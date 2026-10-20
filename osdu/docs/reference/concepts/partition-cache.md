---
id: delivery-concept-partition-cache
title: "The partition cache: one versioned cache per OSDU partition, filled by cache flows and read by every delivery"
type: concept
summary: "How a partition's cache works: versions, retention, cache flows sharing one cache, deliveries keeping a type current, change tags, rollout, system properties."
keywords:
  - partition cache
  - cache version
  - current version
  - cache refresh
  - cache retention
  - retentiondays
  - pruned version
  - purge cache history
  - prune history
  - several cache flows
  - onchange approve
  - approve a cache change
  - update tag
  - cache rollout
  - system properties
  - keywordlower
  - render.cacheversion
  - cache import
  - osdu.cacheversion
  - cache warm-up
  - first render slow
  - keepcurrent
  - keep a type current
  - cache step
  - cache kept current
  - delivery holds a cached record
related:
  - delivery-flow-cache
  - delivery-flow-dictionary
  - delivery-cli-cache
  - delivery-concept-change-detection
  - delivery-concept-partitions
  - delivery-concept-ledger
  - delivery-guide-lookup-table-cache
  - delivery-guide-reference-data-cache
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheScope.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheDeclaration.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheOrigin.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/SystemProperty.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheRetention.cs
  - osdu/src/SqlFlow.Delivery/Catalog/OsduCacheStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/CacheWarmupService.cs
  - osdu/src/SqlFlow.Delivery/Catalog/CacheVersions.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryInterfaceCatalog.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryCachePruneEndpoints.cs
  - osdu/gui/src/features/delivery/CachePruneButton.tsx
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Engine/CacheExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheRefresh.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheKeeper.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SnapshotBuilder.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheImpact.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/SystemPropertyCapture.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/CacheUpdateRolloutService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryModuleOptions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryCacheStreams.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/gui/src/features/delivery/DeliveryCachePage.tsx
  - osdu/gui/src/module.tsx
---

# The partition cache: one versioned cache per OSDU partition, filled by cache flows and read by every delivery

OSDU Delivery keeps one cache per OSDU data partition. Every [cache flow](../flow/cache.md) that serves a partition
writes into that partition's cache, and every delivery flow that delivers to the partition reads from it while it
renders records: the reference data a mapping resolves ids against, the lookup tables it translates source values
through, the dictionaries and dimension values it reads. The cache is versioned, so every delivered record can say
exactly which cached values it was built from, and a later change to one of those values can find and redeliver exactly
the records it reaches. It lives in the module's database (the `osdu` schema), never in a repository: the cache flow
files define what is cached, and their runs fill it. Its history is bounded: every refresh prunes the records of the
versions the partition no longer needs ([Retention](#retention)).

## One cache per partition

A cache is keyed by the partition the flows reach, after references are resolved: a flow naming `partitions: [dev]`,
one whose `source.headers.data-partition-id` is `dev`, and one whose header is `${env:OSDU_PARTITION}` resolving to
`dev` all fill the same cache. A capture, an import, the repository sync and a render resolve the partition the same
way, so a flow can move from a header to named partitions and keep the cache it had. A partition name is letters,
digits, underscore, hyphen and dot, at most 200 characters, or a `${env:...}` or `${keyvault:...}` reference.

A delivery run reads the cache of the partition it delivers to ([partitions](partitions.md)). Its mapping reads each
type by name (`$cache.UnitAlias`, `$cache: UnitOfMeasure.id`), whichever cache flow filled it and whatever its origin.

## Versions

Each refresh that changes what a partition's cache holds writes a new version of the whole cache:

- **The label is the capture instant,** `20261009T012000Z`; when two captures of one partition share a second, the
  sequence is appended (`20261009T012000Z-7`).
- **The newest version is always current.** Every delivery renders against the current version unless its flow pins an
  earlier one with `render.cacheVersion`.
- **Every version stays listed,** because the render context of a delivered record names the version it was rendered
  against. A version records the cache flow and run that wrote it, who asked, where its content came from and what it
  changed. Its records are kept for the partition's [retention](#retention), then pruned.
- **A version that would change nothing is not written.** A version label enters the render context, so writing one for
  a capture that found nothing new would re-render every record built from the cache. A refresh that finds exactly what
  the current version holds writes nothing, and nothing renders again: refreshing often is free.
- **Each type carries its own content hash,** how it compares with the version before (`added`, `changed` or
  `unchanged`), and the version its content dates from. A version moves when any type or system property moves; a type
  that only rode along keeps its hash, and nothing built from it is analysed again.
- **Every load is checked.** A version whose records no longer match the hash it was written with is refused (`Version
  <v> of the cache of partition '<p>' does not match the content hash it was written with ...: its records were altered
  after the version was written, so nothing renders against it.`).
- **A version is read into memory once per process, ahead of the renders that need it.** A render reads the whole
  version it renders against; a process keeps the eight versions asked for last, and callers asking for a version while it
  is read wait on that one read. A version of a few hundred thousand records takes seconds to read and hundreds of MB to
  hold, so the control plane and every node read each partition's current version at start, and a version a refresh
  writes within `PollSeconds` of it becoming current (the [cache warm-up](control-plane.md#background-services)): the
  first run, Render or Preview after a start or a refresh finds it in memory. The warm-up logs `Version <v> of the cache
  of partition '<p>' (<n> records) is in memory, <s> s after it was asked for`.

In the database a version is a row of `osdu.CacheVersion`; the records are kept by version range in `osdu.CacheItem`
(a record is stored once and a new row is written only when it changes, arrives or leaves); `osdu.CacheMember` says
which cache flow's last capture holds each record, and which delivery landed it when a delivery keeps its type current
(`delivery:<ledger name>`) ([the ledger](ledger.md)).

## Retention

A version's records serve whoever reads that version: the Cache page browsing or comparing it, and a delivery flow
pinning it. A delivered record needs none of them once it is delivered: the values its render read stay with it in the
ledger (its cache set), and a change tag keeps the values before and after. So every refresh and every import of a
partition's cache, whether or not it writes a version, ends by pruning the records of the versions the partition no
longer needs. It keeps the records of:

- **the current version,** which every delivery renders against;
- **the version the current one replaced,** which a run that resolved the current version just before it moved may still
  load, and which the latest change is compared with;
- **every version a delivery flow pins** with `render.cacheVersion`;
- **every version replaced less than the retention ago.** A version stops being current when the next one is captured,
  and its retention runs from that instant.

The retention is the cache flow's `retentionDays` ([cache flow](../flow/cache.md#retentiondays)), 7 days when it declares
none. When several cache flows fill one partition, the partition keeps the longest any of them declares.

Pruning a version removes only the stored rows no kept version shares: a record that did not change since is one row,
kept for the versions that still hold it. Before any row goes, the version's changes (how many records of each type
changed, arrived and left) are recorded on its row, so the History tab reads exactly as it did. A pruned version then
stays listed, marked `pruned` with when, and its records can no longer be read:

| Where | What a pruned version answers |
| --- | --- |
| A delivery flow pinning it | The run fails: `render.cacheVersion pins version <v> of the cache of partition '<p>', whose records the cache's retention pruned at <time>, so nothing can render against it. Pin a version the cache still holds ('sqlflow cache list <p>' lists them), or remove render.cacheVersion to render against the current version.` The repository sync warns about such a pin too. |
| The Cache page | History lists it with its counts; its records and its comparison with the version before are not offered. |
| The API | `GET /cache/items` and `GET /cache/diff` answer `410 Gone` naming the version and when it was pruned; `GET /cache/versions` and `/cache/history` list it with `prunedUtc`. |
| `sqlflow cache list` | The version is marked `pruned`, with when. |

Pruning is safe beside everything else that touches the cache. It removes only rows a version up to the current one
closed, so a merge running beside it loses nothing, and it is repeatable: a pass cut short is finished by the next
refresh. A version a delivery flow pins is kept from the first refresh after the pin is recorded, by a repository sync
or by the control plane when it starts, which describes again from the catalog's copies every delivery flow described
before pins were recorded. While an active delivery flow that may read the partition has no recorded pin (after an
upgrade, until the control plane has started once), the retention prunes nothing and names the flows it waits on. The
control plane also makes inactive the interfaces of a repository the catalog holds no delivery flow of any more, so a
repository removed from the catalog leaves nothing to wait on. A retention that fails is logged and reported in the
run's result; the refresh itself stands, and the next one applies the retention again.

### Purging the history by hand

An admin can purge a partition cache's history now, rather than wait for its next refresh: **Prune history** on the
Cache page's History tab, `POST /cache/prune` ([the API](api.md)), or `sqlflow cache prune` ([the CLI](../cli/cache.md)).
It is the same pass, with the days the admin names in place of the partition's retention, whatever its cache flows
declare: the current version, the one it replaced and every pinned version keep their records whatever the days, and 0
keeps those alone. The page starts from the partition's retention and shows what the days chosen prune (the versions,
and how many stored rows) before anything goes; `POST /cache/prune/preview` and `--dry-run` say the same. The purge is
an admin's, since what it removes cannot be brought back; a preview is anyone's who may operate.

Every pruned version records when and by whom: the requester of the refresh, the account of the import, or the admin who
purged. History shows it on the version's `pruned` mark, `sqlflow cache list` beside it, and the API as `prunedBy`.

In the database, `osdu.CacheDefinition.RetentionDays` holds what each flow declares, `osdu.CacheVersion.PrunedUtc` and
`PrunedBy` when and by whom a version was pruned and `ChangesJson` what it changed, and `osdu.Interface.CacheVersion` the
version each delivery interface renders against (migrations `CacheRetention`, module version 1.40.0, and
`CachePruneActor`, module version 1.41.0).

## Several cache flows, one cache

Several cache flows may fill one partition, in one repository or several, so each project declares the reference data it
needs without copying another project's file. The cache holds the union of what they declare:

- **Types are shared by name.** Flows declaring a type under the same name share one type. A refresh of any of them
  fetches every path any synced flow of the partition declares for the type, so a field one project asks for is there
  for every pipeline reading the partition, and every flow's query adds records to it.
- **The newest capture of a record wins.** A captured record replaces what the cache held for it. A record the capturing
  flow no longer finds leaves the cache only when no other flow's last capture still holds it, and no delivery holds it
  while OSDU still does ([Deliveries keep a type current](#deliveries-keep-a-type-current)). Types a capture does not
  cover stay as they are, except a type no synced flow declares any more, which the merge removes.
- **A lookup table has one owner.** A table, dictionary or dimension type is declared by one cache flow of a partition:
  its capture replaces the whole type, so a second flow declaring it would replace the first one's rows.
- **Declarations must agree.** Two declarations of one type that disagree on the entity type, or cache one field name from
  different paths, are refused. The repository sync leaves the declaration synced second out of the catalog with a
  warning naming both flows, and a refresh of a flow that disagrees fails before it captures anything.
- **One platform per partition.** The sync warns when the cache flows of a partition search different endpoints.
- **Refreshes of one partition merge one after another.** A refresh takes the partition's merge lock, so a second waits for
  the first to commit and merges onto the version it wrote. After five minutes of waiting it gives up, keeps nothing of
  its capture, and says to run the refresh again. Refreshes of different partitions never wait on each other.
- **onChange is the strictest.** A type's changes wait for approval when any flow declaring it says `onChange: approve`.

When a flow stops declaring a type, the sync deletes that flow's hold on the type's records, so a later capture by
another flow lets go of the records only the first one kept.

## What a refresh does

1. Resolves the partition and reads every synced declaration of it; a conflict with another flow stops here.
2. Captures each declared type from its origin: OSDU types through the search service (every path the partition keeps
   for the type), table types from the ingestion database, dictionaries from the repository tree, dimension types from
   the dimension's last build. A type that cannot be read whole fails the refresh before anything is written. A run
   that names `types` captures those alone.
3. Asks OSDU's storage service about the records of each OSDU type that a delivery holds and the search did not find
   ([below](#deliveries-keep-a-type-current)), and reads the partition's system properties, when it reached OSDU
   ([System properties](#system-properties)).
4. Merges the capture into the current version and writes the next version, unless nothing moved.
5. Applies the partition's [retention](#retention): prunes the records of the versions it no longer keeps.
6. Compares each type that moved with the version before, and tags the changes that delivered records were built from.

A run over several partitions does this for each in turn; a partition that fails leaves the others refreshed.

## Deliveries keep a type current

A refresh brings the cache up to date on the cache flow's schedule. Within one schedule, a delivery that lands the
records a later flow looks up (the units the wells refer to, the wellbores the logs belong to) would leave them out of
the cache until the next refresh, and the later flow would render without them: a reference written unverified, or a
record held for a reference the cache does not hold. A type of OSDU records declared `keepCurrent: true` closes that gap:
every delivery run of a kind the type's `kind` matches, into a partition the type is built for, ends with a cache step
that puts what the run landed into the type.

```yaml
types:
  - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
    fields: [data.Code, data.Name]
    keepCurrent: true
```

The step:

1. **Runs as the run ends,** after its last delivery, its fan-out members' too, and after the sweep of unfinished
   deliveries. A drain of the whole flow ends with it as well; a run that delivered nothing does not take it. It runs
   whether or not the delivery's own mapping reads the cache: the cache is the one of the partition the flow delivers to.
2. **Takes what the run landed from the ledger:** the records the flow delivered since the run started (a minute earlier,
   for the clocks of the nodes a fanned-out run delivers on), at most 50,000. A run landing more is a bulk load: the step
   reads nothing, and says to run the cache flow's refresh, which reads them more cheaply.
3. **Reads them back from OSDU's storage service by id** (`POST /api/storage/v2/query/records`, 100 at a time), not the
   search service, whose index holds a record a moment after it is written. A record storage does not return is counted
   as missing and left out.
4. **Keeps of each record the paths the partition keeps for the type,** as a refresh does, and merges them into the
   partition's cache as the delivery's capture, held under `delivery:<ledger name>`. The merge adds and replaces records
   and takes none away. When anything moved it writes a version, which becomes current, and tags what the changes reach
   as a refresh does ([below](#from-a-cache-change-to-osdu)): a reference a record wrote unverified that the cache now
   holds is tagged `found`.

The step's failure is not the run's: what the run delivered stands in OSDU and in the ledger, the run's log warns that
the cache was not brought up to date, and the next refresh of the cache flow brings the records in. The run's result
carries the step under `cacheKept` (the partition, the types, the records landed, read and missing, the version written,
and a sentence saying it), and the run page shows the sentence under **Cache kept current**:

```text
The cache of partition 'dev' (UnitOfMeasure) holds the 12 record(s) the run landed as version 20261020T101500Z.
```

A record a delivery holds stays in the cache when a refresh's search does not find it, since the search index may not
have caught up with it yet. The refresh asks storage about those records alone: one storage still holds stays, and one it
no longer holds (deleted, or not readable with the cache flow's credentials) leaves the cache, unless another cache flow's
last capture still holds it.

- **Any flow declaring the type decides.** When several cache flows declare one type, the deliveries keep it current
  when any of them says `keepCurrent: true`, and the step keeps every path any of them keeps for the type.
- **OSDU records only, and every one of the kind.** A lookup table (a table, a dictionary or a dimension) is captured
  whole by its cache flow and cannot be kept current; a type narrowed by a `query` cannot either, since a delivered
  record the query does not match would stay in the cache.
- **Lineage orders it.** A delivery of a kind that keeps a type current writes that type, so a flow reading it runs after
  the delivery in the same schedule ([lineage](lineage.md)). Flows in other schedules are not waited for.

## From a cache change to OSDU

Every delivered record points at the set of cached values its render read: the record a value was found by, each value
written into the document, each path read that held nothing, each key a lookup table did not list, and each id written as
an unverified reference. A refresh compares
the new version with the one it replaces over those sets (thousands of them, never the records themselves) and writes
one tag per change: the partition, the type, the cached record, the path, the value before and after, the versions it
moved between, and how many delivered records it reaches.

| Change | Meaning |
| --- | --- |
| `changed` | A value records read now reads differently. |
| `removed` | A cached record records used is no longer in the cache. |
| `unmatched` | A record no longer matches by the value records found it by. |
| `listed` | A lookup table now lists a key records looked up and found no row under, or an OSDU type now holds a record by a value records found none by. |
| `relisted` | The rows a key finds are no longer the ones records read every one of. |
| `found` | The cache now holds a record that records reference as an unverified id. |

What a tag does depends on the type's `onChange`:

- **`auto`** (the default): the tag is approved as it is written, and each affected record is delivered again on its
  flow's next run.
- **`approve`**: the affected records are held back; a plan skips them as `awaiting approval` ("a cache change is tagged
  against this record and is waiting for approval"), so OSDU keeps the documents it has, until someone decides on the
  Cache page. Approving lets the rollout carry the change out; rejecting leaves the delivered documents alone. If the
  value moves again after an approval and before the rollout carried it, the tag reopens, because the approval was for
  the value someone looked at.

A tag's status moves through `pending`, `approved` or `rejected`, `rolling` (its records are being marked), `delivering`
(all are marked and some flow has not rendered them again yet) and `applied`.

The control plane carries approved changes out in batches, so a corrected unit that reaches millions of records drains
at a set pace and resumes after a restart. The marking is metadata only: no payload is re-uploaded because a reference
value changed. A held or failed record is marked too, and its next plan renders it under the change; a `reverted` or
`deleted` record is not, since an operator's reversal, step back or removal decided what OSDU holds of it, and a tag does
not wait for one. A record held back while a change awaits approval keeps any request to plan it again (a release, a
redelivery) for the plan that runs once the change is decided.

| Setting (`Osdu:CacheRollout`) | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Turns the automatic rollout off; approved changes then wait. |
| `BatchSize` | `5000` | Records marked for redelivery per batch. |
| `BatchesPerPass` | `2` | Batches per pass, across all approved changes. |
| `PollSeconds` | `60` | Seconds between passes. |

## System properties

A refresh that reaches OSDU also reads the partition's system properties: the feature flags the indexer and the search
service report through `GET /api/indexer/v2/info` and `GET /api/search/v2/info` (their `featureFlagStates`). They are
kept with the version, apart from the cached records: they have no record id, and no mapping reads them. The engine
relies on one, `featureFlag.keywordLower.enabled`: where the indexer keeps a lowercased copy of every text property, a
search that finds no record exactly asks again regardless of case.

- A state reported for the partition wins over one reported for no partition; states for other partitions are ignored.
- A service that cannot be asked fails nothing: the refresh logs why and keeps what the cache knew of that service. A
  property the engine relies on that no service reports is recorded as unknown, and nothing relies on it being on.
- A changed state is a change of the cache, so the refresh writes a version even when every record is the same.
- A flow of lookup tables alone reaches no platform and reads none.

## Filling a cache without a platform

For work without OSDU, `sqlflow cache import <cache flow> --from-dir <dir>` merges type files into the cache of the
flow's partition as that flow's capture, and applies the partition's retention as a refresh does
([`sqlflow cache`](../cli/cache.md)). The files stand in for what a search of
the partition would return: every record is an OSDU record of the declared entity type in the flow's partition. A lookup
table is never imported: it is captured from its table, dictionary or dimension wherever the flow runs.

## Where to see it

- **The Cache page** (OSDU, Build, Cache) shows the cache of the partition picked in the title bar: the cache flow files
  that fill it, with **Cache files** and **Refresh now** (a **Refresh** menu naming the flows when several fill the
  partition, since a refresh captures what one flow declares); and four tabs. **Records** browses what a version holds, a type
  at a time, with how a mapping reads each row. **History** lists the versions and which types each changed, a pruned
  version marked as such, and offers an admin **Prune history**.
  **Deliveries** lists the changes with what they reach, the approvals waiting, and the records built without something
  the cache did not hold yet. **Setup** lists the cache flows, the types each declares, the retention each declares and
  the one the partition keeps, and the partition's OSDU feature flags.
- **A cache flow's pipeline page** opens on its **Cache versions** tab.
- **A delivery run's page** shows what its cache step did under **Cache kept current**, and the Setup tab marks a type
  the deliveries keep current. A cache flow's run dialog lists its types, to refresh some of them.
- **`sqlflow cache list <partition>`** prints the partition's retention, the versions, newest first, each pruned one
  marked, and the current version's system properties.
- **The API** (`/api/v1/delivery`): `GET /caches`, `/cache/versions`, `/cache/items`, `/cache/history`, `/cache/diff`,
  `/cache/tags`, `/cache/gaps`, `/cache/streams` (each type's upstream flows, from files to the cache flow, and who reads
  it), `POST /cache/tags/decide`, and `POST /cache/prune` with its preview ([the API](api.md)).

## Related

- [Cache flow](../flow/cache.md): the document that declares what a cache holds.
- [Change detection](change-detection.md): what else makes a record deliver again.
- [The ledger](ledger.md): where cache sets and tags are kept.
