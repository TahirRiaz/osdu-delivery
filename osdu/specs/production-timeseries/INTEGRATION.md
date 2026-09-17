# Production time series (historian) and PDMS CSV parser: integration brief

The Production DDMS historian stores bulk time series (points) for series defined by OSDU
`work-product-component--ProductionValues` records. It consists of the ingestion service
(`pddms-timeseries-ingestion`), the query service (`pddms-timeseries`), and the PDMS CSV parser
(`osdu-pdms-csv-parser`), which is the Airflow workflow `pddms-csv-parser` that loads CSV files into the historian
through the ingestion service. The two services carry SLB and PETRONAS copyright notices [783 README.md:59-60]
[790 README.md:57-58]. This brief serves the `timeSeries` route type of stage 7 of
`docs/osdu-coverage-plan.md`, and the historian's use of the generic `workflow` route of stage 6. It lists every call
those routes make with the contract it comes from, the payload and identity rules, reads and verification, the
missing delete, limits, where the pinned contracts and the code disagree, and what is still open. DSPDM, the
relational Production DDMS "core", is a separate product with its own brief,
`osdu/specs/production-dspdm/INTEGRATION.md` (section 12).

## Sources

| Key | What it is | Pinned at |
| --- | --- | --- |
| `ING` | `osdu/specs/production-timeseries/ingestion.openapi.yaml`: OpenAPI 3.0.3, "OSDU Production DDMS. Time-series Ingestion Service" 1.0.0; copy of `docs/api/azure/openapi.yaml` of project 783 | `46b4d57e998b68dd82068ec0ab09711facd0baf4` (`sources.json`) |
| `QRY` | `osdu/specs/production-timeseries/timeseries.openapi.yaml`: OpenAPI 3.0.3, "OSDU Production DDMS. Time-series Service" 1.0.0; copy of `docs/api/azure/openapi.yaml` of project 790 | `3bf10e48ffa8c703e76818efeff406364efe9178` (`sources.json`) |
| `STORAGE` | `osdu/specs/core/storage/openapi.yaml`, Storage service, from the core specification set | `sources.json` |
| `FILE` | `osdu/specs/core/file/openapi.yaml`, File service, from the core specification set | `sources.json` |
| `WORKFLOW` | `osdu/specs/core/workflow/openapi.yaml`, Workflow service, from the core specification set | `sources.json` |
| `783` | Project 783, `osdu/platform/domain-data-mgmt-services/production/historian/services/pddms-timeseries-ingestion`, branch `main` | `46b4d57e998b68dd82068ec0ab09711facd0baf4` (2026-09-15; head of `main` on 2026-09-16) |
| `790` | Project 790, `osdu/platform/domain-data-mgmt-services/production/historian/services/pddms-timeseries`, branch `main` | `3bf10e48ffa8c703e76818efeff406364efe9178` (2026-09-15; head of `main` on 2026-09-16) |
| `1486` | Project 1486, `osdu/platform/domain-data-mgmt-services/production/historian/services/osdu-pdms-csv-parser`, branch `main` | `b83d9c8be5493025077606554afeb9b8afe4c5c2` (2026-09-15; head of `main` on 2026-09-16) |
| `1525` | Project 1525, `osdu/platform/domain-data-mgmt-services/production/historian/home` (docs, demo scripts, Postman collections), branch `main` | `d61ffc361c81bf6fef46280ffdef1b4917909c61` (2026-09-10; head of `main` on 2026-09-16) |

Path shorthands inside project citations:

| Shorthand | Path |
| --- | --- |
| `ing/` | `src/main/scala/org/opengroup/osdu/production/` in 783 |
| `qry/` | `src/main/scala/org/opengroup/osdu/production/timeseries/` in 790 |
| `csv/` | `csv-parser-core/src/main/java/org/opengroup/osdu/csvparser/` in 1486 |
| `azure.properties` | `provider/csv-parser-azure/src/main/resources/application.properties` in 1486 |
| `ks/` | `docs/knowledge-sharing/` in 1525 |
| `csvwf/` | `api/csv-ingestion-workflow/` in 1525; `csvwf/collection` is `api/csv-ingestion-workflow/OSDU PDDMS M25 CSV Ingestion Workflow.postman_collection.json`, cited by request name |

How statements are marked: a citation with a contract key (`ING`, `QRY`, `STORAGE`, `FILE`, `WORKFLOW`) rests on the
pinned contract; a citation with a project key (`783`, `790`, `1486`, `1525`) rests on that project's source, docs or
examples at the pinned commit and is not in the contract; "(inference)" marks a conclusion drawn from the cited files
that neither states outright.

Shared libraries: both services build on `org.opengroup.osdu.production` libraries (`pddms-security-commons-lib-*`,
`pddms-pekko-stream-commons-lib`, `pddms-platform-services-timeseries-lib` and others) [783 build.sbt:20-24, 104-110].
`MetadataRepository` (the Storage record lookup), `EntitlementFunctions`, `requireRole`, `requireJson`,
`CompletionHelper` and `ParameterKind` live there and were not examined; statements that depend on them say so.

## 1. Base paths, versions, headers and auth

### 1.1 Ingestion service

- Contract: server `/api/pddms/ingest/v1` ("OSDU API Gateway"), security `BearerAuth` [ING: servers, security];
  operations `getDetailedVersionInfo` (`GET /info`), `getShortVersionInfo` (`GET /info/short`), `livenessCheck`,
  `readinessCheck` and the three ingestion operations of section 3.2 [ING: paths].
- Service routes are `{http.prefix}/v1/...` with `prefix = ""` by default [783 ing/route/EndpointRouter.scala:50-101;
  ing/common/ApiVersion.scala:20; src/main/resources/local.conf:10-19].
- Gateway: the core-plus VirtualService rewrites the prefixes `/api/pddms/ingest/` and `/api/timeseries-ingestion/`
  (with or without the trailing slash) to `/`, and passes `/v1` through
  [783 devops/core-plus/deploy/templates/virtual-service.yaml:15-47]; the Azure values use `path: "/api/pddms/ingest/"`,
  `pathRewrite: "/"` [783 devops/azure/pddms-timeseries-ingestion.values.yaml:43-44]. So the batch call is
  `POST https://{host}/api/pddms/ingest/v1/production-values/batch/timeseries` (inference), which is what the demo
  client and the CSV parser call [1525 demo/utils/client.py:171; 1486 csv/timeseries/ProductionValuesTimeSeriesClient.java:54].
- The M27 Postman collection uses `{{PDDMS_PROTOCOL}}://{{PDDMS_HOST}}/writeback/service/timeseries-management/v1`
  [783 postman/pddms-timeseries-ingestion.postman_collection.json:2375-2376], with `PDDMS_HOST` such as
  `osdu-glab.msft-osdu-test.org/api/pddms` [1525 api/api/README.md:11]. The base path is a per-environment parameter.
- `GET /v1/liveness_check` and `GET /v1/readiness_check` are open and answer plain text
  [783 ing/route/LivenessRoute.scala:7-11; ing/route/ReadinessRoute.scala:7-11]; `/v1/info` and `/v1/info/short` need
  a token only [783 ing/route/EndpointRouter.scala:65-69].

### 1.2 Query service

- Contract: server `/api/pddms/query/v1`, security `BearerAuth` [QRY: servers, security].
- Routes are under `v1` [790 qry/route/EndpointRouter.scala:69-74]; the probes are `/v1/liveness_check` and
  `/v1/readiness_check`, and readiness answers 500 when Redis is not connected [790 qry/route/ProbeRoutes.scala:34-57;
  QRY: readinessCheck].
- Gateway: the core-plus VirtualService rewrites `/api/pddms/query/` and `/api/timeseries/` to `/`
  [790 devops/core-plus/deploy/templates/virtual-service.yaml:15-47]. The demo client calls
  `/api/pddms/query/v1/production-values/batch/timeseries?start=...&end=...` [1525 demo/utils/client.py:213]; the M27
  Postman collection uses `{{PDDMS_PROTOCOL}}://{{PDDMS_HOST}}/ppstimeseries/service/timeseries/v1/ts`
  [790 postman/pddms-timeseries.postman_collection.json:2371-2372].

### 1.3 Headers (both services)

| Header | Rule | Source |
| --- | --- | --- |
| `Authorization: Bearer <token>` | required | [ING, QRY: components.securitySchemes.BearerAuth]; an `AuthorizationFailedRejection` is answered 401 "The supplied authentication is invalid or has expired." [783 ing/route/EndpointRouter.scala:103-113; 790 qry/route/EndpointRouter.scala:108-114]; the ingestion handler also refuses an empty value with "Authorization header is missing/invalid" [783 ing/util/validation/ValidationUtil.scala:32-35; ing/common/IngestionServiceMessages.scala:36] |
| `data-partition-id` | required | [ING, QRY: components.parameters.DataPartitionId]; ingestion only accepts the deployment's single configured partition (`data-partition-id`, environment `DATA_PARTITION_ID`, default `opendes`); any other value fails the series with 400 "Data partition id is not valid" [783 src/main/resources/local.conf:155-156; ing/util/validation/ValidationUtil.scala:37-40; ing/handler/IngestionHandler.scala:56-67; ing/common/IngestionServiceMessages.scala:35] |
| `Content-Type: application/json` | POST routes are wrapped in `requireJson` (shared library) | [783 ing/route/IngestTimeSeriesRoute.scala:76; ing/route/IngestTimeSeriesBatchRoute.scala:68; 790 qry/route/GetTimeSeriesBatchForSingleProductionValuesRoute.scala:43] |
| `trace-id` | optional correlation id; a UUID is generated when absent; the value is returned in the response header `CorrelationId` | [783 ing/common/CorrelationIdDirective.scala:28, 41-51; ing/route/IngestTimeSeriesRoute.scala:69-72; 790 qry/common/CorrelationIdDirective.scala:11; qry/route/EndpointRouter.scala:59-63] |
| `x-api-token` | optional; read and handed to the ingestion handler, which does not use it | [783 ing/common/ApiHeader.scala:23; ing/route/IngestTimeSeriesRoute.scala:182; ing/handler/IngestionHandler.scala:40-72] |
| `Accept-Encoding: gzip` | the query service's single-series and single-record routes wrap their answer in `encodeResponseWith(Coders.Gzip)`; the multi-record batch routes do not | [790 qry/route/GetTimeSeriesRoute.scala:83; qry/route/GetTimeSeriesByVersionRoute.scala:82; qry/route/GetTimeSeriesLatestValueRoute.scala:62; qry/route/GetTimeSeriesBatchForSingleProductionValuesRoute.scala:70; qry/route/GetTimeSeriesLatestValueBatchForSingleProductionValuesRoute.scala:69] |

### 1.4 Authorization

- Ingestion: the ingestion and validation routes sit inside `requireRole(Admin, Editor)`
  [783 ing/route/EndpointRouter.scala:70-78]; the contract: "Allowed roles: `service.pddms.editor` and
  `service.pddms.admin`" [ING: ingestTimeSeries, ingestTimeSeriesBatchForSingleRecord, ingestTimeSeriesBatch,
  description].
- Query: `requireRole(Admin, Editor, Viewer)` [790 qry/route/EndpointRouter.scala:73]; the contract: "Allowed roles:
  `service.pddms.viewer`, `service.pddms.editor` and `service.pddms.admin`" [QRY: getTimeSeries and the other data
  operations, description].
- Group names default to `service.pddms.viewer`, `service.pddms.admin`, `service.pddms.editor` and can be changed with
  `OSDU_CORE_ENTITLEMENT_PDMS_VIEWER`, `..._ADMIN`, `..._EDITOR` [783 src/main/resources/local.conf:21-33;
  devops/core-plus/deploy/values.yaml:34-36]. The failure status of `requireRole` is decided in a shared library; the
  contracts document 403 [ING, QRY: components.responses.403Forbidden].
- Record access: every ingestion and query request loads the ProductionValues record through `MetadataRepository`
  with headers built from the caller's request [783 ing/route/IngestTimeSeriesRoute.scala:80-83;
  ing/route/IngestTimeSeriesBatchRoute.scala:76-80; 790 qry/route/GetTimeSeriesRoute.scala:60-62]. The Storage
  settings are `storage-service.getRecordById = "/records/{elementId}"` on
  `https://${AZURE_DNS_NAME}${OSDU_CORE_STORAGE_PATH}`, with a circuit breaker (30 s call timeout, 5 failures, 1 minute
  reset) [783 src/main/resources/local.conf:35-48], and `OSDU_CORE_STORAGE_PATH=/api/storage/v2`
  [783 devops/core-plus/deploy/values.yaml:37; devops/azure/pddms-timeseries-ingestion.values.yaml:68]; that is the
  core `GET /records/{id}` [STORAGE: getLatestRecordVersion] (inference). The caller therefore also needs read access
  to the record (inference).

## 2. Data model

- A series is defined by a Storage record of kind `osdu:wks:work-product-component--ProductionValues:2.0.0`
  [1525 docs/properties.md:3, 14]; the contract describes `recordId` as "ProductionValues v2.0.0+ record ID"
  [ING, QRY: components.parameters.RecordId].
- One record defines many series: each `data.ProductionMetricValues[]` entry is one series, and its `DDMSDatasetID` is
  the `timeseriesId` of the APIs [1525 docs/timeseries.md:5, 115-121] [ING, QRY: components.parameters.TimeseriesId,
  "DDMSDatasetID. The timeseries identifier within the record."].
- Record fields [1525 docs/properties.md:10-91]: `Source`; `DDMSDatasets: ["urn://pddms/production-values/{recordId}/timeseries"]`;
  `Remarks`; `ReportingEntityID`; `StartDateTime`; `EndDateTime`; shared `NominalPeriodID`, `QuantityMethodID`,
  `DurationContextID`, `VolumeFlowMeasurementTypeID`, `MeasurementConditions`; and per series `PropertyID`,
  `UnitQuantityID`, `ProductID`, `DispositionID`, `ParameterKindID`, `UnitOfMeasureID`, `DDMSDatasetID`.
- `ReportingEntityID` is an existing Storage record id; the allowed kinds are `master-data--Field`, `Reservoir`,
  `ReservoirSegment`, `Well`, `Wellbore`, `IsolatedInterval` and `WellboreOpening`; a looser master-data pattern is
  under discussion [1525 docs/entities.md:9-33].
- `Source`: "Requests where source is missing or unrecognized will return an error status."
  [1525 docs/timeseries.md:182-184]. No such check was found in the 783 files examined; if it exists, it is in the
  shared record lookup.
- Value kinds [1525 docs/timeseries.md:41-49]:

  | Value type | ParameterKind reference | Notes |
  | --- | --- | --- |
  | DOUBLE | `*:reference-data--ParameterKind:Double:` | |
  | INTEGER | `*:reference-data--ParameterKind:Integer:` | 64-bit signed |
  | BOOLEAN | `*:reference-data--ParameterKind:Boolean:` | |
  | STRING | `*:reference-data--ParameterKind:String:` | |
  | DATETIME | `*:reference-data--ParameterKind:Timestamp:` | ISO-8601 in UTC, for example `2020-12-16T11:46:20.163Z` |

- `ParameterKindID` fixes the series' value type: `valueType = ParameterKind.parse(metadata.parameterKindId)`
  [783 ing/command/IngestTimeSeriesCommand.scala:33; 790 qry/command/GetTimeSeriesCommand.scala:59]. The mapping from
  the reference value to a kind is in a shared library. A kind whose text contains `set-string` or `setstring` is
  treated as SET-STRING [783 ing/util/validation/ValidationUtil.scala:47-48; ing/service/TimeSeriesPublisherMessage.scala:74-75].
- Responses echo each series' metadata as `nominalPeriodId`, `quantityMethodId`, `durationContextId`,
  `volumeFlowMeasurementTypeId`, `propertyId`, `unitQuantityId`, `productId`, `dispositionId`, `parameterKindId`,
  `unitOfMeasureId` [ING, QRY: components.schemas.TimeSeriesMetadata]; the serializers drop `dDMSDatasetId` and
  `reportingEntityId` from it and put `reportingEntityId` on the record item
  [783 ing/common/IngestTimeSeriesResponseSerializer.scala:65-98; 790 qry/common/GetTimeSeriesResponseSerializer.scala:74-101].
- Streams are internal. The ingestion service publishes messages keyed `{recordId}:{timeseriesId}`
  [783 ing/util/IngestionCommonsUtil.scala:45-47; ing/service/TimeSeriesPublisherMessage.scala:56, 120-127]; a
  downstream publisher validates or creates the stream and forwards the data to the store; a stream store links the
  stream id to the ProductionValues id; Redis caches the mapping [1525 ks/20260119_111734.content.md:82-116]. The query
  service reads the mapping from the Redis key `/stream-mappings/stream/{recordId}:{timeseriesId}` and the store from
  `{PDI_TIMESERIES_BASE_URL}{PDI_TIMESERIES_API_PATH}/{streamId}` (path default `/timeseries/api/v1/streams`)
  [790 qry/pds/timeseries/TimeSeriesService.scala:226-242; qry/pds/timeseries/TimeSeriesQueryService.scala:102-123;
  qry/pds/PdsHttpClient.scala:37-38]. The publisher and the store are not in these projects.
- Stores: Azure Data Explorer with Azure Service Bus, or TimescaleDB with Kafka
  [1525 ks/20260127_030042.content.md:18-28].
- Bi-temporal and immutable: each accepted write gets a server version, reads can be pinned to a version, and
  "information cannot be discarded even if it is erroneous" [1525 docs/timeseries.md:220-240; docs/key-principles.md:5].

## 3. The calls a writer makes

### 3.1 Step A: the definition record (Storage)

1. The reporting entity (master data) must already exist in Storage [1525 docs/entities.md:9].
2. Create the ProductionValues record with `PUT /records` on the Storage service, a JSON array of records
   [STORAGE: PUT /records, createOrUpdateRecords], with one `ProductionMetricValues` entry per series. The demo does
   this with `PUT /api/storage/v2/records` [1525 demo/utils/client.py:360-381; demo/create_productionvalues.py:87].
   The ingestion contract has no operation that creates a definition [ING: paths].
3. The demo derives the record id with `uuid5` over (CSV file name, asset), so reruns do not mint new records
   [1525 demo/utils/productionvalues.py:16-24]. It writes kind version `1.3.0` and picks each `ParameterKindID` at
   random [1525 demo/utils/productionvalues.py:37, 49]; neither is suitable for delivery (section 10).

### 3.2 Step B: the points (ingestion service)

| operationId | Method and path | Body | Declared responses |
| --- | --- | --- | --- |
| `ingestTimeSeries` | `POST /production-values/{recordId}/timeseries/{timeseriesId}` | `IngestTimeSeriesRequest`, for example `{"points": [{"timestamp": 978307200000, "value": 58883469.74}]}` | 202, 400, 401, 403, 404, 500 |
| `ingestTimeSeriesBatchForSingleRecord` | `POST /production-values/{recordId}/timeseries` | `IngestTimeSeriesBatchForSingleProductionValuesRequest`, for example `{"timeseries": [{"timeseriesId": "ESTIMATED_OIL_VOLUME", "points": [...]}]}` | 202, 207, 400, 401, 403, 404, 500 |
| `ingestTimeSeriesBatch` | `POST /production-values/batch/timeseries` | `IngestTimeSeriesBatchRequest`, for example `{"productionValues": [{"recordId": "...", "timeseries": [{"timeseriesId": "...", "points": [...]}]}]}` | 202, 207, 400, 401, 403, 500 |

All from [ING: paths]; every request body is required and `application/json`; every operation requires
`data-partition-id`. The source request models are `Point(timestamp: Long, value)` and items carrying only
`timeseriesId` and `points` [783 ing/request/Point.scala:20; ing/request/IngestTimeSeriesRequest.scala:26-27;
ing/request/IngestTimeSeriesBatchTimeSeriesRequestItem.scala:31-33].

Processing (source):

1. The record is loaded: one lookup for the single-record forms, one `getBatch` over the distinct record ids for the
   multi-record form [783 ing/route/IngestTimeSeriesRoute.scala:80-83;
   ing/route/IngestTimeSeriesBatchForSingleProductionValuesRoute.scala:79-82; ing/route/IngestTimeSeriesBatchRoute.scala:76-80].
2. Each `timeseriesId` must be one of the record's `DDMSDatasetID`s, otherwise that series is answered 404
   "'X' timeseries not found in {recordId}" [783 ing/route/IngestTimeSeriesRoute.scala:85-86, 139-144;
   ing/route/IngestTimeSeriesBatchForSingleProductionValuesRoute.scala:149-160].
3. Each series is validated (section 3.4), then accepted and published (section 3.5).

### 3.3 Response body and status

- The body of all three operations is an array of record items: `{recordId, reportingEntityId, timeseries[], result}`,
  or `{recordId, result}` for a record that failed as a whole [ING: components.schemas.BatchResponse, BatchResultItem,
  ProductionValuesErrorResponse] [783 ing/common/IngestTimeSeriesResponseSerializer.scala:57-71].
- An accepted series is `{timeseriesId, version, start, end, metadata, pointsCount, result}` with `result`
  `{"code": 202, "reason": "Accepted", "message": "The request has been accepted for processing, but the processing
  has not been completed."}`; `start` and `end` are the smallest and largest point timestamps
  [ING: ingestTimeSeries 202 example] [783 ing/common/IngestTimeSeriesResponseSerializer.scala:73-98;
  ing/response/IngestTimeSeriesResponse.scala:46-72].
- An unknown series is `{timeseriesId, result}` with code 404 [ING: ingestTimeSeriesBatchForSingleRecord 207 example]
  [783 ing/common/IngestTimeSeriesResponseSerializer.scala:61-63].
- A series that fails the handler's checks carries no `timeseriesId`: validation failures are `{result}` and the
  token and partition failures `{content, result}` [783 ing/handler/IngestionHandler.scala:53-72;
  ing/common/IngestTimeSeriesResponseSerializer.scala:54-55, 105-106] [ING: ingestTimeSeriesBatchForSingleRecord 207
  example, the item without `timeseriesId` at file lines 377-382]. Items keep the request order
  [783 ing/route/IngestTimeSeriesBatchForSingleProductionValuesRoute.scala:149-162], so per-series results are matched
  to the request by position (inference).
- Record-level `result`: for `ingestTimeSeries` it carries the series' own code; for the two batch operations it is
  always 207 "Multi-Status", "Partially successful. See sub-requests response codes.", even when every series was
  accepted [783 ing/route/IngestTimeSeriesBatchForSingleProductionValuesRoute.scala:175-182;
  ing/common/IngestTimeSeriesResponseSerializer.scala:35-41, 65-71].
- HTTP status:

  | Operation | Status | Source |
  | --- | --- | --- |
  | `ingestTimeSeries` | the series' code (202, or 400 from validation); 404 "'X' timeseries not found in {recordId}" for an unknown series; the lookup's code and message when the record cannot be read; 500 "Storage service error" when the lookup throws | [783 ing/route/IngestTimeSeriesRoute.scala:105-153] |
  | `ingestTimeSeriesBatchForSingleRecord` | 207 for any batch that was parsed and executed; the lookup's code when the record cannot be read; 500 "Storage service error" when the lookup throws | [783 ing/route/IngestTimeSeriesBatchForSingleProductionValuesRoute.scala:95-113, 175-182] |
  | `ingestTimeSeriesBatch` | 207 for any batch that was parsed and executed ("Always return 207 for a successfully parsed and executed batch"); a record the lookup did not return is an item with 404 "Record not found {recordId}"; a record the lookup refused is an item with the lookup's code; 500 "System error: ..." when the batch lookup throws | [783 ing/route/IngestTimeSeriesBatchRoute.scala:84-140] |

- Only 200, 202, 207, 400, 403, 404 and 500 pass through the completion helper; any other status is answered 500
  "Unable to process your request at this time. Please try again later. If the issue persists, please contact
  support." [783 ing/util/RoutesCompletionHelper.scala:32-64]. A 401 produced inside the handler would therefore
  surface as 500 (inference).
- Success is judged per series from `result.code` (202 accepted), never from the HTTP status.

### 3.4 Per-series validation (source)

[783 ing/util/validation/ValidationUtil.scala:32-178; ing/common/IngestionServiceMessages.scala:25-36]

- Token present, and partition equal to the configured one (section 1.3).
- `points` not empty, otherwise 400 "No data point is posted in the request".
- Kind: an unknown kind is 400 `Unknown data type '<kind>' for '<timeseriesId>'`; a SET-STRING series needs every
  value to be an array of unique strings, otherwise 400 `Invalid Point value - ... input is not a SET`.
- Other kinds: each value's detected type must equal the kind; a JSON string is String, a decimal number Double, a
  boolean Boolean, an integral number Integer, and an Integer value is accepted for a Double series. Otherwise 400,
  for example "Invalid point value type. Expected Double, but invalid value(s): 'abc' of String is(are) found for
  timestamp(s): 978307200000" [ING: components.responses.400BadRequest example].
- DATETIME: the detector never yields the timestamp kind and the ISO-8601 check is commented out
  [783 ing/util/validation/ValidationUtil.scala:88-130, 167-178], so every point of a series whose kind maps to the
  timestamp kind appears to be rejected with 400 (inference; to be confirmed against a deployment, section 11).

### 3.5 Accept and publish (source)

- The points are turned into protobuf `WriteBackData` messages carrying the metadata id `{recordId}:{timeseriesId}`,
  the `requestId` and the `version` (as `requestStartTime`), split so each message stays below
  `cloud.<provider>.max-message-size` (8388608 bytes)
  [783 ing/service/TimeSeriesPublisherMessage.scala:52-153; ing/util/IngestionCommonsUtil.scala:50-51;
  ing/common/IngestionServiceConfiguration.scala:36-37; src/main/resources/local.conf:101-102]. Each message has the
  attribute `v` set to the version [783 ing/service/TimeSeriesPublisher.scala:34, 46-48].
- Publishing is started asynchronously and not awaited ("Fire-and-forget"); the answer is returned at once with 202
  [783 ing/handler/IngestionHandler.scala:74-95].
- A failed publish is retried immediately, without delay, up to `max-retries` (10), then only logged: "Retry limit
  exceeded for pub sub message publish" [783 ing/service/TimeSeriesPublisher.scala:78-105;
  src/main/resources/local.conf:101]. A 202 is therefore not proof that the points were stored.
- Messaging: provider `cloud.messaging` (default `azure-servicebus`, environment `MESSAGING_PROVIDER`; `gcp-pubsub` and
  `kafka` are configured too), producer `timeseries-data-gateway`, topic default
  `osdu-timeseries-publisher-gateway-dev` (environment `TIMESERIES_DATA_GATEWAY_TOPIC_NAME`)
  [783 src/main/resources/local.conf:82-133]; the core-plus chart defaults to `kafka`
  [783 devops/core-plus/deploy/values.yaml:23].
- The docs: "Time-series ingestion operations are not transactional. A successful status indicates that the operation
  has been `accepted for processing`, but the changes may not immediately be reflected in the time-series read APIs."
  [1525 docs/timeseries.md:230-232].

## 4. Payload and bulk data shapes

- Timestamps: Unix epoch milliseconds, int64 [ING: components.schemas.Point (property named `point`, section 10);
  QRY: components.schemas.Point]; negative values (before 1970) are allowed [1525 docs/timeseries.md:248-250].
- Values: number, string or boolean [ING: components.schemas.Point.value]; SET-STRING values are JSON arrays of unique
  strings [783 ing/util/validation/ValidationUtil.scala:60-69]. DATETIME values are ISO-8601 strings
  [1525 docs/timeseries.md:49] parsed with `ZonedDateTime.parse(...).toEpochSecond`, so they are stored at second
  precision [783 ing/service/TimeSeriesPublisherMessage.scala:433-449]. Integer kinds are stored as 64-bit longs
  [783 ing/service/TimeSeriesPublisherMessage.scala:44-50, 155-203].
- Units: no conversion. "In this initial version, the API expects numerical values to be provided in the unit of
  measurement specified by the `ProductionMetricValues[].unitOfMeasureId` attribute"; "The system currently stores
  numeric values raw without performing any Unit of Measure (UOM) conversions" [1525 docs/timeseries.md:206-214]. The
  request models have no unit field and the command's `unit` is fixed to `None`, with a code comment reserving it for
  future unit handling [783 ing/request/IngestTimeSeriesRequest.scala:26-27; ing/command/IngestTimeSeriesCommand.scala:34].
  The sender converts values to the series' `UnitOfMeasureID`.
- Batching: the source sets no limit on records, series or points per request; the only size setting is the 8 MB
  per bus message used for server-side chunking (section 3.5). The docs give "the maximum payload size supported is
  8 MB" and mark that figure as still to be verified [1525 docs/timeseries.md:216-218]. `local.conf` sets
  `max-content-length = infinite` for the HTTP client only; for the server it sets `request-timeout = infinite`,
  `max-connections = 25` and `pipelining-limit = 1`, and no request size [783 src/main/resources/local.conf:54-80].
  Bodies are kept under 8 MB until the target deployment's limit is known.

## 5. Identities and versions

- Record id: the ProductionValues Storage record id. Series id: `timeseriesId` = `DDMSDatasetID`.
- `version`: `System.currentTimeMillis()` when the command for that series is built, after the record lookup, so the
  series of one request carry different versions [783 ing/request/IngestTimeSeriesRequest.scala:31-37;
  ing/request/IngestTimeSeriesBatchTimeSeriesRequestItem.scala:37-43] [ING: ingestTimeSeriesBatchForSingleRecord 202
  example, versions 1781770405994, 1781770405997, 1781770405999]. The docs: "Versions are assigned by the server when
  the operation is accepted ... This is the time the system became aware of a given piece of data."
  [1525 docs/timeseries.md:234-240].
- `requestId`: a UUID derived from a SHA-256 of (recordId + timeseriesId, version)
  [783 ing/util/IngestionCommonsUtil.scala:31-35]; it is carried in logs and bus messages and is not returned in the
  response [783 ing/common/IngestTimeSeriesResponseSerializer.scala:73-98].
- Idempotency: none. No client-supplied request id is accepted and every accepted write is a new version; how
  overlapping points of successive versions combine is decided downstream, outside these projects. The docs promise
  that the latest version gives the most accurate data currently known and that the data as known at any earlier
  point in time stays queryable [1525 docs/timeseries.md:220-228]. The ledger prevents duplicate sends; any resend
  becomes a new version.

## 6. Reads, verification and deletes

### 6.1 Query operations

| operationId | Method and path | Parameters and body |
| --- | --- | --- |
| `getTimeSeries` | `GET /production-values/{recordId}/timeseries/{timeseriesId}` | `start`, `end` required; `version` optional |
| `getTimeSeriesByVersion` | `GET /production-values/{recordId}/timeseries/{timeseriesId}/versions/{version}` | `version` int64 path parameter; `start`, `end` required |
| `getLatestValue` | `GET /production-values/{recordId}/timeseries/{timeseriesId}/latest-value` | `at`, `version` optional |
| `getTimeSeriesBatchForSingleRecord` | `POST /production-values/{recordId}/timeseries` | `start`, `end` required, `version` optional; body `{"timeseries": ["<id>", ...]}` |
| `getLatestValueBatchForSingleRecord` | `POST /production-values/{recordId}/timeseries/latest-value` | `at`, `version` optional; same body |
| `getTimeSeriesBatch` | `POST /production-values/batch/timeseries` | `start`, `end` required, `version` optional; body `{"productionValues": [{"recordId": "...", "timeseries": ["<id>", ...]}]}` |
| `getLatestValueBatch` | `POST /production-values/batch/timeseries/latest-value` | `at`, `version` optional; same body |

All from [QRY: paths].

- `start` is inclusive and `end` exclusive, Unix epoch milliseconds [QRY: components.parameters.StartTimestamp,
  EndTimestamp]; both are validated as longs [790 qry/route/GetTimeSeriesRoute.scala:54-58].
- `version` omitted means the latest version [QRY: components.parameters.Version]. The single-series GET passes an
  absent version on as absent [790 qry/command/GetTimeSeriesCommand.scala:41]; the batch forms resolve an absent
  version to the current time and query every series with it [790 qry/command/GetTimeSeriesBatchCommand.scala:40, 60;
  qry/util/TimeSeriesRequestUtil.scala:59-63]. An absent or unparsable `at` becomes the current time
  [790 qry/util/TimeSeriesRequestUtil.scala:49-57; qry/command/GetTimeSeriesLatestValueCommand.scala:38].
- For the single-record POST an omitted or empty `timeseries` list returns every series of the record
  [QRY: getTimeSeriesBatchForSingleRecord, requestBody description]
  [790 qry/request/GetTimeSeriesBatchForSingleProductionValuesRequest.scala:19-20].
- Response items: `{recordId, reportingEntityId, timeseries: [{timeseriesId, version, start, end, metadata,
  pointsCount, points: [{timestamp, value}], result}], result}` [QRY: getTimeSeries 200 example]
  [790 qry/common/GetTimeSeriesResponseSerializer.scala:74-101]; latest-value items carry `timeseriesId`, `version`,
  `metadata`, `points` (one point) and `result` [QRY: getLatestValue 200 example]
  [790 qry/common/GetTimeSeriesResponseSerializer.scala:106-126].
- Status: the four batch forms answer 207 for any executed batch, with a record-level `result` of 207 as well
  [790 qry/pds/timeseries/TimeSeriesService.scala:65-144; qry/route/GetTimeSeriesBatchRoute.scala:145-146;
  qry/route/GetTimeSeriesLatestValueBatchRoute.scala:142-143]. The contract declares 200 only for the two
  single-record POSTs, and 200 or 207 for the two multi-record POSTs [QRY: paths].
- Until the downstream pipeline has created the stream mapping, a series answers 404 "Failed to get a Stream
  Mapping" [790 qry/pds/timeseries/TimeSeriesService.scala:222, 361]; this is expected right after ingestion.
- The query service fetches store pages through a cursor and stops after 100 pages per request
  (`MaximumPagesPerRequest`) [790 qry/pds/timeseries/TimeSeriesQueryService.scala:35, 87, 120-122]; the page size is
  set by the store. A result longer than 100 pages is cut without an error (inference from `source.limit`).
- Latest-value lookups search only the ten years before `at`: the range is `[at minus 10 years, at + 1)`
  [790 qry/pds/timeseries/TimeSeriesService.scala:168-169].
- The docs mention `GET /latestVersion` [1525 docs/timeseries.md:242]; neither contract has it and no route serves
  it; the internal `getLatestVersion` returns the current time, with a code comment saying the value should come from
  the store [790 qry/pds/timeseries/TimeSeriesService.scala:415-419].

### 6.2 Verification (inference)

After a series item answered 202, store its `version`, `start`, `end` and `pointsCount`, then poll
`GET .../timeseries/{timeseriesId}/versions/{version}?start={start}&end={end + 1}` (`end` is exclusive) until the item
answers 200 with the same `pointsCount`. Treat 404 "Failed to get a Stream Mapping" as pending, and bound the wait,
because a 202 can still be lost after the bus retries (section 3.5).

### 6.3 Deletes

- Neither contract has a delete operation [ING: paths; QRY: paths] and neither service registers one
  [783 ing/route/EndpointRouter.scala:60-83; 790 qry/route/EndpointRouter.scala:69-87]. The knowledge-sharing notes
  list DELETE as a capability of the redesigned API [1525 ks/20260127_030042.content.md:73-79;
  ks/20260227_030059.content.md:56-63].
- Consequence: points cannot be removed through the API; a correction is a new version. Only the ProductionValues
  record can be removed, at the reversible scope `POST /records/{id}:delete` [STORAGE: POST /records/{id}:delete,
  deleteRecord, "a logical deletion of the record ... can be reverted later"]. Points already ingested remain in the
  historian, so a live test cannot make them answer 404 and its log says so.

## 7. CSV parser workflow (file plus workflow)

### 7.1 Purpose and sequence

- "PDDMS supports CSV ingestion `pddms-csv-parser` Airflow workflow based on standard OSDU `csv-parser` workflow"
  [1525 csvwf/README.md:3]; the parser is a fork of the OSDU csv-parser that "Ingests multiple time-series from CSV
  files to Production DDMS" [1486 README.md:3]; the flow is upload the CSV, store metadata, trigger the workflow, run
  the Airflow job, parse, call the ingestion APIs [1525 ks/20260119_111734.content.md:136-151].
- Workflow name: `pddms-csv-parser` [1486 airflowdags/pddms-csv-parser.py:49; .gitlab-ci.yml:46]
  [1525 csvwf/collection, "6. Trigger the Workflow", path variable `workflow_name`].
- Sequence, from the "CSV Ingestion" folder of [1525 csvwf/collection], with the matching core operations
  (the core servers are `/api/file/` and `/api/workflow` [FILE: servers; WORKFLOW: servers]):
  1. Optional pre-checks: "1. Validate metadata", `POST {{ingestionBaseUrl}}/validate/csv/metadata` with the file
     record as JSON, and "2. Validate CSV", `POST {{ingestionBaseUrl}}/validate-csv` with multipart parts `metadata`
     and `csv`. Neither path is in `ING`; the service's own routes are different (section 7.6).
  2. "3. Get upload URL": `GET https://{{FILE_HOST}}/files/uploadURL` with `data-partition-id`
     [FILE: GET /v2/files/uploadURL, getLocationFile]; the test script reads `Location.SignedURL` and
     `Location.FileSource` (the contract types `Location` as a string map [FILE: components.schemas.LocationResponse]).
  3. "4. Upload file": `PUT {signed URL}` with the CSV and `x-ms-blob-type: BlockBlob` (Azure); 200 or 201 expected.
  4. "5. Add metadata": `POST https://{{FILE_HOST}}/files/metadata` [FILE: POST /v2/files/metadata, postFilesMetadata]
     with a `{partition}:wks:dataset--File.Generic:1.0.0` record: `acl`, `legal`, `data.Name`,
     `data.DatasetProperties.FileSourceInfo{FileSource, FileSize, ...}` and the descriptor in
     `data.ExtensionProperties.FileContentsDetails` (section 7.5). The contract answers 201 with `id`
     [FILE: components.schemas.FileMetadataResponse]; the script accepts 200 or 201 and keeps `id` as the file record
     id.
  5. "6. Trigger the Workflow": `POST https://{{WORKFLOW_HOST}}/workflow/pddms-csv-parser/workflowRun`
     [WORKFLOW: POST /v1/workflow/{workflow_name}/workflowRun, triggerWorkflow] with
     `{"executionContext": {"id": "<file record id>", "dataPartitionId": "<partition>"}}`; the contract body also
     allows an explicit `runId` [WORKFLOW: components.schemas.TriggerWorkflowRequest] and the call requires
     `service.workflow.creator` [WORKFLOW: triggerWorkflow, description]. The script keeps `runId` from the response
     [WORKFLOW: components.schemas.WorkflowRunResponse].
  6. "7. Workflow status": `GET https://{{WORKFLOW_HOST}}/workflow/pddms-csv-parser/workflowRun/{runId}`
     [WORKFLOW: getWorkflowRunById]. The contract's `status` values are INPROGRESS, PARTIAL_SUCCESS, SUCCESS, FAILED
     and SUBMITTED [WORKFLOW: components.schemas.WorkflowRunResponse]; the script expects `finished` or `running`.
  7. "8. Verify recently ingested timeseries": the M25 collection still calls the legacy `{{timeseriesBaseUrl}}/batch`
     and expects 207; with the ProductionValues model the check is section 6.

### 7.2 What the DAG receives and runs

[1486 airflowdags/pddms-csv-parser.py:12-128]

- From `dag_run.conf`: `execution_context.id` (the file record id), `execution_context.dataPartitionId`,
  `execution_context.data_service_to_use` (default `file`), `execution_context.userId`, `authToken`, and `run_id`
  (read but not passed on) [lines 39-46]. The translation of the API's `executionContext` into `dag_run.conf` is done
  by the Workflow service, outside these projects.
- One `KubernetesPodOperator` step runs the parser image with a single JSON argument `{"id", "authorization",
  "dataPartitionId", "steps": ["LOAD_FROM_CSV", "TIME_SERIES"], "dataServiceName", "userId"}` [lines 45, 55-62,
  114-125]. No run id is passed, so the parser's `IngestionRequest.runId` stays empty (inference)
  [1486 csv/ingestion/model/IngestionRequest.java:28-30].
- Default arguments set `retries: 1` and `retry_delay` five minutes [lines 12-21]: a failed parser step runs once more
  (inference from Airflow's semantics), and points it had already sent are sent again as new versions (inference).
- Status: `update_status_running` runs before the parser [lines 110-112, 128]; the success and failure callbacks only
  construct `UpdateStatusOperator` objects [lines 26-36] and the final `update_status_finished` step is commented out
  [line 128]. Whether the run reaches a terminal status depends on `update_default_args` from `osdu_airflow`, which was
  not examined (inference).
- Environment [lines 66-85]: Storage, Schema, Search, Partition, Unit and File endpoints on `https://{azure_dns_host}`;
  `TIME_SERIES_SERVICE_ENDPOINT` = `https://{azure_dns_host}/api/pddms/writeback/service/timeseries-management/v1`;
  `TIMESERIES_INGESTION_CHUNK_SIZE`, `..._MAX_ATTEMPTS`, `..._BACKOFF_DELAY`, `..._BACKOFF_MULTIPLIER` from Airflow
  variables. It sets neither `PRODUCTION_VALUES_SERVICE_ENDPOINT` nor `DESCRIPTOR_PRODUCTION_VALUES_SOURCE_ENABLED`.

### 7.3 Parser flow

- The descriptor is read with `GET {FILE_SERVICE_ENDPOINT}/files/{id}/metadata` [FILE: getFileMetadataById] and the
  CSV through `GET /files/{id}/downloadURL` (`SignedUrl`) and the signed URL [FILE: downloadURL]; each call is made
  at most `MAX_ATTEMPTS` (2) times, `DELAY` (2000 ms) apart [1486 csv/file/FileService.java:37-117;
  azure.properties:34, 65-66].
- Pre-validation: the whole CSV is loaded into memory and posted with the descriptor to
  `{TIME_SERIES_SERVICE_ENDPOINT}/validate-csv` as multipart parts `csv` (`file.csv`) and `metadata`
  (`metadata.json`); any failure is only logged [1486 csv/writeback/WriteBackService.java:32-61;
  csv/writeback/WriteBackClient.java:42-68; csv/CsvIngestionRunner.java:57-64].
- Only the steps `LOAD_FROM_CSV` and `TIME_SERIES` run (the default flow also has the Storage, kind and id steps)
  [1486 csv/flow/model/StepType.java:49-51], so this DAG creates no Storage records.
- `TIME_SERIES` uses `ProductionValuesTimeSeriesHandler` when `DESCRIPTOR_PRODUCTION_VALUES_SOURCE_ENABLED=true`, and
  otherwise the legacy `TimeSeriesHandler`, which posts to `{TIME_SERIES_SERVICE_ENDPOINT}/batch/{source}` (the entity,
  property and source model) [1486 csv/handler/HandlerProvider.java:18, 77-78; csv/handler/HandlerConfig.java:119-137;
  csv/timeseries/TimeSeriesClient.java:48-64]. The switch defaults to `false` in code and in the Azure properties
  [1486 csv/handler/HandlerProvider.java:18; azure.properties:36-39]; the core-plus config map sets none of these
  values [1486 devops/core-plus/deploy/templates/configmap.yaml:7-23].
- A row that fails in a handler is logged and skipped; a handler's completion failure ends the process with
  `System.exit(1)`; an ingestion failure publishes a job status with the error and is rethrown
  [1486 csv/ingestion/IngestionService.java:54-66, 76-89; csv/CsvIngestionRunner.java:65-73].

### 7.4 ProductionValues handler

[1486 csv/handler/handlers/ProductionValuesTimeSeriesHandler.java]

- Per row [lines 60-105]: the asset is the value of `TimeSeries.AssetSourceColumn`, the timestamp the value of
  `Points.SourceColumn`; each other column is matched (case-insensitively) to a `Columns` entry and then to the
  `ProductionValues` target whose `AssetName` equals the asset; points accumulate in memory per
  (`TargetProductionValuesId`, `TargetDDMSDatasetId`). Any exception drops the row with a SEVERE log line.
- Values [lines 114-153]: DOUBLE is parsed after removing commas; INTEGER and LONG are parsed as doubles and truncated;
  BOOLEAN accepts YES, TRUE, Y, 1 and NO, FALSE, N, 0 (anything else is skipped); blank or `NULL` cells are skipped;
  unparsable numbers are logged and skipped; any other type is sent as the cell's string.
  `SourceUnitOfMeasureId` is not used: no unit conversion happens here.
- Timestamps [lines 155-195]: when `Points.ValueType` is not DATETIME (that is, TIMESTAMP) the cell is parsed with
  `Long.parseLong` as epoch milliseconds; DATETIME uses `Points.Format` (a Java `DateTimeFormatter` pattern) and
  `Points.TimezoneOffset` (default `+00:00`; a missing sign becomes `+`, `.` becomes `:`); a format without an hour
  field yields the start of the day at that offset.
- Sending [lines 52-53, 197-222]: at completion the records are sent in chunks of `TIMESERIES_INGESTION_CHUNK_SIZE`
  ProductionValues records, each record with all of its points, as
  `POST {PRODUCTION_VALUES_SERVICE_ENDPOINT}/api/pddms/ingest/v1/production-values/batch/timeseries` with
  `{"productionValues": [{"recordId", "timeseries": [{"timeseriesId", "points": [{"timestamp", "value"}]}]}]}`
  [1486 csv/timeseries/ProductionValuesTimeSeriesClient.java:42-65; csv/timeseries/model/ProductionValuesBatchRequest.java:14;
  csv/timeseries/model/ProductionValuesRecordTimeSeries.java:14-17; csv/timeseries/model/ProductionValuesTimeSeriesEntry.java:14-17;
  csv/timeseries/model/ProductionValuesTimeSeriesPoint.java:13-16]. `PRODUCTION_VALUES_SERVICE_ENDPOINT` is therefore
  the host root, and it is injected without a default [1486 csv/timeseries/ProductionValuesProperties.java:13].
- Retries: `@Retryable` on `HttpServerErrorException`, `ResourceAccessException` and `IngestionException` with
  `TIMESERIES_INGESTION_MAX_ATTEMPTS`, `..._BACKOFF_DELAY` and `..._BACKOFF_MULTIPLIER`, which have no defaults in the
  repository [1486 csv/timeseries/ProductionValuesTimeSeriesClient.java:37-40; azure.properties:72-76]. Every
  `RestClientException`, 4xx included, is rethrown as `IngestionException` [lines 53-60 of the client], so client
  errors are retried too (inference).
- The response is read as `Void` [line 55 of the client], so per-series failures inside a 207 body are not detected.
  A chunk that still fails stops the process; chunks already sent stay ingested (inference from lines 213-221 and
  section 7.3).

### 7.5 Descriptor for the ProductionValues path

From [1525 demo/templates/metadata-productionvalues.json:1-54; demo/metadata.py:81-99, 128-140;
demo/utils/productionvalues.py:103-115] and the parser's model [1486 csv/descriptor/model/TimeSeriesMetadata.java:17-24
(`Values`, with `Columns` as an alias); csv/descriptor/model/TimeSeriesDetails.java:18-30;
csv/descriptor/model/PointDetails.java:17-26; csv/descriptor/model/productionvalues/ProductionValuesTarget.java:17-23;
csv/descriptor/model/FileContentsDetails.java:34-61]:

```json
"ExtensionProperties": {
  "Name": "File",
  "FileContentsDetails": {
    "HeaderRowIndex": 1,
    "TargetKind": "osdu:wks:work-product-component--ProductionValues:1.0.0",
    "nestedFieldDelimiter": ".",
    "FileType": "csv",
    "TimeSeries": {
      "AssetSourceColumn": "<asset column>",
      "Points": {
        "SourceColumn": "<timestamp column>",
        "ValueType": "DATETIME",
        "Format": "<DateTimeFormatter pattern>",
        "TimezoneOffset": "+08:00"
      },
      "Columns": [
        {
          "SourceColumn": "<csv column>",
          "ValueType": "DOUBLE",
          "SourceUnitOfMeasureId": "{partition}:reference-data--UnitOfMeasure:bbl%2Fd",
          "ProductionValues": [
            {
              "AssetName": "<asset value in AssetSourceColumn>",
              "TargetProductionValuesId": "{partition}:work-product-component--ProductionValues:<uuid>",
              "TargetDDMSDatasetId": "<DDMSDatasetID>"
            }
          ]
        }
      ]
    }
  }
}
```

- For TIMESTAMP files the demo sets `Points.ValueType` to `TIMESTAMP` and removes `Format` and `TimezoneOffset`
  [1525 demo/metadata.py:137-140]; it adds `SourceUnitOfMeasureId` only when a unit is mapped
  [1525 demo/metadata.py:84-87].
- The M25 example files use the legacy descriptor: `Entities{SourceColumn, Values[{SourceValue, TargetStorageRecordId}]}`
  and `TimeSeries.Values[{SourceColumn, SourceUnitOfMeasureId, TargetPropertyDescriptor, ValueType}]`, with
  `Points.ValueType` `TIMESTAMP` [1525 csvwf/files/*.metadata.json]; that is the "Legacy | Entity / Property / Source
  based" stage of the API evolution [1525 ks/20260127_030042.content.md:31-37].
- Value types of the workflow ("M26 GLAB" column): DOUBLE, INTEGER, LONG, BOOLEAN, STRING, SET-STRING, DATETIME
  [1525 csvwf/README.md:9-17].
- Preconditions: "Before running the workflow all the entities records must exist in the OSDU Storage and registered
  in PDDMS." [1525 csvwf/README.md:31]; the registration is the legacy entity API that the M25 collection calls
  (`POST {{entityBaseUrl}}/entities/register`, folder "Create Entities") [1525 csvwf/collection]. With the
  ProductionValues handler the records named in the descriptor must exist; the demo pipeline creates them before it
  ingests (and ingests through the API directly) [1525 demo/run_pipeline.py:13; demo/ingest.py:120].
- Observability: the run status plus the Airflow logs ("Logs available in Airflow UI")
  [1525 ks/20241218_143141.content.md:57]; dropped rows and values appear only as log lines (section 7.4).

### 7.6 The ingestion service's validation routes

Not in `ING` paths, but served inside `requireRole(Admin, Editor)` [783 ing/route/EndpointRouter.scala:70-78]:

- `POST /v1/workflow/validate`: multipart parts `metadata` (a `.json` file) and `csv` (a `.csv` file)
  [783 ing/route/CsvWorkflowValidateRoute.scala:45-113; ing/util/validation/FileValidator.scala:13-16].
- `POST /v1/workflow/validate/metadata`: JSON `ValidationRequest` [783 ing/route/CsvWorkflowValidateMetadataRoute.scala:39].
- The metadata validator requires a kind matching `*:*wks*:*dataset--File.Generic*:n.n.n`; non-empty owners and
  viewers matching `^data\.[...]@domain`; non-empty `legal.legaltags`; `FileSourceInfo.FileSource`; a numeric
  `FileSize`; `FileContentsDetails.Entities{SourceColumn, Values}`; `TimeSeries.Points` with `ValueType` DATETIME (a
  valid `Format` and a `TimezoneOffset` that is `Z` or a signed `hh:mm` up to `14:00`) or TIMESTAMP; and `TimeSeries.Values[]`
  with `TargetPropertyDescriptor` and `ValueType`; the check of the `ValueType` against the allowed list is commented
  out [783 ing/util/validation/MetadataValidator.scala:19-235; ing/request/ValidationRequest.scala:20-88]. These are
  the legacy descriptor fields; a ProductionValues descriptor (`AssetSourceColumn`, `Columns`, no `Entities`) fails
  them (inference).

## 8. Limits and errors

| Item | Value | Source |
| --- | --- | --- |
| bus message size (server-side chunking) | 8388608 bytes | [783 src/main/resources/local.conf:101-102] |
| publish retries | 10, immediate | [783 ing/service/TimeSeriesPublisher.scala:78-105] |
| request body | not set in `local.conf`; docs give 8 MB, unverified | [783 src/main/resources/local.conf:54-80; 1525 docs/timeseries.md:216-218] |
| server connections | `max-connections = 25`, `pipelining-limit = 1`, no request timeout | [783 src/main/resources/local.conf:63-71] |
| Storage lookup breaker | 30 s call timeout, 5 failures, 1 minute reset | [783 src/main/resources/local.conf:40-47] |
| partitions per ingestion deployment | one (`DATA_PARTITION_ID`) | [783 src/main/resources/local.conf:155-156] |
| query pages per request | 100 | [790 qry/pds/timeseries/TimeSeriesQueryService.scala:35] |
| latest-value look-back | 10 years | [790 qry/pds/timeseries/TimeSeriesService.scala:168-169] |
| CSV parser chunking and retries | `TIMESERIES_INGESTION_*` Airflow variables, no defaults | [1486 airflowdags/pddms-csv-parser.py:81-84; azure.properties:72-76] |

Errors use `AppError` `{"result": {"code", "reason", "message"}}` [ING, QRY: components.schemas.AppError,
components.responses]; per-item errors use the item shapes of section 3.3. The ingestion service adds the response
headers `X-Frame-Options`, `X-XSS-Protection`, `Cache-Control`, `X-Content-Type-Options` and
`Content-Security-Policy` [783 ing/util/RoutesCompletionHelper.scala:99-106].

## 9. What the route types need

### 9.1 `timeSeries` (historian points)

Step A is the ordinary Storage record route: kind `work-product-component--ProductionValues:2.0.0`, one
`ProductionMetricValues` entry per series with `DDMSDatasetID`, `ParameterKindID` and `UnitOfMeasureID`,
`ReportingEntityID` pointing at existing master data, and `Source`. Step B sends points. Parameters:

- Ingestion base (`/api/pddms/ingest/v1` per the contract) and query base (`/api/pddms/query/v1`), both per
  environment; the data partition, which must equal the ingestion deployment's `DATA_PARTITION_ID`; a credential
  reference for an identity in `service.pddms.editor` or `service.pddms.admin` (and `service.pddms.viewer` or better
  for read back) that can also read the ProductionValues record.
- Record id (from step A); `timeseriesId` (= `DDMSDatasetID`); timestamp column with its source format and timezone,
  converted to epoch milliseconds; value column; value type, which must match `ParameterKindID`; unit handling (the
  engine converts to `UnitOfMeasureID`, since the service stores values raw).
- Operation (single series, single-record batch, multi-record batch) and chunking of records, series and points so
  that each body stays under 8 MB until the deployment's limit is known.
- Optional `trace-id` per request for correlation (echoed as `CorrelationId`).
- Ledger per series: `version`, `start`, `end`, `pointsCount` and the item `result`; the outcome is judged per series
  from `result.code` (202 accepted), matched by position when `timeseriesId` is missing, never from the HTTP status
  (207 is normal for batches). `requestId` is not returned.
- Verification: section 6.2, bounded by a timeout.
- Idempotency: none on the server; the ledger prevents duplicate sends, and any resend is a new version.
- Delete: not available for points. Cleanup can only remove the ProductionValues record with
  `POST /records/{id}:delete`; points already ingested remain, and the live-test log says so.
- DATETIME series are not delivered until the validation behaviour of section 3.4 is confirmed.

### 9.2 `workflow` (PDMS CSV parser)

- File service base, Workflow service base, workflow name `pddms-csv-parser`, data partition, credential reference
  (also `service.workflow.creator` for the trigger).
- File record: kind `{partition}:wks:dataset--File.Generic:1.0.0`, ACL, legal, `Name`, `FileSource` (from
  `uploadURL`), `FileSize`, and the descriptor of section 7.5.
- Trigger body `{"executionContext": {"id", "dataPartitionId"}}`; run status polling interval and timeout, with the
  status values of the core contract.
- Deployment prerequisites outside the engine's control: `DESCRIPTOR_PRODUCTION_VALUES_SOURCE_ENABLED=true`,
  `PRODUCTION_VALUES_SERVICE_ENDPOINT` and the `TIMESERIES_INGESTION_*` variables; without the switch the legacy
  handler and API are used.
- Ledger ids: the file record id (the `dataset--File.Generic` record, logged and removed like any other id) and the
  workflow run id. No versions, per-series results or per-row outcomes come back, partial failures are not detected
  by the parser, and a retried parser step resends points, so completeness can only be proven by query read back.
  This path gives weaker per-record traceability than 9.1.

## 10. Contract versus code

1. `ING` `components.schemas.Point` requires `point` and `value`; every `ING` example, `QRY`, the docs and the source
   use `timestamp` [783 ing/request/Point.scala:20]. Send `timestamp`.
2. `ING` `IngestTimeSeriesRequest` and `IngestTimeSeriesBatchTimeSeriesRequestItem` list an optional `unit`; the source
   request models have none and the command's unit is `None` (section 4); the `/info` example's commit message reads
   "Update : Remove unit from response and request" [ING: getDetailedVersionInfo example, line 50].
3. `ING` `IngestTimeSeriesResponse` requires `count` and `points`; the serializer writes `pointsCount` and no points
   (section 3.3), as the `ING` examples do.
4. `QRY` `GetTimeSeriesResponse` requires `count` and lists `unit`; the serializer writes `pointsCount` and no `unit`
   [790 qry/common/GetTimeSeriesResponseSerializer.scala:88-98], as the `QRY` examples do.
5. `ING` documents 202 for both batch operations and 207 only for partial success, with record-level `result` 202 in
   the all-accepted examples; the source answers 207 for every executed batch, at route and record level. `QRY`
   declares 200 only for the two single-record POSTs and 200 or 207 for the two multi-record POSTs; the source answers
   207 for all four (sections 3.3 and 6.1).
6. A per-series validation failure has no `timeseriesId` in the source; the `ING` 207 example shows exactly that item
   (lines 377-382) without explaining it, while the schema `TimeSeriesErrorResponse` requires `timeseriesId`
   [ING: components.schemas.TimeSeriesErrorResponse].
7. `ING` defines `ValidationRequest`, `ErrorPropResponse` and related schemas but no path; the service exposes
   `/v1/workflow/validate` and `/v1/workflow/validate/metadata` (section 7.6). The contract's `Legal.legalTags` is
   `legaltags` in the service model, and its `FileContentsDetail` (`FileContentTypeID`, `FrameOfReference`) differs
   from the service model (`HeaderRowIndex`, `Entities`, `TimeSeries`, `TargetKind`, `NestedFieldDelimiter`,
   `FileType`) [ING: components.schemas.Legal, FileContentsDetail] [783 ing/request/ValidationRequest.scala:30-34, 70-77].
8. The `ING` request example of `ingestTimeSeriesBatchForSingleRecord` ends with a point that has a `timestamp` and no
   `value` (file line 245).
9. ProductionValues kind version: docs `2.0.0` and `ING` "v2.0.0+"; the demo writes `1.3.0`
   [1525 demo/utils/productionvalues.py:49]; descriptors carry `TargetKind ...ProductionValues:1.0.0`, which the two
   DAG steps do not use.
10. `1525 docs/timeseries.md` refers to `GET /latestVersion`, which does not exist (section 6.1).
11. Gateway bases: the contracts use `/api/pddms/ingest/v1` and `/api/pddms/query/v1`; the M27 Postman collections use
    `/writeback/service/timeseries-management/v1` and `/ppstimeseries/service/timeseries/v1/ts` under `PDDMS_HOST`,
    and the DAG validates against `/api/pddms/writeback/service/timeseries-management/v1` (sections 1 and 7.2).
12. The CSV parser posts its pre-validation to `{TIME_SERIES_SERVICE_ENDPOINT}/validate-csv` and the M25 collection
    posts to `/validate/csv/metadata` and `/validate-csv`, while the ingestion service serves `/v1/workflow/validate`
    and `/v1/workflow/validate/metadata`; its validator requires the legacy `Entities` and `TargetPropertyDescriptor`
    fields that a ProductionValues descriptor does not carry. The parser only logs the outcome.
13. The M25 status check expects `finished` or `running`; the core Workflow contract enumerates INPROGRESS,
    PARTIAL_SUCCESS, SUCCESS, FAILED and SUBMITTED (section 7.1).
14. Documentation differences: INTEGER is "64-bit" in `docs/timeseries.md` and "32-bit" in `csvwf/README.md`
    [1525 docs/timeseries.md:46; csvwf/README.md:12]; the knowledge-sharing notes list "Unit conversion during
    ingestion" [1525 ks/20260119_111734.content.md:154-158] while the docs and the code convert nothing (section 4);
    the demo client treats only 200 and 202 as success for the batch ingest [1525 demo/utils/client.py:183] while the
    service answers 207.

## 11. Open questions

- DATETIME ingestion: whether `ParameterKind.parse` maps `ParameterKind:Timestamp:` to the timestamp kind, and so
  whether every DATETIME point is refused (section 3.4). Test against the target deployment.
- The request body limit of the ingestion server and of the gateway (section 4).
- The downstream publisher and stream store: how overlapping points of successive versions combine, how long the
  stream mapping takes to appear, and the store's page size (sections 5 and 6).
- Whether `Source` is validated anywhere (section 2), and the exact error bodies and role-failure status produced by
  the shared libraries (section 1.4).
- The ingestion handler assigns the caller's token to a shared configuration field (`serviceConfig.userAuthToken`)
  [783 ing/handler/IngestionHandler.scala:55]; what reads that field, and whether concurrent requests can see each
  other's token, was not examined.
- CSV parser deployment: how the deployed DAG supplies `PRODUCTION_VALUES_SERVICE_ENDPOINT` and the
  `DESCRIPTOR_PRODUCTION_VALUES_SOURCE_ENABLED` switch, the values of the `TIMESERIES_INGESTION_*` variables, and
  whether the run reaches a terminal status (section 7.2).
- A delete for points is planned in the redesign notes only (section 6.3).

## 12. Relation to DSPDM

The historian and DSPDM (`dspdm-services`, project 1245) are different products under the same
`domain-data-mgmt-services/production` group. The historian's knowledge-sharing notes describe the Halliburton DDMS as
a "Separate implementation", "Relational model", "Fixed schema", "Independent but may converge in future"
[1525 ks/20260119_111734.content.md:162-170]. The historian's series are defined by OSDU `ProductionValues` records and
stored in a time series store; DSPDM keeps relational rows in its own databases. The route types share nothing but the
partition and credential; see `osdu/specs/production-dspdm/INTEGRATION.md`.
