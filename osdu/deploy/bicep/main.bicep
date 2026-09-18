// The full OSDU Delivery estate on Azure Container Apps in one resource-group deployment: Log Analytics and the
// Container Apps environment, a Key Vault holding every secret, the Azure SQL databases the estate has (the
// catalog, the pre-ingestion landing database and the ingestion database the OSDU flow reads), and the three apps
// composed from the per-tier templates in this directory:
//
//   gui.bicep            the SPA, external ingress; calls the control plane cross-origin
//   control-plane.bicep  the API (in-process worker OFF: the API replica does API work only), CORS'd to the GUI
//   worker.bicep         one worker pool, no ingress, scaled 0..N on the control plane's replica target
//
// Data reaches OSDU through three flows in lineage order: a SQLFlow pre-ingestion flow lands the source files in
// the pre database, a SQLFlow ingestion flow loads the keyed ingestion tables, and the OSDU flow reads those
// tables and delivers. The two databases are wired under the fixed names ${env:SQLFLOW_CONN_PRE} and
// ${env:SQLFLOW_CONN_DWH}, so a flow document moves from test to prod unchanged.
//
// Secrets flow one way: the deployment writes them into Key Vault, each app's user-assigned managed identity
// reads them at start, and the same identities resolve ${keyvault:...} references at run time. No secret value
// appears in app configuration. The deploying principal needs the Key Vault Secrets Officer (or Key Vault
// Administrator) data-plane role to write the secrets, since the vault uses RBAC authorization.
//
// Build and push the three images first (see osdu/deploy/README.md), then:
//
//   az deployment group create -g <rg> -f main.bicep \
//     -p acrName=<registry> \
//        controlPlaneImage=<registry>.azurecr.io/osdu-delivery-control-plane:latest \
//        workerImage=<registry>.azurecr.io/osdu-delivery-worker:latest \
//        guiImage=<registry>.azurecr.io/osdu-delivery-gui:latest \
//        sqlAdminPassword=<...> jwtSigningKey=<...> adminPassword=<...>
//
// On first start, bootstrap provisioning applies SQLFlow's catalog migrations and then the OSDU module's, and
// creates the admin user, so the GUI is sign-in ready at the guiUrl output. The nodes need a node-scoped personal
// access token, which can only be minted once the control plane is up, so a first deployment leaves nodeToken
// empty and re-runs with it. Add worker pools by deploying worker.bicep again with a distinct name and pool.

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Container image reference for the control plane, e.g. myregistry.azurecr.io/osdu-delivery-control-plane:latest')
param controlPlaneImage string

@description('Container image reference for the worker, e.g. myregistry.azurecr.io/osdu-delivery-worker:latest')
param workerImage string

@description('Container image reference for the GUI, e.g. myregistry.azurecr.io/osdu-delivery-gui:latest')
param guiImage string

@description('Name of a container registry in THIS resource group holding the three images: each app identity is granted AcrPull on it. Leave empty for a public registry, or one you authorize yourself via acrLoginServer.')
param acrName string = ''

@description('Login server of a registry outside this resource group (grant AcrPull to the three app identities yourself). Ignored when acrName is set.')
param acrLoginServer string = ''

@description('Address of an existing SQL Server to host the databases instead of creating one: host or host,port (a Managed Instance private FQDN, a public-endpoint address with ,3342, or any reachable SQL Server). Empty creates an Azure SQL logical server here. With an existing server the network path is yours to provide (for a VNet-only Managed Instance, set infrastructureSubnetId so the apps egress inside its VNet) and the databases are provisioned out of band; only their connection secrets are wired here.')
param existingSqlServer string = ''

@description('Resource id of a vNet subnet to integrate the Container Apps environment into (consumption architecture: an undelegated subnet of at least /23). Empty deploys the environment without VNet integration.')
param infrastructureSubnetId string = ''

@description('The private ranges (CIDR, comma separated) the worker may reach: with infrastructureSubnetId set, the VNet ranges an OSDU, a storage account or a proxy behind a private endpoint resolves to. Empty reaches public addresses only.')
param privateNetworks string = ''

@description('SQL admin login: for the server created here, or the existing server\'s login when existingSqlServer is set.')
param sqlAdminLogin string = 'osdudeliveryadmin'

@secure()
@description('SQL admin password (Azure SQL enforces complexity). Single quotes and semicolons are handled; the value is quoted into the connection string.')
param sqlAdminPassword string

@secure()
@minLength(32)
@description('HS256 signing key for control plane tokens: at least 32 bytes of randomness, e.g. openssl rand -base64 48.')
param jwtSigningKey string

@description('Username of the initial admin user bootstrap provisioning creates on first start.')
param adminUsername string = 'admin'

@secure()
@minLength(12)
@description('Password for the initial admin user (at least 12 characters).')
param adminPassword string

@secure()
@description('Token for private git remotes: managed sync on the control plane fetches with it, nodes materialize with it. Leave empty when every registered flow repository is public.')
param gitToken string = ''

@secure()
@description('A personal access token minted with the node scope, which the worker pool presents to the control plane\'s dispatcher. The control plane must be running to mint one (an admin: POST /api/v1/me/tokens with scopes ["node"]), so a first deployment leaves this empty and re-runs with it once the control plane is up; until then the worker deploys without a credential and takes no work.')
param nodeToken string = ''

@description('Username paired with gitToken when the host requires one: a Bitbucket app password takes the account username, a Bitbucket repository access token takes x-token-auth; GitHub ignores it. Empty sends the token alone.')
param gitUsername string = ''

@description('Let this template create and own the Entra app registration users sign in with (App Roles + "Assignment required" baked in, so only assigned group members can sign in), through the Microsoft Graph Bicep extension. On (the default) enables "Sign in with Microsoft" and ignores azureAdAllowedTenantIds/azureAdClientId; the deploying principal then needs directory write (Application Administrator or Application.ReadWrite.All), and assigning a group needs Entra ID P1. Turn off for local-only sign-in, or to point at an externally managed registration via azureAdAllowedTenantIds + azureAdClientId.')
param provisionEntraApp bool = true

@description('Object id of the Entra security group whose members may sign in when provisionEntraApp is on. Only its members are assigned the app role and can obtain a token. Empty still enforces assignment required, so no one signs in until a group or users are assigned in the enterprise application. Reaches only this app\'s home tenant (subscription().tenantId); a tenant named in azureAdAdditionalAllowedTenantIds assigns its own users itself. Ignored when provisionEntraApp is off.')
param azureAdAllowedGroupObjectId string = ''

@description('Display name for the app registration this template creates when provisionEntraApp is on.')
param entraAppDisplayName string = 'OSDU Delivery'

@description('Stable unique name (Graph identity key) for that registration, so redeploys update the same app. Lowercase, no spaces.')
param entraAppUniqueName string = 'osdu-delivery'

@description('PROVISIONED app mode only (provisionEntraApp on): further Entra tenant (directory) ids, beyond this deployment\'s own home tenant (always trusted), whose users may also sign in. A non-empty list makes the app registration multi-tenant automatically; each named tenant\'s own admin must still consent to the app once and then assign the SqlFlow.User role to their own users/groups (this template has no directory access into a tenant it does not own); see the entraForeignTenantReminder output for that step. Empty keeps the app single-tenant.')
param azureAdAdditionalAllowedTenantIds array = []

@description('EXTERNAL app mode only (provisionEntraApp off): Microsoft Entra tenant (directory) ids allowed for GUI single sign-on, against an app registration you manage yourself. Set together with azureAdClientId to offer "Sign in with Microsoft"; leave empty for local sign-in only. Register the GUI origin (the guiUrl output) as a redirect URI on that SPA registration, and make it multi-tenant yourself if this list has more than one entry.')
param azureAdAllowedTenantIds array = []

@description('EXTERNAL app mode only (provisionEntraApp off): client id of the SPA app registration users sign in with. Set together with azureAdAllowedTenantIds to enable SSO. Ignored when provisionEntraApp is on (this template creates the registration and supplies the client id itself).')
param azureAdClientId string = ''

@description('Role a first-time SSO user is provisioned with (least privilege by default; an admin raises it afterwards in the GUI).')
param azureAdDefaultRole string = 'viewer'

@description('ADDITIONAL flow environment references beyond the built-in SQLFLOW_CONN_PRE and SQLFLOW_CONN_DWH, one object per \${env:...} reference the worker pool\'s flows use, and for the OSDU module database connection the ledger is read and written through on a node: { name: the environment variable, secretName: an EXISTING Key Vault secret in keyVaultName holding its value }, e.g. [{ name: \'OSDU_CLIENT_SECRET\', secretName: \'osdu-client-secret\' }]. OSDU credentials are put in the vault out of band and never pass through this template; the node reads them under its own identity, so they never reach the control plane.')
param workerFlowEnv array = []

@description('The pool the worker app serves. Empty takes untargeted runs only.')
param workerPool string = ''

@description('Upper bound for worker scale out.')
@minValue(1)
param workerMaxReplicas int = 10

@description('Minimum control plane replicas. Keep at 1 so the API is warm (a trigger never waits on a cold start).')
@minValue(1)
param controlPlaneMinReplicas int = 1

@description('Maximum control plane replicas. Keep at 1: the run queue is owned by exactly one replica (the dispatch lease), so an extra replica adds no dispatch capacity and refuses every node call that lands on it (503, retried by the node), which only slows hand-outs.')
@minValue(1)
param controlPlaneMaxReplicas int = 1

@description('Name of the Container App running the control plane.')
param controlPlaneName string = 'osdu-delivery-control-plane'

@description('Name of the Container App running the worker pool.')
param workerName string = 'osdu-delivery-worker'

@description('Name of the Container App running the GUI.')
param guiName string = 'osdu-delivery-gui'

@description('Name of the Container Apps managed environment.')
param environmentName string = 'osdu-delivery-env'

@description('Name of the Log Analytics workspace receiving container logs.')
param logAnalyticsName string = 'osdu-delivery-logs'

@minLength(3)
@maxLength(24)
@description('Key Vault name (globally unique, 3-24 alphanumerics and hyphens).')
param keyVaultName string = 'osdu-kv-${uniqueString(resourceGroup().id)}'

@description('Azure SQL logical server name (globally unique, lowercase).')
param sqlServerName string = 'osdu-sql-${uniqueString(resourceGroup().id)}'

@description('Name of SQLFlow\'s catalog database on that server: the metadata engine, holding pipelines, runs, schedules, lineage, sources and users.')
param catalogDatabaseName string = 'SQLFlow'

@description('Name of the OSDU Delivery module\'s database (schema `osdu`): the record ledger, mappings, templates and the OSDU cache. A database of its own by default, since an Azure SQL database cannot reach another in one statement and the ledger grows with the records, not the metadata. Name the catalog database here to keep both in one.')
param osduDatabaseName string = 'OSDUDelivery'

@description('Name of the database pre-ingestion flows land raw source files in, reachable from flow YAML as \${env:SQLFLOW_CONN_PRE}.')
param preDatabaseName string = 'OsduDeliveryPre'

@description('Name of the database ingestion flows load the keyed ingestion tables into, which the OSDU flow reads, reachable from flow YAML as \${env:SQLFLOW_CONN_DWH}.')
param ingestionDatabaseName string = 'OsduDeliveryIng'

@description('Catalog database SKU (ignored when existingSqlServer is set). It carries the metadata, the run journal and the delivery ledger: modest, but polled continuously, so avoid serverless auto-pause.')
param sqlDatabaseSku object = {
  name: 'S1'
  tier: 'Standard'
}

@description('SKU for the pre and ingestion databases (ignored when existingSqlServer is set). These carry the record data, so they are sized apart from the catalog.')
param dataDatabaseSku object = {
  name: 'S1'
  tier: 'Standard'
}

// Secret names shared with the per-tier templates (their defaults match these).
var catalogConnectionSecretName = 'osdu-delivery-catalog-db'
var osduConnectionSecretName = 'osdu-delivery-osdu-db'
var preConnectionSecretName = 'osdu-delivery-pre-db'
var ingestionConnectionSecretName = 'osdu-delivery-ing-db'
var jwtSigningKeySecretName = 'osdu-delivery-jwt-signing-key'
var adminPasswordSecretName = 'osdu-delivery-admin-password'
var gitTokenSecretName = 'osdu-delivery-git-token'
var nodeTokenSecretName = 'osdu-delivery-node-token'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    vnetConfiguration: empty(infrastructureSubnetId) ? null : {
      infrastructureSubnetId: infrastructureSubnetId
    }
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = if (empty(existingSqlServer)) {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

// The 0.0.0.0 rule is Azure's "allow Azure services" switch: the Container Apps consumption plan has no fixed
// outbound address for a narrower rule. To close the databases to the public internet, run the environment in a
// VNet and reach SQL over a private endpoint instead (then disable public access and drop this rule).
resource sqlAllowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (empty(existingSqlServer)) {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource catalogDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = if (empty(existingSqlServer)) {
  parent: sqlServer
  name: catalogDatabaseName
  location: location
  sku: sqlDatabaseSku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

// The delivery module's own database, created only when it is not the catalog's.
resource osduDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = if (empty(existingSqlServer) && osduDatabaseName != catalogDatabaseName) {
  parent: sqlServer
  name: osduDatabaseName
  location: location
  sku: sqlDatabaseSku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

// The two data databases the delivery chain has: pre stages the landed source files, ing holds the keyed
// ingestion tables the OSDU flow reads. Like the catalog they are only created when this template creates the
// server; on an existing server they are provisioned out of band and only their connection secrets are wired here.
resource preDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = if (empty(existingSqlServer)) {
  parent: sqlServer
  name: preDatabaseName
  location: location
  sku: dataDatabaseSku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

resource ingestionDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = if (empty(existingSqlServer)) {
  parent: sqlServer
  name: ingestionDatabaseName
  location: location
  sku: dataDatabaseSku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

// host,port for the connection string: the created server on the standard port, or the existing address with its
// own port when it carries one (a Managed Instance public endpoint is host,3342).
var sqlServerAddress = empty(existingSqlServer)
  ? '${sqlServer!.properties.fullyQualifiedDomainName},1433'
  : (contains(existingSqlServer, ',') ? existingSqlServer : '${existingSqlServer},1433')

// The one place each connection string exists; everything else references the vault secret. The SQL admin is used
// because logins cannot be created from ARM; switching the apps to least-privilege credentials (or
// Entra-authenticated access for their managed identities) later means updating only these secrets. The password
// is quoted (embedded single quotes doubled) so any complex value survives ADO.NET parsing.
var quotedSqlAdminPassword = '\'${replace(sqlAdminPassword, '\'', '\'\'')}\''
var catalogConnectionString = 'Server=tcp:${sqlServerAddress};Initial Catalog=${catalogDatabaseName};User ID=${sqlAdminLogin};Password=${quotedSqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30'
var osduConnectionString = 'Server=tcp:${sqlServerAddress};Initial Catalog=${osduDatabaseName};User ID=${sqlAdminLogin};Password=${quotedSqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30'
var preConnectionString = 'Server=tcp:${sqlServerAddress};Initial Catalog=${preDatabaseName};User ID=${sqlAdminLogin};Password=${quotedSqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30'
var ingestionConnectionString = 'Server=tcp:${sqlServerAddress};Initial Catalog=${ingestionDatabaseName};User ID=${sqlAdminLogin};Password=${quotedSqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30'

resource catalogDbSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: catalogConnectionSecretName
  properties: {
    value: catalogConnectionString
  }
}

resource osduDbSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: osduConnectionSecretName
  properties: {
    value: osduConnectionString
  }
}

resource preDbSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: preConnectionSecretName
  properties: {
    value: preConnectionString
  }
}

resource ingestionDbSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: ingestionConnectionSecretName
  properties: {
    value: ingestionConnectionString
  }
}

resource jwtSigningKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: jwtSigningKeySecretName
  properties: {
    value: jwtSigningKey
  }
}

resource adminPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: adminPasswordSecretName
  properties: {
    value: adminPassword
  }
}

resource gitTokenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(gitToken)) {
  parent: keyVault
  name: gitTokenSecretName
  properties: {
    value: gitToken
  }
}

resource nodeTokenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(nodeToken)) {
  parent: keyVault
  name: nodeTokenSecretName
  properties: {
    value: nodeToken
  }
}

// Each app's FQDN is <app>.<environment default domain>, so the cross-origin wiring (GUI -> API base URL,
// control plane -> CORS origin) is computed up front instead of creating a dependency cycle between the apps.
var controlPlaneFqdn = '${controlPlaneName}.${managedEnvironment.properties.defaultDomain}'
var guiFqdn = '${guiName}.${managedEnvironment.properties.defaultDomain}'

// A non-empty azureAdAdditionalAllowedTenantIds means a second organization needs to sign in too, so the
// registration itself must be multi-tenant; otherwise it stays single-tenant (the secure-by-default case).
var entraSignInAudience = empty(azureAdAdditionalAllowedTenantIds) ? 'AzureADMyOrg' : 'AzureADMultipleOrgs'

// The Entra app users sign in with: created and owned here (App Roles + assignment required) when
// provisionEntraApp is on, otherwise an externally managed registration referenced by the azureAd* params.
// The GUI origin is the SPA redirect URI, known up front from the environment's default domain.
module entraApp 'entra-app.bicep' = if (provisionEntraApp) {
  name: 'osdu-delivery-entra-app'
  params: {
    displayName: entraAppDisplayName
    uniqueName: entraAppUniqueName
    redirectUri: 'https://${guiFqdn}'
    allowedGroupObjectId: azureAdAllowedGroupObjectId
    signInAudience: entraSignInAudience
  }
}

// This deployment's own subscription tenant is always trusted when the app is provisioned here; in external mode
// the full allow-list comes from the caller. Either way this is what the control plane validates tokens against,
// and an empty client id leaves SSO off (local sign-in only).
var effectiveAzureAdAllowedTenantIds = provisionEntraApp
  ? concat([subscription().tenantId], azureAdAdditionalAllowedTenantIds)
  : azureAdAllowedTenantIds
var effectiveAzureAdClientId = provisionEntraApp ? entraApp!.outputs.clientId : azureAdClientId

module controlPlane 'control-plane.bicep' = {
  name: 'osdu-delivery-control-plane-app'
  params: {
    location: location
    name: controlPlaneName
    managedEnvironmentId: managedEnvironment.id
    image: controlPlaneImage
    keyVaultName: keyVault.name
    catalogConnectionSecretName: catalogConnectionSecretName
    osduConnectionSecretName: osduConnectionSecretName
    jwtSigningKeySecretName: jwtSigningKeySecretName
    // Compute belongs to the worker app; the API replica does API + scheduler + sync work only.
    workerEnabled: false
    // The GUI is a separate origin on Container Apps (one ingress FQDN per app).
    corsAllowedOrigins: [
      'https://${guiFqdn}'
    ]
    bootstrapAdminUsername: adminUsername
    bootstrapAdminPasswordSecretName: adminPasswordSecretName
    gitTokenSecretName: empty(gitToken) ? '' : gitTokenSecretName
    gitUsername: gitUsername
    azureAdAllowedTenantIds: effectiveAzureAdAllowedTenantIds
    azureAdClientId: effectiveAzureAdClientId
    azureAdDefaultRole: azureAdDefaultRole
    acrName: acrName
    acrLoginServer: acrLoginServer
    minReplicas: controlPlaneMinReplicas
    maxReplicas: controlPlaneMaxReplicas
  }
  // The app reads these vault secrets at creation and migrates the catalog and the OSDU module at startup.
  dependsOn: [
    catalogDbSecret
    osduDbSecret
    jwtSigningKeySecret
    adminPasswordSecret
    gitTokenSecret
    catalogDatabase
    osduDatabase
    sqlAllowAzureServices
  ]
}

// What every node reads under fixed names, so a flow document referencing ${env:SQLFLOW_CONN_PRE} or
// ${env:SQLFLOW_CONN_DWH} moves from test to prod unchanged, and the ledger is reachable wherever the module's
// database is. A node opens no catalog connection, so without SQLFLOW_OSDU_DB it validates and plans but
// delivers nothing. Caller-supplied references follow, and must not reuse these three names.
var builtInFlowEnv = [
  {
    name: 'SQLFLOW_OSDU_DB'
    secretName: osduConnectionSecretName
  }
  {
    name: 'SQLFLOW_CONN_PRE'
    secretName: preConnectionSecretName
  }
  {
    name: 'SQLFLOW_CONN_DWH'
    secretName: ingestionConnectionSecretName
  }
]

module worker 'worker.bicep' = {
  name: 'osdu-delivery-worker-app'
  params: {
    location: location
    name: workerName
    managedEnvironmentId: managedEnvironment.id
    image: workerImage
    keyVaultName: keyVault.name
    pool: workerPool
    controlPlaneUrl: 'https://${controlPlaneFqdn}'
    nodeTokenSecretName: empty(nodeToken) ? '' : nodeTokenSecretName
    gitTokenSecretName: empty(gitToken) ? '' : gitTokenSecretName
    gitUsername: gitUsername
    privateNetworks: privateNetworks
    flowEnv: concat(builtInFlowEnv, workerFlowEnv)
    acrName: acrName
    acrLoginServer: acrLoginServer
    maxReplicas: workerMaxReplicas
  }
  dependsOn: [
    preDbSecret
    ingestionDbSecret
    gitTokenSecret
    nodeTokenSecret
    preDatabase
    ingestionDatabase
    sqlAllowAzureServices
  ]
}

module gui 'gui.bicep' = {
  name: 'osdu-delivery-gui-app'
  params: {
    location: location
    name: guiName
    managedEnvironmentId: managedEnvironment.id
    image: guiImage
    apiBaseUrl: 'https://${controlPlaneFqdn}'
    acrName: acrName
    acrLoginServer: acrLoginServer
  }
}

@description('Sign in here with the bootstrap admin (adminUsername/adminPassword).')
output guiUrl string = gui.outputs.guiUrl

@description('Client id of the Entra registration users sign in with: created and owned by this template when provisionEntraApp is on, otherwise the azureAdClientId you passed. Empty means SSO is off (local sign-in only).')
output entraClientId string = effectiveAzureAdClientId

@description('The GUI origin registered as the SPA redirect URI. When provisionEntraApp is on this template already set it on the registration; in external mode add it yourself (Single-page application platform). Empty when SSO is off.')
output entraRedirectUri string = empty(effectiveAzureAdClientId) ? '' : gui.outputs.guiUrl

@description('Next step for Bicep-owned SSO: assignment is required, so no one can sign in until members are assigned. Set azureAdAllowedGroupObjectId to a security group to grant its members access, or assign users/groups in the enterprise application. Empty when a group was already assigned or SSO is off.')
output entraAssignmentReminder string = (provisionEntraApp && empty(azureAdAllowedGroupObjectId)) ? 'Assign a group via azureAdAllowedGroupObjectId (or in the enterprise application) so members can sign in; assignment is required and none are assigned yet.' : ''

@description('Next step for each tenant named in azureAdAdditionalAllowedTenantIds: this template cannot grant role assignments outside its own home tenant, so an admin in that tenant must consent to the app (creates their own local enterprise application object) and then assign the SqlFlow.User role to their own users or groups on it. Empty when no additional tenant was configured or SSO is off.')
output entraForeignTenantReminder string = (provisionEntraApp && !empty(azureAdAdditionalAllowedTenantIds))
  ? 'For each tenant in azureAdAdditionalAllowedTenantIds: an admin there must consent to app ${effectiveAzureAdClientId}, then assign the SqlFlow.User role to their own users/groups in their own enterprise application view.'
  : ''

@description('The API base URL: point CLI remotes (SQLFLOW_URL) and any external scheduler at this.')
output controlPlaneBaseUrl string = controlPlane.outputs.controlPlaneBaseUrl

@description('Client id of the control plane identity, for granting access to flow secrets beyond this vault.')
output controlPlaneIdentityClientId string = controlPlane.outputs.identityClientId

@description('Client id of the node identity, for granting access to the payload storage and the OSDU credentials its pool\'s flows use.')
output workerIdentityClientId string = worker.outputs.identityClientId

@description('SQL server address (the connection strings are in Key Vault as osdu-delivery-catalog-db, -pre-db and -ing-db).')
output sqlServerFqdn string = empty(existingSqlServer) ? sqlServer!.properties.fullyQualifiedDomainName : existingSqlServer

@description('The vault holding every estate secret; put OSDU credentials here as \${keyvault:...} targets.')
output keyVaultUri string = keyVault.properties.vaultUri
