output "mcp_url" {
  description = "The MCP endpoint URL to register with clients (the control plane's Assistant:Mcp:ServerUrl)."
  value       = "https://${azurerm_container_app.this.ingress[0].fqdn}/mcp"
}

output "identity_principal_id" {
  description = "The principal (object) id of the app identity, for role assignments made outside this module."
  value       = azurerm_user_assigned_identity.this.principal_id
}
