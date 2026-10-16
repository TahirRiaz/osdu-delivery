output "endpoint" {
  description = "The account endpoint callers use (token-authenticated when granted Cognitive Services User)."
  value       = azurerm_cognitive_account.this.endpoint
}

output "project_endpoint" {
  description = "The Foundry project endpoint the control plane's assistant connects to."
  value       = "https://${var.name}.services.ai.azure.com/api/projects/${azurerm_cognitive_account_project.this.name}"
}

output "account_name" {
  description = "The account name, for later model deployments or diagnostics."
  value       = azurerm_cognitive_account.this.name
}

output "project_name" {
  description = "The project name under the account."
  value       = azurerm_cognitive_account_project.this.name
}

output "model_deployment_name" {
  description = "The model deployment name the control plane references, or empty when no model was deployed."
  value       = var.model_name == "" ? "" : local.model_deployment_name
}

output "transcription_deployment_name" {
  description = "The audio-transcription deployment name, or empty when none was deployed."
  value       = var.transcription_model_name
}
