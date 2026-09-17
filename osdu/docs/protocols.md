# Delivery protocols

OSDU takes data in a handful of shapes ([design.md](design.md) section 8,
[../../docs/interfaces-design.md](../../docs/interfaces-design.md) section 5). Each is a named protocol implemented in
code and parameterised by the flow, not an authorable step language.

| Protocol | Services | Pattern | Batched |
| --- | --- | --- | --- |
| `osduRecord` | storage | One JSON document, upsert by client-supplied id, array endpoint. | up to `batchSize` records per request |
| `osduWellLog` | the DDMS serving each record's entity type (the Wellbore DDMS's nine collections by default), by the call pattern of its shape: the Wellbore DDMS v3, the Well Delivery DDMS, RAFS, the Production DDMS historian | Record, then, on a collection that keeps bulk data, its bulk data (a Wellbore DDMS session, RAFS's content tables, or the historian's points). | one record per request |
| `osduFile` | file, storage | Signed upload URL per file, streamed upload, dataset registration, then the record with its dataset list. | the record write, up to `batchSize` |
| `osduDataset` | dataset, storage | Staging location per record, upload the way its provider takes it, registration under the record's own id (or a dataset the record refers to), retrieval checked. | up to 20 registrations per request |
| `osduManifest` | file, dataset, workflow, search, storage | Uploads, one manifest per batch handed to the ingestion workflow, inline or by reference, the run polled, the records read back. | one workflow run per batch of up to `batchSize` |
| `osduFileAndDdms` | file, the DDMS | The record's files as `osduFile` registers them, the record through its DDMS naming them, then its bulk data; each part only when it moved. | one record per request |
| `osduManifestAndDdms` | file, dataset, workflow, search, storage, the DDMS | The batch through the manifest, then each record's bulk data through its DDMS. | as `osduManifest` |
| `osduWorkflow` | dataset, workflow, search, storage; Airflow's REST API when the flow names it | The record written, its inputs registered, up to four workflow runs in order, what they wrote found and read back. | one record per run |

A flow in the single form names its route with `target.protocol`, as a route type or as the protocol it maps onto.
An interface of a source is given one by its route, which follows from what the interface declares
([documents.md](documents.md#routes)): `storage` is `osduRecord`, `file` is `osduFile`, `dataset` is `osduDataset`,
`manifest` is `osduManifest`, `ddms` is `osduWellLog` (whose payload is the interface's `bulk`), `fileAndDdms` is
`osduFileAndDdms`, `manifestAndDdms` is `osduManifestAndDdms`, and `workflow` is `osduWorkflow`.

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
The attempt carries the full step list (`osdu.Attempt.ResultJson`: each step's name, timing, status,
returned values and whether it was resumed) and the record's target state (`osdu.Record.TargetStateJson`)
merges the returned values of every delivery: record id and version, dataset ids, file sources, a session id,
a workflow run id. See [design.md](design.md) section 16.3.

## Payload parts

`osduFileAndDdms`, `osduManifestAndDdms` and `osduWorkflow` send a record's payload in parts: its files, its bulk data,
a workflow's inputs, and the workflow run. The planner resolves each part's folder and content hash from the record's
row as it resolves a single payload's, and the ledger keeps the parts in the columns a payload has always had
([ledger.md](ledger.md#payloads-in-parts)): the payload hash is the hash of the parts' hashes, and the pending payload
lists each part with its folder and hash. `DeliveryWork.Parts` carries them to the protocol, which sends a part when its
hash differs from the one the record's target state keeps for it (`payload.<set>`) or when the work forces it
(`DeliveryWork.ForcedParts`, from a redelivery that names the part or a record that never delivered its payload), and
returns `payload.<set>` with the hash of each part it sent. An optional workflow input a row names no folder for is a
part without files.

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

## `osduWellLog`: the ddms route

A record goes to the DDMS serving its entity type, and by the call pattern of that DDMS's shape: `wellboreDdmsV3`
(described first, below), [`wellDeliveryV1`](#the-well-delivery-shape), [`rafsV2`](#the-rafs-shape) and
[`productionTimeSeriesV1`](#the-production-historian-shape). The Wellbore
DDMS shape's calls, rules and deletes follow its pinned contract and its source at the same commit
([../specs/wellbore-ddms/INTEGRATION.md](../specs/wellbore-ddms/INTEGRATION.md)).

- Where a record goes: the collection of the DDMS serving its entity type, which the record id names
  ([documents.md](documents.md#the-ddmss-a-flow-delivers-to)): a DDMS under `target.ddms`, then the Wellbore DDMS. The
  Wellbore DDMS has four collections that keep bulk data beside their records (`welllogs`, `wellboretrajectories`,
  `ppfgdataset`, `wellpressuretestrawmeasurement`) and five that hold records alone (`wells`, `wellbores`,
  `wellboremarkersets`, `wellboreintervalsets`, `welllogacquisition`). A kind no DDMS the flow reaches serves, and a
  bulk part whose records go to a record collection, are refused by the run's preflight and by `sqlflow check`, and a
  record the protocol cannot route is held naming why.
- Metadata: `POST {root}/ddms/v3/{collection}` with a one-element array (the Wellbore DDMS v3 shape). Override
  `recordPath` and `recordMethod` for a facade such as petrodb-api; the paths a flow leaves out come from the collection
  of its records' entity type. Step `metadata` returns the version.
- The endpoint of a ddms flow in the single form is, by default, the Wellbore DDMS itself (or a facade serving its
  paths): the DDMS paths (`/ddms/v3/...`, `/about`) carry no `/api/<service>/` prefix, unlike every other protocol,
  whose endpoint is the OSDU platform root. A flow whose endpoint is the platform root declares
  `protocolOptions.ddmsRoot: /api/os-wellbore-ddms` (the platform's ingress route for the DDMS, and the base the OSDU C#
  client uses), or a DDMS with its root under `target.ddms`; every DDMS default path is then taken under that root, and
  the storage-owned calls resolve under the endpoint as they do for the other protocols. A source with interfaces always
  has the platform root as its endpoint. A path option the flow sets explicitly is used as written either way.
  `ddmsRoot` must be a path starting with `/` and is refused on any other protocol. A cache flow declares its own
  `source.endpoint`, the platform root its searches go to, so it never depends on a ddms flow's endpoint.
- The record's rules: before anything is sent, the record is checked against the rules the Wellbore DDMS applies to its
  kind (the service's `app/consistency` modules), and a record that breaks one is held naming the rule. A WellLog's
  CurveIDs are unique and its ReferenceCurveID is one of them; a WellboreTrajectory's station property names are
  unique; a PPFGDataset names its ContextTypeID and ReferenceWellTrajectoryID, keeps unique CurveIDs and a
  PrimaryReferenceCurveID among them; a WellPressureTestRawMeasurement keeps unique CurveIDs. With bulk data, every
  curve of a WellLog, PPFGDataset or WellPressureTestRawMeasurement carries a CurveID, which the service matches
  columns by. A value counts as given the way the service's Python reads it: not null, not empty, not zero, not false.
- The bulk data's columns, read from the chunks' parquet footers and checked together: every column of a WellLog,
  PPFGDataset or WellPressureTestRawMeasurement is a `data.Curves[].CurveID` of the record, a column labelled
  `NAME[...]` being one column of the array curve `NAME`, and a WellLog's or WellPressureTestRawMeasurement's curve has
  as many columns as its `NumberOfColumns` (1 when not given); every column of a WellboreTrajectory is a
  `data.AvailableTrajectoryStationProperties[].Name`. What needs the rows (a monotonic reference, sampling bounds that
  match its first and last values) is the service's to check: the chunks are never parsed.
- The bulk link: the DDMS writes `data.ExtensionProperties.wdms.bulkURI` on every bulk write and session commit, and
  refuses a record write whose bulkURI differs from the one the latest version holds, a write that leaves it out
  included. An update of a record in a bulk collection therefore reads the version the DDMS holds first, and carries its
  bulkURI and the `urn://wdms-1/uuid:` entries the DDMS appended to `DDMSDatasets`; the rest of `ExtensionProperties`
  and `DDMSDatasets` is what the mapping renders. A bulkURI the mapping renders is replaced by the DDMS's, with a
  warning, and a create sends none. When the ledger knew no version of the record and the DDMS refuses the write, the
  record is read once and the write sent again if the DDMS holds a link after all (a delivery whose outcome was lost);
  any other refusal stands. A record collection keeps no link and is written without a read.
- Remove: `DELETE {root}/ddms/v3/{collection}/{id}` is a logical deletion the DDMS can revert. On a bulk collection
  `?purge=true` makes it physical: the DDMS purges the record in storage and deletes its bulk data without waiting. A
  record collection's DELETE is logical only, so the everything scope goes to the storage service's purge
  (`purgePath`, or `/api/storage/v2/records/{id}` under a platform-root endpoint), which is what the DDMS itself calls
  for a bulk record. The DDMS has no operation on a record's versions (its only versions route is a GET listing), and
  versions belong to the storage service for every kind of record, so the history scope goes to storage's
  `/api/storage/v2/records/{id}/versions`. Under a platform-root endpoint the storage defaults resolve; otherwise
  storage is a different service from this flow's endpoint, so the flow says where it is by declaring `purgePath` and
  `purgeVersionsPath`, normally as whole URLs. Any protocol path option may be written as an absolute URL, and absolute
  URLs go through the same SSRF guard and `reliability.urlAllowlist` as every other request. Without them those scopes
  are refused rather than sent somewhere nobody chose, and the GUI does not offer them.
- `preserveDataKeys` (for example `Datasets`, `DDMSDatasets`, `ExtensionProperties`) are read from the
  existing record before an update and copied into the document's `data`, because OSDU owns them
  ([decisions/0004](decisions/0004-preserved-keys.md)). The read is the same one the bulk link takes.
- Probe: `GET {root}/about` of every DDMS the flow reaches, or the flow's `probePath`; the target is reachable when
  every one of them answers.
- Discovery: a DDMS `target.ddms` names with `register` is read from the Register service
  (`GET /api/register/v1/ddms/{id}`, [../specs/core/INTEGRATION.md](../specs/core/INTEGRATION.md) section 2.7) when the
  protocol is built, once per run, and the run's route check sees what it registered. A registration names one server
  per interface and one retrieval operation (`x-ddms-retrieve-entity`); a retrieval path that is not a collection of the
  declared shape, a registration naming several servers for a DDMS without a declared root, and a missing registration
  (404) fail the run naming the registration. The lookup by type (`GET /ddms?type=`) is not used: its `type` pattern
  (`^[A-Za-z0-9]{1,50}`) cannot carry an entity type with its group.
- Payload, one chunk: `POST {dataPath}` with the chunk streamed as `payloadContentType` with its length. Only
  ever one chunk goes this way: that request carries "the entire bulk which will replace as latest version any
  previous bulk", so several chunks sent to it would overwrite each other. Step `payload` returns the chunk count.
- Payload, more than one chunk (or `sessionThresholdChunks: 0`, which sessions even a single chunk):
  `POST {sessionPath}` with `{ mode: overwrite, fromVersion, timeToLive }`, one `POST {sessionDataPath}` per chunk
  in order, then `PATCH {sessionCommitPath}` with `{ state: commit }`, which is what aggregates the chunks into one
  new version. Any failure abandons the session (best effort) and surfaces the error. The session id is returned.
- A session aggregates its chunks by row label, and a chunk whose labels another chunk already used replaces those
  rows while the commit still succeeds (seen live on M26: chunks of five and four rows that both started at zero
  committed a log of five rows). So before a session opens, every parquet chunk's labels are read from its footer
  (a stored index column's statistics, a pandas `RangeIndex`, or the rows numbered from zero when the file carries no
  pandas metadata), and two chunks that give the same labels to different rows hold the record, naming both files.
  Chunks with exactly the same labels and different curves (a log whose curves were split) go together. After the
  commit, `GET {dataPath}?describe=true` reads the bulk back: fewer rows or columns than the chunks carried holds the
  record, naming both counts, and step `payload` returns `rows`. A target that cannot describe its bulk leaves the
  delivery unchecked with a warning, and a JSON payload is not measured, because the payload itself is never parsed.
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

### The Well Delivery shape

The Well Delivery DDMS keeps well planning and drilling entities in a store of its own, indexes the references between
them for its domain queries, and copies each entity into Storage where the deployment says so
([../specs/well-delivery-ddms/INTEGRATION.md](../specs/well-delivery-ddms/INTEGRATION.md)).

- Write: `PUT {root}/storage/v1/{type}` with the entity alone (the service takes no array), `{type}` being the type of
  the record id lowercased. The service answers 201 whether or not the entity passed its schema check; the findings of a
  failed check are kept on the attempt (`wellDelivery.schemaFindings`) and the record is delivered with a warning.
- Version: the route chooses it, a 13-digit epoch-millisecond value above the one the ledger holds, and records it as
  step `version` before the write, so a retry of the same revision sends the same version, which the DDMS replaces in
  place instead of adding a version. The service orders versions as text, which a fixed width keeps numeric. Content the
  ledger already delivered, redelivered, goes back under the version the ledger holds, so the entities that cite that
  version see the rewrite; on `provider: ibm`, whose store refuses a second save of a version, it takes a new one. A
  write answered with a server error, or not at all, is settled by reading that version back.
- References: the DDMS indexes only references that end in a version, and its queries and reference trees work only
  through that index. A reference the mapping renders in the usual form (`opendes:master-data--Well:w1:`) to an entity
  type the DDMS serves is sent with the version the DDMS holds for that entity (read once, and known without a read for
  an entity the same protocol wrote); one the DDMS does not hold is sent as rendered, and the attempt names it
  (`wellDelivery.unpinned`), so a redelivery once the entity lands adds the version.
- Rules checked before anything is sent: the id's shape, a type the reads can take (letters, digits and `-`), an entity
  id without `:` or `%` (the reference trees skip one with a colon), a kind, owners and viewers that name a domain, at
  least one legal tag (at most 25, which the service validates in one Legal request) and one country, a data object with
  `ExistenceKind` in reference form (ending in `:` or a version), `meta` as an array of objects, and a `StartDateTime`
  or `EndDateTime` in a form the service parses (it stores one it cannot parse, fractional seconds included, as null).
- Concurrency: the writes one node sends to the deployment go through a gate of `concurrency` (default 1).
- Read and verify: `GET {root}/storage/v1/{type}/{entityId}`, the latest version.
- Remove: the reversible scope is the DDMS's soft delete of every version (`DELETE .../{entityId}`, with the JSON
  content type the service requires; only a write of the same version restores it) and, with `mirror`, storage's
  `POST /records/{id}:delete` of the copy, whose id is the record id with its namespace replaced by the partition.
  Everything is the DDMS's purge (`DELETE .../{entityId}:purge`, an admin operation) and storage's purge of the copy.
  The history scope is refused: the DDMS keys every version by the value other entities' references cite.
- Probe: `GET {root}/info`, which the service's code serves and its contract does not declare.

### The RAFS shape

The Rock and Fluid Sample DDMS writes sample records through Storage and keeps the tabular content of their analyses
([../specs/rafs-ddms/INTEGRATION.md](../specs/rafs-ddms/INTEGRATION.md)).

- Write: `POST {root}/v2/{collection}` with a one-element array, typed exactly `application/json` (the service refuses
  a charset parameter). RAFS checks the kind against the collection, the record against its schema, its mandatory
  references and the records it names before it writes; the route checks what it can before sending: a
  `<authority>:wks:<entity type>:<x.y.z>` kind, an `acl` of owners and viewers alone, a `legal` block of tags, countries
  and status alone with a tag and a country, a SamplesAnalysis's `SampleAnalysisTypeIDs`, and a SaturationFunctionSet's
  identified functions. The version is matched by id in the response, whose names are camelCase on the wire; a record
  Storage skipped is read back. A FluidModel without its type is delivered with the warning RAFS gives.
- Content: each table of the record's `bulk` part, in content type order, as
  `POST {root}/v2/{collection}/{id}/data?content_schema_version={version}`, or `.../data/{contentType}` in a collection
  holding several types, streamed as `application/json` or `application/x-parquet`. Before the record is written, each
  content type and version is checked against the service's own catalogue (`GET /v2/samplesanalysis/analysistypes`,
  `GET /v2/fluidmodel/fluidmodeltypes`, or `GET /v2/{collection}/data/schema` for a collection of one type, each read
  once), and a depth shift table must hold one row. Each table is a step (`content-<type>`) returning its URN, content
  id and schema version, so a retry sends only the tables that did not land. The content id and schema version are read
  from the URN: its last two segments, `{dataset id}:{version}/{schema version}` in dataset mode and
  `{schema version}/{uuid}` in blob mode.
- Every table registers a `dataset--File.Generic` for the content (in dataset mode) and writes a new record version; the
  record is read back for the version it ends at. The ledger keeps the dataset ids (`rafs.datasets`): RAFS never removes
  them.
- The link: `data.DDMSDatasets` belongs to RAFS, and a record write replaces it, so a metadata update reads the stored
  record and carries its RAFS URNs; a manifest that rewrites the record through `osduManifestAndDdms` carries them too.
- Reads (verify, read back, the catalogues) send `Cache-Control: no-store`, since RAFS caches its answers for up to a
  minute.
- Remove: the reversible scope is RAFS's logical delete (`DELETE /v2/{collection}/{id}`) and storage's reversible
  delete of each content dataset. RAFS has no purge and no version operation, so the history scope is storage's version
  purge and everything is storage's purge of the record and of its content datasets.
- Probe: `GET {root}/info`, then `GET {root}/v2/samplesanalysis/analysistypes`, which checks the token and the
  partition.

### The production historian shape

The Production DDMS historian keeps the points of the series a `work-product-component--ProductionValues` record
defines, behind an ingestion service and a query service
([../specs/production-timeseries/INTEGRATION.md](../specs/production-timeseries/INTEGRATION.md)).

- Record: a Storage record. It goes to `PUT /api/storage/v2/records`, with the data keys the flow preserves and its link
  to its points, `urn://pddms/production-values/{id}/timeseries`, in `data.DDMSDatasets` (in place of any other
  historian link it renders), and is read, verified and removed through Storage. Checked before anything is sent: a
  ProductionValues kind of version 2.0.0 or later, a master data `ReportingEntityID`, and one
  `ProductionMetricValues` entry per series with a unique `DDMSDatasetID` and a `ParameterKindID`.
- Points: the `bulk` part's files ([documents.md](documents.md#the-ddmss-a-flow-delivers-to)), read in full before
  the record is written: every series one the record defines, of a kind the ingestion service takes, its values of
  that kind, its timestamps increasing, and the whole split into requests, so a point too large for one holds the
  record too.
- Write: `POST {root}/production-values/{id}/timeseries` with
  `{"timeseries":[{"timeseriesId","points":[{"timestamp","value"}]}]}`, typed exactly `application/json`, each request
  at most `maxRequestBytes` (or the target's declared ceiling below it), each series listed once per request, and the
  attempt's correlation id as `trace-id` beside `correlation-id`. The points go in file order, a parquet file row group
  by row group, series by series, so a request holds the next points of the series that fit.
- Steps: each request is a step, `points-<n>`, returning the SHA-256 of its body, its bytes, points and series, and per
  series the version it was accepted under and its points (`<series>.version`, `<series>.points`). The service takes no
  request id and stores every accepted series as a new version, so a later try splits the points the same way and sends
  only the requests whose body no try recorded. A request whose answer is lost after the service acted is the one that
  goes twice; the second version holds the same points.
- Answers: read per series from each item's `result.code`, matched to the request by position (the service answers
  207 for every batch, and leaves the series id out of a failure). 202 is accepted, with its version, range and points,
  which must be every point sent. A series the service refuses (400, 401, 403, 404) holds the record, the versions of
  the series it accepted named; any other refusal leaves the record for its next try, which sends the whole request
  again. The request refused as a whole with 400, 403 or 404 holds the record naming the likely cause; a 500 (the
  service's storage lookup failing) is retried.
- Read back: the ingestion service accepts points before they are stored, so each accepted version is read from the
  query service, `GET {queryRoot}/production-values/{id}/timeseries/{series}/versions/{version}?start=&end=`, in ranges
  of at most 10000 points (`end` being exclusive), until it serves at least the points sent in each, for at most
  `settleSeconds`, `pollSeconds` apart. A series answering 404 "Failed to get a Stream Mapping" has not reached the
  store yet. A request whose ranges are all served is recorded as `settled` on its step; when the time is up the record
  waits for its next try, which reads back only what is not settled and sends nothing again. A 403 from the query
  service holds the record: the flow's identity needs `service.pddms.viewer`, or `settleSeconds: 0`. Where the query
  service reads as of a version, points an earlier version holds in the same range count too, so a redelivery of
  points at timestamps a delivery already holds cannot tell its own version from the earlier one (how versions combine
  is open in the brief, section 11).
- Returned: `timeSeries.requests`, `timeSeries.points`, `timeSeries.settled`, and per series
  `timeSeries.<series>.versions` (the first 50), `.points`, `.start` and `.end`.
- Remove: every scope is Storage's, the record being a Storage record (`POST /records/{id}:delete`, the version purge,
  the purge). The historian has no delete for points, so they stay; the outcome says so.
- Probe: `GET {root}/info` and `GET {queryRoot}/info`.

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
   The step is marked with its `fileSource` before the request goes out, because the service mints a dataset
   record per accepted registration: a try that stops between the response and the report would otherwise
   register the same file again and leave the first dataset with nothing referencing it. A try that finds such a
   mark asks the search index which dataset that landing-zone path became
   (`POST {searchQueryPath}`, for up to `datasetIndexWaitSeconds`) and takes it over, returning it with
   `adopted`; nothing listed means the registration never landed and the file is registered again. Two datasets
   for one path hold the record, naming both. `osduManifest` registers through the same step.
3. The record, with `data.{datasetsProperty}` (default `Datasets`) referencing the registered datasets as `{id}:`
   (the form the work product component schemas require; any references the mapping rendered are kept), goes through the storage array endpoint exactly as `osduRecord`, batched with
   the rest of the batch. Step `records`.

A metadata-only change rewrites the record with the dataset ids of its earlier delivery (from the target
state); a payload change uploads and registers new datasets and rewrites the record to point at them. An
empty file, or one above `reliability.maxRequestBodyBytes`, holds the record before anything is sent. Purge
deletes the dataset records and their files (`DELETE {fileDeletePath}`, default
`/api/file/v2/files/{id}/metadata`) with the record; the reversible removal leaves them, so the record can be
restored whole, and a history purge touches only the record's own earlier versions. Verify and read back go to storage.

The files go as `payloadContentType`, or `filesContentType` when the flow names it.

## `osduDataset`

The files and the record through the Dataset service (openapi dataset v1, storage v2;
[../specs/core/INTEGRATION.md](../specs/core/INTEGRATION.md) sections 2.5 and 2.5.1). A record of a dataset kind is
the dataset; any other record refers to one dataset of `datasetKind` holding its files.

1. `POST {datasetInstructionsPath}?kindSubType=<entity type>` (default `/api/dataset/v1/storageInstructions`) hands out
   a staging location for the record's own entity type, or `datasetKind`'s. A 400 (no DMS serves the type) or 405 (the
   DMS stores no files) holds the record. No `expiryTime` is sent, because the Dataset service does not pass it on.
2. The files go where the location says, as `filesContentType` (or `payloadContentType`): a single file with `PUT` to
   its `signedUrl` and the flow's `uploadHeaders` (the Azure blob type added on an Azure blob host); a collection's
   files the way the provider that signed the location takes them. On Azure each file is created, appended in parts of
   at most 100 MiB and flushed under the Data Lake directory, each request stating the SAS's service version; on MinIO
   and S3 each is posted as a form with the POST policy's fields, its key under the directory and the file last; on
   Google Cloud Storage each is a media upload under the folder, with the token scoped to it. A location with
   temporary credentials for an endpoint it does not name (IBM) holds the record. A `dataset--File.*` record carries
   exactly one file, and a collection two files of one name holds the record. Step `storage-files` returns
   `fileSource` or `collectionPath`, `files`, `providerKey` and `complete`; a retry reuses it only when every upload of
   the earlier try completed, and otherwise asks for a new location, since the signed location is a credential that is
   never logged or kept.
3. The dataset record: a record of a dataset kind with `data.DatasetProperties.FileSourceInfo` (its one file) or
   `FileCollectionPath` and a `FileSourceInfos` entry per file, and `data.TotalSize`; for a record of another kind, a
   dataset of `datasetKind` under `{partition}:{dataset entity type}:{key}-files` with the record's `acl` and `legal`.
   `PUT {datasetRegisterPath}` (default `/api/dataset/v1/registerDataset`) registers up to 20 per request, and a
   request refused as a whole is tried record by record. `POST {datasetRetrievalPath}` (default
   `/api/dataset/v1/retrievalInstructions`) then checks that the service hands out every dataset it registered; one it
   leaves out fails its record for the try. Step `register` returns `datasetId`, `version` and `retrievable`.
4. A record of another kind is written through storage as `osduRecord` writes it, its dataset list naming the dataset,
   when its document changed or it has not named the dataset before. A change to a dataset record alone is written
   through storage with the `DatasetProperties` storage holds, because a registration would copy the staging area
   again; a record storage does not hold, or holds without them, fails the try.

A dataset record's reversible removal is `POST {datasetSoftDeletePath}` (default
`/api/dataset/v1/metadataRecord/{id}/softDelete`), which the Dataset service's undelete restores; any other record's
is storage's, which leaves its dataset. The history purge is storage's. Removing everything purges the record through
storage, and for a record of another kind its dataset too; the files a registration copied stay in the platform's
storage. Verify and read back go to storage.

## `osduFileAndDdms` and `osduManifestAndDdms`

The composed routes send a record's files and its bulk data as parts ([Payload parts](#payload-parts)).

`osduFileAndDdms`:

1. The DDMS's rules for the record and the bulk data's chunks are checked first, as `osduWellLog` checks them, so a
   record the DDMS would refuse is held before any file is uploaded.
2. When the files part goes, its files are uploaded and registered as in `osduFile` (steps `upload-{i}` and
   `register-{i}`), as `filesContentType` (default `application/octet-stream`).
3. The record goes through its DDMS collection when its document changed or its files moved, its dataset list naming
   the new datasets or those of its earlier delivery; the bulk link the DDMS holds is carried, from the stored record
   when the record was delivered before.
4. When the bulk part goes, the bulk data goes as `osduWellLog` sends it, as `payloadContentType`, and the version is
   read back.

It returns `datasetIds`, `files`, and `payload.<set>` for each part it sent. Removal is the DDMS's; removing everything
also deletes the datasets the files were registered as, with their files, through the file service. The probe asks
the file service and every DDMS the flow reaches.

`osduManifestAndDdms`:

1. Each record's DDMS rules and bulk data chunks are checked first.
2. A record whose document changed, whose files moved, or which is new and has bulk data goes through the manifest as
   `osduManifest` sends it (its files registered first, as `filesContentType`). A record that already holds bulk data
   has the DDMS's bulk link, and the `DDMSDatasets` entries the DDMS wrote, carried into its manifest from one batched
   storage read (`POST {verifyBatchPath}`, projected to `data.ExtensionProperties` and `data.DDMSDatasets`), since
   ingestion writes through storage, past the DDMS; a read that fails fails those records for the try. A new record's
   manifest carries the link its DDMS gives a record it holds nothing for yet: the historian's link to its points, and
   no Wellbore DDMS bulk link.
3. Once the run has written the record, its bulk data goes through its DDMS from the version the manifest wrote. A
   failure there leaves the record as the manifest wrote it; the next try resumes the manifest step's run rather than
   ingesting the record again, and sends the bulk data.
4. A record whose bulk data alone changed skips the manifest.

Verify and read back go to storage. Removal is the DDMS's, with the files' datasets when everything goes. The probe
asks the Workflow service and every DDMS.

## `osduWorkflow`

One protocol for every ingestion workflow ([documents.md](documents.md#the-workflow-route);
[../specs/workflows/INTEGRATION.md](../specs/workflows/INTEGRATION.md)).

1. The anchor tag, when the route declares one, is written into the record's `tags`.
2. Each input whose hash moved is registered through the Dataset service as in `osduDataset`: one dataset per file under
   `{partition}:{entity type}:{key}-{input}-{n}`, or one collection under `{key}-{input}`, with the record's `acl` and
   `legal`. Step `register-{input}` returns the ids; the target state keeps them as `input.<name>`. A storage anchor's
   own files are registered the same way, as `datasetKind`.
3. The anchor is written when its document or its files changed: a dataset anchor with new files is staged and
   registered as `osduDataset` registers a dataset record; any other write goes through storage (a dataset anchor
   carrying the `DatasetProperties` storage holds, a storage anchor its dataset list). Step `anchor`.
4. When a run is due (`runWhen`: the anchor or an input was written, the record is new, or a redelivery names
   `workflow`), each stage in order: the context filled (secrets only in the request), `Payload` added when the
   workflow reads it and the context leaves it out, the context checked against the workflow's contract (a context
   it refuses holds the record), step `stage-{n}` marked `triggering` with the run id, `POST {workflowRunPath}`, then
   polled every `pollSeconds` until it ends or its timeout passes. A retry that finds `triggering` sends the same run
   id again, and a 409 says the service has the run; one that finds `triggered` polls it. A failed run fails the
   record, and the next try triggers a new one. A finished run's outputs, read from their templates or from an XCom
   entry, are kept on the step, and a retry reuses them without running the stage again.
5. What the runs wrote is found the way the route declares (`anchor`, `ids`, `artefact`, `search` by cursor through
   `query_with_cursor`, `manifest` read from the retrieval instructions of the manifest dataset, or `xcom`) and read
   back from storage, repeated every `workflowPollSeconds` until `minimum` records are present or `waitSeconds` pass.
   Fewer fail the record for the try, and the next try looks again without running the workflows again. Step
   `results`; the target state keeps `workflow.records`, the first `keep` ids as `workflow.recordIds`, and a search's
   kind and query.
6. The anchor's version is read back, since a workflow may have written the anchor itself, and the target state keeps
   the run ids as `payload.workflow`.

An XCom output is read through the Workflow service's `latestInfo`, which serves the run's latest task only, unless
`target.airflow` names the Airflow behind the service: then `GET .../dags/{workflow}/dagRuns/{runId}/taskInstances/{task}/xcomEntries/{key}`
on `/api/v1` (Airflow 2, a string value) or `/api/v2` (Airflow 3, the stored value), with the flow's Airflow
credentials, which on Airflow 3 are exchanged at `POST /auth/token` for a token kept until shortly before it expires.
The record ids are taken from the value, parsed as JSON or as the text Airflow 2 renders, each without its version,
and of the entity type `match` names.

The reversible removal of a dataset anchor goes through the Dataset service's `softDelete`, of a storage anchor through
storage. The records the runs created are removed at the same scope when the route removes them (`remove`), a search
repeated to find them all; the registered inputs and files go only when everything goes. The probe asks the Workflow
service, and whether this partition registers every workflow the stages name.

## `osduManifest`

OSDU's own bulk path (openapi file v2, workflow v1, storage v2): the batch's files, one manifest, one
workflow run.

1. The batch's files are uploaded and registered as in `osduFile` (steps `upload-{i}` and `register-{i}`), and
   each record's dataset list references the datasets registered for it as `{id}:`. Registration comes first
   because it is what makes a file retrievable: a dataset the manifest only describes is created with its file
   left in the landing zone, where the file service's download URL finds nothing (observed on a live M26
   service). The file service mints the dataset ids and ignores an id the request supplies, so a payload
   change registers new datasets and the record points at them; the earlier ones stay, as for `osduFile`. The
   reference form matters as well: manifest ingestion validates the schemas' reference pattern and drops a
   record that breaks it.
2. Before the manifest, the registered datasets are waited for until the search index lists them
   (`POST {searchQueryPath}`, default `/api/search/v2/query`, asked every `workflowPollSeconds` for up to
   `datasetIndexWaitSeconds`, default 120; 0 does not wait). Ingestion checks a record's references against the
   index and drops a record whose dataset it cannot find yet while the run still finishes: on a live M26 service
   a manifest sent a second after registration lost its record, and the same manifest sent once the index
   listed the dataset wrote it. A wait that runs out is named on step `indexed` and the manifest goes ahead.
   Then each record's version is read from storage, as in step 5, and carried on the manifest step as
   `priorVersion`.
3. One manifest (`manifestKind`, default `osdu:wks:Manifest:1.0.0`) carries every record of the batch in the
   section its kind names (`ReferenceData`, `MasterData`, `Data.WorkProduct`, `Data.WorkProductComponents`,
   `Data.Datasets`; `manifestSection` overrides). `POST {workflowRunPath}` (default
   `/api/workflow/v1/workflow/{workflow}/workflowRun`, `workflowName` default `Osdu_ingest`) with
   `{ runId, executionContext: { Payload: { AppKey, data-partition-id, ...workflowPayload }, manifest } }`.
   The run id is chosen here, so a request the service accepted before a retry resent it answers 409 and is
   polled, not run twice. Step `manifest` is reported on every record of the batch, with the run id, before
   polling starts.
4. `GET {workflowStatusPath}` every `workflowPollSeconds` until the run reaches a terminal status, or
   `workflowTimeoutMinutes` pass. The terminal statuses are `SUCCESS`, `PARTIAL_SUCCESS`, `FINISHED` and
   `FAILED`, compared upper case because the service reports them in both cases (openapi workflow v1:
   `WorkflowRunResponse` is upper, `WorkflowRun` is lower). A timeout fails the try; the next try resumes
   polling the same run. A failed run fails the batch; the next try triggers a new run. A status the service
   has never been known to report is named in the error rather than polled forever. Step `workflow` returns
   the status and timestamps.
5. The records are read back from storage (`POST {recordQueryPath}`, default
   `/api/storage/v2/query/records`, a hundred ids per request, projected to the dataset list) so each settles
   on its own evidence: present with a version, delivered; named under `retryRecords`, failed saying so; not
   returned, failed with the run named and re-submitted in a new run on the next try. Storage names a record it
   does not hold under `invalidRecords` (a live M26 service does), so a listed id is a record the workflow did
   not write, not a verdict on the id. A record still at the version it held before the run (`priorVersion`)
   was not written by it either, because a finished run that dropped a record leaves an existing one in place,
   and it goes into a new run the same way. Step `records` returns
   the record id and version.

Verify, read back and removal go to storage, and a purge of everything deletes the datasets and their files through the file
service, as for `osduFile`.

A manifest goes by reference when `manifestByReference` is `always`, or `auto` and the trigger request (measured as the
request indented by four) is above `manifestInlineLimitKb`, on a partition that registers `byReferenceWorkflowName`
(asked once, `GET {workflowPath}`). The manifest, with the first record's `acl` and `legal` at its top level, is stored
through the Dataset service as a `dataset--File.Generic` (of `datasetKind`) under
`{partition}:{entity type}:osdu-delivery-manifest-{runId}`, and the run is triggered on the by-reference workflow with
`{ Payload, acl, legal, manifest: "<id>" }`. The manifest step names the workflow and the dataset, so a retry resumes
that run, and the dataset is removed reversibly (`softDelete`) once the run has settled. `always` on a partition
without the workflow fails the batch; `auto` there splits the batch into manifests under the limit, a record whose
manifest alone is above it going on its own.

## Before a run: legal tags

Every record a mapping renders carries the same legal tags, and storage refuses a record whose tag is unknown or
expired, on every record that carries it. So a deliver or intake run asks the legal service first
(`POST /api/legal/v1/legaltags:validate`, at most 25 names per request) and, when it refuses any tag, the run fails
before anything is planned or sent, naming each tag and the reason the service gives (expired, not found). Plan runs
do not ask; they send nothing.

- Where it asks: under the endpoint, for every flow whose endpoint is the platform root, which includes a ddms flow
  that declares `ddmsRoot`, a DDMS with a root or a registration under `target.ddms`, or any interface of a source. A
  ddms flow whose endpoint is the DDMS itself does not reach the legal service by a path; it asks only when it names
  `protocolOptions.legalValidatePath` (normally an absolute URL), and otherwise the run logs that the tags were not
  checked. Not checked is never read as valid.
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
| Held | 400, 403, 404, 405, 409, 413, 415, 422, any status in `reliability.skipStatusCodes`, no payload chunks, an empty file, a staging location no upload can reach, a workflow context its contract refuses, a pending payload in parts this version did not write, a `RecordHeldException` | `held` (terminal until released) |
| Failed | the record-level retry budget (`reliability.retry.attempts`) is exhausted | `failed` (released like held) |

Inside one call the HTTP executor repeats a request only when repeating it is safe, the line the OSDU C# client
draws in its `ReadRetryHandler`:

- Safe by method: GET, HEAD, PUT, DELETE (RFC 9110). Safe by the service's own semantics, declared by the
  protocol that makes the call: record writes with client-supplied ids, `POST /records/{id}:delete` and the bulk
  soft delete (a repeat finds the records already gone), reads by id (`POST /query/records`), searches, the
  replace-the-whole-bulk `POST {dataPath}`, the workflow trigger (it names its own run id, so a resend answers
  409), the Dataset service's storage instructions (a call only signs a location), registration (every record names
  its own id) and retrieval instructions, its `softDelete` (a repeat answers 404, which reads as already gone), and
  token requests.
- Never repeated: session create, session chunks, session commit, file registration (`POST /files/metadata`, which
  mints a dataset record per accepted call), and the uploads a provider takes by `PATCH` or `POST` (a Data Lake append
  or flush, a POST policy form, a Google Cloud Storage media upload). Not after a status, and not after a transport
  failure either, where the service may have acted before the connection went.
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
