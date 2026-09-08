using SqlFlow.Delivery.Identity;

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
    Deleted,
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

    public string? MetadataHash { get; init; }

    public string? PayloadHash { get; init; }

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

    public string? PendingMetadataHash { get; init; }

    public string? PendingPayloadHash { get; init; }

    /// <summary>Drop-relative location of the pending payload chunks, or null when no payload is pending.</summary>
    public string? PendingPayloadLocation { get; init; }

    public bool PendingMetadata { get; init; }

    public bool PendingPayload { get; init; }

    /// <summary>
    /// Set when the record was held, failed or deleted and not released since. A blocked record is planned again
    /// only when its source changes (fingerprint moved) or an operator releases it.
    /// </summary>
    public bool Blocked { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }
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

    public long? TargetVersion { get; init; }

    public string? TargetId { get; init; }

    public DateTime? NextAttemptUtc { get; init; }

    public string? Error { get; init; }

    /// <summary>The target state to merge into the record (the returned identifiers), when the try changed it.</summary>
    public string? TargetStateJson { get; init; }

    /// <summary>The step progress to keep on the record for the next try (null clears it).</summary>
    public string? PendingStepJson { get; init; }
}

/// <summary>Tier-0 watermark: the Delta commit version of a source table for one flow scope (design.md section 6.6).</summary>
public sealed record SourceWatermark(Guid FlowId, string Scope, string Table, long Version, DateTime RecordedUtc);

/// <summary>The compact known-state row Databricks reads at the start of a run (design.md section 6.7).</summary>
public sealed record KnownState(DeliveryKey DeliveryKey, string SourceKey, string? SourceFingerprint, string? MetadataHash, string? PayloadHash, RecordStatus Status, string? TargetId, long? TargetVersion);

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

    /// <summary>Only delivered records whose last verify found drift or a missing record.</summary>
    public bool Drifted { get; init; }

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
    /// worker is delivering right now is left alone. Returns how many records were staged.
    /// </summary>
    Task<int> UpsertPendingAsync(IReadOnlyList<RecordState> records, CancellationToken ct = default);

    /// <summary>Records a tier-1 or tier-2 skip without queueing work: touches the submission pointer only.</summary>
    Task MarkSkippedAsync(Guid flowId, IEnumerable<DeliveryKey> keys, Guid submissionId, CancellationToken ct = default);

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

    /// <summary>Keeps a delivery's step progress on the record mid-try, so a crash after an upload never repeats it.</summary>
    Task SaveStepAsync(DeliveryKey key, string stepJson, CancellationToken ct = default);

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

    /// <summary>Records that a record was removed from OSDU: status deleted, hashes forgotten, an attempt written.</summary>
    Task MarkDeletedAsync(DeliveryKey key, bool purged, string worker, DateTime nowUtc, CancellationToken ct = default);

    Task<IReadOnlyList<KnownState>> KnownStateAsync(Guid flowId, CancellationToken ct = default);

    /// <summary>The known state of every record of the flow, streamed in key order in pages, for publications of any size.</summary>
    IAsyncEnumerable<KnownState> StreamKnownStateAsync(Guid flowId, int pageSize = 10_000, CancellationToken ct = default);

    Task<IReadOnlyList<SourceWatermark>> GetWatermarksAsync(Guid flowId, string scope, CancellationToken ct = default);

    Task SetWatermarksAsync(IEnumerable<SourceWatermark> watermarks, CancellationToken ct = default);

    /// <summary>Removes attempts older than the cut-off, keeping the latest attempt per record.</summary>
    Task<int> PruneAttemptsAsync(DateTime olderThanUtc, CancellationToken ct = default);

    Task<ActivityRecord> StartActivityAsync(ActivityRecord activity, CancellationToken ct = default);

    /// <summary>Closes an activity with its outcome, summary and captured log, and the submission it turned out to work on.</summary>
    Task CompleteActivityAsync(long activityId, string outcome, string? summary, string? log, DateTime completedUtc, Guid? submissionId = null, CancellationToken ct = default);

    Task<ActivityRecord?> GetActivityAsync(long activityId, CancellationToken ct = default);

    Task<IReadOnlyList<ActivityRecord>> ListActivitiesAsync(ActivityQuery query, CancellationToken ct = default);

    /// <summary>How many activities match a listing, for paging.</summary>
    Task<int> CountActivitiesAsync(ActivityQuery query, CancellationToken ct = default);
}
