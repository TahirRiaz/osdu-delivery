using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Worker;

public sealed record WorkerSummary(int Processed, int Delivered, int Retried, int Held, int Failed)
{
    public static WorkerSummary Empty { get; } = new(0, 0, 0, 0, 0);

    public WorkerSummary Add(WorkerSummary other) => new(
        Processed + other.Processed, Delivered + other.Delivered, Retried + other.Retried, Held + other.Held, Failed + other.Failed);
}

/// <summary>
/// The record-grained lease-and-retry worker (design.md section 7.5): the fan-out. Claims a batch of pending
/// records with a lease (any number of nodes share the ledger safely), processes them with bounded concurrency,
/// renews leases while a long payload streams, writes one append-only attempt per record, and fires the completion
/// callback (<see cref="IDeliveryListener"/>) after every outcome. A crashed worker's leases expire and the records
/// are reclaimed by the next claim; a stopping worker releases them at once.
/// </summary>
public sealed class DeliveryWorker
{
    private readonly ILedger _ledger;
    private readonly IDropReader _drops;
    private readonly IDeliveryProtocol _protocol;
    private readonly FlowDefinition _flow;
    private readonly TimeProvider _time;
    private readonly IDeliveryListener _listener;
    private readonly ILogger<DeliveryWorker> _logger;
    private readonly string _workerId;

    /// <summary>The platform run the worker drains for, stamped on every attempt it writes.</summary>
    public Guid? RunId { get; init; }

    public DeliveryWorker(
        ILedger ledger,
        IDropReader drops,
        IDeliveryProtocol protocol,
        FlowDefinition flow,
        TimeProvider time,
        IDeliveryListener listener,
        ILogger<DeliveryWorker> logger,
        string? workerId = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(drops);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(logger);
        _ledger = ledger;
        _drops = drops;
        _protocol = protocol;
        _flow = flow;
        _time = time;
        _listener = listener;
        _logger = logger;
        _workerId = workerId ?? $"{Environment.MachineName}/{Environment.ProcessId}";
    }

    /// <summary>Processes until no record of the submission (or flow) is pending and due. Records backing off are left for later.</summary>
    public async Task<WorkerSummary> DrainAsync(Guid? submissionId, CancellationToken ct = default)
    {
        var total = WorkerSummary.Empty;
        while (!ct.IsCancellationRequested)
        {
            var summary = await PassAsync(submissionId, ct).ConfigureAwait(false);
            total = total.Add(summary);
            if (summary.Processed == 0)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>One claim batch, processed with the flow's concurrency.</summary>
    public async Task<WorkerSummary> PassAsync(Guid? submissionId, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var lease = TimeSpan.FromSeconds(Math.Max(_flow.Reliability.LeaseSeconds, 30));
        var claimed = await _ledger.ClaimAsync(_flow.Id, submissionId, _workerId, _flow.Reliability.BatchSize, lease, now, ct).ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            return WorkerSummary.Empty;
        }

        _logger.LogInformation("Claimed {Count} record(s) for {Lease}s.", claimed.Count, lease.TotalSeconds);
        var results = new WorkerSummary[claimed.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, _flow.Reliability.Concurrency));
        var tasks = claimed.Select(async (record, i) =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Claimed but never started: hand it back so the next pass does not wait for the lease to expire.
                await ReleaseAsync(record, record.LeaseOwner ?? _workerId).ConfigureAwait(false);
                throw;
            }

            try
            {
                results[i] = await ProcessAsync(record, lease, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Aggregate(WorkerSummary.Empty, (acc, r) => acc.Add(r));
    }

    private async Task<WorkerSummary> ProcessAsync(RecordState record, TimeSpan lease, CancellationToken ct)
    {
        var started = _time.GetUtcNow().UtcDateTime;
        var owner = record.LeaseOwner ?? _workerId;
        using var renewals = new CancellationTokenSource();
        var renewTask = RenewLoopAsync(record.DeliveryKey, owner, lease, renewals.Token);

        try
        {
            if (record.PendingDocument is null || record.TargetId is null)
            {
                return await CompleteAsync(record, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null, "no pending document on the record; re-submit the drop", ct).ConfigureAwait(false);
            }

            var document = JsonNode.Parse(record.PendingDocument) as JsonObject
                ?? throw new DeliveryException("the pending document is not a JSON object");
            var work = new DeliveryWork
            {
                TargetId = record.TargetId,
                Document = document,
                DeliverMetadata = record.PendingMetadata,
                DeliverPayload = record.PendingPayload,
                Payload = record.PendingPayloadLocation is { } location ? new DropPayloadSource(_drops, location) : null,
                ExistingVersion = record.TargetVersion,
            };

            var outcome = await _protocol.DeliverAsync(work, ct).ConfigureAwait(false);
            var phase = (outcome.MetadataDelivered, outcome.PayloadDelivered) switch
            {
                (true, true) => "metadata+payload",
                (true, false) => "metadata",
                (false, true) => "payload",
                _ => "none",
            };
            _logger.LogInformation("Delivered {SourceKey} ({Phase}) as {TargetId} version {Version}.", record.SourceKey, phase, record.TargetId, outcome.TargetVersion);
            return await CompleteAsync(record, started, RecordStatus.Delivered, AttemptOutcome.Delivered, phase, outcome.TargetVersion, outcome.Detail, null, ct, promote: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A stop, not a failure: hand the record back now rather than letting the lease expire, and do not
            // charge the interrupted try to the record's retry budget.
            await ReleaseAsync(record, owner).ConfigureAwait(false);
            throw;
        }
        catch (RecordHeldException ex)
        {
            _logger.LogWarning("Held {SourceKey}: {Reason}", record.SourceKey, ex.Message);
            return await CompleteAsync(record, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null, ex.Message, ct).ConfigureAwait(false);
        }
        catch (HttpStatusException ex) when (IsTerminal(ex.StatusCode))
        {
            _logger.LogWarning("Held {SourceKey}: HTTP {Status} is not retryable ({Message})", record.SourceKey, ex.StatusCode, HeaderRedaction.RedactMessage(ex.Message));
            return await CompleteAsync(record, started, RecordStatus.Held, AttemptOutcome.Held, "none", null, null, ex.Message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or HttpRequestException or IOException or TimeoutException)
        {
            var attempts = record.AttemptCount;
            if (attempts >= _flow.Reliability.Retry.Attempts)
            {
                _logger.LogError("Failed {SourceKey} after {Attempts} attempt(s): {Message}", record.SourceKey, attempts, HeaderRedaction.RedactMessage(ex.Message));
                return await CompleteAsync(record, started, RecordStatus.Failed, AttemptOutcome.Failed, "none", null, null, ex.Message, ct).ConfigureAwait(false);
            }

            var backoff = RetryPolicy.RecordBackoff(_flow.Reliability.Retry, attempts);
            var next = _time.GetUtcNow().UtcDateTime + backoff;
            _logger.LogWarning("Retry {SourceKey} at {Next:u} (attempt {Attempt} of {Max}): {Message}", record.SourceKey, next, attempts, _flow.Reliability.Retry.Attempts, HeaderRedaction.RedactMessage(ex.Message));
            return await CompleteAsync(record, started, RecordStatus.Pending, AttemptOutcome.Failed, "none", null, null, ex.Message, ct, nextAttempt: next).ConfigureAwait(false);
        }
        finally
        {
            await renewals.CancelAsync().ConfigureAwait(false);
            try
            {
                await renewTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }
    }

    private bool IsTerminal(int status)
    {
        if (_flow.Reliability.SkipStatusCodes.Contains(status))
        {
            return true;
        }

        // Client errors that a retry cannot fix. 401/408/425/429 are transient (token refresh, throttling).
        return status is (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.Forbidden or (int)HttpStatusCode.NotFound
            or (int)HttpStatusCode.MethodNotAllowed or (int)HttpStatusCode.Conflict or (int)HttpStatusCode.RequestEntityTooLarge
            or (int)HttpStatusCode.UnsupportedMediaType or (int)HttpStatusCode.UnprocessableEntity;
    }

    private async Task<WorkerSummary> CompleteAsync(
        RecordState record,
        DateTime started,
        RecordStatus status,
        AttemptOutcome outcome,
        string phase,
        long? version,
        string? detail,
        string? error,
        CancellationToken ct,
        bool promote = false,
        DateTime? nextAttempt = null)
    {
        var completed = _time.GetUtcNow().UtcDateTime;
        var redacted = error is null ? null : HeaderRedaction.RedactMessage(error);
        await _ledger.CompleteAsync(new RecordCompletion
        {
            DeliveryKey = record.DeliveryKey,
            Status = status,
            Promote = promote,
            TargetVersion = version,
            TargetId = record.TargetId,
            NextAttemptUtc = nextAttempt,
            Error = redacted,
            Attempt = new AttemptRecord
            {
                DeliveryKey = record.DeliveryKey,
                SubmissionId = record.LastSubmissionId,
                RunId = RunId,
                Worker = _workerId,
                StartedUtc = started,
                CompletedUtc = completed,
                Outcome = outcome,
                Phase = phase,
                MetadataHash = record.PendingMetadataHash,
                PayloadHash = record.PendingPayloadHash,
                TargetVersion = version,
                Error = redacted ?? detail,
            },
        }, CancellationToken.None).ConfigureAwait(false);

        // The completion callback: everything a listener needs to trace this try, after the ledger row is written.
        await _listener.OnEventAsync(new DeliveryEvent
        {
            AtUtc = completed,
            FlowId = _flow.Id,
            FlowName = _flow.Name,
            Kind = status switch
            {
                RecordStatus.Delivered => "record.delivered",
                RecordStatus.Pending => "record.retry",
                RecordStatus.Held => "record.held",
                _ => "record.failed",
            },
            SubmissionId = record.LastSubmissionId,
            DeliveryKey = record.DeliveryKey,
            SourceKey = record.SourceKey,
            Label = record.Label,
            TargetId = record.TargetId,
            TargetVersion = version,
            Worker = _workerId,
            Phase = phase,
            Duration = completed - started,
            Detail = redacted ?? detail ?? (nextAttempt is { } n ? $"next attempt at {n:u}" : null),
        }, CancellationToken.None).ConfigureAwait(false);

        return status switch
        {
            RecordStatus.Delivered => new WorkerSummary(1, 1, 0, 0, 0),
            RecordStatus.Pending => new WorkerSummary(1, 0, 1, 0, 0),
            RecordStatus.Held => new WorkerSummary(1, 0, 0, 1, 0),
            _ => new WorkerSummary(1, 0, 0, 0, 1),
        };
    }

    private async Task ReleaseAsync(RecordState record, string owner)
    {
        try
        {
            var released = await _ledger.ReleaseLeaseAsync(record.DeliveryKey, owner, countAttempt: false, _time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            if (released)
            {
                _logger.LogInformation("Released {SourceKey} on shutdown; it will be picked up by the next pass.", record.SourceKey);
                await _listener.OnEventAsync(new DeliveryEvent
                {
                    AtUtc = _time.GetUtcNow().UtcDateTime,
                    FlowId = _flow.Id,
                    FlowName = _flow.Name,
                    Kind = "record.released",
                    SubmissionId = record.LastSubmissionId,
                    DeliveryKey = record.DeliveryKey,
                    SourceKey = record.SourceKey,
                    Label = record.Label,
                    TargetId = record.TargetId,
                    Worker = _workerId,
                    Detail = "worker stopped; lease released without charging an attempt",
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is DeliveryException or InvalidOperationException or System.Data.Common.DbException)
        {
            // The lease will expire on its own; losing one attempt of budget is the worst case.
            _logger.LogWarning("Could not release {SourceKey} on shutdown: {Message}", record.SourceKey, ex.Message);
        }
    }

    private async Task RenewLoopAsync(Identity.DeliveryKey key, string owner, TimeSpan lease, CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(lease.TotalMilliseconds / 2, 1000));
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, _time, ct).ConfigureAwait(false);
            var renewed = await _ledger.RenewLeaseAsync(key, owner, lease, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            if (!renewed)
            {
                _logger.LogWarning("Lease on {Key} could not be renewed; another worker may have reclaimed it.", key);
            }
        }
    }
}

/// <summary>Opens payload chunks from the drop; each open is a fresh stream so a retried request re-reads the blob.</summary>
public sealed class DropPayloadSource : IPayloadSource
{
    private readonly IDropReader _drops;
    private readonly string _location;
    private IReadOnlyList<PayloadChunk>? _chunks;

    public DropPayloadSource(IDropReader drops, string location)
    {
        ArgumentNullException.ThrowIfNull(drops);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        _drops = drops;
        _location = location;
    }

    public async Task<IReadOnlyList<PayloadChunk>> ListChunksAsync(CancellationToken ct = default)
        => _chunks ??= await _drops.ListPayloadChunksAsync(_location, ct).ConfigureAwait(false);

    public Task<Stream> OpenAsync(PayloadChunk chunk, CancellationToken ct = default) => _drops.OpenChunkAsync(chunk, ct);
}
