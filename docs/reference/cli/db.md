# sqlflow db

## Synopsis

```bash
sqlflow db migrate [--db <conn-ref>] [--create]
sqlflow db status  [--db <conn-ref>]
sqlflow db sync    [path] [--db <conn-ref>] [--repo <name>] [--repo-url <url>]
```

## Description

`sqlflow db` operates the catalog: the EF Core-managed SQL Server database that holds the platform's read model of the git/YAML estate (repositories, pipelines, schedules), the durable run queue and the run history with its event trace, the fleet registry, users and tokens, and, in its `delivery` schema, the delivery ledger ([ledger.md](../../delivery/ledger.md)). For the estate, git stays the source of truth and the sync is one-directional (files into database, never the reverse), so the projection can always be rebuilt by re-syncing, and several repositories sync into one catalog.

The command takes a subcommand instead of a document:

- `migrate` provisions the catalog schema from the EF model and verifies an existing one against it. It does NOT auto-create a missing database: without `--create` it calls `CatalogDatabase.ProvisionExistingAsync` and refuses a missing or non-catalog database, so a mistyped `--db` can never silently provision the wrong (possibly production) server. Pass `--create` to provision a new catalog (create the database, or initialise the schema in an empty one).
- `status` reports whether the catalog is provisioned and which of the model's tables it lacks, without changing anything.
- `sync` verifies (and initialises, when empty) the catalog first, never creating the database, then projects the local estate under `[path]` into the catalog: each flow document becomes a pipeline row, YAML-declared schedules are mirrored, each `run.json` in the estate's run history becomes a run row with its events, and the delivery kind's sync extension projects the mapping documents and the snapshot stores.

The platform tables live in the SQL Server schema `catalog` (`CatalogDbContext.SchemaName`) and the ledger in `delivery`. The catalog is the only place in the product that uses Entity Framework Core. There are no migrations: the schema is created from the EF model and nothing upgrades it in place, so a model change means dropping the database and provisioning it again (see the repository's CLAUDE.MD). The control plane provisions the same way at startup (`Bootstrap:ApplyMigrations`, on by default), and its managed git sync runs the same projection over the repositories it pulls ([control plane](../concepts/control-plane.md#managed-git-to-catalog-sync-repo-sources)); this verb is the local, manual counterpart.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<subcommand>` | yes | One of `migrate`, `sync`, `status` (case-insensitive). Anything else, including none, prints `Usage: sqlflow db <migrate\|sync\|status> [path] [--db <conn-ref>] [--create]` to stderr and exits 1. |
| `[path]` | no | `sync` only: the estate folder to project. Defaults to the current directory. |

## Options

All flags take their value as the next argument (`--db "$REF"`, not `--db=REF`).

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--db` | connection reference | `${env:SQLFLOW_CATALOG_DB}` | Reference to the catalog connection string, resolved through the secret resolver (`${env:NAME}` and `${keyvault:vault/secret}`), with local values supplied by the git-ignored `.sqlflow/env` file, searched from the current directory upward. |
| `--create` | switch | off | `migrate` only: provision a new catalog, creating the database itself when it does not exist. Without it, `migrate` initialises an existing but empty database and otherwise only verifies. |
| `--repo` | string | folder name of `[path]`, fallback `default` | `sync` only: the repository name the synced estate is attributed to. The repository id is derived from the name, and pipelines, schedules, runs, mappings and snapshots are scoped to it inside the catalog. |
| `--repo-url` | string | (none) | `sync` only: records the git remote URL on the repo row. A later sync without it preserves the previously recorded URL. |

If the `--db` value looks like a literal connection string carrying a credential keyword (`password=`, `pwd=`, `secret=`, and similar), the command still runs but warns on stderr:

```text
WARN  --db embeds a credential on the command line (it lands in shell history). Prefer the canonical ${env:SQLFLOW_CATALOG_DB}, an explicit ${env:NAME} or ${keyvault:vault/secret} reference, with local values in the git-ignored .sqlflow/env file.
```

## Behavior and output

### migrate

`db migrate` without `--create` calls `CatalogDatabase.ProvisionExistingAsync` (src/SqlFlow.Catalog/CatalogDatabase.cs): it initialises an existing but empty database, verifies it against the model, and refuses, with exit 1, a database that does not exist, one that holds tables but none of the catalog's own, or one missing tables this build declares:

```text
ERROR  catalog 'migrate' failed: The catalog database does not exist (server '<server>', database '<db>'). Refusing to create it automatically. Provision it explicitly with 'sqlflow db migrate --create --db <ref>', or point the connection at your existing catalog.
ERROR  catalog 'migrate' failed: The database (server '<server>', database '<db>') exists and contains <N> table(s) but none of the catalog's own. Refusing to initialise catalog tables into a populated database that may not be a catalog. If this really is a new, dedicated catalog database, provision it with 'sqlflow db migrate --create'.
```

Add `--create` to call `CatalogDatabase.ProvisionAsync` instead, which on an empty server creates the database and then the whole schema from the EF model. Re-running `migrate` is always safe, so the command is idempotent (a no-op when the catalog already matches the model). On success it prints one line:

```text
OK   catalog database provisioned and matches this build's model.
```

Because nothing upgrades a schema in place, `migrate` also refuses a database that is missing tables this build declares, naming them:

```text
ERROR  catalog 'migrate' failed: The catalog database (server '<server>', database '<db>') is missing <N> table(s) this build declares: delivery.UpdateTag. The schema is created from the model and nothing upgrades it in place, so a database provisioned before a model change has to be provisioned again: drop it and run 'sqlflow db migrate --create --db <ref>'.
```

### status

`db status` compares the tables and columns the EF model declares against those the database actually has, and prints one of:

```text
catalog: not provisioned (the database holds none of the catalog's tables).
catalog: provisioned and matches this build's model.
catalog: provisioned but missing N table(s) or column(s) this build declares.
  missing: <schema>.<table>
```

with one `missing:` line (indented two spaces) per absent table or column (`schema.table.column`). It exits 0 when the database matches the model and 2 otherwise, so it works as a CI drift gate.

### sync

`db sync` calls the same guarded `MigrateExistingAsync` first, so a sync against an existing catalog just works and a sync against a missing database is refused rather than provisioned. It then runs `CatalogSync.SyncAsync` (src/SqlFlow.Catalog/CatalogSync.cs) in two phases: phase one collects and parses the estate (file reads and pure computation, no transaction held); phase two applies every write in one serializable transaction through the EF execution strategy (`CatalogTransaction.InSerializableAsync`), with the change tracker cleared before each attempt and the rows re-staged from the phase-one results, so concurrent syncs of the same repository serialize instead of racing and a retried attempt is safe.

The pass projects, in order:

1. **The repository.** The repo row (id derived from `--repo`, name, remote URL, the absolute root path of `[path]`, last-sync time) is inserted or refreshed.
2. **Pipelines.** The estate scanner (src/SqlFlow.Catalog/EstateScan.cs) collects every `*.yaml` under `[path]` (recursively; `schedules.yaml` and `*.schedules.yaml` are schedule libraries, not flows) and parses each through the document loader. A YAML with no `flowType` (a mapping, a config) is simply not a flow; one that declares a `flowType` but fails to parse is a warning, and the sync continues. Each flow is upserted by its stable identity (repository + flow name) with its kind, batch, relative path, execution mode and lifecycle, the drop location and OSDU endpoint as its source and target references, the secret-redacted YAML text, its content hash, and the parsed definition as queryable JSON. Unchanged content (by hash) only re-affirms presence; a flow that has left the folder is removed together with its schedule memberships, keeping its run history; two documents declaring the same flow name are warned and the first wins. A flow document over 16 MiB is skipped with a warning.
3. **Schedules.** Named schedules (a `schedules.yaml` entry, or a flow's inline `schedule:` block, which takes the flow's name when unnamed) are validated and mirrored into the schedule and schedule-member tables with the flows that joined them. An invalid cron or time zone is a warning; a schedule that left git is removed, and schedules created through the API are never touched.
4. **Runs.** Every `run.json` under a `.sqlflow/runs/` directory below `[path]` is read; runs are inserted once by their run id, with the events from the artifact's `events` array, so re-syncing the same folder, or aggregating several machines' folders, is idempotent. A `run.json` over 64 MiB, or one missing `runId`, `flowName` or `flowKind`, is skipped with a warning and counted as unreadable. A run recorded this way is attributed to the `cli` trigger source.
5. **Mappings and snapshots**, through the delivery sync extension (src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs, registered as an `ICatalogSyncExtension`), in the same transaction. Every `*.yaml` or `*.yml` anywhere in the tree (outside `.git`, `.sqlflow`, `bin`, `obj`, `node_modules` and `runs`) that declares `documentType: mapping` becomes a `delivery.Mapping` row keyed by its reference (`Name@version`): name, version, OSDU kind, path, content hash, the YAML text, a summary (natural key, legal tags, ACLs, property count) and a status of `valid` or `invalid` with the parse error, so a broken mapping is visible in the GUI rather than absent. Every `snapshots` directory becomes `delivery.Snapshot` rows: one per schema snapshot (`schemas/*.meta.json`: kind, version, capture time, the schema's data property count and required list) and one per reference snapshot version (`references/<version>/manifest.json`: capture time, content hash, the types and item counts, and whether the `current` pointer selects it). A mapping or snapshot that disappears from the tree loses its row; a duplicate reference is warned and the first wins.

Secrets never rest in the catalog: a flow document that appears to embed a credential produces a warning, and the stored YAML and definition JSON pass through the same redactor used for error text ([environment-variables.md](../../environment-variables.md)).

On success `sync` prints one summary line, in this literal format from src/SqlFlow.Cli/Program.cs:

```text
OK   synced '<path>': pipelines +A added, U updated, N unchanged, D deactivated, X removed; runs +R added (E events), K known, F unreadable; documents +a added, u updated, r removed, i invalid.
```

`deactivated` counts flows a selection-scoped managed sync excluded; the CLI never passes a selection, so it is always 0 here, and a flow that left the folder counts under `removed`. `documents` tallies mappings and snapshots together. At most the first 20 warnings print to stderr as `WARN  <text>`; warnings do not change the exit code.

### Run write-back (keeping the catalog current without manual syncs)

After any `sqlflow run`, when `--db` is passed or `SQLFLOW_CATALOG_DB` is set, the produced run is recorded into the catalog automatically (`RecordRunsInCatalogAsync` in src/SqlFlow.Cli/Program.cs): the repo row and the single flow that ran are upserted with the same redaction, hashing and projection the full sync uses, and the run and its events are inserted once by id. Opt out with `--no-db-sync`. Repository attribution follows `--repo`, then `SQLFLOW_REPO`, then the flow document's folder name; `--repo-url` is honored. The catalog is verified first with the same guarded provisioning `sync` uses (never created). The write-back deliberately does not scan sibling flows, mirror schedules, or reconcile mappings and snapshots; those remain `db sync`'s job. On success it prints, after the run's own output (not with `--json`), plus up to 10 warnings:

```text
  catalog: 1 of 1 run(s) recorded into [<repo>].
```

It is best-effort: a failure prints

```text
WARN  catalog write-back skipped (<redacted message>); the run itself is unaffected. Run 'sqlflow db sync' to backfill.
```

and never changes the run's own exit code.

A run executed by a compute node ([worker.md](worker.md)) needs none of this: it is enqueued in the catalog, streams its events live, and records its outcome under its claim.

## Examples

Provision a catalog and check it, using the canonical environment variable:

```bash
export SQLFLOW_CATALOG_DB="Server=localhost;Database=SqlFlowCatalog;Integrated Security=True;TrustServerCertificate=True"

sqlflow db migrate --create
# OK   catalog database provisioned and matches this build's model.

sqlflow db status
# catalog: provisioned and matches this build's model.
```

Project the sample estate (samples/recall-welllog: one flow, one mapping, one schema snapshot and one reference snapshot version) into the catalog under the repository name `recall-welllog`, from a fresh clone with no local run history:

```bash
sqlflow db sync samples/recall-welllog --repo recall-welllog
# OK   synced 'samples/recall-welllog': pipelines +1 added, 0 updated, 0 unchanged, 0 deactivated, 0 removed; runs +0 added (0 events), 0 known, 0 unreadable; documents +3 added, 0 updated, 0 removed, 0 invalid.
```

A local run of the sample flow (`sqlflow run samples/recall-welllog/flows/recall-welllog.yaml --operation plan --set logSource=demo --no-db-sync`) leaves a `run.json` under `samples/recall-welllog/flows/.sqlflow/runs/`; the next sync of the folder adds it under `runs`, and the sync after that reports it as `known`.

Sync a folder under an explicit repository name and remote, against an explicit connection reference:

```bash
sqlflow db sync ./repo --repo osdu-flows --repo-url https://github.com/acme/osdu-flows.git --db '${env:SQLFLOW_CATALOG_DB}'
```

Use `db status` as a CI drift gate (exit 2 means a deploy must run `db migrate` first):

```bash
if ! sqlflow db status; then
  echo "catalog schema is behind this build; run: sqlflow db migrate"
  exit 1
fi
```

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| 0 | `migrate` or `sync` succeeded (warnings on stderr do not affect it); `status` found the database matching the model. |
| 1 | Unknown or missing subcommand (usage printed); the `--db` reference failed to resolve; the provisioning guard refused; or any EF Core / SqlClient failure, reported as `ERROR  catalog '<sub>' failed: <redacted message>`. |
| 2 | `status` found the database unprovisioned, or missing tables the model declares. |

Error messages pass through the credential redactor, so a connection string quoted by a driver exception never leaks a password into logs.

## See also

- [Control plane](../concepts/control-plane.md): startup provisioning, the managed git sync that runs the same projection, the durable run queue.
- [sqlflow worker](worker.md): the compute node that drains the queue the catalog holds.
- [The ledger](../../delivery/ledger.md): the `delivery` schema, `delivery.Mapping` and `delivery.Snapshot`, retention, provisioning.
- [Architecture](../../architecture.md): where the catalog sits in the platform.
- [Environment variables](../../environment-variables.md): `SQLFLOW_CATALOG_DB`, `SQLFLOW_REPO`, and the `.sqlflow/env` file.
