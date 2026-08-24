# Generates a faithful V3 "ods" (relational ingestion) *.flow.yaml from old SQLFlow metadata.
# Reads one flw.Ingestion row (the pre.v_* view -> arc/ods table merge) and emits the stage-2 flow:
# keyed upsert, incremental watermark, surrogate identity PK, and audit system columns - matching prod.
#
# Usage: pwsh Generate-OdsFlow.ps1 -FlowId 546 -OutDir ../Baatbooking
param(
    [Parameter(Mandatory)] [int]    $FlowId,
    [Parameter(Mandatory)] [string] $OutDir,
    # Legacy metadata connection; defaults come from the OldSQlFlowConStr environment variable (see below).
    [string] $Server,
    [string] $MetaDb,
    [string] $User,
    [string] $Password,
    # Overrides the flow name (and file name), which otherwise derives from the TARGET table.
    # Required when several source flows merge into one shared arc table, as APC does: five
    # operators all feed [arc].[APC_Calls], so the derived name would collide for all of them.
    [string] $FlowName,
    # Overrides batch, which otherwise carries the legacy batch code verbatim.
    [string] $Batch,
    # Emits schedule membership so the flow joins the source's single schedule.
    [string] $Schedule
)

$ErrorActionPreference = 'Stop'

# Legacy metadata connection. The live old SQLFlow control DB is reached through the User-scoped
# OldSQlFlowConStr environment variable (it is not inherited by the shell, so read it explicitly); the
# 92.221.59.28 restore is only the fallback for when that variable is unset, because it lags the live estate.
# Parameters passed on the command line always win.
$legacyMeta = @{ Server = '92.221.59.28'; Database = 'dw-sqlflow-prod-last'; User = 'SQLFlow'; Password = 'fhin352' }
$legacyConStr = [Environment]::GetEnvironmentVariable('OldSQlFlowConStr', 'User')
if ($legacyConStr) {
    # psbase is required: the builder implements IDictionary, so a plain property assignment would be routed
    # to the indexer and store the whole connection string under a key named 'ConnectionString' instead.
    $csb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $csb.psbase.ConnectionString = $legacyConStr
    $legacyMeta = @{
        Server   = $csb.psbase.DataSource
        Database = $csb.psbase.InitialCatalog
        User     = $csb.psbase.UserID
        Password = $csb.psbase.Password
    }
}
if (-not $Server)   { $Server   = $legacyMeta.Server }
if (-not $MetaDb)   { $MetaDb   = $legacyMeta.Database }
if (-not $User)     { $User     = $legacyMeta.User }
if (-not $Password) { $Password = $legacyMeta.Password }
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

# In legacy SQLFlow the ingestion source was itself a table carrying audit stamps (UpdatedDate_DW /
# InsertedDate_DW), and the flow used one of those as its incremental watermark. In the V3 two-stage design
# the ods source is the typed pre view, which does NOT carry those target-side audit columns; it carries the
# file-date provenance stamp (FileDate_DW). The engine requires every incremental column to exist in the source
# (IncrementalWindowResolver rejects an unknown watermark column), so remap an audit watermark to FileDate_DW,
# the pre view's actual per-file high-water column. Non-audit (business) incremental columns pass through.
$auditWatermarks = @('UpdatedDate_DW', 'InsertedDate_DW')
if ($incCols) {
    $incCols = @($incCols | ForEach-Object { if ($auditWatermarks -contains $_) { 'FileDate_DW' } else { $_ } } | Select-Object -Unique)
}

# Target table name (bare) drives the file name and flow name.
$table = ([regex]::Matches($trgObj, '\[([^\]]+)\]') | ForEach-Object { $_.Groups[1].Value })[-1]
if (-not $table) { $table = ($trgObj -split '\.')[-1].Trim('[',']') }

$srcConn, $srcDb = ConnEnv $srcObj
$trgConn, $trgDb = ConnEnv $trgObj

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("# Generated from old SQLFlow metadata (flw.Ingestion FlowID $FlowId) by Generate-OdsFlow.ps1.")
[void]$sb.AppendLine("# Stage 2 of 2 (ods): keyed-merges the typed pre view into the ODS/arc table.")
[void]$sb.AppendLine("flowType: ing")
$flowNameOut = if ($FlowName) { $FlowName } else { "$($table.ToLower())_02_ing" }
$batchOut    = if ($Batch)    { $Batch }    else { $batch }
[void]$sb.AppendLine("name: $flowNameOut")
[void]$sb.AppendLine("batch: $batchOut")
if ($Schedule) { [void]$sb.AppendLine("schedule: $Schedule") }
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

$outFile = Join-Path $OutDir ("{0}.yaml" -f $flowNameOut)
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$sb.ToString() | Set-Content -Path $outFile -Encoding utf8 -NoNewline
Write-Host "wrote $outFile (keys: $($keyCols.Count), incremental: $($incCols.Count), identity: $(if($identity){$identity}else{'none'}))"
