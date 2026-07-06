---
id: flow-exp
title: "Export flow (flowType: exp): tables to CSV/Parquet files"
type: flow-reference
summary: "flowType: exp reads a SQL Server table or view and writes CSV or Parquet files, one full file or chunked by day, month, or integer key."
keywords:
  - export
  - flowtype exp
  - csv
  - parquet
  - chunking
  - filetype
  - subfolderpattern
  - source.filter
yamlPath: "(root, flowType: exp)"
related:
  - flow-overview
  - source-type-parquet
  - flow-hooks
  - guide-incremental-and-backfill
sourceRefs:
  - src/SqlFlow.Yaml/YamlExportFlowLoader.cs
  - src/SqlFlow.Yaml/ExportYaml.cs
  - src/SqlFlow.Yaml/YamlDocumentParts.cs
  - src/SqlFlow.Core/Export/ExportFlow.cs
  - src/SqlFlow.Core/Export/IExportDestination.cs
  - src/SqlFlow.SqlServer/Export/ExportSegmentPlanner.cs
  - src/SqlFlow.SqlServer/Export/ExportFlowRunner.cs
  - src/SqlFlow.SqlServer/Export/ExportFileWriters.cs
  - src/SqlFlow.Execution/DocumentLoader.cs
  - samples/export/orders-export.flow.yaml
---

# Export flow (flowType: exp)

An export flow reads a SQL Server table or view and writes it to one or more CSV or Parquet files: one full file by default, or chunked into one file per day window, month window, or integer-key range. The source must be SQL Server (mssql or azdb) because the export read is generated T-SQL; a foreign source provider is rejected at parse time. Empty chunks leave no file, and every generated SQL statement (the column probe, the key-max probe, and each per-segment SELECT) is captured in the run's SQL trace, on success and on failure.

## Minimal example

```yaml
flowType: exp
name: orders-export

connections:
  dwh: ${env:SQLFLOW_DW}

source:
  server: dwh
  object: DW.raw.Orders

target:
  path: ./out
```

Run it with the standard document verbs (src/SqlFlow.Cli/Program.cs):

```bash
sqlflow validate orders-export.flow.yaml
sqlflow run orders-export.flow.yaml
```

## Keys reference

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `flowType` | string | yes | | Must be `exp` for this document type. |
| `name` | string | yes | | Flow identity; becomes `SysAlias` and seeds the stable flow id. |
| `batch` | string | no | | Batch label recorded with the run. |
| `connections` | map | no | | Named connections referenced by `source.server`; omit it when the source uses a direct `connection:`. |
| `source` | map | yes | | The SQL Server object to read. |
| `target` | map | yes | | Where and how the files are written. |
| `export` | map | no | one full file | Chunk policy; omit the whole block for a single full file. |
| `postInvoke` | string | no | | Alias of a declared invoke to run after the files are written. |
| `invokes` | map | no | | Named invoke definitions referenced by `postInvoke`. |
| `servicePrincipals` | map | no | | Named Azure service principals available to invokes. |
| `onErrorResume` | bool | no | `true` | Batch-mode error tolerance flag carried on the flow. |

### name

Required. Missing or blank fails with `'name' is required for an export flow.` The name becomes the flow's `SysAlias`, and the `FlowId` is a stable positive int31 derived deterministically from the name (`YamlDocumentParts.StableFlowId`), so logs and run records key consistently across runs without a database assigning ids.

### connections

The document-local connection registry: each key is an alias, each value either a plain connection reference string, a map with `provider` and `connection` keys, or nothing at all (a bare alias resolves `${env:SQLFLOW_CONN_<NAME>}` by convention). Secrets should not rest in the file; use `${env:NAME}` or `${keyvault:vault/secret}` references.

### source

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `server` | string | one of server/connection | | Alias of a connection declared under `connections:`. |
| `connection` | string | one of server/connection | | Inline connection reference (a connection named `source` is synthesized). |
| `provider` | string | no | SQL Server | Provider of a direct `connection:`; must resolve to mssql or azdb. |
| `object` | string | yes | | Three-part name `Database.Schema.Table` (a view works too). Alias: `table`. |
| `filter` | string | no | | Static predicate ANDed to every chunk's read. |

Setting both `server` and `connection` fails with `'source' sets both 'server' and 'connection'; use exactly one.` A `server` alias not declared under `connections:` is a parse error. A non-SQL-Server connection fails with `the source connection '<name>' is '<kind>'; an export flow's source must be SQL Server (mssql or azdb).`

`object` (or the alias `table`) is required; when both are blank the error is `'source.object' is required (a three-part name like Database.Schema.Table).`

#### source.filter

A static predicate appended to every generated SELECT (the legacy `srcFilter`; the legacy engine dropped it in the batched path, V3 applies it). The planner emits `SELECT ... FROM <object> WHERE 1=1 AND <predicate> AND <chunk bounds>`. The loader normalizes the value to a leading `" AND "` plus the predicate, so the author may write the leading `AND` or omit it; a bare `AND` with nothing after it fails with `'source.filter' has no predicate after the AND.`

```yaml
source:
  server: dwh
  object: DW.raw.Orders
  filter: "Status = 'open'"     # or "AND Status = 'open'", same result
```

### target

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `path` | string | yes | | Folder for the exported files; a local path or `file://` URI. |
| `fileName` | string | no | source table name | File name prefix. |
| `fileType` | string | no | `csv` | `csv` or `parquet` (alias `prq`). |
| `encoding` | string | no | `utf8` | CSV encoding: `utf8`, `utf16`, `utf32`, `ascii`. |
| `compression` | string | no | `gzip` | Parquet codec: `gzip`, `snappy`, `none`. |
| `delimiter` | string | no | `;` | CSV column delimiter; must be non-empty. |
| `textQualifier` | string | no | `"` | CSV quote character; exactly one character. |
| `addTimestamp` | bool | no | `true` | Append `_yyyyMMddHHmmss` (run start, UTC) to the file name prefix. |
| `subfolderPattern` | string | no | | Date-token folder pattern (`YYYY`, `MM`, `DD`) for day/month chunks. |

#### target.path

Required; missing fails with `'target.path' is required (the folder for the exported files; a local path or file:// URI).` A relative path is resolved against the flow file's own directory at load time (src/SqlFlow.Execution/DocumentLoader.cs), so `./out` works no matter where the command runs from. The path is matched against the registered `IExportDestination` implementations; the local filesystem destination ships, and cloud destinations plug in by URI scheme. A path no destination handles fails the run with `No export destination handles the path '<path>'.`

#### target.fileName

The file name prefix. Defaults to the source object's table name. The final file name is `<prefix><timestamp><chunk postfix>.<fileType>`, where the chunk postfix is the date or key range for chunked exports (for example `orders_20260617174824_2024-01-01-2024-01-31.csv`) and empty for a full export.

#### target.fileType

`csv` (default) or `parquet` (alias `prq` is accepted and stored as `parquet`). Anything else fails with `'target.fileType' must be csv or parquet; got '<value>'.` The value is also the file extension.

#### target.encoding

CSV only. Accepted tokens: `utf8`/`utf-8` (default, stored as null and written as UTF-8 without BOM), `unicode`/`utf16`/`utf-16` (UTF-16), `utf32`/`utf-32` (UTF-32), `ascii` (ASCII). Any other token fails at parse with `'target.encoding' must be utf8, utf16, utf32, or ascii; got '<value>'.` The writer would silently fall back to UTF-8 for unknown tokens, so the loader rejects them instead of exporting in the wrong encoding.

#### target.compression

Parquet only, the codec: `gzip` (default), `snappy`, or `none`. Unknown values fail at parse with `'target.compression' must be gzip, snappy, or none; got '<value>'.` (The writer would map unknown values to gzip otherwise, so they are rejected up front.)

#### target.delimiter

CSV column delimiter, default `;`. An empty string fails with `'target.delimiter' must not be empty.` Multi-character delimiters are allowed.

#### target.textQualifier

CSV quote character, default `"`. Must be exactly one character; anything else fails with `'target.textQualifier' must be a single character (omit it for the default double quote); got '<value>'.` The CSV writer quotes every string-typed column, plus any value containing the delimiter, the qualifier, CR, or LF; an embedded qualifier is doubled; NULL writes empty.

#### target.addTimestamp

Default `true`: the run's start timestamp is appended to the prefix as `_yyyyMMddHHmmss`. Set `false` for stable file names.

#### target.subfolderPattern

Distributes day/month chunk files into date folders keyed on each chunk's start date. The tokens `YYYY`, `MM`, and `DD` each add one folder level, in that fixed order (for example `YYYY/MM` produces `2024/03/`). It applies only to `export.by: day` and `export.by: month` chunks; full and key exports write directly under `target.path`, as does the trailing null-rows file.

### export

The chunk policy. Omit the whole block for one full file.

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `by` | string | no | `full` | `full`, `day`, `month`, or `key`; single letters `f`/`d`/`m`/`k` accepted. |
| `size` | int | no | `1` | Windows per file: days, months, or rows (key values) per chunk. |
| `dateColumn` | string | when by is day/month | | Date column that bounds each chunk. |
| `keyColumn` | string | when by is key | | Integer key column; ranges run from 0 to the probed MAX. |
| `fromDate` | date | no | 3 years back | Window start (`yyyy-MM-dd`, invariant culture). |
| `toDate` | date | no | today | Window end. |
| `threads` | int | no | `0` | Concurrent chunk writers; 0 means the default (one at a time). |

#### export.by

Case-insensitive tokens `full`/`f` (default), `day`/`d`, `month`/`m`, `key`/`k`; the model stores the legacy letters `F`/`D`/`M`/`K` (note that the model type's own default is `D`; the YAML default is `full`). Any other value fails with `'export.by' must be full, day, month, or key; got '<value>'.`

Chunk semantics (src/SqlFlow.SqlServer/Export/ExportSegmentPlanner.cs):

- `day`/`month`: half-open date intervals on `dateColumn` (`>= start AND < end+1day`), one file per window of `size` days or months, bounded by `fromDate`/`toDate`.
- `key`: inclusive integer ranges on `keyColumn` of `size` key values each, from 0 to the probed `MAX(keyColumn)`; range bounds are zero-padded in the file name.
- Day, month, and key exports each append one trailing `<prefix><timestamp>_NullRows` segment selecting the rows where the chunk column IS NULL.
- `full`: a single segment with no chunk predicate.

#### export.size

Must be at least 1; `'export.size' must be at least 1, got <n>.` otherwise.

#### export.dateColumn and export.keyColumn

`dateColumn` is required when `by` is day or month: `'export.dateColumn' is required when 'export.by' is day.` (or `month`). `keyColumn` is required when `by` is key: `'export.keyColumn' is required when 'export.by' is key.` Both checks fail at parse, not in the planner mid-run.

#### export.fromDate and export.toDate

Invariant-culture dates; a malformed value fails with `'export.fromDate' must be a date like 2024-01-31, got '<value>'.` When `fromDate` is after `toDate` the error is `'export.fromDate' (<from>) is after 'export.toDate' (<to>).` When omitted, day/month exports default the window to 3 years before today through today.

#### export.threads

Must be 0 or more (`'export.threads' must be 0 (default) or more, got <n>.`). The runner fans segments out under `max(1, threads)` concurrent writers, capped at the segment count.

### postInvoke, invokes, servicePrincipals

`postInvoke` names an invoke declared under `invokes:`; an undeclared alias fails at parse with `'postInvoke' references '<alias>', which is not declared under 'invokes:'.` The invoke runs after all files are written. A failed invoke raises and fails the export run unless the invoke's own `onErrorResume` (default `true`) tolerates it, in which case the failed result is returned and the export continues (src/SqlFlow.Core/Invoke/IInvokeRunner.cs). The document executor wires the document's `invokes:` block into the runner, so a declared `postInvoke` executes without a control database; an `ExportFlowRunner` constructed without any invoke runner surfaces a set alias as a clear error rather than silently skipping it.

### onErrorResume

Default `true`. Consumed by full-mode batch execution: in an export batch run, a failed flow stops the batch unless its `onErrorResume` is `true` (src/SqlFlow.SqlServer/FullMode/FullModeIngestionHost.cs).

## Run behavior

The runner (src/SqlFlow.SqlServer/Export/ExportFlowRunner.cs):

1. Resolves the source connection and probes the source columns with a schema-only `SELECT * FROM <object>`; a source exposing no columns fails with `Export source <object> exposes no columns.`
2. For key exports, probes `SELECT MAX([keyColumn]) FROM <object>` (a NULL or missing max plans from 0).
3. Plans the segments and streams each chunk's SELECT to a file through the destination seam (`IExportDestination`); the local filesystem destination is the default.
4. Deletes the file of any chunk that returned zero rows (an empty chunk leaves no file).
5. Runs `postInvoke` if set, then records the run to the run log with FlowType `exp`; when synced to the catalog, export runs record RunFile rows from `result.Files` and the generated SELECTs in RunStatement.
6. Never throws: a failure returns an `ExportRunResult` with `Success = false` and the error message; the SQL trace is carried on the result in both outcomes, and a successful result also carries `TotalRows` and each file's rows and bytes.

The model also carries a `ZipTrg` capability (compress each written file into a single-entry `.zip` in place; a destination that cannot compress fails with `This export destination does not support zipTrg (compression) for '<location>'.`). It is not exposed as a key in the exp YAML document; it is set only on flows constructed on the model directly, mapped from a legacy `flw.Export` row (src/SqlFlow.Core/Export/Legacy/ExportFlowMapper.cs), or loaded from the full-mode control database (src/SqlFlow.SqlServer/FullMode/SqlExportFlowLoader.cs).

## Full example

Adapted from samples/export/orders-export.flow.yaml, chunked by month into date folders:

```yaml
flowType: exp
name: orders-export

connections:
  dwh: ${env:SQLFLOW_DW}

source:
  server: dwh
  object: DW.raw.Orders
  filter: "Status = 'open'"

export:
  by: month
  size: 1
  dateColumn: OrderDate
  fromDate: 2024-01-01
  toDate: 2024-12-31
  threads: 4

target:
  path: ./out
  fileName: orders
  fileType: parquet
  compression: snappy
  addTimestamp: true
  subfolderPattern: YYYY/MM
```

```bash
sqlflow validate orders-export.flow.yaml
sqlflow run orders-export.flow.yaml --log-level trace
```

`validate` prints `OK  '<name>' is valid (export: <object> -> <path> as <fileType>).`; `run` prints each exported file with its rows and bytes. Note that `sqlflow plan` does not support export flows: the chunk plan is determined at run time against the live source.

## See also

- [Flow types overview](overview.md)
- [Parquet source type](source-types/parquet.md)
- [Process and invoke hooks](hooks.md)
- [Incremental loads and backfill](../guides/incremental-and-backfill.md)
