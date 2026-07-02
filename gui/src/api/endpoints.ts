// Thin, typed wrappers over the control plane's /api/v1 surface: one function per endpoint, nothing else.
// Auth, error shaping, and rate-limit handling live in client.ts; pages compose these with TanStack Query.

import { del, get, getAnonymous, post, postAnonymous, type QueryParams } from "./client";
import type {
  AuthProviders, ColumnHit, CreateScheduleRequest, CreateUserRequest, Dashboard, DefinitionHit, FlowDependency,
  LineageEdge, LineageObject, LineageObjectColumn, LineageObjectDetail, Node, ObjectHit, PagedResult,
  PipelineDetail, PipelineSummary, RegisterRepoSourceRequest, Repo, RepoSource, RepoSourceRegistered, Role,
  RunAssertion, RunDetail, RunFile, RunHealthCheckMetric, RunStatement, RunSummary, RunSurrogateKey,
  RunTriggerAccepted, RunTriggerRequest, Schedule, ScheduleCreated, SessionResponse, TokenResponse, User, Wave,
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
};

// ---- Dashboard ------------------------------------------------------------------------------------------------------

export const summaryApi = {
  get: () => get<Dashboard>("/api/v1/summary"),
};

// ---- Repos and pipelines ----------------------------------------------------------------------------------------------

export const repoApi = {
  list: (query: PageQuery = {}) => get<PagedResult<Repo>>("/api/v1/repos", query as QueryParams),
  getById: (id: string) => get<Repo>(`/api/v1/repos/${id}`),
};

export interface PipelineListQuery extends PageQuery {
  repoId?: string;
  kind?: string;
  active?: boolean;
  name?: string;
}

export const pipelineApi = {
  list: (query: PipelineListQuery = {}) => get<PagedResult<PipelineSummary>>("/api/v1/pipelines", query as QueryParams),
  getById: (id: string) => get<PipelineDetail>(`/api/v1/pipelines/${id}`),
};

// ---- Runs ----------------------------------------------------------------------------------------------------------------

export interface RunListQuery extends PageQuery {
  repoId?: string;
  pipelineId?: string;
  flowKind?: string;
  status?: string;
  success?: boolean;
  flowName?: string;
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
  surrogateKeys: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunSurrogateKey>>(`/api/v1/runs/${runId}/surrogate-keys`, query as QueryParams),
  healthMetrics: (runId: string, query: PageQuery = {}) =>
    get<PagedResult<RunHealthCheckMetric>>(`/api/v1/runs/${runId}/health-metrics`, query as QueryParams),
  trigger: (request: RunTriggerRequest) => post<RunTriggerAccepted>("/api/v1/runs", request),
  cancel: (runId: string) => post<RunTriggerAccepted>(`/api/v1/runs/${runId}/cancel`),
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
  create: (request: CreateScheduleRequest) => post<ScheduleCreated>("/api/v1/schedules", request),
  pause: (id: string) => post<Schedule>(`/api/v1/schedules/${id}/pause`),
  resume: (id: string) => post<Schedule>(`/api/v1/schedules/${id}/resume`),
  remove: (id: string) => del<void>(`/api/v1/schedules/${id}`),
};

// ---- Nodes --------------------------------------------------------------------------------------------------------------------

export const nodeApi = {
  list: (query: PageQuery = {}) => get<PagedResult<Node>>("/api/v1/nodes", query as QueryParams),
};

// ---- Repo sources -------------------------------------------------------------------------------------------------------------

export const repoSourceApi = {
  list: (query: PageQuery = {}) => get<PagedResult<RepoSource>>("/api/v1/repos/sources", query as QueryParams),
  register: (request: RegisterRepoSourceRequest) => post<RepoSourceRegistered>("/api/v1/repos/sources", request),
  syncNow: (id: string) => post<RepoSource>(`/api/v1/repos/sources/${id}/sync`),
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
  objectColumns: (key: string, query: PageQuery = {}) =>
    get<PagedResult<LineageObjectColumn>>("/api/v1/lineage/objects/columns", { key, ...query } as QueryParams),
  edges: (repoId: string, query: LineageEdgeQuery = {}) =>
    get<PagedResult<LineageEdge>>(`/api/v1/repos/${repoId}/lineage/edges`, query as QueryParams),
  waves: (repoId: string) => get<Wave[]>(`/api/v1/repos/${repoId}/waves`),
  dependencies: (repoId: string, query: PageQuery = {}) =>
    get<PagedResult<FlowDependency>>(`/api/v1/repos/${repoId}/dependencies`, query as QueryParams),
};

// ---- Search --------------------------------------------------------------------------------------------------------------------

export const searchApi = {
  objects: (name: string, query: PageQuery = {}) =>
    get<PagedResult<ObjectHit>>("/api/v1/search/objects", { name, ...query } as QueryParams),
  columns: (name: string, query: PageQuery = {}) =>
    get<PagedResult<ColumnHit>>("/api/v1/search/columns", { name, ...query } as QueryParams),
  definitions: (q: string, query: PageQuery = {}) =>
    get<PagedResult<DefinitionHit>>("/api/v1/search/definitions", { q, ...query } as QueryParams),
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
