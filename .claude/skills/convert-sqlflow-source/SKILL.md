---
name: convert-sqlflow-source
description: Convert an old SQLFlow source (a "batch") into SQLFlow V3 pre + ods flow YAML files, verified against production and registered in the V3 catalog. Use when the user wants to port/migrate/upgrade a legacy SQLFlow source, batch, or its tables to V3 (e.g. "convert the Baatbooking source", "port batch BB", "upgrade the Citybike flows"). Reads the legacy metadata DB, generates the two-stage flows, tests them locally, compares the resulting schema to prod, and syncs the catalog.
---

# Convert an old SQLFlow source to V3

Ports one legacy SQLFlow **source** (identified by its `Batch` code) into V3 flow files. Each dataset/table
becomes **two** flows: a **pre** flow (CSV -> raw string landing table + typed view) and an **ods** flow
(typed view -> keyed merge into the arc/ODS table). Output is verified column-for-column against the
production schema and registered in the official V3 catalog so progress is visible.

## Inputs

- **Batch code** (required): the legacy `Batch` value, e.g. `BB`. Used to find the flows in the metadata DB.
- **Readable source name** (required): the human folder name, e.g. `Baatbooking`. Folder = `migration/<ReadableName>/`.
- **Scope** (optional): specific tables/FlowIDs. Default = every active flow in the batch.

If the user gives only a readable name, derive the batch code by querying the metadata (see Discovery), or ask.

## Configuration (this environment)

- SQL Server (local targets + catalog): `localhost`, user `SQLFlow` (password in `B:\SQLFlowUpgradeV3\creds.txt` and `.sqlflow/env`).
- Legacy source server: **`92.221.59.28`** (hosts the old SQLFlow control DB the migration reads from; NOT localhost).
- Legacy metadata DB: **`dw-sqlflow-prod-last`** on `92.221.59.28` (the old SQLFlow control DB; read-only, never write to it).
- Targets (local): pre landing -> **`dw-pre-prod`** (schema `pre`); ods/arc -> **`dw-dwh-prod`** (schema `arc`).
- Official V3 catalog: **`dw-sqlflow-prodV3`** (`SQLFLOW_CATALOG_DB` in `.sqlflow/env`).
- Data lake (source files): account `dwdatalakestorev2prod`, container `datalakev2`; storage URL base
  `https://dwdatalakestorev2prod.dfs.core.windows.net/datalakev2`.
- Reference prod DDL (for schema comparison): `B:\SQLFlowUpgradeV3\dw-dwh-prod\` and `...\dw-pre-prod\`.
- Generators: `migration/_tools/Generate-PreFlow.ps1`, `migration/_tools/Generate-OdsFlow.ps1`. Both read the
  legacy metadata over the network and default `-Server` to the IP **`92.221.59.28`** (the metadata DB is NOT
  on localhost). Pass `-Server` only to override.

### Credentials caveat (important)

The machine's global `AZURE_*` environment variables point at a **different** service principal and win over
`.sqlflow/env`. Any command that reads the data lake MUST inline-export the correct SP from `creds.txt`:

```bash
export AZURE_TENANT_ID=<tenant> AZURE_CLIENT_ID=<appid> AZURE_CLIENT_SECRET='<secret>' SQLFLOW_AZURE_AUTH=ServicePrincipal
```

Verify once with `dotnet run --project src/SqlFlow.Cli --no-build -- auth --scope storage` (expect `OK`).

## Procedure

Run from the repo root `b:\SQLFlowV3`. Build the CLI first if needed: `dotnet build src/SqlFlow.Cli -c Debug`.

### 1. Discover the flows in the batch

```sql
-- CSV pre flows:
SELECT FlowID, srcPath, trgDBSchTbl FROM flw.PreIngestionCSV
 WHERE Batch = '<BATCH>' AND (DeactivateFromBatch = 0 OR DeactivateFromBatch IS NULL) ORDER BY FlowID;
-- Ingestion (ods) flows:
SELECT FlowID, srcDBSchTbl, trgDBSchTbl FROM flw.Ingestion WHERE Batch = '<BATCH>' ORDER BY FlowID;
```

(Other pre-ingestion kinds exist too: `flw.PreIngestionADO/JSN/PRQ/XLS/XML`. This skill covers CSV; extend
the same way for others.)

### 2. Ensure target schemas exist

```sql
-- in dw-pre-prod:  IF SCHEMA_ID('pre') IS NULL EXEC('CREATE SCHEMA pre');
-- in dw-dwh-prod:  IF SCHEMA_ID('arc') IS NULL EXEC('CREATE SCHEMA arc');
```

### 3. Generate the flow files (one pre per CSV FlowID, one ods per Ingestion FlowID)

```powershell
foreach ($id in <csv-flowids>) {
  ./migration/_tools/Generate-PreFlow.ps1 -FlowId $id -OutDir migration/<ReadableName> `
     -StorageUrlBase 'https://dwdatalakestorev2prod.dfs.core.windows.net/datalakev2'
}
foreach ($id in <ingestion-flowids>) {
  ./migration/_tools/Generate-OdsFlow.ps1 -FlowId $id -OutDir migration/<ReadableName>
}
```

Produces `migration/<ReadableName>/<Table>.01_pre.flow.yaml` and `<Table>.02_ods.flow.yaml`.

### 4. Validate, then test with a bounded window

```bash
dotnet run --project src/SqlFlow.Cli --no-build -- validate migration/<ReadableName>/<Table>.01_pre.flow.yaml
dotnet run --project src/SqlFlow.Cli --no-build -- validate migration/<ReadableName>/<Table>.02_ods.flow.yaml
```

For a fast test load, temporarily narrow the pre flow's `location` to one partition (e.g. `.../<dataset>/2018/05/`)
and `run` it, then `run` the ods flow. A full historical load reads every partition and is a long,
one-time backfill (run it in the background; downloading partitions to a local temp folder is faster than
per-file network reads).

### 5. Verify the schema matches production

Compare the generated typed view / arc table against the prod DDL in `B:\SQLFlowUpgradeV3\`:
materialize the prod object (renamed) over the same table and `diff` the `sys.columns` name+type lists.
The expected result is an exact match (one known synonym: V3 emits `decimal(14,0)` where prod DDL says the
equivalent `numeric(14,0)`).

### 6. Register in the catalog

```bash
dotnet run --project src/SqlFlow.Cli --no-build -- db sync migration/<ReadableName> --repo <ReadableName> --connect
```

Use **`--connect`** so the derived tier reads each generated view's actual source from `sys.sql_modules`,
parses the SQL, and links the transformation view to its parent table(s) with column-level lineage. The
transformation view is created automatically by the engine and is NOT a YAML concept, so its lineage must come
from the parsed SQL, not the flow definition. (Offline `db sync` without `--connect` will not show the
view->table edge.) Run the flows first so the view exists before the connected sync reads it.

Uses `SQLFLOW_CATALOG_DB` (`dw-sqlflow-prodV3`). Confirm with:

```sql
SELECT Name, Kind, Batch, Wave FROM catalog.Pipeline WHERE Batch = '<BATCH>' ORDER BY Wave, Name;
SELECT [Database],[Schema],Name,Kind FROM catalog.Object ORDER BY 1,2,3;   -- lineage
```

## Naming standard (do not deviate)

- Folder: `migration/<ReadableName>/` (readable, e.g. `Baatbooking`, not the cryptic batch code).
- File: `<Table>.01_pre.flow.yaml` and `<Table>.02_ods.flow.yaml` (numeric prefix keeps pre before ods).
- Flow `name:`: `<Batch>_<Table>_<stage>` (e.g. `BB_Baatbooking_sess_ods`).
- `batch:` field carries the batch code inside every file.
- **Object names (tables/views) are locked to production** and must match exactly, including casing.

## Faithfulness rules (matching prod)

- Use **all** transform columns from `flw.PreIngestionTransfrom` (data columns + the 5 `_DW` provenance
  columns). The current source file may have fewer columns than the metadata due to schema evolution; the
  metadata is authoritative.
- Provenance columns land as `varchar(255)`; the typed view casts them (`FileDate_DW`/`DataSet_DW` ->
  `decimal/numeric(14,0)`, `FileRowDate_DW` -> datetime, `FileSize_DW` -> `decimal(18,0)`).
- Disable provenance columns the metadata does not list (notably `RowNumber_DW`, which legacy never had).
- Surrogate PK: `target.identityColumn` from `flw.Ingestion.IdentityColumn`, verbatim (names are inconsistent
  across the estate and must NOT be generated). Omit when NULL.
- Merge keys from `KeyColumns`; incremental watermark from `IncrementalColumns` + `NoOfOverlapDays`; audit
  columns from `SysColumns` (`InsertedDate_DW`, `UpdatedDate_DW`, created as `datetime`).
- **Incremental watermark must be a source-view column.** The ods source is the typed pre view, whose only
  high-water columns are the file provenance stamps (`FileDate_DW` etc.), not the target-side audit columns.
  Legacy flows that used `UpdatedDate_DW`/`InsertedDate_DW` as `IncrementalColumns` (those were audit columns
  on the old ingestion source table) must be remapped to `FileDate_DW`, the view's per-file high-water column;
  the engine throws `Incremental column '...' is not among the source columns` otherwise. The ods generator
  does this remap automatically; business (non-audit) incremental columns pass through unchanged.

## Definition of done for a source

Every dataset in the batch has a `01_pre` + `02_ods` pair that: validates, loads data end to end
(pre table -> typed `v_` view -> arc table), matches the prod schema column-for-column, and appears in
`dw-sqlflow-prodV3` under its batch.
