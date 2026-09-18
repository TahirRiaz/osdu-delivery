# Deploying OSDU Delivery

Three images, built from the OSDU Delivery hosts in `osdu/hosts`:

| Image | Built from | Scales on | Behind the ingress? |
| --- | --- | --- | --- |
| `osdu-delivery-control-plane` | `osdu/deploy/docker/control-plane.Dockerfile` | one replica (dispatch has one owner) | yes, under `/api` |
| `osdu-delivery-gui` | `osdu/deploy/docker/gui.Dockerfile` | trivially (static) | yes, under `/` |
| `osdu-delivery-worker` | `osdu/deploy/docker/worker.Dockerfile` | the control plane's replica target (KEDA) | never (pull model: it polls the control plane for work, no inbound surface) |

**All three build from the repository root.** The .NET images publish hosts that reference both `osdu/src` and
`sqlflow/src`, and the GUI image compiles `osdu/gui` together with the vendored `sqlflow/gui` sources it imports,
so no image can be built from a narrower context. The root `.dockerignore` keeps the context small.

```bash
docker build -f osdu/deploy/docker/control-plane.Dockerfile -t osdu-delivery-control-plane:latest .
docker build -f osdu/deploy/docker/worker.Dockerfile       -t osdu-delivery-worker:latest .
docker build -f osdu/deploy/docker/gui.Dockerfile          -t osdu-delivery-gui:latest .
```

The image names carry the product rather than the platform, because the vendored SQLFlow in `sqlflow/` ships its
own `sqlflow-*` images from its own deployment assets; these are different artifacts. Project, namespace, binary
and environment-variable names stay `SqlFlow.*` and `SQLFLOW_*`, as the project's naming rule says.

## How the tiers fit together

- **The control plane** is the API, the scheduler, the managed git sync and the run dispatcher. It owns the run
  queue through a lease in the catalog, so it runs **one replica**: a second adds no dispatch capacity and refuses
  every node call that lands on it.
- **Nodes** poll the control plane for work over the node protocol, authenticated with a personal access token
  minted with the `node` scope. A node opens **no catalog connection**: the run's definition, its YAML, its
  lineage context and its live trace all travel over that protocol.
- **The databases of an estate.** All of SQLFlow's metadata is one database, and the source data, which is where
  the volume is, passes through two of its own:

  | Database | Holds | Grows with |
  | --- | --- | --- |
  | `SQLFlow` | the metadata: SQLFlow's catalog schema (pipelines, runs, schedules, lineage, sources, users) and the delivery module's `osdu` schema beside it (the record ledger, mappings, templates, OSDU caches) | the estate's activity, and every record ever delivered |
  | `OsduDeliveryPre` | what pre-ingestion lands from the source systems | the source data |
  | `OsduDeliveryIng` | the keyed ingestion tables the OSDU flows read | the source data |

  The two schemas of the metadata database stay separate things: their own EF contexts, their own migration
  histories, their own versions, and no foreign key between them. That is what lets an estate that has to keep
  them in two databases do so: name that database in `SQLFLOW_OSDU_DB` (or `Osdu:Database:Connection`), on the
  control plane and on every node, and the module's rows are written and committed there instead. An estate on
  Azure SQL that wants them apart has no other option, since no statement there reaches across two databases.
  The choice cannot be changed by editing the setting afterwards, so make it before the first migrate.

  The ledger reads under snapshot isolation, so whichever database holds the `osdu` schema is allowed it once
  (Azure SQL Database allows it by default): `ALTER DATABASE [SQLFlow] SET ALLOW_SNAPSHOT_ISOLATION ON;`
- **The OSDU ledger on a node.** The delivery engine reads and writes the `osdu` schema per record while it plans
  and delivers, and a node opens no catalog connection, so a node without the module's connection validates and
  plans but delivers nothing. See [../docs/environment-variables.md](../docs/environment-variables.md).
- **The delivery chain** is three flows in lineage order: a SQLFlow pre-ingestion flow lands the source files, a
  SQLFlow ingestion flow loads the keyed ingestion tables, and the OSDU flow reads those tables and delivers. The
  two databases are wired under the fixed names `${env:SQLFLOW_CONN_PRE}` and `${env:SQLFLOW_CONN_DWH}`, so a flow
  document moves from test to prod unchanged.

## Local / single host: docker compose

Nodes need a node-scoped token, which can only be minted once the control plane is running, so a first start is
two steps:

```bash
cd osdu/deploy/compose
cp .env.example .env    # set the secrets
docker compose up -d --build mssql dbinit controlplane gui
# sign in as the admin, mint the token (POST /api/v1/me/tokens with scopes ["node"], or the GUI's token page),
# put it in .env as SQLFLOW_NODE_TOKEN, then:
docker compose up -d --build worker
docker compose up -d --scale worker=3   # more compute, nothing else changes
```

GUI at <http://localhost:8081>, API at <http://localhost:5000>. Bootstrap provisioning creates the `SQLFlow`
metadata database (the compose file turns on `ControlPlane__Bootstrap__AllowCreate`; the default only migrates a
database that already exists), applies SQLFlow's catalog migrations and then the OSDU module's beside them, seeds
roles, and creates the admin from `.env` on first start. The `dbinit` service creates the chain's two data
databases, which nothing else creates: SQLFlow makes schemas and tables inside a database, never a database.

## Azure Container Apps: `bicep/`

The same three-tier layout on managed infrastructure, one template per tier plus a composition. The `.json` files
beside each `.bicep` are the compiled ARM templates; rebuild them with `az bicep build --file <name>.bicep` after
editing a template.

| Template | Deploys |
| --- | --- |
| `main.bicep` | The full estate: Log Analytics and the Container Apps environment, a Key Vault holding every secret, the Azure SQL metadata database (`SQLFlow`) and the pre and ingestion databases, and the three apps below. |
| `control-plane.bicep` | The API as an always-on Container App. `main.bicep` runs it API-only; standalone it also hosts the in-process worker. |
| `worker.bicep` | One worker pool: no ingress, scaled 0..N on the control plane's replica target by the built-in KEDA metrics-api scaler, authenticated with the node token. One deployment per pool. |
| `gui.bicep` | The SPA behind its own ingress. |
| `entra-app.bicep` | The Entra app registration users sign in with (app roles, assignment required), when `provisionEntraApp` is on. |

```bash
az group create -n osdu-delivery -l <region>
az acr create -g osdu-delivery -n <registry> --sku Basic
az acr build -r <registry> -t osdu-delivery-control-plane:latest -f osdu/deploy/docker/control-plane.Dockerfile .
az acr build -r <registry> -t osdu-delivery-worker:latest        -f osdu/deploy/docker/worker.Dockerfile .
az acr build -r <registry> -t osdu-delivery-gui:latest           -f osdu/deploy/docker/gui.Dockerfile .

az deployment group create -g osdu-delivery -f osdu/deploy/bicep/main.bicep \
  -p acrName=<registry> \
     controlPlaneImage=<registry>.azurecr.io/osdu-delivery-control-plane:latest \
     workerImage=<registry>.azurecr.io/osdu-delivery-worker:latest \
     guiImage=<registry>.azurecr.io/osdu-delivery-gui:latest \
     sqlAdminPassword='<complex password>' \
     jwtSigningKey="$(openssl rand -base64 48)" \
     adminPassword='<initial admin password, 12+ chars>'
```

Sign in at the `guiUrl` output with the bootstrap admin, then mint a node-scoped token and redeploy with
`nodeToken=<sqlf_...>` so the worker pool gets its credential and its scale rule. Point CLI remotes at
`controlPlaneBaseUrl`. How the Kubernetes layout maps onto Container Apps:

- **Two origins instead of a path split**: every Container App has its own ingress FQDN, so `main.bicep` wires the
  GUI's `SQLFLOW_API_BASE_URL` to the control plane URL and CORS-lists the GUI origin on the control plane. Put
  Front Door or Application Gateway in front of both apps to restore the one-host layout, then blank both.
- **KEDA is built in**: `worker.bicep` reads the same scale-target endpoint as `worker-pool.yaml`, with
  `minReplicas: 0` and the node token as the scaler's credential. Add a pool with another deployment of it
  (`-p name=osdu-delivery-worker-<pool> pool=<pool>`).
- **Secrets live in Key Vault, read by managed identity**: no secret value appears in the templates or app
  configuration, and the same identities resolve `${keyvault:...}` references at run time
  (`SQLFLOW_AZURE_AUTH=mi`). The `osdu` schema's connection and the pre and ingestion connections are wired for
  free under their fixed names, on both tiers. The OSDU credentials a pool's flows use are added via
  `workerFlowEnv`, one `{ name, secretName }` entry per reference naming a secret created in the vault out of
  band, so no credential passes through the template. The deploying principal needs to create role assignments
  (Owner or User Access Administrator) and to write vault secrets (Key Vault Secrets Officer; the vault uses RBAC).
- **Bring your own SQL and network**: `existingSqlServer=<host[,port]>` points the databases at a server you
  already run (a Managed Instance FQDN, for example) instead of creating one; create the databases there yourself,
  since bootstrap refuses to create a missing one unless `ControlPlane__Bootstrap__AllowCreate` is on.
  `infrastructureSubnetId` VNet-integrates the environment (an undelegated /23), which is how the apps reach a
  VNet-only Managed Instance privately. The delivery engine refuses every private address it is not told about, so
  a worker whose OSDU, storage accounts or proxy resolve to private addresses (Azure Private Link) needs their ranges
  in `privateNetworks` (`SQLFLOW_DELIVERY_PRIVATE_NETWORKS`, CIDR ranges, comma separated). Loopback, link-local and
  cloud metadata addresses stay unreachable whatever it lists.
- **Private git remotes**: `gitToken` lands in Key Vault and reaches managed sync (control plane) and
  materialization (nodes) as `SQLFLOW_GIT_TOKEN`. Hosts that pair the token with a username (Bitbucket app
  passwords or `x-token-auth` repository tokens) also set `gitUsername`; GitHub needs the token alone.

## Kubernetes: `k8s/`

Prerequisites: an ingress controller (the annotations assume ingress-nginx), cert-manager or another TLS source,
and [KEDA](https://keda.sh) for node autoscaling.

```bash
kubectl apply -f osdu/deploy/k8s/namespace.yaml
# create the real secret (see secrets.example.yaml for the required keys), then:
kubectl apply -f osdu/deploy/k8s/controlplane.yaml
kubectl apply -f osdu/deploy/k8s/gui.yaml
kubectl apply -f osdu/deploy/k8s/ingress.yaml
# mint the node token, add it to the secret, then:
kubectl apply -f osdu/deploy/k8s/worker-pool.yaml
```

The layout and the reasoning behind it:

- **One host, path split** (`/api` and `/openapi` to the control plane, `/` to the GUI): the SPA runs same-origin
  with the API, so no CORS configuration exists anywhere. The GUI image's `SQLFLOW_API_BASE_URL=""` means "same
  origin".
- **The control plane runs one replica, API-only** (`ControlPlane__Worker__Enabled=false`). A rolling update
  briefly overlaps two replicas; the old one releases the dispatch lease on its graceful stop and the nodes retry.
- **Forwarded headers are trusted from the ingress only** (`ControlPlane__Proxy__*`): set `KnownNetworks` to your
  cluster's ingress/pod CIDR. Without it, rate limiting and login throttling key on the ingress address instead of
  the real client.
- **Nodes scale 0 to N per pool on the control plane's replica target**: KEDA's metrics-api scaler reads
  `GET /api/v1/node/scale-target?pool=<name>` with the node token and scales the matching Deployment;
  `minReplicaCount: 0` means an idle pool costs nothing. Copy `worker-pool.yaml` per pool (set
  `SQLFLOW_WORKER_POOL` and the URL's `?pool=`).
- **Secrets stay on the tier that uses them**: the control plane gets the metadata connection and JWT material;
  nodes get a node token, the git token, a connection to the `osdu` schema of their own, the pre and ingestion
  connections and every `${env:...}` reference their pool's flows use, and never the catalog's own credential. Nothing data-plane
  ever passes through the control plane.

## Scale-in must not sever a delivery

A node handles SIGTERM by draining: it stops taking work and lets the runs it already holds finish and record
their outcomes, for up to `SQLFLOW_WORKER_DRAIN_SECONDS` (default 540). That only works if the orchestrator waits,
and both Kubernetes and Container Apps default to a 30 second grace period. **Set the termination grace period
above the drain window on every node workload**: `worker-pool.yaml` and `worker.bicep` both set 600. Left at the
default, the drain starts and is killed 30 seconds in, every unfinished run is severed with no outcome recorded,
and each severed run consumes one of its execution attempts.

## Placement reminder

Nodes must run where they can reach the ingestion database and the OSDU endpoint their pool's flows touch (that is
the point of pools). A cloud cluster's nodes serve cloud-reachable data; an on-prem pool means nodes on-prem
(compose, systemd, or a local cluster) pointed at the same control plane, registered under that pool name.
