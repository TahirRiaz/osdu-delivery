---
id: concept-data-operations
title: "Data operations: duplicate-key check and old-versus-new baseline comparison"
type: concept
summary: The read-only duplicate-key check and the old-versus-new baseline comparison, behind the ControlPlane DataOps switch.
keywords:
  - dataops
  - duplicate keys
  - baseline comparison
  - linked server
  - migration reconciliation
  - compute task
related:
  - concept-control-plane
  - concept-shadow-catalog
  - concept-upsert-and-change-detection
  - concept-provenance-and-row-keys
sourceRefs:
  - src/SqlFlow.Core/Quality/DuplicateKeyModels.cs
  - src/SqlFlow.Core/Comparison/BaselineComparisonModels.cs
  - src/SqlFlow.Core/Comparison/SqlFragmentGuard.cs
  - src/SqlFlow.SqlServer/Quality/SqlServerDuplicateKeyProbe.cs
  - src/SqlFlow.SqlServer/Comparison/SqlServerBaselineComparer.cs
  - src/SqlFlow.Execution/ComputeTaskExecutor.cs
  - src/SqlFlow.ControlPlane/Api/DatasourceEndpoints.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
---

# Data operations

Two live, interactive checks that run against the warehouse from the control plane: the **duplicate-key
check**, and the **baseline comparison** that proves a V3 migration against the old production estate.

Both are **read-only**. The duplicate check groups and counts. The comparison reads both estates and writes
nothing but a session temp table in `tempdb`. That property is enforced by construction and asserted in tests,
which is what makes the surface safe to hand to an assistant.

Both are **off by default**, behind one switch.

> **Not a DBA surface.** This deliberately does not offer index rebuilds, statistics updates, compression, or
> any other warehouse maintenance remediation. The four warehouse-health DMV probes (`missingIndexes`,
> `statisticsHealth`, `indexUsage`, `topQueries`) are a separate, older feature that backs the insights
> recommendations; they are NOT part of this surface and are not gated by its switch.

## The switch

```
ControlPlane__DataOps__Enabled=true
```

With it off, `POST /api/v1/datasources/tasks` refuses the `duplicateKeys` and `compareBaseline` operations
with a 403 naming the setting, and `GET /api/v1/dataops/capabilities` reports `enabled: false` so a GUI or an
assistant explains the situation instead of showing a failing button. Nothing else in the product changes,
and in particular the insights dashboard is unaffected.

The comparison additionally needs its linked servers allowlisted. A linked-server name becomes an identifier
in generated SQL and a route into another estate, so it is configuration, never something a request chooses:

```
ControlPlane__DataOps__Comparison__LinkedServers__0=old-dwh-prod
ControlPlane__DataOps__Comparison__LinkedServers__1=old-pre-prod
ControlPlane__DataOps__Comparison__LinkedServers__2=old-sqlflow-prod
ControlPlane__DataOps__Comparison__LinkedServers__3=OLDPROD
ControlPlane__DataOps__Comparison__DefaultLinkedServer=old-dwh-prod
```

`Databases` narrows it further to named databases on those servers; left empty, any database the linked
server's own login can reach is permitted, which is the usual case because the linked server is the boundary.

## How it executes

Both ride the existing ad-hoc compute queue, so nothing new was invented for transport:

1. `POST /api/v1/datasources/tasks` (the `operate` scope) validates the request at the trust boundary and
   writes a `CatalogComputeTask` row. Only a connection **reference** travels; a secret never does.
2. Whichever worker node can reach the source claims the row (`RunWorker.DrainComputeAsync` drains compute
   ahead of flow runs, because compute is interactive) and executes it through `ComputeTaskExecutor`.
3. The result lands on the same row. `GET /api/v1/datasources/tasks/{id}?waitMs=20000` long-polls it.

The control plane never opens a connection to a datasource. Neither check carries a wall-clock deadline: an
anti-join over a billion rows legitimately outruns any deadline safe for an interactive browse, so they stay
cancellable and are backstopped by the queue's running-task expiry, exactly like `detectUniqueKey`.

Every task row records `RequestedBy` and the full arguments, so this surface is auditable by construction.

## Duplicate keys, and the key it uses

The `duplicateKeys` operation answers "does this table hold more than one row per key". The key it
groups by is the one the **table itself declares**, in this order:

1. **SQLFlow's own `NCI_KeyColumn`** business-key index, which `CanonicalIndexPlanner` creates on every target.
   This is the key the load MERGES on, so a duplicate against it is a real defect. Matched by prefix, because
   legacy SQLFlow suffixed the name with a table hash and a table ported from old production still carries
   that form. It is chosen **whether or not it currently enforces uniqueness**: a non-unique or disabled
   variant is precisely the case where duplicates can have accumulated.
2. A **primary key that is not a bare identity**.
3. Any other **unique index or constraint**, narrowest first.

A surrogate identity key is **never** used. SQLFlow appends one to every target it creates, so a naive "group
by the primary key" check would group by a column that is unique by construction, report zero duplicates on
every table in the estate, and prove nothing.

Two details decide how a result must be read, and the report states both:

- **A filtered key index** (SCD2's `NCI_KeyColumn`, `WHERE [flag] = 1`) is unique only among current rows. The
  check applies the same predicate, so historical versions are not counted as duplicates.
- **An enabled, unfiltered UNIQUE index** makes duplicates impossible. A zero is then *guaranteed, not
  measured*, and the report says so rather than dressing up a tautology as a finding.

### When the table declares no usable key

The action **asks**. It does not guess, because a duplicate check run against the wrong key answers
confidently and wrongly, which is worse than not answering. The task succeeds carrying a `question`:

```jsonc
{
  "question": {
    "prompt": "Which columns identify one real row of arc.Ferde_Passeringer? ...",
    "parameter": "columns",
    "options": ["Dato", "Sted", "Klokkeslett", "..."]
  }
}
```

A client presents that to a person, collects an answer, and re-runs with `columns`. A client must check
`question` before reading `findings` as a verdict.

## Baseline comparison

Compares the current V3 estate against the OLD production baseline through an allowlisted linked server. The
aggregation and the anti-join run **server-side** via `OPENQUERY`, so only the answer travels and a
billion-row table can be compared at all.

Three modes, meant to be climbed in order:

- **`inventory`**: every table in a schema on either side, with row counts from partition metadata (exact for
  a settled table, and free). Says which tables disagree at all.
- **`schema`**: one object's columns **position by position** - name, type with length and precision,
  nullability, identity. Walking positions rather than matching names is deliberate: column ORDER is part of
  the contract for any consumer doing `SELECT *`. The verdict distinguishes the three cases that matter -
  identical (a direct transfer is legal), same column set in a different shape (the case a **compatibility
  view** under the old name is for), and a different column set (a mapping problem no view alone solves).
- **`data`**: one object's rows. A count decomposition, a **bidirectional** key anti-join, and value parity
  across the shared keys.

### The logical key

Data mode requires `keyExpressions`: the expressions that identify one real-world reading **on both estates**.
Not the surrogate primary key, which each estate assigns independently and which therefore proves nothing.
Establish it with a person before running; a wrong key invalidates every number below it.

The comparison is built to refuse the mistakes that make a reconciliation lie:

- **Physical rows are not the comparison.** A table with duplicates on one side can hold exactly the same
  readings as the other and still report a different `COUNT(*)`. The report decomposes physical rows into
  distinct logical keys plus duplicates, and says which number matters.
- **Both anti-join directions, always.** A table that is "sometimes more, sometimes less" is the normal case
  after a migration, and a one-directional check reads it as clean.
- **Each side is collapsed to one row per key** before value parity. Without that the join is many-to-many
  across duplicates and inflates every mismatch below it.
- **Value parity uses `EXCEPT`**, which is NULL-safe, so a column legitimately NULL on both sides is not a
  mismatch on every row.
- **Provenance and audit columns are excluded by default** (`%\_DW`): they carry the load instant, which
  legitimately differs between estates.
- A column whose mismatches are **all** "current is NULL where baseline was not" is called out as the
  empty-string-versus-NULL landing difference, not lost data. It is the most common false alarm in this
  migration.

### The fragment guard

`keyExpressions` and `where` cannot be parameters: they are projected and grouped, not compared to a value, so
they are interpolated into generated SQL. `SqlFragmentGuard` is what makes that safe, and it is an
**allowlist**: a fragment is accepted only when every token is an identifier, a bracketed identifier, a
literal, an operator, or a word on the keyword/function allowlist. That refuses statement terminators, comment
introducers, variables, every DML and DDL verb, and any function call outside a small scalar set - so a
fragment cannot stop being an expression and become a statement. Every identifier is passed bare and quoted by
the builder, so a name carrying its own bracket is refused rather than escaped.

## API

| Route | Purpose |
| --- | --- |
| `GET /api/v1/dataops/capabilities` | Whether the surface is enabled, the operations available, the allowlisted linked servers |
| `POST /api/v1/datasources/tasks` | `operation: "duplicateKeys"` or `"compareBaseline"` |
| `GET /api/v1/datasources/tasks/{id}?waitMs=20000` | Long-poll the result |

## MCP tools

- `dataops_capabilities` - call first; reports whether the surface is enabled here
- `check_duplicate_keys` - the duplicate check, including the ask-back path
- `compare_baseline` - inventory, schema, or data comparison

## Composing SQL against these tables

The join graph is not part of this surface, because it already exists. `describe_object`
(`GET /api/v1/lineage/objects/dossier`) returns a table's columns, its interpreted key, and its relationships
to other tables, where `origin` distinguishes an explicit FOREIGN KEY from a join inferred from the
codebase's own equality predicates, and `occurrences` counts the distinct scripts exhibiting it so the
canonical join path ranks highest. That is the metadata to compose a query from, rather than guessing joins.
