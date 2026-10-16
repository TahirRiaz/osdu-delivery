---
id: delivery-concept-dimension-tables
title: "The tables a dimension keeps: the osdu model behind a dimension flow, and its dim_ tables and v_dim_ views"
type: concept
summary: "Every osdu table of a dimension: its grain, keys and contents, what writes and reads it, and how the dim_ tables and v_dim_ views are laid out from them."
keywords:
  - dimension tables
  - DimensionMember
  - DimensionValue
  - DimensionAttribute
  - DimensionRun
  - DimensionChange
  - DimensionView
  - DimensionKeyRecord
  - "osdu.dim_"
  - "osdu.v_dim_"
related:
  - delivery-flow-dimension
  - delivery-concept-dimension-blueprint
  - delivery-cli-dimensions
  - delivery-concept-architecture
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduDbContext.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTables.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionViewStore.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRemoval.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20260930103543_DimensionFlows.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261001163440_DimensionTables.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261009204033_DimensionViews.cs
---

# The tables a dimension keeps: the osdu model behind a dimension flow, and its dim_ tables and v_dim_ views

A dimension flow's build keeps what it read in tables of the module's `osdu` schema, owned by the module's model and
created by its migrations, and from them makes the two kinds of object no migration creates: each dimension's own table,
`[osdu].[dim_<dimension>]` ([The dimension's table](../flow/dimension.md#the-dimensions-table)), and each view, `[osdu].[v_dim_<view>]`
([Views](../flow/dimension.md#views)). The model's tables are the ledger of the dimension; the `dim_` tables and `v_dim_` views are what a
report, a pipeline or a person reads.

Every table but the views' record belongs to a ledger of kind `dimension`, one per flow and partition, and is keyed by
the ledger partition's number (`PartitionId`) and an identity of its own, which is how the others name its rows: they
are joined on numbers, never on text, with no foreign keys. Text a filter or a key compares (a value, a key, an attribute
value, a collected text) is held in a binary collation and compared exactly. The tables keep the names they were made
with: a **member** (`DimensionMember`) is a dimension's value, and an **original** (`DimensionValue`) is a key.

| Table | One row per | Unique by | Holds |
| --- | --- | --- | --- |
| `Dimension` | dimension of a flow in a partition | `DimensionId`; `PartitionId`, `FlowId`, `Name` | The declaration its last build read with (kind, query, path, the label's paths, the attributes, the elements, the clean steps and their hash), the field the index was asked for, what each collected attribute was read from, its table's name and the names of its key's and value's columns, how many values and keys it holds, its last build and its last full load. |
| `DimensionRun` | build | `DimensionRunId` | The platform run, who started it, its status (`running`, `completed`, `failed`, `cancelled`), how it loaded (`full` or `incremental`, its window, the records it found changed and the keys it read again), the query run, the kinds read and the template each was read against, its counts and requests, up to 100 lines of notes, and its error. |
| `DimensionMember` | value | `PartitionId`, `DimensionId`, `Value` | The value, the records holding any of its keys (and whether that count is exact), its keys, its keys no query can carry, its filter and how many queries it takes, the build that first found it, and the build that no longer did. |
| `DimensionValue` | key | `PartitionId`, `DimensionId`, `OriginalHash` (SHA-256 of the key) | The key exactly as the index holds it, its value or why it has none (`empty`, `tooLong`, `dropped`, `failed`), what cleaning said of it, the label read and the record it was read from, its filter, its records, whether a query can carry it, the build that first found it, the one that no longer did, and the build since which it is under its value. |
| `DimensionAttributeName` | attribute or element field of a dimension | `PartitionId`, `DimensionId`, `Name` | Its number, its name, whether it is collected, and its place among those the dimension declares now (null once it is declared no more, its number kept). |
| `DimensionAttribute` | key, attribute and value | `PartitionId`, `DimensionId`, `ValueId`, `AttributeId`, `Value` | The value read for the key, where it was read (the record's id, or for a collected attribute the text the records hold; null for the `unlabelled` value), and for a collected value how many of the key's records hold it. Indexed by attribute and value, so the keys of a value (`Country` is `Norway`) are one seek. |
| `DimensionCollectedText` | collected attribute and text | `PartitionId`, `DimensionId`, `AttributeId`, `TextHash` | A text the records hold at the collected path, exactly as the index holds it, the value it is shown as, and the records holding it: what a search picking a collected value asks for. |
| `DimensionElement` | field of an object of a key's elements | `PartitionId`, `DimensionId`, `ValueId`, `Seq`, `AttributeId` | The object's place among the key's (from 1), the field by its number, and the value read (up to 4,000 characters). |
| `DimensionKeyRecord` | key and record its label and attributes were read through | `PartitionId`, `DimensionId`, `ValueId`, `RecordHash` | The SHA-256 of the record's id without its version, and the entity type it names: how an incremental load finds the keys to read again when such a record changes, and the types to ask what changed in (`osdu.DimensionKeyRecord` in [Full and incremental loads](../flow/dimension.md#full-and-incremental-loads)). |
| `DimensionRecord` | record and key it held | `PartitionId`, `DimensionId`, `RecordHash`, `ValueId` | The SHA-256 of the record's unique key (`incremental.keyColumns`): the keys a record held when a load last read it. Only a flow with an `incremental` block keeps them. |
| `DimensionChange` | change to a key | `ChangeId` | The build, the key, the change (`added` after the first build, `removed`, `moved`, `restored`), and the values it moved from and to. |
| `DimensionView` | view in the database | `Name`; `ViewName` | The flow that declares it, the ledger of the build that last wrote it, its declaration, the statement it was written with and that statement's hash (null when a build dropped it to be written again), the tables it reads, its columns, why it is not in the database when it is not, and the run and person that wrote it. A view spans every partition its tables hold, so it belongs to no ledger partition. |
| `DimensionViewCheck` | check of a view in a partition | `CheckId` | The run, `passed` or `failed`, the rows, what each join found, what each converted column could not read with examples, and the error. A view keeps its 50 newest checks of each partition. |

**What writes them.** A build registers its dimension and its build when it starts, and writes the rest when it has read
everything: in one transaction, under an application lock per dimension (`osdu-dimension:<partition number>:<dimension
id>`), its keys and values are staged and merged by set-based statements that add, rewrite, mark removed and bring back,
give each attribute its number, rewrite the attributes, elements and records read through of the keys it found, log every
change, and bring the dimension's own table to the same rows, then close the build with its counts. Nothing is deleted:
a value or key no build finds is marked removed and keeps its number should it come back. A failed build writes nothing
but its build row. The views and their checks are written after the dimensions, each view in a transaction of its own.
[Removing a dimension](../flow/dimension.md#removing-a-dimension) deletes every row of it in its partition, in batches under the same lock,
and drops its table with the last partition writing it; removing a view deletes its record and its checks.

**What reads them.** The dimension's pages, [`sqlflow dimensions`](../cli/dimensions.md) and the API read the values,
keys, attributes, changes and builds; a [composed search](../cli/dimensions.md#search) reads values, keys and collected
texts; a [cache flow](../flow/cache.md) capturing a dimension reads its values, keys and attributes; an incremental load reads its builds,
records read through and records; [the blueprint](dimension-blueprint.md) reads the last build's kinds and the attributes' coverage.

**How the `dim_` table is laid out from them.** A row of `[osdu].[dim_<dimension>]` is a key the dimension holds now
(`DimensionValue`, not removed) under its value (`DimensionMember`): `key_id` is the key's `ValueId`, the key's column
its text, the value's column its value's text, `records` its records (or the collected value's, or 1 for an element),
`filter` its filter, and each attribute's column the value of the attribute in that place (`DimensionAttributeName`
ordering `DimensionAttribute`). A collected attribute makes a row for each value of the key, and elements a row for each
object (`DimensionElement`), numbered in `element`. A build writes only the rows that differ, so a row keeps its `id`.
A view reads only `dim_` tables, of its own flow; `DimensionView` records each view a build made, so a build writes over
and drops only views it recorded.
