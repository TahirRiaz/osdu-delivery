# Where the dev environment's Terraform state lives: a blob in a storage account the deploying identity may write
# (Storage Blob Data Contributor), reached with Entra authentication, no account key. Created once, before the first
# deploy-dev.ps1 -Infrastructure, outside this configuration. Replace every <placeholder>, or leave this file as it is
# and keep the environment's own copy outside the repository (OSDU_ENVIRONMENT_DIR).
subscription_id      = "<subscription id of the state storage account>"
resource_group_name  = "<resource group of the state storage account>"
storage_account_name = "<state storage account name>"
container_name       = "tfstate"
key                  = "osdu-delivery-dev.tfstate"
