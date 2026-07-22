#requires -Version 5.1
<#
.SYNOPSIS
    SQLFlow V3 prod-v2 container deploy: build images in ACR, point the container
    apps at the new tag, wait for the new control-plane revision, show its startup log.

.DESCRIPTION
    The image tag is the current commit's short SHA (what the apps actually run).
    Keep this script at the repo root: build contexts are resolved relative to it.

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
    Only those apps (e.g. an engine-only change).

.EXAMPLE
    .\deploy-prod.ps1 gui
    GUI only. Known apps: control-plane worker gui mcp slack-bot
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Apps
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Operate from the repo root (this script's directory).
Set-Location -LiteralPath $PSScriptRoot

$Rg  = 'datawarehouse-west-rg-prod-v2'
$Acr = 'sqlflowv3acrprod'
$Sub = '83731164-2cea-4291-b78d-7e2e69eea8a6'

# app -> Dockerfile + build context.
$Config = [ordered]@{
    'control-plane' = @{ Dockerfile = 'Dockerfile';          Context = '.'   }
    'worker'        = @{ Dockerfile = 'Dockerfile.worker';   Context = '.'   }
    'gui'           = @{ Dockerfile = 'gui/Dockerfile';      Context = 'gui' }
    'mcp'           = @{ Dockerfile = 'Dockerfile.mcp';      Context = '.'   }
    'slack-bot'     = @{ Dockerfile = 'Dockerfile.slackbot'; Context = '.'   }
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
# UTF-8 on the CLI's Python keeps its output and exit code honest. The server-side
# verify below is still the source of truth.
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'

# Image tag = current commit short SHA.
$Tag = (git rev-parse --short HEAD).Trim()
if (-not $Tag) { throw 'Could not read git HEAD. Run this from the SQLFlowV3 repo.' }

# The image builds from the committed tree at $Tag; warn on uncommitted source.
if (git status --porcelain -- src gui) {
    Write-Warning "Uncommitted changes in src/ or gui/ - image $Tag will NOT include them."
}

Write-Host ''
Write-Host '=== SQLFlow V3 deploy ===' -ForegroundColor Cyan
Write-Host "   tag:  $Tag"
Write-Host "   apps: $($Apps -join ', ')"
Write-Host "   rg:   $Rg"
Write-Host ''

az account set --subscription $Sub
if ($LASTEXITCODE -ne 0) { throw 'az account set failed.' }

function Build-App {
    param([string] $App)
    $c = $Config[$App]
    Write-Host ''
    Write-Host "--- Building sqlflow-v3-${App}:$Tag   ($($c.Dockerfile), context $($c.Context)) ---" -ForegroundColor Cyan
    # Exit code intentionally not trusted here; Test-Image confirms server-side.
    az acr build --registry $Acr --image "sqlflow-v3-${App}:$Tag" --file $c.Dockerfile $c.Context
}

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
    # Note: only errors timestamped AFTER 'Bootstrap provisioning completed.' are real;
    # the scheduler races the migrations at startup and prints a harmless early burst.
    az containerapp logs show -n $App -g $Rg --tail 300 --type console |
        Select-String -SimpleMatch -Pattern 'Bootstrap', 'Synced', 'warn', 'error', 'exception'
}

# --- Build every requested app (server-side) ---
foreach ($a in $Apps) { Build-App $a }

# --- Verify server-side that each tag landed; this gates the deploy ---
Write-Host ''
Write-Host "=== Verifying images on $Acr ===" -ForegroundColor Cyan
$allOk = $true
foreach ($a in $Apps) { if (-not (Test-Image $a)) { $allOk = $false } }
if (-not $allOk) { throw 'One or more builds did not succeed - not deploying. See above.' }

# --- Deploy ---
Write-Host ''
Write-Host '=== Deploying container apps ===' -ForegroundColor Cyan
foreach ($a in $Apps) { Deploy-App $a }

# --- Wait for the control-plane revision, then show its startup log ---
if ($Apps -contains 'control-plane') {
    Wait-Running 'sqlflow-v3-control-plane'
    Show-Log 'sqlflow-v3-control-plane'
}

Write-Host ''
Write-Host "=== Done. Deployed tag $Tag to: $($Apps -join ', ') ===" -ForegroundColor Green
