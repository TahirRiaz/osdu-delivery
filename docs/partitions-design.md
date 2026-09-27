# Partitions: one catalog, several OSDU partitions

One OSDU Delivery catalog delivers to several OSDU data partitions (dev, test, prod) from the same flows and the same
mappings. A cache flow names the partitions it builds a cache for, a delivery flow names the partitions it may deliver
to, and every run targets exactly one of them. The partition is the key that ties the three together: a run for `test`
reads the cache built for `test`, delivers with the `data-partition-id` `test`, mints ids that say `test`, and keeps its
records in a ledger of their own.

This document is the specification of stage 1. Stage 2 (the global partition switcher in the workbench title bar, and
the cache page built around it) builds on it.

## 1. Why

Before this change the partition was never declared. Every flow wrote `data-partition-id: ${env:OSDU_DATA_PARTITION}`,
so the partition a flow served was whatever the host resolving that variable held:

- A cache definition's identity was `repo/flow/type`, so a catalog kept one partition per cache flow. When the control
  plane's variable changed, the next repository sync moved the flow's definitions to the new partition without a word.
- The delivery ledger is keyed by `(FlowId, DeliveryKey)`, and `FlowId` is derived from the flow's ledger name alone.
  Pointing a delivery flow at another partition made it plan against the first partition's records.
- One run resolved the partition three separate times: the target header (where records go and which cache is read),
  the mapping's `dataPartition` parameter (the prefix of every id it mints), and the cache flow's header (which cache is
  filled). Nothing made them agree.

## 2. Documents

### 2.1 Partition names

A partition is named by its OSDU `data-partition-id`, written literally: letters, digits, underscore, hyphen and dot,
at most 200 characters (the cache scope's rule). A reference (`${env:...}`) is refused: the name is a key, and a key a
host resolves is exactly what this change removes. Two names of one list may not differ only by case.

### 2.2 Cache flows

```yaml
flowType: cache
name: recall-lookups-00-cache
partitions: [dev, test, prod]        # the partitions this flow builds a cache for
source:
  connection: ${env:OSDU_DATA_DB}
types:
  - table: OsduData.arc.CacheCurveDictionary
    name: CurveDictionary
    key: mnemonic
    fields: [log_curve_type_id, log_curve_main_family_id, log_curve_family_id]
  - table: OsduData.arc.CacheRecallUnits
    name: RecallUnits
    key: source_unit
    fields: [osdu_unit]
    partitions: [dev, test]          # built only for these; each must be one of the flow's
```

- `partitions` lists the partitions the flow builds a cache for, at least one and at most 64. With it, `source.headers`
  may not name `data-partition-id` (the engine sets it for each partition), and `parameters` may not declare `partition`
  (the run value of that name picks the partition).
- A type may narrow the flow's list with its own `partitions`. A type without one is built for every partition of the
  flow.
- A type name is unique per partition, not per flow: two entries may share a name when their partitions do not
  overlap, so prod can read its CurveDictionary from another table than dev.
- A flow without `partitions` is read exactly as before: one partition, from `source.headers.data-partition-id`.

### 2.3 Delivery flows

```yaml
flowType: delivery
name: recall-welllog-03-header-delivery
partitions:
  - name: dev
    keepLedger: true                 # the records this flow delivered before it named its partitions went to dev
  - test
```

- `partitions` lists the partitions the flow may deliver to, at least one and at most 64. Each entry is a name, or a
  name with settings. With it, `target.headers` may not name `data-partition-id`, `render.parameters` may not set
  `dataPartition` (the engine sets both from the run's partition), `parameters` may not declare `partition`, and a flow
  naming more than one partition may not pin `render.cacheVersion`, because a version is a version of one partition's
  cache. A partition's ledger name (`<ledger>@<partition>`) has to fit the ledger's width.
- `keepLedger: true` says which partition keeps the ledger the flow kept before it named its partitions (section 4). At
  most one partition keeps it.
- Every interface of a source serves the source's partitions.
- A flow without `partitions` is read exactly as before.

## 3. Runs

A run of a flow that names its partitions targets exactly one of them. The partition is the run value `partition`, set
from the trigger dialog (a dropdown of the flow's partitions), from a schedule's `values:`, from `--set partition=<name>`
on `sqlflow run` and `sqlflow trigger`, or defaulted when the flow names only one; the module's own verbs (`check`,
`preview`, `fixtures`, `records`, `cache`) take `--partition`. The executor takes the value off the run's values before
anything else reads them, so it never reaches the flow's parameters, its watermarks or a submission's scope. A run naming
a partition the flow does not list is refused before it starts, naming the flow's partitions; a run of a flow with
several partitions that names none is refused the same way; a run of a flow that names none and names a partition is
refused, unless the flow declares a parameter of that name, which then takes it. A fan-out member of a bound run carries
its partition, so every member works in the partition its coordinator does.

The engine binds the flow to the run's partition before anything else reads it. The bound flow is the document as if it
had been written for that one partition:

- `target.headers.data-partition-id` (a cache flow's `source.headers.data-partition-id`) is the partition;
- the mapping's `dataPartition` is the partition, written literally, which the kind supplies to a mapping that declares
  it, so every id and reference it mints names it (the render context holds the value, never a reference, so a flow
  moving from `${env:OSDU_DATA_PARTITION}` to the partition it resolved to renders every record to the same hash);
- the cache the mapping reads is the partition's;
- the ledger identity is the partition's (section 4);
- every `${env:NAME}` the flow names resolves with the partition's values of the central configuration first
  (section 5), so the endpoint, the credentials, the legal tag and the ACL groups are the partition's.

Everything downstream (planning, rendering, the protocols, the cache capture and merge, preflight) works on the bound
flow and needs no knowledge of partitions. An unbound flow that names its partitions has no ledger identity, no cache
scope and no ledger name: asking for one throws, so a path that forgot to bind fails loudly rather than reading another
partition's ledger.

A mapping's fixtures are captured against one partition's cache, and render against that partition's cache wherever the
mapping runs: the partition a fixture's `dataPartition` names (its own, or `fixtureDefaults`), at the current version of
that partition's cache. A mapping delivered to several partitions keeps one set of fixtures, and a run in another
partition fails its preflight, naming each fixture, while the partition the fixtures are written for holds no cache
version.

## 4. Ledger identity

A flow without partitions keeps `FlowId.Of(ledgerName)`, unchanged: no existing record moves.

A flow with partitions keeps one ledger per partition: `FlowId.Of(ledgerName, partition)`, derived in a namespace of its
own so it can never equal another flow's id, and named `<ledgerName>@<partition>` wherever a ledger is shown. The
partition marked `keepLedger` keeps `FlowId.Of(ledgerName)` and its name, and with them every record the flow delivered
before it named its partitions. For a source with interfaces the rule applies per interface, each keeping its own ledger
name. The read model of interfaces (`osdu.Interface`) keeps one row per interface and partition, with the partition, so a
record leads to its pipeline, interface and partition, and a task queued for it acts in that partition.

Two guards keep the ledgers apart:

- A flow that names its partitions while its pre-partition ledger still holds records, with no partition keeping it, is
  refused before any run starts, naming the record count and the partitions those records were delivered to (read from
  their OSDU ids). Starting that partition's ledger empty would deliver every record again as new.
- The kept ledger may hold only records delivered to the partition that keeps it. A record whose OSDU id names another
  partition refuses the run, naming the count per partition.

Both are checked when a whole run starts (not a re-run of a submission, a record-scoped run or a fan-out member, which
the whole run that started them already passed), and a check that passed is remembered by the host, so a flow that
passes is read once per host rather than once per run.

## 5. Central configuration per partition

A property of the central configuration (`osdu.ConfigProperty`) may be set for the whole control plane, for one
repository, for one partition, or for one repository and partition. For a run targeting partition P, the effective value
of a name is taken from the first that sets it:

1. the repository's value for P,
2. the control plane's value for P,
3. the repository's value,
4. the control plane's value.

A value set for a partition always wins over one that is not, so an estate-wide `OSDU_URL` can never send a test run to
the dev platform. A run of a flow without partitions takes the values of 3 and 4, as before.

A run is queued carrying the values set for no partition (`references`) and every partition's own
(`partitionReferences`), and binds to its partition on the node, which is also how a cache run of every partition gives
each partition its own. A node task queued for one record or one flow (a read back, a source read, a preview, a delete, a
probe) carries the effective values of the one partition it acts in, as its `references` argument. A partition is always
an id, never a reference, in the configuration as in a document.

## 6. Cache

- The sync keeps one cache definition per flow, type and partition, marked as declared by a flow that names its
  partitions. The identity of a definition of a flow without partitions stays `repo/flow/type`; with partitions it is
  `repo/flow/type@partition`. A flow moving to partitions keeps its partition's cache: its members and versions stay where
  they are, because a cache is keyed by the partition and the partition is the one its header resolved to. A partition
  the flow stops naming lets go of what the flow held in it, and of nothing it holds elsewhere.
- A refresh run names one partition and captures for it. A refresh that names none (a schedule's fire, a run from the
  CLI without the value) builds every partition the flow names in turn, each with its own header and configuration: a
  partition that fails leaves the others refreshed, and the run fails naming it, with each partition's result.
- Lineage declares one cache type node per partition, and a delivery flow naming its partitions reads and writes in each
  of them. Lineage keeps a partition as written, so flows meet on one node when they name a partition the same way: an
  estate that moves to named partitions moves all its flows, and a retrieval flow, which names none yet, writes the
  partition in its header.

## 7. GUI

A partition is always chosen from a dropdown of declared partitions, never typed: the trigger dialog of a delivery or
cache flow, the cache page's partition picker, and the partition picker of a delivery flow's views. The dropdown lists
the flow's (or the catalog's) declared partitions, and is shown even when there is one, because which partition a view
is about is what an operator needs to know before acting.

- The trigger dialog gives a kind's fields the pipeline the run is launched for (`TriggerFieldsProps.pipelineId`, a
  generic extension point of SQLFlow's GUI), and the delivery fields read the flow's partitions through it. A delivery
  run of a flow naming several partitions cannot be submitted until one is picked; a cache refresh offers "every
  partition, one after another" beside each partition. A run being repeated opens on the partition it ran in.
- A delivery flow's pages (overview, records, submissions, preview) read one partition at a time, chosen in the URL
  (`?partition=`), and every count and action is that partition's. The pipeline header lists the partitions; the Delivery
  overview's card adds them up and lists them; a record names its partition and links to the flow's page in it; the
  Records lookup names a hit's partition and offers each partition's ledger as a flow to narrow to.
- The cache page's picker lists every partition a cache flow fills; Refresh on a flow that names its partitions opens on
  the partition in view, and the definition says which partitions each flow builds.
- The mapping builder offers a flow that names its partitions once per partition, and checks with the entry for the
  partition whose cache is picked.

## 8. API and CLI

The pipeline-level delivery routes take `?partition=`: required for a flow with several partitions (400 `Partition
required`), defaulted for one with a single partition, and refused for a partition the flow does not name (400 `No such
partition`). Two routes read the flow whole without one: the interface listing lists every interface in every partition,
each row naming its partition, and the counts add every partition up and list the partitions. Record routes already name
the ledger's flow id and need no partition; a record, a submission, a lookup hit and a ledger choice name their partition.
The configuration routes take `?partition=` to set, remove and read a partition's values. The CLI's run and trigger take
the run value as `--set partition=<name>`; `check`, `preview`, `fixtures`, `records`, `cache` and `config` take
`--partition`.

## 9. Compatibility

- Documents without `partitions` behave exactly as before, down to their ledger ids and cache definition ids.
- Retrieval flows name no partitions in stage 1: a retrieval keeps its `data-partition-id` header, and refuses the
  `partition` run value.
- Moving a flow to `partitions` is a change in the repository, never made by the engine. The Recall estate moves with
  `partitions: [dev]` on its cache flows and `dev` keeping the delivery flow's ledger, which changes nothing it
  delivers.
