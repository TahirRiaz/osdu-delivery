# Production DDMS (DSPDM): integration brief

DSPDM (`dspdm-services`) is the Production DDMS "core" contributed by Halliburton (the GitLab description of project
1245 calls it "part of Halliburton's contribution"): a metadata-driven relational store for production master,
operational and reference data, kept in the service's own PostgreSQL or SQL Server databases, not in OSDU Storage
(section 2). Every table is exposed as a business object (BO) and written through one generic save call. This
brief serves the Production DDMS route that stage 7 of `docs/osdu-coverage-plan.md` builds ("route from its brief"). It
lists every call that route makes with the contract it comes from, the payload and identity rules, reads, deletes,
limits, where the pinned contract and the code disagree, and what is still open. The historian time series is a
separate product with its own brief, `osdu/specs/production-timeseries/INTEGRATION.md`; the two meet only in name
(section 10).

## Sources

| Key | What it is | Pinned at |
| --- | --- | --- |
| `C` | `osdu/specs/production-dspdm/swagger-api.json`: OpenAPI 3.0.1, "Production DDMS(core) Service" 1.0; copy of `docs/Swagger-API.json` of project 1245 | `74f4dcba64ee17be80f1574ca27d67a0c4b95e4f` (`sources.json`) |
| `CFG` | `osdu/specs/production-dspdm/{mainservice,spatialservice,volumeservice,wellflowmeasurement,wellstatus}.openapi.yaml`: the swagger configuration files of the main service and the four business API modules (`src/*/src/main/resources/openapi.yaml`); top-level keys `prettyPrint`, `cacheTTL`, `openAPI`, and no `paths` | same commit (`sources.json`) |
| `FILE` | `osdu/specs/core/file/openapi.yaml`, File service, from the core specification set | `sources.json` |
| `STORAGE` | `osdu/specs/core/storage/openapi.yaml`, Storage service, from the core specification set | `sources.json` |
| `1245` | Project 1245, `osdu/platform/domain-data-mgmt-services/production/core/dspdm-services`, branch `main`, source and docs | `74f4dcba64ee17be80f1574ca27d67a0c4b95e4f` (2026-08-19; head of `main` on 2026-09-16) |
| `1525` | Project 1525, `osdu/platform/domain-data-mgmt-services/production/historian/home`, branch `main` (cited once, section 10) | `d61ffc361c81bf6fef46280ffdef1b4917909c61` (2026-09-10; head of `main` on 2026-09-16) |

Path shorthands inside `1245` citations:

| Shorthand | Path in project 1245 |
| --- | --- |
| `main/` | `src/dspdm.msp.mainservice/src/main/java/com/lgc/dspdm/msp/mainservice/` |
| `common/` | `src/dspdm-common/src/main/java/com/lgc/dspdm/core/common/` |
| `config.properties` | `src/dspdm-common/src/main/resources/config.properties` |
| `delegate/` | `src/dspdm-delegate/src/main/java/com/lgc/dspdm/repo/delegate/` |
| `service/` | `src/dspdm-service/src/main/java/com/lgc/dspdm/service/common/` |
| `spatial/` | `src/business-api-spatialservice/src/main/java/com/lgc/dspdm/businessapi/spatialservice/` |
| `volume/` | `src/business-api-volumeservice/src/main/java/com/lgc/dspdm/businessapi/volumeservice/` |
| `wfm/` | `src/business-api-wellflowmeasurement/src/main/java/com/lgc/dspdm/businessapi/wellflowmeasurement/` |
| `ws/` | `src/business-api-wellstatus/src/main/java/com/lgc/dspdm/businessapi/wellstatus/` |
| `test/` | `testing/dspdm-test-core/src/main/java/com/lgc/dspdm/test/core/` |
| `api-docs/` | `docs/modules/pdm-api/pages/` |
| `gc/` | `devops/gc/deploy/` |

How statements are marked: a citation with a contract key (`C`, `CFG`, `FILE`, `STORAGE`) rests on the pinned contract;
a citation with a project key (`1245`, `1525`) rests on that project's source or documentation at the pinned commit and
is not in the contract; "(inference)" marks a conclusion drawn from the cited files that neither states outright.
Documentation pages of project 1245 describe the pre-OSDU product in places; where docs and code disagree, the code
is followed and the difference is listed in section 8.

## 1. Base path, versions, headers and auth

### Base path and versions

- Contract server: `servers[0].url` is `/{MID_PATH}`, description `{HOST}/{MID_PATH}/{API_PATH}`, `MID_PATH` default
  empty [C: servers; CFG: mainservice.openapi.yaml]. Contract version `1.0`; the common data routes (`/save`,
  `/common`, `/delete` and the rest) have no version segment [C: paths].
- The service is a Grizzly server rooted at `/`, port 8086 unless `-p` is given, with Swagger UI files served under
  `/apidocs` [1245 main/GrizzlyServer.java:35-37, 69-91].
- GC deployment: the Istio VirtualService matches the prefix `/api/dspdm/v1/` and rewrites it to `/`
  [1245 gc/templates/virtual-service.yaml:30-40]; the README gives `mid_path: api/dspdm/v1` and Swagger UI at
  `${service_root_path}/apidocs/` [1245 README.md:261-270]. So `POST /save` is `https://{host}/api/dspdm/v1/save` on
  such a deployment (inference). An older test setting uses `SERVICE_ROOT_PATH=.../api/ppdms`
  [1245 README.md:159-168]. The base path is a per-environment parameter.
- Business APIs run in the same server: `startServer` registers `SpatialDataServiceImpl`, `VolumeDataServiceImpl`,
  `WellFlowMeasurementDataServiceImpl` and `WellStatusDataServiceImpl` [1245 main/GrizzlyServer.java:72-82], each under
  `@Path("/" + VersionInfo.API_VERSION)` with `API_VERSION = "v1"` [1245 spatial/SpatialDataServiceImpl.java:79;
  volume/VolumeDataServiceImpl.java:57; wfm/WellFlowMeasurementDataServiceImpl.java:37;
  ws/WellStatusDataServiceImpl.java:31; spatial/api/VersionInfo.java:4, and line 4 of `api/VersionInfo.java` in the
  volume, wfm and ws modules]. Their contract paths (`/v1/spatialdata/...`, `/v1/entities/...`) are relative to the
  same root [C: paths], so behind the GC gateway they are `https://{host}/api/dspdm/v1/v1/spatialdata/...`
  (inference).
- The four business API configuration files still list the servers `/services/dev-businessapi/msp`,
  `/services/qa-businessapi/msp`, `/services/businessapi/msp` and `http://localhost:8080/msp`, and only `bearerAuth`
  [CFG: spatialservice, volumeservice, wellflowmeasurement, wellstatus]. `mainservice.openapi.yaml` carries the same
  server and both security schemes as `C` [CFG: mainservice].
- `MainserviceImpl` and `DspdmRestApi` registrations are commented out; the common routes are served through
  `MetadataChangeRestServiceImpl`, which is `@Path("/")` and extends `MainserviceImpl`
  [1245 main/GrizzlyServer.java:73-75; 1245 main/MetadataChangeRestServiceImpl.java:54-55] (that the inherited
  resource methods are the ones served is an inference).

### Headers

| Header | Rule | Source |
| --- | --- | --- |
| `Authorization: Bearer <token>` | required; passed on to Entitlements and to the Partition service | [C: components.securitySchemes.bearerAuth]; [1245 common/util/auth/EntitlementServiceClient.java:59-90; main/utils/osdu/PartitionServiceClient.java:74-86] |
| `data-partition-id` | required | [C: components.securitySchemes.partationId, apiKey in header]; `tenant_header_name=data-partition-id` [1245 config.properties:67] |
| `Content-Type` | `application/json` for JSON bodies; `POST /save` also consumes `text/plain`; `multipart/form-data` for `/import`, `/parse`, `/parse/bulk`, `/parse/boNames`, `/save/bulk` and the spatial file upload | [C: requestBody content]; [1245 main/MainserviceImpl.java:2705-2708]; [1245 api-docs/write-records.adoc:17-23] |
| `Accept-Language` | optional; the first acceptable language becomes the message locale | [1245 main/BaseController.java:64-72] |
| `unit_test_case_context: true` | optional, test only; adds `executionContext` to the response | [1245 main/BaseController.java:55-59; main/utils/DSPDMResponseSerializer.java:64-68] |

The contract's top-level `security` lists `bearerAuth` and `partationId` as two alternative requirement objects
[C: security]; the service needs both (below).

### Partition check (every route)

- A request filter runs for every request whose path does not start with `health` or `ready`
  [1245 main/RequestContextFilter.java:29-36]. A missing `data-partition-id` is answered 401 with a `text/plain` body
  (the message key `NO_TENANT_ID_FOUND_IN_REQUEST_HEADER`) [1245 main/RequestContextFilter.java:38-44].
- The partition must be listed by the Partition service: `GET {osdu_partition_service_root_path}partitions`, called
  with the caller's `Authorization` [1245 main/utils/osdu/PartitionServiceClient.java:38-43, 62-86]; otherwise 401
  "Cannot authorize user in requested partition" [1245 main/utils/tenant/TenantInitializator.java:59-70]. A non-200
  from the Partition service gives an empty list [1245 main/utils/osdu/PartitionServiceClient.java:67-71], so every
  request is then refused with that 401 (inference). The list is fetched on every request (inference from the same
  lines; no cache is kept there).

### Tenancy and onboarding

- At start-up the service records every partition the Partition service lists as already initialized
  [1245 main/utils/tenant/TenantInitializator.java:46-57; main/GrizzlyServer.java:135-142]. That start-up call has no
  caller token: it carries an Azure client-credentials token when `osdu_azure_auth_token_url` is configured and no
  `Authorization` header otherwise [1245 main/utils/osdu/PartitionServiceClient.java:74-106]; if it fails, the
  recorded list is empty and every partition is onboarded again on its first request after a restart (inference).
- A request for a partition that the Partition service lists but that record does not hold onboards the partition
  inside the request filter: reference data is copied from the tenant `dfs_master_tenant_id` (`dfs-tenant-master`),
  then the search index is rebuilt; failures are logged, not returned
  [1245 main/utils/tenant/TenantInitializator.java:71-73, 87-147; config.properties:70].
- `POST /tenant?tenantId=...&operation=OnBoarding|Enable|Disable|UpdateAccessMap` [C: POST /tenant, tenantOperate]
  needs no entitlement group (permission `Open`); only `OnBoarding` does anything, in a background thread, and the call
  answers 200 "Tenant onboarding successfully." at once; the other three operations are accepted and do nothing
  [1245 main/MainserviceImpl.java:6519-6643].

### Authorization

- DSPDM does not validate the token itself. For each route it resolves a permission to entitlement group names and
  calls the OSDU core-common Entitlements client `authorizeAny(groups)` with the caller's `Authorization` and
  `data-partition-id`; permission `Open` skips the check [1245 main/BaseController.java:29-62;
  common/util/auth/AuthUtils.java:26-60; common/util/auth/EntitlementServiceClient.java:59-90].
- Entitlements failures become errors with the Entitlements status and body; a "User is unauthorized" failure without
  a response becomes 401 "The user is not authorized to perform this action"; anything else 500
  [1245 common/util/auth/EntitlementServiceClient.java:65-80].
- Permission to group map, `osdu_group_permission_map_string` [1245 config.properties:74]:

  | Group | Permissions |
  | --- | --- |
  | `service.storage.admin` | System Admin, the custom business object, unique constraint, search index, relationship and attribute permissions, metadata generation and deletion, Edit, Delete, Export, View, Open |
  | `service.storage.creator` | Custom Attribute, Edit, Delete, Export, View, Open |
  | `service.storage.viewer` | Export, View, Open |

  `Edit` is documented as the save, update, import and parse permission [1245 common/util/DSPDMConstants.java:395-398].
- Permission per route:

  | Route | Permission | Source |
  | --- | --- | --- |
  | `POST /save` | `View`, then `Edit` (`Open` instead of `Edit` when every BO in the body is on the unsecure list: unit templates, user unit settings, user favorite, user search history, user recent tables) | [1245 main/MainserviceImpl.java:2717-2718, 3020-3042; common/util/DSPDMConstants.java:320-357] |
  | `POST /save/bulk` | `View`, then as `/save` | [1245 main/MainserviceImpl.java:2787-2803] |
  | `POST /xmgmt/save` | `Edit` | [1245 main/MainserviceImpl.java:2830-2831] |
  | `POST /delete`, `POST /xmgmt/delete` | `Delete` | [1245 main/MainserviceImpl.java:3343-3344, 3433-3434] |
  | `DELETE /delete/{boName}/{id}` | `Delete` (`Open` for `USER FAVORITE`) | [1245 main/MainserviceImpl.java:3241-3250] |
  | `GET /common/{boName}`, `POST /common`, `POST /common/count` | `View` | [1245 main/MainserviceImpl.java:2457-2458, 2602-2603, 2686-2687] |
  | spatial reads, creates and updates, deletes | `View`, `Edit`, `Delete` | [1245 spatial/SpatialDataServiceImpl.java:104, 168, 305] |

- `api-docs/authentication.adoc` describes a Keycloak token endpoint `/msp/auth/token` of the pre-OSDU product
  [1245 api-docs/authentication.adoc:1-11]; the OSDU build authorizes through Entitlements as above.

### Health

`GET /health` and `GET /ready` [C: getHealth, getReady] are exempt from the filter. They answer 200 when start-up
initialization succeeded and an error response (500) otherwise [1245 main/MainserviceImpl.java:6646-6700;
main/GrizzlyServer.java:93-157]; the contract lists only 200.

## 2. Business objects and where they are stored

- The data model is derived from PPDM 3.9 with modifications [1245 docs/modules/data-footprint/pages/Overview.adoc:59-66].
  Classes [1245 docs/modules/data-footprint/pages/classification.adoc:11-72]:
  - Master data: area, field, pool/reservoir, formation, zone, platform, well, wellbore, well completion, well
    tubular, network, pipeline, facilities, equipment (including well equipment), business associate.
  - Operational data: daily and monthly production and injection volumes, well tests, well production aggregate,
    well activities, well status history and production method history.
  - Reference data (`r_*` tables) and real-time data; models are planned.
- Entity pages include Product Volume Summary, Reporting Entity, RPEN Allocation Factor, Well Test (with allocation
  factor, flow measurement, flow period and measurement), Well Activity, Well Production Method History and Well Status
  History [1245 docs/modules/data-footprint/pages/operational-data.adoc:350-1060], and Equipment, Facility, Field,
  Network, Pipeline, Spatial Description, Well, Wellbore, Well Completion, Well Perforation, Well Directional Survey
  and Well Equipment [1245 docs/modules/data-footprint/pages/master-data.adoc:158-3815]. The usage guide also covers
  hierarchy, flow model, decline analysis, fluid analysis, down time, haul events, risk rank, facility systems,
  reporting entity remarks and well flow measurement [1245 docs/README.adoc:3-21].
- The tables indexed for search are listed in `search_index_table_list` (233 entries, for example `well`, `wellbore`,
  `well_completion`, `well_test`, `product_volume_summary`, `well_flow_measurement`, `well_status`,
  `spatial_description`, `sp_point`) [1245 config.properties:68].
- Metadata driven: each BO is described in `BUSINESS_OBJECT` (name, table, sequence for the primary key),
  `BUSINESS_OBJECT_GROUP`, `BUSINESS_OBJECT_ATTR`, and optionally `BUS_OBJ_ATTR_UNIQ_CONSTRAINTS` and
  `BUS_OBJ_RELATIONSHIP`; a new BO can be added without a restart or redeploy, followed by a metadata refresh
  [1245 api-docs/introduction.adoc:21-66]. BO names in requests are upper case with spaces (`WELL`, `WELLBORE`,
  `WELL COMPLETION`, `WELL TEST`, `WELL TEST PUMP`, `WELL VOL DAILY`) [1245 api-docs/write-records.adoc:92-106;
  api-docs/write-children-along-with-parent.adoc:46-51; api-docs/import.adoc:26;
  test/cases/common/SaveDeleteTest.java:119-217]; the server upper-cases BO names
  and attribute names [1245 main/utils/DTOHelper.java:112, 352].
- Storage: a data-model database and a service database configured by `data_model_db_jdbc_*` and
  `service_db_jdbc_*` [1245 README.md:85-138], PostgreSQL or MS SQL
  [1245 docs/modules/getting-started/pages/Whats-new.adoc:6] (the GC chart configures the PostgreSQL driver
  [1245 gc/templates/configmap.yaml:24-38]), created by a database job running `restore.sh` from `database/`
  [1245 README.md:21-84].
- The only OSDU services configured are Entitlements, Partition and File, plus optional Azure token settings
  [1245 README.md:107-118; gc/templates/configmap.yaml:29-34]. No Storage or Search client was found in the files
  examined. The File service is used by the spatial file upload only (section 3.3).
- Search uses the internal `BO_SEARCH` table, which save and delete maintain, not OSDU Search
  [1245 api-docs/search.adoc:7-45; config.properties:35-37].
- Change history: save, simple import, bulk import and delete record who, what, when, rows affected, old and new
  values in `user_performed_opr` and `bus_obj_attr_change_history`; `r_business_object_opr` is a read-only system
  table [1245 api-docs/change-history-track.adoc:1-94]; `history_change_track_enabled=true`
  [1245 config.properties:36].
- DSPDM's docs describe a real-time time series read, `GET {business service}/v1/wells/{uwi}/timeseries/realtime`
  [1245 docs/modules/pdm-business-api/pages/get-latest-timeseries-data.adoc:1-10]; no such resource is registered by
  `startServer` [1245 main/GrizzlyServer.java:72-82] and the contract has no such path [C: paths].

## 3. The calls a writer makes

### 3.1 Save: `POST /save`

Contract: [C: POST /save, saveOrUpdate], tag Common Data Management; request body `application/json`, schema
`object` with `additionalProperties: object`; only a `default` response.

The body is a map keyed by BO name [1245 main/utils/DTOHelper.java:99-278]. Example (shape of the module's own test
[1245 test/cases/common/SaveDeleteTest.java:119-217] and [1245 api-docs/write-records.adoc:92-106], trimmed):

```json
{
  "WELL": {
    "language": "en",
    "timezone": "GMT+08:00",
    "readBack": true,
    "data": [
      {
        "WELL_ID": null,
        "UWI": "UNIT_TEST_1",
        "WELL_NAME": "TW00001",
        "IS_ACTIVE": true,
        "children": {
          "WELLBORE": {
            "language": "en",
            "timezone": "GMT+08:00",
            "data": [ { "WELLBORE_ID": null, "WELL_UWI": "UNIT_TEST_1", "WELLBORE_UWI": "UNIT_TEST_1" } ]
          }
        }
      }
    ]
  }
}
```

Keys of each BO map (the request key constants are `DSPDM_REQUEST` [1245 common/util/DSPDMConstants.java:1306-1320]).
The `/save` pre-check on the top-level maps answers 400; the request parser (`DTOHelper`) checks every map, children
included, and most of its errors carry no HTTP code, so they answer 500 (section 6); the parser's 400s are marked
below [1245 common/exception/DSPDMException.java:13-47].

| Key | Rule | Source |
| --- | --- | --- |
| `language` | required in every BO map and must be a known language; `/save` accepts only `en` at the top level | [1245 main/MainserviceImpl.java:2735-2740; main/utils/DTOHelper.java:118-129] |
| `timezone` | required in every BO map and must be a known time zone; `/save` accepts only `GMT+hh:mm` or `GMT-hh:mm` between `GMT-12:00` and `GMT+14:00` at the top level | [1245 main/MainserviceImpl.java:2728-2734; main/BaseController.java:78-99; main/utils/DTOHelper.java:130-141] |
| `data` | required, a non-empty array of at most `max_records_to_parse` (10000) rows; message keys `INVALID_INPUT_NO_FOUND_FOR_BO_NAME`, `PAYLOAD_MUST_BE_AN_ARRAY_FOR_BO_NAME`, `INVALID_INPUT_NO_RECORDS_FOR_BO_NAME`, `TOO_MANY_RECORDS_IN_A_SINGLE_REQUEST_FOR_BO_NAME` | [1245 main/utils/DTOHelper.java:248-271; config.properties:20] |
| `readBack` | optional; return the saved rows, read again from the database; once true it stays true for nested maps | [1245 main/utils/DTOHelper.java:142-152; delegate/common/write/BusinessObjectWriteDelegateImpl.java:88-92] |
| `readBackSimple` | optional; return the saved rows without reading them again | [1245 main/utils/DTOHelper.java:154-164; delegate/common/write/BusinessObjectWriteDelegateImpl.java:88-92] |
| `operationName` | optional; `save_and_update` (default) or `bulk_import`; any other value is an error | [1245 main/utils/DTOHelper.java:226-247; common/util/DSPDMConstants.java:2108-2111] |
| `showSQLStats`, `collectSQLScript` | optional diagnostics, honoured because `allow_sql_stats_collection=true`; an unknown `collectSQLScript` option is a 400 | [1245 main/utils/DTOHelper.java:167-198; config.properties:3] |
| `writeNullValues` | optional; print null attributes in the response | [1245 main/utils/DTOHelper.java:217-225] |
| `dspdmUnits` | optional array of `{boAttrName, sourceUnit}`; the listed attributes are converted from the source unit to the stored unit | [1245 common/util/DSPDMConstants.java:2047; main/utils/DTOHelper.java:926-979; test/cases/common/SaveDeleteTest.java:125] |
| `deleteCascade` | delete only (section 5.3) | [1245 main/utils/DTOHelper.java:208-216] |
| `fullDrop` | metadata operations only | [1245 main/utils/DTOHelper.java:200-207] |
| `ownerTenantId` | `/xmgmt/*` only (section 3.2) | [1245 common/util/DSPDMConstants.java:1319] |

Rows:

- Keys are attribute names; the server upper-cases them and trims string values. An attribute not in the BO's
  metadata is kept on save and dropped with a log line on delete [1245 main/utils/DTOHelper.java:332-396].
- `children` holds child BO maps of the same shape [1245 main/utils/DTOHelper.java:342-349]. Children work only for
  relationships defined in `BUS_OBJ_RELATIONSHIP` (an unrelated pair is an error), up to six levels by default, and
  parent key values are propagated to children in the same request
  [1245 api-docs/write-children-along-with-parent.adoc:11-15, 96-107].
- A reference attribute (`IS_REFERENCE_IND`) whose value does not exist in its reference BO is refused with 400
  (`INVALID_VALUE_FOR_REFERENCE_BO_ATTR`) [1245 main/utils/DTOHelper.java:400-417]. A database foreign key violation
  is reported as "These record(s) cannot be inserted or updated due to foreign key violation. Please check the values
  for reference columns." (similar texts for check, primary key, unique, not-null and empty-string constraints)
  [1245 common/util/SQLState.java:197-215; main/BaseController.java:141-149].
- Metadata BOs and the system BOs `USER PERFORMED OPR`, `BUS OBJ ATTR CHANGE HISTORY` and `BO SEARCH` cannot be
  written [1245 main/utils/DTOHelper.java:47-97].
- Mandatory and primary key attributes can be discovered with `POST /common` on BO `BUSINESS OBJECT ATTR`, selecting
  `BO_ATTR_NAME`, `IS_MANDATORY`, `IS_PRIMARY_KEY` [1245 api-docs/write-records.adoc:614-722].
- Accepted date and time formats run from `yyyy-MM-dd'T'HH:mm:ss.SSSX` down to `yyyy-MM-dd`, `HH:mm:ss`, and slash
  variants [1245 api-docs/write-records.adoc:38-76].

Semantics:

- Upsert by primary key: a row with a primary key value is an update, a row without one is inserted with a
  sequence-generated key [1245 api-docs/write-records.adoc:7; api-docs/introduction.adoc:48-50]. An update whose key
  does not exist writes nothing and answers 200 with status INFO (statusCode 0) and "Update executed but no records
  updated. Please make sure primary key values exist." [1245 main/MainserviceImpl.java:3079-3084;
  api-docs/write-records.adoc:522-599].
- Unchanged rows are ignored; on update, attributes missing from the row are left as they are and an explicit `null`
  clears a value; on insert, missing attributes are stored as null
  [1245 api-docs/write-children-along-with-parent.adoc:7-25]. The switches `read_before_update=true` and
  `do_tiny_update=true` are set [1245 config.properties:5-6] (that they drive this is an inference).
- A unique constraint violation fails the call: HTTP 500, statusCode -2, for example "Cannot perform save operation on
  business object 'WELL'. Value 'Solrr' for attribute 'UWI' already exists" [1245 api-docs/write-records.adoc:131-185].
- One transaction per call: `DynamicWriteService.saveOrUpdate` goes through `BusinessObjectWriteDelegate`, which begins
  a JTA transaction, saves the whole map and commits, or rolls back on any error [1245
  service/dynamic/write/DynamicWriteService.java:38-39; delegate/BusinessDelegateFactory.java:83-85;
  delegate/common/write/BusinessObjectWriteDelegate.java:71-87; delegate/BaseTransactionalDelegate.java:66-116].
  The transaction timeout is `data_model_db_transaction_timeout_seconds` [1245 delegate/BaseTransactionalDelegate.java:52],
  120 in the GC chart [1245 gc/templates/configmap.yaml:28]. Inside it: the rows, their children, reporting entity and
  reporting facility rows, the user operation row and the change history rows
  [1245 delegate/common/write/BusinessObjectWriteDelegateImpl.java:61-138].
- The search index is updated after the transaction; a failure there is only logged
  [1245 main/MainserviceImpl.java:3092, 3106-3124].
- `POST /save` routes a BO flagged as an equipment specification or measurement catalog table to the equipment save
  [1245 main/MainserviceImpl.java:2742-2750].

### 3.2 Other write routes

| Route | Contract | Notes |
| --- | --- | --- |
| `POST /save/bulk` | [C: saveBulk], `multipart/form-data` with field `params` | `params` carries the same map and runs the same save; the `/save` language and timezone pre-check is not applied (the checks of section 3.1 in `DTOHelper` still are) [1245 main/MainserviceImpl.java:2763-2809] |
| `POST /import`, `POST /parse`, `POST /parse/bulk`, `POST /parse/boNames` | [C: uploadBinary, parseUploadBinary, bulkImport, getBONamesSheetNames], `multipart/form-data` with `uploadedFile` and `params` | Excel sheets named after BOs; `max_records_to_import=10000` [1245 config.properties:16]; the doc pages name other URLs (`/parse` in `import.adoc`, `/bulk` in `bulk-parse.adoc`) [1245 api-docs/import.adoc:9-30; api-docs/bulk-parse.adoc:13-30] |
| `POST /save/equipmentSpec`, `POST /save/equipmentMeas` | [C: saveOrUpdateEquipmentSpec, saveOrUpdateEquipmentMeas] | equipment catalog tables |
| `POST /xmgmt/save`, `POST /xmgmt/delete` | not in `C` | cross-tenant writes: `ownerTenantId` required (400), allowed only when a valid `TENANT XREF` and `XMGMT BUSINESS OBJECT` row permits the BO (403 otherwise) [1245 main/MainserviceImpl.java:2811-3000, 3404-3667] |

### 3.3 Typed spatial API

| operationId | Method and path | Body | Created response |
| --- | --- | --- | --- |
| `createPointSpatialDesc` | `POST /v1/spatialdata/point-spatialdescriptions` | `GeneratedPointSpatialDescription` | 201 `FullPointSpatialDescription` |
| `createLineSpatialDesc` | `POST /v1/spatialdata/line-spatialdescriptions` | `GeneratedLineSpatialDescription` | 201 `FullLineSpatialDescription` |
| `createPolygonSpatialDesc` | `POST /v1/spatialdata/polygon-spatialdescriptions` | `GeneratedPolygonSpatialDescription` | 201 `FullPolygonSpatialDescription` |
| `createSpatialPoint`, `createSpatialLine`, `createSpatialPolygon`, `createSpatialFile` | `POST /v1/spatialdata/spatialdescriptions/{id}/points`, `.../lines`, `.../polygons`, `.../spfiles` | `GeneratedSpatialPoint`, `GeneratedSpatialLine`, `GeneratedSpatialPolygon`, `GeneratedSpatialFile` | 201 |
| `createSpatialLinePoint`, `createSpatialPolygonBoundary` | `POST /v1/spatialdata/spatialdescriptions/lines/{id}/points`, `.../polygons/{id}/boundaries`, optional query `appendAt` | `GeneratedPoints` | 201 |
| `uploadSpatialFile` | `POST /v1/spatialdata/spatialdescriptions/{id}/files` | `multipart/form-data` (no schema) | 201 `FullSpatialFile` |

All from [C: paths]; `id` is an int32 path parameter.

- The three create bodies require `spatialDescription`, `entityKind` (for example `Equipment`), `entityID` (int32),
  `crs` and `xyCoordinatesUnit`, and carry `point`, `lines` or `polygons`, and `files[{fileID, remark}]`
  [C: components.schemas.GeneratedPointSpatialDescription, GeneratedLineSpatialDescription,
  GeneratedPolygonSpatialDescription, GeneratedSpatialFile].
- Documented errors: 400 "Missing mandatory data.", 401, 403, 404 "Crs is not found from its reference.", 409
  "Existing satial decription.", 422 "Invalid unit input." [C: POST /v1/spatialdata/point-spatialdescriptions,
  responses].
- The 201 example wraps the created object: `{"code": 201, "version": "v1", "message": "Create successfully",
  "data": {"spatialDescriptionID": 165, ...}}` [C: createPointSpatialDesc, 201 example], while the declared schema is
  the object itself.
- Source only: the three creates accept a query parameter `isOverwrite` (default `false`) that deletes an existing
  spatial description and creates a new one [1245 spatial/SpatialDataServiceImpl.java:160-172, 202-211, 241-247];
  `POST .../spfiles` takes an array of `GeneratedSpatialFile` [1245 spatial/SpatialDataServiceImpl.java:863-864]; the
  upload reads the multipart parts `file` and `remark` [1245 spatial/SpatialDataServiceImpl.java:823-837].
- The upload uses the OSDU File service with the caller's `Authorization` and `data-partition-id`
  [1245 spatial/utils/osdu/OSDUFileServiceClient.java:242-251]:
  1. `GET {osdu_file_service_root_path}files/uploadURL` [FILE: GET /v2/files/uploadURL, getLocationFile]; the client
     reads `Location.FileSource` [1245 spatial/utils/osdu/OSDUFileServiceClient.java:74-83, 138-141].
  2. `PUT` to the signed URL: raw bytes with `x-ms-blob-type: BlockBlob` when an Azure token URL is configured,
     otherwise a multipart `file` part with the caller's headers; 200 or 201 expected
     [1245 spatial/utils/osdu/OSDUFileServiceClient.java:85-104].
  3. `POST {root}files/metadata` [FILE: POST /v2/files/metadata, postFilesMetadata] with kind
     `osdu_file_service_meta_kind`, `data.DatasetProperties.FileSourceInfo.FileSource`, ACL owners and viewers
     `data.default.owners@{partition}.group` and `data.default.viewers@{partition}.group` unless configured, and the
     configured legal tags and countries; 201 expected, `id` read from the response
     [1245 spatial/utils/osdu/OSDUFileServiceClient.java:106-176]. The GC chart sets the kind to
     `{partition}:wks:dataset--File.Generic:1.0.0` and the legal tag to `{partition}-{legalTag}` (default
     `default-data-tag`) [1245 gc/templates/configmap.yaml:30-33; gc/values.yaml:20-24].
  4. Downloads use `GET {root}files/{id}/downloadURL` (`SignedUrl`) [FILE: downloadURL]
     [1245 spatial/utils/osdu/OSDUFileServiceClient.java:178-187]; removal uses `DELETE {root}files/{id}/metadata`,
     204 expected [FILE: DELETE /v2/files/{id}/metadata, deleteFileMetadataById]
     [1245 spatial/utils/osdu/OSDUFileServiceClient.java:233-240].

  Each upload therefore creates a `dataset--File.Generic` record in the partition with ACL and legal tags chosen by
  DSPDM's configuration, not by the caller.

## 4. Identities and versions

- Keys are integer surrogate primary keys per BO (`<BO>_ID`, for example `WELL_ID`), drawn from the database
  sequence named in `BUSINESS_OBJECT` [1245 api-docs/introduction.adoc:48-50;
  delegate/common/write/BusinessObjectWriteDelegateImpl.java:47-57]. `DELETE /delete/{boName}/{id}` types `id` as
  int64 [C: removeById]. Natural keys are the unique constraints in `BUS_OBJ_ATTR_UNIQ_CONSTRAINTS` (for example
  `UWI` on `WELL`) [1245 api-docs/introduction.adoc:60-62; api-docs/write-records.adoc:131-185].
- No OSDU record id is produced or accepted by the common API (none in the files examined). The spatial API keys data
  by `entityKind` plus integer `entityID` and returns integer ids (`spatialDescriptionID`, `spPointID`, `spFileID`)
  [C: components.schemas.FullPointSpatialDescription, FullSpatialFile].
- Generated keys come back only with `readBack` or `readBackSimple`; without either, `data` is emptied
  [1245 main/MainserviceImpl.java:3093-3095]. `data` is keyed by BO name; reporting entity and reporting facility rows
  written by the same call are added unless `readBackSimple` is set
  [1245 delegate/common/write/BusinessObjectWriteDelegateImpl.java:129-138].
- Each row is written as `type`, `id` (the key value, or an array for a composite key), `isInserted`, `isUpdated` or
  `isDeleted`, then its attributes; nulls are left out unless `writeNullValues`; paged reads become
  `{"totalRecords": n, "list": [...]}` [1245 main/utils/DSPDMResponseSerializer.java:125-147, 226-234, 303-426].
- Rows have no versions. Change history (section 2) is the only record of earlier values.

## 5. Reads, verification and deletes

### 5.1 Reads

- `POST /common` [C: POST /common, getCustomResponsePost], body `BOQuery`: required `boName`; `language`, `timezone`,
  `selectList`, `criteriaFilters[{boAttrName, operator, values[]}]` (operators EQUALS, NOT_EQUALS, GREATER_THAN,
  LESS_THAN, GREATER_OR_EQUALS, LESS_OR_EQUALS, BETWEEN, NOT_BETWEEN, IN, NOT_IN, LIKE, NOT_LIKE, ILIKE, NOT_ILIKE,
  JSONB_FIND_EXACT, JSONB_FIND_LIKE, JSONB_DOT, JSONB_DOT_FOR_TEXT), `filterGroups`,
  `orderBy[{boAttrName, order ASC|DESC}]`, `pagination{recordsPerPage, pages[]}`, `readAllRecords`,
  `readRecordsCount`, `readUnique`, `readFirst`, `readParentBO`, `readChildBO`, `readMetadata`, `readReferenceData`,
  joins and aggregates [C: components.schemas.BOQuery, CriteriaFilter, FilterGroup, OrderBy, Pagination].
  `readAllRecords` reads at most 10000 rows [1245 api-docs/read-records.adoc:46].
- Response: `data: {"<BO>": {"totalRecords": n, "list": [...]}}` [1245 api-docs/responses.adoc:44-70], plus a
  `dspdmUnits` entry [1245 main/MainserviceImpl.java:2612-2614].
- Page settings: `default_max_records_to_read=20`, `max_page_size=10000`, `max_records_to_read=100000`
  [1245 config.properties:7, 15, 18] (that 20 is the page size used when `pagination` is absent is an inference from
  the setting's name).
- Also: `GET /common/{boName}` [C: getCustomResponseGet]; `POST /common/count` [C: count_1] answering
  `data: {"count": n}` [1245 main/MainserviceImpl.java:2668-2702]; `GET /common/count/{boName}` [C: count_2];
  `POST /search` [C: search_1], exact or like search over `BO_SEARCH` with JSONB_FIND_EXACT or JSONB_FIND_LIKE
  [1245 api-docs/search.adoc:31-45]; `POST /export` and `GET /export/{boName}` [C: export, export_1], answering
  `application/octet-stream`.
- History: `POST /common` on `USER PERFORMED OPR`, `BUS OBJ ATTR CHANGE HISTORY` and `R BUSINESS OBJECT OPR`
  [1245 api-docs/change-history-track.adoc:30-66].
- Spatial: `GET /v1/spatialdata/spatialdescriptions/{id}` [C: getSpatialDescInfoByID],
  `GET /v1/spatialdata/spatialdescriptions-info/{spatialdescription}` [C: getSpatialDescInfo],
  `GET /v1/entities/spatialdata?entityType=&ids=` with optional `verbosityLevel` Standard or Detailed
  [C: getEntitySpatialData], and the `/points`, `/lines`, `/polygons`, `/fileinfo` variants.
- The volume, well flow measurement and well status business APIs are read APIs: GET routes, plus two POST query
  routes in the volume API (`/v1/entities/daily/volumes`, `/v1/wells/daily/volumes/ranges`)
  [1245 volume/VolumeDataServiceImpl.java:57-58, 77-78, 488-490, 967-970;
  wfm/WellFlowMeasurementDataServiceImpl.java:37-38, 56-57; ws/WellStatusDataServiceImpl.java:31-32, 50-51].
  None of these routes is in the contract [C: paths].

### 5.2 Verification after a save (inference)

Read the row back with `POST /common` filtered on the primary key returned by `readBack`, or on the natural key, and
compare the attributes sent. A `status.statusCode` of 1 with `isInserted` or `isUpdated` on the row is success; 0
(INFO) means nothing was applied (sections 3.1 and 6).

### 5.3 Deletes

- `POST /delete` [C: POST /delete, removeByJson], permission `Delete`: the same map shape with primary key values
  only, for example `{"WELL": {"language": "en", "timezone": "GMT+08:00", "data": [{"WELL_ID": 123}]}}`
  [1245 api-docs/delete.adoc:27-45; test/cases/common/SaveDeleteTest.java:345-382].
  - Child rows may be listed under `children`; they must be children of the listed parents, and deleting a parent
    whose children remain is an error [1245 api-docs/delete-with-children.adoc:31-35].
  - `deleteCascade: true` deletes the whole child tree defined in the relationship metadata, grandchildren first,
    and removes the search index rows [1245 api-docs/delete-with-children.adoc:76-98;
    test/cases/common/SaveDeleteTest.java:384-402].
    That path calls `BusinessObjectWriteDelegateImpl` directly instead of the transactional wrapper
    [1245 main/MainserviceImpl.java:3358-3364].
  - Every requested row is read first; if fewer rows are found than requested, or a row belongs to another tenant,
    the call fails with `NO_DELETE_PERMISSION` [1245 delegate/common/write/BusinessObjectWriteDelegateImpl.java:280-287, 415-429].
  - When a delete executes but removes nothing, the answer is 200, INFO, "Delete operation executed but no record was
    deleted. Please make sure primary key values exist and the record(s) are not already deleted."
    [1245 main/MainserviceImpl.java:3375-3381].
  - The response is JSON (`@Produces("application/json")`) [1245 main/MainserviceImpl.java:3320-3323].
- `DELETE /delete/{boName}/{id}` [C: removeById], permission `Delete`: 404 when the id does not exist
  [1245 main/MainserviceImpl.java:3261-3268]; metadata BOs are refused [1245 main/MainserviceImpl.java:3253-3259].
- Both are hard deletes through `dynamicWriteService.delete` (or the cascade path above)
  [1245 main/MainserviceImpl.java:3281, 3358-3364]. A `softDelete` exists in the write service
  [1245 service/dynamic/write/DynamicWriteService.java:48-49; delegate/common/write/BusinessObjectWriteDelegate.java:107-123]
  but these routes do not call it (inference from the handlers cited). Deleted non-null values are recorded in change
  history [1245 api-docs/change-history-track.adoc:92-94].
- Spatial deletes: `DELETE /v1/spatialdata/spatialdescriptions/{id}` with the query `isCascade` (required in the
  contract, default `false`) [C: deleteSpatialDesc], `DELETE .../spfiles/{id}` with `isCascade` [C: deleteSpatialFile],
  and `deleteSpatialPoint`, `deleteSpatialLine`, `deleteSpatialLinePoint`, `deleteSpatialPolygon`,
  `deleteSpatialPolygonBoundary` [C: paths].

## 6. Limits and errors

### Limits

| Setting | Value | Source |
| --- | --- | --- |
| rows per BO `data` array | `max_records_to_parse=10000` (enforced) | [1245 config.properties:20; main/utils/DTOHelper.java:250-259] |
| other save limits | `max_records_to_save=100000`, `max_batch_insert_size=500`, `max_batch_update_size=500`, `max_batch_delete_size=500`, `max_bytes_to_save=10485760`, `max_jsonb_length_to_save=10485760`, `max_allowed_length_for_string_data_type=10000`, `max_sql_in_statement_args_count=256` | [1245 config.properties:19-27] |
| import and export | `max_records_to_import=10000`, `max_records_to_export=10000` | [1245 config.properties:16-17] |
| reads | `max_page_size=10000`, `max_records_to_read=100000`, `default_max_records_to_read=20` | [1245 config.properties:7, 15, 18] |
| child levels on save | six by default | [1245 api-docs/write-children-along-with-parent.adoc:15] |
| transaction timeout | `data_model_db_transaction_timeout_seconds`, 120 on GC | [1245 delegate/BaseTransactionalDelegate.java:52; gc/templates/configmap.yaml:28] |

Gateway rate limit (GC chart): an Envoy local rate limit filter is added only when `global.autoscalingMode` is
`requests` [1245 gc/templates/rate-limits.yaml:16-58]; the default mode is `cpu` [1245 gc/values.yaml:6]. The template
reads `limits.maxTokens`, `limits.tokensPerFill` and `limits.fillInterval`
[1245 gc/templates/rate-limits.yaml:44-47], documented with defaults 12, 12 and `1s` [1245 gc/README.md:90-96], while
the chart's `values.yaml` sets `limits.max_tokens: 100`, `tokens_per_fill: 100`, `fill_interval: "1s"`, under other
key names [1245 gc/values.yaml:64-67]. The effective limit is deployment specific; the route type treats throttling
as retryable.

### Response envelope

- Contract: `DSPDMResponse` with `status` (string enum INFO, SUCCESS, PARTIAL_SUCCESS, WARNING, ERROR, FATAL,
  SESSION_TIMEOUT), `messages[]` (`DSPDMMessage`: `message`, `status`, `statusCode`, `statusLabel`), `data{}`,
  `exception` (a Java throwable shape), `executionContext`, `response` [C: components.schemas.DSPDMResponse,
  DSPDMMessage].
- Code: `status` is an object `{statusCode, severity, statusLabel}`, each message is `{message, status{...}}`,
  `exception` is `{message, stackTrace}` with the stack trace as one string, then `data`, `version`, `threadName`,
  `requestTime`, `responseTime`, and `sqlStats` or `sqlScript` when requested
  [1245 main/utils/DSPDMResponseSerializer.java:45-123, 593-614]; the docs and README examples show the same shape
  [1245 api-docs/responses.adoc:3-102; README.md:174-222].
- Status codes: INFO 0, SUCCESS 1, PARTIAL_SUCCESS 2, WARNING -1, ERROR -2, FATAL -3, SESSION_TIMEOUT -4
  [1245 common/util/DSPDMConstants.java:1201-1208].

### HTTP status

- Success, INFO and partial success answer 200 [1245 main/model/DSPDMResponse.java:72-85].
- An error answers with the exception's HTTP code, 500 when it carries none
  [1245 main/model/DSPDMResponse.java:41-69; common/exception/DSPDMException.java:13], and the status becomes WARNING
  when the root error has no cause [1245 main/model/DSPDMResponse.java:52-64]. The docs table pairs WARNING, ERROR,
  FATAL and SESSION_TIMEOUT with 500 [1245 api-docs/responses.adoc:30-42]; the code also sets 400, 401, 403, 404 and
  503 explicitly (sections 1, 3 and 5), from the constants in [1245 common/util/DSPDMConstants.java:2086-2096], and
  passes on the status of an Entitlements refusal (section 1).
- A 200 with status INFO ("no records updated", "no record was deleted") means nothing was applied (sections 3.1 and
  5.3).
- Error bodies carry server stack traces: `print_exception_stack_trace_in_response=true`
  [1245 config.properties:4; main/utils/DSPDMResponseSerializer.java:598-611]. They are redacted before they are
  stored or shown.
- Error texts are resolved from message keys (the resource bundle was not examined); the route type keys on HTTP
  status and `status.statusCode`, not on text.

## 7. What the route type needs

Record route through a DDMS, parameters (from sections 1 to 6):

- Endpoint base (for example `https://{host}/api/dspdm/v1` on GC), data partition, and a credential reference for an
  identity in `service.storage.creator` or `service.storage.admin`, which grant both `Edit` and `Delete`
  [1245 config.properties:74].
- Per interface: BO name; column to attribute mapping; primary key attribute (`<BO>_ID`); natural key attributes (the
  unique constraint) for lookup; optional child BO mappings, whose relationships must exist in the metadata.
- Request constants: `language` = `en`; `timezone` = `GMT+hh:mm` or `GMT-hh:mm` of the source values; `readBack` =
  `true`; optional `dspdmUnits`.
- Batching: at most 10000 rows per BO per request; each request is one transaction (except `deleteCascade`).
- Ledger identity: (partition, BO name, integer primary key) plus the natural key; there is no OSDU id and no
  version.
- Create or update: `POST /save`. To redeliver safely, look the primary key up first with `POST /common` on the
  natural key and send it, since inserting a duplicate natural key fails with 500 (inference from section 3.1).
- Outcome: HTTP 200 with `status.statusCode` 1 and `isInserted` or `isUpdated` on the returned row is success; 0 (INFO)
  means nothing was applied; any other answer is a failure whose body is redacted before it is stored.
- Read back: `POST /common` filtered on the primary key or the natural key; history through
  `BUS OBJ ATTR CHANGE HISTORY` and `USER PERFORMED OPR`.
- Delete: `POST /delete` with primary key values (optionally `deleteCascade`) or `DELETE /delete/{boName}/{id}`; both
  are hard deletes. A missing key fails `POST /delete` with `NO_DELETE_PERMISSION` and gives 404 on the path form.
- Spatial variant: `entityKind`, integer `entityID`, `crs`, `xyCoordinatesUnit`; store `spatialDescriptionID`; expect
  409 on duplicates unless `isOverwrite` is used. A spatial file upload creates a `dataset--File.Generic` record in
  OSDU through the File service; a live test logs that id and removes it at the reversible scope,
  `POST /records/{id}:delete` [STORAGE: POST /records/{id}:delete, deleteRecord], which the contract describes as a
  logical, revertible deletion.

## 8. Contract versus code

1. `security`: the contract lists `bearerAuth` and `partationId` as alternatives [C: security]; the service needs
   both (section 1).
2. `POST /save` declares only a `default` response and `application/json` [C: saveOrUpdate]; the source also consumes
   `text/plain` and answers 200 or the error codes of section 6 [1245 main/MainserviceImpl.java:2705-2761]. `/save/equipmentSpec`, `/save/equipmentMeas` and the
   `/metadata/write/*` routes also declare only `default` [C: paths].
3. `POST /save/bulk`: the summary "Parses data of given text file and saves it and returns it in json format if
   requested" [C: saveBulk] is the source's own annotation text, but the handler reads only the `params` form field
   and saves it like `/save` [1245 main/MainserviceImpl.java:2763-2809].
4. `DSPDMResponse.status` is a string enum in the contract and an object `{statusCode, severity, statusLabel}` in the
   serializer; `exception` is a throwable schema in the contract and `{message, stackTrace}` in the serializer
   (section 6).
5. Spatial schemas expose getter names (`getxRotation`, `getyRotation`, `getzRotation`, `getxScale`, `getyScale`,
   `getzScale`) while the examples use `xRotation`, `xScale` and so on
   [C: components.schemas.GeneratedPointSpatialDescription and its examples]; the 201 examples wrap the object in
   `{code, version, message, data}` while the schema is the bare object [C: createPointSpatialDesc].
6. The spatial creates accept `isOverwrite`, and `POST .../spfiles` takes an array, neither of which the contract shows
   (section 3.3); the upload's multipart parts (`file`, `remark`) are not described [C: uploadSpatialFile].
7. Routes in the source that the contract lacks include `/xmgmt/save`, `/xmgmt/delete`, `/referenceData`, the
   `/hierarchy/*` routes and the `/unit/*` routes [1245 main/MainserviceImpl.java:135-925, 1814-2186, 2514-2581,
   2812, 3405], and every volume, well flow measurement and well status business route (section 5.1).
8. `/health` and `/ready` declare only 200; the source answers 500 when start-up failed (section 1).
9. `POST /tenant` lists four operations; only `OnBoarding` has an effect, asynchronously (section 1).
10. The business API configuration files [CFG] carry legacy servers and no paths; `spatialservice.openapi.yaml`
    declares only `bearerAuth`, although the partition filter applies to every route; `wellstatus.openapi.yaml`
    repeats the title "PDM Domain APIs for Well Flow Measurement" [CFG: wellstatus].
11. Docs versus code: `delete.adoc` and `delete-with-children.adoc` say a successful delete returns an Excel
    `application/octet-stream` file [1245 api-docs/delete.adoc:56-60; api-docs/delete-with-children.adoc:113-117];
    the handler produces JSON [1245 main/MainserviceImpl.java:3320-3323]. `write-records.adoc` says `readBack` brings
    back only the new id [1245 api-docs/write-records.adoc:27-36]; the code returns whole rows (section 4). The
    `responses.adoc` table maps every negative status to 500; the code uses the exception's code (section 6).

## 9. Open questions

- `write-records.adoc` section 3.1 "b. Example" claims that a row carrying a primary key value that does not exist is
  inserted, while its section 3.2 example and the service's INFO message say such a row updates nothing
  [1245 api-docs/write-records.adoc:209-282, 522-599; main/MainserviceImpl.java:3079-3084]. The data access code that
  decides this (`DynamicDAO.saveOrUpdate`) was not examined; test against the target deployment before relying on
  either reading.
- Transactional behaviour of `deleteCascade`, which bypasses the transaction wrapper (section 5.3).
- The texts behind message keys such as `NO_DELETE_PERMISSION` and `TOO_MANY_RECORDS_IN_A_SINGLE_REQUEST_FOR_BO_NAME`.
- The gateway path and rate limit on the target deployment; only the GC chart was examined.
- Whether `fileInfo.fileID` in a spatial file response is the File service record id; the service class that maps
  the upload (`SpatitalBusinessService`) and the response builder (`APIResponseBuilder`) were not examined.
- Which BOs, keys and unique constraints exist on the target deployment: they are metadata, read at run time from
  `BUSINESS OBJECT ATTR` and `BUS OBJ RELATIONSHIP` (section 3.1).

## 10. Relation to the historian time series

DSPDM and the Production DDMS historian (`pddms-timeseries-ingestion`, `pddms-timeseries`, `osdu-pdms-csv-parser`) are
different products under the same `domain-data-mgmt-services/production` group. The historian's knowledge-sharing notes
describe the Halliburton DDMS as a "Separate implementation", "Relational model", "Fixed schema", "Independent but may
converge in future" [1525 docs/knowledge-sharing/20260119_111734.content.md:162-170]. DSPDM stores rows in its own
databases and never writes OSDU records (except the spatial file uploads); the historian's series are defined by OSDU
`ProductionValues` records. Their route types share nothing but the partition and credential; see
`osdu/specs/production-timeseries/INTEGRATION.md`.
