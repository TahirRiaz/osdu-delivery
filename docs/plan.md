# Rebuild plan: OSDU Delivery as a module on SQLFlow

This plan rebuilds OSDU Delivery from the stripped SQLFlow fork at `D:\Projects\eq\src\osdu-delivery` into this repository,
as a module on an unmodified, vendored SQLFlow (`sqlflow/`, SQLFlow `fb8d5a6`). Each stage lists what it changes and the
tests that close it. A stage is finished only when those tests pass; nothing moves to the next stage before that.

## Why

- **One engine.** The fork copied SQLFlow's platform and then diverged from it (lineage, ingestion and most flow kinds were
  removed, dispatch was rewritten, the catalog moved from migrations to `EnsureCreated`), so SQLFlow improvements no longer
  reach it. Vendoring an unmodified SQLFlow and putting OSDU on top through extension points keeps every future SQLFlow
  change one `git subtree pull` away.
- **Visible stages.** Data arrives through SQLFlow's own pre-ingestion and ingestion flows instead of a drop manifest and a
  replica inside the delivery flow. Each stage is its own flow with its own run, log and failure, ordered by lineage, so an
  operator can see and debug where a record is.
- **A real schema lifecycle.** The OSDU tables get their own schema, migration history and version, so the ledger can be
  upgraded in production without touching SQLFlow's catalog.

## Target layout

```text
osdu-delivery/
  sqlflow/                         SQLFlow, vendored, never edited here
  src/
    SqlFlow.Delivery/              the OSDU module: flow kind, executor, ledger, protocols, rendering, mappings, templates, cache
    SqlFlow.Delivery.Data/         OsduDbContext, the osdu schema model, its migrations and schema version
    hosts/                         control plane, node and CLI builds that compose SQLFlow with the module and its branding
  gui/modules/osdu/                the OSDU pages, panels and API client, registered through SQLFlow's GUI module contract
  samples/                         a sample estate: pre, ing and OSDU flows, mappings, cache flow, source files
  tests/                           the module's suites (unit and SQL Server)
  docs/                            product and design documentation
```

## Stage 0: repository foundation (done)

- `sqlflow/` holds SQLFlow `fb8d5a6` as a squashed git subtree.
- `tools/check-vendored-sqlflow.sh` fails on any difference between `sqlflow/` and its vendored commit.
- `CLAUDE.md` carries the project rules, including the vendored-code rule and the `osdu` schema rule.

## Stage 1: extension points in SQLFlow

Made in the SQLFlow repository (`B:\SQLFlowV3`, `main`), under its rules (catalog changes ship with migrations, zero warnings,
SQLFlow's own suites green), then pulled into `sqlflow/`. Every extension point is generic: SQLFlow keeps working without
any module, and its existing flow kinds keep their current behaviour.

| Extension point | What SQLFlow gains | Where in SQLFlow today |
| --- | --- | --- |
| Flow kind registry | A module registers a `flowType` (and companion documents such as mappings) instead of the loader's fixed dispatch | `src/SqlFlow.Yaml/YamlDocumentLoader.cs` (fixed loader list and `flowType` chain) |
| Executor fallback | A registered executor runs a document type the built-in branches do not handle; run artifacts and the requesting actor are available to it | `src/SqlFlow.Execution/DocumentExecutor.cs` (closed switch) |
| Compute operations | A module registers a named compute operation with free-form arguments | `src/SqlFlow.Execution` compute task executor (closed switch over datasource operations) |
| Run parameters | An operation name, a values dictionary and a kind-owned JSON payload beside SQLFlow's typed parameters, on runs, triggers and schedules | `src/SqlFlow.Core/Runs/RunParameters.cs`, run queue, schedule fire |
| Fan-out run groups | A run enqueues and waits for member runs of its own pipeline; members may run in parallel | `src/SqlFlow.Catalog` run queue (`RunGroupModes` has node and batch only) and the one-running-run-per-pipeline index |
| Catalog sync extension | A module projects its companion documents during a repository sync, inside the sync's transaction | `src/SqlFlow.Catalog/CatalogSync.cs` |
| Module database | A module registers its own EF context and migrations; `db migrate` and startup apply SQLFlow's catalog first, then each module; `db status` reports each | `src/SqlFlow.Catalog/CatalogDatabase` provisioning, CLI `db` verbs |
| Run result projection | A module maps its run result into the run list's counts | `src/SqlFlow.Catalog/CatalogProjection.cs` |
| Lineage contributor | A registered kind declares what it reads and writes, so it takes part in waves and dependencies; an unknown kind no longer fails collection | `src/SqlFlow.Lineage/Collection/FlowSetCollector.cs`, `FlowDocumentHeaders.cs`, `FlowDeclaredEndpoints.cs` |
| Host modules | A module adds services, endpoint groups under SQLFlow's authorization policies, hosted services, options sections and CLI verbs | control plane `Program.cs`, CLI `Program.cs` |
| GUI module contract | A module adds routes, navigation entries, panels on the pipeline, run and trigger pages, and shared components | `gui/src/App.tsx`, `gui/src/layout/nav.ts`, pipeline, run and trigger pages |
| Branding | A host sets the product name, logo and "powered by SQLFlow" lockup | GUI shell, login page, CLI banner, notifications |

Closes when: SQLFlow's solution builds clean, SQLFlow's unit and SQL Server suites pass, each extension point has tests
with a test-only module, and the result is pulled into `sqlflow/` with `tools/check-vendored-sqlflow.sh` passing.

## Stage 2: the OSDU backend module

- Move the delivery domain from the fork into `src/SqlFlow.Delivery`, registered through stage 1's extension points: the flow
  kind and executor, the ledger, the protocols (record, well log, file, manifest), rendering, the preflight gate, templates
  and the mapping builder, the OSDU cache and its refresh, the worker, verifier, retrieval, removal and fan-out.
- Create `src/SqlFlow.Delivery.Data` with `OsduDbContext`:
  - every OSDU table, view and index in the `osdu` schema;
  - the migration history in `[osdu].[__EFMigrationsHistory]`;
  - `[osdu].[SchemaVersion]` with the module version, the last migration, when and by whom, and the minimum SQLFlow catalog
    migration required;
  - version checks at host startup and in `sqlflow db status` (pending migrations, database newer than the code, SQLFlow
    catalog too old), each naming the migration or version;
  - the first migration, schema version 1.0.0, creating the ledger, mapping, template and cache tables and the indexed
    record count view.
- No foreign keys or navigations into SQLFlow's tables. A build check fails when the module's migration script touches a
  schema other than `osdu`, or SQLFlow's touches `osdu`.
- Move the control plane endpoints (delivery, templates), the background services (cache rollout, data definitions warmup)
  and the CLI verbs (`check`, `cache`, `template`) as module registrations.
- Leave behind what stage 4 replaces: the drop manifest and reader, the scope file readers, the replica, the SQL source
  extraction, inline-drop writing, the drop-off area and known-state publishing.

Closes when: the solution builds clean with the module, the moved unit suites and the ledger's SQL Server suites pass against
a disposable database provisioned by migrations, and the schema-scope check passes.

## Stage 3: the OSDU GUI module

- Move the OSDU pages into `gui/modules/osdu`: delivery overview, flow tabs, record and submission pages, audit trail,
  mappings, templates, mapping builder, OSDU cache.
- Register routes, navigation and the per-kind panels through stage 1's GUI module contract; shared components the pages need
  and SQLFlow lacks go into SQLFlow, not into the module.
- The OSDU Delivery host build applies the branding: "OSDU Delivery, powered by SQLFlow".

Closes when: the GUI builds and lints clean for both the plain SQLFlow build and the OSDU Delivery build, and the OSDU e2e
specs pass against the OSDU Delivery host.

## Stage 4: the OSDU flow reads ingestion tables

- The OSDU flow's source becomes ingestion tables: a record table and child tables with their join keys, the payload location
  column, and SQLFlow's system columns (`UpdatedDate_DW` for incremental reads, `FileName_DW` and `RowNumber_DW` for each
  record's origin, stored in the ledger).
- Planning reads the tables in pages, incrementally by `UpdatedDate_DW`; a full re-plan reads them all; fan-out slices by key.
- The flow's lineage contribution declares the ingestion tables it reads, so SQLFlow orders pre, ing and OSDU flows in waves.
- Records submitted through the API land where a pre-ingestion flow reads them.
- The sample estate becomes a pre flow, an ing flow and an OSDU flow per dataset (well logs, curves), with source files.
- The engine suites that used sample drops are re-fixtured on ingestion tables; the SQL Server end-to-end suite runs the whole
  chain: files, pre, ing, OSDU flow with a fake protocol.

Closes when: the chain runs end to end on SQL Server in the test suites, lineage orders it in waves, and every moved and new
suite passes.

## Stage 5: live verification

- Run the sample chain against the live OSDU test partition with the project's rule for live work: every id logged in
  `.sqlflow/live-e2e/actions.log` when created, removed at the reversible scope when done, and each confirmed absent.
- Update the documentation (`docs/`) for the new architecture: how data arrives, the OSDU flow document, the `osdu` schema and
  its upgrade path, operations.

Closes when: records are delivered and verified in the live partition, cleanup is confirmed, and the documentation matches the
code.

## What moves from the fork

| Area | Verdict |
| --- | --- |
| Ledger, protocols, rendering, templates, mapping builder, cache and refresh, document loader and model, worker, verifier, retrieval, removal, preflight | Moves as it is |
| Planner, intake, `FlowRuntime`, `DeliveryExecutor` | Moves; its input changes from a drop to ingestion tables |
| Delivery and template endpoints, CLI verbs, OSDU GUI pages | Move as module registrations |
| Ledger entities | Move into `OsduDbContext` in the `osdu` schema, delivered by migration |
| Drop manifest and reader, scope file readers, replica, SQL source extraction, inline drops, drop-off area, known-state publishing | Left behind; SQLFlow's pre and ing flows replace them |
| The fork's platform changes (kind registry, executor and compute registries, run parameters, fan-out, sync extension) | Reworked into SQLFlow's stage 1 extension points |

## Risks

- **SQLFlow keeps moving.** Stage 1 starts from SQLFlow's current `main`, and every extension point keeps SQLFlow's existing
  flows and suites working.
- **The GUI pages drifted.** SQLFlow's pipeline, run and trigger pages differ from the fork's, so the per-kind panel slots are
  designed against SQLFlow's current pages.
- **Stage 4 is new design.** It is proven only by its new SQL Server end-to-end suite, not by the moved tests.
