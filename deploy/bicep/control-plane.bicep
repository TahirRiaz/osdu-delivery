// Deploys the SQLFlow control plane as an always-on Azure Container App: the warm API that ADF (or any scheduler)
// triggers. It runs with a user-assigned managed identity that reads its secrets (the catalog connection and the
// JWT signing key) from Key Vault, and that SAME identity is what the engine uses to resolve ${keyvault:...}
// references at run time (SQLFLOW_AZURE_AUTH=mi) - so no secret value is ever placed in this template or in app
// configuration. Provide an existing Container Apps environment and Key Vault; everything else is created here.
//
//   az deployment group create -g <rg> -f control-plane.bicep \
//     -p managedEnvironmentId=<env-id> image=<registry>/sqlflow-control-plane:latest keyVaultName=<kv>

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

@description('ACR login server for managed-identity image pull (leave blank for a public registry).')
param acrLoginServer string = ''

@description('Minimum replicas. Keep at 1 so the API is warm (a trigger never waits on a cold start).')
@minValue(1)
param minReplicas int = 1

@description('Maximum replicas for ingress autoscale.')
param maxReplicas int = 3

// The Key Vault Secrets User built-in role, so the app's identity can read the configured secrets.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

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

var vaultUri = keyVault.properties.vaultUri

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
      registries: empty(acrLoginServer) ? [] : [
        {
          server: acrLoginServer
          identity: identity.id
        }
      ]
      // Secrets are pulled from Key Vault by the app's managed identity; their values never appear here.
      secrets: [
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
          env: [
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
          ]
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
}

@description('The control plane base URL to give the ADF pipeline as controlPlaneBaseUrl.')
output controlPlaneBaseUrl string = 'https://${app.properties.configuration.ingress.fqdn}'

@description('The client id of the app identity (grant it db_datareader/writer on the catalog + access to flow secrets).')
output identityClientId string = identity.properties.clientId
