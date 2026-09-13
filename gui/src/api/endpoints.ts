// Thin, typed wrappers over the control plane's /api/v1 surface: one function per endpoint, nothing else.
// Auth, error shaping, and rate-limit handling live in client.ts; pages compose these with TanStack Query.

import { del, get, getAnonymous, getText, post, postAnonymous, put, streamSse, type QueryParams, type SseFrame } from "./client";
import type {
  AccessToken, AllSearchResult, AuthProviders,
  CreateAccessTokenRequest, CreateNotificationSubscriptionRequest, CreateScheduleRequest, CreatedAccessToken,
  CreateUserRequest, Dashboard, DiscoveredFlow, DiscoverRepoRequest, FlowHit,
  GenerateNotificationDigestRequest, Identity, MyNotificationOptions, Node, NodePurgeResult,
  NotificationDelivery, NotificationDigest, NotificationDigestSummary, NotificationQueuedDelivery,
  NotificationSubscription, PagedResult, PipelineBatch, PipelineDetail, PipelineSummary,
  ProposalCreated, ProposeFilesRequest,
  RegisterRepoSourceRequest, Repo, RepoDeletionResult, RepoSource, RepoSourceRegistered, RepoSyncResult, RepoTree,
  Role, RunDetail, RunEventPurgeResult, RunGroup, RunScope, RunScopePreview, RunSummary,
  RunTraceEntry, RunTraceRetention, RunTraceRetentionUpdate, RunTraceStorage, RunTriggerAccepted,
  RunTriggerRequest, Schedule, ScheduleCreated, ScheduleDefinition, SchedulePlan, ScheduleRunAccepted,
  SendNotificationDigestRequest, SessionResponse, TokenResponse, UpdateNotificationSubscriptionRequest,
  UpdateUserProfileRequest, User, WorkerPool, WorkerPoolScaleRequest,
} from "./types";

export interface PageQuery {
  page?: number;
  pageSize?: number;
}

// ---- Authentication ------------------------------------------------------------------------------------------------

export const authApi = {
  providers: () => getAnonymous<AuthProviders>("/api/v1/auth/providers"),
  login: (username: string, password: string) =>
    postAnonymous<SessionResponse>("/api/v1/auth/login", { username, password }),
  exchange: (token: string) => postAnonymous<SessionResponse>("/api/v1/auth/exchange", { token }),
  bootstrapToken: (secret: string, scopes: string[]) =>
    postAnonymous<TokenResponse>("/api/v1/auth/token", { secret, subject: null, scopes }),
  /** Trade the live session token for a fresh one; authenticated by the token it replaces. AuthContext drives this
   * on a timer so a working session never lapses under the user. Answers 401/403 once the session may no longer
   * roll (past the absolute cap, account deactivated, or a credential that does not renew at all). */
  renew: () => post<SessionResponse>("/api/v1/auth/renew"),
  /** Who the current credential is: subject, role, scopes, and the user id when it is a user's session. */
  me: () => get<Identity>("/api/v1/me"),
  /** Approve or deny a CLI device-code sign-in from the signed-in browser session. */
  approveDevice: (userCode: string) => post<void>("/api/v1/auth/device/approve", { userCode }),
  denyDevice: (userCode: string) => post<void>("/api/v1/auth/device/deny", { userCode }),
};

// ---- Dashboard ------------------------------------------------------------------------------------------------------

export const summaryApi = {
  get: () => get<Dashboard>("/api/v1/summary"),
};

// ---- Repos and pipelines ----------------------------------------------------------------------------------------------

export const repoApi = {
  list: (query: PageQuery = {}) => get<PagedResult<Repo>>("/api/v1/repos", query as QueryParams),
  getById: (id: string) => get<Repo>(`/api/v1/repos/${id}`),
  // Everything the repository holds on its synced branch, not only the flows the catalog imported: the folder outline
  // the repo view lists its projects from. 400s for a repo with no git source and no reachable root path.
  tree: (id: string) => get<RepoTree>(`/api/v1/repos/${id}/tree`),
  // Re-sync a CLI/local-path repo from its recorded root path. Refused server-side for a git-source-managed repo,
  // which syncs via its source instead.
  syncLocal: (id: string) => post<RepoSyncResult>(`/api/v1/repos/${id}/sync`),
  // Delete a repo and everything attributed to it (pipelines, runs, run groups, schedules), plus any managed git
  // source registered under the same name so the background sync cannot recreate it. Irreversible; returns the
  // counts removed.
  delete: (id: string) => del<RepoDeletionResult>(`/api/v1/repos/${id}`),
};

export interface PipelineListQuery extends PageQuery {
  repoId?: string;
  kind?: string;
  active?: boolean;
  name?: string;
  /** Root folder within the repo (the first path segment, or "(root)"); narrows the list to one source. */
  project?: string;
  /** Batch (grouping) label; "default" also matches flows that declare no batch. */
  batch?: string;
}

export const pipelineApi = {
  list: (query: PipelineListQuery = {}) => get<PagedResult<PipelineSummary>>("/api/v1/pipelines", query as QueryParams),
  // The distinct projects (repo-root folders) for the list's project filter, optionally scoped to one repo.
  projects: (repoId?: string) =>
    get<string[]>("/api/v1/pipelines/projects", repoId ? { repoId } : {}),
  // The distinct batches (grouping labels) with flow counts, optionally scoped to one repo; a flow that declares
  // no batch is coalesced into the "default" batch.
  batches: (repoId?: string) =>
    get<PipelineBatch[]>("/api/v1/pipelines/batches", repoId ? { repoId } : {}),
  getById: (id: string) => get<PipelineDetail>(`/api/v1/pipelines/${id}`),
  /** The trace of the pipeline's newest run, without having to look the run up first. */
  latestTrace: (id: string, query: PageQuery = {}) =>
    get<PagedResult<RunTraceEntry>>(`/api/v1/pipelines/${id}/trace`, query as QueryParams),
  latestTraceText: (id: string) => getText(`/api/v1/pipelines/${id}/trace/text`),
};

// ---- Runs ----------------------------------------------------------------------------------------------------------------

export interface RunListQuery extends PageQuery {
  repoId?: string;
  pipelineId?: string;
  flowKind?: string;
  status?: string;
  success?: boolean;
  flowName?: string;
  /** Exact batch (grouping) label; the board's batch dropdown supplies a real label. */
  batch?: string;
  /** Keep only the runs of this schedule's member flows (the board's schedule dropdown), by membership. */
  scheduleId?: string;
  /** Keep only this run group's members (the group view), returned in wave order. */
  groupId?: string;
  /** Keep only each pipeline's newest run (the status board); status filters apply to that latest run. */
  latest?: boolean;
  /** Inclusive lower bound on WrittenUtc (ISO 8601, UTC): the schedules timeline reads runs by day. */
  from?: string;
  /** Inclusive upper bound on WrittenUtc (ISO 8601, UTC): the schedules timeline reads runs by day. */
  to?: string;
}

export interface RunScopePreviewQuery {
  repoId: string;
  flowName?: string;
  scope: RunScope;
}

export const runApi = {
  list: (query: RunListQuery = {}) => get<PagedResult<RunSummary>>("/api/v1/runs", query as QueryParams),
  getById: (runId: string) => get<RunDetail>(`/api/v1/runs/${runId}`),
  trace: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunTraceEntry>>(`/api/v1/runs/${runId}/trace`, query as QueryParams),
  /** The whole trace rendered as one plain-text document (the Copy-trace surface; also handy for tickets). */
  traceText: (runId: string) => getText(`/api/v1/runs/${runId}/trace/text`),
  /** The live trace as SSE: `entry` frames while the run executes, one `end` frame at its terminal status.
   * The cursor resumes a dropped connection without replaying entries the caller already holds. */
  streamTrace: (
    runId: string,
    cursors: { afterEventId?: number },
    onFrame: (frame: SseFrame) => void,
    signal: AbortSignal,
    onOpen?: () => void,
  ) => streamSse(`/api/v1/runs/${runId}/trace/stream`, cursors, onFrame, signal, onOpen),
  trigger: (request: RunTriggerRequest) => post<RunTriggerAccepted>("/api/v1/runs", request),
  cancel: (runId: string) => post<RunTriggerAccepted>(`/api/v1/runs/${runId}/cancel`),
  previewScope: (query: RunScopePreviewQuery) =>
    get<RunScopePreview>("/api/v1/runs/preview", query as unknown as QueryParams),
  group: (groupId: string) => get<RunGroup>(`/api/v1/runs/groups/${groupId}`),
  /** The live run group as SSE: a `member` frame (a RunSummary) whenever a member's summary changes (status,
   * last action, rows, timing), a full snapshot on connect, one `end` frame once every member is terminal. */
  streamGroup: (groupId: string, onFrame: (frame: SseFrame) => void, signal: AbortSignal, onOpen?: () => void) =>
    streamSse(`/api/v1/runs/groups/${groupId}/stream`, undefined, onFrame, signal, onOpen),
  cancelGroup: (groupId: string) => post<RunTriggerAccepted>(`/api/v1/runs/groups/${groupId}/cancel`),
};

// ---- Activity trace (generic operation trace: repo sync, ...) ------------------------------------------------------

export const activityApi = {
  /** The live trace of one operation as SSE: `entry` frames per new log line for the given (kind, subject), then a
   * single `end` frame once the newest line is terminal (or "idle" when the subject has no trace yet). The `afterId`
   * cursor resumes a dropped connection without replaying lines the caller already holds. One stream serves every
   * operation kind, so the bottom trace panel tails a sync and any other activity through the same call. */
  streamTrace: (
    kind: string,
    subject: string,
    cursor: { afterId?: number },
    onFrame: (frame: SseFrame) => void,
    signal: AbortSignal,
    onOpen?: () => void,
  ) => streamSse("/api/v1/activities/stream", { kind, subject, ...cursor }, onFrame, signal, onOpen),
};

// ---- Schedules ---------------------------------------------------------------------------------------------------------------

export interface ScheduleListQuery extends PageQuery {
  repoId?: string;
  pipelineId?: string;
  source?: string;
  enabled?: boolean;
  /** Free-text substring of the schedule name, matched server-side so it spans every page, not just the one shown. */
  search?: string;
}

export const scheduleApi = {
  list: (query: ScheduleListQuery = {}) => get<PagedResult<Schedule>>("/api/v1/schedules", query as QueryParams),
  getById: (id: string) => get<Schedule>(`/api/v1/schedules/${id}`),
  // The cadence plus the flows a fire runs, in wave order, from the same expander the fire uses: the pre-flight
  // run board reads this to show "what runs, in which order" before Start is pressed.
  plan: (id: string) => get<SchedulePlan>(`/api/v1/schedules/${id}/plan`),
  // The YAML that defines the cadence: the declaring flow's document, or the schedules.yaml library file.
  definition: (id: string) => get<ScheduleDefinition>(`/api/v1/schedules/${id}/definition`),
  create: (request: CreateScheduleRequest) => post<ScheduleCreated>("/api/v1/schedules", request),
  // Fire the schedule now, on demand, without moving the next scheduled fire. Optional batches narrow the fire to
  // members carrying those batch: tags; repeated as ?batch=a&batch=b. An empty/omitted list runs every member.
  // A filter can only ever select a subset of the schedule's own members. chain=false keeps the fire to this
  // schedule alone; the schedules chained behind it do not follow. Omitted means the chain runs, matching what a
  // clock-driven fire does.
  runNow: (id: string, batches?: string[], chain?: boolean) => {
    const params = new URLSearchParams();
    for (const b of batches ?? []) {
      params.append("batch", b);
    }
    if (chain === false) {
      params.set("chain", "false");
    }
    const query = params.toString() === "" ? "" : `?${params.toString()}`;
    return post<ScheduleRunAccepted>(`/api/v1/schedules/${id}/run${query}`);
  },
  pause: (id: string) => post<Schedule>(`/api/v1/schedules/${id}/pause`),
  resume: (id: string) => post<Schedule>(`/api/v1/schedules/${id}/resume`),
  remove: (id: string) => del<void>(`/api/v1/schedules/${id}`),
};

// ---- Nodes --------------------------------------------------------------------------------------------------------------------

export const nodeApi = {
  list: (query: PageQuery = {}) => get<PagedResult<Node>>("/api/v1/nodes", query as QueryParams),
  /** Every worker pool's desired state and resolved replica target. */
  listPools: () => get<WorkerPool[]>("/api/v1/nodes/pools"),
  /** Set a pool's always-on floor and/or bounded manual override; returns the pool's new resolved state. */
  scalePool: (request: WorkerPoolScaleRequest) => put<WorkerPool>("/api/v1/nodes/pools/scale", request),
  /** Ask a node to drain and restart; the orchestrator recreates it. */
  restart: (name: string) => post<void>(`/api/v1/nodes/${encodeURIComponent(name)}/restart`),
  /** Remove a node from the fleet registry (a dead entry); a live node re-registers on its next heartbeat. */
  delete: (name: string) => del<void>(`/api/v1/nodes/${encodeURIComponent(name)}`),
  /** Drop every offline node from the fleet registry at once; returns how many entries were removed. */
  purgeOffline: () => del<NodePurgeResult>("/api/v1/nodes/offline"),
};

// ---- Repo sources -------------------------------------------------------------------------------------------------------------

export const repoSourceApi = {
  list: (query: PageQuery = {}) => get<PagedResult<RepoSource>>("/api/v1/repos/sources", query as QueryParams),
  register: (request: RegisterRepoSourceRequest) => post<RepoSourceRegistered>("/api/v1/repos/sources", request),
  syncNow: (id: string) => post<RepoSource>(`/api/v1/repos/sources/${id}/sync`),
  // Preview-first scan: list a repo's flows without importing (nothing reaches the catalog until a sync).
  discover: (request: DiscoverRepoRequest) => post<DiscoveredFlow[]>("/api/v1/repos/discover", request),
  // Remove a source-only registration (one registered but not yet synced, so no repo exists to delete through).
  // Once a sync has produced the repo, deleting the repo removes the source instead.
  remove: (id: string) => del<void>(`/api/v1/repos/sources/${id}`),
  // Propose files to the source's repository as a pull request: the control plane pushes a branch with the source's own
  // credential and opens the pull request against the tracked branch. Nothing reaches the catalog until it merges and syncs.
  propose: (id: string, request: ProposeFilesRequest) =>
    post<ProposalCreated>(`/api/v1/repos/sources/${id}/proposals`, request),
};

// ---- Search --------------------------------------------------------------------------------------------------------------------

export const searchApi = {
  all: (q: string) => get<AllSearchResult>("/api/v1/search/all", { q }),
  flows: (q: string, query: PageQuery = {}) =>
    get<PagedResult<FlowHit>>("/api/v1/search/flows", { q, ...query } as QueryParams),
};

// ---- Users ---------------------------------------------------------------------------------------------------------------------

export interface UserListQuery extends PageQuery {
  username?: string;
  role?: string;
  provider?: string;
  active?: boolean;
}

export const userApi = {
  list: (query: UserListQuery = {}) => get<PagedResult<User>>("/api/v1/users", query as QueryParams),
  getById: (id: string) => get<User>(`/api/v1/users/${id}`),
  create: (request: CreateUserRequest) => post<User>("/api/v1/users", request),
  updateProfile: (id: string, request: UpdateUserProfileRequest) =>
    post<User>(`/api/v1/users/${id}/profile`, request),
  remove: (id: string) => del<void>(`/api/v1/users/${id}`),
  setRole: (id: string, role: string) => post<User>(`/api/v1/users/${id}/role`, { role }),
  activate: (id: string) => post<User>(`/api/v1/users/${id}/activate`),
  deactivate: (id: string) => post<User>(`/api/v1/users/${id}/deactivate`),
  setPassword: (id: string, password: string) => post<User>(`/api/v1/users/${id}/password`, { password }),
  roles: () => get<Role[]>("/api/v1/roles"),
};

// ---- Personal access tokens (self-service: the caller's own tokens) ----------------------------------------------

export const tokenApi = {
  list: () => get<AccessToken[]>("/api/v1/me/tokens"),
  create: (request: CreateAccessTokenRequest) => post<CreatedAccessToken>("/api/v1/me/tokens", request),
  revoke: (id: string) => del<void>(`/api/v1/me/tokens/${id}`),
};

// ---- Notifications (self-service: the caller's own subscriptions and deliveries) ---------------------------------

export const notificationApi = {
  options: () => get<MyNotificationOptions>("/api/v1/me/notifications/options"),
  listSubscriptions: () => get<NotificationSubscription[]>("/api/v1/me/notifications/subscriptions"),
  createSubscription: (request: CreateNotificationSubscriptionRequest) =>
    post<NotificationSubscription>("/api/v1/me/notifications/subscriptions", request),
  updateSubscription: (id: string, request: UpdateNotificationSubscriptionRequest) =>
    put<NotificationSubscription>(`/api/v1/me/notifications/subscriptions/${id}`, request),
  deleteSubscription: (id: string) => del<void>(`/api/v1/me/notifications/subscriptions/${id}`),
  testSubscription: (id: string) =>
    post<NotificationQueuedDelivery>(`/api/v1/me/notifications/subscriptions/${id}/test`),
  listDeliveries: (take = 50) => get<NotificationDelivery[]>("/api/v1/me/notifications/deliveries", { take }),
  // Estate digests are not per-user: one record of what the estate did in each window, readable by anyone.
  listDigests: (take = 50) => get<NotificationDigestSummary[]>("/api/v1/notifications/digests", { take }),
  getDigest: (id: string) => get<NotificationDigest>(`/api/v1/notifications/digests/${id}`),
  generateDigest: (request: GenerateNotificationDigestRequest) =>
    post<NotificationDigest>("/api/v1/notifications/digests", request),
  sendDigest: (id: string, request: SendNotificationDigestRequest) =>
    post<NotificationQueuedDelivery>(`/api/v1/notifications/digests/${id}/send`, request),
};

// ---- Maintenance (run-trace storage retention) ------------------------------------------------------------------

export const maintenanceApi = {
  traceStorage: () => get<RunTraceStorage>("/api/v1/maintenance/trace-storage"),
  /** Set how many days to keep the run events of finished runs, or null to keep them forever (age-based pruning off). */
  setTraceRetention: (retentionDays: number | null) =>
    put<RunTraceRetention>("/api/v1/maintenance/trace-retention", { retentionDays } satisfies RunTraceRetentionUpdate),
  /** Prune the run events past the retention window now and return how many rows were deleted. */
  pruneEvents: () => post<RunEventPurgeResult>("/api/v1/maintenance/events/prune"),
  /** Delete every run event now and return how many rows were deleted; the deliberate escape hatch. */
  purgeEvents: () => post<RunEventPurgeResult>("/api/v1/maintenance/events/purge"),
};
