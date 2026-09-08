using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Verify;

public sealed record VerifySummary(int Checked, int Matched, int Drifted, int Missing, int Errors)
{
    public override string ToString() => $"{Checked} checked: {Matched} match, {Drifted} drifted, {Missing} missing, {Errors} errors";
}

/// <summary>
/// The drift pass (design.md section 7.6): compares OSDU's current version of each delivered record with the
/// ledger's <c>targetVersion</c>. A mismatch is either drift to correct or a signal that this system is not the
/// authority for the record. With <c>reconcile</c> the drifted records are queued for redelivery on the next
/// submission by clearing their hashes. Every drifted record and the pass itself go through the completion callback.
/// </summary>
public sealed class Verifier
{
    private readonly ILedger _ledger;
    private readonly IDeliveryProtocol _protocol;
    private readonly FlowDefinition _flow;
    private readonly TimeProvider _time;
    private readonly IDeliveryListener _listener;
    private readonly ILogger<Verifier> _logger;

    public Verifier(ILedger ledger, IDeliveryProtocol protocol, FlowDefinition flow, TimeProvider time, IDeliveryListener listener, ILogger<Verifier> logger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(logger);
        _ledger = ledger;
        _protocol = protocol;
        _flow = flow;
        _time = time;
        _listener = listener;
        _logger = logger;
    }

    public async Task<VerifySummary> RunAsync(int max, TimeSpan? notVerifiedWithin, bool reconcile, IReadOnlyList<DeliveryKey>? keys = null, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var before = notVerifiedWithin is { } window ? now - window : (DateTime?)null;
        var records = await _ledger.ListForVerifyAsync(_flow.Id, before, max, ct).ConfigureAwait(false);
        if (keys is { Count: > 0 })
        {
            // A scoped verify (one record from its detail page, a handful from a listing) reads exactly those.
            var scoped = await _ledger.GetRecordsAsync(_flow.Id, keys, ct).ConfigureAwait(false);
            records = scoped.Values.Where(r => r.Status == RecordStatus.Delivered && r.TargetId is not null).ToList();
        }
        var matched = 0;
        var drifted = 0;
        var missing = 0;
        var errors = 0;

        using var gate = new SemaphoreSlim(Math.Max(1, _flow.Reliability.Concurrency));
        var tasks = records.Select(async record =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                VerifyResult result;
                try
                {
                    result = await _protocol.VerifyAsync(record.TargetId!, record.TargetVersion, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is DeliveryException or HttpRequestException)
                {
                    _logger.LogWarning("Verify {SourceKey} failed: {Message}", record.SourceKey, HeaderRedaction.RedactMessage(ex.Message));
                    result = new VerifyResult(VerifyOutcome.Error, null, ex.Message);
                }

                switch (result.Outcome)
                {
                    case VerifyOutcome.Match:
                        Interlocked.Increment(ref matched);
                        break;
                    case VerifyOutcome.Drifted:
                        Interlocked.Increment(ref drifted);
                        _logger.LogWarning("Drift on {SourceKey} ({TargetId}): {Detail}", record.SourceKey, record.TargetId, result.Detail);
                        break;
                    case VerifyOutcome.Missing:
                        Interlocked.Increment(ref missing);
                        _logger.LogWarning("Missing in OSDU: {SourceKey} ({TargetId})", record.SourceKey, record.TargetId);
                        break;
                    default:
                        Interlocked.Increment(ref errors);
                        break;
                }

                if (result.Outcome != VerifyOutcome.Error)
                {
                    await _ledger.RecordVerifyAsync(record.DeliveryKey, result.Outcome, result.ObservedVersion, _time.GetUtcNow().UtcDateTime, reconcile, CancellationToken.None).ConfigureAwait(false);
                }

                if (result.Outcome is VerifyOutcome.Drifted or VerifyOutcome.Missing)
                {
                    await _listener.OnEventAsync(new DeliveryEvent
                    {
                        AtUtc = _time.GetUtcNow().UtcDateTime,
                        FlowId = _flow.Id,
                        FlowName = _flow.Name,
                        Kind = "verify.drifted",
                        SubmissionId = record.LastSubmissionId,
                        DeliveryKey = record.DeliveryKey,
                        SourceKey = record.SourceKey,
                        Label = record.Label,
                        TargetId = record.TargetId,
                        TargetVersion = result.ObservedVersion,
                        Worker = "verify",
                        Detail = (result.Outcome == VerifyOutcome.Missing ? "missing in OSDU" : result.Detail) + (reconcile ? "; redelivery queued" : string.Empty),
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        var summary = new VerifySummary(records.Count, matched, drifted, missing, errors);
        _logger.LogInformation("Verify: {Summary}.", summary);
        await _listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = _time.GetUtcNow().UtcDateTime,
            FlowId = _flow.Id,
            FlowName = _flow.Name,
            Kind = "verify.completed",
            Worker = "verify",
            Detail = summary.ToString() + (reconcile ? " (reconcile on)" : string.Empty),
        }, CancellationToken.None).ConfigureAwait(false);
        return summary;
    }
}
