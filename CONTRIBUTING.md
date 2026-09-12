# Contributing to OSDU Delivery

Thanks for your interest in contributing! This guide gets you productive quickly.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- Node.js, for the GUI under `gui/`
- (Optional, for the DB-backed suites) a SQL Server instance you can create a throwaway database on: LocalDB,
  a container, or Azure SQL

## Getting started

```bash
git clone <this repository>
cd osdu-delivery
dotnet build SqlFlow.sln
dotnet test SqlFlow.sln
cd gui && npm ci && npm run build
```

The pure tests need no database. The DB-backed suites read a connection string from the `SQLFLOW_TEST_DB`
environment variable and skip when it is unset or unreachable. Never point it at a catalog holding real data:
the suites migrate and seed the database they are given.

## Development principles

The architecture is described in [docs/architecture.md](docs/architecture.md). The rules that matter most for
contributors:

1. **The platform never references a concrete flow kind.** Kinds are registered in the host's composition
   root through `IFlowDocumentKind`, `IFlowDocumentExecutor`, and `IComputeOperation`. The catalog, the run
   queue, the scheduler, and the GUI shell work from the document headers every kind provides.
2. **Traceability is the product.** Anything that happens to a record goes through the ledger. No delivery
   path, retry, repair, or GUI action bypasses it.
3. **Secrets are references.** `${env:NAME}` and `${keyvault:NAME}` only; a literal secret in a flow, a mapping,
   a catalog row, a log line, or a test fixture is a defect.
4. **The EF model is the catalog schema.** There are no migrations: the database is created from
   `CatalogEntities.cs` and the model configuration, and both provisioning paths verify the schema against the
   model afterwards. A shape change therefore means minting the catalog again rather than upgrading it in
   place, which is a deliberate trade while no production catalog exists. See `CLAUDE.md` for the procedure.

## Coding standards

- Modern C#: nullable reference types, file-scoped namespaces, `record` for data, primary constructors,
  `async`/`CancellationToken` for I/O.
- Style is enforced by `.editorconfig` and analyzers. `dotnet build SqlFlow.sln` must be warning-clean, and
  `npm run build` in `gui/` must pass the type check.
- No TODOs, stubs, or placeholders: every change you hand in is finished.
- Add or update tests for behavior changes.

## Dependencies

Package versions are managed centrally in `Directory.Packages.props`; project files carry none of their own.
Moving one is a deliberate step someone takes and stands behind:

1. Change the single `PackageVersion` line for the package you mean to move, and nothing else.
2. Run `dotnet restore SqlFlow.sln`, then `dotnet build SqlFlow.sln -c Release` warning-clean, then
   `dotnet test SqlFlow.sln`. A bump across a major version usually asks for source changes; make them in the
   same change, so the history says what the new version needed.
3. Commit the bump on its own, naming what moved and what it cost.

Automated dependency branches are off on purpose: the repository keeps a single branch, and nothing lands that
nobody read. The trade is real, and it is the reason step 3 exists: no branch appears when a dependency
publishes a vulnerability, so watching that surface belongs to whoever looks after the release.

## Submitting changes

1. Fork and create a topic branch.
2. Make your change with tests.
3. Ensure `dotnet build`, `dotnet test`, and the GUI build pass.
4. Open a pull request describing the *why*. Link any related issue.

## Reporting bugs / requesting features

Open an issue. For security issues, see [SECURITY.md](SECURITY.md) and please do **not** open a public issue.
