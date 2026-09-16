# Submitting records

For a source system, or an operator, that sends records to OSDU Delivery directly instead of writing files for the
pre-ingestion flow itself. The source sends each record's metadata in its own shape, the columns it has; the
submission **lands those rows as files for the flow's declared pre-ingestion flows**; and the chain of pre, ingestion
and OSDU runs it queues loads them into the ingestion tables and delivers them. Nothing about the delivery is
different from a file the preparing side dropped into the landing folder itself, which is the point: a record sent
this way becomes the same rows in the same tables, and is traceable in exactly the same way
([architecture.md](architecture.md)).

A submission has two parts: the **metadata**, which the request carries, and, for a flow that streams files, **where
the payload files already are**, which the request points at. Files are never uploaded through the API and never
staged: the record says where its files sit, and the node opens that location with its own identity when the run
delivers, and again on every retry. That is what lets a submission deliver through the protocols that stream files.

## 1. When to use it

**The flow has to offer it.** A flow takes records sent in a request only when its document says where they land:

```yaml
source:
  connection: ${env:OSDU_SAMPLE_DB}
  record:
    object: OsduSample.ing.WellLog
    key: [source_project, log_id]
  submissions:
    record:
      preFlow: recall-welllog-pre        # the pre-ingestion flow that reads the landing folder
      landing: ../data/welllog           # where the record rows are written, inside that flow's source.location
    datasets:
      curves:
        preFlow: recall-welllog-curves-pre
        landing: ../data/curves-meta
    fileRoots:                           # extra prefixes a submitted record may point payload files inside
      - ../data/curves
```

It is opt-in, because a flow fed by files the preparing side writes should not also accept hand-written records
unless the estate decided it should. A request to a flow that declares no `source.submissions` is refused, naming the
key.

**`landing` has to be a folder the named pre flow actually reads.** Each declared pre flow is checked when the
request arrives: the pipeline exists and is active, `landing` lies under its `source.location`, the file name the
submission will write matches its `srcFile` glob, and its `source.type` is a format the writer supports. A
declaration that fails any of these refuses the submission before anything is stored, naming what is wrong.

**The payload roots are what keep a submission honest about files.** The node reads the files with its own identity,
which can read whatever it has been granted, so an unguarded location would let a caller have any readable file
shipped to OSDU. A record may only point inside a declared `payloads.<name>.root` or one of
`submissions.fileRoots`. A location outside them, or one containing `..`, is refused when the request is accepted.

| Use a submission of records when | Let the preparing side write the files when |
| --- | --- |
| The flow declares `source.submissions`. A flow that streams payload files (`osduWellLog` bulk data, `osduFile`, `osduManifest`) offers it on the same terms: each record says where its files are. | The set is larger, or is produced in bulk by a job that is already writing files. |
| A handful of records at a time: at most 1,000 records, 100,000 child rows and 8 MB of metadata per submission. | The payload files still have to be written, and that job is what writes them. |
| The source reacts to a change as it happens (an edit, an approval, a correction). | The source works in scheduled batches. |

## 2. What happens to a submission

1. **It is checked** (`POST /api/v1/delivery/submissions`), before anything is stored: the flow exists, is a delivery
   flow and declares `source.submissions`; the records parse and keep to section 4; every record carries a non-empty
   value for every column of `source.record.key`; every payload location sits inside the roots, with a hash when the
   flow decides payload changes by content hash; each declared pre flow passes the checks in section 1; and the flow
   parameter values resolve against the flow's declarations.
2. **It is stored**, in one transaction: the submission itself (`osdu.InlineSubmission`, status `accepted`), one
   landing row per dataset (`osdu.SubmissionLanding`) carrying the location, file name, format, row count and content
   hash the write will produce, and an `osdu.Activity` of kind `submit` naming who sent it.
3. **The files are landed.** One file per dataset is written into the declared landing folder: the record rows for
   `record`, and one file per child dataset. The file is written under a temporary name that does not match the pre
   flow's `srcFile`, then promoted, so a pre run never reads a half-written file. Each landing row is then marked
   written and the submission becomes `landed`.
4. **The chain is enqueued**, in one transaction with the ledger rows that describe it: the declared pre flows, every
   flow that is both a descendant of those pre flows and an ancestor of the OSDU flow (the ingestion flows), and the
   OSDU flow, ordered by their lineage waves. Each pre flow member runs with a full load and a file pattern naming
   exactly the file this submission landed, so it reads that file whatever its watermark says. The OSDU member runs
   the submission's `deliver` or `plan` with the accepted parameter values. The submission becomes `queued`, carrying
   the group id and the OSDU run id. A declared pre flow that does not reach the OSDU flow through lineage refuses the
   submission, naming the flow, before anything is enqueued.
5. **The chain runs.** The pre run lands the file into the pre table, the ingestion run upserts it into the keyed
   ingestion table, and the OSDU run plans the submission's keys against that table and delivers them. On completion
   the submission becomes `completed` or `failed`.

A submission that was stored but whose files or chain did not follow (a host that stopped between the steps) is
finished by the control plane's own resume service: landing the files again is a no-op when they are already there
with the same hash, and the enqueue checks the group inside its transaction, so neither step can happen twice.

### What the ledger records

| Table | What it holds |
| --- | --- |
| `osdu.InlineSubmission` | Exactly what was sent (the canonical records, the content and request hashes), for which flow, mapping and parameter values, the operation, who sent it and when, its status, its chain group and OSDU run, and the redacted error when it failed. |
| `osdu.SubmissionLanding` | Per dataset: which file was written where, in which format, with how many rows and bytes, its content hash, when it was written, and which pre run took it. The file name is unique across the ledger, so a delivered record's origin file resolves back to the submission that landed it. |
| `osdu.Submission` (`Kind = inline`) | The plan and delivery counts, the render context, the work batches and the reference. |
| `osdu.Record` and `osdu.Attempt` | The origin file and row of each delivered version. |
| `osdu.Activity` | `submit` for the request, and the OSDU run's `deliver` or `plan`. |

Because the link is the file name, traceability holds even when a scheduled pre run picked the landing file up and
delivered the record before the submission's own chain ran.

## 3. The request

```http
POST /api/v1/delivery/submissions
Authorization: Bearer <token with the operate scope>
Content-Type: application/json

{
  "flow": "recall-wellbore",
  "parameters": { "site": "NO_15_9" },
  "submissionId": "0191e0a4-7a1c-7c3e-9b2e-5d0f3c8a1b42",
  "records": [
    {
      "record": {
        "facility_name": "NO 15/9-19 SR",
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
| `records` | The records: 1 to 1,000, each in the shape of section 4, with `files` for a flow that streams them. Required. |
| `parameters` | The flow parameter values, as for a run. A required parameter without a default must be given; an undeclared one is refused. |
| `submissionId` | Optional. The idempotency key (section 6): a UUID the source mints for each change it sends. Without one a new id is minted. |
| `reference` | Optional. What the sending system calls this submission in its own records: a filename, a ticket, a job id. At most 200 characters on one line, stored trimmed, never interpreted, and searchable (section 5). It is part of the request `submissionId` names, so a repeat that relabels the work is refused. |
| `operation` | `deliver` (the default), or `plan` to render the records and report what a delivery would do without sending anything. |
| `force` | Optional. Plans past the change gates, as for a run. Each record's own hashes still decide what is sent. |
| `reland` | Optional. Writes the landing files again from what the ledger stored before the chain is queued, for a submission whose files were removed from the landing folders. |
| `pool` | Optional. Routes the runs to a worker pool. |

## 4. Records

Each record has the shape of a mapping fixture:

```json
{
  "record": { "source_project": "NO_15_9", "log_id": "L-1001", "index_min": 1000, "update_date": "2026-09-11T12:00:00Z" },
  "datasets": {
    "curves": [
      { "curve_id": "GR", "curve_unit": "GAPI" },
      { "curve_id": "MD", "curve_unit": "M" }
    ]
  }
}
```

- **`record`** is the dataset's row: one column per value the mapping reads as `dataset.<column>`. **`datasets`** holds
  the rows of each child dataset the mapping reads (`dataset.<child>`), keyed by the child dataset's name and nested
  under the record they belong to, exactly as a mapping fixture carries them. A record without child rows leaves
  `datasets` out. Each child dataset is written as its own landing file, for the pre flow declared for it under
  `source.submissions.datasets`.
- **Values** are strings, numbers, booleans or null. A column holds one type in every row of its dataset: whole numbers
  and decimals together are decimals; a string in one row and a number in another is refused. A whole number outside the
  64-bit range is refused; send it as a string. An object or an array as a value is refused; a collection is a child
  dataset.
- **A column left out is null.** Each landing file declares every column any record sent and every column the mapping
  reads, null-filled, so a record may leave out the columns it has no value for.
- **The join columns of a child row are filled from its parent.** A child row that sends a join column must send the
  same value its parent carries, or the request is refused.
- **Column names** are at most 128 characters and compared without case, so `Name` and `name` in one row are refused.
  Child dataset names are identifiers (letters, digits, `_` and `-`), and a submission carries at most 32 child
  datasets.
- **The record key** columns (the columns `source.record.key` names) must be non-empty on every record: they are what
  the OSDU flow reads the record back by after the ingestion run has loaded it.
- **Row order is the order sent**, so the row number the ledger records as a record's origin is its position in the
  file the submission landed.
- **The version column** the flow names (`source.lastModified`) works as it does for any other row. A record carrying a
  moment older than the version already delivered is recorded as stale and never sent, and the same moment with the
  same content is skipped, so a change needs a later moment.

### Payload files

A flow that streams payload files takes them the same way, by pointing at them. Each payload the flow declares is
named under `files`:

```json
{
  "record": { "source_project": "NO_15_9", "log_id": "L-1001", "update_date": "2026-09-11T12:00:00Z" },
  "files": { "curves": "../data/curves/NO_15_9/L-1001" }
}
```

- **The value** is where the files are: a folder, or a glob over the chunk files. `{ "location": ..., "hash": ... }` is
  the longer form, carrying the payload's content hash with it.
- **Every record points at every payload the flow streams**, under that payload's name. A record that points at
  nothing, or at a payload the flow does not stream, is refused; so is a record carrying `files` for a flow that
  streams none.
- **The location must sit inside the payload's `root` or one of `submissions.fileRoots`** (section 1), and may not
  contain `..`. The location and the hash are written into the landing file's `locationColumn` and `hashColumn`, so
  the ingestion table carries them exactly as a file written by the preparing side would.
- **The hash is required when the flow decides payload changes by content hash** (`change.payloadDetect: contentHash`,
  the default). A flow declaring `change.payloadDetect: lastModified` takes the files' modified times, names and sizes
  as the payload's watermark instead, and a hash is then optional. Which one applies is in the source contract.
- **Nothing is read at submission time.** The request is checked for shape and roots only; whether the files exist and
  are readable is decided by the run, which reports a record whose files cannot be listed or read as held or failed,
  with the location in the message.

### The source contract

Which columns a flow reads and what each of them fills, which are the record key, which template version the mapping
fills, which parameters it declares, which payloads its records point at (with whether a hash is required and the
roots allowed) and whether it takes records at all is answered by
`GET /api/v1/delivery/flows/{pipelineId}/source-contract` (scope `read`):

| Field | What it says |
| --- | --- |
| `template` | `{ kind, version, saved }`: the template version the flow's mapping fills. `saved: false` means a run cannot render the records until that version is saved on the Templates page. |
| `system`, `key`, `label` | The mapping's `dataset.system`, the record key's columns (bare names) and its `dataset.label`. |
| `columns` | The dataset row's columns, each `{ name, key, label, uses }`. Every use is `{ target, role, source, required, modifiers, findBy, appliesWhen }`: the template variable the entry fills (such as `osdu.data.FacilityName`), and a `role` of `value` (the column's value, modified, is what the entry writes), `findBy` (the column's value finds the cached record the entry writes from) or `appliesWhen` (the column decides whether the entry applies). |
| `datasets` | The child datasets, each `{ name, fills, columns }`: the lists it fills (`{ target, required }`) and its columns, described as above. |
| `parameters`, `lastModifiedColumn` | The flow parameters a submission carries, and the column the flow versions rows by. |
| `sourceObject`, `updatedColumn` | The ingestion table the flow reads its records from, and the system column its incremental reads window on. |
| `payloads`, `payloadHashRequired`, `payloadRoots` | The payloads the flow streams, whether each record's files need a content hash, and the roots they may sit inside. |
| `acceptsRecords`, `recordsRefusal`, the ceilings | Whether the flow takes records, why not when it does not, and how much one submission may carry. |
| `mappingProblem` | Why the columns are unknown, or, while they are listed, that the pinned template version is not saved. |

## 5. The answers

| Status | When | Body |
| --- | --- | --- |
| `202 Accepted` | The records were stored, landed, and the chain queued. | `{ "runId", "pipelineId", "flowName", "status", "submissionId", "replayed": false, "groupId" }`, where `runId` is the OSDU flow's run in the chain and `groupId` the chain itself. |
| `200 OK` | The same request was accepted before under this `submissionId`: nothing new is queued. | The same body, with the chain that request queued and `"replayed": true`. |
| `400 Bad Request` | The request is malformed (`Invalid request`), a parameter does not resolve (`Invalid run parameters`), a record breaks section 4 (`Invalid records`, naming the record, the child dataset and the column), a record points at files the flow does not allow or leaves them out (`Invalid records`, naming the record and the roots), or the flow takes no records (`Records not accepted by this flow`, naming what its document is missing). | Problem details. |
| `404 Not Found` | No active delivery flow by that name or id. | Problem details. |
| `409 Conflict` | The flow name is ambiguous, or the `submissionId` was used before for a different request (naming what differs) or by a submission that was not sent through the API. | Problem details. |
| `502 Bad Gateway` | The submission was accepted and its landing files could not be written. | Problem details naming the location; the resume service finishes it. |

`GET /api/v1/runs/{runId}` then reports the OSDU run's progress and outcome, and
`GET /api/v1/delivery/submissions/{id}/content` reports the submission itself: its status, the files it landed with
the pre run that took each, and the records as sent.

### Finding a submission again

A source that keeps its own records does not have to keep this system's ids as well. The `reference` it sent is on the
submission and on every page that shows one, and the listing narrows on it:

```http
GET /api/v1/delivery/flows/{pipelineId}/submissions?reference=L-1001.las
```

The match is a containment, so a fragment of a filename finds the submission whose reference embeds it. It reaches the
submissions the ledger registered, which is to say the ones an OSDU run has planned; the records as sent, with their
reference, are at `GET /api/v1/delivery/submissions/{id}/content` from the moment they were accepted.

## 6. Idempotency

A `submissionId` names one request. Send the same request again under it (after a timeout, say) and the answer is the
chain the first one queued, with nothing queued twice, however many repeats race each other. The same id with
different records, parameters, operation or `force`, or once the flow pins another mapping, is refused with `409`,
naming what differs: a new change is a new id.

Without a `submissionId` every request is a new submission. That is safe for delivery (records OSDU already holds are
skipped by the change gates), but each retry lands another set of files and leaves a submission of its own in the
ledger, so a source that retries should send its own id.

## 7. Preview

`"operation": "plan"` stores the records, lands the files and queues the chain with the OSDU flow running `plan`: the
pre and ingestion runs load the rows as they always would, and the OSDU run renders every record and reports what a
delivery would create, update, skip or hold. Nothing is written to OSDU. A delivery afterwards is a new request with
a new `submissionId`.

## 8. From the GUI

A delivery flow's page has **Submit records**, and **Manual submission** in the navigation lists every flow that
offers it (with the payloads each streams, and, on request, the flows that offer none and why). The dialog builds a
form from the flow's source contract for one record (each field with what it fills, a Now button for the version
column, and each child dataset with the list it fills), with the template kind and version next to the mapping, or
takes any number of records as JSON in the shape of section 4. For a flow that streams files it also asks where each
payload's files are, and for the hash when the flow needs one, showing the roots the flow allows. It offers the flow
parameters, the preview, `force`, an optional submission id and an optional reference, and opens the run it queued.

A submission sent this way has a **Landed files** tab on its page, listing the file written for each dataset with its
pre flow, format, row count, hash and the pre run that took it, and a **Records sent** tab with the records as sent,
who sent them and how far the submission got. The submissions list shows each one under the name its source gave it.

## 9. What each mistake leads to

| What you see | Why | What to do |
| --- | --- | --- |
| `400 Records not accepted by this flow` | The flow declares no `source.submissions` | Add the block, naming the pre flow and landing folder for the record and for each child dataset, or let the preparing side write the files. |
| `400 ... landing ... is not under the source location of pre flow ...` | The landing folder is not somewhere the named pre flow reads | Point `landing` inside that flow's `source.location`. |
| `400 ... does not match the srcFile of pre flow ...` | The pre flow's glob would not pick the file up | Widen its `srcFile`, or land into a folder whose pre flow accepts the name. |
| `400 records[0].record.log_id is empty` | A record does not carry every key column | Every record names every column of `source.record.key`. |
| `400 records[0] points at no files: flow ... streams the payload 'curves'` | The flow streams files and the record named none | Add `"files": { "curves": "..." }` to every record. |
| `400 payload location '...' is outside what flow ... allows` | The location is not inside the roots | Point inside the payload's `root`, or add the prefix to `source.submissions.fileRoots`. |
| `400 records[0] gives no hash for payload ...` | The flow decides payload changes by content hash | Send the hash with the location, or let the flow watch the files' modified times (`change.payloadDetect: lastModified`). |
| `400 Invalid records: records[3].record.depth is a string, but records[0].record.depth is a number` | A column holds two types | Send one type per column. |
| `409 ... differs from it in the records` | A `submissionId` was reused for a changed request | Use a new id for a new change. |
| `409 ... differs from it in the reference` | A repeat under one id renamed the work, or left the name off | Send the same reference the first request carried, or use a new id. |
| `400 reference is at most 200 characters` | The reference carries content rather than a name | Send the name; put the content in the records. |
| `502 ... could not be landed` | Storage refused the write | Fix the access to the landing folder; the resume service finishes the submission. |
| A record is held: the ingestion tables hold no row for this key | The pre or ingestion run has not loaded the landing file, or it failed | The hold names the landing file and the flows expected to have loaded it; look at those runs in the chain. |
| A record's origin names a file other than the submission's | The ingestion table already held identical content from that file, so the upsert left the row untouched | Nothing to do: the record is delivered from the row that stands, and the submission's own file is still in the ledger. |
| A record is held: no payload files under ... | The location is empty, or the node cannot see it | Check the files are there and the node's identity may read them. |
| A record is skipped as `stale` | Its version column is older than the version delivered | Send a later moment. |
| Nothing is sent and the record is `unchanged` | The record renders to what OSDU already holds | Nothing to do. |
