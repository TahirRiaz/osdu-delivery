# Rebuilds .sqlflow\env for local dev, reading an estate's values straight from Azure.
#
#   powershell -ExecutionPolicy Bypass -File osdu\tools\dev-setup.ps1 -ResourceGroup <rg> -ControlPlaneApp <app> -WorkerApp <app>
#
# Run this once, and again whenever a connection string or key rotates in that estate. It needs
# 'az login' with access to the resource group.
#
# WHAT THIS PUTS ON YOUR MACHINE: the estate's catalog connection, its JWT signing key (which mints
# tokens that estate accepts), its git token, and the pre and ingestion database connections, all in
# cleartext in the git-ignored .sqlflow\env. Everything you then run locally acts on those real
# databases. Point it at a development estate of its own where there is one; the production estate
# needs -IUnderstandThisIsProduction, so that it is a decision each time rather than the default
# (go-live map SEC-3, DEC-7).
#
# WHY IT READS CONTAINER APP SECRETS, NOT KEY VAULT: the Key Vault copies of these connection
# strings have gone stale in both host and password before, naming a managed instance that no
# longer exists. Using them gets a connection that fails with a misleading "transient failure ...
# consider EnableRetryOnFailure" error. The container apps' own secrets are what the running system
# uses, so they are the only trustworthy source.
[CmdletBinding()]
param(
    # The estate to read. The defaults are the production estate, which is why naming it is not enough on its own.
    [string]$ResourceGroup = "datawarehouse-west-rg-prod-v2",
    [string]$ControlPlaneApp = "sqlflow-v3-control-plane",
    [string]$WorkerApp = "sqlflow-v3-worker",
    # Required when the estate named above is a production one: the acknowledgement that its credentials
    # and its data are what this machine will be working with.
    [switch]$IUnderstandThisIsProduction,
    # Overwrites an existing .sqlflow\env. Without it an existing file is left alone, so a working local
    # setup is never replaced by a run meant for another estate.
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$rg = $ResourceGroup
$app = $ControlPlaneApp
$worker = $WorkerApp

# A name with 'prod' in it is the production estate until someone says otherwise. The check is on the
# name because the name is what an operator reads before pressing enter.
$looksLikeProduction = ($rg -match "prod") -or ($app -match "prod") -or ($worker -match "prod")
if ($looksLikeProduction -and -not $IUnderstandThisIsProduction) {
    Write-Error @"
'$rg' looks like the production estate, and this script would copy its catalog connection, its JWT
signing key, its git token and its database connections onto this machine in cleartext, after which
everything you run locally acts on the real databases.

Point it at a development estate:
    -ResourceGroup <dev-rg> -ControlPlaneApp <dev-app> -WorkerApp <dev-worker>

or say that production is what you mean:
    -IUnderstandThisIsProduction
"@
}
# The SPA app registration backing the GUI's Microsoft sign-in button, looked up by display name.
$entraAppName = "OSDU Delivery GUI"
# osdu\tools -> osdu -> the repository root.
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$envPath = Join-Path $repoRoot ".sqlflow\env"

Write-Host "Reading live configuration from Azure ..."
az account show 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "Not logged in to Azure. Run: az login" }

function Get-AppSecret([string]$name) {
    $v = az containerapp secret show -n $app -g $rg --secret-name $name --query value -o tsv 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($v)) { Write-Error "Could not read container app secret '$name'." }
    return $v
}
function Get-WorkerSecret([string]$name) {
    $v = az containerapp secret show -n $worker -g $rg --secret-name $name --query value -o tsv 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($v)) { Write-Error "Could not read worker secret '$name'." }
    return $v
}

$catalog = Get-AppSecret "catalog-db"
$jwt     = Get-AppSecret "jwt-signing-key"
$git     = Get-AppSecret "git-token"
# From the WORKER app: it is the thing that actually runs the pre, ingestion and OSDU flows.
$pre     = Get-WorkerSecret "pre-conn"
$ing     = Get-WorkerSecret "dwh-conn"

# Issuer/audience must match the estate's, or a token minted by the cloud GUI is rejected locally.
$issuer   = az containerapp show -n $app -g $rg --query "properties.template.containers[0].env[?name=='ControlPlane__Jwt__Issuer'].value | [0]" -o tsv
$audience = az containerapp show -n $app -g $rg --query "properties.template.containers[0].env[?name=='ControlPlane__Jwt__Audience'].value | [0]" -o tsv

# Entra SSO for the GUI's "Sign in with Microsoft" button. Discovered, not hardcoded: the tenant is whichever one
# 'az login' is on, and the SPA app registration is found by name. SSO is additive, so a tenant without that
# registration is not an error: the button simply stays hidden and username/password still works.
$entraTenant = az account show --query tenantId -o tsv
$entraClient = az ad app list --filter "displayName eq '$entraAppName'" --query "[0].appId" -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($entraClient)) {
    $entraBlock = @"
# --- Entra SSO: OFF ---
# No '$entraAppName' app registration was found in tenant $entraTenant, so the GUI offers username/password only.
# Create a single-tenant SPA registration of that name with http://localhost:5173 as a redirect URI to turn it on.
"@
} else {
    $entraBlock = @"
# --- Entra SSO: the GUI's "Sign in with Microsoft" button ---
# Turns itself on because both ids are present; there is no separate Enabled flag to set. The registration is a
# SPA whose redirect URI is http://localhost:5173, matching the origin MSAL asks for (window.location.origin), so
# the GUI must be on exactly that port for the popup to come back.
#
# FIRST Entra sign-in PROVISIONS THE USER just-in-time, with DefaultRole (viewer), IN THE SHARED CATALOG: it is a
# real row in the real estate, not a local one. Raise the role from the GUI's user admin afterwards if needed.
ControlPlane__AzureAd__AllowedTenantIds__0=$entraTenant
ControlPlane__AzureAd__ClientId=$entraClient
"@
}

New-Item -ItemType Directory -Force -Path (Split-Path $envPath) | Out-Null

$content = @"
# OSDU Delivery local dev. NEVER COMMITTED: .sqlflow/ is git-ignored.
# GENERATED by osdu\tools\dev-setup.ps1 from the live estate. Re-run it after any secret rotation.
#
# Everything RUNS on this machine; every resource is the REAL estate: the same catalog, the same
# pre and ingestion databases, the same storage. You are debugging the live system, locally.
#
# EVERY value comes from CONTAINER APP SECRETS, never Key Vault: the Key Vault copies have been
# stale in both host and password before.

# --- Catalog: the real catalog, byte-identical to what the container app uses. It also holds the
# --- OSDU module's osdu schema, so the module database needs no connection of its own here.
SQLFLOW_CATALOG_DB=$catalog
ControlPlane__Catalog__ConnectionReference=$catalog

# The catalog EXISTS and is SHARED, so never let a local run provision one.
ControlPlane__Bootstrap__AllowCreate=false

# Do NOT migrate the shared databases from a laptop by default. Local code is usually ahead of what
# is deployed, and applying an unfinished migration here breaks the running container apps: they are
# on older code and would hit columns they do not know about.
# dev.bat applies pending migrations explicitly through 'db migrate'; flip this to true only when
# deliberately testing startup migration, accepting that it hits the estate.
ControlPlane__Bootstrap__ApplyMigrations=false

# No admin bootstrap: the shared catalog already has your users, so your normal GUI login works.
# Setting AdminUsername/AdminPasswordReference would try to re-provision the admin in the SHARED
# catalog and could rewrite the estate's admin password.

# --- One process: API + scheduler + dispatcher + in-process node, all under one debugger ---
ControlPlane__Worker__Enabled=true

# Vite's origin. The allowlist defaults to EMPTY, and empty means no CORS at all, so without this
# every GUI call fails with a CORS error.
ControlPlane__Cors__AllowedOrigins__0=http://localhost:5173

# Same signing key/issuer/audience as the estate, so tokens are interchangeable.
ControlPlane__Jwt__SigningKey=$jwt
ControlPlane__Jwt__Issuer=$issuer
ControlPlane__Jwt__Audience=$audience

$entraBlock

# --- Cloud auth: Key Vault and storage go through your 'az login' ---
# This is what makes `${keyvault:...} refs in flow YAML resolve locally with no extra config.
SQLFLOW_AZURE_AUTH=cli

# --- The delivery chain's databases, under the fixed names every flow document references:
# --- pre-ingestion lands source files in PRE, the ingestion flow loads the keyed tables into DWH,
# --- and the OSDU flow reads those tables.
SQLFLOW_CONN_PRE=$pre
SQLFLOW_CONN_DWH=$ing

# --- Repo sync ---
SQLFLOW_GIT_USERNAME=x-bitbucket-api-token-auth
SQLFLOW_GIT_TOKEN=$git
"@

if ((Test-Path $envPath) -and -not $Force) {
    Write-Error "$envPath already exists. Re-run with -Force to replace it, after checking which estate it is for."
}

Set-Content -Path $envPath -Value $content -Encoding utf8
Write-Host "Wrote $envPath"
Write-Host ""
Write-Host "  Estate:   $rg ($app, $worker)$(if ($looksLikeProduction) { '  [PRODUCTION]' })"
Write-Host "  Catalog:  $(($catalog -split ';' | Where-Object { $_ -match '^(Server|Database|Initial Catalog)=' }) -join '; ')"
Write-Host "  On this machine now: the catalog connection, the JWT signing key, the git token, and the pre and ingestion connections."
Write-Host ""
Write-Host "Next:  dev.bat"
