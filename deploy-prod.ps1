#requires -Version 5.1
<#
.SYNOPSIS
    OSDU Delivery prod container deploy: detect which images actually changed since the
    tag each app is serving, build those in ACR (fast + parallel), point the container
    apps at the new tag, verify the new revision is actually serving, and roll back
    automatically if it is not.

.DESCRIPTION
    The image tag is the current commit's short SHA (what the apps actually run).
    Keep this script at the repo root. Do NOT run it while a manual deploy is in
    flight: it wipes .deploy-ctx on start.

    ESTATE: the resource group, registry, subscription and app naming schemes are
    parameters with no defaults for the estate itself (-ResourceGroup, -Registry,
    -Subscription, or OSDU_DEPLOY_RG / OSDU_DEPLOY_ACR / OSDU_DEPLOY_SUBSCRIPTION).
    They name an EXISTING estate: the script targets apps by name and creates nothing.

    Change detection (the single interface: run it bare and it deploys what is required):
      * Each app declares the paths its image is built from (its Dockerfile plus the
        source trees that Dockerfile reads). The tag an app currently serves IS a
        commit SHA, so `git diff <servingTag> HEAD -- <paths>` decides whether the
        image needs a rebuild. Unchanged apps are skipped, with the reason printed.
      * Bare invocation considers control-plane, worker, and gui. Naming apps explicitly
        deploys them unconditionally (no change filter): an explicit ask is an order, and
        it is also the escape hatch when detection must be bypassed.
      * An app with no serving image, a foreign image, or a tag that is not a commit
        in this clone counts as changed: when the baseline cannot be trusted, deploy.

    Fast path (why this is not just `az acr build .`):
      * Context is a `git archive` of TRACKED files only, extracted to .deploy-ctx.
        `az acr build .` uploads the whole working tree and does NOT honor
        .dockerignore on the client side, so it ships gigabytes of bin/obj/
        node_modules every build. The archive is tens of MB.
      * All three images build from the SAME repository-root context: the .NET hosts
        in osdu/hosts reference both osdu/src and sqlflow/src, and the GUI compiles
        osdu/gui together with the vendored sqlflow/gui sources it imports.
      * Each build runs FROM INSIDE the context directory with `--file <path> .`,
        because az resolves --file against the current working directory, not the
        context path.
      * Builds run in parallel, and each is verified by its ACR run id polled to a
        terminal state. The az CLI can crash on a glyph AFTER the server build
        succeeds (cp1252 cannot encode its check mark), so the local exit code is not
        trusted; the run id (printed before any crash) is.

    SAFETY (why this script refuses to guess):
      * An estate can hold the same app under more than one naming scheme (a renamed
        estate beside the one it replaced). `az containerapp update -n <name>` targets by
        name alone, so a stale name silently deploys to the DEAD environment and still
        reports success. This script resolves each app across every -AppPrefix given and
        REFUSES to continue when the choice is ambiguous: pass -Target with the prefix
        you mean.
      * Every deploy prints the resolved app, its environment, and the image it is
        replacing, before anything is changed. Use -WhatIf to see that plan and stop.
      * A deploy is not "done" when the API accepts it. Each app is verified to be
        serving the new tag on a healthy revision; if it is not, the app is rolled back
        to the exact image it was running and the script exits non-zero.
      * Building from a dirty tree is an error, not a warning: the image would not
        contain your changes. Pass -Force if that is genuinely what you want.

    NOT handled here: creating the container apps (they must already exist, with their
    identity/secrets/env wired), and the flow YAML in the estate's flow repositories.

.EXAMPLE
    .\deploy-prod.ps1
    Deploy whichever of control-plane, worker, and gui changed since the tag each serves.

.EXAMPLE
    .\deploy-prod.ps1 all
    Same change detection, but across every app: control-plane, worker, gui.

.EXAMPLE
    .\deploy-prod.ps1 control-plane worker
    Force exactly those apps, changed or not. Known: control-plane worker gui

.EXAMPLE
    .\deploy-prod.ps1 -WhatIf
    Resolve the targets and print the plan without building or deploying.

.EXAMPLE
    .\deploy-prod.ps1 -ResourceGroup my-rg -Registry myacr -Subscription 00000000-0000-0000-0000-000000000000
    Name the estate explicitly instead of through OSDU_DEPLOY_RG, OSDU_DEPLOY_ACR and OSDU_DEPLOY_SUBSCRIPTION.

.EXAMPLE
    .\deploy-prod.ps1 -AppPrefix osdu-delivery-,osdu- -Target osdu-delivery-
    Search both naming schemes, and deploy to the osdu-delivery- one where an app exists under both.
#>
# PositionalBinding=$false so bare app names only ever bind to -Apps. Without it PowerShell
# hands the first positional argument to the next declared parameter, and `.\deploy-prod.ps1
# control-plane worker` fails with "control-plane does not belong to the set" for -Target.
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Apps,

    # Which naming scheme to deploy to when an app exists under more than one of -AppPrefix.
    # 'auto' searches them in order; anything else must be one of the prefixes given.
    [string] $Target = 'auto',

    # Build and deploy even though the image build paths have uncommitted changes.
    [switch] $Force,

    # Resolve targets and print the plan, then stop.
    [switch] $WhatIf,

    # Leave a failed deploy in place instead of restoring the previous image.
    [switch] $NoRollback,

    # The estate. No defaults: naming it is a deliberate act, and a wrong name would deploy these
    # images over another product's running apps. Pass them, or set OSDU_DEPLOY_RG,
    # OSDU_DEPLOY_ACR and OSDU_DEPLOY_SUBSCRIPTION.
    [string] $ResourceGroup = $env:OSDU_DEPLOY_RG,

    [string] $Registry = $env:OSDU_DEPLOY_ACR,

    [string] $Subscription = $env:OSDU_DEPLOY_SUBSCRIPTION,

    # The container app name prefixes to look for, newest first, and the image repository prefix.
    # An app is resolved across every prefix given, which is how a renamed estate stays reachable.
    [string[]] $AppPrefix = @('osdu-delivery-'),

    [string] $ImagePrefix = 'osdu-delivery-'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

foreach ($required in @(
    @{ Name = 'ResourceGroup'; Value = $ResourceGroup; Variable = 'OSDU_DEPLOY_RG'; What = 'the resource group holding the container apps' },
    @{ Name = 'Registry'; Value = $Registry; Variable = 'OSDU_DEPLOY_ACR'; What = 'the container registry to build the images in' },
    @{ Name = 'Subscription'; Value = $Subscription; Variable = 'OSDU_DEPLOY_SUBSCRIPTION'; What = 'the subscription that estate lives in' })) {
    if ([string]::IsNullOrWhiteSpace($required.Value)) {
        throw "Name the estate before deploying: pass -$($required.Name), or set $($required.Variable), to $($required.What)."
    }
}

if ($AppPrefix.Count -eq 0 -or ($AppPrefix | Where-Object { [string]::IsNullOrWhiteSpace($_) })) {
    throw 'Every -AppPrefix must be a non-blank container app name prefix.'
}

if ([string]::IsNullOrWhiteSpace($ImagePrefix)) {
    throw 'Name the image repository prefix with -ImagePrefix.'
}

$Rg  = $ResourceGroup
$Acr = $Registry
$Sub = $Subscription

# The naming schemes an app can live under, in the order they are searched. The ACR repository name
# is $ImagePrefix<app> regardless of what the container app resource is called.
$NamePrefix = [ordered]@{}
for ($i = 0; $i -lt $AppPrefix.Count; $i++) {
    $NamePrefix["scheme$($i + 1)"] = $AppPrefix[$i]
}

# app -> the Dockerfile that builds it (repo-relative, as used from inside the context root)
# and the repo paths that feed the image. Paths drive change detection: an app is rebuilt only
# when `git diff <servingTag> HEAD -- <Paths>` is non-empty, so a docs-only commit deploys
# nothing. All three images use the repository root as their context.
$DotnetBuildInputs = @(
    'osdu/src', 'osdu/hosts', 'sqlflow/src',
    'OsduDelivery.sln', 'global.json', '.dockerignore',
    'osdu/Directory.Build.props', 'osdu/Directory.Packages.props',
    'sqlflow/Directory.Build.props', 'sqlflow/Directory.Packages.props'
)
$Config = [ordered]@{
    'control-plane' = @{
        Dockerfile = 'osdu/deploy/docker/control-plane.Dockerfile'
        Paths      = @('osdu/deploy/docker/control-plane.Dockerfile') + $DotnetBuildInputs
    }
    'worker'        = @{
        Dockerfile = 'osdu/deploy/docker/worker.Dockerfile'
        Paths      = @('osdu/deploy/docker/worker.Dockerfile', 'osdu/deploy/docker/worker-entrypoint.sh') + $DotnetBuildInputs
    }
    'gui'           = @{
        Dockerfile = 'osdu/deploy/docker/gui.Dockerfile'
        Paths      = @(
            'osdu/deploy/docker/gui.Dockerfile',
            'osdu/deploy/docker/nginx.conf',
            'osdu/deploy/docker/40-runtime-config.sh',
            '.dockerignore',
            'osdu/gui',
            'sqlflow/gui'
        )
    }
}

# Explicitly named apps deploy unconditionally; the bare default and 'all' are candidate sets
# that change detection narrows to what is actually required.
$ExplicitSelection = $true
if (-not $Apps -or $Apps.Count -eq 0) {
    $Apps = @('control-plane', 'worker', 'gui')
    $ExplicitSelection = $false
}
if ($Apps -contains 'all') {
    $Apps = @($Config.Keys)
    $ExplicitSelection = $false
}
foreach ($a in $Apps) {
    if (-not $Config.Contains($a)) {
        throw "Unknown app '$a'. Known: $($Config.Keys -join ', '), or 'all' for every one."
    }
}

$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'

$Tag = (git rev-parse --short HEAD).Trim()
if (-not $Tag) { throw 'Could not read git HEAD. Run this from the OSDU Delivery repo.' }

# A dirty tree means the image would NOT contain the working changes. That has burned
# enough deploys to be an error rather than a warning. Checked over the watch paths of
# every app in play.
$watchPaths = @($Apps | ForEach-Object { $Config[$_].Paths } | Sort-Object -Unique)
$dirty = git status --porcelain -- @watchPaths
if ($dirty) {
    if (-not $Force) {
        Write-Host ''
        Write-Host "Uncommitted changes in the paths these images are built from:" -ForegroundColor Yellow
        Write-Host ($dirty | Out-String)
        throw "Refusing to deploy: image $Tag is built from the COMMITTED tree and would not include the changes above. Commit them, or pass -Force to deploy $Tag anyway."
    }
    Write-Warning "Uncommitted changes in image build paths - image $Tag is built from the committed tree and will NOT include them (-Force given)."
}

# Every az call goes through this. PowerShell 5.1 turns ANY stderr output from a native
# executable into a NativeCommandError, and under $ErrorActionPreference='Stop' that kills
# the script even when az succeeded (az writes progress, warnings, and stray blank lines to
# stderr routinely). So stderr is captured, and success is judged by the exit code alone.
function Invoke-Az {
    param([Parameter(Mandatory)][string[]] $AzArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & az @AzArgs 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    return [pscustomobject]@{ Output = $out; ExitCode = $code }
}

$r = Invoke-Az @('account', 'set', '--subscription', $Sub)
if ($r.ExitCode -ne 0) { throw 'az account set failed.' }

# --- Resolve each logical app to a real container app, refusing to guess ---------------

# Every container app in the resource group, fetched once. Resolution then happens in
# memory: `az containerapp show` on a missing app writes to stderr, which PowerShell 5.1
# turns into a terminating NativeCommandError under $ErrorActionPreference='Stop', so
# probing name-by-name would blow up on the very case it needs to handle (not found).
$listResult = Invoke-Az @('containerapp', 'list', '-g', $Rg, '-o', 'json')
if ($listResult.ExitCode -ne 0 -or -not $listResult.Output) { throw "Could not list container apps in $Rg." }
# ConvertFrom-Json emits a JSON array as ONE object, so assign first; piping it straight
# into a filter would treat the whole array as a single element.
$AllApps = @((($listResult.Output -join "`n") | ConvertFrom-Json))

function Get-AppRecord {
    <#
        Returns the container app resource for a logical app name, or $null when it does
        not exist. Also carries the environment name and the image currently deployed, so
        the plan can be shown and a rollback target captured before anything changes.
    #>
    param([string] $ResourceName)

    $obj = $AllApps | Where-Object { $_.name -eq $ResourceName } | Select-Object -First 1
    if (-not $obj) { return $null }

    $envName = ($obj.properties.environmentId -split '/')[-1]
    $image = $null
    if ($obj.properties.template.containers -and $obj.properties.template.containers.Count -gt 0) {
        $image = $obj.properties.template.containers[0].image
    }

    return [pscustomobject]@{
        Name        = $ResourceName
        Environment = $envName
        Image       = $image
    }
}

function Resolve-Target {
    <#
        Finds the one container app a logical app refers to. When both naming schemes
        exist the choice is genuinely ambiguous, so this throws and asks for -Target
        instead of picking one: silently deploying to the retired estate looks like a
        success and is the exact failure this guard exists to prevent.
    #>
    param([string] $App)

    $found = @()
    foreach ($scheme in $NamePrefix.Keys) {
        if ($Target -ne 'auto' -and $NamePrefix[$scheme] -ne $Target) { continue }
        $rec = Get-AppRecord -ResourceName "$($NamePrefix[$scheme])$App"
        if ($rec) {
            $rec | Add-Member -NotePropertyName Scheme -NotePropertyValue $scheme -Force
            $found += $rec
        }
    }

    if ($found.Count -eq 0) {
        $tried = @()
        foreach ($scheme in $NamePrefix.Keys) {
            if ($Target -ne 'auto' -and $NamePrefix[$scheme] -ne $Target) { continue }
            $tried += "$($NamePrefix[$scheme])$App"
        }
        throw "Container app for '$App' not found in $Rg (looked for: $($tried -join ', ')). Create it first, or check -Target."
    }

    if ($found.Count -gt 1) {
        $detail = ($found | ForEach-Object { "$($_.Name) (env $($_.Environment), prefix $($NamePrefix[$_.Scheme]))" }) -join ' AND '
        throw "Ambiguous target for '$App': $detail. Re-run with -Target and one of $($AppPrefix -join ', ') so this does not deploy to the wrong estate."
    }

    return $found[0]
}

function Get-ChangeStatus {
    <#
        Decides whether an app's image needs a rebuild by diffing its watch paths between
        the commit it is SERVING (the tag on its current image is a short SHA) and HEAD.
        Any baseline that cannot be trusted (no image, a foreign image, a tag that is not
        a commit in this clone) counts as changed: guessing "unchanged" there would skip
        a deploy that is in fact required, which is the one wrong answer.
    #>
    param([string] $App, [pscustomobject] $Record)

    if (-not $Record.Image) {
        return [pscustomobject]@{ Changed = $true; Reason = 'nothing deployed yet' }
    }
    if ($Record.Image -notmatch "/$([regex]::Escape($ImagePrefix))$([regex]::Escape($App)):([^:/]+)$") {
        return [pscustomobject]@{ Changed = $true; Reason = "serving a foreign image ($($Record.Image))" }
    }
    $baseTag = $Matches[1]

    git rev-parse --quiet --verify "$baseTag^{commit}" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Changed = $true; Reason = "serving tag $baseTag is not a commit in this clone" }
    }

    $paths = @($Config[$App].Paths)
    git diff --quiet $baseTag HEAD -- @paths
    if ($LASTEXITCODE -eq 0) {
        return [pscustomobject]@{ Changed = $false; Reason = "no changes vs serving tag $baseTag" }
    }
    if ($LASTEXITCODE -eq 1) {
        $files = @(git diff --name-only $baseTag HEAD -- @paths)
        return [pscustomobject]@{ Changed = $true; Reason = "$($files.Count) file(s) changed since $baseTag" }
    }
    return [pscustomobject]@{ Changed = $true; Reason = "git diff vs $baseTag failed (exit $LASTEXITCODE), assuming changed" }
}

Write-Host ''
Write-Host '=== OSDU Delivery deploy ===' -ForegroundColor Cyan
Write-Host "   tag:  $Tag"
$mode = if ($ExplicitSelection) { 'forced (named explicitly)' } else { 'candidates (change-filtered below)' }
Write-Host "   apps: $($Apps -join ', ') [$mode]"
Write-Host "   rg:   $Rg"
Write-Host "   acr:  $Acr"
Write-Host "   name: $($AppPrefix -join ', ')<app>, image $ImagePrefix<app>"
Write-Host "   target: $Target"
Write-Host ''

Write-Host '=== Resolved targets ===' -ForegroundColor Cyan
$targets = [ordered]@{}
foreach ($a in $Apps) {
    $rec = Resolve-Target -App $a
    $targets[$a] = $rec
    Write-Host ("   {0,-14} -> {1,-28} env {2,-22} now: {3}" -f $a, $rec.Name, $rec.Environment, $rec.Image)
}

# Explicit names are an order; candidate sets (bare default, 'all') get narrowed to the
# apps whose build inputs actually changed since the tag each one is serving.
if (-not $ExplicitSelection) {
    Write-Host ''
    Write-Host '=== Change detection (build inputs vs each serving tag) ===' -ForegroundColor Cyan
    $selected = @()
    foreach ($a in $Apps) {
        $st = Get-ChangeStatus -App $a -Record $targets[$a]
        if ($st.Changed) {
            Write-Host ("   {0,-14} deploy  {1}" -f $a, $st.Reason)
            $selected += $a
        }
        else {
            Write-Host ("   {0,-14} skip    {1}" -f $a, $st.Reason) -ForegroundColor DarkGray
        }
    }
    if ($selected.Count -eq 0) {
        Write-Host ''
        Write-Host "Nothing to deploy: every candidate app already serves its build inputs at HEAD ($Tag)." -ForegroundColor Green
        return
    }
    $Apps = $selected
}

# Deploying half the estate to one environment and half to another is never intended.
$envs = @($Apps | ForEach-Object { $targets[$_].Environment } | Sort-Object -Unique)
if ($envs.Count -gt 1) {
    throw "Selected apps span more than one environment ($($envs -join ', ')). Re-run with an explicit -Target."
}

if ($WhatIf) {
    Write-Host ''
    Write-Host "-WhatIf: nothing built or deployed. Would deploy tag $Tag to: $($Apps -join ', ')." -ForegroundColor Yellow
    return
}

function Wait-RunTerminal {
    param([string] $RunId)
    for ($i = 0; $i -lt 120; $i++) {
        $res = Invoke-Az @('acr', 'task', 'show-run', '-r', $Acr, '--run-id', $RunId, '--query', 'status', '-o', 'tsv')
        $st = ''
        if ($res.ExitCode -eq 0 -and $res.Output) { $st = ($res.Output | Select-Object -First 1).ToString().Trim() }
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

function Set-AppImage {
    param([string] $ResourceName, [string] $Image)
    $res = Invoke-Az @('containerapp', 'update', '-n', $ResourceName, '-g', $Rg, '--image', $Image, '-o', 'none')
    if ($res.ExitCode -ne 0) {
        Write-Host ($res.Output | Out-String)
        throw "az containerapp update failed for $ResourceName (exit $($res.ExitCode))."
    }
}

function Test-Serving {
    <#
        True once the app's active revision runs $Image AND that revision is healthy.
        Matching on the image alone is not enough: during a swap the OLD revision is
        still active and Running, so a naive check passes against the previous build.
    #>
    param([string] $ResourceName, [string] $Image, [int] $TimeoutSeconds = 900)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $res = Invoke-Az @('containerapp', 'revision', 'list', '-n', $ResourceName, '-g', $Rg, '-o', 'json')
        if ($res.ExitCode -eq 0 -and $res.Output) {
            $revs = @((($res.Output -join "`n") | ConvertFrom-Json))
            foreach ($r in $revs) {
                if (-not $r.properties.active) { continue }
                $img = $null
                if ($r.properties.template.containers -and $r.properties.template.containers.Count -gt 0) {
                    $img = $r.properties.template.containers[0].image
                }
                if ($img -ne $Image) { continue }

                $running = "$($r.properties.runningState)"
                $health  = "$($r.properties.healthState)"
                # A node scaled to zero reports Scaled/Inactive rather than Running, which is
                # correct and healthy for it, so both are accepted.
                if ($running -in @('Running', 'RunningAtMaxScale', 'Scaled', 'Succeeded') -and $health -ne 'Unhealthy') {
                    return $true
                }
                if ($running -eq 'Failed' -or $health -eq 'Unhealthy') {
                    Write-Host "   $ResourceName revision $($r.name): runningState=$running healthState=$health" -ForegroundColor Red
                    return $false
                }
            }
        }
        Start-Sleep -Seconds 10
    }
    Write-Host "   $ResourceName did not serve $Image within $TimeoutSeconds s" -ForegroundColor Red
    return $false
}

function Show-Log {
    param([string] $ResourceName)
    Write-Host ''
    Write-Host "=== $ResourceName startup log (bootstrap / sync / warnings / errors) ==="
    # Only issues timestamped AFTER 'Bootstrap provisioning completed.' are real; the scheduler
    # races the migrations at startup and prints a harmless early burst.
    $res = Invoke-Az @('containerapp', 'logs', 'show', '-n', $ResourceName, '-g', $Rg, '--tail', '300', '--type', 'console')
    $res.Output | Select-String -SimpleMatch -Pattern 'Bootstrap', 'Synced', 'warn', 'error', 'exception'
}

# --- Build one clean, minimal context from tracked files only ------------------------
# All three images share it: the .NET hosts span osdu/ and sqlflow/, and the GUI compiles
# both GUI trees.
$CtxRoot = Join-Path $PSScriptRoot '.deploy-ctx'
Remove-Item -Recurse -Force $CtxRoot -ErrorAction SilentlyContinue
$RepoCtx = Join-Path $CtxRoot 'repo'

Write-Host ''
Write-Host 'Preparing clean build context (tracked files only)...'
New-Item -ItemType Directory -Force -Path $RepoCtx | Out-Null
git archive --format=tar.gz -o (Join-Path $CtxRoot 'repo.tar.gz') HEAD
if ($LASTEXITCODE -ne 0) { throw 'git archive failed.' }
tar -xzf (Join-Path $CtxRoot 'repo.tar.gz') -C $RepoCtx

# --- Build every requested app in parallel ------------------------------------------
Write-Host "Building $($Apps.Count) image(s) in parallel at $Tag..." -ForegroundColor Cyan
$jobs = foreach ($a in $Apps) {
    $df = $Config[$a].Dockerfile
    Start-Job -Name $a -ScriptBlock {
        param($Acr, $App, $Tag, $Df, $Ctx, $Prefix)
        $env:PYTHONUTF8 = '1'; $env:PYTHONIOENCODING = 'utf-8'
        # Run from inside the context so az finds the Dockerfile (it resolves --file against the cwd).
        Set-Location -LiteralPath $Ctx
        az acr build --registry $Acr --image "${Prefix}${App}:$Tag" --file $Df . 2>&1
    } -ArgumentList $Acr, $a, $Tag, $df, $RepoCtx, $ImagePrefix
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

# --- Deploy, verify, and roll back anything that does not come up --------------------
Write-Host ''
Write-Host '=== Deploying container apps ===' -ForegroundColor Cyan
$failed = @()
foreach ($a in $Apps) {
    $rec = $targets[$a]
    $newImage = "$Acr.azurecr.io/${ImagePrefix}${a}:$Tag"
    Write-Host "--- $($rec.Name) -> :$Tag ---"

    Set-AppImage -ResourceName $rec.Name -Image $newImage

    if (Test-Serving -ResourceName $rec.Name -Image $newImage) {
        Write-Host "   $($rec.Name): serving $Tag" -ForegroundColor Green
        continue
    }

    $failed += $a
    if ($NoRollback) {
        Write-Host "   $($rec.Name): FAILED to serve $Tag (-NoRollback, leaving it as is)" -ForegroundColor Red
        continue
    }
    if (-not $rec.Image) {
        Write-Host "   $($rec.Name): FAILED to serve $Tag and no previous image was recorded, cannot roll back" -ForegroundColor Red
        continue
    }

    Write-Host "   $($rec.Name): FAILED to serve $Tag, rolling back to $($rec.Image)" -ForegroundColor Red
    try {
        Set-AppImage -ResourceName $rec.Name -Image $rec.Image
        if (Test-Serving -ResourceName $rec.Name -Image $rec.Image -TimeoutSeconds 600) {
            Write-Host "   $($rec.Name): rolled back and serving $($rec.Image)" -ForegroundColor Yellow
        }
        else {
            Write-Host "   $($rec.Name): ROLLBACK DID NOT COME UP - needs manual attention" -ForegroundColor Red
        }
    }
    catch {
        Write-Host "   $($rec.Name): rollback threw: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# --- Control-plane startup log is the useful one to eyeball --------------------------
if ($Apps -contains 'control-plane' -and $failed -notcontains 'control-plane') {
    Show-Log $targets['control-plane'].Name
}

Write-Host ''
if ($failed.Count -gt 0) {
    throw "Deploy FAILED for: $($failed -join ', ') (tag $Tag). Any app listed above was rolled back unless -NoRollback was given."
}
Write-Host "=== Done. Deployed tag $Tag to: $(($Apps | ForEach-Object { $targets[$_].Name }) -join ', ') ===" -ForegroundColor Green
