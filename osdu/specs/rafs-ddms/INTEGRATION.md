# Rock and Fluid Sample DDMS: integration brief

The Rock and Fluid Sample DDMS (RAFS DDMS, API v2) is a Python FastAPI service that writes rock and fluid sample
records (sample master data and work product components) to the Storage service, stores the tabular analysis content
of those records as parquet files, and serves both back with filtering and search. OSDU Delivery reaches it through
the DDMS routes of stage 7 of `docs/osdu-coverage-plan.md`, that is the `ddms` route type of
`docs/interfaces-design.md` section 5.4 with a shape for this service. This brief lists every call such a route makes,
with the contract or code it rests on, the payloads, the identities the ledger must keep, how to verify and remove
what was written, how RAFS compares with the Wellbore DDMS v3 shape, where the contract and the code disagree, and
what is still open.

## Sources

| Key | What it is | Revision | How it is cited |
| --- | --- | --- | --- |
| `C` | `osdu/specs/rafs-ddms/openapi.yaml`: the RAFS OpenAPI 3.0.3 contract (`info.title` "Rock and Fluid Sample DDMS", `info.version` 0.2.0), a byte-identical copy (SHA-256 `ebf50fcff6d80eb52b442102c0442b8472fa8e2e82de8d2d7f85b3c127cabc2c`) of `docs/api/community/v2/openapi.yaml` in project 1415 | commit `585258198bf42416dd1e8f5fdf61dc3898b6ce93` (2026-09-10), per `osdu/specs/sources.json` | contract: `[C: METHOD path, operationId]` or `[C: component Name]`. Paths of `C` are written relative to `/api/rafs-ddms` |
| `1415` | project 1415, `osdu/platform/domain-data-mgmt-services/rock-and-fluid-sample/rafs-ddms-services`, branch `main` | commit `585258198bf42416dd1e8f5fdf61dc3898b6ce93` ("Merge branch 'feature/namespace-raf-ddms' into 'main'", committed 2026-09-10T19:04:11-06:00), the head of `main` on 2026-09-16 and the commit `C` was copied from | source: `[1415 path:lines]` |
| `WB` | `osdu/specs/wellbore-ddms/openapi.json`: the Wellbore DDMS OpenAPI 3.1.0 contract (`info.version` 0.29), a byte-identical copy of `docs/api/community/v1/openapi.json` in project 98, `osdu/platform/domain-data-mgmt-services/wellbore/wellbore-domain-services` | commit `e5641a0e4e2dde1c67c12eb916087b67e8087c43` (2026-09-10), per `osdu/specs/sources.json` | contract: `[WB: METHOD path, operationId]` or `[WB: component Name]` |
| `ST`, `DS`, `SS`, `SE`, `EN` | the Storage, Dataset, Schema, Search and Entitlements contracts of the core specification set, copied under `osdu/specs/core/storage`, `dataset`, `schema_service`, `search` and `entitlements` (`openapi.yaml` in each) | copied from the core specification set on 2026-09-16; `sources.json` records no commit for them | contract: `[ST: METHOD path, operationId]`. Their servers are `/api/storage/v2/`, `/api/dataset/v1/`, `/api/schema-service/v1`, `/api/search/v2/` and `/api/entitlements/v2` |

Pinned runtime of project 1415: FastAPI 0.121.1, pydantic 1.10.18, pandas 2.2.0, pandera 0.25.0, pyarrow 14.0.2,
httpx 0.26.0 [1415 requirements.txt:83,104,205,207,224,228].

How statements are marked:

- A statement cited to `C`, `WB`, `ST`, `DS`, `SS`, `SE` or `EN` is what that pinned file says.
- A statement cited to `1415` is what the code, its tests, its charts or its documents show at the pinned commit. It is
  source, not a published contract. Where `C` and the code disagree, the code and its tests describe what the service
  does, and section 8 lists the difference.
- *(inference)* marks a conclusion drawn from the cited files that they do not state themselves.
- Inside one bracket, a path after a semicolon belongs to the same key as the path before it.

Path aliases used in `1415` citations:

| Alias | Full path |
| --- | --- |
| `deps/` | `app/api/dependencies/` |
| `routes/` | `app/api/routes/` |
| `errors/` | `app/api/errors/` |
| `settings/` | `app/core/settings/` |
| `services/` | `app/services/` |
| `clients/` | `app/services/osdu_clients/` |
| `schemas/` | `app/models/data_schemas/` |
| `ut/` | `tests/test_api/test_routes/` |
| `it/` | `tests/integration/` |

Operation ids in `C` follow FastAPI's pattern (`post_records_api_rafs_ddms_v2_masterdata_post`). Where the text cites
a family of them it writes the function prefix, for example `get_record_*`; Appendix A lists every v2 operation with
its full id.

Key findings:

1. Records go to `POST /api/rafs-ddms/v2/{collection}` as a JSON array of whole Storage records, the same for all seven
   collections. RAFS checks kinds, JSON schema, mandatory references and referential integrity, then forwards the
   array to Storage `PUT /records`. The response is camelCase on the wire (`recordCount`, `recordIdVersions`,
   `skippedRecordCount`); `C` documents snake_case names.
2. Five collections take tabular content: `POST .../{record_id}/data` for one content type, and
   `POST .../{record_id}/data/{content_type}` for several. One synchronous request carries the whole table; there
   are no sessions. The required query `content_schema_version` selects a content model compiled into the service;
   *(inference, section 2.4)* no content schema is fetched from the Schema service.
3. In the default (dataset) mode each content write creates or re-versions a `dataset--File.Generic` record, writes a
   URN into the parent's `data.DDMSDatasets` and writes a new parent version. The response is only
   `{"ddms_urn": "..."}`; the content id every read needs is inside the URN.
4. `DELETE` is a logical delete. There is no purge route, and the dataset records minted by content writes are not
   deleted with their parent.
5. RAFS shares the record upsert and the single-type content path with the Wellbore DDMS v3 shape, but not its
   sessions, its responses, its content reads or its delete (section 7).

## 1. Base path, versions, headers and auth

### 1.1 Base path

- `C` has no `servers` entry; every path starts with `/api/rafs-ddms` [C]. The host comes from configuration.
- The app mounts `{OPENAPI_PREFIX}` (info, healthz, readiness), `{OPENAPI_PREFIX}/v2` and `{OPENAPI_PREFIX}/dev`
  [1415 app/main.py:98-100; routes/api.py:19-22].
- The code's default prefix is `/api/os-rafs-ddms` [1415 settings/app.py:34], which the unit tests use
  [1415 ut/osdu/storage_mock_objects.py:426-430]. The Azure and core-plus charts set `/api/rafs-ddms`
  [1415 devops/azure/values.yaml:24,47; devops/core-plus/deploy/values.yaml:72], and the committed contract is
  generated with `OPENAPI_PREFIX=/api/rafs-ddms` and checked against the generated schema in CI
  [1415 README.md:90-107; scripts/generate_openapi.py:53-57; scripts/check_openapi_sync.py:76-99;
  devops/osdu/.rafs.gitlab-ci.yml:25-34].
- The service reads the API version and the collection from positions 3 and 4 of `request.url.path.split("/")`
  [1415 routes/utils/api_version.py:16-23; routes/utils/records.py:293-297], and uses them to pick the content model
  [1415 deps/validation.py:445-449] and to build URNs and blob names [1415 routes/v2/data/endpoints.py:214-218].
  *(inference)* The path the service receives must therefore be `/<segment>/<segment>/v2/<collection>/...`;
  `/api/rafs-ddms` satisfies this, and a gateway that rewrites the prefix to another number of segments breaks the
  model lookup.

### 1.2 Versions

| Version | Value | Citation |
| --- | --- | --- |
| API route groups | `v2` (use this). `dev` holds four experimental SamplesAnalysis operations (tag `dev-samplesanalysis`) and a hidden `GET /dev/trigger-sa-indexer`. No v1 router is mounted; the tutorial calls v1 deprecated. | [C; 1415 app/main.py:98-100; app/dev/api/routes/v_dev/api.py:26-38; app/dev/api/routes/v_dev/index/endpoints.py:230-237; docs/tutorial/README.md:211-213] |
| Service version | `0.2.0` | [C: info.version; 1415 settings/app.py:30] |
| Record kind version | any `major.minor.patch` of an accepted kind base (section 2.1) | [1415 deps/validation.py:196-228; routes/utils/records.py:225-252] |
| Content schema version | query `content_schema_version`, for example `1.0.0`. `C` marks it required on all 29 content, search and schema operations. | [C; 1415 app/custom_openapi.py:239-251] |

Content schema version rules [1415 deps/request.py:123-165; deps/schema_version.py:22-65]:

- The query parameter wins. It must match `^\d+\.\d+(?:\.\d+)?$`, otherwise 422 [1415 deps/schema_version.py:29-38].
- Without it, the deprecated form `Accept: <type>;version=x.y.z` is parsed. The type must be `application/json`,
  `application/x-parquet` or `*/*` and a version must be present, otherwise 406
  [1415 deps/schema_version.py:22,41-59]. The response then carries `Deprecation: true`,
  `Sunset: Sun, 31 May 2026 00:00:00 GMT` and `X-Deprecation-Notice` [1415 deps/schema_version.py:24-27,62-65]. The
  code at the pinned commit still accepts this form after the stated sunset.
- With neither, the response is 406 "No schema version provided. ..." [1415 deps/request.py:154-160].
- The route always sends the query parameter.

### 1.3 Headers

| Header | Applies to | Rule | Citation |
| --- | --- | --- | --- |
| `Authorization: Bearer <token>` | every v2 and dev operation | security scheme `HTTPBearer` in `C`; FastAPI `HTTPBearer()` in the code. A missing header, or one that is not a bearer credential, gets 403 with `reason` "Not authenticated" in the unit and integration tests; a token the core services reject gets their 401, passed through (`reason` "Access denied" in the integration tests). | [C: component securitySchemes.HTTPBearer; 1415 deps/auth.py:22-32; ut/osdu/storage_records_test.py:1095-1101; ut/osdu/storage_mock_objects.py:49-52; it/tests/depth_shift/test_depth_shift_negative_headers.py:27-115] |
| `data-partition-id` | required on 67 of the 68 operations (all but `info`) | missing: 400 `{"code":400,"reason":"Bad request.","errors":["No data-partition-id in headers"]}`. A request whose only validation errors are header errors gets 400 instead of 422. | [C; 1415 routes/v2/api.py:27; deps/request.py:46-63; errors/validation_error.py:34-90; it/tests/depth_shift/test_depth_shift_negative_headers.py:119-146] |
| `Content-Type` on a record POST | `POST /v2/{collection}` | exactly `application/json`. Missing: 400 "Content-Type header is required, but was not provided". Any other value, including `application/json; charset=utf-8`: 415. The check is membership of the raw header value in a list. | [C: descriptions of `post_records_*`; 1415 deps/request.py:66-89,109-120; routes/osdu/storage_records.py:286-289; it/tests/depth_shift/test_depth_shift_negative_headers.py:149-174] |
| `Content-Type` on a content POST | the five content POSTs | exactly `application/json` or `application/x-parquet`. `application/parquet` gets 415, although the descriptions in `C` name it. | [C: descriptions of `post_data_*`; 1415 deps/request.py:92-106; app/resources/mime_types.py:31-44; it/tests/fluid_model/black_oil/test_black_oil_negative_headers.py:130-161] |
| `Accept` | content GET, search and schema operations | absent or `*/*`: JSON. Otherwise the media ranges are read in order, parameters dropped, lower-cased: `application/x-parquet` or `application/parquet` gives parquet, `application/json` gives JSON. A range for another type RAFS knows (`application/zip`, `application/x-zip-compressed`, `application/octet-stream`, the xlsx type) is accepted and a content GET then answers JSON. Any other type: 406. | [1415 deps/request.py:168-206; app/resources/mime_types.py:18-58; routes/data/api.py:278-284; it/tests/fluid_model/black_oil/test_black_oil_negative_headers.py:164-177] |
| `x-collaboration` | optional on 61 operations in `C` | the code reads it on every route that builds a core-service client and forwards it to Storage, Search, Dataset, Schema and Entitlements. The README gives the formats `<uuid>` and `id=<uuid>,application=<name>` and says the header is validated; the code only reads and forwards it. | [C; 1415 deps/request.py:317-320; deps/services.py:34-127; README.md:567-579] |
| `correlation-id` | optional; not in `C` | when absent the middleware generates `rafs-ddms-<uuid4>` and adds it to the request. The value is sent on every core call and echoed on the response. | [1415 app/middleware/correlation_id_middleware.py:47-63; deps/services.py:34-39] |
| `Cache-Control: no-store` or `no-cache` | cached GETs | bypasses the response cache (section 1.5) | [1415 README.md:171] |

### 1.4 Auth and authorisation

- `C` declares `HTTPBearer` on every v2 and dev operation; `GET /info` has no security
  [C: GET /info, get_info_api_rafs_ddms_info_get]. The Azure chart turns ingress auth off for `/info`, `/docs*` and
  `/openapi.json` [1415 devops/azure/values.yaml:36-40].
- RAFS builds every core-service client with the caller's bearer token, `data-partition-id`, `correlation-id` and,
  when present, `x-collaboration` [1415 deps/services.py:34-127; clients/storage_client.py:44-49;
  clients/dataset_client.py:47-52; clients/schema_client.py:101-106]. The delivery identity therefore needs the core
  roles of the calls RAFS makes for it. The core contracts state: Storage `POST /query/records` and
  `GET /records/{id}` need `service.storage.viewer`, `creator` or `admin`, and `POST /records/{id}:delete` needs
  `service.storage.creator` or `admin` and ownership of the record
  [ST: POST /query/records, getRecords; GET /records/{id}, getLatestRecordVersion; POST /records/{id}:delete, deleteRecord];
  Dataset `POST /storageInstructions` needs `service.dataset.editors`, `PUT /registerDataset` needs
  `service.storage.creator` or `service.storage.admin`, and `GET /retrievalInstructions` needs
  `service.dataset.viewers` [DS: POST /storageInstructions, storageInstructions; PUT /registerDataset,
  createOrUpdateDatasetRegistry; GET /retrievalInstructions, retrievalInstructions]; Schema
  `GET /schema/{id}` needs `service.schema-service.viewers` [SS: GET /schema/{id}, getSchema]. The Storage contract
  states no roles for `PUT /records` [ST: PUT /records, createOrUpdateRecords].
- The descriptions of 56 operations in `C` state RAFS roles: GET needs `users.datalake.viewers`, `editors` or
  `admins`; POST and DELETE need `users.datalake.editors` or `admins`; in both cases the caller must also be in the
  record's ACL groups. The ten v2 search operations, the report `source` operation and `info` state none [C;
  1415 app/resources/required_roles.py:19-20; routes/utils/api_description_helper.py:21-74]. *(inference)* The
  fetched code writes these roles only into descriptions and enforces none itself; the core services enforce access.
  The types and schema operations call Entitlements `GET /groups` before answering
  [1415 routes/common/endpoints.py:48-58,78-105; services/entitlements.py:41-55;
  app/dev/services/osdu_clients/entitlements_client.py:29,70; EN: GET /groups, listGroups].

### 1.5 Response cache

- `CACHE_ENABLE` (default False) switches the cache on; the Azure and core-plus charts set it to True
  [1415 settings/app.py:60; app/core/helpers/cache_helper.py:22-45; devops/azure/values.yaml:55;
  devops/core-plus/deploy/values.yaml:94].
- Cached: the record GET, the version list and the specific version [1415 routes/osdu/storage_records.py:53-114], and
  every content GET [1415 routes/v2/data/endpoints.py:100,341,460; routes/samplesanalysis/endpoints.py:138]. The TTL
  is `CACHE_DEFAULT_TTL`, 60 seconds by default [1415 app/core/helpers/cache/settings.py:17; README.md:194].
- The cache key hashes the `Authorization` and `data-partition-id` headers, the URL, the query parameters and the
  request `Content-Type`. It does not include `Accept` or `x-collaboration`
  [1415 app/core/helpers/cache/key_builder.py:22-47]. *(inference)* Within the TTL a cached JSON body can answer a
  parquet request for the same URL, and a read in one collaboration namespace can answer another. Verification reads
  send `Cache-Control: no-store`.

## 2. The calls a writer makes

### 2.1 Collections served

All paths are under `/api/rafs-ddms/v2`. A kind base is `{a}:wks:<entity type>`, where `{a}` is `SCHEMA_AUTHORITY`
(default `osdu`; none of the Azure, core-plus and gc charts sets it) [1415 settings/base.py:40;
app/models/domain/osdu/base.py:27-81; devops/azure/values.yaml:44-66; devops/core-plus/deploy/templates/configmap.yaml;
devops/gc/deploy/templates/configmap.yaml]. Any `x.y.z` version of an accepted base is accepted.

| Collection | Entity types accepted | Content | Content types (path value: schema versions) |
| --- | --- | --- | --- |
| `masterdata` | `master-data--GenericFacility`, `master-data--GenericSite`, `master-data--Sample`, `master-data--SampleAcquisitionJob`, `master-data--SampleChainOfCustodyEvent`, `master-data--SampleContainer` (not Wellbore) | none | none |
| `samplesanalysesreport` | `work-product-component--SamplesAnalysesReport` | none; `GET /samplesanalysesreport/{record_id}/source[?version=]` returns the files named in `data.Datasets` (one file as is, several as a zip, 404 when there are none) | none |
| `samplesanalysis` | `work-product-component--SamplesAnalysis` | several content types | 49 analysis types (section 3.3) |
| `saturationfunctionset` | `work-product-component--SaturationFunctionSet` | one content type | `saturationfunctionset`: 1.0.0 |
| `reservoirsimulationrockphysicsmodel` | `work-product-component--ReservoirSimulationRockPhysicsModel` | one content type | `reservoirsimulationrockphysicsmodel`: 1.0.0 |
| `fluidmodel` | `work-product-component--FluidModel` | several content types | `blackoilfluidmodel`: 1.0.0; `compositionalfluidmodel`: 1.0.0 |
| `depthshift` | `work-product-component--DepthShift` | one content type, exactly one row | `depthshift`: 1.0.0 |

Citations: routers [1415 routes/v2/api.py:32-77]; kind bases [1415 app/models/domain/osdu/base.py:59-81, the
master data list at 67-74]; the kind list each collection checks [1415 deps/validation.py:276-395]; content wiring
[1415 routes/saturationfunctionset/api.py:97-102; routes/reservoirsimulationrockphysicsmodel/api.py:102-107;
routes/fluidmodel/api.py:72-77; routes/depthshift/api.py:92-97; routes/samplesanalysis/api.py:73-76;
routes/v2/data/endpoints.py:283-322,577-597]; model maps [1415 schemas/base.py:28-47;
schemas/sample_analysis_api_v2/base.py:232-282; schemas/fluid_model_v2/base.py:20-32;
schemas/depth_shift_v2/base.py; schemas/saturation_function_set_api_v2/base.py:5-11;
schemas/reservoir_simulation_rock_physics_v2/base.py:5-11]; the source operation
[1415 routes/osdu/wpc_dataset_source.py:54-99; app/resources/source_renderer.py:38-51].

The route templates, per collection (every operation with its id is in Appendix A):

| Call | Path under `/api/rafs-ddms/v2` | Collections |
| --- | --- | --- |
| record upsert | `POST /{collection}` | all seven |
| record read | `GET /{collection}/{record_id}` | all seven |
| version list, one version | `GET /{collection}/{record_id}/versions`, `GET /{collection}/{record_id}/versions/{version}` | all seven |
| logical delete | `DELETE /{collection}/{record_id}` | all seven |
| content write, one content type | `POST /{collection}/{record_id}/data` | `saturationfunctionset`, `reservoirsimulationrockphysicsmodel`, `depthshift` |
| content write, several content types | `POST /{collection}/{record_id}/data/{content_type}` | `samplesanalysis`, `fluidmodel` |
| content read | `GET /{collection}/{record_id}/data/{content_id}` or `GET /{collection}/{record_id}/data/{content_type}/{content_id}` | the five content collections |
| content schema | `GET /{collection}/data/schema` or `GET /{collection}/{content_type}/data/schema` | the five content collections |
| type catalogue | `GET /samplesanalysis/analysistypes`, `GET /fluidmodel/fluidmodeltypes` | two |
| search | `GET /{collection}/search` and `.../search/data`, or `GET /{collection}/{content_type}/search` and `.../search/data` | the five content collections |

### 2.2 Preconditions and order

- **ACL groups.** Storage answers `PUT /records` with 404 "Invalid acl group." when a group does not exist
  [ST: PUT /records, createOrUpdateRecords]; RAFS passes a Storage 404 on upsert through
  [1415 services/storage.py:112-129].
- **Referential integrity of records.** Every reference-data, master-data and work-product-component id inside a
  posted record, except its top-level `id`, must exist in Storage. RAFS checks them with Storage `POST /query/records`
  in chunks of `STORAGE_QUERY_LIMIT` (100); any `invalidRecords` fail the whole request with 422 "Request can't be
  processed due to missing referenced records. Records not found: [...]"
  [1415 deps/validation.py:115-129,157-193; app/resources/common_osdu_regex.py:16-20; routes/utils/query.py:37-52;
  settings/app.py:64; clients/storage_client.py:182-204; ST: POST /query/records, getRecords]. Ids are checked
  without their version [1415 routes/utils/query.py:48]; `dataset--` ids are not checked.
- **Referential integrity of content.** The same check runs over the whole content table; its failures are reported
  under "Missing records in storage" [1415 app/bulk_data_validation/data_validation.py:39-59,99-114;
  routes/data/api.py:551-581].
- **Order.** *(inference from the two checks)* Reference data first, then master data (Sample and related), then
  SamplesAnalysesReport, then SamplesAnalysis and the other work product components, then their content. The
  tutorial states the same: metadata first, then content [1415 docs/tutorial/README.md:24-27].
- **Record schema.** RAFS fetches the kind's schema with `GET /schema/{kind}`; when the Schema service answers 404,
  validation is skipped with a warning [1415 services/schema.py:125-175; clients/schema_client.py:169-185;
  SS: GET /schema/{id}, getSchema].

### 2.3 Record upsert

```http
POST /api/rafs-ddms/v2/{collection}
Authorization: Bearer <token>
data-partition-id: <partition>
Content-Type: application/json

[
  {
    "id": "<partition>:work-product-component--SamplesAnalysis:<local-id>",
    "kind": "osdu:wks:work-product-component--SamplesAnalysis:1.0.0",
    "acl": {"viewers": ["<group>"], "owners": ["<group>"]},
    "legal": {"legaltags": ["<legal tag>"], "otherRelevantDataCountries": ["<country>"]},
    "data": {"SampleAnalysisTypeIDs": ["<partition>:reference-data--SampleAnalysisType:NMR:"], "...": "..."}
  }
]
```

[C: POST /v2/{collection}, `post_records_*`; 1415 routes/osdu/storage_records.py:116-142,251-290;
routes/fluidmodel/endpoints.py:55-133]

**Checks.** All run before anything is written; the endpoint runs only when every check passes. The two route
dependencies are declared in the order records validation, then `Content-Type`
[1415 routes/osdu/storage_records.py:286-289].

1. The body must be a JSON array whose items parse as the record model (section 3.1). Otherwise 422
   `{"code":422,"reason":"Unprocessable entity.","errors":[...]}`, for example "body value is not a valid list" or
   "kind field required" [1415 errors/validation_error.py:67-90; ut/osdu/storage_mock_objects.py:409,412].
2. Per record, the kind must have four `:`-separated parts, a base accepted by the collection and a version
   `x.y.z`. Otherwise 422 "Kind `...` not supported in RAFS-DDMS. Supported kinds for this endpoint: [...]" or
   "Kind `...` has an invalid version `...`. ..." [1415 deps/validation.py:196-228,248-252;
   routes/utils/records.py:225-252].
3. Each record is validated against its kind's JSON schema from the Schema service, with custom `date-time` and
   `time` format checks. Failures give 422 whose `reason` is a list of `{id, kind, errors}`
   [1415 deps/validation.py:239-273; services/schema.py:34-75,146-175].
4. Mandatory references: SamplesAnalysis needs at least one id in `data.SampleAnalysisTypeIDs`; SaturationFunctionSet
   needs at least one value under a key containing `ID` inside `data.SaturationFunctions`. Otherwise 422 whose
   `reason` is a list of `Missing <field> in index <n>`
   [1415 deps/validation.py:53,99-112,132-154,294-327].
5. Referential integrity (section 2.2).
6. `Content-Type` (section 1.3).

**Write.** The endpoint sends the request array, as parsed from the body rather than the validated copy, to Storage
`PUT /records` [1415 routes/osdu/storage_records.py:116-142; clients/storage_client.py:60-77;
ST: PUT /records, createOrUpdateRecords]. The unit test asserts the posted record reaches Storage unchanged
[1415 ut/osdu/storage_records_test.py:557-577].

**Response 200, as sent on the wire:**

```json
{"recordCount": 1, "recordIdVersions": ["<partition>:work-product-component--SamplesAnalysis:<local-id>:<version>"], "skippedRecordCount": 0}
```

- The model fields are camelCase with snake_case aliases [1415 app/models/schemas/osdu_storage.py:20-23], the route
  serialises by field name (`response_model_by_alias=False`) [1415 routes/osdu/storage_records.py:280-281], the unit
  test expects the camelCase body [1415 ut/osdu/storage_records_test.py:573-574;
  ut/osdu/storage_mock_objects.py:413-417], and the integration tests read `recordIdVersions` and `recordCount`
  [1415 it/tests/fluid_model/test_fluid_model_defect_coverage.py:91; it/tests/fluid_model/test_fluid_model_x_collaboration.py:16].
  `C` documents `record_count`, `record_id_versions` and `skipped_record_count` [C: component StorageUpsertResponse].
- Storage answers 201 [ST: PUT /records, createOrUpdateRecords]; RAFS answers 200 [C; 1415
  routes/osdu/storage_records.py:279].
- `recordIdVersions` passes through `set(...)`, so order and duplicates are lost. Match the returned `id:version`
  values to the input records by id [1415 routes/osdu/storage_records.py:140].
- `skippedRecordCount` is the number of Storage's `skippedRecordIds`; the ids themselves, and Storage's `recordIds`,
  are dropped [1415 routes/osdu/storage_records.py:134-136; ST: component CreateUpdateRecordsResponse]. A count above
  zero needs a read-back per id.
- `id` may be omitted: RAFS forwards the record without it [1415 ut/osdu/storage_records_test.py:719-740] and Storage
  creates a new record [ST: PUT /records, createOrUpdateRecords]. The route always sends `id`.
- **FluidModel only.** When a record has no `data.FluidModelTypeID`, the response adds `warning` ("Records missing
  FluidModelTypeID will not be included in outputs produced by search endpoints") and the ids of those records,
  taken from Storage's `recordIdVersions` by the record's input position
  [1415 routes/fluidmodel/endpoints.py:55-92; deps/validation.py:889-909]. The field is `warningRecordIds` with the
  alias `warning_record_ids` [1415 routes/osdu/storage_records.py:317-321], and this route also serialises by field
  name [1415 routes/fluidmodel/endpoints.py:118-124]. *(inference)* The wire name is `warningRecordIds`, and the ids
  are right only when Storage returns `recordIdVersions` in input order; no test asserts either. `C` documents
  `warning` and `warning_record_ids` [C: component StorageUpsertResponseWithWarning; POST /v2/fluidmodel,
  post_records_api_rafs_ddms_v2_fluidmodel_post].
- **Errors.** A Storage 400 becomes 422. Storage 401, 403 and 404 pass through. Any other upstream status passes
  through when it is 409, 422, 503 or 504, and becomes 502 otherwise [1415 services/storage.py:112-129;
  services/error_handlers.py:26-72].
- **Re-posting an existing id.** Storage creates a new version of the record from the posted record
  [ST: PUT /records, createOrUpdateRecords]. RAFS forwards `data` as posted and does not merge the parent's existing
  `data.DDMSDatasets` into it [1415 routes/osdu/storage_records.py:133]. *(inference)* A metadata redelivery that
  omits `DDMSDatasets` writes a parent version without the content URNs, so it must carry the current URNs or
  re-post the content afterwards.

### 2.4 Content write

```http
POST /api/rafs-ddms/v2/samplesanalysis/{record_id}/data/{analysis_type}?content_schema_version=1.0.0
POST /api/rafs-ddms/v2/fluidmodel/{record_id}/data/{fluid_model_type}?content_schema_version=1.0.0
POST /api/rafs-ddms/v2/{depthshift|saturationfunctionset|reservoirsimulationrockphysicsmodel}/{record_id}/data?content_schema_version=1.0.0
Authorization: Bearer <token>
data-partition-id: <partition>
Content-Type: application/json | application/x-parquet
```

[C: `post_data_v2_api_rafs_ddms_v2_samplesanalysis__record_id__data__analysis_type__post`,
`post_data_multiple_content_type_*`, `post_data_single_content_type_*`; 1415 routes/v2/data/endpoints.py:173-253,
295-322,410-458,528-575,634-661]

The body forms are in section 3.2. Processing, in order [1415 routes/v2/data/endpoints.py:173-253;
routes/data/api.py:388-524]:

1. Route dependencies: the record id pattern (422) and `Content-Type` (400 or 415)
   [1415 routes/v2/data/endpoints.py:298-317,637-656].
2. The content model is chosen from the collection, the content type and `content_schema_version`. An unknown type
   or version gives 404 "Model not found for type '...', version '...'.", followed by the available versions when
   the type is known [1415 deps/validation.py:398-503]. The content type must equal a key of the model map, in lower
   case (section 3.3).
3. The parent record is read from Storage; a missing parent gives Storage's 404, passed through
   [1415 routes/v2/data/endpoints.py:220; services/storage.py:54-76].
4. The whole body is read into memory [1415 deps/validation.py:632; routes/data/api.py:347] and validated with a
   pandera schema built from the content model with `coerce=True`; nulls in optional columns are tolerated
   [1415 schemas/data_schema.py:20-28; app/bulk_data_validation/data_validation.py:39-85,125-166]. Then the
   referential integrity of the table is checked [1415 app/bulk_data_validation/data_validation.py:99-114].
   Failures give the 422 body of section 6.1.
5. When the content has the id column of its family (section 3.4), every non-null value, without its version and
   trailing colon, must equal the URL `record_id`; otherwise 422 under "Invalid value"
   [1415 routes/data/api.py:362-386,428-429; routes/utils/records.py:48-81].
6. JSON input: columns the model declares as float are cast, then the frame is written with
   `to_parquet(index=False)`; a conversion failure gives 400 "Parquet conversion error: ..."
   [1415 routes/data/api.py:78-87,431-439]. Parquet input is stored as sent [1415 routes/data/api.py:345-353].
7. DepthShift only: the stored parquet must hold exactly one row and valid `DepthShiftSet` values, otherwise 422
   [1415 routes/depthshift/endpoints.py:52-90].
8. **Dataset mode**, the default (`USE_BLOB_STORAGE` is False by default and `"False"` in the Azure chart)
   [1415 settings/app.py:70; devops/azure/values.yaml:66]:
   - RAFS looks in `data.DDMSDatasets` for the first URN whose dataset id is a `dataset--` id with a local part
     starting with the content type. If it finds one it reuses that dataset id; otherwise it mints
     `{data-partition-id}:dataset--File.Generic:{content_type}-{uuid4}`
     [1415 routes/data/api.py:487-506; routes/utils/records.py:108-129].
   - It calls the Dataset service: `POST /storageInstructions?kindSubType=dataset--File.Generic`, an upload of the
     parquet bytes to the signed URL through the provider's blob loader, then `PUT /registerDataset` with one record
     [1415 services/dataset.py:74-111; clients/dataset_client.py:63-86,169-189;
     app/providers/dependencies/blob_loader.py:31-50; DS: POST /storageInstructions, storageInstructions;
     PUT /registerDataset, createOrUpdateDatasetRegistry]. That record has kind
     `{a}:wks:dataset--File.Generic:1.0.0`, the parent's `acl` and `legal`, the parent's `ResourceHomeRegionID`,
     `ResourceHostRegionIDs` and `ResourceSecurityClassification` where present, and
     `data.DatasetProperties.FileSourceInfo` with `FileSource` and `FileSize`
     [1415 services/utils/dataset.py:16-44]. `FileSize` is `str(sys.getsizeof(<bytes>))`, the size of the Python
     object rather than the byte count [1415 services/dataset.py:99-106]. The service returns `{id}:{version}`
     [1415 services/dataset.py:110-111].
   - It builds the URN
     `urn://rafs-v2/{content type without "-"}data/{record_id}/{dataset id}:{dataset version}/{content_schema_version}`
     [1415 routes/utils/records.py:159-184; routes/data/api.py:486-515; settings/app.py:32], replaces the matching
     entry of `data.DDMSDatasets` or appends it, and writes the parent record, as it was read from Storage, back with
     `PUT /records` [1415 routes/data/api.py:502,516-521; routes/utils/records.py:132-156].
   - **Response 200:** `{"ddms_urn": "<urn>"}` [1415 routes/data/api.py:524]. `C` declares an untyped object, with
     400 `BadRequestResponse`, 415 `UnsupportedMediaTypeResponse` and 422 `HTTPValidationError` [C].
9. **Blob mode** (`USE_BLOB_STORAGE=True`): the parquet goes to the provider's blob storage (Azure, or a local folder
   when `LOCAL_DEV_MODE` is set; any other provider raises `NotImplementedError`) under the object name
   `{collection}/{content_type}/{content_schema_version}/{uuid4}`. The URN is `urn://rafs/{record_id}/{object name}`;
   the entry with the same collection, content type and schema version is replaced, or the URN is appended. The
   response is `{"ddms_urn": ..., "updated_wpc_id": [<recordIdVersions of the parent upsert>]}`, and no
   `dataset--File.Generic` record is created [1415 app/providers/dependencies/blob_storage.py:31-52,199-225;
   routes/utils/ddms_datasets.py:68-126; routes/utils/records.py:300-353; README.md:542-560]. The SamplesAnalysis
   content GET without depth-shift parameters always takes the dataset path
   [1415 routes/samplesanalysis/endpoints.py:138-191]; *(inference)* in blob mode such a read finds no dataset id in
   the URN and answers 404.

The core-plus chart sets `RAFS_USE_BLOB_STORAGE` [1415 devops/core-plus/deploy/templates/configmap.yaml:30;
devops/core-plus/deploy/values.yaml:80], while the setting is read from `USE_BLOB_STORAGE` (pydantic `BaseSettings`
with no environment prefix) [1415 settings/base.py:27-53; settings/app.py:70]. The chart's value is `"False"` either
way; *(inference)* setting it to `"True"` there changes nothing. The integration test helper keys on
`RAFS_USE_BLOB_STORAGE` [1415 it/helpers/common.py:67-74].

**Where content schemas come from.** The content models are pydantic classes generated from JSON schema files kept in
the repository under `app/models/data_schemas/jsonschema/<family>/`, and selected through `MAPS_TO_DATA_MODEL`
[1415 schemas/base.py:28-47]. The architecture document says: "As of the M25 release, the DDMS has not introduced the
content schema registry for schema management" [1415 docs/architecture/README.md:23-28]. The DepthShift and
SaturationFunctionSet files name an OSDU content kind in `x-osdu-schema-source`. *(inference)* No content schema is
read from the Schema service: the only Schema service call in the fetched code is `GET /schema/{kind}` for record
validation [1415 clients/schema_client.py:169-185].

**Redelivery and concurrency.**

- Dataset mode keeps one content dataset per record and content type: a repeat POST for the same type reuses the
  dataset id, registers a new dataset version and replaces the URN entry, including its schema version
  [1415 routes/data/api.py:490-503; routes/utils/records.py:108-156]. No content type name is a prefix of another at
  the pinned commit, so the prefix match cannot pick another type's dataset (checked over the 54 names in
  [1415 app/resources/paths.py:19-68,181-194]).
- Blob mode keeps one entry per collection, content type and schema version
  [1415 routes/utils/ddms_datasets.py:97-111].
- Every content POST writes a new parent version [1415 routes/data/api.py:521; routes/utils/ddms_datasets.py:118].
- *(inference)* The parent is read, its `DDMSDatasets` edited and the record written back with no version
  precondition [1415 routes/v2/data/endpoints.py:220; routes/data/api.py:521], and Storage `PUT /records` offers
  none [ST: PUT /records, createOrUpdateRecords]. Concurrent content POSTs to one record can drop each other's URN,
  so the route serialises content writes per record.
- Nothing chunks or limits the body in the fetched code; the whole table is held in memory. Core calls use
  `REQUEST_TIMEOUT` (15 seconds by default, 180 in the Azure chart) and httpx transports with `retries=3`
  [1415 settings/base.py:44; clients/conf.py:19-25; devops/azure/values.yaml:61;
  clients/storage_client.py:216-220].

### 2.5 Other calls a route makes

- **Content schema discovery.** `GET /v2/samplesanalysis/{analysistype}/data/schema`,
  `GET /v2/fluidmodel/{fluid_model_type}/data/schema` and `GET /v2/{collection}/data/schema` for the three
  single-type collections, each with `content_schema_version`, return the pydantic JSON schema of the model, or 404
  listing the available versions [1415 routes/common/endpoints.py:72-162;
  C: `get_content_schema_*`]. `GET /v2/samplesanalysis/analysistypes` and `GET /v2/fluidmodel/fluidmodeltypes`
  return `{"<type>": ["<version>", ...]}` [1415 routes/common/endpoints.py:32-69; C: `get_types_*`]. A route can
  use them to check a content type and version before sending.

**Probes.**

| Endpoint | In `C` | Auth | Returns | Citation |
| --- | --- | --- | --- | --- |
| `GET /api/rafs-ddms/info` | yes | none | `InfoResponse` {`name`, `app_version`, `build_time`, `branch`, `commit_id`, `commit_message`, `release_version`} | [C: GET /info, get_info_api_rafs_ddms_info_get; component InfoResponse; 1415 routes/info.py:24-40] |
| `GET /api/rafs-ddms/healthz` | no | none | `{"status": "healthy"}`; the liveness probe of both charts | [1415 routes/healthz.py:20-27; devops/azure/values.yaml:33-35; devops/core-plus/deploy/templates/deployment.yaml:41-47] |
| `GET /api/rafs-ddms/readiness` | no | none | text `Ready` after a GET to each URL in `SERVICE_READINESS_URLS` (the Storage and Schema `liveness_check` in both charts); an upstream failure is raised | [1415 routes/readiness.py:24-37; clients/readiness_client.py:33-50; devops/azure/values.yaml:30-32,54; devops/core-plus/deploy/templates/configmap.yaml:18] |
| `GET /api/rafs-ddms/openapi.json`, `/docs`, `/redoc` | no | none at the Azure ingress | the live contract | [1415 settings/app.py:84-86; devops/azure/values.yaml:36-40] |
| `/metrics` | no | none | Prometheus metrics, only when `APP_ENV=prod` and `CLOUD_PROVIDER=azure` | [1415 app/providers/helpers/metric.py:21-25; devops/azure/values.yaml:25-28,45] |

An authenticated probe that writes nothing: `GET /v2/samplesanalysis/analysistypes` checks the token and partition
through Entitlements `GET /groups` and returns the type catalogue [1415 routes/common/endpoints.py:48-58].

## 3. Payload and content shapes

### 3.1 Record body

`C` documents an item schema only for `masterdata`: an inline array titled `MasterDataRecords` of an inline object
titled `OsduStorageRecord`, with required `kind`, `acl`, `legal` and `data`; `acl` refers to component `Acl`
(`viewers`, `owners`, both required, no other keys) and `legal` to component `Legal` (`legaltags` and
`otherRelevantDataCountries` required, `status` optional, no other keys)
[C: POST /v2/masterdata, post_records_api_rafs_ddms_v2_masterdata_post; components Acl, Legal]. For the other six
collections `C` says only "array of object" [C: POST /v2/depthshift, post_records_api_rafs_ddms_v2_depthshift_post].
`C` marks the body required on five record POSTs, and not on `masterdata` or `samplesanalysis` [C].

The code validates every collection's body with the same model
[1415 routes/osdu/storage_records.py:254-273; routes/fluidmodel/endpoints.py:97-116;
app/models/schemas/osdu_storage.py:26-56]:

- required: `kind`; `acl` with `viewers` and `owners` and no other keys; `legal` with `legaltags` and
  `otherRelevantDataCountries`, optional `status`, and no other keys; `data` (object);
- optional: `id`, `meta`, `ancestry`, `tags`, `version`, `createUser`, `createTime`, `modifyUser`, `modifyTime`.

The Storage record requires at least one legal tag and one country (`minItems: 1`) [ST: component Legal]; RAFS does
not check this.

### 3.2 Content body forms

- **JSON split object.** `C` has an inline schema titled `OrientSplit` with `additionalProperties: false`: `columns`
  (string array), `index` (integer array) and `data` (array of arrays), each required with `minItems: 1`
  [C: POST /v2/samplesanalysis/{record_id}/data/{analysis_type}, post_data_v2_api_rafs_ddms_v2_samplesanalysis__record_id__data__analysis_type__post;
  the same body on the saturationfunctionset and reservoirsimulationrockphysicsmodel content POSTs]. The lengths of
  `index` and `data` must be equal, otherwise 422 [1415 deps/validation.py:643-650]. `C` has no request body on the
  depthshift and fluidmodel content POSTs, which run the same code [C; 1415 app/custom_openapi.py:729-766].

  ```json
  {"columns": ["SamplesAnalysisID", "SampleID", "Meta", "NMRTest"],
   "index": [0],
   "data": [["<partition>:work-product-component--SamplesAnalysis:<local-id>:",
             "<partition>:master-data--Sample:<sample-id>:",
             [{"kind": "Unit", "name": "degree Fahrenheit", "unitOfMeasureID": "<partition>:reference-data--UnitOfMeasure:degF:", "propertyNames": ["NMRSummaryData.Temperature"]}],
             [{"...": "..."}]]]}
  ```

  (shape of [1415 it/data/v2/samples_analysis/nmr_data.1.0.0.json])
- **JSON records array.** A non-empty array of objects, at least one of them non-empty; one object per row. A split
  object wrapped in an array gives 422 [1415 deps/validation.py:595-641]. The tests post `[payload]` and read it back
  with `orient=records` [1415 it/tests/samples_analysis/nmr/test_nmr.py:446-468].
- **Parquet.** `Content-Type: application/x-parquet`. The bytes are validated through a JSON round trip and stored
  as sent [1415 routes/data/api.py:327-360,431].
- **Malformed input.** Invalid JSON gives 422 "Invalid JSON format. Please check syntax."; a split object missing a
  key gives 422 naming the missing keys [1415 routes/data/api.py:294-325,354-359].
- `Meta` holds unit and CRS descriptors (`{"kind": "Unit", "name", "unitOfMeasureID", "propertyNames"}`), as in the
  example above and the examples in `C` for the samplesanalysis, saturationfunctionset and
  reservoirsimulationrockphysicsmodel content POSTs.

Download forms: JSON with `orient=split` (the default), JSON with `orient=records`, or parquet through `Accept`
(section 5.2).

### 3.3 SamplesAnalysis analysis types

The path values come from `CommonRelativePathsV2` [1415 app/resources/paths.py:19-68] and the versions from
`PATH_TO_DATA_MODEL_VERSIONS_API_V2` [1415 schemas/sample_analysis_api_v2/base.py:74-282]. The third column is
`SAMPLESANALYSIS_TYPE_MAPPING` [1415 app/resources/paths.py:83-179]: the `reference-data--SampleAnalysisType` codes
search uses to find records of the type (section 5.3). `compositionalanalysis` and `atmosphericflash` share one
content model per version [1415 schemas/sample_analysis_api_v2/base.py:86-93].

| `analysis_type` | Versions | SampleAnalysisType codes |
| --- | --- | --- |
| `routinecoreanalysis` | 1.0.0 | `BasicRockProperties.RoutineCoreAnalysis` |
| `constantcompositionexpansion` | 1.0.0 | `PVT.ConstantCompositionExpansion` |
| `differentialliberation` | 1.0.0 | `PVT.DifferentialLiberation` |
| `transport` | 1.0.0 | `PVT.TransportProperties` |
| `compositionalanalysis` | 1.0.0, 1.1.0 | `PVT.CompositionalAnalysis` |
| `atmosphericflash` | 1.0.0, 1.1.0 | `PVT.SingleStageFlash` |
| `multistageseparator` | 1.0.0 | `PVT.MultiStageSeparatorTest` |
| `swelling` | 1.0.0 | `PVT.SolubilitySwelling` |
| `constantvolumedepletion` | 1.0.0 | `PVT.ConstantVolumeDepletion` |
| `wateranalysis` | 1.0.0 | `WaterAnalysis` |
| `interfacialtension` | 1.0.0 | `PVT.InterfacialTension` |
| `vaporliquidequilibrium` | 1.0.0, 2.0.0 | `PVT.VaporLiquidEquilibria` |
| `multiplecontactmiscibility` | 1.0.0 | `PVT.MultiContactMiscibility` |
| `slimtube` | 1.0.0, 2.0.0 | `PVT.SlimTube` |
| `relativepermeability` | 1.0.0, 1.1.0 | `BrinePermeability`, `RelativePermeability.SteadyState`, `RelativePermeability.SingleSpeedCentrifuge`, `RelativePermeability.UnsteadyState` |
| `fractionation` | 1.0.0 | `Fractionation`, `Fractionation.SARA` |
| `extraction` | 1.0.0 | `Extraction` |
| `rockcompressibility` | 1.0.0 | `Geomechanics.Compressibility` |
| `electricalproperties` | 1.0.0 | `ElectricalProperties`, `ElectricalProperties.FormationResistivityFactor`, `ElectricalProperties.ResistivityIndex`, `ElectricalProperties.DigitalRockModelling` |
| `nmr` | 1.0.0 | `NMR`, `NMR.ResearchAndDevelopmentMethod` |
| `multiplesalinitytests` | 1.0.0 | `ElectricalProperties.CoCw` |
| `gcmsalkanes` | 1.0.0 | `GasChromatographyMassSpectroscopy.Saturate` |
| `gcmsaromatics` | 1.0.0 | `GasChromatographyMassSpectroscopy.Aromatic` |
| `gcmsratios` | 1.0.0 | `GasChromatographyMassSpectroscopy.Ratios` |
| `gaschromatographyanalyses` | 1.0.0 | `GasChromatography.Aromatic`, `GasChromatography.Gasoline`, `GasChromatography.HighTemperature`, `GasChromatography.Pyrolysis`, `GasChromatography.Saturate`, `GasChromatography.SimulatedDistillation`, `GasChromatography.SulfurDetection`, `GasChromatography.ThermalExtraction`, `GasChromatography.WholeOil` |
| `gascompositionanalyses` | 1.0.0 | `GasChromatography.GasComposition` |
| `isotopes` | 1.0.0 | `Isotope.CompoundSpecificIsotopeAnalysis`, `Isotope.Gas`, `Isotope.Non%2DGas` |
| `bulkpyrolysisanalyses` | 1.0.0 | `Pyrolysis.Bulk` |
| `coregamma` | 1.0.0 | `GammaRay.CoreGammaRay` |
| `uniaxial` | 1.0.0 | `Geomechanics.UniaxialTesting` |
| `gcmsms` | 1.0.0 | `GasChromatographyMassSpectroscopy.TandemMassSpectroscopy`, `GasChromatographyMassSpectroscopyMassSpectroscopy`, `GasChromatographyMassSpectroscopyMassSpectroscopy.QQQ`, `GasChromatographyMassSpectroscopyMassSpectroscopy.MRM` |
| `cec` | 1.0.0 | `ElectricalProperties.CationExchangeCapacity` |
| `triaxial` | 1.0.0 | `Geomechanics.TriaxialStrengthTesting` |
| `capillarypressure` | 1.0.0, 1.1.0 | `CapillaryPressure.Centrifuge`, `CapillaryPressure.MercuryInjection`, `CapillaryPressure.ResearchAndDevelopmentMethod`, `CapillaryPressure.PorousPlate`, `CapillaryPressure.DigitalRockModelling`, `CapillaryPressure` |
| `capillarysuctiontime` | 1.0.0 | `FluidSensitivity.CapillarySuctionTime` |
| `wettabilityindex` | 1.0.0 | `Wettability.AmottHarvey`, `Wettability.AmottUSBM`, `Wettability.Amott%2DHarvey`, `Wettability.Amott%2DUSBM` |
| `tec` | 1.0.0 | `Geomechanics.Thermal`, `Geomechanics.TEC` |
| `edsmapping` | 1.0.0 | `ElementalComposition.EDS%2DMapping` |
| `xrf` | 1.0.0, 1.1.0 | `ElementalComposition.XRF` |
| `tensilestrength` | 1.0.0 | `Geomechanics.TensileStrengthAnalysis` |
| `vitrinitereflectance` | 1.0.0 | `Petrography.VitriniteReflectance` |
| `xrd` | 1.0.0, 1.1.0 | `Mineralogy.XRD` |
| `pdp` | 1.0.0 | `BasicRockProperties.PressureDependentPermeability` |
| `stocktankoilanalysis` | 1.0.0, 1.1.0 | `PVT.StockTankOilCharacterization` |
| `crushedrockanalysis` | 1.0.0 | `BasicRockProperties.CrushedRockAnalysis` |
| `mininggeotechlogging` | 1.0.0 | `Mining.Geotechlogging` |
| `proppantembed` | 1.0.0 | `Geomechanics.ProppantEmbedment` |
| `rolleroventest` | 1.0.0 | `FluidSensitivity.RollerOvenTest` |
| `saturationtest` | 1.0.0 | `PVT.SaturationTest` |

FluidModel types: `blackoilfluidmodel` maps to the FluidModelType code `BlackOil` and `compositionalfluidmodel` to
`Compositional` [1415 app/resources/paths.py:192-200]; both have version 1.0.0 only
[1415 schemas/fluid_model_v2/base.py:20-32].

The schema files for the analysis types are `app/models/data_schemas/jsonschema/sample_analysis_api_v2/<name>.<version>.json`
(56 schema files and 56 example files at the pinned commit), for example `nmr.1.0.0.json`,
`capillary_pressure.1.1.0.json` and `atmospheric_flash_and_compositional_analysis.1.1.0.json`.

### 3.4 Row identity and required columns

Each content schema describes one row: its top-level properties are the table's columns. The id column is compared
with the URL `record_id` (section 2.4, step 5).

| Content | Id column | Required properties (schema files read) | Citation |
| --- | --- | --- | --- |
| every SamplesAnalysis type | `SamplesAnalysisID` (the class default) | `SamplesAnalysisID`, `SampleID`, `Meta` in `nmr.1.0.0.json` and `capillary_pressure.1.1.0.json`; the other types' files were not read | [1415 routes/data/api.py:91; schemas/jsonschema/sample_analysis_api_v2/nmr.1.0.0.json; schemas/jsonschema/sample_analysis_api_v2/capillary_pressure.1.1.0.json] |
| `saturationfunctionset` | `SaturationFunctionSetID` | `SaturationFunctionSetID`; `x-osdu-schema-source` is `osdu:wks:content--SaturationFunctionSet:1.0.0` | [1415 routes/saturationfunctionset/api.py:101; schemas/jsonschema/saturation_function_set_api_v2/saturation_function_set.1.0.0.json] |
| `reservoirsimulationrockphysicsmodel` | `ReservoirSimulationRockPhysicsID` | `ReservoirSimulationRockPhysicsID`, `Compressibility` | [1415 routes/reservoirsimulationrockphysicsmodel/api.py:106; schemas/jsonschema/reservoir_simulation_rock_physics_v2/reservoir_simulation_rock_physics_model.1.0.0.json] |
| `blackoilfluidmodel` | `FluidModelID` | `FluidModelID`, `Meta` | [1415 routes/fluidmodel/api.py:76; schemas/jsonschema/fluid_model_v2/black_oil_fluid_model.1.0.0.json] |
| `compositionalfluidmodel` | `FluidModelID` | all 18 properties: `FluidModelID`, `Meta`, `OilViscosityModel`, `OilViscosityModelTypeID`, `GasViscosityModel`, `GasViscosityModelTypeID`, `StandardCondition`, `StandardPressure`, `StandardTemperature`, `SequencedComponentList`, `BinaryInteractionCoefficient`, `ComponentProperties`, `EquationOfState`, `WaterCompressibility`, `WaterTable`, `TemperatureAtDepth`, `CompositionsAtDepth`, `FlashValidationExamples` | [1415 routes/fluidmodel/api.py:76; schemas/jsonschema/fluid_model_v2/compositional_fluid_model.1.0.0.json] |
| `depthshift` | `DepthShiftWPCID` | `DepthShiftWPCID`, `Meta`, `DepthShiftSet`; `x-osdu-schema-source` is `osdu:wks:content--CoreToLogDepthShift:1.0.0` | [1415 routes/depthshift/api.py:96; schemas/jsonschema/depth_shift_v2/coretologdepthshift.1.0.0.json] |

**Id values in content end with a colon.** In every schema file read, the id columns (`SamplesAnalysisID`,
`SampleID`, `SaturationFunctionSetID`, `ReservoirSimulationRockPhysicsID`, `FluidModelID`, `DepthShiftWPCID`) have a
pattern ending in `:[0-9]*$`, and the generated models carry the same constraint
[1415 schemas/sample_analysis_api_v2/nmr_data_model_1_0_0.py:405-422;
schemas/depth_shift_v2/coretologdepthshift_data_model_1_0_0.py:213-219]. A value must therefore be `<id>:` or
`<id>:<version>`; a bare `<id>` fails validation. The tests write `<record_id>:`
[1415 it/tests/samples_analysis/nmr/test_nmr.py:75; it/tests/depth_shift/test_depth_shift_negative_headers.py:137].

## 4. Identities and versions

- **Record ids in paths.** Each collection has its own pattern in `C` and in the code, for example
  `^[\w\-\.]+:work-product-component--SamplesAnalysis:[\w\-\.\:\%]+$`; `masterdata` accepts
  `^[\w\-\.]+:(master-data--GenericFacility|master-data--GenericSite|master-data--Sample|master-data--SampleAcquisitionJob|master-data--SampleChainOfCustodyEvent|master-data--SampleContainer):[\w\-\.\:\%]+$`.
  A mismatch gives 422 [C: parameter record_id; 1415 routes/samplesanalysis/endpoints.py:58;
  routes/samplesanalysesreport/api.py:24; routes/saturationfunctionset/api.py:41;
  routes/reservoirsimulationrockphysicsmodel/api.py:41; routes/fluidmodel/api.py:39; routes/depthshift/api.py:36;
  routes/v2/master_data/api.py:23-27; errors/validation_error.py:40-43].
- **Encoding.** A literal `%` inside an id is double-encoded in the path (`%` becomes `%25`)
  [C: description of parameter record_id]. RAFS URL-encodes the id again when it calls Storage
  [1415 clients/storage_client.py:206-214].
- **Version path parameter.** An integer from 1 to 9223372036854775807 (int64 maximum) in the code
  [1415 routes/osdu/storage_records.py:36,230-238]; `C` renders the maximum as `9.223372036854776e+18` [C].
- **Content ids.** A dataset id with or without `:version`, or a uuid in blob mode. `C` and the code use the
  unanchored pattern
  `([\w\-\.]+:dataset--File.Generic:[\w\-\d\.]+:?[\w\-\.\:\%]*)|([\d\w]+-[\d\w]+-[\d\w]+-[\d\w]+-[\d\w]+)`
  [C: parameter content_id; 1415 routes/v2/data/endpoints.py:93-95].
- **Record versions.** Every record POST and every content POST writes a new version of the record (sections 2.3
  and 2.4).
- **Dataset versions.** Every dataset-mode content POST registers a new version of the content dataset; content
  reads serve the unversioned id (section 5.2).
- **Reference ids used by search.** SamplesAnalysis search looks for
  `{data-partition-id}:reference-data--SampleAnalysisType:{code}` and FluidModel search for
  `{data-partition-id}:reference-data--FluidModelType:{BlackOil|Compositional}:` (section 5.3). *(inference)* A
  mapping that writes these references with another prefix produces records that search never returns.

**What the ledger records for a content write (dataset mode):**

- the content id, `ddms_urn.split("/")[-2]` (`{dataset id}:{version}`), and the content schema version, the last
  segment; the tests take the content id the same way [1415 it/helpers/common.py:67-74]. In blob mode the content id
  is the last segment and the schema version the one before it
  [1415 app/providers/dependencies/blob_storage.py:46-48; routes/utils/records.py:300-316];
- the minted `dataset--File.Generic` id, which RAFS created on the caller's behalf: it is logged when it is created
  and removed at cleanup (section 5.4). The RAFS tests purge these ids
  [1415 it/tests/apply_depth_shift/conftest.py:101-123];
- the parent's new version, which the dataset-mode response does not carry: read
  `GET /v2/{collection}/{record_id}/versions` afterwards;
- the content schema version used, because every content read repeats it.

## 5. Reads, verification and deletes

### 5.1 Records

- `GET /v2/{collection}/{record_id}` returns the body of Storage `GET /records/{id}`
  [C: `get_record_*`; 1415 routes/osdu/storage_records.py:53-68; clients/storage_client.py:79-98;
  ST: GET /records/{id}, getLatestRecordVersion].
- `GET /v2/{collection}/{record_id}/versions` returns the body of Storage `GET /records/versions/{id}`,
  `{"recordId": "...", "versions": [...]}` [C: `get_record_versions_*`, an untyped object;
  1415 routes/osdu/storage_records.py:70-85; clients/storage_client.py:123-142;
  it/tests/samples_analysis/nmr/test_nmr.py:52-54; ST: GET /records/versions/{id}, getRecordVersions; component RecordVersions].
- `GET /v2/{collection}/{record_id}/versions/{version}` returns the body of Storage
  `GET /records/{id}/{version}`, and 404 "Record version N not found" when Storage returns another version
  [C: `get_record_specific_version_*`; 1415 routes/osdu/storage_records.py:87-114;
  clients/storage_client.py:100-121; ST: GET /records/{id}/{version}, getSpecificRecordVersion].
- Storage 401, 403 and 404 pass through on these reads [1415 services/storage.py:54-94]. All three reads may be
  cached (section 1.5).

### 5.2 Content

- **Routes.** `GET /v2/samplesanalysis/{record_id}/data/{analysis_type}/{content_id}`,
  `GET /v2/fluidmodel/{record_id}/data/{fluid_model_type}/{content_id}` and
  `GET /v2/{collection}/{record_id}/data/{content_id}` for the single-type collections
  [C: `get_data_v2_*`, `get_data_multiple_content_type_*`, `get_data_single_content_type_*`]. Query parameters:
  `content_schema_version` (required), `orient` (`split`, the default, or `records`), `columns_filter`,
  `rows_filter`, `columns_aggregation`; SamplesAnalysis also takes `depth_shift_policy` and `depth_shift_id` [C].
- **Preconditions.** The record must list the dataset in `data.DDMSDatasets`, otherwise 404
  `{"message": "<id> does not exist in current record.", "reason": "Not found."}` [1415 routes/data/api.py:286-290].
  `content_schema_version` must be one of the versions stored for that dataset, otherwise 400 "Schema version
  mismatch for dataset ...: requested ..., stored [...]" [1415 routes/data/api.py:526-549]. A dataset that returns
  no bytes gives 422 [1415 routes/data/api.py:268-273].
- **Versions.** The version inside `content_id` is dropped, and the Dataset service is asked for the unversioned id
  [1415 routes/data/api.py:260,266; services/dataset.py:53-72; clients/dataset_client.py:142-167;
  DS: GET /retrievalInstructions, retrievalInstructions]. *(inference)* The latest registered file is served, and an
  older content version cannot be read through RAFS.
- **Output.** JSON split `{"columns": [...], "index": [...], "data": [[...]]}`, JSON records `[{...}]`, or parquet
  with `Accept: application/x-parquet` (media type `application/x-parquet`). An aggregation forces the split form
  [1415 routes/data/api.py:255-292]. `C` declares only an untyped `application/json` 200 [C].
- **Filters.** `columns_filter` is `A,B`; `rows_filter` is one condition `Column,op,value` with op one of `lt`,
  `gt`, `lte`, `gte`, `eq`, `neq`; `columns_aggregation` is `Column,func` with func one of `mean`, `count`, `max`,
  `min`, `sum`, `describe`; dotted names reach nested fields; invalid filters give 422
  [1415 docs/tutorial/README.md:103-177; deps/validation.py:506-546].
- **Depth shift (SamplesAnalysis).** `depth_shift_policy=definitive`, or `id_provided` with `depth_shift_id`, adds
  `DepthShiftData` and `DepthShiftMetadata` to the JSON; invalid combinations give 400
  [1415 deps/request.py:209-301; routes/samplesanalysis/endpoints.py:181-398]. Delivery verification does not use
  it.
- **Verification recipe.** GET the content with the delivered `content_schema_version`, the `orient` of the form
  sent, and `Cache-Control: no-store`, then compare it with what was sent. The integration tests compare the posted
  split payload with the JSON read, and the JSON read with the parquet read
  [1415 it/tests/samples_analysis/nmr/test_nmr.py:71-94].

### 5.3 Search (a secondary check)

- `GET .../search` returns `{"result": [<record ids>], "offset", "page_limit", "total_size"}`, the record ids being
  taken from the URNs; `GET .../search/data` returns `{"result": <frame>, "offset", "page_limit", "total_size"}`, or
  parquet [1415 routes/common/base_search.py:107-325; routes/utils/search.py:55-84].
- Paging: `offset` from 0; `page_limit` from 1 to 1000 (default 1000) on `search` and from 1 to 100 (default 100) on
  `search/data`; `page_limit` and `total_size` count parquet files; `indexed_start_date` and `indexed_end_date` are
  `YYYY-MM-DD` [C; 1415 deps/validation.py:653-732]. Scope parameters: `basin_id`, `field_id`, `well_id` and
  `wellbore_id` on samplesanalysis; `basin_id`, `field_id` and `wellbore_id` on depthshift; `basin_id` and
  `field_id` on the others [C; 1415 deps/validation.py:735-886]. A malformed scope id gives 400
  [1415 deps/request.py:323-353].
- A SamplesAnalysis record is found only when all of these hold [1415 app/search/ddms_dataset_ids_fetcher.py:93-144,233-290;
  routes/utils/search.py:24-84; services/search.py:52-80; clients/search_client.py:78-92;
  SE: POST /query_with_cursor, queryWithCursor]:
  - its kind matches `{a}:wks:work-product-component--SamplesAnalysis:*`;
  - `data.SampleAnalysisTypeIDs` holds `{data-partition-id}:reference-data--SampleAnalysisType:{code}`, with or
    without a trailing `:`, for a code the type maps to (section 3.3);
  - a URN in `data.DDMSDatasets` has a dataset id containing `:{content_type}-` and the requested schema version;
  - the Search index holds the record.
- A FluidModel record is found only when `data.FluidModelTypeID` equals
  `{data-partition-id}:reference-data--FluidModelType:{BlackOil|Compositional}:`, with the same URN conditions
  [1415 app/search/fluid_model_ids_fetcher.py:42-126].
- *(inference)* Search depends on indexing, so a delivery uses it only as a secondary check.

### 5.4 Delete

- `DELETE /v2/{collection}/{record_id}` returns 204 with no body [C: `soft_delete_record_*`]. It calls Storage
  `POST /records/{url-encoded id}:delete` [1415 routes/osdu/storage_records.py:144-159;
  clients/storage_client.py:144-160], which the Storage contract describes as a logical deletion that can be
  reverted [ST: POST /records/{id}:delete, deleteRecord]. Storage 401, 403 and 404 pass through
  [1415 services/storage.py:96-110].
- `C` has no purge route. The Storage client has a `delete_record` method (Storage `DELETE /records/{id}`, the purge)
  that no route calls [1415 clients/storage_client.py:162-180; ST: DELETE /records/{id}, purgeRecord].
- Deleting a record leaves its content in place: the minted `dataset--File.Generic` records and their files (or the
  blobs in blob mode) stay. The RAFS tests purge the dataset records directly
  [1415 it/tests/apply_depth_shift/conftest.py:122-123]; this project removes such ids with Storage
  `POST /records/{id}:delete` instead, never with a purge.

## 6. Limits and errors

### 6.1 Error bodies

| Case | Status | Body | Citation |
| --- | --- | --- | --- |
| an HTTP exception raised by RAFS (model not found, record version not found, 406, kind and reference errors) | as raised | `{"code": <status>, "reason": <detail>}`; `reason` can be a list | [1415 errors/http_error.py:22-29; app/exceptions/exceptions.py:48-109] |
| request validation | 422, or 400 when every error is a header error | `{"code": 422, "reason": "Unprocessable entity.", "errors": [...]}` (400: `"reason": "Bad request."`) | [1415 errors/validation_error.py:34-90; app/models/schemas/errors.py:23-39; ut/osdu/storage_mock_objects.py:409] |
| missing `Content-Type`, schema version mismatch, parquet conversion | 400 | `{"code": 400, "reason": "..."}` | [1415 errors/invalid_header_error.py:22-29; errors/invalid_body_error.py:22-29; app/exceptions/exceptions.py:23-45] |
| content validation | 422 | `{"code": 422, "reason": "Data validation failed.", "errors": {<group>: [...]}}` with groups "Invalid parameters", "Mandatory parameters missing", "Invalid type", "Invalid value", "Missing records in storage", "Unknown errors" | [1415 routes/data/api.py:551-581; errors/data_validation_error.py:22-32] |
| unsupported media type | 415 | `{"code": 415, "reason": "The provided content-type is not supported. ..."}` | [C: component UnsupportedMediaTypeResponse; 1415 errors/unsupported_media_type_error.py:22-29; deps/request.py:85-89] |
| content not in the record, report without source files | 404 | `{"message": "...", "reason": "Not found."}` or `{"message": "entity has no source data"}`, without `code` | [1415 routes/data/api.py:286-290; app/resources/source_renderer.py:45-49] |
| an upstream error from Storage, Schema, Search or Entitlements | the method's expected codes pass through; others: 400, 401, 403, 404, 409, 422, 503 and 504 pass through, anything else becomes 502; a Storage 400 on upsert becomes 422 | `{"code", "reason", "message"}` | [1415 services/error_handlers.py:26-107; services/storage.py:112-120; errors/osdu_api_error.py:84-144] |
| an upstream error from the Dataset calls of a content write or read | the upstream status, unchanged (these methods have no translation) | `{"code", "reason", "message"}` | [1415 services/dataset.py:53-111; errors/osdu_api_error.py:84-116] |

As everywhere in this project, the ledger stores these bodies only after redaction.

### 6.2 Status codes

`C` lists 200 or 204 and 422 for most operations, adds 400 and 415 on the content POSTs, and lists only 200 for
`info` [C; Appendix A]. The code also returns 400 (headers, schema version mismatch, parquet conversion, depth-shift
and scope parameters), 401 and 403 (passed through, and 403 for a missing token), 404 (record, model, content,
source), 406 (schema version, `Accept`), 409, 503 and 504 (passed through), 415, 501 (a depth-shift path that is not
implemented) and 502 (other upstream codes) [sections 1.2, 1.3, 2.3 to 2.4, 5.2, 6.1;
1415 routes/samplesanalysis/endpoints.py:264-273].

### 6.3 Limits

- Record POST: no batch limit in the fetched code; the records of one request are validated against their schemas
  concurrently, and the integrity check queries Storage 100 ids at a time
  [1415 deps/validation.py:157-193,239-273; settings/app.py:64].
- Content POST: one table per request, held in memory; no size limit or chunking in the fetched code (section 2.4).
- Search: RAFS reads the Search service in pages of 1000 records [1415 services/search.py:25]; the response page
  limits are in section 5.3.
- Core calls: `REQUEST_TIMEOUT`, default 15 seconds (section 2.4).

## 7. Fit with the Wellbore DDMS v3 shape, and what a RAFS shape needs

### 7.1 Comparison

`docs/interfaces-design.md` section 5.4 describes the `ddms` route type with the shape `wellboreDdmsV3`:
`POST /{collection}`, `/{collection}/{id}/data`, `/{collection}/{id}/sessions`. Compared with the pinned Wellbore
contract:

| Aspect | Wellbore DDMS v3 | RAFS v2 | Fit |
| --- | --- | --- | --- |
| Base | server `/api/os-wellbore-ddms`, paths `/ddms/v3/{collection}...` [WB] | no server; paths `/api/rafs-ddms/v2/{collection}...` [C] | base path and version are settings |
| Record upsert | `POST /ddms/v3/{collection}`, required body: an array of `Record` (required `kind`, `acl`, `legal`, `data`) [WB: POST /ddms/v3/welllogs, post_welllog_osdu; component Record] | `POST /v2/{collection}`, an array of records with the same required fields (section 3.1); `Content-Type` exactly `application/json`; kind, schema, reference and integrity checks before the write | same call pattern |
| Record response | `CreateUpdateRecordsResponse`: `recordCount`, `recordIdVersions`, `recordIds`, `skippedRecordIds` [WB: component CreateUpdateRecordsResponse] | `recordCount`, `recordIdVersions` (a set), `skippedRecordCount` on the wire; snake_case names in `C` | different: no id lists, skipped ids unknown |
| Content write | `POST /ddms/v3/{collection}/{record_id}/data`, body `application/json` (split orient) or `application/x-parquet`, no query parameters [WB: POST /ddms/v3/welllogs/{record_id}/data, write_record_data] | `POST /v2/{collection}/{record_id}/data` for one content type, `.../data/{content_type}` for several; required query `content_schema_version`; JSON split, JSON records or parquet | same path for three collections, plus a required query parameter; one extra segment for two |
| Sessions | `POST .../{record_id}/sessions` (`mode` required: `overwrite` or `update`), `POST .../sessions/{session_id}/data` (`post_chunk_data`), `PATCH .../sessions/{session_id}` (`state`: `commit` or `abandon`) [WB: components CreateDataSessionRequest, UpdateSessionState] | none | different: one request per table |
| Content write response | an untyped 200 [WB] | an untyped object in `C`; `{"ddms_urn"}` on the wire (plus `updated_wpc_id` in blob mode) | different: the content id is read from the URN |
| Side effects of a content write | not described [WB] | a `dataset--File.Generic` record minted or re-versioned, and a new parent version | RAFS-specific |
| Content read | `GET .../{record_id}/data` and `GET .../{record_id}/versions/{version}/data`, JSON or parquet [WB: GET /ddms/v3/welllogs/{record_id}/data] | `GET .../data[/{content_type}]/{content_id}` with `content_schema_version`; the latest dataset version only | different: needs the content id and schema version; no versioned read |
| Delete | `DELETE /ddms/v3/{collection}/{record_id}` with query `purge` (boolean, default false) [WB: DELETE /ddms/v3/welllogs/{record_id}, del_osdu_welllog] | `DELETE /v2/{collection}/{record_id}`, logical only | RAFS has no purge |
| `data-partition-id` | declared optional on every v3 operation [WB] | declared required [C] | always sent |

*(inference)* RAFS shares the record upsert and the single-type content path with `wellboreDdmsV3`, but its content
contract (required schema version, content-type segment, no sessions, URN response, content-id reads, content
datasets to clean up) needs a shape of its own under the `ddms` route type.

### 7.2 Parameters of a RAFS shape

1. **Base.** Base path `/api/rafs-ddms` and API version `v2`; the prefix keeps two segments (section 1.1).
2. **Collection** per entity type, stated in the catalog (section 2.1).
3. **Accepted kinds** per collection: the kind bases of section 2.1, the authority of the deployment's
   `SCHEMA_AUTHORITY`, any `x.y.z` version. RAFS rejects other kinds with 422.
4. **Record write.** `POST {base}/v2/{collection}`, a JSON array whose batch size is a route setting,
   `Content-Type: application/json` with no parameters. Response: read camelCase names (and accept the snake_case
   names of `C`); match `recordIdVersions` by id, never by position; a `skippedRecordCount` above zero triggers a read
   back per id; FluidModel may add `warning` and the warning ids.
5. **Record path.** `{collection}/{recordId}`, the id URL-encoded with a literal `%` double-encoded; an optional
   pre-check with the collection's pattern.
6. **Content write.** A path template with an optional content-type segment (`.../data` or
   `.../data/{contentType}`); the required query `content_schema_version`; a body form of JSON split, JSON records
   or parquet, sent as `application/json` or `application/x-parquet`; response parsing by storage mode (content id at
   URN segment -2 and schema version at -1 in dataset mode, -1 and -2 in blob mode); the minted child id logged;
   `updated_wpc_id` read in blob mode; the parent's new version read afterwards.
7. **Content read.** `{collection}/{recordId}/data[/{contentType}]/{contentId}` with `content_schema_version`,
   `orient`, `Accept` and `Cache-Control: no-store`.
8. **Versions.** `/{recordId}/versions` and `/{recordId}/versions/{version}`.
9. **Delete.** `DELETE /{recordId}`, 204, logical; the minted content datasets need their own removal through
   Storage.
10. **Content schema discovery.** The schema and type catalogue routes of section 2.5, to check a content type and
    version in preflight.
11. **Headers.** Bearer token and `data-partition-id` always; `x-collaboration` when the flow uses a namespace;
    `correlation-id` optional (RAFS echoes it and sends it to the core services).
12. **Order.** Referenced records before referrers; a record before its content; content writes to one record
    serialised.
13. **Storage mode.** Dataset or blob mode is deployment configuration; it changes the URN parsing and whether a
    child dataset record is minted. The URN shape of the first write shows the mode.
14. **Probes.** `GET {base}/info` without auth; `GET {base}/v2/samplesanalysis/analysistypes` with auth.

### 7.3 RAFS-specific mapping data

- **Content types and versions** per collection (sections 2.1 and 3.3). The single-type collections use the
  collection name as the content type in dataset ids and URNs (`depthshift`, `saturationfunctionset`,
  `reservoirsimulationrockphysicsmodel`); FluidModel uses `blackoilfluidmodel` and `compositionalfluidmodel`. The
  mapping keeps the content schema version of each delivered content, because every read repeats it.
- **Id columns** (section 3.4), each value equal to the URL record id and written as `<id>:` or `<id>:<version>`.
  DepthShift content has exactly one row.
- **Mandatory and search-relevant record fields.** SamplesAnalysis `data.SampleAnalysisTypeIDs` is mandatory and, for
  search, holds partition-prefixed SampleAnalysisType codes of section 3.3. SaturationFunctionSet
  `data.SaturationFunctions` needs at least one `...ID` value. FluidModel `data.FluidModelTypeID` should be set;
  without it the response warns and search never returns the record.
- **`data.DDMSDatasets` belongs to RAFS.** A metadata redelivery preserves it; dataset mode keeps one dataset per
  content type per record.
- **SamplesAnalysesReport** is record only. The source documents named in its `data.Datasets` are not uploaded
  through RAFS; RAFS only serves them back through `source`.
- **masterdata** accepts only the six entity types of section 2.1; Wellbore is not among them.

## 8. What differs between the contract and the code

1. **Record POST response names.** `C` uses `record_count`, `record_id_versions`, `skipped_record_count` and
   `warning_record_ids`; the wire uses camelCase (section 2.3; the tests assert the first three).
2. **`application/parquet`.** The content POST descriptions in `C` call it supported; the code and an integration
   test reject it with 415. `C` never names `application/x-parquet`, the only parquet type accepted
   (section 1.3).
3. **Request bodies.** `C` has no request body on `POST /v2/depthshift/{record_id}/data` and
   `POST /v2/fluidmodel/{record_id}/data/{fluid_model_type}`; for the other three content POSTs it documents only the
   JSON split form, while JSON records and parquet are accepted too (section 3.2). `C` does not mark the record body
   required on `masterdata` and `samplesanalysis`.
4. **Undeclared parameters.** `C` does not declare the path parameter `fluid_model_type` on the five fluidmodel
   operations whose path contains it, nor `analysistype` on the samplesanalysis schema operation; the handlers read
   them from the request's path parameters [1415 routes/v2/data/endpoints.py:379,448;
   routes/common/endpoints.py:154-162]. `C` also omits `x-collaboration` on those five fluidmodel operations and on
   `fluidmodeltypes`, while the code honours it on every operation that calls a core service
   [1415 deps/services.py:34-127]. `correlation-id` and `Accept` are declared nowhere.
5. **Schema version.** `C` marks `content_schema_version` required; the code keeps it optional, with the deprecated
   `Accept` form still accepted after its stated sunset of 2026-05-31 (section 1.2).
6. **Content write response.** `C` declares an untyped object; the body is `{"ddms_urn"}`, plus `updated_wpc_id` in
   blob mode (section 2.4).
7. **Content read response.** `C` declares only `application/json`; the code also answers `application/x-parquet`
   (section 5.2).
8. **Example.** The samplesanalysesreport example in `C` is not a valid body: it has no `data` object (its business
   fields sit at the top level), and its kind uses the literal authority `schema_authority`, which the kind check
   rejects with the default `SCHEMA_AUTHORITY=osdu`
   [C: POST /v2/samplesanalysesreport, post_records_api_rafs_ddms_v2_samplesanalysesreport_post].
9. **Status codes.** `C` lists 200 or 204 and 422 for most operations; the code returns more (section 6.2).
10. **Repository documents that lag the code** (the code is authoritative):
    - the tutorial posts to `/api/rafs-ddms/masterdata` without `/v2`, shows a single-object body and a bare
      `id:version` response, uses the deprecated `Accept: */*;version=1.0.0`, and links `../spec/openapi.json`
      [1415 docs/tutorial/README.md:22,43-68,85,144,162,176];
    - `docs/auth/README.md` has no DepthShift section and lists no content, schema, type or search operation for
      FluidModel [1415 docs/auth/README.md:71-79];
    - `docs/rafs_ddms_footprint_v0.28.json` lists 59 reference-data kinds, the master data kinds (Sample at 2.0.0
      and 2.1.0), SamplesAnalysesReport, and SamplesAnalysis with its 49 content types, but not FluidModel,
      DepthShift, SaturationFunctionSet or ReservoirSimulationRockPhysicsModel; its content schema URLs point to
      `app/models/data_schemas/jsonschema/api_v2/`, a folder that does not exist at the pinned commit (the files are
      under `jsonschema/sample_analysis_api_v2/`) [1415 docs/rafs_ddms_footprint_v0.28.json];
    - `app/models/data_schemas/README.md` links the same missing folder and lists 44 families at version 1.0.0 only
      [1415 schemas/README.md:7-52];
    - the README says `x-collaboration` is validated; the code only forwards it (section 1.3).

## 9. What is still open

To verify against a live partition, or in files not read here, before relying on it:

- The wire name of the FluidModel warning ids (`warningRecordIds` is expected), and whether Storage returns
  `recordIdVersions` in input order, which the warning ids depend on.
- Whether a metadata redelivery without `data.DDMSDatasets` drops the URNs (expected from the Storage description,
  section 2.3).
- The storage mode of the target deployment: `POST` one content and read the URN shape.
- The status for a missing token behind the target platform's gateway: the unit and integration tests expect RAFS's
  403 (section 1.3), but a gateway in front of another deployment may answer 401 first.
- Whether the Dataset service resolves an unversioned dataset id to the latest registered file (section 5.2).
- Which error a request gets when several checks fail at once (for example a bad body and a bad `Content-Type`):
  the order in which FastAPI resolves the declared dependencies is not established from these files.
- The status a RAFS record GET answers after a logical delete (it passes Storage's status through); the cleanup rule
  of this project needs 404.
- The roles Storage requires for `PUT /records`, which its contract does not state.
- The content schema files of the analysis types not read here (section 3.4), for their required columns.
- The blob-mode read path for SamplesAnalysis content (section 2.4).

## Appendix A: v2 operations in `C`

Paths are relative to `/api/rafs-ddms/v2`; the codes are the ones `C` lists. The table was checked against `C`: it
holds all 63 v2 operations.

| Method | Path | operationId | Codes in `C` |
| --- | --- | --- | --- |
| POST | `/masterdata` | `post_records_api_rafs_ddms_v2_masterdata_post` | 200, 422 |
| GET | `/masterdata/{record_id}` | `get_record_api_rafs_ddms_v2_masterdata__record_id__get` | 200, 422 |
| DELETE | `/masterdata/{record_id}` | `soft_delete_record_api_rafs_ddms_v2_masterdata__record_id__delete` | 204, 422 |
| GET | `/masterdata/{record_id}/versions` | `get_record_versions_api_rafs_ddms_v2_masterdata__record_id__versions_get` | 200, 422 |
| GET | `/masterdata/{record_id}/versions/{version}` | `get_record_specific_version_api_rafs_ddms_v2_masterdata__record_id__versions__version__get` | 200, 422 |
| POST | `/samplesanalysesreport` | `post_records_api_rafs_ddms_v2_samplesanalysesreport_post` | 200, 422 |
| GET | `/samplesanalysesreport/{record_id}` | `get_record_api_rafs_ddms_v2_samplesanalysesreport__record_id__get` | 200, 422 |
| DELETE | `/samplesanalysesreport/{record_id}` | `soft_delete_record_api_rafs_ddms_v2_samplesanalysesreport__record_id__delete` | 204, 422 |
| GET | `/samplesanalysesreport/{record_id}/source` | `get_source_data_api_rafs_ddms_v2_samplesanalysesreport__record_id__source_get` | 200, 422 |
| GET | `/samplesanalysesreport/{record_id}/versions` | `get_record_versions_api_rafs_ddms_v2_samplesanalysesreport__record_id__versions_get` | 200, 422 |
| GET | `/samplesanalysesreport/{record_id}/versions/{version}` | `get_record_specific_version_api_rafs_ddms_v2_samplesanalysesreport__record_id__versions__version__get` | 200, 422 |
| POST | `/samplesanalysis` | `post_records_api_rafs_ddms_v2_samplesanalysis_post` | 200, 422 |
| GET | `/samplesanalysis/analysistypes` | `get_types_api_rafs_ddms_v2_samplesanalysis_analysistypes_get` | 200, 422 |
| GET | `/samplesanalysis/{analysistype}/data/schema` | `get_content_schema_api_rafs_ddms_v2_samplesanalysis__analysistype__data_schema_get` | 200, 422 |
| GET | `/samplesanalysis/{analysis_type}/search` | `search_endpoint_api_rafs_ddms_v2_samplesanalysis__analysis_type__search_get` | 200, 422 |
| GET | `/samplesanalysis/{analysis_type}/search/data` | `search_endpoint_api_rafs_ddms_v2_samplesanalysis__analysis_type__search_data_get` | 200, 422 |
| GET | `/samplesanalysis/{record_id}` | `get_record_api_rafs_ddms_v2_samplesanalysis__record_id__get` | 200, 422 |
| DELETE | `/samplesanalysis/{record_id}` | `soft_delete_record_api_rafs_ddms_v2_samplesanalysis__record_id__delete` | 204, 422 |
| POST | `/samplesanalysis/{record_id}/data/{analysis_type}` | `post_data_v2_api_rafs_ddms_v2_samplesanalysis__record_id__data__analysis_type__post` | 200, 400, 415, 422 |
| GET | `/samplesanalysis/{record_id}/data/{analysis_type}/{content_id}` | `get_data_v2_api_rafs_ddms_v2_samplesanalysis__record_id__data__analysis_type___content_id__get` | 200, 422 |
| GET | `/samplesanalysis/{record_id}/versions` | `get_record_versions_api_rafs_ddms_v2_samplesanalysis__record_id__versions_get` | 200, 422 |
| GET | `/samplesanalysis/{record_id}/versions/{version}` | `get_record_specific_version_api_rafs_ddms_v2_samplesanalysis__record_id__versions__version__get` | 200, 422 |
| POST | `/saturationfunctionset` | `post_records_api_rafs_ddms_v2_saturationfunctionset_post` | 200, 422 |
| GET | `/saturationfunctionset/data/schema` | `get_content_schema_api_rafs_ddms_v2_saturationfunctionset_data_schema_get` | 200, 422 |
| GET | `/saturationfunctionset/search` | `search_endpoint_api_rafs_ddms_v2_saturationfunctionset_search_get` | 200, 422 |
| GET | `/saturationfunctionset/search/data` | `search_endpoint_api_rafs_ddms_v2_saturationfunctionset_search_data_get` | 200, 422 |
| GET | `/saturationfunctionset/{record_id}` | `get_record_api_rafs_ddms_v2_saturationfunctionset__record_id__get` | 200, 422 |
| DELETE | `/saturationfunctionset/{record_id}` | `soft_delete_record_api_rafs_ddms_v2_saturationfunctionset__record_id__delete` | 204, 422 |
| POST | `/saturationfunctionset/{record_id}/data` | `post_data_single_content_type_api_rafs_ddms_v2_saturationfunctionset__record_id__data_post` | 200, 400, 415, 422 |
| GET | `/saturationfunctionset/{record_id}/data/{content_id}` | `get_data_single_content_type_api_rafs_ddms_v2_saturationfunctionset__record_id__data__content_id__get` | 200, 422 |
| GET | `/saturationfunctionset/{record_id}/versions` | `get_record_versions_api_rafs_ddms_v2_saturationfunctionset__record_id__versions_get` | 200, 422 |
| GET | `/saturationfunctionset/{record_id}/versions/{version}` | `get_record_specific_version_api_rafs_ddms_v2_saturationfunctionset__record_id__versions__version__get` | 200, 422 |
| POST | `/reservoirsimulationrockphysicsmodel` | `post_records_api_rafs_ddms_v2_reservoirsimulationrockphysicsmodel_post` | 200, 422 |
| GET | `/reservoirsimulationrockphysicsmodel/data/schema` | `get_content_schema_api_rafs_ddms_v2_reservoirsimulationrockphysicsmodel_data_schema_get` | 200, 422 |
| GET | `/reservoirsimulationrockphysicsmodel/search` | `search_endpoint_api_rafs_ddms_v2_reservoirsimulationrockphysicsmodel_search_get` | 200, 422 |
| GET | `/reservoirsimulationrockphysicsmodel/search/data` | `search_endpoint_api_rafs_ddms_v2_reservoirsimulationrockphysicsmodel_search_data_get` | 200, 422 |
| GET | `/reservoirsimulationrockphysicsmodel/{record_id}` | `get_record_api_rafs_ddms_v2_reservoirsimulationrockphysicsmodel__record_id__get` | 200, 422 |
| DELETE | `/reservoirsimulationrockphysicsmodel/{record_id}` | `soft_delete_record_api_rafs_ddms_v2_reservoirsimulationrockphysicsmodel__record_id__delete` | 204, 422 |
| POST | `/reservoirsimulationrockphysicsmodel/{record_id}/data` | `post_data_single_content_type_api_rafs_ddms_v2_reservoirsimulationrockphysicsmodel__record_id__data_post` | 200, 400, 415, 422 |
| GET | `/reservoirsimulationrockphysicsmodel/{record_id}/data/{content_id}` | `get_data_single_content_type_api_rafs_ddms_v2_reservoirsimulationrockphysicsmodel__record_id__data__content_id__get` | 200, 422 |
| GET | `/reservoirsimulationrockphysicsmodel/{record_id}/versions` | `get_record_versions_api_rafs_ddms_v2_reservoirsimulationrockphysicsmodel__record_id__versions_get` | 200, 422 |
| GET | `/reservoirsimulationrockphysicsmodel/{record_id}/versions/{version}` | `get_record_specific_version_api_rafs_ddms_v2_reservoirsimulationrockphysicsmodel__record_id__versions__version__get` | 200, 422 |
| POST | `/fluidmodel` | `post_records_api_rafs_ddms_v2_fluidmodel_post` | 200, 422 |
| GET | `/fluidmodel/fluidmodeltypes` | `get_types_api_rafs_ddms_v2_fluidmodel_fluidmodeltypes_get` | 200, 422 |
| GET | `/fluidmodel/{fluid_model_type}/data/schema` | `get_content_schema_api_rafs_ddms_v2_fluidmodel__fluid_model_type__data_schema_get` | 200, 422 |
| GET | `/fluidmodel/{fluid_model_type}/search` | `search_endpoint_api_rafs_ddms_v2_fluidmodel__fluid_model_type__search_get` | 200, 422 |
| GET | `/fluidmodel/{fluid_model_type}/search/data` | `search_endpoint_api_rafs_ddms_v2_fluidmodel__fluid_model_type__search_data_get` | 200, 422 |
| GET | `/fluidmodel/{record_id}` | `get_record_api_rafs_ddms_v2_fluidmodel__record_id__get` | 200, 422 |
| DELETE | `/fluidmodel/{record_id}` | `soft_delete_record_api_rafs_ddms_v2_fluidmodel__record_id__delete` | 204, 422 |
| POST | `/fluidmodel/{record_id}/data/{fluid_model_type}` | `post_data_multiple_content_type_api_rafs_ddms_v2_fluidmodel__record_id__data__fluid_model_type__post` | 200, 400, 415, 422 |
| GET | `/fluidmodel/{record_id}/data/{fluid_model_type}/{content_id}` | `get_data_multiple_content_type_api_rafs_ddms_v2_fluidmodel__record_id__data__fluid_model_type___content_id__get` | 200, 422 |
| GET | `/fluidmodel/{record_id}/versions` | `get_record_versions_api_rafs_ddms_v2_fluidmodel__record_id__versions_get` | 200, 422 |
| GET | `/fluidmodel/{record_id}/versions/{version}` | `get_record_specific_version_api_rafs_ddms_v2_fluidmodel__record_id__versions__version__get` | 200, 422 |
| POST | `/depthshift` | `post_records_api_rafs_ddms_v2_depthshift_post` | 200, 422 |
| GET | `/depthshift/data/schema` | `get_content_schema_api_rafs_ddms_v2_depthshift_data_schema_get` | 200, 422 |
| GET | `/depthshift/search` | `search_endpoint_api_rafs_ddms_v2_depthshift_search_get` | 200, 422 |
| GET | `/depthshift/search/data` | `search_endpoint_api_rafs_ddms_v2_depthshift_search_data_get` | 200, 422 |
| GET | `/depthshift/{record_id}` | `get_record_api_rafs_ddms_v2_depthshift__record_id__get` | 200, 422 |
| DELETE | `/depthshift/{record_id}` | `soft_delete_record_api_rafs_ddms_v2_depthshift__record_id__delete` | 204, 422 |
| POST | `/depthshift/{record_id}/data` | `post_data_single_content_type_api_rafs_ddms_v2_depthshift__record_id__data_post` | 200, 400, 415, 422 |
| GET | `/depthshift/{record_id}/data/{content_id}` | `get_data_single_content_type_api_rafs_ddms_v2_depthshift__record_id__data__content_id__get` | 200, 422 |
| GET | `/depthshift/{record_id}/versions` | `get_record_versions_api_rafs_ddms_v2_depthshift__record_id__versions_get` | 200, 422 |
| GET | `/depthshift/{record_id}/versions/{version}` | `get_record_specific_version_api_rafs_ddms_v2_depthshift__record_id__versions__version__get` | 200, 422 |

Outside v2, `C` has `GET /info` (`get_info_api_rafs_ddms_info_get`, 200 only) and four `dev-samplesanalysis`
operations under `/api/rafs-ddms/dev/samplesanalysis/` (`get_search_*`, `get_search_data_*`, `get_data_dev_*`,
`post_data_dev_*`), which a delivery does not use.
