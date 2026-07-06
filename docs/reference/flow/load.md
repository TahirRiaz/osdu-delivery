---
id: flow-load
title: "File flow: load section and index handling"
type: flow-reference
summary: "load block of a file flow: mode (append or truncate-load), batchSize, tableLock, manageIndexes, plus desiredIndexes and the SqlBulkCopy path."
keywords:
  - load.mode
  - append
  - truncate-load
  - batchsize
  - tablelock
  - manageindexes
  - desiredindexes
  - sqlbulkcopy
yamlPath: load
related:
  - flow-schema
  - flow-hooks
  - concept-schema-evolution
sourceRefs:
  - src/SqlFlow.Core/Model/FlowDefinition.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
  - src/SqlFlow.Yaml/FlowYaml.cs
  - src/SqlFlow.SqlServer/SqlBulkLoader.cs
  - src/SqlFlow.SqlServer/BulkTuning.cs
  - src/SqlFlow.SqlServer/SqlServerIndexManager.cs
  - src/SqlFlow.SqlServer/SqlServerDesiredIndexManager.cs
  - src/SqlFlow.SqlServer/DesiredIndexParser.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Core/Model/IndexAction.cs
  - src/SqlFlow.Core/Data/StreamingDataReader.cs
---

# File flow: `load` section and index handling

The `load` block of a file flow controls how rows reach the target table: whether the table is emptied first (`mode`), the SqlBulkCopy batch size and table locking, and whether existing non-clustered indexes are disabled around the load. The entire block is optional; omitting it yields an append load with a 50000-row batch size, a table lock, and no index management. This page also covers the related top-level keys `desiredIndexes`, `preProcess`, and `postProcess`, because they participate in the same load pipeline.

Minimal working example (adapted from samples/csv/csv-truncate-load.flow.yaml):

```yaml
name: Csv_TruncateLoad
source:
  type: csv
  location: ./data/orders.csv
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_TruncateLoad
load:
  mode: truncate-load
```

## Keys reference

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `load.mode` | string | no | `append` | `append` adds rows to the target; `truncate-load` empties the target (full refresh) before loading. |
| `load.batchSize` | int | no | `50000` | Rows per SqlBulkCopy batch (`SqlBulkCopy.BatchSize`). |
| `load.tableLock` | bool | no | `true` | Take a table lock during the bulk load (`SqlBulkCopyOptions.TableLock`). |
| `load.manageIndexes` | bool | no | `false` | Disable non-clustered indexes on an existing target before the load and rebuild them after. |
| `desiredIndexes` | string (top level) | no | none | One or more `CREATE INDEX` statements applied after the load, only on the run that created the table. |
| `preProcess` | list of strings (top level) | no | `[]` | Raw SQL statements executed on the target before the load. |
| `postProcess` | list of strings (top level) | no | `[]` | Raw SQL statements executed on the target after the load. |

Defaults come from `LoadPolicy` in src/SqlFlow.Core/Model/FlowDefinition.cs and the mapping in `MapLoad` in src/SqlFlow.Yaml/YamlFlowLoader.cs.

### `load.mode`

Allowed values: `append` (default) and `truncate-load`.

Parsing first strips every `-` and `_` from the value, then matches the enum names `Append` and `TruncateLoad` case-insensitively (`NormalizeToken` plus `ParseEnum` in src/SqlFlow.Yaml/YamlFlowLoader.cs). So `truncate-load`, `truncateLoad`, `truncate_load`, and `TRUNCATE-LOAD` all map to `LoadMode.TruncateLoad`. An empty or missing value falls back to `append`. Any other value fails validation with:

```text
<file>: 'load.mode' has invalid value '<value>'. Allowed: Append, TruncateLoad.
```

The `<value>` shown is the normalized token, with dashes and underscores already stripped.

With `truncate-load`, the engine runs `TRUNCATE TABLE [schema].[table];` against the qualified target before opening the source (`SqlBulkLoader.TruncateAsync` in src/SqlFlow.SqlServer/SqlBulkLoader.cs). The table then always reflects only the current run's data.

### `load.batchSize`

Integer, default `50000`. Sets `SqlBulkCopy.BatchSize`: the number of rows sent to SQL Server per batch.

### `load.tableLock`

Boolean, default `true`. When true the bulk copy runs with `SqlBulkCopyOptions.TableLock`; when false it uses `SqlBulkCopyOptions.Default` (row locks). A table lock is the fast path for exclusive loads; turn it off only when concurrent access to the target during the load matters more than throughput.

### `load.manageIndexes`

Boolean, default `false`. Applies only when the target table already exists; see the index-handling section below for exactly which indexes are touched and in what order.

### `desiredIndexes` (top level)

A string containing one or more T-SQL `CREATE INDEX` statements. Applied only on the run that created the table; see below.

### `preProcess` and `postProcess` (top level)

Lists of raw SQL statements run on the target connection. `preProcess` runs after any schema DDL and before index disabling and the truncate; `postProcess` runs after the load, the index rebuild, and desired-index creation (src/SqlFlow.Core/Engine/FlowRunner.cs).

## Order of operations in a run

For a file flow, `FlowRunner.RunAsync` executes the load-relevant stages in this order (src/SqlFlow.Core/Engine/FlowRunner.cs); other stages, such as the incremental probe, `source.open` (between the truncate and the load), and `source.complete`, interleave with them:

1. `schema.apply-ddl`: create or evolve the target table.
2. `target.preprocess`: run `preProcess` statements.
3. `indexes.disable`: if `load.manageIndexes: true` and the table existed before this run.
4. `target.truncate`: if `load.mode: truncate-load`.
5. `target.load`: the SqlBulkCopy load.
6. `indexes.rebuild`: rebuild the indexes disabled in step 3.
7. `indexes.desired`: if the table was created by this run and `desiredIndexes` is set.
8. `target.postprocess`: run `postProcess` statements.

If the run fails after indexes were disabled, the engine makes a best-effort rebuild of those indexes so a failed load does not leave the table with disabled indexes; a rebuild failure at that point is logged but does not mask the original error.

## The SqlBulkCopy path

`SqlBulkLoader` (src/SqlFlow.SqlServer/SqlBulkLoader.cs) loads with:

- `EnableStreaming = true` and `BulkCopyTimeout = 0` (no timeout).
- `BatchSize` from `load.batchSize`, `TableLock` from `load.tableLock`.
- A 1:1 column-name mapping for every field the source reader exposes, so target column order does not matter but names must match.
- The returned row count is `SqlBulkCopy.RowsCopied64`.

Two supporting pieces:

- `BulkTuning.ForBulk` (src/SqlFlow.SqlServer/BulkTuning.cs) raises the connection `PacketSize` to the TDS maximum 32767 when the connection string left it at or below the 8000 default; a pinned custom packet size is preserved.
- `StreamingDataReader` (src/SqlFlow.Core/Data/StreamingDataReader.cs) adapts any `IAsyncEnumerator<object?[]>` row source into a forward-only `DbDataReader`, so sources of any size stream into SqlBulkCopy at bounded memory; a `null` cell surfaces as `DBNull.Value`.

## Index handling around a load

Two mutually exclusive paths exist, selected by whether the target table existed before the run (src/SqlFlow.Core/Engine/FlowRunner.cs):

- New table (created by this run's DDL): the `desiredIndexes` script is applied after the load.
- Existing table with `load.manageIndexes: true`: current non-clustered indexes are disabled before the load and rebuilt after.

Because the paths never overlap, no index work is duplicated.

### Disable and rebuild (`load.manageIndexes`)

`SqlServerIndexManager` (src/SqlFlow.SqlServer/SqlServerIndexManager.cs) queries `sys.indexes` for indexes that are `NONCLUSTERED`, not primary-key backed, not unique-constraint backed, not already disabled, and named. Each match gets:

```sql
ALTER INDEX [name] ON [schema].[table] DISABLE;
```

before the load, and after the load:

```sql
ALTER INDEX [name] ON [schema].[table] REBUILD;
```

Disabling keeps the index definition in place, so the plain rebuild restores each index exactly, including included columns, filters, and options. Primary-key, unique-constraint, and clustered indexes are never touched: a disabled clustered index would make the table inaccessible, and constraint-backed indexes are not safe to disable around a load. The rebuild step skips any index that no longer exists on the table (for example when the table was recreated in the meantime).

### Desired indexes (`desiredIndexes`)

`desiredIndexes` declares the indexes the target should have as verbatim `CREATE INDEX` T-SQL. The engine applies them only on the run that created the table; on later runs against the existing table the disable/rebuild path (when enabled) covers the indexes already present.

`DesiredIndexParser` (src/SqlFlow.SqlServer/DesiredIndexParser.cs) parses the script with the ScriptDom `TSql160Parser`:

- Only `CREATE INDEX` statements are collected; comments and batch separators are ignored.
- Each statement's original text is preserved verbatim and executed exactly as authored.
- A parse error throws `Invalid desired-index script: line N: <message>`; multiple parse errors are joined with `"; "` in the same message.
- A `CREATE INDEX` without a resolvable index or table name throws `Desired-index script contains a CREATE INDEX without a resolvable index or table name.`

`SqlServerDesiredIndexManager` (src/SqlFlow.SqlServer/SqlServerDesiredIndexManager.cs) executes each statement and records an `IndexAction` with `Kind` `Created` or `Failed` (src/SqlFlow.Core/Model/IndexAction.cs). A `SqlException` on one index does not stop the rest; failures surface in the run events as warnings of the form `index [name] not created: <detail>`, and successes as `created index [name] on <table>`.

Example (samples/csv/csv-desired-index.flow.yaml):

```yaml
name: Csv_DesiredIndex
source:
  type: csv
  location: ./data/orders.csv
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_DesiredIndex
desiredIndexes: |
  CREATE NONCLUSTERED INDEX IX_Csv_DesiredIndex_OrderId ON dbo.Csv_DesiredIndex (OrderId);
  CREATE NONCLUSTERED INDEX IX_Csv_DesiredIndex_Customer ON dbo.Csv_DesiredIndex (Customer) INCLUDE (Amount);
```

## Full example

Adapted from samples/quickstart/orders.flow.yaml and samples/quickstart/orders.indexed.flow.yaml:

```yaml
name: orders

source:
  type: csv
  location: ./orders.csv
  options:
    delimiter: ","
    header: true

target:
  connection: ${env:SQLFLOW_DW}
  schema: dbo
  table: Orders

schema:
  evolve: widen

load:
  mode: truncate-load        # append | truncate-load
  batchSize: 50000
  tableLock: true
  manageIndexes: true        # disable non-clustered indexes before load, rebuild after

preProcess:
  - "PRINT 'sqlflow: pre-process orders'"
postProcess:
  - "UPDATE STATISTICS dbo.Orders"
```

Run it with the CLI (src/SqlFlow.Cli/Program.cs):

```bash
sqlflow validate orders.flow.yaml   # check the definition
sqlflow plan     orders.flow.yaml   # preview the exact SQL (changes nothing)
sqlflow run      orders.flow.yaml   # create/evolve the table and load
```

Integration coverage: tests/SqlFlow.Core.Tests/Integration/CsvLoadIntegrationTests.cs (load modes), tests/SqlFlow.Core.Tests/Integration/IndexIntegrationTests.cs (disable/rebuild and desired indexes), tests/SqlFlow.Core.Tests/DesiredIndexParserTests.cs (script parsing).

## See also

- [schema section: table creation and evolution](schema.md)
- [preProcess and postProcess hooks](hooks.md)
- [Schema evolution concept](../concepts/schema-evolution.md)
