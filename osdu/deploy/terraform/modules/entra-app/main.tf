# The Microsoft Entra app users sign in to OSDU Delivery with, the Terraform mirror of
# osdu/deploy/bicep/entra-app.bicep, so the estate's single sign-on is managed as code instead of by hand:
#
#   application         an SPA registration with the GUI origin as its redirect URI, carrying one app role,
#                       SqlFlow.User, that gates who may sign in. Single-tenant by default; multi-tenant when a second
#                       organization also needs to sign in (sign_in_audience).
#   service principal   the enterprise application, with app role assignment required: Entra then refuses to issue a
#                       token to anyone not assigned the app role, so "any tenant user" becomes "only assigned members".
#   role assignment     the SqlFlow.User role for one security group in THIS app's home tenant, when its object id is
#                       given. Every member of that group (and only them) can sign in.
#
# The role value stays SqlFlow.User: changing it on a live registration would revoke every existing assignment. The
# deploying identity needs directory write access (Application Administrator or Application.ReadWrite.All), and
# assigning a GROUP to the role needs Microsoft Entra ID P1. With assignment required and no group id, NO ONE can sign
# in until members are assigned: access is denied until a group is named, never open to the whole tenant.

locals {
  # A deterministic id for the app role, stable across applies, so a re-run updates the same role rather than churning
  # a new one. Roles are keyed by id inside the application, so this must not change once members are assigned.
  app_role_id = uuidv5("url", "https://osdu-delivery.invalid/${var.unique_name}/app-roles/SqlFlow.User")

  # Microsoft Graph's well-known app id, and the id of its User.Read delegated permission: the baseline consent a
  # sign-in SPA needs so the account can read its own profile.
  microsoft_graph_app_id = "00000003-0000-0000-c000-000000000000"
  user_read_scope_id     = "e1fe6dd8-ba31-4d61-89e7-88639da4683d"

  # Entra stores a redirect URI that has no path with a trailing slash, and treats the two spellings as one when MSAL
  # signs in from window.location.origin; the provider insists on the stored spelling, so an origin gets its slash.
  redirect_uri = can(regex("^https?://[^/]+$", var.redirect_uri)) ? "${var.redirect_uri}/" : var.redirect_uri
}

resource "azuread_application" "this" {
  display_name     = var.display_name
  sign_in_audience = var.sign_in_audience

  single_page_application {
    redirect_uris = [local.redirect_uri]
  }

  app_role {
    id                   = local.app_role_id
    allowed_member_types = ["User"]
    display_name         = "OSDU Delivery User"
    description          = "May sign in to OSDU Delivery. Assignment is required, so only members of this role reach the app."
    value                = "SqlFlow.User"
    enabled              = true
  }

  required_resource_access {
    resource_app_id = local.microsoft_graph_app_id

    resource_access {
      id   = local.user_read_scope_id
      type = "Scope"
    }
  }
}

resource "azuread_service_principal" "this" {
  client_id = azuread_application.this.client_id
  # The gate: Entra issues a token only to users assigned an app role on this enterprise application.
  app_role_assignment_required = true
}

# Assign the whole group to the SqlFlow.User role in one grant. Entra expands group membership at sign-in, so adding
# or removing a person from the group changes their access with no apply.
resource "azuread_app_role_assignment" "group" {
  count = var.allowed_group_object_id != "" ? 1 : 0

  app_role_id         = local.app_role_id
  principal_object_id = var.allowed_group_object_id
  resource_object_id  = azuread_service_principal.this.object_id
}
