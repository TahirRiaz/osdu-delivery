using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Record status vocabulary (design.md section 7.4). <c>Held</c>, <c>Failed</c> and <c>Deleted</c> are terminal
/// until an operator releases the record or the source changes.
/// </summary>
public enum RecordStatus
{
    Pending,
    Delivering,
    Delivered,
    Held,
    Failed,

    /// <summary>Removed from OSDU by an operator; blocked from redelivery while the source is unchanged.</summary>
    Deleted,
}

public enum SubmissionStatus
{
    Received,
    Planned,
    Running,
    Completed,
    Failed,
}

public enum AttemptOutcome
{
    Delivered,
    Skipped,
    Failed,
    Held,

    /// <summary>The record was removed from OSDU, reversibly or by a purge of everything.</summary>
    Deleted,

    /// <summary>The record's earlier versions were purged; the record itself is still delivered and live.</summary>
    HistoryPurged,
}

/// <summary>The phases of the attempts that record a decision not to send, beside the delivery phases the worker reports.</summary>
public static class AttemptPhases
{
    /// <summary>A skipped attempt: the drop carried a version older than the one delivered or queued.</summary>
    public const string Stale = "stale";

    /// <summary>A skipped attempt: the final hash check found OSDU already holding the queued document and payload.</summary>
    public const string Unchanged = "unchanged";
}

public enum VerifyOutcome
{
    Match,
    Drifted,
    Missing,
    Error,
}

/// <summary>One drop handed over by Databricks (design.md section 7.2). Its id is the idempotency key.</summary>
public sealed record SubmissionState
{
    public required Guid SubmissionId { get; init; }

    public required Guid FlowId { get; init; }

    public required string FlowName { get; init; }

    public required string MappingReference { get; init; }

    public required string RenderContext { get; init; }

    public required string DropLocation { get; init; }

    public string ParametersJson { get; init; } = "{}";

    public long RecordCount { get; init; }

    public SubmissionStatus Status { get; init; } = SubmissionStatus.Received;

    /// <summary>The work location the intake wrote its batches under (design.md section 16.2).</summary>
    public string? WorkLocation { get; init; }

    /// <summary>How many work batches the intake wrote.</summary>
    public int BatchCount { get; init; }

    /// <summary>How many root-scope partitions the drop declared.</summary>
    public int Partitions { get; init; }

    public DateTime ReceivedUtc { get; init; }

    public DateTime? StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public long Planned { get; init; }

    public long SkippedUnchanged { get; init; }

    /// <summary>
    /// Records the drop carried in a version older than the one already delivered or queued: skipped and never sent,
    /// each with an attempt saying which version it was and which one stands.
    /// </summary>
    public long SkippedStale { get; init; }

    /// <summary>Records whose queued document was, when the worker came to send it, what OSDU already held: nothing was sent.</summary>
    public long UnchangedAtPush { get; init; }

    /// <summary>Records held, failed or deleted earlier whose source has not changed; they need a release.</summary>
    public long Blocked { get; init; }

    public long Delivered { get; init; }

    public long Held { get; init; }

    public long Failed { get; init; }

    public string? Error { get; init; }
}

/// <summary>The current state of one deliverable (design.md section 7.3).</summary>
public sealed record RecordState
{
    public required DeliveryKey DeliveryKey { get; init; }

    public required Guid FlowId { get; init; }

    public required string SourceKey { get; init; }

    /// <summary>Human-readable label from the mapping's identity.label template (for search and display only).</summary>
    public string? Label { get; init; }

    public required string MappingName { get; init; }

    /// <summary>The render context of the last delivered document, canonical JSON.</summary>
    public string? RenderContext { get; init; }

    public string? SourceFingerprint { get; init; }

    /// <summary>When the source row the delivered document was built from last changed (the flow's source.lastModified).</summary>
    public DateTime? SourceModifiedUtc { get; init; }

    public string? MetadataHash { get; init; }

    public string? PayloadHash { get; init; }

    /// <summary>The newest modified time among the chunk files the delivered payload was sent from.</summary>
    public DateTime? PayloadModifiedUtc { get; init; }

    public string? TargetId { get; init; }

    public long? TargetVersion { get; init; }

    public RecordStatus Status { get; init; } = RecordStatus.Pending;

    public DateTime? LastDeliveredUtc { get; init; }

    public DateTime? LastVerifiedUtc { get; init; }

    public VerifyOutcome? LastVerifyOutcome { get; init; }

    public string? LeaseOwner { get; init; }

    public DateTime? LeaseExpiresUtc { get; init; }

    public Guid? LastSubmissionId { get; init; }

    /// <summary>The platform run that queued or held the pending work, for the attempt it writes.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Attempts made for the pending work; reset when new work is queued.</summary>
    public int AttemptCount { get; init; }

    public DateTime? NextAttemptUtc { get; init; }

    /// <summary>Redacted message of the last failure, or the hold reason.</summary>
    public string? LastError { get; init; }

    /// <summary>
    /// Where the rendered document waiting to be delivered sits in the submission's work batches
    /// (batch:offset:length, see <see cref="Storage.DocumentRef"/>), and the hashes it will establish.
    /// </summary>
    public string? PendingDocumentRef { get; init; }

    /// <summary>The work batch the pending document was written in.</summary>
    public int? WorkBatch { get; init; }

    /// <summary>
    /// The identifiers the target returned for what it currently holds (record id and version, dataset ids and
    /// file sources, a workflow run id), as a JSON object merged step by step (design.md section 16.3).
    /// </summary>
    public string? TargetStateJson { get; init; }

    /// <summary>
    /// The steps of the pending delivery that already completed and what they returned, so a retry resumes after
    /// them instead of repeating an upload (a JSON object keyed by step name).
    /// </summary>
    public string? PendingStepJson { get; init; }

    public string? PendingRenderContext { get; init; }

    /// <summary>The source fingerprint of the pending work, or of the state a held/failed/deleted record was left in.</summary>
    public string? PendingSourceFingerprint { get; init; }

    /// <summary>The source last-modified moment of the pending work, or of the state a held/failed/deleted record was left in.</summary>
    public DateTime? PendingSourceModifiedUtc { get; init; }

    public string? PendingMetadataHash { get; init; }

    public string? PendingPayloadHash { get; init; }

    /// <summary>The payload watermark of the pending work, when it carries a payload.</summary>
    public DateTime? PendingPayloadModifiedUtc { get; init; }

    /// <summary>Drop-relative location of the pending payload chunks, or null when no payload is pending.</summary>
    public string? PendingPayloadLocation { get; init; }

    public bool PendingMetadata { get; init; }

    public bool PendingPayload { get; init; }

    /// <summary>
    /// Set when the record was held, failed or deleted and not released since. A blocked record is planned again
    /// only when its source changes (fingerprint moved) or an operator releases it.
    /// </summary>
    public bool Blocked { get; init; }

    /// <summary>
    /// The cache values this record was built from, as the id of the set it shares with every record that read the
    /// same values. A plan compares it against the gated sets to know whether an unapproved cache change is holding
    /// this record back.
    /// </summary>
    public long? CacheSetId { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }

    /// <summary>A rendered document is queued for the record, or being delivered right now.</summary>
    public bool HasPendingWork => PendingDocumentRef is not null && Status is RecordStatus.Pending or RecordStatus.Delivering;
}

/// <summary>One delivery try, append-only (design.md section 7.3).</summary>
public sealed record AttemptRecord
{
    public long AttemptId { get; init; }

    public required DeliveryKey DeliveryKey { get; init; }

    public Guid? SubmissionId { get; init; }

    /// <summary>The platform run the attempt happened in, when it did.</summary>
    public Guid? RunId { get; init; }

    public required string Worker { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required DateTime CompletedUtc { get; init; }

    public required AttemptOutcome Outcome { get; init; }

    /// <summary>What the attempt delivered: metadata, payload, both, delete or nothing.</summary>
    public required string Phase { get; init; }

    public string? MetadataHash { get; init; }

    public string? PayloadHash { get; init; }

    public long? TargetVersion { get; init; }

    public string? Error { get; init; }

    /// <summary>
    /// What the try did and what the target answered, step by step: a JSON object with a <c>steps</c> array (name,
    /// started and completed times, status, the values returned) and the values the record now carries.
    /// </summary>
    public string? ResultJson { get; init; }

    /// <summary>The work batch the try belonged to, when it ran from one.</summary>
    public int? WorkBatch { get; init; }
}

/// <summary>What the worker writes back after processing a claimed record.</summary>
public sealed record RecordCompletion
{
    public required DeliveryKey DeliveryKey { get; init; }

    public required RecordStatus Status { get; init; }

    public required AttemptRecord Attempt { get; init; }

    /// <summary>When delivered: promote the pending document, hashes and context to current.</summary>
    public bool Promote { get; init; }

    /// <summary>
    /// The promotion settles work the final hash check found OSDU already holding: the pending state becomes current
    /// but nothing reached the target, so the delivery and verification times stay as they were.
    /// </summary>
    public bool NothingSent { get; init; }

    public long? TargetVersion { get; init; }

    public string? TargetId { get; init; }

    public DateTime? NextAttemptUtc { get; init; }

    public string? Error { get; init; }

    /// <summary>The target state to merge into the record (the returned identifiers), when the try changed it.</summary>
    public string? TargetStateJson { get; init; }

    /// <summary>The step progress to keep on the record for the next try (null clears it).</summary>
    public string? PendingStepJson { get; init; }

    /// <summary>
    /// The pending work this try carried out, as it stood when the record was claimed. A newer version of the record
    /// can be queued while the try is in flight; the completion then promotes what it actually delivered, leaves the
    /// newer work pending for the next pass, and keeps its step progress out of the newer work. Null for a
    /// completion that carried no document.
    /// </summary>
    public ClaimedWork? Claimed { get; init; }
}

/// <summary>
/// What a claimed record's pending work was: the submission and document reference that identify it (a reference is
/// only unique within its submission's work batches), and the values a delivery of it establishes.
/// </summary>
public sealed record ClaimedWork(
    Guid? SubmissionId,
    string DocumentRef,
    string? RenderContext,
    string? SourceFingerprint,
    DateTime? SourceModifiedUtc,
    string? MetadataHash,
    string? PayloadHash,
    DateTime? PayloadModifiedUtc,
    bool Metadata,
    bool Payload)
{
    /// <summary>The claimed work of a record, or null when it holds no pending document.</summary>
    public static ClaimedWork? Of(RecordState record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.PendingDocumentRef is not { } reference
            ? null
            : new ClaimedWork(
                record.LastSubmissionId, reference, record.PendingRenderContext, record.PendingSourceFingerprint, record.PendingSourceModifiedUtc,
                record.PendingMetadataHash, record.PendingPayloadHash, record.PendingPayloadModifiedUtc, record.PendingMetadata, record.PendingPayload);
    }
}

/// <summary>Why the intake left a record's delivered state as it is.</summary>
public enum SkipKind
{
    /// <summary>The source version, payload and render context equal what OSDU holds: decided without rendering.</summary>
    Unchanged,

    /// <summary>Rendered, and the document and payload hashes equal what OSDU holds (or what is already queued).</summary>
    Rendered,

    /// <summary>The drop carries a version older than the one delivered or queued; OSDU keeps the newer one.</summary>
    Stale,
}

/// <summary>One record the intake skipped, with what it saw, so the ledger can advance or record it.</summary>
public sealed record SkippedRecord
{
    public required DeliveryKey DeliveryKey { get; init; }

    public required SkipKind Kind { get; init; }

    /// <summary>What the plan said about the record, for the attempt a stale skip writes.</summary>
    public required string Reason { get; init; }

    /// <summary>The source version the drop carried.</summary>
    public string? SourceFingerprint { get; init; }

    public DateTime? SourceModifiedUtc { get; init; }

    /// <summary>The payload watermark the drop carried.</summary>
    public DateTime? PayloadModifiedUtc { get; init; }

    /// <summary>The render context the record was rendered under (a rendered skip advances the record to it).</summary>
    public string? RenderContext { get; init; }

    /// <summary>The platform run of the intake, for the attempt a stale skip writes.</summary>
    public Guid? RunId { get; init; }
}

/// <summary>What staging a batch of pending work did.</summary>
/// <param name="Staged">Records whose pending work was written (queued behind an in-flight delivery included).</param>
/// <param name="Refused">
/// Records refused because the ledger already holds a newer source version or payload for them, delivered or queued
/// by a concurrent intake since this one read them. They are stale and are recorded as such.
/// </param>
public sealed record PendingStaging(int Staged, IReadOnlyList<DeliveryKey> Refused);

/// <summary>Tier-0 watermark: the Delta commit version of a source table for one flow scope (design.md section 6.6).</summary>
public sealed record SourceWatermark(Guid FlowId, string Scope, string Table, long Version, DateTime RecordedUtc, string? ContextHash = null);

/// <summary>One stored dependency of a cache set: which cached path it holds, and what it held.</summary>
public sealed record CacheUse(string TypeName, string ItemId, string Path, Snapshots.CacheUsageKind Kind, string ValueHash, string ValueText);

/// <summary>A cache set that holds one cached value, for the impact query.</summary>
public sealed record CacheSetUse(long SetId, string TypeName, string ItemId, string Path, Snapshots.CacheUsageKind Kind, string ValueHash, string ValueText);

/// <summary>What one rollout pass did, and what is left of the tag.</summary>
public sealed record UpdateRolloutBatch(long TagId, long Marked, long Processed, long Affected, bool Completed);

/// <summary>One cache change and what happens about it, covering every record built from the value that moved.</summary>
public sealed record UpdateTag
{
    public long TagId { get; init; }

    public string Kind { get; init; } = "cache";

    public required string TypeName { get; init; }

    public required string ItemId { get; init; }

    public required string Path { get; init; }

    /// <summary>changed, removed or unmatched.</summary>
    public required string Change { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public string? FromVersion { get; init; }

    public required string ToVersion { get; init; }

    /// <summary>auto or approve.</summary>
    public required string Mode { get; init; }

    /// <summary>pending, approved, rejected, rolling or applied.</summary>
    public string Status { get; init; } = "pending";

    /// <summary>The cache sets holding the value that moved.</summary>
    public IReadOnlyList<long> SetIds { get; init; } = [];

    public long AffectedRecords { get; init; }

    public long Processed { get; init; }

    public DateTime DetectedUtc { get; init; }

    public DateTime? DecidedUtc { get; init; }

    public string? DecidedBy { get; init; }

    public DateTime? StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    /// <summary>A tag waits for a decision only under approve; auto is decided as it is written.</summary>
    public bool WaitsForApproval => Mode.Equals("approve", StringComparison.OrdinalIgnoreCase) && Status.Equals("pending", StringComparison.OrdinalIgnoreCase);

    /// <summary>Records still to be marked for redelivery.</summary>
    public long Remaining => Math.Max(0, AffectedRecords - Processed);

    public string Describe() => Change switch
    {
        "removed" => $"{TypeName} '{ItemId}' is no longer in the cache (it held {Path} = '{OldValue}')",
        "unmatched" => $"{TypeName} '{ItemId}' no longer matches by {Path} = '{OldValue}'",
        _ => $"{TypeName} '{ItemId}': {Path} changed from '{OldValue}' to '{NewValue}'",
    };
}

/// <summary>The compact known-state row Databricks reads at the start of a run (design.md section 6.7).</summary>
public sealed record KnownState(
    DeliveryKey DeliveryKey,
    string SourceKey,
    string? SourceFingerprint,
    DateTime? SourceModifiedUtc,
    string? MetadataHash,
    string? PayloadHash,
    DateTime? PayloadModifiedUtc,
    RecordStatus Status,
    string? TargetId,
    long? TargetVersion);

/// <summary>What is uploaded and what is not, per flow: the numbers an operator looks at first.</summary>
public sealed record FlowStats
{
    public long Total { get; init; }

    public long Pending { get; init; }

    public long Delivering { get; init; }

    public long Delivered { get; init; }

    public long Held { get; init; }

    public long Failed { get; init; }

    public long Deleted { get; init; }

    /// <summary>Delivered records whose last verify found drift or a missing record.</summary>
    public long Drifted { get; init; }

    public long DeliveredLast24h { get; init; }

    public DateTime? LastDeliveredUtc { get; init; }

    public DateTime? LastVerifiedUtc { get; init; }

    public long Submissions { get; init; }

    public SubmissionState? LastSubmission { get; init; }
}

/// <summary>The life of one work batch: queued by the intake, claimed by a drain, done or failed.</summary>
public enum WorkBatchStatus
{
    Queued,
    Running,
    Done,
    Failed,
}

/// <summary>One work batch of a submission (design.md section 16.2): a file of rendered documents and its progress.</summary>
public sealed record WorkBatchState
{
    public required Guid SubmissionId { get; init; }

    public required Guid FlowId { get; init; }

    public required int Index { get; init; }

    public required string Location { get; init; }

    public int RecordCount { get; init; }

    public WorkBatchStatus Status { get; init; } = WorkBatchStatus.Queued;

    public string? LeaseOwner { get; init; }

    public DateTime? LeaseExpiresUtc { get; init; }

    /// <summary>The platform run that is draining, or drained, the batch.</summary>
    public Guid? RunId { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime? StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public long Delivered { get; init; }

    public long Held { get; init; }

    public long Failed { get; init; }

    /// <summary>Records left pending with a retry time when the batch closed.</summary>
    public long Retrying { get; init; }

    public string? Error { get; init; }
}

/// <summary>A claimed batch with the pending records it leased for the claimer.</summary>
public sealed record ClaimedWorkBatch(WorkBatchState Batch, IReadOnlyList<RecordState> Records);

/// <summary>Which half of a record a forced redelivery re-sends.</summary>
public enum RedeliverScope
{
    All,
    Metadata,
    Payload,
}

/// <summary>How a free-text search is applied. Prefix search uses the indexes and answers in milliseconds.</summary>
public enum SearchMode
{
    Prefix,
    Contains,
}

/// <summary>A record listing: filter, search, sort and page.</summary>
public sealed record RecordQuery
{
    public RecordStatus? Status { get; init; }

    /// <summary>A delivery key, a target id, or a prefix of the label or source key.</summary>
    public string? Search { get; init; }

    public SearchMode Mode { get; init; } = SearchMode.Prefix;

    /// <summary>Only records touched by this submission.</summary>
    public Guid? SubmissionId { get; init; }

    /// <summary>
    /// Only records the given platform run touched, resolved through the attempts that run wrote. Records carry no
    /// run of their own: a record is delivered by many runs over its life, and the attempt is the thing that
    /// belongs to one.
    /// </summary>
    public Guid? RunId { get; init; }

    /// <summary>Only delivered records whose last verify found drift or a missing record.</summary>
    public bool Drifted { get; init; }

    /// <summary>
    /// Filters on whether the ledger has ever recorded a delivery for the record. A record is given its OSDU id
    /// when it is planned, not when it lands, so an id is no evidence that OSDU holds anything: this is what
    /// separates the records a removal will really take from the ones it will find were never there.
    /// </summary>
    public bool? EverDelivered { get; init; }

    public int Max { get; init; } = 100;

    public int Offset { get; init; }
}

/// <summary>An activity listing: filter and page.</summary>
public sealed record ActivityQuery
{
    public Guid? FlowId { get; init; }

    public Guid? DeliveryKey { get; init; }

    public Guid? SubmissionId { get; init; }

    public Guid? RunId { get; init; }

    public string? Kind { get; init; }

    public string? Actor { get; init; }

    public string? Outcome { get; init; }

    public DateTime? SinceUtc { get; init; }

    public DateTime? UntilUtc { get; init; }

    public int Max { get; init; } = 100;

    public int Offset { get; init; }
}

/// <summary>
/// One operator or scheduler action, persisted for the audit trail: who did what, when, with which inputs, and
/// what came of it. Record-level history lives in attempts; this is the history of runs and interventions.
/// </summary>
/// <summary>The statuses of a retrieval run.</summary>
public static class RetrievalStatus
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>One retrieval run as the ledger holds it: the window it covered, where its files went, and its outcome.</summary>
public sealed record RetrievalState
{
    public long RetrievalId { get; init; }

    public required Guid FlowId { get; init; }

    public required string FlowName { get; init; }

    public Guid? RunId { get; init; }

    public string Actor { get; init; } = "unknown";

    /// <summary>The kinds the run covered, comma separated.</summary>
    public required string Kinds { get; init; }

    /// <summary>The query as it ran, window included.</summary>
    public string? Query { get; init; }

    public string? WindowField { get; init; }

    public DateTime? WindowFrom { get; init; }

    /// <summary>The upper bound of the window: the next run's lower bound once this one is done.</summary>
    public DateTime? WindowTo { get; init; }

    public required string Location { get; init; }

    public string? ManifestLocation { get; init; }

    public string Status { get; init; } = RetrievalStatus.Running;

    public long Records { get; init; }

    public int Files { get; init; }

    /// <summary>Uncompressed bytes written.</summary>
    public long Bytes { get; init; }

    public DateTime StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public string? Error { get; init; }
}

public sealed record ActivityRecord
{
    public long ActivityId { get; init; }

    public required Guid FlowId { get; init; }

    public required string FlowName { get; init; }

    /// <summary>run, submit, plan, verify, known-state, release, redeliver, delete, poll, notification.</summary>
    public required string Kind { get; init; }

    /// <summary>cli:&lt;user&gt;, gui:&lt;user&gt;, service:notification, service:schedule.</summary>
    public required string Actor { get; init; }

    public required DateTime StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    /// <summary>running, completed, failed, cancelled.</summary>
    public string Outcome { get; init; } = "running";

    public string? ParametersJson { get; init; }

    public Guid? SubmissionId { get; init; }

    /// <summary>Set when the action targeted one record.</summary>
    public Guid? DeliveryKey { get; init; }

    /// <summary>The platform run the activity ran as, when it was a run (deliver, verify, known-state).</summary>
    public Guid? RunId { get; init; }

    public string? Summary { get; init; }

    /// <summary>Captured log lines (capped), for actions run from the service.</summary>
    public string? Log { get; init; }
}

/// <summary>
/// The ledger (design.md section 7): submissions, records and append-only attempts, with leasing for the worker,
/// plus the activity audit trail and the catalog read-model. Implemented over SQL Server through EF Core; the
/// interface keeps the engine free of EF.
/// </summary>
public interface ILedger
{
    Task<SubmissionState?> GetSubmissionAsync(Guid submissionId, CancellationToken ct = default);

    /// <summary>Registers a submission. Returns the existing one when the id was seen before (idempotent).</summary>
    Task<(SubmissionState Submission, bool Created)> RegisterSubmissionAsync(SubmissionState submission, CancellationToken ct = default);

    Task UpdateSubmissionAsync(SubmissionState submission, CancellationToken ct = default);

    Task<IReadOnlyList<SubmissionState>> ListSubmissionsAsync(Guid? flowId, int max, CancellationToken ct = default);

    Task<IReadOnlyDictionary<DeliveryKey, RecordState>> GetRecordsAsync(Guid flowId, IEnumerable<DeliveryKey> keys, CancellationToken ct = default);

    Task<RecordState?> GetRecordAsync(Guid flowId, DeliveryKey key, CancellationToken ct = default);

    /// <summary>Finds a record by key across flows (the key is globally unique).</summary>
    Task<RecordState?> FindRecordAsync(DeliveryKey key, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates records with pending work; existing current-state columns are preserved. A record another
    /// worker is delivering right now keeps its lease and status, and the new work is queued behind the delivery:
    /// the worker's completion leaves it pending for the next pass. Work carrying a source or payload version older
    /// than the one the record already holds, delivered or queued, is refused, so concurrent intakes can never take
    /// a record back to an earlier version.
    /// </summary>
    Task<PendingStaging> UpsertPendingAsync(IReadOnlyList<RecordState> records, CancellationToken ct = default);

    /// <summary>
    /// Records what the intake skipped. A record with no pending work moves to the submission; a record with pending
    /// work stays with the submission that queued it. A rendered skip of a delivered record advances its source
    /// version, payload watermark and render context to what was just found identical, so the next plan decides it
    /// without rendering. A stale skip writes an attempt saying which version the drop carried and which one stands.
    /// </summary>
    Task MarkSkippedAsync(Guid flowId, IReadOnlyList<SkippedRecord> records, Guid submissionId, CancellationToken ct = default);

    /// <summary>Marks records held without queueing work (render-time holds).</summary>
    Task MarkHeldAsync(IEnumerable<RecordState> records, CancellationToken ct = default);

    /// <summary>
    /// Atomically leases up to <paramref name="max"/> pending records (or records whose lease expired) for
    /// <paramref name="owner"/>. Records are returned with the lease applied.
    /// </summary>
    Task<IReadOnlyList<RecordState>> ClaimAsync(Guid flowId, Guid? submissionId, string owner, int max, TimeSpan lease, DateTime nowUtc, CancellationToken ct = default);

    Task<bool> RenewLeaseAsync(DeliveryKey key, string owner, TimeSpan lease, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Hands a leased record back to <c>pending</c> without writing an attempt: the worker is stopping, not failing.
    /// With <paramref name="countAttempt"/> false the interrupted try is not charged to the record's retry budget.
    /// </summary>
    Task<bool> ReleaseLeaseAsync(DeliveryKey key, string owner, bool countAttempt, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Writes the attempt and the resulting record state, releasing the lease.</summary>
    Task CompleteAsync(RecordCompletion completion, CancellationToken ct = default);

    /// <summary>Writes many completions in one round trip (a drained batch); each is the same write as <see cref="CompleteAsync"/>.</summary>
    Task CompleteManyAsync(IReadOnlyList<RecordCompletion> completions, CancellationToken ct = default);

    /// <summary>
    /// Keeps a delivery's step progress on the record mid-try, so a crash after an upload never repeats it. Written
    /// only while the record still holds the document the try is delivering: the steps of a superseded document
    /// must never let the newer one skip an upload it has not made.
    /// </summary>
    Task SaveStepAsync(DeliveryKey key, Guid? submissionId, string documentRef, string stepJson, CancellationToken ct = default);

    /// <summary>How many distinct records a submission's attempts settled with the given outcome (and phase, when given).</summary>
    Task<long> CountAttemptsAsync(Guid submissionId, AttemptOutcome outcome, string? phase = null, CancellationToken ct = default);

    /// <summary>Releases leases that expired before <paramref name="beforeUtc"/> and returns how many were reclaimed.</summary>
    Task<int> ReclaimExpiredLeasesAsync(Guid flowId, DateTime beforeUtc, CancellationToken ct = default);

    Task<long> CountAsync(Guid flowId, Guid? submissionId, RecordStatus status, CancellationToken ct = default);

    Task<bool> HasPendingAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>The earliest retry time among pending records that are not yet due, or null when nothing waits.</summary>
    Task<DateTime?> NextDueAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Registers a work batch the intake wrote (idempotent on submission and index).</summary>
    Task AddWorkBatchAsync(WorkBatchState batch, CancellationToken ct = default);

    /// <summary>
    /// Atomically claims the oldest queued (or lease-expired) work batch of the flow, or of one submission, and
    /// leases its pending records for <paramref name="owner"/>. Null when nothing is claimable.
    /// </summary>
    Task<ClaimedWorkBatch?> ClaimWorkBatchAsync(Guid flowId, Guid? submissionId, string owner, TimeSpan lease, DateTime nowUtc, Guid? runId = null, CancellationToken ct = default);

    /// <summary>Extends the lease on a batch and on every record leased under its token.</summary>
    Task<bool> RenewWorkBatchLeaseAsync(Guid submissionId, int batch, string owner, TimeSpan lease, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Closes a batch with its counts; records it did not reach are handed back to pending.</summary>
    Task CompleteWorkBatchAsync(Guid submissionId, int batch, string owner, WorkBatchStatus status, long delivered, long held, long failed, long retrying, string? failure, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Hands a claimed batch (and its leased records) back on a stop, without charging attempts.</summary>
    Task<bool> ReleaseWorkBatchAsync(Guid submissionId, int batch, string owner, DateTime nowUtc, CancellationToken ct = default);

    Task<IReadOnlyList<WorkBatchState>> ListWorkBatchesAsync(Guid submissionId, int max, int offset, CancellationToken ct = default);

    Task<long> CountWorkBatchesAsync(Guid submissionId, WorkBatchStatus? status, CancellationToken ct = default);

    Task<IReadOnlyList<RecordState>> ListAsync(Guid flowId, RecordQuery query, CancellationToken ct = default);

    Task<FlowStats> StatsAsync(Guid flowId, DateTime nowUtc, CancellationToken ct = default);

    Task<IReadOnlyList<AttemptRecord>> ListAttemptsAsync(DeliveryKey key, int max, CancellationToken ct = default);

    /// <summary>The attempts a submission produced, newest first: the submission view.</summary>
    Task<IReadOnlyList<AttemptRecord>> ListAttemptsForSubmissionAsync(Guid submissionId, int max, CancellationToken ct = default);

    /// <summary>How many records match a listing, for paging.</summary>
    Task<int> CountAsync(Guid flowId, RecordQuery query, CancellationToken ct = default);

    /// <summary>Records matching a lookup across every flow: an exact delivery key, or a prefix over the OSDU id, the
    /// source key and the label. Index-backed, newest first.</summary>
    Task<IReadOnlyList<RecordState>> LookupAsync(string term, int max, CancellationToken ct = default);

    /// <summary>How many records a lookup matches.</summary>
    Task<int> CountLookupAsync(string term, CancellationToken ct = default);

    /// <summary>Delivered records due for the drift pass, oldest verification first.</summary>
    Task<IReadOnlyList<RecordState>> ListForVerifyAsync(Guid flowId, DateTime? verifiedBeforeUtc, int max, CancellationToken ct = default);

    Task RecordVerifyAsync(DeliveryKey key, VerifyOutcome outcome, long? observedVersion, DateTime nowUtc, bool requeue, CancellationToken ct = default);

    /// <summary>
    /// Releases held, failed or deleted records: those with a pending document go back to pending for the worker,
    /// the others are unblocked so the next submission plans them again. Null keys means every blocked record.
    /// </summary>
    Task<int> ReleaseAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Forgets what OSDU holds for the records so the next plan redelivers them (the whole record, the metadata
    /// document or the payload). Returns how many records were marked.
    /// </summary>
    Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey> keys, RedeliverScope scope, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Records what a removal did to a set of records, in one round trip. <see cref="RemovalScope.Record"/> and
    /// <see cref="RemovalScope.Everything"/> take the record out of OSDU, so the ledger marks it deleted and
    /// blocked and forgets the hashes; <see cref="RemovalScope.History"/> leaves the record live, so its custody
    /// state is untouched and only the attempt is written. Either way every record gets its own attempt, saying
    /// which scope ran and who asked for it, because that attempt is how the removal is audited afterwards.
    /// </summary>
    Task MarkRemovedAsync(IReadOnlyList<DeliveryKey> keys, RemovalScope scope, string worker, DateTime nowUtc, string? correlationId = null, CancellationToken ct = default);

    /// <summary>
    /// The keys of every record a listing matches, in key order, up to <paramref name="max"/>. Key order is what
    /// makes this safe to act on: a removal changes the records it touches, and the newest-first order the listing
    /// pages in would shuffle rows between pages while the removal ran.
    /// </summary>
    Task<IReadOnlyList<DeliveryKey>> ListKeysAsync(Guid flowId, RecordQuery query, int max, CancellationToken ct = default);

    Task<IReadOnlyList<KnownState>> KnownStateAsync(Guid flowId, CancellationToken ct = default);

    /// <summary>The known state of every record of the flow, streamed in key order in pages, for publications of any size.</summary>
    IAsyncEnumerable<KnownState> StreamKnownStateAsync(Guid flowId, int pageSize = 10_000, CancellationToken ct = default);

    /// <summary>
    /// The id of the cache set holding exactly these values, creating it the first time it is seen. A render hands
    /// over what it consumed and gets back one number to put on the record, so no matter how many records a run
    /// stages, the dependency trail costs one row per distinct combination rather than one per record.
    /// </summary>
    Task<long> EnsureCacheSetAsync(IReadOnlyList<Snapshots.CacheUsage> usages, CancellationToken ct = default);

    /// <summary>The values behind one set, for a record's history page.</summary>
    Task<IReadOnlyList<CacheUse>> ListCacheSetAsync(long setId, CancellationToken ct = default);

    /// <summary>
    /// The sets that hold a cached value of the given items, with the value each of them holds. This is the impact
    /// query: it runs over the sets, never over the records, so it stays the same size as the cache.
    /// </summary>
    Task<IReadOnlyList<CacheSetUse>> FindCacheSetsAsync(string typeName, IReadOnlyList<string> itemIds, CancellationToken ct = default);

    /// <summary>How many delivered records were built from these sets.</summary>
    Task<long> CountRecordsInSetsAsync(IReadOnlyList<long> setIds, CancellationToken ct = default);

    /// <summary>
    /// Writes the tags a cache change produced, one per change rather than one per record, and gates the sets an
    /// unapproved change touches. Returns how many tags were new; a change already open for the same value updates
    /// the standing tag, and reopens it when the value moved again after an approval.
    /// </summary>
    Task<int> TagUpdatesAsync(IReadOnlyList<UpdateTag> tags, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>The gated sets, which a plan reads once per run to know which records are held back.</summary>
    Task<IReadOnlyList<long>> GatedCacheSetsAsync(CancellationToken ct = default);

    /// <summary>The tags in a status, newest first.</summary>
    Task<IReadOnlyList<UpdateTag>> ListTagsAsync(string? status, int max, int offset, CancellationToken ct = default);

    Task<int> CountTagsAsync(string? status, CancellationToken ct = default);

    /// <summary>
    /// Decides tags: approving lets the rollout carry the change out, rejecting leaves the delivered documents
    /// alone. Either way the sets stop being gated unless another undecided tag still covers them.
    /// </summary>
    Task<int> DecideTagsAsync(IReadOnlyList<long> tagIds, bool approve, string actor, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Carries one batch of an approved tag: marks up to <paramref name="batchSize"/> of its records for
    /// redelivery in key order from the tag's cursor, advances the cursor and reports what is left. A change over
    /// millions of records is drained a batch at a time by a caller that decides the pace.
    /// </summary>
    Task<UpdateRolloutBatch> RollOutTagAsync(long tagId, int batchSize, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>The approved tags with rollout still to do, oldest decision first.</summary>
    Task<IReadOnlyList<UpdateTag>> ListRolloutQueueAsync(int max, CancellationToken ct = default);

    Task<IReadOnlyList<SourceWatermark>> GetWatermarksAsync(Guid flowId, string scope, CancellationToken ct = default);

    Task SetWatermarksAsync(IEnumerable<SourceWatermark> watermarks, CancellationToken ct = default);

    /// <summary>Removes attempts older than the cut-off, keeping the latest attempt per record.</summary>
    Task<int> PruneAttemptsAsync(DateTime olderThanUtc, CancellationToken ct = default);

    /// <summary>Opens the row of a retrieval run and returns it with its id.</summary>
    Task<RetrievalState> StartRetrievalAsync(RetrievalState retrieval, CancellationToken ct = default);

    /// <summary>Closes a retrieval run with its outcome and counts.</summary>
    Task CompleteRetrievalAsync(long retrievalId, string status, long records, int files, long bytes, string? manifestLocation, string? failure, DateTime completedUtc, CancellationToken ct = default);

    /// <summary>The flow's most recent retrieval run in the given status (the watermark chain reads the last done one), or null.</summary>
    Task<RetrievalState?> LastRetrievalAsync(Guid flowId, string status, CancellationToken ct = default);

    /// <summary>The flow's retrieval runs, newest first.</summary>
    Task<IReadOnlyList<RetrievalState>> ListRetrievalsAsync(Guid flowId, int max, CancellationToken ct = default);

    Task<ActivityRecord> StartActivityAsync(ActivityRecord activity, CancellationToken ct = default);

    /// <summary>Closes an activity with its outcome, summary and captured log, and the submission it turned out to work on.</summary>
    Task CompleteActivityAsync(long activityId, string outcome, string? summary, string? log, DateTime completedUtc, Guid? submissionId = null, CancellationToken ct = default);

    Task<ActivityRecord?> GetActivityAsync(long activityId, CancellationToken ct = default);

    Task<IReadOnlyList<ActivityRecord>> ListActivitiesAsync(ActivityQuery query, CancellationToken ct = default);

    /// <summary>How many activities match a listing, for paging.</summary>
    Task<int> CountActivitiesAsync(ActivityQuery query, CancellationToken ct = default);
}
