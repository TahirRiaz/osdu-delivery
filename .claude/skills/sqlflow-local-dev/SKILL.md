---
name: sqlflow-local-dev
description: Run SQLFlow locally against the real Azure resources for fast iteration and debugging, instead of building containers and deploying. Use when asked to run/debug/test locally, "spin it up", set up dev mode, reproduce a bug, or try a change without deploying. Covers dev.bat, VS Code F5, the .sqlflow/env contract, and the traps (stale Key Vault secret, empty CORS default, shared run queue).
---

# SQLFlow local dev

**Deploying to Azure to try a change is the slow path. Default to this.** A code change is
`dotnet run` (seconds) and a GUI change is a Vite hot-reload (instant), against the same catalog and
the same data the estate uses. Only build containers when shipping (see `deploy-sqlflow-azure`).

## The shape

Everything **runs** on this machine; every **resource** is the real Azure estate: the same catalog
(`dw-sqlflow-prod`), the same `pre`/`ods` databases, the same lake. You are debugging the live
system, locally. It is the same user table, so the normal SQLFlow login works.

| | |
|---|---|
| GUI | http://localhost:5173 (Vite, hot-reloads on save) |
| Control plane | http://localhost:5000 (API + scheduler + worker in ONE process) |
| Catalog | the real Azure `dw-sqlflow-prod` |
| Config | `.sqlflow/env` (git-ignored, generated) |

## Start it

```
dev.bat                 # GUI + control plane
dev.bat api             # control plane only
```
Or **F5 in VS Code → "SQLFlow: Dev (F5)"** for breakpoints. Because `ControlPlane__Worker__Enabled=true`
puts compute in the same process, breakpoints hit in API, scheduler, sync, lineage **and** run
execution. There is also a "CLI (sqlflow)" launch config: edit its `args` and F5.

First run only: `powershell -ExecutionPolicy Bypass -File scripts\dev-setup.ps1` writes `.sqlflow/env`
from live Azure values. Re-run after any secret rotation. Needs `az login`.

## Read this once: you share the run queue with Azure

The catalog is shared, so the local worker can **claim runs the cloud worker would have run**, and
the local scheduler competes to fire schedules. The claim is compare-and-swap, so nothing double-fires
or double-runs, but:

- A run you trigger may execute locally (good, breakpoints) or in Azure (a race).
- **Leave it running overnight and your machine may run the 04:00 nightly.** Close the window when done.
- `SchedulerOptions` has **no `Enabled` flag**, only `PollSeconds`. You cannot config the local
  scheduler off. To be certain it never fires, stop the process.
- Flows read and **WRITE** the real cloud databases and lake, exactly as the estate does.

To be API-only (no local compute), set `ControlPlane__Worker__Enabled=false` in `.sqlflow/env`.

## Traps that will waste your time

**The Key Vault secret `dw-sqlflow-prod` is STALE.** It points at the OLD managed instance
(`dw-sql-mi-prod`); the estate runs on `dw-mi-sql-prod`. Using it gives a connection that fails with
a misleading *"transient failure ... consider EnableRetryOnFailure"* error. **Container app secrets
are the source of truth**, which is what `dev-setup.ps1` reads:

```bash
az containerapp secret show -n sqlflow-v3-control-plane -g datawarehouse-west-rg-prod-v2 \
  --secret-name catalog-db --query value -o tsv
# also: jwt-signing-key, git-token, bootstrap-admin-password
```

**CORS defaults to EMPTY, and empty means no CORS at all.** Without
`ControlPlane__Cors__AllowedOrigins__0=http://localhost:5173` every GUI call fails with a CORS error.
Nothing warns you.

**The control plane does NOT read `.sqlflow/env` itself.** That loader is for flow directories and
the engine, not the host. `dev.bat` parses the file into real env vars; VS Code uses `envFile`. If
you run `dotnet run` by hand, export them yourself.

**Do not `source .sqlflow/env` in bash.** Values contain spaces (`User ID=sa`), so bash splits them
and you get a truncated connection string plus `User: command not found`. Use `dev.bat` or VS Code.

**Never bootstrap the admin locally.** Setting `ControlPlane__Bootstrap__AdminUsername` +
`AdminPasswordReference` against the shared catalog tries to re-provision the admin and can rewrite
the estate's password. Omit both; your normal login already exists. (Setting only one throws:
"must be set together".)

**`ApplyMigrations` is false by default here, deliberately.** Local code is usually ahead of what is
deployed; applying an unfinished migration to the shared catalog breaks the running container apps,
which are on older code and hit columns they do not know. Flip it to true only when deliberately
testing a migration against the estate.

**`AllowCreate` stays false**, so a typo in the connection can never provision a stray database.

## Verify it is actually working

```bash
# Log in and read the estate through the LOCAL api.
PW=$(az containerapp secret show -n sqlflow-v3-control-plane -g datawarehouse-west-rg-prod-v2 \
      --secret-name bootstrap-admin-password --query value -o tsv)
TOK=$(curl -s -X POST http://localhost:5000/api/v1/auth/login -H "Content-Type: application/json" \
      -d "{\"username\":\"admin\",\"password\":\"$PW\"}" | python -c "import sys,json;print(json.load(sys.stdin)['accessToken'])")
curl -s http://localhost:5000/api/v1/schedules -H "Authorization: Bearer $TOK"
```
Real data coming back means catalog, auth and CORS are all good. Note there is **no `/health`** on
this API (it 404s); do not use it as a readiness check.

Engine-only checks need no server at all, and are the fastest loop of all:
```bash
dotnet run --project src/SqlFlow.Cli -- lineage --dir "C:/Projects/dwh-pipelines-prod/Baatbooking" --no-connect
dotnet test --filter "Category!=Integration"    # ~4,083 pass; 1 known pre-existing failure
```

## Windows batch traps (all three of these made dev.bat silently do NOTHING)

If you edit `dev.bat` or write any `.bat` here, these will bite:

- **`cmd` needs CRLF.** A batch file written with LF endings misparses labels and `for /f` blocks and
  appears to do nothing at all, with no error. The Write/Edit tools emit LF, so re-apply CRLF after
  touching a `.bat`: `python -c "import io;p='dev.bat';s=io.open(p,encoding='utf-8',newline='').read().replace('
','
');io.open(p,'w',encoding='utf-8',newline='
').write(s)"`
- **`az` is `az.cmd`, and `npm` is `npm.cmd`.** Invoking a `.cmd` from a `.bat` WITHOUT `call` hands
  over control and never returns: the rest of the script silently never runs and prints nothing.
  Always `call az ...`, `call npm ...`. (`dotnet` is a real `.exe` and needs no `call`.)
- **A running process never sees a PATH change.** Node was installed while VS Code was open, so that
  terminal and every window it spawns had no `npm`. `dev.bat` now locates Node itself
  (`%ProgramFiles%
odejs`) rather than requiring a restart; a plain terminal still needs restarting
  to get `npm` directly.

## VS Code launch.json traps

- **`serverReadyAction.uriFormat` must contain exactly one `%s`.** A hardcoded URL makes VS Code
  reject the whole config with *"Format uri ... must contain exactly one substitution placeholder"*
  and nothing launches. To open a URL unrelated to the captured group (the GUI, when the pattern
  matched the API's log line), drop `serverReadyAction` and let Vite's `--open` do it.
- `.vscode/` is **git-ignored** here, so `launch.json`/`tasks.json` are local-only and never shipped.

## Machine facts

- Node lives at `C:\Program Files\nodejs` (Node 24 LTS, installed via winget). If `node` is not on
  PATH in a shell, that shell started before the install: open a new one.
- `gui/public/config.json` already points the GUI at `http://localhost:5000`, so Vite needs no proxy.
- Vite is pinned to port 5173 (`strictPort`), which is the origin the CORS line grants.
