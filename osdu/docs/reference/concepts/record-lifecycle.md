---
id: delivery-concept-record-lifecycle
title: "Record lifecycle: record states, attempts, retries, held and failed records, issues and releases"
type: concept
summary: "Record states, what each attempt records, when a record is retried, held or failed, how issues group blocked records, and what a release does."
keywords:
  - record status
  - record states
  - held record
  - failed record
  - retry
  - backoff
  - blocked
  - release
  - issues
  - waiting record
  - attempt outcome
  - attempt phase
  - reliability.retry
  - skipStatusCodes
related:
  - delivery-concept-ledger
  - delivery-concept-submissions
  - delivery-concept-change-detection
  - delivery-concept-protocols
  - delivery-concept-removal-and-reversal
  - delivery-guide-operations-runbook
  - delivery-cli-records
  - delivery-flow-delivery
  - delivery-flow-mapping-assertions
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Leases.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Waits.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Problems.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Problems.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ProblemSignature.cs
  - osdu/src/SqlFlow.Delivery/Ledger/RecordWaits.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Artifacts.cs
  - osdu/src/SqlFlow.Delivery/Planning/ChangeDetector.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/ValidationGate.cs
  - osdu/src/SqlFlow.Delivery/Validation/AssertionFindings.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/FailureGuard.cs
  - osdu/src/SqlFlow.Delivery/Http/RetryPolicy.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/SourceDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/RecordProblemBackfillService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryIssueEndpoints.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryRecordVerbs.cs
---

# Record lifecycle: record states, attempts, retries, held and failed records, issues and releases

Every record a delivery flow plans has one row in the [ledger](ledger.md) (`osdu.Record`) holding its custody state, and an
append-only history of attempts (`osdu.Attempt`). This page says which states a record can be in, how it moves between
them, what each attempt records, when a failed send is retried, held or failed for good, how the ledger groups blocked
records into issues, and what releasing a record does. For removals and reversals, which also move records, see
[Removal and reversal](removal-and-reversal.md).

## Record states

| Status | Meaning | Blocked | How it moves on |
| --- | --- | --- | --- |
| `pending` | A rendered document (and payload) is queued for the record, due now or at `NextAttemptUtc`. | No | A worker claims it: `delivering`. |
| `delivering` | A worker holds it under a lease and is sending it. | No | The try settles it when the lease applies; a lease that ran out is recovered. |
| `delivered` | OSDU holds what the ledger says: `TargetId`, `TargetVersion` and the hashes. | No | A plan finds a change: `pending`. |
| `held` | The record was refused: a data problem, a status no retry can fix, the check before sending, or a render problem. | Yes | Its source row changes, or an operator releases it. |
| `failed` | Retryable failures used up the retry budget. | Yes | As `held`. |
| `waiting` | Its document refers to a record another record of the ledger holds and has not delivered. | No | That record lands: `pending`. Nothing is charged. |
| `deleted` | Removed from OSDU by an operator, or by a reversal of the run that created it. | Not after a removal; yes after a reversal | The next run that reads its row delivers it again (removal), or a release (reversal). |
| `reverted` | A reversal, or a step back to the previous version, wrote an earlier version back. | Yes | Its source row changes, or a release makes it `delivered` again. |

A **blocked** record is skipped by every later plan as `blocked`, with the reason
`<status> since <time>: <last error>; release the record or change the source to plan it again`, until either its source
row moves past the version it was left at (its last-modified moment moves on when the flow orders rows by one, otherwise
its fingerprint changes) or an operator releases it. A re-run of the same data never re-attempts a known problem, while a
corrected source row flows through on its own.

```text
 new or changed row --plan--> pending --claim--> delivering --sent--> delivered
                                ^                    |
                                +-- retry later -----+
                                                     +--> held    (refused; blocked)
                                                     +--> failed  (retry budget spent; blocked)
 pending --claim, refers to a record not landed--> waiting --that record lands--> pending
 delivered --removal (record or everything)--> deleted --row read again--> pending
 delivered --reversal or previous--> reverted --release--> delivered
```

## How a record reaches pending

A run plans the rows of its selection ([Submissions](submissions.md)) and decides, record by record, what to do
([Change detection](change-detection.md)). The plan writes an attempt for every decision that changes the record, even
when nothing is sent, so the record's history holds every change of its row:

| Decision | Status | Attempt (outcome, phase) |
| --- | --- | --- |
| New, or the document or payload renders differently from what OSDU holds | `pending`, its document in a work batch | none until a worker tries it |
| The render cannot be delivered (a missing value, an unresolved lookup, a refused OSDU id) | `held`, blocked | `held`, `render` |
| The ingestion table marked the row deleted | `held`, blocked | `held`, `source-deleted` |
| The row changed but renders the document OSDU holds or already queues | unchanged; the origin moves to the new row | `skipped`, `identical` |
| The row is older than the version delivered or queued | unchanged | `skipped`, `stale` |
| The row and its render context are unchanged | unchanged | none |

Versions never go backwards: staging refuses work older than what the ledger holds, so two intakes racing for one record
leave the newer version standing. Work planned for a record a worker is sending right now is written behind that delivery
and sent after it. Before anything is sent, the worker compares the queued hashes with what the ledger says OSDU holds at
that moment; when neither half differs it settles the record with an attempt (`skipped`, `unchanged`) and sends nothing.

A row the ingestion table marks deleted is never delivered and never removed as a side effect: its hold says
`the ingestion table marked the record row deleted at <time>; a deleted row is never delivered. Remove the record from OSDU deliberately, or restore the row in the source.`

## Attempts

Each attempt names the record, the submission, the platform run, the worker, start and end, what was sent, the OSDU
version returned, the ingestion row the document was built from, the redacted error, and the steps of the try with what
OSDU answered and the correlation id every request carried ([The delivery ledger](ledger.md#osduattempt-every-try-and-every-decision-that-changed-the-record)).

| Outcome | Written when |
| --- | --- |
| `delivered` | A try sent the record and OSDU accepted every call. |
| `skipped` | Nothing was sent: unchanged at the final check, identical or stale at plan time, waiting on an undo, a row a sync no longer found, or a record a reversal passed over. |
| `held` | A try or a plan refused the record. |
| `failed` | A try failed; the record is retried (status `pending`) or failed for good (status `failed`). |
| `deleted` | A removal took the record out of OSDU, or a reversal removed a record its run created. |
| `historypurged` | A removal purged the record's earlier versions; the record itself is still live. |
| `restored` | An earlier version was written back, by a reversal or a step back to the previous version. |
| `undone` | What an unfinished delivery left in OSDU was undone. |

| Phase | Meaning |
| --- | --- |
| `metadata`, `payload`, `metadata+payload` | What a delivered try sent: the record document, the payload, or both. |
| `none` | A try that sent nothing: one that held, failed or will be retried. |
| `render`, `source-deleted`, `identical`, `stale` | Decisions the plan wrote (worker `intake`). |
| `unchanged` | The final hash check found OSDU already holding the queued work. |
| `undo-wait` | The newer work waited for an earlier delivery's undo ([below](#waiting-for-an-undo)). |
| `source-missing` | A sync found the row gone from the ingestion table, or outside the record's scope (worker `sync`). |
| `delete`, `purge-history` | A removal at the `record` or `everything` scope, or at the `history` scope. |
| `restore-previous` | An operator stepped the record back to the version before the latest. |
| `reverse` | What a reversal did to the record. |
| `undo` | An undo of an unfinished delivery, naming each object it took back. |

A try that sent nothing (`unchanged`, `undo-wait`) is not charged to the record's retry budget, and neither is a try a
stopping worker handed back.

## Retry, hold, fail

A send goes through two loops. Inside one call, the HTTP executor repeats a request that is safe to repeat after a
transient status or a transport failure, waiting from `reliability.retry.baseDelayMs` up to
`reliability.retry.maxDelayMs`; which requests and statuses it repeats is on
[Requests every route makes](protocols.md#requests-every-route-makes). Whatever a call still fails with reaches the
worker, which settles the record:

| What the try ended with | Record | Attempt and error |
| --- | --- | --- |
| Every call answered 2xx | `delivered`; the pending work becomes what OSDU holds | `delivered` |
| HTTP 400, 403, 404, 405, 409, 413, 415 or 422, or a status listed in `reliability.skipStatusCodes` | `held`, blocked | `held`: `HTTP <status> is not retryable: <message>` |
| A hold the route or the check before sending raised (a payload over `reliability.maxRequestBodyBytes`, a document the template refuses under `target.validation`, a document that fails an assertion of its mapping whose `onFail` is `hold`, a reference storage does not hold under `target.verifyReferences: storage`) | `held`, blocked; the rendered document is kept | `held`, with the route's reason |
| Any other HTTP status (401, 408, 429, 5xx, ...), a transport failure, a timeout, a read or parse failure | `pending`, due again after the record backoff, completed steps kept for the resume | `failed` with the message |
| The same, when the record has already been tried `reliability.retry.attempts` times | `failed`, blocked | `failed`: `failed after <n> attempt(s): <message>` |
| Anything unexpected | `failed`, blocked | `failed`: `unexpected failure: <type>: <message>` |

The record backoff is `recordBaseDelayMinutes` times 2 to the power of (tries made minus one), at most
`recordMaxDelayMinutes`; a `Retry-After` longer than `maxDelayMs` is not waited out inline, and the record's next try is no
sooner than the service asked. `reliability.retry.attempts` bounds both loops: the attempts one safe request is given inside
a call, and the tries a record is given before it fails. A record's try count starts again from zero when new work is
queued for it, when it is delivered, and when it is released with its rendered document.

| Key | Default | Meaning |
| --- | --- | --- |
| `reliability.retry.attempts` | `4` | Attempts of one safe request inside a call, and tries of a record before it fails. |
| `reliability.retry.backoff` | `exponential` | `exponential` or `fixed`, for the inline retries. |
| `reliability.retry.baseDelayMs` | `500` | The first inline wait. |
| `reliability.retry.maxDelayMs` | `30000` | The longest inline wait; a longer `Retry-After` moves to the record backoff. |
| `reliability.retry.honorRetryAfter` | `true` | Whether a service's `Retry-After` is honoured. |
| `reliability.retry.recordBaseDelayMinutes` | `1` | The first record backoff. |
| `reliability.retry.recordMaxDelayMinutes` | `60` | The longest record backoff. |
| `reliability.skipStatusCodes` | `[]` | More statuses that hold a record instead of retrying it. |

```yaml
reliability:
  leaseSeconds: 300
  skipStatusCodes: [412]
  retry:
    attempts: 4
    recordBaseDelayMinutes: 1
    recordMaxDelayMinutes: 60
```

A record with a data problem is held and its interface goes on. When so many records fail that the interface as a whole
has a problem, `failWhen` stops it: by default after 25 connection or permission failures in a row with none delivered
between (`failWhen.outageFailures`), and optionally on a share of held and failed records (`failWhen.failedPercent`,
judged once `failWhen.minRecords`, default 100, have settled) or on consecutive failures of one kind
(`failWhen.consecutiveFailures`). See [Delivery flow](../flow/delivery.md).

## Releasing a record

A release lets blocked records go again once their cause is fixed. It reaches the records named by key, every blocked
record of the flow, or every record one issue keeps blocked ([below](#issues)). What it does to each record:

| The record | After the release |
| --- | --- |
| Blocked and still holding its rendered document (held or failed by a try, held by the check before sending) | `pending` again in its submission, its tries counted from zero; the document is accepted as it is, so the check before sending sends it whatever its verdict and its mapping's assertions say. |
| Blocked with no rendered document (held at render, deleted by a reversal) | Unblocked and asked to be planned again (`PlanRequestedUtc`), with the note `released; the flow's next run plans it again from its ingestion rows`. |
| `reverted` | `delivered` again, asked to be planned again; the next plan sends only what renders differently from what OSDU now holds. |
| `waiting`, named by key | `pending` without its references: sent without waiting ("Send without waiting" on the record's page). A release of the whole flow leaves waiting records to their wait. |

A release changes the ledger only; the flow's next deliver run sends. A released record that holds its document is sent by
the next deliver run whatever that run plans, since a run takes the due records of every settled submission after its own
(ten submissions at a time). A record asked to be planned again is read by key at the start of the next run, 5,000 records
to a pass, as many passes as there are such records. From the GUI and the API a release can also queue that run at once.
A release records a `release` activity under who asked, and names every record it released in `osdu.ActivityRecord`.

```bash
sqlflow records release flows/welldb-wellbore-03-delivery.yaml --partition dev --key 6f1c2a9e-4b7d-4c1e-9a3f-2d8e5b7c1a40
sqlflow records release flows/welldb-wellbore-03-delivery.yaml --partition dev --issue 3fa1c09b5d2e7a44
```

**A record held for an assertion of its mapping** (`onFail: hold`, [mapping assertions](../flow/mapping-assertions.md))
is held by the check before sending with its document kept, under an error that starts `assertion:` and names the
property, the assertion and the value. The records one assertion holds share one issue whatever values they hold, since
the error quotes the values. A release sends the kept document as it is: its verdict is marked accepted and still lists
what the assertions found. The record stays held while only the mapping changes; correcting the row plans it again, and
so does a redelivery, under the mapping the flow pins then.

## Issues

A blocked record's error names the record: the value it read, an OSDU id, a moment, a row, the correlation id of the
refused request. A million records held for one reason carry a million different errors. The ledger keeps beside each
blocked record the **issue** its error names: the error with every part that names the record replaced by a placeholder
(the pattern), hashed. Records refused for the same reason share an issue, so a flow with a million blocked records shows
the handful of issues they share. The code calls an issue a problem (`ProblemHash`); the GUI's Issues tab, the API's
`/issues` routes and `sqlflow records issues` call it an issue.

The pattern is made in one place from the redacted error, so the same error is the same issue on every path:

| Part of the error | Becomes |
| --- | --- |
| The correlation id a refused request names | `<id>` |
| An OSDU kind (`osdu:wks:master-data--Wellbore:1.3.0`) | kept: it names what was refused |
| A web address | its scheme, host and the words of its path; other segments `<id>`, the query `<query>` |
| Any other address, and a file path | `<path>` |
| A moment (ISO 8601, a cache version label, a time of day) | `<time>` |
| A UUID or 32 hexadecimal characters | `<id>` |
| An OSDU record id | `<osdu id>` |
| A run of 16 or more hexadecimal characters | `<hash>` |
| A value in single, back or typographic quotes | `'<value>'` in its own quotes |
| A number standing alone | `<n>`; a digit inside a word (`v2`) stays |

A double-quoted string is kept, since that is how a service spells its reason. An error with nothing in it is the issue
`no reason recorded`. An issue is named by the first eight bytes of the pattern's SHA-256, written as 16 hexadecimal
characters wherever it appears. Two errors that differ in a part the rules keep are two issues: the rules split an issue
rather than merge two, which is the safer way to be wrong when a whole issue is released at once.

**Set errors and row errors.** When an issue's records name no value, or all name the same values (the same bad unit in
every row of a file), the issue is a **set error**: fix its cause once (the cache, the mapping, a legal tag) and release
its records together. When they name different values, each its own row's, it is **row errors**: fix the rows in the
source, and each corrected row is planned again on its own. The listing reads the shape from each issue's newest and
oldest record; an issue's own page reads it from samples spread across it (five by default, up to 20), which can only turn
a set error into row errors, never the reverse.

**Reading issues.** `GET /flows/{pipelineId}/issues` and `sqlflow records issues` list a flow's issues, the most records
first (50 by default in the CLI): each with its records, held and failed, when they last changed, the pattern and an
example record. `GET /flows/{pipelineId}/issues/{issue}` and `--issue` add the files its records came from and the
samples; `sqlflow records list --issue <id>` lists its records. Everything is counted from the blocked records through a
filtered index that holds them alone, never kept beside them.

**Releasing an issue** (`POST /flows/{pipelineId}/issues/{issue}/release`, `sqlflow records release --issue`) walks the
issue's records a page of 1,000 at a time and releases each page in its own statement; a record a run blocks with the same
issue while the walk goes is released too. The activity records the issue and the pattern the operator was shown.

**Records blocked before the ledger kept issues** have none. The control plane sorts them in the background, 500 records a
page a quarter of a second apart, looking again every ten minutes; until then a flow's issues say how many blocked records
are not sorted yet.

## Waiting records

A record whose rendered document refers, through a relationship its template declares, to an OSDU id another record of the
same ledger holds and has not delivered yet is left `waiting` when a worker comes to claim it, with `WaitingFor` naming that
id. Nothing is charged and no operator is needed: the record goes back to `pending` when the record holding the id lands.
An id no record of the ledger holds is not waited for (it is OSDU's or another system's), nor is one whose record was
removed from OSDU, nor one of an interface the source's order says this one does not wait for
([Interfaces](../flow/interfaces.md)). Every decision to wait is taken under one application lock, and a wait is followed
through at most 64 records: a longer chain is taken as one that might lead back to the record, which is then sent rather
than left waiting for good.

With `target.verifyReferences: storage`, ids the ledger does not hold are asked of OSDU's storage service before the record
is sent, and a record naming one storage does not hold is held instead.

## Waiting for an undo

A delivery that does not complete is undone ([Removal and reversal](removal-and-reversal.md#unfinished-deliveries-and-their-undo)).
Newer work for a record waits while an earlier delivery's undo has not finished, since it would write ids the undo still
has to take back. The try is skipped (phase `undo-wait`), nothing is sent and nothing is charged. Once the undo has used its
ten tries the record is held, saying
`an earlier delivery of this record left <n> item(s) in OSDU that 10 undo tries could not take back (...)`, and releasing it
tries the undo again before the newer work.

## Related pages

- [The delivery ledger](ledger.md): every column a record and an attempt keep.
- [Submissions](submissions.md): how a run plans, claims and settles records.
- [Change detection](change-detection.md): what makes a record pending again.
- [Operations runbook](../guides/operations-runbook.md): held, failed, waiting and stuck records, task by task.
- [`sqlflow records`](../cli/records.md): list, show, issues and release from the command line.
