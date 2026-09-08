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
  blocked: number;
  delivered: number;
  held: number;
  failed: number;
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
  metadataHash: string | null;
  payloadHash: string | null;
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
}

export interface DeliveryRecordDetail {
  record: DeliveryRecord;
  pipelineId: string | null;
  repoId: string | null;
  flowName: string | null;
  /** The rendered document waiting to be delivered, when the record is pending. */
  pendingDocument: string | null;
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
  outcome: "delivered" | "skipped" | "failed" | "held" | "deleted";
  phase: string;
  metadataHash: string | null;
  payloadHash: string | null;
  targetVersion: number | null;
  error: string | null;
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
  /** Only delivered records whose last verify found drift or a missing record. */
  drifted?: boolean;
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

export const deliveryApi = {
  stats: (pipelineId: string) => get<DeliveryFlowStats>(`/api/v1/delivery/flows/${pipelineId}/stats`),
  records: (pipelineId: string, query: DeliveryRecordListQuery = {}) =>
    get<PagedResult<DeliveryRecord>>(`/api/v1/delivery/flows/${pipelineId}/records`, query as QueryParams),
  submissions: (pipelineId: string, max?: number) =>
    get<DeliverySubmission[]>(`/api/v1/delivery/flows/${pipelineId}/submissions`, max ? { max } : {}),
  record: (key: string) => get<DeliveryRecordDetail>(`/api/v1/delivery/records/${key}`),
  attempts: (key: string, max?: number) =>
    get<DeliveryAttempt[]>(`/api/v1/delivery/records/${key}/attempts`, max ? { max } : {}),
  recordActivities: (key: string, max?: number) =>
    get<DeliveryActivity[]>(`/api/v1/delivery/records/${key}/activities`, max ? { max } : {}),
  submission: (submissionId: string) => get<DeliverySubmissionDetail>(`/api/v1/delivery/submissions/${submissionId}`),
  submissionAttempts: (submissionId: string, max?: number) =>
    get<DeliveryAttempt[]>(`/api/v1/delivery/submissions/${submissionId}/attempts`, max ? { max } : {}),
  activities: (query: DeliveryActivityListQuery = {}) =>
    get<PagedResult<DeliveryActivity>>("/api/v1/delivery/activities", query as QueryParams),
  activity: (activityId: number) => get<DeliveryActivity>(`/api/v1/delivery/activities/${activityId}`),
  mappings: (repoId?: string, status?: string) =>
    get<DeliveryMapping[]>("/api/v1/delivery/mappings", { repoId, status }),
  mapping: (mappingId: string) => get<DeliveryMappingDetail>(`/api/v1/delivery/mappings/${mappingId}`),
  snapshots: (repoId?: string, kind?: string) =>
    get<DeliverySnapshot[]>("/api/v1/delivery/snapshots", { repoId, kind }),
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
  /** Queues the removal of the record from OSDU (a logical delete, or a purge). */
  remove: (key: string, purge = false) => post<ComputeTaskAccepted>(`/api/v1/delivery/records/${key}/delete`, { purge }),
  prune: (olderThanDays: number) => post<DeliveryPruneResult>("/api/v1/delivery/ledger/prune", { olderThanDays }),
};

export const computeApi = {
  task: (taskId: string) => get<ComputeTask>(`/api/v1/compute/tasks/${taskId}`),
  cancel: (taskId: string) => post<void>(`/api/v1/compute/tasks/${taskId}/cancel`),
};
