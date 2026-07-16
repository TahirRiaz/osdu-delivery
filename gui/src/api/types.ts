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

/** The outcome of a manual local-path repo sync: the pipeline reconciliation counts and lineage tallies, whether the
 * derived tier connected to the live database, and any warnings the pass surfaced (bounded). */
export interface RepoSyncResult {
  pipelinesAdded: number;
  pipelinesUpdated: number;
  pipelinesUnchanged: number;
  pipelinesDeactivated: number;
  pipelinesDeleted: number;
  objects: number;
  columns: number;
  edges: number;
  waves: number;
  dependencies: number;
  connected: boolean;
  warnings: string[];
}

export interface PipelineSummary {
  id: string;
  repoId: string;
  name: string;
  kind: string;
  batch: string | null;
  wave: number;
  active: boolean;
  /** "auto" (default: schedules and group runs execute it) or "manual" (the flow's YAML mode: it runs only
   * when triggered directly; the scheduler and batch/node expansion skip it). */
  executionMode: "auto" | "manual";
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

export type RunStatus = "queued" | "running" | "succeeded" | "failed" | "cancelled" | "skipped";

/** How a run was scoped: one flow, a flow and its descendants (Node), or a whole batch / data source. */
export type RunScope = "flow" | "node" | "batch";

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
  /** How many source files this run processed (file flows); 0 for non-file flows. */
  fileCount: number;
  /** The run group this run belongs to when it was launched as one member of a Node or Batch run; null for a
   * standalone single-flow run. */
  groupId: string | null;
  /** The run's newest trace event (a stage summary, a file read, a decision): what the run is doing right now
   * while it executes, what it did last once it ended. Null when no events were recorded (a queued run, or one
   * that predates the event stream). */
  lastAction: string | null;
  lastActionUtc: string | null;
}

export interface RunDetail extends RunSummary {
  claimedByNode: string | null;
  /** When an operator asked to cancel this run while it was already running; null otherwise. While set and the run
   * is still "running", the owning node is aborting the in-flight statement (a transitional "cancelling" state). */
  cancelRequestedUtc: string | null;
  schemaVersion: number;
  startUtc: string | null;
  endUtc: string | null;
  error: string | null;
  host: string | null;
  fullLoad: boolean;
  backfillFrom: string | null;
  backfillTo: string | null;
  filePattern: string | null;
  /** True when this run evaluated the flow's data-quality assertions (manual-mode ones included) against the
   * current target without loading anything (the on-demand assertion run). */
  assertionsOnly: boolean;
  /** The incremental read scope the engine computed and applied this run: mode (full / incremental / backfill /
   * init-load), the filter that bounded the read, and the resolved watermark with the object it was probed from.
   * Null on flows with no incremental surface. Distinct from the operator's backfill parameters above. */
  incrementalMode: string | null;
  incrementalFilter: string | null;
  incrementalWatermark: string | null;
  incrementalWatermarkSource: string | null;
  /** How DataSet_DW was derived for a file run (e.g. "filename dates; month-first (inferred from file set)" or
   * "last-modified"), so the detail view shows what the reader detected. Null for flows with no DataSet_DW. */
  dataSetConvention: string | null;
  /** On a failed run, the exact statement that threw (the run's failure point), so the detail view can show the
   * offending SQL next to the error banner. Null on every non-failed run, or when no statement was attributed. */
  failedStatementOrdinal: number | null;
  failedStatementStep: string | null;
  failedStatementSql: string | null;
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

export interface PipelineFile {
  name: string;
  path: string | null;
  modified: string | null;
  rows: number;
  sizeBytes: number;
  lastRun: boolean;
  lastProcessedUtc: string | null;
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
  /** When the statement was generated (UTC); null on statements recorded before the trace carried timestamps. */
  timestampUtc: string | null;
  step: string;
  sql: string;
  /** The error this statement raised, or null when it succeeded (or was never reached). Set on exactly one
   * statement of a failed run: the one whose execution threw. */
  error: string | null;
}

/** One entry of a run's consolidated trace: a canonical run event (kind "event": file progress, a resolved
 * watermark, an engine decision, a stage summary, a warning) or a generated SQL statement (kind "statement"),
 * the two streams interleaved by timestamp. An event carries message (plus optional rows/elapsedMs); a
 * statement carries sql (plus error on the one that threw). */
export interface RunTraceEntry {
  id: number;
  runId: string;
  repoId: string | null;
  kind: "event" | "statement";
  /** 1-based position within the entry's own stream (events and statements count separately). */
  ordinal: number;
  /** Null only on statements recorded before the trace carried timestamps; those sort first. */
  timestampUtc: string | null;
  level: "trace" | "debug" | "info" | "warning" | "error";
  step: string | null;
  message: string | null;
  sql: string | null;
  error: string | null;
  rows: number | null;
  elapsedMs: number | null;
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

export type RunParameterInput = "Toggle" | "DateRange" | "Glob";

/** One run parameter that applies to a flow, with the metadata the trigger form renders from. `key` maps back to
 *  the trigger request: "fullLoad", "backfillWindow" (backfillFrom/backfillTo), "filePattern", "assertionsOnly". */
export interface RunParameterDescriptor {
  key: string;
  input: RunParameterInput;
  label: string;
  help: string;
}

/** The run parameters that apply to a pipeline, driven by its kind and definition (the same per-kind rules the
 *  engine honors). `parameters` is empty for kinds with no selection surface (stored procedure, health check,
 *  inventory), so the trigger form shows only controls the run will actually honor. */
export interface FlowParameters {
  flowKind: string;
  parameters: RunParameterDescriptor[];
}

export interface RunTriggerRequest {
  repoId: string;
  flowName: string;
  pool?: string | null;
  commitSha?: string | null;
  // The built-in backfill: per-run substitution parameters, all optional and audited on the run. Honored only for
  // a single flow (scope "flow" or omitted); a Node or Batch run always runs its members with default parameters.
  fullLoad?: boolean;
  backfillFrom?: string | null;
  backfillTo?: string | null;
  filePattern?: string | null;
  /** Evaluate the flow's data-quality assertions (manual-mode ones included) against the current target and
   * load nothing. Ingestion flows only; single-flow scope only. */
  assertionsOnly?: boolean;
  /** The execution scope: one flow (default), a flow and its descendants (node), or a whole batch (batch). */
  scope?: RunScope;
  /** The batch label for a batch-scoped run; when omitted the anchor flow's own batch is used. */
  batch?: string | null;
}

/** The trigger response covers both a single flow (runId set) and a Node/Batch group (groupId + memberCount set). */
export interface RunTriggerAccepted {
  runId?: string | null;
  groupId?: string | null;
  memberCount?: number | null;
  status: string;
}

/** One flow a scope expansion would run, with the wave that orders it. */
export interface RunScopePreviewMember {
  flowName: string;
  flowKind: string;
  wave: number;
}

/** What a Node or Batch run would enqueue, without enqueuing: the resolved anchor and the ordered members. */
export interface RunScopePreview {
  scope: RunScope;
  anchor: string;
  memberCount: number;
  waveCount: number;
  members: RunScopePreviewMember[];
}

/** A run group's member counts by lifecycle state. */
export interface RunGroupCounts {
  total: number;
  queued: number;
  running: number;
  succeeded: number;
  failed: number;
  cancelled: number;
  skipped: number;
}

/** One run group's header and the live rollup of its members. The members come from the runs list by groupId. */
export interface RunGroup {
  groupId: string;
  repoId: string;
  mode: "node" | "batch";
  anchor: string;
  memberCount: number;
  commitSha: string | null;
  enqueuedUtc: string;
  counts: RunGroupCounts;
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
  /** Whether missed occurrences are backfilled (one per scheduler tick) instead of skipped. */
  catchup: boolean;
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
  catchup?: boolean | null;
}

export interface ScheduleCreated {
  id: string;
  nextFireUtc: string | null;
}

/** The manual run-now acknowledgement: the id of the run the schedule's flow was enqueued as. */
export interface ScheduleRunAccepted {
  runId: string;
}

// ---- Nodes ------------------------------------------------------------------------------------------------------------

export interface Node {
  name: string;
  firstSeenUtc: string;
  lastSeenUtc: string;
  version: string | null;
  online: boolean;
  /** When set, a restart was requested and is pending until the node observes it on its next heartbeat. */
  restartRequestedUtc: string | null;
}

/** One worker pool's desired compute state and its live resolution. `pool` is the empty string for the default
 *  (untargeted) pool. `replicaTarget` is the count the autoscaler holds: max of queued runs, the always-on floor,
 *  and the manual override while active. */
export interface WorkerPool {
  pool: string;
  minReplicas: number;
  manualReplicas: number;
  manualUntilUtc: string | null;
  manualActive: boolean;
  queuedRuns: number;
  replicaTarget: number;
  /** How many of this pool's workers are online right now; when below replicaTarget, the pool is spinning one up. */
  onlineNodes: number;
  updatedUtc: string | null;
  updatedBy: string | null;
}

/** A change to a pool's desired state. Every field is optional: send `minReplicas` to set/clear the always-on
 *  floor, or `manualReplicas` (with `manualForMinutes`) to bring workers up for a bounded window. */
export interface WorkerPoolScaleRequest {
  pool: string | null;
  minReplicas?: number;
  manualReplicas?: number;
  manualForMinutes?: number;
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
  /** A secret reference (${keyvault:...}/${env:...}) for the git token, never the token itself; null uses the host env. */
  credentialReference: string | null;
  /** The git username paired with the token (for Bitbucket); null uses the token-only placeholder. */
  credentialUsername: string | null;
  /** The repo-relative flow files this source does NOT import (the preview-first selection); empty imports all. */
  excludedFlowPaths: string[];
  createdUtc: string;
  updatedUtc: string;
}

export interface RegisterRepoSourceRequest {
  name: string;
  remoteUrl: string;
  branch?: string | null;
  syncIntervalSeconds?: number | null;
  enabled?: boolean | null;
  credentialReference?: string | null;
  credentialUsername?: string | null;
  /** The flow files to leave out of the import; null or omitted imports every *.flow.yaml. */
  excludedFlowPaths?: string[] | null;
}

/** The body to preview a repo's flows without importing them (a read-only discover). */
export interface DiscoverRepoRequest {
  remoteUrl: string;
  branch?: string | null;
  credentialReference?: string | null;
  credentialUsername?: string | null;
}

/** One *.flow.yaml a discover found, for the selection wizard. */
export interface DiscoveredFlow {
  relativePath: string;
  flowName: string | null;
  kind: string | null;
  sizeBytes: number;
  parseOk: boolean;
  parseError: string | null;
  /** Secret-redacted content for preview; null when the file is too large to inline. */
  content: string | null;
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
  /** The object's depth in the estate-wide data-movement graph (0 = a source nothing produces); null when the
   * object takes part in no data movement. */
  level: number | null;
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

/** The script behind any lineage node in one shape: a pipeline's YAML, a view/procedure's module body, or a
 * table's generated CREATE TABLE. `language` is "yaml" or "sql"; `source` is Authored / Module / a script tier. */
export interface NodeScript {
  key: string;
  kind: string;
  language: string;
  script: string | null;
  source: string | null;
  name: string | null;
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
  /** The object's database and schema from the global registry (proper-cased); null for a file or a
   * partially-resolved identity. */
  objectDatabase: string | null;
  objectSchema: string | null;
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

/** One selectable project for the lineage graph's scope picker: a repo-root folder, the repo it lives in (so the
 * picker can show "repo / project" and disambiguate a folder name shared across repos), and its active-flow count. */
export interface LineageProject {
  repoId: string;
  repoName: string;
  project: string;
  flowCount: number;
}

/** One pipeline node in a project's cross-repo lineage closure. `isSeed` marks a flow in the selected project
 * itself (its base objects seed the graph); a non-seed flow was reached downstream, possibly in another repo, which
 * is why `repoId`/`repoName` travel with every node. `depth` is how many downstream hops from the seed it sits at. */
export interface ProjectGraphPipeline {
  id: string;
  name: string;
  kind: string;
  wave: number;
  repoId: string;
  repoName: string;
  relativePath: string;
  isSeed: boolean;
  depth: number;
}

/** A project's lineage as one cross-repo subgraph: the pipeline nodes in the downstream closure, the edges among
 * them and the objects they move, the object keys at the depth-capped frontier that still have un-included consumers
 * (so the client can offer to expand them), and whether a node cap truncated the walk. */
export interface ProjectGraph {
  pipelines: ProjectGraphPipeline[];
  edges: LineageEdge[];
  frontier: string[];
  truncated: boolean;
}

/** One repo whose lineage references an object: how many of its edges touch the object and whether a flow there
 * writes/creates it. Ranked writing-repo-first so a search jump to the lineage graph opens on the repo that shows
 * how the object is populated. */
export interface ObjectRepo {
  repoId: string;
  repoName: string;
  edgeCount: number;
  writes: boolean;
}

/** A file-ingestion pipeline whose source spec matches a file: the flow that would ingest it, resolved from the
 * flow definitions in the catalog (no run required). `pathConfirmed` is true when the full path (not just the file
 * name) was matched, so a definitively-located match ranks above a name-only one. */
export interface FilePipelineMatch {
  pipelineId: string;
  pipelineName: string;
  repoId: string;
  repoName: string;
  sourceType: string;
  sourceLocation: string | null;
  pattern: string;
  pathConfirmed: boolean;
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
  // Which body carried the match: "Module" (the live sys.sql_modules definition) or "Script" (the emitted DDL).
  source: string;
}

export interface FileHit {
  name: string;
  path: string | null;
  runId: string;
  flowName: string;
  flowKind: string;
  rows: number;
  columns: number;
  sizeBytes: number;
  runUtc: string | null;
  // The flow that ingested the file and its repo, so the hit can jump to that flow's node in the lineage graph.
  pipelineId: string;
  repoId: string | null;
  repoName: string | null;
}

export interface FlowHit {
  id: string;
  name: string;
  kind: string;
  batch: string | null;
  relativePath: string;
  repoId: string;
  repoName: string;
  // Where the term matched: "Name", "Path", or "Body" (inside the YAML).
  matchedIn: string;
  snippet: string;
}

// One category of a combined search: the full match count plus a short preview of the top hits.
export interface SearchCategory<T> {
  total: number;
  items: T[];
}

// The combined result of a single global search across every catalog surface.
export interface AllSearchResult {
  objects: SearchCategory<ObjectHit>;
  columns: SearchCategory<ColumnHit>;
  definitions: SearchCategory<DefinitionHit>;
  files: SearchCategory<FileHit>;
  flows: SearchCategory<FlowHit>;
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

// ---- Personal access tokens -------------------------------------------------------------------------------------

export interface AccessToken {
  id: string;
  name: string;
  /** The recognizable, non-secret lead of the token (for example "sqlf_a1b2c3"). */
  prefix: string;
  scopes: string[];
  createdUtc: string;
  /** Null when the token never expires; a past value means it is expired. */
  expiresUtc: string | null;
  lastUsedUtc: string | null;
  /** Non-null once the token is revoked. */
  revokedUtc: string | null;
}

export interface CreateAccessTokenRequest {
  name: string;
  /** Capped server-side to the caller's own scopes; omit/empty defaults to all of them. */
  scopes?: string[];
  /** Null (omitted) means the token never expires. */
  expiresInDays?: number | null;
}

/** The create response: the listing view plus the one-time secret, shown exactly once. */
export interface CreatedAccessToken {
  token: AccessToken;
  secret: string;
}

// ---- Datasources and ad-hoc compute -------------------------------------------------------------------------------

/** One datasource the estate declares: a connection reference plus how the catalog sees it used. */
export interface Datasource {
  /** The connection reference: a whole ${...} / @alias token, or the hashed identity of an inline literal. */
  reference: string;
  /** Best-effort provider kind read from the flow definitions (MSSQL / AZDB / MySQL / PostgreSQL / Oracle). */
  kind: string | null;
  /** True when a worker can resolve the reference for ad-hoc compute (only whole references can travel). */
  resolvable: boolean;
  sourcePipelines: number;
  targetPipelines: number;
}

export type ComputeOperation =
  | "testConnection"
  | "listDatabases"
  | "listSchemas"
  | "listObjects"
  | "searchObjects"
  | "introspectObject"
  | "detectUniqueKey";

/** The body that requests an ad-hoc compute task. References only; a secret is never sent. */
export interface ComputeTaskRequest {
  reference: string;
  operation: ComputeOperation;
  /** Provider kind for a ${...} reference (default SQL Server); an @alias resolves its own kind. */
  kind?: string | null;
  /** Routes the task to a node serving this pool (a node that can reach the source); omit for any node. */
  pool?: string | null;
  database?: string | null;
  schema?: string | null;
  objectName?: string | null;
  nameLike?: string | null;
  searchTerm?: string | null;
  includeTables?: boolean;
  includeViews?: boolean;
  includeSystem?: boolean;
  offset?: number;
  limit?: number;
  /** detectUniqueKey: null auto-samples large tables, 0 forces a full scan, positive sets an explicit sample. */
  sampleSize?: number | null;
  maxKeyColumns?: number;
  maxCandidates?: number;
  verifyCandidates?: boolean;
  trustDeclaredKeys?: boolean;
}

export interface ComputeTaskAccepted {
  taskId: string;
  status: string;
}

/** A compute task as the task list shows it (no result body). */
export interface ComputeTaskSummary {
  taskId: string;
  operation: ComputeOperation;
  sourceRef: string;
  providerKind: string | null;
  pool: string | null;
  status: string;
  requestedBy: string | null;
  enqueuedUtc: string;
  startUtc: string | null;
  endUtc: string | null;
  claimedByNode: string | null;
  cancelRequestedUtc: string | null;
  error: string | null;
  hasResult: boolean;
  /** The [db.]schema.name an object-scoped task ran against (introspect / detect key); null otherwise. */
  target: string | null;
}

/** A single compute task; result is the operation's JSON document once the task succeeded. */
export interface ComputeTask extends Omit<ComputeTaskSummary, "hasResult"> {
  result: unknown | null;
}

// ---- Compute result payloads (the shapes of ComputeTask.result per operation) --------------------------------------

export interface ConnectionTestResult {
  ok: boolean;
  kind: string;
  serverVersion: string | null;
  database: string | null;
  elapsedMs: number;
}

export interface DatasourceDatabase {
  name: string;
  collation: string | null;
  state: string | null;
}

export interface DatasourceSchema {
  name: string;
  owner: string | null;
}

export interface DatasourceObject {
  schema: string;
  name: string;
  type: "Table" | "View";
  approxRows: number;
}

export interface DatasourceObjectPage {
  items: DatasourceObject[];
  offset: number;
  limit: number;
  total: number;
  hasMore: boolean;
}

export interface DatasourceObjectMatch {
  schema: string;
  name: string;
  type: "Table" | "View";
  rank: number;
}

export interface IntrospectedColumn {
  name: string;
  ordinal: number;
  nativeType: string;
  isNullable: boolean;
  collation: string | null;
  isIdentity: boolean;
  computedExpression: string | null;
  defaultExpression: string | null;
  isPrimaryKeyMember: boolean;
}

export interface IntrospectedIndex {
  name: string;
  isPrimaryKey: boolean;
  isUnique: boolean;
  isClustered: boolean;
  isColumnStore: boolean;
  keyColumns: string[];
}

export interface IntrospectedObject {
  name: { database: string | null; schema: string; name: string; schemaQualified: string; qualifiedName: string };
  type: "Table" | "View";
  columns: IntrospectedColumn[];
  indexes: IntrospectedIndex[];
  isTemporal: boolean;
}

export interface IntrospectionResult {
  found: boolean;
  object?: IntrospectedObject;
}

export interface UniqueKeyCandidate {
  columns: string[];
  isUnique: boolean;
  verified: boolean;
  declared: boolean;
  distinct: number;
  nulls: number;
  rows: number;
  duplicates: number;
  estimated: boolean;
  selectivity: number;
}

export interface UniqueKeyReport {
  objectName: string | null;
  totalRows: number;
  scannedRows: number;
  sampled: boolean;
  columns: { column: string; distinct: number; nulls: number; scanned: number; selectivity: number }[];
  candidates: UniqueKeyCandidate[];
  excludedColumns: { column: string; reason: string }[];
  note: string | null;
}

// ---- Notifications (self-service: the caller's own subscriptions) ------------------------------------------------

/** Whether one delivery channel is usable on this deployment, and which configured provider backs it. */
export interface NotificationChannelAvailability {
  available: boolean;
  /** The configured provider name (for example "smtp" or "slack"); null when the channel is not configured. */
  provider: string | null;
}

/** The deployment's notification capabilities plus the defaults a new subscription starts from. */
export interface MyNotificationOptions {
  enabled: boolean;
  email: NotificationChannelAvailability;
  slack: NotificationChannelAvailability;
  /** Every event kind a subscription can select (for example "run_failed"). */
  kinds: string[];
  /** The kinds preselected for a new subscription. */
  defaultKinds: string[];
  modes: string[];
  /** The account email used when a subscription carries no explicit address; null when the account has none. */
  userEmail: string | null;
  defaultDigestIntervalMinutes: number;
  defaultCooldownMinutes: number;
}

export interface NotificationSubscription {
  id: string;
  channel: "email" | "slack";
  mode: "immediate" | "digest";
  kinds: string[];
  /** Wildcard filter over flow names; null means the subscription covers all flows. */
  flowPattern: string | null;
  /** Null means messages go to the account email. */
  emailAddress: string | null;
  /** Null means the user is direct-messaged. */
  slackTarget: string | null;
  digestIntervalMinutes: number;
  cooldownMinutes: number;
  enabled: boolean;
  lastSentUtc: string | null;
  /** When the next digest is due; null for immediate subscriptions or when nothing is pending. */
  nextDueUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface CreateNotificationSubscriptionRequest {
  channel: "email" | "slack";
  mode?: "immediate" | "digest";
  /** Omit to start from the server's default kinds. */
  kinds?: string[];
  flowPattern?: string | null;
  emailAddress?: string | null;
  slackTarget?: string | null;
  digestIntervalMinutes?: number;
  cooldownMinutes?: number;
}

/** Partial update: null/omitted keeps the current value; an empty string clears flowPattern/emailAddress/slackTarget. */
export interface UpdateNotificationSubscriptionRequest {
  mode?: "immediate" | "digest";
  kinds?: string[];
  flowPattern?: string | null;
  emailAddress?: string | null;
  slackTarget?: string | null;
  digestIntervalMinutes?: number;
  cooldownMinutes?: number;
  enabled?: boolean;
}

/** One outbound message, tracked from queueing through delivery. */
export interface NotificationDelivery {
  id: string;
  subscriptionId: string;
  channel: "email" | "slack";
  target: string;
  subject: string;
  status: "queued" | "sending" | "sent" | "failed";
  attempts: number;
  /** How many notification events the message carries (a digest bundles several). */
  eventCount: number;
  lastError: string | null;
  createdUtc: string;
  sentUtc: string | null;
}

/** The accepted test send: the delivery to watch for in the recent-deliveries list. */
export interface NotificationTestSend {
  deliveryId: string;
}
