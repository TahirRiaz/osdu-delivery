# sqlflow auth

## Synopsis

```bash
sqlflow auth [--scope storage|keyvault|arm|<uri>]
```

## Description

Verifies Azure authentication end to end in the current environment. The command takes no flow document; it is a pure environment check. It reports the raw value of the `SQLFLOW_AZURE_AUTH` environment variable and the auth mode it resolves to, then actually acquires a token for the chosen scope through `IAzureCredentialFactory` (src/SqlFlow.Azure/IAzureCredentialFactory.cs, implemented by src/SqlFlow.Azure/AzureCredentialFactory.cs), the one credential factory `AddSqlFlowEngine` registers (src/SqlFlow.Execution/SqlFlowEngineServices.cs) and every Azure access path shares:

- Key Vault secret resolution: every `${keyvault:...}` reference in a flow, a mapping, or the control plane's configuration (src/SqlFlow.Azure/AzureKeyVaultSecretProvider.cs and src/SqlFlow.Azure/AzureKeyVaultSecretVault.cs).
- The Azure blob file store, which reads drop files from `abfss://`, `wasbs://` and `https://<account>.blob|dfs.core.windows.net` locations (src/SqlFlow.Azure/AzureBlobFileStore.cs), and the blob writer that puts work batches and known-state publications on the lake under the same identity (`AzureBlobFileWriter` in src/SqlFlow.Delivery/Storage/FileStore.cs).
- The control plane's Microsoft Graph email sender, whenever no explicit app registration is configured for it (src/SqlFlow.ControlPlane/Notifications/GraphEmailSender.cs).

A successful token from `sqlflow auth` therefore confirms the ambient credential every one of those paths reads, before you run a flow that needs it. Like every CLI command, it first applies the nearest git-ignored `.sqlflow/env` file (searched from the current directory upward; a variable already in the process environment always wins), so `SQLFLOW_AZURE_AUTH` and the `AZURE_*` family can come from there during local development.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| (none) | n/a | `sqlflow auth` takes no positional arguments and no flow document. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--scope` | string | `storage` | Token audience. An alias or a verbatim scope URI; see the alias table below. |

`--scope` aliases (matched case-insensitively):

| Value | Resolved scope URI |
| --- | --- |
| `storage`, `adls`, `blob` | `https://storage.azure.com/.default` |
| `keyvault`, `vault` | `https://vault.azure.net/.default` |
| `arm`, `management` | `https://management.azure.com/.default` |
| anything else | used verbatim as the scope URI |

## Authentication modes: SQLFLOW_AZURE_AUTH

`SQLFLOW_AZURE_AUTH` is parsed in exactly one place (`AzureAuth.Mode()` in src/SqlFlow.Azure/AzureAuth.cs), so Key Vault and blob storage obey one auth intent. Values are trimmed and matched case-insensitively:

| Value | Mode | Credential built |
| --- | --- | --- |
| `serviceprincipal`, `sp` | ServicePrincipal | `ClientSecretCredential` from `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`. A missing variable fails with `Service-principal auth requires environment variable '<name>'.` |
| `managedidentity`, `mi`, `msi` | ManagedIdentity | `ManagedIdentityCredential`. A non-blank `AZURE_CLIENT_ID` selects that user-assigned identity; otherwise the system-assigned identity is used. |
| `azurecli`, `cli`, `azlogin` | AzureCli | `AzureCliCredential` (uses the `az login` session). |
| any other value, or unset | DefaultChain | `DefaultAzureCredential` with the options described below. |

The variable and the `AZURE_*` family it relies on are listed, with every other variable, in [Environment variables and secrets](../../environment-variables.md).

### The default chain and in-Azure detection

In DefaultChain mode the credential is `DefaultAzureCredential` with options from `AzureEnvironment.DefaultCredentialOptions()` (src/SqlFlow.Azure/AzureEnvironment.cs):

- The managed-identity credential is excluded when the process is not running in Azure. This avoids the chain stalling on the IMDS probe, which has no endpoint on a developer machine.
- The environment, Azure CLI, Visual Studio, VS Code, and interactive browser credentials are all enabled, so a signed-in developer authenticates with no configuration.

Whether the process is "in Azure" is decided by `AzureEnvironment.IsRunningInAzure()`:

- The explicit override `IS_RUNNING_IN_AZURE` wins in both directions when set: `true` (case-insensitive) forces in-Azure on; any other non-blank value forces it off.
- Otherwise the process counts as in Azure when any of these variables is non-empty: `WEBSITE_INSTANCE_ID`, `FUNCTIONS_WORKER_RUNTIME`, `AZURE_CONTAINER_INSTANCE_ROOT_PATH`, `KUBERNETES_SERVICE_HOST`, `AZURE_VM_RESOURCE_GROUP`, `MSI_ENDPOINT`, `MSI_SECRET`, `IDENTITY_HEADER`, `APPSETTING_WEBSITE_SITE_NAME`.

The credential is cached per distinct auth configuration (mode, principal, and the in-Azure decision) and shared by every consumer, so repeated authentications become cached-token lookups. The detection rules are covered by tests/SqlFlow.Core.Tests/Azure/AzureEnvironmentTests.cs.

## Behavior and output

The command prints, in order:

1. `SQLFLOW_AZURE_AUTH: <raw value or (unset)> -> <resolved mode>`. For DefaultChain the label explains the chain composition: in Azure it reads `default chain (managed identity -> az CLI -> env)`; off-cloud it reads `default chain (az CLI -> env; managed identity excluded off-cloud)`. The other modes print as `ServicePrincipal`, `ManagedIdentity`, or `AzureCli`.
2. `Acquiring a token for scope: <resolved scope URI>`.
3. On success: `OK   acquired a token (expires <timestamp> UTC). Azure auth works.`
4. On failure, to stderr: `FAIL could not acquire a token: <underlying message>`. The failure is reported with the cause message only, no stack trace, because the failure itself is the diagnostic answer. A service-principal mode with a missing `AZURE_*` variable fails here too, before any network call.

## Examples

Check the default storage scope on a developer machine with an `az login` session:

```bash
sqlflow auth
```

```text
SQLFLOW_AZURE_AUTH: (unset) -> default chain (az CLI -> env; managed identity excluded off-cloud)
Acquiring a token for scope: https://storage.azure.com/.default
OK   acquired a token (expires 2026-07-02 13:05:11Z UTC). Azure auth works.
```

Verify that a CI service principal can reach Key Vault before running a flow that resolves `${keyvault:...}` references:

```bash
export SQLFLOW_AZURE_AUTH=sp
export AZURE_TENANT_ID=00000000-0000-0000-0000-000000000000
export AZURE_CLIENT_ID=11111111-1111-1111-1111-111111111111
export AZURE_CLIENT_SECRET=...
sqlflow auth --scope keyvault
```

Verify a user-assigned managed identity on an Azure VM, against a verbatim scope URI:

```bash
export SQLFLOW_AZURE_AUTH=mi
export AZURE_CLIENT_ID=22222222-2222-2222-2222-222222222222
sqlflow auth --scope https://management.azure.com/.default
```

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| 0 | A token was acquired for the resolved scope. |
| 1 | Token acquisition failed; the cause is printed to stderr as `FAIL could not acquire a token: <message>`. |

## See also

- [Environment variables and secrets](../../environment-variables.md): the canonical list, including `SQLFLOW_AZURE_AUTH`, the `AZURE_*` family, and the `.sqlflow/env` file.
- [Deployment guide](../guides/deployment.md): the Azure Container Apps template sets `SQLFLOW_AZURE_AUTH=mi` and `AZURE_CLIENT_ID` for its user-assigned identity.
- [Control-plane verbs](control-plane.md): `sqlflow login` and `sqlflow whoami` cover the control plane's own credential, which is separate from Azure authentication.
- [Authentication and identity](../concepts/authentication-and-identity.md): how the control plane signs users in.
