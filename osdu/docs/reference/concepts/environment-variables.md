---
id: delivery-concept-environment-variables
title: "Environment variables OSDU Delivery adds (SQLFLOW_OSDU_DB, SQLFLOW_DELIVERY_*, OSDU_TELEMETRY_*) and which tier reads each"
type: concept
summary: "Every environment variable and secret reference OSDU Delivery adds to SQLFlow's, which tier reads it, and where a flow's references resolve from."
keywords:
  - environment variables
  - sqlflow_osdu_db
  - sqlflow_delivery_private_networks
  - sqlflow_delivery_allow_loopback
  - sqlflow_delivery_allow_insecure_tls
  - osdu_telemetry_exporter
  - osdu_data_partition
  - osdu_legal_tag
  - osdu_acl_owner
  - secret reference
  - "${env:"
  - "${keyvault:"
  - central configuration
  - which tier
  - osdu_data_partition default
related:
  - concept-environment-variables
  - concept-connections-and-secrets
  - delivery-concept-control-plane
  - delivery-cli-config
  - delivery-cli-worker
  - delivery-guide-deployment
  - delivery-concept-run-trace-and-metrics
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Hosting/OsduModuleDatabase.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryModuleOptions.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery/Http/NetworkPolicy.cs
  - osdu/src/SqlFlow.Delivery/Http/HttpClientBuilder.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryServices.cs
  - osdu/src/SqlFlow.Delivery/Model/DeliveryDestination.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryConfigStore.cs
  - osdu/src/SqlFlow.Delivery.Telemetry/TelemetryOptions.cs
  - osdu/deploy/compose/docker-compose.yml
  - osdu/deploy/k8s/worker-pool.yaml
  - osdu/deploy/k8s/controlplane.yaml
  - sqlflow/src/SqlFlow.Cli/Program.cs
  - sqlflow/src/SqlFlow.Core/Secrets/EnvSecretProvider.cs
---

# Environment variables and secret references OSDU Delivery adds

A flow, a mapping and a cache document carry secret references (`${env:NAME}`, `${keyvault:vault/secret}`), never
values; the values arrive through each process's environment. That contract, SQLFlow's own variables
(`SQLFLOW_CATALOG_DB`, `SQLFLOW_URL`, `SQLFLOW_TOKEN`, `SQLFLOW_GIT_TOKEN`, `SQLFLOW_AZURE_AUTH`, `SQLFLOW_CONN_<NAME>`,
`SQLFLOW_WORKER_*`) and the `.sqlflow/env` file are documented in
[SQLFlow's environment variables page](../../../../sqlflow/docs/reference/concepts/environment-variables.md) and
[connections and secrets](../../../../sqlflow/docs/reference/concepts/connections-and-secrets.md). This page lists only what
OSDU Delivery adds, and which tier reads each.

The three tiers read different things on purpose. A node delivers, so it holds the module database connection and the
credentials its pool's flows use. The control plane never delivers, but it answers the reads a person waits on (a probe,
a record read back, a preview, a source read, the explorer) with the flow's own credentials, so it is given the same flow
references as a node. The CLI reads what a local run needs.

## Variables the module reads

| Variable | Read by | Meaning |
| --- | --- | --- |
| `SQLFLOW_OSDU_DB` | node (always), CLI and control plane (when set) | The connection string of the database holding the `osdu` schema: the ledger, mappings, templates, caches, partitions and central configuration. Hosts read it as the reference `${env:SQLFLOW_OSDU_DB}`. A node opens no catalog connection, so it always needs it; the control plane and the CLI use it only when it is set, and otherwise use the catalog's database (on the control plane, `Osdu:Database:Connection` wins over it). A node's login needs rights on schema `osdu` alone; whatever runs `sqlflow db migrate` (or the control plane's bootstrap) needs the rights to create it. |
| `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` | every process that reaches OSDU: node, control plane, CLI | The private ranges a flow may reach, as CIDR ranges or single addresses separated by commas, semicolons or white space (`10.20.0.0/16,fd12:3456::/48`): the ranges an OSDU, a storage account or a proxy behind a private endpoint resolves to. Empty by default, which reaches public addresses only. Checked against the address a URL names and every address a host name resolves to, on every redirect. Link-local and cloud metadata addresses and the Azure wireserver are never reachable, whatever it lists. |
| `SQLFLOW_DELIVERY_ALLOW_LOOPBACK` | node, control plane, CLI | `true` lets a flow target a loopback address (a local OSDU stub, the tests). Off by default. |
| `SQLFLOW_DELIVERY_ALLOW_INSECURE_TLS` | node, control plane, CLI | `true` lets a flow that declares `reliability.verifyTls: false` run in this process. Off by default: a repository document cannot take a process off TLS on its own. Trusting the issuing authority on that host is the better fix. |
| `OSDU_TELEMETRY_EXPORTER`, `OSDU_TELEMETRY_OTLP_ENDPOINT`, `_OTLP_PROTOCOL`, `_OTLP_HEADERS`, `_AZURE_MONITOR_CONNECTION`, `_EXPORT_SECONDS`, `_SERVICE_NAME`, `_SERVICE_INSTANCE` | node only | Where a node sends the module's metrics; the control plane takes the same settings from `Osdu:Telemetry` ([run trace and metrics](run-trace-and-metrics.md#where-the-metrics-go)). `_OTLP_HEADERS` and `_AZURE_MONITOR_CONNECTION` hold a `${env:...}` or `${keyvault:...}` reference, never the value. A one-shot CLI command exports nothing. |

What happens when one is wrong:

- A node started without `SQLFLOW_OSDU_DB` refuses to start: `ERROR  the worker refuses to start:` followed by
  `Environment variable 'SQLFLOW_OSDU_DB' is not set.` It also refuses a module database that is missing, behind or ahead
  of its build.
- A CLI or control plane with no module database answers any operation that needs one with
  `This host has no osdu database connection (Osdu:Database:Connection or SQLFLOW_OSDU_DB); the delivery ledger, templates and caches are unavailable.`
- A flow asking for `verifyTls: false` without the switch is refused:
  `This flow declares reliability.verifyTls: false, and this deployment verifies every certificate. A deployment that has to reach a target with a self-signed or enterprise-internal certificate sets SQLFLOW_DELIVERY_ALLOW_INSECURE_TLS=true on the nodes that reach it; the honest fix is to trust the issuing authority on those nodes instead.`
- A loopback target without its switch is refused as `a loopback address, which only a process with SQLFLOW_DELIVERY_ALLOW_LOOPBACK reaches`.
- An entry of `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` that is neither a range nor an address is refused:
  `SQLFLOW_DELIVERY_PRIVATE_NETWORKS lists '<entry>', which is not a CIDR range such as 10.20.0.0/16 or fd12:3456::/48, nor an address.`

## References the delivery kind supplies

Where a record goes and under whose access and legal terms belongs to the delivery kind. A mapping declares the
parameters it fills; when the flow does not set one under `render.parameters`, the kind supplies a reference:

| Parameter | Reference supplied | Fills |
| --- | --- | --- |
| `dataPartition` | the partition the flow is bound to: the run's partition for a flow that works in partitions, its `data-partition-id` header (as written, a literal or a reference) for one that names its partition there; `${env:OSDU_DATA_PARTITION}` only for a flow bound to no partition | The partition every record id is minted in, and whose cache the render reads. |
| `aclOwner` | `${env:OSDU_ACL_OWNER}` | `acl.owners` |
| `aclViewer` | `${env:OSDU_ACL_VIEWER}` | `acl.viewers` |
| `legalTag` | `${env:OSDU_LEGAL_TAG}` | `legal.legaltags` |

A value the flow sets wins. A flow that names its partitions (`partitions:`) is given `dataPartition` as the partition each
run is bound to, written literally, never reads `OSDU_DATA_PARTITION`, and may not set `dataPartition` itself. A flow
whose `target.headers.data-partition-id` names its partition mints its ids in that partition, never in
`OSDU_DATA_PARTITION`.

## References a flow names

Everything else is a name the flow documents choose; no code reads it. Every reference a pool's flows use has to resolve
on every node of that pool and on the control plane. The examples in this documentation use these names:

| Reference | Points at |
| --- | --- |
| `${env:OSDU_DATA_DB}` | The ingestion database the delivery flow's `source.connection` reads. The shipped deployment assets wire the two databases of the delivery chain as `SQLFLOW_CONN_PRE` (what pre flows land) and `SQLFLOW_CONN_DWH` (the keyed ingestion tables). |
| `${env:OSDU_URL}` | The OSDU endpoint (`target.endpoint`). |
| `${env:OSDU_TOKEN_URL}`, `${env:OSDU_CLIENT_ID}`, `${env:OSDU_CLIENT_SECRET}`, `${env:OSDU_SCOPE}` | The OAuth2 client-credentials exchange (`target.auth`). |

The wellbore flow, `flows/welldb-wellbore-03-delivery.yaml`, names them all:

```yaml
flowType: delivery
name: welldb-wellbore-03-delivery
batch: welldb

partitions: [dev]

source:
  connection: ${env:OSDU_DATA_DB}
  record:
    object: OsduData.silver.Wellbore
    key: [wellbore_id]
    primaryKey: RecId
  lastModified: update_date
  work: ../.work/wellbore

render:
  mapping: Wellbore@1.0.0

target:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body:
        scope: ${env:OSDU_SCOPE}
  protocol: storage
```

A flow may also set a parameter of the kind itself, and name a secret in a key vault rather than the environment. Written
so, the same file's `render` and `target.auth` read:

```yaml
render:
  mapping: Wellbore@1.0.0
  parameters:
    legalTag: ${env:WELLDB_LEGAL_TAG}   # set by the flow; aclOwner and aclViewer come from the kind's references

target:
  auth:
    secretRef: ${keyvault:welldb-vault/osdu-client-secret}
```

## Where a reference resolves from

A reference does not have to be a variable on every node. The control plane keeps a central configuration in the `osdu`
schema and attaches it to every delivery, retrieval, cache, assertion, dimension and inventory run it queues
([the control plane](control-plane.md#the-central-configuration-on-every-run)).
A run bound to a partition resolves `${env:NAME}` in this order:

1. the repository's value for that partition;
2. the control plane's value for that partition;
3. the repository's value for no partition;
4. the control plane's value for no partition;
5. the environment of the process running it.

A run's trace names, for each `${env:NAME}` it resolves, whether the central configuration or the node's environment gave
the value, never the value itself ([the run trace](run-trace-and-metrics.md#the-steps-of-a-runs-trace)). A
`${keyvault:...}` reference is always the node's and is not named.

A property holds a value or a `${env:...}` / `${keyvault:...}` reference, which travels unresolved and is resolved where
the run executes. A literal secret in a property is a defect: the row is ordinary catalog content, readable in the run
payload. Properties are set with [sqlflow config](../cli/config.md) (`config set`, `config effective`).

## What each tier is given

| Tier | Beyond SQLFlow's own variables |
| --- | --- |
| Control plane | `Osdu:Database:Connection` or `SQLFLOW_OSDU_DB` only when the `osdu` schema is in a database of its own; every `${env:...}` reference the flows use; `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` (and the two switches, where the nodes have them). The `Osdu:*` settings ([the control plane](control-plane.md#configuration)). |
| Node | `SQLFLOW_OSDU_DB` (required); every `${env:...}` reference its pool's flows use; `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` and the switches it needs; `OSDU_TELEMETRY_*` when its metrics are exported. |
| CLI | `SQLFLOW_OSDU_DB` when the `osdu` schema is not in the database `--db` names; the references of the flows it runs locally; the network switches. |
| MCP server | Its own variables ([the MCP server](../guides/mcp.md#running-it-as-a-service)); no secret. |

Keep the control plane's list of flow references the same as the nodes': a reference missing there makes a probe or a
preview fail while the deliveries still work.

## See also

- [SQLFlow: environment variables and .sqlflow/env](../../../../sqlflow/docs/reference/concepts/environment-variables.md)
- [The control plane](control-plane.md)
- [Deploying OSDU Delivery](../guides/deployment.md)
- [sqlflow config](../cli/config.md)
