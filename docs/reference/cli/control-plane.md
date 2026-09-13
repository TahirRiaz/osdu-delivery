# Control-plane verbs

The `sqlflow` binary drives the same `/api/v1` API the GUI uses, so anything verified in the browser can be
verified from a terminal, a shell script, or an integration. The target resolves from `--url` or
`SQLFLOW_URL`; the credential from `--token`, then `SQLFLOW_TOKEN` (both suppliable through the git-ignored
`.sqlflow/env`), then the per-URL store `login` writes (`~/.sqlflow/credentials.json`, relocatable with
`SQLFLOW_CREDENTIALS_FILE`). Human output goes to stdout with notes on stderr; `--json` switches stdout to the
raw API shapes. Exit codes: 0 success, 1 error or a followed run that did not succeed, 130 on Ctrl+C.
(src/SqlFlow.Cli/Program.cs, src/SqlFlow.Cli/Remote/RemoteVerbs.cs, src/SqlFlow.Cli/Remote/RemoteVerbs.Estate.cs)

## Signing in

```text
sqlflow login --url https://delivery.example.com --username tahir   # password from a hidden prompt / piped stdin
sqlflow login --device                                              # RFC 8628 browser grant, for SSO accounts
sqlflow login --with-token                                          # paste an existing sqlf_ personal access token
sqlflow login --username ci-bot --no-store                          # print the minted secret once (CI bootstrap)
sqlflow logout                                                      # revoke the stored token and remove it
sqlflow whoami                                                      # subject, role, scopes, credential source
```

Password and device sign-ins mint a personal access token (`--token-name`, default `cli@<machine>`;
`--expires-days`, default 90; `--no-expiry`; `--scopes "read operate"` to narrow below the account's own) and
store that, never the password and never the expiring session JWT, so the stored credential is always
revocable server-side.

```text
sqlflow user reset-password <username> [--db <conn-ref>]   # local-user password reset, direct against the catalog
```

`user reset-password` is a direct-catalog break-glass verb (it does not call the API): it resets a local user's
password in the catalog database addressed by `--db` (default `${env:SQLFLOW_CATALOG_DB}`), for recovering an
account when no one can sign in. The new password comes from a hidden prompt, never a flag. It only affects
local users; an SSO (Entra) user's credential lives in the external identity provider, not the catalog.

## Execution

```text
sqlflow trigger --repo recall --flow recall-welllog --set logSource=STAT_COMP              # the hourly deliver, now
sqlflow trigger --repo recall --flow recall-welllog --operation plan --set logSource=STAT_COMP --follow
sqlflow trigger --repo recall --flow recall-welllog --operation verify --record <key> --force
sqlflow trigger --repo recall --flow recall-welllog --preview                              # shown, not enqueued
sqlflow runs list [--repo r] [--status failed] [--flow recall-welllog] [--batch recall] [--kind delivery]
                  [--group <groupId>] [--latest] [--page N --page-size N]
sqlflow runs show <runId>                                             # the header: parameters, counts, error
sqlflow runs trace <runId> [--follow]                                 # the trace as text; --follow rides the live SSE
sqlflow runs cancel <runId>                                           # API when a URL is configured; --db forces the
                                                                      # direct-catalog break-glass route
sqlflow groups show <groupId> [--follow] | cancel <groupId> | rerun <groupId> [--follow]
```

`trigger` enqueues one flow through `POST /runs`, exactly as the GUI's trigger dialog does. `--repo` (a repo
name or id) and `--flow` are required. `--pool` routes the run to a worker pool, `--commit` pins an exact git
object id (otherwise the run is pinned to the repo's last synced commit), `--preview` prints what would be
enqueued and stops, and `--follow` attaches to the live trace and exits by the run's outcome. The delivery
run options (`--operation deliver|verify|plan|known-state`, `--force`, `--set name=value`, `--drop`,
`--submission`, `--record`, `--redeliver`, `--publish-to`) are the same as a local `sqlflow run` and are parsed and
validated once for both; see [the delivery run options](delivery.md). A `--scope` other than `flow` is
refused: a whole set of flows runs through its schedule (`sqlflow schedules run <id>`), whose membership is
what a fire runs.

`runs trace` prints the plain-text trace document; with `--json` it prints the first 200 trace entries. A run
group is a schedule fire with several members; `groups rerun` fires that schedule again (the members are
re-resolved from its current membership), and refuses a group that was not fired by a schedule.

## Estate

```text
sqlflow summary                                    # the dashboard rollup
sqlflow nodes [--page N --page-size N]             # the worker fleet, heartbeat-derived liveness
sqlflow schedules list [--repo r] [--enabled true|false] | show <id>
sqlflow schedules create --repo r --flow f[,f2,...] (--cron "0 * * * *"|--interval 3600)
                 [--name n] [--timezone Europe/Oslo] [--max-concurrency n] [--disabled] [--catchup]
                 [--operation deliver|verify|plan|known-state]
sqlflow schedules run <id> | pause <id> | resume <id> | delete <id>
sqlflow repos list | show <name|id> | sync <name|id> # git source sync-now, or local-path re-sync
sqlflow repos register --name recall --remote-url https://... [--branch main] [--interval 300]
                 [--credential-ref '${env:GIT_TOKEN}'] [--credential-user git] [--disabled]
sqlflow repos discover --remote-url https://... [--branch b] [--credential-ref r] [--credential-user u]
sqlflow pipelines list [--repo r] [--kind delivery] [--active true] [--name n] [--page N --page-size N]
sqlflow pipelines show <id> [--yaml|--definition]
sqlflow search <term> [--flows] [--page N --page-size N]
```

`schedules create` takes exactly one of `--cron` and `--interval` (seconds); `--flow` takes one name or a
comma-separated member list, and every member joins the same wave-ordered fire. `--operation` is the operation
every fire carries (`deliver` by default), so a drift check is a second schedule with `--operation verify`
next to the hourly deliver. `schedules run <id>` fires the schedule now without moving its cadence and prints
the run, or the group, to follow.

`repos register` refuses a `--credential-ref` that looks like a raw token: it must be a `${env:NAME}` or
`${keyvault:vault/secret}` reference. `repos sync` picks the right mechanism per repo: a repo backed by a
managed git source is made due now, a local-path repo is re-synced from disk.

`pipelines show` prints the facts; `--yaml` or `--definition` switches stdout to the raw document. `search`
previews the top flow hits and the delivery records the ledger matched (a delivery key, an OSDU id, a source
key or a label prefix); `--flows` pages through the flow hits.

## Environment and testing helpers

```text
sqlflow health                                     # anonymous /health/live + /health/ready probe; exit 0 when both 200
sqlflow doctor                                     # env file, SQLFLOW_* presence, control plane, credential, catalog
sqlflow validate <folder> [--json]                 # every document in the estate; exit 0 only when all parse (CI gate)
sqlflow runs local [folder] [--flow x] [--last N]  # the on-disk .sqlflow/runs history, no catalog needed
sqlflow completions bash|zsh|powershell            # shell completion script on stdout
```

`sqlflow health` needs no credential (both probes are anonymous): it reports the `/health/live` and
`/health/ready` status codes and bodies, and exits 0 only when both answer 200. A failing `ready` under a
passing `live` usually means the catalog database is unreachable from the control plane.

`sqlflow doctor` reports which `.sqlflow/env` was found, which `SQLFLOW_*` variables are set (presence only,
never values), whether the control plane is live and ready and who the credential authenticates as, and
whether the catalog schema is current. Unconfigured surfaces are `SKIP`, not failures; it exits 1 only when a
configured surface fails its probe.

## See also

- [The control plane](../concepts/control-plane.md): the API these verbs call.
- [Authentication and identity](../concepts/authentication-and-identity.md): tokens, scopes, the device grant.
- [`sqlflow check`, `cache`, `template`, and the delivery run options](delivery.md)
- [`sqlflow db`](db.md), [`sqlflow worker`](worker.md), [`sqlflow validate`](validate.md), [`sqlflow auth`](auth.md)
- [Environment variables and secrets](../../environment-variables.md)
