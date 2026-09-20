// The delivery ledger's API: what each flow delivered (records, their history, their submissions), the audit trail,
// the mappings, templates and caches the catalog holds, the mapping builder, and the interventions (release,
// redeliver, verify, read back, delete). Same conventions as endpoints.ts: one function per endpoint, pages compose them
// with TanStack Query.

import { del, get, getText, post, type QueryParams } from "@/api/client";
import type { ComputeTaskAccepted, PagedResult, RunStatus } from "@/api/types";
import type { PageQuery } from "@/api/endpoints";

/**
 * A record's custody state. `waiting` holds a rendered document that refers to a record another record of the ledger
 * holds and has not delivered; the record goes back to pending when that one lands.
 */
export type DeliveryRecordStatus = "pending" | "delivering" | "delivered" | "held" | "failed" | "deleted" | "waiting";

export const DELIVERY_RECORD_STATUSES: readonly DeliveryRecordStatus[] = [
  "pending", "waiting", "delivering", "delivered", "held", "failed", "deleted",
];

export type DeliverySubmissionStatus = "received" | "planned" | "running" | "completed" | "failed";

/**
 * What a submission read from the ingestion tables: the rows changed in a window after the scope's watermark
 * (`incremental`), every row of the scope (`full`), or the keys a record-scoped or replanned run named (`keys`).
 */
export type DeliverySubmissionKind = "incremental" | "full" | "keys";

export type DeliveryVerifyOutcome = "match" | "drifted" | "missing" | "error";

/** A delivery flow's record counts by state, drift, throughput, and its last submission. */
export interface DeliveryFlowStats {
  pipelineId: string;
  flowName: string;
  flowId: string;
  total: number;
  pending: number;
  delivering: number;
  delivered: number;
  held: number;
  failed: number;
  deleted: number;
  /** Records waiting for a record they refer to that has not landed. */
  waiting: number;
  drifted: number;
  deliveredLast24h: number;
  lastDeliveredUtc: string | null;
  lastVerifiedUtc: string | null;
  submissions: number;
  lastSubmission: DeliverySubmission | null;
}

/** One submission as the ledger received it and what became of it. */
export interface DeliverySubmission {
  submissionId: string;
  flowId: string;
  flowName: string;
  mappingReference: string;
  renderContext: string;
  parametersJson: string;
  recordCount: number;
  status: DeliverySubmissionStatus;
  receivedUtc: string;
  startedUtc: string | null;
  completedUtc: string | null;
  planned: number;
  skippedUnchanged: number;
  /** Records a cache change waiting for approval holds back: rendered and ready, not sent until it is decided. */
  awaitingApproval: number;
  /** Records the submission carried in a version older than the one delivered or queued: skipped, never sent. */
  skippedStale: number;
  /** Records whose queued document OSDU already held when the worker came to send it: nothing was sent. */
  unchangedAtPush: number;
  blocked: number;
  delivered: number;
  held: number;
  failed: number;
  /** Records of the submission still waiting, when it closed, for a record they refer to; they go out once it lands. */
  waiting: number;
  error: string | null;
  /** Where the intake wrote the work batches (the rendered documents the drains read). */
  workLocation: string | null;
  batchCount: number;
  /** How many key slices the intake cut the submission into for its fan-out members; 0 when it fanned out none. */
  slices: number;
  /** Which selection the plan read from the ingestion tables. */
  kind: DeliverySubmissionKind;
  /** Rows the plan read that carried no complete record key, so nothing could be delivered under them. */
  untracked: number;
  /** The connection reference of the ingestion database as the flow declares it; never a resolved secret. */
  sourceConnection: string;
  /** The three-part name of the ingestion table the records were read from. */
  sourceObject: string;
  /** The change window the plan covered, on the ingestion tables' updated column; null for a plan with no window. */
  windowFromUtc: string | null;
  windowToUtc: string | null;
  /** What else bounded the read: the child dataset objects, the overlap, the scope values, the slice bounds, the key count. */
  sourceWindow: Record<string, unknown> | null;
  /** The run that coordinated the submission. */
  runId: string | null;
}

/** One work batch of a submission: a file of rendered documents and how far its drain got. */
export interface DeliveryWorkBatch {
  submissionId: string;
  index: number;
  location: string;
  recordCount: number;
  status: "queued" | "running" | "done" | "failed";
  leaseOwner: string | null;
  leaseExpiresUtc: string | null;
  runId: string | null;
  createdUtc: string;
  startedUtc: string | null;
  completedUtc: string | null;
  delivered: number;
  held: number;
  failed: number;
  retrying: number;
  /** Records the batch's claim found waiting for a record they refer to, and did not send. */
  waiting: number;
  error: string | null;
}

export interface DeliverySubmissionDetail {
  submission: DeliverySubmission;
  pipelineId: string | null;
  /** The runs that carried or re-ran this submission, newest first. */
  runIds: string[];
}

/** The current state of one deliverable: what OSDU holds for it, what is pending, and why it is where it is. */
export interface DeliveryRecord {
  deliveryKey: string;
  flowId: string;
  sourceKey: string;
  label: string | null;
  mappingName: string;
  renderContext: string | null;
  sourceFingerprint: string | null;
  /** When the source row the delivered document was built from last changed (the flow's source.lastModified). */
  sourceModifiedUtc: string | null;
  metadataHash: string | null;
  payloadHash: string | null;
  /** The newest modified time among the chunk files the delivered payload was sent from. */
  payloadModifiedUtc: string | null;
  targetId: string | null;
  targetVersion: number | null;
  status: DeliveryRecordStatus;
  lastDeliveredUtc: string | null;
  lastVerifiedUtc: string | null;
  lastVerifyOutcome: DeliveryVerifyOutcome | null;
  leaseOwner: string | null;
  leaseExpiresUtc: string | null;
  lastSubmissionId: string | null;
  attemptCount: number;
  nextAttemptUtc: string | null;
  lastError: string | null;
  hasPendingDocument: boolean;
  pendingMetadata: boolean;
  pendingPayload: boolean;
  pendingPayloadLocation: string | null;
  blocked: boolean;
  createdUtc: string;
  updatedUtc: string;
  /** Where the pending document sits in the submission's work batches (batch:offset:length), when one is waiting. */
  pendingDocumentRef: string | null;
  workBatch: number | null;
  /** The identifiers the target returned for what it holds now (record id and version, dataset ids, a workflow run). */
  targetState: Record<string, unknown> | null;
  /** The steps of the pending delivery an earlier try completed, with what they returned. */
  pendingSteps: Record<string, unknown> | null;
  /** The ingestion file the delivered document was built from, and the row of it. */
  sourceFileName: string | null;
  sourceRowNumber: number | null;
  /** When the ingestion table last updated that row. */
  sourceUpdatedUtc: string | null;
  /** The same three for work that is waiting: where the pending document will be built from. */
  pendingSourceFileName: string | null;
  pendingSourceRowNumber: number | null;
  pendingSourceUpdatedUtc: string | null;
  /** The key columns that find the record's row in the ingestion tables, as JSON. */
  sourceKeyJson: string | null;
  /** When a plan last asked for this record, for a record waiting on one. */
  planRequestedUtc: string | null;
  /** While the record is waiting: the OSDU id of the record it waits for. */
  waitingFor: string | null;
  /** The OSDU ids the pending document refers to, each with the property that holds it. */
  references: DeliveryRecordReference[] | null;
}

/** An OSDU id a record's pending document refers to, and the property of the record holding it. */
export interface DeliveryRecordReference {
  id: string;
  property: string;
}

/** A record of the ledger another record waits for, or that waits for it: where it is and how it stands. */
export interface DeliveryRecordLink {
  flowId: string;
  deliveryKey: string;
  pipelineId: string | null;
  flowName: string | null;
  interface: string | null;
  sourceKey: string;
  label: string | null;
  targetId: string | null;
  status: DeliveryRecordStatus;
}

export interface DeliveryRecordDetail {
  record: DeliveryRecord;
  pipelineId: string | null;
  repoId: string | null;
  flowName: string | null;
  interface?: string | null;
  /** The record this one waits for, while it waits. */
  waitsOn?: DeliveryRecordLink | null;
  /** The records waiting for this one (the first 50). */
  waitedOnBy?: DeliveryRecordLink[] | null;
}

/**
 * A delivery record the combined search found: the `data` of each hit in the `records` category the module's search
 * contributor adds to GET /api/v1/search/all. It matches an exact delivery key, or a prefix of the record's OSDU id,
 * source key or label, across every flow.
 */
export interface DeliveryRecordHit {
  deliveryKey: string;
  flowId: string;
  flowName: string | null;
  pipelineId: string | null;
  sourceKey: string;
  label: string | null;
  targetId: string | null;
  status: string;
  lastDeliveredUtc: string | null;
  updatedUtc: string;
  /** The interface of a source the record belongs to; null for a flow in the single form. */
  interface?: string | null;
}

/** One step of a delivery try: what it did, how long it took, what the target answered. */
export interface DeliveryAttemptStep {
  name: string;
  startedUtc?: string;
  ms?: number;
  status?: number;
  resumed?: boolean;
  error?: string;
  returned?: Record<string, string>;
}

export interface DeliveryAttemptResult {
  /** The correlation id every OSDU request of the try carried, for finding it in the services' own logs. */
  correlationId?: string;
  /** Absent on attempts recorded before every attempt carried a steps array. */
  steps?: DeliveryAttemptStep[];
  returned?: Record<string, string>;
  /** What an attempt that did not fail has to say (chunks sent, why nothing was sent, what a removal took). */
  detail?: string;
}

/** One delivery try, as the append-only history holds it. */
export interface DeliveryAttempt {
  attemptId: number;
  deliveryKey: string;
  submissionId: string | null;
  runId: string | null;
  worker: string;
  startedUtc: string;
  completedUtc: string;
  outcome: "delivered" | "skipped" | "failed" | "held" | "deleted" | "historypurged";
  phase: string;
  metadataHash: string | null;
  payloadHash: string | null;
  targetVersion: number | null;
  error: string | null;
  /** The steps the try took and what the target returned, null for tries that recorded none. */
  result: DeliveryAttemptResult | null;
  workBatch: number | null;
  /** The ingestion file and row the document this try sent was built from, and when that row last changed. */
  sourceFileName: string | null;
  sourceRowNumber: number | null;
  sourceUpdatedUtc: string | null;
}

/** One entry of the audit trail: who did what, when, with which inputs, and how it ended. */
export interface DeliveryActivity {
  activityId: number;
  flowId: string;
  flowName: string;
  kind: string;
  actor: string;
  startedUtc: string;
  completedUtc: string | null;
  outcome: "running" | "completed" | "failed" | "cancelled";
  parametersJson: string | null;
  submissionId: string | null;
  deliveryKey: string | null;
  runId: string | null;
  summary: string | null;
  /** The captured log; only the detail endpoint fills it. */
  log: string | null;
}

/** A mapping document as the sync found it in a repository. */
export interface DeliveryMapping {
  id: string;
  repoId: string;
  reference: string;
  name: string;
  version: string;
  kind: string;
  relativePath: string;
  contentHash: string;
  status: "valid" | "invalid";
  message: string | null;
  summary: Record<string, unknown>;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

export interface DeliveryMappingDetail {
  mapping: DeliveryMapping;
  yaml: string;
}

/** One path a partition's cache keeps for a type, the name it is cached under, and the cache flows that declare it. */
export interface DeliveryCacheField {
  path: string;
  as: string;
  flows: string[];
}

/** One cache flow's declaration of a type: the kind it searches, its query, and what a change does in its declaration. */
export interface DeliveryCacheTypeSource {
  flow: string;
  kind: string;
  query: string | null;
  /** approve or auto, as this flow declares it. */
  onChange: string;
}

/**
 * One type of a partition's cache, as its cache flows together declare it: each flow's kind and query, every path any of
 * them keeps, what a change does, and what the current version holds of it.
 */
export interface DeliveryCacheType {
  name: string;
  entityType: string;
  sources: DeliveryCacheTypeSource[];
  fields: DeliveryCacheField[];
  /** approve (a change waits for a decision, when any declaring flow asks for that) or auto (a change goes out on the next run). */
  onChange: string;
  /** How many records the current version holds of the type. */
  items: number;
}

/** A schedule that refreshes a cache flow: its cadence, or that it fires behind other schedules. */
export interface DeliveryCacheSchedule {
  id: string;
  name: string;
  cron: string | null;
  intervalSeconds: number | null;
  chained: boolean;
}

/**
 * A cache flow filling a partition's cache: the repository and file that define it (the file is where what it caches is
 * changed), its pipeline, the OSDU endpoint reference it searches, the schedules that refresh it, and the types it declares.
 */
export interface DeliveryCacheFlow {
  name: string;
  repoId: string;
  repoName: string;
  relativePath: string;
  pipelineId: string | null;
  endpoint: string;
  schedules: DeliveryCacheSchedule[];
  types: string[];
}

/**
 * The cache of one OSDU partition: every cache flow that fills it, the types it holds as those flows together declare them,
 * its current version with the flow and run that wrote it, and how many versions it has. Every delivery flow delivering to
 * the partition reads it.
 */
export interface DeliveryCache {
  /** The partition: the data-partition-id every flow that fills or reads the cache carries. */
  scope: string;
  flows: DeliveryCacheFlow[];
  types: DeliveryCacheType[];
  current: DeliveryCacheVersion | null;
  versions: number;
}

/** One cached record: its OSDU id and the values captured at the declared paths, in whatever shape they came. */
export interface DeliveryCachedItem {
  itemId: number;
  /** The partition whose cache holds the record. */
  scope: string;
  /** The version of the cache this row is the record as of. */
  version: string;
  typeName: string;
  entityType: string;
  recordId: string;
  fields: Record<string, unknown>;
}

/** One type a cache version holds, and how many records of it. */
export interface DeliveryCacheVersionType {
  name: string;
  entityType: string;
  items: number;
}

/**
 * One version of a partition's cache: when it was written, whether it is the version deliveries render against, the version
 * that was current before it, the cache flow and the run that wrote it and who asked (no run for an import from files),
 * where the content came from, and what it holds.
 */
export interface DeliveryCacheVersion {
  scope: string;
  version: string;
  sequence: number;
  capturedUtc: string;
  current: boolean;
  previousVersion: string | null;
  /** The cache flow whose capture or import wrote the version. */
  flow: string;
  runId: string | null;
  capturedBy: string;
  origin: string;
  items: number;
  types: DeliveryCacheVersionType[];
}

/** How one cached record differs between two versions of its cache. */
export type DeliveryCacheChange = "changed" | "added" | "removed";

/** A comparison of two versions of one cache: `from` is required, `to` defaults to the current version, `change` narrows the items only. */
export type DeliveryCacheDiffQuery = {
  scope: string;
  from: string;
  to?: string;
  type?: string;
  change?: DeliveryCacheChange;
  search?: string;
  page?: number;
  pageSize?: number;
};

/** One cached record that differs between two versions, with what it held on each side. */
export interface DeliveryCacheDiffItem {
  typeName: string;
  entityType: string;
  recordId: string;
  change: DeliveryCacheChange;
  /** The captured values at the earlier version; null for a record the later version added. */
  before: Record<string, unknown> | null;
  /** The captured values at the later version; null for a record the later version no longer holds. */
  after: Record<string, unknown> | null;
  /** The captured names whose value differs; empty unless the record changed. */
  changedFields: string[];
}

/** How many records of one cached type changed, arrived and left between the two versions. */
export interface DeliveryCacheDiffType {
  typeName: string;
  changed: number;
  added: number;
  removed: number;
}

/**
 * What changed in a cache between two versions. The counts follow the type and search filters but not the change filter, so
 * they describe every kind of change while the items show the one picked.
 */
export interface DeliveryCacheDiff {
  scope: string;
  fromVersion: string;
  toVersion: string;
  changed: number;
  added: number;
  removed: number;
  types: DeliveryCacheDiffType[];
  items: PagedResult<DeliveryCacheDiffItem>;
}

/** One version in a cache's history: the version captured before it, and how many records it changed, added and removed against that one. */
export interface DeliveryCacheHistoryEntry {
  version: DeliveryCacheVersion;
  /** The version captured before it; null for the first version, whose records all arrived with it. */
  before: string | null;
  changed: number;
  added: number;
  removed: number;
}

/** One cache change and what happens about it: it covers every delivered record built from the value that moved. */
export interface DeliveryUpdateTag {
  tagId: number;
  kind: string;
  /** The partition whose cache the change was found in. */
  scope: string;
  typeName: string;
  itemId: string;
  path: string;
  /** changed, removed or unmatched. */
  change: string;
  oldValue: string | null;
  newValue: string | null;
  fromVersion: string | null;
  toVersion: string;
  /** auto or approve. */
  mode: string;
  /** pending, approved, rolling, rejected or applied. */
  status: string;
  summary: string;
  /** Delivered records built from the old value. */
  affectedRecords: number;
  /** How many of them the rollout has marked for redelivery. */
  processed: number;
  remaining: number;
  detectedUtc: string;
  decidedUtc: string | null;
  decidedBy: string | null;
  startedUtc: string | null;
  completedUtc: string | null;
}

/** One cached value a record was built from. */
export interface DeliveryCacheUse {
  /** The partition whose cache the value was read from. */
  scope: string;
  typeName: string;
  itemId: string;
  path: string;
  /** match (what it resolved by) or value (what went into the document). */
  kind: string;
  value: string;
}

export interface DeliveryRunAccepted {
  runId: string;
  status: RunStatus;
}

export interface DeliveryReleaseResult {
  released: number;
}

export interface DeliveryRedeliverResult {
  marked: number;
  runId: string | null;
}

export interface DeliveryPruneResult {
  /** Delivery tries deleted; the latest try of every record is always kept. */
  attemptsPruned: number;
  /** Activities whose captured run log was cleared. The audit row itself is never deleted. */
  activityLogsCleared: number;
}

export interface DeliveryRecordListQuery extends PageQuery {
  /** Which interface of the source the records are of. Required when the source delivers more than one. */
  interface?: string;
  /** A delivery key (exact), or a prefix over label, source key and target id. */
  search?: string;
  /** "contains" for the slower substring match; prefix by default. */
  mode?: "prefix" | "contains";
  status?: DeliveryRecordStatus;
  /** Only records this submission last planned, delivered or found unchanged; a later submission moves them on. */
  submissionId?: string;
  /** Only records this submission delivered, which stay its own however many submissions touch them afterwards. */
  deliveredBy?: string;
  /** Only records the given platform run touched, resolved through that run's attempts. */
  runId?: string;
  /** Only delivered records whose last verify found drift or a missing record. */
  drifted?: boolean;
}

/** What the Records page asks the ledger for: a term over every flow, and a custody state to narrow it to. */
export interface DeliveryRecordLookupQuery extends PageQuery {
  /** A delivery key, or the start of an OSDU id, a source key, a label or an ingestion file name. */
  search: string;
  status?: DeliveryRecordStatus;
}

/**
 * How much of a record a removal takes away in OSDU. The three are different endpoints with different promises,
 * not degrees of one thing: only "record" can be undone, and only "history" leaves the record live.
 */
export type RemovalScope = "record" | "history" | "everything";

/** Where a flow's records live, and the exact call each removal scope would make against them. */
export interface DeliveryTarget {
  pipelineId: string;
  flowName: string;
  /** The endpoint as the flow declares it, secret references included: that reference names the environment. */
  endpoint: string;
  dataPartition: string | null;
  protocol: string;
  authType: string;
  recordPath: string;
  /** The method the record scope calls recordPath with: POST for storage and the dataset service, DELETE for a DDMS's own removal. */
  recordMethod: string;
  historyPath: string;
  everythingPath: string;
  /** The interface of the source the target is for, or null for a flow in the single form. */
  interface: string | null;
  /** On the ddms route: which collection of which DDMS the records go to, as a sentence; null on the other routes. */
  ddms: string | null;
}

/** One interface another waits for, or does not wait for, and where that comes from. */
export interface DeliveryInterfaceWait {
  interface: string;
  /** "after" when the document declares it, "schema" when a mapping's reference implies it. */
  origin: string;
  why: string;
}

/**
 * One interface of a source (docs/interfaces-design.md): its ledger, how it is delivered and why, what it renders,
 * the order it runs in, and its record counts. A flow in the single form lists one, with no name.
 */
export interface DeliveryInterface {
  /** The interface's name, or null for a flow in the single form. */
  interface: string | null;
  /** The ledger identity its records are keyed by. */
  flowId: string;
  ledger: string;
  route: string;
  routeReason: string | null;
  mapping: string;
  kind: string | null;
  recordObject: string;
  after: string[];
  stats: DeliveryFlowStats;
  /** The wave it runs in: every interface of a wave runs together, after the waves before it. */
  wave: number;
  waitsFor: DeliveryInterfaceWait[] | null;
  notWaitedFor: DeliveryInterfaceWait[] | null;
  /** Why the order shown is only what `after:` gives (a mapping the catalog does not hold, interfaces that wait for each other). */
  orderProblem: string | null;
}

/** The listing a removal is aimed at: the same filter the records list is built from. */
export interface DeliveryRecordFilter {
  status?: DeliveryRecordStatus;
  search?: string;
  mode?: "prefix" | "contains";
  submissionId?: string;
  /** The records a submission delivered: what "remove the batch we ran" acts on. */
  deliveredBy?: string;
  runId?: string;
  drifted?: boolean;
}

/**
 * A removal of one or many records. The records are named by `keys` or by `filter` (every record it matches),
 * never both. `expected` is the count the operator was shown: the API refuses the removal when the filter no
 * longer resolves to it, rather than running against a set that changed underneath them.
 */
export interface DeliveryRemovalRequest {
  scope: RemovalScope;
  keys?: string[];
  filter?: DeliveryRecordFilter;
  expected?: number;
}

/** What a removal would act on, and from where: the contents of the confirmation. */
export interface DeliveryRemovalPreview {
  scope: RemovalScope;
  records: number;
  inOsdu: number;
  neverDelivered: number;
  capped: boolean;
  target: DeliveryTarget;
}

/** A removal was queued on a node. */
export interface DeliveryRemovalAccepted {
  taskId: string;
  status: RunStatus;
  scope: RemovalScope;
  records: number;
}

export interface DeliveryActivityListQuery extends PageQuery {
  pipelineId?: string;
  /** Which interface of that source; required when it delivers more than one. */
  interface?: string;
  submissionId?: string;
  runId?: string;
  kind?: string;
  actor?: string;
  outcome?: string;
  since?: string;
  until?: string;
}

/** One retrieval run of a retrieval flow: the window it covered, where its files went, and its outcome. */
export interface DeliveryRetrieval {
  retrievalId: number;
  flowId: string;
  flowName: string;
  runId: string | null;
  actor: string;
  kinds: string;
  query: string | null;
  windowField: string | null;
  windowFrom: string | null;
  windowTo: string | null;
  location: string;
  manifestLocation: string | null;
  status: "running" | "done" | "failed" | "cancelled";
  records: number;
  files: number;
  bytes: number;
  startedUtc: string;
  completedUtc: string | null;
  error: string | null;
}

/** The shape of the value a template variable takes. */
export type DeliveryTemplateShape = "Value" | "ValueList" | "Group" | "GroupList" | "Whole";

/** Who writes a template variable: a mapping, OSDU Delivery itself (the id and the kind), or OSDU when it stores the record. */
export type DeliveryTemplateRole = "Mapping" | "Engine" | "Osdu";

/** A saved template version: the OSDU kind, its content version, when, by whom and from where it was saved, and how many synced mappings pin it. */
export interface DeliveryTemplate {
  kind: string;
  version: string;
  capturedUtc: string;
  capturedBy: string;
  origin: string;
  pinnedBy: number;
}

/** One variable of a template: a property of the OSDU record, with what the schema says about it. */
export interface DeliveryTemplateVariable {
  /** The variable's path, such as osdu.data.Name or osdu.data.Curves[].CurveUnit; `[]` steps into an array of objects. */
  path: string;
  shape: DeliveryTemplateShape;
  type: string;
  itemType: string | null;
  format: string | null;
  required: boolean;
  role: DeliveryTemplateRole;
  relationships: string[];
  pattern: string | null;
  unitContext: string | null;
  title: string | null;
  description: string | null;
  /** For an object with free keys (tags): the type of the value under any key. Entries target `<path>.<name>`. */
  keyValueType: string | null;
  /** A list inside a repeated item: listed for reference, never fillable. */
  nested: boolean;
  /** The repository's cached types this variable can be read from, when a repository was given. */
  cacheTypes: string[];
}

/** A template laid out variable by variable, parents before their children in schema order. `saved` is null for a schema not saved yet. */
export interface DeliveryTemplateDetail {
  kind: string;
  version: string;
  title: string | null;
  description: string | null;
  saved: DeliveryTemplate | null;
  variables: DeliveryTemplateVariable[];
}

/** What saving a schema did: `created` for a new version, `unchanged` for one already saved. */
export interface DeliveryTemplateSaved {
  template: DeliveryTemplate;
  outcome: "created" | "unchanged";
}

/**
 * A release of the OSDU data definitions, the Open Group's public repository of OSDU schemas: its version tag, the commit
 * the tag names, when that was committed, and the release's schema folder in the repository.
 */
export interface DeliveryOsduRelease {
  name: string;
  commit: string;
  publishedUtc: string | null;
  webUrl: string;
  /** The release is in the control plane's local copy, so reading it needs no download. */
  local: boolean;
}

/** The releases, newest first, and when the control plane last read the list from the repository. */
export interface DeliveryOsduReleases {
  syncedUtc: string | null;
  releases: DeliveryOsduRelease[];
}

/** What a sync did: the release list as read just now, and the releases it downloaded into the local copy. */
export interface DeliveryOsduSync {
  syncedUtc: string;
  releases: DeliveryOsduRelease[];
  downloaded: string[];
}

/** A record schema a release publishes: its kind, its status in the release, and its file with a link to it. */
export interface DeliveryOsduSchema {
  kind: string;
  entityType: string;
  version: string;
  /** PUBLISHED, DEVELOPMENT or OBSOLETE; null when the release's index gives none. */
  status: string | null;
  /** The file under the release's Generated folder, such as master-data/Wellbore.1.3.0.json. */
  path: string;
  webUrl: string;
}

/** Every record schema one release publishes, by entity type and newest version first. */
export interface DeliveryOsduSchemaIndex {
  release: DeliveryOsduRelease;
  schemas: DeliveryOsduSchema[];
}

/** A kind's schema bundled from a release: the template version it saves as, and the origin a save records. Nothing is saved. */
export interface DeliveryOsduSchemaFile {
  kind: string;
  version: string;
  release: DeliveryOsduRelease;
  path: string;
  webUrl: string;
  origin: string;
  schema: Record<string, unknown>;
}

/** What a difference between two template versions means for a mapping written for the older one. */
export type DeliveryTemplateChangeImpact = "Breaking" | "Additive" | "Wording";

/** A field of a variable that differs between the versions; `before` is null for a new variable, `after` for a removed one. */
export interface DeliveryTemplateFieldChange {
  /** type, format, pattern, keyValueType, unitContext, role, nested, required, relationships, title or description. */
  field: string;
  before: string | null;
  after: string | null;
  impact: DeliveryTemplateChangeImpact;
}

/** A variable that differs between two versions of a template. */
export interface DeliveryTemplateVariableChange {
  path: string;
  change: "Added" | "Removed" | "Changed";
  impact: DeliveryTemplateChangeImpact;
  role: DeliveryTemplateRole;
  fields: DeliveryTemplateFieldChange[];
}

/** One side of a comparison: a kind's version in a release, its status there, the template version it saves as, and its file as published. */
export interface DeliveryOsduComparisonSide {
  kind: string;
  release: DeliveryOsduRelease;
  path: string;
  webUrl: string;
  status: string | null;
  templateVersion: string;
  fileText: string;
}

/** A shared schema file the two versions refer to whose published text differs; a path, link and text are null where a version does not refer to it. */
export interface DeliveryOsduReferencedFile {
  /** The file's name without its version, such as AbstractFacility. */
  name: string;
  fromPath: string | null;
  toPath: string | null;
  fromWebUrl: string | null;
  toWebUrl: string | null;
  fromText: string | null;
  toText: string | null;
}

/** Two versions of a kind from the OSDU data definitions, compared as files and variable by variable. */
export interface DeliveryOsduComparison {
  from: DeliveryOsduComparisonSide;
  to: DeliveryOsduComparisonSide;
  sameFile: boolean;
  /** The files say the same thing once each one's own kind and file name are set aside. */
  onlyIdentifiersDiffer: boolean;
  sameTemplate: boolean;
  breaking: number;
  additive: number;
  wording: number;
  unchanged: number;
  changes: DeliveryTemplateVariableChange[];
  /** How many of the shared schema files both versions refer to are the same. */
  sameReferencedFiles: number;
  /** The shared schema files that differ, or that only one version refers to. */
  referencedFiles: DeliveryOsduReferencedFile[];
}

/** A type a cache holds: the name mappings read it by, its entity type, and the names its values are cached under. */
export interface DeliveryCachedType {
  name: string;
  entityType: string;
  fields: string[];
}

/** A delivery flow of a repository: its OSDU connection, the mapping and parameters it renders with, and the partition whose cache it reads. */
export interface DeliveryBuilderFlow {
  pipelineId: string;
  name: string;
  mapping: string;
  parameters: Record<string, string>;
  endpoint: string;
  /** The partition the flow delivers to, whose cache it reads; null when its target names none a cache is kept under. */
  cacheScope: string | null;
}

/** A repository as the mapping builder offers it: the git source a proposal opens against, and its delivery flows. */
export interface DeliveryBuilderRepo {
  repoId: string;
  name: string;
  sourceId: string | null;
  sourceBranch: string | null;
  flows: DeliveryBuilderFlow[];
}

/** A partition's cache as the mapping builder offers it: the partition, the cache flows filling it, its current version and its types. */
export interface DeliveryBuilderCache {
  scope: string;
  flows: string[];
  currentVersion: string | null;
  types: DeliveryCachedType[];
}

/** Where a draft entry's value comes from: a dataset column, a child dataset's rows, a cached record, or a fixed value. */
export type MappingDraftInput = "Dataset" | "Repeat" | "Cache" | "Static";

export type MappingDraftModifierKind = "trim" | "upper" | "lower" | "split" | "replace" | "equals" | "date" | "number";

export type MappingDraftConditionOperator = "is" | "isNot" | "isEmpty" | "isNotEmpty";

/** A parameter the mapping declares, which the flow supplies under render.parameters. */
export interface MappingDraftParameter {
  name: string;
  required: boolean;
  default: string | null;
  description: string | null;
}

/** One findBy line: the cached field, and the dataset column (without `dataset.`) or the fixed text it must equal. */
export interface MappingDraftFind {
  field: string;
  column: string | null;
  literal: string | null;
}

export interface MappingDraftReplacement {
  from: string;
  to: string;
}

/**
 * One modifier with its settings: split takes a separator and a part, replace its pairs, equals its text, date an optional
 * format in text, and number its decimal and group separators.
 */
export interface MappingDraftModifier {
  kind: MappingDraftModifierKind;
  separator: string | null;
  part: number | null;
  replacements: MappingDraftReplacement[] | null;
  text: string | null;
  /** number: the separator before the decimals, "." or ",". */
  decimalSeparator: string | null;
  /** number: the separator between groups of three digits, or null when the value is written without one. */
  groupSeparator: string | null;
}

/** An appliesWhen: the dataset column (without `dataset.`), the operator, and the text for is and isNot. */
export interface MappingDraftCondition {
  column: string;
  operator: MappingDraftConditionOperator;
  text: string | null;
}

/** One entry as the builder edits it. */
export interface MappingDraftEntry {
  target: string;
  input: MappingDraftInput;
  /** Dataset: `column`, or `child.column` for an entry inside a repeater. */
  column: string | null;
  /** Repeat: the child dataset whose rows become the items. */
  child: string | null;
  /** Cache: the cached type, such as UnitOfMeasure. */
  cacheType: string | null;
  /** Cache: the field to read, usually id. */
  cacheField: string | null;
  findBy: MappingDraftFind[];
  modifiers: MappingDraftModifier[];
  appliesWhen: MappingDraftCondition | null;
  required: boolean;
  ignoreSeparators: boolean;
  /** Static: the value as JSON text, such as "[\"a\"]", "\"MD\"", "5" or "true". */
  static: string | null;
  description: string | null;
  /** True when the builder proposed the entry from the cache, until someone edits it. */
  prefilled: boolean;
}

/** An example row and the exact record it must render to. */
export interface MappingDraftFixture {
  name: string;
  parameters: Record<string, string>;
  record: Record<string, string | null>;
  datasets: Record<string, Record<string, string | null>[]>;
  expected: string;
}

/** A mapping as the builder edits it: the header, the parameters, the entries and the fixtures. */
export interface MappingDraft {
  name: string;
  version: string;
  templateKind: string;
  templateVersion: string;
  description: string | null;
  system: string;
  /** The dataset columns of the key, without `dataset.`. */
  key: string[];
  /** The label as written, with {dataset.column} tokens. */
  label: string | null;
  parameters: MappingDraftParameter[];
  entries: MappingDraftEntry[];
  fixtures: MappingDraftFixture[];
}

/** What the builder found about a draft: an error stops the mapping from loading, a warning does not. */
export interface MappingDraftIssue {
  severity: "error" | "warning";
  message: string;
  target: string | null;
}

/** Starts a mapping for a repository and a saved template version. */
export interface DeliveryMappingDraftRequest {
  /** The partition whose cache the draft's cache entries are prefilled from; null prefills none. */
  scope: string | null;
  kind: string;
  version: string;
  name: string;
  mappingVersion: string;
  system: string;
}

/** A draft written as YAML, what the checks found, and whether the mapping loads and passes the preflight. */
export interface DeliveryMappingComposeResult {
  yaml: string;
  issues: MappingDraftIssue[];
  valid: boolean;
}

/** A mapping document read back into a draft, or the problems that stopped it. */
export interface DeliveryMappingParseResult {
  draft: MappingDraft | null;
  issues: MappingDraftIssue[];
}

/** A parameter a mapping declares, and the value its record shape was drawn with: the one given, else the default. */
export interface DeliveryMappingShapeParameter {
  name: string;
  required: boolean;
  default: string | null;
  description: string | null;
  value: string | null;
}

/**
 * The shape of the records a mapping renders, drawn by the renderer delivery uses: a placeholder naming the template's
 * type and the source wherever a value comes from a row or the cache. `record` is null when an issue stopped it.
 */
export interface DeliveryMappingShapeResult {
  record: Record<string, unknown> | null;
  parameters: DeliveryMappingShapeParameter[];
  notes: string[];
  issues: MappingDraftIssue[];
}

/**
 * Where one record lives: its flow's ledger id and its delivery key. A key alone names one record per flow that reads
 * the row, so every record address carries both, in the API and in the GUI.
 */
export interface DeliveryRecordRef {
  flowId: string;
  deliveryKey: string;
}

const recordApiPath = ({ flowId, deliveryKey }: DeliveryRecordRef) =>
  `/api/v1/delivery/records/${encodeURIComponent(flowId)}/${encodeURIComponent(deliveryKey)}`;

/** The GUI page of one record. */
export const deliveryRecordRoute = ({ flowId, deliveryKey }: DeliveryRecordRef) =>
  `/delivery/records/${encodeURIComponent(flowId)}/${encodeURIComponent(deliveryKey)}`;

/**
 * A flow-level path with the interface the request is about. A source that delivers several interfaces answers
 * nothing without one, because its records, submissions, target and counts are per interface, never summed.
 */
const flowPath = (pipelineId: string, suffix: string, interfaceName?: string | null) =>
  `/api/v1/delivery/flows/${pipelineId}${suffix}${interfaceName ? `?interface=${encodeURIComponent(interfaceName)}` : ""}`;

export const deliveryApi = {
  /** The counts of one interface, or of the whole source when it names none. */
  stats: (pipelineId: string, interfaceName?: string | null) =>
    get<DeliveryFlowStats>(`/api/v1/delivery/flows/${pipelineId}/stats`, interfaceName ? { interface: interfaceName } : {}),
  /** Every interface of a source, in the order a run takes them, each with its route and counts. */
  interfaces: (pipelineId: string) => get<DeliveryInterface[]>(`/api/v1/delivery/flows/${pipelineId}/interfaces`),
  records: (pipelineId: string, query: DeliveryRecordListQuery = {}) =>
    get<PagedResult<DeliveryRecord>>(`/api/v1/delivery/flows/${pipelineId}/records`, query as QueryParams),
  /**
   * A record by what an operator holds, across every flow: a delivery key lands on the record of every flow reading
   * that row; anything else is a prefix over the OSDU id, the source key, the label and the ingestion file name. The
   * ledger's indexed lookup, so it answers at production volume and counts no further than its candidate bound.
   */
  lookupRecords: (query: DeliveryRecordLookupQuery) =>
    get<PagedResult<DeliveryRecordHit>>("/api/v1/delivery/records", query as unknown as QueryParams),
  /** A flow's submissions, newest first. */
  submissions: (pipelineId: string, max?: number, interfaceName?: string | null) =>
    get<DeliverySubmission[]>(`/api/v1/delivery/flows/${pipelineId}/submissions`, {
      ...(max ? { max } : {}),
      ...(interfaceName ? { interface: interfaceName } : {}),
    }),
  retrievals: (pipelineId: string, max?: number) =>
    get<DeliveryRetrieval[]>(`/api/v1/delivery/flows/${pipelineId}/retrievals`, max ? { max } : {}),
  record: (record: DeliveryRecordRef) => get<DeliveryRecordDetail>(recordApiPath(record)),
  attempts: (record: DeliveryRecordRef, max?: number) =>
    get<DeliveryAttempt[]>(`${recordApiPath(record)}/attempts`, max ? { max } : {}),
  recordActivities: (record: DeliveryRecordRef, max?: number) =>
    get<DeliveryActivity[]>(`${recordApiPath(record)}/activities`, max ? { max } : {}),
  submission: (submissionId: string) => get<DeliverySubmissionDetail>(`/api/v1/delivery/submissions/${submissionId}`),
  submissionAttempts: (submissionId: string, max?: number) =>
    get<DeliveryAttempt[]>(`/api/v1/delivery/submissions/${submissionId}/attempts`, max ? { max } : {}),
  submissionBatches: (submissionId: string, query: PageQuery & { status?: DeliveryWorkBatch["status"] } = {}) =>
    get<PagedResult<DeliveryWorkBatch>>(`/api/v1/delivery/submissions/${submissionId}/batches`, query as QueryParams),
  activities: (query: DeliveryActivityListQuery = {}) =>
    get<PagedResult<DeliveryActivity>>("/api/v1/delivery/activities", query as QueryParams),
  activity: (activityId: number) => get<DeliveryActivity>(`/api/v1/delivery/activities/${activityId}`),
  mappings: (repoId?: string, status?: string) =>
    get<DeliveryMapping[]>("/api/v1/delivery/mappings", { repoId, status }),
  mapping: (mappingId: string) => get<DeliveryMappingDetail>(`/api/v1/delivery/mappings/${mappingId}`),
  /** The saved template versions, with how many synced mappings pin each. */
  templates: () => get<DeliveryTemplate[]>("/api/v1/delivery/templates"),
  /** A saved template laid out variable by variable; with `scope`, each variable names the types of that partition's cache it can be read from. */
  templateDetail: (kind: string, version: string, scope?: string) =>
    get<DeliveryTemplateDetail>("/api/v1/delivery/templates/detail", { kind, version, scope }),
  /** The saved template's bundled schema as JSON text, exactly as it was saved. */
  templateSchema: (kind: string, version: string) =>
    getText(`/api/v1/delivery/templates/schema?${new URLSearchParams({ kind, version }).toString()}`),
  /**
   * Lays out a schema as a template without saving it; a 400 says why the JSON is not a record schema. A schema that refers
   * to the shared schemas of the OSDU data definitions has them read from `release` (the newest when omitted).
   */
  previewTemplate: (kind: string, schema: Record<string, unknown>, scope?: string | null, release?: string | null) =>
    post<DeliveryTemplateDetail>("/api/v1/delivery/templates/preview", { kind, schema, scope: scope ?? null, release: release ?? null }),
  /** The releases of the OSDU data definitions (the Open Group's public schema repository), newest first; a 502 when it cannot be read. */
  osduReleases: () => get<DeliveryOsduReleases>("/api/v1/delivery/templates/osdu/releases"),
  /** Reads the release list again from the repository and downloads `release` (the newest when omitted) when it is not local; needs the operate scope. */
  osduSync: (release?: string | null) =>
    post<DeliveryOsduSync>("/api/v1/delivery/templates/osdu/sync", { release: release ?? null }),
  /** Every record kind a release of the OSDU data definitions publishes; a 404 for a release it does not have. */
  osduSchemas: (release: string) =>
    get<DeliveryOsduSchemaIndex>("/api/v1/delivery/templates/osdu/schemas", { release }),
  /** One kind's schema from a release, bundled with every schema it refers to. Nothing is saved; a 404 for a kind the release does not publish. */
  osduSchema: (release: string, kind: string) =>
    get<DeliveryOsduSchemaFile>("/api/v1/delivery/templates/osdu/schema", { release, kind }),
  /** Two versions of one kind, each from a release, compared as published files and variable by variable; a 400 for two different kinds. */
  osduCompare: (from: { release: string; kind: string }, to: { release: string; kind: string }) =>
    get<DeliveryOsduComparison>("/api/v1/delivery/templates/osdu/compare", {
      fromRelease: from.release,
      fromKind: from.kind,
      toRelease: to.release,
      toKind: to.kind,
    }),
  /**
   * Saves a schema as a template version; saving one already saved changes nothing. References to the OSDU data definitions
   * are read from `release`, as a preview reads them, and the saved origin names the release.
   */
  saveTemplate: (kind: string, schema: Record<string, unknown>, origin: string, release?: string | null) =>
    post<DeliveryTemplateSaved>("/api/v1/delivery/templates", { kind, schema, origin, release: release ?? null }),
  /** Deletes a saved template version; refused with a 409 while a synced mapping pins it. */
  deleteTemplate: (kind: string, version: string) =>
    del<void>(`/api/v1/delivery/templates?${new URLSearchParams({ kind, version }).toString()}`),
  /** The repositories the mapping builder offers, with their git source and delivery flows. */
  builderRepos: () => get<DeliveryBuilderRepo[]>("/api/v1/delivery/mapping-builder/repos"),
  /** The partition caches the mapping builder offers, with the flows filling each, their current version and their types. */
  builderCaches: () => get<DeliveryBuilderCache[]>("/api/v1/delivery/mapping-builder/caches"),
  /** A new draft for a saved template version, prefilled from the named partition's cache; a 404 when the template is not saved. */
  draftMapping: (request: DeliveryMappingDraftRequest) =>
    post<MappingDraft>("/api/v1/delivery/mapping-builder/draft", request),
  /** Writes a draft as YAML and checks it against its template and the named partition cache's current version, rendering with `parameters`. */
  composeMapping: (scope: string | null, draft: MappingDraft, parameters: Record<string, string> | null) =>
    post<DeliveryMappingComposeResult>("/api/v1/delivery/mapping-builder/compose", { scope, draft, parameters }),
  /** Reads a mapping document back into a draft for the builder. */
  parseMapping: (yaml: string, path: string | null) =>
    post<DeliveryMappingParseResult>("/api/v1/delivery/mapping-builder/parse", { yaml, path }),
  /** The shape of the records a mapping document renders, drawn with `parameters`; nothing is read or stored. */
  mappingShape: (yaml: string, path: string | null, parameters: Record<string, string>) =>
    post<DeliveryMappingShapeResult>("/api/v1/delivery/mapping-builder/shape", { yaml, path, parameters }),
  /** Every partition's cache: the cache flows filling it, what refreshes them, its types and its current version. */
  caches: (repoId?: string) =>
    get<DeliveryCache[]>("/api/v1/delivery/caches", { repoId }),
  /** The versions of one partition's cache, newest first, each with the flow and run that wrote it: what the version picker offers. */
  cacheVersions: (scope: string) =>
    get<DeliveryCacheVersion[]>("/api/v1/delivery/cache/versions", { scope }),
  /** The records of one partition's cache at one version (the current one when none is named). */
  cachedItems: (query: PageQuery & { scope: string; type?: string; search?: string; version?: string }) =>
    get<PagedResult<DeliveryCachedItem>>("/api/v1/delivery/cache/items", query as unknown as QueryParams),
  /** What changed in one partition's cache between two versions (`to` defaults to the current one), a page of records at a time. */
  cacheDiff: (query: DeliveryCacheDiffQuery) =>
    get<DeliveryCacheDiff>("/api/v1/delivery/cache/diff", query),
  /** Every version of one partition's cache, newest first, with what it changed against the one before it; `type` narrows the counts to one type. */
  cacheHistory: (scope: string, type?: string) =>
    get<DeliveryCacheHistoryEntry[]>("/api/v1/delivery/cache/history", { scope, type }),
  /** The cache changes delivered records were built from, by status (pending, approved, rolling, rejected, applied) and partition. */
  updateTags: (query: PageQuery & { status?: string; scope?: string }) =>
    get<PagedResult<DeliveryUpdateTag>>("/api/v1/delivery/cache/tags", query as QueryParams),
  /** Approves or rejects tags; approving lets the next run carry the new document to OSDU. */
  decideTags: (tagIds: number[], approve: boolean) =>
    post<{ decided: number; approved: boolean }>("/api/v1/delivery/cache/tags/decide", { tagIds, approve }),
  /** What one record read out of the cache when it was rendered. */
  recordCacheUses: (record: DeliveryRecordRef) => get<DeliveryCacheUse[]>(`${recordApiPath(record)}/cache`),
  /** Releases the flow's held, failed and deleted records (all of them, or the given keys) back to pending. */
  releaseFlow: (pipelineId: string, keys?: string[], interfaceName?: string | null) =>
    post<DeliveryReleaseResult>(flowPath(pipelineId, "/release", interfaceName), { keys: keys ?? null }),
  /** Queues a target probe on a node: is OSDU reachable with the flow's credentials? */
  probe: (pipelineId: string, interfaceName?: string | null) =>
    post<ComputeTaskAccepted>(flowPath(pipelineId, "/probe", interfaceName)),
  release: (record: DeliveryRecordRef) => post<DeliveryReleaseResult>(`${recordApiPath(record)}/release`),
  /** Marks the record for redelivery and (with run) queues the deliver run that sends it. */
  redeliver: (record: DeliveryRecordRef, scope: "all" | "metadata" | "payload" = "all", run = true) =>
    post<DeliveryRedeliverResult>(`${recordApiPath(record)}/redeliver`, { scope, run }),
  /** Queues a verify run scoped to this record. */
  verify: (record: DeliveryRecordRef) => post<DeliveryRunAccepted>(`${recordApiPath(record)}/verify`),
  /** Queues a read-back of the record as its flow wrote it to OSDU; poll the task for the document. */
  read: (record: DeliveryRecordRef) => post<ComputeTaskAccepted>(`${recordApiPath(record)}/read`),
  /**
   * Queues a read of the record's rows as the ingestion tables hold them now, on a node: the record row with its
   * system columns, its child datasets, and the origin file and row the ingestion tables record. Poll the task.
   */
  readSource: (record: DeliveryRecordRef) => post<ComputeTaskAccepted>(`${recordApiPath(record)}/source`),
  /** Where the flow's records live, and which call each removal scope makes against them. */
  target: (pipelineId: string, interfaceName?: string | null) =>
    get<DeliveryTarget>(`/api/v1/delivery/flows/${pipelineId}/target`, interfaceName ? { interface: interfaceName } : {}),
  /** What a removal would act on, without removing anything: the confirmation's contents. */
  previewRemoval: (pipelineId: string, request: DeliveryRemovalRequest, interfaceName?: string | null) =>
    post<DeliveryRemovalPreview>(flowPath(pipelineId, "/records/remove/preview", interfaceName), request),
  /** Queues the removal of the selected records, or of every record the filter matches, on a node. */
  removeRecords: (pipelineId: string, request: DeliveryRemovalRequest, interfaceName?: string | null) =>
    post<DeliveryRemovalAccepted>(flowPath(pipelineId, "/records/remove", interfaceName), request),
  prune: (olderThanDays: number) => post<DeliveryPruneResult>("/api/v1/delivery/ledger/prune", { olderThanDays }),
};

