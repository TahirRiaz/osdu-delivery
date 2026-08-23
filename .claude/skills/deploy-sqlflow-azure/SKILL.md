---
name: deploy-sqlflow-azure
description: Build and deploy SQLFlow V3 to the Azure container estate, and ship matching pipeline YAML to Bitbucket. Use when asked to deploy, release, "push to azure", rebuild the containers, ship a schema/migration change, or verify a change in the live estate. Covers the environment map, the exact build/deploy commands, verification without API auth, and the local gotchas (no node, az acr build crashes on Windows, git credential hangs).
---

# Deploy SQLFlow V3 to Azure

## Read this first: the estate is a DEV environment

The resource group is named `...-prod-v2` and the images are tagged `prod`, but **this estate is
where features are verified, not a customer-facing production system.** Do not price changes as if
an outage were catastrophic. Deploy, look at it, fix forward. Do not stall the user with risk
analysis they did not ask for; state a real risk once, in one line, then act.

Genuine caution still applies to exactly two things:
- **Data loss** in the catalog or the DWH (a migration that drops rows, a merge that corrupts a table).
- **Silent** breakage: a source that stops updating with no error. Say so plainly and move on.

## Environment map

| Thing | Value |
|---|---|
| Subscription | `Kolumbus EA subscription PROD` / `83731164-2cea-4291-b78d-7e2e69eea8a6` |
| Resource group | `datawarehouse-west-rg-prod-v2` |
| ACR | `sqlflowv3acrprod` (`sqlflowv3acrprod.azurecr.io`) |
| Catalog DB | `dw-sqlflow-prod` on `dw-mi-sql-prod.public.6b122fbc620a.database.windows.net,3342` |
| Key Vault | `sqlflow-v3-secrets` |
| GUI | https://sqlflow-gui.wonderfulsea-cf44760f.westeurope.azurecontainerapps.io |
| Control plane | https://sqlflow-control-plane.wonderfulsea-cf44760f.westeurope.azurecontainerapps.io |

Container apps, and the Dockerfile each is built from (repo root = `C:\Projects\SQLFlowV3`):

**The container app name and the image repository name are NOT the same.** The app has no `v3-` infix,
the image does. Passing the image name to `-n` fails with `The containerapp '...' does not exist`: that is
`az containerapp update` refusing to create anything, not a missing resource. Confirm with
`az containerapp list -o tsv --query "[].name"` before deploying.

| App (the `-n` argument) | Image repository | Dockerfile | Build context |
|---|---|---|---|
| `sqlflow-control-plane` | `sqlflow-v3-control-plane` | `Dockerfile` | `.` |
| `sqlflow-worker` | `sqlflow-v3-worker` | `Dockerfile.worker` | `.` |
| `sqlflow-gui` | `sqlflow-v3-gui` | `gui/Dockerfile` | `gui/` |
| `sqlflow-mcp` | `sqlflow-v3-mcp` | `Dockerfile.mcp` | `.` |
| `sqlflow-slack-bot` | `sqlflow-v3-slack-bot` | `Dockerfile.slackbot` | `.` |

Pipeline YAML lives in a **separate repo**: `C:\Projects\V3Upgrade\dwh-pipelines-prod` →
`https://bitbucket.org/kolumbuscode/dwh-pipelines-prod.git` (branch `main`, the only branch).

**Image tags are the git short SHA** of the SQLFlowV3 commit (`git rev-parse --short HEAD`).
`latest` exists but is not what the apps run. Commit first, then build with that SHA.

## The deploy, end to end

```bash
cd /c/Projects/SQLFlowV3
git add -A && git commit -m "..."          # commit FIRST: the SHA is the image tag
TAG=$(git rev-parse --short HEAD)

# 1. Build. Run the three concurrently (run_in_background); each takes several minutes.
az acr build --registry sqlflowv3acrprod --image sqlflow-v3-control-plane:$TAG --file Dockerfile .
az acr build --registry sqlflowv3acrprod --image sqlflow-v3-worker:$TAG --file Dockerfile.worker .
az acr build --registry sqlflowv3acrprod --image sqlflow-v3-gui:$TAG --file gui/Dockerfile gui/

# 2. Confirm server-side (see the az acr build gotcha below: the CLI exit code LIES).
az acr task list-runs -r sqlflowv3acrprod --top 5 -o tsv \
  --query "[].{id:runId,status:status,image:outputImages[0].repository,tag:outputImages[0].tag}"

# 3. Deploy.
for app in control-plane worker gui; do
  az containerapp update -n sqlflow-$app -g datawarehouse-west-rg-prod-v2 \
    --image sqlflowv3acrprod.azurecr.io/sqlflow-v3-$app:$TAG \
    --query "properties.template.containers[0].image" -o tsv
done

# 4. Wait for Running (never chain sleeps; use an until-loop).
until [ "$(az containerapp revision list -n sqlflow-control-plane -g datawarehouse-west-rg-prod-v2 \
  --query "[?properties.active] | [0].properties.runningState" -o tsv)" = "Running" ]; do sleep 10; done
```

**Migrations apply themselves.** `ControlPlane:Bootstrap:ApplyMigrations` defaults to `true` and
`AllowCreate` is unset, so the control plane runs `MigrateExistingAsync` at startup. Do not run
`dotnet ef database update` against the estate.

## Ordering: containers BEFORE pipeline YAML

When an engine change and a YAML change depend on each other, deploy containers first, then push
YAML, then let the sync land. Both orders have a bad window; this one's worst case is a missed run,
the other's is flows running in the wrong order and corrupting a merge.

```bash
cd /c/Projects/V3Upgrade/dwh-pipelines-prod
TOK=$(az keyvault secret show --vault-name sqlflow-v3-secrets --name bitbucket-git-token --query value -o tsv)
GIT_TERMINAL_PROMPT=0 git push "https://x-bitbucket-api-token-auth:${TOK}@bitbucket.org/kolumbuscode/dwh-pipelines-prod.git" main
```

`RepoSyncService` picks the new commit up on its own within a few minutes. No manual trigger needed.

## Verification without API auth

The API is `401` without a token and there is **no `/health`** (it 404s). Verify from the logs,
which need no auth and are the fastest signal:

```bash
# Did it start, migrate, and sync?
az containerapp logs show -n sqlflow-control-plane -g datawarehouse-west-rg-prod-v2 --tail 300 --type console 2>&1 \
  | grep -iE "Bootstrap provisioning completed|Synced repo source|warn|error|exception"

# Only errors AFTER bootstrap completed are real (see the startup race below).
az containerapp logs show -n sqlflow-control-plane -g datawarehouse-west-rg-prod-v2 --tail 400 --type console 2>&1 \
  | awk -F'"' '$4 > "<bootstrap-completed-timestamp>"' | grep -iE "warn|error"
```

`Synced repo source 'dwh-pipelines-prod' from git at commit <sha>` with **no warnings** is the
green light: it means every flow is attached to a schedule, no schedule is memberless, and no
`schedule: <name>` reference dangles. A clean sync is a stronger signal than any GUI check.

Also verify engine behaviour locally against the real YAML, before deploying anything:

```bash
dotnet run --project src/SqlFlow.Cli -- lineage --dir "C:/Projects/V3Upgrade/dwh-pipelines-prod/Baatbooking" --no-connect
```
It prints the wave plan. For Baatbooking the correct answer is wave 1 `_00_cpy` → wave 2 the four
`_01_csv` → wave 3 the four `_02_ing`.

## Gotchas that cost real time

**Do NOT run the repo-root builds concurrently.** `Dockerfile`, `Dockerfile.worker`, and `Dockerfile.mcp`
all use the repo root as context, and `az acr build` walks the whole 13 GB tree to apply `.dockerignore`
before it uploads anything. Three of those at once thrash: each `az` process sits CPU-bound for 10+ minutes
with flat memory and never submits a run, so `az acr task list-runs` shows nothing at all and there is no
error to read. Run them **sequentially** (the small `gui/` context is fine alongside them). A local
`cargo build --release` makes this markedly worse by inflating `tools/target`.

Related: `next-app/` (460 MB), `_probe_sql/` (55 MB), and `data/` (19 MB) are NOT in `.dockerignore`, so
they are tarred into every .NET image context for no reason. Worth excluding, but note that editing
`.dockerignore` changes the commit and therefore the image tag.

**A loop of `az acr build` calls does NOT serialize them.** Because the CLI dies early (see below) instead of
waiting for its run, the next iteration starts while the previous build is still going server-side. Several
contexts then upload at once, which is the thrash the previous point warns about, and worse: each upload is a
snapshot of the working tree AT THAT MOMENT, so a build dispatched mid-edit compiles a half-saved tree and fails
with errors that do not reproduce locally (seen: `'RouteGroupBuilder' does not contain a definition for
MapGitHistoryEndpoints`, from a context uploaded before the file was written). Wait on the real signal, the tag
appearing in the registry, before dispatching the next one:

```bash
until az acr repository show-tags -n sqlflowv3acrprod --repository sqlflow-v3-control-plane | grep -q "\"$TAG\""; do sleep 20; done
```

**`az acr build` exit code lies.** On this Windows box it dies with
`UnicodeEncodeError: 'charmap' codec can't encode character '✓'` (cp1252 cannot print its `✓`)
*after* the build succeeds server-side. It can also report exit 0 having printed nothing. **Always**
confirm with `az acr task list-runs`, never trust the exit code or the tail output.

**Node IS installed, but not on PATH.** It lives at `C:\Program Files
odejs` (v24). A bare `node` /
`npm` / `npx` fails with `exec: node: not found`, which reads as "not installed" and is not. Call it by
full path and the GUI typechecks and builds locally in seconds:

```powershell
& "C:\Program Files
odejs
pm.cmd" run typecheck   # tsc -b, the fast signal
& "C:\Program Files
odejs
pm.cmd" run build       # tsc + vite build
```
Do this **before** `az acr build`: identical errors, and it iterates far faster than a container build.
Docker (`docker build -f gui/Dockerfile gui/`) is the fallback when the local toolchain is unavailable.

**Bare `git fetch`/`git push` on `dwh-pipelines-prod` hangs forever.** `credential.helper` is
`manager` with no stored credential, so it blocks on an invisible prompt until killed. Always embed
the token in the URL and set `GIT_TERMINAL_PROMPT=0`. The scheme that works is Basic with username
`x-bitbucket-api-token-auth` and the `bitbucket-git-token` Key Vault secret (an Atlassian API token,
prefix `ATAT`) as the password. `x-token-auth` and email-as-username both fail.

**A startup race prints scary errors that are not real.** `SchedulerService` and
`BootstrapProvisioningService` are both hosted services and start together, so the scheduler ticks
against the *old* schema before migrations finish. Expect a burst of
`Invalid column name '<NewColumn>'` / `Scheduler tick error` in the first ~2 seconds. Only errors
timestamped **after** `Bootstrap provisioning completed.` mean anything.

**`dotnet ef migrations add` needs `--startup-project src/SqlFlow.Catalog`**, not
`src/SqlFlow.ControlPlane` (only the Catalog project references `EntityFrameworkCore.Design`):

```bash
dotnet ef migrations add <Name> --project src/SqlFlow.Catalog --startup-project src/SqlFlow.Catalog
```
Always read the scaffolded migration. It only diffs the model and cannot see data intent, so it will
silently skip backfills, identity re-keying, and de-duplication that a unique index needs.

**One test fails on a clean tree**:
`SqlFlow.ControlPlane.Tests.DeploymentReadinessTests.ApiOnlyReplica_LeavesQueuedRunsForStandaloneWorkers`
(expects `queued`, gets `failed`). It is pre-existing and unrelated. Do not chase it; if a run shows
exactly one failure and it is this one, the suite is green. Confirm suspicion with
`git stash && dotnet test --filter FullyQualifiedName~<Test> && git stash pop`.

**Integration tests need a database.** Filter them out for a fast signal:
`dotnet test --filter "Category!=Integration"` (≈4,083 pass).

## Working style the user expects

- **Act, don't over-ask.** They want the change shipped, not a survey of options or a risk essay.
- **Lead with the decision.** Short answers. No walls of text, no headers on a simple reply.
- **Never give time estimates.** Describe scope instead.
- **No em dashes anywhere** (see CLAUDE.md). Commas, colons, parentheses.
- **Commit directly to `main`** in both repos; never create a branch, never attribute a commit to Claude.
- Report faithfully: if a test fails, say so; if something is unverified, say which part.
