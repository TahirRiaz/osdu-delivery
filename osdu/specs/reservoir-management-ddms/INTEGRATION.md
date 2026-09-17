# Reservoir Management DDMS: integration brief

The Reservoir Management DDMS (RM DDMS, version 1.8.0) is a FastAPI service that keeps reservoir management data
(estimated volumes, PVT and fluid syntheses, relative permeability and Phi-K syntheses, tank and aquifer data,
forecasts) in its own PostgreSQL database and mirrors nine of its object types as OSDU Storage records. Stage 7 of
`docs/osdu-coverage-plan.md` builds OSDU Delivery's route for this service from this brief. The brief lists every call
a writer makes with its contract citation, and records what the service's code and its OSDU client library do where
the generated contract is silent or wrong.

## Sources

| Key | Source | Pinned at |
| --- | --- | --- |
| `C` | `osdu/specs/reservoir-management-ddms/openapi.generated.json`: the OpenAPI 3.0.2 document the service's FastAPI app generates, written by `tools/generate-rmddms-openapi.py`. The service publishes no static contract; this is the contract the route is designed against. | Project 1470 at commit `088ae063eaedfb6ad24c9d8c3e1f52c2cb45d2cd` (`osdu/specs/sources.json`). Regenerating it from that commit gives a byte-identical file. |
| `PM` | `osdu/specs/reservoir-management-ddms/postman-collection.json`: copy of the service's `docs/Reservoir Management Domain Data Management Service.postman_collection.json` (Postman schema v2.0.0) | Same commit; byte-identical to the upstream file. |
| `RD` | `osdu/specs/reservoir-management-ddms/README.md`: copy of the service's root `README.md` | Same commit; byte-identical to the upstream file. |
| `ST`, `SE`, `SC` | `osdu/specs/core/storage/openapi.yaml` (Storage v2), `osdu/specs/core/search/openapi.yaml` (Search v2) and `osdu/specs/core/schema_service/openapi.yaml` (Schema v1), from the core specification set | `osdu/specs/sources.json` records the copy date (2026-09-16) and size; the core specification set carries no commit. |
| `1470` | GitLab project 1470, `osdu/platform/domain-data-mgmt-services/reservoir-management/reservoir-management-domain-services`, branch `main`: the service's source (`app/`, `tests/resources/`, `docs/README.md`, `Dockerfile`, `.env.sample`, `requirements.txt`, `app/db/rmddms-db-dump.sql`) | Commit `088ae063eaedfb6ad24c9d8c3e1f52c2cb45d2cd` ("RM DDMS V1.8.0", 2024-04-23). `main` is the project's only branch, this commit was its head on 2026-09-16, and the project has no tags. All 129 files of the tree at this commit were read. |
| `SDK26` | GitLab project 148, `osdu/platform/system/sdks/common-python-sdk`, tag `v0.26.0`: the `osdu_api` package 0.26.0 | Commit `77ac49617b2b21942b72cefff1e4d9c62657becb` (2024-04-09). |
| `SDK28` | Project 148, tag `v0.28.0`: `osdu_api` 0.28.0 | Commit `cc5493fbc38afa632fb8b6ff74258326c063cabf` (2025-02-24). |
| `SDK120` | Project 148, the commit the project's package registry built `osdu_api` 1.2.0 from | Commit `cd71e9ed3745ea41ba9892c10480c73079cd15af` (2026-04-08). |
| `ID` | `docs/interfaces-design.md` in this repository; section 5.4 defines the `wellboreDdmsV3` route shape | This repository. |
| `WB` | `osdu/specs/wellbore-ddms/openapi.json`: the Wellbore DDMS contract (project 98, `osdu/platform/domain-data-mgmt-services/wellbore/wellbore-domain-services`), used here only for its path prefix | Commit `e5641a0e4e2dde1c67c12eb916087b67e8087c43` (`osdu/specs/sources.json`). |

**Why three SDK versions.** `requirements.txt` installs `osdu-api` unpinned from project 148's package registry
[1470 requirements.txt:14-15], so the version depends on when the image is built. 0.26.0 was the newest final release
on the service's commit date (PyPI's first `osdu-api` release is 1.0.3, from 2025-06-26). A resolution of
`requirements.txt` on 2026-09-17 selects 0.28.0, because the 1.x releases require `requests>=2.32.3`,
`tenacity>=9.1.2` and `httpx>=0.28.1` [SDK120 pyproject.toml:20-23] while the service pins `requests~=2.31.0`,
`tenacity~=8.2.3` and `httpx~=0.24.1` [1470 requirements.txt:4, 6, 13]. SDK120 is cited only where it differs.
`requirements.txt` and `requirements-dev.txt` are UTF-16 files.

**How statements are marked.**

- `[C: METHOD path, operationId]` cites the generated contract. In the family tables, `{c}` is a collection segment
  (for example `tank-datum`), `{c_}` is the same segment with underscores (`tank_datum`), `{x}` is the path variable
  `{c_}_id` (`tank_datum_id`) and `{p}` is the parent's path variable. The appendix lists all 117 concrete operations.
- `[PM]`, `[RD:lines]`, `[ST: ...]`, `[SE: ...]`, `[SC: ...]` cite the other pinned files.
- `[1470 path:lines]`, `[SDK26 path:lines]`, `[SDK28 path:lines]` and `[SDK120 path:lines]` cite source code. Behaviour
  that only code shows carries a source citation, never a contract citation.
- **(run)** marks a statement confirmed by running the app. The app at the pinned commit was exercised in-process
  with FastAPI's test client on Python 3.12.10, with the dependency versions the service's requirements resolve
  (FastAPI 0.88.0, Starlette 0.22.0, Pydantic 1.10.26, SQLAlchemy 2.0.54, psycopg2-binary 2.9.13, requests 2.31.0,
  PyJWT 2.8.0), against a disposable PostgreSQL 16.2 database created from the service's own
  `app/db/rmddms-db-dump.sql`. Token validation was replaced by fixed claims, except where the token checks were the
  subject. `osdu_api` was either replaced by recording fakes or, for every statement about HTTP requests to OSDU,
  loaded unmodified from its 0.26.0 and 0.28.0 wheels with the HTTP functions replaced by a recording transport.
  Nothing reached a network.
- **(inference)** marks a conclusion drawn from the cited sources that was not observed.

## Key findings

1. **Two route families, JSON only.** Two generic router classes generate every route: 9 "header" collections that
   are mirrored as OSDU records, and 12 child collections held only in the service's database. With the health check
   the contract has 84 paths and 117 operations [C]. There are no file, multipart, parquet, streaming or session
   routes.
2. **No version in the path; partition in the query.** Paths are `/ddms/{c}...`; version 1.8.0 appears only as
   `info.version` [C]. The partition is the query parameter `data_partition_id`, and only header routes take it.
3. **Ids travel in the query string.** Get-by-id and delete ignore their `{x}` path segment; the id is the query
   parameter `catalog_entity_id` (run).
4. **The one OSDU write does not name its record.** `PUT /ddms/{c}` takes an array of complete storage records and
   requires the first record and its parent to exist already in Storage. It then sends Storage `PUT /records` with
   every record's `id` set to `null` (run). The Storage contract says a record without an id is created as a new
   record [ST: PUT /records, createOrUpdateRecords]. The answer is the service's local rows; Storage's `recordIds` and
   `recordIdVersions` are discarded (run).
5. **Its delete purges.** With the `osdu_api` versions the service's requirements resolve, `DELETE /ddms/{c}/{x}`
   sends Storage `DELETE /records/{id}`, the irreversible purge [ST: DELETE /records/{id}, purgeRecord] (run), and
   then answers 500 on Storage's empty 204 while keeping the local row (run). OSDU Delivery must never call it.
6. **Tabular data never reaches OSDU.** Child rows (Kr and Phi-K tables, PVT and black-oil tables, forecast steps) are
   posted one row per call and stored only in PostgreSQL; their integer keys come from database sequences.
7. **Preconditions outside the API.** Header rows appear only through the list route's sync with Search, which needs
   `pool` master rows and reference rows inserted in the database directly, and which cannot insert Kr synthesis rows
   at all on a database built from the dump (run).
8. **Outcomes need the body as well as the status.** Some failures answer 200 with a one-string array; most malformed
   inputs and every OSDU error on the write path answer 500 (run).

## 1. Base path, versions, headers and auth

### 1.1 Base path and version

- The app is `FastAPI(title=..., description=..., summary=..., version="1.8.0", license_info=..., openapi_tags=...)`
  [1470 app/main.py:32-42]. The generated document has `openapi: 3.0.2`, the title "Reservoir Management Domain Data
  Management Service", version 1.8.0, an Apache 2.0 license, 22 tags and no `servers` entry; FastAPI 0.88 does not
  emit the `summary` argument [C].
- The health router is mounted at the root and the resource routers under `PREFIX = "/ddms"` [1470
  app/main.py:52-58, app/core/constants.py:17]. Each resource router sets its own prefix, for example
  `prefix = "/estimated-volumes"` [1470 app/api/routers/src/estimated_volume.py:20], through
  `APIRouter(prefix=..., tags=[...], responses={404: ...}, dependencies=[Depends(authentication_required)])` [1470
  app/api/routers/router_api.py:24-31]. The 21 resource routers are included in [1470
  app/api/routers/_endpoint_routes.py:41-62].
- URL shape: `{service root}/ddms/{c}...`. No path has a version segment [C].
- The project defines neither the deployed host nor an ingress prefix: the contract has no `servers`, and the Postman
  collection's only variable is `baseUrl` with the value `/` [PM].
- The image runs `python -m uvicorn app.main:app --host 0.0.0.0 --port 8000` on `python:3.11-slim` [1470
  Dockerfile:1, 25]. The README shows `uvicorn main:app --port LOCAL_PORT` and the `/docs` page [RD:117-123,
  131-133].
- FastAPI's `/openapi.json`, `/docs` and `/redoc` are registered and answer 200 without a token (run). There is no
  info or version route; `GET /` is the health check (section 5.4).

### 1.2 Headers

| Header | On | Source |
| --- | --- | --- |
| `Authorization: Bearer <token>` | every `/ddms/...` operation | router dependency [1470 app/api/routers/router_api.py:29, app/api/dependencies/auth.py:21-32]; security scheme `HTTPBearer` on 116 operations, none on `GET /` [C] |
| `Content-Type: application/json` | PUT and POST | the only content type in the contract [C]. A body sent as `text/plain` reaches the code as bytes and answers 500; a body without a content type is parsed as JSON (run). |
| `Accept: application/json` | every request | [PM] |

- The service reads no `data-partition-id` header. The only header its code reads is `authorization` [1470
  app/services/osdu_api/osdu_client_service.py:38], and it passes that value unchanged as the bearer token of every
  OSDU call [1470 app/services/osdu_api/osdu_client_service.py:43, 52, 59, 86, 95].
- The partition is `data_partition_id`, a plain function argument and therefore a required query parameter [1470
  app/api/routers/router_base.py:51, 80, 109, 138, 163], on the 45 header-collection operations [C]. A PUT without it
  answers 422 (run). Child-collection routes take no partition.
- The Postman collection sets `auth: bearer {{bearerToken}}` on the 116 authenticated requests and none on the health
  check, but defines no `bearerToken` variable. The original request of each saved example response carries
  `Authorization: Bearer <token>` with the note "Added as a part of security scheme: bearer" (423 times) [PM].

### 1.3 Authentication

- The router dependency is FastAPI's `HTTPBearer()` [1470 app/api/dependencies/auth.py:21]. Without the header the
  answer is 403 `{"detail":"Not authenticated"}`; with another scheme, such as `Basic`, it is 403
  `{"detail":"Invalid authentication credentials"}` (run).
- The service validates the token itself against Azure AD [1470 app/core/aad_token_verify/token_verifier.py:26-59,
  118-158, app/services/authservice.py:39-48]. It reads the token header's `kid`, loads
  `https://login.microsoftonline.com/{TENANT_ID}/.well-known/openid-configuration` and the keys at its `jwks_uri` (each
  cached for one hour), and decodes with `algorithms=["RS256"]`, `audience=SCOPE` and that configuration's `issuer`.
- With PyJWT 2.8.0 the token's `aud` must equal the `SCOPE` setting exactly, or be a list that contains it (run). The
  README and `.env.sample` show `SCOPE` in the form `<guid>/.default` [RD:107, 1470 .env.sample:4].

| Token | Answer |
| --- | --- |
| expired, wrong audience or wrong issuer (`AuthorizationError`) | 401 `{"detail":"Your token expired, please provide a new correct one."}` [1470 app/services/authservice.py:46-47] (run) |
| not a JWT, or no `kid` (`TokenParseError`) | 500 (run) |
| bad signature, no `aud` claim, or any other PyJWT error | 500: PyJWT 2.8.0 raises `InvalidSignatureError` and `MissingRequiredClaimError` for the first two (run), and the verifier maps only the three errors above [1470 app/core/aad_token_verify/token_verifier.py:53-58] |
| unknown `kid` (`GetPublicKeyFromToken`), OpenID configuration or key download failure (`AADError`, `requests` errors) | 500 [1470 app/core/aad_token_verify/token_verifier.py:100-115, 132-143, 156-158] |
| valid, but without the `name` or `unique_name` claim | 500, `KeyError` [1470 app/services/authservice.py:30-31, 45] (run) |

- `Request.identity` is assigned on the Starlette `Request` class, not on the request [1470
  app/services/authservice.py:45], so concurrent requests share it; nothing in the service reads it.
- Settings [1470 app/core/config.py:27-39]: `TENANT_ID`, `SCOPE`, `POSTGRES_USER`, `POSTGRES_PASSWORD`,
  `POSTGRES_SERVER`, `POSTGRES_PORT` and `POSTGRES_DB` (combined into `DATABASE_URL`), `BACKEND_CORS_ORIGINS` and
  `OSDU_HOST`. `CLIENT_ID` and `CLIENT_SECRET` are declared and read nowhere else under `app/`.
- Importing the app fails when `POSTGRES_PASSWORD`, `POSTGRES_PORT`, `BACKEND_CORS_ORIGINS` or `OSDU_HOST` is unset
  (run). `.env.sample` has no `OSDU_HOST` line [1470 .env.sample], and the image sets only `CLIENT_ID`,
  `CLIENT_SECRET`, `TENANT_ID`, `SCOPE`, `OSDU_HOST` and `POSTGRES_DB` [1470 Dockerfile:5-17].

### 1.4 What the service calls in OSDU

The service builds `{OSDU_HOST}/api/schema-service/v1`, `{OSDU_HOST}/api/storage/v2` and `{OSDU_HOST}/api/search/v2`
[1470 app/core/constants.py:21-23, app/services/osdu_api/osdu_client_service.py:32-37], the base paths the core
contracts declare [SC] [ST] [SE]. Every call goes through `osdu_api` with the caller's token:

| Wrapper in [1470 app/services/osdu_api/osdu_client_service.py] | Request sent (run, SDK26 and SDK28) | Core contract |
| --- | --- | --- |
| `get_osdu_schema(kind)`, lines 42-44 | `GET /api/schema-service/v1/schema/{kind}` [SDK26 osdu_api/clients/schema/schema_client.py:46-48] | [SC: GET /schema/{id}, getSchema] |
| `get_all_records_from_kind(kind)`, lines 46-55 | `POST /api/search/v2/query` with `{"kind": kind, "query": "", "limit": 100, "returnHighlightedFields": true, "returnedFields": ["id"]}` and `null` for `aggregateBy`, `cursor`, `from`, `offset`, `queryAsOwner`, `sort` and `spatialFilter` [SDK26 osdu_api/clients/search/search_client.py:46-49, osdu_api/model/search/query_request.py:24-60] | [SE: POST /query, queryRecords] |
| `get_osdu_record(id)`, lines 57-62 | `GET /api/storage/v2/records/{id}` [SDK26 osdu_api/clients/storage/record_client.py:70-76] | [ST: GET /records/{id}, getLatestRecordVersion] |
| `put_osdu_record(records)`, lines 64-91 | `PUT /api/storage/v2/records` with a JSON array (section 2.6) [SDK26 osdu_api/clients/storage/record_client.py:46-68] | [ST: PUT /records, createOrUpdateRecords] |
| `delete_osdu_record(id)`, lines 93-98 | `DELETE /api/storage/v2/records/{id}` [SDK26 osdu_api/clients/storage/record_client.py:94-95] | [ST: DELETE /records/{id}, purgeRecord] |

The SDK28 files cited in this table are identical to the SDK26 ones at the same lines.

- Every request carries `content-type: application/json`, `data-partition-id` with the `data_partition_id` query
  value, and `Authorization` with the caller's header value; the library adds the `Bearer` prefix only when it is
  missing, so the value goes out unchanged [SDK26 osdu_api/clients/base_client.py:186-188, 232-235; SDK28
  osdu_api/clients/base_client.py:193-195, 239-242] (run). The core contracts require the `data-partition-id` header
  on all five operations [ST] [SE] [SC].
- A non-2xx answer raises `requests.HTTPError` [SDK26 osdu_api/clients/base_client.py:192-193; SDK28
  osdu_api/clients/base_client.py:199-200]. Nothing retries: the library's retry decorator wraps only its
  token-refresher path [SDK26 osdu_api/clients/base_client.py:197-213].
- TLS: 0.26.0 sends every request with `verify=False` [SDK26 osdu_api/clients/base_client.py:122-131]; 0.28.0 checks
  certificates unless `OSDU_API_DISABLE_SSL_VERIFICAITON=iknowthemaninthemiddle` is set [SDK28
  osdu_api/clients/base_client.py:52, 77-78, 127-138]. With 0.26.0 the caller's token travels over connections whose
  certificates are not checked.
- The clients need no `osdu_api.ini`: the library's configuration error is caught and the URLs and partition come
  from the constructor arguments [SDK26 osdu_api/clients/base_client.py:63-92] (run).
- Neither body conforms to its core contract (checked with a JSON Schema validator). The Search body sets `null` for
  the typed properties `aggregateBy`, `offset`, `queryAsOwner`, `sort` and `spatialFilter`, and adds
  `returnHighlightedFields`, `cursor` and `from`, which `QueryRequest` does not define (it does not forbid extra
  properties) [SE: POST /query, queryRecords]. The Storage body is covered in section 2.6.

## 2. The calls a writer makes

### 2.1 How the routes are generated

- All 21 resources use one of two classes: `RouterBase` for header collections [1470
  app/api/routers/router_base.py:29-180] and `RouterBaseNoOSDU` for child collections [1470
  app/api/routers/router_base_no_osdu.py:25-159].
- The URL segment `{c}` is set per resource. The tag label (for example "Estimated Volumes") gives the path variable
  and the example file name through `path_format` (`estimated_volumes`) [1470 app/core/utils.py:63-69], and the child
  key names through `id_format` (`id_estimated_volumes_det`) [1470 app/core/utils.py:72-78,
  app/services/crud_base.py:104-109].
- No route has a typed model. Header PUT takes `objects_metier: List[Any]` [1470 app/api/routers/router_base.py:140];
  child PUT and POST take `objects_metier: Any` [1470 app/api/routers/router_base_no_osdu.py:103, 126]; every route
  sets `response_model=None`. Field names come from the SQLAlchemy models (`app/models/*.py`), the database dump and
  the files in `tests/resources/`, which the contract embeds as `example` values [C].
- No route uses the Pydantic classes under `app/schemas/`. Only `user.py` [1470 app/api/dependencies/auth.py:18] and
  `osdu_record.py` (for a type annotation [1470 app/services/crud_base.py:22]) are imported; 12 of the other 14 files
  import `app.schemas.domain.osdu.osdu_record`, which does not exist in the tree (for example [1470
  app/schemas/tank_datum.py:16]).
- The operationIds follow the patterns in the two tables below; all 117 match them [C].

### 2.2 Header collections (`RouterBase`)

| # | Request and contract | Parameters | Body | What the code does | Answers |
| --- | --- | --- | --- | --- | --- |
| A1 | `GET /ddms/{c}/`, trailing slash required [C: GET /ddms/{c}/, get_all_ddms_{c_}__get] | query `data_partition_id`; query `parent_type` | none | Rejects a `parent_type` other than `Reservoir`, `Segment` or `Sector`; searches the collection's kind; runs the sync (section 2.7); returns the local rows whose `id` is among the search hits and whose `parent_object_id` contains `"{parent_type}:"` [1470 app/api/routers/router_base.py:40-66, app/services/crud_base.py:53-57] | 200 array of local rows. 404 `{"detail":{"message":"No {parent_type} objects had been found."}}`, also when rows exist but the search returns none of them. 422 `{"detail":"This master type does not exists, try with Reservoir, Segment or Sector"}` (run) |
| A2 | `GET /ddms/{c}/{x}` [C: GET /ddms/{c}/{x}, get_by_id_ddms_{c_}__{x}__get] | query `data_partition_id`; query `catalog_entity_id`, a string with the kind's pattern (section 2.4); the path segment is not declared and its value is ignored (run) | none | Storage GET of the id, then the local row with that `id` [1470 app/api/routers/router_base.py:97-121] | 200 local row. A Storage error comes back with its status and its body as `detail` (run). 404 `"The record {id} doesn't exist"`. 422 when the id fails the pattern (run) |
| A3 | `GET /ddms/{c}/parent/{parent_id}` [C: GET /ddms/{c}/parent/{parent_id}, get_by_parent_id_ddms_{c_}_parent__parent_id__get] | path `parent_id`; query `data_partition_id` | none | Storage GET of the parent, search of the kind, then the local rows with `parent_object_id == parent_id` whose `id` is among the hits [1470 app/api/routers/router_base.py:68-95, app/services/crud_base.py:66-72] | 200 array. A search error is passed through. 404 `"The record {parent_id} doesn't exist"` when no row matches. A missing parent does not give the coded 404 "The record doesn't exist with this entity id", because the wrapper returns an `HTTPError` object, not `None` (run) |
| A4 | `PUT /ddms/{c}`, no trailing slash [C: PUT /ddms/{c}, update_ddms_{c_}_put] | query `data_partition_id` | JSON array of storage records (section 3.1) | Checks and writes to Storage, then updates the local rows (section 2.6) [1470 app/api/routers/router_base.py:123-146] | 200 array of updated local rows, or 200 `["Error: the object doesn't exist"]`. 422 `{"detail":{"One or several attributes mandatory are NULL": ...}}` on a database error. 500 on any OSDU error (run) |
| A5 | `DELETE /ddms/{c}/{x}` [C: DELETE /ddms/{c}/{x}, delete_by_id_ddms_{c_}__{x}__delete] | query `data_partition_id`; query `catalog_entity_id` (pattern); path segment ignored | none | Purges the record in Storage, then deletes the local row only when the client's JSON is `None` [1470 app/api/routers/router_base.py:148-173] | Section 5.3. OSDU Delivery must not call it. |

- In both families the routes declare 204 on DELETE, 400 on PUT and POST, and 401 on the list GET [1470
  app/api/routers/router_base.py:45-49, 129-136, 156-161, app/api/routers/router_base_no_osdu.py:42-45, 97-101,
  120-124, 137-141], and the service's code raises none of them. A 401 does come from the token check on every
  `/ddms` route, and FastAPI itself answers 400 `{"detail":"There was an error parsing the body"}` for a body it
  cannot decode, such as invalid UTF-8 (run).
- An OSDU error is passed through (its status, with its body as `detail`) only where the wrapper catches `HTTPError`
  and the route checks for it: the search in A1 and A3, the Storage GET in A2 and the Storage delete in A5 [1470
  app/api/routers/router_base.py:59-60, 87-88, 115-116, 168-169] (run for A1, A2 and A5). The same check on the PUT
  result [1470 app/api/routers/router_base.py:144-145] never applies, because `put_osdu_record` lets `HTTPError`
  escape (section 2.6).

### 2.3 Child collections (`RouterBaseNoOSDU`)

| # | Request and contract | Parameters | Body | What the code does | Answers |
| --- | --- | --- | --- | --- | --- |
| B1 | `GET /ddms/{c}/` [C: GET /ddms/{c}/, get_all_estimated_volume_detail_ddms_{c_}__get] | query `master_type` | none | Rejects an unknown `master_type`; returns the rows whose `parent_object_id` contains `"{master_type}:"`; `forecast-base` has no `parent_object_id` and returns all its rows [1470 app/api/routers/router_base_no_osdu.py:36-55, app/services/crud_base.py:111-116] | 200 array. 404 `{"detail":{"message":"No {master_type} objects had been found"}}`. 422 for an unknown type (run) |
| B2 | `GET /ddms/{c}/{x}` [C: GET /ddms/{c}/{x}, get_estimated_volume_detail_by_id_ddms_{c_}__{x}__get] | query `catalog_entity_id` (integer); path segment ignored | none | The row with `id_{entity} == catalog_entity_id` [1470 app/api/routers/router_base_no_osdu.py:57-73, app/services/crud_base.py:118-120] | 200 row. 404 `"The record {id} doesn't exist"`. 422 for a non-integer such as `1.0` (run) |
| B3 | `GET /ddms/{c}/header-entity/{p}`, not on `forecast-base` [C: GET /ddms/{c}/header-entity/{p}, get_by_header_entity_id_ddms_{c_}_header_entity__{p}__get] | query `header_entity_id` (string); path segment ignored | none | The rows whose `id_{parent}` equals the value [1470 app/api/routers/router_base_no_osdu.py:75-89, app/services/crud_base.py:122-124] | 200 array, possibly empty (run). Where the parent key is an integer column, a non-numeric value answers 500 (`DataError`) (run) |
| B4 | `POST /ddms/{c}` [C: POST /ddms/{c}, create_ddms_{c_}_post] | none | one flat JSON object | Reads `body["id_{parent}"]`, looks the parent row up, inserts `Model(**body)`; `forecast-base` inserts without a parent [1470 app/api/routers/router_base_no_osdu.py:91-112, app/services/crud_base.py:126-156] | 200 the stored row with its generated key (run). 404 `{"detail":{"message":"id_{parent} has not been found"}}` (run). 422 on a database error (run) |
| B5 | `PUT /ddms/{c}` [C: PUT /ddms/{c}, update_ddms_{c_}_put] | none | one flat JSON object that includes `id_{entity}` | Update or insert keyed on `id_{entity}` (section 3.2) [1470 app/services/crud_base.py:158-176] | 200 the row (run). 422 on a database error |
| B6 | `DELETE /ddms/{c}/{x}` [C: DELETE /ddms/{c}/{x}, delete_ddms_{c_}__{x}__delete] | query `catalog_entity_id` (integer); path segment ignored | none | Deletes the row [1470 app/api/routers/router_base_no_osdu.py:130-144, app/services/crud_base.py:178-185] | 200 `["Successfully deleted the object with id: {id}"]`, or 200 `["Error: l'objet que vous souhaitez supprimer n'existe plus"]` when the row is absent. 500 when rows still refer to it (run) |

Failure modes (run): a body without the parent key, a body with a key that is not a column (`TypeError` from
`Model(**body)`), a JSON array, or a B5 body without its row key answers 500; only `SQLAlchemyError` becomes 422. A
trailing slash on POST or PUT, a list GET without its trailing slash, and a DELETE on the list path answer 405.

### 2.4 The nine header collections

| Collection `{c}` | Tag | Kind searched by A1 and A3 [1470 app/core/constants.py:41-49] | Type segment of the `catalog_entity_id` pattern [1470 app/core/constants.py:27-39] | Local table | Example body in `tests/resources/` |
| --- | --- | --- | --- | --- | --- |
| `estimated-volumes` | Estimated Volumes | `osdu:wks:work-product-component--ReservoirEstimatedVolumes:1.0.0` | `work-product-component--ReservoirEstimatedVolumes` | `pool_hc_in_place` | `estimated_volumes_100_update.json` |
| `pvt-properties` | Pvt Properties | `osdu:wks:master-data--FluidSystem:1.0.0` | `master-data--FluidSystem` | `pool_pvt_properties` | `pvt_properties_100_update.json` |
| `geological-labels` | Geological Labels | `osdu:wks:work-product-component--GeoLabelSet:1.0.0` | `work-product-component--GeoLabelSet` | `pool_geological_labels` | `geological_labels_100_update.json` |
| `petro-properties` | Petro Properties | `osdu:wks:work-product-component--ReservoirModelScenario:1.0.0` | `work-product-component--ReservoirModelScenario` | `pool_petro_properties` | `petro_properties_100_update.json` |
| `tank-datum` | Tank Datum | `osdu:wks:work-product-component--AcquiferInterpretation:1.1.0` | `work-product-component--AcquiferInterpretation` | `pool_tank_datum` | `tank_datum_100_update.json` |
| `fluid-synthesis` | Fluid Synthesis | `osdu:wks:work-product-component--FluidSystemCharacterization:1.0.0` | `work-product-component--FluidSystemCharacterization` | `pool_fluid_synthesis` | `fluid_synthesis_100_update.json` |
| `kr-synthesis` | Kr Synthesis | `osdu:wks:work-product-component--PersistedCollection:1.2.0` | `work-product-component--PersistedCollection` | `pool_kr_synthesis` | `kr_synthesis_100_update.json` |
| `phi-k-synthesis` | Phi K Synthesis | `osdu:wks:work-product-component--PersistedCollection:1.2.0` | `work-product-component--PersistedCollection` | `pool_phi_k_synthesis` | `phi_k_synthesis_100_update.json` |
| `forecast` | Forecast | `osdu:wks:work-product-component--ProductionValues:1.0.0` | `work-product-component--ProductionValues` | `pool_forecast` | `forecast_100_update.json` |

- Each pattern is `^[\w\-\.]+:<type segment>:[\w\-\.\:\%]+$`, with the hyphens of the type segment escaped (for
  example `work-product-component\-\-GeoLabelSet`); the contract carries the same patterns [C].
  `docs/README.md` lists the same collection-to-kind mapping [1470 docs/README.md:7-15].
- A1 and A3 search the code's constant; A4 looks up and writes the `kind` given in the request body [1470
  app/services/osdu_api/osdu_client_service.py:65, 68, 81]. A record written under another kind is never found by A1
  or A3 (inference). For tank datum the constant spells `AcquiferInterpretation`, while the example body's `kind`
  spells `AquiferInterpretation` (its `id` uses `AcquiferInterpretation`) [1470
  tests/resources/tank_datum_100_update.json].
- `kr-synthesis` and `phi-k-synthesis` share one kind [1470 app/core/constants.py:48-49], so each one's sync inserts
  the other's records, and any other `PersistedCollection` 1.2.0 record whose `data` has a `ParentObjectID`
  (inference from [1470 app/services/crud_base.py:31-45]).
- Parents are master records; the list filter is a substring match on `"{parent_type}:"` [1470
  app/services/crud_base.py:53-57, 111-114]. The master id patterns are defined and never used [1470
  app/core/constants.py:51-53]; the one for sectors expects `master-data--PersistedCollection`, which does not contain
  `Sector:`, so `parent_type=Sector` matches only parent ids that contain `Sector:` (inference). The examples use
  `master-data--Reservoir` parents.

### 2.5 The twelve child collections

B4 reads `body["id_{parent}"]`. When the parent model has an `id` attribute (the header models) it looks the parent
up by `id`; otherwise by `id_{parent}` [1470 app/services/crud_base.py:126-135]. The dump's composite foreign keys
then require `parent_object_id` (and, for forecasts, `id_forecast_base`) to match the parent row; a mismatch answers
422 with the same "mandatory are NULL" message (run). "Required" below means the parent key plus the columns that are
NOT NULL without a default in the dump [1470 app/db/rmddms-db-dump.sql]; a POST without `rt_tab_name` on
`kr-synthesis-rt` or without `name` on `aquifer-datum` answered 422 (run). Row keys come from sequences [1470
app/db/rmddms-db-dump.sql:500, 685-695] and B4 returns them as JSON integers (run).

| Collection `{c}` | Parent looked up by | Row key | Required in the POST body | Other columns | Table |
| --- | --- | --- | --- | --- | --- |
| `estimated-volumes-det` | `id_estimated_volumes` (header id) | `id_estimated_volumes_det` | `id_estimated_volumes`; `parent_object_id` is nullable and completes the two-column foreign key | string `probability`; numeric `riagip`, `ridgip`, `rigcgip`, `rigip`, `rooip`, `rpiip`, `iagip`, `idgip`, `igcgip`, `igip`, `ooip`, `piip`, `stooip` | `pool_hc_in_place_det` |
| `aquifer-datum` | `id_tank_datum` (header id) | `id_aquifer_datum` | `id_tank_datum`, `parent_object_id`, `name` | numeric `encroa_angle`, `aqui_comp`, `aqui_poro`, `io_rd_ratio`, `res_thick`, `res_radius`, `aqui_pem`, `aqui_vol`; string `aqui_type`, `ref_aqui`, `ref_report`, `ecl_infl_tab`, `comment`, `ref_report_filename` | `pool_aquifer_datum` |
| `fluid-synthesis-tank-pvt` | `id_fluid_synthesis` (header id) | `id_fluid_synthesis_tank_pvt` | `id_fluid_synthesis`, `parent_object_id` | numeric `depth`, `pressure`, `cond_dens`, `gas_dens`, `oil_dens`, `wat_dens`, `wat_compr`, `oil_compr`, `rock_compr`, `gas_visco`, `oil_visco`, `wat_visco`, `pb`, `temperature`, `bg`, `bo`, `bw`, `cgr_res`, `cgr_vapo`, `rs`, `z_factor`; string `comment`, `description` | `pool_fluid_synthesis_tank_pvt` |
| `fluid-synthesis-tank-blackoil` | `id_fluid_synthesis` (header id) | `id_fluid_synthesis_tank_blackoil` | `id_fluid_synthesis`, `parent_object_id`, `fluid_unit`, `flu_tab_type` (the last two are in the primary key) | numeric `surf_oil_den`, `pvdg_bg`, `pvdg_pgas`, `pvdg_vis_gas`, `pvtg_bg_prim`, `pvtg_rv`, `pvtg_bo`, `pvtg_rs`, `pvtg_pres`, `pvtg_vis_gas`, `pvto_pb`, `pvto_oil_vis` | `pool_fluid_synthesis_tank_blackoil` |
| `kr-synthesis-rt` | `id_kr_synthesis` (header id) | `id_kr_synthesis_rt` | `id_kr_synthesis`, `parent_object_id`, `rt_tab_name` (NOT NULL in the dump, nullable in the model) | numeric `krg_max`, `krc_max`, `krw_max`, `sgr`, `sgr_hys`, `sor_hys`, `sorw`, `swi`, `swi_hys`, `gas_sweep`, `water_sweep`, `corey_ng`, `corey_no`, `corey_nw`; string `rt_name`, `rt_geol_desc`; boolean `rt_kr_table`, `has_kr_table` | `pool_kr_synthesis_rt` |
| `kr-synthesis-kr` | `id_kr_synthesis_rt` (integer) | `id_kr_synthesis_kr` | `id_kr_synthesis_rt`, `parent_object_id`, `id_kr_synthesis`, `sat_tab_type` | string `comment`; numeric `sgnf_krg`, `sgnf_sg`, `sgof_krg`, `sgof_krog`, `sgof_sg`, `sgof_pcow`, `sof3_krog`, `sof3_krow`, `sof3_so`, `swfn_krw`, `swfn_sw`, `swfn_pcow`, `swof_krow`, `swof_sw`, `swof_krw`, `swof_pcow`, `sgfn_pcog` | `pool_kr_synthesis_kr` |
| `phi-k-synthesis-rt` | `id_phi_k_synthesis` (header id) | `id_phi_k_synthesis_rt` | `id_phi_k_synthesis`, `parent_object_id`, `rt_tab_name`, `rt_phi_k_tab` (bigint) | string `rt_name`, `rt_geol_desc`, `phi_k_model` | `pool_phi_k_synthesis_rt` |
| `phi-k-synthesis-phi-k` | `id_phi_k_synthesis_rt` (integer) | `id_phi_k_synthesis_phi_k` | `id_phi_k_synthesis_rt`, `parent_object_id`, `id_phi_k_synthesis` | string `comment`; numeric `rhos`, `kgas`, `kwat`, `phie`, `phit`, `depth`; `id_r_ori` (foreign key to `r_ori`) | `pool_phi_k_synthesis_phi_k` |
| `forecast-fluid` | `id_forecast` (header id) | `id_forecast_fluid` | `id_forecast`, `id_forecast_base`, `parent_object_id`, `id_fluid` (foreign key to `r_fluid`) | numeric `qf_cutoff`, `qf_fore`, `qf_hist`, `qc_d_fluid`, `fluid_const`, `fluid_slope`; string `fluid_method` | `pool_forecast_fluid` |
| `forecast-det` | `id_forecast` (header id) | `id_forecast_det` | `id_forecast`, `id_forecast_base`, `parent_object_id`, `dt` | numeric `optime`, `optime_p`, `cgr`, `glr`, `gor`, `wct`, `wgr`; string `comment` | `pool_forecast_det` |
| `forecast-det-fluid` | `id_forecast_det` (integer) | `id_forecast_det_fluid` | `id_forecast_det`, `id_forecast_base`, `parent_object_id`, `id_forecast`, `id_fluid` | numeric `cum_fluid`, `qf_c`, `qf_p`, `fluid` | `pool_forecast_det_fluid` |
| `forecast-base` | none: `create_without_parent` [1470 app/api/routers/router_base_no_osdu.py:105-106] | `id_forecast_base` | none | string `name`, `dt_forecast`, `comment` | `forecast` |

Hierarchy:

```text
Reservoir, Segment or Sector master record (OSDU id; local table `pool`)
  estimated-volumes   (OSDU) -> estimated-volumes-det
  tank-datum          (OSDU) -> aquifer-datum
  fluid-synthesis     (OSDU) -> fluid-synthesis-tank-pvt, fluid-synthesis-tank-blackoil
  kr-synthesis        (OSDU) -> kr-synthesis-rt -> kr-synthesis-kr
  phi-k-synthesis     (OSDU) -> phi-k-synthesis-rt -> phi-k-synthesis-phi-k
  forecast            (OSDU, refers to forecast-base) -> forecast-fluid,
                                                         forecast-det -> forecast-det-fluid
  pvt-properties, geological-labels, petro-properties (OSDU): no children
forecast-base (local only, no parent)
```

### 2.6 What A4 does

Order of work (run with recording fakes, and on the wire with SDK26 and SDK28), in [1470
app/services/osdu_api/osdu_client_service.py:64-91] and [1470 app/services/crud_base.py:74-92]:

1. `GET /schema/{kind}` with the `kind` of element 0 (lines 65, 68). The call is not guarded: when the kind is unknown
   to Schema, the 404 raises `HTTPError` out of the route and the answer is 500 (run).
2. `GET /records/{ParentObjectID}` and `GET /records/{id}` for element 0 (lines 66, 70-71). When either answers
   non-2xx, the wrapper returns the `HTTPError` object; the membership test on it then raises `TypeError` (lines
   84-85) and the answer is 500 (run). The first record and its parent must therefore exist in Storage before A4 is
   called.
3. `PUT /records` with one entry per array element (lines 79-87), but only when the schema body has no top-level
   `error` key, the record body has no top-level `error` key and the parent body has a `data` key. Otherwise
   `put_osdu_record` returns `None`, and the route goes on to the local update and answers 200 without writing to
   Storage (run with fakes). The documented error body of these services is `AppError` (`code`, `reason`, `message`)
   [ST] [SC], and error statuses already raise at steps 1 and 2, so this branch is reached only by a 2xx body with a
   top-level `error` key or a parent body without `data` (inference).
4. When Storage rejects the write, `HTTPError` escapes (line 86 is outside the `try`) and the answer is 500 before any
   local change (run).
5. The local update, element by element: convert the `data` keys to snake_case in place, load the local row with
   `id == element.id`, set every converted key, commit. The dictionaries were already serialised for Storage, so the
   conversion does not change what Storage received (run). An element without a local row stops the loop with 200
   `["Error: the object doesn't exist"]`; the elements before it stay committed, and Storage has already received
   every element (run).

The Storage body, as sent with SDK26 (run; SDK28 adds `"tags": null`):

```json
[
  {
    "acl": {"owners": ["data.default.owners@opendes.example.com"], "viewers": ["data.default.viewers@opendes.example.com"]},
    "ancestry": null,
    "data": {"ParentObjectID": "opendes:master-data--Reservoir:r1", "name": "Tank W"},
    "id": null,
    "kind": "osdu:wks:work-product-component--AcquiferInterpretation:1.1.0",
    "legal": {"legaltags": ["opendes-tag"], "otherRelevantDataCountries": ["NO"], "status": "compliant"},
    "meta": null,
    "version": null
  }
]
```

- Each entry is built as `Record(kind=..., acl=..., legal=..., data=...)`, without the element's `id` [1470
  app/services/osdu_api/osdu_client_service.py:81]. `Record` keeps `id`, `version`, `ancestry` and `meta` as `None`
  (and `tags` in 0.28.0), and `to_JSON` writes every attribute [SDK26 osdu_api/model/storage/record.py:31-49,
  osdu_api/model/base.py:18-24; SDK28 osdu_api/model/storage/record.py:31-51].
- Against the contract the entry fails on `id` (required, a string with a pattern) and on `version`, `ancestry` and
  `meta` (and `tags`), which are typed and not nullable [ST: PUT /records, createOrUpdateRecords]. The operation's
  description says that a record with no id, or with an id not yet present, is created as a new record; it does not
  say how `"id": null` is treated (section 9). If Storage treats it as no id, every A4 call creates a new record with
  an id Storage assigns and the service never returns, while the local row stays keyed by the given id (inference).
- `acl`, `legal` and `data` are sent as received, in their original key casing (run).
- Only element 0 is checked (kind, parent, record); an array that mixes kinds or parents is sent as it is [1470
  app/services/osdu_api/osdu_client_service.py:65-71].
- The answer is the local rows [1470 app/api/routers/router_base.py:146]. Storage's 201 body (`recordCount`,
  `recordIds`, `skippedRecordIds`, `recordIdVersions` [ST: PUT /records, createOrUpdateRecords]) is discarded (run).
- `cleared_data` is computed and never used [1470 app/services/osdu_api/osdu_client_service.py:75-77].

### 2.7 How local header rows come into existence

No route creates a header row; the only code that inserts one is the sync `get_diff_data_with_db` that A1 runs [1470
app/services/crud_base.py:31-45]. For each search hit without a local row it reads the record from Storage and, when
the record's `data` has `ParentObjectID`, inserts a row with only `id` and `parent_object_id` (run). A hit whose
record has no `ParentObjectID` is skipped (run); a hit whose Storage GET fails raises `TypeError` and the list answers
500 (run).

| Collection | Sync insert on a database built from the dump (run) |
| --- | --- |
| `estimated-volumes` | works; the dump has no `pool` foreign key on this table |
| `pvt-properties` | needs an `r_hc_type` row named `None`, the model default of `hc_type_name` [1470 app/models/pvt_property.py:44, app/db/rmddms-db-dump.sql:898-899]; the dump has no `pool` foreign key on this table |
| `geological-labels`, `petro-properties`, `tank-datum`, `fluid-synthesis`, `phi-k-synthesis` | work once the `pool` row exists; model defaults fill their NOT NULL columns |
| `kr-synthesis` | always fails: `name`, `dt` and `description` are NOT NULL without defaults [1470 app/models/kr_synthesis.py:29-32, app/db/rmddms-db-dump.sql:363-372] |
| `forecast` | needs a `forecast` row with `id_forecast_base = 0`, the model default [1470 app/models/forecast.py:28, app/db/rmddms-db-dump.sql:861-862]. `POST /ddms/forecast-base` with `{"id_forecast_base": 0, ...}` creates it once; a second such POST answers 500 (run) |

- A failed insert answers 500 for the whole list request (run).
- `pool` rows: the dump has `parent_object_id -> pool(id)` foreign keys on 7 of the 9 header tables [1470
  app/db/rmddms-db-dump.sql:838, 865, 868, 877, 884, 887, 902], and all 9 models declare one (for example [1470
  app/models/tank_datum.py:27]). No route inserts a `pool` row: `CRUDBase.create_master` [1470
  app/services/crud_base.py:59-64] is never called. The README lists the tables to fill beforehand: `pool`,
  `forecast`, `r_cor_gas_visc`, `r_cor_oil_visc`, `r_cor_pbrsbo`, `r_fluid`, `r_hc_type`, `r_ori` [RD:141-156].
- Only the first 100 search hits are synced and listed, with no offset or cursor [1470
  app/services/osdu_api/osdu_client_service.py:47]. The Search contract allows up to 1000 per query and points to
  `query_with_cursor` beyond that [SE: POST /query, queryRecords].
- The README says objects related to OSDU are created "from the Ingestion Workflow", and others "directly with the API
  endpoint when POST available" [RD:143-146]; POST exists only for child collections.

### 2.8 Write sequences

A Phi-K synthesis with its tables (run end to end, with recording fakes for OSDU). Estimated volumes, tank datum and
fluid synthesis follow the same pattern. Kr synthesis cannot: its sync fails (section 2.7), so its local row can only
be inserted in the database outside the service.

1. Preconditions: a `pool` row for the Reservoir id; a record of kind
   `osdu:wks:work-product-component--PersistedCollection:1.2.0` with `data.ParentObjectID` set to the Reservoir id,
   present in Storage and indexed by Search; the Reservoir record present in Storage. The service creates none of
   these.
2. `GET /ddms/phi-k-synthesis/?data_partition_id={p}&parent_type=Reservoir` creates the local row, filled with the
   defaults `default_name`, `default_date` and `default_description`.
3. `PUT /ddms/phi-k-synthesis?data_partition_id={p}` with
   `[{"id", "kind", "acl", "legal", "data": {"ParentObjectID", "name", "dt", "description", ...}}]`. A JSON array of
   row objects is a success; `["Error: ..."]` is a failure. Storage receives the record without its id (section 2.6).
4. `POST /ddms/phi-k-synthesis-rt` with `{"parent_object_id", "id_phi_k_synthesis", "rt_tab_name", "rt_phi_k_tab",
   ...}`; keep `id_phi_k_synthesis_rt` from the answer.
5. `POST /ddms/phi-k-synthesis-phi-k` once per table row, with `{"parent_object_id", "id_phi_k_synthesis",
   "id_phi_k_synthesis_rt", ...}`.
6. Read back with `GET /ddms/phi-k-synthesis-phi-k/header-entity/{any}?header_entity_id={id_phi_k_synthesis_rt}`.

A forecast (run end to end, with recording fakes for OSDU), with the same preconditions for a record of kind
`osdu:wks:work-product-component--ProductionValues:1.0.0`:

1. `POST /ddms/forecast-base`; keep `id_forecast_base`.
2. `GET /ddms/forecast/?data_partition_id={p}&parent_type=Reservoir` (the sync) inserts the header row with
   `id_forecast_base = 0`, which needs the `forecast` row 0 (section 2.7).
3. `PUT /ddms/forecast?data_partition_id={p}` with `data.id_forecast_base` set to the key from step 1, as
   `forecast_100_update.json` does; the local row's `id_forecast_base`, part of its primary key, takes that value.
4. `POST /ddms/forecast-det` (with `dt`) and `POST /ddms/forecast-fluid` (with `id_fluid`), each with `id_forecast`,
   `id_forecast_base` (the key from step 1) and `parent_object_id`; keep `id_forecast_det`.
5. `POST /ddms/forecast-det-fluid` with `id_forecast_det`, `id_forecast_base`, `parent_object_id`, `id_forecast` and
   `id_fluid`.

A child row that still carries `id_forecast_base = 0` after step 3 answers 422 (run).

## 3. Payload and bulk data shapes

### 3.1 A4: array of storage records

- Required: `id` in every element [1470 app/services/crud_base.py:81]; `kind`, `acl`, `legal` and `data` in every
  element [1470 app/services/osdu_api/osdu_client_service.py:81]; `data.ParentObjectID` in element 0 (line 66). A
  missing one, or an empty array, answers 500 (run for `id`, `acl`, `ParentObjectID` and the empty array). The
  examples also carry `acl.viewers`, `acl.owners`, `legal.legaltags`, `legal.otherRelevantDataCountries` and
  `legal.status`.
- `convert_attribute_json_keys` turns each `data` key into snake_case, with `ID` becoming `id` [1470
  app/core/utils.py:45-60]: `EstimatedVolumeTypeID` becomes `estimated_volume_type_id`, `HasAquifer` becomes
  `has_aquifer` (run). Keys already in snake_case stay as they are.
- A converted key that is not a column is not stored, but it is echoed in the answer (run). Every converted key is
  set, falsy values included (run for an empty string). A value of the wrong type, or `null` in a NOT NULL column,
  answers 422 (run).
- `ParentObjectID` rewrites the row's `parent_object_id`, a primary key column; a parent without a `pool` row answers
  422 (run).
- The example bodies place these properties directly in the OSDU record's `data`, and they reach Storage unchanged:

| Collection | `data` keys (examples and models) |
| --- | --- |
| `estimated-volumes` | `ParentObjectID`, `EstimationName`, `EstimationDate`, `EstimatedVolumeTypeID`, `EffectiveEstimationYear`, `HydrocarbonTypeIDs` [1470 app/models/estimated_volumes.py:25-31] |
| `pvt-properties` | `ParentObjectID`, `name`, `api`, `salinity`, `gas_sg`, `bgi`, `boi`, `cgri`, `rsi`, `pi`, `datum_pi`, `comment`, `dt`, `oil_visco`, `oil_dens`, `pb`, `hc_type_name` (foreign key to `r_hc_type`) [1470 app/models/pvt_property.py:27-44] |
| `geological-labels` | `ParentObjectID`, `comment`, `deposition`, `domi_facies`, `geol_age`, `geol_form`, `res_type`, `structure` [1470 app/models/geological_labels.py:27-35] |
| `petro-properties` | `ParentObjectID`, `name`, `ntg_avg`, `phie_avg`, `phie_max`, `phie_min`, `swi_avg`, `hu_avg`, `hu_max`, `hu_min`, `k_high`, `k_low`, `grv`, `nrv`, `pv`, `main_rt`, `comment`, `k_mean` [1470 app/models/petro_property.py:27-45] |
| `tank-datum` | `ParentObjectID`, `name`, `gascap_ratio`, `poro_avg`, `swi`, `initial_p`, `stooip`, `formation_t`, `aquifer`, `mbal_model`, `prod_startup`, `comment`, `mbal_model_filename`, `has_aquifer` [1470 app/models/tank_datum.py:27-41] |
| `fluid-synthesis` | `ParentObjectID`, `name`, `cond_dens`, `gas_dens`, `oil_dens`, `res_gas_grav`, `sep_gas_grav`, `tank_gas_grav`, `co2_content`, `h2s_content`, `n2_content`, `pdew`, `pb`, `res_pres`, `ref_depth`, `sep_pres`, `wat_salinity`, `res_temp`, `sep_temp`, `cgr`, `rs`, `sep_gor`, `tank_gor`, `bo_table`, `dt`, `fluid_pvt`, `description`, `path`, `pvt_table`, `report`, `report_filename`, `bo_tab_file_filename`, `has_pvt_table`, `id_cor_gas_visc`, `id_cor_oil_visc`, `id_cor_pb_rs_bo` (the last three are foreign keys to the `r_cor_*` tables) [1470 app/models/fluid_synthesis.py:27-64] |
| `kr-synthesis` | `ParentObjectID`, `name`, `comment`, `dt`, `description`, `path`, `report_filename` [1470 app/models/kr_synthesis.py:27-34] |
| `phi-k-synthesis` | `ParentObjectID`, `name`, `comment`, `dt`, `description`, `path`, `report_filename` [1470 app/models/phi_k_synthesis.py:27-34]; the example uses `dtz`, which is not a column |
| `forecast` | `ParentObjectID`, `id_forecast_base` (foreign key to `forecast`), `qc_duree`, `cgr_cutoff`, `cgr_fore`, `cgr_hist`, `glr_cutoff`, `glr_fore`, `glr_hist`, `gor_cutoff`, `gor_fore`, `gor_hist`, `optime_p`, `wct_cutoff`, `wtc_fore`, `wct_hist`, `wgr_cutoff`, `wgr_fore`, `wgr_hist`, `act_hyp`, `comment`, `cum_gas_hist`, `cum_oil_hist`, `date_fore`, `date_model`, `file`, `fore_name`, `fore_start`, `fore_stop`, `planning`, `qc_mbal`, `qc_other`, `qc_outflow`, `qc_surface` [1470 app/models/forecast.py:28-62] |

### 3.2 Child rows

- One flat JSON object per call whose keys are column names (section 2.5). The answer is the stored row: every column,
  with numeric values as JSON numbers and integer-valued keys as integers (run).
- B4 inserts a key given in the body as it is and uses the sequence only when the body has none (run).
- B5 [1470 app/services/crud_base.py:158-176]:
  - the body must contain `id_{entity}`, or the answer is 500 (run);
  - when the row exists, values that are falsy (`0`, `false`, `""`, `null`) are skipped (line 168), so a value cannot
    be set to zero or cleared (run);
  - when the row does not exist, B5 runs B4 with the whole body, so the row is inserted with the given key and the
    sequence does not move (run). A given key at or above the sequence's next value makes a later POST fail with 422
    (duplicate key) and use up that sequence value (run);
  - on `forecast-base`, whose parent label is empty, a missing row raises `KeyError: 'id_'` and answers 500 (run).
- A child POST is not idempotent: the dump has no unique constraint besides the primary keys, which include the
  sequence key, so a repeated POST inserts another row with a new key (inference from [1470
  app/db/rmddms-db-dump.sql:741-823, app/services/crud_base.py:137-143]).

### 3.3 Bulk data

- No operation takes files, parquet, binary or multipart content; the contract uses only `application/json` [C], and
  no module imports `UploadFile`, `File`, `Form` or a streaming response [1470 app/].
- There are no session, upload or chunk routes.
- JSON arrays appear only in A4, where all elements go to Storage in one call. Tabular content (relative permeability
  tables, PVT and black-oil tables, forecast steps, Phi-K samples) is sent one row per B4 or B5 call; an array sent to
  those routes answers 500 (run).

## 4. Identities and versions

- A header row's `id` is the OSDU record id. In the dump the primary key is `(parent_object_id, id)` on 7 of the 9
  header tables, `(id_forecast_base, parent_object_id, id)` on `pool_forecast` and `(id)` on `pool_pvt_properties`
  [1470 app/db/rmddms-db-dump.sql:748, 766, 769, 775, 781, 787, 793, 802, 805].
- Only A2 and A5 check an id, against the kind's pattern (`catalog_entity_id`) [C]. A4 does not check element ids,
  and no route checks parent ids.
- A4 does not pass the id to Storage and does not return Storage's ids or versions (section 2.6). To know which
  Storage record and version a delivery produced, OSDU Delivery must read Storage itself [ST: GET /records/{id},
  getLatestRecordVersion] [ST: GET /records/versions/{id}, getRecordVersions]. If a null id creates new records, they
  can only be found through Search, for example by kind and `data.ParentObjectID` [SE: POST /query, queryRecords]
  (inference).
- Under this repository's live-OSDU rules, every id such a write creates must be logged, including ids Storage mints,
  and removed at record scope through Storage's logical delete [ST: POST /records/{id}:delete, deleteRecord], never
  through the service's delete.
- Child rows carry OSDU ids only as strings (`parent_object_id`, `id_{header}`); they have no kind and no version.
  Their keys are integers from sequences, returned by B4 and needed by the rows below them.
- The service keeps no version of anything; `info.version` 1.8.0 is its only version string [C].

## 5. Reads, verification and deletes

### 5.1 Reads

- A1, A2 and A3 return the service's local rows (the flat table columns), not OSDU records (run). Storage and Search
  serve only as existence and permission checks; their errors are passed through as described in section 2.2.
- A1 and A3 consider only the first 100 search hits [1470 app/services/osdu_api/osdu_client_service.py:47].
- Child collections are read by master type (B1), by row key (B2) or by parent key (B3).

### 5.2 Verification

- The service offers no way to see a delivered OSDU record's id, version or `data`. Verification reads Storage
  directly (section 4) and the local row through A2, which also performs a Storage GET of the given id.
- A write is confirmed only by the answer's body: a JSON array of row objects for A4 and a row object for B4 and B5
  (section 6).

### 5.3 Deletes

- A5 sends `DELETE /records/{id}` [SDK26 osdu_api/clients/storage/record_client.py:94-95; SDK28 same lines] (run),
  the purge that "performs the physical deletion of the given record and all of its versions" and "cannot be undone"
  [ST: DELETE /records/{id}, purgeRecord].
  - The purge's success is 204 with no body [ST]. The wrapper calls `.json()` on it, which raises
    `requests.exceptions.JSONDecodeError`, not an `HTTPError` [1470 app/services/osdu_api/osdu_client_service.py:93-98]:
    the answer is 500 and the local row stays (run).
  - A Storage error is passed through, for example 404 with Storage's body (run). A 2xx JSON body other than `null`
    answers 500 `{"detail":"We were not able to delete the record on OSDU API Storage."}` (run with fakes). Only a
    body that is JSON `null` leads to the local delete: 200 `["Successfully deleted the object with id:  {id}"]`
    (two spaces), or 200 `["Error: the object is already deleted"]` when the row is absent (run with fakes).
  - `osdu_api` 1.2.0 changed `delete_record` to the logical delete `POST /records/{id}:delete` [SDK120
    src/osdu_api/clients/storage/record_client.py:140-147], but the service's requirements cannot install 1.2.0.
  - This repository never uses `DELETE /records/{id}` or a purge. The route type must not expose A5; cleanup of what
    A4 wrote goes through Storage's logical delete at record scope (section 4).
- Local cascades: the ORM deletes child rows only for `estimated-volumes -> estimated-volumes-det` and
  `tank-datum -> aquifer-datum` [1470 app/models/estimated_volumes.py:34, app/models/tank_datum.py:44] (run). The dump
  has no `ON DELETE` rule, and neither `CRUDBase.delete` [1470 app/services/crud_base.py:94-101] nor
  `CRUDBaseNoOSDU.delete` [1470 app/services/crud_base.py:178-185] catches database errors, so deleting any other row
  that rows still refer to answers 500; for a header row this happens after the Storage call (run).
- B6 deletes one local row and answers 200 whether or not the row existed (run).

### 5.4 Health

- `GET /` [C: GET /, health__get] needs no token and answers 200 with the JSON string `"Success"` [1470
  app/api/routers/info.py:24-33] (run).

## 6. Limits and errors

| Situation | Answer |
| --- | --- |
| missing or invalid query parameter, body of the wrong JSON type, id failing the pattern | 422 FastAPI validation body `{"detail":[{"loc":...,"msg":...,"type":...}]}` (run) |
| body that is not valid JSON | 422 with `type` `value_error.jsondecode` (run) |
| body FastAPI cannot decode, such as invalid UTF-8 | 400 `{"detail":"There was an error parsing the body"}` (run) |
| unknown `parent_type` or `master_type` | 422 with a plain string `detail` (run) |
| database error the code catches (`SQLAlchemyError` in A4, B4 and B5): NOT NULL, foreign key, duplicate key, wrong value type | 422 `{"detail":{"One or several attributes mandatory are NULL": <bound parameters>}}` whatever the cause [1470 app/services/crud_base.py:90-92, 144-146, 174-176]. The parameters are an object or an array of objects and echo the row's values (run) |
| OSDU error in the A1 or A3 search, the A2 Storage GET or the A5 delete | the OSDU status, with the OSDU body as `detail` (run) |
| missing local row in A4, A5 or B6 | 200 with a one-string array (run) |
| A4: any OSDU error at the schema, record or parent lookup or the write; a missing `id`, `kind`, `acl`, `legal`, `data` or `ParentObjectID`; an empty array | 500 (run) |
| B4 and B5: missing parent key or row key, unknown column, array body, non-JSON content type | 500 (run) |
| database errors the code does not catch: failed sync insert, delete of a row others refer to, non-numeric value for an integer key in B3, duplicate `forecast-base` key | 500 (run) |
| token problems | 401 or 500 (section 1.3) |

- The service returns Python sets for its messages; FastAPI writes them as JSON arrays of one string (run). An answer
  is a failure when it is an array of strings whose first element starts with `Error`, even with status 200.
- The 422 database error echoes row values. OSDU Delivery stores such bodies only through its usual redaction and
  size limits.
- One child row per call; no batching, no transaction across calls, no paging on any list route.
- A4 sends all elements in one Storage call but commits the local rows one by one (section 2.6).
- No rate or size limit is declared in the code or the contract.
- Import-time requirements (run): the four settings of section 1.3; an importable `osdu_api` and `psycopg2`
  (`create_engine` runs at import [1470 app/db/database.py:21-23]); and the example files, which the routes read at
  import [1470 app/api/routers/router_base.py:140, app/api/routers/router_base_no_osdu.py:103, 126]. The example path
  is cut at the first `app` in the module's absolute path [1470 app/core/utils.py:35-38]; in the image layout
  (`WORKDIR /app`, `ADD app ./app` [1470 Dockerfile:20-23]) it becomes `/tests/resources/...`, which the image does not
  contain, and importing a tree that has only `app/` fails with `FileNotFoundError` (run). How a deployed image
  supplies those files is not visible in the project.
- Python 3.12 prints 14 `SyntaxWarning: invalid escape sequence '\w'` lines for [1470 app/core/constants.py:28-39,
  51-53] (run); they are warnings only.

## 7. What differs between the contract and the code

- **Undeclared path variables.** 53 operations (21 get-by-id, 21 delete, 11 header-entity) have a path variable with
  no parameter [C]. The segment's value is ignored and the real id is a required query parameter (run). Strict
  validators and client generators reject or mishandle these operations; OSDU Delivery's contract checks must treat
  the segment as free text.
- **Untyped bodies and answers.** Header PUT bodies are `{"type": "array", "items": {}, "title": "Objects Metier"}`,
  child PUT and POST bodies are `{"title": "Objects Metier"}` with no type, every 200 answer schema is `{}`, and the
  only component schemas are `HTTPValidationError` and `ValidationError` [C]. Field names appear only in the `example`
  values; sections 2.5 and 3.1 give them from the models and the dump.
- **Declared statuses.** Besides 200, the contract declares 404 on every operation, 422 on every operation but the
  health check, 204 on DELETE, 400 on PUT and POST, and 401 on the list GETs [C]; section 6 lists what the code
  answers.
- **Tags.** Every operation lists its tag twice [C].
- **Servers.** None [C]; the base URL is deployment configuration.
- **Postman collection.** It is an import of this document: the same 117 operations (63 GET, 21 PUT, 21 DELETE, 12
  POST), placeholder values `<string>` and `<integer>`, catalog ids generated from the patterns (for example
  `adECd0z:work-product-component--ReservoirEstimatedVolumes:v`), header PUT bodies `[]`, child bodies
  `{"title": "Objects Metier"}`, no request descriptions, query parameters described as "(Required)", and saved example
  answers for the declared statuses [PM].
- **Examples in `tests/resources/`**, which the contract embeds:
  - tank datum: the `kind` spells `AquiferInterpretation` while the code, its `id` and `docs/README.md` spell
    `AcquiferInterpretation` [1470 docs/README.md:10];
  - PVT properties: the id's type `master-data--FluIdSystem` (capital I) fails the pattern;
  - geological labels: the id hard-codes the partition `opendes`; the other ids use `{{data-partition-id}}`
    placeholders, which fail the pattern until replaced;
  - `kr_synthesis_kr_update.json` has the key `id_kr_sythesis_kr`, on which B5 answers 500;
  - `estimated_volumes_det_update.json` has `estimated_volumes_id` instead of `id_estimated_volumes`;
  - `fluid_synthesis_tank_blackoil_update.json` puts a string in the integer key;
  - `fluid_synthesis_tank_pvt_update.json` and `forecast_det_fluid_update.json` put strings in numeric columns, and
    `tank_datum_100_update.json` puts one in the boolean `has_aquifer`;
  - `forecast_update.json` (`{}`) and `forecast_create.json` are never loaded, because `forecast` is a header
    collection and loads `forecast_100_update.json`.
- **Models and dump.** The app never creates tables; a database built as the README says has the dump's schema.
  - `pool_pvt_properties` has primary key `(id)` in the dump [1470 app/db/rmddms-db-dump.sql:801-802] and
    `(parent_object_id, id)` in the model [1470 app/models/pvt_property.py:27-28];
  - `pool_kr_synthesis_rt.rt_tab_name` is NOT NULL only in the dump [1470 app/db/rmddms-db-dump.sql:418];
  - `pool_forecast.id_forecast_base` is unique only in the model [1470 app/models/forecast.py:28];
  - `pool_pvt_properties.name` has a default only in the model [1470 app/models/pvt_property.py:29,
    app/db/rmddms-db-dump.sql:538];
  - the sequence names in the models' `server_default` differ from the dump for `pool_aquifer_datum` and
    `pool_fluid_synthesis_tank_blackoil` [1470 app/models/tank_datum.py:56-57, app/models/fluid_synthesis.py:81-82,
    app/db/rmddms-db-dump.sql:686-687]; the database default applies at run time (run);
  - only the dump has `pool_hc_in_place.description` and `.path` and `pool_fluid_synthesis.bo_tab_file`, and it
    declares `pool_fluid_synthesis.dt` as `varchar(50)` [1470 app/db/rmddms-db-dump.sql:81, 87, 315-316];
  - the dump sets sequence values left from another database, for example `forecast_id_forecast_base_seq` at 2 [1470
    app/db/rmddms-db-dump.sql:703].
- **Tests.** The only test is a placeholder [1470 tests/services/test_reservoir_service.py:20-21].

## 8. What the route type needs

The service does not have the `wellboreDdmsV3` shape [ID 5.4]:

| Aspect | `wellboreDdmsV3` | RM DDMS |
| --- | --- | --- |
| Record write | `POST /{collection}` | `PUT /ddms/{c}?data_partition_id=` with an array of storage records; header collections only |
| Bulk data | `/{collection}/{id}/data` | none; each tabular row is its own `POST /ddms/{child}` |
| Sessions | `/{collection}/{id}/sessions` | none |
| Record id | path segment | the segment is ignored; the id is the `catalog_entity_id` query parameter |
| Version in the path | `/ddms/v3/...` [WB] | none; fixed `/ddms` prefix |

A route type for it needs:

1. **Operation definitions.** For each operation: method, path, and where every value goes (path, query or body). They
   cannot be read from the contract, because ids are query values behind an ignored path segment.
2. **Path control.** The collection segment under `/ddms`, no version, and exact trailing slashes: list routes have
   one, write routes must not.
3. **Partition as a query parameter** on header routes only; no partition header.
4. **A token the service accepts:** issued by the service's tenant, `aud` equal to its `SCOPE`, with the `name` and
   `unique_name` claims (section 1.3).
5. **Preconditions it cannot create through the API:** `pool` master rows and the reference rows; the record and its
   parent already in Storage and, for the sync, among the first 100 Search hits of their kind (section 2.7). Kr
   synthesis cannot be synced on a database built from the dump.
6. **Two body builders:** an array of complete storage records with one kind and one parent per call, and a flat
   object of snake_case column values that includes the parent key columns.
7. **Ordered dependencies with key capture:** sync before A4; child keys (`id_kr_synthesis_rt`,
   `id_phi_k_synthesis_rt`, `id_forecast_det`, `id_forecast_base`) taken from B4 answers and fed to the rows below.
   B4 bodies carry no key, and B5 is used only with keys the service returned (section 3.2).
8. **Outcome classification from the body as well as the status** (section 6). A child POST is not idempotent, so its
   returned key must be in the ledger before any retry, and a POST whose outcome is unknown can leave a duplicate row
   (inference).
9. **Verification through Storage and Search directly** (section 4); the service returns no OSDU ids or versions.
10. **No deletes through the service.** A5 purges (section 5.3); OSDU cleanup goes through Storage's logical delete at
    record scope, and B6 touches only the service's database.
11. **Contract checks that tolerate this contract:** the ignored path segment, untyped bodies, and the patterns on
    `catalog_entity_id`.

## 9. What is still open

Each item is to be settled against the pinned contracts or a live partition, never assumed.

1. **Null id in `PUT /records`.** Whether Storage treats `"id": null` as no id and creates a new record, and whether a
   gateway rejects the entry's null fields, which the contract does not allow [ST: PUT /records,
   createOrUpdateRecords]. A live check follows this repository's live-OSDU rules: log the intent and every id,
   including ids Storage mints, and remove them at record scope.
2. **Deployed library.** Which `osdu_api` version the running image contains (0.26.0 and 0.28.0 purge on delete; 1.2.0
   deletes logically but is not installable with the service's pins), and whether TLS verification is on.
3. **Search body.** Whether Search accepts the null-valued properties the library sends (section 1.4).
4. **Schema acceptance.** Whether Schema and Storage accept the snake_case properties the examples put in `data` for
   these `wks` kinds, and which spelling of the tank datum kind the target partition holds.
5. **Token.** The deployment's tenant and `SCOPE`, and whether OSDU Delivery's token carries `name` and `unique_name`
   and the issuer of the tenant's OpenID configuration.
6. **Deployment.** The base URL and any ingress prefix; how the image supplies `tests/resources`; which database
   schema the deployment runs (the dump or another); who fills `pool` and the reference tables.
7. **Shared kind.** Whether other `PersistedCollection` 1.2.0 records with `data.ParentObjectID` exist in the target
   partition; the Kr and Phi-K syncs would pull them in.

## Appendix: all 117 operations

From the generated contract [C]. Every query parameter is required. "pattern" marks the kind pattern of section 2.4.
"not declared" marks a path variable with no parameter; its value is ignored (run).

| Tag | Method | Path | operationId | Path parameters | Query parameters (all required) | Body | Bearer |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Informations | GET | `/` | `health__get` | none | none | none | no |
| Estimated Volumes | GET | `/ddms/estimated-volumes/` | `get_all_ddms_estimated_volumes__get` | none | `data_partition_id` (string), `parent_type` (string) | none | yes |
| Estimated Volumes | GET | `/ddms/estimated-volumes/{estimated_volumes_id}` | `get_by_id_ddms_estimated_volumes__estimated_volumes_id__get` | `estimated_volumes_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Estimated Volumes | DELETE | `/ddms/estimated-volumes/{estimated_volumes_id}` | `delete_by_id_ddms_estimated_volumes__estimated_volumes_id__delete` | `estimated_volumes_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Estimated Volumes | GET | `/ddms/estimated-volumes/parent/{parent_id}` | `get_by_parent_id_ddms_estimated_volumes_parent__parent_id__get` | `parent_id` | `data_partition_id` (string) | none | yes |
| Estimated Volumes | PUT | `/ddms/estimated-volumes` | `update_ddms_estimated_volumes_put` | none | `data_partition_id` (string) | JSON array, items untyped | yes |
| Estimated Volumes Det | GET | `/ddms/estimated-volumes-det/` | `get_all_estimated_volume_detail_ddms_estimated_volumes_det__get` | none | `master_type` (string) | none | yes |
| Estimated Volumes Det | GET | `/ddms/estimated-volumes-det/{estimated_volumes_det_id}` | `get_estimated_volume_detail_by_id_ddms_estimated_volumes_det__estimated_volumes_det_id__get` | `estimated_volumes_det_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Estimated Volumes Det | DELETE | `/ddms/estimated-volumes-det/{estimated_volumes_det_id}` | `delete_ddms_estimated_volumes_det__estimated_volumes_det_id__delete` | `estimated_volumes_det_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Estimated Volumes Det | GET | `/ddms/estimated-volumes-det/header-entity/{estimated_volumes_id}` | `get_by_header_entity_id_ddms_estimated_volumes_det_header_entity__estimated_volumes_id__get` | `estimated_volumes_id` (not declared) | `header_entity_id` (string) | none | yes |
| Estimated Volumes Det | PUT | `/ddms/estimated-volumes-det` | `update_ddms_estimated_volumes_det_put` | none | none | JSON, untyped | yes |
| Estimated Volumes Det | POST | `/ddms/estimated-volumes-det` | `create_ddms_estimated_volumes_det_post` | none | none | JSON, untyped | yes |
| Pvt Properties | GET | `/ddms/pvt-properties/` | `get_all_ddms_pvt_properties__get` | none | `data_partition_id` (string), `parent_type` (string) | none | yes |
| Pvt Properties | GET | `/ddms/pvt-properties/{pvt_properties_id}` | `get_by_id_ddms_pvt_properties__pvt_properties_id__get` | `pvt_properties_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Pvt Properties | DELETE | `/ddms/pvt-properties/{pvt_properties_id}` | `delete_by_id_ddms_pvt_properties__pvt_properties_id__delete` | `pvt_properties_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Pvt Properties | GET | `/ddms/pvt-properties/parent/{parent_id}` | `get_by_parent_id_ddms_pvt_properties_parent__parent_id__get` | `parent_id` | `data_partition_id` (string) | none | yes |
| Pvt Properties | PUT | `/ddms/pvt-properties` | `update_ddms_pvt_properties_put` | none | `data_partition_id` (string) | JSON array, items untyped | yes |
| Geological Labels | GET | `/ddms/geological-labels/` | `get_all_ddms_geological_labels__get` | none | `data_partition_id` (string), `parent_type` (string) | none | yes |
| Geological Labels | GET | `/ddms/geological-labels/{geological_labels_id}` | `get_by_id_ddms_geological_labels__geological_labels_id__get` | `geological_labels_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Geological Labels | DELETE | `/ddms/geological-labels/{geological_labels_id}` | `delete_by_id_ddms_geological_labels__geological_labels_id__delete` | `geological_labels_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Geological Labels | GET | `/ddms/geological-labels/parent/{parent_id}` | `get_by_parent_id_ddms_geological_labels_parent__parent_id__get` | `parent_id` | `data_partition_id` (string) | none | yes |
| Geological Labels | PUT | `/ddms/geological-labels` | `update_ddms_geological_labels_put` | none | `data_partition_id` (string) | JSON array, items untyped | yes |
| Petro Properties | GET | `/ddms/petro-properties/` | `get_all_ddms_petro_properties__get` | none | `data_partition_id` (string), `parent_type` (string) | none | yes |
| Petro Properties | GET | `/ddms/petro-properties/{petro_properties_id}` | `get_by_id_ddms_petro_properties__petro_properties_id__get` | `petro_properties_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Petro Properties | DELETE | `/ddms/petro-properties/{petro_properties_id}` | `delete_by_id_ddms_petro_properties__petro_properties_id__delete` | `petro_properties_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Petro Properties | GET | `/ddms/petro-properties/parent/{parent_id}` | `get_by_parent_id_ddms_petro_properties_parent__parent_id__get` | `parent_id` | `data_partition_id` (string) | none | yes |
| Petro Properties | PUT | `/ddms/petro-properties` | `update_ddms_petro_properties_put` | none | `data_partition_id` (string) | JSON array, items untyped | yes |
| Tank Datum | GET | `/ddms/tank-datum/` | `get_all_ddms_tank_datum__get` | none | `data_partition_id` (string), `parent_type` (string) | none | yes |
| Tank Datum | GET | `/ddms/tank-datum/{tank_datum_id}` | `get_by_id_ddms_tank_datum__tank_datum_id__get` | `tank_datum_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Tank Datum | DELETE | `/ddms/tank-datum/{tank_datum_id}` | `delete_by_id_ddms_tank_datum__tank_datum_id__delete` | `tank_datum_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Tank Datum | GET | `/ddms/tank-datum/parent/{parent_id}` | `get_by_parent_id_ddms_tank_datum_parent__parent_id__get` | `parent_id` | `data_partition_id` (string) | none | yes |
| Tank Datum | PUT | `/ddms/tank-datum` | `update_ddms_tank_datum_put` | none | `data_partition_id` (string) | JSON array, items untyped | yes |
| Aquifer Datum | GET | `/ddms/aquifer-datum/` | `get_all_estimated_volume_detail_ddms_aquifer_datum__get` | none | `master_type` (string) | none | yes |
| Aquifer Datum | GET | `/ddms/aquifer-datum/{aquifer_datum_id}` | `get_estimated_volume_detail_by_id_ddms_aquifer_datum__aquifer_datum_id__get` | `aquifer_datum_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Aquifer Datum | DELETE | `/ddms/aquifer-datum/{aquifer_datum_id}` | `delete_ddms_aquifer_datum__aquifer_datum_id__delete` | `aquifer_datum_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Aquifer Datum | GET | `/ddms/aquifer-datum/header-entity/{tank_datum_id}` | `get_by_header_entity_id_ddms_aquifer_datum_header_entity__tank_datum_id__get` | `tank_datum_id` (not declared) | `header_entity_id` (string) | none | yes |
| Aquifer Datum | PUT | `/ddms/aquifer-datum` | `update_ddms_aquifer_datum_put` | none | none | JSON, untyped | yes |
| Aquifer Datum | POST | `/ddms/aquifer-datum` | `create_ddms_aquifer_datum_post` | none | none | JSON, untyped | yes |
| Fluid Synthesis | GET | `/ddms/fluid-synthesis/` | `get_all_ddms_fluid_synthesis__get` | none | `data_partition_id` (string), `parent_type` (string) | none | yes |
| Fluid Synthesis | GET | `/ddms/fluid-synthesis/{fluid_synthesis_id}` | `get_by_id_ddms_fluid_synthesis__fluid_synthesis_id__get` | `fluid_synthesis_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Fluid Synthesis | DELETE | `/ddms/fluid-synthesis/{fluid_synthesis_id}` | `delete_by_id_ddms_fluid_synthesis__fluid_synthesis_id__delete` | `fluid_synthesis_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Fluid Synthesis | GET | `/ddms/fluid-synthesis/parent/{parent_id}` | `get_by_parent_id_ddms_fluid_synthesis_parent__parent_id__get` | `parent_id` | `data_partition_id` (string) | none | yes |
| Fluid Synthesis | PUT | `/ddms/fluid-synthesis` | `update_ddms_fluid_synthesis_put` | none | `data_partition_id` (string) | JSON array, items untyped | yes |
| Fluid Synthesis Tank Pvt | GET | `/ddms/fluid-synthesis-tank-pvt/` | `get_all_estimated_volume_detail_ddms_fluid_synthesis_tank_pvt__get` | none | `master_type` (string) | none | yes |
| Fluid Synthesis Tank Pvt | GET | `/ddms/fluid-synthesis-tank-pvt/{fluid_synthesis_tank_pvt_id}` | `get_estimated_volume_detail_by_id_ddms_fluid_synthesis_tank_pvt__fluid_synthesis_tank_pvt_id__get` | `fluid_synthesis_tank_pvt_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Fluid Synthesis Tank Pvt | DELETE | `/ddms/fluid-synthesis-tank-pvt/{fluid_synthesis_tank_pvt_id}` | `delete_ddms_fluid_synthesis_tank_pvt__fluid_synthesis_tank_pvt_id__delete` | `fluid_synthesis_tank_pvt_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Fluid Synthesis Tank Pvt | GET | `/ddms/fluid-synthesis-tank-pvt/header-entity/{fluid_synthesis_id}` | `get_by_header_entity_id_ddms_fluid_synthesis_tank_pvt_header_entity__fluid_synthesis_id__get` | `fluid_synthesis_id` (not declared) | `header_entity_id` (string) | none | yes |
| Fluid Synthesis Tank Pvt | PUT | `/ddms/fluid-synthesis-tank-pvt` | `update_ddms_fluid_synthesis_tank_pvt_put` | none | none | JSON, untyped | yes |
| Fluid Synthesis Tank Pvt | POST | `/ddms/fluid-synthesis-tank-pvt` | `create_ddms_fluid_synthesis_tank_pvt_post` | none | none | JSON, untyped | yes |
| Fluid Synthesis Tank Blackoil | GET | `/ddms/fluid-synthesis-tank-blackoil/` | `get_all_estimated_volume_detail_ddms_fluid_synthesis_tank_blackoil__get` | none | `master_type` (string) | none | yes |
| Fluid Synthesis Tank Blackoil | GET | `/ddms/fluid-synthesis-tank-blackoil/{fluid_synthesis_tank_blackoil_id}` | `get_estimated_volume_detail_by_id_ddms_fluid_synthesis_tank_blackoil__fluid_synthesis_tank_blackoil_id__get` | `fluid_synthesis_tank_blackoil_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Fluid Synthesis Tank Blackoil | DELETE | `/ddms/fluid-synthesis-tank-blackoil/{fluid_synthesis_tank_blackoil_id}` | `delete_ddms_fluid_synthesis_tank_blackoil__fluid_synthesis_tank_blackoil_id__delete` | `fluid_synthesis_tank_blackoil_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Fluid Synthesis Tank Blackoil | GET | `/ddms/fluid-synthesis-tank-blackoil/header-entity/{fluid_synthesis_id}` | `get_by_header_entity_id_ddms_fluid_synthesis_tank_blackoil_header_entity__fluid_synthesis_id__get` | `fluid_synthesis_id` (not declared) | `header_entity_id` (string) | none | yes |
| Fluid Synthesis Tank Blackoil | PUT | `/ddms/fluid-synthesis-tank-blackoil` | `update_ddms_fluid_synthesis_tank_blackoil_put` | none | none | JSON, untyped | yes |
| Fluid Synthesis Tank Blackoil | POST | `/ddms/fluid-synthesis-tank-blackoil` | `create_ddms_fluid_synthesis_tank_blackoil_post` | none | none | JSON, untyped | yes |
| Kr Synthesis | GET | `/ddms/kr-synthesis/` | `get_all_ddms_kr_synthesis__get` | none | `data_partition_id` (string), `parent_type` (string) | none | yes |
| Kr Synthesis | GET | `/ddms/kr-synthesis/{kr_synthesis_id}` | `get_by_id_ddms_kr_synthesis__kr_synthesis_id__get` | `kr_synthesis_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Kr Synthesis | DELETE | `/ddms/kr-synthesis/{kr_synthesis_id}` | `delete_by_id_ddms_kr_synthesis__kr_synthesis_id__delete` | `kr_synthesis_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Kr Synthesis | GET | `/ddms/kr-synthesis/parent/{parent_id}` | `get_by_parent_id_ddms_kr_synthesis_parent__parent_id__get` | `parent_id` | `data_partition_id` (string) | none | yes |
| Kr Synthesis | PUT | `/ddms/kr-synthesis` | `update_ddms_kr_synthesis_put` | none | `data_partition_id` (string) | JSON array, items untyped | yes |
| Kr Synthesis RT | GET | `/ddms/kr-synthesis-rt/` | `get_all_estimated_volume_detail_ddms_kr_synthesis_rt__get` | none | `master_type` (string) | none | yes |
| Kr Synthesis RT | GET | `/ddms/kr-synthesis-rt/{kr_synthesis_rt_id}` | `get_estimated_volume_detail_by_id_ddms_kr_synthesis_rt__kr_synthesis_rt_id__get` | `kr_synthesis_rt_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Kr Synthesis RT | DELETE | `/ddms/kr-synthesis-rt/{kr_synthesis_rt_id}` | `delete_ddms_kr_synthesis_rt__kr_synthesis_rt_id__delete` | `kr_synthesis_rt_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Kr Synthesis RT | GET | `/ddms/kr-synthesis-rt/header-entity/{kr_synthesis_id}` | `get_by_header_entity_id_ddms_kr_synthesis_rt_header_entity__kr_synthesis_id__get` | `kr_synthesis_id` (not declared) | `header_entity_id` (string) | none | yes |
| Kr Synthesis RT | PUT | `/ddms/kr-synthesis-rt` | `update_ddms_kr_synthesis_rt_put` | none | none | JSON, untyped | yes |
| Kr Synthesis RT | POST | `/ddms/kr-synthesis-rt` | `create_ddms_kr_synthesis_rt_post` | none | none | JSON, untyped | yes |
| Kr Synthesis Kr | GET | `/ddms/kr-synthesis-kr/` | `get_all_estimated_volume_detail_ddms_kr_synthesis_kr__get` | none | `master_type` (string) | none | yes |
| Kr Synthesis Kr | GET | `/ddms/kr-synthesis-kr/{kr_synthesis_kr_id}` | `get_estimated_volume_detail_by_id_ddms_kr_synthesis_kr__kr_synthesis_kr_id__get` | `kr_synthesis_kr_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Kr Synthesis Kr | DELETE | `/ddms/kr-synthesis-kr/{kr_synthesis_kr_id}` | `delete_ddms_kr_synthesis_kr__kr_synthesis_kr_id__delete` | `kr_synthesis_kr_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Kr Synthesis Kr | GET | `/ddms/kr-synthesis-kr/header-entity/{kr_synthesis_rt_id}` | `get_by_header_entity_id_ddms_kr_synthesis_kr_header_entity__kr_synthesis_rt_id__get` | `kr_synthesis_rt_id` (not declared) | `header_entity_id` (string) | none | yes |
| Kr Synthesis Kr | PUT | `/ddms/kr-synthesis-kr` | `update_ddms_kr_synthesis_kr_put` | none | none | JSON, untyped | yes |
| Kr Synthesis Kr | POST | `/ddms/kr-synthesis-kr` | `create_ddms_kr_synthesis_kr_post` | none | none | JSON, untyped | yes |
| Phi K Synthesis | GET | `/ddms/phi-k-synthesis/` | `get_all_ddms_phi_k_synthesis__get` | none | `data_partition_id` (string), `parent_type` (string) | none | yes |
| Phi K Synthesis | GET | `/ddms/phi-k-synthesis/{phi_k_synthesis_id}` | `get_by_id_ddms_phi_k_synthesis__phi_k_synthesis_id__get` | `phi_k_synthesis_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Phi K Synthesis | DELETE | `/ddms/phi-k-synthesis/{phi_k_synthesis_id}` | `delete_by_id_ddms_phi_k_synthesis__phi_k_synthesis_id__delete` | `phi_k_synthesis_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Phi K Synthesis | GET | `/ddms/phi-k-synthesis/parent/{parent_id}` | `get_by_parent_id_ddms_phi_k_synthesis_parent__parent_id__get` | `parent_id` | `data_partition_id` (string) | none | yes |
| Phi K Synthesis | PUT | `/ddms/phi-k-synthesis` | `update_ddms_phi_k_synthesis_put` | none | `data_partition_id` (string) | JSON array, items untyped | yes |
| Phi K Synthesis RT | GET | `/ddms/phi-k-synthesis-rt/` | `get_all_estimated_volume_detail_ddms_phi_k_synthesis_rt__get` | none | `master_type` (string) | none | yes |
| Phi K Synthesis RT | GET | `/ddms/phi-k-synthesis-rt/{phi_k_synthesis_rt_id}` | `get_estimated_volume_detail_by_id_ddms_phi_k_synthesis_rt__phi_k_synthesis_rt_id__get` | `phi_k_synthesis_rt_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Phi K Synthesis RT | DELETE | `/ddms/phi-k-synthesis-rt/{phi_k_synthesis_rt_id}` | `delete_ddms_phi_k_synthesis_rt__phi_k_synthesis_rt_id__delete` | `phi_k_synthesis_rt_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Phi K Synthesis RT | GET | `/ddms/phi-k-synthesis-rt/header-entity/{phi_k_synthesis_id}` | `get_by_header_entity_id_ddms_phi_k_synthesis_rt_header_entity__phi_k_synthesis_id__get` | `phi_k_synthesis_id` (not declared) | `header_entity_id` (string) | none | yes |
| Phi K Synthesis RT | PUT | `/ddms/phi-k-synthesis-rt` | `update_ddms_phi_k_synthesis_rt_put` | none | none | JSON, untyped | yes |
| Phi K Synthesis RT | POST | `/ddms/phi-k-synthesis-rt` | `create_ddms_phi_k_synthesis_rt_post` | none | none | JSON, untyped | yes |
| Phi K Synthesis Phi K | GET | `/ddms/phi-k-synthesis-phi-k/` | `get_all_estimated_volume_detail_ddms_phi_k_synthesis_phi_k__get` | none | `master_type` (string) | none | yes |
| Phi K Synthesis Phi K | GET | `/ddms/phi-k-synthesis-phi-k/{phi_k_synthesis_phi_k_id}` | `get_estimated_volume_detail_by_id_ddms_phi_k_synthesis_phi_k__phi_k_synthesis_phi_k_id__get` | `phi_k_synthesis_phi_k_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Phi K Synthesis Phi K | DELETE | `/ddms/phi-k-synthesis-phi-k/{phi_k_synthesis_phi_k_id}` | `delete_ddms_phi_k_synthesis_phi_k__phi_k_synthesis_phi_k_id__delete` | `phi_k_synthesis_phi_k_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Phi K Synthesis Phi K | GET | `/ddms/phi-k-synthesis-phi-k/header-entity/{phi_k_synthesis_rt_id}` | `get_by_header_entity_id_ddms_phi_k_synthesis_phi_k_header_entity__phi_k_synthesis_rt_id__get` | `phi_k_synthesis_rt_id` (not declared) | `header_entity_id` (string) | none | yes |
| Phi K Synthesis Phi K | PUT | `/ddms/phi-k-synthesis-phi-k` | `update_ddms_phi_k_synthesis_phi_k_put` | none | none | JSON, untyped | yes |
| Phi K Synthesis Phi K | POST | `/ddms/phi-k-synthesis-phi-k` | `create_ddms_phi_k_synthesis_phi_k_post` | none | none | JSON, untyped | yes |
| Forecast | GET | `/ddms/forecast/` | `get_all_ddms_forecast__get` | none | `data_partition_id` (string), `parent_type` (string) | none | yes |
| Forecast | GET | `/ddms/forecast/{forecast_id}` | `get_by_id_ddms_forecast__forecast_id__get` | `forecast_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Forecast | DELETE | `/ddms/forecast/{forecast_id}` | `delete_by_id_ddms_forecast__forecast_id__delete` | `forecast_id` (not declared) | `data_partition_id` (string), `catalog_entity_id` (string, pattern) | none | yes |
| Forecast | GET | `/ddms/forecast/parent/{parent_id}` | `get_by_parent_id_ddms_forecast_parent__parent_id__get` | `parent_id` | `data_partition_id` (string) | none | yes |
| Forecast | PUT | `/ddms/forecast` | `update_ddms_forecast_put` | none | `data_partition_id` (string) | JSON array, items untyped | yes |
| Forecast Fluid | GET | `/ddms/forecast-fluid/` | `get_all_estimated_volume_detail_ddms_forecast_fluid__get` | none | `master_type` (string) | none | yes |
| Forecast Fluid | GET | `/ddms/forecast-fluid/{forecast_fluid_id}` | `get_estimated_volume_detail_by_id_ddms_forecast_fluid__forecast_fluid_id__get` | `forecast_fluid_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Forecast Fluid | DELETE | `/ddms/forecast-fluid/{forecast_fluid_id}` | `delete_ddms_forecast_fluid__forecast_fluid_id__delete` | `forecast_fluid_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Forecast Fluid | GET | `/ddms/forecast-fluid/header-entity/{forecast_id}` | `get_by_header_entity_id_ddms_forecast_fluid_header_entity__forecast_id__get` | `forecast_id` (not declared) | `header_entity_id` (string) | none | yes |
| Forecast Fluid | PUT | `/ddms/forecast-fluid` | `update_ddms_forecast_fluid_put` | none | none | JSON, untyped | yes |
| Forecast Fluid | POST | `/ddms/forecast-fluid` | `create_ddms_forecast_fluid_post` | none | none | JSON, untyped | yes |
| Forecast Det | GET | `/ddms/forecast-det/` | `get_all_estimated_volume_detail_ddms_forecast_det__get` | none | `master_type` (string) | none | yes |
| Forecast Det | GET | `/ddms/forecast-det/{forecast_det_id}` | `get_estimated_volume_detail_by_id_ddms_forecast_det__forecast_det_id__get` | `forecast_det_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Forecast Det | DELETE | `/ddms/forecast-det/{forecast_det_id}` | `delete_ddms_forecast_det__forecast_det_id__delete` | `forecast_det_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Forecast Det | GET | `/ddms/forecast-det/header-entity/{forecast_id}` | `get_by_header_entity_id_ddms_forecast_det_header_entity__forecast_id__get` | `forecast_id` (not declared) | `header_entity_id` (string) | none | yes |
| Forecast Det | PUT | `/ddms/forecast-det` | `update_ddms_forecast_det_put` | none | none | JSON, untyped | yes |
| Forecast Det | POST | `/ddms/forecast-det` | `create_ddms_forecast_det_post` | none | none | JSON, untyped | yes |
| Forecast Det Fluid | GET | `/ddms/forecast-det-fluid/` | `get_all_estimated_volume_detail_ddms_forecast_det_fluid__get` | none | `master_type` (string) | none | yes |
| Forecast Det Fluid | GET | `/ddms/forecast-det-fluid/{forecast_det_fluid_id}` | `get_estimated_volume_detail_by_id_ddms_forecast_det_fluid__forecast_det_fluid_id__get` | `forecast_det_fluid_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Forecast Det Fluid | DELETE | `/ddms/forecast-det-fluid/{forecast_det_fluid_id}` | `delete_ddms_forecast_det_fluid__forecast_det_fluid_id__delete` | `forecast_det_fluid_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Forecast Det Fluid | GET | `/ddms/forecast-det-fluid/header-entity/{forecast_det_id}` | `get_by_header_entity_id_ddms_forecast_det_fluid_header_entity__forecast_det_id__get` | `forecast_det_id` (not declared) | `header_entity_id` (string) | none | yes |
| Forecast Det Fluid | PUT | `/ddms/forecast-det-fluid` | `update_ddms_forecast_det_fluid_put` | none | none | JSON, untyped | yes |
| Forecast Det Fluid | POST | `/ddms/forecast-det-fluid` | `create_ddms_forecast_det_fluid_post` | none | none | JSON, untyped | yes |
| Forecast Base | GET | `/ddms/forecast-base/` | `get_all_estimated_volume_detail_ddms_forecast_base__get` | none | `master_type` (string) | none | yes |
| Forecast Base | GET | `/ddms/forecast-base/{forecast_base_id}` | `get_estimated_volume_detail_by_id_ddms_forecast_base__forecast_base_id__get` | `forecast_base_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Forecast Base | DELETE | `/ddms/forecast-base/{forecast_base_id}` | `delete_ddms_forecast_base__forecast_base_id__delete` | `forecast_base_id` (not declared) | `catalog_entity_id` (integer) | none | yes |
| Forecast Base | PUT | `/ddms/forecast-base` | `update_ddms_forecast_base_put` | none | none | JSON, untyped | yes |
| Forecast Base | POST | `/ddms/forecast-base` | `create_ddms_forecast_base_post` | none | none | JSON, untyped | yes |
