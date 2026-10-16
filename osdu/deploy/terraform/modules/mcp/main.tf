# The OSDU Delivery MCP server in HTTP mode as a Container App, the Terraform mirror of osdu/deploy/bicep/mcp.bicep:
# `osdu-delivery-mcp http`, the streamable HTTP endpoint remote MCP clients connect to (the image built from
# osdu/deploy/docker/mcp.Dockerfile). The chat assistant's model in Azure AI Foundry is the primary consumer, but any
# MCP client that can send an Authorization header works.
#
# The app holds NO credentials: every /mcp request must carry a SQLFlow bearer token (a person's, or the one the
# control plane delegates to the model for a single run), which the server checks with the control plane and forwards
# per tool call, so authorization always happens where the data lives. Ingress is external because Foundry's runtime
# calls in from Microsoft-managed compute; the mandatory bearer plus TLS is the access control. /healthz serves the
# platform probes without auth.

locals {
  acr_pull_role_id = "7f951dda-4ed3-4680-a7ca-43fe172d538d"
  registry_server  = var.acr_login_server

  env = concat(
    [
      { name = "SQLFLOW_CONTROL_PLANE_URL", value = var.control_plane_url },
      { name = "SQLFLOW_MCP_HTTP_BIND", value = "0.0.0.0:8080" },
      { name = "SQLFLOW_GUI_URL", value = var.gui_url },
    ],
    var.allowed_hosts == "" ? [] : [
      { name = "SQLFLOW_MCP_HTTP_ALLOWED_HOSTS", value = var.allowed_hosts },
    ],
  )
}

resource "azurerm_user_assigned_identity" "this" {
  name                = "${var.name}-id"
  location            = var.location
  resource_group_name = var.resource_group_name
  tags                = var.tags
}

resource "azurerm_role_assignment" "acr_pull" {
  count = var.acr_id != "" ? 1 : 0

  scope              = var.acr_id
  role_definition_id = "${regex("^/subscriptions/[^/]+", var.acr_id)}/providers/Microsoft.Authorization/roleDefinitions/${local.acr_pull_role_id}"
  principal_id       = azurerm_user_assigned_identity.this.principal_id
  principal_type     = "ServicePrincipal"
}

resource "azurerm_container_app" "this" {
  name                         = var.name
  resource_group_name          = var.resource_group_name
  container_app_environment_id = var.container_app_environment_id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.this.id]
  }

  ingress {
    external_enabled           = true
    target_port                = 8080
    transport                  = "http"
    allow_insecure_connections = false

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  dynamic "registry" {
    for_each = local.registry_server == "" ? [] : [local.registry_server]
    content {
      server   = registry.value
      identity = azurerm_user_assigned_identity.this.id
    }
  }

  template {
    # Exactly one replica: MCP sessions (Mcp-Session-Id) live in this process's memory, and the streamable HTTP
    # transport has no session affinity across replicas. Scaling out would need a shared session store.
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "osdu-delivery-mcp"
      image  = var.image
      cpu    = var.cpu
      memory = var.memory

      dynamic "env" {
        for_each = local.env
        content {
          name  = env.value.name
          value = env.value.value
        }
      }

      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/healthz"
        initial_delay           = 2
        interval_seconds        = 3
        failure_count_threshold = 10
      }

      liveness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/healthz"
        interval_seconds        = 15
        failure_count_threshold = 3
      }

      readiness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/healthz"
        interval_seconds        = 10
        failure_count_threshold = 3
      }
    }
  }

  depends_on = [
    azurerm_role_assignment.acr_pull,
  ]

  # The image is set when the app is created. After that deploy.ps1 rolls it forward (and rolls it back when a revision
  # does not serve), so a plan never takes an app back to the image it was created with.
  lifecycle {
    ignore_changes = [template[0].container[0].image]
  }
}
