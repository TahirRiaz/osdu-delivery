# OSDU Delivery - Project Instructions

## Project Identity

**This project is OSDU Delivery, powered by SQLFlow: a metadata-driven system that publishes subsurface records (wells, wellbores, well logs, and the other OSDU kinds) into an OSDU platform, with per-record traceability.** It is NOT DeltaForge.

- SQLFlow is the engine underneath and is vendored, unmodified, in `sqlflow/`. OSDU Delivery is a module on top of it: the OSDU flow kind, the delivery ledger, the protocols, mappings, templates, the OSDU cache, the OSDU GUI pages, and the hosts that compose SQLFlow with the module.
- The product name shown in the GUI, the CLI banner, and the docs is "OSDU Delivery". Where the product introduces itself (the login page, the workbench title bar, the CLI banner, the README) it carries the lockup "OSDU Delivery, powered by SQLFlow". Prose, page titles and notification subjects keep the short name. The `SqlFlow.*` project, namespace, binary, image and environment-variable names are kept on purpose.
- Data reaches OSDU through SQLFlow's own flows: a pre-ingestion flow lands source files, an ingestion (`ing`) flow loads the keyed ingestion tables, and the OSDU flow reads those tables and delivers to OSDU. Lineage orders the three like any other flows. There is no drop manifest, no drop reader and no replica: do not reintroduce them.
- The rebuild follows `docs/plan.md`. The previous implementation at `D:\Projects\eq\src\osdu-delivery` is the working reference the OSDU code is moved from; it is not edited as part of this rebuild.
- The `deltaforge` MCP server and its documentation describe a different system. Verify behaviour against the source in this repository. OSDU API behaviour (storage, search, legal, entitlements, schema, delete and purge) is verified against the OpenAPI specifications in `D:\Projects\eq\src\osdu-csharp-client-main\openapi_specs`, never from memory.

## Vendored SQLFlow Is Never Edited Here

**Nothing under `sqlflow/` is changed in this repository. Not a fix, not a hook, not a comment.** `sqlflow/` is a squashed git subtree of the SQLFlow repository (`B:\SQLFlowV3`, branch `main`), and it must stay byte-for-byte the SQLFlow commit it was vendored from.

- A change SQLFlow needs (an extension point, a fix) is made in the SQLFlow repository under that repository's own rules, committed there, and then brought in: `git subtree pull --prefix=sqlflow --squash B:/SQLFlowV3 main`.
- `tools/check-vendored-sqlflow.sh` fails when `sqlflow/` differs from its vendored commit, in a commit or in the working tree. Run it before every commit that touches the repository layout, and it must pass before work is handed back.
- OSDU code uses SQLFlow only through SQLFlow's extension points (flow kinds, executors, compute operations, run parameters, fan-out, catalog sync extension, module database, GUI routes and panels, lineage, branding). When an extension point is missing, it is added to SQLFlow, never worked around by copying SQLFlow code into the module.

## The OSDU Database Schema, Migrations And Version

**Every OSDU table, view and index lives in the dedicated `osdu` schema, owned by the module's own EF Core context, with its own migration history and its own schema version. SQLFlow's catalog model never contains an OSDU table, and OSDU never changes SQLFlow's tables.**

- The migration history table is `[osdu].[__EFMigrationsHistory]`; migrations live in the module's migrations project, never in SQLFlow's.
- **Every change to the OSDU model ships with its migration.** A new entity, column, length or index is incomplete until its migration, designer file and refreshed model snapshot are committed together. There is no re-minting of databases.
- `[osdu].[SchemaVersion]` records the module's schema version, the last migration applied, when and by whom, and the minimum SQLFlow catalog migration it requires. The hosts and `sqlflow db status` refuse to run against pending OSDU migrations, a database newer than the code, or a SQLFlow catalog older than required, and name the migration or version in the message.
- No foreign keys and no EF navigations from `osdu` into SQLFlow's tables. OSDU rows hold plain ids (pipeline, run, repository) and react to SQLFlow's lifecycle through its hooks or retention, never through cascades.
- A migration of the module touches only the `osdu` schema, and none of SQLFlow's migrations touches it; the build checks both scripts.
- Work that must commit together with SQLFlow's catalog (a repository sync writing mappings, a run queued with its submission) joins the catalog's connection and transaction instead of opening a second one.

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

- Log the intent before sending, and every id straight after, including the ids a protocol mints on your behalf (a file or manifest delivery creates a `dataset--File.Generic` as well as the document).
- Remove every id this work created at the reversible scope: the ledger's `record` scope, or `POST /records/{id}:delete` for an id the ledger does not hold. Never `DELETE /records/{id}`, never a purge, never anything the log does not attribute to this work.
- A GET on each id must answer 404 before cleanup counts as done. Local artifacts (uploads, staged files, work folders) count too.

## Single Code Path Principle

**Never create multiple execution pathways for the same feature.** Before implementing something, verify whether an existing path (in the module or in SQLFlow) already handles it, and extend it rather than creating a parallel one. This applies at all layers: backend execution, API routes, GUI commands, and frontend data flows.

## Zero Warnings, Zero Errors

**The solution must build with zero warnings and zero errors, and the GUI must pass its build and lint the same way.** Fix warnings at the source. Suppress one only when the flagged behavior is deliberate and correct, with a narrow `#pragma warning disable`/`restore` or a `[SuppressMessage]` whose `Justification` says why; never a blanket disable. Verify with a clean rebuild (`-t:Rebuild`) before committing.

## No TODOs, No Stubs, Production-Grade Only

**It is forbidden to leave TODOs, FIXMEs, placeholders, stubs, fake return values, or "will implement later" markers in any code you write.** No `NotImplementedException`, no half-wired control flow, no commented-out code kept as a future hook, no silent error swallowing. Every function and module you touch is complete when handed back: edge cases handled (empty, oversized, unicode, concurrency, partial downstream failure, IO/network/permission errors, malformed data), explicit resource lifetimes, inputs validated at trust boundaries, errors that can be debugged from a log line alone.

## Tests

- Every stage of `docs/plan.md` is done only when its tests pass: the moved OSDU suites, SQLFlow's own suites for any extension point, and the SQL Server suites against a real database rather than skipping.
- DB-backed suites need `SQLFLOW_TEST_DB` pointing at a disposable SQL Server database (the git-ignored `.sqlflow/env` is the usual place). Never point it at a catalog that holds real data: the suites migrate and seed the database they are given.
