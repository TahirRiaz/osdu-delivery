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
  protocol: osduWellLog            # osduRecord | osduWellLog (osduFile, osduManifest reserved)
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

schedule: { cron: "0 * * * *", timeZone: UTC, operation: deliver }   # service: what to run, and when
verify: { reconcile: false }       # whether the verify pass re-queues drifted or missing records
```

### Render-affecting versus operational

Only `render.*` enters the render context and therefore the content hash. Everything else changes how a
document gets there. Raising `reliability.concurrency` or changing `target.endpoint` never redelivers a record.

### Parameters

Flow parameters are supplied by `--set name=value` or by the manifest (`parameters`). When both are present
they must agree. `{name}` tokens are substituted in `source.location` and `source.knownState`.

### Schedules

The inline `schedule` fires the flow on the platform scheduler; `operation` (deliver by default; verify, plan or
known-state) is what every fire runs. A nightly drift pass is a second schedule in the repository's schedule
library with `operation: verify` and the flow as its member. Run-now on a schedule keeps its operation and adds
`force`.

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
