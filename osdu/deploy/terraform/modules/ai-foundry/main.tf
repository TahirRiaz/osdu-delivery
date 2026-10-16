# An Azure AI Foundry resource for OSDU Delivery's chat assistant, the Terraform mirror of
# osdu/deploy/bicep/ai-foundry.bicep: the Cognitive Services account kind that carries Foundry projects, plus one
# project, with the given principals granted access so they call the endpoint keylessly through Entra. Set model_name
# (and model_version) to deploy the model the assistant answers with; it must serve the Responses API, which the
# control plane calls with its MCP tool.

locals {
  # The Cognitive Services User built-in role: call the endpoint, read no keys.
  cognitive_services_user_role_id = "a97b65f3-24c7-4388-baec-2e87135dc908"
  # The Cognitive Services OpenAI User built-in role: model inference, including the OpenAI Responses API the chat
  # assistant drives its MCP tool through. Read-only over the data plane, no key access.
  cognitive_services_openai_user_role_id = "5e0bd9bd-7b93-4f28-af87-19fc36ad61bd"
  model_deployment_name                  = var.model_deployment_name == "" ? var.model_name : var.model_deployment_name
}

resource "azurerm_cognitive_account" "this" {
  name                = var.name
  location            = var.location
  resource_group_name = var.resource_group_name
  kind                = "AIServices"
  sku_name            = "S0"
  # The custom subdomain is what makes Entra (token) auth and project APIs addressable.
  custom_subdomain_name         = var.name
  project_management_enabled    = true
  public_network_access_enabled = true
  local_auth_enabled            = !var.disable_local_auth
  tags                          = var.tags

  identity {
    type = "SystemAssigned"
  }
}

resource "azurerm_cognitive_account_project" "this" {
  name                 = var.project_name
  cognitive_account_id = azurerm_cognitive_account.this.id
  location             = var.location
  display_name         = coalesce(var.project_display_name, var.project_name)
  tags                 = var.tags

  identity {
    type = "SystemAssigned"
  }
}

# Serialized behind the project because Cognitive Services rejects concurrent control-plane operations on one account
# with a 409.
resource "azurerm_cognitive_deployment" "model" {
  count = var.model_name != "" ? 1 : 0

  name                 = local.model_deployment_name
  cognitive_account_id = azurerm_cognitive_account.this.id

  model {
    format  = var.model_format
    name    = var.model_name
    version = var.model_version == "" ? null : var.model_version
  }

  sku {
    name     = var.model_sku_name
    capacity = var.model_capacity
  }

  depends_on = [azurerm_cognitive_account_project.this]
}

# Also serialized (behind the chat model deployment) for the same 409 reason.
resource "azurerm_cognitive_deployment" "transcription" {
  count = var.transcription_model_name != "" ? 1 : 0

  name                 = var.transcription_model_name
  cognitive_account_id = azurerm_cognitive_account.this.id

  model {
    format  = var.model_format
    name    = var.transcription_model_name
    version = var.transcription_model_version == "" ? null : var.transcription_model_version
  }

  sku {
    name     = var.model_sku_name
    capacity = var.transcription_model_capacity
  }

  depends_on = [
    azurerm_cognitive_account_project.this,
    azurerm_cognitive_deployment.model,
  ]
}

# Counted, not keyed: the principal ids are often identities created in the same apply, unknown until then, while how
# many there are is always known.
resource "azurerm_role_assignment" "user" {
  count = length(var.user_principal_ids)

  scope              = azurerm_cognitive_account.this.id
  role_definition_id = "${regex("^/subscriptions/[^/]+", azurerm_cognitive_account.this.id)}/providers/Microsoft.Authorization/roleDefinitions/${local.cognitive_services_user_role_id}"
  principal_id       = var.user_principal_ids[count.index]
  principal_type     = "ServicePrincipal"
}

resource "azurerm_role_assignment" "agent" {
  count = length(var.agent_principal_ids)

  scope              = azurerm_cognitive_account.this.id
  role_definition_id = "${regex("^/subscriptions/[^/]+", azurerm_cognitive_account.this.id)}/providers/Microsoft.Authorization/roleDefinitions/${local.cognitive_services_openai_user_role_id}"
  principal_id       = var.agent_principal_ids[count.index]
  principal_type     = "ServicePrincipal"
}
