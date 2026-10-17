@echo off
REM ============================================================================
REM  OSDU Delivery local dev, on Windows (./dev.sh on macOS and Linux). Run this instead of deploying to try something.
REM
REM      dev.bat          start the GUI and the control plane
REM      dev.bat api      control plane only (no GUI window, so no Node.js needed)
REM      dev.bat managed  managed workers: the control plane starts and stops its own worker
REM                       processes (the 'process' launcher) instead of running an in-process node.
REM                       Combine with api: dev.bat managed api
REM      dev.bat setup    ask the setup questions again (the SQL Server, OSDU, a repository), then start
REM      dev.bat check    say what this machine has and what it is missing, test the OSDU sign-in,
REM                       and start nothing
REM
REM  First start: with no .sqlflow\env yet, the setup asks for what it cannot know, offers to create
REM  the databases (or start SQL Server in Docker), writes .sqlflow\env from dev.env.example, and
REM  registers the partition. Every start then checks the machine and the file and prints what is set
REM  and what is missing, with the fix for each, and stops only when a start would fail. Both are the
REM  command line's 'sqlflow dev' verb (osdu\docs\reference\cli\dev.md); this file only finds .NET,
REM  builds the command line, and starts what the check cleared.
REM
REM  What comes up:
REM      GUI            http://localhost:5173   (Vite, hot-reloads on save)
REM      Control plane  http://localhost:5000   (API + scheduler + dispatcher + in-process node)
REM      Workers        with 'managed': SqlFlow.Delivery.Worker.Host processes the control plane
REM                     starts, named worker-<pool>-<id>, ended with it
REM
REM  Everything runs here, against the databases and the OSDU partition .sqlflow\env names. Flows read
REM  and WRITE them exactly as a deployed estate does.
REM
REM  If .sqlflow\env names a catalog a deployed estate also uses, you share that estate's work: this
REM  process's in-process node can take runs the estate's workers would have run, and its scheduler
REM  competes to fire schedules (every claim is compare-and-swap, so nothing runs twice). Close the
REM  window when you are done. Starting dev.bat again stops whatever the last run left behind.
REM
REM  Migrations: pending SQLFlow catalog and OSDU module migrations from THIS checkout are applied
REM  before startup (the deployed containers do the same for themselves). F5 alone does NOT migrate:
REM  run dev.bat once after pulling or authoring a migration, then debug.
REM ============================================================================
setlocal

cd /d "%~dp0"
REM --- Arguments: 'api', 'managed', 'setup' and 'check', in any order ---
set "_api="
set "_managed="
set "_setup="
set "_check="
for %%a in (%*) do (
    if /i "%%~a"=="api" set "_api=1"
    if /i "%%~a"=="managed" set "_managed=1"
    if /i "%%~a"=="setup" set "_setup=1"
    if /i "%%~a"=="check" set "_check=1"
)
set "_apiflag="
if defined _api set "_apiflag=--api"

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
REM Without .NET nothing else can run, the check included, so this one is said here.
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [X] No .NET SDK matching global.json ^(9.0.100 or later^) was found.
    echo     Install it:  winget install Microsoft.DotNet.SDK.9
    echo     then open a NEW terminal and start dev.bat again. The setup then checks the rest a local run
    echo     needs ^(SQL Server or Docker, Node.js 22 or later for the GUI^) and says how to get each.
    exit /b 1
)

REM --- Node on PATH ---
REM Node is usually installed AFTER VS Code (or this terminal) was started, and a running process
REM never picks up a PATH change. So the terminal, and every window it spawns, has no npm until it
REM is restarted. Rather than demand a restart, find Node and add it for this script and its children.
REM The check reports whether it was found.
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

REM --- The command line: the setup, the check, the env file's settings and the migrations ---
REM Built once here and run from its output folder. A control plane left running does not lock it.
echo Building the OSDU Delivery command line (the first build takes a minute) ...
dotnet build osdu\hosts\SqlFlow.Delivery.Cli.Host -v q -nologo
if errorlevel 1 (
    echo [X] The command line did not build. Fix the build above and start dev.bat again.
    exit /b 1
)
set "_cli=%~dp0osdu\hosts\SqlFlow.Delivery.Cli.Host\bin\Debug\net9.0\SqlFlow.Delivery.Cli.Host.exe"

REM --- 'check': the report alone, with an OSDU sign-in; nothing is stopped or started ---
REM Outside a parenthesised block, so %errorlevel% is read after the check ran: exit 0 ready, 2 not ready.
if not defined _check goto :prepare
"%_cli%" dev check --test-osdu %_apiflag%
exit /b %errorlevel%

:prepare

REM --- The setup when there is no env file (or it was asked for), else the check ---
REM Exit codes: 0 ready, 2 something a start needs is missing (the report says what), 1 stopped.
if defined _setup goto :setup
if not exist ".sqlflow\env" goto :setup
"%_cli%" dev check --prepare %_apiflag%
if errorlevel 1 exit /b 1
goto :checked

:setup
if not exist ".sqlflow\env" echo .sqlflow\env is missing, so this is a first start: a few questions set this machine up.
"%_cli%" dev setup %_apiflag%
if errorlevel 1 exit /b 1

:checked
REM --- Stop whatever the last run left behind ---
REM A control plane from an earlier run keeps its DLL open, so the 'dotnet run' below fails its build
REM with "the file is locked by another process" and you end up debugging the OLD binary. A surviving
REM in-process node also keeps taking runs behind your back. So: kill the port owners, kill a control
REM plane that crashed off its port but still holds the bin\ lock (F5 sessions included), and kill
REM the GUI window a previous dev.bat spawned. This waits until the check passed, so a start that
REM would fail never takes down one that works.
call :stopport 5000 "control plane"
call :stopport 5173 "GUI"
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -in 'dotnet.exe','SqlFlow.Delivery.ControlPlane.Host.exe' -and $_.CommandLine -like '*SqlFlow.Delivery.ControlPlane.Host*' } | ForEach-Object { Write-Host ('Stopping previous control plane (pid ' + $_.ProcessId + ') ...'); Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
REM Workers a managed control plane started end with it when it stops cleanly, but a killed one leaves them
REM polling; they would hold the worker host's bin\ open and take runs for a control plane that is gone.
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -in 'dotnet.exe','SqlFlow.Delivery.Worker.Host.exe' -and $_.CommandLine -like '*SqlFlow.Delivery.Worker.Host*' } | ForEach-Object { Write-Host ('Stopping previous managed worker (pid ' + $_.ProcessId + ') ...'); Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
taskkill /f /t /fi "WINDOWTITLE eq OSDU Delivery GUI*" >nul 2>&1

REM --- Load .sqlflow\env into this process ---
REM The control plane builds its configuration from the process environment before it reads .sqlflow\env
REM itself, so its ControlPlane__* settings reach it only this way. 'dev env' prints the file's settings as
REM the hosts read them (surrounding quotes taken off, the first of a name set twice), and the check has
REM refused any value cmd's set could not carry, so the two never disagree.
for /f "usebackq delims=" %%l in (`call "%_cli%" dev env`) do set "%%l"

REM --- Apply pending migrations, up front and visibly ---
REM The deployed containers migrate themselves at startup, but the local HOST runs with
REM ApplyMigrations=false so an F5 or a stray local run never mutates the catalog implicitly. Starting
REM dev.bat is the explicit moment your checkout meets the databases, so its pending migrations are
REM applied here through the command line's guarded 'db migrate' (it upgrades an EXISTING database only
REM and refuses to provision anything, so a wrong connection string can never create a stray database).
REM It covers SQLFlow's catalog and the OSDU module's osdu schema.
echo Applying pending migrations (SQLFlow catalog, then the OSDU module) ...
"%_cli%" db migrate
if errorlevel 1 (
    echo [!] Migration failed. The control plane still starts, but any surface that needs the new
    echo     schema will error until:  "%_cli%" db migrate
)

REM --- Managed workers: the control plane starts its own worker processes ---
REM The worker host is built here because the control plane starts it, it does not build it. The
REM control plane then runs API-only and keeps at least one worker running per managed pool, starting
REM more for queued runs and asking idle ones to leave, at most 4 unless .sqlflow\env sets
REM ControlPlane__ManagedWorkers__MaxWorkers (any other ControlPlane__ManagedWorkers__* setting there applies too).
if defined _managed (
    echo Building the worker host for managed workers ...
    dotnet build osdu\hosts\SqlFlow.Delivery.Worker.Host -v q -nologo
    if errorlevel 1 (
        echo [X] The worker host did not build; managed workers cannot start. Fix the build above and retry.
        exit /b 1
    )
    set "ControlPlane__Worker__Enabled=false"
    set "ControlPlane__ManagedWorkers__Enabled=true"
    set "ControlPlane__ManagedWorkers__Launcher=process"
    set "ControlPlane__ManagedWorkers__Process__Command=%~dp0osdu\hosts\SqlFlow.Delivery.Worker.Host\bin\Debug\net9.0\SqlFlow.Delivery.Worker.Host.exe"
    set "ControlPlane__ManagedWorkers__Process__ControlPlaneUrl=http://localhost:5000"
    if not defined ControlPlane__ManagedWorkers__MaxWorkers set "ControlPlane__ManagedWorkers__MaxWorkers=4"
)

if defined _api goto :api

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
echo   GUI:      http://localhost:5173
echo   API:      http://localhost:5000
echo   Catalog:  the one .sqlflow\env names (dev.bat check says which, and what it holds)
if defined _managed echo   Workers:  managed: the control plane starts worker processes itself (see the Nodes page).
echo   Ctrl+C    stops the control plane. The GUI has its own window.
echo.
REM 'dotnet run' takes the project directory as its content root, where there is no appsettings.json
REM (SQLFlow's, with the framework categories at Warning, reaches only a built host's OUTPUT folder
REM through the project reference). So every request and every EF Core command logs at Information
REM here unless these two are set; do not "fix" this by adding an appsettings.json to the host project,
REM that would replace the platform's file in the output and change what the deployed containers run
REM with.
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
