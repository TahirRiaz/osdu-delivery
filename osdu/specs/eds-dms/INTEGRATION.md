# External Data Services (EDS): integration brief

External Data Services (the OSDU projects name them External Data Sources [1247 README.md:1]) bring an external,
OSDU-shaped catalogue into a partition by pulling from it. Two projects make up EDS: the EDS DMS (`eds-dms`), a Java
service that turns the ids of registered external dataset records into retrieval instructions fetched live from the
external source, and `core-external-data-workflow`, the Airflow DAGs that fetch, ingest, naturalize and schedule. This
brief serves the "External Data Services" row of stage 7 in `docs/osdu-coverage-plan.md`. Its finding is that EDS
accepts no pushed data, so it needs no route type of its own: the records that tell EDS what to pull go through the
existing `storage` route (or `manifest`), and an operator-started fetch goes through the stage 6 `workflow` route
(section 2). The brief lists every call those routes make, with its contract, what EDS then does with the records, and
what the pinned files leave open.

## Sources

| Key | What it is | Revision | How it is cited |
| --- | --- | --- | --- |
| `C` | `osdu/specs/eds-dms/openapi.yaml`: the eds-dms OpenAPI 3.1.0 contract, a byte-identical copy of `docs/api/community/v1/openapi.yaml` in project 1247 | commit `793cddb3ddb18050116088d43824af8ee045961f` (2026-08-27), per `osdu/specs/sources.json` | contract: `[C: METHOD path, operationId]` or `[C lines]` |
| `R` | `osdu/specs/workflows/core-external-data-workflow.md`: the `README.md` of project 407 | commit `70e5543ae1e07ae356aec61466535c2b63c8e6f1` (2026-08-26), per `osdu/specs/sources.json` | contract (the project's published description): `[R lines]` |
| `AL` | `osdu/specs/workflows/osdu-airflow-lib.md`: the `README.md` of project 668, `osdu/platform/data-flow/ingestion/osdu-airflow-lib` | commit `ec1394a8016326d25672d51044099a2feb4a7d28` (2026-09-16), per `osdu/specs/sources.json` | `[AL lines]` |
| `W`, `ST`, `SE`, `EN`, `DS` | the Workflow, Storage, Search, Entitlements and Dataset contracts of the core specification set, copied under `osdu/specs/core/workflow`, `storage`, `search`, `entitlements` and `dataset` (`openapi.yaml` in each) | copied from the core specification set on 2026-09-16; `sources.json` records no commit for them | contract: `[W: METHOD path, operationId]`, or `[ST: components.schemas.Name]` for a schema. Paths are relative to each contract's server: `/api/workflow`, `/api/storage/v2/`, `/api/search/v2/`, `/api/entitlements/v2`, `/api/dataset/v1/` |
| (none) | the Secret service | no contract is pinned | every statement about it comes from eds-dms code |
| `1247` | project 1247, `osdu/platform/data-flow/ingestion/external-data-sources/eds-dms`, branch `master` | commit `793cddb3ddb18050116088d43824af8ee045961f`, the head of `master` on 2026-09-16 (the commit `C` was copied from) | source: `[1247 path:lines]` |
| `407` | project 407, `osdu/platform/data-flow/ingestion/external-data-sources/core-external-data-workflow`, branch `master` | commit `70e5543ae1e07ae356aec61466535c2b63c8e6f1`, the head of `master` on 2026-09-16 (the commit `R` was copied from) | source: `[407 path:lines]` |

How statements are marked:

- A statement cited to `C`, `R`, `AL`, `W`, `ST`, `SE`, `EN` or `DS` is what that pinned file says.
- A statement cited to `1247` or `407` is what the code does at the pinned commit. It is source, not a published
  contract.
- **(legacy tests)** marks a statement that rests on the unit tests of project 407 (limits below).
- *Inference:* marks a conclusion drawn from the cited files that they do not state themselves.
- Inside one bracket, a path after a semicolon belongs to the same project as the path before it, until another key
  (`1247`, `407`, `C`, `R`, ...) appears.

Path aliases used in citations:

| Alias | Full path |
| --- | --- |
| `core/` | 1247 `eds-dms-core/src/main/java/org/opengroup/osdu/edsdms/` |
| `core-plus/` | 1247 `eds-dms-core-plus/src/main/java/org/opengroup/osdu/edsdmscoreplus/` |
| `azure/` | 1247 `provider/eds-dms-azure/src/main/java/org/opengroup/osdu/edsdms/provider/azure/` |
| `gc/` | 1247 `provider/eds-dms-gc/src/main/java/org/opengroup/osdu/edsdms/provider/gcp/` |
| `it-core/` | 1247 `testing/eds-dms-test-core/src/main/java/org/opengroup/osdu/edsdms/` |
| `it-azure/`, `it-gc/`, `it-bm/` | 1247 `testing/eds-dms-test-azure/`, `testing/eds-dms-test-gc/` and `testing/eds-dms-test-baremetal/`, each followed by `src/test/java/org/opengroup/osdu/edsdms/` |
| `ingest_dag` | 407 `src/dags/eds_ingest/src_dags_fetch_ingest_scheduler_dag.py` |
| `scheduler_dag` | 407 `src/dags/eds_scheduler/eds_scheduler_dag.py` |
| `naturalization_dag` | 407 `src/dags/eds_naturalization/naturalization_dataset_dag.py` |
| `tests/` | 407 `tests/unit-tests/` |
| `console/` | 407 `console utility/` |
| `postman` | 407 `postman/EDS Scheduler Demo.postman_collection.json`, cited by request name and the line of that request's `name` |

Evidence limits:

1. The three DAG files hold only the Airflow wiring. The fetch, ingest, naturalization, scheduling and email logic is
   imported from the `osdu_airflow.eds` package [407 ingest_dag:16-20; scheduler_dag:14-26; naturalization_dag:19-25],
   pinned as `osdu-airflow~=0.30.0` from the package index of project 668 [407 requirements.txt:3-4]. That project's
   README confirms the package and its EDS modules [AL 58-59, 164, 188], but its code is not among the files read.
2. What that logic does is therefore known only from `R`, the Postman collection, the console utility and the unit
   tests under `tests/`. Those tests import modules that are not in the project
   (`eds_ingest.libs.src_dags_fetch_and_ingest`, `eds_scheduler.eds_email_automation`)
   [407 tests/test_fetch_and_ingest.py:6; tests/test_search_registry_entry.py:10]. Ten of them say "Legacy code" in
   their docstrings (for example [407 tests/test_source_data_job_kind.py:10-11]). CI skips the standalone tests
   (`AZURE_SKIP_STANDALONE_TESTS: "true"`) [407 .gitlab-ci.yml:65-69], and the test image runs
   `tests/unit_tests.sh`, which is not in the project [407 deployments/scripts/azure/dockerFolder/run_standalone_tests_dockerfile:9-11;
   deployments/scripts/azure/bootstrap_ut.sh:10]. The tests show how EDS was designed; they do not prove how it
   behaves today.
3. `RetrievalInstructionsRequest`, `RetrievalInstructionsResponse` and `DatasetRetrievalProperties` come from
   os-core-common [1247 core/api/EdsDmsApi.java:24-25; core-plus/di/CorePlusDatasetServiceImpl.java:24], version
   `7.1.3` [1247 pom.xml:30]. So do the Storage and Entitlements clients eds-dms calls
   [1247 core/service/handlers/StorageHandlerImpl.java:24-32; core/service/EntitlementsAndCacheServiceImpl.java:28-30].
   The shapes below come from the accessors the eds-dms code calls, the integration-test models and the same-named
   schemas in `DS`; the routes those clients call are not in these projects.

## 1. What EDS is

### 1.1 The two projects

- eds-dms "integrates with the Dataset Service to accept and proxy requests for obtaining retrieval instructions for
  externally resing datasets" [1247 README.md:3]. It works with four parties [1247 README.md:11-16]:
  - the Dataset service, which forwards requests for external datasets to eds-dms;
  - Storage, which returns the external dataset records;
  - the Secret service, which returns the secrets needed to authorise against the external source;
  - the external dataset supplier, which returns the retrieval instructions.
- The eds-dms README gives the end-to-end design [1247 README.md:22-28]:
  1. The EDS Scheduler DAG runs the Fetch and Ingest workflows on schedules stored on the data jobs.
  2. Fetch and Ingest creates WP, MD, WPC and external dataset records "pertaining to an external data catalog".
  3. A user passes dataset ids to the Dataset service.
  4. The Dataset service routes each id by kind subtype, and sends external datasets to eds-dms.
  5. eds-dms looks up the registry entry and the secrets, then proxies retrieval from the external system. The README
     also names the data job here; the code never loads it (section 7.2).
- eds-dms does not cache: "Currently, the EDS DMS pulls datasets every time an external dataset is requested"
  [1247 README.md:34].
- The workflow project holds the Airflow DAGs "for managing the ingestion, naturalization, and scheduling of external
  data into the OSDU platform" [R 3]:
  - `eds_ingest` fetches from an external connected source and ingests into OSDU [R 7-9];
  - `eds_naturalization` handles naturalization of WPC datasets [R 39-41]; its DAG description reads "To transfer
    external dataset files into OSDU" [407 naturalization_dag:269];
  - `eds_scheduler` runs `eds_ingest` on a schedule and sends activity reports [R 63-65].

In short: the partition holds configuration records that describe an external source and its fetch jobs. EDS pulls
the metadata on a schedule, and either serves the files on request through the proxy or copies them in.

### 1.2 Records EDS relies on

| Record | Kinds seen | Written by | Read by |
| --- | --- | --- | --- |
| **ConnectedSourceRegistryEntry (CSRE)**: the external source, its credentials as secret names, its retrieval endpoint, its report settings | `osdu:wks:master-data--ConnectedSourceRegistryEntry:1.0.0` [407 console/constants.py:4; 1247 docs/examples/connected_source_registry_entry_example.json:20; 407 tests/Constants.py:110]; `...:2.0.1` in the scheduler fixtures, (legacy tests) [407 tests/Constants.py:1180]; `{partition}:wks:ConnectedSourceRegistryEntry:1.0.0` with id `{partition}:ConnectedSourceRegistryEntry:edsdms-int-test-guid` in the eds-dms integration tests [1247 it-core/EdsDms.java:43, 249-250] | an operator. The console utility and the Postman collection submit it through the `Osdu_ingest` manifest workflow [407 console/service_connected_source_utility.py:39-124; postman "[WORKFLOW] Create ConnectedSourceRegistryEntry" (line 233)]; the eds-dms integration tests use a Storage PUT [1247 it-core/EdsDms.java:242-245, 419-426] | eds-dms reads `DatasetURL` and `SecuritySchemes` [1247 core/service/EdsDmsServiceImpl.java:121-183]. eds_ingest reads its security scheme, `SourceOrganisationID` and `ReferenceValueMappings`, (legacy tests) [407 tests/test_source_data_job_kind.py:18-27; tests/test_build_auth_code_client_credentials.py:37-41; tests/Constants.py:12-13, 43-139]. The scheduler report reads `SmtpSchemes`, (legacy tests) [407 tests/test_email_trigger.py:24-32; tests/test_fetch_date.py:15-41] |
| **ConnectedSourceDataJob (CSDJ)**: one fetch job against a CSRE | `osdu:wks:master-data--ConnectedSourceDataJob:2.0.0` [407 console/constants.py:5; console/service_connected_source_utility.py:158; tests/Constants.py:182]; `...:1.0.0` [407 postman lines 315, 398; 1247 docs/examples/connected_source_data_job_example.json:32]; `{partition}:wks:ConnectedDataSourceJob:1.0.0` in the eds-dms integration tests [1247 it-core/EdsDms.java:42, 305-306] | first an operator, through the console utility or Postman (`Osdu_ingest`, or Storage `PUT /records`) [407 console/service_connected_source_utility.py:126-226; postman lines 315, 398, 618]. Then EDS: `_update_datajob_lastexectutetimestamp(csdj_id, failed_records, max_create_time)` reads the latest version and writes the record back through the osdu_api record client, (legacy tests) [407 tests/test_update_datajob_lastexectutetimestamp.py:32-46] | eds_scheduler selects the jobs whose `ActiveIndicator` is true [R 77-78]. eds_ingest reads the job, looked up through Search, (legacy tests) [407 tests/test_fetch_connected_source_data_job.py:18-35; tests/test_source_data_job_kind.py:18-27]. eds-dms never loads it (section 7.2) |
| **External (proxy) dataset**: a local record that points at a dataset in the source partition | `{partition}:wks:dataset--External:1.0.0` [1247 README.md:38-71; docs/examples/external_dataset_example.json:2-3; it-core/EdsDms.java:381-417]; `osdu:wks:dataset--ConnectedSource.Generic:0.2.0`, which eds_ingest creates for fetched WPCs, (legacy tests) [407 tests/Constants.py:1853] | eds_ingest, (legacy tests) [407 tests/test_wpc_dataset_check_client.py:12-24]; the eds-dms integration tests use a Storage PUT [1247 it-core/EdsDms.java:381-417] | eds-dms reads `data.DatasetProperties` [1247 core/model/externaldataset/ExternalDataset.java:43-76] |
| **Fetched copies** (master data, WP, WPC, datasets) | the source's kinds (`FetchKind`) | eds_ingest [1247 README.md:25; R 23-27], through the `Osdu_ingest` manifest workflow and with the CSDJ's ACL and legal tags, (legacy tests) [407 tests/test_ingest_client.py:27-46; tests/Constants.py:20, 29-30, 1853] | consumers |
| **Naturalized datasets and the WPCs that point to them** | not named in these files | eds_naturalization, which rewrites the WPC's `data.Datasets` [407 naturalization_dag:125-167] | consumers |
| **Secret service entries**: the values behind the CSRE's `*KeyName` fields | not records | the platform or an operator. The eds-dms baremetal tests create them with `POST {SECRETS_URL}/secrets` [1247 it-bm/util/AnthosTestUtils.java:164-169, 189-212] | eds-dms, with `GET {SECRET_API}/secrets/{name}` and the caller's headers [1247 core-plus/di/CorePlusSecretManager.java:48-77; azure/di/AzureSecretManager.java:47-75; gc/di/GcpSecretManager.java:47-76]. eds_ingest, through a secret client that lists secrets and picks values by key, (legacy tests) [407 tests/test_get_secrets_values.py:16-34] |
| **Reference data** the CSRE names | `reference-data--SecuritySchemeType:OAuth2:`, `reference-data--OAuth2FlowType:<flow>:` | the platform | eds-dms requires `TypeID` to be present and takes the flow name from `FlowTypeID` [1247 core/model/externaldataset/SecurityScheme.java:133-136] |
| **Workflow registrations** | `eds_scheduler` [407 postman "[WORKFLOW] Create EDS Scheduler Workflow" (line 97)]; `Eds_ingest` in the Azure packaging [407 deployments/scripts/azure/output_dag_folder.py:7, 22-27] | platform deployment | the Workflow service |

## 2. Route decision for OSDU Delivery

EDS pulls data. Nothing in eds-dms or in the workflow project accepts pushed data:

- the only data endpoint of eds-dms is a read [C: POST /retrievalInstructions, getRetrievalInstructions], and eds-dms
  calls Storage only to read [1247 core/service/handlers/StorageHandlerImpl.java:52-72];
- data reaches the partition because the scheduler starts `eds_ingest` for each active CSDJ, and `eds_ingest`
  fetches from the source and ingests the results [R 11-27, 63-79; 1247 README.md:24-25];
- files reach consumers either through the proxy, on every request [1247 README.md:26-28, 34], or by being copied in
  by `eds_naturalization` [R 39-59].

So delivering through EDS means registration, not transfer. *Inference:* EDS adds no call pattern that the planned
route types lack, so stage 7 needs no EDS route type. It is served as follows.

### 2.1 Route A, recommended: source registration records through `storage`

- **What to send.** A CSRE and its CSDJs, as master-data records through the `storage` route (section 4.1). That keeps
  a single code path. The `manifest` route, which the upstream tools use (section 4.1), is an alternative, not a
  requirement.
- **CSRE checks before sending** (sections 5.1, 5.2 and 7.2):
  - `DatasetURL` is present and non-empty whenever proxy retrieval through eds-dms is expected;
  - `SecuritySchemes` is present and non-empty, and every entry, not only the first, has `Name`, `TypeID`,
    `FlowTypeID` and the keys its flow requires. eds-dms builds every entry on every request, so one bad entry breaks
    retrieval for every dataset of that CSRE;
  - no entry uses the `Implicit` flow, and `GcpServiceAccount` is used only on the GC build;
  - `SecuritySchemes[0]` is the scheme eds-dms uses; if several entries share its `Name`, the first one wins;
  - every `*KeyName` value names a secret that already exists (see "Secrets" below).
- **CSDJ checks before sending** (section 5.3): `ConnectedSourceRegistryEntryID` (the CSRE id followed by a colon, as
  every workflow-project source writes it), `ActiveIndicator`, `FetchKind`, `Filter`, `ConnectedSourceDataPartitionID`,
  `OnIngestionDataPartitionID`, `OnIngestionLegalTags`, `OnIngestionAcl`, `ScheduleUTC`, and a `Workflows` entry with
  `Tag` `FETCH`, `Handler` `eds_ingest`, a `Url` and a `SecuritySchemeName`. *Inference:* the `SecuritySchemeName`
  should equal the `Name` of a scheme on the CSRE, and the `OnIngestionAcl` entries become record ACLs (legacy
  tests), so they should satisfy the Storage ACL item pattern (`data.` groups) [ST: components.schemas.Acl].
- **Kinds and versions.** Take them from the target partition's schemas. The sources disagree: the CSDJ appears as
  `1.0.0` and `2.0.0`, the CSRE as `1.0.0` and `2.0.1`, and the eds-dms integration tests use non-standard kinds
  (section 1.2).
- **Stopping a job.** Set `ActiveIndicator` to `false` [R 77-78] rather than removing the record. If removal is
  needed, use the reversible delete (section 7.4). The project rules forbid `DELETE /records/{id}`, which the Postman
  collection and the eds-dms integration tests use.
- **EDS writes the CSDJ too** (legacy tests, section 6). A redelivery must therefore merge the run-state fields EDS
  owns instead of overwriting them, and a version EDS writes is not drift. *Inference:* the fields are
  `LastSuccessfulRunDateUTC` (the update method's name), `FailedRecords` and `CreateTimeMax` (its arguments); the exact
  list is in osdu-airflow-lib. The upstream console utility does not merge: its update rewrites the whole record with
  fixed `ActiveIndicator`, `ConnectedSourceDataPartitionID`, `ScheduleUTC`, `OnIngestionDataPartitionID` and
  `LastSuccessfulRunDateUTC` values [407 console/service_connected_source_utility.py:160-168;
  console/connected_source_utility.py:91-129].
- **Destination settings are protected.** The ACLs, legal tags, partition ids, token URL, dataset URL and secret names
  inside a CSRE and a CSDJ define where data goes and how it is reached. Under the project rules the engine carries
  them exactly as configured and never changes them unless the user asks for that exact change.
- **Secrets are a precondition, not a delivery.** The CSRE holds only secret names; the values must already exist in
  the partition's Secret service before retrieval or fetch can work (section 5.2). Creating them is platform or
  operator set-up. This matches the project rule that secrets are references only.

### 2.2 Route B, optional and operator-started: an EDS fetch through `workflow`

- **How to trigger** (section 4.2):
  - one job now: `eds_ingest` with `executionContext.connectedSourceDataJobId` set to the job's id. That key is
    inferred (section 5.5), and the registered workflow name must be read from the target environment (section 4.3);
  - every active job in the partition: `eds_scheduler` with an empty body. This has a much wider effect.
- **What the ledger records:** the operator, the time, the CSDJ id, the workflow name, the `runId` and the run's
  `status` as the Workflow service reports it.
- **Limits on traceability.** The run results (ingested and failed ids, errors) exist only as Airflow XComs and in an
  email (section 7.1). The Workflow contract returns a run's ids, times, status and submitter only
  [W: GET /v1/workflow/{workflow_name}/workflowRun/{runId}, getWorkflowRunById], and types the latest task details as
  a bare `object` [W: GET /v1/workflow/{workflow_name}/workflowRun/{runId}/latestInfo, getWorkflowRunDetails]. The
  engine can trace the configuration it delivered and the runs it started, not the records EDS minted (fetched
  copies, proxy datasets, naturalized datasets).
- **Live-test clean-up needs a plan.** The live-OSDU rule requires every id a run creates to be logged and cleaned up.
  Before triggering EDS in a live partition, decide how the engine learns those ids; these files offer no way.
  *Inference:* a search of the destination partition by the fetched kind and the CSDJ's `OnIngestion*` ACL and legal
  tags is the nearest option.
- *Inference:* the legacy code finds the CSDJ and CSRE through Search, (legacy tests)
  [407 tests/test_fetch_connected_source_data_job.py:18-35; tests/test_fetch_connected_source_registry_entry.py:19-36],
  so a trigger sent straight after the records are written may not find a job that is not yet indexed.

### 2.3 Route C, narrow: proxy dataset records through `storage`

- These are `dataset--External` or `dataset--ConnectedSource.Generic` records whose `DatasetProperties` reference a
  registered CSRE and CSDJ, the source partition and the source record id (section 5.4).
- They matter only if the engine itself catalogues files that stay in an external store the Dataset service can
  reach. Otherwise `eds_ingest` creates them.
- Checks: all four `DatasetProperties` present, since one record missing a CSRE id, CSDJ id or source record id fails
  every retrieval request that includes it (section 7.2); one source partition per CSRE, since eds-dms sends all proxy
  records of one CSRE under the first record's `SourceDataPartitionId` (section 7.2).

### 2.4 Out of role

- **Retrieval calls as a delivery step.** `POST /api/eds/v1/retrievalInstructions` is the consumer's read path,
  normally reached through the Dataset service. At most it can serve as an optional check after registration
  (section 7.3).
- **The external side.** Implementing the source's `retrievalInstructions` or search endpoint.
- **EDS's own logic.** Fetch, reference-value mapping, the ACL and legal rewrite, manifest ingestion, naturalization,
  scheduling.
- **Platform set-up.** Registering or deploying DAGs (`POST /v1/workflow` needs `service.workflow.admin`
  [W: POST /v1/workflow, create]); Airflow Variables; the partition flags `eds.enabled` and `system.eds.enabled`;
  entitlement groups (`service.edsdms.user`, `service.secret.viewer`, `service.secret.editor`); Secret service entries.
- **Naturalization.** Triggering `eds_naturalization`; its input is WPC ids that EDS created.

## 3. Base paths, versions, headers and auth

### 3.1 eds-dms

- **Base path `/api/eds/v1/`** [C 12-13]. Every provider sets it as the servlet context path
  [1247 eds-dms-core-plus/src/main/resources/application.properties:2; provider/eds-dms-azure/src/main/resources/application.properties:2;
  provider/eds-dms-gc/src/main/resources/application.properties:2], and the generated server URL is that context path
  [1247 eds-dms-core/src/main/resources/swagger.properties:26]. Deployment routes agree: the Azure chart uses
  `path: /api/eds/v1` [1247 devops/azure/chart/values.yaml:67]; the GC and core-plus Istio routes match the prefix
  `/api/eds` [1247 devops/gc/deploy/templates/virtual-service.yaml:33-35; devops/core-plus/deploy/templates/virtual-service.yaml:16-18].
  The Azure README's example `EDSDMS_BASE_URL` is `/api/eds/dms/v1/` [1247 provider/eds-dms-azure/README.md:109],
  which does not match.
- **Versions.** The contract is OpenAPI `3.1.0` with `info.version` `2.0.0` [C 1, 11]. The build at the pinned commit
  is `0.31.0-SNAPSHOT` on os-core-common `7.1.3` [1247 pom.xml:20, 30]. `GET /info` reports the deployed build
  (section 4.4).
- **Authentication: bearer token.** The contract declares one HTTP bearer scheme, `Authorization`, applied at the top
  level and repeated on the retrieval operation [C 14-15, 100-101, 287-293]. In code:
  - every provider's HTTP filter chain permits all requests [1247 core-plus/security/SecurityConfiguration.java:41;
    azure/security/AzureIstioSecurityConfig.java:41; gc/security/GSuiteSecurityConfig.java:40];
  - only `/retrievalInstructions` is protected, by
    `@PreAuthorize("@authorizationFilter.hasRole('service.edsdms.user')")` [1247 core/api/EdsDmsApi.java:68;
    core/EdsDmsRole.java:19]; the health endpoints are `@PermitAll` and `/info` has no check
    [1247 core/api/HealthCheckApi.java:34, 45; core/api/InfoApi.java:45-48];
  - the Azure gateway exempts only `/`, the Swagger pages, `*/actuator/health` and `*/_ah/**` from authentication
    [1247 devops/azure/chart/values.yaml:75-90]. *Inference:* on Azure, `/info` and `/health/*` still need a token at
    the gateway.
- **The role check.** `hasRole` asks os-core-common's Entitlements client for the caller's groups and throws 403 with
  reason `Access denied` and message `The user is not authorized to perform this action` when none matches
  [1247 core/util/AuthorizationFilter.java:35-38; core/service/EntitlementsAndCacheServiceImpl.java:45-46, 58-65]. When
  the Entitlements call itself fails, the same reason and message come back with the Entitlements status code
  [1247 core/service/EntitlementsAndCacheServiceImpl.java:151-156]. Groups are cached under a hash of the partition id
  and the `Authorization` header [1247 core/service/EntitlementsAndCacheServiceImpl.java:140-166], but only the Azure
  build has a real cache (Redis, or an in-memory cache when `runtime.env` is `local`)
  [1247 azure/cache/GroupsRedisConfig.java:37-50]; the core-plus and GC caches never return anything
  [1247 core-plus/cache/GroupCache.java:33-36; gc/cache/GroupCache.java:32-35]. *Inference:* the client calls the
  Entitlements groups listing, whose response carries `desId` and `groups` [EN: GET /groups, listGroups]; the code reads
  `getDesId()` [1247 core/service/EntitlementsAndCacheServiceImpl.java:61].
- **Role name.** The contract text says "Allowed roles: service.eds.user" [C 29-30; 1247
  eds-dms-core/src/main/resources/swagger.properties:38-39]. The code and every provider README use
  `service.edsdms.user` [1247 eds-dms-core-plus/README.md:49-55; provider/eds-dms-azure/README.md:117-123;
  provider/eds-dms-gc/docs/gc/README.md:160-167]. Those READMEs also list `service.secret.viewer` and
  `service.secret.editor`. *Inference:* because eds-dms reads secrets with the caller's headers (section 5.2), the
  caller also needs read access to those secrets.
- **Headers.** The contract marks `Content-Type` and `data-partition-id` as required header parameters on all four
  operations [C 32-44, 110-122, 138-150, 166-178]. The retrieval endpoint consumes and produces `application/json`
  [1247 core/api/EdsDmsApi.java:67]. Every incoming header whose name starts with `x-provider-` is forwarded to the
  external source [1247 core/service/EdsDmsServiceImpl.java:92-100, 207; core/service/handlers/DatasetHandlerImpl.java:52-54].

### 3.2 Partition feature flag (core-plus and GC builds)

- When the feature `eds.enabled` is off, the filter answers HTTP 400 with `Content-Type: application/json` and the
  JSON string body `"EDS service is disabled."` [1247 core-plus/filter/PartitionFilter.java:38-54;
  gc/filter/PartitionFilter.java:40-66]. The core-plus filter applies to every request. The GC filter lets `/info`, and
  any request without a `data-partition-id` header, through [1247 gc/filter/PartitionFilter.java:47-58]. The Azure
  build has no such filter.
- The GC documentation names the partition properties `eds.enabled` (partition) and `system.eds.enabled` (platform)
  [1247 provider/eds-dms-gc/docs/gc/README.md:43-48].
- core-plus sets `featureFlag.strategy=appProperty` with `eds.enabled=true`
  [1247 eds-dms-core-plus/src/main/resources/application.properties:38-40]; so does the GC `anthos` profile
  [1247 provider/eds-dms-gc/src/main/resources/application-anthos.properties:1-3]. The GC `gcp` profile uses
  `featureFlag.strategy=dataPartition` [1247 provider/eds-dms-gc/src/main/resources/application-gcp.properties:1-4].

### 3.3 The core services on the routes

| Service | Base path | Auth and headers on the operations cited here | Contract |
| --- | --- | --- | --- |
| Workflow | `/api/workflow` + `/v1/...` | bearer `Authorization`; `data-partition-id` required | `W` servers and parameters |
| Storage | `/api/storage/v2/` | bearer `Authorization`; `data-partition-id` required; optional `x-collaboration` | `ST` |
| Search | `/api/search/v2/` | bearer `Authorization`; `data-partition-id` required | `SE` |
| Entitlements | `/api/entitlements/v2` | bearer `Authorization`; `data-partition-id` required | `EN` |
| Dataset | `/api/dataset/v1/` | bearer `Authorization`; `data-partition-id` required | `DS` |
| Secret | no contract pinned. eds-dms defaults `SECRET_API` to `http://secret/api/secret/v2/` on core-plus and GC [1247 eds-dms-core-plus/src/main/resources/application.properties:24-27; provider/eds-dms-gc/src/main/resources/application.properties:24-27]; the Azure chart sets `http://secret/api/secret/v1` [1247 devops/azure/chart/values.yaml:102-103] | the caller's headers, passed through | none |

The workflow project pins Airflow `2.11.2` and `osdu-airflow~=0.30.0` [407 requirements.txt:1-4]; its DAGs support
Airflow 2 and 3 through a compatibility layer [407 ingest_dag:6; scheduler_dag:6, 97-137], and the library's README
prefers the Airflow 3 extra [AL 55-59].

## 4. The calls a writer makes

### 4.1 Routes A and C: records

| Step | Request | Contract and upstream use |
| --- | --- | --- |
| Write CSRE, CSDJ or proxy records | `PUT /api/storage/v2/records`, body an array of records (`id`, `kind`, `acl`, `legal`, `data` required). 201 returns `recordCount`, `recordIds`, `skippedRecordIds`, `recordIdVersions` | [ST: PUT /records, createOrUpdateRecords]. Used for CSDJs by [407 postman "[STORAGE] Create ConnectedSourceDataJobs" (line 398)], which expects 201 with `recordIds`, and for all three record types by [1247 it-core/EdsDms.java:419-426] |
| Alternative: by manifest | `POST /api/workflow/v1/workflow/Osdu_ingest/workflowRun` with `executionContext` `{acl, legal, Payload: {AppKey, data-partition-id}, manifest: {kind: "<authority>:wks:Manifest:1.0.0", MasterData: [...]}}` | [W: POST /v1/workflow/{workflow_name}/workflowRun, triggerWorkflow]. Used by [407 console/service_connected_source_utility.py:39-124, 126-226] and by [407 postman "[WORKFLOW] Create ConnectedSourceRegistryEntry" (line 233); postman "[WORKFLOW][X] Create ConnectedSourceDataJobs" (line 315)], which send a `runId` and expect 200 with `workflowId` and `runId` |
| Change a schedule | `PUT /api/storage/v2/records` with the whole record, including `version` | [ST: PUT /records, createOrUpdateRecords]; [407 postman "[STORAGE] Update The Record Schedule Time" (line 618)] |
| Read back | `GET /api/storage/v2/records/{id}` | [ST: GET /records/{id}, getLatestRecordVersion] |
| See the versions EDS added | `GET /api/storage/v2/records/versions/{id}` returns `recordId` and `versions` | [ST: GET /records/versions/{id}, getRecordVersions] |
| Find jobs | `POST /api/search/v2/query` with `kind`, `query`, `returnedFields` | [SE: POST /query, queryRecords]. The console utility queries `data.Name:"<name>"` [407 console/service_connected_source_utility.py:234-260]; Postman queries ids and returns `id` and `data.ScheduleUTC` [407 postman "[SEARCH] Find ConnectedSourceDataJobs" (line 469)] |
| Remove, reversibly | `POST /api/storage/v2/records/{id}:delete`, 204 | [ST: POST /records/{id}:delete, deleteRecord]: a logical deletion that "can be reverted later" |
| Not used by the engine | `DELETE /api/storage/v2/records/{id}`, 204 | [ST: DELETE /records/{id}, purgeRecord]: physical, "cannot be undone". Used by [407 postman "[STORAGE] Remove The Record" (line 675); 1247 it-core/EdsDms.java:176-206] |

### 4.2 Route B: triggers

| Step | Request | Contract and upstream use |
| --- | --- | --- |
| Check the workflow name | `GET /api/workflow/v1/workflow/{workflow_name}` (role `service.workflow.viewer`), or `GET /api/workflow/v1/workflow?prefix=...` | [W: GET /v1/workflow/{workflow_name}, getWorkflowByName]; [W: GET /v1/workflow, getAllWorkflowsForTenant] |
| Run one job | `POST /api/workflow/v1/workflow/<registered eds_ingest name>/workflowRun`, body `{"executionContext": {"connectedSourceDataJobId": "<CSDJ id>"}}` (the key is inferred, section 5.5); role `service.workflow.creator` | [W: POST /v1/workflow/{workflow_name}/workflowRun, triggerWorkflow]: body `TriggerWorkflowRequest` (`runId` optional, `executionContext`); 200 `WorkflowRunResponse` with `workflowId`, `runId`, `startTimeStamp`, `endTimeStamp`, `status` (`INPROGRESS`, `PARTIAL_SUCCESS`, `SUCCESS`, `FAILED`, `SUBMITTED`), `submittedBy`. Nothing in either project triggers `eds_ingest` this way |
| Run every active job | `POST /api/workflow/v1/workflow/eds_scheduler/workflowRun` with `Content-Type: application/json`, `data-partition-id` and body `{}` | [W: POST /v1/workflow/{workflow_name}/workflowRun, triggerWorkflow]; [407 postman "[WORKFLOW] Start Scheduler Workflow" (line 542)], which expects 200 with `workflowId` and `runId` |
| Poll the run | `GET /api/workflow/v1/workflow/{workflow_name}/workflowRun/{runId}` (role `service.workflow.viewer`) | [W: GET /v1/workflow/{workflow_name}/workflowRun/{runId}, getWorkflowRunById] |

### 4.3 The DAGs a trigger starts

| DAG id | Defined at | Schedule | Params | Tasks, in order |
| --- | --- | --- | --- | --- |
| `eds_ingest` | `dag_id=Constant.EDS_INGEST_DAG_NAME`, a constant from osdu_airflow [407 ingest_dag:185-196]. The literal `eds_ingest` is what the scheduler triggers [407 scheduler_dag:104-109, 127-130] and the CSDJ `Workflows[].Handler` value | none (`get_dag_schedule_kwargs(None)`) | `execution_context`: object, default `{}` | the task `Constant.FETCH_CLIENT` (named `fetch_client` in [R 19]), then `send_email_notification` with trigger rule `all_success` [407 ingest_dag:203-218] |
| `eds_scheduler` | [407 scheduler_dag:72-89] | `AirflowUtility().scheduler_interval()`, `catchup=False` | none | `active_version_gate` first. Airflow 2: `trigger_eds_ingest` (`SchedulerTriggerDagRunOperator`), then `send_email_notification`. Airflow 3: `compute_eds_ingest_runs` (`SchedulerComputeTriggerSpecsOperator`), then a mapped `trigger_eds_ingest` (`TriggerDagRunOperator`, `skip_when_already_exists=True`), then `send_email_notification` (`all_done`) [407 scheduler_dag:93-137] |
| `eds_naturalization` | [407 naturalization_dag:266-277] | none | `execution_context`: object, default `{}` | `start`, `generate_batches`, then N task groups `Naturalization_<i>`, each holding `fetch_wpc_csg_upload_file_<i>` (`none_failed`), then `update_wpc_record` (`all_done`), then `send_email_notification` (`all_done`) [407 naturalization_dag:284-333]. N is the Airflow Variable `eds__config__naturalization_batches`, default 4 [407 naturalization_dag:28-30] |

- `R` lists the task flows without the gate and `start` tasks [R 19, 51, 73].
- **Scheduler gate.** Manual and API-triggered runs always proceed. A scheduled run proceeds only on the instance whose
  Airflow major version matches the Airflow Variable `osdu_airflow_active_version` (default `airflow2`)
  [407 scheduler_dag:42-65].
- **Triggering `eds_scheduler`:** on its schedule [R 67-69], or through the Workflow service (section 4.2). Postman
  registers it with `POST /api/workflow/v1/workflow`, body `{"workflowName": "eds_scheduler",
  "registrationInstructions": {"dagName": "eds_scheduler", "dagContent": null, "etc": "/etc?"}, "description": "EDS
  Scheduler Workflow"}` [407 postman "[WORKFLOW] Create EDS Scheduler Workflow" (line 97); W: POST /v1/workflow,
  create].
- **Triggering `eds_ingest`:** `R` lists "Manual via Workflow API", "Manual via Airflow UI" and "Queued by EDS Scheduler
  DAG" [R 11-15]. The Azure packaging writes the registration body
  `[{"workflowName":"Eds_ingest","description":"EDS Fetch and Ingest","registrationInstructions":{"dagName":"Eds_ingest"}}]`
  [407 deployments/scripts/azure/output_dag_folder.py:7, 22-27], whose capitalisation differs from the `eds_ingest` the
  scheduler and the CSDJ handler use.
- **Triggering `eds_naturalization`:** through the Workflow API, from the Airflow UI, or by `eds_ingest` [R 43-47];
  `eds_ingest` reports the naturalization run id as the XCom `eds_naturalization_dag_run_id`
  [407 ingest_dag:85-88].
- **GC and baremetal deployment.** The ingest and scheduler DAG files, not the naturalization file, are rendered with
  `osdu-dag-versioning` (`DAGVersionGenerator(...).add_dag_version()`) [407 deployments/scripts/gc/render_dag_file.py:19;
  deployments/scripts/gc/bootstrap.sh:23-30; deployments/scripts/gc/requirements.txt:1-3;
  deployments/scripts/baremetal/bootstrap.sh:23-30; deployments/scripts/baremetal/requirements.txt:1-3]. All three
  folders are synced to `dags/eds` [407 devops/gc/pipeline/override-stages.yml:6, 67-69, 82-84]. These files do not show what versioning does to the
  registered name, so read the name from the target environment before triggering.

### 4.4 The calls EDS makes itself

These are not calls the engine makes. They define what a fake of eds-dms, or a live check, has to satisfy.

| From | Call | Contract |
| --- | --- | --- |
| eds-dms | the Entitlements groups of the caller, for the role check (section 3.1) | os-core-common client; *Inference:* [EN: GET /groups, listGroups] |
| eds-dms | the proxy and CSRE records, at most 100 ids per call, with the caller's headers [1247 core/service/handlers/StorageHandlerImpl.java:43-72] | os-core-common `IStorageService.getRecords`, returning `MultiRecordInfo`. *Inference:* [ST: POST /query/records, getRecords], whose `records` list has `maxItems: 100` and whose response carries `records`, `invalidRecords`, `retryRecords` |
| eds-dms | `GET {SECRET_API}/secrets/{name}` with the caller's headers (section 5.2) | none pinned |
| eds-dms | `POST {TokenUrl}` with a form body (section 5.2) | the external token endpoint |
| eds-dms | `POST {DatasetURL}/retrievalInstructions` with up to 20 ids (section 7.2) | the external source; shaped like [DS: POST /retrievalInstructions, retrievalInstructions_1] |
| eds_ingest, (legacy tests) | Search for the CSDJ and CSRE; `requests.post` to the source's search URL; the Secret service list; `Osdu_ingest` through the osdu_api workflow client; Airflow `DagRun` and `XCom` for the manifest run; the osdu_api record client for the CSDJ update | [407 tests/test_fetch_connected_source_data_job.py:18-35; tests/test_fetch_connected_source_registry_entry.py:19-36; tests/test_fetch_client.py:35-50; tests/test_get_secrets_values.py:16-34; tests/test_ingest_client.py:27-46; tests/test_check_task_status.py:23-44; tests/test_get_manifest_records.py:26-36; tests/test_update_datajob_lastexectutetimestamp.py:32-46] |

`GET /api/eds/v1/info` returns `VersionInfo`: `groupId`, `artifactId`, `version`, `buildTime`, `branch`, `commitId`,
`commitMessage`, `connectedOuterServices[]` (`name`, `version`) and `featureFlagStates[]` (`name`, `enabled`,
`partition`, `source`) [C: GET /info, info; C 212-286].

## 5. Payload shapes

### 5.1 CSRE fields

| Field | Meaning and use | Sources |
| --- | --- | --- |
| `Name`, `Description` | label of the source | [1247 docs/examples/connected_source_registry_entry_example.json:3, 17; 407 console/service_connected_source_utility.py:71-72] |
| `SourceOrganisationID` | becomes `dataProviderName` in the ingest criteria, (legacy tests) | [407 tests/Constants.py:49, 20] |
| `FullOSDUImplementationIndicator` | Boolean; every example sets it to `false` | [1247 docs/examples/connected_source_registry_entry_example.json:18; it-core/EdsDms.java:255; 407 console/service_connected_source_utility.py:73] |
| `DatasetURL` | **required by eds-dms.** Read from the top level of `data`; the root URL of the external `retrievalInstructions` endpoint. Missing fails the request; empty drops the datasets silently (section 7.2) | [1247 core/service/EdsDmsServiceImpl.java:53, 173-183; core/model/externaldataset/Workflow.java:36-45]. The integration tests set it to the platform Dataset service URL without its trailing slash, and also add a `Parameters` entry titled `DatasetURL` [1247 it-core/EdsDms.java:254, 272-281]. The docs example, the console utility and the Postman CSRE omit it [1247 docs/examples/connected_source_registry_entry_example.json:2-19; 407 console/service_connected_source_utility.py:70-93; postman line 233] |
| `SecuritySchemes[]` | every entry is built, and its secrets read, on every retrieval; eds-dms uses the scheme whose `Name` equals `SecuritySchemes[0].Name` (section 5.2) | [1247 core/service/EdsDmsServiceImpl.java:136-171; core/model/externaldataset/SecurityScheme.java:128-212]. The console utility writes `Type` and `Flow` instead of `TypeID` and `FlowTypeID` [407 console/service_connected_source_utility.py:77-78], which eds-dms rejects with `Required key 'TypeID' missing from security scheme` [1247 core/model/externaldataset/SecurityScheme.java:134, 209-212]. The Postman CSRE writes both `Type` and `TypeID`, and only `FlowTypeID` [407 postman line 233] |
| `SmtpSchemes[]` | scheduler report email settings: `Name`, `SmtpHostKeyName`, `SmtpPort`, `SmtpUserKeyName`, `SmtpPasswordKeyName`, `SmtpSenderMail`, `SmtpReceiverMail[]`, `SmtpStartTLS`, `SmtpSSL`, `SmtpTimeOut`, `SmtpRetryLimit`, `EmailTriggerFrequency`, and the report window `ReportStartDate`, `ReportEndDate`, (legacy tests). With STARTTLS and SSL both false, sending raises `SMTP STARTTLS AND SMTP SSL BOTH CAN NOT BE FALSE` | [407 tests/Constants.py:1097-1112, 1156-1171, 1251, 1332-1333; tests/test_fetch_date.py:22-41; tests/test_email_trigger.py:46-57] |
| `ReferenceValueMappings` | maps `target:<ReferenceType>` to `{<target value>: [<source values>]}`; the fetched records' reference values are replaced with it, (legacy tests). With `{"Offshore": ["Off", ...]}`, `...OperatingEnvironment:Off:` becomes `...OperatingEnvironment:Offshore:` | [407 tests/Constants.py:12, 69-106, 390, 895; tests/test_clean_search_records.py:15-23] |
| `AgreementIDs`, `Parameters[]` | only in the eds-dms integration tests | [1247 it-core/EdsDms.java:256-281] |
| `LastSuccessfulRunDateUTC` | only on the Postman CSRE | [407 postman line 233] |

### 5.2 Security schemes and flows

Every scheme needs `Name`, `TypeID` and `FlowTypeID`; the value of `TypeID` is not checked
[1247 core/model/externaldataset/SecurityScheme.java:133-135]. The flow is the text after
`reference-data--OAuth2FlowType:` in `FlowTypeID`, once everything from the third colon on has been removed
[1247 core/model/externaldataset/SecurityScheme.java:135-136; core/util/RecordIdUtil.java:26-32], and it is looked up
in `GRANT_TYPES` by that exact name [1247 core/model/externaldataset/SecurityScheme.java:87-94, 138].

| Flow | Further required keys | Token request in the provider builds | Sources |
| --- | --- | --- | --- |
| `ClientCredentials` | `TokenUrl`, `ClientIDKeyName`, `ClientSecretKeyName`, `ScopesKeyName` | form body `grant_type=client_credentials`, `scope`, `client_id`, `client_secret` | [1247 core/model/externaldataset/SecurityScheme.java:45-50, 190-191; core-plus/oauth/CorePlusOauthClientCredentials.java:46-54] |
| `PasswordCredentials` | `TokenUrl`, `ClientIDKeyName`, `ClientSecretKeyName`, `UsernameKeyName`, `PasswordKeyName`, `ScopesKeyName` | form body `grant_type=password`, `username`, `password`, `client_id`, `client_secret`, `scope` | [1247 core/model/externaldataset/SecurityScheme.java:78-85, 192-193; core-plus/oauth/CorePlusOauthPasswordCredentials.java:48-58] |
| `RefreshToken` | `TokenUrl`, `ClientIDKeyName`, `ClientSecretKeyName`, `ScopesKeyName`, `RefreshTokenKeyName` | form body `grant_type=refresh_token`, `scope`, `client_id`, `client_secret`, `refresh_token` | [1247 core/model/externaldataset/SecurityScheme.java:52-58, 194-195; core-plus/oauth/CorePlusOauthRefreshCredentials.java:46-55] |
| `AuthorizationCode` | `TokenUrl`, `CallbackUrl`, `ClientIDKeyName`, `ClientSecretKeyName`, `ScopesKeyName`, `RefreshTokenKeyName`; the secret named by `RefreshTokenKeyName` is used as the authorization code | form body `grant_type=authorization_code`, `scope`, `client_id`, `client_secret`, `code`, `redirect_uri` (the `CallbackUrl`) | [1247 core/model/externaldataset/SecurityScheme.java:60-69, 172-177, 198-199; core-plus/oauth/CorePlusOauthAuthorizationCodeCredentials.java:49-59] |
| `GcpServiceAccount` | `GcpServiceAccountKey` (a secret name), `TokenUrl` | GC build only: the secret holds a base64-encoded service-account key; the body is `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer` with a signed `assertion` whose `target_audience` is `osdu`. The other builds keep the default that throws `UnsupportedOperationException` | [1247 core/model/externaldataset/SecurityScheme.java:40-43, 184-185, 196-197; gc/oauth/GcpOauthCredentialsManager.java:49-52; gc/oauth/GcpServiceAccountCredentials.java:35, 39-80; core/service/oauth/OAuthCredentialsManager.java:31-33] |
| `Implicit` | `AuthorizationUrl`, `CallbackUrl`, `ClientIDKeyName`, `ScopesKeyName` are checked (and the two secrets read), then the scheme is rejected with `Implicit flow not supported` | none | [1247 core/model/externaldataset/SecurityScheme.java:71-76, 200-202] |

- **Which token code runs.** core-plus, Azure and GC each register a `@Primary` OAuth provider and credentials
  manager [1247 core-plus/oauth/CorePlusOauthProviderImpl.java:41-44; core-plus/oauth/CorePlusOauthCredentialsManager.java:26-28;
  azure/oauth/AzureOAuthProviderImpl.java:37-39; azure/oauth/AzureOauthCredentialManager.java:24-26;
  gc/oauth/GcpOauthProviderImpl.java:38-41; gc/oauth/GcpOauthCredentialsManager.java:25-27]. All three POST the form
  body to `TokenUrl`, accept any 2xx, and read `access_token`, falling back to `id_token`
  [1247 core-plus/oauth/CorePlusOauthProviderImpl.java:68-105; azure/oauth/AzureOAuthProviderImpl.java:45-84;
  gc/oauth/GcpOauthProviderImpl.java:46-85]. The Azure and GC credential classes build the same bodies as core-plus.
  The core defaults (grant parameters in the query string, a Basic header for client credentials, only HTTP 200
  accepted, `access_token` only) [1247 core/service/oauth/OAuthProviderImpl.java:37-82;
  core/service/oauth/OAuthClientCredentials.java:34-58] are not the ones these builds use.
- **How the key names are resolved.** Each `*KeyName` value, and `GcpServiceAccountKey`, is the name of a secret.
  eds-dms reads it with `GET {SECRET_API}/secrets/{name}`, passing the incoming request's headers, and parses the
  answer as a v2 secret when the URL contains `v2` and as a v1 secret otherwise; the value is the `value` field
  [1247 core-plus/di/CorePlusSecretManager.java:48-77; azure/di/AzureSecretManager.java:47-75;
  gc/di/GcpSecretManager.java:47-76]. The models carry `id`, `key`, `value`, `createdAt`, `enabled`, and in v2 also
  `createdBy`, `secretAcls`, `metadata` [1247 core-plus/model/Secret.java:31-38; core-plus/model/SecretV2.java:34-45].
  The response status is not checked; a body that does not parse gives 500 `Internal Server Error`
  [1247 core-plus/di/CorePlusSecretManager.java:53-64].
- **Keys never read.** `SecretRepoUrl`, `AccessTokenKeyName` and `APIKeyKeyName` have cases in the switch but are in no
  flow's required list, so they are never read [1247 core/model/externaldataset/SecurityScheme.java:40-94, 153-155,
  178-183]. The GC test CSRE's `Audience` is not read either [1247 testing/eds-dms-test-gc/src/test/resources/ConnectedSourceRegistryEntry.json:18;
  gc/oauth/GcpServiceAccountCredentials.java:35].
- **The secrets the tests create.** The baremetal tests post a v2 secret body `{id, key, value, enabled, secretAcls:
  {owners, viewers}, metadata}` to `{SECRETS_URL}/secrets`, and delete with `DELETE {SECRETS_URL}/secrets/{name}`
  [1247 it-bm/util/AnthosTestUtils.java:172-212; it-bm/model/SecretV2.java:31-40].

### 5.3 CSDJ fields

The ingest step turns the CSDJ and the CSRE into a "criteria" dictionary: a test asserts that
`_source_data_job_kind(csdj, csre)` returns `Constants.criteria`, (legacy tests)
[407 tests/test_source_data_job_kind.py:18-27; tests/Constants.py:20, 44-214]. The middle column shows the criteria key
each field feeds.

| CSDJ field | Criteria key | Notes and sources |
| --- | --- | --- |
| `id` | `connectedSourceDataJobId` | [407 tests/Constants.py:20, 210] |
| `Name` | none | the console utility looks jobs up by name [407 console/service_connected_source_utility.py:248-260]; Postman looks them up by id [407 postman line 469] |
| `ConnectedSourceRegistryEntryID` | none shown | the CSRE id followed by a colon in every workflow-project source [407 console/connected_source_utility.py:147-149; postman lines 315, 398, 618; tests/Constants.py:179]; the eds-dms docs example does the same [1247 docs/examples/connected_source_data_job_example.json:29]. In the fixture, the criteria's `connectedSourceRegistryEntryId` equals the `id` of the CSRE passed in (`...32860f`), not this field (`...32840f:`) [407 tests/Constants.py:20, 135, 179] |
| `ActiveIndicator` | none | the scheduler triggers only jobs where it is `True`, and cleans up inactive ones [R 77-78]. The eds-dms integration tests write `Active` instead [1247 it-core/EdsDms.java:311] |
| `FetchKind` | `fetch_kind` | the kind queried at the source |
| `Filter` | `filter_criteria` | a search query string, for example `data.VersionCreationReason: "INCREMENTALFETCH"` [407 console/connected_source_utility.py:158; tests/Constants.py:12] |
| `LimitRecords` | `limit` | [407 tests/Constants.py:149, 20]; Postman uses `2` [407 postman line 398] |
| `ConnectedSourceDataPartitionID` | `ConnectedSourceDataPartitionId` | the partition queried at the source |
| `OnIngestionDataPartitionID` | `onIngestionDataPartitionId` | the destination partition. `OnIngestionPartitionID` also appears in Postman and in the docs example [407 postman line 398; 1247 docs/examples/connected_source_data_job_example.json:25] |
| `OnIngestionSchemaAuthority` | `onIngestionSchemaAuthority` | [407 tests/Constants.py:162] |
| `OnIngestionLegalTags {legaltags, otherRelevantDataCountries}` | `onIngestionLegalTags`, with `status: compliant` added | `R` says only that records get "the appropriate ACL and legal tags" [R 25]; the fixture's output records carry the criteria's ACL and legal tags, (legacy tests) [407 tests/Constants.py:20, 1853] |
| `OnIngestionAcl {owners, viewers}` | `onIngestionAcl` | as above |
| `ScheduleUTC` | none | a cron string per job: `0 13 * * 1` [407 console/service_connected_source_utility.py:166], `0 1 * * *` [407 postman line 398], and the six-field `0 13 * * ? 1` in the docs example [1247 docs/examples/connected_source_data_job_example.json:3]. Schedules are stored on the jobs [1247 README.md:24]; how they are evaluated is inside osdu_airflow |
| `LastSuccessfulRunDateUTC` | `lastSuccessfulRunDateUTC` | *Inference:* the incremental-fetch watermark |
| `CreateTimeMax` | `max_create_time` | passed to the post-run CSDJ update, (legacy tests) [407 tests/Constants.py:163; tests/test_update_datajob_lastexectutetimestamp.py:42-43] |
| `FailedRecords[]` | `failedRecords` | ids of records that failed in an earlier run, (legacy tests) [407 tests/Constants.py:145-147] |
| `Workflows[]` | `search_url`, from the `FETCH` entry's `Url` | each entry is `{Tag: "FETCH", Handler: "eds_ingest", SecuritySchemeName, Url}` [407 console/service_connected_source_utility.py:185-192; tests/Constants.py:165-172]. The console utility asks for it as the "Search URL" [407 console/connected_source_utility.py:160], and in the fixtures it is the source's search endpoint (`.../api/search/v2/query`) [407 tests/Constants.py:170]; the docs example uses `.../osdu-eds/v1/query` [1247 docs/examples/connected_source_data_job_example.json:19]. `RETRIEVE` entries appear only in the eds-dms integration tests [1247 it-core/EdsDms.java:336-350], and no eds-dms code reads them |

- The query sent to the search URL is not shown. *Inference:* it is built from `fetch_kind`, `filter_criteria` and
  `limit`, as the test names suggest [407 tests/test_fetch_client_filter_criteria.py:56;
  tests/test_fetch_client_limit_condition.py:56], and as the Postman request that sends `{kind: <fetch kind>, query:
  <filter>}` to Search illustrates [407 postman "[SEARCH] Find Records To Ingest" (line 160)].
- The fixtures and the eds-dms docs example show the nested ACL and legal objects as flattened keys
  (`OnIngestionAcl.owners`, `OnIngestionLegalTags.legaltags`) [407 tests/Constants.py:151-178;
  1247 docs/examples/connected_source_data_job_example.json:5-10, 22-28]. The console utility reads them in that form
  from search results [407 console/connected_source_utility.py:123-125] but writes nested objects
  [407 console/service_connected_source_utility.py:169-184].

### 5.4 Proxy dataset fields

- eds-dms reads four fields from `data.DatasetProperties`, each with an `Id` or an `ID` suffix
  [1247 core/model/externaldataset/ExternalDataset.java:47-56]: `ConnectedSourceRegistryEntryId`,
  `ConnectedSourceDataJobId`, `SourceDataPartitionId`, `SourceRecordId`.
- The ingest fixture writes the `ID` spellings, (legacy tests) [407 tests/Constants.py:1853]:
  - the proxy for the source dataset `osdu:dataset--File.Generic:c43254...` is `osdu:dataset--ConnectedSource.Generic:c43254...`,
    with the same id suffix, kind `osdu:wks:dataset--ConnectedSource.Generic:0.2.0`, and the criteria's ACL and legal
    tags;
  - its `SourceRecordID` is the source `dataset--File.Generic` id, and its CSRE, CSDJ and partition values are the
    criteria's;
  - the output is manifest-shaped (`WorkProductComponents`, `Datasets`), and the WPC's `Datasets` holds the literal
    placeholder `dataset_id:`, so the fixture does not show how the WPC comes to point at the proxy.
- The docs example uses another layout (`Source`, `DataJob`, `ExternalDatasetProperties.FileSourceInfo`)
  [1247 docs/examples/external_dataset_example.json:6-19]. eds-dms does not read it; such a record has no CSDJ or CSRE
  id and fails the request (section 7.2).

### 5.5 `executionContext` payloads

- **`eds_ingest`.** The DAG passes `params["execution_context"]` unchanged to
  `FetchAndIngest().fetch_and_ingest(input_params, run_id)` [407 ingest_dag:35-41]; osdu_airflow defines the keys it
  accepts. Evidence for the shape, (legacy tests):
  - the ingest tests call `fetch_and_ingest` with `{"connectedSourceDataJobId": "<CSDJ id>"}`
    [407 tests/Constants.py:1959-1961; tests/test_fetch_and_ingest.py:27]. An earlier class attribute with the same
    name holds both ids [407 tests/Constants.py:287-290]; the later definition replaces it;
  - the scheduler's log test builds a DAG run whose `conf` is `{"connectedSourceDataJobId": ...}` and expects it
    back in the activity log [407 tests/Constants.py:1463-1465, 1633-1646; tests/test_fetch_eds_logs.py:15-38];
    other log fixtures carry both `connectedSourceDataJobId` and `connectedSourceRegistryEntryId`
    [407 tests/Constants.py:1649-1694]. The fixture's DAG id is `testing`, not `eds_ingest` [407 tests/Constants.py:1446].
  - *Inference, unverified:* the Workflow API body is
    `{"executionContext": {"connectedSourceDataJobId": "<partition>:master-data--ConnectedSourceDataJob:<id>"}}`. The
    step that turns `executionContext` into the DAG's `execution_context` param is not in these files.
- **`eds_naturalization`.** `execution_context.items` is a list of `{"id": "<WPC id>", "path": "<direct-download URL,
  optional>"}`; `path` is passed as `signed_url_direct_download` [407 naturalization_dag:45, 81-91; R 55].
- **`eds_scheduler`.** No context; Postman sends `{}` [407 postman line 542].
- **`Osdu_ingest`, as the upstream tools send it.** `{acl, legal, Payload: {AppKey, data-partition-id}, manifest: {kind:
  "osdu:wks:Manifest:1.0.0", MasterData: [<record>]}}` [407 console/service_connected_source_utility.py:43-112];
  Postman uses `<partition>:wks:Manifest:1.0.0` and adds a `runId` [407 postman line 233]. The console utility builds a
  `runId` but hands only `executionContext` to the client [407 console/service_connected_source_utility.py:43-45, 116,
  119].

### 5.6 The retrieval request and response

- **Request** `{"datasetRegistryIds": ["<local proxy record id>", ...]}` [C: POST /retrievalInstructions,
  getRetrievalInstructions; C 188-194]. The body is validated with `@Valid` against the os-core-common model
  [1247 core/api/EdsDmsApi.java:69], whose constraints are not in these files.
- **Response.** The contract gives the 200 body as `type: string` [C 52-57], but the controller returns
  `ResponseEntity<RetrievalInstructionsResponse>` [1247 core/api/EdsDmsApi.java:69-72], which the provider code and
  the integration-test models read as:

  ```json
  {
    "datasets": [
      {
        "datasetRegistryId": "<id as the external source reports it>",
        "retrievalProperties": { "<key>": "<value>" },
        "providerKey": "<provider key the external source reports>"
      }
    ]
  }
  ```

  [1247 core-plus/di/CorePlusDatasetServiceImpl.java:76-88; azure/di/AzureDatasetServiceImpl.java:109-121;
  it-core/model/response/IntTestRetrievalInstructionsResponse.java:25-33;
  it-core/model/response/IntTestDatasetRetrievalDeliveryItem.java:24-33]. The same names and fields make up `DS`'s
  `RetrievalInstructionsResponse` and `DatasetRetrievalProperties` (`retrievalProperties` is an object of objects
  there) [DS: POST /retrievalInstructions, retrievalInstructions_1].
- The provider integration tests expect: Azure, `retrievalProperties` with `signedUrl` and `createdBy` and
  `providerKey` `AZURE` [1247 it-azure/TestEdsDms.java:37-46]; GC, two retrieval properties and `providerKey` `GCP`
  [1247 it-gc/TestEdsDms.java:29-37]; baremetal, two retrieval properties and the configured provider key
  [1247 it-bm/TestEdsDms.java:44-49].

## 6. Identities and versions

- **Record ids and kinds** must match the Storage patterns: id `^[\w\-\.]+:[\w\-\.]+:[\w\-\.\:\%]+$`, kind
  `^[\w\-\.]+:[\w\-\.]+:[\w\-\.]+:[0-9]+.[0-9]+.[0-9]+$` [ST: PUT /records, createOrUpdateRecords]. The eds-dms test
  ids (`{partition}:ConnectedSourceRegistryEntry:edsdms-int-test-guid`) and kinds fit them while not following the
  `master-data--` naming (section 1.2).
- **Version stripping.** eds-dms cuts the CSRE id, the CSDJ id and the source record id at their third colon
  [1247 core/model/externaldataset/ExternalDataset.java:58-60; core/util/RecordIdUtil.java:26-32], so a reference with
  a trailing colon or a version works. It then compares the stripped CSRE id with the `id` Storage returns
  [1247 core/service/EdsDmsServiceImpl.java:135, 149]. *Inference:* an id whose last part itself contains a colon is
  cut too, and then matches no record.
- **The ids sent to the source.** A source record id that does not start with the source partition id gets
  `<SourceDataPartitionId>:` in front [1247 core/service/EdsDmsServiceImpl.java:218-220, 259-264]. The answer's
  `datasetRegistryId` is copied unchanged [1247 core-plus/di/CorePlusDatasetServiceImpl.java:80-86;
  azure/di/AzureDatasetServiceImpl.java:113-119; gc/di/GcpDatasetServiceImpl.java:74-80], so it names the source
  record, not the local proxy record.
- **Ids EDS mints.** The fixture's proxy id keeps the source dataset's id suffix, (legacy tests)
  [407 tests/Constants.py:1726, 1853]. Naturalization writes the naturalized dataset ids into the WPC with a trailing
  colon [407 naturalization_dag:136-156].
- **Versions EDS writes.** EDS writes a new CSDJ version after a run, (legacy tests)
  [407 tests/test_update_datajob_lastexectutetimestamp.py:32-46]. *Inference:* only when records were fetched: the
  ingest tests patch the update in every case except the one that fetches nothing
  [407 tests/test_fetch_and_ingest.py:14, 30, 46-59, 61]. Storage returns the new version in `recordIdVersions` and
  lists all versions [ST: PUT /records, createOrUpdateRecords; ST: GET /records/versions/{id}, getRecordVersions].
  Naturalization writes new WPC versions [407 naturalization_dag:164-167].
- **Kinds disagree** across the sources (section 1.2): the target partition's schema is the authority.

## 7. Reads, verification and deletes

### 7.1 What EDS does after registration

`eds_ingest` [R 23-28], with detail from the legacy tests:

1. Fetch records of `FetchKind` from the source [R 24].
2. Clean them: remove null values and replace reference values with the CSRE's `ReferenceValueMappings`, (legacy
   tests) [407 tests/test_clean_search_records.py:1-24] (section 5.1).
3. Set ACL and legal tags [R 25]. The fixture's output records carry the criteria's ACL and legal tags, (legacy
   tests) [407 tests/Constants.py:20, 1853], and the rewrite reads each record's `namespace`
   [407 tests/test_modify_ingest_records.py:13-21].
4. For WPC kinds, create `dataset--ConnectedSource.Generic` proxy datasets, (legacy tests)
   [407 tests/test_wpc_dataset_check_client.py:12-24; tests/Constants.py:1721-1788, 1853] (section 5.4).
5. Ingest by triggering `Osdu_ingest`, (legacy tests) [407 tests/test_ingest_client.py:27-46; tests/Constants.py:29-30].
   A trigger that does not return 200 raises `Failed to created records from workflow. Received status:`
   [407 tests/test_ingest_client.py:48-65]. The run state is read through Airflow's `DagRun`
   [407 tests/test_check_task_status.py:23-44; tests/Constants.py:31-32, 37], and the created ids through the XCom
   `record_ids` of `process_single_manifest_file_task` [407 tests/test_get_manifest_records.py:12-36;
   tests/Constants.py:31-36]. The constants also name `skipped_ids` of `provide_manifest_integrity_task`
   [407 tests/Constants.py:34-35], which no test uses.
6. Update the CSDJ (section 6).
7. Push the XComs and send the email [R 27-28].

XComs pushed by the fetch task [407 ingest_dag:42-92, 136-143]:

| Key | Content |
| --- | --- |
| `manifest_dag_run_id` | run id of the manifest ingestion |
| `total_fetched_records` | count of fetched records |
| `ingested_record_count`, `ingested_record_ids` | count and ids of ingested records |
| `failed_record_count`, `failed_record_ids` | count and ids of failed records |
| `referential_integrity_error` (filled from the response key `reference_error`), `schema_validation_error`, `acl_legal_tag_error` | error lists |
| `eds_naturalization_dag_run_id` | run id of the triggered naturalization |
| `run_error` | set when fetch-and-ingest raises, or returns something other than a dict (the task then fails) |
| `csdj`, `csre`, `version.milestone`, `eds_ingest_non_data_failure` | not pushed in the DAG file; read by the email task |

- The `IngestionLog` the email carries has `DagRunId`, `ManifestDagRunId`, `TotalFetchedRecords`,
  `IngestedRecordCount`, `IngestedRecordIds`, `FailedRecordCount`, `FailedRecordIds`, `ReferentialIntegrityError`,
  `SchemaValidationError`, `AclLegalTagError`, `NonDataFailure`, `EdsNaturalizationDagRunId`,
  `ConnectedSourceRegistryEntryId`, `ConnectedSourceDataJobId` and `VersionMilestone` [407 ingest_dag:146-162]. No
  email is sent when the fetch task fails, because its trigger rule is `all_success` [407 ingest_dag:214].

`eds_naturalization` [407 naturalization_dag:66-253]:

- each batch pushes `results_<i>`, read back as `{wpc_id, wpc_record, datasets: [{source_dataset_id,
  naturalized_dataset_id, file_size}]}` [407 naturalization_dag:92-98, 125-145, 230];
- the WPC update is sent in bulk through `UpdateWPCRecord().update_wpc_records` [407 naturalization_dag:164-167]:
  `data.Datasets` becomes the naturalized ids; if some datasets failed, their source ids are kept alongside; a WPC with
  no naturalized dataset, or without a record or datasets, is not updated [407 naturalization_dag:130-161];
- the XComs `updated_record_ids`, `skipped_record_ids`, `partially_updated_record_ids`, `not_updated_record_ids`
  [407 naturalization_dag:177-182];
- the email carries `NaturalizationLog` rows: WPC id, dataset ids and count, naturalized ids and count, failed ids and
  count, total size in KB [407 naturalization_dag:233-243].

`eds_scheduler` [R 77-79]: `eds_ingest` runs for each active CSDJ; clean-up of scheduled jobs whose CSDJ no longer
exists or is inactive; an activity report email covering the CSRE records. The report, (legacy tests), uses the CSRE's
`SmtpSchemes`, has the columns `Dag Run ID`, `Start Time`, `End Time`, `EDS Ingest Status`, `URL`, and is skipped with
`Mail not triggered, Trigger time not reached yet` when the trigger frequency is not reached
[407 tests/Constants.py:1456-1462; tests/test_email_trigger.py:59-71].

### 7.2 The retrieval read path, in order

`getRetrievalInstructions` [1247 core/service/EdsDmsServiceImpl.java:80-90]:

1. **Load the proxy records** from Storage, 100 ids per call, with the caller's headers
   [1247 core/service/handlers/StorageHandlerImpl.java:43-72]. Each batch's result replaces the previous one
   (`records = multiRecordInfo.getRecords();`, line 63), so a request with more than 100 ids keeps only the last
   batch. Only `getRecords()` is read, so ids Storage does not return are dropped. A `StorageException` gives 500
   `Error retrieving records` (lines 64-67).
2. **Parse each record** [1247 core/model/externaldataset/ExternalDataset.java:43-76]. A record whose
   `DatasetProperties`, CSRE id, CSDJ id or source record id is missing cannot be parsed (the version stripping of a
   missing id throws); it is logged and kept without shared properties (lines 70-75). The next step dereferences those
   properties [1247 core/service/EdsDmsServiceImpl.java:124-127], so one such record fails the whole request with 500
   `Server error.` from the generic mapper.
3. **Load each distinct CSRE** from Storage the same way, and take `data.DatasetURL` and `data.SecuritySchemes[0].Name`
   [1247 core/service/EdsDmsServiceImpl.java:121-142, 173-183]:
   - a missing `DatasetURL` or `SecuritySchemes` gives 500 `Dataset URL or SecuritySchemes not configured properly`
     (lines 179-181);
   - an empty `DatasetURL`, or a CSRE that Storage does not return, leaves the dataset invalid, and invalid datasets
     are dropped without an error [1247 core/model/externaldataset/Workflow.java:36-45;
     core/service/EdsDmsServiceImpl.java:187-189];
   - *Inference:* an empty `SecuritySchemes` list fails at `get(0)` outside that handler, with 500 `Server error.`; a
     missing `Name` on the first entry fails in the next step.
4. **Build every scheme** on each loaded CSRE, reading its secrets as it goes
   [1247 core/service/EdsDmsServiceImpl.java:144-171; core/model/externaldataset/SecurityScheme.java:128-203], and give
   each dataset the scheme named like `SecuritySchemes[0]` (`findFirst`, line 155). A missing required key throws
   `Required key '<key>' missing from security scheme` [1247 core/model/externaldataset/SecurityScheme.java:209-212],
   which reaches the caller as 500 `Server error.` with the exception text as the message
   [1247 core/util/GlobalOtherExceptionMapper.java:33-38]. *Inference:* an unknown flow name also fails here.
5. **Get the access tokens** in parallel, one per scheme object, on a pool of `edsdms.threads` threads (default 50)
   [1247 core/service/EdsDmsServiceImpl.java:66-78, 266-314]. The datasets of one CSRE share one scheme object
   (lines 144-160). *Inference:* nothing is cached across requests; schemes, secrets and tokens are rebuilt for each
   request. One failed token fails the whole request: an `AppException` from the provider passes through with its
   status, anything else becomes 500 `Security Scheme failed to get token` (lines 297-311).
6. **Call the external source once per CSRE** [1247 core/service/EdsDmsServiceImpl.java:202-257]:
   - the group's dataset URL, token and `SourceDataPartitionId` come from its first dataset
     [1247 core/service/EdsDmsServiceImpl.java:211; core/service/multithread/DatasetRetrievalProcessor.java:44-47];
   - URL `{DatasetURL}/retrievalInstructions`, joined by os-core-common's `UrlNormalizationUtil`
     [1247 core/service/dataset/DatasetServiceImpl.java:57, 67-69; azure/di/AzureDatasetServiceImpl.java:41, 69, 85-87;
     gc/di/GcpDatasetServiceImpl.java:38, 56]; core-plus takes the path from
     `eds.dataset.retrieval-instructions-path=/retrievalInstructions`
     [1247 eds-dms-core-plus/src/main/resources/application.properties:43; core-plus/di/CorePlusDatasetFactoryImpl.java:38-48;
     core-plus/di/CorePlusDatasetServiceImpl.java:58];
   - headers `data-partition-id: <SourceDataPartitionId>`, `Authorization: Bearer <token>` (the external token) and the
     forwarded `x-provider-*` headers [1247 core/service/handlers/DatasetHandlerImpl.java:48-54];
   - body `{"datasetRegistryIds": [<source record ids>]}`, 20 ids per call
     [1247 core/service/handlers/DatasetHandlerImpl.java:42, 57-66], which matches the 1 to 20 items `DS` allows
     [DS: POST /retrievalInstructions, retrievalInstructions_1];
   - a non-success answer becomes an error with the external status code, reason `Error from Dataset Service` and the
     external body in the message; a success body that does not parse gives 500 `Error Parsing Dataset Service
     Response` [1247 core/service/dataset/DatasetServiceImpl.java:71-89; azure/di/AzureDatasetServiceImpl.java:89-107];
   - if any group fails, the whole request fails [1247 core/service/EdsDmsServiceImpl.java:244-252].
7. **Merge** the `datasets` arrays and return them (section 5.6).
8. **Unknown ids are only logged** [1247 core/service/EdsDmsServiceImpl.java:86-88, 102-114]. The integration test
   sends one valid and one unknown id and expects exactly one dataset back [1247 it-core/EdsDms.java:159-165, 474-485].

The CSDJ id is parsed and must be present (step 2), but nothing in the request path loads the CSDJ or groups by it:
datasets are grouped by CSRE id [1247 core/service/EdsDmsServiceImpl.java:203-205], and the CSDJ id appears otherwise
only in `ExternalDatasetShared.equals` and `hashCode` [1247 core/model/externaldataset/ExternalDatasetShared.java:41-61],
which the request path never calls. The integration tests' CSDJ even names another CSRE
(`...ConnectedSourceRegistryEntry:some-guid`) [1247 it-core/EdsDms.java:310].

The external source must therefore provide a `POST /retrievalInstructions` shaped like the Dataset service's. The
integration tests use the platform's own Dataset service as that source [1247 it-core/EdsDms.java:254;
provider/eds-dms-azure/README.md:98].

### 7.3 What a route can verify

- **Route A and C records:** read back with `GET /records/{id}` and compare [ST: GET /records/{id},
  getLatestRecordVersion]; the CSRE and CSDJ checks of section 2.1 are local and need no call.
- **Route B runs:** poll the run [W: GET /v1/workflow/{workflow_name}/workflowRun/{runId}, getWorkflowRunById]. The
  created records are not observable through these contracts (section 2.2).
- **Optional retrieval check:** `POST /api/eds/v1/retrievalInstructions` with the ids of registered proxy records.
  It needs `service.edsdms.user` and, by inference, read access to the CSRE's secrets (section 3.1). A 200 does not
  prove every id was served: ids Storage does not return, datasets left invalid and unknown ids are dropped silently
  (section 7.2), so compare the returned `datasetRegistryId` values with the source record ids sent.

### 7.4 Deletes, stopping a job and clean-up

- Stop a job with `ActiveIndicator: false` (section 2.1).
- Remove a record with the reversible `POST /records/{id}:delete`, 204, allowed to `service.storage.creator` and
  `service.storage.admin` owners of the record [ST: POST /records/{id}:delete, deleteRecord]. Never the purge
  `DELETE /records/{id}` [ST: DELETE /records/{id}, purgeRecord] that the Postman collection and the eds-dms tests use
  (section 4.1).
- *Inference:* removing a CSRE that proxy records still reference makes those datasets drop silently from retrieval
  (section 7.2, step 3).
- Secrets created for a live test (outside the engine's role) are removed with `DELETE {SECRETS_URL}/secrets/{name}`
  in the upstream tests [1247 it-bm/util/AnthosTestUtils.java:172-179]; no Secret contract is pinned.

## 8. Limits and errors

| Limit | Value | Source |
| --- | --- | --- |
| Storage ids per eds-dms read | 100; only the last batch is kept | [1247 core/service/handlers/StorageHandlerImpl.java:49-50, 57-63]; [ST: POST /query/records, getRecords] `maxItems: 100` |
| Ids per external retrieval call | 20 | [1247 core/service/handlers/DatasetHandlerImpl.java:42]; [DS: POST /retrievalInstructions, retrievalInstructions_1] `maxItems: 20` |
| Parallel token and retrieval calls | `edsdms.threads`, default 50 | [1247 core/service/EdsDmsServiceImpl.java:66-78] |
| core-plus token client | connect 10000 ms, read 30000 ms, pool wait 10000 ms, 20 connections per host, 40 in total | [1247 eds-dms-core-plus/src/main/resources/application.properties:45-50] |
| Naturalization batches | Airflow Variable `eds__config__naturalization_batches`, default 4 | [407 naturalization_dag:28-30] |

eds-dms error mapping. `AppError` is `{"code": <int>, "reason": "<string>", "message": "<string>"}` [C 195-211]:

| Cause | Status and reason | Source |
| --- | --- | --- |
| `AppException` | its own code | [1247 core/util/GlobalExceptionMapper.java:46-49, 91-104] |
| validation error | 400 `Validation error.` | [1247 core/util/GlobalExceptionMapper.java:51-55] |
| `NotFoundException` | 404 `Resource not found.` | [1247 core/util/GlobalExceptionMapper.java:57-61] |
| unrecognised property, JSON processing | 400 `Unrecognized property.`, 400 `Failed to process JSON.` | [1247 core/util/GlobalExceptionMapper.java:63-73] |
| access denied | 403 `Access denied` | [1247 core/util/GlobalExceptionMapper.java:75-79] |
| method not supported | 405 `Method not found.` | [1247 core/util/GlobalExceptionMapper.java:81-89] |
| anything else | 500 `Server error.`, message the exception's `toString()` | [1247 core/util/GlobalOtherExceptionMapper.java:33-38] |
| token endpoint answers non-2xx; no token in the answer; I/O failure | the endpoint's status with `OAuth Server Error`; that status with `OAuth Server Missing Access Token`; 500 `OAuth Server Request Failed` | [1247 core-plus/oauth/CorePlusOauthProviderImpl.java:85-112; azure/oauth/AzureOAuthProviderImpl.java:62-91; gc/oauth/GcpOauthProviderImpl.java:64-92] |
| external source answers non-success | its status with `Error from Dataset Service` | section 7.2, step 6 |
| EDS disabled in the partition | 400 with the JSON string `"EDS service is disabled."`, not an `AppError` | section 3.2 |
| missing request parameter | 200 with the text `Missing parameter: ...`; *Inference:* the endpoint declares no parameters, so this rarely applies | [1247 core/api/EdsDmsApi.java:79-83] |

- **Errors carry upstream text.** The token endpoint's and the external source's response bodies are placed in the
  error message returned to the caller (above). A route that stores or shows such an error must redact it first,
  under the project's secrets rule.
- **Logging of the external call.** core-plus and GC log the outgoing headers, including the external
  `Authorization` header, and the response object [1247 core-plus/di/CorePlusDatasetServiceImpl.java:59-69;
  gc/di/GcpDatasetServiceImpl.java:57-63]. Azure removes `Authorization` from the logged headers and logs a copy of
  the response without its body [1247 azure/di/AzureDatasetServiceImpl.java:70-79]. GC logs the token URL
  [1247 gc/oauth/GcpOauthProviderImpl.java:48].
- **Whole-request failures.** One malformed proxy record, one bad scheme on a CSRE, one failed token or one failed
  external group fails every dataset in the request (section 7.2).
- **Silent drops.** More than 100 ids, records Storage does not return, an empty `DatasetURL`, an unreadable CSRE and
  unknown ids all shrink the answer without an error (section 7.2).
- **Workflow side.** A failed fetch sends no email [407 ingest_dag:214]; a scheduled scheduler run on the inactive
  Airflow instance is skipped by the gate [407 scheduler_dag:42-65]; the legacy ingest raises on a non-200
  `Osdu_ingest` trigger (section 7.1).

## 9. What differs between the contracts and the code

1. **Retrieval response.** `C` gives the 200 body as `type: string`, described "All kinds retrieved successfully"
   [C 52-57]; the code returns the JSON object of section 5.6. The controller annotation declares
   `ResponseEntity.class` as the schema [1247 core/api/EdsDmsApi.java:58]; *Inference:* that is where the string came
   from. A fake of eds-dms built from `C` must return the code's shape.
2. **Role.** `C` says `service.eds.user` [C 29-30]; the code checks `service.edsdms.user` (section 3.1).
3. **Security.** `C` applies bearer authentication to all four operations [C 14-15]; the code checks only the
   retrieval call (section 3.1).
4. **Headers.** `C` requires `Content-Type` and `data-partition-id` on all four operations, including the GETs that
   have no body [C 110-122, 138-150, 166-178]; the GC filter lets requests without `data-partition-id` through
   (section 3.2).
5. **Health texts.** `C` describes the readiness message as `External Data Sources Data Management Service is ready`
   and its 200 as "External Data Sources Data Management Service" [C 135-136, 153], and the liveness message as
   `External Data Sources Data Management Service is alive` [C 163-164, 181]; the code returns
   `EDS DMS Service is ready` and `EDS DMS Service is alive` [1247 core/api/HealthCheckApi.java:42, 53].
6. **Status codes.** `C` lists 400, 401, 403, 404, 500, 502 and 503 as `AppError` [C 51-99]; the code can also return
   any status the token endpoint or the external source returned, 405, a 400 whose body is a JSON string, and a 200
   whose body is text (section 8).
7. **Request limits.** `C`'s `RetrievalInstructionsRequest` has no `required` list and no size limits [C 188-194]; the
   code keeps only the last 100 ids (section 7.2). `DS`, whose shape the external call follows, requires
   `datasetRegistryIds` with 1 to 20 items [DS: POST /retrievalInstructions, retrievalInstructions_1].
8. **Base path.** The Azure README's `/api/eds/dms/v1/` differs from `C`'s `/api/eds/v1/` (section 3.1).
9. **README and code.** The eds-dms README says eds-dms looks up the data jobs [1247 README.md:28]; the code never
   loads them (section 7.2).
10. **Examples and code.** The docs CSRE example lacks `DatasetURL`, and its `PasswordCredentials` scheme lacks
    `ScopesKeyName` [1247 docs/examples/connected_source_registry_entry_example.json:2-19;
    core/model/externaldataset/SecurityScheme.java:78-85]; the docs proxy example uses a layout eds-dms does not read
    (section 5.4). Both would fail a retrieval request.
11. **Upstream tools and code.** The console utility writes `Type` and `Flow` (rejected by eds-dms), names its scheme
    `name` while its CSDJ's `SecuritySchemeName` is `OAuth2_ClientCredentials`, and writes no `DatasetURL`
    [407 console/service_connected_source_utility.py:74-93, 189]. Its update path reads `FlowTypeID` from the record
    it found [407 console/connected_source_utility.py:66].
12. **Workflow contract and EDS payloads.** `W` declares every `executionContext` value, and every
    `registrationInstructions` value, as an `object` [W: POST /v1/workflow/{workflow_name}/workflowRun,
    triggerWorkflow; W: POST /v1/workflow, create]. The inferred `eds_ingest` context has a string value, the
    `eds_naturalization` context has an array (`items`), and the Postman registration sends a string `dagName`, a
    `null` `dagContent` and a string `etc` (sections 4.3 and 5.5). *Inference:* this repository's contract check validates
    values against `additionalProperties` (`osdu/tests/SqlFlow.Delivery.Tests/Contracts/SchemaCheck.cs`), so it would
    report them. The Azure registration file is an array of one object, while `W` takes one object.
13. **Workflow names.** The Azure packaging registers `Eds_ingest`; the scheduler triggers `eds_ingest` (section 4.3).
14. **README and DAGs.** `R` omits the gate and `start` tasks and describes four fixed batches [R 51, 56]; the code
    reads the batch count from a Variable, default 4 (section 4.3).
15. **Test kinds and fields.** The eds-dms integration tests use `{partition}:wks:ConnectedSourceRegistryEntry:1.0.0`,
    `{partition}:wks:ConnectedDataSourceJob:1.0.0` and the CSDJ field `Active` (sections 1.2 and 5.3).

## 10. What is still open

1. The keys `eds_ingest` accepts in `execution_context`, and how it looks up the CSDJ and CSRE: in osdu-airflow-lib
   (project 668) under `osdu_airflow/eds/`, which was not read.
2. The workflow names registered in the target environment: `eds_ingest`, `Eds_ingest`, or a name the GC and
   baremetal versioning produces.
3. The CSRE and CSDJ schema versions in the target partition (CSDJ `1.0.0` or `2.0.0`, CSRE `1.0.0` or `2.0.1`), and
   whether `DatasetURL`, `SmtpSchemes`, `ReferenceValueMappings`, `LimitRecords`, `CreateTimeMax` and `FailedRecords`
   are schema properties.
4. Which CSDJ fields EDS writes after a run, and when.
5. Which dataset kinds the Dataset service routes to eds-dms (`dataset--External`, `dataset--ConnectedSource.Generic`,
   or others): that routing is configured outside these projects, and route C depends on it.
6. The Secret service contract: none is pinned. eds-dms's v1 and v2 models (section 5.2) are the only description.
7. os-core-common `7.1.3`: the validation on `RetrievalInstructionsRequest`, and the routes its Storage and
   Entitlements clients call (section 4.4).
8. The role name to grant: `service.edsdms.user` (code) or `service.eds.user` (contract text).
9. How the created ids of an EDS run can be observed without Airflow access: the Workflow contract offers only the
   run status and an untyped `latestInfo` (section 2.2).
10. How the scheduler evaluates `ScheduleUTC`, given the five-field and six-field examples (section 5.3).
11. Whether `eds_ingest` requires the CSDJ's `SecuritySchemeName` to match a CSRE scheme name.

## Appendix A. Record skeletons (placeholders only)

**CSRE that eds-dms can use, client-credentials flow.** Fields from
[1247 testing/eds-dms-test-baremetal/src/test/resources/ConnectedSourceRegistryEntry.json:3-55] and
[407 postman line 233]; kind from [407 console/constants.py:4].

```json
{
  "id": "<partition>:master-data--ConnectedSourceRegistryEntry:<id>",
  "kind": "<authority>:wks:master-data--ConnectedSourceRegistryEntry:1.0.0",
  "acl": { "owners": ["<owners group>"], "viewers": ["<viewers group>"] },
  "legal": { "legaltags": ["<legal tag>"], "otherRelevantDataCountries": ["<country>"] },
  "data": {
    "Name": "<source name>",
    "Description": "<description>",
    "FullOSDUImplementationIndicator": false,
    "DatasetURL": "<root URL of the external retrievalInstructions endpoint>",
    "SecuritySchemes": [
      {
        "Name": "<scheme name>",
        "TypeID": "<partition>:reference-data--SecuritySchemeType:OAuth2:",
        "FlowTypeID": "<partition>:reference-data--OAuth2FlowType:ClientCredentials:",
        "TokenUrl": "<token endpoint>",
        "ClientIDKeyName": "<secret name>",
        "ClientSecretKeyName": "<secret name>",
        "ScopesKeyName": "<secret name>"
      }
    ]
  }
}
```

**CSDJ that `eds_ingest` can use.** Fields from [407 postman line 398] and
[407 console/service_connected_source_utility.py:157-193]. Set `kind` to the version the target partition defines.
`LimitRecords` is an example value; `LastSuccessfulRunDateUTC` is EDS run state after the first run (section 2.1).

```json
{
  "id": "<partition>:master-data--ConnectedSourceDataJob:<id>",
  "kind": "<authority>:wks:master-data--ConnectedSourceDataJob:<version>",
  "acl": { "owners": ["<owners group>"], "viewers": ["<viewers group>"] },
  "legal": { "legaltags": ["<legal tag>"], "otherRelevantDataCountries": ["<country>"] },
  "data": {
    "Name": "<job name>",
    "ConnectedSourceRegistryEntryID": "<CSRE id>:",
    "ActiveIndicator": true,
    "FetchKind": "<source kind to fetch>",
    "Filter": "<search query at the source>",
    "LimitRecords": 100,
    "ConnectedSourceDataPartitionID": "<source partition>",
    "OnIngestionDataPartitionID": "<destination partition>",
    "ScheduleUTC": "<cron expression>",
    "LastSuccessfulRunDateUTC": "<timestamp>",
    "OnIngestionLegalTags": { "legaltags": ["<legal tag>"], "otherRelevantDataCountries": ["<country>"] },
    "OnIngestionAcl": { "owners": ["<owners group>"], "viewers": ["<viewers group>"] },
    "Workflows": [
      {
        "Tag": "FETCH",
        "Handler": "eds_ingest",
        "SecuritySchemeName": "<scheme name on the CSRE>",
        "Url": "<source search endpoint>"
      }
    ]
  }
}
```

**Proxy dataset that eds-dms can serve (route C).** Fields from [1247 README.md:38-71] and
[1247 it-core/EdsDms.java:381-417]; the four `DatasetProperties` are the ones eds-dms reads (section 5.4).

```json
{
  "id": "<partition>:dataset--External:<id>",
  "kind": "<authority>:wks:dataset--External:1.0.0",
  "acl": { "owners": ["<owners group>"], "viewers": ["<viewers group>"] },
  "legal": { "legaltags": ["<legal tag>"], "otherRelevantDataCountries": ["<country>"] },
  "data": {
    "Name": "<name>",
    "DatasetProperties": {
      "ConnectedSourceRegistryEntryId": "<CSRE id>",
      "ConnectedSourceDataJobId": "<CSDJ id>",
      "SourceDataPartitionId": "<source partition>",
      "SourceRecordId": "<dataset record id in the source partition>"
    }
  }
}
```
