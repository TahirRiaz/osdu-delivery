<#
.SYNOPSIS
    The steps of an OSDU Delivery deployment that need a person's rights rather than an app's, run inside the
    deployment itself by its setup identity, so a deployment needs no step by hand after it: the same script for the
    Terraform estate (osdu/deploy/terraform) and the Bicep one (osdu/deploy/bicep, the Marketplace package).

.DESCRIPTION
    Runs as an Azure deployment script (AzurePowerShell) under the estate's setup identity, a user-assigned managed
    identity the deployment creates: Key Vault Secrets Officer on the estate's vault, and the Entra administrator of the
    SQL server the deployment creates.

    -Step Secrets (before the apps are created): writes each secret the deployment generates (the JWT signing key, the
    initial admin's password, the node credential the workers present) into the vault when the vault does not hold it,
    and never changes one it holds, so a value rotated by hand stays. No generated value leaves the container: not in
    an output, not in a log line, not in Terraform state.

    -Step Grants (once the apps' identities exist): connects to each database as the setup identity and creates the
    database users the apps sign in as (each app as its own managed identity, and the people the deployment names), with
    the roles each needs. Every statement can run again; running the step on every deployment keeps the grants as the
    deployment says.

    The step's input is SETUP_SPEC, one JSON document the deployment builds:

        {
          "vault": "<vault name>",
          "generated": [ { "name": "<secret name>", "kind": "signing-key | password | node-token" } ],
          "sql": {
            "server": "<server FQDN>",
            "databases": [
              {
                "name": "<database>",
                "users": [ { "name": "<user name>", "id": "<client id of an identity, object id of a person or group>", "type": "E | X" } ],
                "statements": [ "<T-SQL run after the users exist>" ]
              }
            ]
          }
        }

    "type" is E for an identity or a person and X for a group (CREATE USER ... WITH SID, TYPE), so no directory lookup
    is needed and the setup identity needs no Microsoft Graph permission. "sql" is null when the deployment does not
    administer the server (an existing server, or SQL authentication).

.PARAMETER Step
    Secrets or Grants.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Secrets', 'Grants')]
    [string] $Step
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# How long a step keeps trying through what settles on its own after a deployment: a role assignment the vault does not
# honour yet, an SQL administrator the server does not know yet.
$SettleBudget = [TimeSpan]::FromMinutes(10)

function Write-Step([string] $Message) {
    # The deployment script's log; never its outputs, which carry only what the step reports by name.
    Write-Host "[$Step] $Message"
}

function Get-Spec {
    $json = $env:SETUP_SPEC
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw 'SETUP_SPEC is not set: the deployment passes the setup step its input as one JSON document.'
    }

    return $json | ConvertFrom-Json -Depth 20
}

function Invoke-Settled([string] $What, [scriptblock] $Action) {
    # Runs $Action until it succeeds or the settle budget is spent, waiting longer each time; the last error is the
    # one reported, with what was being done.
    $deadline = [DateTime]::UtcNow + $SettleBudget
    $delay = 5
    while ($true) {
        try {
            return & $Action
        }
        catch {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "$What did not succeed within $([int]$SettleBudget.TotalMinutes) minutes: $($_.Exception.Message)"
            }

            Write-Step "$What is not possible yet ($($_.Exception.Message)); trying again in $delay s."
            Start-Sleep -Seconds $delay
            $delay = [Math]::Min($delay * 2, 60)
        }
    }
}

function New-Secret([string] $Kind) {
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48)
    switch ($Kind) {
        'signing-key' {
            # 384 bits, well above the 256 an HS256 key needs.
            return [Convert]::ToBase64String($bytes)
        }
        'password' {
            # 32 letters and digits: about 190 bits, typed nowhere, read from the vault when a person needs it.
            $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
            $chars = for ($i = 0; $i -lt 32; $i++) { $alphabet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($alphabet.Length)] }
            return -join $chars
        }
        'node-token' {
            # SQLFlow's own token form: sqlf_ and 43 base64url characters (256 bits).
            $token = [Convert]::ToBase64String($bytes[0..31]).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            return "sqlf_$token"
        }
        default {
            throw "SETUP_SPEC names a generated secret of kind '$Kind'; the kinds are signing-key, password and node-token."
        }
    }
}

function Invoke-Secrets($Spec) {
    if ([string]::IsNullOrWhiteSpace($Spec.vault)) {
        throw 'SETUP_SPEC names no vault.'
    }

    $kept = @()
    $written = @()
    foreach ($secret in @($Spec.generated)) {
        $name = [string] $secret.name
        $existing = Invoke-Settled "Reading secret '$name' in vault '$($Spec.vault)'" {
            try {
                Get-AzKeyVaultSecret -VaultName $Spec.vault -Name $name -ErrorAction Stop
            }
            catch {
                # A secret the vault does not hold is the answer, not a failure to retry.
                if ($_.Exception.Message -match 'not found|NotFound|SecretNotFound|\(404\)') {
                    return $null
                }

                throw
            }
        }

        if ($null -ne $existing) {
            $kept += $name
            continue
        }

        $value = ConvertTo-SecureString (New-Secret ([string] $secret.kind)) -AsPlainText -Force
        Invoke-Settled "Writing secret '$name' in vault '$($Spec.vault)'" {
            try {
                Set-AzKeyVaultSecret -VaultName $Spec.vault -Name $name -SecretValue $value -ContentType 'generated by the deployment' -ErrorAction Stop | Out-Null
            }
            catch {
                # A secret deleted earlier is kept by the vault's soft delete under its name; it is the deployment's
                # own, so it is taken back rather than left blocking the name.
                if ($_.Exception.Message -match 'deleted but recoverable') {
                    Undo-AzKeyVaultSecretRemoval -VaultName $Spec.vault -Name $name -ErrorAction Stop | Out-Null
                    return
                }

                throw
            }
        } | Out-Null
        $written += $name
    }

    Write-Step "kept $(if ($kept.Count) { $kept -join ', ' } else { 'none' }); generated $(if ($written.Count) { $written -join ', ' } else { 'none' })."
    return @{ generated = ($written -join ','); kept = ($kept -join ',') }
}

function Get-SqlToken {
    $token = (Get-AzAccessToken -ResourceUrl 'https://database.windows.net/' -ErrorAction Stop).Token
    if ($token -is [securestring]) {
        return [System.Net.NetworkCredential]::new('', $token).Password
    }

    return [string] $token
}

function ConvertTo-SqlSid([string] $Id) {
    # The SID of an Entra principal is its id's bytes in the order .NET writes a Guid.
    $guid = [Guid]::Parse($Id)
    return '0x' + [Convert]::ToHexString($guid.ToByteArray())
}

function ConvertTo-SqlName([string] $Name) {
    return '[' + $Name.Replace(']', ']]') + ']'
}

function ConvertTo-SqlString([string] $Value) {
    return "N'" + $Value.Replace("'", "''") + "'"
}

function Test-SqlClient {
    # PowerShell 7 carries System.Data.SqlClient; when the container's does not, the SqlServer module's Invoke-Sqlcmd
    # takes the same access token.
    if ($null -ne ('System.Data.SqlClient.SqlConnection' -as [type])) {
        return $true
    }

    try {
        Add-Type -AssemblyName System.Data.SqlClient -ErrorAction Stop
        return $true
    }
    catch {
        if (-not (Get-Module -ListAvailable -Name SqlServer)) {
            Write-Step 'System.Data.SqlClient is not available; installing the SqlServer module for Invoke-Sqlcmd.'
            Install-Module -Name SqlServer -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop
        }

        Import-Module SqlServer -ErrorAction Stop
        return $false
    }
}

function Invoke-SqlBatch([string] $Server, [string] $Database, [string[]] $Statements, [bool] $SqlClient) {
    Invoke-Settled "Connecting to database '$Database' on '$Server' as the setup identity" {
        $token = Get-SqlToken
        if (-not $SqlClient) {
            foreach ($statement in $Statements) {
                Invoke-Sqlcmd -ServerInstance "tcp:$Server,1433" -Database $Database -AccessToken $token -Query $statement -QueryTimeout 120 -Encrypt Mandatory -ErrorAction Stop | Out-Null
            }

            return
        }

        $connection = [System.Data.SqlClient.SqlConnection]::new("Server=tcp:$Server,1433;Initial Catalog=$Database;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30")
        try {
            $connection.AccessToken = $token
            $connection.Open()
            foreach ($statement in $Statements) {
                $command = $connection.CreateCommand()
                try {
                    $command.CommandText = $statement
                    $command.CommandTimeout = 120
                    [void] $command.ExecuteNonQuery()
                }
                finally {
                    $command.Dispose()
                }
            }
        }
        finally {
            $connection.Dispose()
        }
    } | Out-Null
}

function Invoke-Grants($Spec) {
    if ($null -eq $Spec.sql) {
        Write-Step 'the deployment administers no SQL server here; nothing to grant.'
        return @{ databases = '' }
    }

    $server = [string] $Spec.sql.server
    $sqlClient = Test-SqlClient
    $done = @()
    foreach ($database in @($Spec.sql.databases)) {
        $statements = @()
        foreach ($user in @($database.users)) {
            if ($user.type -notin 'E', 'X') {
                throw "SETUP_SPEC gives user '$($user.name)' type '$($user.type)'; a user is E (an identity or a person) or X (a group)."
            }

            $statements += "IF USER_ID($(ConvertTo-SqlString $user.name)) IS NULL CREATE USER $(ConvertTo-SqlName $user.name) WITH SID = $(ConvertTo-SqlSid $user.id), TYPE = $($user.type);"
        }

        $statements += @($database.statements | ForEach-Object { [string] $_ })
        Invoke-SqlBatch -Server $server -Database ([string] $database.name) -Statements $statements -SqlClient $sqlClient
        $done += [string] $database.name
        Write-Step "granted in $($database.name): $(@($database.users).Count) user(s), $(@($database.statements).Count) statement(s)."
    }

    return @{ databases = ($done -join ',') }
}

$spec = Get-Spec
$DeploymentScriptOutputs = switch ($Step) {
    'Secrets' { Invoke-Secrets $spec }
    'Grants' { Invoke-Grants $spec }
}
