---
id: delivery-concept-partitions
title: "OSDU partitions: delivering the same flows to dev, test and prod, and how a run picks its partition"
type: concept
summary: "How a flow names its OSDU data partitions or follows the registry, how a run binds to one, and what each partition keeps of its own."
keywords:
  - data partition
  - data-partition-id
  - partition registry
  - default partition
  - partitions key
  - keepledger
  - registry-driven flow
  - multiple environments
  - "--set partition"
  - "partition=*"
  - per-partition ledger
  - per-partition cache
  - per-partition configuration
related:
  - delivery-cli-partition
  - delivery-cli-config
  - delivery-flow-delivery
  - delivery-flow-cache
  - delivery-concept-ledger
  - delivery-concept-partition-cache
  - delivery-concept-gui
  - delivery-cli-run
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Model/DeclaredPartition.cs
  - osdu/src/SqlFlow.Delivery/Model/RegisteredPartitions.cs
  - osdu/src/SqlFlow.Delivery/Model/SourceDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/CacheDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/AssertionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/InventoryFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/DeliveryDestination.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheScope.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/CacheExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/RetrievalExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/PartitionLedgers.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryPartitionRegistry.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryConfigStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery.Cli/CliPartitions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryPartitionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/WorkbenchPartition.cs
  - osdu/gui/src/module.tsx
  - osdu/docs/census/keys.delivery.json
  - osdu/docs/census/keys.cache.json
---

# Partitions

An OSDU platform keeps its data in partitions: every request names one in its `data-partition-id` header, and every
record id starts with one. One OSDU Delivery catalog can deliver the same flows and the same mappings to several
partitions (`dev`, `test`, `prod`), and the partition is what ties a run together: a run for `test` reads the cache built
for `test`, sends `data-partition-id: test`, mints ids that start with `test:`, keeps its records in a ledger of their
own, and resolves the `test` values of the central configuration first. This page explains how a flow says which
partitions it works in, how a run picks one, and what each partition keeps apart.

## Three ways a flow names its partitions

| Form | How it is written | Serves |
| --- | --- | --- |
| **Header** | `data-partition-id` in `target.headers` (a delivery flow) or `source.headers` (a cache, retrieval, assertion, dimension or inventory flow), a literal id or a `${env:...}` reference. | The one partition the header names, resolved on the host that runs the flow. |
| **Hard-coded** | `partitions:` lists them, and the header is left out. | The partitions listed, whether or not they are registered. |
| **Registry-driven** | Neither `partitions` nor the header. | Every partition registered with the catalog ([sqlflow partition](../cli/partition.md)). The same documents then deploy to every environment, and a new environment is a row in the registry rather than an edit to every flow. |

A flow that names its partitions, hard-coded or through the registry, is said to work in partitions. A header flow
behaves as flows did before partitions existed: one partition, its ledger under the flow's own name.

A hard-coded delivery flow, whose `dev` partition keeps the ledger the flow kept while it delivered through a header, with
an hourly schedule that delivers to `test`:

```yaml
flowType: delivery
name: welldb-welllog-03-delivery
partitions:
  - name: dev
    keepLedger: true      # the records this flow delivered before it named partitions went to dev
  - test
target:
  endpoint: ${env:OSDU_URL}          # no data-partition-id: every run sets it to the partition it targets
schedule:
  cron: "0 * * * *"
  values:
    partition: test
    logSource: WIRELINE
```

A registry-driven delivery flow, serving every registered partition, with `dev` keeping its earlier ledger:

```yaml
flowType: delivery
name: welldb-wellbore-03-delivery
keepLedger: dev           # only for a registry-driven flow; a hard-coded one marks the entry instead
target:
  endpoint: ${env:OSDU_URL}          # resolved with the run's partition's own configuration values first
```

A cache flow that builds the lookup cache of two partitions, one type only for `test`:

```yaml
flowType: cache
name: welldb-lookups-00-cache
partitions: [dev, test]
source:
  connection: ${env:OSDU_DATA_DB}
types:
  - table: OsduData.silver.UnitAlias
    key: source_unit
    fields: [osdu_unit]
  - table: OsduData.silver.CurveDictionary
    key: mnemonic
    fields: [log_curve_type_id, unit]
    partitions: [test]    # built for test only
```

The rest of each document (source, render, auth) is as in [Delivery flow](../flow/delivery.md) and
[Cache flow](../flow/cache.md); all three examples come from documents that pass `sqlflow validate`.

### Rules for a flow that works in partitions

| Rule | Detail |
| --- | --- |
| Literal names | A partition is its data-partition-id written literally: letters, digits, underscore, hyphen and dot, at most 200 characters. A reference is refused, because the name is the key the partition's cache, ledger and runs are kept under: `partitions[0] is the reference '${env:OSDU_DATA_PARTITION}'. A partition is named literally, by its data-partition-id: ...` |
| Count | At least one and at most 64 per flow, none twice ignoring case. A name YAML would read as a boolean or a decimal number is written in quotes. |
| No header | `target.headers` (or `source.headers`) may not name `data-partition-id`: `target.headers names 'data-partition-id', and the flow names its partitions: every run sets the header to the partition it targets. Remove the header.` |
| No `dataPartition` | `render.parameters` may not set `dataPartition`: every id a run mints is minted in the partition it targets. |
| No `partition` parameter | `parameters` may not declare `partition`, the run value that names the partition. |
| No cache pin across partitions | `render.cacheVersion` may not pin a version when the flow names more than one partition or follows the registry, because a version is a version of one partition's cache. |
| `keepLedger` | On a hard-coded delivery flow, `keepLedger: true` on at most one entry. On a registry-driven one, `keepLedger: <partition>` at the top; refused beside `partitions` and on a header flow. |

### By flow kind

| Kind | Names its partitions with | A run naming `*` | Each partition keeps |
| --- | --- | --- | --- |
| Delivery | `partitions` (a name, or `{ name, keepLedger }`), or top-level `keepLedger` when registry-driven | refused: a delivery acts in one partition | its own ledger |
| Cache | `partitions`; `types[].partitions` narrows a type to some of them (any partitions, for a registry-driven flow) | builds every partition the flow serves, one after another | its own cache |
| Assertion | `partitions` | refused: a report describes one partition | its own report history |
| Dimension | `partitions` | refused | its own dimensions |
| Inventory | `partitions` | refused | its own inventories |
| Retrieval | the `data-partition-id` header only | a run that names any partition is refused | its runs, in the partition its header resolves to |

## The partition registry

The registry is the table `[osdu].[Partition]`: the partitions registered with the catalog, and the one marked default.
It is kept with [sqlflow partition](../cli/partition.md), on the GUI's Partitions page (under Setup in the OSDU menu) and
through `/api/v1/delivery/partitions`. The first partition registered becomes the default, and the default can be
removed only when it is the last. Removing a partition deletes nothing kept under it.

The registry settles only what a document leaves open: which partitions a registry-driven flow serves, and which
partition a run that names none runs in. A repository sync describes each registry-driven flow in the partitions
registered at that moment (its interfaces and its cache types, one per partition), and warns about a registry-driven cache
flow while no partition is registered:
`cache flow '<name>' builds a cache for every partition registered with the catalog, and none is registered yet, so it builds none.`

## The partition a run targets

A run of a flow that works in partitions targets exactly one partition (or, for a cache refresh, every one in turn). It
names it in the run value `partition`:

| Where the run starts | How it names the partition |
| --- | --- |
| The GUI | The partition picked in the title bar's switcher. Every run started from the GUI acts in it, and a flow that does not serve it refuses to start, saying which partitions it serves. |
| `sqlflow run`, `sqlflow trigger` | `--set partition=<id>` ([Running an OSDU flow](../cli/run.md)). |
| A schedule | `values: { partition: <id> }` in the YAML `schedule:` block, or `--set partition=<id>` on `sqlflow schedules create`. |
| The module's own verbs | `--partition <id>` on `check`, `preview`, `values`, `records`, `cache`, `config`, `assertions`, `dimensions` and `inventory list`; the other `inventory` subcommands take the partition as an argument. |

The partition is settled the same way on every host:

| The run names | Hard-coded `partitions` | Registry-driven | Header |
| --- | --- | --- | --- |
| a partition | that one, which the flow has to list; it need not be registered | that one, which has to be registered | refused |
| none | the registry's default when the flow lists it, else the flow's only partition, else refused | the registry's default, else refused | the header's |
| `*` (cache refresh only) | every partition listed, one after another | every registered partition, one after another | refused |

The refusals name what would settle it, for example
`Flow 'welldb-welllog-03-delivery' serves dev, test and no partition is the default; name the one this run or request targets, or make one the default.`,
`Partition 'prod' is not registered with the catalog, so nothing runs in it. Register it on the Partitions page or with 'sqlflow partition add prod'; the registered partitions are dev, test.`,
and for a header flow
`Flow '<name>' names no partitions; it delivers to the partition its target.headers name, so a run or request cannot target '<id>'. ...`.
The registry is read only when the document leaves the partition open: always for a registry-driven flow, and for a
hard-coded one only when the run names no partition and the flow lists several, to find the default. A run of a
hard-coded flow that names its partition, or of one that lists a single partition, never reads it.

The partition is taken off the run's values before the flow's parameters are read, so naming it moves neither a
watermark nor a submission's scope. The one exception is a header flow written before partitions existed that declares a
parameter called `partition`: it keeps the value as its own.

The verbs that only read what a partition kept (`records`, `assertions`, `dimensions`) bind a named partition as it is,
even one taken out of the registry, so its records and reports stay readable. `check`, `preview`, `values` and `cache`
settle it as a run would.

## What a bound run is

Bound to its partition, a run is the flow as if it had been written for that partition alone:

- **The header.** Every request carries `data-partition-id: <partition>`.
- **The ids.** The mapping's `dataPartition` is the partition, written literally, so every id and reference it mints
  starts with it. The flow kind supplies it to a mapping that declares the parameter; a flow that works in partitions
  never reads `${env:OSDU_DATA_PARTITION}` for it.
- **The cache.** The mapping reads that partition's cache ([The partition cache](partition-cache.md)).
- **The ledger.** The run keeps its records in the partition's own ledger (below).
- **The configuration.** Every `${env:NAME}` the flow names resolves from the partition's values of the central
  configuration first, so one document reaches a different endpoint, legal tag and access groups per partition
  ([sqlflow config](../cli/config.md)).

A run logs the partition it was bound to, and everything downstream (planning, rendering, the routes, the cache capture,
the preflight) works on the bound flow.

## A ledger per partition

Each partition a flow works in keeps a ledger of its own, named `<ledger>@<partition>`
(`welldb-welllog-03-delivery@test`) and keyed apart from every other, so the same source row delivered to `dev` and to
`test` is two records, each with its own history. The partition marked `keepLedger` keeps the ledger the flow kept before
it worked in partitions, under its old name and identity, so moving a flow from a header to partitions keeps every
record it delivered.

Two guards keep the ledgers apart, checked when a whole run starts:

- When no partition keeps the flow's earlier ledger and that ledger holds records delivered to a partition the flow
  serves, the run is refused, because that partition's new, empty ledger would deliver them all again as new. The message
  counts the records per partition and ends with what to write: `Mark the partition they were delivered to with
  keepLedger: true under partitions (<partition>). Nothing ran.`, or for a registry-driven flow
  `Name the partition they were delivered to at the top of the flow: keepLedger: <partition>. Nothing ran.`
- When the partition that keeps the earlier ledger finds records in it delivered to another partition, the run is
  refused rather than treat them as its own.

How the ledger keys every row by partition is in [The delivery ledger](ledger.md).

## In the GUI and the API

The partition is chosen in one place in the GUI: the title bar's switcher, which lists every registered partition and
every partition something is still kept under (marked unregistered), starts at the registry's default and remembers the
choice. Every OSDU page is read in it and every run started from the GUI writes to it; no page has a partition picker of
its own. Every call the GUI makes carries it in the `X-Osdu-Partition` header ([The GUI](gui.md)).

The flow-level routes of the delivery API take `?partition=` and settle it as a run does, answering
`400 Partition required` or `400 No such partition` when it settles none; the reads across flows take `?partition=`, else
the `X-Osdu-Partition` header, else read every partition ([The delivery API](api.md)).

## See also

- [sqlflow partition](../cli/partition.md): keeping the registry.
- [sqlflow config](../cli/config.md): values set for one partition.
- [Delivery flow](../flow/delivery.md) and [Cache flow](../flow/cache.md): the `partitions` and `keepLedger` keys in context.
- [The delivery ledger](ledger.md): how every ledger row is keyed by its partition.
- [The partition cache](partition-cache.md): one cache per partition.
- [Running an OSDU flow](../cli/run.md): `--set partition=<id>` and the other run values.
