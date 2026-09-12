# Deploying: docker compose, Kubernetes, Azure Container Apps, and external schedulers

OSDU Delivery ships as three container images with three distinct scaling behaviors:

| Image | Built from | Scales on | Behind the ingress? |
| --- | --- | --- | --- |
| `sqlflow-control-plane` | `Dockerfile` (repo root) | request load (HPA) | yes, under `/api` |
| `sqlflow-gui` | `gui/Dockerfile` | trivially (static SPA) | yes, under `/` |
| `sqlflow-worker` | `Dockerfile.worker` | queue depth (KEDA) | never (pull model, no inbound surface) |

```bash
docker build -t sqlflow-control-plane:latest .
docker build -f Dockerfile.worker -t sqlflow-worker:latest .
docker build -t sqlflow-gui:latest gui/
```

The two .NET images build from the repository root (central package management and the cross-project references span the repo; `.dockerignore` keeps `bin/`, `obj/`, `.git/`, `docs/` and the GUI trees out of that context). Both publish framework-dependent inside the Linux SDK image, so the linux-x64 native assets for LibGit2Sharp ship under `runtimes/linux-x64`, and both install `libssl3` on the Debian runtime image because LibGit2Sharp's bundled git needs OpenSSL for HTTPS remotes. Both listen on 8080 and run as the image's non-root user. The GUI image builds the SPA with Node 22 and serves it from unprivileged nginx on 8080; the API base URL is written to `/config.json` at container start (`gui/docker/40-runtime-config.sh`) from `SQLFLOW_API_BASE_URL`, so one image serves every environment.

The split matters because the two tiers have opposite traffic shapes. The control plane is an HTTP API: it scales on request load and sits behind the ingress. Workers are compute nodes that pull work from the durable run queue over outbound SQL only (plus outbound git for SHA-pinned materialization); they expose nothing, never sit behind an ingress, and scale on queue depth. `ControlPlane__Worker__Enabled=false` is the switch that keeps the two independent: it turns off the control plane's in-process worker so API replicas do API, scheduler and managed-sync work only (`WorkerOptions` in `src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs`; the default is `true`, the single-node mode).

## Local / single host: docker compose

`deploy/compose/docker-compose.yml` runs the full stack: SQL Server 2022 (Developer edition) holding the catalog, an API-only control plane, one scalable worker, and the GUI.

```bash
cd deploy/compose
cp .env.example .env    # set the secrets
docker compose up -d --build
docker compose up -d --scale worker=3   # more compute, nothing else changes
```

- GUI: `http://localhost:8081` (sign in with the bootstrap admin from `.env`)
- Control plane API: `http://localhost:5000` (OpenAPI at `/openapi/v1.json`)
- SQL Server: published on host port `14333` for host-side tooling only

On first start, bootstrap provisioning (`src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs`) provisions the catalog schema from the EF model, seeds the built-in roles, and creates the initial admin from `.env`. Bootstrap is idempotent: restarts converge to the same state and never reset an existing admin's password. It does not create the catalog database itself: `ControlPlane:Bootstrap:AllowCreate` defaults to `false`, so against a missing database it logs a critical `Bootstrap provisioning refused` error and stops, with readiness staying red. The SQL Server container starts with no `SqlFlowCatalog` database, so provision it once, either by adding `ControlPlane__Bootstrap__AllowCreate: "true"` to the `controlplane` service for the first start, or from the host with `sqlflow db migrate --create --db "Server=localhost,14333;Database=SqlFlowCatalog;User ID=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=True"` (see [db](../cli/db.md)).

### Required .env values

From `deploy/compose/.env.example`; the compose file fails fast (`:?` expansion) when a required one is missing:

| Variable | Required | Meaning |
| --- | --- | --- |
| `MSSQL_SA_PASSWORD` | yes | SQL Server `sa` password, also used in the stack's connection strings. SQL Server enforces complexity. |
| `SQLFLOW_JWT_SIGNING_KEY` | yes | HS256 signing key for control-plane tokens, at least 32 bytes of randomness. |
| `SQLFLOW_BOOTSTRAP_SECRET` | yes | Break-glass bootstrap secret, at least 32 bytes. Used to provision users, then rotate or remove. |
| `SQLFLOW_ADMIN_PASSWORD` | yes | The initial admin's password (at least 12 characters). |
| `SQLFLOW_ADMIN_USERNAME` | no | Defaults to `admin`. |
| `SQLFLOW_WORKER_POOL` | no | Pool(s) the worker service serves; empty means untargeted runs only. |
| `SQLFLOW_GIT_TOKEN` | no | Token for private git remotes (SHA-pinned materialization). |
| `PETRODB_URL`, `OSDU_TOKEN_URL`, `OSDU_SCOPE`, `OSDU_CLIENT_ID`, `OSDU_CLIENT_SECRET`, `APIM_KEY` | per estate | The `${env:...}` references the sample flow (`samples/recall-welllog`) declares: the OSDU endpoint, token URL, scope, client id and secret, and the APIM subscription key. A real estate names its own references. `plan` runs need none, so they can stay empty until a target exists. |

The 32-byte minimums for the signing key and bootstrap secret are enforced at startup by `ControlPlaneOptions.Validate()`; a shorter value stops the control plane from starting with a clear error.

### How the compose layout is wired

- The control plane runs API-only: `ControlPlane__Worker__Enabled: "false"`. Compute belongs to the separate `worker` service, which you scale with `--scale worker=N`.
- The GUI is a separate origin in this layout (`http://localhost:8081` vs `http://localhost:5000`), so the control plane carries `ControlPlane__Cors__AllowedOrigins__0: http://localhost:8081` and the GUI reads `SQLFLOW_API_BASE_URL: http://localhost:5000`. Production ingress layouts route both under one host and drop the CORS configuration entirely.
- The control plane container health check probes `/health/live`. Liveness has no dependencies; readiness (`/health/ready`) checks the catalog database. Both endpoints are exempt from the rate limiter.
- Every `${env:...}` reference the flows declare resolves on the worker service from `.env`; the sample flow names its OSDU endpoint, token URL, scope, client id and secret, and APIM subscription key. The control plane carries none of them.

## The worker image

`Dockerfile.worker` publishes `src/SqlFlow.Cli/SqlFlow.Cli.csproj` and runs it through `deploy/docker/worker-entrypoint.sh`, which composes the `sqlflow worker` invocation from the environment. The catalog connection is the CLI default (`${env:SQLFLOW_CATALOG_DB}`), so it never appears on the command line or in `ps` output. Configuration is environment-only, matching the worker's "credentials live on the node" model:

| Variable | Required | Maps to | Meaning |
| --- | --- | --- | --- |
| `SQLFLOW_CATALOG_DB` | yes | the CLI's default `--db` reference | Catalog database connection string. |
| `SQLFLOW_WORKER_POOL` | no | `--pool` | Comma-separated pools this node serves; empty means untargeted runs only. |
| `SQLFLOW_WORKER_POLL_SECONDS` | no | `--poll-seconds` | Queue poll cadence; the CLI default is 5. |
| `SQLFLOW_WORKER_DRAIN_SECONDS` | no | `--drain-seconds` | How long a stopping node finishes the runs it already claimed; the CLI default is 540. Must stay below the platform's termination grace period (see below). |
| `SQLFLOW_GIT_TOKEN` (optionally with `SQLFLOW_GIT_USERNAME`) | no | read by git materialization (`src/SqlFlow.Node/GitMaterializer.cs`) | Token for private git remotes; the username is for hosts that pair the token with one (Bitbucket app passwords or `x-token-auth`), GitHub needs the token alone. |
| every `${env:...}` reference the flows use | per estate | secret resolver | Drop locations and OSDU credentials resolve on the node, never in the control plane. |

The underlying CLI command is `sqlflow worker [--db <conn-ref>] [--poll-seconds N] [--pool a,b] [--drain-seconds N]` (see [worker](../cli/worker.md)). The queue's atomic claim makes any number of concurrent workers safe.

### Scale-in must not sever runs: the grace period is not optional

Workers are scaled in routinely, and the platform picks its victims blindly: a replica executing a long delivery is as likely to be reclaimed as an idle one. The worker handles SIGTERM by draining (it stops claiming and lets the runs it already holds finish and record their outcomes), but that only works if the orchestrator actually waits.

**Set the termination grace period above `--drain-seconds` on every worker workload.** Both Kubernetes and Container Apps default to 30 seconds, which is shorter than most real loads:

| Platform | Setting | Estate value |
| --- | --- | --- |
| Kubernetes | `spec.template.spec.terminationGracePeriodSeconds` (`deploy/k8s/worker-pool.yaml`) | 600 |
| Azure Container Apps | `properties.template.terminationGracePeriodSeconds` (`deploy/bicep/worker.bicep`, or `az containerapp update --termination-grace-period`) | 600 |

Leaving it at the default does not disable the drain, it truncates it: the worker begins draining, the platform kills it 30 seconds later, and every run that needed longer is severed with no outcome recorded. Each severed run is then requeued by the orphan reaper, which **consumes one of its three execution attempts** (`RunQueueStore.MaxExecutionAttempts`). A run caught by three scale-ins is failed permanently and reported as though the run itself were at fault. This is the single most common way a healthy flow acquires a mysterious interruption failure.

Note that `az containerapp update --image` (what most deploy scripts run) does **not** set this property. It has to be applied through the bicep or an explicit `--termination-grace-period` update, and it survives subsequent image swaps once set.

## Kubernetes: deploy/k8s

Prerequisites: an ingress controller (the annotations assume ingress-nginx), cert-manager or another TLS source, and [KEDA](https://keda.sh) for worker autoscaling.

```bash
kubectl apply -f deploy/k8s/namespace.yaml
# create the real secret (see secrets.example.yaml for the required keys), then:
kubectl apply -f deploy/k8s/controlplane.yaml
kubectl apply -f deploy/k8s/gui.yaml
kubectl apply -f deploy/k8s/ingress.yaml
kubectl apply -f deploy/k8s/worker-pool.yaml
```

The catalog database must exist before the control plane starts (bootstrap initialises an existing empty database and refuses to create one; provision it with `sqlflow db migrate --create`), and the control plane must have provisioned the schema before the worker's ScaledObject is created, since its scale query reads catalog tables.

### Secrets

`deploy/k8s/secrets.example.yaml` documents the keys every deployment references (create the real secret out of band, never commit values):

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: sqlflow-secrets
  namespace: sqlflow
type: Opaque
stringData:
  catalog-connection: "Server=your-sql-server;Database=SqlFlowCatalog;User ID=sqlflow;Password=CHANGE_ME;TrustServerCertificate=True"
  jwt-signing-key: "CHANGE_ME_32+_RANDOM_BYTES________"
  bootstrap-secret: "CHANGE_ME_32+_RANDOM_BYTES________"
  admin-password: "CHANGE_ME_12+_CHARS"
  git-token: ""
  osdu-client-id: "CHANGE_ME"
  osdu-client-secret: "CHANGE_ME"
  apim-key: "CHANGE_ME"
```

Secrets stay on the tier that uses them: the control plane gets the catalog connection and JWT material; workers additionally get the git token and every `${env:...}` reference their pool's flows use. Nothing data-plane ever passes through the control plane. `controlplane.yaml` also reads two optional keys, `entra-tenant-id` and `entra-client-id`, which turn on "Sign in with Microsoft" when both are present and are simply absent otherwise.

### One host, path split, no CORS

`deploy/k8s/ingress.yaml` routes `/api` and `/openapi` to the control plane service and `/` to the GUI service under a single host. The SPA is same-origin with the API, so no CORS configuration exists anywhere; the GUI deployment sets `SQLFLOW_API_BASE_URL=""`, which means "same origin". Add `ControlPlane__Cors__AllowedOrigins__0` only if the GUI is served from another host.

### Control plane: API-only replicas on an HPA

`deploy/k8s/controlplane.yaml` runs 2 to 10 stateless replicas scaled on CPU (70% target). Multi-replica is safe by construction: token validation is stateless (shared signing key), and the scheduler, managed sync, and run queue all claim work atomically in the catalog. Key environment:

- `ControlPlane__Worker__Enabled=false`: compute is the worker deployments' job.
- `ControlPlane__Proxy__Enabled=true` plus `ControlPlane__Proxy__KnownNetworks__0` set to your cluster's ingress/pod CIDR: `X-Forwarded-For`/`X-Forwarded-Proto` are honored only from the listed proxies, so clients cannot spoof their address, and rate limiting and login throttling key on the real client instead of the ingress IP. `ControlPlaneOptions.Validate()` refuses to start when proxy support is enabled but no `KnownNetworks` or `KnownProxies` are set, and rejects malformed CIDRs and IPs by name.
- `ControlPlane__AzureAd__AllowedTenantIds__0` and `ControlPlane__AzureAd__ClientId` from the optional secret keys, with `ControlPlane__AzureAd__DefaultRole=viewer`: Entra SSO auto-enables when both are present. The SPA registration's redirect URI must be the GUI origin; add further `AllowedTenantIds__1`, `__2` entries to trust more tenants.
- Liveness probes `/health/live` (no dependencies); readiness probes `/health/ready` (catalog reachability).

### Workers: one Deployment plus one KEDA ScaledObject per pool

`deploy/k8s/worker-pool.yaml` is the template for one pool. Copy it per pool, setting `SQLFLOW_WORKER_POOL` and the pool predicates in the ScaledObject query. A `TriggerAuthentication` (`sqlflow-catalog-auth`) hands KEDA's mssql scaler the `catalog-connection` secret; the ScaledObject scales between `minReplicaCount: 0` and `maxReplicaCount: 10`, polls every 15 seconds, keeps a warm worker for 300 seconds after the work drains (`cooldownPeriod`), and targets one replica per unit of the query's result.

The query returns the greatest of three catalog-driven terms, so the GUI's fleet controls steer the pool without the control plane ever calling the orchestrator (it only writes catalog rows):

1. Demanded work: the claimable queued runs for the pool (mirroring the claim's own gates: wave order inside a run group, the group's max concurrency, one execution per pipeline) divided by the node's run concurrency (4, `RunWorker.DefaultMaxConcurrentRuns`), plus the nodes that reported `BusyRuns > 0` within the last 60 seconds. Counting busy nodes keeps the target honest while the fleet is executing; a queued-only count reads zero the moment the batch is claimed, and KEDA would then scale in pods carrying live runs.
2. The always-on floor: `[catalog].[WorkerPool].[MinReplicas]` for the pool.
3. An active manual override: `[ManualReplicas]` while `[ManualUntilUtc]` is in the future (a spawn-from-zero or temporary scale-up that reverts on its own).

A pool with no `WorkerPool` row still scales to zero once nothing is queued and no node is busy, so an idle pool costs nothing. Because runs are pinned to the repo's synced commit at enqueue, a cold-started worker needs only its environment: it claims, materializes the pinned commit from git, executes, and reports back. The worker Deployment mounts one env entry per `${env:...}` reference the pool's flows use; the template covers the sample flow:

```yaml
env:
  - name: SQLFLOW_CATALOG_DB
    valueFrom:
      secretKeyRef: { name: sqlflow-secrets, key: catalog-connection }
  - name: SQLFLOW_WORKER_POOL
    value: ""          # empty = untargeted runs only; set the pool name(s) for a pooled copy
  - name: SQLFLOW_GIT_TOKEN
    valueFrom:
      secretKeyRef: { name: sqlflow-secrets, key: git-token }
  - name: PETRODB_URL
    value: "https://your-osdu-host"
  - name: OSDU_TOKEN_URL
    value: "https://login.microsoftonline.com/your-tenant/oauth2/v2.0/token"
  - name: OSDU_SCOPE
    value: "api://your-osdu-app/.default"
  - name: OSDU_CLIENT_ID
    valueFrom:
      secretKeyRef: { name: sqlflow-secrets, key: osdu-client-id }
  - name: OSDU_CLIENT_SECRET
    valueFrom:
      secretKeyRef: { name: sqlflow-secrets, key: osdu-client-secret }
  - name: APIM_KEY
    valueFrom:
      secretKeyRef: { name: sqlflow-secrets, key: apim-key }
```

The template sets `terminationGracePeriodSeconds: 600` so an in-flight run can finish on scale-down; an interrupted run is requeued anyway.

### Placement

Workers must run where they can reach the drops and the OSDU endpoint their pool's flows touch; that is the point of pools. A cloud cluster's workers serve cloud-reachable drops. An on-prem pool means workers running on-prem (compose, systemd, or a local cluster) pointed at the same catalog database and registered under that pool name.

## Azure Container Apps: the full estate with Bicep

`deploy/bicep/main.bicep` deploys everything into one resource group: Log Analytics plus the Container Apps environment, a Key Vault (RBAC authorization) holding every secret, an Azure SQL catalog database, optionally the Entra app registration users sign in with, and the three apps composed from one template per tier. Each tier template (`control-plane.bicep`, `worker.bicep`, `gui.bicep`) also deploys standalone into an existing environment and Key Vault, which is how a second worker pool is added (`-p name=sqlflow-worker-<pool> pool=<pool>`).

```bash
az group create -n osdu-delivery -l <region>
az acr create -g osdu-delivery -n <registry> --sku Basic
az acr build -r <registry> -t sqlflow-control-plane:latest .
az acr build -r <registry> -t sqlflow-worker:latest -f Dockerfile.worker .
az acr build -r <registry> -t sqlflow-gui:latest gui/

az deployment group create -g osdu-delivery -f deploy/bicep/main.bicep \
  -p acrName=<registry> \
     controlPlaneImage=<registry>.azurecr.io/sqlflow-control-plane:latest \
     workerImage=<registry>.azurecr.io/sqlflow-worker:latest \
     guiImage=<registry>.azurecr.io/sqlflow-gui:latest \
     sqlAdminPassword='<complex password>' \
     jwtSigningKey="$(openssl rand -base64 48)" \
     adminPassword='<initial admin password, 12+ chars>'
```

The template creates the catalog database (empty), so first start behaves like a provisioned compose stack: bootstrap provisions the catalog schema and creates the initial admin (`adminUsername`/`adminPassword`), and the `guiUrl` output is sign-in ready. The other outputs: `controlPlaneBaseUrl` (for CLI remotes via `SQLFLOW_URL` and for any external scheduler, see below), `controlPlaneIdentityClientId` and `workerIdentityClientId` (grant them access to the drops, secrets and OSDU credentials the flows touch), `sqlServerFqdn`, `keyVaultUri`, and the Entra outputs `entraClientId`, `entraRedirectUri`, `entraAssignmentReminder`, `entraForeignTenantReminder`.

How the Kubernetes layout maps onto Container Apps:

- **Two origins instead of a path split.** Every Container App has its own ingress FQDN, so `main.bicep` computes both hostnames up front, points the GUI's `SQLFLOW_API_BASE_URL` at the control plane URL, and CORS-lists the GUI origin on the control plane (`corsAllowedOrigins`). Restoring the one-host layout means putting Front Door or Application Gateway in front of both apps, then blanking both settings.
- **The control plane runs API-only** (`workerEnabled: false` in the module call), scaled `controlPlaneMinReplicas..controlPlaneMaxReplicas` (defaults 1 and 3); compute belongs to the worker app, exactly as in the k8s split.
- **KEDA is built in.** `worker.bicep` scales `0..maxReplicas` (default 10) on the same catalog query as `worker-pool.yaml` (pool predicates included when `pool` is set; `maxConcurrentRunsPerReplica`, default 4, must match the node's concurrency), and keeps `terminationGracePeriodSeconds: 600` so an in-flight run can finish on scale-in. The scaler is go-mssqldb, not .NET SqlClient, and does not strip the quotes ADO.NET puts around a password, so `main.bicep` writes a second, URL-form connection string as the `sqlflow-catalog-db-scaler` vault secret for the scale rule alone (`scalerConnectionSecretName` on `worker.bicep`).
- **Secrets live in Key Vault, read by user-assigned managed identity.** The deployment writes `sqlflow-catalog-db`, `sqlflow-jwt-signing-key`, `sqlflow-admin-password`, the scaler secret and (when `gitToken` is given) `sqlflow-git-token`; each app identity gets Key Vault Secrets User plus AcrPull when `acrName` names a same-group registry. The deploying principal therefore needs to create role assignments (Owner or User Access Administrator) and to write vault secrets (Key Vault Secrets Officer). The same identities resolve `${keyvault:...}` references at run time (`SQLFLOW_AZURE_AUTH=mi`, `AZURE_CLIENT_ID`). The references a pool's flows use land on the worker only, via `workerFlowEnv`: one `{ name, secretName }` entry per reference, naming an existing vault secret to wire to that environment variable (for example `[{ name: 'OSDU_CLIENT_SECRET', secretName: 'osdu-client-secret' }]`). Flow credentials are created in the vault out of band and never pass through the template.
- **The catalog is an Azure SQL database** (`sqlDatabaseSku`, default S1: the catalog is metadata, the run queue and the delivery ledger, modest but polled continuously, so serverless auto-pause is the wrong shape). The ADO.NET connection string exists only as the `sqlflow-catalog-db` vault secret. The server admits Azure-service traffic (the consumption plan has no fixed egress address for a narrower rule); the hardening path is a VNet-integrated environment with a private endpoint to SQL and least-privilege or Entra credentials in place of the SQL admin, all behind that one secret.
- **Proxy trust stays off in the estate.** Container Apps ingress terminates TLS in front of the app, and the platform's forwarding hops have no contractual CIDR on the consumption plan, so per-client rate limiting keys on the ingress hop. `main.bicep` does not expose the setting; for a VNet-integrated environment whose infrastructure subnet is known, deploy `control-plane.bicep` with `proxyKnownNetworks` to key on real client addresses.
- **Bring your own SQL and network.** `existingSqlServer=<host[,port]>` skips the Azure SQL server and points the catalog connection at a server you already run; an address without a port gets `,1433` (a Managed Instance public endpoint would be `host,3342`). No template creates the database on that server, and bootstrap refuses to, so create `SqlFlowCatalog` there first or provision it with `sqlflow db migrate --create` against the connection string in the vault. `infrastructureSubnetId` VNet-integrates the environment (consumption architecture: an undelegated subnet of at least /23), the route to a VNet-only Managed Instance: its default FQDN resolves to the private ILB address, and the default `AllowVnetInBound` rule admits 1433 unless a custom NSG rule denies it.
- **Private git remotes.** `gitToken` lands in Key Vault and reaches managed sync (control plane) and materialization (workers) as `SQLFLOW_GIT_TOKEN`. Hosts that pair the token with a username (Bitbucket app passwords or `x-token-auth` repository tokens) also set `gitUsername`, which arrives as `SQLFLOW_GIT_USERNAME`; GitHub needs the token alone.
- **Entra sign-in as code.** With `provisionEntraApp` on (the default), `entra-app.bicep` creates the app registration, its enterprise application with assignment required, and, when `azureAdAllowedGroupObjectId` is given, the assignment of that group to the `SqlFlow.User` role, through the Microsoft Graph Bicep extension (`bicepconfig.json`). The deploying principal needs directory write access (Application Administrator or `Application.ReadWrite.All`), and assigning a group needs Entra ID P1. With no group assigned nobody can use SSO until members are assigned in the enterprise application; local username/password sign-in works regardless. `azureAdAdditionalAllowedTenantIds` makes the registration multi-tenant, and each named tenant's admin must consent and assign its own users. Turn `provisionEntraApp` off for local sign-in only, or to point at a registration you manage through `azureAdAllowedTenantIds` plus `azureAdClientId`.

## Azure: the control plane alone, and triggering from an external scheduler

OSDU Delivery runs under Azure Data Factory (or any scheduler) as a thin trigger, not as a container booted per run. The model is an always-on control plane that a small pipeline of Web Activities calls: authenticate, trigger, poll, fail on failure. Nothing is provisioned per run; a trigger is a sub-second authenticated call. No pipeline definition ships in `deploy/`; the endpoints in step 3 are the whole contract. The steps below deploy the single-app mode into an existing environment. On the full estate above, the image, app and catalog schema are already in place: register your repos (step 2's post-deploy notes) and continue at step 3 with the estate's `controlPlaneBaseUrl` output.

### 1. Build and push the image

```bash
docker build -t <registry>.azurecr.io/sqlflow-control-plane:latest .
az acr login -n <registry>
docker push <registry>.azurecr.io/sqlflow-control-plane:latest
```

### 2. Deploy the control plane as a Container App

Put the two secrets in Key Vault first (the names are the template defaults, `catalogConnectionSecretName` and `jwtSigningKeySecretName`):

```bash
az keyvault secret set --vault-name <kv> --name sqlflow-catalog-db      --value "<ADO.NET connection string>"
az keyvault secret set --vault-name <kv> --name sqlflow-jwt-signing-key --value "<a random string of >= 32 bytes>"
```

Then deploy `deploy/bicep/control-plane.bicep`:

```bash
az deployment group create -g <rg> -f deploy/bicep/control-plane.bicep \
  -p managedEnvironmentId=<container-apps-env-id> \
     image=<registry>.azurecr.io/sqlflow-control-plane:latest \
     keyVaultName=<kv> \
     acrLoginServer=<registry>.azurecr.io
```

The template creates a user-assigned managed identity, grants it the Key Vault Secrets User role on the vault (and AcrPull when `acrName` names a same-group registry), and wires the app to pull the secrets at startup; no secret value appears in the template or app configuration. That same identity resolves `${keyvault:...}` references and cloud-storage credentials at run time (the template sets `SQLFLOW_AZURE_AUTH=mi` and `AZURE_CLIENT_ID`). The catalog connection lands in `SQLFLOW_CATALOG_DB`, which the control plane's default `ControlPlane:Catalog:ConnectionReference` (`${env:SQLFLOW_CATALOG_DB}`) reads directly. Standalone, the in-process worker stays on (`workerEnabled` defaults to `true`) and no CORS origin is listed. Optional parameters add the bootstrap admin (`bootstrapAdminPasswordSecretName`, `bootstrapAdminUsername`), a git token (`gitTokenSecretName`, `gitUsername`), proxy trust (`proxyKnownNetworks`), and Entra SSO (`azureAdAllowedTenantIds`, `azureAdClientId`, `azureAdDefaultRole`). Probes hit `/health/live` and `/health/ready`; `minReplicas` defaults to 1 so a trigger never waits on a cold start, `maxReplicas` to 3.

Outputs:

- `controlPlaneBaseUrl`: the base URL every call below is made against.
- `identityClientId` and `identityPrincipalId`: grant the identity access to the drops and any flow secrets, since the control plane's in-process worker resolves every credential from its own environment.

After deploy:

- Provision the catalog once against the connection string in the vault: `sqlflow db migrate --create --db "<connection>"` (see [db](../cli/db.md)). Bootstrap migrates an existing catalog on every start but refuses to create the database.
- Register git repos as managed sources via `POST /api/v1/repos/sources` (body: `name`, `remoteUrl`, optional `branch` defaulting to `main`, optional `syncIntervalSeconds` defaulting to 300, optional `enabled` defaulting to true, optional `credentialReference`, `credentialUsername` and `excludedFlowPaths`) so the catalog stays synced from git. `credentialReference` must be a `${env:...}` or `${keyvault:...}` reference; a raw token is rejected with 400. Without one, managed sync fetches with the control plane's own `SQLFLOW_GIT_TOKEN`.

### 3. The calls a scheduler makes

Each is one Web Activity; poll with an Until loop.

1. **Authenticate.** The durable credential for automation is a personal access token: a service account creates one under `POST /api/v1/me/tokens` (its scopes capped to the account's own; see [Authentication and identity](../concepts/authentication-and-identity.md)), and the pipeline reads it from a Key Vault linked service. The break-glass alternative is `POST /api/v1/auth/token` with `{ "secret": "<bootstrap secret>", "scopes": ["operate"] }`, mapped only when `ControlPlane:Jwt:BootstrapSecret` is set; it accepts the scopes `read`, `operate`, `author` and `admin` (default `read`) and answers `{ "accessToken", "tokenType": "Bearer", "expiresIn" }`.
2. **Trigger.** `POST /api/v1/runs` (scope `operate`) with `{ "repoId": "<repo id from GET /api/v1/repos>", "flowName": "<flow>" }`, plus the flow's parameter values under `values` (for the sample flow, `{ "logSource": "STAT_COMP" }`). The reply is 202 `{ "runId", "status": "queued" }` with a `Location` of `/api/v1/runs/{runId}`; an unknown or deactivated flow is 404. When the preparing side has just finished a drop, the manifest notification is the better trigger: `POST /api/v1/delivery/submissions` (scope `operate`) with `{ "flow": "<flow name>", "drop": "<drop location>", "parameters": { ... } }`, naming the flow by `pipelineId`, by `repoId` plus `flow`, or by `flow` alone when the name is unique in the estate (optional `force` and `pool`). It queues a `deliver` run for that drop and answers 202 `{ "runId", "pipelineId", "flowName", "status" }` (`src/SqlFlow.ControlPlane/Api/DeliveryEndpoints.cs`). A source with a handful of metadata records and no payload files can send the records themselves to the same route under `records` instead of `drop`, with its own `submissionId` as the idempotency key; see [Submitting records](../../delivery/submitting-records.md).
3. **Wait.** Poll `GET /api/v1/runs/{runId}` (scope `read`) until `status` is `succeeded`, `failed`, or `cancelled` (`skipped` is possible for a member of a run group whose upstream failed). The detail carries `success`, `error`, the record counts (`recordsPlanned`, `recordsDelivered`, `recordsHeld`, `recordsFailed`, `recordsSkipped`) and `resultSubmissionId` for the delivery ledger.
4. **Fail on failure.** Fail the pipeline when the terminal status is not `succeeded`, so a failed flow surfaces as a failed pipeline run.

### Routing and production notes

- **Pool routing**: add `"pool": "<pool>"` to the trigger body to route the run to a node serving that pool; omit it for any node.
- **Version pinning**: add `"commitSha": "<sha>"` to `POST /api/v1/runs` to run an exact committed version. The value must be a 4 to 64 character hexadecimal git object id; otherwise the API returns 400 with `commitSha must be a 4- to 64-character hexadecimal git object id (or omitted to pin to the last synced commit).` (`src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs`). Omitting it pins the run to the repo's last synced commit.
- **Operation**: `POST /api/v1/runs` defaults to `deliver`; `"operation": "verify"`, `"plan"` or `"known-state"` runs the other delivery operations, `"drop"` names one drop location, and `"force": true` re-sends unchanged records (see [delivery](../cli/delivery.md) for what each operation does).
- **Token lifetime**: a bootstrap token expires after `ControlPlane:Jwt:AccessTokenMinutes` (default 720, valid range 1 to 1440) and does not renew. If a flow can outlive that, fetch the token inside the polling loop or use a personal access token, which carries its own lifetime.
- **Secrets in the scheduler**: source the token or bootstrap secret from a Key Vault linked service rather than a parameter value.

## See also

- [Control plane](../concepts/control-plane.md)
- [Worker command](../cli/worker.md)
- [db command](../cli/db.md)
- [Authentication and identity](../concepts/authentication-and-identity.md)
- [Environment variables and secrets](../../environment-variables.md)
- [Delivery operations](../../delivery/operations.md)
