---
name: convert-sqlflow-source
description: Convert an old SQLFlow source (a "batch") into SQLFlow V3 flows, phase by phase - acquisition + pre + ods YAMLs, bounded test, full history backfill, schema/count reconciliation against production, and catalog registration via Bitbucket. Use when the user wants to port/migrate/upgrade a legacy SQLFlow source, batch, or its tables to V3 (e.g. "convert the Baatbooking source", "port batch BB", "upgrade the Citybike flows").
---

# Convert an old SQLFlow source to V3

Ports ONE legacy SQLFlow **source** (identified by its `Batch` code) into V3. A full conversion produces, per
dataset: a stage-0 **acquisition** (`api`/`cpy`), a stage-1 **pre** flow (raw landing + typed view), a stage-2
**ods** flow (typed view -> keyed merge into arc), plus compat views where the V3 shape differs from old prod,
a **history backfill** that actually lands the data, and a reconciliation that proves (or honestly documents)
coverage vs old production.

**The work is PHASED. Finish each phase before starting the next, and convert ONE source at a time** (never fan
out across sources; see CLAUDE.md). The phases:

1. **Scope & discovery** - what the batch contains, full column truth, static dependencies.
2. **Acquisition** - find or establish the producer (api flow, or cpy when no producer exists).
3. **Author pre + ods** - generate/write the stage-1/stage-2 YAMLs to the estate conventions.
4. **Validate & bounded test** - prove the path end to end on a small window.
5. **Backfill** - land the FULL history in bounded windows; discover retention horizons; import what the source
   can no longer serve as a deactivated STATIC dataset so new prod still reaches full coverage.
6. **Verify & reconcile** - schema exactness (compat views) and row counts vs old prod, gaps documented.
7. **Ship** - commit/push to Bitbucket (that IS the catalog registration).

A source is not "done" because YAMLs exist. Done = data landed, loaded to arc, reconciled, and registered.

## Inputs

- **Batch code** (required): the legacy `Batch` value, e.g. `BB`. Used to find the flows in the metadata DB.
- **Readable source name** (required): the human folder name, e.g. `Baatbooking`. Output folder =
  `C:\Projects\V3Upgrade\dwh-pipelines-prod\<ReadableName>\` (the official pipelines repo, see Configuration).
- **Scope** (optional): specific tables/FlowIDs. Default = every active flow in the batch.

If the user gives only a readable name, derive the batch code by querying the metadata (Phase 1), or ask.

## Configuration (this environment)

- **Official output repo**: **`C:\Projects\V3Upgrade\dwh-pipelines-prod`** (git remote `origin` ->
  `https://bitbucket.org/kolumbuscode/dwh-pipelines-prod.git`). This is where converted/migrated flows live
  and ship from. Each source is a top-level folder `C:\Projects\V3Upgrade\dwh-pipelines-prod\<ReadableName>\`
  holding its flow YAMLs plus a generated `.sqlflow\lineage\`. Generate flow files straight into this folder,
  commit there, and push to Bitbucket. **The push IS the catalog registration** - the control plane auto-syncs
  the whole repo from Bitbucket (Phase 7); never register a source any other way. The SQLFlowV3 repo's
  `migration/` folder is the tooling/scratch area only, not a shipping location.
- **PATs**: `C:\Projects\pat.txt` holds the GitHub + Bitbucket personal access tokens. The confirmed-working
  Bitbucket push token is also in `.sqlflow/env` as `SQLFLOW_GIT_TOKEN` (username scheme
  `x-bitbucket-api-token-auth`); see also the `bitbucket-prod-repo-push` memory. Push with the token embedded
  in the URL - the named `origin` prompts for credentials and hangs.
- SQL: **everything runs against the real Azure estate**, not localhost. Server
  `tcp:dw-mi-sql-prod.public.6b122fbc620a.database.windows.net,3342`, user `SQLFlow` (connection strings +
  password in `.sqlflow/env`). Targets: pre landing -> **`dw-pre-prod`** (schema `pre`); ods/arc ->
  **`dw-dwh-prod`** (schema `arc`). Catalog: **`dw-sqlflow-prod`** (`SQLFLOW_CATALOG_DB` in `.sqlflow/env`).
- Legacy source server: **`92.221.59.28`** (hosts the old SQLFlow control DB the migration reads from; NOT
  localhost).
- Legacy metadata DB: **`dw-sqlflow-prod-last`** on `92.221.59.28` (the old SQLFlow control DB; read-only,
  never write to it). The generators read it via `sqlcmd` with the credentials they hardcode (`-U SQLFlow`).
- Data lake (source files): account **`dwdatalakeprodv2`**, container `datalakev2`; storage URL base
  `https://dwdatalakeprodv2.dfs.core.windows.net/datalakev2`. **Always use `dwdatalakeprodv2`** for everything
  the V3 pipeline reads/writes. The old `dwdatalakestorev2prod` account is being retired: it appears ONLY as
  the SOURCE side of one-time copy/archive flows (Phases 2 and 5), never as a live pipeline endpoint.
- Reference prod DDL (for schema comparison): `B:\SQLFlowUpgradeV3\dw-dwh-prod\` and `...\dw-pre-prod\`.
- **Real OLD prod DWH (live truth, READ ONLY)**: the User-scoped environment variable **`OldDwhConStr`**
  (read it with `[Environment]::GetEnvironmentVariable('OldDwhConStr','User')`; it is NOT inherited by the
  shell, so `$env:OldDwhConStr` is empty) holds the full connection string to the actual old production DWH:
  server `dw-sql-server-prod.database.windows.net,1433`, database `dw-dwh-prod`, user `dw-kolumbus-admin`.
  Use it when the `B:\` DDL drive is not mounted or the 92.221.59.28 restores are stale/missing a table
  (they lag the real estate; e.g. `arc.Frida_Vehicles` exists only here). **Read operations ONLY**: schema
  reads (`sys.columns`), row counts, reconciliation queries. Never write, never point a flow at it.
- **Real OLD prod PRE (live truth, READ ONLY)**: the User-scoped environment variable **`OldPreConStr`**
  (same access pattern: `[Environment]::GetEnvironmentVariable('OldPreConStr','User')`, NOT inherited by the
  shell) holds the connection string to the actual old production PRE landing database: server
  `dw-sql-server-prod.database.windows.net,1433`, database `dw-pre-prod`, user `dw-kolumbus-admin`. This is
  the counterpart to `OldDwhConStr` and the authority for what the landing tables really look like, what a
  push-fed source actually writes, and which principals were granted on them
  (`sys.database_principals`, `sys.database_permissions`, `sys.database_role_members`). **Read only.**
- Generators: `migration/_tools/Generate-PreFlow.ps1`, `migration/_tools/Generate-OdsFlow.ps1`. Both read the
  legacy metadata over the network and default `-Server` to `92.221.59.28`. Pass `-Server` only to override.
- Run the tooling from the SQLFlowV3 repo root `c:\Projects\SQLFlowV3`. Build the CLI first if needed. When
  the local control plane is running it locks the Debug bins: **build and run the CLI as Release**
  (`dotnet build SqlFlow.sln -c Release`, `dotnet run --project src/SqlFlow.Cli -c Release --no-build -- ...`).

### Credentials caveat (important)

The machine's global `AZURE_*` environment variables point at a **different** service principal and win over
`.sqlflow/env`. Any command that reads the data lake MUST inline-export the correct SP from `creds.txt`:

```bash
export AZURE_TENANT_ID=<tenant> AZURE_CLIENT_ID=<appid> AZURE_CLIENT_SECRET='<secret>' SQLFLOW_AZURE_AUTH=ServicePrincipal
```

Verify once with `dotnet run --project src/SqlFlow.Cli --no-build -- auth --scope storage` (expect `OK`).

---

## Phase 1 - Scope & discovery

Goal: know exactly what the batch contains, the FULL truth of every table's shape, and every static
dependency, BEFORE authoring anything. Do not skip ahead; most conversion defects trace back to a shortcut
here.

### 1.1 Confirm the source is live, then discover its flows

Confirm the source is LIVE (not dead/retired/commented-out, not pointed at a test host). If it is dead or
test-only, STOP and report; do not ship an acquisition for it.

```sql
-- CSV pre flows:
SELECT FlowID, srcPath, trgDBSchTbl FROM flw.PreIngestionCSV
 WHERE Batch = '<BATCH>' AND (DeactivateFromBatch = 0 OR DeactivateFromBatch IS NULL) ORDER BY FlowID;
-- Ingestion (ods) flows:
SELECT FlowID, srcDBSchTbl, trgDBSchTbl FROM flw.Ingestion WHERE Batch = '<BATCH>' ORDER BY FlowID;
```

(Other pre-ingestion kinds exist too: `flw.PreIngestionADO/JSN/PRQ/XLS/XML` - query the one matching the
source's native format.)

### 1.2 Capture the FULL definition of every old table behind the flow (MANDATORY)

**The transform metadata is NOT the whole truth. `flw.PreIngestionTransfrom` (the "main" column list the
generator reads) is frequently STALE: columns were added to the real production table over the years without
anyone back-filling the transform rows.** If you generate the view from the transform metadata alone, the view
silently comes out SHORT and the backward-compat contract is broken. Real case: `Billettkontroll_from_excel_report`
has 48 data columns in prod arc but `flw.PreIngestionTransfrom` lists only 37; the 11 later additions
(`Avdeling`, `Kunde`, `Navn_på_vakt`, `Skjema`, `Ukedag`, `Kontrollmetode`, `Type_kontroll`,
`Signatur__din_Vekter_ID_`, `Signatur_makker`, `Vaktdato`, `Dato`) exist only in the table, not the metadata.

For EACH flow, gather the column set from ALL relevant places and reconcile; the arc table is the
consumer-facing contract:

1. **The prod arc/ODS target table** - the definitive contract. Read the full DDL in
   `B:\SQLFlowUpgradeV3\dw-dwh-prod\`, file `arc.<Table>.Table.sql`. This is the column list, order, and types
   the DWH consumers depend on. (These `.sql` are UTF-16/BOM Scripting-generated files; read as text.)
2. **The prod pre landing table** - `dw-pre-prod`.`pre.<Table>` (or its DDL under `...\dw-pre-prod\`): the raw
   string columns actually landed, i.e. the real header names the source file carries.
3. **The legacy typed view** - if the old DWH is reachable, read `v_<Table>` from `sys.sql_modules` for the
   exact cast expressions the old system used per column.
4. **The transform metadata** - `flw.PreIngestionTransfrom` for the FlowID (colName, datatype, expr). This is
   the generator's input but only a LOWER BOUND on the columns; diff it against #1.

Reconcile: the set of columns and their types MUST equal the prod arc table (#1). Any column in #1 missing
from #4 was added later and MUST be added to the generated view by hand, with the type from #1. Any type
disagreement resolves in favour of #1 (the shipped table).

**The arc table can also be a SUPERSET of the CURRENT source (columns dropped/renamed over time), so the view
must be reconciled against the ACTUAL landed columns too.** The pre landing is string-first: one column per
header in the file being loaded. A view referencing a column the current file lacks fails at run time with
`Invalid column name '<col>'` (Billettkontroll: arc 48 columns, current file 31). Get the real header list
from the file itself (after the copy lands it, `az storage fs file download` and read line 1) or from the pre
table the first run creates. Then:

- Arc column the current file DOES provide: cast the landed column normally.
- Arc column the current file does NOT provide: keep it in the view as a typed NULL
  (`CAST(NULL as <arc-type>) AS [<col>]`) so the arc contract shape is preserved and the merge stays defined.
  Never point the view at a landing column the file lacks.
- The ods merge key may be a provenance column with no landed V3 twin: legacy `FileLineNumber_DW` is V3's
  physical `FileLineNumber` (option `includeFileLineNumber: "true"`) or the data-row index `RowNumber_DW`
  (`includeRowNumber`, default on). Land the right one and alias it in the view
  (`CAST([FileLineNumber] as int) AS [FileLineNumber_DW]`).

**Long free-text columns (e.g. `remarks`) need a wider landing and a bigger parser buffer.** The landing
default is `varchar(255)` and the CSV parser buffer is 1024 bytes, so a prod `varchar(4000)` column truncates
and can fail with `MaxBufferSize exceeded`. Set the pre flow's `source.options`:
`defaultColDataType: "varchar(4000)"` and `maxBufferSize: "65536"`. First confirm the failing row is genuinely
long free-text, not an unbalanced quote (count `"` on the line; even = balanced).

### 1.3 Identify static / manually-maintained dependencies (`man` schema)

**Some upstream tables a batch depends on are static reference data, not pipeline outputs.** The tell is the
**`man` schema** (e.g. `man.Bysykkel_session_metadata`): manually maintained, no producer of any kind. A
star-schema build will reference them, so the fact/dim cannot build until the table exists in the new estate.

**Do NOT invent a flow for these.** The correct action is a **one-time manual table transfer from the OLD
ODS/DWH database to the NEW one**, schema and rows verbatim, exact names/types/contents (same principle as the
compat views). It is an operator step (`INSERT ... SELECT` across a linked server, `bcp`, or SSMS scripts),
run once, not scheduled, not in YAML. Record it as a migration prerequisite for the affected fact/dim. Same
treatment for any hand-curated lookup table (unknown-member rows, category/mapping tables) with no producer.
The `skey` (surrogate key) schema transfers the same way when the EDW layer depends on it.

---

## Phase 2 - Establish the acquisition (stage 0)

Goal: V3 must own how the data ARRIVES, not just how it is read. The going-forward LIVE feed MUST come from the
same EXTERNAL SOURCE INTERFACE the legacy producer read from (the API, SFTP, S3/object store, source database,
etc.), re-established as a V3 `api` / `sftp` / external-`cpy` flow. Find the real producer, port its fetch, and
reproduce whatever it did to the payload. Never guess the producer by name.

**THE OLD STORAGE ACCOUNT IS A TARGET, NOT A SOURCE. NEVER mirror `dwdatalakestorev2prod` -> `dwdatalakeprodv2`
as the live acquisition.** The old lake is where the legacy producer WROTE its output; copying from it leaves V3
permanently dependent on the legacy runbook still running and on a storage account that is being retired. The
moment legacy is turned off (the whole point of the migration) the mirror dries up and the source silently goes
stale. Worse, mirroring a producer's output silently inherits transform logic V3 does NOT own (timezone
conversion, array pivots, cross-endpoint joins, PII scrubbing the runbook performed), so "I copied the files" is
NOT "the acquisition is done": that logic is lost the day the producer is retired. Copying a target and pretending
the acquisition logic is handled is the single most damaging shortcut in a conversion.

**A copy FROM the old storage account is legitimate for ONE thing only: data the external interface can no longer
serve** (history the API/SFTP/S3 has aged out past its retention horizon, or a feed whose endpoint/template
changed so the old captures are the only copy). That is the deactivated STATIC-ARCHIVE backfill of Phase 5.3, NOT
the live feed. Everything the external interface CAN still serve must be acquired FROM that interface. If you
find yourself reaching for an old-lake mirror as the steady-state feed, stop: you are re-pointing the source at a
dying target instead of establishing the real acquisition.

### 2.0 First decide whether the source is PULLED or PUSHED

Everything else in Phase 2 assumes SQLFlow FETCHES the data. Some sources are the other way round: the
upstream platform WRITES rows straight into the pre database, and SQLFlow only merges pre -> arc. The tells,
checked before hunting for a producer:

- The batch has an `flw.Ingestion` row but NO `flw.PreIngestion*` row of any kind. There is no landing flow
  because there is no file and no fetch.
- The ingestion `srcDBSchTbl` is a view in the pre database over a table in a schema that no flow writes,
  typically **`stg`** rather than `pre` (e.g. `pre.v_SanntidNG_...` over `stg.SanntidNG_...`).
- The old pre database (`OldPreConStr`) holds principals that are NOT SQLFlow and NOT the DBA: an external
  managed identity or a source-named SQL login with INSERT/UPDATE on exactly that one staging table. That
  grant list IS the acquisition contract.
- The staging table has no `_DW` provenance columns, and the ingestion's `IncrementalColumns` /
  `DataSetColumn` name columns that do not exist in the source view (leftover defaults from `SQLFlowInit`).
  Do not carry those over: drop the watermark, the legacy engine compared the whole view every run.

For a pushed source there is NO stage-0 flow and there must never be one. The acquisition is a **receive
endpoint**, and porting it means recreating that endpoint in the new estate, as DDL, not YAML:

1. Recreate the staging table in the new pre database, shape-for-shape (including its heap/index layout).
2. Recreate the pre view over it verbatim; its column list is the arc contract.
3. Recreate the writer's principals and grants, enumerating them from the OLD pre database first
   (`sys.database_principals`, `sys.database_permissions`) and reproducing them exactly, no wider.
4. Ship only the `02_ing` flow, plus the DDL scripts, plus a schedule that starts DISABLED.

Head the ing flow with a comment saying the source is pushed and why no acquisition flow exists, so the next
person does not "fix" the missing stage 0 by inventing an api/cpy flow.

**Cutover for a pushed source is not ours to make.** The upstream owner has to repoint their writer at the new
server; until they do, the new staging table stays empty and the V3 flow is a no-op, so there is no value in
enabling its schedule early. Land the history into arc directly (Phase 5.6) and hand back the exact
connection details and grants the upstream needs.

**Grant caveat on the new estate**: `dw-mi-sql-prod` is a Managed Instance with NO Entra administrator and no
instance managed identity, so it cannot authenticate Entra principals at all. `CREATE USER [x] FROM EXTERNAL
PROVIDER` fails with "Only connections established with Active Directory accounts can create other Active
Directory users", and the Azure SQL Database escape hatch `CREATE USER [x] WITH SID = 0x..., TYPE = E` is a
syntax error on MI. The SQLFlow login is `db_owner` but holds no server role and only `CONNECT SQL`, so it
cannot create a SQL login either. Reproducing an external writer's access therefore needs an instance
administrator (`dwmiadmin`, whose password is not in `sqlflow-v3-secrets`) and, for the Entra path, two
instance-level changes: `az sql mi update --assign-identity` and `az sql mi ad-admin create`. Both are shared
prod infrastructure: propose them, do not perform them.

### 2.1 Crosscheck the producer against what the pre flows READ (MANDATORY)

The file names/paths the producer WRITES must match the `srcFile`/`srcPath` patterns the batch's pre flows
READ (from `flw.PreIngestionCSV` etc.). **If they do not match, you have the wrong script; stop and find the
right producer.** A name overlap is not proof (estate example: `mobilapp.ps1` writes `export_*` files for the
MobilApp feeds, while Billettapp's pre reads `billettapp*.csv` produced by the `Billettapp` function in
`dw-function-prod`; porting mobilapp.ps1 as billettapp's acquisition shipped a copy flow matching 0 files).

How to find the producer:

1. Get the exact file identity from the metadata: `srcPath` (lake path read) + `srcFile` (filename pattern).
   These strings, not the batch code, are what you match against.
2. Automation runbooks are checked out at **`C:\Projects\V3Upgrade\automation-runbooks`**. Grep their CONTENT
   for the path/file stems: `grep -rniE "raw/<source>|<file-stem>" C:\Projects\V3Upgrade\automation-runbooks`.
3. The definitive link is a runbook line that **writes that exact lake path** (typically
   `New-AzDataLakeGen2Item ... -Path "raw/<source>/..."`). A hit on a script's filename or an unrelated column
   name is NOT a match; read the surrounding line.
4. If no runbook writes the path, widen to the whole tree (`grep -rniE "<file-stem>|raw/<source>" C:\Projects`),
   then check the function apps (`dw-function-prod`, `dw-function-py-prod`) and ADF. A `from_excel_report/`
   path or `pss_*` name is the tell of a manual/system export with no script.
5. **ADF caveat**: the one factory is **`dw-datafactory-prod`** in **`datawarehouse-west-rg-prod`** (readable
   with the machine's `az login`). **The `SQLFlow_<Batch>` pipelines there are NOT producers - they are the OLD
   SCHEDULER** (an AzureFunctionActivity calling `ExecFlowBatch?Batch=<Batch>`). Finding one answers "how was
   the batch scheduled" (maps to a V3 schedule), never "how do files arrive". A real ADF producer would be a
   `Copy` activity with a sink dataset on the lake path; its absence means ADF is orchestration-only there.

Worked example (Billettkontroll): pre reads `raw/billettkontroll/pss/history/ticket_control*.csv` and
`.../from_excel_report/pss_billettkontrol.csv`. Content greps found no runbook writing `raw/billettkontroll`;
whole-tree grep empty; ADF had only the `ExecFlowBatch` scheduler pipeline and no Copy activity on that path.
Conclusion: no automated producer; the files are manual/PSS exports.

### 2.2 API-fed sources: one multi-item `api` flow, raw format

When the producer is an API-calling runbook (or several for one source), consolidate them into ONE declarative
multi-item `api` flow (`<source>_00_api.yaml`), one `items:` entry per dataset, sharing the source envelope
(baseUrl, token exchange via `${keyvault:...}`, reliability). Land the RAW response verbatim
(`format: json` - never flatten to CSV; see Phase 3.4) into
`raw/<source>/api/<dataset>/history/...` on `dwdatalakeprodv2`, exactly where the stage-1 pre flows read, so
lineage connects acquire -> pre -> ods.

- Windowed endpoints get `iterate: date_window` (granularity hour/day/month) with a small rolling default
  (`from: now-2d to: now`); the CLI's `--from/--to` overrides the window for backfills (Phase 5).
- Per-entity endpoints add `ids_from` (discover ids, fan out one request per id per window). Prefer `month`
  granularity on per-entity fan-outs: `day` multiplies the request count ~30x.
- **Reliability tuning**: the engine runs each item's fan-out with bounded concurrency
  (`reliability.concurrency`, default 8) gated by `rateLimitRps`. For wide per-entity backfills set both
  explicitly (e.g. `rateLimitRps: 20`, `concurrency: 16`); the retry honors 429 Retry-After so an over-eager
  rate self-corrects. Without concurrency a wide fan-out is latency-bound one file at a time.
- Leave the flow UNSCHEDULED (or the schedule disabled) while the legacy batch still runs daily against the
  old lake; wiring the daily schedule is a cutover step, so the two never double-feed the estate.
- **The engine lands the RAW payload and never redacts or projects it** (`LandingPipeline`: "It never transforms
  the payload: the raw incoming format is preserved"). If the legacy producer SCRUBBED PII or dropped fields
  before writing (rider names, addresses, phone, exact coordinates), landing the raw API response would PERSIST
  that PII at rest in the lake and the pre table. Reproducing the scrub in the typed VIEW is NOT enough: the raw
  landed file already holds it. Such a source needs the PII fields excluded AT LANDING (an acquire-engine
  projection/redaction step). Close that gap at the acquisition; do NOT fall back to mirroring the old lake's
  already-scrubbed output as the live feed (that is the forbidden "target as source" shortcut, and it hides the
  fact that V3 never owned the scrub). Flag the missing capability and add it, rather than copying a target.

### 2.3 No external interface at all: establish the acquisition as a one-time `cpy` from the old lake

This case is ONLY for a source with NO external interface to re-establish: manual/system exports (a person's
Excel upload, a PSS-system dump) that only EVER existed as files in the old lake, with no API/SFTP/S3/database
behind them to fetch from. Billettkontroll is the archetype: an exhaustive search found no producer of any kind,
so the old-lake files ARE the source. Then build a `<table>_00_cpy.yaml` copy flow: source = old lake path,
target = `dwdatalakeprodv2` at the SAME path the pre flow reads (so lineage connects). This is not "changing a
flow's endpoint" - it establishes the missing acquisition, sanctioned for the genuinely no-interface case.

**Do not reach for this just because an external fetch is inconvenient.** "No producer found" must mean you
PROVED there is no external interface (2.1's exhaustive search came up empty), not that mirroring the old lake
was easier. If a producer exists that calls an API/SFTP/S3/DB, the live feed comes from THAT interface (2.2 /
2.4), and the old lake is only for aged-out history (5.3).

- **Do NOT give such a batch a live recurring schedule** (there is no producer; a schedule would imply one).
- **A copy flow has NO `mode:` field** (`mode: manual` is silently ignored). The estate's manual-only idiom is
  a **disabled schedule** on the copy:

```yaml
schedule:
  name: <source>_manual
  enabled: false   # validates without a cron; nothing fires it, but it can be triggered by hand
```

- Copy option gotcha: the old lake has 0-byte ADLS directory-marker blobs under year subfolders; with
  structure preserved they get copied as files and 404/block nested writes. When the file names are globally
  unique, set `preserveStructure: false` and land flat.

### 2.4 Non-API external interfaces: re-establish the fetch (SFTP / S3 / database), do not mirror the old lake

When the producer reads a NON-API external interface (an SFTP drop, an S3 / object-store export, a source
database), the live feed is a V3 flow that reads THAT interface directly: a `cpy` whose SOURCE is the external
store (e.g. `s3://...`, an `sftp://` path), an `sftp` acquisition, or a direct-from-source pre. Point it at the
same external location the legacy producer read, land to the path the pre reads, and reproduce the producer's
reshaping in the typed view (Phase 3.4). Use `modifiedWithinDays: <n>` for the steady-state daily window and `0`
for the one-time full backfill.

**Do NOT substitute a mirror of the old lake for this**, even when the runbook conveniently left a normalized
copy on `dwdatalakestorev2prod`. Copying THAT is the forbidden "old storage account as source" shortcut (see the
Phase 2 goal): it inherits the runbook's reshaping without owning it, and it dies when the runbook is retired.
The ONLY slice that legitimately comes from the old lake is history the external interface no longer serves
(retention horizon, or a format/endpoint change that makes the old captures the only copy), imported as the
deactivated static archive of Phase 5.3. If re-establishing the external fetch needs a capability the engine
lacks (say a landing-time field projection, or an auth mode), the correct move is to ADD it, not to fall back to
mirroring a dying target.

---

## Phase 3 - Author the pre + ods flows (stages 1-2)

### 3.1 Ensure target schemas exist

```sql
-- in dw-pre-prod:  IF SCHEMA_ID('pre') IS NULL EXEC('CREATE SCHEMA pre');
-- in dw-dwh-prod:  IF SCHEMA_ID('arc') IS NULL EXEC('CREATE SCHEMA arc');
```

### 3.2 Generate (CSV sources) or author (JSON/API sources) the flow files

For CSV-metadata flows, generate straight into the pipelines repo:

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

For API/JSON sources, author `<source>_<object>_01_jsn.yaml` + `<source>_<object>_02_ing.yaml` by hand on the
sibling-folder pattern; the typed view reproduces the legacy column contract from the raw JSON (Phase 3.4).

### 3.3 Naming and grouping standard (match the sibling folders exactly)

- Folder: `C:\Projects\V3Upgrade\dwh-pipelines-prod\<ReadableName>\` (readable name, not the batch code).
- File and flow `name:`: `<source>_<object>_<counter>_<flowtype>`, `<source>` = folder name lowercased. The
  pipeline prefix ALWAYS matches its repo folder, NOT the target table's prefix (Citybike's tables are
  `Bysykkel_*` but its flows are `citybike_*`; the old names stay only inside flow bodies).
- Counters order the stages: `00` acquisition (`api`/`cpy`), `01` pre (`csv`/`jsn`), `02` ods (`ing`),
  `03` EDW (`ing`/`sp`).
- `batch:`: per-object grouping label shared by a dataset's pre+ods pair (e.g. `batch: bikes`); the
  acquisition uses its flow type (`api`, `copy`). Grouping/filter label only; NO effect on scheduling.
- **Scheduling is by schedule membership, never by batch**: define ONE schedule on the acquisition anchor
  (`schedule:` block, e.g. `citybike_daily`, cron + timezone) and have every pre/ods flow join it with
  `schedule: <name>`. One fire runs the whole source wave-ordered.
- Object names (tables/views) are locked to production and must match exactly, including casing.

### 3.4 Raw format in the lake; backward compatibility via the typed view

**Keep the upstream's ORIGINAL/native format in the lake. Never re-encode it.** JSON stays JSON, XML stays
XML. The legacy scripts' CSV flattening historically lost detail (dropped fields, truncated precision,
culture-mangled values), which is exactly why V3 acquisitions land raw.

**Backward compatibility is provided by a view, not by reshaping data.** The old table's column names/types
are a CONTRACT. The pre flow's typed view (`generateView`) projects the legacy column names over the raw
landing; the ods flow merges that view into arc so the shape consumers query is the production shape. The raw
landing table may look nothing like the old table; the view is what matches.

- Upstream natively JSON/XML: land raw, write the typed view to surface the legacy CSV-era names. Adapt the
  view expressions to the raw tokens (JSON invariant decimals, ISO-8601 dates); legacy culture-specific
  `TRY_PARSE ... USING 'nn-NO'` exprs typically NULL out on JSON tokens while output types must stay identical
  to prod. Test expressions against the real server.
- Old solution genuinely CSV and files still arrive as CSV: keep CSV, do not re-encode; verify the generated
  view column-for-column against prod DDL (Phase 6).

### 3.5 Faithfulness rules (matching prod)

- Use **all** transform columns from `flw.PreIngestionTransfrom` (data + the 5 `_DW` provenance columns); the
  metadata is authoritative over the current file's header set (Phase 1.2 covers both directions).
- Provenance columns land as `varchar(255)`; the typed view casts them (`FileDate_DW`/`DataSet_DW` ->
  `decimal/numeric(14,0)`, `FileRowDate_DW` -> datetime, `FileSize_DW` -> `decimal(18,0)`).
- Disable provenance columns the metadata does not list (notably `RowNumber_DW`, which legacy never had).
- Surrogate PK: `target.identityColumn` from `flw.Ingestion.IdentityColumn`, verbatim; omit when NULL.
- Merge keys from `KeyColumns`; incremental watermark from `IncrementalColumns` + `NoOfOverlapDays`; audit
  columns from `SysColumns` (`InsertedDate_DW`, `UpdatedDate_DW`, as `datetime`).
- **The incremental watermark must be a source-view column.** Legacy flows that used
  `UpdatedDate_DW`/`InsertedDate_DW` (audit columns on the old source table) must be remapped to
  `FileDate_DW`; the engine otherwise throws `Incremental column '...' is not among the source columns`. The
  ods generator does this remap automatically.
- `change.hashColumns` is change-DETECTION only, never merge keying.

---

## Phase 4 - Validate & bounded test

Prove the path end to end on a SMALL slice before any full load.

```bash
dotnet run --project src/SqlFlow.Cli -c Release --no-build -- validate "C:/Projects/V3Upgrade/dwh-pipelines-prod/<ReadableName>/<flow>.yaml"
```

- Validate every flow in the source (acquisition, pre, ods).
- Test-load a bounded window: temporarily narrow the pre flow's `location` to one partition
  (e.g. `.../<dataset>/2018/05/`), `run` it, then `run` the ods flow. Confirm rows appear in pre, the typed
  view selects, and the arc merge inserts.
- For an `api` acquisition, run one bounded historical window FIRST (e.g. a single old year) and check what it
  actually returns before committing to the full sweep: a fan-out over a window the API does not retain burns
  hours landing nothing (Phase 5.2). `landing.skip ... empty payload` across the board = the API has no data
  there.
- Pick the fastest smoke test available; long runs go to background tasks with logs.

---

## Phase 5 - Backfill: land the FULL history

Landing data is the point; an authored YAML that never runs is not progress.

### 5.1 Drive the acquisition backfill in bounded windows

Run the `api` acquisition with `--from/--to` overriding the rolling window, **in 6-month windows**, oldest
first, one window at a time in a background task with a log file:

```bash
dotnet run --project src/SqlFlow.Cli -c Release --no-build -- run "<source>_00_api.yaml" --from 2021-01-01 --to 2021-07-01 > <log> 2>&1
```

- 6-month windows keep the per-entity fan-out bounded (months x ids per run) and give a natural
  progress/verification checkpoint per window (files landed per dataset, bytes, skips).
- Watch the first window's log before queueing more: `landing.write` lines with real byte sizes = data;
  uniform `landing.skip ... empty payload` = nothing served for that period.
- No nested `&` launches inside a background task (the child gets orphaned); one window = one tracked task.

### 5.2 AFTER the backfill: check what is missing, then note it in the acquisition YAML

The order is fixed: **first RUN the backfill to completion (every window, oldest to newest), THEN check what
is missing, THEN write the note.** Do not pre-judge what the source will serve or write coverage claims from
assumptions; the finished sweep is the evidence.

1. Run all windows (5.1) until the sweep reaches the present.
2. Check what is missing: per dataset, compare what landed/loaded against old prod. APIs frequently do NOT
   retain deep history, and retention differs per endpoint within one source. Citybike measured:
   `issue_report` served from 2021, `bikes_session` from 2023-05, `alert` only from 2023-07, snapshot
   endpoints (bikes/inventory/...) current-state only. Confirm horizons with the EVENT dates in arc after
   loading, not just file dates.
3. Add a note about it **as a comment block in the acquisition YAML** (see `Citybike/citybike_00_api.yaml`
   for the format): retention horizon per dataset, arc rows vs old prod, and where any missing remainder
   lives. This note is part of the backfill deliverable, not optional documentation.

### 5.3 Import the API-unreachable history as a STATIC dataset (full coverage in new prod)

**There is ALWAYS old history the live source no longer serves (an API that only keeps a recent window, a feed
whose template/endpoint changed). That data must not be left behind: it lives ONLY in the old lake as the legacy
captures, and the new prod DWH must still reach full coverage. The standard treatment is a one-time, DEACTIVATED
"static dataset" consolidation pipeline that copies those files into the new storage account and LOADS them into
the same old-prod arc table, so `arc.<Table>` ends up with the full history (live era + static archive), not just
what the API can still serve.** This is the general pattern for every source, not an Entur special case.

**Copy from the OLD storage account ONLY for data the external interface (API, SFTP, S3, etc.) cannot serve.**
The live acquisition is always the source of record for everything it CAN return; the archive exists solely to
recover the remainder. Before building the archive, MEASURE that remainder against the live interface and let it
justify the copy: the archive's net contribution to `arc.<Table>` must be exactly the interface-unreachable rows.
Two shapes recur:
- **Windowed/history feed** (the API keeps only a recent window): the remainder is every period older than the
  retention horizon. Copy those older years only; do not re-copy files inside the window the API still serves.
- **Current-state interface** (a register/snapshot API with no history endpoints, e.g. Frida vehicles): the API
  returns only "now", so the remainder is the rows that have since LEFT the current state (a departed vehicle, a
  closed account). The arc table is keyed current-state, so replaying the old snapshots contributes only those
  departed rows (the current rows collapse to a no-op refresh). Confirm the delta explicitly - e.g. `arc` rows
  NOT EXISTS in the live pre landing - and record the exact count/example in the coverage note, so the copy is
  demonstrably scoped to API-missing data (Frida: live API 503, archive adds 1 departed vehicle, arc = 504).

Three deactivated flows, run once by hand, then left in the repo as the documented consolidation:

1. **Copy the static files into new storage** (`<source>_archive_00_cpy.yaml`): old lake
   `raw/<source>/.../history/<year>` -> new lake `raw/<source>/archive/<dataset>/history/<year>`, one item per
   missing year. `preserveStructure: false` per year item (directory-marker blobs, Phase 2.3; safe because files
   are date-stamped and unique). Verify per-year counts equal the old lake. The static dataset now lives in the
   storage account for good.
2. **Pre-load the static files in their OLD format** (`<source>_<object>_archive_01_<csv|xls|...>.yaml`): read the
   copied files with a pre flow matching the OLD delivered format (usually the legacy CSV, i.e. the flatten the
   old runbook produced - already the arc column names), landing to its OWN `pre.<Table>_archive` table + typed
   view `v_<Table>_archive`. Keep it separate from the live pre so the two formats never collide.
3. **Merge the static rows into the SAME arc table** (`<source>_<object>_archive_02_ing.yaml`): keyed merge
   `v_<Table>_archive` -> `arc.<Table>`, **windowed to the non-overlapping period** so the static import and the
   live era do not double-count the boundary (e.g. `AND [<dateCol>] < '<cutover>'` where the live feed starts).
   The window is essential: the two eras compute different row hashes for the same logical row (different raw
   date/number formats), so an unwindowed overlap would duplicate rather than dedupe.

- **All three carry a DISABLED schedule** (`enabled: false`): a manual, run-once consolidation, never scheduled
  (there is no live producer for this history). Head the acquisition/pre with a comment block stating this is
  STATIC ARCHIVE data, the window it covers, and that it is manual.
- **Consolidate in a view ONLY when the archive cannot share the live arc table** (a genuinely different
  schema/format the arc table can't represent). Then keep `arc.<Table>` as the live physical table, load the
  static rows into `arc.<Table>_archive`, and expose a VIEW under the OLD dwh table name that `UNION ALL`s the two
  in the old-prod column order/types. When both eras share the arc schema (the common case), skip the view and
  merge straight into `arc.<Table>` - one physical table already IS full coverage.
- Confirm full coverage after loading: `arc.<Table>` row count and event-date range should span BOTH eras and
  reconcile against old prod (Phase 6.3).

### 5.4 Run the full pre + ods loads

- Run each pre flow `--full` (background task + log; a JSON pre over tens of thousands of files is a LONG run,
  dominated by sequential schema inference before `target.load` starts).
- Then run the ods flow. Independent datasets can overlap (a file-read-heavy pre alongside a DB-heavy ods),
  but never two runs writing the same tables.
- **Check the target's existing arc counts BEFORE launching an expensive re-load.** A dataset may already be
  loaded from an earlier run; re-running a `--full` pre APPENDS to the pre landing (mode append), doubling the
  staging for zero arc gain.

### 5.5 Read the ods result numbers critically (they encode the load's correctness)

- `N staged, 0 inserted, 0 updated` on a keyed merge: either the target already holds every key (idempotent
  re-run: fine; verify with distinct-key counts pre vs arc), or the merge keys are NULL in the source (NULL
  never equals NULL in the merge join, silently dropping those rows). Diagnose with
  `COUNT(*) vs COUNT(key) vs COUNT(DISTINCT key)` on the source view.
- NULL-able or absent business keys => go keyless: build a dedup VIEW over pre
  (`ROW_NUMBER() OVER (PARTITION BY <business cols> ORDER BY FileDate_DW DESC) ... WHERE _rn = 1`) and load it
  keyless with `truncateBeforeLoad: true` (full refresh) instead of a keyed merge that drops NULL-key rows.
- Note `truncateBeforeLoad: true` bypasses per-key dedup entirely (fast insert-all): the source view must
  already be unique.
- Merges beyond ~1M rows on Azure SQL: set `batchUpsert: true` (+ `batchUpsertRowCount`) or the single
  transaction aborts with "SqlTransaction has completed".

### 5.6 PUSHED sources: seed arc straight from old prod over the OLDPROD linked server

A pushed source (Phase 2.0) has no acquisition to replay, so the whole history is seeded server-to-server
from old prod's arc table via the `OLDPROD` linked server on the MI, the same route Phase 5.3 uses for the
fara-dat archive. Old prod is an Azure SQL Database and the new estate a Managed Instance, so there is no
backup/restore path, and a client-side bulk copy bottlenecks on the operator's link.

- Carry the surrogate PK across verbatim under `SET IDENTITY_INSERT`, so new arc is row-for-row identical to
  old arc and anything downstream holding that key still resolves. `IDENTITY_INSERT` is session scoped: the
  whole windowed loop must run on ONE connection.
- Create the target's CLUSTERED PK before the load (ascending identity inserts append cheaply) but build the
  nonclustered merge-key index AFTER it, or every insert becomes a random one.
- Window on the PK (2-4M rows), with a `NOT EXISTS` anti-join per window so an interrupted run resumes.
- Do NOT bother seeding the new staging table. The staging table is a receive buffer, not a consumer contract:
  its content is already fully represented in arc, the upstream writer refills it after cutover, and (unlike
  arc) there is no linked server to the old PRE database, only to the old DWH. Say so in the flow comment
  rather than leaving it looking like an oversight.
- Old prod keeps merging until cutover, so the seed is a snapshot with a small recent tail missing. That tail
  lands on the first V3 fire after cutover, because the merge key is unique and re-asserting a row is a no-op.
  Record the snapshot date and count.

---

## Phase 6 - Verify & reconcile

### 6.1 Schema exactness vs prod DDL

Compare the typed view / arc table against the prod DDL in `B:\SQLFlowUpgradeV3\`: materialize the prod object
(renamed) and diff the `sys.columns` name+type lists. Expected: exact match (one known synonym: V3 emits
`decimal(14,0)` where prod DDL says `numeric(14,0)`).

**First check the COUNT, then the names.** Stale metadata (Phase 1.2) produces views that validate and load
yet miss columns; this diff is how that gap is caught. If the view has fewer columns than prod arc, go back to
Phase 1.2, add the missing columns, and re-diff until equal.

### 6.2 MATCH THE OLD PRODUCTION FORMAT EXACTLY (compat view under the old name)

**FUNDAMENTAL PRINCIPLE: PORTING MUST NOT CHANGE WHAT DOWNSTREAM CONSUMERS SEE. THE CONSUMER-FACING
`arc.<OldTable>` MUST MATCH OLD PRODUCTION EXACTLY: SAME NAME, COLUMN SET, COLUMN ORDER, NAMES, TYPES
(INCLUDING LENGTHS AND `decimal` vs `numeric`).**

The V3 arc table's physical column ORDER (and occasionally a type) will NOT match the hand-built old table
even with an identical column set (surrogate PK, audit columns, and legacy-extra columns shift positions), so
`SELECT *`/positional consumers would break. Diff the FULL ORDERED (name, type) list against
`B:\SQLFlowUpgradeV3\dw-dwh-prod\arc.<OldTable>.Table.sql`. When not byte-for-byte identical (it usually is
not):

- Point the ods `target.object` at a physical table named for the V3 SOURCE, matching the flow prefix
  (e.g. `arc.Citybike_<Object>`, NOT the old `arc.Bysykkel_<Object>`, NOT an `_ods` suffix).
- Create a VIEW under the OLD production name over that physical table, projecting the EXACT old-prod format
  (`CAST` every column to its old type, old order). Downstream keeps its name and shape.
- Keep the `CREATE OR ALTER VIEW` statements in a `compat_views.sql` in the source folder. One-time manual
  DDL; not bolted onto the ods run.

### 6.3 Reconcile row counts vs old production, and explain every gap

Count old prod vs new arc per table. For each shortfall, identify WHICH bucket it is - the answer changes the
action:

1. **Source retention** (the API cannot serve older periods): not recoverable from the source. Preserve via
   the archive (Phase 5.3) and document.
2. **Our request shape**: a windowed POST body can silently narrow results (Citybike's `issue/filter` with
   `Categories:[0]` returned ~1.8K vs old prod's 181K). When one filter-endpoint dataset is drastically short
   while its siblings reconcile, suspect the request body BEFORE blaming retention; test a broader query.
3. **Snapshot endpoints**: the API returns only current state; old prod's accumulated daily snapshots are not
   reconstructable from the API. Current-state is usually what dims need; document the history as
   old-lake-only.
4. **Load defect**: dedup/NULL-key/merge problems (Phase 5.5). Fix and re-run; never document away a load bug.

Write the final coverage stats into the acquisition YAML's comment block (Phase 5.2) so the next person sees
retention horizons, arc-vs-old-prod counts, and where any missing remainder lives, right where the feed is
defined.

---

## Phase 7 - Ship & register (Bitbucket push = catalog registration)

**The catalog registration is the Bitbucket push, nothing else.** The control plane auto-syncs the WHOLE
`dwh-pipelines-prod` repo on every push (folder-prefixed paths like `<ReadableName>/<flow>.yaml`); that is
what puts flows in the catalog and computes lineage.

**Do NOT run `sqlflow db sync "<folder>" --repo <ReadableName>`.** It registers the same flows a SECOND time
under a different repo with paths that do not exist, so the GUI shows every flow twice and re-syncing never
dedupes. Remove a stray repo by reconciling against an empty path:
`sqlflow db sync <empty-dir> --repo <name>`.

Commit and push (commit as the human user, never attribute to Claude):

```bash
cd /c/Projects/V3Upgrade/dwh-pipelines-prod
git add -A <ReadableName>/ && git commit -m "Add <ReadableName> flows (ported from legacy FlowIDs ...)"
TOK=$(grep -oE 'SQLFLOW_GIT_TOKEN=.*' /c/Projects/SQLFlowV3/.sqlflow/env | cut -d= -f2-)
GIT_TERMINAL_PROMPT=0 git -c credential.helper= push \
  "https://x-bitbucket-api-token-auth:${TOK}@bitbucket.org/kolumbuscode/dwh-pipelines-prod.git" main
```

Confirm the flows landed in the catalog (`SQLFLOW_CATALOG_DB` = `dw-sqlflow-prod`):

```sql
SELECT p.Name, p.Kind, p.Batch, p.Wave FROM catalog.Pipeline p
  JOIN catalog.Repo r ON r.Id = p.RepoId
 WHERE r.Name = 'dwh-pipelines-prod' AND p.Batch = '<BATCH>' ORDER BY p.Wave, p.Name;
```

The connected view->table column lineage requires the created `v_` views to exist in the DB, so run the flows
(Phases 4-5) before the sync that should pick them up.

---

## Definition of done for a source

Every dataset in the batch has its stage flows (acquisition + `01` pre + `02` ods) that: validate; landed the
FULL available history (Phase 5, retention horizons measured and any unreachable remainder archived); loaded
end to end (pre -> typed `v_` view -> arc); match the prod schema column-for-column with compat views where
the physical shape differs; reconcile against old prod row counts with every gap explained and the coverage
stats documented in the acquisition YAML; and, after the Bitbucket push, appear in the catalog
(`dw-sqlflow-prod`) under repo `dwh-pipelines-prod` and their batch.
