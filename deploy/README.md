# Deploying SQLFlow

Three images, three scaling behaviors:

| Image | Built from | Scales on | Behind the ingress? |
|---|---|---|---|
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

GUI at http://localhost:8081, API at http://localhost:5000. Bootstrap provisioning creates the catalog database,
applies migrations, seeds roles, and creates the admin from `.env` on first start.

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
  workers additionally get the git token and every `${env:...}` connection their pool's flows reference. Nothing
  data-plane ever passes through the control plane.

## Placement reminder

Workers must run where they can reach the data their pool's flows touch (that is the point of pools). A cloud
cluster's workers serve cloud-reachable sources; an on-prem pool means workers on-prem (compose, systemd, or a
local cluster) pointed at the same catalog database, registered under that pool name.
