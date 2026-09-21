# Environment Variables and Secrets

Flow, mapping and cache documents live under source control. Credentials never do. This page is the contract that
makes those two facts compatible for OSDU Delivery: which names exist, where values come from on each tier, and
what the tool does to keep secrets out of a repository.

SQLFlow's own variable family is documented in the vendored tree
([../../sqlflow/docs/environment-variables.md](../../sqlflow/docs/environment-variables.md)); this page lists the
ones an OSDU Delivery deployment actually sets, and the ones the module adds.

## The rule

A document carries secret REFERENCES, never values. Two reference forms exist:

| Form | Example | Use |
| --- | --- | --- |
| Environment reference | `${env:OSDU_CLIENT_SECRET}` | The value is an environment variable of the process that runs the flow (a compute node, or the CLI). |
| Key Vault reference | `${keyvault:my-vault/osdu-client-secret}` | The value lives in Azure Key Vault; the process fetches it with its Azure identity. |

A literal secret in a document is a defect. `sqlflow validate` and `sqlflow run` print a loud warning whenever a
document embeds a `Password=`-style literal, naming the alternatives; the warning never echoes the value. The
managed sync reports the same warning on the repository's activity trace.

All names are UPPERCASE: environment variables are case-sensitive on Linux, and one spelling everywhere is the
only convention that survives a mixed estate. The `SQLFLOW_` prefix is kept from the platform, as the project's
naming rule says.

## Which tier reads what

The three tiers hold different credentials on purpose, and the split is load-bearing: nothing data-plane ever
passes through the control plane.

### The control plane

| Variable | Meaning |
| --- | --- |
| `SQLFLOW_CATALOG_DB` | The metadata connection string (`SQLFlow` in the shipped deployments): SQLFlow's catalog schema, and the OSDU module's `osdu` schema beside it. Also accepted as `ControlPlane:Catalog:ConnectionReference`, which may itself be a `${keyvault:...}` reference. The module's schema is elsewhere only when the module declares a connection of its own. |
| `SQLFLOW_GIT_TOKEN`, `SQLFLOW_GIT_USERNAME` | The credential managed sync fetches private flow repositories with, when a repo source declares no reference of its own. The username is for hosts that pair the token with one (Bitbucket app passwords, `x-token-auth`); GitHub needs the token alone. |
| `SQLFLOW_AZURE_AUTH` | How the process authenticates to Azure for Key Vault and storage: `default`, `cli`, `managedidentity` (`mi`), `serviceprincipal` (`sp`). The standard `AZURE_TENANT_ID` / `AZURE_CLIENT_ID` / `AZURE_CLIENT_SECRET` family applies exactly as the Azure SDK defines it. |

Everything else the control plane reads is ASP.NET Core configuration under the `ControlPlane` section
(`ControlPlane__Jwt__SigningKey`, `ControlPlane__Bootstrap__AdminPasswordReference`,
`ControlPlane__Cors__AllowedOrigins__0`, and so on): environment variables with `__` as the section separator, or
`appsettings.json`. Every value that is a secret takes a `${env:...}` or `${keyvault:...}` reference.

### A compute node

A node speaks only the node protocol. It opens **no catalog connection**: the run's definition, its snapshotted
YAML, its lineage context and its live trace all travel over that protocol.

| Variable | Meaning |
| --- | --- |
| `SQLFLOW_URL` | The control plane base URL the node takes its work from. `--url` overrides it. |
| `SQLFLOW_TOKEN` | A personal access token minted with the `node` scope, or a `${env:...}` / `${keyvault:...}` reference to one, resolved on the node. `--token` overrides it. |
| The OSDU module database connection reference | The delivery engine reads and writes the `osdu` ledger per record while it plans and delivers. Because a node has no catalog connection, the module database needs a reference of its own on this tier. |
| `SQLFLOW_GIT_TOKEN`, `SQLFLOW_GIT_USERNAME` | For materializing a SHA-pinned run from a private remote. |
| `SQLFLOW_AZURE_AUTH` (with the `AZURE_*` family) | How `${keyvault:...}` references and Azure storage locations authenticate from this node. |
| `SQLFLOW_DELIVERY_ALLOW_LOOPBACK` | `true` lets a delivery flow target a loopback address (a local OSDU stub, the tests). Off by default: the URL guard refuses loopback and private targets. |
| `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` | The private ranges the delivery engine may reach, as CIDR ranges or addresses separated by commas (`10.20.0.0/16,fd12:3456::/48`): the ranges an OSDU, a storage account or a proxy behind a private endpoint resolves to. Empty by default, which reaches public addresses only. The guard checks the address a URL names and every address a host name resolves to when a connection opens, and every redirect; loopback (see above), link-local, cloud metadata and the Azure wireserver address are never reachable, whatever this lists. |
| every `${env:...}` reference the pool's flows declare | The ingestion database connection the OSDU flow reads through, the payload storage, and the OSDU credentials. |

The container image turns three further variables into node options through its entrypoint:
`SQLFLOW_WORKER_POOL` (the pools this node serves), `SQLFLOW_WORKER_POLL_SECONDS` (how long each poll waits for
work) and `SQLFLOW_WORKER_DRAIN_SECONDS` (how long a stopping node finishes what it holds; keep it below the
orchestrator's termination grace period).

### The CLI

| Variable | Meaning |
| --- | --- |
| `SQLFLOW_URL`, `SQLFLOW_TOKEN` | The control plane and the credential the remote verbs use; `--url` and `--token` override them, and `sqlflow login` stores a token in the credentials file instead. |
| `SQLFLOW_CREDENTIALS_FILE` | Where the CLI keeps stored credentials (default: under the user profile). |
| `SQLFLOW_CATALOG_DB` | The default `--db` for the verbs that talk to a database directly: `sqlflow db`, and the OSDU verbs `check`, `cache` and `template`, whose templates and cache versions live in the catalog database. |
| `SQLFLOW_REPO` | The repository a local run is attributed to when `--repo` is omitted. |
| `SQLFLOW_TEST_DB` | A disposable SQL Server database the DB-backed suites migrate and seed; they skip when it is unset or unreachable. Never point it at a database holding real data. |

## The estate's flow references

These are not read by any code: they are the names the flow documents reference, wired on the node by the
deployment. An estate names its own; the deployment templates wire the first two for every estate, so a flow
document moves from test to prod unchanged.

| Reference | What it points at |
| --- | --- |
| `${env:SQLFLOW_CONN_PRE}` | The database the pre-ingestion flows land the source files in. |
| `${env:SQLFLOW_CONN_DWH}` | The database the ingestion flows load the keyed ingestion tables into, which the OSDU flow reads. |
| `${env:OSDU_URL}` | The OSDU endpoint the sample estate delivers to. |
| `${env:OSDU_DATA_PARTITION}` | The partition the sample estate delivers to: the `data-partition-id` its requests carry, the partition whose cache its flows read, and the partition its record ids are minted in. |
| `${env:OSDU_ACL_OWNER}`, `${env:OSDU_ACL_VIEWER}` | The entitlements groups every record the sample estate delivers is owned and readable by. |
| `${env:OSDU_LEGAL_TAG}` | The legal tag every record the sample estate delivers carries. |
| `${env:OSDU_TOKEN_URL}`, `${env:OSDU_SCOPE}`, `${env:OSDU_CLIENT_ID}`, `${env:OSDU_CLIENT_SECRET}` | The OAuth2 client-credentials flow the sample estate authenticates with. |
| `${env:APIM_KEY}` | The API management subscription key the sample estate's target requires. |

A `plan` run needs no OSDU target, so these can stay empty until one exists.

## The central configuration

A reference does not have to be a variable on every node. The control plane holds a configuration of its own in
`[osdu].[ConfigProperty]`, and attaches it to every run it queues, so a node resolves `${env:NAME}` from what the run
carried before it looks at its own environment. A reference the configuration does not name still comes from the node,
so an estate can hold some values centrally and leave the rest where they are.

| Where | What it covers |
| --- | --- |
| The control plane | Every repository it serves. |
| One repository | The flows that repository holds, over the control plane's own value of the same name. |

A property holds a non-secret value (a partition, an entitlements group, a legal tag, a base URL) or a `${env:...}` or
`${keyvault:...}` reference, which travels unresolved and is resolved on the node. **A literal secret in a property is a
defect**: the row is ordinary catalog content and the run payload that carries it is readable beside the run. Point a
property at a secret with a reference instead, and the node resolves it.

```bash
sqlflow config set OSDU_DATA_PARTITION --value dev --db <conn-ref>
sqlflow config set OSDU_LEGAL_TAG --value dev-equinor-private-default --repo <repo id> --db <conn-ref>
sqlflow config effective --repo <repo id> --db <conn-ref>   # what a run of that estate is given
```

The same through the API: `GET /api/v1/delivery/config`, `PUT /api/v1/delivery/config/{name}` and
`DELETE /api/v1/delivery/config/{name}` (both admin, both taking an optional `repoId`), and
`GET /api/v1/delivery/config/effective/{repoId}`.

### What the OSDU flow kind supplies

Where a record goes and under whose access and legal terms belongs to the kind, not to any one flow. A mapping declares
the parameters it fills and a flow does not repeat them: `dataPartition`, `aclOwner`, `aclViewer` and `legalTag` default
to `${env:OSDU_DATA_PARTITION}`, `${env:OSDU_ACL_OWNER}`, `${env:OSDU_ACL_VIEWER}` and `${env:OSDU_LEGAL_TAG}`. A flow
that names its own value under `render.parameters` still wins, so a document can pin a destination when it has to.

## Where values come from

- **Compute nodes** hold the credentials their pool's flows need: every `${env:...}` reference a flow uses is an
  environment variable on the node (a Kubernetes secret, a Container Apps secret from Key Vault, a systemd
  environment file), alongside the node token and the OSDU module database reference.
- **The control plane** holds the catalog connection, the JWT material, the bootstrap admin and the git token.
- **Local development** puts values in the git-ignored `.sqlflow/env` file at the repository root. The CLI loads
  it on start (the process environment always wins over it), and `dev.bat` exports it into the control plane
  process it launches. `osdu/tools/dev-setup.ps1` generates that file from the live estate.

## Redaction

Resolved secrets are redacted before they reach any log line, run event, error message, run artifact or ledger
row. The catalog stores redacted errors only, the run trace never carries a resolved value, and an HTTP error
names the request URL without its query string, so a signed upload URL's credential never reaches an error
message.
