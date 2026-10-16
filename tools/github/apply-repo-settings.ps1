<#
.SYNOPSIS
Applies the repository settings that make work reach main through reviewed pull requests, and the deployment
environments the deploy workflow uses.

.DESCRIPTION
Applies, through the gh CLI and idempotently (each setting is read first, and only what differs is changed):

  - the ruleset in .github/rulesets/main.json, created or updated by its name: main cannot be deleted or force-pushed,
    keeps a linear history, and takes changes only through a pull request with one approval, resolved conversations
    and every CI check passing on a branch up to date with main;
  - the merge settings: rebase merging only, the head branch deleted on merge, auto-merge allowed;
  - the environments dev, test and prod, each deploying only from the default branch, prod also waiting for the
    reviewers given;
  - with -DeployMethod, the DEPLOY_METHOD repository variable that chooses which deploy method runs.

-WhatIf reads the repository and prints every change it would make, changing nothing.

Prerequisites, checked before anything changes:
  - the gh CLI, signed in (gh auth login) as an administrator of the repository, with a token that has the repo scope
    (or a fine-grained token with Administration, Environments and Variables write access);
  - a plan that offers rulesets on the repository: a public repository has them, and so does every repository of an
    organization on GitHub Team or Enterprise; a private repository of a personal account needs GitHub Pro. Without
    them the API answers 403 "Upgrade to GitHub Pro or make this repository public", and the script says so;
  - for prod's required reviewers on a private or internal repository, GitHub Enterprise (on Free, Pro and Team they
    exist for public repositories only). When the plan refuses them, prod is still restricted to the default branch
    and the script reports that it has no reviewers.

The script never reads or prints a token: gh holds the credential.

.PARAMETER Repository
The repository as owner/name. Defaults to what gh resolves for this clone, or else the origin remote's URL.

.PARAMETER ProdReviewers
Who approves a deployment to prod: a user's login, or org/team-slug for a team of the owning organization. One to six.

.PARAMETER DeployMethod
The deploy method the repository uses, set as the DEPLOY_METHOD repository variable: terraform runs the Deploy
workflow's jobs, and a deploy workflow runs only for the method it belongs to. Left as it is when omitted.

.EXAMPLE
pwsh -File tools/github/apply-repo-settings.ps1 -ProdReviewers my-org/release-approvers -DeployMethod terraform -WhatIf

Prints what would change on the repository this clone belongs to.

.EXAMPLE
powershell -File tools\github\apply-repo-settings.ps1 -Repository my-org/osdu-delivery -ProdReviewers alice,bob

Applies the settings to my-org/osdu-delivery, with alice and bob approving deployments to prod.

.NOTES
The ruleset lets repository administrators (RepositoryRole 5) bypass it at any time ("always"): a subtree pull of
SQLFlow lands as a merge commit, which neither a rebase merge nor a linear history admits, and an administrator may
have to land an emergency fix. In an organization whose owners, rather than the repository's administrators, should
hold that bypass, use the actor type OrganizationAdmin in .github/rulesets/main.json instead.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Repository,

    [Parameter(Mandatory = $true)]
    [ValidateCount(1, 6)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)?$')]
    [string[]] $ProdReviewers,

    [ValidatePattern('^[a-z][a-z0-9-]{0,31}$')]
    [string] $DeployMethod
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$environmentNames = @('dev', 'test', 'prod')
$repositoryPattern = '^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+$'
$repoRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($PSScriptRoot, '..', '..'))
$rulesetRelativePath = '.github/rulesets/main.json'
$rulesetPath = [System.IO.Path]::Combine($repoRoot, '.github', 'rulesets', 'main.json')
$problems = New-Object System.Collections.Generic.List[string]

function Write-Step([string] $Text) {
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
}

function Write-Detail([string] $Text) {
    Write-Host ('  ' + $Text)
}

# Runs a native command and returns its exit code, its standard output and its standard error, without letting a line
# on standard error become a terminating error (Windows PowerShell does that under ErrorActionPreference Stop).
function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter(Mandatory = $true)] [string[]] $Arguments
    )
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    $out = New-Object System.Collections.Generic.List[string]
    $err = New-Object System.Collections.Generic.List[string]
    foreach ($line in @($lines)) {
        if ($line -is [System.Management.Automation.ErrorRecord]) {
            $err.Add($line.ToString())
        }
        elseif ($null -ne $line) {
            $out.Add([string] $line)
        }
    }
    [pscustomobject]@{
        ExitCode = $exitCode
        Output   = ($out -join "`n")
        Error    = ($err -join "`n")
    }
}

# A property of a parsed JSON answer, or null when the answer lacks it (strict mode would throw on a missing one).
function Get-JsonValue($Object, [string] $Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# Calls the REST API through gh. Ok is false on any answer outside 2xx; Status is the HTTP status gh reports, and
# Message what GitHub said, from the answer's message when it has one.
function Invoke-GhApi {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [string] $Method = 'GET',
        [string] $InputFile,
        [string[]] $Fields = @(),
        [string[]] $RawFields = @()
    )
    $arguments = @('api', '--method', $Method, '-H', 'Accept: application/vnd.github+json',
        '-H', 'X-GitHub-Api-Version: 2022-11-28')
    if ($InputFile) { $arguments += @('--input', $InputFile) }
    foreach ($field in $Fields) { $arguments += @('-F', $field) }
    foreach ($field in $RawFields) { $arguments += @('-f', $field) }
    $arguments += $Path

    $result = Invoke-Native -FilePath 'gh' -Arguments $arguments
    $body = $null
    if ($result.Output.Trim().Length -gt 0) {
        try {
            $body = $result.Output | ConvertFrom-Json
        }
        catch {
            $body = $null
        }
    }
    $status = 0
    if ($result.Error -match '\(HTTP (\d{3})\)') { $status = [int] $Matches[1] }
    elseif ($result.ExitCode -eq 0) { $status = 200 }
    $message = [string] (Get-JsonValue $body 'message')
    if (-not $message) { $message = ($result.Error -replace '^gh: ', '') }
    # Without its closing full stop, so it reads the same quoted or inside a sentence of this script's.
    $message = $message.Trim().TrimEnd('.')
    [pscustomobject]@{
        Ok      = ($result.ExitCode -eq 0)
        Status  = $status
        Body    = $body
        Message = $message
    }
}

function Write-JsonFile([object] $Value) {
    $path = [System.IO.Path]::GetTempFileName()
    $json = $Value | ConvertTo-Json -Depth 10 -Compress
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
    return $path
}

function Remove-TempFile([string] $Path) {
    if ($Path -and [System.IO.File]::Exists($Path)) { [System.IO.File]::Delete($Path) }
}

function Test-PlanRefusal([string] $Message) {
    return ($Message -match 'Upgrade to GitHub|make this repository public|billing plan|GitHub Enterprise')
}

# Prerequisites ---------------------------------------------------------------------------------------------------

Write-Step 'Prerequisites'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'The gh CLI is not on the PATH. Install it from https://cli.github.com and sign in with gh auth login.'
}
$version = Invoke-Native -FilePath 'gh' -Arguments @('--version')
Write-Detail (($version.Output -split "`n")[0])

$previousPrompt = $env:GH_PROMPT_DISABLED
$env:GH_PROMPT_DISABLED = '1'
try {
    $auth = Invoke-Native -FilePath 'gh' -Arguments @('auth', 'status')
    if ($auth.ExitCode -ne 0) {
        throw 'gh is not signed in. Run gh auth login as an administrator of the repository, then run this script again.'
    }
    Write-Detail 'gh is signed in.'

    if (-not $Repository) {
        Push-Location $repoRoot
        try {
            $view = Invoke-Native -FilePath 'gh' -Arguments @('repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner')
            if ($view.ExitCode -eq 0 -and $view.Output.Trim()) {
                $Repository = $view.Output.Trim()
            }
            else {
                $origin = Invoke-Native -FilePath 'git' -Arguments @('remote', 'get-url', 'origin')
                if ($origin.ExitCode -eq 0 -and
                    $origin.Output.Trim() -match '[:/](?<owner>[^/:]+)/(?<name>[^/]+?)(\.git)?/?$') {
                    $Repository = '{0}/{1}' -f $Matches['owner'], $Matches['name']
                }
            }
        }
        finally {
            Pop-Location
        }
        if (-not $Repository) {
            throw 'Neither gh nor the origin remote names the repository of this clone. Pass -Repository owner/name.'
        }
    }
    if ($Repository -notmatch $repositoryPattern) {
        throw ('"{0}" is not a repository as owner/name.' -f $Repository)
    }
    $owner = $Repository.Split('/')[0]

    $repo = Invoke-GhApi -Path "repos/$Repository"
    if (-not $repo.Ok) {
        $text = '{0} cannot be read (HTTP {1}): {2}. Check the name, and that the signed-in account can see it.'
        throw ($text -f $Repository, $repo.Status, $repo.Message)
    }
    $permissions = Get-JsonValue $repo.Body 'permissions'
    if (-not (Get-JsonValue $permissions 'admin')) {
        $text = 'The signed-in account is not an administrator of {0}; only an administrator can change its ' +
            'rulesets, merge settings, environments and variables.'
        throw ($text -f $Repository)
    }
    $visibility = Get-JsonValue $repo.Body 'visibility'
    $ownerType = Get-JsonValue (Get-JsonValue $repo.Body 'owner') 'type'
    $defaultBranch = Get-JsonValue $repo.Body 'default_branch'

    $plan = $null
    $ownerText = 'an organization'
    if ($ownerType -eq 'User') {
        $ownerText = 'a personal account'
        $me = Invoke-GhApi -Path 'user'
        if ($me.Ok -and (Get-JsonValue $me.Body 'login') -eq $owner) {
            $plan = Get-JsonValue (Get-JsonValue $me.Body 'plan') 'name'
        }
    }
    else {
        $org = Invoke-GhApi -Path "orgs/$owner"
        if ($org.Ok) { $plan = Get-JsonValue (Get-JsonValue $org.Body 'plan') 'name' }
    }
    $planText = 'its plan is not visible to this account'
    if ($plan) { $planText = 'plan ' + $plan }
    Write-Detail ('{0}: {1}, owned by {2} ({3}), default branch {4}.' -f
        $Repository, $visibility, $ownerText, $planText, $defaultBranch)
    if ($defaultBranch -ne 'main') {
        $text = 'The default branch is {0}, but CI and the deploy workflow run on main; rename the branch, or change ' +
            'the workflows'' triggers, so they agree.'
        $problems.Add(($text -f $defaultBranch))
    }

    if (-not [System.IO.File]::Exists($rulesetPath)) {
        throw ('The ruleset file {0} is missing.' -f $rulesetPath)
    }
    $ruleset = [System.IO.File]::ReadAllText($rulesetPath) | ConvertFrom-Json
    $rulesetName = Get-JsonValue $ruleset 'name'
    if (-not $rulesetName) { throw ('{0} gives the ruleset no name.' -f $rulesetPath) }

    # The required checks are pinned to the GitHub Actions app, so a status of the same name from anything else never
    # satisfies them. The app's id is checked here, since a wrong one would leave every pull request waiting forever.
    $actionsApp = Invoke-GhApi -Path 'apps/github-actions'
    if (-not $actionsApp.Ok) {
        throw ('The GitHub Actions app cannot be read (HTTP {0}): {1}.' -f $actionsApp.Status, $actionsApp.Message)
    }
    $actionsAppId = [long] (Get-JsonValue $actionsApp.Body 'id')
    foreach ($rule in @(Get-JsonValue $ruleset 'rules')) {
        if ((Get-JsonValue $rule 'type') -ne 'required_status_checks') { continue }
        foreach ($check in @(Get-JsonValue (Get-JsonValue $rule 'parameters') 'required_status_checks')) {
            $integration = Get-JsonValue $check 'integration_id'
            if ($null -ne $integration -and [long] $integration -ne $actionsAppId) {
                $text = 'The ruleset pins the check "{0}" to app {1}, but GitHub Actions is app {2} here; set its ' +
                    'integration_id to {2} in {3}.'
                throw ($text -f (Get-JsonValue $check 'context'), $integration, $actionsAppId, $rulesetRelativePath)
            }
        }
    }
    Write-Detail ('The ruleset "{0}" pins its required checks to the GitHub Actions app ({1}).' -f
        $rulesetName, $actionsAppId)

    # Prod's reviewers, resolved to the ids the environments API takes.
    $reviewers = New-Object System.Collections.Generic.List[object]
    foreach ($reviewer in $ProdReviewers) {
        if ($reviewer.Contains('/')) {
            $parts = $reviewer.Split('/')
            $team = Invoke-GhApi -Path ('orgs/{0}/teams/{1}' -f $parts[0], $parts[1])
            if (-not $team.Ok) {
                throw ('The team {0} cannot be read (HTTP {1}): {2}.' -f $reviewer, $team.Status, $team.Message)
            }
            $reviewers.Add([ordered]@{ type = 'Team'; id = [long] (Get-JsonValue $team.Body 'id') })
        }
        else {
            $user = Invoke-GhApi -Path "users/$reviewer"
            if (-not $user.Ok) {
                throw ('The user {0} cannot be read (HTTP {1}): {2}.' -f $reviewer, $user.Status, $user.Message)
            }
            if ((Get-JsonValue $user.Body 'type') -ne 'User') {
                throw ('{0} is not a user; name a team as org/team-slug.' -f $reviewer)
            }
            $reviewers.Add([ordered]@{ type = 'User'; id = [long] (Get-JsonValue $user.Body 'id') })
        }
    }
    Write-Detail ('Prod reviewers: {0}.' -f ($ProdReviewers -join ', '))

    # Merge settings ------------------------------------------------------------------------------------------------

    Write-Step 'Merge settings'
    $wanted = [ordered]@{
        allow_rebase_merge     = $true
        allow_squash_merge     = $false
        allow_merge_commit     = $false
        delete_branch_on_merge = $true
        allow_auto_merge       = $true
    }
    $fields = New-Object System.Collections.Generic.List[string]
    foreach ($key in $wanted.Keys) {
        $current = Get-JsonValue $repo.Body $key
        $value = $wanted[$key]
        if ($current -ne $value) {
            $fields.Add(('{0}={1}' -f $key, $value.ToString().ToLowerInvariant()))
            Write-Detail ('{0}: {1} -> {2}' -f $key, $current, $value)
        }
    }
    if ($fields.Count -eq 0) {
        Write-Detail 'Rebase merging only, branches deleted on merge, auto-merge allowed: already set.'
    }
    elseif ($PSCmdlet.ShouldProcess($Repository, ('Set ' + ($fields -join ', ')))) {
        $patch = Invoke-GhApi -Path "repos/$Repository" -Method 'PATCH' -Fields $fields.ToArray()
        if ($patch.Ok) {
            Write-Detail 'Set.'
        }
        else {
            $problems.Add(('The merge settings were not set (HTTP {0}): {1}.' -f $patch.Status, $patch.Message))
        }
    }

    # Ruleset -------------------------------------------------------------------------------------------------------

    Write-Step ('Ruleset "{0}" ({1})' -f $rulesetName, $rulesetRelativePath)
    $planExplanation = 'Rulesets are not available on {0}: GitHub answered "{1}". A private repository of a ' +
        'personal account needs GitHub Pro for rulesets and branch protection, and an organization has them on ' +
        'GitHub Team or Enterprise. Upgrade the plan, move the repository to its organization, or make it public, ' +
        'then run this script again. Until then main is not protected: anyone with write access can push to it.'
    $list = Invoke-GhApi -Path "repos/$Repository/rulesets?includes_parents=false&per_page=100"
    if (-not $list.Ok) {
        if ($list.Status -eq 403 -and (Test-PlanRefusal $list.Message)) {
            $problems.Add(($planExplanation -f $Repository, $list.Message))
            Write-Detail 'Not available on this plan.'
        }
        else {
            $text = 'The rulesets of {0} cannot be read (HTTP {1}): {2}.'
            $problems.Add(($text -f $Repository, $list.Status, $list.Message))
        }
    }
    else {
        $existing = $null
        foreach ($item in @($list.Body)) {
            if ($null -eq $item) { continue }
            if ((Get-JsonValue $item 'name') -eq $rulesetName -and
                (Get-JsonValue $item 'source_type') -eq 'Repository') {
                $existing = $item
            }
        }
        if ($existing) {
            $id = Get-JsonValue $existing 'id'
            $action = 'Update ruleset {0} to the rules in {1}' -f $id, $rulesetRelativePath
            $method = 'PUT'
            $apiPath = "repos/$Repository/rulesets/$id"
        }
        else {
            $action = 'Create the ruleset from {0}' -f $rulesetRelativePath
            $method = 'POST'
            $apiPath = "repos/$Repository/rulesets"
        }
        if ($PSCmdlet.ShouldProcess(('{0} ruleset "{1}"' -f $Repository, $rulesetName), $action)) {
            $write = Invoke-GhApi -Path $apiPath -Method $method -InputFile $rulesetPath
            if ($write.Ok) {
                Write-Detail ('{0}: done (ruleset {1}).' -f $action, (Get-JsonValue $write.Body 'id'))
            }
            elseif ($write.Status -eq 403 -and (Test-PlanRefusal $write.Message)) {
                $problems.Add(($planExplanation -f $Repository, $write.Message))
            }
            else {
                $problems.Add(('The ruleset was not written (HTTP {0}): {1}.' -f $write.Status, $write.Message))
            }
        }
    }

    # Environments --------------------------------------------------------------------------------------------------

    foreach ($environment in $environmentNames) {
        Write-Step ('Environment {0}' -f $environment)
        $target = '{0} environment {1}' -f $Repository, $environment
        $body = [ordered]@{
            deployment_branch_policy = [ordered]@{ protected_branches = $false; custom_branch_policies = $true }
        }
        $description = 'deploys from {0} only' -f $defaultBranch
        if ($environment -eq 'prod') {
            $body['reviewers'] = $reviewers.ToArray()
            $description += ', after a review by ' + ($ProdReviewers -join ', ')
        }

        $written = $true
        if ($PSCmdlet.ShouldProcess($target, ('Create or update: ' + $description))) {
            $file = Write-JsonFile $body
            try {
                $put = Invoke-GhApi -Path "repos/$Repository/environments/$environment" -Method 'PUT' -InputFile $file
            }
            finally {
                Remove-TempFile $file
            }
            if (-not $put.Ok -and $environment -eq 'prod') {
                $text = 'prod has no required reviewers: GitHub refused them (HTTP {0}): {1}.'
                if ($visibility -ne 'public') {
                    $text += ' On a private or internal repository, environment reviewers need GitHub Enterprise; ' +
                        'on Free, Pro and Team they exist for public repositories only.'
                }
                $text += ' Until they are set, whoever may run the deploy workflow can deploy to prod.'
                $problems.Add(($text -f $put.Status, $put.Message))
                $body.Remove('reviewers')
                $file = Write-JsonFile $body
                try {
                    $put = Invoke-GhApi -Path "repos/$Repository/environments/$environment" -Method 'PUT' -InputFile $file
                }
                finally {
                    Remove-TempFile $file
                }
            }
            if ($put.Ok) {
                Write-Detail 'Created or updated.'
            }
            else {
                $written = $false
                $text = 'The environment {0} was not written (HTTP {1}): {2}.'
                if ($visibility -ne 'public') {
                    $text += ' Environments on a private repository need GitHub Pro, Team or Enterprise.'
                }
                $problems.Add(($text -f $environment, $put.Status, $put.Message))
            }
        }
        if (-not $written) { continue }

        # Its deployment branch policies: exactly one, the default branch.
        $policies = Invoke-GhApi -Path "repos/$Repository/environments/$environment/deployment-branch-policies?per_page=100"
        $current = @()
        if ($policies.Ok) {
            $current = @(Get-JsonValue $policies.Body 'branch_policies')
        }
        elseif ($policies.Status -ne 404) {
            $text = 'The deployment branch policies of {0} cannot be read (HTTP {1}): {2}.'
            $problems.Add(($text -f $environment, $policies.Status, $policies.Message))
            continue
        }
        $hasDefault = $false
        foreach ($policy in $current) {
            if ($null -eq $policy) { continue }
            $policyName = Get-JsonValue $policy 'name'
            $policyType = Get-JsonValue $policy 'type'
            if ($null -eq $policyType) { $policyType = 'branch' }
            if ($policyName -eq $defaultBranch -and $policyType -eq 'branch') {
                $hasDefault = $true
                continue
            }
            $policyId = Get-JsonValue $policy 'id'
            if ($PSCmdlet.ShouldProcess($target, ('Remove the deployment {0} policy "{1}"' -f $policyType, $policyName))) {
                $delete = Invoke-GhApi -Method 'DELETE' `
                    -Path "repos/$Repository/environments/$environment/deployment-branch-policies/$policyId"
                if ($delete.Ok) {
                    Write-Detail ('Removed the {0} policy "{1}".' -f $policyType, $policyName)
                }
                else {
                    $text = 'The {0} policy "{1}" of {2} was not removed (HTTP {3}): {4}.'
                    $problems.Add(($text -f $policyType, $policyName, $environment, $delete.Status, $delete.Message))
                }
            }
        }
        if ($hasDefault) {
            Write-Detail ('Deploys from {0} only: already set.' -f $defaultBranch)
        }
        elseif ($PSCmdlet.ShouldProcess($target, ('Allow deployments from the branch {0}' -f $defaultBranch))) {
            $add = Invoke-GhApi -Method 'POST' -RawFields @("name=$defaultBranch", 'type=branch') `
                -Path "repos/$Repository/environments/$environment/deployment-branch-policies"
            if ($add.Ok) {
                Write-Detail ('Deploys from {0} only.' -f $defaultBranch)
            }
            else {
                $text = 'The environment {0} was not restricted to {1} (HTTP {2}): {3}.'
                $problems.Add(($text -f $environment, $defaultBranch, $add.Status, $add.Message))
            }
        }
    }

    # Deploy method -------------------------------------------------------------------------------------------------

    if ($PSBoundParameters.ContainsKey('DeployMethod')) {
        Write-Step 'Deploy method'
        $variable = Invoke-GhApi -Path "repos/$Repository/actions/variables/DEPLOY_METHOD"
        $currentMethod = 'unset'
        if ($variable.Ok) { $currentMethod = Get-JsonValue $variable.Body 'value' }
        if ($currentMethod -ceq $DeployMethod) {
            Write-Detail ('DEPLOY_METHOD is {0}: already set.' -f $DeployMethod)
        }
        elseif ($PSCmdlet.ShouldProcess(('{0} variable DEPLOY_METHOD' -f $Repository),
                ('Set to {0} (now {1})' -f $DeployMethod, $currentMethod))) {
            $set = Invoke-Native -FilePath 'gh' -Arguments @('variable', 'set', 'DEPLOY_METHOD', '--body', $DeployMethod,
                '--repo', $Repository)
            if ($set.ExitCode -eq 0) {
                Write-Detail ('DEPLOY_METHOD is {0}.' -f $DeployMethod)
            }
            else {
                $problems.Add(('DEPLOY_METHOD was not set: {0}' -f $set.Error.Trim()))
            }
        }
    }
}
finally {
    $env:GH_PROMPT_DISABLED = $previousPrompt
}

Write-Host ''
if ($problems.Count -gt 0) {
    Write-Host 'Not everything is in place:' -ForegroundColor Yellow
    foreach ($problem in $problems) {
        Write-Host ('  - ' + $problem) -ForegroundColor Yellow
    }
    exit 1
}
if ($WhatIfPreference) {
    Write-Host 'Nothing was changed (-WhatIf).' -ForegroundColor Green
}
else {
    $text = '{0} is set up: main takes reviewed pull requests, and dev, test and prod deploy from {1}.'
    Write-Host ($text -f $Repository, $defaultBranch) -ForegroundColor Green
}
exit 0
