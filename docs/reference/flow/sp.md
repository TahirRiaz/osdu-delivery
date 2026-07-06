---
id: flow-sp
title: "Stored-procedure flow (flowType: sp)"
type: flow-reference
summary: "flowType: sp executes one existing stored procedure (three-part name, no parameters) on a resolved SQL Server, with optional post-run invoke hook."
keywords:
  - stored procedure
  - flowtype sp
  - procedure.object
  - three-part name
  - exec
  - postinvoke
  - onerrorresume
yamlPath: "(root, flowType: sp)"
related:
  - flow-overview
  - flow-inv
  - flow-hooks
  - flow-batch
  - guide-lineage-demo
sourceRefs:
  - src/SqlFlow.Yaml/YamlStoredProcedureFlowLoader.cs
  - src/SqlFlow.Yaml/StoredProcedureYaml.cs
  - src/SqlFlow.Yaml/YamlDocumentParts.cs
  - src/SqlFlow.Yaml/YamlInvokeParts.cs
  - src/SqlFlow.Core/StoredProcedures/StoredProcedureFlow.cs
  - src/SqlFlow.SqlServer/StoredProcedures/StoredProcedureFlowRunner.cs
  - samples/sp/refresh-marts.flow.yaml
  - samples/lineage-demo/30-build-order-fact.flow.yaml
---

# Stored-procedure flow (flowType: sp)

A `flowType: sp` document executes one existing stored procedure on a resolved SQL Server, with no parameters bound (the legacy contract). Use it for orchestration steps that already live in T-SQL: mart rebuilds, statistics refreshes, archive sweeps. The document is self-contained; the `connections:` block it declares becomes an in-memory data-source store, so no control database is required. The procedure runs with `CommandType.StoredProcedure` and no command timeout, and the `EXEC` statement is captured in the run's SQL trace.

## Minimal example

```yaml
flowType: sp
name: refresh-marts

connections:
  dwh: ${env:SQLFLOW_DW}

procedure:
  server: dwh
  object: DW.dbo.usp_RefreshMarts
```

Validate and run it with the CLI (flags per src/SqlFlow.Cli/Program.cs):

```bash
sqlflow validate refresh-marts.flow.yaml
sqlflow run refresh-marts.flow.yaml --log-level trace --show-sql
```

## Keys reference

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `flowType` | string | yes | none | Must be `sp` (case-insensitive) to select this document kind. |
| `name` | string | yes | none | The flow name; becomes the SysAlias and seeds the stable, name-derived flow id. |
| `description` | string | no | none | Free-text description; blank values are treated as absent. |
| `batch` | string | no | none | Batch label carried on the flow and its run record. |
| `connections` | map | no | empty | Named connection registry referenced by `procedure.server`. |
| `procedure` | map | yes | none | The procedure endpoint: which server and which three-part procedure. |
| `postInvoke` | string | no | none | Name of an entry in `invokes:` to run after the procedure succeeds. |
| `invokes` | map | no | empty | Named invoke definitions (ADF pipelines or Automation runbooks). |
| `servicePrincipals` | map | no | empty | Named Azure service principals referenced by entries in `invokes:`. |
| `onErrorResume` | bool | no | `true` | Batch-mode error tolerance flag carried on the flow (see below). |

### `procedure` keys

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `server` | string | exactly one of `server`/`connection` | none | A name declared under `connections:`. |
| `connection` | string | exactly one of `server`/`connection` | none | An inline connection string or `${...}` reference, registered under the synthesized name `target`. |
| `provider` | string | no | `mssql` | Provider of a direct `connection:`. Must resolve to SQL Server (`mssql` or `azdb`). |
| `object` | string | yes | none | The three-part procedure name `Database.Schema.Procedure`. |

## Key details

### `flowType`

`sp` selects the stored-procedure loader (src/SqlFlow.Yaml/YamlDocumentLoader.cs matches it case-insensitively; src/SqlFlow.Yaml/YamlStoredProcedureFlowLoader.cs maps and validates the document).

### `name`

Required. A missing or blank name fails with:

```text
<source>: 'name' is required for a stored-procedure flow.
```

The name becomes `SysAlias` and derives a stable positive `FlowId` (deterministic name-based identity), so logs and run records key consistently across runs without a database to assign ids.

### `connections`

The document's named connection registry, shared with the `ing` and `exp` document kinds. Each entry is one of:

- A plain string: a connection string or a whole `${env:NAME}` / `${keyvault:vault/secret}` reference (SQL Server, the back-compatible form).
- A map with `provider:` and `connection:` keys.
- Nothing (a bare alias): resolves the canonical `${env:SQLFLOW_CONN_<NAME>}` environment variable by convention.

Connection names must use letters, digits, `_`, `.`, or `-`; a duplicate name fails with `connection '<name>' is declared more than once.` Recognized provider tokens are `mssql` (also `sqlserver`), `azdb`, `mysql`, `postgres` (also `postgresql`), and `oracle`; anything else fails with `has unknown provider '<value>'. Allowed: mssql, azdb, mysql, postgres, oracle.`

Secrets never rest in the file: use a `${...}` reference or a passwordless connection string.

### `procedure`

Required. Omitting the block fails with:

```text
<source>: 'procedure' is required.
```

**`server` vs `connection`.** Exactly one must be set. Setting both fails with `'procedure' sets both 'server' and 'connection'; use exactly one.` Setting neither fails with `'procedure' needs a connection. Set 'procedure.connection' to a connection string or a ${...} reference, or 'procedure.server' to a name declared under 'connections:'.` A `server:` that names an undeclared connection fails with `'procedure.server' references '<name>', which is not declared under 'connections:'.` A direct `connection:` is registered under the synthesized name `target`; if `connections:` already declares `target`, the document is rejected with instructions to rename or use `procedure.server: target`.

**`provider`.** The procedure executes with T-SQL, so the resolved connection must be SQL Server. A non-SQL-Server provider is rejected at parse time, not deep in the run:

```text
<source>: the procedure connection '<name>' is '<kind>'; a stored-procedure flow's server must be SQL Server (mssql or azdb).
```

**`object`.** Required. A missing or blank value fails with:

```text
<source>: 'procedure.object' is required (a three-part name like Database.Schema.Procedure).
```

The value is parsed as a strict three-part `[Database].[Schema].[Object]` name; the wrong number of parts fails with:

```text
<source>: 'procedure.object': Object name '<value>' must be a three-part [Database].[Schema].[Object] name; found <n> part(s).
```

### `postInvoke`

Optional. Names an entry in the document's `invokes:` block, run after the procedure succeeds; a failed EXEC jumps straight to the failure path and never reaches the post-invoke step. A reference to an undeclared invoke fails at parse time:

```text
<source>: 'postInvoke' references '<name>', which is not declared under 'invokes:'.
```

At run time, a post-invoke failure the invoke does not tolerate fails the flow. When no invoke runner is wired (the without-database composition with no declared invokes), a set alias is surfaced as a clear error by `NullInvokeRunner` rather than being silently skipped.

### `invokes` and `servicePrincipals`

The same invoke dialect as the `ing` and `exp` documents and the standalone `flowType: inv` document (src/SqlFlow.Yaml/YamlInvokeParts.cs). Each invoke has a `type` (`adf` runs a `pipeline`, `aut` runs a `runbook`), a required `servicePrincipal` reference to a name declared under `servicePrincipals:`, and an optional type-preserving `parameters` map. Cross-field mismatches (an `adf` invoke with a `runbook`, an invoke whose service principal lacks the matching `dataFactoryName` or `automationAccountName`) are rejected at parse time. A `clientSecret` may only be a whole `${...}` reference; the value itself never rests in the document. See [flow-hooks](./hooks.md) and [flow-inv](./inv.md).

### `onErrorResume`

Default `true`. Consumed by full-mode batch execution: in a stored-procedure batch run (flows sharing a `batch:` label, loaded from `flw.StoredProcedure`), a failed flow stops the batch unless its `onErrorResume` is `true` (src/SqlFlow.SqlServer/FullMode/FullModeIngestionHost.cs). The without-database `flowType: batch` orchestrator does not read it: batch continuation there is decided by the batch document's own `onError` and `ignoreErrors` instead (see [flow-batch](./batch.md)).

## Execution behavior

Implemented by src/SqlFlow.SqlServer/StoredProcedures/StoredProcedureFlowRunner.cs:

- The server alias is resolved through the connection registry, then the procedure runs as `CommandType.StoredProcedure` with `CommandTimeout = 0` (no timeout) and no parameters bound.
- The SQL trace records one step, `procedure.exec`, with the text `EXEC` followed by the procedure's bracketed, `]`-escaped three-part name and a semicolon, for example `EXEC [DW].[dbo].[usp_RefreshMarts];`. The trace is captured unconditionally and the result carries it on success and on failure.
- The runner also builds an `IngestionRunRecord` for the run log (`FlowType` `sp`, zero row counts, `Process` `-->{server}.[Database].[Schema].[Object]`); the without-database CLI path wires a no-op run log, so this record is built and then discarded, not written to `run.json` or the shadow catalog. Full mode's control-database host persists it as one `flw.SysLog` row instead.
- Failures (other than cancellation) never throw; the runner returns a failed result carrying the original error message, and a run-log write failure never masks the run error.
- `run.log` detail is controlled by `--log-level info|debug|trace`, the same flag as `ing`/`exp`/`hc` runs; `trace` weaves every statement into the timeline. Run artifacts (`run.json`, `run.log`, `trace.sql`) go to a timestamped run folder under `.sqlflow/runs/<flow>/` next to the pipeline file, and `--show-sql` prints the trace to the console.

CLI output on success is `OK  EXEC <three-part-name> completed in <n>s`; on failure `FAILED  <error>`. The `run` command exits 0 on success and 1 on failure. `sqlflow validate` prints `OK  '<name>' is valid (stored procedure: EXEC <three-part-name> on '<server>').`

## Lineage

In lineage, the declared tier records the flow's procedure requirement. A connected sync (`sqlflow db sync --connect`) adds the derived tier: module bodies are harvested from `sys.sql_modules` and parsed, so the procedure body's reads and writes order the sp flow after its upstream flows in the execution plan. See [guide-lineage-demo](../guides/lineage-demo.md); samples/lineage-demo/30-build-order-fact.flow.yaml is a working example.

## Fuller example

Adapted from samples/sp/refresh-marts.flow.yaml with a post-run ADF hook:

```yaml
flowType: sp
name: refresh-marts
description: Rebuilds the reporting marts after the nightly loads.
batch: nightly

connections:
  dwh: ${env:SQLFLOW_DW}

procedure:
  server: dwh
  object: DW.dbo.usp_RefreshMarts

servicePrincipals:
  analytics:
    tenantId: 00000000-0000-0000-0000-000000000000
    clientId: 11111111-1111-1111-1111-111111111111
    clientSecret: ${env:SQLFLOW_SP_SECRET}
    subscriptionId: 22222222-2222-2222-2222-222222222222
    resourceGroup: rg-analytics
    dataFactoryName: adf-analytics

invokes:
  notify-marts:
    type: adf
    pipeline: pl_notify_marts_refreshed
    servicePrincipal: analytics
    parameters:
      mart: orders

postInvoke: notify-marts

onErrorResume: true
```

```bash
sqlflow run refresh-marts.flow.yaml --log-level debug
```

## See also

- [Flow documents overview](./overview.md)
- [Invoke flow (flowType: inv)](./inv.md)
- [Invoke hooks (preInvoke / postInvoke)](./hooks.md)
- [Lineage demo walkthrough](../guides/lineage-demo.md)
