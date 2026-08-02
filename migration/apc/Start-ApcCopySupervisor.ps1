<#
.SYNOPSIS
    Drives the APC arc-table copy to completion without babysitting.

.DESCRIPTION
    Keeps a fixed number of mig.usp_ApcCopyRun workers alive until the chunk queue drains,
    replacing any worker that exits (its time cap, an idle timeout, or a dropped connection).
    When the queue is empty it rebuilds the indexes and then reconciles against OLDPROD.

    The workers do all the data movement server side; this script only starts sessions and
    reports counts. No APC row is ever streamed into PowerShell.

.PARAMETER Workers
    Concurrent copy sessions to keep alive.

.PARAMETER PollSeconds
    How often to check the queue and top the worker pool back up.

.PARAMETER SkipIndexes
    Copy only, leaving the tables as heaps.

.PARAMETER SkipVerify
    Do not run the reconciliation pass at the end.
#>
[CmdletBinding()]
param(
    [int]    $Workers     = 12,
    [int]    $PollSeconds = 30,
    [switch] $SkipIndexes,
    [switch] $SkipVerify
)

$ErrorActionPreference = 'Stop'

$envFile = 'C:\Projects\SQLFlowV3\.sqlflow\env'
$line = Select-String -Path $envFile -Pattern '^SQLFLOW_CONN_DWDWHPROD=' | Select-Object -First 1
if (-not $line) { throw "SQLFLOW_CONN_DWDWHPROD not found in $envFile" }
$connStr = ($line.Line -replace '^SQLFLOW_CONN_DWDWHPROD=', '')

function Write-Log {
    param([string] $Message)
    Write-Host ("[{0}] {1}" -f (Get-Date).ToString('HH:mm:ss'), $Message)
}

function Invoke-Scalar {
    param([string] $Sql, [int] $Timeout = 300)
    $c = New-Object System.Data.SqlClient.SqlConnection $connStr
    try {
        $c.Open()
        $cmd = $c.CreateCommand()
        $cmd.CommandText = $Sql
        $cmd.CommandTimeout = $Timeout
        return $cmd.ExecuteScalar()
    } finally { $c.Close(); $c.Dispose() }
}

# One worker session. Runs until its own exit condition, then returns.
$workerBlock = {
    param($ConnStr, $WorkerId)
    $c = New-Object System.Data.SqlClient.SqlConnection $ConnStr
    $c.Open()
    try {
        $cmd = $c.CreateCommand()
        # One writer per table. Two workers inserting into the same heap with TABLOCK deadlock
        # against each other on this instance (observed on APC_PassengerCount chunk 23 and
        # APC_CallDetails_Dalane chunk 21, both losing all three attempts after long runs). The
        # lost work far outweighs the tail parallelism that a second writer would buy, so tables
        # are single writer and MaxAttempts is raised to absorb any other transient failure.
        $cmd.CommandText = 'EXEC mig.usp_ApcCopyRun @WorkerId = @w, @MaxMinutes = 180, @MaxWorkersPerTable = 1, @MaxAttempts = 6, @IdleExitSeconds = 90;'
        $cmd.Parameters.AddWithValue('@w', $WorkerId) | Out-Null
        $cmd.CommandTimeout = 0
        $cmd.ExecuteNonQuery() | Out-Null
    } finally { $c.Close(); $c.Dispose() }
}

$queueSql = @"
SELECT COUNT(*) FROM mig.ApcCopyChunk c
JOIN mig.ApcCopyTable t ON t.TableName = c.TableName
WHERE t.Status = 'Ready'
  AND (c.Status IN ('Pending','Running') OR (c.Status = 'Failed' AND c.Attempts < 3));
"@

Write-Log "Supervisor starting with $Workers workers."

$jobs = @{}
$lastReport = [DateTime]::MinValue

try {
    while ($true) {
        $remaining = [int](Invoke-Scalar $queueSql)
        if ($remaining -eq 0) {
            Write-Log 'Chunk queue is empty: copy phase complete.'
            break
        }

        # Reclaim chunks orphaned by a session that died. A chunk takes minutes at most, so one
        # still Running after an hour has no live owner. This is bounded by age precisely so it
        # can never steal a chunk from a worker that is genuinely mid-copy.
        $orphans = [int](Invoke-Scalar @"
UPDATE mig.ApcCopyChunk
SET Status = 'Pending',
    ErrorMessage = N'Reclaimed: Running with no live owner for over 60 minutes.'
WHERE Status = 'Running' AND StartedUtc < DATEADD(MINUTE, -60, SYSUTCDATETIME());
SELECT @@ROWCOUNT;
"@)
        if ($orphans -gt 0) { Write-Log "Reclaimed $orphans orphaned chunk(s)." }

        # Reap finished workers.
        foreach ($id in @($jobs.Keys)) {
            $j = $jobs[$id]
            if ($j.State -ne 'Running') {
                Receive-Job -Job $j -ErrorAction SilentlyContinue | Out-Null
                Remove-Job  -Job $j -Force -ErrorAction SilentlyContinue
                $jobs.Remove($id)
            }
        }

        # Size the pool from what is actually copying right now, not just from this script's own
        # jobs, so the supervisor coexists with workers started outside it and converges on
        # $Workers concurrent streams as those drain away.
        $activeInDb = [int](Invoke-Scalar "SELECT COUNT(*) FROM mig.ApcCopyChunk WHERE Status='Running';")
        $toStart = [Math]::Min($Workers - $activeInDb, $Workers - $jobs.Count)

        if ($toStart -gt 0) {
            $started = 0
            for ($id = 1; $id -le $Workers -and $started -lt $toStart; $id++) {
                if (-not $jobs.ContainsKey($id)) {
                    $jobs[$id] = Start-Job -ScriptBlock $workerBlock -ArgumentList $connStr, (100 + $id)
                    Write-Log "Started worker $(100 + $id) (active streams $activeInDb of $Workers)."
                    $started++
                }
            }
        }

        if (((Get-Date) - $lastReport).TotalMinutes -ge 5) {
            $stat = Invoke-Scalar @"
SELECT CONCAT(
  FORMAT(SUM(RowsCopied), 'N0'), ' / ', FORMAT(SUM(RemoteRows), 'N0'), ' rows (',
  CAST(SUM(RowsCopied)*100.0/NULLIF(SUM(RemoteRows),0) AS decimal(5,2)), '%), tables done ',
  SUM(CASE WHEN Status='Done' THEN 1 ELSE 0 END), '/', COUNT(*))
FROM mig.vw_ApcCopyProgress;
"@
            $rate = Invoke-Scalar @"
SELECT ISNULL(CAST(SUM(RowsCopied)/300.0 AS int),0) FROM mig.ApcCopyChunk
WHERE Status='Done' AND CompletedUtc >= DATEADD(MINUTE,-5,SYSUTCDATETIME());
"@
            $failed = Invoke-Scalar "SELECT COUNT(*) FROM mig.ApcCopyChunk WHERE Status='Failed';"
            Write-Log "$stat | ${rate} rows/s | chunks left $remaining | failed $failed"
            $lastReport = Get-Date
        }

        Start-Sleep -Seconds $PollSeconds
    }
}
finally {
    foreach ($id in @($jobs.Keys)) {
        $j = $jobs[$id]
        if ($j.State -eq 'Running') { Wait-Job -Job $j -Timeout 300 | Out-Null }
        Receive-Job -Job $j -ErrorAction SilentlyContinue | Out-Null
        Remove-Job  -Job $j -Force -ErrorAction SilentlyContinue
    }
}

$failedCount = [int](Invoke-Scalar "SELECT COUNT(*) FROM mig.ApcCopyChunk WHERE Status='Failed';")
Write-Log "Copy phase finished. Failed chunks: $failedCount"

if ($failedCount -gt 0) {
    Write-Log 'Failed chunks remain, so indexes and verification are skipped. Inspect mig.ApcCopyChunk.'
    return
}

if (-not $SkipIndexes) {
    Write-Log 'Building indexes from the OLDPROD definitions. This is the long tail of the migration.'
    & "$PSScriptRoot\Invoke-ApcSql.ps1" -Query "EXEC mig.usp_ApcCopyIndexes @TablePattern = N'APC%', @LoadedOnly = 1;" -TimeoutSeconds 0
    Write-Log 'Index build complete.'
}

if (-not $SkipVerify) {
    Write-Log 'Reconciling row counts against OLDPROD.'
    & "$PSScriptRoot\Invoke-ApcSql.ps1" -Query "EXEC mig.usp_ApcCopyVerify @TablePattern = N'APC%', @DeepVerify = 0;" -TimeoutSeconds 0
}

Write-Log 'Supervisor done.'
