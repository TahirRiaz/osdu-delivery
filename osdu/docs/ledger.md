# The ledger

The ledger is the system ([design.md](design.md) section 7): the tables in the `osdu` schema that say, per record,
what OSDU holds, what is waiting, and everything that ever happened to it. Everything an operator or a dashboard asks
is answered from here, and every answer is index-backed.

The `osdu` schema lives in the same database as the platform's catalog, so one backup covers both and a run row and
the attempts it produced are joined by id, but it is the module's own: its own EF context (`OsduDbContext`), its own
migration history and its own schema version, with no foreign keys into SQLFlow's tables
([architecture.md](architecture.md)). The control plane reaches it on the catalog's connection; a node, which opens
no catalog connection at all, reaches it through a connection reference of its own.

## Tables

### `osdu.Submission`: one plan of a flow over its ingestion tables

| Column | Purpose |
| --- | --- |
| `SubmissionId` | Primary key and idempotency key. |
| `FlowId`, `FlowName`, `MappingReference`, `RenderContext` | What produced the run. |
| `ParametersJson`, `RecordCount` | The parameter values the plan read with, and how many records it covered. |
| `WorkLocation`, `BatchCount`, `Slices` | Where the intake wrote its work batches, how many, and how many key slices it cut the work into for its fan-out members. |
| `Kind` | Which selection was read: `incremental` (the rows changed in a window after the scope's watermark), `full` (every row of the scope), or `keys` (named record keys: a record-scoped run, or the records the ledger asked to plan again). |
| `SourceConnection`, `SourceObject` | The ingestion database's connection reference exactly as the flow declares it (never a resolved secret), and the three-part name of the record table read. |
| `WindowFromUtc`, `WindowToUtc` | The `UpdatedDate_DW` window the plan covered. Both null for a keys plan with no window; a full plan records its upper bound. |
| `SourceWindowJson` | What else bounded the read: the child dataset objects, the overlap seconds, the scope values, the slice boundaries, the key count, and for a keys plan the key digest. |
| `RunId` | The run that coordinated the submission. |
| `Untracked` | Rows the plan read that carried no complete record key, so nothing could be delivered under them. |
| `Status` | `received`, `planned`, `running`, `completed`, `failed`. |
| `Planned`, `SkippedUnchanged`, `AwaitingApproval`, `SkippedStale`, `UnchangedAtPush`, `Blocked`, `Delivered`, `Held`, `Failed` | Counts scoped to the records this submission touched. `AwaitingApproval` counts the records a cache change waiting for a decision held back: rendered and ready, and not unchanged, so a run says how many wait on an approval instead of hiding them among the records it had no reason to send. `Planned`, `SkippedUnchanged`, `SkippedStale` and `Blocked` describe its latest planning pass: a re-run of the submission plans it again and replaces them. `Delivered` and `UnchangedAtPush` count the distinct records the submission's attempts delivered, or found OSDU already holding at the final hash check, across every pass. `Held` and `Failed` count the records whose last submission this is and that are held or failed now. A re-run that re-sends one record of three already delivered therefore shows 1 planned and 3 delivered; what that run itself did is on the run (`recordsPlanned`, `recordsDelivered`). `SkippedStale` counts rows older than the version delivered or queued. |
| `ReceivedUtc`, `StartedUtc`, `CompletedUtc`, `Error` | Timeline. |

The platform's run row carries the submission too: `Run.SubmissionId` when a run was asked to re-run one, and
`Run.ResultSubmissionId` for the submission a deliver run registered or completed, so a submission page lists
the runs that carried it.

### `osdu.Record`: the current state of one deliverable

| Column | Purpose |
| --- | --- |
| `FlowId`, `DeliveryKey` | Primary key. The flow, and the deterministic key derived from source data. The same row read by several flows is one record per flow ([One source, several flows](#one-source-several-flows)). |
| `SourceKey`, `Label`, `MappingName` | Provenance. `Label` is the mapping's `dataset.label` rendered for the row (a wellbore name, a log name), for search and display only. |
| `RenderContext`, `SourceFingerprint`, `MetadataHash`, `PayloadHash` | What OSDU holds: the gates for tiers 1 and 2. `SourceFingerprint` is the ingestion fingerprint, computed over the record row's `UpdatedDate_DW` and, per child dataset, its row count and newest `UpdatedDate_DW`. |
| `SourceModifiedUtc`, `PayloadModifiedUtc` | The last-modified moment of the source row, and the newest modified time of the payload files, that OSDU's document and payload were built from: the watermarks an incremental run is ordered against. |
| `SourceKeyJson` | The record's key tuple as a JSON array, in `source.record.key` order: what a key-scoped read of the ingestion tables uses. |
| `SourceFileName`, `SourceRowNumber`, `SourceUpdatedUtc` | Where the version OSDU holds came from: the ingestion row's `FileName_DW`, `RowNumber_DW` and `UpdatedDate_DW`. |
| `PendingSourceFileName`, `PendingSourceRowNumber`, `PendingSourceUpdatedUtc` | The same for the queued version, or for the state a held, failed or deleted record was left in. |
| `PlanRequestedUtc` | Set when the ledger asks for the record to be planned again (a redeliver, a release with no pending document, a cache rollout); the next run pages these records and plans them as a keys selection, and planning clears it. |
| `TargetId`, `TargetVersion` | The OSDU id and the last known version (the drift handle). |
| `ClaimedTargetId` | The OSDU id the record claimed for its flow when it first queued a document, kept for good. Unique across the ledger: one OSDU record belongs to one flow. Null for a record that was only ever held. |
| `Status` | `pending`, `delivering`, `delivered`, `held`, `failed`, `deleted`. |
| `Blocked` | Set when the record was held, failed or deleted and not released since. |
| `LastDeliveredUtc`, `LastVerifiedUtc`, `LastVerifyOutcome` | Custody timestamps. |
| `LeaseOwner` | The token of the lease that holds the record while a worker delivers it ([Leasing](#leasing)); the lease row says when it runs out. |
| `LastSubmissionId`, `AttemptCount`, `NextAttemptUtc`, `LastError` | The pending work's progress; `LastError` is redacted. |
| `Pending*` | The hashes, context, fingerprint and payload location of the work waiting to be delivered. |
| `WorkBatch`, `PendingDocumentRef` | Where the pending rendered document is: the work batch and its `batch:offset:length` range in the batch file. The document itself lives on storage, never here. |
| `PendingStepJson` | The steps an earlier try of the pending work completed, with what the target returned, so the next try resumes after them. |
| `TargetStateJson` | Every value the target returned across the record's deliveries (record id and version, dataset ids, file sources, a workflow run id): what OSDU holds for the record. |

### `osdu.Attempt`: append-only, one row per delivery try

The record it belongs to (`FlowId`, `DeliveryKey`), worker, start and end, outcome (`delivered`, `skipped`, `failed`, `held`, `deleted`, `historypurged`), the
phase delivered (`metadata`, `payload`, `metadata+payload`, `delete`, `purge-history`, `none`), the hashes
established, the version returned,
the redacted error (for a held or failed try only: a try that did not fail keeps its note, chunks sent or why nothing
was sent or what a removal took, as `detail` in its result), the platform `RunId` the attempt happened in, the `WorkBatch` it was drained from, and
`ResultJson`: every step the protocol took (name, timing, status, what the target returned, whether an earlier
try had completed it) and the values returned. Render-time holds are written by the intake with worker
`intake`; deletions by the actor who asked for them.

An attempt's result names the `correlationId` every OSDU request of that try carried in the `correlation-id` header,
so the attempt can be found in the services' own logs (a removal names the id its chunk's calls carried, with what OSDU
held for the record under `returned`), and the error of a refused or failed request quotes the id the
service answered with. The OpenAPI descriptions do not declare the header; the storage service answers with the id it
is sent, and with one of its own when it is sent none.

### `osdu.WorkBatch`: one file of rendered documents

| Column | Purpose |
| --- | --- |
| `SubmissionId`, `Index` | Primary key: the batch's place in its submission. |
| `FlowId`, `Location`, `RecordCount` | Whose it is, where the JSON Lines file is, how many documents it holds. |
| `Status` | `queued`, `running`, `done`, `failed`. |
| `LeaseOwner`, `RunId` | The token of the lease its drain holds, and the run it is being drained in. |
| `CreatedUtc`, `StartedUtc`, `CompletedUtc` | Timeline. |
| `Delivered`, `Held`, `Failed`, `Retrying`, `Error` | How its drain ended. |

A drain claims the oldest queued batch of the flow (or of one submission) under a lease of its own and marks the
batch's due records with the lease's token; the records' rows point at the batch and their range in its file. A batch
whose drain stopped is recovered with its records once the lease runs out.

### `osdu.Lease`: one claim of a worker

| Column | Purpose |
| --- | --- |
| `Token` | Primary key: the worker's name, a slash and a 32-character id, minted per claim. The records the claim holds carry it in `LeaseOwner`, and so does a claimed batch. |
| `FlowId`, `SubmissionId`, `WorkBatch` | The flow; the submission the claim was limited to; the batch a drain claimed, null for a claim of records outside a running batch. |
| `Owner` | Who holds the lease: the worker that claimed it, or `{machine}/{process}/recovery` once a recovery has taken it over. |
| `RunId`, `AcquiredUtc`, `ExpiresUtc` | The run it was claimed in, when, and when it runs out unless it is renewed. |

A worker renews its one lease row, however many records the lease holds.

### `osdu.RecordEvent`: what a worker learned and its lease has not applied yet

One row per step a try completed and per try that ended, appended while the lease is out and deleted as the lease
applies it ([Leasing](#leasing)).

| Column | Purpose |
| --- | --- |
| `EventId` | Primary key, ever-increasing: the order the events were appended in. |
| `LeaseToken`, `FlowId`, `DeliveryKey` | The lease it was appended under, and the record it concerns. |
| `Kind`, `AtUtc` | `step` (a step completed; `StepJson` holds the steps so far) or `completion` (a try ended), and when. |
| `Status`, `Promote`, `NothingSent`, `NextAttemptUtc`, `Error`, `TargetId`, `TargetVersion`, `TargetStateJson`, `PendingStepJson` | For a completion: how the try settles the record. |
| `Claim*` | What the try was claimed with (submission, document, render context, fingerprint, origin, hashes), so a completion promotes what the try actually delivered even when newer work was queued meanwhile, and a step is kept only while the record still holds that document. |

A try's attempt is written to `osdu.Attempt` in the transaction that appends its completion, so the record's history
is complete as soon as the try ends.

### `osdu.Retrieval`: one run of a retrieval flow

| Column | Purpose |
| --- | --- |
| `RetrievalId` | Primary key. |
| `FlowId`, `FlowName`, `RunId`, `Actor` | Whose it is, the platform run, who asked. |
| `Kinds`, `Query` | The kinds covered and the query as it ran, window included. |
| `WindowField`, `WindowFrom`, `WindowTo` | The incremental window; `WindowTo` of the last done run is the next run's lower bound. |
| `Location`, `ManifestLocation` | The run's directory on the lake and its manifest. |
| `Status` | `running`, `done`, `failed`, `cancelled`. |
| `Records`, `Files`, `Bytes` | What was written (bytes uncompressed). |
| `StartedUtc`, `CompletedUtc`, `Error` | Timeline and the redacted error. |

### `osdu.Activity`: the audit trail of runs and interventions

One row per operator or scheduler action: `deliver`, `intake`, `drain`, `submit`, `verify`,
`release`, `redeliver`, `delete`. Each carries the actor (the run's requesting user, `schedule` or `manual` for a run;
`user:<name>` for an intervention from the GUI or the API; `cli:<user>` from a workstation), start and end,
outcome (`running`, `completed`, `failed`, `cancelled`), the parameters as JSON, the submission, record and
platform run it targeted when it targeted one, a summary and, for runs, the captured run log.

Record history is the attempts; run and intervention history is the activities. A record's page in the GUI
shows both, plus its verify outcomes; a run's page links to what it did to each record through the run id.

### `osdu.SourceWatermark`: tier 0

One row per `(FlowId, Scope)`, where the scope is the flow's parameter set: `UpdatedThroughUtc` is the upper bound of
the last completed whole-scope plan, with the `SubmissionId` that wrote it, when it was recorded, and the render
context it was written under. The next run reads the window `(UpdatedThroughUtc - overlapSeconds, now]`, and a scope
with no watermark is read in full.

It moves only when a whole-scope plan finishes and every one of its fan-out members succeeded, so a failed member
leaves the watermark where it was and the next run covers the same rows again. A `keys` or `inline` plan never moves
it. When no row changed in the window and no record is waiting to be planned again, the run is skipped entirely,
unless it is forced.

### `osdu.Mapping` and `osdu.CacheDefinition`: what the repositories declare

Read models the repository sync writes: every mapping document (its reference, kind, the template version it pins
in `TemplateVersion`, a parsed summary, the YAML, and whether it parses) and every type a cache flow declares (the
cache flow's `FlowName` and `RelativePath`, the partition it fills as `Scope`, the `Endpoint` it searches as declared,
the type's `Name`, `EntityType`, `Kind` and `Query`, the kept paths as `FieldsJson`, and `OnChange`). A type is one
row per repository, cache flow and name (unique on `RepoId`, `FlowName`, `Name`), indexed on `Scope` and `Name`
because a refresh reads every declaration of its partition to know the paths the cache keeps for a type. A declaration
that disagrees with another flow's declaration of the same type for the partition is left out with a warning. They
back the GUI's Mappings and OSDU cache pages, and the templates listing counts each template version's pins from
`osdu.Mapping`; nothing writes them but the sync.

### `osdu.CacheVersion`, `osdu.CacheItem` and `osdu.CacheMember`: one cache per partition

What a cache holds lives here and nowhere else ([design.md](design.md) section 6.2): nothing about it is written to a
repository. There is one cache per OSDU data partition, keyed by the partition (`Scope`, the `data-partition-id` the
cache flows declare), and every cache flow of the partition writes into it. A version is written by a refresh run whose
merge changed the cached content, or by `sqlflow cache import`, and never changes afterwards; the newest version of a
partition is its current one. Every version is kept, because a delivered record's
render context names the version it was rendered against.

| Column | Purpose |
| --- | --- |
| `Id` | Primary key, derived from the partition and the version label. |
| `Scope`, `Version`, `Sequence` | The partition, the label minted from the capture instant (`20260908T212727Z`, with the sequence appended when two captures of the partition share a second, as in `20260908T212727Z-7`), and the version's place in the partition's history, 1 for the first. `(Scope, Version)` and `(Scope, Sequence)` are both unique; the sequence is what a concurrent write of the same partition collides on, so it fails rather than interleaving. |
| `FlowName` | The cache flow whose capture, or import, wrote the version. |
| `CapturedUtc`, `CapturedBy`, `RunId`, `Origin` | When it was captured; who asked (the run's trigger, or `cli:<user>` for an import); the platform run that captured it, null for an import; the endpoint reference searched or the directory imported. |
| `ContentHash` | The hash of the whole content, checked on every load: a version whose records were altered after it was written is refused, and nothing renders against it. |
| `PreviousVersion`, `Current` | The version that was current when this one was written, which the capture was merged onto, and whether this is the newest version, the one deliveries render against unless a flow pins another. |
| `TypesJson`, `Items` | The types the version holds, each with its entity type and record count, and the records across them. |

`osdu.CacheItem` keeps the cached records by version range rather than by copy. A row is one record of one partition's cache
(`Scope`, `TypeName`, `RecordId`) with its captured values (`FieldsJson`, with every scalar also in `Terms` for search)
as a run of consecutive versions held them: from the version at `FromSequence` up to, and not including, the one at
`ToSequence`, which is null while the newest version still holds the record unchanged. A record is stored once per
partition however many cache flows capture it, and a merge writes rows only for the records that changed, arrived or
left, so keeping every version costs rows in proportion to what moved.

Both tables are stored in partition order: `osdu.CacheVersion` is clustered on `(Scope, Sequence)` and
`osdu.CacheItem` on `(Scope, ItemId)`, with their ids as nonclustered primary keys, and `osdu.CacheMember`'s key starts
with the partition too. Whatever a refresh reads or writes of its partition is then a range seek of that partition,
however small the tables are, so refreshes of different partitions never lock each other's rows. Clustered on their
ids, two partitions refreshing at once each waited on the other's new version row, and the database ended one of them as
a deadlock victim.

`osdu.CacheMember` is current state, not history: one row per partition, type, record and cache flow (`Scope`,
`TypeName`, `RecordId`, `FlowName`, which together are the key) saying that the flow's last capture of the type held
the record. It is what lets several cache flows share one partition's cache. A merge replaces the capturing flow's rows
for the types it captured, and a record the capturing flow no longer finds leaves the cache only when no other flow's
row still holds it. The rows of a type the merge removes go with it, and the repository sync deletes a flow's rows for
a type the flow stops declaring. What each version held is in `osdu.CacheItem`.

### `osdu.CacheSet`, `osdu.CacheSetEntry` and `osdu.UpdateTag`: what a cache change reaches

A `osdu.CacheSet` is one distinct combination of cached values a render consumed, shared by every record that read
the same values through the record's `CacheSetId`. Each `osdu.CacheSetEntry` is one value in it: the partition
whose cache it was read from (`Scope`, the partition the delivery flow delivers to), the type, the cached record, the
path, and the value as it was read. A `osdu.UpdateTag` is one change a refresh found in values delivered records
were built from: the partition (`Scope`), the type, the cached record, the
path, the value before and after, the versions it moved between, `Mode` (`approve` or `auto`), `Status` (`pending`,
`approved`, `rejected`, `rolling`, `applied`), how many delivered records it reaches and how far the rollout has got
([design.md](design.md) section 6.2).

### `osdu.Template`: the templates mappings pin

The OSDU schemas mappings are checked and rendered against ([mapping-templates.md](mapping-templates.md)). They are
saved in the catalog rather than in a repository, because the control plane runs as a container whose disk does not
survive a restart. A row is written by the GUI's Templates page, `POST /api/v1/delivery/templates`, or
`sqlflow template capture` and `import`, and never changes afterwards.

| Column | Purpose |
| --- | --- |
| `Id` | Primary key, derived from the kind and the version. |
| `Kind`, `Version` | The OSDU kind, and the content version: the first 16 hexadecimal characters of the hash of the canonical bundled schema. Unique together; a mapping pins both. |
| `SchemaJson` | The bundled JSON Schema, every reference resolved into its definitions. |
| `Origin`, `CapturedBy`, `CapturedUtc` | Where the schema came from (the release, commit and file of the OSDU data definitions, a local data definitions folder, or an imported file), who saved it and when. |

Saving a schema that is already saved adds no row. A version is deleted only while no `osdu.Mapping` row pins it
(the `(Kind, TemplateVersion)` index answers that), so a template a synced mapping pins cannot be removed from under
it.

## Record lifecycle

```text
                 intake                     worker
   (new/changed) ──────▶ pending ──claim──▶ delivering ──▶ delivered ◀── verify (drift → hashes cleared → next plan updates)
                            ▲                  │
                            │ backoff          ├──▶ held    (data problem or non-retryable status; Blocked)
                            └──────────────────┤
                                               └──▶ failed  (retry budget exhausted; Blocked)
   operator removal ──▶ deleted (Blocked; OSDU no longer holds it) [scope record or everything]
                   └─▶ (no change)                                  [scope history: OSDU still holds it]
   operator release ──▶ pending (when a rendered document is still there) or unblocked for the next plan
```

A **blocked** record (held, failed or deleted and not released) is skipped by every later plan as `blocked`
until either the source row changes (its fingerprint moves, or its last-modified moment passes the one it was left
at) or an operator releases it. That is what "do not retry without intervention" means in practice: a re-run of the
same data never re-attempts a known problem, while a corrected source row flows through on its own.

A released record that still holds its rendered document goes back to pending in its submission, and the flow's next
deliver run sends it, whatever that run itself plans: the row is what the record already queues, so a run whose plan
finds nothing new for it still sends it. Once a run's own records are sent it takes the due records of up to ten
completed or failed submissions and recomputes their totals. A `drain` run sends it at any time. A record released
without a rendered document is stamped to be planned again instead, and the next run reads it from the ingestion
tables by key.

Versions never go backwards. A row older than the version a record holds, delivered or queued, is skipped with an
attempt (`skipped`, phase `stale`) naming both versions, and staging refuses work older than what the ledger holds,
so two intakes racing for one record leave the newer version standing. Work planned for a record another worker is
delivering right now is written behind the delivery rather than dropped: the record keeps its lease, the completion
promotes what that try actually delivered (from the claim it carries, not from the columns the newer work replaced)
and leaves the newer work pending, and the try's step progress is kept only while the record still holds its
document. Before anything is sent, the worker compares the queued document and payload hashes with what the record
says OSDU holds at that moment and sends only the halves that differ; when neither does, it settles the record with
an attempt (`skipped`, phase `unchanged`) and sends nothing.

**Redeliver** forgets the hashes of what OSDU holds (all of them, or only the metadata or the payload) and stamps the
record to be planned again, so the next plan that reaches it sends that part again. From the GUI, redeliver also
queues a deliver run scoped to the record, which reads it from the ingestion tables by key, so the redelivery happens
at once and is recorded under the user who asked. It never bypasses the render: the document sent is always the one
the pinned mapping produces from the current source rows.

**Removal** takes the record out of OSDU through the flow's protocol, to one of three depths (see
[operations](operations.md#removing-records-from-osdu)). `record` and `everything` write a `delete` attempt,
forget the hashes and version, and block the record. If the source still presents the record and an operator
releases it, the next plan creates it again; that is ownership, not an accident.

`history` is the exception: it destroys the record's earlier versions and leaves the record itself live in OSDU
at the version the ledger already holds. Its custody state is therefore still true and is not disturbed; the
purge is written as a `purge-history` attempt and nothing else changes. Every removal, at every depth, names the
scope and the operator on the attempt and in the activity trail.

## One source, several flows

An ingestion table can feed several OSDU flows, each rendering the rows with its own mapping into its own OSDU kind
([design.md](design.md) section 5.4). The rows are loaded once; each flow keeps its own ledger over them.

- **A record is one flow's.** Its key is the flow and the delivery key together, so a row read by two flows is two
  records, each with its own state, attempts, activities, submissions and watermark. Every write the engine makes
  (staging, holds, claims, leases, completions, step progress, verify outcomes, removals) names the flow, and none of
  them reaches another flow's record of the same row. Statistics are counted per flow.
- **A record is addressed by both parts.** The API's record routes are `/api/v1/delivery/records/{flowId}/{deliveryKey}`
  and the GUI's record page is `/delivery/records/{flowId}/{deliveryKey}`. The flow id is the ledger's (derived from the
  flow's name), not the pipeline's, so a record's history stays reachable after its flow leaves the repository. The
  search box, given a delivery key, lists one record per flow that reads the row.
- **One OSDU record, one flow.** The OSDU id is `{partition}:{entityType}:{deliveryKey}`, so flows delivering to
  different entity types or partitions write different records. Staging claims a record's OSDU id the first time the
  record queues a document (`ClaimedTargetId`, unique across the ledger). Work whose id another flow's record has
  claimed is not staged: the record is held, with the owning flow named in its error, and nothing is sent. A release
  plans it again and meets the same conflict. To resolve it, deliver the second flow to another partition, or give its
  mapping a `dataset.system` or key that yields other ids. A new mapping has delivered nothing, so changing its
  identity re-keys nothing.
- **A flow acts only on ids it claimed.** The worker sends only a claimed id. A read back reads the claimed id, and a
  removal skips a record that claimed nothing, so a record that was only ever held can never reach another flow's
  OSDU record. A held record is not given an id another flow has claimed.
- **Races settle in the database.** Two flows' intakes staging the same new id at once are serialized by the unique
  index: the loser's slice of staging is rolled back, runs again, and then holds its record naming the winner. A slice
  the database ends as a deadlock victim is run again the same way (see [Many nodes, one table](#many-nodes-one-table)).

## Leasing

A worker does not write the record table while it delivers. It writes its lease row, the events it appends and the
attempts; the records change when the lease applies the events.

```text
claim:      INSERT Lease (Token, FlowId, SubmissionId, WorkBatch, Owner, ExpiresUtc = @now + lease)
            UPDATE Record SET Status='delivering', LeaseOwner=@token, AttemptCount+=1
              WHERE FlowId=@flow AND DeliveryKey IN (@due) AND Status='pending' AND <due>   (1,000 keys a statement)
renew:      UPDATE Lease SET ExpiresUtc=@now + lease WHERE Token=@token AND Owner=@worker   (one row)
append:     INSERT Attempt ...; INSERT RecordEvent ...                                      (one transaction a write)
checkpoint: apply the lease's events to their records, 1,000 records a transaction, deleting what was applied
close:      checkpoint; hand back what the lease still holds; settle its batch; DELETE Lease
recover:    UPDATE Lease SET Owner=@recoverer, ExpiresUtc=@now + 5 min WHERE Token=@token AND ExpiresUtc < @now
            then close it as expired
```

- **Claim.** A drain claims the oldest queued batch of the flow (or of one submission): the lease row and the batch's
  move from `queued` to `running` commit together, so two workers never hold one batch. The batch's due records are
  then marked with the lease's token, a thousand to a statement. A claim outside the batches takes up to 500 due
  records of the flow under a lease of its own. Either way the record's move from `pending` to `delivering` is a
  compare-and-swap, so two leases never hold one record, and any number of nodes share the ledger. That is what lets a
  submission's drains spread over the fleet ([design.md](design.md) section 16.4).
- **Renew.** The worker renews its lease at half its length (at least once a second apart), and at each renewal
  checkpoints the lease and reports its progress to the run's trace (`batch.progress`: how many of its records are
  settled so far, by outcome). A renewal that finds the lease taken over stops the worker: it stops sending, applies
  what it appended, and leaves the rest to whoever took the lease over. A renewal that fails on a database error is
  tried again until the lease would run out.
- **Append.** The worker's concurrent deliveries hand their completed steps and ended tries to the lease's journal,
  which writes them with group commit: one write carries whatever was handed over while the previous write was in
  flight, at most 500 entries. A delivery waits until its entry is written, so a completed step is in the ledger
  before the next step starts, and a try's `record.*` event reaches the run's trace only after its attempt is stored.
- **Apply.** A checkpoint applies the lease's events a thousand records to a transaction, in record order, each record
  taking its latest event. A completion settles the record as the try said (status, backoff, the pending state
  promoted, the target state) and releases it from the lease; a step keeps the step progress on the record while the
  record still holds the document the try was claimed with. A record another lease holds by then is left to that
  lease: the event is dropped, and the try's attempt stays in the history. The events go in the transaction that
  applies them.
- **Close.** A worker that ends its lease applies it and hands back what it still holds: records its batch never
  reached, or that a stop interrupted, go back to pending without charging the try, and the batch is settled (`done`
  or `failed` with its counts, or `queued` again after a stop). A worker whose lease was taken over applies what it
  appended, so a try it finished lands on a record no one else holds, and settles nothing else.
- **Recover.** Every claim of a flow first recovers the flow's leases that ran out. It takes each one over, a
  compare-and-swap on the lease row, so two recoveries never settle one lease and the stalled worker can no longer
  renew it. It applies what the stopped worker appended, hands the rest of its records back to pending with the try
  charged (`LastError` says the lease expired mid-attempt), and queues its batch again. A recovery that stops holds
  the lease for five minutes, and the next claim after that recovers it. The recovery also applies the flow's events
  that no lease will: a worker whose lease was taken over applies what it still appends when it closes, and one that
  stops before closing leaves those events behind. Events older than the five minutes whose lease row is gone are
  applied like any others.

The record table is therefore the read model of the deliveries: it shows what every lease has applied, which trails
what the workers have sent by at most one renewal. The attempts are written with each append, and the run's trace
carries the live progress.

A run recovered after its worker stopped (a control plane or node killed mid-delivery, whose run the reaper
requeued) finds its submission already planned, and the records its dead worker was sending still leased. The
platform runs one execution of a flow at a time, so such a lease belongs to nobody alive: the run waits until it
runs out, recovers it and sends the record, resuming after the steps the dead attempt had appended. Seen live, a recovered run that only passed over the submission finished `succeeded` with
the record left `delivering` and its change never recorded. A lease running out further ahead than the flow's own
`reliability.leaseSeconds` is left alone with a warning, because only a running worker renews a lease that far.

### Many nodes, one table

A fanned-out submission has every node staging, leasing, renewing and completing records in `osdu.Record` at once,
and a ledger of hundreds of millions of records must keep them from waiting on each other. What the ledger does on
SQL Server:

- **No statement writes more than 1,000 records.** SQL Server turns the row locks a statement holds on one index into
  a lock on the whole table once they reach 5,000, and a lock on the record table would stop every node of every flow
  while it lasted. Staging copies a batch into a staging table once and writes it a thousand records to a transaction.
  A batch's claim and hand-back, an operator's release or redelivery and a cache change's marking first read the keys
  of the records they reach and then write them a thousand keys to a statement. Every index of the table ends with the
  table's key, so such a statement finds each record with one seek and locks no record it does not write. A lease
  applies its events a thousand records to a transaction, a worker's append carries at most 500 events with their
  attempts, and pruning deletes 4,000 attempts to a statement.
- **A worker does not write the record table while it delivers.** Keeping a lease is one row, however many records it
  holds, and what the worker learns goes to `osdu.RecordEvent`, whose ever-increasing key takes every node's appends
  at its end without touching a record. The records change when a lease is checkpointed, closed or recovered.
- **Staging locks only records that exist.** A slice finds the records it will update by key and update-locks them to
  the end of its transaction, and inserts the rest. It never locks a range of keys, so intakes of one flow, whose new
  keys interleave, do not wait on each other. A record another staging inserted after the slice looked is refused by
  the table's key; the slice is rolled back and runs again, finds the record and compares its work with it, so newer
  work is never overwritten. The claim check is one seek of the claim index per record.
- **Reads never wait on writes.** Every read of the ledger (a worker's, the planner's, the GUI's, the API's and the
  CLI's) runs in a snapshot transaction: it sees what was committed when it started and takes no shared locks, so it
  neither waits for a writer nor holds one up. The connection is set back to read committed before it returns to the
  pool, so no write runs under snapshot isolation. The database must allow it ([Provisioning](#provisioning)). The
  claim's reads are also answered from `(FlowId, Status, NextAttemptUtc)` and
  `(FlowId, LastSubmissionId, Status, NextAttemptUtc)` alone.
- **A deadlock victim runs again.** SQL Server ends a deadlock by rolling one statement back. A staging slice, an
  append, an application slice, a claim and a lease statement are each run again, up to five times, after a short wait
  that grows with each try and differs between nodes; each is written so a second run does what the first would have.
  Anything else fails with the database's error.

The statistics view is maintained in the transaction of every record write, so the writes of one flow meet on its few
rows there. Each write holds them only until it commits, and every write above is short.

## Indexes and search

Listings are index-backed so the GUI answers in milliseconds at any estate size:

| Index | Serves |
| --- | --- |
| `Record (FlowId, Status, NextAttemptUtc) INCLUDE (LastSubmissionId, UpdatedUtc, PendingDocumentRef)`, `(FlowId, LastSubmissionId, Status, NextAttemptUtc) INCLUDE (UpdatedUtc)` | the worker's claim and its other reads (what is due next, the settled submissions with due work), for a flow and for one submission, from the index alone |
| `Record (FlowId, Status, UpdatedUtc)`, `(FlowId, LastSubmissionId, UpdatedUtc)` | a status's or a submission's records, most recent first, read in index order |
| `Record (LastSubmissionId, WorkBatch)`, `(LeaseOwner)`, `WorkBatch (FlowId, Status, CreatedUtc)`, `(SubmissionId, Status)` | the batch claim, its records, the records one lease holds (its hand-back, and the expiry a listing shows), the submission's batch list |
| `Lease (FlowId, ExpiresUtc)`, `(SubmissionId, ExpiresUtc)` | the leases of a flow that ran out, for the recovery; the next expiry a run waits for, for a flow and for one submission |
| `RecordEvent (LeaseToken, FlowId, DeliveryKey, EventId)`, `(FlowId, AtUtc) INCLUDE (LeaseToken)` | one lease's events in record order, a slice at a time, for its checkpoint; a flow's old events, for the recovery of those whose lease is gone |
| `Retrieval (FlowId, StartedUtc)`, `(FlowId, Status, StartedUtc)`, `(RunId)` | a retrieval flow's runs, the watermark chain (the last done run), the run's row |
| `Record (FlowId, Label)`, `(FlowId, SourceKey)`, `(FlowId, TargetId)` | prefix search (`LIKE 'term%'`) on the three identity columns |
| `Record (FlowId, UpdatedUtc)`, `(FlowId, LastDeliveredUtc)`, `(FlowId, LastVerifyOutcome)` | recency listings, the last delivery and the part-hour of the 24-hour count, drift |
| `Record` primary key `(FlowId, DeliveryKey)` | one flow's record, and key-ordered walks of one flow: a removal's key list, a keys selection's pages |
| `Record (DeliveryKey)` | the lookup across every flow by delivery key: one record per flow that reads the row |
| `Record (ClaimedTargetId) WHERE ClaimedTargetId IS NOT NULL`, unique, binary collation | one flow per OSDU id: the claim check staging runs, and the database's refusal of a second claim |
| `Record (CacheSetId, DeliveryKey, FlowId) WHERE CacheSetId IS NOT NULL` | a cache change's rollout, in key and then flow order from its cursor |
| `Record (FlowId, SourceFileName, SourceRowNumber)`, global `(SourceFileName)` | "which records came from this file", inside one flow and across the estate |
| `Record (FlowId, PlanRequestedUtc) WHERE PlanRequestedUtc IS NOT NULL` | the records the planner pages each run, so it stays as small as the backlog |
| `RecordCount` indexed view `(FlowId, Status, LastVerifyOutcome, DeliveredHour)` | flow statistics, read from a few rows per flow (see [Statistics](#statistics)) |
| `Attempt (FlowId, DeliveryKey, StartedUtc)`, `(SubmissionId, Outcome, Phase) INCLUDE (DeliveryKey)`, `(RunId, FlowId, DeliveryKey)`, `(StartedUtc)` | record timeline and the later attempt pruning looks for, the submission view and the counts a closing submission reads from the index alone, a run's records, pruning in start order |
| `Activity (FlowId, StartedUtc)`, `(FlowId, DeliveryKey, StartedUtc)`, `(Kind, StartedUtc)`, `(Actor, StartedUtc)`, `(SubmissionId)`, `(RunId)` | the audit views and their filters, and one record's interventions |
| `Run (SubmissionId)`, `Run (ResultSubmissionId)`, `Run (PipelineId, Operation)` | a submission's runs, a flow's runs by operation |

A search term that parses as a UUID matches the delivery key exactly (in a flow's listing, that flow's record; in the
lookup across every flow, each flow's record of the row); anything else is a prefix over label, source key and target
id, and a lookup across flows matches each record on its own values. A slower "contains" mode exists for the rare
case, and the API names it explicitly.
`SourceKey` is capped at 400 characters so it fits an index key.

### Bounds

Every listing reads a bounded part of the ledger, however many records a flow holds:

- A listing counts no further than 25,000 matches, the removal selection limit. Up to that the total is exact; past it
  the API returns `totalCapped: true` with the total as a floor, and the GUI shows "25,000+". Pages reach the first
  25,000 records of the order; a page past them is refused with a 400 that says to narrow the filter.
- Records are listed most recently updated first, ties broken by delivery key, so pages partition a batch that one bulk
  write stamped with a single update time.
- A prefix search takes at most 25,000 candidates from each identity column's index, in index order, and applies the rest
  of the filter to those. When a column runs into that bound while another filter narrows the listing, the count is
  reported as a floor.
- A contains term has no index. It runs only when the rest of the filter leaves at most 100,000 records, counted first
  and no further than that; a broader filter is refused with a 400 that names the ways to narrow it.
- The lookup across every flow (the search box) takes at most 1,000 candidates from each identity index and reports a
  larger match as "1,000+".

## Statistics

A flow's statistics (`GET /api/v1/delivery/flows/{id}/stats`: the records by status, the drifted ones, the deliveries
of the last 24 hours) are read on SQL Server from the `osdu.RecordCount` indexed view, which counts the flow's
records by status, last verify outcome and the hour of their last delivery. SQL Server maintains the view in the
transaction of every record write, so the counts are derived from the ledger, exact, and cost a few rows per flow at
any volume. The deliveries of the last 24 hours add the view's whole hours inside the window to an index count of the
part-hour the window opens in, which is exact to the tick and reads under an hour of deliveries. EF cannot declare an
indexed view, so the module's migrations create it with SQL: the initial migration alongside the tables, and a later
migration that changes the record table's key drops it first and creates it again after. The SQLite database the
tests use has no indexed views and counts the records directly.

## Retention

Attempts grow per delivery try. `POST /api/v1/delivery/ledger/prune` (admin scope) with `olderThanDays`
deletes older attempts while keeping the latest of every flow's record, so a record's last outcome is always explainable.
It deletes 4,000 attempts per statement, oldest first, each statement its own short transaction, so a prune of years of
history never holds a long lock on the table every drain appends to, and never enough row locks for SQL Server to lock
the whole table instead; an attempt goes only when a later attempt of the same record exists, which is one seek of the
record's timeline.
Activities are small and kept; partition either table by time in the model if volume demands it (see
[decisions/0005-ledger-retention.md](decisions/0005-ledger-retention.md)).

For the analytical view, snapshot the tables into Delta when one is needed. The ledger is a live status store, not a
reporting table.

## Provisioning

The ledger is the module's own schema: `osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs` declares the entities,
`DeliveryModel.Configure` maps them into schema `osdu`, and `OsduDbContext` owns them. Every model change ships with
its EF migration, with its own history table (`[osdu].[__EFMigrationsHistory]`) and its own schema version
(`[osdu].[SchemaVersion]`, which also records the minimum SQLFlow catalog migration it requires), so the ledger is
upgraded in place without touching SQLFlow's catalog.

The indexed view `[osdu].[RecordCount]` is created by the same migration as the tables it counts, and rebuilt by
any migration that changes the record table's key.

`LedgerPerFlow` (module version 1.2.0) keyed the record table by flow and delivery key. It gives every existing
attempt the flow of its record (or of its submission, when the record is missing), claims the OSDU id of every record
that queued or delivered a document, and completes an interrupted rollout's cursor with its record's flow. It stops,
naming the count and how to find them, when an attempt belongs to no record and no submission or when two records hold
one OSDU id; a failed migration leaves the ledger as it was. Going back down is refused while any delivery key has
records in more than one flow.

It is ordered for a ledger of hundreds of millions of records: the backfills run while the record table is still keyed
by delivery key and before the table is rebuilt, the record table's nonclustered indexes are dropped before its
clustered key changes and built once after (rather than rebuilt by the key drop and again by the key build), and the
attempt table's two ever-increasing indexes are set to `OPTIMIZE_FOR_SEQUENTIAL_KEY` where the server has it (SQL
Server 2019 and later, Azure SQL), because every drain appends to them at once. It still rewrites the record table and
updates every attempt in one transaction, so on a large ledger it needs log space for both and runs while no host is
up (the hosts refuse to start against a pending migration anyway).

`CoverWorkerReads` (module version 1.4.0) lets the worker's reads answer from an index alone (see
[Many nodes, one table](#many-nodes-one-table)). It rebuilds `(FlowId, Status, NextAttemptUtc)` in place with its
included columns (`DROP_EXISTING`, so the table always has the index and the rebuild reads the old index rather than
the table) and builds `(FlowId, LastSubmissionId, Status, NextAttemptUtc)`. Both are index builds over the record
table, sized by its row count, and run while no host is up.

`LeasesAndRecordEvents` (module version 1.5.0) moves the worker's writes off the record table (see
[Leasing](#leasing)). It creates `osdu.Lease` and `osdu.RecordEvent`, and turns every lease a stopped worker left
behind into a lease row, so the next claim of its flow recovers it like any other: a batch's lease keeps its batch, run
and expiry, and the records a claim outside the batches leased make one lease per token, expiring with the last of
them. It then drops the expiry columns of `osdu.Record` and `osdu.WorkBatch` with the indexes that served the old
lease sweep, and rebuilds the claim's two covering indexes in place without the expiry. Going back down is refused
while `osdu.RecordEvent` holds an event no lease has applied; a revert puts each lease's expiry back on its batch and
its records. The rebuilds are index builds over the record table, sized by its row count, and run while no host is up.

From 1.5.0 the ledger reads under snapshot isolation ([Many nodes, one table](#many-nodes-one-table)), so the database
that holds the `osdu` schema must allow it. Allow it once:

```sql
ALTER DATABASE [<database>] SET ALLOW_SNAPSHOT_ISOLATION ON;
```

Azure SQL Database allows it by default; a SQL Server database does not until it is set. The migration leaves the
setting to the operator because it covers the whole database, SQLFlow's catalog included: while it is on, every update
and delete in the database keeps the row's previous version in `tempdb` for as long as a snapshot transaction may read
it, and a row changed meanwhile carries 14 more bytes. The ledger's snapshot reads are short, so the versions are kept
briefly. A ledger whose database does not allow it refuses its first read, before it claims any work, with an error
that names the database and the statement above.

The control plane applies pending migrations on start, and `sqlflow db migrate --db <ref>` does it by hand. Both
hosts and `sqlflow db status` refuse to run against pending migrations, a database newer than the code, or a catalog
older than the module requires, naming the migration or version. The SQLite database the tests use is created from
the model directly and has no indexed view, so those suites count the records instead.
