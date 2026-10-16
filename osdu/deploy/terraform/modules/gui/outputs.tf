output "gui_url" {
  description = "The GUI URL: sign in here with the bootstrap admin."
  value       = "https://${azurerm_container_app.this.ingress[0].fqdn}"
}
