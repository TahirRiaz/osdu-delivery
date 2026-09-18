# OSDU Delivery Architecture

OSDU Delivery publishes subsurface records into an OSDU platform and keeps every record traceable. It is a module
on SQLFlow, not a fork of it: SQLFlow is vendored in `sqlflow/` and lands and loads the source data, and everything
OSDU Delivery adds lives in `osdu/`.

The platform underneath (the control plane, the catalog, the dispatcher, compute nodes, schedules, the GUI shell)
is SQLFlow's, and it is documented in the vendored tree:
[../../sqlflow/docs/architecture.md](../../sqlflow/docs/architecture.md). This page describes what OSDU Delivery
adds on top and how the two meet.

## Data arrives through three flows

```text
source files ──pre──▶ pre tables ──ing──▶ ingestion tables ──osdu──▶ OSDU platform
                                                              │
                                                              └──▶ the ledger (osdu schema)
```

| Step | Flow | What it does |
| --- | --- | --- |
| 1 | SQLFlow pre-ingestion | Lands the source files (CSV, JSON, Parquet) into a raw table and builds its typed view. |
| 2 | SQLFlow ingestion (`flowType: ing`) | Loads the typed view into a keyed ingestion table, with schema evolution and change detection. |
| 3 | OSDU flow (`flowType: delivery`) | Reads the ingestion tables, renders each record with its pinned mapping and the OSDU cache, delivers it, and records everything in the ledger. |

SQLFlow's lineage orders the three like any other flows, so an operator sees where a record is, and each stage has
its own run, log and failure. There is no drop manifest, no drop reader and no replica: the pre and ingestion
flows replace all of that.

The OSDU flow reads its records through the ingestion tables' own columns and SQLFlow's system columns:
`UpdatedDate_DW` drives the incremental read, and `FileName_DW` and `RowNumber_DW` become each record's origin in
the ledger, so a delivered record still points back at the source file and row it came from.

## What the module adds

| Piece | Where | Role |
| --- | --- | --- |
| The flow kinds | `osdu/src/SqlFlow.Delivery` | `delivery` (render and deliver), `retrieval` (OSDU's search index paged onto storage), and `cache` (the reference and master data mappings resolve against, captured from OSDU into one versioned cache per data partition). |
| The ledger and the rest of the `osdu` schema | `osdu/src/SqlFlow.Delivery.Data` | Submissions, records, append-only attempts, watermarks, the audit trail, the mapping and cache read models, the templates and the cache versions. |
| The delivery API | `osdu/src/SqlFlow.Delivery.ControlPlane` | Everything under `/api/v1/delivery`, plus the cache rollout and data-definitions background services. |
| The CLI verbs | `osdu/src/SqlFlow.Delivery.Cli` | `sqlflow check`, `sqlflow cache`, `sqlflow template`. |
| The GUI pages | `osdu/gui` | The delivery overview, the per-flow tabs, the record and submission pages, the audit trail, mappings, templates, the mapping builder and the OSDU cache page, installed into SQLFlow's GUI through its module contract. |
| The hosts | `osdu/hosts` | The control plane, the node and the CLI, each composing SQLFlow with the module and the product's branding. |

## How the module meets SQLFlow

The platform never names a concrete flow kind. The module registers itself through SQLFlow's extension points,
and nothing else in `sqlflow/` knows OSDU exists:

| Extension point | What the module registers |
| --- | --- |
| Flow kind registry | The `delivery`, `retrieval` and `cache` document kinds, and the mapping companion document. |
| Executor | The run executors for those kinds. |
| Compute operations | The target-side operations a node runs outside a flow: probe, read back, delete. |
| Run parameters | The operation, the flow's parameter values and the kind-owned JSON payload a run carries. |
| Catalog sync extension | The mapping documents and the types each cache flow declares, projected during a repository sync, inside the sync's transaction. |
| Module database | `OsduDbContext` over the `osdu` schema, with its own migrations and schema version. |
| Run result projection | The delivery counts shown on the run list. |
| Lineage contributor | What each OSDU flow reads and writes: the ingestion tables, payload files and cache types a delivery flow reads and the OSDU type it writes, the types a cache flow reads and the cache types it writes, the types a retrieval flow reads and the files it lands. Lineage shows them as nodes and orders pre, ing, delivery, cache and retrieval flows in waves. |
| Catalog sync lineage gate | A changed mapping document recomputes the repository's lineage, since a delivery flow's OSDU type comes from its mapping. |
| Host modules | The endpoint groups under SQLFlow's authorization policies, the background services, the options sections and the CLI verbs. |
| GUI module contract | The routes, navigation entries and per-kind panels on the pipeline, run and trigger pages. |
| Branding | "OSDU Delivery, powered by SQLFlow". |

## The `osdu` schema is separate

Every OSDU table, view and index lives in the dedicated `osdu` schema, owned by the module's own EF Core context,
with its own migration history (`[osdu].[__EFMigrationsHistory]`) and its own schema version
(`[osdu].[SchemaVersion]`, which also records the minimum SQLFlow catalog migration it requires). SQLFlow's
catalog model never contains an OSDU table, and OSDU never changes SQLFlow's tables: there are no foreign keys and
no EF navigations from `osdu` into them, only plain ids.

Every model change ships with its migration, so the ledger can be upgraded in production without touching
SQLFlow's catalog. The hosts and `sqlflow db status` refuse to run against pending OSDU migrations, a database
newer than the code, or a catalog older than the module requires, and name the migration or version.

The module database is either a database of its own (`OSDUDelivery` in the shipped deployments) or the catalog's
own, and the connection reference decides which: given one, the control plane migrates, verifies and reads that
database; given none, the `osdu` schema sits in the catalog database and the control plane reaches it on the
catalog connection. Both are supported, and an estate cannot move between them by changing the setting after the
first migrate. Two databases is the shape to prefer where the ledger grows with the records rather than with the
metadata, and the shape Azure SQL forces, since no statement there reaches across two databases: the one place
that wrote module rows inside the catalog's own transaction, the repository sync, asks where the rows are
(`ModuleDatabase.IsReachableOn`) and commits its own work when they are elsewhere, reconciling idempotently from
the repository so the next sync settles what a failure left behind.

**A compute node opens no catalog connection at all** (it speaks only the node protocol), so a node always needs
the module's connection reference, whichever shape the estate is: see
[environment-variables.md](environment-variables.md). Without it a node validates and plans but delivers nothing.

## A delivery run

A delivery run is an ordinary platform run: its operation, the flow's parameter values and the kind's JSON payload
are its run parameters, its log is the run trace, and its counts are projected onto the run row. A flow that declares
interfaces is one source delivering several OSDU types: its run checks every interface first, then takes them in the
waves of what they wait for, each interface going through the steps below under a ledger identity of its own
([operations.md](operations.md#running-a-source)).

1. **Intake.** Register the submission, read the changed records from the ingestion tables, stream each through the
   pinned mapping against its pinned template and the cache version it reads, and decide per record what changed
   (fingerprints, business version, render context, independent metadata and payload hashes). The pending work goes
   to the ledger and the rendered documents to work batch files on the flow's work location.
2. **Deliver.** Lease work batches, send their records through the flow's protocol (`osduRecord`, `osduWellLog`,
   `osduFile`, `osduManifest`), report every step and what the target returned, and write one append-only attempt
   per try. A worker keeps one lease row per claim and appends what it learns; the lease applies that to the records
   at each renewal and when it closes. Back off and retry, resume after completed steps, hold what retrying cannot
   fix, and hand leases back on a stop, so any number of nodes share the work. A large submission fans its intake and its drains out over member
   runs across the fleet.
3. **Verify.** Read delivered records back from OSDU and compare versions; queue drifted records for redelivery
   when the flow reconciles.

The ledger holds submissions, records, attempts, source watermarks and the audit trail of every operator
intervention (release, redeliver, delete, verify) with who did it, when, and the run it ran in. Statistics, record
search, history, submission views and the interventions in the GUI, the API and the CLI are all views over it.

## Principles

1. **Traceability over everything.** If it happened to a record, the ledger says so. No path bypasses it.
2. **Secrets never live in documents.** `${env:...}` and `${keyvault:...}` references only; resolved values are
   redacted before they reach any log, trace, error, artifact or ledger row.
3. **Control plane versus compute.** The control plane decides what and when and owns state; nodes execute near
   the data and take their work over the node protocol, so they can run inside a customer's network.
4. **The vendored SQLFlow takes generic extension points only.** No OSDU code, name or wording goes into
   `sqlflow/`.
5. **Single code path.** One implementation per feature, extended rather than duplicated, at every layer.
