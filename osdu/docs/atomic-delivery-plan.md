# Plan: deliveries that complete or undo themselves

A delivery of one record can take many calls: files uploaded and registered, the record written, its bulk data sent
through a DDMS session, tables posted, a workflow run. Each call that lands changes OSDU, and the services behind the
DDMSs call storage, file and dataset in turn. When a call fails part way, OSDU keeps what the earlier calls wrote. A
delivery that then ends held or failed, or is superseded by newer work before it finishes, leaves behind records that
look complete and are not, and datasets, sessions, rows and versions that nothing references and the ledger no longer
names. They are the orphans that are hard to find.

This plan makes every delivery a **unit of work** that either completes or is **undone**: what it created is removed
reversibly, what it changed is put back where the route can, and what neither is possible for is kept on the ledger as
**left behind**, with why. Every object a delivery creates in OSDU is recorded in the ledger before or as it is created,
in an indexed table, so the ledger names every id it ever minted. The inventory flow
([inventory-plan.md](inventory-plan.md)) joins those ids with what OSDU holds. The decision is recorded in
[decisions/0012](decisions/0012-atomic-delivery.md).

Each stage lists what it changes and the tests that close it. All work is in `osdu/`: nothing in `sqlflow/` changes, and
no OSDU table changes without its migration.

## Status

| Stage | State |
| --- | --- |
| 1. The ledger: `osdu.Artifact`, the unit of work, the `undone` attempt | Done: migration `DeliveryArtifacts` (module 1.29.0), `SqlServerLedgerMigrationTests`, `AtomicShapeLedgerTests` |
| 2. The worker: recording artifacts, undo on abort, undo of abandoned units, the sweep | Done: `AtomicDeliveryTests`, `ArtifactUndoTests`, `UndoRunnerTests` |
| 3. The routes: what each declares and how each undoes | Done: the `Atomic*RouteTests` and `Atomic*Tests` of each route and shape |
| 4. Removal, deleting the ledger, purging records | Done: `AtomicDeliveryTests` |
| 5. The API, the CLI and the GUI | In progress |

## What the audit found

Every route was read against its code, its pinned contract and the services' source (osdu/specs). The worker keeps a
record's completed steps only while the record goes back to `pending`. They are dropped on six other paths, and with them
the only working copy of what the unfinished delivery created:

| Path | Where |
| --- | --- |
| A try ends `held` (a `RecordHeldException`, or a status no retry fixes) | `DeliveryWorker.Settle` |
| A try ends `failed` (the retry budget is spent) | `DeliveryWorker.Settle` |
| New work is staged for a record that is resuming | `SqlServerLedgerBulk.PendingWriteSql` |
| A try completes after newer work was queued behind it | `SqlServerLedgerBulk.CompletionUpdateSql` |
| The planner holds the record | `OsduLedger` (plan holds) |
| The record is removed or reverted | `OsduLedger` (removals, reversions) |

A failed try's ids then survive only in `osdu.Attempt.ResultJson`, which is JSON, not indexed, and pruned once a later
attempt exists. Per route:

| Route | Orphaned or incomplete when a delivery does not complete |
| --- | --- |
| `storage` | Nothing orphaned: one write, under a client id. A write whose answer was lost and whose record then fails leaves a record or a version the ledger has no version of. |
| `file` | Every dataset registered for the record (`POST /files/metadata` mints its id) when the record write is refused; a registration whose answer was lost and the index did not list in time is registered again. A payload change leaves the earlier datasets live, named nowhere once the target state moves on. |
| `dataset` | The record's `-files` dataset (a client id) when the record write fails; on an update the dataset already serves the new files under a record that did not change. |
| `ddms`, Wellbore DDMS | A created record whose metadata landed and whose bulk data never did (curves declared, no data); on an update the new metadata over the earlier bulk data. Session ids are never recorded, a cancelled try does not abandon its session, and a commit answered `committing` is taken as committed. |
| `ddms`, Well Delivery | A storage copy (`mirror`) written before the DDMS's own store refused the entity. |
| `ddms`, RAFS | A created record with some of its content tables, each a `dataset--File.Generic` RAFS minted; a release then rewrites the record without the earlier URNs and orphans those datasets for good. |
| `ddms`, historian | A created ProductionValues record with some of its points; versions a partly refused request accepted are named only in its error. |
| `ddms`, Seismic Store | A registered dataset with part of its objects, locked for a day when the try ended other than held; on an update, the read-only flag left lifted. |
| `ddms`, Reservoir Management | Rows a failed try posted under the record (up to 49 keys never recorded), posted again in full by the next release. |
| `fileAndDdms`, `manifestAndDdms` | The file route's datasets, and the DDMS's half record, together. |
| `manifest` | Every record's datasets when the trigger is refused; the by-reference manifest dataset when the run is not settled; a record a FAILED run still wrote. |
| `workflow` | Inputs and the anchor written before a stage failed; records a run wrote that the results search did not find. |
| `dspdm` | A row inserted by a save whose answer was lost, when the record is then held or failed before a try finds it. |
| `etp` | A dataspace and its OSDU record created for a batch whose transaction rolled back; objects committed whose arrays were never filled. |

## The unit of work

A **unit** is one delivery of one record's pending work: it begins with the first call that can change OSDU and ends
**committed** (delivered) or **aborted**. It spans every try while the record stays pending, since a retry resumes it. The
worker gives each unit an id, kept with the record's completed steps (`$unit`, with when it began), so the unit is dropped
exactly when the steps are, and a later try of the same work resumes the same unit.

A unit is aborted when:

| Abort | When | What the undo may remove |
| --- | --- | --- |
| `held` | a try ends held | everything the unit created, the record itself when the unit created it |
| `failed` | the retry budget is spent | the same |
| `abandoned` | its steps were dropped while it was unfinished (new work staged, a plan hold, a superseded completion) | everything; the record itself is left only when the newer work writes its metadata again, and only on a route whose next write does not read back what the unit wrote |
| `removed` | the record is removed from OSDU, or its ledger deleted | everything the unit created; the removal takes the record |

## Artifacts

An **artifact** is something a unit created in OSDU, or set out to create: a row of `osdu.Artifact`, written in the
transaction that writes the step that created it, so it is in the ledger before the delivery goes on. A call whose id the
service chooses, and whose answer can be lost, is preceded by an **intent**: the artifact is written before the call with
what will find the object when the answer is lost (a file's landing-zone path), and completed with the id when the
answer comes.

| Role | What | Kept after the unit commits |
| --- | --- | --- |
| `record` | the record itself, written by a unit that created it, before a later call of the unit | no: the record's row is its record |
| `version` | a version of an existing record (or of a dataset beside it) the unit wrote before a later call, with the version it replaced | no |
| `dataset` | a dataset the unit registered for the record's files, inputs, or a manifest | yes: one row per minted id |
| `content` | a dataset a DDMS registered for the record (RAFS content) | yes |
| `output` | a record a workflow run wrote | yes |
| `dataspace` | a Reservoir DDMS dataspace and its OSDU record | yes |
| `session` | a Wellbore DDMS bulk session | no |
| `lock` | a Seismic Store write lock, or a read-only flag lifted | no |
| `rows` | rows a DDMS keeps under the record (Reservoir Management, DSPDM) | no: the target state names them |
| `points` | historian points accepted under series versions | no |
| `objects` | an object store's objects, ETP objects or arrays | no |
| `run` | a workflow run the unit triggered, which can write records until it ends | no |

States: `intent` (the call is about to go), `pending` (created by an open unit), `live` (its unit committed),
`superseded` (a later delivery of the record replaced it; it stays live in OSDU, since earlier versions of the record
name it), `due` (its unit aborted; the undo has not run), `removed`, `restored`, `gone` (OSDU no longer held it), `kept`
(no call removes it: it is left behind, with why), `failed` (the undo was refused or could not reach OSDU; tried again
with backoff).

An artifact of a role not kept after commit is deleted when its unit commits: what it stood for is the record's own state
then. An OSDU id the unit minted stays for good as `live` or `superseded`, which is what the inventory joins.

A slot reported again (an intent completed by its id, a step a resumed try reports again) updates its one row. A resumed
try that reads OSDU again sees the unit's own write, so the version a write replaced stays the one first reported, and a
slot first reported as the record the unit created is not made a version of it.

## When the undo runs

| Moment | What runs |
| --- | --- |
| A try ends held or failed | The worker undoes the unit at once, under the record's lease, before the lease closes. Its result is an attempt of phase `undo`. |
| The worker claims a record whose open artifacts belong to another unit than the one it resumes | The abandoned unit is undone first, under the lease, keeping the record itself when the newer work writes its metadata again. When anything of it could not be undone, the newer work is not sent: it would write the ids the undo still has to take back, and the undo, when it lands, would take back the newer work's. The try is skipped (phase `undo-wait`) and not charged to the retry budget, and the record waits for the undo's next try; once the undo has used its tries, the record is held until an operator releases it, which tries the undo again first. |
| A deliver or drain run ends, and the `undo` operation | The sweep: every artifact `due`, or `failed` and past its backoff, of a record no lease holds, and every artifact `pending` of a record whose current unit is another one. The platform runs one run of a flow at a time, so nothing delivers the flow meanwhile. |
| A removal, and deleting the ledger | The record's open artifacts are undone before the record is removed; deleting the ledger also removes reversibly the datasets its records named. |

An undo is idempotent: a soft delete of an id OSDU no longer holds is `gone`, an abandoned session is not abandoned twice,
and a version written back is not written again once its artifact is `restored`. An undo that cannot reach OSDU leaves its
artifacts `failed`, tried again up to ten times with backoff, then left for an operator's `undo` run, and the record's
page says so.

### The record itself

A unit that **created** the record (the ledger held no version of it) removes it at the route's reversible scope. Before
it does, it checks that OSDU did not hold the record before the unit began: storage's `createTime` of the record must be
no earlier than the unit's start, less five minutes for clocks. A record that already existed (an id the flow claimed over
a record another system wrote) is treated as an update. Where the route cannot read storage, the record is removed only
when the unit's own step recorded its first version.

A unit that **updated** the record puts back the version the ledger held (the version the unit replaced) through the
restore a reversal makes (`VersionWriteBack`), when the route can write a version back; otherwise the record is kept, and
the undo says which part is new and which part is the earlier delivery's.

### What no undo removes

These are kept, with their reason on the artifact and the attempt: the files behind a soft-deleted dataset; landing-zone
uploads no registration names; historian points (the historian has no delete); Seismic Store objects on gc (one
dataset's delete removes every dataset of its subproject) and the earlier bytes an update overwrote; Wellbore DDMS bulk
blobs under a logical delete; Reservoir Management's own copy of a record; an ETP dataspace (its delete purges an OSDU
record) and the arrays of an ETP object the undo deleted (the store does not delete them with it); DSPDM's change history;
a DSPDM row whose key is too long to record, inserted by a save whose answer was lost.

### Order within a record

The record goes back before the datasets it names: a record that cannot be taken back yet keeps them, answered failed with
it, so OSDU never serves a record naming datasets the undo already removed. What a DDMS made beside the record (a session,
a content dataset) goes before the record, since the record is what finds it or what it can still write into; when any of
it cannot be undone yet, the record waits with it. A route reports only an intent, a created artifact, or one it settled
itself (removed, kept, gone); a slot it settled itself opens again when a later try reports it again, and one an undo
settled never does.

## The routes

Each route reports what it creates with the step that creates it (`DeliveryWork.ReportStepAsync` takes the artifacts),
reports an intent before a call that mints an id, names what a delivery made obsolete
(`DeliveryOutcome.Superseded`), and undoes a unit (`IDeliveryProtocol.UndoAsync`). A route that makes one atomic write
declares nothing and undoes nothing.

| Route | Declares | Undo of a created record | Undo of an update | Also fixed |
| --- | --- | --- | --- | --- |
| `storage` | nothing | n/a | n/a | |
| `file` | an intent per registration (its file source), completed with the dataset id; the record write's intent when the unit registered files | the record removed, then every dataset registered for each file source (found in the index, with the id the registration answered) soft-deleted | the record's prior version written back, then the datasets as for a create; the record keeps the datasets of its earlier delivery | the earlier datasets superseded on a payload change; a duplicate whose removal failed left as an intent for the sweep |
| `dataset` | an intent before each registration, under the id the route gives it: `record` when storage does not hold it, `version` with the version it holds otherwise; the `-files` dataset of a record of another kind, or a dataset record itself | Dataset service soft delete, after the `createTime` check | the prior version written back through storage; taken back even when newer work follows, since a record's own write carries the `DatasetProperties` storage holds | a registration storage cannot say anything about is not sent |
| Wellbore DDMS | the record's write as an intent before it goes, when bulk data follows; the `session` as an intent before it is created | open session abandoned (one still committing, or whose state cannot be read, waits); logical DDMS delete | the version the ledger held written back through storage (the earlier bulk data comes back with its bulk link) | the session id reported; abandoned on cancellation; `committing` polled to `committed` |
| Well Delivery | the version the unit writes, as an intent before the write (`objects`, with the version the ledger held and, where the deployment copies entities into Storage, the copy's id) | that version alone soft-deleted, so an entity another system wrote keeps its versions; the Storage copy removed when Storage says the unit's write created it | the new version soft-deleted, so the ledger's version is the latest again; the copy keeps the version the write gave it | |
| RAFS | `record` or `version`; each content table as an intent before its write, completed with its dataset (`content` when new, `version` of a dataset an earlier delivery made) | each content dataset soft-deleted (an intent found by the URN the record names), then the RAFS logical delete | content datasets new in the unit soft-deleted; a dataset an earlier delivery made given back its prior version; the record's prior version written back | a table resumes only once its write answered |
| historian | `record` or `version`; `points` per request | record soft-deleted; points kept | prior version written back; points kept | accepted versions of a partly refused request reported before the try fails |
| Seismic Store | before the registration, the dataset, its `lock` and the record it carries (every id is the route's own); the read-only flag once lifted, the lock before it is asked for; the record before a close or patch that carries it | dataset deleted with its objects (not on gc, not one taken over from before the unit), record soft-deleted | the lock released under the delivery's own id alone, read-only flag put back, record written back; objects kept | the lock released whatever ended the try; another writer's lock never released |
| Reservoir Management | `record` or `version`; `rows` per block, the in-flight block included | rows deleted, record soft-deleted | rows the unit posted deleted, prior version written back | every posted key recorded |
| `fileAndDdms`, `manifestAndDdms` | the file route's and the DDMS's | the DDMS's (session, then the record), then the datasets | both, the record first | |
| `manifest` | the file route's; the by-reference manifest `dataset` before it is stored; the record and each `run` before it is triggered, a run under a slot of its own | nothing while any run the unit triggered is in a state not known as ended; then the record removed, then its datasets soft-deleted | the record's prior version written back, then the datasets | the manifest dataset removed whatever the run did; a later try's run never hides an earlier one |
| `workflow` | an intent before each input registration and before the anchor's write; each `run` before it is triggered, under the run id the route chose; each `output` the results found, up to the number the route keeps | nothing while a run the unit triggered is going (the undo fails and waits); outputs removed reversibly when the route removes them and OSDU created them after the unit began; the anchor and inputs removed | inputs and the anchor given back their prior versions; outputs as for a create | records a run wrote that no result named stay, for the inventory |
| `dspdm` | an intent at `save-begin` for an insert, with the business object's kind and the key that finds the row, completed with the primary key DSPDM drew | the row deleted for good by its primary key, or the one row its key finds (none: the insert did not land; more than one: kept); deleted even when newer work follows, which would otherwise be held by its own row | nothing to undo (DSPDM keeps no versions) | |
| `etp` | the `dataspace` and its OSDU record when created, on the record that needed it; each object the ledger knows no object of as an intent before the commit, completed after it | objects the store created after the unit began deleted in one transaction per dataspace, a locked dataspace unlocked and locked again; the dataspace kept | kept (the store keeps no earlier versions) | dataspace creation recorded |

## Stages

### 1. The ledger

- `osdu.Artifact` (migration, module version), keyed by the ledger partition and an identity, unique on
  `(PartitionId, FlowId, DeliveryKey, UnitId, Slot)`, indexed on `(PartitionId, TargetId)` for the inventory and a lookup
  by OSDU id, on `(PartitionId, FlowId, DeliveryKey)` for a record's page, and filtered on the open states for the sweep.
- `AttemptOutcome.Undone` and the phase `undo`.
- `ILedger`: artifacts appended with steps and settled with completions in the same transaction; open artifacts of
  records; the sweep's page; artifact settlements written with their `undo` attempt.

Tests (SQL Server): an artifact written with its step survives a crash before the completion; a delivered completion makes
the unit's ids `live` and deletes its transient rows; a held completion makes them `due`; a superseded id becomes
`superseded`; an intent completed by its id is one row; the sweep's page; settlements and the `undo` attempt in one
transaction; migration up and down.

### 2. The worker

- The unit id in the steps; artifacts forwarded with each step; `DeliveryWork.ReportStepAsync` with artifacts.
- Undo on held and failed, before the lease closes; undo of an abandoned unit at claim; the sweep at the end of deliver
  and drain runs, and the `undo` operation.

Tests: a fake route that creates objects across steps, failing at each step in turn, for a create and an update, held,
failed and pending; newer work staged mid-unit; a crash after a step; an undo that cannot reach OSDU retried by the sweep;
an undo that is refused stays `failed` and is reported.

### 3. The routes

Each route in the table above, with its `FakeHttpHandler` tests: every failure point the audit names, for a create and an
update, held and failed, the intent of a lost answer, and the gap fixes.

### 4. Removal, deleting the ledger, purging records

- A removal undoes the record's open artifacts first.
- Deleting the ledger removes reversibly the datasets its records named, and refuses to forget an artifact whose undo
  failed, as it refuses to forget a record OSDU still holds.
- Purging a record keeps its artifacts: the inventory names an id minted by a purged record.

### 5. The API, the CLI and the GUI

- A record's artifacts on its page and `GET /records/{flowId}/{key}/artifacts`; a flow's open undos; `sqlflow records
  artifacts`; the `undo` operation in the run dialog.
