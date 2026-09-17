# Well Delivery DDMS: integration brief

The Well Delivery DDMS stores well planning and drilling entities (wells, wellbores, well activity programs, activity
plans, hole sections, BHA runs, trajectories, operations and fluids reports and related kinds) in a store of its own,
indexes the references between them for its domain queries, and can mirror each entity into Storage. OSDU Delivery
reaches it through the DDMS record route (`docs/osdu-coverage-plan.md`, stages 5 and 7). This brief is what that route
type is built from: every request the route sends, with its contract, and the service behaviour behind each request as
the pinned contract and the service's source show it.

## Sources

| Key | Source | Marking |
| --- | --- | --- |
| `C` | `osdu/specs/well-delivery-ddms/swagger.yaml`: Swagger 2.0, 2375 lines, 78425 bytes, 38 operations. It is `docs/api/swagger.yaml` of project 482 at the commit below (`osdu/specs/sources.json`), byte for byte. | Contract. Cited as `[C: METHOD path, operationId]`, `[C: definitions.Name]` or `[C: line n]`. |
| `S` | `osdu/specs/core/storage/openapi.yaml`, the Storage contract of the core specification set (repository copy under `osdu/specs/core`). | Contract of the Storage copy. `[S: METHOD path, operationId]`. |
| `SC`, `LG`, `EN` | `osdu/specs/core/schema_service/openapi.yaml`, `osdu/specs/core/legal/openapi.yaml`, `osdu/specs/core/entitlements/openapi.yaml` (core specification set). | Contracts of the core calls the DDMS makes on the writer's behalf. |
| `482` | GitLab project 482, `osdu/platform/domain-data-mgmt-services/well-delivery/well-delivery`, branch `master`, commit `a508b4e946424de9fbb3110801517f3ae9d36b18` (committed 2026-09-04; the head of `master` on 2026-09-16 and on 2026-09-17; the commit `C` was taken from). All 392 text files were fetched and each matches the tree of that commit; the 9 binary files (Maven wrapper jars, a meeting recording, slide decks) were not. | Source. `[482 path:lines]`. Path prefixes: `core/` = `wd-core/src/main/java/org/opengroup/osdu/wd/core/`; `azure/`, `gc/`, `aws/`, `ibm/` = `provider/wd-azure/src/main/java/org/opengroup/osdu/wd/azure/`, `provider/wd-gc/src/main/java/org/opengroup/osdu/wd/gcp/`, `provider/wd-aws/src/main/java/org/opengroup/osdu/wd/aws/`, `provider/wd-ibm/src/main/java/org/opengroup/osdu/wd/ibm/`; `it/` = `testing/wd-test-core/src/main/java/org/opengroup/osdu/wd/test/core/`; `<provider>.properties` = `provider/wd-<provider>/src/main/resources/application.properties`; `Postman` = `Postman/WellDelivery_DDMS_CI-CD_v3.1.1.postman_collection.json`; `examples/` = `examples/ddp-blueberry/`. |
| `67` | GitLab project 67, `osdu/platform/system/lib/core/os-core-common`, tag `v3.0.0`, commit `98aff641a4ef75724251f3068c5623a50e7a71c5`. The DDMS depends on version 3.0.0 (`pom.xml:108` of 482), and the `pom.xml` at this commit declares 3.0.0. Read: `AppException`, `AppError`, `DpsHeaders`, `DpsHeaderFactory`, `Groups`, `StorageService`, `LegalService`, `EntitlementsService`. | Source. `[67 path:lines]`, prefix `occ/` = `src/main/java/org/opengroup/osdu/core/common/`. |
| (inference) | A statement that follows from the code plus the documented behaviour of a framework or library not read here (Spring Boot 3.2.6, Jackson, Gson 2.9.1, the MongoDB and Azure Cosmos drivers, Cloudant, Envoy, Istio), or that was not observed against a live service. | Marked `(inference)` where it appears. |

Nothing in this brief was observed against a live Well Delivery deployment.

## 1. Base path, versions, headers and auth

### Base path and versions

| Item | Value | Basis |
| --- | --- | --- |
| Base path | `/api/well-delivery`. Below, `{base}` is `https://<host>/api/well-delivery`. | `[C: line 7]` (`host` is the placeholder `dns-host`, line 6); `server.servlet.contextPath=/api/well-delivery/` `[482 <provider>.properties:1]` for all four providers; ingress prefix `/api/well-delivery` `[482 devops/gc/deploy/templates/virtual-service.yaml:31-33]`, `[482 devops/ibm/ibm-well-delivery-deploy/templates/istio-virtualservice.yaml:19-21]`, `[482 devops/azure/well-delivery.values.yaml:35]` |
| Scheme | `https` | `[C: lines 31-32]` |
| Media type | `application/json` in and out | `[C: lines 33-36]`, and `consumes`/`produces` on every operation |
| API version | `1.0.0`. Every resource family carries its own `v1` segment (`storage/v1`, `wells/v1`, `query/v1`, ...). | `[C: info.version]`; `[482 wd-core/src/main/resources/swagger.properties:14]` |
| Service build | `0.29.0-SNAPSHOT`, Spring Boot 3.2.6, os-core-common 3.0.0 | `[482 pom.xml:22, 90, 108]` |
| Title | "Well Delivery Service APIs", description "Entity service" | `[C: info]` |
| Generated OpenAPI | Swagger UI at `{base}/swagger`, JSON at `{base}/api-docs` | `[482 wd-core/src/main/resources/swagger.properties:7-9]`; `[482 README.md:26-28]` |

Transport rules in the request filter `[482 core/auth/EntityFilter.java:50-100]`:

- A request whose URL contains `/swagger`, `/api-docs`, `/configuration/ui`, `/webjars/` or `/info`, or ends in `/warmup`, skips the next two checks (lines 81-85, 111-117).
- No or empty `Authorization`: `401` with an empty body before any controller runs (lines 87-90).
- Plain HTTP (URL not `https`, `x-forwarded-proto` not `https`, not localhost, `ACCEPT_HTTP` not `true`): `307` with a `location` header that repeats the URL with `https` (lines 92-97, 132-134). `ACCEPT_HTTP=true` is set for Google Cloud `[482 gc.properties:40]`.
- A request that passes both checks and has method `OPTIONS` is answered `200` without reaching a controller (lines 65-66).
- Trailing slashes are accepted `[482 core/config/WebConfiguration.java:25]`, exercised by `[482 it/entity/EntityTest.java:61-75]`.

### Headers

| Header | Rule | Basis |
| --- | --- | --- |
| `Authorization: Bearer <token>` | Required. The contract declares an `apiKey` scheme named `Bearer` carried in `Authorization` and applies it to every operation; the service's generated OpenAPI declares an HTTP bearer scheme. The filter only checks that the header is non-empty; the token is judged by Entitlements (below). | `[C: lines 2178-2182]`; `[482 core/swagger/SwaggerConfiguration.java:32-39]`; `[482 core/auth/EntityFilter.java:102-105]` |
| `data-partition-id` | Required on every operation (header parameter, `required: true`, default `opendes`). The service does not check it itself (no controller declares it); it reads it from the request's `DpsHeaders` to choose the partition's store on Mongo and Cosmos (section 6, shared-instance hazards) and to rewrite the namespace of the Storage copy's id (section 4). | `[C: every operation]`; `[482 core/swagger/SwaggerConfiguration.java:80-91]`; `[482 core/dataaccess/impl/MongodbInit.java:74-78]`; `[482 azure/cosmosdb/CosmosdbInit.java:66-70]`; `[482 core/util/RecordConversion.java:63-71]` |
| `Content-Type: application/json` | Required on `PUT /storage/v1/{type}`, `POST /query/v1/by_well/{type}:batch`, `DELETE /storage/v1/{type}/{id}`, `DELETE /storage/v1/{type}/{id}:purge` and `DELETE /storage/v1/{type}/{id}/{version}`, which all declare `consumes = application/json`; not on `DELETE /storage/v1/{type}/{id}/{version}:purge`. A request to those routes without the JSON content type is not routed to the handler; Spring answers `415` (inference). The contract lists the header as required only on the batch query. The upstream integration client sends it on every call. | `[482 core/api/EntityApi.java:67, 168, 193, 218, 244]`; `[482 core/api/QueryApi.java:73]`; `[C: POST /query/v1/by_well/{type}:batch, Get a list of entity objects for a list of wells]` (lines 1680-1686); `[482 it/util/TestUtils.java:104-122]` |
| `correlation-id` | Optional. The service adds one when it is missing. The headers object os-core-common builds for each request keeps `authorization`, `data-partition-id`, `correlation-id`, a few other named headers and every `x-*` header, and the core clients send it on, so sending the ledger attempt's id as `correlation-id` ties the DDMS and core service logs to that attempt. Every provider's component scan includes that request-scoped factory. | `[482 core/auth/EntityFilter.java:54]`; `[67 occ/http/DpsHeaderFactory.java:29-45]`; `[67 occ/model/http/DpsHeaders.java:48-63, 99-106, 177-181]`; `[482 azure/AzureApplication.java:23-26]` and the application class of each other provider |

The DDMS sends the writer's request headers, including its `Authorization`, on the core calls it makes (`DpsHeaders.getHeaders()` in each client: `[482 core/osducoreserviceclient/schema/SchemaService.java:51-52]`, `[67 occ/storage/StorageService.java:60-66]`, `[67 occ/legal/LegalService.java:86-100]`, `[67 occ/entitlements/EntitlementsService.java:73-80]`). The delivery identity therefore needs the Entitlements, Legal, Schema and Storage rights these calls require, not only the DDMS roles.

The service reads no application key from the caller. Its Schema client is built without one, so the `AppKey` branch of `SchemaService` is never taken; its Entitlements client is configured with `app.entitlements.api.key` `[482 core/osducoreserviceclient/schema/SchemaClientFactory.java:41-47]`, `[482 core/osducoreserviceclient/schema/SchemaService.java:43-45]`, `[482 core/osducoreserviceclient/entitlements/EntitlementsClientFactory.java:35-36, 47]`. The upstream Postman collection declares an `appkey` header that is empty on every request but one; that request carries a literal bearer token and a literal `appkey` value, and neither may be reused `[482 Postman, folder UC3, query_wellboreTrajectory_by_well]`.

### Authentication and authorisation

- Spring Security permits every request on Azure with Istio authentication (the default), Google Cloud, AWS and IBM `[482 azure/security/AzureIstioSecurityConfig.java:31, 40]`, `[482 gc/security/GcpSecurityConfig.java:45]`, `[482 aws/security/AwsSecurityConfig.java:44]`, `[482 ibm/security/SecurityConfig.java:25]`. With `azure.istio.auth.enabled=false`, `AADSecurityConfig` applies instead `[482 azure/security/AADSecurityConfig.java:31]`.
- The real check is `@PreAuthorize("@authorizationFilter.hasRole(...)")` on each controller method `[482 core/auth/AuthorizationFilter.java:34-38]`. It loads the caller's groups with Entitlements `GET /groups` `[EN: GET /groups, listGroups]`, `[67 occ/entitlements/EntitlementsService.java:73-80]`, and requires one of the listed role groups by exact name `[67 occ/model/entitlements/Groups.java:37-51]`. Otherwise: `403` "The user is not authorized to perform this action" `[482 core/osducoreserviceclient/entitlements/EntitlementsAndCacheClient.java:48-59]`. When the Entitlements call itself fails, the DDMS answers with Entitlements' status and the same message (same file, lines 133-140).
- Groups are cached in-process under a key built from the partition and the token (same file, lines 126-150), except on Google Cloud, where the group cache is a no-op `[482 gc/cache/GcGroupCache.java:24-44]`.
- Role groups: `service.storage.viewer`, `service.storage.creator`, `service.storage.admin` `[482 core/auth/EntityRole.java:17-21]`.
- On Azure, the service's chart adds an Istio `AuthorizationPolicy` that denies requests without a request principal except for swagger and health paths `[482 devops/azure/chart/templates/azure-istio-auth-policy.yaml:20-40]`. `/info` and `/_ah/warmup` are not in its exemptions; the values file for the shared Azure chart lists both under `auth.disable` `[482 devops/azure/well-delivery.values.yaml:43-48]`.

| Operation | Roles in the code | Roles in the contract |
| --- | --- | --- |
| `PUT /storage/v1/{type}` | creator, admin | creator, admin |
| Every `GET`, and `POST /query/v1/by_well/{type}:batch` | viewer, creator, admin | viewer, creator, admin |
| `DELETE /storage/v1/{type}/{id}`, `DELETE /storage/v1/{type}/{id}/{version}` | creator, admin | creator, admin |
| `DELETE /storage/v1/{type}/{id}:purge` | admin only | creator, admin |
| `DELETE /storage/v1/{type}/{id}/{version}:purge` (not in `C`) | admin only | not in the contract |

Basis: `[482 core/api/EntityApi.java:68, 93, 118, 144, 169, 194, 219, 245]`, `[482 core/api/QueryApi.java:74, 107, 152]`; the "Required roles" sentence of each operation's description in `C` (for purge, lines 424-426).

Record-level read check: the entity `GET`s and most domain queries also require the caller to belong to one of the record's owner or viewer groups, compared by the group name before `@`. A single entity fails with `403` "The user does not have access to the entity"; a list fails as a whole with `403` "The user does not have access to the entities" when any item is inaccessible `[482 core/services/EntityValidateService.java:86-108]`, `[482 core/osducoreserviceclient/entitlements/EntitlementsAndCacheClient.java:113-124]`. The version lists, the reference trees and `full_content` do not run this check `[482 core/services/EntityStorageService.java:260-267]`, `[482 core/services/WellActivityProgramQueryService.java:50-76]`, `[482 core/services/OperationsReportService.java:58-63]`, `[482 core/services/WellQueryService.java:35-40]`, `[482 core/services/WellboreQueryService.java:35-40]`. Azure and IBM return `acl.viewers` equal to `acl.owners` (section 5), so there the check passes only for members of an owners group (inference).

### Error shape

Errors raised by the service's own code have the body `{"message": "..."}` `[482 core/errors/AppError.java:24-26]`, `[C: definitions.AppError]`. The mapping `[482 core/errors/GlobalExceptionMapper.java:38-98]`, `[482 core/errors/GlobalOtherExceptionMapper.java:32-37]`:

- An `AppException` keeps its status; the body's `message` is the exception's message argument with CR and LF replaced by `_`, not its reason `[67 occ/model/http/AppException.java:66-79]`.
- `jakarta.validation.ValidationException`: `400` with the validation message.
- `java.nio.file.AccessDeniedException` (not Spring Security's) and `IllegalStateException`: `403`.
- Unsupported method: `405` "Method not found.".
- Any other exception: `500` "An unknown error has occurred.".
- The filter's `401` and `307` have no body.
- The mapper extends Spring's `ResponseEntityExceptionHandler`, so framework rejections other than an unsupported method (unsupported media type, unreadable body, a non-numeric `{version}`) get Spring's default status and body, not `{"message"}` (inference).

## 2. The calls a writer makes

| Purpose | Request | Contract | Code |
| --- | --- | --- | --- |
| Write one entity (create or new version) | `PUT {base}/storage/v1/{type}`, JSON body | `[C: PUT /storage/v1/{type}, Create or update entity]`: body `Entity`, `201` `EntityReturn`, `400`, `403` | `[482 core/api/EntityApi.java:67-74]`, `[482 core/services/EntityStorageService.java:73-180]` |
| Read a specific version | `GET {base}/storage/v1/{type}/{entityId}/{version}` | `[C: GET /storage/v1/{type}/{id}/{version}, Get a specific version of the given entity]`: `version` is an int64 path parameter; `200` `EntityReturn`, `400`, `403`, `404` | `[482 core/api/EntityApi.java:117-125]` |
| Read the latest version | `GET {base}/storage/v1/{type}/{entityId}` | `[C: GET /storage/v1/{type}/{id}, Get latest versio of entity]`: `200`, `400`, `403`, `404` | `[482 core/api/EntityApi.java:92-99]` |
| List versions | `GET {base}/storage/v1/{type}/versions/{entityId}` | `[C: GET /storage/v1/{type}/versions/{id}, Get all entity version  numbers]`: `200` `VersionNumbers` | `[482 core/api/EntityApi.java:143-150]` |
| Soft-delete every version | `DELETE {base}/storage/v1/{type}/{entityId}` | `[C: DELETE /storage/v1/{type}/{id}, Delete entity]`: `204`, `400`, `403`, `404` | `[482 core/api/EntityApi.java:168-175]` |
| Soft-delete one version | `DELETE {base}/storage/v1/{type}/{entityId}/{version}` | `[C: DELETE /storage/v1/{type}/{id}/{version}, Delete a specific version of entity]` | `[482 core/api/EntityApi.java:218-226]` |
| Purge every version (admin) | `DELETE {base}/storage/v1/{type}/{entityId}:purge` | `[C: DELETE /storage/v1/{type}/{id}:purge, Purge entity]` | `[482 core/api/EntityApi.java:193-200]` |
| Purge one version (admin) | `DELETE {base}/storage/v1/{type}/{entityId}/{version}:purge` | not in `C` | `[482 core/api/EntityApi.java:244-252]` |
| Prove the references were indexed | the domain query the record should appear in (section 4, type table), for example `GET {base}/holeSections/v1/by_wellbore/{wellboreEntityId}` | `[C: GET /holeSections/v1/by_wellbore/{wellbore_id}, Get a list of hole section objects for a wellbore]` and the other query operations | `[482 core/api/*Api.java]` |
| Read the Storage copy | `GET /api/storage/v2/records/{storageId}` and `GET /api/storage/v2/records/versions/{storageId}` | `[S: GET /records/{id}, getLatestRecordVersion]`, `[S: GET /records/versions/{id}, getRecordVersions]` | not called by the DDMS |
| Remove the Storage copy (reversible) | `POST /api/storage/v2/records/{storageId}:delete` | `[S: POST /records/{id}:delete, deleteRecord]` (owner, creator or admin) | the DDMS never deletes it |
| Reachability | `GET {base}/info`, `GET {base}/_ah/warmup` | not in `C` | `[482 core/api/InfoApi.java:48-51]`, `[482 core/api/HealthCheckApi.java:31, 43-47]` |

In every call `{entityId}` is the bare id segment after the type (`<ns>:<group>--<Type>:<entityId>`), never the full id: the contract's defaults for `{id}` are bare GUIDs `[C: lines 127, 189, 250, 319, 387, 450]`, the service looks up `entityId` `[482 core/dataaccess/impl/MongoEntityClient.java:53-117]`, and the integration tests read with the bare id `[482 it/util/EntityUtil.java:48-55]`.

Calls the DDMS makes on the writer's behalf during a `PUT`, with the writer's headers:

| Call | Contract | Code |
| --- | --- | --- |
| Entitlements `GET /groups` (roles, ACL domain) | `[EN: GET /groups, listGroups]` | `[67 occ/entitlements/EntitlementsService.java:73-80]` |
| Legal `POST /legaltags:validate` with `{"names": [...]}` | `[LG: POST /legaltags:validate, validateLegalTags]` | `[67 occ/legal/LegalService.java:86-94]` |
| Legal `GET /legaltags:properties` (country codes) | `[LG: GET /legaltags:properties, getLegalTagProperties]` | `[67 occ/legal/LegalService.java:96-100]` |
| Schema `GET {app.schema.api}/schema/{kind}` | `[SC: GET /schema/{id}, getSchema]` | `[482 core/osducoreserviceclient/schema/SchemaService.java:48-54]` |
| Storage `PUT {app.storage.api}/records` with a one-element array, no `skipdupes` (only when mirroring) | `[S: PUT /records, createOrUpdateRecords]` | `[67 occ/storage/StorageService.java:53-66]`, `[482 core/osducoreserviceclient/storage/StorageClient.java:35-46]` |

The configured roots end in `/api/schema-service/v1` and `/api/storage/v2` `[482 devops/azure/chart/templates/deployment.yaml:67-70]`, `[482 devops/azure/well-delivery.values.yaml:54-55]`, `[482 gc.properties:17-23]`, `[482 devops/ibm/ibm-well-delivery-config/values.yaml:23-24]`, `[482 provider/wd-aws/README.md:51-52]`.

### What `PUT {base}/storage/v1/{type}` does, in order

Basis: `[482 core/services/EntityStorageService.java:73-180]` unless another file is named.

1. Filter: no `Authorization` gives `401`; plain HTTP gives `307` (section 1).
2. Role: creator or admin, otherwise `403` (section 1).
3. Parse: the body, already read by Spring into a Java object, is written out with Gson and read back with Jackson; a failure gives `400` with the parser's message `[482 core/util/Helper.java:39-49]`. Gson leaves out object properties whose value is `null`, so such properties are neither validated, stored, mirrored nor returned (inference).
4. `id`: missing or blank gives `400` "Entity Id is empty."; not matching `^[\w\-\.]+:[0-9a-zA-Z\-]+\-\-[\w\-]*:[\w\-\.\:\%]+$` gives `400` "Entity Id is invalid." (lines 66, 78-82). The type is the text between the first `--` and the next `:`, lowercased; the entity id is everything after that `:` `[482 core/util/Helper.java:82-89]`. The path `{type}` must equal the id's type ignoring case, otherwise `400` `Entity type in API(<type>) and body(<type>) are not same.` (lines 83-85).
5. `kind`: missing or blank gives `400` `The Kind of Entity <id> is empty.` (lines 90-92). The DDMS does not check the kind against the type or against a pattern.
6. `version` (lines 95-102): absent (or JSON `null`, which step 3 drops) means the server uses the current epoch milliseconds. A value that is not a JSON long gives `400` `The version of Entity <id> is invalid.`. A long greater than 0 is used as sent. The check is Jackson's `isLong()`, and Jackson types values that fit in 32 bits as int nodes, so any value from -2147483648 to 2147483647, including `0`, is rejected (inference). Send 13-digit epoch-millisecond values, as the upstream examples do (`"version": 1621376587950` in `[482 examples/Wellbore_910a7be4-b288-4fb6-a9f8-86c37d07f056.json]`).
7. Legal and ACL `[482 core/services/EntityValidateService.java:51-84]`: `legal` missing or empty ("Legal are empty"), `legal.legaltags` not a non-empty array of non-blank strings ("Legal Tags are empty"), `legal.otherRelevantDataCountries` likewise ("Countries are empty"), `acl` missing or empty ("ACL are empty"), `acl.owners` or `acl.viewers` likewise ("Owner Acls are empty", "Viewer Acls are empty"): all `400`. Then:
   - Legal validates the tags; an invalid one gives `400` `Invalid legal tags: <first invalid name>` `[482 core/osducoreserviceclient/legal/LegalAndCacheClient.java:49-60]`.
   - Each country must be a key of Legal's `otherRelevantDataCountries` property map, otherwise `400` `The country code '<code>' is invalid` (same file, lines 62-75); `[LG: GET /legaltags:properties, getLegalTagProperties]` returns that map keyed by ISO alpha-2 code.
   - Each ACL's domain (after `@`) must equal, ignoring case, the domain of the email of the caller's first Entitlements group, otherwise `400` `Invalid domain name: <acl>`; an empty group list or an unexpected email gives `500` "Unknown error happened when validating ACL"; the check that the group exists is commented out `[482 core/osducoreserviceclient/entitlements/EntitlementsAndCacheClient.java:61-90]`. An ACL without `@` fails the split and falls to the catch-all `500` (inference).
   - A Legal failure gives Legal's status with "An unexpected error occurred when validating legal tags" or "An unexpected error occurred when getting legal tag properties" `[482 core/osducoreserviceclient/legal/LegalAndCacheClient.java:77-98]`.
8. Schema: `GET /schema/{kind}`, cached in-process `[482 core/osducoreserviceclient/schema/SchemaAndCacheClient.java:33-50]`. Schema `404` gives `400` `The schema is not existed for Kind <kind>.` (lines 108-110); any other Schema error gives Schema's status with `An unexpected error occurred when getting schema: <Schema body>`.
9. JSON-schema validation of the whole body with `com.networknt:json-schema-validator` 1.0.43 set to JSON Schema version 7 (`SpecVersion.VersionFlag.V7`) `[482 core/util/Helper.java:51-80]`, `[482 wd-core/pom.xml:62-64]`. Findings do not block the write: `valid` becomes `false` and `errors` carries the messages (lines 113-115). A failure to build or run the validator gives `400` with the validator's message.
10. `data`: missing gives `400` "Entity data is empty"; not convertible to an object gives `400` "Invalid data." (lines 118-121, 199-207). `meta`, when present, must be an array of objects, otherwise `400` "Invalid meta." (lines 124-125, 209-223).
11. `data.ExistenceKind`: missing or blank gives `400` "ExistenceKind is empty."; not matching `^[\w\-\.]+:[0-9a-zA-Z\-]+\-\-[\w\-]*:[\w\-\.\:\%]+:[0-9]*$` gives `400` "ExistenceKind is not reference data format." (lines 70, 128-132). The value segment (between the `:` after the type and the next `:`) is lowercased and stored as the existence kind every domain query filters on; any value is accepted, the `planned`/`actual` check is commented out (lines 133-136), `[482 core/util/Helper.java:91-97]`. A value without a trailing `:` does not match: `<ns>:reference-data--ExistenceKind:Planned:` is accepted, `<ns>:reference-data--ExistenceKind:Planned` is not.
12. Dates: `data.StartDateTime` and `data.EndDateTime` are parsed (section 3); a value that does not parse is stored as `null` without an error (lines 139-149, 230-238).
13. References are indexed (section 3) (lines 161-166).
14. Storage mirror, only when `app.entity.storage` is `true` and `version > 20000` (lines 63-64, 71, 170-173). A Storage error gives Storage's status with `An unexpected error occurred when saving record: <Storage body>` `[482 core/osducoreserviceclient/storage/StorageClient.java:35-46]`. Storage's response (record ids and versions) is discarded (line 172).
15. DDMS store write, keyed `<entityId>:<version>` (line 176):
    - Mongo (`app.entity.source=mongodb`: AWS, and an option on Azure per `[482 azure.properties:22]`): `replaceOne` with `upsert(true)`; a driver error gives `500` whose message is the driver's `[482 core/dataaccess/impl/MongoEntityClient.java:41-50]`, `[482 core/dataaccess/impl/MongodbFacade.java:51-62]`.
    - Azure Cosmos: `upsertItem` into a container partitioned on `/entityId`, serialised by one lock per instance `[482 azure/cosmosdb/CosmosEntityClient.java:37-51]`, `[482 azure/cosmosdb/CosmosdbFacade.java:42-50, 470-482]`.
    - Google Cloud: update when a non-deleted row with that type, id and version exists, otherwise insert `[482 gc/dataaccess/GoogleEntityClient.java:43-54]`.
    - IBM Cloudant: `Database.save` with `_id = <entityId>:<version>` and no `_rev` `[482 ibm/dataaccess/CloudantEntityClient.java:32-40]`, `[482 ibm/dataaccess/CloudantEntity.java:55-77]`.
16. `201 Created` with the body in section 3 `[482 core/api/EntityApi.java:72-73]`. Creating and updating both answer `201`.

Storage is written before the DDMS store. A store failure after a successful mirror leaves a Storage record with no DDMS entity; a Storage failure leaves nothing. The upstream design diagram `[482 docs/design/EntityBasedAPISequence.puml:28-35]` draws the opposite order and a relationship-update step; the code is authoritative, and that step is commented out `[482 core/services/EntityStorageService.java:167]`.

## 3. Payload and bulk data shapes

### Request body

One JSON object per request (`@RequestBody @Valid @NotNull Object entity`) `[482 core/api/EntityApi.java:69-71]`. No route accepts a list of entities. An array body finds no `id` and fails with `400` "Entity Id is empty." (inference).

```http
PUT {base}/storage/v1/wellbore
Authorization: Bearer <token>
data-partition-id: <partition>
Content-Type: application/json

{
  "id": "<partition>:master-data--Wellbore:<entityId>",
  "kind": "<authority>:<source>:master-data--Wellbore:<major.minor.patch>",
  "version": <13-digit epoch milliseconds>,
  "acl": { "owners": ["<group>@<domain>"], "viewers": ["<group>@<domain>"] },
  "legal": { "legaltags": ["<legal tag>"], "otherRelevantDataCountries": ["<ISO alpha-2>"] },
  "data": {
    "ExistenceKind": "<partition>:reference-data--ExistenceKind:Planned:",
    "WellID": "<partition>:master-data--Well:<wellEntityId>:<wellVersion>"
  }
}
```

| Field | Contract (`[C: definitions.Entity]`, lines 2189-2207) | Code |
| --- | --- | --- |
| `id` | Optional. `EntityID` declares the pattern `^[\w\-\.]+:master-data\-\-ExampleType:[\w\-\.\:\%]+$` (line 2239), which admits only the example type. | Required; any `<ns>:<group>--<Type>:<entityId>` matching the pattern in step 4 of section 2. |
| `kind` | Required; `EntityKind` pattern `^[\w\-\.]+:[\w\-\.]+:[\w\-\.]+:[0-9]+.[0-9]+.[0-9]+$` (line 2248). | Required, non-blank, and its schema must exist. |
| `acl` | Required, with `owners` and `viewers` required (lines 2249-2269). | Both non-empty arrays of non-blank strings, domains checked. |
| `legal` | Required; `legaltags` and `otherRelevantDataCountries` optional, both `uniqueItems`, countries matching `^[A-Z]{2,2}$` (lines 2270-2290). | Both sub-fields required and non-empty, checked with Legal. |
| `data` | Required, free-form object; example carries `ExistenceKind: 'namespace:reference-data--ExistenceKind:planned:'` (lines 2306-2312). | Required; `data.ExistenceKind` required in reference form. |
| `version` | Not a property of `Entity` (only of `EntityReturn`, "set by the framework", lines 2216-2220); the schema does not forbid additional properties. | Optional caller version (section 2, step 6). |
| `meta` | Not in `C`. | Optional array of objects; stored, mirrored and returned. |
| `tags`, `ancestry`, `createTime`, `createUser`, `modifyTime`, `modifyUser`, other top-level fields | Not in `C`. | Seen by the JSON-schema validation of the whole body, then dropped: `EntityDto`, `MongoEntity`, `CosmosEntity` and the Storage copy carry only id, kind, version, acl, legal, data and meta of the caller's fields `[482 core/models/EntityDto.java:30-57]`, `[482 core/dataaccess/impl/MongoEntity.java:35-87]`, `[482 azure/cosmosdb/CosmosEntity.java:32-78]`, `[482 core/util/RecordConversion.java:38-61]`. The Postman bodies send all of these fields. |

The ACL and legal arrays are copied into hash sets, so duplicates disappear and order is not kept in storage or responses `[482 core/services/EntityValidateService.java:69-83, 117-124]`.

### Response body (`201`)

`EntityDtoReturn` `[482 core/models/EntityDtoReturn.java:31-65]`, written with Gson (pretty-printed, HTML escaping off, `null` fields left out) `[482 core/util/Common.java:22-26]`. `[C: definitions.EntityReturn]` (lines 2208-2230) lists all fields but `errors` and `meta`.

| Field | Meaning |
| --- | --- |
| `id` | The request id as sent (not partition-rewritten). |
| `kind` | As sent. |
| `version` | int64: the version the server used, its own epoch milliseconds or the caller's. Children must cite this value. |
| `acl`, `legal` | As validated (sets). |
| `valid` | The JSON-schema result. |
| `errors` | The JSON-schema messages; absent when validation passed (the helper returns `null`, which Gson leaves out) `[482 core/util/Helper.java:65-72]`. Not in `C`. |
| `data` | As parsed in step 3 of section 2. The keys added to the Storage copy are not echoed. |
| `meta` | As sent, absent when not sent. Not in `C`. |

The response carries no Storage record id and no Storage version.

### Data fields with special meaning

- `data.StartDateTime` and `data.EndDateTime` are parsed by `[482 core/util/DateTimeUtil.java:25-94]`, trying these groups in this order (the patterns inside a group exclude each other): `yyyy-MM-ddTHH:mm:ss`, `yyyy-MM-ddTHH:mm:ss+hh:mm` (or `-hh:mm`), RFC 1123; then `yyyyMMdd`, `yyyy-MM-dd`, `yyyy-MM-dd+hh:mm`; then `yyyy-M-d`; then `yyyy-MM-ddTHH:mm:ssZ`; then `HH:mm:ss`, `HH:mm:ss+hh:mm`; then `H:m:s`.
  - No pattern admits fractional seconds, so `2021-05-18T10:00:00.000Z` is stored as `null`.
  - Time-only values take the server's current date (lines 80-92).
  - Parsing into a local date-time drops a numeric offset rather than converting it (inference).
  - The result is stored as an ISO local date-time string and drives `GET /operationsReports/v1/by_timeRange/{start_time}/{end_time}`; a record whose date is `null` cannot match that query (inference). The service's own description of the route lists the same formats `[482 wd-core/src/main/resources/swagger.properties:83]`.
- `data.FacilityName` is the well name the `wells/v1/.../by_name/{name}` routes match exactly `[482 core/dataaccess/impl/MongodbFacade.java:179-234]`, `[482 gc/util/QueryArgsConcatUtil.java:55-57]`.
- `data.ExtensionProperties` is scanned for references a second time `[482 core/services/EntityStorageService.java:162-166]`; the first pass already descends into it, so its references are indexed twice (inference from `[482 core/util/Helper.java:99-133]`).

### Reference indexing

The domain queries and reference trees work only through this index `[482 core/util/Helper.java:99-148]`:

- Every string value in `data` is tested, including strings in nested objects and in arrays of strings or objects; arrays nested directly in arrays are not scanned.
- The test is `^[\w\-\.]+:[0-9a-zA-Z\-]+\-\-[\w\-]*:[\w\-\.\:\%]+:[0-9]+$` `[482 core/services/EntityStorageService.java:68]`: only references that end in a numeric version match, for example `<ns>:master-data--Wellbore:<entityId>:1621376587950`.
- The usual OSDU form with a trailing colon (`<ns>:master-data--Wellbore:<entityId>:`) does not match; such references are stored in `data` but not indexed, so no domain query finds the record through them.
- A match is stored as `{"id": "<entityId>:<version>", "entityType": "<lowercased type>"}` `[482 core/models/Relationship.java:25-31]`. The namespace is not kept.
- References to the entity's own type are dropped `[482 core/util/Helper.java:144-145]`.
- The relationship type is the lowercased type segment of the reference, so a `by_holeSection` query needs `--HoleSection:` references. The example BHA runs reference `--WellboreSegment:` instead (`data.WellboreSegmentID` in `[482 examples/BHARun_*.json]`).
- The reference trees follow only relationship ids matching `^[^:]+:[0-9]+$` and fetch that exact non-deleted version; entity ids containing `:` are skipped `[482 core/dataaccess/impl/MongoTreeTraversal.java:46-86]`, and every provider applies the same filter `[482 azure/cosmosdb/CosmosTreeTraversal.java:64]`, `[482 gc/dataaccess/JdbcTreeTraversal.java:66]`, `[482 ibm/dataaccess/CloudantTreeTraversal.java:54]`.
- In the twelve schemas the Postman collection registers, all 106 reference patterns end in `:[0-9]*$` (only the records' own `id` patterns do not), so a versioned reference also passes their validation `[482 Postman, folder Schemas]`.

### Bulk data, files and logs

There are none:

- Every operation in `C` and in `core/api/` exchanges JSON entity metadata only.
- `wellLog`, `ppfgDataset` and `plannedLithology` are ordinary JSON entities here.
- The only "batch" route is a read: `POST {base}/query/v1/by_well/{type}:batch` with a JSON array of well entity ids (bare, for example `["welldemo2"]`), returning planned `wellboreTrajectory` or `bhaRun` entities `[C: POST /query/v1/by_well/{type}:batch, Get a list of entity objects for a list of wells]`, `[482 core/api/QueryApi.java:73-92]`, `[482 it/domain/QueryTest.java:115-147]`.

## 4. Identities and versions

### The path `{type}`

- The write route takes any `{type}` equal, ignoring case, to the id's type segment (section 2, step 4). There is no list of accepted types for writes.
- Each type is stored apart: a collection or container named `<lowercased type>Container` (Mongo, Cosmos) `[482 core/dataaccess/impl/MongodbInit.java:63-72]`, `[482 azure/cosmosdb/CosmosdbInit.java:57-64]`, or rows of the single `jdbc_entity` table with the whole entity, type included, in a JSONB column (Google Cloud) `[482 gc/dataaccess/db/postgres/JdbcEntityRepository.java:63-83]`, `[482 gc/model/JdbcEntity.java:36-57]`.
- Every read and delete route rejects a path `{type}` not matching `^[0-9a-zA-Z\-]*$` with `400` `Invalid entity type: <type>` `[482 core/services/EntityStorageService.java:67, 240-299]`. A type containing `_` can be written but not read back.
- Every storage route lowercases the path type except `DELETE /storage/v1/{type}/{id}` `[482 core/services/EntityStorageService.java:272]`. Google Cloud lowercases it anyway `[482 gc/util/QueryArgsConcatUtil.java:27-29]`; Mongo and Cosmos build `<Type>Container` from the raw value. Mongo's existence check ignores case but the collection handle does not, so a mixed-case delete can miss the entity (inference). Send `{type}` in lowercase on every call.

### Types the read routes know

`[482 core/models/ENTITY_TYPE.java:17-69]`; routes from `[482 core/api/*.java]`; index use from `[482 core/dataaccess/impl/MongoQueryClient.java]` and `[482 core/dataaccess/impl/MongodbFacade.java]`. Kinds seen upstream: the tests use `osdu:wks:<group>--<Type>:1.0.0` `[482 it/domain/*.java]`, the examples `osdu-test:well-delivery:<lowercased type>:1.0.0` `[482 examples/]`, the Postman collection `{{authority}}:{{schemaSource}}:<group>--<Type>:<version>` `[482 Postman, folders UC1 and UC3]`.

| `{type}` | Id group seen | Kinds seen | Read routes (existence kind) | Index the read needs |
| --- | --- | --- | --- | --- |
| `well` | `master-data--Well` | tests, examples, Postman | `wells/v1/by_name/{name}`, `.../{version}`, `versions/by_name/{name}`, each `:actual` and `:planned` | none: exact `data.FacilityName` and existence kind |
| `wellPlanningWell`, `wellPlanningWellbore` | `master-data--WellPlanningWell`, `--WellPlanningWellbore` | examples, Postman | `storage/v1` only | n/a |
| `wellbore` | `master-data--Wellbore` | tests, examples, Postman | `wellbores/v1/by_well/{well_id}`, `.../{wellbore_version}`, `versions/by_well/{well_id}`, each `:actual` and `:planned`; also resolves well to wellbore for the activity plan, well activity program and by-wells routes | type `well`, id starting `<wellEntityId>:` |
| `wellActivityProgram` | `master-data--WellActivityProgram` | tests, examples, Postman | `wellActivityPrograms/v1/by_well/{well_id}`, `.../{wap_version}`, `versions/...`, `reference_tree/...`, `full_content/...` (planned) | type `wellbore`, pointing at the latest planned wellbore that references the well (well, then wellbore, then program) |
| `activityPlan` | `master-data--ActivityPlan` | tests; examples; Postman writes kind `...:master-data--WellActivityPlan:1.0.0` | `activityPlans/v1/by_well/{well_id}` (planned); `query/v1/activityPlan/by_wellbore/{id}:planned` | type `wellbore` |
| `wellboreArchitecture` | `master-data--WellboreArchitecture` | examples, Postman | `query/v1/wellboreArchitecture/by_wellbore/{id}:planned` (`C` spells the enum value differently, section 9) | type `wellbore` |
| `wellboreTrajectory` | `work-product-component--WellboreTrajectory` (examples, Postman); `master-data--WellboreTrajectory` (tests) | tests `osdu:wks:work-product-component--WellboreTrajectory:1.0.0`; Postman `...:1.1.0` | `POST query/v1/by_well/wellboreTrajectory:batch` and `GET wellboreTrajectories/v1/by_wells/{well_ids}:planned` (not in `C`); `query/v1/wellboreTrajectory/by_wellbore/{id}:planned` and `:actual` | type `wellbore`. The by-wells routes need, on Mongo and Cosmos, the exact id `<wellboreEntityId>:<latest version>` of each planned wellbore of the wells, and answer `404` when any such wellbore has none `[482 core/dataaccess/impl/MongoQueryClient.java:95-111, 363-373]`, `[482 core/dataaccess/impl/MongodbFacade.java:451-481]`; Google Cloud matches a text prefix of the bare wellbore id instead `[482 gc/dataaccess/GoogleQueryClient.java:93-111]`. |
| `holeSection` | `master-data--HoleSection` | tests, Postman | `holeSections/v1/by_wellbore/{wellbore_id}` (planned); `query/v1/holeSection/...:planned` | type `wellbore` |
| `bhaRun` | `master-data--BHARun` | tests, examples, Postman | `bhaRuns/v1/by_holeSection/{id}` (planned); `bhaRuns/v1/by_wellbore/{id}:actual`; `POST query/v1/by_well/bhaRun:batch` and `GET bhaRuns/v1/by_wells/{well_ids}:planned` (not in `C`); `query/v1/bhaRun/...:planned` and `:actual` | type `holesection` for `by_holeSection`; type `wellbore` for the rest (by-wells as for trajectories) |
| `tubularAssembly`, `tubularComponent` | `work-product-component--TubularAssembly` (examples, Postman); the component not seen | examples, Postman (assembly) | `query/v1/{type}/...:planned` and `:actual` | type `wellbore` (the example assemblies carry none) |
| `operationsReport` | `master-data--OperationsReport` (tests) | tests | `operationsReports/v1/by_wellbore/{id}`, `latest/by_wellbore/{id}`, `by_timeRange/{start}/{end}`, `reference_tree/by_operationsReport/{id}` (actual); `query/v1/operationsReport/...:actual` | type `wellbore`; the time range uses the stored `startTime` and `endTime` |
| `fluidsReport` | `master-data--FluidsReport` (tests) | tests | `fluidsReports/v1/by_wellbore/{id}` (actual); `query/v1/fluidsReport/...:actual` | type `wellbore` |
| `fluidsProgram` | not seen | not seen | `fluidsPrograms/v1/by_wellbore/{id}` (planned); `query/v1/fluidsProgram/...:planned` | type `wellbore` |
| `casingDesign`, `evaluationPlan`, `plannedCementJob`, `wellBarrierElementTest`, `wellLog` | not seen as written ids (Postman references some as `master-data--...`) | not seen | `query/v1/{type}/by_wellbore/{id}:planned` | type `wellbore` |
| `geometricTargetSet`, `risk`, `surveyProgram` | `master-data--GeometricTargetSet`, `--Risk`, `--SurveyProgram` (examples) | examples | `query/v1/{type}/by_wellbore/{id}:planned` | type `wellbore` (the example risks carry no wellbore reference, so this query would not find them) |
| `ppfgDataset`, `plannedLithology` | `work-product-component--PPFGDataset`, `--PlannedLithology` (referenced, unversioned, in Postman) | not seen | `query/v1/{type}/by_wellbore/{id}:planned` | type `wellbore` |
| `wellboreMarkerSet` | `work-product-component--WellboreMarkerSet` (examples) | examples | `query/v1/wellboreMarkerSet/...:planned` | type `wellbore` |

Not members of `ENTITY_TYPE`, written through the generic route in the examples: `rig` (`master-data--Rig`, reached only through reference trees) and `wellboreSegment` (`master-data--WellboreSegment`).

Contract lines for the query types: planned list `[C: lines 1755-1774]` and `[482 core/api/QueryApi.java:111-129]`; actual list `[C: lines 1830-1836]` and `[482 core/api/QueryApi.java:156-161]`; by-wells types `[C: lines 1692-1694]` and `[482 core/api/QueryApi.java:79-90]`. A value outside the code's list answers `400` `Invalid entity type: <type>` `[482 core/api/QueryApi.java:90, 135, 167]`.

Every read route that touches a type answers `400` `Collection <type>Container is not existed.` on Mongo when that type was never written `[482 core/dataaccess/impl/MongodbFacade.java:558-567]`. Cosmos has the same check with `The Container <type>Container is not existed` `[482 azure/cosmosdb/CosmosdbFacade.java:431-441]`, but it tests `db.getContainer(name).getId()` `[482 azure/cosmosdb/CosmosdbFacade.java:502-522]`, which the Cosmos SDK answers without contacting the service, so that `400` is probably never raised there and the read fails later instead (inference).

### Ids

- The DDMS never mints ids: `id` is required (section 2, step 4).
- The store key keeps only the type name and the entity id: the namespace and the group are dropped `[482 core/services/EntityStorageService.java:83-87]`, `[482 core/util/Helper.java:82-89]`, `[482 core/dataaccess/impl/MongoEntity.java:66]`. `<ns1>:master-data--Well:<x>` and `<ns2>:master-data--Well:<x>` are the same entity to the DDMS, and so are `master-data--WellboreTrajectory:<x>` and `work-product-component--WellboreTrajectory:<x>`. Where one store serves several partitions (AWS, Google Cloud; section 6), entity ids must be unique across those partitions as well.
- The integration tests and the Postman collection use the data partition id as the namespace (`<partition>:master-data--<Type>:<entityId>`) `[482 it/util/EntityUtil.java:231-239]`, `[482 Postman, folder UC1]`; the Storage copy's id is then the id as sent (below).
- Keep the entity id segment to letters, digits, `-`, `_` and `.`: a `:` makes the reference trees skip the entity and breaks the well-to-wellbore resolution, which keeps only the text before the first `:` `[482 core/dataaccess/impl/MongoQueryClient.java:351-361]`; the entity id also becomes part of path segments that end in `:purge`, `:planned` or `:actual`.
- Each `PUT` produces two identities when the deployment mirrors: the DDMS key (`{type}`, entity id, version) and the Storage record id with a Storage-assigned version.

### Versions, idempotency and conflicts

- Each `PUT` writes the key `<entityId>:<version>`. Without a caller version, every call, a retry included, creates a new version stamped with the current millisecond; two writes of one id in the same millisecond share a key and the second replaces the first.
- A caller version makes retries idempotent in the DDMS store on Mongo, Cosmos and Google Cloud: the same id and version replace that version in place (upsert keyed on `<entityId>:<version>`).
  - The upstream examples do this. The wellbore example carries `"version": 1621376587950` and references a trajectory (`...:1621376598066`), a marker set (`...:1621376599046`), a survey program (`...:1621376600121`) and a target set (`...:1621376611419`) whose versions are later; the wellbore was presumably rewritten at its own version once those existed (inference) `[482 examples/Wellbore_910a7be4-b288-4fb6-a9f8-86c37d07f056.json]`.
  - On IBM the save sends no `_rev`, so repeating a key is a document conflict for Cloudant, which the service answers through its catch-all `500` (inference).
  - The Storage mirror receives the record again on every retry. `PUT /records` on an existing id creates a new Storage version `[S: PUT /records, createOrUpdateRecords]`, and the DDMS does not send `skipdupes` `[67 occ/storage/StorageService.java:60-66]`.
- Restoring a soft-deleted version means writing the same version again. Mongo and Cosmos write it with `deleted=false` `[482 core/dataaccess/impl/MongoEntity.java:72]`, `[482 azure/cosmosdb/CosmosEntity.java:63]`; on Google Cloud the existence check ignores deleted rows, so a new row is inserted beside the deleted one `[482 gc/dataaccess/GoogleEntityClient.java:45-50]`, `[482 gc/dataaccess/db/postgres/JdbcEntityRepository.java:113-125]`.
- "Latest" is a text order, not a numeric one: by `_id` on Mongo `[482 core/dataaccess/impl/MongodbFacade.java:64-78]`, by `c.id` on Cosmos `[482 azure/cosmosdb/CosmosdbFacadeConstants.java:6-7]`, by `data->>'version'` on Google Cloud `[482 gc/util/StatementBuilder.java:77-80]`, `[482 gc/dataaccess/db/postgres/JdbcEntityRepository.java:99-111]`, and "latest per entity" by the largest `_id` or `id` text `[482 core/dataaccess/impl/MongodbFacade.java:419-481]`, `[482 azure/cosmosdb/CosmosdbFacade.java:279-325]`. Keep every version of an entity at the same digit width (13-digit epoch milliseconds) so text order equals numeric order.
- No optimistic concurrency: there is no ETag, `If-Match` or expected-version parameter in `C` or in `core/api/`; the last write wins.

### The Storage copy

`[482 core/util/RecordConversion.java:38-71]`, checked by `[482 wd-core/src/test/java/org/opengroup/osdu/wd/core/util/RecordConversionTest.java]`:

- `id`: the request id with its first `:`-segment replaced by the `data-partition-id` value. Java's `String.replace` replaces every occurrence of that text, inside the entity id too; with the partition id as namespace the rewrite changes nothing. Storage's own rule for ids is `{Data-Partition-Id}:{object-type}:{uuid}` `[S: components.schemas.Record]`.
- `version`: the DDMS version is sent, but Storage's contract marks `Record.version` read-only and "assigned by the server on each update" `[S: components.schemas.Record]`; the Storage version is not the DDMS version, and the DDMS does not report it.
- `kind`, `acl`, `legal`, `meta`: as validated (the Storage copy keeps the real `viewers`).
- `data`: the request data plus `origId` (the request id), `entityType` (lowercased type), `entityId` and `ddmsid` (`app.ddmsid`, `well-delivery-ddms-1` for every provider, `[482 <provider>.properties:6]`, line 7 for Azure); caller keys with those names are overwritten.
- No `tags` and no `ancestry`, although Storage's `Record` has both `[S: components.schemas.Record]`.

| Provider | Store (`app.entity.source`) | Mirror (`app.entity.storage`) | Basis |
| --- | --- | --- | --- |
| Azure | `cosmosdb` | `true` | `[482 azure.properties:23, 26]` |
| AWS | `mongodb` | `true` | `[482 aws.properties:15, 17]` |
| Google Cloud | `postgresql` | `false` in the properties; the Helm chart sets `APP_ENTITY_STORAGE: "true"` by default, which Spring binds to `app.entity.storage` (inference) | `[482 gc.properties:30, 33]`; `[482 devops/gc/deploy/values.yaml:15]`; `[482 devops/gc/deploy/templates/configmap.yaml:29]`; `[482 devops/gc/deploy/templates/spot-deploy.yaml:64-68]` |
| IBM | `cloudantdb` (chart) | `true` (chart); the properties hold placeholders | `[482 devops/ibm/ibm-well-delivery-config/values.yaml:11-12]`; `[482 devops/ibm/ibm-well-delivery-config/templates/configmap.yaml:25-27]` |

Whether a deployment mirrors cannot be read through the API.

### Write ordering

A parent must exist, at the version a child cites, before the child is written if the reference trees are to resolve, and the child's reference must carry that version. The version comes from the parent's `201` or from a version the writer chose.

- The Postman collection writes UC1 (`well`, `wellbore` citing `Well:welldemo2:{{wellVersion}}`, `wellplanningwell`, `wellplanningwellbore`, `activityplan`, `wellactivityprogram`) and then UC3 (`tubularassembly`, two `holesection`s, `bharun` citing `HoleSection:holesectiondemo3:{{hole3Version}}`, `wellborearchitecture`, `wellboretrajectory`). The writes whose versions later bodies cite store `pm.response.json().version` (well, wellbore, wellplanningwell, activityplan, wellactivityprogram, tubularassembly, both hole sections); the other four do not `[482 Postman, folders UC1 and UC3]`.
- The integration tests write well, wellbore, hole section, trajectory, BHA run and fluids report in that order, each citing the versions returned before `[482 it/domain/QueryTest.java:67-102]`.
- The versions cited in the examples rise in dependency order: well `1621376575600`, wellbore `1621376587950`, planning wellbore `1621376588941`, rig `1621376589846`, segments and BHA runs `1621376590691` to `1621376596281`, architecture `1621376597174`, trajectory `1621376598066`, marker set `1621376599046`, survey program `1621376600121`, target set `1621376611419`, risks `1621376614367` to `1621376620450`, activity plan `1621376621036`. The well activity program, which cites the rig, the wellbore, the BHA runs, the architecture, the risks and the activity plan, carries no version and is cited by nothing `[482 examples/]`.

## 5. Reads, verification and deletes

### Read back

| Read | Route | Notes |
| --- | --- | --- |
| A specific version | `GET {base}/storage/v1/{type}/{entityId}/{version}` | `404` `Could not find entity version with id: <id>_<version>` `[482 core/services/EntityStorageService.java:250-258]`; Google Cloud raises its own `404` `There is no objects with id: <id>, and version: <version>` `[482 gc/dataaccess/GoogleEntityClient.java:66-74]`. |
| The latest version | `GET {base}/storage/v1/{type}/{entityId}` | `200` `EntityReturn` whose `id` is the full original id; `404` `Could not find entity with id: <id>` `[482 core/services/EntityStorageService.java:240-248]`; Google Cloud: `There is no objects with id: <id>` `[482 gc/dataaccess/GoogleEntityClient.java:56-64]`. |
| The version list | `GET {base}/storage/v1/{type}/versions/{entityId}` | `{"id": "<entityId>", "versions": [...]}` `[C: definitions.VersionNumbers]`; ascending on Mongo, Cosmos and IBM, descending on Google Cloud `[482 core/dataaccess/impl/MongodbFacade.java:94-113]`, `[482 azure/cosmosdb/CosmosdbFacade.java:77-80]`, `[482 ibm/dataaccess/CloudantdbFacade.java:72-75]`, `[482 gc/dataaccess/db/postgres/JdbcEntityRepository.java:142-152]`. |
| The index | the domain route of the type (section 4) | A `404` there with a `200` on the entity read means the references were unversioned or of another type. |
| The Storage copy | `GET /api/storage/v2/records/{storageId}` | `[S: GET /records/{id}, getLatestRecordVersion]`; the DDMS exposes nothing about it. |

### Verification rules

- Compare `id`, `kind` and `version` exactly; `acl` and `legal` as sets; `data` and `meta` as JSON values.
- Compare numbers by value. The service writes and reads bodies with Gson; on the Mongo and IBM paths `data` is read back through Gson into untyped maps, which makes every number a double, so `100` can come back as `100.0` (inference).
- Expect `data` properties sent as `null` to be absent (section 2, step 3).
- Azure and IBM return `acl.viewers` equal to `acl.owners` `[482 azure/cosmosdb/CosmosEntity.java:66, 93]` (Azure also stores it that way), `[482 ibm/dataaccess/CloudantEntity.java:65, 91]`. A viewers mismatch there is this upstream defect, not a delivery error.
- `valid` is kept; `errors` is not, so only the `201` carries the messages `[482 core/dataaccess/impl/MongoEntity.java:89-105]`.
- A read of a type never written answers `400` on Mongo (section 4), `404` on Google Cloud, and probably a `404` or `500` on Cosmos (inference).
- The Storage copy is a separate record at the rewritten id; check its existence, `data.origId`, `data.entityType`, `data.entityId` and `data.ddmsid` through Storage.

### Delete

| Operation | Route | Behaviour |
| --- | --- | --- |
| Soft delete, all versions | `DELETE {base}/storage/v1/{type}/{entityId}` with `Content-Type: application/json` and a lowercase type | Flags every version deleted: Mongo `updateMany` `[482 core/dataaccess/impl/MongodbFacade.java:115-124]`, Cosmos one upsert per item `[482 azure/cosmosdb/CosmosdbFacade.java:82-110]`, Google Cloud `deleted_at` `[482 gc/dataaccess/db/postgres/JdbcEntityRepository.java:154-170]`. `204`, or `404` when no row exists for the id. The count includes rows already deleted, so repeating the delete answers `204` again on these three providers. |
| Soft delete, one version | `DELETE {base}/storage/v1/{type}/{entityId}/{version}` with the JSON content type | `204`, or `404` `Could not find entity version with id: <id>_<version>` `[482 core/services/EntityStorageService.java:285-291]`. |
| Purge, all versions | `DELETE {base}/storage/v1/{type}/{entityId}:purge` with the JSON content type | Admin only. Physical delete on Mongo, Cosmos and Google Cloud `[482 core/dataaccess/impl/MongodbFacade.java:126-135]`, `[482 azure/cosmosdb/CosmosdbFacade.java:112-134]`, `[482 gc/dataaccess/db/postgres/JdbcEntityRepository.java:172-186]`. |
| Purge, one version (not in `C`) | `DELETE {base}/storage/v1/{type}/{entityId}/{version}:purge` | Admin only. A soft delete on Google Cloud `[482 gc/dataaccess/GoogleEntityClient.java:96-99]`. |

- There is no undelete route, although `C` says the deletes "can be reverted later" `[C: lines 162-165, 292-295]`. Writing the same version again is the only restore (section 4).
- Deletes and purges never reach the Storage copy: the Storage client has only `saveRecord`, called only from the write `[482 core/osducoreserviceclient/storage/IStorageClient.java:20-23]`, `[482 core/services/EntityStorageService.java:172]`. Removing a mirrored record takes `POST /api/storage/v2/records/{storageId}:delete` `[S: POST /records/{id}:delete, deleteRecord]`. `DELETE /records/{id}` is Storage's purge `[S: DELETE /records/{id}, purgeRecord]`, which this repository's live-cleanup rule excludes, as it excludes the DDMS purges.
- On Azure and IBM the `storage/v1` reads keep returning a soft-deleted entity, and the version list keeps listing it: the latest read, the version read and the version list do not filter `deleted` `[482 azure/cosmosdb/CosmosdbFacadeConstants.java:5-7]`, `[482 azure/cosmosdb/CosmosdbFacade.java:52-80, 392-396]`, `[482 ibm/dataaccess/CloudantdbFacade.java:41-75]`, and the response has no `deleted` flag. There, a soft delete shows only in the `204` and in the entity dropping out of the domain queries, which do filter `deleted = false` `[482 azure/cosmosdb/CosmosdbFacade.java:222-372]`. Only a purge makes those reads answer `404`.
- Mongo and Google Cloud reads filter deleted rows `[482 core/dataaccess/impl/MongodbFacade.java:64-113]`, `[482 gc/util/StatementBuilder.java:57-60]`.

### Probes

| Probe | Route | Auth | Basis |
| --- | --- | --- | --- |
| Readiness | `GET {base}/_ah/warmup`: `200`, empty body | none in the service (`@PermitAll`, exempt in the filter) | `[482 core/api/HealthCheckApi.java:31, 43-47]`; readiness probe `[482 devops/azure/chart/templates/deployment.yaml:52-55]`, `[482 devops/azure/well-delivery.values.yaml:36-38]` |
| Version information | `GET {base}/info`: `VersionInfo` JSON | none in the service | `[482 core/api/InfoApi.java:48-51]`; liveness probe `[482 devops/azure/well-delivery.values.yaml:39-42]` |
| Actuator | Azure exposes `health,info,prometheus`; Google Cloud serves `/health/liveness` and `/health/readiness` on management port 8081, outside the ingress prefix | n/a | `[482 azure.properties:5]`, `[482 gc.properties:42-46]`, `[482 devops/gc/deploy/templates/spot-deploy.yaml:90-102]` |
| Credentials and partition | an entity read of a known id | viewer or above | `[C: GET /storage/v1/{type}/{id}, Get latest versio of entity]`, `[482 core/api/EntityApi.java:92-99]` |

Neither `/_ah/warmup` nor `/info` is in `C`. On Azure the chart's Istio policy does not exempt them (section 1), so a probe should carry the bearer token too. There is no dry-run, validate-only or permission-check route.

## 6. Limits and errors

### Limits

- One entity per request; there is no write batch.
- No request-size limit is configured in the project (no `max-http`, `max-request` or multipart settings in any properties file or chart).
- Legal's validate body accepts at most 25 names `[LG: POST /legaltags:validate, validateLegalTags]`, and the DDMS sends all of a record's tags in one call `[482 core/osducoreserviceclient/legal/LegalAndCacheClient.java:88-98]`, so more than 25 tags on one record presumably fails there (inference).
- Google Cloud, when `global.autoscalingMode` is `requests` (the default is `cpu`): an Envoy local rate limit on each pod's inbound traffic with a bucket of 80 tokens refilled by 80 every second, and `x-ratelimit` headers `[482 devops/gc/deploy/templates/rate-limit.yaml:16-58]`, `[482 devops/gc/deploy/values.yaml:5, 64-67]`. Envoy refuses the excess with `429` (inference).
- Azure: every new container is created with a manual throughput of 1200 `[482 azure/cosmosdb/CosmosdbFacade.java:470-482]`.

### Outcome of a `PUT`

| Response | Cause | Treat as |
| --- | --- | --- |
| `201`, `valid: true` | stored | delivered |
| `201`, `valid: false` | stored with JSON-schema findings in `errors` | delivered with warnings; keep `errors` in the ledger, it cannot be read later |
| `400` with a validation message | id, type, kind, version, legal, countries, ACL domain, data, meta, ExistenceKind, missing schema, validator failure (section 2) | permanent for this payload |
| `401`, empty body | no `Authorization` | configuration |
| `403` "The user is not authorized to perform this action" | no creator or admin role; or Entitlements refused the token and its status is passed on | configuration or credentials |
| `403` `Error getting partition info for data-partition: <id>` | Azure, when the store client is first created for an instance | configuration `[482 azure/partition/PartitionAndCacheService.java:92-101]` |
| `307` | plain HTTP | configuration |
| `415` (inference) | no JSON content type | defect of the route |
| a core service's status with "An unexpected error occurred when ..." | Legal, Schema or Storage failed; for Storage nothing reached the DDMS store | retry on `429` and `5xx`; otherwise permanent. Storage's `PUT /records` documents `404` "Invalid acl group" `[S: PUT /records, createOrUpdateRecords]`, so a `PUT` to the DDMS can answer `404`. |
| `500` | store failure (driver message), an empty group list, an unexpected exception ("An unknown error has occurred."), an IBM key conflict (inference) | retry with the same version |
| `429` (inference), `502`, `503` | gateway | retry with the same version |

### Shared-instance hazards and caches

Read from the code, not observed live (inference):

- `MongodbInit` and `CosmosdbInit` are singletons that keep two things in unsynchronised fields `[482 core/dataaccess/impl/MongodbInit.java:26-78]`, `[482 azure/cosmosdb/CosmosdbInit.java:25-70]`:
  - the database client, created from the first request's `data-partition-id` and reused for every later request, so one instance serves one partition only;
  - the "current" collection or container, which concurrent requests for different types overwrite, so concurrent writes of different types to one instance can land in the wrong type's collection.
- Google Cloud (one table) and IBM (`CloudantdbInit` is request-scoped, `[482 ibm/dataaccess/CloudantdbInit.java:29-31]`) do not share this pattern.
- The AWS connection string ignores the partition, so all partitions share one DDMS database there `[482 aws/dataaccess/AWSMongodbConnection.java:84-112]`, `[482 aws.properties:19]`. Google Cloud has one datasource and no partition column or filter, so all partitions share its table `[482 gc.properties:35-39]`, `[482 gc/model/JdbcEntity.java:36-43]`, `[482 gc/util/StatementBuilder.java:23-171]`.
- When Azure looks up a partition's Cosmos endpoint, it replaces the request's `Authorization` with the service's own token for the rest of that request `[482 azure/partition/PartitionAndCacheService.java:80-89, 137-140]`; this happens only when the instance creates its client, and a read at that moment runs the record-level check with the service's groups.
- The in-process caches never expire by time: `LRUCache.get` compares `Duration.between(Instant.now(), node.timestamp)`, which is never positive `[482 core/cache/LRUCache.java:61-79, 119-129]`. `SchemaCache`, `LegalTagCache`, `CountryCodeCache`, `GroupCache` and `MongoConnStringCache` are all `LRUCache(100, 600)` (`[482 core/cache/SchemaCache.java:22]` and line 22 of the other four), so entries leave only by capacity or restart:
  - a legal tag or country accepted once stays accepted by this instance;
  - a changed schema for a kind is not read again;
  - a caller's group change is not seen while the same token is in use (not on Google Cloud, which does not cache groups);
  - `isLegalTagInCache` and `isCountryCodeInCache` also answer "cached" when the cache throws `[482 core/osducoreserviceclient/legal/LegalAndCacheClient.java:100-130]`.

## 7. Compared with a direct Storage upsert

Storage facts come from `S`; DDMS facts from the code cited above.

What the DDMS adds:

1. Identity rules: a mandatory id of the form `<ns>:<group>--<Type>:<entityId>`, a path type equal to the id's type, and a reference-form `data.ExistenceKind` on every entity.
2. A schema gate: the kind's schema must exist, and the whole body is validated against it (JSON Schema version 7) without rejecting the write.
3. Legal and ACL checks before anything is written: tag validity, a non-empty and valid `otherRelevantDataCountries`, and ACL domains equal to the caller's group domain.
4. The reference index, existence kind and time window in a store of its own, which every domain query and reference tree depends on.
5. Writer-chosen version keys, which make retries idempotent in that store and let a parent be rewritten in place with references to children written later.

What changes or is lost relative to `PUT /api/storage/v2/records` `[S: PUT /records, createOrUpdateRecords]`:

- One record per call; Storage takes an array of records per call, and the DDMS's own array overload `IStorageClient.saveRecord(Record[])` is never called `[482 core/osducoreserviceclient/storage/StorageClient.java:48-59]`.
- `tags`, `ancestry` and every other top-level field are dropped.
- The Storage copy's `data` gains `origId`, `entityType`, `entityId` and `ddmsid`, and its id is rewritten with the partition id.
- Storage's response (`recordIds`, `recordIdVersions`, `skippedRecordIds`, `[S: components.schemas.CreateUpdateRecordsResponse]`) is not returned.
- Deletes are not propagated to Storage.
- Mirroring depends on a deployment flag and is skipped for `version <= 20000`.
- The DDMS store is written after Storage, so a partial failure leaves a Storage-only record.

Writing the same kinds straight to Storage is not equivalent. A Storage-only record has no DDMS entity: the `storage/v1` reads answer `404` (or `400` for a type never written on Mongo), every domain query and reference tree misses it, it has no index, existence kind or time window, it lacks the four provenance keys, and it skipped the DDMS gates. In the other direction, the record the DDMS mirrors is an ordinary Storage record with the same kind, ACL, legal and data, apart from the id rewrite, the added keys and the dropped `tags` and `ancestry`, and only where mirroring is on. A consumer of the Well Delivery APIs needs the DDMS route; a consumer of Storage records alone is served by either path when mirroring is on, except that `tags` and `ancestry` survive only a direct Storage write. What other services (Search, for example) do with either record is outside this project's code.

## 8. What the route type needs

1. Endpoint and transport: `PUT {base}/storage/v1/{type}` with one JSON object per request (batch size 1); `Authorization: Bearer <token>`, `data-partition-id`, `Content-Type: application/json` on the write and on every `DELETE`; `{type}` taken from the id's type segment and sent in lowercase; a `correlation-id` per attempt.
2. Id policy: always send `id`; use the data partition id as the namespace; keep the entity id segment free of `:` and `%`; record in the ledger both the DDMS key (type, entity id, version) and, when mirroring, the Storage record id; log both identities for live verification.
3. Version policy: the route mints the version as a 13-digit epoch-millisecond value (which clears both lower limits of section 2, 2147483647 and 20000), increasing per entity, and keeps it in the ledger before the first attempt so every retry of that record revision sends the same value. It sends the value as `version` and stores the version sent and the version returned. Children can then cite `...:<entityId>:<version>` without waiting for the parent's response, and a parent can be written again at its version to add references to children written later.
4. References and order: the mapping renders every cross-entity reference as `<ns>:<group>--<Type>:<entityId>:<version>` with the type segment the target queries filter on; references ending in `:` are stored but not indexed. The route orders deliveries on those references, parents first, and needs a "write the parent again at the same version" step.
5. Preflight, once per run: the schema of each kind exists (`[SC: GET /schema/{id}, getSchema]`); legal tags are valid, at most 25 per record, and `otherRelevantDataCountries` is non-empty and valid (`[LG: POST /legaltags:validate, validateLegalTags]`, `[LG: GET /legaltags:properties, getLegalTagProperties]`); every ACL entry has the partition's group domain after `@`; `data.ExistenceKind` is in reference form with a trailing `:`; `StartDateTime` and `EndDateTime` use a supported format without fractional seconds, since an unsupported one is dropped silently.
6. Outcomes: as in section 6; a `valid: false` is a warning outcome whose `errors` are kept.
7. Concurrency: one partition per deployment, and writes to one DDMS deployment serial, or at least never different types at the same time (section 6); honour the Google Cloud rate limit where it is on.
8. Verification: `GET {base}/storage/v1/{type}/{entityId}/{version}` with the rules of section 5; optionally the domain query the record should appear in; the Storage copy through Storage. A per-deployment setting says whether the DDMS mirrors and which provider it runs, since the API exposes neither.
9. Delete and cleanup: the reversible scope is the DDMS soft delete (`DELETE {base}/storage/v1/{type}/{entityId}`) plus `POST /api/storage/v2/records/{storageId}:delete` for the mirrored copy; the DDMS does not propagate deletes. Writing the same version again is the only restore.
10. Probe: `GET {base}/info` or `GET {base}/_ah/warmup` with the bearer token for reachability, and an entity read of a known id for credentials and partition. There is no dry-run write.
11. Capabilities the route type must express: single-object body; required client id with the path segment taken from its type; client-minted version; JSON content type on `DELETE`; version-suffixed reference rendering and dependency order; a mirrors-to-Storage flag with a second identity; a delete that does not cascade to Storage; a per-deployment concurrency limit; an optional rate limit; a warning outcome.

## 9. Contract versus code

1. `C` says `PUT` creates a new entity "when no entity id is provided" `[C: lines 43-50]`; the code requires `id` `[482 core/services/EntityStorageService.java:78-79]`.
2. `C`'s `EntityID` declares the pattern `^[\w\-\.]+:master-data\-\-ExampleType:[\w\-\.\:\%]+$` `[C: line 2239]`; the code accepts any `<ns>:<group>--<Type>:<entityId>`. A request check against `C` must treat that pattern as an example, or it rejects every real id.
3. `C`'s `Entity` has no `version` and says the returned version is "set by the framework" `[C: lines 2189-2207, 2216-2220]`; the code accepts a caller version.
4. `C`'s `EntityReturn` lacks `errors` and `meta`, which the code returns `[482 core/models/EntityDtoReturn.java:44-48]`.
5. `C` gives purge to creator or admin `[C: lines 424-426]`; the code allows admin only `[482 core/api/EntityApi.java:194]`. The service's own description repeats the contract's text `[482 wd-core/src/main/resources/swagger.properties:58-60]`.
6. `DELETE /storage/v1/{type}/{id}/{version}:purge`, `GET /bhaRuns/v1/by_wells/{well_ids}:planned`, `GET /wellboreTrajectories/v1/by_wells/{well_ids}:planned`, `GET /_ah/warmup` and `GET /info` exist in the code and not in `C`; every operation of `C` exists in the code.
7. `C`'s planned query enum lists `wellboreArchitectory` `[C: line 1757]`; the code accepts only `wellboreArchitecture` `[482 core/models/ENTITY_TYPE.java:30]`, so the contract's value answers `400` "Invalid entity type: wellboreArchitectory".
8. `C` does not require `legal.legaltags` or `legal.otherRelevantDataCountries` `[C: lines 2270-2290]`; the code requires both, non-empty.
9. `C` lists `Content-Type` as a required header only on the batch query; the code also requires the JSON content type on three `DELETE` routes.
10. `C` lists no `401`, `415` or `5xx`; the filter answers `401`, and the code's own OpenAPI annotations list `401`, `404`, `500`, `502` and `503` on the storage operations `[482 core/api/EntityApi.java:57-66]`.
11. `C`'s `ReferenceTree` items spell `verion` `[C: lines 2332, 2341]`; the code writes `version` `[482 core/dataaccess/impl/MongoTreeTraversal.java:55]`.
12. `C` says the deletes "can be reverted later" `[C: lines 164, 294]`; there is no revert route.
13. `C` documents `400` only as "Invalid entity type"; the code also answers `400` for a type never written (Mongo).
14. `C`'s `type` path parameter defaults to `Well` on every storage operation `[C: lines 68, 121, 183, 244, 313, 381, 444]`; used on `DELETE /storage/v1/{type}/{id}` (line 183), which does not lowercase it, that default addresses `WellContainer` on Mongo and Cosmos.
15. `C`'s `BHARun` by-section parameter is `holeSection_id` `[C: line 1559]`; the code names it `hole_section_id` `[482 core/api/BHARunApi.java:67-70]`. The URL is unaffected.
16. `C` declares an `apiKey` security scheme named `Bearer`; the code's OpenAPI declares HTTP bearer named `Authorization`. Both carry the token in `Authorization`.
17. The upstream design diagram `[482 docs/design/EntityBasedAPISequence.puml:28-35]` shows a relationship-update step and the store write before the Storage write; the code has the step commented out and writes Storage first.

## 10. Open points

1. Every statement marked (inference) needs a live check or a contract fake that encodes it: `415` without the content type, rejection of 32-bit versions, dropped `null` properties, numbers read back as doubles, the Cosmos existence check, IBM key conflicts, Envoy's `429`, Istio's refusal of unauthenticated probes.
2. Whether a target deployment mirrors to Storage, and which provider it runs, cannot be read through the API; both must be configuration of the route.
3. What Storage does with the `version` field the DDMS sends, which its contract marks read-only, is not stated in `S`.
4. Cleanup on Azure and IBM: this repository requires a read of each created id to answer `404` before cleanup counts as done, but the `storage/v1` reads keep returning soft-deleted entities there, and only a purge (admin, physical, excluded by the same rule) makes them answer `404`. The proof of cleanup on those providers needs a decision, for example the `204` plus the entity's absence from its domain query.
5. The domain queries differ by provider (exact or prefix id match, latest version per entity or every version), so a verification step that uses them must know the provider.
6. Which kinds have schemas on a target is a question for its Schema service; the upstream tests, examples and Postman collection use three different kind families.
7. The shared-instance hazards of section 6 decide how much concurrency is safe per deployment; they were read from code, not measured.
