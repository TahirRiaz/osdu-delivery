#!/usr/bin/env bash
# Launches the SQLFlow V3 control plane (API + in-process worker + scheduler) against the migration catalog.
# The control plane does NOT read .sqlflow/env itself, so this loads every KEY=VALUE from it into the process
# environment first: the catalog connection, the flow-referenced target connections, the Azure service
# principal (so the in-process worker can read the data lake), and the control-plane dev settings.
set -euo pipefail
cd "$(dirname "$0")/../.."   # repo root

# Stop any prior control-plane instance first, so a restart never collides on port 5000. Running the app as
# `dotnet <dll>` means the process is dotnet.exe (identified by its command line, not an app-named exe), and a
# reaped background task can leave it detached; this makes the launcher idempotent.
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { \$_.CommandLine -like '*SqlFlow.ControlPlane.dll*' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }" >/dev/null 2>&1 || true

while IFS= read -r line; do
  [[ -z "$line" || "$line" == \#* ]] && continue
  key="${line%%=*}"; val="${line#*=}"
  export "$key=$val"
done < .sqlflow/env

echo "control plane -> ${ASPNETCORE_URLS:-default} | catalog: dw-sqlflow-prodV3"
# Run the built DLL directly (not 'dotnet run'), so this IS the app process and a stop kills it cleanly instead
# of orphaning a child that would keep port 5000 bound and lock the assemblies against a rebuild.
exec dotnet src/SqlFlow.ControlPlane/bin/Debug/net9.0/SqlFlow.ControlPlane.dll
