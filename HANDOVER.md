# Handover

Workspace: `B:\osdu-delivery` (this repository). Branch `main`, pushed to `origin`
(<https://github.com/TahirRiaz/osdu-delivery.git>).

`D:\Projects\eq\src\osdu-delivery` is the **old fork**, read-only reference only. `B:\SQLFlowV3` is upstream SQLFlow and
is never modified.

## State (2026-09-18, after the live wave, the upstream fixes and the database split)

| Suite | Result |
| --- | --- |
| `osdu/tests/SqlFlow.Delivery.Tests` | 1757 passed, 0 skipped, against a real SQL Server |
| `osdu/tests/SqlFlow.Delivery.ControlPlane.Tests` | 32 passed, 0 skipped (the four new two-database tests included) |
| `sqlflow/tests/SqlFlow.Core.Tests` | 5017 passed, 226 skipped (environment-gated) |
| `sqlflow/tests/SqlFlow.ControlPlane.Tests` | 570 passed, 3 skipped (foreign engines) |
| `sqlflow/tests/SqlFlow.Dispatch.Tests` | 62 passed |
| `sqlflow/tests/SqlFlow.Acquire.Tests` | 124 passed |
| `sqlflow/tests/SqlFlow.Copy.Tests` | 37 passed |
| `sqlflow/tests/SqlFlow.Sftp.Tests` | 13 passed |
| `sqlflow/tests/SqlFlow.Translate.Tests` | 39 passed |
| `sqlflow/tests/SqlFlow.SlackBot.Tests` | 26 passed |
| GUI end to end (`osdu/gui`, `npm run e2e`) | 29 passed, from empty databases |
| `osdu/gui` `npm run lint`, `npm run build` | clean |
| `bash tools/check-vendored-sqlflow.sh` | exit 0 |
| Builds | `-t:Rebuild`: zero warnings, zero errors |

Run the suites one at a time. A build while a suite is running swaps the binaries under it: doing that once cost 150
false failures in `SqlFlow.ControlPlane.Tests`, which passes on its own.

## Tracing an id through the chain (2026-09-20, second pass)

The first pass made the delivery ledger reachable; this one answers the actual question: **take an id and say where it
is across pre-ingestion, ingestion and OSDU.**

- **The identity index** (`osdu.RecordIdentity`, migration `RecordIdentityIndex`, module version **1.9.0**). One row
  per value a record is known by: the columns a mapping declares in the new `dataset.identity`, the source key and its
  columns, the words of the label, the OSDU id and its part after the last colon, and the ingestion file. Folded to
  upper case in `Token`, kept as written in `Display`, with `Kind`. The primary key leads with the token, so any
  identifier is one seek across every flow. `RecordIdentities` in `osdu/src/SqlFlow.Delivery/Ledger/RecordIdentity.cs`
  is the only place the token set is decided. A staging rewrites a record's rows as a set, so a record is found by what
  it is now, not what it was; `RecordIdentityBackfillService` fills in records the ledger already held, a page at a
  time, repeatable and resumable. The lookup's old four-column prefix scan is gone.
- **The chain** (`GET /api/v1/delivery/records/{flowId}/{key}/chain`, `RecordChain.cs`). The record carries the
  ingestion file its version came from; `catalog.RunFile` says which runs handled a file of that name, so the runs that
  landed and loaded it are facts, not a reconstruction. The flow's kind names the stage (a file kind is
  pre-ingestion, `ing` is ingestion). A file no run recorded returns no stages and a note saying why. In the e2e
  estate the chain ran through the CLI, so `catalog.RunFile` is empty there and the note is what the spec asserts.
- **`sqlflow:` commit `c0ad79d`** added `catalog.RunFile(Name, RunId)` (migration `RunFileByName`) with its own test.
  Without it, "which runs handled this file" scans every processed file the estate has recorded. It is generic and
  upstream wants it regardless; it is recorded in `docs/sqlflow-changes.md`.
- The GUI: the Records page shows which value matched and what it is; the record's journey strip opens with
  pre-ingestion and ingestion before the ledger's own milestones, and the chain runs take their place in the timeline.
- Verification: `SqlLedgerTests` (identity tokens, restaging, backfill, the batch filter), `SqlServerLedgerTests`,
  `DeliveryRecordLookupApiTests`, `DeliveryRecordChainApiTests`, `RunFileByNameTests` (in `sqlflow/`), and
  `20-record-trace.spec.ts` extended for the declared wellbore id and the chain.

| Suite | Result on 2026-09-20 |
| --- | --- |
| `osdu/tests/SqlFlow.Delivery.Tests` | 1760 passed, 1 failed under load (`EtpSessionTests`, a live local socket; 15/15 alone) |
| `osdu/tests/SqlFlow.Delivery.ControlPlane.Tests` | 34 passed |
| `sqlflow/tests/SqlFlow.ControlPlane.Tests` | 571 passed, 3 skipped (foreign engines) |
| `osdu/gui` e2e specs 19 and 20 | 3 passed |
| `osdu/gui` typecheck, lint, build | clean |
| `bash tools/check-vendored-sqlflow.sh` | exit 0 |

Two tests now fail only when the machine is busy and pass alone: `SqlServerChainTests` and `EtpSessionTests`. Both
open something real (a SQL Server transaction, a local WebSocket), which is why the note at the top of this file
about running the suites one at a time matters.

## Record traceability in the GUI (2026-09-20)

The ledger and the API had held every trace from the start (a record's attempts, interventions, redelivery, release,
single and bulk removal); what was missing was the way to them from the product's front door, and one batch semantic.

- **Records** (Operate, `/delivery/records`) finds a record across every flow from what an operator holds (source
  key, label, OSDU id, delivery key, ingestion file name), narrowed by status; the Delivery page carries the same
  field. Behind it is `GET /api/v1/delivery/records?search=&status=`, the ledger's `LookupAsync` (now with a status),
  which the combined search already read; `DeliveryRecordHits` in `RecordSearchContributor.cs` builds the hit for both.
- **A record's journey** (`RecordJourney.tsx`) sits above the record page's tabs: a milestone strip (received, planned,
  dispatched, landed, verified, removed) over a timeline built client-side from the record, its attempts and its
  record-scoped activities. The header now shows the received-from file and row. A file name finds only records that
  were delivered from it: the current origin is set when a delivery promotes the pending one.
- **A batch is undone from its submission's page.** `RecordQuery.DeliveredBySubmissionId` (API `deliveredBy`, records
  tab `?delivered=`, removal filter `DeliveredBySubmissionId`) is the set of records a submission delivered, resolved
  through its delivered attempts over the `(SubmissionId, Outcome, Phase)` index. `SubmissionId` is a different set:
  the records a submission last planned, which a later submission moves on (`MarkSkippedAsync` moves an unchanged
  record's `LastSubmissionId`), so it was never "the batch we ran". No migration: the index existed.
- Verification: `SqlLedgerTests.A_submission_s_delivered_records_stay_its_own_after_later_submissions_move_them_on`
  (SQLite), `DeliveryRecordLookupApiTests` (real database), `20-record-trace.spec.ts` (e2e, no OSDU reached, no
  removal run; passes). `SqlServerChainTests` fails one test when the suite runs beside a build and passes alone, as
  the note above says. In the same e2e run, `03-seed`, `06-pipelines`, `07-lineage` and `18-interfaces` failed
  because the Pipelines page now renders a folder tree (`treeitem`) while those specs still click `table-row`: that
  is the concurrent pipelines-tree work, not this change. The removal dialog now scrolls inside a short viewport;
  at 1280x720 its buttons were past the bottom of the window.
- **A control plane started with `dotnet run` prints every request and every SQL statement.** SQLFlow's
  `appsettings.json` (framework categories at Warning) reaches the OSDU host's build output through the project
  reference, so a published container has it; `dotnet run` takes the project directory as its content root, where
  there is no such file, so `dev.bat`, F5 and the e2e host log at Information for everything. Do not add an
  `appsettings.json` to the OSDU host: it would replace the platform's file in the output and change what the
  containers run with (`Bootstrap:ApplyMigrations`, rate limits, dispatch timings). Start the host from its output
  instead (`SqlFlow.Delivery.ControlPlane.Host.exe --contentroot <bin folder>`), or set the two
  `Logging__LogLevel__Microsoft.*` variables. The worker hosts log through the CLI's logger
  (`SqlFlow.Cli/Program.cs`, minimum level Information, no category filters); SQLFlow's worker opens no catalog
  connection, but the OSDU worker runs EF Core through the ledger, so its container logs likely carry every ledger
  statement. That is a follow-up: a `sqlflow:` extension point for logging filters on the worker, or an OSDU-side
  filter, not a change made here.
- Running the e2e beside a leftover e2e control plane: one was still listening on 5299 from the Debug bin, which
  also blocks a Debug build of the host. Build the hosts in Release, run with `SQLFLOW_E2E_DOTNET_CONFIGURATION=Release`
  and `SQLFLOW_E2E_GUI_PORT=5174` (5173 is `dev.bat`'s GUI), and stop only the orphaned 5299 host.
- The backend half of this change (ledger, operations, endpoints, contributor, their tests) was committed by a
  concurrent session's `git add -A` inside `96db68f` ("Record the repo outline fix in the SQLFlow change log"); the
  GUI, the e2e spec, the API test and the docs are the uncommitted remainder.

## The databases of an estate (2026-09-18)

All of SQLFlow's metadata is one database. What is separated is the source data, where the volume is.

| Database | Holds | Grows with |
| --- | --- | --- |
| `SQLFlow` | the metadata: SQLFlow's `catalog` schema (pipelines, runs, schedules, lineage, sources, users) and the delivery module's `osdu` schema beside it (the record ledger, mappings, templates, OSDU caches) | the estate's activity, and every record delivered |
| `OsduDeliveryPre` | what pre-ingestion lands from the source systems | the source data |
| `OsduDeliveryIng` | the keyed ingestion tables the OSDU flows read | the source data |

- The two schemas stay separate things inside that database: their own EF contexts, their own migration histories, their
  own versions, no foreign key between them. That is what lets an estate that must keep them in **two** databases do so:
  name the second in `SQLFLOW_OSDU_DB` (or `Osdu:Database:Connection`) before the first migrate. On Azure SQL that is
  the only way to separate them, since no statement there reaches across two databases. It is a capability, not the
  default: a commit on 2026-09-18 made it the default in every deployment, which was wrong, and `deb950d` reverted it.
- A node always needs that connection, pointed at whichever database holds the schema, because a node opens no catalog
  connection at all. Without it a node validates and plans but delivers nothing. Two deployments never mounted it (the
  k8s worker manifest named the secret and mounted nothing; the bicep estate left it to `workerFlowEnv`); both do now.
- The metadata database needs no setting of its own. Until 2026-09-18 the ledger read every listing, wait and claim in
  a snapshot transaction, so that database had to allow snapshot isolation, and one that did not stopped every node from
  claiming any work while `/health/ready` still reported 200. That requirement is gone (`a476eed`), and so is the
  `osdu.RecordCount` indexed view that caused the contention it was hiding (`3434b82`, `RetireRecordCountView`, module
  version 1.8.0): SQL Server maintained the view inside the transaction of every record write, so a flow's nodes all met
  on its few count rows. Statistics are counted from the records now, by one path on every database, and a record and
  its lease are read in one statement. What still wants snapshot isolation is the **source** database a flow reads,
  where a record and its child rows come back as several result sets, and that one is per-flow
  (`isolation: readCommitted`).
- The repository sync was the one place that wrote the module's rows inside the catalog's transaction. It now asks where
  they are (`ModuleDatabase.IsReachableOn`): in one database they still ride the sync's transaction, in two they are
  written and committed on the module's connection, and the reconciliation is repeatable from the repository so the next
  sync settles what a failure left. Nothing else in the module crosses the two: the API endpoints take both contexts and
  join in memory, and the one raw SQL statement is the module's own.
- The compose stack never created the chain's two data databases, so its first pre flow failed; a `dbinit` service
  creates them.
- The e2e estate runs in one database, `OsduDeliveryE2E`: the metadata, the module's schema and the source data, reused
  by every run (since 2026-09-24; it used to be `SQLFlow_E2E` beside `OsduSample_E2E`). `SQLFLOW_E2E_CATALOG_DB` names it
  on another server; `SQLFLOW_E2E_OSDU_DB` and `SQLFLOW_E2E_SAMPLE_DB` run the suite against separate databases instead. The suite waits for `/health/ready`, not `/health/live`: liveness is up before bootstrap
  has created anything, and starting tests then cost a whole run.
- `OsduDeliveryPre` and `OsduDeliveryIng` keep their names because they are flow source locations. `OSDUSource` and
  `OSDUIngestion` would read better; that is a change to ask for, not one to make.

## The live wave (2026-09-17, Azure Data Manager for Energy 0.29, partition `dev`)

The approved list is `docs/live-wave-one.md`; the report is `osdu/docs/osdu-testing.md` **section 0**; every id is in
`.sqlflow/live-e2e/test-data.md` and every call in `.sqlflow/live-e2e/actions.log`.

- The storage, file, manifest and ddms routes each delivered live and were read back; verify and reconcile were
  exercised; the refusals were provoked; check 14's known-state operation does not exist in this build.
- **Twelve ids created, twelve removed** at the reversible scope (204 each) with a GET answering 404 each, and a search
  for `tags.RunMarker:"ODLIVE20260918"` returning nothing. Nothing was purged. What a soft delete leaves behind (the
  well logs' bulk data, the four files behind the dataset records, the Airflow run) is listed in the inventory.
- Six defects found, all fixed with tests: the cache capture's cursor paging, the partial pandas entry in the sample
  bulk chunks, the session reference-curve rule, the missing CLI release verb, the sample source's undeclared child
  datasets, and the flow that named no DDMS root (a deployment value, fixed in the live estate only).
- Three platform behaviours worth knowing: it enriches master-data records after a write (so a verify reports drift
  until the flow names `TechnicalAssuranceTypeID` under `preserveDataKeys`), a soft delete is visible immediately in
  storage and in search, and `Osdu_ingest` takes about half a minute for a one-record manifest.

The live estate (`.sqlflow/live-e2e/repo`, git-ignored) now carries the deployment's own values: the DDMS root, the
preserved key, `verify.reconcile: true` on the wellbore flow, a manifest flow and mapping of its own (`e2e-manifest`,
`ManifestDocument@1.0.0`), and fixtures whose expected ids are the ones this partition holds. The live catalog is
`OsduDeliveryLive20260918`.

## This session's commits

| Commit | What |
| --- | --- |
| `2beff98` | A cache capture ends a type when its pages stop bringing anything new (this deployment keeps its search cursor) |
| `adf74d3` | `sqlflow records release` from the command line |
| `549580b` | A bulk chunk a dataframe reader cannot read is held before it is sent; the sample chunks and every fixture write the whole pandas entry |
| `2a6b71e` | A session whose chunks do not carry the reference curve as a column is held; the observation added to the integration notes |
| `fe71d0e` | `sqlflow:` the digest clamp test no longer fails in the first five minutes of a UTC day |
| `a28a404` | The sample source's interfaces declare the child datasets their mappings repeat, with a suite over every committed delivery document |
| `d8403d1` | The live wave's report, the go-live map's evidence, the test matrix's live column |
| `7b7e131` | What one control plane replica costs and how an operator recovers; the ledger's retention and backup policy (was the OPS-3/OPS-5 worktree) |
| `8ce07bd` | The retention pass's second count in the GUI's types and the ledger page |
| `cd6e13b` | The scheduled target probe, its options, its metric and the operations section (was the OPS-2 worktree) |
| `d2d480e`, `e32ad50`, `c3640f0` | The map, the coverage plan's stage 10 and the wave one list marked done |
| `0efd194` | Three end-to-end specs the estate outgrew, the records spec staging the ledger it reads through an intake, and the CLI's empty listing naming the runs that stage records |
| `a1e6c2f`, `342afae`, `2774ac4`, `6c9c84a` | The session rule in the protocol reference, what `preserveDataKeys` is for when a platform writes a key, the changelog, and what every suite reports on this build |

Both agent worktrees are landed and removed; `git worktree list` shows only the main checkout.

## What is left, and what it waits for

Everything left in `docs/go-live-map.md` waits on a decision or on services this deployment does not run:

- **LIVE-2** (every other route and shape). The inventory was corrected on 2026-09-18: `dev` **does** run the Reservoir
  DDMS and its ETP 1.2 server (the first listing asked it for `/about`, which it does not have). What `dev` could serve
  once a list is approved: the `dataset` route, the `workflow` route (`csv-parser`), manifest by reference, `rafsV2`,
  and `etp`. Seismic Store is deployed but answers 403 to these credentials. Well Delivery, DSPDM, the historian,
  Reservoir Management and eds-dms are not routed there at any prefix, so they need another deployment. The user's
  decision on 2026-09-18 was to stop at the four routes wave one proved rather than run a second wave.
- **LIVE-3** (the `history` and `everything` scopes) needs `service.storage.admin`, which the app registration lacks.
- **LIVE-4** (deployed hosting, Key Vault, managed identity) and **LIVE-5** (a volume run) need a deployment.
- **SEC-3** needs a development estate of its own (DEC-7); **SEC-4** is an upstream SQLFlow dependency upgrade;
  **SEC-5**, **CI-5b**, **OPS-1b** and DEC-1 to DEC-11 are decisions.

## What the last end-to-end run taught

A `plan` run stages nothing: it renders, reports what it would do, and writes no ledger rows. A submission is staged by
an `intake` or a `deliver`, and an intake reaches no OSDU at all once the flow's legal check is off. The end-to-end
suite's records spec assumed a plan filled the ledger, which is why it could not pass; it now stages its own records
with an intake, and is the only test that reads a real ledger through `sqlflow records`.

## The second half of the session: gaps, bugs and upstream

- **Upstream fixes**, each a `sqlflow:` commit and all recorded in [docs/sqlflow-changes.md](docs/sqlflow-changes.md):
  the CLI took an option it did not know for a flag and read its value as a positional argument (`sqlflow run
  flow.yaml --interface documents` ran every interface and said nothing), SSH.NET sat on a high-severity advisory that
  warned on every build, and the `Microsoft.Extensions` pins were a servicing band behind what a current library needs.
  A guard test reads the CLI's own sources so its option vocabulary cannot fall behind the code that reads it.
- **`docs/sqlflow-changes.md`** is the new inventory of everything this project changed in the vendored SQLFlow: the
  fixes upstream wants regardless of this project, the five test races, and each extension point with what it is for.
- **Metrics now leave the process**: OTLP to any backend that speaks it, Azure Monitor, or the console, off until a
  deployment names one, with the settings that carry secrets kept as references. The control plane binds them from
  configuration; a node reads them from its environment.
- **`verifyTls: false`** needs the deployment's own switch as well as the flow's, **`dev-setup.ps1`** names the estate
  it copies and asks before production, and **CI publishes the images** to whatever registry `IMAGE_REGISTRY` names.

What is left is what only you can decide: a development estate of its own (DEC-7), which registry CI signs in to
(DEC-9), the cleanup policies for the routes wave two covers, and where the metrics are pointed.
