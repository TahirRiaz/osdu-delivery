// Deploys an Azure AI Foundry resource: the Cognitive Services account kind that carries Foundry projects,
// plus one project, with the given principals granted the Cognitive Services User role so they call the
// endpoint keylessly via Entra. Set modelName (and modelVersion) to pin a model deployment here; the Slack
// assistant depends on one, and anything else that needs a specific model and version should add its own.
//
//   az deployment group create -g <rg> -f ai-foundry.bicep -p name=<globally-unique-name>

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Account name, which is also the endpoint subdomain (lowercase letters, digits, hyphens; globally unique).')
param name string

@description('Name of the Foundry project created under the account.')
param projectName string = 'default'

@description('Display name of the project. Defaults to the project name.')
param projectDisplayName string = projectName

@description('Principal (object) ids granted the Cognitive Services User role on the account, e.g. the managed identities of apps that will call models.')
param userPrincipalIds array = []

@description('Principal (object) ids granted the Azure AI User role on the account: the data-plane role Foundry Agent Service callers need (create agents, threads, and runs). The Slack bot identity goes here.')
param agentPrincipalIds array = []

@description('Model to deploy under the account, e.g. gpt-5.1 (pick one the region still accepts for new deployments). Empty deploys no model.')
param modelName string = ''

@description('Publisher format of the model, e.g. OpenAI.')
param modelFormat string = 'OpenAI'

@description('Model version to pin. Empty lets the service pick the current default version.')
param modelVersion string = ''

@description('Name of the model deployment. Defaults to the model name; agents reference this name.')
param modelDeploymentName string = ''

@description('Deployment SKU: GlobalStandard suits chat workloads; use Standard for data-residency-bound regions.')
param modelSkuName string = 'GlobalStandard'

@description('Deployment capacity in thousands of tokens per minute.')
@minValue(1)
param modelCapacity int = 30

@description('Disable key-based auth so only Entra identities can call the endpoint.')
param disableLocalAuth bool = false

// The Cognitive Services User built-in role: call the endpoint, read no keys.
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'
// The Azure AI User built-in role: Foundry data-plane access, including the Agent Service.
var azureAiUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'
var effectiveModelDeploymentName = empty(modelDeploymentName) ? modelName : modelDeploymentName

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: name
  location: location
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    // The custom subdomain is what makes Entra (token) auth and project APIs addressable.
    customSubDomainName: name
    allowProjectManagement: true
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: disableLocalAuth
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: account
  name: projectName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: projectDisplayName
  }
}

// Serialized behind the project (dependsOn) because Cognitive Services rejects concurrent control-plane
// operations on one account with a 409.
resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = if (!empty(modelName)) {
  parent: account
  name: effectiveModelDeploymentName
  sku: {
    name: modelSkuName
    capacity: modelCapacity
  }
  properties: {
    model: union({
      format: modelFormat
      name: modelName
    }, empty(modelVersion) ? {} : {
      version: modelVersion
    })
  }
  dependsOn: [
    project
  ]
}

resource userAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in userPrincipalIds: {
  scope: account
  name: guid(account.id, principalId, cognitiveServicesUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

resource agentAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in agentPrincipalIds: {
  scope: account
  name: guid(account.id, principalId, azureAiUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAiUserRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

@description('The account endpoint callers use (token-authenticated when granted Cognitive Services User).')
output endpoint string = account.properties.endpoint

@description('The Foundry project endpoint, the address agent clients (the Slack bot) connect to.')
output projectEndpoint string = 'https://${name}.services.ai.azure.com/api/projects/${projectName}'

@description('The account name, for later model deployments or diagnostics.')
output accountName string = account.name

@description('The project name under the account.')
output projectName string = project.name

@description('The model deployment name agents reference, or empty when no model was deployed.')
output modelDeploymentName string = empty(modelName) ? '' : effectiveModelDeploymentName
