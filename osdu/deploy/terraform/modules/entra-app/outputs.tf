output "client_id" {
  description = "Client (application) id of the registration: the control plane's AzureAd ClientId."
  value       = azuread_application.this.client_id
}

output "service_principal_object_id" {
  description = "Object id of the enterprise application (service principal)."
  value       = azuread_service_principal.this.object_id
}

output "app_role_value" {
  description = "Value of the app role that gates sign-in; appears in the token roles claim for assigned users."
  value       = "SqlFlow.User"
}
