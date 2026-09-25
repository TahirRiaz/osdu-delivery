@echo off
REM ============================================================================
REM  OSDU Delivery local dev. Run this instead of deploying to try something.
REM
REM      dev.bat          start the GUI and the control plane
REM      dev.bat api      control plane only (no GUI window)
REM
REM  What comes up:
REM      GUI            http://localhost:5173   (Vite, hot-reloads on save)
REM      Control plane  http://localhost:5000   (API + scheduler + dispatcher + in-process node)
REM
REM  Everything RUNS here; every resource is the REAL Azure estate: the same catalog,
REM  the same databases, the same storage. Sign in with your normal OSDU Delivery
REM  login, because it is literally the same user table.
REM
REM  Azure calls go through your 'az login' (SQLFLOW_AZURE_AUTH=cli), so ${keyvault:...}
REM  refs in flow YAML resolve with no extra config.
REM
REM  ---------------------------------------------------------------------------
REM  READ THIS ONCE: you share the estate with the cloud.
REM
REM  The catalog is shared, so this process's in-process node can take runs the cloud
REM  fleet would have run, and its scheduler competes to fire schedules. Every claim is
REM  compare-and-swap, so nothing double-fires or double-runs. But it does mean:
REM    - a run you trigger may execute here (good: breakpoints) or in the cloud (race).
REM    - if you leave this running overnight, YOUR MACHINE may run the nightly.
REM  Close the window when you are done. Starting dev.bat again kills whatever the last
REM  run left behind, so you never end up with two of these racing each other.
REM
REM  Flows read and WRITE the real cloud databases and the real OSDU partition the flow
REM  declares, exactly as the estate does.
REM  ---------------------------------------------------------------------------
REM
REM  Migrations: pending SQLFlow catalog and OSDU module migrations from THIS checkout are
REM  applied before startup (the deployed containers do the same for themselves). F5 alone
REM  does NOT migrate: run dev.bat once after pulling or authoring a migration, then debug.
REM
REM  Config: .sqlflow\env (git-ignored). Rebuild it with osdu\tools\dev-setup.ps1.
REM ============================================================================
setlocal

cd /d "%~dp0"

REM --- Stop whatever the last run left behind, before anything builds ---
REM A control plane from an earlier run keeps its DLL open, so the 'dotnet run' below fails
REM its build with "the file is locked by another process" and you end up debugging the OLD
REM binary. A surviving in-process node also keeps taking runs behind your back. So: kill the
REM port owners, kill a control plane that crashed off its port but still holds the bin\ lock
REM (F5 sessions included), and kill the GUI window a previous dev.bat spawned.
call :stopport 5000 "control plane"
call :stopport 5173 "GUI"
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -in 'dotnet.exe','SqlFlow.Delivery.ControlPlane.Host.exe' -and $_.CommandLine -like '*SqlFlow.Delivery.ControlPlane.Host*' } | ForEach-Object { Write-Host ('Stopping previous control plane (pid ' + $_.ProcessId + ') ...'); Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
taskkill /f /t /fi "WINDOWTITLE eq OSDU Delivery GUI*" >nul 2>&1

REM --- .NET SDK on PATH ---
REM A runtime-only Program Files install shadows a per-user SDK (user PATH comes after system PATH),
REM so fall back to the per-user installs, prepended so they win. 'dotnet --version' honours global.json.
REM 'neq 0', not 'errorlevel 1': the host's "no SDK" exit code is negative.
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" (
        set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
        set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"
    )
)
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
        set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
        set "PATH=%USERPROFILE%\.dotnet;%PATH%"
    )
)
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [X] No .NET SDK matching global.json was found. Install it from https://aka.ms/dotnet/download
    echo     ^(winget install Microsoft.DotNet.SDK.9^), then start dev.bat again.
    exit /b 1
)

REM --- Azure login is what routes Key Vault and storage to the cloud ---
REM 'call' is REQUIRED: az on Windows is az.cmd, and invoking a .cmd from a .bat without 'call'
REM transfers control and never comes back, so the rest of this script silently never runs.
REM Not logged in is not a dead end: az login opens the browser sign-in right here, so one file
REM carries a fresh machine (or an expired token) all the way to a running estate.
call az account show >nul 2>&1
if errorlevel 1 (
    echo Not logged in to Azure; opening the sign-in ...
    call az login
    call az account show >nul 2>&1
    if errorlevel 1 (
        echo [X] Azure sign-in did not complete. Run 'az login' and start dev.bat again.
        exit /b 1
    )
)

REM --- The env file is generated, so a missing one is generated, not reported ---
REM dev-setup.ps1 reads the live container app secrets through the az login above, which is why
REM the login step comes first. Regenerate by hand after a secret rotation (see its own header).
if not exist ".sqlflow\env" (
    echo .sqlflow\env is missing; generating it from the live estate ...
    powershell -NoProfile -ExecutionPolicy Bypass -File osdu\tools\dev-setup.ps1
    if not exist ".sqlflow\env" (
        echo [X] osdu\tools\dev-setup.ps1 did not produce .sqlflow\env. Fix its output above and retry.
        exit /b 1
    )
)

REM --- Load .sqlflow\env into this process ---
REM The control plane does NOT read .sqlflow\env itself: that loader is for flow directories and
REM the engine, not the host. So push the values in as real environment variables.
REM 'delims==' splits on the FIRST '=' only, so connection strings (full of '=' and ';') survive;
REM the findstr drops comment lines and blank lines.
for /f "usebackq tokens=1,* delims==" %%a in (`findstr /v /r /c:"^#" ".sqlflow\env" ^| findstr /r /c:"="`) do set "%%a=%%b"

REM --- Apply pending migrations, up front and visibly ---
REM The deployed containers migrate themselves at startup, but the local HOST runs with
REM ApplyMigrations=false so an F5 or a stray local run can never mutate the shared catalog
REM implicitly. Starting dev.bat is the explicit moment your checkout meets the shared databases,
REM so its pending migrations are applied here through the CLI's guarded 'db migrate' (it upgrades
REM an EXISTING database only and refuses to provision anything, so a wrong connection string can
REM never create a stray database). It covers SQLFlow's catalog and the OSDU module's osdu schema.
echo Applying pending migrations (SQLFlow catalog, then the OSDU module) ...
dotnet run --project osdu\hosts\SqlFlow.Delivery.Cli.Host -- db migrate
if errorlevel 1 (
    echo [!] Migration failed. The control plane still starts, but any surface that needs the new
    echo     schema will error until:  dotnet run --project osdu\hosts\SqlFlow.Delivery.Cli.Host -- db migrate
)

if /i "%~1"=="api" goto :api

REM --- Node on PATH ---
REM Node is usually installed AFTER VS Code (or this terminal) was started, and a running process
REM never picks up a PATH change. So the terminal, and every window it spawns, has no npm until it
REM is restarted. Rather than demand a restart, find Node and add it for this script and its children.
where npm >nul 2>&1
if errorlevel 1 (
    if exist "%ProgramFiles%\nodejs\npm.cmd" set "PATH=%PATH%;%ProgramFiles%\nodejs"
)
where npm >nul 2>&1
if errorlevel 1 (
    if exist "%ProgramFiles(x86)%\nodejs\npm.cmd" set "PATH=%PATH%;%ProgramFiles(x86)%\nodejs"
)
where npm >nul 2>&1
if errorlevel 1 (
    if exist "%LOCALAPPDATA%\Programs\nodejs\npm.cmd" set "PATH=%PATH%;%LOCALAPPDATA%\Programs\nodejs"
)
where npm >nul 2>&1
if errorlevel 1 (
    echo [X] npm not found. Install Node ^(winget install OpenJS.NodeJS.LTS^), then open a NEW terminal.
    echo     Or run:  dev.bat api    to start the control plane without the GUI.
    exit /b 1
)

REM --- GUI deps: install on first run AND whenever the lock file moved on ---
REM npm writes node_modules\.package-lock.json on every install; when the repo's package-lock.json
REM is newer than that stamp (a pulled or newly added dependency), the tree is stale and Vite would
REM fail at the first missing import. xcopy /L /D lists files newer than the target without copying:
REM one line of output means the lock moved on, so reinstall. Only osdu\gui installs: it compiles the
REM vendored sqlflow\gui sources in place and resolves every package from this one install.
set "_gui_install="
if not exist "osdu\gui\node_modules\.package-lock.json" set "_gui_install=1"
if not defined _gui_install (
    for /f %%n in ('xcopy /L /D /Y "osdu\gui\package-lock.json" "osdu\gui\node_modules\.package-lock.json" ^| findstr /r /c:"^1 "') do set "_gui_install=1"
)
if defined _gui_install (
    echo Installing GUI dependencies ...
    pushd osdu\gui
    call npm ci
    popd
)

echo Starting the GUI on http://localhost:5173 ...
start "OSDU Delivery GUI" cmd /k "cd /d %~dp0osdu\gui && npm run dev -- --open"

:api
echo.
echo   GUI:      http://localhost:5173     (your normal OSDU Delivery login)
echo   API:      http://localhost:5000
echo   Catalog:  the REAL catalog. Your in-process node shares the estate's work.
echo   Ctrl+C    stops the control plane. The GUI has its own window.
echo.
REM The managed git-to-catalog sync is claim-based on the SHARED catalog: a local instance that ran
REM it would steal due syncs from the deployed control plane and execute them with this machine's
REM filesystem, credentials, and code version. Dev instances therefore never participate; use the
REM deployed estate's "sync now" (or the CLI's db sync against a local folder) instead.
set "ControlPlane__ManagedSync__Enabled=false"
REM 'dotnet run' takes the project directory as its content root, where there is no appsettings.json
REM (SQLFlow's, with the framework categories at Warning, reaches only a built host's OUTPUT folder
REM through the project reference). So every request and every EF Core command logs at Information
REM here unless these two are set; do not "fix" this by adding an appsettings.json to the host project,
REM that would replace the platform's file in the output and change what the deployed containers run
REM with (see HANDOVER.md).
set "Logging__LogLevel__Microsoft.AspNetCore=Warning"
set "Logging__LogLevel__Microsoft.EntityFrameworkCore=Warning"
dotnet run --project osdu\hosts\SqlFlow.Delivery.ControlPlane.Host --urls http://localhost:5000

endlocal
exit /b

REM ---------------------------------------------------------------------------
REM  :stopport <port> <what>   free a listening port by killing the process tree that owns it.
REM  netstat prints the owning pid in the last column; the trailing space in the pattern is what
REM  keeps ":5000 " from also matching ":50000".
REM  Do NOT add '-p tcp': that switch makes netstat print IPv4 ONLY, and Vite listens on
REM  [::1]:5173, so the GUI would never be found. Bare '-ano' lists v4 and v6; the LISTENING
REM  match drops the UDP rows on its own.
REM ---------------------------------------------------------------------------
:stopport
setlocal
set "_port=%~1"
set "_what=%~2"
for /f "tokens=5" %%p in ('netstat -ano ^| findstr /r /c:":%_port% .*LISTENING"') do (
    echo Stopping previous %_what% ^(pid %%p^) on port %_port% ...
    taskkill /f /t /pid %%p >nul 2>&1
)
endlocal
goto :eof
