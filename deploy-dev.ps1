#requires -Version 5.1
<#
.SYNOPSIS
    Builds and deploys OSDU Delivery to dev, the environment osdu/deploy/terraform/environments/dev.tfvars defines.

.DESCRIPTION
    deploy.ps1 with -Environment dev: every other parameter of deploy.ps1 works here (Get-Help .\deploy.ps1 -Full).

      .\deploy-dev.ps1                       deploy the apps whose images changed since what dev serves
      .\deploy-dev.ps1 control-plane worker  deploy exactly those apps
      .\deploy-dev.ps1 -WhatIf               show what would be deployed, and stop
      .\deploy-dev.ps1 -Infrastructure       plan dev's Terraform, and apply it once 'dev' is typed back
      .\deploy-dev.ps1 -Infrastructure -WhatIf   only the Terraform plan
#>
& (Join-Path $PSScriptRoot 'deploy.ps1') -Environment dev @args
