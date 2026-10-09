---
id: delivery-concept-lineage
title: "OSDU flows in lineage: OSDU types, mappings, cache types and dimensions as nodes, and the waves they order"
type: concept
summary: "How OSDU flows, mappings, cache types and dimensions appear in SQLFlow's lineage graph, what each kind reads and writes, and why a flow lands in its wave."
keywords:
  - lineage
  - osdu type node
  - mapping node
  - cache type node
  - dimension node
  - waves
  - flow order
  - dataset node
  - wildcard kind
  - lineage warnings
  - sqlflow lineage
  - explain
related:
  - concept-lineage-graph-and-plan
  - concept-lineage-tiers
  - cli-lineage
  - delivery-flow-overview
  - delivery-flow-mapping
  - delivery-flow-cache
  - delivery-concept-partition-cache
  - delivery-concept-templates
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/OsduLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/AssertionLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/InventoryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/OsduKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLayout.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryCacheStreams.cs
  - sqlflow/src/SqlFlow.Yaml/FlowDocumentKinds.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs
  - docs/lineage-design.md
---

# OSDU flows in lineage: OSDU types, mappings, cache types and dimensions as nodes, and the waves they order

SQLFlow's lineage graph holds every OSDU flow as a flow node, with its data on both sides, and orders it in waves with
everything else ([SQLFlow's lineage graph](../../../../sqlflow/docs/reference/concepts/lineage-graph-and-plan.md)). Each
OSDU kind describes what it reads and writes, and the module adds four kinds of node SQLFlow does not have: the OSDU types
records are delivered as, the mappings a delivery renders through, the cache types a mapping reads, and the dimensions a
dimension flow builds. With those in place the waves read the way the estate works: files land, `ing` flows key them,
cache flows capture lookups, deliveries send records, and flows that read OSDU run after the flows that write it.

This page says what each kind contributes, how the nodes are identified, how they order flows, and what the warnings
mean. The lineage machinery itself (tiers, the plan, cycles, `lineage.json`) is SQLFlow's.

## The nodes the module adds

Each is a SQLFlow dataset node: a node of an external system, keyed by its system, an optional instance, a namespace, a
group and a name. The graph captions a node with its system, hyphens read as spaces, and places it at `namespace.group`.

| Node | System | Instance | Namespace | Group | Name | Caption in the graph |
| --- | --- | --- | --- | --- | --- | --- |
| OSDU type | `osdu-type` | The flow's endpoint as written (`${env:OSDU_URL}`) | The partition | The entity type's group (`master-data`, `reference-data`, `work-product-component`, `dataset`) | The kind, version included | `osdu type · dev.master-data` |
| Mapping | `osdu-mapping` | The flow's endpoint | The partition | The folder the mapping is filed in, relative to the repository (`welldb/mappings`) | The reference (`WellLog@1.0.0`) | `osdu mapping · dev.welldb/mappings` |
| Cache type | `osdu-cache` | (none: a partition has one cache whichever platform filled it) | The partition | `cache` | The type name (`UnitAlias`) | `osdu cache · dev.cache` |
| Dimension | `osdu-dimension` | (none) | The partition | The dimension flow's name | The dimension's name | `osdu dimension · dev.<flow>` |

Node keys are SQLFlow's lowercased form, for example:

```text
dataset:osdu-type:${env:osdu_url}|dev|master-data|osdu:wks:master-data--wellbore:1.3.0
dataset:osdu-mapping:${env:osdu_url}|dev|welldb/mappings|welllog@1.0.0
dataset:osdu-cache|dev|cache|unitalias
```

A few rules follow from the identity:

- **A version is part of the type.** `Wellbore:1.3.0` and `Wellbore:1.4.0` are two nodes, since they are two things to
  deliver.
- **The platform is the reference as written.** Lineage never resolves a reference: two flows naming the same platform
  through two different references are two platforms. A literal URL is identified by a hash and never shown.
- **A flow's partitions multiply its nodes.** A flow that lists `partitions: [dev, test]` contributes its nodes in each
  partition, so the same mapping pinned for both is two mapping nodes, each reading its own partition's cache. A flow that
  leaves its partitions to the registry (no `partitions` and no `data-partition-id` header) is described once, under the
  partition `*`. A flow whose `data-partition-id` header cannot name a partition gets a warning instead of OSDU nodes.

## What each kind reads and writes

| Flow | Reads | Writes |
| --- | --- | --- |
| File flow (SQLFlow) | Its source files, at their repository-relative location | Its `pre` table and typed view |
| `ing` (SQLFlow) | The typed view | Its keyed ingestion table |
| `delivery` | Its record table and every `source.datasets` table, on the server `source.connection` names; the payload files under the root of each payload set its route sends; the mapping it pins, as a node of its own | The OSDU type its mapping fills (`template.kind`); for the routes that register datasets (`file`, `manifest`, `dataset`, `fileAndDdms`, `manifestAndDdms`, and `workflow` when it writes the record to storage with files) also `protocolOptions.datasetKind`; for the workflow route each input's dataset kind |
| Mapping (a node read by the flow, not a flow) | Every cache type it names; every cache type of the partition holding records of an entity type its ids are checked against; every kind its searches look in | Nothing: the flow reading it writes the OSDU type |
| `cache` | Each OSDU type's kind (wildcards allowed); each table type's ingestion table, on `source.connection`; each dictionary type's file; each dimension type's dimension | Each declared type, as a cache type of its partition |
| `retrieval` | Each of `source.kinds` (wildcards allowed) | The record files (`part-*.jsonl`, `.gz` when compressed) and the manifest under `target.location`, as a file drop |
| `assertion` | Each test's kind, one type in one version, in every partition the flow tests | Nothing: its reports stay in the module database |
| `dimension` | Each dimension's kind (wildcards allowed); the dictionary file each cleaning `map` step names | Each dimension, as a dimension node of its partition |
| `inventory` | Each inventory's kind (wildcards allowed) | Nothing another flow reads |

The table reads are SQLFlow database nodes, on the identity SQLFlow gives every server reference, so a delivery flow
reading `OsduData.silver.Wellbore` over `${env:OSDU_DATA_DB}` lands on the node the `ing` flow writes through the same
reference, and is ordered after it.

## The mapping is a node between the cache and the flow

A mapping decides what a record reads, so the graph draws what it reads into the mapping, and the mapping into the flow
rendering with it:

```text
welldb-lookups-00-cache ──▶ UnitAlias (osdu cache) ──▶ WellLog@1.0.0 (osdu mapping) ──▶ welldb-welllog-03-delivery ──▶ WellLog:1.4.0 (osdu type)
```

The flow inherits everything the mapping reads, as a flow reading a SQL view inherits the view's tables, so it is still
ordered after the cache flow. A mapping reads the cache in two ways, and lineage follows both:

| How the mapping reads | Written as | What lineage declares |
| --- | --- | --- |
| A cache type by name | `$cache: UnitOfMeasure.id`, a `lookups` entry, `replace: $cache.UnitAlias`, a `{$cache.CurveDictionary.field}` token in an id | That cache type, in the flow's partition |
| Cached records by what they are | An `id` modifier whose template names the entity type, `ref` written in full (`ref: reference-data--UnitOfMeasure`), or a bare `ref` | Every cache type of the partition, declared by a cache flow of the same repository, that holds records of that entity type |
| The platform, as it renders | A `searches` entry and `$search` | The kind the search looks in, wildcards allowed |

Which entity type a bare `ref` (or one naming the entity alone) points to is told by the template the mapping pins, and
templates live in the module's database, not the repository. Lineage reads the pinned version from there when the host
has it: the repository sync always can, and `sqlflow lineage` can when the command line names a database (`--db`,
`SQLFLOW_CATALOG_DB` or `SQLFLOW_OSDU_DB`). When it cannot, the flow keeps everything the documents alone tell and a
warning says what was left out:

```text
WARN  welldb/flows/welldb-welllog-03-delivery.yaml: delivery flow 'welldb-welllog-03-delivery' shows fewer cache types
in lineage than its mapping reads: mapping 'WellLog@1.0.0' builds a reference with ref at osdu.data.WellboreID,
osdu.data.SamplingDomainTypeID, osdu.data.Curves[].CurveUnit, and the entity type of each is told by its template
(osdu:wks:work-product-component--WellLog:1.4.0 version 26a3c3441882db4f), which could not be read (SqlFlowException:
Environment variable 'SQLFLOW_CATALOG_DB' is not set.), so the cache types those references are checked against are left out.
```

The clause after "which" is one of: `this host has no osdu database to read from`, `is not saved`, or
`could not be read (<reason>)`. Save the template and the next sync shows the missing reads. An id whose entity type a
token gives (`{$param.dataPartition}:{group}--{entity}:...`) is only known at render time, and lineage shows nothing for
what it is checked against.

Lineage reads the mapping from the repository checkout being scanned, through the same layout a run uses
(`render.mappings`, or the nearest `mappings` folder walking up from the flow file), and never outside the checkout.

## Wildcard kinds

A cache, retrieval, dimension or inventory flow, and a mapping's search, may read a kind with `*` in any segment. The
wildcard read is a node of its own, standing for everything of that type the platform holds, and SQLFlow also binds it to
every exact kind the estate writes on the same platform and partition that matches segment by segment (`:` separates the
segments, so `*` never spans two). A search for `osdu:wks:master-data--Wellbore:*` therefore reads the
`Wellbore:1.3.0` node a wellbore delivery flow writes. A pattern nothing in the estate writes stays a source node on its
own, which is the usual case for reference data the platform owns. A write never carries a wildcard, and a flow's own
writes (or what a flow rendering through a mapping writes) are never bound to its own reads.

## How the nodes order flows

Reads and writes of OSDU types, cache types and dimensions order flows exactly as table reads and writes do:

- A delivery flow runs after the `ing` flows writing its tables, after the cache flows writing what its mapping reads,
  and after the delivery flows writing the kinds its mapping searches.
- A cache flow runs after the `ing` flows loading its table types, after the delivery flows writing the kinds it
  captures, and after the dimension flows building its dimension types.
- Retrieval, assertion, dimension and inventory flows run after the delivery flows writing the kinds they read; a file
  flow reading a retrieval's folder runs after the retrieval.
- Two flows that each read what the other writes (a delivery whose mapping looks up the very type it writes, and the cache
  flow capturing that type) are not ordered against each other. Longer loops are reported as dependency cycles and their
  flows go to SQLFlow's fallback wave.

For the example estate of this corpus (wellbores, well logs with curves, a lookup table, a dictionary and the reference
data cache), `sqlflow lineage` lays out:

```text
$ sqlflow lineage .
Lineage over 13 flow(s), 37 object(s), 63 edge(s) (declared+observed)
  wave 1: osdu-reference-00-cache, welldb-unitalias-01-pre, welldb-wellbore-01-pre, welldb-welllog-01-curves-pre, welldb-welllog-01-header-pre
  wave 2: welldb-unitalias-02-ing, welldb-wellbore-02-ing, welldb-welllog-02-curves-ing, welldb-welllog-02-header-ing
  wave 3: welldb-lookups-00-cache, welldb-wellbore-03-delivery
  wave 4: welldb-welllog-03-delivery
  wave 5: welldb-welllog-04-assertion
```

`--explain` shows why a flow is where it is. The well log delivery waits for the lookups cache (its mapping reads
`UnitAlias` and `SamplingDomain` by name) and for the two `ing` flows writing its tables:

```text
$ sqlflow lineage . --explain welldb-welllog-03-delivery
  flow 'welldb-welllog-03-delivery' (delivery): wave 4
    depends on:
      welldb-lookups-00-cache (wave 3) via dataset:osdu-cache|dev|cache|samplingdomain, dataset:osdu-cache|dev|cache|unitalias, ...
      welldb-welllog-02-curves-ing (wave 2) via ${env:osdu_data_db}|osdudata|silver|welllogcurve
      welldb-welllog-02-header-ing (wave 2) via ${env:osdu_data_db}|osdudata|silver|welllog
    reads   [declared] ${env:osdu_data_db}|osdudata|silver|welllog
    reads   [declared] ${env:osdu_data_db}|osdudata|silver|welllogcurve
    reads   [derived] dataset:osdu-cache|dev|cache|samplingdomain
    reads   [derived] dataset:osdu-cache|dev|cache|unitalias
    ...
    reads   [declared] dataset:osdu-mapping:${env:osdu_url}|dev|welldb/mappings|welllog@1.0.0
    reads   [declared] dataset:osdu-mapping:${env:osdu_url}|test|welldb/mappings|welllog@1.0.0
    writes  [declared] dataset:osdu-type:${env:osdu_url}|dev|work-product-component|osdu:wks:work-product-component--welllog:1.4.0
    writes  [declared] dataset:osdu-type:${env:osdu_url}|test|work-product-component|osdu:wks:work-product-component--welllog:1.4.0
    required by: welldb-welllog-04-assertion (wave 5)
```

The reads through the mapping are `derived`: the flow inherits them from the mapping's node. In that offline run the
mapping's bare `ref`s could not be resolved (no database was named), so `osdu-reference-00-cache` is not yet a dependency
of the well log delivery and sits in wave 1. With the pinned template saved and readable, the mapping also reads the
reference cache types holding `UnitOfMeasure` and `WellLogSamplingDomainType` records, and the well log delivery waits
for that cache flow too. Its `ref` to the wellbore is read the same way, as a check against cached wellbore records, and
no cache flow of this estate captures any, so lineage does not order the well log delivery after the wellbore delivery;
delivered as interfaces of one source, the two are ordered by that reference
([one source, several kinds](../guides/multi-kind-source.md)).

The same waves are what the repository sync stamps on each pipeline and what a schedule fires its member flows in, so
"run a flow and its descendants" from a pre flow also runs the cache, assertion and other flows downstream of its
deliveries.

## When lineage is computed again

The repository sync recomputes lineage when a flow document changes, as SQLFlow always does. The module adds three more
reasons, because a delivery flow's lineage depends on documents that are not flows:

- A mapping document was added, removed or edited (compared by path and content hash with the rows the module keeps).
- A template one of the repository's mappings pins was saved after the sync that last saw the mapping, since a bare
  `ref` could not be resolved without it.
- The stored graph holds a delivery flow writing an OSDU type with no read of a mapping node, which is what a graph drawn
  before mappings were nodes looks like. The first ordinary sync after an upgrade draws it again.

## Warnings

A problem with one flow's description is a warning naming the flow, prefixed with its file; it never fails the sync and
never costs other flows their lineage. The common ones:

| Warning | What it means |
| --- | --- |
| `delivery flow '<flow>' shows no OSDU type in lineage: mapping '<ref>' could not be read (<reason>).` | The pinned mapping is missing, invalid, or filed under another name. The flow keeps its tables and files, and loses its OSDU nodes until the mapping reads. |
| `delivery flow '<flow>' shows no OSDU type in lineage: render.mappings '<dir>' lies outside the repository, so mapping '<ref>' is not read.` | Lineage reads only inside the checkout it scans. |
| `<flow> shows no OSDU node in lineage: <why>` | The flow's `data-partition-id` header names no partition lineage can key its nodes by. |
| `... shows fewer cache types in lineage than its mapping reads ...` | The template the mapping pins could not be read; see above. |
| `cache flow '<flow>': type '<type>' holds dictionary <name>, which was not found in a dictionaries/ directory above the flow, so its lineage leaves the file out.` | The dictionary file is not where a refresh would look for it. |
| `retrieval flow '<flow>': target.location '<path>' is a relative path; a retrieval run resolves it against the working directory of the process running it, not the flow file, ...` | Use an absolute path or a storage URI, so the node is where the files land. |
| `<flow> shows no OSDU type for <what>: '<kind>' is not an exact kind.` | A write needs an exact kind, version included. |

## Known limits

- **Endpoint and partition are compared as written.** `${env:OSDU_URL}` in one flow and a literal URL in another are two
  platforms.
- **Which cache types hold an entity type is read from the scanned repository.** A type held only by another
  repository's cache flow is not shown as read by an `id` or a `ref`; a type a mapping names is a node whoever writes it.
- **A mapping no delivery flow pins is not in the graph.** A mapping is a node because a flow reads it.
- **Payload files** are placed at the payload root; the per-record folder below it is only known when a record is read.

## Where you see it

- The lineage graph in the GUI draws the OSDU nodes with their captions, and the catalog explorer lists them under
  Datasets, grouped by system, partition and group; an object's Pipelines tab shows the flows that write and read it.
- The Mappings, Cache and Tests pages jump from a row to its node in the graph.
- The OSDU cache page reads the same edges to show where each cached type's content comes from (the flows upstream of the
  cache flow, and where their stream starts) and which delivery flows read it.
- `sqlflow lineage <folder>` computes the same graph offline, with `--explain`, `--of` and `--json`
  ([sqlflow lineage](../../../../sqlflow/docs/reference/cli/lineage.md)).

## See also

- [SQLFlow: the lineage graph and the execution plan](../../../../sqlflow/docs/reference/concepts/lineage-graph-and-plan.md)
- [SQLFlow: lineage tiers](../../../../sqlflow/docs/reference/concepts/lineage-tiers.md)
- [The OSDU documents](../flow/overview.md)
- [Mapping](../flow/mapping.md), [lookups in a mapping](../flow/mapping-lookups.md), [cache flow](../flow/cache.md)
- [Templates](templates.md) and [the partition cache](partition-cache.md)
