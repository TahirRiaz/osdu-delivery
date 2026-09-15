# Contributing to OSDU Delivery

Thanks for your interest in contributing! This guide gets you productive quickly.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- Node.js 22 or later, for the GUI under `osdu/gui`
- (For the DB-backed suites) a SQL Server instance you can create a throwaway database on: LocalDB, a
  container, or Azure SQL

## Getting started

```bash
git clone <this repository>
cd osdu-delivery
dotnet build OsduDelivery.sln
dotnet test OsduDelivery.sln
cd osdu/gui && npm ci && npm run build && npm run lint
```

The pure tests need no database. The DB-backed suites read a connection string from the `SQLFLOW_TEST_DB`
environment variable and skip when it is unset or unreachable. Never point it at a database holding real data:
the suites migrate and seed the database they are given.

`dev.bat` runs the whole thing locally against a real estate; `osdu/tools/dev-setup.ps1` generates the
git-ignored `.sqlflow/env` it reads.

## Repository layout

| Path | What it is |
| --- | --- |
| `sqlflow/` | SQLFlow, vendored as a squashed git subtree. This project adds only generic extension points to it. |
| `osdu/` | Everything OSDU Delivery adds: the module, its data project, the control plane endpoints, the CLI verbs, the hosts, the GUI, the samples, the tests, the docs and the deployment assets. |
| `tools/check-vendored-sqlflow.sh` | Lists this project's changes to `sqlflow/` and fails when one mixes in other paths or OSDU code. |
| `docs/plan.md` | The rebuild plan: the stages, what each one changes, and the tests that close it. |
| `CLAUDE.md` | The project's working rules. Read it before your first change. |

## Development principles

1. **The vendored SQLFlow takes generic extension points only.** A change to `sqlflow/` must be something any
   module could use (a flow kind registry, an executor fallback, run parameters, a module database, a GUI module
   contract, a lineage contributor, branding). No OSDU code, name, table or wording goes into `sqlflow/`. Every
   commit that touches `sqlflow/` touches nothing else and starts its subject with `sqlflow:`. The OSDU work that
   uses an extension point is a separate commit. `bash tools/check-vendored-sqlflow.sh` enforces both and must
   pass before you hand work back.
2. **The OSDU schema owns its own lifecycle.** Every OSDU table, view and index lives in the `osdu` schema, owned
   by the module's EF Core context in `osdu/src/SqlFlow.Delivery.Data`, with its own migration history
   (`[osdu].[__EFMigrationsHistory]`) and its own schema version. **Every model change ships with its migration,
   designer file and refreshed model snapshot in the same commit.** No foreign keys or EF navigations from `osdu`
   into SQLFlow's tables.
3. **Traceability is the product.** Anything that happens to a record goes through the ledger. No delivery path,
   retry, repair script or GUI action bypasses it, and statistics are derived from the ledger rather than counted
   separately.
4. **Secrets are references.** `${env:NAME}` and `${keyvault:vault/secret}` only; a literal secret in a flow, a
   mapping, a catalog row, a log line or a test fixture is a defect.
5. **One code path per feature.** Before adding something, check whether a path in `osdu/` or `sqlflow/` already
   handles it and extend that instead. This applies at every layer: execution, API routes, GUI commands, data flow.

## Coding standards

- Modern C#: nullable reference types, file-scoped namespaces, `record` for data, primary constructors,
  `async`/`CancellationToken` for I/O.
- Style is enforced by `.editorconfig` and analyzers. `dotnet build OsduDelivery.sln -c Release` must be
  warning-clean, and in `osdu/gui` both `npm run lint` (ESLint, no warnings allowed) and `npm run build` (the type
  check and the bundle) must pass. Fix a lint finding at its source; a component file exports only components, so
  a hook, a context, a constant or a variants helper lives in a `.ts` module beside it.
- No TODOs, stubs or placeholders: every change you hand in is finished.
- Add or update tests for behavior changes. A change to an extension point in `sqlflow/` needs tests there too,
  written against a test-only module rather than the OSDU one.

## Dependencies

Package versions are managed centrally, in `osdu/Directory.Packages.props` for the OSDU projects and
`sqlflow/Directory.Packages.props` for the vendored ones; project files carry none of their own. Moving one is a
deliberate step someone takes and stands behind:

1. Change the single `PackageVersion` line for the package you mean to move, and nothing else. A bump inside
   `sqlflow/` follows the vendoring rule: its own commit, prefixed `sqlflow:`.
2. Run `dotnet restore OsduDelivery.sln`, then `dotnet build OsduDelivery.sln -c Release` warning-clean, then
   `dotnet test OsduDelivery.sln`. A bump across a major version usually asks for source changes; make them in the
   same change, so the history says what the new version needed.
3. Commit the bump on its own, naming what moved and what it cost.

Automated dependency branches are off on purpose: the repository keeps a single branch, and nothing lands that
nobody read. The trade is real, and it is the reason step 3 exists: no branch appears when a dependency publishes
a vulnerability, so watching that surface belongs to whoever looks after the release.

## Updating the vendored SQLFlow

SQLFlow improvements come in as one reviewable commit:

```bash
git subtree pull --prefix=sqlflow --squash <sqlflow-remote> main
bash tools/check-vendored-sqlflow.sh
dotnet build OsduDelivery.sln -c Release && dotnet test OsduDelivery.sln
```

Conflicts can only arise in the files that carry this project's extension points; resolve them keeping both
SQLFlow's change and the extension point.

## Submitting changes

1. Fork and create a topic branch.
2. Make your change with tests.
3. Ensure the build, the tests, the GUI build and lint, and the vendored-SQLFlow guard all pass.
4. Open a pull request describing the *why*. Link any related issue.

## Reporting bugs / requesting features

Open an issue. For security issues, see [SECURITY.md](SECURITY.md) and please do **not** open a public issue.
