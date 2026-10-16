variable "display_name" {
  description = "Display name of the app registration as it appears in Entra and on the consent prompt."
  type        = string
  default     = "OSDU Delivery"
}

variable "unique_name" {
  description = "Stable name the app role id is derived from. Lowercase, no spaces."
  type        = string
  default     = "osdu-delivery"
}

variable "redirect_uri" {
  description = "The exact GUI origin to register as the SPA redirect URI, e.g. https://osdu-delivery-gui.<env>.azurecontainerapps.io. MSAL signs in against window.location.origin, so this is the origin with no trailing path."
  type        = string
}

variable "allowed_group_object_id" {
  description = "Object id of the Entra security group whose members may sign in; they are assigned the SqlFlow.User role. Empty leaves the app with assignment required but no members. Only reaches users in THIS app's home tenant."
  type        = string
  default     = ""
}

variable "sign_in_audience" {
  description = "AzureADMyOrg (default) restricts sign-in to this app's home tenant. AzureADMultipleOrgs also allows any tenant that has consented; the control plane's AllowedTenantIds is the actual gate on which of those are trusted."
  type        = string
  default     = "AzureADMyOrg"

  validation {
    condition     = contains(["AzureADMyOrg", "AzureADMultipleOrgs"], var.sign_in_audience)
    error_message = "sign_in_audience must be AzureADMyOrg or AzureADMultipleOrgs."
  }
}
