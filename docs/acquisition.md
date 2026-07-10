# Generic acquisition (flowType: acq)

One declarative engine that replaces the estate's Azure Automation runbooks. An `acq` flow fetches from a
third-party system (an HTTP API, an SFTP server, an S3 bucket, or an Azure Storage Table) and lands the **raw**
response payloads as files in the data lake, one file per page/iteration, preserving the
original format byte-for-byte. It performs no CSV/flatten transform: the existing SQLFlow `json`/`csv`/`xml`
file-flows ingest the landed files into SQL. Integrations are now version-controlled YAML managed centrally
alongside every other pipeline, testable and schedulable through the same control plane.

## Why a two-stage split

The old runbooks fetched *and* reshaped to CSV *and* uploaded, in one opaque script. This engine does only
acquisition and lands raw. That keeps one code path (ingestion stays the mature file-flow path), makes a fetch
replayable, and means a schema change downstream never requires touching the integration.

    [acq flow] --raw JSON/XML/bin--> raw zone --existing json/csv/xml flow--> SQL

## Run it

    sqlflow run samples/acquire/jsonplaceholder-basic.flow.yaml     # live, no auth, lands to ./_landing

Incremental resume, retries, rate limiting, run history, and scheduling all come from the shared engine.

## Runbook pattern -> YAML feature coverage

Every capability observed across the 40 runbooks maps to a schema feature. The runbooks were used only to derive
the patterns; the engine is verified against public APIs and a deterministic test suite.

| Runbook pattern (examples) | YAML feature |
| --- | --- |
| No auth (SVV, opencom, geonorge, stavanger) | `auth.type: none` |
| Static bearer token (shiplog, norled, ryde, hentmeg) | `auth.type: bearer` + `secretRef` |
| API key header, custom name (App Insights `X-Api-Key`) | `auth.type: api_key_header` + `headerName` |
| Token as query param (Skynet `?token=`) | `auth.type: api_key_query` + `paramName` |
| Non-standard bearer header (easypark `X-Authorization`) | `token.applyHeaderName` + `applyPrefix` |
| OAuth2 client-credentials, form body (Voi, frida) | `auth.type: oauth2_client_credentials`, `token.bodyKind: form` |
| OAuth2 client-credentials, JSON body + audience (Entur) | `token.bodyKind: json`, `token.body` |
| OAuth2 Basic-auth client (Voi id:secret) | `token.basicAuthClient: true` |
| OAuth2 refresh-token exchange (easypark) | `auth.type: oauth2_refresh_token` |
| OIDC discovery for the token endpoint (frida) | `token.discoveryUrl` |
| Custom token exchange, pre-formed form body in KV (Citybike) | `auth.type: token_exchange`, `token.rawBody` |
| Per-window token refresh (Voi) | `token.refreshPerIteration: true` |
| GET / POST | `request.method` |
| GraphQL POST (SVV) | `request.bodyKind: graphql` |
| SOAP-over-POST envelope (Questback) | `request.bodyKind: soap` |
| JSON / form-urlencoded body | `request.bodyKind: json` / `form` + `bodyFields` |
| Path + query templating with date/id tokens | `{placeholder}`, `{yyyyMMdd}`, `{window.from:fmt}` |
| Page-number pagination (Questback, frida) | `pagination.strategy: page` |
| Offset/limit pagination | `pagination.strategy: offset` |
| Cursor-in-body pagination | `pagination.strategy: cursor_body` + `cursorPath` |
| Link-header pagination (GitHub-style, SVV NVDB next-href) | `pagination.strategy: link_header` |
| Keyset / `idAfter` resume, stop on sentinel status (Entur) | `pagination.strategy: keyset`, `stopOnStatus` |
| Date window day-by-day (easypark, fjord1, shiplog) | `iterate: date_window`, `granularity: day` |
| Hourly sub-window (Voi, Ryde 24x/day) | `granularity: hour` |
| Monthly window (svv_index) | `granularity: month` |
| Static list fan-out (operatorIds, routes, lines) | `iterate: list` + `values` |
| Ids from a prior call (bikes -> alerts/sessions) | `iterate: ids_from` + `idRequest` + `idPath` |
| Batched-id chunking, max N per request (svv 50 ids) | `ids_from` + `batchSize` + `batchSeparator` |
| Retry with backoff (shiplog, norled, questback) | `reliability.retry` |
| Honor Retry-After, retry 408/425/429/5xx | built in (`retry.honorRetryAfter`) |
| Rate limiting / request budget (svv batching) | `reliability.rateLimitRps` |
| Per-request timeout (60/120/300s) | `reliability.timeoutSeconds` |
| Empty-data skip (easypark, voi) | `landing.skipEmpty` |
| History path with year/month/day + id tokens | `landing.pathTemplate` |
| Raw JSON passthrough (fjord1/norled json) | `landing.format: json` (or `auto`) |
| Binary/XLSX response (Entur report) | `landing.format: auto` (lands `.bin`/declared ext) |
| gzip on landing | `landing.compression: gzip` |
| Incremental resume from lake state (Entur lastReportId) | `incremental` + keyset |
| SFTP download, modified-within window (Citybike/Nets, Ferde) | `transport: sftp` |
| AWS S3 object download (mobilapp) | `transport: s3` |
| Azure Storage Table query (fjord1, apc) | `transport: azuretable` + OData `filter` |
| Secrets from Key Vault, never inline | `${keyvault:vault/secret}` refs |
| SSRF safety (block metadata/private IPs) | `reliability.urlAllowlist` + built-in IP guard |

### Residual runbooks (not API integrations)

Three runbooks are not third-party integrations and stay outside this engine: the two Azure Automation job-status
log exports (`Fail_jobs`, `Maintenance`) read the Azure control plane, and `baatbooking`/`Z_ebk_test_72` copy
lake-to-lake. The first two are platform observability; the copy is an ingestion concern, not an acquisition one.

## Debugging

The GUI's **Integrations** page lists every `acq` flow with its YAML definition (View code) and provides a
DeltaForge-style debugger: edit the YAML, run a safe **Test** invoke (fetch without landing), and inspect the
request, the raw response, timing, the pagination steps, and where each page would land. Production runs go through
the normal run/schedule path like any other pipeline.
