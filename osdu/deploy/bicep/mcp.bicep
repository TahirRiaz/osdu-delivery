// Deploys the OSDU Delivery MCP server in HTTP mode as a Container App: `osdu-delivery-mcp http`, the streamable
// HTTP endpoint remote MCP clients connect to (the image built from osdu/deploy/docker/mcp.Dockerfile). The chat
// assistant's model in Azure AI Foundry is the primary consumer, but any MCP client that can send an Authorization
// header works.
//
// The app holds NO credentials: every /mcp request must carry a SQLFlow bearer token (a person's, or the one the
// control plane delegates to the model for a single run), which the server checks with the control plane and
// forwards per tool call, so authorization always happens where the data lives. Ingress is external because
// Foundry's runtime calls in from Microsoft-managed compute; the mandatory bearer plus TLS is the access control.
// /healthz serves the platform probes without auth.
//
//   az deployment group create -g <rg> -f mcp.bicep \
//     -p managedEnvironmentId=<env-id> image=<registry>/osdu-delivery-mcp:latest controlPlaneUrl=https://... \
//        guiUrl=https://osdu-delivery-gui.<env-domain> allowedHosts=osdu-delivery-mcp.<env-domain>
//
// The same template as SQLFlow's (sqlflow/deploy/bicep/mcp.bicep), with the server's host allowlist added.

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Name of the Container App.')
param name string = 'osdu-delivery-mcp'

@description('Resource id of an existing Container Apps managed environment to host the app. The app runs on its Consumption workload profile.')
param managedEnvironmentId string

@description('Container image reference, e.g. myregistry.azurecr.io/osdu-delivery-mcp:latest')
param image string

@description('Base URL of the control plane the MCP tools proxy, e.g. https://osdu-delivery-control-plane.<env-domain>.')
param controlPlaneUrl string

@description('Public base URL of the GUI, e.g. https://osdu-delivery-gui.<env-domain>. Tool results carry deep links into it (a record\'s page, a submission\'s, a run\'s) so an assistant can hand the reader something to open. Empty emits root-relative links, which only resolve for a client rendering inside the GUI itself.')
param guiUrl string = ''

@description('The host names the server answers on, comma separated (SQLFLOW_MCP_HTTP_ALLOWED_HOSTS): its own ingress FQDN, osdu-delivery-mcp.<env-domain>. A request naming any other host is refused, which keeps a rebound DNS name from reaching it. Empty accepts any host.')
param allowedHosts string = ''

@description('Name of a container registry in THIS resource group: the template grants the app identity AcrPull on it and configures the pull. Leave empty for a public registry, or one you authorize yourself via acrLoginServer.')
param acrName string = ''

@description('Login server of a registry outside this resource group (grant AcrPull to the app identity yourself). Ignored when acrName is set.')
param acrLoginServer string = ''

@description('vCPU per replica; the server is a thin authenticated proxy, so the smallest consumption pair suffices.')
param cpu string = '0.25'

@description('Memory per replica, paired with cpu.')
param memory string = '0.5Gi'

@description('Tags set on the app and its identity.')
param tags { *: string } = {}

// The AcrPull built-in role, for managed-identity image pull from a same-group registry.
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-id'
  location: location
  tags: tags
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

var registryServer = !empty(acrName) ? acr.properties.loginServer : acrLoginServer

var allowedHostsEnv = empty(allowedHosts) ? [] : [
  {
    name: 'SQLFLOW_MCP_HTTP_ALLOWED_HOSTS'
    value: allowedHosts
  }
]

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: managedEnvironmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: empty(registryServer) ? [] : [
        {
          server: registryServer
          identity: identity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'osdu-delivery-mcp'
          image: image
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: concat([
            {
              name: 'SQLFLOW_CONTROL_PLANE_URL'
              value: controlPlaneUrl
            }
            {
              name: 'SQLFLOW_MCP_HTTP_BIND'
              value: '0.0.0.0:8080'
            }
            {
              name: 'SQLFLOW_GUI_URL'
              value: guiUrl
            }
          ], allowedHostsEnv)
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              initialDelaySeconds: 2
              periodSeconds: 3
              failureThreshold: 10
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              periodSeconds: 15
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              periodSeconds: 10
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        // Exactly one replica: MCP sessions (Mcp-Session-Id) live in this process's memory, and the streamable HTTP
        // transport has no session affinity across replicas. One small replica comfortably serves the assistant's
        // workload; scaling out would need a shared session store, not more copies.
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    acrPull
  ]
}

@description('The MCP endpoint URL to register with clients (the control plane\'s Assistant:Mcp:ServerUrl).')
output mcpUrl string = 'https://${app.properties.configuration.ingress.fqdn}/mcp'

@description('The principal (object) id of the app identity, for role assignments made outside this template.')
output identityPrincipalId string = identity.properties.principalId
