<#
.SYNOPSIS
    Diffs each generated APC pre-flow view against the real arc table contract.

.DESCRIPTION
    The legacy `flw.PreIngestionTransfrom` metadata that Generate-PreFlow reads is only a LOWER
    BOUND on the columns: over the years columns were added to the production arc tables without
    anyone back-filling the transform rows. A view generated from that metadata therefore
    validates and loads happily while silently being SHORT, which breaks the backward-compatibility
    contract the DWH consumers depend on.

    So every generated view is compared, by name, against `arc.<Table>` in the new production DWH
    (which is now a faithful copy of old production). Expected shape:

        arc columns  =  view columns  +  surrogate identity PK  +  audit column(s)

    Anything else is reported. A MISSING column means the arc table has it but the view does not:
    it must be added to the flow by hand, with the type taken from the arc table.
#>
[CmdletBinding()]
param(
    [string] $FlowDir = 'C:\Projects\V3Upgrade\dwh-pipelines-prod\apc'
)

$ErrorActionPreference = 'Stop'

$envFile = 'C:\Projects\SQLFlowV3\.sqlflow\env'
$line = Select-String -Path $envFile -Pattern '^SQLFLOW_CONN_DWDWHPROD=' | Select-Object -First 1
$connStr = ($line.Line -replace '^SQLFLOW_CONN_DWDWHPROD=', '')

# Audit / surrogate columns the framework appends, which legitimately are not in the view.
$frameworkCols = @('InsertedDate_DW', 'UpdatedDate_DW')

function Get-ArcColumns([string] $table) {
    $c = New-Object System.Data.SqlClient.SqlConnection $connStr
    $c.Open()
    try {
        $cmd = $c.CreateCommand()
        $cmd.CommandText = @"
SELECT c.name, t.name AS typ, c.is_identity
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(@o)
ORDER BY c.column_id;
"@
        $cmd.Parameters.AddWithValue('@o', "arc.$table") | Out-Null
        $r = $cmd.ExecuteReader()
        $cols = @()
        while ($r.Read()) {
            $cols += [pscustomobject]@{ Name = $r[0]; Type = $r[1]; IsIdentity = [bool]$r[2] }
        }
        return $cols
    } finally { $c.Close(); $c.Dispose() }
}

$results = @()

foreach ($f in Get-ChildItem $FlowDir -Filter '*_01_csv.yaml' | Sort-Object Name) {
    $text = Get-Content $f.FullName -Raw

    # Transform column names, in order.
    $viewCols = [regex]::Matches($text, '(?m)^\s*-\s*\{\s*name:\s*([^,}\s]+)') |
                ForEach-Object { $_.Groups[1].Value }

    # The ods flow for the same object tells us which arc table this feeds.
    $odsFile = $f.FullName -replace '_01_csv\.yaml$', '_02_ing.yaml'
    if (-not (Test-Path $odsFile)) { continue }
    $odsText = Get-Content $odsFile -Raw
    if ($odsText -notmatch 'object:\s*"\[dw-dwh-prod\]\.\[arc\]\.\[([^\]]+)\]"') { continue }
    $arcTable = $Matches[1]

    $arcCols = Get-ArcColumns $arcTable
    $arcNames = $arcCols.Name

    # Columns the arc table has that the view does not supply (excluding framework-added ones).
    $missing = $arcNames | Where-Object {
        $_ -notin $viewCols -and $_ -notin $frameworkCols -and
        -not ($arcCols | Where-Object { $_.Name -eq $PSItem -and $_.IsIdentity })
    }
    # Re-filter identity properly (the pipeline var above is awkward inside Where-Object).
    $identityNames = ($arcCols | Where-Object IsIdentity).Name
    $missing = $arcNames | Where-Object {
        $_ -notin $viewCols -and $_ -notin $frameworkCols -and $_ -notin $identityNames
    }

    # Columns the view supplies that the arc table does not have.
    $extra = $viewCols | Where-Object { $_ -notin $arcNames }

    $results += [pscustomobject]@{
        Flow      = $f.BaseName
        ArcTable  = $arcTable
        ViewCols  = $viewCols.Count
        ArcCols   = $arcCols.Count
        Missing   = ($missing -join ', ')
        Extra     = ($extra   -join ', ')
        Status    = if ($missing -or $extra) { 'MISMATCH' } else { 'OK' }
    }
}

$results | Format-Table Flow, ArcTable, ViewCols, ArcCols, Status -AutoSize | Out-String -Width 200 | Write-Host

$bad = $results | Where-Object Status -eq 'MISMATCH'
if ($bad) {
    Write-Host "`n=== MISMATCHES ===" -ForegroundColor Yellow
    foreach ($b in $bad) {
        Write-Host ("{0} -> arc.{1}" -f $b.Flow, $b.ArcTable)
        if ($b.Missing) { Write-Host ("   MISSING from view : {0}" -f $b.Missing) }
        if ($b.Extra)   { Write-Host ("   EXTRA in view     : {0}" -f $b.Extra) }
    }
    Write-Host ("`n{0} of {1} flows mismatch." -f $bad.Count, $results.Count)
} else {
    Write-Host "`nAll $($results.Count) views match their arc contract."
}
