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
- **Readable source name** (required): the human folder name, e.g. `Baatbooking`. Output folder =
  `C:\Projects\dwh-pipelines-prod\<ReadableName>\` (see Configuration; this is the official pipelines repo).
- **Scope** (optional): specific tables/FlowIDs. Default = every active flow in the batch.

If the user gives only a readable name, derive the batch code by querying the metadata (see Discovery), or ask.

## Configuration (this environment)

- **Official output repo**: **`C:\Projects\dwh-pipelines-prod`** (git remote `origin` ->
  `https://bitbucket.org/kolumbuscode/dwh-pipelines-prod.git`). This is where converted/migrated flows live
  and ship from. Each source is a top-level folder `C:\Projects\dwh-pipelines-prod\<ReadableName>\` holding its
  flow YAMLs plus a generated `.sqlflow\lineage\`. Generate flow files straight into this folder, commit there,
  and push to Bitbucket. **The push IS the catalog registration** - the control plane auto-syncs the whole repo
  from Bitbucket (see step 6); never register a source any other way. The SQLFlowV3 repo's `migration/` folder is
  the tooling/scratch area only, not a shipping location.
- **PATs**: `C:\Projects\pat.txt` holds the GitHub + Bitbucket personal access tokens. The confirmed-working
  Bitbucket push token is also in `.sqlflow/env` as `SQLFLOW_GIT_TOKEN` (username scheme `x-bitbucket-api-token-auth`);
  see also the `bitbucket-prod-repo-push` memory. Push with the token embedded in the URL - the named `origin`
  prompts for credentials and hangs.
- SQL: **everything runs against the real Azure estate**, not localhost. Server
  `tcp:dw-mi-sql-prod.public.6b122fbc620a.database.windows.net,3342`, user `SQLFlow` (connection strings +
  password in `.sqlflow/env`). Targets: pre landing -> **`dw-pre-prod`** (schema `pre`); ods/arc ->
  **`dw-dwh-prod`** (schema `arc`). Catalog: **`dw-sqlflow-prod`** (`SQLFLOW_CATALOG_DB` in `.sqlflow/env`).
- Legacy source server: **`92.221.59.28`** (hosts the old SQLFlow control DB the migration reads from; NOT localhost).
- Legacy metadata DB: **`dw-sqlflow-prod-last`** on `92.221.59.28` (the old SQLFlow control DB; read-only, never
  write to it). The generators read it via `sqlcmd` with the credentials they hardcode (`-U SQLFlow`).
- Data lake (source files): account **`dwdatalakeprodv2`**, container `datalakev2`; storage URL base
  `https://dwdatalakeprodv2.dfs.core.windows.net/datalakev2`. **Always use `dwdatalakeprodv2`.** This is the
  account the copy (`_00_cpy`) flows land into, so the pre (`_01_csv`) read MUST resolve to the same account or
  lineage breaks (the copy targets become dead-ends and the reader an orphan). The old `dwdatalakestorev2prod`
  account is retired: never emit it, and replace it with `dwdatalakeprodv2` anywhere it still appears.
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

Run the tooling from the SQLFlowV3 repo root `c:\Projects\SQLFlowV3` (the generators and CLI live here; they
write flow files into the pipelines repo via `-OutDir`). Build the CLI first if needed:
`dotnet build src/SqlFlow.Cli -c Debug`.

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

Write straight into the official pipelines repo. `$OUT` is that source's folder there:

```powershell
$OUT = 'C:\Projects\dwh-pipelines-prod\<ReadableName>'
foreach ($id in <csv-flowids>) {
  ./migration/_tools/Generate-PreFlow.ps1 -FlowId $id -OutDir $OUT `
     -StorageUrlBase 'https://dwdatalakeprodv2.dfs.core.windows.net/datalakev2'
}
foreach ($id in <ingestion-flowids>) {
  ./migration/_tools/Generate-OdsFlow.ps1 -FlowId $id -OutDir $OUT
}
```

Produces `<ReadableName>\<table>_01_csv.yaml` (pre) and `<table>_02_ing.yaml` (ods), where `<table>` is the
lowercased target table name.

### 4. Validate, then test with a bounded window

```bash
dotnet run --project src/SqlFlow.Cli --no-build -- validate "C:/Projects/dwh-pipelines-prod/<ReadableName>/<table>_01_csv.yaml"
dotnet run --project src/SqlFlow.Cli --no-build -- validate "C:/Projects/dwh-pipelines-prod/<ReadableName>/<table>_02_ing.yaml"
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

### 6. Register in the catalog - ALWAYS via Bitbucket, never a local `db sync`

**The catalog registration is the Bitbucket push, nothing else.** The control plane auto-syncs the WHOLE
`dwh-pipelines-prod` repo from Bitbucket on every push (catalog repo `dwh-pipelines-prod`, folder-prefixed paths
like `<ReadableName>/<table>_01_csv.yaml`) and that is what puts the flows in the catalog and computes lineage.

**Do NOT run `sqlflow db sync "<folder>" --repo <ReadableName>`.** It registers the same flows a SECOND time under
a different repo (the folder name) with root-relative paths that do not exist in the repo, so the GUI shows every
flow twice and re-syncing never dedupes (each sync reconciles only its own repo). If a stray repo was already
created this way, remove it by reconciling it against an empty path:
`sqlflow db sync <empty-dir> --repo <name>` (reports its pipelines as "removed").

Commit the new `<ReadableName>\` folder and push (commit as the human user, never attribute to Claude):

```bash
cd /c/Projects/dwh-pipelines-prod
git add -A <ReadableName>/ && git commit -m "Add <ReadableName> pre + ods flows (ported from legacy FlowIDs ...)"
TOK=$(grep -oE 'SQLFLOW_GIT_TOKEN=.*' /c/Projects/SQLFlowV3/.sqlflow/env | cut -d= -f2-)
GIT_TERMINAL_PROMPT=0 git -c credential.helper= push \
  "https://x-bitbucket-api-token-auth:${TOK}@bitbucket.org/kolumbuscode/dwh-pipelines-prod.git" main
```

The push triggers the control-plane sync. Confirm the flows landed (note the join on the `dwh-pipelines-prod`
repo, `SQLFLOW_CATALOG_DB` = `dw-sqlflow-prod`):

```sql
SELECT p.Name, p.Kind, p.Batch, p.Wave FROM catalog.Pipeline p
  JOIN catalog.Repo r ON r.Id = p.RepoId
 WHERE r.Name = 'dwh-pipelines-prod' AND p.Batch = '<BATCH>' ORDER BY p.Wave, p.Name;
```

The connected view->table column lineage (each generated `v_` view parsed from `sys.sql_modules`) requires the
sync to read the created view from the DB, so **run the flows first** (step 4) so the view exists before the
sync that should pick it up.

## Naming standard (do not deviate)

- Folder: `C:\Projects\dwh-pipelines-prod\<ReadableName>\` (readable, e.g. `Baatbooking`, not the cryptic batch code).
- File: `<table>_01_csv.yaml` (pre) and `<table>_02_ing.yaml` (ods), where `<table>` is the lowercased target
  table name (e.g. `baatbooking_detail_01_csv.yaml`). The numeric prefix keeps pre before ods.
- Flow `name:` matches the file stem: `<table>_01_csv` / `<table>_02_ing` (the generators emit this).
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
(pre table -> typed `v_` view -> arc table), matches the prod schema column-for-column, and - after the
Bitbucket push (step 6) - appears in the catalog (`dw-sqlflow-prod`) under repo `dwh-pipelines-prod` and its batch.
