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
    /// <summary>A skipped attempt: the source carried a version older than the one delivered or queued.</summary>
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

/// <summary>Where the version of a record came from: the ingestion table's file and row, and when the table last updated the row.</summary>
public readonly record struct RecordOrigin(string? FileName, long? RowNumber, DateTime? UpdatedUtc)
{
    public static RecordOrigin None { get; } = new(null, null, null);
}

/// <summary>
/// One plan of a flow over its ingestion tables (docs/stage4-design.md section 3.3): an incremental window, a full read,
/// or a set of record keys. Its id is the idempotency key.
/// </summary>
public sealed record SubmissionState
{
    public required Guid SubmissionId { get; init; }

    public required Guid FlowId { get; init; }

    public required string FlowName { get; init; }

    public required string MappingReference { get; init; }

    public required string RenderContext { get; init; }

    public string ParametersJson { get; init; } = "{}";

    /// <summary>How many candidate records the plan estimated when it opened the source.</summary>
    public long RecordCount { get; init; }

    public SubmissionStatus Status { get; init; } = SubmissionStatus.Received;

    /// <summary>The work location the intake wrote its batches under (design.md section 16.2).</summary>
    public string? WorkLocation { get; init; }

    /// <summary>How many work batches the intake wrote.</summary>
    public int BatchCount { get; init; }

    /// <summary>How many key slices the intake was cut into for its fan-out; 1 for a plan that ran on one node.</summary>
    public int Slices { get; init; }

    public DateTime ReceivedUtc { get; init; }

    public DateTime? StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public long Planned { get; init; }

    public long SkippedUnchanged { get; init; }

    /// <summary>
    /// Records a cache change waiting for approval held back: rendered, ready, and not sent until an operator
    /// approves or rejects the change. They are not unchanged, and a run that reports them as such hides the fact
    /// that a decision is what the estate is waiting on.
    /// </summary>
    public long AwaitingApproval { get; init; }

    /// <summary>
    /// Records the source carried in a version older than the one already delivered or queued: skipped and never sent,
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

    /// <summary>Records without a derivable delivery key: not planned, not delivered.</summary>
    public long Untracked { get; init; }

    public string? Error { get; init; }

    /// <summary>One of <see cref="SubmissionKinds"/>.</summary>
    public string Kind { get; init; } = SubmissionKinds.Incremental;

    /// <summary>The source connection reference as the flow declared it; never a resolved value.</summary>
    public string SourceConnection { get; init; } = string.Empty;

    /// <summary>The record table's three-part name.</summary>
    public string SourceObject { get; init; } = string.Empty;

    /// <summary>The lower bound (exclusive) of the change window planned; null for a plan without one.</summary>
    public DateTime? WindowFromUtc { get; init; }

    /// <summary>The upper bound (inclusive) of the change window planned; set for incremental and full plans.</summary>
    public DateTime? WindowToUtc { get; init; }

    /// <summary>What else bounded the read, as JSON (<see cref="Source.SourceWindowDescription"/>).</summary>
    public string? SourceWindowJson { get; init; }

    /// <summary>The platform run that coordinated the plan.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Whether the plan covered the whole scope, so its completion may move the scope's watermark.</summary>
    public bool CoversScope => Kind is SubmissionKinds.Incremental or SubmissionKinds.Full;
}

/// <summary>What a submission is: which selection of the ingestion tables it planned.</summary>
public static class SubmissionKinds
{
    /// <summary>The rows the tables changed in a window after the scope's watermark.</summary>
    public const string Incremental = "incremental";

    /// <summary>Every row of the scope, up to a bound: a first plan, or a replan.</summary>
    public const string Full = "full";

    /// <summary>Named record keys: a record-scoped run, or the records the ledger asked to plan again.</summary>
    public const string Keys = "keys";

    public static IReadOnlyList<string> All { get; } = [Incremental, Full, Keys];
}

/// <summary>
/// The current state of one deliverable (design.md section 7.3). A record is one flow's: its identity is the flow and the
/// delivery key together, so two flows reading the same source row keep two records with separate histories.
/// </summary>
public sealed record RecordState
{
    public required DeliveryKey DeliveryKey { get; init; }

    public required Guid FlowId { get; init; }

    public required string SourceKey { get; init; }

    /// <summary>The record's key tuple as a JSON array of strings, in the flow's key order: what a key-scoped read uses.</summary>
    public string? SourceKeyJson { get; init; }

    /// <summary>Human-readable label from the mapping's identity.label template (for search and display only).</summary>
    public string? Label { get; init; }

    public required string MappingName { get; init; }

    /// <summary>The render context of the last delivered document, canonical JSON.</summary>
    public string? RenderContext { get; init; }

    /// <summary>The ingestion fingerprint of the rows the delivered document was built from.</summary>
    public string? SourceFingerprint { get; init; }

    /// <summary>The business version of the row the delivered document was built from (the flow's source.lastModified).</summary>
    public DateTime? SourceModifiedUtc { get; init; }

    /// <summary>The ingestion file the version OSDU holds came from.</summary>
    public string? SourceFileName { get; init; }

    /// <summary>The row of that file.</summary>
    public long? SourceRowNumber { get; init; }

    /// <summary>When the ingestion table last updated that row.</summary>
    public DateTime? SourceUpdatedUtc { get; init; }

    public string? MetadataHash { get; init; }

    public string? PayloadHash { get; init; }

    /// <summary>The newest modified time among the payload files the delivered payload was sent from.</summary>
    public DateTime? PayloadModifiedUtc { get; init; }

    public string? TargetId { get; init; }

    /// <summary>
    /// The OSDU id the record claimed for its flow when it first queued a document; null for a record that was only ever
    /// held. Only a claimed id is this flow's to write, read back or remove. Written by the ledger, never by a caller.
    /// </summary>
    public string? ClaimedTargetId { get; init; }

    public long? TargetVersion { get; init; }

    public RecordStatus Status { get; init; } = RecordStatus.Pending;

    public DateTime? LastDeliveredUtc { get; init; }

    public DateTime? LastVerifiedUtc { get; init; }

    public VerifyOutcome? LastVerifyOutcome { get; init; }

    /// <summary>The token of the lease the record is being delivered under, while it is.</summary>
    public string? LeaseOwner { get; init; }

    /// <summary>When that lease runs out unless its worker renews it; the lease holds it, not the record.</summary>
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

    /// <summary>The ingestion fingerprint of the pending work, or of the state a held/failed/deleted record was left in.</summary>
    public string? PendingSourceFingerprint { get; init; }

    /// <summary>The business version of the pending work, or of the state a held/failed/deleted record was left in.</summary>
    public DateTime? PendingSourceModifiedUtc { get; init; }

    /// <summary>The ingestion file the queued version came from, or the one a held record was left at.</summary>
    public string? PendingSourceFileName { get; init; }

    /// <summary>The row of that file.</summary>
    public long? PendingSourceRowNumber { get; init; }

    /// <summary>When the ingestion table last updated that row.</summary>
    public DateTime? PendingSourceUpdatedUtc { get; init; }

    public string? PendingMetadataHash { get; init; }

    public string? PendingPayloadHash { get; init; }

    /// <summary>The payload watermark of the pending work, when it carries a payload.</summary>
    public DateTime? PendingPayloadModifiedUtc { get; init; }

    /// <summary>Where the pending payload's files are listed from (folder and pattern), or null when no payload is pending.</summary>
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

    /// <summary>When the ledger asked for the record to be planned again (a redeliver, a release, a cache rollout); null once a plan took it.</summary>
    public DateTime? PlanRequestedUtc { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }

    /// <summary>A rendered document is queued for the record, or being delivered right now.</summary>
    public bool HasPendingWork => PendingDocumentRef is not null && Status is RecordStatus.Pending or RecordStatus.Delivering;

    /// <summary>The origin of the version OSDU holds.</summary>
    public RecordOrigin Origin => new(SourceFileName, SourceRowNumber, SourceUpdatedUtc);

    /// <summary>The origin of the queued version, or of the one a held record was left at.</summary>
    public RecordOrigin PendingOrigin => new(PendingSourceFileName, PendingSourceRowNumber, PendingSourceUpdatedUtc);
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

    /// <summary>The ingestion file the attempt's document was built from.</summary>
    public string? SourceFileName { get; init; }

    /// <summary>The row of that file.</summary>
    public long? SourceRowNumber { get; init; }

    /// <summary>When the ingestion table last updated that row.</summary>
    public DateTime? SourceUpdatedUtc { get; init; }
}

/// <summary>How a try ended for one claimed record: what the worker appends under its lease, applied to the record later.</summary>
public sealed record RecordCompletion
{
    public required DeliveryKey DeliveryKey { get; init; }

    public required RecordStatus Status { get; init; }

    public required AttemptRecord Attempt { get; init; }

    /// <summary>When delivered: promote the pending document, hashes, context and origin to current.</summary>
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
    bool Payload,
    RecordOrigin Origin = default)
{
    /// <summary>The claimed work of a record, or null when it holds no pending document.</summary>
    public static ClaimedWork? Of(RecordState record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.PendingDocumentRef is not { } reference
            ? null
            : new ClaimedWork(
                record.LastSubmissionId, reference, record.PendingRenderContext, record.PendingSourceFingerprint, record.PendingSourceModifiedUtc,
                record.PendingMetadataHash, record.PendingPayloadHash, record.PendingPayloadModifiedUtc, record.PendingMetadata, record.PendingPayload,
                record.PendingOrigin);
    }
}

/// <summary>Why the intake left a record's delivered state as it is.</summary>
public enum SkipKind
{
    /// <summary>The source version, payload and render context equal what OSDU holds: decided without rendering.</summary>
    Unchanged,

    /// <summary>Rendered, and the document and payload hashes equal what OSDU holds (or what is already queued).</summary>
    Rendered,

    /// <summary>The source carries a version older than the one delivered or queued; OSDU keeps the newer one.</summary>
    Stale,
}

/// <summary>One record the intake skipped, with what it saw, so the ledger can advance or record it.</summary>
public sealed record SkippedRecord
{
    public required DeliveryKey DeliveryKey { get; init; }

    public required SkipKind Kind { get; init; }

    /// <summary>What the plan said about the record, for the attempt a stale skip writes.</summary>
    public required string Reason { get; init; }

    /// <summary>The record's key tuple, as the source read it.</summary>
    public string? SourceKeyJson { get; init; }

    /// <summary>The ingestion fingerprint the source carried.</summary>
    public string? SourceFingerprint { get; init; }

    public DateTime? SourceModifiedUtc { get; init; }

    /// <summary>The ingestion file and row the plan read, for the attempt a stale skip writes.</summary>
    public RecordOrigin Origin { get; init; }

    /// <summary>The payload watermark the source carried.</summary>
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
/// <param name="Conflicts">
/// Records refused because another flow's record has claimed the OSDU id they would be written to. Nothing was written
/// for them; the caller holds them with the owner named.
/// </param>
public sealed record PendingStaging(int Staged, IReadOnlyList<DeliveryKey> Refused, IReadOnlyList<TargetIdConflict> Conflicts)
{
    public static PendingStaging Empty { get; } = new(0, [], []);
}

/// <summary>A record whose OSDU id another flow has claimed: the id, and the flow that holds it.</summary>
/// <param name="DeliveryKey">The record that was not staged.</param>
/// <param name="TargetId">The OSDU id it would have been written to.</param>
/// <param name="OwnerFlowId">The flow whose record claimed the id.</param>
/// <param name="OwnerFlowName">That flow's name as its last submission recorded it, when the ledger knows it.</param>
public sealed record TargetIdConflict(DeliveryKey DeliveryKey, string TargetId, Guid OwnerFlowId, string? OwnerFlowName)
{
    /// <summary>What the held record says about the conflict: which id, whose it is, and how to resolve it.</summary>
    public string Describe()
    {
        var owner = OwnerFlowName is null ? $"flow {OwnerFlowId:D}" : $"flow '{OwnerFlowName}' ({OwnerFlowId:D})";
        return $"OSDU id {TargetId} is already claimed by {owner}, and one OSDU record belongs to one flow. Deliver this flow "
            + "to another data partition, or give its mapping a dataset.system or key that yields other OSDU ids.";
    }
}

/// <summary>
/// The watermark of one flow scope: the upper bound of the last whole-scope plan that completed, and the submission that
/// wrote it. The next incremental plan reads the rows changed after it, less the flow's overlap.
/// </summary>
public sealed record SourceWatermark(Guid FlowId, string Scope, DateTime UpdatedThroughUtc, Guid SubmissionId, DateTime RecordedUtc, string? ContextHash);

/// <summary>A record the ledger asked to be planned again, with the key tuple a key-scoped read finds it by.</summary>
public sealed record PlanRequestedRecord(DeliveryKey DeliveryKey, string? SourceKeyJson, DateTime RequestedUtc);

/// <summary>One stored dependency of a cache set: which cache and cached path it holds, and what it held.</summary>
public sealed record CacheUse(string Scope, string TypeName, string ItemId, string Path, Snapshots.CacheUsageKind Kind, string ValueHash, string ValueText);

/// <summary>A cache set that holds one cached value, for the impact query.</summary>
public sealed record CacheSetUse(long SetId, string Scope, string TypeName, string ItemId, string Path, Snapshots.CacheUsageKind Kind, string ValueHash, string ValueText);

/// <summary>What one rollout pass did, and what is left of the tag.</summary>
public sealed record UpdateRolloutBatch(long TagId, long Marked, long Processed, long Affected, bool Completed);

/// <summary>One cache change and what happens about it, covering every record built from the value that moved.</summary>
public sealed record UpdateTag
{
    public long TagId { get; init; }

    public string Kind { get; init; } = "cache";

    /// <summary>The partition whose cache the change was found in.</summary>
    public required string Scope { get; init; }

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

    /// <summary>The token of the lease the batch is drained under, while it is.</summary>
    public string? LeaseOwner { get; init; }

    /// <summary>When that lease runs out unless its worker renews it.</summary>
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

/// <summary>
/// A worker's hold on work (design.md section 16.2): a work batch, or a group of records due for a retry. It is one row
/// however many records it holds, and the worker renews only that row. The records carry its token.
/// </summary>
public sealed record LeaseState
{
    public required string Token { get; init; }

    public required Guid FlowId { get; init; }

    /// <summary>The submission the claim was made for: the batch's, or the one a retry claim named.</summary>
    public Guid? SubmissionId { get; init; }

    /// <summary>The work batch the lease drains, or null for records due for a retry.</summary>
    public int? WorkBatch { get; init; }

    /// <summary>Who holds the lease: the worker that claimed it, or the one recovering it.</summary>
    public required string Owner { get; init; }

    public Guid? RunId { get; init; }

    public DateTime AcquiredUtc { get; init; }

    public DateTime ExpiresUtc { get; init; }
}

/// <summary>A claimed batch, the lease it is drained under, and the pending records the lease holds.</summary>
public sealed record ClaimedWorkBatch(WorkBatchState Batch, LeaseState Lease, IReadOnlyList<RecordState> Records);

/// <summary>Records due for a retry, claimed together under one lease; no lease when nothing was due.</summary>
public sealed record ClaimedRecords(LeaseState? Lease, IReadOnlyList<RecordState> Records)
{
    public static ClaimedRecords None { get; } = new(null, []);
}

/// <summary>
/// A step of a delivery that completed against the target, with every step of the pending work completed so far: what the
/// record's next try resumes after. It belongs to the pending work the record was claimed with (the submission and the
/// document reference), and never reaches newer work queued behind the try.
/// </summary>
public sealed record RecordStep(DeliveryKey DeliveryKey, Guid? SubmissionId, string DocumentRef, string StepJson, DateTime AtUtc);

/// <summary>What a worker appends under its lease in one write: steps that completed, and tries that ended.</summary>
public sealed record LeaseAppend(IReadOnlyList<RecordStep> Steps, IReadOnlyList<RecordCompletion> Completions);

/// <summary>How a lease ends, which decides what happens to the work it did not reach.</summary>
public enum LeaseEnd
{
    /// <summary>The worker went through all of it: the batch is done.</summary>
    Done,

    /// <summary>The worker could not go on (the batch file could not be read): the batch failed.</summary>
    Failed,

    /// <summary>The worker is stopping: the batch is queued again, and the tries it did not finish are not charged.</summary>
    Stopped,

    /// <summary>The lease ran out with nobody renewing it: the batch is queued again, and the interrupted tries count.</summary>
    Expired,
}

/// <summary>How a worker closes its lease, with the batch's counts when it drained one.</summary>
public sealed record LeaseClosing
{
    public required LeaseEnd End { get; init; }

    public long Delivered { get; init; }

    public long Held { get; init; }

    public long Failed { get; init; }

    public long Retrying { get; init; }

    /// <summary>Why the batch failed, redacted.</summary>
    public string? Failure { get; init; }
}

/// <summary>What applying a lease did: the records its appended events settled, and the records it handed back.</summary>
public sealed record LeaseApplied(int Applied, int Released)
{
    public static LeaseApplied None { get; } = new(0, 0);

    public LeaseApplied Add(LeaseApplied other) => new(Applied + other.Applied, Released + other.Released);
}

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

    /// <summary>A delivery key, a target id, or a prefix of the label, the source key or the origin file name.</summary>
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

    /// <summary>Where the page starts; inside the first <see cref="RecordListing.CountLimit"/> records of the order.</summary>
    public int Offset { get; init; }
}

/// <summary>A count that stops at a limit: the number of matching records when <see cref="Exact"/>, otherwise a floor.</summary>
public readonly record struct BoundedCount(int Count, bool Exact);

/// <summary>
/// What bounds a record listing, so that every page, count and search reads a bounded part of the ledger however many
/// records a flow holds.
/// </summary>
public static class RecordListing
{
    /// <summary>
    /// How far a listing counts and how deep it pages. A total up to this is exact and a larger one is a floor; the
    /// records past it are reached by narrowing the filter. It equals the removal selection limit, so a listing's count
    /// is exact whenever the records it matches could be removed together.
    /// </summary>
    public const int CountLimit = RemovalLimits.MaxSelection;

    /// <summary>
    /// The most records a contains search reads. A contains term has no index, so the rest of the filter must leave at
    /// most this many records for it to scan; a broader filter is refused with <see cref="RecordQueryTooBroadException"/>.
    /// </summary>
    public const int ContainsScanLimit = 100_000;

    /// <summary>Candidates the lookup across every flow takes from each identity index before ordering them.</summary>
    public const int LookupCandidateLimit = 1_000;
}

/// <summary>A record listing that cannot be answered inside the bounds of <see cref="RecordListing"/>; the message says how to narrow it.</summary>
public sealed class RecordQueryTooBroadException : DeliveryException
{
    public RecordQueryTooBroadException(string message)
        : base(message)
    {
    }

    public RecordQueryTooBroadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
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

/// <summary>
/// One operator or scheduler action, persisted for the audit trail: who did what, when, with which inputs, and
/// what came of it. Record-level history lives in attempts; this is the history of runs and interventions.
/// </summary>
public sealed record ActivityRecord
{
    public long ActivityId { get; init; }

    public required Guid FlowId { get; init; }

    public required string FlowName { get; init; }

    /// <summary>deliver, plan, intake, drain, verify, replan, submit, release, redeliver, delete, poll, notification.</summary>
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

    /// <summary>The platform run the activity ran as, when it was a run (deliver, plan, verify, replan).</summary>
    public Guid? RunId { get; init; }

    public string? Summary { get; init; }

    /// <summary>Captured log lines (capped), for actions run from the service.</summary>
    public string? Log { get; init; }
}

/// <summary>
/// The ledger (design.md section 7): submissions, records and append-only attempts, with leasing for the worker,
/// plus the activity audit trail and the catalog read-model. Implemented over the <c>osdu</c> schema through EF Core; the
/// interface keeps the engine free of EF.
/// </summary>
public interface ILedger
{
    Task<SubmissionState?> GetSubmissionAsync(Guid submissionId, CancellationToken ct = default);

    /// <summary>Registers a submission. Returns the existing one when the id was seen before (idempotent).</summary>
    Task<(SubmissionState Submission, bool Created)> RegisterSubmissionAsync(SubmissionState submission, CancellationToken ct = default);

    Task UpdateSubmissionAsync(SubmissionState submission, CancellationToken ct = default);

    /// <summary>A flow's submissions, newest first, or every flow's when <paramref name="flowId"/> is null.</summary>
    Task<IReadOnlyList<SubmissionState>> ListSubmissionsAsync(Guid? flowId, int max, CancellationToken ct = default);

    Task<IReadOnlyDictionary<DeliveryKey, RecordState>> GetRecordsAsync(Guid flowId, IEnumerable<DeliveryKey> keys, CancellationToken ct = default);

    Task<RecordState?> GetRecordAsync(Guid flowId, DeliveryKey key, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates one flow's records with pending work; existing current-state columns are preserved. A record
    /// another worker is delivering right now keeps its lease and status, and the new work is queued behind the delivery:
    /// the worker's completion leaves it pending for the next pass. Work carrying a source or payload version older
    /// than the one the record already holds, delivered or queued, is refused, so concurrent intakes can never take
    /// a record back to an earlier version. Work for an OSDU id another flow's record has claimed is refused as a
    /// conflict, and the first staging of a record claims its id for the flow. Staging writes the record's key tuple and
    /// the pending origin, and clears a request to plan it again. Every record must carry <paramref name="flowId"/>.
    /// </summary>
    Task<PendingStaging> UpsertPendingAsync(Guid flowId, IReadOnlyList<RecordState> records, CancellationToken ct = default);

    /// <summary>
    /// Records what the intake skipped. A record with no pending work moves to the submission; a record with pending
    /// work stays with the submission that queued it. A rendered skip of a delivered record advances its source
    /// version, payload watermark and render context to what was just found identical, so the next plan decides it
    /// without rendering. A stale skip writes an attempt saying which version and origin the source carried and which
    /// version stands. Every skip writes the record's key tuple and clears a request to plan it again.
    /// </summary>
    Task MarkSkippedAsync(Guid flowId, IReadOnlyList<SkippedRecord> records, Guid submissionId, CancellationToken ct = default);

    /// <summary>
    /// Marks one flow's records held without queueing work (render-time holds), writing the key tuple and the origin they
    /// were held at. A held record claims no OSDU id, and it is given no id another flow's record has claimed. Every
    /// record must carry <paramref name="flowId"/>.
    /// </summary>
    Task MarkHeldAsync(Guid flowId, IEnumerable<RecordState> records, CancellationToken ct = default);

    /// <summary>
    /// The records of a flow the ledger asked to be planned again, in delivery-key order after <paramref name="after"/>, at
    /// most <paramref name="max"/>: what a run pages through to plan them as a key-scoped read.
    /// </summary>
    Task<IReadOnlyList<PlanRequestedRecord>> ListPlanRequestedAsync(Guid flowId, DeliveryKey? after, int max, CancellationToken ct = default);

    /// <summary>Clears the request to plan records again for records a plan saw and left untouched (blocked ones).</summary>
    Task ClearPlanRequestedAsync(Guid flowId, IReadOnlyList<DeliveryKey> keys, CancellationToken ct = default);

    /// <summary>
    /// Claims up to <paramref name="max"/> of the flow's (or submission's) pending records that are due, under one new
    /// lease for <paramref name="owner"/>, after recovering the flow's leases that ran out. The records come back holding
    /// the lease; <see cref="ClaimedRecords.None"/> when nothing was due.
    /// </summary>
    Task<ClaimedRecords> ClaimAsync(Guid flowId, Guid? submissionId, string owner, int max, TimeSpan lease, DateTime nowUtc, Guid? runId = null, CancellationToken ct = default);

    /// <summary>
    /// Extends a lease its worker still holds: one row, however many records it holds. False when the lease is gone or
    /// another worker took it over after it ran out, and the worker must stop sending.
    /// </summary>
    Task<bool> RenewLeaseAsync(string token, TimeSpan lease, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Appends, in one write, the steps that completed and the tries that ended under a lease: each try's attempt, and
    /// the events the lease later applies to the records. Nothing is updated. A step is written before the delivery that
    /// reported it goes on, so a crash never repeats it.
    /// </summary>
    Task AppendAsync(Guid flowId, string token, LeaseAppend append, CancellationToken ct = default);

    /// <summary>
    /// Applies what the lease's worker has appended so far to its records, a slice at a time, and deletes each event with
    /// its application: a try's outcome settles the record and hands it back from the lease; a step is kept for the next
    /// try while the record still holds the work it belongs to. A record another lease holds now is left to that lease.
    /// </summary>
    Task<LeaseApplied> CheckpointLeaseAsync(string token, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Ends a lease: applies what its worker appended, hands the records it did not settle back to pending (as
    /// <paramref name="closing"/> says), settles its batch, and deletes it. A lease another worker recovered meanwhile
    /// still has its appended events applied, and nothing else.
    /// </summary>
    Task<LeaseApplied> CloseLeaseAsync(string token, LeaseClosing closing, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Recovers the flow's leases that ran out before <paramref name="nowUtc"/>: each is taken over by this caller, so two
    /// never recover the same one, and closed as <see cref="LeaseEnd.Expired"/>. Returns the records settled or handed back.
    /// </summary>
    Task<int> RecoverExpiredLeasesAsync(Guid flowId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>How many distinct records a submission's attempts settled with the given outcome (and phase, when given).</summary>
    Task<long> CountAttemptsAsync(Guid submissionId, AttemptOutcome outcome, string? phase = null, CancellationToken ct = default);

    Task<long> CountAsync(Guid flowId, Guid? submissionId, RecordStatus status, CancellationToken ct = default);

    Task<bool> HasPendingAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>The earliest retry time among pending records that are not yet due, or null when nothing waits.</summary>
    Task<DateTime?> NextDueAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// When the earliest lease of the flow runs out, or of the submission (its batches, and any lease holding one of its
    /// records), or null when there is none. Expired leases are included, so a lease a stopped worker left behind stays
    /// visible until it is recovered.
    /// </summary>
    Task<DateTime?> NextLeaseExpiryAsync(Guid flowId, Guid? submissionId, CancellationToken ct = default);

    /// <summary>
    /// The completed or failed submissions of the flow, other than <paramref name="except"/>, that still hold records due
    /// for delivery with their rendered documents: records released back to pending after their run was over. At most
    /// <paramref name="max"/>. It reads the flow's pending records, which a run leaves few of once its own are sent.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListSettledSubmissionsWithDueWorkAsync(Guid flowId, Guid? except, DateTime nowUtc, int max, CancellationToken ct = default);

    /// <summary>Registers a work batch the intake wrote (idempotent on submission and index).</summary>
    Task AddWorkBatchAsync(WorkBatchState batch, CancellationToken ct = default);

    /// <summary>
    /// Claims the oldest queued work batch of the flow, or of one submission, under a new lease for
    /// <paramref name="owner"/>, after recovering the flow's leases that ran out, and has the lease hold the batch's
    /// pending records that are due. Null when nothing is claimable.
    /// </summary>
    Task<ClaimedWorkBatch?> ClaimWorkBatchAsync(Guid flowId, Guid? submissionId, string owner, TimeSpan lease, DateTime nowUtc, Guid? runId = null, CancellationToken ct = default);

    Task<IReadOnlyList<WorkBatchState>> ListWorkBatchesAsync(Guid submissionId, int max, int offset, CancellationToken ct = default);

    Task<long> CountWorkBatchesAsync(Guid submissionId, WorkBatchStatus? status, CancellationToken ct = default);

    /// <summary>
    /// A page of the records a listing matches, most recently updated first and ties broken by key. It reads a bounded
    /// part of the ledger at any volume: the page starts inside the first <see cref="RecordListing.CountLimit"/> records,
    /// a prefix search orders at most that many candidates from each identity index, and a contains search is refused
    /// when the rest of the filter leaves more than <see cref="RecordListing.ContainsScanLimit"/> records. A listing past
    /// those bounds throws <see cref="RecordQueryTooBroadException"/>.
    /// </summary>
    Task<IReadOnlyList<RecordState>> ListAsync(Guid flowId, RecordQuery query, CancellationToken ct = default);

    Task<FlowStats> StatsAsync(Guid flowId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>One of the flow's records' attempts, newest first: the record's history, and nothing of another flow's record with the same key.</summary>
    Task<IReadOnlyList<AttemptRecord>> ListAttemptsAsync(Guid flowId, DeliveryKey key, int max, CancellationToken ct = default);

    /// <summary>The attempts a submission produced, newest first: the submission view.</summary>
    Task<IReadOnlyList<AttemptRecord>> ListAttemptsForSubmissionAsync(Guid submissionId, int max, CancellationToken ct = default);

    /// <summary>
    /// How many records match a listing, counting no further than <paramref name="limit"/>: exact below it, a floor at
    /// it. A prefix search that ran into its candidate bound while another filter narrowed it is a floor too.
    /// </summary>
    Task<BoundedCount> CountAsync(Guid flowId, RecordQuery query, int limit, CancellationToken ct = default);

    /// <summary>Records matching a lookup across every flow: an exact delivery key (one record per flow that reads the
    /// row), or a prefix over the OSDU id, the source key, the label and the origin file name. At most
    /// <see cref="RecordListing.LookupCandidateLimit"/> candidates are read from each identity index, and the most recently
    /// updated of them are returned.</summary>
    Task<IReadOnlyList<RecordState>> LookupAsync(string term, int max, CancellationToken ct = default);

    /// <summary>How many records a lookup matches, counting no further than <paramref name="limit"/>.</summary>
    Task<BoundedCount> CountLookupAsync(string term, int limit, CancellationToken ct = default);

    /// <summary>Delivered records due for the drift pass, oldest verification first.</summary>
    Task<IReadOnlyList<RecordState>> ListForVerifyAsync(Guid flowId, DateTime? verifiedBeforeUtc, int max, CancellationToken ct = default);

    Task RecordVerifyAsync(Guid flowId, DeliveryKey key, VerifyOutcome outcome, long? observedVersion, DateTime nowUtc, bool requeue, CancellationToken ct = default);

    /// <summary>
    /// Releases held, failed or deleted records: those with a pending document go back to pending for the worker,
    /// the others are unblocked and asked to be planned again by the flow's next run. Null keys means every blocked record.
    /// </summary>
    Task<int> ReleaseAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Forgets what OSDU holds for the records (the whole record, the metadata document or the payload) and asks the
    /// flow's next run to plan them again. Returns how many records were marked.
    /// </summary>
    Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey> keys, RedeliverScope scope, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Records what a removal did to a set of the flow's records, in one round trip. <see cref="RemovalScope.Record"/> and
    /// <see cref="RemovalScope.Everything"/> take the record out of OSDU, so the ledger marks it deleted and
    /// blocked and forgets the hashes; <see cref="RemovalScope.History"/> leaves the record live, so its custody
    /// state is untouched and only the attempt is written. Either way every record gets its own attempt, saying
    /// which scope ran and who asked for it, because that attempt is how the removal is audited afterwards.
    /// </summary>
    Task MarkRemovedAsync(Guid flowId, IReadOnlyList<DeliveryKey> keys, RemovalScope scope, string worker, DateTime nowUtc, string? correlationId = null, CancellationToken ct = default);

    /// <summary>
    /// The keys of every record a listing matches, in key order, up to <paramref name="max"/>. Key order is what
    /// makes this safe to act on: a removal changes the records it touches, and the newest-first order the listing
    /// pages in would shuffle rows between pages while the removal ran. Bounded like <see cref="ListAsync"/>: a prefix
    /// search reads at most <see cref="RecordListing.CountLimit"/> + 1 candidates per identity index, and a broad
    /// contains search is refused.
    /// </summary>
    Task<IReadOnlyList<DeliveryKey>> ListKeysAsync(Guid flowId, RecordQuery query, int max, CancellationToken ct = default);

    /// <summary>
    /// The id of the cache set holding exactly these values of <paramref name="scope"/>, creating it the first time it
    /// is seen. A render hands over what it consumed and gets back one number to put on the record, so no matter how many
    /// records a run stages, the dependency trail costs one row per distinct combination rather than one per record.
    /// </summary>
    Task<long> EnsureCacheSetAsync(string scope, IReadOnlyList<Snapshots.CacheUsage> usages, CancellationToken ct = default);

    /// <summary>The values behind one set, for a record's history page.</summary>
    Task<IReadOnlyList<CacheUse>> ListCacheSetAsync(long setId, CancellationToken ct = default);

    /// <summary>
    /// The sets that hold a cached value of the given items of one cache, with the value each of them holds. This is the
    /// impact query: it runs over the sets, never over the records, so it stays the same size as the cache.
    /// </summary>
    Task<IReadOnlyList<CacheSetUse>> FindCacheSetsAsync(string scope, string typeName, IReadOnlyList<string> itemIds, CancellationToken ct = default);

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

    /// <summary>The tags in a status, newest first; with a cache name, only the changes that cache's refreshes found.</summary>
    Task<IReadOnlyList<UpdateTag>> ListTagsAsync(string? status, int max, int offset, string? scope = null, CancellationToken ct = default);

    Task<int> CountTagsAsync(string? status, string? scope = null, CancellationToken ct = default);

    /// <summary>
    /// Decides tags: approving lets the rollout carry the change out, rejecting leaves the delivered documents
    /// alone. Either way the sets stop being gated unless another undecided tag still covers them.
    /// </summary>
    Task<int> DecideTagsAsync(IReadOnlyList<long> tagIds, bool approve, string actor, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Carries one batch of an approved tag: marks up to <paramref name="batchSize"/> of its records, of every flow, for
    /// redelivery in key and then flow order from the tag's cursor (asking each flow's next run to plan them again),
    /// advances the cursor and reports what is left. A change over millions of records is drained a batch at a time by a caller that decides
    /// the pace.
    /// </summary>
    Task<UpdateRolloutBatch> RollOutTagAsync(long tagId, int batchSize, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>The approved tags with rollout still to do, oldest decision first.</summary>
    Task<IReadOnlyList<UpdateTag>> ListRolloutQueueAsync(int max, CancellationToken ct = default);

    /// <summary>The watermark of one flow scope, or null when no whole-scope plan of it has completed.</summary>
    Task<SourceWatermark?> GetWatermarkAsync(Guid flowId, string scope, CancellationToken ct = default);

    /// <summary>Writes the watermark of one flow scope; a watermark never moves back to an earlier bound.</summary>
    Task SetWatermarkAsync(SourceWatermark watermark, CancellationToken ct = default);

    /// <summary>Removes attempts older than the cut-off, keeping the latest attempt of every flow's record.</summary>
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
