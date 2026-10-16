# The OSDU Delivery GUI as a Container App, the Terraform mirror of osdu/deploy/bicep/gui.bicep: the built SPA served by
# unprivileged nginx. The API base URL is injected at container start (the image writes /config.json from
# SQLFLOW_API_BASE_URL), so one image serves every environment. On Container Apps each app has its own ingress FQDN, so
# the usual layout is two origins: point api_base_url at the control plane and list this app's origin in the control
# plane's CORS. Behind a path-splitting front (Front Door, Application Gateway) pass an empty api_base_url for the
# same-origin layout and drop the CORS entries.

locals {
  acr_pull_role_id = "7f951dda-4ed3-4680-a7ca-43fe172d538d"
  registry_server  = var.acr_login_server
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
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

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
    min_replicas = var.min_replicas
    max_replicas = var.max_replicas

    container {
      name   = "gui"
      image  = var.image
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "SQLFLOW_API_BASE_URL"
        value = var.api_base_url
      }

      liveness_probe {
        transport        = "HTTP"
        port             = 8080
        path             = "/"
        interval_seconds = 30
      }

      readiness_probe {
        transport        = "HTTP"
        port             = 8080
        path             = "/"
        interval_seconds = 15
      }
    }

    http_scale_rule {
      name                = "http-load"
      concurrent_requests = "200"
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
