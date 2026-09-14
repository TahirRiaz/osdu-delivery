# OSDU Delivery Architecture

OSDU Delivery publishes subsurface records from data drops into an OSDU platform and keeps every record
traceable. It is two things: a platform inherited from SQLFlow V3 that schedules, runs, and observes flows,
and a delivery domain that turns drop rows into OSDU records and records what happened to each of them.

## The platform

```
git repositories ──sync──▶ catalog (SQL Server) ◀──claim/complete── compute nodes (sqlflow worker)
      ▲                        ▲          ▲                                  │
   proposals               API + GUI   scheduler                     executes the flow,
   (pull requests)         + CLI       (cron / interval / chains)     streams the trace
```

- **Flow repositories** are git repositories of YAML documents. The control plane's managed sync clones each
  registered source, parses every document through the kind registry, and projects it into the catalog as a
  pipeline row (name, kind, batch, source and target reference, execution mode, lifecycle, the YAML itself and
  its parsed definition). A run is pinned to the commit it was enqueued from, and the executing node
  materializes exactly that commit (or the catalog's content snapshot of the document when the document needs
  no sibling files).
- **The catalog** (`src/SqlFlow.Catalog`, EF Core on SQL Server) is the read model of the estate and the
  system of record for everything operational: the durable run queue, run groups and waves, schedules and
  their chains, nodes and worker pools, users, tokens and roles, notifications, activity traces, and the
  delivery ledger. The schema is created from the EF model; a schema change means provisioning the database again.
- **The control plane** (`src/SqlFlow.ControlPlane`) is the ASP.NET Core host: the `/api/v1` API (JWT and
  personal access tokens, read / operate / author / admin scopes), the scheduler, the managed git sync, the
  notification service, the orphan reaper, and bootstrap provisioning. It is stateless across replicas: every
  claim is an atomic catalog write.
- **Compute nodes** (`src/SqlFlow.Node`, started by `sqlflow worker`) pull queued runs for the pools they
  serve, heartbeat, execute through the execution registry, stream run events to the catalog as they happen,
  and complete the run from its artifact. Pools scale on queue depth (KEDA); a node holds the credentials its
  pool's flows need, so nothing data-plane ever passes through the control plane.
- **The CLI** (`src/SqlFlow.Cli`) validates and runs documents locally, hosts the worker, migrates the catalog,
  and drives the control plane remotely (trigger, runs, schedules, repos, pipelines, search, nodes).
- **The GUI** (`gui/`) is a React workbench over the API: dashboard, runs with live traces, run groups, nodes
  and pools, repos and sources, pipelines, schedules and their timeline, search, users, tokens, notifications,
  and maintenance.

### Extension points

The platform never names a concrete flow kind. Three registries, filled in the host's composition root, are
the whole contract between the platform and a domain:

| Registry | Role |
| --- | --- |
| `IFlowDocumentKind` (`src/SqlFlow.Yaml`) | Parses the body of a document whose `flowType` it owns; the loader reads the envelope (schedule, mode, lifecycle) once for every kind. |
| `IFlowDocumentExecutor` (`src/SqlFlow.Execution`) | Executes a parsed document of its kind and produces the run outcome and artifact. |
| `IComputeOperation` (`src/SqlFlow.Core`) | A named operation a node can run outside a flow (a probe, a delete), queued through the compute task table. |

Every document exposes the same headers (`FlowDocument`): name, kind, batch, source reference, target
reference, credential references (for secret hygiene), and whether it needs the repository tree. The estate
scan, the catalog sync, the run queue, and the GUI work from those headers alone.

## The delivery domain

The delivery domain (`src/SqlFlow.Delivery`, documented in [delivery/README.md](delivery/README.md)) holds the
production flow kinds: `delivery`; `retrieval` for the reverse direction (OSDU's search index paged into
files on the lake, incremental by watermark, one ledger row per run); and `cache`, which defines the reference and
master data the mappings resolve against and captures it from OSDU into one versioned cache per OSDU data partition in the catalog. A delivery flow names a drop (a storage location the preparing side writes: a manifest,
parquet record scopes, payload chunks), a pinned mapping (how rows become OSDU records of one kind, which
columns identify a record, which values resolve against reference data), the cache the mapping reads (the cache of the partition it delivers to, at its current version or the one `render.cacheVersion` pins),
and an OSDU target (endpoint, auth and header references, the delivery protocol). The mapping it renders with lives in
the flow's repository and is synced into the catalog as a read model; the template the mapping pins (the OSDU schema of
its kind) and every version of the cache are held in the catalog itself. Running the flow means:

1. **Intake**: register the drop's submission under its manifest id, stream every record through the pinned
   mapping against its pinned template and the cache version it reads, and decide per record what changed (source versions, fingerprints,
   independent metadata and payload hashes); write the pending work to the ledger and the rendered documents
   to work batch files on the flow's work location.
2. **Deliver**: lease work batches, send their records through the flow's protocol (a batched record write; a
   record plus a streamed payload; files uploaded, registered and referenced; a manifest handed to the
   ingestion workflow and polled), report every step and what the target returned, and write one append-only
   attempt per try; back off and retry, resume after completed steps, hold what cannot be fixed by retrying,
   and release leases on a stop so any number of nodes share the work. A large submission fans its intake and
   its drains out over member runs across the fleet.
3. **Verify**: read delivered records back from OSDU and compare versions; queue drifted records for
   redelivery when the flow reconciles. **Known state** publishes the compact view the preparing side reads.

A delivery run is a platform run: its operation (deliver, verify, plan, known-state, intake, drain; retrieve
on a retrieval flow; refresh on a cache flow), scope and force flag are its run parameters, its log is the run trace, and its counts are projected onto the run row. The ledger
(the `delivery` schema of the catalog) holds submissions, records, attempts, source watermarks and the audit
trail of interventions (release, redeliver, delete, verify) with who did them, when, and the run they ran in.
The target-side interventions (probe, read back, delete) run on a node as compute tasks. Statistics, record
search and history, submission views and the interventions in the GUI, the API and the CLI are all views over
the ledger.

## Principles

1. **Traceability over everything.** If it happened to a record, the ledger says so. No path bypasses it.
2. **Secrets never live in documents.** `${env:...}` and `${keyvault:...}` references only; resolved values are
   redacted before they reach any log, trace, error, or artifact.
3. **Control plane versus compute.** The control plane decides what and when and owns state; nodes execute
   near the data and pull their work, so they can run inside a customer's network.
4. **Single code path.** One implementation per feature, extended rather than duplicated, at every layer.
5. **Logic in code, not the database.** The catalog is a clean model and state store; behavior lives in C#.
