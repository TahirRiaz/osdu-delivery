#requires -Version 5.1
<#
.SYNOPSIS
    SQLFlow V3 prod-v2 container deploy: build images in ACR (fast + parallel),
    point the container apps at the new tag, wait for the new control-plane
    revision, show its startup log.

.DESCRIPTION
    The image tag is the current commit's short SHA (what the apps actually run).
    Keep this script at the repo root.

    Builds from a `git archive` of TRACKED files only, extracted into .deploy-ctx.
    This matters: `az acr build .` uploads the whole working tree and does NOT honor
    .dockerignore on the client side, so it ships gigabytes of bin/obj/target/
    node_modules every build (2.9 GiB here, ~20 min upload x N apps). The archive
    context is tracked files only (tens of MB), and the three builds run in parallel,
    turning an hour into a few minutes.

    NOT handled here: the pipeline YAML in the separate dwh-pipelines-prod repo.
    When flow YAML changes, push it after this finishes:
      $tok = az keyvault secret show --vault-name sqlflow-v3-secrets --name bitbucket-git-token --query value -o tsv
      cd C:\Projects\dwh-pipelines-prod
      $env:GIT_TERMINAL_PROMPT = '0'
      git push "https://x-bitbucket-api-token-auth:$tok@bitbucket.org/kolumbuscode/dwh-pipelines-prod.git" main

.EXAMPLE
    .\deploy-prod.ps1
    Build + deploy control-plane, worker, and gui.

.EXAMPLE
    .\deploy-prod.ps1 control-plane worker
    Only those apps (e.g. an engine-only change). Known: control-plane worker gui mcp slack-bot
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Apps
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$Rg  = 'datawarehouse-west-rg-prod-v2'
$Acr = 'sqlflowv3acrprod'
$Sub = '83731164-2cea-4291-b78d-7e2e69eea8a6'

# app -> Dockerfile (as named at the repo root). 'gui' is special-cased below because
# its build context is the gui/ subtree, where the Dockerfile sits at the context root.
$Config = [ordered]@{
    'control-plane' = @{ Dockerfile = 'Dockerfile'          }
    'worker'        = @{ Dockerfile = 'Dockerfile.worker'   }
    'gui'           = @{ Dockerfile = 'gui/Dockerfile'      }
    'mcp'           = @{ Dockerfile = 'Dockerfile.mcp'      }
    'slack-bot'     = @{ Dockerfile = 'Dockerfile.slackbot' }
}

if (-not $Apps -or $Apps.Count -eq 0) {
    $Apps = @('control-plane', 'worker', 'gui')
}
foreach ($a in $Apps) {
    if (-not $Config.Contains($a)) {
        throw "Unknown app '$a'. Known: $($Config.Keys -join ', ')"
    }
}

# `az acr build` prints a check-mark glyph the cp1252 console cannot encode and then
# dies with a UnicodeEncodeError AFTER the server build already succeeded. Forcing
# UTF-8 keeps its output and exit code honest. The server-side verify is still the truth.
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'

# Image tag = current commit short SHA. The archive is of HEAD, so uncommitted work is
# excluded on purpose (the tag names a commit).
$Tag = (git rev-parse --short HEAD).Trim()
if (-not $Tag) { throw 'Could not read git HEAD. Run this from the SQLFlow V3 repo.' }

if (git status --porcelain -- src gui) {
    Write-Warning "Uncommitted changes in src/ or gui/ - image $Tag is built from the committed tree and will NOT include them."
}

Write-Host ''
Write-Host '=== SQLFlow V3 deploy ===' -ForegroundColor Cyan
Write-Host "   tag:  $Tag"
Write-Host "   apps: $($Apps -join ', ')"
Write-Host "   rg:   $Rg"
Write-Host ''

az account set --subscription $Sub
if ($LASTEXITCODE -ne 0) { throw 'az account set failed.' }

function Test-Image {
    param([string] $App)
    $status = az acr task list-runs -r $Acr --top 25 -o tsv `
        --query "[?outputImages[0].repository=='sqlflow-v3-$App' && outputImages[0].tag=='$Tag'] | [0].status"
    if ($status) { $status = $status.Trim() }
    if ($status -eq 'Succeeded') {
        Write-Host "   sqlflow-v3-${App}:$Tag   OK" -ForegroundColor Green
        return $true
    }
    Write-Host "   sqlflow-v3-${App}:$Tag   NOT OK (status: $status)" -ForegroundColor Red
    return $false
}

function Deploy-App {
    param([string] $App)
    Write-Host "--- sqlflow-v3-$App -> :$Tag ---"
    az containerapp update -n "sqlflow-v3-$App" -g $Rg `
        --image "$Acr.azurecr.io/sqlflow-v3-${App}:$Tag" `
        --query 'properties.template.containers[0].image' -o tsv
    if ($LASTEXITCODE -ne 0) { throw "Deploy of sqlflow-v3-$App failed." }
}

function Wait-Running {
    param([string] $App)
    Write-Host ''
    Write-Host "=== Waiting for $App new revision to reach Running ==="
    for ($i = 0; $i -lt 60; $i++) {
        $state = az containerapp revision list -n $App -g $Rg `
            --query '[?properties.active] | [0].properties.runningState' -o tsv
        if ($state) { $state = $state.Trim() }
        if ($state -eq 'Running') { Write-Host "   ${App}: Running" -ForegroundColor Green; return }
        Start-Sleep -Seconds 10
    }
    Write-Warning "$App did not reach Running in time; check the portal."
}

function Show-Log {
    param([string] $App)
    Write-Host ''
    Write-Host "=== $App startup log (bootstrap / sync / warnings / errors) ==="
    # Only issues timestamped AFTER 'Bootstrap provisioning completed.' are real; the scheduler
    # races the migrations at startup and prints a harmless early burst.
    az containerapp logs show -n $App -g $Rg --tail 300 --type console |
        Select-String -SimpleMatch -Pattern 'Bootstrap', 'Synced', 'warn', 'error', 'exception'
}

# --- Build a clean, minimal context from tracked files only -------------------------
$CtxRoot = Join-Path $PSScriptRoot '.deploy-ctx'
$CtxRepo = Join-Path $CtxRoot 'repo'   # whole-repo context (backend Dockerfiles)
$CtxGui  = Join-Path $CtxRoot 'gui'    # gui subtree context (gui/Dockerfile at its root)
Remove-Item -Recurse -Force $CtxRoot -ErrorAction SilentlyContinue

$needRepo = @($Apps | Where-Object { $_ -ne 'gui' }).Count -gt 0
$needGui  = $Apps -contains 'gui'

Write-Host 'Preparing clean build context (tracked files only)...'
if ($needRepo) {
    New-Item -ItemType Directory -Force -Path $CtxRepo | Out-Null
    # Write the archive to a file, then extract; do NOT pipe git|tar (PowerShell mangles binary streams).
    git archive --format=tar.gz -o (Join-Path $CtxRoot 'repo.tar.gz') HEAD
    if ($LASTEXITCODE -ne 0) { throw 'git archive (repo) failed.' }
    tar -xzf (Join-Path $CtxRoot 'repo.tar.gz') -C $CtxRepo
}
if ($needGui) {
    New-Item -ItemType Directory -Force -Path $CtxGui | Out-Null
    git archive --format=tar.gz -o (Join-Path $CtxRoot 'gui.tar.gz') 'HEAD:gui'
    if ($LASTEXITCODE -ne 0) { throw 'git archive (gui) failed.' }
    tar -xzf (Join-Path $CtxRoot 'gui.tar.gz') -C $CtxGui
}

# --- Build every requested app in parallel ------------------------------------------
Write-Host "Building $($Apps.Count) image(s) in parallel at ${Tag}..." -ForegroundColor Cyan
$jobs = foreach ($a in $Apps) {
    if ($a -eq 'gui') { $df = 'Dockerfile'; $ctx = $CtxGui }
    else              { $df = $Config[$a].Dockerfile; $ctx = $CtxRepo }
    Start-Job -Name $a -ScriptBlock {
        param($Acr, $App, $Tag, $Df, $Ctx)
        $env:PYTHONUTF8 = '1'; $env:PYTHONIOENCODING = 'utf-8'
        az acr build --registry $Acr --image "sqlflow-v3-${App}:$Tag" --file $Df $Ctx
        "exit=$LASTEXITCODE"
    } -ArgumentList $Acr, $a, $Tag, $df, $ctx
}
$jobs | Wait-Job | Out-Null
foreach ($j in $jobs) {
    $tail = (Receive-Job $j) | Select-Object -Last 1
    Write-Host "   build $($j.Name): $($j.State) ($tail)"
    Remove-Job $j
}

# --- Verify server-side that each tag landed; this gates the deploy ------------------
Write-Host ''
Write-Host "=== Verifying images on $Acr ===" -ForegroundColor Cyan
$allOk = $true
foreach ($a in $Apps) { if (-not (Test-Image $a)) { $allOk = $false } }
if (-not $allOk) { throw 'One or more builds did not succeed - not deploying. See above.' }

# --- Deploy -------------------------------------------------------------------------
Write-Host ''
Write-Host '=== Deploying container apps ===' -ForegroundColor Cyan
foreach ($a in $Apps) { Deploy-App $a }

# --- Wait for the control-plane revision, then show its startup log ------------------
if ($Apps -contains 'control-plane') {
    Wait-Running 'sqlflow-v3-control-plane'
    Show-Log 'sqlflow-v3-control-plane'
}

Write-Host ''
Write-Host "=== Done. Deployed tag $Tag to: $($Apps -join ', ') ===" -ForegroundColor Green
