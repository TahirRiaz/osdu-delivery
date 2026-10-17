#!/usr/bin/env bash
# =====================================================================================================================
#  OSDU Delivery local dev, on macOS and Linux (dev.bat on Windows). Run this instead of deploying to try something.
#
#      ./dev.sh          start the GUI and the control plane
#      ./dev.sh api      control plane only (no GUI, so no Node.js needed)
#      ./dev.sh managed  managed workers: the control plane starts and stops its own worker processes instead of
#                        running an in-process node. Combine with api: ./dev.sh managed api
#      ./dev.sh setup    ask the setup questions again (the SQL Server, OSDU, a repository), then start
#      ./dev.sh check    say what this machine has and what it is missing, test the OSDU sign-in, and start nothing
#
#  First start: with no .sqlflow/env yet, the setup asks for what it cannot know, offers to create the databases (and to
#  start SQL Server 2022 in Docker, which is how SQL Server runs on a Mac), writes .sqlflow/env from dev.env.example, and
#  registers the partition. Every start then checks the machine and the file and prints what is set and what is missing,
#  with the fix for each, and stops only when a start would fail. Both are the command line's 'sqlflow dev' verb
#  (osdu/docs/reference/cli/dev.md); this script only finds .NET, builds the command line, and starts what the check
#  cleared.
#
#  What comes up: the GUI on http://localhost:5173 (Vite, hot-reloads on save) and the control plane on
#  http://localhost:5000 (API + scheduler + dispatcher + in-process node), both in this terminal: Ctrl+C stops both.
#  Everything runs here, against the databases and the OSDU partition .sqlflow/env names; flows read and WRITE them
#  exactly as a deployed estate does. Pending migrations from THIS checkout are applied before startup.
#
#  Written for the bash macOS ships (3.2): no ${var,,}, no associative arrays, no empty-array expansion under set -u.
# =====================================================================================================================
set -euo pipefail

cd "$(dirname "$0")"

api=""
managed=""
setup=""
check=""
for argument in "$@"; do
    case "$(printf '%s' "$argument" | tr '[:upper:]' '[:lower:]')" in
        api) api=1 ;;
        managed) managed=1 ;;
        setup) setup=1 ;;
        check) check=1 ;;
        *)
            echo "[X] '$argument' is none of api, managed, setup and check." >&2
            exit 1
            ;;
    esac
done
api_flag=""
if [ -n "$api" ]; then api_flag="--api"; fi

# --- .NET SDK on PATH ---
# A terminal opened before the install, or an install outside the PATH (the Microsoft script's ~/.dotnet), is found
# here rather than demanding a new terminal. 'dotnet --version' honours global.json.
if ! dotnet --version >/dev/null 2>&1; then
    for candidate in "$HOME/.dotnet" /usr/local/share/dotnet /opt/homebrew/share/dotnet /usr/share/dotnet /usr/lib/dotnet; do
        if [ -x "$candidate/dotnet" ]; then
            export DOTNET_ROOT="$candidate"
            export PATH="$candidate:$PATH"
            break
        fi
    done
fi
# Without .NET nothing else can run, the check included, so this one is said here.
if ! dotnet --version >/dev/null 2>&1; then
    echo "[X] No .NET SDK matching global.json (9.0.100 or later) was found." >&2
    case "$(uname -s)" in
        Darwin) echo "    Install it:  brew install --cask dotnet-sdk" >&2 ;;
        *) echo "    Install it:  https://learn.microsoft.com/dotnet/core/install/linux" >&2 ;;
    esac
    echo "    then open a new terminal and start ./dev.sh again. The setup then checks the rest a local run needs" >&2
    echo "    (SQL Server, which runs in Docker here, and Node.js 22 or later for the GUI) and says how to get each." >&2
    exit 1
fi

# --- The command line: the setup, the check, the env file's settings and the migrations ---
echo "Building the OSDU Delivery command line (the first build takes a minute) ..."
if ! dotnet build osdu/hosts/SqlFlow.Delivery.Cli.Host -v q -nologo; then
    echo "[X] The command line did not build. Fix the build above and start ./dev.sh again." >&2
    exit 1
fi
cli="$PWD/osdu/hosts/SqlFlow.Delivery.Cli.Host/bin/Debug/net9.0/SqlFlow.Delivery.Cli.Host"

# --- 'check': the report alone, with an OSDU sign-in; nothing is stopped or started ---
if [ -n "$check" ]; then
    set +e
    "$cli" dev check --test-osdu $api_flag
    exit $?
fi

# --- The setup when there is no env file (or it was asked for), else the check ---
# Exit codes: 0 ready, 2 something a start needs is missing (the report says what), 1 stopped.
if [ -n "$setup" ] || [ ! -f .sqlflow/env ]; then
    if [ ! -f .sqlflow/env ]; then
        echo ".sqlflow/env is missing, so this is a first start: a few questions set this machine up."
    fi
    "$cli" dev setup $api_flag || exit 1
else
    "$cli" dev check --prepare $api_flag || exit 1
fi

# --- Stop whatever the last run left behind ---
# A control plane from an earlier run holds its port and keeps taking runs; a managed worker of one keeps polling. This
# waits until the check passed, so a start that would fail never takes down one that works.
stop_port() {
    local pids
    pids="$(lsof -ti "tcp:$1" -sTCP:LISTEN 2>/dev/null || true)"
    if [ -n "$pids" ]; then
        echo "Stopping previous $2 (pid $(echo "$pids" | tr '\n' ' ')) on port $1 ..."
        echo "$pids" | xargs kill 2>/dev/null || true
    fi
}
stop_port 5000 "control plane"
stop_port 5173 "GUI"
pkill -f 'SqlFlow.Delivery.ControlPlane.Host' 2>/dev/null || true
pkill -f 'SqlFlow.Delivery.Worker.Host' 2>/dev/null || true

# --- Load .sqlflow/env into this process ---
# The control plane builds its configuration from the process environment before it reads .sqlflow/env itself, so its
# ControlPlane__* settings reach it only this way. 'dev env' prints the file's settings as the hosts read them; each is
# exported as it stands, never evaluated, so no value is ever run as shell.
settings="$("$cli" dev env)" || { echo "[X] .sqlflow/env could not be read; ./dev.sh check says why." >&2; exit 1; }
while IFS= read -r line; do
    [ -z "$line" ] && continue
    name="${line%%=*}"
    value="${line#*=}"
    if [ -z "$value" ]; then
        unset "$name"
    else
        export "$name=$value"
    fi
done <<< "$settings"

# --- Apply pending migrations, up front and visibly ---
# The local host runs with ApplyMigrations=false so an F5 or a stray local run never mutates the catalog implicitly;
# starting here is the explicit moment the checkout meets the databases. 'db migrate' upgrades an EXISTING database
# only and refuses to provision anything, so a wrong connection string can never create a stray database.
echo "Applying pending migrations (SQLFlow catalog, then the OSDU module) ..."
if ! "$cli" db migrate; then
    echo "[!] Migration failed. The control plane still starts, but any surface that needs the new schema will error" >&2
    echo "    until:  $cli db migrate" >&2
fi

# --- Managed workers: the control plane starts its own worker processes ---
if [ -n "$managed" ]; then
    echo "Building the worker host for managed workers ..."
    if ! dotnet build osdu/hosts/SqlFlow.Delivery.Worker.Host -v q -nologo; then
        echo "[X] The worker host did not build; managed workers cannot start. Fix the build above and retry." >&2
        exit 1
    fi
    export ControlPlane__Worker__Enabled=false
    export ControlPlane__ManagedWorkers__Enabled=true
    export ControlPlane__ManagedWorkers__Launcher=process
    export ControlPlane__ManagedWorkers__Process__Command="$PWD/osdu/hosts/SqlFlow.Delivery.Worker.Host/bin/Debug/net9.0/SqlFlow.Delivery.Worker.Host"
    export ControlPlane__ManagedWorkers__Process__ControlPlaneUrl=http://localhost:5000
    export ControlPlane__ManagedWorkers__MaxWorkers="${ControlPlane__ManagedWorkers__MaxWorkers:-4}"
fi

# --- The GUI, in the background of this terminal: Ctrl+C (or the control plane ending) stops it too ---
gui_pid=""
stop_gui() {
    if [ -n "$gui_pid" ]; then
        kill "$gui_pid" 2>/dev/null || true
        pkill -P "$gui_pid" 2>/dev/null || true
    fi
}
if [ -z "$api" ]; then
    # npm writes node_modules/.package-lock.json on every install; a package-lock.json newer than it means a pulled or
    # added dependency, and Vite would fail at the first missing import, so the tree is installed again.
    if [ ! -f osdu/gui/node_modules/.package-lock.json ] || [ osdu/gui/package-lock.json -nt osdu/gui/node_modules/.package-lock.json ]; then
        echo "Installing GUI dependencies ..."
        (cd osdu/gui && npm ci)
    fi
    echo "Starting the GUI on http://localhost:5173 ..."
    (cd osdu/gui && exec npm run dev -- --open) &
    gui_pid=$!
    trap stop_gui EXIT INT TERM
fi

echo
echo "  GUI:      http://localhost:5173"
echo "  API:      http://localhost:5000"
echo "  Catalog:  the one .sqlflow/env names (./dev.sh check says which, and what it holds)"
if [ -n "$managed" ]; then echo "  Workers:  managed: the control plane starts worker processes itself (see the Nodes page)."; fi
echo "  Ctrl+C    stops the control plane and the GUI."
echo
# 'dotnet run' takes the project directory as its content root, where there is no appsettings.json, so every request
# and every EF Core command would log at Information without these two. Their names hold a '.', which a shell variable
# cannot, so env passes them.
env "Logging__LogLevel__Microsoft.AspNetCore=Warning" "Logging__LogLevel__Microsoft.EntityFrameworkCore=Warning" \
    dotnet run --project osdu/hosts/SqlFlow.Delivery.ControlPlane.Host --urls http://localhost:5000
