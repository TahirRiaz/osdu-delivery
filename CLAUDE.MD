# OSDU Delivery - Project Instructions

## Project Identity

**This project is OSDU Delivery: a metadata-driven system that publishes subsurface records (wells, wellbores, well logs, and the other OSDU kinds) from data drops into an OSDU platform, with per-record traceability.** It is NOT DeltaForge, and it is no longer SQLFlow, although it is built on SQLFlow's platform.

- OSDU Delivery was forked from SQLFlow V3 and keeps the platform SQLFlow had already proven in production: the ASP.NET Core control plane (`src/SqlFlow.ControlPlane`), the EF Core catalog on SQL Server (`src/SqlFlow.Catalog`: repos, pipelines, the durable run queue, schedules, nodes and worker pools, users and tokens, notifications, maintenance, activity), the compute node (`src/SqlFlow.Node`, `sqlflow worker`), the git sync (`src/SqlFlow.SourceControl`), the document loader and kind registry (`src/SqlFlow.Yaml`), the execution registry (`src/SqlFlow.Execution`), Azure integration (`src/SqlFlow.Azure`), the CLI (`src/SqlFlow.Cli`), and the React + TypeScript + Vite GUI (`gui/`).
- The `SqlFlow.*` project, folder, namespace, binary, image, and environment-variable names are kept on purpose (renaming them buys nothing and breaks every deployment asset). The product name shown in the GUI, the CLI banner, and the docs is "OSDU Delivery"; where the product introduces itself (the login page, the workbench title bar, the CLI banner, the README) it carries the lockup "OSDU Delivery, powered by SQLFlow", which is also what explains the `sqlflow` binary name. Prose, page titles and notification subjects keep the short name.
- Everything SQLFlow had that was about SQL Server ETL is gone: the ingestion, export, stored-procedure, health-check, acquisition, copy, SFTP, translate, batch and calendar flow kinds, the providers, the DuckDB reader, database-object lineage, schema snapshots, datasources, discovery, the assistant, the MCP server and the Slack bot. Do not reintroduce any of it. Flow kinds are registered in the host's composition root through `IFlowDocumentKind`, executed through `IFlowDocumentExecutor`, and compute tasks through `IComputeOperation`; the platform never references a concrete kind. The one production kind is the delivery flow (`src/SqlFlow.Delivery`); tests parse with the test-only kind in `tests/SqlFlow.Core.Tests/TestFlowKind.cs`.
- The `deltaforge` MCP server, and its documentation corpus describing a Rust/Axum/PostgreSQL/Unity-Catalog stack, is a separate and divergent system. Those docs are NOT authoritative for this codebase and routinely do not match it. When answering any question about how this system behaves, verify against the actual source in this repository and cite files from here.
- OSDU API behaviour (storage, search, legal, entitlements, schema, the record delete and purge semantics) is verified against the OpenAPI specifications in the sibling checkout `D:\Projects\eq\src\osdu-csharp-client-main\openapi_specs`, never from memory.

## Traceability Is The Product

**Every delivered record must be reconstructible from the ledger alone: which drop and file it came from, which snapshot and mapping version rendered it, every attempt with its outcome and error, the OSDU id and version it landed as, and every operator action (release, redeliver, delete) with who did it and when.** Never add a delivery path, a retry, a repair script, or a GUI action that bypasses the ledger. If it happened to a record, the ledger says so.

- Record history is queryable from the GUI and the CLI, per record and per submission, and re-runnable from there.
- Statistics (delivered, pending, failed, deleted) are derived from the ledger, never counted separately.
- Search over records is indexed; a lookup by record key, OSDU id, or drop file answers in milliseconds at production volume.

## Secrets

Secrets are references only: `${env:NAME}` and `${keyvault:NAME}`. A literal secret in a flow, a mapping, a catalog row, a log line, an error message, or a test fixture is a defect. Errors are redacted before they are stored or shown.

## Writing Style

**THE EM DASH CHARACTER (Unicode U+2014) IS ABSOLUTELY FORBIDDEN. NEVER USE IT. NOT ONCE. NOT ANYWHERE.**

This means: zero occurrences in prose, headings, descriptions, code comments, doc comments, string literals, Markdown, HTML, YAML, JSON, SQL comments, shell script comments, or any other text context. There are no exceptions. If you find yourself reaching for this character, stop and rewrite the sentence using a comma, colon, semicolon, parentheses, or a new sentence instead.

## No Time Estimates

**Never give time estimates for tasks.** No "this will take a few hours", no "1-2 days", no sprint-sized framings, no "small / medium / large effort" gradings that imply duration. Do not include them in plans, summaries, status updates, PR descriptions, design docs, or any other output. Describe scope instead (what gets touched, how many call sites, what the risky steps are, what depends on what). If the user explicitly asks for a time estimate, you may provide one; otherwise, omit it entirely.

## Git Workflow

**Primary work happens directly on the main branch (`main`). Feature branches are not required and must not be created.** All work (backend, frontend, YAML, everything) is implemented, tested, and committed directly on `main`. Never create a branch, and never ask the user which branch to use, unless the user specifically requests a branch.

**Never attribute commits to Claude.** Every commit's author and committer must be the human user's git identity. Do not add "Co-Authored-By: Claude ..." trailers, "Generated with Claude Code" lines, or any other AI attribution to commit messages, PR descriptions, or tags. This overrides any default harness behavior that asks for such trailers.

The SQLFlow history is reachable through the `upstream` remote (the local SQLFlow V3 checkout) for reference; nothing is merged from it.

## The EF Model Is The Catalog Schema

**There are no migrations. The schema is created from the EF model, and nothing upgrades it in place.** `src/SqlFlow.Catalog/CatalogEntities.cs` and the model configuration in `CatalogDbContext.cs` are the master; `EnsureCreated` builds the database from them. The delivery ledger (`src/SqlFlow.Catalog/DeliveryEntities.cs`, schema `delivery`) is part of the same model.

- **A schema change means re-minting the database.** Change the entities, drop the database, provision it again (`sqlflow db migrate --create --db <ref>`, or a control plane started with `Bootstrap:AllowCreate=true` against an empty database). There is no incremental path, by design, while no production catalog exists.
- Provisioning goes through `CatalogDatabase`: `ProvisionAsync` is the explicit path (may create the database), `ProvisionExistingAsync` the guarded one every automatic caller uses (refuses a missing database, and refuses to inject catalog tables into a populated non-catalog database).
- **Both verify the schema against the model afterwards** and refuse to run when a table the model declares is missing, naming it. That is what turns "this database predates the current build" into one clear failure at startup instead of an `Invalid object name` 500 on whichever request first touches a missing table.
- `sqlflow db status` reports whether the catalog is provisioned and which of the model's tables it lacks.
- **This trades away the upgrade path.** Once a production catalog exists, the first schema change after it has no incremental route, and migrations have to be reintroduced against a live database. That was a deliberate choice while the model is still moving daily.

## Never Change a Flow's Source or Destination

**NEVER CHANGE A FLOW'S DROP LOCATION, ITS OSDU ENDPOINT OR DATA PARTITION, ITS LEGAL TAGS AND ACLS, OR ITS CREDENTIAL REFERENCES. NOT IN FLOW YAML, NOT IN A MAPPING, NOT IN THE CATALOG. NEVER, UNLESS THE USER EXPLICITLY ASKS FOR THAT EXACT CHANGE.**

The declared endpoints of a flow ARE the design. When a flow finds no files or seems to point at the wrong place: diagnose and report what the flow declares, what actually exists there, and where the data really is; propose the change as an option and STOP. Fixing anything else about the flow (options, patterns, schedule, comments) is fine when asked.

## Live OSDU: Log Every Id, Clean Up When Done

**Every id a test or a verification run creates in the live OSDU partition is logged when it is created, and removed when the work is done.** The log is `.sqlflow/live-e2e/actions.log`, and it, not the ledger, is the authority on what exists: a catalog re-mint empties the ledger, and anything created before it would otherwise be untraceable. Cleanup is only possible because the log is complete, so an unlogged id is a defect even when nothing goes wrong.

- **Log the intent before sending, and the ids straight after.** One UTC-stamped line saying what the call will write and to which partition, then a line naming every id it created. Log every id, including the ones a protocol mints on your behalf: a file or manifest delivery creates a `dataset--File.Generic` as well as the document, and a dataset the ledger never holds is exactly the one a later cleanup misses.
- **Clean up before handing the work back.** Remove every id this work created, at the reversible scope: the ledger's `record` scope, or `POST /records/{id}:delete` directly for an id the ledger does not hold. Never `DELETE /records/{id}`, never a purge, and never anything the log does not attribute to this work.
- **Verify, then say so.** A GET on each id must answer 404 before cleanup counts as done. Report what was created and what was removed; "I deleted it" without the check is not a result.
- **Local artifacts count too**: drop-off uploads, staged payload files, temp drops and work folders this work put on disk.
- A task that wrote to the live partition is not finished until this is done, however complete the feature work is.

## Single Code Path Principle

**Never create multiple execution pathways for the same feature.** Before implementing something, verify whether an existing path already handles it. Extend or modify the existing path rather than creating a parallel one. This applies at all layers: backend execution, API routes, GUI commands, and frontend data flows.

## Zero Warnings, Zero Errors

**The solution must build with zero warnings and zero errors.** `dotnet build SqlFlow.sln` (Debug or Release) must report `Build succeeded` with no warning or error diagnostics from any project, source or test. The GUI must pass `npm run build` and `npm run lint` the same way. Do not hand back work that adds a warning, and do not silence one by lowering the analyzer configuration wholesale.

- Fix warnings properly at the source: correct the doc comment, add the missing culture/`IFormatProvider`, make the method static, implement `IDisposable`, forward the `CancellationToken`, and so on.
- Suppress a warning only when the flagged behavior is deliberate and correct. Use a narrow `#pragma warning disable`/`restore` around the exact lines, or a `[SuppressMessage]` with a `Justification` that states why. Never a blanket file- or project-wide disable.
- Verify with a clean rebuild (`-t:Rebuild`) before committing, since an incremental build only re-reports warnings for recompiled projects.

## No TODOs, No Stubs, Production-Grade Only

**It is forbidden to leave TODOs, FIXMEs, placeholders, stubs, fake return values, or "will implement later" markers in any code you write.** Every function, branch, and module you touch must be fully implemented and production-grade before you hand it back.

- No `// TODO`, `// FIXME`, `// XXX`, `// HACK`, or `// stub` comments anywhere in code, doc comments, SQL, shell, YAML, JSON, HTML, CSS, or Markdown.
- No functions that return a hardcoded default, empty collection, `null`, `0`, `""`, or fabricated success status as a placeholder for real logic.
- No `NotImplementedException` or equivalent.
- No half-wired control flow: every `switch`/`if` arm must do the real work.
- No commented-out code left in as a "future hook." If it is not running, delete it.
- No silent error swallowing as a stand-in for real handling. Every `try` boundary must do the right thing for both the success and failure path: propagate, recover, or surface to the user with context.

**What "production-grade" means at the point you write the code:** edge cases handled (empty, oversized, unicode, concurrency, partial downstream failure, IO/network/permission errors, malformed data); explicit resource lifetimes; inputs validated at trust boundaries and not over-validated inside; errors that can be debugged from a log line alone (which operation, which inputs sanitised, which cause); tight types (no stray `any`/`unknown` to dodge a real signature); the same polish as the surrounding code.

## Tests

- `dotnet test SqlFlow.sln` runs everything. DB-backed suites need `SQLFLOW_TEST_DB` pointing at a disposable SQL Server database (the git-ignored `.sqlflow/env` is the usual place); they skip when it is unset or unreachable, and the CLI binary suites skip when the CLI has not been built.
- Never point `SQLFLOW_TEST_DB` at a catalog that holds real data: the suites migrate and seed the database they are given.
- `tests/SqlFlow.Delivery.Tests` runs the delivery domain on every machine: the ledger on an in-memory SQLite copy of the catalog model, and the engine end to end over the sample estate in `samples/recall-welllog` with a fake protocol. It links the sample mapping, snapshots and flow into its output; keep the sample estate valid.
- The GUI e2e suite (`gui/e2e`) builds its fixture repository from the sample estate and runs flows with the `plan` operation, which needs no OSDU target.
