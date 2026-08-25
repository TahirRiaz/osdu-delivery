---
name: reconcile-arc-table
description: Reconcile an already-converted table between OLD production and the NEW V3 estate - find out WHY the row counts differ, classify every difference by cause, and converge the two by transferring the genuinely missing rows over the OLDPROD linked server. Use when a ported table "does not match", has more or fewer rows than old prod, or needs proving equal (e.g. "arc.X does not match", "why does the new table have fewer rows", "make both tables the same", "verify the ported table").
---

# Reconcile a converted arc table against old production

Takes ONE already-converted table and answers three questions in order: **why do the two estates disagree**,
**which differences are defects and which are expected**, and **what exactly should be transferred to make
them agree**. It ends with the two tables holding the same logical data, and the finding written into the
source's YAML coverage note so it stays known.

This is the AFTER-conversion counterpart to `convert-sqlflow-source`. Use that skill to port a source; use
this one when a ported table's numbers are questioned.

**The governing rule: `COUNT(*)` IS NOT THE COMPARISON.** Old prod and V3 legitimately hold different
physical row counts for identical logical data, in both directions. Reaching for `COUNT(*)` and "fixing" the
difference is how real data gets duplicated or deleted. Establish the logical key first (Phase 2); every
number after that depends on it.

---

## Inputs

- **Table** (required): the arc table, e.g. `arc.stavanger_parkering`. If old prod uses a different name
  (e.g. `arc.Bysykkel_Trips` vs V3's `arc.Citybike_Trips`), you need both.
- **Symptom** (optional): "fewer rows", "more rows", "data does not match". Useful but never trusted; the
  measurement decides.

---

## Configuration (this environment)

Same estate as `convert-sqlflow-source`. Everything runs against the real Azure estate, never localhost.

- **NEW estate (target, read/write)**: server
  `tcp:dw-mi-sql-prod.public.6b122fbc620a.database.windows.net,3342`, user `SQLFlow`. Connection strings and
  password in `c:\Projects\SQLFlowV3\.sqlflow\env`:
  - `SQLFLOW_CONN_DWDWHPROD` -> `dw-dwh-prod` (schema `arc`) - where arc tables live
  - `SQLFLOW_CONN_DWPREPROD` -> `dw-pre-prod` (schema `pre`) - landing tables and typed `v_` views
  - `SQLFLOW_CATALOG_DB` -> `dw-sqlflow-prod` - the V3 catalog
- **OLD prod DWH (live truth, READ ONLY)**: User-scoped env var **`OldDwhConStr`**; server
  `dw-sql-server-prod.database.windows.net,1433`, database `dw-dwh-prod`, user `dw-kolumbus-admin`.
- **OLD prod PRE (live truth, READ ONLY)**: User-scoped env var **`OldPreConStr`**, same server, database
  `dw-pre-prod`. The authority for what actually landed, which is where a value divergence is usually settled.
- **OLD SQLFlow control DB (READ ONLY)**: User-scoped env var **`OldSQlFlowConStr`**, database
  `dw-sqlflow-prod` on the old server. Holds `flw.Ingestion` (KeyColumns, IncrementalColumns, SysColumns) and
  `flw.PreIngestion*`. **This is where the legacy merge key comes from**, which is the starting point for the
  logical key in Phase 2. Never write to it.
- **`OLDPROD` linked server**: already configured on the new MI, pointing at old prod DWH. This is the
  transfer route: server-to-server, no client-side bulk copy. Reference as
  `OLDPROD.[dw-dwh-prod].[arc].[<Table>]`, or through `OPENQUERY(OLDPROD, '...')` to push work to the old side.
- **Reference prod DDL**: `B:\SQLFlowUpgradeV3\dw-dwh-prod\arc.<Table>.Table.sql` and `...\dw-pre-prod\`.
  These are UTF-16/BOM scripted files, and the `B:` drive is often not mounted. Prefer live `sys.columns` over
  `OldDwhConStr`.
- **Pipelines repo (where the finding is documented)**:
  `C:\Projects\V3Upgrade\dwh-pipelines-prod\<ReadableName>\`, git remote
  `https://bitbucket.org/kolumbuscode/dwh-pipelines-prod.git`. The push IS the catalog registration.
  Token: `SQLFLOW_GIT_TOKEN` in `.sqlflow/env`, username scheme `x-bitbucket-api-token-auth`, embedded in the
  URL (the named `origin` prompts and hangs).
- Stale fallback: `dw-sqlflow-prod-last` on `92.221.59.28` is an OLD restore that lags the estate and has been
  unreachable since 2026-08-24. Use only if the live server is down, and say so.
- CLI (rarely needed here): build and run as Release when the local control plane is running, since it locks
  the Debug bins: `dotnet build SqlFlow.sln -c Release`,
  `dotnet run --project src/SqlFlow.Cli -c Release --no-build -- ...`.

### Reading a User-scoped connection string

`OldDwhConStr` / `OldPreConStr` / `OldSQlFlowConStr` are **not inherited by the shell**, so `$env:OldDwhConStr`
is empty. Read them from the User scope and unpack:

```powershell
$b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
# psbase is REQUIRED: the builder implements IDictionary, so a plain $b.ConnectionString = ... is routed to
# the indexer and stores the whole string under a key named 'ConnectionString', leaving every part empty.
$b.psbase.ConnectionString = [Environment]::GetEnvironmentVariable('OldDwhConStr','User')
sqlcmd -S $b.psbase.DataSource -d $b.psbase.InitialCatalog -U $b.psbase.UserID -P $b.psbase.Password `
       -C -h -1 -W -Q "SET NOCOUNT ON; <query>"
```

### Project rules that bind this work

- **Never change a flow's source or destination.** Reconciliation NEVER repoints a flow. Diagnose, propose,
  stop. (CLAUDE.md.)
- **Never attribute commits to Claude.** Commit as the human git identity, no trailers.
- Work directly on `main` in both repos. No feature branches.
- No em dash (U+2014) anywhere, including SQL comments and YAML notes.
- No time estimates.
- Catalog schema changes need an EF Core migration. This skill does not normally touch the catalog.

---

## The tool

`scripts/Compare-ArcTable.ps1` runs Phases 1 and 3-5 mechanically and prints a verdict. It is READ ONLY and
never writes to either estate.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/reconcile-arc-table/scripts/Compare-ArcTable.ps1 `
  -Table arc.stavanger_parkering `
  -Key "Dato,Sted,CAST(Klokkeslett AS time)" `
  -DateColumn Dato
```

It emits: schema parity, count decomposition, per-period breakdown, bidirectional anti-join, and NULL-safe
value parity with a per-column mismatch breakdown. Useful switches: `-OldTable` when names differ, `-Where`
to bound a large table, `-CompareColumns` to override value-parity columns, `-Period day` to localise a
narrow divergence, `-SampleRows 0` to suppress examples.

The tool does NOT decide anything. **Phase 2 (the key) is yours to establish and Phase 5 (classification) is
yours to judge.** Feeding it a wrong key produces confident, wrong numbers.

---

## Phase 1 - Schema parity

Decides whether a direct transfer is even legal, so it comes first.

The tool's section 1 diffs the full ordered `(name, type, nullability)` list on both sides. Three outcomes:

- **Identical** -> a straight `INSERT ... SELECT` over `OLDPROD` is structurally valid. Proceed.
- **Differs in column ORDER only, same set** -> the V3 physical shape drifted (surrogate PK, audit columns,
  legacy-extra columns shift positions). Transfer with an EXPLICIT column list, and check that a compat view
  under the old name exists for downstream (`convert-sqlflow-source` Phase 6.2).
- **Differs in column SET or types** -> STOP. This is a conversion defect, not a data gap. Fix the view/flow
  first; transferring rows into a wrong-shaped table entrenches the defect.

A type difference of `decimal(14,0)` vs `numeric(14,0)` is a known synonym pair, not a real difference.

---

## Phase 2 - Establish the LOGICAL key (the step everything depends on)

The logical key identifies ONE real-world reading on BOTH estates. It is usually NOT the physical merge key.

**Start from the legacy merge key**, then subtract what is not stable across estates:

```sql
-- against OldSQlFlowConStr
SELECT FlowID, trgDBSchTbl, KeyColumns, IncrementalColumns, DataSetColumn, SysColumns, IdentityColumn
FROM flw.Ingestion WHERE trgDBSchTbl LIKE '%<Table>%';
```

Then remove or normalize:

- **The surrogate PK** (`IdentityColumn`). Always excluded; it is generated per estate.
- **Provenance columns** (`FileDate_DW`, `FileName_DW`, `FileRowDate_DW`, `FileSize_DW`, `DataSet_DW`,
  `FileLineNumber_DW`). These describe the LOAD, not the reading. `FileLineNumber_DW` in particular is only
  comparable when both estates read the same physical file format; a JSON-era V3 line number never
  corresponds to a legacy CSV line number. Including a provenance column in the key makes unrelated rows look
  missing (see the `provenance-key-multiplies-rolling-files` memory).
- **Columns carrying a legacy parse artifact.** The killer case: a time-only source string `TRY_PARSE`d as
  `DATETIME` gets the PARSE date, not the data date. Old prod's `Klokkeslett` therefore holds a date that
  varies by when the load ran, while V3 anchors it deterministically. The stable key is
  `CAST(Klokkeslett AS time)`, not `Klokkeslett`. Test for it: if a column is in the key and old prod has far
  more duplicates than V3, look at whether its date/precision part moves.
- **Columns whose landing convention differs.** V3 lands an empty CSV cell as `NULL` where legacy landed `''`
  (`empty-string-null-breaks-ported-predicates` memory). Such a column is unusable as a key without
  `NULLIF(x,'')` normalization on both sides.

**Validate the key before trusting it.** On EACH side:

```sql
SELECT COUNT(*) AS physical, COUNT(<keycol>) AS non_null, COUNT(DISTINCT <key tuple>) AS distinct_keys
FROM <table>;
```

- `non_null` < `physical` on a key column: NULL keys. They never match anything, so they inflate both
  anti-join directions and they were silently INSERTED rather than merged (`null-key-rows-are-inserted-not-dropped`).
  Fix the key or the flow before continuing.
- `distinct_keys` far below `physical` on BOTH sides in the same ratio: the key is too coarse, add a column.
- `distinct_keys` == `physical` on one side but not the other: that is the real finding, not a key problem.
  Carry on; Phase 5 classifies it.

---

## Phase 3 - Decompose the difference (never a single number)

The tool's section 2 prints:

```
                            OLD            NEW
physical rows           331,479        280,962
distinct log. keys      260,487        274,914
duplicate rows           70,992          6,048
```

The identity that must hold:

```
(new_physical - old_physical) = (new_dupes - old_dupes) + (new_distinct - old_distinct)
```

Read it as two independent facts:

- **Duplicate difference** = a defect on whichever side has more. Never a reason to copy rows.
- **Distinct-key difference** = the only number that describes actual coverage.

A table can be short on physical rows while being a strict SUPERSET of old prod's data. That is the normal
outcome of a good conversion, because V3 collapses legacy duplicate bloat.

Then read the **per-period breakdown** (section 3), which localises the divergence in time and usually names
the cause on sight:

- **Contiguous run of divergent periods** -> an era boundary, a producer outage, or a retention horizon.
- **Scattered single periods** -> a load defect.
- **Divergence only at the TAIL** -> a parallel run: both estates are still writing.
- **One period wildly off, siblings fine** -> a request-shape or filter defect in that one flow.

---

## Phase 4 - Anti-join BOTH directions, then check VALUES

"Sometimes we have more rows and sometimes less" is exactly why both directions are mandatory. The tool's
section 4 reports `old_keys_missing_from_new` and `new_keys_missing_from_old`.

Then section 5 checks **values on the keys both sides share**, NULL-safely (via `EXISTS (SELECT o.cols EXCEPT
SELECT n.cols)`), with a per-column breakdown. This step is not optional: a column can match on name, type and
position and still be 100% wrong or 100% NULL (`column-parity-misses-null-values` memory). Matching keys is
not matching data.

When duplicates exist the tool collapses each side to one representative row per key first, otherwise the
join is many-to-many and inflates both the compared and mismatched counts.

**A value mismatch outranks every count finding.** It means a TRANSFORM diverged, and transferring rows will
not fix it, it will just move wrong data. Stop and fix the view.

---

## Phase 5 - Classify every non-zero bucket BEFORE transferring anything

Each cause has a different correct action. Do not skip to the transfer.

### A. Old prod duplicate bloat (old has more physical rows, same distinct keys)

Legacy re-loaded a file and the merge key differed by a parse artifact, so it INSERTed instead of updating.
Tell: old dupes >> new dupes, and the duplicate pairs are identical except for a date/precision part.
**Action: none.** Do NOT reproduce it. Document that `COUNT(*)` is not comparable for this table.

### B. V3 duplicate multiplication (new has more physical rows, same distinct keys)

A file-provenance column in the merge key over rolling-window exports makes row count track FILES LOADED
rather than readings (`provenance-key-multiplies-rolling-files`). Tell: new row count scales with load runs.
**Action: fix the key and rebuild.** This is a V3 defect. Never document it away.

### C. Parallel run (divergence only at the tail, both directions non-zero)

Both estates still fire against the same live source at different minutes, so each records its own captures
under disjoint timestamps. Tell: a steady per-day count of unmatched keys starting at the cutover, and
disjoint minute-of-hour histograms:

```sql
SELECT DATEPART(minute, <tsCol>) mi, COUNT(*) FROM <table> WHERE <dateCol> >= '<cutover>' GROUP BY DATEPART(minute, <tsCol>);
```

**Action: a judgement call, and the user's to make.** Either wait for legacy decommissioning, or transfer the
legacy captures so V3 is a strict superset. If transferring, say plainly that sample density doubles for the
overlap window (two independent observations per hour instead of one), which changes per-period COUNTs for
anyone reading a time series. See `parallel-run-schedule-after-legacy`.

### D. A backfill script that stopped short of an era boundary

Tell: `old_keys_missing_from_new` is non-zero and confined to periods after a date that appears as a literal
in the source folder's `*_backfill_linked.sql` or in an archive flow's `source.filter`.
**Action: extend the window in the EXISTING script and re-run it.** Do not add a second script; that violates
the Single Code Path Principle. This was the stavanger_parkering case.

### E. Empty-string vs NULL landing difference

V3 lands empty CSV cells as `NULL` where legacy landed `''`. A ported `WHERE x IS NOT NULL` that was a no-op
in old prod now silently drops rows (`empty-string-null-breaks-ported-predicates`; cost 875k rows on
`Bysykkel_Fact_Trips`). Tell: the per-column mismatch table shows a column where mismatches are ALL
`new_null_old_not`, or a whole period is short with no other explanation.
**Action: fix the view predicate with `NULLIF`/`ISNULL`, then re-run the ods.** Not a transfer.

### F. Source retention / snapshot-only endpoint

The API cannot serve the older period at all, or returns only current state.
**Action: not a transfer from old arc.** Import the unreachable history as a deactivated STATIC ARCHIVE
(`convert-sqlflow-source` Phase 5.3) and document the horizon. See `static-archive-full-coverage` and
`snapshot-endpoint-history-trap`.

### G. Watermark defects (new is short, and the ods reported SUCCESS)

Several distinct traps, all reporting success while skipping rows:
- `UpdatedDate_DW` was NULL on insert in V3, freezing ported watermarks (`updateddate-dw-null-on-insert`).
- A numeric/id watermark skips late-committing rows; the fix is `incremental.lookback`
  (`numeric-watermark-lookback`).
- An arc backfill is invisible to a watermarked downstream `03_ing`, which reports `0 inserted`
  (`arc-backfill-invisible-to-watermarked-edw`). Re-run `--full` or windowed.
**Action: fix the watermark and re-run the flow.** Do not transfer rows past a broken watermark; it will just
happen again.

### H. Value divergence on shared keys

**The most serious finding.** A transform differs between estates. Settle it against the LANDED data
(`OldPreConStr` for old, `dw-pre-prod` for new), not against arc, because arc is downstream of the view that
is probably at fault. Known instance: `billettapp-orderdate-utc-drift` (V3 stores Oslo local where legacy
stored UTC). **Action: fix the view. Never transfer.**

### I. New has more, and it is legitimate

V3 commonly recovers readings legacy never loaded (a runbook that failed silently, an overwritten day file,
a filter that dropped rows). Tell: `new_keys_missing_from_old` clusters in periods where old prod is visibly
thin, and the extra rows have plausible values and file provenance.
**Action: keep, and PROVE it.** Trace a sample back to a real source file before claiming recovery; do not
assume a positive delta is a win (`ids-from-discovers-only-current-fleet` is the cautionary case).

---

## Phase 6 - Converge (dry run, then transfer)

Only for buckets classified as C, D, or a genuine old-only remainder. Never for B, E, G, or H.

### 6.1 Dry run first, always

**Never copy old -> new on a count gap.** Run the exact anti-join the INSERT will use and confirm the number
matches what Phase 4 predicted (`cross-estate-backfill-dry-run`). A dry run that disagrees with the diagnosis
means the key is wrong; go back to Phase 2.

### 6.2 The transfer template

Extend the source folder's existing `<source>_backfill_linked.sql` if there is one; otherwise create it
there. Re-runnable, guarded by `NOT EXISTS` on the LOGICAL key, no truncate:

```sql
INSERT INTO [arc].[<Table>] (<explicit column list, no identity PK>)
SELECT <columns, with any normalization applied>
FROM (
    SELECT o.*,
           ROW_NUMBER() OVER (PARTITION BY <logical key on the old side>
                              ORDER BY o.[FileDate_DW] DESC, o.[<PK>] DESC) AS rn
    FROM OLDPROD.[dw-dwh-prod].[arc].[<Table>] o
) s
WHERE s.rn = 1
  AND NOT EXISTS (SELECT 1 FROM [arc].[<Table>] n
                  WHERE <logical key equality, normalized on both sides>);
```

Points that matter:

- **`ROW_NUMBER() ... WHERE rn = 1` collapses old prod's duplicate bloat** so cause A is not imported.
- **Normalize on the way in** so the target keeps ONE convention (e.g.
  `CAST(s.Dato AS datetime) + CAST(CAST(s.Klokkeslett AS time) AS datetime)` re-anchors the legacy parse-date
  artifact). Never import a value you just classified as an artifact.
- **Let the surrogate PK regenerate.** Carry it across under `SET IDENTITY_INSERT` ONLY when something
  downstream holds that key. `IDENTITY_INSERT` is session-scoped, so a windowed loop must run on ONE
  connection.
- **Copy provenance and audit columns verbatim**, preserving the legacy audit trail.
- **Window on the PK (2-4M rows) with the `NOT EXISTS` per window** for large tables, so an interrupted run
  resumes. Create the clustered PK before the load, the nonclustered merge-key index after.
- Merges beyond ~1M rows: `batchUpsert: true` if going through the engine, or window the raw INSERT.

### 6.3 Prove convergence

Re-run the tool. `missing in new` must be 0, and value parity must still be clean. The counts it prints are
what goes into the documentation.

---

## Phase 7 - Document, then ship

A reconciliation nobody can find gets re-litigated. Write the finding where the feed is defined.

1. **The source's acquisition YAML** (`<source>_00_api.yaml` or the equivalent) carries a
   `GAP RECONCILIATION` comment block. Add the finding there: what was measured, the cause from the Phase 5
   taxonomy, what was transferred, the before/after numbers, and any consequence for consumers.
2. **Mark superseded text.** These blocks are chronological, so a reader going top-down hits the OLD story
   first. When a new finding contradicts an earlier paragraph, mark that paragraph superseded and point at
   the current one. Keep the old diagnosis if it still explains the mechanism.
3. **State plainly if physical counts still differ** and why `COUNT(*)` is not the comparison for this table.
   That sentence prevents the next person re-opening the same question.
4. **Commit and push** (the push IS the catalog registration):

```bash
cd /c/Projects/V3Upgrade/dwh-pipelines-prod
git add -A <ReadableName>/
git commit -m "<what was reconciled and what changed>"
TOK=$(grep -oE 'SQLFLOW_GIT_TOKEN=.*' /c/Projects/SQLFlowV3/.sqlflow/env | cut -d= -f2- | tr -d '\r')
GIT_TERMINAL_PROMPT=0 git -c credential.helper= push \
  "https://x-bitbucket-api-token-auth:${TOK}@bitbucket.org/kolumbuscode/dwh-pipelines-prod.git" main
```

5. If the finding changes how the source should be understood, update the source's memory file.

---

## Doing many tables

Reconcile ONE table end to end before starting the next, the same discipline `convert-sqlflow-source` applies
to sources. Batching produces a pile of half-classified numbers and lands nothing.

A practical order when a whole source is in question:

1. Run the tool over each table with a cheap key first (`-SampleRows 0`), and record only the verdict block.
2. Sort by severity, not by size: **value mismatches first**, then `missing in new`, then duplicate
   asymmetry, then `missing in old`.
3. Work the list one table at a time through Phases 5-7.

Tables in one source usually share a cause. Once a cause is identified on the first table, check for it
directly on the siblings rather than re-deriving it.

---

## Definition of done

- The logical key is established and validated (no NULL keys, no provenance columns, artifacts normalized).
- Schema parity is known, and any shape difference is either fixed or covered by a compat view.
- Both anti-join directions are measured, and EVERY non-zero bucket is classified against the Phase 5
  taxonomy. No bucket is left as "probably fine".
- Value parity is clean on shared keys, or the divergence is fixed at the view.
- Anything transferable is transferred via a dry-run-first, re-runnable script in the source folder, and the
  tool re-run proves `missing in new = 0`.
- Anything NOT transferable is documented with its cause and horizon.
- The finding is in the acquisition YAML's coverage note, superseded text is marked, and the change is pushed
  to Bitbucket.
