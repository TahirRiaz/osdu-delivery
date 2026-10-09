---
id: delivery-cli-config
title: "sqlflow config: the central configuration the control plane gives the runs of OSDU flows"
type: cli-command
summary: "List, set and remove the values a flow's ${env:NAME} references resolve to, for the whole control plane, one repository or one partition."
keywords:
  - central configuration
  - config set
  - config effective
  - config list
  - "${env:name}"
  - environment reference
  - per-partition values
  - repository override
  - osdu.configproperty
  - endpoint per partition
  - legal tag and acl groups
  - node environment
cliCommand: config
related:
  - delivery-concept-partitions
  - delivery-cli-partition
  - delivery-concept-environment-variables
  - delivery-concept-control-plane
  - delivery-cli-run
  - concept-connections-and-secrets
  - cli-control-plane
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryConfigVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryConfigStore.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/ConfiguredRunDispatcher.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryConfigEndpoints.cs
  - osdu/src/SqlFlow.Delivery/Http/SuppliedReferenceResolver.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Model/DeliveryDestination.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheScope.cs
  - sqlflow/src/SqlFlow.Cli/Hosting/CliVerbContext.cs
  - sqlflow/src/SqlFlow.Cli/Program.cs
---

# sqlflow config

OSDU Delivery's command line is `sqlflow`: SQLFlow's CLI with the OSDU verbs added. `config` is one of those verbs. It
reads and writes the central configuration: named values the control plane attaches to the delivery, cache, retrieval, assertion and dimension runs it queues, so a
flow that names `${env:OSDU_URL}` or `${env:OSDU_LEGAL_TAG}` gets its value from one place instead of from the
environment of whichever node picks the run up.

## Synopsis

```bash
sqlflow config list      [--repo <id>] [--partition <id>] [--db <conn-ref>] [--json]
sqlflow config effective --repo <id> [--partition <id>] [--db <conn-ref>] [--json]
sqlflow config set <name> --value <value> [--repo <id>] [--partition <id>] [--description <text>] [--db <conn-ref>] [--json]
sqlflow config remove <name> [--repo <id>] [--partition <id>] [--db <conn-ref>]
```

## Description

A property is a name and a value, kept in `[osdu].[ConfigProperty]` in the module's database. The name is the one a flow
spells in `${env:NAME}`. The value is a non-secret value (an endpoint URL, a legal tag, an entitlements group) or a
`${env:...}` or `${keyvault:vault/secret}` reference that the node resolves. The verb talks to the database directly, not
to the control plane, so it needs the catalog connection (`--db`, else `${env:SQLFLOW_CATALOG_DB}`), or `SQLFLOW_OSDU_DB`
when the `osdu` schema has a database of its own (see [sqlflow db](db.md)).

A property is set at one of four scopes:

| Scope | Options | Applies to |
| --- | --- | --- |
| The control plane | neither option | Every repository's runs. |
| One repository | `--repo <id>` | The runs of that repository's flows; wins over the control plane's value of the same name. |
| One partition | `--partition <id>` | Runs bound to that OSDU partition; wins over every value set for no partition. |
| One repository and partition | both | Runs of that repository's flows bound to that partition; wins over everything else. |

For a run bound to partition P, each name takes the first value set at: the repository for P, the control plane for P,
the repository, the control plane. A name none of them sets is resolved from the node's own environment, so an estate can
hold some values centrally and leave the rest on its nodes. A value set for a partition always wins over one set for no
partition, which is what keeps an estate-wide `OSDU_URL` from sending a run of `test` to the `dev` platform. A run of a
flow whose partition is its `data-partition-id` header is bound to no partition and takes the repository's and the
control plane's values only. How runs are bound to partitions is in [Partitions](../concepts/partitions.md).

### Which runs read it

- **Runs the control plane queues.** A run triggered from the GUI or with `sqlflow trigger`, a schedule fire, and every
  member of a run group is queued carrying the values set for no partition and every partition's own values. This
  applies to delivery, cache, retrieval, assertion and dimension flows; runs of SQLFlow's own flow kinds, and of
  inventory flows, are queued without them. The node then resolves with the values of the partition the run binds to.
- **Node tasks of the module.** A value check (`delivery-check-values`) and a removal (`delivery-delete`) carry the
  effective values of the one partition they act in.
- **What the control plane runs while a person waits**: a probe of the target, a record read back from OSDU, a preview,
  a scope's values. These resolve with the same values.
- **Not a workstation.** `sqlflow run`, `check`, `preview` and `values` on a workstation do not read the central
  configuration: they resolve every reference from the workstation's environment and its `.sqlflow/env` file.

A run that already carries values in its payload (`references` or `partitionReferences`) keeps them: a caller that
composed the payload deliberately is not overruled. Nothing is attached when the configuration is empty.

### How a value is resolved

Only `${env:NAME}` references are looked up in the configuration. A `${keyvault:...}` reference written in a flow is
always resolved by the node directly. A property whose value is itself a reference is resolved once, by the node's own
resolver: `OSDU_CLIENT_SECRET = ${keyvault:welldb-vault/osdu-client-secret}` makes the node read the vault, and a value of
`${env:OTHER}` is read from the node's environment even when `OTHER` is also a property (properties do not chain). A
property whose value is its own reference (`OSDU_URL = ${env:OSDU_URL}`) is handed back to the node's environment.

The OSDU flow kind supplies four mapping parameters a flow does not set under `render.parameters`: `dataPartition`,
`aclOwner`, `aclViewer` and `legalTag`, which default to `${env:OSDU_DATA_PARTITION}`, `${env:OSDU_ACL_OWNER}`,
`${env:OSDU_ACL_VIEWER}` and `${env:OSDU_LEGAL_TAG}`. Those names are therefore natural properties. A flow bound to a
partition is given `dataPartition` as that partition, written literally, and never reads `OSDU_DATA_PARTITION`.

### Limits

| Item | Rule |
| --- | --- |
| Name | A letter or underscore, then letters, digits and underscores; at most 64 characters. A run matches it to a flow's `${env:NAME}` exactly, case included. |
| Value | Not empty, at most 1,000 characters, no control characters. |
| Partition | A data-partition-id written literally: letters, digits, underscore, hyphen and dot, at most 200 characters. It need not be registered. |
| Description | At most 400 characters, the width of its column. Neither the command nor the API checks it first: a longer one fails when the row is written, with the database's own error. |
| Per run | At most 64 properties in any one scope, and values for at most 64 partitions. A configuration beyond either, or one too large for a run's payload, is not attached: the run resolves every reference on the node, and the control plane logs a warning naming the flow. |

The command does not inspect a value for secrets. A literal secret in a property is a defect: the row is ordinary
catalog content, and the run payload that carries it is readable beside the run. Point a property at a secret with a
`${keyvault:...}` or `${env:...}` reference instead.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<subcommand>` | yes | One of `list`, `effective`, `set`, `remove` (case-insensitive). |
| `<name>` | `set`, `remove` | The property's name, as a flow spells it in `${env:NAME}`. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--repo <id>` | repository id | none (the control plane's own) | The repository the property is set for, by its id (a GUID), not its name. `sqlflow repos show <name>` prints it. Anything that is not a GUID is refused. The command does not check that a repository with that id exists, so a mistyped id sets a value no run reads. |
| `--partition <id>` | data-partition-id | none (no particular partition) | The partition the property is set for, or read in. |
| `--value <value>` | string | none | `set` only, required: the value or reference. |
| `--description <text>` | string | none | `set` only: what the property is for. Replaced by every `set`, and cleared when left out. |
| `--db <conn-ref>` | connection reference | `${env:SQLFLOW_CATALOG_DB}` | The catalog database, which holds the `osdu` schema unless `SQLFLOW_OSDU_DB` names its own. |
| `--json` | flag | off | Writes one JSON document to stdout instead of text (not for `remove`). |

## Behavior and output

### list

Without `--repo`, `list` prints every property of every scope, ordered by name, and ignores `--partition`. With `--repo`,
it prints the properties set at exactly that scope: the repository's values for no partition, or with `--partition` the
repository's values for that partition. There is no form that lists only the control plane's own values for one
partition; read them from the full listing.

```text
OSDU_CLIENT_SECRET = ${keyvault:welldb-vault/osdu-client-secret}  [(all repositories)]  set by cli at 2026-10-09 07:40:52Z
OSDU_LEGAL_TAG = welldb-test-legal  [0b5e7f3a-2c41-4d8e-9a6b-1f0c2d3e4a5b, partition test]  set by cli at 2026-10-09 07:41:12Z
OSDU_URL = https://osdu.example.com  [(all repositories)]  set by cli at 2026-10-09 07:40:03Z
OSDU_URL = https://osdu-test.example.com  [(all repositories), partition test]  set by cli at 2026-10-09 07:40:31Z
```

With nothing at the scope asked for it prints
`no configuration property is set; every reference a flow names is resolved on the node that runs it`. With `--json` it
prints an array of `{ name, value, repoId, partition, description, updatedUtc, updatedBy }`.

### effective

`effective` answers the question an operator usually asks: what does a run of this repository's flows get? It merges the
control plane's values with the repository's over them, and with `--partition`, that partition's values (the control
plane's, then the repository's) over both. It prints one `NAME = value` line per name, ordered by name; `--json` prints
one object of name to value. It needs `--repo`; without it the command stops with
`name the repository with --repo <id>: what a run is given depends on which estate it belongs to.`

### set

`set` creates the property at the scope given, or replaces the value and description already there, and prints
`<name> set` (with `--json`, the stored property). The command records `cli` as who set it; a property set through the
API records the signed-in user.

### remove

`remove` deletes the property at exactly the scope given (the same `--repo` and `--partition` it was set with) and prints
`<name> removed`, or `<name> was not set` when nothing was there. Both exit 0: removing what is absent leaves the same
state.

### Errors

| Message | Cause |
| --- | --- |
| `'<name>' does not name a configuration property: a property is named as a flow spells it in ${env:NAME}, which is a letter or underscore followed by letters, digits and underscores, at most 64 characters.` | The name breaks the name rule. |
| `the value of '<name>' is empty, longer than 1000 characters, or holds a control character; a property holds an identifier, a URL or a ${env:...} or ${keyvault:...} reference.` | The value breaks the value rule. |
| `'<partition>' is not a data-partition-id a property can be set for: letters, digits, underscore, hyphen and dot, at most 200 characters.` | `--partition` is a reference or holds other characters. |
| `--repo '<text>' is not a repository id.` | `--repo` is not a GUID. |
| `name the property to set.` / `give the value with --value <value>.` / `name the property to remove.` | A required argument is missing; the verb's usage follows. |
| `use 'config list', 'config effective --repo <id>', ...` | No subcommand, or one that is not listed; the verb's usage follows. |

Each is printed as `ERROR  <message>` on stderr. A usage error is followed by the verb's usage lines.

## The same through the API

The control plane serves the same table: `GET /api/v1/delivery/config` (every property, with each repository named),
`PUT /api/v1/delivery/config/{name}` and `DELETE /api/v1/delivery/config/{name}` (both admin, with optional `repoId`
and `partition` query parameters), and `GET /api/v1/delivery/config/effective/{repoId}` (optional `partition`). The
API checks that the repository exists and trims the value; the CLI does neither. The GUI has no page for it. See
[The delivery API](../concepts/api.md).

## Examples

Point the whole control plane at one OSDU platform, and the `test` partition at another:

```bash
sqlflow config set OSDU_URL --value https://osdu.example.com --description "dev platform"
sqlflow config set OSDU_URL --value https://osdu-test.example.com --partition test
sqlflow config set OSDU_TOKEN_URL --value https://login.example.com/oauth2/token
```

Hold the client secret centrally by reference only; each node reads the vault with its own identity:

```bash
sqlflow config set OSDU_CLIENT_SECRET --value '${keyvault:welldb-vault/osdu-client-secret}'
```

Give one repository its own legal tag and access groups in `test`, then check what a run bound to `test` is given:

```bash
REPO=$(sqlflow repos show welldb | jq -r .repo.id)
sqlflow config set OSDU_LEGAL_TAG --value welldb-test-legal --repo "$REPO" --partition test
sqlflow config set OSDU_ACL_OWNER --value data.welldb.owners@test.example.com --repo "$REPO" --partition test
sqlflow config effective --repo "$REPO" --partition test
```

```text
OSDU_ACL_OWNER = data.welldb.owners@test.example.com
OSDU_CLIENT_SECRET = ${keyvault:welldb-vault/osdu-client-secret}
OSDU_LEGAL_TAG = welldb-test-legal
OSDU_TOKEN_URL = https://login.example.com/oauth2/token
OSDU_URL = https://osdu-test.example.com
```

Against an explicit catalog connection reference:

```bash
sqlflow config list --db '${env:SQLFLOW_CATALOG_DB}' --json
```

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| 0 | The subcommand did what it was asked, including `remove` of a property that was not set. |
| 1 | A usage error, a refused name, value, partition or repository id, or a database connection that could not be resolved, printed as `ERROR  <message>`. |
| 130 | Interrupted with Ctrl+C. |

## See also

- [Partitions](../concepts/partitions.md): how a run is bound to the partition whose values it reads first.
- [sqlflow partition](partition.md): the partition registry.
- [Environment variables](../concepts/environment-variables.md): the references an estate's flows name, and where each tier reads them.
- [Running an OSDU flow](run.md): `--set` values and the run payload.
- [Connections and secrets](../../../../sqlflow/docs/reference/concepts/connections-and-secrets.md): how SQLFlow resolves `${env:...}` and `${keyvault:...}`.
- [Control-plane verbs](../../../../sqlflow/docs/reference/cli/control-plane.md): `sqlflow repos show` and the other remote verbs.
