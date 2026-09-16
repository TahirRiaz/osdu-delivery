# Stage 4 design: the OSDU flow reads ingestion tables

This is the working design for stage 4 of `docs/plan.md`: data reaches OSDU through SQLFlow's own pre-ingestion and
ingestion flows, and the OSDU flow reads the keyed ingestion tables they load. There is no drop manifest, no drop reader,
no replica and no SQL-source extraction.

> **Manual submission was removed after this stage.** Section 4 designed a way to send records in an API request, land
> them as files for the pre flows and queue the chain that delivered them. It was built, then taken out: records delivered
> by hand are files placed where a pre flow reads them, and the regular chain loads and delivers them. The migration
> `RemoveManualSubmission` drops `osdu.InlineSubmission`, `osdu.SubmissionLanding` and the `Reference` and `GroupId`
> columns of `osdu.Submission`. The sections below that describe the flow document are current; the change lists,
> tests and ordering in sections 3 and 5 to 7 still name the submission pieces as they were built at the time.

## Decisions

- **Nodes reach the ledger through the module database.** SQLFlow nodes speak only the node protocol and open no
  catalog connection. The OSDU engine runs on the node and reads and writes the `osdu` ledger per record while it plans
  and delivers, so an OSDU Delivery node is configured with the `osdu` module database connection reference. The
  ingestion tables are read through the flow's own `source.connection` reference, resolved on the node.

## 0. Facts in the vendored SQLFlow the design depends on

- Pre-ingestion (file) flow keys: `name`, `batch`, `lifecycle`, `source{type,location,options}`,
  `target{connection,schema,table}`, `schema{evolve,defaultColumnType,overrides}`,
  `load{mode,batchSize,tableLock,manageIndexes,resetWhenConsolidated}`,
  `transform{inferTypes,onConvertError,threshold,sample,preserveLeadingZeros,generateView,columns[]}`,
  `incremental{table,dateColumn,overlapDays,watermarkColumn,watermarkOverlap,fullLoad}`, `preProcess`, `postProcess`,
  `desiredIndexes`; no `flowType` (`sqlflow/src/SqlFlow.Yaml/FlowYaml.cs`, `YamlFlowLoader.cs`).
- The typed view is `[schema].[v_<table>]` (`SqlFlow.Core/Engine/FlowRunner.cs`).
- Provenance columns written by file readers, on by default: `FileName_DW`, `FileDate_DW` (`yyyyMMddHHmmss`),
  `RowNumber_DW`, plus `DataSet_DW`, `FileSize_DW`, `FileRowDate_DW` (`SqlFlow.Sources/FileSourceReaderBase.cs`).
- Ingestion (`flowType: ing`) keys: `connections`, `source{server|connection,provider,object,filter,...}`,
  `target{server|connection,object,...}`, `load{keyColumns,...,reloadColumn,...}`, `change{hashColumns,hashType,
  ignoreColumnsInHash}`, `systemColumns{insertedDate,updatedDate,deletedDate,rowStatus}`,
  `incremental{columns,dateColumn,overlapDays,lookback,fullLoad,fetchMinValuesFromSource}`; three-part object names.
- System columns: `InsertedDate_DW`, `UpdatedDate_DW` (`datetime`), optionally `DeletedDate_DW`, `RowStatus_DW`; a new
  target gets `NCI_UpdatedDate_DW` and a key index.
- `UpdatedDate_DW` is set to `SYSUTCDATETIME()` on insert and on update only for rows whose checksum changed; `_DW`
  columns are excluded from the checksum, so an identical re-landed row keeps its `FileName_DW`, `RowNumber_DW` and
  `UpdatedDate_DW`. It is stamped at statement start, not commit, and many rows share one value per statement.
- Landing reset truncates a pre table only once every consumer of its typed view has consolidated it.
- `RunParameters`: `Operation` `^[a-z][a-z0-9-]{0,31}$`, at most 32 `Values`, `Payload` one JSON object of at most
  64,000 characters.
- Lineage: a `RegisteredFlowDocument` contributes headers and declared endpoints but no object `Reads`/`Writes` facts;
  the ingestion case in `FlowSetCollector.cs` is the pattern a lineage contributor mirrors.
- Run groups: `RunQueueStore.EnqueueGroupAsync` takes explicit members with waves and per-member parameters, and skips
  queued descendants of a failed or cancelled member.

## 1. The OSDU flow document

### 1.1 `source:`

| Key | Meaning |
| --- | --- |
| `connection` | Connection reference of the database holding the ingestion tables, resolved on the node; also `SourceConnectionReference`. |
| `record.object` | Three-part name of the record ingestion table. |
| `record.key` | Key columns (the ing flow's `load.keyColumns`); must equal the mapping's `dataset.key` columns; string, integer or uniqueidentifier. |
| `record.scope` | `column: parameterName`, each a typed `[column] = @p` predicate. |
| `datasets.<name>.object` | Three-part name of a child ingestion table; the mapping reads `dataset.<name>.<column>`. |
| `datasets.<name>.join` | `childColumn: recordColumn`, covering every key column. |
| `datasets.<name>.orderBy` | Child row order within a record. |
| `datasets.<name>.maxRowsPerRecord` | Ceiling on child rows per record (default 100000). |
| `payloads.<name>.root` | Folder or prefix payload files must sit under; `{parameter}` tokens; relative to the flow file. |
| `payloads.<name>.locationColumn` | Record column with the payload folder, relative to `root` or absolute under `root`. |
| `payloads.<name>.pattern` | Glob under the folder (default `*`). |
| `payloads.<name>.hashColumn` | Column with the payload content hash (unless `change.payloadDetect: lastModified`). |
| `payloads.<name>.chunkCountColumn` | Column with the chunk count. |
| `lastModified` | Optional business version column (stale gate, `StaysBlocked`). |
| `systemColumns.updated/.fileName/.rowNumber/.deleted` | Defaults `UpdatedDate_DW`, `FileName_DW`, `RowNumber_DW`, `DeletedDate_DW`; `fileName: ~` opts out. |
| `incremental.overlapSeconds` | Re-read below the last watermark (default 900, 0 to 86400). |
| `incremental.pageSize` | Keys per page (default 1000). |
| `incremental.isolation` | `snapshot` (default) or `readCommitted`. |
| `incremental.commandTimeoutSeconds` | Default 0 (bounded by cancellation). |
| `work` | Where the intake writes work batches (required). |

Removed: `location`, `manifest`, `records`, `scopes`, path-template `payloads`, `fingerprint`, `knownState`,
`manualSubmission`, `manualSubmissionFileRoots`, `sql`, `replica`, and (since the removal above) `submissions`.
Mappings are unchanged.

### 1.2 `osdu/samples/recall-welllog/flows/recall-welllog.yaml`

```yaml
flowType: delivery
name: recall-welllog
batch: recall

parameters:
  logSource:
    required: true
    description: The Recall log source (STAT_COMP, STAT_CPI, STAT_CORE, STAT_ILT, STAT_PLT, STAT_PRESS).

source:
  connection: ${env:OSDU_SAMPLE_DB}
  record:
    object: OsduSample.ing.WellLog
    key: [source_project, log_id]
    scope:
      log_name: logSource
  datasets:
    curves:
      object: OsduSample.ing.WellLogCurve
      join:
        source_project: source_project
        log_id: log_id
      orderBy: [curve_ordinal]
  payloads:
    curves:
      root: ../data/curves
      locationColumn: curve_folder
      pattern: "chunk_*.parquet"
      hashColumn: payload_hash
      chunkCountColumn: chunk_count
  lastModified: update_date
  incremental:
    overlapSeconds: 900
  work: ../.work/{logSource}
```

`render`, `change`, `target` (endpoint, auth, headers, protocol, options), `reliability`, `schedule` and `verify` stay as
in the current sample, with the schedule carrying `values: { logSource: STAT_COMP }`.

### 1.3 Pre and ing flows

- `recall-welllog-pre`: csv from `../data/welllog` (`srcFile: "*.csv"`) into `pre.WellLog`, `schema.evolve: widen`,
  `transform.inferTypes` with typed `log_run`, `log_version`, `update_date` (`datetime2`), `chunk_count` (`int`),
  `incremental.dateColumn: FileDate_DW`, `load.mode: append`.
- `recall-welllog-curves-pre`: csv from `../data/curves-meta` into `pre.WellLogCurve`, typed `curve_ordinal`,
  `curve_version`, `business_value` (`NULLIF(@ColName, '')`).
- `recall-welllog-ing`: `OsduSample.pre.v_WellLog` into `OsduSample.ing.WellLog`, `keyColumns: [source_project, log_id]`,
  `incremental.columns: [FileDate_DW]`, `systemColumns.insertedDate/updatedDate: true`.
- `recall-welllog-curves-ing`: `OsduSample.pre.v_WellLogCurve` into `OsduSample.ing.WellLogCurve`,
  `keyColumns: [source_project, log_id, curve_id]`.
- `OsduSample` is the Initial Catalog of `${env:OSDU_SAMPLE_DB}`; SQL Server suites generate these files with the test
  database's name.

### 1.4 Sample data

- `data/welllog/welllog_20260901.csv`: `source_project,log_id,wellbore_uwi,log_name,log_run,log_source,index_min,
  index_max,index_increment,index_unit,depth_coding,elev_meas_ref,creator,log_version,log_pass,native_uid,update_date,
  curve_folder,payload_hash,chunk_count` for L-1001, L-1002 (NO_15_9) and L-2001 (NO_16_2).
- `data/curves-meta/welllog_curves_20260901.csv`: `source_project,log_id,curve_ordinal,curve_id,curve_unit,index_unit,
  index_min,index_max,curve_description,curve_version,business_value`.
- `data/curves/<project>/<log>/chunk_00000.parquet` per log.
- `payload_hash` values are computed by `osdu/tools/SampleData` (ported grid, chunk writer and hash from the previous
  `SampleDropBuilder`); `SampleEstateTests` recomputes them.

### 1.5 Wellbore chain

`recall-wellbore-pre`, `recall-wellbore-aliases-pre`, `recall-wellbore-ing` (`keyColumns: [facility_name]`),
`recall-wellbore-aliases-ing` (`[facility_name, alias_name]`) and `recall-wellbore` (record `OsduSample.ing.Wellbore`,
dataset `aliases` joined on `facility_name`, `protocol: osduRecord`).

## 2. Planning over the ingestion tables

### 2.1 Source abstraction (`osdu/src/SqlFlow.Delivery/Source/`)

- `IIngestionSourceFactory.Open(flow, values, secrets)` on the node.
- `IIngestionSource`: `OpenAsync(selection, storedWindow)` returns a header (columns per dataset, key SQL types,
  `UpperUtc` from `SYSUTCDATETIME()`, estimated candidates, tier-0 answer); `SliceBoundsAsync(header, slices)`;
  `ReadAsync(header, keyRange)` streams `SourceRecord`s.
- `SourceSelection`: `Incremental(lowerUtc)`, `Full`, `Keys(keys)`, `Inline(submissionId, keys)`.
- `SqlServerIngestionSource` (identifiers bracket-quoted in `IngestionSql`); an in-memory implementation in the tests.

### 2.2 Candidate keys: keyset by record key

The window `(lower, upper]` is fixed at open. Pages go by record key (deterministic, sliceable, never splits rows that
share an `UpdatedDate_DW`). The candidate query unions record rows changed in the window with records whose child rows
changed in it (and child `DeletedDate_DW` in the window when present), filtered by the scope predicate, keyset-paged
`ORDER BY` the key. Window parameters use the column's own SQL type so `NCI_UpdatedDate_DW` stays seekable. `Full` drops
the window; `Keys` joins `OPENJSON(@keys) WITH (...)` typed from `sys.columns`, and a missing key is held with a reason.

### 2.3 Rows of a page

One command per page (snapshot isolation by default): the page keys into a table variable, then one result set per
dataset joined on the keys, child rows ordered by `orderBy` and grouped in memory. A record over `maxRowsPerRecord` is
held. Snapshot refusal names the `ALTER DATABASE` fix and the `readCommitted` alternative. Deadlocks are retried. SQL
types normalise to `long`, `decimal`, `double`, `bool`, UTC `DateTime`, `DateTimeOffset`, `DateOnly`, `Guid`, `string`,
null. `SourceRecord.Origin` becomes `SourceOrigin(FileName, RowNumber, UpdatedUtc)`; `Version` carries the fingerprint;
`DeclaredDeliveryKey` is removed.

### 2.4 Watermark, re-plan, key-scoped plans

- One watermark per `(FlowId, Scope)`; next lower bound `UpdatedThroughUtc - overlapSeconds`; none means `Full`.
- Written by `FinalizePlanningAsync` only for a whole-scope plan whose fan-out members all succeeded, with the stored
  window's upper bound. `Keys` and `Inline` plans never move it.
- Tier 0: skip the run when no row changed in the window and no plan-requested record waits. A moved render context is
  handled by cache rollout and explicit `replan`, not by re-rendering the scope.
- `ForceRedeliverAsync`, `ReleaseAsync` and `RollOutTagAsync` stamp `Record.PlanRequestedUtc`; the coordinator pages those
  records and plans them as a `Keys` selection (replaces the replica replan).
- `replan` is `Full` with force (lifts tier 0 and the completed-submission gate; per-record hashes still decide).
- `recordKeys` resolve to `SourceKeyJson` through the ledger and plan as `Keys`; an unknown key asks for a run first.

### 2.5 Payloads, render context, change detection

- `PayloadLocations.Resolve` joins `locationColumn` to `root` (or checks an absolute value against the roots through
  `PayloadRoots.Refusal`); empty or outside holds the record; listing through `Storage/PayloadFiles.ListAsync`;
  `StoragePayloadSource` replaces `DropPayloadSource`; the resolved location is stored in `PendingPayloadLocation`.
- Render context and snapshot are unchanged; fan-out members render with the submission's recorded cache version.
- Ingestion fingerprint: SHA-256 over the record row's `UpdatedDate_DW` ticks and, per dataset in name order, its child
  row count and maximum `UpdatedDate_DW` ticks. `SourceVersion(Fingerprint, ModifiedUtc = lastModified)`.
- Tier 1 skips only when the fingerprint, the business version (when declared), the render context and the payload hash
  all match; `StaysBlocked` and the stale gate keep their semantics.

### 2.6 Fan-out by key ranges

When `reliability.fanOut > 0`, the dispatcher is available, a run id exists and the estimate is at least
`fanOutMinRecords`: slice bounds on the record table's identity primary key (`source.record.primaryKey`, required
with a fan-out), cut from a count of the candidates per range of key values rather than by ranking them (at most 1024
slices, each at least `batchRecords`, `Source/KeySlices.cs`), stored with the column on `Submission.SourceWindowJson`; members run `Operation = intake` with
`Payload = {"submissionId":"...","slices":[i..j]}`; drains run `Operation = drain` with the submission id.

### 2.7 Connection

`source.connection` resolves on the node (`ISecretResolver`), opened by `Source/IngestionConnection.Open` with
`ApplicationName = "OSDU Delivery/<flow>"`, never logged. The same text is the lineage source server, and the lineage
contributor declares reads of `record.object` and every dataset object so waves order pre, ing, then OSDU.

**Ledger access decision:** Nodes reach the ledger through the `osdu` module database connection, configured on the node as a connection reference. Section 3.1 designs ledger access on that basis; sections 3.2 to 3.6 are the model.

## 3. Ledger model in `OsduDbContext`, schema `osdu`

### 3.1 Ledger access on control plane and nodes

- **Configuration.**
  - Every OSDU Delivery host reads one option: section `Osdu:Database:Connection`, environment variable `SQLFLOW_OSDU_DB`.
  - It holds a `${env:NAME}` or `${keyvault:NAME}` reference, resolved through `ISecretResolver` at first use.
  - A literal secret is refused at startup, with a message naming the option and never echoing the value.
- **One database, two logins.**
  - The `osdu` schema lives in the catalog database. This is required by the CLAUDE.md rule that work committing with the catalog (a run queued with its submission) joins the catalog's transaction.
  - The control plane opens `OsduDbContext` on the catalog's `DbConnection` and enlists in its transaction (`Database.UseTransaction`).
  - Nodes connect with a separate login that can only reach schema `osdu`: `GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::osdu`, nothing on SQLFlow's schemas.
  - The operations guide documents the grant. `sqlflow db status` on a node reports the `osdu` schema version it sees.
- **Composition** (`osdu/src/SqlFlow.Delivery/Engine/DeliveryServices.cs`).
  - `AddDeliveryLedger(Func<IServiceProvider, Func<OsduDbContext>?> contexts)` replaces the `CatalogDbContext` factory.
  - `DeliveryLedgerSource` opens `OsduLedger`, `OsduTemplateStore` and `OsduCacheStore` over `OsduDbContext`.
  - On a node the factory comes from the configured reference; on the control plane it comes from the catalog connection.
  - Without a configuration, a host still validates documents, and every ledger operation fails with: "This host has no osdu database connection (Osdu:Database:Connection or SQLFLOW_OSDU_DB); the delivery ledger, templates and caches are unavailable."
- **Version check.** Both hosts check `[osdu].[SchemaVersion]` at startup through the module database extension point. They refuse to run when migrations are pending, when the database is newer than the code, or when the catalog migration is older than `MinimumCatalogMigration`. The message names the migration or version.
- **Fan-out on nodes.** Fan-out needs the run queue, which lives in SQLFlow's catalog, and nodes have no catalog connection. So `IFanOutDispatcher` on a node is implemented over the fan-out run group extension of the node protocol (`SqlFlow.Dispatch`), not over `CatalogDbContext`.
  - `Catalog/CatalogFanOutDispatcher.cs` becomes the control plane's in-process implementation.
  - A new `Engine/FanOut/NodeFanOutDispatcher.cs` serves nodes.
  - Both call the same extension point, so there is one code path.

### 3.2 Context and migrations

- **Context** (`osdu/src/SqlFlow.Delivery.Data/OsduDbContext.cs`).
  - `OnModelCreating` calls `DeliveryModel.Configure(modelBuilder, sqlServer)` with `DeliveryModel.SchemaName = "osdu"`.
  - Migration history table: `[osdu].[__EFMigrationsHistory]`.
  - Entity class names stay `Delivery*` and move to namespace `SqlFlow.Delivery.Data`. Table names are unchanged apart from the schema.
- **Initial migration.** One clean migration, `Migrations/<timestamp>_InitialOsduSchema.cs`, plus `.Designer.cs` and `OsduDbContextModelSnapshot.cs`. It creates every table below.
  - It also creates the indexed view `[osdu].[RecordCount]` through `migrationBuilder.Sql` with the batches now in `DeliveryModel.IndexedViews`.
  - That replaces the runtime `CatalogIndexedView` creation and removes `IndexedViews` from the model class.
- **SQLite tests.** They keep `EnsureCreated`, and the view is simply absent there, as today.

### 3.3 Entity by entity

#### `DeliverySubmission` (table `osdu.Submission`)

- **Removed columns:**
  - `DropLocation`
  - `ManifestJson`
  - `SourceSequence`, with the unique filtered index `(FlowId, SourceSequence) WHERE SourceSequence > 0`
  - `LoadedUtc`, `SourceRecords`, `LoadedRows`, `LoadedOrdinals`, `Duplicates`
  - `ReplicaInserted`, `ReplicaUpdated`, `ReplicaSchemaJson`
  - `SourcePrunedUtc`
- **Renamed:** `Partitions` becomes `Slices` (int), the key slices the intake was cut into.
- **Changed:** `Kind` nvarchar(16) takes `incremental`, `full`, `keys` or `inline` (it was `drop` or `replan`).
- **Kept:** `SubmissionId`, `FlowId`, `FlowName`, `MappingReference`, `RenderContext`, `ParametersJson`, `Reference`, `RecordCount`, `WorkLocation`, `BatchCount`, `Status`, `ReceivedUtc`, `StartedUtc`, `CompletedUtc`, `Planned`, `SkippedUnchanged`, `AwaitingApproval`, `SkippedStale`, `UnchangedAtPush`, `Blocked`, `Delivered`, `Held`, `Failed`, `Error`, `Untracked`.
- **Added:**
  - `SourceConnection` nvarchar(400) not null: the reference text as declared, never resolved.
  - `SourceObject` nvarchar(400) not null: the record table's three-part name.
  - `WindowFromUtc` datetime2 null and `WindowToUtc` datetime2 null: the `UpdatedDate_DW` window planned. Both null for a full, keys or inline plan without a window. `WindowToUtc` is set for full.
  - `SourceWindowJson` nvarchar(max) null: dataset objects, overlap seconds, scope values, slice boundary tuples, key count, and for keys and inline plans the key digest.
  - `RunId` uniqueidentifier null: the coordinating run.
  - `GroupId` uniqueidentifier null: the chain group, for an inline submission.
- **Indexes:**
  - Kept: `(FlowId, ReceivedUtc)`, `(FlowId, Status)`, `(FlowId, Reference)`.
  - Added: `(FlowId, Kind, ReceivedUtc)` for the flow's submission listing filtered by kind; `RunId` for run page links from a run to the submission it coordinated.

#### `DeliveryInlineSubmission` (table `osdu.InlineSubmission`)

- **Removed:** `DropLocation`, `WrittenUtc`.
- **Kept:** `SubmissionId`, `FlowId`, `FlowName`, `MappingReference`, `Operation`, `Force`, `ParametersJson`, `Reference`, `RecordsJson`, `ContentHash`, `RequestHash`, `RecordCount`, `ChildRowCount`, `ContentBytes`, `ReceivedUtc`, `ReceivedBy`.
- **Added:**
  - `Status` nvarchar(16) not null: `accepted`, `landed`, `queued`, `completed` or `failed`.
  - `LandedUtc` datetime2 null.
  - `GroupId` uniqueidentifier null: the chain run group.
  - `OsduRunId` uniqueidentifier null: the OSDU flow's member run in that group.
  - `Error` nvarchar(4000) null, redacted.
- **Indexes:**
  - Kept: `(FlowId, ReceivedUtc)`.
  - Added: `(Status, ReceivedUtc)` filtered `WHERE [Status] IN ('accepted','landed','queued')`, which the resume service scans; `GroupId`, for matching group state back to the submission.

#### New `DeliverySubmissionLanding` (table `osdu.SubmissionLanding`)

- **Key:** `(SubmissionId, Dataset)`. `Dataset` nvarchar(128) is `record` or a child dataset name.
- **Columns:**
  - `PreFlowName` nvarchar(200) not null
  - `Location` nvarchar(2000) not null: the full file location written.
  - `FileName` nvarchar(800) not null: the value `FileName_DW` will hold.
  - `Format` nvarchar(16) not null: `csv`, `ndjson`, `json` or `parquet`.
  - `RowCount` bigint
  - `Bytes` bigint
  - `ContentHash` char(64) not null
  - `WrittenUtc` datetime2 null
  - `PreRunId` uniqueidentifier null: the pre flow member run that took the file.
- **Index:** unique `FileName`. It resolves a record's origin file to the submission that landed it, which is the traceability link.

#### `DeliveryDropOff` (table `osdu.DropOff`)

Removed entirely: the entity, its configuration and its three indexes. The drop-off area is not part of the new architecture.

#### `DeliveryRecord` (table `osdu.Record`)

- **Removed:** none.
- **Changed meaning:** `SourceFingerprint` nvarchar(200) and `PendingSourceFingerprint` now hold the computed ingestion fingerprint (64 hex characters).
- **Added:**
  - `SourceKeyJson` nvarchar(2000) null: the record's key tuple as a JSON array of strings, in `source.record.key` order, which key-scoped reads use.
  - `SourceFileName` nvarchar(800) null, `SourceRowNumber` bigint null, `SourceUpdatedUtc` datetime2 null: the origin of the version OSDU holds.
  - `PendingSourceFileName` nvarchar(800) null, `PendingSourceRowNumber` bigint null, `PendingSourceUpdatedUtc` datetime2 null: the origin of the queued version.
  - `PlanRequestedUtc` datetime2 null: set when the ledger asks for the record to be planned again.
- **Indexes:**
  - All existing indexes are kept.
  - Added `(FlowId, SourceFileName, SourceRowNumber)`: "which records came from this file" inside a flow, in milliseconds. The 800-character limit keeps the key under 1700 bytes; a longer `FileName_DW` is refused when the source is opened, naming the file and the limit.
  - Added global `SourceFileName`, for the search box across flows.
  - Added `(FlowId, PlanRequestedUtc) WHERE [PlanRequestedUtc] IS NOT NULL`: a filtered index the planner pages each run, so it stays as small as the backlog.

#### `DeliveryAttempt` (table `osdu.Attempt`)

- **Added:** `SourceFileName` nvarchar(800) null, `SourceRowNumber` bigint null, `SourceUpdatedUtc` datetime2 null. The origin the attempt's document was built from, kept append-only because the record's current columns are overwritten by later versions.
- **Indexes:** unchanged. History per record goes through `(DeliveryKey, StartedUtc)`.

#### `DeliverySourceWatermark` (table `osdu.SourceWatermark`)

- **Removed:** `TableName`, `Version`.
- **Key:** changes from `(FlowId, Scope, TableName)` to `(FlowId, Scope)`.
- **Added:** `UpdatedThroughUtc` datetime2 not null (the upper bound of the last completed whole-scope plan), `SubmissionId` uniqueidentifier not null (the submission that wrote it).
- **Kept:** `Scope` nvarchar(400), `ContextHash` nvarchar(64), `RecordedUtc`.

#### Unchanged entities (schema moves to `osdu` only)

- `DeliveryWorkBatch`, `DeliveryActivity`, `DeliveryRetrieval`, `DeliveryMapping`, `DeliveryTemplate`.
- `DeliveryCacheDefinition`, `DeliveryCacheVersion`, `DeliveryCacheItem`, `DeliveryCacheMember`, `DeliveryCacheSet`, `DeliveryCacheSetEntry`, `DeliveryUpdateTag`.
- `DeliveryRecordCount` (view), with the same indexes.
- `DeliveryActivity.Kind` values: `known-state` is no longer written; `submit` and `replan` are added.

### 3.4 New `OsduSchemaVersion` (table `osdu.SchemaVersion`)

- **Columns:**
  - `Id` int, primary key, with check constraint `[Id] = 1`: exactly one row.
  - `ModuleVersion` nvarchar(32) not null: `1.0.0` for the initial migration.
  - `LastMigration` nvarchar(150) not null: the migration id last applied.
  - `AppliedUtc` datetime2 not null.
  - `AppliedBy` nvarchar(200) not null: the host and actor running the migrate.
  - `MinimumCatalogMigration` nvarchar(150) not null: at least the SQLFlow migration that added the kind argument and run group extension points, today `20260915201516_RunKindArgumentsAndRequester`.
- **Written by** the module database migrate step after `Database.MigrateAsync`, in the same connection.
- **Read by** host startup checks and `sqlflow db status`.

### 3.5 `ILedger` changes (`osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs`)

- **Records reshaped:**
  - `SubmissionState`: fields as in 3.3; delete `IsReplan` and `IsLoaded`.
  - `SubmissionKinds`: `Incremental`, `Full`, `Keys`, `Inline`.
  - `RecordState`: add `SourceKeyJson`, `SourceFileName`, `SourceRowNumber`, `SourceUpdatedUtc`, the three pending origin fields, and `PlanRequestedUtc`.
  - `AttemptRecord` and `SkippedRecord`: add the origin fields.
  - `SourceWatermark(Guid FlowId, string Scope, DateTime UpdatedThroughUtc, Guid SubmissionId, DateTime RecordedUtc, string? ContextHash)`.
  - Delete `KnownState`.
- **Methods removed:** `MarkInlineSubmissionWrittenAsync`, `BeginSourceLoadAsync`, `MarkSourcePrunedAsync`, `KnownStateAsync`, `StreamKnownStateAsync`.
- **Methods changed:** `GetWatermarksAsync` and `SetWatermarksAsync` become `GetWatermarkAsync(flowId, scope)` and `SetWatermarkAsync(SourceWatermark)`.
- **Methods added:**
  - `ListPlanRequestedAsync(Guid flowId, DeliveryKey? after, int max)`: pairs of `DeliveryKey` and `SourceKeyJson`, in key order.
  - `MarkInlineStatusAsync(Guid submissionId, string status, Guid? groupId, Guid? osduRunId, string? error)`.
  - `GetLandingsAsync(Guid submissionId)`.
  - `MarkLandingWrittenAsync(Guid submissionId, string dataset, long bytes, DateTime writtenUtc)`.
  - `SetLandingPreRunAsync(Guid submissionId, string dataset, Guid runId)`.
  - `FindLandingByFileAsync(string fileName)`.
- **Behaviour changes:**
  - `ForceRedeliverAsync`, `ReleaseAsync` (for a record with no pending document) and `RollOutTagAsync` stamp `PlanRequestedUtc`.
  - `UpsertPendingAsync`, `MarkSkippedAsync` and `MarkHeldAsync` clear it and write `SourceKeyJson` and the pending origin.
  - `CompleteManyAsync`, on a promoting completion, copies the pending origin to the delivered origin and writes it on the attempt.

### 3.6 Traceability check against CLAUDE.md

- **Source file and row:** `Record.SourceFileName`, `SourceRowNumber`, `SourceUpdatedUtc`, and the same on every `Attempt`. An API-submitted record resolves through `SubmissionLanding.FileName` to its submission.
- **Mapping version and cache version:** `Record.RenderContext` and `PendingRenderContext`.
- **Every attempt with outcome and error:** `Attempt`.
- **OSDU id and version:** `Record.TargetId`, `TargetVersion`.
- **Operator actions (release, redeliver, delete) with who and when:** `Activity` and removal attempts.
- **Statistics** are derived from the ledger through `[osdu].[RecordCount]`, never counted separately.

---

## 4. API-submitted records (removed)

This section designed records sent in an API request: stored in `osdu.InlineSubmission`, written as one landing file
per dataset into a folder the delivery flow declared under `source.submissions`, and delivered by a run group of the
pre, ingestion and delivery flows, with `osdu.SubmissionLanding` linking each landing file to its submission.

It was removed. The control plane resolved a relative landing folder against its own working directory while the node's
pre flow read its own checkout of the repository, so a landed file never reached the flow that should load it; and the
regular flows already cover the case. Records delivered by hand are now files placed where the pre flow's
`source.location` points, a location both the person placing them and the nodes can reach, and a run of the chain (from
the GUI, the API or the schedule) loads and delivers them with the same traceability as any other file.

## 5. File-by-file change list

### 5.1 `osdu/src/SqlFlow.Delivery`

**Model**
- **`Model/FlowDefinition.cs`**
  - `FlowSource` is rewritten per section 1.1. New properties: `Connection`, `Record` (new `FlowSourceTable { Object, Key, Scope }`), `Datasets` (new `FlowSourceDataset { Object, Join, OrderBy, MaxRowsPerRecord }`), `Payloads` (new `FlowPayload { Root, LocationColumn, Pattern, HashColumn, ChunkCountColumn }`), `LastModified`, `SystemColumns` (new `FlowSystemColumns`), `Incremental` (new `FlowIncremental`), `Work`, `Submissions` (new `FlowSubmissions` and `FlowSubmissionDataset`).
  - Deleted from `FlowSource`: `Location`, `Manifest`, `Records`, `Scopes`, the string-template `Payloads`, `Fingerprint`, `ChangeColumn`, `KnownState`, `ManualSubmission`, `ManualSubmissionFileRoots`, `Sql`, `Replica`, `WorkRoot(string, string)`.
  - Deleted types: `FlowScope`, `FlowSqlSource`, `FlowSqlWatermark`, `SqlWatermarkType`, `SqlIsolation`, `FlowReplica`.
  - `CredentialReferences`: drop the `source.sql.connection` and `source.replica.connection` branches (lines 48-56); add `source.connection`.
  - `FlowChange.UseSourceVersions`: doc comment now describes the tier-0 window gate.
  - `ChangeDetection.LastModified`: doc comment reworded to name the payload files under the location column.

**Documents**
- **`Documents/YamlModels.cs`**
  - `FlowSourceYaml` (lines 55-82) rewritten.
  - Deleted: `FlowReplicaYaml`, `FlowReplicaColumnYaml`, `FlowSqlYaml`, `FlowSqlWatermarkYaml`, `FlowScopeYaml`.
  - Added: `FlowSourceTableYaml`, `FlowSourceDatasetYaml`, `FlowPayloadYaml`, `FlowSystemColumnsYaml`, `FlowIncrementalYaml`, `FlowSubmissionsYaml`, `FlowSubmissionDatasetYaml`.
- **`Documents/DeliveryDocumentLoader.cs`**
  - `FlowMapper.Map`: source mapping (lines 230-255) rewritten to `MapSource`.
  - Deleted: `MapSql` (302-331), `ValidateSql` (338-420), `MapReplica` (425-468), `ValidateReplica` (474-571), the source.sql and replica calls in `Validate` (575-583), the fingerprint and lastModified refusal (587-591), the `source.work` token check tied to drops (746-752, replaced), the payload template checks (754-766), the `source.location` token check (768-774), the `source.knownState` token check (776-782), the manual submission root checks (653-666, replaced by submissions checks).
  - Added: `ValidateSource`, covering every rule of section 1.1.
- **`Documents/DeliveryFlowKind.cs`**
  - `DeliveryFlowDocument` derives from `RegisteredFlowDocument` (stage 2 alignment).
  - `SourceConnectionReference => Flow.Source.Connection`; `SourceReference => Flow.Source.Record.Object`; `TargetReference => Flow.Target.Endpoint`.
  - Declared reads of `Record.Object` and each `Datasets[*].Object` on `Connection`, for the lineage contributor.
  - `DeliveryFlowKind.Description`: "deliver records from ingestion tables into OSDU".
  - `Operations`: deliver, plan, intake, drain, verify, replan, each a `FlowKindOperation` with `WritesTarget` set for deliver, drain and replan.
  - `ValidateParameters` parses and validates `DeliveryRunPayload` and refuses built-in parameters (`FullLoad`, backfill, `FilePattern`, `SourceFilter`).
- **New `Documents/DeliveryLineage.cs`:** builds the declared object reads from a `FlowDefinition`, used by `DeliveryFlowDocument`.
- **`Documents/MappingCatalog.cs` (`FlowParameters`)**
  - Delete `DropLocation` (133-137) and `KnownStateLocation` (147-151).
  - `WorkLocation(flow, values)` substitutes `Source.Work` and resolves it against the flow file.
  - Add `ScopeValues(flow, values)`, returning the `record.scope` bindings.
  - `Resolve` error text: "Supply it with --set or the run's values" (the manifest no longer exists).

**Submissions (moved from Drops)**
- **`Drops/InlineRecords.cs`** moves to **`Submissions/InlineRecords.cs`**, namespace `SqlFlow.Delivery.Submissions`.
  - `DropManifest.RootScope` (lines 178, 238, 240, 582) becomes `SourceDatasets.Record`.
  - The `DropReader.DeliveryKeyColumn` reservation and UUID check (324-335) are removed; `ReadRow`'s `allowDeliveryKey` parameter is removed.
  - Messages saying "deliver the set as a drop" (175, 271, 288) become "split it into several submissions, or land the files for the pre-ingestion flow".
- **Delete the `Drops/` folder.**
- **New `Submissions/SubmissionLanding.cs`:** `Refusal(flow, preFlows)`, `FilesRefusal(flow, values, records)`, `PayloadContract(flow, values)`, `Plan(submission, flow, mapping, preFlows)` returning the per-dataset `LandingFile` list (name, location, format, columns, rows, hash).
- **New `Submissions/LandingFileWriter.cs`:** CSV, NDJSON, JSON and Parquet writers, temporary name then promote, existing-file hash check.

**Source (new folder)**
- `Source/IIngestionSource.cs`: `IIngestionSourceFactory`, `IIngestionSource`, `SourceSelection`, `SourceWindow`, `SourceHeader`, `KeyTuple`, `KeyRange`.
- `Source/SqlServerIngestionSource.cs`: keyset pages, window, keys via `OPENJSON`, child result sets, isolation, deadlock retry, value normalization, fingerprint.
- `Source/IngestionSql.cs`: SQL text builders with bracket quoting; unit tested.
- `Source/SourceObjectName.cs`: three-part name parse and quoting.
- `Source/IngestionConnection.cs`: reference hygiene check (from `SqlSourceConnection.CheckDeclared`), connection opening, `ApplicationName`.
- `Source/IngestionFingerprint.cs`
- `Source/KeySlices.cs`: slice count and length rule, `Shares`.
- `Source/SourceBindings.cs`: flow bindings against table columns: `lastModified`, system columns, payload columns, join columns, key equals mapping key, key column types. Replaces `Planner.CheckFlowBindings`.
- `Source/SourceDatasets.cs`: `Record = "record"`.

**Engine**
- **`Engine/FlowRuntime.cs`**
  - `EngineContext`: remove `Drops`, `SqlSources`, `SqlConnector`; add `IIngestionSourceFactory Sources` and `IPayloadFiles Payloads`.
  - `FlowRuntime` removals: `_drop`, `DropLocation`, `HasDrop`, `Replica`, `ReplanAsync` (191-229), `WriteInlineDropAsync` (236-256), `ExtractSqlDropAsync` (263-266), `Publisher` (286), `PublishKnownStateAsync` (499-504), `IntakeWithFanOutAsync` drop branch (636-699), `ReplicaIntakeWithFanOutAsync` (707-764), `Split` (866-877).
  - `CreateAsync(context, flow, values, ct)` without a drop override; `CreateAsync(context, flowPath, values, ct)`.
  - New `Selection` property.
  - `Planner` property builds `Planner(source, context.Payloads, ledger, logger)` from `Sources.Open`.
  - `IntakeWithFanOutAsync` rewritten per section 2.6, with member `RunParameters { Operation = "intake", Values, Payload = DeliveryRunPayload.ToJson() }`.
  - `DrainWithFanOutAsync` members use `Payload`.
  - `TrackAsync` activity parameters: `source` (the record object) and `selection` instead of `drop`.
  - `RequireLedger` message names the osdu database connection.
- **`Engine/DeliveryExecutor.cs`**
  - `CanExecute(RegisteredFlowDocument)`; `ExecuteAsync(RegisteredFlowDocument, ...)`.
  - `DeliveryRunPayload.Parse(options.Parameters)`; `RunParameters.Validate()` is kept.
  - Deleted: the inline drop, replica, replan and SQL extraction branches (116-209), the known-state case (258-265), the replica planning branch of `PlanAsync` (300-322), and the manifest record count issue (340-343).
  - New case `replan`: `Full` selection with force.
  - The inline submission path loads `InlineSubmissionState` and sets `Selection = Inline`.
  - `ForcesReplan(DeliveryRunPayload payload, bool reRunningSubmission)`; `RedeliverScopeOf(DeliveryRunPayload)`.
  - Records: `DeliverOutcome.Drop`, `PlanOutcome.Drop` and `IntakeOutcome.Drop` become `Source`; `PlanOutcome.Partitions` becomes `Slices`; `IntakeOutcome.Partitions` becomes `Slices`. Delete `KnownStateOutcome`.
  - `LogSubmission`: "working on submission {id} of {source}".
- **New `Engine/DeliveryRunPayload.cs`:** `Force`, `SubmissionId`, `RecordKeys` (at most 1000), `Redeliver` (`all`, `metadata`, `payload`), `Slices`, `Reland`; `Parse`, `Validate`, `ToJson`; plus `DeliveryOperations` constants `Deliver`, `Plan`, `Intake`, `Drain`, `Verify`, `Replan`, replacing the old platform `RunParameters.*Operation` constants used throughout.
- **`Engine/Planning/Planner.cs`**
  - `PlanInput` wraps `SourceRecord` only: delete `FromReplica`, `Origin`, `ReplicaKey`, `FromDrop`.
  - `PlanHeader`: `Drop`, `Origins`, `Partitions` become `SourceHeader Source` and `int Slices`.
  - `DeliveryPlan.Drop` is removed.
  - Constructor `Planner(IIngestionSource source, IPayloadFiles payloads, ILedger? ledger, ILogger<Planner> logger)`.
  - `OpenAsync(flow, resolved, values, SourceSelection selection, bool gate, ct)`: opens the source, runs `Preflight.Check` with table columns and `SourceBindings.Check`, applies tier 0 (section 2.4).
  - Deleted: `OpenStoredAsync` (299-344), `CheckOrigin` (352-366), `OpenDropAsync` (368-424), `DropInputsAsync` (442-450), `DropOfAsync` (613-622), `RecordCountIssue` (591-595), `CheckManifestAgainstFlow` (924-944), `CheckFlowBindings` (946-1006).
  - `EntriesAsync(header, KeyRange? range, ...)` reads `source.ReadAsync`.
  - `PlanAsync(flow, resolved, values, selection, force, ct)`.
  - `PayloadName(flow)` uses `flow.Source.Payloads`.
  - `PlanBatchAsync`: the payload comes from `flow.Source.Payloads[payloadName]` instead of `drop.Manifest.Payloads`; locations from `PayloadLocations.Resolve`; listings from `IPayloadFiles`; `ReadSourceVersion` combines the fingerprint with `lastModified`.
  - `DeclaredChunkCount(FlowPayload, SourceRow)`.
  - `PlanEntry` gains `Origin`, `SourceKeyJson`, `Existing` (kept).
- **`Engine/Intake/SubmissionIntake.cs`**
  - Deleted: `Replica` property, `IntakeSource`, `PreparedIntake.Summary`, `IntakeSlicesAsync` (247-269), `SliceInputsAsync` (272-296), `IntakeDropAsync` (299-378), `DropManifestSummary` (762-772).
  - New `IntakeRequest(SourceSelection Selection, Guid? SubmissionId, IReadOnlyList<int>? Slices)`.
  - `PrepareAsync(flow, resolved, values, IntakeRequest, force, ct)`: registers the submission (`Kind`, `SourceConnection`, `SourceObject`, window, `SourceWindowJson`, `RunId`), or reopens it for a member.
  - `PlanSlicesAsync(flow, prepared, slices, ct)`.
  - `IntakeAsync(flow, resolved, values, IntakeRequest, force, ct)`.
  - `FinalizePlanningAsync(flow, submissionId, values, counts, ct)`: writes the single watermark only for incremental and full kinds.
  - `Skipped`, `HeldState` and `PendingState` carry `SourceKeyJson` and the pending origin.
  - Stale reason (596): "while this run was planning".
- **`Engine/Worker/DeliveryWorker.cs`**
  - Constructor takes `IPayloadFiles` instead of `IDropReader` (54, 75, 86).
  - `DropPayloadSource` (947-966) is deleted.
  - `DeliveryWork.Payload = new StoragePayloadSource(payloads, location)` (466).
  - The held reason at 428, "re-submit the drop", becomes "no pending document on the record; release or redeliver it to plan it again".
  - `Settle` writes the pending origin onto the attempt.
- **`Engine/DeliveryServices.cs`**
  - Remove the `IDropReader` registration (49) and `using Drops`.
  - Remove `ISignedUploadIssuer` and `AzureBlobSignedUploadIssuer` (45) once the check confirms only the drop-off area used them.
  - Register `IIngestionSourceFactory` (`SqlServerIngestionSourceFactory`) and `IPayloadFiles`.
  - `EngineContext` construction updated.
  - `AddDeliveryLedger(Func<IServiceProvider, Func<OsduDbContext>?>)` per section 3.1.
  - `DeliveryLedgerSource` over `OsduDbContext` with `OsduLedger`, `OsduTemplateStore`, `OsduCacheStore`.
  - `IFanOutDispatcher` registration chooses the control plane or node implementation.
- **New `Engine/FanOut/NodeFanOutDispatcher.cs`:** over the node protocol run group extension.
- **`Catalog/CatalogFanOutDispatcher.cs`:** unchanged logic; control plane only.
- **`Engine/CacheExecutor.cs`** (line 110) and **`Engine/RetrievalExecutor.cs`** (line 113): replace the checks on `SubmissionId`, `RecordKeys`, `Partitions` and `Drop` with a refusal of `parameters.HasKindArguments` beyond their own operations; `CanExecute(RegisteredFlowDocument)`.
- **`Engine/RunLogLoggerFactory.cs`** (line 83): remove the `KnownStatePublisher` category; add `SqlServerIngestionSource` mapped to `source`.
- **`Engine/Protocols/OsduWellLogProtocol.cs`** (lines 62, 301, 408, 437, 568) and **`Engine/Protocols/FileUploads.cs`** (lines 5, 47, 74): `Drops.PayloadChunk` becomes `PayloadFile`; remove `using SqlFlow.Delivery.Drops`.
- **New `Engine/Operations/ReadSourceRowOperation.cs`:** an `IComputeOperation` that reads one record's ingestion rows by key on a node. It serves the record page's source view and replaces the replica view.

**Ledger**
- **`Ledger/ILedger.cs`:** per section 3.5.
- **`Ledger/CatalogLedger.cs`** becomes **`Ledger/OsduLedger.cs`** over `Func<OsduDbContext>`.
  - `RegisterSubmissionAsync` (line 84 `DropLocation`) writes the new submission fields.
  - Entity mapping (2281-2356) updated for every field in section 3.3.
  - Deleted: `KnownStateAsync` and `StreamKnownStateAsync` (1469-1510), `BeginSourceLoadAsync`, `MarkSourcePrunedAsync`.
  - Watermarks (1929-1950) become `GetWatermarkAsync` and `SetWatermarkAsync` on the `(FlowId, Scope)` key.
  - `ForceRedeliverAsync`, `ReleaseAsync` and `RollOutTagAsync` stamp `PlanRequestedUtc`.
  - `UpsertPendingAsync`, `MarkSkippedAsync` and `MarkHeldAsync` clear it and write `SourceKeyJson` and the pending origin.
  - `CompleteManyAsync` promotes the pending origin and writes it on the attempt.
  - New: `ListPlanRequestedAsync`, `MarkInlineStatusAsync`, `GetLandingsAsync`, `MarkLandingWrittenAsync`, `SetLandingPreRunAsync`, `FindLandingByFileAsync`.
  - `LookupAsync` and `CountLookupAsync` add the `SourceFileName` prefix search.
- **`Ledger/SqlServerLedgerBulk.cs`:** bulk column lists and `MERGE` statements gain `SourceKeyJson`, the six origin columns and `PlanRequestedUtc`; the attempt bulk insert gains the three origin columns; table names under `[osdu]`.
- **`Ledger/InlineSubmissionState.cs`:**
  - Remove `using SqlFlow.Catalog` and `using SqlFlow.Delivery.Drops`, and the `DropLocation` and `WrittenUtc` fields.
  - Add `Status`, `LandedUtc`, `GroupId`, `OsduRunId`, `Error`.
  - `Operations` uses `DeliveryOperations`.
  - `InlineSubmissionRows.ToEntity` and `ToState` map the new fields.
- **`Catalog/CatalogCacheStore.cs`, `Catalog/CacheVersions.cs`, `Catalog/CatalogCacheReader.cs`, `Catalog/DeliveryCatalogSync.cs`, `Templates/TemplateStore.cs`:** `CatalogDbContext` becomes `OsduDbContext` for delivery tables. `DeliveryCatalogSync` keeps joining the catalog sync transaction through the catalog sync extension point (stage 2); no other stage 4 change.

**Planning, protocols, rendering, storage, validation**
- **`Planning/SourceVersion.cs`:** remove `using Drops`; `PayloadFiles.Of(IReadOnlyList<PayloadFile>)` (line 78); doc comment "a source row" instead of "a drop row". The ingestion fingerprint fills `SourceVersion.Fingerprint`.
- **`Planning/ChangeDetector.cs`:** `CanSkipWithoutRender` (71-98) requires both source parts per section 2.5; `StaysBlocked` (211-222) doc and fingerprint fallback; doc comments say "the ingestion row" instead of "the drop".
- **`Protocols/IDeliveryProtocol.cs`:** remove `using Drops`; add `public sealed record PayloadFile(int Index, string Path, long Size, DateTimeOffset? Modified = null)`; `IPayloadSource.ListChunksAsync` returns `IReadOnlyList<PayloadFile>`, and `OpenAsync(PayloadFile file, ...)`.
- **`Rendering/SourceRow.cs`:** `SourceOrigin(string? FileName, long? RowNumber, DateTime? UpdatedUtc)`; `SourceRecord.Version` added; `SourceRecord.DeclaredDeliveryKey` removed; doc comments name the ingestion row.
- **`Rendering/MappingRenderer.cs`:** remove the declared delivery key hold (183-186).
- **`Storage/ParquetScopeReader.cs`** becomes **`Storage/ParquetFiles.cs`**: keep `ReadShapeAsync`, `EnsureSeekableAsync`, `WriteAsync`, `Normalize`, `MakeNullable` and the writer class (398-412). Remove scope-row reading (`ReadRowsAsync`, `ReadColumnsAsync`) once no caller remains.
- **`Storage/FileStore.cs`:** line 143 calls `ParquetFiles.EnsureSeekableAsync`; `CreateUploadAsync` (120) is removed with the drop-off signed upload issuer, after the check.
- **`Storage/SignedUpload.cs`:** deleted after the check confirms only the drop-off area used it.
- **New `Storage/PayloadFiles.cs`:** `IPayloadFiles { ListAsync(location, pattern), OpenAsync(PayloadFile) }` and its `FileStoreRegistry` implementation.
- **New `Storage/StoragePayloadSource.cs`.**
- **`Model/WellboreDdmsSessionChunks.cs`** (line 16): cref to `ParquetFiles.ReadShapeAsync`.
- **`Validation/PayloadRoots.cs`:** delete `DropOffEnvironmentVariable`, `DropOffRoot`, `DropRoot` (20-26, 45-49, 105-126). `Of(flow, values)` returns each payload `root` resolved and substituted, plus `submissions.fileRoots`. `Refusal(flow, values, location)`.
- **New `Validation/PayloadLocations.cs`:** `Resolve(flow, values, row, payloadName)`.
- **`Validation/Preflight.cs`:** remove `using Drops`; parameter `dropColumns` becomes `sourceColumns`; `DropManifest.RootScope` (394) becomes `SourceDatasets.Record`; messages at 398-403 name the ingestion table instead of "the drop".
- **`Validation/MappingColumns.cs`:** doc comment at `MappingSourceColumns` names the landing file columns instead of "an inline submission's drop".

### 5.2 `osdu/src/SqlFlow.Delivery.Data`

- **`DeliveryEntities.cs`:**
  - Namespace becomes `SqlFlow.Delivery.Data`.
  - Entity changes per section 3.3, including deleting `DeliveryDropOff` (181-227, 951-967) and adding `DeliverySubmissionLanding`.
  - `DeliveryModel.SchemaName = "osdu"`; `IndexedViews` moves into the migration.
  - `Configure` updated with every index in section 3.3.
- **New `OsduDbContext.cs`:** the `DbSet`s currently on `CatalogDbContext` for delivery tables, plus `SubmissionLandings` and `SchemaVersions`; migration history table `[osdu].[__EFMigrationsHistory]`.
- **New `OsduSchemaVersion.cs`:** entity and `OsduSchema.Current` (module version `1.0.0`, minimum catalog migration).
- **New `Migrations/<timestamp>_InitialOsduSchema.cs`**, **`.Designer.cs`**, **`OsduDbContextModelSnapshot.cs`**.
- **New `OsduDesignTimeFactory.cs`:** `IDesignTimeDbContextFactory<OsduDbContext>` for `dotnet ef`.

### 5.3 `osdu/src/SqlFlow.Delivery.ControlPlane`

- **`Api/DeliveryEndpoints.cs`**
  - Line 13: `using SqlFlow.Delivery.Drops` becomes `using SqlFlow.Delivery.Submissions`.
  - Submission DTO (35-40): remove `DropLocation`, `Partitions`, `Kind = SubmissionKinds.Drop` default, `LoadedUtc`, `SourceRecords`, `LoadedRows`, `Duplicates`, `ReplicaInserted`, `ReplicaUpdated`, `ReplicaSchema`, `SourcePrunedUtc`; add `SourceConnection`, `SourceObject`, `WindowFromUtc`, `WindowToUtc`, `Slices`, `RunId`, `GroupId`.
  - `DeliverySubmissionRequest` (189): remove `Drop`; add `bool Reland`.
  - `DeliverySubmissionAccepted` (193-196): add `GroupId`.
  - `DeliveryInlineSubmissionDto` (199-201): `DropLocation` and `WrittenUtc` become `Status`, `LandedUtc`, `GroupId`, `OsduRunId`, `Error`, `IReadOnlyList<DeliverySubmissionLandingDto> Landings`.
  - Route 342 `/records/{key}/replica` and `GetRecordReplicaAsync` (1472-1500): replaced by `/records/{key}/source`, which runs `ReadSourceRowOperation` on a node through the compute operation path and returns rows with their system columns.
  - Route 365 name `SubmitDeliveryDrop` becomes `SubmitDeliveryRecords`.
  - `SubmitAsync` (1024-1090): the drop branch (1029-1036, 1044-1052, 1070-1089) is removed; records are required.
  - `SubmitRecordsAsync` (1097-1187): section 4.2 steps 1 to 4; `InlineDrop.Refusal` and `FilesRefusal` become `SubmissionLanding.Refusal` and `FilesRefusal`.
  - `InlineRunParameters` (1189-1195): deleted; chain member parameters are built in `SubmissionChain`.
  - Manual flows listing (1245-1258) and source contract (1323-1336): `InlineDrop.*` becomes `SubmissionLanding.*`; the contract's `definition.Source.Fingerprint` becomes `SourceObject` and `UpdatedColumn`; `payload.Roots` from `PayloadRoots.Of`.
  - `GetSubmissionContentAsync` (1359-1381): returns landings and status.
  - Redeliver (1437-1466): the replica branch is removed; `RunParameters { Operation = "deliver", Values = SubmissionValues(last?.ParametersJson), Payload = {"recordKeys":[key],"redeliver":scope} }`.
  - Verify (1535): `RunParameters { Operation = "verify", Payload = {"recordKeys":[key],"force":true} }`.
  - Submission mapping (1946-1950): new fields.
  - `EnqueueRunAsync`: takes `OsduDbContext` companion rows enlisted on the catalog transaction; new `EnqueueChainAsync` through the group enqueue extension.
  - Every `CatalogDbContext` query of delivery tables (`db.DeliveryMappings`, `db.DeliveryTemplates`) becomes `OsduDbContext`; catalog queries (`db.Runs`, `db.Pipelines`) stay on `CatalogDbContext`.
- **New `Api/SubmissionChain.cs`:** member set from `CatalogFlowDependency` and `CatalogPipeline.Wave`, member parameters, lineage reachability refusal.
- **New `Background/SubmissionLandingService.cs`:** section 4.2 step 6.
- **`Api/DeliveryTemplateEndpoints.cs`, `Background/CacheUpdateRolloutService.cs`, `Background/DataDefinitionsWarmupService.cs`:** `OsduDbContext` for delivery tables; no stage 4 behaviour change.

### 5.4 `osdu/src/SqlFlow.Delivery.Cli`

- **`DeliveryVerbs.cs`** (lines 36-105)
  - `check` no longer probes a drop (`engine.Drops.OpenAsync`, 69-79) or takes a drop override.
  - Without `--connect` it validates the flow, mappings, templates and payload roots.
  - With `--connect` it opens the source on this machine through the connection reference and reports tables, columns, key types, system columns, the current watermark window and candidate counts.
  - Output lines 102-105 become `source  <connection reference> <record object> (+N datasets)`.
  - The report dictionary key `drop` (47) becomes `source`.

### 5.5 `osdu/tests` up to `FileProtocolTests`

**`SqlFlow.Delivery.Tests/TestSupport.cs`**
- `SqliteCatalog` becomes `SqliteOsduLedger` over `OsduDbContext` (`EnsureCreated`): `Ledger()`, `Templates()`, `Caches()`, `CreateDbContext()`.
- `Samples.Engine(...)` (438-455): `new DropReader(stores)` (448) is removed; parameters `IIngestionSourceFactory? sources = null` (default a new `MemoryIngestionTables`) and `IPayloadFiles? payloads = null`.
- `Samples.LocalFlow(string dropLocation)` (458-474) becomes `LocalFlow(string connectionReference = MemoryIngestionTables.Reference)`, which replaces `Source.Connection` and keeps the partition header, reliability overrides and local target.
- `using SqlFlow.Delivery.Drops` removed.

**New fixtures (`SqlFlow.Delivery.Tests/Fixtures/`)**
- **`SampleWellLogs.cs`** replaces `SampleDropBuilder`:
  - `SampleRecord`, `SampleCurve`, `DefaultRecords(logSource)`, `IndexCurveId`, `HashGrid`, `WriteChunkAsync(root, record)`;
  - `WellLogRow(record, payloadRoot)` and `CurveRows(record)` returning ingestion rows with business columns;
  - `WriteCsvFilesAsync(folder, records)` for the chain suite.
- **`MemoryIngestionTables.cs`:**
  - An `IIngestionSourceFactory` over in-memory tables keyed by three-part name, with reference `mem://ingestion`.
  - `Upsert(object, row, fileName, rowNumber)` stamps `UpdatedDate_DW` from a `TestClock` and sets `FileName_DW`, `RowNumber_DW`, `InsertedDate_DW`. It leaves a row with unchanged business columns untouched, mirroring the ingestion checksum's exclusion of `_DW` columns.
  - `Delete(object, key)`.
  - Window, keys, scope, child join, ordering and slice semantics match `SqlServerIngestionSource`: ordinal comparison for strings, numeric for integers.
- **`SqlServerIngestionFixture.cs`:**
  - Creates schemas `pre_<n>` and `ing_<n>` in `SQLFLOW_TEST_DB`, and tables in the shape an ing flow creates: business columns, `InsertedDate_DW`, `UpdatedDate_DW` as `datetime`, `FileName_DW`, `RowNumber_DW`, `NCI_UpdatedDate_DW`, key index.
  - Bulk loads rows.
  - Generates the sample pre, ing and OSDU flows with the test database name and runs them through `DocumentExecutor`.
  - Drops its schemas on dispose.

**New `SqlFlow.Delivery.Tests/IngestionSourceContract.cs`:** an abstract suite with the cases listed in section 6. Inherited by `MemoryIngestionSourceTests` and `SqlServerIngestionSourceTests`.

**`SqlFlow.Delivery.Tests/EngineTests.cs`**
- `using SqlFlow.Delivery.SampleDrop` (21) is removed; `DropAsync` (lines 36-60) becomes `TablesAsync(records)`, loading `MemoryIngestionTables`.
- **`EndToEndTests`** re-fixtured on ingestion tables:
  - `Plan_without_a_ledger_creates_everything` (70): unchanged intent.
  - `Tier0_plans_the_scope_again_when_the_cache_moved_under_an_unchanged_source` (85) becomes `Tier0_skips_a_window_without_changed_rows_and_logs_a_moved_context`.
  - `Run_delivers_then_skips_unchanged_then_updates_only_what_changed` (120): rows upserted between runs; `plan.Drop.Manifest.SubmissionId` (197, 216) becomes `run.Submission.SubmissionId`.
  - `Unresolvable_reference_holds_the_record_and_release_requeues_it` (225): unchanged intent.
  - `A_record_released_with_its_rendered_document_is_sent_by_the_next_run_of_its_drop_though_that_run_plans_nothing` (265) becomes `..._by_the_next_run_of_the_flow_though_that_run_plans_nothing`.
  - `A_record_released_from_a_settled_submission_...` (302), `A_run_recovered_after_its_worker_stopped_...` (336), `A_record_the_service_asked_to_wait_on_...` (374), `Transient_failures_back_off_...` (399), `Stopping_the_worker_releases_...` (448), `Verify_detects_drift_and_reconcile_queues_redelivery` (491): unchanged intent, new fixture.
  - `Known_state_is_published_as_parquet` (521): deleted.
  - `An_incremental_drop_reprocesses_rows_modified_since_and_never_goes_back_to_an_older_one` (538) becomes `An_incremental_run_reads_rows_updated_since_the_watermark_and_never_sends_an_older_business_version`.
  - `A_newer_version_queued_behind_an_in_flight_delivery_...` (598): unchanged intent.
  - `Payload_chunk_files_are_the_watermark_when_the_flow_takes_their_modified_times` (664): payload location column and root instead of `payloadHash: false` drops (674).
  - `A_redelivery_scoped_to_one_record_sends_only_that_part_of_it_after_the_drop_was_delivered` (742) becomes `..._after_the_flow_delivered_it`, as a key-scoped run.
  - `Each_attempt_names_the_correlation_id_its_requests_carried` (774): unchanged intent.
  - `Drop_prepared_for_another_mapping_or_flow_is_refused` (794) becomes `A_table_missing_a_column_the_mapping_reads_is_refused_before_anything_is_planned`.
  - `Records_a_cache_change_holds_back_are_counted_as_awaiting_approval_not_as_unchanged` (818): unchanged intent.
- **New `EndToEndTests` cases:**
  - `A_curve_change_replans_its_log_only`
  - `Records_marked_by_a_rollout_are_planned_by_the_next_incremental_run`
  - `Each_delivered_record_and_attempt_keeps_the_origin_row`
  - `A_failed_plan_leaves_the_watermark_where_it_was`
  - `A_payload_location_outside_the_roots_holds_the_record`
- **`ProtocolTests`** (863-1520): `NotParquetPayload` (1424) and `MemoryPayload` (1454) implement `IPayloadSource` with `PayloadFile` (1426-1460); `ParquetScopeReader.WriteAsync` and `PandasMetadataKey` (1512, 1516) become `ParquetFiles`.
- **`DeliverRunScopeTests`** (1521-1540): `ForcesReplan` with `DeliveryRunPayload { RecordKeys }`, `{ Force = true }`, and the re-running flag.
- **`DeliverOutcomeTests`** (1543-1594): field `Drop` becomes `Source`.
- **`DeliveryRunBoundaryTests`** (1596-1629): `SampleDropBuilder.WriteAsync` (1611) becomes `SampleWellLogs` rows in `MemoryIngestionTables`; `DropLocation = "drops/STAT_COMP"` (1552) is removed.

**`SqlFlow.Delivery.Tests/ScaleEngineTests.cs`**
- `using SqlFlow.Delivery.SampleDrop` (10) removed; `SampleDropBuilder.WriteAsync(..., partitions)` (29) becomes generated rows in `MemoryIngestionTables`.
- Fan-out tests (39, 92, 138) use key slices.
- The member runner (217-220): `FlowRuntime.CreateAsync(Engine, _flow, member.Values, ct)`, with slices from `DeliveryRunPayload.Parse(member).Slices` and `IntakeOutcome.From(intake, source, slices)`.
- New `A_fan_out_across_key_slices_plans_every_record_exactly_once_with_disjoint_batch_numbers`.

**`SqlFlow.Delivery.Tests/RemovalTests.cs`**
- Lines 510-516: `SampleDrop.SampleDropBuilder.WriteAsync` and `runtime.DropLocation` become `SampleWellLogs` rows and `runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, new IntakeRequest(SourceSelection.Full, null, null), force: false)`.

**`SqlFlow.Delivery.Tests/CoreTests.cs`**
- Line 1068: `Drops.PayloadChunk[]` becomes `PayloadFile[]` (the `PayloadFiles.Of` tests).
- `DropManifestTests` (1087-1120) deleted.
- New `IngestionFingerprintTests`: equal for identical rows; changes with the record's `UpdatedDate_DW`, a child's maximum `UpdatedDate_DW`, or a child count; independent of child row order.
- New `ChangeDetectorTests` cases for the two-part tier-1 condition.

**`SqlFlow.Delivery.Tests/DocumentsTests.cs`**
- The `Flow` fixture (13-33) uses the new `source:` shape: `connection`, `record`, `datasets.curves`, `payloads.curves`, `lastModified`, `work`.
- `Manual_submission_is_opt_in_and_bounded_by_where_its_files_may_sit` (35-80) becomes `Submissions_are_opt_in_and_bounded_by_the_payload_roots`.
- Assertions at 131 (`Source.KnownState`) and 294-297 (`FlowParameters.DropLocation`, `KnownStateLocation`) deleted.
- New validation tests, one per rule in section 1.1, each asserting the exact message:
  - a two-part object name;
  - a join missing a key column;
  - a dataset named `record`;
  - a scope naming an undeclared parameter;
  - a streaming protocol without `locationColumn`;
  - `hashColumn` missing under `contentHash`;
  - a literal secret in `connection`;
  - a submissions dataset not in `datasets`;
  - `fileRoots` with a wildcard.
- New `Removed_source_keys_are_refused_by_name`: `location`, `manifest`, `sql`, `replica`, `knownState`, `fingerprint` are refused as unknown keys.
- New `The_sample_flows_parse`: all sample OSDU flows through `DeliveryDocumentLoader`; all pre and ing flows through `YamlFlowLoader` and `YamlIngestionFlowLoader`.

**`SqlFlow.Delivery.Tests/StorageTests.cs`**
- `using SqlFlow.Delivery.SampleDrop` (7) removed.
- Parquet tests (62-64, 121-151, 175-180) use `ParquetFiles`; tests that only exercised scope-row reading are deleted along with `ReadRowsAsync` and `ReadColumnsAsync`.
- `DropReader.CompareValues` tests (158-160) deleted.
- `DropReaderTests` (215-260) deleted.
- `HashGrid` tests (268-272) move to `SampleWellLogsTests` in the fixtures folder.
- New `PayloadFilesTests`: listing honours the pattern and orders by name; `StoragePayloadSource` re-opens a fresh stream per call.
- New `PayloadLocationsTests`: relative value joined to the root; absolute value under root or `fileRoots` accepted; `..` refused; outside the roots refused with the roots named; tokens substituted.

**`SqlFlow.Delivery.Tests/SubmissionReferenceTests.cs`**
- The `DropManifest.Parse` reference round trip (92-96) and the `Manifest(reference)` helper (103-120) are deleted.
- Replaced by `The_reference_travels_onto_the_inline_submission_and_its_submission`: accept with a reference, run the inline plan against `MemoryIngestionTables`, and assert `SubmissionState.Reference` and `ListSubmissionsAsync(reference)`.

**`SqlFlow.Delivery.Tests/FileProtocolTests.cs`**
- Lines 660-663: the fake payload source returns `PayloadFile` (`new PayloadFile(i, $"mem://files/curve_{i}.parquet", 7)`), and `OpenAsync(PayloadFile file, ...)`.
- **`CacheChangeTests.cs:67`** and **`LedgerTests.cs:44`**: remove `DropLocation`.
- **`LedgerTests.cs:468-473`**: replace the table watermarks and `KnownState` with tests for the single watermark, `PlanRequestedUtc` and the origin columns.
- **`InlineRecordsTests.cs`**: new namespace; a column named `deliveryKey` is now an ordinary column.
- **`SqlServerLedgerTests.cs`**: provision by migrations, `[osdu].[RecordCount]` (line 245), new index assertions, `SchemaVersion` row.
- **`WellboreEstate.cs`**: `Flow(...)` emits `connection`, `record`, `datasets`, `payloads.root` and `submissions`; `Lake` becomes the payload root.
- **New:** `SubmissionLandingTests.cs`, `SqlServerChainTests.cs`, `SampleEstateTests.cs`, `ArchitectureTests.cs`.
- **`SqlFlow.Delivery.ControlPlane.Tests/DeliverySubmissionApiTests.cs`**: line 118 (`stored.DropLocation`) becomes landing assertions; remove line 899; add tests for chain enqueue, replay, conflict and resume.
- **`SampleEstate.cs`**: copy `data/`.

---

## 6. Test plan

### Fast, in memory (no database)

- **`IngestionSourceContract` over `MemoryIngestionTables`**
  - Keyset pages never repeat or skip a key across page boundaries, including many rows sharing one `UpdatedDate_DW`.
  - The window is `(lower, upper]`.
  - A child change brings in its parent.
  - The scope predicate filters.
  - A Keys selection reports missing keys.
  - Slice bounds cover every candidate exactly once.
  - The fingerprint changes on a child count or timestamp change and nothing else.
- **`EngineTests`, `ScaleEngineTests`, `RemovalTests`**: the existing assertions (skip unchanged, update only what changed, holds, release, backoff, stop without charging an attempt, verify, stale versions, queued-behind-in-flight, payload watermark, correlation id, approval gate), now fed by table rows.
  - New: every ledger record and attempt carries `FileName_DW`, `RowNumber_DW` and `UpdatedDate_DW`.
  - New: the watermark advances only after finalize.
  - New: a failed fan-out member leaves the watermark unchanged.
- **`DocumentsTests`**: every `ValidateSource` rule with an exact message; the sample flows parse.
- **`SubmissionLandingTests`**
  - File names match the pre flow's `srcFile`.
  - Columns are null-filled; child rows get join columns; a child row sending a different join value is refused.
  - The CSV, NDJSON, JSON and Parquet writers round-trip.
  - An idempotent rewrite is a no-op; a different hash fails.
  - A pre flow with custom CSV options is refused.
- **`ArchitectureTests`**: no type or member in `SqlFlow.Delivery*` assemblies is named `Drop*`, `Replica*`, `KnownState*`, `InlineDrop` or `PayloadChunk`. This is the guard against reintroducing them.

### SQL Server (`SQLFLOW_TEST_DB`; fail rather than skip)

- **`SqlServerIngestionSourceTests`**: the same contract suite on real tables. Plus:
  - Sargable seeks on `NCI_UpdatedDate_DW`: the plan XML contains an index seek.
  - Snapshot isolation refusal message.
  - `OPENJSON` key reads with typed columns.
  - Deadlock retry.
- **`SqlServerLedgerTests`**: migrations apply only to schema `osdu`; the `SchemaVersion` row; indexed view counts; `(FlowId, SourceFileName)` lookup; plan-requested paging.
- **`SqlServerChainTests`** (end to end: files, pre, ing, OSDU flow with a fake `IProtocolFactory`). Each test writes the sample files into a temp estate whose flows are generated with the test database name.
  1. **First run.** Run pre, ing and OSDU in wave order through `DocumentExecutor` and `RunQueueStore.EnqueueGroupAsync`. Asserts:
     - three well logs are delivered;
     - each rendered document equals the `WellLog@1.4.0` fixture's `expected` JSON;
     - payload chunks are streamed;
     - each record's `SourceFileName` is `welllog_20260901.csv` with `SourceRowNumber` 1 to 3;
     - the watermark is set.
  2. **Re-run with no new files.** Tier 0 skips; nothing is sent; no attempts are written.
  3. **Changed curve.** Land a second curves file changing one curve's description. Only L-1001 is updated; its record origin stays on the welllog file while the child timestamp moved.
  4. **Identical re-land.** Land a file identical to an existing row. The ing checksum leaves the row unchanged, the OSDU run plans nothing, and the origin is unchanged.
  5. **Older business version.** Land a row with an older `update_date` but changed content. It is a stale skip, never sent.
  6. **Fan-out.** With `fanOut: 2` and 2,000 generated rows, every record is planned exactly once, batch namespaces are disjoint, and the watermark is written once.
  7. **Lineage.** The flow set collected through the lineage contributor orders the waves pre, then ing, then OSDU.
  8. **Inline submission** (ControlPlane test host with the wellbore chain). POST records:
     - landing files exist; the group has three waves;
     - wellbores are delivered with the `SubmissionLanding.FileName` origin;
     - a replay returns the same group; a different payload returns 409;
     - killing the host between store and enqueue leaves the resume service to finish the submission;
     - a failing ing member marks the submission `failed` with the member's run id.

---

## 7. Risks, open questions, implementation order

### Risks

- **Ledger reachability on nodes** (the blocker in the introduction).
- **Late-committing ingestion statements.** `UpdatedDate_DW` is stamped when the statement starts. An ing transaction that commits after the OSDU read, with a stamp older than `upper - overlap`, is missed.
  - Mitigations: a generous `overlapSeconds`, and wave ordering in groups.
  - The exact fix is a generic `systemColumns.rowVersion` in SQLFlow ingestion, read with `MIN_ACTIVE_ROWVERSION()`. That would be a proposed `sqlflow:` extension point.
- **Child deletions.** A keyed upsert never removes child rows, so a curve removed at the source is still delivered.
  - `load.reloadColumn` on a single parent-key column (per-parent replace) handles it, except when every child row of a parent disappears, which triggers no re-plan.
  - `matchKeysInSourceAndTarget` in tag mode is unsafe against a reset landing view.
  - Open question; needs a decision per dataset.
- **Columns in `change.ignoreColumnsInHash`.** A column the mapping reads but the ing flow excludes from its checksum changes without moving `UpdatedDate_DW`, so tier 1 wrongly skips. The module cannot see this across flows; document it, and warn in `check --connect` when lineage exposes it.
- **Lineage server identity.** Server identity must match between the ing `connections` reference and `source.connection`. Verify that `ServerIdentity.From` normalizes the reference text.
- **Value typing.** Types from `inferTypes` (decimal against the old double) may alter rendered numbers. Chain test 1 pins this against the mapping fixtures.
- **Pre and ing timing.** A scheduled pre run can pick up a submission file before the submission's own group runs. This is harmless for delivery, and traceability holds through `SubmissionLanding.FileName`.

### Verify before or during implementation

- A relative `source.location` resolves against the flow file's directory.
- `srcFile` is honored by the CSV reader.
- The typed view projects `FileDate_DW` as `decimal(14,0)`, so `incremental.columns: [FileDate_DW]` compares correctly.
- Downstream watermark anchoring applies to file flows run by the control plane.
- `FullLoad` combined with `FilePattern` really bypasses the file watermark.
- Local file writers can promote a temporary file atomically.
- `Storage/SignedUpload.cs` has no use outside the drop-off area.

### Generic `sqlflow:` extension points this design needs

Each is its own commit with tests, touching only `sqlflow/`.

1. **Lineage contributor.** For example `RegisteredFlowDocument.DeclaredObjects` returning `(Reads|Writes, connectionReference, threePartName)`, consumed in `FlowSetCollector` like the ingestion case.
2. **Group enqueue with companion rows.** `EnqueueGroupAsync` in a caller-supplied transaction, so `osdu` rows commit with the runs.
3. **Group mode label.** Optionally a new `RunGroupModes` constant for an explicit member set (for example `chain`).

### Implementation order, with dependencies

1. **Documents.** `FlowDefinition`, `YamlModels`, `DeliveryDocumentLoader`, `DeliveryFlowKind` shape; `DocumentsTests`. Depends only on stage 2's kind alignment.
2. **Ledger.** Entities, `OsduDbContext`, initial migration, `SchemaVersion`, `ILedger` reshape; ledger suites. Depends on stage 2's module database.
3. **Payload files.** `PayloadFile` rename, `PayloadFiles`, `StoragePayloadSource`, `PayloadLocations`, `PayloadRoots`; protocol and storage tests. Independent of 1 and 2.
4. **Source layer.** `Source/*` with the SQL implementation, the memory fixture and the contract suites. Depends on 1.
5. **Engine.** `Planner`, `SubmissionIntake`, `FlowRuntime`, `DeliveryExecutor`, `DeliveryWorker`, `DeliveryRunPayload`, `DeliveryServices`; re-fixture `EngineTests`, `ScaleEngineTests`, `RemovalTests`. Depends on 1 to 4 and on node ledger reachability.
6. **Lineage.** The contributor extension in `sqlflow/`, then the kind's declared reads. Depends on 1.
7. **Samples.** Pre, ing and OSDU YAML for both chains, `osdu/tools/SampleData`, data files, `SampleEstateTests`. Depends on 1 and 4.
8. **Chain suite.** `SqlServerChainTests` 1 to 7. Depends on 5 to 7.
9. **Inline submissions.** `InlineRecords` move, `SubmissionLanding`, `LandingFileWriter`, endpoints, `SubmissionChain`, `SubmissionLandingService`, the group-transaction extension, API tests, chain test 8. Depends on 2, 5, 6 and 7.
10. **Remaining surface.** Endpoint DTO cleanup, redeliver and verify payloads, `ReadSourceRowOperation`, CLI `check`. Depends on 5.
11. **Deletions and guards.** Delete the dead code, add `ArchitectureTests`, clean rebuild with zero warnings.
12. **Follow-on outside `osdu/src` and `osdu/tests`.** `osdu/gui/src/api/delivery.ts`, `DeliverySubmissionPage.tsx`, `DeliveryRecordPage.tsx` (drop and replica fields). Stage 5 docs: `osdu/docs/submitting-records.md`, `ledger.md`, `design.md`.

### Critical files

- `osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs`
- `osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs`
- `osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs`
- `osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs`
- `osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs`
