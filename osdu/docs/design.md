# OSDU Delivery: the delivery design

## 1. Purpose

Deliver prepared data into OSDU, and keep it correct over time.

"Keep it correct" is the load-bearing half. Delivering a record once is a pipeline.
Knowing what OSDU currently holds, detecting when the source has moved, updating only
what changed, and being able to prove the two still agree is a delivery system. The
difference shows up entirely in where state lives and at what grain.

### 1.1 Goals

- **Per-record custody.** Every deliverable has a durable identity, a current state, and
  an append-only attempt history.
- **Deliver only what changed.** A run where nothing changed does almost nothing.
- **Idempotent by construction.** Re-running, retrying, or replaying cannot create
  duplicate OSDU records.
- **Metadata-driven.** What gets sent is declared, not coded. A mapping fix ships without
  a build.
- **Generic across OSDU.** Any kind, any of OSDU's delivery shapes, not just well logs.
- **Safe by default.** Inconsistent inputs fail before anything reaches OSDU.

### 1.2 Non-goals

- **Not an ETL engine.** It does not read source systems, join, aggregate, or reshape at
  volume. SQLFlow's own pre-ingestion and ingestion flows land the files and load the keyed
  ingestion tables; the OSDU flow reads those tables by key and by window, without joining
  or reshaping them ([architecture.md](architecture.md)).
- **Not an ETL engine on the way back either.** The retrieval kind (section 15) pages
  OSDU's search index into files on the lake and stops there; projecting, joining and
  reshaping what it lands stays with the lake.
- **Not a workflow engine.** Delivery protocols are a small closed vocabulary implemented
  in code, not an authorable step language. See section 8.4.
- **Not a separate service.** It is the SQLFlow platform (control plane, catalog, nodes, scheduler,
  GUI) with the delivery domain as its one flow kind (section 12). It does not target SQL Server,
  generate DDL, or carry the SQL flow kinds SQLFlow had.

## 2. Why the current design does not fit

The existing `recall_to_osdu` package is a pipeline, and its failure modes follow from
that rather than from any individual bug. Each of the following was verified in the
source and each is addressed by a specific decision below.

| Observed | Cause | Addressed by |
|---|---|---|
| A wellbore whose metadata POST succeeded but whose curve upload failed gets a **fresh POST** next run, creating a second OSDU record. `osdu_id_map` is populated only under `--force_update`. | State is written but not consumed on the retry path. | Deterministic OSDU ids (5.3) |
| Wellbores with no curve data are marked `failed` forever and re-POST metadata **every run**, because the metadata write happens before the curve-data check. | No terminal state for "delivered as far as it can be". | The ledger (7) |
| The previous run's `error_message` is wiped, because prepare upserts `status=prepared, error_message=None` over it. | One mutable row per record. | Append-only attempts (7.3) |
| `attempt_count` counts state writes, not attempts, and climbs two to three per real attempt. | Same. | Append-only attempts (7.3) |
| The end-of-run summary counts every `success` and `failed` row for the log source, not the records the run touched. | Run-grained reporting over record-grained state. | Submission scope (7.2) |
| A record delivered in January never updates when Recall changes it in March. The only alternative is `--force_update`, which resends everything. | No change detection at all. `written_from_recall` exists in the schema, is written as `None`, and is never populated. | Content hashing (6) |
| The generated PySpark select reads `recall:BUSINESS_VALUE`; production reads `recall_curve:BUSINESS_VALUE`. Production reads `NoDataValue`, which the mapping does not declare. | Two copies of one contract, fixes flowing only into the copy. | Single interpreted mapping (4.2), preflight gate (10) |
| Every status write costs six catalog round trips plus a Spark job, twice per record, from eight threads contending on Delta optimistic concurrency. | Delta doing an OLTP job. | Relational ledger (7.1) |
| No `MaxRequestBodySize` is configured in petrodb-api, so Kestrel's 30 MB default applies. The 10 million cell chunk limit appears to have been tuned to stay under it. | An inherited default mistaken for a capability limit. | Section 14.3 |

## 3. Architecture

Data reaches OSDU through three flows on one platform, each keeping what it is good at.

```
  source files        SQLFlow pre        SQLFlow ing          OSDU Delivery              OSDU
  ------------        -----------        -----------          -------------              ----
  CSV / JSON /  -->   land raw     -->   keyed upsert    -->  render (mapping)    -->    storage, DDMS,
  Parquet             typed view         system columns       hash + skip                file, workflow
  payload files                          UpdatedDate_DW       ledger, lease, retry
  (left in place)                        FileName_DW          stream payloads
```

**The pre-ingestion flow** lands whatever files arrive into a raw table and builds its
typed view. **The ingestion flow** upserts that view into a keyed ingestion table, stamping
SQLFlow's system columns: `UpdatedDate_DW`, moved only when a row's checksum changed, and
`FileName_DW` and `RowNumber_DW`, which say which file and row a value came from. **The
OSDU flow** reads those tables, renders documents, decides what changed, maintains the
ledger, leases work, retries, streams payloads, and verifies. It knows nothing about how
the files arrived.

Lineage orders the three in waves, so the OSDU flow runs after the table it reads has been
loaded, and each stage has its own run, log and failure. The system columns are what carry
traceability across the boundary: the incremental window is taken on `UpdatedDate_DW`, and
`FileName_DW` and `RowNumber_DW` become each delivered record's origin in the ledger.

### 3.1 Why the payload goes past, not through

The delivery worker holds retries, which means it must be able to re-send a payload.
Buffering payloads in the worker would size its memory by the largest wellbore. Instead the
payload files stay where the preparing side wrote them, the record's own row says which
folder holds them (`source.payloads[].locationColumn`, under a declared `root`), and a retry
re-opens the blob. Nothing about a payload is ever loaded into the ingestion tables. See
section 13.

### 3.2 Trigger

A platform schedule fires the OSDU flow, and the run reads whatever the ingestion tables
have changed since its scope's watermark. There is no notification to miss and no container
to poll: the ingestion flow's own run is what makes new rows visible, and lineage means a
chain triggered together runs pre, then ing, then OSDU.

A run whose window holds no changed row, and for which no record is waiting to be planned
again, does nothing at all. That is what makes frequent scheduling free on quiet hours.

### 3.3 Records sent in the request

A source with a handful of records to deliver, rather than files it writes itself, sends
them in the submission: `POST /api/v1/delivery/submissions` with `records`, each in the
shape of a mapping fixture (its dataset row under `record` and its child dataset rows under
`datasets`, as JSON scalars). It is the same flow, the same mapping and the same path,
because the submission lands the rows as files for the flow's own pre-ingestion flows, and
they become the same rows in the same tables.

- The control plane checks the request's shape, the record keys, the payload roots, the
  declared pre flows and the flow's parameters, then stores the submission, one landing row
  per dataset and a `submit` activity in one transaction (`osdu.InlineSubmission`,
  `osdu.SubmissionLanding`). The submission id is the idempotency key: a repeated request
  answers with the chain it queued, and a different request under the same id is refused.
- It writes one file per dataset into the landing folder the flow declares, under a
  temporary name the pre flow's `srcFile` does not match, then promotes it, so a pre run
  never reads a half-written file. A file already there with the same content hash is left
  alone, which is what makes landing idempotent.
- It enqueues the chain in one transaction with the ledger rows describing it: the declared
  pre flows, the ingestion flows between them and the OSDU flow, and the OSDU flow, in wave
  order. Each pre flow member reads exactly the file this submission landed, whatever its
  own watermark says.
- From there nothing is special: the ingestion upsert, the preflight gate, the per-record
  change gates, the ledger and the drain. The OSDU run plans the submission's keys against
  the ingestion table, so a key those tables do not hold is held with a reason naming the
  landing file and the flows expected to have loaded it.

A flow offers this or it does not: `source.submissions` says where each dataset's rows land
and which pre flow reads them, and a request to a flow that declares nothing is refused
naming the key. It is opt-in because a flow fed by files the preparing side writes should
not also accept hand-written records unless the estate decided it should.

A submission is metadata plus, for a flow that streams payload files, **where those files
already are**. Nothing is uploaded through the API and nothing is staged: a record carries
the location of its files, which is written into the landing file's `locationColumn` and
`hashColumn` exactly as a file from the preparing side would carry it, and the node opens
that location with its own identity when it delivers, re-opening it on every retry, exactly
as section 3.1 describes. Because the node's identity can read whatever it has been granted,
a record may only point inside a declared payload `root` or one of
`source.submissions.fileRoots`; anything else is refused when the request is accepted.
[submitting-records.md](submitting-records.md) is the contract for the source side.

Because the link between a delivered record and its submission is the landing file name,
traceability holds even when a scheduled pre run picked the file up and delivered the record
before the submission's own chain ran.

## 4. The four inputs and the render context

A rendered document is a pure function of four inputs. They change on different clocks
under different owners, and the danger is not any one being wrong but the four being
mutually inconsistent.

| Input | Owner | Clock | Artifact |
|---|---|---|---|
| Source data | The pre and ingestion flows | Continuous | The keyed ingestion tables, with SQLFlow's system columns |
| Mapping | This repository | Deliberate, gated | A versioned mapping document |
| Cache (reference and master data) | OSDU, captured into the catalog by a cache flow | Refresh runs | Versioned, immutable |
| Template (the target schema) | OSDU, saved in the catalog | Pinned by the mapping | Versioned, immutable |

### 4.1 The render context

The three non-source inputs are pinned together as a **render context**:

```
renderContext = (mappingVersion, cache partition and cacheVersion, templateVersion)
```

A flow reads the cache of the partition it delivers to (its `target.headers.data-partition-id`, section 6.2); a
render reads the version that is current when the run starts, unless the flow pins one (`render.cacheVersion`), and the
ledger's render context records the partition under `cache` and the version under `cacheVersion`. A mapping that reads
nothing from a cache renders against no cache at all.

It is fixed for a render, recorded in the ledger against every document produced, and it
enters the content hash. That single construct gives reproducibility, correct
invalidation when any input moves, and the ability to find every document produced under
a bad combination after the fact.

The template version is pinned **by** the mapping, through its `template` block (the kind
and the content version), and the ledger's render context records it under `schema`. An
OSDU schema upgrade is therefore a mapping change and inherits the mapping's blast radius
and its gate.

### 4.2 The mapping is interpreted, not compiled

The existing `MappingGenerator` compiles one mapping document into C# mappers, C#
endpoints, a Python client and a PySpark select. Those outputs were copied across a
repository boundary and have since drifted from production in both directions.

A mapping is data, not a program. Every construct in the current document is
interpretable at request time: `TargetProperty` is a dotted JSON path, `Transform` plus
`TransformConfig` is a coercion vocabulary, `IsCollection` with a definition reference is
a repeater, and a non-collection reference is a single nested block. Interpret it, and
delete the generators. Interpreted here, a mapping is a list of entries, each naming a
variable of the pinned template and where its value comes from
([mapping-templates.md](mapping-templates.md)).

The principle that separates the two cases: **generate from what you do not own,
interpret what you do.** OSDU's schema is external and has one authoritative upstream, so
generated types cannot drift from it. The mapping is internal and changes on our clock,
so compiling it into two places guarantees drift.

### 4.3 The mapping does not restate the target

OSDU publishes a JSON Schema per kind, and `osdu-client` already carries 1,427 generated
classes covering abstract, master-data, reference-data, dataset, work-product and
work-product-component types.

So the mapping declares only what the schema cannot know:

- where each value comes from: a dataset column, a cached record, or a static value
- how an incoming value is changed (the modifiers)
- the dataset key
- envelope policy: which legal tags and ACLs apply

Types, requiredness, relationship targets and units all come from the pinned template, the
OSDU schema saved in the catalog. The current document spends most of its 1,056 lines
restating exactly those, creating a second source of truth for facts OSDU already
publishes.

## 5. Identity

Three keys, with distinct jobs.

### 5.1 Source key

The source system's own identity, carried verbatim as provenance. For Recall well logs
that is the source project plus the log id, which is what
`RECALL_LOGCURVE_ENRICHED_CONTRACT` already declares as its row key.

Note this is finer than the key the current pipeline tracks on.
`build_collected_metadata_df` groups on wellbore plus log name and collapses `LogRun`,
`LogActivity`, `LogVersion` and `WellLogNativeUID` with a first-value aggregate, so two
logging runs of the same type on one wellbore silently become one record carrying
arbitrary values. **Whether the delivery grain is (wellbore, log source) or
(source project, log id) is an open domain question** and must be settled before the
ledger schema is built. See section 17.

### 5.2 Delivery key

A deterministic surrogate derived from the source key, for example a UUIDv5 over the
source system, source project and log id.

The essential property is that it is **computed from the data, never assigned by a run**.
Databricks and the delivery service derive the same key independently, a re-run produces
the same key, and the storage path, the ledger primary key, the idempotency token and the
OSDU id are all the same value. Nothing needs coordinating between the two halves.

### 5.3 Target id: deterministic and client-supplied

OSDU record ids may be client-supplied, in the form partition, entity type and a unique
segment. Derive that unique segment from the delivery key.

This is the highest-leverage decision in the design. With it:

- Upsert is native. Create-versus-update disappears as a concept.
- The stored OSDU id bookkeeping disappears, and with it the entire duplicate-record
  class of defects traced in section 2.
- References to records we also deliver can be **computed** rather than searched, which
  removes an OSDU search call per record.
- Replay is safe by construction.

The current design lets OSDU assign the id, which is precisely why so much state exists
to remember it.

## 6. Change detection

The question to answer per record is "would delivering this change anything in OSDU".

### 6.1 Hash the rendered document, not the source

Source-row hashing is a proxy that fails in both directions: an unmapped column changes
and you redeliver for nothing, or a mapping entry changes and you deliver nothing when you
should.

```
contentHash = H(canonicalRenderedDocument, renderContext)
```

Including the render context is not optional. If the hash covers only source data, a
mapping fix silently never propagates to existing records and OSDU stays stale
indefinitely. That failure is invisible until someone notices.

### 6.2 Canonical rendering is a prerequisite

The render must be byte-stable across runs, or the hash never matches and you have
reinvented full reload. That means canonical JSON with sorted keys, normalised number
formatting, and no run-varying content.

This is one reason the reference data has to move out of an in-memory per-replica cache
and into a versioned cache that every render names by version. Today the same input can render differently on two
replicas, or before and after a refresh interval, and `OsduReferenceCachesHostedService`
logs initial-load failures and continues by design, so a replica can serve from an empty
reference set with no signal.

**What the cache holds.** A cached record is its OSDU id and the values found at the paths
the cache declares. A path is cached in whatever shape OSDU returned it: a scalar, a set of
values, or a nested object. Paths cross arrays implicitly, so `data.NameAlias.AliasName`
reaches through an array of objects and caches the set of aliases it found. Nothing is
narrowed to text on the way in, because a cache that quietly drops what it cannot flatten
looks, at render time, exactly like bad source data.

**How it is matched and read.** A mapping entry selects a cached record with `findBy`. A
field holding a set matches on any one of its values. Matching is trimmed; an exact match
of one record wins, case is ignored only when that finds exactly one record, and a value
several records answer to holds the record rather than taking one of them. A
`cache.<Type>.id` source takes the matched record's id; `cache.<Type>.<field>` takes the
value at a path inside it, which is how a mapping builds a document out of cached data
rather than only pointing at it. A type or field the cache does not hold fails the
preflight gate rather than holding every record at run time.

**Who fills it.** What is cached is defined by flows of their own, `flowType: cache`
([documents.md](documents.md#cache-flow)): the OSDU platform to search, the types to cache
(each a kind, an optional query and the paths to keep), and what a changed value does.
There is one cache per OSDU data partition, keyed by the partition as the flows declare it
(`CacheScope`): a cache flow fills the cache of the partition in its
`source.headers.data-partition-id`, and a delivery flow reads the cache of the partition in
its `target.headers.data-partition-id`, so no flow names a cache. A run of a cache flow, the
`refresh` operation that its schedule fires, sweeps every declared type in full through the
search cursor and merges what it found into the partition's cache, which writes a new
version into the catalog. The capture is deliberately never incremental: a cache holding
only the last hour's changes cannot answer a lookup. A merge that changes no cached content
(the same content hash) writes no version at all, because a version label enters the render
context, and a new label for unchanged content would render every record built from the
cache again for nothing; refreshing as often as anyone likes is free. The newest version of
a partition is always its current one. Each version records the cache flow that wrote it,
the run that captured it and who asked, so a cached value can be traced to the capture that
produced it. Version labels are minted from the capture instant (`20260908T212727Z`), with
the sequence appended when two captures of one partition share a second
(`20260908T212727Z-7`).

**Several flows, one cache.** A partition's cache is shared by every project delivering to
the partition, so several cache flows, in one repository or in several, may fill it.
Records are stored once per partition, and the cache holds the union of what the flows
declare (`CacheDeclaration`): flows declaring a type under the same name share one type, a
refresh of any of them fetches every path any synced flow declares for that type (so a value
one project asks for is there for every pipeline reading the partition), every flow's query
adds records, and the type's changes wait for approval when any flow declaring it asks for
that. A merge (`CacheMerge`) replaces what the cache held for every captured record,
whichever flow captured it, so the newest capture is what every pipeline reads. Which flows'
last capture held each record is kept beside the versions (`osdu.CacheMember`), because a
record the capturing flow no longer finds may be exactly what another project's query keeps:
it leaves the cache only when no other flow's last capture still holds it. Types the capture
does not cover are untouched, except a type no synced flow declares any more, which is
removed. Two declarations of one type that disagree on the entity type, or cache one field
name from two paths, would give one name two meanings and are refused: the repository sync
leaves the declaration synced second out with a warning, and a refresh of a flow that
disagrees fails before capturing. The sync also warns when the cache flows of one partition
search different endpoints. Two refreshes of one partition writing at once collide on the
partition's next sequence, and the second fails and asks to be run again rather than
interleaving. `makeCurrent` is gone: versions form one line per partition, and a delivery
flow that has to stay on an earlier one pins it with `render.cacheVersion`.

**Where it lives.** In the catalog, and only there (`osdu.CacheVersion`,
`osdu.CacheItem`, `osdu.CacheMember`, each keyed by the partition). The repository holds the definition and nothing else: OSDU Delivery
reads git and never writes to it, so no capture is committed and no run writes into the
copy of the repository it executes from. Every version is kept, because a delivered
record's render context names the version it was rendered against and the ledger has to be
able to show what that version held. Items are stored once per partition, by version range: one row per record
per run of consecutive versions that held it unchanged, so a refresh writes rows only for
the records that changed, arrived or left, and keeping every version costs rows in
proportion to what moved rather than to the size of the cache times the number of captures.
Each version carries the hash of its whole content, checked every time it is loaded, so a
version altered after it was written is refused rather than rendered against. A render
reads its version from the catalog, which is what keeps a plan working without a call to
OSDU.

**Where it is visible.** The repository sync projects each cache flow's declared types, with
the partition it fills and the endpoint it searches (`osdu.CacheDefinition`), so a
refresh knows every path its partition keeps for a type, and the GUI's OSDU cache page shows
which files fill a partition's cache beside the versions their runs wrote, and searches the
cached values. Every read of cached records names one partition and one version, the current
one unless another is named: versions share rows, so a listing that was not scoped to one would show a cached
record once per version and count it as many.

**What a new version does to what is already delivered.** A cache is an input to every
document built from it, so a changed value means delivered records no longer match what
the cache says. Answering "which ones" by re-rendering the estate is both slow and mute
about the reason, so every render records what it consumed: which cached record, which
path, and the value it read.

That trail has to survive the shape of the estate, which is hundreds of millions of
manifest rows and rising. A row per record per consumed value would be billions of rows
to write, index and query, so the trail is stored by **dependency set** instead
(`osdu.CacheSet`, `osdu.CacheSetEntry`): one row per distinct combination of
cached values, which every record reading the same values shares. A well log estate
resolves the same handful of units, curve types and wellbores over and over, so the sets
number in the thousands while the records number in the billions. A render computes its
set's hash in memory and puts one `CacheSetId` column on the manifest row, so staging a
million records writes no dependency rows at all, and the impact query runs over the sets,
never over the records.

A refresh compares the new version against the one it replaces, and for the items that
moved it asks which sets hold their values. A set is touched when a value a record wrote
into its document now reads differently, when the cached record it used is gone, or when
the value it matched by no longer resolves; anything else the change does not touch,
including a record that only ever read the id of an item whose name changed. Each set is
judged by the value it holds, since sets built against different cache versions can hold
different values of one path: a set already holding the new value is not touched.

**Who decides.** Each changed value becomes one tag (`osdu.UpdateTag`): the cached
record, the path, the value the replaced version held and the one the new version holds,
and how many delivered records it reaches.
One decision covers all of them, because asking an operator to approve twelve million rows
is not asking anything. The cached type's `onChange` says what the tag means: `auto`, the default,
approves it as it is written; `approve`, an option a cache flow or one of its types opts
into, holds the affected sets until an operator decides. When several cache flows of a
partition declare the type, any one of them opting in holds its changes.
A run counts what the gate holds back as awaiting approval, never as unchanged: the change
is rendered and ready, and a decision is what the estate is waiting on.
The gate is real, and it has to be: the render context moved with the cache version, so
tier 1 would otherwise re-render and send exactly the update being held back. A plan reads
the gated set ids once (a handful of numbers), sees a record whose set is gated ahead of
every change tier, and skips it. Holding a million records back never writes to a million
rows.

**How it is carried out.** An approved change is rolled out in bounded batches by a
control-plane service (`ControlPlane:CacheRollout`), which marks a page of the affected
records for redelivery in delivery-key order from the tag's own cursor, then stops until
the next tick. A change over millions of records drains at a set pace instead of in one
statement, an interrupted rollout resumes where it stopped rather than starting over, and
the marking is metadata only: a corrected reference value rewrites the manifest row and
never re-uploads the payload that was delivered with it.

**And the run has to happen.** The whole-run gate (tier 0) used to skip a run when no
source table advanced, which is the case a cache refresh produces: the source is exactly
where it was, and everything about how it renders has changed. The watermark now carries
the render context of the run that wrote it, so a moved cache, mapping or template version
plans the scope rather than skipping it.

### 6.3 Two hashes, decided independently

The metadata document and the payload are delivered by different calls and change at very
different rates. Coupling them means a corrected `LogRun` re-uploads a hundred megabytes
of grid.

- `metadataHash` over the canonical rendered document alone: the render context decides when a record is rendered
  again, never whether it is sent, so a new cache or template version that renders the same document sends nothing
- `payloadHash` over the **logical** payload content

### 6.4 Do not hash payload bytes

Parquet encoding is not deterministic for identical logical data. Compression settings,
row group boundaries, dictionary encoding decisions and the writer version all affect the
bytes, so a pyarrow upgrade would re-ship the entire estate.

Hash the logical grid instead: rows in index order, columns in canonical order, values in
a canonical numeric representation.

Hash the **result**, not the inputs. The grid depends on the curve samples, on `TopDepth`
and `DepthIncrement`, on the index-value versus metadata-grid branch, and on which curves
survived the duplicate check in `_valid_curve_ids`. Hashing the constructed grid captures
every one of those without enumerating them, and enumerating them is how you miss one.

### 6.5 Payload hash grain is the whole payload

Not per chunk. The OSDU session is created with overwrite mode, so a commit replaces the
whole log's bulk data and a single chunk can never be delivered alone. Chunk-level
hashing would buy nothing.

Compute the hash inside the grouped map that already holds the grid in memory, and carry
it in the manifest. No extra pass, and it is computed in the one place that has the
canonical logical form.

### 6.6 Two-tier gating

Deciding to skip must not require rendering, or most of the cost is already paid.

**Tier 0, whole run.** One watermark per `(flow, scope)`, the upper bound of the last
completed whole-scope plan. If no row changed in the window above it, and no record is
waiting to be planned again, skip the entire run. This costs one query and is what makes
frequent scheduling free on quiet hours. The watermark moves only when the plan and every
one of its fan-out members succeeded, so a failure re-reads rather than skips.

**Tier 1, cheap per-record gate.** The ingestion fingerprint (the record row's
`UpdatedDate_DW` and, per child dataset, its row count and newest `UpdatedDate_DW`) plus
the render context, the business version and the payload hash. If all are unchanged, skip
without rendering.

**Tier 2, authoritative.** Render and compare `contentHash`. This can still skip when the
render turns out identical despite a changed source column.

A fingerprint only says "different". When the source carries a last-modified column the flow
names it (`source.lastModified`) and the gate is ordered too: a row modified after the version
the ledger holds, delivered or queued, is rendered and hashed; the same moment skips at tier 1;
an older one (a replayed or late row) is stale and never sent. The payload's files can
be the payload's watermark the same way (`change.payloadDetect: lastModified`). Whatever
triggers the work, the hash decides the push, twice: at plan time against the ledger, and by
the worker against what OSDU holds at the moment it has the record, because work queued behind
an in-flight delivery is planned against a state that delivery is about to change.

Two limits to respect. A watermark **cannot see a deletion** the ingestion flow does not
record, so a `replan` pass is the backstop where deletions matter; where the ing flow
declares `systemColumns.deletedDate`, a child row's deletion does move its parent's
fingerprint. And a keyed upsert never removes child rows on its own, so a child removed at
the source keeps being delivered unless the ing flow replaces that parent's children.

### 6.7 Closing the loop back to the preparing side

> **Superseded.** This section described a known-state snapshot the delivery side published
> for the preparing job to read. There is no such snapshot and no `known-state` operation
> any more. The pre and ingestion flows are inside the same platform now, and the ingestion
> upsert already does the equivalent: a re-landed row whose business columns are identical
> leaves `UpdatedDate_DW` where it was, so the OSDU run's window never reads it and nothing
> downstream repeats the work. What a preparing job should skip is decided by SQLFlow's own
> incremental keys on the pre flow (`incremental.dateColumn`), not by a file this system
> writes.

## 7. The ledger

The ledger is the system. Everything else is machinery around it.

### 7.1 It is relational, not Delta

Delta is a bulk analytical format and the current design has it doing an OLTP job. One
status write in `OsduStateWriter.upsert` costs six catalog round trips plus a Spark job,
and the transfer phase issues two of them per record from eight threads that then collide
on optimistic concurrency and back off. The retry loop is the steady state, not a safety
net.

The problems are structural, not tuning: a transaction log entry per state change,
copy-on-write file rewrites for a single row, optimistic concurrency instead of row
locking, and no point lookups.

Use SQL Server through EF Core, in the module's own `osdu` schema with its own context,
migration history and schema version, so the ledger is upgraded in place without touching
SQLFlow's catalog.

### 7.2 Three levels

```
submission   one plan of a flow over its ingestion tables
  record     one deliverable, keyed by delivery key
    attempt  one delivery try, append-only
```

**Submission** carries the idempotency key, which selection was read (incremental, full,
keys or inline) with the window and the table it read, the render context and the scope. It
is what makes run-scoped reporting honest: the current end-of-run summary counts every
success and failure for a log source rather than the records the run touched.

**Record** is the current state of one deliverable. This is the table that answers "what
is missing".

**Attempt** is append-only. This is the specific fix for two observed defects: the
previous run's `error_message` being wiped because prepare overwrites the single mutable
row, and `attempt_count` counting state writes rather than attempts.

### 7.3 Record columns

| Column | Purpose |
|---|---|
| `deliveryKey` | Primary key. Deterministic, derived from source data. |
| `sourceKey` | Provenance, carried verbatim. |
| `mappingName`, `renderContext` | What produced the current state. |
| `sourceFingerprint` | Tier 1 gate. |
| `metadataHash`, `payloadHash` | Tier 2 gates, decided independently. |
| `targetId` | The OSDU record id. Deterministic, so this is a convenience not a dependency. |
| `targetVersion` | Last known OSDU version. The handle for drift detection. |
| `status` | See below. |
| `lastDeliveredUtc`, `lastVerifiedUtc` | Custody timestamps. |
| `leaseOwner`, `leaseExpiresUtc` | Concurrency control for the worker. |

### 7.4 Status

`pending`, `delivering`, `delivered`, `held`, `failed`.

`held` is the state the current design lacks and needs. A record with no curve data
currently gets its metadata created, is marked `failed`, and is retried on every
subsequent run forever, POSTing metadata again each time. `held` means "delivered as far
as it correctly can be, do not retry without intervention", and it is a terminal state
that stops the loop.

### 7.5 Leasing, not status flags

The worker claims a record with a lease and an expiry. A crashed worker's lease expires
and the record is reclaimed by a sweep.

This deletes the `in_progress` write entirely, which is one of the two round trips per
record in the current design, and it removes the best-effort duplicate guard that
currently depends on a status value surviving a crash.

Model it on `CatalogNotificationDelivery` in SQLFlow, which already implements this shape:
status, attempts, next-attempt time for backoff, claim timestamp with a stale-claim sweep,
and a redacted last error. Copy the shape, not the file, since it is notification-specific.

### 7.6 Ownership needs verification, not just idempotence

Content hashing gives source-side idempotence. It does not tell you OSDU still holds what
you sent, because someone can edit or delete a record and the ledger will happily report
everything delivered.

The write response returns a version, and the bulk session already threads it as
`FromVersion`. Store it as `targetVersion`, then run a periodic verify pass comparing
OSDU's current version against it. A mismatch means either drift to correct, or a signal
that we are not actually the authority for that record.

That pass is the difference between "we do not redeliver unnecessarily" and "we own this".

One known gap. `UpdateWellLogDataAsync` GETs the existing record and copies exactly three
keys forward from its data block (`Datasets`, `DDMSDatasets`, `ExtensionProperties`),
replacing everything else. Those three come from OSDU rather than from our render, so
they sit outside the content hash and outside our control. Decide explicitly whether they
are ours.

### 7.7 Volume and retention

A record-grained table is a different class of load from SQLFlow's run-grained catalog.
Decide retention and partitioning for the attempt table before it is built, and whether
terminal attempts are rolled up and aged out. Getting this wrong turns the ledger into
the new bottleneck, which is the Delta mistake wearing a different hat.

Snapshot the ledger into Delta periodically for the analytical view. The coverage
dashboards under `dsis_liberation_completeness` want that shape, and they want it as an
analytical table rather than a live status store. Delta was never the wrong technology,
it was being asked to be a write-ahead log.

## 8. Delivery protocols

### 8.1 OSDU has at least four delivery shapes

From the specs already held in `osdu-csharp-client/openapi_specs/`:

| Shape | Service | Pattern |
|---|---|---|
| Plain record | `storage` | One JSON document, upsert by id, batched arrays |
| Record plus bulk | `wellbore_ddms` | Record, then binary payload, optionally via a session |
| Record plus file | `file`, `dataset` | Signed upload URL, upload, register metadata |
| Manifest ingestion | `workflow`, `file` | Register the files, assemble a manifest, trigger a DAG, poll to completion |

The last is asynchronous and batch-shaped, and it is OSDU's own preferred bulk path.

All four are implemented as named protocols (`osduRecord`, `osduWellLog`, `osduFile`,
`osduManifest`; [protocols.md](protocols.md)). The record, file and manifest protocols
batch records per request, and every protocol reports each step it took and what the
target returned (section 16.3).

### 8.2 Design the vocabulary up front

Four protocols designed together will be a coherent set. Four discovered one at a time
will not. The taxonomy is documentable from artifacts already held, so there is no reason
to derive it from the one worked example.

The core is protocol-independent: identity, rendering, change detection, the ledger,
idempotency, preflight validation. Only the protocol varies.

### 8.3 The session is not required as often as it is used

`needs_session` is currently true whenever there is more than one chunk, and the chunk
cell limit appears to have been derived from an unconfigured 30 MB Kestrel default rather
than from any OSDU constraint. Once the ceilings are set deliberately (section 14.3), the
common case should be a single request and the session should be the exception.

### 8.4 Named protocols, not a step language

Resist declaring the session sequence as a generic step list with response threading.
Once YAML threads responses between steps it needs variable scoping, per-step error
handling and conditionals, and it has become a mediocre workflow engine inside a config
file.

Use named protocols implemented in code, parameterised by the flow. Generalise only when
a second target genuinely needs a different sequence, at which point the right
abstraction will be evident rather than guessed.

## 9. Document model

Two authored documents, kept separate.

### 9.1 Why separate

Blast radius. A mapping change re-renders every document that uses it and invalidates
every content hash it touches. A flow change (concurrency, retry, endpoint) changes
nothing about what a document is. In one file you cannot tell those edits apart, and
every operational tweak looks like grounds for redelivering the estate.

Also: the mapping validates against its pinned template, the flow validates against
storage and connection config. Different gates, different failure meanings. And six log
sources all deliver the same kind, so one mapping serves six flows. Inlining it would
create six copies of a contract that will drift, which is precisely what already happened
between the generator and production.

### 9.2 The flow document

```yaml
flowType: delivery
name: recall-welllog

parameters:
  logSource: { required: true }

source:
  connection: ${env:OSDU_SAMPLE_DB}
  record:
    object: OsduSample.ing.WellLog
    key: [source_project, log_id]
    scope: { log_name: logSource }
  datasets:
    curves:
      object: OsduSample.ing.WellLogCurve
      join: { source_project: source_project, log_id: log_id }
  payloads:
    curves: { root: ../data/curves, locationColumn: curve_folder, hashColumn: payload_hash }
  work: ../.work/{logSource}

render:
  mapping: WellLog@1.4.0

change:
  detect: renderedHash
  payloadDetect: contentHash
  onUnchanged: skip

target:
  endpoint: ${env:PETRODB_URL}/welllogs
  auth: { type: oauth2ClientCredentials }
  protocol: osduWellLog

reliability:
  concurrency: 8
  retry: { attempts: 4, backoff: exponential }
  skipStatusCodes: [409]

schedule:
  cron: "0 * * * *"
```

The mapping reference is **pinned**, never floating. A floating reference means a mapping
edit silently re-renders production without passing the promotion gate, which defeats the
separation.

One document with a parameter replaces the six near-identical job blocks currently in
`databricks.yml` that differ only by a log source string.

### 9.3 Render-affecting versus operational keys

Only `render.*` and the identity rules change what a document is. Everything under
`reliability` and the endpoint changes only how it gets there.

Only the first group enters the content hash. Otherwise raising concurrency triggers a
full redelivery, which is the behaviour the design exists to eliminate.

### 9.4 Identity lives in the mapping

Not in the flow. Changing identity re-keys every record and orphans the ledger, which is
mapping-level blast radius and must not sit in an operational file where it looks like
ordinary config.

Express it in source terms: the mapping's `dataset.key` names the columns of the incoming
dataset that identify a record, in order, and `dataset.system` the source system; the
deterministic delivery key, and so the OSDU id, is derived from them. The key is declared
apart from the entries, so identity never decides what a property of the record holds, and
a key column need not be written into the record at all.

Treat it as immutable once a flow has delivered anything, enforced at the schema level,
with an explicit migration path to change it.

### 9.5 No flow-level mapping overrides

The content hash is `H(document, renderContext)`, and that works only because the mapping
version fully determines the shape. Allow a flow to override a field and two flows on the
same mapping version render differently, so the override set must enter the hash, so the
flow becomes a render input, so every concurrency tweak invalidates hashes again.

Two knock-on effects follow from the same root. The preflight gate loses meaning, because
there is no longer a validated mapping artifact, only N mapping-plus-override
combinations. And blast radius inverts, because a flow edit becomes capable of silently
changing content, so flow edits need canaries too.

The pressure will come, and it is almost always one of two things. "This scope needs a
constant value" usually means there is data that was not put in the data: have prepare
emit it as a column. "This source names the column differently" is a source contract
difference: normalise it in prepare, or it is genuinely a different shape and deserves
its own mapping.

If variation is real and recurring, the answer is **mapping parameters**: the mapping
declares what it accepts, the flow supplies values, the values enter the hash explicitly.
The distinction is who is in control. A parameter is variation the contract sanctioned.
An override is variation imposed on it from outside.

## 10. Validation and the preflight gate

### 10.1 Each input can be valid while the combination is broken

This is the mechanism behind systematic garbage, and it has already happened here. The
generator's select reads `recall:BUSINESS_VALUE`; production reads
`recall_curve:BUSINESS_VALUE`. Production reads `NoDataValue`, which the mapping does not
declare. The mapping declares `Mnemonic`, `DateStamp`, `NativeUID` and `SourceProject`,
which production never sends. Every artifact was individually valid.

### 10.2 The gate

Before any render, and with no OSDU call:

1. Every dataset column and child dataset the mapping reads exists in the flow's ingestion
   tables.
2. Every cached type the mapping reads exists in the cache version the render reads, with
   the fields it finds by and reads.
3. Every property the template requires in `data` has an entry that may not be left out.
4. Every target is a variable of the pinned template, with an agreeing shape, and every
   cached or static reference points at an entity type the schema allows.
5. The mapping's own fixtures still render exactly, without holds, under this exact context.

[mapping-templates.md](mapping-templates.md) lists every check.

If the combination does not validate, nothing renders. Not a warning.

### 10.3 Schema validation is structurally complete and semantically blind

Point 4 would not have caught the `recall_curve` bug. Both are strings, both bind to a
string field, both validate. The fixtures at point 5 are what catch semantic drift: each is
an example row and the exact record it must render to, a regression suite written in the
contract's own terms.

### 10.4 Unknown keys are an error

SQLFlow's loaders use `IgnoreUnmatchedProperties`, and its own documentation is explicit
about the consequence: a misspelled key is silently ignored rather than rejected, and the
document then behaves as if the key were absent.

For a system writing into a governed store that default is wrong. Unknown keys are a hard
parse error here. One line on the deserializer, one class of silent misconfiguration
removed.

### 10.5 Blast radius control for mapping changes

A source change affects one record. A reference change affects the records that use it.
A **mapping change affects every record at once**, so the three must not move the same
way.

- `plan` renders a sample under both the old and new context and shows the document diff.
- A canary applies the new context to a declared subset.
- Promotion is explicit.

Source and reference changes flow continuously. Mapping changes do not.

### 10.6 Why prevention matters more here

A corrupted warehouse table gets truncated and reloaded. OSDU records are versioned,
carry legaltags and ACLs, and are a governed corporate asset, so bad writes are durable
and the correction is another version rather than an erasure.

That asymmetry is what justifies building the gate properly. It is also why the ledger
records the render context per document: when something wrong does ship, the ledger
identifies exactly the documents produced under the bad combination, so remediation is
targeted rather than a full reload.

## 11. Operations

The delivery domain runs on the platform's verbs and API; there is no separate delivery service.

| Operation | Where | Behaviour |
|---|---|---|
| `check` | CLI: `sqlflow check <flow.yaml> [--connect]` | Everything checkable without OSDU: document parse, the mapping against its pinned template and the version of the cache of the partition the flow delivers to (both read from the module database), and with `--connect` the ingestion tables themselves: their columns, the key types, the system columns, the watermark window and the candidate counts. |
| `plan` | CLI: `sqlflow run <flow.yaml> --operation plan`; GUI and API: a run with operation `plan` | Renders documents and reports what would be created, updated, skipped or held. Works without OSDU, against the pinned template and the cache version read from the catalog. Changes nothing. |
| `deliver` | CLI: `sqlflow run <flow.yaml>`; GUI and API: a run, a schedule fire, or `POST /api/v1/delivery/submissions` with records (section 3.3) | Executes a submission: intake, plan into the ledger, deliver what changed. |
| `verify` | a run with operation `verify` (the record page queues one scoped to the record) | The drift pass: compares OSDU's current version against `targetVersion`. |
| `replan` | a run with operation `replan` | Reads every row of the scope again, past the whole-run gates, and delivers what renders differently now. Each record's own hashes still decide what is sent. |
| `intake`, `drain` | the fan-out members a deliver run enqueues (section 16.4); also runnable by hand | `intake` registers and plans a selection (or some of its key slices) into work batches without delivering; `drain` delivers the pending batches of a submission (or of the whole flow) without reading the source. |
| `retrieve` | a run on a retrieval flow (its default); `plan` on the same flow counts | Pages OSDU's search index into files on the lake (section 15). |
| `refresh` | a run on a cache flow (its default, and what its schedule fires); `plan` on the same flow counts what each type's search matches | Captures every type the cache flow declares, merges it into the cache of the flow's partition, and writes a new version into the catalog when the cached content moved, then tags the changes that reach delivered records (section 6.2). |
| `cache` | CLI: `sqlflow cache list`, `sqlflow cache import` | Lists the versions of a partition's cache; merges type files into the flow's partition as that flow's capture, for work without OSDU. |
| `template` | CLI: `sqlflow template capture`, `import`, `list`, `show`, `delete`; the GUI's Templates page | Saves an OSDU schema as an immutable template version in the catalog, from the OSDU data definitions (the Open Group's public repository, or a local checkout of it) or from a bundled schema file. |
| release, redeliver, delete, read back, probe | the GUI record and flow pages; `POST /api/v1/delivery/records/{key}/...` | Interventions, recorded in the ledger's activity trail under the user who asked. The ones that touch OSDU (delete, read back, probe) run on a node as compute tasks. |

`plan` working without OSDU is a direct consequence of reading the reference data as a cache
version and the schemas as templates, both from the catalog. It is also the single most valuable operational feature
here, because it makes a mapping change previewable against real records before it
touches a governed store.

Every validation failure is a `FlowValidationException` carrying the file path, following
the platform's error contract.

## 12. Relationship to the platform

The first design of OSDU Delivery vendored about 1,500 lines of SQLFlow infrastructure into
a standalone service. That version is superseded: OSDU Delivery is the SQLFlow platform,
stripped of its SQL Server ETL kinds, with the delivery domain registered as its one flow
kind (`src/SqlFlow.Delivery`). What the domain takes from the platform, and what stays its own:

### 12.1 Taken from the platform

- **The document is the pipeline.** One declarative file with a `flowType` discriminator
  and camelCase keys; the platform reads the envelope (`schedule`, `mode`, `lifecycle`) and
  hands the body to the delivery kind through `IFlowDocumentKind`. Runs go through
  `IFlowDocumentExecutor`, target-side operations through `IComputeOperation`.
- **A catalog on SQL Server through EF Core.** The ledger's tables live in the module's own
  `osdu` schema in the same database, with its own context, migrations and schema version,
  so one backup covers both and neither model contains the other's tables.
- **The run queue, nodes and pools, schedules and chains, the git sync, identity, tokens,
  notifications and the GUI workbench.** Every delivery run is a platform run with a live
  trace and a run artifact; every intervention is an activity in the ledger and, when it
  ran as a run, a run in the history.
- **File stores, the secret chain and redaction.** Local and Azure Blob reads, `${env:...}`
  and `${keyvault:...}` references, secrets redacted before any log or row. The delivery
  domain adds only the writers it needs (work batches, a submission's landing files).
- **The HTTP reliability stack.** The delivery copies in `src/SqlFlow.Delivery/Http` keep
  their vendored headers because they diverged from the platform's originals: a request
  factory per attempt so a binary payload streams and retries, no charset handling.

### 12.2 Kept record-grained, on purpose

- The platform's run is the grain of scheduling, tracing and history. The ledger's record
  is the grain of custody. A deliver run is one submission's intake plus drain; the ledger
  carries what each record went through, linked to the run id.
- A flow's own parameters (`parameters:`) bind the record scope's predicate and are
  substituted into the work location, and they travel as run parameter values, recorded on
  the run and on the submission.
- The mapping lives in the flow's repository (`mappings/`), and a cache is defined there by
  its cache flow; both are synced into the catalog as read models and never edited through
  the API, and a mapping the GUI's mapping builder writes reaches the repository as a pull
  request. The templates the mappings pin and every version of every cache live in the
  catalog itself, and a saved template or cache version never changes.

### 12.3 Streaming and retry coexist

`HttpExecutor.SendAsync` takes a request **factory**, so each retry attempt builds a fresh
request. The payload factory re-opens the blob stream and wraps it in `StreamContent`;
streaming and retry therefore coexist, and worker memory is bounded by concurrency times
the copy buffer. Had the executor taken a pre-built request, buffering would have been
unavoidable.

Note that petrodb-api already does this correctly, end to end: the endpoint takes
`httpContext.Request.Body` as a `Stream` rather than binding a byte array, carries it
through as a `Stream`, and wraps it in `StreamContent`. It has no retry, which is why its
pre-built request has not yet bitten; if retry is ever added there, `ExecuteRequestAsync`
needs the same change from a request to a request factory.

## 13. Streaming and memory

### 13.1 The payload is never parsed

Payload chunks are opaque bytes: open a blob stream, copy into a request body. A stream
copy is bounded by the buffer, not the file. Only the source-shaped metadata rows are
parsed, and `ParquetSourceReader` already iterates row groups one at a time with column
pruning, so peak memory is one row group's selected columns.

### 13.2 The real ceiling is upstream

`applyInPandas` hands the grouped map the **entire wellbore** as one pandas frame.
`build_wellbore_grid_frame` builds the full wide grid, and only then does
`encode_grid_chunks` split it. So the ten million cell limit bounds the *payload*, not the
*memory*, and a large enough wellbore will OOM a single executor. This is true today,
independent of anything in this design.

Fix by moving the split before the pivot: group on wellbore plus depth bucket so each
window pivots independently. Peak memory becomes one window rather than one wellbore, and
a large wellbore parallelises instead of pinning one executor. The session aggregates
chunks by row label, so each window's chunk has to carry an index that continues from the
previous window's (a window that restarts at zero replaces rows instead of adding them, and
the commit still succeeds). The delivery side checks the labels before a session opens and
the committed log after it ([protocols.md](protocols.md)), so such a window holds the record
instead of losing rows.

### 13.3 Budget it explicitly

With streaming throughout, worker memory is `maxConcurrentDeliveries * bufferSize` plus
one row group. Concurrency is the only multiplier, so set it deliberately rather than
inheriting a default.

## 14. Dependencies and approval

### 14.1 A deliberately small surface

The source is a SQL Server or Azure SQL database, read through `Microsoft.Data.SqlClient`, and the payload files are
opaque bytes read from storage. `Parquet.Net` remains for the payload shape checks the well log protocol makes and for
writing a submission's landing files, and `abfss` is read through `Azure.Storage.Blobs` and `Azure.Identity`. All pure
managed: there is no native code on the delivery path.

The direct dependency set of the solution after the strip:

- Microsoft first-party: `Microsoft.EntityFrameworkCore.SqlServer` (and `Microsoft.Data.SqlClient` through
  it), `Microsoft.Extensions.*`, `Microsoft.AspNetCore.Authentication.JwtBearer`,
  `Microsoft.AspNetCore.OpenApi`, `Microsoft.IdentityModel.Protocols.OpenIdConnect`, `Azure.Identity`,
  `Azure.Storage.Blobs`, `Azure.Security.KeyVault.Secrets`
- Third-party, all MIT and mainstream: `Parquet.Net`, `YamlDotNet`, `Cronos`, `MailKit` (the SMTP channel of
  the notification service; the Graph and Slack channels are plain HTTP), `LibGit2Sharp` (git materialisation)

### 14.2 What was kept out

DuckDB, the SQL Server providers, `Microsoft.ML`, `SSH.NET`, SMO, Oracle, `SlackNet`, `Anthropic` and
`AWSSDK.S3` left with the SQLFlow flow kinds they served; nothing in the solution references them. The one
native dependency that remains is `LibGit2Sharp`, confined to `src/SqlFlow.SourceControl` (the managed sync
of repo sources) and `src/SqlFlow.Node` (materialising the pinned commit a run was enqueued from). A run
whose document needs no sibling files executes from the catalog's content snapshot and never touches it.

Run `dotnet list package --include-transitive` before any approval conversation. Transitive dependencies are
where enterprise scanning finds things.

### 14.3 Three undeclared size ceilings

petrodb-api configures no `MaxRequestBodySize`, so Kestrel's 30,000,000 byte default
applies and larger payloads are rejected with 413 before the handler runs. Ten million
cells of float64 is roughly 80 MB raw, which parquet compression lands near that ceiling.

A 413 surfaces as a curve upload failure after the metadata write has already succeeded,
which under the current retry path produces a duplicate record on the next run. The
missing configuration may have been quietly generating duplicates.

There are two further ceilings nobody set deliberately: the Radix ingress body size and
the APIM gateway limit. Measure all three, set them to the same intentional number, and
derive the chunk cell limit from the measured ceiling rather than from folklore. Check
OSDU's own documented bulk limit first, since that is the one ceiling that cannot be
raised.

That last one is no longer folklore. The wellbore DDMS publishes it: the OpenAPI
description of `POST /ddms/v3/welllogs/{record_id}/data` says bulk over "10 millions
values or 3000 columns" must be sent through the chunking APIs, and the service carries
the same numbers as `WRITE_MAX_TOTAL_VALUES_COUNT = 10_000_000` ("restrict chunk to
~100MB") and `WRITE_MAX_COLUMNS_COUNT = 3_000`. The column half is milestone-dependent:
500 through M25, 3000 from M26. `WellboreDdmsBulkLimits` holds both with their provenance,
`target.protocolOptions.maxChunkValues` and `maxChunkColumns` declare them per flow, and
the well log protocol checks every chunk against them in the same preflight as the byte
ceiling.

Measuring the shape needs the parquet footer, which is not a departure from 13.1: the
schema and the row group headers are read, no column data is, so the memory is the schema
whatever the chunk holds. The bytes still stream past unparsed.

## 15. Reading from OSDU

Reads in service of writing were always here: the verify pass by id, the schema fetches
templates are saved from, and the cache refresh (section 6.2). Bulk inbound is the
retrieval kind, `flowType: retrieval`, added because the lake needs OSDU's records back
without a second export pipeline ([decisions/0008](decisions/0008-retrieval-lands-raw-records.md)).

### 15.1 The shape

A retrieval flow names the OSDU side (endpoint, auth and headers, one or more kinds, an
optional Lucene query) and the lake side (a location, JSON Lines files rolled by record
count, optional gzip). A run opens one search cursor per kind
(`POST /api/search/v2/query_with_cursor`, pages of up to a thousand hits), streams every
hit through the store's writer into the current file, rolls the file at the declared
count, and writes a manifest next to the files listing every file with its record count,
the window the run covered, and the records storage could not read back. Kinds run
concurrently up to the flow's concurrency; pages within a kind are sequential because a
cursor is.

An empty page ends a cursor. The search service hands back a cursor for the page after
the last one as well, so ending only on a null cursor is how a walk pages forever, and a
cursor that comes back unchanged is the same page again. A walk that stops before the
end (a failure, a cancellation) releases the cursor
(`DELETE /api/search/v2/query_with_cursor/{cursor}`) rather than leaving the search
context to expire. A cache refresh (section 6.2) pages the same way for the same
reasons.

The index holds a projection of each record. When the flow needs the whole record it sets
`fetchRecords`, and every page's ids are read back from storage a hundred at a time
(`POST /api/storage/v2/query/records`), several requests in flight per page; the ids
storage asks to retry get one more request, and what is still missing is counted and
listed in the manifest rather than silently dropped.

### 15.2 Incremental by watermark

An incremental flow names a record timestamp field (`modifyTime` by default) and a lag.
A run covers the half-open window from the last completed run's upper bound (or the
declared start) to now minus the lag, expressed as a range clause appended to the query.
The lag keeps records the indexer has not caught up with for the next run instead of
losing them. The upper bound is recorded on the run's ledger row when it completes, and
only a completed run advances the chain: a failed run leaves the watermark where it was.
A forced run restarts at the declared start.

### 15.3 Tracked like everything else

Every run writes one row to `osdu.Retrieval` (the window, the location, the counts,
the outcome, the run id and the actor) when it starts and closes it when it ends, so the
GUI lists a flow's retrievals, and the manifest on the lake and the row in the ledger say
the same thing. The trace carries one line per kind, per hundred pages and per file, never
per record. The plan operation counts what the query matches per kind
(`POST /api/search/v2/query` with `trackTotalCount`) and writes nothing.

What the retrieval kind does not do: it never renders. A mapping is not invertible, since
an `equals` modifier collapses a string to a boolean, a `split` discards everything but one
part, and static values have no source at all. What lands is the record as OSDU holds it.

## 16. Scale: streaming intake, work batches, returned values and fan-out

An ingestion table can hold millions of rows and a flow billions over time. Nothing in the
engine holds a table, a dataset or a batch of rendered documents in memory, and one run can
spread its work across the fleet ([decisions/0006](decisions/0006-work-batches.md)).

### 16.1 The intake streams

The source never materialises a table. Candidate records are paged by record key, which is
deterministic, sliceable, and never splits rows that share an `UpdatedDate_DW`. One command
per page reads the page's keys into a table variable and then returns one result set per
dataset joined on those keys, with child rows ordered by the dataset's `orderBy` and grouped
in memory, so a record arrives with its children and nothing spills to disk. Snapshot
isolation is the default; a refusal names the `ALTER DATABASE` fix and the `readCommitted`
alternative, and deadlocks are retried.

Rendering runs on a bounded pipeline: batches of source records flow through a bounded
channel to a configurable number of renderers (`reliability.renderParallelism`), and the
plan entries stream out the other end into the ledger and the work batches. Peak memory is
the channel's capacity times the batch size, never the table.

### 16.2 Work batches

The intake writes rendered documents to JSON Lines work batch files under the flow's work
location (`source.work`), `reliability.batchRecords` documents
per batch, and the ledger's record row carries only the batch number and the document's
byte range within it. A drain leases a whole batch (its due records under one lease
token), reads the documents by range, and hands the protocol up to
`protocolOptions.batchSize` records per request where the service takes arrays. A batch
that finishes closes with its counts; a crashed drain's lease expires and the next drain
reclaims the batch. The submission page lists the batches.

### 16.3 Steps and returned values

A protocol reports every step it takes (a record write, an upload, a registration, a
workflow trigger, a poll) with its timing, the status the target answered and what the
target returned: record ids and versions, file sources and dataset ids, a session id, a
workflow run id. A completed step is persisted on the record before the next step starts,
so a retry resumes after the last step that succeeded instead of repeating it: a file
uploaded and registered by the previous try is referenced, not uploaded again; a workflow
run triggered by the previous try is polled, not triggered again. A step whose repetition
would create a second thing is marked before its request goes out, not only after it
returns: the file service mints a dataset record per accepted registration, so a try that
stops between the response and the report leaves a mark carrying the landing-zone path,
and the next try asks which dataset that path became instead of registering it again. The values the target
returned merge into the record's target state, and every attempt carries the full step
list and the returned values, so the ledger reconstructs what the target holds for a
record and how it got there.

### 16.4 Fan-out

A submission above `reliability.fanOutMinRecords` records, on a flow with
`reliability.fanOut` above zero, spreads across the fleet. The parent deliver run
registers the submission, takes its own share of the work, and enqueues `intake` member
runs for the rest. The work is cut into contiguous **key slices**, bounded by
`ROW_NUMBER() OVER (ORDER BY keys)`: at least a work batch of records each and never more
than 1024 slices, recorded on the submission, so every member reads exactly its own range of
the record key and no two members plan the same record. When the members report, it finalises the planning, enqueues `drain` members that lease batches
concurrently with it, waits for them, settles what is left (expired leases, records in
backoff), and completes the submission. Members ride the platform's run queue as one
family under the parent: they pass the pipeline gate together, they are cancelled with
their root, and the run page shows the family. A host without a catalog cannot fan out
and runs the whole submission itself.

### 16.5 The trace stays at operation grain

Fifty million rows must not produce fifty million trace events. The run trace carries the
operations: the intake's counts, every batch's outcome, every protocol step that changed
the target, the fan-out members, the settle. Per-record lines exist only at the trace log
level, off by default; per-record history lives in the ledger, where it is indexed.

## 17. Open decisions

These block schema design and should be settled first.

1. **Delivery grain.** `(wellbore, log source)` as today, or `(source project, log id)` as
   the source models it. This is a domain question about whether multiple logging runs of
   the same type on one wellbore should be one OSDU record or several. Everything else
   follows from the answer.
2. **Deterministic client-supplied OSDU ids.** Confirm OSDU and the data partition accept
   them for the kinds in scope. If yes, adopt; most of section 2 dissolves.
3. **Where rendering runs.** petrodb-api with the runtime renderer, or the delivery
   service. Either works and the pinned inputs are neutral, but it determines whether the
   translate renderer is vendored.
4. **Storage and database access.** Whether the nodes' identity can be granted read on the
   ingestion database the flows declare and on every payload root they stream from. On the
   critical path.
5. **The three preserved keys.** `Datasets`, `DDMSDatasets` and `ExtensionProperties` sit
   outside the content hash. Decide whether they are ours.
6. **Ledger retention.** Attempt table growth, rollup and aging, before the schema exists.
7. **Change Data Feed.** Whether to enable it on the source tables in exchange for exact
   changed-row sets including deletes.
8. **Library ownership.** `osdu-client` provides the typed data model, `osdu-csharp-client`
   provides transport with a streaming bulk facade and spec provenance. Both are in-house.
   Better for the two teams to agree which owns which layer than to pick around them.
9. **Do the other packages want this?** `els`, `smda_load` and `osdu_to_adx` carry three
   separate state implementations totalling 1,089 lines, none shared. Whether they want the
   same contract, or diverge for real reasons, changes the shape of what gets built.

## 18. Sequencing

1. Settle decisions 1, 2 and 4.
2. Build the ledger schema and the record-grained lease-and-retry worker. This is the
   artifact everything else depends on and it survives whichever engine runs it.
3. Pin the reference data as cache versions and the schemas as templates. Rendering becomes
   reproducible, and `plan` starts working without OSDU.
4. Move the mapping from generated to interpreted, proving byte-identical output against
   the 56 example fixtures before and after.
5. Add change detection, metadata first, then payload.
6. Close the loop back to the preparing side (superseded by the pre and ingestion flows;
   see section 6.7).
7. The file and manifest protocols, the streaming intake with work batches and fan-out,
   and the retrieval kind (sections 8, 15 and 16) landed once the first kind was real.

Change detection is the smallest piece that converts delivery into maintenance, but it
depends on identity and reproducible rendering, so it lands fourth rather than first.
