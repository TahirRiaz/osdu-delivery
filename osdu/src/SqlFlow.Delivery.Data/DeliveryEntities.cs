using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SqlFlow.Delivery.Data;

// The delivery ledger (docs/delivery/ledger.md), in the osdu schema: submissions, records and append-only attempts, the
// audit trail of interventions, the source watermarks, and the read model of the mapping and cache documents the sync found in the repositories. Statuses are stored as short strings
// so the tables read without a decoder ring. Everything the GUI lists is index-backed. No foreign key or navigation
// reaches SQLFlow's catalog: a run, a group or a repository is referenced by id only.

/// <summary>
/// One plan of a flow over its ingestion tables: an incremental window, a full read, or a set of record keys. Its id is
/// the idempotency key, and every record it planned points back at it.
/// </summary>
public sealed class DeliverySubmission
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public Guid SubmissionId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public string MappingReference { get; set; } = string.Empty;

    public string RenderContext { get; set; } = string.Empty;

    public string ParametersJson { get; set; } = "{}";

    public long RecordCount { get; set; }

    /// <summary>The work location the intake wrote its batches under.</summary>
    public string? WorkLocation { get; set; }

    /// <summary>How many work batches the intake wrote.</summary>
    public int BatchCount { get; set; }

    /// <summary>How many key slices the intake was cut into for its fan-out; 1 for a plan that ran on one node.</summary>
    public int Slices { get; set; }

    public string Status { get; set; } = "received";

    public DateTime ReceivedUtc { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public long Planned { get; set; }

    public long SkippedUnchanged { get; set; }

    /// <summary>Records a cache change waiting for approval held back; rendered and ready, not sent until it is decided.</summary>
    public long AwaitingApproval { get; set; }

    /// <summary>Records the source carried in a version older than the one delivered or queued; skipped, never sent.</summary>
    public long SkippedStale { get; set; }

    /// <summary>Records whose queued document was, when its turn came, what OSDU already held; nothing was sent.</summary>
    public long UnchangedAtPush { get; set; }

    public long Blocked { get; set; }

    public long Delivered { get; set; }

    public long Held { get; set; }

    public long Failed { get; set; }

    /// <summary>Records of the submission still waiting, when it closed, for a record they refer to that has not landed.</summary>
    public long Waiting { get; set; }

    /// <summary>Records without a derivable delivery key: not planned, not delivered.</summary>
    public long Untracked { get; set; }

    public string? Error { get; set; }

    /// <summary>incremental (a window of changed rows), full (every row up to a bound) or keys (named record keys).</summary>
    public string Kind { get; set; } = "incremental";

    /// <summary>The source connection reference as the flow declares it; never a resolved value.</summary>
    public string SourceConnection { get; set; } = string.Empty;

    /// <summary>The record table's three-part name.</summary>
    public string SourceObject { get; set; } = string.Empty;

    /// <summary>The lower bound (exclusive) of the change window planned; null for a plan without a window.</summary>
    public DateTime? WindowFromUtc { get; set; }

    /// <summary>The upper bound (inclusive) of the change window planned; set for incremental and full plans.</summary>
    public DateTime? WindowToUtc { get; set; }

    /// <summary>What else bounded the read, as JSON: dataset objects, overlap, scope values, slice boundaries, key count
    /// and, for a keys plan, the digest of the keys.</summary>
    public string? SourceWindowJson { get; set; }

    /// <summary>The platform run that coordinated the plan.</summary>
    public Guid? RunId { get; set; }
}

/// <summary>
/// The current state of one deliverable, keyed by the flow that delivers it and its deterministic delivery key. The same
/// source row read by two flows is two records, each with its own state and history.
/// </summary>
public sealed class DeliveryRecord
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    public string SourceKey { get; set; } = string.Empty;

    /// <summary>The record's key tuple as a JSON array of strings, in the flow's key order: what a key-scoped read uses.</summary>
    public string? SourceKeyJson { get; set; }

    public string? Label { get; set; }

    public string MappingName { get; set; } = string.Empty;

    public string? RenderContext { get; set; }

    /// <summary>The ingestion fingerprint of the rows OSDU's document was built from.</summary>
    public string? SourceFingerprint { get; set; }

    /// <summary>When the source row OSDU's document was built from last changed (the flow's source.lastModified).</summary>
    public DateTime? SourceModifiedUtc { get; set; }

    /// <summary>The ingestion file the version OSDU holds came from.</summary>
    public string? SourceFileName { get; set; }

    /// <summary>The row of that file.</summary>
    public long? SourceRowNumber { get; set; }

    /// <summary>When the ingestion table last updated the row the version OSDU holds came from.</summary>
    public DateTime? SourceUpdatedUtc { get; set; }

    /// <summary>
    /// When the ingestion table first inserted the record's row, as the last plan that read it saw it: the row's arrival,
    /// which later changes never move. Null when the table does not carry it.
    /// </summary>
    public DateTime? SourceInsertedUtc { get; set; }

    public string? MetadataHash { get; set; }

    public string? PayloadHash { get; set; }

    /// <summary>The newest modified time among the chunk files OSDU's payload was delivered from.</summary>
    public DateTime? PayloadModifiedUtc { get; set; }

    public string? TargetId { get; set; }

    /// <summary>
    /// The OSDU id this record claimed for its flow when it first queued a document, kept for good afterwards. One OSDU
    /// record belongs to one flow: no other flow's record can claim the same id. A record that was only ever held has
    /// claimed nothing.
    /// </summary>
    public string? ClaimedTargetId { get; set; }

    public long? TargetVersion { get; set; }

    public string Status { get; set; } = "pending";

    public DateTime? LastDeliveredUtc { get; set; }

    public DateTime? LastVerifiedUtc { get; set; }

    public string? LastVerifyOutcome { get; set; }

    /// <summary>
    /// The token of the lease the record is being delivered under (<see cref="DeliveryLease"/>), set once when a worker
    /// claims it and cleared when the lease applies its outcome or hands it back. The lease holds the expiry.
    /// </summary>
    public string? LeaseOwner { get; set; }

    public Guid? LastSubmissionId { get; set; }

    public int AttemptCount { get; set; }

    public DateTime? NextAttemptUtc { get; set; }

    public string? LastError { get; set; }

    /// <summary>Where the pending document sits in the submission's work batches (batch:offset:length).</summary>
    public string? PendingDocumentRef { get; set; }

    /// <summary>The work batch the pending document was written in.</summary>
    public int? WorkBatch { get; set; }

    /// <summary>The identifiers the target returned for what it holds now (a JSON object merged step by step).</summary>
    public string? TargetStateJson { get; set; }

    /// <summary>The completed steps of the pending delivery and what they returned (a JSON object keyed by step).</summary>
    public string? PendingStepJson { get; set; }

    /// <summary>
    /// The cache values this record was built from, as the id of the set it shares with every other record that
    /// read the same values (<see cref="DeliveryCacheSet"/>). One column rather than a row per dependency: at
    /// estate scale the records number in the hundreds of millions and the distinct sets in the thousands.
    /// </summary>
    public long? CacheSetId { get; set; }

    public string? PendingRenderContext { get; set; }

    public string? PendingSourceFingerprint { get; set; }

    public DateTime? PendingSourceModifiedUtc { get; set; }

    /// <summary>The ingestion file the queued version came from.</summary>
    public string? PendingSourceFileName { get; set; }

    /// <summary>The row of that file.</summary>
    public long? PendingSourceRowNumber { get; set; }

    /// <summary>When the ingestion table last updated the row the queued version came from.</summary>
    public DateTime? PendingSourceUpdatedUtc { get; set; }

    public string? PendingMetadataHash { get; set; }

    public string? PendingPayloadHash { get; set; }

    public DateTime? PendingPayloadModifiedUtc { get; set; }

    public string? PendingPayloadLocation { get; set; }

    public bool PendingMetadata { get; set; }

    public bool PendingPayload { get; set; }

    /// <summary>
    /// The OSDU ids the pending document refers to through the properties its template declares relationships for, each
    /// with the property that holds it, as a JSON array (docs/interfaces-design.md section 7). Written with the pending
    /// document and cleared with it, so only the work still to be sent keeps them.
    /// </summary>
    public string? PendingReferences { get; set; }

    public bool Blocked { get; set; }

    /// <summary>
    /// While the record is blocked, held or failed: the problem that keeps it so, as the hash of its last error with every
    /// part that names the record replaced (a value, an id, a moment, a number), which every record refused for the same
    /// reason shares. Null for any other record, so the index that groups blocked records by problem holds them alone.
    /// </summary>
    public long? ProblemHash { get; set; }

    /// <summary>
    /// What the last check of the record's document against its schema came to, when the gate before a record is sent
    /// checked it: <c>valid</c>, <c>invalid</c> or <c>unverified</c>. Null until the gate has checked a document of the
    /// record. A flow's records are counted by it (docs/validation-plan.md).
    /// </summary>
    public string? ValidationOutcome { get; set; }

    /// <summary>How many problems that check found.</summary>
    public long? ValidationProblems { get; set; }

    /// <summary>When that check was made.</summary>
    public DateTime? ValidatedUtc { get; set; }

    /// <summary>
    /// The metadata hash of the pending document an operator's release accepted as it is: the gate sends that document
    /// whatever its verdict says. A document rendered differently has another hash and is judged again.
    /// </summary>
    public string? AcceptedMetadataHash { get; set; }

    /// <summary>
    /// While the record is waiting: the OSDU id of the record it waits for, which another record of the ledger holds and
    /// has not delivered. The record goes back to pending when that one lands.
    /// </summary>
    public string? WaitingFor { get; set; }

    /// <summary>When the ledger asked for the record to be planned again (a redeliver, a release, a cache rollout); null
    /// once a plan took it.</summary>
    public DateTime? PlanRequestedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>One delivery try, append-only: the record's history.</summary>
public sealed class DeliveryAttempt
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long AttemptId { get; set; }

    /// <summary>The flow of the record the try belongs to; with the delivery key, the record's identity.</summary>
    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    public Guid? SubmissionId { get; set; }

    /// <summary>The platform run the attempt happened in, when it did (operator actions carry none).</summary>
    public Guid? RunId { get; set; }

    public string Worker { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime CompletedUtc { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string Phase { get; set; } = string.Empty;

    public string? MetadataHash { get; set; }

    public string? PayloadHash { get; set; }

    public long? TargetVersion { get; set; }

    public string? Error { get; set; }

    /// <summary>The steps of the try and what the target answered, as JSON.</summary>
    public string? ResultJson { get; set; }

    /// <summary>The work batch the try belonged to, when it ran from one.</summary>
    public int? WorkBatch { get; set; }

    /// <summary>The ingestion file the attempt's document was built from; kept here because the record's own origin
    /// columns move on with later versions.</summary>
    public string? SourceFileName { get; set; }

    /// <summary>The row of that file.</summary>
    public long? SourceRowNumber { get; set; }

    /// <summary>When the ingestion table last updated that row.</summary>
    public DateTime? SourceUpdatedUtc { get; set; }

    /// <summary>When the ingestion table marked that row deleted, for the hold of a deleted row; null otherwise.</summary>
    public DateTime? SourceDeletedUtc { get; set; }
}

/// <summary>One work batch of a submission: a file of rendered documents, claimed and drained as one unit.</summary>
public sealed class DeliveryWorkBatch
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public Guid SubmissionId { get; set; }

    public int Index { get; set; }

    public Guid FlowId { get; set; }

    public string Location { get; set; } = string.Empty;

    public int RecordCount { get; set; }

    /// <summary>queued, running, done, failed.</summary>
    public string Status { get; set; } = "queued";

    /// <summary>The token of the lease a worker drains the batch under (<see cref="DeliveryLease"/>), while it runs.</summary>
    public string? LeaseOwner { get; set; }

    public Guid? RunId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public long Delivered { get; set; }

    public long Held { get; set; }

    public long Failed { get; set; }

    public long Retrying { get; set; }

    /// <summary>Records of the batch its claim found waiting for a record they refer to, and did not send.</summary>
    public long Waiting { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// A worker's hold on work: one row per claim, a work batch or a group of records due for a retry. The worker renews
/// this one row while it delivers, however many records the claim holds, and the records carry its token. A lease that
/// ran out belongs to nobody alive, and the next claim of its flow recovers it: it applies what the worker appended and
/// hands the rest of the work back.
/// </summary>
public sealed class DeliveryLease
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    /// <summary>The claim's token: the worker's name and a fresh id.</summary>
    public string Token { get; set; } = string.Empty;

    public Guid FlowId { get; set; }

    /// <summary>The submission the claim was made for: the batch's, or the one a retry claim named.</summary>
    public Guid? SubmissionId { get; set; }

    /// <summary>The work batch the lease drains, or null for a group of records due for a retry.</summary>
    public int? WorkBatch { get; set; }

    /// <summary>Who holds the lease: the worker that claimed it, or the one recovering it.</summary>
    public string Owner { get; set; } = string.Empty;

    public Guid? RunId { get; set; }

    public DateTime AcquiredUtc { get; set; }

    public DateTime ExpiresUtc { get; set; }
}

/// <summary>
/// One value a record is findable by: a wellbore id, a well name, a key value, the OSDU id or its trailing part, a word
/// of the label, or the ingestion file it came from. The token is folded to upper case, so a lookup compares folded
/// terms and one index answers "starts with" for every identifier an operator may hold, across every flow. Rows belong
/// to their record: they are rewritten whenever its identity changes, and deleted with it.
/// </summary>
public sealed class DeliveryRecordIdentity
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>The value folded to upper case: what a lookup seeks.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The value as it was read, for showing beside a hit.</summary>
    public string Display { get; set; } = string.Empty;

    /// <summary>Where the token came from: identity, key, label, osdu or file, as the ledger's identity kinds name them.</summary>
    public string Kind { get; set; } = string.Empty;
}

/// <summary>
/// What a worker appended while it delivered under a lease, not yet applied to the record: a step that completed, or the
/// outcome of a try. Rows are only ever added. The lease applies them to their records when the worker checkpoints or
/// closes it, or when the lease is recovered, and deletes them in the same transaction, so each is applied once. The
/// try's permanent history is its <see cref="DeliveryAttempt"/>, written with the outcome.
/// </summary>
public sealed class DeliveryRecordEvent
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long EventId { get; set; }

    public string LeaseToken { get; set; } = string.Empty;

    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>step or completion.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>When the step completed or the try ended.</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>A step: every step of the pending work completed so far and what each returned (a JSON object keyed by step).</summary>
    public string? StepJson { get; set; }

    /// <summary>A completion: the status the try settles the record in.</summary>
    public string? Status { get; set; }

    public bool Promote { get; set; }

    public bool NothingSent { get; set; }

    public DateTime? NextAttemptUtc { get; set; }

    public string? Error { get; set; }

    /// <summary>A completion that holds or fails the record: the problem its error names, which the record keeps while it is blocked.</summary>
    public long? ProblemHash { get; set; }

    /// <summary>A completion of a try that checked its document: the outcome the record keeps; null leaves the record's as it was.</summary>
    public string? ValidationOutcome { get; set; }

    /// <summary>How many problems that check found.</summary>
    public long? ValidationProblems { get; set; }

    /// <summary>When that check was made.</summary>
    public DateTime? ValidatedUtc { get; set; }

    public string? TargetId { get; set; }

    public long? TargetVersion { get; set; }

    /// <summary>The target state the record holds after the try, when the try changed it.</summary>
    public string? TargetStateJson { get; set; }

    /// <summary>The step progress the record keeps for its next try; null clears it.</summary>
    public string? PendingStepJson { get; set; }

    // The pending work the try carried, as it stood when the record was claimed: a step belongs to it, and a completion
    // promotes it when newer work was queued behind the try. A null document reference means the try carried none.

    public Guid? ClaimSubmissionId { get; set; }

    public string? ClaimDocumentRef { get; set; }

    public string? ClaimRenderContext { get; set; }

    public string? ClaimSourceFingerprint { get; set; }

    public DateTime? ClaimSourceModifiedUtc { get; set; }

    public string? ClaimSourceFileName { get; set; }

    public long? ClaimSourceRowNumber { get; set; }

    public DateTime? ClaimSourceUpdatedUtc { get; set; }

    public string? ClaimMetadataHash { get; set; }

    public string? ClaimPayloadHash { get; set; }

    public DateTime? ClaimPayloadModifiedUtc { get; set; }

    public bool ClaimMetadata { get; set; }

    public bool ClaimPayload { get; set; }
}

/// <summary>
/// The watermark of one flow scope (the parameter set): the upper bound of the last whole-scope plan that completed, so
/// the next incremental plan reads the rows the ingestion tables changed after it.
/// </summary>
public sealed class DeliverySourceWatermark
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public Guid FlowId { get; set; }

    public string Scope { get; set; } = string.Empty;

    /// <summary>The upper bound of the change window the last completed whole-scope plan covered.</summary>
    public DateTime UpdatedThroughUtc { get; set; }

    /// <summary>The submission whose completion wrote the watermark.</summary>
    public Guid SubmissionId { get; set; }

    /// <summary>
    /// The render context the last plan of this scope used. The whole-run gate compares it, so a cache, mapping or
    /// schema version that moved re-renders the scope even when no source row changed.
    /// </summary>
    public string? ContextHash { get; set; }

    public DateTime RecordedUtc { get; set; }
}

/// <summary>The audit trail of runs and interventions: who did what, when, with which inputs, and the outcome.</summary>
public sealed class DeliveryActivity
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long ActivityId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string Outcome { get; set; } = "running";

    public string? ParametersJson { get; set; }

    public Guid? SubmissionId { get; set; }

    public Guid? DeliveryKey { get; set; }

    /// <summary>The platform run the activity ran as, when it was a run (deliver, plan, verify, replan).</summary>
    public Guid? RunId { get; set; }

    public string? Summary { get; set; }

    public string? Log { get; set; }

    /// <summary>
    /// A run that completed having changed nothing: it planned, held, blocked and sent no record, and left none waiting.
    /// The schedule fired and found nothing new. The audit trail leaves these out unless asked for them, and says how many
    /// it left out; an intervention is never idle.
    /// </summary>
    public bool Idle { get; set; }
}

/// <summary>
/// One record an intervention changed, written in the statement that changed it. An intervention made for one record names
/// it on its <see cref="DeliveryActivity"/>; one made for many (a release of a whole flow, or of every record a problem
/// keeps blocked) names each record here, so a record's history holds every request that reached it, with who asked and
/// when, however many records the request reached.
/// </summary>
public sealed class DeliveryActivityRecord
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>The intervention, as the audit trail numbers it.</summary>
    public long ActivityId { get; set; }
}

/// <summary>
/// A record deleted from the ledger after it was removed from OSDU (docs/ledger.md, Deleting a removed record from the
/// ledger): its attempts, search entries and row went, and this line is what the ledger keeps of it, so who deleted which
/// record, with which OSDU id and last version, is still answered from the ledger. Rows are only ever added; the
/// intervention that deleted it names it under <see cref="DeliveryActivityRecord"/> as well.
/// </summary>
public sealed class DeliveryPurgedRecord
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long PurgedRecordId { get; set; }

    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>The record's key in its source, as the ledger held it.</summary>
    public string SourceKey { get; set; } = string.Empty;

    public string? Label { get; set; }

    /// <summary>The OSDU id the record was delivered and removed under; null for a record that never had one.</summary>
    public string? TargetId { get; set; }

    /// <summary>The last OSDU version an attempt of the record named, before it was removed.</summary>
    public long? LastVersion { get; set; }

    /// <summary>How many attempts were deleted with it.</summary>
    public int Attempts { get; set; }

    /// <summary>The intervention that deleted it, as the audit trail numbers it.</summary>
    public long? ActivityId { get; set; }

    public string PurgedBy { get; set; } = string.Empty;

    public DateTime PurgedUtc { get; set; }
}

/// <summary>
/// One reversal: what one run or one submission put into OSDU, put back record by record as OSDU held it before
/// (docs/reversal-plan.md). There is one per source of a ledger; asking again resumes it. Its counts are read from its
/// items, never kept here.
/// </summary>
public sealed class DeliveryReversal
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long ReversalId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    /// <summary>run or submission.</summary>
    public string SourceKind { get; set; } = string.Empty;

    /// <summary>The run or the submission reversed.</summary>
    public Guid SourceId { get; set; }

    /// <summary>The submissions the source covers, as a JSON array of ids, fixed when the reversal was opened.</summary>
    public string SubmissionsJson { get; set; } = "[]";

    /// <summary>capturing, reversing, completed, failed or cancelled.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Who asked for the reversal first.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    public DateTime RequestedUtc { get; set; }

    /// <summary>When every record the source delivered was listed as an item; null while the listing is not finished.</summary>
    public DateTime? CapturedUtc { get; set; }

    /// <summary>When the latest run working on it started.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>When the latest run working on it ended.</summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>The latest run that worked on it.</summary>
    public Guid? LastRunId { get; set; }

    /// <summary>Why the latest run stopped, redacted; null when it did not fail.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// One record a reversal reaches: what its source delivered of it, what OSDU held before, and what the reversal did. Written
/// when the reversal lists its source, and settled in the transaction that changes the record.
/// </summary>
public sealed class DeliveryReversalItem
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long ReversalId { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>The OSDU id the record claimed when the reversal listed it; null for a record that claimed none.</summary>
    public string? TargetId { get; set; }

    /// <summary>The source's first delivered attempt of the record.</summary>
    public long FirstAttemptId { get; set; }

    /// <summary>The version that attempt wrote.</summary>
    public long? FirstVersion { get; set; }

    /// <summary>The version the source's last delivered attempt of the record left.</summary>
    public long? RunVersion { get; set; }

    /// <summary>What OSDU held before the source: version, none or unknown.</summary>
    public string Prior { get; set; } = string.Empty;

    /// <summary>With <see cref="Prior"/> version: the version OSDU held.</summary>
    public long? PriorVersion { get; set; }

    /// <summary>The attempt that delivered or restored the version OSDU held before, when the ledger still has it: its hashes and origin are what the record gets back.</summary>
    public long? PriorAttemptId { get; set; }

    /// <summary>pending, sending, done, skipped or failed.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>What came of it once settled: restored, removed, already-gone, or why it was passed over or failed.</summary>
    public string? Outcome { get; set; }

    /// <summary>The reason, redacted.</summary>
    public string? Detail { get; set; }

    /// <summary>The version a restore put back.</summary>
    public long? RestoredVersion { get; set; }

    /// <summary>The version OSDU gave the restored record.</summary>
    public long? NewVersion { get; set; }

    /// <summary>The reverse run that settled it, or marked it sending.</summary>
    public Guid? RunId { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>A mapping document as the sync found it in a repository: the read model behind the mappings page.</summary>
public sealed class DeliveryMapping
{
    /// <summary>Stable id: derived from the repo id and the mapping reference.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>Name@version.</summary>
    public string Reference { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>The OSDU kind of the template the mapping fills.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The template version the mapping pins, also for a document that fails to load; empty when it names none.</summary>
    public string TemplateVersion { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The document text, secret-redacted, as it was when synced.</summary>
    public string Yaml { get; set; } = string.Empty;

    /// <summary>A parsed summary (template, dataset system, key and label, child datasets, entry and fixture counts, envelope), for listings.</summary>
    public string SummaryJson { get; set; } = "{}";

    /// <summary>valid or invalid.</summary>
    public string Status { get; set; } = "valid";

    public string? Message { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// One interface of a delivery flow document, as the repository sync found it (docs/interfaces-design.md section 4): the
/// pipeline it belongs to, the ledger identity its records, submissions and statistics are kept under, and what it delivers
/// and how. The API and the GUI find the pipeline of a ledger identity here, and the interfaces of a pipeline, without
/// parsing a document. A document in the single form has one row, whose interface name is empty. A document that names its
/// partitions has one row per interface and partition, each with the partition's ledger (docs/partitions-design.md section
/// 4). The row of an interface the repository no longer declares is kept, inactive, so the records it delivered still lead
/// to their flow.
/// </summary>
public sealed class DeliveryInterface
{
    /// <summary>The longest interface name.</summary>
    public const int MaxInterfaceLength = 64;

    /// <summary>
    /// Stable id: derived from the repository, the flow's name and the interface's name, and for a flow that names its
    /// partitions the partition's name as well.
    /// </summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The flow's name: its pipeline in the repository.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The interface's name; empty for a document in the single form.</summary>
    public string Interface { get; set; } = string.Empty;

    /// <summary>The partition whose ledger the row describes; empty for a flow that names no partitions.</summary>
    public string Partition { get; set; } = string.Empty;

    /// <summary>Where the interface is in its document, from 0.</summary>
    public int Ordinal { get; set; }

    /// <summary>The ledger identity (the flow id every ledger row of the interface carries).</summary>
    public Guid LedgerFlowId { get; set; }

    /// <summary>The name the ledger identity is derived from: the flow, <c>flow/interface</c>, or the ledger it adopts.</summary>
    public string LedgerName { get; set; } = string.Empty;

    /// <summary>The route the interface is delivered by: storage, file, dataset, manifest, ddms, fileAndDdms, manifestAndDdms or workflow.</summary>
    public string Route { get; set; } = string.Empty;

    /// <summary>Why it goes by that route; null for the single form, whose document names its protocol.</summary>
    public string? RouteReason { get; set; }

    /// <summary>The mapping it pins (Name@version).</summary>
    public string MappingReference { get; set; } = string.Empty;

    /// <summary>The OSDU kind the mapping fills, when the repository holds a valid mapping of that reference; empty otherwise.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The record table it reads.</summary>
    public string RecordObject { get; set; } = string.Empty;

    /// <summary>The interfaces of the document it waits for (<c>after:</c>), as a JSON array of names.</summary>
    public string AfterJson { get; set; } = "[]";

    public string RelativePath { get; set; } = string.Empty;

    /// <summary>False once the repository no longer declares the interface.</summary>
    public bool Active { get; set; } = true;

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// A template: the OSDU schema of one kind, captured from OSDU or imported from a file, which every mapping for that
/// kind is checked and rendered against (docs/delivery/mapping-templates.md). Templates are owned by OSDU Delivery. A
/// version is identified by the kind and the hash of its schema, and never changes; a mapping pins the version it fills.
/// </summary>
public sealed class DeliveryTemplate
{
    /// <summary>Stable id: derived from the kind and the version.</summary>
    public Guid Id { get; set; }

    /// <summary>The OSDU kind the schema describes (osdu:wks:work-product-component--WellLog:1.4.0).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The content version: the hash prefix of the canonical bundled schema.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The bundled JSON Schema, every reference resolved into its definitions.</summary>
    public string SchemaJson { get; set; } = string.Empty;

    /// <summary>Where the schema came from: the flow and endpoint reference it was captured through, or the imported file.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>Who captured or imported it.</summary>
    public string CapturedBy { get; set; } = string.Empty;

    public DateTime CapturedUtc { get; set; }
}

/// <summary>
/// A cached OSDU type as one cache flow (<c>flowType: cache</c>) declares it: which records it captures into its partition's
/// cache and which paths of each it keeps. The cache flow's YAML in the repository is the only place what is cached is
/// defined; the sync copies the declaration here, so a refresh knows every path the partition keeps for a type and the GUI
/// can show which files fill a cache. Several flows may declare the same type for one partition: the cache holds their union.
/// </summary>
public sealed class DeliveryCacheDefinition
{
    /// <summary>Stable id: derived from the repo id, the cache flow name and the cached type name.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The cache flow that declares the type.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>
    /// The partition whose cache the flow fills: one of the partitions it names (<c>partitions</c>), or for a flow that names
    /// none, its <c>source.headers.data-partition-id</c> resolved.
    /// </summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    /// True when the flow works in partitions (docs/partitions-design.md section 2.2): it names the partitions it builds a
    /// cache for, or leaves them to the registry. It then has a row per type and partition it serves, and a refresh of it
    /// names the partition it builds. False for a flow whose partition is its header's.
    /// </summary>
    public bool DeclaresPartitions { get; set; }

    /// <summary>
    /// Where the type's records come from: <c>osdu</c> (searched on the platform), <c>table</c> (an ingestion table) or
    /// <c>dictionary</c> (a dictionary document in the repository). Rows written before origins existed are osdu.
    /// </summary>
    public string Origin { get; set; } = "osdu";

    /// <summary>For an OSDU type: the endpoint the flow searches, as declared (a reference, never a resolved value).</summary>
    public string? Endpoint { get; set; }

    /// <summary>For a table type: the flow's source connection, as declared (a reference, never a resolved value).</summary>
    public string? Connection { get; set; }

    /// <summary>For a table type: the three-part name of the ingestion table read.</summary>
    public string? SourceObject { get; set; }

    /// <summary>For a table or dictionary type: the name each row's key is kept under.</summary>
    public string? KeyField { get; set; }

    /// <summary>For a dictionary type: the dictionary document's file, relative to the repository root.</summary>
    public string? DictionaryPath { get; set; }

    /// <summary>The cache flow's file, relative to the repository root.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>The short name mappings use (UnitOfMeasure).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The OSDU entity type (reference-data--UnitOfMeasure), or lookup--&lt;Name&gt; for a table or dictionary type.</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>For an OSDU type: the search kind the capture sweeps.</summary>
    public string? Kind { get; set; }

    /// <summary>For an OSDU type: the search query narrowing the capture.</summary>
    public string? Query { get; set; }

    /// <summary>
    /// What the cache keeps of each record as JSON: <c>[{ "path": "data.Code", "as": "Code" }]</c>, the path a column for a
    /// table type, and for a dictionary type the fields its document names.
    /// </summary>
    public string FieldsJson { get; set; } = "[]";

    /// <summary>approve or auto: what a changed cached value of this type does to the records already built from it.</summary>
    public string OnChange { get; set; } = "auto";

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// One version of a partition's cache: the whole cache as one merge left it, written by the cache flow whose capture moved
/// it. A version is never rewritten. A capture that changes what the cache holds writes the next version, which becomes
/// current, and one that changes nothing writes none, because a new version moves the render context of every record built
/// against the cache. Every version stays readable for as long as the ledger exists: a delivered record's render context
/// names the version it was rendered against, and the ledger has to be able to show what that version held.
/// </summary>
public sealed class DeliveryCacheVersion
{
    /// <summary>Stable id: derived from the partition and the version label.</summary>
    public Guid Id { get; set; }

    /// <summary>The partition whose cache the version belongs to.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The cache flow whose capture, or import, wrote the version.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The label, minted from the capture instant (20260910T165153Z), with the sequence appended when two captures share a second.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The version's place in the partition's history, 1 for the first; the ranges of <see cref="DeliveryCacheItem"/> count in it.</summary>
    public int Sequence { get; set; }

    public DateTime CapturedUtc { get; set; }

    /// <summary>Hash of the whole content, checked on every load so a version altered after it was written is refused.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The version that was current when this one was written, which the capture was merged onto; null for the first.</summary>
    public string? PreviousVersion { get; set; }

    /// <summary>Whether this is the newest version, the one deliveries render against unless a flow pins another.</summary>
    public bool Current { get; set; }

    /// <summary>The platform run that captured the version; null for one imported from files.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Who asked for it: the run's trigger (manual:&lt;user&gt;, schedule:&lt;name&gt;), or cli:&lt;user&gt; for an import.</summary>
    public string CapturedBy { get; set; } = string.Empty;

    /// <summary>Where the content came from: the OSDU endpoint reference a capture searched, or the directory an import read.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>The types the version holds, by name, each with its entity type and record count: <c>[{ "name", "entityType", "items" }]</c>.</summary>
    public string TypesJson { get; set; } = "[]";

    /// <summary>
    /// The partition's system properties the capture found, kept apart from the cached records because they describe the
    /// platform rather than any record: <c>[{ "service", "name", "state", "source", "detail" }]</c>. Empty for a version
    /// written before captures recorded them, or by an import onto a cache no capture had asked about.
    /// </summary>
    public string SystemPropertiesJson { get; set; } = "[]";

    /// <summary>How many cached records the version holds across its types.</summary>
    public long Items { get; set; }
}

/// <summary>
/// One cached record as a run of consecutive versions of its partition's cache held it: the OSDU id and the values captured
/// at the declared paths, valid from the version at <see cref="FromSequence"/> up to, and not including, the one at
/// <see cref="ToSequence"/>, which is null while the newest version still holds it unchanged. A record is stored once per
/// partition however many cache flows capture it, and a merge writes rows only for the records that changed, arrived or
/// left, so keeping every version costs rows in proportion to what moved rather than to the size of the cache times the
/// number of captures.
/// </summary>
public sealed class DeliveryCacheItem
{
    public long ItemId { get; set; }

    /// <summary>The partition whose cache holds the record.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The cached type's short name (UnitOfMeasure).</summary>
    public string TypeName { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    /// <summary>The OSDU record id, without a version.</summary>
    public string RecordId { get; set; } = string.Empty;

    /// <summary>The captured values as JSON, in whatever shape the paths yielded, names in ordinal order.</summary>
    public string FieldsJson { get; set; } = "{}";

    /// <summary>Every scalar the item holds, newline separated: what a search over cached values matches on.</summary>
    public string Terms { get; set; } = string.Empty;

    /// <summary>The sequence of the first version that holds the record with these values.</summary>
    public int FromSequence { get; set; }

    /// <summary>The sequence of the first version that no longer holds it so; null while the newest version still does.</summary>
    public int? ToSequence { get; set; }
}

/// <summary>
/// That a cache flow's last capture of a type held a record. It is what lets several flows share one partition's cache: a
/// record a flow no longer finds leaves the cache only when no other flow's capture still holds it. Current state, not
/// history; what each version held is in <see cref="DeliveryCacheItem"/>.
/// </summary>
public sealed class DeliveryCacheMember
{
    /// <summary>The partition whose cache holds the record.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The cached type's short name (UnitOfMeasure).</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>The OSDU record id, without a version.</summary>
    public string RecordId { get; set; } = string.Empty;

    /// <summary>The cache flow whose last capture of the type held the record.</summary>
    public string FlowName { get; set; } = string.Empty;
}

/// <summary>
/// One distinct combination of cached values that records were built from: the set of (type, cached record, path,
/// value) a render consumed. Records share sets heavily (every log that resolved metres and the same wellbore
/// shares one), so the estate's dependency trail is a few thousand sets rather than a row per record per value.
/// A record points at its set; a cache change finds the sets that hold the changed value and, through them, the
/// records to update.
/// </summary>
public sealed class DeliveryCacheSet
{
    public long SetId { get; set; }

    /// <summary>Content hash of the set's entries: the identity a render computes without a round trip.</summary>
    public string SetHash { get; set; } = string.Empty;

    public int EntryCount { get; set; }

    /// <summary>
    /// Set while a change to one of these values is tagged and unapproved. The planner reads the gated sets once
    /// per run and skips their records, so holding records back never means writing to them.
    /// </summary>
    public bool Gated { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>One cached value inside a set: what was read, and what it read at the time.</summary>
public sealed class DeliveryCacheSetEntry
{
    public long SetId { get; set; }

    /// <summary>The partition whose cache the value was read from: the partition the delivery flow delivers to.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The cached type's short name (UnitOfMeasure).</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>The cached record's OSDU id.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>What the mapping read: <c>id</c>, <c>Name</c>, <c>NameAlias.AliasName</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>match (the value the source resolved by) or value (a value written into the document).</summary>
    public string Kind { get; set; } = "value";

    public string ValueHash { get; set; } = string.Empty;

    /// <summary>The value as it was consumed, truncated for display.</summary>
    public string ValueText { get; set; } = string.Empty;
}

/// <summary>
/// One change to the cache that delivered records were built from, and what happens about it. A tag is written per
/// change, not per record: a corrected unit name is one decision covering every record that read it, with the count
/// of what it affects. Under <c>approve</c> the affected sets are gated until someone decides; under <c>auto</c> it
/// is approved as written. Either way the update is rolled out in batches from <see cref="Cursor"/>, so a change
/// touching millions of records drains at a controlled rate instead of flooding the estate.
/// </summary>
public sealed class DeliveryUpdateTag
{
    public long TagId { get; set; }

    /// <summary>What caused the tag; <c>cache</c> today.</summary>
    public string Kind { get; set; } = "cache";

    /// <summary>The partition whose cache the change was found in.</summary>
    public string Scope { get; set; } = string.Empty;

    public string TypeName { get; set; } = string.Empty;

    public string ItemId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// changed (the value moved, or a path that gave none gives one), removed (the cached record is gone), unmatched (what it
    /// matched by is gone) or listed (a lookup table now lists a key records looked up and found no row under).
    /// </summary>
    public string Change { get; set; } = "changed";

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public string? FromVersion { get; set; }

    public string ToVersion { get; set; } = string.Empty;

    /// <summary>auto or approve, as the cache declared for this type when the tag was written.</summary>
    public string Mode { get; set; } = "approve";

    /// <summary>pending, approved, rejected, rolling or applied.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>The cache sets holding the changed value, comma separated; the records are found through them.</summary>
    public string SetIds { get; set; } = string.Empty;

    /// <summary>How many delivered records were built from the old value when the change was found.</summary>
    public long AffectedRecords { get; set; }

    /// <summary>How many of them the rollout has marked for redelivery so far.</summary>
    public long Processed { get; set; }

    /// <summary>
    /// Where the rollout got to, in delivery-key order and then flow order, so a pass resumes rather than restarts: the
    /// delivery key of the last record marked.
    /// </summary>
    public Guid? Cursor { get; set; }

    /// <summary>The flow of the last record marked; with <see cref="Cursor"/>, where the rollout got to.</summary>
    public Guid? CursorFlowId { get; set; }

    public DateTime DetectedUtc { get; set; }

    public DateTime? DecidedUtc { get; set; }

    public string? DecidedBy { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }
}

/// <summary>One retrieval run: the window it covered, where its files went, and its outcome.</summary>
public sealed class DeliveryRetrieval
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long RetrievalId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public Guid? RunId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>The kinds the run covered, comma separated.</summary>
    public string Kinds { get; set; } = string.Empty;

    /// <summary>The query as it ran, window included.</summary>
    public string? Query { get; set; }

    public string? WindowField { get; set; }

    public DateTime? WindowFrom { get; set; }

    public DateTime? WindowTo { get; set; }

    public string Location { get; set; } = string.Empty;

    public string? ManifestLocation { get; set; }

    public string Status { get; set; } = "running";

    public long Records { get; set; }

    public int Files { get; set; }

    /// <summary>Uncompressed bytes written.</summary>
    public long Bytes { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// One run of an assertion flow's tests in one partition (docs/assertions-design.md section 7): the tests it ran, how they
/// came out, and who asked. The results of each test are rows of <see cref="DeliveryAssertionResult"/>.
/// </summary>
public sealed class DeliveryAssertionRun
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long AssertionRunId { get; set; }

    /// <summary>The ledger identity of the flow in the partition.</summary>
    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    /// <summary>The platform run the tests ran in.</summary>
    public Guid? RunId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>The tests the run was asked for, as JSON (an object of tests and tags); null when it ran every test.</summary>
    public string? Selection { get; set; }

    /// <summary>running, passed, failed, errored or cancelled.</summary>
    public string Status { get; set; } = "running";

    /// <summary>The tests the run took up, those skipped included.</summary>
    public int Tests { get; set; }

    public int Passed { get; set; }

    public int Failed { get; set; }

    public int Warned { get; set; }

    public int Errored { get; set; }

    public int Skipped { get; set; }

    /// <summary>A hash of the tests the run ran, as their definitions stood: two runs of the same tests hash the same.</summary>
    public string? DefinitionsHash { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// One test's result in one run of an assertion flow: its outcome, what it read and matched, and every assertion with what
/// it expected, what it found and the records that failed it (<see cref="Detail"/>, JSON).
/// </summary>
public sealed class DeliveryAssertionResult
{
    /// <summary>The partition the row belongs to: the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long ResultId { get; set; }

    public long AssertionRunId { get; set; }

    /// <summary>The ledger identity of the flow in the partition.</summary>
    public Guid FlowId { get; set; }

    public string TestName { get; set; } = string.Empty;

    /// <summary>The OSDU kind the test reads.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>passed, failed, warned, errored or skipped.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>The heaviest severity among the assertions that failed; null when none did.</summary>
    public string? Severity { get; set; }

    /// <summary>The records the test matched (the index's count, or the ids storage holds); null when it did not get as far.</summary>
    public long? Matched { get; set; }

    /// <summary>The records the test read and held its assertions to.</summary>
    public long? Evaluated { get; set; }

    /// <summary>True when more records matched than the test reads, and it evaluated the first ones as a sample.</summary>
    public bool Sampled { get; set; }

    public int Assertions { get; set; }

    public int FailedAssertions { get; set; }

    /// <summary>The hash of the test's definition when it ran, so a result says whether the test has changed since.</summary>
    public string DefinitionHash { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public string? Error { get; set; }

    /// <summary>The whole result as JSON: every assertion, what it expected and found, and its failing examples.</summary>
    public string Detail { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime CompletedUtc { get; set; }
}

/// <summary>
/// One dimension of a dimension flow in one partition (docs/dimension-plan.md): the declaration its last build read with, the
/// field it asked the index for, and how many members and originals it holds now. Its members, originals, builds and
/// changes are rows of <see cref="DeliveryDimensionMember"/>, <see cref="DeliveryDimensionValue"/>,
/// <see cref="DeliveryDimensionRun"/> and <see cref="DeliveryDimensionChange"/>, keyed by <see cref="DimensionId"/>.
/// </summary>
public sealed class DeliveryDimension
{
    /// <summary>The longest dimension name, as a run's payload and a page name it.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The longest path a dimension reads, which a query writes unquoted.</summary>
    public const int MaxPathLength = 512;

    /// <summary>The longest field the index is asked for: a keyword sub-field inside the service's nested form.</summary>
    public const int MaxAggregateByLength = 1100;

    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The ledger identity of the dimension flow in the partition.</summary>
    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The kind the dimension reads, wildcards allowed per segment.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The query narrowing the records read, as the last build ran it; null for every record of the kind.</summary>
    public string? Query { get; set; }

    public string Path { get; set; } = string.Empty;

    /// <summary>How the index stores the field: text, keyword, number, boolean or date. Null until a build has read the templates.</summary>
    public string? FieldIndex { get; set; }

    /// <summary>The nested array the field sits in, or null.</summary>
    public string? NestedPath { get; set; }

    /// <summary>The field as the search's aggregateBy names it.</summary>
    public string? AggregateBy { get; set; }

    /// <summary>Whether one record can hold the field more than once, so members' counts need not be of records.</summary>
    public bool Repeats { get; set; }

    /// <summary>The clean steps, as JSON.</summary>
    public string CleanJson { get; set; } = "[]";

    /// <summary>
    /// Where each key's label is read, as a JSON array of paths: the record the key names, then each record a path before
    /// it refers to. Null when keys are their own values.
    /// </summary>
    public string? LabelJson { get; set; }

    /// <summary>
    /// The attributes each key is read with, as a JSON array of <c>{ "name", "steps", "collect" }</c>: the paths each is read
    /// from the record the key names, or the path of the dimension's own records whose values it collects. Null when
    /// the dimension reads none.
    /// </summary>
    public string? AttributesJson { get; set; }

    /// <summary>
    /// What the last build that settled the field read of each collected attribute, as a JSON array: the attribute's name and
    /// path, how the index stores its field, and the value records holding none of its values were given. The texts its
    /// values stand for are rows of <see cref="DeliveryDimensionCollectedText"/>. Null when the dimension collects nothing.
    /// </summary>
    public string? CollectedJson { get; set; }

    /// <summary>The hash of the dimension's declaration as the last build read it.</summary>
    public string DefinitionHash { get; set; } = string.Empty;

    /// <summary>
    /// The dimension's table in this schema (<c>dim_&lt;dimension&gt;</c>), as the last build wrote it: the
    /// dimension as one table, a row per key and value it collects and a column per attribute. Builds make the table and
    /// widen it as the flow declares more; it is no table of the model, and no migration touches it. Null until a build
    /// has written it.
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>
    /// The column of the dimension's table that holds each key, as the table has it now: named after the property the
    /// dimension's path ends with (with <c>Key</c> at its end where the value's column has that name) unless its document
    /// names it, and <c>key</c> in a table made before dimensions named their columns, which the next build renames. Null
    /// until the table has been made ready.
    /// </summary>
    public string? KeyColumn { get; set; }

    /// <summary>
    /// The column of the dimension's table that holds each key's value, as the table has it now: named after the property
    /// the dimension's label ends with (or after the dimension, when it reads no label) unless its document names it, and
    /// <c>value</c> in a table made before dimensions named their columns. Null until the table has been made ready.
    /// </summary>
    public string? ValueColumn { get; set; }

    /// <summary>The members the dimension holds now.</summary>
    public long Members { get; set; }

    /// <summary>The originals the dimension holds now, those under no member included.</summary>
    public long Originals { get; set; }

    /// <summary>The build that last wrote the dimension.</summary>
    public long? LastRunId { get; set; }

    public DateTime? LastBuiltUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// One build of one dimension: the declaration and the field it read with, how it read (aggregations, ranges, scans), what it
/// found and how complete that is, and what it changed.
/// </summary>
public sealed class DeliveryDimensionRun
{
    public short PartitionId { get; set; }

    public long DimensionRunId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The ledger identity of the dimension flow in the partition.</summary>
    public Guid FlowId { get; set; }

    /// <summary>The platform run the build ran in.</summary>
    public Guid? RunId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>running, completed, failed or cancelled.</summary>
    public string Status { get; set; } = "running";

    public string DefinitionHash { get; set; } = string.Empty;

    /// <summary>The query as the build ran it, its tokens substituted.</summary>
    public string? Query { get; set; }

    public string? AggregateBy { get; set; }

    /// <summary>The kinds the pattern matched and the template each was read against, as JSON.</summary>
    public string? Templates { get; set; }

    /// <summary>The records the query matched; null when that could not be counted.</summary>
    public long? Records { get; set; }

    /// <summary>The records the exact field holds a value for; null when that could not be counted.</summary>
    public long? WithValue { get; set; }

    /// <summary>The records (or objects) whose value is null.</summary>
    public long Nulls { get; set; }

    /// <summary>The records holding only text values longer than the exact field keeps; null when not counted.</summary>
    public long? TooLong { get; set; }

    /// <summary>Values the index holds that are not of the field's type.</summary>
    public long Unreadable { get; set; }

    public long Members { get; set; }

    public long Originals { get; set; }

    /// <summary>Originals under no member: cleaned to nothing, to a clean value too long, or dropped by a map.</summary>
    public long LeftOut { get; set; }

    /// <summary>Originals no query can carry, which their members' filters leave out.</summary>
    public long Unfilterable { get; set; }

    public long MembersAdded { get; set; }

    public long MembersRemoved { get; set; }

    public long MembersRestored { get; set; }

    public long OriginalsAdded { get; set; }

    public long OriginalsRemoved { get; set; }

    /// <summary>Originals now under another member than before, or none where they had one.</summary>
    public long OriginalsMoved { get; set; }

    public long OriginalsRestored { get; set; }

    public int Aggregations { get; set; }

    /// <summary>Ranges an aggregation answered whole.</summary>
    public int Slices { get; set; }

    public int Splits { get; set; }

    public int ScannedSlices { get; set; }

    public int ScanPages { get; set; }

    public long ScannedUnits { get; set; }

    /// <summary>Counts of members' records the build asked the search for.</summary>
    public int CountQueries { get; set; }

    /// <summary>Keys whose label the build read from the record they name.</summary>
    public long Labelled { get; set; }

    /// <summary>Keys the build could not label: a key that names no record, a record the search does not hold, or one without the path.</summary>
    public long Unlabelled { get; set; }

    /// <summary>Searches the build asked to read labels.</summary>
    public int LabelQueries { get; set; }

    /// <summary>What the build had to say, as a JSON array of lines.</summary>
    public string? Notes { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// One member of a dimension: a clean value, with how many records hold any of its originals, and the search filter that
/// finds them. A member a build no longer finds is marked removed and kept, so its id stays its own if it comes back. A build
/// writes a row only when something of it changed, so every member the dimension holds now was seen by its last build.
/// </summary>
public sealed class DeliveryDimensionMember
{
    public short PartitionId { get; set; }

    public long MemberId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The clean value, trimmed, compared exactly.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>The records holding any of its originals (or the sum of its originals' counts when <see cref="RecordsExact"/> is false).</summary>
    public long Records { get; set; }

    public bool RecordsExact { get; set; }

    /// <summary>The originals under it.</summary>
    public int Originals { get; set; }

    /// <summary>Its originals no query can carry, which the filter leaves out.</summary>
    public int Unfilterable { get; set; }

    /// <summary>The search filter finding every record holding a filterable original, when it fits one query; null otherwise.</summary>
    public string? Filter { get; set; }

    /// <summary>How many queries the filter takes: 1 when <see cref="Filter"/> holds it, more when its originals pass one query's clauses, 0 with none filterable.</summary>
    public int FilterParts { get; set; }

    /// <summary>The build that first found it. One the dimension holds now was found by the dimension's last build.</summary>
    public long FirstSeenRunId { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    /// <summary>The build that no longer found it, or null while it is there.</summary>
    public long? RemovedRunId { get; set; }

    public DateTime? RemovedUtc { get; set; }
}

/// <summary>
/// One original of a dimension: a value exactly as the index holds it, the member it belongs to or why it belongs to none, and
/// how many records (or objects of a nested array) hold it.
/// </summary>
public sealed class DeliveryDimensionValue
{
    /// <summary>The longest original kept; a longer one is counted and left out by the build.</summary>
    public const int MaxOriginalLength = 1024;

    /// <summary>The longest label kept; a longer one is cut, with a note.</summary>
    public const int MaxLabelLength = 1024;

    /// <summary>The longest filter of one original: the original quoted and escaped, inside the field's nested form.</summary>
    public const int MaxFilterLength = 4000;

    public short PartitionId { get; set; }

    public long ValueId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The value exactly as the index holds it.</summary>
    public string Original { get; set; } = string.Empty;

    /// <summary>SHA-256 of the original's UTF-8 bytes: what an original is unique by, exactly, however long it is.</summary>
    public byte[] OriginalHash { get; set; } = [];

    /// <summary>The member it belongs to, or null when cleaning left it out of every member.</summary>
    public long? MemberId { get; set; }

    /// <summary>Why it belongs to no member: empty, tooLong, dropped or failed; null when it has a member.</summary>
    public string? LeftOut { get; set; }

    /// <summary>What cleaning had to say about it.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// The label the dimension read for it from the record it names (<c>NO 15/9-19 A</c> for a wellbore's id), before
    /// cleaning; null for a dimension that reads no label, or when the build could not read one.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>The id of the record the label was read from: the record the original names, or the last one a label's steps reached.</summary>
    public string? LabelFrom { get; set; }

    /// <summary>The search filter finding the records holding it, exactly as the index holds it; null when no query can carry it.</summary>
    public string? Filter { get; set; }

    public long Count { get; set; }

    /// <summary>Whether a query can carry it, so its member's filter finds its records.</summary>
    public bool Filterable { get; set; }

    /// <summary>The build that first found it. One the dimension holds now was found by the dimension's last build.</summary>
    public long FirstSeenRunId { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    /// <summary>The build that no longer found it, or null while it is there.</summary>
    public long? RemovedRunId { get; set; }

    public DateTime? RemovedUtc { get; set; }

    /// <summary>The build since which it belongs to its member (or to none).</summary>
    public long MemberSinceRunId { get; set; }
}

/// <summary>
/// One value of one attribute of one original (a key). An attribute read through the record the key names holds one value
/// a key, with the record it was read from; a collected attribute holds every value the key's own records hold, each with how
/// many of them hold it. A row is rewritten when a build reads it otherwise, and removed when a build finds the key
/// without it; a key no build finds any more keeps what its last build read.
/// </summary>
public sealed class DeliveryDimensionAttributeValue
{
    /// <summary>The longest attribute name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest attribute value kept; a longer one is cut by the build, with a note.</summary>
    public const int MaxValueLength = 256;

    public short PartitionId { get; set; }

    /// <summary>The row's own number, which the table is stored in the order of.</summary>
    public long AttributeValueId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The original the attribute is of (<see cref="DeliveryDimensionValue.ValueId"/>).</summary>
    public long ValueId { get; set; }

    /// <summary>The attribute, by its number (<see cref="DeliveryDimensionAttributeName.AttributeId"/>).</summary>
    public int AttributeId { get; set; }

    /// <summary>The value read, trimmed, compared exactly.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Where it was read: the id of the record the original names, or of the last one the attribute's steps reached; for a
    /// collected attribute, the text the key's records hold, as the index holds it (the first, ordinally, when several are shown
    /// as one value). Null for the value a key holding none is given.
    /// </summary>
    public string? ValueFrom { get; set; }

    /// <summary>
    /// For a collected attribute, how many of the key's records hold the value; null for an attribute read through the record the
    /// key names, which is every record of the key's.
    /// </summary>
    public long? Records { get; set; }
}

/// <summary>
/// One text a dimension's records hold at a collected attribute's path, as the last build that settled the field read it:
/// the text exactly as the index holds it, the value it is shown as (several texts shown alike are one value), and the
/// records holding it. A search picking a collected value asks for every text shown as it; a pick of the value for what is
/// not read asks for the records holding none of them. A build replaces the dimension's rows with what it read.
/// </summary>
public sealed class DeliveryDimensionCollectedText
{
    public short PartitionId { get; set; }

    /// <summary>The row's own number, which the table is stored in the order of.</summary>
    public long TextId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The collected attribute, by its number (<see cref="DeliveryDimensionAttributeName.AttributeId"/>).</summary>
    public int AttributeId { get; set; }

    /// <summary>SHA-256 of the text's UTF-8 bytes, which the row is unique by, since a text may be longer than a key holds.</summary>
    public byte[] TextHash { get; set; } = [];

    /// <summary>The text exactly as the index holds it, compared exactly.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The value it is shown as, compared exactly.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>The records holding it.</summary>
    public long Records { get; set; }
}

/// <summary>
/// An attribute of a dimension, under the number its values are kept and joined by: its name as the dimension declares
/// it, whether it is collected from the dimension's own records, and its place among the attributes the dimension declares
/// now, which is the order of its columns as the dimension's table is read. An attribute the dimension no longer
/// declares keeps its number and has no place.
/// </summary>
public sealed class DeliveryDimensionAttributeName
{
    public short PartitionId { get; set; }

    public int AttributeId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The attribute's name, as the dimension declares it, compared exactly.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Its place among the attributes the dimension declares now, from 1. Null for an attribute the dimension no longer declares.</summary>
    public short? Ordinal { get; set; }

    /// <summary>Whether its values are collected from the dimension's own records, so a key holds several.</summary>
    public bool Collected { get; set; }
}

/// <summary>
/// A change a build made to one original: it arrived (after the dimension's first build), left, came back, or moved from one
/// member to another. The first build's originals are its arrivals, told by their first build.
/// </summary>
public sealed class DeliveryDimensionChange
{
    public short PartitionId { get; set; }

    public long ChangeId { get; set; }

    public int DimensionId { get; set; }

    public long DimensionRunId { get; set; }

    public long ValueId { get; set; }

    /// <summary>added, removed, moved or restored.</summary>
    public string Change { get; set; } = string.Empty;

    public long? FromMemberId { get; set; }

    public long? ToMemberId { get; set; }

    public DateTime ChangedUtc { get; set; }
}

/// <summary>
/// The single row that says which version of the osdu schema a database holds: the module version and the last migration
/// applied, when and by whom, and the oldest SQLFlow catalog migration this schema works with. Written by the module
/// database's migrate step in the same connection as the migrations, and read by every host at startup.
/// </summary>
public sealed class OsduSchemaVersion
{
    /// <summary>Always 1: the table holds exactly one row.</summary>
    public int Id { get; set; } = 1;

    public string ModuleVersion { get; set; } = string.Empty;

    public string LastMigration { get; set; } = string.Empty;

    public DateTime AppliedUtc { get; set; }

    /// <summary>The host and actor that ran the migrate.</summary>
    public string AppliedBy { get; set; } = string.Empty;

    /// <summary>The oldest SQLFlow catalog migration the schema needs beside it.</summary>
    public string MinimumCatalogMigration { get; set; } = string.Empty;
}

/// <summary>
/// What a central configuration property may be called and hold, and how many of them one run carries. The bounds are
/// here rather than at each caller so the store, the API, the CLI and the run payload all refuse the same things.
/// </summary>
public static partial class DeliveryConfigNames
{
    /// <summary>The longest accepted property name, which is an environment variable name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest accepted value. A value is an identifier, a URL or a reference, never a document.</summary>
    public const int MaxValueLength = 1000;

    /// <summary>The most properties one run carries, which bounds what a queued run writes into its payload.</summary>
    public const int MaxPerRun = 64;

    /// <summary>Whether <paramref name="name"/> can name a property: an environment variable name, which is what a flow spells it as.</summary>
    public static bool IsName(string? name)
        => !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && NamePattern().IsMatch(name);

    /// <summary>
    /// Whether <paramref name="value"/> can be held as a property: non-empty, within the bound and free of the control
    /// characters that would make a value unprintable in a log line or a run payload.
    /// </summary>
    public static bool IsValue(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= MaxValueLength && !value.Any(char.IsControl);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();
}

/// <summary>
/// A partition the ledger keeps rows under, and the number that stands for it in every ledger key (docs/ledger.md,
/// Partitions). A partition's name is a data-partition-id of up to 200 characters; carried in every key of the record
/// table it would push the source file index past SQL Server's 1700-byte limit and widen every index of the largest
/// tables, so the ledger keys by this number and names the partition here. A row is added the first time a ledger of the
/// partition is registered, and is never renumbered or removed: every ledger row names its partition by it.
/// </summary>
public sealed class DeliveryLedgerPartition
{
    /// <summary>The number every ledger key starts with. <see cref="DeliveryModel.UnassignedPartition"/> (0) has no row.</summary>
    public short PartitionId { get; set; }

    /// <summary>The data-partition-id, as runs name it; compared regardless of case, so a partition has one number.</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// One ledger: the rows of one flow (or interface of a source) in one partition, under one ledger identity (docs/ledger.md,
/// Partitions). The engine registers a ledger when a run binds its flow, before it writes a row, with the partition the
/// run delivers to: the one the flow names, the registry's, or the one its <c>data-partition-id</c> header resolves to.
/// From then on the ledger belongs to that partition. Every ledger row carries the partition in its key, written from here,
/// so which partition a record, a submission or an audit entry belongs to is known from the ledger alone.
/// </summary>
/// <remarks>
/// A ledger the upgrade to partition keys could not place (no interface named its partition and its records carry no OSDU
/// id, or ids of more than one partition) is <see cref="DeliveryModel.UnassignedPartition"/> until its next run adopts it,
/// which refuses a ledger holding records delivered to another partition than the run's.
/// </remarks>
public sealed class DeliveryLedger
{
    /// <summary>The widest flow name a ledger keeps.</summary>
    public const int MaxFlowNameLength = 200;

    /// <summary>The partition the ledger belongs to, or <see cref="DeliveryModel.UnassignedPartition"/>.</summary>
    public short PartitionId { get; set; }

    /// <summary>The ledger identity every row of the ledger carries.</summary>
    public Guid FlowId { get; set; }

    /// <summary>delivery, retrieval or assertion: the kind of flow whose ledger this is.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The flow (for a source with interfaces, the source) the ledger belongs to.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The interface of a source, or empty for a flow in the single form.</summary>
    public string Interface { get; set; } = string.Empty;

    /// <summary>The ledger's name, as pages show it: <c>&lt;ledger&gt;@&lt;partition&gt;</c> for a partition keeping a ledger of its own.</summary>
    public string LedgerName { get; set; } = string.Empty;

    public DateTime RegisteredUtc { get; set; }
}

/// <summary>
/// An OSDU partition registered with the catalog (docs/partitions-design.md section 2.1): the partitions every run, cache and
/// ledger of the module is keyed by. A flow that names no partitions serves every registered one, so the same documents
/// deploy to every environment; a run that names none runs in the one marked the default. A flow that names its
/// partitions serves those of them that are registered.
/// </summary>
/// <remarks>
/// Removing a partition deletes nothing kept under it: its caches, ledgers and runs stay as they are, and no run can target
/// it until it is registered again. At most one partition is the default, which a filtered unique index keeps.
/// </remarks>
public sealed class DeliveryPartition
{
    /// <summary>The widest description a partition keeps.</summary>
    public const int MaxDescriptionLength = 400;

    /// <summary>The data-partition-id, as runs name it and caches and ledgers are keyed by it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What the partition is for (the environment, the platform), in the words of whoever registered it.</summary>
    public string? Description { get; set; }

    /// <summary>True for the one partition a run that names none runs in.</summary>
    public bool IsDefault { get; set; }

    public DateTime CreatedUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>
/// One property of the central configuration: a value the control plane supplies to the runs it queues, so a flow that
/// names <c>${env:NAME}</c> resolves it from here rather than from whatever the node that picks the run up happens to
/// hold. A property is set once for the whole control plane (<see cref="RepoId"/> null) and may be set again for one
/// repository, which is an estate: the repository's value wins for the flows that repository holds. Either may also be set
/// for one OSDU partition (<see cref="Partition"/>), and a run of a flow bound to that partition resolves with it first.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Value"/> is a non-secret value (an entitlements group, a legal tag, a base URL) or a
/// <c>${env:NAME}</c> or <c>${keyvault:vault/secret}</c> reference, which travels unresolved and is resolved on the node.
/// A literal secret here is a defect: this row, and the run payload it is carried in, are ordinary catalog content.
/// </para>
/// <para>
/// For a run bound to partition P the value of a name is taken from the first scope that sets it: the repository's for P,
/// the control plane's for P, the repository's, the control plane's (docs/partitions-design.md section 5). A value set for
/// a partition always wins over one that is not, so an estate-wide endpoint never sends a run of one partition to another's
/// platform.
/// </para>
/// </remarks>
public sealed class DeliveryConfigProperty
{
    public Guid Id { get; set; }

    /// <summary>The repository whose flows this value applies to, or null for the control plane's own value.</summary>
    public Guid? RepoId { get; set; }

    /// <summary>
    /// The OSDU partition whose runs this value applies to, by its data-partition-id, or null for a value that applies
    /// whatever the partition.
    /// </summary>
    public string? Partition { get; set; }

    /// <summary>The reference name a flow spells as <c>${env:NAME}</c>, held as written.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The value, or a reference the node resolves.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>What this property is for, shown wherever it is listed.</summary>
    public string? Description { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>Who set it last.</summary>
    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>The EF model of the delivery ledger, in the <c>osdu</c> schema.</summary>
public static class DeliveryModel
{
    public const string SchemaName = "osdu";

    /// <summary>
    /// The partition number of a ledger the upgrade to partition keys could not place, until its next run adopts it
    /// (<see cref="DeliveryLedger"/>). No <see cref="DeliveryLedgerPartition"/> row carries it.
    /// </summary>
    public const short UnassignedPartition = 0;

    /// <summary>The widest data-partition-id, as every table that names a partition keeps it.</summary>
    public const int MaxPartitionLength = 200;

    /// <summary>
    /// The collation of the columns that key on an OSDU record id. OSDU ids are case-sensitive:
    /// <c>...UnitOfMeasure:ft</c> (the foot) and <c>...UnitOfMeasure:fT</c> (the femtotesla) are two records, and SQL
    /// Server's default collation folds case, which would make them one key.
    /// </summary>
    public const string OsduIdCollation = "Latin1_General_100_BIN2";

    /// <summary>
    /// The collation a search of text the ledger keeps exactly asks it in: case folded, so a person finds a clean value or an
    /// original by the spelling they know, whatever its case.
    /// </summary>
    public const string SearchCollation = "Latin1_General_100_CI_AS";

    /// <summary>
    /// The longest ingestion file name a record's origin holds. It keeps the (flow, file, row) index key under SQL Server's
    /// 1700-byte limit; a longer file name is refused when the source is opened, naming the file and this limit.
    /// </summary>
    public const int MaxSourceFileNameLength = 800;

    /// <summary>The longest lease token, and the longest worker name a lease records as its owner.</summary>
    public const int MaxLeaseTokenLength = 200;

    /// <summary>
    /// The longest identity token, and the longest display value beside it: 200 characters keep the
    /// (token, flow, key) primary key well under SQL Server's 1700-byte limit for a nonclustered index key, and a
    /// longer value is still found by its start. The ledger's <c>RecordIdentityLimits.MaxTokenLength</c> is this.
    /// </summary>
    public const int MaxIdentityTokenLength = 200;

    /// <summary>
    /// The filtered index of the blocked records not sorted into a problem yet, named because a statement reaches it by
    /// name and repeats its filter: the problem backfill's.
    /// </summary>
    public const string UnsortedProblemIndex = "IX_Record_Unsorted";

    /// <param name="modelBuilder">The model being built.</param>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // The ledger's directory: the partitions it keys by, and the ledger of every flow in one of them.
        modelBuilder.Entity<DeliveryLedgerPartition>(e =>
        {
            e.ToTable("LedgerPartition", SchemaName);
            e.HasKey(p => p.PartitionId);
            e.Property(p => p.PartitionId).ValueGeneratedOnAdd();
            e.Property(p => p.Name).HasMaxLength(MaxPartitionLength).IsRequired();
            // A partition has one number whatever the case it is written in, as the registry compares names.
            e.HasIndex(p => p.Name).IsUnique();
        });

        modelBuilder.Entity<DeliveryLedger>(e =>
        {
            e.ToTable("Ledger", SchemaName);
            // A partition's ledgers together, as every ledger table keeps its rows.
            e.HasKey(l => new { l.PartitionId, l.FlowId });
            e.Property(l => l.Kind).HasMaxLength(16).IsRequired();
            e.Property(l => l.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(l => l.Interface).HasMaxLength(DeliveryInterface.MaxInterfaceLength).IsRequired();
            e.Property(l => l.LedgerName).HasMaxLength(200).IsRequired();
            // A ledger belongs to one partition: its identity is unique on its own, and every read of it starts here.
            e.HasIndex(l => l.FlowId).IsUnique();
        });

        // Every ledger table keys by partition first, so one partition's rows are kept together and every read of a
        // flow, a partition or a listing seeks a range of one partition. A row found by an id unique on its own (a
        // submission, an activity, a retrieval, a lease, a work batch) keeps that id unique in an index of its own, for
        // the reads that hold nothing else.
        modelBuilder.Entity<DeliverySubmission>(e =>
        {
            e.ToTable("Submission", SchemaName);
            e.HasKey(s => new { s.PartitionId, s.SubmissionId });
            e.Property(s => s.FlowName).HasMaxLength(200).IsRequired();
            e.Property(s => s.MappingReference).HasMaxLength(200).IsRequired();
            e.Property(s => s.RenderContext).IsRequired();
            e.Property(s => s.WorkLocation).HasMaxLength(2000);
            e.Property(s => s.ParametersJson).IsRequired();
            e.Property(s => s.Status).HasMaxLength(16).IsRequired();
            e.Property(s => s.Error).HasMaxLength(4000);
            e.Property(s => s.Kind).HasMaxLength(16).IsRequired();
            e.Property(s => s.SourceConnection).HasMaxLength(400).IsRequired();
            e.Property(s => s.SourceObject).HasMaxLength(400).IsRequired();
            // A submission named by its id alone: a run's payload, a link, a record's last submission.
            e.HasIndex(s => s.SubmissionId).IsUnique();
            e.HasIndex(s => new { s.PartitionId, s.FlowId, s.ReceivedUtc });
            e.HasIndex(s => new { s.PartitionId, s.FlowId, s.Status });
            // The flow's submission listing filtered by kind.
            e.HasIndex(s => new { s.PartitionId, s.FlowId, s.Kind, s.ReceivedUtc });
            // The submissions of a partition, most recent first, across its flows.
            e.HasIndex(s => new { s.PartitionId, s.ReceivedUtc });
            // A run page links the run to the plan it coordinated.
            e.HasIndex(s => s.RunId);
        });

        modelBuilder.Entity<DeliveryRecord>(e =>
        {
            e.ToTable("Record", SchemaName);
            // A record is one flow's in one partition: two flows reading the same source row keep two records, and so does
            // one flow delivering it to two partitions, under the same delivery key. A flow's key-ordered walks (a
            // removal's key list, a key-scoped plan) read the key in order within the partition.
            e.HasKey(r => new { r.PartitionId, r.FlowId, r.DeliveryKey });
            e.Property(r => r.SourceKey).HasMaxLength(400).IsRequired();
            e.Property(r => r.SourceKeyJson).HasMaxLength(2000);
            e.Property(r => r.Label).HasMaxLength(400);
            e.Property(r => r.MappingName).HasMaxLength(200).IsRequired();
            e.Property(r => r.SourceFingerprint).HasMaxLength(200);
            e.Property(r => r.SourceFileName).HasMaxLength(MaxSourceFileNameLength);
            e.Property(r => r.MetadataHash).HasMaxLength(64);
            e.Property(r => r.PayloadHash).HasMaxLength(64);
            e.Property(r => r.TargetId).HasMaxLength(500);
            OptionalOsduId(e.Property(r => r.ClaimedTargetId)).HasMaxLength(500);
            OptionalOsduId(e.Property(r => r.WaitingFor)).HasMaxLength(500);
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.LastVerifyOutcome).HasMaxLength(16);
            e.Property(r => r.LeaseOwner).HasMaxLength(200);
            e.Property(r => r.LastError).HasMaxLength(2000);
            e.Property(r => r.PendingSourceFingerprint).HasMaxLength(200);
            e.Property(r => r.PendingSourceFileName).HasMaxLength(MaxSourceFileNameLength);
            e.Property(r => r.PendingMetadataHash).HasMaxLength(64);
            e.Property(r => r.PendingPayloadHash).HasMaxLength(64);
            e.Property(r => r.PendingPayloadLocation).HasMaxLength(2000);
            e.Property(r => r.PendingDocumentRef).HasMaxLength(64);
            e.Property(r => r.ValidationOutcome).HasMaxLength(16);
            e.Property(r => r.AcceptedMetadataHash).HasMaxLength(64);

            // Worker and intake paths. The worker's reads (the claim, what is due next, the settled submissions with due
            // work) are answered from these two alone, for a flow and for one submission of it.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Status, r.NextAttemptUtc })
                .IncludeProperties(r => new { r.LastSubmissionId, r.UpdatedUtc, r.PendingDocumentRef });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastSubmissionId, r.Status, r.NextAttemptUtc })
                .IncludeProperties(r => r.UpdatedUtc);
            // The rollout walks one set's records in key order, then flow order; a set is one partition's, and the filtered
            // index keeps untagged records out of it.
            e.HasIndex(r => new { r.CacheSetId, r.DeliveryKey, r.FlowId }).HasFilter("[CacheSetId] IS NOT NULL");
            // One OSDU record, one flow: the database refuses a second flow's claim on an id, whatever races the intakes
            // run. An id names its partition, so this stays unique across every partition.
            e.HasIndex(r => r.ClaimedTargetId).IsUnique().HasFilter("[ClaimedTargetId] IS NOT NULL");
            e.HasIndex(r => new { r.LastSubmissionId, r.WorkBatch });
            // The records a lease holds: the ones a checkpoint or a close hands back, and the ones a submission waits on.
            e.HasIndex(r => r.LeaseOwner);
            // A submission's records, most recent first, read in index order however many the submission holds.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastSubmissionId, r.UpdatedUtc });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastVerifiedUtc });

            // GUI: one flow's prefix search and recency listings, all answered from an index.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Label });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.SourceKey });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.TargetId });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.UpdatedUtc });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Status, r.UpdatedUtc });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastDeliveredUtc });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastVerifyOutcome });

            // The lookup across the flows of a partition (the search box, read in the partition the workbench works in): a
            // delivery key, an OSDU id, a source key, a label prefix or an ingestion file name answers from these.
            e.HasIndex(r => new { r.PartitionId, r.DeliveryKey });
            e.HasIndex(r => new { r.PartitionId, r.TargetId });
            e.HasIndex(r => new { r.PartitionId, r.SourceKey });
            e.HasIndex(r => new { r.PartitionId, r.Label });
            e.HasIndex(r => new { r.PartitionId, r.SourceFileName });

            // The Records page with nothing typed: the most recently updated records across the flows of a partition, and
            // the most recent of one custody state, read from the end of an index instead of by ordering the whole ledger.
            e.HasIndex(r => new { r.PartitionId, r.UpdatedUtc });
            e.HasIndex(r => new { r.PartitionId, r.Status, r.UpdatedUtc });

            // Which records came from this file, inside a flow, in milliseconds.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.SourceFileName, r.SourceRowNumber });

            // The records the ledger asked to be planned again, paged by the planner each run: the filter keeps the
            // index as small as the backlog.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.PlanRequestedUtc }).HasFilter("[PlanRequestedUtc] IS NOT NULL");

            // The records waiting for an id, released when the record holding that id lands in the same partition: the
            // filter keeps the index as small as what is waiting, so the release every settle runs costs a seek.
            e.HasIndex(r => new { r.PartitionId, r.WaitingFor }).HasFilter("[WaitingFor] IS NOT NULL");

            // The blocked records by the problem that keeps them so: a flow's problems counted with their held and failed
            // records, their newest and oldest, and their files; a problem's records listed newest first and released. The
            // filter keeps the index as small as what is blocked, however many records the ledger holds.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.ProblemHash, r.UpdatedUtc })
                .IncludeProperties(r => new { r.Status, r.PendingSourceFileName })
                .HasFilter("[ProblemHash] IS NOT NULL");

            // The blocked records not sorted into a problem yet: held or failed before the ledger kept problems, or by a
            // completion an older build appended. The control plane's backfill reads and sorts them from here, so the index
            // is empty but for that backlog. A query reaches it only by repeating the filter, which the backfill names.
            e.HasIndex(r => new { r.PartitionId, r.FlowId }, UnsortedProblemIndex)
                .HasFilter("[ProblemHash] IS NULL AND [Blocked]=(1) AND ([Status] IN (N'held', N'failed'))");

            // A flow's records by what the last check of their documents came to: counted for the flow's page, and the
            // invalid or unverified ones listed. The filter leaves out the records no check has reached.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.ValidationOutcome, r.UpdatedUtc })
                .HasFilter("[ValidationOutcome] IS NOT NULL");
        });

        modelBuilder.Entity<DeliveryRecordIdentity>(e =>
        {
            e.ToTable("RecordIdentity", SchemaName);
            // One row per token per record: the partition, then the token, so the row is written and read by what it
            // identifies within the partition the lookup reads.
            e.HasKey(i => new { i.PartitionId, i.Token, i.FlowId, i.DeliveryKey });
            e.Property(i => i.Token).HasMaxLength(MaxIdentityTokenLength).IsRequired();
            e.Property(i => i.Display).HasMaxLength(MaxIdentityTokenLength).IsRequired();
            e.Property(i => i.Kind).HasMaxLength(16).IsRequired();

            // The lookup: any identifier an operator holds, as a prefix, across the flows of a partition. The primary key
            // answers it, and the record it names is read by joining to the record's own key.
            //
            // The other direction: a record's own tokens, to rewrite or delete them when its identity changes.
            e.HasIndex(i => new { i.PartitionId, i.FlowId, i.DeliveryKey });

            // The same lookup narrowed to one flow (the Records page's flow filter): its candidates are that flow's own
            // tokens, so a prefix many other flows share cannot use up the candidate bound before this flow is reached.
            e.HasIndex(i => new { i.PartitionId, i.FlowId, i.Token });
        });

        modelBuilder.Entity<DeliveryAttempt>(e =>
        {
            e.ToTable("Attempt", SchemaName);
            // Append-only: the ever-increasing id within each partition, so every drain appends at the end of its range.
            e.HasKey(a => new { a.PartitionId, a.AttemptId });
            e.Property(a => a.AttemptId).ValueGeneratedOnAdd();
            e.Property(a => a.Worker).HasMaxLength(200).IsRequired();
            e.Property(a => a.Outcome).HasMaxLength(16).IsRequired();
            e.Property(a => a.Phase).HasMaxLength(32).IsRequired();
            e.Property(a => a.MetadataHash).HasMaxLength(64);
            e.Property(a => a.PayloadHash).HasMaxLength(64);
            e.Property(a => a.Error).HasMaxLength(2000);
            e.Property(a => a.SourceFileName).HasMaxLength(MaxSourceFileNameLength);
            // A record's timeline, and the later attempt pruning looks for beside each one it removes.
            e.HasIndex(a => new { a.PartitionId, a.FlowId, a.DeliveryKey, a.StartedUtc });
            // Pruning by age, across every partition: retention is the estate's, not a partition's.
            e.HasIndex(a => a.StartedUtc);
            // A submission's attempts, and the records they settled by outcome, counted when the submission closes: the
            // count reads this index alone, however many attempts a full plan wrote.
            e.HasIndex(a => new { a.SubmissionId, a.Outcome, a.Phase }).IncludeProperties(a => a.DeliveryKey);
            // A run's records: the listing's run filter seeks the run and joins on the record's key without reading the attempt.
            e.HasIndex(a => new { a.RunId, a.PartitionId, a.FlowId, a.DeliveryKey });
        });

        modelBuilder.Entity<DeliveryWorkBatch>(e =>
        {
            e.ToTable("WorkBatch", SchemaName);
            e.HasKey(b => new { b.PartitionId, b.SubmissionId, b.Index });
            e.Property(b => b.Location).HasMaxLength(2000).IsRequired();
            e.Property(b => b.Status).HasMaxLength(16).IsRequired();
            e.Property(b => b.LeaseOwner).HasMaxLength(200);
            e.Property(b => b.Error).HasMaxLength(2000);
            // The claim: the oldest queued batch of a flow (or a submission).
            e.HasIndex(b => new { b.PartitionId, b.FlowId, b.Status, b.CreatedUtc });
            // A batch named by its submission and place, and a submission's batches in order, counted by status: the batch's
            // identity is unique on its own.
            e.HasIndex(b => new { b.SubmissionId, b.Index }).IsUnique().IncludeProperties(b => b.Status);
        });

        modelBuilder.Entity<DeliveryLease>(e =>
        {
            e.ToTable("Lease", SchemaName);
            e.HasKey(l => new { l.PartitionId, l.Token });
            e.Property(l => l.Token).HasMaxLength(MaxLeaseTokenLength);
            e.Property(l => l.Owner).HasMaxLength(MaxLeaseTokenLength).IsRequired();
            // A lease named by its token: a renewal, a checkpoint, a close, a record's holder.
            e.HasIndex(l => l.Token).IsUnique();
            // The sweep: a flow's leases that ran out. A submission's leases: what its run waits on.
            e.HasIndex(l => new { l.PartitionId, l.FlowId, l.ExpiresUtc });
            e.HasIndex(l => new { l.SubmissionId, l.ExpiresUtc });
        });

        modelBuilder.Entity<DeliveryRecordEvent>(e =>
        {
            e.ToTable("RecordEvent", SchemaName);
            // Clustered on an ever-increasing id within each partition: every worker only appends, at the end of the
            // partition's range.
            e.HasKey(v => new { v.PartitionId, v.EventId });
            e.Property(v => v.EventId).ValueGeneratedOnAdd();
            e.Property(v => v.LeaseToken).HasMaxLength(MaxLeaseTokenLength).IsRequired();
            e.Property(v => v.Kind).HasMaxLength(16).IsRequired();
            e.Property(v => v.Status).HasMaxLength(16);
            e.Property(v => v.Error).HasMaxLength(2000);
            e.Property(v => v.ValidationOutcome).HasMaxLength(16);
            e.Property(v => v.TargetId).HasMaxLength(500);
            e.Property(v => v.ClaimDocumentRef).HasMaxLength(64);
            e.Property(v => v.ClaimSourceFingerprint).HasMaxLength(200);
            e.Property(v => v.ClaimSourceFileName).HasMaxLength(MaxSourceFileNameLength);
            e.Property(v => v.ClaimMetadataHash).HasMaxLength(64);
            e.Property(v => v.ClaimPayloadHash).HasMaxLength(64);
            // What a lease applies: its events, record by record, the latest last.
            e.HasIndex(v => new { v.LeaseToken, v.FlowId, v.DeliveryKey, v.EventId });
            // A flow's recovery: its events old enough that only a lease that is gone can have left them.
            e.HasIndex(v => new { v.PartitionId, v.FlowId, v.AtUtc }).IncludeProperties(v => v.LeaseToken);
        });

        modelBuilder.Entity<DeliverySourceWatermark>(e =>
        {
            e.ToTable("SourceWatermark", SchemaName);
            e.HasKey(w => new { w.PartitionId, w.FlowId, w.Scope });
            e.Property(w => w.Scope).HasMaxLength(400);
            e.Property(w => w.ContextHash).HasMaxLength(64);
        });

        modelBuilder.Entity<DeliveryActivity>(e =>
        {
            e.ToTable("Activity", SchemaName);
            e.HasKey(a => new { a.PartitionId, a.ActivityId });
            e.Property(a => a.ActivityId).ValueGeneratedOnAdd();
            e.Property(a => a.FlowName).HasMaxLength(200).IsRequired();
            e.Property(a => a.Kind).HasMaxLength(32).IsRequired();
            e.Property(a => a.Actor).HasMaxLength(200).IsRequired();
            e.Property(a => a.Outcome).HasMaxLength(16).IsRequired();
            e.Property(a => a.Summary).HasMaxLength(2000);
            // An entry named by its id: the audit trail's detail, and the run that completes it.
            e.HasIndex(a => a.ActivityId).IsUnique();
            e.HasIndex(a => new { a.PartitionId, a.FlowId, a.StartedUtc });
            e.HasIndex(a => new { a.PartitionId, a.FlowId, a.DeliveryKey, a.StartedUtc });
            e.HasIndex(a => a.SubmissionId);
            e.HasIndex(a => a.RunId);
            // The audit trail of a partition, newest first, and filtered by action or by actor.
            e.HasIndex(a => new { a.PartitionId, a.Kind, a.StartedUtc });
            e.HasIndex(a => new { a.PartitionId, a.Actor, a.StartedUtc });
            e.HasIndex(a => new { a.PartitionId, a.StartedUtc });
            // The trail as it opens, without the idle runs, and the count of those it leaves out: a partition whose schedules
            // fire every hour holds mostly idle runs, so both are a seek rather than a walk past them.
            e.HasIndex(a => new { a.PartitionId, a.Idle, a.StartedUtc });
        });

        modelBuilder.Entity<DeliveryActivityRecord>(e =>
        {
            e.ToTable("ActivityRecord", SchemaName);
            // A record's requests are one seek of its key, in the order the trail numbered them.
            e.HasKey(a => new { a.PartitionId, a.FlowId, a.DeliveryKey, a.ActivityId });
        });

        modelBuilder.Entity<DeliveryPurgedRecord>(e =>
        {
            e.ToTable("PurgedRecord", SchemaName);
            e.HasKey(p => new { p.PartitionId, p.PurgedRecordId });
            e.Property(p => p.PurgedRecordId).ValueGeneratedOnAdd();
            e.Property(p => p.SourceKey).HasMaxLength(400).IsRequired();
            e.Property(p => p.Label).HasMaxLength(400);
            OptionalOsduId(e.Property(p => p.TargetId)).HasMaxLength(500);
            e.Property(p => p.PurgedBy).HasMaxLength(200).IsRequired();
            // A record's page asks by its key what became of a record the ledger no longer holds, and a lookup by OSDU id finds
            // which record went under it.
            e.HasIndex(p => new { p.PartitionId, p.FlowId, p.DeliveryKey });
            e.HasIndex(p => new { p.PartitionId, p.TargetId }).HasFilter("[TargetId] IS NOT NULL");
        });

        modelBuilder.Entity<DeliveryReversal>(e =>
        {
            e.ToTable("Reversal", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.ReversalId });
            e.Property(r => r.ReversalId).ValueGeneratedOnAdd();
            e.Property(r => r.FlowName).HasMaxLength(200).IsRequired();
            e.Property(r => r.SourceKind).HasMaxLength(16).IsRequired();
            e.Property(r => r.SubmissionsJson).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.RequestedBy).HasMaxLength(200).IsRequired();
            e.Property(r => r.Error).HasMaxLength(2000);
            // A reversal named by its id alone: a run's payload, a link.
            e.HasIndex(r => r.ReversalId).IsUnique();
            // One reversal per source of a ledger: asking again resumes it rather than opening a second one.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.SourceKind, r.SourceId }).IsUnique();
            // A ledger's reversals, newest first.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.RequestedUtc });
        });

        modelBuilder.Entity<DeliveryReversalItem>(e =>
        {
            e.ToTable("ReversalItem", SchemaName);
            // A reversal's records together, in key order: what its listing adds to and its settlement walks.
            e.HasKey(i => new { i.PartitionId, i.ReversalId, i.DeliveryKey });
            OptionalOsduId(e.Property(i => i.TargetId)).HasMaxLength(500);
            e.Property(i => i.Prior).HasMaxLength(16).IsRequired();
            e.Property(i => i.State).HasMaxLength(16).IsRequired();
            e.Property(i => i.Outcome).HasMaxLength(24);
            e.Property(i => i.Detail).HasMaxLength(2000);
            // The next page of what is still to do, in key order, and the counts by state and outcome, read from this alone.
            e.HasIndex(i => new { i.PartitionId, i.ReversalId, i.State, i.DeliveryKey }).IncludeProperties(i => i.Outcome);
            // A reversal's records by what came of them, a page at a time.
            e.HasIndex(i => new { i.PartitionId, i.ReversalId, i.Outcome, i.DeliveryKey });
        });

        modelBuilder.Entity<DeliveryRetrieval>(e =>
        {
            e.ToTable("Retrieval", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.RetrievalId });
            e.Property(r => r.RetrievalId).ValueGeneratedOnAdd();
            e.Property(r => r.FlowName).HasMaxLength(200).IsRequired();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Kinds).HasMaxLength(4000).IsRequired();
            e.Property(r => r.WindowField).HasMaxLength(200);
            e.Property(r => r.Location).HasMaxLength(2000).IsRequired();
            e.Property(r => r.ManifestLocation).HasMaxLength(2000);
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            // A retrieval named by its id: the run that completes it.
            e.HasIndex(r => r.RetrievalId).IsUnique();
            // The flow's listing, the watermark chain (the last done run), and the run's row.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.StartedUtc });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Status, r.StartedUtc });
            e.HasIndex(r => r.RunId);
        });

        modelBuilder.Entity<DeliveryAssertionRun>(e =>
        {
            e.ToTable("AssertionRun", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.AssertionRunId });
            e.Property(r => r.AssertionRunId).ValueGeneratedOnAdd();
            e.Property(r => r.FlowName).HasMaxLength(200).IsRequired();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.DefinitionsHash).HasMaxLength(64);
            e.Property(r => r.Error).HasMaxLength(4000);
            // A run named by its id: its report, and the results that close it.
            e.HasIndex(r => r.AssertionRunId).IsUnique();
            // The flow's runs in a partition, newest first; and the platform run's row.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.StartedUtc });
            e.HasIndex(r => r.RunId);
        });

        modelBuilder.Entity<DeliveryAssertionResult>(e =>
        {
            e.ToTable("AssertionResult", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.ResultId });
            e.Property(r => r.ResultId).ValueGeneratedOnAdd();
            e.Property(r => r.TestName).HasMaxLength(100).IsRequired();
            e.Property(r => r.Kind).HasMaxLength(400).IsRequired();
            e.Property(r => r.Outcome).HasMaxLength(16).IsRequired();
            e.Property(r => r.Severity).HasMaxLength(16);
            e.Property(r => r.DefinitionHash).HasMaxLength(32).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            e.Property(r => r.Detail).IsRequired();
            e.HasIndex(r => r.ResultId).IsUnique();
            // A run's report: its results in the order they were recorded.
            e.HasIndex(r => new { r.PartitionId, r.AssertionRunId });
            // One test's history, newest run first, and the latest result of each test of a flow: one seek per test.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.TestName, r.AssertionRunId });
        });

        modelBuilder.Entity<DeliveryDimension>(e =>
        {
            e.ToTable("Dimension", SchemaName);
            e.HasKey(d => new { d.PartitionId, d.DimensionId });
            e.Property(d => d.DimensionId).ValueGeneratedOnAdd();
            e.Property(d => d.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(d => d.Name).HasMaxLength(DeliveryDimension.MaxNameLength).IsRequired();
            e.Property(d => d.Description).HasMaxLength(4000);
            e.Property(d => d.Kind).HasMaxLength(400).IsRequired();
            e.Property(d => d.Query).HasMaxLength(4000);
            e.Property(d => d.Path).HasMaxLength(DeliveryDimension.MaxPathLength).IsRequired();
            e.Property(d => d.FieldIndex).HasMaxLength(16);
            e.Property(d => d.NestedPath).HasMaxLength(DeliveryDimension.MaxPathLength);
            e.Property(d => d.AggregateBy).HasMaxLength(DeliveryDimension.MaxAggregateByLength);
            e.Property(d => d.CleanJson).IsRequired();
            e.Property(d => d.DefinitionHash).HasMaxLength(16).IsRequired();
            e.Property(d => d.TableName).HasMaxLength(128);
            e.Property(d => d.KeyColumn).HasMaxLength(128);
            e.Property(d => d.ValueColumn).HasMaxLength(128);
            // A dimension named by its id: every member, original, build and change of it names it so.
            e.HasIndex(d => d.DimensionId).IsUnique();
            // A flow's dimension in a partition, by its name: what a build registers and a page finds.
            e.HasIndex(d => new { d.PartitionId, d.FlowId, d.Name }).IsUnique();
            // The dimensions writing one table, one a partition: what a build asks before it writes and a removal before it drops.
            e.HasIndex(d => d.TableName);
        });

        modelBuilder.Entity<DeliveryDimensionRun>(e =>
        {
            e.ToTable("DimensionRun", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.DimensionRunId });
            e.Property(r => r.DimensionRunId).ValueGeneratedOnAdd();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.DefinitionHash).HasMaxLength(16).IsRequired();
            e.Property(r => r.Query).HasMaxLength(4000);
            e.Property(r => r.AggregateBy).HasMaxLength(DeliveryDimension.MaxAggregateByLength);
            e.Property(r => r.Error).HasMaxLength(4000);
            e.HasIndex(r => r.DimensionRunId).IsUnique();
            // A dimension's builds, newest first; and the platform run's builds.
            e.HasIndex(r => new { r.PartitionId, r.DimensionId, r.StartedUtc });
            e.HasIndex(r => r.RunId);
        });

        modelBuilder.Entity<DeliveryDimensionMember>(e =>
        {
            e.ToTable("DimensionMember", SchemaName);
            e.HasKey(m => new { m.PartitionId, m.MemberId });
            e.Property(m => m.MemberId).ValueGeneratedOnAdd();
            // A clean value is compared exactly, as a filter an application stored compares it.
            ExactText(e.Property(m => m.Value)).HasMaxLength(256).IsRequired();
            e.HasIndex(m => m.MemberId).IsUnique();
            // A member is a clean value of one dimension, once: what a build matches its members by.
            e.HasIndex(m => new { m.PartitionId, m.DimensionId, m.Value }).IsUnique();
            // The members a dimension holds now, and those it held, each read as one range.
            e.HasIndex(m => new { m.PartitionId, m.DimensionId, m.RemovedRunId });
            // The members most records hold first, a page at a time.
            e.HasIndex(m => new { m.PartitionId, m.DimensionId, m.Records, m.Value }).IsDescending(false, false, true, false);
        });

        modelBuilder.Entity<DeliveryDimensionValue>(e =>
        {
            e.ToTable("DimensionValue", SchemaName);
            e.HasKey(v => new { v.PartitionId, v.ValueId });
            e.Property(v => v.ValueId).ValueGeneratedOnAdd();
            ExactText(e.Property(v => v.Original)).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength).IsRequired();
            e.Property(v => v.OriginalHash).HasMaxLength(32).IsFixedLength().IsRequired();
            e.Property(v => v.LeftOut).HasMaxLength(16);
            e.Property(v => v.Note).HasMaxLength(400);
            e.Property(v => v.Label).HasMaxLength(DeliveryDimensionValue.MaxLabelLength);
            e.Property(v => v.LabelFrom).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength);
            e.Property(v => v.Filter).HasMaxLength(DeliveryDimensionValue.MaxFilterLength);
            e.HasIndex(v => v.ValueId).IsUnique();
            // An original is one value of one dimension, once, compared by its hash so no length or collation blurs two.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.OriginalHash }).IsUnique();
            // A member's originals.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.MemberId });
            // The originals most records hold first, a page at a time.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.Count, v.ValueId }).IsDescending(false, false, true, false);
            // A dimension's keys in the order they arrived, a page at a time, as one range of this dimension alone.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.ValueId });
        });

        modelBuilder.Entity<DeliveryDimensionAttributeValue>(e =>
        {
            e.ToTable("DimensionAttribute", SchemaName);
            e.HasKey(a => new { a.PartitionId, a.AttributeValueId });
            e.Property(a => a.AttributeValueId).ValueGeneratedOnAdd();
            ExactText(e.Property(a => a.Value)).HasMaxLength(DeliveryDimensionAttributeValue.MaxValueLength).IsRequired();
            e.Property(a => a.ValueFrom).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength);
            e.HasIndex(a => a.AttributeValueId).IsUnique();
            // A key's attributes, read with it a page at a time: one row per value, so a collected attribute holds several,
            // and a key holds a value of an attribute once.
            e.HasIndex(a => new { a.PartitionId, a.DimensionId, a.ValueId, a.AttributeId, a.Value }).IsUnique();
            // The keys an attribute value holds (Country is Norway), and an attribute's values: one seek either way.
            e.HasIndex(a => new { a.PartitionId, a.DimensionId, a.AttributeId, a.Value });
        });

        modelBuilder.Entity<DeliveryDimensionAttributeName>(e =>
        {
            e.ToTable("DimensionAttributeName", SchemaName);
            e.HasKey(n => new { n.PartitionId, n.AttributeId });
            e.Property(n => n.AttributeId).ValueGeneratedOnAdd();
            ExactText(e.Property(n => n.Name)).HasMaxLength(DeliveryDimensionAttributeValue.MaxNameLength).IsRequired();
            e.HasIndex(n => n.AttributeId).IsUnique();
            // A dimension's attribute by its name, once: what a build finds the number by.
            e.HasIndex(n => new { n.PartitionId, n.DimensionId, n.Name }).IsUnique();
        });

        modelBuilder.Entity<DeliveryDimensionCollectedText>(e =>
        {
            e.ToTable("DimensionCollectedText", SchemaName);
            e.HasKey(t => new { t.PartitionId, t.TextId });
            e.Property(t => t.TextId).ValueGeneratedOnAdd();
            e.Property(t => t.TextHash).HasMaxLength(32).IsFixedLength().IsRequired();
            ExactText(e.Property(t => t.Text)).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength).IsRequired();
            ExactText(e.Property(t => t.Value)).HasMaxLength(DeliveryDimensionAttributeValue.MaxValueLength).IsRequired();
            e.HasIndex(t => t.TextId).IsUnique();
            // A text of a collected attribute, once, by its hash, since a text may be longer than a key holds.
            e.HasIndex(t => new { t.PartitionId, t.DimensionId, t.AttributeId, t.TextHash }).IsUnique();
            // The texts of the values a search picks: one seek.
            e.HasIndex(t => new { t.PartitionId, t.DimensionId, t.AttributeId, t.Value });
        });

        modelBuilder.Entity<DeliveryDimensionChange>(e =>
        {
            e.ToTable("DimensionChange", SchemaName);
            e.HasKey(c => new { c.PartitionId, c.ChangeId });
            e.Property(c => c.ChangeId).ValueGeneratedOnAdd();
            e.Property(c => c.Change).HasMaxLength(16).IsRequired();
            e.HasIndex(c => c.ChangeId).IsUnique();
            // An original's history, and a build's changes.
            e.HasIndex(c => new { c.PartitionId, c.DimensionId, c.ValueId, c.ChangeId });
            e.HasIndex(c => new { c.PartitionId, c.DimensionRunId });
        });

        modelBuilder.Entity<DeliveryMapping>(e =>
        {
            e.ToTable("Mapping", SchemaName);
            e.HasKey(m => m.Id);
            e.Property(m => m.Reference).HasMaxLength(200).IsRequired();
            e.Property(m => m.Name).HasMaxLength(150).IsRequired();
            e.Property(m => m.Version).HasMaxLength(50).IsRequired();
            e.Property(m => m.Kind).HasMaxLength(200).IsRequired();
            e.Property(m => m.TemplateVersion).HasMaxLength(64).IsRequired();
            e.Property(m => m.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(m => m.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(m => m.Yaml).IsRequired();
            e.Property(m => m.SummaryJson).IsRequired();
            e.Property(m => m.Status).HasMaxLength(16).IsRequired();
            e.Property(m => m.Message).HasMaxLength(4000);
            e.HasIndex(m => new { m.RepoId, m.Reference }).IsUnique();
            e.HasIndex(m => new { m.RepoId, m.Kind });
            // A template delete asks which mappings pin the version.
            e.HasIndex(m => new { m.Kind, m.TemplateVersion });
        });

        modelBuilder.Entity<DeliveryInterface>(e =>
        {
            e.ToTable("Interface", SchemaName);
            e.HasKey(i => i.Id);
            e.Property(i => i.FlowName).HasMaxLength(400).IsRequired();
            e.Property(i => i.Interface).HasMaxLength(DeliveryInterface.MaxInterfaceLength).IsRequired();
            e.Property(i => i.LedgerName).HasMaxLength(200).IsRequired();
            e.Property(i => i.Route).HasMaxLength(32).IsRequired();
            e.Property(i => i.RouteReason).HasMaxLength(1000);
            e.Property(i => i.MappingReference).HasMaxLength(200).IsRequired();
            e.Property(i => i.Kind).HasMaxLength(200).IsRequired();
            e.Property(i => i.RecordObject).HasMaxLength(400).IsRequired();
            e.Property(i => i.AfterJson).HasMaxLength(4000).IsRequired();
            e.Property(i => i.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(i => i.Partition).HasMaxLength(200).IsRequired();
            e.HasIndex(i => new { i.RepoId, i.FlowName, i.Interface, i.Partition }).IsUnique();
            // A record's page, a submission's page and the record search find the pipeline behind a ledger identity.
            e.HasIndex(i => i.LedgerFlowId);
        });

        modelBuilder.Entity<DeliveryTemplate>(e =>
        {
            e.ToTable("Template", SchemaName);
            e.HasKey(t => t.Id);
            e.Property(t => t.Kind).HasMaxLength(200).IsRequired();
            e.Property(t => t.Version).HasMaxLength(64).IsRequired();
            e.Property(t => t.SchemaJson).IsRequired();
            e.Property(t => t.Origin).HasMaxLength(1000).IsRequired();
            e.Property(t => t.CapturedBy).HasMaxLength(200).IsRequired();
            e.HasIndex(t => new { t.Kind, t.Version }).IsUnique();
        });

        modelBuilder.Entity<DeliveryCacheVersion>(e =>
        {
            e.ToTable("CacheVersion", SchemaName);
            // Stored in partition order (the clustered index below), so everything a refresh reads or writes of its
            // partition's versions is a range seek of that partition, and refreshes of different partitions never touch,
            // and so never wait on, each other's rows.
            e.HasKey(v => v.Id).IsClustered(false);
            e.Property(v => v.Scope).HasMaxLength(200).IsRequired();
            e.Property(v => v.FlowName).HasMaxLength(200).IsRequired();
            e.Property(v => v.Version).HasMaxLength(64).IsRequired();
            e.Property(v => v.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(v => v.PreviousVersion).HasMaxLength(64);
            e.Property(v => v.CapturedBy).HasMaxLength(200).IsRequired();
            e.Property(v => v.Origin).HasMaxLength(1000).IsRequired();
            e.Property(v => v.TypesJson).IsRequired();
            e.Property(v => v.SystemPropertiesJson).IsRequired().HasDefaultValue("[]");
            e.HasIndex(v => new { v.Scope, v.Version }).IsUnique();
            // The sequence is what a concurrent second write of the same partition collides on, so two captures can never
            // both claim the next version. It is the table's clustered key: a partition's versions in order.
            e.HasIndex(v => new { v.Scope, v.Sequence }).IsUnique().IsClustered();
            e.HasIndex(v => v.RunId);
        });

        modelBuilder.Entity<DeliveryCacheItem>(e =>
        {
            e.ToTable("CacheItem", SchemaName);
            // Stored in partition order (the clustered index below) for the same reason as the versions: a refresh reads
            // and closes only its own partition's rows, whatever plan the table's size leads to.
            e.HasKey(i => i.ItemId).IsClustered(false);
            e.HasIndex(i => new { i.Scope, i.ItemId }).IsUnique().IsClustered();
            e.Property(i => i.Scope).HasMaxLength(200).IsRequired();
            e.Property(i => i.TypeName).HasMaxLength(200).IsRequired();
            e.Property(i => i.EntityType).HasMaxLength(200).IsRequired();
            OsduId(e.Property(i => i.RecordId)).HasMaxLength(512).IsRequired();
            e.Property(i => i.FieldsJson).IsRequired();
            e.Property(i => i.Terms).IsRequired();
            // A record holds one range per distinct content: the type listing is its prefix, so this index answers both a
            // version's listing of a type and one record's history.
            e.HasIndex(i => new { i.Scope, i.TypeName, i.RecordId, i.FromSequence }).IsUnique();
            // The rows the newest version holds (ToSequence null), which a merge compares its result against.
            e.HasIndex(i => new { i.Scope, i.ToSequence });
        });

        modelBuilder.Entity<DeliveryCacheMember>(e =>
        {
            e.ToTable("CacheMember", SchemaName);
            e.Property(m => m.Scope).HasMaxLength(200).IsRequired();
            e.Property(m => m.TypeName).HasMaxLength(200).IsRequired();
            OsduId(e.Property(m => m.RecordId)).HasMaxLength(512).IsRequired();
            e.Property(m => m.FlowName).HasMaxLength(200).IsRequired();
            // A merge reads who holds each record of the captured types, and replaces one flow's rows of a type.
            e.HasKey(m => new { m.Scope, m.TypeName, m.RecordId, m.FlowName });
            e.HasIndex(m => new { m.Scope, m.FlowName, m.TypeName });
        });

        modelBuilder.Entity<DeliveryCacheSet>(e =>
        {
            e.ToTable("CacheSet", SchemaName);
            e.HasKey(c => c.SetId);
            e.Property(c => c.SetHash).HasMaxLength(64).IsRequired();
            e.HasIndex(c => c.SetHash).IsUnique();
            // The planner reads this every run: a filtered index keeps it to the handful of gated sets.
            e.HasIndex(c => c.Gated).HasFilter("[Gated] = 1");
        });

        modelBuilder.Entity<DeliveryCacheSetEntry>(e =>
        {
            e.ToTable("CacheSetEntry", SchemaName);
            e.Property(c => c.Scope).HasMaxLength(200).IsRequired();
            e.Property(c => c.TypeName).HasMaxLength(200).IsRequired();
            OsduId(e.Property(c => c.ItemId)).HasMaxLength(512).IsRequired();
            e.Property(c => c.Path).HasMaxLength(400).IsRequired();
            e.Property(c => c.Kind).HasMaxLength(16).IsRequired();
            e.Property(c => c.ValueHash).HasMaxLength(64).IsRequired();
            e.Property(c => c.ValueText).HasMaxLength(400).IsRequired();
            e.HasKey(c => new { c.SetId, c.Scope, c.TypeName, c.ItemId, c.Path, c.Kind });
            // The impact query: which sets hold this cached value of this partition's cache.
            e.HasIndex(c => new { c.Scope, c.TypeName, c.ItemId });
        });

        modelBuilder.Entity<DeliveryPartition>(e =>
        {
            e.ToTable("Partition", SchemaName);
            e.HasKey(p => p.Name);
            e.Property(p => p.Name).HasMaxLength(200).IsRequired();
            e.Property(p => p.Description).HasMaxLength(DeliveryPartition.MaxDescriptionLength);
            e.Property(p => p.CreatedBy).HasMaxLength(200).IsRequired();
            e.Property(p => p.UpdatedBy).HasMaxLength(200).IsRequired();
            // At most one partition is the default.
            e.HasIndex(p => p.IsDefault).IsUnique().HasFilter("[IsDefault] = 1");
        });

        modelBuilder.Entity<DeliveryConfigProperty>(e =>
        {
            e.ToTable("ConfigProperty", SchemaName);
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).HasMaxLength(DeliveryConfigNames.MaxNameLength).IsRequired();
            e.Property(c => c.Value).HasMaxLength(DeliveryConfigNames.MaxValueLength).IsRequired();
            e.Property(c => c.Description).HasMaxLength(400);
            e.Property(c => c.UpdatedBy).HasMaxLength(200).IsRequired();
            e.Property(c => c.Partition).HasMaxLength(200);
            // One value per name per scope: the control plane's own value is the row with no repository, and a value for
            // no particular partition the row with no partition. The filter is cleared because EF excludes nulls from a
            // unique index over nullable columns by default, which would leave those rows unconstrained; SQL Server treats
            // nulls as equal, so one index covers every scope.
            e.HasIndex(c => new { c.RepoId, c.Partition, c.Name }).IsUnique().HasFilter(null);
            e.HasIndex(c => c.Name);
        });

        modelBuilder.Entity<DeliveryUpdateTag>(e =>
        {
            e.ToTable("UpdateTag", SchemaName);
            e.HasKey(t => t.TagId);
            e.Property(t => t.Kind).HasMaxLength(16).IsRequired();
            e.Property(t => t.Scope).HasMaxLength(200).IsRequired();
            e.Property(t => t.TypeName).HasMaxLength(200).IsRequired();
            OsduId(e.Property(t => t.ItemId)).HasMaxLength(512).IsRequired();
            e.Property(t => t.Path).HasMaxLength(400).IsRequired();
            e.Property(t => t.Change).HasMaxLength(16).IsRequired();
            e.Property(t => t.OldValue).HasMaxLength(400);
            e.Property(t => t.NewValue).HasMaxLength(400);
            e.Property(t => t.FromVersion).HasMaxLength(64);
            e.Property(t => t.ToVersion).HasMaxLength(64).IsRequired();
            e.Property(t => t.Mode).HasMaxLength(16).IsRequired();
            e.Property(t => t.Status).HasMaxLength(16).IsRequired();
            e.Property(t => t.DecidedBy).HasMaxLength(200);
            e.Property(t => t.SetIds).IsRequired();
            // The rollout queue, across every partition.
            e.HasIndex(t => t.Status);
            e.HasIndex(t => new { t.Scope, t.TypeName, t.ItemId, t.Path, t.Status });
            // A partition's changes of one status, newest first: the tag id the table is keyed by orders them.
            e.HasIndex(t => new { t.Scope, t.Status });
        });

        modelBuilder.Entity<DeliveryCacheDefinition>(e =>
        {
            e.ToTable("CacheDefinition", SchemaName);
            e.HasKey(c => c.Id);
            e.Property(c => c.FlowName).HasMaxLength(200).IsRequired();
            e.Property(c => c.Scope).HasMaxLength(200).IsRequired();
            e.Property(c => c.Origin).HasMaxLength(16).IsRequired().HasDefaultValue("osdu");
            e.Property(c => c.Endpoint).HasMaxLength(1000);
            e.Property(c => c.Connection).HasMaxLength(1000);
            e.Property(c => c.SourceObject).HasMaxLength(400);
            e.Property(c => c.KeyField).HasMaxLength(128);
            e.Property(c => c.DictionaryPath).HasMaxLength(1000);
            e.Property(c => c.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
            e.Property(c => c.EntityType).HasMaxLength(200).IsRequired();
            e.Property(c => c.Kind).HasMaxLength(400);
            e.Property(c => c.Query).HasMaxLength(4000);
            e.Property(c => c.FieldsJson).IsRequired();
            e.Property(c => c.OnChange).HasMaxLength(16).IsRequired();
            // A cache flow that names its partitions declares each of its types once per partition it builds a cache for.
            e.HasIndex(c => new { c.RepoId, c.FlowName, c.Name, c.Scope }).IsUnique();
            // A refresh reads every declaration of its partition; the GUI lists a partition's types and the flows filling them.
            e.HasIndex(c => new { c.Scope, c.Name });
            e.HasIndex(c => c.FlowName);
        });

        modelBuilder.Entity<OsduSchemaVersion>(e =>
        {
            e.ToTable("SchemaVersion", SchemaName, t => t.HasCheckConstraint("CK_SchemaVersion_SingleRow", "[Id] = 1"));
            e.HasKey(v => v.Id);
            e.Property(v => v.Id).ValueGeneratedNever();
            e.Property(v => v.ModuleVersion).HasMaxLength(32).IsRequired();
            e.Property(v => v.LastMigration).HasMaxLength(150).IsRequired();
            e.Property(v => v.AppliedBy).HasMaxLength(200).IsRequired();
            e.Property(v => v.MinimumCatalogMigration).HasMaxLength(150).IsRequired();
        });
    }

    /// <summary>
    /// A column keyed on an OSDU record id: compared exactly by the database (<see cref="OsduIdCollation"/>) and exactly by
    /// the change tracker. Without the ordinal comparer, a context tracking the foot and
    /// the femtotesla side by side (two memberships of one cache flow, say) takes them for one key and refuses the second.
    /// </summary>
    private static PropertyBuilder<string> OsduId(PropertyBuilder<string> property)
    {
        property.Metadata.SetValueComparer(new ValueComparer<string>(
            (left, right) => string.Equals(left, right, StringComparison.Ordinal),
            value => StringComparer.Ordinal.GetHashCode(value),
            value => value));
        return property.UseCollation(OsduIdCollation);
    }

    /// <summary>
    /// A column of text a dimension compares exactly: by the database (<see cref="OsduIdCollation"/>), where the default
    /// collation would take GR and gr for one member, and by the change tracker.
    /// </summary>
    private static PropertyBuilder<string> ExactText(PropertyBuilder<string> property) => OsduId(property);

    /// <summary>A nullable column keyed on an OSDU record id, compared exactly as <see cref="OsduId"/> compares a required one.</summary>
    private static PropertyBuilder<string?> OptionalOsduId(PropertyBuilder<string?> property)
    {
        property.Metadata.SetValueComparer(new ValueComparer<string?>(
            (left, right) => string.Equals(left, right, StringComparison.Ordinal),
            value => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value),
            value => value));
        return property.UseCollation(OsduIdCollation);
    }
}
