---
id: flow-sftp
title: "SFTP flow (flowType: sftp): download from / upload to an SFTP server"
type: flow-reference
summary: "flowType: sftp downloads files from an SFTP server into the data lake / local, or uploads the other way, byte-for-byte."
keywords:
  - sftp
  - download
  - upload
  - vendor drop
  - private key
related:
  - flow-overview
  - flow-cpy
  - flow-acq
sourceRefs:
  - src/SqlFlow.Core/Sftp/SftpFlow.cs
  - src/SqlFlow.Sftp/SftpEngine.cs
  - src/SqlFlow.Yaml/YamlSftpFlowLoader.cs
  - samples/sftp/citybike-nets-download.flow.yaml
---

# SFTP flow (flowType: sftp)

An `sftp` flow transfers files byte-for-byte between an SFTP server and the data lake (or a local path): `download` (server -> lake/local, the default) or `upload` (lake/local -> server). It performs no parsing; the downstream json/csv/xml file flows ingest what it lands. It is the dedicated interface to SFTP - the file counterpart to the `api` (REST) flow type. The lake side accepts an Azure storage URI (managed-identity authenticated) or a local path.

## Minimal example

```yaml
flowType: sftp
name: Vendor_Settlement
direction: download
server:
  host: sftp.vendor.com
  username: svc
  passwordRef: ${keyvault:dw-keyvault-prod/vendor-sftp-password}
remotePath: /outbound
local: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/vendor
pattern: "*.xml"
modifiedWithinDays: 3
```

```bash
sqlflow validate vendor-settlement.flow.yaml
sqlflow run      vendor-settlement.flow.yaml
```

## Keys reference

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `flowType` | string | yes | | Must be `sftp`. |
| `name` | string | yes | | Flow identity; seeds the stable flow id. |
| `batch` | string | no | | Batch label recorded with the run. |
| `direction` | enum | no | `download` | `download` (server -> local) or `upload` (local -> server). |
| `server` | map | yes | | The SFTP server and credentials. |
| `local` | string | yes | | The non-SFTP side: an Azure storage URI or a local/UNC path. Target on download, source on upload. |
| `remotePath` | string | no | `.` | The directory on the server (the remote root). Listed on download, written to on upload. |
| `pattern` | string | no | `*` | File-name glob selecting which files transfer. |
| `recursive` | bool | no | `true` | Recurse into subdirectories under the root. |
| `modifiedWithinDays` | int | no | `0` | Only transfer files modified within this many days; 0 = all. |
| `overwrite` | bool | no | `true` | Overwrite an existing destination file; when false a collision fails rather than clobbers. |
| `preserveStructure` | bool | no | `true` | Preserve the source's folder structure under the destination; flat by name otherwise. |

### server

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `host` | string | yes | | The SFTP host name or IP. |
| `port` | int | no | `22` | The SFTP port. |
| `username` | string | yes | | The SFTP username (a coordinate, not a secret). |
| `passwordRef` | secret ref | one of | | A `${...}` reference to the password. |
| `privateKeyRef` | secret ref | one of | | A `${...}` reference to the private-key PEM (an alternative to `passwordRef`). |
| `passphraseRef` | secret ref | no | | A `${...}` reference to the passphrase protecting `privateKeyRef`. |

Exactly one of `passwordRef` / `privateKeyRef` is required; a flow with neither fails at run time. Secret material is always a whole `${keyvault:...}` / `${env:...}` reference, never inline.

## See also

- [Copy flow](cpy.md)
- [Flow types overview](overview.md)
