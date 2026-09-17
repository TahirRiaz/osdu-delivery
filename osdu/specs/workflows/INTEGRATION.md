# Ingestion workflows: integration brief

The OSDU ingestion workflows are Apache Airflow DAGs started through the Workflow service: manifest ingestion
(`Osdu_ingest`, `Osdu_ingest_by_reference`), the CSV parser, the Energistics translators and the SEG-Y conversions to
OpenVDS, ZGY and MDIO. The Workflow contract types a run request loosely (an optional run id and a free-form
`executionContext`) and returns only run status; each DAG decides in code which context keys it reads, what must exist
before it runs, what it creates and when it fails. OSDU Delivery's `manifest` route type already triggers
`Osdu_ingest`. This brief serves the generic `workflow` route type of stage 6 of `docs/osdu-coverage-plan.md`: files
uploaded and registered, a named workflow triggered with a payload built from the interface's declaration, the run
polled, the created records found. For each workflow it states the trigger payload, the prerequisites, the outputs and
how to find them, the failure semantics and the parameters the route type has to carry, each with the contract or
source line it rests on.

## Sources

| Key | Source | Kind |
| --- | --- | --- |
| `WF` | `osdu/specs/core/workflow/openapi.yaml`: Workflow Service API 2.0.5, OpenAPI 3.1.0; the repository copy of the core specification set's workflow contract, copied on 2026-09-16 (`osdu/specs/sources.json` records no commit for the core set) | contract |
| `DS` | `osdu/specs/core/dataset/openapi.yaml`: Dataset Service 1.0.0 | contract |
| `FS` | `osdu/specs/core/file/openapi.yaml`: File Service 2.0.0 | contract |
| `ST` | `osdu/specs/core/storage/openapi.yaml`: Storage Service 2.0.0 | contract |
| `SE` | `osdu/specs/core/search/openapi.yaml`: Search Service 2.0 | contract |
| `D147` | `osdu/specs/workflows/ingestion-dags.md`: README of project 147 at `5d3822506bb4d4a5105f3c440e667b76cd122284` | pinned README |
| `D668` | `osdu/specs/workflows/osdu-airflow-lib.md`: README of project 668 at `ec1394a8016326d25672d51044099a2feb4a7d28` | pinned README |
| `D202` | `osdu/specs/workflows/csv-parser.md`: README of project 202 at `6c800d2c3cc7d7af6b8ffa4b798f196c178a135a` | pinned README |
| `D1414` | `osdu/specs/workflows/energistics-parser-dag.md`: README of project 1414 at `35e05496f559c360222f1ac8e5f24e63da32eebe` | pinned README |
| `D469` | `osdu/specs/workflows/segy-to-vds-conversion.md`: README of project 469 at `b6277de3d7c295a8bcb2a6bfa02dcbae99863d43` | pinned README |
| `D460` | `osdu/specs/workflows/segy-to-zgy-conversion.md`: README of project 460 at `90fa221d620911e76ee375c6067c1c77ca759d6b` | pinned README |
| `D1551` | `osdu/specs/workflows/segy-to-mdio-conversion-dag.md`: README of project 1551 at `a0b2d1ca835a417355ab2c235a764c568d1c4919` | pinned README |
| `147` | project 147, `osdu/platform/data-flow/ingestion/ingestion-dags`, branch `master`, commit `5d3822506bb4d4a5105f3c440e667b76cd122284` | source |
| `668` | project 668, `osdu/platform/data-flow/ingestion/osdu-airflow-lib`, branch `master`, commit `ec1394a8016326d25672d51044099a2feb4a7d28` | source |
| `202` | project 202, `osdu/platform/data-flow/ingestion/csv-parser/csv-parser`, branch `master`, commit `6c800d2c3cc7d7af6b8ffa4b798f196c178a135a` | source |
| `1414` | project 1414, `osdu/platform/data-flow/ingestion/energistics/energistics-parser-dag`, branch `main`, commit `35e05496f559c360222f1ac8e5f24e63da32eebe` | source |
| `1363` | project 1363, `osdu/platform/data-flow/ingestion/energistics/energistics-parser-lib`, branch `main`, commit `cd3f172670a3235197705e404c63bb989a7d7a45` | source |
| `1311` | project 1311, `osdu/platform/data-flow/ingestion/energistics/witsml-parser-v2`, branch `main`, commit `8bbb883d386259031bb9b1ec80a712f48c6f6667` | source |
| `1310` | project 1310, `osdu/platform/data-flow/ingestion/energistics/resqml-parser`, branch `main`, commit `040d00adb586c2e8275de23e5bd67d718e423cf3` | source |
| `1308` | project 1308, `osdu/platform/data-flow/ingestion/energistics/prodml-parser`, branch `master`, commit `636a5908c963d929c86761cd32735506de2ae19d` | source |
| `1697` | project 1697, `osdu/platform/data-flow/ingestion/energistics/enyparser`, branch `main`, commit `24193a80f6ba7403cded7af8612796d4ca84e897` | source |
| `469` | project 469, `osdu/platform/data-flow/ingestion/segy-to-vds-conversion`, branch `master`, commit `b6277de3d7c295a8bcb2a6bfa02dcbae99863d43` | source |
| `460` | project 460, `osdu/platform/data-flow/ingestion/segy-to-zgy-conversion`, branch `master`, commit `90fa221d620911e76ee375c6067c1c77ca759d6b` | source |
| `1551` | project 1551, `osdu/platform/data-flow/ingestion/segy-to-mdio-conversion-dag`, branch `master`, commit `a0b2d1ca835a417355ab2c235a764c568d1c4919` | source |
| `MR` | this repository's `manifest` route: `osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduManifestProtocol.cs` (cited by line) and its options in `osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs` (cited as `MR options`), at `d146f49a86b363a9d04317bca4857095ba32bff7` | repository code |

Each project commit above is the head of its branch on 2026-09-16; for the seven projects with a pinned README it is
also the commit `osdu/specs/sources.json` records. Every upstream file cited below was read at that commit.

How statements are marked: a citation with a contract key (`WF`, `DS`, `FS`, `ST`, `SE`) is a contract statement. A
`D` key cites a project's pinned README. A project number cites that project's source at the commit above, for
behaviour only code shows. `MR` cites this repository. Text marked "(inference)" is a conclusion drawn from the cited
lines, not stated by them.

Path abbreviations used inside citations:

| Citation prefix | Path in the project |
| --- | --- |
| `147 r3` | `src/osdu_dags/osdu-ingest-r3.py` |
| `147 ref` | `src/osdu_dags/osdu-ingest-r3-by-reference.py` |
| `668 op/` | `osdu_airflow/operators/` |
| `668 eds/` | `osdu_airflow/eds/` |
| `668 guide` | `docs/docs/eds-configuration-guide.md` |
| `202 dag` | `airflowdags/csv_ingestion_all_steps.py` |
| `202 core/` | `csv-parser-core/src/main/java/org/opengroup/osdu/csvparser/` |
| `202 IBM "<request>"` | `devops/ibm/IBM_ODI_R3_v2.0.1_CSV.postman_collection.json`, request by name |
| `1414 conv` | `src/osdu_dags/energyml-converter-dag.py` |
| `1414 k8s` | `src/osdu_dags/energyml-converter-dag-vk8s.py` |
| `1414 deliv` | `src/osdu_dags/energyml-epc-h5-delivery.py` |
| `1414 EP <n>` | `postman/EnergisticsParser.postman_collection.json`, request by number |
| `1363 op/` | `energistics_parser/operators/` |
| `1363 app/` | `app/energyml_parser/` |
| `1697 dag` | `orchestrator/airflow/dags/enyparser_translation_dag.py` |
| `1697 main` | `orchestrator/app/enyparser_dag/main.py` |
| `1697 README` | `orchestrator/README.md` |
| `1697 ENY <n>` | `orchestrator/postman/enyparser.postman_collection.json`, request by number |
| `469 dag` | `src/dags/segy_to_vds_ssdms_conversion_dag.py` |
| `469 QS` | `docs/gcp/QUICKSTART.md` |
| `469 IBM "<request>"` | `tests/e2e/ibm/IBM_OVDS_Conversion_Collection.postman_collection.json`, request by name |
| `460 dag` | `airflow/workflow-svc-v2/segy_to_zgy_ingestion_dag.py` |
| `460 testing` | `doc/testing.md` |
| `1551 dag` | `airflow/dags/segy_to_mdio_conversion_dag.py` |
| `1551 app/` | `app/osdu_mdio_conversion/` |
| `1551 E2E <n>` | `e2e/51_CICD_SegyToMdio conversion using Seismicstore v1.0.postman_collection.json`, request by number |

What these sources do not cover:

- Libraries outside these projects: `osdu-ingestion` (project 823), `osdu-api` (148), `commons-parser` (1309) and
  `energyml-delivery` (1405) [147 src/osdu_dags/requirements.txt:3-10; 1414 src/osdu_dags/requirements.txt:16-38].
  Behaviour implemented there (`Context.populate`, the manifest processor, `UpdateStatus.update_workflow_status`,
  `OpenVDSMetadata`, `put_to_dataset_service`, `_find_key_values`) is not verified; where a cited file describes it,
  the statement is attributed to that file.
- The Workflow service implementation. How it turns a trigger into Airflow's `dag_run.conf`, and whether it reconciles
  run status with Airflow, is known only from what the DAGs read and what their projects say about it.
- Installed versions. The code was read on the default branches, while deployments install released packages: the
  DAG projects pin `osdu-airflow~=0.30.0` [147 src/osdu_dags/requirements.txt:6-7] and `osdu-airflow==0.28.0`
  [1414 src/osdu_dags/requirements.txt:19-20], and `osdu-airflow-lib` is at 0.31.0 on its default branch
  [668 VERSION:1]. An installed release can differ line by line.
- `osdu/specs/workflows/core-external-data-workflow.md` is pinned in the same folder and is not covered here.

## 1. Base path, versions, headers and auth

- Server `/api/workflow` [WF:25-26]. Every operation path starts with `/v1/` except the two health checks
  [WF:852, :874]. API version 2.0.5, OpenAPI 3.1.0 [WF:1, :24]. The full form is
  `https://<host>/api/workflow/v1/workflow/{workflow_name}/workflowRun`, as the upstream documentation writes it
  [1551 airflow/README.md:45; 1697 README:63-65] and as the `manifest` route defaults to [MR:30-32].
- Every operation declares a required `data-partition-id` header, `/v1/info` and the health checks included (for
  example [WF:460-465, :839-844]).
- Security: one scheme, `Authorization`, HTTP bearer [WF:1223-1229], applied globally [WF:27-28] and on each
  operation. Requests carry `Authorization: Bearer <token>`.
- The contract sets no size limit on a trigger: `WF` declares no `maxLength`, `maxItems` or `maxProperties` anywhere.

### 1.1 Operations

| Operation | operationId | Tag | Required role (from the description) | Request | 200 response | Lines |
| --- | --- | --- | --- | --- | --- | --- |
| `GET /v1/workflow` | `getAllWorkflowsForTenant` | `workflow-manager-api` | `service.workflow.viewer` | optional query `prefix` | `WorkflowMetadata` (one object, not an array) | [WF:210-284] |
| `POST /v1/workflow` | `create` | `workflow-manager-api` | `service.workflow.admin` | `CreateWorkflowRequest` | `WorkflowMetadata` | [WF:285-364] |
| `POST /v1/workflow/system` | `createSystemWorkflow` | `workflow-system-manager-api` | `service.workflow.admin` | `CreateWorkflowRequest` | `WorkflowMetadata` | [WF:530-606] |
| `GET /v1/workflow/{workflow_name}` | `getWorkflowByName` | `workflow-manager-api` | `service.workflow.viewer` | none | `WorkflowMetadata`; 404 listed | [WF:608-682] |
| `DELETE /v1/workflow/{workflow_name}` | `deleteWorkflowById` | `workflow-manager-api` | `service.workflow.admin` | none | 204 | [WF:683-753] |
| `DELETE /v1/workflow/system/{workflow_name}` | `deleteSystemWorkflowById` | `workflow-system-manager-api` | `service.workflow.admin` | none | 204 | [WF:897-967] |
| `POST /v1/workflow/{workflow_name}/workflowRun` | `triggerWorkflow` | `workflow-run-api` | `service.workflow.creator` | `TriggerWorkflowRequest` (required body) | `WorkflowRunResponse` | [WF:448-528] |
| `GET /v1/workflow/{workflow_name}/workflowRun` | `getAllRunInstances` | `workflow-run-api` | `service.workflow.viewer` | required object-typed query `params` | `WorkflowRun` (one object, not an array) | [WF:366-447] |
| `GET /v1/workflow/{workflow_name}/workflowRun/{runId}` | `getWorkflowRunById` | `workflow-run-api` | `service.workflow.viewer` | none | `WorkflowRunResponse` | [WF:42-122] |
| `PUT /v1/workflow/{workflow_name}/workflowRun/{runId}` | `updateWorkflowRun` | `workflow-run-api` | `service.workflow.viewer` | `UpdateWorkflowRunRequest` | `WorkflowRunResponse` | [WF:123-208] |
| `GET /v1/workflow/{workflow_name}/workflowRun/{runId}/latestInfo` | `getWorkflowRunDetails` | `run-details-api` | `service.workflow.viewer` and ownership of the run | none | `type: object` | [WF:755-829] |
| `GET /v1/info` | `info` | `info` | none stated | none | `VersionInfo` | [WF:831-851] |
| `GET /readiness_check`, `GET /liveness_check` | `readinessCheck`, `livenessCheck` | `health` | none stated | none | string | [WF:853-895] |

Every operation except `/v1/info` and the health checks also lists 400, 401, 403, 404, 500, 502 and 503 with an
`AppError {code, reason, message}` body [WF:1011-1027]. All of those except `latestInfo` list 409 as well, with one
shared description, "A Workflow with the given name already exists.", on reads and triggers alike (for example
[WF:97-102, :503-508]).

### 1.2 Schemas

- `TriggerWorkflowRequest` [WF:1091-1101]: `runId`, a string described as "Optional. Explicit setting up workflow run
  id."; `executionContext`, an object whose `additionalProperties` are `type: object`, described as "Map to configure
  workflow speciffic key value pairs". The schema has no `required` list.
- `WorkflowRunResponse` [WF:982-1010], returned by trigger, get and update: `workflowId`, `runId`, `startTimeStamp`
  and `endTimeStamp` (strings with `format: int64`, epoch), `status` with the enum `INPROGRESS`, `PARTIAL_SUCCESS`,
  `SUCCESS`, `FAILED`, `SUBMITTED`, and `submittedBy`.
- `WorkflowRun` [WF:1102-1137], returned by the run listing: the same fields plus `workflowName` and
  `workflowEngineExecutionDate`, with `status` enum `submitted`, `running`, `finished`, `failed`, `success`, `queued`.
- `UpdateWorkflowRunRequest` [WF:970-981]: `status` with the lower-case enum.
- `CreateWorkflowRequest` [WF:1028-1049]: `workflowName`, `description` and the required `registrationInstructions`,
  which "could contains" the name of an already registered Airflow DAG or the content of a DAG file, "By default this
  is Airflow DAG named workflowName". The contract's example registers `{externalAirflowSecret, dagName}`
  [WF:1213-1222].
- `WorkflowMetadata` [WF:1050-1090]: `workflowId`, `workflowName`, `description`, `createdBy`, `creationTimestamp`,
  `version`, `registrationInstructions`, and the write-only `isDeployedThroughWorkflowService` and `isSystemWorkflow`.

### 1.3 What a DAG receives

The DAGs read these keys of Airflow's `dag_run.conf`. Which keys the Workflow service writes is not in the contract.

| Key | Read by | Citation |
| --- | --- | --- |
| `execution_context` | every DAG and every operator | [147 r3:76; 668 op/process_manifest_r3.py:170] |
| `workflow_name`, `run_id` | both status operators | [668 op/update_status.py:159-161; 668 op/update_status_by_reference.py:126-127] |
| `authToken` | the pod DAGs, which hand it to their containers; the VDS DAG as the Seismic DMS token fallback; the status operators, which mask it before logging the conf | [202 dag:31; 460 dag:32-33; 1551 dag:157; 1697 dag:273; 469 dag:105; 668 op/update_status.py:140-142] |
| `run_id` | the CSV DAG, which does not pass it on | [202 dag:33] |
| `runId` | the ZGY and Energistics pod templates (assigned, not used further) | [460 dag:35; 1414 k8s:45] |
| `correlation_id` | enyparser, which logs it | [1697 dag:293; 1697 main:173-178] |

- The enyparser README states that the Workflow service puts the caller's bearer, with its `Bearer ` prefix, into
  `dag_run.conf["authToken"]` [1697 README:174-191].
- The enyparser Azure registration script states that the Workflow service triggers `registrationInstructions.dagName`,
  falling back to `workflowName`, so a `dagName` that does not match the DAG id leaves runs in `submitted`
  [1697 orchestrator/devops/azure/output_dag_folder.py:21-26]; the DAG repeats the requirement [1697 dag:159-162].
- The VDS DAG's TaskFlow tasks read the run configuration through their `params` argument [469 dag:102-105, :167-170];
  how Airflow fills `params` from the run configuration is not in these sources.

### 1.4 Workflow names per deployment

For most DAGs the workflow name is deployment configuration: the same file is rendered with a different DAG id per
provider.

| Workflow | Name | Where it is set |
| --- | --- | --- |
| Manifest ingestion | `Osdu_ingest` | DAG [147 r3:68]; Azure registers `{"workflowName":"Osdu_ingest","registrationInstructions":{"dagName":"Osdu_ingest"}}` [147 deployments/scripts/azure/output_dag_folder.py:7, :18-23]; GC renders this file [147 devops/gc/pipeline/override-stages.yml:5]; the IBM chart's copy has the same id [147 devops/ibm/ibm-manifest-dag/files/osdu-ingest-r3.py:48] |
| Manifest by reference | `Osdu_ingest_by_reference` | DAG [147 ref:60]; EDS constant [668 eds/eds_ingest/constants.py:42]; availability varies (section 3.3) |
| Test DAG | `manifest_ingestion` | [147 src/osdu_dags/manifest_ingestion.py:12-19] |
| CSV parser | `csv_ingestion` (GC, core-plus, baremetal), `csv-parser` (Azure), `csv-parser-pipeline` (IBM) | placeholder [202 dag:39]; [202 deployments/scripts/gc/render_dag_file.py:16; 202 deployments/scripts/core-plus/render_dag_file.py:19; 202 deployments/scripts/baremetal/render_dag_file.py:19; 202 deployments/scripts/azure/create_dag.py:15, :93; 202 devops/ibm/ibm_csv_ingestion_all_steps.py:35]; Azure also registers it through `POST .../api/workflow/v1/workflow/system` [202 deployments/scripts/azure/dag_bootstrap/register_dag.py:14, :92-110] |
| Energistics translation | `Energyml_Converter` | [1414 conv:41]; GC deploys the pod variant [1414 devops/gc/pipeline/override-gc-stages.yml:3] rendered under this name [1414 deployments/scripts/gc/render_dag_file.py:25]; Azure registers `Energyml_Converter` and `Energyml_Delivery` [1414 deployments/scripts/azure/output_dag_folder.py:8, :19-27] |
| Energistics delivery | `Energyml_Delivery` | [1414 deliv:39] |
| enyparser translation | `Enyparser_Translation` | [1697 dag:159-162; 1697 orchestrator/devops/azure/output_dag_folder.py:36-37, :54-59] |
| SEG-Y to OpenVDS | `Segy_to_vds_conversion_sdms` (baremetal, GC quickstart), `segy-to-vds-conversion` (Azure), `openvds_import` (IBM; also the older AWS DAG and the README's example) | placeholder [469 dag:86]; [469 deployments/scripts/baremetal/render_dag_file.py:18; 469 QS:210; 469 deployments/scripts/azure/output_dag_folder.py:17-20; 469 devops/azure/override-stages.yml:56; 469 devops/ibm/ibm-vds-dag/files/replace-values.sh:5; 469 openvds.py:51; D469:73-99] |
| SEG-Y to ZGY | `Segy_to_zgy_conversion` (baremetal), `segy-to-zgy-conversion` (Azure), `sgy-to-zgy` (IBM chart), `sgy-to-zgy-pipeline` (IBM standalone DAG) | placeholder [460 dag:44]; [460 deployments/scripts/baremetal/render_dag_file.py:21; 460 deployments/scripts/azure/output_dag_folder.py:17-20; 460 devops/azure/override-stages.yml:107; 460 devops/ibm/ibm-zgy-dag/files/replace-variable.sh:6; 460 devops/ibm/segy_to_zgy_ingestion_dag_ibm.py:41] |
| SEG-Y to MDIO | `segy_to_mdio_conversion` | the `@dag` function of that name, with no explicit id [1551 dag:112-118]; the README triggers that name [1551 airflow/README.md:45]; the e2e suite registers `dagName: segy_to_mdio_conversion` [1551 E2E 4.2] and names it in its variables [1551 e2e/README.md:48] |

## 2. The calls a writer makes

### 2.1 The sequence

1. Probe. `GET /v1/workflow/{workflow_name}` [WF: getWorkflowByName, 608-682] answers 200 with `WorkflowMetadata`
   and lists 404. EDS treats a 200 on `GET {core__service__workflow__url}/workflow/<name>` as "available"
   [668 eds/service/manifest_by_reference_workflow.py:26-55]; that Variable is documented with the example value
   `<http||https>://<service-host>/api/workflow/v1` [D668:40]. The enyparser collection reads a 404 on trigger as "this
   deployment has not registered the workflow" [1697 ENY 40.1].
2. Prerequisites (section 2.2): files uploaded and registered, or records already in Storage.
3. Trigger. `POST /v1/workflow/{workflow_name}/workflowRun` [WF: triggerWorkflow, 448-528] with a
   `TriggerWorkflowRequest` [WF:1091-1101]. The `manifest` route sends a `runId` of its own (a new GUID) with
   `executionContext {Payload, manifest}`, reports the run on every record before polling, and treats a 409 on a
   re-sent request as the run already accepted, which it then polls [MR:390-417]. The contract's 409 text does not
   describe that case (section 7).
4. Poll. `GET /v1/workflow/{workflow_name}/workflowRun/{runId}` [WF: getWorkflowRunById, 42-122] until a terminal
   status (section 2.3), within a per-workflow timeout (section 6.2). The `manifest` route polls every 10 seconds for
   at most 60 minutes by default, and a later try resumes the same run [MR:452-484; MR options:584-587].
5. Find what the run created (section 5.2), verify it, and record the side effects the DAG minted (section 5.3).
6. For the translate-only workflows (`Energyml_Converter`, `Enyparser_Translation`), a second stage triggers
   `Osdu_ingest_by_reference` with the first stage's manifest dataset id [1414 EP 07; 1697 dag:21-32, :50-53].

Not part of a delivery (inference): registering workflows (`POST /v1/workflow`, `POST /v1/workflow/system`), deleting
them, and `PUT .../workflowRun/{runId}`. The DAGs report their own status through `osdu-ingestion`'s `UpdateStatus`
[668 op/update_status.py:165-183]; the HTTP call it makes is not in these sources. The contract lets any holder of
`service.workflow.viewer` update a run [WF:127].

### 2.2 Prerequisite calls

#### 2.2.1 Dataset service (Energistics, enyparser, manifests by reference)

Server `/api/dataset/v1/` (the `servers` entry of `DS`).

1. `POST /storageInstructions?kindSubType=dataset--File.Generic` [DS: storageInstructions, 101-183]: `kindSubType`
   is a required query parameter and `expiryTime` an optional one (pattern `\d+([mhd]|[MHD])$`); role
   `service.dataset.editors`. The 200 body is `GetDatasetStorageInstructionsResponse {storageLocation, providerKey}`
   with `storageLocation` an untyped object [DS:939-947]. Callers read `signedUrl`, `fileSource`, `unsignedUrl` and
   `signedUploadFileName` from it [668 op/base_osdu_operator_by_reference.py:163-189; 1414 EP 01]; EDS fails when
   `signedUrl` is absent [668 eds/eds_ingest/data_ingestor/implementation/manifest_by_reference_strategy.py:101-123].
2. `PUT <signedUrl>` with the bytes and no OSDU authorization. EDS and the collections add `x-ms-blob-type: BlockBlob`
   [668 eds/eds_ingest/data_ingestor/implementation/manifest_by_reference_strategy.py:125-184; 1414 EP 02;
   1697 ENY 10.2]; the enyparser collection sends neither `Authorization` nor `data-partition-id` to a signed URL,
   because some object stores reject a request that also carries a bearer [1697 ENY 10.2].
3. `PUT /registerDataset` [DS: createOrUpdateDatasetRegistry, 29-99], roles `service.storage.creator` or
   `service.storage.admin`: body `CreateDatasetRegistryRequest {datasetRegistries}` with 1 to 20 records
   [DS:789-800]; 201 `GetCreateUpdateDatasetRegistryResponse {datasetRegistries}` [DS:915-921]. The registered id is
   `datasetRegistries[0].id` [668 eds/eds_ingest/data_ingestor/implementation/manifest_by_reference_strategy.py:233-236;
   668 op/base_osdu_operator_by_reference.py:214-216].
4. Reading a dataset back: `POST /retrievalInstructions` with `GetDatasetRegistryRequest {datasetRegistryIds}`, 1 to
   20 ids [DS: retrievalInstructions_1, 340-421; DS:948-959], or `GET /retrievalInstructions?id=`
   [DS: retrievalInstructions, 257-339]; role `service.dataset.viewers`. The 200 body is
   `RetrievalInstructionsResponse {datasets: [{datasetRegistryId, retrievalProperties, providerKey}]}` with
   `retrievalProperties` an untyped object [DS:960-977]. Callers read `datasets[0].retrievalProperties.signedUrl`
   [668 op/base_osdu_operator_by_reference.py:61-65; 202 core/dataset/DatasetService.java:43-62]. The enyparser
   collection uses the GET form and also accepts a `delivery` array [1697 ENY 30.1], which `DS` does not define.

Record shapes the upstream callers register:

| Caller | Kind | `data` | Other fields | Citation |
| --- | --- | --- | --- | --- |
| EDS manifest file | `<schema authority>:wks:dataset--File.Generic:1.0.0` | `DatasetProperties.FileSourceInfo {FileSource: <fileSource>, PreloadFilePath: <fileSource>}` | the job's `acl` and `legal` | [668 eds/eds_ingest/data_ingestor/implementation/manifest_by_reference_strategy.py:186-236; 668 eds/eds_ingest/constants.py:30] |
| by-reference side-effect files | `<kind_authority>:wks:dataset--File.Generic:1.0.0` | section 3.3 | a fixed `version`, empty ancestry | [668 op/base_osdu_operator_by_reference.py:149-216] |
| Energistics collection | `<authority>:<source>:dataset--File.Generic:1.0.0` | `DatasetProperties.FileSourceInfo {FileSource, PreloadFilePath: "", Name}` | `legal` with `status: compliant`, `meta: []`, `tags: {}`, a fixed `version`; expects 201 | [1414 EP 03] |
| enyparser collection | `osdu:wks:dataset--File.Generic:1.0.0` | `Name`, `DatasetProperties.FileSourceInfo {FileSource, Name}` | `legal` with `status: compliant`, `ancestry.parents: []`; expects 201 | [1697 ENY 10.3, 10.6] |

The file name matters to enyparser: the suffix selects the loader [1697 README:134-138]. The contract's `Record` lists
`id` as required [DS:831-901]; these callers send none, and the service mints one on each registration
[1697 docs/extending.md:247-251].

#### 2.2.2 File service (CSV parser)

Server `/api/file/` (the `servers` entry of `FS`).

1. `GET /v2/files/uploadURL` [FS: getLocationFile, 462-545]: 200 `LocationResponse {FileID, Location}` with
   `Location` an untyped object [FS:923-931]; IBM's collection starts here [202 IBM "Get Upload Signed URL"].
2. `PUT` the bytes to the signed URL [202 IBM "Upload File Using SignedURL"].
3. `POST /v2/files/metadata` [FS: postFilesMetadata, 90-167]: body `FileMetadata`, which requires `acl`, `data`,
   `kind` and `legal` [FS:676-712]; 201 `FileMetadataResponse {id}` [FS:791-798]. The descriptor the CSV parser needs
   goes here (section 3.5).
4. The parser itself then calls `GET /v2/files/{id}/metadata` [FS: getFileMetadataById, 233-305] and
   `GET /v2/files/{id}/downloadURL` [FS: downloadURL, 376-460], whose 200 body is `DownloadUrlResponse {SignedUrl}`
   [FS:916-922]; see [202 core/file/FileService.java:44-68, :91-117].

#### 2.2.3 Records in Storage (SEG-Y conversions)

The conversions start from records that already exist: written with Storage `PUT /records`
[ST: createOrUpdateRecords, 156-245], as IBM's OpenVDS collection does [469 IBM "Store File Collection With SD Path"],
or ingested with an `Osdu_ingest` run, as the MDIO e2e suite and the VDS quickstart do [1551 E2E 3.1, 3.2;
469 QS:190-199]. For OSDU Delivery these are records of an earlier delivery, so the route accepts "records already
delivered" as an input, not only files (inference).

#### 2.2.4 Seismic DMS content (SEG-Y conversions)

The SEG-Y file is uploaded to Seismic DMS outside these contracts, with `sdutil` in the upstream guides
[469 QS:159-188; 460 testing:31-55]. Those calls belong to the Seismic DDMS route (`osdu/specs/seismic-ddms`).

### 2.3 Run status

- The contract has two vocabularies. `WorkflowRunResponse.status`, returned by trigger and get, is `INPROGRESS`,
  `PARTIAL_SUCCESS`, `SUCCESS`, `FAILED` or `SUBMITTED` [WF:999-1007]; `WorkflowRun.status` and
  `UpdateWorkflowRunRequest.status` are `submitted`, `running`, `finished`, `failed`, `success` or `queued`
  [WF:975-981, :1126-1132].
- The upstream material reads lower-case values from trigger and get: trigger responses with `submitted`
  [D469:121-130; 460 testing:141-151; 1414 EP 04; 1551 E2E 4.3]; polls that retry on `submitted` and `running`
  [1414 EP 05], on `submitted`, `queued` and `running` [1551 E2E 3.2, 4.4], or on `running` and `submitted` while
  asserting the status is not `failed` [1697 ENY 20.2]; polls that expect `finished` at the end [1414 EP 05;
  1551 E2E 3.2, 4.4]. EDS models the lower-case set and notes that it was handcrafted from the Workflow service's
  `WorkflowStatusType` [668 eds/models/workflow_framework/workflow_response_status.py:15-27].
- A poller accepts both casings. The `manifest` route compares upper-cased values: terminal `SUCCESS`,
  `PARTIAL_SUCCESS`, `FINISHED`, `FAILED`; pending `SUBMITTED`, `INPROGRESS`, `IN_PROGRESS`, `RUNNING`, `QUEUED`; any
  other value is an error [MR:50-59, :465-475]. A lower-case `success` becomes `SUCCESS` that way.
- How the DAGs set it. `UpdateStatusOperator` derives the status from the run's task instances: none in state `failed`
  or `success` gives `running`, any `failed` gives `failed`, otherwise `finished` [668 op/update_status.py:58-87]. It
  sends that status through `UpdateStatus(...).update_workflow_status()` [668 op/update_status.py:174-183] and then
  raises `PipelineFailedError` when the status is `failed`, so Airflow agrees [668 op/update_status.py:197-198]. Every
  workflow DAG in section 3 except the test DAG has a first status task and a last one with the `all_done` trigger
  rule [147 r3:95-109; 147 ref:90-104; 202 dag:71-88; 1414 conv:51-59; 1414 k8s:88-90, :120-124; 1414 deliv:49-57;
  1697 dag:248, :323-326; 469 dag:209-211, :234-237; 460 dag:80-82, :95-98; 1551 dag:122-124, :189-191].
- The non-reference operator records a failure to send the status in XCom and swallows it, except when the status is
  `running` [668 op/update_status.py:182-187]. A run can therefore stay `running` in the Workflow service after
  Airflow finished it (inference), so the route needs its own timeout per workflow. The by-reference operator does not
  guard the call, so a failed update fails its task [668 op/update_status_by_reference.py:149].
- `finished` does not mean "everything was stored" for most workflows (section 6.3).

## 3. Payloads and data shapes

### 3.1 Rules for every execution context

- `Payload: {AppKey, data-partition-id}`. The operators build their context with `Context.populate(execution_context)`
  [668 op/process_manifest_r3.py:171]. The enyparser DAG states that `Context.populate` reads both keys in one `try`,
  so a `Payload` without `AppKey` raises `UnboundLocalError` in the first status task
  [1697 dag:64-69; 1697 README:97-101]. Always send both.
- Without `Payload`, the status operators take the top-level `data-partition-id`, then `dataPartitionId`, and a
  top-level `AppKey` that defaults to an empty string [668 op/update_status.py:144-157;
  668 op/update_status_by_reference.py:112-125]. A context with none of `Payload`, `data-partition-id` and
  `dataPartitionId` fails the first status task with a `KeyError` (inference from the same lines).
- `userId` is read at the top level of the context [668 op/update_status.py:138; 668 op/process_manifest_r3.py:178];
  the EDS request model places it inside `Payload` [668 eds/models/workflow_framework/manifest_by_reference.py:55-62].
- The contract types every context value as an object; the DAGs read strings and arrays as well (section 7).
- Credentials travel in some contexts (section 6.5). OSDU Delivery keeps them as `${keyvault:...}` or `${env:...}`
  references, resolves them only when the request is sent, and redacts the stored and displayed payload.

### 3.2 `Osdu_ingest`

**Name.** `Osdu_ingest` (section 1.4). The R2 DAG `Osdu_ingest_r2` is documented as deprecated
[D147:37-38, :170-172], and `src/osdu_dags` holds no R2 DAG file at this commit.

**What it ingests.** One R3 manifest, an object with `kind` (`<authority>:<source>:Manifest:<major>.<minor>.<patch>`),
`ReferenceData`, `MasterData` and `Data {WorkProduct, WorkProductComponents, Datasets}`
[668 eds/models/workflow_framework/manifest_request.py:66-141; 668 guide:115-123], or a list of such manifests
[147 r3:71-85].

**Task graph.** `update_status_running_task`, then `check_payload_type`, then either `validate_manifest_schema_task`,
`provide_manifest_integrity_task` and `process_single_manifest_file_task` (an object) or `batch_upload` fanning out to
`process_manifest_task_1` to `process_manifest_task_N` (an array), then `update_status_finished_task`
[147 r3:95-143]. N is the Airflow Variable `core__ingestion__batch_count` (default 3), read when the DAG file is parsed
[147 r3:47-52].

**Execution context.**

| Key | Shape | Required | Read at |
| --- | --- | --- | --- |
| `Payload.AppKey`, `Payload["data-partition-id"]` | strings | yes | section 3.1 |
| `manifest` | object (single) or array of objects (batch); anything else raises `NotOSDUSchemaFormatError` | yes | [147 r3:71-85; 668 op/base_osdu_operator.py:46-60] |
| `userId` | string | no | [668 op/process_manifest_r3.py:178] |
| `acl`, `legal` | objects | sent by EDS [668 guide:92-126] | not read by this DAG's operators; any use inside `osdu-ingestion` is not verified |

The EDS example's `legal` carries `legaltags` and `otherRelevantDataCountries` only [668 guide:103-110].

**Prerequisites.**

- The files that `Data.Datasets` names exist: the process step wires a `FileHandler`, a `FileSourceValidator` and a
  `SourceFileChecker` into the processor [668 op/process_manifest_r3.py:173-177, :203-208]; their checks live in
  `osdu-ingestion`. The `manifest` route registers each file through the File service before the manifest names it
  [MR:12-26].
- Every kind's schema resolves: `SchemaValidator` [668 op/validate_manifest_schema.py:61-72;
  668 op/process_manifest_r3.py:192-197, :209-214].
- References resolve through Search: `ManifestIntegrity` [668 op/ensure_manifest_integrity.py:59-71;
  668 op/process_manifest_r3.py:180-185, :199-201]. EDS documents referential-integrity failures in
  `provide_manifest_integrity_task` [668 guide:253-257]; the `manifest` route waits until Search lists the datasets it
  registered, because ingestion checks references against the index [MR:21-22].
- Airflow Variables for the service URLs: `core__service__storage__url`, `core__service__search__url`,
  `core__service__schema__url` and `core__service__file__url` for the process step
  [668 op/process_manifest_r3.py:80-83, :181, :187, :193], and `core__service__workflow__url` for the status tasks
  [668 op/update_status.py:165-168].

**What it creates.** The manifest's records, written through Storage by the processor's `RecordClient`
[668 op/process_manifest_r3.py:186-191, :203-208], in batches when configured (section 3.12).

**Finding the results.**

- XCom `record_ids` of `process_single_manifest_file_task` or of each `process_manifest_task_N`
  [668 op/process_manifest_r3.py:235; D147:213-221].
- XCom `saved_record_ids` of `update_status_finished_task`: a map from task id to that task's `record_ids`
  [668 op/update_status.py:107-124, :189-190].
- Both live in Airflow. EDS runs inside Airflow and reads `record_ids` of `process_single_manifest_file_task` directly
  [668 eds/eds_ingest/utilities/osdu_ingest_xcom_utility.py:48-64]; the EDS guide sends users to the Airflow UI
  [668 guide:128-129].
- Without Airflow access: set the ids in the manifest before triggering and read each back (section 5.2). EDS computes
  the ingested and the failed ids as the intersection and the difference of the ids it sent and the saved ids
  [668 eds/eds_ingest/fetch_ingestion_summary.py:50-57, :141-147, :211-223].

**Failure reporting.**

- Skipped entities go to XCom `skipped_ids` of the task that dropped them [668 op/validate_manifest_schema.py:87-91;
  668 op/ensure_manifest_integrity.py:80-81; 668 op/process_manifest_r3.py:236-237], and the final status task logs
  them [668 op/update_status.py:193-195].
- Skips fail a task only when the Airflow Variable `core__ingestion__raise_on_any_error` is true
  [668 op/base_osdu_operator.py:44; 668 op/validate_manifest_schema.py:93-96; 668 op/ensure_manifest_integrity.py:83-86;
  668 op/process_manifest_r3.py:239-242], with two exceptions. Validation and integrity fail when a whole non-empty
  section was removed (`MasterData`, `ReferenceData`, or `Data`, which counts datasets, components and the work
  product) [668 op/base_osdu_operator.py:99-159; 668 op/validate_manifest_schema.py:98-104;
  668 op/ensure_manifest_integrity.py:88-94]. Processing fails when it saved nothing from a non-empty manifest
  [668 op/base_osdu_operator.py:161-184; 668 op/process_manifest_r3.py:244-250].
- XCom `errors` holds `ManifestError.corrupted_records` for manifest errors and the exception text otherwise
  [668 op/base_osdu_operator.py:86-94; 668 osdu_airflow/exceptions/exceptions.py:120-129]. The masking of `Bearer`
  tokens computed at `:88` is not used: `:92` and `:94` push `str(exception)`.
- Batch mode: a list item that raises `UploadFileError`, `HTTPError`, `GetSchemaError`, `SchemaError` or
  `GenericManifestSchemaError` is logged and skipped [668 op/process_manifest_r3.py:143-152], so a run can end
  `finished` with whole manifests missing. The batch tasks name `provide_manifest_integrity_task_N` as their
  predecessor, a task the DAG never defines [147 r3:133-139], so they read `execution_context["manifest"]`
  [668 op/base_osdu_operator.py:51-59] (inference: no XCom exists under that id); schema and integrity checks run
  inside the processor [668 op/process_manifest_r3.py:215-222]. Task N takes the slice `[(N-1)*s, N*s)` with
  `s = ceil(len / batch_count)` [668 op/process_manifest_r3.py:100-109], where `batch_count` is read again when the
  task runs [668 op/process_manifest_r3.py:84]. The second argument of `process_manifest` is `True` for list items and
  `False` for a single manifest [668 op/process_manifest_r3.py:121-124, :136-140]; its meaning is inside
  `osdu-ingestion`.
- Retries 0; `dagrun_timeout` 180 minutes [147 r3:59-64, :92].

### 3.3 `Osdu_ingest_by_reference` and manifests by reference

**Name and availability.** `Osdu_ingest_by_reference` [147 ref:60]. Deployment is uneven: GC renders, publishes and
deploys it [147 devops/gc/pipeline/override-stages.yml:16-106]; Azure zips the whole DAG folder, but its registration
body names only `Osdu_ingest` [147 deployments/scripts/azure/output_dag_folder.py:7, :15-23]; IBM copies only
`osdu-ingest-r3.py` and a tutorial DAG [147 devops/ibm/ibm-manifest-dag/files/copy-dags.sh:11-12;
147 devops/ibm/ibm-stages.yml:8, :31]. The by-reference process operator reads `core__service__storage__url` and
`core__service__file__host` without defaults when it is constructed [668 op/process_manifest_r3_by_reference.py:84-85],
that is when the DAG file is parsed; the enyparser DAG notes that a `Variable.get` at parse time breaks DAG import when
the Variable is missing [1697 dag:164-166]. Probe before relying on it (section 2.1).

**Task graph.** As `Osdu_ingest`, with the by-reference operators; the validate task's predecessor is
`check_payload_type` [147 ref:90-139].

**Execution context.**

| Key | Shape | Required | Read at |
| --- | --- | --- | --- |
| `Payload` | section 3.1 | yes | section 3.1 |
| `manifest` | string: the `dataset--File.Generic` record id of the manifest file | yes | [147 ref:63-80] |
| `acl`, `legal` | objects | fallbacks for the files the DAG writes (below); required by the EDS request model | [668 op/base_osdu_operator_by_reference.py:218-224, :266-273; 668 eds/models/workflow_framework/manifest_by_reference.py:65-106] |
| `userId` | string | no | [668 op/process_manifest_r3_by_reference.py:175] |

Only the string form works end to end (inference from the code):

- String: `check_payload_type` pushes `[manifest]` to XCom `manifest_ref_ids` [147 ref:72-74], and the validate task
  reads the last entry of that list [668 op/validate_manifest_schema_by_reference.py:119-123;
  668 op/base_osdu_operator_by_reference.py:50-53].
- Object: routed to the same validate task [147 ref:70-71], but nothing is pushed, so the lookup has no list to index,
  and the task re-raises every such exception as `ServiceError` [668 op/validate_manifest_schema_by_reference.py:168-169].
- Array: routed to `batch_upload`, whose tasks read the record id from the XCom of `provide_manifest_integrity_task_N`
  [147 ref:129-135; 668 op/base_osdu_operator_by_reference.py:54-55], a task the DAG never defines (it defines
  `provide_manifest_integrity_task` only [147 ref:112-116]); the batch branch of `_process_manifest` also returns one
  list where `execute` unpacks two values [668 op/process_manifest_r3_by_reference.py:127-155, :243-245]. The enyparser
  collection describes an array as "a batch of either" [1697 ENY group 40 description]; the code does not bear that out.
- EDS validates the id against `^[\w\-\.]+:dataset\-\-File\.Generic:[\w\-\.\:\%]+$`
  [668 eds/models/workflow_framework/manifest_by_reference.py:99-106].

**The manifest file.** Stored through the Dataset service as `dataset--File.Generic` (section 2.2.1). EDS writes a JSON
document with top-level `kind` (`<schema authority>:wks:Manifest:1.0.0`), `acl`, `legal`, `MasterData`,
`ReferenceData` and `Data {WorkProduct, WorkProductComponents, Datasets}`
[668 eds/eds_ingest/data_ingestor/implementation/manifest_by_reference_strategy.py:125-184;
668 eds/models/dataset_framework/upload_manifest_payload.py:69-128; 668 eds/eds_ingest/constants.py:29]. The DAG takes
the Dataset client's retrieval instructions for the id, downloads `datasets[0].retrievalProperties.signedUrl` without
auth and parses JSON, accepting a JSON string that itself holds JSON [668 op/base_osdu_operator_by_reference.py:61-70].
Content that does not decode logs "Invalid Manifest data format. Please upload the manifest data in binary format." and
fails the task [668 op/validate_manifest_schema_by_reference.py:164-166]. Top-level `acl` and `legal` (with `status`)
in the file matter, because the DAG copies them onto the files it writes (below).

**Trigger.** `POST /v1/workflow/Osdu_ingest_by_reference/workflowRun` with
`executionContext {Payload {AppKey, data-partition-id}, acl, legal, manifest: "<dataset--File.Generic id>"}`
[668 eds/eds_ingest/data_ingestor/implementation/manifest_by_reference_strategy.py:60-99;
668 eds/eds_ingest/workflow_framework/manifest_by_reference_workflow.py:1-9]. The Energistics and enyparser
collections send the same body without `acl` and `legal` [1414 EP 07; 1697 ENY 40.1].

**When to use it (size).**

- The Workflow contract states no limit (section 1).
- EDS: `OSDU_MANIFEST_PAYLOAD_LIMIT = 12000  # Limit in Azure's HTTP header size`, in kilobytes
  [668 eds/eds_ingest/constants.py:108-109], overridable with the Airflow Variable
  `core__config__manifest_payload_limit` [668 eds/eds_ingest/utilities/airflow_utility.py:35-48]. The size is
  `sys.getsizeof(json.dumps(request, indent=4).encode("utf-8")) / 1024` over the whole trigger request
  [668 eds/eds_ingest/data_ingestor/implementation/ingestion_strategy.py:42-50]. Above the limit EDS uses the
  by-reference workflow when the probe finds it
  [668 eds/eds_ingest/data_ingestor/implementation/ingestion_strategy.py:51-59], and otherwise splits the manifest
  into several `Osdu_ingest` runs
  [668 eds/eds_ingest/data_ingestor/implementation/manifest_ingestion_strategy.py:43-179]. The guide
  words this as "Manifest Payload <= 12 MB" inline and "> 12 MB" by reference or chunked [668 guide:633-645;
  668 docs/docs/eds-fetch-and-ingest.md:125-127].
- enyparser: a translated manifest "for a real package is megabytes, and XCom is a row in Airflow's metadata
  database" [1697 dag:26-29]; an inline object would put the whole manifest in the request and then in that database
  [1697 ENY 40.1].

**What it creates.** The manifest's records (as `Osdu_ingest`), plus `dataset--File.Generic` records minted on every
run:

| Record | Created by | Id visible in |
| --- | --- | --- |
| the validated manifest | `validate_manifest_schema_task` [668 op/validate_manifest_schema_by_reference.py:154-163] | that task's XCom `return_value` |
| the integrity-checked manifest | `provide_manifest_integrity_task` [668 op/ensure_manifest_integrity_by_reference.py:139-148] | that task's XCom `return_value` |
| a report file holding `str(saved_ids)` (a Python dict rendering) | every run of a by-reference status task, so both `update_status_running_task` and `update_status_finished_task` [668 op/update_status_by_reference.py:182-205] | only the task log line `#SKIPPED_IDS: Some ids in the manifest were skipped. You can find the report in the datasetService with this record id : <id>`, written at error level whether or not anything was skipped, and twice when the run did not fail [668 op/update_status_by_reference.py:206-214] |

These files are uploaded to a signed URL from `storage_instructions(kind_sub_type="dataset--File.Generic")` without
auth and registered as `<kind_authority>:wks:dataset--File.Generic:1.0.0` (Airflow Variable `kind_authority`, default
`osdu`) with `data.DatasetProperties.FileSourceInfo {FileSource, PreloadFilePath: <unsignedUrl><signedUploadFileName>}`,
`ResourceSecurityClassification` `<partition>:reference-data--ResourceSecurityClassification:RESTRICTED:`,
`SchemaFormatTypeID` `<partition>:reference-data--SchemaFormatType:TabSeparatedColumnarText:`, the fixed `version`
1614105463059152 and an empty ancestry [668 op/base_osdu_operator_by_reference.py:149-216]. ACL and legal come, in
order, from the manifest's top-level `acl` (`viewers`, `owners`) and `legal` (`legaltags`,
`otherRelevantDataCountries`, `status`), else from the execution context's `acl` and `legal`, else from defaults: the
e-mails of the caller's Entitlements groups named `data.default.viewers` and `data.default.owners` (falling back to
`<group>@<data-partition-id>.<host of core__service__dataset__url>`) and the legal tag
`<data-partition-id>-demo-legaltag` with country `US` and status `compliant`
[668 op/base_osdu_operator_by_reference.py:97-129, :218-273]. The report files always take the execution-context or
default values [668 op/update_status_by_reference.py:189-200].

**Finding the results.** As `Osdu_ingest` (`record_ids`, `saved_record_ids`); the side-effect ids as in the table.

**Failure reporting.** As `Osdu_ingest`, except that the validate task turns every exception other than a JSON decoding
error into `ServiceError` [668 op/validate_manifest_schema_by_reference.py:164-169], and a failed status update fails
the status task before any report file is written [668 op/update_status_by_reference.py:149]. Retries 0;
`dagrun_timeout` 60 minutes [147 ref:51-56, :88].

### 3.4 `manifest_ingestion` (test DAG)

A single `BashOperator` that echoes, described as "used for testing Workflow service, it does nothing but returns OK
status only to Workflow service" [147 src/osdu_dags/manifest_ingestion.py:12-19]. It has no status task, so whatever
status the Workflow service reports for it is not written by the DAG (inference). It is not a delivery target.

### 3.5 CSV parser

**What it ingests.** One CSV file; each row becomes a record of one target kind. `KindHandler` sets every record's kind
to `data.ExtensionProperties.FileContentsDetails.TargetKind` of the file's metadata record
[202 core/handler/handlers/KindHandler.java:21-24]. The README says nested attributes are not supported and that no
date/time, unit or CRS conversion is done [D202:3-4], although the DAG's step list names `UNIT` and `CRS`
[202 dag:35]; those handlers are not among the files read.

**Execution context** [202 dag:30-36, :46-53].

| Key | Shape | Required | Notes |
| --- | --- | --- | --- |
| `id` | string | yes | the File-service metadata record id; the request is invalid without it when `LOAD_FROM_CSV` runs [202 core/ingestion/model/IngestionRequest.java:28, :59-63] |
| `dataPartitionId` | string | yes | `@NotEmpty` [202 core/ingestion/model/IngestionRequest.java:36-37]; also satisfies the status operators' fallback (section 3.1) |
| `data_service_to_use` | `"file"` (default) or `"dataset"` | no | passed as `dataServiceName` and as a pod environment variable [202 dag:34, :51, :58-59]; `"dataset"` moves the content download to the Dataset service [202 core/loader/RecordLoaderConfig.java:57-68] |
| `userId` | string | no | sent as the `x-on-behalf-of` header [202 core/util/Util.java:68] |
| `Payload` | object | no | when present, the status operators use it instead of `dataPartitionId` (section 3.1) |

The DAG hands the parser one JSON argument `{id, authorization, dataPartitionId, steps, dataServiceName, userId}`
[202 dag:46-53, :80], where `authorization` is `dag_run.conf['authToken']` [202 dag:31]. The parser strips `Bearer `
and sends the token as the bearer of its calls, with `correlation-id` and `data-partition-id` headers
[202 core/util/Util.java:56-72]. `run_id` is read but not passed [202 dag:33], so the parser's `runId` stays null
[202 core/ingestion/model/IngestionRequest.java:30]. The step list is fixed in the DAG: `LOAD_FROM_CSV, TYPE_COERCION,
ID, ACL, LEGAL, KIND, META, TAGS, UNIT, CRS, RELATIONSHIP, STORE_TO_OSDU` [202 dag:35]; the parser's own default flow
also has `SPATIAL_LOCATION` [202 core/flow/model/StepType.java:47-49]. IBM's collection triggers with
`{"executionContext": {"id": "{{csvRecordId}}", "dataPartitionId": "{{data-partition-id}}"}, "runId": "{{runId}}"}`
[202 IBM "Trigger Workflow"].

**Prerequisites.**

- The file and its metadata record, through the File service (section 2.2.2). IBM uploads with the upload URL, the
  signed URL, then `POST /files/metadata` with kind `<authority>:wks:dataset--File.Generic:1.0.0` and
  `data.ExtensionProperties.FileContentsDetails {TargetKind, FileType: "csv"}` [202 IBM "Get Upload Signed URL",
  "Upload File Using SignedURL", "Upload File Metadata"]; the core tests use kind
  `osdu:wks:dataset--File.Generic:1.0.0` with `TargetKind`
  [202 testing/csv-parser-core-test/src/main/java/org/opengroup/osdu/csvparser/test/core/IngestionSteps.java:219-240].
- The parser always reads the descriptor from the File service, `GET {FILE_SERVICE_ENDPOINT}/files/{id}/metadata`
  [202 core/file/FileService.java:91-117; 202 core/descriptor/DescriptorService.java:25-29], whatever
  `data_service_to_use` says, when it builds the ingestion context [202 core/ingestion/IngestionContext.java:40-47, :77-83].
- The descriptor's shape [202 core/descriptor/model/]:
  - `acl`, `@NotNull` and `@ValidAcl` [IngestionDescriptor.java:22-24]; `legal`, validated when present [:26-27];
    `data`, `@NotNull` [:29-31]; an optional top-level `tags` map, copied onto every record [:33;
    202 core/handler/handlers/TagsHandler.java:24-29].
  - `data.DatasetProperties.FileSourceInfo.FileSource`, `@NotEmpty` on the field [FileSourceInfo.java:17-19]; the
    `DatasetProperties` field carries no `@Valid` [IngestionData.java:17-18], so bean validation does not reach it
    (inference).
  - `data.ExtensionProperties.FileContentsDetails` with `TargetKind`, `FrameOfReference`, `relationships`,
    `relatedNaturalKey`, `unitOfMeasure`, `SpatialMapping` and `nestedFieldDelimiter` [FileContentsDetails.java:32-54].
  - Optional `data.encoding` and `data.format {delimiter, recordSeparator, quote, escape}`; when `format` is present,
    all four are required [IngestionData.java:20-23; IngestionFormat.java:15-29].
- The target kind's schema exists: it is loaded when the context is built [202 core/ingestion/IngestionContext.java:45-46],
  and its `x-osdu-natural-key` properties drive record ids [202 core/handler/handlers/IdHandler.java:61-88].
- Content: the File service's `GET {FILE_SERVICE_ENDPOINT}/files/{id}/downloadURL`, field `SignedUrl`
  [202 core/file/FileService.java:44-68], or, with `"dataset"`, the Dataset service's
  `POST {DATASET_SERVICE_ENDPOINT}/retrievalInstructions` with `datasetRegistryIds: [id]`, which must return exactly
  one dataset [202 core/dataset/DatasetService.java:43-77]. The Azure build sets `DATASET_SERVICE_ENDPOINT=xyz`
  [202 provider/csv-parser-azure/src/main/resources/application.properties:43] (inference: the Dataset path is not
  configured there unless the deployment overrides the property).
- The Airflow Variable `namespace`, read when the DAG file is parsed, with no default [202 dag:42-43].

**What it creates.** Records of `TargetKind` carrying the descriptor's `acl`, `legal` and `tags`
[202 core/handler/handlers/AclHandler.java:21-24; 202 core/handler/handlers/LegalHandler.java:21-24;
202 core/handler/handlers/TagsHandler.java:24-29], stored with `PUT {STORAGE_SERVICE_ENDPOINT}/records`
[202 core/storage/StorageClient.java:50-79] in batches of `OSDU_STORAGE_BATCH_SIZE`
[202 core/storage/StorageProperties.java:13-17; 202 core/storage/StoringStrategy.java:38-56]: 10 on GC and core-plus,
50 on Azure [202 provider/csv-parser-gc/src/main/resources/application.properties:25;
202 csv-parser-core-plus/src/main/resources/application.properties:25;
202 provider/csv-parser-azure/src/main/resources/application.properties:18].

Record ids: when the schema marks natural keys, `id = <data-partition-id>:<TargetKind segment 3>:<TargetKind segment
2>-<Base64 of the natural-key values concatenated in key order, "=" removed>`
[202 core/handler/handlers/IdHandler.java:38-59]. For the `TargetKind` `osdu:wks:master-data--Well:1.0.0` that is
`<partition>:master-data--Well:wks-<base64>` (inference from the format string). Values are matched to schema
properties case-insensitively, nested objects included [IdHandler.java:68-88]. Without natural keys the handler logs
"No natural keys found for Record. Id will be generated from Storage service." and sets no id [IdHandler.java:52-56].

**Finding the results.**

- The pod is a plain `KubernetesPodOperator` without XCom [202 dag:75-83], so `saved_record_ids` is empty
  [668 op/update_status.py:107-124] (inference).
- Per-record outcomes are published as status events: kind `status`, stage `INGESTOR_SYNC`, `SUCCESS` with the record
  id (or the id Storage generated) or `FAILED` with the root message; a job failure uses stage `INGESTOR` with
  `recordId` set to the run id [202 core/gsm/IngestionStatusPublisher.java:43-46, :117-173]. They go to a messaging
  topic, not to the Workflow service: `status-changed` on GC and core-plus
  [202 provider/csv-parser-gc/src/main/resources/application.properties:59-60;
  202 csv-parser-core-plus/src/main/resources/application.properties:59-60;
  202 provider/csv-parser-gc/docs/gcp/README.md:31-32], `statuschangedtopic` by default on Azure
  [202 provider/csv-parser-azure/src/main/resources/application.properties:40]. A failure to publish is only logged
  [202 core/gsm/IngestionStatusPublisher.java:235-241].
- Practical read-back: compute the natural-key ids and read them, or query by kind as IBM does
  [202 IBM "Search Records For Schema", "Get all records for a kind"]; the core tests query by kind plus id or field
  [IngestionSteps.java:320, :340].

**Failure reporting.**

- A handler error on a row is logged at WARNING and the remaining handlers still run on that row
  [202 core/ingestion/IngestionService.java:51-62] (inference: a row whose `ID` step failed is stored with a
  Storage-generated id).
- A failed Storage batch is logged with "No further retry will be attempted." and published as `FAILED` statuses, not
  thrown [202 core/storage/StoringStrategy.java:58-71]. The run can therefore end `finished` with rows missing.
- A missing or invalid argument or request (`BadRequestException`) is thrown before ingestion starts and publishes no
  status [202 core/CsvIngestionRunner.java:40-45, :62-86]. Descriptor, schema and other errors raised during ingestion
  publish a job status and are rethrown [202 core/CsvIngestionRunner.java:51-58], which fails the pod and the run.
- The DAG sets `retries: 1` with a 5-minute delay [202 dag:23-24]: a retried attempt stores the rows again, and rows
  without natural keys get new Storage-generated ids (inference).

### 3.6 `Energyml_Converter`

**What it does.** Loads EPC or XML files plus HDF5 from the Dataset service and writes an OSDU manifest back to the
Dataset service [1363 op/content_loading.py:124-159; 1363 op/energistics_translation.py:179-249;
1363 app/energyml_translation.py:217-306]. "The conversion DAG does NOT ingest the resulting manifest in the catalog.
It is a separated operation." [1414 EP collection description]. Ingestion is a second run, of
`Osdu_ingest_by_reference` with `manifest` set to the generated id [1414 EP 07]. The project README says only that it
holds the DAGs for `energistics-parser-lib` [D1414:1-3].

**Two variants.** The operator variant runs `ContentLoading` and `EnergisticsTranslation` inside Airflow
[1414 conv:61-73]. The pod variant, which GC deploys, runs two containers, passes the whole execution context to both
as `--context`, and passes the second the ids the first returned [1414 k8s:47, :63, :92-118].

**Execution context.**

| Key | Shape | Required | Read at |
| --- | --- | --- | --- |
| `Payload` | section 3.1 | yes: `Context.populate` in the operator variant, the status tasks in both | [1363 op/content_loading.py:65-66; 1414 conv:51-59; 1414 k8s:88-90, :120-124] |
| `dataset_xml` | list (or a single string) of EPC or XML dataset record ids, not empty | yes | [1363 op/content_loading.py:124, :127-137; 1363 app/content_loading.py:140-149] |
| `dataset_h5` | list (or a single string) of HDF5 dataset record ids; read with `[]`, so the key must be present, and it may be empty | yes | [1363 op/content_loading.py:125, :143-147; 1363 app/content_loading.py:141]; the collection sends `[]` when no HDF5 was uploaded [1414 EP 03, EP 04] |
| `data_partition_id` | string | pod variant | [1414 k8s:44, :63, :73] |
| `userId` | string | no | [1414 k8s:46, :74] |
| parser configuration overlay | below | no | [1363 op/content_loading.py:104-119; 1363 op/energistics_translation.py:147-163; 1363 README.md:7-41] |

`dataset_xml` and `dataset_h5` are constants of `commons-parser` [1363 op/content_loading.py:30-33;
1310 resqml_parser/resqml_namespace_functions.py:23-28]; the spellings above are those the reference request sends
[1414 EP 04] and the pod template reads from the first container's output [1414 k8s:110-111], and the WITSML and
PRODML parsers define `XML_DATASETS_KEY = "dataset_xml"` [1311 witsml_parser_v2/witsml_namespace_functions.py:39;
1308 prodml_parser/prodml_namespace_functions.py:33].

Overlay keys in the reference request [1414 EP 04]: `acl`, `legal {legaltags, otherRelevantDataCountries, status}`,
`tags_every_entity_keys`, `data-partition-id`, `namespace`, `schema_authority`, `schema_version`, `authors`,
`app_name`, `app_key`, `wgs84_projected_epsg_code`, `wgs84_vertical_epsg_code`, `use_vertical_crs`,
`compute_spatials`, `override_files_acl`, `override_files_legals`, `store_complete_geo_json`,
`store_complete_geo_json_wgs84` and `ignore` (regular expressions of entity types to skip). Their meaning is in
[1310 README.md:41-66]. A configuration key found in the context overrides the configuration file
[1363 op/content_loading.py:104-119]. The translation wraps the `tags_every_entity_keys` value in a list
[1363 op/energistics_translation.py:170-174; 1363 app/energyml_translation.py:208-212]. The library README's own
example writes `legal` with `tags` and `country` [1363 README.md:28-35], not the keys the reference request uses.

**Prerequisites.**

- Each EPC, XML and HDF5 file uploaded and registered through the Dataset service (section 2.2.1) [1414 EP 01, 02, 03].
- A parser configuration file at `PARSER_CONFIG_PATH` [1363 README.md:5]; the code logs a missing or unreadable file at
  debug level and continues with an empty configuration [1363 op/content_loading.py:93-100;
  1363 app/energyml_translation.py:177-183].
- The CRS catalog and converter, Unit and Dataset services [1363 README.md:44-47]. The operator variant reads their
  URLs from Airflow Variables without defaults when its operators are constructed [1363 op/content_loading.py:48-50;
  1363 op/energistics_translation.py:60-64]; the pod variant from rendered environment variables [1414 k8s:67-77;
  1414 deployments/scripts/gc/render_dag_file.py:19-24].
- The pod variant hands no caller token to its containers (the `authToken` lines are commented out [1414 k8s:42-43]);
  the containers use `osdu-ingestion`'s token refresher when Airflow is importable and `osdu-api`'s
  `BaseTokenRefresher` otherwise [1363 app/energyml_translation.py:33-37]. Neither is verified here.

**What it creates (before any ingestion).** One dataset record per XML part of the EPC and "mini-hdf5" files per
RESQML entity [1310 README.md:149-150]; GeoJSON datasets (`dataset--File.GeoJSON`) when configured
[1310 README.md:155-183]; the manifest file, named `manifest.json` [1363 op/energistics_translation.py:227-242;
1363 app/energyml_translation.py:272-287]; and two report files, because both status tasks are
`UpdateStatusOperatorByReference` [1414 conv:51-59; 1414 k8s:88-90, :120-124; section 3.3]. After the follow-up
ingestion, `tags_every_entity_keys` values are in `data.Tags` of every work-product component and master-data record
[1310 README.md:66], and the EPC dataset's name is added to `data.Tags` of every work-product component
[1310 README.md:185-195].

**Finding the results.**

- Manifest id: the operator variant pushes XCom `record_ids = [manifest_record_id]` and returns `{record_id, manifest}`
  [1363 op/energistics_translation.py:244-249], so `saved_record_ids` of `update_status_finished_task` holds it; the pod
  variant writes `{record_id, manifest, errors}` to `/airflow/xcom/return.json` [1363 app/energyml_translation.py:289-300].
  No execution-context key sets the manifest id in either variant; whether a `commons-parser` configuration key could
  is not visible in these sources. The collection reads the id through Airflow's REST API at
  `{{AIRFLOW_URL}}/api/v1/dags/Energyml_Converter/dagRuns/{{RUN_ID}}/taskInstances/update_status_finished_task/xcomEntries/saved_record_ids`,
  takes it from the rendered `'energyml_manifest_creation': [...]` entry and strips a trailing `:`; otherwise it tells
  the user to open the Airflow UI [1414 EP collection description, EP 06, EP 06b].
- Ingested records: search by tag, `kind` `<authority>:<source>:*--*:*.*.*` with `query`
  `data.Tags:"MY_TAG_for-all-WPC"` [1414 EP 08].

**Failure reporting.** An empty `dataset_xml` fails with "Nothing to translate. No record id found for epc/xml file.
(check the 'dataset_xml' attribute in the payload you sent)" [1363 op/content_loading.py:127-131]. The pod variant
writes an `errors` list whose default is "No errors, or error feedback is not currently supported"
[1363 app/energyml_translation.py:263-265, :296-298], and the DAG does not inspect it [1414 k8s:104-118]. Retries 0 in
both variants. The operator variant sets `dagrun_timeout` 60 minutes [1414 conv:32-37, :48]; the pod variant puts
`execution_timeout` and `dagrun_timeout` of 24 hours into `default_args` [1414 k8s:29-37]. The operator variant
constructs both operators with `previous_task_id=None` [1414 conv:61-69], and the translation pulls `return_value`
with that task id [1363 op/energistics_translation.py:81-83] (section 8).

### 3.7 `Energyml_Delivery`

**What it does.** "deliver an epc + h5 couple from the storage service (WPC must have been generated by the
energyml-converter-dag)" [1414 deliv:15]. The collection labels it "[WorkInProgress]" [1414 EP 09]. It exports; it
does not ingest.

**Execution context.** `Payload`; `ids`, any JSON (the collection passes a Search response [1414 EP 08, EP 09]),
handed to `energyml-delivery`'s `_find_key_values` with the key pattern `^[iI][dD]$`
[1363 op/energistics_delivery.py:23, :95] (the helper is not read; by its name and arguments it collects the values
under `id` keys); `name`, optional, default `delivery`, with a `.epc` or `.h5` suffix stripped
[1363 op/energistics_delivery.py:91-103]; the configuration overlay [1363 op/energistics_delivery.py:123-139].

**What it creates.** An EPC and an HDF5 dataset record [1363 op/energistics_delivery.py:141-171] and two report files
(by-reference status tasks) [1414 deliv:49-57]. **Finding them:** XCom `return_value {"epc": <id>, "h5": <id>}` only
[1363 op/energistics_delivery.py:174-177].

**Limits.** Retries 0; `dagrun_timeout` 60 minutes [1414 deliv:30-35, :46].

### 3.8 `Enyparser_Translation`

**What it does.** One pod translates RESQML, WITSML, PRODML or EML, stores the load manifest as a dataset record and
returns its id; "This DAG translates; it does not ingest." Ingestion is a second run, of `Osdu_ingest_by_reference`
with that id [1697 dag:15-32, :50-53; 1697 README:26-38].

**Execution context** [1697 main:326-481].

| Key (aliases) | Shape | Required | Read at |
| --- | --- | --- | --- |
| `Payload.AppKey`, `Payload["data-partition-id"]` | strings | yes | [1697 dag:64-69, :284-286]; the partition is lifted into the engine configuration unless the overlay sets `partition` [1697 main:476-479] |
| `source_dataset_ids` (`sourceDatasetIds`), `epc_dataset_id` (`epcDatasetId`) | string or list; both keys may be given; duplicates and blanks are dropped | at least one | [1697 main:326, :349-354, :401-427] |
| `h5_dataset_id` (`h5DatasetId`, `hdf5_dataset_id`) | string or list | no; without it the run logs a warning and writes no footprints | [1697 main:328, :380-386, :430-460; 1697 README:106-109] |
| `manifest_dataset_id` (`manifestDatasetId`) | string (the first id is used) | no, but it is the only way for the caller to know the manifest id; reusing an id overwrites that record | [1697 main:241-258, :331; 1697 README:86-92] |
| `work_product_name` (`workProductName`) | string | no | [1697 main:347] |
| `enyparserConfig` (`enyparser_config`) | object; the engine settings forbid unknown fields | no | [1697 main:463-481; 1697 README:102-105] |

The reference request puts `legal.legaltags` and `acl` under `enyparserConfig` [1697 ENY 20.1]. The DAG hands
`authToken` to the pod as the environment variable `OSDU_ACCESS_TOKEN`, not as an argument, and the whole context as
`--execution-context`, with `--publish-manifest` and without `--fail-on-error` [1697 dag:266-273, :299-313].

**Prerequisites.**

- Sources and HDF5 registered through the Dataset service. The collection registers
  `osdu:wks:dataset--File.Generic:1.0.0` with `data.Name` and `DatasetProperties.FileSourceInfo {FileSource, Name}`
  [1697 ENY 10.1-10.6] and notes that `dataset--FileCollection.EPC` is also readable [1697 ENY group 10 description].
  A file name on the record matters: its suffix selects the loader [1697 README:134-138].
- The pod fetches each record's retrieval instructions with the caller's token
  [1697 orchestrator/app/enyparser_dag/fetch.py:27-31, :63-66].
- Airflow Variables: `image__enyparser`, `core__service__workflow__url`, the Storage, Dataset, Search and CRS catalog
  URLs, `enyparser__cloud_provider` (no default), the namespace, the kube config and resources [1697 dag:71-89;
  1697 README:276-286].

**What it creates.** The manifest as a `File.Generic` dataset named `enyparser-load-manifest.json`
[1697 main:81-86, :261-307]. Every run re-uploads the package and every GeoJSON and registers them without an id, so a
re-run creates new dataset records for identical bytes, and the new manifest links to the new set
[1697 README:145-167; 1697 docs/extending.md:242-255]. Translated record ids are derived, not requested
[1697 docs/extending.md:257-260]. The status tasks are the non-reference `UpdateStatusOperator`
[1697 dag:107, :248, :323-326], so no report files are minted.

**Finding the results.** Fetch the manifest at the chosen `manifest_dataset_id` [1697 ENY 30.1, 30.2]; the task's XCom
is `{"manifest_dataset_id", "records"}` [1697 main:302-307]. After ingestion, the ids to verify are the ones in that
manifest; the collection's search example uses `kind` `*:*:work-product-component--*:*` with `data.Name` and warns that
indexing is asynchronous [1697 ENY 90.4].

**Failure reporting.** The container writes `{"error": ...}` and exits 1 [1697 main:180-187];
`check_translation_result`, attached as the pod task's `post_execute`, fails the task on an empty payload, an `error`,
or a payload with neither `manifest_dataset_id` nor `manifest` [1697 dag:188-214, :317]. Record-level diagnostics do
not fail the run, because the DAG does not pass `--fail-on-error` [1697 main:120-124, :229-232; 1697 dag:299-313]; "A
`finished` run is not the same as a complete translation" [1697 ENY 20.2]. Retries 0; no `dagrun_timeout`
[1697 dag:169-185, :235-244].

### 3.9 SEG-Y to OpenVDS

**What it does.** Converts a SEG-Y file already in Seismic DMS into an OpenVDS dataset in Seismic DMS, then ingests a
metadata manifest [469 dag:25, :200-239].

**Execution context (current DAG).**

| Key | Shape | Required | Read at |
| --- | --- | --- | --- |
| `Payload` | section 3.1 | yes | [469 dag:107, :171] |
| `file_record_id` | `dataset--FileCollection.SEGY` record id | yes | [469 dag:126, :128, :173] |
| `work_product_id` | `work-product--WorkProduct` record id | yes | [469 dag:172] |
| `id_token` | Seismic DMS token; `authToken` otherwise | no | [469 dag:105, :131-141] |
| `segyimport_arguments` | list of extra `SEGYImport` arguments | no | [469 dag:144-147] |
| `userId` | string | no | [469 dag:109, :176] |

The DAG's docstring example also shows `persistent_id` [469 dag:27-39], and the quickstart shows `vds_url` and
`persistent_id` [469 QS:214-225]; the current DAG derives both from the SEG-Y path and reads neither [469 dag:126-127].
The README's `url_connection`, `input_connection`, `segy_file` and `url` example belongs to the older flavour
[D469:102-130].

**Prerequisites.**

- The SEG-Y file uploaded to Seismic DMS, and the `FileCollection.SEGY` and WorkProduct records ingested with
  `Osdu_ingest`, with the sd-path in the file collection record (the quickstart writes
  `data.DatasetsProperties.FileCollectionPath`) [469 QS:159-199].
- Optionally `data.VectorHeaderMapping[]` on the file record, which overrides byte locations [469 QS:201-202;
  469 dag:124, :128-129, :149-156].
- The entitlement groups the quickstart lists [469 QS:18-28].
- IBM's collection creates the `FileCollection.SEGY`, `SeismicBinGrid`, `SeismicTraceData` and `WorkProduct` records
  with Storage `PUT /records` before triggering [469 IBM "Store File Collection With SD Path",
  "Create Seismic Bin Grid Record", "Create Seismic Trace Record", "Create Seismic Work Product"].
- Airflow Variables `core__service__seismic__url`, `image__segy_to_vds_converter` and the resource settings
  [D469:47-56].

**What it creates.** The OpenVDS dataset at `<vds_url>/<persistent_id>` [469 dag:137-142, :187-191], and a manifest
built by `OpenVDSMetadata.create_metadata(...)` (in `osdu-ingestion`), which `ProcessManifestOperatorR3` ingests from
the `post_conversion` task's XCom [469 dag:185-198, :229-232]. The quickstart says a new OpenVDS file record is created
and the SeismicTraceData record gains an `Artefacts` reference to it [469 QS:228].

**Finding the results.** XCom `record_ids` of `process_single_manifest_file_task`; without Airflow, read the
SeismicTraceData record and follow `data.Artefacts` [469 QS:228; 469 IBM "Storage API Search Record"].

**Failure reporting.** A pod failure fails the run; the metadata ingestion behaves as `Osdu_ingest`; the final status
task uses `all_done` [469 dag:234-237]. Retries 0; `dagrun_timeout` 24 hours [469 dag:77-82, :204].

### 3.10 SEG-Y to ZGY

**Execution context** [460 dag:32-41, :54, :59-72].

| Key | Required | Notes |
| --- | --- | --- |
| `data_partition_id` | yes | pod `OSDU_DATAPARTITIONID` |
| `filecollection_segy_id` | yes | first id after `--osdu` |
| `work_product_id` | yes | second id |
| `sd_svc_api_key`, `storage_svc_api_key` | yes (the template references them) | "It can be a random string if the key is not required in the deployment" [460 testing:88-97] |
| `id_token` | no | Seismic DMS token; `authToken` otherwise [460 dag:33] |
| `userId` | no | pod `USER_ID` [460 dag:41, :71] |
| `access_token` | Azure and IBM variants | Azure renders `STORAGE_SVC_TOKEN` and `SD_SVC_TOKEN` from it [460 deployments/scripts/azure/output_dag_folder.py:23-28]; IBM's standalone DAG sets both to `Bearer ` plus it [460 devops/ibm/segy_to_zgy_ingestion_dag_ibm.py:37, :56] |
| `Payload` | needed in practice | the documented v2 context has only the five keys above [460 testing:123-139], which gives the status operators none of `Payload`, `data-partition-id` and `dataPartitionId` (section 3.1); the baremetal example includes `Payload` [460 deployments/scripts/baremetal/Readme.md:22-36] |

In the base DAG the Storage token is always `authToken` [460 dag:32, :63]. The ids must be latest-version references
without a version number [D460:204]; the README's example ends each id with `:` [D460:208].

**Prerequisites.** The SEG-Y file in Seismic DMS, and `dataset--FileCollection.SEGY`, `work-product--WorkProduct`,
`work-product-component--SeismicTraceData` and `work-product-component--SeismicBinGrid` records with correct
references: `FileSource` set to the sd-path; SeismicTraceData `data.Datasets` set to the file collection id and
`data.BinGridId` to the bin grid id; WorkProduct `data.Components` listing both components [460 testing:5-11, :56-83;
sample records under `doc/sample-records/volve`]. Indexing is a prerequisite of the conversion and runs when the index
is missing and `SEGYTOZGY_GENERATE_INDEX` is set [D460:134]; the DAG sets it [460 dag:70].

**What it creates** (directly through Storage, without a manifest): a new version of the SeismicTraceData record whose
`data.Artefacts[]` gains an entry with `RoleID` `<partition>:reference-data--ArtefactRole:ConvertedContent:` pointing
at a new `dataset--FileCollection.Slb.OpenZGY` record; the ZGY file in Seismic DMS, named after the input with a GUID
inserted and `.zgy` as the extension; and index files next to the input [D460:101-126; 460 testing:155-191].

**Finding the results.** Read the latest SeismicTraceData record, take the `Artefacts` entry, then read the OpenZGY
record and its `data.DatasetProperties.FileSourceInfos[].FileSource` [460 testing:155-181]. The pod pushes no XCom
[460 dag:84-93].

**Failure reporting.** The pod's exit code; a human-readable error near the end of the converter output
[460 testing:203-208]. On failure a stale ZGY file or a dangling OpenZGY record can remain, and concurrent conversions
of the same SeismicTraceData record can lose artefact references [D460:167, :228-230]. Retries 0;
`execution_timeout` and `dagrun_timeout` of 24 hours in `default_args` [460 dag:19-27].

### 3.11 SEG-Y to MDIO

The project has two parts, the converter application and the DAG that runs it in a pod [D1551:7-10].

**Execution context.**

| Key | Shape | Required | Read at |
| --- | --- | --- | --- |
| `Payload["data-partition-id"]` (and `AppKey`) | strings | yes | [1551 dag:156] |
| `work_product_id` | string | yes | [1551 dag:153] |
| `filecollection_segy_id` | string | yes | [1551 dag:154] |
| `mdio_sd_path` | new Seismic DMS path | yes | [1551 dag:155] |
| `client_id`, `client_secret`, `refresh_token`, `refresh_url` | strings; all four or none | no | [1551 dag:162-165]; a missing key renders as the string `None`, which counts as absent, and a partial set raises `PartialCredentialsError` [1551 app/utils/validators.py:29-70; 1551 app/utils/constants.py:16] |
| `lossless` | bool | no | [1551 dag:58-62] |
| `compression_tolerance` | float | no | [1551 dag:63-67] |
| `grid_overrides` | object (`ChannelWrap`, `AutoChannelWrap`, `ChannelsPerCable`, `CalculateCable`) | no | [1551 dag:68-72; 1551 airflow/README.md:63-71] |
| `chunksize` | list of int | no | [1551 dag:73-77] |

The DAG passes `authToken` as the `--auth` argument [1551 dag:157]; with all four client credentials the container
uses those instead [1551 app/main.py:136-155]. The e2e suite sends the credentials on GCP and the string `None`
elsewhere [1551 E2E 4.3].

**Prerequisites.** The SEG-Y file in Seismic DMS; a record whose kind contains `osdu:wks:dataset--FileCollection.SEGY`
(the `osdu` authority is fixed) with `data.DatasetProperties.FileCollectionPath`; an `osdu:wks:work-product--WorkProduct`
record whose `data.Components` ids containing `work-product-component--Seismic` resolve to kinds containing
`osdu:wks:work-product-component--Seismic` [1551 app/osdu_metainformation.py:34-36, :138-166, :208-236, :285-304]. The
e2e suite ingests these with `Osdu_ingest` first [1551 E2E 3.1, 3.2]. Airflow Variables for the image, the resources
and the Storage and Seismic URLs [1551 airflow/README.md:26-36].

**What it creates.** The MDIO dataset at `mdio_sd_path`, and a manifest in XCom that (a) updates every work-product
component whose `data.Datasets` references the SEG-Y record, appending an artefact with a surrogate `ResourceID`
(`surrogate-key:record-<uuid>`), `ResourceKind` `osdu:wks:dataset--FileCollection.TGS.MDIO:1.0.0` and `RoleID`
`<partition>:reference-data--ArtefactRole:ConvertedContent:`, and (b) adds a `FileCollection.TGS.MDIO` record with
the ACL and legal of the SEG-Y record and `DatasetProperties.FileCollectionPath`
[1551 app/mdio_metainformation.py:22, :92-173; 1551 airflow/README.md:78-136]. `ProcessManifestOperatorR3` ingests it
with the pod task as predecessor [1551 dag:183-187].

**Finding the results.** XCom `record_ids`; without Airflow, read the SeismicTraceData record, take
`data.Artefacts[0].ResourceID` without its trailing `:`, then read that MDIO record and its `FileCollectionPath`
[1551 E2E 5.3, 5.4; 1551 e2e/README.md:66].

**Failure reporting.** The container writes `{"error": ...}`; for errors after the MDIO dataset was created it first
deletes that dataset; with an output path it does not raise [1551 app/main.py:190-207]. The pod task can therefore
succeed, and the separate `check_conversion_result` task fails the run when the XCom is not a dict or carries `error`
[1551 dag:92-109, :177-181]. The final status task uses `all_done` [1551 dag:189-191]. Retries 0; no
`dagrun_timeout` [1551 dag:79-89, :112-117].

### 3.12 Per-run options and deployment settings

No DAG or operator in these sources reads a context key that skips schema validation or integrity checks. The context
keys read are the ones listed per workflow above; a scan of the `execution_context`, `dag_run.conf` and `params` reads
covered the operators of project 668 and the DAG sources of 147, 202, 1414, 1363, 1697, 469, 460 and 1551.

| Workflow | Per-run option | Effect |
| --- | --- | --- |
| `Osdu_ingest` | `manifest` as an array | the batch path (section 3.2) |
| `Osdu_ingest_by_reference` | none usable | the array form is not coherent (section 3.3) |
| CSV | `data_service_to_use` | File or Dataset content download (section 3.5); the step list is fixed in the DAG, although the parser accepts `source`, `destination`, `storageSas`, `sourceFiles`, `steps` and `splitterConfig` [202 core/ingestion/model/IngestionRequest.java:43-57] |
| `Energyml_Converter` | overlay keys | `ignore`, `override_files_acl`, `override_files_legals`, `compute_spatials`, `store_complete_geo_json`, `store_complete_geo_json_wgs84`, `tags_every_entity_keys` and the rest (section 3.6) |
| `Energyml_Delivery` | `ids`, `name`, overlay keys | section 3.7 |
| `Enyparser_Translation` | `enyparserConfig`, `work_product_name`, `manifest_dataset_id` | engine overlay; record-level failures never fail the run from this DAG (section 3.8) |
| SEG-Y to VDS | `segyimport_arguments` | extra converter arguments |
| SEG-Y to MDIO | `lossless`, `compression_tolerance`, `grid_overrides`, `chunksize` | converter options |
| SEG-Y to ZGY | none | indexing, verbosity and cache sizes are fixed in the DAG [460 dag:59-72] |

Deployment-level settings (not settable per run):

| Setting | Default | Effect | Citation |
| --- | --- | --- | --- |
| `core__ingestion__batch_count` | 3 | number of parallel batch tasks (DAG parse time) and the slice size (task run time) | [147 r3:47-52; 668 op/process_manifest_r3.py:84] |
| `core__ingestion__batch_save_enabled` | false | saves manifest entities to Storage in batches | [668 op/process_manifest_r3.py:85-89; D147:127-132; D668:47] |
| `core__ingestion__batch_save_size` | 500 | batch size for the above | [668 op/process_manifest_r3.py:71, :90-95; D668:48] |
| `core__ingestion__thread_save_number` | 1 | parallel Storage writes | [668 op/process_manifest_r3.py:96-98; D668:49] |
| `core__ingestion__raise_on_any_error` | false | fails the task when any entity is skipped | [668 op/base_osdu_operator.py:44; D668:46] |
| `core__config__reference_patterns_whitelist` | none | passed to the integrity validator | [668 op/ensure_manifest_integrity.py:46] |
| `core__config__show_skipped_ids` | false | read by the by-reference validate and integrity operators, not used further in their code | [668 op/validate_manifest_schema_by_reference.py:53-55; 668 op/ensure_manifest_integrity_by_reference.py:52-54] |
| `kind_authority` | `osdu` | authority of the by-reference side-effect files | [668 op/base_osdu_operator_by_reference.py:193-195] |
| `core__config__manifest_payload_limit` | 12000 (KB) | EDS's inline versus by-reference threshold | section 3.3 |
| `OSDU_STORAGE_BATCH_SIZE` (CSV parser property) | 10 or 50 | CSV Storage batch size | section 3.5 |

### 3.13 Route parameters per workflow (inference)

One generic route type can drive all of these through trigger, poll and read-back, provided it is parameterised per
workflow and has three capabilities that a single "files, workflow, poll, read" step lacks:

1. **Stages.** `Energyml_Converter` and `Enyparser_Translation` only translate; records are created by a second run, of
   `Osdu_ingest_by_reference`, fed with the first run's manifest id (sections 3.6 and 3.8). The route needs an ordered
   list of workflow stages in which an output of one stage becomes a placeholder in the next stage's payload.
2. **Pluggable result discovery.** The Workflow contract never returns outputs (section 5.1), so each workflow declares
   how its results are found (section 5.2): by known ids, by artefact, by search, or by Airflow XCom where the
   deployment exposes Airflow's API.
3. **Side-effect capture.** Several DAGs mint records the caller never asked for (section 5.3). The project rule that
   every id a run creates is logged and removed cannot be met for these without a capture strategy per workflow, and
   for the by-reference report files not at all without access to Airflow's task logs.

Other differences the parameters carry:

- **Prerequisite protocol.** Dataset-service files (Energistics, enyparser, manifests by reference), a File-service
  metadata record with `ExtensionProperties.FileContentsDetails.TargetKind` (CSV, which always reads
  `/files/{id}/metadata`), or Seismic DMS content plus records already ingested (the SEG-Y conversions).
- **Key spelling and types.** `Payload["data-partition-id"]`, `data_partition_id` and `dataPartitionId`; `manifest`
  is a string by reference and an object or array inline; `dataset_h5` must be present even when empty. The payload
  templates are per workflow, typed, and always include `Payload {AppKey, data-partition-id}`.
- **Workflow names** are deployment configuration (section 1.4), checked per partition with the probe.
- **Status** is mapped from both vocabularies, and `finished` is not read as complete (section 6.3).
- **Timeouts** range from 60 minutes to 24 hours, and some DAGs set none (section 6.2).
- **Credentials in the payload** are secret references, resolved at send time and redacted (section 6.5).
- **Retries inside the DAG.** The CSV DAG retries once, so one trigger can store rows twice (section 3.5).

| Workflow | Name (per deployment) | Prerequisites | Payload template (placeholders in `{}`) | Result discovery | Side effects to capture | Poll timeout basis |
| --- | --- | --- | --- | --- | --- | --- |
| `Osdu_ingest` | `Osdu_ingest` | files uploaded and registered; schemas exist; referenced records indexed | `{Payload: {AppKey: {appKey}, data-partition-id: {partition}}, manifest: {manifestObject or manifestArray}}` | by known ids (XCom `record_ids` where Airflow is reachable) | none | 180 min DAG timeout |
| `Osdu_ingest_by_reference` | `Osdu_ingest_by_reference` (probe first) | the manifest file as `dataset--File.Generic`, with top-level `acl` and `legal` | `{Payload: {...}, acl: {acl}, legal: {legal}, manifest: "{manifestDatasetId}"}` | by known ids | 2 manifest copies (XCom), 2 report files (task log only) | 60 min |
| CSV | `csv_ingestion`, `csv-parser` or `csv-parser-pipeline` | File-service metadata record with `TargetKind`; the target schema, with natural keys for known ids | `{id: "{fileMetadataId}", dataPartitionId: "{partition}", data_service_to_use: "file", Payload: {...}}` (`Payload` optional), optional `runId` | by known ids (natural keys) or by search on the kind | rows stored twice on the DAG's retry | none set; one retry after 5 min |
| `Energyml_Converter` | `Energyml_Converter` | EPC, XML and HDF5 as `dataset--File.Generic` | `{Payload: {...}, data_partition_id: "{partition}", dataset_xml: [{xmlIds}], dataset_h5: [{h5Ids}], acl, legal, tags_every_entity_keys: [{runTag}], ...overlay}` | stage 1 by Airflow XCom (manifest id); stage 2 by search on the run tag | part, mini-HDF5, GeoJSON, manifest and report datasets | 60 min (operators) or 24 h (pods) |
| then `Osdu_ingest_by_reference` | as above | the stage 1 output | `{Payload: {...}, manifest: "{stage1.manifestId}"}` | by search (tag) | as by reference | 60 min |
| `Energyml_Delivery` | `Energyml_Delivery` | ingested Energistics records | `{Payload: {...}, ids: {searchResult}, name: "{name}"}` | by Airflow XCom `return_value` | EPC, HDF5 and report datasets | 60 min |
| `Enyparser_Translation` | `Enyparser_Translation` | sources and HDF5 as dataset records, with file names | `{Payload: {...}, source_dataset_ids: [{ids}] or epc_dataset_id: "{id}", h5_dataset_id: "{h5Id}", manifest_dataset_id: "{chosenId}", work_product_name: "{name}", enyparserConfig: {acl, legal, ...}}` | stage 1: the chosen id; stage 2: the ids in the manifest | new dataset registrations per run | none set |
| then `Osdu_ingest_by_reference` | as above | the stage 1 output | `{Payload: {...}, manifest: "{chosenId}"}` | by known ids | as by reference | 60 min |
| SEG-Y to VDS | `Segy_to_vds_conversion_sdms`, `segy-to-vds-conversion` or `openvds_import` | SEG-Y in Seismic DMS; `FileCollection.SEGY` and WorkProduct ingested | `{Payload: {...}, file_record_id: "{segyFileCollectionId}", work_product_id: "{workProductId}", id_token: "{secretRef}" (optional), segyimport_arguments: [...]}` | by artefact | the OpenVDS dataset in Seismic DMS | 24 h |
| SEG-Y to ZGY | `Segy_to_zgy_conversion`, `segy-to-zgy-conversion`, `sgy-to-zgy` or `sgy-to-zgy-pipeline` | as VDS, plus SeismicTraceData and SeismicBinGrid | `{Payload: {...}, data_partition_id: "{partition}", filecollection_segy_id: "{id}", work_product_id: "{id}", sd_svc_api_key: "{keyRef}", storage_svc_api_key: "{keyRef}", id_token or access_token: "{secretRef}"}` | by artefact | the ZGY file, index files, possibly a dangling record on failure | 24 h (in `default_args`) |
| SEG-Y to MDIO | `segy_to_mdio_conversion` | as VDS, with `osdu:wks:` kinds and `FileCollectionPath` | `{Payload: {...}, work_product_id: "{id}", filecollection_segy_id: "{id}", mdio_sd_path: "{sdPath}", lossless, compression_tolerance, grid_overrides, chunksize, client_id, client_secret, refresh_token, refresh_url: "{secretRef}" (all four or none)}` | by artefact | the MDIO dataset in Seismic DMS | none set |

## 4. Identities and versions

- **Run ids.** A trigger may set `runId` [WF:1094-1096]; the `manifest` route does, and handles a re-sent trigger
  through the 409 it then receives [MR:390-417]. EDS treats the Workflow `runId` as the Airflow DAG run id: it reads
  task states and XCom with `run_ids=[manifest_response.runId]` and `run_id=manifest_response.runId`
  [668 eds/eds_ingest/utilities/osdu_ingest_xcom_utility.py:42-46, :58-64]. The Energistics collection reads Airflow's
  API with the `runId` the trigger returned [1414 EP 04, EP 06]. The Workflow service implementation is not read.
- **Workflow names.** Deployment configuration, not constants (section 1.4). The route triggers the name registered
  with the Workflow service, and `registrationInstructions.dagName` maps it to the DAG [WF:1028-1049;
  1697 orchestrator/devops/azure/output_dag_folder.py:21-26].
- **Record ids the workflows create.**

| Workflow | Ids | Citation |
| --- | --- | --- |
| `Osdu_ingest`, by reference | as set in the manifest; surrogate keys are resolved during processing, inside `osdu-ingestion` | [1551 airflow/README.md:15] |
| by-reference side-effect files | minted by the Dataset service | section 3.3 |
| CSV | the natural-key formula, or generated by Storage | section 3.5 |
| `Energyml_Converter` | the manifest id is minted; translated ids come from `commons-parser` (not read) | section 3.6 |
| `Enyparser_Translation` | the manifest id as chosen; translated ids are derived | section 3.8; [1697 docs/extending.md:257-260] |
| SEG-Y to VDS | built by `OpenVDSMetadata` (not read) | section 3.9 |
| SEG-Y to ZGY | the OpenZGY record is minted by the converter | section 3.10 |
| SEG-Y to MDIO | the MDIO record by surrogate key, resolved at ingestion | section 3.11 |

- **Dataset ids.** The Dataset service mints an id on each registration that sends none [1697 docs/extending.md:247-251]:
  registering the same bytes again creates another record (enyparser, the Energistics uploads, the by-reference
  side-effect files).
- **Record versions.** The ZGY converter takes latest-version references [D460:204] and writes a new SeismicTraceData
  version [D460:119-126]; the MDIO and VDS workflows update work-product components through manifest ingestion, which
  gives those records new versions (inference). The by-reference side-effect records and the Energistics collection's
  registrations send the fixed `version` 1614105463059152 [668 op/base_osdu_operator_by_reference.py:208; 1414 EP 03].
  The collections strip a trailing `:` from version-less references before reading them [1414 EP 06; 1551 E2E 5.3]. The
  `manifest` route counts a record as written only when its version moved during the run [MR:22-24].
- **Code versions.** Deployments install released packages that may differ from the code read here (Sources).

## 5. Reads, verification and deletes

### 5.1 What the Workflow contract returns

No operation returns what a run produced. The run response carries status and timestamps only [WF:982-1010],
`latestInfo` answers `type: object` [WF:780-785], and none of the sources reads `latestInfo`. The enyparser DAG says
the same: "the Workflow service reports a run's *status*, never what it produced" [1697 dag:55-58].

### 5.2 Finding the results

| Strategy | Calls | Used for |
| --- | --- | --- |
| by known ids | Storage `POST /query/records` [ST: getRecords, 763-840] with at most 100 ids per request [ST:2033-2048], answering `MultiRecordInfo {records, invalidRecords, retryRecords}` [ST:2049-2063]; or `GET /records/{id}` [ST: getLatestRecordVersion, 1008-1097] | `Osdu_ingest` and by reference with ids set in the manifest; CSV with natural keys; enyparser (the chosen manifest id, then the ids in the manifest). The `manifest` route reads back this way [MR:17-18, :47-48] |
| by artefact | read the work-product components, select the `data.Artefacts[]` entry by `RoleID` `...ArtefactRole:ConvertedContent:` and `ResourceKind`, then read the artefact record (same Storage calls) | VDS [469 QS:228], ZGY [460 testing:155-181], MDIO [1551 E2E 5.3, 5.4] |
| by search | Search `POST /query` [SE: queryRecords, 112-187] with the required `kind` and a `query` [SE:627-716]; indexing is asynchronous [1697 ENY 90.4] | the Energistics tag search [1414 EP 08]; CSV without natural keys [202 IBM "Search Records For Schema"] |
| by kind listing | Storage `GET /query/records?kind=` [ST: getAllRecords_1, 670-762], role `service.storage.admin` | [202 IBM "Get all records for a kind"] |
| by Airflow XCom | Airflow's REST API, outside the Workflow contract | the only way to learn the `Energyml_Converter` manifest id and the `Energyml_Delivery` outputs [1414 EP 06, EP 06b] |
| dataset content | Dataset `POST /retrievalInstructions` (at most 20 ids) or `GET /retrievalInstructions?id=` (section 2.2.1) | the enyparser manifest [1697 ENY 30.1, 30.2] |

XCom keys the DAGs write [668 op/base_osdu_operator.py:35-38, :83-97; 668 op/update_status.py:189-195]:

| Key | Written by | Content |
| --- | --- | --- |
| `record_ids` | `process_single_manifest_file_task` and `process_manifest_task_N` (manifest DAGs, VDS, MDIO); `energyml_manifest_creation` (Energistics operator variant) | the ids saved |
| `saved_record_ids` | the final status task | a map from task id to `record_ids` |
| `skipped_ids` | validate, integrity and process tasks; the final status task | the skipped entities |
| `errors` | any operator task that failed; the final status task (the by-reference one always) [668 op/update_status_by_reference.py:184] | error details |
| `return_value` | the by-reference validate and integrity tasks (manifest file ids); pod tasks (`/airflow/xcom/return.json`); `epc_h5_delivery` | as in section 3 |
| `manifest_ref_ids` | `check_payload_type` (by reference) | `[manifest id]` |

### 5.3 Side effects to capture

| Workflow | Created besides the delivered records | Where the ids are |
| --- | --- | --- |
| by reference | 2 manifest copies and 2 report files per run | XCom `return_value`; the task log only, for the reports |
| `Energyml_Converter` | per-part XML datasets, mini-HDF5 datasets, GeoJSON datasets, the manifest, 2 report files | not returned; the manifest id only through XCom |
| `Energyml_Delivery` | EPC and HDF5 datasets, 2 report files | XCom `return_value` |
| `Enyparser_Translation` | the manifest (at the chosen id), and a new registration of every uploaded file and GeoJSON per run | the manifest links to the new registrations [1697 README:154-158] |
| SEG-Y to VDS | the OpenVDS dataset in Seismic DMS | derived from the SEG-Y path inside `osdu-ingestion` |
| SEG-Y to ZGY | the ZGY file and index files in Seismic DMS; on failure a stale ZGY file or a dangling OpenZGY record | the OpenZGY record's `FileSourceInfos` |
| SEG-Y to MDIO | the MDIO dataset at `mdio_sd_path`, deleted by the container on late failures | `mdio_sd_path` |
| CSV | rows stored twice when the DAG retries | none |

### 5.4 Deletes

- Records OSDU Delivery created are removed with Storage `POST /records/{id}:delete` [ST: deleteRecord, 509-584],
  which answers 204. `DELETE /records/{id}` is a purge [ST: purgeRecord, 1098-1167] and is not used. The MDIO e2e
  suite cleans up with `DELETE /records/{id}` [1551 E2E 7.1, 7.2, 7.3]; that pattern is not copied.
- Seismic DMS datasets (VDS, ZGY, MDIO, index files) are not Storage records; the MDIO e2e suite deletes its dataset
  through Seismic DMS [1551 E2E 7.4]. Their removal belongs to the Seismic DDMS route (`osdu/specs/seismic-ddms`).
- A delivery does not delete workflow definitions: `DELETE /v1/workflow/{workflow_name}` and its system variant need
  `service.workflow.admin` [WF:687, :901].
- The by-reference report files can be attributed to a run only through its task log (section 3.3).

## 6. Limits and errors

### 6.1 Sizes

- The Workflow contract declares no limit (section 1).
- EDS switches to a manifest by reference, or to chunked `Osdu_ingest` runs, above 12000 KB measured over the whole
  trigger request (section 3.3); enyparser keeps megabyte manifests out of XCom by publishing them (section 3.8).
- A Dataset registration takes at most 20 records, and a retrieval at most 20 ids [DS:789-800, :948-959]; a Storage
  read-back takes at most 100 ids [ST:2033-2048].

### 6.2 Timeouts and retries

| Workflow | DAG run timeout | Retries | Citation |
| --- | --- | --- | --- |
| `Osdu_ingest` | 180 min | 0 | [147 r3:59-64, :92] |
| `Osdu_ingest_by_reference` | 60 min | 0 | [147 ref:51-56, :88] |
| CSV | none set | 1, after 5 min | [202 dag:16-25, :66-70] |
| `Energyml_Converter` (operators) | 60 min | 0 | [1414 conv:32-37, :48] |
| `Energyml_Converter` (pods) | 24 h, set in `default_args`, with a 24 h task `execution_timeout` | 0 | [1414 k8s:29-37] |
| `Energyml_Delivery` | 60 min | 0 | [1414 deliv:30-35, :46] |
| `Enyparser_Translation` | none set; pod start timeout 600 s | 0 | [1697 dag:169-185, :235-244, :260] |
| SEG-Y to VDS | 24 h | 0 | [469 dag:77-82, :204] |
| SEG-Y to ZGY | 24 h, set in `default_args`, with a 24 h task `execution_timeout` | 0 | [460 dag:19-27] |
| SEG-Y to MDIO | none set; pod start timeout 300 s | 0 | [1551 dag:79-89, :112-117, :135] |

A run can stay `running` in the Workflow service after Airflow finished it (section 2.3), so the route keeps its own
poll timeout per workflow, and a later try resumes the same run, as the `manifest` route does [MR:452-484].

### 6.3 When `finished` is not complete

- `Osdu_ingest` and by reference: partial skips and skipped batch items (section 3.2).
- CSV: row and batch failures (section 3.5).
- `Energyml_Converter`: the pod's `errors` list is not inspected (section 3.6).
- `Enyparser_Translation`: diagnostics do not fail the run (section 3.8).

Every workflow therefore needs read-back verification against expected ids or counts (inference). The `manifest`
route already settles each record on its own read-back and sends a record the run did not write into a new run
[MR:12-26, :486-491].

### 6.4 Errors

- Workflow errors carry `AppError {code, reason, message}` [WF:1011-1027]; a 409 on a trigger has the shared text "A
  Workflow with the given name already exists." [WF:503-508].
- Failure detail lives in Airflow (XCom `errors` and `skipped_ids`, task logs), on the CSV parser's status topic, or in
  pod logs; none of it is reachable through the Workflow contract.
- The `errors` XCom is not masked (section 3.2).

### 6.5 Credentials in payloads and pods

- `id_token` (VDS, ZGY), `access_token` (ZGY on Azure and IBM), and `client_id`, `client_secret` and `refresh_token`
  (MDIO) travel inside `executionContext` (sections 3.9 to 3.11), and so do the ZGY API keys. Under this repository's
  secrets rule they are `${keyvault:...}` or `${env:...}` references, resolved only when the request is sent, and the
  stored or displayed payload is redacted.
- The DAGs put the caller's token where it can be seen: the CSV DAG in the pod's JSON argument [202 dag:46-53, :80];
  the MDIO DAG as `--auth` [1551 dag:157]; the VDS DAG in the `SEGYImport` arguments (`--url-connection` and
  `--input-connection` with `sdtoken=<token>`) and in the `pre_conversion` task's returned XCom, masked in logs with
  `mask_secret` [469 dag:131-142, :160-164]; the ZGY DAG in pod environment variables [460 dag:62-63]. The enyparser
  DAG keeps it in the environment, not in the arguments [1697 dag:266-273]. The ZGY converter prints tokens when
  `SEGYTOZGY_INSECURE_PRINT_TOKEN` is set [D460:136-138].
- The status operators replace `authToken` with `***` before logging the conf [668 op/update_status.py:140-142].

## 7. What differs between the contract and the code

1. **Status casing.** `WorkflowRunResponse.status` is upper-case [WF:999-1007], yet the upstream documentation and
   tests read lower-case values from trigger and get: `submitted` on trigger [D469:121-130; 460 testing:141-151;
   1414 EP 04; 1551 E2E 4.3], and `submitted`, `queued`, `running`, `finished` and `failed` on polls [1414 EP 05;
   1551 E2E 3.2, 4.4; 1697 ENY 20.2, 40.2]. EDS models the lower-case set after the Workflow service's
   `WorkflowStatusType` [668 eds/models/workflow_framework/workflow_response_status.py:15-27]. The `manifest` route
   upper-cases and knows `FINISHED` [MR:50-59].
2. **Context value types.** `executionContext` values are declared `type: object` [WF:1097-1100]; the DAGs read
   strings (`manifest` by reference, `id`, `dataPartitionId`, record ids) and arrays (`manifest` batches,
   `dataset_xml`, `dataset_h5`).
3. **Required fields.** `TriggerWorkflowRequest` requires nothing [WF:1091-1101]; every DAG reads `execution_context`
   with `[]` (section 1.3).
4. **Listings.** `GET /v1/workflow` and `GET .../workflowRun` declare a single `WorkflowMetadata` or `WorkflowRun`
   object, not an array [WF:229-234, :392-397]; the run listing's `params` is a required object-typed query parameter
   [WF:378-384].
5. **`latestInfo`** is tagged `run-details-api`, which the top-level tag list does not declare [WF:29-39, :756-757],
   and its response is untyped [WF:780-785].
6. **Update role.** `PUT .../workflowRun/{runId}` needs only `service.workflow.viewer` [WF:127].
7. **409.** The contract gives every operation's 409 the text "A Workflow with the given name already exists." (for
   example [WF:97-102, :503-508]). The `manifest` route treats a 409 on a re-sent trigger with its own `runId` as an
   accepted run [MR:390-394, :417]. The CSV parser's Azure registration script reads `conflictId` from a 409 body
   [202 deployments/scripts/azure/dag_bootstrap/register_dag.py:108-110], a field `AppError` does not have
   [WF:1011-1027].
8. **Dataset records.** The Dataset contract's `Record` requires `id` [DS:831-901]; the upstream callers register
   without one and the service mints it [1697 docs/extending.md:247-251]. `storageLocation` and `retrievalProperties`
   are untyped [DS:939-947, :960-970], while the callers rely on `signedUrl`, `fileSource`, `unsignedUrl` and
   `signedUploadFileName` (section 2.2.1). The enyparser collection also accepts a `delivery` array in retrieval
   responses [1697 ENY 30.1], which the contract does not define [DS:971-977].
9. **CSV ids.** Storage record ids must match `^[\w\-\.]+:[\w\-\.]+:[\w\-\.\:\%]+$` [ST:1709-1779]; the CSV parser's
   natural-key suffix is standard Base64 [202 core/handler/handlers/IdHandler.java:57-58], which can contain `+` and
   `/` (inference: such ids fall outside the pattern).
10. **Documentation against code.**
    - `D147` lists `core__service__file__host` and marks `core__service__workflow__url` as deprecated
      [D147:114-119]. The non-reference process operator reads `core__service__file__url`
      [668 op/process_manifest_r3.py:81-83], the by-reference one reads `core__service__file__host`
      [668 op/process_manifest_r3_by_reference.py:85], and both status operators and EDS read
      `core__service__workflow__url` [668 op/update_status.py:165-168; 668 eds/eds_ingest/utilities/airflow_utility.py:102-113];
      the enyparser README says status reporting fails without it [1697 README:281].
    - The token masking in `_add_error_to_xcom` is computed and not applied [668 op/base_osdu_operator.py:86-94].
    - `energistics-parser-lib`'s README writes `legal` with `tags` and `country` [1363 README.md:28-35]; the reference
      request and the by-reference operator use `legaltags`, `otherRelevantDataCountries` and `status` [1414 EP 04;
      668 op/base_osdu_operator_by_reference.py:117-121].
    - The VDS README's trigger example is the older AWS-style context [D469:102-130]. The ZGY testing guide's v2
      context has no `Payload` [460 testing:123-139], and its curl examples post to `/workflow/{workflow-id}` without
      `/workflowRun` [460 testing:103, :126].
    - The VDS quickstart names `data.DatasetsProperties.FileCollectionPath` [469 QS:192]; the MDIO code reads
      `data.DatasetProperties.FileCollectionPath` [1551 app/osdu_metainformation.py:299-301].
    - The enyparser collection describes an array `manifest` as "a batch of either" [1697 ENY group 40 description];
      the by-reference DAG does not support it (section 3.3).

## 8. What is still open

1. Whether the target deployments expose Airflow's REST API to OSDU Delivery. Without it, the `Energyml_Converter`
   manifest id and the `Energyml_Delivery` outputs cannot be found, and by-reference report files cannot be
   attributed.
2. Whether `Osdu_ingest_by_reference` is registered on each target partition. GC deploys it; the Azure and IBM material
   in project 147 does not show it registered. A probe per partition answers it.
3. What `latestInfo` returns on the target deployments.
4. How the Workflow service reconciles run status with Airflow, including Airflow-side timeouts, which decides whether
   a run can stay `running`; which `dag_run.conf` keys it writes (`run_id`, `runId`, `workflow_name`, `authToken`,
   `correlation_id`); and whether the `runId` of a trigger is always the Airflow run id.
5. Behaviour inside `osdu-ingestion`, `osdu-api`, `commons-parser` and `energyml-delivery`: manifest processing,
   surrogate-key resolution, file checks, `Context.populate`, the HTTP call behind `update_workflow_status`,
   `OpenVDSMetadata`, and the kind, ACL and legal that `put_to_dataset_service` gives the Energistics datasets.
6. The library versions each target deployment installs (Sources).
7. The `Energyml_Converter` operator variant constructs its translation operator with `previous_task_id=None` and
   pulls `return_value` with it [1414 conv:61-69; 1363 op/energistics_translation.py:81-83]; how Airflow resolves that
   pull decides whether the variant works.
8. Whether a `dagrun_timeout` placed in `default_args` bounds the run (Energistics pod variant, ZGY).
9. How missing context keys render in the templated DAGs on the target Airflow version (the ZGY API keys; MDIO's
   optional keys use `default('None')` [1551 dag:162-165]).
10. On Airflow 3, the MDIO DAG states that a `KubernetesPodOperator`'s `post_execute` receives `result=None` and that
    raising there blocks the `return_value` XCom push [1551 dag:96-98]; the enyparser DAG's result check runs as
    `post_execute` and pulls that XCom itself [1697 dag:188-214, :317]. Its behaviour on Airflow 3 is not verified.
11. The CSV parser's Base64 ids against the Storage id pattern (section 7), and whether the descriptor's `FileSource` is
    validated at all (section 3.5), need a live check.
