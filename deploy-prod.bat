@echo off
setlocal enabledelayedexpansion

REM ============================================================================
REM  SQLFlow V3 - prod-v2 container deploy
REM
REM  Builds the requested images in ACR, points the container apps at the new
REM  tag, waits for the new control-plane revision, and prints its startup log.
REM  The image tag is the current commit's short SHA (what the apps actually run).
REM
REM  KEEP THIS FILE AT THE REPO ROOT: the build contexts are resolved relative
REM  to it.
REM
REM  Usage:
REM    deploy-prod.bat                        build+deploy control-plane, worker, gui
REM    deploy-prod.bat control-plane worker   only those apps (e.g. engine-only change)
REM    deploy-prod.bat gui                     GUI only
REM
REM  Known apps: control-plane  worker  gui  mcp  slack-bot
REM
REM  NOT handled here: the pipeline YAML in the separate dwh-pipelines-prod repo.
REM  When flow YAML changes, push it after this script finishes:
REM    cd /c/Projects/dwh-pipelines-prod
REM    TOK=$(az keyvault secret show --vault-name sqlflow-v3-secrets ^
REM          --name bitbucket-git-token --query value -o tsv)
REM    GIT_TERMINAL_PROMPT=0 git push ^
REM      "https://x-bitbucket-api-token-auth:${TOK}@bitbucket.org/kolumbuscode/dwh-pipelines-prod.git" main
REM ============================================================================

REM Always operate from the repo root (this script's directory).
cd /d "%~dp0"

set "RG=datawarehouse-west-rg-prod-v2"
set "ACR=sqlflowv3acrprod"
set "SUB=83731164-2cea-4291-b78d-7e2e69eea8a6"

REM `az acr build` prints a check-mark glyph the cp1252 console cannot encode and
REM then dies with a UnicodeEncodeError AFTER the server build already succeeded.
REM Forcing UTF-8 on the Python that hosts the CLI keeps its output (and exit
REM code) honest. The server-side verify below is still the source of truth.
set "PYTHONUTF8=1"
set "PYTHONIOENCODING=utf-8"

REM Apps to process (default: the three that make up a normal deploy).
set "APPS=%*"
if "%APPS%"=="" set "APPS=control-plane worker gui"

REM Image tag = current commit short SHA.
set "TAG="
for /f "delims=" %%i in ('git rev-parse --short HEAD') do set "TAG=%%i"
if "%TAG%"=="" (
  echo ERROR: could not read git HEAD. Run this from the SQLFlowV3 repo.
  exit /b 1
)

REM The image builds from the committed tree at %TAG%; warn on uncommitted source.
for /f "delims=" %%c in ('git status --porcelain -- src gui') do (
  echo WARNING: uncommitted changes in src/ or gui/ - image %TAG% will NOT include them.
  goto :dirty_done
)
:dirty_done

echo.
echo === SQLFlow V3 deploy ===
echo    tag:  %TAG%
echo    apps: %APPS%
echo    rg:   %RG%
echo.

call az account set --subscription "%SUB%"
if errorlevel 1 ( echo ERROR: az account set failed. & exit /b 1 )

REM --- Build each requested app (server-side; exit code is confirmed below) -----
for %%a in (%APPS%) do call :build %%a

REM --- Verify server-side that each tag landed (the real source of truth) -------
echo.
echo === Verifying images on %ACR% ===
set "FAILED="
for %%a in (%APPS%) do call :verify %%a
if defined FAILED (
  echo.
  echo ERROR: one or more builds did not succeed - not deploying. See above.
  exit /b 1
)

REM --- Deploy each app ----------------------------------------------------------
echo.
echo === Deploying container apps ===
for %%a in (%APPS%) do call :deploy %%a

REM --- Wait for the control-plane's new revision, then show its startup log -----
echo %APPS% | findstr /i "control-plane" >nul && (
  call :wait_running sqlflow-v3-control-plane
  call :show_log sqlflow-v3-control-plane
)

echo.
echo === Done. Deployed tag %TAG% to: %APPS% ===
exit /b 0

REM ============================================================================
REM  Subroutines
REM ============================================================================

:build
set "APP=%~1"
call :cfg %APP%
if errorlevel 1 exit /b 1
echo.
echo --- Building sqlflow-v3-%APP%:%TAG%   (%DF%, context %CTX%) ---
call az acr build --registry %ACR% --image sqlflow-v3-%APP%:%TAG% --file "%DF%" "%CTX%"
exit /b 0

:verify
set "APP=%~1"
set "OK="
for /f "delims=" %%s in ('az acr task list-runs -r %ACR% --top 25 -o tsv --query "[?outputImages[0].repository=='sqlflow-v3-%APP%' ^&^& outputImages[0].tag=='%TAG%'] | [0].status"') do set "OK=%%s"
if /i "%OK%"=="Succeeded" (
  echo    sqlflow-v3-%APP%:%TAG%   OK
) else (
  echo    sqlflow-v3-%APP%:%TAG%   NOT OK ^(status: %OK%^)
  set "FAILED=1"
)
exit /b 0

:deploy
set "APP=%~1"
echo --- sqlflow-v3-%APP% -^> :%TAG% ---
call az containerapp update -n sqlflow-v3-%APP% -g %RG% --image %ACR%.azurecr.io/sqlflow-v3-%APP%:%TAG% --query "properties.template.containers[0].image" -o tsv
if errorlevel 1 ( echo ERROR: deploy of sqlflow-v3-%APP% failed. & exit /b 1 )
exit /b 0

:wait_running
set "APP=%~1"
echo.
echo === Waiting for %APP% new revision to reach Running ===
set /a _tries=0
:wr_loop
set "STATE="
for /f "delims=" %%s in ('az containerapp revision list -n %APP% -g %RG% --query "[?properties.active] | [0].properties.runningState" -o tsv') do set "STATE=%%s"
if /i "%STATE%"=="Running" ( echo    %APP%: Running & exit /b 0 )
set /a _tries+=1
if %_tries% geq 60 ( echo WARNING: %APP% still '%STATE%' after waiting; check the portal. & exit /b 0 )
timeout /t 10 /nobreak >nul
goto wr_loop

:show_log
set "APP=%~1"
echo.
echo === %APP% startup log ^(bootstrap / sync / warnings / errors^) ===
call az containerapp logs show -n %APP% -g %RG% --tail 300 --type console 2>&1 | findstr /i "Bootstrap Synced warn error exception"
exit /b 0

:cfg
REM Map app short-name -> Dockerfile + build context.
set "DF="
set "CTX="
if /i "%~1"=="control-plane" ( set "DF=Dockerfile"          & set "CTX=." )
if /i "%~1"=="worker"        ( set "DF=Dockerfile.worker"   & set "CTX=." )
if /i "%~1"=="gui"           ( set "DF=gui\Dockerfile"      & set "CTX=gui" )
if /i "%~1"=="mcp"           ( set "DF=Dockerfile.mcp"      & set "CTX=." )
if /i "%~1"=="slack-bot"     ( set "DF=Dockerfile.slackbot" & set "CTX=." )
if "%DF%"=="" ( echo ERROR: unknown app '%~1'. Known: control-plane worker gui mcp slack-bot & exit /b 1 )
exit /b 0
