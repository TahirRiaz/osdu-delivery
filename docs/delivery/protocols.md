# Delivery protocols

OSDU has at least four delivery shapes ([design.md](design.md) section 8). They are named protocols
implemented in code and parameterised by the flow, not an authorable step language.

| Protocol | Service | Pattern | Status |
| --- | --- | --- | --- |
| `osduRecord` | storage | One JSON document, upsert by client-supplied id, array endpoint. | Implemented |
| `osduWellLog` | wellbore DDMS | Record, then binary payload, optionally through a session. | Implemented |
| `osduFile` | file, dataset | Signed upload URL, upload, register metadata. | Declared, rejected at parse time |
| `osduManifest` | workflow | Assemble a manifest, trigger a DAG, poll. | Declared, rejected at parse time |

The core is protocol independent: identity, rendering, change detection, the ledger, idempotency and the
preflight gate never change. A protocol implements two operations:

```csharp
Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct);
Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct);
```

`DeliveryWork` says what to send (the document, whether metadata and/or payload changed, the payload source,
the last known version). `DeliveryOutcome` says what was sent and the version OSDU reported.

## `osduRecord`

- `{recordMethod} {endpoint}{recordPath}` with a one-element array body. Defaults: `PUT /api/storage/v2/records`.
- The response's `versionPath` (default `recordIdVersions[0]`, an `id:version` string) supplies the version.
- Verify: `GET {endpoint}{verifyPath}` (default `/api/storage/v2/records/{id}`), compare `version`.

## `osduWellLog`

- Metadata: `POST {endpoint}/ddms/v3/welllogs` with a one-element array (the wellbore DDMS shape). Override
  `recordPath` and `recordMethod` for a facade such as petrodb-api.
- `preserveDataKeys` (for example `Datasets`, `DDMSDatasets`, `ExtensionProperties`) are read from the
  existing record before an update and copied into the document's `data`, because OSDU owns them
  ([decisions/0004](decisions/0004-preserved-keys.md)).
- Payload, up to `sessionThresholdChunks` chunks: one `POST {dataPath}` per chunk with the chunk streamed as
  `application/x-parquet`.
- Payload, more chunks: `POST {sessionPath}` with `{ mode: overwrite, fromVersion, timeToLive }`, one
  `POST {sessionDataPath}` per chunk in order, then `PATCH {sessionCommitPath}` with `{ state: commit }`. Any
  failure abandons the session (best effort) and surfaces the error.
- Every chunk request is built from a factory that re-opens the blob, so the retry stack can resend a chunk
  without buffering it.

## Retry, hold, fail

| Outcome | Cause | Record status |
| --- | --- | --- |
| Delivered | 2xx on every call | `delivered`, pending state promoted to current, version recorded |
| Retry later | transport failure, 5xx exhausted within a call, 401, 408, 429 | `pending` with `NextAttemptUtc` (exponential in minutes) |
| Held | 400, 403, 404, 405, 409, 413, 415, 422, any status in `reliability.skipStatusCodes`, no payload chunks, a `RecordHeldException` | `held` (terminal until released) |
| Failed | the record-level retry budget (`reliability.retry.attempts`) is exhausted | `failed` (released like held) |

Inside one call the HTTP executor already retries 408, 425, 429, 500, 502, 503 and 504 with backoff and
honours `Retry-After`; the record-level backoff is the outer loop across worker passes.

## Adding a protocol

1. Add the enum value (already present for `osduFile` and `osduManifest`).
2. Implement `IDeliveryProtocol` in `src/SqlFlow.Delivery/Engine/Protocols`, reusing `OsduHttpClient` and
   `RecordWriter`.
3. Register it in `ProtocolFactory` and mark it implemented in `DeliveryProtocols.IsImplemented`.
4. Add defaults for its paths to `ProtocolOptions` handling and document them here.
5. Cover it with a `FakeHttpHandler` test like the existing ones.
