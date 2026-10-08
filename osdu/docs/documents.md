# Document reference

Two authored documents, kept separate ([design.md](design.md) section 9). Keys are camelCase. Unknown keys
are a parse error. Every validation failure names the file.

## Flow

```yaml
flowType: delivery                 # required discriminator
name: recall-welllog-03-header-delivery              # required; the flow id is derived from it

parameters:                        # optional; {name} tokens usable in source.work and each payload root
  logSource: { required: true, default: null, description: ... }

source:
  connection: ${env:OSDU_DATA_DB}  # the ingestion database, resolved on the node; never a literal secret
  record:
    object: OsduData.arc.WellLog   # three-part name of the record ingestion table
    key: [source_project, log_id]    # the ing flow's load.keyColumns; must equal the mapping's dataset.key columns
    primaryKey: RecId                # the table's identity primary key (the ing flow's target.identityColumn); required with fanOut
    scope:                           # optional: column -> parameter, each a typed [column] = @p predicate
      log_name: logSource
  datasets:                          # optional child ingestion tables the mapping repeats
    curves:
      object: OsduData.arc.WellLogCurve
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
    inserted: InsertedDate_DW        # when the row first reached the table: dates its arrival on the record's history
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
  detect: renderedHash             # renderedHash | always (a document is compared by what it renders to)
  payloadDetect: contentHash       # contentHash | lastModified | always (a payload by its content hash)
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
    data-partition-id: dev         # hard-codes the flow's one partition, whose cache the mapping reads; left out with partitions, or with neither to serve every registered partition (see Partitions)
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
    payload: curves                # which source.payloads entry the protocol streams; left out, the only one declared
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
  validation:                      # what the gate before a record is sent does with the record's verdict
    mode: report                   # report (send it and record the verdict) | enforce (hold a record that breaks its schema)
    unverified: send               # send | hold (hold a record some part of which could not be checked)
  validation:                      # what the gate before a record is sent does with the record's verdict
    mode: report                   # report (send it and record the verdict) | enforce (hold a record that breaks its schema)
    unverified: send               # send | hold (hold a record some part of which could not be checked)

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
  renderParallelism: 0             # renderers in the intake pipeline (0 = half the machine's processors, at least one)
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
mapping version, an edit to the mapping that can change a record, a template version or cache version, or a changed
system property a mapping's searches rely on) renders the record again, and whether it is sent is still
decided by the hash of the rendered document alone, so a new cache version that renders the same document sends nothing.
A mapping, template or parameter change is picked up by the flow's next ordinary run, which reads its whole scope once.
`target.validation` is operational too: changing it renders nothing again and redelivers nothing.

### Validation before a record is sent

Immediately before a try writes a record, the gate checks its document against the template of its kind at the version
it was rendered for (the version its render context names, whatever the flow pins now), as the route will send it
([validation-plan.md](validation-plan.md)). It applies every rule JSON Schema states that OSDU's schemas use: types,
formats, patterns, enumerations, constants, lengths, bounds, item counts and uniqueness, required properties at every
depth, properties an object does not allow, the forms a `oneOf` or `anyOf` allows (read as `anyOf`, since OSDU's forms
overlap), and the entity types a relationship (`x-osdu-relationship`) allows. The records the document refers to are
looked up once for each group of records sent together: in the ledger first, then in OSDU's storage service under
`target.verifyReferences: storage`, else in the cache version the flow renders with, for the entity types it captures.
A type the cache does not capture says nothing about whether a record exists, so such an id is counted as not checked.

| Outcome | When |
| --- | --- |
| `valid` | every rule that applies was checked and met |
| `invalid` | a rule is broken, or the document refers to a record the cache (or storage, when asked) does not hold |
| `unverified` | no rule is broken and some part could not be checked: a pattern neither regular expression dialect reads, a match that ran out of time, a value deeper or a list longer than a check walks, the time a check may take spent, a reference the template's bundle does not hold, or no saved template of the kind at that version |
| `notValidated` | the try sends the payload alone, so nothing of the document is sent or checked |

| Setting | What the gate does |
| --- | --- |
| `mode: report` (the default) | sends every record, and records its verdict |
| `mode: enforce` | holds an `invalid` record with its document kept, under an issue naming the rules it breaks |
| `unverified: send` (the default) | sends an `unverified` record, and records what was not checked |
| `unverified: hold` | holds an `unverified` record as `enforce` holds an invalid one |

Every attempt of a try that wrote the record carries its verdict under `validation` in its result, and the record keeps
the last outcome, how many problems it found and when ([ledger.md](ledger.md)). A record held by the gate keeps its
document: releasing it accepts that document as it is, so the gate sends it whatever its verdict says and the verdict
records that it was accepted; a document rendered differently later is judged again. Records broken the same way share
one issue, since a hold names the rules by the property path every record shares and quotes the values. What the route
fills when it sends a record (the dataset list of a route that registers files or datasets, the dataset properties the
Dataset service fills, the data keys an update carries forward from the version OSDU holds, and the bulk link a DDMS
manages) is not judged as it was rendered. A run's trace counts the verdicts in its progress lines, and each drain ends
with one line per template naming the rules broken most often.

In the interface form `target.validation` applies to every interface, and an interface's own `validation` block lays its
keys over it. What the render itself holds a record for (a value that cannot be converted to its variable's type, a
required `data` property rendered empty, a built id that breaks its pattern or relationship), what a route's own rules
hold it for (the Wellbore DDMS's, External Data Services'), and what `target.verifyReferences: storage` holds it for are
held in every mode, as they always were.

### The cache a flow renders with

The mapping's `cache.<Type>` sources read the cache of the partition a run delivers to: the one the run targets
([Partitions](#partitions)), or for a flow whose header names its partition, the partition in
`target.headers.data-partition-id`. Every cache flow of that partition fills it
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
partition's cache holds no version yet (refresh a cache flow that builds that partition: one naming it under
`partitions`, one serving every registered partition, or one whose `source.headers.data-partition-id` is that
partition), or when `render.cacheVersion` pins a version the catalog does not hold. Both the cache and
the template are read from the catalog, so rendering needs the catalog connection.

### Partitions

One flow and one generic mapping can serve several environments (dev, test, prod) from one SQLFlow instance
([docs/partitions-design.md](../../docs/partitions-design.md)). A flow says which partitions it works in along one of two
paths:

- **Hard-coded**: it names them under `partitions`, or names its one partition in `target.headers.data-partition-id`.
- **Registry-driven**: it names neither, and serves every partition registered with the catalog (the Partitions page,
  `sqlflow partition add <name>`). A run picks one at run time, and the registry's default when it names none, so the
  same document deploys to every environment.

```yaml
flowType: delivery
name: recall-welllog-03-header-delivery
partitions:                        # hard-coded; leave it out, and the header too, to serve every registered partition
  - name: dev
    keepLedger: true               # keeps the ledger the flow kept before it served partitions
  - test
  - prod
target:
  endpoint: ${env:OSDU_URL}        # no data-partition-id: each run sets it to the partition it targets
```

```yaml
flowType: delivery
name: recall-welllog-03-header-delivery
keepLedger: dev                    # registry-driven: the partition that keeps the ledger the flow kept before
target:
  endpoint: ${env:OSDU_URL}        # resolved with the run's partition's own values of the central configuration
```

- **A run targets one partition**, named under the run value `partition`: the partition picked in the GUI's title bar,
  `--set partition=test` on `sqlflow run` and `sqlflow trigger`, `values: { partition: test }` on a schedule; the
  module's own verbs (`check`, `preview`, `records`, `cache`, `config`) take `--partition test`. A run that
  names none runs in the registry's default when the flow serves it, else in the flow's only listed partition, and is
  refused otherwise. A registry-driven flow runs only in a registered partition; a hard-coded partition need not be
  registered. A flow whose header names its partition refuses a run that names one.
- **Bound to its partition, a run is the flow as if it had been written for it.** Every request carries
  `data-partition-id: <partition>`. Every id and reference the mapping mints is minted in it, because the kind supplies the
  partition, written literally, as `dataPartition` to a mapping that declares it. The mapping reads that partition's
  cache, and the central configuration resolves with that partition's own values first
  ([environment-variables.md](environment-variables.md#the-central-configuration)), so one document reaches a different
  platform per partition. The mapping itself names no partition.
- **Each partition keeps a ledger of its own**, named `<ledger>@<partition>` (`recall-welllog-03-header-delivery@test`)
  and keyed apart from every other, so the same record delivered to test and to prod is two records, each with its own
  history. The partition marked `keepLedger` (`keepLedger: true` on its entry, or `keepLedger: <partition>` at the top of
  a registry-driven flow) keeps the ledger the flow kept before it served partitions, under its old name and identity, so
  a flow that moves from a header to partitions keeps every record it delivered. A whole run refuses to start, and says
  why, while no partition keeps a ledger that holds records delivered to a partition the flow still serves (they would be
  delivered again as new), and while the kept ledger holds another partition's records.
- A flow that works in partitions leaves `target.headers.data-partition-id` and `render.parameters.dataPartition` out,
  and declares no parameter named `partition`. It pins no `render.cacheVersion` when it names more than one partition or
  serves the registry, because a version is a version of one partition's cache. A partition is written literally, an id
  segment (letters, digits, underscore, hyphen and dot) of at most 200 characters, and a flow names at most 64.
- **The GUI and the API read a flow one partition at a time.** The title bar's switcher picks the partition every OSDU
  page is read in and every run started from the GUI writes to, starting at the registry's default; no page picks one of
  its own. A flow's pages are that partition's, every count, record, submission, preview and action on them, and a flow
  that does not serve it says so. The API takes
  `?partition=` on every flow-level route and settles one left out as a run does, answering 400 when that settles none,
  with two exceptions: the interface listing then lists every interface in every partition, each row naming its
  partition, and the counts add every partition up, as the Delivery overview's card shows them with the partitions
  listed.

### Incremental reads: what changed since the last run

A run does not read every row. It reads the rows the ingestion tables changed in a window above the scope's
watermark, the record rows the ingestion flow marked deleted in it, the records whose child rows changed or were
marked deleted in it, and any record the ledger asked to plan again. Every row it does read goes through the whole
pipeline: render, the preflight-checked mapping, the hash of the rendered document against what OSDU holds, and the
same hash check again by the worker just before anything is sent.

| Key | What it does |
| --- | --- |
| `source.systemColumns.updated` | The ingestion column the window is taken on, `UpdatedDate_DW` by default. SQLFlow's ingestion stamps it on insert, and on update only for rows whose checksum changed, so an identically re-landed row is never read again. The window is `(watermark - overlapSeconds, now]`, fixed when the read opens. |
| `source.systemColumns.deleted` | The soft-delete stamp, `DeletedDate_DW` by default when the table carries it. SQLFlow's ingestion stamps it when its key match finds a row gone from the source, without touching the update column, so the window is taken on it as well: a row marked deleted in the window is read, held, and never delivered. |
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
  connection: ${env:OSDU_DATA_DB}
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
    record: { object: OsduData.arc.Wellbore, key: [facility_name], primaryKey: RecId }
    datasets:
      aliases: { object: OsduData.arc.WellboreAlias, join: { facility_name: facility_name }, orderBy: [alias_name] }
    mapping: Wellbore@1.0.0
  welllogs:
    ledger: wells-welllog-03-header-delivery
    record: { object: OsduData.arc.WellLog, key: [source_project, log_id], primaryKey: RecId, scope: { log_source: logSource } }
    datasets:
      curves: { object: OsduData.arc.WellLogCurve, join: { source_project: source_project, log_id: log_id }, orderBy: [curve_ordinal] }
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
of that interface alone does. A run deleting the ledger ([ledger.md](ledger.md#deleting-the-ledger)) takes the waves
backwards, each interface after every interface whose records refer to its own.

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

## Assertion flow

Tests of what an OSDU partition holds once the data has landed ([docs/assertions-design.md](../../docs/assertions-design.md)).
A flow holds tests; a test reads one type in one version, the one a mapping delivers, and holds assertions about what it
read. The tests of each mapping's type are a flow of their own. Each run keeps a report in the
module's database. Nothing a test does writes to OSDU.

```yaml
flowType: assertion
name: recall-welllog-04-header-assertion
batch: recall
partitions: [dev]                        # or "*" for every registered partition; without it, source.headers names one
parameters:
  logSource: { default: STAT_COMP }      # {logSource} in queries, ids and expected text; {partition} is always there

source:
  endpoint: ${env:OSDU_URL}
  auth: { type: oauth2ClientCredentials, secondarySecretRef: ${env:OSDU_CLIENT_ID}, secretRef: ${env:OSDU_CLIENT_SECRET}, token: { url: ${env:OSDU_TOKEN_URL} } }
  ddmsRoot: /api/os-wellbore-ddms        # where bulk data is read; the service paths below have defaults too
  # queryPath: /api/search/v2/query
  # searchPath: /api/search/v2/query_with_cursor
  # recordQueryPath: /api/storage/v2/query/records
  # legalPath: /api/legal/v1

defaults: { maxRecords: 10000, examples: 20, read: storage, indexSettleSeconds: 300 }
failRunOn: error                         # error | warning | never: which outcomes fail the platform run
reliability: { concurrency: 4, retry: { attempts: 4 } }
schedule: recall-welllog

tests:
  - name: logs-delivered
    tags: [smoke, ledger]
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"osdu-delivery" AND data.Name:"{logSource}"'
    read: index
    assert:
      - count: { atLeast: 1 }
      - delivered: recall-welllog-03-header-delivery
      - indexed: true

  - name: log-headers
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"osdu-delivery" AND data.Name:"{logSource}"'
    assert:
      - conforms: true
      - field: data.WellboreID
        resolves: master-data--Wellbore
      - field: data.ReferenceCurveID
        equals: MD
      - name: the index curve is among the curves
        field: data.Curves.CurveID
        equals: MD
        values: any
      - legal: valid
      - unique: [data.WellboreID, data.Name, data.LogRun, data.LogVersion]
        severity: warning
      - aggregate: missing
        field: data.SamplingInterval
        equals: 0
        severity: info

  - name: log-curves
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"osdu-delivery"'
    maxRecords: 200
    sample: true
    bulk: { columns: [MD], maxRows: 2000000 }
    assert:
      - rowCount: { atLeast: 1 }
      - columns: { includes: [MD] }
      - column: MD
        monotonic: strictlyIncreasing
      - column: MD
        exists: true
        for: 99%

  - name: log-sources
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"osdu-delivery"'
    assert:
      - groupBy: data.Name
        absent: [UNKNOWN]
        groupCount: { atLeast: 1 }
```

A test:

| Key | Meaning |
| --- | --- |
| `name` | Unique in the flow: a letter or digit, then letters, digits, `.`, `_` and `-`, at most 100. A run names tests by it. At most 500 tests per flow. |
| `description`, `tags` | What the test is for, and labels a run selects tests by (`{"tags":["smoke"]}`) and the board filters by. |
| `kind` | The one type it reads, in one version: `authority:source:entityType:major.minor.patch`, as the `template.kind` of the mapping that delivers it names it. A kind with wildcards is refused: a test is checked against one schema and reads one type node in lineage. |
| `query` | Lucene over the index, with `{parameter}` tokens. Without it (and without `ids`), every record of the kind. |
| `ids` | Records named by id instead of a query, read from storage; an id storage does not return is noted in the result. |
| `spatial` | The search's `spatialFilter` (`field` and one of `byBoundingBox`, `byDistance`, `byGeoPolygon`, `byIntersection`, `byWithinPolygon`), checked when the flow loads. |
| `sort` | `[{ field, order }]`: the order records are read in, which is what a sample takes. |
| `read` | `storage` (the record as OSDU keeps it; the default) or `index` (the projection search returns). |
| `maxRecords` | The most records the test reads (10,000 by default, at most 1,000,000). When more match, the assertions that need records are not evaluated and say so. |
| `sample` | `true` evaluates the first `maxRecords` records when more match, and marks the result as a sample. |
| `indexSettleSeconds` | How long the search index is given to list a change before the test is judged, 0 to 3600 seconds (`defaults.indexSettleSeconds`, 300 unless the flow says otherwise); 0 judges it whatever changed ([Records the index may not list yet](#records-the-index-may-not-list-yet)). |
| `bulk` | Reads each record's bulk data from the Wellbore DDMS under `source.ddmsRoot`: `columns` narrows the columns read (the ones the assertions name are always read), `maxRows` bounds the rows (1,000,000 by default). Only for a kind the DDMS keeps bulk data for (WellLog, WellboreTrajectory, PPFGDataset, WellPressureTestRawMeasurement). |
| `template` | The template version its fields are checked against: the mapping's `template.version`, so the test is checked against the schema the mapping renders to. The newest saved one of its kind when left out. |
| `partitions` | Narrows the test to some of the flow's partitions; a run in another skips it. |
| `severity` | The default severity of its assertions: `error` (the default), `warning` or `info`. |
| `assert` | Its assertions, at least one. |

An assertion is exactly one subject, with the keys that subject takes; `name`, `description` and `severity` go on any.
A key a subject does not take is a parse error naming the assertion.

| Subject | Keys | Holds when |
| --- | --- | --- |
| `count: <n or comparison>` | | The index's exact count of what the test matches compares true. |
| `field: <path>` | a condition, `for`, `values`, `optional`, `where` | The condition holds of the path's values in the records read, for the share `for` says. |
| `aggregate: min\|max\|sum\|avg\|count\|distinct\|missing` | `field` (or `column`, over the bulk data), a comparison, `tolerance` | The aggregate over the records read compares true; `missing` counts the values that are not there, `distinct` the different ones. |
| `unique: [paths]` | | No two records read share the values of all the paths. |
| `groupBy: <path>` | `groups`, `mode`, `absent`, `groupCount` | The index's groups of the path: each named group's count compares true (`mode: exact` allows no other group), no `absent` group is there, and the number of groups compares true. |
| `recordSet: { columns, rows, mode }` | | The rows of the columns (paths) the records hold, against `rows`: `exact` (the same rows, in any order; the default), `ordered`, `includes` or `excludes`. |
| `conforms: true` | | Every record read meets the schema of its kind, as the saved template lays it out. |
| `indexed: true` | | No record the test matches has an index error (`index.statusCode` 201 or above). |
| `legal: valid` | | Every record read carries a legal tag, and every tag they carry is valid now (Legal's `legaltags:validate`). |
| `delivered: <delivery flow>` | `interface`, `exact` | Every record that flow's ledger holds as delivered in the partition is among what the test matches; with `exact: true`, nothing else is. |
| `rowCount: <n or comparison>` | | Each record's bulk data has that many rows. |
| `columns: { includes, excludes, equals }` | | Each record's bulk data has, lacks, or has exactly these columns. |
| `column: <name>` | a condition, `for`, `optional`, `where` | The condition holds of the column's values in each record's bulk data. |
| `monotonic: increasing\|decreasing\|strictlyIncreasing\|strictlyDecreasing` | `column` | The column's values only go that way in each record's bulk data. |

A condition is one of `equals`, `notEquals`, `in`, `notIn`, `atLeast`, `atMost`, `greaterThan`, `lessThan`, `between`
(`[low, high]`), `matches` and `notMatches` (a regular expression, one second at most per value), `startsWith`,
`endsWith`, `contains` and `notContains` (text, or an element of an array), `exists` and `empty` (true or false), `type`
(JSON Schema's words: `string`, `number`, `integer`, `boolean`, `object`, `array`, `null`), `length` (a number or a comparison, of text or
an array), and `resolves` (true: the id names a record the partition holds; or an entity type the record must be, such as
`master-data--Wellbore`). `ignoreCase: true` compares text regardless of case; `tolerance` allows numbers that close.
A number in the document is compared exactly as written; `equals: 5` compares a number and `equals: "5"` text.

`for` is `all` (the default), `any`, `none` or a share such as `99%`. `values: any` holds a record when any of the values a
path yields across arrays meets the condition, rather than all of them. `optional: true` lets a record without the path
pass. `where` is a list of conditions, each with its own `field` (or `column`), that selects the records (or rows) the
assertion looks at.

A path is dotted from the record root (`data.WellboreID`, `acl.viewers`, `legal.legaltags`, `id`, `kind`), crossing
arrays implicitly (`data.Curves.CurveID` is every curve's id), or with `[*]` or `[n]`. Before a run reads anything, each
test that reads fields of an exact kind is checked against the template of its kind: a path that is not a variable of
the schema, an operator that does not suit the variable's type, or an operand the variable cannot hold keeps the test
from being evaluated, with the nearest variable suggested. A kind whose template is not saved says how to save one.

What a test reads today is storage and search for every kind, and bulk data from the Wellbore DDMS alone; the other
DDMSs and file contents are not read yet ([docs/assertions-design.md](../../docs/assertions-design.md) section 11).

### Records the index may not list yet

Every test finds its records through the search index, and OSDU indexes a change from a queue: for a while after records
are written, removed or put back at an earlier version, the index lists them as they were. A test judged then fails on
records that are fine (`delivered` misses what was just written, a field check finds nothing to check). So before a run
judges anything it reads from the ledger what this module's delivery flows changed in OSDU in the partition within each
test's `indexSettleSeconds`: the records each ledger wrote (when it last delivered them), the records it took out of OSDU
or put back at an earlier version and still holds, and the records it deleted from itself that OSDU had held. A test that
reads an entity type changed within its window is **skipped**, not failed: its result says which ledgers changed what,
the latest change, and from when a run judges it. A skipped test does not fail the run, the run's summary names it, and
the next run after the window judges it. A test that does not fit its template is reported as such whatever changed.

The ledgers are matched by entity type (the kind's, or for a test that names records by `ids`, theirs), so a change to
another version of the type skips the test too. What another system writes to the partition is not in the ledger and is
not waited for. The flow's schedule runs it right after its delivery flow, so a delivery that changed records skips the
tests of that type in the same fire; with `indexSettleSeconds: 0` a test is judged at once, whatever changed.

The operations are `test` (the default) and `plan` (select, check and count the tests, and record nothing). The payload
takes `tests` (names) and `tags`; a run with neither runs every test. A flow's pipeline has a Tests tab (its board), a
History tab (its tests against its runs) and a Reports tab; OSDU, **Tests** is the board of every assertion flow
([operations.md](operations.md#the-gui)).

## Dimension flow

The distinct values of any part of an OSDU document, gathered into dimensions ([dimension-plan.md](dimension-plan.md)),
so a person can pick values and get the OSDU search that finds the records holding them. A dimension reads one path of
the records of a kind: a property of the record (`kind`, `legal.legaltags`, `acl.viewers`, `tags.<name>`, `createUser`)
or of its data, inside nested arrays too (`data.Curves.Mnemonic`). It keeps two things apart:

- a **key** is exactly what the search index holds, the text a query compares. For a reference such as
  `data.WellboreID`, the key is the id (`dev:master-data--Wellbore:NO-15-9-F-1:`), never a name.
- a **value** is the name a person picks, ready for a drop-down. With a `label`, it is read from the record the key
  names (the wellbore's `FacilityName`) and cleaned; without one, the key itself is cleaned, and a key that names an
  OSDU record starts as the code its id ends with, its escapes decoded (`dev:reference-data--UnitOfMeasure:us%2Fft:`
  is `us/ft`), never the escaped id. The clean steps gather several keys into one value (`GR`, `gr`, and `GR` with a
  space before it, into `GR`; or every wellbore of a country into `Norway`).
- an **attribute** is a further fact of a key, read the way its label is (a wellbore's `Country`, `Field`,
  `SpudDate`). A key keeps each; values and keys are looked up by them, an attribute lists its values, and a search
  picks keys by them (every wellbore where `Country` is `Norway`).

Every key carries the search filter that finds exactly its records, and every value the filter that finds the records
holding any of its keys; values picked across dimensions compose one search (the search builder, `sqlflow dimensions
search`). A build only reads OSDU; everything it finds is kept in the module's database. OSDU's search returns at most
1,000 distinct values of a field and pages none of them, so a build reads by value ranges: a range the aggregation cuts
off is split at a value it returned, until every range answers whole, and a range that cannot be split is read record by
record through the search cursor.

```yaml
flowType: dimension
name: recall-welllog-05-dimensions
batch: recall
partitions: [dev]                        # or none: the partition a run names, or the registry's default
parameters:
  logSource: { default: STAT_COMP }      # {logSource} in queries; {partition} is always there

source:
  endpoint: ${env:OSDU_URL}
  auth: { type: oauth2ClientCredentials, secondarySecretRef: ${env:OSDU_CLIENT_ID}, secretRef: ${env:OSDU_CLIENT_SECRET}, token: { url: ${env:OSDU_TOKEN_URL} } }
  # queryPath: /api/search/v2/query               aggregations, counts and the label searches
  # searchPath: /api/search/v2/query_with_cursor  the scans of a range an aggregation cannot answer
  # aggregationSize: 1000                         the search service's AGGREGATION_SIZE, if the platform raised it
reliability: { concurrency: 4 }                   # what a build asks of the search at once: ranges, label searches, cursors (default 8)

dimensions:
  - name: Wellbore
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"osdu-delivery"'
    path: data.WellboreID                          # keys: the wellbore ids
    label: data.FacilityName                       # values: each wellbore's name
    unlabelled: Not specified                      # the value of whatever is not found
    attributes:                                    # facts of each wellbore, looked up and searched by
      Country: [data.GeoContexts.GeoPoliticalEntityID, 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName']
      Field: [data.GeoContexts.FieldID, data.FieldName]      # read from the wellbore record
      Source: { collect: data.Source }                       # collected from the logs themselves

  - name: CurveMnemonic
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"osdu-delivery" AND data.Name:"{logSource}"'
    path: data.Curves.Mnemonic
    clean:
      - trim
      - collapseSpaces
      - upper
      - replace: { pattern: '^(\w+)_\d+$', with: '$1' }
      - map: CurveAliases                            # a dictionary: the value it gives replaces the one looked up
      - map: { dictionary: CurveFamilies, field: family, otherwise: ~ }

  - name: LegalTag
    kind: "*:*:*:*"
    path: legal.legaltags
    countRecords: true
```

A dimension:

| Key | Meaning |
| --- | --- |
| `name` | Unique among the dimensions of every flow, not only this one: a letter or digit, then letters, digits, `.`, `_` and `-`, at most 100. A run, a search and a cache flow name the dimension by it, and so does its table in the database, `osdu.dim_<name>`, which is why no two flows declare a dimension of the same name (the second to build is refused, naming the first). At most 100 dimensions per flow. |
| `description` | What the dimension is for. |
| `kind` | The kind whose records are read, `authority:source:entityType:version` with wildcards per segment. Every kind the pattern matches in the partition must store the path the same way. |
| `query` | Lucene narrowing the records, with `{parameter}` tokens. Without it, every record of the kind. Every filter of the dimension selects by key alone; a composed search joins it with this query. Dimensions meant to compose into one search usually share it. |
| `path` | The key read: a property of the record, or `data.` and a path of the schema. How the index stores it (text, keyword, number, boolean or date, inside a nested array or not) is read from the saved template of each kind the pattern matches, so every such kind needs its template saved (the Templates page, `sqlflow template capture`). An object, an array of objects and a property the index keeps no exact value of are refused, naming a leaf to read instead. |
| `label` | Where a key's label is read, for a key that names an OSDU record: one path of that record (`label: data.FacilityName`), or a list of paths, each but the last reading the references the next records are found by (`label: [data.GeoContexts.FieldID, data.FieldName]`), at most 3. Every reference a step reads is followed (at most 20 a key), and the first record reached that holds a value at the last path gives it. A segment holding objects can filter them: `[Property=text]` keeps those whose property equals the text, `[Property*=text]` those whose property contains it ignoring case, `[Property$=text]` those whose property ends with it ignoring case; so `[data.GeoContexts.GeoPoliticalEntityID, 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName']` reads, of the political entities a wellbore names, the one whose own type is Country. A build finds the records by id through the search service, 500 ids a search, in the kind of the entity type the id names, and reads the path in the record as the search returns it. A key that names no record, a record the search does not hold, and one holding nothing at the path, have no label, and the build's notes say how many and why. The label is cleaned into the key's value; the key stays the id. |
| `unlabelled` | The value of whatever is not read, cleaned like a label: `unlabelled: Not specified`, so a drop-down lists it under one value, as an application lists a missing country or name. It values a key whose label is not read (and that value's filter finds exactly their records), an attribute a key has none of, and a collected attribute's records holding none of its values. At most 256 characters, for a dimension that reads a label or attributes. Left out, a key without a label is valued by the code its id ends with, its escapes decoded, and a key holds no value of an attribute it has none of. |
| `attributes` | Further facts of each key, each under a name. A path, or a list of paths, is read from the record the key names, as a label is: `attributes: { Country: [data.GeoContexts.GeoPoliticalEntityID, 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName'], SpudDate: data.SpudDate }`; a build reads them with the label, in the same searches where they start at the same records, and keeps each key's value of each (at most 256 characters; a value that is itself a record reference kept as its decoded code) with the record it came from. `{ collect: <path> }` collects the values of the dimension's own records instead: `Source: { collect: data.Source }` on a dimension keyed by the logs' `data.WellboreID` gives each wellbore every source its logs hold, with how many logs hold each, the records holding none under the `unlabelled` value. A collected attribute holds as many distinct values as the dimension keeps keys (`maxValues`): a build reads few values a read per value, and many in one pass over the records. A dimension collects one, so each row of its table is a key and one collected value. Picking the `unlabelled` value of a collected attribute in a search excludes every one of its values, so an attribute of more than 1,000 values refuses that pick, saying why. At most 20 attributes; a name is a letter, then letters, digits and underscores, at most 64, unique ignoring case, none of `value`, `keys`, `key`, `key_id`, `records`, `filter`, `label`, `id`, `partition`, and not the name of the dimension's own key or value column (`columns`). Values and keys are looked up by them (`attr=Country:Norway`), an attribute lists its values with their keys and records, narrowed by the other picks of a cascade, a search picks keys (and for a collected attribute, records) by them, the table lists them a column each, and a cached dimension carries the ones its `fields` name. |
| `columns` | The names of the two columns of the dimension's table that hold each key and its value: `columns: { key: <name>, value: <name> }`, either one or both. Left out, the key's column is named after the property `path` ends with (`data.WellboreID` is `WellboreID`) and the value's after the property the last `label` path ends with (`data.FacilityName` is `FacilityName`), or after the dimension itself when it reads no label. Where both would be the same (a dimension `Source` reading `data.Source` with no label), the value's column keeps the name, being the one a person reads, and the key's takes `Key` at its end: `Source` and `SourceKey`. A dimension has to name one where a name cannot name a column: where it is a column every table has (`path: id`) or an attribute's name, or where the dimension's name is no column name. A name is a letter, then letters, digits and underscores, at most 64, none of `id`, `partition`, `key_id`, `records`, `filter`, and unlike the other and every attribute, ignoring case; the key's cannot be `value` nor the value's `key`, the words the two columns are asked for by whatever they are named. A name given here stays when the path or the label changes. |
| `clean` | The steps each key (or its label, or for a key naming a record and no label read, the decoded code its id ends with) runs through, in order, at most 20: `trim`, `collapseSpaces`, `upper`, `lower`, `nfc`, `nfkc`, `foldSeparators` (dashes, underscores and dots to one space), `replace: { pattern, with }` (a regular expression, run without backtracking, one second at most per value; `$1` names a group), and `map` (a dictionary, [Dictionary](#dictionary): the value it gives replaces the one looked up; `map: { dictionary, field, otherwise }` names the field of a dictionary with several, and what an unlisted value comes to: kept when left out, the key left out of every value with `~`, or the text given). The value is trimmed; one that is empty, or longer than 256 characters, leaves the key of no value, and the key keeps why. |
| `countRecords` | `true` counts each value's records exactly, with one search per value. Without it a value's records are exact where a record holds the path once, and otherwise the sum of its keys' counts. |
| `maxValues` | The most distinct keys a build reads, 1,000,000 by default and 5,000,000 at most: a field with more fails the build rather than filling the database. |
| `partitions` | Narrows the dimension to some of the flow's partitions; a build in another skips it. |

How a key is compared follows how the index stores it. A text property is read through its `keyword` sub-field, which
holds the whole value up to 256 characters; a longer value is not aggregated, and a build counts the records holding only
such values. A number reads in its canonical form (`1.5`, not `1.50`), a date as `yyyy-MM-ddTHH:mm:ss.fffZ`, a boolean as
`true` or `false`. A key inside a nested array is counted per object of the array, not per record. Keys are ordered by
code point, as the index orders them.

A search composed from values picked across dimensions finds a record holding one of the values picked in each dimension:
the values of one dimension are joined with OR, the dimensions with AND, each dimension's own query is added once, and a
query of one's own narrows it further. A dimension can be picked by the attribute values its keys hold as well as by
value (every wellbore where `Country` is `Norway`, or the wellbores picked that are in `Statfjord`): its part then
compares exactly the keys holding them, at most 1,000. A collected value picks records: `Source` is `RECALL` finds the
logs whose `data.Source` is one of the texts the value stands for, not every log of the wellbores holding it, and its
`unlabelled` value finds the logs holding none of the attribute's texts. Every dimension picked in has to read the kind searched (the one kind they all
read, or one their kind patterns cover), and the query holds at most 1,000 clauses, each key compared being one (the
service allows 1,024). A key no query can carry (a text over 256 characters, a value the query language cannot state
exactly) is left out and said to be.

A dimension is one table in the module's database, `osdu.dim_<dimension>` (`osdu.dim_Wellbore`, named after the
dimension alone, so a dimension's name must be unique among the flows of a database), which a build makes and keeps
with no line of the document asking for it ([dimension-plan.md](dimension-plan.md), The table). It has a row per key and value it collects (one row for a
key of a dimension collecting nothing): `id`, an identity and the number a table of facts joins on; `partition`;
`key_id`, the key's number; the key and its value, each in a column named after what the dimension reads (`WellboreID`
and `FacilityName` for a dimension reading `data.WellboreID` labelled by `data.FacilityName`, or as `columns` names
them); `records` (those holding the collected value, or every record of the key); `filter`; and a column per
attribute, named as declared. Its schema follows the document: an attribute added to `attributes` gets its column on
the next run, through SQLFlow's schema evolution, with no migration; nothing is dropped, and an attribute taken out
keeps its column, emptied. A key's or a value's column the document names otherwise (or a changed path or label does)
is renamed where it is by the next run, its rows and their `id`s kept; a query that names the old column is changed
with the flow, and a name given under `columns` stays. So an attribute cannot be named after one of the table's own
columns (`id`, `partition`, `key_id`, `records`, `filter`, and the dimension's key and value columns), nor `key`,
`value`, `keys` or `label`, and two dimensions cannot have names that differ only in characters a table's name leaves
out (`Well.Type` and `Well-Type`). A
select of a cascade lists the distinct values of its column among the rows the other selects leave.
`sqlflow dimensions table` and the API's `table` read a page of it, searched, narrowed and ordered;
`sqlflow dimensions export --set table` and the API's `export?set=table` write it whole as CSV or JSON Lines; the API's
`attributes/<name>?attr=Country:Norway&value=<id>` and `sqlflow dimensions attributes --attr Country=Norway --value <value>`
answer one select's list from the ledger, among the keys the other picks leave, a pick of the attribute itself aside.

The operations are `build` (the default) and `plan` (settle each dimension's field and count the records it would read,
reading no value and keeping nothing). The payload takes `dimensions` (names); a run with none builds every one. A build
writes each dimension in one transaction: new keys and values are added, what changed is changed (a key's label, value,
filter or attributes), what the build no longer found is marked removed (and keeps its id, should a later build find it again), and
every change to a key is logged. A dimension taken out of the flow keeps what its last build wrote until an admin
removes it ([operations.md](operations.md#the-gui)). A dimension whose build fails keeps what the build before it wrote, and the run ends
failed with every other dimension built. A dimension flow's pipeline has a Dimensions tab; OSDU, **Dimensions** is the
page of every dimension and of the search builder ([operations.md](operations.md#the-gui)). A cache flow can hold a
dimension's values as a lookup table ([Cache flow](#cache-flow)).

## Inventory flow

Every id an OSDU kind holds in a partition, with its version and who created and last changed it, kept in the module's
database and compared with every ledger of the partition ([inventory-plan.md](inventory-plan.md)). The ledgers say what
the flows delivered and minted; the inventory says what OSDU serves; where the two disagree is the report: records no
ledger knows (orphans), records a ledger expects that storage does not hold (missing), ids an unfinished delivery left
that its undo has not taken back (undoing), and records a ledger removed, never confirmed or forgot that OSDU still
serves. A build only reads OSDU; nothing is ever written to it.

```yaml
flowType: inventory
name: welllog-inventory
batch: reconciliation
partitions: [dev]                        # or none: the partition a run names, or the registry's default

source:
  endpoint: ${env:OSDU_URL}
  auth: { type: oauth2ClientCredentials, secondarySecretRef: ${env:OSDU_CLIENT_ID}, secretRef: ${env:OSDU_CLIENT_SECRET}, token: { url: ${env:OSDU_TOKEN_URL} } }
  read: search                           # search (the default) or storage (every active record; storage's admin role)
  # queryPath, searchPath, recordQueryPath, headersPath, versionsPath, schemaPath: the services' paths, defaulted
owners: [delivery-sp@contoso.com]        # optional: the identities this estate writes as; inferred when left out
maxMissingChecks: 100000                 # optional: ids a ledger expects that one build reads from storage (0 to 1,000,000)
reliability: { concurrency: 4 }          # version lists read at once, for versions: all

inventories:
  - name: WellLogs
    kind: "*:*:work-product-component--WellLog:*"   # covers its type whole: missing records of the type are reported
    versions: all                                   # latest (the default) or every version storage keeps
  - name: NorwegianWells
    kind: "osdu:wks:master-data--Well:1.*.*"
    query: 'data.Country:"NO"'                      # search only; {name} parameters and {partition} are filled in
```

- `source.read: search` pages the search index (a viewer's entitlements suffice). The index lags writes and leaves out a
  record it failed to index, so an id a ledger expects that the read did not list is read from storage by id and
  reported `unlisted` when storage holds it. `source.read: storage` lists every active record of each kind from storage
  itself, reads their system properties a thousand at a time (a hundred where the headers route is not deployed), and
  expands a kind with wildcards through the schema service. It takes no `query`.
- `owners` decides `orphan` from `foreign` for an id no ledger knows: an owner created it, or another identity did. Left
  out, a build infers them: every identity that created at least 1% of the inventory's ids a ledger claims. The run says
  which it used and how it knew.
- An inventory **covers its type whole** when its kind names the entity type and leaves the authority, source and version
  as `*`, and no query narrows it. A ledger's delivered record, or live minted id, of the type that the read did not list
  is then read from storage and reported `missing` or `unlisted`. An inventory narrowed further reports only the ids it
  listed before and no longer lists.
- `versions: all` reads each record's versions (`GET /records/versions/{id}`) for a record that is new or whose latest
  version moved since its versions were read, so a rebuild sends requests only for what moved.

The operations are `build` (the default: read each inventory whole, merge, reconcile), `reconcile` (compare each
inventory, as its last build left it, with the ledgers as they stand now, reading from storage only the ids a ledger
expects) and `plan` (count what each inventory would read, reading no id and keeping nothing). The payload takes
`inventories` (names); a run with none takes every one. A run reads one partition. A build merges each inventory in one
transaction once its read is whole: a read that fails part way changes nothing, and the run ends failed with every other
inventory done. The findings, and what each says, are in [inventory-plan.md](inventory-plan.md), The findings. Lineage
orders an inventory flow after the flows that write the kinds it reads.

## Cache flow

The reference data the mappings resolve against ([design.md](design.md) section 6.2). A cache flow is the one place what
is cached is defined: the OSDU platform to search, the types to cache, and for each type the paths of a record to keep.
A cache is for closed vocabularies a capture can hold whole; records that are business data, such as the wellbores a
well log names, are searched for by the mapping as each record needs one ([findBy and a search](#findby-and-a-search)).
Beside OSDU's own records, a partition's cache holds lookup tables this estate keeps itself, such as how a source spells
its units: a cache flow fills one from a dictionary document in the repository ([Dictionary](#dictionary)), and every
mapping reads it the way it reads any cached type. It fills the cache of every partition it names under `partitions`, of
the partition its `source.headers.data-partition-id` names, or, naming neither, of every registered partition, and a
delivery run reads the cache of the
partition it delivers to ([The partition cache](#the-partition-cache)). A cache flow capturing the reference data well
log and trajectory mappings look units, business values and station property types up in, filling partitions `dev` and
`test`, reads:

```yaml
flowType: cache
name: osdu-reference-00-cache
batch: reference
partitions: [dev, test]            # the partitions whose caches it fills; a refresh names one, '*' for each in turn, or none for the default

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

The sample estate (`osdu/samples/recall`) has one, `recall/cache/recall-reference-00-cache.yaml`, capturing the nine
reference types a Recall well log points to. Its WellLog mapping builds every reference it writes with the `ref` and
`id` modifiers, from the lookup tables its other cache flow, `recall/cache/recall-lookups-00-cache.yaml`, captures
([Dictionary](#dictionary)), and each id it builds has to name one of the records this flow captured ([id](#id)); it
searches the platform for the wellbore. A type's `query` narrows a kind to the records a mapping reads, but only where
their values say which those are: the ids a mapping builds are looked up by the record they name, and a record's code
need not match its id (dev holds `LogCurveType:Equinor-RW`, whose code does not start with `Equinor-`), so the sample
flow keeps every LogCurveType the partition holds, tens of thousands of them, rather than lose one its dictionary names.

| Key | Meaning |
| --- | --- |
| `name` | Required. The cache flow's name: its pipeline identity, and the name the versions it writes and the records it holds in the partition's cache are recorded under. A cache flow is named globally: the sync warns when a second file in the repository declares the same name (the first file wins), and when another repository declares it too (rename one of them). |
| `description` | Optional text describing the cache. |
| `parameters` | Optional, as on a delivery flow; `{name}` tokens usable in a type's `query`. A token no parameter declares is refused, and so is a parameter named `partition` on a flow that names its partitions. |
| `partitions` | Optional: the OSDU partitions whose caches the flow fills, each written literally as an id segment, at most 64. A refresh names one of them under the run value `partition` (the partition picked in the GUI's title bar, `--set partition=<name>` on `sqlflow run` and `sqlflow trigger`, a schedule's `values`), or, naming none, fills each in turn: a partition that fails leaves the others refreshed, and the run's result says how each went. Each partition's refresh searches with that partition's `data-partition-id`, keeps what it captures in that partition's cache, and resolves its references with the partition's own configuration first. A flow that names its partitions leaves `source.headers.data-partition-id` out. |
| `types[].partitions` | Optional: the partitions of the flow's `partitions` this type is cached in, when not all of them. A type's name is unique within each partition, and every partition the flow names caches at least one type. |
| `source.endpoint`, `source.auth`, `source.headers` | The OSDU platform the OSDU types are searched on, written as `target` is on a delivery flow: `${env:NAME}` and `${keyvault:vault/secret}` references and the same auth types. `source.endpoint` is required when the flow declares a type with a `kind`, and refused, with `source.auth`, when every type it declares is a lookup table. `data-partition-id` hard-codes the one partition whose cache the flow fills, an id segment (letters, digits, underscore, hyphen and dot) or a `${env:...}` or `${keyvault:...}` reference, and every search carries it. A flow that names its partitions leaves it out, and so does a flow that names neither and fills every registered partition; each refresh sets it to the partition it fills. |
| `types` | Required, at least one: the types the flow caches, each from one origin: a `kind` searched on OSDU, a `table` an ingestion flow loads, a `dictionary` (a lookup table kept in the repository, [Dictionary](#dictionary)), or a `dimension` a dimension flow builds ([Dimension flow](#dimension-flow)). Each type's name is unique within the flow, because a mapping reads a type by its name. Another cache flow of the same partition may declare the same OSDU type, and the cache then holds one type under it ([The partition cache](#the-partition-cache)); a lookup table is declared by one cache flow of a partition. |
| `types[].kind` | For an OSDU type: the kind searched, `authority:source:entityType:version` with wildcards per segment. |
| `source.connection` | The ingestion database the flow's table types are read from, declared exactly as a delivery flow's `source.connection` is: a whole `${env:...}` or `${keyvault:...}` reference, or a SQL Server connection string whose secrets are references; a literal password is refused. Required when the flow declares a type with a `table`, and refused when it declares none. |
| `types[].table`, `types[].key` | For a lookup table an ingestion flow loads: the three-part name of the table, and the column each row is keyed by. The type is named after the table unless `name` says otherwise, is kept as `lookup--<name>`, and takes no `kind`, `entityType` or `query`: it holds every row of the table that its ingestion flow has not marked deleted (`DeletedDate_DW`, when the table has it). A key is trimmed, and a key that is empty, longer than 256 characters, or held by two rows once trimmed refuses the capture, naming the rows; so does a table over 100,000 rows. Every value is kept as the text the delivery reader gives it. Lineage orders the flow after the ingestion flow that loads the table. |
| `types[].dictionary` | For a lookup table kept in the repository: the name of the dictionary document it holds, `dictionaries/<name>.yaml` in the nearest `dictionaries/` folder above the flow. The type is named after the dictionary unless `name` says otherwise, is kept as `lookup--<name>`, and takes no `kind`, `entityType`, `query`, `key` or `fields`: the document names its key and its values. A flow holding a dictionary needs the repository's tree when it runs, and reads the file at the run's commit. |
| `types[].dimension`, `types[].dimensionFlow` | For a lookup table a dimension flow builds: the dimension and the dimension flow declaring it. The type holds the dimension's values as its last completed build in the partition left them: one row per value, keyed by `value`, with `keys` (every key the value stands for, a set a lookup matches on any one of), `records`, and `filter` (the search finding the value's records, when one query holds it) beside it. It is named after the dimension unless `name` says otherwise, is kept as `lookup--<name>`, and takes no `kind`, `entityType`, `query` or `key`; its `fields` name attributes of the dimension each row carries beside those (a set when the value's keys hold several), each one the dimension reads. A refresh before the dimension's first completed build fails, naming the build to run, and so does a field the dimension does not read; a dimension over 100,000 values or 500,000 keys refuses the capture. A mapping turns a raw key into its value as the dimension does with `replace from $cache.<name> (keys to value)`, and finds a key's attribute with `replace from $cache.<name> (keys to Country)`. Lineage orders the flow after the dimension flow. |
| `types[].name`, `types[].entityType` | Optional. The entity type is derived from the kind, and the name from the entity type (`reference-data--UnitOfMeasure` gives `UnitOfMeasure`). A kind that names no entity type needs `entityType`. |
| `types[].query` | Optional Lucene query narrowing the type; `*` when omitted. |
| `types[].fields` | For an OSDU type, required: the paths to keep, written bare (`data.Code`, cached as `Code`) or as `{ path: ..., as: ... }`. For a table type, required: the columns to keep beside the key, bare (kept under the column's name) or as `{ column: ..., as: ... }`. A dictionary type takes none. Whatever a path yields is cached as it is: a scalar, a set of values, or a nested object. A path crosses arrays implicitly, so `data.NameAliases.AliasName` reaches through an array of objects and caches the set of aliases it finds. A path that yields nothing on every record is reported at capture. |
| `onChange`, `types[].onChange` | What a changed cached value does to the records already built from it. `auto` (the default) tags them and lets the next run carry the new document; `approve` is an option that tags them and holds them back until someone approves the update on the OSDU cache page. Set for the flow and overridden per type, so a single type whose changes should be looked at first can opt in while the rest update on their own. When several cache flows of a partition declare a type, its changes wait for approval when any of them says `approve`. |
| `reliability` | The HTTP settings, as on a delivery flow. |
| `schedule` | The platform envelope, as on every flow; a fire runs a refresh. |

A cache flow's operations are `refresh` and `plan`. `refresh` is the default: a run triggered without an operation, a
scheduled fire and a run asking for `deliver` all refresh. `plan` counts what each type's search matches and writes
nothing. A cache flow takes no submission, record or slice scope; only its parameter values.

A refresh sweeps every declared type in full through the search cursor, because a cache holding only the last hour's
changes cannot answer a lookup. A type is read whole or the refresh fails: a sweep that loses a page or comes back with
fewer records than the search matches is read once more from its first page, and when that sweep fails too the refresh
fails before anything is written, so the partition's cache keeps the version it had ([design.md](design.md) section
15.1). A refresh keeps for every hit each path the partition's cache keeps for the type: the paths
this flow declares, and those any other synced cache flow of the partition declares for a type of the same name. It
then merges the capture into the partition's cache and writes the next version, labelled from the capture instant
(`20260908T212727Z`, with the sequence appended when two captures of the partition share a second, as in
`20260908T212727Z-7`), unless the merge changes no cached content: then no version is written, and nothing built from
the cache renders again. A version is the whole partition's cache, so it moves when any type, or any system property,
does. Each type it holds therefore carries its own content hash, over exactly what the type contributes to the version's
hash, with how it compares with the version before (`added`, `changed` or `unchanged`) and the version its content dates
from: a type that only rode along with another type's change keeps its hash and its date, its records are neither read
nor rewritten by the merge, and it is not analysed for what it reaches. A refresh's result lists every type it captured
with its `change` and `hash`, and its log line names the types that moved. The newest version is always the current one. A version records the cache flow that wrote it,
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

The OSDU cache page lists them under OSDU feature flags on its Setup tab, one block per service with the endpoint they were read
from: each flag's state, where the service says it was set, why it is unknown, and the version whose refresh found it in
that state, with the flag the engine relies on marked, and `sqlflow cache list <partition>` prints those of the current version.

A refresh does not only write a version. Every delivered record points at the set of cached values it was built
from, so the refresh compares the new version against the one it replaces and raises one tag per changed value: the
partition, the cached record, the path, the value the replaced version held and the one the new version holds, and how
many delivered records it reaches. Each set is judged by the value it holds, so a set already built from the new value
is not touched, and only where the refresh moved what it reads: a cached record is asked about as a whole when any of
its values moved, and a path of it that reads in the new version exactly as it did in the one before raises nothing,
whatever the set holds. So a change someone rejected is not raised again because another value of the same record
moved, a change waiting for a decision keeps the version it was found in, and a path that held an empty set and still
does is not told as a change. A tag under `approve` holds those records back (a plan skips them, so OSDU keeps the documents it has)
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

A catalog keeps one cache per OSDU data partition, keyed by the partition the flows reach (its scope). A cache flow fills
the cache of each partition it names under `partitions`, of each registered partition when it names neither them nor a
header, one at a time, or of the partition in its `source.headers.data-partition-id`; a delivery run reads the cache of
the partition it delivers to: the one the run targets, or the one in `target.headers.data-partition-id`. A refresh builds
the partition it names, every partition the flow serves in turn when it names `*`, or the registry's default when it
names none. A partition a flow names under
`partitions` is written literally; a header partition is written as an id segment (letters, digits, underscore, hyphen
and dot, at most 200 characters) or a `${env:...}` or `${keyvault:...}` reference, and a cache is keyed by what it
resolves to: `dev`, a partition named `dev`, and a reference that resolves to `dev` name the same cache, and a capture, an
import, the repository sync and a render all resolve it the same way. So a flow can move from its header to named
partitions and keep reading and filling the cache it had.

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

## Dictionary

A dictionary is one lookup table kept in the repository: how a source spells a unit and what the partition calls it, or
what each curve mnemonic measures. It is filed as `dictionaries/<name>.yaml`, one table per file, and a cache flow holds
it in the partition's cache (`types: [{ dictionary: <name> }]`), where a mapping reads it the way it reads any cached
type. Editing the file changes nothing until the cache flow runs; that refresh writes a new cache version, and only the
records a changed entry reaches are tagged and delivered again ([Cache flow](#cache-flow)). A table another system
keeps, or one too large to review as a document, is better held as an ingestion table: the sample estate keeps
petrodb-api's unit maps and curve dictionary as CSV files in `samples/recall/cache/data/`, which the pre and ing flows
beside them in `samples/recall/cache/` load into `OsduData.arc.CacheRecallUnits`, `CacheRecallDepthUnits` and
`CacheCurveDictionary`, and `samples/recall/cache/recall-lookups-00-cache.yaml` captures those tables as the cache types
`RecallUnits`, `RecallDepthUnits` and `CurveDictionary`. The data is static, so it and the flows that
load it sit in the cache folder rather than among the flows of the source's data.

A dictionary of pairs maps each key to one value:

```yaml
documentType: dictionary
name: RecallUnits
description: Unit spellings in the Recall source, as the codes of the partition's UnitOfMeasure records.
entries:
  M: m
  METRES.: m
  V/V: m3/m3
  NONE: ~           # no value: the entry's required flag decides what that does
```

A dictionary with `fields` gives each key several named values:

```yaml
documentType: dictionary
name: CurveDictionary
key: mnemonic
fields: [type, family, mainFamily, unit]
entries:
  GR: { type: Equinor-GR, family: Gamma Ray, mainFamily: GammaRay, unit: gAPI }
  LFP_AI: { type: Equinor-AI, family: EQ-Acoustic Impedance Compressional, mainFamily: Geophysics }
```

| Key | Meaning |
| --- | --- |
| `documentType` | Always `dictionary`. |
| `name` | Required. A letter followed by letters, digits, `_` or `-`. The file is named by it, and a cache flow declares the dictionary by it. |
| `description` | Free text. |
| `key` | The name each entry's key is kept under, `key` when left out. A mapping matches on it by that name. |
| `fields` | The names of the values each entry gives. Left out, the dictionary is one of pairs and each value is kept as `value`. |
| `entries` | Required: at least one entry, at most 100,000, since every entry is loaded with the cache version a render reads. A table larger than that belongs in an ingestion table. |

Every key and value is text exactly as it is written: `NO`, `true` and `1.10` are those texts, never a boolean or a
number, and the template decides the type a value is written as. An unquoted `~`, `null` or empty value is no value;
a quoted `"~"` is the text. A key is at most 256 characters, not empty, and has no spaces around it, and two entries
never share a key: a key written twice is refused, naming its line. A key or a field is never called `id` in any
casing, because a lookup row's key is its id, and the key's own name is never listed among the fields. A value is one
text; a list or a map is refused, and so is a value under a name `fields` does not list.

The repository sync reads every dictionary a cache flow declares, and leaves a type whose dictionary is missing or
invalid out of the cache with a warning naming the file, so a broken file is reported when it is pushed, not by the next
refresh. The proposal preflight checks a dictionary document before it is pushed, like a mapping.

## Mapping

A mapping fills one template: the OSDU record of one kind, with a variable for every property its schema declares. Its
`record` block is laid out the way the rendered record is, and each property in it says where its value comes from; a
property the mapping does not write is left out of the record. [mapping-templates.md](mapping-templates.md) describes
templates, where they are saved, and the mapping builder.

```yaml
documentType: mapping
name: WellLog                      # the reference is Name@version, pinned by a flow under render.mapping
version: 1.4.0                     # part of the render context; the file is mappings/WellLog@1.4.0.yaml
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0   # authority:source:entityType:major.minor.patch
  version: 26a3c3441882db4f        # the saved template version: 16 hexadecimal characters
description: Well logs, one record per logging run.

dataset:
  system: wells                    # enters the delivery key
  key: [source_project, log_id]    # the columns the delivery key, and so the OSDU id, is derived from
  label: "{wellbore_uwi} / {log_source}"   # display and search only; never in the record
  identity: [wellbore_uwi, log_id] # indexed for lookup; never in the record

parameters:                        # what the mapping accepts from the flow; values enter the render context
  dataPartition: { required: true }  # always declared: ids are minted in it, so letters, digits, _ - . only
  aclOwner: { required: true }       # the access groups and the legal tag differ per estate, so the flow supplies them
  aclViewer: { required: true }
  legalTag: { required: true }

searches:                          # record sets searched for on the platform as a record needs one, not cached
  Wellbore:
    kind: "osdu:wks:master-data--Wellbore:*"                 # one entity type, at one version or every version
    schema: { kind: osdu:wks:master-data--Wellbore:1.3.0, version: 58d6bdbd9d066a06 }  # says how it is indexed

record:                            # laid out as the record is; every word of the mapping language starts with '$'
  acl:                             # the four access and legal properties are literal, non-empty lists
    owners: ["{$param.aclOwner}"]
    viewers: ["{$param.aclViewer}"]
  legal:
    legaltags: ["{$param.legalTag}"]
    otherRelevantDataCountries: [NO]
  tags:
    DeliveredBy: osdu-delivery     # a literal: the value it is
  data:
    Name:
      $from: log_source            # a column of the dataset's row
      $modifiers: [trim]
    WellboreID:
      $search: Wellbore            # the id of the one record on the platform $findBy finds
      $findBy:                     # tried in order; a line is asked only when every line before it found nothing
        - data.FacilityName = wellbore_uwi
        - data.NameAliases.AliasName = wellbore_uwi
    VerticalMeasurement:           # an object: the properties it holds, each a node in turn
      VerticalMeasurementTypeID: "{$param.dataPartition}:reference-data--VerticalMeasurementType:KellyBushing:"
    Curves:
      $forEach: curves             # one array item per row of the child dataset
      $item:                       # under $item a bare column name reads the item's row
        CurveID: { $from: curve_id }
        LogCurveBusinessValueID:
          $cache: LogCurveBusinessValue.id
          $findBy:                 # tried in order; the first line that finds a record wins
            - Code = business_value
            - Name = business_value
          $required: false         # no value leaves the property out instead of holding the record
```

### The header

| Key | Meaning |
| --- | --- |
| `documentType` | Always `mapping`. |
| `name`, `version` | The mapping's reference, `Name@version`, which a flow pins under `render.mapping`. |
| `template.kind`, `template.version` | The saved template version the mapping fills. A run refuses to render against any other, and one the catalog does not hold stops the run. |
| `description` | Free text. |
| `dataset.system` | The source system. It enters the delivery key, and so the OSDU id: two mappings that deliver the same rows into the same entity type and partition need different systems or keys (the OSDU id carries the entity type, not the kind's version), because one OSDU record belongs to one flow ([ledger.md](ledger.md#one-source-several-flows)). |
| `dataset.key` | The columns of the dataset's own row that identify a record, in order, each named as it is (`log_id`). The delivery key, and so the OSDU id, is derived from them. A key column need not be written into the record. |
| `dataset.idFrom` | Optional: `deliveryKey` (the default) makes the OSDU id's last part from the delivery key, `key` from the key's own values, percent-encoded (`dev:reference-data--ExternalUnitOfMeasure:RECALL::GAPI`), as OSDU's reference catalogs name their records. The ledger keys the record by its delivery key either way, and a record keeps the id it first claimed ([mapping-templates.md](mapping-templates.md#the-osdu-id)). |
| `dataset.label` | Optional display text for the ledger and the GUI, with `{<column>}` tokens naming columns of the dataset's own row, cut at 400 characters. It never enters the record. |
| `dataset.identity` | Optional list of the dataset's own columns whose values identify the record to a person (a wellbore id, a log id). Every value is indexed by the ledger, so the Records page finds the record by any of them across every flow. Search only: it never enters the record. |
| `parameters` | Values the flow supplies under `render.parameters`, each declared with `required`, `default` and `description`. `dataPartition` is always declared, and a flow value for a parameter the mapping does not declare is refused. |
| `searches` | The record sets the mapping's `$search` nodes look in: each a `kind` and the saved template (`schema.kind`, `schema.version`) whose schema says how that kind's properties are indexed. One search per kind, and each one is read by a node. |
| `record` | The record the mapping renders, laid out as the record is (below). |

### The record tree

`record` holds the record's own properties, `acl`, `legal`, `tags` and `data`, and below them the properties the template
declares, each at the place the rendered record has it. **Every word of the mapping language starts with `$`, and every
other key is a property of the record**, so a template's property is never read as the language whatever it is called,
and no column is either. A property is one of four nodes:

| Node | Written as | Writes |
| --- | --- | --- |
| A literal | `ReferenceCurveID: MD`, `otherRelevantDataCountries: [NO]` | The value as it is written: text, a number, a boolean, or a list. Its text reads a parameter as `{$param.<name>}` and nothing else. |
| An object | `VerticalMeasurement:` and the properties it holds | Each property it holds, as that property's node writes it. |
| A value node | `Name: { $from: log_source, $modifiers: [trim] }` | One value, read with `$from`, `$expr`, `$value`, `$cache` or `$search`, with the settings beside it (below). |
| A `$coalesce` node | `Name: { $coalesce: [ { $from: log_name }, { $from: log_source } ] }` | The value of the first of its alternatives that gives one, each a value node of its own ([mapping-templates.md](mapping-templates.md#coalesce)). |
| A `$forEach` node | `Curves: { $forEach: curves, $item: { CurveID: { $from: curve_id } } }` | An array with one item per row of a child dataset, each item laid out under `$item`. |

A map holding any key that starts with `$` is a node of the language, and all of its keys start with `$`: a map that
mixes the language's words with the record's properties is refused, and so is a word the language does not have, with
the one it most likely meant. A property whose own name starts with `$` is written with one more, `$$name`, in the tree
and inside a literal list's objects alike; what `$value` holds is written as it is. A literal list holds literals only,
and a repeated array inside a repeated item is not supported yet.

A value node reads its value with one of these, and takes the settings after it:

| Key | Meaning |
| --- | --- |
| `$from` | A column: a bare name reads the row the node is in (the dataset's own row, or under `$forEach` the item's row), and `$dataset.<column>` reads the dataset's own row from anywhere. |
| `$expr` | A value an [expression](#expressions) computes from the row, such as `coalesce(log_name, log_source)`. It takes the settings `$from` takes, but `$findBy` and `$ignoreSeparators`. |
| `$value` | A literal with settings: text, a number, a boolean, a list or an object, written as it is. It takes only `$when` and `$description` beside it. |
| `$cache` | `<Type>.id`, the OSDU id of the cached record `$findBy` selects, with the trailing `:` OSDU relationships use; or `<Type>.<field>`, a field of that record or a path inside one (`Name`, `NameAliases.AliasName`). |
| `$search` | `<name>`, one of the mapping's searches: the OSDU id of the one record a search of the platform finds by `$findBy`, with the trailing `:`. A search reads nothing else. |
| `$findBy` | With `$cache` and `$search`, and required there: which record to read. One line or a list. |
| `$modifiers` | Changes to the incoming dataset value, applied top to bottom. |
| `$when` | A condition, an [expression](#expressions) giving true or false: when the property applies to a row. When it does not, the property is left out for that row. |
| `$required` | What an empty value does: `true` (the default) holds the record, `false` leaves the property out. |
| `$ignoreSeparators` | With `$cache`: a last matching attempt with punctuation and spacing folded away. |
| `$unverified` | With an `id` or `ref` modifier: the id is written even when the cache holds records of its entity type and not this one, recorded as an unverified reference ([id](#id)). |
| `$description` | Free text. |

A `$coalesce` node takes `$coalesce:`, a list of two or more value nodes tried in order, each with its own `$findBy`,
`$modifiers`, `$ignoreSeparators` and `$unverified`, and `$when`, `$required` and `$description` beside the list, which
decide for all of them. The first alternative that gives a value is written; one that gives nothing passes to the next,
one that meets a mistake holds the record, and a literal can only be the last ([Coalesce](mapping-templates.md#coalesce)).

A `$forEach` node takes `$forEach: <child dataset>`, `$item` with the properties each row fills, `$where`, and `$when`,
`$required` and `$description`. `$where` is a condition each child row must hold to become an item, read against that
row (`$where: curve_id != "DEPT"`); `$when` and `$required` decide for the whole array and read the dataset's own row,
and an array whose `$where` keeps no row has no rows. Each child dataset is the flow's `source.datasets` entry of the
same name, joined to the record by its key.

### findBy and the cache

```yaml
$findBy:
  - Code = curve_unit
  - Name = curve_unit
```

Each line compares a field of the cached type the node reads with a column, named as `$from` names it, or a quoted text
(`'KellyBushing'`). The lines are tried in order, a line whose value is empty is skipped, and the first line that finds
one record wins. Modifiers change the dataset value before it is compared; cached values are OSDU's own and are never
modified.

Fields are named as the capture stored them (the path without its `data.` root, or the `as` it declared; see the
cache flow's `types[].fields`), or as a path inside one (`NameAliases.AliasName`) when the field was cached
whole. A field holding a set matches on any one of its values, so a record with three aliases is found by any of them.
An exact match wins, and case is ignored only when that finds exactly one record: OSDU codes that differ only by case
are different records (`ft` is the foot and `fT` the femtotesla, `s/m` second per metre and `S/m` siemens per metre).
A value several records answer to, exactly or once case is ignored, selects none of them: when no line finds exactly
one record, the record is held whatever `$required` says, with a reason naming the candidates, and a `replace` modifier
makes the incoming value exact.

A line on `id`, on a type that caches no field of its own called `ID`, compares the record id, and the code the id ends
with as well: a reference to OSDU reference data is the partition, the entity type and that code
(`dev:reference-data--LogCurveFamily:Gamma%20Ray:`), so a table that names reference data by its code finds the record
it names, whether the code is written as the id encodes it (`Gamma%20Ray`) or decoded (`Gamma Ray`). A curve's family
can be read that way: the curve dictionary gives the code for the curve's mnemonic, and the line finds the record with
that id in the partition's cache.

```yaml
LogCurveFamilyID:
  $cache: LogCurveFamily.id
  $findBy: id = curve_id
  $modifiers:
    - replace: $cache.CurveDictionary     # GR gives Gamma%20Ray
      field: log_curve_family_id
      otherwise: ~
```

`$ignoreSeparators: true` adds a last attempt, for a name rather than a code. Source systems and OSDU write the same
facility name differently, because each grew its own convention for the spaces, slashes, underscores and hyphens
between the parts that carry the meaning: with the fold on, `NO 15/9-19 SR`, `NO_15_9-19_SR` and `no-15-9-19-sr` all
find one wellbore. The fold keeps the letters and digits in order (including æ, ø and å) and replaces every run of
anything else with one separator, on the cached values as well as on the incoming value. It belongs on names, never on
codes: `s/m` and `S.M` would fold together and must not. It runs only after the exact and case-insensitive comparisons
have both found nothing, so it never moves a value that already resolved, and a folded value several records answer to
selects none of them.

### findBy and a search

```yaml
$findBy:
  - data.FacilityName = wellbore_uwi
  - data.NameAliases.AliasName = wellbore_uwi
```

Each line names a property under `data` as the searched kind's schema names it, and asks the platform, under the flow's
own target, credentials and partition, for the records whose property is exactly the line's value. The query follows
how the pinned schema has the platform index the property: the `keyword` sub-field of text, the property itself for a
keyword, and the service's `nested(...)` form inside a nested array. Where the partition's system properties say its
indexer keeps a lowercased copy of text (keywordLower, [Cache flow](#cache-flow)), a text property no record holds
exactly is asked once more on that copy, and its answer is taken only when it is one record; several hold the record,
since codes that differ only by case are different records. Exactly one record found is the answer; none on any
line leaves the property out or holds the record as `$required` says; several records, a query the service refuses, or
a value that cannot be asked for when no line found the record hold the record whatever `$required` says; and a
platform that cannot be asked fails the run. [mapping-templates.md](mapping-templates.md#searches) has the rules in
full: what each property shape is asked as, which values are refused and why, and how a run asks.

A value that already is an OSDU id names its record by id, and the record is looked for by its record id, even on a
type that caches a field of its own called `ID`. When the cache does not hold it, a `$cache: <Type>.id` node writes it
as it is (ending in `:`), and a node reading another field has no value. In a `$findBy` line or a `$cache` node, a
cached field of its own called `ID` shadows the record id under that name, so `$cache: UnitOfMeasure.ID` reads what OSDU
calls `data.ID`, while `id` on a type caching no such field reads the record id. A cached field holding a set writes a
list where the template takes one, and holds the record where it takes a single value, unless the set holds exactly one.

### Record ids held in cached fields

Where OSDU already models a translation, its records cache the id of the platform record a source value stands for, and
a mapping writes that id as the reference. `ExternalUnitOfMeasure` maps a unit of an external system to the partition's
`UnitOfMeasure` through `data.UnitOfMeasureID`, within the catalog namespace `data.NamespaceID` names, and says how exact
the mapping is in `data.MapStateID` (a `CatalogMapStateType`). `ExternalReferenceValueMapping` does the same for any
reference value, in `data.SimpleMap.ReferenceValueID`.

The cache flow keeps the external code and the id, and its query keeps only the records of the source's namespace
whose mapping is identical. The ids in the query are the partition's own; write them as the partition holds its
`ExternalCatalogNamespace` and `CatalogMapStateType` records:

```yaml
types:
  - kind: "osdu:wks:reference-data--ExternalUnitOfMeasure:*"
    name: RecallUnitAliases
    query: 'data.NamespaceID:"dev:reference-data--ExternalCatalogNamespace:Recall:" AND data.MapStateID:"dev:reference-data--CatalogMapStateType:identical:"'
    fields: [data.Code, data.UnitOfMeasureID]
```

```yaml
CurveUnit:
  $cache: RecallUnitAliases.UnitOfMeasureID
  $findBy: Code = curve_unit
```

Only identical mappings are kept because only those can replace the reference and nothing else: a `corrected` unit is
the same concept with different conversion parameters, so the values measured in it have to be converted before the
reference changes, and an `unsupported` one has no equivalent at all (its `UnitOfMeasureID` is not expected to be
given). A unit the query leaves out finds no alias, which leaves the property out or holds the record as `$required`
says; a lookup table replace (`replace: $cache.RecallUnits`) is the way to name what the source's spellings are where
OSDU keeps no translation.

A cached field written to a relationship, or to a list of them, is checked as a reference:

- The gate refuses the mapping when a value the field holds in the cache version is not an OSDU record id, or names a
  record of an entity type the relationship does not allow, naming how many and the first of them.
- Where the cache holds records of the entity type an id names, an id it holds no record for is listed as a warning, and
  a render meeting it holds the record whatever `$required` says: the reference would point at nothing. Where the cache
  holds no records of that type, the id is written as it is, since nothing says whether the record exists.
- An id is written with the colon that separates a version, added where the cached value leaves it out; an id naming a
  version is written as it is. The record it names is recorded among the record's cache dependencies, so a version of
  the cache without it reaches the records that point at it.

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
| id | `- id: "{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:"` | `"m/s"` | `"dev:reference-data--UnitOfMeasure:m%2Fs:"` |
| ref | `- ref`, `- ref: UnitOfMeasure` or `- ref: reference-data--UnitOfMeasure` | `"m/s"` | `"dev:reference-data--UnitOfMeasure:m%2Fs:"` |

The modifiers are listed under `$modifiers`; their own names and settings (`split`, `separator`, `otherwise`...) are
words of the modifier, not of the record, so they carry no `$`. `part` counts from one; a part the value does not have,
or an empty one, gives an empty value. A separator of a single space splits on any run of whitespace. `equals` compares
trimmed text and ignores case, and a node whose last modifier is `equals` must fill a boolean. `replace`, `id` and `ref`
have their own sections below.

#### replace

```yaml
$modifiers:
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
| `NONE: ~` | The listed value becomes no value, and `$required` decides what that does. |
| no `otherwise` | A value the table does not list passes on unchanged, trimmed. |
| `otherwise: ~` | A value the table does not list becomes no value. |
| `otherwise: Unevaluated` | A value the table does not list becomes `Unevaluated`. |

`otherwise` is written beside `replace`, never inside its table, so no incoming value is ever read as a setting; a
replace takes no other setting beside it. It applies only to a value that is there and unlisted: an empty incoming value
stays empty, and a listed value that becomes no value is not unlisted. A quoted `"~"` is the text `~`, and a boolean is
written as YAML spells it, `true` or `false`.

A table used by one property of one mapping is written here. A table shared by mappings, or one another team maintains,
belongs in the partition's cache, where every mapping reads it the same way.

##### A table read from the cache

A lookup table with one field beside its key (a unit map, or a dictionary of pairs): the key matches, and that field
replaces.

```yaml
$modifiers:
  - replace: $cache.RecallUnits
```

A lookup table with several fields: `match` defaults to its key, `mnemonic`, and `field` names the one of its fields that
replaces.

```yaml
$modifiers:
  - replace: $cache.CurveDictionary
    field: log_curve_family_id
    otherwise: ~
```

OSDU reference data: a type of OSDU records has no key, so both fields are named.

```yaml
$modifiers:
  - replace: $cache.UnitOfMeasure
    match: Name
    field: Code
```

`replace: $cache.<Type>` reads its table from the version of the partition's cache the render reads: a
[dictionary](#dictionary), an ingestion table, or any OSDU type a [cache flow](#cache-flow) declares. The incoming value
is matched on the `match` field by the same rules as a written table, and replaced by what the matched row holds at
`field`. The mapping names the type, never the cache, so every flow delivering to a partition reads the same table.

| Setting | Default | Meaning |
| --- | --- | --- |
| `match` | the table's key | The cached field the incoming value is compared with. A type of OSDU records has no key, so it names one. |
| `field` | the one field a lookup table holds beside its key (`value` for a dictionary of pairs) | The cached field that replaces the value. A lookup table with several fields, and a type of OSDU records, names one. |
| `otherwise` | the value passes on unchanged | What a value no row holds becomes, exactly as for a written table. |

A matched row with nothing in `field` gives no value; `otherwise` applies only when no row holds the value. A row that
holds several values in `field`, or an object, holds the record, since a replace gives one value; so does a value two
rows answer to only once case is ignored when they give different values, a type the cache version does not hold, and a
field the table cannot settle. `match` and `field` are refused beside a written table.

Every row a replacement was decided by is recorded with the record's other cache dependencies: the value it matched,
and what it gave or that it gave nothing. A key a lookup table does not list is recorded too, when the replace matches
on the key. So a new version of the table that changes an entry, empties or removes it, or comes to list a key that
records looked up and did not find, tags exactly the records built from it, as any other change to the cache does. A
value no row of a type of OSDU records holds, matched on another field, depends on no row, so a record built without it
is delivered again only when its source changes or an operator redelivers it.

The preflight checks that the type is in the cache version, that it holds `match` and `field`, and warns about rows
that hold several values. Where the replaced value is then looked up in the cache by the node's `$findBy`, every value
the replace can give (a written table's values, a cached table's `field` values, and a text `otherwise`) is looked up
there as well, and the ones that find nothing are listed as a warning before any row arrives.

#### id

```yaml
Curves:
  $forEach: curves
  $item:
    CurveUnit:
      $from: unit
      $modifiers:
        - replace: $cache.RecallUnits
        - id: "{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:"
    LogCurveTypeID:
      $from: mnemonic
      $modifiers:
        - id: "{$param.dataPartition}:reference-data--LogCurveType:{$cache.CurveDictionary.log_curve_type_id}:"
      $required: false
```

`id` builds the OSDU id a node writes from a template, so a mapping generates references rather than looking every
one up: reference data, master data, work product components and datasets alike, with or without a version. The
template is text with tokens, quoted, since YAML reads text that starts with `{` as a map.

| Token | Gives |
| --- | --- |
| `{$value}` | The node's value, after the modifiers before `id`. |
| `{<column>}` | A column of the row the node reads: the dataset's own row, or under `$forEach` the item's row, as `$from` names it. |
| `{$dataset.<column>}` | A column of the dataset's own row, from anywhere. |
| `{$cache.<Type>.<field>}` | `field` of the row of a cached lookup table (a [dictionary](#dictionary) or an ingestion table) whose key is the node's value, matched as a replace matches it. |
| `{$param.<name>}` | A parameter the mapping declares, as the flow supplies it. |

A bare name is always a column, so a column called `value` is `{value}` and the node's own value is `{$value}`. A
token of the old vocabulary written without its marker (`{param.dataPartition}`) is refused with the token it most
likely meant. Everything else is written as it stands, and is what an id carries: ASCII letters, digits, `_`, `-`, `.`,
`:` and percent-escapes such as `%2F`. The template's own text holds the colons that part an id,
`<partition>:<group>--<Entity>:<code>`, and a reference ends with `:` and the version it pins, if any
(`dev:master-data--Wellbore:1234:`). A template that reads nothing a record changes (only text and parameters) is
refused: that is a literal, which takes `{$param.<name>}` too.

Each token's value is trimmed and percent-encoded as UTF-8: letters, digits, `_`, `-`, `.` and `:` stay as they are, a
percent-escape already in the value is kept, so nothing is encoded twice, and anything else is escaped (`NO 15/5-7` gives
`NO%2015%2F5-7`). A `:` stays because the code of an id may hold one (`Projected:EPSG::23031`). An incoming value that
already is an OSDU id names its record and is written as it is, with the version separator added, when the template
reads `{$value}`; one of another entity type than the template builds holds the record.

The id is then looked for in the version of the partition's cache the render reads, when that version holds records of
the entity type it names: reference data a cache flow captured from the partition, such as the sample estate's
`recall-reference-00-cache`. It has to name one of them, by its exact id (ids that differ only by case are different
records), because a reference to a record the partition does not hold reaches OSDU pointing at nothing; the record it
names is a dependency of the render, so a later version that drops or changes it reaches the record. Looking the id up,
rather than a code, finds a record whatever its `Code` says (dev holds `LogCurveFamily:EQ-CPI%20Qual%20Flag` with the code
`EQ-CPI Quality Flag`), and a stray record beside it decides nothing (dev holds `UnitOfMeasure:degC:` beside
`UnitOfMeasure:degC`). A version holding no record of that entity type answers nothing about it, and the id is written as
built, as it is for a mapping that reads no cache at all. An id whose code carries colons of its own and that ends in a
version is not looked up, since its text does not say where the code ends. A node that says `$unverified: true` writes an
id the cache holds no record under all the same, recorded among the render's dependencies as an unverified reference;
when a later refresh holds the record, the change is tagged `found` and the record is built again against it.

| Situation | `$required: true` (default) | `$required: false` |
| --- | --- | --- |
| The value is empty, or a token has no value (an empty column, a key the lookup table does not list, a row with nothing in the field) | Record held, naming the tokens | Property left out |
| The cache version holds records of the entity type the id names, and none under this id | Record held, naming the id and the types looked in | Property left out |
| A parameter the flow gives no value, a type the cache version does not hold or that holds OSDU records, a value two rows answer to only once case is ignored with different values, a row holding several values in the field, text that is not valid Unicode | Record held | Record held |
| The id built is not OSDU's id shape, does not match the pattern the template gives the variable, or names an entity type its relationship does not allow | Record held | Record held |

A half-built id is never written. `id` applies only to a `$from` or an `$expr` node, since a `$cache` or `$search` node
gives the id itself, and it is the last modifier, since anything after it would change the id it built. Every row a cache token read,
and every key a lookup table did not list, is recorded among the record's cache dependencies, exactly as for
[a replace reading the cache](#a-table-read-from-the-cache), so a new version of the table tags the records built from it.

The mapping is refused when it is read for a token it cannot read, text an id cannot carry, fewer than two colons, text
between the first two colons that is no entity type, a parameter it does not declare, or `id` anywhere but last. The
preflight checks that the variable takes text; that each cache token reads a lookup table the cache version holds, at a
field its rows hold; that each parameter has a value; and builds the id with a stand-in for every value a row gives,
refusing a template whose ids are of an entity type the variable's relationship does not allow or do not match its
pattern (a reference without its trailing `:`, say). Where a token writes the entity type, that is checked on each
record as its id is built.

#### ref

```yaml
SamplingDomainTypeID:
  $from: index_type
  $modifiers:
    - replace: { DEPTH: Depth }
    - ref
```

`ref` is `id` with the template written for it: `{$param.dataPartition}:<group>--<Entity>:{$value}:`, the reference to
the record whose code is the value, in the flow's partition. Written alone, the entity type is the one the variable's
`x-osdu-relationship` in the pinned template names. A variable pointing to several names the one meant, by its entity
(`- ref: UnitOfMeasure`, which also completes a relationship that names a group alone) or in full
(`- ref: reference-data--UnitOfMeasure`, which serves a variable the template gives no relationship too). Everything the
`id` section says holds for `ref`: the encoding, an incoming value that already is an id, the holds, and the checks. The
loader refuses a `ref` naming text that is no entity type, on a `$cache` or `$search` node, or anywhere but last, and a
node may carry one of `id` and `ref`. The preflight refuses a `ref` the template does not settle, naming the types the
variable points to; a render meeting one anyway holds the record with the same reason.

#### date

`date` writes a value in the form the template's `format` names. OSDU schemas are JSON Schema draft-07, where the
`date-time`, `date` and `time` formats are the RFC 3339 `date-time`, `full-date` and `full-time` forms. Where the
template takes a `date`, the modifier writes `2026-09-01`. Anywhere else it writes an RFC 3339 date-time in UTC,
`2026-09-01T10:15:30Z`, the form OSDU's own `createTime` and `modifyTime` take. A node whose last modifier is `date`
must fill text, and never a `time`.

| Written as | Reads | Refuses |
| --- | --- | --- |
| `- date` | ISO 8601 only: `2026-09-01`; or a date, `T` or a space, and a time of hours and minutes with optional seconds and up to seven fractional digits, followed by `Z`, an offset (`+02:00` or `+0200`), or nothing. `t` and `z` may be lower case. | `01/02/2026` (either month), `12:30` (a time takes the day the render ran), `Sep 1 2026`, `20260901`, `2026-02-30` |
| `- date: dd.MM.yyyy` | Exactly that .NET date format, such as `yyyyMMdd` or `dd MMM yyyy HH:mm`. | A format without a four-digit year (`yy` does not say its century), a month and a day of the month, a one-letter standard pattern, an unclosed quote. These are refused when the mapping is read. |

A value without an offset is taken as UTC. A `datetime` or `datetime2` column of the ingestion table is a date already
and is written in its property's form with or without the modifier.

A value `date` cannot read holds the record whatever `$required` says. So does a value with a time of day where the
template takes a date, because writing it would drop the time: take the date part first, with
`- split: { separator: T, part: 1 }` before `- date`.

Without `date`, a text value is written exactly as it arrives, so the preflight warns about every node that fills a
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
minus (`−`). A value `number` cannot read holds the record whatever `$required` says, and a node whose last modifier
is `number` must fill a number, an integer, or text without a `format`.

Values from the ingestion tables are read as the numbers their SQL types hold. A `real` column gives `12.3`, not the
`12.300000190734863` its bits widen to; a `decimal` column keeps its exact value until the property decides its form;
and a null is an empty value, so `$required` decides. The dataset key and the
label read the same text, so a float or decimal key column keys a record by its written value; an expression reads a
number as the number it is. A literal
number is written as it is, and YAML's `.nan` and `.inf`, alone or inside a literal list or object, are refused when the
mapping is read, because a record cannot carry them.

### Expressions

```yaml
Description:
  $expr: coalesce(log_description, log_source & " run " & log_run)
SamplingInterval:
  $from: index_increment
  $when: depth_coding = "REGULAR" and index_increment > 0
Curves:
  $forEach: curves
  $where: curve_id != "DEPT"
```

An expression computes a property's value (`$expr`), decides whether a property is written for a row (`$when`), or
decides whether a child row becomes an item (`$where`). It reads columns as `$from` names them (`` `name` `` in
backticks for a keyword, a leading digit or a `-`), `$dataset.<column>` and `$param.<name>`; compares with `=`, `!=`,
`<`, `<=`, `>`, `>=` and `in [...]`; joins conditions with `and`, `or` and `not`, text with `&`; computes with
`+ - * /`; and calls `iif`, `coalesce`, `nullif`, `empty`, `trim`, `upper`, `lower`, `substring`, `left`, `right`,
`replace`, `length`, `contains`, `startsWith`, `endsWith`, `number`, `text`, `round` and `abs`, named and behaving as
in SQL. [mapping-templates.md](mapping-templates.md#expressions) is the full reference: the grammar, how no value, text,
numbers and dates compare, and every function.

A missing column and blank text are no value, which is the same only as no value: `status != "FINAL"` holds for a row
without a status. Text compares trimmed and ignoring case. A condition that is false, or no value, leaves the property
out for that row, or the row out of the array. An expression that meets a value it cannot work with (text where it
computes with numbers, a column it tests that holds neither true nor false) holds the record, naming the expression, the
part and the value. `$expr` giving no value is an empty value, and `$required` decides. The four access and legal
properties take no `$when`. The loader refuses an expression it cannot read, a condition giving a value, and a condition
or `$expr` reading no column; a condition in the form conditions used to be written in (`depth_coding is REGULAR`) is
refused with the expression to write instead.

### $required

| Situation | `$required: true` (default) | `$required: false` |
| --- | --- | --- |
| The value (a column, or what `$expr` gives) is empty after modifiers | Record held | Property left out |
| The cache has no matching record | Record held | Property left out |
| The cache has several matching records | Record held | Record held |
| A `date` or `number` modifier cannot read the value | Record held | Record held |
| A value cannot take the property's type (`NaN` or `Infinity`, a fraction for an integer, a value out of range) | Record held | Record held |
| A `$forEach` node's child dataset has no rows with values, or none its `$where` keeps | Record held | List written empty |
| `$when` is false | Property left out | Property left out |
| An expression meets a value it cannot work with | Record held | Record held |

`$required: false` never introduces a value, and a literal takes no `$required`. A held record is never sent, and the
ledger records the reason.

### What the record contains

The engine starts from nothing and writes `id` (from the `dataPartition` parameter, the template's entity type and the
delivery key) and `kind` (the template's), then writes each property's value at its place. `record.id`, `record.kind`
and the properties OSDU sets (`version`, `createTime`, `createUser`, `modifyTime`, `modifyUser`) are refused. The
template decides the type: text becomes a number, an integer or a boolean where the schema says so, a single value
written to a list of values becomes a list of one, and a value that cannot take the type holds the record with a reason
naming the variable. A property the mapping does not write, a node that does not apply and an optional node with no
value are left out, but for a list (below); an array item that received no value is left out of its array.

A record never carries a null where its template takes none, and the mapping decides what it carries. A text, number,
integer, boolean or object with no value is left out, never written as null, nor as an empty text, a zero, `false` or
`{}`, each of which would be a value of its own. A list the mapping defines (a `$forEach`, a list of values, a list of
objects, or a node whose variable is a list) that gives nothing for the row, empty or not applying, is written empty
(`"Curves": []`), at any depth: in the record, in an object the record writes, and in each item of a list of objects
that defines it and is written. A list the mapping does not define is left out, as Storage keeps it in a record's `data`;
no object is made to hold an empty list, and an item none of whose properties gives a value is still left out of its
list. A list of the record's own outside `data` (`meta` in OSDU's schemas) is a field of Storage's record, which Storage
reads back as null when it is left out and the record's schema refuses there, so the record carries it empty
(`"meta": []`) whenever nothing fills it. A null item is dropped from a list whose items take no null (a list read whole
from the cache may hold one), in every object the record holds; a value that takes one of several forms (`oneOf`,
`anyOf`) is not looked into, since which form it takes is the value's own. A DSPDM row, a row of single values that never
reaches Storage, is given no empty list.

A property the schema requires in `data` that renders empty holds the record, and so does a record key with an
empty column.

Messages name a node by where the document writes it (`record.data.Curves.$item.CurveUnit`), and by the template
variable it fills (`osdu.data.Curves[].CurveUnit`) where the variable is what the message is about.

A mapping of a DSPDM kind renders a business object row: `id` and `kind` as above, the attributes under `data`, and
`attributes`, the upper-case list of the attributes its nodes fill. An update sends each attribute in that list that
rendered empty as null, so a value the source no longer gives is cleared in DSPDM, and leaves the attributes the mapping
does not fill as they are ([protocols.md](protocols.md#osdudspdm-the-dspdm-route)).

### What the preflight gate checks

When the mapping is read, its header, every node of the record tree, the `$findBy` lines, modifiers and expressions
parse, each condition gives true or false and reads a column, a key written twice in one map is refused, and the four access and legal properties are literal lists. Before
any row is rendered, and with no OSDU call:

1. The template version the mapping pins is saved in the catalog, and is the one the render is given.
2. Every property is a variable of the template, with an agreeing shape: `$forEach` only on an array of objects, a
   single value only on a scalar or a list of values, an object only from a literal (`$value`).
3. No node fills `record.id`, `record.kind` or a property OSDU sets.
4. Every property the schema requires in `data` has a node, and none of those nodes is `$required: false`. An object
   the schema requires may instead be filled by nodes for its properties (a WellboreTrajectory's
   `VerticalMeasurement.VerticalMeasurement` and the rest), of which at least one is a literal or required without
   `$when`, so the object renders on every record that is sent.
5. Every dataset column and child dataset the mapping reads, the record key's, the label's and the expressions' included,
   exists in the flow's ingestion tables, when those are known.
6. Every cached type exists in the cache version the render reads and holds the field the node reads. A `$findBy` field
   the cache does not hold is a warning; a type holding none of them is an error, because it would hold every record at
   run time.
7. A `$cache: <Type>.id` node resolves to the entity type the schema expects for its property: `data.WellboreID` reads
   only a cached type of `master-data--Wellbore`.
8. A literal on a relationship is an OSDU id of an entity type the relationship allows, and exists in the cache version
   the render reads when that version holds that entity type. An `id` or `ref` modifier builds ids the variable takes,
   and a `ref` settles one entity type.
9. Every parameter the mapping requires has a value, the flow supplies none the mapping does not declare, and every
   `{$param.name}` token and every `$param.<name>` an expression reads has a value.

If any check fails, nothing renders.

## Lineage

SQLFlow's lineage graph shows every OSDU flow as a node with its data on both sides, and orders the flows in waves from
it ([docs/lineage-design.md](../../docs/lineage-design.md)). What each kind contributes:

| Flow | Reads | Writes |
| --- | --- | --- |
| Delivery | The record table and every `source.datasets` table, on the server `source.connection` names; the files under the `root` of the payload set its protocol streams; the mapping it pins (`render.mapping`), which is a node of its own | The OSDU type its mapping fills (`template.kind`); for `file` and `manifest`, also `protocolOptions.datasetKind` |
| Mapping (a node, not a flow) | Every cache type it names (a `$cache` source, a lookup, a replace's table, a `{$cache.Type.field}` token of an id); every cache type of the partition holding records of an entity type an `id` or a `ref` of it builds, since each id is looked up there; every kind its searches look in | Nothing: the delivery flow reading it writes the OSDU type |
| Cache | Each type's `kind`, wildcards included | Each type's `name` in its partition's cache |
| Retrieval | Each of `source.kinds`, wildcards included | The record files (`part-*.jsonl`, `.gz` when compressed) and the manifest under `target.location` |
| Assertion | Each test's `kind`, one type in one version, in each partition the flow tests: the type node the delivery flow whose mapping names it writes | Nothing: its reports are kept in the module's database, not as data |

An **OSDU type** node is one exact kind in one partition of one platform: the platform is the flow's endpoint as written
(`${env:OSDU_URL}`; a literal URL is identified by a hash, never shown), the partition is `data-partition-id` as written,
or each partition a flow names under `partitions` (such a flow has nodes in every one of them), and the node is listed
under the entity type's group (`master-data`, `reference-data`, `work-product-component`, `dataset`). A
kind read with wildcards has a node of its own, and also reads every exact kind the estate writes on the same platform
and partition that it matches segment by segment. A **cache type** node is a cache type name in a partition, whichever
platform filled it, because a partition has one cache. A **mapping** node is a mapping document as one partition of one
platform renders it, named by its reference (`WellLog@1.5.0`) and listed under the folder it is filed in
(`recall/mappings`): the same document pinned for two partitions is a node in each, reading that partition's cache, and
two flows pinning it for one partition read the one node. The catalog explorer lists all three under Datasets, and an
object's Pipelines tab shows which flows write and read it.

The graph draws what a mapping reads into the mapping, and the mapping into the flow rendering with it:

```text
recall-reference-00-cache ──► LogType (osdu cache) ──► WellLog@1.5.0 (osdu mapping) ──► recall-welllog-03-header-delivery ──► WellLog:1.5.0 (osdu type)
```

A mapping does not have to name a cache type to read it. Every id a `ref` or an `id` builds is looked up among the
cached records of the entity type it names, so a mapping writing `SamplingDomainTypeID` with `ref` reads whichever cache
type holds `reference-data--WellLogSamplingDomainType`, and lineage shows that read. Which entity type a bare `ref`
names is told by the template the mapping pins, which lineage reads from the module's database. Where it cannot (the
offline `sqlflow lineage`, a template version that is not saved, a database that does not answer), the flow keeps the
reads the documents alone tell and the sync warns, naming the mapping, the properties and the template; a template
saved afterwards is picked up by the next sync. Which cache types hold an entity type is read from the cache flows of
the same repository.

So a cache flow capturing wellbores runs after the delivery flow that delivers them, a delivery flow whose mapping reads
the cache runs after the cache flow, a file flow reading a retrieval's folder runs after the retrieval, and an assertion
flow testing well logs runs after the flow that delivers them. Two flows
that each read what the other writes are not ordered against each other.

Lineage reads the mapping a delivery flow pins from the checkout the repository sync scans, through the same layout a
run uses (`render.mappings`, or the nearest `mappings` folder walking up from the flow file), and never outside that
checkout. A mapping that is missing, invalid, filed under another name, or outside the checkout costs the flow its OSDU
nodes, and the sync says why; the flow keeps its tables and files. Editing, adding or removing a mapping recomputes the
lineage on the next sync even when no flow changed.
