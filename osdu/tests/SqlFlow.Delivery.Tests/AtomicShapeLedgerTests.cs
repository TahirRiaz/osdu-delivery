using System.Globalization;
using System.Net;
using System.Text;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// One record's artifacts as the ledger keeps them while a route's tries report them (SqlServerLedgerBulk.ArtifactUpsertSql):
/// a row per slot; a slot reported again updates its row while the row is an intent or pending, or one the route settled itself
/// (removed, kept or gone), taking the id, locator and version of the later report where it names one; the version a write
/// replaced stays as first reported, and a slot first reported as the record the unit created is never made a version of it. A
/// row an undo settled is never opened again. The rows still open are what an undo of the unit is given, in the order the unit
/// created them, each as the ledger hands it on (LedgerArtifact.ToItem); an undo's answers settle them. Every report is kept
/// with how many calls the target had seen when it came, so a test can tell what a route named before which call.
/// </summary>
internal sealed class ShapeArtifactLedger
{
    private static long s_nextId;

    private readonly object _gate = new();
    private readonly List<Row> _rows = [];
    private readonly Func<int> _calls;

    public ShapeArtifactLedger(DeliveryUnit unit, Func<int> calls)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(calls);
        Unit = unit;
        _calls = calls;
    }

    /// <summary>The unit every report belongs to.</summary>
    public DeliveryUnit Unit { get; }

    /// <summary>Every report, in order, with how many calls the target had seen when it came.</summary>
    public List<(StepReport Report, int Calls)> Reports { get; } = [];

    /// <summary>The values each step last reported, as the worker hands them to a later try of the same work.</summary>
    public Dictionary<string, IReadOnlyDictionary<string, string>> Completed { get; } = new(StringComparer.Ordinal);

    /// <summary>The rows, in the order the unit created them.</summary>
    public IReadOnlyList<Row> Rows
    {
        get
        {
            lock (_gate)
            {
                return [.. _rows];
            }
        }
    }

    /// <summary>The row of <paramref name="slot"/>.</summary>
    public Row this[string slot] => Assert.Single(Rows, r => r.Slot == slot);

    /// <summary>The report of <paramref name="step"/> that came last.</summary>
    public (StepReport Report, int Calls) Last(string step) => Reports.Last(r => r.Report.Step == step);

    /// <summary>What a route's tries report to, as the worker listens: <see cref="DeliveryWork.StepCompleted"/>.</summary>
    public Task Listen(StepReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            Reports.Add((report, _calls()));
            Completed[report.Step] = report.Returned;

            // One report writes each slot once: the last of its artifacts in a slot is the one the ledger keeps.
            foreach (var artifact in report.Artifacts.GroupBy(a => a.Slot, StringComparer.Ordinal).Select(g => g.Last()))
            {
                Upsert(artifact);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>The rows an undo of the unit takes: every one still open (an intent, pending, due, or one whose undo failed).</summary>
    public IReadOnlyList<UndoItem> Open()
    {
        lock (_gate)
        {
            return _rows.Where(r => ArtifactStatuses.IsOpen(r.State)).OrderBy(r => r.Id).Select(r => r.Item(Unit)).ToList();
        }
    }

    /// <summary>The undo of the unit's open rows for one record.</summary>
    public UndoWork Undo(
        DeliveryKey key, string targetId, UndoReason reason = UndoReason.Failed, bool keepRecord = false,
        IReadOnlyDictionary<string, string>? targetState = null, long? committedVersion = null) => new()
        {
            Key = key,
            TargetId = targetId,
            Reason = reason,
            KeepRecord = keepRecord,
            TargetState = targetState ?? new Dictionary<string, string>(StringComparer.Ordinal),
            CommittedVersion = committedVersion,
            Items = Open(),
        };

    /// <summary>Writes what an undo answered on the rows it answered for, as the ledger settles them.</summary>
    public void Settle(IReadOnlyList<UndoResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        lock (_gate)
        {
            foreach (var result in results)
            {
                if (_rows.FirstOrDefault(r => r.Id == result.Item.ArtifactId) is { } row)
                {
                    row.State = result.Outcome;
                    row.Note = result.Note ?? row.Note;
                    row.SettledByRoute = false;
                }
            }
        }
    }

    private void Upsert(TargetArtifact artifact)
    {
        var row = _rows.FirstOrDefault(r => r.Slot == artifact.Slot);
        if (row is null)
        {
            _rows.Add(new Row
            {
                Id = Interlocked.Increment(ref s_nextId),
                Slot = artifact.Slot,
                Role = artifact.Role,
                TargetId = artifact.TargetId,
                Locator = artifact.Locator,
                Version = artifact.Version,
                PriorVersion = artifact.PriorVersion,
                State = artifact.Status,
                Note = artifact.Note,
                SettledByRoute = SettledByItself(artifact.Status),
            });
            return;
        }

        // A row an undo settled is not opened again; one the route settled itself takes what the route reports of it next.
        if (row.State is not (ArtifactStatus.Intent or ArtifactStatus.Pending) && !(SettledByItself(row.State) && row.SettledByRoute))
        {
            return;
        }

        var keepsRecord = row.Role == ArtifactRoles.Record && artifact.Role == ArtifactRoles.Version;
        row.PriorVersion = keepsRecord ? row.PriorVersion : row.PriorVersion ?? artifact.PriorVersion;
        row.Role = keepsRecord ? row.Role : artifact.Role;
        row.TargetId = artifact.TargetId ?? row.TargetId;
        row.Locator = artifact.Locator ?? row.Locator;
        row.Version = artifact.Version ?? row.Version;
        row.State = artifact.Status;
        row.Note = artifact.Note ?? row.Note;
        row.SettledByRoute = SettledByItself(artifact.Status);
    }

    /// <summary>The states a route reports an artifact in when it settled the artifact itself.</summary>
    private static bool SettledByItself(ArtifactStatus status) => status is ArtifactStatus.Removed or ArtifactStatus.Kept or ArtifactStatus.Gone;

    /// <summary>One row of the ledger's artifacts.</summary>
    internal sealed class Row
    {
        public required long Id { get; init; }

        public required string Slot { get; init; }

        public required string Role { get; set; }

        public string? TargetId { get; set; }

        public string? Locator { get; set; }

        public long? Version { get; set; }

        public long? PriorVersion { get; set; }

        public ArtifactStatus State { get; set; }

        public string? Note { get; set; }

        /// <summary>Whether the route settled the row itself (the ledger's <c>SettledBy</c> 'route'), rather than an undo.</summary>
        public bool SettledByRoute { get; set; }

        /// <summary>The row as an undo takes it: an intent stays one, anything else open is handed on as pending.</summary>
        public UndoItem Item(DeliveryUnit unit) => new(
            Id,
            new TargetArtifact
            {
                Slot = Slot,
                Role = Role,
                TargetId = TargetId,
                Locator = Locator,
                Version = Version,
                PriorVersion = PriorVersion,
                Status = State == ArtifactStatus.Intent ? ArtifactStatus.Intent : ArtifactStatus.Pending,
                Note = Note,
            },
            unit.Id,
            unit.StartedUtc);
    }
}

/// <summary>Checks on what an undo answered.</summary>
internal static class UndoAnswers
{
    /// <summary>Checks that the undo answered every artifact it was given exactly once, and nothing it was not given.</summary>
    public static void EachOnce(IReadOnlyList<UndoWork> works, IReadOnlyList<UndoResult> results)
    {
        ArgumentNullException.ThrowIfNull(works);
        ArgumentNullException.ThrowIfNull(results);
        Assert.Equal(
            works.SelectMany(w => w.Items).Select(i => i.ArtifactId).Order(),
            results.Select(r => r.Item.ArtifactId).Order());
        Assert.All(results, r => Assert.True(ArtifactStatuses.IsUndoOutcome(r.Outcome), $"{r.Item.Artifact.Slot} was answered {r.Outcome}"));
    }

    /// <summary>What the undo answered for the artifact in <paramref name="slot"/>.</summary>
    public static UndoResult Of(IReadOnlyList<UndoResult> results, string slot) => Assert.Single(results, r => r.Item.Artifact.Slot == slot);
}

/// <summary>
/// Sits in front of a fake platform and changes what chosen calls do: answers one without the platform seeing it, loses the
/// answer of one the platform acted on (the call lands, and the caller sees the connection drop), rewrites what the platform
/// answered, or acts on the platform just before a call reaches it. Every request that comes through is kept, so a test can
/// order a route's reports against its calls.
/// </summary>
internal sealed class ShapeCallHook : DelegatingHandler
{
    private readonly object _gate = new();
    private readonly List<Rule> _rules = [];

    public ShapeCallHook(HttpMessageHandler platform)
    {
        InnerHandler = platform;
    }

    /// <summary>Every request that came through, as its method and unescaped path and query, in order.</summary>
    public List<string> Seen { get; } = [];

    /// <summary>How many requests came through.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return Seen.Count;
            }
        }
    }

    /// <summary>The requests that came through from <paramref name="from"/> on.</summary>
    public List<string> Since(int from)
    {
        lock (_gate)
        {
            return Seen.Skip(from).ToList();
        }
    }

    /// <summary>A request of <paramref name="method"/> whose unescaped path holds <paramref name="path"/>.</summary>
    public static Func<HttpRequestMessage, bool> Call(HttpMethod method, string path)
        => r => r.Method == method && Uri.UnescapeDataString(r.RequestUri!.AbsolutePath).Contains(path, StringComparison.Ordinal);

    /// <summary>A request of <paramref name="method"/> whose unescaped path and query hold <paramref name="text"/>.</summary>
    public static Func<HttpRequestMessage, bool> CallWith(HttpMethod method, string text)
        => r => r.Method == method && Uri.UnescapeDataString(r.RequestUri!.PathAndQuery).Contains(text, StringComparison.Ordinal);

    /// <summary>The <paramref name="n"/>th request <paramref name="match"/> takes, counted from 1, and no other.</summary>
    public static Func<HttpRequestMessage, bool> Nth(Func<HttpRequestMessage, bool> match, int n)
    {
        ArgumentNullException.ThrowIfNull(match);
        var seen = 0;
        return r => match(r) && Interlocked.Increment(ref seen) == n;
    }

    /// <summary>Answers the next <paramref name="times"/> requests <paramref name="match"/> takes with <paramref name="status"/>; the platform never sees them.</summary>
    public ShapeCallHook Answer(Func<HttpRequestMessage, bool> match, HttpStatusCode status, string? body = null, int times = 1)
        => Add(new Rule(match, times) { Status = status, Body = body });

    /// <summary>Sends the next <paramref name="times"/> requests <paramref name="match"/> takes on to the platform, and loses the answer.</summary>
    public ShapeCallHook LoseAnswer(Func<HttpRequestMessage, bool> match, int times = 1)
        => Add(new Rule(match, times) { Lose = true });

    /// <summary>Replaces what the platform answered the next <paramref name="times"/> requests <paramref name="match"/> takes with <paramref name="status"/> and <paramref name="body"/>.</summary>
    public ShapeCallHook Rewrite(Func<HttpRequestMessage, bool> match, HttpStatusCode status, string body, int times = 1)
        => Add(new Rule(match, times) { Status = status, Body = body, AfterPlatform = true });

    /// <summary>Runs <paramref name="action"/> just before the next <paramref name="times"/> requests <paramref name="match"/> takes reach the platform.</summary>
    public ShapeCallHook Before(Func<HttpRequestMessage, bool> match, Action action, int times = 1)
        => Add(new Rule(match, times) { Action = action });

    /// <summary>Drops every rule not yet used up, so the calls that follow reach the platform as they are.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _rules.Clear();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Rule? rule;
        lock (_gate)
        {
            Seen.Add(request.Method.Method + " " + Uri.UnescapeDataString(request.RequestUri!.PathAndQuery));
            rule = _rules.FirstOrDefault(r => r.Left > 0 && r.Match(request));
            if (rule is not null)
            {
                rule.Left--;
            }
        }

        if (rule is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        rule.Action?.Invoke();
        if (rule.Status is { } status && !rule.AfterPlatform)
        {
            return Respond(status, rule.Body);
        }

        var answered = await base.SendAsync(request, cancellationToken);
        if (rule.Lose)
        {
            answered.Dispose();
            throw new HttpRequestException("The connection closed before the answer came (a test lost it after the call landed).");
        }

        if (rule.AfterPlatform && rule.Status is { } replaced)
        {
            answered.Dispose();
            return Respond(replaced, rule.Body);
        }

        return answered;
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string? body) => new(status)
    {
        Content = new StringContent(body ?? string.Create(CultureInfo.InvariantCulture, $"{{\"code\":{(int)status},\"message\":\"a test answered the call\"}}"), Encoding.UTF8, "application/json"),
    };

    private ShapeCallHook Add(Rule rule)
    {
        lock (_gate)
        {
            _rules.Add(rule);
        }

        return this;
    }

    private sealed class Rule(Func<HttpRequestMessage, bool> match, int times)
    {
        public Func<HttpRequestMessage, bool> Match { get; } = match;

        public int Left { get; set; } = times;

        public HttpStatusCode? Status { get; init; }

        public string? Body { get; init; }

        public bool Lose { get; init; }

        public bool AfterPlatform { get; init; }

        public Action? Action { get; init; }
    }
}

/// <summary>
/// The emulation of the ledger the shape suites build their undos from (<see cref="ShapeArtifactLedger"/>) keeps the rules of
/// the ledger's upsert of a slot reported again, so what the suites hand an undo is what the worker would.
/// </summary>
public sealed class AtomicShapeLedgerTests
{
    private static readonly DeliveryUnit Unit = new(Guid.Parse("0192aa00-0000-7000-8000-00000000a7e1"), new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc));

    private static StepReport Report(string step, params TargetArtifact[] artifacts)
        => new(step, new Dictionary<string, string>(StringComparer.Ordinal) { ["step"] = step }, artifacts);

    [Fact]
    public async Task A_slot_first_reported_as_the_record_the_unit_created_stays_so_and_keeps_its_first_prior_version()
    {
        var calls = 0;
        var ledger = new ShapeArtifactLedger(Unit, () => calls);
        await ledger.Listen(Report("metadata", TargetArtifact.RecordWritten("dev:a:1", 7, null)), default);
        calls = 3;
        await ledger.Listen(Report("metadata", TargetArtifact.RecordWritten("dev:a:1", 8, 7)), default);

        var row = ledger[TargetArtifact.RecordSlot];
        Assert.Equal((ArtifactRoles.Record, 8L, (long?)null), (row.Role, row.Version!.Value, row.PriorVersion));
        Assert.Equal([0, 3], ledger.Reports.Select(r => r.Calls));

        // A version reported again keeps the version its first report replaced.
        await ledger.Listen(Report("other", TargetArtifact.RecordWritten("dev:a:2", 10, 9) with { Slot = "v" }), default);
        await ledger.Listen(Report("other", TargetArtifact.RecordWritten("dev:a:2", 11, 10) with { Slot = "v" }), default);
        Assert.Equal((ArtifactRoles.Version, 11L, 9L), (ledger["v"].Role, ledger["v"].Version!.Value, ledger["v"].PriorVersion!.Value));
    }

    [Fact]
    public async Task An_intent_completed_by_its_id_is_one_row_and_a_row_the_route_settled_opens_again_when_it_reports_it_again()
    {
        var ledger = new ShapeArtifactLedger(Unit, () => 0);
        await ledger.Listen(Report("a", TargetArtifact.Intent("x", ArtifactRoles.Rows, "t|p")), default);
        await ledger.Listen(Report("b", TargetArtifact.Created("x", ArtifactRoles.Rows, "id-1")), default);
        var row = Assert.Single(ledger.Rows);
        Assert.Equal((ArtifactStatus.Pending, "id-1", "t|p"), (row.State, row.TargetId, row.Locator));

        // A lock released by a close and taken again by a later try of the unit is the unit's again.
        await ledger.Listen(Report("c", TargetArtifact.Created("x", ArtifactRoles.Rows, "id-1") with { Status = ArtifactStatus.Removed, Note = "released" }), default);
        Assert.Empty(ledger.Open());
        await ledger.Listen(Report("d", TargetArtifact.Intent("x", ArtifactRoles.Rows, "t|q")), default);
        Assert.Equal((ArtifactStatus.Intent, "id-1", "t|q", "released"), (ledger["x"].State, ledger["x"].TargetId, ledger["x"].Locator, ledger["x"].Note));

        // The route may settle a slot as kept or gone itself, and that too opens again.
        await ledger.Listen(Report("e", TargetArtifact.Created("x", ArtifactRoles.Rows, "id-1") with { Status = ArtifactStatus.Gone, Note = "not the unit's" }), default);
        Assert.Empty(ledger.Open());
        await ledger.Listen(Report("f", TargetArtifact.Created("x", ArtifactRoles.Rows, "id-2")), default);
        Assert.Equal((ArtifactStatus.Pending, "id-2"), (ledger["x"].State, ledger["x"].TargetId));
    }

    [Fact]
    public async Task A_row_an_undo_settled_is_never_opened_again()
    {
        var ledger = new ShapeArtifactLedger(Unit, () => 0);
        await ledger.Listen(Report("a", TargetArtifact.Created("x", ArtifactRoles.Lock, "sd://a", locator: "W1")), default);
        var item = Assert.Single(ledger.Open());
        ledger.Settle([UndoResult.Gone(item, "lapsed")]);

        await ledger.Listen(Report("b", TargetArtifact.Created("x", ArtifactRoles.Lock, "sd://a", locator: "W2")), default);
        Assert.Equal((ArtifactStatus.Gone, "W1", "lapsed"), (ledger["x"].State, ledger["x"].Locator, ledger["x"].Note));
        Assert.Empty(ledger.Open());
    }

    [Fact]
    public async Task An_undo_takes_the_open_rows_in_the_order_the_unit_made_them_and_its_answers_settle_them()
    {
        var ledger = new ShapeArtifactLedger(Unit, () => 0);
        await ledger.Listen(Report("a", TargetArtifact.Created("one", ArtifactRoles.Lock, "sd://a"), TargetArtifact.Intent("two", ArtifactRoles.Rows, "t|p")), default);
        await ledger.Listen(Report("b", TargetArtifact.Created("three", ArtifactRoles.Points, "dev:p:1")), default);
        var work = ledger.Undo(DeliveryKey.Derive("ledger", ["a"]), "dev:a:1");

        Assert.Equal(["one", "two", "three"], work.Items.Select(i => i.Artifact.Slot));
        Assert.Equal([ArtifactStatus.Pending, ArtifactStatus.Intent, ArtifactStatus.Pending], work.Items.Select(i => i.Artifact.Status));
        Assert.All(work.Items, i => Assert.Equal((Unit.Id, Unit.StartedUtc), (i.UnitId, i.UnitStartedUtc)));

        ledger.Settle([UndoResult.Removed(work.Items[0]), UndoResult.Failed(work.Items[1], "refused"), UndoResult.Kept(work.Items[2], "no delete")]);
        Assert.Equal(["two"], ledger.Open().Select(i => i.Artifact.Slot));
        Assert.Equal("refused", ledger["two"].Note);
    }
}
