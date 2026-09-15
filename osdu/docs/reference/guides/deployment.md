# Deploying OSDU Delivery

The deployment assets and their step-by-step instructions are in [../../../deploy/](../../../deploy/README.md):
the three container images, the docker compose stack, the Kubernetes manifests and the Azure Container Apps Bicep
templates. This page is the reasoning behind them, and what an OSDU Delivery deployment needs that a plain SQLFlow
one does not. SQLFlow's own deployment guide is
[../../../../sqlflow/docs/reference/guides/deployment.md](../../../../sqlflow/docs/reference/guides/deployment.md).

## Three images, one context

| Image | Publishes | Scales on | Behind the ingress? |
| --- | --- | --- | --- |
| `osdu-delivery-control-plane` | `osdu/hosts/SqlFlow.Delivery.ControlPlane.Host` | one replica (dispatch has one owner) | yes, under `/api` |
| `osdu-delivery-gui` | `osdu/gui` (with the vendored `sqlflow/gui` compiled in place) | trivially (static) | yes, under `/` |
| `osdu-delivery-worker` | `osdu/hosts/SqlFlow.Delivery.Worker.Host` | the control plane's replica target (KEDA) | never (it takes work outbound) |

All three build from the **repository root**. The .NET hosts reference both `osdu/src` and `sqlflow/src`, and the
GUI compiles `osdu/gui` together with the vendored `sqlflow/gui` sources it imports, so no image can be built from
a narrower context. The root `.dockerignore` keeps that context small.

The image names carry the product rather than the platform, because the vendored SQLFlow ships its own `sqlflow-*`
images from its own deployment assets and these are different artifacts. Project, namespace, binary and
environment-variable names stay `SqlFlow.*` and `SQLFLOW_*`.

## What each tier holds

- **The control plane** is the API, the scheduler, the managed git sync and the run dispatcher, plus the module's
  delivery and template endpoints and its background services. It owns the run queue through a lease, so it runs
  **one replica**. It holds the catalog connection (which also carries the `osdu` schema), the JWT material, the
  bootstrap admin and the git token, and no data-plane credential at all.
- **Nodes** take work from the dispatcher with a personal access token minted with the `node` scope. A node holds
  the OSDU module database reference, the ingestion database connection, the payload storage credential and the
  OSDU client secret its pool's flows use. It opens no catalog connection.
- **The GUI** is a static SPA. Its API base URL is written at container start from `SQLFLOW_API_BASE_URL`, so one
  image serves every environment: empty means same-origin behind a path-splitting ingress, a URL means a separate
  API origin with CORS configured on the control plane.

## First start, in order

1. Deploy the control plane and the GUI. Bootstrap provisioning applies SQLFlow's catalog migrations and then the
   OSDU module's, seeds the roles, and creates the admin. It refuses to create a missing database unless
   `ControlPlane__Bootstrap__AllowCreate` is on, so a mistyped connection never provisions the wrong server.
2. Sign in and mint a node-scoped personal access token (`POST /api/v1/me/tokens` with scopes `["node"]`, or the
   GUI's token page). Nodes cannot take work before this exists, which is why it cannot be a deployment parameter
   on a first run.
3. Deploy the worker pool with that token, and with the OSDU credentials and database references its flows need.
4. Register the flow repository as a managed repo source, so the pre, ingestion and OSDU flow documents are synced
   into the catalog and their schedules start firing.
5. Save the templates the mappings pin and fill the OSDU cache (`sqlflow template`, and a `refresh` run of the
   cache flow), so the preflight gate has something to check against.

## Scale-in must not sever a delivery

A node handles SIGTERM by draining: it stops taking work and lets the runs it already holds finish and record
their outcomes, for up to `SQLFLOW_WORKER_DRAIN_SECONDS` (default 540). That only works if the orchestrator waits.
Both Kubernetes and Container Apps default to a 30 second grace period, so **set the termination grace period
above the drain window on every node workload**; the shipped manifests and templates set 600.

Left at the default, the drain starts and is killed 30 seconds in. Every unfinished run is severed with no outcome
recorded, is recovered only by a requeue, and each requeue consumes one of that run's execution attempts, so three
unlucky scale-ins fail a perfectly healthy delivery and report the run itself as the cause. A severed delivery
loses no records, because the ledger leases each record to one attempt at a time and a lease the severed node held
expires, but it does repeat the work.

## Placement

Nodes must run where they can reach the ingestion database, the payload storage and the OSDU endpoint their pool's
flows touch. That is what pools are for: a cloud pool serves cloud-reachable data, and an on-prem pool means nodes
on-prem pointed at the same control plane and registered under that pool name. Only the node needs that reach; the
control plane never touches any of it.

## Triggering from an external scheduler

OSDU Delivery runs under Azure Data Factory, or any scheduler, as a thin trigger rather than a container booted
per run: the control plane is always on and a trigger is a sub-second authenticated call. Each step is one HTTP
request:

1. **Authenticate.** A service account's personal access token, read from a secret store. The break-glass
   alternative is `POST /api/v1/auth/token` with the bootstrap secret, mapped only when one is configured.
2. **Trigger.** `POST /api/v1/runs` (scope `operate`) with the repository and flow, the flow's parameter values,
   and the operation and payload the kind takes. A 202 carries the run id.
3. **Wait.** Poll `GET /api/v1/runs/{runId}` until the status is terminal. The detail carries the record counts
   and the submission the run produced.
4. **Fail on failure.** Fail the pipeline when the terminal status is not `succeeded`, so a failed delivery
   surfaces as a failed pipeline run.

See [../cli/control-plane.md](../cli/control-plane.md) for the same calls from a terminal, and
[../cli/delivery.md](../cli/delivery.md) for what each operation does.
