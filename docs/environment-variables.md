# Environment Variables and Secrets: the Canonical Contract

Flow and mapping documents live under source control. Credentials never do. This page is the one contract
that makes those two facts compatible: which names exist, where values come from on each platform, and what
the tool does to keep secrets out of your repository. Everything here works identically on Windows, Linux,
macOS, and containers; OSDU Delivery is plain .NET and takes no platform-specific dependencies for any of it.

## The rule

A document carries secret REFERENCES, never values. Two reference forms exist:

| Form | Example | Use |
| --- | --- | --- |
| Environment reference | `${env:OSDU_CLIENT_SECRET}` | The value is an environment variable of the process that runs the flow (a compute node, or the CLI). |
| Key Vault reference | `${keyvault:my-vault/osdu-client-secret}` | The value lives in Azure Key Vault; the process fetches it with its Azure identity. |

A literal secret in a document is a defect. `sqlflow validate` and `sqlflow run` print a loud warning whenever a
document embeds a `Password=`-style literal, naming the alternatives; the warning never echoes the value. The
managed sync reports the same warning on the repository's activity trace, and a document that embeds a literal
is never snapshotted into the catalog (its runs materialize the exact commit from git instead).

## The variable family

All names are UPPERCASE: environment variables are case-sensitive on Linux, and one spelling everywhere is the
only convention that survives a mixed estate. The `SQLFLOW_` prefix is kept from the platform.

| Variable | Read by | Meaning |
| --- | --- | --- |
| `SQLFLOW_CATALOG_DB` | control plane, `sqlflow worker`, `sqlflow db`, `sqlflow runs cancel` | The catalog connection string. The control plane also accepts it as `ControlPlane:Catalog:ConnectionReference`, which may itself be a `${keyvault:...}` reference. |
| `SQLFLOW_URL` | the remote CLI verbs | The control plane base URL (`https://controlplane.example.com`); `--url` overrides it. |
| `SQLFLOW_TOKEN` | the remote CLI verbs | A personal access token or session token; `--token` overrides it, and `sqlflow login` stores one in the credentials file instead. |
| `SQLFLOW_CREDENTIALS_FILE` | `sqlflow login`, `logout`, and the remote verbs | Where the CLI keeps stored credentials (default: under the user profile). |
| `SQLFLOW_REPO` | `sqlflow run` with a catalog connection, `sqlflow doctor` | The repository a local run is attributed to in the catalog when `--repo` is omitted (after that, the flow's folder name). The remote verbs take `--repo` explicitly. |
| `SQLFLOW_GIT_TOKEN` | control plane (managed sync), compute nodes (materialization) | The token for private git remotes, when a repo source declares no credential reference of its own. |
| `SQLFLOW_GIT_USERNAME` | control plane, compute nodes | The username paired with the git token on hosts that need one (Bitbucket app passwords, `x-token-auth`). |
| `SQLFLOW_AZURE_AUTH` | every Azure access path (Key Vault, blob storage) | How the process authenticates to Azure: `default`, `cli` (also `azurecli`, `azlogin`), `managedidentity` (also `mi`, `msi`), `serviceprincipal` (also `sp`). The standard `AZURE_TENANT_ID` / `AZURE_CLIENT_ID` / `AZURE_CLIENT_SECRET` family applies, exactly as the Azure SDK defines it. |
| `SQLFLOW_TEST_DB` | the test suites | A disposable SQL Server database the DB-backed suites migrate and seed; they skip when it is unset or unreachable. Never a catalog holding real data. |
| `SQLFLOW_DELIVERY_ALLOW_LOOPBACK` | compute nodes, the CLI | `true` lets a delivery flow target a loopback address (a local OSDU stub, the tests). Off by default: the URL guard refuses loopback and private targets. |

The container images add three worker settings that the entrypoint turns into CLI options: `SQLFLOW_WORKER_POOL`
(the pools the node serves), `SQLFLOW_WORKER_POLL_SECONDS` (the queue poll cadence) and
`SQLFLOW_WORKER_DRAIN_SECONDS` (how long a stopping node waits for its in-flight runs).

Everything else the control plane reads is ASP.NET Core configuration under the `ControlPlane` section
(`ControlPlane__Jwt__SigningKey`, `ControlPlane__Bootstrap__AdminPasswordReference`,
`ControlPlane__Cors__AllowedOrigins__0`, and so on), including
`ControlPlane__MaxRequestBodyMegabytes`, the API's request body ceiling (default 64, set on purpose rather than
left at the server default): environment variables with `__` as the section separator,
or `appsettings.json`. Every value that is a secret takes a `${env:...}` or `${keyvault:...}` reference.

## Where values come from

- **Compute nodes** hold the credentials their pool's flows need: every `${env:...}` reference a flow uses is an
  environment variable on the node (a Kubernetes secret, a Container Apps secret from Key Vault, a systemd
  environment file). Nothing data-plane passes through the control plane.
- **The control plane** holds the catalog connection, the JWT material, the bootstrap admin, and the git token.
- **Local development** puts values in the git-ignored `.sqlflow/env` file at the repository root. The CLI loads
  it on start (the process environment always wins over it), and `dev.bat` exports it into the control plane
  process it launches.

## Redaction

Resolved secrets are redacted before they reach any log line, run event, error message, run artifact, or
catalog row. The catalog stores redacted errors only, and the run trace never carries a resolved value.
