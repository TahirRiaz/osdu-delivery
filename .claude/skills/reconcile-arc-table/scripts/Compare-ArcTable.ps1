<#
.SYNOPSIS
Read-only diagnostic diff of one arc table between OLD production and the NEW V3 estate.

.DESCRIPTION
Answers "why do these two tables disagree" without ever treating COUNT(*) as the verdict. Runs, in order:
schema parity, a count decomposition (physical rows vs distinct logical readings vs duplicates), an optional
per-period breakdown, a BIDIRECTIONAL anti-join on the logical key, and a NULL-safe value-parity check across
the keys both estates share.

READ ONLY. It never writes to either estate. Use it to decide what (if anything) to transfer; the transfer
itself is a separate reviewed INSERT (skill Phase 6).

Both sides are staged into temp tables on the NEW server, the old side over the OLDPROD linked server. On a
large table bound the run with -Where and compare period aggregates before drilling in.

.PARAMETER Table
Target table on the NEW estate, schema-qualified, e.g. arc.stavanger_parkering.

.PARAMETER OldTable
Table on OLD prod when the name differs (e.g. arc.Bysykkel_Trips vs arc.Citybike_Trips). Defaults to -Table.

.PARAMETER Key
The LOGICAL key: comma-separated SQL expressions identifying one real-world reading on BOTH estates, e.g.
"Dato,Sted,CAST(Klokkeslett AS time)". Establish this before running (skill Phase 2); a wrong key invalidates
every number below it.

.PARAMETER DateColumn
Column to group the per-period breakdown by. Omit to skip that section.

.PARAMETER Period
'month' (default) or 'day'.

.PARAMETER CompareColumns
Columns for value parity. Default: every column on the new table except the identity PK, anything matching
-ExcludePattern, and any bare column named in -Key.

.PARAMETER ExcludePattern
LIKE pattern for columns excluded from value parity by default. Default '%\_DW' (provenance/audit columns
legitimately differ between estates: FileDate_DW is a copy instant, InsertedDate_DW is the load time).

.PARAMETER Where
Predicate applied to BOTH sides, without the WHERE keyword, e.g. "Dato >= '2025-01-01'".

.PARAMETER LinkedServer
Linked server name for old prod. Default OLDPROD.

.PARAMETER OldDatabase
Database on the linked server. Default 'dw-dwh-prod'.

.PARAMETER SampleRows
Example rows printed per finding. Default 5; 0 suppresses examples.

.EXAMPLE
./Compare-ArcTable.ps1 -Table arc.stavanger_parkering -Key "Dato,Sted,CAST(Klokkeslett AS time)" -DateColumn Dato
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Table,
    [string]$OldTable,
    [Parameter(Mandatory)][string]$Key,
    [string]$DateColumn,
    [ValidateSet('month','day')][string]$Period = 'month',
    [string[]]$CompareColumns,
    [string]$ExcludePattern = '%\_DW',
    [string]$Where,
    [string]$LinkedServer = 'OLDPROD',
    [string]$OldDatabase = 'dw-dwh-prod',
    [int]$SampleRows = 5
)

$ErrorActionPreference = 'Stop'
if (-not $OldTable) { $OldTable = $Table }

# powershell.exe -File does not split a comma-separated argument into an array, it hands over one string.
# Re-split so both "-CompareColumns a,b,c" and "-CompareColumns a","b" behave the same.
if ($CompareColumns) {
    $CompareColumns = @($CompareColumns | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Split-Name([string]$qualified) {
    $parts = $qualified -split '\.'
    if ($parts.Count -ne 2) { throw "Table '$qualified' must be schema-qualified, e.g. arc.MyTable." }
    @{ Schema = $parts[0].Trim('[',']'); Name = $parts[1].Trim('[',']') }
}
$new = Split-Name $Table
$old = Split-Name $OldTable

function Get-ConnString([string]$which) {
    if ($which -eq 'new') {
        $envFile = 'c:\Projects\SQLFlowV3\.sqlflow\env'
        if (-not (Test-Path $envFile)) { throw "Not found: $envFile (holds SQLFLOW_CONN_DWDWHPROD)." }
        $line = Get-Content $envFile | Where-Object { $_ -like 'SQLFLOW_CONN_DWDWHPROD=*' } | Select-Object -First 1
        if (-not $line) { throw "SQLFLOW_CONN_DWDWHPROD not found in $envFile." }
        return ($line -replace '^SQLFLOW_CONN_DWDWHPROD=','').Trim()
    }
    # NOT inherited by the shell: $env:OldDwhConStr is empty, it must be read from the User scope.
    $cs = [Environment]::GetEnvironmentVariable('OldDwhConStr','User')
    if (-not $cs) { throw 'User environment variable OldDwhConStr is not set.' }
    return $cs
}

function Invoke-Sql([string]$which, [string]$sql) {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    # psbase is required: SqlConnectionStringBuilder implements IDictionary, so a plain assignment is routed
    # to the indexer and stores the whole string under a key literally named 'ConnectionString'.
    $b.psbase.ConnectionString = Get-ConnString $which
    $conn = New-Object System.Data.SqlClient.SqlConnection $b.psbase.ConnectionString
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $sql
        $cmd.CommandTimeout = 0
        $ds = New-Object System.Data.DataSet
        [void](New-Object System.Data.SqlClient.SqlDataAdapter $cmd).Fill($ds)
        return $ds.Tables
    } finally { $conn.Close() }
}

function Write-Section([string]$title) {
    Write-Host ''
    Write-Host ('=' * 100)
    Write-Host $title
    Write-Host ('=' * 100)
}

# ==========================================================================================================
# 1. SCHEMA PARITY - decides whether a straight INSERT ... SELECT transfer is even legal.
# ==========================================================================================================
Write-Section "1. SCHEMA PARITY   $OldTable (old)  vs  $Table (new)"

$schemaSql = @'
SELECT c.column_id, c.name,
       t.name + CASE
         WHEN t.name IN ('varchar','char','varbinary','binary') THEN '(' + IIF(c.max_length=-1,'max',CAST(c.max_length AS varchar(10))) + ')'
         WHEN t.name IN ('nvarchar','nchar') THEN '(' + IIF(c.max_length=-1,'max',CAST(c.max_length/2 AS varchar(10))) + ')'
         WHEN t.name IN ('decimal','numeric') THEN '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
         ELSE '' END AS type_full,
       c.is_nullable, c.is_identity
FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('{0}') ORDER BY c.column_id;
'@

$newCols = (Invoke-Sql new ($schemaSql -f $Table))[0]
$oldCols = (Invoke-Sql old ($schemaSql -f $OldTable))[0]
if ($newCols.Rows.Count -eq 0) { throw "Table $Table does not exist on the new estate." }
if ($oldCols.Rows.Count -eq 0) { throw "Table $OldTable does not exist on old prod." }

function Format-Col($r) {
    if (-not $r) { return '<absent>' }
    '{0} {1} {2}' -f $r.name, $r.type_full, $(if ($r.is_nullable) { 'NULL' } else { 'NOT NULL' })
}
$schemaDiffs = @()
for ($i = 0; $i -lt [Math]::Max($newCols.Rows.Count, $oldCols.Rows.Count); $i++) {
    $n = if ($i -lt $newCols.Rows.Count) { $newCols.Rows[$i] } else { $null }
    $o = if ($i -lt $oldCols.Rows.Count) { $oldCols.Rows[$i] } else { $null }
    if ((Format-Col $n) -ne (Format-Col $o)) {
        $schemaDiffs += [pscustomobject]@{ Position = $i + 1; Old = (Format-Col $o); New = (Format-Col $n) }
    }
}

Write-Host ('old columns: {0}    new columns: {1}' -f $oldCols.Rows.Count, $newCols.Rows.Count)
if ($schemaDiffs.Count -eq 0) {
    Write-Host 'IDENTICAL - same columns, same order, same types, same nullability.'
    Write-Host '=> A direct INSERT ... SELECT transfer old -> new is structurally valid.'
} else {
    Write-Host "DIFFERENT in $($schemaDiffs.Count) position(s):"
    $schemaDiffs | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host '=> Do NOT blind-transfer. Map columns explicitly, and check whether the new shape needs a'
    Write-Host '   compat view under the old name (convert-sqlflow-source skill, Phase 6.2).'
}
$identityCol = ($newCols.Rows | Where-Object { $_.is_identity } | Select-Object -First 1).name

# ==========================================================================================================
# Resolve key expressions and value-parity columns.
# ==========================================================================================================
# Split on commas that are not inside parentheses, so CAST(x AS time) survives.
$keyExprs = @()
$depth = 0
$buf = ''
foreach ($ch in $Key.ToCharArray()) {
    if ($ch -eq '(') { $depth++ }
    if ($ch -eq ')') { $depth-- }
    if ($ch -eq ',' -and $depth -eq 0) { $keyExprs += $buf.Trim(); $buf = '' } else { $buf += $ch }
}
if ($buf.Trim()) { $keyExprs += $buf.Trim() }
$keyExprs = @($keyExprs | Where-Object { $_ })
if ($keyExprs.Count -eq 0) { throw '-Key produced no expressions.' }
$keyAlias = @(0..($keyExprs.Count - 1) | ForEach-Object { "k$_" })

if (-not $CompareColumns) {
    $bareKeys = @($keyExprs | Where-Object { $_ -match '^\[?\w+\]?$' } | ForEach-Object { $_.Trim('[',']') })
    $excludeSql = "SELECT c.name FROM sys.columns c WHERE c.object_id=OBJECT_ID('$Table') AND c.name LIKE '$ExcludePattern' ESCAPE '\';"
    $excluded = @((Invoke-Sql new $excludeSql)[0].Rows | ForEach-Object { $_.name })
    $CompareColumns = @($newCols.Rows | ForEach-Object { $_.name } | Where-Object {
        $_ -ne $identityCol -and $excluded -notcontains $_ -and $bareKeys -notcontains $_ })
}
$CompareColumns = @($CompareColumns)

# The per-period breakdown needs its date column staged on both sides.
$stagedCols = @($CompareColumns)
$dateIsKey = @($keyExprs | Where-Object { $_.Trim('[',']') -eq $DateColumn }).Count -gt 0
if ($DateColumn -and -not $dateIsKey -and $stagedCols -notcontains $DateColumn) { $stagedCols += $DateColumn }

$whereClause = if ($Where) { "WHERE $Where" } else { '' }

Write-Host ''
Write-Host "logical key      : $($keyExprs -join ' | ')"
Write-Host "value-parity cols: $(if ($CompareColumns.Count) { $CompareColumns -join ', ' } else { '<none>' })"
if ($identityCol) { Write-Host "identity column  : $identityCol (excluded from value parity)" }
if ($Where)       { Write-Host "filter (both)    : $Where" }

# ==========================================================================================================
# Stage both sides symmetrically: key expressions aliased k0..kN, compare columns by name.
# ==========================================================================================================
$proj = @()
for ($i = 0; $i -lt $keyExprs.Count; $i++) { $proj += "$($keyExprs[$i]) AS $($keyAlias[$i])" }
foreach ($c in $stagedCols) { $proj += "[$c]" }
$projList = $proj -join ', '
$aliasList = $keyAlias -join ','

$stage = @"
SET NOCOUNT ON;
IF OBJECT_ID('tempdb..#old') IS NOT NULL DROP TABLE #old;
IF OBJECT_ID('tempdb..#new') IS NOT NULL DROP TABLE #new;
SELECT * INTO #old FROM OPENQUERY($LinkedServer,
  'SELECT $projList FROM [$OldDatabase].[$($old.Schema)].[$($old.Name)] $whereClause');
SELECT $projList INTO #new FROM [$($new.Schema)].[$($new.Name)] $whereClause;
CREATE INDEX ix_old_key ON #old($aliasList);
CREATE INDEX ix_new_key ON #new($aliasList);
"@

$keyJoin = (@(0..($keyExprs.Count - 1) | ForEach-Object { "o.$($keyAlias[$_]) = n.$($keyAlias[$_])" })) -join ' AND '

# ==========================================================================================================
# 2. COUNT DECOMPOSITION
# ==========================================================================================================
Write-Section '2. COUNT DECOMPOSITION (physical rows are NOT the comparison)'

$counts = (Invoke-Sql new @"
$stage
SELECT 'old_physical_rows' AS metric, COUNT(*) AS value FROM #old
UNION ALL SELECT 'old_distinct_keys', COUNT(*) FROM (SELECT DISTINCT $aliasList FROM #old) a
UNION ALL SELECT 'new_physical_rows', COUNT(*) FROM #new
UNION ALL SELECT 'new_distinct_keys', COUNT(*) FROM (SELECT DISTINCT $aliasList FROM #new) b;
"@)[0]

$m = @{}
foreach ($r in $counts.Rows) { $m[[string]$r.metric] = [int64]$r.value }
$oldDupes = $m['old_physical_rows'] - $m['old_distinct_keys']
$newDupes = $m['new_physical_rows'] - $m['new_distinct_keys']

Write-Host ('{0,-22} {1,14} {2,14}' -f '', 'OLD', 'NEW')
Write-Host ('{0,-22} {1,14:N0} {2,14:N0}' -f 'physical rows',      $m['old_physical_rows'], $m['new_physical_rows'])
Write-Host ('{0,-22} {1,14:N0} {2,14:N0}' -f 'distinct log. keys', $m['old_distinct_keys'], $m['new_distinct_keys'])
Write-Host ('{0,-22} {1,14:N0} {2,14:N0}' -f 'duplicate rows',     $oldDupes, $newDupes)
Write-Host ''
Write-Host ('physical-row difference (new - old) : {0:N0}' -f ($m['new_physical_rows'] - $m['old_physical_rows']))
Write-Host ('logical-key difference  (new - old) : {0:N0}   <-- the number that matters' -f ($m['new_distinct_keys'] - $m['old_distinct_keys']))
if ($oldDupes -gt 0 -or $newDupes -gt 0) {
    Write-Host ''
    Write-Host ('Duplicates present: old {0:N0}, new {1:N0}, net {2:N0} of the physical gap.' -f $oldDupes, $newDupes, ($oldDupes - $newDupes))
    Write-Host 'Duplicates on ONE side are a defect on that side, never a reason to copy rows across.'
}

# ==========================================================================================================
# 3. PER-PERIOD BREAKDOWN
# ==========================================================================================================
if ($DateColumn) {
    Write-Section "3. PER-PERIOD BREAKDOWN on $DateColumn (distinct logical keys)"
    $kIdx = -1
    for ($i = 0; $i -lt $keyExprs.Count; $i++) { if ($keyExprs[$i].Trim('[',']') -eq $DateColumn) { $kIdx = $i } }
    $dateRef = if ($kIdx -ge 0) { $keyAlias[$kIdx] } else { "[$DateColumn]" }

    # Legacy landing is string-first, so a date column is frequently varchar. CONVERT with style 126 gives
    # the yyyy-MM-dd prefix directly and, unlike FORMAT, accepts a temporal value without a culture lookup.
    $temporal = @('date','datetime','datetime2','smalldatetime','datetimeoffset','time')
    $dcType = ($newCols.Rows | Where-Object { $_.name -eq $DateColumn } | Select-Object -First 1).type_full
    if (-not $dcType) { throw "-DateColumn '$DateColumn' is not a column of $Table." }
    $dcBase = ($dcType -split '\(')[0]
    $dateVal = if ($temporal -contains $dcBase) { $dateRef } else { "TRY_CONVERT(datetime, $dateRef)" }
    if ($temporal -notcontains $dcBase) {
        Write-Host "note: $DateColumn is $dcType, so it is parsed with TRY_CONVERT; unparseable values group under '(no date)'."
    }
    $len = if ($Period -eq 'day') { 10 } else { 7 }
    # ISNULL matters: an unparseable or NULL date yields NULL, and NULL never equals NULL, so without this
    # the FULL OUTER JOIN below reports one unmatched row per side instead of comparing them.
    $periodExpr = "ISNULL(CONVERT(varchar($len), $dateVal, 126), '(no date)')"
    # When the date column IS one of the key expressions it is already in $aliasList; projecting it again
    # would emit a duplicate column name in the derived table.
    $distinctList = if ($kIdx -ge 0) { $aliasList } else { "$aliasList, $dateRef" }

    $rows = (Invoke-Sql new @"
$stage
WITH o AS (SELECT $periodExpr AS p, COUNT(*) AS c FROM (SELECT DISTINCT $distinctList FROM #old) d GROUP BY $periodExpr),
     n AS (SELECT $periodExpr AS p, COUNT(*) AS c FROM (SELECT DISTINCT $distinctList FROM #new) d GROUP BY $periodExpr)
SELECT ISNULL(o.p,n.p) AS period, ISNULL(o.c,0) AS old_keys, ISNULL(n.c,0) AS new_keys, ISNULL(n.c,0)-ISNULL(o.c,0) AS diff
FROM o FULL OUTER JOIN n ON n.p = o.p ORDER BY 1;
"@)[0]

    $divergent = @($rows.Rows | Where-Object { [int64]$_.diff -ne 0 })
    Write-Host ('periods: {0}   in agreement: {1}   divergent: {2}' -f $rows.Rows.Count, ($rows.Rows.Count - $divergent.Count), $divergent.Count)
    if ($divergent.Count -gt 0) {
        Write-Host ''
        $divergent | Select-Object period, old_keys, new_keys, diff | Format-Table -AutoSize | Out-String | Write-Host
        Write-Host 'CONTIGUOUS run of divergent periods -> an era / outage / retention boundary.'
        Write-Host 'SCATTERED pattern                   -> a load defect.'
        Write-Host 'Run at the TAIL only                -> a parallel run (both estates still writing).'
    }
}

# ==========================================================================================================
# 4. BIDIRECTIONAL ANTI-JOIN - "sometimes more, sometimes less" needs BOTH directions.
# ==========================================================================================================
Write-Section '4. BIDIRECTIONAL ANTI-JOIN on the logical key'

$antiRes = (Invoke-Sql new @"
$stage
SELECT 'old_keys_missing_from_new' AS direction, COUNT(*) AS value FROM (
  SELECT DISTINCT $aliasList FROM #old o
  WHERE NOT EXISTS (SELECT 1 FROM #new n WHERE $keyJoin)) a
UNION ALL
SELECT 'new_keys_missing_from_old', COUNT(*) FROM (
  SELECT DISTINCT $aliasList FROM #new n
  WHERE NOT EXISTS (SELECT 1 FROM #old o WHERE $keyJoin)) b;
"@)[0]

foreach ($r in $antiRes.Rows) { Write-Host ('{0,-28} {1,12:N0}' -f $r.direction, [int64]$r.value) }
$missingFromNew = [int64](($antiRes.Rows | Where-Object { $_.direction -eq 'old_keys_missing_from_new' }).value)
$missingFromOld = [int64](($antiRes.Rows | Where-Object { $_.direction -eq 'new_keys_missing_from_old' }).value)

Write-Host ''
if ($missingFromNew -eq 0 -and $missingFromOld -eq 0) {
    Write-Host 'The two estates hold exactly the same logical keys.'
} else {
    if ($missingFromNew -gt 0) { Write-Host ('-> {0:N0} old-prod readings ABSENT from V3. Classify before transferring (skill Phase 5).' -f $missingFromNew) }
    if ($missingFromOld -gt 0) { Write-Host ('-> {0:N0} V3 readings ABSENT from old prod. Usually rows legacy never loaded - PROVE they are real before keeping.' -f $missingFromOld) }
}

if ($SampleRows -gt 0 -and ($missingFromNew -gt 0 -or $missingFromOld -gt 0)) {
    $ex = (Invoke-Sql new @"
$stage
SELECT TOP $SampleRows 'missing_from_new' AS side, $aliasList FROM #old o
 WHERE NOT EXISTS (SELECT 1 FROM #new n WHERE $keyJoin)
UNION ALL
SELECT TOP $SampleRows 'missing_from_old', $aliasList FROM #new n
 WHERE NOT EXISTS (SELECT 1 FROM #old o WHERE $keyJoin);
"@)[0]
    Write-Host ''
    $ex | Format-Table -AutoSize | Out-String | Write-Host
}

# ==========================================================================================================
# 5. VALUE PARITY - matching keys is NOT matching data.
# ==========================================================================================================
if ($CompareColumns.Count -gt 0) {
    Write-Section '5. VALUE PARITY on shared keys (NULL-safe via EXCEPT)'
    $exceptOld = ($CompareColumns | ForEach-Object { "o.[$_]" }) -join ', '
    $exceptNew = ($CompareColumns | ForEach-Object { "n.[$_]" }) -join ', '

    # Collapse each side to ONE representative row per logical key first. Without this the join is
    # many-to-many across duplicates and reports more compared pairs than either side has keys, which
    # silently inflates the mismatch count.
    $dedup = @"
IF OBJECT_ID('tempdb..#old1') IS NOT NULL DROP TABLE #old1;
IF OBJECT_ID('tempdb..#new1') IS NOT NULL DROP TABLE #new1;
SELECT * INTO #old1 FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY $aliasList ORDER BY (SELECT NULL)) AS _rn FROM #old) x WHERE _rn = 1;
SELECT * INTO #new1 FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY $aliasList ORDER BY (SELECT NULL)) AS _rn FROM #new) x WHERE _rn = 1;
CREATE INDEX ix_old1 ON #old1($aliasList);
CREATE INDEX ix_new1 ON #new1($aliasList);
"@
    if ($oldDupes -gt 0 -or $newDupes -gt 0) {
        Write-Host 'Duplicates present, so each side is collapsed to one representative row per key first.'
        Write-Host ''
    }

    $par = (Invoke-Sql new @"
$stage
$dedup
SELECT 'shared_keys_compared' AS metric, COUNT(*) AS value FROM #old1 o JOIN #new1 n ON $keyJoin
UNION ALL
SELECT 'keys_with_a_value_mismatch', COUNT(*) FROM #old1 o JOIN #new1 n ON $keyJoin
 WHERE EXISTS (SELECT $exceptOld EXCEPT SELECT $exceptNew);
"@)[0]

    foreach ($r in $par.Rows) { Write-Host ('{0,-30} {1,12:N0}' -f $r.metric, [int64]$r.value) }
    $mismatch = [int64](($par.Rows | Where-Object { $_.metric -eq 'keys_with_a_value_mismatch' }).value)

    if ($mismatch -gt 0) {
        Write-Host ''
        Write-Host 'VALUE MISMATCHES ARE THE MOST SERIOUS FINDING. The same reading carries different values on'
        Write-Host 'the two estates, so a TRANSFORM diverged; rows are not missing. Transferring rows will NOT'
        Write-Host 'fix it. Find the per-column culprit below and fix the view/flow.'
        $perCol = foreach ($c in $CompareColumns) {
            "SELECT '$c' AS column_name, COUNT(*) AS mismatches, SUM(CASE WHEN o.[$c] IS NULL AND n.[$c] IS NOT NULL THEN 1 ELSE 0 END) AS old_null_new_not, SUM(CASE WHEN n.[$c] IS NULL AND o.[$c] IS NOT NULL THEN 1 ELSE 0 END) AS new_null_old_not FROM #old1 o JOIN #new1 n ON $keyJoin WHERE EXISTS (SELECT o.[$c] EXCEPT SELECT n.[$c])"
        }
        $colRes = (Invoke-Sql new ("$stage`n$dedup`n" + ($perCol -join "`nUNION ALL`n") + ';'))[0]
        Write-Host ''
        $colRes.Rows | Where-Object { [int64]$_.mismatches -gt 0 } |
            Select-Object column_name, mismatches, old_null_new_not, new_null_old_not |
            Format-Table -AutoSize | Out-String | Write-Host
        Write-Host 'A column whose mismatches are ALL new_null_old_not is the empty-string-vs-NULL landing'
        Write-Host 'difference, not lost data (skill Phase 5, cause E).'
    } else {
        Write-Host ''
        Write-Host 'Every shared key agrees on every compared column.'
    }
}

# ==========================================================================================================
Write-Section 'VERDICT'
if ($schemaDiffs.Count -eq 0) { Write-Host 'schema         : identical (direct transfer legal)' }
else                          { Write-Host "schema         : DIFFERS in $($schemaDiffs.Count) position(s) (map columns explicitly)" }
Write-Host ('missing in new : {0:N0}' -f $missingFromNew)
Write-Host ('missing in old : {0:N0}' -f $missingFromOld)
Write-Host ('duplicates     : old {0:N0}   new {1:N0}' -f $oldDupes, $newDupes)
Write-Host ''
Write-Host 'Next: classify every non-zero bucket against the skill Phase 5 taxonomy BEFORE transferring anything.'
