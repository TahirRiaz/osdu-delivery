---
id: delivery-flow-inventory
title: "Inventory flow (flowType: inventory): every id OSDU serves, compared with the ledgers to find orphans and missing records"
type: flow-reference
summary: "The flowType: inventory document: list every id a kind holds in a partition, compare it with every ledger, and report orphan, missing and stale records."
keywords:
  - inventory flow
  - orphan records
  - missing records
  - reconcile
  - findings
  - owners
  - maxmissingchecks
  - "source.read"
  - removal
  - soft delete
  - purge
  - unlisted
  - drift between osdu and ledger
yamlPath: "(root, flowType: inventory)"
related:
  - delivery-guide-finding-orphans
  - delivery-cli-inventory
  - delivery-concept-ledger
  - delivery-concept-removal-and-reversal
  - delivery-concept-partitions
  - delivery-flow-delivery
  - flow-schedule
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/InventoryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/InventoryDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/InventoryFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/InventoryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryRunner.Removal.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryReaders.cs
  - osdu/src/SqlFlow.Delivery/Engine/Inventories/InventoryReport.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Inventories.cs
  - osdu/src/SqlFlow.Delivery/Ledger/InventoryRemovals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Inventory.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryInventoryEndpoints.cs
  - osdu/docs/census/keys.inventory.json
  - osdu/specs/core/storage/openapi.yaml
---

# Inventory flow (flowType: inventory): every id OSDU serves, compared with the ledgers to find orphans and missing records

An inventory flow lists every record id one or more OSDU kinds hold in a partition, with its version and who created and
last changed it, keeps the list in the module's database, and compares every id with every ledger of the partition.
The ledgers say what the delivery flows delivered and minted; the inventory says what OSDU serves; where the two
disagree is the report. Use it to find records no ledger knows (orphans), records a ledger expects that OSDU no longer
holds (missing), ids an unfinished delivery left behind, and records a ledger removed or forgot that OSDU still serves.

A ledger can fall out of step with OSDU for reasons no delivery controls: a service that acted after its answer was
lost, an ingestion workflow that wrote more than it reported, a record written outside the flows, a ledger deleted or a
record purged from it, a restore of OSDU or of the database. None of this is visible from the ledger alone. A build, a
reconcile and a plan only read OSDU and never write a ledger. A flow that declares `removal` also lets an operator
remove the orphan, stale or forgotten ids it found. The [orphans guide](../guides/finding-orphans.md) walks through it;
[`sqlflow inventory`](../cli/inventory.md) reads the results.

## Example

```yaml
flowType: inventory
name: welldb-06-inventory
description: Every wellbore and well log OSDU serves in the partition, set against the ledgers of the welldb flows.
batch: welldb
partitions: [dev, test]

source:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body: { scope: "${env:OSDU_SCOPE}" }
  read: search

owners: [welldb-delivery@example.com]
maxMissingChecks: 100000
removal: { findings: [orphan, stale], purge: false }
reliability: { concurrency: 4 }

inventories:
  - name: Wellbores
    description: Every wellbore record, whatever its authority, source and schema version.
    kind: "*:*:master-data--Wellbore:*"
  - name: WellLogs
    kind: "*:*:work-product-component--WellLog:*"
    versions: all
  - name: LogFiles
    kind: "osdu:wks:dataset--File.Generic:*"
    query: 'tags.DeliveredBy:"welldb"'
```

`Wellbores` and `WellLogs` cover their entity types whole, so a record a ledger delivered that OSDU no longer serves is
reported missing. `WellLogs` keeps every version of each record. `LogFiles` is narrowed by a query, so it reports only
the ids it listed before and no longer lists. An operator may soft delete the orphan and stale ids it finds.

## Top-level keys

Unknown keys are refused when the document is read.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `flowType` | text | required | `inventory`. |
| `name` | text | required | The flow's name: its pipeline identity, and the name its inventories are kept under (`name@partition` for a flow that works in partitions). |
| `description` | text | none | What the inventories are for. |
| `batch` | text | none | The grouping label the pipeline is filed under. |
| `parameters` | map | none | Parameters an inventory's `query` uses as `{name}` tokens. `partition` cannot be declared: `{partition}` is always the partition the run reads. |
| `partitions` | list | none | The partitions the flow keeps inventories of, each a literal `data-partition-id`. Partitions are settled as a [dimension flow's](dimension.md#partitions) are: listed, hard-coded by `source.headers.data-partition-id`, or every registered partition. A run reads one partition; `*` is refused. |
| `source` | map | required | The OSDU platform, and how it is read. See [source](#source). |
| `owners` | list | inferred | The identities this estate's records are written as (OSDU's `createUser`: a user's e-mail, an application's client id), at most 50, each at most 256 characters with no whitespace, unique ignoring case. See [Owners](#owners). |
| `maxMissingChecks` | integer | 100,000 | How many ids a ledger expects that one build reads from storage, to tell `missing` from `unlisted`: 0 (none) to 1,000,000. |
| `inventories` | list | required | The inventories, at least 1 and at most 100, names unique ignoring case. |
| `removal` | map | none | What an operator may remove from OSDU of what the inventories found. Left out, the flow only reads OSDU. See [Removal](#removal). |
| `reliability` | map | delivery defaults | The HTTP settings a [delivery flow](delivery.md) takes, and `concurrency` (default 8): how many version lists a build reads at once for `versions: all`. Inventories are read one after another. `parallelInterfaces` is refused. |
| `schedule`, `mode`, `lifecycle` | | none | SQLFlow's envelope keys ([schedule](../../../../sqlflow/docs/reference/flow/schedule.md), [flow overview](../../../../sqlflow/docs/reference/flow/overview.md)). A schedule's `values` may name the partition (`partition: dev`) and the flow's parameters. |

### source

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `endpoint` | text | required | The platform's base URL, usually `${env:OSDU_URL}`. |
| `auth` | map | `type: none` | How requests authenticate, written as a delivery flow's `target.auth` is; secrets are references only. |
| `headers` | map | none | Headers sent with every request; `data-partition-id` only for a flow that names no `partitions`. |
| `read` | `search` or `storage` | `search` | How the inventories read OSDU. See [Reading OSDU](#reading-osdu). |
| `queryPath` | text | `/api/search/v2/query` | The offset search a plan counts through. |
| `searchPath` | text | `/api/search/v2/query_with_cursor` | The cursor search a search read pages through. |
| `recordQueryPath` | text | `/api/storage/v2/query/records` | Storage's listing of a kind's ids (GET) and its read of records by id (POST). |
| `headersPath` | text | `/api/storage/v2/query/records/headers` | Storage's read of up to 1,000 records' system properties by id. |
| `versionsPath` | text | `/api/storage/v2/records/versions` | Storage's list of a record's versions, for `versions: all`. |
| `schemaPath` | text | `/api/schema-service/v1/schema` | The schema service, which expands a kind with wildcards for a storage read. |
| `bulkDeletePath` | text | `/api/storage/v2/records/delete` | Storage's soft delete of a list of records, which a removal sends 500 ids at a time. |
| `deletePath` | text | `/api/storage/v2/records/{id}:delete` | Storage's soft delete of one record, which a removal falls back to; must name `{id}`. |
| `purgePath` | text | `/api/storage/v2/records/{id}` | Storage's purge of a record and every version, one id at a time; must name `{id}`. |

A path must start with `/` and hold no whitespace, query or fragment.

### Reading OSDU

- **`read: search`** (the default) pages the search index (`query_with_cursor`, 1,000 ids a page) returning `id`, `kind`,
  `version`, `createUser`, `createTime`, `modifyUser` and `modifyTime`. A viewer's entitlements are enough. The index
  lags writes and leaves out a record it failed to index, so such a record is not listed; when a ledger expects it, it is
  read from storage by id and reported `unlisted`.
- **`read: storage`** lists every active record of each kind from storage itself (`GET /query/records?kind=`, 1,000 ids a
  page, the `service.storage.admin` role), and reads their system properties 1,000 at a time (`POST
  /query/records/headers`), or 100 at a time through `POST /query/records` where a deployment does not serve the headers
  route. A kind with wildcards is expanded through the schema service into the kinds it matches, where `*` stands for a
  whole segment. Unindexed records are listed. A storage read takes no `query`.

A soft-deleted record is not served, so it is in no inventory.

### An inventory

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `name` | text | required | A letter or digit, then letters, digits, `.`, `_` and `-`, at most 100. A run's payload, the CLI and the GUI name the inventory by it. |
| `description` | text | none | What the inventory holds. |
| `kind` | text | required | The kind read, `authority:source:entityType:version`, each segment a value or `*`, at most 300 characters. |
| `query` | text | every record | A Lucene query narrowing a search read, with `{name}` and `{partition}` tokens, at most 4,000 characters. Refused on a storage read. |
| `versions` | `latest` or `all` | `latest` | `all` also keeps every version storage keeps of each record, read (`GET /records/versions/{id}`) only for a record that is new or whose latest version moved since its versions were read. |

**Covering an entity type whole.** An inventory whose kind names the entity type and leaves the authority, source and
version as `*` (`"*:*:master-data--Wellbore:*"`), with no query, holds every record of that type OSDU serves. Its
reconcile also reads from storage the ledgers' delivered records and live minted ids of the type it holds no row of,
and reports each `missing` or `unlisted`. An inventory narrowed further (a query, or a concrete authority or version)
reports only the ids it listed before and no longer lists.

### Owners

An id no ledger knows is an `orphan` when one of the owners created it, else `foreign`. The owners are the flow's
`owners`; left out, a reconcile infers them: every identity that created at least 1% of the inventory's ids a ledger
claims (and at least one). Each reconcile records which identities it used, how many claimed ids each created, and how
it knew them (`declared`, `inferred`, or `none` when nothing is claimed).

### Removal

```yaml
removal: { findings: [orphan, stale, forgotten], purge: false }
```

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `findings` | list | required | The findings whose ids an operator may remove, at least one, each once: `orphan`, `stale`, `forgotten`. Every other finding is acted on through its ledger, and `foreign` ids are never removable. |
| `purge` | boolean | `false` | Whether a removal may purge (`DELETE /records/{id}`: every version destroyed, not reversible) rather than soft delete (`POST /records/delete`, reversible in OSDU). |

The removal goes through the flow's own `source` and credentials. A soft delete needs `users.datalake.editors` (or
admins) and the identity in each record's owners ACL; a purge needs `service.storage.admin` and owner (storage OpenAPI).
See [What a removal does](#what-a-removal-does).

## Operations

| Operation | What it does |
| --- | --- |
| `build` (default) | Reads every id of each inventory whole, merges it, reads versions where asked, and reconciles. |
| `reconcile` | Compares each inventory, as its last build left it, with the ledgers as they stand now, reading from storage only the ids a ledger expects. Refused for an inventory never built. |
| `plan` | Counts through the search the records each inventory matches (and lists the kinds a storage read expands to), reading no id and keeping nothing. |
| `remove` | Removes from OSDU the ids of one finding of one inventory, as an operator asked; only for a flow that declares `removal`. |

The payload takes `inventories` (names; none takes every inventory). A `remove` run also takes `removal` and `confirm`,
and names exactly one inventory:

```json
{
  "inventories": ["WellLogs"],
  "removal": { "finding": "orphan", "scope": "record", "expected": 37 },
  "confirm": "dev"
}
```

| Field | Meaning |
| --- | --- |
| `removal.finding` | `orphan`, `stale` or `forgotten`, one the flow's `removal.findings` names. |
| `removal.scope` | `record` (a soft delete) or `everything` (a purge, only where `removal.purge` is true). |
| `removal.expected` | How many ids the operator was shown, at least 1. With no `ids`, the inventory must hold exactly that many ids of the finding. |
| `removal.ids` | The ids picked, 1 to 1,000, each once, as many as `expected`. Left out, every id of the finding is removed. |
| `confirm` | The partition the run acts in, typed back; it must be the run's partition. |

`removal` and `confirm` are refused on any other operation. Anything else in the payload is refused: `An inventory flow's
payload names the inventories a run builds or reconciles (inventories), and for a removal what it removes (removal,
confirm), and nothing else; an inventory has no submission, record, slice, interface, test or dimension to name.`

```bash
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation reconcile --payload '{"inventories":["WellLogs"]}'
sqlflow run flows/welldb-06-inventory.yaml --set partition=dev --operation remove --payload @removal.json
```

`build`, `reconcile` and `remove` keep what they do in the module's database; a `plan` keeps nothing. See
[running an OSDU flow](../cli/run.md).

## The findings

Each id gets one finding. "Live" artifacts are the datasets and other ids a delivery minted.

| Finding | What OSDU serves | What the ledgers hold | Raised |
| --- | --- | --- | --- |
| `orphan` | the record | nothing; an owner created it | yes |
| `missing` | nothing | a delivered record, or a live minted id, that storage does not hold | yes |
| `undoing` | the id | a minted id whose undo is due or failed | yes |
| `forgotten` | the record | a record purged from its ledger, or an id minted by a delivery of one | yes |
| `stale` | the record | a record marked removed, or a minted id removed, gone or kept by an undo | yes |
| `unconfirmed` | the record | a record that never confirmed a delivery (pending, held or failed, no version), or a minted id still in intent or pending | yes |
| `drifted` | the record, at another version | a delivered record at another version | yes |
| `unlisted` | not in the read | a delivered record, or a live minted id, that storage holds: the index has not caught up, or it is outside the query | yes |
| `foreign` | the record | nothing; another identity created it | no |
| `superseded` | the dataset | a minted id a later delivery replaced, kept live on purpose | no |
| `tracked` | the record | a delivered record at this version, or a live minted id | no |
| `gone` | nothing | nothing: listed before, and no ledger expects it (or the build read nothing from storage) | no |
| `unreconciled` | the record | not compared yet: a build listed it and no reconcile has run since | no |

Every finding keeps the ledger and record, or the minted id, it rests on, and why in a line (`the ledger holds version
3, OSDU serves version 4`). A raised finding is one the report counts as a disagreement. What to do about each is in
the [orphans guide](../guides/finding-orphans.md#4-act-on-each-finding).

## What a build does

1. **Read.** Each inventory is read whole into a stage, 10,000 ids a write. A read that fails part way merges nothing:
   an id missing from a complete read is marked gone, so a partial read would mark live records gone. A run whose
   process stopped is closed failed by the next run of the inventory, which discards its stage.
2. **Merge**, in one transaction under a lock per inventory: new ids are added (`unreconciled` until compared), changed
   ones rewritten, ids served again brought back, and ids the read did not list marked gone, never deleted. A second
   build of the same inventory waits for the lock and, after ten minutes, fails with `Another build of the inventory held
   it for ten minutes; this build merged nothing.`
3. **Versions**, for `versions: all`: the version list of every record that is new or moved, `concurrency` at a time.
4. **Reconcile**: the owners, the finding of every id served, then up to `maxMissingChecks` ids a ledger expects that the
   read did not list, read by id from storage (`missing` when storage does not hold it, `unlisted` when it does). With
   `maxMissingChecks: 0` nothing is read from storage and every id no longer listed is `gone`. A build that reaches the
   bound logs that more may be missing.

Inventories run one after another; one that fails leaves the others to complete, and the run ends failed naming it.

## What a removal does

A removal is a `remove` run, queued from the inventory's page in the GUI (`POST
/api/v1/delivery/inventories/{partition}/{inventoryId}/removals`) or run with `sqlflow run --operation remove`. Before it
reads anything it is refused unless the flow allows the finding and the scope, `confirm` names the run's partition, the
inventory has been reconciled, and the inventory holds as many ids of the finding as `expected` (or a row of every id
named). The page and the API also refuse while a run of the flow is queued or running.

It then reads the ids 500 at a time, in order. Each id is checked again just before it goes, and left in OSDU, skipped
with why, when the inventory no longer finds it so, the ledgers give it another finding now, storage serves another
version than the inventory listed, or (an orphan) storage says no owner created it; an id storage no longer holds is
`gone`. The rest are removed through the flow's `source`: a soft delete 500 ids a request (a chunk answered 207, or refused
whole, is sent again one id at a time, so every id has its own outcome), or a purge one id at a time.

Every chunk is recorded before the next is read: each id's outcome (`removed`, `gone`, `skipped`, `failed`) with its
version and why, the inventory's rows marked gone at once, and, for a stale record, its own ledger's removal (an attempt
naming who asked). A removal stopped part way keeps what it did. OSDU refusing every id of a chunk with 401 or 403 stops
the removal there: `OSDU refused the removal (403) of every id of a chunk: the flow's source credentials must be an owner
of the records (users.datalake.editors, and in each record's owners ACL). Nothing more was asked of it.` The removal is
an `inventory-remove` activity of the audit trail.

## What a run returns

A build's or reconcile's result is the flow, the partition, how many inventories completed and failed, and per inventory
its kind, status, run number, ids listed, added, changed, gone and served again, versions read, ids checked in storage,
counts by finding, the owners and how they were known, pages, requests and error. A plan's result is, per inventory, the
query, how it reads, which versions, the records it would read, the kinds a storage read expands to, and its problems. A
removal's result is its number, the finding, the scope, how many ids were asked for, and how many were removed, already
gone, skipped and failed, with why it stopped.

## Lineage and the GUI

An inventory flow reads each inventory's kind on its platform and partition and writes nothing another flow reads, so
lineage orders it after the delivery flows writing those kinds ([lineage](../concepts/lineage.md)). An inventory flow's
pipeline has an **Inventories** tab. **OSDU**, **In OSDU**, **Inventories** lists every inventory with what its last
reconcile raised; an inventory's report has its counts by finding over a grid of ids, the tabs Ids, Runs and (for a flow
that allows removal) Removals, a lookup by OSDU id, and a CSV export.

## Errors when the document is read

| Message | Cause |
| --- | --- |
| `inventories must list at least one inventory; an inventory flow without inventories has nothing to read.` | No `inventories`. |
| `inventories[0] 'A': query narrows a search, and the flow reads storage (source.read: storage), which lists every record of a kind and takes no query. Take the query out, or read through search.` | A query on a storage read. |
| `source.read is 'index'; it is search (the index, a viewer's entitlements, the default) or storage (every active record, the storage service's admin role).` | An unknown read mode. |
| `inventories[0] 'A': versions is 'every'; it is latest (the latest version of each record, the default) or all (every version storage keeps, read for what is new or moved).` | An unknown `versions`. |
| `removal.findings[0] is 'missing'; the ids an inventory may remove are those of orphan, stale, forgotten: what OSDU serves that no ledger holds live. Every other finding is acted on through its ledger.` | A finding that is not removable. |
| `maxMissingChecks is 2000000; it is how many ids a ledger expects that one build reads from storage to tell missing from merely unlisted, between 0 (none) and 1,000,000.` | Out of range. |
| `source.purgePath '/api/storage/v2/records' must name the record it acts on as {id}, as '/api/storage/v2/records/{id}' does.` | A record path without `{id}`. |

## Errors when it runs

| Message | Cause |
| --- | --- |
| `Inventory 'WellLogs' has never been built in 'dev', so there is nothing to reconcile yet; build it first.` | `reconcile` before a build. |
| `Inventory flow 'welldb-06-inventory' allows no removal: its document declares no 'removal', so it only reads OSDU. ...` | `remove` on a flow without `removal`. |
| `Inventory flow 'welldb-06-inventory' allows soft deletes only; removal.purge: true lets it purge, which destroys every version for good. Nothing was removed.` | `scope: everything` without `purge: true`. |
| `The removal confirms partition 'test', and inventory flow 'welldb-06-inventory' reads 'dev'. Nothing was removed.` | `confirm` names another partition. |
| `Inventory 'WellLogs' holds 41 orphan id(s) now, and the removal was asked for 37: what it holds changed since it was shown. Nothing was removed; look at it again and ask again.` | The inventory changed since the operator looked. |
| `Storage no longer knows the cursor of its listing of <kind> part way through, so the listing cannot be read whole; the next build reads it again from the start.` | A storage listing lost its cursor. |
