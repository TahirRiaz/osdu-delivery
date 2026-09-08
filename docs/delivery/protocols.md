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
Task<DeleteOutcome> DeleteAsync(string targetId, bool purge, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct);
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
- The response's `recordIdVersions` (`id:version` strings) supply each record's version; `skippedRecordIds`
  marks the records the service found unchanged. A single-record write also honours `versionPath`.
- A batch the service refuses as a whole (a 4xx) is retried record by record, so one bad document holds
  itself and not its neighbours.
- Verify: `GET {endpoint}{verifyPath}` (default `/api/storage/v2/records/{id}`), compare `version`.
- Delete: `POST {id}:delete` is the logical, revertible delete; `DELETE {id}` purges every version.

## `osduWellLog`

- Metadata: `POST {endpoint}/ddms/v3/welllogs` with a one-element array (the wellbore DDMS shape). Override
  `recordPath` and `recordMethod` for a facade such as petrodb-api. Step `metadata` returns the version.
- `preserveDataKeys` (for example `Datasets`, `DDMSDatasets`, `ExtensionProperties`) are read from the
  existing record before an update and copied into the document's `data`, because OSDU owns them
  ([decisions/0004](decisions/0004-preserved-keys.md)).
- Payload, up to `sessionThresholdChunks` chunks: one `POST {dataPath}` per chunk with the chunk streamed as
  `payloadContentType` with its length. Step `payload` returns the chunk count.
- Payload, more chunks: `POST {sessionPath}` with `{ mode: overwrite, fromVersion, timeToLive }`, one
  `POST {sessionDataPath}` per chunk in order, then `PATCH {sessionCommitPath}` with `{ state: commit }`. Any
  failure abandons the session (best effort) and surfaces the error. The session id is returned.
- A retry after a payload failure resumes past the metadata step it already completed.
- Every chunk request is built from a factory that re-opens the blob, so the retry stack can resend a chunk
  without buffering it.

## `osduFile`

The files go first, then the record that references them (openapi file v2, storage v2).

1. Per payload chunk: `GET {uploadUrlPath}` (default `/api/file/v2/files/uploadURL`, `expiryTime` from
   `uploadUrlExpiry`) hands out `Location.SignedURL` and `Location.FileSource`; the chunk streams to the signed
   URL with `PUT`, its length, `payloadContentType`, and only the `uploadHeaders` the flow declares (an Azure
   landing zone needs `x-ms-blob-type: BlockBlob`). The signed URL carries its own authorisation and is never
   logged or stored. Step `upload-{i}` returns `fileSource`, `fileId`, `name`, `size`.
2. Per uploaded file: `POST {fileMetadataPath}` (default `/api/file/v2/files/metadata`) registers the dataset
   record: `datasetKind` (default `osdu:wks:dataset--File.Generic:1.0.0`), the record's own `acl` and `legal`
   copied, and `data.DatasetProperties.FileSourceInfo` with the file source, name and size. Step
   `register-{i}` returns `datasetId`.
3. The record, with `data.{datasetsProperty}` (default `Datasets`) pointing at the registered ids (any ids the
   mapping rendered are kept), goes through the storage array endpoint exactly as `osduRecord`, batched with
   the rest of the batch. Step `records`.

A metadata-only change rewrites the record with the dataset ids of its earlier delivery (from the target
state); a payload change uploads and registers new datasets and rewrites the record to point at them. An
empty file, or one above `reliability.maxRequestBodyBytes`, holds the record before anything is sent. Purge
deletes the dataset records and their files (`DELETE {fileDeletePath}`, default
`/api/file/v2/files/{id}/metadata`) with the record; a logical delete leaves them, so the record can be
restored whole. Verify and read back go to storage.

## `osduManifest`

OSDU's own bulk path (openapi file v2, workflow v1, storage v2): the batch's files, one manifest, one
workflow run.

1. The batch's files are uploaded as in `osduFile` (steps `upload-{i}`); no registration. Each file becomes a
   dataset entry of the manifest with an id derived from its record's id
   (`dev:work-product-component--WellLog:abc` with the default dataset kind gives
   `dev:dataset--File.Generic:abc-0`), so a redelivery overwrites its datasets instead of leaking new ones,
   and the record's dataset list is complete before the workflow registers them.
2. One manifest (`manifestKind`, default `osdu:wks:Manifest:1.0.0`) carries every record of the batch in the
   section its kind names (`ReferenceData`, `MasterData`, `Data.WorkProduct`, `Data.WorkProductComponents`,
   `Data.Datasets`; `manifestSection` overrides) and the dataset entries. `POST {workflowRunPath}` (default
   `/api/workflow/v1/workflow/{workflow}/workflowRun`, `workflowName` default `Osdu_ingest`) with
   `{ runId, executionContext: { Payload: { AppKey, data-partition-id, ...workflowPayload }, manifest } }`.
   The run id is chosen here, so a request the service accepted before a retry resent it answers 409 and is
   polled, not run twice. Step `manifest` is reported on every record of the batch, with the run id, before
   polling starts.
3. `GET {workflowStatusPath}` every `workflowPollSeconds` until the run is `SUCCESS`, `PARTIAL_SUCCESS` or
   `FAILED`, or `workflowTimeoutMinutes` pass. A timeout fails the try; the next try resumes polling the same
   run. A failed run fails the batch; the next try triggers a new run. Step `workflow` returns the status and
   timestamps.
4. The records are read back from storage (`POST {recordQueryPath}`, default
   `/api/storage/v2/query/records`, a hundred ids per request, projected to the dataset list) so each settles
   on its own evidence: present with a version, delivered; absent, failed with the run named, and re-submitted
   in a new run on the next try. Step `records` returns the record id and version.

Verify, read back and delete go to storage, and purge deletes the datasets and their files through the file
service, as for `osduFile`.

## Retry, hold, fail

| Outcome | Cause | Record status |
| --- | --- | --- |
| Delivered | 2xx on every call | `delivered`, pending state promoted to current, version and returned values recorded |
| Retry later | transport failure, 5xx exhausted within a call, 401, 408, 429, a workflow run that failed or timed out, a record a finished workflow run did not write | `pending` with `NextAttemptUtc` (exponential in minutes); completed steps kept for the resume |
| Held | 400, 403, 404, 405, 409, 413, 415, 422, any status in `reliability.skipStatusCodes`, no payload chunks, an empty file, a `RecordHeldException` | `held` (terminal until released) |
| Failed | the record-level retry budget (`reliability.retry.attempts`) is exhausted | `failed` (released like held) |

Inside one call the HTTP executor already retries 408, 425, 429, 500, 502, 503 and 504 with backoff and
honours `Retry-After`; the record-level backoff is the outer loop across worker passes. HTTP errors name the
request URL without its query string, so a signed URL's credential never reaches an error message.

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
