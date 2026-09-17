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
}

/// <summary>
/// One row of the <c>osdu.RecordCount</c> indexed view: how many of a flow's records share a status, a last verify
/// outcome and the hour they were last delivered in. SQL Server maintains the view in the transaction of every record
/// write, so a flow's statistics read a few rows however many records the flow holds. Read-only, created by the initial
/// migration on SQL Server, and absent on SQLite.
/// </summary>
public sealed class DeliveryRecordCount
{
    public Guid FlowId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? LastVerifyOutcome { get; set; }

    /// <summary>The hour <see cref="DeliveryRecord.LastDeliveredUtc"/> falls in, truncated; null for a record never delivered.</summary>
    public DateTime? DeliveredHour { get; set; }

    public long Records { get; set; }
}

/// <summary>One work batch of a submission: a file of rendered documents, claimed and drained as one unit.</summary>
public sealed class DeliveryWorkBatch
{
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
/// What a worker appended while it delivered under a lease, not yet applied to the record: a step that completed, or the
/// outcome of a try. Rows are only ever added. The lease applies them to their records when the worker checkpoints or
/// closes it, or when the lease is recovered, and deletes them in the same transaction, so each is applied once. The
/// try's permanent history is its <see cref="DeliveryAttempt"/>, written with the outcome.
/// </summary>
public sealed class DeliveryRecordEvent
{
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
/// parsing a document. A document in the single form has one row, whose interface name is empty. The row of an interface
/// the repository no longer declares is kept, inactive, so the records it delivered still lead to their flow.
/// </summary>
public sealed class DeliveryInterface
{
    /// <summary>The longest interface name.</summary>
    public const int MaxInterfaceLength = 64;

    /// <summary>Stable id: derived from the repository, the flow's name and the interface's name.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The flow's name: its pipeline in the repository.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The interface's name; empty for a document in the single form.</summary>
    public string Interface { get; set; } = string.Empty;

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

    /// <summary>The partition whose cache the flow fills: its <c>source.headers.data-partition-id</c>.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The OSDU endpoint the flow searches, as declared (a reference, never a resolved value).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The cache flow's file, relative to the repository root.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>The short name mappings use (UnitOfMeasure).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The OSDU entity type (reference-data--UnitOfMeasure).</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The search kind the capture sweeps.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The search query narrowing the capture.</summary>
    public string? Query { get; set; }

    /// <summary>The captured paths as JSON: <c>[{ "path": "data.Code", "as": "Code" }]</c>.</summary>
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

    /// <summary>changed (the value moved), removed (the cached record is gone) or unmatched (what it matched by is gone).</summary>
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

/// <summary>The EF model of the delivery ledger, in the <c>osdu</c> schema.</summary>
public static class DeliveryModel
{
    public const string SchemaName = "osdu";

    /// <summary>The indexed view that counts a flow's records (<see cref="DeliveryRecordCount"/>).</summary>
    public const string RecordCountView = "RecordCount";

    /// <summary>
    /// The collation of the columns that key on an OSDU record id. OSDU ids are case-sensitive:
    /// <c>...UnitOfMeasure:ft</c> (the foot) and <c>...UnitOfMeasure:fT</c> (the femtotesla) are two records, and SQL
    /// Server's default collation folds case, which would make them one key. SQLite compares ordinally already.
    /// </summary>
    public const string OsduIdCollation = "Latin1_General_100_BIN2";

    /// <summary>
    /// The longest ingestion file name a record's origin holds. It keeps the (flow, file, row) index key under SQL Server's
    /// 1700-byte limit; a longer file name is refused when the source is opened, naming the file and this limit.
    /// </summary>
    public const int MaxSourceFileNameLength = 800;

    /// <summary>The longest lease token, and the longest worker name a lease records as its owner.</summary>
    public const int MaxLeaseTokenLength = 200;

    /// <param name="modelBuilder">The model being built.</param>
    /// <param name="sqlServer">Whether the model is for SQL Server, the provider whose default collation folds case.</param>
    public static void Configure(ModelBuilder modelBuilder, bool sqlServer)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<DeliverySubmission>(e =>
        {
            e.ToTable("Submission", SchemaName);
            e.HasKey(s => s.SubmissionId);
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
            e.HasIndex(s => new { s.FlowId, s.ReceivedUtc });
            e.HasIndex(s => new { s.FlowId, s.Status });
            // The flow's submission listing filtered by kind.
            e.HasIndex(s => new { s.FlowId, s.Kind, s.ReceivedUtc });
            // A run page links the run to the plan it coordinated.
            e.HasIndex(s => s.RunId);
        });

        modelBuilder.Entity<DeliveryRecord>(e =>
        {
            e.ToTable("Record", SchemaName);
            // A record is one flow's: two flows reading the same source row keep two records, and a flow's key-ordered
            // walks (a removal's key list, a key-scoped plan) read the key in order.
            e.HasKey(r => new { r.FlowId, r.DeliveryKey });
            e.Property(r => r.SourceKey).HasMaxLength(400).IsRequired();
            e.Property(r => r.SourceKeyJson).HasMaxLength(2000);
            e.Property(r => r.Label).HasMaxLength(400);
            e.Property(r => r.MappingName).HasMaxLength(200).IsRequired();
            e.Property(r => r.SourceFingerprint).HasMaxLength(200);
            e.Property(r => r.SourceFileName).HasMaxLength(MaxSourceFileNameLength);
            e.Property(r => r.MetadataHash).HasMaxLength(64);
            e.Property(r => r.PayloadHash).HasMaxLength(64);
            e.Property(r => r.TargetId).HasMaxLength(500);
            OptionalOsduId(e.Property(r => r.ClaimedTargetId), sqlServer).HasMaxLength(500);
            OptionalOsduId(e.Property(r => r.WaitingFor), sqlServer).HasMaxLength(500);
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

            // Worker and intake paths. The worker's reads (the claim, what is due next, the settled submissions with due
            // work) are answered from these two alone, for a flow and for one submission of it.
            e.HasIndex(r => new { r.FlowId, r.Status, r.NextAttemptUtc })
                .IncludeProperties(r => new { r.LastSubmissionId, r.UpdatedUtc, r.PendingDocumentRef });
            e.HasIndex(r => new { r.FlowId, r.LastSubmissionId, r.Status, r.NextAttemptUtc })
                .IncludeProperties(r => r.UpdatedUtc);
            // The rollout walks one set's records in key order, then flow order; the filtered index keeps untagged records out of it.
            e.HasIndex(r => new { r.CacheSetId, r.DeliveryKey, r.FlowId }).HasFilter("[CacheSetId] IS NOT NULL");
            // One OSDU record, one flow: the database refuses a second flow's claim on an id, whatever races the intakes run.
            e.HasIndex(r => r.ClaimedTargetId).IsUnique().HasFilter("[ClaimedTargetId] IS NOT NULL");
            e.HasIndex(r => new { r.LastSubmissionId, r.WorkBatch });
            // The records a lease holds: the ones a checkpoint or a close hands back, and the ones a submission waits on.
            e.HasIndex(r => r.LeaseOwner);
            // A submission's records, most recent first, read in index order however many the submission holds.
            e.HasIndex(r => new { r.FlowId, r.LastSubmissionId, r.UpdatedUtc });
            e.HasIndex(r => new { r.FlowId, r.LastVerifiedUtc });

            // GUI: prefix search and recency listings, all answered from an index.
            e.HasIndex(r => new { r.FlowId, r.Label });
            e.HasIndex(r => new { r.FlowId, r.SourceKey });
            e.HasIndex(r => new { r.FlowId, r.TargetId });
            e.HasIndex(r => new { r.FlowId, r.UpdatedUtc });
            e.HasIndex(r => new { r.FlowId, r.Status, r.UpdatedUtc });
            e.HasIndex(r => new { r.FlowId, r.LastDeliveredUtc });
            e.HasIndex(r => new { r.FlowId, r.LastVerifyOutcome });

            // The global lookup (the search box): a delivery key, an OSDU id, a source key, a label prefix or an
            // ingestion file name answers from these across every flow.
            e.HasIndex(r => r.DeliveryKey);
            e.HasIndex(r => r.TargetId);
            e.HasIndex(r => r.SourceKey);
            e.HasIndex(r => r.Label);
            e.HasIndex(r => r.SourceFileName);

            // Which records came from this file, inside a flow, in milliseconds.
            e.HasIndex(r => new { r.FlowId, r.SourceFileName, r.SourceRowNumber });

            // The records the ledger asked to be planned again, paged by the planner each run: the filter keeps the
            // index as small as the backlog.
            e.HasIndex(r => new { r.FlowId, r.PlanRequestedUtc }).HasFilter("[PlanRequestedUtc] IS NOT NULL");

            // The records waiting for an id, released when the record holding that id lands: the filter keeps the index
            // as small as what is waiting, so the release every settle runs costs a seek.
            e.HasIndex(r => r.WaitingFor).HasFilter("[WaitingFor] IS NOT NULL");
        });

        modelBuilder.Entity<DeliveryAttempt>(e =>
        {
            e.ToTable("Attempt", SchemaName);
            e.HasKey(a => a.AttemptId);
            e.Property(a => a.AttemptId).ValueGeneratedOnAdd();
            e.Property(a => a.Worker).HasMaxLength(200).IsRequired();
            e.Property(a => a.Outcome).HasMaxLength(16).IsRequired();
            e.Property(a => a.Phase).HasMaxLength(32).IsRequired();
            e.Property(a => a.MetadataHash).HasMaxLength(64);
            e.Property(a => a.PayloadHash).HasMaxLength(64);
            e.Property(a => a.Error).HasMaxLength(2000);
            e.Property(a => a.SourceFileName).HasMaxLength(MaxSourceFileNameLength);
            // A record's timeline, and the later attempt pruning looks for beside each one it removes.
            e.HasIndex(a => new { a.FlowId, a.DeliveryKey, a.StartedUtc });
            e.HasIndex(a => a.StartedUtc);
            // A submission's attempts, and the records they settled by outcome, counted when the submission closes: the
            // count reads this index alone, however many attempts a full plan wrote.
            e.HasIndex(a => new { a.SubmissionId, a.Outcome, a.Phase }).IncludeProperties(a => a.DeliveryKey);
            // A run's records: the listing's run filter seeks the run and joins on the record without reading the attempt.
            e.HasIndex(a => new { a.RunId, a.FlowId, a.DeliveryKey });
        });

        modelBuilder.Entity<DeliveryRecordCount>(e =>
        {
            // Created by the initial migration on SQL Server: EF cannot declare an indexed view.
            e.HasNoKey();
            e.ToView(RecordCountView, SchemaName);
            e.Property(c => c.Status).HasMaxLength(16);
            e.Property(c => c.LastVerifyOutcome).HasMaxLength(16);
        });

        modelBuilder.Entity<DeliveryWorkBatch>(e =>
        {
            e.ToTable("WorkBatch", SchemaName);
            e.HasKey(b => new { b.SubmissionId, b.Index });
            e.Property(b => b.Location).HasMaxLength(2000).IsRequired();
            e.Property(b => b.Status).HasMaxLength(16).IsRequired();
            e.Property(b => b.LeaseOwner).HasMaxLength(200);
            e.Property(b => b.Error).HasMaxLength(2000);
            // The claim: the oldest queued batch of a flow (or a submission).
            e.HasIndex(b => new { b.FlowId, b.Status, b.CreatedUtc });
            e.HasIndex(b => new { b.SubmissionId, b.Status });
        });

        modelBuilder.Entity<DeliveryLease>(e =>
        {
            e.ToTable("Lease", SchemaName);
            e.HasKey(l => l.Token);
            e.Property(l => l.Token).HasMaxLength(MaxLeaseTokenLength);
            e.Property(l => l.Owner).HasMaxLength(MaxLeaseTokenLength).IsRequired();
            // The sweep: a flow's leases that ran out. A submission's leases: what its run waits on.
            e.HasIndex(l => new { l.FlowId, l.ExpiresUtc });
            e.HasIndex(l => new { l.SubmissionId, l.ExpiresUtc });
        });

        modelBuilder.Entity<DeliveryRecordEvent>(e =>
        {
            e.ToTable("RecordEvent", SchemaName);
            // Clustered on an ever-increasing id: every worker only appends, at the end of the table.
            e.HasKey(v => v.EventId);
            e.Property(v => v.EventId).ValueGeneratedOnAdd();
            e.Property(v => v.LeaseToken).HasMaxLength(MaxLeaseTokenLength).IsRequired();
            e.Property(v => v.Kind).HasMaxLength(16).IsRequired();
            e.Property(v => v.Status).HasMaxLength(16);
            e.Property(v => v.Error).HasMaxLength(2000);
            e.Property(v => v.TargetId).HasMaxLength(500);
            e.Property(v => v.ClaimDocumentRef).HasMaxLength(64);
            e.Property(v => v.ClaimSourceFingerprint).HasMaxLength(200);
            e.Property(v => v.ClaimSourceFileName).HasMaxLength(MaxSourceFileNameLength);
            e.Property(v => v.ClaimMetadataHash).HasMaxLength(64);
            e.Property(v => v.ClaimPayloadHash).HasMaxLength(64);
            // What a lease applies: its events, record by record, the latest last.
            e.HasIndex(v => new { v.LeaseToken, v.FlowId, v.DeliveryKey, v.EventId });
            // A flow's recovery: its events old enough that only a lease that is gone can have left them.
            e.HasIndex(v => new { v.FlowId, v.AtUtc }).IncludeProperties(v => v.LeaseToken);
        });

        modelBuilder.Entity<DeliverySourceWatermark>(e =>
        {
            e.ToTable("SourceWatermark", SchemaName);
            e.HasKey(w => new { w.FlowId, w.Scope });
            e.Property(w => w.Scope).HasMaxLength(400);
            e.Property(w => w.ContextHash).HasMaxLength(64);
        });

        modelBuilder.Entity<DeliveryActivity>(e =>
        {
            e.ToTable("Activity", SchemaName);
            e.HasKey(a => a.ActivityId);
            e.Property(a => a.ActivityId).ValueGeneratedOnAdd();
            e.Property(a => a.FlowName).HasMaxLength(200).IsRequired();
            e.Property(a => a.Kind).HasMaxLength(32).IsRequired();
            e.Property(a => a.Actor).HasMaxLength(200).IsRequired();
            e.Property(a => a.Outcome).HasMaxLength(16).IsRequired();
            e.Property(a => a.Summary).HasMaxLength(2000);
            e.HasIndex(a => new { a.FlowId, a.StartedUtc });
            e.HasIndex(a => new { a.FlowId, a.DeliveryKey, a.StartedUtc });
            e.HasIndex(a => a.SubmissionId);
            e.HasIndex(a => a.RunId);
            e.HasIndex(a => new { a.Kind, a.StartedUtc });
            e.HasIndex(a => new { a.Actor, a.StartedUtc });
            e.HasIndex(a => a.StartedUtc);
        });

        modelBuilder.Entity<DeliveryRetrieval>(e =>
        {
            e.ToTable("Retrieval", SchemaName);
            e.HasKey(r => r.RetrievalId);
            e.Property(r => r.RetrievalId).ValueGeneratedOnAdd();
            e.Property(r => r.FlowName).HasMaxLength(200).IsRequired();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Kinds).HasMaxLength(4000).IsRequired();
            e.Property(r => r.WindowField).HasMaxLength(200);
            e.Property(r => r.Location).HasMaxLength(2000).IsRequired();
            e.Property(r => r.ManifestLocation).HasMaxLength(2000);
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            // The flow's listing, the watermark chain (the last done run), and the run's row.
            e.HasIndex(r => new { r.FlowId, r.StartedUtc });
            e.HasIndex(r => new { r.FlowId, r.Status, r.StartedUtc });
            e.HasIndex(r => r.RunId);
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
            e.HasIndex(i => new { i.RepoId, i.FlowName, i.Interface }).IsUnique();
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
            OsduId(e.Property(i => i.RecordId), sqlServer).HasMaxLength(512).IsRequired();
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
            OsduId(e.Property(m => m.RecordId), sqlServer).HasMaxLength(512).IsRequired();
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
            OsduId(e.Property(c => c.ItemId), sqlServer).HasMaxLength(512).IsRequired();
            e.Property(c => c.Path).HasMaxLength(400).IsRequired();
            e.Property(c => c.Kind).HasMaxLength(16).IsRequired();
            e.Property(c => c.ValueHash).HasMaxLength(64).IsRequired();
            e.Property(c => c.ValueText).HasMaxLength(400).IsRequired();
            e.HasKey(c => new { c.SetId, c.Scope, c.TypeName, c.ItemId, c.Path, c.Kind });
            // The impact query: which sets hold this cached value of this partition's cache.
            e.HasIndex(c => new { c.Scope, c.TypeName, c.ItemId });
        });

        modelBuilder.Entity<DeliveryUpdateTag>(e =>
        {
            e.ToTable("UpdateTag", SchemaName);
            e.HasKey(t => t.TagId);
            e.Property(t => t.Kind).HasMaxLength(16).IsRequired();
            e.Property(t => t.Scope).HasMaxLength(200).IsRequired();
            e.Property(t => t.TypeName).HasMaxLength(200).IsRequired();
            OsduId(e.Property(t => t.ItemId), sqlServer).HasMaxLength(512).IsRequired();
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
            e.HasIndex(t => t.Status);
            e.HasIndex(t => new { t.Scope, t.TypeName, t.ItemId, t.Path, t.Status });
        });

        modelBuilder.Entity<DeliveryCacheDefinition>(e =>
        {
            e.ToTable("CacheDefinition", SchemaName);
            e.HasKey(c => c.Id);
            e.Property(c => c.FlowName).HasMaxLength(200).IsRequired();
            e.Property(c => c.Scope).HasMaxLength(200).IsRequired();
            e.Property(c => c.Endpoint).HasMaxLength(1000).IsRequired();
            e.Property(c => c.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
            e.Property(c => c.EntityType).HasMaxLength(200).IsRequired();
            e.Property(c => c.Kind).HasMaxLength(400).IsRequired();
            e.Property(c => c.Query).HasMaxLength(4000);
            e.Property(c => c.FieldsJson).IsRequired();
            e.Property(c => c.OnChange).HasMaxLength(16).IsRequired();
            e.HasIndex(c => new { c.RepoId, c.FlowName, c.Name }).IsUnique();
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
    /// A column keyed on an OSDU record id: compared exactly by the database (<see cref="OsduIdCollation"/> on SQL Server,
    /// ordinally on SQLite) and exactly by the change tracker. Without the ordinal comparer, a context tracking the foot and
    /// the femtotesla side by side (two memberships of one cache flow, say) takes them for one key and refuses the second.
    /// </summary>
    private static PropertyBuilder<string> OsduId(PropertyBuilder<string> property, bool sqlServer)
    {
        property.Metadata.SetValueComparer(new ValueComparer<string>(
            (left, right) => string.Equals(left, right, StringComparison.Ordinal),
            value => StringComparer.Ordinal.GetHashCode(value),
            value => value));
        return sqlServer ? property.UseCollation(OsduIdCollation) : property;
    }

    /// <summary>A nullable column keyed on an OSDU record id, compared exactly as <see cref="OsduId"/> compares a required one.</summary>
    private static PropertyBuilder<string?> OptionalOsduId(PropertyBuilder<string?> property, bool sqlServer)
    {
        property.Metadata.SetValueComparer(new ValueComparer<string?>(
            (left, right) => string.Equals(left, right, StringComparison.Ordinal),
            value => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value),
            value => value));
        return sqlServer ? property.UseCollation(OsduIdCollation) : property;
    }
}
