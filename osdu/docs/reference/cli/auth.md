# sqlflow auth in an OSDU Delivery estate

```bash
sqlflow auth [--scope storage|keyvault|arm|<uri>]
```

`sqlflow auth` verifies Azure authentication end to end: it reports the raw value of `SQLFLOW_AZURE_AUTH` and the
mode it resolves to, then actually acquires a token for the chosen scope through the one credential factory every
Azure access path shares. The modes, the `AZURE_*` family, the default chain and its in-Azure detection, the
output and the exit codes are documented in
[../../../../sqlflow/docs/reference/cli/auth.md](../../../../sqlflow/docs/reference/cli/auth.md).

This page says which OSDU Delivery paths that credential is, so a green `sqlflow auth` means something concrete
here.

## What the credential is used for

| Path | Scope to check |
| --- | --- |
| Resolving `${keyvault:...}` references in a flow, a mapping or the control plane's own configuration: the OSDU client secret, an API management key, a database connection. | `keyvault` |
| Reading the payload files a record points at, on `abfss://`, `wasbs://` or `https://<account>.blob\|dfs.core.windows.net` locations. | `storage` |
| Writing a run's work batch files to the flow's work location. | `storage` |
| The control plane's Microsoft Graph email sender for notifications, when no explicit app registration is configured for it. | (its own) |

A successful token therefore confirms the ambient credential all of those read, **before** a delivery run needs
it. That matters most on a compute node: the node is where every one of these resolves, and a credential problem
there surfaces as a failed delivery rather than a startup error.

```bash
# on a node, as the node's identity: can it read the vault the OSDU secrets live in?
SQLFLOW_AZURE_AUTH=mi AZURE_CLIENT_ID=<the node identity> sqlflow auth --scope keyvault

# and can it read the payload storage?
SQLFLOW_AZURE_AUTH=mi AZURE_CLIENT_ID=<the node identity> sqlflow auth --scope storage
```

The deployment templates set `SQLFLOW_AZURE_AUTH=mi` and `AZURE_CLIENT_ID` on every app, so the identity
`sqlflow auth` checks from inside a container is the identity the flows will use.

## What it does not check

`sqlflow auth` proves the Azure credential, not the OSDU one. A flow authenticates to OSDU with its own
`target.auth` block (client credentials, a bearer reference, an API key header), and the OSDU platform's own
entitlements decide what that principal may write. To check that end of it, probe the flow's target from the GUI
or the API, which runs on a node as a compute task, or run `sqlflow check`, which validates everything checkable
without OSDU ([delivery.md](delivery.md)).

It also says nothing about the control plane's own credential: that is `sqlflow login` and `sqlflow whoami`
([control-plane.md](control-plane.md)).

## See also

- [../../environment-variables.md](../../environment-variables.md): `SQLFLOW_AZURE_AUTH` and the `AZURE_*` family.
- [worker.md](worker.md): what a node resolves and when.
