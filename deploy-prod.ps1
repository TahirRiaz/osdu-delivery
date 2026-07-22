#requires -Version 5.1
<#
.SYNOPSIS
    SQLFlow V3 prod-v2 container deploy: build images in ACR (fast + parallel),
    point the container apps at the new tag, wait for the new revisions, show the
    control-plane startup log.

.DESCRIPTION
    The image tag is the current commit's short SHA (what the apps actually run).
    Keep this script at the repo root. Do NOT run it while a manual deploy is in
    flight: it wipes .deploy-ctx on start.

    Fast path (why this is not just `az acr build .`):
      * Context is a `git archive` of TRACKED files only, extracted to .deploy-ctx.
        `az acr build .` uploads the whole working tree and does NOT honor
        .dockerignore on the client side, so it ships gigabytes of bin/obj/target/
        node_modules every build (2.9 GiB here). The archive is tens of MB.
      * Each build runs FROM INSIDE its context directory with `--file <name> .`.
        az resolves --file against the current working directory, not the context
        path, so building from the repo root with `--file Dockerfile` would pick the
        repo-root (backend) Dockerfile against the wrong context.
      * Builds run in parallel, and each is verified by its ACR run id polled to a
        terminal state. The az CLI can crash on a glyph AFTER the server build
        succeeds (cp1252 cannot encode its check mark), so the local exit code is not
        trusted; the run id (printed before any crash) is.

    NOT handled here: the pipeline YAML in the separate dwh-pipelines-prod repo.

.EXAMPLE
    .\deploy-prod.ps1
    Build + deploy control-plane, worker, and gui.

.EXAMPLE
    .\deploy-prod.ps1 control-plane worker
    Only those apps. Known: control-plane worker gui mcp slack-bot
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

# app -> Dockerfile name (as it sits at the root of that app's context) and which context it uses.
# 'repo' is the whole-repo archive; 'gui' is the gui/ subtree archive (its Dockerfile is at the subtree root).
$Config = [ordered]@{
    'control-plane' = @{ Dockerfile = 'Dockerfile';          Sub = 'repo' }
    'worker'        = @{ Dockerfile = 'Dockerfile.worker';   Sub = 'repo' }
    'gui'           = @{ Dockerfile = 'Dockerfile';          Sub = 'gui'  }
    'mcp'           = @{ Dockerfile = 'Dockerfile.mcp';      Sub = 'repo' }
    'slack-bot'     = @{ Dockerfile = 'Dockerfile.slackbot'; Sub = 'repo' }
}

if (-not $Apps -or $Apps.Count -eq 0) {
    $Apps = @('control-plane', 'worker', 'gui')
}
foreach ($a in $Apps) {
    if (-not $Config.Contains($a)) {
        throw "Unknown app '$a'. Known: $($Config.Keys -join ', ')"
    }
}

$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'

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

function Wait-RunTerminal {
    param([string] $RunId)
    for ($i = 0; $i -lt 120; $i++) {
        $st = az acr task show-run -r $Acr --run-id $RunId --query status -o tsv 2>$null
        if ($st) { $st = $st.Trim() }
        if ($st -eq 'Succeeded') { return $true }
        if ($st -in @('Failed', 'Canceled', 'Error', 'Timeout')) {
            Write-Host "   run $RunId -> $st" -ForegroundColor Red
            return $false
        }
        Start-Sleep -Seconds 10
    }
    Write-Host "   run $RunId did not finish in time" -ForegroundColor Red
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
    Write-Host "=== Waiting for $App to run on $Tag ==="
    for ($i = 0; $i -lt 90; $i++) {
        # Match on the tag, not just runningState: during a swap the OLD revision is still Running.
        $img = az containerapp revision list -n $App -g $Rg `
            --query "[?properties.active] | [0].properties.template.containers[0].image" -o tsv 2>$null
        if ($img -and $img -match [regex]::Escape($Tag)) {
            Write-Host "   ${App}: Running on $Tag" -ForegroundColor Green
            return
        }
        Start-Sleep -Seconds 10
    }
    Write-Warning "$App did not reach $Tag in time; check the portal."
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
Remove-Item -Recurse -Force $CtxRoot -ErrorAction SilentlyContinue
$needRepo = @($Apps | Where-Object { $Config[$_].Sub -eq 'repo' }).Count -gt 0
$needGui  = @($Apps | Where-Object { $Config[$_].Sub -eq 'gui' }).Count -gt 0

Write-Host 'Preparing clean build context (tracked files only)...'
if ($needRepo) {
    $d = Join-Path $CtxRoot 'repo'
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    git archive --format=tar.gz -o (Join-Path $CtxRoot 'repo.tar.gz') HEAD
    if ($LASTEXITCODE -ne 0) { throw 'git archive (repo) failed.' }
    tar -xzf (Join-Path $CtxRoot 'repo.tar.gz') -C $d
}
if ($needGui) {
    $d = Join-Path $CtxRoot 'gui'
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    git archive --format=tar.gz -o (Join-Path $CtxRoot 'gui.tar.gz') 'HEAD:gui'
    if ($LASTEXITCODE -ne 0) { throw 'git archive (gui) failed.' }
    tar -xzf (Join-Path $CtxRoot 'gui.tar.gz') -C $d
}

# --- Build every requested app in parallel ------------------------------------------
Write-Host "Building $($Apps.Count) image(s) in parallel at $Tag..." -ForegroundColor Cyan
$jobs = foreach ($a in $Apps) {
    $ctx = Join-Path $CtxRoot $Config[$a].Sub
    $df  = $Config[$a].Dockerfile
    Start-Job -Name $a -ScriptBlock {
        param($Acr, $App, $Tag, $Df, $Ctx)
        $env:PYTHONUTF8 = '1'; $env:PYTHONIOENCODING = 'utf-8'
        # Run from inside the context so az finds the Dockerfile (it resolves --file against the cwd).
        Set-Location -LiteralPath $Ctx
        az acr build --registry $Acr --image "sqlflow-v3-${App}:$Tag" --file $Df . 2>&1
    } -ArgumentList $Acr, $a, $Tag, $df, $ctx
}
$jobs | Wait-Job | Out-Null

# --- Verify each build by its ACR run id (gates the deploy) --------------------------
Write-Host ''
Write-Host '=== Verifying builds on ACR ===' -ForegroundColor Cyan
$allOk = $true
foreach ($j in $jobs) {
    $out = (Receive-Job $j) | Out-String
    Remove-Job $j
    $m = [regex]::Match($out, 'Queued a build with ID:\s*(\S+)')
    if (-not $m.Success) {
        Write-Host "   $($j.Name): build never queued" -ForegroundColor Red
        Write-Host $out
        $allOk = $false
        continue
    }
    $runId = $m.Groups[1].Value
    Write-Host "   $($j.Name): run $runId, waiting for result..."
    if (Wait-RunTerminal $runId) {
        Write-Host "   $($j.Name): Succeeded" -ForegroundColor Green
    }
    else { $allOk = $false }
}
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
