#requires -Version 5.1
<#
    The environments of OSDU Delivery as osdu/deploy/terraform/environments defines them: <name>.tfvars (the estate and
    its Terraform variables) and <name>.backend.hcl (where its Terraform state lives). When OSDU_ENVIRONMENT_DIR names a
    directory, an environment's two files are read from there instead, and the repository's copies may stay as their
    placeholders: that is how an environment's values are kept out of the repository, on a workstation and in the
    GitHub Deploy workflow (which writes them there from its environment's variables). The one reader of those files,
    for deploy.ps1 (and through it deploy-dev.ps1, deploy-test.ps1, deploy-prod.ps1 and the GitHub Deploy workflow) and
    tools/github/connect-environment.ps1; and the one way those scripts call az.
#>

Set-StrictMode -Version Latest

function Read-OsduEnvironmentFile {
    <#
        The top-level string and boolean assignments of a .tfvars or .hcl file (an environment names its estate with
        them), and every line still holding a <placeholder>, which stops everything: an environment file is filled in
        once, by hand, and a placeholder reaching Azure or Terraform is a deploy to nowhere.
    #>
    param([Parameter(Mandatory)][string] $Path)

    $values = @{}
    $placeholders = @()
    $number = 0
    foreach ($line in Get-Content -LiteralPath $Path) {
        $number++
        if ($line -match '^\s*(#|//)') { continue }
        if ($line -match '<[^<>"]+>') { $placeholders += "  ${Path}:${number}: $($line.Trim())" }
        if ($line -match '^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*"([^"]*)"') { $values[$Matches[1]] = $Matches[2] }
        elseif ($line -match '^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(true|false)\s*(#.*)?$') { $values[$Matches[1]] = $Matches[2] }
    }

    return [pscustomobject]@{ Values = $values; Placeholders = $placeholders }
}

function Get-OsduEnvironment {
    <#
        Environment $Name: its files, its Terraform variables, its state location, and the estate they name (the
        subscription, the resource group, the registry, and the prefix its apps share). Throws, naming the file and
        what to do, when the environment does not exist, names no estate, or still holds placeholders (those of the
        backend file only when -WithBackend: only Terraform reads it).
    #>
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $RepoRoot,
        [switch] $WithBackend
    )

    if ($Name -notmatch '^[a-z][a-z0-9-]{0,30}$') {
        throw "-Environment '$Name' is not an environment name: lower-case letters, digits and hyphens, as the files in osdu/deploy/terraform/environments are named."
    }

    # Where the environment's files are: the directory OSDU_ENVIRONMENT_DIR names, or the repository's.
    $override = [Environment]::GetEnvironmentVariable('OSDU_ENVIRONMENT_DIR')
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        if (-not (Test-Path -LiteralPath $override -PathType Container)) {
            throw "OSDU_ENVIRONMENT_DIR names '$override', which is not a directory; it holds the environment's $Name.tfvars and $Name.backend.hcl."
        }
        $directory = (Resolve-Path -LiteralPath $override).ProviderPath
        $shown = "OSDU_ENVIRONMENT_DIR ($directory)"
    }
    else {
        $directory = Join-Path $RepoRoot 'osdu/deploy/terraform/environments'
        $shown = 'osdu/deploy/terraform/environments'
    }
    $varFile = Join-Path $directory "$Name.tfvars"
    $backendFile = Join-Path $directory "$Name.backend.hcl"
    if (-not (Test-Path -LiteralPath $varFile)) {
        $known = @(Get-ChildItem -LiteralPath $directory -Filter '*.tfvars' -ErrorAction SilentlyContinue | ForEach-Object { $_.BaseName })
        throw "No environment '$Name': $shown has no $Name.tfvars. Known: $($known -join ', ')."
    }
    if ($WithBackend -and -not (Test-Path -LiteralPath $backendFile)) {
        throw "$shown has no $Name.backend.hcl: it names where the environment's Terraform state lives."
    }

    $variables = Read-OsduEnvironmentFile -Path $varFile
    $placeholders = @($variables.Placeholders)
    $backend = @{}
    if ($WithBackend) {
        $read = Read-OsduEnvironmentFile -Path $backendFile
        $backend = $read.Values
        $placeholders += @($read.Placeholders)
    }
    if ($placeholders.Count -gt 0) {
        throw ("Environment '$Name' is not filled in yet; replace each <placeholder> first, or give the environment its " +
            "files from outside the repository with OSDU_ENVIRONMENT_DIR:`n$($placeholders -join "`n")")
    }

    $values = $variables.Values
    foreach ($key in 'subscription_id', 'resource_group_name', 'acr_name') {
        if (-not $values.ContainsKey($key) -or [string]::IsNullOrWhiteSpace($values[$key])) {
            throw "$Name.tfvars in $shown sets no $key; an environment names its subscription_id, resource_group_name and acr_name."
        }
    }

    # The app names Terraform gives the apps, which share a prefix: osdu-delivery-<app> unless the file renames them.
    $prefix = 'osdu-delivery-'
    if ($values.ContainsKey('control_plane_name')) {
        if ($values['control_plane_name'] -notmatch '^(.+-)control-plane$') {
            throw "control_plane_name '$($values['control_plane_name'])' in $Name.tfvars does not end in control-plane, so the other apps' names cannot be found from it."
        }
        $prefix = $Matches[1]
    }
    foreach ($pair in @(@('worker_name', 'worker'), @('gui_name', 'gui'), @('mcp_name', 'mcp'))) {
        if ($values.ContainsKey($pair[0]) -and $values[$pair[0]] -ne "$prefix$($pair[1])") {
            throw "$($pair[0]) '$($values[$pair[0]])' in $Name.tfvars is not $prefix$($pair[1]): the apps of an environment share one name prefix."
        }
    }

    return [pscustomobject]@{
        Name          = $Name
        Source        = $shown
        FromOutside   = -not [string]::IsNullOrWhiteSpace($override)
        VarFile       = $varFile
        BackendFile   = $backendFile
        Variables     = $values
        Backend       = $backend
        Subscription  = $values['subscription_id']
        ResourceGroup = $values['resource_group_name']
        Registry      = $values['acr_name']
        AppPrefix     = $prefix
        McpEnabled    = ($values['mcp_enabled'] -eq 'true') -or -not [string]::IsNullOrEmpty($values['mcp_image'])
    }
}

function Invoke-Az {
    <#
        Every az call of the deploy scripts goes through this. PowerShell 5.1 turns ANY stderr output from a native
        executable into a NativeCommandError, and under $ErrorActionPreference='Stop' that kills the script even when az
        succeeded (az writes progress, warnings, and stray blank lines to stderr routinely). So stderr is captured apart
        from stdout, which alone is the command's answer (JSON a caller parses), and success is judged by the exit code
        alone. Errors holds what az wrote to stderr, for a failure's message.
    #>
    param([Parameter(Mandatory)][string[]] $AzArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $all = & az @AzArgs 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    $stdout = @($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    $stderr = @($all | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    return [pscustomobject]@{ Output = $stdout; Errors = $stderr; ExitCode = $code }
}

Export-ModuleMember -Function Get-OsduEnvironment, Invoke-Az
