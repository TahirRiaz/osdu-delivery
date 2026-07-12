// Deploys one SQLFlow worker pool as a Container App: `sqlflow worker`, the pull-based drain loop over the
// durable run queue. Workers expose nothing (no ingress; outbound SQL to the catalog plus outbound git for
// SHA-pinned materialization) and scale 0..N on QUEUE DEPTH through the built-in KEDA mssql scaler, so an idle
// pool costs nothing. Because runs are pinned to the repo's synced commit at enqueue, a cold-started replica
// needs only its environment: it claims, materializes the pinned commit, executes, and reports back.
//
// This is the Container Apps mirror of deploy/k8s/worker-pool.yaml: deploy one copy per pool (set name and
// pool together). Secrets come from an existing Key Vault, read by the app's user-assigned managed identity;
// that same identity resolves ${keyvault:...} references at run time (SQLFLOW_AZURE_AUTH=mi).
//
//   az deployment group create -g <rg> -f worker.bicep \
//     -p managedEnvironmentId=<env-id> image=<registry>/sqlflow-worker:latest keyVaultName=<kv>

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Name of the Container App (and the prefix for its managed identity). Deploy one app per pool, e.g. sqlflow-worker-etl.')
param name string = 'sqlflow-worker'

@description('Resource id of an existing Container Apps managed environment to host the app.')
param managedEnvironmentId string

@description('Container image reference, e.g. myregistry.azurecr.io/sqlflow-worker:latest')
param image string

@description('Name of an existing Key Vault holding the catalog connection (and any flow) secrets.')
param keyVaultName string

@description('Key Vault secret name for the catalog ADO.NET connection string.')
param catalogConnectionSecretName string = 'sqlflow-catalog-db'

@description('The pool this deployment serves (single pool name, used for both SQLFLOW_WORKER_POOL and the scale query). Empty drains untargeted runs only.')
param pool string = ''

@description('Key Vault secret name for a token for private git remotes (SHA-pinned materialization). Leave empty when remotes are public.')
param gitTokenSecretName string = ''

@description('Username paired with the git token when the host requires one (Bitbucket app passwords take the account username, repository access tokens take x-token-auth; GitHub ignores it). Empty sends the token alone.')
param gitUsername string = ''

@description('Flow environment references, one object per \${env:...} reference the pool\'s flows use: { name: the environment variable, secretName: the Key Vault secret holding its value }. Credentials live on the node, never in the control plane.')
param flowEnv array = []

@description('Name of a container registry in THIS resource group: the template grants the app identity AcrPull on it and configures the pull. Leave empty for a public registry, or one you authorize yourself via acrLoginServer.')
param acrName string = ''

@description('Login server of a registry outside this resource group (grant AcrPull to the app identity yourself). Ignored when acrName is set.')
param acrLoginServer string = ''

@description('Key Vault secret name for a Go-driver-compatible catalog connection string used ONLY by the KEDA scale rule (a go-mssqldb URL, sqlserver://user:urlencoded-pw@host:port?database=...). The KEDA mssql scaler is not .NET SqlClient: it does not strip the single quotes an ADO.NET connection string puts around a password, so the .NET catalog connection cannot be reused for the scaler. Leave empty to reuse catalogConnectionSecretName for the scaler (correct only when that password needs no quoting).')
param scalerConnectionSecretName string = ''

@description('Upper bound for queue-depth scale out (one queued run per replica).')
@minValue(1)
param maxReplicas int = 10

@description('vCPU per replica, as a string for exact decimals. Must form a valid Container Apps consumption pair with memory.')
param cpu string = '1.0'

@description('Memory per replica, paired with cpu.')
param memory string = '2.0Gi'

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

// Constructed from the vault name (not read from the resource) so the flowEnvSecrets loop below stays
// computable at the start of the deployment, which Bicep requires of for-bodies in variables.
var vaultUri = 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/'
var registryServer = !empty(acrName) ? acr.properties.loginServer : acrLoginServer

// Secrets are pulled from Key Vault by the app's managed identity; their values never appear here. The KEDA
// scaler reads the same Key Vault backed secret for its connection.
var baseSecrets = [
  {
    name: 'catalog-db'
    keyVaultUrl: '${vaultUri}secrets/${catalogConnectionSecretName}'
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

var flowEnvSecrets = [for (entry, i) in flowEnv: {
  name: 'flow-env-${i}'
  keyVaultUrl: '${vaultUri}secrets/${entry.secretName}'
  identity: identity.id
}]

// A distinct secret for the KEDA scale rule when the scaler needs a different (Go-driver) connection string
// than the .NET container. When unset, the scaler falls back to the container's catalog-db secret.
var scalerSecrets = empty(scalerConnectionSecretName) ? [] : [
  {
    name: 'catalog-scaler'
    keyVaultUrl: '${vaultUri}secrets/${scalerConnectionSecretName}'
    identity: identity.id
  }
]
var scalerSecretRef = empty(scalerConnectionSecretName) ? 'catalog-db' : 'catalog-scaler'

var baseEnv = [
  {
    name: 'SQLFLOW_CATALOG_DB'
    secretRef: 'catalog-db'
  }
  // Empty = untargeted runs only, matching the scale query below.
  {
    name: 'SQLFLOW_WORKER_POOL'
    value: pool
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

// Every ${env:...} reference the pool's flows use resolves HERE, on the node.
var flowEnvVars = [for (entry, i) in flowEnv: {
  name: entry.name
  secretRef: 'flow-env-${i}'
}]

// One queued run per replica, the same query as deploy/k8s/worker-pool.yaml: an untargeted worker drains
// untargeted runs, a pooled worker drains its pool.
var queueDepthQuery = empty(pool)
  ? 'SELECT COUNT(*) FROM [catalog].[Run] WHERE [Status] = \'queued\' AND [TargetPool] IS NULL'
  : 'SELECT COUNT(*) FROM [catalog].[Run] WHERE [Status] = \'queued\' AND [TargetPool] = \'${pool}\''

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
      // No ingress: the worker has no inbound surface at all.
      registries: empty(registryServer) ? [] : [
        {
          server: registryServer
          identity: identity.id
        }
      ]
      secrets: concat(baseSecrets, gitTokenSecrets, flowEnvSecrets, scalerSecrets)
    }
    template: {
      // Let an in-flight run finish on scale-in or revision swap; an interrupted one is requeued anyway.
      terminationGracePeriodSeconds: 600
      containers: [
        {
          name: 'worker'
          image: image
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: concat(baseEnv, gitTokenEnv, gitUsernameEnv, flowEnvVars)
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'queue-depth'
            custom: {
              type: 'mssql'
              metadata: {
                query: queueDepthQuery
                targetValue: '1'
              }
              auth: [
                {
                  secretRef: scalerSecretRef
                  triggerParameter: 'connectionString'
                }
              ]
            }
          }
        ]
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

@description('The client id of the worker identity (grant it access to the data its pool\'s flows touch).')
output identityClientId string = identity.properties.clientId

@description('The principal (object) id of the worker identity, for role assignments made outside this template.')
output identityPrincipalId string = identity.properties.principalId
