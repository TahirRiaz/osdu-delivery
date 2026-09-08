using System.Text.Json.Nodes;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Protocols;

/// <summary>Opens a record's payload chunks for streaming. Each open returns a fresh stream, so retries re-open the blob.</summary>
public interface IPayloadSource
{
    Task<IReadOnlyList<PayloadChunk>> ListChunksAsync(CancellationToken ct = default);

    Task<Stream> OpenAsync(PayloadChunk chunk, CancellationToken ct = default);
}

/// <summary>What one delivery attempt must do for one record.</summary>
public sealed record DeliveryWork
{
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NoSteps = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

    public required DeliveryKey Key { get; init; }

    public required string TargetId { get; init; }

    public required JsonObject Document { get; init; }

    public required bool DeliverMetadata { get; init; }

    public required bool DeliverPayload { get; init; }

    public IPayloadSource? Payload { get; init; }

    /// <summary>The last known OSDU version, when updating; null when creating.</summary>
    public long? ExistingVersion { get; init; }

    public string? SourceKey { get; init; }

    public string? Label { get; init; }

    /// <summary>
    /// The steps an earlier try of this same pending work already completed, by step name, with what the target
    /// returned for each (design.md section 16.3). A protocol skips a completed step and reuses its values: a file
    /// uploaded and registered by the previous try is referenced, not uploaded again.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> CompletedSteps { get; init; } = NoSteps;

    /// <summary>
    /// Called by the protocol after every step that changed the target, with what the target returned. The worker
    /// persists it on the record before the next step starts, so a crash never repeats a completed step.
    /// </summary>
    public Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task>? StepCompleted { get; init; }

    /// <summary>The identifiers the target returned for this record in earlier deliveries (dataset ids, a workflow run).</summary>
    public IReadOnlyDictionary<string, string> TargetState { get; init; } = NoValues;

    /// <summary>The values of a completed step, or null when the step has not run for this pending work.</summary>
    public IReadOnlyDictionary<string, string>? Completed(string step)
        => CompletedSteps.TryGetValue(step, out var values) ? values : null;

    /// <summary>Reports a completed step to the worker (a no-op when nobody listens).</summary>
    public Task ReportStepAsync(string step, IReadOnlyDictionary<string, string> returned, CancellationToken ct)
        => StepCompleted is null ? Task.CompletedTask : StepCompleted(step, returned, ct);
}

/// <summary>One step of a delivery attempt, with its timing, the status the target answered, and what it returned.</summary>
public sealed record DeliveryStep(
    string Name,
    DateTime StartedUtc,
    DateTime CompletedUtc,
    int? Status,
    IReadOnlyDictionary<string, string> Returned,
    bool Resumed = false,
    string? Error = null);

/// <summary>Builds the step list of one attempt as the protocol goes.</summary>
public sealed class DeliverySteps
{
    private readonly TimeProvider _time;
    private readonly List<DeliveryStep> _steps = [];

    public DeliverySteps(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<DeliveryStep> Steps => _steps;

    /// <summary>All values returned so far, later steps overriding earlier ones on the same name.</summary>
    public IReadOnlyDictionary<string, string> Returned
    {
        get
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var step in _steps)
            {
                foreach (var (name, value) in step.Returned)
                {
                    merged[name] = value;
                }
            }

            return merged;
        }
    }

    public DateTime Now => _time.GetUtcNow().UtcDateTime;

    public void Add(string name, DateTime started, int? status, IReadOnlyDictionary<string, string>? returned = null, string? error = null)
        => _steps.Add(new DeliveryStep(name, started, Now, status, returned ?? new Dictionary<string, string>(StringComparer.Ordinal), false, error));

    /// <summary>A step an earlier try completed: recorded as resumed, with the values it returned then.</summary>
    public void Resumed(string name, IReadOnlyDictionary<string, string> returned)
    {
        var now = Now;
        _steps.Add(new DeliveryStep(name, now, now, null, returned, Resumed: true));
    }
}

public sealed record DeliveryOutcome
{
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>(StringComparer.Ordinal);

    public required bool MetadataDelivered { get; init; }

    public required bool PayloadDelivered { get; init; }

    /// <summary>The OSDU version after the write, when the response reported one.</summary>
    public long? TargetVersion { get; init; }

    public int ChunksSent { get; init; }

    public string? Detail { get; init; }

    /// <summary>
    /// Every value the target returned for the record: the record id and version, dataset ids and file sources,
    /// a bulk session id, a workflow run id. Merged into the record's target state and written on the attempt.
    /// </summary>
    public IReadOnlyDictionary<string, string> Returned { get; init; } = NoValues;

    /// <summary>The steps the attempt took, in order, with what each returned.</summary>
    public IReadOnlyList<DeliveryStep> Steps { get; init; } = [];

    /// <summary>Set when the work failed; the worker classifies it (held, retried, failed) exactly as a thrown one.</summary>
    public Exception? Failure { get; init; }

    public bool Succeeded => Failure is null;

    public static DeliveryOutcome Failed(Exception failure, IReadOnlyList<DeliveryStep>? steps = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new DeliveryOutcome { MetadataDelivered = false, PayloadDelivered = false, Failure = failure, Steps = steps ?? [] };
    }
}

public sealed record VerifyResult(Ledger.VerifyOutcome Outcome, long? ObservedVersion, string? Detail);

/// <summary>
/// A named delivery protocol implemented in code and parameterised by the flow (design.md section 8.4). The core is
/// protocol independent: identity, rendering, change detection, the ledger and idempotency; only this varies. A
/// protocol that can write several records in one request declares <see cref="MaxBatch"/> above one and the worker
/// hands it batches (section 16.2); every protocol reports each step it took and what the target returned.
/// </summary>
public interface IDeliveryProtocol
{
    DeliveryProtocol Kind { get; }

    /// <summary>Records per write request the protocol can batch. One delivers each record in its own request.</summary>
    int MaxBatch => 1;

    Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default);

    /// <summary>
    /// Delivers several records, in one request when <see cref="MaxBatch"/> allows it. Outcomes align with the
    /// works; an item that failed carries its failure instead of throwing, so one bad record never sinks the batch.
    /// </summary>
    async Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var outcomes = new List<DeliveryOutcome>(works.Count);
        foreach (var work in works)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                outcomes.Add(await DeliverAsync(work, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                outcomes.Add(DeliveryOutcome.Failed(ex));
            }
        }

        return outcomes;
    }

    /// <summary>Reads the record back and compares the observed version with the expected one.</summary>
    Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default);

    /// <summary>
    /// Removes the record from OSDU: a logical (revertible) delete by default, a physical purge when
    /// <paramref name="purge"/> is set. A record that is already gone is not an error. <paramref name="targetState"/>
    /// carries the identifiers of what else the record owns (its dataset records), for protocols that remove those too.
    /// </summary>
    Task<DeleteOutcome> DeleteAsync(string targetId, bool purge, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default);

    /// <summary>Reads the record back as the target holds it, or null when the target has no such record.</summary>
    Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default);

    /// <summary>A reachability and credential check against the service's info endpoint, under the flow's auth.</summary>
    Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default);
}

/// <summary>What a probe found: whether the service answered, with which status, and the path it was asked on.</summary>
public sealed record ProbeOutcome(bool Reachable, int Status, string Detail, string Path);

public sealed record DeleteOutcome(bool Deleted, bool AlreadyGone, string Detail);
