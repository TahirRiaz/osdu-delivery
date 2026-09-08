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
  volume. Databricks does that and hands over prepared data.
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

Three components, each keeping what it is already good at.

```
  Databricks                    storage account              OSDU Delivery                petrodb-api
  ----------                    ---------------              -------------                -----------
  read Unity Catalog     -->    source-shaped parquet  -->   render (mapping)      -->    map + OSDU envelope
  build curve grids             opaque payload chunks        hash + skip                  OSDU client
  write drop + manifest         manifest                     ledger, lease, retry         (stateless)
                                                             stream payloads                    |
                                                                                                v
                                                                                              OSDU
```

**Databricks** stays because Unity Catalog managed tables have no door from outside, and
because the curve grid build is distributed Spark work. It reads, builds grids, writes a
drop, and posts one manifest notification. It owns no delivery state and makes no OSDU
calls.

**OSDU Delivery** owns delivery. It reads the drop, renders documents, decides what
changed, maintains the ledger, leases work, retries, streams payloads, and verifies.
It knows nothing about OSDU's APIs.

**petrodb-api** stays stateless. It keeps the OSDU relationship, the mapping renderer,
and the reference resolution, and gains upsert-by-natural-key. It never grows a database.

### 3.1 Why the handoff is a storage account

Unity Catalog managed tables and managed volumes are both unreadable from outside
Databricks without credential vending. `prepare_osdu_payloads` currently issues
`CREATE VOLUME IF NOT EXISTS` with no `LOCATION`, which creates a managed volume.

The drop must be an **external** location: an external volume with an explicit
`LOCATION`, or a plain `abfss://` path, on a container the delivery node's identity
can read. This is a small code change plus a UC external-location grant, and the grant
is on the critical path.

### 3.2 Why the payload goes past, not through

The delivery worker holds retries, which means it must be able to re-send a payload.
Buffering payloads in the worker would size its memory by the largest wellbore. Instead
the drop holds the payload files and the manifest holds pointers. Retry re-opens the
blob. See section 13.

### 3.3 Trigger

The preparing side posts a **manifest notification** when a drop is complete:
`POST /api/v1/delivery/submissions` with the flow, the drop location and the flow
parameters the drop was prepared with. The control plane queues a deliver run for it and
answers with the run id; the run's intake registers the submission under the manifest's
`submissionId`, so a notification repeated for the same drop is idempotent. Explicit
notification beats polling a container, because it carries the idempotency key and cannot
race a partial write.

The preparing side makes exactly one HTTP call per run. A platform schedule can also
deliver the flow's declared drop on a cadence, as a fallback for a missed notification.

## 4. The four inputs and the render context

A rendered document is a pure function of four inputs. They change on different clocks
under different owners, and the danger is not any one being wrong but the four being
mutually inconsistent.

| Input | Owner | Clock | Artifact |
|---|---|---|---|
| Source data | Databricks / Recall | Continuous | The drop, plus a declared source contract |
| Mapping | This repository | Deliberate, gated | A versioned mapping document |
| Reference snapshot | OSDU | Periodic sync | Versioned, immutable |
| Target schema snapshot | OSDU | Pinned by the mapping's kind | Versioned, immutable |

### 4.1 The render context

The three non-source inputs are pinned together as a **render context**:

```
renderContext = (mappingVersion, referenceSnapshotVersion, schemaSnapshotVersion)
```

It is fixed for a render, recorded in the ledger against every document produced, and it
enters the content hash. That single construct gives reproducibility, correct
invalidation when any input moves, and the ability to find every document produced under
a bad combination after the fact.

The schema snapshot version is pinned **by** the mapping, through the mapping's declared
kind. An OSDU schema upgrade is therefore a mapping change and inherits the mapping's
blast radius and its gate.

### 4.2 The mapping is interpreted, not compiled

The existing `MappingGenerator` compiles one mapping document into C# mappers, C#
endpoints, a Python client and a PySpark select. Those outputs were copied across a
repository boundary and have since drifted from production in both directions.

A mapping is a template, not a program. Every construct in the current document is
interpretable at request time: `TargetProperty` is a dotted JSON path, `Transform` plus
`TransformConfig` is a coercion vocabulary, `IsCollection` with a definition reference is
a repeater, and a non-collection reference is a single nested block. Interpret it, and
delete the generators.

The principle that separates the two cases: **generate from what you do not own,
interpret what you do.** OSDU's schema is external and has one authoritative upstream, so
generated types cannot drift from it. The mapping is internal and changes on our clock,
so compiling it into two places guarantees drift.

### 4.3 The mapping does not restate the target

OSDU publishes a JSON Schema per kind, and `osdu-client` already carries 1,427 generated
classes covering abstract, master-data, reference-data, dataset, work-product and
work-product-component types.

So the mapping declares only what the schema cannot know:

- the source binding
- the transform
- the natural key
- envelope policy: which legaltag and ACL apply

Types, requiredness, relationship targets and units all come from the pinned schema. The
current document spends most of its 1,056 lines restating exactly those, creating a
second source of truth for facts OSDU already publishes.

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
and you redeliver for nothing, or a transform changes and you deliver nothing when you
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
and into a versioned snapshot. Today the same input can render differently on two
replicas, or before and after a refresh interval, and `OsduReferenceCachesHostedService`
logs initial-load failures and continues by design, so a replica can serve from an empty
reference set with no signal.

### 6.3 Two hashes, decided independently

The metadata document and the payload are delivered by different calls and change at very
different rates. Coupling them means a corrected `LogRun` re-uploads a hundred megabytes
of grid.

- `metadataHash` over the canonical rendered document
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

**Tier 0, whole run.** Record the Delta commit version of each source table per scope. If
none has advanced since the last run, skip the entire run. This costs nothing and is what
makes frequent scheduling free on quiet hours.

**Tier 1, cheap per-record gate.** A source fingerprint plus the render context. If both
are unchanged, skip without rendering. `recallcommonmodel:update_date` already exists in
the source common model as a source-derived audit field alongside `create_date`, and is
currently emitted by the generator as `DateStamp` but not selected by production. The
signal is available and unused.

**Tier 2, authoritative.** Render and compare `contentHash`. This can still skip when the
render turns out identical despite a changed source column.

Two limits to respect. Watermarks **cannot see deletions**, so a periodic full fingerprint
pass is required as a backstop. And Change Data Feed is not enabled on any source table;
enabling it would give exact changed-row sets including deletes, which is strictly better
than a watermark and is worth evaluating against its retention cost.

### 6.7 Closing the loop back to Databricks

Skipping on the delivery side saves the network. Prepare has still built every grid,
encoded it and written every chunk. To skip the Spark work as well, prepare needs to know
what the delivery system already holds.

The delivery service publishes a compact **known-state snapshot** (delivery key, source
fingerprint, payload hash) to a location Databricks reads at the start of a run. That is
the difference between a quiet run costing a full Spark job and costing almost nothing,
and it is the point at which the two halves stop being a producer and a consumer and
become one delivery system.

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

Use SQL Server through EF Core, following SQLFlow's catalog pattern including its
migration discipline: any change to the entities is incomplete until its migration
exists, and the schema is upgraded only by migrations.

### 7.2 Three levels

```
submission   one drop handed over by Databricks
  record     one deliverable, keyed by delivery key
    attempt  one delivery try, append-only
```

**Submission** carries the idempotency key, the drop location, the render context and the
scope. It is what makes run-scoped reporting honest: the current end-of-run summary counts
every success and failure for a log source rather than the records the run touched.

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
| Manifest ingestion | `workflow` | Assemble a manifest, trigger a DAG, poll to completion |

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

Also: the mapping validates against the schema snapshot, the flow validates against
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
  location: abfss://lake@account.dfs.core.windows.net/osdu-prepare/{logSource}
  records: metadata/*.parquet
  payloads:
    curves: curves/{deliveryKey}/chunk_*.parquet

render:
  mapping: WellLog@1.4.0
  references: pinned

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

Express it in target terms: the mapping names which of its mapped properties form the
natural key. The mapping already declares each property's source binding, so naming the
key properties is enough to derive both the source columns and the deterministic delivery
key. No duplication.

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

1. Every source binding the mapping names exists in the drop's declared schema.
2. Every reference type the mapping resolves against exists in the reference snapshot.
3. Every schema-required property has a binding that resolves.
4. Every target path resolves to a real field in the pinned schema, with agreeing types.
5. The mapping's own example fixtures still render correctly under this exact context.

If the combination does not validate, nothing renders. Not a warning.

### 10.3 Schema validation is structurally complete and semantically blind

Point 4 would not have caught the `recall_curve` bug. Both are strings, both bind to a
string field, both validate. The example fixtures at point 5 are what catch semantic
drift, which is why the 56 example pairs already in the mapping are worth preserving
through any format change. They are a per-property regression suite generated from the
contract itself.

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
| `check` | CLI: `sqlflow check <flow.yaml>` | Everything checkable without a network: document parse, the mapping against the schema snapshot, the reference snapshot and, when the drop is present, the manifest and the source bindings. |
| `plan` | CLI: `sqlflow run <flow.yaml> --operation plan`; GUI and API: a run with operation `plan` | Renders documents and reports what would be created, updated, skipped or held. Works offline against pinned snapshots. Changes nothing. |
| `deliver` | CLI: `sqlflow run <flow.yaml>`; GUI and API: a run, a schedule fire, or `POST /api/v1/delivery/submissions` | Executes a submission: intake, plan into the ledger, deliver what changed. |
| `verify` | a run with operation `verify` (the record page queues one scoped to the record) | The drift pass: compares OSDU's current version against `targetVersion`. |
| `known-state` | a run with operation `known-state` | Publishes the compact known state the preparing side reads. |
| `intake`, `drain` | the fan-out members a deliver run enqueues (section 16.4); also runnable by hand | `intake` registers and plans a drop (or some of its partitions) into work batches without delivering; `drain` delivers the pending batches of a submission (or of the whole flow) without reading the drop. |
| `retrieve` | a run on a retrieval flow (its default); `plan` on the same flow counts | Pages OSDU's search index into files on the lake (section 15). |
| `snapshot` | CLI: `sqlflow snapshot <flow.yaml> schema`, `references`, `list` | Captures reference and schema snapshots into the repository's snapshot store and mints a new version. |
| release, redeliver, delete, read back, probe | the GUI record and flow pages; `POST /api/v1/delivery/records/{key}/...` | Interventions, recorded in the ledger's activity trail under the user who asked. The ones that touch OSDU (delete, read back, probe) run on a node as compute tasks. |

`plan` working offline is a direct consequence of snapshotting the references and schemas.
It is also the single most valuable operational feature here, because it makes a mapping
change previewable against real records before it touches a governed store.

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
- **A catalog on SQL Server through EF Core, with migrations as the only upgrade path.**
  The ledger's tables live in the catalog's `delivery` schema, so one database, one
  migration history and one connection serve both.
- **The run queue, nodes and pools, schedules and chains, the git sync, identity, tokens,
  notifications and the GUI workbench.** Every delivery run is a platform run with a live
  trace and a run artifact; every intervention is an activity in the ledger and, when it
  ran as a run, a run in the history.
- **File stores, the secret chain and redaction.** Local and Azure Blob reads, `${env:...}`
  and `${keyvault:...}` references, secrets redacted before any log or row. The delivery
  domain adds only the writers it needs (snapshots, the known state).
- **The HTTP reliability stack.** The delivery copies in `src/SqlFlow.Delivery/Http` keep
  their vendored headers because they diverged from the platform's originals: a request
  factory per attempt so a binary payload streams and retries, no charset handling.

### 12.2 Kept record-grained, on purpose

- The platform's run is the grain of scheduling, tracing and history. The ledger's record
  is the grain of custody. A deliver run is one submission's intake plus drain; the ledger
  carries what each record went through, linked to the run id.
- A flow's own parameters (`parameters:`) are substituted into the drop location and travel
  as run parameter values, recorded on the run and on the submission.
- The mapping and the snapshots live in the flow's repository (`mappings/`, `snapshots/`),
  synced into the catalog as read models and never edited through the API.

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
a large wellbore parallelises instead of pinning one executor. Chunks already carry an
index and the session sends them in order, so the protocol accommodates this unchanged.

### 13.3 Budget it explicitly

With streaming throughout, worker memory is `maxConcurrentDeliveries * bufferSize` plus
one row group. Concurrency is the only multiplier, so set it deliberately rather than
inheriting a default.

## 14. Dependencies and approval

### 14.1 A deliberately small surface

The drop is plain parquet, so no Delta reader is needed: the drop reader in `src/SqlFlow.Delivery` uses
`Parquet.Net`, pure managed, and reads `abfss` through `Azure.Storage.Blobs` and `Azure.Identity`. There is
no native code on the delivery path.

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

Reads in service of writing were always here: the verify pass by id, schema and reference
snapshot fetches, reference resolution on a snapshot miss. Bulk inbound is the retrieval
kind, `flowType: retrieval`, added because the lake needs OSDU's records back without a
second export pipeline ([decisions/0008](decisions/0008-retrieval-lands-raw-records.md)).

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

Every run writes one row to `delivery.Retrieval` (the window, the location, the counts,
the outcome, the run id and the actor) when it starts and closes it when it ends, so the
GUI lists a flow's retrievals, and the manifest on the lake and the row in the ledger say
the same thing. The trace carries one line per kind, per hundred pages and per file, never
per record. The plan operation counts what the query matches per kind
(`POST /api/search/v2/query` with `trackTotalCount`) and writes nothing.

What the retrieval kind does not do: it never renders. A mapping is not invertible, since
an equality transform collapses a string to a boolean, a split discards everything but one
element, and constants have no source at all. What lands is the record as OSDU holds it.

## 16. Scale: streaming intake, work batches, returned values and fan-out

A drop can hold millions of rows and a flow billions over time. Nothing in the engine
holds a drop, a scope or a batch of rendered documents in memory, and one run can spread
its work across the fleet ([decisions/0006](decisions/0006-work-batches.md)).

### 16.1 The intake streams

The drop reader never materialises a scope. Root rows stream one row group at a time. A
drop whose manifest declares itself `partitioned` (root file i and child file i hold the
same records, each file sorted by delivery key) is merge-joined partition by partition in
lockstep; any other drop's child scopes are spilled to a disk-backed hash partition keyed
by delivery key (buckets sized to a target of 32 MB, at most 1024 of them) and joined
bucket by bucket. An unsorted partitioned file is a validation error, not a wrong join.

Rendering runs on a bounded pipeline: batches of source records flow through a bounded
channel to a configurable number of renderers (`reliability.renderParallelism`), and the
plan entries stream out the other end into the ledger and the work batches. Peak memory is
the channel's capacity times the batch size, never the drop.

### 16.2 Work batches

The intake writes rendered documents to JSON Lines work batch files under the flow's work
location (`source.work`, or `.work` under the drop), `reliability.batchRecords` documents
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
run triggered by the previous try is polled, not triggered again. The values the target
returned merge into the record's target state, and every attempt carries the full step
list and the returned values, so the ledger reconstructs what the target holds for a
record and how it got there.

### 16.4 Fan-out

A submission above `reliability.fanOutMinRecords` records, on a flow with
`reliability.fanOut` above zero, spreads across the fleet. The parent deliver run
registers the submission, takes its own share of the drop's partitions, and enqueues
`intake` member runs for the rest (each scoped to a partition set); when the members
report, it finalises the planning, enqueues `drain` members that lease batches
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
   service. Either works and the snapshots are neutral, but it determines whether the
   translate renderer is vendored.
4. **Storage access.** Whether the delivery service's Radix identity can be granted read on
   the drop container, and the UC external-location grant for the drop. On the critical
   path.
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
3. Snapshot the reference and schema data. Rendering becomes reproducible, and `plan`
   starts working offline.
4. Move the mapping from generated to interpreted, proving byte-identical output against
   the 56 example fixtures before and after.
5. Add change detection, metadata first, then payload.
6. Close the loop back to Databricks with the known-state snapshot.
7. The file and manifest protocols, the streaming intake with work batches and fan-out,
   and the retrieval kind (sections 8, 15 and 16) landed once the first kind was real.

Change detection is the smallest piece that converts delivery into maintenance, but it
depends on identity and reproducible rendering, so it lands fourth rather than first.
