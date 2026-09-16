using System.Globalization;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Worker;

/// <summary>What a record's failure points at: its own data, the way to the service, or the caller's rights.</summary>
public enum FailureClass
{
    /// <summary>Something about the record (a refusal of its document, a missing file, a problem rendering it).</summary>
    Data,

    /// <summary>The service could not be reached or did not answer: a transport failure, a timeout, a 5xx, throttling.</summary>
    Connection,

    /// <summary>The service refused the caller: 401 or 403.</summary>
    Permission,
}

/// <summary>
/// Watches the outcomes of one interface's records during a run and says when they add up to a failure of the interface
/// itself (docs/interfaces-design.md section 8.2): the connection or permission failure of record after record with none
/// delivered in between (an outage), <see cref="FlowFailWhen.ConsecutiveFailures"/> failures of one class in a row, or a
/// share of held and failed records at or above <see cref="FlowFailWhen.FailedPercent"/> once enough records settled.
/// When it trips, <see cref="Token"/> is cancelled: the work under it stops and hands its leases back without charging
/// the interrupted tries, and <see cref="Reason"/> says why. A record failing never trips it on its own. Thread-safe: every
/// concurrent delivery of the interface reports here.
/// </summary>
public sealed class FailureGuard : IDisposable
{
    private readonly FlowFailWhen _rules;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _tripped = new();
    private readonly int[] _consecutive = new int[Enum.GetValues<FailureClass>().Length];
    private long _settled;
    private long _failed;
    private string? _reason;

    public FailureGuard(FlowFailWhen rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
    }

    /// <summary>Cancelled when the guard trips.</summary>
    public CancellationToken Token => _tripped.Token;

    /// <summary>Why the guard tripped, or null while it has not.</summary>
    public string? Reason
    {
        get
        {
            lock (_gate)
            {
                return _reason;
            }
        }
    }

    public bool Tripped => Reason is not null;

    /// <summary>What a failure points at, read through the exceptions it wraps.</summary>
    public static FailureClass Classify(Exception? failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case OsduStatusException { StatusCode: 401 or 403 }:
                    return FailureClass.Permission;
                case OsduStatusException { StatusCode: >= 500 or 408 or 429 }:
                    return FailureClass.Connection;
                case OsduStatusException:
                    return FailureClass.Data;
                case HttpRequestException or IOException or TimeoutException or TaskCanceledException:
                    return FailureClass.Connection;
            }
        }

        return FailureClass.Data;
    }

    /// <summary>Records that settled delivered, or found already delivered: every run of failures ends.</summary>
    public void Succeeded(long count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return;
        }

        lock (_gate)
        {
            _settled += count;
            Array.Clear(_consecutive);
        }
    }

    /// <summary>
    /// A record failed for a reason of <paramref name="failure"/>'s class: <paramref name="settled"/> when it was held or
    /// failed for good, not when it was scheduled for another try. <paramref name="detail"/> is the failure as the ledger
    /// records it, already redacted, and ends up in <see cref="Reason"/> when this failure trips the guard.
    /// </summary>
    public void Failed(FailureClass failure, bool settled, string? detail)
    {
        string? tripped;
        lock (_gate)
        {
            if (settled)
            {
                _settled++;
                _failed++;
            }

            var inARow = ++_consecutive[(int)failure];
            tripped = _reason is null ? Judge(failure, inARow, detail) : null;
            _reason ??= tripped;
        }

        if (tripped is not null)
        {
            _tripped.Cancel();
        }
    }

    /// <summary>
    /// What the planning did before anything was sent: <paramref name="held"/> records it could not render, of the
    /// <paramref name="held"/> plus <paramref name="planned"/> it took up. Delivering the planned records cannot bring the
    /// held share down, so a share at or above <see cref="FlowFailWhen.FailedPercent"/> stops the interface before it sends
    /// anything. The records it planned are judged again as they are delivered.
    /// </summary>
    public void Planned(long held, long planned)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(held);
        ArgumentOutOfRangeException.ThrowIfNegative(planned);
        string? tripped = null;
        lock (_gate)
        {
            if (_reason is null)
            {
                tripped = Share(held, held + planned, "records the run planned were held before anything was sent", null);
                _reason = tripped;
            }
        }

        if (tripped is not null)
        {
            _tripped.Cancel();
        }
    }

    public void Dispose() => _tripped.Dispose();

    private string? Judge(FailureClass failure, int inARow, string? detail)
    {
        var last = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"; the last: {detail}";
        if (failure is FailureClass.Connection or FailureClass.Permission && _rules.OutageFailures > 0 && inARow >= _rules.OutageFailures)
        {
            var what = failure == FailureClass.Connection ? "could not reach the service" : "were refused by the service (401 or 403)";
            return string.Create(CultureInfo.InvariantCulture, $"an outage: {inARow} records in a row {what} with none delivered in between (failWhen.outageFailures {_rules.OutageFailures}){last}");
        }

        if (_rules.ConsecutiveFailures is { } limit && inARow >= limit)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{inARow} records in a row failed with {failure.ToString().ToLowerInvariant()} problems and none delivered in between (failWhen.consecutiveFailures {limit}){last}");
        }

        return Share(_failed, _settled, "records the run delivered so far were held or failed", detail);
    }

    /// <summary>Why <paramref name="failed"/> of <paramref name="total"/> stops the interface, or null when it does not.</summary>
    private string? Share(long failed, long total, string what, string? detail)
    {
        if (_rules.FailedPercent is not { } percent || total == 0 || total < _rules.MinRecords)
        {
            return null;
        }

        var share = failed * 100.0 / total;
        if (share < percent)
        {
            return null;
        }

        var last = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"; the last: {detail}";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{failed} of the {total} {what} ({share:0.#}%), at or above failWhen.failedPercent {percent:0.#} (judged once failWhen.minRecords {_rules.MinRecords} records settled){last}");
    }
}
