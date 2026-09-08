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
  fingerprint: update_date         # root-scope column for the tier-1 gate (optional)
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
  payloadDetect: contentHash       # contentHash | always
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
    data-partition-id: dev
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
    sessionThresholdChunks: 1      # more chunks than this opens a session
    payloadContentType: application/x-parquet
    versionPath: recordIdVersions[0]
    preserveDataKeys: [Datasets, DDMSDatasets, ExtensionProperties]
    batchSize: 100                 # records per write request where the service takes arrays (osduRecord, osduFile, osduManifest; at most 500)
    uploadUrlPath: /api/file/v2/files/uploadURL      # osduFile, osduManifest: the signed landing-zone location
    uploadUrlExpiry: 12H           # how long the signed URL stays valid (30M, 12H, 2D); default the service's one hour
    uploadHeaders: { x-ms-blob-type: BlockBlob }     # headers on the upload to the signed URL itself
    fileMetadataPath: /api/file/v2/files/metadata    # osduFile: registers the dataset record
    fileDeletePath: /api/file/v2/files/{id}/metadata # purge: deletes a dataset record and its file
    datasetKind: osdu:wks:dataset--File.Generic:1.0.0
    datasetsProperty: Datasets     # the record's data property listing its dataset ids
    workflowName: Osdu_ingest      # osduManifest: the ingestion workflow
    workflowRunPath: /api/workflow/v1/workflow/{workflow}/workflowRun
    workflowStatusPath: /api/workflow/v1/workflow/{workflow}/workflowRun/{runId}
    workflowPollSeconds: 10
    workflowTimeoutMinutes: 60     # a run still going after this fails the try; the next try resumes polling it
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

schedule: { cron: "0 * * * *", timezone: UTC, operation: deliver }   # service: what to run, and when
verify: { reconcile: false }       # whether the verify pass re-queues drifted or missing records
```

### Render-affecting versus operational

Only `render.*` enters the render context and therefore the content hash. Everything else changes how a
document gets there. Raising `reliability.concurrency` or changing `target.endpoint` never redelivers a record.

### Parameters

Flow parameters are supplied by `--set name=value` or by the manifest (`parameters`). When both are present
they must agree. `{name}` tokens are substituted in `source.location`, `source.knownState` and `source.work`, and
in a retrieval flow's `source.query` and `target.location`.

### Schedules

The inline `schedule` fires the flow on the platform scheduler; `operation` (deliver by default; verify, plan,
known-state, intake or drain, and retrieve or plan on a retrieval flow) is what every fire runs. A nightly drift pass is a second schedule in the repository's schedule
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
  headers: { data-partition-id: dev }
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
| `target.location` | The run's directory root. Without a `{run}` token every run gets a timestamped directory beneath it, so runs never overwrite each other. |
| `target.rollRecords` | A new file every this many records: `part-00001.jsonl[.gz]`, `part-00002...` under a directory named after the kind. |

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
kind: osdu:wks:work-product-component--WellLog:1.4.0   # pins the schema snapshot

source:
  system: recall                   # enters the delivery key
  scopes: [curves]                 # child scopes collections iterate

identity:
  naturalKey: [data.LogSource, data.LogRun]   # mapped properties whose source columns form the key
  label: "{wellbore_uwi} / {log_name} / run {log_run}"   # display and search only; never in the document

envelope:
  legalTags: [...]
  otherRelevantDataCountries: [NO]
  acl: { owners: [...], viewers: [...] }
  tags: { DeliveredBy: osdu-delivery }        # static tags (optional)

parameters:                        # what the mapping accepts from the flow; values enter the hash
  dataPartition: { required: true }           # always required: ids are minted in it

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
| `deliveredReference` | `type` (entity type), `system`, `keys` | The computed id of a record this system also delivers. |
| `template` | `format` with `{column}` and `{param:name}` tokens | A formatted string. |
| `dateTime` | `inputFormat` | RFC 3339 UTC. |

`onMiss` is `hold` (default: the record is held until intervention), `omit` (drop the property) or `error`.

### Coercion

The schema decides the type. Strings become numbers, integers or booleans as the schema says; a value that
cannot be coerced holds the record with a reason naming the property. Nulls are omitted, never emitted.

### What the preflight gate checks

1. Every source binding exists in the drop's declared schema.
2. Every reference type exists in the reference snapshot.
3. Every schema-required property has a binding.
4. Every target path exists in the pinned schema with an agreeing shape.
5. Every example and fixture renders exactly as declared under this context.

If any check fails, nothing renders.
