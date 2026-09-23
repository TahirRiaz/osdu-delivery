# Document reference

Two authored documents, kept separate ([design.md](design.md) section 9). Keys are camelCase. Unknown keys
are a parse error. Every validation failure names the file.

## Flow

```yaml
flowType: delivery                 # required discriminator
name: wells-welllog-03-header-delivery               # required; the flow id is derived from it

parameters:                        # optional; {name} tokens usable in source.work and each payload root
  logSource: { required: true, default: null, description: ... }

source:
  connection: ${env:OSDU_SAMPLE_DB}  # the ingestion database, resolved on the node; never a literal secret
  record:
    object: OsduSample.ing.WellLog   # three-part name of the record ingestion table
    key: [source_project, log_id]    # the ing flow's load.keyColumns; must equal the mapping's dataset.key columns
    primaryKey: RecId                # the table's identity primary key (the ing flow's target.identityColumn); required with fanOut
    scope:                           # optional: column -> parameter, each a typed [column] = @p predicate
      log_name: logSource
  datasets:                          # optional child ingestion tables the mapping repeats
    curves:
      object: OsduSample.ing.WellLogCurve
      join: { source_project: source_project, log_id: log_id }   # childColumn: recordColumn, covering every key column
      orderBy: [curve_ordinal]       # child row order within a record
      maxRowsPerRecord: 100000       # a record with more child rows than this is held
  payloads:                          # optional: the file sets a streaming protocol sends
    curves:
      root: ../data/curves           # folder the files must sit under; {parameter} tokens; relative to the flow file
      locationColumn: curve_folder   # record column holding the payload folder
      pattern: "chunk_*.parquet"     # glob under that folder (default *)
      hashColumn: payload_hash       # unless change.payloadDetect is lastModified
      chunkCountColumn: chunk_count  # optional: the declared number of files
  lastModified: update_date          # optional business version column (the stale gate)
  systemColumns:                     # defaults shown; fileName: ~ opts out of recording the origin file
    updated: UpdatedDate_DW
    fileName: FileName_DW
    rowNumber: RowNumber_DW
    deleted: DeletedDate_DW
  incremental:
    overlapSeconds: 900              # re-read below the last watermark (default 900, 0 to 86400)
    pageSize: 1000                   # keys per page
    isolation: snapshot              # snapshot (default) or readCommitted
    commandTimeoutSeconds: 0         # 0 = bounded by cancellation
  work: ../.work/{logSource}         # where the intake writes its work batches (required)

render:                            # the only block that changes what a document is
  mapping: WellLog@1.4.0           # pinned Name@version, never floating
  cacheVersion: current            # the version of the target partition's cache: current (the default) or a label such as 20260908T212727Z
  parameters:                      # values for the parameters the mapping declares, as literals or ${env:...} references
    dataPartition: ${env:OSDU_DATA_PARTITION}
    aclOwner: ${env:OSDU_ACL_OWNER}
    aclViewer: ${env:OSDU_ACL_VIEWER}
    legalTag: ${env:OSDU_LEGAL_TAG}

change:
  detect: renderedHash             # renderedHash | always
  payloadDetect: contentHash       # contentHash | lastModified | always
  onUnchanged: skip                # skip | deliver
  useSourceVersions: true          # tier-0 gate: skip the run when no row changed in the window

target:
  endpoint: ${env:OSDU_URL}        # ${env:NAME} and ${keyvault:vault/secret} references
  auth:
    type: oauth2ClientCredentials  # none | bearer | apiKeyHeader | basic | oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token: { url: ${env:OSDU_TOKEN_URL}, body: { scope: ${env:OSDU_SCOPE} }, basicAuthClient: false, tokenPath: access_token, applyPrefix: "Bearer " }
  headers:                         # extra headers on every request
    data-partition-id: dev         # required: every OSDU service rejects a request without it, so the loader insists on it; its cache is the one the mapping reads
  # the route: storage | file | dataset | manifest | ddms | fileAndDdms | manifestAndDdms | workflow | dspdm | etp
  # (the names these routes carried before, osduRecord | osduWellLog | ... , still load)
  protocol: ddms
  ddms:                            # ddms: DDMSs the records go to by entity type, before the Wellbore DDMS under ddmsRoot (see "The DDMSs a flow delivers to")
    wellbore: { root: /api/os-wellbore-ddms }
  workflow: { ... }                # workflow: the workflow route's declaration ("The workflow route" below)
  airflow:                         # workflow: the Airflow behind the Workflow service, for outputs read from XCom
    endpoint: ${env:AIRFLOW_URL}
    apiVersion: v1                 # v1 (Airflow 2, /api/v1) | v2 (Airflow 3, /api/v2)
    auth: { type: basic, secondarySecretRef: ${env:AIRFLOW_USER}, secretRef: ${env:AIRFLOW_PASSWORD} }  # on v2, basic is exchanged for a token at /auth/token
    headers: {}
  eds:                             # External Data Services: how the records that configure it are checked (see "External Data Services")
    checks: true                   # hold registry entries, data jobs and proxy datasets EDS could not use (default true)
    retrieval: true                # registry entries need the DatasetURL eds-dms retrieves their datasets through (default true)
    build: azure                   # the eds-dms build: corePlus | azure | gc; only gc takes GcpServiceAccount schemes
  dspdm:                           # dspdm: the Production DDMS core service the rows go to (see "The Production DDMS core service")
    root: /api/dspdm/v1            # where DSPDM is under the endpoint; left out when the endpoint is DSPDM
    timezone: GMT+00:00            # the zone every request names, GMT+hh:mm (default GMT+00:00)
    existingRows: hold             # a row that holds a record's key and that the record did not write: hold (default) | update
    businessObjects:               # by the entity type a kind names; a kind not listed is the business object named like its entity
      well_test: { name: WELL TEST, key: [UWI, TEST_NUM] }
  etp:                             # etp: the Reservoir DDMS the Energistics objects go to (see "The Reservoir DDMS")
    path: /api/reservoir-ddms-etp/v2/  # where its ETP WebSocket answers under the endpoint
    dataspace: volve/study         # the dataspace a record goes into when its document names none
    objectsPerMessage: 100         # objects per message, which is also the batch the route is handed
    maxMessageBytes: 16000000      # the largest message this side offers; the server narrows it to its own maximum
    maxArrayBytes: 268435456       # the most bytes of one array a delivery reads into memory
    lock: false                    # leave the dataspace read-only between deliveries, unlocking it to write
  protocolOptions:
    payload: curves                # which source.payloads entry the protocol streams
    # ddms: every path defaults to the collection serving the record's entity type (/ddms/v3/welllogs for a WellLog);
    # a path set here is used as written, for a facade such as petrodb-api, and the collection supplies the rest.
    recordPath: /ddms/v3/welllogs
    recordMethod: POST
    dataPath: /ddms/v3/welllogs/{id}/data
    sessionPath: /ddms/v3/welllogs/{id}/sessions
    sessionDataPath: /ddms/v3/welllogs/{id}/sessions/{sessionId}/data
    sessionCommitPath: /ddms/v3/welllogs/{id}/sessions/{sessionId}
    verifyPath: /ddms/v3/welllogs/{id}
    deletePath: /ddms/v3/welllogs/{id}       # logical delete (storage: POST /api/storage/v2/records/{id}:delete)
    # The storage service's purge (storage: DELETE /api/storage/v2/records/{id}). On the ddms route a bulk
    # collection purges with DELETE {deletePath}?purge=true, and a record collection, whose DELETE is logical only,
    # purges here; a DDMS endpoint does not reach storage by a path, so write the whole URL.
    purgePath: https://osdu.example.com/api/storage/v2/records/{id}
    # Versions belong to the storage service, which is not where a DDMS endpoint points, so a ddms flow whose endpoint
    # is the DDMS needs the whole URL here. Any path option may be written absolute; it is guarded like every other request.
    purgeVersionsPath: https://osdu.example.com/api/storage/v2/records/{id}/versions
    sessionThresholdChunks: 1      # 1: a single chunk goes to the bulk endpoint, more open a session. 0: always a session
    maxChunkValues: 10000000       # wellbore DDMS ceiling: cells (rows x columns) per chunk (0 = do not check)
    maxChunkColumns: 3000          # wellbore DDMS ceiling: columns per chunk; 500 on targets before OSDU M26
    payloadContentType: application/x-parquet  # the type the payload goes as: the bulk data on a ddms route, the files where they are the payload
    filesContentType: text/plain   # the type a record's files are uploaded as; default payloadContentType where the files are the payload, application/octet-stream beside bulk data
    versionPath: recordIdVersions[0]
    skipDuplicates: false          # storage, file: opt in to skipdupes=true only once the target is confirmed to compare acl, legal and tags, not just data
    verifyBatchPath: /api/storage/v2/query/records   # the batched read a verify pass uses (100 ids per request)
    ddmsRoot: /api/os-wellbore-ddms  # ddms: the endpoint is the platform root and the Wellbore DDMS sits under this path; omit when the endpoint is the DDMS itself
    registerPath: /api/register/v1/ddms/{id}  # ddms: where the Register service reads the registration of a DDMS target.ddms names with register
    contentSchemaVersion: 1.0.0    # ddms, RAFS: the content schema version of a table whose file name names none (nmr.parquet, not nmr.1.1.0.parquet)
    validateLegalTags: true        # deliver and intake runs ask the legal service about the mapping's legal tags first; false skips it
    legalValidatePath: /api/legal/v1/legaltags:validate  # where to ask; needed (as a whole URL) only for a ddms flow whose endpoint is the DDMS itself
    preserveDataKeys: [Datasets, DDMSDatasets, ExtensionProperties]  # data keys other systems write, carried from the stored record into every update
    batchSize: 100                 # records per write request where the service takes arrays (storage, file, manifest; at most 500)
    uploadUrlPath: /api/file/v2/files/uploadURL      # file, manifest: the signed landing-zone location
    uploadUrlExpiry: 12H           # how long the signed URL stays valid (30M, 12H, 2D); default the service's one hour
    uploadHeaders: { x-ms-blob-type: BlockBlob }     # extra headers on the signed-URL upload; the Azure blob type is added for a *.blob.core.* URL anyway
    fileMetadataPath: /api/file/v2/files/metadata    # file, manifest: registers the dataset record
    fileDeletePath: /api/file/v2/files/{id}/metadata # purge: deletes a dataset record and its file
    datasetKind: osdu:wks:dataset--File.Generic:1.0.0  # file, manifest: the kind of the dataset registered per file; dataset: the kind of the dataset a record of another kind refers to
    datasetsProperty: Datasets     # the record's data property listing its dataset ids
    workflowName: Osdu_ingest      # manifest: the ingestion workflow
    workflowRunPath: /api/workflow/v1/workflow/{workflow}/workflowRun
    workflowStatusPath: /api/workflow/v1/workflow/{workflow}/workflowRun/{runId}
    workflowPollSeconds: 10
    workflowTimeoutMinutes: 60     # a run still going after this fails the try; the next try resumes polling it
    datasetIndexWaitSeconds: 120   # manifest: how long to wait for the search index to list the registered datasets before the manifest names them (0 = no wait); file, manifest: how long a resumed registration waits for the index before registering the file again
    searchQueryPath: /api/search/v2/query            # file, manifest: where those waits ask
    workflowAppKey: osdu-delivery  # executionContext.Payload.AppKey
    workflowPayload: {}            # extra executionContext.Payload entries
    manifestKind: osdu:wks:Manifest:1.0.0
    manifestSection: WorkProductComponents           # default derived from each record's kind
    recordQueryPath: /api/storage/v2/query/records   # reads the records back after a workflow run
    manifestByReference: never     # manifest, manifestAndDdms: never | always | auto (by reference above manifestInlineLimitKb, when the partition registers the workflow)
    manifestInlineLimitKb: 12000   # auto: the largest trigger request sent inline, measured as the request indented by four
    byReferenceWorkflowName: Osdu_ingest_by_reference
    workflowPath: /api/workflow/v1/workflow/{workflow}               # where the Workflow service describes a workflow; asked before relying on one
    datasetInstructionsPath: /api/dataset/v1/storageInstructions     # dataset, workflow, manifest by reference: the Dataset service's paths
    datasetRegisterPath: /api/dataset/v1/registerDataset
    datasetRetrievalPath: /api/dataset/v1/retrievalInstructions
    datasetSoftDeletePath: /api/dataset/v1/metadataRecord/{id}/softDelete
  verifyReferences: none           # none (the ledger alone) | storage (ask storage about the ids the ledger does not hold)

reliability:
  concurrency: 8
  retry: { attempts: 4, backoff: exponential, baseDelayMs: 500, maxDelayMs: 30000, honorRetryAfter: true, recordBaseDelayMinutes: 1, recordMaxDelayMinutes: 60 }
  skipStatusCodes: [409]           # statuses that hold a record instead of retrying
  timeoutSeconds: 100
  rateLimitRps: 0                  # 0 = unlimited
  verifyTls: true                 # false needs SQLFLOW_DELIVERY_ALLOW_INSECURE_TLS=true on the node too, or the run is refused
  urlAllowlist: []                 # SSRF allowlist; *.suffix wildcards; checked on every redirect too
  maxResponseBytes: 67108864
  maxRequestBodyBytes: 0           # the target's declared request body ceiling; a bigger chunk holds the record (0 = not declared)
  leaseSeconds: 300
  batchSize: 50
  batchRecords: 500                # rendered documents per work batch file
  renderParallelism: 0             # renderers in the intake pipeline (0 = the machine's cores)
  fanOut: 0                        # member runs a large submission spreads over (0 = none; at most 64; needs source.record.primaryKey)
  fanOutMinRecords: 1000           # below this a submission never fans out

schedule:                          # service: what to run, when, and with which parameter values
  cron: "0 * * * *"
  timezone: UTC
  operation: deliver
  values: { logSource: STAT_COMP }  # required when the flow declares required parameters; a fire supplies nothing else
verify: { reconcile: false }       # whether the verify pass re-queues drifted or missing records
```

### Render-affecting versus operational

Only `render.*`, and the template version the pinned mapping names, enter the render context. Everything else changes how a document gets there: raising
`reliability.concurrency` or changing `target.endpoint` never redelivers a record. A moved render context (a new
mapping version, template version or cache version, or a changed system property a mapping's searches rely on) renders
the record again, and whether it is sent is still
decided by the hash of the rendered document alone, so a new cache version that renders the same document sends nothing.

### The cache a flow renders with

The mapping's `cache.<Type>` sources read the cache of the partition the flow delivers to: the partition in
`target.headers.data-partition-id`, which every cache flow of that partition fills
([The partition cache](#the-partition-cache)). A flow names no cache: a flow document that still declares
`render.cache` is refused when it loads, rather than read against a cache other than the one its author meant.
`render.cacheVersion` says which version of the partition's cache: `current`, the default, takes whichever version is
current when the run starts and records it in the render context; a version label (`20260908T212727Z`) pins that
version. The render context records the partition under `cache` and the version under `cacheVersion`.

A mapping that reads nothing from a cache renders against no cache, so refreshing a cache never moves the render
context of records that never read it. A mapping that searches the platform is pinned as well to the state of the
partition's system properties its lookups rely on, recorded under `systemProperties`
(`{"indexer":{"featureFlag.keywordLower.enabled":"Enabled"}}`): those of the version it renders against when it reads
the cache, and when it only searches, those of the version `render.cacheVersion` names, read without its records, while
`cacheVersion` stays `none`. So a refresh that changes reference data never renders a record that only searches again,
and one that finds the partition's keywordLower setting changed renders all of them again under the new rule. When the
partition's cache holds no version yet, or the host has no module database, the properties are unknown and a search
asks exact questions alone. A mapping that does read the cache fails before anything renders when the
partition's cache holds no version yet (run a cache flow whose `source.headers.data-partition-id` is that partition
with the refresh operation), or when `render.cacheVersion` pins a version the catalog does not hold. Both the cache and
the template are read from the catalog, so rendering needs the catalog connection.

### Incremental reads: what changed since the last run

A run does not read every row. It reads the rows the ingestion tables changed in a window above the scope's
watermark, the records whose child rows changed in it, and any record the ledger asked to plan again. Every row it
does read goes through the whole pipeline: render, the preflight-checked mapping, the hash of the rendered document
against what OSDU holds, and the same hash check again by the worker just before anything is sent.

| Key | What it does |
| --- | --- |
| `source.systemColumns.updated` | The ingestion column the window is taken on, `UpdatedDate_DW` by default. SQLFlow's ingestion stamps it on insert, and on update only for rows whose checksum changed, so an identically re-landed row is never read again. The window is `(watermark - overlapSeconds, now]`, fixed when the read opens. |
| `source.incremental.overlapSeconds` | How far below the watermark the next run reads again (900 by default), so a transaction that committed after the previous read's upper bound is still picked up. |
| `source.lastModified` | An optional business version column: a `datetime`, or text holding RFC 3339 / ISO 8601 (without an offset it is read as UTC). A row whose moment is later than the version the ledger holds, delivered or queued, is planned; the same moment is skipped without rendering; an older one is **stale**, never sent, and recorded as a skipped attempt against the record. An empty or unreadable value holds the record with a reason naming the column. |
| `change.payloadDetect: lastModified` | The payload's files are its watermark. A payload is reconsidered when a file was modified after the ones OSDU's payload was sent from, or the set of files (names, sizes, times) changed; files older than the payload already delivered or queued are stale and never sent. When the flow still declares a `hashColumn`, that hash stays the final check, so rewritten files with the same content are not uploaded again; without one, the files themselves are the payload's identity. Costs one storage listing per record per run. |

The per-record gate is the **ingestion fingerprint**: SHA-256 over the record row's `UpdatedDate_DW` and, per child
dataset in name order, its row count and newest `UpdatedDate_DW`. A record is skipped without rendering only when
that fingerprint, the business version when one is declared, the render context and the payload hash all match what
the ledger holds.

A record whose newer version arrives while an earlier one is being delivered does not lose it: the new work queues
behind the delivery and the next pass of the same run sends it, after the final check has compared it with what just
landed. Concurrent intakes cannot take a record backwards either; the ledger refuses staged work older than what it
holds and records it as stale. The submission counts the skips (`skippedStale`), and the records the final check found
OSDU already holding (`unchangedAtPush`), beside the usual counts.

### Where the records come from

The flow reads the keyed ingestion tables SQLFlow's own pre-ingestion and ingestion flows load
([architecture.md](architecture.md)). `source.connection` names the database, resolved on the node that runs the
flow; `source.record.object` is the record table, and each `source.datasets` entry a child table joined to it by the
record key. Nothing else reads the source: there is no drop, no manifest and no replica, and the flow never extracts
from a database of its own.

`source.record.key` must name the same columns as the mapping's `dataset.key`, and they should be the ing flow's
`load.keyColumns`, because that is what makes one row one deliverable. The loader refuses a two-part object name, a
dataset join missing a key column, a dataset named `record`, a scope naming an undeclared parameter, a streaming
protocol without a `locationColumn`, a missing `hashColumn` under `contentHash`, and a literal secret in `connection`.
The removed keys (`location`, `manifest`, `records`, `scopes`, `fingerprint`, `knownState`, `manualSubmission`,
`submissions`, `sql`, `replica`) are refused by name.

### The identity primary key

`source.record.primaryKey` names the record table's identity primary key: an integer identity column that is the
table's own single-column primary key, which SQLFlow's ingestion creates when the ing flow sets
`target.identityColumn` (the samples use `RecId`). The table is then clustered on it, so every other index carries
only that narrow value as its row locator. The record key stays what identifies a record, derives its delivery key and
OSDU id, and joins the child tables; the primary key is how the rows are found and dealt out.

- **Paging.** A read pages by the primary key: each page is a seek on the clustered key after the last value read,
  and the page's rows are read by it. Without one, a read pages by the record key.
- **Fan-out.** A flow with `reliability.fanOut` above zero must name it, and the loader refuses one that does not. The
  coordinating run counts the candidates per range of primary key values (about 64 ranges per slice, one aggregate
  over the candidates, which ranks and sorts nothing) and cuts them into at most 1024 contiguous slices of the
  primary key, each holding its share of candidates give or take one counted range. The submission records the bounds
  and the column they were cut on, and each member reads exactly its own range. A re-run of a submission cut on
  another column than the flow now names is refused.
- **What a run checks.** Opening the source, a run refuses a declared column that is not an integer, not an identity
  column, or not the table's single-column primary key, and a record key without a unique index that has no filter
  (a record held by two rows could otherwise fall into two ranges). The message says what is missing and, for a table
  that lacks the key, the statement that adds one:
  `ALTER TABLE [db].[schema].[table] ADD [RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_table] PRIMARY KEY CLUSTERED;`
  That rewrites the table, so run it while nothing loads it. SQLFlow adds `target.identityColumn` only when it
  creates a table: set on an existing table, it adds a plain nullable column that nothing fills, which a run refuses as
  not an identity column (drop it before adding the real one).

### The DDMSs a flow delivers to

A record on the `ddms` route goes to the collection of the DDMS that serves its entity type, which every record id
names (`dev:work-product-component--WellboreTrajectory:...`). OSDU Delivery knows the collections of the Wellbore
DDMS ([../specs/wellbore-ddms/INTEGRATION.md](../specs/wellbore-ddms/INTEGRATION.md) sections 2 and 4.3):

| Entity type | Collection | Bulk data | Bulk columns checked against |
| --- | --- | --- | --- |
| `work-product-component--WellLog` | `welllogs` | yes | `data.Curves[].CurveID`, and each curve's `NumberOfColumns` |
| `work-product-component--WellboreTrajectory` | `wellboretrajectories` | yes | `data.AvailableTrajectoryStationProperties[].Name` |
| `work-product-component--PPFGDataset` | `ppfgdataset` | yes | `data.Curves[].CurveID` |
| `work-product-component--WellPressureTestRawMeasurement` | `wellpressuretestrawmeasurement` | yes | `data.Curves[].CurveID`, and each curve's `NumberOfColumns` |
| `master-data--Well` | `wells` | no | |
| `master-data--Wellbore` | `wellbores` | no | |
| `work-product-component--WellboreMarkerSet` | `wellboremarkersets` | no | |
| `work-product-component--WellboreIntervalSet` | `wellboreintervalsets` | no | |
| `master-data--WellLogAcquisition` | `welllogacquisition` | no | |

`protocolOptions.ddmsRoot` says where the Wellbore DDMS is under the endpoint (`/api/os-wellbore-ddms`); a flow in the
single form without it and without `target.ddms` has the Wellbore DDMS itself as its endpoint. `target.ddms` declares
DDMSs by name: a Wellbore DDMS deployed elsewhere, a DDMS of the same call pattern serving other collections, one the
Register service knows, or a DDMS of another call pattern.

```yaml
target:
  endpoint: ${env:OSDU_URL}
  headers: { data-partition-id: dev }
  ddms:
    wellbore:
      root: /api/os-wellbore-ddms       # where it is under the endpoint
      shape: wellboreDdmsV3             # its call pattern, and the default
      collections:                      # what it serves; the shape's own collections (the table above) when left out
        work-product-component--WellLog: { path: welllogs, bulk: true, columns: curveIdsAndWidths }
        master-data--Wellbore: { path: wellbores }
    partner:
      register: partner-wdms            # its root and collections are read from the Register service when the flow runs
    welldelivery:
      root: /api/well-delivery
      shape: wellDeliveryV1
      mirror: true                      # the deployment copies each entity into Storage (the default)
      provider: azure                   # azure, aws, gc or ibm
      concurrency: 1                    # writes this node sends to the deployment at once (the default)
    rafs:
      root: /api/rafs-ddms
      shape: rafsV2
    historian:
      root: /api/pddms/ingest/v1         # the ingestion service; required, the records going through Storage
      shape: productionTimeSeriesV1
      queryRoot: /api/pddms/query/v1     # the query service (the default)
      settleSeconds: 60                  # how long a delivery reads accepted points back for (the default; 0 reads nothing back)
      pollSeconds: 5                     # the pause between two reads of points not served yet (the default)
      maxRequestBytes: 8000000           # the largest request the points go in (the default)
    seismic:
      root: /api/seismic-store/v3        # Seismic Store with its version path; required, the records going through Storage
      shape: seismicStoreV3
      subproject: seismic-raw            # required: the subproject the datasets are registered in
      tenant: dev                    # the tenant (the default: the flow's data-partition-id)
      folder: surveys/north              # the folder the datasets go in (the default: the subproject's root)
      provider: anthos                   # azure, gc, anthos or ibm (the default: the provider the service names)
      objectStore: https://minio.example.com   # the S3 store on anthos and ibm, another Google endpoint on gc
      region: us-east-1                  # the region S3 requests are signed for (the default)
      chunkMiB: 32                       # each blob a file is cut into on Azure, and each part it goes up in (the default)
      readOnly: false                    # whether a delivered dataset is closed read-only (the default)
    reservoir:
      root: /api/rm-ddms                 # required: the service's prefix, which its deployment gives it
      shape: reservoirManagement
      settleSeconds: 60                  # how long a delivery with rows waits for the service to take its record in (the default)
      pollSeconds: 5                     # the pause between two asks (the default)
```

| Key | Meaning |
| --- | --- |
| `root` | Where the DDMS is under the endpoint: a path starting with `/`. Left out, the endpoint is the DDMS itself, which only a flow in the single form declaring that one DDMS and no `ddmsRoot` can say; every DDMS of a source with interfaces names its root or its registration. |
| `shape` | The DDMS's call pattern: `wellboreDdmsV3` (the default), the Wellbore DDMS v3's `/ddms/v3/<collection>` calls; `wellDeliveryV1`, the Well Delivery DDMS's `/storage/v1/<type>` calls; `rafsV2`, the Rock and Fluid Sample DDMS's `/v2/<collection>` calls; `productionTimeSeriesV1`, the Production DDMS historian's `/production-values/<id>/timeseries` calls, beside Storage for its records; `seismicStoreV3`, Seismic Store v3's `/dataset/tenant/<tenant>/subproject/<subproject>/dataset/<name>` calls and the object store behind it, beside Storage for its records; `reservoirManagement`, the Reservoir Management DDMS's `/ddms/<collection>` and `/ddms/<table>` calls, beside Storage for its records. |
| `collections` | The entity types (with their group) the DDMS serves, each with `path`, the collection's path segment; `bulk`, whether it keeps bulk data beside its records (default `false`); `columns`, what the bulk data's columns are checked against before they are sent: `unchecked` (the default), `curveIds`, `curveIdsAndWidths` or `trajectoryStations`, which only a bulk collection of a Wellbore DDMS takes; and `typedContent`, which says a RAFS content collection holds several content types, each under its own path segment (default `false`). A Well Delivery DDMS serves each type under the type itself, lowercased, so its collections name no `path` (or that one), and hold records alone. A Seismic Store serves `dataset--FileCollection.*` types, each keeping files, so its collections take no `bulk: false`, `columns` or `typedContent`, and `path` only names the type (the part after `dataset--FileCollection.`, lowercased, when left out). A Reservoir Management DDMS serves an entity type under one of its nine header collections, named by `path` (the one the service's own entity type has when left out); whether its records keep rows is the collection's, so `bulk`, `columns` and `typedContent` are not given. |
| `register` | The id the DDMS is registered under in the Register service (2 to 50 letters, digits and `-`), for a DDMS of the `wellboreDdmsV3` shape. What the flow leaves out is read from the registration when the flow's protocol is built: the root from the one server its interfaces' OpenAPI documents name, the collections from their retrieval operations (`x-ddms-retrieve-entity`), where a collection the shape knows by its path takes the shape's entity type and rules. `protocolOptions.registerPath` says where the registration is read (default `/api/register/v1/ddms/{id}` under the endpoint). A DDMS that declares both its root and its collections has nothing to read, so `register` is refused there. |
| `mirror` | Well Delivery DDMS: whether the deployment copies every entity into Storage (`app.entity.storage`, on in every provider's chart, so `true` by default). The API cannot tell. The copy's id is kept on the record, and a removal takes the copy through Storage too, since the DDMS never deletes it. |
| `provider` | Well Delivery DDMS: the provider the deployment runs on, which the API cannot tell either. On `ibm` an entity is never written again under a version it already has, since that store refuses the second save. Seismic Store: `azure`, `gc`, `anthos` or `ibm`, which decides the object store the files go to and how; left out, it is the provider the service names in its `Service-Provider` header. |
| `concurrency` | Well Delivery DDMS: how many writes one node sends to the deployment at once, 1 to 16 (default 1). The service's Mongo and Cosmos stores keep the collection of the current write in shared state, so writes of different types at once can land in each other's collection. |
| `queryRoot` | Production DDMS historian: where its query service is under the endpoint (default `/api/pddms/query/v1`, its contract's server). Each accepted version of the points is read back there, and the probe asks its `/info`. |
| `settleSeconds` | Production DDMS historian: how long a delivery reads the points it sent back before it leaves the record for its next try, 0 to 3600 (default 60). The ingestion service accepts points before they are stored, so a delivery counts once the query service serves them; 0 delivers on the acceptance alone. Reservoir Management DDMS: how long a delivery with rows waits for the service to take its record into its database, 0 to 3600 (default 60; 0 asks once). |
| `pollSeconds` | Production DDMS historian: the pause between two reads of points the query service does not serve yet, 1 to 60 (default 5). Reservoir Management DDMS: the pause between two asks, 1 to 60 (default 5). |
| `maxRequestBytes` | Production DDMS historian: the largest request body the points are sent in, 10000 to 64000000 (default 8000000, the limit the historian's documentation gives and has not verified). A declared `reliability.maxRequestBodyBytes` below it bounds the requests instead. |
| `subproject` | Seismic Store, required: the subproject the datasets are registered in (a lower-case letter, then lower-case letters, digits and `-`). An operator provisions it; the route never creates one. |
| `tenant` | Seismic Store: the tenant the datasets are registered under (letters, digits, `_`, `.` and `-`). Left out, it is the flow's `data-partition-id`, which the tenant equals on OSDU. |
| `folder` | Seismic Store: the folder under the subproject the datasets are registered in, as segments of letters, digits, `_`, `.` and `-` (`surveys/north`; leading and trailing slashes are dropped). Left out, the subproject's root. |
| `objectStore` | Seismic Store: the object store's absolute `http(s)` address, without credentials, query or fragment. Required on `anthos` and `ibm`, where the service issues a key triple for an S3 store it does not name; on `gc`, another endpoint than `https://storage.googleapis.com`. Azure's credential names its store. |
| `region` | Seismic Store: the region S3 requests are signed for on `anthos` and `ibm` (default `us-east-1`). |
| `chunkMiB` | Seismic Store: 0 to 256 (default 32). On Azure, a single file is cut into blobs of this size (0 keeps it one blob); everywhere, the size of each block, piece or part a file goes up in (32 MiB when 0). |
| `readOnly` | Seismic Store: whether a delivered dataset is closed read-only (default `false`). A later delivery of its files lifts the flag, writes, and closes it read-only again. |

The Well Delivery DDMS serves, by default, the entity types its brief lists
([../specs/well-delivery-ddms/INTEGRATION.md](../specs/well-delivery-ddms/INTEGRATION.md) section 4), in the groups the
OSDU data definitions give them: `master-data--` Well, WellPlanningWell, WellPlanningWellbore, Wellbore,
WellActivityProgram, ActivityPlan, WellboreArchitecture, HoleSection, BHARun, TubularAssembly, TubularComponent,
OperationsReport, FluidsReport, FluidsProgram, CasingDesign, EvaluationPlan, PlannedCementJob, WellBarrierElementTest,
GeometricTargetSet, Risk, SurveyProgram and Rig; `work-product-component--` WellboreTrajectory, TubularAssembly,
TubularComponent, WellLog, PPFGDataset, PlannedLithology and WellboreMarkerSet. Declared first, a Well Delivery DDMS
with those defaults takes the well logs, trajectories, pore pressure datasets and marker sets the Wellbore DDMS under
`ddmsRoot` would otherwise keep; a flow that sends their bulk data to the Wellbore DDMS lists the Well Delivery
collections it wants.

RAFS serves ([../specs/rafs-ddms/INTEGRATION.md](../specs/rafs-ddms/INTEGRATION.md) section 2.1):

| Entity type | Collection | Content |
| --- | --- | --- |
| `master-data--` GenericFacility, GenericSite, Sample, SampleAcquisitionJob, SampleChainOfCustodyEvent, SampleContainer | `masterdata` | none |
| `work-product-component--SamplesAnalysesReport` | `samplesanalysesreport` | none |
| `work-product-component--SamplesAnalysis` | `samplesanalysis` | several types (`nmr`, `capillarypressure` and the others the service's catalogue lists) |
| `work-product-component--FluidModel` | `fluidmodel` | several types (`blackoilfluidmodel`, `compositionalfluidmodel`) |
| `work-product-component--SaturationFunctionSet` | `saturationfunctionset` | one type, named after the collection |
| `work-product-component--ReservoirSimulationRockPhysicsModel` | `reservoirsimulationrockphysicsmodel` | one type, named after the collection |
| `work-product-component--DepthShift` | `depthshift` | one type, named after the collection, of exactly one row |

A RAFS record's `bulk` part holds its content tables, one file per content type, named after what it holds:
`<contentType>.json` or `<contentType>.parquet`, or `<contentType>.<schemaVersion>.parquet` where the table follows
another content schema version than `protocolOptions.contentSchemaVersion` (default `1.0.0`). JSON goes as
`application/json` (the split form, or a list of rows), parquet as `application/x-parquet`.

The historian serves `work-product-component--ProductionValues` records alone, so its DDMS lists no `collections`
([../specs/production-timeseries/INTEGRATION.md](../specs/production-timeseries/INTEGRATION.md) section 2). A record of
kind 2.0.0 or later defines one series per `data.ProductionMetricValues` entry, by its `DDMSDatasetID`, and the kind of
its values by its `ParameterKindID` (Double, Integer, Boolean, String, a SET-STRING kind; the historian's checks refuse
every Timestamp point, so a date-time series' points are not sent). The record's `bulk` part holds its points, in one or
more files:

- `<name>.parquet`, a wide table: a `timestamp` column in epoch milliseconds (whole numbers, parquet timestamps, or
  dates, read as midnight UTC) and one column per series, named by its `DDMSDatasetID`. An empty cell (or a NaN) is no
  point. A pandas index column is left out unless it is the `timestamp`.
- `<name>.json`, the ingestion service's own body for one record, up to 64 MiB:
  `{"timeseries":[{"timeseriesId":"OIL","points":[{"timestamp":978307200000,"value":58883469.74}]}]}`. A SET-STRING
  series is sent from here, each value a list of distinct strings; a number is sent with the digits it was written with.

Values are sent as they are, in the series' `UnitOfMeasureID`: the historian converts nothing. A series keeps its points
in one file, in increasing timestamp order with one value per timestamp, and every point is of its series' kind (an
Integer series takes whole numbers only); a file that breaks a rule holds the record before anything is sent.

Seismic Store registers a dataset for each `dataset--FileCollection.*` record
([../specs/seismic-ddms/INTEGRATION.md](../specs/seismic-ddms/INTEGRATION.md)). Its DDMS serves, by default, the types
Seismic Store's clients and its v4 service know: `dataset--FileCollection.` SEGY, Slb.OpenZGY, Bluware.OpenVDS and
Generic; a flow lists others under `collections`. A record's dataset is `sd://<tenant>/<subproject>/<folder>/<key>`,
where the key is the part of the record's id after its type and takes letters, digits, `_`, `.` and `-` only. The
record is the dataset's `seismicmeta`, pointing at the dataset: `data.DatasetProperties.FileCollectionPath` is the
folder (`sd://<tenant>/<subproject>/<folder>/`) and one `FileSourceInfos` entry names the dataset, its `FileSize` and
the record's `data.TotalSize` being the bytes of the files; what the mapping renders there is replaced, its other
`FileSourceInfos` fields kept. The record names its owners, viewers, a legal tag and a country, which Seismic Store
would otherwise fill in with defaults.

The record's `bulk` part holds the dataset's files. One file is written as Seismic Store's clients read it: on Azure
cut into blobs `0` to `N-1` of `chunkMiB` each (one blob with `chunkMiB: 0`), with the file's MD5 up to each blob's end
as its content MD5; elsewhere as one object `0`. Several files are each one object under its name, so no two share a
name and none is `.` or `..`. A record delivered without its `bulk` part registers its dataset empty.

The Reservoir Management DDMS keeps a copy of nine kinds of records and the rows of tables below them in its own
database ([../specs/reservoir-management-ddms/INTEGRATION.md](../specs/reservoir-management-ddms/INTEGRATION.md)):

| Collection | Entity type (the service's kind) | Tables below it |
| --- | --- | --- |
| `estimated-volumes` | `work-product-component--ReservoirEstimatedVolumes` (1.0.0) | `estimated-volumes-det` |
| `pvt-properties` | `master-data--FluidSystem` (1.0.0) | none |
| `geological-labels` | `work-product-component--GeoLabelSet` (1.0.0) | none |
| `petro-properties` | `work-product-component--ReservoirModelScenario` (1.0.0) | none |
| `tank-datum` | `work-product-component--AcquiferInterpretation` (1.1.0, as the service's code spells it) | `aquifer-datum` |
| `fluid-synthesis` | `work-product-component--FluidSystemCharacterization` (1.0.0) | `fluid-synthesis-tank-pvt`, `fluid-synthesis-tank-blackoil` |
| `kr-synthesis` | `work-product-component--PersistedCollection` (1.2.0) | `kr-synthesis-rt`, and `kr-synthesis-kr` below it |
| `phi-k-synthesis` | `work-product-component--PersistedCollection` (1.2.0) | `phi-k-synthesis-rt`, and `phi-k-synthesis-phi-k` below it |
| `forecast` | `work-product-component--ProductionValues` (1.0.0) | `forecast-fluid`, `forecast-det`, and `forecast-det-fluid` below it |

A Kr synthesis and a Phi-K synthesis are the same kind of record, so a DDMS that lists no `collections` sends
`PersistedCollection` records to `phi-k-synthesis`, and a flow delivering Kr syntheses lists
`work-product-component--PersistedCollection: { path: kr-synthesis }`. The historian serves `ProductionValues` too, so a
flow declaring both lists the collections of one of them.

The record's `bulk` part holds its rows, as one or more `.json` files of the tables below its collection, each an array
of rows; a row is an object of its columns, and of the tables below its table:

```json
{
  "phi-k-synthesis-rt": [
    { "rt_tab_name": "RT1", "rt_phi_k_tab": 1,
      "phi-k-synthesis-phi-k": [ { "phie": 0.21, "kgas": 12.5 }, { "phie": 0.18, "kgas": 8.1 } ] }
  ]
}
```

A row gives only the columns its table has, with values of their types (numbers, strings, `true`/`false`; `null` clears
a column), and the columns its table requires (`name` of an aquifer, `rt_tab_name` and `rt_phi_k_tab` of a Phi-K rock
type, `dt` of a forecast step, and the others the brief's section 2.5 lists). It leaves out the columns the route fills
from the rows above it: its own key, the header's (`id_phi_k_synthesis`), the key of the row above it
(`id_phi_k_synthesis_rt`), `parent_object_id` and a forecast's `id_forecast_base`. A record with rows is one of the
service's kinds, with a `data.ParentObjectID` and an id of the collection's pattern. A file that breaks a rule holds the
record before anything is sent.

A record goes to the DDMS serving its entity type: the DDMSs under `target.ddms`, then the Wellbore DDMS under
`ddmsRoot` (or at the endpoint, as above). A record goes to one DDMS, so the loader refuses an entity type two declared
DDMSs serve, and the protocol refuses a registration serving one another DDMS serves. A source declaring `target.ddms`
with no interface on the `ddms` route is refused, and so is `target.ddms` in a single-form flow on another route. A flow
that names its own paths (`recordPath` and the rest) has them used as written, and the collection of its records'
entity type supplies the paths it leaves out.

The run's preflight and `sqlflow check` refuse a mapping whose kind no DDMS the flow reaches serves, and a bulk part
whose records go to a collection that holds records alone; `sqlflow check` says where each flow's records go, and so
does the API's view of a flow's target. [protocols.md](protocols.md#osduwelllog-the-ddms-route) says what the route
sends to each collection.

### External Data Services

External Data Services pulls from an external source and takes no pushed data
([../specs/eds-dms/INTEGRATION.md](../specs/eds-dms/INTEGRATION.md) section 2), so it has no route of its own. What a
flow delivers for it are the records that configure it: connected source registry entries
(`master-data--ConnectedSourceRegistryEntry`), connected source data jobs (`master-data--ConnectedSourceDataJob`) and
proxy datasets (`dataset--External`, `dataset--ConnectedSource.Generic`), through the storage route, or the manifest
route the upstream tools use. A fetch an operator starts is the workflow route running `eds_ingest` for one job or
`eds_scheduler` for every active one ([The workflow route](#the-workflow-route)).

```yaml
target:
  eds:
    checks: true        # default true; false checks nothing
    retrieval: true     # default true; false lets through a registry entry without a DatasetURL
    build: azure        # corePlus | azure | gc; left out, a GcpServiceAccount scheme is held
```

Before anything is sent, every rendered record of those types is checked for what eds-dms and the EDS workflows need,
and a record that breaks a rule is held with every rule it breaks named:

| Record | What is checked |
| --- | --- |
| Registry entry | `data.DatasetURL` is an absolute http(s) URL, and is there unless `retrieval` is false: without it eds-dms answers 500 to every retrieval of the entry's datasets, and an empty one leaves them out without a word. `data.SecuritySchemes` lists at least one scheme, and every scheme, not only the first (eds-dms builds them all on every retrieval), has a `Name` no other scheme has, a `TypeID`, and a `FlowTypeID` naming a flow eds-dms builds, with the keys that flow requires: `ClientCredentials`, `PasswordCredentials`, `RefreshToken` and `AuthorizationCode` with their secret names and an absolute http(s) `TokenUrl`, `GcpServiceAccount` only when `build` is `gc`, and never `Implicit`. |
| Data job | `ConnectedSourceRegistryEntryID` refers to a registry entry with its trailing colon (`<partition>:master-data--ConnectedSourceRegistryEntry:<id>:`); `ActiveIndicator` is true or false; `FetchKind` is a kind the source's search takes; `Filter` is a string (an empty one fetches every record of the kind); `ConnectedSourceDataPartitionID` and `OnIngestionDataPartitionID` are partition ids; `OnIngestionLegalTags` holds legal tags and countries, and `OnIngestionAcl` owners and viewers in Storage's ACL form, since EDS gives them to every record it fetches; `ScheduleUTC` is a cron expression of five or six fields; `LimitRecords`, when given, is a whole number above zero; and one `Workflows` entry is tagged `FETCH`, with the handler `eds_ingest`, an absolute http(s) `Url` and a `SecuritySchemeName`. |
| Proxy dataset | `data.DatasetProperties` holds its four ids, each under an `Id` or an `ID` suffix (the same value when both are given): the registry entry, which must be one; the data job; the source partition; and the source record, as eds-dms sends it to the source (a version after the third colon cut off, and the partition put in front of an id that does not start with it). A record missing one fails every retrieval request that includes it. |

What the engine cannot see is not checked: whether the secrets a registry entry names exist in the partition's Secret
service (no contract of that service is pinned), whether a job's `SecuritySchemeName` names a scheme of its registry
entry (the brief leaves it open), and whether the proxy datasets of one registry entry name one source partition (EDS
writes a proxy for every dataset it fetches with its own job's partition, and only a retrieval request that mixes them
suffers). A proxy dataset names a dataset that stays in the source, so the routes that register files for a record (file,
dataset, and a manifest or workflow route with files) refuse its kind.

EDS writes a data job itself after a fetch: `LastSuccessfulRunDateUTC`, `FailedRecords` and `CreateTimeMax`. On the
storage, manifest and workflow routes those keys join the flow's `preserveDataKeys` for a data job: every update carries
them from the version OSDU holds, and a version whose only changes are in them is not drift
([protocols.md](protocols.md#osdurecord)). A job is stopped by delivering it with `ActiveIndicator: false`, not by removing
it.

A platform can write into a record the same way. Azure Data Manager for Energy adds `data.TechnicalAssuranceTypeID` to a
master-data record within a second of the record being written, under its own identity, so the record takes a version
nobody asked for and a verify reports drift on every delivery (seen live on release 0.29,
[osdu-testing.md](osdu-testing.md) section 0.4). Naming that key under the flow's `preserveDataKeys` is the answer: the
update carries the platform's value forward, and the verify reads the version as another system's rather than as drift.

### The Reservoir DDMS

The Reservoir DDMS keeps RESQML, WITSML and PRODML content as Energistics data objects in dataspaces of its own store,
reached over ETP 1.2 on a WebSocket rather than through an OSDU service
([../specs/reservoir-ddms/INTEGRATION.md](../specs/reservoir-ddms/INTEGRATION.md)). The `etp` route writes those objects.

- A mapping of objects fills a template whose kind has the source `etp`: `{authority}:etp:{type}:{version}`
  (`energistics:etp:obj_Grid2dRepresentation:2.0.1`). The route and the kind go together: the etp route writes only kinds
  with the source `etp`, and no other route writes them.
- **The object is its XML.** A record carries it as its `files` payload (one file), or its mapping renders it into
  `data.Xml`. The store reads the object's identity out of that XML and nothing else, so the route checks it before it
  opens a session: a root `uuid` and `schemaVersion`, a `Citation` directly under the root, a namespace and version the
  store files (RESQML 2.0 and 2.2, EML 2.0 and 2.3, WITSML 2.1, PRODML 2.2), and, for the markup languages whose
  references name their target by content type, the `obj_` spelling those references resolve against. The record's target
  state keeps the URI the object landed at (`etp.uri`), its dataspace, its type and its uuid.
- **The dataspace** is the record's `data.Dataspace`, or the flow's `target.etp.dataspace`. It is created only when it is
  missing, outside any transaction, carrying the record's own ACLs and legal tags, which is what the server registers its
  `dataset--ETPDataspace` record with; that record's id is logged when the dataspace is created. The route never deletes a
  dataspace, because the server purges that OSDU record when it does.
- **The arrays** an object names in its XML (`PathInHdfFile`, `PathInExternalFile`) are declared under `data.Arrays`, each
  with the path the XML names, its transport type, its shape, and its values: inline (`Values`) or a column of the
  record's `bulk` payload (`Column`). An array the XML names and the document does not declare, or the other way round,
  holds the record before anything is sent, because the store refuses the commit of either.
- **One transaction per dataspace** carries a whole batch: the objects, then the arrays that fit a message. An array too
  large for one message is declared in that transaction and filled slice by slice afterwards, each fill its own
  transaction. A commit the store refuses rolls the transaction back and holds the batch with the reason it gave.

```yaml
target:
  endpoint: https://osdu.example.com
  headers: { data-partition-id: dev }
  protocol: etp
  etp:
    dataspace: volve/study
    objectsPerMessage: 100
```

```json
{
  "kind": "energistics:etp:obj_Grid2dRepresentation:2.0.1",
  "acl": { "viewers": ["data.default.viewers@dev.example.com"], "owners": ["data.default.owners@dev.example.com"] },
  "legal": { "legaltags": ["dev-public-usa-dataset-1"], "otherRelevantDataCountries": ["US"], "status": "compliant" },
  "data": {
    "Dataspace": "volve/study",
    "Arrays": [
      { "Path": "RESQML/points", "Type": "arrayOfDouble", "Dimensions": [1000, 3], "Column": "points" }
    ]
  }
}
```

| Key | Meaning |
| --- | --- |
| `path` | Where the ETP WebSocket answers under the endpoint. Default `/api/reservoir-ddms-etp/v2/`, where an OSDU deployment serves it. |
| `dataspace` | The dataspace a record goes into when its document names none. At least three characters of letters, digits and `_ - . /`; two levels (`project/study`) are recommended. |
| `objectsPerMessage` | Objects per message, which is also the batch the worker hands the route. Default 100, as the Reservoir DDMS's own REST gateway sends. |
| `maxMessageBytes` | The largest message this side offers to send or accept. The session settles on whichever side allows less. Default 16,000,000, the server's own default. |
| `maxArrayBytes` | The most bytes of one array a delivery reads into memory before it holds the record instead. Default 268,435,456. |
| `lock` | Whether a delivery leaves the dataspace locked, which makes it read-only until the next delivery unlocks it. Off by default: a locked dataspace refuses every write, including this flow's next one. |

An array's values come from one place: `Values` in the document, or `Column` of the `bulk` payload, never both. A
column's parquet values are converted to the declared transport type, and a column whose values that type does not take
holds the record. `Type` is one of `arrayOfBoolean`, `arrayOfInt`, `arrayOfLong`, `arrayOfFloat`, `arrayOfDouble` and
`bytes`; `arrayOfString` is written but never read back by the store, so the route does not send one. `Uri` names the
object an array hangs under when it is not the record's own object (a RESQML 2.0.1 array hangs under the
`EpcExternalPartReference` its representation names).

The Reservoir DDMS creates objects of its own alongside a delivery: at each commit it writes a
`resqml20.obj_Activity` naming the objects that are new to the dataspace as its outputs, and, once per dataspace, the
`obj_ActivityTemplate` it belongs to. They carry uuids the server minted and are not this flow's records; a verify looks
only at the types the batch delivered, so they do not disturb it, and a removal leaves them where they are.

A record on this route has no OSDU version: the store keeps only an object's latest content. A verify lists the
dataspace's resources and compares the store's last write with the one the delivery recorded, and a removal deletes the
object outright (the everything scope); the record and history scopes are refused, as they are for DSPDM rows.

### The Production DDMS core service

The Production DDMS core service (DSPDM) keeps production data as rows of business objects in its own database, not as
OSDU records ([../specs/production-dspdm/INTEGRATION.md](../specs/production-dspdm/INTEGRATION.md) section 2). The
`dspdm` route writes those rows.

- A mapping of rows fills a template whose kind has the source `dspdm`: `{authority}:dspdm:{entity}:{version}`
  (`acme:dspdm:well_test:1.0.0`), the entity being the business object's entity (its table). The template is imported
  like any other (`sqlflow template import`), from a JSON schema whose `data` properties are the business object's
  attributes.
- Its entries fill the attributes as properties of `osdu.data` (`osdu.data.UWI`) and nothing else. A row has no access or
  legal block, so the loader refuses an envelope entry, and any target outside `osdu.data`, in a mapping of a DSPDM kind.
  Attribute names are read in upper case, as DSPDM reads them.
- The route and the kind go together: the dspdm route writes only kinds with the source `dspdm`, and no other route
  writes them.

```yaml
target:
  endpoint: https://osdu.example.com
  headers: { data-partition-id: dev }
  protocol: dspdm
  dspdm:
    root: /api/dspdm/v1
    timezone: GMT+02:00
    existingRows: hold
    businessObjects:
      well_test: { name: WELL TEST, key: [UWI, TEST_NUM] }
```

| Key | Meaning |
| --- | --- |
| `root` | Where DSPDM is under the endpoint (`/api/dspdm/v1` behind the GC gateway). Left out when the endpoint is DSPDM itself. |
| `timezone` | The zone every request names: `GMT+hh:mm` or `GMT-hh:mm`, from GMT-12:00 to GMT+14:00, the only form DSPDM's save takes. Default `GMT+00:00`. DSPDM moves a date and time written in the ISO form with an offset into this zone, and keeps any other form as written. |
| `businessObjects.<entity>.name` | The business object the rows of that entity are. Default: the entity in upper case, a space for each `_` (`well_test` is `WELL TEST`). |
| `businessObjects.<entity>.key` | The attributes a row is found again by, which must be one of the business object's unique constraints. Default: its one unique constraint. |
| `existingRows` | What happens to a row that holds a record's key when the record did not write it (a row another system wrote, a row loaded before the flow existed, or the row of another record that renders the same key). `hold`, the default, holds the record and names the row. `update` updates the row and keeps it as the record's, to take over rows loaded before. |

Before a run, the route reads each business object from DSPDM's metadata (`BUSINESS OBJECT`, `BUSINESS OBJECT ATTR`,
`BUS OBJ ATTR UNIQ CONSTRAINTS`). It refuses a business object it cannot write:

- a name DSPDM does not know, or another entity than the kind names;
- an inactive business object, or a metadata or equipment catalog table;
- a primary key that is not one whole number;
- rows that cannot be found again by one unique constraint: there is none, there are several and `key` names none of
  them, or `key` is not one of them or names the primary key.

A row is checked before it is sent, and held with every reason:

- an attribute the business object does not have, or one DSPDM keeps itself (its primary key, the four audit attributes,
  a read-only attribute);
- a value its data type does not take: a number, a whole number in range, text within its length, a decimal within its
  precision, a date or time in a form DSPDM reads, true or false (`Y`, `N`, `1`, `0` as text);
- an empty key attribute;
- a mandatory attribute missing from an insert, or cleared by an update.

Two records of one delivery that are one row hold the later one.

Removal: DSPDM keeps no deleted rows and no versions. The `everything` scope deletes a record's row for good; the
`record` and `history` scopes are refused. So a live test of this route cannot be cleaned up by a soft delete, and
needs its own approval.

What the route relies on and cannot see:

- DSPDM's shipped settings: `read_before_update` and `do_tiny_update` true, `use_utc_timezone_to_save` and
  `use_client_timezone_to_display` false. With `do_tiny_update` off, an update rewrites every attribute of a row,
  clearing the ones the mapping does not fill. With the time zone settings changed, a date key or a version reads
  differently.
- Whether DSPDM runs on the target deployment, and which business objects and constraints it holds, are known only there.

### Parameters

Flow parameters are supplied by `--set name=value` or by the run's values. `{name}` tokens are substituted in
`source.work` and each `source.payloads[].root`, in a retrieval flow's `source.query` and `target.location`, and in a
cache flow's `types[].query`. `source.record.scope` binds a parameter to a record column instead: each entry becomes
a typed `[column] = @p` predicate, so the value is bound and never substituted into SQL.

### Schedules

The inline `schedule` fires the flow on the platform scheduler; `operation` (deliver by default; verify, plan,
intake, drain or replan; retrieve or plan on a retrieval flow; refresh or plan on a cache flow) is what every fire runs, and `values`
supplies the flow's own parameters. A flow that declares a required parameter **must** give the schedule values for
it: a fire supplies nothing on its own, so without them every run fails validation with "parameter 'name' is
required". A run-now's values override the schedule's name by name, leaving the rest in place. A nightly drift pass is a second schedule in the repository's schedule
library with `operation: verify` and the flow as its member. Run-now on a schedule keeps its operation and adds
`force`.

## A source with interfaces

A delivery document can also describe a whole source system: the connection, the target and the defaults once, and
under `interfaces` one entry for each OSDU type the source delivers
([../../docs/interfaces-design.md](../../docs/interfaces-design.md)). Both forms load into one model and run on one
engine. A document without `interfaces` (the form above) is a source with one interface and keeps the ledger of its
own name. A type that is better managed on its own, or delivered again often, can stay in a document of its own.

```yaml
flowType: delivery
name: wells                          # the source: one pipeline in the catalog, one schedule, one run
parameters:
  logSource: { required: true }
schedule: { cron: "0 * * * *", values: { logSource: STAT_COMP } }

source:                               # shared by every interface
  connection: ${env:OSDU_SAMPLE_DB}
  lastModified: update_date
  work: ../.work/wells/{logSource}
render:
  parameters: { dataPartition: dev }
target:
  endpoint: ${env:OSDU_URL}
  auth: { ... }
  headers: { data-partition-id: dev }
  protocolOptions:
    ddmsRoot: /api/os-wellbore-ddms   # where the DDMS sits under the endpoint, for the interfaces delivered through one
reliability:
  concurrency: 8
  fanOut: 2
  parallelInterfaces: 4               # how many interfaces of one wave run at once (1 to 32, default 4)
failWhen: { failedPercent: 20 }       # every interface's stop rules, unless it sets its own

interfaces:
  wellbores:
    ledger: wells-wellbore-03-header-delivery           # keep the ledger of the flow this interface replaces
    record: { object: OsduSample.ing.Wellbore, key: [facility_name], primaryKey: RecId }
    datasets:
      aliases: { object: OsduSample.ing.WellboreAlias, join: { facility_name: facility_name }, orderBy: [alias_name] }
    mapping: Wellbore@1.0.0
  welllogs:
    ledger: wells-welllog-03-header-delivery
    record: { object: OsduSample.ing.WellLog, key: [source_project, log_id], primaryKey: RecId, scope: { log_source: logSource } }
    datasets:
      curves: { object: OsduSample.ing.WellLogCurve, join: { source_project: source_project, log_id: log_id }, orderBy: [curve_ordinal] }
    bulk: { root: ../data/curves, locationColumn: curve_folder, pattern: "chunk_*.parquet", hashColumn: payload_hash, chunkCountColumn: chunk_count }
    protocolOptions: { sessionThresholdChunks: 1 }
    mapping: WellLog@1.4.0          # fills osdu.data.WellboreID, so it waits for wellbores without an after:
    failWhen: { consecutiveFailures: 50 }
```

### What an interface declares

| Key | Meaning |
| --- | --- |
| `record` | The interface's record table, as `source.record` declares it: `object`, `key`, `primaryKey`, `scope`. Required. |
| `datasets` | Its child tables, as `source.datasets`. |
| `mapping` | The pinned mapping (`Name@version`), as `render.mapping`. Required. |
| `files` | The files each record carries: a payload set (`root`, `locationColumn`, `pattern`, `hashColumn`, `chunkCountColumn`), uploaded and registered before the record, or, on the dataset route, as the record. |
| `bulk` | The DDMS bulk data each record carries, a payload set of the same shape, written after the record. |
| `workflow` | The workflow each record starts ([The workflow route](#the-workflow-route)). |
| `route` | `storage`, `file`, `dataset`, `manifest`, `ddms`, `fileAndDdms`, `manifestAndDdms` or `workflow`: names the route instead of letting what the interface declares decide it. |
| `ledger` | The name of an existing flow whose ledger the interface keeps. |
| `after` | Interfaces of this document it waits for, beside the ones its records refer to ([Order](#order)). |
| `failWhen` | Its stop rules, laid over the source's. |
| `description` | Free text. |
| `lastModified`, `systemColumns`, `incremental` | The source's settings, overridden for this interface. |
| `render` | `cacheVersion`, and `parameters` merged over the source's. |
| `change`, `reliability`, `verify`, `protocolOptions` | The source's blocks, with the interface's keys laid over them. |

An interface's block is laid over the source's key by key: a key the interface sets replaces the source's, a key it
leaves out keeps the source's value, a nested block (`reliability.retry`) is laid over the same way, a map
(`render.parameters`, `protocolOptions.uploadHeaders`, `protocolOptions.workflowPayload`) is merged key by key, and a
list (`reliability.skipStatusCodes`, `protocolOptions.preserveDataKeys`) replaces the source's list whole. `systemColumns`
is merged column by column, and a column the interface opts out of (`fileName: ~`) stays opted out for it. What one
interface lays over the shared blocks never reaches another. Every message about an interface names its own keys
(`interfaces.welllogs.record.key`).

What belongs to an interface is refused at the source level, each named: `source.record`, `source.datasets`,
`source.payloads`, `render.mapping`, `target.protocol` and `target.protocolOptions.payload`.
`reliability.parallelInterfaces` is the source's own setting: it is refused on an interface and in a document that
declares no interfaces.

An interface name is a letter followed by letters, digits, `_` and `-`, at most 64 characters, and unique in its
document regardless of case. A document declares at most 200 interfaces.

### Routes

What an interface's records carry decides how they are delivered:

| The interface declares | Route | Each record |
| --- | --- | --- |
| no `files`, no `bulk` | `storage` | is written through the storage service. |
| `files` | `file` | has its files uploaded and registered through the file service, then is written through the storage service. |
| `files` and `route: dataset` | `dataset` | has its files stored and registered through the dataset service: a record of a dataset kind is that dataset, any other refers to one dataset holding its files. |
| `bulk` | `ddms` | is written through its DDMS, then its bulk data. |
| `files` and `bulk` | `fileAndDdms` | has its files uploaded and registered through the file service, is written through its DDMS referring to them, then its bulk data. |
| `route: manifest`, with or without `files` | `manifest` | has its files registered first, then goes through the ingestion workflow in manifests. |
| `route: manifest` and `bulk`, with or without `files` | `manifestAndDdms` | goes through the ingestion workflow in manifests, its files registered first, then its bulk data through its DDMS. |
| `workflow` | `workflow` | is written first, its inputs registered, then the workflow runs in stages and what it wrote is read back. |
| `route: dspdm` | `dspdm` | is a row of a Production DDMS business object: found again by its unique key, then saved through DSPDM ([The Production DDMS core service](#the-production-ddms-core-service)). |
| `route: etp`, with or without `files` and `bulk` | `etp` | is an Energistics object in a dataspace of the Reservoir DDMS, written over ETP 1.2 on a WebSocket with the arrays it names ([The Reservoir DDMS](#the-reservoir-ddms)). |

`route:` names a route outright. A named route and a part only another route sends make the two routes' composition:
`file` with `bulk` and `ddms` with `files` are `fileAndDdms`, and `manifest` with `bulk` is `manifestAndDdms`. A route
that cannot deliver what the interface declares is refused when the document loads: `storage` with `files` or `bulk`,
`file` or `dataset` without `files`, `dataset` or `workflow` with `bulk`, `dspdm` with `files` or `bulk`, a `workflow`
block on any other route, and a composed route without both of its parts. A source's `target.dspdm` goes to its `dspdm`
interfaces, and its `target.etp` to its `etp` interfaces; each is refused when no interface takes that route. An `etp`
interface may declare `files` (the object's XML), `bulk` (the values of its arrays), both or neither, since a mapping
can render either into the document instead.

A `ddms` interface needs to know where its DDMS is: a DDMS under `target.ddms`, `ddmsRoot` under the source's or its
own `protocolOptions`, or paths of its own (`recordPath` and the rest). Its records go to the collection serving their
entity type ([The DDMSs a flow delivers to](#the-ddmss-a-flow-delivers-to)), and the run's preflight refuses a mapping
whose kind no DDMS the interface reaches serves. The same applies to a flow in the single form whose `target.protocol`
is `ddms`. The routes are the protocols of [protocols.md](protocols.md); a document without interfaces names its route
with `target.protocol`, as a route type or as the protocol it maps onto, and declares its payload sets under
`source.payloads` with the names `files` and `bulk` where a route sends both.

#### Files and bulk data of one record

`fileAndDdms` and `manifestAndDdms` send a record's files and its bulk data as two parts. Each part goes when its content
hash moves or a redelivery names it: new curves do not upload the record's files again, and new files do not send its
curves. A record that already holds bulk data keeps the DDMS's link to it on every rewrite, including a manifest's. The
files go as `filesContentType`, or `application/octet-stream`, and the bulk data as `payloadContentType`.
`protocolOptions.payload` is refused on these routes: the parts go by their own names.

#### Manifests by reference

`protocolOptions.manifestByReference` sends a manifest to `byReferenceWorkflowName` as a stored dataset instead of
inline: `always`, or `auto` for a trigger request above `manifestInlineLimitKb` on a partition that registers the
workflow (a partition that does not has the batch split into manifests under the limit). The stored manifest is removed
reversibly once its run has settled. Only the manifest routes take the option.

#### The dataset route

The dataset route takes `files` and stores them where the dataset service says, the way the provider that signed the
location takes them. A record of a `dataset--File.*` kind carries one file, a `dataset--FileCollection.*` record any
number under its directory. A record of another kind refers to one dataset of `protocolOptions.datasetKind`, which has
to be a dataset kind, registered under an id of its own made from the record's (the record's key with `-files` after it,
under the dataset's entity type), so new files land on the same dataset.

### The workflow route

```yaml
interfaces:
  resqml:
    record: { object: Src.ing.EpcPackage, key: [package_id], primaryKey: RecId }
    files: { root: ../data/epc, locationColumn: epc_folder, pattern: "*.epc", hashColumn: epc_hash }
    mapping: EpcPackage@1.0.0              # the dataset--File.Generic each package is registered as
    workflow:
      anchor: dataset                      # dataset (the default when files are declared) | storage
      anchorTag: osduDeliveryAnchor        # optional: tags.<key> = osdu-delivery-<24 hex> on the record, {anchorTag} in a template
      runWhen: changed                     # changed (the default) | created | requested
      inputs:                              # optional, at most 8: payload sets registered through the dataset service
        h5:
          root: ../data/epc
          locationColumn: epc_folder
          pattern: "*.h5"
          hashColumn: h5_hash
          datasetKind: osdu:wks:dataset--File.Generic:1.0.0
          optional: true                   # a package without HDF5 files sends an empty input
      secrets: {}                          # names for {secret:name}, each a ${env:...} or ${keyvault:...} reference
      stages:                              # 1 to 4, run in order
        - workflow: Energyml_Converter     # the name this partition registers it under
          timeoutMinutes: 120              # 0 or left out: the longer of workflowTimeoutMinutes and the contract's
          pollSeconds: 30                  # 0 or left out: protocolOptions.workflowPollSeconds
          context:
            dataset_xml: ["{record:id}"]
            dataset_h5: "{input:h5}"
            data_partition_id: "{partition}"
            tags_every_entity_keys: ["{anchorTag}"]
          outputs:                         # read by later stages and the results as {stage:1.<name>}
            manifestId:
              xcom: { task: update_status_finished_task, key: saved_record_ids, match: dataset--File.Generic }
        - workflow: Osdu_ingest_by_reference   # the converter only translates, so a stage ingests its manifest
          context:
            manifest: "{stage:1.manifestId}"
      results:                             # one way to find what the runs wrote, or none
        search: { kind: "*:*:*--*:*.*.*", query: "data.Tags:\"{anchorTag}\"" }
        minimum: 1                         # fewer found fails the try (default 1; 0 for none or anchor)
        waitSeconds: 300                   # how long a search waits for the index (default datasetIndexWaitSeconds)
        keep: 100                          # ids kept on the record (at most 1000)
        remove: true                       # a removal takes them with the record
```

| Key | Meaning |
| --- | --- |
| `anchor` | How the record is written first: `dataset`, registered with its `files` through the dataset service, or `storage`. |
| `anchorTag` | A tag key (a letter, then letters, digits and `_`, at most 64 characters) the route writes on the record with a value derived from its id, `{anchorTag}` in a template. |
| `runWhen` | When a delivery starts the runs: `changed`, when it writes the record or an input; `created`, only for a new record; `requested`, only when a redelivery names `workflow`. |
| `inputs.<name>` | A payload set (a letter, then letters, digits, `_` and `-`, at most 32 characters) registered as one dataset per file, or one collection, under an id derived from the record's; `datasetKind` (default `osdu:wks:dataset--File.Generic:1.0.0`) and `optional`. |
| `secrets.<name>` | A `${env:...}` or `${keyvault:...}` reference a context names as `{secret:name}`. It is resolved only for the trigger request, and every step and message shows `***`. |
| `stages[].workflow` | The workflow as the partition registers it. The run's preflight asks the Workflow service for it. |
| `stages[].contract` | The known workflow whose payload contract the context follows, when `workflow` is not one of its names: `osduIngest`, `osduIngestByReference`, `csvParser`, `energymlConverter`, `energymlDelivery`, `enyparserTranslation`, `segyToVds`, `segyToZgy`, `segyToMdio`, `edsIngest`, `edsScheduler`, `edsNaturalization`. The Workflow service's test DAG is refused. A workflow that only translates is followed by a stage that ingests its manifest. |
| `stages[].context` | The execution context, any YAML, with placeholders in its strings. It is checked against the contract when the document loads, and filled and checked again before every trigger. `Payload {AppKey, data-partition-id}` is added when the context leaves it out and the workflow reads it. |
| `stages[].timeoutMinutes`, `pollSeconds` | How long the run may take (at most a week; by default the longer of `workflowTimeoutMinutes` and the contract's) and how often it is polled (at most an hour; by default `workflowPollSeconds`). |
| `stages[].outputs.<name>` | A `value` template, or an `xcom` entry: `task`, `key`, and `match`, the entity type the record ids taken from it must have (every id when left out). The entry is read through the Workflow service's `latestInfo`, which serves the run's latest task only, or from Airflow's REST API when `target.airflow` is declared. |
| `results` | One of `anchor: true` (the run writes the record), `ids` (a template), `artefact` (`role`, `kind`: the record's `data.Artefacts`), `search` (`kind`, `query`), `manifest` (a template naming a manifest dataset) or `xcom`; with `minimum`, `waitSeconds`, `keep` and `remove`. |

Placeholders: `{partition}`, `{appKey}`, `{runId}`, `{anchorTag}`, `{record:path}` (a value of the record:
`data.Name`, `data.Curves[0].CurveID`, `data.Curves[*].CurveID`, `data.Parameters[Title=work_product_id].DataObjectParameter`),
`{input:name}` or `{input:name[n]}` (the ids an input was registered as), `{dataset:suffix}` (a `dataset--File.Generic`
id derived from the record's), `{stage:n.output}` (an output of an earlier stage) and `{secret:name}`. The modifiers
`|id` (without the version), `|ref` (with a trailing `:`), `|list`, `|first` and `|json` shape the value. A string that
is one placeholder takes the value's JSON shape; `{{` and `}}` are literal braces. The templates are checked when the
document loads: a placeholder that names a secret, an input or an output the route does not have, or a stage that has
not run by then, is refused.

### Ledger identity

Every interface keeps a ledger of its own: its records, submissions, watermark, OSDU id claims and statistics are
kept under the flow id derived from `<flow>/<interface>` (as the ledger records it, `wells/welllogs`; ids ignore case).
A document without interfaces keeps the flow id of its own name.

`ledger: <name>` keeps the ledger of an existing flow instead, so consolidating single-kind flows into one source loses
no history and sends nothing again that has not changed. Remove the flow whose ledger was adopted: while two flows keep
one ledger, the repository sync warns, naming both. Two interfaces of one document never keep the same ledger, and a
ledger name is at most 200 characters.

### Order

An interface waits for another in two cases:

- **Its records refer to what the other delivers.** A property its mapping fills (from a column, the cache, a search
  or a static value) refers to other records when the template's schema says so with `x-osdu-relationship`: `osdu.data.WellboreID`
  of a well log refers to `master-data--Wellbore`. The interface then waits for every other interface of the source
  whose mapping fills that entity type. A relationship that names only a group (`Datasets[]` refers to `dataset`) waits
  for every interface delivering a kind of that group. A reference to a kind no other interface delivers is not waited
  for: those records are OSDU's already, or another source's.
- **`after:` names the other.** It adds what the schemas do not show.

A run takes the interfaces in waves: the first wave holds every interface that waits for nothing, and each later wave
the interfaces whose dependencies all ran before it. Within a wave the interfaces are taken by OSDU group (reference
data, master data, datasets, work product components, work products, then any other group) and then as the document
lists them; up to `reliability.parallelInterfaces` of them run at once. Each still plans, fans out and drains as a run
of that interface alone does.

OSDU's schemas refer both ways (a wellbore to its definitive trajectory, the trajectory to its wellbore), so two
interfaces can wait for each other. Such a cycle is cut, and the reference that is not waited for is reported with why:

1. A reference the document's `after:` orders the other way is not waited for.
2. Otherwise, a reference from a group that comes earlier in the order above to a later group is not waited for (the
   wellbores run before the trajectories). The reference points back, and it resolves once the other interface has
   delivered.
3. Interfaces of one group that refer to each other are refused, naming them and the properties involved, until
   `after:` says which one waits.

The references are read from the mappings and the templates they pin, which live in the catalog, so the order is worked
out when a run starts (its preflight refuses a cycle nothing cuts), by `sqlflow check`, and by the API's listing of a
flow's interfaces. When the document is read, only `after:` is checked: an `after:` naming an interface the document
does not declare, the interface itself, or one interface twice is refused, and so are interfaces whose `after:` wait
for each other (`the interfaces a -> b -> a wait for each other`).

### Records that wait for records

Order between interfaces is not the whole story: one record can be ready while the record it refers to is not. When a
record is rendered, the OSDU ids its relationship properties hold are kept beside its document. A claim leaves the
record **waiting** when one of those ids belongs to a record the ledger holds and has not delivered, saying which
record it waits for; it is sent when that record lands, and nothing is charged for the wait
([ledger.md](ledger.md#record-lifecycle)). An id no record of the ledger holds is not waited for, since it is OSDU's
or another system's; nor is a reference the order above does not wait for, because it points back.

`target.verifyReferences: storage` goes further for a source that must not write dangling references: the ids no
record of the ledger holds are asked of OSDU's storage service before the record is sent, and a record naming one
storage does not hold is held with the ids and the properties. The dspdm route writes rows that refer to no storage
record, so a flow it delivers is refused the setting.

### When an interface stops

A record with a data problem is held and the interface goes on. `failWhen` says when the records' failures add up to
a failure of the interface itself:

| Key | The interface stops when | Default |
| --- | --- | --- |
| `outageFailures` | this many records in a row could not reach the service (a transport failure, a timeout, HTTP 408, 429 or 5xx) or were refused by it (HTTP 401 or 403), with none delivered in between. 0 turns the rule off. | 25 |
| `consecutiveFailures` | this many records in a row failed with problems of one class (data, connection or permission), with none delivered in between. | not set |
| `failedPercent` | held and failed records reach this share (above 0, at most 100) of the records the run settled, judged once `minRecords` have settled. A planning pass that holds this share of the records it took up stops the interface before it sends anything. | not set |
| `minRecords` | how many records have to settle before `failedPercent` is judged. | 100 |

A record scheduled for another try counts toward the failures in a row, not toward the share. A stopped interface
stops its work, hands back the records it had not sent without charging them a try, and closes its submission as
failed (`stopped: <why>`), so its next deliver run sends what is left. The interfaces waiting for it are skipped, and
the others carry on ([operations.md](operations.md#running-a-source)). `failWhen` applies to a document without
interfaces too, whose run then ends failed with the reason.

## Retrieval flow

The reverse direction ([design.md](design.md) section 15): OSDU's search index into JSON Lines files on the lake.

```yaml
flowType: retrieval
name: wellbores-out
parameters:
  region: { required: true }

source:
  endpoint: ${env:OSDU_URL}
  auth: { type: oauth2ClientCredentials, secondarySecretRef: ${env:OSDU_CLIENT_ID}, secretRef: ${env:OSDU_CLIENT_SECRET}, token: { url: ${env:OSDU_TOKEN_URL} } }   # as target.auth on a delivery flow
  headers: { data-partition-id: dev }   # required, as on a delivery flow's target
  kinds:                             # one cursor per kind; `kind:` for a single one
    - "osdu:wks:master-data--Wellbore:1.*.*"
    - "osdu:wks:master-data--Well:1.*.*"
  query: 'data.GeoPoliticalEntityID:"{region}"'   # Lucene, optional; {parameter} tokens
  returnedFields: []                 # project the hits; empty returns whole hits
  pageSize: 1000                     # at most 1000
  incremental:                       # optional; without it every run takes everything the query matches
    field: modifyTime
    since: 2026-01-01T00:00:00Z      # where the first (and a forced) run starts; omitted means from the beginning
    lagMinutes: 5
  fetchRecords: false                # read every hit's full record back from storage
  fetchParallelism: 4                # storage read-backs in flight per page (a hundred ids each)
  searchPath: /api/search/v2/query_with_cursor
  queryPath: /api/search/v2/query    # the plan operation counts here
  recordQueryPath: /api/storage/v2/query/records
  probePath: /api/search/v2/info

target:
  location: abfss://lake@acct.dfs.core.windows.net/osdu-out/{region}   # {run} and {date} tokens too
  format: jsonl
  compression: gzip                  # none | gzip
  rollRecords: 100000                # records per file
  manifest: manifest.json

reliability: { concurrency: 4, retry: { attempts: 4 } }   # kinds retrieved at once; the HTTP settings as on a delivery flow
schedule: { cron: "0 3 * * *", timezone: UTC, operation: retrieve }
```

| Key | Meaning |
| --- | --- |
| `source.kinds` | The kinds to retrieve, `authority:source:entityType:version` with wildcards per segment. Each is one cursor; they run concurrently up to `reliability.concurrency`. |
| `source.query` | A Lucene query narrowing the kinds. The window of an incremental flow is appended as `AND field:[from TO to}`. |
| `source.incremental` | The watermark: a run covers `[last completed run's upper bound, now minus lag)` on `field`. Only a completed run advances it; `--force` restarts at `since`. |
| `source.fetchRecords` | The index holds a projection; set this to land the record as storage holds it. Ids storage cannot return are counted and listed in the manifest. |
| `target.location` | The run's directory root. Without a `{run}` token every run gets a timestamped directory beneath it, so runs never overwrite each other. A relative local path is resolved against the working directory of the process running the flow, not the flow file, so the repository sync warns about one: use an absolute path or a storage URI. |
| `target.rollRecords` | A new file every this many records: `part-00001.jsonl[.gz]`, `part-00002...` under a directory named after the kind. |

A run's directory holds the files per kind and the manifest: the flow, the run, the window, every file with its
record count and uncompressed bytes, and per kind the records storage could not read back. The ledger's
`osdu.Retrieval` row carries the same counts, the outcome and the run id; the pipeline's Retrievals tab lists
them. The operations are `retrieve` (the default for a retrieval flow) and `plan` (count what the query matches,
write nothing).

A retrieval flow lands records as files and nothing else. The cache the mappings resolve against is defined and
captured by a cache flow.

## Cache flow

The reference data the mappings resolve against ([design.md](design.md) section 6.2). A cache flow is the one place what
is cached is defined: the OSDU platform to search, the types to cache, and for each type the paths of a record to keep.
A cache is for closed vocabularies a capture can hold whole; records that are business data, such as the wellbores a
well log names, are searched for by the mapping as each record needs one ([findBy and a search](#findby-and-a-search)). It fills the cache of the partition its `source.headers.data-partition-id` names, and a delivery flow
reads the cache of the partition it delivers to ([The partition cache](#the-partition-cache)). The sample estate's
cache flow, `samples/wells/cache/wells-osdu-00-reference-cache.yaml`, fills partition `dev`:

```yaml
flowType: cache
name: wells-osdu-00-reference-cache
batch: wells

source:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body:
        scope: ${env:OSDU_SCOPE}
  headers:
    data-partition-id: dev

# A changed cached value rewrites the documents built from it. By default (onChange: auto) the affected records are tagged
# and the next run delivers them. Where a change should be looked at first, onChange: approve (here for every type, or on
# one type) holds the affected records until someone approves the update on the OSDU cache page.

types:
  - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
    name: UnitOfMeasure
    fields: [data.Code, data.Name, data.ID]
  - kind: "osdu:wks:reference-data--LogCurveBusinessValue:*"
    name: LogCurveBusinessValue
    fields: [data.Code, data.Name]
  - kind: "osdu:wks:reference-data--VerticalMeasurementType:*"
    name: VerticalMeasurementType
    fields: [data.Code, data.Name]
  - kind: "osdu:wks:reference-data--TrajectoryStationPropertyType:*"
    name: TrajectoryStationPropertyType
    fields: [data.Code, data.Name, data.ID]

reliability:
  retry: { attempts: 4, backoff: exponential, baseDelayMs: 500, maxDelayMs: 30000 }
  timeoutSeconds: 100

# Nightly, well before the hourly delivery runs, so a delivery renders against a cache captured the same day.
schedule:
  cron: "0 2 * * *"
```

| Key | Meaning |
| --- | --- |
| `name` | Required. The cache flow's name: its pipeline identity, and the name the versions it writes and the records it holds in the partition's cache are recorded under. A cache flow is named globally: the sync warns when a second file in the repository declares the same name (the first file wins), and when another repository declares it too (rename one of them). |
| `description` | Optional text describing the cache. |
| `parameters` | Optional, as on a delivery flow; `{name}` tokens usable in a type's `query`. A token no parameter declares is refused. |
| `source.endpoint`, `source.auth`, `source.headers` | The OSDU platform the types are searched on, written as `target` is on a delivery flow: `${env:NAME}` and `${keyvault:vault/secret}` references and the same auth types. `data-partition-id` is required, because every search carries it, and it names the partition whose cache the flow fills: an id segment (letters, digits, underscore, hyphen and dot) or a `${env:...}` or `${keyvault:...}` reference. |
| `types` | Required, at least one: the OSDU types the flow caches. Each type's name is unique within the flow, because a mapping reads a type by its name. Another cache flow of the same partition may declare the same name, and the cache then holds one type under it ([The partition cache](#the-partition-cache)). |
| `types[].kind` | Required. The kind searched, `authority:source:entityType:version` with wildcards per segment. |
| `types[].name`, `types[].entityType` | Optional. The entity type is derived from the kind, and the name from the entity type (`reference-data--UnitOfMeasure` gives `UnitOfMeasure`). A kind that names no entity type needs `entityType`. |
| `types[].query` | Optional Lucene query narrowing the type; `*` when omitted. |
| `types[].fields` | Required: the paths to keep, written bare (`data.Code`, cached as `Code`) or as `{ path: ..., as: ... }`. Whatever a path yields is cached as it is: a scalar, a set of values, or a nested object. A path crosses arrays implicitly, so `data.NameAliases.AliasName` reaches through an array of objects and caches the set of aliases it finds. A path that yields nothing on every record is reported at capture. |
| `onChange`, `types[].onChange` | What a changed cached value does to the records already built from it. `auto` (the default) tags them and lets the next run carry the new document; `approve` is an option that tags them and holds them back until someone approves the update on the OSDU cache page. Set for the flow and overridden per type, so a single type whose changes should be looked at first can opt in while the rest update on their own. When several cache flows of a partition declare a type, its changes wait for approval when any of them says `approve`. |
| `reliability` | The HTTP settings, as on a delivery flow. |
| `schedule` | The platform envelope, as on every flow; a fire runs a refresh. |

A cache flow's operations are `refresh` and `plan`. `refresh` is the default: a run triggered without an operation, a
scheduled fire and a run asking for `deliver` all refresh. `plan` counts what each type's search matches and writes
nothing. A cache flow takes no submission, record or slice scope; only its parameter values.

A refresh sweeps every declared type in full through the search cursor, because a cache holding only the last hour's
changes cannot answer a lookup, and keeps for every hit each path the partition's cache keeps for the type: the paths
this flow declares, and those any other synced cache flow of the partition declares for a type of the same name. It
then merges the capture into the partition's cache and writes the next version, labelled from the capture instant
(`20260908T212727Z`, with the sequence appended when two captures of the partition share a second, as in
`20260908T212727Z-7`), unless the merge changes no cached content: then no version is written, and nothing built from
the cache renders again. The newest version is always the current one. A version records the cache flow that wrote it,
the run that captured it and who asked, and it is kept for as long as the catalog exists, because the render context
of a delivered record names the version it was rendered against. A refresh therefore needs the catalog connection.
Nothing about a cache is written to the repository: the files define what is cached, and their runs fill the catalog
([ledger.md](ledger.md)).

Every refresh also reads the partition's system properties: the settings the platform's indexer and search service
report for the partition from `GET /api/indexer/v2/info` and `GET /api/search/v2/info` (their `featureFlagStates`),
such as whether the indexer keeps a lowercased copy of every text property (`featureFlag.keywordLower.enabled`). They
are tagged as system properties and kept with the version, apart from the cached records: they are neither reference
nor master data, have no record id, and no mapping reads them with `cache.<Type>.<field>`. The engine relies on one of
them: where keywordLower is on, a search that finds no record exactly asks again regardless of case
([findBy and a search](#findby-and-a-search)).

- A state a service reports for the partition wins over one it reports for no partition, and states for other
  partitions are ignored.
- A service that cannot be asked, or does not publish its settings, fails nothing: the refresh carries on, logs why,
  and the version keeps what the cache knew of that service. A property the engine relies on that no service reports is
  recorded as unknown, with the reason, and nothing relies on it being on.
- A property whose state changed is a change of the cache, so the refresh writes a version even when every cached
  record is the same. What a service explained a state with is not: the words change without the setting changing.
  One read that fails never writes a version.
- `sqlflow cache import` asks no platform and keeps the current properties.

The OSDU cache page lists them on its System properties tab, with the service that reported each, its state, what it was
taken from and why it is unknown, and `sqlflow cache list <partition>` prints those of the current version.

A refresh does not only write a version. Every delivered record points at the set of cached values it was built
from, so the refresh compares the new version against the one it replaces and raises one tag per changed value: the
partition, the cached record, the path, the value the replaced version held and the one the new version holds, and how
many delivered records it reaches. Each set is judged by the value it holds, so a set already built from the new value
is not touched. A tag under `approve` holds those records back (a plan skips them, so OSDU keeps the documents it has)
until someone approves or rejects it; a tag under `auto` is approved as it is written. If a value moves again after
approval but before the rollout carried it, the tag reopens, because the approval was for the value someone looked
at.

An approved change is carried out in batches by the control plane (`ControlPlane:CacheRollout`: `BatchSize`
records per batch, `BatchesPerPass` batches every `PollSeconds`), so a change reaching millions of records drains
at a set pace rather than in one statement, and resumes where it stopped after a restart. The redelivery is
metadata only: a cached value that changed rewrites the manifest row and never re-uploads its payload.

The GUI's OSDU cache page shows all of it, partition by partition: which cache flow files fill it and what each
declares, the versions they wrote and what each changed, the records of any version with the `cache.<Type>.<name>`
sources a mapping reads them by, and the changes with their record counts and rollout progress. A cache flow's pipeline
page lists the versions of the partition's cache on the Cache versions tab, and `sqlflow cache list <partition>` prints them. For work
without an OSDU platform, `sqlflow cache import <cache.yaml> --from-dir <dir>` merges type files into the flow's
partition as that flow's capture ([cli/delivery.md](reference/cli/delivery.md#cache)). The files stand in for what a
search of the partition would return, so every record in them is an OSDU record of the declared entity type in the flow's
partition, its id written `<partition>:<entityType>:<code>`, and a lookup table is never imported: it is captured from
its table or dictionary wherever the flow runs.

### The partition cache

A catalog keeps one cache per OSDU data partition, keyed by the partition the flows reach through their
`data-partition-id` header (its scope). A cache flow fills the cache of the partition in its
`source.headers.data-partition-id`; a delivery flow reads the cache of the partition in its
`target.headers.data-partition-id`. A partition is written as an id segment (letters, digits, underscore, hyphen and
dot, at most 200 characters) or a `${env:...}` or `${keyvault:...}` reference, and a cache is keyed by what it resolves
to: `dev` and a reference that resolves to `dev` name the same cache, and a capture, an import, the repository sync and a
render all resolve it the same way.

Several cache flows may fill one partition, in the same repository or in different ones, so each project declares the
reference data it needs without copying another project's file. What they capture is stored once per partition, and
the cache holds the union of what they declare:

- **Types.** Flows that declare a type under the same name share one type in the cache. A refresh of any of them
  fetches every path any synced flow of the partition declares for the type, so a value one project asks for is there
  for every pipeline reading the partition, and every flow's query adds records to it.
- **Changes.** A changed value of a type waits for approval when any flow declaring the type says `onChange: approve`,
  and is carried out automatically otherwise.
- **Merging a capture.** A captured record replaces what the cache held for it, whichever flow captured it, so the
  newest capture of a record is what every pipeline reads. The catalog records which flows' last capture held each
  record (`osdu.CacheMember`), and a record the capturing flow no longer finds leaves the cache only when no other
  flow's last capture still holds it. Types the capture does not cover are left as they are, except a type no synced
  flow declares any more, which the merge removes. A merge that changes no cached content writes no version; the
  membership is still updated.
- **Conflicts.** Two declarations of one type for a partition that disagree on the entity type, or that cache the same
  field name from different paths, would hold two meanings under one name, and are refused. The repository sync leaves
  the declaration synced second out of the catalog with a warning naming both flows, and a refresh of a flow that
  disagrees with another fails before it captures anything. Make the declarations agree, or give one of the types
  another name.
- **Endpoints.** The cache flows of a partition should search the same OSDU platform: the sync warns when they declare
  different endpoints.
- **Concurrent writes.** Two refreshes of one partition writing at the same moment cannot both claim the next version:
  the second fails, keeps nothing of its capture, and says to run the refresh again.

When a flow stops declaring a type, the sync deletes that flow's membership of the type's records, so a later capture
of the type lets go of records no remaining flow holds.

Versions form one line per partition, and the newest version is always current. `makeCurrent` is not a setting any
more, and a cache flow that still declares it is refused when it loads; a delivery flow that has to stay on an earlier
version pins it with `render.cacheVersion`.

## Mapping

A mapping fills one template: the OSDU record of one kind, with a variable for every property its schema declares.
Each entry names a variable and where its value comes from, and a variable without an entry is left out of the record.
[mapping-templates.md](mapping-templates.md) describes templates, where they are saved, and the mapping builder.

```yaml
documentType: mapping
name: WellLog                      # the reference is Name@version, pinned by a flow under render.mapping
version: 1.4.0                     # part of the render context; the file is mappings/WellLog@1.4.0.yaml
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0   # authority:source:entityType:major.minor.patch
  version: 26a3c3441882db4f        # the saved template version: 16 hexadecimal characters
description: Well logs, one record per logging run.

dataset:
  system: wells                   # enters the delivery key
  key: [dataset.source_project, dataset.log_id]          # the columns the delivery key, and so the OSDU id, is derived from
  label: "{dataset.wellbore_uwi} / {dataset.log_source}"   # display and search only; never in the record
  identity: [dataset.wellbore_uwi, dataset.log_id]        # indexed for lookup; never in the record

parameters:                        # what the mapping accepts from the flow; values enter the render context
  dataPartition: { required: true }  # always declared: ids are minted in it, so letters, digits, _ - . only
  aclOwner: { required: true }       # the access groups and the legal tag differ per estate, so the flow supplies them
  aclViewer: { required: true }
  legalTag: { required: true }

searches:                          # record sets searched for on the platform as a record needs one, not cached
  Wellbore:
    kind: "osdu:wks:master-data--Wellbore:*"                 # one entity type, at one version or every version
    schema: { kind: osdu:wks:master-data--Wellbore:1.3.0, version: 58d6bdbd9d066a06 }  # says how it is indexed

mappings:
  - target: osdu.acl.owners        # the four access and legal variables take static, non-empty lists
    static: ["{param.aclOwner}"]
  - target: osdu.acl.viewers
    static: ["{param.aclViewer}"]
  - target: osdu.legal.legaltags
    static: ["{param.legalTag}"]
  - target: osdu.legal.otherRelevantDataCountries
    static: [NO]
  - target: osdu.tags.DeliveredBy  # a key under an object with free keys
    static: osdu-delivery
  - target: osdu.data.Name
    source: dataset.log_source       # a column of the dataset's row
    modifiers: [trim]
  - target: osdu.data.WellboreID
    source: search.Wellbore.id     # the id of the one record on the platform findBy finds
    findBy:                        # tried in order; a line is asked only when every line before it found nothing
      - search.Wellbore.data.FacilityName = dataset.wellbore_uwi
      - search.Wellbore.data.NameAliases.AliasName = dataset.wellbore_uwi
  - target: osdu.data.VerticalMeasurement.VerticalMeasurementTypeID
    static: "{param.dataPartition}:reference-data--VerticalMeasurementType:KellyBushing:"
  - target: osdu.data.Curves       # the repeater: one item per row of the child dataset
    source: dataset.curves
  - target: osdu.data.Curves[].CurveID
    source: dataset.curves.curve_id
  - target: osdu.data.Curves[].LogCurveBusinessValueID
    source: cache.LogCurveBusinessValue.id
    findBy:                        # tried in order; the first line that finds a record wins
      - cache.LogCurveBusinessValue.Code = dataset.curves.business_value
      - cache.LogCurveBusinessValue.Name = dataset.curves.business_value
    required: false                # no value leaves the variable out instead of holding the record

fixtures:                          # whole-record regression fixtures, rendered by the preflight gate
  - name: ...
    parameters: { dataPartition: dev }
    searches:                            # what the platform is assumed to answer; a fixture never asks it
      - { search: Wellbore, field: data.FacilityName, value: NO 15/9-F-1, id: "dev:master-data--Wellbore:abc" }
    record: { column: value, ... }       # the dataset's row
    datasets: { curves: [ { ... } ] }    # child dataset rows by child dataset name
    expected: |
      { ...the exact record... }
```

### The header

| Key | Meaning |
| --- | --- |
| `documentType` | Always `mapping`. |
| `name`, `version` | The mapping's reference, `Name@version`, which a flow pins under `render.mapping`. |
| `template.kind`, `template.version` | The saved template version the mapping fills. A run refuses to render against any other, and one the catalog does not hold stops the run. |
| `description` | Free text. |
| `dataset.system` | The source system. It enters the delivery key, and so the OSDU id: two mappings that deliver the same rows into the same entity type and partition need different systems or keys (the OSDU id carries the entity type, not the kind's version), because one OSDU record belongs to one flow ([ledger.md](ledger.md#one-source-several-flows)). |
| `dataset.key` | The columns of the dataset's row that identify a record, in order, each written `dataset.<column>`. The delivery key, and so the OSDU id, is derived from them. A key column need not be written into the record. |
| `dataset.label` | Optional display text for the ledger and the GUI, with `{dataset.<column>}` tokens, cut at 400 characters. It never enters the record. |
| `dataset.identity` | Optional list of the dataset's own columns whose values identify the record to a person (a wellbore id, a log id). Every value is indexed by the ledger, so the Records page finds the record by any of them across every flow. Search only: it never enters the record. |
| `parameters` | Values the flow supplies under `render.parameters`, each declared with `required`, `default` and `description`. `dataPartition` is always declared, and a flow value for a parameter the mapping does not declare is refused. |
| `searches` | The record sets the mapping's `search.` sources look in: each a `kind` and the saved template (`schema.kind`, `schema.version`) whose schema says how that kind's properties are indexed. One search per kind, and each one is read by an entry. |
| `mappings` | The entries, at least one. |
| `fixtures` | Example rows and the exact record each must render to, with `searches:` saying what the platform is assumed to answer to each search the render asks. |

### An entry

| Key | Meaning |
| --- | --- |
| `target` | The template variable to fill: `osdu.` and the property's path in the record, with `[]` after an array of objects (`osdu.data.Curves[].CurveID`). A path steps into at most one array. |
| `source` | Where the value comes from (below). An entry has `source` or `static`, never both. |
| `static` | A fixed value: text, a number, a boolean, a list or an object. `{param.name}` tokens in its text are replaced with the flow's parameter values. |
| `findBy` | With a cache or a search source, and required there: which record to read. One line or a list. |
| `modifiers` | Changes to the incoming dataset value, applied top to bottom. |
| `appliesWhen` | When the entry applies to a row. When it does not, the variable is left out for that row. |
| `required` | What an empty value does: `true` (the default) holds the record, `false` leaves the variable out. |
| `ignoreSeparators` | With a cache source: a last matching attempt with punctuation and spacing folded away. |
| `description` | Free text. |

A static entry takes only `appliesWhen` and `description` besides its value. No two entries fill the same variable.

### Sources

| Written as | Reads |
| --- | --- |
| `dataset.<column>` | A column of the dataset's row. |
| `dataset.<child>.<column>` | A column of a child dataset's row, inside a repeater over that child dataset. |
| `dataset.<child>` | On a target other entries step into (`osdu.data.Curves`): one array item per row of the child dataset. This is the repeater, and it takes no modifiers. |
| `cache.<Type>.id` | The OSDU id of the cached record `findBy` selects, with the trailing `:` OSDU relationships use. |
| `cache.<Type>.<field>` | A field of that cached record, or a path inside one (`Name`, `NameAliases.AliasName`). |
| `search.<name>.id` | The OSDU id of the one record a search of the platform finds by `findBy`, with the trailing `:`. A search reads nothing else. |

An entry inside a repeater (`osdu.data.Curves[].CurveID`) reads the rows of the child dataset the repeater names, and can
read the dataset's own row with `dataset.<column>` too. A repeater inside a repeated item is not supported. Each child
dataset is the flow's `source.datasets` entry of the same name, joined to the record by its key.

### findBy and the cache

```yaml
findBy:
  - cache.UnitOfMeasure.Code = dataset.curves.curve_unit
  - cache.UnitOfMeasure.Name = dataset.curves.curve_unit
```

Each line compares a field of the cached type the source reads with `dataset.<column>`, `dataset.<child>.<column>`, or
a quoted text (`'KellyBushing'`). The lines are tried in order, a line whose value is empty is skipped, and the first
line that finds one record wins. Modifiers change the dataset value before it is compared; cached values are OSDU's own
and are never modified.

Fields are named as the capture stored them (the path without its `data.` root, or the `as` it declared; see the
cache flow's `types[].fields`), or as a path inside one (`NameAliases.AliasName`) when the field was cached
whole. A field holding a set matches on any one of its values, so a record with three aliases is found by any of them.
An exact match wins, and case is ignored only when that finds exactly one record: OSDU codes that differ only by case
are different records (`ft` is the foot and `fT` the femtotesla, `s/m` second per metre and `S/m` siemens per metre).
A value several records answer to, exactly or once case is ignored, selects none of them: when no line finds exactly
one record, the record is held whatever `required` says, with a reason naming the candidates, and a `replace` modifier
makes the incoming value exact.

`ignoreSeparators: true` adds a last attempt, for a name rather than a code. Source systems and OSDU write the same
facility name differently, because each grew its own convention for the spaces, slashes, underscores and hyphens
between the parts that carry the meaning: with the fold on, `NO 15/9-19 SR`, `NO_15_9-19_SR` and `no-15-9-19-sr` all
find one wellbore. The fold keeps the letters and digits in order (including æ, ø and å) and replaces every run of
anything else with one separator, on the cached values as well as on the incoming value. It belongs on names, never on
codes: `s/m` and `S.M` would fold together and must not. It runs only after the exact and case-insensitive comparisons
have both found nothing, so it never moves a value that already resolved, and a folded value several records answer to
selects none of them.

### findBy and a search

```yaml
findBy:
  - search.Wellbore.data.FacilityName = dataset.wellbore_uwi
  - search.Wellbore.data.NameAliases.AliasName = dataset.wellbore_uwi
```

Each line names a property under `data` as the searched kind's schema names it, and asks the platform, under the flow's
own target, credentials and partition, for the records whose property is exactly the line's value. The query follows
how the pinned schema has the platform index the property: the `keyword` sub-field of text, the property itself for a
keyword, and the service's `nested(...)` form inside a nested array. Where the partition's system properties say its
indexer keeps a lowercased copy of text (keywordLower, [Cache flow](#cache-flow)), a text property no record holds
exactly is asked once more on that copy, and its answer is taken only when it is one record; several hold the record,
since codes that differ only by case are different records. Exactly one record found is the answer; none on any
line leaves the variable out or holds the record as `required` says; several records, a query the service refuses, or
a value that cannot be asked for when no line found the record hold the record whatever `required` says; and a
platform that cannot be asked fails the run. [mapping-templates.md](mapping-templates.md#searches) has the rules in
full: what each property shape is asked as, which values are refused and why, and how a run asks.

A value that already is an OSDU id names its record by id. When the cache does not hold it, a `cache.<Type>.id` source
writes it as it is (ending in `:`), and an entry reading another field has no value. A cached field of its own called
`ID` shadows the record id under that name, so `cache.UnitOfMeasure.ID` reads what OSDU calls `data.ID`, while `id` on a
type caching no such field reads the record id. A cached field holding a set writes a list where the template takes
one, and holds the record where it takes a single value, unless the set holds exactly one.

### Modifiers

| Modifier | Written as | Incoming value | Result |
| --- | --- | --- | --- |
| trim | `- trim` | `" STAT_COMP "` | `"STAT_COMP"` |
| upper, lower | `- upper` | `"gapi"` | `"GAPI"` |
| split | `- split: { separator: ",", part: 1 }` | `"MAIN,REPEAT"` | `"MAIN"` |
| replace | `- replace: { GAPI: gAPI, NONE: ~ }` | `"GAPI"`, `"NONE"` | `"gAPI"`, no value |
| equals | `- equals: REGULAR` | `"REGULAR"` or `"DISCRETE"` | `true` or `false` |
| date | `- date` or `- date: dd.MM.yyyy` | `"01.09.2026"` | `"2026-09-01T00:00:00Z"`, or `"2026-09-01"` where the template takes a date |
| number | `- number` or `- number: { decimal: ",", group: " " }` | `"1 234,5"` | `1234.5` |

`part` counts from one; a part the value does not have, or an empty one, gives an empty value. A separator of a single
space splits on any run of whitespace. `equals` compares trimmed text and ignores case, and an entry whose last modifier
is `equals` must fill a boolean. `replace` has its own section below.

#### replace

```yaml
modifiers:
  - replace: { M: m, METRE: m, FT: ft, NONE: ~ }
    otherwise: ~
```

A replace lists incoming values and what each becomes. A value is matched trimmed, by the cache's own rules: an exact
key wins, case is ignored only when that finds one key, and a value that matches several keys only once case is ignored
holds the record, naming them, unless they all replace it with the same value. Two keys that are the same once trimmed,
and a key that is empty once trimmed, are refused when the mapping is read.

| Written as | Result |
| --- | --- |
| `NONE: m` | The listed value becomes `m`. |
| `NONE: ~` | The listed value becomes no value, and `required` decides what that does. |
| no `otherwise` | A value the table does not list passes on unchanged, trimmed. |
| `otherwise: ~` | A value the table does not list becomes no value. |
| `otherwise: Unevaluated` | A value the table does not list becomes `Unevaluated`. |

`otherwise` is written beside `replace`, never inside its table, so no incoming value is ever read as a setting; a
replace takes no other setting beside it. It applies only to a value that is there and unlisted: an empty incoming value
stays empty, and a listed value that becomes no value is not unlisted. A quoted `"~"` is the text `~`, and a boolean is
written as YAML spells it, `true` or `false`.

A table used by one field of one mapping is written here. A table shared by mappings, or one another team maintains,
belongs in the partition's cache, where every mapping reads it the same way.

#### date

`date` writes a value in the form the template's `format` names. OSDU schemas are JSON Schema draft-07, where the
`date-time`, `date` and `time` formats are the RFC 3339 `date-time`, `full-date` and `full-time` forms. Where the
template takes a `date`, the modifier writes `2026-09-01`. Anywhere else it writes an RFC 3339 date-time in UTC,
`2026-09-01T10:15:30Z`, the form OSDU's own `createTime` and `modifyTime` take. An entry whose last modifier is `date`
must fill text, and never a `time`.

| Written as | Reads | Refuses |
| --- | --- | --- |
| `- date` | ISO 8601 only: `2026-09-01`; or a date, `T` or a space, and a time of hours and minutes with optional seconds and up to seven fractional digits, followed by `Z`, an offset (`+02:00` or `+0200`), or nothing. `t` and `z` may be lower case. | `01/02/2026` (either month), `12:30` (a time takes the day the render ran), `Sep 1 2026`, `20260901`, `2026-02-30` |
| `- date: dd.MM.yyyy` | Exactly that .NET date format, such as `yyyyMMdd` or `dd MMM yyyy HH:mm`. | A format without a four-digit year (`yy` does not say its century), a month and a day of the month, a one-letter standard pattern, an unclosed quote. These are refused when the mapping is read. |

A value without an offset is taken as UTC. A `datetime` or `datetime2` column of the ingestion table is a date already
and is written in its property's form with or without the modifier.

A value `date` cannot read holds the record whatever `required` says. So does a value with a time of day where the
template takes a date, because writing it would drop the time: take the date part first, with
`- split: { separator: T, part: 1 }` before `- date`.

Without `date`, a text value is written exactly as it arrives, so the preflight warns about every entry that fills a
`date` or `date-time` property, or a list of them, from a dataset column without it: whatever reads the record expects
the RFC 3339 form. A list of values is judged by its items throughout: `date` and `number` must give what each item takes.
OSDU's frame of reference guidance allows a non-ISO value when the record's `meta` describes its format with a
`DateTime` item, which is the one case for leaving the modifier off.

#### number

How a value becomes a number depends on the property it fills. JSON (RFC 8259) carries no `NaN` or `Infinity`, and a
double (IEEE 754 binary64) is the precision every reader of a record agrees on.

| The property takes | A value is written as | The record is held for |
| --- | --- | --- |
| `number` | The number: a whole value exactly (`12.0` is `12`), anything else as the nearest double, so a decimal with more digits than a double holds is rounded to it. | `NaN` or `Infinity`; text beyond a double's range, or too close to zero to be told apart from it. |
| `integer` | The whole number, `12.0` and `1e3` included. | A fraction; a value outside the range its format declares (`int32`: -2147483648 to 2147483647, otherwise 64 bits); a double beyond 2^53, where a double no longer holds every whole number exactly, so the integer it stands for is not known. |
| text | The shortest text that reads back as the same number: `12.5` from a double, every digit of a decimal without trailing zeros. | `NaN` or `Infinity`. |

Text is read as digits with an optional sign, `.` before the decimals and an optional exponent: `-1234.5`, `1.2E-3`.
A value written any other way holds the record rather than being guessed at, because `12,5` is twelve and a half or,
with `,` between digit groups, a hundred and twenty-five. `number` reads text written with other separators:

| Written as | Incoming value | Result |
| --- | --- | --- |
| `- number: { decimal: "," }` | `"12,5"` | `12.5` |
| `- number: { decimal: ",", group: " " }` | `"1 234 567,89"`, a no-break or thin space counting as a space | `1234567.89` |
| `- number: { decimal: ",", group: "." }` | `"1.234.567,89"` | `1234567.89` |
| `- number: { group: "," }` | `"1,234,567.89"` | `1234567.89` |

`decimal` is `.` (the default) or `,`. `group` is `,`, `.`, a space or `'`, never the same as `decimal`, and without it
no group separator is read. The separators are checked when the mapping is read. After the first group of one to three
digits every group is exactly three, so `1.23,5` holds rather than being read as 123.5. The sign may be the Unicode
minus (`−`). A value `number` cannot read holds the record whatever `required` says, and an entry whose last modifier
is `number` must fill a number, an integer, or text without a `format`.

Values from the ingestion tables are read as the numbers their SQL types hold. A `real` column gives `12.3`, not the
`12.300000190734863` its bits widen to; a `decimal` column keeps its exact value until the property decides its form;
and a null is an empty value, so `required` decides. The dataset key, the
label and conditions read the same text, so a float or decimal key column keys a record by its written value. A static
number is written as it is, and YAML's `.nan` and `.inf`, alone or inside a static list or object, are refused when the
mapping is read, because a record cannot carry them.

### appliesWhen

```yaml
appliesWhen: dataset.depth_coding is REGULAR
```

The forms are `<value> is <text>`, `<value> is not <text>`, `<value> is empty` and `<value> is not empty`, where the
value is `dataset.<column>` or, inside a repeater, `dataset.<child>.<column>`, and the text may be quoted. Comparison is
of trimmed text and ignores case, and an empty value never `is` a text. A false condition leaves the variable out for
that row and never holds a record. A repeater's condition decides for the whole array and reads the dataset's own row.
The four access and legal entries take none.

### required

| Situation | `required: true` (default) | `required: false` |
| --- | --- | --- |
| The dataset value is empty after modifiers | Record held | Variable left out |
| The cache has no matching record | Record held | Variable left out |
| The cache has several matching records | Record held | Record held |
| A `date` or `number` modifier cannot read the value | Record held | Record held |
| A value cannot take the property's type (`NaN` or `Infinity`, a fraction for an integer, a value out of range) | Record held | Record held |
| A repeater's child dataset has no rows with values | Record held | Variable left out |
| `appliesWhen` is false | Variable left out | Variable left out |

`required: false` never introduces a value, and a static entry takes no `required`. A held record is never sent, and the
ledger records the reason.

### What the record contains

The engine starts from nothing and writes `id` (from the `dataPartition` parameter, the template's entity type and the
delivery key) and `kind` (the template's), then writes each entry's value at its target. `osdu.id`, `osdu.kind` and the
properties OSDU sets (`version`, `createTime`, `createUser`, `modifyTime`, `modifyUser`) take no entry. The template
decides the type: text becomes a number, an integer or a boolean where the schema says so, a single value written to a
list of values becomes a list of one, and a value that cannot take the type holds the record with a reason naming the
target. A variable with no entry, an entry that does not apply and an optional entry with no value are left out; an
array item that received no value is left out of its array, and an array with no items is left out. A property the
schema requires in `data` that renders empty holds the record, and so does a record key with an empty column.

A mapping of a DSPDM kind renders a business object row: `id` and `kind` as above, the attributes under `data`, and
`attributes`, the upper-case list of the attributes its entries fill. An update sends each attribute in that list that
rendered empty as null, so a value the source no longer gives is cleared in DSPDM, and leaves the attributes the mapping
does not fill as they are ([protocols.md](protocols.md#osdudspdm-the-dspdm-route)).

### Fixtures

| Key | Meaning |
| --- | --- |
| `name` | Names the fixture in messages. |
| `parameters` | Parameter values for this fixture, over the flow's. |
| `record` | The dataset's row, column by column. |
| `datasets` | The rows of each child dataset, by child dataset name. |
| `expected` | The exact record, as JSON, compared canonically. |

### What the preflight gate checks

When the mapping is read, its keys, sources, `findBy` lines, modifiers and conditions parse, each entry has exactly one
input, no two entries fill the same variable, and the four access and legal variables are static lists. Before any row
is rendered, and with no OSDU call:

1. The template version the mapping pins is saved in the catalog, and is the one the render is given.
2. Every target is a variable of the template, with an agreeing shape: a repeater only on an array of objects, `[]`
   only under a repeater, a single value only on a scalar or a list of values, an object only from `static`.
3. No entry fills `osdu.id`, `osdu.kind` or a property OSDU sets.
4. Every property the schema requires in `data` has an entry, and none of those entries is `required: false`. An
   object the schema requires may instead be filled by entries for its properties (a WellboreTrajectory's
   `osdu.data.VerticalMeasurement.VerticalMeasurement` and the rest), of which at least one is static or required
   without `appliesWhen`, so the object renders on every record that is sent.
5. Every dataset column and child dataset the mapping reads, the record key's and the label's included, exists in the
   flow's ingestion tables, when those are known.
6. Every cached type exists in the cache version the render reads and holds the field the source reads. A `findBy` field the cache
   does not hold is a warning; a type holding none of them is an error, because it would hold every record at run time.
7. A `cache.<Type>.id` source resolves to the entity type the schema expects for its target: `osdu.data.WellboreID`
   reads only a cached type of `master-data--Wellbore`.
8. A static value on a relationship is an OSDU id of an entity type the relationship allows, and exists in the
   cache version the render reads when that version holds that entity type.
9. Every parameter the mapping requires has a value, the flow supplies none the mapping does not declare, and every
   `{param.name}` token has a value.
10. Every fixture renders exactly as declared, and without holds, under this context.

If any check fails, nothing renders.

## Lineage

SQLFlow's lineage graph shows every OSDU flow as a node with its data on both sides, and orders the flows in waves from
it ([docs/lineage-design.md](../../docs/lineage-design.md)). What each kind contributes:

| Flow | Reads | Writes |
| --- | --- | --- |
| Delivery | The record table and every `source.datasets` table, on the server `source.connection` names; the files under the `root` of the payload set its protocol streams; every cache type its mapping reads (`cache.<Type>` sources and `findBy` lines) | The OSDU type its mapping fills (`template.kind`); for `file` and `manifest`, also `protocolOptions.datasetKind` |
| Cache | Each type's `kind`, wildcards included | Each type's `name` in its partition's cache |
| Retrieval | Each of `source.kinds`, wildcards included | The record files (`part-*.jsonl`, `.gz` when compressed) and the manifest under `target.location` |

An **OSDU type** node is one exact kind in one partition of one platform: the platform is the flow's endpoint as written
(`${env:OSDU_URL}`; a literal URL is identified by a hash, never shown), the partition is `data-partition-id`, and the
node is listed under the entity type's group (`master-data`, `reference-data`, `work-product-component`, `dataset`). A
kind read with wildcards has a node of its own, and also reads every exact kind the estate writes on the same platform
and partition that it matches segment by segment. A **cache type** node is a cache type name in a partition, whichever
platform filled it, because a partition has one cache. The catalog explorer lists both under Datasets, and an object's
Pipelines tab shows which flows write and read it.

So a cache flow capturing wellbores runs after the delivery flow that delivers them, a delivery flow rendering against
the cache runs after the cache flow, and a file flow reading a retrieval's folder runs after the retrieval. Two flows
that each read what the other writes are not ordered against each other.

Lineage reads the mapping a delivery flow pins from the checkout the repository sync scans, through the same layout a
run uses (`render.mappings`, or the nearest `mappings` folder walking up from the flow file), and never outside that
checkout. A mapping that is missing, invalid, filed under another name, or outside the checkout costs the flow its OSDU
nodes, and the sync says why; the flow keeps its tables and files. Editing, adding or removing a mapping recomputes the
lineage on the next sync even when no flow changed.
