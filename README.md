# SQLFlow

[![CI](https://github.com/sqlflow/sqlflow/actions/workflows/ci.yml/badge.svg)](https://github.com/sqlflow/sqlflow/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg)](https://dotnet.microsoft.com/)

**Metadata-driven, self-evolving incremental ETL for SQL Server - pipelines as YAML, no hand-written T-SQL.**

SQLFlow reads your source, looks at the live target schema, and **generates the ETL on the fly** -
the `CREATE`/`ALTER` to evolve the schema and the bulk load to move the data. You describe *what*
you want in a small YAML file; SQLFlow figures out the SQL.

> This is the v3 rebuild: a clean, modern .NET 9 engine. It runs in **lightweight mode** (no
> control database, no setup) today; a full mode that adds memory, logging, lineage, and a control
> plane is on the roadmap.

## Quick start

```bash
# 1. point at your SQL Server (secrets never live in the YAML)
export SQLFLOW_DW="Server=localhost;Database=DW;Trusted_Connection=True;TrustServerCertificate=True"

# 2. preview exactly what SQL it would run - changes nothing
dotnet run --project src/SqlFlow.Cli -- plan samples/quickstart/orders.flow.yaml

# 3. run it
dotnet run --project src/SqlFlow.Cli -- run samples/quickstart/orders.flow.yaml
```

A pipeline is a YAML file:

```yaml
name: orders
source:
  type: csv
  location: ./orders.csv
target:
  connection: ${env:SQLFLOW_DW}   # a reference, never a secret
  schema: dbo
  table: Orders
schema:
  evolve: widen                   # create | widen | strict
load:
  mode: append                    # append | truncate-load
```

## Why SQLFlow

- **It writes the SQL, you don't.** Schema-sync DDL and bulk load are generated from your metadata
  and the live schema - not maintained by hand.
- **Self-evolving schema.** New columns in the source are added to the target automatically
  (`widen`), or rejected (`strict`), your choice.
- **`plan` before you `run`.** See the exact generated T-SQL before anything touches the database.
- **Config as code.** Pipelines are YAML in git - diffable, reviewable, versioned.
- **SQL Server native.** Built directly on `Microsoft.Data.SqlClient` + `SqlBulkCopy`.

## How it works

```
YAML ──▶ [ model ] ──▶ infer source schema ──▶ introspect target
                                                      │
                                                      ▼
                                  diff ──▶ generate DDL ──▶ execute ──▶ bulk load
```

The engine is stateless in lightweight mode. In **full mode** (roadmap) a metadata database becomes
the source of truth and supplies *memory* - watermarks and run history - so the generator can emit
*optimal incremental* code instead of full loads. See [docs/architecture.md](docs/architecture.md).

## Repository layout

| Project | Responsibility |
|---|---|
| `src/SqlFlow.Core` | Domain model, abstractions, the stateless engine |
| `src/SqlFlow.SqlServer` | SQL Server introspection, DDL generation, bulk load, type mapping |
| `src/SqlFlow.Sources` | Source readers (CSV) + type inference |
| `src/SqlFlow.Yaml` | YAML ↔ model mapping and validation |
| `src/SqlFlow.Cli` | The `sqlflow` command-line tool |
| `tests/SqlFlow.Core.Tests` | Unit tests |

## Build & test

```bash
dotnet build
dotnet test
```

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download).

### Integration tests against a real SQL Server

Most tests are pure unit tests with no dependencies. The tests under `tests/SqlFlow.Core.Tests/Integration`
exercise the real SQL Server path (table create, bulk load, schema evolution, index disable/rebuild,
desired indexes) against a live **sink database**. They read the connection from the
`SQLFlowSinkConStr` environment variable and **skip automatically when it is not set or not reachable**,
so the default `dotnet test` run stays self-contained.

To run them, point `SQLFlowSinkConStr` at a throwaway database you have `db_owner` on:

```bash
# the value is a normal connection string; use a database you don't mind tables being created/dropped in
export SQLFlowSinkConStr="Server=localhost,1433;Database=TestDB;User ID=...;Password=...;TrustServerCertificate=True"
dotnet test --filter Category=Integration
```

Each integration test creates uniquely named tables and drops them before and after itself, so the
sink starts fresh every run. The connection must use a reachable endpoint: SQL Server's TCP/IP protocol
has to be enabled (SQL Server Configuration Manager) and the port in the connection string must match
the port the instance listens on.

## Contributing

Contributions are welcome - see [CONTRIBUTING.md](CONTRIBUTING.md) and our
[Code of Conduct](CODE_OF_CONDUCT.md).

## License

[MIT](LICENSE).
