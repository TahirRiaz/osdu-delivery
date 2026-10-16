output "control_plane_base_url" {
  description = "The control plane base URL: point CLI remotes (SQLFLOW_URL), the nodes and any external scheduler at this."
  value       = "https://${azurerm_container_app.this.ingress[0].fqdn}"
}

output "identity_client_id" {
  description = "The client id of the app identity (grant it access to flow secrets beyond this estate's vault)."
  value       = azurerm_user_assigned_identity.this.client_id
}

output "identity_principal_id" {
  description = "The principal (object) id of the app identity, for role assignments made outside this module."
  value       = azurerm_user_assigned_identity.this.principal_id
}

output "identity_name" {
  description = "Name of the app identity: the user to create in each database it uses under Entra authentication (CREATE USER [<name>] FROM EXTERNAL PROVIDER)."
  value       = azurerm_user_assigned_identity.this.name
}
