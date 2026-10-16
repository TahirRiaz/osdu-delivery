# The full OSDU Delivery estate on Azure Container Apps, the Terraform mirror of osdu/deploy/bicep/main.bicep: the same
# resources, names, secrets and wiring, in one resource group (osdu/deploy/README.md, "Terraform").
#
# No generated or hand-entered secret passes through this configuration (the deployment's setup step writes the generated
# ones, a person the rest), but state holds the connection strings it writes, and under password authentication the SQL
# admin password, so keep it in the remote backend below, in a storage account only the deploying identities can read,
# and never commit a state file. Initialise it with the account to use:
#
#   terraform init \
#     -backend-config="resource_group_name=<state rg>" -backend-config="storage_account_name=<account>" \
#     -backend-config="container_name=tfstate" -backend-config="key=osdu-delivery.tfstate"
#
# (terraform init -backend=false is enough for terraform validate.)

terraform {
  required_version = ">= 1.8.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.81"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.10"
    }
  }

  backend "azurerm" {
    use_azuread_auth = true
  }
}

provider "azurerm" {
  features {
    # The landing storage account takes no shared key (identity access only), and the provider would otherwise poll
    # its blob service with one after creating it, which the account refuses. Nothing here is set through the data
    # plane: the account and its container are managed through Resource Manager alone.
    storage {
      data_plane_available = false
    }
  }

  # Null reads ARM_SUBSCRIPTION_ID instead; azurerm 4 requires one of the two.
  subscription_id = var.subscription_id
}

# Only used when provision_entra_app is on; the deploying identity then needs directory write access (Application
# Administrator, or the Application.ReadWrite.All application permission).
provider "azuread" {}
