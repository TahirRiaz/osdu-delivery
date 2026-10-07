using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Record status vocabulary (design.md section 7.4). <c>Held</c>, <c>Failed</c>, <c>Deleted</c> and <c>Reverted</c> are terminal
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

    /// <summary>
    /// Put back by a reversal to the version OSDU held before the run that was reversed (docs/reversal-plan.md): OSDU
    /// holds that version again, as a new one, and the record is blocked from redelivery while the source is unchanged. A
    /// release makes it delivered again.
    /// </summary>
    Reverted,

    /// <summary>
    /// Holds a rendered document that refers to a record another record of the ledger holds and has not delivered, and
    /// goes back to pending when that one lands (docs/interfaces-design.md section 7). Waiting is not a try: it charges
    /// nothing, and no operator is needed.
    /// </summary>
    Waiting,
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

    /// <summary>
    /// An earlier version was written back as a new version: by a reversal (phase <see cref="AttemptPhases.Reverse"/>), the
    /// version OSDU held before the run it reversed; by an operator (phase <see cref="AttemptPhases.RestorePrevious"/>), the
    /// version before the latest.
    /// </summary>
    Restored,
}

/// <summary>The phases of the attempts that record a decision not to send, beside the delivery phases the worker reports.</summary>
public static class AttemptPhases
{
    /// <summary>A skipped attempt: the source carried a version older than the one delivered or queued.</summary>
    public const string Stale = "stale";

    /// <summary>A skipped attempt: the final hash check found OSDU already holding the queued document and payload.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>
    /// A skipped attempt the intake writes: the ingestion table changed the row, and the document it renders is the one
    /// OSDU holds or the one already queued, so nothing is sent. It records the change, with the row's new origin.
    /// </summary>
    public const string Identical = "identical";

    /// <summary>A held attempt: the ingestion table marked the record row deleted, and a deleted row is never delivered.</summary>
    public const string SourceDeleted = "source-deleted";

    /// <summary>
    /// A skipped attempt a sync writes: the record's row is not in the ingestion table, or not in the scope the record was
    /// planned under. The record keeps its status: what OSDU holds is removed only by a removal someone asks for.
    /// </summary>
    public const string SourceMissing = "source-missing";

    /// <summary>
    /// What a reversal did to a record (docs/reversal-plan.md): restored (outcome restored), removed (outcome deleted),
    /// passed over (outcome skipped) or failed, each saying why in its result.
    /// </summary>
    public const string Reverse = "reverse";

    /// <summary>
    /// An operator took the record's latest version out of being current (docs/reversal-plan.md, Restoring the previous
    /// version): the version before it was written back as a new version (outcome restored), naming both in its result.
    /// </summary>
    public const string RestorePrevious = "restore-previous";
}

public enum VerifyOutcome
{
    Match,
    Drifted,
    Missing,
    Error,
}

/// <summary>What a sync found of one record's row that the ledger has to take in.</summary>
public sealed record SourceSyncFinding
{
    public required DeliveryKey DeliveryKey { get; init; }

    /// <summary>When the ingestion table first inserted the row, for a record whose ledger holds another moment or none.</summary>
    public DateTime? InsertedUtc { get; init; }

    /// <summary>The row changed, or was marked deleted, since the version the ledger stands at: the flow's next run plans it.</summary>
    public bool RequestPlan { get; init; }

    /// <summary>Why the row was not found (gone from the table, or out of the record's scope), for the attempt that says so.</summary>
    public string? NotFound { get; init; }

    /// <summary>The origin the ledger last recorded for the row, which the attempt of a row not found names.</summary>
    public RecordOrigin Origin { get; init; }
}

/// <summary>What the ledger wrote of a sync's findings: arrivals set, plans newly requested, rows reported not found.</summary>
public sealed record SourceSyncApplied(int Arrivals, int PlansRequested, int NotFound);

/// <summary>
/// A version of a record's ingestion row as its attempts recorded it: its origin, when the ingestion table marked the row
/// deleted (for the hold of a deleted row), and when the ledger first recorded it.
/// </summary>
public sealed record RecordOriginSeen(RecordOrigin Origin, DateTime? DeletedUtc, DateTime FirstRecordedUtc);

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

    /// <summary>
    /// The OSDU partition the row belongs to, as the ledger's directory names it: filled when the ledger reads the row, and
    /// null for a ledger not yet placed in a partition. A write takes the partition of its ledger identity, never this.
    /// </summary>
    public string? Partition { get; init; }


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

    /// <summary>Records held, failed, deleted or reverted earlier whose source has not changed; they need a release.</summary>
    public long Blocked { get; init; }

    public long Delivered { get; init; }

    public long Held { get; init; }

    public long Failed { get; init; }

    /// <summary>Records of the submission still waiting, when it closed, for a record they refer to that has not landed; they go out once it does.</summary>
    public long Waiting { get; init; }

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

    /// <summary>
    /// The OSDU partition the row belongs to, as the ledger's directory names it: filled when the ledger reads the row, and
    /// null for a ledger not yet placed in a partition. A write takes the partition of its ledger identity, never this.
    /// </summary>
    public string? Partition { get; init; }


    public required string SourceKey { get; init; }

    /// <summary>The record's key tuple as a JSON array of strings, in the flow's key order: what a key-scoped read uses.</summary>
    public string? SourceKeyJson { get; init; }

    /// <summary>Human-readable label from the mapping's identity.label template (for search and display only).</summary>
    public string? Label { get; init; }

    /// <summary>
    /// The values of the columns the mapping declares as identities (<c>dataset.identity</c>): a wellbore id, a well
    /// name, whatever an operator holds when they come looking. Search only, never part of the record.
    /// </summary>
    public IReadOnlyList<string> Identities { get; init; } = [];

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

    /// <summary>
    /// When the ingestion table first inserted the record's row (SQLFlow's <c>InsertedDate_DW</c>), as the last plan that
    /// read the row saw it: the row's arrival, which later changes never move. Null when the table does not carry it, or
    /// when no plan has read the row since the ledger began keeping it.
    /// </summary>
    public DateTime? SourceInsertedUtc { get; init; }

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

    /// <summary>
    /// When the ingestion table marked the row the pending state was read from deleted, for a record held because of it:
    /// the hold's attempt records the moment under <see cref="AttemptPhases.SourceDeleted"/>. The attempt keeps it, not
    /// the record.
    /// </summary>
    public DateTime? PendingSourceDeletedUtc { get; init; }

    public string? PendingMetadataHash { get; init; }

    public string? PendingPayloadHash { get; init; }

    /// <summary>The payload watermark of the pending work, when it carries a payload.</summary>
    public DateTime? PendingPayloadModifiedUtc { get; init; }

    /// <summary>Where the pending payload's files are listed from (folder and pattern), or null when no payload is pending.</summary>
    public string? PendingPayloadLocation { get; init; }

    public bool PendingMetadata { get; init; }

    public bool PendingPayload { get; init; }

    /// <summary>
    /// The OSDU ids the pending document refers to through the properties its template declares relationships for,
    /// with the property holding each. A record waits for the ones another record of the ledger holds and has not
    /// delivered.
    /// </summary>
    public IReadOnlyList<RecordReference> PendingReferences { get; init; } = [];

    /// <summary>While the record is <see cref="RecordStatus.Waiting"/>: the OSDU id of the record it waits for.</summary>
    public string? WaitingFor { get; init; }

    /// <summary>
    /// Set when the record was held, failed, deleted or reverted and not released since. A blocked record is planned again
    /// only when its source changes (fingerprint moved) or an operator releases it.
    /// </summary>
    public bool Blocked { get; init; }

    /// <summary>
    /// While the record is blocked, held or failed: the problem that keeps it so (<see cref="ProblemSignature"/>), which
    /// every record refused for the same reason shares; null otherwise, and until the ledger has sorted a record blocked
    /// before it kept problems. Written by the ledger from the record's error, never by a caller.
    /// </summary>
    public long? ProblemHash { get; init; }

    /// <summary>
    /// What the last check of the record's document against its schema came to, written by the gate before a record is
    /// sent (<see cref="Validation.ValidationOutcomes"/>: valid, invalid, unverified); null until a document was checked.
    /// </summary>
    public string? ValidationOutcome { get; init; }

    /// <summary>How many problems that check found.</summary>
    public long? ValidationProblems { get; init; }

    /// <summary>When that check was made.</summary>
    public DateTime? ValidatedUtc { get; init; }

    /// <summary>The metadata hash of the pending document an operator's release accepted as it is, whatever its verdict.</summary>
    public string? AcceptedMetadataHash { get; init; }

    /// <summary>Whether the pending document is the one an operator's release accepted.</summary>
    public bool PendingAccepted => PendingMetadataHash is not null && string.Equals(PendingMetadataHash, AcceptedMetadataHash, StringComparison.Ordinal);

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

    /// <summary>When the ingestion table marked that row deleted, for the hold of a deleted row; null otherwise.</summary>
    public DateTime? SourceDeletedUtc { get; init; }
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

    /// <summary>What the check of the try's document came to, which the record keeps; null for a try that checked none.</summary>
    public RecordValidation? Validation { get; init; }
}

/// <summary>What a check of a record's document came to, as the record keeps it: the outcome, the problems, and when.</summary>
public sealed record RecordValidation(string Outcome, long Problems, DateTime CheckedUtc);

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

    /// <summary>What the plan said about the record, for the attempt a stale or identical skip writes.</summary>
    public required string Reason { get; init; }

    /// <summary>The record's key tuple, as the source read it.</summary>
    public string? SourceKeyJson { get; init; }

    /// <summary>The ingestion fingerprint the source carried.</summary>
    public string? SourceFingerprint { get; init; }

    public DateTime? SourceModifiedUtc { get; init; }

    /// <summary>The ingestion file and row the plan read, for the attempt a stale or identical skip writes.</summary>
    public RecordOrigin Origin { get; init; }

    /// <summary>When the ingestion table first inserted the row, as the plan read it.</summary>
    public DateTime? SourceInsertedUtc { get; init; }

    /// <summary>The payload watermark the source carried.</summary>
    public DateTime? PayloadModifiedUtc { get; init; }

    /// <summary>The render context the record was rendered under (a rendered skip advances the record to it).</summary>
    public string? RenderContext { get; init; }

    /// <summary>The platform run of the intake, for the attempt a stale or identical skip writes.</summary>
    public Guid? RunId { get; init; }
}

/// <summary>What staging a batch of pending work did.</summary>
/// <param name="Staged">Records whose pending work was written (queued behind an in-flight delivery included).</param>
/// <param name="Refused">
/// Records refused because the ledger already holds a newer source version or payload for them, delivered or queued
/// by a concurrent intake since this one read them. They are stale and are recorded as such.
/// </param>
/// <param name="Conflicts">
/// Records refused because another record, of another flow or of the same one, has claimed the OSDU id they would be
/// written to, or because another record of the same staging gives the same id. Nothing was written for them; the caller
/// holds them with the owner named.
/// </param>
public sealed record PendingStaging(int Staged, IReadOnlyList<DeliveryKey> Refused, IReadOnlyList<TargetIdConflict> Conflicts)
{
    public static PendingStaging Empty { get; } = new(0, [], []);
}

/// <summary>A record whose OSDU id another record has claimed: the id, and the record that holds it.</summary>
/// <param name="DeliveryKey">The record that was not staged.</param>
/// <param name="TargetId">The OSDU id it would have been written to.</param>
/// <param name="OwnerFlowId">The flow whose record claimed the id.</param>
/// <param name="OwnerFlowName">That flow's name as its last submission recorded it, when the ledger knows it.</param>
/// <param name="OwnerDeliveryKey">The record that claimed the id, when it is of the same flow as the refused one.</param>
/// <param name="OwnerSourceKey">That record's source key, as the ledger shows it.</param>
public sealed record TargetIdConflict(
    DeliveryKey DeliveryKey, string TargetId, Guid OwnerFlowId, string? OwnerFlowName, DeliveryKey? OwnerDeliveryKey = null, string? OwnerSourceKey = null)
{
    /// <summary>What the held record says about the conflict: which id, whose it is, and how to resolve it.</summary>
    public string Describe()
    {
        if (OwnerDeliveryKey is { } record)
        {
            // Two records of one flow: their keys differ and give the same id, which only an id made from the key's values
            // can, or the ledger holds the id under a key the mapping no longer derives (dataset.system or key changed).
            var owner = OwnerSourceKey is null ? $"record {record}" : $"record {OwnerSourceKey} ({record})";
            return $"OSDU id {TargetId} is already claimed by {owner} of this flow, and one OSDU record belongs to one record of the ledger. "
                + "Two keys of this flow give the same id: keep one of them in the source. When the mapping's dataset.system or key changed "
                + "since that record was delivered, the id stays that record's: put the mapping back, or remove the flow's records from OSDU "
                + "(the record scope) and deliver them under a ledger of their own (the interface's ledger:), or delete them from the ledger "
                + "with the removal.";
        }

        var flow = OwnerFlowName is null ? $"flow {OwnerFlowId:D}" : $"flow '{OwnerFlowName}' ({OwnerFlowId:D})";
        return $"OSDU id {TargetId} is already claimed by {flow}, and one OSDU record belongs to one flow. Deliver this flow "
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

/// <summary>
/// One thing records were built without: a value no record of the type answered to (<c>unlisted</c>), a key no row was
/// listed under (<c>listed</c>, with no rows), an id written without its record (<c>unverified</c>), or a path that held
/// nothing (<c>empty</c>). <see cref="Key"/> is what was looked for (the record id for a path read empty or an unverified
/// id), <see cref="Path"/> the field it was looked for under, and <see cref="Records"/> how many records were built so.
/// </summary>
public sealed record CacheGap(string TypeName, string Path, Snapshots.CacheUsageKind Kind, string Key, string Value, long Records);

/// <summary>One page of the gaps of a partition's cache, and how many there are in all.</summary>
public sealed record CacheGapPage(IReadOnlyList<CacheGap> Items, int Total);

/// <summary>What one rollout pass did, and what is left of the tag.</summary>
public sealed record UpdateRolloutBatch(long TagId, long Marked, long Processed, long Affected, bool Completed);

/// <summary>One cache change and what happens about it, covering every record built from the value that moved.</summary>
public sealed record UpdateTag
{
    /// <summary>The most characters of <see cref="ItemId"/> the ledger keeps.</summary>
    public const int MaxItemIdLength = 512;

    /// <summary>The most characters of <see cref="Path"/>, <see cref="OldValue"/> and <see cref="NewValue"/> the ledger keeps.</summary>
    public const int MaxTextLength = 400;

    /// <summary>A text as the ledger keeps it: whole when it fits, else its first <paramref name="max"/> characters.</summary>
    public static string? Kept(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];

    public long TagId { get; init; }

    public string Kind { get; init; } = "cache";

    /// <summary>The partition whose cache the change was found in.</summary>
    public required string Scope { get; init; }

    public required string TypeName { get; init; }

    public required string ItemId { get; init; }

    public required string Path { get; init; }

    /// <summary>
    /// changed, removed, unmatched, listed (a lookup table now lists a key records looked up and found no row under, or a
    /// type of OSDU records now holds a record by a value records found none by), relisted (the rows a key finds are no
    /// longer the ones records read every one of), or found (the cache now holds a record records reference as an
    /// unverified id).
    /// </summary>
    public required string Change { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public string? FromVersion { get; init; }

    public required string ToVersion { get; init; }

    /// <summary>auto or approve.</summary>
    public required string Mode { get; init; }

    /// <summary>pending, approved, rejected, rolling (records being marked), delivering (every record marked, some not yet rendered again by their flow) or applied.</summary>
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

    /// <summary>
    /// The records of each flow still built from the value that moved, as the ledger listed the change: the flows that have
    /// not rendered them again since. Empty for a change rolled out or rejected.
    /// </summary>
    public IReadOnlyDictionary<Guid, long> WaitingByFlow { get; init; } = new Dictionary<Guid, long>();

    /// <summary>The records, of every flow, still built from the value that moved.</summary>
    public long Waiting => WaitingByFlow.Values.Sum();

    public string Describe() => Change switch
    {
        "removed" => $"{TypeName} '{ItemId}' is no longer in the cache (it held {Path} = '{OldValue}')",
        "unmatched" => $"{TypeName} '{ItemId}' no longer matches by {Path} = '{OldValue}'",
        "found" => $"{TypeName} now holds '{ItemId}', which delivered records reference as an unverified id",
        "listed" => NewValue is null
            ? $"{TypeName} now lists '{OldValue}' as '{ItemId}', which gives no {Path}"
            : $"{TypeName} now lists '{OldValue}' as '{ItemId}', giving {Path} = '{NewValue}'",
        "relisted" => $"{TypeName} rows whose {Path} holds '{ItemId}' are now {Rows(NewValue)}, where they were {Rows(OldValue)}",
        _ when OldValue is null => $"{TypeName} '{ItemId}': {Path} gave no value and now gives '{NewValue}'",
        _ => $"{TypeName} '{ItemId}': {Path} changed from '{OldValue}' to '{NewValue}'",
    };

    /// <summary>The rows a listing names, as a description reads them.</summary>
    private static string Rows(string? ids) => string.IsNullOrEmpty(ids) ? "none" : ids;
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

    /// <summary>Records a reversal put back to the version OSDU held before the run it reversed, blocked until released.</summary>
    public long Reverted { get; init; }

    /// <summary>Records waiting for a record they refer to that has not landed.</summary>
    public long Waiting { get; init; }

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

    /// <summary>Records the batch's claim found waiting for a record they refer to, and did not send.</summary>
    public long Waiting { get; init; }

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

/// <summary>
/// A claimed batch, the lease it is drained under, and the pending records the lease holds; beside them, the records of
/// the batch the claim found waiting for a record they refer to.
/// </summary>
public sealed record ClaimedWorkBatch(WorkBatchState Batch, LeaseState Lease, IReadOnlyList<RecordState> Records)
{
    public IReadOnlyList<WaitingRecord> Waiting { get; init; } = [];
}

/// <summary>
/// Records due for a retry, claimed together under one lease; no lease when nothing was due. Beside them, the due records
/// the claim found waiting for a record they refer to, which it did not take.
/// </summary>
public sealed record ClaimedRecords(LeaseState? Lease, IReadOnlyList<RecordState> Records)
{
    public static ClaimedRecords None { get; } = new(null, []);

    public IReadOnlyList<WaitingRecord> Waiting { get; init; } = [];
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

/// <summary>
/// What a forced redelivery re-sends: a half of the record, and, for a payload sent in parts, the parts of it (files,
/// bulk, workflow); no parts means every part.
/// </summary>
public sealed record RedeliverSelection(RedeliverScope Scope, IReadOnlyList<string> Parts)
{
    public override string ToString()
        => Parts.Count == 0 ? Scope.ToString().ToLowerInvariant() : string.Join(" and ", Parts);
}

/// <summary>How a free-text search is applied. Prefix search uses the indexes and answers in milliseconds.</summary>
public enum SearchMode
{
    Prefix,
    Contains,
}

/// <summary>
/// What a release reaches: the records named by key, the records one problem keeps blocked, or every blocked record of
/// the flow.
/// </summary>
public sealed record ReleaseSelection
{
    private ReleaseSelection(IReadOnlyList<DeliveryKey>? keys, long? problem)
    {
        Keys = keys;
        Problem = problem;
    }

    /// <summary>Every held, failed and deleted record of the flow that is blocked.</summary>
    public static ReleaseSelection EveryBlocked { get; } = new(null, null);

    /// <summary>The records named; a waiting one among them is sent without waiting.</summary>
    public IReadOnlyList<DeliveryKey>? Keys { get; }

    /// <summary>The problem whose records are released (<see cref="ProblemSignature"/>).</summary>
    public long? Problem { get; }

    /// <summary>The records named by key.</summary>
    public static ReleaseSelection Named(IEnumerable<DeliveryKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return new ReleaseSelection(keys.Distinct().ToList(), null);
    }

    /// <summary>The records one problem keeps blocked.</summary>
    public static ReleaseSelection OfProblem(long problem) => new(null, problem);
}

/// <summary>
/// One problem keeping records of a ledger blocked (<see cref="ProblemSignature"/>): what it says, and how many records it
/// keeps blocked, counted from the records.
/// </summary>
public sealed record ProblemGroup
{
    public required long Problem { get; init; }

    /// <summary>The error with every part that names a record replaced: what the problem's records have in common.</summary>
    public required string Pattern { get; init; }

    /// <summary>The records it keeps blocked: the held and the failed together.</summary>
    public required long Records { get; init; }

    /// <summary>Of those, the ones held: a data problem, or a refusal that is not retried.</summary>
    public long Held { get; init; }

    /// <summary>Of those, the ones failed: the retries ran out.</summary>
    public long Failed { get; init; }

    /// <summary>When the record of the problem that changed longest ago last changed.</summary>
    public DateTime OldestUtc { get; init; }

    /// <summary>When its most recently changed record last changed.</summary>
    public DateTime NewestUtc { get; init; }

    /// <summary>Its most recently changed record, with the error as that record carries it.</summary>
    public RecordState? Example { get; init; }

    /// <summary>
    /// Where the problem lies, as its newest and its oldest record say: a set error when they name the same values (or the
    /// pattern names none), row errors when they name different ones. A problem's samples tell it more surely.
    /// </summary>
    public ProblemShape Shape { get; init; }

    /// <summary>The values the example names (<see cref="ProblemSignature.Values"/>): for a set error, the values every record names.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];
}

/// <summary>A ledger's problems, the largest first, with what the page leaves out.</summary>
/// <param name="Problems">The problems listed.</param>
/// <param name="TotalProblems">How many problems keep the ledger's records blocked, listed or not.</param>
/// <param name="TotalRecords">How many records they keep blocked.</param>
/// <param name="Unsorted">
/// Blocked records the ledger has not sorted into a problem yet: held or failed before it kept problems, which the
/// control plane's backfill sorts a page at a time.
/// </param>
public sealed record ProblemListing(IReadOnlyList<ProblemGroup> Problems, long TotalProblems, long TotalRecords, long Unsorted);

/// <summary>An ingestion file some records of one problem were left at, and how many; a null name for the records that name none.</summary>
public sealed record ProblemFile(string? FileName, long Records);

/// <summary>A record listing: filter, search, sort and page.</summary>
public sealed record RecordQuery
{
    public RecordStatus? Status { get; init; }

    /// <summary>A delivery key, a target id, or a prefix of the label, the source key or the origin file name.</summary>
    public string? Search { get; init; }

    public SearchMode Mode { get; init; } = SearchMode.Prefix;

    /// <summary>
    /// Only records whose last submission is this one: the one that last planned them, whether it delivered them or
    /// found them unchanged. A later submission that touches a record moves it on, so this is "what the submission
    /// left behind", not "what it delivered"; for that, see <see cref="DeliveredBySubmissionId"/>.
    /// </summary>
    public Guid? SubmissionId { get; init; }

    /// <summary>
    /// Only records this submission delivered, resolved through the delivered attempts it wrote. A record keeps that
    /// attempt however many submissions touch it afterwards, so this is the set an operator means by "the batch we
    /// ran": what it put into OSDU, findable and removable after the records have moved on.
    /// </summary>
    public Guid? DeliveredBySubmissionId { get; init; }

    /// <summary>
    /// Only records the given platform run touched, resolved through the attempts that run wrote. Records carry no
    /// run of their own: a record is delivered by many runs over its life, and the attempt is the thing that
    /// belongs to one.
    /// </summary>
    public Guid? RunId { get; init; }

    /// <summary>Only delivered records whose last verify found drift or a missing record.</summary>
    public bool Drifted { get; init; }

    /// <summary>Only the blocked records one problem keeps blocked (<see cref="ProblemSignature"/>), read from the problem index.</summary>
    public long? Problem { get; init; }

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

/// <summary>How many records of one ledger went to one OSDU partition, as the partition their OSDU id names.</summary>
public sealed record PartitionRecords(string Partition, long Records);

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

    /// <summary>
    /// Candidates the lookup across every flow takes from each identity index before ordering them, and how far back the
    /// recency listing that answers an empty search box reaches.
    /// </summary>
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
    /// <summary>The partition whose audit trail is read, by its data-partition-id; null reads every partition's.</summary>
    public string? Partition { get; init; }

    public Guid? FlowId { get; init; }

    /// <summary>
    /// The activities about one record: those made for it alone and, with <see cref="FlowId"/>, those made for many records
    /// that reached it (a release of a whole flow or of a problem's records), which name it among the records they changed.
    /// </summary>
    public Guid? DeliveryKey { get; init; }

    public Guid? SubmissionId { get; init; }

    public Guid? RunId { get; init; }

    public string? Kind { get; init; }

    public string? Actor { get; init; }

    public string? Outcome { get; init; }

    /// <summary>False leaves out the runs that changed nothing, true reads only them, and null reads every activity.</summary>
    public bool? Idle { get; init; }

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

/// <summary>The statuses of an assertion run: running while its tests run, then how they came out, or why it stopped.</summary>
public static class AssertionRunStatus
{
    public const string Running = "running";

    /// <summary>Every test passed, or failed only assertions of a severity that does not fail the run.</summary>
    public const string Passed = "passed";

    /// <summary>A test failed an assertion whose severity fails the run.</summary>
    public const string Failed = "failed";

    /// <summary>The run could not run its tests (the platform unreachable, the flow unresolvable), or a test could not be evaluated.</summary>
    public const string Errored = "errored";

    public const string Cancelled = "cancelled";
}

/// <summary>The outcomes of one test in one run, and of one assertion of it.</summary>
public static class TestOutcomes
{
    /// <summary>Every assertion held (an info assertion that failed does not change it).</summary>
    public const string Passed = "passed";

    /// <summary>An error-severity assertion failed.</summary>
    public const string Failed = "failed";

    /// <summary>A warning-severity assertion failed, and no error-severity one did.</summary>
    public const string Warned = "warned";

    /// <summary>The test could not be evaluated: its definition does not fit its template, or reading OSDU failed.</summary>
    public const string Errored = "errored";

    /// <summary>The test does not run in the partition the run tested, or the run stopped before it.</summary>
    public const string Skipped = "skipped";

    public static IReadOnlyList<string> All { get; } = [Passed, Failed, Warned, Errored, Skipped];
}

/// <summary>How many of a run's tests came out each way.</summary>
public sealed record AssertionCounts(int Tests, int Passed, int Failed, int Warned, int Errored, int Skipped)
{
    public static AssertionCounts None { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>The counts of <paramref name="outcomes"/>, one outcome per test.</summary>
    public static AssertionCounts Of(IEnumerable<string> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var list = outcomes.ToList();
        return new AssertionCounts(
            list.Count,
            list.Count(o => o == TestOutcomes.Passed),
            list.Count(o => o == TestOutcomes.Failed),
            list.Count(o => o == TestOutcomes.Warned),
            list.Count(o => o == TestOutcomes.Errored),
            list.Count(o => o == TestOutcomes.Skipped));
    }
}

/// <summary>One run of an assertion flow's tests as the ledger holds it (docs/assertions-design.md section 7).</summary>
public sealed record AssertionRunState
{
    public long AssertionRunId { get; init; }

    /// <summary>The ledger identity of the flow in the partition it tested.</summary>
    public required Guid FlowId { get; init; }

    /// <summary>The partition the run tested, as the ledger's directory names it: filled when the ledger reads the row.</summary>
    public string? Partition { get; init; }

    public required string FlowName { get; init; }

    public Guid? RunId { get; init; }

    public string Actor { get; init; } = "unknown";

    /// <summary>The tests the run was asked for, as JSON; null when it ran every test.</summary>
    public string? Selection { get; init; }

    public string Status { get; init; } = AssertionRunStatus.Running;

    public AssertionCounts Counts { get; init; } = AssertionCounts.None;

    public string? DefinitionsHash { get; init; }

    public DateTime StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public string? Error { get; init; }
}

/// <summary>One test's result in one run, as the ledger holds it: the summary columns, and the whole result as JSON.</summary>
public sealed record AssertionResultState
{
    public long ResultId { get; init; }

    public long AssertionRunId { get; init; }

    public required Guid FlowId { get; init; }

    public required string TestName { get; init; }

    public required string Kind { get; init; }

    public required string Outcome { get; init; }

    /// <summary>The heaviest severity among the assertions that failed; null when none did.</summary>
    public string? Severity { get; init; }

    public long? Matched { get; init; }

    public long? Evaluated { get; init; }

    public bool Sampled { get; init; }

    public int Assertions { get; init; }

    public int FailedAssertions { get; init; }

    public required string DefinitionHash { get; init; }

    public long DurationMs { get; init; }

    public string? Error { get; init; }

    /// <summary>The whole result as JSON (<see cref="Engine.Assertions.TestResult"/>).</summary>
    public required string Detail { get; init; }

    public DateTime StartedUtc { get; init; }

    public DateTime CompletedUtc { get; init; }
}

/// <summary>
/// One operator or scheduler action, persisted for the audit trail: who did what, when, with which inputs, and
/// what came of it. Record-level history lives in attempts; this is the history of runs and interventions.
/// </summary>
public sealed record ActivityRecord
{
    public long ActivityId { get; init; }

    public required Guid FlowId { get; init; }

    /// <summary>
    /// The OSDU partition the row belongs to, as the ledger's directory names it: filled when the ledger reads the row, and
    /// null for a ledger not yet placed in a partition. A write takes the partition of its ledger identity, never this.
    /// </summary>
    public string? Partition { get; init; }


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

    /// <summary>
    /// A run that completed having changed nothing: it planned, held, blocked and sent no record, and left none waiting.
    /// Only a completed activity is idle, and an intervention never is.
    /// </summary>
    public bool Idle { get; init; }
}

/// <summary>The kinds of flow a ledger belongs to.</summary>
public static class LedgerKinds
{
    public const string Delivery = "delivery";

    public const string Retrieval = "retrieval";

    public const string Assertion = "assertion";

    public const string Dimension = "dimension";
}

/// <summary>
/// One ledger as the ledger's directory holds it (docs/ledger.md, Partitions): the rows of one flow (or interface of a
/// source) in one partition, under one ledger identity. The partition is part of the key of every row the ledger keeps.
/// </summary>
public sealed record LedgerEntry
{
    /// <summary>The ledger identity every row of the ledger carries.</summary>
    public required Guid FlowId { get; init; }

    /// <summary>
    /// The data-partition-id the ledger belongs to. Null, when read, for a ledger the upgrade to partition keys could not
    /// place, until its next run adopts it; a registration always names one.
    /// </summary>
    public string? Partition { get; init; }

    /// <summary><see cref="LedgerKinds.Delivery"/>, <see cref="LedgerKinds.Retrieval"/> or <see cref="LedgerKinds.Assertion"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The flow the ledger belongs to: the source, for an interface of a source.</summary>
    public required string FlowName { get; init; }

    /// <summary>The interface of a source, or empty for a flow in the single form.</summary>
    public string Interface { get; init; } = string.Empty;

    /// <summary>The ledger's name as pages show it.</summary>
    public required string LedgerName { get; init; }

    public DateTime RegisteredUtc { get; init; }
}

/// <summary>
/// The ledger (design.md section 7): submissions, records and append-only attempts, with leasing for the worker,
/// plus the activity audit trail and the catalog read-model. Implemented over the <c>osdu</c> schema through EF Core; the
/// interface keeps the engine free of EF.
/// </summary>
public interface ILedger
{
    /// <summary>
    /// Registers the ledger a run is about to write, in the partition the run delivers to (docs/ledger.md, Partitions), and
    /// returns it as the directory now holds it. Every write of a ledger's rows needs its registration; every row carries
    /// the partition in its key. A ledger belongs to one partition: registering it in another is refused, naming both. A
    /// ledger the upgrade could not place is adopted into the partition registered, unless its records were delivered to
    /// another partition.
    /// </summary>
    /// <exception cref="DeliveryException">The ledger belongs to another partition, or holds records delivered to one.</exception>
    Task<LedgerEntry> RegisterLedgerAsync(LedgerEntry ledger, CancellationToken ct = default);

    /// <summary>The ledger <paramref name="flowId"/> names, as the directory holds it, or null when no run has registered it.</summary>
    Task<LedgerEntry?> GetLedgerAsync(Guid flowId, CancellationToken ct = default);

    /// <summary>The ledgers of <paramref name="partition"/> (by its data-partition-id), or every ledger when it is null.</summary>
    Task<IReadOnlyList<LedgerEntry>> ListLedgersAsync(string? partition, CancellationToken ct = default);

    Task<SubmissionState?> GetSubmissionAsync(Guid submissionId, CancellationToken ct = default);

    /// <summary>Registers a submission. Returns the existing one when the id was seen before (idempotent).</summary>
    Task<(SubmissionState Submission, bool Created)> RegisterSubmissionAsync(SubmissionState submission, CancellationToken ct = default);

    Task UpdateSubmissionAsync(SubmissionState submission, CancellationToken ct = default);

    /// <summary>
    /// A flow's submissions, newest first, or when <paramref name="flowId"/> is null, those of every flow of
    /// <paramref name="partition"/>, or of every partition when that is null too.
    /// </summary>
    Task<IReadOnlyList<SubmissionState>> ListSubmissionsAsync(Guid? flowId, int max, string? partition = null, CancellationToken ct = default);

    Task<IReadOnlyDictionary<DeliveryKey, RecordState>> GetRecordsAsync(Guid flowId, IEnumerable<DeliveryKey> keys, CancellationToken ct = default);

    Task<RecordState?> GetRecordAsync(Guid flowId, DeliveryKey key, CancellationToken ct = default);

    /// <summary>
    /// Which of <paramref name="targetIds"/> a record of the ledger has claimed, of any flow and partition. Ids compare
    /// exactly, as OSDU compares them; one seek of the claim index per id.
    /// </summary>
    Task<IReadOnlySet<string>> ClaimedTargetIdsAsync(IReadOnlyCollection<string> targetIds, CancellationToken ct = default);

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
    /// The records of a flow the ledger asked to be planned again, in the order they were asked for (then by delivery key)
    /// after <paramref name="after"/>, at most <paramref name="max"/>: what a run pages through to plan them as a key-scoped
    /// read. Each page is a seek of the index that holds the requests alone, however many are waiting.
    /// </summary>
    Task<IReadOnlyList<PlanRequestedRecord>> ListPlanRequestedAsync(Guid flowId, PlanRequestedRecord? after, int max, CancellationToken ct = default);

    /// <summary>Clears the request to plan records again for records a plan saw and left untouched (blocked ones).</summary>
    Task ClearPlanRequestedAsync(Guid flowId, IReadOnlyList<DeliveryKey> keys, CancellationToken ct = default);

    /// <summary>
    /// Claims up to <paramref name="max"/> of the flow's (or submission's) pending records that are due, under one new
    /// lease for <paramref name="owner"/>, after recovering the flow's leases that ran out. A due record that refers to a
    /// record another record of the ledger holds and has not delivered is not claimed but left waiting, as
    /// <paramref name="waits"/> says (every such record when null). The records come back holding the lease;
    /// <see cref="ClaimedRecords.None"/> when nothing was due.
    /// </summary>
    Task<ClaimedRecords> ClaimAsync(Guid flowId, Guid? submissionId, string owner, int max, TimeSpan lease, DateTime nowUtc, Guid? runId = null, WaitRules? waits = null, CancellationToken ct = default);

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

    /// <summary>Whether the ledger of <paramref name="flowId"/> holds any record: an index seek, however large the ledger.</summary>
    Task<bool> HoldsRecordsAsync(Guid flowId, CancellationToken ct = default);

    /// <summary>
    /// How many records of <paramref name="flowId"/> went to each OSDU partition, read from the partition their OSDU id names
    /// (the id delivered, or the one claimed before delivery), most first; a record with neither is not counted. It reads the
    /// flow's whole ledger, so it is asked when the ledgers of a flow that names its partitions are checked, never per record.
    /// </summary>
    Task<IReadOnlyList<PartitionRecords>> DeliveredPartitionsAsync(Guid flowId, CancellationToken ct = default);

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
    /// The completed or failed submissions of the flow, other than those <paramref name="except"/> names, that still hold
    /// records due for delivery with their rendered documents: records released back to pending after their run was over,
    /// and the records a stopped run handed back untried when it closed its submission as failed. At most
    /// <paramref name="max"/>; a caller sending all of them asks again naming the ones it sent. It reads the flow's pending
    /// records, which a run leaves few of once its own are sent.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListSettledSubmissionsWithDueWorkAsync(Guid flowId, IReadOnlyCollection<Guid> except, DateTime nowUtc, int max, CancellationToken ct = default);

    /// <summary>Registers a work batch the intake wrote (idempotent on submission and index).</summary>
    Task AddWorkBatchAsync(WorkBatchState batch, CancellationToken ct = default);

    /// <summary>
    /// Claims the oldest queued work batch of the flow, or of one submission, under a new lease for
    /// <paramref name="owner"/>, after recovering the flow's leases that ran out, and has the lease hold the batch's
    /// pending records that are due, except those left waiting as <paramref name="waits"/> says (see
    /// <see cref="ClaimAsync"/>). Null when nothing is claimable.
    /// </summary>
    Task<ClaimedWorkBatch?> ClaimWorkBatchAsync(Guid flowId, Guid? submissionId, string owner, TimeSpan lease, DateTime nowUtc, Guid? runId = null, WaitRules? waits = null, CancellationToken ct = default);

    /// <summary>
    /// Sends back to pending the flow's waiting records (the ones named, or all of them) whose wait is over: the record
    /// they wait for has landed, or no record the ledger holds is left to wait for. The next claim decides again. Returns
    /// how many went back.
    /// </summary>
    Task<int> ReleaseResolvedWaitsAsync(Guid flowId, IReadOnlyCollection<DeliveryKey>? keys, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// The records waiting for <paramref name="targetId"/>, in any flow of the partition of <paramref name="flowId"/>'s
    /// ledger (an id is referred to within its partition), at most <paramref name="max"/>, most recently updated first.
    /// </summary>
    Task<IReadOnlyList<RecordState>> ListWaitingForAsync(Guid flowId, string targetId, int max, CancellationToken ct = default);

    /// <summary>
    /// The ids among <paramref name="ids"/> that a record of the partition of <paramref name="flowId"/>'s ledger holds,
    /// other than a record removed from OSDU.
    /// </summary>
    Task<IReadOnlySet<string>> HeldIdsAsync(Guid flowId, IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>
    /// The records that hold <paramref name="targetId"/> as the id they are delivered to, in any flow of the partition of
    /// <paramref name="flowId"/>'s ledger, compared exactly: the one that claimed it first, then any held before it claimed
    /// one. At most <paramref name="max"/>.
    /// </summary>
    Task<IReadOnlyList<RecordState>> ListHoldersAsync(Guid flowId, string targetId, int max, CancellationToken ct = default);

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

    /// <summary>
    /// The versions of one of the flow's records' ingestion row its attempts recorded, newest first: each distinct origin
    /// (the stamp, file and row) an attempt was built from or recorded, and each moment the ingestion table marked the row
    /// deleted, with when the ledger first recorded it. Read over every attempt the record has, not a page of them; at
    /// most <paramref name="max"/>. The record's own delivered and queued origins are on the record.
    /// </summary>
    Task<IReadOnlyList<RecordOriginSeen>> ListOriginsAsync(Guid flowId, DeliveryKey key, int max, CancellationToken ct = default);

    /// <summary>
    /// One flow's records in delivery-key order after <paramref name="after"/>, at most <paramref name="max"/>: what a
    /// sync of every record pages through, one seek of the record's key a page.
    /// </summary>
    Task<IReadOnlyList<RecordState>> ListRecordsAsync(Guid flowId, DeliveryKey? after, int max, CancellationToken ct = default);

    /// <summary>The newest attempt of each of the flow's records named; a record that has none is not in the answer.</summary>
    Task<IReadOnlyDictionary<DeliveryKey, AttemptRecord>> LatestAttemptsAsync(Guid flowId, IReadOnlyList<DeliveryKey> keys, CancellationToken ct = default);

    /// <summary>
    /// Takes in what a sync found of the flow's records' rows: each arrival the ledger lacks or holds otherwise, a request
    /// to plan each record whose row changed or was marked deleted unseen, and an attempt
    /// (<see cref="AttemptPhases.SourceMissing"/>) for each record whose row is gone. A record keeps its status, and one
    /// already asked to be planned keeps the moment it was asked.
    /// </summary>
    Task<SourceSyncApplied> ApplySourceSyncAsync(Guid flowId, IReadOnlyList<SourceSyncFinding> findings, Guid? runId, CancellationToken ct = default);

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
    /// updated of them are returned. With <paramref name="status"/>, only the candidates in that state. With
    /// <paramref name="flowId"/>, the records of that ledger identity alone, its candidates read from its own tokens. With
    /// <paramref name="partition"/>, the records of that partition alone (the workbench reads one partition at a time); a
    /// ledger identity names its own partition.</summary>
    Task<IReadOnlyList<RecordState>> LookupAsync(string term, int max, RecordStatus? status = null, Guid? flowId = null, string? partition = null, CancellationToken ct = default);

    /// <summary>How many records a lookup matches, counting no further than <paramref name="limit"/>.</summary>
    Task<BoundedCount> CountLookupAsync(string term, int limit, RecordStatus? status = null, Guid? flowId = null, string? partition = null, CancellationToken ct = default);

    /// <summary>The ledger's most recently updated records across every flow, at most <paramref name="max"/> of them,
    /// newest first and ties broken by key: what the delivery system last took in, sent or was answered about, without a
    /// term to seek. With <paramref name="status"/>, only the records in that state; with <paramref name="flowId"/>, only
    /// the records of that ledger identity. It reads the end of a recency index, so it costs the same however many records
    /// the ledger holds, and it reaches no further back than <see cref="RecordListing.LookupCandidateLimit"/> records.
    /// With <paramref name="partition"/>, the records of that partition alone.</summary>
    Task<IReadOnlyList<RecordState>> ListRecentAsync(int max, RecordStatus? status = null, Guid? flowId = null, string? partition = null, CancellationToken ct = default);

    /// <summary>How many records the recency listing has to show, counting no further than <paramref name="limit"/>.</summary>
    Task<BoundedCount> CountRecentAsync(int limit, RecordStatus? status = null, Guid? flowId = null, string? partition = null, CancellationToken ct = default);

    /// <summary>The ledger identities among <paramref name="flowIds"/> that hold at least one record, in any state. It asks
    /// whether each holds one, never how many, so it costs one index seek per identity however many records the ledger
    /// holds.</summary>
    Task<IReadOnlySet<Guid>> FlowsWithRecordsAsync(IReadOnlyCollection<Guid> flowIds, CancellationToken ct = default);

    /// <summary>The ledger identities among <paramref name="flowIds"/> with at least one activity on the audit trail, of any
    /// kind or outcome. Like <see cref="FlowsWithRecordsAsync"/> it asks whether each has one, never how many: one index seek
    /// per identity however long the trail.</summary>
    Task<IReadOnlySet<Guid>> FlowsWithActivitiesAsync(IReadOnlyCollection<Guid> flowIds, CancellationToken ct = default);

    /// <summary>Delivered records due for the drift pass, oldest verification first.</summary>
    Task<IReadOnlyList<RecordState>> ListForVerifyAsync(Guid flowId, DateTime? verifiedBeforeUtc, int max, CancellationToken ct = default);

    Task RecordVerifyAsync(Guid flowId, DeliveryKey key, VerifyOutcome outcome, long? observedVersion, DateTime nowUtc, bool requeue, CancellationToken ct = default);

    /// <summary>
    /// Releases held, failed, deleted or reverted records: those with a pending document go back to pending for the worker,
    /// the others are unblocked and asked to be planned again by the flow's next run. Null keys means every blocked record.
    /// A waiting record named by key goes back to pending without its references, so it is sent without waiting any more;
    /// a release of the whole flow leaves waiting records to their wait.
    /// </summary>
    Task<int> ReleaseAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Releases the records <paramref name="selection"/> names, as the release by keys above does: a record still holding
    /// its rendered document goes back to pending, any other is unblocked and asked to be planned again, and a waiting
    /// record named by key is sent without waiting. No statement writes more than a slice of records, however many the
    /// selection reaches. A release of every blocked record, or of the records a problem keeps blocked, walks an index
    /// forward a page at a time and ends at its end; a record a run blocks with the same problem while it walks is
    /// released too. With <paramref name="activityId"/>, the statement that releases a record names it under that intervention
    /// (<c>osdu.ActivityRecord</c>), so the record's own history shows the release, who asked and when, however many
    /// records it reached. Returns how many records it released.
    /// </summary>
    Task<int> ReleaseAsync(Guid flowId, ReleaseSelection selection, long? activityId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// The problems keeping the flow's records blocked (<see cref="ProblemSignature"/>), the most records first and at most
    /// <paramref name="max"/> of them, each with its held and failed records, its files, when its records last changed and
    /// its most recently changed record as the example; with how many problems and records there are in all, and how many
    /// blocked records the ledger has not sorted into problems yet. Counted from the records, through the problem index alone.
    /// </summary>
    Task<ProblemListing> ListProblemsAsync(Guid flowId, int max, CancellationToken ct = default);

    /// <summary>One problem of the flow, counted as <see cref="ListProblemsAsync"/> counts it; null when no record has it.</summary>
    Task<ProblemGroup?> GetProblemAsync(Guid flowId, long problem, CancellationToken ct = default);

    /// <summary>
    /// The ingestion files the records one problem keeps blocked came from, the most records first and at most
    /// <paramref name="max"/>: the file of the version each record was left at.
    /// </summary>
    Task<IReadOnlyList<ProblemFile>> ListProblemFilesAsync(Guid flowId, long problem, int max, CancellationToken ct = default);

    /// <summary>
    /// Records of one problem spread evenly across it, at most <paramref name="count"/>: its newest, its oldest and the ones
    /// between at even steps of the problem index's order, so records loaded at different times, from different files,
    /// stand side by side. What an operator checks before releasing the problem's records together, and what tells a set
    /// error from row errors. Each sample is a seek of its own after one pass over the problem's range of the index.
    /// </summary>
    Task<IReadOnlyList<RecordState>> ListProblemSamplesAsync(Guid flowId, long problem, int count, CancellationToken ct = default);

    /// <summary>
    /// Forgets what OSDU holds for the records (the whole record, the metadata document or the payload) and asks the
    /// flow's next run to plan them again. Null keys means every record the flow has delivered. Returns how many records
    /// were marked.
    /// </summary>
    Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, RedeliverScope scope, DateTime nowUtc, CancellationToken ct = default)
        => ForceRedeliverAsync(flowId, keys, new RedeliverSelection(scope, []), nowUtc, ct);

    /// <summary>
    /// Marks records so the next plan sends again what <paramref name="selection"/> names: the named ones, or with null
    /// keys every record the flow has delivered, a slice at a time. A selection of payload parts
    /// (docs/interfaces-design.md section 5.5) leaves them on the record's delivered payload hash
    /// (<see cref="Model.PayloadParts.RedeliverMarker"/>), where the plan reads them and the next payload delivery replaces them.
    /// </summary>
    Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, RedeliverSelection selection, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// As the redelivery above, with every record it marks named under the intervention <paramref name="activityId"/>
    /// (<c>osdu.ActivityRecord</c>), so each record's history shows the redelivery that reached it, who asked and when. A
    /// redelivery of every delivered record walks them a page at a time, passing over those it marked. A record still
    /// blocked by a problem keeps its error, which its problem was read from.
    /// </summary>
    Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, RedeliverSelection selection, long? activityId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Asks records OSDU holds to be brought up to date: rendered again by the next plan that meets them, and sent only where
    /// they render differently. The named records, or with null keys every record the flow has delivered, a slice at a time;
    /// a named record OSDU does not hold is left as it is. Unlike a redelivery, nothing of what OSDU holds is forgotten: the
    /// delivered hashes stay, and decide what is sent. The source version a record was last planned under is cleared, so no
    /// plan passes it as unchanged without rendering it, whichever run meets it first; a plan that finds it unchanged writes
    /// that version back. Every record marked is named under the intervention <paramref name="activityId"/>. Returns how
    /// many were marked.
    /// </summary>
    Task<int> RequestRenderAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, long? activityId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Records what a removal did to a set of the flow's records, in one round trip. <see cref="RemovalScope.Record"/> and
    /// <see cref="RemovalScope.Everything"/> take the record out of OSDU, so the ledger marks it deleted and forgets the
    /// hashes and the version, without blocking it: the next run that reads its row delivers it again, as it would a
    /// record never delivered. <see cref="RemovalScope.History"/> leaves the record live, so its custody
    /// state is untouched and only the attempt is written. Either way every record gets its own attempt, saying
    /// which scope ran and who asked for it, because that attempt is how the removal is audited afterwards.
    /// </summary>
    Task MarkRemovedAsync(Guid flowId, IReadOnlyList<DeliveryKey> keys, RemovalScope scope, string worker, DateTime nowUtc, string? correlationId = null, CancellationToken ct = default);

    /// <summary>
    /// What OSDU held of each record before the write that left the version <paramref name="current"/> names for it, as the
    /// ledger tells it (<see cref="PriorVersion"/>): the attempt that delivered or restored that version, and the one before
    /// it. A record no such attempt names is <see cref="ReversalPriors.Unknown"/>.
    /// </summary>
    Task<IReadOnlyDictionary<DeliveryKey, PriorVersion>> PriorVersionsAsync(Guid flowId, IReadOnlyDictionary<DeliveryKey, long> current, CancellationToken ct = default);

    /// <summary>
    /// Deletes records from the ledger that were removed from OSDU (docs/ledger.md, Deleting a removed record from the
    /// ledger), a slice to a transaction. Only a record the ledger marks deleted goes, and none a lease holds; any other is
    /// left as it is. Each record deleted keeps one line (<see cref="PurgedRecordState"/>: what it was, its OSDU id and last
    /// version, who deleted it and when) and is named under <paramref name="activityId"/>; its attempts, its search entries
    /// and its row are deleted. The activities that name it stay, as the audit trail does. Returns the records it deleted.
    /// </summary>
    Task<IReadOnlyList<DeliveryKey>> PurgeRecordsAsync(
        Guid flowId, IReadOnlyList<DeliveryKey> keys, string actor, long? activityId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>What the ledger keeps of <paramref name="key"/> after the record was deleted from it, the latest time it was; null when it never was.</summary>
    Task<PurgedRecordState?> FindPurgedAsync(Guid flowId, DeliveryKey key, CancellationToken ct = default);

    /// <summary>
    /// Records that each record's version before the latest was written back as its current version (docs/reversal-plan.md,
    /// Restoring the previous version), a chunk to a transaction. A record is settled only while the ledger still holds it at
    /// the version that was replaced, with no lease and no work queued; one that moved on meanwhile is left as it is and
    /// returned, so the caller can say what OSDU now holds. A settled record becomes reverted and blocked at the new version,
    /// with the hashes and origin of the attempt that delivered or restored the version put back when the ledger holds one
    /// (none otherwise, so a release sends it again where it renders differently), and gets its own attempt (outcome
    /// restored, phase <see cref="AttemptPhases.RestorePrevious"/>) naming both versions and who asked. A record already
    /// blocked by an earlier write back keeps the source version it is blocked at.
    /// </summary>
    Task<IReadOnlyList<DeliveryKey>> MarkRestoredAsync(
        Guid flowId, IReadOnlyList<PreviousVersionRestored> restored, string worker, Guid? runId, DateTime nowUtc, string? correlationId = null, CancellationToken ct = default);

    /// <summary>
    /// Opens the reversal of <paramref name="source"/> in the flow's ledger, or resumes the one it holds
    /// (docs/reversal-plan.md): one reversal per source of a ledger, which the run <paramref name="runId"/> now works on. A new
    /// one fixes the submissions its source covers (a submission, or the submissions a run coordinated). Throws when the
    /// source is not the ledger's: a submission of another ledger, or a run that neither planned a submission of it nor
    /// delivered a record of it.
    /// </summary>
    Task<ReversalState> OpenReversalAsync(Guid flowId, string flowName, ReversalSource source, string actor, Guid? runId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Lists every record the reversal's source delivered as an item, with what OSDU held of it before, unless the listing
    /// is finished: the source's delivered attempts are read a page at a time in index order and each record is added once,
    /// so a listing stopped half way and started again adds only what is missing. <paramref name="progress"/> hears after
    /// every page. Returns how many it added and how many items the reversal holds.
    /// </summary>
    Task<ReversalCapture> CaptureReversalAsync(long reversalId, Func<ReversalCapture, Task>? progress, CancellationToken ct = default);

    /// <summary>
    /// The next items of a reversal to work on after <paramref name="after"/>, in key order, at most <paramref name="max"/>:
    /// pending, left sending by a run that stopped, failed, or passed over as busy. A run walks them forward once.
    /// </summary>
    Task<IReadOnlyList<ReversalItemState>> ListReversalWorkAsync(long reversalId, DeliveryKey? after, int max, CancellationToken ct = default);

    /// <summary>Marks items as sending before the run writes to OSDU for them, so a run that stops mid-write is known to have.</summary>
    Task MarkReversalSendingAsync(long reversalId, IReadOnlyList<DeliveryKey> keys, Guid? runId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Settles items of a reversal with their records, a slice to a transaction: each record a restore or a removal puts
    /// back becomes reverted or deleted and blocked, while it is still the record the source left; every item gets its
    /// attempt (phase reverse), is named under <paramref name="activityId"/> when it changed its record, and is settled.
    /// </summary>
    Task<ReversalSettled> SettleReversalAsync(
        long reversalId, IReadOnlyList<ReversalSettlement> settlements, string actor, long? activityId, Guid? runId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Ends the latest run's work on a reversal in <paramref name="status"/>, with the redacted <paramref name="failure"/> of one that failed.</summary>
    Task CloseReversalAsync(long reversalId, string status, string? failure, DateTime nowUtc, CancellationToken ct = default);

    Task<ReversalState?> GetReversalAsync(long reversalId, CancellationToken ct = default);

    /// <summary>The reversal of <paramref name="source"/> in the flow's ledger, or null when none was asked for.</summary>
    Task<ReversalState?> FindReversalAsync(Guid flowId, ReversalSource source, CancellationToken ct = default);

    /// <summary>The ledger's reversals, newest first.</summary>
    Task<IReadOnlyList<ReversalState>> ListReversalsAsync(Guid flowId, int max, CancellationToken ct = default);

    /// <summary>A reversal's records counted by state and outcome, read from its items.</summary>
    Task<ReversalCounts> CountReversalAsync(long reversalId, CancellationToken ct = default);

    /// <summary>
    /// A page of a reversal's items after <paramref name="after"/> in key order, every one or those of one
    /// <paramref name="outcome"/> (<see cref="ReversalItemStates.Pending"/> for those not settled yet).
    /// </summary>
    Task<IReadOnlyList<ReversalItemState>> ListReversalItemsAsync(long reversalId, string? outcome, DeliveryKey? after, int max, CancellationToken ct = default);

    /// <summary>
    /// What reversing <paramref name="source"/> would reach, writing nothing: the submissions it covers, how many records it
    /// delivered, and the first <paramref name="sample"/> of them in key order as the reversal would list them.
    /// </summary>
    Task<ReversalSourceRead> ReadReversalSourceAsync(Guid flowId, string flowName, ReversalSource source, int sample, CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="source"/> delivered a record of the flow's ledger, which is what a reversal of it would take:
    /// a delivered attempt under the submission, or under a submission the run planned (a seek of the submission's index), or
    /// one of the run's own, read from at most <see cref="OsduLedger.SourceDeliveredProbe"/> of the run's attempts in this
    /// ledger. Writes nothing; a source the ledger does not know delivered nothing.
    /// </summary>
    Task<bool> SourceDeliveredAsync(Guid flowId, ReversalSource source, CancellationToken ct = default);

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
    /// What records of <paramref name="scope"/>'s cache were built without, most records first: each value a node found no
    /// cached record by (a wellbore the cache does not hold), each key a <c>$findAll</c> found no row under (a field no
    /// access group lists) in any of the forms it was asked for, a reference with and without its version separator, shown
    /// once, and each id written without the record it names, with how many records were built so. With
    /// <paramref name="typeName"/>, the gaps of one cached type; with <paramref name="empty"/>, the paths read that held
    /// nothing as well (a wellbore without a field), which include the empty fields a <c>$findAll</c> asked for. Every gap
    /// is filled by the refresh that brings what is missing, which tags the records and redelivers them. Paged by
    /// <paramref name="skip"/> and <paramref name="take"/> in that order, with the count of every gap.
    /// </summary>
    Task<CacheGapPage> ListCacheGapsAsync(string scope, string? typeName, bool empty, int take, int skip, CancellationToken ct = default);

    /// <summary>
    /// Writes the tags a cache change produced, one per change rather than one per record, and gates the sets an
    /// unapproved change touches. Returns how many tags were new; a change already open for the same value updates
    /// the standing tag, and reopens it when the value moved again after an approval.
    /// </summary>
    Task<int> TagUpdatesAsync(IReadOnlyList<UpdateTag> tags, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>The gated sets, which a plan reads once per run to know which records are held back.</summary>
    Task<IReadOnlyList<long>> GatedCacheSetsAsync(CancellationToken ct = default);

    /// <summary>
    /// The tags in a status, newest first; with a cache name, only the changes that cache's refreshes found. <c>rolling</c>
    /// covers the changes being marked and the ones waiting for their flows. Each open change carries the records of each flow
    /// still built from the value that moved.
    /// </summary>
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
    /// the pace. Once every record is marked the change waits (<c>delivering</c>) until no flow still builds one of them from
    /// the old value, and only then is it rolled out (<c>applied</c>); a call on a waiting change checks that again.
    /// </summary>
    Task<UpdateRolloutBatch> RollOutTagAsync(long tagId, int batchSize, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>The approved tags with rollout still to do, oldest decision first.</summary>
    Task<IReadOnlyList<UpdateTag>> ListRolloutQueueAsync(int max, CancellationToken ct = default);

    /// <summary>
    /// Closes every change whose records are all marked and none of which any flow still builds from the old value: the
    /// last flow reading it has run. Returns how many it closed.
    /// </summary>
    Task<int> SettleRolloutsAsync(DateTime nowUtc, CancellationToken ct = default);

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

    /// <summary>Opens the row of an assertion run and returns it with its id.</summary>
    Task<AssertionRunState> StartAssertionRunAsync(AssertionRunState run, CancellationToken ct = default);

    /// <summary>Records one test's result in a run, as soon as the test has come out, and returns it with its id.</summary>
    Task<AssertionResultState> RecordAssertionResultAsync(AssertionResultState result, CancellationToken ct = default);

    /// <summary>Closes an assertion run with its status, its counts and, for a run that stopped, why.</summary>
    Task CompleteAssertionRunAsync(long assertionRunId, string status, AssertionCounts counts, string? failure, DateTime completedUtc, CancellationToken ct = default);

    /// <summary>The assertion run <paramref name="assertionRunId"/> names, naming its partition, or null.</summary>
    Task<AssertionRunState?> GetAssertionRunAsync(long assertionRunId, CancellationToken ct = default);

    /// <summary>A flow's assertion runs in its partition, newest first.</summary>
    Task<IReadOnlyList<AssertionRunState>> ListAssertionRunsAsync(Guid flowId, int max, CancellationToken ct = default);

    /// <summary>The results a run recorded, in the order it recorded them.</summary>
    Task<IReadOnlyList<AssertionResultState>> ListAssertionResultsAsync(long assertionRunId, CancellationToken ct = default);

    /// <summary>
    /// Each test's most recent result of each flow in <paramref name="flowIds"/>, the latest run that ran it; a test no run
    /// has run has none. The summary columns alone, without the detail: what a board across flows reads.
    /// </summary>
    Task<IReadOnlyList<AssertionResultState>> LatestAssertionResultsAsync(IReadOnlyCollection<Guid> flowIds, CancellationToken ct = default);

    /// <summary>
    /// The results of a flow's <paramref name="runs"/> most recent runs, newest run first, for one test or (null) every test:
    /// the history a matrix of tests against runs, or one test's trend, is drawn from. With <paramref name="withDetail"/> each
    /// result carries its detail; without, an empty one.
    /// </summary>
    Task<IReadOnlyList<AssertionResultState>> AssertionHistoryAsync(Guid flowId, string? testName, int runs, bool withDetail, CancellationToken ct = default);

    /// <summary>
    /// Removes the finished assertion runs that started before <paramref name="olderThanUtc"/> and whose every result a later
    /// result of the same test superseded, each run with all its results, so a report that is kept is whole. A run holding a
    /// test's latest result stays however old, so a board always shows where each test stands. Returns the runs removed.
    /// </summary>
    Task<int> PruneAssertionRunsAsync(DateTime olderThanUtc, CancellationToken ct = default);

    /// <summary>
    /// Registers a dimension a build is about to read (its declaration as the build reads it, in the ledger the build
    /// registered) and opens the build's row, returning both with their ids and the partition they are kept in.
    /// </summary>
    Task<(DimensionState Dimension, DimensionRunState Run)> StartDimensionRunAsync(
        DimensionDeclaration declaration, Guid? runId, string actor, DateTime startedUtc, CancellationToken ct = default);

    /// <summary>
    /// Writes a completed build's originals and members into its dimension and closes the build, in one transaction: what is
    /// new is added, what changed is changed, what the build no longer found is marked removed, and every change to an
    /// original is logged. Returns the closed build with what it changed. A failure writes nothing and leaves the build open
    /// for <see cref="CloseDimensionRunAsync"/>.
    /// </summary>
    Task<DimensionRunState> WriteDimensionAsync(DimensionWrite write, CancellationToken ct = default);

    /// <summary>Closes a build that wrote nothing, as failed or cancelled, with what it read before it stopped and why.</summary>
    Task CloseDimensionRunAsync(long dimensionRunId, string status, DimensionReadCounts read, string? failure, DateTime completedUtc, CancellationToken ct = default);

    /// <summary>
    /// The dimensions of one flow's ledger (<paramref name="flowId"/>), of one partition (by its data-partition-id), or of
    /// every partition when both are null; by flow and name.
    /// </summary>
    Task<IReadOnlyList<DimensionState>> ListDimensionsAsync(string? partition, Guid? flowId, CancellationToken ct = default);

    /// <summary>The dimension <paramref name="dimensionId"/> names, naming its partition, or null.</summary>
    Task<DimensionState?> GetDimensionAsync(int dimensionId, CancellationToken ct = default);

    /// <summary>The dimension named <paramref name="name"/> in the ledger <paramref name="flowId"/> names, or null.</summary>
    Task<DimensionState?> FindDimensionAsync(Guid flowId, string name, CancellationToken ct = default);

    /// <summary>A dimension's builds, newest first.</summary>
    Task<IReadOnlyList<DimensionRunState>> ListDimensionRunsAsync(int dimensionId, int max, CancellationToken ct = default);

    /// <summary>The newest build of each dimension named, whatever it came to; a dimension never built has none.</summary>
    Task<IReadOnlyList<DimensionRunState>> LatestDimensionRunsAsync(IReadOnlyCollection<int> dimensionIds, CancellationToken ct = default);

    /// <summary>The builds named by id; an id the ledger does not hold is left out.</summary>
    Task<IReadOnlyList<DimensionRunState>> GetDimensionRunsAsync(IReadOnlyCollection<long> dimensionRunIds, CancellationToken ct = default);

    /// <summary>The builds a platform run made, one per dimension it built.</summary>
    Task<IReadOnlyList<DimensionRunState>> DimensionRunsOfAsync(Guid runId, CancellationToken ct = default);

    /// <summary>A page of a dimension's members, in order of their clean values or with the most records first.</summary>
    Task<IReadOnlyList<DimensionMemberState>> ListDimensionMembersAsync(int dimensionId, DimensionMemberQuery query, CancellationToken ct = default);

    /// <summary>The members of a dimension named by id or by clean value, removed ones included, in order of their clean values.</summary>
    Task<IReadOnlyList<DimensionMemberState>> GetDimensionMembersAsync(
        int dimensionId, IReadOnlyCollection<long> memberIds, IReadOnlyCollection<string> values, CancellationToken ct = default);

    /// <summary>A page of a dimension's originals, in the order they arrived or with the most records first.</summary>
    Task<IReadOnlyList<DimensionValueState>> ListDimensionValuesAsync(int dimensionId, DimensionValueQuery query, CancellationToken ct = default);

    /// <summary>Every original the dimension holds now under the members <paramref name="memberIds"/> names, with each member's clean value.</summary>
    Task<IReadOnlyList<DimensionValueState>> MemberOriginalsAsync(int dimensionId, IReadOnlyCollection<long> memberIds, CancellationToken ct = default);

    /// <summary>
    /// The originals most records hold of each member <paramref name="memberIds"/> names, at most <paramref name="perMember"/>
    /// of each, with the member's clean value: what a page of members shows beside each.
    /// </summary>
    Task<IReadOnlyList<DimensionValueState>> TopMemberOriginalsAsync(int dimensionId, IReadOnlyCollection<long> memberIds, int perMember, CancellationToken ct = default);

    /// <summary>
    /// A page of a dimension's change log, newest first: of every build, or of one build, one original or one member, of one
    /// kind of change or of every kind, as <paramref name="query"/> narrows it.
    /// </summary>
    Task<IReadOnlyList<DimensionChangeState>> ListDimensionChangesAsync(int dimensionId, DimensionChangeQuery query, CancellationToken ct = default);

    /// <summary>
    /// Makes sure the dimension has its table (<see cref="DimensionTables"/>) with the rows the ledger holds of it, as a
    /// build's write does, without a build: for a dimension built before dimensions had a table, or one whose table was
    /// dropped. A table already there keeps the names its key's and its value's columns have: only a build, which reads
    /// the declaration, renames them. Answers whether the table had to be made or widened. False when the ledger holds
    /// no such dimension.
    /// </summary>
    /// <exception cref="DeliveryException">Another dimension writes a table of that name, or the table cannot be made.</exception>
    Task<bool> EnsureDimensionTableAsync(int dimensionId, DimensionTableSpec table, CancellationToken ct = default);

    /// <summary>
    /// The dimension's table as the database holds it: its name, the names of the columns that hold its key and its
    /// value, and its attribute columns, in order. Null when the ledger holds no such dimension.
    /// </summary>
    /// <exception cref="DimensionTableMissingException">The database holds no table of the dimension.</exception>
    Task<DimensionTableShape?> DimensionTableShapeAsync(int dimensionId, CancellationToken ct = default);

    /// <summary>
    /// A page of the dimension's table in the dimension's partition, as <paramref name="query"/> narrows and orders it. Null
    /// when the ledger holds no such dimension.
    /// </summary>
    /// <exception cref="DimensionTableMissingException">The database holds no table of the dimension.</exception>
    /// <exception cref="DimensionTableRenamedException">A build renamed a column of the table while it was read; reading it again finds it.</exception>
    /// <exception cref="DeliveryException">The query names a column the table does not have.</exception>
    Task<DimensionTablePage?> ReadDimensionTableAsync(int dimensionId, DimensionTableQuery query, CancellationToken ct = default);

    /// <summary>
    /// Every row of the dimension's table in the dimension's partition, by value then row number, one at a time, so a table
    /// of millions of rows is never held whole. Nothing when the ledger holds no such dimension.
    /// </summary>
    /// <exception cref="DimensionTableMissingException">The database holds no table of the dimension.</exception>
    /// <exception cref="DimensionTableRenamedException">A build renamed a column of the table as the read began; reading it again finds it.</exception>
    IAsyncEnumerable<DimensionTableRow> StreamDimensionTableAsync(int dimensionId, CancellationToken ct = default);

    /// <summary>
    /// The types of the cache flows of the dimension's partition that capture it (<c>dimension: &lt;name&gt;,
    /// dimensionFlow: &lt;flow&gt;</c>), as the last repository sync recorded them; none when no cache flow does, or the
    /// ledger holds no such dimension.
    /// </summary>
    Task<IReadOnlyList<DimensionCaptureState>> DimensionCapturesAsync(int dimensionId, CancellationToken ct = default);

    /// <summary>
    /// Removes the dimension from its partition for good: every row kept of it (its collected texts, attribute values, change
    /// log, keys, values and builds) and then the dimension itself, and nothing of any other dimension or partition. Rows go
    /// a batch at a time under the dimension's write lock, so a build writing it finishes first; the dimension's own row goes
    /// last, so a removal that stops part way leaves it listed and is finished by removing it again. Null when the ledger
    /// holds no such dimension. Whether the dimension may go (its flow no longer declares it, no cache flow captures it) is
    /// the caller's to settle (<see cref="Engine.Dimensions.DimensionRemoval"/>).
    /// </summary>
    Task<DimensionRemoved?> RemoveDimensionAsync(int dimensionId, CancellationToken ct = default);

    /// <summary>
    /// The texts a collected attribute's values stand for, as the dimension's last build that settled its field read them:
    /// those shown as one of <paramref name="values"/>, or every one when null; at most <paramref name="limit"/>, by value
    /// then text. The name compares exactly, as the dimension declares it.
    /// </summary>
    Task<IReadOnlyList<DimensionCollectedText>> ListDimensionCollectedTextsAsync(
        int dimensionId, string name, IReadOnlyCollection<string>? values, int limit, CancellationToken ct = default);

    /// <summary>
    /// The values an attribute holds among the dimension's originals a build finds now, as <paramref name="query"/> narrows
    /// them, the most records first: each with the originals holding it and their records (summed). The name compares
    /// exactly, as the dimension declares it.
    /// </summary>
    Task<IReadOnlyList<DimensionAttributeValueState>> ListDimensionAttributeValuesAsync(
        int dimensionId, DimensionAttributeValueQuery query, CancellationToken ct = default);

    /// <summary>
    /// How much of each attribute a build read, over the dimension's keys a build finds now: the keys holding a value read
    /// from a record or collected from their own (not the dimension's value for what is not read), and how many values those
    /// are. An attribute no key holds a read value of is left out.
    /// </summary>
    Task<IReadOnlyList<DimensionAttributeCoverage>> DimensionAttributeCoverageAsync(int dimensionId, CancellationToken ct = default);

    /// <summary>
    /// The values each attribute holds among the keys a build finds now of each of <paramref name="memberIds"/>: the most keys
    /// first, at most <paramref name="perAttribute"/> per attribute of a member.
    /// </summary>
    Task<IReadOnlyList<DimensionMemberAttributes>> MemberAttributesAsync(
        int dimensionId, IReadOnlyCollection<long> memberIds, int perAttribute, CancellationToken ct = default);

    Task<ActivityRecord> StartActivityAsync(ActivityRecord activity, CancellationToken ct = default);

    /// <summary>
    /// Closes an activity with its outcome, summary and captured log, the submission it turned out to work on, and whether
    /// it changed nothing (<see cref="ActivityRecord.Idle"/>), which only a completed activity can be.
    /// </summary>
    Task CompleteActivityAsync(long activityId, string outcome, string? summary, string? log, DateTime completedUtc, Guid? submissionId = null, bool idle = false, CancellationToken ct = default);

    Task<ActivityRecord?> GetActivityAsync(long activityId, CancellationToken ct = default);

    Task<IReadOnlyList<ActivityRecord>> ListActivitiesAsync(ActivityQuery query, CancellationToken ct = default);

    /// <summary>How many activities match a listing, for paging.</summary>
    Task<int> CountActivitiesAsync(ActivityQuery query, CancellationToken ct = default);
}
