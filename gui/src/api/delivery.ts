// The delivery ledger's API: what each flow delivered (records, their history, their submissions), the audit trail,
// the mappings and snapshots the repositories hold, and the interventions (release, redeliver, verify, read back,
// delete). Same conventions as endpoints.ts: one function per endpoint, pages compose them with TanStack Query.

import { del, get, post, postForm, type QueryParams } from "./client";
import type { PagedResult, RunStatus } from "./types";
import type { PageQuery } from "./endpoints";

export type DeliveryRecordStatus = "pending" | "delivering" | "delivered" | "held" | "failed" | "deleted";

export const DELIVERY_RECORD_STATUSES: readonly DeliveryRecordStatus[] = [
  "pending", "delivering", "delivered", "held", "failed", "deleted",
];

export type DeliverySubmissionStatus = "received" | "planned" | "running" | "completed" | "failed";

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
  drifted: number;
  deliveredLast24h: number;
  lastDeliveredUtc: string | null;
  lastVerifiedUtc: string | null;
  submissions: number;
  lastSubmission: DeliverySubmission | null;
}

/** One drop as the ledger received it and what became of it. */
export interface DeliverySubmission {
  submissionId: string;
  flowId: string;
  flowName: string;
  mappingReference: string;
  renderContext: string;
  dropLocation: string;
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
  /** Records the drop carried in a version older than the one delivered or queued: skipped, never sent. */
  skippedStale: number;
  /** Records whose queued document OSDU already held when the worker came to send it: nothing was sent. */
  unchangedAtPush: number;
  blocked: number;
  delivered: number;
  held: number;
  failed: number;
  error: string | null;
  /** Where the intake wrote the work batches (the rendered documents the drains read). */
  workLocation: string | null;
  batchCount: number;
  /** How many root-scope partitions the drop declared. */
  partitions: number;
  /** What the sending system calls this submission in its own records; null when it named none. */
  reference: string | null;
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
}

export interface DeliveryRecordDetail {
  record: DeliveryRecord;
  pipelineId: string | null;
  repoId: string | null;
  flowName: string | null;
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

/** One path a cache captures, and the name it is cached under. */
export interface DeliveryCacheField {
  path: string;
  as: string;
}

/** A cached OSDU type as a retrieval flow declares it: what is cached and which paths are captured. */
export interface DeliveryCacheDefinition {
  id: string;
  repoId: string;
  flowName: string;
  relativePath: string;
  name: string;
  entityType: string;
  kind: string;
  query: string | null;
  fields: DeliveryCacheField[];
  makeCurrent: boolean;
  /** How many records the snapshot version being read holds for this type. */
  items: number;
  version: string | null;
  capturedUtc: string | null;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

/** One cached record: its OSDU id and the values captured at the declared paths, in whatever shape they came. */
export interface DeliveryCachedItem {
  itemId: number;
  snapshotId: string;
  /** The snapshot version this row is the record as of. */
  version: string;
  typeName: string;
  entityType: string;
  recordId: string;
  fields: Record<string, unknown>;
}

/**
 * One reference snapshot version of the cache: what the version picker offers. `carried` says whether the catalog
 * still holds this version's items; older versions stay listed after their items are aged out, and the snapshot
 * files remain complete either way.
 */
export interface DeliveryCacheVersion {
  repoId: string;
  repoName: string;
  version: string;
  capturedUtc: string | null;
  current: boolean;
  carried: boolean;
  items: number;
}

/** How one cached record differs between two snapshot versions. */
export type DeliveryCacheChange = "changed" | "added" | "removed";

/** A comparison of two cache versions: `from` is required, `to` defaults to the current version, `change` narrows the items only. */
export type DeliveryCacheDiffQuery = {
  from: string;
  to?: string;
  repoId?: string;
  type?: string;
  change?: DeliveryCacheChange;
  search?: string;
  page?: number;
  pageSize?: number;
};

/** One cached record that differs between two snapshot versions, with what it held on each side. */
export interface DeliveryCacheDiffItem {
  repoId: string;
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

/** A repository in scope whose cache could not be compared, and why. */
export interface DeliveryCacheDiffGap {
  repoId: string;
  repoName: string;
  reason: string;
}

/**
 * What changed in the cache between two snapshot versions. The counts follow the type and search filters but not the
 * change filter, so they describe every kind of change while the items show the one picked.
 */
export interface DeliveryCacheDiff {
  fromVersion: string;
  /** The later version as asked for; null when it is each repository's current version. */
  toVersion: string | null;
  changed: number;
  added: number;
  removed: number;
  types: DeliveryCacheDiffType[];
  gaps: DeliveryCacheDiffGap[];
  items: PagedResult<DeliveryCacheDiffItem>;
}

/**
 * One version in the cache's history: the version of the same repository captured before it and, when the catalog
 * carries both (`compared`), how many records it changed, added and removed. The counts are null otherwise.
 */
export interface DeliveryCacheHistoryEntry {
  version: DeliveryCacheVersion;
  previousVersion: string | null;
  compared: boolean;
  changed: number | null;
  added: number | null;
  removed: number | null;
}

/** One cache change and what happens about it: it covers every delivered record built from the value that moved. */
export interface DeliveryUpdateTag {
  tagId: number;
  kind: string;
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
  typeName: string;
  itemId: string;
  path: string;
  /** match (what it resolved by) or value (what went into the document). */
  kind: string;
  value: string;
}

/** A schema or reference snapshot version as the sync found it. */
export interface DeliverySnapshot {
  id: string;
  repoId: string;
  kind: "schema" | "references";
  name: string;
  version: string;
  capturedUtc: string | null;
  current: boolean;
  relativePath: string;
  summary: Record<string, unknown>;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

/** A value of an inline record's column: a JSON scalar. A collection is a child scope, never a nested value. */
export type DeliveryInlineValue = string | number | boolean | null;

/** Where one record's payload files already sit: the location alone, or with the content hash the flow decides changes by. */
export type DeliveryInlineFile = string | { location: string; hash?: string };

/**
 * One inline record: its root row, the rows of each child scope (the shape of a mapping fixture), and where the files of
 * each payload the flow streams already sit. Files are pointed at, never uploaded: the node opens the location with its
 * own identity when the run delivers.
 */
export interface DeliveryInlineRecord {
  record: Record<string, DeliveryInlineValue>;
  scopes?: Record<string, Array<Record<string, DeliveryInlineValue>>>;
  files?: Record<string, DeliveryInlineFile>;
}

export type DeliverySubmissionOperation = "deliver" | "plan";

/**
 * A submission: the manifest notification for a finished drop (`drop`), or the records themselves (`records`), never
 * both. `operation` is deliver (the default) or plan; `submissionId` is the idempotency key of inline records.
 */
export interface DeliverySubmissionRequest {
  pipelineId?: string | null;
  repoId?: string | null;
  flow?: string | null;
  drop?: string | null;
  records?: DeliveryInlineRecord[] | null;
  submissionId?: string | null;
  operation?: DeliverySubmissionOperation;
  parameters?: Record<string, string> | null;
  force?: boolean;
  pool?: string | null;
  /**
   * What the sending system calls this submission in its own records (a filename, a ticket, a job id): stored,
   * searchable, never interpreted. It is part of the request `submissionId` names, so a repeat carrying a different one
   * is refused. A drop's reference is the one its manifest carries.
   */
  reference?: string | null;
}

export interface DeliverySubmissionAccepted {
  runId: string;
  pipelineId: string;
  flowName: string;
  status: RunStatus;
  /** The inline submission's id; null for a drop, whose id is its manifest's. */
  submissionId: string | null;
  /** True when this answered a repeat of a request already accepted: the run is the one that request started. */
  replayed: boolean;
}

/** A parameter a flow declares. */
export interface DeliveryFlowParameter {
  name: string;
  required: boolean;
  default: string | null;
  description: string | null;
}

/** What a source sends a flow: its parameters, the columns its mapping reads, and whether it takes records inline. */
export interface DeliverySourceContract {
  pipelineId: string;
  flowName: string;
  mappingReference: string;
  protocol: string;
  acceptsRecords: boolean;
  recordsRefusal: string | null;
  parameters: DeliveryFlowParameter[];
  recordColumns: string[];
  scopes: Array<{ scope: string; columns: string[] }>;
  naturalKey: string[];
  lastModifiedColumn: string | null;
  fingerprintColumn: string | null;
  /** Why the columns are unknown, when the catalog cannot read the flow's pinned mapping. */
  mappingProblem: string | null;
  maxRecords: number;
  maxChildRows: number;
  maxContentBytes: number;
  /** The payload the flow streams, which every record then points at under `files`; null when it streams none. */
  payloadName: string | null;
  /** True when each record has to carry the payload's content hash: the flow decides payload changes by hash. */
  payloadHashRequired: boolean;
  /** Where a record's payload files may sit: the roots the flow allows. */
  payloadRoots: string[];
}

/** One delivery flow on the manual submission page: whether its document offers manual submission, and what it needs. */
export interface DeliveryManualFlow {
  pipelineId: string;
  repoId: string;
  flowName: string;
  batch: string | null;
  mappingReference: string;
  protocol: string;
  acceptsRecords: boolean;
  /** Why the flow takes no records; null when it does. */
  recordsRefusal: string | null;
  parameters: DeliveryFlowParameter[];
  /** The payload its records point at; null when the flow streams no files. */
  payloadName: string | null;
}

/** Where a drop-off file's content hash came from, so a claim never reads as a check. */
export type DeliveryDropOffHashSource = "computed" | "client" | "none";

/** How a drop-off's bytes reached storage. */
export type DeliveryDropOffUploadMode = "stream" | "signed";

/** One file in a drop-off, as it landed. */
export interface DeliveryDropOffFile {
  name: string;
  bytes: number;
  sha256: string;
  /**
   * `computed` when the control plane hashed the bytes as they streamed past it, `client` when the uploader asserted the
   * hash about a file written straight to storage, `none` when a signed upload asserted none.
   */
  hashSource: DeliveryDropOffHashSource;
}

/**
 * Files uploaded into the drop-off area, for a submission to point at afterwards. `location` is what goes into a
 * submission's `files`; the node reads the files from there when the run delivers.
 */
export interface DeliveryDropOff {
  dropOffId: string;
  location: string;
  status: "uploading" | "complete" | "failed" | "deleted";
  fileCount: number;
  totalBytes: number;
  label: string | null;
  uploadedUtc: string;
  uploadedBy: string;
  completedUtc: string | null;
  deletedUtc: string | null;
  /** Why an upload failed, redacted; null otherwise. */
  error: string | null;
  files: DeliveryDropOffFile[];
  /** How the bytes got here. While a `signed` drop-off is `uploading`, its files are what was reserved, not what landed. */
  uploadMode: DeliveryDropOffUploadMode;
  /** When a reservation's upload URLs stop working; null for a streamed upload. */
  reservedUntilUtc: string | null;
}

/** Whether this deployment offers a drop-off area at all, where it is, and what one upload may carry. */
export interface DeliveryDropOffArea {
  enabled: boolean;
  location: string | null;
  maxFileMegabytes: number;
  maxFilesPerUpload: number;
  /** Days a completed drop-off is kept before a sweep removes it; 0 means nothing is removed automatically. */
  retentionDays: number;
  /** Whether a caller can be handed URLs to write straight to storage, which is what a file too large to stream needs. */
  signedUploads: boolean;
  /** The largest single file a signed upload may carry; 0 when signed uploads are unavailable. */
  maxSignedFileGigabytes: number;
  /** How long a reservation's URLs stay valid; 0 when signed uploads are unavailable. */
  signedUploadExpiryMinutes: number;
}

/** One file a caller asks to upload itself: its name, and how large it will be. */
export interface DeliveryDropOffReserveFile {
  name: string;
  bytes: number;
}

/** One file's write-only URL. It carries its own credential, so it is used and not stored. */
export interface DeliveryDropOffUpload {
  name: string;
  location: string;
  url: string;
  expiresUtc: string;
}

/** A reservation: the drop-off it will become, and where to write each file. Nothing has landed yet. */
export interface DeliveryDropOffReservation {
  dropOffId: string;
  location: string;
  status: "uploading" | "complete" | "failed" | "deleted";
  label: string | null;
  uploadedUtc: string;
  uploadedBy: string;
  reservedUntilUtc: string;
  uploads: DeliveryDropOffUpload[];
}

/** An inline submission's records as the ledger holds them, with who sent them and where a run wrote them. */
export interface DeliveryInlineSubmission {
  submissionId: string;
  flowId: string;
  flowName: string;
  pipelineId: string | null;
  mappingReference: string;
  operation: DeliverySubmissionOperation;
  force: boolean;
  parametersJson: string;
  recordCount: number;
  childRowCount: number;
  contentBytes: number;
  contentHash: string;
  receivedUtc: string;
  receivedBy: string;
  dropLocation: string | null;
  writtenUtc: string | null;
  runIds: string[];
  records: DeliveryInlineRecord[];
}

export interface DeliveryRunAccepted {
  runId: string;
  status: RunStatus;
}

export interface ComputeTaskAccepted {
  taskId: string;
  status: RunStatus;
}

/** One ad-hoc compute task as the queue holds it, with its result once a node produced it. */
export interface ComputeTask {
  taskId: string;
  operation: string;
  sourceRef: string;
  status: RunStatus;
  requestedBy: string | null;
  enqueuedUtc: string;
  startUtc: string | null;
  endUtc: string | null;
  claimedByNode: string | null;
  cancelRequestedUtc: string | null;
  error: string | null;
  resultJson: string | null;
}

export interface DeliveryReleaseResult {
  released: number;
}

export interface DeliveryRedeliverResult {
  marked: number;
  runId: string | null;
}

export interface DeliveryPruneResult {
  attemptsPruned: number;
}

export interface DeliveryRecordListQuery extends PageQuery {
  /** A delivery key (exact), or a prefix over label, source key and target id. */
  search?: string;
  /** "contains" for the slower substring match; prefix by default. */
  mode?: "prefix" | "contains";
  status?: DeliveryRecordStatus;
  submissionId?: string;
  /** Only records the given platform run touched, resolved through that run's attempts. */
  runId?: string;
  /** Only delivered records whose last verify found drift or a missing record. */
  drifted?: boolean;
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
  historyPath: string;
  everythingPath: string;
}

/** The listing a removal is aimed at: the same filter the records list is built from. */
export interface DeliveryRecordFilter {
  status?: DeliveryRecordStatus;
  search?: string;
  mode?: "prefix" | "contains";
  submissionId?: string;
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

export const deliveryApi = {
  stats: (pipelineId: string) => get<DeliveryFlowStats>(`/api/v1/delivery/flows/${pipelineId}/stats`),
  records: (pipelineId: string, query: DeliveryRecordListQuery = {}) =>
    get<PagedResult<DeliveryRecord>>(`/api/v1/delivery/flows/${pipelineId}/records`, query as QueryParams),
  /** A flow's submissions, newest first; `reference` narrows them to the ones whose caller-supplied reference contains it. */
  submissions: (pipelineId: string, max?: number, reference?: string) =>
    get<DeliverySubmission[]>(`/api/v1/delivery/flows/${pipelineId}/submissions`, {
      ...(max ? { max } : {}),
      ...(reference ? { reference } : {}),
    }),
  retrievals: (pipelineId: string, max?: number) =>
    get<DeliveryRetrieval[]>(`/api/v1/delivery/flows/${pipelineId}/retrievals`, max ? { max } : {}),
  record: (key: string) => get<DeliveryRecordDetail>(`/api/v1/delivery/records/${key}`),
  attempts: (key: string, max?: number) =>
    get<DeliveryAttempt[]>(`/api/v1/delivery/records/${key}/attempts`, max ? { max } : {}),
  recordActivities: (key: string, max?: number) =>
    get<DeliveryActivity[]>(`/api/v1/delivery/records/${key}/activities`, max ? { max } : {}),
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
  snapshots: (repoId?: string, kind?: string) =>
    get<DeliverySnapshot[]>("/api/v1/delivery/snapshots", { repoId, kind }),
  /** The cache as the repositories declare it: one row per cached type, with what it captures and holds at `version`. */
  cache: (repoId?: string, search?: string, version?: string) =>
    get<DeliveryCacheDefinition[]>("/api/v1/delivery/cache", { repoId, search, version }),
  /** The snapshot versions of the cache, newest capture first: what the version picker offers. */
  cacheVersions: (repoId?: string) =>
    get<DeliveryCacheVersion[]>("/api/v1/delivery/cache/versions", { repoId }),
  /** The cached records themselves at one snapshot version (the current one when none is named). */
  cachedItems: (query: PageQuery & { repoId?: string; type?: string; search?: string; version?: string }) =>
    get<PagedResult<DeliveryCachedItem>>("/api/v1/delivery/cache/items", query as QueryParams),
  /** What changed in the cache between two snapshot versions (`to` defaults to the current one), a page of records at a time. */
  cacheDiff: (query: DeliveryCacheDiffQuery) =>
    get<DeliveryCacheDiff>("/api/v1/delivery/cache/diff", query),
  /** Every version, newest first, with what it changed against the one before it; `type` narrows the counts to one cached type. */
  cacheHistory: (repoId?: string, type?: string) =>
    get<DeliveryCacheHistoryEntry[]>("/api/v1/delivery/cache/history", { repoId, type }),
  /** The cache changes delivered records were built from, by status: pending, approved, rolling, rejected, applied. */
  updateTags: (query: PageQuery & { status?: string }) =>
    get<PagedResult<DeliveryUpdateTag>>("/api/v1/delivery/cache/tags", query as QueryParams),
  /** Approves or rejects tags; approving lets the next run carry the new document to OSDU. */
  decideTags: (tagIds: number[], approve: boolean) =>
    post<{ decided: number; approved: boolean }>("/api/v1/delivery/cache/tags/decide", { tagIds, approve }),
  /** What one record read out of the cache when it was rendered. */
  recordCacheUses: (key: string) => get<DeliveryCacheUse[]>(`/api/v1/delivery/records/${key}/cache`),
  /** A submission, of a drop or of records: queues the run that takes it (or answers with the run of a repeated request). */
  submit: (request: DeliverySubmissionRequest) => post<DeliverySubmissionAccepted>("/api/v1/delivery/submissions", request),
  /** What a source sends the flow: parameters, the columns the mapping reads, and whether it takes records inline. */
  sourceContract: (pipelineId: string) => get<DeliverySourceContract>(`/api/v1/delivery/flows/${pipelineId}/source-contract`),
  /** The flows records can be submitted to by hand; with `all`, the other delivery flows too, each with its reason. */
  manualSubmissionFlows: (all = false) =>
    get<DeliveryManualFlow[]>("/api/v1/delivery/manual-submission/flows", all ? { all: true } : {}),
  /** Whether this deployment offers a drop-off area, where it is, and what one upload may carry. */
  dropOffArea: () => get<DeliveryDropOffArea>("/api/v1/delivery/dropoff-area"),
  /** The drop-offs, newest first. */
  dropOffs: (query?: { status?: string; search?: string; limit?: number }) =>
    get<DeliveryDropOff[]>("/api/v1/delivery/dropoffs", query as QueryParams | undefined),
  dropOff: (dropOffId: string) => get<DeliveryDropOff>(`/api/v1/delivery/dropoffs/${dropOffId}`),
  /** Uploads files into the drop-off area; the location it answers with is what a submission then points at. */
  uploadDropOff: (files: File[], label?: string) => {
    const form = new FormData();
    for (const file of files) {
      form.append("files", file, file.name);
    }

    if (label !== undefined && label !== "") {
      form.append("label", label);
    }

    return postForm<DeliveryDropOff>("/api/v1/delivery/dropoffs", form);
  },
  /**
   * Reserves a drop-off the caller uploads into itself, for files too large to send through the control plane. The
   * answer carries one write-only URL per file; write each, then complete the reservation.
   */
  reserveDropOff: (files: DeliveryDropOffReserveFile[], label?: string) =>
    post<DeliveryDropOffReservation>("/api/v1/delivery/dropoffs/reserve", {
      files,
      label: label !== undefined && label !== "" ? label : null,
    }),
  /**
   * Closes a reservation once its files are written. What actually landed is read from storage and is what the ledger
   * records; a hash given here is the uploader's own and is recorded as asserted, not as checked.
   */
  completeDropOff: (dropOffId: string, files: { name: string; sha256?: string }[]) =>
    post<DeliveryDropOff>(`/api/v1/delivery/dropoffs/${dropOffId}/complete`, { files }),
  /** Removes a drop-off's files; the row stays, saying when they went and who took them. */
  deleteDropOff: (dropOffId: string) => del<DeliveryDropOff>(`/api/v1/delivery/dropoffs/${dropOffId}`),
  /** The records an inline submission carried (a 404 for a drop submission). */
  submissionContent: (submissionId: string) => get<DeliveryInlineSubmission>(`/api/v1/delivery/submissions/${submissionId}/content`),
  /** Releases the flow's held, failed and deleted records (all of them, or the given keys) back to pending. */
  releaseFlow: (pipelineId: string, keys?: string[]) =>
    post<DeliveryReleaseResult>(`/api/v1/delivery/flows/${pipelineId}/release`, { keys: keys ?? null }),
  /** Queues a target probe on a node: is OSDU reachable with the flow's credentials? */
  probe: (pipelineId: string) => post<ComputeTaskAccepted>(`/api/v1/delivery/flows/${pipelineId}/probe`),
  release: (key: string) => post<DeliveryReleaseResult>(`/api/v1/delivery/records/${key}/release`),
  /** Marks the record for redelivery and (with run) queues the deliver run that sends it. */
  redeliver: (key: string, scope: "all" | "metadata" | "payload" = "all", run = true) =>
    post<DeliveryRedeliverResult>(`/api/v1/delivery/records/${key}/redeliver`, { scope, run }),
  /** Queues a verify run scoped to this record. */
  verify: (key: string) => post<DeliveryRunAccepted>(`/api/v1/delivery/records/${key}/verify`),
  /** Queues a read-back of the record as OSDU holds it; poll the task for the document. */
  read: (key: string) => post<ComputeTaskAccepted>(`/api/v1/delivery/records/${key}/read`),
  /** Where the flow's records live, and which call each removal scope makes against them. */
  target: (pipelineId: string) => get<DeliveryTarget>(`/api/v1/delivery/flows/${pipelineId}/target`),
  /** What a removal would act on, without removing anything: the confirmation's contents. */
  previewRemoval: (pipelineId: string, request: DeliveryRemovalRequest) =>
    post<DeliveryRemovalPreview>(`/api/v1/delivery/flows/${pipelineId}/records/remove/preview`, request),
  /** Queues the removal of the selected records, or of every record the filter matches, on a node. */
  removeRecords: (pipelineId: string, request: DeliveryRemovalRequest) =>
    post<DeliveryRemovalAccepted>(`/api/v1/delivery/flows/${pipelineId}/records/remove`, request),
  prune: (olderThanDays: number) => post<DeliveryPruneResult>("/api/v1/delivery/ledger/prune", { olderThanDays }),
};

export const computeApi = {
  task: (taskId: string) => get<ComputeTask>(`/api/v1/compute/tasks/${taskId}`),
  cancel: (taskId: string) => post<void>(`/api/v1/compute/tasks/${taskId}/cancel`),
};
