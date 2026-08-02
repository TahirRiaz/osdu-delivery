<#
.SYNOPSIS
    Writes the five APC acquisition (copy) flows, one per region.

.DESCRIPTION
    Each flow copies its region's eight LIVE dataset folders from the old lake to the new one, landing at the
    identical path so the region's stage-1 pre flows read exactly where the copy writes and lineage binds the
    copy (wave 0) ahead of the pre loads.

    Only the eight live datasets are copied. The other eight per operator are deactivated in the legacy
    metadata and their arc tables showed zero drift during the bulk migration, so copying them would keep a
    retired feed alive.
#>
[CmdletBinding()]
param(
    [string] $OutDir = 'C:\Projects\V3Upgrade\dwh-pipelines-prod\apc',
    [int]    $ModifiedWithinDays = 7
)

$ErrorActionPreference = 'Stop'

# Region -> its folder in the lake. Dalane/Jaeren is the one whose lake folder does not match its flow prefix.
$regions = [ordered]@{
    dalane      = 'dalanejaeren'
    haugalandet = 'haugalandet'
    norgesbuss  = 'norgesbuss'
    ryfylken    = 'ryfylken'
    ryfylkes    = 'ryfylkes'
}

# The eight datasets still fed by the producer, in the lake's casing.
$datasets = @(
    'AssignedBlocks', 'Calls', 'Line', 'PassengerCount',
    'PassengerInOut', 'PlannedBlocks', 'PlannedJourneys', 'StopPoint'
)

$oldLake = 'abfss://datalakev2@dwdatalakestorev2prod.dfs.core.windows.net'
$newLake = 'abfss://datalakev2@dwdatalakeprodv2.dfs.core.windows.net'

foreach ($region in $regions.Keys) {
    $folder = $regions[$region]
    $name   = "apc_${region}_00_cpy"

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("# Acquisition (data acquisition) for the APC $region region: brings its eight live dataset folders")
    [void]$sb.AppendLine("# into the new lake at the SAME paths the region's stage-1 pre flows read, so lineage binds this copy")
    [void]$sb.AppendLine("# (wave 0) ahead of the pre loads (wave 1) and the ods merges (wave 2) in one fire.")
    [void]$sb.AppendLine('#')
    [void]$sb.AppendLine('# INTERIM BRIDGE, NOT THE PERMANENT ACQUISITION. Copying from the old lake makes V3 depend on the legacy')
    [void]$sb.AppendLine('# producer still running and on a storage account that is being retired: the day APC_Runner is switched')
    [void]$sb.AppendLine('# off, this copy silently goes dry and the source goes stale without failing. It exists only because the')
    [void]$sb.AppendLine('# producer (Azure Function APC_Runner in dw-function-prod) still writes its CSV exports to')
    [void]$sb.AppendLine('# dwdatalakestorev2prod. THE FIX IS TO REPOINT APC_Runner AT dwdatalakeprodv2, after which these five copy')
    [void]$sb.AppendLine('# flows should be deleted, not kept. They are the bridge across the cutover, nothing more.')
    [void]$sb.AppendLine('#')
    [void]$sb.AppendLine("# Only the eight LIVE datasets are copied. The other eight this operator once produced (CallDetails,")
    [void]$sb.AppendLine('# Company, Destination, DestinationDisplay, InvalidatedJourneys, JourneyType, PassengerCount_PB,')
    [void]$sb.AppendLine('# PassengerInOut_Category) are deactivated in the legacy metadata, and their arc tables showed zero drift')
    [void]$sb.AppendLine('# through the bulk table migration, which is independent proof the feed stopped. Copying them would')
    [void]$sb.AppendLine('# resurrect a feed the business retired.')
    [void]$sb.AppendLine('#')
    [void]$sb.AppendLine("# The one-time history (540,778 files / 308 GB across all regions) was already moved server-to-server with")
    [void]$sb.AppendLine('# azcopy, so this flow only has to keep up with the daily delta; modifiedWithinDays bounds each run to the')
    [void]$sb.AppendLine('# recent window and makes it self-healing if the producer re-touches a file.')
    [void]$sb.AppendLine('flowType: cpy')
    [void]$sb.AppendLine("name: $name")
    [void]$sb.AppendLine('batch: copy')
    [void]$sb.AppendLine('operation: copy')
    [void]$sb.AppendLine("# Joins this region's schedule, so the copy is wave 0 of the region's own chain link.")
    [void]$sb.AppendLine("schedule: apc_${region}_daily")
    [void]$sb.AppendLine('options:')
    [void]$sb.AppendLine('  overwrite: true')
    [void]$sb.AppendLine('  preserveStructure: true')
    [void]$sb.AppendLine('items:')

    foreach ($ds in $datasets) {
        $src = "$oldLake/raw/apc/$folder/history/$ds"
        $tgt = "$newLake/raw/apc/$folder/history/$ds"
        [void]$sb.AppendLine("  - source: { location: $src, pattern: `"*.csv`", recursive: true, modifiedWithinDays: $ModifiedWithinDays }")
        [void]$sb.AppendLine("    target: { location: $tgt }")
    }

    $out = Join-Path $OutDir "$name.yaml"
    $sb.ToString() | Set-Content -Path $out -Encoding utf8 -NoNewline
    Write-Host "wrote $out ($($datasets.Count) items)"
}
