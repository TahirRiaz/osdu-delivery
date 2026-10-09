---
id: delivery-concept-ledger
title: "The delivery ledger: what OSDU Delivery records about every record, run and intervention"
type: concept
summary: "What the ledger in the osdu schema records per delivered record, how to reconstruct a record from it, and how its counts and record search work."
keywords:
  - delivery ledger
  - osdu schema
  - osdu.record
  - osdu.attempt
  - attempt history
  - audit trail
  - traceability
  - record history
  - reconstruct a record
  - record search
  - identity index
  - statistics
  - purged record
  - activity
related:
  - delivery-concept-record-lifecycle
  - delivery-concept-submissions
  - delivery-concept-removal-and-reversal
  - delivery-concept-availability-and-retention
  - delivery-concept-partitions
  - delivery-cli-records
  - delivery-concept-architecture
  - concept-provenance-and-row-keys
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduDbContext.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduSchema.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20260928064320_LedgerPartitions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Purges.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Purges.cs
  - osdu/src/SqlFlow.Delivery/Ledger/RecordIdentity.cs
  - osdu/src/SqlFlow.Delivery/Ledger/AttemptResult.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Artifacts.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/LedgerRegistration.cs
  - osdu/src/SqlFlow.Delivery/Protocols/IDeliveryProtocol.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/RecordChain.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryRecordVerbs.cs
---

# The delivery ledger: what OSDU Delivery records about every record, run and intervention

The ledger is the set of tables in the `osdu` schema that says, for every record a delivery flow handles, what OSDU
holds, what is waiting to be sent, and everything that ever happened to it. Traceability is the product: a delivered
record can be reconstructed from the ledger alone, from the source file and row it came from, through the mapping,
template and cache version that rendered it and every try to send it, to the OSDU id and version it landed as and every
operator action on it. Every delivery path, retry, removal, reversal and release goes through the ledger; there is no
repair script or side door that changes what OSDU holds without writing it here.

You read the ledger in the GUI (OSDU, Ledger: **Delivery**, **Records**, **Audit trail**, and a flow's Records, Issues and
Submissions tabs), with [`sqlflow records`](../cli/records.md), and through the [delivery API](api.md). This page says what
the ledger records and how to read it back. [Record lifecycle](record-lifecycle.md) says how a record moves between states,
and [Submissions](submissions.md) how a run plans and sends.

## Where the ledger lives

The `osdu` schema belongs to the OSDU Delivery module: its own EF Core context (`OsduDbContext`), its own migration history
(`[osdu].[__EFMigrationsHistory]`) and its own schema version (`[osdu].[SchemaVersion]`, one row: the module version, the
last migration, when and by whom, and the oldest SQLFlow catalog migration it needs). No table of the schema has a foreign
key into SQLFlow's catalog: a ledger row names a run, a pipeline or a repository by its id alone. See
[Architecture](architecture.md) and [`sqlflow db`](../cli/db.md).

By default the schema lives in the catalog's database. A deployment can give the module a database of its own with
`SQLFLOW_OSDU_DB` (or `Osdu:Database:Connection`). A worker node never opens a catalog connection; it reaches the ledger
through that module connection, and a node started without it refuses every operation that needs the ledger.

## Ledgers, flows and partitions

A **ledger** is the records of one flow in one OSDU partition, under one ledger identity (the `FlowId` every ledger row
carries). A flow in the single form has one identity, derived from its name; each interface of a source keeps its own,
derived from `<flow>/<interface>` (see [Interfaces](../flow/interfaces.md)). A flow that works in partitions derives one
identity per partition from that name and the partition, except in the partition marked `keepLedger`. The same source row read by two flows is two
records, each with its own state and history.

`osdu.Ledger` is the directory: one row per ledger identity, naming its partition, its kind (`delivery`, `retrieval`,
`assertion`, `dimension` or `inventory`), its flow and interface. `osdu.LedgerPartition` numbers each partition once, and every ledger table's key
starts with that two-byte number, so one partition's rows are one range of every clustered index. A run registers its
ledger in the partition it delivers to before it writes a row; a registration that names another partition than the
directory holds is refused, naming both. See [Partitions](partitions.md).

## The tables

| Table | One row is | Grows with |
| --- | --- | --- |
| `osdu.Record` | The current state of one deliverable of one flow | Deliverables |
| `osdu.Attempt` | One try of a record, append-only, or one decision written as an attempt | Tries |
| `osdu.Submission` | One plan of a flow over its ingestion tables | Plans |
| `osdu.WorkBatch` | One file of rendered documents of a submission | Batches |
| `osdu.Lease`, `osdu.RecordEvent` | A worker's hold on work, and what it learned and has not applied yet | In-flight work only |
| `osdu.SourceWatermark` | How far the last whole-scope plan of a flow scope read | Flow scopes |
| `osdu.Activity` | One run or operator intervention: the audit trail | Runs and interventions |
| `osdu.ActivityRecord` | One record an intervention or a reversal reached | Records reached |
| `osdu.RecordIdentity` | One value a record can be found by | Records and their values |
| `osdu.PurgedRecord` | The one line kept of a record deleted from the ledger | Records deleted |
| `osdu.Artifact` | One object a delivery created in OSDU, or set out to create | Objects delivered |
| `osdu.Reversal`, `osdu.ReversalItem` | One reversal of a run or submission, and each record it reached | Reversals |
| `osdu.Ledger`, `osdu.LedgerPartition` | The ledger directory and the partition numbers | Ledgers |

Other tables of the schema are documented with what they serve:

- `osdu.CacheVersion`, `osdu.CacheItem`, `osdu.CacheMember`, `osdu.CacheSet`, `osdu.CacheSetEntry`, `osdu.UpdateTag`,
  `osdu.CacheDefinition`: the partition cache and its changes, in [The partition cache](partition-cache.md).
- `osdu.Template`: saved OSDU schemas, in [Templates](templates.md). `osdu.Mapping`: the synced mappings, in
  [The mapping document](../flow/mapping.md). `osdu.Interface`: the read model of sources and interfaces, in
  [Interfaces](../flow/interfaces.md).
- `osdu.Partition` and `osdu.ConfigProperty`: the partition registry and central configuration, in
  [Partitions](partitions.md).
- `osdu.Retrieval`: in [Retrieval flow](../flow/retrieval.md). `osdu.AssertionRun`, `osdu.AssertionResult`: in
  [Assertion flow](../flow/assertion.md).
- `osdu.Dimension` and its tables, and each `osdu.dim_<dimension>`: in [Dimension flow](../flow/dimension.md).
  `osdu.Inventory` and its tables: in [Inventory flow](../flow/inventory.md).
- `osdu.SearchTerm`, `osdu.SearchTermRefinement`: in [Search terms](search-terms.md).

### `osdu.Record`: the current state of one deliverable

Keyed by `(PartitionId, FlowId, DeliveryKey)`. The delivery key is a deterministic UUID derived from the source key, so the
same row always lands on the same record.

| Columns | What they hold |
| --- | --- |
| `SourceKey`, `SourceKeyJson`, `Label`, `MappingName` | The source key as text (at most 400 characters) and as a JSON array in `source.record.key` order, which a key-scoped read uses; the mapping's `dataset.label` rendered for the row (at most 400 characters, for search and display); the mapping. |
| `TargetId`, `ClaimedTargetId`, `TargetVersion` | The OSDU id and the last known version. `ClaimedTargetId` is the id the record claimed when it first queued a document, kept for good and unique across the ledger: one OSDU record belongs to one record of one flow. |
| `TargetStateJson` | Every value the target returned across the record's deliveries: record id and version, dataset ids, file sources, a workflow run id. |
| `MetadataHash`, `PayloadHash`, `PayloadModifiedUtc` | What OSDU holds, as hashes: the change gates for the next plan ([Change detection](change-detection.md)). |
| `RenderContext`, `CacheSetId` | What the version OSDU holds was rendered with (below), and the set of cached values it read. |
| `SourceFingerprint`, `SourceModifiedUtc` | The source version the delivered document was built from. |
| `SourceFileName`, `SourceRowNumber`, `SourceUpdatedUtc`, `SourceInsertedUtc` | Where that version came from: the ingestion row's `FileName_DW` (at most 800 characters), `RowNumber_DW` and `UpdatedDate_DW`, and the row's arrival (`InsertedDate_DW`). See SQLFlow's [provenance columns](../../../../sqlflow/docs/reference/concepts/provenance-and-row-keys.md). |
| `Pending*`, `PendingDocumentRef`, `WorkBatch`, `LastSubmissionId` | The work waiting to be sent: its hashes, render context, source version and origin, payload location, the OSDU ids it refers to (`PendingReferences`), and where its rendered document sits (`batch:offset:length` in a work batch file). The document itself is never stored in the database. |
| `PendingStepJson` | The steps an earlier try of the pending work completed, so the next try resumes after them. |
| `Status`, `Blocked`, `ProblemHash` | The custody state and whether it is blocked, and while blocked by a held or failed try, the issue it shares with every record refused for the same reason ([Record lifecycle](record-lifecycle.md)). |
| `AttemptCount`, `NextAttemptUtc`, `LastError` | The pending work's tries, when the next is due, and the last error or hold reason, redacted (at most 2,000 characters). |
| `LeaseOwner`, `WaitingFor`, `PlanRequestedUtc` | The lease delivering it now; the OSDU id a waiting record waits for; the moment the ledger asked for it to be planned again. |
| `ValidationOutcome`, `ValidationProblems`, `ValidatedUtc`, `AcceptedMetadataHash` | What the last check of its document against the template came to, and the document a release accepted as it is ([Preflight](preflight.md)). |
| `LastDeliveredUtc`, `LastVerifiedUtc`, `LastVerifyOutcome` | Custody times, and the last verify outcome: `match`, `drifted`, `missing` or `error`. |
| `CreatedUtc`, `UpdatedUtc` | When the row was created and last changed. |

### `osdu.Attempt`: every try, and every decision that changed the record

Append-only. One row per delivery try, and one per decision the engine or an operator made about the record even when
nothing was sent: a hold at render time, a stale or identical row, a removal, a restore, an undo, a row a sync no longer
found. The record's history is its attempts.

| Columns | What they hold |
| --- | --- |
| `FlowId`, `DeliveryKey`, `SubmissionId`, `RunId`, `WorkBatch` | The record, the submission and platform run the try belonged to, and the batch it was drained from. |
| `Worker`, `StartedUtc`, `CompletedUtc` | Who made it (a worker, `intake`, `sync`, or the operator who asked for a removal) and when. |
| `Outcome`, `Phase` | What came of it and what was sent; the values are listed in [Record lifecycle](record-lifecycle.md#attempts). |
| `MetadataHash`, `PayloadHash`, `TargetVersion` | The hashes the try established and the OSDU version it returned. |
| `SourceFileName`, `SourceRowNumber`, `SourceUpdatedUtc`, `SourceDeletedUtc` | The ingestion row the try's document was built from. Kept here because the record's own origin moves on with later versions. |
| `Error` | The redacted error of a held or failed try (at most 2,000 characters). |
| `ResultJson` | Every step the route took (name, start, duration, status, what the target returned, whether an earlier try completed it), the `correlationId` every OSDU request of the try carried, the version OSDU held before (`replaced`), the verdict of the check before sending (`validation`), the unit of work (`unit`) and a note (`detail`) for a try that did not fail. |

The `correlationId` is the id each OSDU request of the try sent in its `correlation-id` header, so an attempt can be followed
into the OSDU services' own logs.

### Runs, submissions and leases

`osdu.Submission`, `osdu.WorkBatch`, `osdu.Lease`, `osdu.RecordEvent` and `osdu.SourceWatermark` hold how a run planned and
sent its records: one row per plan with its window and counts, one per work batch file, one per worker claim, and what a
worker appended under its lease. [Submissions](submissions.md) describes them.

### `osdu.Activity` and `osdu.ActivityRecord`: the audit trail

One `osdu.Activity` row per run and per operator intervention, never deleted:

- runs: `deliver`, `intake`, `drain`, `verify`, `sync`, `undo`, `reverse`, `delete-ledger`, and `probe` (a target probe,
  by button or schedule);
- interventions: `release`, `redeliver`, `rerender` (bring up to date), `delete` (a removal from OSDU),
  `restore-previous`, `purge` (deleting removed records from the ledger), `remove-dimension` and `inventory-remove`.

Each row carries the actor (for a run, who asked for it or `schedule:<name>` for a run a schedule fired; `user:<name>` for
an intervention from the GUI or the API, `cli:<user>` for one from a workstation, `service:schedule` for a scheduled probe;
see [Authentication and identity](authentication-and-identity.md)),
start and end, outcome (`running`, `completed`, `failed` or `cancelled`), parameters, the submission, record and platform
run it concerned, a summary of the counts that are not zero, and for runs the captured run log (at most 200,000
characters). `Idle` marks a run that completed having changed nothing (a scheduled run that found nothing new). The GUI's
audit trail leaves idle runs out unless **Show idle runs** is on, and says how many it left out; `GET /activities` takes
`idle=false` to leave them out and `idle=true` to list only them. An intervention is never idle.

An intervention made for one record names it on its row. One that reaches many (a release of every blocked record, a
redelivery of every delivered record, a reversal) names each record it changed in `osdu.ActivityRecord`, written by the
statement that changed the record. A record's activities (`GET /records/{flowId}/{key}/activities`) are those that name it
on their row and those that name it there, so its history shows every request that reached it, however many records the
request reached.

### `osdu.PurgedRecord`: what is kept of a record deleted from the ledger

A record removed from OSDU can be deleted from the ledger ([Removal and reversal](removal-and-reversal.md)). Its attempts,
search entries and row go; one line stays: its key, source key, label, the OSDU id it was delivered and removed under, the
last version an attempt named, how many attempts went with it, who deleted it, when, and under which activity. Asked for
afterwards, the record's API route answers 404 titled `Deleted from the ledger` with that line under `purged`.

### `osdu.Artifact`: every id a delivery minted

Every object a delivery created in OSDU, or set out to create, written in the transaction that writes the step that made it:
a dataset, a bulk session, a version written before a later call failed, rows, a workflow run. An id a committed delivery
minted stays for good, whatever later happens to the record or its ledger, which is what lets an
[inventory flow](../flow/inventory.md) say which ids OSDU serves that no delivery accounts for. A delivery that does not
complete is undone from these rows; see [Removal and reversal](removal-and-reversal.md#unfinished-deliveries-and-their-undo).

| Columns | What they hold |
| --- | --- |
| `UnitId`, `UnitStartedUtc`, `Slot`, `Role` | The unit of work, when it began, the route's name for the object and what it is (`record`, `version`, `dataset`, `content`, `output`, `dataspace`, `session`, `lock`, `rows`, `points`, `objects`, `run`). |
| `TargetId`, `Locator`, `Version`, `PriorVersion` | The OSDU id, or what finds the object when the id is not known yet; the version written and the one it replaced. |
| `State` | `intent`, `pending`, `live`, `superseded`, `due`, `removed`, `restored`, `gone`, `kept` or `failed`. |
| `Note`, `UndoAttempts`, `NextUndoUtc`, `Settled*` | Why it was kept or why its undo failed, the undo tries, when the next is due, and who settled it in which run. |

### `osdu.Reversal` and `osdu.ReversalItem`

One reversal per run or submission reversed in a ledger, and one item per record its source delivered, with what OSDU held
before and what the reversal did. Never pruned; deleting the ledger deletes them. See [Removal and reversal](removal-and-reversal.md#reversing-a-run-or-a-submission).

## Reconstructing a delivered record

Every question about a delivered record is answered from the ledger:

| Question | Where the ledger answers it |
| --- | --- |
| Which source file and row did it come from? | `SourceFileName`, `SourceRowNumber`, `SourceUpdatedUtc` on the record for the version OSDU holds, and on each attempt for the version that try sent. `GET /records/{flowId}/{key}/chain` follows every change of the row back to the ingestion run that stamped it and the landing run that brought its file in, naming a run only when the catalog proves it. |
| Which mapping, template and cache rendered it? | `RenderContext`: the mapping reference and fingerprint, the template version, the cache partition and version, and the mapping parameters. `CacheSetId` names the cached values the render read (`GET /records/{flowId}/{key}/cache`). |
| Every try and its outcome | The record's attempts, newest first: outcome, phase, error, steps, correlation id, the ingestion row it was built from. |
| Which OSDU id and version did it land as? | `TargetId`, `TargetVersion`, `TargetStateJson`; each delivered attempt's `TargetVersion` and the version it replaced. |
| Who released, redelivered, removed or reversed it, and when? | The record's activities: its own and those naming it in `osdu.ActivityRecord`. |
| What did its deliveries create in OSDU? | Its artifacts. |

In the GUI a record's page has the tabs **Timeline** (attempts and activities), **Source**, **Render**, **OSDU** and
**Artifacts**. On the command line:

```bash
sqlflow records show flows/welldb-wellbore-03-delivery.yaml --key welldb:WB-000123 --partition dev --attempts 50
sqlflow records artifacts flows/welldb-wellbore-03-delivery.yaml --key welldb:WB-000123 --partition dev
```

`--key` takes the delivery key or the source key. The API routes are `GET /api/v1/delivery/records/{flowId}/{key}` and its
`/attempts`, `/activities`, `/chain`, `/cache` and `/artifacts`. The flow id in these routes is the ledger identity, not the
pipeline id, so a record's history stays reachable after its flow leaves the repository.

## Statistics are counted from the records

A flow's counts (`GET /api/v1/delivery/flows/{pipelineId}/stats`: total, pending, delivering, delivered, held, failed,
deleted, reverted, waiting, drifted, delivered in the last 24 hours, the last delivery and verification, the submissions)
are counted from `osdu.Record` through its status indexes on every call. Nothing keeps a running total beside the records,
so the counts are exact and cannot drift from the ledger. Drifted counts records whose last verify outcome is `drifted` or
`missing`. A source's counts are its interfaces' added up, and a flow that serves several partitions is counted per
partition or added up across them.

A submission's counts are counted the same way when it closes ([Submissions](submissions.md)), and a flow's issues are
counted from the blocked records ([Record lifecycle](record-lifecycle.md#issues)). The metrics the engine exports
([Run trace and metrics](run-trace-and-metrics.md)) are rates to watch and alert on; the delivered, pending, held and
failed counts the GUI and CLI show are always read from the ledger.

## Finding records

Every listing reads a bounded, index-backed part of the ledger, however many records a flow holds.

**The lookup across flows** (the search box, `GET /api/v1/delivery/records?search=`) reads the partition the request names,
else the workbench's (the `X-Osdu-Partition` header the GUI sends), else every partition, each through its own range of a
partition-first index. A term that parses as a UUID is a delivery key and finds every flow's record of that row. Anything else is a prefix over
`osdu.RecordIdentity`, which holds, folded to upper case, every value a record is known by:

| Kind | Values |
| --- | --- |
| `identity` | The columns the mapping declares in `dataset.identity` (a wellbore name, a UWI). |
| `key` | The source key whole, and each of its key values. |
| `label` | Up to 8 words of the rendered label. |
| `osdu` | The OSDU id, and its part after the last colon. |
| `file` | The ingestion file the current version came from. |

A record contributes at most 24 values, each at most 200 characters; values shorter than 3 characters are not stored. A
record's values are rewritten as a set whenever it is staged. The lookup takes at most 1,000 candidates from the index and
reports a larger match as a floor.

**A flow's listing** (a flow's Records tab, `GET /flows/{pipelineId}/records`, `sqlflow records list`) filters by status,
submission, run, the submission that delivered it, issue or drift, and searches by a UUID (exact) or a prefix over the
source key, label, OSDU id and origin file name. It counts no further than 25,000 matches, pages through the first 25,000
in update order (newest first, ties broken by key), and refuses a page past them, saying to narrow the filter. A contains
search has no index: it runs only when the rest of the filter leaves at most 100,000 records, and is refused otherwise with
`A contains search reads every record the rest of the filter leaves, and this filter leaves more than 100000. Use a prefix search, which is indexed, or narrow by status, submission or run first.`

### The indexes behind it

Every key and every index that serves a listing leads with `PartitionId`. The ones an operator meets most:

| Index on `osdu.Record` | Serves |
| --- | --- |
| `(PartitionId, FlowId, DeliveryKey)` (primary key) | One record; key-ordered walks of a flow (removals, key-scoped plans, deleting a ledger). |
| `(PartitionId, FlowId, Status, NextAttemptUtc)`, `(PartitionId, FlowId, LastSubmissionId, Status, NextAttemptUtc)` | The worker's claim and what is due next, answered from the index alone. |
| `(PartitionId, FlowId, Status, UpdatedUtc)`, `(PartitionId, FlowId, UpdatedUtc)`, `(PartitionId, Status, UpdatedUtc)`, `(PartitionId, UpdatedUtc)` | Listings newest first, in a flow and across a partition. |
| `(PartitionId, FlowId, Label)`, `(..., SourceKey)`, `(..., TargetId)`, `(PartitionId, FlowId, SourceFileName, SourceRowNumber)` | Prefix search in a flow, and "which records came from this file". |
| `(PartitionId, DeliveryKey)`, `(PartitionId, TargetId)`, `(PartitionId, SourceKey)`, `(PartitionId, Label)`, `(PartitionId, SourceFileName)` | Lookups across the flows of a partition. |
| `(PartitionId, FlowId, ProblemHash, UpdatedUtc)` filtered to blocked records | A flow's issues, an issue's records, the release of an issue. |
| `(PartitionId, FlowId, PlanRequestedUtc)` filtered | The records asked to be planned again, which each run pages. |
| `(PartitionId, WaitingFor)` filtered | Waiting records, released when the record they wait for lands. |
| `(ClaimedTargetId)` unique, filtered, binary collation | One OSDU id, one record: the database refuses a second claim. |

`osdu.Attempt` is indexed on `(PartitionId, FlowId, DeliveryKey, StartedUtc)` (a record's timeline), `(SubmissionId, Outcome,
Phase)` (a submission's counts and the records it delivered), `(RunId, PartitionId, FlowId, DeliveryKey)` (a run's records)
and `(StartedUtc)` (retention). Its primary key and `StartedUtc` index, and `osdu.RecordEvent`'s primary key, grow at their
end and are set to `OPTIMIZE_FOR_SEQUENTIAL_KEY` where the server has it. OSDU ids are compared in a binary collation
(`Latin1_General_100_BIN2`), since ids that differ only in case are different records.

## Writes at scale

Many nodes write one record table at once, so no statement writes more than 1,000 records: staging, releases, redeliveries
and lease applications all work a slice at a time, and a statement the database picks as a deadlock victim is tried up to
five times in all, each statement written so a second run does what the first would have. A worker does not write `osdu.Record` while it delivers: it renews
one lease row and appends events and attempts, and the records change when the lease applies them
([Submissions](submissions.md#leases-and-claims)). Nothing else is maintained inside a record write.

## Related pages

- [Record lifecycle](record-lifecycle.md): states, attempts, retries, issues, releases.
- [Submissions](submissions.md): plans, work batches, leases, many nodes.
- [Removal and reversal](removal-and-reversal.md): taking records out of OSDU, deleting the ledger, reversing a run.
- [Availability and retention](availability-and-retention.md): pruning, backup, recovery.
- [`sqlflow records`](../cli/records.md) and the [operations runbook](../guides/operations-runbook.md).
