<#
.SYNOPSIS
    Generates the APC stage-1 (pre) and stage-2 (ods) flows into the pipelines repo.

.DESCRIPTION
    APC is the widest source in the estate: five operators (Dalane/Jaeren, Haugalandet,
    Norgesbuss, Ryfylke N, Ryfylke S), each landing the same eight datasets, all merging into
    SHARED arc tables discriminated by SourceSystemID. That shape drives two decisions here:

      * The ods flow name MUST carry the operator. Generate-OdsFlow derives its name from the
        TARGET table, and five operators share [arc].[APC_Calls], so without -FlowName all five
        would write to one file and overwrite each other.
      * batch is the per-object grouping label '<operator>_<dataset>', so a dataset's pre+ods
        pair share one batch, matching the estate convention.

    ONLY THE LIVE DATASETS ARE GENERATED. Of the 16 datasets per operator, 8 are deactivated in
    the legacy metadata (CallDetails, Company, Destination, DestinationDisplay,
    InvalidatedJourneys, JourneyType, PassengerCount_PB, PassengerInOut_Category). That was
    confirmed independently: during the bulk table migration only the 8 live datasets' arc tables
    drifted, and every deactivated dataset's table reconciled to zero change. Shipping flows for
    a dead dataset would resurrect a feed the business retired.

    NO SCHEDULE IS EMITTED. The legacy APC batch still runs daily against the old lake, so
    scheduling these now would double-feed arc. Wiring the schedule is a cutover step, together
    with repointing the APC_Runner producer at the new lake.

.PARAMETER OutDir
    Target folder in the pipelines repo.

.PARAMETER WhatIf
    List what would be generated without writing anything.
#>
[CmdletBinding()]
param(
    [string] $OutDir = 'C:\Projects\V3Upgrade\dwh-pipelines-prod\apc',
    [string] $StorageUrlBase = 'https://dwdatalakeprodv2.dfs.core.windows.net/datalakev2',
    [string] $Server = '92.221.59.28',
    [string] $MetaDb = 'dw-sqlflow-prod-last',
    [string] $User   = 'SQLFlow',
    [string] $Pass   = 'fhin352',
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'
$tools = 'C:\Projects\SQLFlowV3\migration\_tools'

function Invoke-Meta([string] $sql) {
    $c = New-Object System.Data.SqlClient.SqlConnection `
        "Server=$Server;Database=$MetaDb;User Id=$User;Password=$Pass;Encrypt=True;TrustServerCertificate=True;Connect Timeout=60"
    $c.Open()
    try {
        $cmd = $c.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 180
        $r = $cmd.ExecuteReader()
        $rows = @()
        while ($r.Read()) {
            $o = [ordered]@{}
            for ($i = 0; $i -lt $r.FieldCount; $i++) { $o[$r.GetName($i)] = $r.GetValue($i) }
            $rows += [pscustomobject]$o
        }
        return $rows
    } finally { $c.Close(); $c.Dispose() }
}

# Live pre flows. The pre table is APC_<Operator>_<Dataset>, which is already unique, so the
# generator's derived name is correct and only batch needs overriding.
$pre = Invoke-Meta @"
SELECT FlowID, Batch, trgDBSchTbl
FROM flw.PreIngestionCSV
WHERE Batch LIKE 'APC%' AND Batch NOT LIKE 'APCEDW%'
  AND (DeactivateFromBatch = 0 OR DeactivateFromBatch IS NULL)
ORDER BY Batch, FlowID;
"@

# Live ods flows. Sourced from v_APC_<Operator>_<Dataset>, which is what makes the operator
# recoverable; the target table alone would not distinguish them.
$ods = Invoke-Meta @"
SELECT FlowID, Batch, srcDBSchTbl, trgDBSchTbl
FROM flw.Ingestion
WHERE Batch LIKE 'APC%' AND Batch NOT LIKE 'APCEDW%'
  AND Batch <> 'APC_Matched_Trip_Backup'
  AND Batch <> 'APCCallDetails'
  AND (DeactivateFromBatch = 0 OR DeactivateFromBatch IS NULL)
ORDER BY Batch, FlowID;
"@

function Get-ObjectKey([string] $raw) {
    # '[dw-pre-prod].[pre].[APC_Dalane_Calls]' or 'v_APC_Dalane_Calls' -> 'dalane_calls'
    $leaf = ($raw -split '\.')[-1].Trim('[', ']')
    $leaf = $leaf -replace '^v_', ''
    $leaf = $leaf -replace '^APC_', ''
    return $leaf.ToLower()
}

Write-Host "APC flow generation -> $OutDir"
Write-Host ("pre flows: {0}   ods flows: {1}" -f $pre.Count, $ods.Count)

if (-not $WhatIf -and -not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}

# One schedule PER REGION, mirroring the legacy design: the ADF pipeline SQLFlow_APC chained the five
# operators serially (Dalane -> Haugalandet -> Norgesbuss -> RyfylkeN -> RyfylkeS via dependsOn). Separate
# schedules keep that separation while letting an operator be re-run alone, without touching the other four
# regions' data. Definitions and the stagger live in schedules.yaml.
function Get-Region([string] $key) { ($key -split '_')[0] }

$made = 0
foreach ($p in $pre) {
    $key    = Get-ObjectKey $p.trgDBSchTbl         # dalane_assignedblocks
    $region = Get-Region $key                      # dalane
    $name   = "apc_${key}_01_csv"
    $sched  = "apc_${region}_daily"
    if ($WhatIf) { Write-Host "  [pre] FlowID $($p.FlowID) -> $name (batch $key, schedule $sched)"; continue }
    & "$tools\Generate-PreFlow.ps1" -FlowId $p.FlowID -OutDir $OutDir `
        -StorageUrlBase $StorageUrlBase -FlowName $name -Batch $key -Schedule $sched | Out-Null
    Write-Host "  [pre] $name  [$sched]"
    $made++
}

foreach ($o in $ods) {
    $key    = Get-ObjectKey $o.srcDBSchTbl         # dalane_assignedblocks (from the v_ view)
    $region = Get-Region $key
    $name   = "apc_${key}_02_ing"
    $sched  = "apc_${region}_daily"
    if ($WhatIf) { Write-Host "  [ods] FlowID $($o.FlowID) -> $name (batch $key, schedule $sched)"; continue }
    & "$tools\Generate-OdsFlow.ps1" -FlowId $o.FlowID -OutDir $OutDir `
        -FlowName $name -Batch $key -Schedule $sched | Out-Null
    Write-Host "  [ods] $name  [$sched]"
    $made++
}

Write-Host ("Done. {0} flow file(s) {1}." -f $made, $(if ($WhatIf) { 'previewed' } else { 'written' }))
