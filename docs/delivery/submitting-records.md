# Submitting records

For a source system, or an operator, that sends records to OSDU Delivery directly instead of preparing a drop. The
source sends each record's metadata in its own shape, the columns it has; the flow's pinned mapping renders it into
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
| `reference` | Optional. What the sending system calls this submission in its own records: a filename, a ticket, a job id. At most 200 characters on one line, stored trimmed, never interpreted, and searchable (section 5). It is part of the request `submissionId` names, so a repeat that relabels the work is refused. A drop's reference is the one its manifest carries. |
| `operation` | `deliver` (the default), or `plan` to render the records and report what a delivery would do without sending anything. |
| `force` | Optional. Plans past the change gates, as for a run. Each record's own hashes still decide what is sent. |
| `pool` | Optional. Routes the run to a worker pool. |

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
  `datasets` out. The run writes each child dataset as the drop scope of the same name.
- **Values** are strings, numbers, booleans or null. A column holds one type in every row of its dataset: whole numbers
  and decimals together are decimals; a string in one row and a number in another is refused. A whole number outside the
  64-bit range is refused; send it as a string. An object or an array as a value is refused; a collection is a child
  dataset.
- **A column left out is null.** The written drop declares every column the mapping reads, so a record may leave out the
  columns it has no value for. A column the mapping does not read changes nothing, and the run log names it, so a
  misspelt column is visible there.
- **Column names** are at most 128 characters and compared without case, so `Name` and `name` in one row are refused.
  Child dataset names are identifiers (letters, digits, `_` and `-`), and a submission carries at most 32 child
  datasets.
- **The dataset key** columns (the columns the mapping's `dataset.key` names) must be non-empty. A record
  whose key is incomplete is reported as untracked by the run and not delivered, as it would be in a drop.
- **Each record once per submission.** Two records with the same dataset key fail the run, naming both.
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

### Dropping the files off first

A record points at files that already exist. When they do not exist yet, the drop-off area is the pre-step: upload them
to a place the compute nodes can read, then submit records pointing at where they landed.

```http
POST /api/v1/delivery/dropoffs
Authorization: Bearer <token with the operate scope>
Content-Type: multipart/form-data

(one or more file parts, and an optional "label" field)
```

The answer carries the `location` the files landed under, which is what goes into the submission's `files`:

```json
{ "dropOffId": "0191e0a4-7a1c-7c3e-9b2e-5d0f3c8a1b42",
  "location": "abfss://lake@acct.dfs.core.windows.net/dropoff/0191e0a4-7a1c-7c3e-9b2e-5d0f3c8a1b42",
  "status": "complete",
  "uploadMode": "stream",
  "files": [ { "name": "L-1001.csv", "bytes": 20480, "sha256": "d7f848...", "hashSource": "computed" } ] }
```

- **Where it lands** is the deployment's drop-off area, `SQLFLOW_DROPOFF_ROOT`, read by the control plane (which writes
  there) and by every node (which reads there). Unset, the upload is refused saying so, and there is no drop-off area.
- **Every flow that takes submissions may point inside it**, in addition to its own `manualSubmissionFileRoots`: the
  deployment owns that area, everything in it arrived through the API, and the ledger says who put each file there.
- **The bytes pass through the control plane once, on the way to storage**, which is the one place they do. The delivery
  itself still streams from storage to OSDU without passing through the control plane. Uploads are bounded for that
  reason: 100 MB per file and 20 files per upload by default (`ControlPlane:DropOff:MaxFileMegabytes` and
  `:MaxFilesPerUpload`). A file larger than that goes straight to storage instead (below), and a set larger than the
  drop-off is for is prepared as a drop.
- **Each file's SHA-256 is computed as it streams past**, so a record that needs a payload hash can carry the one the
  upload reported without reading the files again. Each file says so: `hashSource` is `computed`.
- **Nothing is removed automatically.** Re-processing a submission (a redelivery, a verify) reads its files again, so a
  drop-off is kept until somebody deletes it (`DELETE /api/v1/delivery/dropoffs/{id}`). A deployment whose uploads are
  single-use sets `ControlPlane:DropOff:RetentionDays`, and then a sweep removes drop-offs that **completed** longer ago
  than that. An upload that failed or stopped halfway is never swept; it stays until it is dealt with.
- **A file name is a name, not a path.** Names carrying a separator or `..` are refused, so nothing lands outside the
  drop-off it belongs to.

### A file too large to send through the control plane

A file of a few gigabytes has no business travelling through the control plane on its way to a lake the caller can write
to directly. Such a file is **reserved**, written straight to storage, and the reservation then **completed**.

```http
POST /api/v1/delivery/dropoffs/reserve
Authorization: Bearer <token with the operate scope>
Content-Type: application/json

{ "label": "the wellbore run",
  "files": [ { "name": "L-1001.dlis", "bytes": 8589934592 } ] }
```

The answer is the drop-off as it will be, with one write-only URL per file:

```json
{ "dropOffId": "0191e0a4-7a1c-7c3e-9b2e-5d0f3c8a1b42",
  "location": "abfss://lake@acct.dfs.core.windows.net/dropoff/0191e0a4-7a1c-7c3e-9b2e-5d0f3c8a1b42",
  "status": "uploading",
  "reservedUntilUtc": "2026-09-12T13:00:00Z",
  "uploads": [ { "name": "L-1001.dlis",
                 "location": "abfss://lake@acct.dfs.core.windows.net/dropoff/0191e0a4-.../L-1001.dlis",
                 "url": "https://acct.blob.core.windows.net/lake/dropoff/...?sv=...",
                 "expiresUtc": "2026-09-12T13:00:00Z" } ] }
```

Write each file to its URL, then say so:

```http
POST /api/v1/delivery/dropoffs/{dropOffId}/complete
Content-Type: application/json

{ "files": [ { "name": "L-1001.dlis", "sha256": "9f2c1b..." } ] }
```

- **The row exists before the first URL does.** A reservation nobody finishes is an abandoned upload in the listing, not
  files in the area that no row accounts for.
- **Each URL writes one file and does nothing else.** It cannot read, list or delete, it cannot reach a second file, and
  it stops working at `reservedUntilUtc` (`ControlPlane:DropOff:SignedUploadExpiryMinutes`, an hour by default). URLs
  carry their own credential, so they are used and never stored or logged.
- **Completion is decided by what storage holds**, not by what the caller says: every reserved file must be there at the
  size it was reserved at, and nothing else may be. A file that is missing, short, or unexpected fails the completion
  naming it, and the reservation stays open so the caller can finish and complete again. A drop-off a submission may
  point at is one that completed.
- **The hash is the caller's word.** The bytes never came past the control plane, so nothing here computed one: a hash
  given at completion is recorded with `hashSource: "client"`, and a file reported without one with `hashSource:
  "none"`. A flow that decides payload changes by content hash (`change.payloadDetect: contentHash`, the default) needs
  a hash, so a caller on this path supplies the one it computed while writing the file, or the flow watches the files'
  modified times instead.
- **The ceiling is its own**, because this path costs the control plane nothing per byte: 64 GB per file by default
  (`ControlPlane:DropOff:MaxSignedFileGigabytes`), against the 100 MB a streamed upload carries.
- **It needs a store that can issue a URL.** Only Azure Storage can, and the control plane's identity needs the
  **Storage Blob Delegator** role on the account on top of the role that lets it write; without either,
  `GET /api/v1/delivery/dropoff-area` answers `"signedUploads": false` and a reservation is refused saying so.
- **A browser needs CORS on the storage account**, because the write goes from the page to storage and not through the
  control plane: allow `PUT` from the GUI's origin, with the `x-ms-blob-type` and `Content-Type` headers. Without it the
  GUI's upload fails in the browser while the API path keeps working, since a server calling the URL is not subject to
  CORS at all.
- **Completing twice answers the same drop-off**, so a caller that lost the first answer retries safely.
- **In the GUI**, the Drop-off page takes this route on its own for any file past the streamed ceiling. A browser cannot
  hash a file of this size without reading it all into memory, so it asserts none and says so.

Which columns a flow reads and what each of them fills, which are the dataset key, which template version the mapping
fills, which parameters it declares, which payload its records point at (with whether a hash is required and the roots
allowed) and whether it takes records at all is answered by
`GET /api/v1/delivery/flows/{pipelineId}/source-contract` (scope `read`):

| Field | What it says |
| --- | --- |
| `template` | `{ kind, version, saved }`: the template version the flow's mapping fills. `saved: false` means a run cannot render the records until that version is saved on the Templates page. |
| `system`, `key`, `label` | The mapping's `dataset.system`, the dataset key's columns (bare names) and its `dataset.label`. |
| `columns` | The dataset row's columns, each `{ name, key, label, uses }`. Every use is `{ target, role, source, required, modifiers, findBy, appliesWhen }`: the template variable the entry fills (such as `osdu.data.FacilityName`), and a `role` of `value` (the column's value, modified, is what the entry writes), `findBy` (the column's value finds the cached record the entry writes from) or `appliesWhen` (the column decides whether the entry applies). |
| `datasets` | The child datasets, each `{ name, fills, columns }`: the lists it fills (`{ target, required }`) and its columns, described as above. |
| `parameters`, `lastModifiedColumn`, `fingerprintColumn` | The flow parameters a submission carries, and the column the flow versions rows by. |
| `payloadName`, `payloadHashRequired`, `payloadRoots` | The payload the flow streams, whether each record's files need a content hash, and the roots they may sit inside. |
| `acceptsRecords`, `recordsRefusal`, the ceilings | Whether the flow takes records inline, why not when it does not, and how much one submission may carry. |
| `mappingProblem` | Why the columns are unknown, or, while they are listed, that the pinned template version is not saved. |

## 5. The answers

| Status | When | Body |
| --- | --- | --- |
| `202 Accepted` | The records were stored and a run queued. `Location: /api/v1/runs/{runId}`. | `{ "runId", "pipelineId", "flowName", "status", "submissionId", "replayed": false }` |
| `200 OK` | The same request was accepted before under this `submissionId`: nothing new is queued. | The same body, with the run that request started and `"replayed": true`. |
| `400 Bad Request` | The request is malformed (`Invalid request`), a parameter does not resolve (`Invalid run parameters`), a record breaks section 4 (`Invalid records`, naming the record, the child dataset and the column), a record points at files the flow does not allow or leaves them out (`Invalid records`, naming the record and the roots), or the flow offers no manual submission (`Records not accepted by this flow`). | Problem details. |
| `404 Not Found` | No active delivery flow by that name or id. | Problem details. |
| `409 Conflict` | The flow name is ambiguous, or the `submissionId` was used before for a different request (naming what differs) or by a drop. | Problem details. |

`GET /api/v1/runs/{runId}` then reports the run's progress and outcome, with the counts of what it planned, delivered,
skipped, held and failed.

### Finding a submission again

A source that keeps its own records does not have to keep this system's ids as well. The `reference` it sent is on the
submission and on every page that shows one, and the listing narrows on it:

```http
GET /api/v1/delivery/flows/{pipelineId}/submissions?reference=L-1001.las
```

The match is a containment, so a fragment of a filename finds the submission whose reference embeds it. It reaches the
submissions the ledger registered, which is to say the ones a run has taken; the records as sent, with their reference,
are at `GET /api/v1/delivery/submissions/{id}/content` from the moment they were accepted.

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
it (with the payload each streams, and, on request, the flows that offer none and why). **Drop-off** next to it is the
pre-step: it uploads files into the drop-off area, lists what has been dropped off with who uploaded it and when, copies
a location to paste into a submission, and deletes a drop-off when it is no longer needed. The dialog builds a form from the
flow's source contract for one record (each field with what it fills, a Now button for the version column, and each
child dataset with the list it fills), with the template kind and version next to the mapping, or takes any number of
records as JSON in the shape of section 4, its template writing `datasets`. For a flow that streams files it also asks
where the record's files are, and for the hash when the flow needs one, showing the roots the flow allows. It offers the flow
parameters, the preview, `force`, an optional submission id and an optional reference, and opens the run it queued. A
submission of records has a **Records sent** tab on its page, and the submissions list shows each one under the name its
source gave it. The Drop-off page takes a file past the streamed ceiling straight to storage on its own, and marks such a
drop-off `direct`, saying for each file whether its hash was computed here, asserted by the uploader, or absent.

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
| `409 ... differs from it in the reference` | A repeat under one id renamed the work, or left the name off | Send the same reference the first request carried, or use a new id. |
| `400 reference is at most 200 characters` | The reference carries content rather than a name | Send the name; put the content in the records. |
| `400 Signed uploads unavailable` | The drop-off area is not on Azure Storage, or the control plane may not delegate | Upload through the API, or grant the control plane Storage Blob Delegator on the account. |
| `400 ... is 9 bytes, not the 14 reserved` | An upload to a signed URL stopped partway | Write the file again to the same URL, then complete again. |
| The run fails: "the drop was prepared for mapping ..." | The flow was promoted to another mapping between the request and the run | Send the records again under a new id. |
| The run fails: "records[0] and records[1] are the same record" | One record twice in one submission | Send each record once. |
| The run log warns that a column is not read by the mapping | The column is misspelt, or the mapping does not use it | Check the source contract's columns. |
| A record is skipped as `stale` | Its version column is older than the version delivered | Send a later moment. |
| Nothing is sent and the record is `unchanged` | The record renders to what OSDU already holds | Nothing to do. |
