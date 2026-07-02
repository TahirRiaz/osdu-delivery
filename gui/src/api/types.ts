// The control plane API contracts, mirrored by hand from src/SqlFlow.ControlPlane/Api/Contracts.cs and the
// endpoint DTOs. Property names are the camelCase form System.Text.Json emits. All timestamps are UTC and may
// arrive without a timezone suffix; parse them with parseUtc from ../lib/time.

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

// ---- Authentication ---------------------------------------------------------------------------------------------

export interface SessionResponse {
  accessToken: string;
  tokenType: string;
  expiresIn: number;
  subject: string;
  role: string;
  scopes: string[];
}

export interface TokenResponse {
  accessToken: string;
  tokenType: string;
  expiresIn: number;
}

export interface EntraProviderInfo {
  enabled: boolean;
  tenantId: string | null;
  clientId: string | null;
  authority: string | null;
}

export interface AuthProviders {
  local: boolean;
  bootstrap: boolean;
  entra: EntraProviderInfo;
}

// ---- Dashboard ----------------------------------------------------------------------------------------------------

export interface RunCounts {
  queued: number;
  running: number;
  succeeded: number;
  failed: number;
  cancelled: number;
  last24h: number;
}

export interface Dashboard {
  repos: number;
  pipelines: number;
  activePipelines: number;
  runs: RunCounts;
  nodesOnline: number;
  nodesTotal: number;
  schedulesEnabled: number;
  schedulesPaused: number;
  repoSources: number;
  repoSourcesWithErrors: number;
  asOfUtc: string;
}

// ---- Catalog --------------------------------------------------------------------------------------------------------

export interface Repo {
  id: string;
  name: string;
  remoteUrl: string | null;
  rootPath: string | null;
  firstSeenUtc: string;
  lastSyncUtc: string;
}

export interface PipelineSummary {
  id: string;
  repoId: string;
  name: string;
  kind: string;
  batch: string | null;
  wave: number;
  active: boolean;
  sourceServer: string | null;
  targetServer: string | null;
  relativePath: string;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

export interface PipelineDetail extends PipelineSummary {
  contentHash: string;
  yaml: string;
  definitionJson: string;
}

/** One resolved column of a pipeline's pre-ingestion transformation view: "declared" rows come from the flow
 * YAML (the source of truth), "detected" rows from the latest run's generated view. */
export interface PipelineColumn {
  kind: "declared" | "detected";
  ordinal: number;
  columnName: string;
  sourceColumn: string | null;
  expression: string | null;
  dataType: string | null;
  sortOrder: number | null;
  isVirtual: boolean;
  excludeFromView: boolean;
  converted: boolean;
}

// ---- Runs -----------------------------------------------------------------------------------------------------------

export type RunStatus = "queued" | "running" | "succeeded" | "failed" | "cancelled";

export interface RunSummary {
  runId: string;
  pipelineId: string;
  repoId: string | null;
  flowName: string;
  flowKind: string;
  /** The batch label joined from the pipeline row; a flow with no YAML batch reports under "default". */
  batch: string;
  /** The lineage execution step (wave) joined from the pipeline row; -1 until lineage has been computed. */
  wave: number;
  status: RunStatus;
  success: boolean;
  targetPool: string | null;
  commitSha: string | null;
  writtenUtc: string;
  enqueuedUtc: string | null;
  durationSeconds: number | null;
  rowsLoaded: number | null;
  rowsInserted: number | null;
  rowsUpdated: number | null;
  rowsDeleted: number | null;
}

export interface RunDetail extends RunSummary {
  claimedByNode: string | null;
  schemaVersion: number;
  startUtc: string | null;
  endUtc: string | null;
  error: string | null;
  host: string | null;
  fullLoad: boolean;
  backfillFrom: string | null;
  backfillTo: string | null;
  filePattern: string | null;
}

export interface RunFile {
  id: number;
  runId: string;
  repoId: string | null;
  name: string;
  path: string | null;
  rows: number;
  columns: number;
  sizeBytes: number;
}

export interface RunAssertion {
  id: number;
  runId: string;
  repoId: string | null;
  name: string;
  result: string;
  assertedValue: string;
  evaluated: boolean;
  error: string | null;
}

export interface RunStatement {
  id: number;
  runId: string;
  repoId: string | null;
  ordinal: number;
  step: string;
  sql: string;
}

export interface RunSurrogateKey {
  id: number;
  runId: string;
  repoId: string | null;
  surrogateKeyId: number;
  surrogateTable: string;
  surrogateColumn: string;
  isRemote: boolean;
  keysGenerated: number;
  rowsStamped: number;
  executed: boolean;
  error: string | null;
}

export interface RunHealthCheckMetric {
  id: number;
  runId: string;
  repoId: string | null;
  name: string;
  seriesPoints: number;
  imputedPoints: number;
  immaturePoints: number;
  anomalies: number;
  levelShifts: number;
  modelTrained: boolean;
  modelTrainer: string | null;
  error: string | null;
}

export interface RunTriggerRequest {
  repoId: string;
  flowName: string;
  pool?: string | null;
  commitSha?: string | null;
  // The built-in backfill: per-run substitution parameters, all optional and audited on the run.
  fullLoad?: boolean;
  backfillFrom?: string | null;
  backfillTo?: string | null;
  filePattern?: string | null;
}

export interface RunTriggerAccepted {
  runId: string;
  status: string;
}

// ---- Schedules -------------------------------------------------------------------------------------------------------

export interface Schedule {
  id: string;
  repoId: string;
  pipelineId: string;
  flowName: string;
  cron: string | null;
  intervalSeconds: number | null;
  timezone: string;
  enabled: boolean;
  paused: boolean;
  source: string;
  nextFireUtc: string | null;
  lastFireUtc: string | null;
  lastRunId: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface CreateScheduleRequest {
  repoId: string;
  flowName: string;
  cron?: string | null;
  intervalSeconds?: number | null;
  timezone?: string | null;
  enabled?: boolean | null;
}

export interface ScheduleCreated {
  id: string;
  nextFireUtc: string | null;
}

// ---- Nodes ------------------------------------------------------------------------------------------------------------

export interface Node {
  name: string;
  firstSeenUtc: string;
  lastSeenUtc: string;
  version: string | null;
  online: boolean;
}

// ---- Repo sources -----------------------------------------------------------------------------------------------------

export interface RepoSource {
  id: string;
  name: string;
  remoteUrl: string;
  branch: string;
  enabled: boolean;
  syncIntervalSeconds: number;
  nextSyncUtc: string | null;
  lastSyncUtc: string | null;
  lastSyncedSha: string | null;
  lastError: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface RegisterRepoSourceRequest {
  name: string;
  remoteUrl: string;
  branch?: string | null;
  syncIntervalSeconds?: number | null;
  enabled?: boolean | null;
}

export interface RepoSourceRegistered {
  id: string;
}

// ---- Lineage -----------------------------------------------------------------------------------------------------------

export interface LineageObject {
  key: string;
  serverRef: string;
  database: string | null;
  schema: string | null;
  name: string;
  kind: string;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

export interface LineageObjectDetail extends LineageObject {
  definition: string | null;
}

export interface LineageObjectColumn {
  ordinal: number;
  name: string;
  dataType: string | null;
  nullable: boolean;
}

export interface LineageEdge {
  id: number;
  repoId: string;
  pipelineId: string | null;
  flow: string | null;
  viaModule: string | null;
  relation: string;
  objectKey: string;
  objectName: string;
  tier: string;
}

export interface WavePipeline {
  id: string;
  name: string;
  kind: string;
}

export interface Wave {
  wave: number;
  pipelines: WavePipeline[];
}

export interface FlowDependency {
  id: number;
  repoId: string;
  fromFlow: string;
  toFlow: string;
  fromPipelineId: string;
  toPipelineId: string;
  viaObjects: string;
}

// ---- Search -------------------------------------------------------------------------------------------------------------

export interface ObjectHit {
  key: string;
  name: string;
  kind: string;
  serverRef: string;
  database: string | null;
  schema: string | null;
}

export interface ColumnHit {
  objectKey: string;
  objectName: string;
  columnName: string;
  dataType: string | null;
  nullable: boolean;
}

export interface DefinitionHit {
  key: string;
  name: string;
  kind: string;
  snippet: string;
}

// ---- Users ---------------------------------------------------------------------------------------------------------------

export interface User {
  id: string;
  username: string;
  email: string | null;
  displayName: string | null;
  role: string;
  provider: string;
  active: boolean;
  createdUtc: string;
  updatedUtc: string;
  lastLoginUtc: string | null;
}

export interface Role {
  name: string;
  scopes: string;
  description: string;
}

export interface CreateUserRequest {
  username: string;
  password: string;
  role: string;
  email?: string | null;
  displayName?: string | null;
}
