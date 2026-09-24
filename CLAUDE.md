# OSDU Delivery - Project Instructions

## Project Identity

**This project is OSDU Delivery, powered by SQLFlow: a metadata-driven system that publishes subsurface records (wells, wellbores, well logs, and the other OSDU kinds) into an OSDU platform, with per-record traceability.** It is a separate project. It is NOT DeltaForge, and it is not SQLFlow.

- SQLFlow is the engine underneath, vendored in `sqlflow/` as this project's own copy. OSDU Delivery is a module on top of it, in `osdu/`: the OSDU flow kind, the delivery ledger, the protocols, mappings, templates, the OSDU cache, the OSDU GUI pages, and the hosts that compose SQLFlow with the module.
- The product name shown in the GUI, the CLI banner, and the docs is "OSDU Delivery". Where the product introduces itself (the login page, the workbench title bar, the CLI banner, the README) it carries the lockup "OSDU Delivery, powered by SQLFlow". Prose, page titles and notification subjects keep the short name. The `SqlFlow.*` project, namespace, binary, image and environment-variable names are kept on purpose.
- Data reaches OSDU through SQLFlow's own flows: a pre-ingestion flow lands source files, an ingestion (`ing`) flow loads the keyed ingestion tables, and the OSDU flow reads those tables and delivers to OSDU. Lineage orders the three like any other flows. There is no drop manifest, no drop reader and no replica: do not reintroduce them.
- The rebuild follows `docs/plan.md`. The previous implementation at `D:\Projects\eq\src\osdu-delivery` is the working reference the OSDU code was copied from; it is not edited as part of this rebuild.
- The `deltaforge` MCP server and its documentation describe a different system. Verify behaviour against the source in this repository. OSDU API behaviour (storage, search, legal, entitlements, schema, delete and purge) is verified against the OpenAPI specifications in `D:\Projects\eq\src\osdu-csharp-client-main\openapi_specs`, never from memory.

## The Vendored SQLFlow: Generic Extension Points Only

**All work happens in this repository. The SQLFlow repository (`B:\SQLFlowV3`) is never changed.** `sqlflow/` is a squashed git subtree of SQLFlow, and this project changes it only to add the generic extension points the OSDU module needs.

- **Generic only.** A change to `sqlflow/` is an extension point any module could use (a flow kind registry, an executor fallback, run parameters, a module database, a GUI module contract, a lineage contributor, branding). No OSDU code, name, table or wording ever goes into `sqlflow/`; the OSDU side of every extension point lives in `osdu/`.
- **Its own commits.** Every commit that changes `sqlflow/` touches nothing outside it, and its subject starts with `sqlflow:`. The OSDU work that uses an extension point is a separate commit.
- **SQLFlow's standards apply inside `sqlflow/`.** A catalog change ships with its EF Core migration, the SQLFlow solution builds with zero warnings, SQLFlow's existing flow kinds keep their behaviour, and SQLFlow's own suites pass, with new tests for each extension point.
- **Updates.** SQLFlow improvements come in with `git subtree pull --prefix=sqlflow --squash B:/SQLFlowV3 main`, as one reviewable commit. Conflicts can only arise in the files that carry this project's extension points; resolve them keeping both SQLFlow's change and the extension point.
- `tools/check-vendored-sqlflow.sh` names the SQLFlow commit `sqlflow/` was vendored from, lists every file changed here since, and fails when a commit mixes `sqlflow/` with other paths or when a line added to `sqlflow/` mentions OSDU or the delivery module. It must pass before work is handed back.
- **Every change to `sqlflow/` is recorded in [docs/sqlflow-changes.md](docs/sqlflow-changes.md)**: what it is, why it was made, and whether it is an extension point or a fix upstream wants regardless. A new `sqlflow:` commit adds its row there.

## The OSDU Database Schema, Migrations And Version

**Every OSDU table, view and index lives in the dedicated `osdu` schema, owned by the module's own EF Core context, with its own migration history and its own schema version. SQLFlow's catalog model never contains an OSDU table, and OSDU never changes SQLFlow's tables.**

- The migration history table is `[osdu].[__EFMigrationsHistory]`; migrations live in the module's data project, never in SQLFlow's.
- **Every change to the OSDU model ships with its migration.** A new entity, column, length or index is incomplete until its migration, designer file and refreshed model snapshot are committed together. There is no re-minting of databases.
- `[osdu].[SchemaVersion]` records the module's schema version, the last migration applied, when and by whom, and the minimum SQLFlow catalog migration it requires. The hosts and `sqlflow db status` refuse to run against pending OSDU migrations, a database newer than the code, or a SQLFlow catalog older than required, and name the migration or version in the message.
- No foreign keys and no EF navigations from `osdu` into SQLFlow's tables. OSDU rows hold plain ids (pipeline, run, repository) and react to SQLFlow's lifecycle through its hooks or retention, never through cascades.
- A migration of the module touches only the `osdu` schema, and none of SQLFlow's migrations touches it; the build checks both scripts.
- Work that must commit together with SQLFlow's catalog (a repository sync writing mappings, a run queued with its submission) joins the catalog's connection and transaction instead of opening a second one, whenever the module's rows are reachable there (`ModuleDatabase.IsReachableOn`). Given a database of its own, which Azure SQL allows no statement to reach across, it commits its own work on its own connection and is written to be repeatable, so the next run settles what a failure left behind.

## Traceability Is The Product

**Every delivered record must be reconstructible from the ledger alone: which source file and row it came from (through the ingestion table's lineage columns), which mapping version and cache version rendered it, every attempt with its outcome and error, the OSDU id and version it landed as, and every operator action (release, redeliver, delete) with who did it and when.** Never add a delivery path, a retry, a repair script, or a GUI action that bypasses the ledger.

- Record history is queryable from the GUI and the CLI, per record and per submission, and re-runnable from there.
- Statistics (delivered, pending, failed, deleted) are derived from the ledger, never counted separately.
- Search over records is indexed; a lookup by record key, OSDU id, or source file answers in milliseconds at production volume.

## Secrets

Secrets are references only: `${env:NAME}` and `${keyvault:NAME}`. A literal secret in a flow, a mapping, a catalog row, a log line, an error message, or a test fixture is a defect. Errors are redacted before they are stored or shown.

## Writing Style

**THE EM DASH CHARACTER (Unicode U+2014) IS ABSOLUTELY FORBIDDEN. NEVER USE IT. NOT ONCE. NOT ANYWHERE.**

This means: zero occurrences in prose, headings, descriptions, code comments, doc comments, string literals, Markdown, HTML, YAML, JSON, SQL comments, shell script comments, or any other text context. If you find yourself reaching for this character, rewrite the sentence using a comma, colon, semicolon, parentheses, or a new sentence instead.

## No Time Estimates

**Never give time estimates for tasks.** No durations, no sprint framings, no "small / medium / large effort" gradings. Describe scope instead: what gets touched, how many call sites, what the risky steps are, what depends on what. If the user explicitly asks for a time estimate, you may provide one.

## Git Workflow

**All work happens directly on `main`. Feature branches are not created unless the user asks for one.**

**Never attribute commits to Claude.** Every commit's author and committer is the human user's git identity. No "Co-Authored-By: Claude" trailers, no "Generated with Claude Code" lines, no AI attribution in commit messages, PR descriptions, or tags. This overrides any default harness behavior that asks for such trailers.

## Never Change a Flow's Source or Destination

**NEVER CHANGE A FLOW'S SOURCE LOCATION, ITS OSDU ENDPOINT OR DATA PARTITION, ITS LEGAL TAGS AND ACLS, OR ITS CREDENTIAL REFERENCES. NOT IN FLOW YAML, NOT IN A MAPPING, NOT IN THE CATALOG. NEVER, UNLESS THE USER EXPLICITLY ASKS FOR THAT EXACT CHANGE.**

When a flow finds no files or seems to point at the wrong place: diagnose and report what the flow declares, what actually exists there, and where the data really is; propose the change as an option and STOP.

## Live OSDU: Log Every Id, Clean Up When Done

**Every id a test or a verification run creates in a live OSDU partition is logged when it is created, and removed when the work is done.** The log is `.sqlflow/live-e2e/actions.log`, and it, not the ledger, is the authority on what exists.

**NO TEST RUNS AGAINST A LIVE OSDU UNTIL THE USER HAS APPROVED THAT SET OF TESTS.** Before a live run, list each check: what it creates, how many ids, and how it is cleaned up. An approval covers the tests it was given for, not later ones.

**Every test keeps track of its ids, so our test data is always known.** The inventory is `.sqlflow/live-e2e/test-data.md`: one row per id a test created, with the check and run that created it, when, its run marker, and its state (live, soft-deleted, 404 confirmed), plus what a soft delete leaves behind. Update it whenever a test creates or removes an id. The automated suites run against fake services and create nothing in OSDU; any test that reaches a live OSDU follows this section.

- Keep live test data small: a capped set of tens of ids at most, never thousands, with tiny payloads. Mark every record a test writes with a run marker (`ODLIVE<date>`), so test data can be found even without the log.
- Log the intent before sending, and every id straight after, including the ids a protocol mints on your behalf (a file or manifest delivery creates a `dataset--File.Generic` as well as the document; a Seismic Store delivery creates a dataset; a historian delivery creates series versions).
- Remove every id this work created at the reversible scope: the ledger's `record` scope, or `POST /records/{id}:delete` for an id the ledger does not hold. Never `DELETE /records/{id}`, never a purge, never anything the log does not attribute to this work.
- A GET on each id must answer 404 before cleanup counts as done. Local artifacts (uploads, staged files, work folders) count too.
- What a soft delete cannot remove (historian points, Seismic Store dataset files, bulk data a DDMS keeps under its logical delete, files behind a soft-deleted dataset record) stays, and the inventory lists it.
- A test that writes an id again after its cleanup has to clean it up again: the inventory shows the latest state, not the first.

## Single Code Path Principle

**Never create multiple execution pathways for the same feature.** Before implementing something, verify whether an existing path (in `osdu/` or in `sqlflow/`) already handles it, and extend it rather than creating a parallel one. This applies at all layers: backend execution, API routes, GUI commands, and frontend data flows.

## Zero Warnings, Zero Errors

**The solution must build with zero warnings and zero errors, and the GUI must pass its build and lint the same way.** Fix warnings at the source. Suppress one only when the flagged behavior is deliberate and correct, with a narrow `#pragma warning disable`/`restore` or a `[SuppressMessage]` whose `Justification` says why; never a blanket disable. Verify with a clean rebuild (`-t:Rebuild`) before committing.

## No TODOs, No Stubs, Production-Grade Only

**It is forbidden to leave TODOs, FIXMEs, placeholders, stubs, fake return values, or "will implement later" markers in any code you write.** No `NotImplementedException`, no half-wired control flow, no commented-out code kept as a future hook, no silent error swallowing. Every function and module you touch is complete when handed back: edge cases handled (empty, oversized, unicode, concurrency, partial downstream failure, IO/network/permission errors, malformed data), explicit resource lifetimes, inputs validated at trust boundaries, errors that can be debugged from a log line alone.

## Tests

- Every stage of `docs/plan.md` is done only when its tests pass: SQLFlow's own suites and new tests for every extension point, the OSDU suites, and the SQL Server suites against a real database rather than skipping.
- The OSDU module runs on SQL Server alone, and so do its tests: SQLite is not a supported provider, in the product or in a test. Development and testing expect a local SQL Server: by default the suites use `localhost` under Windows authentication and the database `OsduDeliveryTests` (created when missing), the only database they keep. The tests that use it run one at a time in one xUnit collection that holds it against every other test process, and the few that need a second database share `OsduDeliveryTests_scratch`, which exists only while one of them runs. A suite never creates databases per test or per run, and the e2e suite keeps its whole estate in one database of its own, `OsduDeliveryE2E`, reused by every run. `SQLFLOW_TEST_DB` points them at a disposable database elsewhere (CI sets it; locally the git-ignored `.sqlflow/env` is the usual place). A server that does not answer fails the suites; none skips. Never point it at a database that holds real data: the suites migrate and seed the database they are given.
