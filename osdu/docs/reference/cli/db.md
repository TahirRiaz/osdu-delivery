# sqlflow db and the `osdu` module database

```bash
sqlflow db migrate [--db <conn-ref>] [--create] [--module <name>]
sqlflow db status  [--db <conn-ref>] [--module <name>]
sqlflow db sync    [path] [--db <conn-ref>] [--repo <name>] [--repo-url <url>]
```

What `sqlflow db` does for SQLFlow's catalog, how `sync` projects a local estate into it, and the guards on
provisioning are documented in
[../../../../sqlflow/docs/reference/cli/db.md](../../../../sqlflow/docs/reference/cli/db.md). This page covers the
OSDU module's own database.

## The module database

`migrate` and `status` cover SQLFlow's catalog **and** every module database the host registers. OSDU Delivery
registers one:

| Property | Value |
| --- | --- |
| Schema | `osdu` |
| Migration history | `[osdu].[__EFMigrationsHistory]` |
| Schema version | `[osdu].[SchemaVersion]`, recording the module version, the last migration applied, when and by whom, and the minimum SQLFlow catalog migration it requires |
| Connection | The catalog connection, unless the module declares a reference of its own |
| Model | `osdu/src/SqlFlow.Delivery.Data` (`OsduDbContext`) |

`migrate` applies SQLFlow's catalog first and then each module database, in that order, because a module can
require a minimum catalog migration. `--module <name>` limits either verb to one module database, which needs no
catalog connection at all when that module has a connection of its own.

`status` reports each database and names the migrations a database has that this build does not know about. It
exits 0 when every database is current and 2 otherwise, so it works as a deployment or CI drift gate:

```bash
if ! sqlflow db status; then
  echo "a database is behind (or ahead of) this build; run: sqlflow db migrate"
  exit 1
fi
```

## There are migrations here, and they are not optional

The `osdu` schema has a real migration history, unlike the platform's own catalog in some earlier builds. **Every
change to the module's model ships with its migration, its designer file and a refreshed model snapshot, in the
same commit.** A new entity, column, length or index is incomplete without them, and nothing re-mints a database
to make up for a missing one.

The EF tooling is pinned in the repository-root tool manifest (`.config/dotnet-tools.json`, `dotnet-ef` 9.0.8), so
it is the same version everywhere:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project osdu/src/SqlFlow.Delivery.Data
```

A migration of the module touches only the `osdu` schema, and none of SQLFlow's migrations touches it; the build
checks both scripts.

## Startup refuses a mismatch

The hosts apply the same checks `status` reports, before serving anything: pending OSDU migrations, a database
newer than the code, or a SQLFlow catalog older than the module requires each stop startup with a message naming
the migration or version. That turns "this database predates the current build" into one clear failure at startup
instead of an `Invalid object name` on whichever request first touches a missing table.

## `db sync` and the module's documents

`sync` projects a local estate into the catalog, and the module extends it in the same transaction: every
`documentType: mapping` document becomes a mapping row keyed by its `Name@version`, and every `flowType: cache`
document becomes one cache definition row per type it declares, with the partition it fills and the endpoint it
searches. A mapping that does not parse is recorded as invalid with its error rather than left out, so a broken
mapping is visible in the GUI instead of absent.

Neither templates nor cache contents are in a repository, so the sync reads and writes neither: templates are
saved into the catalog directly (`sqlflow template`), and a partition's cache versions are written by the runs of
the cache flows that fill it.

## See also

- [delivery.md](delivery.md): `sqlflow check`, `cache` and `template`, which all read the catalog.
- [../../../deploy/README.md](../../../deploy/README.md): when the databases are created during a deployment.
- [../../environment-variables.md](../../environment-variables.md): `SQLFLOW_CATALOG_DB` and the module
  database's connection reference on a node.
