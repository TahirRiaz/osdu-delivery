// The delivery ledger's API: what each flow delivered (records, their history, their submissions), the audit trail,
// the mappings and snapshots the repositories hold, and the interventions (release, redeliver, verify, read back,
// delete). Same conventions as endpoints.ts: one function per endpoint, pages compose them with TanStack Query.

import { get, post, type QueryParams } from "./client";
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

/** The manifest notification: a drop is ready and asks to be delivered. */
export interface DeliverySubmissionRequest {
  pipelineId?: string | null;
  repoId?: string | null;
  flow?: string | null;
  drop: string;
  parameters?: Record<string, string> | null;
  force?: boolean;
  pool?: string | null;
}

export interface DeliverySubmissionAccepted {
  runId: string;
  pipelineId: string;
  flowName: string;
  status: RunStatus;
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
  submissions: (pipelineId: string, max?: number) =>
    get<DeliverySubmission[]>(`/api/v1/delivery/flows/${pipelineId}/submissions`, max ? { max } : {}),
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
  /** The cache changes delivered records were built from, by status: pending, approved, rolling, rejected, applied. */
  updateTags: (query: PageQuery & { status?: string }) =>
    get<PagedResult<DeliveryUpdateTag>>("/api/v1/delivery/cache/tags", query as QueryParams),
  /** Approves or rejects tags; approving lets the next run carry the new document to OSDU. */
  decideTags: (tagIds: number[], approve: boolean) =>
    post<{ decided: number; approved: boolean }>("/api/v1/delivery/cache/tags/decide", { tagIds, approve }),
  /** What one record read out of the cache when it was rendered. */
  recordCacheUses: (key: string) => get<DeliveryCacheUse[]>(`/api/v1/delivery/records/${key}/cache`),
  /** The manifest notification: queues the deliver run for a drop. */
  submit: (request: DeliverySubmissionRequest) => post<DeliverySubmissionAccepted>("/api/v1/delivery/submissions", request),
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
