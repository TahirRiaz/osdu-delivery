@echo off
REM ============================================================================
REM  OSDU Delivery - deploy to prod from cmd: runs deploy-prod.ps1 (deploy.ps1 with
REM  -Environment prod), the one deploy path, with the same arguments.
REM
REM    deploy-prod.bat                        what changed since what prod serves
REM    deploy-prod.bat control-plane worker   exactly those apps
REM    deploy-prod.bat -WhatIf                the plan, nothing deployed
REM
REM  prod is osdu/deploy/terraform/environments/prod.tfvars. KEEP THIS FILE AT THE REPO ROOT.
REM ============================================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-prod.ps1" %*
exit /b %ERRORLEVEL%
