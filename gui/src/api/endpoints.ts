// Thin, typed wrappers over the control plane's /api/v1 surface: one function per endpoint, nothing else.
// Auth, error shaping, and rate-limit handling live in client.ts; pages compose these with TanStack Query.

import { del, get, getAnonymous, getText, post, postAnonymous, put, streamSse, type QueryParams, type SseFrame } from "./client";
import type {
  AccessToken, AllSearchResult, AuthProviders, ColumnHit, ComputeTask, ComputeTaskAccepted, ComputeTaskRequest,
  ComputeTaskSummary, CreateAccessTokenRequest, CreateNotificationSubscriptionRequest, CreateScheduleRequest, CreatedAccessToken,
  CreateUserRequest, Dashboard, Datasource, DefinitionHit, DiscoveredFlow,
  DiscoverRepoRequest, FileHit, FlowDependency, FlowHit,
  FilePipelineMatch,
  LineageEdge, LineageObject, LineageObjectColumn, LineageObjectDetail, LineageProject, MyNotificationOptions, Node, NodeScript,
  ProjectGraph,
  NotificationDelivery, NotificationSubscription, NotificationTestSend, ObjectHit, ObjectRepo, PagedResult,
  FlowParameters,
  PipelineColumn, PipelineDetail, PipelineFile, PipelineSummary, RegisterRepoSourceRequest, Repo, RepoSource, RepoSourceRegistered, RepoSyncResult, Role,
  RunAssertion, RunDetail, RunFile, RunGroup, RunHealthCheckMetric, RunScope, RunScopePreview, RunStatement, RunTraceEntry,
  RunSummary, RunSurrogateKey,
  RunTriggerAccepted, RunTriggerRequest, Schedule, ScheduleCreated, SchedulePlan, ScheduleRunAccepted, SessionResponse, TokenResponse,
  UpdateNotificationSubscriptionRequest, User, Wave, WorkerPool, WorkerPoolScaleRequest,
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
};

// ---- Dashboard ------------------------------------------------------------------------------------------------------

export const summaryApi = {
  get: () => get<Dashboard>("/api/v1/summary"),
};

// ---- Repos and pipelines ----------------------------------------------------------------------------------------------

export const repoApi = {
  list: (query: PageQuery = {}) => get<PagedResult<Repo>>("/api/v1/repos", query as QueryParams),
  getById: (id: string) => get<Repo>(`/api/v1/repos/${id}`),
  // Re-sync a CLI/local-path repo from its recorded root path (connected: reads the live database for the derived
  // lineage tier). Refused server-side for a git-source-managed repo, which syncs via its source instead.
  syncLocal: (id: string) => post<RepoSyncResult>(`/api/v1/repos/${id}/sync`),
};

export interface PipelineListQuery extends PageQuery {
  repoId?: string;
  kind?: string;
  active?: boolean;
  name?: string;
  /** Root folder within the repo (the first path segment, or "(root)"); narrows the list to one source. */
  project?: string;
}

export const pipelineApi = {
  list: (query: PipelineListQuery = {}) => get<PagedResult<PipelineSummary>>("/api/v1/pipelines", query as QueryParams),
  // The distinct projects (repo-root folders) for the list's project filter, optionally scoped to one repo.
  projects: (repoId?: string) =>
    get<string[]>("/api/v1/pipelines/projects", repoId ? { repoId } : {}),
  getById: (id: string) => get<PipelineDetail>(`/api/v1/pipelines/${id}`),
  /** The run parameters that apply to this flow (kind + definition driven), for the dynamic trigger form. */
  parameters: (id: string) => get<FlowParameters>(`/api/v1/pipelines/${id}/parameters`),
  columns: (id: string) => get<PipelineColumn[]>(`/api/v1/pipelines/${id}/columns`),
  files: (id: string, query: PipelineFilesQuery = {}) =>
    get<PagedResult<PipelineFile>>(`/api/v1/pipelines/${id}/files`, query as QueryParams),
};

export interface PipelineFilesQuery extends PageQuery {
  search?: string;
}

// ---- Runs ----------------------------------------------------------------------------------------------------------------

export interface RunListQuery extends PageQuery {
  repoId?: string;
  pipelineId?: string;
  flowKind?: string;
  status?: string;
  success?: boolean;
  flowName?: string;
  batch?: string;
  /** Keep only this run group's members (the group view), returned in wave order. */
  groupId?: string;
  /** Keep only each pipeline's newest run (the batch status board); status filters apply to that latest run. */
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
  batch?: string;
}

export const runApi = {
  list: (query: RunListQuery = {}) => get<PagedResult<RunSummary>>("/api/v1/runs", query as QueryParams),
  getById: (runId: string) => get<RunDetail>(`/api/v1/runs/${runId}`),
  files: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunFile>>(`/api/v1/runs/${runId}/files`, query as QueryParams),
  assertions: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunAssertion>>(`/api/v1/runs/${runId}/assertions`, query as QueryParams),
  statements: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunStatement>>(`/api/v1/runs/${runId}/statements`, query as QueryParams),
  trace: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunTraceEntry>>(`/api/v1/runs/${runId}/trace`, query as QueryParams),
  /** The whole trace rendered as one plain-text document (the Copy-trace surface; also handy for tickets). */
  traceText: (runId: string) => getText(`/api/v1/runs/${runId}/trace/text`),
  /** The live trace as SSE: `entry` frames while the run executes, one `end` frame at its terminal status.
   * The cursors resume a dropped connection without replaying entries the caller already holds. */
  streamTrace: (
    runId: string,
    cursors: { afterEventId?: number; afterStatementId?: number },
    onFrame: (frame: SseFrame) => void,
    signal: AbortSignal,
    onOpen?: () => void,
  ) => streamSse(`/api/v1/runs/${runId}/trace/stream`, cursors, onFrame, signal, onOpen),
  surrogateKeys: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunSurrogateKey>>(`/api/v1/runs/${runId}/surrogate-keys`, query as QueryParams),
  healthMetrics: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunHealthCheckMetric>>(`/api/v1/runs/${runId}/health-metrics`, query as QueryParams),
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

// ---- Schedules ---------------------------------------------------------------------------------------------------------------

export interface ScheduleListQuery extends PageQuery {
  repoId?: string;
  pipelineId?: string;
  source?: string;
  enabled?: boolean;
}

export const scheduleApi = {
  list: (query: ScheduleListQuery = {}) => get<PagedResult<Schedule>>("/api/v1/schedules", query as QueryParams),
  getById: (id: string) => get<Schedule>(`/api/v1/schedules/${id}`),
  // The cadence plus the flows a fire runs, in wave order, from the same expander the fire uses: the pre-flight
  // run board reads this to show "what runs, in which order" before Start is pressed.
  plan: (id: string) => get<SchedulePlan>(`/api/v1/schedules/${id}/plan`),
  create: (request: CreateScheduleRequest) => post<ScheduleCreated>("/api/v1/schedules", request),
  // Fire the schedule now, on demand (to test it): enqueues a run of its flow without moving the next scheduled fire.
  runNow: (id: string) => post<ScheduleRunAccepted>(`/api/v1/schedules/${id}/run`),
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
};

// ---- Datasources and ad-hoc compute ---------------------------------------------------------------------------------

export interface ComputeTaskListQuery extends PageQuery {
  status?: string;
  reference?: string;
  operation?: string;
}

export const datasourceApi = {
  list: () => get<Datasource[]>("/api/v1/datasources"),
  tasks: (query: ComputeTaskListQuery = {}) =>
    get<PagedResult<ComputeTaskSummary>>("/api/v1/datasources/tasks", query as QueryParams),
  /** One task; waitMs long-polls the server (capped at 20s) so a result arrives in one round trip. */
  task: (taskId: string, waitMs?: number, signal?: AbortSignal) =>
    get<ComputeTask>(`/api/v1/datasources/tasks/${taskId}`, { waitMs }, signal),
  createTask: (request: ComputeTaskRequest) => post<ComputeTaskAccepted>("/api/v1/datasources/tasks", request),
  cancelTask: (taskId: string) => post<ComputeTaskAccepted>(`/api/v1/datasources/tasks/${taskId}/cancel`),
};

/**
 * Runs one compute task end to end: enqueue, then long-poll until it reaches a terminal state. Resolves with
 * the terminal task (the caller inspects status/result/error); rejects only on transport/auth errors or when
 * the signal aborts. The abort signal also best-effort cancels the server-side task so an abandoned browse
 * never keeps a worker busy.
 */
export async function executeComputeTask(request: ComputeTaskRequest, signal?: AbortSignal): Promise<ComputeTask> {
  const accepted = await datasourceApi.createTask(request);
  try {
    for (;;) {
      const task = await datasourceApi.task(accepted.taskId, 10_000, signal);
      if (task.status === "succeeded" || task.status === "failed" || task.status === "cancelled" || task.status === "skipped") {
        return task;
      }
    }
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError") {
      // The caller walked away (unmounted, changed scope): stop the server-side work too, best-effort.
      datasourceApi.cancelTask(accepted.taskId).catch(() => undefined);
    }

    throw error;
  }
}

// ---- Repo sources -------------------------------------------------------------------------------------------------------------

export const repoSourceApi = {
  list: (query: PageQuery = {}) => get<PagedResult<RepoSource>>("/api/v1/repos/sources", query as QueryParams),
  register: (request: RegisterRepoSourceRequest) => post<RepoSourceRegistered>("/api/v1/repos/sources", request),
  syncNow: (id: string) => post<RepoSource>(`/api/v1/repos/sources/${id}/sync`),
  // Preview-first scan: list a repo's flows without importing (nothing reaches the catalog until a sync).
  discover: (request: DiscoverRepoRequest) => post<DiscoveredFlow[]>("/api/v1/repos/discover", request),
};

// ---- Lineage -------------------------------------------------------------------------------------------------------------------

export interface LineageObjectQuery extends PageQuery {
  name?: string;
  serverRef?: string;
  kind?: string;
}

export interface LineageEdgeQuery extends PageQuery {
  pipelineId?: string;
  objectKey?: string;
  relation?: string;
  tier?: string;
}

export const lineageApi = {
  objects: (query: LineageObjectQuery = {}) =>
    get<PagedResult<LineageObject>>("/api/v1/lineage/objects", query as QueryParams),
  objectDetail: (key: string) => get<LineageObjectDetail>("/api/v1/lineage/objects/detail", { key }),
  script: (key: string) => get<NodeScript>("/api/v1/lineage/script", { key }),
  objectColumns: (key: string, query: PageQuery = {}) =>
    get<PagedResult<LineageObjectColumn>>("/api/v1/lineage/objects/columns", { key, ...query } as QueryParams),
  objectRepos: (key: string) => get<ObjectRepo[]>("/api/v1/lineage/objects/repos", { key }),
  filePipelines: (file: string) => get<FilePipelineMatch[]>("/api/v1/lineage/file-pipelines", { file }),
  edges: (repoId: string, query: LineageEdgeQuery = {}) =>
    get<PagedResult<LineageEdge>>(`/api/v1/repos/${repoId}/lineage/edges`, query as QueryParams),
  waves: (repoId: string) => get<Wave[]>(`/api/v1/repos/${repoId}/waves`),
  dependencies: (repoId: string, query: PageQuery = {}) =>
    get<PagedResult<FlowDependency>>(`/api/v1/repos/${repoId}/dependencies`, query as QueryParams),
  // Every selectable (repo, project) pair the lineage graph can be scoped to, for the searchable scope picker.
  projects: () => get<LineageProject[]>("/api/v1/lineage/projects"),
  // A project's cross-repo downstream lineage closure. `expand` re-seeds the walk from frontier nodes (an object
  // key or a pipeline id) and is sent as repeated query params, which the single-value query builder cannot do, so
  // it is folded into the path here; the rest ride the normal query object.
  projectGraph: (params: { repoId?: string; project?: string; depth?: number; expand?: string[] }) => {
    const expand = params.expand ?? [];
    const suffix = expand.length > 0
      ? `?${expand.map((token) => `expand=${encodeURIComponent(token)}`).join("&")}`
      : "";
    return get<ProjectGraph>(
      `/api/v1/lineage/project-graph${suffix}`,
      { repoId: params.repoId, project: params.project, depth: params.depth } as QueryParams,
    );
  },
};

// ---- Search --------------------------------------------------------------------------------------------------------------------

export const searchApi = {
  all: (q: string) => get<AllSearchResult>("/api/v1/search/all", { q }),
  objects: (name: string, query: PageQuery = {}) =>
    get<PagedResult<ObjectHit>>("/api/v1/search/objects", { name, ...query } as QueryParams),
  columns: (name: string, query: PageQuery = {}) =>
    get<PagedResult<ColumnHit>>("/api/v1/search/columns", { name, ...query } as QueryParams),
  definitions: (q: string, query: PageQuery = {}) =>
    get<PagedResult<DefinitionHit>>("/api/v1/search/definitions", { q, ...query } as QueryParams),
  files: (name: string, query: PageQuery = {}) =>
    get<PagedResult<FileHit>>("/api/v1/search/files", { name, ...query } as QueryParams),
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
  testSubscription: (id: string) => post<NotificationTestSend>(`/api/v1/me/notifications/subscriptions/${id}/test`),
  listDeliveries: (take = 50) => get<NotificationDelivery[]>("/api/v1/me/notifications/deliveries", { take }),
};
