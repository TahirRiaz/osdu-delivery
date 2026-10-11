---
id: delivery-cli-cache
title: "sqlflow cache: list a partition's cache versions, or import cached types for work without OSDU"
type: cli-command
summary: "sqlflow cache list shows the versions of a partition's cache and its system properties; cache import loads OSDU type files where no platform is reachable."
keywords:
  - sqlflow cache
  - cache list
  - cache import
  - cache versions
  - partition cache
  - from-dir
  - offline reference data
  - system properties
  - refresh the cache
  - cache retention
  - pruned version
cliCommand: cache
related:
  - delivery-concept-partition-cache
  - delivery-flow-cache
  - delivery-cli-run
  - delivery-cli-db
  - delivery-guide-reference-data-cache
  - delivery-guide-lookup-table-cache
  - cli-run
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.Cli/CliPartitions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SnapshotBuilder.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheRefresh.cs
  - osdu/src/SqlFlow.Delivery/Model/CacheDefinition.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheScope.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/ReferenceSnapshot.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheRetention.cs
  - osdu/src/SqlFlow.Delivery/Hosting/OsduModuleDatabase.cs
  - sqlflow/src/SqlFlow.Core/Runs/RunActors.cs
---

# sqlflow cache: list a partition's cache versions, or import cached types for work without OSDU

## Synopsis

```bash
sqlflow cache list <partition | cache.yaml> [--partition <id>] [--db <conn-ref>] [--json]
sqlflow cache import <cache.yaml> --from-dir <dir> [--partition <id>] [--db <conn-ref>] [--json]
```

## Description

`cache` is one of the verbs OSDU Delivery adds to SQLFlow's command line (`sqlflow`, built as
`SqlFlow.Delivery.Cli.Host`). It reads and writes the [partition cache](../concepts/partition-cache.md) in the module's
database.

It does not fill a cache from its sources. A cache is filled by running its [cache flow](../flow/cache.md) with the
`refresh` operation, the same run its schedule fires:

```bash
sqlflow run osdu-reference-00-cache.yaml --set partition=dev
sqlflow run osdu-reference-00-cache.yaml --operation plan --set partition=dev   # count what each type would hold, write nothing
```

That is also how a lookup table gets into the cache: a cache flow's `table`, `dictionary` or `dimension` type is
captured from its origin by a refresh, and `cache import` refuses it ([lookup table guide](../guides/lookup-table-cache.md)).

Both subcommands need the module's database: `SQLFLOW_OSDU_DB` when the `osdu` schema has a database of its own, or else
the catalog's database, named by `--db` (default `${env:SQLFLOW_CATALOG_DB}`). Without either, the command fails before
it reads anything (`Environment variable 'SQLFLOW_CATALOG_DB' is not set.`).

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `list <partition>` | one of the two | A partition id (`dev`) or a `${env:...}` / `${keyvault:...}` reference resolving to one. A value that is neither is refused. |
| `list <cache.yaml>` | one of the two | A cache flow file: lists the cache of each partition the flow builds (every one for a flow that names its partitions or serves the registry, unless `--partition` names one), or of the partition its header names. |
| `import <cache.yaml>` | yes | The cache flow whose capture the files stand in for. |

## Options

| Option | Applies to | Description |
| --- | --- | --- |
| `--partition <id>` | both, with a cache flow file | The partition to list or import into, settled as a refresh settles it. With `import`, a flow that builds several partitions needs it when the registry's default is not one of them. A flow whose partition is its header's refuses it. |
| `--from-dir <dir>` | `import` | The directory of type files. Required: `name the directory the type files are in with --from-dir.` |
| `--db <conn-ref>` | both | The catalog connection reference, used for the `osdu` schema when `SQLFLOW_OSDU_DB` is not set. |
| `--json` | both | Print JSON instead of text. |

## cache list

Prints the partition's [retention](../concepts/partition-cache.md#retention), then every version of its cache, newest
first: its label, `current` against the current one and `pruned` against one whose records the retention pruned, how
many records in how many types, the cache flow that wrote it, when, for whom, in which run, and when its records were
pruned. Under each version, the types it moved by their own content hash (`types moved: UnitAlias (changed); 3
unchanged`), or `no type changed`. Then the current version's system properties: each one's service, name, state, where
the service took it from, and why it is unknown.

```text
partition dev: the records of a replaced version are kept 7 day(s) after it was replaced, then pruned; a pruned version is listed with what it was
20261009T012000Z  current  4210 record(s) in 4 type(s), written by cache flow welldb-lookups-00-cache at 2026-10-09 01:20:00Z for <who asked> in run <run id>
  types moved: UnitAlias (changed); 3 unchanged
20261008T012000Z           4209 record(s) in 4 type(s), written by cache flow osdu-reference-00-cache at 2026-10-08 01:20:00Z for <who asked> in run <run id>
  no type changed
20260929T012000Z  pruned   4209 record(s) in 4 type(s), written by cache flow osdu-reference-00-cache at 2026-09-29 01:20:00Z for <who asked> in run <run id>; records pruned at 2026-10-08 01:20:31Z
  types moved: UnitOfMeasure (changed); 3 unchanged

system properties of partition dev, as version 20261009T012000Z holds them (settings of the platform, not cached records):
  indexer   featureFlag.keywordLower.enabled  enabled  from <source>
```

A version written before types were hashed prints no type line. A partition whose cache has no version yet prints
`the cache of partition dev holds no version yet; run a cache flow of the partition with the refresh operation to
capture one`; one whose current version read no system properties prints `system properties: none read yet; refresh a
cache flow of partition dev to read them` (a flow of lookup tables alone reaches no platform and reads none).

With `--json`, the output is one array of versions, each with `partition`, `flow`, `version`, `sequence`, `current`,
`prunedUtc` (null while its records are kept), `retentionDays` (the partition's), `capturedUtc`, `capturedBy`, `runId`,
`origin`, `previousVersion`, `records`, `types` (each `name`, `entityType`, `records`, `hash`, `change`, `since`; the last
three null for a version written before types were hashed) and `systemProperties` (each `service`, `name`, `state`,
`source`, `detail`).

## cache import

Merges type files into the cache of the flow's partition as that flow's capture, for work without an OSDU platform (a
first setup, a test estate). The files stand in for what a search of the partition would return:

- One file per OSDU type, `<Name>.json` in `--from-dir`: `{ "entityType": "reference-data--UnitOfMeasure", "items": [
  { "id": "dev:reference-data--UnitOfMeasure:m", "Code": "m", "Name": "metre" } ] }`. Ids carry no trailing version
  colon.
- A file for every OSDU type the flow declares, and none for a type it does not declare.
- Each file under the declared entity type, every id an id of that entity type in the flow's partition
  (`<partition>:<entityType>:<code>`), and no value under a name the partition's cache does not keep for the type.
- Every value another cache flow of the partition keeps for the type, since a record in the files replaces the cached
  record whole.
- No file for a lookup table. A type the flow fills from a table, a dictionary or a dimension is neither required nor
  accepted: `<Type>.json holds <Type>, which cache flow '<flow>' fills from table <table>; a lookup table is captured from
  its origin, never imported`.

Anything else is refused, naming every mismatch, and nothing is written: `The files under '<dir>' are not what cache flow
'<flow>' declares, so nothing was imported: ...`. A flow that disagrees with another cache flow of the partition is
refused as a refresh would be.

The merge is the one a refresh makes. When it changes the cached content, a version is written and becomes current,
recorded as written by the cache flow, captured by `cli:<user>@<machine>` with no run, its origin `files under <dir>`:

```text
cache of partition dev: version 20261009T094512Z written from cache flow osdu-reference-00-cache, holding 7 type(s) and 312 record(s), now current; types moved: UnitOfMeasure
retention of partition dev: 7 day(s), pruned 2 version(s) and 318 stored row(s); 3 version(s) keep their records
```

Files that add nothing write nothing: `cache of partition dev: the files add nothing version <version> does not already
hold, so nothing was written`. Either way the import then applies the partition's retention, as a refresh does, and its
second line says what it pruned: `nothing to prune`; `nothing pruned yet: <what it waits for>`, after an upgrade until
the repository sync has recorded which versions delivery flows pin; or `not applied (<why>); the next refresh or import
applies it` when it failed, which leaves the import standing. With `--json`, an import reports `partition`, `flow`,
`version`, `written`, `types`, `records`, `typesMoved`, `typesRemoved` and `retention` (`retentionDays`, `kept`,
`pruned`, `rowsRemoved`, `failure`, `deferred`).

Records imported this way are not the platform's: a record delivered against them carries references to whatever the
files say. Fill the cache of a partition that delivers for real by refreshing its cache flow.

## Errors and exit codes

| Message | Cause |
| --- | --- |
| `name the partition or the cache document.` | No target after `list` or `import`. |
| `'<verb>' is not a cache subcommand.` | A subcommand other than `list` or `import`. |
| `Environment variable 'SQLFLOW_CATALOG_DB' is not set.` | No `--db`, no `SQLFLOW_CATALOG_DB`, no `SQLFLOW_OSDU_DB`. |
| `The directory '<dir>' to import cached types from does not exist.` | A wrong `--from-dir`. |
| `The directory '<dir>' holds no cached type files ({Name}.json), so there is nothing to import.` | An empty directory. |
| `the cache flow builds a cache for dev, test; name the one the files belong to with --partition.` | `import` with `--partition '*'`. |

The command exits 0 on success, 1 on a usage error or a failure (printed as `ERROR  <message>`, with the usage text
after a usage error), and 130 when interrupted with Ctrl+C.

## Examples

```bash
# the versions of the dev partition's cache (the catalog from SQLFLOW_CATALOG_DB)
sqlflow cache list dev

# the versions of every partition a cache flow builds, as JSON
sqlflow cache list reference/osdu-reference-00-cache.yaml --json

# fill the test partition's cache from type files, without OSDU
sqlflow cache import reference/osdu-reference-00-cache.yaml --from-dir cache-records --partition test
```

## Related

- [The partition cache](../concepts/partition-cache.md): versions, sharing, change tags and rollout.
- [Cache flow](../flow/cache.md): what a refresh captures.
- [Running an OSDU flow](run.md) and SQLFlow's [`sqlflow run`](../../../../sqlflow/docs/reference/cli/run.md).
- [`sqlflow db`](db.md): the module database the cache lives in.
