using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine;

/// <summary>What <see cref="RunTrace.Admit"/> decided about one line: whether it is written, and a note to write once beside it.</summary>
/// <param name="Write">Whether the line goes on the trace.</param>
/// <param name="Note">Said once, when an allowance runs out, so the trace shows where its lines of that sort stop.</param>
public readonly record struct TraceAdmission(bool Write, string? Note = null);

/// <summary>
/// What one run's live trace may carry. A delivery run moves millions of records, and every line of its trace is streamed
/// to the GUI, kept in the run's events and stored in the catalog, so what a run writes is bounded by these allowances and
/// never grows with the run's size:
/// <list type="bullet">
/// <item>the first <see cref="RecordAllowance"/> records a run sends or holds are described in full (the plan line, the
/// send, each step and call, the outcome); nothing is said about any other record on its own;</item>
/// <item>each step (<c>source</c>, <c>intake</c>, <c>deliver</c>, <c>http</c>, ...) writes at most
/// <see cref="StepAllowance"/> lines of its own outside the described records, then says once that it stops;</item>
/// <item>the run names at most <see cref="ProblemAllowance"/> problems (warnings), then counts the rest;</item>
/// <item>a retried call is named <see cref="RetryAllowance"/> times at most;</item>
/// <item>progress is said on a pace that slows as the run goes on (<see cref="ProgressDue"/>), from run-level totals,
/// never per batch.</item>
/// </list>
/// Errors are always written. Whatever the trace leaves out, every record's history is in the ledger.
/// </summary>
/// <remarks>
/// One per run, shared by everything the run does (its interfaces, its intake and its workers), and thread-safe, since
/// they report from parallel renderers and concurrent deliveries. Whether what is said and sent belongs to a record, and
/// whether the trace describes it, is carried on the flow of execution (<see cref="AboutRecords"/>), because the protocols
/// and the HTTP stack know nothing of the trace. The run's gate (<c>RunTraceGate</c>) applies <see cref="Admit"/> to every
/// line the engine logs while it serves the run.
/// </remarks>
public sealed class RunTrace
{
    /// <summary>The records whose every line the trace carries.</summary>
    public const int RecordsDescribed = 20;

    /// <summary>The lines each step writes outside the described records.</summary>
    public const int LinesPerStep = 100;

    /// <summary>The problems (warnings) a run names.</summary>
    public const int ProblemsDescribed = 100;

    /// <summary>The retries of a call the trace names.</summary>
    public const int RetriesDescribed = 50;

    /// <summary>The step the run's own lines are filed under: what it resolved, how it delivers, how it ended.</summary>
    public const string RunStep = "run";

    /// <summary>How often progress is said for the first two minutes of a run; later it is said less often.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Marks a line whose writer bounds how many it writes itself (a paced progress line, the first entries of a plan, a
    /// retry within its allowance): the trace takes it as it is.
    /// </summary>
    public static readonly EventId Bounded = new(7101, "bounded");

    /// <summary>Whether what is said and sent on this flow of execution is about records the trace describes; null outside any record.</summary>
    private static readonly AsyncLocal<bool?> AboutDescribed = new();

    private readonly TimeProvider _time;
    private readonly DateTimeOffset _started;
    private readonly Lock _gate = new();
    private readonly HashSet<DeliveryKey> _described = [];
    private readonly Dictionary<string, long> _stepLines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _progress = new(StringComparer.Ordinal);
    private bool _recordsLeftOut;
    private long _problems;
    private long _retries;
    private long _planned;
    private long _delivered;
    private long _unchanged;
    private long _retrying;
    private long _held;
    private long _failed;
    private DateTimeOffset? _firstSettled;

    /// <summary>A run's trace with the allowances every run has; a test names smaller ones to see where they stop.</summary>
    public RunTrace(
        TimeProvider? time = null, int recordsDescribed = RecordsDescribed, int linesPerStep = LinesPerStep, int problemsDescribed = ProblemsDescribed,
        int retriesDescribed = RetriesDescribed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recordsDescribed);
        ArgumentOutOfRangeException.ThrowIfNegative(linesPerStep);
        ArgumentOutOfRangeException.ThrowIfNegative(problemsDescribed);
        ArgumentOutOfRangeException.ThrowIfNegative(retriesDescribed);
        _time = time ?? TimeProvider.System;
        _started = _time.GetUtcNow();
        RecordAllowance = recordsDescribed;
        StepAllowance = linesPerStep;
        ProblemAllowance = problemsDescribed;
        RetryAllowance = retriesDescribed;
    }

    /// <summary>How many records this trace describes.</summary>
    public int RecordAllowance { get; }

    /// <summary>How many lines each step writes outside the described records.</summary>
    public int StepAllowance { get; }

    /// <summary>How many problems this trace names.</summary>
    public int ProblemAllowance { get; }

    /// <summary>How many retries of a call this trace names.</summary>
    public int RetryAllowance { get; }

    /// <summary>
    /// Whether the trace describes the record: the first <see cref="RecordAllowance"/> records a run meets are, each time it
    /// meets them again. Whoever meets the first record left out says so on <paramref name="log"/>, once for the run, so the
    /// trace shows where its record lines stop whichever part of the run gets there first.
    /// </summary>
    public bool Describes(DeliveryKey key, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (Describes(key, out var firstLeftOut))
        {
            return true;
        }

        if (firstLeftOut)
        {
            log.LogInformation(
                Bounded,
                "The first {Count} records of this run are on its trace in full; the rest show in the progress lines, and every record's history is in the ledger.",
                RecordAllowance);
        }

        return false;
    }

    /// <summary>
    /// Whether the trace describes the record; <paramref name="firstLeftOut"/> is true for the first record it does not, so
    /// the trace can say once where its record lines stop.
    /// </summary>
    public bool Describes(DeliveryKey key, out bool firstLeftOut)
    {
        lock (_gate)
        {
            firstLeftOut = false;
            if (_described.Contains(key))
            {
                return true;
            }

            if (_described.Count < RecordAllowance)
            {
                _described.Add(key);
                return true;
            }

            firstLeftOut = !_recordsLeftOut;
            _recordsLeftOut = true;
            return false;
        }
    }

    /// <summary>Whether the trace names a retry; <paramref name="firstLeftOut"/> is true for the first it does not.</summary>
    public bool DescribesRetry(out bool firstLeftOut) => Take(ref _retries, RetryAllowance, out firstLeftOut);

    /// <summary>
    /// Whether a line a step logs goes on the trace. A line its writer bounds (<see cref="Bounded"/>) and an error always
    /// do, and so does everything said about records the trace describes. About any other record, only a problem does,
    /// within the run's problem allowance. Outside any record a problem takes from that allowance too, and anything else
    /// from its step's; the run's own warnings (<see cref="RunStep"/>) take from its step's as well, so no number of
    /// record problems crowds them out.
    /// </summary>
    public TraceAdmission Admit(string step, LogLevel level, EventId eventId)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (eventId.Id == Bounded.Id || level >= LogLevel.Error)
        {
            return new TraceAdmission(true);
        }

        var about = AboutDescribed.Value;
        if (about == true)
        {
            return new TraceAdmission(true);
        }

        // The run's own warnings (a submission left open, a lease that could not be reclaimed, a member that failed) are
        // few by nature and never crowded out by the records' problems: they take from the run step's lines instead.
        if (level == LogLevel.Warning && step != RunStep)
        {
            var written = Take(ref _problems, ProblemAllowance, out var firstLeftOut);
            return new TraceAdmission(written, firstLeftOut
                ? string.Create(CultureInfo.InvariantCulture, $"The first {ProblemAllowance} problems of this run are on its trace; the rest are counted in its progress lines and totals, and each record's history says why.")
                : null);
        }

        if (about == false)
        {
            // What a record the trace does not describe goes through is in its history, not on the trace.
            return new TraceAdmission(false);
        }

        lock (_gate)
        {
            var lines = _stepLines.GetValueOrDefault(step) + 1;
            _stepLines[step] = lines;
            if (lines <= StepAllowance)
            {
                return new TraceAdmission(true);
            }

            return new TraceAdmission(false, lines == StepAllowance + 1
                ? string.Create(CultureInfo.InvariantCulture, $"Step {step} has written its {StepAllowance} lines to the trace; its later lines are left out, the progress lines keep counting, and every record's history is in the ledger.")
                : null);
        }
    }

    /// <summary>
    /// Marks what is said and sent on this flow of execution, until the scope is disposed, as about records the trace
    /// describes (<paramref name="described"/>) or about records it does not.
    /// </summary>
    public static RecordScope AboutRecords(bool described) => new(described);

    /// <summary>
    /// Whether it is time to say how far <paramref name="what"/> (<c>intake</c>, <c>deliver</c>, <c>verify</c>, ...) has got:
    /// every <see cref="ProgressInterval"/> for the first two minutes of the run, every minute up to an hour, every five
    /// minutes after that, so a run of any length says so a bounded number of times. The first ask starts the clock.
    /// </summary>
    public bool ProgressDue(string what, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        var elapsed = now - _started;
        var every = elapsed < TimeSpan.FromMinutes(2) ? ProgressInterval
            : elapsed < TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(1)
            : TimeSpan.FromMinutes(5);
        lock (_gate)
        {
            if (!_progress.TryGetValue(what, out var last))
            {
                _progress[what] = now;
                return false;
            }

            if (now - last < every)
            {
                return false;
            }

            _progress[what] = now;
            return true;
        }
    }

    /// <summary>Adds records a submission of this run planned to deliver, which the progress lines count against.</summary>
    public void AddPlanned(long records)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(records);
        Interlocked.Add(ref _planned, records);
    }

    /// <summary>Counts one record's try as it settled, for the run's progress lines.</summary>
    public void Settled(RecordStatus status, bool nothingSent)
    {
        lock (_gate)
        {
            _firstSettled ??= _time.GetUtcNow();
        }

        switch (status)
        {
            case RecordStatus.Delivered when nothingSent:
                Interlocked.Increment(ref _unchanged);
                break;
            case RecordStatus.Delivered:
                Interlocked.Increment(ref _delivered);
                break;
            case RecordStatus.Pending:
                Interlocked.Increment(ref _retrying);
                break;
            case RecordStatus.Held:
                Interlocked.Increment(ref _held);
                break;
            default:
                Interlocked.Increment(ref _failed);
                break;
        }
    }

    /// <summary>How far the run's deliveries have got, as one progress line says it.</summary>
    public string DeliveryProgress(DateTimeOffset now)
    {
        var delivered = Interlocked.Read(ref _delivered);
        var unchanged = Interlocked.Read(ref _unchanged);
        var retrying = Interlocked.Read(ref _retrying);
        var held = Interlocked.Read(ref _held);
        var failed = Interlocked.Read(ref _failed);
        var planned = Interlocked.Read(ref _planned);
        var settled = delivered + unchanged + retrying + held + failed;
        DateTimeOffset? first;
        lock (_gate)
        {
            first = _firstSettled;
        }

        var sending = first is { } since && now > since ? now - since : TimeSpan.Zero;
        var rate = sending.TotalSeconds >= 1 ? settled / sending.TotalSeconds : 0d;
        var of = planned > 0 ? string.Create(CultureInfo.InvariantCulture, $" of {planned:N0} planned") : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{settled:N0}{of} record(s) settled after {Elapsed(now - _started)} ({rate:N1} a second): {delivered:N0} delivered, {unchanged:N0} already in OSDU, {retrying:N0} to try again, {held:N0} held, {failed:N0} failed");
    }

    /// <summary>How long something took, as the trace says it: milliseconds under a second, seconds under two minutes, minutes above.</summary>
    public static string Elapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromSeconds(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (long)elapsed.TotalMilliseconds)} ms");
        }

        return elapsed < TimeSpan.FromMinutes(2)
            ? string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0.#} s")
            : string.Create(CultureInfo.InvariantCulture, $"{(long)elapsed.TotalMinutes} min {elapsed.Seconds} s");
    }

    /// <summary>How a record reads on the trace: its source key, and its label when it has one, as a plan names it.</summary>
    public static string Record(string? sourceKey, string? label, DeliveryKey key)
    {
        var name = string.IsNullOrWhiteSpace(sourceKey) ? key.ToString() : sourceKey;
        return string.IsNullOrWhiteSpace(label) ? name : $"{name} [{label}]";
    }

    private static bool Take(ref long taken, int allowance, out bool firstLeftOut)
    {
        var n = Interlocked.Increment(ref taken);
        firstLeftOut = n == allowance + 1;
        return n <= allowance;
    }

    /// <summary>What a flow of execution is about, for as long as the scope lasts; disposing it restores what it was about before.</summary>
    public readonly struct RecordScope : IDisposable
    {
        private readonly bool? _previous;

        internal RecordScope(bool described)
        {
            _previous = AboutDescribed.Value;
            AboutDescribed.Value = described;
        }

        public void Dispose() => AboutDescribed.Value = _previous;
    }
}
