---
id: delivery-guide-deployment
title: "Deploying OSDU Delivery: the images, what each tier needs beyond SQLFlow, and a first start in order"
type: guide
summary: "Deploying OSDU Delivery with compose, Kubernetes or Container Apps: its images, databases, what each tier needs, and a first start."
keywords:
  - deploy
  - deployment
  - docker compose
  - kubernetes
  - azure container apps
  - bicep
  - images
  - osdu-delivery-worker
  - sqlflow_osdu_db
  - node token
  - first deployment
  - external scheduler
  - azure data factory
related:
  - guide-deployment
  - cli-worker
  - delivery-concept-control-plane
  - delivery-concept-environment-variables
  - delivery-concept-architecture
  - delivery-guide-getting-started
  - delivery-cli-db
  - delivery-guide-mcp
sourceRefs:
  - osdu/deploy/README.md
  - osdu/deploy/docker/control-plane.Dockerfile
  - osdu/deploy/docker/worker.Dockerfile
  - osdu/deploy/docker/worker-entrypoint.sh
  - osdu/deploy/docker/gui.Dockerfile
  - osdu/deploy/docker/mcp.Dockerfile
  - osdu/deploy/compose/docker-compose.yml
  - osdu/deploy/compose/.env.example
  - osdu/deploy/k8s/controlplane.yaml
  - osdu/deploy/k8s/worker-pool.yaml
  - osdu/deploy/k8s/mcp.yaml
  - osdu/deploy/k8s/secrets.example.yaml
  - osdu/deploy/bicep/main.bicep
  - osdu/deploy/bicep/control-plane.bicep
  - osdu/deploy/bicep/worker.bicep
  - osdu/deploy/bicep/entra-app.bicep
  - osdu/hosts/SqlFlow.Delivery.ControlPlane.Host/Program.cs
  - osdu/hosts/SqlFlow.Delivery.Worker.Host/Program.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - sqlflow/src/SqlFlow.Cli/Program.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/RunTriggerEndpoints.cs
---

# Deploying OSDU Delivery

OSDU Delivery deploys exactly as SQLFlow does: an always-on control plane that owns the run queue and runs one replica,
a static GUI, and worker nodes that poll the control plane for work and scale on its replica target. The tiers, KEDA, the
ingress layouts, the drain contract, pools and triggering from Azure Data Factory are explained in
[SQLFlow's deployment guide](../../../../sqlflow/docs/reference/guides/deployment.md). OSDU Delivery ships its own
deployment assets under `osdu/deploy/` (docker, compose, Kubernetes, Bicep, with a README of their own), because its
images are different artifacts. This guide covers what an OSDU Delivery deployment needs that a plain SQLFlow one does not.

## The images

| Image | Dockerfile | Publishes | Scales on |
| --- | --- | --- | --- |
| `osdu-delivery-control-plane` | `osdu/deploy/docker/control-plane.Dockerfile` | `osdu/hosts/SqlFlow.Delivery.ControlPlane.Host` | one replica |
| `osdu-delivery-worker` | `osdu/deploy/docker/worker.Dockerfile` | `osdu/hosts/SqlFlow.Delivery.Worker.Host` | the control plane's replica target (KEDA) |
| `osdu-delivery-gui` | `osdu/deploy/docker/gui.Dockerfile` | `osdu/gui`, compiled with the vendored `sqlflow/gui` | trivially (static) |
| `osdu-delivery-mcp` (optional) | `osdu/deploy/docker/mcp.Dockerfile` | `osdu/hosts/osdu-delivery-mcp` | trivially (stateless) |

Every image builds from the **repository root**: the .NET hosts reference both `osdu/src` and `sqlflow/src`, the GUI
compiles both GUI trees, and the MCP server compiles the documentation of both into its binary.

```bash
docker build -f osdu/deploy/docker/control-plane.Dockerfile -t osdu-delivery-control-plane:latest .
docker build -f osdu/deploy/docker/worker.Dockerfile        -t osdu-delivery-worker:latest .
docker build -f osdu/deploy/docker/gui.Dockerfile           -t osdu-delivery-gui:latest .
docker build -f osdu/deploy/docker/mcp.Dockerfile           -t osdu-delivery-mcp:latest .
```

The worker host is the `sqlflow worker` verb with the OSDU module installed and nothing else: its entrypoint passes only
the node's options (`--pool` from `SQLFLOW_WORKER_POOL`, `--poll-seconds`, `--drain-seconds`), and it refuses to be told to
run another verb.

## The databases

| Database (shipped names) | Holds |
| --- | --- |
| `SQLFlow` | The metadata: SQLFlow's catalog schema and the module's `osdu` schema beside it (the ledger, mappings, templates, caches, partitions, central configuration). |
| `OsduDeliveryPre` | What the pre flows land from the source files. |
| `OsduDeliveryIng` | The keyed ingestion tables the delivery flows read. |

The control plane's bootstrap creates (with `ControlPlane__Bootstrap__AllowCreate`) and migrates the metadata database:
SQLFlow's catalog first, then the `osdu` schema. Nothing creates the two data databases; the compose stack's `dbinit`
service does it once, and elsewhere they are provisioned with the server.

**Decide where the `osdu` schema lives before the first migrate.** By default it sits in the catalog's database. An estate
that must keep it apart (Azure SQL lets no statement reach across two databases) names its database in
`Osdu:Database:Connection` (or `SQLFLOW_OSDU_DB`) on the control plane, and points every node at it; the choice cannot be
changed by editing the setting afterwards. In the Bicep templates this is `osduDatabaseName`.

The source database a delivery flow reads allows snapshot isolation once, so a record and its child rows are read as one
moment: `ALTER DATABASE [OsduDeliveryIng] SET ALLOW_SNAPSHOT_ISOLATION ON;` (Azure SQL Database allows it by default). A
flow that cannot have it declares `source.incremental.isolation: readCommitted`; without either, the run fails with
`the source database does not allow snapshot isolation, which is how a record and its child rows are read as one moment`.

## What each tier is given beyond SQLFlow's

| Tier | OSDU Delivery adds |
| --- | --- |
| Control plane | Every `${env:...}` reference the flows use, the same list as the nodes', because a person's reads of a flow (a probe, a read-back, a preview, a source read, the explorer) run here with the flow's credentials; `SQLFLOW_DELIVERY_PRIVATE_NETWORKS`; the `Osdu:*` settings it needs. It never delivers. |
| Node | `SQLFLOW_OSDU_DB`, the connection of the database holding the `osdu` schema with a login on that schema alone; every `${env:...}` reference its pool's flows use (the ingestion database, the OSDU endpoint and its credentials); `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` when a target resolves to a private address. A node opens no catalog connection. |
| GUI | Nothing beyond SQLFlow's `SQLFLOW_API_BASE_URL`. |
| MCP server | Optional; holds no credential ([the MCP server](mcp.md#running-it-as-a-service)). |

Without `SQLFLOW_OSDU_DB` a node refuses to start (`ERROR  the worker refuses to start: ...`), and it also refuses a
module database that is missing, behind or ahead of its build. Every variable is listed in
[environment variables](../concepts/environment-variables.md).

The shipped assets wire the two data databases as `SQLFLOW_CONN_PRE` and `SQLFLOW_CONN_DWH`, and the sample OSDU references
(`OSDU_URL`, `OSDU_TOKEN_URL`, `OSDU_SCOPE`, `OSDU_CLIENT_ID`, `OSDU_CLIENT_SECRET`, `OSDU_DATA_PARTITION`, `OSDU_ACL_OWNER`,
`OSDU_ACL_VIEWER`, `OSDU_LEGAL_TAG`) on both the control plane and the nodes. Rename them to whatever your flows reference,
and keep the two lists equal. Values the central configuration holds ([sqlflow config](../cli/config.md)) need no variable
on any tier.

## A first start, in order

1. **Deploy the control plane and the GUI.** Bootstrap migrates the catalog and then the `osdu` schema, seeds the roles and
   creates the admin. Startup refuses pending module migrations, a database newer than the build and a catalog older than
   the module needs, naming the migration ([sqlflow db](../cli/db.md)).
2. **Mint a node token.** Sign in as the admin and mint a personal access token with the `node` scope
   (`POST /api/v1/me/tokens` with scopes `["node"]`, or the GUI's token page). It can only be minted once the control plane
   runs, which is why a first start has two steps.
3. **Deploy the workers** with that token as `SQLFLOW_TOKEN`, `SQLFLOW_OSDU_DB`, and the references their flows use.
4. **Register the flow repository** as a managed repository source, so the pre, ingestion and OSDU flows, the mappings and
   the cache flows are synced and their schedules fire.
5. **Register the partitions** the flows deliver to ([sqlflow partition](../cli/partition.md)), save the templates the
   mappings pin ([sqlflow template](../cli/template.md)) and fill each partition's cache with a refresh run of its cache
   flows.
6. **Plan before anything touches OSDU**: run the pre and ingestion flows, then the delivery flow with operation `plan`,
   then `deliver` a small scope. [Getting started](getting-started.md) walks through it.

### docker compose

```bash
cd osdu/deploy/compose
cp .env.example .env                      # set the secrets there, never in the compose file
docker compose up -d --build mssql dbinit controlplane gui
# mint the node token, put it in .env as SQLFLOW_NODE_TOKEN, then:
docker compose up -d --build worker
docker compose --profile mcp up -d --build mcp   # optional: the MCP server on http://localhost:8787/mcp
```

The GUI is on `http://localhost:8081` and the API on `http://localhost:5000`.

### Kubernetes

`osdu/deploy/k8s` holds `namespace.yaml`, `controlplane.yaml`, `gui.yaml`, `ingress.yaml`, `worker-pool.yaml` (one pool;
copy it per pool), `mcp.yaml` (optional, adds `/mcp` to the same host) and `secrets.example.yaml`, which names every key of
the `osdu-delivery-secrets` secret, including `osdu-module-connection` (the node's `SQLFLOW_OSDU_DB`). The layout is
SQLFlow's: one host with `/api` to the control plane and `/` to the GUI, so no CORS.

### Azure Container Apps

`osdu/deploy/bicep/main.bicep` deploys the whole estate: Log Analytics, the environment, a Key Vault holding every secret,
the Azure SQL databases, and the control plane, a worker pool and the GUI (not the MCP server). OSDU Delivery's
parameters beyond SQLFlow's:

| Parameter | Meaning |
| --- | --- |
| `osduDatabaseName` | The database holding the `osdu` schema; the catalog's database by default. |
| `preDatabaseName`, `ingestionDatabaseName` | The two data databases (`OsduDeliveryPre`, `OsduDeliveryIng`). |
| `workerFlowEnv` | One `{ name, secretName }` per further `${env:...}` reference the flows use, each naming a Key Vault secret created out of band. The control plane is given the same references. |
| `privateNetworks` | `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` for the apps. |
| `nodeToken` | The node token; empty on a first deployment, then redeployed with it. |
| `provisionEntraApp`, `azureAdAllowedGroupObjectId` | The Entra registration with the `SqlFlow.User` app role and assignment required ([authentication and identity](../concepts/authentication-and-identity.md)). |

## Scale-in, placement and the module's own settings

- **Drain.** A node draining on SIGTERM finishes the deliveries it holds for up to `SQLFLOW_WORKER_DRAIN_SECONDS` (540 by
  default), so the termination grace period has to be longer: `worker-pool.yaml` and `worker.bicep` set 600. A severed
  delivery loses no record (the ledger leases each record to one attempt), but it repeats work and consumes one of the
  run's execution attempts ([SQLFlow's worker](../../../../sqlflow/docs/reference/cli/worker.md)).
- **Placement.** A node runs where it reaches its pool's ingestion database and OSDU endpoint. The control plane needs
  the same reach for the reads it answers.
- **Probing and metrics.** The scheduled target probe (`Osdu:TargetProbe`) and the metrics export (`Osdu:Telemetry` on the
  control plane, `OSDU_TELEMETRY_*` on nodes) are off until a deployment turns them on
  ([run trace and metrics](../concepts/run-trace-and-metrics.md)).

## Triggering from an external scheduler

An OSDU flow is triggered as SQLFlow's ADF integration triggers any flow: authenticate with a service account's personal
access token, `POST /api/v1/runs`, poll `GET /api/v1/runs/{runId}` until it is terminal, and fail the pipeline when it did
not succeed. The trigger body adds the kind's own arguments:

```json
{
  "repoId": "0195c9a2-7f30-7c44-9c1e-0aa1b2c3d4e5",
  "flowName": "welldb-wellbore-03-delivery",
  "operation": "deliver",
  "values": { "partition": "dev" }
}
```

`operation` is the kind's (`deliver`, `plan`, `verify`, ... for a delivery flow), `values` are the flow's parameters and,
for a flow that names its partitions, `partition`; `payload` carries the kind's options. They are described in
[running an OSDU flow](../cli/run.md). A delivery run that held records still succeeds: held records are an outcome in the
ledger, not a failure of the run, so a pipeline that must stop on them reads the held and failed counts in the run
detail's `resultJson`.

## See also

- [SQLFlow: deploying](../../../../sqlflow/docs/reference/guides/deployment.md)
- [Environment variables](../concepts/environment-variables.md)
- [The control plane](../concepts/control-plane.md)
- `osdu/deploy/README.md`, beside the assets
