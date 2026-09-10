# The ledger

The ledger is the system ([design.md](design.md) section 7): the tables in the catalog database's `delivery`
schema that say, per record, what OSDU holds, what is waiting, and everything that ever happened to it. It is
created from the catalog's EF model. Everything an operator or a dashboard asks is answered from
here, and every answer is index-backed.

The ledger sits in the same database as the platform's catalog, so one connection, one model and
one backup cover both, and a run row and the attempts it produced are joined by id.

## Tables

### `delivery.Submission`: one drop handed over by the preparing side

| Column | Purpose |
| --- | --- |
| `SubmissionId` | Primary key and idempotency key (the manifest's `submissionId`). |
| `FlowId`, `FlowName`, `MappingReference`, `RenderContext` | What produced the run. |
| `DropLocation`, `ParametersJson`, `RecordCount` | The handover. |
| `WorkLocation`, `BatchCount`, `Partitions` | Where the intake wrote its work batches, how many, and how many root partitions the drop declared. |
| `Status` | `received`, `planned`, `running`, `completed`, `failed`. |
| `Planned`, `SkippedUnchanged`, `SkippedStale`, `UnchangedAtPush`, `Blocked`, `Delivered`, `Held`, `Failed` | Counts scoped to the records this submission touched. `Planned`, `SkippedUnchanged`, `SkippedStale` and `Blocked` describe its latest planning pass: a re-run of the submission plans it again and replaces them. `Delivered` and `UnchangedAtPush` count the distinct records the submission's attempts delivered, or found OSDU already holding at the final hash check, across every pass. `Held` and `Failed` count the records whose last submission this is and that are held or failed now. A re-run that re-sends one record of three already delivered therefore shows 1 planned and 3 delivered; what that run itself did is on the run (`recordsPlanned`, `recordsDelivered`). `SkippedStale` counts rows older than the version delivered or queued. |
| `ReceivedUtc`, `StartedUtc`, `CompletedUtc`, `Error` | Timeline. |

The platform's run row carries the submission too: `Run.SubmissionId` when a run was asked to re-run one, and
`Run.ResultSubmissionId` for the submission a deliver run registered or completed, so a submission page lists
the runs that carried it.

### `delivery.Record`: the current state of one deliverable

| Column | Purpose |
| --- | --- |
| `DeliveryKey` | Primary key. Deterministic, derived from source data. |
| `FlowId`, `SourceKey`, `Label`, `MappingName` | Provenance. `Label` is the mapping's `identity.label` rendered for the row (a wellbore name, a log name), for search and display only. |
| `RenderContext`, `SourceFingerprint`, `MetadataHash`, `PayloadHash` | What OSDU holds: the gates for tiers 1 and 2. |
| `SourceModifiedUtc`, `PayloadModifiedUtc` | The last-modified moment of the source row, and the newest modified time of the chunk files, that OSDU's document and payload were built from: the watermarks an incremental drop is ordered against. |
| `TargetId`, `TargetVersion` | The OSDU id and the last known version (the drift handle). |
| `Status` | `pending`, `delivering`, `delivered`, `held`, `failed`, `deleted`. |
| `Blocked` | Set when the record was held, failed or deleted and not released since. |
| `LastDeliveredUtc`, `LastVerifiedUtc`, `LastVerifyOutcome` | Custody timestamps. |
| `LeaseOwner`, `LeaseExpiresUtc` | Worker concurrency control. |
| `LastSubmissionId`, `AttemptCount`, `NextAttemptUtc`, `LastError` | The pending work's progress; `LastError` is redacted. |
| `Pending*` | The hashes, context, fingerprint and payload location of the work waiting to be delivered. |
| `WorkBatch`, `PendingDocumentRef` | Where the pending rendered document is: the work batch and its `batch:offset:length` range in the batch file. The document itself lives on storage, never here. |
| `PendingStepJson` | The steps an earlier try of the pending work completed, with what the target returned, so the next try resumes after them. |
| `TargetStateJson` | Every value the target returned across the record's deliveries (record id and version, dataset ids, file sources, a workflow run id): what OSDU holds for the record. |

### `delivery.Attempt`: append-only, one row per delivery try

Worker, start and end, outcome (`delivered`, `skipped`, `failed`, `held`, `deleted`, `historypurged`), the
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

### `delivery.WorkBatch`: one file of rendered documents

| Column | Purpose |
| --- | --- |
| `SubmissionId`, `Index` | Primary key: the batch's place in its submission. |
| `FlowId`, `Location`, `RecordCount` | Whose it is, where the JSON Lines file is, how many documents it holds. |
| `Status` | `queued`, `running`, `done`, `failed`. |
| `LeaseOwner`, `LeaseExpiresUtc`, `RunId` | The drain that holds it and the run it is being drained in. |
| `CreatedUtc`, `StartedUtc`, `CompletedUtc` | Timeline. |
| `Delivered`, `Held`, `Failed`, `Retrying`, `Error` | How its drain ended. |

A drain claims the oldest queued batch of the flow (or of one submission) and leases the batch's due records
under the batch's token; the records' rows point at the batch and their range in its file. A batch whose drain
crashed is reclaimed with its records when the lease expires.

### `delivery.Retrieval`: one run of a retrieval flow

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

### `delivery.Activity`: the audit trail of runs and interventions

One row per operator or scheduler action: `deliver`, `intake`, `drain`, `submit`, `verify`, `known-state`,
`release`, `redeliver`, `delete`. Each carries the actor (the run's requesting user, `schedule` or `manual` for a run;
`user:<name>` for an intervention from the GUI or the API; `cli:<user>` from a workstation), start and end,
outcome (`running`, `completed`, `failed`, `cancelled`), the parameters as JSON, the submission, record and
platform run it targeted when it targeted one, a summary and, for runs, the captured run log.

Record history is the attempts; run and intervention history is the activities. A record's page in the GUI
shows both, plus its verify outcomes; a run's page links to what it did to each record through the run id.

### `delivery.SourceWatermark`: tier 0

`(FlowId, Scope, TableName) -> Version`, where the scope is the flow's parameter set. A manifest whose
`sourceVersions` have not advanced past these skips the whole run (unless the run is forced).

### `delivery.Mapping` and `delivery.Snapshot`: what the repositories hold

Read models the repository sync writes: every mapping document (its reference, kind, a parsed summary, the
YAML, and whether it parses) and every schema and reference snapshot (kind, version, when it was captured,
which reference version is current). They back the GUI's mappings and snapshots page; nothing writes them but
the sync.

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

Versions never go backwards. A row older than the version a record holds, delivered or queued, is skipped with an
attempt (`skipped`, phase `stale`) naming both versions, and staging refuses work older than what the ledger holds,
so two intakes racing for one record leave the newer version standing. Work planned for a record another worker is
delivering right now is written behind the delivery rather than dropped: the record keeps its lease, the completion
promotes what that try actually delivered (from the claim it carries, not from the columns the newer work replaced)
and leaves the newer work pending, and the try's step progress is kept only while the record still holds its
document. Before anything is sent, the worker compares the queued document and payload hashes with what the record
says OSDU holds at that moment and sends only the halves that differ; when neither does, it settles the record with
an attempt (`skipped`, phase `unchanged`) and sends nothing.

**Redeliver** forgets the hashes of what OSDU holds (all of them, or only the metadata or the payload), so the
next plan of a drop that carries the record sends that part again. From the GUI, redeliver also queues the
deliver run scoped to the record, so the redelivery happens at once and is recorded under the user who asked.
It never bypasses the render: the document sent is always the one the pinned mapping produces from the
current source.

**Removal** takes the record out of OSDU through the flow's protocol, to one of three depths (see
[operations](operations.md#removing-records-from-osdu)). `record` and `everything` write a `delete` attempt,
forget the hashes and version, and block the record. If the source still presents the record and an operator
releases it, the next plan creates it again; that is ownership, not an accident.

`history` is the exception: it destroys the record's earlier versions and leaves the record itself live in OSDU
at the version the ledger already holds. Its custody state is therefore still true and is not disturbed; the
purge is written as a `purge-history` attempt and nothing else changes. Every removal, at every depth, names the
scope and the operator on the attempt and in the activity trail.

## Leasing

```text
claim:    UPDATE Record SET Status='delivering', LeaseOwner=@token, LeaseExpiresUtc=@now+lease, AttemptCount+=1
          WHERE DeliveryKey IN (@candidates)
            AND ((Status='pending' AND (NextAttemptUtc IS NULL OR NextAttemptUtc <= @now))
              OR (Status='delivering' AND LeaseExpiresUtc < @now))
          then SELECT ... WHERE LeaseOwner=@token
renew:    UPDATE ... SET LeaseExpiresUtc=@now+lease WHERE DeliveryKey=@key AND LeaseOwner=@token
complete: INSERT Attempt; UPDATE Record (status, promote pending -> current when delivered, release lease)
release:  UPDATE Record SET Status='pending', LeaseOwner=NULL, LeaseExpiresUtc=NULL, AttemptCount=AttemptCount-1
          WHERE DeliveryKey=@key AND LeaseOwner=@token AND Status='delivering'      (a stopping worker)
```

The claim is a single compare-and-swap, so two workers never hold one record, and any number of nodes can
share the ledger. A drain claims a work batch the same way (`WorkBatch.Status` from `queued` to `running`
under a token) and then leases the batch's due records under that token in one statement; completion of a
batch is one bulk write (a table-valued merge on SQL Server), and closing the batch releases the token. That
is what lets a submission's drains spread over the fleet ([design.md](design.md) section 16.4). A deliver run drains its own submission; runs of the same flow on
several nodes share the ledger safely. A crashed worker's lease expires and the next claim picks the record
up. A stopping worker releases its records at once without charging the interrupted attempt. Long payload
uploads renew the lease at half its length.

## Indexes and search

Listings are index-backed so the GUI answers in milliseconds at any estate size:

| Index | Serves |
| --- | --- |
| `Record (FlowId, Status, NextAttemptUtc)` | the worker's claim |
| `Record (FlowId, Status, UpdatedUtc)`, `(FlowId, LastSubmissionId, UpdatedUtc)` | a status's or a submission's records, most recent first, read in index order |
| `Record (LastSubmissionId, WorkBatch)`, `WorkBatch (FlowId, Status, CreatedUtc)`, `(SubmissionId, Status)`, `(Status, LeaseExpiresUtc)` | the batch claim, its records, the lease sweep, the submission's batch list |
| `Retrieval (FlowId, StartedUtc)`, `(FlowId, Status, StartedUtc)`, `(RunId)` | a retrieval flow's runs, the watermark chain (the last done run), the run's row |
| `Record (FlowId, Label)`, `(FlowId, SourceKey)`, `(FlowId, TargetId)` | prefix search (`LIKE 'term%'`) on the three identity columns |
| `Record (FlowId, UpdatedUtc)`, `(FlowId, LastDeliveredUtc)`, `(FlowId, LastVerifyOutcome)` | recency listings, the last delivery and the part-hour of the 24-hour count, drift |
| `Record (FlowId, DeliveryKey)` | key-ordered walks of one flow: the known-state stream, a removal's key list |
| `RecordCount` indexed view `(FlowId, Status, LastVerifyOutcome, DeliveredHour)` | flow statistics, read from a few rows per flow (see [Statistics](#statistics)) |
| `Attempt (DeliveryKey, StartedUtc)`, `(SubmissionId)`, `(RunId, DeliveryKey)`, `(StartedUtc)` | record timeline, submission view, a run's records, pruning |
| `Activity (FlowId, StartedUtc)`, `(DeliveryKey, StartedUtc)`, `(Kind, StartedUtc)`, `(Actor, StartedUtc)`, `(SubmissionId)`, `(RunId)` | the audit views and their filters |
| `Run (SubmissionId)`, `Run (ResultSubmissionId)`, `Run (PipelineId, Operation)` | a submission's runs, a flow's runs by operation |

A search term that parses as a UUID matches the delivery key exactly; anything else is a prefix over label,
source key and target id. A slower "contains" mode exists for the rare case, and the API names it explicitly.
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
of the last 24 hours) are read on SQL Server from the `delivery.RecordCount` indexed view, which counts the flow's
records by status, last verify outcome and the hour of their last delivery. SQL Server maintains the view in the
transaction of every record write, so the counts are derived from the ledger, exact, and cost a few rows per flow at
any volume. The deliveries of the last 24 hours add the view's whole hours inside the window to an index count of the
part-hour the window opens in, which is exact to the tick and reads under an hour of deliveries. EF cannot declare an
indexed view, so the catalog creates it right after the tables, and a catalog without it is refused at startup like
one missing a table. The SQLite catalog the tests use has no indexed views and counts the records directly.

## Retention

Attempts grow per delivery try. `POST /api/v1/delivery/ledger/prune` (admin scope) with `olderThanDays`
deletes older attempts while keeping the latest per record, so a record's last outcome is always explainable.
Activities are small and kept; partition either table by time in the model if volume demands it (see
[decisions/0005-ledger-retention.md](decisions/0005-ledger-retention.md)).

For the analytical view, publish the known state (a `known-state` run) and, when needed, snapshot the tables
into Delta. The ledger is a live status store, not a reporting table.

## Provisioning

The ledger's model is part of the catalog's: `src/SqlFlow.Catalog/DeliveryEntities.cs` declares the entities
and `DeliveryModel.Configure` the schema. There are no migrations: the database is created from the EF model,
and nothing upgrades it in place, so a change to these entities means dropping the database and provisioning it
again (see the repository's CLAUDE.MD). The control plane provisions on start against an empty database when
`Bootstrap:AllowCreate` is set; `sqlflow db migrate --create` does it by hand, and `sqlflow db status` reports
whether the database matches the model.
