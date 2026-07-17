@echo off
REM ============================================================================
REM  SQLFlow local dev. Run this instead of deploying to Azure to try something.
REM
REM      dev.bat          start the GUI and the control plane
REM      dev.bat api      control plane only (no GUI window)
REM
REM  What comes up:
REM      GUI            http://localhost:5173   (Vite, hot-reloads on save)
REM      Control plane  http://localhost:5000   (API + scheduler + worker, ONE process)
REM
REM  Everything RUNS here; every resource is the REAL Azure estate: the same catalog
REM  (dw-sqlflow-prod), the same pre/ods databases, the same lake. Sign in with your
REM  normal SQLFlow login, because it is literally the same user table.
REM
REM  Azure calls go through your 'az login' (SQLFLOW_AZURE_AUTH=cli), so ${keyvault:...}
REM  refs in flow YAML resolve with no extra config.
REM
REM  ---------------------------------------------------------------------------
REM  READ THIS ONCE: you share the run queue with Azure.
REM
REM  The catalog is shared, so this process's worker can CLAIM runs the cloud worker
REM  would have run, and its scheduler competes to fire schedules. The claim is
REM  compare-and-swap, so nothing double-fires or double-runs. But it does mean:
REM    - a run you trigger may execute here (good: breakpoints) or in Azure (race).
REM    - if you leave this running overnight, YOUR MACHINE may run the 04:00 nightly.
REM  Close the window when you are done. There is no scheduler off switch in config.
REM
REM  Flows read and WRITE the real cloud databases, exactly as the estate does.
REM  ---------------------------------------------------------------------------
REM
REM  Config: .sqlflow\env (git-ignored). Rebuild it with scripts\dev-setup.ps1.
REM  Debugger: F5 in VS Code -> "SQLFlow: Dev (F5)" does the same with breakpoints.
REM ============================================================================
setlocal

cd /d "%~dp0"

if not exist ".sqlflow\env" (
    echo [X] .sqlflow\env is missing. Run:
    echo     powershell -ExecutionPolicy Bypass -File scripts\dev-setup.ps1
    exit /b 1
)

REM --- Load .sqlflow\env into this process ---
REM The control plane does NOT read .sqlflow\env itself: that loader is for flow directories and the
REM engine, not the host. So push the values in as real environment variables.
REM 'delims==' splits on the FIRST '=' only, so connection strings (full of '=' and ';') survive; the
REM findstr drops comment lines and blank lines.
for /f "usebackq tokens=1,* delims==" %%a in (`findstr /v /r /c:"^#" ".sqlflow\env" ^| findstr /r /c:"="`) do set "%%a=%%b"

REM --- Azure login is what routes Key Vault and the lake to the cloud ---
az account show >nul 2>&1
if errorlevel 1 (
    echo [X] Not logged in to Azure. Run:  az login
    exit /b 1
)

if /i "%~1"=="api" goto :api

REM --- GUI deps, first run only ---
if not exist "gui\node_modules" (
    echo Installing GUI dependencies ^(first run only, takes a minute^)...
    pushd gui
    call npm ci
    popd
)

echo Starting the GUI on http://localhost:5173 ...
start "SQLFlow GUI" cmd /k "cd /d %~dp0gui && npm run dev"

:api
echo.
echo   GUI:      http://localhost:5173     (your normal SQLFlow login)
echo   API:      http://localhost:5000
echo   Catalog:  the REAL Azure catalog. Your worker shares the estate's run queue.
echo   Ctrl+C    stops the control plane. The GUI has its own window.
echo.
dotnet run --project src\SqlFlow.ControlPlane --urls http://localhost:5000

endlocal
