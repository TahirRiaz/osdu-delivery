<#
.SYNOPSIS
    Copies APC raw data from the OLD data lake to the NEW one, Azure to Azure.

.DESCRIPTION
    This is a SERVER SIDE copy. azcopy is invoked with --from-to=BlobBlob, so the storage service
    transfers each blob directly from the source account to the destination account using the
    Copy Blob / Put Block From URL APIs. This machine only issues REST calls and tracks progress:
    no APC file content is ever downloaded here.

    Both accounts are ADLS Gen2 (hierarchical namespace) in westeurope, so the transfer stays
    inside the region: no egress charge, and it runs at service speed rather than link speed.

    Credentials are short lived USER DELEGATION SAS tokens minted from the caller's existing
    az login. No account key is read, written, or stored, and the tokens expire on their own.
    They are held in memory only and are never written to the log.

    Source      : dwdatalakestorev2prod  (the retiring lake, READ ONLY here)
    Destination : dwdatalakeprodv2       (the new lake)

    The destination path mirrors the source exactly, so raw/apc/<operator>/history/<dataset>/
    lands where the V3 pipelines already expect to read it.

.PARAMETER Path
    Path under the filesystem to copy, defaulting to the whole APC tree. Narrow it to test, e.g.
    'raw/apc/ryfylken/history/Company'.

.PARAMETER DryRun
    Enumerate and report what would be copied, transferring nothing.

.PARAMETER HoursValid
    Lifetime of the generated SAS tokens. A user delegation SAS caps out at 7 days.

.EXAMPLE
    .\Copy-ApcLake.ps1 -Path 'raw/apc/ryfylken/history/Company' -DryRun
    .\Copy-ApcLake.ps1
#>
[CmdletBinding()]
param(
    [string] $Path       = 'raw/apc',
    [switch] $DryRun,
    [int]    $HoursValid = 72,
    [string] $FileSystem = 'datalakev2',
    [string] $SourceAccount = 'dwdatalakestorev2prod',
    [string] $TargetAccount = 'dwdatalakeprodv2',
    [string] $AzCopy     = 'C:\Tools\azcopy\azcopy_windows_amd64_10.32.4\azcopy.exe'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $AzCopy)) { throw "azcopy not found at $AzCopy" }

function Write-Log {
    param([string] $Message)
    Write-Host ("[{0}] {1}" -f (Get-Date).ToString('HH:mm:ss'), $Message)
}

$expiry = (Get-Date).ToUniversalTime().AddHours($HoursValid).ToString('yyyy-MM-ddTHH:mm:ssZ')

Write-Log "Minting user delegation SAS valid until $expiry (UTC)."

# Source needs read+list only. This lake is being retired and must not be modified.
$srcSas = az storage container generate-sas `
    --account-name $SourceAccount --name $FileSystem `
    --permissions rl --expiry $expiry --auth-mode login --as-user -o tsv
if ($LASTEXITCODE -ne 0 -or -not $srcSas) { throw "Failed to mint source SAS for $SourceAccount." }

# Destination needs to create and write blobs and directories.
$dstSas = az storage container generate-sas `
    --account-name $TargetAccount --name $FileSystem `
    --permissions racwl --expiry $expiry --auth-mode login --as-user -o tsv
if ($LASTEXITCODE -ne 0 -or -not $dstSas) { throw "Failed to mint destination SAS for $TargetAccount." }

Write-Log 'SAS acquired for both accounts (read+list on source, write on destination).'

# azcopy recreates the final segment of the source path under the destination, so the
# destination is the PARENT of the path being copied.
$parent = ($Path -replace '/[^/]+/?$', '')
$leaf   = ($Path -split '/')[-1]

$srcUrl = "https://$SourceAccount.blob.core.windows.net/$FileSystem/$Path`?$srcSas"
$dstUrl = "https://$TargetAccount.blob.core.windows.net/$FileSystem/$parent`?$dstSas"

Write-Log "Source      : $SourceAccount/$FileSystem/$Path"
Write-Log "Destination : $TargetAccount/$FileSystem/$parent/$leaf"
Write-Log 'Transfer    : server side (--from-to=BlobBlob), nothing downloaded locally.'

$azArgs = @(
    'copy', $srcUrl, $dstUrl,
    '--from-to=BlobBlob',
    '--recursive',
    '--overwrite=ifSourceNewer',   # re-runnable: already copied blobs are skipped
    '--log-level=INFO',
    '--output-level=default'
)

if ($DryRun) {
    $azArgs += '--dry-run'
    Write-Log 'DRY RUN: enumerating only, no data will be transferred.'
}

$env:AZCOPY_LOG_LOCATION  = 'C:\Tools\azcopy\logs'
$env:AZCOPY_JOB_PLAN_LOCATION = 'C:\Tools\azcopy\plans'
New-Item -ItemType Directory -Force -Path $env:AZCOPY_LOG_LOCATION, $env:AZCOPY_JOB_PLAN_LOCATION | Out-Null

Write-Log 'Starting azcopy...'
& $AzCopy @azArgs
$code = $LASTEXITCODE

Write-Log "azcopy exit code $code."
Write-Log 'Resume a interrupted job with:  azcopy jobs resume <job-id>'
exit $code
