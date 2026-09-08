# OSDU Delivery - Project Instructions

## Project Identity

**This project is OSDU Delivery: a metadata-driven system that publishes subsurface records (wells, wellbores, well logs, and the other OSDU kinds) from data drops into an OSDU platform, with per-record traceability.** It is NOT DeltaForge, and it is no longer SQLFlow, although it is built on SQLFlow's platform.

- OSDU Delivery was forked from SQLFlow V3 and keeps the platform SQLFlow had already proven in production: the ASP.NET Core control plane (`src/SqlFlow.ControlPlane`), the EF Core catalog on SQL Server (`src/SqlFlow.Catalog`: repos, pipelines, the durable run queue, schedules, nodes and worker pools, users and tokens, notifications, maintenance, activity), the compute node (`src/SqlFlow.Node`, `sqlflow worker`), the git sync (`src/SqlFlow.SourceControl`), the document loader and kind registry (`src/SqlFlow.Yaml`), the execution registry (`src/SqlFlow.Execution`), Azure integration (`src/SqlFlow.Azure`), the CLI (`src/SqlFlow.Cli`), and the React + TypeScript + Vite GUI (`gui/`).
- The `SqlFlow.*` project, folder, namespace, binary, image, and environment-variable names are kept on purpose (renaming them buys nothing and breaks every deployment asset). The product name shown in the GUI, the CLI banner, and the docs is "OSDU Delivery".
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

## Catalog Schema Changes Require a Migration

**Never change the catalog's shape without its EF Core migration.** Any edit to `src/SqlFlow.Catalog/CatalogEntities.cs` or the model configuration in `CatalogDbContext.cs` (a new entity, a new column, a changed length, a new index) is incomplete until a migration exists. The catalog schema is upgraded ONLY by migrations, never by hand.

The delivery ledger (`src/SqlFlow.Catalog/DeliveryEntities.cs`, schema `delivery`) is part of the catalog model and follows the same rule.

- Generate it with `dotnet ef migrations add <PascalCaseName>` from `src/SqlFlow.Catalog` (the design-time factory there makes this work without a startup project, and `migrations add` never touches a database). Commit all three artifacts together: the `{timestamp}_{Name}.cs` migration, its `.Designer.cs`, and the refreshed `CatalogDbContextModelSnapshot.cs`. The generated files are marked as generated code in `.editorconfig`, so style rules do not apply to them.
- The SQLFlow migration history was squashed into one `Initial` migration at the fork; no OSDU Delivery catalog exists in production yet. Until the first production catalog is provisioned, re-squash into `Initial` rather than stacking migrations, so the shipped history starts clean.
- Migrations are discovered from the `SqlFlow.Catalog` assembly. They apply automatically at control-plane startup (`BootstrapProvisioningService` when `ApplyMigrations=true`, the deployed default), via the CLI's `db` verbs, and in every integration test through `CatalogDatabase.MigrateAsync`.
- The tell-tale of a forgotten migration: the model snapshot differs from the last migration, tests fail with "Invalid column name", or the control plane logs pending-migration warnings.

## Never Change a Flow's Source or Destination

**NEVER CHANGE A FLOW'S DROP LOCATION, ITS OSDU ENDPOINT OR DATA PARTITION, ITS LEGAL TAGS AND ACLS, OR ITS CREDENTIAL REFERENCES. NOT IN FLOW YAML, NOT IN A MAPPING, NOT IN THE CATALOG. NEVER, UNLESS THE USER EXPLICITLY ASKS FOR THAT EXACT CHANGE.**

The declared endpoints of a flow ARE the design. When a flow finds no files or seems to point at the wrong place: diagnose and report what the flow declares, what actually exists there, and where the data really is; propose the change as an option and STOP. Fixing anything else about the flow (options, patterns, schedule, comments) is fine when asked.

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
