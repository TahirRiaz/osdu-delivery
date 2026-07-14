---
id: flow-cpy
title: "Copy flow (flowType: cpy): file copy between storage endpoints, with zip/unzip"
type: flow-reference
summary: "flowType: cpy copies files byte-for-byte between local disk and Azure Blob/ADLS in any direction, optionally zipping or unzipping."
keywords:
  - copy
  - cpy
  - lake to lake
  - zip
  - unzip
  - azure blob
  - adls
related:
  - flow-overview
  - flow-sftp
  - flow-acq
sourceRefs:
  - src/SqlFlow.Core/Copy/CopyFlow.cs
  - src/SqlFlow.Copy/CopyEngine.cs
  - src/SqlFlow.Copy/LocalCopyEndpoint.cs
  - src/SqlFlow.Copy/AzureBlobCopyEndpoint.cs
  - src/SqlFlow.Yaml/YamlCopyFlowLoader.cs
  - samples/copy/baatbooking-storage-to-lake.flow.yaml
---

# Copy flow (flowType: cpy)

A `cpy` flow copies files **byte-for-byte** between two storage endpoints, in any direction: Azure Blob/ADLS Gen2 to/from another account, local disk to/from Azure, or local to local. It performs no parsing; it optionally bundles the matched files into one `.zip` (`operation: zip`, a zip-only run) or extracts archives (`operation: unzip`). It replaces the estate's lake-to-lake / drop-zone copy runbooks (e.g. baatbooking). SFTP is a separate flow type (`sftp`), not a copy endpoint.

## Minimal example

```yaml
flowType: cpy
name: Lake_Relay
source:
  location: abfss://drop@vendoracct.dfs.core.windows.net
  modifiedWithinDays: 14
target:
  location: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/vendor/history
```

```bash
sqlflow validate lake-relay.flow.yaml
sqlflow run      lake-relay.flow.yaml
```

## Keys reference

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `flowType` | string | yes | | Must be `cpy`. |
| `name` | string | yes | | Flow identity; seeds the stable flow id. |
| `batch` | string | no | | Batch label recorded with the run. |
| `operation` | enum | no | `copy` | `copy` (verbatim), `zip` (bundle matched files into one archive at the target - a zip-only run), or `unzip` (extract each matched `.zip` to the target). |
| `source` | map | yes | | Where files are read and how they are selected. |
| `target` | map | yes | | Where files are written. |
| `options` | map | no | | Copy behavior. |

### source / target

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `location` | string | yes | | A local/UNC path or an Azure storage URI (`abfss://fs@account.dfs.core.windows.net/path` or the https form). |
| `pattern` | string | no | `*` | Source only: file-name glob selecting which files copy. |
| `recursive` | bool | no | `true` | Source only: recurse into subfolders. |
| `modifiedWithinDays` | int | no | `0` | Source only: only copy files modified within this many days; 0 = all. |
| `connectionStringRef` | secret ref | no | | Azure endpoint: `${...}` reference to a connection string. Absent = ambient managed identity. |
| `sasTokenRef` | secret ref | no | | Azure endpoint: `${...}` reference to a SAS token. |
| `accountKeyRef` | secret ref | no | | Azure endpoint: `${...}` reference to a storage account key (the shape the legacy runbooks used). Prefer managed identity. |

The target ignores `pattern`/`recursive`/`modifiedWithinDays`.

### options

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `overwrite` | bool | no | `true` | Overwrite an existing target file; when false a collision fails rather than clobbers. |
| `preserveStructure` | bool | no | `true` | Preserve each file's folder structure (relative to the source root) under the target; flat by name otherwise. |
| `zipName` | string | no | `<flow>_<timestamp>.zip` | The archive name for `operation: zip`; ignored otherwise. |

## Authentication

Azure endpoints authenticate with the ambient managed identity / az login (`SQLFLOW_AZURE_AUTH`) by default - the same credential Key Vault, invoke, and the other file engines use, so no per-flow secret is needed. For a drop zone that only issues a key, set `accountKeyRef` (account key), `sasTokenRef` (SAS), or `connectionStringRef` (connection string); each is a whole `${keyvault:...}` / `${env:...}` reference, never the value itself.

## Operations

- **copy**: each matched source file is written to the target, at its relative path (or flat when `preserveStructure: false`).
- **zip**: every matched file is read and bundled into a single `.zip` written to the target under `zipName`. Running a flow purely to produce an archive is a zip-only run.
- **unzip**: each matched source archive (a `.zip`) is extracted; with `preserveStructure` its entries nest under a folder named for the archive so two archives never collide, flat by entry name otherwise.

## See also

- [SFTP flow](sftp.md)
- [Flow types overview](overview.md)
