# The control plane: API surface, configuration, bootstrap, managed sync

The control plane (src/SqlFlow.ControlPlane) is the ASP.NET Core host that serves the HTTP API over the catalog, queues and dispatches runs, fires schedules, keeps the catalog synced from git, and answers the delivery ledger. It composes the identical engine wiring as the CLI and the standalone workers via `AddSqlFlowEngine()`, then adds the delivery kind and the ledger (`AddDeliveryKind()`, `AddDeliveryLedger(...)`, src/SqlFlow.ControlPlane/Program.cs), so a run triggered over the API behaves exactly like a run started by `sqlflow run`: same file stores, same secret resolution chain, same `DocumentExecutor`.

It exists so that a team has one place to observe the estate (repos, pipelines, runs, records, nodes), one privileged surface to operate it (trigger, cancel, schedule, sync, release, redeliver), and zero manual steps to keep the queryable catalog current with git.

## Host composition

`src/SqlFlow.ControlPlane/Program.cs` builds the host in one pass:

- Configuration is bound from the `ControlPlane` section and validated eagerly at startup (`options.Validate()` plus `ValidateOnStart()`), so a misconfigured deployment never starts serving. Kestrel's request body ceiling is set on purpose from `ControlPlane:MaxRequestBodyMegabytes` (default 64), never left at the server default, so a submission manifest that lists many records fits and anything larger is refused deliberately.
- The catalog is read through a pooled `CatalogDbContext` with `NoTracking` query behavior; the provider setup (transient-error resiliency) is the one `CatalogDatabase.Configure` shared with the CLI, the worker, and bootstrap.
- Background services host bootstrap provisioning (`BootstrapProvisioningService`), the run worker (`RunExecutionWorker`, only when `Worker:Enabled` is true), the schedule scanner (`SchedulerService`), the orphan reaper (`OrphanRunReaper`), the run-trace pruner (`RunTraceReaper`, when `RunTrace:Enabled`), the managed git sync (`RepoSyncService`), the proposal staging janitor (`ProposalWorkspaceJanitor`), and the notification pipeline (`NotificationService`, when `Notifications:Enabled`).
- When the in-process worker is hosted, the host's shutdown timeout is raised to the worker's drain window plus 30 seconds (`RunWorker.DefaultDrainTimeout`, 9 minutes), so a stop lets in-flight runs record their own outcomes instead of severing them.
- The run queue is the catalog's `Run` table itself: the trigger endpoint enqueues a `queued` row through `IRunDispatcher`, a worker claims the oldest queued run atomically (single-statement `UPDATE ... WITH (UPDLOCK, READPAST, ROWLOCK)` in src/SqlFlow.Catalog/RunQueueStore.cs), and the outcome is recorded under the same id the trigger returned. Runs survive a restart and are visible to the read API from the moment they are queued.
- Every response passes through `CorrelationIdMiddleware`, responses are compressed, and errors are surfaced as RFC problem details (`AddProblemDetails` plus a `GlobalExceptionHandler`).
- OpenAPI is mapped at `/openapi/v1.json` via `MapOpenApi()`, and the device-authorization approval page at `/device` (the `verificationUri` a headless client shows for the RFC 8628 grant).

### Authentication and scopes

One `Authorization: Bearer` header carries either credential: a short-lived HS256 session token (the GUI) or a long-lived personal access token (the CLI and automation, prefixed `sqlf_`). A policy scheme reads the token's shape and forwards it to the right validator, so authorization downstream sees one principal type. JWT validation pins the algorithm to HS256 only (`ValidAlgorithms = [HmacSha256]`), validates issuer, audience, lifetime, and signing key, uses a 30 second `ClockSkew`, and sets `MapInboundClaims = false` so the `sub` and `scope` claim types stay as issued.

Four authorization policies exist, driven by a space-delimited `scope` claim:

| Policy | Requirement |
| --- | --- |
| `read` | Any authenticated user |
| `operate` | Any authenticated user |
| `author` | Any authenticated user |
| `admin` | `scope` claim contains `admin` |

The privilege model has exactly two tiers on purpose: any signed-in user gets the whole operational product (reading, running and cancelling flows, managing schedules and repo sources, scaling pools, proposing pull requests, releasing and redelivering records), and only user administration is fenced off behind `admin`. The route groups still carry the `read`, `operate`, and `author` names, which is where a stricter policy would be applied. Roles still carry scopes (`admin`: `read operate admin author`; `operator`: `read operate author`; `viewer`: `read`), and a personal access token's scopes are capped to its owner's.

The sign-in surface under `/api/v1/auth` maps `GET /auth/providers` and `POST /auth/login` always, `POST /auth/renew` and the device-grant endpoints (`POST /auth/device`, `/auth/device/token`, `/auth/device/approve`, `/auth/device/deny`) always, `POST /auth/exchange` only when Entra sign-on is enabled, and the break-glass `POST /auth/token` only when `Jwt:BootstrapSecret` is set. See [Authentication and identity](./authentication-and-identity.md) for token issuance, local users, and Entra SSO.

### API surface

All endpoints live under `/api/v1` and every list is paged (`page`, `pageSize`). Query parameters are shown in parentheses.

Read group (any authenticated user):

| Endpoint | Purpose |
| --- | --- |
| `GET /repos`, `GET /repos/{id}` | Repository registry (src/SqlFlow.ControlPlane/Api/CatalogEndpoints.cs) |
| `GET /repos/{id}/tree` | What the repository actually holds (folders, files, sizes), read from the synced branch or the local path, not only what the catalog imported |
| `GET /repos/{id}/file` (`path`) | One `.yaml` or `.yml` document of the repository, read where the tree is read, secret-redacted and cut at a million characters (`truncated`); a path outside the repository or under `.git`, a file that is not YAML and binary content are 400s, a missing file a 404 (src/SqlFlow.ControlPlane/Api/RepoTreeEndpoints.cs) |
| `GET /pipelines` (`repoId`, `kind`, `active`, `name`, `project`, `batch`), `GET /pipelines/batches`, `GET /pipelines/projects`, `GET /pipelines/{id}`, `GET /pipelines/{id}/definition` | Pipeline registry; the detail carries the secret-redacted YAML and the parsed definition as JSON |
| `GET /flow-history/commits` (`repoId`, `path`, `author`, `message`, `since`, `until`, `limit`), `GET /flow-history/flows/{pipelineId}`, `GET /flow-history/diff` (`repoId`, `sha`, `path`) | The git history of the flow documents: who changed a flow, when, and the patch (src/SqlFlow.ControlPlane/Api/GitHistoryEndpoints.cs). Credentials never leave the control plane |
| `GET /runs` (`repoId`, `pipelineId`, `flowKind`, `status`, `success`, `flowName`, `batch`, `scheduleId`, `groupId`, `latest`, `from`, `to`), `GET /runs/{runId}` | Run history and detail. The detail carries the run's `operation`, `force`, `submissionId`, `parametersJson`, `requestedBy`, and the delivery counts projected onto it (`recordsPlanned`, `recordsDelivered`, `recordsHeld`, `recordsFailed`, `recordsSkipped`) |
| `GET /runs/preview` (`repoId`, `flowName`, `scope`) | What a trigger would enqueue, without enqueuing |
| `GET /runs/{runId}/trace`, `GET /runs/{runId}/trace/text` | The run's trace: canonical engine events in timeline order (paged JSON, or one plain-text document for copying into a ticket) |
| `GET /runs/{runId}/trace/stream` (`afterEventId`) | The trace as Server-Sent Events while the run executes: one `entry` frame per event, a final `end` frame at the terminal status; the cursor resumes a dropped connection |
| `GET /pipelines/{pipelineId}/trace`, `/trace/text` | The pipeline's latest run's trace without resolving a run id first |
| `GET /runs/groups/{groupId}`, `GET /runs/groups/{groupId}/stream` | A run group (a schedule fire with several members): its rollup, and the members as Server-Sent Events while it executes |
| `GET /activities/stream` (`kind`, `subject`, `afterId`) | The live activity trace of any long-running control-plane operation (a repo-source sync, a discover), as Server-Sent Events (src/SqlFlow.ControlPlane/Api/ActivityEndpoints.cs) |
| `GET /search/all` (`q`), `GET /search/flows` (`q`) | One global search: flow documents by name, path, or body text (a multi-word term matches word by word, every word required, and the reply echoes the parsed `tokens`), and delivery records from the ledger (a delivery key lands on the record; an OSDU id, a source key, or a label prefix lists the records that start with it). `/flows` pages through the flow hits (src/SqlFlow.ControlPlane/Api/SearchEndpoints.cs) |
| `GET /schedules` (`repoId`, `pipelineId`, `source`, `enabled`, `search`, `definitionPath`), `GET /schedules/{id}`, `GET /schedules/{id}/definition`, `GET /schedules/{id}/plan` | Schedules, the YAML behind a git-declared one, and the members a fire runs in wave order |
| `GET /nodes`, `GET /nodes/pools` | Worker fleet with a derived online flag; each pool's desired state and resolved replica target |
| `GET /repos/sources` | Managed repo sources |
| `GET /summary` | Estate summary (repos, pipelines, run counts, nodes online, schedules, sources with errors) |
| `GET /me`, `GET /me/tokens`, `POST /me/tokens`, `DELETE /me/tokens/{id}` | The caller's identity and personal access tokens (scopes capped to the caller's own) |
| `GET /me/notifications/options`, `GET/POST /me/notifications/subscriptions`, `PUT/DELETE /me/notifications/subscriptions/{id}`, `POST /me/notifications/subscriptions/{id}/test`, `GET /me/notifications/deliveries`, `GET/POST /notifications/digests`, `GET /notifications/digests/{id}`, `POST /notifications/digests/{id}/send` | The caller's notification opt-ins and the estate digests; see [Notifications](../guides/notifications.md) |
| `GET /maintenance/trace-storage`, `PUT /maintenance/trace-retention`, `POST /maintenance/events/prune`, `POST /maintenance/events/purge` | Run-event housekeeping: how much trace is stored, the retention window (days, or null for forever), a prune under the policy, a purge of every trace row. The delivery ledger is never touched here |
| `GET /compute/tasks/{taskId}` | One ad-hoc compute task (a target probe, a record read-back, a removal) as the queue holds it: operation, status, the claiming node, and its JSON result or error (src/SqlFlow.ControlPlane/Api/ComputeTaskEndpoints.cs) |
| `GET /delivery/...` | The delivery ledger reads: a flow's stats, records, and submissions; one record with its attempts and activities; one submission with its attempts; the audit trail; the mapping documents the repositories hold; every partition's cache with the cache flows that fill it, and one partition's cached records, versions, history and version comparison (named by `scope`) and the cache changes awaiting a decision (`GET /delivery/caches`, `/cache/items`, `/cache/versions`, `/cache/history`, `/cache/diff`, `/cache/tags`); the saved templates and one laid out variable by variable (`GET /delivery/templates`, `/templates/detail`, `/templates/schema`, `POST /delivery/templates/preview`); the releases of the OSDU data definitions, the record kinds a release publishes and one kind's schema bundled from it, read from the Open Group's public repository (`GET /delivery/templates/osdu/releases`, `/templates/osdu/schemas`, `/templates/osdu/schema`), and two versions of a kind compared (`/templates/osdu/compare`); and the mapping builder's repositories, caches, drafts, YAML and checks (`GET /delivery/mapping-builder/repos`, `/mapping-builder/caches`, `POST /delivery/mapping-builder/draft`, `/compose`, `/parse`; src/SqlFlow.ControlPlane/Api/DeliveryTemplateEndpoints.cs). Documented in [Delivery operations](../../delivery/operations.md) |

Operate group (any authenticated user):

| Endpoint | Purpose |
| --- | --- |
| `POST /runs` | Trigger a run (202 Accepted) |
| `POST /runs/{runId}/cancel` | Cancel a run (200 `cancelled` from queued; 202 `cancelling` while running) |
| `POST /runs/groups/{groupId}/cancel` | Cancel a whole run group (queued members cancelled, running members requested) |
| `POST /schedules`, `POST /schedules/{id}/run` (`batch`, `force`, `chain`), `POST /schedules/{id}/pause`, `POST /schedules/{id}/resume`, `DELETE /schedules/{id}` | Ad-hoc schedule management and firing a schedule now |
| `POST /repos/{id}/sync`, `DELETE /repos/{id}` | Re-sync a local-path repo from its recorded root (refused for a repo managed by a git source), and delete a repo with everything attributed to it (pipelines, runs and traces, groups, schedules, its source) |
| `POST /repos/sources`, `POST /repos/sources/{id}/sync`, `DELETE /repos/sources/{id}` | Register or update a managed repo source (upsert), force a sync now, remove a source |
| `POST /repos/discover` | Preview a repo's flows without importing (the selective-scan wizard) |
| `PUT /nodes/pools/scale`, `POST /nodes/{name}/restart`, `DELETE /nodes/{name}`, `DELETE /nodes/offline` | Set a pool's always-on floor and time-bounded manual override, ask a node to restart, drop one dead fleet entry or every offline one. Only catalog rows are written; the worker and the autoscaler read them |
| `POST /compute/tasks/{taskId}/cancel` | Cancel a queued (200) or running (202) compute task; 404 unknown, 409 already finished |
| `POST /delivery/submissions`, `POST /delivery/flows/{pipelineId}/release`, `POST /delivery/flows/{pipelineId}/probe`, `POST /delivery/records/{key}/release`, `/redeliver`, `/verify`, `/read`, `/delete` | The delivery interventions: a submission queues the run that takes it (a manifest notification for a finished drop, or the records themselves); release, redeliver, and verify act on the ledger under the caller's name or queue a run; probe, read-back, and delete queue a compute task for a node. `POST /delivery/ledger/prune` additionally requires `admin`. Documented in [Delivery operations](../../delivery/operations.md) |

Author group (any authenticated user): `POST /repos/sources/{id}/proposals` (src/SqlFlow.ControlPlane/Api/FlowProposalEndpoints.cs), described under managed sync below, and `POST /delivery/templates` and `DELETE /delivery/templates` (`kind`, `version`), which save a bundled schema as a template version and delete a version no synced mapping pins (src/SqlFlow.ControlPlane/Api/DeliveryTemplateEndpoints.cs).

Admin group (policy `admin`): `GET /users`, `GET /users/{id}`, `POST /users`, `POST /users/{id}/profile` `/role` `/activate` `/deactivate` `/password`, `DELETE /users/{id}`, and `GET /roles` (src/SqlFlow.ControlPlane/Api/UserEndpoints.cs).

### Health probes and rate limiting

- `GET /health/live` has no dependencies (its predicate excludes every check); `GET /health/ready` runs the catalog database check. Both are mapped with `DisableRateLimiting()`, so probe traffic behind a shared egress IP can never be throttled into a false negative.
- A global fixed-window rate limiter partitions per authenticated subject (the `sub` claim) or, when anonymous, per client IP: default 120 permits per 60 second window, `QueueLimit = 0`, and HTTP 429 on rejection. The limiter runs after authentication so the partition can key on the subject.

### CORS and reverse-proxy awareness

- CORS is an explicit allowlist (`ControlPlane:Cors:AllowedOrigins`, any header and any method for those origins). An empty list means no CORS policy is registered at all: same-origin only.
- Forwarded headers (`X-Forwarded-For`, `X-Forwarded-Proto`) are honored only when `ControlPlane:Proxy:Enabled` is true, and only from the proxies listed in `KnownNetworks` (CIDRs) or `KnownProxies` (IPs), with `ForwardLimit` hops (default 1). The forwarded-headers middleware runs first in the pipeline so the rate limiter and login throttle see the real client address. Enabling the proxy section while trusting no proxies is a startup error ("ControlPlane:Proxy is enabled but trusts no proxies; ...").

### Fleet visibility

`GET /nodes` (src/SqlFlow.ControlPlane/Api/NodeEndpoints.cs) lists workers that have heartbeated into the catalog, most recently seen first. A node is reported `online` when its last heartbeat is within the last 60 seconds, computed at read time. Each heartbeat also carries the node's `BusyRuns` (how many runs it is executing), which the KEDA scaler in deploy/k8s/worker-pool.yaml reads alongside the queue depth, so occupied workers hold their replicas while idle ones remain the reclaimable surplus.

`GET /nodes/pools` lists each pool's desired state (the always-on floor, the time-bounded manual override, how many runs are queued for it, the replica target the autoscaler holds, and the online count). `PUT /nodes/pools/scale` takes `{pool, minReplicas, manualReplicas, manualForMinutes}` with every field optional (a null field is left unchanged; at most 100 replicas and a 1440 minute window). The control plane never calls the orchestrator: scaling and restarts only write catalog rows.

### The orphan-run reaper

A background sweep (`OrphanRunReaper`, src/SqlFlow.ControlPlane/Background/OrphanRunReaper.cs) runs every `ControlPlane:Reaper:PollSeconds` (default 30) and recovers runs left `running` by a node that stopped heartbeating for `ControlPlane:Reaper:StaleAfterSeconds` (default 180, never below 60): the executing process is gone, so no outcome will ever be recorded, and the stuck row would otherwise block every future run of its pipeline. Losing a worker is recoverable, not terminal: an orphan is requeued for another worker to execute (its claim cleared), recorded `cancelled` when an operator cancel was already pending, and failed only once it has consumed its whole attempt budget (`RunQueueStore.MaxExecutionAttempts`, 3 claims), which is the bound that stops a poison run from crash-looping the fleet. Every claim increments the run's `Attempt`, which doubles as a fencing token: a zombie node's late outcome writes present a stale attempt and are dropped, so the successor execution's result is authoritative. The same sweep prunes fleet-registry rows for nodes offline longer than `NodeRetentionHours` (default 24; 0 disables the prune).

A **stopping** node is not an orphaned one, and the two must not be confused. When a worker is asked to stop (a SIGTERM from an autoscaler reclaiming the replica, a revision swap, an operator restart) it stops claiming but keeps executing the runs it already holds, and it keeps heartbeating for the whole drain precisely so this sweep leaves that work alone (src/SqlFlow.Node/RunWorker.cs). Were a draining node to go silent, the reaper would declare it dead within `StaleAfterSeconds`, requeue its runs, and a sibling node would re-execute work that was about to finish while the draining node's own outcome writes lost to the claim fence. Only when a node's drain window expires does it sever what remains, and those runs then become genuine orphans for this sweep to requeue.

Because a requeue consumes an execution attempt, anything that severs runs routinely will eventually exhaust the budget of a healthy run and fail it with "a run that repeatedly dies mid-flight is treated as the cause". In an autoscaled fleet the usual culprit is not the flow but the platform: replicas are reclaimed on a cadence, and a termination grace period shorter than the worker's drain window turns every scale-in into a severed run. See [`sqlflow worker`](../cli/worker.md) for the drain contract and the grace-period requirement.

### Run-trace retention

`RunTraceReaper` (src/SqlFlow.ControlPlane/Background/RunTraceReaper.cs) prunes the run event rows on a cadence (`ControlPlane:RunTrace:PollSeconds`, default 3600), keeping each pipeline's latest run, every failed run, anything in flight, and anything inside the retention window; the run header (its stats, counts, and error) is never touched. The retention window is operator-tunable through `PUT /maintenance/trace-retention` and defaults to "keep forever", in which case the sweep does nothing. It is hosted on every replica (the batched delete is idempotent) and `RunTrace:Enabled=false` turns the automatic sweep off while the manual prune keeps working. The delivery ledger has its own retention (`POST /delivery/ledger/prune`); this sweep never touches it.

## Configuration

All settings bind from the `ControlPlane` configuration section (environment variables use the `ControlPlane__Section__Key` form) and are validated at startup with messages that name the offending field (src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs).

| Key | Default | Notes |
| --- | --- | --- |
| `MaxRequestBodyMegabytes` | `64` | Range 1..4096. The API's request body ceiling, applied to Kestrel at startup |
| `Catalog:ConnectionReference` | `${env:SQLFLOW_CATALOG_DB}` | A connection string or a `${env:...}` / `${keyvault:...}` reference, resolved once through the secret resolver and cached for the app lifetime; a transient resolution failure is retried, not cached (src/SqlFlow.ControlPlane/Infrastructure/CatalogConnectionProvider.cs) |
| `Jwt:Issuer` | `sqlflow-control-plane` | Required non-blank |
| `Jwt:Audience` | `sqlflow` | Required non-blank |
| `Jwt:SigningKey` | none | Required; at least 32 UTF-8 bytes for HS256; source it from a secret, never a literal |
| `Jwt:AccessTokenMinutes` | `720` | Range 1..1440. One token's life; a signed-in GUI rolls its token at `POST /auth/renew` rather than letting it lapse |
| `Jwt:SessionMaxDays` | `30` | Range 1..365. The absolute ceiling on a rolling session, measured from the actual sign-in |
| `Jwt:BootstrapSecret` | unset | Optional; at least 32 bytes when set; `POST /auth/token` is only mapped when set |
| `AzureAd:Enabled` | unset | Unset offers Entra sign-on automatically once `AllowedTenantIds` and `ClientId` are configured; `true` requires them (a startup error otherwise); `false` forces SSO off even when they are present |
| `AzureAd:AllowedTenantIds` | `[]` | The tenants allowed to sign in; a token from any other tenant is rejected. No blank entries |
| `AzureAd:ClientId` | unset | The app registration the SPA signs in with |
| `AzureAd:Authority` | `https://login.microsoftonline.com` | The OIDC authority host; override for sovereign clouds. The per-tenant authority is `{host}/{tenantId}/v2.0` |
| `AzureAd:DefaultRole` | `viewer` | Role a first-time Entra user is provisioned with |
| `Bootstrap:ApplyMigrations` | `true` | `false` logs a warning when the database is unprovisioned or missing tables, instead of provisioning it |
| `Bootstrap:AllowCreate` | `false` | When `false` (the default), startup only migrates an EXISTING catalog: a missing database or a populated non-catalog database is refused loudly and startup stops without retrying, so a wrong or mistyped connection never provisions against the wrong (possibly production) server. `true` lets startup CREATE the catalog database and initialise its schema into an empty one, for first-time provisioning or ephemeral/test databases |
| `Bootstrap:AdminUsername` / `Bootstrap:AdminPasswordReference` | unset | Must be set together; the password is a secret reference, never a literal |
| `Bootstrap:DemoRepo` | unset | `Name` and `RemoteUrl` required when configured; `Branch` default `main`; `SyncIntervalSeconds` default `300` (must be positive) |
| `Cors:AllowedOrigins` | `[]` | Empty means same-origin only |
| `RateLimit:PermitPerWindow` / `RateLimit:WindowSeconds` | `120` / `60` | Both must be positive |
| `Proxy:Enabled` | `false` | See the proxy section above; `KnownNetworks` (CIDRs), `KnownProxies` (IPs), `ForwardLimit` default `1` |
| `Scheduler:PollSeconds` | `15` | Schedule scan cadence; must be positive |
| `Worker:Enabled` | `true` | `false` makes the replica API-only (no in-process worker) |
| `Worker:Pools` | `[]` | Empty claims only untargeted runs |
| `Worker:MaxConcurrentRuns` | `4` | How many claimed runs the in-process node executes at once; a saturated node stops claiming. At least 1 |
| `Worker:PollMilliseconds` | `2000` | The drain loop's poll fallback (a triggered run starts at once via the in-process nudge). At least 250 |
| `Worker:NodeName` | empty | The name the in-process node registers, claims and recovers under; empty takes `SQLFLOW_NODE_NAME`, else the machine name. Set it when a standalone `sqlflow worker` runs on the same host, or either one's startup recovery requeues the other's runs. At most 256 characters |
| `Reaper:PollSeconds` / `Reaper:StaleAfterSeconds` / `Reaper:NodeRetentionHours` | `30` / `180` / `24` | See the orphan-run reaper above |
| `ManagedSync:Enabled` | `true` | `false` opts this instance out of the managed sync loop entirely (a local dev instance sharing a production catalog must never steal claims) |
| `ManagedSync:PollSeconds` | `30` | Repo-source scan cadence |
| `RunTrace:Enabled` / `RunTrace:PollSeconds` | `true` / `3600` | See run-trace retention above |
| `Notifications:Enabled` | `true` | The notification pipeline; with no channel configured it idles. `PollSeconds` default `30`; email (`Email:Provider` `none`, `smtp`, or `graph`) and Slack (`Slack:BotTokenReference`) sections, every secret a reference. See [Notifications](../guides/notifications.md) |

## First-run bootstrap

`BootstrapProvisioningService` (src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs) runs in the background and retries with backoff (5s, 10s, 20s, 40s, then every 60s) until the catalog is reachable, so the start order of app and database never matters; the readiness probe reports the catalog being unavailable in the meantime. It logs the resolved catalog target (secret-free) before touching anything.

Before provisioning anything, the service applies the `Bootstrap:AllowCreate` guard (default `false`). Unless it is `true`, a missing database or a populated non-catalog database is a deterministic configuration mistake that retrying cannot fix, so the service logs a critical error and stops (readiness stays red) rather than conjuring a database or injecting catalog tables into the wrong server. Only `AllowCreate = true` creates the catalog database and initialises its schema into an empty one.

Order of operations, all idempotent:

1. Provision the catalog schema from the EF model when `Bootstrap:ApplyMigrations` is true (the default), and verify it against the model either way. With it false, an unprovisioned or stale database is logged as a warning instead.
2. Seed the built-in roles: `admin` (scopes `read operate admin author`), `operator` (`read operate author`), `viewer` (`read`). Existing role rows are kept as-is (`EnsureRoleAsync` inserts only when the role is absent).
3. Create the initial admin from `Bootstrap:AdminUsername` and `Bootstrap:AdminPasswordReference` (resolved through the secret resolver) when the user is absent. An existing admin's password is never reset. An empty resolved password, or one shorter than 12 characters (`LocalPasswords.MinLength`), logs an error and skips creation rather than provisioning a weak credential. With no admin configured and no users in the catalog, a warning says that only the bootstrap secret can access the API.
4. Register the optional demo repo source (`Bootstrap:DemoRepo`) as an upsert, so configuration stays the desired state across restarts.
5. Seed the default worker pool with an always-on floor of one replica, only when the pool has no desired state yet, so a fresh install has a node running rather than a scale-to-zero fleet nobody turned on. An operator's later setting (including zero) is never overridden.

## Managed git-to-catalog sync (repo sources)

A `CatalogRepoSource` (src/SqlFlow.Catalog/CatalogEntities.cs) registers a git repository the control plane keeps synced: it periodically pulls the branch HEAD and runs the catalog sync, so nobody runs `sqlflow db sync` by hand and git stays the source of truth. Defaults: `Branch` `main`, `SyncIntervalSeconds` `300`, `Enabled` `true`. `Name` is unique and the synced pipelines and runs are attributed to it. A source's id is `FlowIdentity.FromName("reposource/{name}")`, so re-registering the same name updates in place; a new source is due immediately (`NextSyncUtc = now`).

`RepoSyncService` (src/SqlFlow.ControlPlane/Background/RepoSyncService.cs) scans every `ManagedSync:PollSeconds` (default 30, at most 50 sources per tick) for sources due to sync. Each due sync is claimed by compare-and-swap on `NextSyncUtc` (`RepoSourceStore.TryClaimSyncAsync`), so several control-plane nodes never sync the same source twice in one interval. The winner writes an activity trace (kind `RepoSync`, subject the source id, tailed at `GET /activities/stream`) and then:

1. Resolves the source's git credential from its stored reference (see below).
2. Clones the branch tip fresh into `<cache>/<repoHash>/branch` (`GitMaterializer.MaterializeBranch`, src/SqlFlow.Node/GitMaterializer.cs). A repo with no commits fails with `'<remote>' (branch '<branch>') has no commits to sync.`
3. Runs the exact same `CatalogSync` as the CLI's `sqlflow db sync`, passing the source's flow selection (see below): pipelines are upserted by identity, the mapping documents and the types the cache flows declare are recorded, the git-declared schedules are mirrored, and run artifacts under the tree are imported.
4. Records the pulled commit SHA on success (`LastSyncedSha`, clearing `LastError`) or a secret-redacted error on failure (`LastError`, keeping the last successful SHA).

One source's failure never stops the others or the loop.

### Git credentials (a reference, never a secret)

A private remote's token is named by the source's `CredentialReference`, a `${keyvault:vault/secret}` or `${env:NAME}` reference resolved through the secret resolver at clone time (`GitMaterializer.ResolveCredentialsAsync`); an optional `CredentialUsername` pairs with it for hosts that authenticate the username too (Bitbucket app passwords). Only the reference is stored; the secret value is created and maintained in the vault, and the API rejects a raw token that is not a `${...}` reference. A source with no reference falls back to the host's own environment (`SQLFLOW_GIT_TOKEN`, optional `SQLFLOW_GIT_USERNAME`), which covers public remotes. The secret never rests in the catalog or the queue.

### Preview-first selective scan

A source imports every `*.flow.yaml` by default. To onboard a subset, `POST /repos/discover` clones the remote and lists its flows (relative path, parsed name and kind or the parse error, size, and redacted content for preview) WITHOUT touching the catalog. The chosen exclusions are stored on the source as `ExcludedFlowPaths` (a JSON array of repo-relative paths); the sync then projects only the included flows as pipelines. Because the sync is the single activation point, an excluded flow never becomes a catalog pipeline (so the scheduler never runs it), a previously-imported flow that becomes excluded is deactivated on the next sync (its run history is kept), and a re-included flow is imported again.

### API

- `GET /repos/sources` (read): paged list of `RepoSourceDto` with fields `id`, `name`, `remoteUrl`, `branch`, `enabled`, `syncIntervalSeconds`, `nextSyncUtc`, `lastSyncUtc`, `lastSyncedSha`, `lastError`, `credentialReference`, `credentialUsername`, `excludedFlowPaths`, `createdUtc`, `updatedUtc`.
- `POST /repos/sources` (operate): upsert with body `{name, remoteUrl, branch?, syncIntervalSeconds?, enabled?, credentialReference?, credentialUsername?, excludedFlowPaths?}`; `branch` defaults to `main`, `syncIntervalSeconds` to 300, `enabled` to true, and an omitted `excludedFlowPaths` imports every flow. A blank `name` or `remoteUrl`, or a `credentialReference` that is not a `${...}` reference, is 400. Returns 201 Created with the source id.
- `POST /repos/sources/{id}/sync` (operate): makes the source due now and returns it; 404 for an unknown or disabled source ("No enabled repo source '{id}'.").
- `DELETE /repos/sources/{id}` (operate): removes the source; 404 "No repo source '{id}'.". The repo it fed stays until `DELETE /repos/{id}` removes it with everything attributed to it.
- `POST /repos/discover` (operate): body `{remoteUrl, branch?, credentialReference?, credentialUsername?}`; clones and returns the flow list for the selection wizard. Read-only; nothing is written to the catalog. A clone or credential failure is a 400 with a redacted message.
- `POST /repos/{id}/sync` (operate): re-syncs a local-path repo from its recorded root path, inline. Refused (400) for a repo managed by a git source (trigger the source's own sync instead) and for a root path the control-plane host cannot see.

### Proposals: pipelines as pull requests

`POST /repos/sources/{id}/proposals` (author scope) proposes flow files, and the documents that go with them such as a mapping, to the source's repo as a pull request. Body `{title, body?, baseBranch?, headBranch?, files:[{path, content}]}`; `baseBranch` defaults to the source's tracked branch and `headBranch` to a content-derived `sqlflow/proposal-*` name. Every proposal is preflighted first (src/SqlFlow.ControlPlane/Api/FlowProposalPreflight.cs): a flow file the sync could not import, or a companion document (a mapping) that does not parse through the kind that owns it, is rejected before any git work, and softer findings ride into the response and the pull-request body. The control plane then pushes the files onto a fresh branch off the base and opens a pull request using the source's own stored credential (github.com or bitbucket.org over HTTPS); it never writes the catalog directly, so a human reviews and merges before the managed sync imports the flows. `files[].path` must be repo-relative and end in `.yaml`/`.yml`/`.sql`/`.json`/`.md` (at most 100 files); a bad path, an unsupported remote host, or a source with no configured credential is a 400. Returns 201 with `{pullRequestUrl, pullRequestNumber, headBranch, commitSha, filesChanged, warnings}`. Feed `commitSha` to `POST /runs` (`commitSha`) to test the proposal pinned to the pull-request commit before it merges. If the branch pushes but the pull request cannot be opened, the branch is rolled back and the response is 502. The git clone is staged in a throwaway temp directory deleted when the request finishes; the whole staging root (`{temp}/sqlflow/proposals`) is also swept on control-plane startup and shutdown by `ProposalWorkspaceJanitor`, so a staged clone never outlives the session and a crash leak is reclaimed on the next start.

## Triggering and cancelling runs

`POST /api/v1/runs` (operate scope, src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs) takes references only (repo and flow name) plus the delivery run parameters (`RunParameters`, src/SqlFlow.Core/Runs/RunParameters.cs):

```json
{
  "repoId": "0195c9a2-7f30-7c44-9c1e-0aa1b2c3d4e5",
  "flowName": "recall-welllog",
  "pool": null,
  "commitSha": null,
  "scope": "flow",
  "operation": "plan",
  "force": false,
  "values": { "logSource": "STAT_COMP" },
  "drop": null,
  "submissionId": null,
  "recordKeys": [],
  "publishTo": null
}
```

- `flowName` is required and must be non-blank (400 "A run trigger requires a non-blank flowName.").
- `scope` must be `flow` or omitted: a trigger runs one flow. A whole set of flows runs through its schedule (`POST /schedules/{id}/run`), whose membership is what a fire runs; anything else is 400.
- `commitSha` must be a 4 to 64 character hexadecimal git object id, or the request is 400. When omitted, enqueueing pins the run to the repo's last synced commit (`LastSyncedSha`) when one is resolvable, so the executed version matches what the catalog shows and any node can materialize it; only a repo with no resolvable synced commit runs unpinned from the node's local copy (src/SqlFlow.Catalog/RunQueueStore.cs).
- `operation` is one of `deliver` (the default: intake the drop, plan against the ledger, deliver what changed), `verify` (the drift pass: read delivered records back and compare versions), `plan` (render and compare, report what would be delivered, change nothing), or `known-state` (publish the compact known-state snapshot the preparing side reads).
- `force` pushes past the change gates: plan every record even when no source table advanced, re-plan a submission that was already completed, verify records verified recently.
- `values` are the flow's own declared parameters (the sample flow's `{logSource}` renders its drop location), at most 32 of them; names are identifiers, values at most 1000 characters without control characters.
- `drop` overrides the flow's declared source location for this run (1 to 2000 characters); `submissionId` re-runs one submission from its own drop; `recordKeys` scopes the run to those delivery keys (at most 1000; with `deliver` they are redelivered regardless of what OSDU holds, with `verify` only they are checked); `publishTo` names where a known-state publication goes. A `known-state` run takes no submission or record scope.
- The parameters are validated at this trust boundary via `RunParameters.Validate()`, so a run no engine path could honor is refused as 400 "Invalid run parameters" before anything is queued.
- An unknown or deactivated pipeline is 404: "No active pipeline '&lt;flow&gt;' in repo '&lt;repoId&gt;'." The pipeline's kind is carried onto the queued run.
- Success is 202 Accepted with `{runId, status: "queued"}` and a `Location` header pointing at `GET /api/v1/runs/{runId}`. The run row records the parameters and who requested it (`requestedBy`), so the history says exactly what was asked; when the run completes, its submission id and record counts are projected onto it.
- No secret is ever accepted in the request or echoed back; the executing node resolves all credentials from its own environment.

`GET /api/v1/runs/preview?repoId=...&flowName=...` answers what the trigger would enqueue without enqueuing it.

`POST /api/v1/runs/{runId}/cancel` (operate scope): a still-queued run is cancelled outright, 200 `{status: "cancelled"}`; a run already RUNNING is cancellable too, but asynchronously, so its owning node is stamped with a durable cancel request and the endpoint returns 202 Accepted with `{status: "cancelling"}` (poll `GET /api/v1/runs/{runId}` for the terminal outcome). An unknown id is 404 "No run '{runId}'."; an already-terminal run is 409 "Run '{runId}' has already finished and cannot be cancelled." The companion `POST /api/v1/runs/groups/{groupId}/cancel` cancels a whole run group the same way (queued members cancelled outright, running members sent a request): 202 `{status: "cancelling"}` when any member was still running, 200 `{status: "cancelled"}` otherwise, and 404 for an unknown group.

The delivery API's `POST /api/v1/delivery/submissions` is the same trigger with `operation` (`deliver` or `plan`), `parameters` and `force`, plus either the submission's `drop` (the manifest notification) or the records themselves under `records`, which the control plane stores in the ledger in the same transaction as the run; it answers 202 with the run id, or 200 with the run an already accepted request started. See [Delivery operations](../../delivery/operations.md) and [Submitting records](../../delivery/submitting-records.md).

## Schedules

Schedules come from two places and fire through one path. A flow declares its cadence in git (the sample flow's `schedule: { cron: "0 * * * *" }` block in samples/recall-welllog/flows/recall-welllog.yaml, or a `schedules.yaml` library), and the managed sync mirrors it into the catalog; an operator creates an ad-hoc one through the API. Either way `SchedulerService` (src/SqlFlow.ControlPlane/Background/SchedulerService.cs) scans every `Scheduler:PollSeconds` (default 15), claims each due occurrence by atomically advancing its next-fire time (so several replicas never double-fire), and enqueues the schedule's member set through `ScheduleFire` onto the same durable run queue a manual trigger uses: one member becomes a single run, several become one wave-gated run group. A bad cron or time zone parks that schedule with a logged error instead of stopping the loop; an inactive or removed pipeline is skipped, not enqueued.

`POST /api/v1/schedules` (operate, src/SqlFlow.ControlPlane/Api/ScheduleEndpoints.cs) takes `{repoId, members: ["recall-welllog"], cron | intervalSeconds, timezone?, enabled?, catchup?, name?, maxConcurrency?, operation?}`: exactly one of `cron` and `intervalSeconds`; `timezone` defaults to `UTC`; `enabled` to true; `catchup` to false; `name` to the first member's flow name; `maxConcurrency` bounds how many members one fire executes at once (omitted takes the product default of 4, `ScheduleDefaults.MaxConcurrency`; `0` asks for unbounded); `operation` is one of `deliver` (default), `verify`, `plan`, `known-state`, and every fire of the schedule carries it, so a verify (drift) pass is a schedule of its own next to the hourly deliver. A schedule with no members is 400 ("membership is what a fire runs"), a member that is not an active pipeline is 404, a name already used in the repo is 409, and success is 201 with `{id, nextFireUtc}`.

`POST /api/v1/schedules/{id}/run` fires a schedule now without moving its cadence: 202 with `{runId, groupId?, memberCount}` and a `Location` pointing at the run or the group; 404 for an unknown schedule; 409 when nothing is runnable (every member deactivated or `mode: manual`). `?force=true` makes every member push past its change gates; `?batch=a&batch=b` narrows the fire to members carrying those batch tags; `?chain=false` keeps the fire to this schedule without setting off the schedules chained behind it (the default carries the chain, exactly as the clock does). `POST /schedules/{id}/pause`, `POST /schedules/{id}/resume`, and `DELETE /schedules/{id}` manage the lifecycle; `GET /schedules/{id}/definition` returns the YAML behind a git-declared schedule (null for an API-created one) and `GET /schedules/{id}/plan` the members a fire runs in wave order.

## Configuration touchpoints

- Configuration section: `ControlPlane` (env form `ControlPlane__...`), documented in the table above; src/SqlFlow.ControlPlane/appsettings.json carries the non-secret defaults.
- Environment variables: `SQLFLOW_CATALOG_DB` (the default catalog connection reference), `SQLFLOW_GIT_TOKEN` and `SQLFLOW_GIT_USERNAME` (managed-sync and worker git credentials). The whole family is listed in [Environment variables and secrets](../../environment-variables.md).
- CLI: `sqlflow db sync [path] [--repo <name>] [--repo-url <url>]` runs the same `CatalogSync` a managed source runs; `sqlflow worker` runs the same node runtime the control plane hosts in-process (src/SqlFlow.Cli/Program.cs). See [`sqlflow db`](../cli/db.md) and [`sqlflow worker`](../cli/worker.md).

## Example: compose deployment

Adapted from deploy/compose/docker-compose.yml (an API-only control plane replica plus a separately scaled worker; the values come from deploy/compose/.env.example):

```yaml
services:
  controlplane:
    environment:
      ControlPlane__Catalog__ConnectionReference: "Server=mssql;Database=SqlFlowCatalog;User ID=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True"
      ControlPlane__Jwt__SigningKey: ${SQLFLOW_JWT_SIGNING_KEY}
      ControlPlane__Jwt__BootstrapSecret: ${SQLFLOW_BOOTSTRAP_SECRET}
      ControlPlane__Bootstrap__AdminUsername: ${SQLFLOW_ADMIN_USERNAME:-admin}
      ControlPlane__Bootstrap__AdminPasswordReference: ${SQLFLOW_ADMIN_PASSWORD}
      # The GUI is a separate origin in this layout.
      ControlPlane__Cors__AllowedOrigins__0: http://localhost:8081
      # Compute belongs to the worker service; this replica is API + scheduler + sync only.
      ControlPlane__Worker__Enabled: "false"
    ports:
      - "5000:8080"

  worker:
    environment:
      SQLFLOW_CATALOG_DB: "Server=mssql;Database=SqlFlowCatalog;User ID=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True"
      # Every ${env:...} reference the flows declare resolves here, on the worker; the sample flow names these six.
      PETRODB_URL: ${PETRODB_URL:-}
      OSDU_TOKEN_URL: ${OSDU_TOKEN_URL:-}
      OSDU_SCOPE: ${OSDU_SCOPE:-}
      OSDU_CLIENT_ID: ${OSDU_CLIENT_ID:-}
      OSDU_CLIENT_SECRET: ${OSDU_CLIENT_SECRET:-}
      APIM_KEY: ${APIM_KEY:-}
      SQLFLOW_WORKER_POOL: ${SQLFLOW_WORKER_POOL:-}
      SQLFLOW_GIT_TOKEN: ${SQLFLOW_GIT_TOKEN:-}
```

Register a source and trigger a run:

```bash
# Register a managed repo source holding the sample estate (operate scope)
curl -s -X POST http://localhost:5000/api/v1/repos/sources \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"recall","remoteUrl":"https://github.com/acme/osdu-flows.git","branch":"main","syncIntervalSeconds":300}'

# Plan the STAT_COMP drop without touching OSDU (202 Accepted; poll the Location header)
curl -s -X POST http://localhost:5000/api/v1/runs \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"repoId":"'$REPO_ID'","flowName":"recall-welllog","operation":"plan","values":{"logSource":"STAT_COMP"}}'
```

## See also

- [Delivery operations](../../delivery/operations.md): the delivery API, the GUI, and the runbook.
- [Worker CLI](../cli/worker.md): the standalone node runtime that drains the same run queue.
- [Control-plane CLI verbs](../cli/control-plane.md): the same API from a terminal.
- [Authentication and identity](./authentication-and-identity.md): tokens, local users, Entra SSO, roles.
- [Architecture](../../architecture.md): the platform and the delivery domain.
- [Deployment guide](../guides/deployment.md): compose and ingress layouts.
