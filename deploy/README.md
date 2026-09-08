# Deploying OSDU Delivery

Three images:

| Image | Built from | Scales on | Behind the ingress? |
| --- | --- | --- | --- |
| `sqlflow-control-plane` | `Dockerfile` | request load (HPA) | yes, under `/api` |
| `sqlflow-gui` | `gui/Dockerfile` | trivially (static) | yes, under `/` |
| `sqlflow-worker` | `Dockerfile.worker` | queue depth (KEDA) | never (pull model, no inbound surface) |

```bash
docker build -t sqlflow-control-plane:latest .
docker build -f Dockerfile.worker -t sqlflow-worker:latest .
docker build -t sqlflow-gui:latest gui/
```

## Local / single host: docker compose

```bash
cd deploy/compose
cp .env.example .env    # set the secrets
docker compose up -d --build
docker compose up -d --scale worker=3   # more compute, nothing else changes
```

GUI at <http://localhost:8081>, API at <http://localhost:5000>. Bootstrap provisioning creates the catalog database (the
compose file turns on `ControlPlane__Bootstrap__AllowCreate`; the default only migrates a catalog that already
exists), applies migrations, seeds roles, and creates the admin from `.env` on first start.

## Azure Container Apps: `deploy/bicep/`

The same three-tier layout on managed infrastructure, one template per tier plus a composition:

| Template | Deploys |
| --- | --- |
| `main.bicep` | The full estate: Log Analytics + the Container Apps environment, a Key Vault holding every secret, an Azure SQL catalog database, and the three apps below. |
| `control-plane.bicep` | The API as an always-on Container App. `main.bicep` runs it API-only; standalone it also hosts the in-process worker. |
| `worker.bicep` | One worker pool: no ingress, scaled 0..N on queue depth by the built-in KEDA mssql scaler. One deployment per pool. |
| `gui.bicep` | The SPA behind its own ingress. |
| `entra-app.bicep` | The Entra app registration users sign in with (app roles, assignment required), when `provisionEntraApp` is on. |

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

Sign in at the `guiUrl` output with the bootstrap admin (first start applies the catalog migrations and creates
the user); point CLI remotes at `controlPlaneBaseUrl`. How the k8s layout maps onto Container Apps:

- **Two origins instead of a path split**: every Container App has its own ingress FQDN, so `main.bicep` wires
  the GUI's `SQLFLOW_API_BASE_URL` to the control plane URL and CORS-lists the GUI origin on the control plane.
  Put Front Door or Application Gateway in front of both apps to restore the one-host layout, then blank both.
- **KEDA is built in**: `worker.bicep` runs the same mssql queue-depth query as `worker-pool.yaml` with
  `minReplicas: 0`. Add a pool with another deployment of it (`-p name=sqlflow-worker-<pool> pool=<pool>`).
- **Secrets live in Key Vault, read by managed identity**: no secret value appears in the templates or app
  configuration, and the same identities resolve `${keyvault:...}` references at run time
  (`SQLFLOW_AZURE_AUTH=mi`). The references a pool's flows use (drop storage, OSDU client secrets) are added via
  `workerFlowEnv`, one `{ name, secretName }` entry per reference naming a secret created in the vault out of
  band, so no credential passes through the template. The deploying principal needs to create role assignments
  (Owner or User Access Administrator) and to write vault secrets (Key Vault Secrets Officer; the vault uses RBAC).
- **Catalog on Azure SQL**: the connection string (SQL auth) exists only as the `sqlflow-catalog-db` vault
  secret; the server allows Azure-service traffic because consumption-plan apps have no fixed egress address.
  Hardening path: a VNet-integrated environment with a private endpoint to SQL, and least-privilege or Entra
  identities in place of the SQL admin. App config never changes; update the one secret.
- **Bring your own SQL and network**: `existingSqlServer=<host[,port]>` points the catalog at a server you
  already run (a Managed Instance FQDN, for example) instead of creating one. Create the empty catalog database
  there yourself: bootstrap initialises an empty database on first start, and refuses to create a missing one
  unless `ControlPlane__Bootstrap__AllowCreate` is on. `infrastructureSubnetId` VNet-integrates the environment (an undelegated /23), which is how the
  apps reach a VNet-only Managed Instance privately.
- **Private git remotes**: `gitToken` lands in Key Vault and reaches managed sync (control plane) and
  materialization (workers) as `SQLFLOW_GIT_TOKEN`. Hosts that pair the token with a username (Bitbucket app
  passwords or `x-token-auth` repository tokens) also set `gitUsername`; GitHub needs the token alone.

## Kubernetes: `deploy/k8s/`

Prerequisites: an ingress controller (the annotations assume ingress-nginx), cert-manager or another TLS source,
and [KEDA](https://keda.sh) for worker autoscaling.

```bash
kubectl apply -f deploy/k8s/namespace.yaml
# create the real secret (see secrets.example.yaml for the required keys), then:
kubectl apply -f deploy/k8s/controlplane.yaml
kubectl apply -f deploy/k8s/gui.yaml
kubectl apply -f deploy/k8s/ingress.yaml
kubectl apply -f deploy/k8s/worker-pool.yaml
```

The layout and the reasoning behind it:

- **One host, path split** (`/api` and `/openapi` to the control plane, `/` to the GUI): the SPA runs
  same-origin with the API, so no CORS configuration exists anywhere. The GUI image's
  `SQLFLOW_API_BASE_URL=""` means "same origin".
- **Control plane replicas are API-only** (`ControlPlane__Worker__Enabled=false`): the HTTP tier scales on
  request load via the HPA, independent of compute. Multi-replica is safe by construction: stateless JWT
  validation, and the scheduler / managed sync / run queue all claim work atomically in the catalog.
- **Forwarded headers are trusted from the ingress only** (`ControlPlane__Proxy__*`): set `KnownNetworks` to
  your cluster's ingress/pod CIDR. Without this, rate limiting and login throttling would key on the ingress
  address instead of the real client.
- **Workers scale 0 to N per pool on queue depth**: KEDA's mssql scaler counts queued runs for the pool and
  scales the matching worker Deployment; `minReplicaCount: 0` means an idle pool costs nothing. Copy
  `worker-pool.yaml` per pool (set `SQLFLOW_WORKER_POOL` and the query's `TargetPool` predicate). Because runs
  are pinned to the repo's synced commit at enqueue, a cold-started worker needs only its environment: it
  materializes the exact commit from git and executes.
- **Secrets stay on the tier that uses them**: the control plane gets the catalog connection and JWT material;
  workers additionally get the git token and every `${env:...}` reference their pool's flows use. Nothing
  data-plane ever passes through the control plane.

## Placement reminder

Workers must run where they can reach the drops and the OSDU endpoint their pool's flows touch (that is the
point of pools). A cloud cluster's workers serve cloud-reachable drops; an on-prem pool means workers on-prem
(compose, systemd, or a local cluster) pointed at the same catalog database, registered under that pool name.
