# Interfaces design: one flow per source, every OSDU type through one generic pipeline

Status: accepted, and built stage by stage ([osdu-coverage-plan.md](osdu-coverage-plan.md)). Built so far: the
document model with both forms, the ledger identity per interface and `ledger:` adoption, the read model of sources
and interfaces, route resolution from what an interface declares with its refusals, the order from the schemas'
relationships and `after:` with its waves, the source runtime with its preflight, stop rules and outcome, and the API
and CLI selection and explanation of interfaces (sections 3, 4, 6, 8 and 10, and the routes of section 5.2 that the
four existing protocols deliver). The rest of this document is the design
the later stages build. What shipped is documented in [../osdu/docs/documents.md](../osdu/docs/documents.md#a-source-with-interfaces)
and [../osdu/docs/operations.md](../osdu/docs/operations.md#running-a-source).

## 1. Why

Today a delivery flow delivers one OSDU kind through one protocol its author names. A source system that produces
fields, wells, wellbores, documents, trajectories and seismic needs one flow file per kind, each repeating the same
connection, target, credentials and schedule, each with a protocol chosen by hand, and nothing checks that the
protocol suits the kind. With the hundreds of kinds OSDU defines, that does not scale, and the protocol set carries
well log assumptions a generic system must not have.

This design keeps what works (the ledger, change detection, fan-out, leases, the failure handling per record) and
changes three things:

1. **One flow file per source.** The file lists the source's interfaces, one per OSDU type it delivers.
2. **The system decides how and when.** Each interface's route (how OSDU accepts it) and its place in the order are
   worked out from what OSDU itself publishes: the kind, its schema and the DDMS registry. No kind is named in code.
3. **One run executes the whole source and tracks everything**, with clear rules for when a record, an interface or
   the whole run stops.

## 2. Principles

- **No kind is special.** The code never tests for a kind or an entity type. Well logs, wellbores and seismic are
  test data, not branches.
- **A new kind needs a mapping, not code.** A new kind of OSDU API (a DDMS with its own call pattern) needs one route
  type, once, and then serves every kind that API accepts.
- **OSDU behaviour comes from OSDU's own code and contracts, never from memory.** The core services are verified
  against the OpenAPI specifications in `D:\Projects\eq\src\osdu-csharp-client-main\openapi_specs`; every DDMS and
  ingestion workflow against the contracts pinned in `osdu/specs` (with the project, file and commit each was read
  at in `osdu/specs/sources.json`). An API without a pinned contract is not designed for until it has one.
- **Both forms are first-class.** A source file with many interfaces and a flow with a single interface run on the
  same engine, so a type that is better managed on its own stays on its own. There is one runtime, one ledger path
  and one set of views for both.
- **Any part can be delivered again on its own.** An interface, a record, or one part of a record (its record, its
  files, its DDMS bulk data) can be sent again without redoing the rest.
- **Traceability stays whole.** Every interface keeps its own ledger identity, so every record still answers which
  row it came from, what it became in OSDU and everything that happened to it.

## 3. The flow file

```yaml
flowType: delivery
name: petrel-project-x                        # the source; one pipeline in the catalog
parameters: { ... }                           # shared by every interface
schedule: { ... }

source:
  connection: ${env:PETREL_DB}                 # one ingestion database for the source
  systemColumns: { ... }                       # defaults for every interface
  incremental: { ... }
  work: ../.work/petrel-project-x

target:
  endpoint: ${env:OSDU_URL}
  auth: { ... }
  headers: { data-partition-id: opendes }
  ddms:                                       # optional: where DDMSs live when the registry does not say (section 5.3)
    wellbore: { root: /api/os-wellbore-ddms, shape: wellboreDdmsV3, entityTypes: { WellLog: welllogs, WellboreTrajectory: wellboretrajectories } }

render: { cacheVersion: current, parameters: { ... } }   # defaults for every interface
change: { ... }
reliability: { ... }

interfaces:
  fields:
    record: { object: Petrel.ing.Field, key: [field_name], primaryKey: RecId }
    mapping: Field@1.0.0
  wells:
    record: { object: Petrel.ing.Well, key: [uwi], primaryKey: RecId }
    mapping: Well@1.2.0
  wellbores:
    record: { object: Petrel.ing.Wellbore, key: [uwbi], primaryKey: RecId }
    datasets: { aliases: { object: Petrel.ing.WellboreAlias, join: { uwbi: uwbi } } }
    mapping: Wellbore@1.3.0
  documents:
    record: { object: Petrel.ing.Document, key: [doc_id], primaryKey: RecId }
    files: { root: ../data/documents, locationColumn: doc_folder, pattern: "*.pdf" }
    mapping: Document@1.0.0
  trajectories:
    record: { object: Petrel.ing.Trajectory, key: [uwbi, survey_id], primaryKey: RecId }
    bulk: { root: ../data/trajectories, locationColumn: survey_folder, pattern: "*.parquet" }
    mapping: WellboreTrajectory@1.1.0
  seismic:
    record: { object: Petrel.ing.Seismic, key: [survey, line], primaryKey: RecId }
    files: { root: ../data/seismic, locationColumn: segy_folder, pattern: "*.segy" }
    route: manifest
    mapping: SeismicTraceData@1.3.0
```

An interface states **what** it is:

| Key | Meaning |
| --- | --- |
| `record` | The interface's record table: `object`, `key` (the record identity), `primaryKey` (the identity column it is paged and fanned out by), `scope`. |
| `datasets` | Child tables joined to the record, as today. |
| `mapping` | The pinned mapping. Its template fixes the interface's kind. |
| `files` | Files the record carries as datasets, uploaded and registered before the record. |
| `bulk` | Tabular bulk data a DDMS stores for the record, written after the record. |
| `route` | Optional: the route, instead of the one the system chooses (section 5). `manifest` sends the interface's records through the ingestion workflow in batches. |
| `after` | Optional list of interfaces this one waits for, beside the ones the schemas imply (section 6). |
| `failWhen` | Optional thresholds that turn record failures into an interface failure (section 8). |
| `render`, `change`, `reliability` | Optional overrides of the source's defaults. |

The shared blocks are written once. The existing top-level `source.record`, `source.datasets`, `source.payloads`,
`render.mapping` and `target.protocol` form stays valid: it is read as a source with one interface (section 11).

## 4. Identity and tracking

- **One pipeline per file.** The catalog, the schedules, the run queue and lineage see one pipeline for the source.
- **One ledger identity per interface.** An interface's records, submissions, watermark, OSDU id claims and
  statistics are keyed by `FlowId.Of("<flow>/<interface>")`. The ledger schema does not change: `FlowId` is already a
  derived id, and one row producing records of two kinds lands under two identities, as one row read by two flows
  does today.
- **Adopting an existing ledger.** `ledger: <old flow name>` on an interface keeps an existing flow's identity, so
  consolidating today's single-kind flows into a source file loses no history and re-sends nothing.
- **One run, many submissions.** A run registers one submission per interface it plans, each with its own window,
  slices and batches. The run page lists the interfaces with their state and counts; the records, submissions and
  statistics views filter by interface and roll up to the source.
- **The run trace** carries `interface.started`, `interface.completed`, `interface.stopped` and
  `interface.skipped` beside the existing batch and record events.
- **Lineage** declares one write per interface (its kind in the target partition) and one read per record and child
  table, so the graph shows everything the source feeds.

## 5. Routes: how each interface is delivered

### 5.1 The facts, for any kind

1. **The kind.** `authority:source:group--Entity:version` names the group type (reference data, master data, work
   product, work product component, dataset) and the entity type. Parsed the same way for every kind.
2. **The schema.** The mapping's template carries OSDU's `x-osdu-relationship` annotations: which properties refer to
   which group and entity types, including the dataset group a record's `Datasets` refer to. The template code
   already reads them.
3. **The DDMS registry.** OSDU's Register service keeps DDMS registrations (openapi register v1), each with the entity
   types it serves and an OpenAPI document for each, read by id (`GET /api/register/v1/ddms/{id}`). Its lookup by type
   (`GET /ddms?type=`) takes only `^[A-Za-z0-9]{1,50}`, which cannot carry an entity type with its group
   (osdu/specs/core/INTEGRATION.md section 2.7), so a flow names the registration it means.

### 5.2 The decision

| The interface declares | And | The route |
| --- | --- | --- |
| no `files`, no `bulk` | | `storage`: the record through the Storage service |
| `files` | the schema refers to the dataset group | `file`: every file uploaded and registered (File service), then the record referring to its datasets |
| `files` and `route: dataset` | | `dataset`: the files stored and registered through the Dataset service; a record of a dataset kind is that dataset, any other record refers to one dataset holding its files (section 5.6) |
| `bulk` | a DDMS serves the entity type | `ddms`: the record through that DDMS, then the bulk data |
| `files` and `bulk` | both of the above | `fileAndDdms`: files registered, the record written through the DDMS referring to them, then the bulk data (section 5.7) |
| `route: manifest` | any of the above but `ddms` | `manifest`: files registered first, then the records in manifests through the ingestion workflow, inline or by reference (section 5.8) |
| `route: manifest` and `bulk` | | `manifestAndDdms`: files registered, the records by manifest, the run polled, then the bulk data through the DDMS (section 5.7) |
| `workflow` | | `workflow`: the record written, its inputs registered, a named workflow run in stages and what it created read back (section 5.9) |

`route:` overrides the choice; it names a route type (`storage`, `file`, `dataset`, `manifest`, `ddms`,
`fileAndDdms`, `manifestAndDdms`, `workflow`). A named route and a part only another route sends make the composed
route of the two (`route: manifest` with `bulk`, `route: file` with `bulk`, `route: ddms` with `files`). A route that
cannot deliver what the interface declares is refused when the flow is read (files on `storage`, bulk data on
`dataset` or `workflow`, a workflow on any other route). What needs no network is checked when the flow is read; the
DDMS lookup, the kind a route needs (a dataset kind for a dataset anchor) and the workflow names a partition registers
are checked in the run's preflight, on the node.

### 5.3 Finding a DDMS

A record goes to the collection of the DDMS serving its entity type, which its id names
(`{partition}:{entityType}:{key}`), so a verify, a read or a removal routes from the id alone. In order:

1. `target.ddms` in the flow: named DDMSs, each with its root under the endpoint, its route shape (section 5.10) and the
   collection each entity type is served under (the shape's own collections when it lists none). The collection is always
   stated, never derived: the Wellbore DDMS serves `WellLog` under `welllogs` and `PPFGDataset` under `ppfgdataset`,
   so no rule turns an entity type into its path. A DDMS named with `register: <id>` has what the flow leaves out read
   from its Register service registration when the protocol is built: the root from the one server its registered
   documents name, the collections from their retrieval operations (`x-ddms-retrieve-entity`), matched against the
   shape's known collections by path.
2. The Wellbore DDMS, whose nine collections OSDU Delivery knows (four that keep bulk data, five that hold records
   alone), under `protocolOptions.ddmsRoot`, or at the endpoint of a single-form flow that declares no DDMS.
3. Neither: the interface is refused in preflight, naming the entity type, the DDMSs the flow reaches and what they
   serve, and how to declare the one it needs. A type a DDMS named by registration may serve waits for the
   registration to be read, which the run's preflight does before it checks.

### 5.4 Route types in code

A route type is one call pattern, written once:

| Route type | Services (openapi) | Status |
| --- | --- | --- |
| `storage` | storage v2 | exists (`osduRecord`) |
| `file` | file v2, storage v2 | exists (`osduFile`) |
| `manifest` | file v2, workflow v1, storage v2 | exists (`osduManifest`) |
| `ddms` with shape `wellboreDdmsV3` | wellbore DDMS v3: `POST /{collection}`, `/{collection}/{id}/data`, `/{collection}/{id}/sessions` | built (stage 5, `osduWellLog`): every Wellbore DDMS collection, the four that keep bulk data (WellLog, WellboreTrajectory, PPFGDataset, WellPressureTestRawMeasurement) and the five that hold records alone, and any DDMS of the same shape |
| `ddms` with shape `wellDeliveryV1` | Well Delivery DDMS: `PUT /storage/v1/{type}`, `/storage/v1/{type}/{entityId}`; storage v2 for the copy it keeps | built (stage 7, section 5.10) |
| `ddms` with shape `rafsV2` | RAFS v2: `POST /v2/{collection}`, `/v2/{collection}/{id}/data[/{contentType}]`, the type catalogues; storage v2 for the datasets it registers | built (stage 7, section 5.10) |
| `ddms` with shape `productionTimeSeriesV1` | Production DDMS historian: ingestion `POST /production-values/{id}/timeseries`, query `GET /production-values/{id}/timeseries/{timeseriesId}/versions/{version}`; storage v2 for the ProductionValues record | built (stage 7, section 5.10) |
| `ddms` with shape `seismicStoreV3` | Seismic Store v3: `POST`, `GET`, `PATCH` and `DELETE /dataset/tenant/{t}/subproject/{s}/dataset/{name}`, `PUT .../lock` and `.../unlock`, `GET /utility/upload-connection-string`; the object store its credentials open (Azure Blob Storage, Google Cloud Storage, S3); storage v2 for the dataset record | built (stage 7, section 5.10) |
| `ddms` with shape `reservoirManagement` | Reservoir Management DDMS: `GET /ddms/{collection}/` (the list call that takes records in), `GET /ddms/{collection}/{id}`, `POST /ddms/{table}`, `GET /ddms/{table}/header-entity/{key}`, `DELETE /ddms/{table}/{key}`; storage v2 for the header record; search v2 for a record the service did not take in | built (stage 7, section 5.10) |
| `dataset` | dataset v1: `storageInstructions`, `registerDataset`, `retrievalInstructions`, `metadataRecord/{id}/softDelete`; storage v2 | built (stage 6, `osduDataset`), section 5.6 |
| `fileAndDdms`, `manifestAndDdms` | the above, composed | built (stage 6, `osduFileAndDdms`, `osduManifestAndDdms`), section 5.7 |
| `workflow` | workflow v1, dataset v1, storage v2, search v2; Airflow's REST API (v1 or v2) for the outputs only it returns | built (stage 6, `osduWorkflow`), section 5.9 |
| the other DDMSs | their briefs under `osdu/specs` | stage 7, section 5.10 |

The well log specific checks became checks of any tabular bulk upload: the row labels of each chunk read before a
session, the rows and columns read back after it, and the columns checked against what the record declares for its
collection (curve ids and widths, or trajectory station properties). The record rules the Wellbore DDMS applies to each
kind are checked before anything is sent, and a metadata update carries the bulk link the DDMS keeps on the record.

### 5.5 Parts: what a route sends beside the record

A route sends the record and up to three kinds of parts: files (the datasets a record refers to, a dataset record's own
content, or the inputs a workflow reads), bulk data (a DDMS keeps it for the record), and, on the workflow route, the
workflow run. A route that sends one payload set keeps the single payload the ledger has always kept: one folder, one
content hash. The composed routes and the workflow route send several, and the ledger keeps them without a schema
change:

- The plan resolves every part's folder and content hash from the record's row, as it resolves a single payload's. The
  record's payload hash is the hash of the parts' hashes, so a change to any part is a payload change, and the pending
  payload lists each part with its folder and hash.
- The record's target state keeps the hash each part was last delivered with (`payload.<set>`). A delivery sends a part
  when its hash differs from that one, so new bulk data does not upload the record's files again.
- A redelivery by part (`files`, `bulk`, `workflow`) leaves the parts it names on the record's delivered payload hash
  (`redeliver:files`). The next plan sends those parts whatever their hashes say, and the marker is replaced when the
  payload lands. `payload` and `all` send every part, as they always have.
- A workflow input the workflow accepts empty (`optional: true`) may name no folder, and is then a part without files.

### 5.6 The dataset route

The Dataset service (osdu/specs/core/INTEGRATION.md sections 2.5 and 3.3) stores a file, or a collection of files, for
any dataset kind a DMS serves, and keeps the id its caller gives, where the File service mints one:

- **A record of a dataset kind is the dataset.** Its files go where `storageInstructions` says for its entity type, and
  the record is registered with `registerDataset` under its own id, with `DatasetProperties` pointing at them:
  `FileSourceInfo` for a `dataset--File.*` kind, which holds one file, and `FileCollectionPath` with a `FileSourceInfos`
  entry per file for a `dataset--FileCollection.*` kind. Up to 20 records go in one registration.
- **Any other record refers to one dataset** of `protocolOptions.datasetKind` holding its files, registered under an id
  derived from the record's (`{partition}:{datasetEntityType}:{key}-files`), and the record is written through storage
  with its dataset list naming it. The id is stable, so new files land on the same dataset.
- **A change to the record alone** is written through storage with the `DatasetProperties` OSDU holds: a registration
  copies the staging area again, and the staged files may be gone.
- **Verify** reads storage; the delivery itself checks that `retrievalInstructions` lists every dataset it registered.
- **Removal:** the reversible scope is the Dataset service's `softDelete` for a dataset record, which its `undelete`
  restores, and storage's reversible delete for a record that refers to a dataset, which leaves the dataset so OSDU can
  restore the record whole; the history purge is storage's; everything is storage's purge of the record and of the
  dataset a record of another kind refers to. The files a registration copied stay in the platform's storage, which no
  public operation of the Dataset service deletes.
- **Uploads** go the way the provider that signed the location takes them: a single file with one `PUT` to its signed
  URL; a collection's files created, appended and flushed under Azure's Data Lake directory, posted with the POST policy
  of MinIO or S3, or put under Google Cloud Storage's folder with the token scoped to it. A location with temporary
  credentials for an endpoint it does not name (IBM) holds the record (osdu/specs/core/INTEGRATION.md section 2.5.1).
  The signed location is never logged or kept, so a retry asks for a new one unless every upload of the earlier try
  completed.

### 5.7 Composed routes

- **`fileAndDdms`:** each record's files are uploaded and registered through the File service; the record is written
  through the collection of its DDMS with its dataset list pointing at them; then its bulk data. The DDMS's rules and the
  bulk data's chunks are checked before the first file is uploaded, and every part resumes on its own.
- **`manifestAndDdms`:** a batch's records go through the ingestion workflow as the manifest route sends them, the run
  is settled by reading each record back, and then each written record's bulk data goes through its DDMS. Ingestion
  writes through storage, past the DDMS, so a record that already holds bulk data carries the DDMS's bulk link into the
  manifest; without it the record would lose the link to its bulk data. A record whose bulk data alone changed is not
  sent through the workflow again.
- **Content types.** The bulk data goes as `payloadContentType`; the files go as `filesContentType`, or
  `application/octet-stream` when the flow names none, so a LAS or SEG-Y file is not stored as parquet.

### 5.8 Manifests by reference

`Osdu_ingest_by_reference` takes the id of a `dataset--File.Generic` holding the manifest instead of the manifest itself
(osdu/specs/workflows/INTEGRATION.md section 3.3):

- `protocolOptions.manifestByReference` is `never` (the default), `always`, or `auto`: by reference when the trigger
  request is above `manifestInlineLimitKb` (12000, the limit External Data Services applies, measured over the whole
  request).
- The manifest is stored through the Dataset service as a `dataset--File.Generic` under an id derived from the run id,
  with the records' `acl` and `legal` at its top level, which the workflow copies onto the files it writes. The workflow
  (`byReferenceWorkflowName`, probed first, since not every deployment registers it) is triggered with
  `{Payload, acl, legal, manifest: "<id>"}`, the run polled, and every record read back as for an inline manifest.
- `auto` on a partition without the workflow splits the batch into manifests under the limit.
- The manifest file is a transport: it is removed reversibly once the run has settled. The copies and report files the
  workflow writes itself can be named only from Airflow's task logs, so they are not removed; the run id is on every
  record's attempt.

### 5.9 The workflow route

One route type drives every ingestion workflow (osdu/specs/workflows/INTEGRATION.md section 3.13):

```yaml
interfaces:
  csvWells:
    record: { object: Src.ing.CsvFile, key: [file_id], primaryKey: RecId }
    files: { root: ../data/csv, locationColumn: folder, pattern: "*.csv", hashColumn: content_hash }
    mapping: CsvDescriptor@1.0.0                 # dataset--File.Generic with its FileContentsDetails
    workflow:
      anchor: dataset                            # the record is registered with its files through the Dataset service
      anchorTag: osduDeliveryAnchor              # written into the record's tags, which the parser copies onto every row
      stages:
        - workflow: csv_ingestion                # the name this deployment registers it under
          context:
            id: "{record:id}"
            dataPartitionId: "{partition}"
            data_service_to_use: file
      results:
        search: { kind: "{record:data.ExtensionProperties.FileContentsDetails.TargetKind}", query: "tags.osduDeliveryAnchor:\"{anchorTag}\"" }
        minimum: 1
```

- **The anchor** is the interface's own record, written first: registered with its files through the Dataset service
  (`anchor: dataset`, the default when the interface declares files), or written through storage (`anchor: storage`)
  when the workflow starts from a record, as the SEG-Y conversions do.
- **Inputs** (`inputs.<name>`, each a payload set with a `datasetKind` and `optional`) are registered through the Dataset
  service under ids derived from the anchor's, one dataset per file, or one collection.
- **Stages** run in order; each names the workflow as the deployment registers it, the known workflow whose contract
  its context follows (`contract:`, when the name is not one of the contract's names), its context, its timeout and its
  outputs. A workflow that only translates (`Energyml_Converter`, `Enyparser_Translation`) is followed by a stage that
  ingests its manifest.
- **Templates.** A context's strings hold placeholders: `{partition}`, `{appKey}`, `{runId}`, `{anchorTag}`,
  `{record:path}` (a value of the anchor, `data.Parameters[Title=work_product_id].DataObjectParameter` selecting by a
  property), `{input:name}`, `{dataset:suffix}` (a `dataset--File.Generic` id derived from the anchor's),
  `{stage:n.output}` and `{secret:name}`; modifiers `|id`, `|ref`, `|list`, `|first`, `|json`. A string that is one
  placeholder takes the value's shape. `Payload {AppKey, data-partition-id}` is added when the context leaves it out and
  the workflow reads it.
- **Contracts.** Every workflow the briefs cover has a payload contract (`WorkflowCatalog`): the keys it reads, their
  shapes, the ones it requires, the credentials it takes, and its run timeout. The template is checked against it when
  the flow is read, and the filled context before every trigger; a context the workflow would fail on is never sent.
  The Workflow service's test DAG (`manifest_ingestion`) is refused as a target.
- **Outputs** are either known before the run (a template: the manifest id enyparser is told to write) or read from an
  XCom entry of the run: through the Workflow service's `latestInfo` for the run's latest task, or from Airflow's REST
  API (`target.airflow`, Airflow 2 or 3) for any task. On Airflow 3, `latestInfo` may give a list or a map as null, so
  an output that is one is read from Airflow (osdu/specs/workflows/INTEGRATION.md section 5.2.1).
- **Results** are found one way: `anchor` (the run writes the record), `ids` (a template), `artefact` (the anchor's
  `data.Artefacts` of a role and kind, as the conversions add them), `search` (a kind and a query), `manifest` (the ids
  in a manifest dataset) or `xcom`. They are read back from storage; fewer than `minimum` fails the record for the try,
  and the next try looks again without running the workflow again. The ids are kept on the record (up to `keep`), with
  their count, and a removal takes them with the anchor (`remove`, default true); a search is repeated to find them all.
- **When a run starts** (`runWhen`): `changed` (the delivery writes the record or an input), `created` (the record is
  new), or `requested` (only a redelivery of `workflow` starts one).
- **Resuming.** A registration lands on the same ids; a run's id is recorded before its trigger is sent, so a retry
  sends the same id and a 409 says the service already has the run; a finished run's outputs are kept. A failed run
  fails the record, and the next try triggers a new one.
- **Credentials** in a context are `{secret:name}` placeholders over `secrets.<name>` references, resolved only for the
  trigger request; the step, the log and every message keep the context with `***` in their place.
- **Preflight** asks the Workflow service for every workflow the route runs, so a name the partition does not register
  stops the run before anything is sent.

### 5.10 The other DDMSs

Every DDMS in OSDU is reached by one call pattern each, built from its brief (`osdu/specs/<service>/INTEGRATION.md`).
Where a DDMS takes an OSDU record and keeps data of its own for it, the pattern is a shape of the `ddms` route: the
record's entity type finds the DDMS under `target.ddms`, and the shape says how the record and its data are checked,
written, read back, verified and removed. The composed routes then work with it as they do with the Wellbore DDMS (the
link a DDMS keeps on a record is the shape's to carry). Where a DDMS's unit of delivery is not an OSDU record, the
pattern is a route type of its own.

| Service | Pattern | What the record is, and what goes beside it |
| --- | --- | --- |
| Well Delivery DDMS | shape `wellDeliveryV1` | The entity, one per write, under a version the route records before the write. References to entities the DDMS holds are sent with their versions, since its index takes no other; content redelivered goes back under the version other entities cite (not on IBM). The copy the deployment keeps in Storage (`mirror`) is removed with the entity. Records alone. |
| Rock and Fluid Sample DDMS | shape `rafsV2` | The record, in an array typed exactly as RAFS requires; its content tables as the `bulk` part, one JSON or parquet file per content type, named after the type (and the content schema version when it is not the flow's default), each checked against the service's catalogue before anything is written. The content datasets RAFS registers are kept on the record and removed with it. |
| Seismic DDMS (Seismic Store v3) | shape `seismicStoreV3` | A `dataset--FileCollection.*` record, registered as the dataset's `seismicmeta` under a lock id the record keeps for every delivery, recorded before the call; its files as the `bulk` part, uploaded with the credentials the service issues (a SAS, a Google token, or an S3 key triple for a configured endpoint) as its clients read them (on Azure a file cut into blobs `0..N-1`, elsewhere one object `0`, several files under their names), resumed past the objects that landed, then the dataset closed with its file metadata. Tenant, subproject and folder are the DDMS's settings. The record scope leaves the dataset, which has no reversible delete; removing everything deletes it first, except on gc. The work product component that refers to the dataset is an interface of its own on the storage route. |
| Production time series (historian) | shape `productionTimeSeriesV1` | The `work-product-component--ProductionValues` record, written through Storage with its link to its points; its points as the `bulk` part (a wide parquet table or the service's own JSON body), checked against the series the record defines and their kinds, and sent in requests under the body limit, each a step recording its body's hash and the version each series was accepted under, so a later try sends only what no try recorded; each accepted version read back through the query service until it serves the points. Points cannot be deleted; a removal takes the record through Storage and says so. |
| Reservoir Management DDMS | shape `reservoirManagement` | The header record, written through Storage (never through the service's `PUT`, which writes records without their ids, or its `DELETE`, which purges); the service's list call takes it into its database, asked until Search serves it; the child rows as the `bulk` part, a JSON tree of the tables below the collection, posted one per call with the keys the service returns fed to the rows below, recorded every 50 rows and found among the service's rows when a try failed after posting, and kept on the record so a redelivery replaces them and removing everything deletes them. |
| External Data Services | the `storage` route (or `manifest`) and the `workflow` route | Connected source registry entries, data jobs and proxy datasets are records, checked for what eds-dms and the EDS workflows need before they are sent (`target.eds`), and held with every rule they break; the run state EDS writes on a job is carried into every rewrite, and a version EDS writes is not taken for drift; an EDS fetch is a workflow the workflow route runs. |
| Production DDMS (DSPDM) | route type `dspdm` | A business object row, not an OSDU record: found again by a unique key of its business object through `/common`, saved with `POST /save` (with the primary key DSPDM gave it once it has one), that key kept in the record's target state, read back, verified and deleted by it. |
| Reservoir DDMS (Open ETP server) | route type `etp` | A dataspace and the RESQML objects and arrays of an EPC package, written over ETP 1.2 in explicit transactions; the dataspace's Storage record is the server's, whose id the ledger keeps. |

Status: the Well Delivery, RAFS, Seismic Store, production historian and Reservoir Management shapes, and the External
Data Services checks, are built; the rest of the table is built in the order it is listed, each with its fake service, its
contract checks and its documentation.

## 6. Order

- **Dependencies from the schemas.** Interface A waits for interface B when A's mapping fills a property whose
  relationship targets B's kind, and B is in the same source. A mapping that does not fill the property creates no
  dependency. A relationship that names only a group (`Datasets[]` names `dataset`) targets every kind of the group.
- **Group order:** reference data, master data, datasets, work product components, work products. It follows the
  direction OSDU's schemas refer in: a work product lists its components (`WorkProduct.1.0.0`, `Components[]`), a
  component refers to its datasets (`AbstractWPCGroupType.1.0.0`, `Datasets[]`) and to master data, and a dataset refers
  to reference data alone (`AbstractDataset.1.0.0`) (project 91, osdu/data/data-definitions, `Generated/` at
  `99f8fc88d8ad838b5738ac5ad92ac643538b5766`). It orders the interfaces within a wave and cuts cycles. OSDU's own
  manifest ingestion does not order by group: it orders a manifest's records by the ids each record's content refers
  to, each record after every record of the manifest it refers to, and does not wait for an id outside the manifest
  (project 823, osdu/platform/data-flow/ingestion/osdu-ingestion-lib, `osdu_ingestion/libs/manifest_analyzer.py`,
  `ManifestAnalyzer`, at `b09eca721fd0aea002878bf7313f20398ab001ed`). The order is the same principle at the level
  of interfaces.
- **Cycles.** OSDU schemas refer both ways (a wellbore refers to its definitive trajectory, the trajectory to its
  wellbore). A cycle is cut first by `after:` (a reference the document orders the other way is not waited for), then
  by group order (a reference from an earlier group to a later one points back); a cycle within one group that
  `after:` does not decide is refused, naming the interfaces. Templates live in the catalog, so the order is worked
  out by the run's preflight, `sqlflow check` and the API, not when the flow is read. The references that point back
  are written as they are: the ids are deterministic, so they resolve once the other side lands. One manifest cannot
  carry both sides of a cycle: the ingestion library sorts a manifest's records with `toposort` 1.6, which raises
  `CircularDependencyError` on a cycle, and its processor catches only errors of single records
  (`osdu_ingestion/libs/processors/single_manifest_processor.py`, `process_manifest`). The manifest route keeps records
  that refer to each other out of one manifest (section 7).
- **`after:`** adds dependencies the schemas do not show.
- **Waves.** Interfaces with nothing left to wait for run together; each still fans out over slices of its own
  primary key, and drains its own batches.

## 7. Records that wait for other records

Order between interfaces is not enough: a wellbore can be held while its well logs are ready.

- When a record is rendered, the values it puts into relationship properties are the OSDU ids it refers to. Each
  reference is kept in a new ledger table, `osdu.RecordReference` (the record, and the OSDU id it refers to), indexed
  by the id.
- A referred id that another record of this ledger has claimed and not delivered holds the referring record,
  saying which record it waits for. An id the ledger does not hold is not waited for: it was delivered by another
  system or already exists. `target.verifyReferences: storage` asks storage for those ids before sending, for
  sources that must not write dangling references.
- When a record is delivered, the records waiting on its id go back to pending, and the next pass sends them. A
  waiting record costs no attempt.
- This needs a module migration (the reference table and its index), which ships with the model change.

## 8. When things stop

### 8.1 The whole run, before anything is sent

The run's preflight checks every interface and stops the run with every finding at once when:

- the flow document is invalid, or the interfaces' dependencies form an unresolved cycle;
- a mapping or template is missing, or a kind has no route;
- a record table is unreachable or has the wrong shape (identity primary key, unique record key);
- the target is unreachable, the credentials are refused, or a legal tag is invalid;
- a DDMS an interface needs cannot be found;
- the ledger database does not allow snapshot isolation.

Nothing is planned or sent, so fixing the cause and running again is all it takes.

### 8.2 One interface, and what depends on it

An interface stops when something fails for the interface as a whole: the credentials are refused on every call, a
service stays unavailable past the retry budget, the source or the work location becomes unreadable, or its
planning fails. Interfaces that depend on it are skipped; the others carry on. The run ends failed and lists which
interfaces completed, stopped and were skipped. Everything already delivered is in the ledger, so the next run
continues where this one stopped.

`failWhen` turns record failures into an interface failure: `failedPercent` (for example 20) or `consecutiveFailures`
(for example 50, counted per error class). By default, 25 consecutive connection or permission failures stop an
interface: that is an outage, not bad data.

### 8.3 Never for one record

A record with a data problem is held, one that keeps failing is failed after its retry budget, a passing problem is
retried later, and a record waiting for another waits (section 7). The interface goes on, and the run succeeds with
its held, failed and waiting counts in the outcome.

### 8.4 A stop

A cancelled run stops every interface, hands its leases back and charges no attempt, as today.

### 8.5 Running again after a failure

A failed run is never redone from the start. What finished is in the ledger, and the next run only does what is left:

| What finished | What the next run does with it |
| --- | --- |
| An interface that completed | Its watermark moved, so the next run finds no changed rows and reads nothing (the tier 0 skip). |
| A record that was delivered | Nothing: its source fingerprint and rendered hash match, so it is skipped without a request. |
| A batch that completed | Nothing: the batch is done in its submission. |
| A record halfway through its steps | The completed steps are kept (a file uploaded and registered, a manifest run started, a DDMS record written), so the next try resumes after the last one. |
| A lease a stopped worker held | Recovered with what the worker had written, and the rest sent. |
| An interface that stopped, and the ones skipped after it | Planned again from their own watermark, and delivered. |

A run whose payload names `interfaces` (or the run page's "run the stopped interfaces") runs only the interfaces
that did not complete. A record can be redelivered or released on its own, as today.

## 9. Keeping a source manageable

- **Every view works per interface.** The run page shows each interface's state, counts and trace on its own line;
  the records, submissions, statistics and history views filter by interface. A problem in one interface is read
  without reading the others.
- **Interfaces are independent units.** Each has its own ledger, watermark, submissions, fan-out and stop rule. A
  large source is many small pipelines run together, not one large one.
- **Order and routes are shown, not guessed.** `sqlflow plan <flow> --explain` and the flow page print every
  interface with its kind, its route, what it waits for and why (the schema property or the `after:`), before
  anything runs.
- **A source can still be split.** One file per source is the recommended shape, not a rule: a very large source can
  use one file per domain (wells, seismic, documents), and dependencies between files are ordered by lineage, as
  flows are today.

## 10. Running part of a source

- `sqlflow run <flow> --payload '{"interfaces":["wells","wellbores"]}'` plans and delivers only those, and the
  interfaces they depend on are not run: their records are read from the ledger as they are.
- A record-scoped run (redeliver, release) names the interface in its payload (`interface`) beside the record keys.
- A redelivery can name the part to send again: `record`, `files`, `bulk`, or `all`. Only that part is sent; the
  others keep what OSDU holds (the record keeps its dataset references and its DDMS bulk link, and a bulk resend
  writes a new bulk version on the same record).
- A type that is often redelivered on its own can live in a flow of its own (the individual form), with its own
  schedule, and still refer to records another source file delivers.
- `drain` drains every interface's pending batches, or the named ones.

## 11. Migration

- A flow without `interfaces` is a source with one interface; its ledger identity stays `FlowId.Of(<flow>)`, so
  existing ledgers, OSDU id claims and history carry on unchanged.
- The sample estate becomes one source file, `recall.yaml`, whose `wellbores` and `welllogs` interfaces adopt the
  ledgers of `recall-wellbore` and `recall-welllog` with `ledger:`.
- `target.protocol` keeps working in the single form: `osduRecord` is `storage`, `osduFile` is `file`,
  `osduManifest` is `manifest`, `osduWellLog` is `ddms` with the well log collection.
- The GUI, API and CLI gain the interface as a filter and the source as a roll-up; the record routes keep their
  ledger identity in the path.

## 12. Tests

- **A matrix of kinds**, none of them special: a reference data kind, a master data kind, a work product component
  with files, a dataset collection, a DDMS kind that is not a well log (WellboreTrajectory), and a manifest batch.
- **Route resolution:** every row of section 5.2, the refusals, a DDMS found in the flow and one found through a fake
  Register service.
- **Order:** dependencies from relationships, a cycle cut by group order, a cycle refused, `after:`, parallel waves.
- **Records that wait:** a child held for its parent and released when the parent lands; an unknown parent not
  waited for.
- **Stops:** each preflight finding, an interface stopped with its dependents skipped, `failWhen`, a cancellation.
- **Compatibility:** every existing single-kind flow reads and runs unchanged; the sample estate consolidated
  without re-sending anything.
- **SQL Server:** a source with several interfaces, fanned out over several nodes, end to end.

## 13. Build order

1. The document model: `interfaces`, the shared defaults, the single-interface reading of today's form, the ledger
   identities, and route resolution from the facts that need no network, with its refusals.
2. The runtime: preflight across interfaces, waves, per-interface planning, fan-out and drains, the stop rules and
   the run outcome.
3. Order from the schemas' relationships, cycles and `after:`.
4. The generic `ddms` route, the `target.ddms` catalog and Register discovery.
5. Records that wait: the reference table, its migration, the hold and the release.
6. The `dataset` route, the composed routes, manifests by reference and the `workflow` route.
7. The GUI, API, CLI and lineage views of sources and interfaces.
8. The sample estate, the documentation and the test matrix.

## 14. Open questions

1. **Which DDMSs are in scope?** Answered: every DDMS. Each has its contract pinned in `osdu/specs` before its route
   type is built ([osdu-coverage-plan.md](osdu-coverage-plan.md)).
2. **Is the Register service's DDMS registry filled in your deployments?** If not, `target.ddms` is the way DDMSs
   are declared.
3. **References to records outside the ledger:** trust them (the default here) or check them in storage before
   sending?
4. **Manifests too large to send inline:** answered by the manifest route's by-reference mode (section 5.8), built from
   the ingestion DAGs' and External Data Services' code.
5. **The name `interfaces`:** kept.
