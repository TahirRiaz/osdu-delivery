// The control plane's /api/v1 contracts, one interface per DTO in src/SqlFlow.ControlPlane/Api (camelCase on the
// wire). Keep these in step with the C# records: the GUI trusts the shape it declares here.

// ---- Paging ---------------------------------------------------------------------------------------------------------

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

// ---- Authentication and identity ------------------------------------------------------------------------------------

export interface TokenResponse {
  accessToken: string;
  tokenType: string;
  expiresIn: number;
}

/** A user session: the token plus who it is for, so the shell can render the role and gate the admin surfaces. */
export interface SessionResponse extends TokenResponse {
  subject: string;
  role: string;
  scopes: string[];
}

export interface EntraProviderInfo {
  enabled: boolean;
  clientId: string | null;
  authority: string | null;
}

/** Which sign-in methods the control plane offers: local username/password, the bootstrap secret, Entra ID. */
export interface AuthProviders {
  local: boolean;
  bootstrap: boolean;
  entra: EntraProviderInfo;
}

/** Who the current credential is (GET /me): subject, role, scopes, and the user id when it is a user's session. */
export interface Identity {
  subject: string;
  role: string | null;
  scopes: string[];
  userId: string | null;
}

// ---- Dashboard ------------------------------------------------------------------------------------------------------

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

// ---- Repos ----------------------------------------------------------------------------------------------------------

export interface Repo {
  id: string;
  name: string;
  remoteUrl: string | null;
  rootPath: string | null;
  firstSeenUtc: string;
  lastSyncUtc: string;
}

export interface RepoTreeEntry {
  path: string;
  isFolder: boolean;
  sizeBytes: number;
}

/** The repository's synced tree (folders and files), read from git or the recorded root path. */
export interface RepoTree {
  readFrom: string;
  entries: RepoTreeEntry[];
  truncated: boolean;
}

export interface RepoSyncResult {
  pipelinesAdded: number;
  pipelinesUpdated: number;
  pipelinesUnchanged: number;
  pipelinesDeactivated: number;
  pipelinesDeleted: number;
  runsAdded: number;
  runsSkipped: number;
  runsFailed: number;
  /** The document families the sync extensions reconcile (mappings, snapshots). */
  documentsAdded: number;
  documentsUpdated: number;
  documentsUnchanged: number;
  documentsRemoved: number;
  documentsInvalid: number;
  warnings: string[];
}

/** What deleting a repo removed, so the confirmation can say what is gone. */
export interface RepoDeletionResult {
  pipelines: number;
  runs: number;
  runGroups: number;
  schedules: number;
  sourceRemoved: boolean;
}

// ---- Pipelines ------------------------------------------------------------------------------------------------------

/** The flow's declared execution mode: auto (default), manual (runs only when named directly), disabled. */
export type ExecutionMode = "auto" | "manual" | "disabled";

export interface PipelineSummary {
  id: string;
  repoId: string;
  name: string;
  kind: string;
  batch: string | null;
  wave: number;
  active: boolean;
  executionMode: ExecutionMode;
  lifecycle: string;
  sourceServer: string | null;
  targetServer: string | null;
  relativePath: string;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

export interface PipelineBatch {
  repoId: string;
  batch: string;
  flowCount: number;
  activeCount: number;
}

export interface PipelineDetail extends PipelineSummary {
  contentHash: string;
  yaml: string;
  definitionJson: string;
}

// ---- Runs -----------------------------------------------------------------------------------------------------------

/** The severity of one trace line, as the engine logs it. */
export type TraceLevel = "trace" | "debug" | "info" | "warning" | "error";

export type RunStatus = "queued" | "running" | "succeeded" | "failed" | "cancelled" | "skipped";

/** What a trigger enqueues: the one named flow. */
export type RunScope = "flow";

export interface RunSummary {
  runId: string;
  pipelineId: string;
  repoId: string | null;
  flowName: string;
  flowKind: string;
  batch: string;
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
  groupId: string | null;
  /** The newest trace event's message; only the group member list resolves it, every other list leaves it null. */
  lastAction: string | null;
  lastActionUtc: string | null;
  error: string | null;
  /** What the run does: deliver, verify, plan or known-state. */
  operation: RunOperation;
  /** Forced past the change gates. */
  force: boolean;
}

export interface RunDetail {
  runId: string;
  pipelineId: string;
  repoId: string | null;
  flowName: string;
  flowKind: string;
  batch: string;
  wave: number;
  status: RunStatus;
  success: boolean;
  targetPool: string | null;
  commitSha: string | null;
  enqueuedUtc: string | null;
  claimedByNode: string | null;
  cancelRequestedUtc: string | null;
  schemaVersion: number;
  writtenUtc: string;
  startUtc: string | null;
  endUtc: string | null;
  durationSeconds: number | null;
  rowsLoaded: number | null;
  rowsInserted: number | null;
  rowsUpdated: number | null;
  rowsDeleted: number | null;
  error: string | null;
  host: string | null;
  /** Who asked for the run (a signed-in user, an API client), null for the scheduler's own fires. */
  requestedBy: string | null;
  operation: RunOperation;
  force: boolean;
  /** The submission the run re-ran or was scoped to, when it was. */
  submissionId: string | null;
  /** The full run parameters as stored (JSON of RunParameters), null for the defaults. */
  parametersJson: string | null;
  /** The submission a deliver run registered or completed, projected from its result. */
  resultSubmissionId: string | null;
  recordsPlanned: number | null;
  recordsDelivered: number | null;
  recordsHeld: number | null;
  recordsFailed: number | null;
  recordsSkipped: number | null;
  groupId: string | null;
  /** For a fan-out member (an intake partition or a drain a deliver run spread across the fleet): the run that fanned it out. */
  fanOutRoot: string | null;
  fanOutSlot: number | null;
  fanOutCount: number | null;
  /** The run's result object as JSON: the kind-specific outcome (counts, submission, partitions), null until it finished. */
  resultJson: string | null;
}

/** The operations a delivery run performs. */
export type RunOperation = "deliver" | "verify" | "plan" | "known-state" | "intake" | "drain";

export const RUN_OPERATIONS: readonly RunOperation[] = ["deliver", "verify", "plan", "known-state", "intake", "drain"];

/** The per-run parameters as the run row stores them (parametersJson) and as a trigger sends them. */
export interface RunParameters {
  operation?: RunOperation;
  force?: boolean;
  /** The flow's declared parameter values, name to value. */
  values?: Record<string, string>;
  /** An explicit drop location overriding the flow's declared source. */
  drop?: string | null;
  /** Re-run one submission. */
  submissionId?: string | null;
  /** The delivery keys the run is scoped to. */
  recordKeys?: string[];
  /** Where a known-state publication goes. */
  publishTo?: string | null;
}

/** One line of a run's trace: the run events in time order. */
export interface RunTraceEntry {
  id: number;
  runId: string;
  repoId: string | null;
  ordinal: number;
  timestampUtc: string;
  level: TraceLevel;
  step: string | null;
  message: string;
  rows: number | null;
  elapsedMs: number | null;
}

/** The payload of the live trace stream's final `end` frame: the run's terminal status. */
export interface RunTraceStreamEnd {
  status: RunStatus;
}

/** One line of an operation's activity trace (a repo sync, for instance). */
export interface ActivityEvent {
  id: number;
  activityId: string;
  kind: string;
  subjectKey: string;
  ordinal: number;
  timestampUtc: string;
  level: TraceLevel;
  step: string | null;
  message: string | null;
  terminal: boolean;
  status: string | null;
}

export interface RunTriggerRequest extends RunParameters {
  repoId: string;
  flowName: string;
  pool?: string | null;
  commitSha?: string | null;
  scope?: RunScope | null;
}

export interface RunTriggerAccepted {
  runId: string;
  status: RunStatus;
}

export interface RunScopePreviewMember {
  flowName: string;
  flowKind: string;
  wave: number;
}

export interface RunScopePreview {
  scope: RunScope;
  anchor: string;
  memberCount: number;
  waveCount: number;
  members: RunScopePreviewMember[];
}

export interface RunGroupCounts {
  total: number;
  queued: number;
  running: number;
  succeeded: number;
  failed: number;
  cancelled: number;
  skipped: number;
}

/** A set of runs enqueued together (a schedule fire), with its member counts by status. */
export interface RunGroup {
  groupId: string;
  repoId: string;
  mode: string;
  anchor: string;
  memberCount: number;
  commitSha: string | null;
  enqueuedUtc: string;
  counts: RunGroupCounts;
}

// ---- Schedules ------------------------------------------------------------------------------------------------------

export interface ScheduleChainLink {
  id: string;
  name: string;
  depth: number;
  memberCount: number;
  enabled: boolean;
  paused: boolean;
}

export interface Schedule {
  id: string;
  repoId: string;
  name: string;
  memberPipelineIds: string[];
  cron: string | null;
  intervalSeconds: number | null;
  timezone: string;
  enabled: boolean;
  catchup: boolean;
  paused: boolean;
  source: string;
  nextFireUtc: string | null;
  lastFireUtc: string | null;
  lastRunId: string | null;
  lastGroupId: string | null;
  lastGroupActive: boolean;
  createdUtc: string;
  updatedUtc: string;
  maxConcurrency: number | null;
  lastCounts: RunGroupCounts | null;
  /** The schedules this one waits for (chained after), by name. */
  afterSchedules: string[] | null;
  /** The schedules chained behind this one, with their depth in the chain. */
  triggersSchedules: ScheduleChainLink[] | null;
  parentFreshnessHours: number;
  lastStaleParents: string | null;
  /** The operation every fire runs its members with (deliver by default; verify for a drift check). */
  operation: RunOperation;
}

export interface ScheduleDefinition {
  scheduleId: string;
  name: string;
  source: string;
  path: string | null;
  flowName: string | null;
  pipelineId: string | null;
  yaml: string | null;
}

export interface CreateScheduleRequest {
  repoId: string;
  members: string[];
  cron?: string | null;
  intervalSeconds?: number | null;
  timezone?: string | null;
  enabled?: boolean | null;
  catchup?: boolean | null;
  name?: string | null;
  maxConcurrency?: number | null;
  operation?: RunOperation | null;
}

export interface ScheduleCreated {
  id: string;
  nextFireUtc: string | null;
}

export interface ScheduleRunAccepted {
  runId: string;
  groupId: string | null;
  memberCount: number;
}

export interface SchedulePlanMember {
  flowName: string;
  flowKind: string;
  wave: number;
  batch: string;
  pipelineId: string | null;
}

export interface SchedulePlan {
  scheduleId: string;
  repoId: string;
  name: string;
  cron: string | null;
  intervalSeconds: number | null;
  timezone: string;
  enabled: boolean;
  paused: boolean;
  nextFireUtc: string | null;
  lastFireUtc: string | null;
  anchor: string;
  memberCount: number;
  waveCount: number;
  members: SchedulePlanMember[];
}

// ---- Nodes and worker pools -----------------------------------------------------------------------------------------

export interface Node {
  name: string;
  firstSeenUtc: string;
  lastSeenUtc: string;
  version: string | null;
  online: boolean;
  restartRequestedUtc: string | null;
}

export interface NodePurgeResult {
  removed: number;
}

export interface WorkerPool {
  pool: string;
  minReplicas: number;
  manualReplicas: number;
  manualUntilUtc: string | null;
  manualActive: boolean;
  queuedRuns: number;
  replicaTarget: number;
  onlineNodes: number;
  updatedUtc: string | null;
  updatedBy: string | null;
}

export interface WorkerPoolScaleRequest {
  pool?: string | null;
  minReplicas?: number | null;
  manualReplicas?: number | null;
  manualForMinutes?: number | null;
}

// ---- Repo sources ---------------------------------------------------------------------------------------------------

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
  credentialReference: string | null;
  credentialUsername: string | null;
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
  excludedFlowPaths?: string[] | null;
}

export interface DiscoverRepoRequest {
  remoteUrl: string;
  branch?: string | null;
  credentialReference?: string | null;
  credentialUsername?: string | null;
}

export interface DiscoveredFlow {
  relativePath: string;
  flowName: string | null;
  kind: string | null;
  sizeBytes: number;
  parseOk: boolean;
  parseError: string | null;
  content: string | null;
}

export interface RepoSourceRegistered {
  id: string;
}

// ---- Search ---------------------------------------------------------------------------------------------------------

export interface FlowHit {
  id: string;
  name: string;
  kind: string;
  batch: string | null;
  relativePath: string;
  repoId: string;
  repoName: string;
  /** Which part of the document matched: the name, the path, or the body. */
  matchedIn: string;
  snippet: string;
}

export interface SearchCategory<T> {
  total: number;
  items: T[];
}

/** One delivery record that matched a lookup: where it belongs, how it is identified, and its custody state. */
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
}

export interface AllSearchResult {
  query: string;
  /** The words the term was split into; every one must match. */
  tokens: string[];
  flows: SearchCategory<FlowHit>;
  /** Delivery records: an exact delivery key, or a prefix over OSDU id, source key and label, across every flow. */
  records: SearchCategory<DeliveryRecordHit>;
}

// ---- Users and roles ------------------------------------------------------------------------------------------------

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

export interface UpdateUserProfileRequest {
  username: string;
  email?: string | null;
  displayName?: string | null;
}

export interface CreateUserRequest {
  username: string;
  password: string;
  role: string;
  email?: string | null;
  displayName?: string | null;
}

// ---- Personal access tokens -----------------------------------------------------------------------------------------

export interface AccessToken {
  id: string;
  name: string;
  prefix: string;
  scopes: string[];
  createdUtc: string;
  expiresUtc: string | null;
  lastUsedUtc: string | null;
  revokedUtc: string | null;
}

export interface CreateAccessTokenRequest {
  name: string;
  scopes?: string[] | null;
  expiresInDays?: number | null;
}

/** The one and only time the secret is shown: the token row plus its plaintext value. */
export interface CreatedAccessToken {
  token: AccessToken;
  secret: string;
}

// ---- Notifications --------------------------------------------------------------------------------------------------

export interface NotificationChannelAvailability {
  available: boolean;
  provider: string | null;
}

export interface EstateDigestOptions {
  enabled: boolean;
  intervalMinutes: number;
  minWindowMinutes: number;
  maxWindowMinutes: number;
}

export interface MyNotificationOptions {
  enabled: boolean;
  email: NotificationChannelAvailability;
  slack: NotificationChannelAvailability;
  kinds: string[];
  defaultKinds: string[];
  modes: string[];
  userEmail: string | null;
  defaultDigestIntervalMinutes: number;
  defaultCooldownMinutes: number;
  estateDigest: EstateDigestOptions;
}

export interface NotificationSubscription {
  id: string;
  channel: string;
  mode: string;
  kinds: string[];
  flowPattern: string | null;
  emailAddress: string | null;
  slackTarget: string | null;
  digestIntervalMinutes: number;
  cooldownMinutes: number;
  enabled: boolean;
  lastSentUtc: string | null;
  nextDueUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface CreateNotificationSubscriptionRequest {
  channel: string;
  mode?: string | null;
  kinds?: string[] | null;
  flowPattern?: string | null;
  emailAddress?: string | null;
  slackTarget?: string | null;
  digestIntervalMinutes?: number | null;
  cooldownMinutes?: number | null;
}

export interface UpdateNotificationSubscriptionRequest {
  mode?: string | null;
  kinds?: string[] | null;
  flowPattern?: string | null;
  emailAddress?: string | null;
  slackTarget?: string | null;
  digestIntervalMinutes?: number | null;
  cooldownMinutes?: number | null;
  enabled?: boolean | null;
}

export interface NotificationDelivery {
  id: string;
  subscriptionId: string;
  channel: string;
  target: string;
  subject: string;
  status: string;
  attempts: number;
  eventCount: number;
  lastError: string | null;
  createdUtc: string;
  sentUtc: string | null;
}

export interface NotificationQueuedDelivery {
  deliveryId: string;
}

export interface NotificationDigestSummary {
  id: string;
  origin: string;
  periodStartUtc: string;
  periodEndUtc: string;
  generatedUtc: string;
  generatedBy: string | null;
  subject: string;
  eventCount: number;
  flowCount: number;
  failedCount: number;
  cancelledCount: number;
  skippedCount: number;
  truncated: boolean;
}

export interface NotificationDigestFlow {
  flowName: string;
  flowKind: string;
  /** The notification event kind (run_failed, run_cancelled, run_skipped), not a run status. */
  kind: string;
  count: number;
  lastOccurredUtc: string;
  lastRunId: string;
  pipelineId: string;
  lastError: string | null;
}

export interface NotificationDigest {
  summary: NotificationDigestSummary;
  flows: NotificationDigestFlow[];
  textBody: string;
  htmlBody: string;
}

export interface GenerateNotificationDigestRequest {
  fromUtc: string;
  toUtc: string;
}

export interface SendNotificationDigestRequest {
  subscriptionId: string;
}

// ---- Maintenance ----------------------------------------------------------------------------------------------------

export interface RunTraceStorage {
  totalEvents: number;
  prunableEvents: number;
  prunableRuns: number;
  retentionDays: number | null;
}

export interface RunTraceRetentionUpdate {
  retentionDays: number | null;
}

export interface RunTraceRetention {
  retentionDays: number | null;
}

export interface RunEventPurgeResult {
  eventsDeleted: number;
}
