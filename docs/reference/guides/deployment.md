---
id: guide-deployment
title: "Deploying: docker compose, Kubernetes, and ADF integration"
type: guide
summary: Deploy the control plane, workers, and GUI with docker compose or Kubernetes (KEDA), and trigger flows from Azure Data Factory Web Activities.
keywords:
  - docker compose
  - kubernetes
  - keda
  - hpa
  - adf
  - web activities
  - images
  - worker pools
  - ingress
related:
  - concept-control-plane
  - cli-worker
  - concept-authentication-and-identity
sourceRefs:
  - deploy/README.md
  - deploy/compose/docker-compose.yml
  - deploy/compose/.env.example
  - Dockerfile.worker
  - deploy/docker/worker-entrypoint.sh
  - deploy/k8s/controlplane.yaml
  - deploy/k8s/worker-pool.yaml
  - deploy/k8s/gui.yaml
  - deploy/k8s/ingress.yaml
  - deploy/k8s/secrets.example.yaml
  - deploy/bicep/control-plane.bicep
  - deploy/adf/SqlFlowTriggerFlow.pipeline.json
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs
  - src/SqlFlow.Cli/Program.cs
  - docs/adf-dropin.md
---

# Deploying: docker compose, Kubernetes, and ADF integration

SQLFlow ships as three container images with three distinct scaling behaviors:

| Image | Built from | Scales on | Behind the ingress? |
|---|---|---|---|
| `sqlflow-control-plane` | `Dockerfile` (repo root) | request load (HPA) | yes, under `/api` |
| `sqlflow-gui` | `gui/Dockerfile` | trivially (static SPA) | yes, under `/` |
| `sqlflow-worker` | `Dockerfile.worker` | queue depth (KEDA) | never (pull model, no inbound surface) |

```bash
docker build -t sqlflow-control-plane:latest .
docker build -f Dockerfile.worker -t sqlflow-worker:latest .
docker build -t sqlflow-gui:latest gui/
```

The split matters because the two tiers have opposite traffic shapes. The control plane is an HTTP API: it scales on request load and sits behind the ingress. Workers are compute nodes that pull work from the durable run queue over outbound SQL only; they expose nothing, never sit behind an ingress, and scale on queue depth. `ControlPlane__Worker__Enabled=false` is the switch that keeps the two independent: it turns off the control plane's in-process worker so API replicas do API work only (see `src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs`, `WorkerOptions`; the default is `true`, which is the single-node mode).

## Local / single host: docker compose

`deploy/compose/docker-compose.yml` runs the full stack: SQL Server (catalog plus a demo sink), an API-only control plane, one scalable worker, and the GUI.

```bash
cd deploy/compose
cp .env.example .env    # set the secrets
docker compose up -d --build
docker compose up -d --scale worker=3   # more compute, nothing else changes
```

- GUI: `http://localhost:8081` (sign in with the bootstrap admin from `.env`)
- Control plane API: `http://localhost:5000` (OpenAPI at `/openapi/v1.json`)
- SQL Server: published on host port `14333` for host-side tooling only

On first start, bootstrap provisioning creates the catalog database, applies EF migrations, seeds the built-in roles, and creates the initial admin from `.env`. Bootstrap is idempotent: restarts converge to the same state and never reset an existing admin's password.

### Required .env values

From `deploy/compose/.env.example`; the compose file fails fast (`:?` expansion) when a required one is missing:

| Variable | Required | Meaning |
|---|---|---|
| `MSSQL_SA_PASSWORD` | yes | SQL Server `sa` password, also used in the stack's connection strings. SQL Server enforces complexity. |
| `SQLFLOW_JWT_SIGNING_KEY` | yes | HS256 signing key for control-plane tokens, at least 32 bytes of randomness. |
| `SQLFLOW_BOOTSTRAP_SECRET` | yes | Break-glass bootstrap secret, at least 32 bytes. Used to provision users, then rotate or remove. |
| `SQLFLOW_ADMIN_PASSWORD` | yes | The initial admin's password (at least 12 characters). |
| `SQLFLOW_ADMIN_USERNAME` | no | Defaults to `admin`. |
| `SQLFLOW_WORKER_POOL` | no | Pool(s) the worker service serves; empty means untargeted runs only. |
| `SQLFLOW_GIT_TOKEN` | no | Token for private git remotes (SHA-pinned materialization). |

The 32-byte minimums for the signing key and bootstrap secret are enforced at startup by `ControlPlaneOptions.Validate()`; a shorter value stops the control plane from starting with a clear error.

### How the compose layout is wired

- The control plane runs API-only: `ControlPlane__Worker__Enabled: "false"`. Compute belongs to the separate `worker` service, which you scale with `--scale worker=N`.
- The GUI is a separate origin in this layout (`http://localhost:8081` vs `http://localhost:5000`), so the control plane carries `ControlPlane__Cors__AllowedOrigins__0: http://localhost:8081` and the GUI reads `SQLFLOW_API_BASE_URL: http://localhost:5000`. Production ingress layouts route both under one host and drop the CORS configuration entirely.
- The control plane container health check probes `/health/live`. Liveness has no dependencies; readiness (`/health/ready`) probes the catalog database via an EF DbContext check. Both endpoints are exempt from the rate limiter.
- The demo flows reference their sink via `${env:SQLFlowSinkConStr}`, so both the control plane and the worker carry that variable.

## The worker image

`Dockerfile.worker` publishes `src/SqlFlow.Cli/SqlFlow.Cli.csproj` (framework-dependent, inside the Linux SDK image so the linux-x64 native assets for LibGit2Sharp and the DuckDB reader ship under `runtimes/linux-x64`) and installs `libssl3` on the Debian runtime image because LibGit2Sharp's bundled native git needs OpenSSL for HTTPS remotes.

The entrypoint, `deploy/docker/worker-entrypoint.sh`, composes the `sqlflow worker` invocation from the environment. The catalog connection is the CLI default (`${env:SQLFLOW_CATALOG_DB}`), so it never appears on the command line or in `ps` output. Configuration is environment-only, matching the worker's "credentials live on the node" model:

| Variable | Required | Maps to | Meaning |
|---|---|---|---|
| `SQLFLOW_CATALOG_DB` | yes | the CLI's default `--db` reference | Catalog database connection string. |
| `SQLFLOW_WORKER_POOL` | no | `--pool` | Comma-separated pools this node serves; empty means untargeted runs only. |
| `SQLFLOW_WORKER_POLL_SECONDS` | no | `--poll-seconds` | Queue poll cadence; the CLI default is 5. |
| `SQLFLOW_GIT_TOKEN` | no | (read by git materialization) | Token for private git remotes. |
| every `${env:...}` reference the flows use | per estate | secret resolver | Source and target connection strings resolve on the node, never in the control plane. |

The underlying CLI command is `sqlflow worker [--db <conn-ref>] [--poll-seconds N] [--pool a,b]` (see `src/SqlFlow.Cli/Program.cs`). The queue's atomic claim makes any number of concurrent workers safe.

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
  sink-connection: "Server=your-sql-server;Database=SqlFlowDemo;User ID=sqlflow;Password=CHANGE_ME;TrustServerCertificate=True"
```

Secrets stay on the tier that uses them: the control plane gets the catalog connection and JWT material; workers additionally get the git token and every `${env:...}` connection their pool's flows reference. Nothing data-plane ever passes through the control plane.

### One host, path split, no CORS

`deploy/k8s/ingress.yaml` routes `/api` and `/openapi` to the control plane service and `/` to the GUI service under a single host. The SPA is same-origin with the API, so no CORS configuration exists anywhere; the GUI deployment sets `SQLFLOW_API_BASE_URL=""`, which means "same origin". Add `ControlPlane__Cors__AllowedOrigins__0` only if the GUI is served from another host.

### Control plane: API-only replicas on an HPA

`deploy/k8s/controlplane.yaml` runs 2 to 10 stateless replicas scaled on CPU (70% target). Multi-replica is safe by construction: token validation is stateless (shared signing key), and the scheduler, managed sync, and run queue all claim work atomically in the catalog. Key environment:

- `ControlPlane__Worker__Enabled=false`: compute is the worker deployments' job.
- `ControlPlane__Proxy__Enabled=true` plus `ControlPlane__Proxy__KnownNetworks__0` set to your cluster's ingress/pod CIDR: `X-Forwarded-For`/`X-Forwarded-Proto` are honored only from the listed proxies, so clients cannot spoof their address, and rate limiting and login throttling key on the real client instead of the ingress IP. `ControlPlaneOptions.Validate()` refuses to start when proxy support is enabled but no `KnownNetworks` or `KnownProxies` are set, and rejects malformed CIDRs and IPs by name.
- Liveness probes `/health/live` (no dependencies); readiness probes `/health/ready` (catalog reachability).

### Workers: one Deployment plus one KEDA ScaledObject per pool

`deploy/k8s/worker-pool.yaml` is the template for one pool. Copy it per pool, setting `SQLFLOW_WORKER_POOL` and the ScaledObject query's `TargetPool` predicate. KEDA's mssql scaler counts queued runs and scales the Deployment 1:1 with queue depth:

```yaml
apiVersion: keda.sh/v1alpha1
kind: ScaledObject
metadata:
  name: sqlflow-worker-default
  namespace: sqlflow
spec:
  scaleTargetRef:
    name: sqlflow-worker-default
  minReplicaCount: 0   # scale to zero when the queue is dry
  maxReplicaCount: 10
  cooldownPeriod: 300  # keep a warm worker for five minutes after the queue drains
  pollingInterval: 15
  triggers:
    - type: mssql
      metadata:
        # For a pooled copy of this manifest change the predicate to: [TargetPool] = '<pool-name>'.
        query: "SELECT COUNT(*) FROM [catalog].[Run] WHERE [Status] = 'queued' AND [TargetPool] IS NULL"
        targetValue: "1"
      authenticationRef:
        name: sqlflow-catalog-auth
```

`minReplicaCount: 0` means an idle pool costs nothing. Because runs are pinned to the repo's synced commit at enqueue, a cold-started worker needs only its environment: it claims, materializes the pinned commit from git, executes, and reports back. The worker Deployment mounts one env entry per `${env:...}` connection reference the pool's flows use:

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
  - name: SQLFlowSinkConStr
    valueFrom:
      secretKeyRef: { name: sqlflow-secrets, key: sink-connection }
```

The template sets `terminationGracePeriodSeconds: 600` so an in-flight run can finish on scale-down; an interrupted run is requeued anyway.

### Placement

Workers must run where they can reach the data their pool's flows touch; that is the point of pools. A cloud cluster's workers serve cloud-reachable sources. An on-prem pool means workers running on-prem (compose, systemd, or a local cluster) pointed at the same catalog database and registered under that pool name.

## Azure: ADF integration

SQLFlow runs inside ADF pipelines as a thin trigger, not as a container ADF boots per run. The model is an always-on control plane that a small ADF pipeline of Web Activities calls: authenticate, trigger, poll, fail on failure. Nothing is provisioned per run; a trigger is a sub-second authenticated call.

### 1. Build and push the image

```bash
docker build -t <registry>.azurecr.io/sqlflow-control-plane:latest .
az acr login -n <registry>
docker push <registry>.azurecr.io/sqlflow-control-plane:latest
```

The image installs `libssl3` for LibGit2Sharp's SHA-pinned git materialization.

### 2. Deploy the control plane as a Container App

Put the two secrets in Key Vault first:

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

The template creates a user-assigned managed identity, grants it the Key Vault Secrets User role on the vault, and wires the app to pull the secrets at startup; no secret value appears in the template or app configuration. That same identity resolves `${keyvault:...}` references and cloud-storage credentials at run time (the template sets `SQLFLOW_AZURE_AUTH=mi` and `AZURE_CLIENT_ID`). The catalog connection lands in `SQLFLOW_CATALOG_DB`, which the control plane's default `ControlPlane:Catalog:ConnectionReference` (`${env:SQLFLOW_CATALOG_DB}`) reads directly. Probes hit `/health/live` and `/health/ready`; `minReplicas` defaults to 1 so a trigger never waits on a cold start.

Outputs:

- `controlPlaneBaseUrl`: pass to the ADF pipeline.
- `identityClientId`: grant it access to the SQL databases the flows touch and any flow secrets, since the control plane's worker resolves every credential from its own environment.

After deploy:

- Run the catalog migration once against the catalog connection: `sqlflow db migrate --db "<connection>"` (the `db` subcommands are `migrate|sync|status`; see `src/SqlFlow.Cli/Program.cs`).
- Register git repos as managed sources via `POST /api/v1/repos/sources` (body: `name`, `remoteUrl`, optional `branch` defaulting to `main`, optional `syncIntervalSeconds` defaulting to 300, optional `enabled` defaulting to true) so the catalog stays synced from git. A credential to pull a private remote is never accepted on this endpoint; the control plane resolves it from its own environment.

### 3. Import the ADF pipeline

Import `deploy/adf/SqlFlowTriggerFlow.pipeline.json` in ADF Studio (Author, Pipelines, Import from pipeline template) or deploy it as a `Microsoft.DataFactory/factories/pipelines` resource. Parameters:

| Parameter | Value |
|---|---|
| `controlPlaneBaseUrl` | the deploy output, e.g. `https://sqlflow-control-plane.<region>.azurecontainerapps.io` |
| `repoId` | the repo id from `GET /api/v1/repos` |
| `flowName` | the flow to run |
| `bootstrapSecret` | the control plane's bootstrap secret (secure string) |
| `pollIntervalSeconds` | poll cadence, default 15 |
| `timeout` | max wait as `d.hh:mm:ss`, default `1.00:00:00` |

The pipeline's activities:

1. `GetToken`: `POST /api/v1/auth/token` with the bootstrap secret and the `operate` scope.
2. `TriggerRun`: `POST /api/v1/runs` with `repoId` and `flowName`, bearer-authenticated.
3. `WaitForRun`: an Until loop that polls `GET /api/v1/runs/{id}` every `pollIntervalSeconds` until the status is `succeeded`, `failed`, or `cancelled`.
4. `FailIfRunDidNotSucceed`: fails the ADF pipeline with error code `SqlFlowRunFailed` when the terminal status is not `succeeded`, so a failed flow surfaces as a failed ADF pipeline.

### Routing and production notes

- **Pool routing**: add `"pool": "<pool>"` to the `TriggerRun` body to route the run to a node serving that pool; omit it for any node.
- **Version pinning**: add `"commitSha": "<sha>"` to run an exact committed version. The value must be a 4 to 64 character hexadecimal git object id; otherwise the API returns 400 with `commitSha must be a 4- to 64-character hexadecimal git object id (or omitted to pin to the last synced commit).` (`src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs`). Omitting it pins the run to the repo's last synced commit.
- **Token lifetime**: the pipeline fetches one token up front. If a flow can outlive `ControlPlane:Jwt:AccessTokenMinutes` (default 60, valid range 1 to 1440), raise that value or move `GetToken` inside the polling loop.
- **Secrets in ADF**: in production, source `bootstrapSecret` from a Key Vault linked service rather than a parameter value.

## Local source databases for development

`docker/` (distinct from `deploy/compose/`) spins up PostgreSQL, MySQL, and Oracle source containers seeded with the Sakila sample database for exploratory pipeline work and the foreign-source integration tests; see `docker/README.md`. These are source systems only; every flow writes to a SQL Server sink you run separately.

## See also

- [Control plane](../concepts/control-plane.md)
- [Worker command](../cli/worker.md)
- [Authentication and identity](../concepts/authentication-and-identity.md)
