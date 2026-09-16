# Rebuild plan: OSDU Delivery as a dedicated solution on SQLFlow

OSDU Delivery becomes a dedicated solution in this repository: its own product, built on its own vendored copy of SQLFlow
(`sqlflow/`, SQLFlow `fb8d5a6`), and able to adopt SQLFlow's improvements with a `git subtree pull`. The OSDU code is copied
from the previous implementation at `D:\Projects\eq\src\osdu-delivery` (commit `7377609`) into `osdu/`. The SQLFlow repository
itself is never changed.

Each stage lists what it changes and the tests that close it. A stage is finished only when those tests pass; nothing moves
to the next stage before that.

## Why

- **Adopting SQLFlow stays easy.** The previous implementation copied SQLFlow's platform and then changed it in place
  (lineage, ingestion and most flow kinds removed, dispatch rewritten, the catalog moved from migrations to `EnsureCreated`),
  so SQLFlow improvements could no longer reach it. Here SQLFlow stays SQLFlow: this project adds only a small set of generic
  extension points to `sqlflow/`, each in its own commit, and everything OSDU lives in `osdu/`. A SQLFlow update is a subtree
  pull whose conflicts, if any, are confined to the files carrying those extension points.
- **Visible stages.** Data arrives through SQLFlow's own pre-ingestion and ingestion flows instead of a drop manifest and a
  replica inside the delivery flow. Each stage is its own flow with its own run, log and failure, ordered by lineage, so an
  operator can see and debug where a record is.
- **A real schema lifecycle.** The OSDU tables get their own schema, migration history and version, so the ledger can be
  upgraded in production without touching SQLFlow's catalog.

## Target layout

```text
osdu-delivery/
  OsduDelivery.sln                        the dedicated solution: SQLFlow's projects from sqlflow/ and the OSDU projects from osdu/
  sqlflow/                                SQLFlow, vendored; generic extension points only
  osdu/                                   everything OSDU Delivery adds
    src/SqlFlow.Delivery/                 the OSDU module: flow kind, executor, ledger, protocols, rendering, mappings, templates, cache
    src/SqlFlow.Delivery.Data/            OsduDbContext, the osdu schema model, its migrations and schema version
    src/SqlFlow.Delivery.ControlPlane/    delivery and template endpoints, cache rollout and data definitions services
    src/SqlFlow.Delivery.Cli/             the check, cache and template verbs
    hosts/                                control plane, node and CLI builds that compose SQLFlow with the module and its branding
    gui/                                  the OSDU pages, panels, API client and e2e specs, registered through the GUI module contract
    samples/                              a sample estate: pre, ing and OSDU flows, mappings, cache flow, source files
    tests/                                the module's suites (unit and SQL Server)
    docs/                                 product and design documentation
  tools/check-vendored-sqlflow.sh         guards the vendored SQLFlow
```

`osdu/README.md` lists where each part of `osdu/` came from and what was left behind.

## Stage 0: repository foundation (done)

- `sqlflow/` holds SQLFlow `fb8d5a6` as a squashed git subtree.
- `osdu/` holds the OSDU code copied from the previous implementation, without the drop, replica and SQL source path.
- `tools/check-vendored-sqlflow.sh` lists this project's changes to `sqlflow/` and fails when a commit mixes `sqlflow/` with
  other paths, or when a line added to `sqlflow/` mentions OSDU or the delivery module.
- `CLAUDE.md` carries the project rules, including the vendored SQLFlow rule and the `osdu` schema rule.

## Stage 1: extension points in the vendored SQLFlow

Made in `sqlflow/` of this repository. Every extension point is generic (any module could use it), lands as its own commit
touching only `sqlflow/` with a subject starting `sqlflow:`, and follows SQLFlow's standards: catalog changes ship with their
migration, zero warnings, SQLFlow's existing flow kinds keep their behaviour, and SQLFlow's own suites pass with new tests
for the extension point.

| Extension point | What SQLFlow gains | Where in `sqlflow/` today |
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

Closes when: SQLFlow's solution in `sqlflow/` builds clean, SQLFlow's unit and SQL Server suites pass, each extension point
has tests with a test-only module, and `tools/check-vendored-sqlflow.sh` passes.

## Stage 2: the OSDU backend module and the dedicated solution

- Create `OsduDelivery.sln` with SQLFlow's projects from `sqlflow/` and the OSDU projects from `osdu/`.
- Wire the delivery domain copied into `osdu/src/SqlFlow.Delivery` onto SQLFlow through stage 1's extension points: the flow
  kind and executor, the ledger, the protocols (record, well log, file, manifest), rendering, the preflight gate, templates
  and the mapping builder, the OSDU cache and its refresh, the worker, verifier, retrieval, removal and fan-out.
- Build `osdu/src/SqlFlow.Delivery.Data` with `OsduDbContext`:
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
- Register the control plane endpoints (delivery, templates), the background services (cache rollout, data definitions
  warmup) and the CLI verbs (`check`, `cache`, `template`) through the host module extension point, and build the OSDU
  Delivery hosts in `osdu/hosts`.

Closes when: `OsduDelivery.sln` builds clean, the OSDU unit suites and the ledger's SQL Server suites pass against a disposable
database provisioned by migrations, and the schema-scope check passes.

## Stage 3: the OSDU GUI module

- Register the OSDU pages copied into `osdu/gui`: delivery overview, flow tabs, record and submission pages, audit trail,
  mappings, templates, mapping builder, OSDU cache.
- Register routes, navigation and the per-kind panels through stage 1's GUI module contract; generic shared components the
  pages need and SQLFlow lacks are added to `sqlflow/` as extension-point commits, OSDU-specific ones stay in `osdu/gui`.
- The OSDU Delivery host build applies the branding: "OSDU Delivery, powered by SQLFlow".

Closes when: the GUI builds and lints clean for the OSDU Delivery build, and the OSDU e2e specs pass against the OSDU Delivery
host.

## Stage 4: the OSDU flow reads ingestion tables

- The OSDU flow's source becomes ingestion tables: a record table and child tables with their join keys, the payload location
  column, and SQLFlow's system columns (`UpdatedDate_DW` for incremental reads, `FileName_DW` and `RowNumber_DW` for each
  record's origin, stored in the ledger).
- Planning reads the tables in pages, incrementally by `UpdatedDate_DW`; a full re-plan reads them all; fan-out slices by key.
- The flow's lineage contribution declares the ingestion tables it reads, so SQLFlow orders pre, ing and OSDU flows in waves.
- Records delivered by hand are files placed where a pre-ingestion flow reads them; the module takes no records through an
  API of its own.
- The sample estate becomes a pre flow, an ing flow and an OSDU flow per dataset (well logs, curves), with source files.
- The engine suites that used sample drops are re-fixtured on ingestion tables; the SQL Server end-to-end suite runs the whole
  chain: files, pre, ing, OSDU flow with a fake protocol.

Closes when: the chain runs end to end on SQL Server in the test suites, lineage orders it in waves, and every copied and new
suite passes.

## Stage 5: live verification

- Run the sample chain against the live OSDU test partition with the project's rule for live work: every id logged in
  `.sqlflow/live-e2e/actions.log` when created, removed at the reversible scope when done, and each confirmed absent.
- Update the documentation in `osdu/docs` for the new architecture: how data arrives, the OSDU flow document, the `osdu`
  schema and its upgrade path, operations.

Closes when: records are delivered and verified in the live partition, cleanup is confirmed, and the documentation matches the
code.

## What moves from the previous implementation

| Area | Verdict |
| --- | --- |
| Ledger, protocols, rendering, templates, mapping builder, cache and refresh, document loader and model, worker, verifier, retrieval, removal, preflight | Copied; wired onto the extension points |
| Planner, intake, `FlowRuntime`, `DeliveryExecutor` | Copied; their input changes from a drop to ingestion tables |
| Delivery and template endpoints, CLI verbs, OSDU GUI pages | Copied; registered through the host and GUI module extension points |
| Ledger entities | Copied; become `OsduDbContext` in the `osdu` schema, delivered by migration |
| Drop manifest and reader, scope file readers, replica, SQL source extraction, inline drops, drop-off area, known-state publishing | Left behind; SQLFlow's pre and ing flows replace them |
| Manual submission (records sent in a request, their landing files and chain, the submission page and dialog) | Copied, then removed: the pre and ing flows cover records delivered by hand |
| The previous implementation's platform changes (kind registry, executor and compute registries, run parameters, fan-out, sync extension) | Not copied; redone as stage 1's generic extension points in `sqlflow/` |

## Adopting SQLFlow updates

1. `git subtree pull --prefix=sqlflow --squash B:/SQLFlowV3 main`.
2. Resolve any conflict in the files that carry this project's extension points, keeping SQLFlow's change and the extension
   point.
3. `tools/check-vendored-sqlflow.sh`, then build `OsduDelivery.sln` clean and run SQLFlow's and the OSDU suites.

## Risks

- **Extension points in central SQLFlow files.** The loader, executor, run parameters, catalog sync and lineage collector are
  files SQLFlow keeps changing, so these are where subtree pulls can conflict. Keeping each extension point small and generic
  keeps those conflicts small.
- **SQLFlow's GUI pages move.** The per-kind panel slots sit in SQLFlow's pipeline, run and trigger pages, which a later
  SQLFlow update may reshape.
- **Stage 4 is new design.** It is proven only by its new SQL Server end-to-end suite, not by the copied tests.
