<#
.SYNOPSIS
    Runs a T-SQL file or statement against the NEW production DWH and streams server messages.

.DESCRIPTION
    The APC migration is driven entirely server side: this script only issues EXEC calls and
    prints what the server says. No row of APC data is ever streamed into PowerShell.

    Batches are split on a lone GO. PRINT and RAISERROR (..., 0, 1) WITH NOWAIT messages are
    surfaced as they arrive, so a multi hour mig.usp_ApcCopyRun reports per chunk progress live.

.PARAMETER File
    Path to a .sql file to execute.

.PARAMETER Query
    A statement to execute instead of a file.

.PARAMETER Database
    Target database on dw-mi-sql-prod. Defaults to dw-dwh-prod.

.PARAMETER TimeoutSeconds
    Per batch command timeout. 0 means no limit, which is what a long copy run needs.
#>
[CmdletBinding(DefaultParameterSetName = 'File')]
param(
    [Parameter(Mandatory, ParameterSetName = 'File')]
    [string] $File,

    [Parameter(Mandatory, ParameterSetName = 'Query')]
    [string] $Query,

    [string] $Database = 'dw-dwh-prod',

    [int] $TimeoutSeconds = 0
)

$ErrorActionPreference = 'Stop'

$envFile = 'C:\Projects\SQLFlowV3\.sqlflow\env'
if (-not (Test-Path $envFile)) { throw "Cannot find $envFile" }

$line = Select-String -Path $envFile -Pattern '^SQLFLOW_CONN_DWDWHPROD=' | Select-Object -First 1
if (-not $line) { throw "SQLFLOW_CONN_DWDWHPROD not found in $envFile" }

$connStr = ($line.Line -replace '^SQLFLOW_CONN_DWDWHPROD=', '') -replace 'Database=dw-dwh-prod', "Database=$Database"

if ($PSCmdlet.ParameterSetName -eq 'File') {
    if (-not (Test-Path $File)) { throw "Cannot find $File" }
    $text = Get-Content -Path $File -Raw
} else {
    $text = $Query
}

# Split on a line that is only GO (optionally followed by a repeat count we do not support).
$batches = [regex]::Split($text, '(?im)^[\t ]*GO[\t ]*(?:--.*)?$') |
           Where-Object { $_.Trim().Length -gt 0 }

$conn = New-Object System.Data.SqlClient.SqlConnection $connStr
$handler = [System.Data.SqlClient.SqlInfoMessageEventHandler] {
    param($sender, $e)
    foreach ($err in $e.Errors) {
        $stamp = (Get-Date).ToString('HH:mm:ss')
        Write-Host "[$stamp] $($err.Message)"
    }
}
$conn.add_InfoMessage($handler)
$conn.FireInfoMessageEventOnUserErrors = $false
$conn.Open()

try {
    $n = 0
    foreach ($batch in $batches) {
        $n++
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $batch
        $cmd.CommandTimeout = $TimeoutSeconds

        $reader = $cmd.ExecuteReader()
        do {
            if ($reader.FieldCount -gt 0) {
                $cols = @(0..($reader.FieldCount - 1) | ForEach-Object { $reader.GetName($_) })
                $rows = @()
                while ($reader.Read()) {
                    $o = [ordered]@{}
                    for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                        $v = $reader.GetValue($i)
                        $o[$cols[$i]] = if ($v -is [DBNull]) { $null } else { $v }
                    }
                    $rows += [pscustomobject]$o
                }
                if ($rows.Count -gt 0) { $rows | Format-Table -AutoSize | Out-String -Width 400 | Write-Host }
            }
        } while ($reader.NextResult())
        $reader.Close()
    }
    Write-Host "OK: $n batch(es) executed against $Database."
}
finally {
    $conn.Close()
    $conn.Dispose()
}
