# Delivery protocols

OSDU has at least four delivery shapes ([design.md](design.md) section 8). They are named protocols
implemented in code and parameterised by the flow, not an authorable step language. All four are implemented.

| Protocol | Services | Pattern | Batched |
| --- | --- | --- | --- |
| `osduRecord` | storage | One JSON document, upsert by client-supplied id, array endpoint. | up to `batchSize` records per request |
| `osduWellLog` | wellbore DDMS | Record, then binary payload, optionally through a session. | one record per request |
| `osduFile` | file, storage | Signed upload URL per file, streamed upload, dataset registration, then the record with its dataset list. | the record write, up to `batchSize` |
| `osduManifest` | file, workflow, storage | Uploads, one manifest per batch handed to the ingestion workflow, the run polled, the records read back. | one workflow run per batch of up to `batchSize` |

The core is protocol independent: identity, rendering, change detection, the ledger, idempotency and the
preflight gate never change. A protocol implements the delivery, and the read-back, verify, probe and delete
operations the interventions use:

```csharp
int MaxBatch { get; }
Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct);
Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct);
Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct);
Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct);
Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct);
Task<IReadOnlyList<RemovalResult>> DeleteBatchAsync(IReadOnlyList<RecordRemoval> removals, RemovalScope scope, CancellationToken ct);
Task<ProbeOutcome> ProbeAsync(CancellationToken ct);
```

`DeliveryWork` says what to send (the document, whether metadata and/or payload changed, the payload source,
the last known version, the steps an earlier try completed, the values the target returned before).
`DeliveryOutcome` says what was sent, the version the target reported, every step taken with its timing and
status, and every value the target returned.

## Steps and returned values

Every protocol reports each step that changes the target (a record write, an upload, a registration, a
workflow trigger) through `DeliveryWork.ReportStepAsync` as soon as it completes, with what the target
returned. The worker persists the step on the record before the protocol moves on, so a retry after a crash or
a later failure resumes after the last step that succeeded: the file uploaded by the previous try is
registered, not uploaded again; the workflow run triggered by the previous try is polled, not triggered again.
The attempt carries the full step list (`delivery.Attempt.ResultJson`: each step's name, timing, status,
returned values and whether it was resumed) and the record's target state (`delivery.Record.TargetStateJson`)
merges the returned values of every delivery: record id and version, dataset ids, file sources, a session id,
a workflow run id. See [design.md](design.md) section 16.3.

## `osduRecord`

- `{recordMethod} {endpoint}{recordPath}` with an array of up to `protocolOptions.batchSize` records (default
  100, at most 500). Defaults: `PUT /api/storage/v2/records`.
- The write carries `skipdupes=true` only when `protocolOptions.skipDuplicates` is true; it is off by default.
  The spec says only "Skip duplicates when updating records with the same value", not which parts of a record
  the service compares. Deliveries are already gated on the hash of the whole rendered document, so a write that
  reaches storage is a document that changed; if the service judged sameness by `data` alone, a change to only
  `acl`, `legal` or `tags` would be skipped while the ledger recorded it as delivered. Opt in only once that is
  confirmed for the target. Opted in, a record the service names under `skippedRecordIds` keeps its version and
  settles on the version the ledger already held.
- The response's `recordIdVersions` (`id:version` strings) supply each record's version; `skippedRecordIds`
  marks the records the service found unchanged. A single-record write also honours `versionPath`.
- A batch the service refuses as a whole (a 4xx) is retried record by record, so one bad document holds
  itself and not its neighbours.
- Verify, one record: `GET {endpoint}{verifyPath}` (default `/api/storage/v2/records/{id}`), compare `version`.
- Verify, a pass: `POST {verifyBatchPath}` (default `/api/storage/v2/query/records`) with up to 100 ids and
  the attributes projected down, so a drift pass over a large estate costs a handful of requests rather than
  one per record. The records it returns carry their observed version, and the rest are missing, whether or not
  the response names them under `invalidRecords`, which is how storage answers for a record it does not hold.
  `osduFile` and `osduManifest` verify through the same read, because their records live in storage too.
- Remove: `POST {id}:delete` stops the record resolving and is revertible in OSDU; `DELETE {id}/versions`
  purges the earlier versions and leaves the latest live; `DELETE {id}` purges the record and every version.
  A set of records at the reversible scope goes through `POST /records/delete` (up to 500 ids per request);
  a 207, or a 400 or 405, falls back to one request per record so each reports its own outcome. Any other
  refusal (401, 403, an exhausted 5xx) is not a verdict on the records, so the chunk is not resent one id at a
  time: every record in it carries the one failure that happened. The paths are `deletePath`,
  `purgeVersionsPath`, `purgePath` and `bulkDeletePath`.

## `osduWellLog`

- Metadata: `POST {endpoint}/ddms/v3/welllogs` with a one-element array (the wellbore DDMS shape). Override
  `recordPath` and `recordMethod` for a facade such as petrodb-api. Step `metadata` returns the version.
- The endpoint of a well log flow is, by default, the wellbore DDMS itself (or a facade serving its paths): the
  DDMS paths (`/ddms/v3/...`, `/about`) carry no `/api/<service>/` prefix, unlike every other protocol, whose
  endpoint is the OSDU platform root. A flow whose endpoint is the platform root declares
  `protocolOptions.ddmsRoot: /api/os-wellbore-ddms` (the platform's ingress route for the DDMS, and the base the
  OSDU C# client uses); every DDMS default path is then taken under it, and the storage-owned calls resolve under
  the endpoint as they do for the other protocols. A path option the flow sets explicitly is used as written
  either way. `ddmsRoot` must be a path starting with `/` and is refused on any other protocol. The distinction is
  also why `sqlflow snapshot` takes `--endpoint` to capture schemas and references from the platform.
- Remove: `DELETE {deletePath}` is a logical deletion the DDMS can revert; `?purge=true` makes it physical. The
  DDMS has no operation on a record's versions (its only versions route is a GET listing), and versions belong to
  the storage service for every kind of record, so the history scope goes to storage. With `ddmsRoot` declared the
  endpoint is the platform root and the storage default (`/api/storage/v2/records/{id}/versions`) resolves under
  it. Without it, storage is a different service from this flow's endpoint, so the flow says where it is by
  declaring `purgeVersionsPath`, normally as a whole URL (`https://<host>/api/storage/v2/records/{id}/versions`). Any protocol path option may be written as an
  absolute URL, and absolute URLs go through the same SSRF guard and `reliability.urlAllowlist` as every other
  request. Without it the scope is refused rather than sent somewhere nobody chose, and the GUI does not offer it.
- `preserveDataKeys` (for example `Datasets`, `DDMSDatasets`, `ExtensionProperties`) are read from the
  existing record before an update and copied into the document's `data`, because OSDU owns them
  ([decisions/0004](decisions/0004-preserved-keys.md)).
- Payload, one chunk: `POST {dataPath}` with the chunk streamed as `payloadContentType` with its length. Only
  ever one chunk goes this way: that request carries "the entire bulk which will replace as latest version any
  previous bulk", so several chunks sent to it would overwrite each other. Step `payload` returns the chunk count.
- Payload, more than one chunk (or `sessionThresholdChunks: 0`, which sessions even a single chunk):
  `POST {sessionPath}` with `{ mode: overwrite, fromVersion, timeToLive }`, one `POST {sessionDataPath}` per chunk
  in order, then `PATCH {sessionCommitPath}` with `{ state: commit }`, which is what aggregates the chunks into one
  new version. Any failure abandons the session (best effort) and surfaces the error. The session id is returned.
- The commit is a PATCH and is never resent blind, so its outcome can be unclear: the connection went, a gateway
  answered 5xx after the service had acted, or an intermediary resent it and the copy met a session that is no
  longer open (409 or 412). The session's own state is read (`GET {sessionCommitPath}`) rather than guessed:
  `committed` or `committing` is the commit that worked; anything else fails saying the payload did not land.
- Session create, every session chunk and the commit are sent once. A chunk sent twice into a session lands twice
  in the committed bulk, so a chunk whose outcome is unclear fails the session, which is abandoned, and the next
  try of the record opens a new one.
- `sessionThresholdChunks` is 1 (the default) or 0. A higher value is refused when the flow is read, because it
  would have asked for chunks to overwrite each other.
- A retry after a payload failure resumes past the metadata step it already completed.
- Every chunk request is built from a factory that re-opens the blob, so the retry stack can resend a chunk
  without buffering it.

### The bulk ceilings

Before the first request, every chunk is checked against two ceilings, so an oversized chunk holds the record
instead of failing after the metadata write has already landed.

| Ceiling | Where it comes from | Declared as |
| --- | --- | --- |
| Request body bytes | The estate: Kestrel, the ingress, the API gateway. Raise it where it is configured. | `reliability.maxRequestBodyBytes` (0 = not declared) |
| 10,000,000 values per chunk (rows times columns) | The wellbore DDMS itself. Cannot be raised. | `target.protocolOptions.maxChunkValues` |
| 3,000 columns per chunk | The wellbore DDMS itself. 500 through OSDU M25, 3000 from M26. | `target.protocolOptions.maxChunkColumns` |

The two DDMS numbers are the service's own: the OpenAPI description of `POST /ddms/v3/welllogs/{record_id}/data`
says bulk over "10 millions values or 3000 columns" must go through the chunking (session) APIs, and the service
carries them as `WRITE_MAX_TOTAL_VALUES_COUNT = 10_000_000` ("restrict chunk to ~100MB") and
`WRITE_MAX_COLUMNS_COUNT = 3_000` in `app/bulk_persistence/constants.py`. They bound the frame the service
materialises, not the request body, so a chunk can be small enough to send and still be too large to accept, and
the service does not reliably reject it: on the current upstream the write-side value ceiling is unreferenced and
the column validator is not wired into a route, so exceeding one shows up as a slow write or an out-of-memory
worker. `SqlFlow.Delivery.Model.WellboreDdmsBulkLimits` holds the numbers and their provenance.

The shape is read from each chunk's parquet footer (the schema and the row group headers), never from its
contents, so the cost is one footer read per chunk and the memory is the schema. The check runs only when the
payload content type is parquet and at least one ceiling is above zero; set both to 0 to opt out. A chunk
declared as parquet whose footer will not read holds the record, because the service would refuse it too.

## `osduFile`

The files go first, then the record that references them (openapi file v2, storage v2).

1. Per payload chunk: `GET {uploadUrlPath}` (default `/api/file/v2/files/uploadURL`, `expiryTime` from
   `uploadUrlExpiry`) hands out `Location.SignedURL` and `Location.FileSource`; the chunk streams to the signed
   URL with `PUT`, its length, `payloadContentType`, the `uploadHeaders` the flow declares, plus
   `x-ms-blob-type: BlockBlob` when the URL is Azure Blob Storage (any `*.blob.core.*` host) and the flow names no
   blob type, because Azure refuses a blob PUT without one; other landing zones get only what the flow declares.
   The signed URL carries its own authorisation and is never
   logged or stored. Step `upload-{i}` returns `fileSource`, `fileId`, `name`, `size`.
2. Per uploaded file: `POST {fileMetadataPath}` (default `/api/file/v2/files/metadata`) registers the dataset
   record: `datasetKind` (default `osdu:wks:dataset--File.Generic:1.0.0`), the record's own `acl` and `legal`
   copied, and `data.DatasetProperties.FileSourceInfo` with the file source, name and size. Step
   `register-{i}` returns `datasetId`.
3. The record, with `data.{datasetsProperty}` (default `Datasets`) referencing the registered datasets as `{id}:`
   (the form the work product component schemas require; any references the mapping rendered are kept), goes through the storage array endpoint exactly as `osduRecord`, batched with
   the rest of the batch. Step `records`.

A metadata-only change rewrites the record with the dataset ids of its earlier delivery (from the target
state); a payload change uploads and registers new datasets and rewrites the record to point at them. An
empty file, or one above `reliability.maxRequestBodyBytes`, holds the record before anything is sent. Purge
deletes the dataset records and their files (`DELETE {fileDeletePath}`, default
`/api/file/v2/files/{id}/metadata`) with the record; the reversible removal leaves them, so the record can be
restored whole, and a history purge touches only the record's own earlier versions. Verify and read back go to storage.

## `osduManifest`

OSDU's own bulk path (openapi file v2, workflow v1, storage v2): the batch's files, one manifest, one
workflow run.

1. The batch's files are uploaded as in `osduFile` (steps `upload-{i}`); no registration. Each file becomes a
   dataset entry of the manifest with an id derived from its record's id
   (`dev:work-product-component--WellLog:abc` with the default dataset kind gives
   `dev:dataset--File.Generic:abc-0`), so a redelivery overwrites its datasets instead of leaking new ones,
   and the record's dataset list is complete before the workflow registers them. The list references each
   dataset as `{id}:`: manifest ingestion validates the schemas' reference pattern and drops a record that
   breaks it, while still creating its datasets.
2. One manifest (`manifestKind`, default `osdu:wks:Manifest:1.0.0`) carries every record of the batch in the
   section its kind names (`ReferenceData`, `MasterData`, `Data.WorkProduct`, `Data.WorkProductComponents`,
   `Data.Datasets`; `manifestSection` overrides) and the dataset entries. `POST {workflowRunPath}` (default
   `/api/workflow/v1/workflow/{workflow}/workflowRun`, `workflowName` default `Osdu_ingest`) with
   `{ runId, executionContext: { Payload: { AppKey, data-partition-id, ...workflowPayload }, manifest } }`.
   The run id is chosen here, so a request the service accepted before a retry resent it answers 409 and is
   polled, not run twice. Step `manifest` is reported on every record of the batch, with the run id, before
   polling starts.
3. `GET {workflowStatusPath}` every `workflowPollSeconds` until the run reaches a terminal status, or
   `workflowTimeoutMinutes` pass. The terminal statuses are `SUCCESS`, `PARTIAL_SUCCESS`, `FINISHED` and
   `FAILED`, compared upper case because the service reports them in both cases (openapi workflow v1:
   `WorkflowRunResponse` is upper, `WorkflowRun` is lower). A timeout fails the try; the next try resumes
   polling the same run. A failed run fails the batch; the next try triggers a new run. A status the service
   has never been known to report is named in the error rather than polled forever. Step `workflow` returns
   the status and timestamps.
4. The records are read back from storage (`POST {recordQueryPath}`, default
   `/api/storage/v2/query/records`, a hundred ids per request, projected to the dataset list) so each settles
   on its own evidence: present with a version, delivered; named under `retryRecords`, failed saying so; not
   returned, failed with the run named and re-submitted in a new run on the next try. Storage names a record it
   does not hold under `invalidRecords` (a live M26 service does), so a listed id is a record the workflow did
   not write, not a verdict on the id. Step `records` returns
   the record id and version.

Verify, read back and removal go to storage, and a purge of everything deletes the datasets and their files through the file
service, as for `osduFile`.

## Before a run: legal tags

Every record a mapping renders carries the same legal tags, and storage refuses a record whose tag is unknown or
expired, on every record that carries it. So a deliver or intake run asks the legal service first
(`POST /api/legal/v1/legaltags:validate`, at most 25 names per request) and, when it refuses any tag, the run fails
before anything is planned or sent, naming each tag and the reason the service gives (expired, not found). Plan runs
do not ask; they send nothing.

- Where it asks: under the endpoint, for every protocol whose endpoint is the platform root, which includes a well log
  flow that declares `ddmsRoot`. A well log flow whose endpoint is the DDMS itself does not reach the legal service by
  a path; it asks only when it names `protocolOptions.legalValidatePath` (normally an absolute URL), and otherwise the
  run logs that the tags were not checked. Not checked is never read as valid.
- `protocolOptions.validateLegalTags: false` turns the check off; storage then refuses a bad tag record by record.
- A verdict on a tag is trusted for ten minutes, so the batches of one run do not each ask again. The service answers
  404 without naming which of several names it does not know, so such a request is asked again name by name; a 404
  that is not the legal service's own error (a gateway, a facade) fails the run as the legal service being
  unreachable, not as every tag being invalid.

## Retry, hold, fail

| Outcome | Cause | Record status |
| --- | --- | --- |
| Delivered | 2xx on every call | `delivered`, pending state promoted to current, version and returned values recorded |
| Retry later | transport failure, 5xx exhausted within a call, 401, 408, 429, a workflow run that failed or timed out, a record a finished workflow run did not write | `pending` with `NextAttemptUtc` (exponential in minutes); completed steps kept for the resume |
| Held | 400, 403, 404, 405, 409, 413, 415, 422, any status in `reliability.skipStatusCodes`, no payload chunks, an empty file, a `RecordHeldException` | `held` (terminal until released) |
| Failed | the record-level retry budget (`reliability.retry.attempts`) is exhausted | `failed` (released like held) |

Inside one call the HTTP executor repeats a request only when repeating it is safe, the line the OSDU C# client
draws in its `ReadRetryHandler`:

- Safe by method: GET, HEAD, PUT, DELETE (RFC 9110). Safe by the service's own semantics, declared by the
  protocol that makes the call: record writes with client-supplied ids, `POST /records/{id}:delete` and the bulk
  soft delete (a repeat finds the records already gone), reads by id (`POST /query/records`), searches, the
  replace-the-whole-bulk `POST {dataPath}`, the workflow trigger (it names its own run id, so a resend answers
  409), and token requests.
- Never repeated: session create, session chunks, session commit, and file registration
  (`POST /files/metadata`, which mints a dataset record per accepted call). Not after a status, and not after a
  transport failure either, where the service may have acted before the connection went.
- A safe request is repeated on 408, 425, 429, 503 and 504, and on a transport failure. 500 and 502 are not
  replayed inline: the services answer them for deterministic failures as often as passing ones. They fall to
  the record-level backoff, as does anything not repeated inline.
- `Retry-After` is never shortened. A wait within `reliability.retry.maxDelayMs` is the floor of the backoff; a
  longer one is not sat through inline, and travels with the failure so the record's next attempt is no sooner
  than the service asked.
- Every request carries a `Content-Type`, bodiless ones included (an empty `application/json` body), because
  storage answers a request without one with 415 even when the operation takes no body.

The record-level backoff is the outer loop across worker passes. HTTP errors name the request URL without its
query string, so a signed URL's credential never reaches an error message. An error body is read for what the
service said rather than kept as raw JSON: AppError's `message` and `reason` from the Java services, a Spring
problem's `title` and `detail` (the 415 storage sends for a missing `Content-Type`), or the wellbore DDMS `detail`,
with the fields a validation error names. Anything else is kept as a bounded, single-line preview.

## Adding a protocol

1. Add the enum value to `DeliveryProtocol` and, when the protocol streams a payload, to
   `DeliveryProtocols.CarriesPayload`.
2. Implement `IDeliveryProtocol` in `src/SqlFlow.Delivery/Engine/Protocols`, reusing `OsduHttpClient`,
   `RecordWriter` and `FileUploads`. Report every step that changes the target through
   `DeliveryWork.ReportStepAsync`, skip the steps `DeliveryWork.Completed` says an earlier try finished, and
   put every value the target returned in the outcome.
3. Register it in `ProtocolFactory`.
4. Add defaults for its paths to `ProtocolOptions` and document them in [documents.md](documents.md) and here.
5. Cover it with a `FakeHttpHandler` test like the existing ones, including the resume of a completed step.
