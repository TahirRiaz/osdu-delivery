# Seismic DDMS (Seismic Store): integration brief

Seismic DDMS, also called Seismic Store or SDMS, is the OSDU domain service that catalogues seismic datasets, issues
short-lived credentials so that clients move bulk bytes directly to and from cloud object storage, and can write each
dataset's OSDU Storage record. OSDU Delivery reaches it through the `seismicStore` route type (stage 7 of
`docs/osdu-coverage-plan.md`). This brief lists every call that route makes, with the contract each call comes from,
and the service and client behaviour the route depends on, as read in the pinned contract and in the service and
client source at the commits named below.

## Sources

| Key | Source | Revision |
| --- | --- | --- |
| `C` | `osdu/specs/seismic-ddms/openapi.yaml`: the Seismic DMS v3 OpenAPI 3.0.0 contract. Copied from project 395, `app/sdms/docs/api/openapi.yaml`, and byte-identical to that file. | commit `0eec0a874ba22e550248bf21cedb08a476dde167` (2026-09-11), as recorded in `osdu/specs/sources.json` |
| `S` | `osdu/specs/core/storage/openapi.yaml`: the Storage service v2 contract from the core specification set. Used for the Storage calls the route makes and the ones Seismic Store makes. | copied from the core specification set; `sources.json` records no upstream commit |
| `395` | Project 395, `osdu/platform/domain-data-mgmt-services/seismic/seismic-dms-suite/seismic-store-service`: the service (`app/sdms` is v3, `app/sdms-v4` is v4), its charts, tests and schema copies. Cited with a path from the repository root. | branch `master`, commit `0eec0a874ba22e550248bf21cedb08a476dde167` (committed 2026-09-11; the branch head on 2026-09-16) |
| `v3` | Short for `395 app/sdms/src/` (the v3 service, TypeScript). | as `395` |
| `v4` | Short for `395 app/sdms-v4/src/` (the v4 service, TypeScript). | as `395` |
| `397` | Project 397, `osdu/platform/domain-data-mgmt-services/seismic/seismic-dms-suite/seismic-store-sdutil`: sdutil, the Python command-line client. | branch `master`, commit `3f3891d3c89e6d8aec3923508ea7324d269ccbd0` (committed 2026-09-11; the branch head on 2026-09-16) |
| `1552` | Project 1552, `osdu/platform/domain-data-mgmt-services/seismic/seismic-dms-suite/seismic-dms-sdfs`: sdfs, the Python fsspec filesystem used by MDIO. | branch `main`, commit `ea6307ea131196dbe69b8e206f42023f4085eea6` (committed 2026-08-31; the branch head on 2026-09-16) |

How statements are marked:

- `[C:<lines>, <operationId or schema>]` and `[S:<lines>, <operationId or schema>]` are **contract**: a line range in
  the pinned file.
- `[395 <path>:<lines>]`, `[v3 <path>:<lines>]`, `[v4 <path>:<lines>]`, `[397 <path>:<lines>]` and
  `[1552 <path>:<lines>]` are **source**: what the code does at the commit above. Source is not a contract guarantee.
  A citation that repeats the file of the citation before it may give only the line numbers.
- **(inference)** marks a conclusion drawn from reading code, or from library and platform behaviour, that no cited
  file states outright. It is confirmed against a live deployment before the route depends on it.

The contract describes v3 only. v4 has no committed OpenAPI file: `docs/docstart.ts` writes `docs/openapi.yaml` from
code [v4 docs/docstart.ts:19; v4 docs/generator.ts:33-65; v4 docs/parser.ts:26-262], and the running service resolves
that file in the background to serve Swagger UI [v4 server/server-start.ts:22-40]. Nothing of v4 is pinned in
`osdu/specs`, so every v4 statement below is source.

## 1. Base path, versions, headers and auth

### 1.1 Versions

- v3 is the contract: `openapi: 3.0.0`, `info.version: 3.0.0` [C:21-24]. The route is built on v3.
- v4 is a separate service in the same repository [395 README.md:11-14] and runs on Azure only (section 8).

### 1.2 Base path

The contract's server URL is a deployment placeholder, `#{SDMS_PREFIX}#` [C:34-35]. The v3 routers are mounted under
the configured `API_BASE_PATH` [v3 services/index.ts:35-69]; Swagger UI and the JSON document are served under
`SDMS_PREFIX` [v3 server/server.ts:91-98], which is a separate setting on some providers. The provider label is the
`CLOUDPROVIDER` environment variable [v3 server/server-start.ts:103]; the registered providers are `google`, `gc`,
`azure`, `ibm` and `anthos` [v3 cloud/providers/index.ts:17-21].

| Provider label | Default route base | Set from | Swagger prefix | Source |
| --- | --- | --- | --- | --- |
| `azure` | `/seistore-svc/api/v3` | env `SDMS_PREFIX` | the same value | [v3 cloud/providers/azure/config.ts:38-39, 173, 231]; the Azure chart routes `/seistore-svc/api/v3` [395 app/sdms/devops/azure/seismic-ddms.osdu.values.yaml:37] |
| `gc` (OSDU Google Cloud) | `/api/seismic-store/v3` | env `API_BASE_URL_PATH` | env `SDMS_PREFIX`, default `/api/seismic-store/v3` | [v3 cloud/providers/gc/config.ts:95, 123, 176] |
| `anthos` (CIMPL, S3-compatible storage) | `/api/seismic-store/v3` | env `API_BASE_PATH` | env `SDMS_PREFIX`, default `/api/seismic-store/v3` | [v3 cloud/providers/anthos/config.ts:59, 94] |
| `ibm` | `/api/v3` | fixed | env `SDMS_PREFIX`, default `/seistore-svc/api/v3` | [v3 cloud/providers/ibm/config.ts:26, 162, 198] |
| `google` (legacy) | `/api/v3` | env `API_BASE_URL_PATH` | env `SDMS_PREFIX`, default `/seistore-svc/api/v3` | [v3 cloud/providers/google/config.ts:51, 92, 120, 177] |
| v4 (`azure`) | `/seistore-svc/api/v4` | env `APIS_BASE_PATH` | the same value | [v4 cloud/config.ts:114; v4 server/server.ts:49; 395 app/sdms-v4/devops/azure/seismic-ddms-v4.osdu.values.yaml:14, 45] |

The public URL also depends on the ingress. sdutil's sample configurations use `https://<host>/seistore-svc/api/v3`
on Azure, `https://<host>/api/seismic-store/v3` on anthos and `https://nginx-ibm/osdu-seismic/api/v3` on IBM
[397 docs/config-azure.yaml:2-3; 397 docs/config-oauth2.yaml:30-31; 397 docs/config-ibm.yaml:2-3]. The route takes
the base URL, version path included, from configuration and never derives it.

The v3 routers under the base are `/dataset`, `/svcstatus`, `/info`, `/imptoken`, `/impersonation-token`,
`/subproject`, `/app`, `/tenant`, `/user`, `/utility`, `/operation` and `/analytics` [v3 services/index.ts:35-69].

Placeholders used below: `{base}` is the Seismic Store base URL with its version path; `{storage}` is the Storage
service base URL, `/api/storage/v2` on a standard deployment [S:13-14]; `{T}` tenant, `{SP}` subproject, `{P}` dataset
folder path without leading or trailing slash, `{N}` dataset name, `{SD}` = `sd://{T}/{SP}/{P}/{N}`, `{W}` the write
lock id the route generates.

### 1.3 Headers

| Header | Direction | Rule |
| --- | --- | --- |
| `Authorization: Bearer <token>` | request | Contract: global security `bearer`, an `apiKey` in header `Authorization` [C:2540-2541; C:2551-2555]. Code: 401 "Unauthenticated Access. Authorizations not found in the request." when absent, except for URLs ending in `svcstatus`, `readiness`, `info` or `metrics` and for `PUT .../imptoken` [v3 server/server.ts:143-161]. The service decodes the JWT payload without verifying a signature [v3 shared/utils.ts:117-134]; token validation is left to the platform in front of it. The token must be a JWT: register reads the caller id from its claims [v3 services/dataset/parser.ts:59-79; v3 shared/utils.ts:47-76]. sdfs documents the same requirement and, on Google, sends the identity provider's `id_token` instead of the opaque access token [1552 docs/csp_specifics.md:209-242; 1552 src/sdfs/providers/google.py:76-87]. **(inference)** A token that is not a JWT makes the payload parse fail and the call answer 500. |
| `data-partition-id` | request | The contract declares it only on the two operation-status reads, as required [C:2426-2431, operation-bulk-delete-get; C:2514-2519, operation-change-tier-get]; the code answers 400 there when it is missing [v3 services/operation/parser.ts:40-50, 94-99]. The dataset, utility and subproject handlers do not read it: the tenant comes from the URL path or the `sd://` path [v3 services/dataset/handler.ts:38-50]. The partition sent to core services is the first label of the registered tenant's `esd` [v3 services/tenant/dao.ts:57-59; v3 dataecosystem/utils.ts:22-28]. Clients send it anyway: sdfs sets it to the tenant name [1552 src/sdfs/clients/seismic_dms_client.py:322-334]; sdutil sends it only when one is configured [397 sdlib/api/seismic_store_service.py:738-741]. The route sends it on every call. v4 requires it (section 8.2). |
| `ltag` | request | Legal tag of the dataset on register [C:128-133, dataset-register], of the subproject on create [C:1795-1799, subproject-create] and on patch, where it is required [C:1924-1929, subproject-patch]. |
| `x-seismic-dms-lockid` | request | Not in the contract. Idempotency key read by register, lock and compute-size [v3 services/dataset/handler.ts:301-307, 1032-1035, 1256-1258]. It must start with `W` for write locks and `R` for read locks, else 400 [v3 services/dataset/locker.ts:203-206, 245-249, 307-311]; no length or character check is applied. Server-generated ids are `W` or `R` followed by 15 alphanumerics [v3 services/dataset/locker.ts:145-151; v3 shared/utils.ts:108-115]. The service's own end-to-end test uses `W` followed by 32 alphanumerics [395 app/sdms/tests/e2e/postman_collection.json, folder "idempotency", request "IDM STATUS SET"]. |
| `impersonation-token-context` | request | Optional, only with impersonation-token credentials [C:121-127, dataset-register]; every dataset operation declares the same parameter. The route uses its own identity and does not send it. |
| `x-user-id` | request | Optional forwarded identity; the header name is `USER_ID_HEADER_KEY_NAME`, default `x-user-id` [v3 cloud/config.ts:419]. See the caller id below. |
| `appkey` | request | Optional. Forwarded to core services as `AppKey` when it differs from `x-api-key`; otherwise the service's own key is used [v3 server/server.ts:184-187; v3 dataecosystem/storage.ts:30-36]. The e2e README calls `SVC_API_KEY` and `DE_APP_KEY` "historical variables and could be any string" [395 app/sdms/tests/e2e/README.md:19-20]; sdutil makes the header name configurable, default `appkey` [397 sdlib/shared/config.py:111-119]. |
| `correlation-id` | both | The name on Azure and gc, overridable with env `CORRELATION_ID` [v3 cloud/providers/azure/config.ts:32, 164; v3 cloud/providers/gc/config.ts:81, 118]. Generated when absent and echoed on the response [v3 server/server.ts:163-169; v3 shared/response.ts:66-68]. The anthos configuration sets no correlation header [v3 cloud/providers/anthos/config.ts:56-97; v3 cloud/config.ts:367]. |
| `Service-Provider` | response | The provider label, on every v3 and v4 response, errors included [v3 shared/response.ts:56-65; v4 shared/response.ts:48-58]. sdfs reads it from `GET {base}/svcstatus`, sent without a token, to choose its storage adapter [1552 src/sdfs/clients/seismic_dms_client.py:155-156, 302-320]. sdutil takes the provider from its own configuration instead [397 sdlib/api/seismic_store_service.py:34-35; 397 sdlib/shared/config.py:144-147]. |

The caller id that becomes `created_by`, and the `x-on-behalf-of` header on the Storage writes Seismic Store makes, is
resolved in this order: the forwarded user-id header; the claim named by `GDPR_COMPLIANT_USER_ID_KEY` (Azure default
`oid`) [v3 cloud/providers/azure/config.ts:228]; a provider lookup when `USER_ID_FROM_PROVIDER_API` is on (gc)
[v3 cloud/providers/gc/config.ts:86; v3 cloud/providers/gc/credentials.ts:161-181]; the claim named by
`USER_ID_CLAIM_FOR_SDMS` (default `subid` on azure, gc and anthos) [v3 cloud/providers/azure/config.ts:227;
v3 cloud/providers/gc/config.ts:169-170; v3 cloud/providers/anthos/config.ts:90]; finally `sub`
[v3 shared/utils.ts:47-76; v3 services/dataset/parser.ts:59-79; v3 services/dataset/handler.ts:324-335].

### 1.4 Authorization model

- Write operations (register, patch, delete, unlock, write lock, upload credentials, compute size) require membership
  of one of the subproject's `acls.admins` groups or of `users.data.root@<esd>` (`FULL_DATA_ACCESS_GROUP`, default
  `users.data.root`) [v3 services/subproject/auth.ts:24-29; v3 cloud/config.ts:346]. On a subproject with
  `access_policy: dataset`, a dataset's own `acls` replace the subproject's when the dataset has them
  [v3 services/dataset/auth.ts:26-45]. Read operations also accept the viewer groups.
- Membership is checked against the caller's Entitlements groups, or through the Policy service when
  `FEATURE_FLAG_POLICY_SVC_INTERACTION` is on [v3 auth/groups.ts:60-81]. The answer is cached for 60 seconds per token
  and group list; a refusal is 403 "User not authorized to perform this operation" [v3 auth/auth.ts:99-119].
- Legal tags are checked with `POST {legal}/legaltags:validate` [v3 dataecosystem/compliance.ts:24-60] on register,
  get, lock, compute size, and patch when `ltag` is patched [v3 services/dataset/handler.ts:165-177, 449-454, 996-1000,
  1239-1243, 719-724]. A valid answer is cached for one hour [v3 dataecosystem/compliance.ts:27-30, 53]. An invalid
  tag is 404 "The legal tag '...' is not valid." [v3 auth/auth.ts:186-195].
- Roles the contract documents: register `subproject.admin` [C:116]; get `subproject.admin` or `subproject.viewer` on a
  uniform subproject, `dataset.admin` or `dataset.viewer` on a dataset-policy subproject [C:191-198]; delete and patch
  the admin role of the applied policy [C:272-279; C:331-336]; lock for write admin, for read viewer [C:429-440];
  upload connection string admin [C:1489-1494]; download connection string viewer [C:1574-1579]; subproject get
  `subproject.admin` [C:1838]; subproject create `tenant.admin` [C:1790]; tenant create `tenant.admin` and
  `users.datalake.ops` [C:2005].
- Impersonation tokens are for `app.trusted` applications [C:1711-1782, impersonation-token-generate and
  impersonation-token-refresh]; the route does not use them.

## 2. Resource model

### 2.1 Tenant, subproject, dataset

- **Tenant.** Registered by an operator with `POST {base}/tenant/{tenantid}` and a body of `gcpid`, `esd` and
  `default_acls` [C:2003-2039, tenant-create; C:3222-3245, TenantCreateBody]. The contract states that in OSDU the
  tenant name matches the data-partition-id [C:1806-1811] and that `esd` starts with the partition name
  [C:3232-3238]. On azure, gc and ibm the code refuses, with 409, a tenant whose name differs from the first label of
  `esd` [v3 services/tenant/parser.ts:43-49; v3 cloud/providers/azure/dataecosystem.ts:50-52;
  v3 cloud/providers/gc/dataecosystem.ts:51-53; v3 cloud/providers/ibm/dataecosystem.ts:107-110]; anthos does not
  check it [v3 cloud/providers/anthos/dataecosystem.ts:44-46]. An unregistered tenant is 404
  `The tenant project {name} does not exist` [v3 services/tenant/dao.ts:47-51].
- **Subproject.** Created by a tenant admin with `POST {base}/subproject/tenant/{tenantid}/subproject/{subprojectid}`,
  header `ltag`, and a body of `admin`, `storage_class`, `storage_location`, `access_policy` (`uniform`, the default,
  or `dataset`) and `acls` [C:1785-1835, subproject-create; C:3091-3135, SubProjectCreateBody]. The name must match
  `^[a-z][a-z\d\-]*[a-z\d]$` [C:1800-1805; v3 services/subproject/parser.ts:58-63; v3 shared/sdpath.ts:57-60]. The
  stored subproject carries `ltag`, `acls`, `gcs_bucket` and `access_policy` [C:3163-3221, SubProject]. Without
  `acls`, the service creates the groups `data.sdms.<tenant>.<subproject>.<uuid>.admin@<esd>` and
  `data.sdms.<tenant>.<subproject>.<uuid>.viewer@<esd>` [v3 services/subproject/handler.ts:123-145;
  v3 services/subproject/groups.ts:55-65; v3 services/tenant/groups.ts:26-28]. The access policy cannot be patched
  except on the legacy `google` provider (400) [v3 services/subproject/parser.ts:82-89], and `dataset` cannot go back
  to `uniform` [v3 services/subproject/handler.ts:418-425]. Subprojects are provisioned by operators; the route never
  creates them.
- **Dataset.** Addressed as `sd://<tenant>/<subproject>/<path>*/<dataset>` [397 README.md:257-285;
  v3 shared/sdpath.ts:28-78]. REST addresses it with the path parameters `tenantid`, `subprojectid` and `datasetid`
  and the optional query parameter `path` [C:134-157]. The server wraps `path` as `/<path>/`, collapses `//`, and
  requires the result to match `^[\/A-Za-z0-9_\.-]*$`, else 400 [v3 services/dataset/parser.ts:360-375;
  v3 shared/params.ts:104-115]. The dataset name is taken from the URL segment with no pattern check
  [v3 services/dataset/parser.ts:360-365]. **(inference)** Nothing else validates the name, and a name that contains
  `/` cannot be written as an `sd://` path; the route restricts names to the path character set.

### 2.2 What a dataset holds

A dataset is a catalogue entry plus any number of objects in cloud storage under the location in `gcsurl`. The
catalogue is Cosmos DB on azure, Datastore on gc, PostgreSQL (through Prisma) on anthos and Cloudant on ibm
[v3 cloud/providers/azure/cosmosdb.ts:19; v3 cloud/providers/gc/datastore.ts:19, 29;
v3 cloud/providers/anthos/postgresql.ts:22, 91; v3 cloud/providers/ibm/datastore.ts:28, 32]. The contract has no
operation that carries bulk bytes: clients transfer bytes directly, with credentials the service issues
[C:1449-1495; 395 README.md:3-9].

- Contract fields of `Dataset` [C:2690-2804]: required `name`, `tenant`, `subproject`, `path`, `created_by`,
  `created_date`, `last_modified_date`, `gcsurl`, `ctag`; optional `filemetadata`, `metadata`, `readonly`, `status`,
  `ltag`, `seismicmeta_guid`, `sbit`, `sbit_count`, `seismicmeta`, `openzgy_v1`, `segy_v1`.
- The code's model adds `type`, `gtags`, `acls`, `access_policy`, `transfer_status`, `computed_size`,
  `computed_size_date` and the internal `storageSchemaRecordType` [v3 services/dataset/model.ts:20-47]; responses
  carry `access_policy` [v3 services/dataset/handler.ts:266, 401].

`filemetadata` and the storage location are described in section 4, the identities (`sbit`, `ctag`,
`seismicmeta_guid`) in section 5.

### 2.3 Relationship to OSDU Storage records

**v3 writes at most one Storage record per request, and only when the request body carries one.** The register and
patch bodies can carry one of `seismicmeta`, `segy_v1` or `openzgy_v1` [C:2642-2654, DatasetRegisterBody;
C:2838-2850, DatasetPatch]. The contract does not say they are exclusive; the code answers 400 "Only one of ... is
allowed in the request payload body." when more than one is present [v3 services/dataset/parser.ts:377-388].

- `seismicmeta` is accepted when `kind` is a string of four colon-separated parts and `data` is an object
  [v3 services/dataset/schema-manager/seismicmeta-manager.ts:41-55]. Nothing else is validated.
- `segy_v1` and `openzgy_v1` are validated with ajv against copies of `FileCollection.SEGY.1.0.0` and
  `FileCollection.Slb.OpenZGY.1.0.0` kept in project 395 [v3 services/dataset/schema-manager/segy-v1-manager.ts:26-32,
  47-65; v3 services/dataset/schema-manager/openzgy-v1-manager.ts:26-33, 47-66;
  395 app/sdms/docs/schemas/segy/FileCollection.SEGY.1.0.0.json;
  395 app/sdms/docs/schemas/openzgy/FileCollection.Slb.OpenZGY.1.0.0.json]. Those copies require only `kind` at the
  top level; allow no top-level keys other than `id`, `kind`, `version`, `acl`, `legal`, `tags`, `createTime`,
  `createUser`, `modifyTime`, `modifyUser`, `ancestry`, `meta` and `data`; require `owners` and `viewers` in `acl` and
  `legaltags` and `otherRelevantDataCountries` in `legal`; and require `data.DatasetProperties` when `data` is
  present [395 app/sdms/docs/schemas/abstract/AbstractAccessControlList.1.0.0.json;
  395 app/sdms/docs/schemas/abstract/AbstractLegalTags.1.0.0.json;
  395 app/sdms/docs/schemas/abstract/AbstractFileCollectionSchema.1.0.0.json]. The SEG-Y copy accepts any `kind`
  shaped `a:b:c:n.n.n` and ids matching `^[\w\.\-]+:dataset--FileCollection.SEGY:[\w\-\.:%]+$`. The OpenZGY copy's
  `kind` pattern is the unanchored literal `osdu:wks:dataset--FileCollection.Slb.OpenZGY:1.0.0`. **(inference, from
  evaluating the patterns as ajv compiles them)** A `...OpenZGY:1.1.0` kind fails that pattern, so `openzgy_v1`
  accepts 1.0.0 records only.
- Defaults applied before the record is written [v3 services/dataset/schema-manager/schema-manager.ts:94-132]:
  - `id`: kept when present; otherwise `<data-partition-id>:<third segment of kind>:<uuid v4>`, for example
    `opendes:dataset--FileCollection.SEGY:<uuid>` for kind `osdu:wks:dataset--FileCollection.SEGY:1.1.0`. The id is
    stored on the dataset as `seismicmeta_guid`.
  - `acl`, when absent: `owners: [data.default.owners@<esd>]`, `viewers: [data.default.viewers@<esd>]`.
  - `legal`, when absent: `{legaltags: [<dataset ltag>], otherRelevantDataCountries: ["US"]}`.
  - `recordType` is deleted.
- The record is written with `PUT {storage}/records`: the body is a one-element array, with the caller's
  `Authorization`, `data-partition-id` from the tenant's `esd`, `AppKey`, and `x-on-behalf-of: <caller id>`
  [v3 dataecosystem/storage.ts:25-55]. The Storage base path is `/api/storage/v2` on azure, gc and anthos
  [v3 cloud/providers/azure/dataecosystem.ts:35; v3 cloud/providers/gc/dataecosystem.ts:31;
  v3 cloud/providers/anthos/dataecosystem.ts:27] and configurable on ibm [v3 cloud/providers/ibm/dataecosystem.ts:89-92].
  This is Storage's `createOrUpdateRecords`: a new id creates a record, an existing id gets a new version
  [S:156-245, createOrUpdateRecords].
- On register the Storage write runs in parallel with the catalogue save [v3 services/dataset/handler.ts:324-335]. It
  is skipped, silently, when `FEATURE_FLAG_SEISMICMETA_STORAGE` is off; every provider defaults the flag to on
  [v3 services/dataset/handler.ts:328; v3 cloud/providers/azure/config.ts:203-204;
  v3 cloud/providers/gc/config.ts:151-152; v3 cloud/providers/anthos/config.ts:75-76;
  v3 cloud/providers/ibm/config.ts:179-180]. The Storage response (ids and versions [S:1793-1816,
  CreateUpdateRecordsResponse]) is discarded [v3 dataecosystem/storage.ts:52-54]; the register response carries only
  `seismicmeta_guid`.
- A patch that carries a record applies the same defaults against the stored dataset and writes the record again,
  before the catalogue update [v3 services/dataset/handler.ts:790-824]: a new version when the id is unchanged.
  **(inference)** A patched record without `id` gets a newly minted id and `seismicmeta_guid` moves to that new
  record, leaving the old one behind; the route always sends the id.
- `GET ...?seismicmeta=true`, optionally with `record-version=<v>`, reads the record by `seismicmeta_guid` and returns
  it under the key it was registered with [C:234-254, dataset-get; v3 services/dataset/handler.ts:378-435;
  v3 services/dataset/parser.ts:123-133]. A failed Storage read is swallowed and the key is simply absent
  [v3 services/dataset/handler.ts:411-419].
- For `segy_v1` and `openzgy_v1`, get and patch apply a transform looked up by the record's `kind` in one table shared
  by both managers. Only SEGY 1.0.0, 1.1.0 and 1.2.0 and OpenZGY 1.0.0 and 1.1.0 are registered, and the lookup has no
  fallback [v3 services/dataset/schema-manager/schema-manager.ts:80, 143-149;
  v3 services/dataset/schema-manager/segy-v1-manager.ts:34-45, 68-101;
  v3 services/dataset/schema-manager/openzgy-v1-manager.ts:35-45, 69-90]. The 1.1.0 and 1.2.0 transforms set `kind`
  and add a top-level `data-transformation-performed: true` to the record object; on patch this happens before the
  Storage write [v3 services/dataset/handler.ts:795-816]. **(inference)** A record of an unregistered kind sent
  through `segy_v1` or `openzgy_v1` fails on get and on patch with 500. The route therefore sends every record as
  `seismicmeta`, which applies no transform.

**What v3 does not do.**

- It never creates `work-product-component--SeismicTraceData`, or any record other than the one in the request body.
  The route writes the work-product-component, and any dataset record it does not send through `seismicmeta`, itself
  through Storage.
- Dataset delete leaves the Storage record in place [v3 services/dataset/handler.ts:563-644].
- The register rollback cannot remove the record. When register fails after the storage location and the catalogue
  key were prepared, the handler deletes the catalogue entry it finds and, when that entry has a `seismicmeta_guid`,
  calls
  `DESStorage.deleteRecord` (a logical delete, `POST /records/{id}:delete` [v3 dataecosystem/storage.ts:57-78]) with
  `last_modified_date` in the place of the record id [v3 services/dataset/handler.ts:276-283, 309-322].
  **(inference)** That Storage call fails; its error replaces the original error and skips the lock release on the
  next line, so the write lock stays until `unlock` or expiry. When the catalogue save itself failed, no entry is
  found and nothing is deleted, so a record already written by the parallel Storage call stays orphaned. Record
  cleanup is the route's job.

**The documented linkage pattern.** The maintainers' v3-to-v4 collection registers a v3 dataset, without an `ltag`
header and without a record `id`, whose `seismicmeta` is an `osdu:wks:dataset--FileCollection.SEGY:1.1.0` record with
`acl`, `legal`, `data.DatasetProperties.FileCollectionPath = "sd://{{Partition}}/{{Subproject}}{{testPath}}/"` and
`FileSourceInfos: [{"FileSource": "{{dataset}}"}]`, then takes the record id from the response's `seismicmeta_guid`.
The same folders exist for OpenZGY, OpenVDS and Generic [395 app/sdms-v4/tests/postman/azure/Seismic DMS V3 to
V4.postman_collection.json, folder "Segy", request "Segy Dataset Register SDMS-v3"]. v4 resolves exactly this shape
back to the v3 dataset [v4 apis/connection/handler.ts:62-84, 87-122].

## 3. The calls a writer makes (v3)

| Step | Call | Contract | Purpose |
| --- | --- | --- | --- |
| 0a | `GET {base}/svcstatus` | [C:60-75, service-status] | reachability and provider label |
| 0b | `GET {base}/svcstatus/access` | [C:78-94, service-status-check] | the `Authorization` header gets through |
| 0c | `GET {base}/subproject/tenant/{T}/subproject/{SP}` | [C:1836-1878, subproject-get] | tenant and subproject exist; policy, legal tag, groups; the route is an admin |
| 0d | `POST {base}/dataset/tenant/{T}/subproject/{SP}/exist` | [C:1088-1138, dataset-exist] | optional existence check |
| 1 | `POST {base}/dataset/tenant/{T}/subproject/{SP}/dataset/{N}?path={P}` | [C:114-187, dataset-register] | catalogue entry, write lock, storage location, Storage record |
| 2 | `GET {base}/utility/upload-connection-string?sdpath={SD}` | [C:1447-1529, utility-upload-connection-string] | write credentials |
| 3 | object store of the provider | none (cloud storage API) | bulk bytes |
| 4 | `PATCH {base}/dataset/tenant/{T}/subproject/{SP}/dataset/{N}?path={P}&close={W}` | [C:326-421, dataset-patch] | file metadata, read-only flag, lock release |
| 5a | `PUT {storage}/records` | [S:156-245, createOrUpdateRecords] | work-product-component and other records |
| 5b | `GET {storage}/records/versions/{id}` | [S:1422-1501, getRecordVersions] | the record version Seismic Store does not return |
| 6 | `POST {base}/dataset/tenant/{T}/subproject/{SP}/dataset/{N}/size?path={P}` | [C:777-833, dataset-size-post] | optional; Azure only |

### Step 0: preflight

1. `GET {base}/svcstatus` answers 200 with `service OK` [C:60-75]: the handler writes that string
   [v3 services/general/handler.ts:32-34], and the response carries `Service-Provider` [v3 shared/response.ts:60]. No
   token is needed [v3 server/server.ts:148-161]. **(inference)** Express sends a string body as text, not as a JSON
   string; sdutil reads it as text [397 sdlib/api/seismic_store_service.py:38-51].
2. `GET {base}/svcstatus/access` answers `{"status": "running"}` [C:78-94; C:2557-2564, Status;
   v3 services/general/handler.ts:35-37]. The code only needs the `Authorization` header to be present (section 10).
3. `GET {base}/subproject/tenant/{T}/subproject/{SP}` requires subproject admin or `users.data.root`
   [C:1838; v3 services/subproject/handler.ts:179-201] and re-validates the subproject's legal tag, so it is 404 when
   that tag is no longer valid [v3 services/subproject/handler.ts:205-210]. The response `SubProject` gives
   `access_policy`, `ltag`, `acls` and `gcs_bucket` [C:1865-1870; C:3163-3221]. A missing tenant or subproject is
   404; for a missing subproject the caller must also be in `users@<esd>`, else 403
   [v3 services/subproject/handler.ts:190-196; v3 services/tenant/groups.ts:46-48].
4. Optionally `POST {base}/dataset/tenant/{T}/subproject/{SP}/exist` with `{"datasets": ["{P}/{N}"]}`
   [C:1117-1118; C:2544-2550; C:2940-2952, DatasetCheckList] answers one boolean per entry [C:1120-1130;
   v3 services/dataset/handler.ts:1092-1128; v3 services/dataset/parser.ts:299-330]. Viewer role.

### Step 1: register the dataset

`POST {base}/dataset/tenant/{T}/subproject/{SP}/dataset/{N}?path={P}` [C:114-187, dataset-register;
v3 services/dataset/handler.ts:200-299; v3 services/dataset/parser.ts:53-96]

- Headers: `Authorization`, `data-partition-id`, `Content-Type: application/json`, `ltag` and
  `x-seismic-dms-lockid: {W}`. `ltag` is optional: the subproject's tag is used when it is absent, and the call is 404
  when neither exists [v3 services/dataset/handler.ts:155-163]. The route generates `{W}` as `W` followed by
  alphanumerics, because it is later sent as the `close` query value, and records it in the ledger before the call.
- Query: `path={P}`, URL-encoded once [C:146-151]; the server default is `/` [v3 services/dataset/parser.ts:370].
- Body `DatasetRegisterBody` [C:158-163; C:2631-2689]:
  - `type`, a string (checked), and `gtags`, a string array (not checked on register)
    [v3 services/dataset/parser.ts:58, 82-86];
  - `acls: {admins: [...], viewers: [...]}`, only on `dataset`-policy subprojects; on `uniform` it is 400
    [v3 services/dataset/handler.ts:213-216]. Both lists must be present, non-empty and made of valid group emails
    [v3 services/dataset/parser.ts:98-121; v3 shared/params.ts:73-102];
  - at most one of `seismicmeta`, `segy_v1`, `openzgy_v1` (section 2.3). The route sends the complete
    `dataset--FileCollection.*` record as `seismicmeta`, with a route-chosen `id`, `acl`, `legal`, and
    `data.DatasetProperties` pointing at `{SD}` as in the documented linkage pattern.
  - `metadata`, `filemetadata` and `readonly` are not read on register [v3 services/dataset/parser.ts:53-96]; the
    route sets them in step 4. An empty body is valid: sdutil and sdfs register without one
    [397 sdlib/api/seismic_store_service.py:241-253; 1552 src/sdfs/clients/seismic_dms_client.py:368-389].
- Server order [v3 services/dataset/handler.ts:200-275]: parse; 400 for `acls` on a uniform subproject; mutex and
  write lock (section 7.2); an idempotent replay returns here; legal-tag default; write authorization and legal-tag
  validation in parallel [v3 services/dataset/handler.ts:165-177]; 409 when the dataset exists [179-196]; Storage
  record defaults [352-358]; storage location, which on an Azure `dataset`-policy subproject creates a container
  [347-350]; catalogue key; catalogue save and Storage write in parallel [255-257, 324-335]; mutex released with the
  lock kept [259-260]; response decorated [262-275]. Before the save, the default tier is filled in, which on Azure
  writes `filemetadata.tier_class: "Hot"` [v3 services/dataset/handler.ts:252-253, 956-966; v3 cloud/storage.ts:88-99].
- Response 200, a `Dataset` [C:165-170]: `sbit` = `{W}` and `sbit_count: 1` [v3 services/dataset/handler.ts:268-270],
  `gcsurl`, `seismicmeta_guid`, `ctag` with the `<gcpid>;<partition>` suffix [262-263], `access_policy` [265-266],
  `created_by`, `created_date` and `last_modified_date` [v3 services/dataset/parser.ts:59-81]. The ledger records all
  of them.
- Contract errors: 400, 401, 403, 404, 409, 423 [C:171-187]. Replays, locks and the 423 path are in section 7.2.

### Step 2: obtain upload credentials

`GET {base}/utility/upload-connection-string?sdpath={SD}`, with `sdpath` URL-encoded [C:1447-1529,
utility-upload-connection-string]

- Contract: `sdpath` is required and described as `sd://tenant/subproject` for uniform policies or
  `sd://tenant/subproject/dataset` for dataset policies [C:1508-1514]; the response is an `AccessToken`
  `{access_token, token_type, expires_in}` [C:1516-1521; C:2969-2988, AccessToken].
- Code: both forms are accepted under both policies. With a dataset path the service looks the dataset up (404 when
  it is missing), authorizes against the dataset's admin groups, and scopes the credential with `gcsurl` split into
  bucket and first folder; with a subproject path it uses `gcs_bucket` [v3 services/utility/handler.ts:85-149;
  v3 services/utility/parser.ts:111-134]. The route asks with the dataset path. The per-provider format is in
  section 4.3.
- Download credentials come from `GET {base}/utility/download-connection-string?sdpath=...` [C:1532-1613,
  utility-download-connection-string], authorized against the viewer groups.
- The older equivalent is `GET {base}/utility/gcs-access-token?sdpath=...&readonly=false` [C:1395-1444,
  utility-gcs-access-token]; `readonly` defaults to true [C:1425-1429; v3 services/utility/parser.ts:157-166], and a
  subproject path on a `dataset`-policy subproject is 400 [v3 services/utility/handler.ts:169-173]. sdutil uses this
  endpoint and caches the token for 3000 seconds whatever scope was asked [397 sdlib/api/seismic_store_service.py:601-622];
  sdfs uses the connection-string endpoints [1552 src/sdfs/clients/seismic_dms_client.py:96-97, 463-493].

### Step 3: upload the objects

The service imposes no object naming; the layouts the in-tree readers expect are in section 4.4. Uploading does not
require holding the write lock: the credential endpoint checks authorization only [v3 services/utility/handler.ts:85-149],
and mutable operations skip the write-lock check because `SKIP_WRITE_LOCK_CHECK_ON_MUTABLE_OPERATIONS` is `true`
[v3 cloud/config.ts:206-212]. When an upload fails, sdutil deletes the half-created dataset
[397 sdlib/cmd/cp/cmd.py:295-300, 322-323].

### Step 4: finalize (file metadata and lock release)

`PATCH {base}/dataset/tenant/{T}/subproject/{SP}/dataset/{N}?path={P}&close={W}` [C:326-421, dataset-patch;
v3 services/dataset/handler.ts:651-858; v3 services/dataset/parser.ts:227-274]

- Body `DatasetPatch` [C:392-397; C:2805-2898]: `dataset_new_name`, `metadata`, `filemetadata`, `last_modified_date`,
  `gtags`, `ltag`, `readonly`, `status`, one record key, `acls`; the description also lists `change_tier` [C:341].
  What the code does with each:
  - `metadata` replaces the stored object [v3 services/dataset/handler.ts:701];
  - `filemetadata` keys are merged into the stored object, and `tier_class` is ignored [702-715];
  - `last_modified_date` from the body is overwritten with the server time [v3 services/dataset/parser.ts:261-262;
    v3 services/dataset/handler.ts:716];
  - `readonly` is set [717]; `gtags` replaces the list when non-empty [718]; `ltag` is validated, then set [719-724];
    `status` is set [725-727];
  - `dataset_new_name` renames, 409 when the new name exists [729-760];
  - `change_tier` must be one of the provider's tiers (Azure `Hot`, `Cool`, `Cold`) and changes the blob tiers in the
    background [v3 services/dataset/parser.ts:243-245; v3 services/dataset/handler.ts:762-788;
    v3 cloud/providers/azure/cloudstorage.ts:34];
  - `acls` only on a `dataset`-policy subproject, else 400 [v3 services/dataset/handler.ts:672-673, 806-809, 896-908];
  - a record key (section 2.3).
- Typical body, as sdutil sends it, with `md5Checksum` and `tier_class` only when the provider adapter reports them
  [397 sdlib/cmd/cp/cmd.py:302-321]:

  ```json
  {"filemetadata": {"type": "GENERIC", "size": 1048576, "nobjects": 1, "md5Checksum": "{hex digest}", "tier_class": "Hot"},
   "readonly": true}
  ```

  sdutil sets `readonly: true` for names ending in `.zgy`, `.sgy` or `.segy` unless told otherwise
  [397 sdlib/config.sample.yaml:20; 397 sdlib/cmd/cp/cmd.py:317-320].
- `close={W}` releases the write lock; a lock held under a different id is 404 "has been locked with different ID"
  [v3 services/dataset/locker.ts:388-408]. With a non-empty body the unlock runs before the dataset lookup and before
  the authorization check [v3 services/dataset/handler.ts:675-698]. **(inference)** A close with a body releases the
  lock even when the patch then fails with 403 or 404.
- An empty body with `close` only unlocks and returns the dataset; a `W` id needs the admin role, any other id the
  viewer role [v3 services/dataset/handler.ts:664-670, 910-954]. Without `close`, an empty body is 400 [C:330;
  v3 services/dataset/parser.ts:233-235; v3 shared/params.ts:22-39].
- Every patch sets a new `ctag` and `last_modified_date` [v3 services/dataset/dao.ts:100-104;
  v3 services/dataset/parser.ts:262].
- Another way to release a lock: `PUT {base}/dataset/tenant/{T}/subproject/{SP}/dataset/{N}/unlock?path={P}`
  [C:514-578, dataset-unlock] removes any lock, for an admin [v3 services/dataset/handler.ts:1051-1088]. When the
  dataset does not exist but a lock does (a failed register), it removes the lock and answers 200 without an
  authorization check; when neither exists it is 404 [1066-1076].
- Until it is closed, a write lock lasts up to 24 hours [v3 services/dataset/locker.ts:31, 210-213], and read-lock
  requests that do not present the write id get 423 [v3 services/dataset/locker.ts:323-335]. sdutil takes a read lock
  before a download [397 sdlib/cmd/cp/cmd.py:133-134]. A read-lock request on a `readonly` dataset returns the
  metadata without consulting the lock [v3 services/dataset/handler.ts:1015-1028].
- On Azure with `POST_PROCESS_ON_DATASET_CLOSE=true`, every close queues a compute-size task
  [v3 services/dataset/handler.ts:853-872; v3 cloud/postprocessor.ts:36-44;
  v3 cloud/providers/azure/postprocessor.ts:25-51; v3 cloud/providers/azure/config.ts:216-217].
- A `readonly` dataset cannot be locked for write (400) [v3 services/dataset/handler.ts:1015-1022].
  **(inference)** Patch and delete do not check `readonly`; no such check appears in
  [v3 services/dataset/handler.ts:563-858].
- `PUT {base}/dataset/tenant/{T}/subproject/{SP}/dataset/{N}/gtags?gtag=a&gtag=b` appends tags and drops duplicates
  [C:707-774, dataset-add-tag; v3 services/dataset/handler.ts:1317-1382].

### Step 5: OSDU records

- When the dataset record went in through `seismicmeta`, its id is `seismicmeta_guid`. Seismic Store does not return
  its version, so the route reads it with `GET {storage}/records/versions/{id}` [S:1422-1501, getRecordVersions;
  S:2440-2449, RecordVersions] or `GET {storage}/records/{id}` [S:1008-1097, getLatestRecordVersion].
- `work-product-component--SeismicTraceData` and any master data are written with `PUT {storage}/records`
  [S:156-245, createOrUpdateRecords], referencing the dataset record id. v3 has no endpoint for them; v4 has
  `tracedata` (section 8).
- A route-chosen record `id` makes retries converge: Storage creates a new version of an existing id instead of a
  second record [S:160-165].

### Step 6: optional size computation (Azure only)

- `POST {base}/dataset/tenant/{T}/subproject/{SP}/dataset/{N}/size?path={P}`, where the contract makes `path`
  required [C:813-818], computes, stores and returns `{computed_size, computed_size_date}` [C:777-833; C:3376-3389,
  DatasetSize; v3 services/dataset/handler.ts:1206-1291]. Only Azure implements the size listing; elsewhere it is 501
  [v3 cloud/storage.ts:61-63; v3 cloud/providers/azure/cloudstorage.ts:198-219].
- It takes the dataset's write lock itself, with the `x-seismic-dms-lockid` header, and deletes the lock when done
  [v3 services/dataset/handler.ts:1252-1289; v3 services/dataset/locker.ts:196-240]. **(inference)** Called while
  the register lock is held, it is 423 without that key, and with that key it releases the register lock. The route
  calls it only after the close.
- On a `uniform` subproject the size is the sum of the blobs directly under `<uuid>/`, from a hierarchical listing
  [v3 cloud/providers/azure/cloudstorage.ts:203-211]. **(inference)** Objects in sub-folders are not counted, which
  matters for directory datasets.
- `GET {base}/dataset/tenant/{T}/subproject/{SP}/size` returns `{size_bytes, dataset_count}` summed from
  `computed_size`, on Azure only [C:836-890, dataset-size-get; C:3390-3403, ComputedSize;
  v3 services/dataset/handler.ts:1184-1202; v3 cloud/journal.ts:200-202; v3 cloud/providers/azure/cosmosdb.ts:215].

## 4. Payload and bulk data shapes

### 4.1 `filemetadata`

- Contract: an object, "Number of objects and the size in bytes of the dataset" [C:2723-2725]. The patch description
  says the field "is mainly used by client libraries to correctly reconstruct the dataset" and gives the example
  `{nObject: N, totalSize: 1024, objsize: 64, sizeUnit: MB}` [C:340].
- In-tree clients: sdutil writes `{type: "GENERIC", size, nobjects}` plus `md5Checksum` and `tier_class` when the
  adapter reports them [397 sdlib/cmd/cp/cmd.py:302-315]; its `cpdir` writes `size` summed over the files and
  `nobjects` as the file count [397 sdlib/cmd/cpdir/cmd.py:340-358]. sdfs writes `{type: "GENERIC", nobjects, size}`
  from a listing of the dataset root, without `close` [1552 src/sdfs/core.py:986-1007;
  1552 src/sdfs/clients/seismic_dms_client.py:580-600].
- sdutil refuses to download a dataset whose `filemetadata` is missing, lacks `nobjects`, or has a `type` other than
  `GENERIC` [397 sdlib/cmd/cp/cmd.py:135-143]. The Azure reader also needs an exact `size` (section 4.4).
- On Azure, register already stores `tier_class: "Hot"` (step 1).
- `size` and `md5Checksum` are client assertions; the service does not check them.

### 4.2 Storage location (`gcsurl`)

The contract describes `gcsurl` only as "Cloud storage object identifier" [C:2729-2731]. Its shape depends on the
provider:

| Provider | Subproject `gcs_bucket` | Dataset `gcsurl` | Source |
| --- | --- | --- | --- |
| azure, `uniform` | container `ss-<env>-<15 chars>`, created with the subproject | `<container>/<uuid>`: a virtual folder in the subproject container | [v3 cloud/providers/azure/cloudstorage.ts:35, 67-80; v3 cloud/providers/azure/seistore.ts:54-66; v3 services/subproject/handler.ts:147-149, 391-403] |
| azure, `dataset` | a generated name; no container is created for the subproject | a dedicated container `<gcs_bucket>-<uuid>`, cut to 63 characters, created at register | [v3 cloud/providers/azure/seistore.ts:46-53, 58-66] |
| gc | `<partition property seismicBucket>/<uuid>`, reserved with a `.keep` object | `<bucket>/<subproject uuid>/<dataset uuid>` | [v3 cloud/providers/gc/gcs.ts:103-125; v3 cloud/providers/gc/partition.ts:37-59; v3 cloud/providers/gc/constants.ts:20; v3 cloud/providers/gc/seistore.ts:76-78; 1552 docs/csp_specifics.md:140-148] |
| anthos | `<SDMS_BUCKET>$$<16 chars>` | `<bucket>$$<folder>/<uuid>` | [v3 cloud/providers/anthos/storage.ts:90-94, 112-120; v3 cloud/providers/anthos/seistore.ts:41-43; v3 cloud/providers/anthos/config.ts:49] |
| ibm | `<prefix>-<16 chars>` | `<gcs_bucket>/<uuid>` | [v3 cloud/providers/ibm/cos.ts:44-53; v3 cloud/providers/ibm/seistore.ts:43-45] |

For credentials, deletion and sizing, the service splits `gcsurl` at `/` into a bucket (the first segment) and a
virtual folder (the second segment) [v3 services/dataset/utils.ts:19-26].

### 4.3 Upload credentials per provider

| Provider | `token_type` | `access_token` | Scope and lifetime | Source |
| --- | --- | --- | --- | --- |
| azure | `SasUrl` | `https://<account>.blob.core.windows.net/<container>[/<folder>]?<sas>`; the folder is appended when a dataset path is asked on a uniform subproject | User-delegation SAS signed for the container; the signature does not include the folder. List always; write, create and delete unless read-only; read unless the dataset is in a non-Hot tier and tier blocking applies, in which case a read-only request is 400. Tier blocking applies when `FEATURE_FLAG_TIER_STORAGE_BLOCK` is on (the Azure chart turns it on) and `filemetadata.live_tier_checked` exists. SAS valid 3599 minutes; `expires_in: 3599`. | [v3 cloud/providers/azure/credentials.ts:29-115; v3 services/utility/handler.ts:138-147; 395 app/sdms/devops/azure/seismic-ddms.osdu.values.yaml:73] |
| gc | `Bearer` | a Google access token downscoped to `storage.objectAdmin` (read-only: `storage.objectViewer`) on the bucket, with an object-name prefix condition | The prefix is the second `gcsurl` segment, which on gc is the subproject folder, so a dataset credential covers the whole subproject folder. `expires_in = expirationTime - Date.now()`. | [v3 cloud/providers/gc/credentials.ts:24-114; v3 services/utility/handler.ts:116-117] |
| anthos | `Bearer` | `AccessKeyId:SecretAccessKey:SessionToken` from STS `AssumeRole`, with an inline policy on `<bucket>/<keyPath>`, where `keyPath` is `<folder>/<uuid>` for a dataset and `<folder>` for a subproject; the upload policy allows put, get, delete, listing and multipart actions | `DurationSeconds` 3600, `expires_in: 3599`. The S3 endpoint is not returned. | [v3 cloud/providers/anthos/credentials.ts:94-137; v3 cloud/providers/anthos/stshelper.ts:42-142] |
| ibm | `Bearer` | the same key triple, scoped to the subproject bucket whatever path is asked | `expires_in = COS_TEMP_CRED_EXPIRY`, default 3600 | [v3 cloud/providers/ibm/credentials.ts:59-94; v3 cloud/providers/ibm/config.ts:114] |

- The contract describes `expires_in` as "expiration time (in minutes)" [C:2982-2984] and shows 3599 (Azure), 3600
  (Google), 3599 (AWS) and 7200 (IBM) in its examples [C:1455-1486]. **(inference)** The gc value is in milliseconds,
  from the google-auth-library `expirationTime`; the anthos and ibm values are seconds.
- **(inference)** On a `uniform` Azure subproject the SAS reaches every dataset in the subproject container.
- **(inference)** The Azure user-delegation key is cached and reused until 15 minutes before its own expiry
  [v3 cloud/providers/azure/credentials.ts:117-137]; a SAS signed with an older key stops working when that key
  expires, so the real lifetime can be much shorter than `expires_in`.
- The route treats `expires_in` as an upper bound, refreshes credentials early, and retries once with new
  credentials on a 403 from the object store.
- The anthos S3 endpoint is configuration. The service reads `S3_ENDPOINT` or `MINIO_ENDPOINT`
  [v3 cloud/providers/anthos/config.ts:45-47]; sdfs reads the same two variables and turns TLS verification off for
  that endpoint, which the route does not copy [1552 src/sdfs/providers/anthos.py:71-82;
  1552 docs/csp_specifics.md:90-118]; sdutil reads `MINIO_ENDPOINT` only
  [397 sdlib/api/providers/anthos/storage_service.py:47-63]. IBM clients take `COS_URL` and `COS_REGION`
  [397 README.md:130-135; 397 sdlib/api/providers/ibm/storage_service.py:41-45].

### 4.4 Object layout the in-tree readers expect

**Single-file formats (SEG-Y, OpenZGY), as sdutil writes them:**

- **azure**: the file is cut into chunks of `--chunk-size` MiB, default 32 [397 sdlib/cmd/cp/cmd.py:205-227;
  397 sdlib/api/providers/azure/storage_service.py:61-70], and each chunk becomes its own blob named `0`, `1`, ...
  under the dataset folder. Each blob is one staged block, committed with `validate_content=True` and with a
  `Content-MD5` equal to the running MD5 of the file up to that chunk, so the last blob carries the whole-file MD5
  [397 sdlib/api/providers/azure/storage_service.py:125-221]. `--chunk-size=0` writes a single blob `0` with the file
  MD5 [72-123]. On a `uniform` subproject sdutil takes the subproject SAS and rewrites `<container>` to
  `<container>/<uuid>` in its URL; on a `dataset` subproject it asks for a dataset SAS [46-59]. The upload reports
  `num_of_objects`, `md5_checksum` and `blob_tier`. The reader downloads blobs `0`, `1`, ... until the bytes read reach
  `filemetadata.size`, so `size` must be exact [223-281].
- **gc**: one object `<gcsurl without the bucket>/0`, sent as a resumable upload in 20 MiB pieces, then the local
  crc32c is compared with the object's [397 sdlib/api/providers/gc/storage_service.py:46-50, 134-189, 235-290]. The
  reader downloads `nobjects` objects `0` to `nobjects - 1` and compares each crc32c [292-322].
- **aws and anthos**: one object `<folder>/<uuid>/0` (the `gcsurl` split at `$$`) through the S3 transfer manager:
  multipart above 5 MiB with 8 MiB parts by default, and a single PutObject for files under 5 GiB with
  `--chunk-size=0` [397 sdlib/api/providers/aws/storage_service.py:46-122]. The reader downloads `nobjects` objects
  [124-149]. anthos also stores the MD5 of the local file, and notes that a multipart ETag is not the file MD5
  [397 sdlib/api/providers/anthos/storage_service.py:26-41, 74-94].
- **ibm**: one object `<uuid>/0` in the subproject bucket, with no checksum
  [397 sdlib/api/providers/ibm/storage_service.py:48-77].
- sdfs requires segmented files to be named with consecutive integers from `0` [1552 src/sdfs/core.py:505-541].

**Directory formats (MDIO, file trees):**

- sdutil `cpdir` uploads each file under its relative path [397 sdlib/cmd/cpdir/cmd.py:300-338]; only the gc adapter
  uses that key [397 sdlib/api/providers/gc/storage_service.py:244-246].
- sdfs maps an `sd://` path to `gs://<gcsurl>` on gc and to `s3://<bucket>/<object prefix>` on anthos, and writes
  arbitrary relative keys below it [1552 docs/csp_specifics.md:122-207; 1552 src/sdfs/providers/google.py:42-74;
  1552 src/sdfs/providers/anthos.py:26-34, 150-194]. sdfs implements only `gc` and `anthos`; its `azure`, `aws` and
  `ibm` adapters raise `NotImplementedError` [1552 src/sdfs/providers/factory.py:37-43;
  1552 src/sdfs/providers/azure.py:26-68].
- sdfs never registers a dataset: `_register_dataset` is defined but not called anywhere under `src/sdfs`
  [1552 src/sdfs/clients/seismic_dms_client.py:368-389], and its README asks for the test dataset to be registered in
  advance through the REST API [1552 README.md:128, 141].

## 5. Identities and versions

- **Dataset.** Unique per tenant, subproject, path and name; register answers 409 for a duplicate
  [v3 services/dataset/handler.ts:179-196]. The lock key is `<tenant>/<subproject><path><name>`
  [v3 services/dataset/handler.ts:301-307, 567]. The storage location is a fresh uuid at every register
  (section 4.2); **(inference)** a dataset registered again after a delete lands at a new location.
- **Write lock id (`sbit`).** "The session lockID" [C:2748-2750], with `sbit_count` "The number of sessions associated
  to the dataset" [C:2751-2753]. Returned by register [v3 services/dataset/handler.ts:268-270], lock [1037-1039] and
  patch (null and 0 after a close) [827-844]. Reads report the current lock: the write id and 1, the read ids joined
  and their count, or null and 0 [v3 services/dataset/dao.ts:264-284].
- **`ctag`.** 16 random characters, regenerated on register and on every update [v3 services/dataset/dao.ts:25-29,
  100-104], described as "The coherency tag ... It changes every time the dataset is updated" [C:2741-2744]. Register,
  get, list, lock and patch responses append `<tenant gcpid>;<data-partition-id>` [v3 services/dataset/handler.ts:262-263,
  397-398, 529-536, 1023-1024, 1041-1042, 846-847]. `GET {base}/dataset/tenant/{T}/subproject/{SP}/dataset/{N}/ctagcheck?path={P}&ctag=<full ctag>`
  [C:645-704, dataset-ctag-check] takes that full string: at least 19 characters, the first 16 are the tag, and the
  rest must split at `;` into gcpid and partition, else 400 [v3 services/dataset/parser.ts:28-51]. It answers `true`
  or `false` [v3 services/dataset/handler.ts:110-134]. A replayed register returns the bare 16 characters
  (section 7.2).
- **Storage record id and version.** `seismicmeta_guid` [C:2745-2747], route-chosen or minted (section 2.3). The
  version is known to Storage only (step 5).
- **`created_by`.** The caller id Seismic Store resolved (section 1.3); the route logs it as the identity the service
  saw.

## 6. Reads, verification and deletes

### 6.1 Read back

- `GET {base}/dataset/tenant/{T}/subproject/{SP}/dataset/{N}?path={P}&seismicmeta=true&translate-user-info=false`
  [C:189-269, dataset-get; v3 services/dataset/handler.ts:362-405] returns the dataset and, with `seismicmeta=true`,
  the Storage record; `record-version` selects a version [C:249-254]. The route compares `gcsurl`, `ltag`,
  `readonly`, `gtags`, `status`, `filemetadata`, `seismicmeta_guid` and `ctag` with the ledger. A missing record key
  does not prove the record is gone (section 2.3).
- A missing dataset is 404; the caller must first pass a viewer check on a uniform subproject, or a `users@<esd>`
  check otherwise, else 403 [v3 services/dataset/handler.ts:464-484].
- `translate-user-info` defaults to true [C:240-248] and converts `created_by` only when
  `FEATURE_FLAG_CCM_INTERACTION` is on [v3 services/dataset/handler.ts:437-447].
- Listing: `GET` or `POST {base}/utility/ls` with `sdpath`, `wmode` (`all`, `dirs`, `datasets`), `limit` and `cursor`
  [C:1196-1302, utility-ls-get and utility-ls-post; v3 services/utility/parser.ts:60-109];
  `POST {base}/dataset/tenant/{T}/subproject/{SP}` with `gtags`, `limit` and `cursor` [C:1026-1085, dataset-list-post;
  C:2583-2608, DatasetListBody], whose `search`, `select` and `filter` options are 501 except on azure and ibm
  [v3 services/dataset/parser.ts:191-217; v3 cloud/providers/azure/config.ts:166-167;
  v3 cloud/providers/ibm/config.ts:156-157]; `GET {base}/dataset/tenant/{T}/subproject/{SP}/readdsdirfulllist?path=`
  for datasets and directories [C:893-934, dataset-read-directory].
- `GET {base}/dataset/tenant/{T}/subproject/{SP}/dataset/{N}/permission?path={P}` returns `{read, write, delete}` for
  the caller [C:581-642, dataset-permission; v3 services/dataset/handler.ts:1386-1421].
- `POST {base}/dataset/tenant/{T}/subproject/{SP}/sizes` returns `filemetadata.size`, or -1, per dataset; it is
  deprecated [C:1141-1193, dataset-sizes].

### 6.2 Verification

- There is no server-side checksum, and the only server-computed size is Azure's (step 6). `size` and `md5Checksum`
  are what the client patched.
- Byte-level verification is provider-specific (Azure blob `Content-MD5`, GCS `crc32c`, S3 ETag, which is not an MD5
  for multipart uploads) and needs download credentials [C:1532-1613].
- `ctagcheck` detects a change made after the route's write (section 5).
- The Storage record is verified through Storage [S:1008-1097; S:1324-1420, getSpecificRecordVersion].

### 6.3 Dataset delete

`DELETE {base}/dataset/tenant/{T}/subproject/{SP}/dataset/{N}?path={P}` [C:270-325, dataset-delete;
v3 services/dataset/handler.ts:563-644]

- Dataset absent: 200 before any authorization check [v3 services/dataset/handler.ts:594-597]; the contract lists 404
  [C:324-325]. The service's e2e test deletes twice and expects 200 both times [395 app/sdms/tests/e2e/postman_collection.json,
  folder "idempotency", requests "IDM DATASET DELETE" and "IDM DATASET DELETE IDEMPOTENT CALL"].
- Otherwise, after the write authorization, and with `FALLBACK_DATASET_DELETE` off (the default)
  [v3 cloud/config.ts:282, 437]: `status` becomes `DELETE:<epoch ms>` and is saved, the objects are deleted and
  awaited, the catalogue entry is deleted, and any lock is removed [v3 services/dataset/handler.ts:622-642]. A failure
  part-way leaves the entry with its `DELETE:` status, and a retry continues. With the fallback on, the entry goes
  first and the object deletion is not awaited [605-621].
- Write locks are not checked [v3 services/dataset/handler.ts:569-577; v3 cloud/config.ts:206-212].
- The delete is not reversible: the objects are removed, and there is no soft delete for bulk data.
- The Storage record (`seismicmeta_guid`) is left in place. The route removes it, and the work-product-component,
  with `POST {storage}/records/{id}:delete`, a logical deletion that "can be reverted later" [S:509-584,
  deleteRecord], and never with `DELETE {storage}/records/{id}`, which "performs the physical deletion ... This
  operation cannot be undone" [S:1098-1167, purgeRecord].
- Object deletion per provider:
  - azure `uniform`: a flat listing of `<uuid>/`, deleted in batches of 256
    [v3 cloud/providers/azure/cloudstorage.ts:101-117, 130-153];
  - azure `dataset`: the dataset's container is deleted [v3 cloud/providers/azure/cloudstorage.ts:154-156];
  - anthos: every key that starts with `<folder>/<uuid>` [v3 cloud/providers/anthos/storage.ts:257-294];
  - ibm: the keys with prefix `<uuid>`; listing and deletion errors are only logged
    [v3 cloud/providers/ibm/cos.ts:163-195];
  - **gc, caution**: the handler passes the second `gcsurl` segment as the dataset folder
    [v3 services/dataset/handler.ts:631-633; v3 services/dataset/utils.ts:23-26], and on gc that segment is the
    subproject folder (section 4.2). `GCS.deleteObjects` deletes every object under `<folder>/`
    [v3 cloud/providers/gc/gcs.ts:152-165], which is what its unit test expects for that argument
    [395 app/sdms/tests/utest/cloud/gc/gcs.ts:110-133]. **(inference)** On a `gc` deployment one dataset delete
    removes the objects of every dataset in the subproject, and the `.keep` marker. The route does not call dataset
    delete on `gc` until this is checked on a disposable subproject.
- Cleanup is done when `GET` on the dataset answers 404 and Storage reports the record deleted.

### 6.4 Other delete paths (not used by the route)

- `PUT {base}/operation/bulk-delete?path=sd://{T}/{SP}/<path>/` answers 202 with `operation_id`; the status is
  `GET {base}/operation/bulk-delete/{operation-id}` with `data-partition-id` [C:2369-2446,
  operation-bulk-delete-push and operation-bulk-delete-get]. It needs `FEATURE_FLAG_ENABLE_BULK_DELETE` (code default
  false; 501 when off) and a task queue that only Azure registers [v3 services/operation/handler.ts:66-116, 178-182;
  v3 cloud/config.ts:171; v3 cloud/providers/azure/config.ts:218; v3 cloud/providers/azure/taskQueue.ts:24]. The
  Azure OSDU chart turns it on [395 app/sdms/devops/azure/seismic-ddms.osdu.values.yaml:71-72]. The Azure runner
  deletes the blobs and the metadata
  [395 app/sdms/src/cloud/providers/azure/sidecar/src/Sidecar.DeleteOperationRunner/README.md:1-16].
- `DELETE {base}/subproject/tenant/{T}/subproject/{SP}` deletes the metadata of every dataset, the subproject, its
  default groups and its storage [v3 services/subproject/handler.ts:225-261]. Operators only.
- v4 delete purges the Storage record (section 8). The route never calls it.

### 6.5 Probe, health and status

| Endpoint | Auth in the code | Response | Source |
| --- | --- | --- | --- |
| `GET {base}/svcstatus` | none (the contract declares bearer [C:63-64]) | `service OK` and `Service-Provider` | [C:60-75; v3 server/server.ts:151; v3 services/general/handler.ts:32-34] |
| `GET {base}/svcstatus/access` | `Authorization` present | `{"status": "running"}` | [C:78-94; v3 services/general/handler.ts:35-37] |
| `GET {base}/svcstatus/readiness` | none | `{"ready": true}`, or 503. Not in the contract. Azure checks that it can get a token for its application resource; the other providers always answer ready. | [v3 services/general/service.ts:37; v3 services/general/handler.ts:38-45; v3 cloud/providers/azure/seistore.ts:84-93; v3 cloud/providers/gc/seistore.ts:90; v3 cloud/providers/anthos/seistore.ts:62; v3 cloud/providers/ibm/seistore.ts:60; 395 app/sdms/devops/azure/seismic-ddms.osdu.values.yaml:48-52] |
| `GET {base}/info` | none (the contract declares bearer [C:100-101]) | `{"info": {group_id: "org.opengroup.osdu.sdms.v3", artifact_id: "sdms-v3", version, build_time, branch, commit_id, commit_message, connected_outer_services}}` | [C:97-111; C:2565-2582, Info; v3 services/info/handler.ts:26-44; v3 services/info/parser.ts:22-33; v3 cloud/config.ts:271-279] |
| `GET {base}/utility/storage-tiers` | `Authorization` present (the contract names the viewer role) | Azure `Hot`, `Cool`, `Cold`; gc `STANDARD`, `NEARLINE`, `COLDLINE`, `ARCHIVE`; 501 on the other providers | [C:1305-1325; v3 services/utility/handler.ts:564-567; v3 cloud/providers/azure/cloudstorage.ts:34, 194-196; v3 cloud/providers/gc/gcs.ts:218-220; v3 cloud/storage.ts:55-57] |
| `GET {base}/user/roles?sdpath=sd://{T}` | the contract names no role | the caller's role in each subproject | [C:2198-2225, user-roles] |
| `GET {base}/dataset/.../permission` | no role check; the dataset must exist (404 otherwise) | `{read, write, delete}` for the caller | [C:581-642; v3 services/dataset/handler.ts:1386-1421] |
| v4 `GET {v4 base}/status`, `/status/readiness`, `/info` | none | `{"status": "running"}`; `{"ready": true}` or 503; `{"info": {...}}` | [v4 apis/status/handler.ts:24-41; v4 apis/info/handler.ts:24-34; v4 server/server.ts:78-104] |

A route probe calls `svcstatus` (reachability and provider label), `svcstatus/access` (the `Authorization` header
gets through) and `subproject-get` (the tenant and the subproject exist, the route's identity is an admin, and the
subproject's legal tag is valid).

## 7. Limits and errors

### 7.1 Error bodies and codes

- v3 writes the status code and the message string [v3 shared/response.ts:31-54]. Its own messages start with
  `[seismic-store-service]` [v3 shared/error.ts:43-51]; errors relayed from core services keep that service's status
  code and carry a prefix such as `[storage-service]` or `[compliance-service]` [v3 shared/error.ts:53-70;
  v3 dataecosystem/storage.ts:52-54; v3 dataecosystem/compliance.ts:56-59]. The contract declares no error schema.
  **(inference)** Express sends a string body as text, not as JSON; the route reads error bodies as text.
- Status codes the code uses: 400, 401, 403, 404, 409, 423, 500, 501 and 503 [v3 shared/error.ts:30-40].
- A 423 message carries `[RCODE:<reason><ttl>]` [C:181-187; v3 shared/error.ts:80-105]. The reasons emitted are
  `WL` and `RL`, with the configured lock lifetimes 86400 and 3600 rather than the time left, and `CL` (mutex not
  acquired) with 6000, the mutex TTL in milliseconds [v3 services/dataset/locker.ts:30-44, 157-165, 221-227, 500-511].
  The contract also lists `UL`; the code defines `CU` for "cannot unlock", and no v3 file calls it.
- A relayed Storage error can replace a register's original error (section 2.3).

### 7.2 Locks, idempotency and retries

| Situation | What the service does | Source |
| --- | --- | --- |
| Register replayed with the same `x-seismic-dms-lockid` while its lock is held and the dataset is saved | 200 with the stored dataset: `sbit` is the key and `sbit_count` is 1, read from the current lock; `ctag` has no suffix; there is no `access_policy`; nothing is written. The service's e2e test expects this answer. | [v3 services/dataset/handler.ts:143-153, 222-225; v3 services/dataset/dao.ts:264-284; v3 services/dataset/locker.ts:216-219; 395 app/sdms/tests/e2e/postman_collection.json, folder "idempotency", request "IDM DATASET REGISTER IDEMPOTENT CALL"] |
| The same replay when the lock is held but the dataset was never saved (an earlier attempt failed and its lock survived) | **(inference)** 200 with the body `{}`: the handler returns nothing and the response writer defaults to an empty object; the mutex is left to expire. | [v3 services/dataset/handler.ts:143-153, 222-225; v3 shared/response.ts:27-29] |
| Register without a key, or with another key, while a lock exists | 423 `WL` (or `RL`) | [v3 services/dataset/locker.ts:210-227] |
| A 423 raised inside register | The handler tries to turn it into 500 "... an idempotent retry is required", but tests `err instanceof ErrorModel`, while `Error.make` returns a plain object. **(inference)** The caller receives the 423. | [v3 services/dataset/handler.ts:285-294; v3 shared/error.ts:20-26, 43-51] |
| Register after the dataset was closed | 409 | [v3 services/dataset/handler.ts:179-196] |
| Register failed after its lock was created | The lock and the mutex are removed in the error path, unless the rollback itself throws (section 2.3); `unlock` then clears the lock. | [v3 services/dataset/handler.ts:276-283, 1066-1071; v3 services/dataset/locker.ts:231-240] |
| Key that does not start with `W` | 400 | [v3 services/dataset/locker.ts:203-206] |
| Concurrent calls on one dataset | A Redis mutex (Redlock: 6 s TTL, 10 retries 200 ms apart plus up to 200 ms of jitter) serializes them; failing to get it is 423 `CL`. | [v3 services/dataset/locker.ts:30, 128-143, 500-511] |
| Close replayed after success | No lock is left, the unlock is a no-op, and the patch is applied again; the e2e test expects 200 with `sbit_count` 0. | [v3 services/dataset/locker.ts:471-477; 395 app/sdms/tests/e2e/postman_collection.json, folder "idempotency", request "IDM DATASET PATCH IDEMPOTENT CALL"] |
| Close with another id | 404 | [v3 services/dataset/locker.ts:396-402] |
| Write lock never closed | Expires after 24 hours; a read lock after 1 hour | [v3 services/dataset/locker.ts:31-33, 210-213, 352-363] |
| Delete replayed | 200 | [v3 services/dataset/handler.ts:594-597] |

Route policy (recommendation): always send `x-seismic-dms-lockid`, stored with the attempt before the call; treat a
200 register response without `name` and `gcsurl` as "not registered" and unlock before retrying; treat 409 as "look
up and compare with the ledger"; treat 423 as "locked by another writer or by an earlier attempt" and compare the
lock id with the ledger; after any failed register, read the dataset and call `unlock` before retrying; give every
Storage record a route-chosen id.

### 7.3 Limits

| Limit | Value | Source |
| --- | --- | --- |
| v3 JSON body | 50 MB, which bounds an inline record | [v3 server/server.ts:77] |
| v4 JSON body | the Express default, no explicit limit; **(inference)** 100 KB | [v4 server/server.ts:36] |
| v3 socket and keep-alive timeouts | 610 s; header timeout 611 s | [v3 server/server.ts:223-232] |
| Dataset size | none: storing a dataset as several objects is how the service avoids per-object limits | [395 README.md:3-7] |
| Azure container name | 63 characters | [v3 cloud/providers/azure/seistore.ts:49-50] |
| Dataset path characters | `[/A-Za-z0-9_.-]` | [v3 shared/params.ts:104-115] |
| Subproject name | `^[a-z][a-z\d\-]*[a-z\d]$` | [C:1800-1805] |
| Group name length at subproject creation | 256 on azure and anthos, 128 on gc | [v3 cloud/providers/azure/config.ts:53; v3 cloud/providers/anthos/config.ts:35; v3 cloud/providers/gc/config.ts:58; v3 services/subproject/handler.ts:405-416] |
| Caches | authorization 60 s; valid legal tag 1 h; tenant 1 h | [v3 auth/auth.ts:103-111; v3 dataecosystem/compliance.ts:27-30, 53; v3 services/tenant/dao.ts:57] |
| Locks | write 24 h; read 1 h; mutex 6 s | [v3 services/dataset/locker.ts:30-33] |
| List paging | `limit` must not be negative, except -1; an empty `cursor` is 400 | [v3 services/dataset/parser.ts:182-189; v3 services/utility/parser.ts:95-99] |

## 8. The v4 service

### 8.1 Scope

- A separate service in the same repository, "Seismic Data Management Service V4", for the data types the OSDU data
  definitions define, with strong type checks [395 app/sdms-v4/README.md:1-3; 395 README.md:11-14].
- **Azure only.** Its provider index exports only `azure` [v4 cloud/providers/index.ts:17]; its README says "The AWS
  provider has been removed from this repository" [395 app/sdms-v4/README.md:41-43]; the v3 and v4 synchronization
  service "is currently built exclusively for Azure" [395 app/sdms-v4/microservices/sdms-sync-v3v4/README.md:55].

### 8.2 Headers

The request gate requires the literal `data-partition-id` header (400) and `Authorization` (401) on every route except
those ending in `status`, `readiness` or `info` [v4 server/server.ts:68-104]. The handlers read the partition from
the header named by `DATA_PARTITION_HEADER_KEY`, default `data-partition-id` [v4 cloud/config.ts:117;
v4 apis/schema/handler.ts:31]. The correlation header is `correlation-id` by default [v4 cloud/config.ts:116;
v4 server/server.ts:72-76].

### 8.3 Endpoints

Each endpoint name `X` is bound to one kind [v4 apis/schema/types.ts:26-128; v4 apis/index.ts:26-32;
v4 apis/schema/service.ts:24-52]. Bulk types: `segy` (`osdu:wks:dataset--FileCollection.SEGY:1.0.0`), `openzgy`
(`...FileCollection.Slb.OpenZGY:1.0.0`), `openvds` (`...FileCollection.Bluware.OpenVDS:1.0.0`), `generic`
(`...FileCollection.Generic:1.0.0`). Record types: `2dinterpretationset` and `3dinterpretationset`
(`master-data--Seismic2DInterpretationSet:1.1.0`, `...Seismic3DInterpretationSet:1.1.0`), `acquisitionsurvey`
(`master-data--SeismicAcquisitionSurvey:1.2.0`), `processingproject` (`master-data--SeismicProcessingProject:1.2.0`),
`bingrid` (`work-product-component--SeismicBinGrid:1.0.0`), `linegeometry` (`...SeismicLineGeometry:1.0.0`),
`horizon` (`...SeismicHorizon:1.2.0`), `tracedata` (`work-product-component--SeismicTraceData:1.3.0`),
`notionalseismicline` (`...NotionalSeismicLine:1.1.0`) and `fault` (`...SeismicFault:1.2.0`).

| Call | Behaviour | Source |
| --- | --- | --- |
| `PUT {v4 base}/{X}/v1`, body an array of records | Each record is validated with `jsonschema` against the Schema service's definition of its own `kind` (`GET {schema}/schema/{kind}`, cached for 24 hours), with format checks skipped unless `ENABLE_SCHEMA_PROPERTIES_FORMAT_VALIDATION` is on; the record's kind label and major version must equal the endpoint's. The records go to `PUT {storage}/records` without `skipdupes`, and the response is Storage's `recordIdVersions` (`<id>:<version>`). For bulk types, a container named by the SHA-256 hex digest of the record id, without its last character, is created when missing. With `FEATURE_FLAG_OPERATIONS_SYNC_V3_V4` (default false) a message is queued that creates a v3 catalogue entry in subproject `syncv4`. An update is the same PUT; each call makes a new version. | [v4 apis/schema/handler.ts:57-93; v4 apis/schema/parser.ts:23-41, 68-83; v4 shared/schema.ts:39-60; v4 services/schema.ts:24-61; v4 services/storage.ts:23-50; v4 shared/utils.ts:75-77; v4 cloud/config.ts:91, 127-130, 136-139, 168; v4 jobs/jobs.ts:24-35; v4 jobs/parser.ts:23-52] |
| `GET {v4 base}/{X}/v1/record/{id}` | Storage read; the id must match the endpoint's id pattern, else 400 | [v4 apis/schema/handler.ts:101-104; v4 apis/schema/parser.ts:43-48, 85-96] |
| `GET {v4 base}/{X}/v1/record/{id}/versions`, `.../version/{v}` | Storage version list and versioned read | [v4 apis/schema/handler.ts:112-115, 144-147; v4 services/storage.ts:52-79, 100-118] |
| `GET {v4 base}/{X}/v1/list?page-limit=&next-page-token=` | Search `query_with_cursor` on the endpoint's kind as the Schema service resolves it (`x-osdu-schema-source`) | [v4 apis/schema/handler.ts:155-163; v4 apis/schema/parser.ts:58-66; v4 shared/schema.ts:31-37; v4 services/search.ts:23-56] |
| `DELETE {v4 base}/{X}/v1/record/{id}` | `DELETE {storage}/records/{id}` (a 404 is ignored), then, for bulk types, the container is deleted | [v4 apis/schema/handler.ts:122-136; v4 services/storage.ts:81-98] |
| `GET {v4 base}/connection-string/upload/record/{id}` and `.../download/record/{id}` | Bulk types only, else 400. The record is read first. When `data.DatasetProperties.FileCollectionPath` does not start with `sd://`, an upload request re-writes the record with `skipdupes=true` to prove ownership, and a SAS for the record's container is returned. When it starts with `sd://`, the v3 dataset is looked up in the v3 catalogue by subproject, path and name, and a SAS for its location is returned. The SAS is a user-delegation SAS with read and list always, write, create and delete for uploads, valid 3599 minutes, `token_type: SasUrl`. **(inference)** A bulk record without `data.DatasetProperties.FileCollectionPath` fails with 500. | [v4 apis/connection/service.ts:25-33; v4 apis/connection/handler.ts:26-122; v4 cloud/providers/azure/credentials.ts:31-33, 66-120; v4 cloud/providers/azure/database.ts:48-73] |
| `GET {v4 base}/status`, `/status/readiness`, `/info` | health | section 6.5 |

- Blob naming is the client's choice. The maintainers' v4 collection registers `FileCollectionPath: "/"` and
  `FileSourceInfos: [{"FileSource": "data.segy"}]`, takes the SAS, rewrites `?` to `/o?`, and PUTs a `BlockBlob`
  (201) [395 app/sdms-v4/tests/postman/azure/Seismic DMS V4.postman_collection.json, folder "File Collection /
  Segy"]. The collection also sends a `schema-format-validation` header that no v4 source file reads. The generated
  documentation lists files as relative `FileSourceInfos[].FileSource` names [v4 docs/parser.ts:72-94].
- Documented roles: register `users.datalake.editors` or `users.datalake.admin` [v4 docs/parser.ts:39-41, 52-56];
  reads `users.datalake.viewers`, editors or admin [v4 docs/parser.ts:168, 192, 236, 252]; delete
  `users.datalake.admin` [v4 docs/parser.ts:204-214]; upload connection string `dataset.owner`, download
  `dataset.viewer` or `dataset.owner` [v4 docs/index.ts:102-124].

### 8.4 Differences from v3 that matter

- v4 has no tenant, subproject, `sd://` path, lock, close, `filemetadata`, gtags or size computation: the Storage
  record is the dataset, its container is derived from its id, and access follows the record's `acl`.
- v4 register returns the record versions and validates against the live Schema service; v3 validates against pinned
  1.0.0 copies, or not at all for `seismicmeta`.
- **v4 delete purges** the Storage record (`DELETE /records/{id}` [S:1098-1167, purgeRecord]) and deletes the
  container. That conflicts with this repository's rule to delete only through `POST /records/{id}:delete`, so the
  route never calls it. A reversible cleanup of a v4 dataset is `POST /records/{id}:delete` [S:509-584], which leaves
  the container in place.
- v4 requires `data-partition-id` on every data call (section 8.2).

### 8.5 Which version the route uses

**v3**, as the portable baseline: it is the contract pinned for this project, it is deployed by every provider
(azure, gc, anthos, ibm), and both in-tree client libraries speak it; sdfs describes itself as "based on Seismic DMS
API v3" [1552 docs/csp_specifics.md:17]. v4 can be an optional Azure-only mode, behind configuration, for register
and connection strings, using the `sd://` linkage of section 2.3. Its delete stays unused.

## 9. Route design summary (recommendation)

### 9.1 What the route implements on v3

1. **Resolve and probe**: base URL with its version path from configuration; `GET svcstatus` for reachability and the
   `Service-Provider` label; `GET svcstatus/access`; `GET subproject` for `access_policy`, the default `ltag`, the
   admin check and a valid subproject legal tag.
2. **Map** a rendered record to `sd://{T}/{SP}/{P}/{N}`, validated against the path and subproject patterns, and to a
   complete `dataset--FileCollection.<format>` record with a route-chosen `id`, `acl`, `legal` (with
   `otherRelevantDataCountries` given explicitly), `data.DatasetProperties.FileCollectionPath = sd://{T}/{SP}/{P}/`
   and `FileSourceInfos`.
3. **Register** with `ltag` and a stored `x-seismic-dms-lockid`, body `{type, gtags, acls (dataset policy only),
   seismicmeta}`; the ledger keeps `sbit`, `gcsurl`, `seismicmeta_guid`, `ctag` and `created_by`. Replays, 409, 423
   and failed registers follow section 7.2.
4. **Get upload credentials** from `upload-connection-string` with the dataset `sdpath`; refresh before expiry and on
   a 403 from the object store.
5. **Upload** through a provider adapter, recording each object's key, size and checksum in the ledger.
6. **Finalize** with `PATCH ...&close={W}` and `filemetadata {type: GENERIC, size, nobjects, md5Checksum}`, plus
   `readonly` and `gtags` as mapped.
7. **Write the work-product-component** (`work-product-component--SeismicTraceData`) and any other record through
   Storage, referencing `seismicmeta_guid`; the ledger keeps the ids and the versions read from Storage.
8. **Verify**: `GET dataset?seismicmeta=true`, compared with the ledger; `ctagcheck` on later runs; an optional byte
   check with download credentials; the Azure size when available.
9. **Delete and redeliver**: `DELETE dataset` (irreversible for the bytes; not on `gc` until checked), then
   `POST /records/{id}:delete` for the dataset record and the work-product-component; confirm 404. On live runs every
   created id (dataset `sdpath`, record ids, lock id) is logged in `.sqlflow/live-e2e/actions.log`.

### 9.2 Cloud-specific pieces

| Concern | azure | gc | anthos (CIMPL) | ibm |
| --- | --- | --- | --- | --- |
| Credential format | SAS URL signed for the container | downscoped Google bearer token | `key:secret:session` STS triple | STS triple |
| Extra client configuration | none | none | S3 endpoint (not returned by the service) | COS endpoint and region |
| Object layout the readers use | blobs `0` to `N-1` under `<uuid>/` (uniform) or at the container root (dataset policy) | `<subproject uuid>/<dataset uuid>/0` | `<folder>/<uuid>/0` | `<uuid>/0` |
| Integrity signal | blob `Content-MD5` (running MD5) | object `crc32c` | MD5 of the local file only | none in sdutil |
| `expires_in` | 3599, minutes | **(inference)** milliseconds left | 3599, seconds (STS session 3600 s) | `COS_TEMP_CRED_EXPIRY` seconds |
| `gcsurl` parse | `container/uuid`, or a dedicated container | `bucket/subproject/dataset` | `bucket$$folder/uuid` | `bucket/uuid` |
| Size computation, subproject size, bulk delete | yes (bulk delete when the flag is on, as in the Azure chart) | no | no | no |
| Tier list, tier change | yes, yes | list only | no | no |
| List `search`, `select`, `filter` | yes | no | no | yes |
| Dataset delete scope | the dataset folder or container | **(inference)** the whole subproject folder (section 6.3) | the dataset prefix | the dataset prefix; errors only logged |
| v4 available | yes | no | no | no |

### 9.3 Configuration the route needs

| Setting | Notes |
| --- | --- |
| Service base URL with version path | for example `https://<host>/seistore-svc/api/v3` or `https://<host>/api/seismic-store/v3` (section 1.2); never derived |
| API mode | `v3` by default, or `v4` (Azure only; register and connection strings only) |
| Provider label | read from `Service-Provider`, with a configured override; selects the upload adapter |
| Tenant | equals the data-partition-id on OSDU deployments; must already be registered |
| Data partition id | sent as `data-partition-id` (required by v4, ignored by v3 dataset calls, required by Storage) |
| Subproject | pre-provisioned; its `access_policy` decides whether dataset `acls` are allowed |
| Dataset folder path and naming rule | maps a record to `{P}` and `{N}`; characters `[/A-Za-z0-9_.-]` |
| Legal tag | the `ltag` header for the dataset and `legal.legaltags` in the record, with `otherRelevantDataCountries` given explicitly so the `US` default never applies |
| Record ACLs | `acl.owners` and `acl.viewers` group emails for the Storage records (the defaults are `data.default.*`) |
| Dataset ACLs | `acls.admins` and `acls.viewers`, only on `dataset`-policy subprojects |
| Record kind and version per format | `osdu:wks:dataset--FileCollection.SEGY:<version>`, `...Slb.OpenZGY`, `...Bluware.OpenVDS`, `...Generic`; always sent as `seismicmeta` |
| Upload tuning | chunk size (sdutil uses 32 MiB on Azure), concurrency, S3 part size, storage tier (Azure) |
| `readonly`, `gtags`, `type` | per mapping |
| Credential references | token acquisition that yields a JWT, as `${env:NAME}` or `${keyvault:NAME}`; the optional `appkey` header name and value, as references |
| S3 or COS endpoint | anthos and ibm only, as configuration |

## 10. What differs between the contract and the code

| Topic | Contract | Code |
| --- | --- | --- |
| `x-seismic-dms-lockid` | absent | idempotency key of register, lock and compute size (section 1.3) |
| Operations | `GET /svcstatus/readiness`, `DELETE /tenant/{tenantid}`, the `/analytics` routes and the legacy `GET /dataset/tenant/{tenantid}/subproject/{subprojectid}` are absent; the last one is commented out [C:937-1025] | all four are routed [v3 services/general/service.ts:37; v3 services/tenant/service.ts:42; v3 services/analytics/service.ts:25-70; v3 services/dataset/service.ts:39-44] |
| `svcstatus`, `info` | bearer security [C:63-64; C:100-101] | no token needed [v3 server/server.ts:148-161] |
| `svcstatus/access` | "Validates if the token audience is allowed", 401 and 403 [C:78-94] | answers `{"status": "running"}` whenever an `Authorization` header is present [v3 services/general/handler.ts:35-37]; `ENABLE_SDMS_ID_AUDIENCE_CHECK` is set from configuration [v3 cloud/config.ts:223, 372; v3 cloud/providers/azure/config.ts:198-199] and read by no check anywhere in the v3 source |
| Dataset delete of a missing dataset | 404 listed [C:324-325] | 200, no authorization check [v3 services/dataset/handler.ts:594-597] |
| Patch `gtags` | "new gtags are appended to this list" [C:342] | replaces the list when non-empty [v3 services/dataset/handler.ts:718]; only `PUT .../gtags` appends |
| Patch `change_tier` | in the description [C:341], not in `DatasetPatch` [C:2805-2898] | read from the body [v3 services/dataset/parser.ts:243-245] |
| Patch `last_modified_date` | a body field [C:2819-2821] | overwritten with the server time [v3 services/dataset/parser.ts:261-262] |
| `Dataset` fields | no `type`, `gtags`, `acls`, `access_policy`, `computed_size`, `computed_size_date`, `transfer_status` [C:2690-2804] | present in the model and in responses (section 2.2) |
| Record keys | three optional properties [C:2642-2654] | at most one; 400 otherwise (section 2.3) |
| `acls` on register | allowed [C:2655-2666] | 400 on a uniform subproject [v3 services/dataset/handler.ts:213-216] |
| `segy_v1`, `openzgy_v1` schemas | `$ref` to `../schemas/...` files [C:2651-2654; C:2763-2766; C:2847-2850] that are not pinned in `osdu/specs` | the copies in `395 app/sdms/docs/schemas/`; `openzgy_v1` accepts 1.0.0 kinds only (section 2.3) |
| 423 reasons | `WL`, `RL`, `CL`, `UL`; TTL in seconds [C:181-187] | `WL`, `RL`, `CL` emitted; `CU` defined and unused; the `CL` TTL is 6000 milliseconds; the `WL` and `RL` TTLs are the configured lifetimes (section 7.1) |
| `expires_in` | minutes [C:2982-2984] | minutes on Azure, seconds on anthos and ibm, **(inference)** milliseconds on gc (section 4.3) |
| Upload connection string `sdpath` | subproject path for uniform, dataset path for dataset policy [C:1508-1514] | either form under either policy [v3 services/utility/handler.ts:100-125] |
| `utility/storage-tiers` roles | subproject admin or viewer [C:1310] | no role check; 501 on anthos and ibm (section 6.5) |
| `ctagcheck` roles | subproject admin or viewer [C:647] | no role check [v3 services/dataset/handler.ts:110-134] |
| `subproject-delete` role | `subproject.admin` [C:1881] | tenant admin [v3 services/subproject/handler.ts:225-235] |
| `TenantCreateBody.default_acls` | required [C:3222-3245] | optional, defaulted from the datalake admin and ops groups [v3 services/tenant/parser.ts:39-41] |
| Error bodies | no schema | the message string (section 7.1) |
| `utility/ls` status | 200, and 201 "for documentation purposes" when paginated [C:1245-1250] | always 200 [v3 services/utility/handler.ts:45-47] |
| Idempotent register replay | not described | `{}` when the lock exists without a dataset, **(inference)** (section 7.2) |

## 11. What is still open

- **gc dataset delete scope.** Code reading says one dataset delete removes every object in the subproject folder
  (section 6.3). To be checked on a disposable subproject before the route calls dataset delete on `gc`.
- **Bulk-data deletion semantics.** Seismic Store offers no reversible dataset delete, while this repository's
  cleanup rule prefers reversible deletes; the route's delete and redeliver scopes for bulk data need a decision.
- **423 inside register.** Code reading says the caller receives 423, not the 500 the handler intends
  (section 7.2); to be confirmed live.
- **Replayed register without a saved dataset.** Code reading says 200 with `{}` (section 7.2); to be confirmed live.
- **Register rollback.** The failing Storage delete in the rollback, the replaced error, and the lock left behind
  (section 2.3) are code reading; to be confirmed live, including the Storage status the date-shaped id produces.
- **`expires_in` on gc**, read as milliseconds from the auth library; and the real Azure SAS lifetime when the cached
  delegation key is near its own expiry (section 4.3).
- **Transformed `segy_v1` and `openzgy_v1` records.** Whether Storage accepts the extra top-level
  `data-transformation-performed` property on patch is not stated by the Storage contract [S:1709-1779, Record]; the
  route avoids the question by sending records as `seismicmeta`.
- **`FEATURE_FLAG_SEISMICMETA_STORAGE`.** When a deployment turns it off, a `seismicmeta` record is silently not
  written (section 2.3); the route verifies the record in Storage after register.
- **Express behaviour.** Text error bodies and the v4 100 KB body limit come from Express defaults, not from the cited
  files.
- **Contract tests.** The pinned contract's `segy_v1` and `openzgy_v1` properties reference files that are not in
  `osdu/specs` (section 10); a test that resolves every `$ref` needs those schema files or has to treat the two
  properties as opaque objects.
- **v4 on the target deployment.** Whether it is deployed, its body limit, and the v4 base URL are deployment facts
  to be read from the environment.
