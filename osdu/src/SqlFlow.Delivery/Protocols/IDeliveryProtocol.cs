using System.Text.Json.Nodes;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Protocols;

/// <summary>One payload file of a record, in delivery order: its position, where it is, how long it is and when it was last written.</summary>
public sealed record PayloadFile(int Index, string Path, long Size, DateTimeOffset? Modified = null);

/// <summary>Opens a record's payload files for streaming. Each open returns a fresh stream, so retries re-open the blob.</summary>
public interface IPayloadSource
{
    Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default);

    Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default);
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

    /// <summary>
    /// The parts of the record's payload a route that sends parts reads (docs/interfaces-design.md section 5.5), each with
    /// its files and content hash; empty for a route that sends one payload set (<see cref="Payload"/>) or none.
    /// </summary>
    public IReadOnlyList<WorkPayloadPart> Parts { get; init; } = [];

    /// <summary>The parts this delivery sends whatever their hashes say (a redelivery named them, or the record never delivered its payload).</summary>
    public IReadOnlySet<string> ForcedParts { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether this delivery sends <paramref name="part"/>: the payload is due, and the part is forced or its content
    /// hash differs from the one the record last delivered it with.
    /// </summary>
    public bool Sends(WorkPayloadPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return DeliverPayload
            && (ForcedParts.Contains(part.Role)
                || !TargetState.TryGetValue(Model.PayloadParts.StateKey(part.Payload), out var delivered)
                || !string.Equals(delivered, part.Hash, StringComparison.Ordinal));
    }

    /// <summary>Whether this delivery runs a part that has no files of its own (the workflow run) because it is forced.</summary>
    public bool Forces(string role) => DeliverPayload && ForcedParts.Contains(role);

    /// <summary>The values of a completed step, or null when the step has not run for this pending work.</summary>
    public IReadOnlyDictionary<string, string>? Completed(string step)
        => CompletedSteps.TryGetValue(step, out var values) ? values : null;

    /// <summary>Reports a completed step to the worker (a no-op when nobody listens).</summary>
    public Task ReportStepAsync(string step, IReadOnlyDictionary<string, string> returned, CancellationToken ct)
        => StepCompleted is null ? Task.CompletedTask : StepCompleted(step, returned, ct);
}

/// <summary>One part of a record's payload as a route that sends parts reads it.</summary>
/// <param name="Role">The part (files, bulk).</param>
/// <param name="Payload">The payload set it is read from.</param>
/// <param name="Source">Its files; null for an optional part the record carries none for.</param>
/// <param name="Hash">Its content hash, which the record's target state keeps once the part is delivered.</param>
public sealed record WorkPayloadPart(string Role, string Payload, IPayloadSource? Source, string Hash);

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
/// One record to verify: the id the target holds it under, the version the ledger recorded for it, and, when its delivery
/// recorded what a verify compares beyond the version (the hash of the content the flow owns), the record's target state.
/// </summary>
public sealed record VerifyRequest(string TargetId, long? ExpectedVersion, IReadOnlyDictionary<string, string>? TargetState = null);

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

    /// <summary>Records one batched read of the target takes: the id to look up and the version the ledger holds.</summary>
    int MaxVerifyBatch => 1;

    /// <summary>
    /// Whether a verify needs each record's target state, which then goes with every <see cref="VerifyRequest"/>: a protocol
    /// whose target keeps a record under a key the target gave (a DSPDM row's primary key) finds the record by it.
    /// </summary>
    bool VerifiesWithTargetState => false;

    /// <summary>
    /// Verifies several records, in one request when <see cref="MaxVerifyBatch"/> allows it. Results align with
    /// <paramref name="requests"/>. The default reads them one at a time; a protocol whose service takes a list of
    /// ids overrides this, which is what keeps a drift pass over a large estate to a handful of requests rather
    /// than one per record.
    /// </summary>
    async Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(IReadOnlyList<VerifyRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var results = new List<VerifyResult>(requests.Count);
        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await VerifyAsync(request.TargetId, request.ExpectedVersion, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// The legal tags among <paramref name="tags"/> the legal service would refuse, each with its reason, or null
    /// when this target does not ask the legal service: a protocol without a legal check, a flow that turned the
    /// check off, or a well log flow whose endpoint is the DDMS and names no legal path. Null means "not checked"
    /// and is never to be read as "valid".
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> InvalidLegalTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    /// <summary>
    /// Removes the record from OSDU to the extent <paramref name="scope"/> asks for. A record that is already gone
    /// is not an error. <paramref name="targetState"/> carries the identifiers of what else the record owns (its
    /// dataset records), for protocols that remove those too.
    /// </summary>
    Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default);

    /// <summary>
    /// Removes several records. Outcomes align with <paramref name="removals"/>; one that failed carries its failure
    /// instead of throwing, so one bad record never sinks the set. The default removes them one at a time; a protocol
    /// whose service takes a list overrides this for the scopes that service can batch.
    /// </summary>
    Task<IReadOnlyList<RemovalResult>> DeleteBatchAsync(IReadOnlyList<RecordRemoval> removals, RemovalScope scope, CancellationToken ct = default)
        => DeleteOneByOneAsync(removals, scope, ct);

    /// <summary>
    /// Removes the records one at a time through <see cref="DeleteAsync"/>, each reporting its own outcome. This is
    /// the fallback behind every batched removal: a protocol whose service takes a list uses it for the scopes that
    /// service cannot batch, and for a chunk the service refused over something other than the records in it.
    /// </summary>
    async Task<IReadOnlyList<RemovalResult>> DeleteOneByOneAsync(IReadOnlyList<RecordRemoval> removals, RemovalScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(removals);
        var results = new List<RemovalResult>(removals.Count);
        foreach (var removal in removals)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await DeleteAsync(removal.TargetId, scope, removal.TargetState, ct).ConfigureAwait(false);
                results.Add(new RemovalResult(removal, outcome, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                results.Add(new RemovalResult(removal, null, ex));
            }
        }

        return results;
    }

    /// <summary>Reads the record back as the target holds it, or null when the target has no such record.</summary>
    Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default);

    /// <summary>
    /// Reads the record back as the target holds it, given what its deliveries recorded (<paramref name="targetState"/>), or
    /// null when the target has no such record. A protocol that finds a record by its id alone reads it as
    /// <see cref="ReadAsync(string, CancellationToken)"/> does.
    /// </summary>
    Task<JsonObject?> ReadAsync(string targetId, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
        => ReadAsync(targetId, ct);

    /// <summary>A reachability and credential check against the service's info endpoint, under the flow's auth.</summary>
    Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default);
}

/// <summary>What a probe found: whether the service answered, with which status, and the path it was asked on.</summary>
public sealed record ProbeOutcome(bool Reachable, int Status, string Detail, string Path);

public sealed record DeleteOutcome(bool Deleted, bool AlreadyGone, string Detail);

/// <summary>
/// How much of a record a removal takes away in OSDU (openapi storage v2). The three are genuinely different
/// operations against different endpoints, not degrees of one: only <see cref="Record"/> can be undone, and only
/// <see cref="History"/> leaves the record live.
/// </summary>
public enum RemovalScope
{
    /// <summary>
    /// <c>POST /records/{id}:delete</c>: the record stops resolving in OSDU. Nothing is destroyed and OSDU can
    /// revert it. This is the only reversible scope.
    /// </summary>
    Record,

    /// <summary>
    /// <c>DELETE /records/{id}/versions</c>: every earlier version is destroyed permanently and the latest version
    /// stays live and retrievable. The record itself is untouched, so the ledger keeps it delivered.
    /// </summary>
    History,

    /// <summary>
    /// <c>DELETE /records/{id}</c>: the record and every one of its versions are destroyed permanently. Cannot be
    /// undone.
    /// </summary>
    Everything,
}

/// <summary>What bounds one removal, wherever it is driven from.</summary>
public static class RemovalLimits
{
    /// <summary>
    /// The most records one removal can select by filter. It bounds the task payload, the work a single node takes
    /// on, and the blast radius of a mis-aimed filter; a larger removal is several removals.
    /// </summary>
    public const int MaxSelection = 25_000;

    /// <summary>Records resolved, removed and written back per round trip.</summary>
    public const int Chunk = 500;

    /// <summary>Per-record results carried back in a removal's task result before it is summarised instead.</summary>
    public const int MaxReported = 200;
}

/// <summary>One record a removal is to act on: its OSDU id and what earlier deliveries registered alongside it.</summary>
public sealed record RecordRemoval(DeliveryKey Key, string TargetId, IReadOnlyDictionary<string, string>? TargetState);

/// <summary>What a batched removal did to one record: its outcome, or the failure that stopped it.</summary>
public sealed record RemovalResult(RecordRemoval Removal, DeleteOutcome? Outcome, Exception? Failure)
{
    public bool Succeeded => Failure is null && Outcome is not null;
}
