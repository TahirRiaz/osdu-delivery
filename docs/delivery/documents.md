# Document reference

Two authored documents, kept separate ([design.md](design.md) section 9). Keys are camelCase. Unknown keys
are a parse error. Every validation failure names the file.

## Flow

```yaml
flowType: delivery                 # required discriminator
name: recall-welllog               # required; the flow id is derived from it

parameters:                        # optional; {name} tokens usable in source.location
  logSource: { required: true, default: null, description: ... }

source:
  location: abfss://lake@acct.dfs.core.windows.net/osdu-prepare/{logSource}   # or a local path
  manifest: manifest.json          # default
  payloads:                        # name -> drop-relative template; must contain {deliveryKey}
    curves: curves/{deliveryKey}/chunk_*.parquet
  lastModified: update_date        # root-scope column saying when the row last changed (optional; or fingerprint: <column>, never both)
  knownState: abfss://lake@acct.dfs.core.windows.net/osdu-prepare/{logSource}/known-state   # where a known-state run publishes when the run names no location (optional)
  work: abfss://lake@acct.dfs.core.windows.net/osdu-work/{logSource}   # where the intake writes its work batches (default {location}/.work)
  scopes:                          # optional overrides of the manifest's child scopes
    curves: { records: curves-meta/*.parquet, key: deliveryKey }

render:                            # the only block that changes what a document is
  mapping: WellLog@1.4.0           # pinned Name@version, never floating
  references: pinned               # or an explicit snapshot version
  parameters:                      # values for the parameters the mapping declares
    dataPartition: dev

change:
  detect: renderedHash             # renderedHash | always
  payloadDetect: contentHash       # contentHash | lastModified | always
  onUnchanged: skip                # skip | deliver
  useSourceVersions: true          # tier-0 gate on the manifest's sourceVersions

target:
  endpoint: ${env:PETRODB_URL}     # ${env:NAME} and ${keyvault:vault/secret} references
  auth:
    type: oauth2ClientCredentials  # none | bearer | apiKeyHeader | basic | oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token: { url: ${env:OSDU_TOKEN_URL}, body: { scope: ${env:OSDU_SCOPE} }, basicAuthClient: false, tokenPath: access_token, applyPrefix: "Bearer " }
  headers:                         # extra headers on every request
    Ocp-Apim-Subscription-Key: ${env:APIM_KEY}
    data-partition-id: dev         # required: every OSDU service rejects a request without it, so the loader insists on it
  protocol: osduWellLog            # osduRecord | osduWellLog | osduFile | osduManifest
  protocolOptions:
    payload: curves                # which source.payloads set the protocol streams
    recordPath: /ddms/v3/welllogs  # protocol defaults shown; override for petrodb-api routes
    recordMethod: POST
    dataPath: /ddms/v3/welllogs/{id}/data
    sessionPath: /ddms/v3/welllogs/{id}/sessions
    sessionDataPath: /ddms/v3/welllogs/{id}/sessions/{sessionId}/data
    sessionCommitPath: /ddms/v3/welllogs/{id}/sessions/{sessionId}
    verifyPath: /ddms/v3/welllogs/{id}
    deletePath: /ddms/v3/welllogs/{id}       # logical delete (osduRecord: POST /api/storage/v2/records/{id}:delete)
    purgePath: /ddms/v3/welllogs/{id}        # physical purge (osduRecord: DELETE /api/storage/v2/records/{id})
    # Versions belong to the storage service, which is not where an osduWellLog endpoint points, so that protocol
    # needs the whole URL here. Any path option may be written absolute; it is guarded like every other request.
    purgeVersionsPath: https://osdu.example.com/api/storage/v2/records/{id}/versions
    sessionThresholdChunks: 1      # 1: a single chunk goes to the bulk endpoint, more open a session. 0: always a session
    maxChunkValues: 10000000       # wellbore DDMS ceiling: cells (rows x columns) per chunk (0 = do not check)
    maxChunkColumns: 3000          # wellbore DDMS ceiling: columns per chunk; 500 on targets before OSDU M26
    payloadContentType: application/x-parquet
    versionPath: recordIdVersions[0]
    skipDuplicates: false          # osduRecord, osduFile: opt in to skipdupes=true only once the target is confirmed to compare acl, legal and tags, not just data
    verifyBatchPath: /api/storage/v2/query/records   # the batched read a verify pass uses (100 ids per request)
    ddmsRoot: /api/os-wellbore-ddms  # osduWellLog: the endpoint is the platform root and the DDMS sits under this path; omit when the endpoint is the DDMS itself
    validateLegalTags: true        # deliver and intake runs ask the legal service about the mapping's legal tags first; false skips it
    legalValidatePath: /api/legal/v1/legaltags:validate  # where to ask; needed (as a whole URL) only for osduWellLog without ddmsRoot
    preserveDataKeys: [Datasets, DDMSDatasets, ExtensionProperties]
    batchSize: 100                 # records per write request where the service takes arrays (osduRecord, osduFile, osduManifest; at most 500)
    uploadUrlPath: /api/file/v2/files/uploadURL      # osduFile, osduManifest: the signed landing-zone location
    uploadUrlExpiry: 12H           # how long the signed URL stays valid (30M, 12H, 2D); default the service's one hour
    uploadHeaders: { x-ms-blob-type: BlockBlob }     # extra headers on the signed-URL upload; the Azure blob type is added for a *.blob.core.* URL anyway
    fileMetadataPath: /api/file/v2/files/metadata    # osduFile, osduManifest: registers the dataset record
    fileDeletePath: /api/file/v2/files/{id}/metadata # purge: deletes a dataset record and its file
    datasetKind: osdu:wks:dataset--File.Generic:1.0.0
    datasetsProperty: Datasets     # the record's data property listing its dataset ids
    workflowName: Osdu_ingest      # osduManifest: the ingestion workflow
    workflowRunPath: /api/workflow/v1/workflow/{workflow}/workflowRun
    workflowStatusPath: /api/workflow/v1/workflow/{workflow}/workflowRun/{runId}
    workflowPollSeconds: 10
    workflowTimeoutMinutes: 60     # a run still going after this fails the try; the next try resumes polling it
    datasetIndexWaitSeconds: 120   # osduManifest: how long to wait for the search index to list the registered datasets before the manifest names them (0 = no wait)
    searchQueryPath: /api/search/v2/query            # osduManifest: where that wait asks
    workflowAppKey: osdu-delivery  # executionContext.Payload.AppKey
    workflowPayload: {}            # extra executionContext.Payload entries
    manifestKind: osdu:wks:Manifest:1.0.0
    manifestSection: WorkProductComponents           # default derived from each record's kind
    recordQueryPath: /api/storage/v2/query/records   # reads the records back after a workflow run

reliability:
  concurrency: 8
  retry: { attempts: 4, backoff: exponential, baseDelayMs: 500, maxDelayMs: 30000, honorRetryAfter: true, recordBaseDelayMinutes: 1, recordMaxDelayMinutes: 60 }
  skipStatusCodes: [409]           # statuses that hold a record instead of retrying
  timeoutSeconds: 100
  rateLimitRps: 0                  # 0 = unlimited
  verifyTls: true
  urlAllowlist: []                 # SSRF allowlist; *.suffix wildcards
  maxResponseBytes: 67108864
  maxRequestBodyBytes: 0           # the target's declared request body ceiling; a bigger chunk holds the record (0 = not declared)
  leaseSeconds: 300
  batchSize: 50
  batchRecords: 500                # rendered documents per work batch file
  renderParallelism: 0             # renderers in the intake pipeline (0 = the machine's cores)
  fanOut: 0                        # member runs a large submission spreads over (0 = none; at most 64)
  fanOutMinRecords: 1000           # below this a submission never fans out

schedule:                          # service: what to run, when, and with which parameter values
  cron: "0 * * * *"
  timezone: UTC
  operation: deliver
  values: { logSource: STAT_COMP }  # required when the flow declares required parameters; a fire supplies nothing else
verify: { reconcile: false }       # whether the verify pass re-queues drifted or missing records
```

### Render-affecting versus operational

Only `render.*` enters the render context. Everything else changes how a document gets there: raising
`reliability.concurrency` or changing `target.endpoint` never redelivers a record. A moved render context (a new
mapping version, schema snapshot or reference snapshot) renders the record again, and whether it is sent is still
decided by the hash of the rendered document alone, so a new cache version that renders the same document sends nothing.

### Incremental drops: what changed since the last run

A drop does not have to carry every record. The rows it carries are planned; the records it leaves out are left
exactly as they are. Two watermarks tell the planner which of the rows it does carry have changed, and every row
that has goes through the whole pipeline: render, the preflight-checked mapping, the hash of the rendered document
against what OSDU holds, and the same hash check again by the worker just before anything is sent.

| Key | What it does |
| --- | --- |
| `source.lastModified` | A root-scope column saying when the source row last changed: a `timestamp`, or a `string` holding RFC 3339 / ISO 8601 text (without an offset it is read as UTC). A row modified after the version the ledger holds, delivered or queued, is planned; a row at the same moment is skipped without rendering; a row older than that version is **stale**, never sent, and recorded as a skipped attempt against the record. An empty or unreadable value holds the record with a reason naming the column. Declare it or `source.fingerprint`, not both: the fingerprint is compared only for equality, so it cannot tell a newer row from an older replay. |
| `change.payloadDetect: lastModified` | The payload's chunk files are its watermark. A payload is reconsidered when a chunk file was modified after the ones OSDU's payload was sent from, or the set of chunk files (names, sizes, times) changed; files older than the payload already delivered or queued are stale and never sent. When the drop still declares a `hashColumn`, that hash stays the final check, so rewritten files with the same content are not uploaded again; without one, the files themselves are the payload's identity. Costs one storage listing per record per run. |

A record whose newer version arrives while an earlier one is being delivered does not lose it: the new work queues
behind the delivery and the next pass of the same run sends it, after the final check has compared it with what just
landed. Concurrent intakes cannot take a record backwards either; the ledger refuses staged work older than what it
holds and records it as stale. The submission counts the skips (`skippedStale`), and the records the final check found
OSDU already holding (`unchangedAtPush`), beside the usual counts.

### Parameters

Flow parameters are supplied by `--set name=value` or by the manifest (`parameters`). When both are present
they must agree. `{name}` tokens are substituted in `source.location`, `source.knownState` and `source.work`, and
in a retrieval flow's `source.query` and `target.location`.

### Schedules

The inline `schedule` fires the flow on the platform scheduler; `operation` (deliver by default; verify, plan,
known-state, intake or drain, and retrieve or plan on a retrieval flow) is what every fire runs, and `values`
supplies the flow's own parameters. A flow that declares a required parameter **must** give the schedule values for
it: a fire supplies nothing on its own, so without them every run fails validation with "parameter 'name' is
required". A run-now's values override the schedule's name by name, leaving the rest in place. A nightly drift pass is a second schedule in the repository's schedule
library with `operation: verify` and the flow as its member. Run-now on a schedule keeps its operation and adds
`force`.

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

cache:                               # optional: the OSDU cache this flow keeps current for the mappings
  makeCurrent: true                  # the minted snapshot becomes what `references: pinned` resolves to
  snapshots: abfss://lake@acct.dfs.core.windows.net/osdu-snapshots   # the store the cache is minted into; the delivery flows name it as render.snapshots. Default: the nearest `snapshots` directory, which a platform run refuses because it runs from a copy of the repository
  onChange: approve                  # default for the types below: approve (wait for a decision) or auto
  types:
    - kind: "osdu:wks:master-data--Wellbore:1.0.0"   # optional when the flow retrieves exactly one kind
      name: Wellbore                 # optional: derived from the entity type in the kind
      entityType: master-data--Wellbore              # optional: derived from the kind
      query: "*"                     # optional; {parameter} tokens
      onChange: auto                 # optional per-type override of cache.onChange
      fields:                        # the paths to cache; a path crosses arrays implicitly
        - data.FacilityName
        - path: data.NameAlias.AliasName
          as: Alias

reliability: { concurrency: 4, retry: { attempts: 4 } }   # kinds retrieved at once; the HTTP settings as on a delivery flow
schedule: { cron: "0 3 * * *", timezone: UTC, operation: retrieve }
```

| Key | Meaning |
| --- | --- |
| `source.kinds` | The kinds to retrieve, `authority:source:entityType:version` with wildcards per segment. Each is one cursor; they run concurrently up to `reliability.concurrency`. |
| `source.query` | A Lucene query narrowing the kinds. The window of an incremental flow is appended as `AND field:[from TO to}`. |
| `source.incremental` | The watermark: a run covers `[last completed run's upper bound, now minus lag)` on `field`. Only a completed run advances it; `--force` restarts at `since`. |
| `source.fetchRecords` | The index holds a projection; set this to land the record as storage holds it. Ids storage cannot return are counted and listed in the manifest. |
| `target.location` | The run's directory root. Without a `{run}` token every run gets a timestamped directory beneath it, so runs never overwrite each other. |
| `target.rollRecords` | A new file every this many records: `part-00001.jsonl[.gz]`, `part-00002...` under a directory named after the kind. |
| `cache.types` | The OSDU types this flow keeps cached for the mappings to resolve against ([design.md](design.md) section 6.2). A retrieve run sweeps each in full and mints a reference snapshot version merged onto the current one, so a version always describes the whole cache. |
| `cache.types[].fields` | The paths to cache, written bare (`data.Code`, cached as `Code`) or as `{ path: ..., as: ... }`. Whatever a path yields is cached as it is: a scalar, a set of values, or a nested object. A path crosses arrays implicitly, so `data.NameAlias.AliasName` reaches through an array of objects and caches the set of aliases it finds. A path that yields nothing on every record is reported at capture. |
| `cache.onChange` | What a changed cached value does to the records already built from it. `approve` (the default) tags them and holds them back until someone approves the update in the GUI; `auto` tags them and lets the next run carry the new document. Set per flow and overridden per type, because a code list that is corrected in place and a master-data name that is edited daily do not deserve the same treatment. |

The cache sweep is independent of `source.incremental`: the window governs which records land as files, while the
cache is captured in full, because a cache holding only the last hour's changes cannot answer a lookup.

A refresh does not only mint a version. Every delivered manifest row points at the set of cached values it was
built from, so the refresh compares the new version against the one it replaces and raises one tag per changed
value: the cached record, the path, the value before and after, and how many delivered records it reaches. A tag
under `approve` holds those records back (a plan skips them, so OSDU keeps the documents it has) until someone
approves or rejects it; a tag under `auto` is approved as it is written. If a value moves again after approval but
before the rollout carried it, the tag reopens, because the approval was for the value someone looked at.

An approved change is carried out in batches by the control plane (`ControlPlane:CacheRollout`: `BatchSize`
records per batch, `BatchesPerPass` batches every `PollSeconds`), so a change reaching millions of records drains
at a set pace rather than in one statement, and resumes where it stopped after a restart. The redelivery is
metadata only: a cached value that changed rewrites the manifest row and never re-uploads its payload. The GUI's
OSDU cache page shows all of it: what each flow declares, what the current snapshot holds, and the changes with
their record counts and rollout progress.

A run's directory holds the files per kind and the manifest: the flow, the run, the window, every file with its
record count and uncompressed bytes, and per kind the records storage could not read back. The ledger's
`delivery.Retrieval` row carries the same counts, the outcome and the run id; the pipeline's Retrievals tab lists
them. The operations are `retrieve` (the default for a retrieval flow) and `plan` (count what the query matches,
write nothing).

## Mapping

```yaml
documentType: mapping
name: WellLog
version: 1.4.0                     # part of the render context
kind: osdu:wks:work-product-component--WellLog:1.4.0   # pins the schema snapshot; must match the storage pattern authority:source:entityType:major.minor.patch

source:
  system: recall                   # enters the delivery key
  scopes: [curves]                 # child scopes collections iterate

identity:
  naturalKey: [data.LogSource, data.LogRun]   # mapped properties whose source columns form the key
  label: "{wellbore_uwi} / {log_name} / run {log_run}"   # display and search only; never in the document

envelope:
  legalTags: [...]                 # at least one, no repeats (the legal lists are sets to storage)
  otherRelevantDataCountries: [NO] # at least one, no repeats
  acl: { owners: [...], viewers: [...] }
  tags: { DeliveredBy: osdu-delivery }        # static tags (optional)

parameters:                        # what the mapping accepts from the flow; values enter the hash
  dataPartition: { required: true }           # always required: ids are minted in it, so letters, digits, _ - . only

properties:
  - target: data.Name              # dotted path from the record root (data.*, tags.*)
    source: log_name               # column in the current scope
    transform: trim                # see the vocabulary below
    config: { ... }
    examples:                      # per-property fixtures, checked by the preflight gate
      - { source: "STAT_COMP ", target: STAT_COMP }
  - target: data.VerticalMeasurement
    properties: [ ... ]            # nested object
  - target: data.Curves
    collection: true               # array of objects, one per row of the scope
    scope: curves
    definition: Curve              # or inline properties

definitions:
  Curve: [ ... ]                   # reusable nested property lists

fixtures:                          # whole-document regression fixtures
  - name: ...
    parameters: { dataPartition: dev }
    record: { column: value, ... }
    scopes: { curves: [ { ... } ] }
    expected: |
      { ...the exact document... }
```

### Transform vocabulary

| Transform | Config | Result |
| --- | --- | --- |
| *(none)* | | The source value, coerced to the schema type. |
| `constant` | `value` (may use `{param:name}`) | A literal. |
| `trim`, `upper`, `lower` | | String operations. |
| `split` | `delimiter`, `index` | One segment; a missing segment omits the property. A space delimiter splits on any whitespace. |
| `equals` | `resolve` | Boolean, case-insensitive. |
| `map` | `values`, `default`, `onMiss` | Dictionary lookup. |
| `reference` | `type`, `matchBy`, `valueMap`, `onMiss`, optional `delimiter`/`index` | An OSDU reference (`id:`) resolved from the reference snapshot. Already-formed ids pass through. |
| `lookup` | `type`, `matchBy`, `select`, `valueMap`, `onMiss`, optional `delimiter`/`index` | A value read out of the cached record the source value matches: the same match as `reference`, but `select` names what to take from it (`Name`, `NameAlias.AliasName`, or `id`, the default). |
| `deliveredReference` | `type` (entity type), `system`, `keys` | The computed id of a record this system also delivers. |
| `template` | `format` with `{column}` and `{param:name}` tokens | A formatted string. |
| `dateTime` | `inputFormat` | RFC 3339 UTC. |

`onMiss` is `hold` (default: the record is held until intervention), `omit` (drop the property) or `error`.

`matchBy` and `select` name cached fields by the name capture stored them under (the path without its `data.`
root, or the `as` it declared), and a path inside one (`NameAlias.AliasName`) when the field was cached whole. A
field holding a set matches on any one of its values, so a record with three aliases is found by any of them. An exact
match wins, and case is ignored only when that finds exactly one record: OSDU codes that differ only by case are
different records (`ft` is the foot and `fT` the femtotesla, `s/m` second per metre and `S/m` siemens per metre), so
a value that names several of them once case is ignored is unresolved (`onMiss` decides what that does) with a reason
naming them, and `valueMap` maps it to the exact code. Two records holding exactly the same value resolve to the first
in snapshot order, which is stable for a snapshot version. A cached
field of its own called `ID` shadows the record id under that name, so `matchBy: [ID]` reads what OSDU calls
`data.ID` while `matchBy: [id]` on a type caching no such field reads the record id. A `lookup` whose `select`
yields a set writes an array where the schema takes one, and holds the record where it takes a single value.

### Coercion

The schema decides the type. Strings become numbers, integers or booleans as the schema says; a value that
cannot be coerced holds the record with a reason naming the property. Nulls are omitted, never emitted.

### What the preflight gate checks

1. Every source binding exists in the drop's declared schema.
2. Every reference type exists in the reference snapshot, and holds the fields the mapping matches by and the value
   a `lookup` selects. A `matchBy` field the cache does not hold is a warning; a type holding none of them, or a
   `select` path nothing caches, is an error, because it would hold every record at run time.
3. Every schema-required property has a binding.
4. Every target path exists in the pinned schema with an agreeing shape.
5. Every example and fixture renders exactly as declared under this context.

If any check fails, nothing renders.
