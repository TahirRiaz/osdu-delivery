# Reservoir DDMS (Open ETP Server): integration brief

The Reservoir DDMS is the Open ETP Server: a C++ service that stores RESQML (and WITSML and PRODML) data objects and
their arrays in PostgreSQL, grouped in dataspaces, and speaks Energistics Transfer Protocol 1.2 (ETP) over WebSocket
instead of a REST API. OSDU Delivery reaches it through the route type `etp` (`docs/osdu-coverage-plan.md`, stage 7):
an ETP 1.2 client with the Avro binary encoding of the messages it uses. This brief is what that route type is built
from: the wire format, every message the route sends with its contract, and the server behaviour behind each message
as the pinned contract and the server's source show it.

## Sources

| Key | Source | Marking |
| --- | --- | --- |
| `P` | `osdu/specs/reservoir-ddms/etp-1.2.avpr`: the ETP 1.2 Avro schema, a JSON object with `namespace` `Energistics.Etp.v12`, `protocol` `Etp`, `version` `1.2` and a `types` array of 228 named Avro types. Message records carry the extra attributes `protocol` and `messageType` (both strings), `senderRole`, `protocolRoles`, `multipartFlag` and `depends`; there is no Avro `messages` block. It is `src/lib/oes/common/etp12/etp-1.2.avpr` of project 828 at the commit below, byte for byte (`osdu/specs/sources.json`). | Contract. Messages are cited by protocol and name below `Energistics.Etp.v12.Protocol.` (`[P: Core.RequestSession]`); data types by their name below `Energistics.Etp.v12.` (`[P: Datatypes.Object.Resource]`). |
| `R` | `osdu/specs/reservoir-ddms/README.md`, the server's `README.md` at the commit below, byte for byte. | Contract (service documentation). `[R: "section", lines]`. |
| `BP` | `osdu/specs/reservoir-ddms/best-practices-for-clients.md`, the server's `docs/bestPracticesForClients.md` at the commit below, byte for byte. | Contract (client guidance). `[BP: "section", lines]`. |
| `ST`, `EN`, `WF`, `PT` | `osdu/specs/core/storage/openapi.yaml`, `osdu/specs/core/entitlements/openapi.yaml`, `osdu/specs/core/workflow/openapi.yaml`, `osdu/specs/core/partition/openapi.yaml`: the core specification set (repository copy under `osdu/specs/core`). | Contracts of the core calls the server makes, or that registration in OSDU needs. `[ST: METHOD path, operationId]`. |
| `828` | GitLab project 828, `osdu/platform/domain-data-mgmt-services/reservoir/open-etp-server`, branch `main`, commit `7a22fe7e255a5dcc0d0b62bbabcfe4724e6dd32e` (the head of `main` on 2026-09-16, committed that day; the commit `P`, `R` and `BP` were taken from). Every file cited here was compared with that commit through the GitLab files API (SHA-256 of content) and matches. | Source. `[828 path:lines]`. Prefixes: `svr/` = `src/lib/oes/eml/etp12/`, `kv/` = `src/lib/oes/eml/openkv/`, `etp/` = `src/lib/oes/common/etp12/`, `epc/` = `src/lib/oes/common/epc/`, `auth/` = `src/lib/oes/common/auth/`, `oapi/` = `src/lib/oes/osdu/api/`, `clt/` = `src/lib/oes/client/` (the C++ client library and EPC importer), `bin/` = `src/bin/openETPServer/`, `tests/` = `src/tests/eml/`. |
| `1079` | GitLab project 1079, `osdu/platform/domain-data-mgmt-services/reservoir/open-etp-client` (TypeScript ETP client and the REST and GraphQL gateway), branch `main`, commit `4ef0d98170ceae1eab35b66726318f11b0a9f94f` (committed 2026-09-14; the head of `main` when the files were read on 2026-09-16; the next commit on `main` that day, `638999a9b68d0258d04db27eae072af7f53bc3e0`, is a merge recorded with `-s ours` and has the same tree). Every file cited here matches that commit (same check as for 828). | Source. `[1079 path:lines]`. Prefix `ts/` = `src/lib/`. The gateway's own OpenAPI document is cited as `[1079 openapi.json: METHOD path, operationId]`. |
| (inference) | A conclusion drawn from reading the code (C++ or TypeScript semantics, the interplay of several functions), or a recommendation that follows from the sources, not something a source states. | Marked `(inference)` or `(recommendation)` where it appears. |

Server messages are quoted as the source writes them; in a quote, `{name}` stands for a value the server fills in.
Nothing in this brief was observed against a live Reservoir DDMS deployment.

## 1. Base path, versions, headers and auth

### 1.1 Endpoint

| Item | Value | Basis |
| --- | --- | --- |
| Port and scheme | The server listens on port 9002 by default, plain `ws`. The server binary has TLS support only on its client side; deployments put it behind an ingress that terminates TLS. A client picks TLS from the URL scheme (`ws` or `wss`). | `[828 bin/ServerCmds.cpp:563-565]`; `[R: "SSL (TLS) Support", lines 408-416]` |
| Public URL | `wss://<host>/api/reservoir-ddms-etp/v2/`, with the trailing slash. The ingress routes the prefix and rewrites it to `/`. | GC: `[828 devops/gc/deploy/templates/virtual-service.yaml:33-42]`; core-plus: `[828 devops/core-plus/deploy/templates/virtual-service.yaml:18-27]`; Azure: `pathPrefix: /api/reservoir-ddms-etp/v2` `[828 devops/azure/chart/values.yaml:93]`, ingress path `{{ .pathPrefix }}/*` `[828 devops/azure/chart/templates/ingress.yaml:44]` with `appgw.ingress.kubernetes.io/backend-path-prefix: "/"` `[828 devops/azure/chart/values.yaml:50]`, and an Istio rewrite to `/` when `istioDnsHost` is set `[828 devops/azure/chart/templates/virtual-service.yaml:19-23]` |
| URL the project's own CI uses | `-S wss://${AZURE_DNS_NAME}/api/reservoir-ddms-etp/v2/`; the GC and core-plus pipelines use the same path | `[828 devops/azure/azure.gitlab-ci.yml:109-119]`; `[828 devops/gc/pipeline/override-stages.yml:16]`; `[828 devops/core-plus/pipeline/override-stages.yml:25]` |
| In-cluster URL | `ws://oetp-server:9002/`, as the REST gateway of project 1079 is configured next to the server in the GC chart | `[1079 devops/gc/deploy/values.yaml:13-15]`; `[1079 devops/gc/deploy/templates/configmap.yaml:27-29]`; `[1079 ts/common/config.ts:36-42]` (the URL is `${protocol}://${host}:${port}${path}/`) |
| WebSocket subprotocol | `etp12.energistics.org` | `[828 svr/Server.cpp:1391-1399]` |

### 1.2 Protocol, contract and versions

- **ETP 1.2.** The service API is ETP 1.2 and exchanges RESQML 2.0.1 and later, WITSML 2.1 and PRODML 2.2 and later
  `[R: "Introduction", lines 7-9]`. `P` is the schema of every message and data type.
- **Server identity** (in `OpenSession` and in the capabilities document of 1.5): `applicationName`
  `ReservoirDDMS - OSDU Reservoir DDMS ETP-1.2 Server`; `applicationVersion` `OSDU M27 - <git commit> (<commit time>)`,
  or `OSDU M27 - v1` when the build carries no git information `[828 kv/ServerConfig.cpp:367-373]`,
  `[828 svr/Server.cpp:2157-2181]`.
- **Protocols the server implements**, with the role and version it advertises
  `[828 kv/ServerConfig.cpp:298-309, 375-438]`, `[828 svr/Server.cpp:2190-2218]`; numbers from the generated enum
  `[828 etp/codegen/Messages.i.h:87-117]`:

  | Protocol | Number | Role | Version (major.minor.revision.patch) |
  | --- | --- | --- | --- |
  | Core | 0 | `server` | 1.2.0.0 |
  | Discovery | 3 | `store` | 1.2.0.0 |
  | Store | 4 | `store` | 1.2.0.0 |
  | DataArray | 9 | `store` | 1.2.0.0 |
  | Transaction | 18 | `store` | 1.2.0.0 |
  | Dataspace | 24 | `store` | 1.2.0.0 |
  | SupportedTypes | 25 | `store` | 1.2.0.0 |
  | CoreOSDU | 2400 | `store` | 1.2.1.0 |
  | StoreOSDU | 2404 | `store` | 1.2.1.0 |
  | DataspaceOSDU | 2424 | `store` | 1.2.1.0 |

  `P` also defines protocol 2100 (`Energistics.Etp.v12.PrivateProtocols.WitsmlSoap`) and the standard streaming,
  notification and query protocols; the server implements none of them.
- **Capabilities of the OSDU protocols.** For 2400, 2404 and 2424 the server's `protocolCapabilities` is a map from
  message name (response messages left out) to message type number, as a `DataValue` int
  `[828 kv/ServerConfig.cpp:97-115, 418-438]`. The other protocols advertise an empty map.
- **Formats, encodings, compression.** `supportedFormats` `["xml"]`, `supportedEncodings` `["binary"]`,
  `supportedCompression` `["gzip"]` `[828 svr/Server.cpp:2220-2223]`.
- **Data object types.** The server's configuration names none (nothing in the server code sets them), so it
  advertises its built-in list: `eml20.*`, `resqml20.*`, `resqml22.*`, `eml23.*`, `witsml21.*` and `prodml22.*`, each
  with `SupportsGet`, `SupportsPut` and `SupportsDelete` `[828 svr/Server.cpp:2225-2238]`,
  `[828 kv/ServerConfig.cpp:367-438]`. The session negotiation accepts fewer (3.2).
- **Endpoint capabilities advertised:** `MaxWebSocketMessagePayloadSize` (long), `MaxMessagePayloadUncompressedSize`
  (long, not an ETP standard name), `DataPartitionMode` and `DataConnectivityMode` (strings)
  `[828 svr/Server.cpp:2240-2268]`.

### 1.3 WebSocket upgrade and headers

The upgrade is validated in `[828 svr/Server.cpp:1336-1536]`.

| Header | Rule | Basis |
| --- | --- | --- |
| `Sec-WebSocket-Protocol: etp12.energistics.org` | Required. Otherwise the upgrade fails with HTTP 412 "Connection Refused: Client not requesting etp12.energistics.org subprotocol." | `[828 svr/Server.cpp:1391-1399]` |
| `Authorization: Bearer <token>` | Required in OSDU deployments. The server does not validate it (1.4); it forwards the value to Entitlements and Storage, and adds the prefix `Bearer` (with its space) when the value lacks it. | `[828 svr/Server.cpp:1409]`; `[828 bin/ServerCmds.cpp:274-323]`; `[828 oapi/BaseClient.cpp:172-187]` |
| `data-partition-id` | Required in `multiple` partition mode (`RDMS_DATA_PARTITION_MODE`, which the server accepts as `single` or `multiple` only), the mode the GC and core-plus charts and `devops/azure/oetp-server.values.yaml` set. The partition selects the database. When the header is absent, the server takes the partition from the authenticated user, but the fixed user of `--authN none` (1.4) carries no partition, so in OSDU deployments the header is the only source (inference). | `[828 svr/Server.cpp:1410, 2710-2715]`; `[828 bin/ServerCmds.cpp:155-163]`; `[828 kv/ServerConfig.cpp:153-163]`; `[828 kv/Repo.cpp:507-525]`; `[828 auth/AuthN.cpp:89-96]`; `[828 devops/gc/deploy/values.yaml:16-17]`; `[828 devops/core-plus/deploy/values.yaml:15-16]`; `[828 devops/azure/oetp-server.values.yaml:53-54]`; `[R: "Partition modes", lines 169-175]` |
| `etp-encoding` | Optional. The session is binary unless the value is `json` (which is not supported, 2.6). Send nothing or `binary`. | `[828 svr/Server.cpp:1408, 2906]` |
| `client-id` | Optional free text. It only labels server log lines, as `<applicationName> @ <client-id>`. Sending the delivery attempt id here ties server logs to the ledger (recommendation). | `[828 svr/Server.cpp:1406, 2422-2425]` |

- **Query-string fallback.** The same four keys (lower-cased) are read from the URL query string for browser
  clients; a header, when present, wins `[828 svr/Server.cpp:1412-1452]`. (recommendation) Never put the token in the
  URL: it would reach proxy logs, and this project forbids secrets in log lines.
- **Upgrade failures.** HTTP 412 for the subprotocol (above); HTTP 400 when the session factory throws
  `[828 svr/Server.cpp:1473-1483]`; HTTP 401 "Connection Refused: Cannot Create ETP Session." when no session is
  created `[828 svr/Server.cpp:1486-1500]`. At this commit the factory throws only for an empty partition mode
  `[828 kv/ServerConfig.cpp:153-157]`, which already stops the server at startup
  `[828 bin/ServerCmds.cpp:155-163, 457]`, so a running server does not answer 400 for a wrong partition (inference).
- **Unknown partition.** At upgrade time an unregistered partition is only logged
  `[828 kv/ServerConfig.cpp:165-171]`. It is resolved on the first database access of a request: the server lists
  partitions from the Partition service with its own service token `[PT: GET /partitions, list]`,
  `[PT: GET /partitions/{partitionId}, get]` (`[828 src/lib/oes/osdu/OsduConnectionProvider.cpp:37-67]`,
  `[828 oapi/PartitionClient.cpp:62-64, 96-98]`), and a partition it cannot find fails the request with
  `EINVALID_ARGUMENT` "Partition with name '{id}' not found in OSDU configuration." (or "... registration failed.")
  `[828 kv/Repo.cpp:507-606]`.
- **Open handshake timeout:** 30 s `[828 svr/Server.cpp:1227]`.
- **Message size cap.** The server sets WebSocketPP's maximum message size to its configured maximum; a larger
  message from the client makes WebSocketPP drop the connection `[828 svr/Server.cpp:1401-1404, 2240-2246]`.

### 1.4 Authentication and authorisation

- **Startup options in OSDU deployments:** `--authN none --authZ delegate=...` (Azure chart: `delegate=''`; Azure
  values file: `delegate=`; GC and core-plus: `delegate=$OSDU_HOST`)
  `[828 devops/azure/chart/templates/deployment.yaml:73]`, `[828 devops/azure/oetp-server.values.yaml:43]`,
  `[828 devops/gc/Dockerfile:78]`, `[828 devops/core-plus/Dockerfile:79]`, `[R: "OSDU Integration", lines 362-374]`.
- **No token validation in the server.** With `--authN none` and connectivity mode `osdu`, every caller becomes the
  fixed user `osdu_user`; the raw `Authorization` value is kept and used for the Entitlements and Storage calls
  `[828 bin/ServerCmds.cpp:274-323]`, `[828 auth/AuthN.cpp:89-96]`. The token is therefore judged only by those
  services (inference).
- **Where the server calls.** With an empty delegate, `STORAGE_URL` and `ENTITLEMENTS_URL` are used as given; with
  `delegate=<host>`, the base is `<host>` followed by `STORAGE_URL` (or `ENTITLEMENTS_URL`) when that value starts
  with `/`, otherwise `<host>/api/storage/v2` (or `<host>/api/entitlements/v2`)
  `[828 oapi/EntitlementUtils.cpp:101-119]`. Records go to `{storage}{RECORDS_PATH}`, `RECORDS_PATH` defaulting to
  `/records` `[828 src/lib/oes/osdu/OsduConfig.cpp:25-27]`. Every call carries `data-partition-id` and the caller's
  token `[828 oapi/BaseClient.cpp:196-229]`.
- **Caller's groups.** `GET {ENTITLEMENTS_URL}/groups` with the caller's token `[EN: GET /groups, listGroups]`
  (`[828 oapi/EntitlementClient.cpp:67, 149-188]`). The list is kept for the life of the session's entitlement object
  and has no expiry `[828 auth/Entitlement.cpp:159-173]`; `Authorize` (3.1) rebuilds that object
  `[828 svr/Server.cpp:2709-2716]`, so it also refreshes the groups (inference).
- **Service groups** `[828 auth/Entitlement.cpp:228-308]`:

  | Right | Group the caller must be in |
  | --- | --- |
  | Read anything (GetDataspaces, GetDataspaceInfo, and the read check inside every other call) | `service.reservoir-dms.viewers@<partition>.<domain>` |
  | Create, update, delete dataspaces, and the write check inside every write | `service.reservoir-dms.owners@<partition>.<domain>` |

- **Per-dataspace check.** Every access goes through `checkDataspaceAccess` on the dataspace; there are no
  object-level ACLs `[828 kv/BaseProto.cpp:179-295]`, and the dataspace is the unit of access control
  `[R: "Restrictions added on top of ETP specifications", line 17]`. The check:
  1. requires the viewers service group for any access, and the owners service group for write access
     (`EAUTHORIZATION_REQUIRED` "Service- User '{mail}' has no permissions to view dataspaces" or "... to modify
     dataspaces") `[828 kv/BaseProto.cpp:245-259]`;
  2. builds the dataspace's ACL from its `customData` (`viewers`, `owners`) `[828 kv/BaseProto.cpp:83-116]`, then
     reads the dataspace's Storage record `[ST: GET /records/{id}, getLatestRecordVersion]`
     (`[828 oapi/EntitlementClient.cpp:290-342]`): on 200 the record's `acl.viewers` and `acl.owners` replace that ACL
     `[828 oapi/EntitlementDataspace.cpp:136-162]`; on 404 the `customData` ACL stays (inference); any other status
     denies access `[828 auth/Entitlement.cpp:318-365]`;
  3. grants read when the caller is in one of the viewers or owners groups, and write when the caller is in one of
     the owners groups and the dataspace is not locked (`EAUTHORIZATION_REQUIRED` "Direct- User '{mail}' has no
     permissions to view|modify '{uid}' ({path}) space") `[828 oapi/EntitlementDataspace.cpp:164-178]`,
     `[828 kv/BaseProto.cpp:270-294]`.

  Results are cached in the session's dataspace cache for 15 minutes (`Cache- ...` messages)
  `[828 kv/AdminDB.cpp:198-235]`, `[828 kv/BaseProto.cpp:193-230]`. Without a valid entitlement (for example on a
  server started with `--authZ none`), every dataspace is readable and writable, except that a locked one refuses writes with
  `EINVALID_ARGUMENT` "Dataspace '{uid}' is locked." `[828 kv/BaseProto.cpp:232-243]`.
- **Authorization mode.** `AUTHORIZATION_MODE` is `osdu` unless set to `simple` `[828 src/lib/oes/osdu/OsduConfig.cpp:20-23]`,
  `[R: "Authorization delegation", lines 462-467]`. OSDU uses the groups-based path (`--authZ delegate=`), not the
  policy-based one (`--authZ policyDelegate=`) `[R: "Authentication delegation", lines 458-459]`,
  `[R: "Authorization delegation", line 463]`; this brief describes the groups-based path.
- **What the delivery identity needs** (inference from the above and `ST`): both service groups; membership of every
  group it lists in a dataspace's `owners` (4.3); Storage rights to create and read the dataspace record
  `[ST: PUT /records, createOrUpdateRecords]`, `[ST: GET /records/{id}, getLatestRecordVersion]`; and, for any call
  that makes the server remove that record (4.3, 7.8), the right to purge it, which `ST` limits to
  `service.storage.admin` members who own the record `[ST: DELETE /records/{id}, purgeRecord]`.

### 1.5 Other HTTP routes on the port

The server answers plain HTTP `GET` on the same port; any other method gets 405
`[828 svr/Server.cpp:1864-2046]`:

| Path | Answer |
| --- | --- |
| `/health`, `/health/liveness`, `/health/liveness_check`, `/health/readiness`, `/health/readiness_check` | 200 "Ready for {path}" |
| `/health/startup` | 200 when the database pool is healthy, else 503 |
| `/metrics` | Metrics registry |
| `/info` | JSON with `version`, `commitId`, `commitTime`, `buildTime` |
| `/refresh-partitions` | Re-reads partitions (multiple partition mode only; checked against the authN delegate's `/service` route when one is set) |
| `/.well-known/etp-server-capabilities?GetVersions=true` | `["etp12.energistics.org"]` |
| `/.well-known/etp-server-capabilities?GetVersion=etp12.energistics.org` | The `ServerCapabilities` record `[P: Datatypes.ServerCapabilities]` as Avro JSON; any other query gets 501; any other path gets 404 |

## 2. Framing and encoding

### 2.1 One ETP message per WebSocket message

- Each ETP message is one WebSocket message with the `BINARY` opcode: the Avro binary encoding of the
  `MessageHeader`, immediately followed by the Avro binary encoding of the body record chosen by
  (`protocol`, `messageType`). There is no container, no schema fingerprint and no length prefix beyond the
  WebSocket message itself `[828 etp/EncoderSender.hpp:150-166, 253-256]`, `[828 svr/Server.cpp:3299-3310, 3494-3500]`.
- The server and the C++ client share this sender (`[828 svr/Server.cpp:3034-3043]`, `[828 clt/Client.cpp:681-683]`),
  which encodes with `avro::binaryEncoder()` wrapped in a pass-through encoder that writes large float and double
  vectors with a "wire-compatible but faster" path `[828 etp/EncoderSender.hpp:128-137]`. The TypeScript client
  uses a hand-written, schema-driven reader and writer `[1079 ts/common/EtpAvro.ts:50-670]` and frames messages the
  same way `[1079 ts/client/ETPClient.ts:311-339, 427-445]`.

### 2.2 Message header, flags and ids

`[P: Datatypes.MessageHeader]`, fields in wire order:

| Field | Avro type | Meaning |
| --- | --- | --- |
| `protocol` | int | Protocol number (4.1) |
| `messageType` | int | Message type number within the protocol |
| `correlationId` | long | 0 on a new request; on a response, the request's `messageId` (2.3) |
| `messageId` | long | Sender's id |
| `messageFlags` | int | Bit set below |

Flags `[828 etp/MsgFlags.h:28-89]`:

| Bit | Name | Meaning |
| --- | --- | --- |
| `0x02` | FIN | Final (or only) part of a message |
| `0x08` | COMPRESSED_BODY | The body is compressed (2.5) |
| `0x10` | ACKNOWLEDGE_RECEIPT | The sender asks for an `Acknowledge` |
| `0x20` | HEADER_EXTENSION | An extension header follows the header (not supported, 2.6) |
| `0x01`, `0x04` | none | No longer part of ETP 1.2 |

Rules the server enforces on every client message, in this order `[828 svr/Server.cpp:3278-3500]`:

1. A session that received `CloseSession` answers anything else with `EINVALID_STATE` "Unable to handle websocket
   message. Session is about to close." (lines 3281-3289, 3314-3317).
2. An undecodable header gives `EINVALID_MESSAGE` (lines 3299-3306).
3. The `Acknowledge` requested by flag `0x10` is sent now, before the checks that follow (lines 3331-3338).
4. An unknown (protocol, type) pair gives `EINVALID_MESSAGETYPE` "Unknown message type" (lines 3340-3345).
5. `messageId` must be even: otherwise `EINVALID_ARGUMENT` "Invalid Message-ID: Not Even" (lines 3351-3357).
6. `messageId` must be greater than the last one received in this session, whatever the protocol: otherwise
   `EINVALID_ARGUMENT` "Invalid Message-ID: Not monotonically increasing" (lines 3359-3370).
7. Compression checks (2.5, lines 3372-3417), then the extension flag (2.6, lines 3419-3425).
8. A `ProtocolException` sent by the client is logged and ignored (lines 3427-3451).
9. A protocol that was not negotiated gives `EUNSUPPORTED_PROTOCOL` "Unsupported protocol: {n}" (lines 3465-3472);
   Core and CoreOSDU are always served `[828 svr/Server.cpp:2054-2080]`.
10. A message that is not a request of that protocol gives `EINVALID_MESSAGETYPE` "Unexpected message: ..."
    (lines 3474-3484).
11. A message whose record has `multipartFlag: false` in `P` must carry FIN: otherwise `EINVALID_ARGUMENT`
    "Non-Multi-Part request lacks FIN bit" (lines 3486-3492).
12. An undecodable body gives `EINVALID_MESSAGE` "Cannot Avro-decode request: ..." (lines 3494-3500).

- **Ids.** Client ids are even and start at 2; server ids are odd and start at 1 `[828 etp/Messages.h:70-93]`. The
  TypeScript client adds 2 per header `[1079 ts/common/ETPCore.ts:130-147]`.
- **FIN on requests.** Among the requests this route sends, only `Store.PutDataObjects` and `Store.Chunk` have
  `multipartFlag: true` in `P` (every other request has `false`). Both reference clients set FIN on every request
  that is not part of a chunk sequence, including an ordinary `PutDataObjects`
  (`[1079 ts/protocols/StoreCustomer.ts:340-344]`, `[1079 ts/common/ETPCore.ts:150-162]`;
  `[828 clt/Client.hpp:82-115]`). The server does not look at FIN on `PutDataObjects`; it decides chunking from
  `blobId` (4.5) `[828 kv/StoreProto.cpp:782-819]`.

### 2.3 Correlation of responses

- Every server reply carries `correlationId` = the request's `messageId`, and the request's `protocol` number in its
  header, including a `ProtocolException` or an `Acknowledge` sent in reply to it
  `[828 svr/Server.cpp:3212-3233, 3252-3262, 3619-3642]`. The `PutDataObjectsResponse` that ends a chunked put carries
  the original `PutDataObjects` id (lines 3628-3639).
- A reply may be multi-part: several messages with the same `correlationId`, FIN on the last only. The server splits
  when a part would exceed the session's size limit or its batch count `[828 svr/MultiPartReply.hpp:98-106, 231-282]`,
  and its sender also splits an oversized encoded message, clearing FIN on all parts but the last
  `[828 etp/EncoderSender.hpp:161-189]`.
- An auxiliary message (for example `Discovery.GetResourcesEdgesResponse`) goes out before the main part, without FIN
  `[828 svr/Server.cpp:3659-3685]`.
- Collect every message with the request's id until one carries FIN (8.1 gives the outcome rules).

### 2.4 Avro binary encoding

Standard Avro binary encoding, as the TypeScript writer and reader implement it `[1079 ts/common/EtpAvro.ts]`:

| Avro type | Encoding | TypeScript basis |
| --- | --- | --- |
| `int`, `long` | zig-zag variable-length integers | writer lines 589-630; reader lines 193-249 |
| `float`, `double` | IEEE 754, little-endian, 4 and 8 bytes | writer lines 632-642; reader lines 251-261 |
| `boolean` | one byte, 0 or 1 | writer lines 585-587; reader lines 189-191 |
| `bytes`, `string` | length (long) then the bytes; strings in UTF-8 | writer lines 644-658; reader lines 268-277 |
| `fixed` | the raw bytes | writer lines 442-448; reader lines 262-266 |
| `enum` | the symbol's index as an int | writer lines 423-441; reader lines 278-280 |
| union | the branch index as an int, then the value | writer lines 519-547; reader lines 143-157 |
| `array`, `map` | one or more blocks, each a count then the items (a map item is the key string then the value), ended by a zero count; a reader must also accept a negative count followed by the block's byte size | writer lines 449-518; reader lines 130-142, 180-187, 293-308 |
| record | the fields in schema order, no names | writer lines 417-422; reader lines 122-129 |

- **Bulk numbers.** An `ArrayOfDouble` or `ArrayOfFloat` can be written as one block: the count, the raw
  little-endian bytes, then 0 `[1079 ts/common/EtpAvro.ts:462-485]`.
- **Union branches and enum ordinals** the route uses (computed from `P`):

  | Type | Order |
  | --- | --- |
  | `[P: Datatypes.DataValue]` `item` | 0 `null`, 1 `boolean`, 2 `int`, 3 `long`, 4 `float`, 5 `double`, 6 `string`, 7 `ArrayOfBoolean`, 8 `ArrayOfNullableBoolean`, 9 `ArrayOfInt`, 10 `ArrayOfNullableInt`, 11 `ArrayOfLong`, 12 `ArrayOfNullableLong`, 13 `ArrayOfFloat`, 14 `ArrayOfDouble`, 15 `ArrayOfString`, 16 `ArrayOfBytes`, 17 `bytes`, 18 `AnySparseArray` (the server's own log text agrees: "Expected string (6) or ArrayOfString (15)" `[828 kv/BaseProto.cpp:78-79]`) |
  | `[P: Datatypes.AnyArray]` `item` | 0 `ArrayOfBoolean`, 1 `ArrayOfInt`, 2 `ArrayOfLong`, 3 `ArrayOfFloat`, 4 `ArrayOfDouble`, 5 `ArrayOfString`, 6 `bytes` |
  | `[P: Datatypes.AnyArrayType]` | 0 `arrayOfBoolean`, 1 `arrayOfInt`, 2 `arrayOfLong`, 3 `arrayOfFloat`, 4 `arrayOfDouble`, 5 `arrayOfString`, 6 `bytes` |
  | `[P: Datatypes.AnyLogicalArrayType]` | 0 `arrayOfBoolean`, 1 `arrayOfInt8`, 2 `arrayOfUInt8`, 3 `arrayOfInt16LE`, 4 `arrayOfInt32LE`, 5 `arrayOfInt64LE`, 6 `arrayOfUInt16LE`, 7 `arrayOfUInt32LE`, 8 `arrayOfUInt64LE`, 9 `arrayOfFloat32LE`, 10 `arrayOfDouble64LE`, 11 `arrayOfInt16BE`, 12 `arrayOfInt32BE`, 13 `arrayOfInt64BE`, 14 `arrayOfUInt16BE`, 15 `arrayOfUInt32BE`, 16 `arrayOfUInt64BE`, 17 `arrayOfFloat32BE`, 18 `arrayOfDouble64BE`, 19 `arrayOfString`, 20 `arrayOfCustom` |
  | `[P: Datatypes.Object.ActiveStatusKind]` | 0 `Active`, 1 `Inactive` |
  | `[P: Datatypes.Object.ContextScopeKind]` | 0 `self`, 1 `sources`, 2 `targets`, 3 `sourcesOrSelf`, 4 `targetsOrSelf` |
  | `[P: Datatypes.Object.RelationshipKind]` | 0 `Primary`, 1 `Secondary`, 2 `Both` |
  | Nullable fields (`[null, X]`: `Resource.sourceCount`, `Resource.targetCount`, `DataObject.blobId`, `GetResources.storeLastWriteFilter`, `GetResources.activeStatusFilter`, `GetDataspaces.storeLastWriteFilter`, `ProtocolException.error`) | 0 `null`, 1 the value |

- **Enums that are not on the wire.** No field in `P` references `[P: Datatypes.Protocol]` or the three
  `*CapabilityKind` enums (checked by parsing `P`): protocol numbers travel as plain ints and capability names as map
  keys. Do not use the ordinals of `Datatypes.Protocol` as protocol numbers: its symbols `CoreOSDU`, `StoreOSDU`,
  `DataspaceOSDU` sit at ordinals 26 to 28, while their wire numbers are 2400, 2404 and 2424
  `[828 etp/codegen/Messages.i.h:87-117]`.
- **UUID byte order.** `[P: Datatypes.Uuid]` is a `fixed` of 16 bytes. The server copies the 16 bytes to and from its
  GUID as they are and prints them in order as the textual UUID `[828 src/lib/oes/common/AvroUtils.cpp:31-39]`,
  `[828 src/lib/oes/core/utils/Guid.cpp:664-691]`: the wire order is the RFC 4122 textual (big-endian) order.
  (inference) .NET's `System.Guid` default byte layout is mixed-endian, so convert explicitly.
- **Timestamps.** `currentDateTime`, `storeCreated`, `storeLastWrite` and `lastChanged` are microseconds since the Unix
  epoch, UTC `[828 svr/Server.cpp:261-281]`, `[828 kv/StoreProto.cpp:548-552, 605-607]`,
  `[828 kv/DiscoProto.cpp:632-637]`.

### 2.5 Compression

- **Negotiation.** The client lists algorithms in `RequestSession.supportedCompression`; the server takes the first
  entry it also supports (only `gzip`) and returns it as the single string `OpenSession.supportedCompression`, or an
  empty string when either side offers none `[828 svr/Server.cpp:2567-2585]`, `[P: Core.OpenSession]`.
- **What is compressed.** Only the body; the header is never compressed. The server inflates a body flagged `0x08`;
  without negotiated compression the answer is `ECOMPRESSION_NOTSUPPORTED` "Compression not negotiated"; a body that
  does not inflate gives `EINVALID_MESSAGE` "Could not decompress body" `[828 svr/Server.cpp:3372-3417]`.
- **Format.** zlib deflate with window bits `15 | 16`, that is the gzip (RFC 1952) wrapper; the server deflates at
  `Z_BEST_SPEED` `[828 src/lib/oes/common/CompressUtils.c:211-212, 448-481]`,
  `[828 src/lib/oes/common/CompressUtils.hpp:59-66]`.
- **What the server compresses once gzip is negotiated** `[828 etp/EncoderSender.hpp:114-126, 202-215]`: every body
  of 256 bytes or more, except `Acknowledge` (message type 1001 on any protocol), `Core.OpenSession` (0/2) and a
  `ProtocolException` whose header protocol is 0. A `ProtocolException` in reply to a Store, DataArray or other
  request carries that request's protocol number (2.3), so it can arrive compressed (inference). A client that
  offers gzip must inflate any message flagged `0x08`; the C++ client does `[828 clt/Client.cpp:903-949]`.
- **Client default.** Compression is opt-in on the client side ("On client-side, compression is opt-in, while on
  server-side, compression is always active, BUT depends on client") `[828 bin/EtpClient12.cpp:349-351]`; the
  TypeScript client offers none `[1079 ts/client/ETPClient.ts:386]`.

### 2.6 Options the server does not support

- **Header extensions.** Flag `0x20` gives `ENOTSUPPORTED` "MessageHeaderExtension not supported yet"
  `[828 svr/Server.cpp:3419-3425]`.
- **JSON encoding.** The server advertises `binary` only `[828 svr/Server.cpp:2221]` and always encodes binary
  `[828 etp/EncoderSender.hpp:128-137]`; the TypeScript client refuses JSON ("JSON message no longer supported, only
  binary is supported") `[1079 ts/common/ETPCore.ts:350-368]`. Use binary only.

## 3. Session

### 3.1 `Core.Authorize` (0/6), answered by `Core.AuthorizeResponse` (0/7)

- **Fields:** `authorization: string`, `supplementalAuthorization: map<string>`; the response has
  `success: boolean` and `challenges: array<string>` `[P: Core.Authorize]`, `[P: Core.AuthorizeResponse]`.
- **Server handling.** The server replaces the session's authorization value, rebuilds the user and the entitlement
  object from it, and answers `success=true`. A failure of either factory comes back as a `ProtocolException` with
  `EAUTHORIZATION_REQUIRED` and the factory's message; other exceptions as `EINVALID_STATE` "Generic Exception: ..."
  `[828 svr/Server.cpp:349-385, 2689-2734]`. `challenges` is never filled.
- **Reference client use.** The C++ client sends `Authorize` with the configured `Authorization` value before
  `RequestSession` whenever one is configured `[828 clt/Client.cpp:1447-1490]`.
- **Token refresh** (inference). Because `Authorize` replaces the value later used for Entitlements and Storage, it is
  the in-band way to refresh a bearer token on a long session. The server's test guide warns that imports take time
  and "you may need to refresh your token" `[828 docs/testing.md:61]`.
- **Without `Authorize`.** `RequestSession` authenticates from the upgrade's `Authorization`; when no user results,
  the error is `EAUTHORIZATION_REQUIRED` "Authorization has not been provided." `[828 svr/Server.cpp:2361-2372]`.

### 3.2 `Core.RequestSession` (0/1)

Fields in wire order `[P: Core.RequestSession]`, with what the server does with each `[828 svr/Server.cpp:2350-2603]`:

| Field | Avro type | What to send | Server behaviour |
| --- | --- | --- | --- |
| `applicationName` | string | `OSDU Delivery` | Logged; the session's log label (lines 2375-2380, 2422-2425) |
| `applicationVersion` | string | Build version | Logged |
| `clientInstanceId` | `Uuid` | Random per process | Logged (lines 2381-2390) |
| `requestedProtocols` | array of `SupportedProtocol` | See below | Each entry checked (lines 2429-2505) |
| `supportedDataObjects` | array of `SupportedDataObject` | At least one `qualifiedType` containing `resqml20.` or `eml20.` | Only entries whose `qualifiedType` contains `resqml20.` or `eml20.` are echoed, with their capabilities; if none: `ENOSUPPORTEDDATAOBJECTTYPES` (29) "None of the requested dataobject types are supported." This applies to WITSML-only work too (lines 2395-2415). |
| `supportedCompression` | array of string | `["gzip"]` or `[]` | 2.5 |
| `supportedFormats` | array of string | `["xml"]` | The server answers `["xml"]` whatever is sent (line 2417) |
| `currentDateTime` | long | Now, in microseconds | Logged; `OpenSession.currentDateTime` is the server's time (lines 261-281, 319-321) |
| `earliestRetainedChangeTime` | long | Now, in microseconds | Not used |
| `serverAuthorizationRequired` | boolean | `false` | Not used |
| `endpointCapabilities` | map of `DataValue` | `MaxWebSocketMessagePayloadSize` as a long | Negotiated (below) |

`[P: Datatypes.SupportedProtocol]` is `protocol: int`, `protocolVersion: Version`, `role: string`,
`protocolCapabilities: map<DataValue>`; `[P: Datatypes.Version]` is `major`, `minor`, `revision`, `patch` (ints).
Rules the server applies to each requested entry (lines 2429-2505):

- **Unsupported protocols** are ignored (lines 2438-2441).
- **Roles.** Core (0) must be requested with role `server`; every other protocol with role `store`. An entry with the
  wrong role is ignored with an error log (lines 2443-2458); the protocol factory only builds `store` roles
  `[828 kv/ServerConfig.cpp:314-365]`.
- **Duplicates** are ignored (lines 2460-2472).
- **Versions.** `major` and `minor` must equal the server's (1 and 2 for every protocol). The `revision` and `patch`
  conditions (lines 2479-2480) only reject a negative client value that is greater than the server's value, which
  cannot happen while the server advertises 0 or 1, so in effect only `major` and `minor` are compared (inference).
  The C++ client requests revision 1 for 2404 and 2424 and 1.2.0.0 for the rest `[828 clt/Client.cpp:1535-1553]`.
- **Capabilities.** The server copies its own `protocolCapabilities` into each accepted entry
  `[828 svr/Server.cpp:2048-2052]` (section 1.2).
- **Nothing accepted:** `ENOSUPPORTEDPROTOCOLS` (2) "None of the requested protocols are supported." (lines 2498-2505).
- **What the route requests.** Core 0, Discovery 3, Store 4, DataArray 9, Transaction 18, Dataspace 24 and
  DataspaceOSDU 2424. The C++ client requests Core, Dataspace, Discovery, SupportedTypes, Store, StoreOSDU, DataArray,
  Transaction and DataspaceOSDU `[828 clt/Client.h:89-99]`, and declares `eml20.*` and `resqml20.*` without
  capabilities `[828 clt/Client.cpp:1564-1565]`. The TypeScript client declares `witsml21.*`, `resqml20.*`,
  `resqml22.*`, `eml20.*` and `eml23.*`, each with `SupportsGet`, `SupportsPut` and `SupportsDelete` as boolean
  `DataValue`s `[1079 ts/client/ETPClient.ts:367-394]`.

**`MaxWebSocketMessagePayloadSize`** (lines 2509-2555):

- The session limit is the smaller of the client's value and the server's configured maximum. The client's value may
  be a `DataValue` long (branch 3) or int (branch 2); any other type is logged and ignored. Without a client value the
  server maximum applies.
- The session limit is echoed in `OpenSession.endpointCapabilities`, and the server sizes its replies with it
  `[828 svr/MultiPartReply.hpp:98-106]`.
- The C++ client sends a long `[828 clt/Client.cpp:1561-1562]`; the TypeScript client sends a long, 10,000,000 bytes by
  default `[1079 ts/client/ETPClient.ts:396-402]`, `[1079 ts/client/ResqmlClient.ts:342-349]`.

### 3.3 `Core.OpenSession` (0/2)

- **Fields in wire order** `[P: Core.OpenSession]`: `applicationName`, `applicationVersion`, `serverInstanceId`
  (`Uuid`), `supportedProtocols`, `supportedDataObjects`, `supportedCompression` (a single string),
  `supportedFormats`, `currentDateTime`, `earliestRetainedChangeTime`, `sessionId` (`Uuid`), `endpointCapabilities`.
- **What the server returns:** its name and version (section 1.2), the accepted protocols with the server's
  capabilities, the echoed data object types, `supportedFormats` `["xml"]`, the server instance id and the session
  id, the negotiated compression and `endpointCapabilities["MaxWebSocketMessagePayloadSize"]`
  `[828 svr/Server.cpp:2392-2425, 2491, 2547-2550, 2580]`.
- **Failure.** `RequestSession` failures come back as a `ProtocolException` correlated to the request; unexpected
  exceptions as `EINVALID_STATE` "Generic Exception: ..." `[828 svr/Server.cpp:285-324]`.

### 3.4 Keep-alive, close, dropped connections and `ResumeSession`

- **Ping.** `Core.Ping` (0/8) is answered by `Core.Pong` (0/9); both carry `currentDateTime` (microseconds)
  `[P: Core.Ping]`, `[P: Core.Pong]`, `[828 svr/Server.cpp:387-404]`. The REST gateway pings every 30 s on the sessions
  that hold a transaction `[1079 ts/restApi/ControllerUtils.ts:94, 912-920]`.
- **CloseSession.** `Core.CloseSession` (0/5) has one field, `reason: string` `[P: Core.CloseSession]`. The server
  sends no response message (the handler completes without one) `[828 svr/Server.cpp:326-347]`. Once the header is
  decoded, the session refuses further messages (2.2); the server waits up to 5 s for requests still in flight, then
  closes the WebSocket with status "going away" `[828 svr/Server.cpp:2799-2866]`. A client that wants a receipt sets
  `0x10`; the C++ client waits for the `Acknowledge` only when acknowledgements are configured
  `[828 clt/Client.cpp:441-516, 673-675]`.
- **Uncommitted transactions** end with the session: the PostgreSQL transaction rolls back in its destructor
  `[828 src/lib/oes/postgresql/Transaction.cpp:80-91]`, and the close log says "(WITH ROLLBACK)"
  `[828 svr/Server.cpp:2863-2865]`.
- **Dropped connections.** When the socket closes without `CloseSession`, the server parks the session object,
  keyed by its session id, for `SESSION_KEEP_ALIVE_DURATION_SECONDS` (120 s by default; 0 disables parking)
  `[828 src/lib/oes/eml/OpenETPServerConfig.cpp:43-59]`, `[828 svr/Server.cpp:1790-1848]`. A parked session keeps its
  transaction object, and a dataspace leaves the process-wide set of dataspaces being written only when that object is
  destroyed `[828 kv/Session.cpp:320-347]`, `[828 kv/Session.h:118-125, 181]`. So after a drop, a new writer of the same
  dataspace can get `EMAX_TRANSACTIONS_EXCEEDED` for up to the keep-alive period (inference).
- **ResumeSession.** `CoreOSDU.ResumeSession` (2400/1), answered by `CoreOSDU.ResumeSessionResponse` (2400/2), carries
  the old `sessionId` `[P: CoreOSDU.ResumeSession]`, `[P: CoreOSDU.ResumeSessionResponse]`. An unknown or expired id gives
  `ENOT_FOUND` "Session not found or expired" `[828 svr/Server.cpp:2605-2644]`. The OpenKV session moves the parked
  session's transaction, pending chunked puts and dataspace cache into the new session
  `[828 kv/Session.cpp:414-426]`, but the generic part leaves the protocol state behind (the line that would copy it
  is commented out) `[828 svr/Server.cpp:2646-2682]`, and `onResume` neither marks the new session open nor re-runs the
  negotiated-state setup that `RequestSession` performs (`[828 svr/Server.cpp:2587-2598]` has no counterpart in
  lines 2605-2644) (inference). The C++ client implements resume `[828 clt/Client.cpp:1727-2000]`, but only the
  server's tests call it `[828 tests/Etp12ServerTests.cpp:101-220]`, and those check session recovery and expiry, not
  a transaction. The C++ importer reconnects with a fresh session instead `[828 clt/ImportEpc.cpp:1280-1300]`.
  (recommendation) Do not rely on resume: reconnect, open a new session, and replay the transaction.

## 4. The calls a writer makes

### 4.1 Message numbers

All from `P` (`protocol` and `messageType` attributes of each record):

| Protocol (number) | Request (type) | Response (type) |
| --- | --- | --- |
| Core (0) | `RequestSession` (1) | `OpenSession` (2) |
| Core (0) | `CloseSession` (5) | none |
| Core (0) | `Authorize` (6) | `AuthorizeResponse` (7) |
| Core (0) | `Ping` (8) | `Pong` (9) |
| any (2.3) | none | `Core.ProtocolException` (1000), `Core.Acknowledge` (1001) |
| Discovery (3) | `GetResources` (1) | `GetResourcesResponse` (4), `GetResourcesEdgesResponse` (7) |
| Store (4) | `GetDataObjects` (1) | `GetDataObjectsResponse` (4), `Chunk` (8) |
| Store (4) | `PutDataObjects` (2), then `Chunk` (8) | `PutDataObjectsResponse` (9) |
| Store (4) | `DeleteDataObjects` (3) | `DeleteDataObjectsResponse` (10) |
| DataArray (9) | `GetDataArrays` (2) | `GetDataArraysResponse` (1) |
| DataArray (9) | `GetDataSubarrays` (3) | `GetDataSubarraysResponse` (8) |
| DataArray (9) | `PutDataArrays` (4) | `PutDataArraysResponse` (10) |
| DataArray (9) | `PutDataSubarrays` (5) | `PutDataSubarraysResponse` (11) |
| DataArray (9) | `GetDataArrayMetadata` (6) | `GetDataArrayMetadataResponse` (7) |
| DataArray (9) | `PutUninitializedDataArrays` (9) | `PutUninitializedDataArraysResponse` (12) |
| Transaction (18) | `StartTransaction` (1) | `StartTransactionResponse` (2) |
| Transaction (18) | `CommitTransaction` (3) | `CommitTransactionResponse` (5) |
| Transaction (18) | `RollbackTransaction` (4) | `RollbackTransactionResponse` (6) |
| Dataspace (24) | `GetDataspaces` (1) | `GetDataspacesResponse` (2) |
| Dataspace (24) | `PutDataspaces` (3) | `PutDataspacesResponse` (6) |
| Dataspace (24) | `DeleteDataspaces` (4) | `DeleteDataspacesResponse` (5) |
| DataspaceOSDU (2424) | `GetDataspaceInfo` (1) | `GetDataspaceInfoResponse` (2) |
| DataspaceOSDU (2424) | `LockDataspaces` (5) | `LockDataspacesResponse` (6) |

Note the DataArray numbering: the request `GetDataArrays` is 2 and its response is 1.

### 4.2 URIs

Parser and printer: `[828 etp/EmlUri.cpp]`.

- **Dataspace URI:** `eml:///dataspace('<path>')` (printer lines 436-447). The legacy form without quotes,
  `eml:///dataspace(<path>)`, is still accepted by the parser (lines 88-112). `eml:///` alone is the root dataspace
  (lines 74-81).
- **Object URI:** `eml:///dataspace('<path>')/<ml>.<type>(<uuid>)` (printer lines 385-419). The parser also accepts
  `(uuid=<uuid>,version='<v>')` and the legacy `(<uuid>,<v>)` (lines 173-236). It recognises the MLs `eml20`, `eml23`,
  `resqml20`, `resqml22`, `witsml21` and `prodml22`; any other prefix parses as an unknown ML rather than failing
  (lines 248-265), and only those six MLs resolve in references (5.1).
- **Type names.** RESQML 2.0.1 and EML 2.0 type names carry the `obj_` prefix, for example
  `resqml20.obj_MdDatum` and `eml20.obj_EpcExternalPartReference` (lines 455-470); RESQML 2.2 and EML 2.3 names do not.
  The type in a URI is compared verbatim with the stored type (5.1, 7.3).
- **Percent-encoding.** The parser percent-decodes the whole URI first (line 68). The printer keeps alphanumerics and
  ``; , / ? : @ & = + $ - _ . ! ~ * ` ( ) #`` and percent-encodes everything else (lines 472-500).
- **No default dataspace.** The server refuses to create or delete the root: "Cannot add default space",
  "Cannot delete default space" `[828 kv/SpaceProto.cpp:325-329, 772-775]`,
  `[R: "Restrictions added on top of ETP specifications", line 17]`.
- (recommendation) Always send the printer's canonical form. The per-dataspace write lock compares URI strings
  (4.4), so a legacy or differently encoded form of the same dataspace does not meet the lock another writer holds
  (inference from `[828 kv/TransProto.cpp:106-121]`, `[828 kv/Session.cpp:333-337]`, `[828 kv/Session.h:123-125]`).

### 4.3 Create the dataspace: `Dataspace.PutDataspaces` (24/3)

**Request** `[P: Dataspace.PutDataspaces]`: `dataspaces: map<Dataspace>`; the map key is a client id echoed in the
reply. `[P: Datatypes.Object.Dataspace]` is `uri: string`, `path: string` (default ""), `storeLastWrite: long`,
`storeCreated: long`, `customData: map<DataValue>`.

**Server rules** `[828 kv/SpaceProto.cpp:238-647]`:

- Empty map: `EINVALID_ARGUMENT` "Nothing to add" (lines 254-267).
- The caller must be in the owners service group (1.4): otherwise `EREQUEST_DENIED` "Not authorized to PutDataspaces"
  (lines 269-302).
- An empty `uri` with a `path` is replaced by the dataspace URI of the path (lines 318-321). The URI's dataspace string
  must equal `path`, or be a non-nil GUID: otherwise `EINVALID_ARGUMENT` "Space URI neither matches PATH, nor uses
  UUID" or "Space URI uses all-zero UUID" (lines 346-364).
- **Path rules** `[828 kv/AdminDB.cpp:263-326]`, failures as `EINVALID_ARGUMENT` "Invalid space path: '{path}': {reason}"
  (lines 366-379): not empty; no backslash; at most one `/`; at least 3 characters, all matching
  `^[A-Za-z0-9_/\-\.]{3,}$`; not ending with `/`. A single segment `x` is stored with the path `x/default` (its uid
  stays `x`). `R` asks for two-level paths ("Two level path for dataspace id: example (project/scenario)")
  `[R: "Restrictions added on top of ETP specifications", line 18]`, `[R: "Data Spaces", line 406]`.
  (recommendation) Always send `project/study` with URI and path equal.
- **Create only.** An existing uid or path fails with `EINVALID_ARGUMENT` "Space already exists: URI conflict with
  {uid}" or "Space already exists: PATH conflict with {path}" (lines 382-392). Idempotency is the caller's job: check
  with `GetDataspaceInfo` first (7.1).
- **Inside a transaction** the dataspace must be one of `StartTransaction.dataspaceUris`: otherwise `EREQUEST_DENIED`
  "Writing to dataspaces not specified in `StartTransaction` is not allowed" (lines 331-336). A dataspace created
  inside a transaction becomes visible to other sessions at commit `[828 tests/openkv/TransProtoTests.cpp:166-189]`.
- **Legal and ACL in `customData`.** Required since M25 `[BP: "Creation of Dataspace", lines 10-13]`,
  `[R: "OSDU Integration", line 374]`:
  - keys `viewers`, `owners`, `legaltags`, `otherRelevantDataCountries`;
  - each value is a `DataValue` holding a `string` (branch 6) or an `ArrayOfString` (branch 15); a string that looks
    like a JSON array (`["a","b"]`) is parsed as a list `[828 kv/BaseProto.cpp:47-81]`;
  - in `AUTHORIZATION_MODE=osdu` (the default) all four must be non-empty: otherwise `EREQUEST_DENIED` "Could not
    register dataspace {uri}: one or more required fields are empty. Provide all of: viewers, owners, legaltags,
    otherRelevantDataCountries" `[828 auth/Entitlement.cpp:418-436]`, `[828 src/lib/oes/common/ErrorMessages.h]`,
    `[828 kv/SpaceProto.cpp:462-479]`;
  - the caller must be a member of every group listed in `owners`: otherwise "... you are not a member of the required
    owner group for this dataspace" `[828 oapi/EntitlementDataspace.cpp:185-199]`, `[828 auth/Entitlement.cpp:407-416]`;
  - examples from the project's own tests: `viewers` `["data.default.viewers@<partition>.<domain>"]`, `owners`
    `["data.default.owners@<partition>.<domain>"]`, `legaltags` a legal tag name (string or list),
    `otherRelevantDataCountries` `["US"]` `[828 devops/azure/azure.gitlab-ci.yml:121]`,
    `[828 devops/core-plus/pipeline/override-stages.yml:35]`, `[1079 bruno/environments/CI.bru:18-20]`,
    `[1079 bruno/Dataspaces/Create Dataspace.bru:18-31]`.

  These values are exactly a flow's legal tags and ACLs, which this project never changes without an explicit request.
- **The Storage record** (6.3). Before creating the dataspace's schema, the server registers a
  `dataset--ETPDataspace` record in Storage with the caller's token `[828 kv/SpaceProto.cpp:454-479]`,
  `[828 auth/Entitlement.cpp:391-463]`. It first reads the record id: 200 means a record already exists, which the
  server removes (7.8, a purge) and then re-creates; 403 fails with "a dataspace with this name already exists and you
  do not have access to it"; 404 proceeds (lines 438-451).
- **Not transactional against Storage** (inference). The record is written by an HTTP call while the schema and the
  admin rows are written on the database connection, so rolling back a transaction that created a dataspace leaves
  the Storage record in place (`[828 kv/SpaceProto.cpp:462-578]`, `[828 kv/Repo.cpp:71-93]`). (recommendation) Create
  dataspaces outside a transaction, and log the record id (6.3) before sending.
- **`fromDataspace`.** A string `customData` entry holding a dataspace URI clones that dataspace's schema into the new
  one and then repairs references; the caller needs read access to the source, and an unknown source gives
  `ENOT_FOUND` "Dataspace not found: {uid}" `[828 kv/SpaceProto.cpp:484-515, 580-599]`,
  `[R: "Restrictions added on top of ETP specifications", line 20]`.

**Response** `[P: Dataspace.PutDataspacesResponse]`: `success: map<string>`, one entry per created dataspace, key =
client id, value = empty string `[828 kv/SpaceProto.cpp:567]`. When every entry fails, only a final
`ProtocolException` with the `errors` map is sent (8.1) `[828 kv/SpaceProto.cpp:439-443, 572-578]`.

### 4.4 Start the write transaction: `Transaction.StartTransaction` (18/1)

**Request** `[P: Transaction.StartTransaction]`: `readOnly: boolean`, `message: string`,
`dataspaceUris: array<string>` (default `[""]`). **Response** `[P: Transaction.StartTransactionResponse]`:
`transactionUuid: Uuid`, `successful: boolean`, `failureReason: string`.

Server rules `[828 kv/TransProto.cpp:66-165]`:

- **Send the dataspace URI explicitly.** Each entry must parse as a dataspace URI; the schema's default `[""]` does
  not (the parser rejects anything shorter than 5 characters `[828 etp/EmlUri.cpp:74-76]`) and fails with
  `EINVALID_ARGUMENT` "Cannot start transaction, invalid dataspace URI(s): ..." (lines 101-127). An empty array means
  all dataspaces (lines 101-102, `[828 kv/Repo.cpp:498-501]`, `[828 tests/openkv/TransProtoTests.cpp:191-209]`) and
  takes no per-dataspace write lock (inference from `[828 kv/Session.cpp:333-337]`). Both reference clients send
  exactly one URI `[828 clt/Client.cpp:2116-2121]`, `[1079 ts/restApi/ControllerUtils.ts:886-891]`.
- **One transaction per session:** a second start gives `EMAX_TRANSACTIONS_EXCEEDED` (15) "Transaction already
  active" (lines 86-90).
- **One write transaction per dataspace**, tracked in a process-wide set of URI strings
  `[828 kv/Session.h:118-125, 181]`, `[828 kv/Session.cpp:320-347]`: a conflict gives `EMAX_TRANSACTIONS_EXCEEDED`
  "Cannot start transaction, too many write transaction URI(s): ..." (lines 106-133). `BP` states the one-writer rule
  and asks clients to retry, with one dataspace per write transaction `[BP: "Transactions", lines 42-45]`.
  (inference) The set lives in one server process; the GC chart scales the server to up to 6 replicas
  `[828 devops/gc/deploy/values.yaml:39-41]`, so two replicas do not see each other's writers.
- A read-write transaction for a read-only user gives `EREQUEST_DENIED` "Cannot start RW transaction for RO user"
  (lines 92-96); a database failure gives `EINVALID_STATE` "Cannot start Transaction: ..." (lines 135-143).
- These failures are `ProtocolException`s, not `successful=false` (lines 86-143).
- **Retry policies of the reference clients.** C++ `ScopedTransaction::start`: by default up to 1000 retries after
  any failure, 400 ms growing by 1.5 up to 2 s `[828 clt/Client.h:200-207]`, `[828 clt/Client.cpp:2099-2158]` (the
  importer passes its own retry count `[828 clt/ImportEpc.cpp:1586-1587]`). TypeScript: 6 retries, 400 ms growing by
  2, only on `EMAX_TRANSACTIONS_EXCEEDED` `[1079 ts/client/ResqmlClient.ts:719-734]`.
- **Writes without an explicit transaction.** `PutDataObjects`, `DeleteDataObjects` and `PutDataArrays` open a
  server-side local transaction that waits for the dataspace (8.6); `PutDataObjects` and `DeleteDataObjects` commit it
  and fail missing or dangling references at once `[828 kv/StoreProto.cpp:865-902, 945-992, 1157-1297]`. `BP`
  strongly recommends explicit transactions, for consistency and because local transactions "may significantly slow
  down the ingestion process" `[BP: "Transactions", lines 27-42]`. Two further reasons from the code (inference): no
  local transaction is ever created on a server running without extra worker threads (8.7), and `PutDataArrays` never
  commits its local transaction, whose destructor rolls it back `[828 kv/ArrayProto.cpp:1204-1366]` (no commit call),
  `[828 kv/TransProto.h:152-157]`. (recommendation) Send every write inside an explicit transaction.

### 4.5 Put the XML objects: `Store.PutDataObjects` (4/2) and `Store.Chunk` (4/8)

**Request** `[P: Store.PutDataObjects]`: `dataObjects: map<DataObject>`, `pruneContainedObjects: boolean`
(default false). The map key is a client id; the TypeScript client uses the object URI
`[1079 ts/protocols/StoreCustomer.ts:427-437]`.

`[P: Datatypes.Object.DataObject]`, fields in wire order:

| Field | Avro type | Value to send |
| --- | --- | --- |
| `resource` | `Resource` | Below |
| `format` | string | `"xml"`. Anything else, the empty string included, gives `EINVALID_OBJECT` "Expected XML format" `[828 kv/StoreProto.cpp:786-789]`. |
| `blobId` | `[null, Uuid]` | `null` for inline XML; a new random UUID when the XML follows in `Chunk` messages |
| `data` | bytes | The UTF-8 XML document. Empty only with `blobId`; otherwise `EINVALID_OBJECT` "No XML content" (lines 790-798). |

`[P: Datatypes.Object.Resource]`, fields in wire order: `uri`, `alternateUris` (array of string), `name`,
`sourceCount` (`[null, int]`), `targetCount` (`[null, int]`), `lastChanged` (long), `storeLastWrite` (long),
`storeCreated` (long), `activeStatus` (`ActiveStatusKind`), `customData` (map of `DataValue`). The C++ importer fills
`uri`, `name` (the citation title) and `lastChanged` (the citation's last change, in microseconds)
`[828 clt/ImportEpc.cpp:1795-1807]`.

**What the server uses** `[828 kv/StoreProto.cpp:205-350, 738-1053]`:

- From `resource.uri`, only the dataspace and the GUID: the URI must be an object URI with a valid GUID
  (`EINVALID_URI` "Expected eOBJ_INSTANCE URI" or "Invalid Object UID", lines 800-808); the dataspace must exist
  (`ENOT_FOUND` "Dataspace not found: {uid}", lines 844-850); the caller needs write access (lines 857-863).
- The rest of `resource` is ignored except `customData`, which is stored with the object; a source comment says the
  handler mostly ignores the resource apart from its metadata and trusts what it extracted from the XML
  (lines 267-276). Identity, type and relations come from the XML (5.1).
- **Upsert.** An object with the same (type, UUID) in the dataspace is replaced, and the relations that pointed to the
  old row are repointed to the new one (lines 252-281).
- **References** resolve within the same dataspace by (type, UUID) (5.1). Inside a transaction an unresolved
  reference waits for its target, which may arrive later in the same transaction
  `[828 kv/SpaceDML.cpp:1485-1515, 1678-1718]`, and fails the commit if it never does (4.7); order inside a transaction
  therefore does not matter `[BP: "Transactions", line 40]`. Without a transaction the request fails as a whole with
  `EINVALID_STATE` "{N} Missing reference(s) in space {uid}" (lines 959-983).
- **Arrays the object names** (5.1): an array that exists is attached to the object; one that does not yet exist is
  recorded as missing and must be supplied before commit (lines 289-316).
- **Response** `[P: Store.PutDataObjectsResponse]`: `success: map<PutResponse>`, one entry per stored object; the
  `PutResponse` arrays are left empty ("No need to fill in details about changes in ContainedObjects", lines 318-319).
  Per-object failures come in a `ProtocolException.errors` map under the same key (8.1).

**Chunked XML** `[828 kv/StoreProto.cpp:813-819, 1363-1406]`, as the C++ importer `[828 clt/ImportEpc.cpp:1781-1841]`
and the TypeScript client `[1079 ts/protocols/StoreCustomer.ts:335-425]` send it:

1. `PutDataObjects` carries the object with `blobId` set and `data` empty. Both clients clear FIN on this message
   (TypeScript lines 378-379; C++ `begin_multi_part` `[828 clt/Client.hpp:60-63, 97-104]`).
2. The server parks the whole request, keyed by its `messageId`, and sends nothing yet (lines 813-819).
3. `Store.Chunk` messages follow `[P: Store.Chunk]`: `blobId: Uuid`, `data: bytes`, `final: boolean`, with header
   `correlationId` = the `PutDataObjects` `messageId`. `correlationId` 0 gives `EINVALID_ARGUMENT` "Chunk must contains
   parent ID as correllation ID 0"; an unknown parent gives "Chunk must be associated with existing parent {id}"
   (lines 1374-1382).
4. The chunk's bytes are appended to the parked object whose `blobId` matches. A chunk whose `blobId` matches no object
   is dropped without an error, because the "found" flag is set for any object in the parent (lines 1385-1394)
   (inference).
5. Processing starts when a chunk arrives with the FIN header flag; `final` is not read (lines 1396-1403). Send
   `final=true` and FIN on the last chunk. The C++ client does this with `sending_final_part`
   `[828 clt/Client.hpp:65-68, 105-107]` and sets each chunk's `correlationId` from the id of the message sent just
   before `[828 clt/Client.cpp:660-671]`.
6. The `PutDataObjectsResponse` carries `correlationId` = the original `PutDataObjects` id
   `[828 svr/Server.cpp:3628-3639]`.

- **Chunk sizes.** The C++ importer chunks any XML of at least 80% of the negotiated size, in chunks of at most that
  80% `[828 clt/ImportEpc.cpp:1572-1574, 1784-1793, 1827-1840]`; the TypeScript client uses the negotiated size minus
  50 bytes `[1079 ts/protocols/StoreCustomer.ts:393-396]`.
- (recommendation) Put one chunked object per `PutDataObjects` message, as the C++ importer does (it flushes the
  current batch first and sends the chunked object alone, `[828 clt/ImportEpc.cpp:543-564]`): the server parks the
  whole request until the FIN chunk, whatever the other objects in it.

### 4.6 Put the arrays: DataArray protocol (9)

**Identifier** `[P: Datatypes.DataArrayTypes.DataArrayIdentifier]`: `uri: string`, `pathInResource: string`. Checks
common to all DataArray messages `[828 kv/ArrayProto.cpp:567-601]`: the dataspace in `uri` must exist (`ENOT_FOUND`
"Dataspace not found"); the URI's GUID must be valid (`EINVALID_URI` "Invalid Object UID"); `pathInResource` must not
be empty (`EINVALID_URI` "Invalid empty pathInResource"); a leading `/` is removed. Which URI to use: 5.3.

**`PutDataArrays` (9/4)** `[P: DataArray.PutDataArrays]`: `dataArrays: map<PutDataArraysType>`;
`[P: Datatypes.DataArrayTypes.PutDataArraysType]` is `uid: DataArrayIdentifier`, `array: DataArray`,
`customData: map<DataValue>`; `[P: Datatypes.DataArrayTypes.DataArray]` is `dimensions: array<long>`,
`data: AnyArray`. Server rules `[828 kv/ArrayProto.cpp:1057-1428]`:

- empty map: `EINVALID_ARGUMENT` "Nothing to add" (lines 1078-1093);
- `dimensions` not empty (`EINVALID_OBJECT` "No array dimensions"); rank at most 4 (`ELIMIT_EXCEEDED` "Array rank > 4:
  {n}"); no negative dimension and no overflow of their product (`ELIMIT_EXCEEDED` "Array dimensions overflow or
  non-positive"); a zero dimension, that is an empty array, is accepted (lines 1126-1138,
  `checkedDimsProduct` at lines 71-79);
- element count equal to the product (`EINVALID_OBJECT` "Array data - dimensions mismatch: {n} != {m}",
  lines 1172-1178);
- transport types (lines 1143-1169): `ArrayOfBoolean` (stored one byte per element), `ArrayOfInt` (32-bit),
  `ArrayOfLong`, `ArrayOfFloat`, `ArrayOfDouble`, `bytes`, and `ArrayOfString` (marked as unfinished in the code, and not
  readable back, 7.6);
- write access to the dataspace (lines 1215-1233);
- **container check**, only in this message: the GUID of `uri` must match a resource of the dataspace
  (`select from res where guid = $1`), checked at commit (4.7, step 5) when a transaction is open, and at once
  (`ENOT_FOUND` "Containing object {uuid} not found") otherwise (lines 1286, 1293-1300,
  `[828 kv/SpaceDML.cpp:1040-1043, 1301-1330]`). By the time the check runs the handler always holds a transaction,
  explicit or local (lines 1239-1276), so the immediate form is not reached, and the deferred check is evaluated
  only by `CommitTransaction` (inference);
- an existing array with the same path is overwritten (lines 1330-1348); an array that no object names yet is kept
  as an orphan until an object naming its path arrives in the same transaction (lines 1350-1360,
  `[828 tests/openkv/TransProtoTests.cpp:336-390]`);
- response `[P: DataArray.PutDataArraysResponse]`: `success: map<string>`, empty values (lines 1362-1364).

The TypeScript client puts an array whole when its size plus 1024 bytes is below the negotiated size
`[1079 ts/client/ResqmlClient.ts:230, 2403-2437]`; the C++ importer when its size is at most 80% of the negotiated size
`[828 clt/ImportEpc.cpp:1572-1574, 1940-1951]`.

**`PutUninitializedDataArrays` (9/9)** `[P: DataArray.PutUninitializedDataArrays]`:
`dataArrays: map<PutUninitializedDataArrayType>`, each `uid` plus `metadata: DataArrayMetadata`;
`[P: Datatypes.DataArrayTypes.DataArrayMetadata]` is, in wire order, `dimensions`, `preferredSubarrayDimensions`,
`transportArrayType`, `logicalArrayType`, `storeLastWrite`, `storeCreated`, `customData`. Server behaviour
`[828 kv/ArrayProto.cpp:1984-2224]`: it uses `dimensions` and `transportArrayType` with the same rank and size rules
(lines 2035-2083), allocates the array (lines 2130-2173), and stores neither `logicalArrayType` nor
`preferredSubarrayDimensions`; there is no container check. Response: `success: map<string>` (line 2189).

**`PutDataSubarrays` (9/5)** `[P: DataArray.PutDataSubarrays]`: `dataSubarrays: map<PutDataSubarraysType>`;
`[P: Datatypes.DataArrayTypes.PutDataSubarraysType]` is `uid`, `data: AnyArray`, `starts: array<long>`,
`counts: array<long>`. Server rules `[828 kv/ArrayProto.cpp:1430-1821]`:

- `starts` and `counts` of equal length, not empty, rank at most 4, no negative count and no overflow
  (lines 1498-1516); element count equal to the product of `counts` (`EINVALID_OBJECT` "Sub-array data - counts
  mismatch", lines 1549-1555);
- the array must exist (`ENOT_FOUND` with the path, lines 1632-1636) and have the same rank ("Unexpected sub-array
  rank", lines 1647-1651);
- on every axis, `0 <= start < dimension` and `0 < count <= dimension - start` (lines 1653-1684);
- the transport type must equal the stored type ("Unexpected sub-array type", lines 1686-1689);
- response: `success: map<string>` (line 1757).

Neither `PutUninitializedDataArrays` nor `PutDataSubarrays` opens a local transaction; of the three array writes only
`PutDataArrays` does (line 1241). Without a transaction the other two write through a stand-alone writer
`[828 kv/Repo.cpp:146-178]`. Use all three inside an explicit transaction (4.4).

### 4.7 Commit or roll back

**`CommitTransaction` (18/3)** `[P: Transaction.CommitTransaction]`: `transactionUuid`. Answered by
`CommitTransactionResponse` (18/5) `[P: Transaction.CommitTransactionResponse]`: `transactionUuid`, `successful`,
`failureReason`. Order of the commit-time steps `[828 kv/TransProto.cpp:167-372]`:

1. Remove the replaced object versions (lines 208-222).
2. Run the Activity automation (5.2) (lines 229-248).
3. Missing references: `successful=false`, `failureReason` "{N} Missing reference(s)" (lines 255-268).
4. Orphan relations: "{N} Orphan reference(s)" (lines 270-276).
5. Missing array containers (4.6): a `ProtocolException` with `EINVALID_STATE` and the message "Missing array
   container(s): " (the format string has no placeholder, so the list of containers is not in the message)
   (lines 278-283).
6. Arrays named by objects but never supplied: "{N} Missing array(s)" (lines 294-304).
7. Arrays put but never attached to an object: "{N} Orphan array(s)" (lines 306-313).
8. Database commit (line 319); a database error gives "Cannot COMMIT Transaction: {reason}" (lines 346-351).

Steps 2 to 5 run only when the transaction obtained a writer, that is when it wrote something (lines 229-288).

- **A failed commit leaves the transaction open:** "the client can decide to RollbackTransaction(), or do more PUTs and
  retry CommitTransaction" (lines 352-354); the early returns of steps 3 to 7 also leave it open.
- No transaction, or another transaction's UUID, gives `successful=false` with "No active transaction" or "Unknown
  transaction uuid" (lines 185-204).

**`RollbackTransaction` (18/4)** `[P: Transaction.RollbackTransaction]`, answered by `RollbackTransactionResponse`
(18/6): with the right UUID the transaction is always discarded, even when the database rollback fails
(lines 411-436); without a transaction or with another UUID the answer is `successful=false` as above
(lines 391-409) `[828 kv/TransProto.cpp:374-453]`.

**Isolation.** Until commit only the writing session sees its writes
`[828 tests/openkv/TransProtoTests.cpp:166-189, 310-334]`, `[BP: "Transactions", line 38]`.

### 4.8 Publish: `DataspaceOSDU.LockDataspaces` (2424/5)

- **Request** `[P: DataspaceOSDU.LockDataspaces]`: `uris: map<string>`, `lock: boolean`. **Response**
  `[P: DataspaceOSDU.LockDataspacesResponse]`: `success: map<string>` (a single-part message).
- **Why.** "when the content needs to be published to the system of record, the client should lock the dataspace",
  which makes it read-only `[BP: "Lock", lines 47-50]`.
- **Server rules** `[828 kv/SpaceOSDUProto.cpp:348-423]`: empty map `EINVALID_ARGUMENT` "Nothing to lock/unlock";
  unknown dataspace `ENOT_FOUND` "Dataspace not found"; locking a locked dataspace (or unlocking an unlocked one)
  `EINVALID_ARGUMENT` "Dataspace is already locked" (or "unlocked"); the caller needs write access, checked ignoring
  the lock when unlocking, else `EAUTHORIZATION_REQUIRED` "No permission to lock Dataspace {uid}" (or "unlock");
  success adds `success[key] = ""`.
- **Effect.** A locked dataspace refuses writes and deletes (1.4) `[828 kv/BaseProto.cpp:201-243, 282-292]`.
- **Response shape.** Any per-item error makes the server send only a final `ProtocolException` with the `errors`
  map, without the success map `[828 svr/Server.cpp:3691-3692]`; the items not listed in `errors` succeeded
  (inference).

### 4.9 End-to-end sequence

```text
WebSocket upgrade: subprotocol etp12.energistics.org; Authorization: Bearer <token>; data-partition-id
0/6    Authorize                        -> 0/7 AuthorizeResponse (optional)
0/1    RequestSession                   -> 0/2 OpenSession, or ProtocolException
2424/1 GetDataspaceInfo {k: dsUri}      -> 2424/2 (ProtocolException first for unknown or unreadable keys)
24/3   PutDataspaces (only if absent)   -> 24/6, or ProtocolException alone when every entry failed
18/1   StartTransaction [dsUri]         -> 18/2, or ProtocolException EMAX_TRANSACTIONS_EXCEEDED (retry)
4/2    PutDataObjects (batches)         -> 4/9 (ProtocolException first for failed keys)
       [4/2 one chunked object, 4/8 Chunk ... 4/8 Chunk with FIN -> 4/9]
9/4    PutDataArrays (arrays that fit)  -> 9/10
9/9    PutUninitializedDataArrays       -> 9/12
18/3   CommitTransaction                -> 18/5 successful; else 18/4 RollbackTransaction -> 18/6
18/1   StartTransaction [dsUri]         -> 18/2 (once per fill batch)
9/5    PutDataSubarrays (slices)        -> 9/11
18/3   CommitTransaction                -> 18/5
2424/5 LockDataspaces lock=true         -> 2424/6, or ProtocolException (optional publish)
0/8    Ping while idle                  -> 0/9
0/5    CloseSession                     (no reply; the server closes the socket)
```

A `ProtocolException` is message type 1000 on the protocol of the request it answers (2.3).

## 5. Payload and bulk data shapes

### 5.1 XML data objects

The server scans each object's XML with `FilePart::scanData` `[828 kv/StoreProto.cpp:229-250]`,
`[828 epc/FilePart.cpp:606-885]`. "It is not main role of etp-server to neither check if the datasets/content is
correct nor output what is wrong" `[R: "Introduction", line 11]`: validate content in the engine.

- **Parse.** XML that does not parse gives `EINVALID_OBJECT` "Cannot parse XML" `[828 kv/StoreProto.cpp:241-244]`.
- **Root attributes.** `uuid`, `schemaVersion` and the optional `xsi:type` are read from the root element
  `[828 epc/FilePart.cpp:644-666]`.
- **Citation.** An EML `Citation` element must be a direct child of the root: the UUID is only handed over when the
  citation is found `[828 epc/FilePart.cpp:668-682]`, so an object without it fails with `EINVALID_OBJECT` "Invalid UUID"
  `[828 kv/StoreProto.cpp:246-250]`. Title, creation, last update, version string, originator and editor are stored
  from the citation `[828 kv/SpaceDML.cpp:1537-1590]`.
- **Type name:** the part of `xsi:type` after the colon, or the root's local name when there is no `xsi:type`
  `[828 kv/StoreProto.cpp:252-257]`.
- **ML:** the root namespace plus the major.minor of `schemaVersion` (for example `2.0.0.20140822` counts as `2.0`)
  `[828 kv/SpaceDML.cpp:1337-1367]`, matched against the pairs the writer primes: `resqmlv2` with `2.0` is `resqml20`,
  with `2.2` `resqml22`; `commonv2` with `2.0` is `eml20`, with `2.3` `eml23`; `witsmlv2` `2.1` is `witsml21`;
  `prodmlv2` `2.2` is `prodml22` (namespaces `http://www.energistics.org/energyml/data/<ns>`, lines 1198-1213). An
  `EpcExternalPartReference` placed in the RESQML 2.0 namespace is filed under `eml20` (lines 1369-1374).
- **RESQML 2.0.1 type spelling.** References name their target through an EML 2.0 `ContentType`
  (`application/x-<ml>+xml;version=<v>;type=<type>`), turned into `<ml><major><minor>.<type>`, for example
  `resqml20.obj_LocalDepth3dCrs` `[828 epc/ContentType.cpp:90-142]`, `[828 epc/FilePart.cpp:237-249]`. The server's own
  test objects carry a root `xsi:type="resqml:obj_LocalDepth3dCrs"` and `xsi:type="eml:obj_EpcExternalPartReference"`
  `[828 src/lib/oes/testing/eml/TestingMocks.cpp:234-270, 272-328]`, so their stored type is `obj_...` and matches both
  such references and `obj_` URIs. A RESQML 2.0.1 object stored without that `xsi:type` gets the root's local name
  (without `obj_`) as its type, and `ContentType`-based references to it would not resolve (inference).
- **References** are found anywhere below the root, among EML-namespace elements: EML 2.0 references by
  `ContentType` (with `UUID`), EML 2.3 references by `QualifiedType` when the parent also has a `Uuid`
  `[828 epc/FilePart.cpp:754-811]`. The reference's type must use one of the six MLs of 4.2
  `[828 kv/SpaceDML.cpp:1640-1676]`. The well-known base property kind UUID `a48c9c25-1e3a-43c8-be6a-044224cc69cb` is
  never treated as a reference `[828 epc/FilePart.cpp:863-871]`.
- **Array references.** EML 2.0 `PathInHdfFile` followed by `HdfProxy`; EML 2.3 `PathInExternalFile`, optional
  `StartIndex` elements, `URI`, then a required following element such as `MimeType`
  `[828 epc/FilePart.cpp:342-401, 714-753]`. The path (without a leading `/`) is the array's key in the dataspace (6.4).

### 5.2 Server-created Activity objects

The server starts with `--check-activities` true by default `[828 bin/ServerCmds.cpp:593-595]`,
`[R: "the --check-activities option", lines 473-474]`, and none of the three deployments overrides it (their start
commands in 1.4 carry no such option, and the server containers of the GC and core-plus charts set no command or
arguments of their own `[828 devops/gc/deploy/templates/spot-deployment.yaml:43-80]`,
`[828 devops/core-plus/deploy/templates/deployment.yaml:21-59]`).
With it `[828 kv/Session.cpp:43-316]`, `[828 kv/StoreProto.cpp:82-203, 986-991]`, `[828 kv/TransProto.cpp:229-248]`:

- at commit, or at the end of a local transaction, the server creates a `resqml20.obj_Activity` titled
  `Scenario_automated_import_<UTC time>` (Session.cpp lines 153-172);
- it names as outputs every object that is new to the dataspace in this transaction, except the outputs of Activity
  objects in the same transaction whose template is known (and, for non-import templates, the objects related to
  those outputs), and except the types `EpcExternalPartReference`, `LocalDepth3dCrs` and `LocalTime3dCrs`
  (Session.cpp lines 64-151; StoreProto.cpp lines 82-89, 174-202);
- when absent, it also creates the `resqml20.obj_ActivityTemplate` titled `Import` with the fixed UUID
  `1801db47-12c3-4f8b-8ae3-25baeb528c23` (Session.cpp lines 43-45, 203-229, 236-249).

Consequences: a read-back with `GetResources` shows these extra objects, with UUIDs the server minted. A delivery
that must reproduce its input exactly either supplies its own Activity objects that name the delivered objects as
outputs, or treats the minted objects as expected and records their URIs (recommendation).

### 5.3 Array identifiers and types

- **Paths are unique per dataspace, not per object.** Table `ary` has `path text not null UNIQUE`
  `[828 kv/SpaceDDL.cpp:238-255]`, and every lookup is by path alone `[828 kv/SpaceDML.cpp:307-315, 1046-1054]`. Keep
  `PathInHdfFile` and `PathInExternalFile` values unique within a dataspace.
- **Which `uri` the reference clients use:**
  - RESQML 2.0.1: the URI of the `eml20.obj_EpcExternalPartReference` (HDF proxy) that the representation's `HdfProxy`
    names; the importer's comment calls it "NOT the "real owner" of the data, but the EpcExt"
    `[828 clt/ImportEpc.cpp:1359, 1765-1779, 1953-1959, 1999-2001]`. The REST SDK reads arrays through
    `eml20.obj_EpcExternalPartReference` too `[1079 sdk/README.md:53-59]`. A RESQML 2.0.1 delivery therefore also puts
    the `EpcExternalPartReference` object into the dataspace, and with a transaction that object satisfies the
    container check of `PutDataArrays` (4.6).
  - RESQML 2.2 and EML 2.3: for arrays it sends whole, the importer uses the owning object's URI, and rewrites the
    `ExternalDataArrayPart/URI` text of the XML from the HDF5 file name to that object URI before sending
    `[828 clt/ImportEpc.cpp:1709-1735, 1953-1959]`. Its uninitialized-array and fill phases look the URI up only among
    `EpcExternalPartReference` objects `[828 clt/ImportEpc.cpp:1993-2001, 1330-1332]`, so this convention rests on the
    whole-array path alone (open item 5).
  - The server's own test puts an array under a grid object's URI and, after the object arrives, reports that URI for
    the array `[828 tests/openkv/TransProtoTests.cpp:336-390]`.
- **Types.** `ArrayOfString` arrays can be written but not read back (7.6). Booleans are stored one byte per element.

### 5.4 Large arrays

- **Strategy** `[BP: "Exchange of Large Arrays", lines 15-23]`: in the first transaction put the objects and declare
  the large arrays with `PutUninitializedDataArrays`; fill them with `PutDataSubarrays` in later transactions, each of
  which can be retried on its own. For reads, get the metadata first and use subarrays when an array exceeds the
  message limit.
- **The C++ importer** does exactly this `[828 clt/ImportEpc.cpp:1526-2075]`: transaction 1 holds the objects, the
  arrays that fit whole and the uninitialized declarations, then commits (lines 1586-2067); it then drops the
  connection, opens a fresh one and fills the arrays in transactions bounded by time or bytes, retrying and resuming
  from a checkpoint (lines 2069-2072, 1245-1486).
- **Slicing.** The importer slices along the first dimension, keeping the trailing dimensions whole, with slices of at
  most 80% of the negotiated size; when one slab of the trailing dimensions is itself too large, it steps through the
  leading indices and slices the next dimension instead, down to the last one `[828 clt/ImportEpc.cpp:714-1075]`. The
  TypeScript client slices only along the first dimension, with slices of at most the negotiated size minus 1024 bytes
  `[1079 ts/client/ResqmlClient.ts:2444-2481, 2598-2611]`. The REST gateway sends arrays in 4 MB chunks
  `[1079 ReleaseNotes.md:123, 131-132, 249-250]`.

## 6. Identities and versions

### 6.1 Dataspaces

A dataspace has a uid (the string inside `dataspace('...')`, normally equal to the path) and a path
`[828 kv/SpaceProto.cpp:346-408]`. It has no version. `GetDataspaces` and `GetDataspaceInfo` return `storeCreated` and
`storeLastWrite` in microseconds, and `customData` including `read-only`, `locked` and `size` (7.1, 7.2).

### 6.2 Data objects

- An object's identity is (type, UUID) within one dataspace; the same UUID in two dataspaces is two objects
  `[R: "Data Spaces", lines 402-404]`.
- `PutDataObjects` is an upsert (4.5): the previous row of the same (type, UUID) loses its UUID and is removed at
  commit, or at the end of the local transaction `[828 kv/StoreProto.cpp:258-265, 948-949]`,
  `[828 kv/TransProto.cpp:208-222]`, `[828 kv/SpaceDML.cpp:1723-1740]`, so the store keeps only an object's latest
  content. Reads and deletes look objects up by (type, UUID); the version part of a URI takes no part
  `[828 kv/StoreProto.cpp:546-565, 1214-1240]`.
- Replays are safe: objects and arrays are upserts (inference from 4.5 and 4.6).

### 6.3 The dataspace's Storage record

Created by `PutDataspaces` (4.3) through the groups-based authorization path
`[828 oapi/EntitlementDataspace.cpp:49-134]`, `[828 oapi/EntitlementClient.cpp:215-250]`:

- **Call:** `PUT {storage}{RECORDS_PATH}` `[ST: PUT /records, createOrUpdateRecords]` with the caller's
  `Authorization` and `data-partition-id` (1.4); anything but 201 is a failure.
- **Body:** a one-element JSON array:
  - `kind`: `<SCHEMA_ID>:wks:dataset--ETPDataspace:1.0.0`, `SCHEMA_ID` defaulting to `osdu`
    `[828 oapi/EntitlementUtils.cpp:122-123]`;
  - `id`: `<data-partition-id>:dataset--ETPDataspace:<urlId>`, where `urlId` is the dataspace uid with every `/`
    replaced by `-`, URL-encoded (alphanumerics and ``- _ . ! ~ * ' ( )`` kept, `[828 src/lib/oes/core/utils/String.cpp:806-832]`),
    and every `%` replaced by `_`; an `urlId` longer than 64 characters becomes its first 32 characters followed by a
    deterministic name-based GUID (Boost `name_generator` with the RFC 4122 DNS namespace) without dashes
    `[828 oapi/EntitlementDataspace.cpp:49-63, 99-102]`, `[828 src/lib/oes/core/utils/Guid.cpp:551-557]`;
  - `acl.viewers`, `acl.owners` from `customData`;
  - `legal.legaltags` and `legal.otherRelevantDataCountries` from `customData`, falling back to the server's
    `LEGAL_TAGS` and `LEGAL_COUNTRIES`; `legal.status` `"compliant"`;
  - `data.DatasetProperties.URI`: `eml:///dataspace(<path>)`, the unquoted legacy form.
- The policy-based path writes the same record without an `acl` block and with the quoted URI
  `[828 oapi/EntitlementDataspace.cpp:231-258]`, `[828 kv/BaseProto.cpp:108-115]`.
- (recommendation) The id is computable from the path before `PutDataspaces` is sent; for a path whose `urlId` stays
  within 64 characters it needs no hashing, since every character the path rule allows survives the encoding. Log it
  before sending, as this project's live-OSDU rule requires for ids a service mints on the engine's behalf.

### 6.4 Arrays

An array's identity is its path within the dataspace (5.3). Re-putting a path overwrites the array, its type
included `[828 kv/ArrayProto.cpp:1330-1348]`, `[828 tests/openkv/ArrayProtoTests.cpp:1338-1408]`.

### 6.5 Records the REST gateway builds for delivered content

The server creates no OSDU records for data objects: its only Storage calls are the dataspace record's create, read
and delete `[828 oapi/EntitlementClient.cpp:215-342]`, `[828 oapi/EntitlementClientPolicy.cpp:354, 403, 453]`. OSDU
work-product-component and related records for RESQML, WITSML and PRODML content come from the REST gateway of
project 1079:

- `POST /manifests/build` builds an OSDU `Manifest:1.0.0` for one dataspace; a request spanning several dataspaces is
  rejected with 400 `[1079 openapi.json: POST /manifests/build, ObjectsManifestAPI_GetManifest]`,
  `[1079 ReleaseNotes.md:20-29]`.
- The manifest goes to the Workflow service's `Osdu_ingest` interface
  `[WF: POST /v1/workflow/{workflow_name}/workflowRun, triggerWorkflow]`. The project ships an Airflow DAG with the
  same interface, `DAG_ID = "Osdu_ingest_rddms"`, which writes the records to Storage in batches of 500
  `[1079 devops/osdu/dags/rddms_ingest_manifest.py:16-38, 58-59]`.
- Link fields in the generated records:
  - a work-product component's `data.DDMSDatasets` is `["eml://rddms1/dataspace('<path>')/<ml>.<type>(<uuid>)"]`, the
    object URI with `eml:///` replaced by `eml://<rddmsId>/`, `rddms1` by default
    `[1079 ts/jsonTypes/WorkProductComponent.ts:2188-2205]`, `[1079 ts/jsonTypes/OsduContext.ts:164-185]`;
  - a `dataset--ETPDataspace` record of kind `osdu:wks:dataset--ETPDataspace:1.0.1`, with
    `data.DatasetProperties.URI` = the dataspace URI as the server returns it (quoted form) and `data.Name` = the path
    `[1079 ts/jsonTypes/ETPDataspace.ts:16-60]`;
  - its id is `<partition>:dataset--ETPDataspace:` followed by `encodeURIComponent` of the uid with its first `/`
    replaced by `-`, and the first `%` replaced by `_` `[1079 ts/jsonTypes/OsduContext.ts:439-444]`.
- (inference) For any uid the path rule allows, the gateway's id equals the server's (6.3) unless the server's
  `urlId` exceeds 64 characters: both encoders keep the same characters, and a path has at most one `/`. Ingesting
  the gateway's manifest therefore writes a new version of the server's record, with kind version 1.0.1 instead of
  1.0.0 and the quoted URI instead of the unquoted one; and a later `PutDataspaces` of the same path finds that record
  and purges it (4.3).
- Delivering RESQML into the Reservoir DDMS and registering it in OSDU search are separate steps: the ETP write, then
  a manifest or record write through Workflow or Storage (inference).

## 7. Reads, verification and deletes

### 7.1 `DataspaceOSDU.GetDataspaceInfo` (2424/1)

`[P: DataspaceOSDU.GetDataspaceInfo]` `uris: map<string>`, answered by `GetDataspaceInfoResponse` (2424/2)
`[P: DataspaceOSDU.GetDataspaceInfoResponse]` `dataspaces: map<Dataspace>` `[828 kv/SpaceOSDUProto.cpp:73-240]`:

- needs the viewers service group (`EREQUEST_DENIED` "Not authorized to GetDataspaces") (lines 89-100);
- per key: `EINVALID_URI` "Invalid URI: ..." or "Expecting Dataspace URI: ..."; `ENOT_FOUND` "Dataspace {uid} not
  found"; `EREQUEST_DENIED` with the access check's message (lines 108-140);
- the returned `customData` holds the stored entries plus `read-only`, `locked` and `size`, and repeats `viewers`,
  `owners` and `legaltags` as `ArrayOfString` (lines 165-197);
- it is the existence check the REST gateway runs before starting a transaction
  `[1079 ts/restApi/ControllerUtils.ts:877-885]`.

### 7.2 `Dataspace.GetDataspaces` (24/1)

`[P: Dataspace.GetDataspaces]` `storeLastWriteFilter: [null, long]`, answered by `GetDataspacesResponse` (24/2)
`dataspaces: array<Dataspace>`. It needs the viewers service group and lists only the dataspaces the caller can read,
with `read-only`, `locked` and `size` added to `customData` `[828 kv/SpaceProto.cpp:72-236]`.

### 7.3 `Store.GetDataObjects` (4/1)

`[P: Store.GetDataObjects]` `uris: map<string>`, `format: string` (default `xml`), answered by
`GetDataObjectsResponse` (4/4) `dataObjects: map<DataObject>` `[828 kv/StoreProto.cpp:380-716]`:

- empty map `EINVALID_ARGUMENT` "Empty URI list"; a `format` other than `xml` or empty gives `ENOTSUPPORTED` "Only XML
  supported" (lines 402-415);
- per key: `EINVALID_URI` "Expected eOBJ_INSTANCE URI"; `ENOT_FOUND` "Space {uid} not found" or "Object not Found";
  access errors (lines 453-584);
- the type in the URI must equal the stored `<ml>.<type>` (the query matches `uri.ml || '.' || typ.xml`,
  lines 546-565): use the same `obj_` spelling as the XML (5.1);
- the returned `Resource` carries `name`, `lastChanged` (the later of the citation's creation and last update),
  `storeCreated`, `storeLastWrite`, and `customData` `creator` and `created`, plus `reference` = `true` when the row
  carries a dataspace-reference id (an object the dataspace holds by reference to another one, inference) (lines 588-625);
- an object whose resource plus XML exceeds the session limit comes back with `blobId` set and empty `data`, in a part
  sent without FIN, followed by `Store.Chunk` (4/8) messages with the same `correlationId`; the final chunk has
  `final=true` (lines 627-652, `[828 svr/Server.cpp:3728-3763]`). Because that part is forced out as a non-final part,
  the chunks carry no FIN, and FIN comes on the last `GetDataObjectsResponse`, which may be empty (inference from
  `[828 svr/MultiPartReply.hpp:231-281]`). Reassemble by `blobId`.

### 7.4 `Discovery.GetResources` (3/1)

`[P: Discovery.GetResources]`: `context: ContextInfo` (`uri`, `depth: int`, `dataObjectTypes`, `navigableEdges`,
`includeSecondaryTargets`, `includeSecondarySources`), `scope`, `countObjects`, `storeLastWriteFilter`,
`activeStatusFilter`, `includeEdges`. Answered by `GetResourcesResponse` (3/4) `resources: array<Resource>`, preceded
by `GetResourcesEdgesResponse` (3/7) when edges are requested `[828 kv/DiscoProto.cpp:114-238, 561]`:

- invalid URI `EINVALID_URI`; unknown dataspace `ENOT_FOUND`; access errors (lines 139-167);
- a dataspace URI lists every resource of the dataspace whatever `scope` and `depth` say (lines 178-184, 658-661);
  `dataObjectTypes` (exact `<ml>.<type>`) and `storeLastWriteFilter` (microseconds, compared with the store's write
  time) filter the list (lines 678-708);
- an object URI supports `self`, `targets`, `sources`, `targetsOrSelf` and `sourcesOrSelf` (lines 196-229); a type URI
  gives `ENOTSUPPORTED` (lines 186-189);
- `countObjects` is not implemented (line 169);
- the C++ importer lists a dataspace with `depth=1` and scope `targets`, when asked to reject duplicates
  `[828 clt/ImportEpc.cpp:1603-1633]`.

### 7.5 `DataArray.GetDataArrayMetadata` (9/6)

`[P: DataArray.GetDataArrayMetadata]` `dataArrays: map<DataArrayIdentifier>`, answered by
`GetDataArrayMetadataResponse` (9/7) `arrayMetadata: map<DataArrayMetadata>`; only `dimensions` and
`transportArrayType` are filled `[828 kv/ArrayProto.cpp:1823-1982]`. Non-standard extension: one entry with key `ALL`,
`uri` = the dataspace URI and `pathInResource` = `*` lists every array of the dataspace; the response keys are the
JSON of each array's identifier, whose URI always says `resqml20` and, for an array no object owns yet, names an
`obj_EpcExternalPartReference` with a made-up GUID `[828 svr/Extensions.cpp:34-70]`,
`[828 kv/ArrayProto.cpp:1848-1853, 2227-2320]`.

### 7.6 `DataArray.GetDataArrays` (9/2) and `GetDataSubarrays` (9/3)

- `GetDataArrays` `[P: DataArray.GetDataArrays]` `dataArrays: map<DataArrayIdentifier>`, answered by (9/1)
  `dataArrays: map<DataArray>` `[828 kv/ArrayProto.cpp:613-798]`. The lookup is by path only (5.3). An array whose byte
  size is at or above the session limit gives `ELIMIT_EXCEEDED` "Oversize array ({n} > {m} bytes): {path}"
  (lines 757-763): get the metadata first and use subarrays `[BP: "Exchange of Large Arrays", lines 17-19]`.
- `GetDataSubarrays` `[P: DataArray.GetDataSubarrays]` `dataSubarrays: map<GetDataSubarraysType>`
  (`uid`, `starts`, `counts`), answered by (9/8) `dataSubarrays: map<DataArray>` `[828 kv/ArrayProto.cpp:800-1055]`.
  The slice must be smaller than the session limit ("Oversize sub-array", lines 985-993). The returned `dimensions`
  are the whole array's dimensions, not the slice's (line 1002).
- **String arrays** are not returned by either message: the entry is dropped from the reply with only a server log
  line and no error entry (lines 775-788, 1024-1037). A reader must treat a missing key as a failure.

### 7.7 `Store.DeleteDataObjects` (4/3)

`[P: Store.DeleteDataObjects]` `uris: map<string>`, `pruneContainedObjects`, answered by
`DeleteDataObjectsResponse` (4/10) `deletedUris: map<ArrayOfString>` `[828 kv/StoreProto.cpp:1055-1360]`:

- empty map `EINVALID_ARGUMENT` "Nothing to delete"; per key `EINVALID_URI` as in 7.3 (lines 1081-1117);
- a local transaction is opened when none is (4.4), then write access is checked (lines 1157-1202);
- the type in the URI must match the stored type as in 7.3 (lines 1214-1240); an unknown object gives `ENOT_FOUND`
  "Object not Found"; a deleted one gives `deletedUris[key]` = an `ArrayOfString` holding the request's URI
  (lines 1242-1254);
- deleting an object that others still reference fails: without a transaction as `EINVALID_STATE` "{N} dangling
  reference(s) in space {uid}" (lines 1277-1295), inside one at commit as "{N} Orphan reference(s)" (4.7);
- the gateway documents that "Deleting an object does not cascade-delete its arrays or referenced objects"
  `[1079 openapi.json: DELETE /dataspaces/{dataspaceId}/resources/{dataObjectType}/{guid}, MutationsAPI_DeleteDataObject]`.

### 7.8 `Dataspace.DeleteDataspaces` (24/4)

`[P: Dataspace.DeleteDataspaces]` `uris: map<string>`, answered by `DeleteDataspacesResponse` (24/5)
`success: map<string>` `[828 kv/SpaceProto.cpp:649-953]`:

- outside a transaction, a dataspace another session is writing fails the whole request with
  `EMAX_TRANSACTIONS_EXCEEDED` "Cannot delete dataspace {uri} while it is being written to" (lines 662-681);
- empty map `EINVALID_ARGUMENT` "Nothing to delete"; the caller must be in the owners service group, else
  `EAUTHORIZATION_REQUIRED` "User {name} has no permissions to delete dataspaces" (lines 688-736);
- per key: the root is refused; inside a transaction the dataspace must be named in `StartTransaction`; an unknown
  dataspace gives `ENOT_FOUND` "Space {uid}"; the caller needs write access, which a locked dataspace denies (unlock
  first; the gateway answers 403 for a locked dataspace, `[1079 ReleaseNotes.md:387, 405]`) (lines 768-807);
- the server then removes the dataspace's Storage record and, when that succeeds, drops the schema and the admin row;
  success is `success[key]` with an empty value, a failed drop gives `EINVALID_STATE` "Failed to delete schema {uid}"
  (lines 809-891);
- when every entry fails, only a final `ProtocolException` with the `errors` map is sent (lines 837-842);
- the server-minted Activity objects (5.2) go with everything else.

**The Storage call.** `DELETE {storage}{RECORDS_PATH}/<record id>` with the caller's token; 200, 204 and 404 count as
success `[828 oapi/EntitlementClient.cpp:252-288]`; the caller must be in one of the owners groups of the record
`[828 auth/Entitlement.cpp:465-495]`. In the core specification set this operation is `purgeRecord`: "the physical
deletion of the given record and all of its versions. This operation cannot be undone", allowed for
`service.storage.admin` members who own the record `[ST: DELETE /records/{id}, purgeRecord]`; the reversible
operation is `POST /records/{id}:delete` `[ST: POST /records/{id}:delete, deleteRecord]`, which the server never
calls. This project's cleanup rule forbids purges and `DELETE /records/{id}`, so deleting a dataspace through ETP
conflicts with it (open item 1). A delete inside a transaction that is rolled back would leave the dataspace and lose
its record, since the purge is an HTTP call outside the database transaction (inference).

## 8. Limits and errors

### 8.1 `Core.ProtocolException` (0/1000) and `Core.Acknowledge` (0/1001)

`[P: Core.ProtocolException]`: `error: [null, ErrorInfo]`, `errors: map<ErrorInfo>` (default `{}`);
`[P: Datatypes.ErrorInfo]` is `message: string` then `code: int` (message first).

Shapes `[828 svr/Server.cpp:3550-3769]`, `[828 svr/MultiPartReply.hpp:231-327]`, `[828 etp/Protocols.h:36-49]`:

- **Fatal.** A scalar `error`, with FIN; no response record follows. It comes from `fatalError(code, message)`, from
  the checks of 2.2, and from exceptions: a handler's thrown error status keeps its code and message; other
  exceptions become `EINVALID_STATE` "Server Error: Unexpected Exception: ..." or "Server Error: Unknown Exception."
  `[828 svr/Server.hpp:26-53]`, `[828 svr/Server.cpp:3868-3890]`, and an exception that escapes the dispatch becomes
  `EINVALID_STATE` "Request processing failed: ..." `[828 svr/Server.cpp:3806-3818]`.
- **Per item.** The `errors` map, keyed by the request's map key, in two variants:
  - partial success: the `ProtocolException` is sent without FIN, followed by the response (possibly multi-part)
    whose last part carries FIN `[828 svr/Server.cpp:3644-3657]`;
  - total failure, when the handler leaves its status not ok: the `ProtocolException` with the `errors` map is sent
    with FIN and no response follows (lines 3691-3692). This happens for `PutDataspaces` and `DeleteDataspaces` when
    every entry fails, and for `LockDataspaces` whenever any entry fails (4.3, 7.8, 4.8).
- **Store and DataArray when every entry fails.** Those handlers still finish with `finalProcess()`, which marks the
  status ok `[828 svr/MultiPartReply.hpp:263-281]`: the reply is a non-final `ProtocolException` with `errors`, then
  an empty final response `[828 kv/StoreProto.cpp:821-839, 995-1014]`, `[828 kv/ArrayProto.cpp:1183-1201, 1368-1387]`.

**Per-item outcome rule.** Collect every message with `correlationId` equal to the request's `messageId` until FIN.
A key in a response's success map succeeded; a key in any `ProtocolException.errors` failed with that code and
message; a scalar `error` failed the whole request. Both maps accumulate across parts, because the server flushes the
errors seen so far with each intermediate part `[828 svr/MultiPartReply.hpp:231-253]`.

**`Acknowledge`** `[P: Core.Acknowledge]` has no fields. The server sends it only when the request carries `0x10`,
right after decoding the header and before the checks of 2.2 that follow, with `messageFlags` 0 (no FIN) and `correlationId` = the
request id `[828 svr/Server.cpp:3331-3338, 3252-3262]`. The real response still follows.

`CommitTransaction` and `RollbackTransaction` report most failures inside their response record (4.7);
`StartTransaction` failures are `ProtocolException`s (4.4).

### 8.2 Success shapes

| Request | Success |
| --- | --- |
| `PutDataspaces` | `success[key] == ""` `[828 kv/SpaceProto.cpp:567]` |
| `DeleteDataspaces` | `success[key]` present `[828 kv/SpaceProto.cpp:875-879]` |
| `PutDataObjects` | `success[key]` present (a `PutResponse` with empty arrays) `[828 kv/StoreProto.cpp:318-319]` |
| `DeleteDataObjects` | `deletedUris[key]` holds the URI `[828 kv/StoreProto.cpp:1242-1254]` |
| `PutDataArrays`, `PutUninitializedDataArrays`, `PutDataSubarrays` | `success[key]` present `[828 kv/ArrayProto.cpp:1364, 2189, 1757]` |
| `StartTransaction` | a `StartTransactionResponse` with `successful == true` and the `transactionUuid` |
| `CommitTransaction`, `RollbackTransaction` | `successful == true` |
| `LockDataspaces` | a `LockDataspacesResponse`; any error suppresses the whole success map (4.8) |

The TypeScript client counts a `PutDataObjectsResponse` entry as a success only when `createdContainedObjectUris` is
not empty `[1079 ts/protocols/StoreCustomer.ts:592-607]`; the server always leaves that array empty, so do not copy
this rule.

### 8.3 Error codes

`[828 etp/ErrorCodes.h:22-60]`:

| Code | Name | Code | Name |
| --- | --- | --- | --- |
| 0 | `IS_OK` | 15 | `EMAX_TRANSACTIONS_EXCEEDED` |
| 1 | `ENOROLE` | 16 | `EDATAOBJECTTYPE_NOTSUPPORTED` |
| 2 | `ENOSUPPORTEDPROTOCOLS` | 17 | `EMAXSIZE_EXCEEDED` |
| 3 | `EINVALID_MESSAGETYPE` | 18 | `EMULTIPART_CANCELLED` |
| 4 | `EUNSUPPORTED_PROTOCOL` | 19 | `EINVALID_MESSAGE` |
| 5 | `EINVALID_ARGUMENT` | 20 | `EINVALID_INDEXKIND` |
| 6 | `EREQUEST_DENIED` | 21 | `ENOSUPPORTEDFORMATS` |
| 7 | `ENOTSUPPORTED` | 22 | `EREQUESTUUID_REJECTED` |
| 8 | `EINVALID_STATE` | 23 | `EUPDATEGROWINGOBJECT_DENIED` |
| 9 | `EINVALID_URI` | 24 | `EBACKPRESSURE_LIMIT_EXCEEDED` |
| 10 | `EAUTHORIZATION_EXPIRED` | 25 | `EBACKPRESSURE_WARNING` |
| 11 | `ENOT_FOUND` | 26 | `ETIMED_OUT` |
| 12 | `ELIMIT_EXCEEDED` | 27 | `EAUTHORIZATION_REQUIRED` |
| 13 | `ECOMPRESSION_NOTSUPPORTED` | 28 | `EAUTHORIZATION_EXPIRING` |
| 14 | `EINVALID_OBJECT` | 29 | `ENOSUPPORTEDDATAOBJECTTYPES` |

Also defined: 1002 `EINVALID_CHANNELID`, 4003 `ENOCASCADE_DELETE`, 4004 `EPLURAL_OBJECT`, 5001
`ERETENTION_PERIOD_EXCEEDED`, 6001 `ENOTGROWINGOBJECT`.

### 8.4 Retry classification

From the handlers cited above; the classification itself is an inference.

- **Retry after a pause:**
  - `EMAX_TRANSACTIONS_EXCEEDED` (15): from `StartTransaction` (4.4), from a local transaction that could not start
    (`[828 kv/StoreProto.cpp:879-896]`, `[828 kv/ArrayProto.cpp:1253-1270]`), and from `DeleteDataspaces` on a
    dataspace being written (7.8);
  - `EBACKPRESSURE_LIMIT_EXCEEDED` (24): "Requested partition '{id}' is not available now due high load.", after
    10 s without a free database connection `[828 kv/Repo.cpp:59, 608-626]`;
  - `EINVALID_STATE` "Unable to handle websocket message. Session is about to close." (reconnect first, 2.2).
- **Retry with a smaller request:** `EMAXSIZE_EXCEEDED` (17) "Maximum size exceeded" under the item's key, sent when
  the server cannot encode a response part within the limit `[828 svr/Server.cpp:3707-3724]`; `ELIMIT_EXCEEDED` (12)
  "Oversize array" or "Oversize sub-array" (7.6).
- **Retry once after refreshing the token (3.1):** `EAUTHORIZATION_REQUIRED` (27). An expired or rejected token
  surfaces as this code with a "no permissions" message, because a failed group listing leaves the caller without
  groups (inference from `[828 oapi/EntitlementClient.cpp:149-188]`, `[828 auth/Entitlement.cpp:159-173, 497-505]`).
- **Do not retry without changing the input:** `EINVALID_ARGUMENT` (5), `EINVALID_URI` (9), `EINVALID_OBJECT` (14),
  `ENOT_FOUND` (11), `EREQUEST_DENIED` (6), `ENOSUPPORTEDPROTOCOLS` (2), `ENOSUPPORTEDDATAOBJECTTYPES` (29), and the
  commit failures of 4.7.

### 8.5 Upgrade and transport failures

- HTTP 412, 400 or 401 at upgrade (1.3); a message above the server's maximum closes the socket (1.3).
- The C++ client maps connect failures `[828 clt/Client.cpp:1370-1434]`: timeout to `ETIMED_OUT` "WebSocket
  connection timed-out"; host unreachable to `ENOT_FOUND`; HTTP 400 to `EINVALID_ARGUMENT` "Bad request"; HTTP 401 to
  `EAUTHORIZATION_REQUIRED` "Unauthorized request"; HTTP 404 to `ENOT_FOUND`; a refused subprotocol to `ENOT_FOUND`
  "Connection refused, requested sub-protocol '...' is not supported"; anything else to `ELIMIT_EXCEEDED` "WebSocket
  connection refused". Its comment names "wrong partition" as a cause of 400; at this commit the server resolves the
  partition after the upgrade instead (1.3).

### 8.6 Sizes, batches, concurrency and timeouts

**Message size.**

- The session limit is the smaller of the client's and the server's maximum (3.2).
- Server default: `-M "16MB, 128MB"`, that is 16,000,000 bytes on the wire and 128,000,000 bytes uncompressed (`MB`
  is decimal; a single value sets the uncompressed limit to 10 times the wire limit)
  `[828 bin/ServerCmds.cpp:461-471, 613-616]`, `[828 bin/EmlAppContext.cpp:104-114, 137-167]`. The GC and core-plus
  images start without `-M` (1.4), so they run with these defaults (inference). The Azure chart passes `-M 2GB`
  `[828 devops/azure/chart/templates/deployment.yaml:73]`.
- `BP`: do not use messages in the GB range; fill messages up to the negotiated size by grouping objects and arrays
  `[BP: "Message Size", lines 5-8]`.
- Budgets of the reference clients: C++ importer 80% of the negotiated size `[828 clt/ImportEpc.cpp:1572-1574]`;
  TypeScript negotiated size minus 1024 bytes for arrays `[1079 ts/client/ResqmlClient.ts:230]` and 10,000,000 bytes
  by default (3.2); C++ command-line client default `-M 1MB` `[828 bin/EtpClient12.h:35-43]`; C++ library default
  2 GB `[828 clt/Client.h:85]`; REST gateway 4 MB array chunks (5.4).

**Batch counts.**

- The server's per-part item cap (`--max-batch-size`) defaults to 1,000,000 ("disable it for now")
  `[828 bin/ServerCmds.cpp:473-480, 615]`.
- The C++ command-line client batches 100 items by default `[828 bin/EtpClient12.h:39]`; the REST gateway sends 100
  objects per ETP message `[1079 README.md:143]`.
- The Azure option `-Q 1000` is the deprecated `msg-queue-depth` `[828 bin/EtpClient12.cpp:341-345]`.

**Arrays.** Rank at most 4; no negative dimension; paths unique per dataspace; whole and sub-array reads below the
session limit; avoid `ArrayOfString`; large arrays as uninitialized declarations plus subarrays, in separate
transactions (4.6, 5.3, 5.4, 7.6).

**Transactions and concurrency.**

- One write transaction per dataspace; different dataspaces can be written concurrently, and the calls inside one
  transaction may be concurrent `[BP: "Transactions", line 42]`.
- A local transaction waits for its dataspace: up to 20 attempts, sleeping 400 ms, 600 ms, 900 ms and then 2 s each
  time (the interval grows by 1.5 until the next step would pass 2 s), about 36 s in all
  `[828 kv/TransProto.h:58-121]`. The header comment there ("up to 5 times ... of 300ms ...") is out of date.
- The number of attempts is 0 when the session counts fewer than 2 additional worker threads
  `[828 kv/TransProto.h:76]`; that count is set when the session opens `[828 svr/Server.cpp:2359]` (8.7).
- Keep transactions short and resumable `[BP: "Exchange of Large Arrays", lines 20-23]`.

**Timeouts.**

- Server: open handshake 30 s (1.3); close drain 5 s (3.4); resume keep-alive 120 s (3.4).
- Azure Application Gateway annotation `request-timeout: "300"` `[828 devops/azure/chart/values.yaml:46]`.
- Reference clients: the Azure CI runs the C++ client with `--timeout 300s` `[828 devops/azure/azure.gitlab-ci.yml:118]`,
  core-plus with `--timeout 10s,60s` `[828 devops/core-plus/pipeline/override-stages.yml:29]`; the command-line default
  is `10s,10s` for connect and for each put or get `[828 bin/EtpClient12.h:36]`; the REST gateway rolls a transaction
  back after 300 s without a call and pings every 30 s `[1079 ts/restApi/ControllerUtils.ts:94, 811-819, 865-935]`.
- (recommendation) Ping during long local work, give large messages generous request timeouts, and refresh the token
  with `Authorize` on long sessions.

**Rate limiting.** When `global.autoscalingMode` is `requests`, the GC chart installs an Envoy `local_ratelimit` HTTP
filter with a bucket of 12 tokens refilled by 12 every second `[828 devops/gc/deploy/templates/rate-limits.yaml:16-58]`,
`[828 devops/gc/deploy/values.yaml:60-63]`; the default mode is `cpu`, which installs no filter (line 5). (inference)
The filter counts HTTP requests, so it limits WebSocket upgrades rather than ETP messages: reuse sessions instead of
reconnecting per request.

### 8.7 Deployment facts that shape the client

- **Worker threads.** The server is single-threaded unless started with `-j` (`-j` alone uses one thread per core,
  `-jN` exactly N, capped at the core count unless `ENABLE_ANY_NUMBER_OF_THREADS=true`)
  `[828 bin/ServerCmds.cpp:482-487, 603-608]`, `[828 svr/Server.h:205]`, `[828 svr/Server.cpp:1277-1308]`. None of the
  three deployments' start commands passes `-j` (1.4). On such a server a session counts no additional worker threads,
  so no local transaction is ever created and every write sent without an explicit transaction fails at once with
  `EMAX_TRANSACTIONS_EXCEEDED` (inference from `[828 kv/TransProto.h:76]`, `[828 kv/StoreProto.cpp:879-896]`,
  `[828 kv/ArrayProto.cpp:1253-1270]`). `R` recommends several threads and gives 3 as the minimum for `-j`, but says the
  server defaults to one thread per CPU, which the code does not do `[R: "Performance Configuration", lines 265-282]`
  (section 11).
- `R` also asks for the server and PostgreSQL in the same zone and warns against DEBUG builds (same section).

## 9. Scope of the route's ETP client

**Transport.** `System.Net.WebSockets.ClientWebSocket` with the subprotocol and headers of 1.3; whole binary messages
sent and received (reassembling WebSocket fragments); the session limit enforced on send; one receive loop per
session dispatching by `correlationId`.

**Avro binary codec** for exactly the types the route uses. The type closure of the 47 message records below is 38
data types (computed from `P`):

- 47 message records: Core `RequestSession`, `OpenSession`, `CloseSession`, `Authorize`, `AuthorizeResponse`, `Ping`,
  `Pong`, `ProtocolException`, `Acknowledge`; Dataspace get, put and delete with their three responses; DataspaceOSDU
  `GetDataspaceInfo`, `LockDataspaces` and their two responses; Store get, put and delete with their three responses,
  and `Chunk`; all 12 DataArray messages; all 6 Transaction messages; Discovery `GetResources`,
  `GetResourcesResponse`, `GetResourcesEdgesResponse`.
- 38 data types: `MessageHeader`, `ErrorInfo`, `Version`, `SupportedProtocol`, `SupportedDataObject`; `DataValue`
  with its 10 `ArrayOf*` records, `AnySparseArray` and `AnySubarray`; `AnyArray`, `AnyArrayType`,
  `AnyLogicalArrayType`; the 7 `DataArrayTypes` records; `Object.Dataspace`, `Resource`, `DataObject`, `PutResponse`,
  `ContextInfo`, `Edge`, `ActiveStatusKind`, `ContextScopeKind`, `RelationshipKind`; `Uuid`.
- Encoder: zig-zag integers, little-endian floats, length-prefixed bytes and strings, arrays and maps as one block plus
  the zero terminator, union indexes (2.4), and a fast path writing `double[]`, `float[]` and `long[]` spans.
- Decoder: several blocks and negative block counts, unknown messages tolerated, allocation limits tied to the session
  limit.
- A hand-written, schema-specific codec is feasible: the schema is fixed and pinned, and the wire carries no schema.
  Prior art: the TypeScript client's reader, writer and validator are one 864-line file
  `[1079 ts/common/EtpAvro.ts]`; the C++ side uses generated code plus the bulk-array encoder
  `[828 etp/codegen/Messages.i.h]`, `[828 etp/EncoderSender.hpp:128-137]`. Generating the C# records and their codec
  from `P` at build time, or checking the generated code in, keeps field order exact. No general .NET Avro library was
  evaluated for this brief; one would need its datum readers and writers used without container files, with the unions
  of `DataValue` and `AnyArray`.
- Use `P`, not the TypeScript client's copy `[1079 ts/common/etp.json]`: that copy lacks `CoreOSDU.ResumeSession`,
  `CoreOSDU.ResumeSessionResponse`, `StoreOSDU.CopyDataObjectsByValue`, `StoreOSDU.CopyDataObjectsByValueResponse`,
  `DataspaceOSDU.CopyDataspaceFromRemote`, `DataspaceOSDU.CopyDataspaceFromRemoteResponse`, `Object.RemoteServer` and
  `Object.TableData`, and its `Datatypes.Protocol` enum lacks `CoreOSDU` and `StoreOSDU`; every other type is identical
  (both files parsed and compared).

**Framing and session layer.** Header encode and decode, flags, even ids from 2 (2.2); reply aggregation until FIN,
merging success maps and `ProtocolException` error maps (8.1); `Acknowledge` handling; `Chunk` reassembly on read
(7.3); gzip both ways (2.5); `Authorize` and `RequestSession` with the negotiated size and compression recorded (3.1,
3.2); ping keep-alive, `CloseSession`, and reconnection with a new session (3.4).

**Protocol clients.** Dataspace, DataspaceOSDU, Store (with chunked put), DataArray (whole, uninitialized, subarray,
metadata), Transaction (with retry on code 15) and Discovery; each returns per-key outcomes with code and server text,
redacted before they are stored.

**Planner.** Group objects and arrays under a byte budget (for example 80% of the session limit, as the C++ importer
does) and send one chunked object per message; slice arrays along the first dimension, then further dimensions when a
slab is too large (5.4); split the work into an objects-and-declarations transaction and fill transactions, each
retryable, rolling back after a failed commit; check dataspace existence before `PutDataspaces`; send every write
inside an explicit transaction (4.4).

**URI and XML guards before sending.** Canonical URIs (4.2); for each object a root `uuid`, a `schemaVersion`, the
`obj_` `xsi:type` for RESQML 2.0.1, and an EML `Citation` directly under the root (5.1); array paths unique in the
dataspace (5.3); references that resolve inside the delivery set or the dataspace; for RESQML 2.0.1 the
`EpcExternalPartReference` object of each HDF proxy.

**OSDU coupling.** `customData` legal and ACL values come from the flow's configuration and are never altered (4.3);
the dataspace record id (6.3) and the server-minted Activity URIs (5.2) are logged; each transaction's commit outcome
goes to the ledger.

**Test harness.** `R` shows the server run in `single` partition and `standalone` connectivity mode with
`--authN none --authZ none` against a disposable PostgreSQL `[R: "Running the Docker Runtime Image", lines 75-83]`,
`[R: "PostgreSQL Setup", lines 160-167]`; the gateway's local setup pulls the published server image
`community.opengroup.org:5555/osdu/platform/domain-data-mgmt-services/reservoir/open-etp-server/open-etp-server-main:latest`
`[1079 config.default.env:17]`. Started so, without `-j`, the server behaves as 8.7 describes, and without
entitlements every dataspace is writable (1.4). The server repository ships sample EPC and HDF5 pairs under `data/` (for example
`Volve_Demo_Horizons_Depth.epc` with its `.h5`), which its CI imports `[828 devops/azure/azure.gitlab-ci.yml:127]`.

**Cautions about the TypeScript client as prior art.** Its success rule for `PutDataObjects` (8.2) and its
`clientInstanceId`, a fixed byte sequence rather than a random UUID `[1079 ts/client/ETPClient.ts:364-366]`, are not to
be copied; its oversized `DeleteDataObjects` split reuses one header, and so one message id, for every part
`[1079 ts/protocols/StoreCustomer.ts:514-570]`, which the server's id check (2.2) would refuse (inference).

## 10. The REST gateway (project 1079)

- **What it is.** A NestJS REST and GraphQL gateway in front of the ETP server `[1079 README.md:3]`, exposed in OSDU
  deployments at `/api/reservoir-ddms/v2` `[1079 devops/gc/deploy/values.yaml:11]`,
  `[1079 devops/azure/chart/values.yaml:83]`. Its OpenAPI document declares that server, title `Reservoir DMS` and
  version `1.3.0-M27` `[1079 openapi.json]`.
- **Write endpoints** `[1079 openapi.json]`:
  - `POST /dataspaces` (`DataspaceMutationsAPI_PostDataspace`): an array of `{DataspaceId, Path, CustomData}`;
  - `POST /dataspaces/{dataspaceId}/transactions` (`TransactionsAPI_PostTransaction`): `{TimeoutPeriod, Retries}`,
    defaults 300 s and 6; `PUT /dataspaces/{dataspaceId}/transactions/{transactionId}` commits
    (`TransactionsAPI_CommitTransaction`) and `DELETE` on the same path rolls back
    (`TransactionsAPI_RollbackTransaction`);
  - `PUT /dataspaces/{dataspaceId}/resources?transactionId=` (`MutationsAPI_PutDataObject`): an array of Energistics
    JSON objects, converted to XML inside the gateway `[1079 ts/restApi/write-etp.module/ObjectWrite.controller.ts:556-576]`,
    so XML is not accepted there; an optional `validate` query runs XSD and reference checks;
  - `PUT /dataspaces/{dataspaceId}/resources/arrays?transactionId=` (`MutationsAPI_PutDataArray`): `Data` as a JSON
    number array (at most 1000 items) or a base64 string (at most 10,000,000 characters), with `Dimensions`,
    `Starts`/`Counts` and `ArrayType`;
  - `DELETE /dataspaces/{dataspaceId}/resources/{dataObjectType}/{guid}` and `DELETE /dataspaces/{dataspaceId}`;
  - `POST /dataspaces/{dataspaceId}/lock` (and `DELETE` on it to unlock);
  - `POST /dataspaces/{dataspaceId}/validate` (read-only validation);
  - `POST /dataspaces/{dataspaceId}/epc/upload`: an EPC with an optional HDF5 file, written in a transaction, objects
    in batches of 100, with optional registration in OSDU;
  - `POST /manifests/build` (6.5); `PUT /witsml/store` (XML or JSON).
- **Transactions live in one gateway process.** The gateway keeps each transaction's ETP session in an in-memory map
  keyed by the transaction id `[1079 ts/restApi/ControllerUtils.ts:94-110, 789-957]`, so all calls of a transaction must
  reach the same gateway instance (inference); a lost session is answered with HTTP 410
  (`WEBSOCKET_SESSION_TERMINATED`) `[1079 ts/restApi/ControllerUtils.ts:1319-1345]`.
- **Arrays over JSON** are slow or impractical at size ("impractical" for a 1 GB float array) `[1079 README.md:130-143]`.
- **Assessment** (inference). For an engine that produces XML and binary arrays, the direct ETP client is the better
  fit; the gateway remains the only place in these projects that builds OSDU manifests for delivered objects (6.5).

## 11. Where the contract and the code differ

| Topic | Contract or documentation | Code |
| --- | --- | --- |
| Default thread count | `R` says the server defaults to one thread per CPU `[R: "Performance Configuration", lines 267-273]` | Single-threaded unless `-j` is given `[828 bin/ServerCmds.cpp:482-487, 603-608]`, `[828 svr/Server.h:205]` (8.7) |
| `StartTransaction.dataspaceUris` default | `[""]` in `P` | `""` is an invalid URI and fails the request (4.4) |
| `DataObject.format` default | `"xml"` in `P` | Only the exact value `xml` is accepted on put; `GetDataObjects` also accepts the empty string (4.5, 7.3) |
| `Chunk.final` | Marks the last chunk in `P` | Not read; the FIN flag ends the sequence (4.5) |
| `GetResources` | `scope`, `depth`, `countObjects` in `P` | Ignored for a dataspace URI; `countObjects` not implemented (7.4) |
| `AuthorizeResponse.challenges` | In `P` | Never filled (3.1) |
| `DataArrayMetadata.logicalArrayType`, `preferredSubarrayDimensions` | In `P` | Not stored or returned (4.6, 7.5) |
| `PutResponse` arrays | In `P` | Always empty (4.5) |
| `GetDataSubarraysResponse` dimensions | A `DataArray` per slice in `P` | The whole array's dimensions (7.6) |
| String arrays | `ArrayOfString` is a transport type in `P` | Written but never returned by reads (7.6) |
| `Datatypes.Protocol` enum | Ordinals 26 to 28 for the OSDU protocols | Wire numbers 2400, 2404, 2424 (2.4) |
| Data object types | `supportedDataObjects` is a negotiation in ETP | Only entries containing `resqml20.` or `eml20.` are accepted (3.2) |
| Dataspace record kind | Server: `dataset--ETPDataspace:1.0.0`, unquoted URI | Gateway manifest: `1.0.1`, quoted URI (6.5) |
| Local transaction retries | Header comment: 5 attempts from 300 ms | 20 attempts from 400 ms, capped at 2 s (8.6) |

## 12. Open items

1. **Dataspace deletion purges a Storage record.** `DeleteDataspaces` makes the server call `purgeRecord` on the
   dataspace's record (7.8), and `PutDataspaces` purges a pre-existing record with the same id (4.3). This project's
   cleanup rule forbids purges; the route's design has to decide whether the engine may delete or re-create dataspaces
   at all, or only delete objects, and how a test run removes the dataspace record it caused (the reversible
   `POST /records/{id}:delete` leaves the Reservoir DDMS dataspace in place).
2. **Server-minted Activity objects** (5.2): deliver the flow's own Activity objects, or accept and record the minted
   ones.
3. **Record kind and URI form of the dataspace record** (6.5): the server writes kind version 1.0.0 with the unquoted
   URI, the gateway's manifest 1.0.1 with the quoted one, under the same id.
4. **Write lock scope.** The one-writer rule is held per server process (4.4); behaviour with several replicas needs a
   live check before the route relies on it.
5. **RESQML 2.2 and EML 2.3 array identifiers** (5.3) rest on the C++ importer's whole-array path only; confirm with a
   round trip against a live server (for example `GetDataArrayMetadata` with the `ALL` extension, 7.5).
6. **String arrays** (WITSML and PRODML string channels) cannot be read back (7.6); the route needs another
   representation for them.
7. **Writes without an explicit transaction** (4.4, 8.7): the readings that no local transaction exists on a server
   without extra worker threads, and that `PutDataArrays` rolls back its local transaction, are inferences; the
   server's own test `test_DataArray9_PutDataArrays_upsert` puts an array outside a transaction on a fixture session
   with the default thread count and expects success `[828 tests/openkv/ArrayProtoTests.cpp:1338-1408]`,
   `[828 src/lib/oes/testing/eml/ServerFixture.cpp:83, 386-420]`, `[828 svr/Server.h:175]`, which this reading says
   should fail. The route avoids the question by always using explicit transactions; a live check settles it.
8. **Record removal on a rolled-back create** (4.3): a dataspace created inside a transaction that is rolled back
   leaves its Storage record (inference); cleanup must cover that case.
