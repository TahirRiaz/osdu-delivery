# The control plane: what the OSDU module adds

The control plane is SQLFlow's ASP.NET Core host: the `/api/v1` API over the catalog, the scheduler, the managed
git sync, the run dispatcher, the notification pipeline and bootstrap provisioning. Its host composition,
authentication, authorization policies, health probes, rate limiting, CORS and proxy handling, configuration
section, first-run bootstrap and managed sync are all documented in
[../../../../sqlflow/docs/reference/concepts/control-plane.md](../../../../sqlflow/docs/reference/concepts/control-plane.md).

This page covers what OSDU Delivery adds to that host. The module registers itself through SQLFlow's host module
extension point, so everything below is composed in by
`osdu/hosts/SqlFlow.Delivery.ControlPlane.Host` rather than referenced from SQLFlow's own code.

## One replica

The control plane runs a single replica. The run queue lives in that process and is owned by exactly one replica
at a time through a lease in the catalog; a second adds no dispatch capacity and refuses every node call that
lands on it. That is a platform property, not an OSDU one, but it is the first thing a delivery estate's
deployment has to get right, so it is repeated here and in [../guides/deployment.md](../guides/deployment.md).

## The delivery API

Everything the module adds lives under `/api/v1/delivery`, mapped into SQLFlow's existing authorization policy
groups so a delivery route is authorized exactly like a platform route.

**Read group** (any authenticated user):

| Endpoints | Purpose |
| --- | --- |
| `GET /delivery/flows/{pipelineId}/...` | A flow's statistics, its records, its submissions, and its retrievals for a retrieval flow. Every count is derived from the ledger, never held separately. |
| `GET /delivery/records/{flowId}/{key}/...`, `GET /delivery/submissions/...` | One flow's record with its attempts and activities; one submission with its attempts and its work batches. |
| `GET /delivery/activity` | The audit trail of interventions: who did what, when, and in which run. |
| `GET /delivery/mappings`, `GET /delivery/caches`, `/cache/items`, `/cache/versions`, `/cache/history`, `/cache/diff`, `/cache/tags` | The mapping documents the repositories hold, every data partition's cache with the cache flows filling it, and one partition's cached records, versions, history, comparison and the changes awaiting a decision. |
| `GET /delivery/templates`, `/templates/detail`, `/templates/schema`, `/templates/osdu/...`, `POST /delivery/templates/preview` | The saved templates, one laid out variable by variable, and the OSDU data definitions releases, kinds and schemas read from the Open Group's public repository. |
| `GET /delivery/mapping-builder/...`, `POST /delivery/mapping-builder/draft`, `/compose`, `/parse` | The mapping builder's repositories, caches, drafts, YAML and checks. |

**Operate group** (any authenticated user):

| Endpoints | Purpose |
| --- | --- |
| `POST /delivery/flows/{pipelineId}/release`, `/probe` | Release held records for a flow; probe the flow's OSDU target. |
| `POST /delivery/records/{flowId}/{key}/release`, `/redeliver`, `/verify`, `/read`, `/delete` | The per-record interventions. Release, redeliver and verify act on the ledger under the caller's name or queue a run; probe, read-back and delete queue a compute task for a node that can reach the target. |
| `POST /delivery/flows/{pipelineId}/records/remove`, `/remove/preview` | Bulk removal by explicit keys or by the listing filter, with the scope named explicitly. |

**Author group**: `POST /delivery/templates` and `DELETE /delivery/templates` save a bundled schema as a template
version and delete a version no synced mapping pins.

**Admin**: `POST /delivery/ledger/prune` additionally requires the `admin` scope.

Every intervention is recorded in the ledger with the requesting user and the run or compute task it produced.
That is the rule the whole product rests on: if it happened to a record, the ledger says so, and no endpoint
exists that changes a record without writing there.

## Background services

| Service | What it does |
| --- | --- |
| Cache update rollout | Carries an approved change in a data partition's cache out to the records that were built from the old value, queueing them for redelivery. |
| Data definitions warmup | Downloads and keeps the local copy of the OSDU data definitions release the templates are captured from, so a capture or a schema browse does not wait on the first download. |

## Configuration

The module binds its own options section alongside `ControlPlane`, registered through the host module extension
point. The values that matter operationally:

- **The catalog connection carries the `osdu` schema too.** The module database declares no connection of its
  own on this tier, so `SQLFLOW_CATALOG_DB` (or `ControlPlane:Catalog:ConnectionReference`) covers both. Startup
  applies SQLFlow's catalog migrations and then the module's.
- **Startup refuses a mismatch.** Pending OSDU migrations, a database newer than this build, or a SQLFlow catalog
  older than the module requires each stop the host with a message naming the migration or version, rather than
  failing later on whichever request first touches a missing table.
- **No OSDU credential lives here.** The endpoints above read and write the ledger; delivering to OSDU happens on
  a node, which holds the endpoint credential. A probe, a read-back or a delete from the GUI is queued as a
  compute task for that reason, not executed in the API.

## See also

- [Authentication and identity](authentication-and-identity.md): who may operate the delivery surface.
- [../cli/control-plane.md](../cli/control-plane.md): the same API from a terminal.
- [../../architecture.md](../../architecture.md): where the module sits in the platform.
