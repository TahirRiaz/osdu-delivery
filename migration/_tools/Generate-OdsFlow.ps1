# Generates a faithful V3 "ods" (relational ingestion) *.flow.yaml from old SQLFlow metadata.
# Reads one flw.Ingestion row (the pre.v_* view -> arc/ods table merge) and emits the stage-2 flow:
# keyed upsert, incremental watermark, surrogate identity PK, and audit system columns - matching prod.
#
# Usage: pwsh Generate-OdsFlow.ps1 -FlowId 546 -OutDir ../Baatbooking
param(
    [Parameter(Mandatory)] [int]    $FlowId,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Server   = 'localhost',
    [string] $MetaDb   = 'dw-sqlflow-prod-last',
    [string] $User     = 'SQLFlow',
    [string] $Password = 'fhin352'
)

$ErrorActionPreference = 'Stop'
$TAB = [char]9

function Sql([string]$q) {
    $out = & sqlcmd -S $Server -d $MetaDb -U $User -P $Password -C -h -1 -s "$TAB" -W -Q "SET NOCOUNT ON; $q" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $out" }
    $out | Where-Object { $_ -and $_ -notmatch '^\(\d+ rows affected\)$' } | ForEach-Object { ,($_ -split $TAB) }
}

# Parses '[A],[B],[C]' (or 'A,B,C') into a clean array of bare names.
function Cols([string]$raw) {
    if ([string]::IsNullOrWhiteSpace($raw)) { return @() }
    ($raw -split ',') | ForEach-Object { $_.Trim().Trim('[',']') } | Where-Object { $_ }
}

# Derives the connection env-var reference from a [db].[schema].[table] object: dw-pre-prod -> ${env:SQLFLOW_CONN_DWPREPROD}.
function ConnEnv([string]$obj) {
    $db = ([regex]::Match($obj, '^\s*\[?([^\].]+)')).Groups[1].Value
    $key = ($db -replace '[^A-Za-z0-9]', '').ToUpper()
    return "`${env:SQLFLOW_CONN_$key}", $db
}

$r = @(Sql "SELECT srcDBSchTbl, trgDBSchTbl, ISNULL(KeyColumns,''), ISNULL(IncrementalColumns,''),
                   ISNULL(NoOfOverlapDays,0), ISNULL(IdentityColumn,''), ISNULL(SysColumns,''),
                   ISNULL(Batch,SysAlias), CAST(ISNULL(TruncateTrg,0) AS int),
                   CAST(ISNULL(SkipUpdateExsisting,0) AS int), CAST(ISNULL(SkipInsertNew,0) AS int),
                   ISNULL(srcFilter,''), ISNULL(IgnoreColumns,'')
            FROM flw.Ingestion WHERE FlowID = $FlowId")
if ($r.Count -eq 0) { throw "No flw.Ingestion row for FlowID $FlowId" }
$f = $r[0]
$srcObj = $f[0]; $trgObj = $f[1]
$keyCols = Cols $f[2]; $incCols = Cols $f[3]
$overlap = [int]$f[4]; $identity = $f[5].Trim('[',']')
$sysCols = ($f[6] -split ',') | ForEach-Object { $_.Trim().ToUpper() }
$batch = $f[7]; $truncate = ($f[8] -eq '1'); $skipUpd = ($f[9] -eq '1'); $skipIns = ($f[10] -eq '1')
$srcFilter = $f[11]; $ignoreCols = Cols $f[12]

# Target table name (bare) drives the file name and flow name.
$table = ([regex]::Matches($trgObj, '\[([^\]]+)\]') | ForEach-Object { $_.Groups[1].Value })[-1]
if (-not $table) { $table = ($trgObj -split '\.')[-1].Trim('[',']') }

$srcConn, $srcDb = ConnEnv $srcObj
$trgConn, $trgDb = ConnEnv $trgObj

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("# Generated from old SQLFlow metadata (flw.Ingestion FlowID $FlowId) by Generate-OdsFlow.ps1.")
[void]$sb.AppendLine("# Stage 2 of 2 (ods): keyed-merges the typed pre view into the ODS/arc table.")
[void]$sb.AppendLine("flowType: ing")
[void]$sb.AppendLine("name: $($table.ToLower())_02_ing")
[void]$sb.AppendLine("batch: $batch")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("connections:")
[void]$sb.AppendLine("  pre: $srcConn")
[void]$sb.AppendLine("  ods: $trgConn")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("source:")
[void]$sb.AppendLine("  server: pre")
[void]$sb.AppendLine("  object: `"$srcObj`"")
if ($ignoreCols.Count -gt 0) { [void]$sb.AppendLine("  ignoreColumns: [$($ignoreCols -join ', ')]") }
if ($srcFilter) { [void]$sb.AppendLine("  filter: `"$($srcFilter -replace '"','""')`"") }
[void]$sb.AppendLine("")
[void]$sb.AppendLine("target:")
[void]$sb.AppendLine("  server: ods")
[void]$sb.AppendLine("  object: `"$trgObj`"")
if ($identity) { [void]$sb.AppendLine("  identityColumn: $identity   # surrogate PK (flw.Ingestion.IdentityColumn)") }
if ($truncate) { [void]$sb.AppendLine("  truncateBeforeLoad: true") }
[void]$sb.AppendLine("")
if ($keyCols.Count -gt 0) {
    [void]$sb.AppendLine("load:")
    [void]$sb.AppendLine("  keyColumns: [$($keyCols -join ', ')]")
    if ($skipUpd) { [void]$sb.AppendLine("  skipUpdateExisting: true") }
    if ($skipIns) { [void]$sb.AppendLine("  skipInsertNew: true") }
    [void]$sb.AppendLine("")
}
if ($incCols.Count -gt 0) {
    [void]$sb.AppendLine("incremental:")
    [void]$sb.AppendLine("  columns: [$($incCols -join ', ')]")
    if ($overlap -gt 0) { [void]$sb.AppendLine("  overlapDays: $overlap") }
    [void]$sb.AppendLine("")
}
[void]$sb.AppendLine("schema:")
[void]$sb.AppendLine("  sync: true")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("systemColumns:")
[void]$sb.AppendLine("  insertedDate: $(( $sysCols -contains 'INSERTEDDATE_DW' ).ToString().ToLower())")
[void]$sb.AppendLine("  updatedDate: $(( $sysCols -contains 'UPDATEDDATE_DW' ).ToString().ToLower())")

$outFile = Join-Path $OutDir ("{0}_02_ing.yaml" -f $table.ToLower())
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$sb.ToString() | Set-Content -Path $outFile -Encoding utf8 -NoNewline
Write-Host "wrote $outFile (keys: $($keyCols.Count), incremental: $($incCols.Count), identity: $(if($identity){$identity}else{'none'}))"
