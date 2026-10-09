---
id: delivery-concept-submissions
title: "Submissions, work batches and leases: how a delivery run plans and sends its records"
type: concept
summary: "How a delivery run plans records into a submission and work batches, how workers claim them under leases across nodes, and how one source feeds several flows."
keywords:
  - submission
  - work batch
  - intake
  - drain
  - lease
  - claim
  - fan-out
  - many nodes
  - watermark
  - submission status
  - record-scoped run
  - one source several flows
  - osdu id claim
  - reliability.leaseSeconds
related:
  - delivery-concept-ledger
  - delivery-concept-record-lifecycle
  - delivery-concept-change-detection
  - delivery-cli-run
  - delivery-flow-delivery
  - delivery-flow-interfaces
  - delivery-concept-partitions
  - cli-worker
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Leases.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/Intake/SubmissionIntake.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/LeaseJournal.cs
  - osdu/src/SqlFlow.Delivery/Engine/LedgerRegistration.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
---

# Submissions, work batches and leases: how a delivery run plans and sends its records

A deliver run of a [delivery flow](../flow/delivery.md) does two things: it **plans** (the intake reads the ingestion
tables, renders each record and decides what to send) and it **drains** (workers claim the planned records and send them to
OSDU). One plan is a **submission**; its rendered documents are written to **work batch** files; workers claim batches and
records under **leases**, so any number of nodes can deliver one flow at once. Everything is recorded in the
[ledger](ledger.md) as it happens. This page explains each of them and what to expect when you run, re-run or stop a flow.

## The operations of a delivery flow

A run of a delivery flow performs one operation, the one it names (`sqlflow run <flow.yaml> --operation <name>`) or
`deliver`. See [Running an OSDU flow](../cli/run.md) for the options and payload.

| Operation | What it does |
| --- | --- |
| `deliver` | Plan the selection into a submission, then drain it; send what released records and settled submissions still hold; undo what unfinished deliveries left. |
| `plan` | Plan and report what would be sent, with a sample of the records it would send. Stages nothing in the ledger and sends nothing. |
| `intake` | Plan into work batches without sending: a fan-out member's share, or a plan to drain later. |
| `drain` | Send the work batches already planned, of the flow or of one submission. |
| `replan` | Read every row of the scope again and deliver what renders differently. |
| `verify` | Read back delivered records from OSDU and record `match`, `drifted` or `missing`: up to 5,000 records a run, those not verified in the last 24 hours unless forced. |
| `sync` | Consolidate the ledger with the ingestion tables; renders and sends nothing ([Removal and reversal](removal-and-reversal.md#syncing-the-ledger-with-the-source)). |
| `reverse`, `undo`, `delete-ledger` | Put back what a run delivered, undo unfinished deliveries, delete the ledger ([Removal and reversal](removal-and-reversal.md)). |

Every operation of a source with interfaces runs each interface it selects in order ([Interfaces](../flow/interfaces.md)),
each under its own ledger identity, as a run of that interface alone would.

## What a run reads

The intake reads one **selection** of the ingestion tables, recorded on the submission as its kind:

| Kind | When | What it reads |
| --- | --- | --- |
| `incremental` | An ordinary run of a scope that has a watermark | The rows changed after the scope's watermark, less `source.incremental.overlapSeconds`. |
| `full` | The scope has no watermark yet, a `replan`, or the mapping, template or parameters changed since the watermark was written | Every row of the scope. |
| `keys` | A run scoped to records (`recordKeys`, at most 1,000), or the records the ledger asked to be planned again | Those records' rows, by their stored key tuples. |

The **scope** is the flow's parameter set: one watermark per distinct set (`osdu.SourceWatermark`). A whole-scope plan
(`incremental` or `full`) moves the watermark to the upper bound it read when its planning finishes, with every fan-out
member successful; a `keys` plan never moves it. When no row changed in the window and no record waits to be planned again,
the run is skipped whole (the tier-0 gate) unless it is forced; the watermark still moves. See
[Change detection](change-detection.md).

Before its own read, a deliver run of a whole scope reads the records the ledger asked to be planned again (a release
without a rendered document, a redelivery, bringing records up to date, a cache rollout, a sync that found a change): 5,000
records a pass, each pass a `keys` submission of its own, as many passes as there are such records. A record a pass cannot
plan (its row gone, or outside the run's scope) keeps its request for a run that can.

## The submission

`osdu.Submission` holds one row per plan. Its id is the idempotency key: a run that names a submission (`submissionId` in
its payload) works on that submission, reading exactly the window or keys it recorded.

| Columns | What they hold |
| --- | --- |
| `FlowId`, `FlowName`, `MappingReference`, `RenderContext`, `ParametersJson` | What produced it: the ledger, the mapping, the render context and the parameter values. |
| `Kind`, `SourceConnection`, `SourceObject` | The selection, the connection reference exactly as the flow declares it (never a resolved secret), and the record table read. |
| `WindowFromUtc`, `WindowToUtc`, `SourceWindowJson` | The `UpdatedDate_DW` window and what else bounded the read: child datasets, overlap, scope values, key slices, the key digest of a `keys` plan. |
| `WorkLocation`, `BatchCount`, `Slices` | Where its work batches were written, how many, and how many key slices a fan-out cut it into. |
| `Status` | `received`, `planned`, `running`, `completed`, `failed`. |
| `RecordCount`, `Planned`, `SkippedUnchanged`, `AwaitingApproval`, `SkippedStale`, `Blocked`, `Held`, `Untracked` | What its latest planning pass found. A re-run of the submission plans it again and replaces them. |
| `Delivered`, `UnchangedAtPush` | Records its attempts delivered, or found OSDU already holding at the final check, across every pass. |
| `Held`, `Failed`, `Waiting` | When it closes: the records whose last submission it is that are held, failed or waiting now (`Held` then replaces the planning pass's count). |
| `RunId`, `ReceivedUtc`, `StartedUtc`, `CompletedUtc`, `Error` | The coordinating run and the timeline. |

**Status.** A submission is `received` while it plans. It is `planned` when the planning finished with records to send,
and `completed` at once when it planned none. When the drain ends it is closed from the ledger: `running` while records of
it are still pending, else `failed` when any of its records failed, else `completed`. A run stopped part way (cancelled, or
its interface stopped by `failWhen`) closes its submission as `failed` with the reason, `stopped: ...`, while records of it
are still pending; the flow's next run sends them.

**Counts.** The closing counts are counted from the ledger each time the submission closes, never tallied while records
are sent: `Delivered` and `UnchangedAtPush` count the records the submission's own attempts settled so, `Held`, `Failed`
and `Waiting` count records now. A re-run that sends one record of three
already delivered therefore shows 1 planned and 3 delivered; what that run itself did is on the run and its activity.

**Re-running.** Running a submission again re-plans its window against the ledger as it stands now, unless it is completed
and not forced, in which case nothing is done. A `drain` with `submissionId` sends what it still holds.

## Work batches

The intake streams the rendered documents into JSON Lines files under the flow's work location (`source.work`),
`reliability.batchRecords` documents a file (500 by default). `osdu.WorkBatch` holds one row per file: its submission and
index, location, record count, status (`queued`, `running`, `done`, `failed`), the lease draining it, and how its drain ended
(delivered, held, failed, retrying, waiting). A record points at its batch and its `batch:offset:length` range; the document
itself is never stored in the database. The work location needs write access from every node that runs the flow, and its
files are part of the submission's evidence until the submission completes.

## Leases and claims

A worker takes work under a **lease**: one `osdu.Lease` row per claim, with a token (the worker's name and a fresh id), the
flow, the submission, the batch, the owner, and an expiry.

- **Claim.** A drain claims the oldest queued batch of the flow (or of one submission): the lease row and the batch's move
  to `running` commit together, so two workers never hold one batch. Records due for a retry outside a running batch are
  claimed `reliability.batchSize` at a time (50 by default, at most 500). Either way a record moves from `pending` to
  `delivering` by a compare-and-swap that also adds one to its tries, so two leases never hold one record.
- **Renew.** The worker renews its one lease row every half lease (at least a second apart). The lease lasts
  `reliability.leaseSeconds` (300 by default). A renewal that finds the lease taken over stops the worker.
- **Append.** While it sends, the worker does not write `osdu.Record`. It appends each completed step and each finished try
  to `osdu.RecordEvent`, with the try's attempt in the same transaction, at most 500 entries a write. A step is in the ledger
  before the next step starts, so a later try resumes after it.
- **Apply.** At each renewal and when the batch closes, the lease's events are applied to their records, 1,000 records a
  transaction, and deleted as they are applied. The record table therefore trails what workers have sent by at most one
  renewal; the attempts are there at once.
- **Close.** A worker that stops on its own terms hands back the records it did not reach, without charging their try, and
  the batch is `done`, `failed`, or `queued` again.
- **Recover.** Every claim first recovers the flow's leases that ran out: it takes each over by a compare-and-swap, applies
  what the stopped worker appended, hands the rest back to `pending` with the try charged and the note
  `the lease expired mid-attempt (the worker stopped) and the record was requeued`, and queues the batch again. A recovery
  holds the lease it took for five minutes, after which a recovery that itself stopped is recovered too.

A run requeued after its node stopped finds its submission planned and records still leased by a worker that is gone. It
waits out the lease, recovers it, and sends the records, resuming after the steps the stopped try had recorded. A lease
running out further ahead than the flow's own `reliability.leaseSeconds` belongs to a live worker and is left alone with a
warning.

## Many nodes

`reliability.fanOut` spreads one submission over member runs across the fleet. When the read holds at least
`reliability.fanOutMinRecords` candidates (1,000 by default), the coordinating run cuts the candidate keys into slices on the
record table's identity primary key (`source.record.primaryKey`), hands slices to up to `fanOut` intake members, plans its
own share, and finalises the submission with every member's totals. A member that fails stops the coordinating run with
`Intake member <slot> (run <id>) <status>: <error>. The submission stays planned as far as it got; re-run it to finish the intake.`
The drain fans out the same way, `fanOut` drain members beside the coordinating run; a drain member that fails leaves its
records to the coordinating run, which keeps draining until the submission is settled. The members appear as a run family
on the run's page.

Records of one flow written by many nodes never wait on each other for long: no statement writes more than 1,000 records,
a worker writes only its lease row and the append-only tables while it sends, staging locks only records that exist, and a
statement the database ends as a deadlock victim is run again, up to five runs in all.

## One source, several flows

An ingestion table can feed several delivery flows, each rendering the rows with its own mapping. The rows are loaded once;
each flow keeps its own ledger over them.

- **A record is one flow's.** The ledger keys a record by flow and delivery key, so a row read by two flows is two records,
  each with its own state, attempts, activities, submissions and watermark, and its own counts.
- **One OSDU record, one record of the ledger.** A record claims its OSDU id the first time it queues a document
  (`ClaimedTargetId`, unique across the ledger). Work whose id another record has claimed is held and nothing is sent:
  `OSDU id <id> is already claimed by flow '<name>' (<id>), and one OSDU record belongs to one flow. Deliver this flow to another data partition, or give its mapping a dataset.system or key that yields other OSDU ids.`
  Within one flow, two keys that give one id (possible with `dataset.idFrom: key`, or after a mapping's `dataset.system` or
  key changed) are refused the same way, naming the record that holds it.
- **A record keeps the id it claimed.** A render that gives another id than the one claimed is held, naming both, and
  nothing is sent.
- **An id made from a key is claimed only when it is free.** Before a record first claims such an id, the intake asks OSDU;
  when OSDU already holds a record there that no record of the ledger claimed, or cannot say, the record is held unclaimed.
- **A flow acts only on ids it claimed.** Sends, read backs and removals reach only claimed ids, so a record that was only
  ever held never touches another flow's OSDU record.

## Ledger registration

A run registers its ledger in the partition it delivers to before it writes any row: the partition the flow is bound to, or
the one its `data-partition-id` header resolves to. A flow with neither is refused:
`Flow '<name>' names no partition, and its target.headers no 'data-partition-id', so the partition its ledger belongs to is unknown.`
See [Partitions](partitions.md).

## Related pages

- [Record lifecycle](record-lifecycle.md): what a try does to a record.
- [Change detection](change-detection.md): what a plan sends and skips.
- [Running an OSDU flow](../cli/run.md): operations, payload and exit codes.
- [Operations runbook](../guides/operations-runbook.md): a submission that stays running, records stuck delivering.
- SQLFlow's [`sqlflow worker`](../../../../sqlflow/docs/reference/cli/worker.md) for the nodes that run these drains.
