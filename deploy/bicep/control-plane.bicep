// Deploys the SQLFlow control plane as an always-on Azure Container App: the warm API that ADF (or any scheduler)
// triggers. It runs with a user-assigned managed identity that reads its secrets (the catalog connection and the
// JWT signing key) from Key Vault, and that SAME identity is what the engine uses to resolve ${keyvault:...}
// references at run time (SQLFLOW_AZURE_AUTH=mi) - so no secret value is ever placed in this template or in app
// configuration. Provide an existing Container Apps environment and Key Vault; everything else is created here.
//
//   az deployment group create -g <rg> -f control-plane.bicep \
//     -p managedEnvironmentId=<env-id> image=<registry>/sqlflow-control-plane:latest keyVaultName=<kv>
//
// The defaults deploy the single-app mode (in-process worker enabled, no CORS). main.bicep composes this module
// with worker.bicep and gui.bicep into the full estate: there the worker is disabled (API replicas do API work
// only) and the GUI origin is CORS-listed.

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Name of the Container App (and the prefix for its managed identity).')
param name string = 'sqlflow-control-plane'

@description('Resource id of an existing Container Apps managed environment to host the app.')
param managedEnvironmentId string

@description('Container image reference, e.g. myregistry.azurecr.io/sqlflow-control-plane:latest')
param image string

@description('Name of an existing Key Vault holding the catalog connection and JWT signing key secrets.')
param keyVaultName string

@description('Key Vault secret name for the catalog ADO.NET connection string.')
param catalogConnectionSecretName string = 'sqlflow-catalog-db'

@description('Key Vault secret name for the JWT signing key (must decode to at least 32 bytes).')
param jwtSigningKeySecretName string = 'sqlflow-jwt-signing-key'

@description('JWT issuer the control plane validates.')
param jwtIssuer string = 'sqlflow-control-plane'

@description('JWT audience the control plane validates.')
param jwtAudience string = 'sqlflow'

@description('Run the in-process worker inside the control plane (single-app mode). Set false when dedicated workers (worker.bicep) drain the run queue, so API replicas do API work only.')
param workerEnabled bool = true

@description('Browser origins allowed to call the API, e.g. the gui.bicep app URL. Leave empty when the GUI is same-origin behind a path-splitting front, or when only non-browser clients call the API.')
param corsAllowedOrigins array = []

@description('Key Vault secret name for the initial admin password (at least 12 characters). When set, bootstrap provisioning creates that admin on first start; leave empty to skip user bootstrap.')
param bootstrapAdminPasswordSecretName string = ''

@description('Username for the bootstrap admin. Only used when bootstrapAdminPasswordSecretName is set.')
param bootstrapAdminUsername string = 'admin'

@description('Key Vault secret name for a git token used by managed sync to fetch private remotes. Leave empty when every registered repo is public.')
param gitTokenSecretName string = ''

@description('Username paired with the git token when the host requires one (Bitbucket app passwords take the account username, repository access tokens take x-token-auth; GitHub ignores it). Empty sends the token alone.')
param gitUsername string = ''

@description('CIDRs of the ingress hops to trust for X-Forwarded-* headers. Leave empty to keep proxy trust off; per-client rate limiting then keys on the ingress hop address instead of the real client.')
param proxyKnownNetworks array = []

@description('Name of a container registry in THIS resource group: the template grants the app identity AcrPull on it and configures the pull. Leave empty for a public registry, or one you authorize yourself via acrLoginServer.')
param acrName string = ''

@description('Login server of a registry outside this resource group (grant AcrPull to the app identity yourself). Ignored when acrName is set.')
param acrLoginServer string = ''

@description('Minimum replicas. Keep at 1 so the API is warm (a trigger never waits on a cold start).')
@minValue(1)
param minReplicas int = 1

@description('Maximum replicas for ingress autoscale.')
param maxReplicas int = 3

// The Key Vault Secrets User built-in role, so the app's identity can read the configured secrets.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
// The AcrPull built-in role, for managed-identity image pull from a same-group registry.
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-id'
  location: location
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource keyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, identity.id, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Only referenced when acrName is set; the placeholder name is never resolved (ARM evaluates the branch lazily).
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: empty(acrName) ? 'unused' : acrName
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(acrName)) {
  scope: acr
  name: guid(resourceGroup().id, acrName, identity.id, acrPullRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

var vaultUri = 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/'
var registryServer = !empty(acrName) ? acr.properties.loginServer : acrLoginServer

// Secrets are pulled from Key Vault by the app's managed identity; their values never appear here.
var baseSecrets = [
  {
    name: 'catalog-db'
    keyVaultUrl: '${vaultUri}secrets/${catalogConnectionSecretName}'
    identity: identity.id
  }
  {
    name: 'jwt-signing-key'
    keyVaultUrl: '${vaultUri}secrets/${jwtSigningKeySecretName}'
    identity: identity.id
  }
]

var bootstrapSecrets = empty(bootstrapAdminPasswordSecretName) ? [] : [
  {
    name: 'bootstrap-admin-password'
    keyVaultUrl: '${vaultUri}secrets/${bootstrapAdminPasswordSecretName}'
    identity: identity.id
  }
]

var gitTokenSecrets = empty(gitTokenSecretName) ? [] : [
  {
    name: 'git-token'
    keyVaultUrl: '${vaultUri}secrets/${gitTokenSecretName}'
    identity: identity.id
  }
]

var baseEnv = [
  // The catalog connection; the control plane's default ConnectionReference (${env:SQLFLOW_CATALOG_DB})
  // reads exactly this variable, so no further config is needed.
  {
    name: 'SQLFLOW_CATALOG_DB'
    secretRef: 'catalog-db'
  }
  // Resolve ${keyvault:...}/cloud-storage credentials at run time via this managed identity.
  {
    name: 'SQLFLOW_AZURE_AUTH'
    value: 'mi'
  }
  {
    name: 'AZURE_CLIENT_ID'
    value: identity.properties.clientId
  }
  {
    name: 'ControlPlane__Jwt__SigningKey'
    secretRef: 'jwt-signing-key'
  }
  {
    name: 'ControlPlane__Jwt__Issuer'
    value: jwtIssuer
  }
  {
    name: 'ControlPlane__Jwt__Audience'
    value: jwtAudience
  }
  {
    name: 'ControlPlane__Worker__Enabled'
    value: workerEnabled ? 'true' : 'false'
  }
]

var bootstrapEnv = empty(bootstrapAdminPasswordSecretName) ? [] : [
  {
    name: 'ControlPlane__Bootstrap__AdminUsername'
    value: bootstrapAdminUsername
  }
  {
    name: 'ControlPlane__Bootstrap__AdminPasswordReference'
    secretRef: 'bootstrap-admin-password'
  }
]

var gitTokenEnv = empty(gitTokenSecretName) ? [] : [
  {
    name: 'SQLFLOW_GIT_TOKEN'
    secretRef: 'git-token'
  }
]

var gitUsernameEnv = empty(gitUsername) ? [] : [
  {
    name: 'SQLFLOW_GIT_USERNAME'
    value: gitUsername
  }
]

var corsEnv = [for (origin, i) in corsAllowedOrigins: {
  name: 'ControlPlane__Cors__AllowedOrigins__${i}'
  value: origin
}]

var proxyNetworkEnv = [for (cidr, i) in proxyKnownNetworks: {
  name: 'ControlPlane__Proxy__KnownNetworks__${i}'
  value: cidr
}]

// Proxy trust is all-or-nothing: enabling it while trusting no networks fails startup validation, so the
// enable switch rides on the network list being non-empty.
var proxyEnv = empty(proxyKnownNetworks) ? [] : concat([
  {
    name: 'ControlPlane__Proxy__Enabled'
    value: 'true'
  }
], proxyNetworkEnv)

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: managedEnvironmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: empty(registryServer) ? [] : [
        {
          server: registryServer
          identity: identity.id
        }
      ]
      secrets: concat(baseSecrets, bootstrapSecrets, gitTokenSecrets)
    }
    template: {
      containers: [
        {
          name: 'control-plane'
          image: image
          resources: {
            cpu: json('0.5')
            memory: '1.0Gi'
          }
          env: concat(baseEnv, bootstrapEnv, gitTokenEnv, gitUsernameEnv, corsEnv, proxyEnv)
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
  // The app resolves its Key Vault secrets (and pulls the image) at creation, so the role grants must exist
  // first. ARM drops the acrPull entry when that resource's condition is false.
  dependsOn: [
    keyVaultAccess
    acrPull
  ]
}

@description('The control plane base URL to give the ADF pipeline as controlPlaneBaseUrl.')
output controlPlaneBaseUrl string = 'https://${app.properties.configuration.ingress.fqdn}'

@description('The client id of the app identity (grant it db_datareader/writer on the catalog + access to flow secrets).')
output identityClientId string = identity.properties.clientId

@description('The principal (object) id of the app identity, for role assignments made outside this template.')
output identityPrincipalId string = identity.properties.principalId
