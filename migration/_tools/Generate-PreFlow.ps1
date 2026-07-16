# Generates a faithful V3 "pre" (CSV pre-ingestion) *.flow.yaml from old SQLFlow metadata.
# Reads flw.PreIngestionCSV + flw.PreIngestionTransfrom for one CSV FlowID and emits the flow,
# including the typed transformation view (transform.generateView) built from the column casts.
#
# Usage: pwsh Generate-PreFlow.ps1 -FlowId 545 -OutDir ../BB -StorageUrlBase "https://dwdatalakestorev2prod.dfs.core.windows.net/datalakev2"
param(
    [Parameter(Mandatory)] [int]    $FlowId,
    [Parameter(Mandatory)] [string] $OutDir,
    [Parameter(Mandatory)] [string] $StorageUrlBase,
    [string] $Server   = 'localhost',
    [string] $MetaDb   = 'dw-sqlflow-prod-last',
    [string] $User     = 'SQLFlow',
    [string] $Password = 'fhin352',
    [string] $TargetConnEnv = '${env:SQLFLOW_CONN_DWPREPROD}'
)

$ErrorActionPreference = 'Stop'
$TAB = [char]9

# Runs a query via sqlcmd and returns rows as arrays of trimmed fields (tab-separated, no header).
function Sql([string]$q) {
    $out = & sqlcmd -S $Server -d $MetaDb -U $User -P $Password -C -h -1 -s "$TAB" -W -Q "SET NOCOUNT ON; $q" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $out" }
    $out | Where-Object { $_ -and $_ -notmatch '^\(\d+ rows affected\)$' } |
        ForEach-Object { ,($_ -split $TAB) }
}

# --- header row (the CSV pre-ingestion settings) ---
$hr = @(Sql "SELECT srcPath, ISNULL(srcFile,''), trgDBSchTbl, ISNULL(ColumnDelimiter,';'),
                 CAST(SearchSubDirectories AS int), ISNULL(srcEncoding,''), ISNULL(Batch,SysAlias)
          FROM flw.PreIngestionCSV WHERE FlowID = $FlowId")
if ($hr.Count -eq 0) { throw "No flw.PreIngestionCSV row for FlowID $FlowId" }
$f = $hr[0]
$h = [pscustomobject]@{
    srcPath              = $f[0]
    srcFile              = $f[1]
    trgDBSchTbl          = $f[2]
    ColumnDelimiter      = $f[3]
    SearchSubDirectories = ($f[4] -eq '1')
    srcEncoding          = $f[5]
    Batch                = $f[6]
}

# trgDBSchTbl is [db].[schema].[table] -> take schema + table (db comes from the connection).
$parts  = ([regex]::Matches($h.trgDBSchTbl, '\[([^\]]+)\]') | ForEach-Object { $_.Groups[1].Value })
$schema = $parts[-2]
$table  = $parts[-1]

# old srcFile is a regex like 'sess(.*?).csv' -> a glob 'sess*.csv'
$glob = ($h.srcFile -replace '\(\.\*\??\)', '*') -replace '\.csv$', '.csv'
$loc  = ($StorageUrlBase.TrimEnd('/')) + '/' + ($h.srcPath.Trim('/')) + '/'
$delim = if ([string]::IsNullOrEmpty($h.ColumnDelimiter)) { ';' } else { $h.ColumnDelimiter }

# --- transform columns (the per-column casts that build the typed view) ---
$rows = @(Sql "SELECT ISNULL(ColName,''), ISNULL(LTRIM(RTRIM(SelectExp)),''), ISNULL(ColAlias,''),
                      CAST(ISNULL(Virtual,0) AS int), CAST(ISNULL(ExcludeColFromView,0) AS int)
             FROM flw.PreIngestionTransfrom WHERE FlowID = $FlowId ORDER BY TransfromID")
$cols = $rows | ForEach-Object {
    [pscustomobject]@{
        ColName          = $_[0]
        SelectExp        = $_[1]
        ColAlias         = $_[2]
        Virtual          = ($_[3] -eq '1')
        ExcludeColFromView = ($_[4] -eq '1')
    }
}

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("# Generated from old SQLFlow metadata (flw.PreIngestionCSV FlowID $FlowId) by Generate-PreFlow.ps1.")
[void]$sb.AppendLine("# Stage 1 of 2 (pre): lands the CSV into [$schema].[$table] and builds the typed view v_$table.")
[void]$sb.AppendLine("name: $($h.Batch)_${table}_pre")
[void]$sb.AppendLine("batch: $($h.Batch)")
[void]$sb.AppendLine("source:")
[void]$sb.AppendLine("  type: csv")
[void]$sb.AppendLine("  location: $loc")
[void]$sb.AppendLine("  options:")
[void]$sb.AppendLine("    srcFile: `"$glob`"")
if ($h.SearchSubDirectories) { [void]$sb.AppendLine("    searchSubDirectories: `"true`"") }
[void]$sb.AppendLine("    delimiter: `"$delim`"")
if ($h.srcEncoding) { [void]$sb.AppendLine("    srcEncoding: $($h.srcEncoding)") }
# Enable exactly the provenance columns the metadata carries. Any V3-default provenance column not in the
# metadata (notably RowNumber_DW, which old SQLFlow never had) is disabled so the pre table matches prod.
$provPresent = @{}
foreach ($c in $cols) { $n = $c.ColName.Trim('[',']'); if ($n -match '_DW$') { $provPresent[$n] = $true } }
$provFlags = [ordered]@{ FileName_DW='includeFileName'; FileDate_DW='includeFileDate'; FileRowDate_DW='includeFileRowDate'; FileSize_DW='includeFileSize'; DataSet_DW='includeDataSet'; RowNumber_DW='includeRowNumber' }
foreach ($col in $provFlags.Keys) {
    if (-not $provPresent.ContainsKey($col)) { [void]$sb.AppendLine("    $($provFlags[$col]): `"false`"") }
}
[void]$sb.AppendLine("target:")
[void]$sb.AppendLine("  connection: $TargetConnEnv")
[void]$sb.AppendLine("  schema: $schema")
[void]$sb.AppendLine("  table: $table")
[void]$sb.AppendLine("schema:")
[void]$sb.AppendLine("  evolve: widen")
[void]$sb.AppendLine("load:")
[void]$sb.AppendLine("  mode: append")
# Incremental by the injected file date: each run probes MAX(FileDate_DW) on the target and reads only files
# newer than that watermark, so a normal re-run picks up just new files instead of reloading the whole history
# (the initial load of an empty target still reads everything, once). FileDate_DW is a yyyyMMddHHmmss string;
# the engine's probe parses its lexicographic MAX as the chronological watermark.
[void]$sb.AppendLine("incremental:")
[void]$sb.AppendLine("  dateColumn: FileDate_DW")
[void]$sb.AppendLine("  overlapDays: 0")
[void]$sb.AppendLine("transform:")
[void]$sb.AppendLine("  generateView: true")
[void]$sb.AppendLine("  columns:")
# Canonical casts for standard SQLFlow provenance columns. Old metadata sometimes leaves these NULL even though
# the prod view casts them (metadata/prod drift); apply the prod-object type so generated objects match prod.
$canonicalProvenance = @{
    'DataSet_DW' = 'CAST(@ColName as numeric(14,0))'
}

foreach ($c in $cols) {
    $name = $c.ColName.Trim('[',']')
    $expr = $c.SelectExp
    if ([string]::IsNullOrWhiteSpace($expr)) {
        if ($canonicalProvenance.ContainsKey($name)) { $expr = $canonicalProvenance[$name] }
        else { continue }   # genuinely uncast column: passthrough string, no view cast
    }
    $expr = $expr -replace '"', '""'
    $line = "    - { name: $name, expr: `"$expr`""
    if ($c.ColAlias -and ($c.ColAlias.Trim('[',']') -ne $name)) { $line += ", as: $($c.ColAlias.Trim('[',']'))" }
    if ($c.ExcludeColFromView) { $line += ", excludeFromView: true" }
    if ($c.Virtual)            { $line += ", virtual: true" }
    $line += " }"
    [void]$sb.AppendLine($line)
}

$outFile = Join-Path $OutDir ("{0}_01_csv.yaml" -f $table.ToLower())
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$sb.ToString() | Set-Content -Path $outFile -Encoding utf8 -NoNewline
Write-Host "wrote $outFile ($($cols.Count) transform column(s))"
