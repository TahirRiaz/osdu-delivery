# Submitting records

For a source system, or an operator, that sends records to OSDU Delivery directly instead of preparing a drop. The
source sends each record's metadata in its own shape, the columns it has; the flow's pinned mapping transforms it into
the OSDU document; and the run delivers it through the regular process: the manifest check, the preflight gate, change
detection, the ledger, the drain and the record history. Nothing about delivery is different from a drop, which is the
point: a record sent this way is traceable in exactly the same way ([design.md](design.md) section 3.4).

[preparing-a-drop.md](preparing-a-drop.md) is the other way in, for larger sets and for sets prepared in bulk.

A submission has two parts: the **metadata**, which the request carries, and, for a flow that streams files, **where the
payload files already are**, which the request points at. Files are never uploaded through the API and never staged: the
record says where its files sit, and the node opens that location with its own identity when the run delivers, and again
on every retry. That is the same "the payload goes past, not through" handling a prepared drop gets ([design.md](design.md)
section 3.2), and it is what lets a submission deliver through the protocols that stream files.

## 1. When to use it

**The flow has to offer it.** A flow takes records sent in a request only when its document says so:

```yaml
source:
  location: abfss://lake@acct.dfs.core.windows.net/osdu-prepare/{site}
  lastModified: update_date
  manualSubmission: true            # this flow also takes records sent in a submission request
  manualSubmissionFileRoots:        # where such a record may point at its payload files
    - abfss://lake@acct.dfs.core.windows.net/recall
```

It is opt-in, because a flow fed by a prepared drop should not also accept hand-written records unless the estate decided
it should. A request to a flow that does not declare it is refused, naming the key.

**`manualSubmissionFileRoots` is what keeps a submission honest about files.** The node reads the files with its own
identity, which can read whatever it has been granted, so an unguarded location would let a caller have any readable file
shipped to OSDU. A record may only point inside one of the declared roots; a flow that declares none allows the fixed
part of its own `source.location` (everything before its first `{parameter}` token), so every drop of that flow is
inside. A location outside them, or one containing `..`, is refused when the request is accepted.

| Use a submission of records when | Use a drop when |
| --- | --- |
| The flow declares `source.manualSubmission`. A flow that streams payload files (`osduWellLog` bulk data, `osduFile`, `osduManifest`) offers it on the same terms: each record says where its files are. | The set is larger, or is prepared in bulk (Databricks). |
| A handful of records at a time: at most 1,000 records, 100,000 child rows and 8 MB of metadata per submission. | The payload files still have to be written, and the prepare run is what writes them. |
| The source reacts to a change as it happens (an edit, an approval, a correction). | The source works in scheduled batches. |

## 2. What happens to a submission

1. `POST /api/v1/delivery/submissions` with `records`. The control plane checks the request: the flow exists and takes
   records, the flow parameter values resolve against the flow's declarations, and the records have the shape below.
   It then stores the records in the ledger (`delivery.InlineSubmission`) together with the run that takes them, in one
   transaction, and answers `202 Accepted` with the run id. The mapping the flow pins at that moment is recorded with
   the records.
2. A node runs the flow. It writes the records as a drop under the flow's work location,
   `{work}/inline/{submissionId}`, where `{work}` is the flow's `source.work`, or `{source.location}/.work` when the flow
   declares none (the flow parameter values filled in). The drop's manifest names the recorded mapping, the parameter
   values and the submission id. The flow's declared source location is never written to: it belongs to the preparing
   side.
3. From there the run reads that drop like any other: the manifest is checked against the flow (a flow whose pinned
   mapping moved since the records were accepted refuses them, exactly as it refuses a drop prepared for an earlier
   mapping), the preflight gate checks the mapping, each record is rendered, compared with what the ledger holds, staged
   and delivered.
4. Everything is traceable afterwards: the submission's page shows the records as sent, by whom and when, and where the
   drop was written; each record's history names the submission; `GET /api/v1/delivery/submissions/{id}/content` returns
   the records.

A run that takes the submission again (a re-run, or a redelivery of a record whose last submission this was) writes the
drop again from the ledger when it is no longer there, so cleaning up work locations never loses a submission.

## 3. The request

```http
POST /api/v1/delivery/submissions
Authorization: Bearer <token with the operate scope>
Content-Type: application/json

{
  "flow": "e2e-wellbore",
  "parameters": { "marker": "ODLIVE20260911" },
  "submissionId": "0191e0a4-7a1c-7c3e-9b2e-5d0f3c8a1b42",
  "records": [
    {
      "record": {
        "facility_name": "ODLIVE20260911-INLINE-1",
        "facility_description": "Sent by the source system",
        "update_date": "2026-09-11T12:00:00Z"
      }
    }
  ]
}
```

| Field | Rule |
| --- | --- |
| `flow`, `repoId`, `pipelineId` | The flow, named by `flow` (with `repoId` when the name exists in more than one repository) or by `pipelineId`. |
| `records` | The records: 1 to 1,000, each in the shape of section 4, with `files` for a flow that streams them. A submission carries `records` or `drop`, never both. |
| `parameters` | The flow parameter values, as for a run. A required parameter without a default must be given; an undeclared one is refused. |
| `submissionId` | Optional. The idempotency key (section 6): a UUID the source mints for each change it sends. Without one a new id is minted. |
| `operation` | `deliver` (the default), or `plan` to render the records and report what a delivery would do without sending anything. |
| `force` | Optional. Plans past the change gates, as for a run. Each record's own hashes still decide what is sent. |
| `pool` | Optional. Routes the run to a worker pool. |

## 4. Records

Each record has the shape of a mapping fixture:

```json
{
  "record": { "source_project": "NO_15_9", "log_id": "L-1001", "index_min": 1000, "update_date": "2026-09-11T12:00:00Z" },
  "scopes": {
    "curves": [
      { "curve_id": "GR", "curve_unit": "GAPI" },
      { "curve_id": "MD", "curve_unit": "M" }
    ]
  }
}
```

- **`record`** is the root row: one column per value the mapping reads. **`scopes`** holds the rows of each child scope
  the mapping iterates, nested under the record they belong to. A record without child rows leaves `scopes` out.
- **Values** are strings, numbers, booleans or null. A column holds one type in every row of its scope: whole numbers
  and decimals together are decimals; a string in one row and a number in another is refused. A whole number outside the
  64-bit range is refused; send it as a string. An object or an array as a value is refused; a collection is a child
  scope.
- **A column left out is null.** The written drop declares every column the mapping reads, so a record may leave out the
  columns it has no value for. A column the mapping does not read changes nothing, and the run log names it, so a
  misspelt column is visible there.
- **Column names** are at most 128 characters and compared without case, so `Name` and `name` in one row are refused.
  Scope names are identifiers (letters, digits, `_` and `-`).
- **The natural key** columns (the source columns of the mapping's `identity.naturalKey`) must be non-empty. A record
  whose key is incomplete is reported as untracked by the run and not delivered, as it would be in a drop.
- **Each record once per submission.** Two records with the same natural key fail the run, naming both.
- **`deliveryKey`** may be sent in the root row, as a drop does; when it is, it must be the key the delivery side derives
  (a different one holds the record). A child row never carries it: it belongs to the record it is nested under.
- **The version column** the flow names (`source.lastModified` or `source.fingerprint`) works as it does for a drop. A
  record carrying a moment older than the version already delivered is recorded as stale and never sent, and the same
  moment with the same content is skipped, so a change needs a later moment.

### Payload files

A flow that streams payload files takes them the same way, by pointing at them. The payload the flow streams is named
under `files`:

```json
{
  "record": { "source_project": "NO_15_9", "log_id": "L-1001", "update_date": "2026-09-11T12:00:00Z" },
  "files": { "curves": "abfss://lake@acct.dfs.core.windows.net/recall/L-1001/chunk_*.parquet" }
}
```

- **The value** is where the files are: a folder, or a glob over the chunk files. `{ "location": ..., "hash": ... }` is
  the longer form, carrying the payload's content hash with it.
- **Every record points at the payload the flow streams**, under that payload's name. A record that points at nothing,
  or at a payload the flow does not stream, is refused; so is a record carrying `files` for a flow that streams none.
- **The location must sit inside the flow's `manualSubmissionFileRoots`** (section 1), and may not contain `..`.
- **The hash is required when the flow decides payload changes by content hash** (`change.payloadDetect: contentHash`,
  the default). A flow declaring `change.payloadDetect: lastModified` takes the files' modified times, names and sizes
  as the payload's watermark instead, and a hash is then optional. Which one applies is in the source contract.
- **Nothing is read at submission time.** The request is checked for shape and roots only; whether the files exist and
  are readable is decided by the run, which reports a record whose files cannot be listed or read as held or failed, with
  the location in the message.
- The written drop carries the location (and the hash) in a reserved root column per payload, and its manifest declares
  the payload by `locationColumn` rather than a path template, so the delivery side reads each record's files from where
  that record said they are ([drop-contract.md](drop-contract.md)).

Which columns a flow reads, which are the natural key, which parameters it declares, which payload its records point at
(with whether a hash is required and the roots allowed) and whether it takes records at all is answered by
`GET /api/v1/delivery/flows/{pipelineId}/source-contract` (scope `read`).

## 5. The answers

| Status | When | Body |
| --- | --- | --- |
| `202 Accepted` | The records were stored and a run queued. `Location: /api/v1/runs/{runId}`. | `{ "runId", "pipelineId", "flowName", "status", "submissionId", "replayed": false }` |
| `200 OK` | The same request was accepted before under this `submissionId`: nothing new is queued. | The same body, with the run that request started and `"replayed": true`. |
| `400 Bad Request` | The request is malformed (`Invalid request`), a parameter does not resolve (`Invalid run parameters`), a record breaks section 4 (`Invalid records`, naming the record, scope and column), a record points at files the flow does not allow or leaves them out (`Invalid records`, naming the record and the roots), or the flow offers no manual submission (`Records not accepted by this flow`). | Problem details. |
| `404 Not Found` | No active delivery flow by that name or id. | Problem details. |
| `409 Conflict` | The flow name is ambiguous, or the `submissionId` was used before for a different request (naming what differs) or by a drop. | Problem details. |

`GET /api/v1/runs/{runId}` then reports the run's progress and outcome, with the counts of what it planned, delivered,
skipped, held and failed.

## 6. Idempotency

A `submissionId` names one request. Send the same request again under it (after a timeout, say) and the answer is the run
the first one started, with nothing queued twice, however many repeats race each other. The same id with different
records, parameters, operation or `force`, or once the flow pins another mapping, is refused with `409`, naming what
differs: a new change is a new id.

Without a `submissionId` every request is a new submission. That is safe for delivery (records OSDU already holds are
skipped by the change gates), but each retry leaves a submission of its own in the ledger, so a source that retries
should send its own id.

## 7. Preview

`"operation": "plan"` stores the records and queues a plan run: the run writes the drop, checks it and renders every
record, and its log and outcome report what a delivery would create, update, skip or hold. Nothing is written to OSDU
and no record enters the ledger. A delivery afterwards is a new request with a new `submissionId`.

## 8. From the GUI

A delivery flow's page has **Submit records**, and **Manual submission** in the navigation lists every flow that offers
it (with the payload each streams, and, on request, the flows that offer none and why). The dialog builds a form from the
flow's source contract for one record (the natural key and version columns marked, a Now button for the version column),
or takes any number of records as JSON in the shape of section 4. For a flow that streams files it also asks where the
record's files are, and for the hash when the flow needs one, showing the roots the flow allows. It offers the flow
parameters, the preview, `force` and an optional submission id, and opens the run it queued. A submission of records has
a **Records sent** tab on its page.

## 9. What each mistake leads to

| What you see | Why | What to do |
| --- | --- | --- |
| `400 Records not accepted by this flow` | The flow declares no `source.manualSubmission` | Add `manualSubmission: true` to the flow's source block, or deliver those records as a drop. |
| `400 records[0] points at no files: flow ... streams the payload 'curves'` | The flow streams files and the record named none | Add `"files": { "curves": "..." }` to every record. |
| `400 payload location '...' is outside what flow ... allows` | The location is not inside the flow's roots | Point inside a declared root, or add the root to `source.manualSubmissionFileRoots`. |
| `400 records[0] gives no hash for payload ...` | The flow decides payload changes by content hash | Send the hash with the location, or let the flow watch the files' modified times (`change.payloadDetect: lastModified`). |
| A record is held: no payload chunk files under ... | The location is empty, or the node cannot see it | Check the files are there and the node's identity may read them. |
| `400 Invalid records: records[3].record.depth is a string, but records[0].record.depth is a number` | A column holds two types | Send one type per column. |
| `409 ... differs from it in the records` | A `submissionId` was reused for a changed request | Use a new id for a new change. |
| The run fails: "the drop was prepared for mapping ..." | The flow was promoted to another mapping between the request and the run | Send the records again under a new id. |
| The run fails: "records[0] and records[1] are the same record" | One record twice in one submission | Send each record once. |
| The run log warns that a column is not read by the mapping | The column is misspelt, or the mapping does not use it | Check the source contract's columns. |
| A record is skipped as `stale` | Its version column is older than the version delivered | Send a later moment. |
| Nothing is sent and the record is `unchanged` | The record renders to what OSDU already holds | Nothing to do. |
