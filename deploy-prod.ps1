#requires -Version 5.1
<#
.SYNOPSIS
    Builds and deploys OSDU Delivery to prod, the environment osdu/deploy/terraform/environments/prod.tfvars defines.

.DESCRIPTION
    deploy.ps1 with -Environment prod: every other parameter of deploy.ps1 works here (Get-Help .\deploy.ps1 -Full).

      .\deploy-prod.ps1                       deploy the apps whose images changed since what prod serves
      .\deploy-prod.ps1 control-plane worker  deploy exactly those apps
      .\deploy-prod.ps1 -WhatIf               show what would be deployed, and stop
      .\deploy-prod.ps1 -Infrastructure       plan prod's Terraform, and apply it once 'prod' is typed back
      .\deploy-prod.ps1 -Infrastructure -WhatIf   only the Terraform plan
#>
& (Join-Path $PSScriptRoot 'deploy.ps1') -Environment prod @args
