variable "name" {
  description = "Name of the Container App (and the prefix for its managed identity)."
  type        = string
  default     = "osdu-delivery-mcp"
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
  description = "Container image reference, e.g. myregistry.azurecr.io/osdu-delivery-mcp:latest"
  type        = string
}

variable "control_plane_url" {
  description = "Base URL of the control plane the MCP tools proxy, e.g. https://osdu-delivery-control-plane.<env-domain>."
  type        = string
}

variable "gui_url" {
  description = "Public base URL of the GUI, e.g. https://osdu-delivery-gui.<env-domain>. Tool results carry deep links into it (a record's page, a submission's, a run's). Empty emits root-relative links, which only resolve for a client rendering inside the GUI itself."
  type        = string
  default     = ""
}

variable "allowed_hosts" {
  description = "The host names the server answers on, comma separated (SQLFLOW_MCP_HTTP_ALLOWED_HOSTS): its own ingress FQDN, osdu-delivery-mcp.<env-domain>. A request naming any other host is refused, which keeps a rebound DNS name from reaching it. Empty accepts any host."
  type        = string
  default     = ""
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

variable "cpu" {
  description = "vCPU per replica; the server is a thin authenticated proxy, so the smallest consumption pair suffices."
  type        = number
  default     = 0.25
}

variable "memory" {
  description = "Memory per replica, paired with cpu."
  type        = string
  default     = "0.5Gi"
}

variable "tags" {
  description = "Tags set on the app and its identity."
  type        = map(string)
  default     = {}
}
