output "identity_client_id" {
  description = "The client id of the node identity (grant it access to the ingestion database, the payload storage and the OSDU credentials its pool's flows touch)."
  value       = azurerm_user_assigned_identity.this.client_id
}

output "identity_principal_id" {
  description = "The principal (object) id of the node identity, for role assignments made outside this module."
  value       = azurerm_user_assigned_identity.this.principal_id
}

output "identity_name" {
  description = "Name of the app identity: the user to create in each database it uses under Entra authentication (CREATE USER [<name>] FROM EXTERNAL PROVIDER)."
  value       = azurerm_user_assigned_identity.this.name
}
