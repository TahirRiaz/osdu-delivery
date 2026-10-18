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
#  First start: with no .sqlflow/env yet, the setup asks for what it cannot know, offers to install .NET, to create the
#  databases and to start SQL Server 2022 in Docker (which is how SQL Server runs on a Mac), writes .sqlflow/env from
#  dev.env.example, and registers the partition. Every start then checks the machine and the file and prints what is set
#  and what is missing, with the fix for each, and stops only when a start would fail. Both are osdu-delivery-setup
#  (osdu/docs/reference/cli/dev.md): built here in seconds where .NET is installed, and where it is not, the
#  self-contained binary each release publishes, fetched with the GitHub CLI into .sqlflow/tools. This script only finds
#  them, and starts what the check cleared.
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
root="$PWD"

api=""
managed=""
asksetup=""
check=""
for argument in "$@"; do
    case "$(printf '%s' "$argument" | tr '[:upper:]' '[:lower:]')" in
        api) api=1 ;;
        managed) managed=1 ;;
        setup) asksetup=1 ;;
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
# A terminal opened before the install, or an install outside the PATH (the Microsoft script's ~/.dotnet, an SDK the
# setup installed since this terminal started), is found here rather than demanding a new terminal. 'dotnet --version'
# honours global.json.
find_dotnet() {
    if dotnet --version >/dev/null 2>&1; then
        return 0
    fi
    local candidate
    for candidate in "$HOME/.dotnet" /usr/local/share/dotnet /opt/homebrew/share/dotnet /usr/share/dotnet /usr/lib/dotnet; do
        if [ -x "$candidate/dotnet" ] && "$candidate/dotnet" --version >/dev/null 2>&1; then
            export DOTNET_ROOT="$candidate"
            export PATH="$candidate:$PATH"
            return 0
        fi
    done
    return 1
}

# --- The published setup, for a machine without .NET ---
# The repository is private, so the download is the signed-in user's, through the GitHub CLI; the archive is used only
# when it matches the checksum the release lists beside it (<archive>.sha256).
setup_rid() {
    local os arch
    os="$(uname -s)"
    arch="$(uname -m)"
    case "$os/$arch" in
        Darwin/arm64) echo osx-arm64 ;;
        Darwin/x86_64) echo osx-x64 ;;
        Linux/x86_64) echo linux-x64 ;;
        Linux/aarch64 | Linux/arm64) echo linux-arm64 ;;
        *) echo "" ;;
    esac
}

fetch_setup() {
    local tools="$root/.sqlflow/tools" rid archive want have latest current
    # The release the binary here came from, and the newest release when the GitHub CLI can say: a newer one replaces
    # it. Offline, or without the GitHub CLI, the binary here is used as it is.
    latest=""
    if command -v gh >/dev/null 2>&1; then
        latest="$(gh release view --json tagName --jq .tagName 2>/dev/null || true)"
    fi
    current="$(cat "$tools/osdu-delivery-setup.release" 2>/dev/null || true)"
    if [ -x "$tools/osdu-delivery-setup" ] && { [ -z "$latest" ] || [ "$latest" = "$current" ]; }; then
        echo "$tools/osdu-delivery-setup"
        return 0
    fi
    rid="$(setup_rid)"
    if [ -z "$rid" ]; then
        echo "[X] No setup binary is published for $(uname -s) $(uname -m); install the .NET SDK and start ./dev.sh again." >&2
        return 1
    fi
    archive="osdu-delivery-setup-$rid.tar.gz"
    echo "The .NET SDK is not installed, so the setup runs from the binary each release publishes." >&2
    if ! command -v gh >/dev/null 2>&1 || ! gh auth status >/dev/null 2>&1; then
        fetch_manual "$archive"
        return 1
    fi
    mkdir -p "$tools"
    echo "Downloading $archive from release $latest ..." >&2
    if ! gh release download ${latest:+"$latest"} --pattern "$archive" --pattern "$archive.sha256" --dir "$tools" --clobber >&2; then
        fetch_manual "$archive"
        return 1
    fi
    want="$(head -n 1 "$tools/$archive.sha256" | cut -d' ' -f1 || true)"
    if command -v sha256sum >/dev/null 2>&1; then
        have="$(sha256sum "$tools/$archive" | cut -d' ' -f1)"
    else
        have="$(shasum -a 256 "$tools/$archive" | cut -d' ' -f1)"
    fi
    if [ -z "$want" ] || [ "$want" != "$have" ]; then
        echo "[X] The download does not match the checksum the release lists for it, so it was not used." >&2
        rm -f -- "$tools/$archive" "$tools/$archive.sha256"
        fetch_manual "$archive"
        return 1
    fi
    tar -xzf "$tools/$archive" -C "$tools"
    rm -f -- "$tools/$archive" "$tools/$archive.sha256"
    chmod +x "$tools/osdu-delivery-setup"
    printf '%s\n' "$latest" > "$tools/osdu-delivery-setup.release"
    echo "$tools/osdu-delivery-setup"
}

fetch_manual() {
    case "$(uname -s)" in
        Darwin) echo "[X] The .NET SDK is not installed. Install it: brew install --cask dotnet-sdk, then start ./dev.sh again." >&2 ;;
        *) echo "[X] The .NET SDK is not installed. Install it (https://learn.microsoft.com/dotnet/core/install/linux), then start ./dev.sh again." >&2 ;;
    esac
    echo "    Or put osdu-delivery-setup from this repository's Releases ($1) in .sqlflow/tools/ and start ./dev.sh again;" >&2
    echo "    with the GitHub CLI signed in (gh auth login), ./dev.sh fetches it itself." >&2
}

# --- The setup: osdu-delivery-setup ---
# Where .NET is installed it is built here: it carries only the setup, so it builds in seconds and is always this
# checkout's. Where .NET is not, the self-contained binary each release publishes runs instead (it needs no .NET, and
# offers to install it).
if find_dotnet; then
    echo "Building the setup ..."
    if ! dotnet build osdu/hosts/SqlFlow.Delivery.Setup.Host -v q -nologo; then
        echo "[X] The setup did not build. Fix the build above and start ./dev.sh again." >&2
        exit 1
    fi
    setup="$root/osdu/hosts/SqlFlow.Delivery.Setup.Host/bin/Debug/net9.0/osdu-delivery-setup"
else
    setup="$(fetch_setup)" || exit 1
fi

# --- 'check': the report alone, with an OSDU sign-in; nothing is stopped or started ---
if [ -n "$check" ]; then
    set +e
    "$setup" check --test-osdu $api_flag
    exit $?
fi

# --- The pre-push tests: git runs the unit tier before every push (CONTRIBUTING.md, Tests) ---
# Once per clone: only in a git checkout, and only where no hooks path is set, so one somebody chose is left as it is.
# 'git push --no-verify' skips the hook once; 'git config --unset core.hooksPath' removes it.
if command -v git > /dev/null 2>&1 && git rev-parse --is-inside-work-tree > /dev/null 2>&1 \
   && [ -z "$(git config --get core.hooksPath || true)" ]; then
    if git config core.hooksPath tools/git-hooks; then
        echo "Installed the pre-push tests: git runs the unit tier (bash tools/test.sh) before every push; git push --no-verify skips it once."
    fi
fi

# --- The setup when there is no env file (or it was asked for), else the check ---
# Exit codes: 0 ready, 2 something a start needs is missing (the report says what), 1 stopped.
if [ -n "$asksetup" ] || [ ! -f .sqlflow/env ]; then
    if [ ! -f .sqlflow/env ]; then
        echo ".sqlflow/env is missing, so this is a first start: a few questions set this machine up."
    fi
    "$setup" setup $api_flag || exit 1
else
    "$setup" check --prepare $api_flag || exit 1
fi

# --- .NET: the setup may have installed it since this terminal started, off its PATH ---
if ! find_dotnet; then
    echo "[X] The .NET SDK is needed to start, and is not installed; the setup said how to install it." >&2
    echo "    Open a new terminal once it is, and start ./dev.sh again." >&2
    exit 1
fi

# --- The command line: the migrations; the setup has usually built it already ---
echo "Building the OSDU Delivery command line ..."
if ! dotnet build osdu/hosts/SqlFlow.Delivery.Cli.Host -v q -nologo; then
    echo "[X] The command line did not build. Fix the build above and start ./dev.sh again." >&2
    exit 1
fi
cli="$root/osdu/hosts/SqlFlow.Delivery.Cli.Host/bin/Debug/net9.0/SqlFlow.Delivery.Cli.Host"

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
# ControlPlane__* settings reach it only this way. 'env' prints the file's settings as the hosts read them; each is
# exported as it stands, never evaluated, so no value is ever run as shell.
settings="$("$setup" env)" || { echo "[X] .sqlflow/env could not be read; ./dev.sh check says why." >&2; exit 1; }
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
    export ControlPlane__ManagedWorkers__Process__Command="$root/osdu/hosts/SqlFlow.Delivery.Worker.Host/bin/Debug/net9.0/SqlFlow.Delivery.Worker.Host"
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
