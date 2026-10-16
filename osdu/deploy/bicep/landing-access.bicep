// Grants the estate's app identities their access to the landing storage account the flows read their source files
// from: the worker's identity may read and write it (a flow that moves a file it has read), the control plane's read it
// (a person's preview of a flow). main.bicep deploys it into the account's own resource group, so an account adopted
// from elsewhere is granted exactly as one created beside the estate.
//
//   az deployment group create -g <account's resource group> -f landing-access.bicep \
//     -p accountName=<account> workerPrincipalId=<id> controlPlanePrincipalId=<id>

@description('Name of an existing storage account in the resource group this template is deployed to.')
param accountName string

@description('Principal (object) id of the worker identity, granted Storage Blob Data Contributor on the account.')
param workerPrincipalId string

@description('Principal (object) id of the control plane identity, granted Storage Blob Data Reader on the account.')
param controlPlanePrincipalId string

// The Storage Blob Data Contributor and Storage Blob Data Reader built-in roles.
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageBlobDataReaderRoleId = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: accountName
}

resource workerAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: account
  name: guid(account.id, workerPrincipalId, storageBlobDataContributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource controlPlaneAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: account
  name: guid(account.id, controlPlanePrincipalId, storageBlobDataReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReaderRoleId)
    principalId: controlPlanePrincipalId
    principalType: 'ServicePrincipal'
  }
}
