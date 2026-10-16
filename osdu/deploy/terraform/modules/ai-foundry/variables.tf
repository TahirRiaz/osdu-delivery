variable "name" {
  description = "Account name, which is also the endpoint subdomain (lowercase letters, digits, hyphens; globally unique)."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group the account is created in."
  type        = string
}

variable "location" {
  description = "Azure region of the account and its project."
  type        = string
}

variable "project_name" {
  description = "Name of the Foundry project created under the account."
  type        = string
  default     = "osdu-delivery"
}

variable "project_display_name" {
  description = "Display name of the project. Null takes the project name."
  type        = string
  default     = null
}

variable "user_principal_ids" {
  description = "Principal (object) ids granted the Cognitive Services User role on the account, e.g. the managed identities of apps that will call models."
  type        = list(string)
  default     = []
}

variable "agent_principal_ids" {
  description = "Principal (object) ids granted the Cognitive Services OpenAI User role on the account: model inference, including the Responses API the chat assistant drives its MCP tool through. The control plane identity goes here when the assistant is on."
  type        = list(string)
  default     = []
}

variable "model_name" {
  description = "Model to deploy under the account, e.g. gpt-5.1 (one the region still accepts for new deployments). Empty deploys no model."
  type        = string
  default     = ""
}

variable "model_format" {
  description = "Publisher format of the model, e.g. OpenAI."
  type        = string
  default     = "OpenAI"
}

variable "model_version" {
  description = "Model version to pin. Empty lets the service pick the current default version."
  type        = string
  default     = ""
}

variable "model_deployment_name" {
  description = "Name of the model deployment. Empty takes the model name; the control plane references this name."
  type        = string
  default     = ""
}

variable "model_sku_name" {
  description = "Deployment SKU: GlobalStandard suits chat workloads; use Standard for data-residency-bound regions."
  type        = string
  default     = "GlobalStandard"
}

variable "model_capacity" {
  description = "Deployment capacity in thousands of tokens per minute."
  type        = number
  default     = 30

  validation {
    condition     = var.model_capacity >= 1
    error_message = "model_capacity must be at least 1."
  }
}

variable "transcription_model_name" {
  description = "Optional audio-transcription model deployed beside the chat model (e.g. gpt-4o-mini-transcribe or whisper), for the GUI chat assistant's voice input. Empty deploys none."
  type        = string
  default     = ""
}

variable "transcription_model_version" {
  description = "Version of the transcription model. Empty lets the service pick the current default version."
  type        = string
  default     = ""
}

variable "transcription_model_capacity" {
  description = "Capacity for the transcription deployment, in thousands of tokens per minute."
  type        = number
  default     = 30

  validation {
    condition     = var.transcription_model_capacity >= 1
    error_message = "transcription_model_capacity must be at least 1."
  }
}

variable "disable_local_auth" {
  description = "Disable key-based auth so only Entra identities can call the endpoint."
  type        = bool
  default     = false
}

variable "tags" {
  description = "Tags set on the account and its project."
  type        = map(string)
  default     = {}
}
