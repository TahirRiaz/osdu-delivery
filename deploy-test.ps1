#requires -Version 5.1
<#
.SYNOPSIS
    Builds and deploys OSDU Delivery to test, the environment osdu/deploy/terraform/environments/test.tfvars defines.

.DESCRIPTION
    deploy.ps1 with -Environment test: every other parameter of deploy.ps1 works here (Get-Help .\deploy.ps1 -Full).

      .\deploy-test.ps1                       deploy the apps whose images changed since what test serves
      .\deploy-test.ps1 control-plane worker  deploy exactly those apps
      .\deploy-test.ps1 -WhatIf               show what would be deployed, and stop
      .\deploy-test.ps1 -Infrastructure       plan test's Terraform, and apply it once 'test' is typed back
      .\deploy-test.ps1 -Infrastructure -WhatIf   only the Terraform plan
#>
& (Join-Path $PSScriptRoot 'deploy.ps1') -Environment test @args
