variable "name" {
  description = "Name of the Container App (and the prefix for its managed identity)."
  type        = string
  default     = "osdu-delivery-gui"
}

variable "resource_group_name" {
  description = "Resource group the app and its identity are created in."
  type        = string
}

variable "location" {
  description = "Azure region of the identity (the app follows its environment)."
  type        = string
}

variable "container_app_environment_id" {
  description = "Resource id of the Container Apps managed environment hosting the app."
  type        = string
}

variable "image" {
  description = "Container image reference, e.g. myregistry.azurecr.io/osdu-delivery-gui:latest"
  type        = string
}

variable "api_base_url" {
  description = "The control plane origin the SPA calls, e.g. https://osdu-delivery-control-plane.<env>.azurecontainerapps.io. Empty means same-origin, valid only behind a path-splitting front."
  type        = string
}

variable "acr_id" {
  description = "Resource id of a container registry the app identity is granted AcrPull on. Empty for a public registry, or one you authorize yourself."
  type        = string
  default     = ""
}

variable "acr_login_server" {
  description = "Login server of the registry the app pulls its image from with its identity (the acr_id registry's, or one you authorize yourself). Empty for a public registry."
  type        = string
  default     = ""
}

variable "min_replicas" {
  description = "Minimum replicas. Static content: one is enough, more buys ingress resilience."
  type        = number
  default     = 1
}

variable "max_replicas" {
  description = "Maximum replicas for ingress autoscale."
  type        = number
  default     = 3
}

variable "tags" {
  description = "Tags set on the app and its identity."
  type        = map(string)
  default     = {}
}
