# Plan: reversing a delivery run

A run that went wrong (a wrong file loaded, a mapping or cache that was wrong, a partition reached by mistake) can put
millions of records into OSDU. A **reversal** puts OSDU back, record by record, as it was before that run: a record the
run created is removed again, and a record the run updated is given back the version OSDU held before. Every step is
written to the ledger, so what the reversal did to each record, why, by whom and when is answered from the ledger alone,
and a reversal stopped half way is finished by asking again.

Each stage lists what it changes and the tests that close it. All work is in `osdu/`: nothing in `sqlflow/` changes, and
no OSDU table changes without its migration.

## Status

| Stage | State |
| --- | --- |
| 1. The ledger | Done: `osdu.Reversal`, `osdu.ReversalItem`, the `reverted` custody state, the `restored` attempt outcome, the version each delivery replaced; migration `RecordReversals` (module 1.26.0) |
| 2. The routes | Done: `IDeliveryProtocol.RestoreBatchAsync`, the storage route and the Wellbore DDMS (through storage) restore; `ReversalRoute` says what each route can do |
| 3. The run | Done: the `reverse` operation, `ReversalRunner`, resumable capture and settlement |
| 4. The API, the CLI and the GUI | Done: preview, request, listing and detail routes; `sqlflow records reverse`; the Reverse dialog on the submission and run pages, the reversal card, the `reverted` state everywhere a state is shown |

## Why

What exists today does not reverse a run:

- **Remove what it delivered** (the submission page, a `deliveredBy` removal) soft-deletes every record the submission
  delivered. A record that existed before the run and was only updated by it is removed as well, so the earlier version
  that was correct is taken out of OSDU with the wrong one. That is a second error, not a reversal.
- A removal takes at most 25,000 records, as one compute task that holds its key list in memory, with no progress kept:
  a node that stops half way leaves no record of how far it got.
- Nothing restores an earlier version. OSDU has no call that removes only the latest version and promotes the one before
  (storage v2 has none), so a restore has to write the earlier version back as a new one.

## What a reversal does

The **source** is what is reversed:

- a **submission**: everything its batch delivered, whichever run drained it (fan-out members, a re-run, a later run
  sending records released back into it);
- a **run**: everything delivered under the submissions it coordinated (its plan, each requested pass, what its fan-out
  members drained) and everything it delivered itself (records of earlier submissions it sent after its own).

What the source delivered is read from the ledger: its attempts with outcome `delivered`. For each record the reversal
works out, from the record's own history, the state before the source's first delivery of it:

| Before the source | What the reversal does | The record afterwards |
| --- | --- | --- |
| OSDU held version *v* | Reads *v* from storage (`GET /records/{id}/{v}`) and writes it back (`PUT /records`), carrying the keys another system owns from the latest version | `reverted`, blocked, at the new version, with the hashes and origin of *v* |
| OSDU held nothing (created by the source, or removed earlier and created again) | Removes it reversibly (`POST /records/delete`, 500 ids a request) | `deleted`, blocked, as a removal leaves it |
| Not known (the attempts before it were pruned) | Asks storage for the record's versions (`GET /records/versions/{id}`): the newest one older than the source's first version is restored; with none, the record is removed | as above |

Every delivered attempt now records the version it replaced (`replaced.version` in its result, null for a record OSDU did
not hold), so a reversal of any run delivered from this version on never depends on older history.

A reversal never purges and never destroys: it writes a new version or soft-deletes, both of which OSDU can undo.

### What it leaves alone

A record is passed over, with an attempt saying why, when putting it back would undo something that came after the source:

| Outcome | When |
| --- | --- |
| `superseded` | The ledger holds another version than the one the source left: a later run delivered it again, or it was restored or removed since |
| `changed-in-osdu` | OSDU's latest version is not the one the source left: something outside this flow wrote it since |
| `missing-in-osdu` | OSDU no longer holds a record the source updated: it was removed outside this flow |
| `unchanged` | The version before the source is the one the source left: its delivery wrote nothing new |
| `version-missing` | The version to put back can no longer be read from OSDU (its history was purged) |
| `busy` | Work is queued or in flight for the record (pending, delivering, waiting): it is passed over now and taken again when the reversal is asked again |
| `not-claimed` | The record never claimed the OSDU id, so this flow wrote nothing to OSDU under it |
| `not-in-ledger` | The ledger no longer holds the record |
| `not-reversible` | The route cannot do what the record needs (see the routes below), named with the reason |

A record the source created that OSDU no longer holds is settled as `already-gone`: there is nothing left to remove, and
the ledger records it as removed.

To reverse a run that a later run superseded, reverse the later run first, newest first.

### After a reversal

A reversed record is **blocked**, as a removed one is: the next runs pass over it while its source row is unchanged, so a
scheduled run does not send the same wrong rows again. A row corrected in the source (a new version) flows through on its
own. A release (by record, by issue, of every blocked record) unblocks it: a `reverted` record becomes `delivered` again and
is planned by the next run, which sends it only where it renders differently from what OSDU now holds.

The source watermark does not move back: rows the source read are not read again by an incremental run, which is what the
block relies on.

## The ledger (stage 1)

`osdu.Reversal`, one row per source of a ledger (unique on the partition, the ledger, the source kind and id):

| Column | Purpose |
| --- | --- |
| `PartitionId`, `ReversalId` | Primary key; `ReversalId` is unique across partitions. |
| `FlowId`, `FlowName` | The ledger reversed. |
| `SourceKind`, `SourceId` | `run` or `submission`, and its id. |
| `SubmissionsJson` | The submissions the source covers, fixed when the reversal is opened. |
| `Status` | `capturing`, `reversing`, `completed`, `failed`, `cancelled`. |
| `RequestedBy`, `RequestedUtc` | Who asked first, and when. |
| `CapturedUtc` | When every record the source delivered was listed; null while the listing is not finished. |
| `StartedUtc`, `CompletedUtc`, `LastRunId`, `Error` | The latest run that worked on it, and how it ended. |

`osdu.ReversalItem`, one row per record the source delivered, keyed by the reversal and the delivery key:

| Column | Purpose |
| --- | --- |
| `TargetId` | The OSDU id the record claimed. |
| `FirstAttemptId`, `FirstVersion`, `RunVersion` | The source's first delivered attempt of the record, the version it wrote, and the version the source left. |
| `Prior`, `PriorVersion`, `PriorAttemptId` | What OSDU held before: `version` (with the version and the attempt that delivered or restored it), `none`, or `unknown`. |
| `State`, `Outcome`, `Detail` | `pending`, `sending` (an OSDU write is under way), `done`, `skipped` or `failed`; what came of it and why. |
| `RestoredVersion`, `NewVersion`, `RunId`, `UpdatedUtc` | The version put back and the version OSDU gave it; the reverse run that settled it. |

Indexes: `(PartitionId, ReversalId, State, DeliveryKey) INCLUDE (Outcome)` (the pending page, the counts by state and
outcome) and `(PartitionId, ReversalId, Outcome, DeliveryKey)` (a reversal's records by outcome). The counts a page shows
are read from the items; nothing keeps a running total.

`osdu.Record` gains the custody state `reverted` (no column changes). `osdu.Attempt` gains the outcome `restored`; a
reversal's attempts have the phase `reverse`. The release statements treat `reverted` as a blocked state.

**Tests:** migration up and down, with the keys, the indexes and one reversal to a source; the model snapshot; the
migration touches the `osdu` schema alone; a release of a reverted record makes it delivered and plans it again; a
delivered attempt records the version it replaced.

## The routes (stage 2)

`IDeliveryProtocol.RestoreBatchAsync` writes records back as OSDU held them at a version, a batch to a request where the
route allows it; the default refuses. `ReversalRoute.Of(flow, kind)` says, without building the protocol, whether the
flow's route can restore and remove, or why not; the preview and the target view show it.

| Route | Restores | Removes what it created |
| --- | --- | --- |
| `storage` | Yes: storage read of the version, storage array write | Yes, in bulk |
| `ddms`, Wellbore DDMS collections | Yes, through storage under a platform endpoint: the version written back carries its `bulkURI`, so the bulk data of that version is what the DDMS serves again. The DDMS's own write refuses a record whose `bulkURI` is not the latest one's, so it is not used | Yes, the DDMS's logical delete |
| Other routes | No (`not-reversible`, with the reason): their records carry files, datasets or rows outside the record that a version of it does not bring back | Where their record scope removes reversibly |

**Tests:** the storage restore writes the version read back without its system properties, carries the keys External
Data Services owns from the latest version and none of the flow's, and reports each record's new version (the batching
and the per-record fallback on a refused batch are the record writer's own); the Wellbore DDMS restore goes to storage
and keeps the version's `bulkURI`; a route that cannot restore refuses every record naming itself; `ReversalRoute` for
every route.

## The run (stage 3)

A reversal is a run of the flow with the operation `reverse` and the payload `{"submissionId": "..."}` or
`{"runId": "..."}` (with `interface` for a source of several interfaces); any other payload key is refused. The platform
runs one execution of a flow at a time, so nothing delivers the flow's records while it reverses them. The run:

1. **Opens** the reversal of its source, or resumes the one that exists.
2. **Lists** what the source delivered, if not listed yet: the source's delivered attempts are read a page of 1,000 at a
   time in index order, and each record of a page is added once with what it needs (one `INSERT ... SELECT` of a page,
   seeking each record's own attempts). Adding is idempotent, so a listing stopped half way starts again from the
   beginning and adds only what is missing.
3. **Reverses** the pending items 500 at a time, in key order. For each page: the records are read from the ledger and
   classified; OSDU's latest versions are read in batches; an unknown prior version is read from the version list; items
   about to be written are marked `sending`; removals go in bulk and restores read their versions with the flow's
   `reliability.concurrency` and write them in batches; then the page is settled in the ledger, at most 1,000 records to a
   transaction: the record, its attempt, its line under the run's activity and the item, together.
4. **Closes** the reversal: `completed` when no item is pending, `failed` or `cancelled` otherwise, with every item already
   settled kept.

A run stopped anywhere (cancelled, a node lost, the database unreachable) loses nothing: settled items stay settled,
pending ones are taken by the next run of the same reversal, and an item left `sending` is checked against OSDU first. If
OSDU's latest version is the one the source left, the write did not land and it is made again; if it holds what the
reversal writes (the earlier version's content, or no record for a removal), the write landed and the item is settled as
such. Asking again also takes the items that failed and those passed over as `busy`.

The run's trace says how far it got at a pace that slows as it goes, never a line per record. Its result counts the
outcomes, with the reversal's id.

**Tests:** a run created and updated records, the reversal removes the created ones and restores the updated ones to
their earlier versions in a stand-in OSDU that keeps versions; superseded, changed in OSDU, busy and not-claimed records
are passed over with their attempts; a reversal stopped after a page resumes and finishes; an item left `sending` whose
write landed is settled without writing again; a source whose earlier attempts were pruned restores from the version
list; the next run passes the reversed records over as blocked, and a release plans them again; a page never writes more
than 1,000 records to a statement.

## The API, the CLI and the GUI (stage 4)

| Route | Scope | Purpose |
| --- | --- | --- |
| `POST /flows/{pipelineId}/reverse/preview` | operate | What reversing `runId` or `submissionId` would reach: how many records the source delivered, what the route can do, a classification of up to 1,000 of them (how many would be restored, removed, passed over and why), and the reversal of that source if there is one. |
| `POST /flows/{pipelineId}/reverse` | operate | Queue the `reverse` run for `runId` or `submissionId`, as the caller; `expected` is refused with 409 when the source no longer delivered that many records. Answers the run. |
| `GET /flows/{pipelineId}/reversible` | read | Whether `runId` or `submissionId` has anything to reverse now (`ReversalAvailability`): it delivered a record of the ledger and has no reversal, or its reversal has records to take; never while a reverse run of it is queued or running. The GUI offers the action by it, and the request and the reverse run refuse by the same answer. |
| `GET /flows/{pipelineId}/reversals` | read | The interface's reversals, newest first; with `runId` or `submissionId`, that source's reversal with its counts. |
| `GET /reversals/{reversalId}` | read | One reversal: its source, its runs, its counts by state and outcome. |
| `GET /reversals/{reversalId}/records` | read | Its records by outcome, a page at a time after a key. |

`sqlflow records reverse <flow> --submission <id> | --run <id> [--preview]` reverses in the process, as a run would, and
prints the counts (or, with `--preview`, what it would do); `sqlflow records reversals <flow>` lists a ledger's
reversals, or one source's records by outcome.

The submission page's **Remove what it delivered** becomes **Reverse this submission**; the page of a run that sent
records (`deliver`, `replan`, `drain`, `intake`) and the audit trail's entry of one offer **Reverse this run** once it has
ended; each is shown only while the source has anything to reverse (**Resume the reversal** for one that stopped); all open the Reverse
dialog (what the preview says, then confirm), and show the reversal's card: its state, its counts by outcome, each
listing its records, and its runs. The reverse run's page shows the same card. The records listing, the counts and the
record page show `reverted`; the timeline shows the reversal's activity and attempts.

**Tests:** the preview by run and by submission, a request naming both sources or neither refused, an unknown source answered 404, a count that moved
refused with 409, the reverse run queued with its payload, the listing, the detail with its runs and the records by
outcome, and the preview naming the reversal that exists.
