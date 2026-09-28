# Partitions: one catalog, several OSDU partitions

One OSDU Delivery catalog delivers to several OSDU data partitions (dev, test, prod) from the same flows and the same
mappings. The partition is the key that ties a run together: a run for `test` reads the cache built for `test`, delivers
with the `data-partition-id` `test`, mints ids that say `test`, and keeps its records in a ledger of their own.

A flow says which partitions it works in along one of two paths:

- **Hard-coded.** The flow names its partitions under `partitions:`, or names its one partition in its
  `data-partition-id` header. It works in those partitions whatever the catalog holds.
- **Registry-driven.** The flow names neither. It serves every partition registered with the catalog, and a run picks
  one at run time: the one it names, or the registry's default. The same documents then deploy to every environment,
  and a new environment is a row in the registry rather than a change to every flow.

The partition registry (section 2.1) is the catalog's list of partitions and its default. The workbench's partition
switcher, the trigger dialog and the cache page all read it.

## 1. Why

Before partitions were declared, every flow wrote `data-partition-id: ${env:OSDU_DATA_PARTITION}`, so the partition a
flow served was whatever the host resolving that variable held:

- A cache definition's identity was `repo/flow/type`, so a catalog kept one partition per cache flow. When the control
  plane's variable changed, the next repository sync moved the flow's definitions to the new partition without a word.
- The delivery ledger is keyed by `(FlowId, DeliveryKey)`, and `FlowId` is derived from the flow's ledger name alone.
  Pointing a delivery flow at another partition made it plan against the first partition's records.
- One run resolved the partition three separate times: the target header (where records go and which cache is read),
  the mapping's `dataPartition` parameter (the prefix of every id it mints), and the cache flow's header (which cache is
  filled). Nothing made them agree.

Naming partitions in every flow fixed that, but tied each document to the environments it listed. The registry takes
that last step: a flow that names none runs in whichever registered partition a run picks.

## 2. Documents

### 2.1 The partition registry

The registry is the table `osdu.Partition` in the module's database: one row per partition, with what it is for, who
registered it and when, and a flag for the one default partition.

- A partition is named by its OSDU `data-partition-id`, written literally: letters, digits, underscore, hyphen and dot,
  at most 200 characters (the cache scope's rule). A reference (`${env:...}`) is refused: the name is a key, and a key a
  host resolves is exactly what this design removes. Names compare regardless of case, so a partition is registered once,
  and two names of one list may not differ only by case.
- At most one partition is the default, which a filtered unique index keeps. The first partition registered becomes it;
  another becomes it when asked. The default is removed only when it is the last partition, so a registry that holds
  partitions always has a default.
- Removing a partition deletes nothing kept under it: its caches, ledgers and runs stay. A registry-driven flow stops
  serving it, and its interfaces stop answering first at the next sync; its records still lead to their flow.
- The registry is kept on the Partitions page, through `/api/v1/delivery/partitions`, and with `sqlflow partition`
  (section 8). Registering, describing, changing the default and removing are admin work.
- The migration that creates the table seeds it with the partitions the catalog already knew (every cache version's and
  cache definition's scope, and every interface's partition, where each is a partition id), and makes the one it finds the
  default when it finds exactly one.

A hard-coded partition need not be registered: a flow that names it works in it either way. The registry settles only
what a document leaves open: which partitions a registry-driven flow serves, and which partition a run that names none
runs in.

### 2.2 Cache flows

```yaml
flowType: cache
name: recall-lookups-00-cache
partitions: [dev, test, prod]        # hard-coded; leave it out to build every registered partition
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
    partitions: [dev, test]          # built only for these
```

- `partitions` lists the partitions the flow builds a cache for, at least one and at most 64. With it, `source.headers`
  may not name `data-partition-id` (the engine sets it for each partition), and `parameters` may not declare `partition`
  (the run value of that name picks the partition).
- A flow that names neither `partitions` nor `data-partition-id` builds a cache for every registered partition. The same
  refusals apply to it.
- A type may narrow the partitions it is built for with its own `partitions`: some of the flow's, for a flow that names
  them, or any partitions, for a registry-driven flow (a type only prod holds). A type without one is built for every
  partition the flow serves.
- A type name is unique per partition, not per flow: two entries may share a name when their partitions do not
  overlap, so prod can read its CurveDictionary from another table than dev.
- A flow whose `data-partition-id` header names its partition is read exactly as before: one partition.

### 2.3 Delivery flows

```yaml
flowType: delivery
name: recall-welllog-03-header-delivery
partitions:
  - name: dev
    keepLedger: true                 # the records this flow delivered before it named its partitions went to dev
  - test
```

```yaml
flowType: delivery
name: recall-welllog-03-header-delivery
keepLedger: dev                      # registry-driven: no partitions, no data-partition-id header
```

- `partitions` lists the partitions the flow may deliver to, at least one and at most 64. Each entry is a name, or a
  name with settings. With it, `target.headers` may not name `data-partition-id`, `render.parameters` may not set
  `dataPartition` (the engine sets both from the run's partition), `parameters` may not declare `partition`, and a flow
  naming more than one partition may not pin `render.cacheVersion`, because a version is a version of one partition's
  cache. A partition's ledger name (`<ledger>@<partition>`) has to fit the ledger's width.
- A flow that names neither `partitions` nor `data-partition-id` serves every registered partition, under the same
  refusals; it may not pin `render.cacheVersion` either.
- `keepLedger` says which partition keeps the ledger the flow kept before it served partitions (section 4): `true` on
  the entry of a flow that names its partitions, at most one; `keepLedger: <partition>` at the top of a registry-driven
  flow. The top-level form is refused beside `partitions` and on a flow whose header names its partition.
- Every interface of a source serves the source's partitions.
- A flow whose `data-partition-id` header names its partition is read exactly as before.

## 3. Runs

A run of a flow that works in partitions targets exactly one of them. The partition is the run value `partition`, set
from the trigger dialog (a dropdown of the partitions the flow serves), from a schedule's `values:`, from
`--set partition=<name>` on `sqlflow run` and `sqlflow trigger`; the module's own verbs (`check`, `preview`, `fixtures`,
`records`, `cache`) take `--partition`. The executor takes the value off the run's values before anything else reads
them, so it never reaches the flow's parameters, its watermarks or a submission's scope.

The partition is settled in one place (`SourceDefinition.Resolve`, `CacheDefinition.ForRun`), the same for every host:

| The run names | Hard-coded `partitions` | Registry-driven |
| --- | --- | --- |
| a partition | that partition, which the flow has to name; it need not be registered | that partition, which has to be registered |
| none | the registry's default when the flow names it, else the flow's only partition, else refused | the registry's default, else refused |
| `*` (cache refresh only) | every partition the flow names, one after another | every registered partition, one after another |

A delivery run naming `*` is refused: a delivery acts in one partition. A refusal names the flow's partitions, or the
registered ones and how to register another. A run of a flow whose header names its partition that names a partition is
refused, unless the flow declares a parameter of that name, which then takes it. A fan-out member of a bound run carries
its partition, so every member works in the partition its coordinator does. The registry is read only when a document
leaves the partition open (a registry-driven flow, or none named for a flow that names several), so a hard-coded run
never waits on it.

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
flow and needs no knowledge of partitions. An unbound flow that works in partitions has no ledger identity, no cache
scope and no ledger name: asking for one throws, so a path that forgot to bind fails loudly rather than reading another
partition's ledger.

A mapping's fixtures are captured against one partition's cache, and render against that partition's cache wherever the
mapping runs: the partition a fixture's `dataPartition` names (its own, or `fixtureDefaults`), at the current version of
that partition's cache. A mapping delivered to several partitions keeps one set of fixtures, and a run in another
partition fails its preflight, naming each fixture, while the partition the fixtures are written for holds no cache
version.

## 4. Ledger identity

A flow whose header names its partition keeps `FlowId.Of(ledgerName)`, unchanged: no existing record moves.

A flow that works in partitions keeps one ledger per partition: `FlowId.Of(ledgerName, partition)`, derived in a
namespace of its own so it can never equal another flow's id, and named `<ledgerName>@<partition>` wherever a ledger is
shown. The partition marked `keepLedger` keeps `FlowId.Of(ledgerName)` and its name, and with them every record the flow
delivered before it served partitions. For a source with interfaces the rule applies per interface, each keeping its
own ledger name. The read model of interfaces (`osdu.Interface`) keeps one row per interface and partition served, with
the partition, so a record leads to its pipeline, interface and partition, and a task queued for it acts in that
partition. A registry-driven flow's rows follow the registry at each sync (section 6).

Two guards keep the ledgers apart:

- A flow that serves partitions while its earlier ledger still holds records delivered to one of them, with no
  partition keeping it, is refused before any run starts, naming the record count and the partitions those records were
  delivered to (read from their OSDU ids), and where to write `keepLedger`. Starting that partition's ledger empty would
  deliver every record again as new. Records delivered to a partition the flow no longer serves (one taken out of the
  registry) hold nothing back.
- The kept ledger may hold only records delivered to the partition that keeps it. A record whose OSDU id names another
  partition refuses the run, naming the count per partition.

Both are checked when a whole run starts (not a re-run of a submission, a record-scoped run or a fan-out member, which
the whole run that started them already passed), and a check that passed is remembered by the host, so a flow that
passes is read once per host rather than once per run.

The ledger is keyed by partition ([ledger.md](../osdu/docs/ledger.md), Partitions). Every ledger table (records, their
identities, attempts, submissions, work batches, leases, events, watermarks, activities, retrievals) carries the
partition's number first in its primary key and in every index that serves a listing, and a directory (`osdu.Ledger`)
holds the partition each ledger identity belongs to. A run registers its ledger in the partition it delivers to before it
writes a row of it, and a registration naming another partition than the directory holds is refused, naming both: a
header flow whose `${env:}` header resolves to another partition on another node stops before it mixes two partitions'
records. The upgrade places each existing ledger from its interface rows or its records' OSDU ids; one it cannot place is
adopted by its next run, unless its records were delivered elsewhere.

## 5. Central configuration per partition

A property of the central configuration (`osdu.ConfigProperty`) may be set for the whole control plane, for one
repository, for one partition, or for one repository and partition. For a run targeting partition P, the effective value
of a name is taken from the first that sets it:

1. the repository's value for P,
2. the control plane's value for P,
3. the repository's value,
4. the control plane's value.

A value set for a partition always wins over one that is not, so an estate-wide `OSDU_URL` can never send a test run to
the dev platform. A run of a flow whose header names its partition takes the values of 3 and 4, as before. This is what
lets one registry-driven flow reach a different platform per partition: the endpoint and credentials a document names
by reference resolve to each partition's own.

A run is queued carrying the values set for no partition (`references`) and every partition's own
(`partitionReferences`), and binds to its partition on the node, which is also how a cache run of every partition gives
each partition its own. A node task queued for one record or one flow (a read back, a source read, a preview, a delete, a
probe) carries the effective values of the one partition it acts in, as its `references` argument. A partition is always
an id, never a reference, in the configuration as in a document.

## 6. Cache

- The sync keeps one cache definition per flow, type and partition served, marked as declared by a flow that works in
  partitions. The identity of a definition of a flow whose header names its partition stays `repo/flow/type`; otherwise
  it is `repo/flow/type@partition`. A flow moving to partitions keeps its partition's cache: its members and versions stay
  where they are, because a cache is keyed by the partition and the partition is the one its header resolved to. A
  partition the flow stops serving lets go of what the flow held in it, and of nothing it holds elsewhere.
- A registry-driven flow is described in the partitions the registry holds when the sync runs, and warns when none is
  registered. The managed sync runs every poll interval whatever the commit, and every change to the registry through the
  control plane makes each repository source due at once, so a partition just registered is described within one sync.
- A refresh run builds the partition section 3 settles: the one it names, the default when it names none, or every
  partition the flow serves in turn when it names `*`. Built in turn, a partition that fails leaves the others
  refreshed, and the run fails naming it, with each partition's result.
- Lineage declares one cache type node per partition a flow names, keyed `dataset:osdu-cache|<partition>|cache|<type>`,
  and a delivery flow naming its partitions reads and writes in each of them. A registry-driven flow is described once,
  under the partition `*`, which stands for every registered partition: lineage is computed from the documents, and the
  registry is the catalog's. The cache page's streams read a `*` node as in every partition, so a registry-driven cache
  flow feeds, and a registry-driven delivery flow reads, whichever partition is in view.

## 7. GUI

A partition is chosen in one place: the title bar's switcher, from a dropdown, never typed. Every page, view and run
follows it, and none has a partition picker of its own: which partition a view is about, and which one a run writes to,
is always the one the title bar shows, so what an operator looks at and what they act on cannot disagree. A link that
names a partition (`?partition=`) makes it the title bar's, so a link still lands where it points.

- The title bar's switcher (a `GuiModule.titleBar` contribution) sets the partition every OSDU page is read in. It lists
  every registered partition, and every partition something is still kept under, marked unregistered, each with what it
  is for, what its cache holds and how many delivery flows deliver there. It starts at the registry's default, remembers
  the choice, follows a page whose link names its partition, and links to the Partitions page.
- The Partitions page lists the same partitions with who registered each and when, and gives an admin register,
  describe, make default and remove.
- The title bar's partition is a global filter. Every call the GUI makes carries it (`X-Osdu-Partition`, a
  `GuiModule.requestHeaders` contribution, a generic extension point of SQLFlow's GUI), and every read across flows
  answers in it: the Records page's lookup, recent listing and flows to narrow to, the audit trail, and the search box.
  The Delivery overview counts each flow in it, and dims a flow that delivers only to other partitions, saying where,
  and leaves it out of the totals; a flow whose partition is its header's is counted in the partition its ledger is kept
  under. A flow's pages say when the flow does not deliver to it. With no partition known, every read is every
  partition's, and each row names its own.
- A run started from the GUI acts in the title bar's partition. The trigger dialog reads the partitions a flow serves
  through the pipeline it is launched for (`TriggerFieldsProps.pipelineId`, a generic extension point of SQLFlow's GUI),
  shows the title bar's partition as the one the run writes to, and refuses to start a run of a flow that does not serve
  it, saying which partitions the flow serves and that the title bar switches them. A flow whose partition is its
  header's runs while the title bar is on that partition (once its ledger says which it is). A run being repeated runs in
  the title bar's partition, and is refused while it names a submission or records of the partition it ran in. Building
  every partition in turn (`*`) is left to schedules and the CLI.
- A delivery flow's pages (overview, records, submissions, preview) are the title bar's partition's, and every count and
  action is that partition's. A flow that does not deliver to it says so, and where it does deliver, instead of showing
  another partition's ledger. The pipeline header lists the partitions; a record names its partition and links to the
  flow's page in it; the Records lookup names a hit's partition when no partition is picked.
- The cache page shows the title bar's partition's cache, and says so when that partition has none; Refresh runs in it,
  and the definition says which partitions each flow builds.
- The mapping builder reads the title bar's partition's cache, and checks with the delivery flow's entry for that
  partition.

## 8. API and CLI

The pipeline-level delivery routes take `?partition=` and settle it as a run does (section 3): named, else the default
among the flow's partitions, else its only one; otherwise 400 `Partition required`, and 400 `No such partition` for a
partition the flow does not serve. Two routes read the flow whole without one: the interface listing lists every
interface in every partition served, each row naming its partition, and the counts add every partition up and list the
partitions. Record routes name the ledger's flow id and bind to the partition the ledger keeps, registered or not, so a
record of a partition since taken out of the registry can still be read and acted on; a run it queues is settled again
when it starts. The routes that read what a ledger kept (a flow's records, submissions, audit trail, counts and
interfaces) also bind a partition taken out of the registry while the ledger's directory keeps a ledger of the flow in
it; a run, a preview or a removal in it asks for it to be registered again. A record, a submission, an activity, a lookup
hit and a ledger choice name their partition. A flow's counts name the partition a flow whose partition is its header's
is kept under (`headerPartition`), which a request never names back. The configuration routes take `?partition=` to set,
remove and read a partition's values.

The reads across flows (`GET /delivery/records`, `/delivery/records/flows`, `/delivery/activities` without a flow, and
the search box's records) take `?partition=`, else the `X-Osdu-Partition` header, else read every partition. A flow and
a partition named together are both filters.

The registry's routes:

| Route | Does |
| --- | --- |
| `GET /delivery/partitions` | Every registered partition and every partition something is kept under (a cache, a flow that names it, a ledger), with `registered`, `isDefault`, `description`, who registered it, its cache, its flows and how many ledgers it keeps. |
| `POST /delivery/partitions` | Registers `{ name, description, isDefault }` (admin). 409 when registered already. |
| `PUT /delivery/partitions/{name}` | Sets `{ description }` (admin). |
| `POST /delivery/partitions/{name}/default` | Makes it the default (admin). |
| `DELETE /delivery/partitions/{name}` | Takes it out of the registry (admin). 409 for the default while others are registered. |

The CLI's run and trigger take the run value as `--set partition=<name>`; `check`, `preview`, `fixtures`, `records`,
`cache` and `config` take `--partition`, settled as a run settles it (`records` and `fixtures` bind a named partition as
it is, since they send nothing). `sqlflow partition list | add <name> [--description <text>] [--default] | describe
<name> --description <text> | default <name> | remove <name>` keeps the registry; a repository describes a change at its
next sync.

## 9. Compatibility

- Documents whose `data-partition-id` header names their partition behave exactly as before, down to their ledger ids
  and cache definition ids. Their partition need not be registered.
- A document that named neither `partitions` nor the header was refused before; it is now registry-driven.
- Retrieval flows keep their `data-partition-id` header, and refuse the `partition` run value.
- Moving a flow between the paths is a change in the repository, never made by the engine. The Recall estate made that
  move: its cache flows name no partitions, and its delivery flow writes `keepLedger: dev` at the top, with `dev`
  registered as the default, so every registered partition is served and `dev` keeps the ledger it always had.
- The ledger's upgrade to partition keys (`LedgerPartitions`, module version 1.14.0) changes no ledger identity and
  moves no record between ledgers: it records the partition each ledger already belongs to.
