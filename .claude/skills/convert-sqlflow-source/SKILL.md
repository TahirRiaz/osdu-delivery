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
  `C:\Projects\V3Upgrade\dwh-pipelines-prod\<ReadableName>\` (see Configuration; this is the official pipelines repo).
- **Scope** (optional): specific tables/FlowIDs. Default = every active flow in the batch.

If the user gives only a readable name, derive the batch code by querying the metadata (see Discovery), or ask.

## Configuration (this environment)

- **Official output repo**: **`C:\Projects\V3Upgrade\dwh-pipelines-prod`** (git remote `origin` ->
  `https://bitbucket.org/kolumbuscode/dwh-pipelines-prod.git`). This is where converted/migrated flows live
  and ship from. Each source is a top-level folder `C:\Projects\V3Upgrade\dwh-pipelines-prod\<ReadableName>\` holding its
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

### 1a. Capture the FULL definition of every old table behind the flow (MANDATORY)

**The transform metadata is NOT the whole truth. `flw.PreIngestionTransfrom` (the "main" column list the
generator reads) is frequently STALE: columns were added to the real production table over the years without
anyone back-filling the transform rows.** If you generate the view from the transform metadata alone, the view
silently comes out SHORT and the backward-compat contract is broken (consumers query columns that no longer
exist). This is a real case in this estate: `Billettkontroll_from_excel_report` has 48 data columns in prod arc
but `flw.PreIngestionTransfrom` for its FlowID lists only 37; the 11 later additions (`Avdeling`, `Kunde`,
`Navn_på_vakt`, `Skjema`, `Ukedag`, `Kontrollmetode`, `Type_kontroll`, `Signatur__din_Vekter_ID_`,
`Signatur_makker`, `Vaktdato`, `Dato`) exist only in the table, not the metadata.

So for EACH flow, before generating, gather the full column set from ALL relevant places and reconcile them, and
treat the widest authoritative definition as the target (the arc table is the consumer-facing contract):

1. **The prod arc/ODS target table** - the definitive contract. Read the full DDL in
   `B:\SQLFlowUpgradeV3\dw-dwh-prod\` (or `C:\Projects\V3Upgrade\dw-dwh-prod\`), file
   `arc.<Table>.Table.sql`. This is the column list, order, and types the DWH consumers depend on. (These
   `.sql` are UTF-16/BOM Scripting-generated files; read them as text, not as clean UTF-8.)
2. **The prod pre landing table** - `dw-pre-prod`.`pre.<Table>` (or its DDL under `...\dw-pre-prod\`): the raw
   string columns actually landed, i.e. the real header names the source file carries.
3. **The legacy typed view** - if the old DWH is reachable, read `v_<Table>`'s definition from
   `sys.sql_modules` for the exact cast expressions the old system used per column.
4. **The transform metadata** - `flw.PreIngestionTransfrom` for the FlowID (colName, datatype, expr). This is
   the generator's input but only a LOWER BOUND on the columns; diff it against #1.

Reconcile: the set of columns and their types MUST equal the prod arc table (#1). Any column in #1 that is
missing from #4 was added later and MUST be added to the generated view by hand, with the type from #1 and a
cast expression matching the estate's convention for that type (see the Faithfulness rules). Any type that
disagrees between #4 and #1 is resolved in favour of #1 (the shipped table). Only when all four agree, or you
have explicitly reconciled the differences, is the column mapping correct.

**The arc table can also be a SUPERSET of the CURRENT source (columns dropped/renamed over time), so the view
must be reconciled against the ACTUAL landed columns too, not only the arc DDL.** The pre landing is
string-first: it creates one column per header in the file being loaded. If the typed view references a column
that is in the arc DDL but NOT in the current file, view creation fails at run time with
`Invalid column name '<col>'` (this happened for `Billettkontroll_from_excel_report`: arc has 48 columns, the
current `pss_billettkontrol.csv` has only 31). Get the real header list from the file itself (now that the copy
has landed it, `az storage fs file download` the file and read line 1) or from the pre landing table the first
run creates. Then:

- For an arc column the current file DOES provide: cast the landed column normally.
- For an arc column the current file does NOT provide: keep it in the view as a typed NULL
  (`CAST(NULL as <arc-type>) AS [<col>]`, expr with no `@ColName`) so the arc/ODS contract shape is preserved
  and the merge stays fully defined. Never point the view at a landing column the file lacks.
- The ods surrogate/merge key may be a provenance column whose legacy name has no V3 equivalent: legacy
  `FileLineNumber_DW` is V3's physical `FileLineNumber` (option `includeFileLineNumber: "true"`) or the data-row
  index `RowNumber_DW` (`includeRowNumber`, default on). Land the right one and alias it in the view
  (`CAST([FileLineNumber] as int) AS [FileLineNumber_DW]`); do not expect a landed `FileLineNumber_DW`.

**Long free-text columns (e.g. `remarks`) need a wider landing and a bigger parser buffer.** The landing default
is `varchar(255)` and the CSV parser buffer defaults to 1024 bytes, so a free-text column that prod holds as
`varchar(4000)` both truncates and, on a long value, fails the load with `MaxBufferSize exceeded`. Set the pre
flow's `source.options`: `defaultColDataType: "varchar(4000)"` (lands every raw column wide enough; the typed
view still narrows each to its prod type) and `maxBufferSize: "65536"` (past the longest row). Confirm the row
is genuinely long free-text, not an unbalanced quote (count the `"` on the failing line - an even count is
balanced), before assuming a bigger buffer is the fix.

### 1b. Crosscheck the acquisition script against the file names the pre flows read (MANDATORY)

When the source has an upstream acquisition script (Automation runbook, Azure Function, ADF pipeline) being
ported alongside the pre/ods flows, verify the match BEFORE porting anything: the file names/paths the script
WRITES must match the `srcFile`/`srcPath` patterns the batch's pre flows READ (from `flw.PreIngestionCSV` etc.).
**If they do not match, you have the wrong script; stop and find the right producer.** A name overlap is not
proof (example from this estate: `mobilapp.ps1` writes `export_*` files for the MobilApp feeds, while
Billettapp's pre reads `billettapp*.csv` produced by the `Billettapp` function in `dw-function-prod`; porting
mobilapp.ps1 as billettapp's acquisition shipped a copy flow that matched 0 files). Producers live in more
places than the runbook folder: check Azure Automation runbooks, function apps (`dw-function-prod`,
`dw-function-py-prod`), and Data Factory pipelines, and confirm the producer's schedule lines up with the
files' actual landing timestamps in the lake.

**How to actually find the producer (do this, do not guess by name):**

1. Get the exact file identity from the metadata first. `flw.PreIngestionCSV.srcPath` is the lake path the pre
   flow READS (e.g. `raw/billettkontroll/pss/history/`) and `srcFile` is the filename pattern (e.g.
   `ticket_control(.*?).csv`). These two strings, not the batch code, are what you match against.
2. The Azure Automation runbooks are checked out locally at **`C:\Projects\V3Upgrade\automation-runbooks`**
   (`*.ps1` and `*.py`). Search their CONTENT for the distinctive tokens of that path and filename, not the
   filenames of the scripts. Grep for the lake path stem and the file stem across every runbook:
   ```
   grep -rniE "raw/<source>|<file-stem>" C:\Projects\V3Upgrade\automation-runbooks
   ```
   e.g. for Billettkontroll: `raw/billettkontroll`, `ticket_control`, `pss_billettkontrol`.
3. The definitive link is a runbook line that **writes that exact lake path** - typically
   `New-AzDataLakeGen2Item ... -Path "raw/<source>/.../$key"` (or the Python lake-upload equivalent). The
   runbook whose `-Path` target equals the pre flow's `srcPath` is the producer. A hit on the script's own
   filename, or on an unrelated column name (e.g. `ticketId`), is NOT a match - read the surrounding line.
4. If NO runbook writes that path, widen to the exact tokens across the whole tree
   (`grep -rniE "<file-stem>|raw/<source>" C:\Projects`). If that is still empty, there is genuinely no
   PowerShell producer to port: the files arrive by another mechanism (a function app in `dw-function-prod` /
   `dw-function-py-prod`, an ADF pipeline, an SFTP drop, or a manual upload). A `from_excel_report/` path or a
   `pss_*` name is the tell of a manual/system export with no script. Report that finding and port only the
   pre+ods flows; do not force-fit an unrelated runbook as the acquisition.
5. **Check Azure Data Factory - but know what it actually holds.** There is ONE factory,
   **`dw-datafactory-prod`** in resource group **`datawarehouse-west-rg-prod`**, and the machine's `az login`
   (the `SQLFLOW_AZURE_AUTH=cli` identity) already has read access to it - no extra credential needed:
   ```bash
   az datafactory list --query "[].{name:name,rg:resourceGroup}" -o tsv
   az datafactory pipeline list --factory-name dw-datafactory-prod --resource-group datawarehouse-west-rg-prod --query "[].name" -o tsv
   az datafactory pipeline show  --factory-name dw-datafactory-prod --resource-group datawarehouse-west-rg-prod --name SQLFlow_<Batch> -o json
   az datafactory trigger  list  --factory-name dw-datafactory-prod --resource-group datawarehouse-west-rg-prod -o json
   ```
   **The `SQLFlow_<Batch>` pipelines (and the schedule triggers that fire them, e.g. `SQLFlow_DivFiles`) are NOT
   file producers and NOT Copy activities. They are the OLD SCHEDULER.** Each is an `AzureFunctionActivity` that
   authenticates to the SQLFlow API (Key Vault `SQLFlow-adf-username`/`-password` -> `POST /api/Login`) and then
   GETs the SQLFlow function `ExecFlowBatch?Batch=<Batch>&execmode=adf`, which just tells the old SQLFlow engine
   to run the batch's pre/ods that read files ALREADY in the lake. So finding `SQLFlow_<Batch>` in ADF answers
   "how was the batch scheduled", not "how do the files arrive": it maps to a V3 SCHEDULE (see the
   `schedule-scope-lineage` memory), never to an acquisition/copy flow. A real ADF-based producer would instead
   be a `Copy` activity with a sink dataset pointing at the lake path - search datasets/activities for that; its
   absence (as for Billettkontroll) means ADF is orchestration-only here. Do not port the `ExecFlowBatch`
   orchestration as a flow.

Worked example (Billettkontroll, batch `Billettkontroll`): the pre flows read `raw/billettkontroll/pss/history/
ticket_control*.csv` and `raw/billettkontroll/pss/from_excel_report/pss_billettkontrol.csv`. A content grep of
`automation-runbooks` returned only `mobilapp.ps1` (a `ticketId` column hit) and a substring noise hit - neither
writes `raw/billettkontroll`. A whole-`C:\Projects` grep for `ticket_control` / `pss_billettkontrol` /
`raw/billettkontroll` returned nothing. ADF has a `SQLFlow_Billettkontroll` pipeline, but it is only the
`ExecFlowBatch?Batch=Billettkontroll` scheduler call (fired by trigger `SQLFlow_DivFiles`), not a producer, and
no Copy activity/dataset targets `raw/billettkontroll`. Conclusion: no automated producer exists;
`pss_billettkontrol.csv` is a manual Excel export and `ticket_control` lands from the PSS system outside every
script/factory in the estate.

**Resolution when no producer is found (DO THIS - do not leave the pre flow an orphan):** if the exhaustive
search (runbooks, function apps, ADF Copy activities) turns up no producer, the source files still physically
exist in the OLD lake (that is where the legacy pre flow read them from). The correct migration action is to
**copy the historical files from the OLD lake storage account into the NEW lake account (`dwdatalakeprodv2`) at
the SAME path the pre flow reads.** Build this as a `<table>_00_cpy.yaml` copy flow (model it on the sibling
`billettapp_00_cpy.yaml`): source = the old lake path, target = `dwdatalakeprodv2/raw/<source>/...` (identical
to the pre flow's `srcPath`, so the copy target and the pre reader line up and lineage connects). This is not
"changing a flow's endpoint" - it is establishing the missing acquisition, and it is explicitly sanctioned for
the no-producer case. **Do NOT give such a batch a live recurring schedule.** These sources have no automated
feed (they were manual/PSS exports), so the copy is a one-time/on-demand backfill, not a recurring job; an
active schedule would imply a producer that does not exist.

**Deactivate the copy so it is manual-execution only.** A copy flow has NO `mode:` field - the header
projection hardcodes `ExecutionMode.Auto`, and the copy DTO uses `IgnoreUnmatchedProperties`, so writing
`mode: manual` on a `cpy` flow is silently ignored (a no-op, do not do it). The estate's real deactivate /
manual-only idiom is a **disabled schedule** on the copy:

```yaml
schedule:
  name: <source>_manual
  enabled: false        # validates without a cron; nothing ever fires it, but it can still be triggered by hand
```

This is the same `enabled: false` pause mechanism `billettapp_00_cpy.yaml` documents. Leave the pre/ods flows
unscheduled and NOT joined to this schedule. Net result: nothing auto-runs, and an operator runs the copy once
by hand (after the deployment MI has read on the old lake).

For Billettkontroll specifically: no producer, so add `_00_cpy` copy flows that move the existing
`ticket_control*.csv` and `pss_billettkontrol.csv` history from the old lake to
`dwdatalakeprodv2/raw/billettkontroll/pss/...`, plus the pre+ods flows; the ADF `SQLFlow_Billettkontroll`
pipeline is only the old scheduler and is NOT reproduced (this batch stays unscheduled).

### 1c. Static / manually-maintained tables (`man` schema) are transferred by hand, NOT ported as flows

**Some upstream tables a batch depends on are static reference data, not pipeline outputs.** The clearest tell
is the **`man` schema** (as in `man.Bysykkel_session_metadata`): `man` = manually maintained. These tables have
no acquisition script, no runbook, no ADF Copy, and no pre/ods flow that produces them - they are hand-curated
lookup/metadata tables that a human populated once and edits occasionally. A star-schema build will reference
them (e.g. `Fact_BikesSession` joins `man.Bysykkel_session_metadata`, a `Dim_SessionMetadata` reads it), so the
fact/dim cannot build until the table physically exists in the new estate.

**Do NOT invent a flow for these.** There is nothing to acquire and nothing to transform, so a `cpy`/`api`/`pre`
flow would be a fabricated producer for data that arrives by hand. The correct migration action is a **one-time
manual table transfer from the OLD ODS/DWH database to the NEW one**, table-and-data as-is:

- Copy both schema and rows verbatim from the old database (`dw-sqlflow-prod-last`/the legacy DWH on
  `92.221.59.28`, or wherever the `man.*` table lives) into the same `man.<Table>` in the new estate
  (`dw-dwh-prod`). Preserve the exact column names, types, and contents - this is reference data the downstream
  star schema was built against, so it must match old production exactly (same fundamental principle as the arc
  compat views).
- This is an operator step (a SQL `INSERT ... SELECT` across a linked server, a `bcp`/`sqlcmd` export+import, or
  an SSMS "Generate Scripts (schema + data)"), run once. It is not scheduled and not represented in YAML.
- Record it as a migration prerequisite for the affected fact/dim (note which `man.*` tables were transferred),
  then build the fact/dim flow once the table is present. Never leave the fact/dim pointed at a `man.*` table
  that does not yet exist in the new estate.

Same treatment applies to any other hand-curated lookup table a batch's EDW layer needs (unknown-member rows,
category/mapping tables) that has no producer: transfer it once from old DWH to new DWH, do not synthesize a flow.

### 2. Ensure target schemas exist

```sql
-- in dw-pre-prod:  IF SCHEMA_ID('pre') IS NULL EXEC('CREATE SCHEMA pre');
-- in dw-dwh-prod:  IF SCHEMA_ID('arc') IS NULL EXEC('CREATE SCHEMA arc');
```

### 3. Generate the flow files (one pre per CSV FlowID, one ods per Ingestion FlowID)

Write straight into the official pipelines repo. `$OUT` is that source's folder there:

```powershell
$OUT = 'C:\Projects\V3Upgrade\dwh-pipelines-prod\<ReadableName>'
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
dotnet run --project src/SqlFlow.Cli --no-build -- validate "C:/Projects/V3Upgrade/dwh-pipelines-prod/<ReadableName>/<table>_01_csv.yaml"
dotnet run --project src/SqlFlow.Cli --no-build -- validate "C:/Projects/V3Upgrade/dwh-pipelines-prod/<ReadableName>/<table>_02_ing.yaml"
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

**First check the COUNT, then the names.** Because `flw.PreIngestionTransfrom` can be stale (see step 1a), the
generated view can validate and load yet still be missing columns. So the diff here is not optional polish - it
is how the stale-metadata gap is caught. If the generated view's column count is lower than the prod arc
table's, the generator under-produced from stale metadata: go back, add the missing columns from the arc DDL
(step 1a), regenerate/patch the pre `01_csv.yaml`, and re-diff until the name+type sets are equal.

### 5a. MATCH THE OLD PRODUCTION FORMAT EXACTLY (compat view under the old table name)

**FUNDAMENTAL PRINCIPLE: PORTING A SOURCE MUST NOT CHANGE WHAT DOWNSTREAM CONSUMERS SEE. THE CONSUMER-FACING
`arc.<OldTable>` MUST MATCH OLD PRODUCTION EXACTLY: SAME NAME, SAME COLUMN SET, SAME COLUMN ORDER, SAME NAMES, SAME
TYPES (INCLUDING LENGTHS AND `decimal` vs `numeric`). DOWNSTREAM USAGE MUST REMAIN INTACT.**

The V3 ODS/arc table is built from the typed view, so its physical column ORDER (and occasionally a type/length)
will NOT match the hand-built old production table even when the column SET is identical: the merge appends the
surrogate PK, the audit columns, and any declared-but-not-landed legacy-extra columns, so their positions shift.
A consumer doing `SELECT *` or positional access breaks. So the count+names check above is NOT sufficient: diff
the FULL ORDERED (name, type) list against `B:\SQLFlowUpgradeV3\dw-dwh-prod\arc.<OldTable>.Table.sql`.

When the V3 arc table is not byte-for-byte identical to old production (it usually is not):

- Point the ods flow (`target.object`) at a physical table named for the V3 SOURCE, matching the flow's source
  prefix: e.g. the Citybike source lands physical tables `arc.Citybike_<Object>` (NOT the old `arc.Bysykkel_<Object>`,
  and NOT an `_ods` suffix).
- Create a VIEW under the OLD production table name (e.g. `arc.Bysykkel_<Object>`) over that physical table,
  projecting the EXACT old-production format: `CAST` every column to its old-prod type, listed in old-prod ORDER.
  Downstream keeps querying the old name and sees the unchanged shape.
- Only skip the compat view when the V3 table already matches old production exactly.
- The view is a one-time/manual DDL op (no engine change): generate the `CREATE OR ALTER VIEW` from the prod DDL,
  create it directly, and keep the statements in a `compat_views.sql` in the source folder for reproducibility.

Never reshape or rename what downstream depends on; interpose a compatibility view under the old name instead.

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
cd /c/Projects/V3Upgrade/dwh-pipelines-prod
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

## Raw format in the lake (do not convert to CSV); backward-compat via a view

**Keep the upstream's ORIGINAL/native format in the lake. Never re-encode it.** JSON stays JSON, XML stays XML,
Parquet stays Parquet. Do NOT flatten a JSON/XML/API payload into CSV during acquisition. The legacy scripts'
CSV flattening historically lost detail the DWH could benefit from (dropped fields, truncated precision,
culture-mangled values), which is exactly why V3 acquisitions land raw.

**Backward compatibility is provided by a view, not by reshaping the data.** The old table's column names and
types are a CONTRACT that downstream consumers depend on, so they must be reproduced EXACTLY (names,
casing, types, column order where it matters). That reproduction lives in the pre flow's typed view
(`generateView`), which projects the old legacy column names over the raw landing; the ods flow then merges that
view into `arc.<Table>` so the arc/ODS shape consumers query is byte-for-byte the production shape. The raw
landing table itself may look nothing like the old table - that is fine and expected; the view is what matches.

**This holds in both directions of the format question:**

- If the upstream is natively JSON/XML/etc.: land it raw and write the typed view to surface the legacy CSV-era
  column names. When the raw format's value tokens differ from the legacy CSV's (JSON invariant decimals,
  ISO-8601 dates with offsets), adapt the typed-view expressions to parse them correctly and TEST the
  expressions against the real server; legacy culture-specific `TRY_PARSE ... USING 'nn-NO'` exprs typically
  return NULL or garbage on JSON tokens while the output column types must stay identical to prod.
- If the old solution genuinely used CSV and the files still arrive as CSV (e.g. Billettkontroll's
  `ticket_control*.csv`): keep them as CSV, do not re-encode. The pre flow lands the CSV and the generated typed
  view (`v_<Table>`) already carries the exact legacy column names/types; verify that view column-for-column
  against the prod DDL (step 5) so the backward-compat contract is proven, not assumed.

The rule is the same either way: **the lake keeps the source's original bytes, and a custom view - never a
data conversion - is what guarantees the old table's exact columns for backward compatibility.**

## Naming standard (do not deviate)

- Folder: `C:\Projects\V3Upgrade\dwh-pipelines-prod\<ReadableName>\` (readable, e.g. `Baatbooking`, not the cryptic batch code).
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
