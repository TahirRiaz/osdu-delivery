# Plan: inventory flows

An **inventory** is every record id one OSDU kind holds in a partition, with its version, who created and last changed it
and when, kept in the module's database and compared with every ledger of the partition. The ledger says what the flows
delivered and minted; the inventory says what OSDU serves. Where the two disagree is the report: records OSDU serves that
no ledger knows (orphans), records a ledger never confirmed, records a ledger removed that OSDU still serves, datasets a
delivery minted and a later one replaced, and records a ledger delivered that OSDU no longer serves.

A ledger can come out of step with OSDU for reasons no delivery controls: a service that acted after its answer was lost,
an ingestion workflow that wrote more than it reported, a record written outside the flows, a ledger deleted or a record
purged from it, a restore of OSDU or of the database. None of these is visible from the ledger alone. This is a flow kind
of its own, `flowType: inventory`, that reads OSDU, as assertion and dimension flows do
([decisions/0010](decisions/0010-assertion-flows-read-only.md), [decisions/0011](decisions/0011-dimension-flows.md),
[decisions/0013](decisions/0013-inventory-flows.md)), and that removes the orphan, stale and forgotten ids it found when
its document allows it and an operator asks ([decisions/0014](decisions/0014-inventory-removals.md)).

Each stage lists what it changes and the tests that close it. All work is in `osdu/`: nothing in `sqlflow/` changes, and
no OSDU table changes without its migration.

## Status

| Stage | State |
| --- | --- |
| 1. The document | Done: `InventoryDocumentTests`, the census checks |
| 2. The tables | Done: migration `InventoryFlows` (module 1.30.0), `InventoryLedgerTests` |
| 3. Reading OSDU: search and storage | Done: `InventoryRunTests` |
| 4. The reconcile | Done: `InventoryLedgerTests`, `InventoryRunTests` |
| 5. The API, the CLI and the GUI | In progress |
| 6. Removing what an inventory found | Done: migration `InventoryRemovals` (module 1.31.0), `InventoryRemovalTests`, `DeliveryInventoryApiTests` |

## The document

```yaml
flowType: inventory
name: welllog-inventory
partitions: [dev]                  # or source.headers.data-partition-id, or neither for every registered partition
source:
  endpoint: ${env:OSDU_URL}
  auth: { type: oauth2ClientCredentials, ... }
  read: search                     # search (default) or storage
owners: [delivery-sp@contoso.com]  # optional: the identities whose records are this estate's; inferred when left out
maxMissingChecks: 100000           # optional: ids a ledger expects that one build reads from storage (0 to 1,000,000)
inventories:
  - name: WellLogs
    kind: "osdu:wks:work-product-component--WellLog:*"
    versions: latest               # latest (default) or all
  - name: LogFiles
    kind: "osdu:wks:dataset--File.Generic:*"
    query: "data.Endian:BIG"       # optional, search only: narrows the records read
removal: { findings: [orphan], purge: false }   # optional: what an operator may remove of what it found
reliability: { concurrency: 4, timeoutSeconds: 100 }
schedule: { cron: "0 4 * * *" }
```

- `read: search` pages the search index (`POST /api/search/v2/query_with_cursor`, 1,000 a page) with the projection
  `id`, `kind`, `version`, `createUser`, `createTime`, `modifyUser`, `modifyTime`. It needs a viewer's entitlements. The
  index lags writes and leaves out a record it failed to index, so a record written seconds before, or not indexed, is
  not in the inventory.
- `read: storage` reads storage itself: `GET /api/storage/v2/query/records?kind=&limit=1000&cursor=` lists every active
  record of one kind (role `service.storage.admin`; storage source, master `e8d65c18`: the Azure and core-plus
  repositories select `status = active`), and `POST /api/storage/v2/query/records/headers` reads the system properties of
  up to 1,000 ids a request (a soft-deleted id and one the caller may not see come back under `notFound`). A deployment
  without the headers route (404 or 405) is read through `POST /api/storage/v2/query/records`, 100 ids a request. A kind
  with wildcards is expanded through the schema service, since storage lists one kind at a time. This is the complete
  answer, unindexed records included.
- `versions: all` reads each record's versions (`GET /api/storage/v2/records/versions/{id}`), one request per record
  whose latest version changed since the last build or that is new, so a rebuild costs requests only for what moved.
- `query` narrows a search read; a storage read refuses it.
- A soft-deleted record is not served, so it is not in the inventory: that is the state an undo or a removal leaves.

## The tables

| Table | One row per |
| --- | --- |
| `osdu.Inventory` | inventory of a flow in a partition: its declaration, its kinds and their counts, the owners it used and why, its last build |
| `osdu.InventoryRun` | build or reconcile: the platform run, who asked, how it read (pages, requests), what it found and changed, its counts by finding |
| `osdu.InventoryRecord` | id the inventory holds or the ledgers expect: the OSDU id, kind, version, the create and modify user and time, first seen, last seen, gone, and the finding with what the ledgers hold of it |
| `osdu.InventoryVersion` | version of a record, with `versions: all` |
| `osdu.InventoryScan` | id a build read, staged until the build merges it |

Every table is keyed by the ledger partition first. A build reads into `osdu.InventoryScan` in chunks (a bulk copy per
10,000 ids) and merges only once the whole kind was read, in one transaction under an application lock on the inventory:
a build stopped part way changes nothing, and a record not in a complete read is marked gone, never deleted, so the report
says when it disappeared. A failed run discards what it staged; a run whose process stopped is closed failed by the next run
of its inventory, which discards its stage. A build writes a record only where something of it changed. Counts on the page
are read from the rows; a run keeps the counts it wrote.

## The findings

Each id is compared with every ledger of the partition: the records' claimed ids, the artifacts deliveries minted
([atomic-delivery-plan.md](atomic-delivery-plan.md)), and the records purged from a ledger.

| Finding | What OSDU serves | What the ledgers hold |
| --- | --- | --- |
| `tracked` | the record or dataset | a delivered record at this version, or a live artifact |
| `drifted` | the record, at another version | a delivered record at another version |
| `unconfirmed` | the record | a record that never confirmed a delivery (pending, held or failed, no version): a write that landed and was not acknowledged, or a half record |
| `stale` | the record or dataset | a record marked removed, or an artifact removed or undone |
| `superseded` | the dataset | an artifact a later delivery replaced (kept live on purpose) |
| `undoing` | the object | an artifact whose undo is due or failed |
| `forgotten` | the record or dataset | a record purged from its ledger, or an artifact of one |
| `orphan` | the record or dataset | nothing; created by an identity this estate writes as |
| `foreign` | the record or dataset | nothing; created by another identity |
| `missing` | nothing | a delivered record, or a live artifact, of the kind, which storage does not hold |
| `unlisted` | nothing in the read | a delivered record, or a live artifact, that storage holds and the read did not list: the index has not caught up, or it is outside the query |
| `gone` | nothing | nothing: the id was listed before and no ledger expects it, or the build read no id from storage |
| `unreconciled` | the record or dataset | not compared yet: a build listed it and no reconcile has run since |

**Owners.** An id no ledger knows is an `orphan` when an owner created it, else `foreign`. The owners are the flow's
`owners`, or, when it names none, the identities that created the records of the inventory a ledger claims, each with how
many records it created: an identity that created a record a flow delivered writes as this estate. The run says which
identities it used and how it knew.

**Missing.** The ids a ledger expects (a delivered record with a version, or a live artifact) that the read did not list
are read by id from storage (`POST /query/records/headers`, or `POST /query/records`), up to `maxMissingChecks` (100,000)
a build: storage not holding one is `missing`; storage holding one (outside the inventory's query, or not indexed yet) is
`unlisted`. The candidates are the ids the inventory listed before and no longer lists, first, and, for an inventory that
covers its entity type whole (its kind names the type and leaves the authority, source and version as `*`, and no query
narrows it), the ledgers' ids of the type it holds no row of. A build over a narrowed query, or a read of a concrete kind
version, reports only the ids it listed before. With `maxMissingChecks: 0` nothing is read from storage, and every id no
longer listed is `gone`. A build that reaches the bound says so, and the next build reads on.

## Operations

| Operation | What it does |
| --- | --- |
| `build` (default) | Read every inventory (or those the payload names), merge, reconcile. |
| `reconcile` | Reconcile with the ledgers as they stand now, reading only the missing candidates from storage. |
| `plan` | Count what each inventory would read; keep nothing. |
| `remove` | Remove from OSDU the ids of one finding of one inventory, as an operator asked ([Removing what an inventory found](#removing-what-an-inventory-found)). |

## Removing what an inventory found

A flow that declares `removal` lets an operator remove the ids of the findings it names, of `orphan`, `stale` and
`forgotten`: what OSDU serves that no ledger holds live. Every other finding is acted on through its ledger, and `foreign`
ids are never removable.

- **The request.** The inventory's page offers a removable finding's ids to pick (at most 1,000), or every one of them at
  once; the API takes `POST /inventories/{partition}/{inventoryId}/removals` with `finding`, `scope` (`record`, a soft
  delete, or `everything`, a purge where `removal.purge` allows it), `expected` (the count the operator was shown), the
  `ids` when they were picked, and `confirm` (the partition, typed back). It is refused unless the flow allows the finding
  and the scope, the inventory was reconciled, it holds as many ids of the finding as shown (or every id picked, with that
  finding), and no run of the flow is queued or running. It queues a run of the flow, operation `remove`, whose payload
  is `{"inventories": [name], "removal": {finding, scope, expected, ids?}, "confirm": partition}`; the run checks all of it
  again before it removes anything.
- **The run.** It reads the removal's ids from the inventory 500 at a time, in order. For each it compares the finding the
  inventory recorded with the one the reconcile's own rule gives it against the ledgers now, reads storage's headers of
  those still to go (`POST /query/records/headers`), and leaves in OSDU, skipped with why, an id whose finding moved, whose
  version is not the one the inventory listed, or (an orphan) whose creator is no owner; an id storage no longer holds is
  already gone. The rest go through the flow's own `source`: a soft delete 500 ids a request (`POST /records/delete`, one
  at a time for the ids a 207 answer names), or a purge one id at a time (`DELETE /records/{id}`). OSDU refusing a whole
  chunk with 401 or 403 stops the removal there.
- **What it keeps.** `osdu.InventoryRemoval` (one row per removal: the run, who asked, the finding, the scope, the count
  shown, whether ids were picked, where it stands, its tallies, why it stopped, its activity) and
  `osdu.InventoryRemovalItem` (one row per id it reached: the version, the finding, the outcome, why, and the ledger record a
  stale or forgotten id rested on). A chunk's outcomes are written with the inventory rows it changed in one transaction:
  an id removed, or found gone, is marked gone in the inventory at once. A stale record's ledger records the removal on the
  record (`MarkRemovedAsync`: an attempt naming who asked). The removal is an activity of the audit trail
  (`inventory-remove`), and a lookup by OSDU id answers what removals did to the id.
- **Stopped part way.** Every chunk is recorded before the next is read, so a removal stopped part way keeps what it did,
  and the next removal of the finding finds what is left; a removal its process left running is closed failed by the next.

## Stages

### 1. The document

`InventoryFlowDefinition`, its YAML model and mapper (strict keys), `InventoryFlowKind` and its executor registered in
every host, lineage (reads each kind; it writes nothing another flow reads), the census file `keys.inventory.json`. Tests:
every key, every refusal (a storage read with a query, duplicate names, more than 100 inventories, more than 50 owners, a
bound out of range, a query token not declared), the binding to a partition, the payload and the operations, the census
checks. A wildcard kind on a storage read is expanded when a build or a plan reads it, through the schema service.

### 2. The tables

The five tables (migration, module version). Tests (SQL Server): migration up and down; a merge of a complete scan adds,
changes and marks gone; an incomplete scan changes nothing; two builds of one inventory write one after the other.

### 3. Reading OSDU

The search reader (the cursor read whole or failing), the storage reader (the listing's cursor, the headers read and its
fallback), the version reader. Tests against fake services: paging, a search read failing after a chunk was staged, a
storage cursor lost or circling part way, the headers route not deployed and its own 404, a record deleted between the
listing and the headers read, a kind storage holds nothing of, a kind with wildcards expanded through the schema service,
versions read only for what moved, a run cancelled part way and closed by the next.

### 4. The reconcile

Set-based joins with `osdu.Record`, `osdu.Artifact` and `osdu.PurgedRecord`; owners; missing candidates read from storage.
Tests: every finding, owners declared and inferred, the missing bound, a reconcile of an inventory never built, one
inventory failing while the others complete.

### 5. The API, the CLI and the GUI

`GET /inventories`, `GET /inventories/{id}`, `GET /inventories/{id}/records?finding=`, a CSV export, a lookup by OSDU id
across inventories; `sqlflow inventory list|show|records|lookup`; the flow kind's panels and the inventory page with
counts by finding and a grid that pages in place, whose ids open as OSDU holds them in the workbench's bottom panel, in
the explorer's record view ([reference/concepts/explorer.md](reference/concepts/explorer.md#a-record)).

### 6. Removing what an inventory found

`removal` in the document and the census; the `remove` operation and its payload; `osdu.InventoryRemoval` and
`osdu.InventoryRemovalItem` (migration `InventoryRemovals`, module 1.31.0); the runner's removal through the flow's source
over `OsduRecordProtocol`'s batched removal; the API (`POST .../removals`, the removals and their items, removals in the
lookup); `sqlflow inventory removals|removal`; the grid's selection, the removal dialog and the Removals tab. Tests: the
document's rules, the payload and the operation; against fake services, every orphan soft deleted in chunks with every
outcome kept and the inventory updated at once, every check again before an id goes (claimed meanwhile, a version moved,
gone, a creator no owner), the refusals before anything is read, a whole chunk refused for permission stopping the run, a
207 answer asked again one id at a time, a purge one id at a time with a stale record's ledger recording it, and picked ids;
over the API, every refusal and the run queued.
