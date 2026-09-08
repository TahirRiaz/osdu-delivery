# The ledger

The ledger is the system ([design.md](design.md) section 7): the tables in the catalog database's `delivery`
schema that say, per record, what OSDU holds, what is waiting, and everything that ever happened to it. It is
upgraded only by the catalog's EF Core migrations. Everything an operator or a dashboard asks is answered from
here, and every answer is index-backed.

The ledger sits in the same database as the platform's catalog, so one connection, one migration history and
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
| `Planned`, `SkippedUnchanged`, `Blocked`, `Delivered`, `Held`, `Failed` | Counts scoped to the records this submission touched. |
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

Worker, start and end, outcome (`delivered`, `skipped`, `failed`, `held`, `deleted`), the phase delivered
(`metadata`, `payload`, `metadata+payload`, `delete`, `none`), the hashes established, the version returned,
the redacted error, the platform `RunId` the attempt happened in, the `WorkBatch` it was drained from, and
`ResultJson`: every step the protocol took (name, timing, status, what the target returned, whether an earlier
try had completed it) and the values returned. Render-time holds are written by the intake with worker
`intake`; deletions by the actor who asked for them.

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
   operator delete ──▶ deleted (Blocked; OSDU no longer holds it)
   operator release ──▶ pending (when a rendered document is still there) or unblocked for the next plan
```

A **blocked** record (held, failed or deleted and not released) is skipped by every later plan as `blocked`
until either the source row changes (its fingerprint moves) or an operator releases it. That is what "do not
retry without intervention" means in practice: a re-run of the same data never re-attempts a known problem,
while a corrected source row flows through on its own.

**Redeliver** forgets the hashes of what OSDU holds (all of them, or only the metadata or the payload), so the
next plan of a drop that carries the record sends that part again. From the GUI, redeliver also queues the
deliver run scoped to the record, so the redelivery happens at once and is recorded under the user who asked.
It never bypasses the render: the document sent is always the one the pinned mapping produces from the
current source.

**Delete** removes the record from OSDU through the flow's protocol (logical, or a purge), writes a `delete`
attempt, forgets the hashes and version, and blocks the record. If the source still presents the record and an
operator releases it, the next plan creates it again; that is ownership, not an accident.

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
| `Record (FlowId, Status, NextAttemptUtc)` | the worker's claim and status filters |
| `Record (LastSubmissionId, WorkBatch)`, `WorkBatch (FlowId, Status, CreatedUtc)`, `(SubmissionId, Status)`, `(Status, LeaseExpiresUtc)` | the batch claim, its records, the lease sweep, the submission's batch list |
| `Retrieval (FlowId, StartedUtc)`, `(FlowId, Status, StartedUtc)`, `(RunId)` | a retrieval flow's runs, the watermark chain (the last done run), the run's row |
| `Record (FlowId, Label)`, `(FlowId, SourceKey)`, `(FlowId, TargetId)` | prefix search (`LIKE 'term%'`) on the three identity columns |
| `Record (FlowId, UpdatedUtc)`, `(FlowId, LastDeliveredUtc)`, `(FlowId, LastVerifyOutcome)`, `(FlowId, LastSubmissionId)` | recency listings, stats, drift, per-submission views |
| `Attempt (DeliveryKey, StartedUtc)`, `(SubmissionId)`, `(RunId)`, `(StartedUtc)` | record timeline, submission view, run linkage, pruning |
| `Activity (FlowId, StartedUtc)`, `(DeliveryKey, StartedUtc)`, `(Kind, StartedUtc)`, `(Actor, StartedUtc)`, `(SubmissionId)`, `(RunId)` | the audit views and their filters |
| `Run (SubmissionId)`, `Run (ResultSubmissionId)`, `Run (PipelineId, Operation)` | a submission's runs, a flow's runs by operation |

A search term that parses as a UUID matches the delivery key exactly; anything else is a prefix over label,
source key and target id. A slower "contains" mode exists for the rare case, and the API names it explicitly.
`SourceKey` is capped at 400 characters so it fits an index key.

## Retention

Attempts grow per delivery try. `POST /api/v1/delivery/ledger/prune` (admin scope) with `olderThanDays`
deletes older attempts while keeping the latest per record, so a record's last outcome is always explainable.
Activities are small and kept; partition either table by time in a migration if volume demands it (see
[decisions/0005-ledger-retention.md](decisions/0005-ledger-retention.md)).

For the analytical view, publish the known state (a `known-state` run) and, when needed, snapshot the tables
into Delta. The ledger is a live status store, not a reporting table.

## Migrations

The ledger's model is part of the catalog's: `src/SqlFlow.Catalog/DeliveryEntities.cs` declares the entities
and `DeliveryModel.Configure` the schema, and the catalog's migration covers both. Generate a migration from
`src/SqlFlow.Catalog` exactly as for any other catalog change (see the repository's CLAUDE.MD); the control
plane applies it on start, and `sqlflow db migrate` applies it by hand.
