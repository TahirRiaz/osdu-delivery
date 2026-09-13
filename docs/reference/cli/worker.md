# sqlflow worker

## Synopsis

```bash
sqlflow worker [--db <conn-ref>] [--poll-seconds N] [--pool a,b] [--node-name NAME] [--drain-seconds N] [-v]
```

## Description

Runs this host as a compute node. The worker drains the catalog's durable run queue: it atomically claims queued runs, executes each one through the same `DocumentExecutor` a direct `sqlflow run` uses (src/SqlFlow.Execution/DocumentExecutor.cs), streams the run's events into the catalog while it executes, and records the outcome under the run id the trigger already returned. A node inside a private network can therefore run the delivery flows the control plane queued without the control plane ever reaching it; every connection is outbound (SQL to the catalog, git to the repository remotes for pinned runs, HTTPS to the drop location and the OSDU endpoint).

The queue is the catalog's `[catalog].[Run]` table itself, so queued and running work survives restarts and any number of workers drain it concurrently: each claim is one atomic T-SQL statement, so two nodes never double-run a flow. Every credential (the catalog connection, the drop location, the OSDU client secret and API key, git tokens) resolves from this node's own environment; nothing sensitive travels through the queue. The node also holds the catalog, so the delivery ledger is live for every run it executes ([the ledger](../../delivery/ledger.md)).

The control plane hosts the very same drain loop in-process (`RunExecutionWorker`, src/SqlFlow.ControlPlane/Background/RunExecutionWorker.cs, on by default under `ControlPlane:Worker:Enabled`), so standalone workers are only needed when compute must live somewhere else: closer to the drop and the OSDU endpoint, inside a network boundary, or scaled out horizontally.

The command runs until it receives a stop signal (SIGINT from Ctrl+C, or the SIGTERM an orchestrator sends when it reclaims the replica) or an operator asks the node to restart. A stop means "stop claiming and finish what you already hold": the in-flight runs keep executing and record their own outcomes, for up to `--drain-seconds`. See "Shutdown and the drain" below, which is the difference between a routine scale-in costing nothing and it destroying a run's progress.

## Arguments

The worker verb takes no positional argument. `worker` is in the parser's no-file verb list (src/SqlFlow.Cli/Program.cs), so a bare `sqlflow worker` starts the drain loop with the default `--db` reference; it never prints usage or exits 1 for a missing file. `--db`, `--pool`, `--node-name`, `--poll-seconds` and `--drain-seconds` are registered value-taking options (`ValueTakingOptions` in the same file), so their values are consumed as option values, never as positionals, and can appear anywhere on the command line.

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--db <conn-ref>` | connection reference | `${env:SQLFLOW_CATALOG_DB}` | The catalog connection as a secret reference (`${env:NAME}`, `${keyvault:vault/secret}`), resolved through the secret resolver. A resolution failure prints `ERROR  <redacted message>` to stderr and exits 1. |
| `--poll-seconds N` | integer | `5` | How long the loop waits after finding the queue empty, with a floor of 1: `0` is raised to 1, and a negative or non-numeric value falls back to 5. |
| `--pool a,b` | comma-separated list | empty | The pools this node serves. The node always drains untargeted runs; with `--pool` it additionally drains runs routed to any of the listed pools. Entries are trimmed and empty entries are dropped. The first entry is the pool the node's heartbeat is attributed to in the fleet view. |
| `--node-name NAME` | string | `${env:SQLFLOW_NODE_NAME}`, else the machine name | The name this node registers in the fleet, stamps on the runs it claims and recovers its orphans under. Give each node process on one host a name of its own (a worker beside a control plane that hosts its in-process worker, or two workers on one VM): nodes sharing a name share a fleet row, and the startup recovery of either requeues the runs the other is executing. Trimmed; at most 256 characters and no control characters, or the process prints `ERROR` and exits 1. |
| `--drain-seconds N` | integer | `540` | How long a stopping node keeps executing the runs it already claimed before severing them (`RunWorker.DefaultDrainTimeout`, nine minutes), with a floor of 0. Keep it BELOW the orchestrator's termination grace period, or the platform's kill lands mid-drain and severs the work anyway. `0` severs at once; a negative or non-numeric value falls back to 540. |
| `-v`, `--verbose` | flag | off | Sets the console minimum log level to Debug (default Information). Every log line goes to stderr; stdout carries only the banner and the stop line. |

## Behavior

### Startup

1. The nearest git-ignored `.sqlflow/env` file (searched from the current directory upward) is applied to the process environment, the process environment winning. The `--db` reference is then resolved; on failure the process exits 1 before anything else happens.
2. The node's identity is `--node-name`, else `SQLFLOW_NODE_NAME`, else its machine name (`NodeIdentity.Resolve`, src/SqlFlow.Node/NodeIdentity.cs). It is stamped onto every run this node claims (`ClaimedByNode`) so work is attributable and recoverable, and it keys the node's row in the fleet registry (`[catalog].[Node]`). The startup recovery in step 4 matches runs by this name, so two node processes on one host must not share it: a worker beside a control plane that hosts its in-process worker (`ControlPlane:Worker:NodeName`), or two workers on one VM, would each requeue the runs the other is executing.
3. The worker prints its banner and starts the drain loop:

   ```text
   Worker 'NODE-01' draining the run queue (poll 5s, pools: untargeted runs only, drain 540s). Press Ctrl+C to stop, again to stop without draining.
   ```

   With pools the banner lists them instead: `pools: onprem, cloud`.

4. Orphan recovery runs once, before the first claim: any run left `running` and claimed by this node name (an orphan from a previous incarnation that stopped mid-run) is dispositioned exactly as the control plane's liveness reaper would (see "Crash recovery" below), and the same is done for compute tasks. The log line is `Recovered N run(s) left running by a previous worker incarnation; requeued.` Recovery is best-effort; a briefly unreachable catalog does not stop the worker from starting.

### The drain loop

The heartbeat runs on its own task every 15 seconds, independent of draining: it upserts the node's row (name, assembly version, pool, last-seen, and `BusyRuns`, the number of runs executing right now, which is the autoscaler's busy signal) and reads back any restart request an operator stamped on the row. It is best-effort and never interrupts draining. Because it is decoupled from the loop, a node saturated with long runs still beats and is never mistaken for a dead one; the control plane reports a node online while its last beat is within 60 seconds.

Each loop iteration observes operator cancels for the runs it is executing, drains the compute-task queue, drains the run queue, then waits `--poll-seconds` and repeats. Draining means: wait for a free concurrency slot, claim one item, start it on its own task with its own DI scope, and claim again until the queue is empty for this node. A node executes up to 4 runs at once (`RunWorker.DefaultMaxConcurrentRuns`) and 2 compute tasks (`DefaultMaxConcurrentComputeTasks`), and never claims more than it can execute, so a saturated node leaves queued work claimable by other nodes. A finishing run's slot goes straight to the next queued run, with no poll gap.

Compute tasks are drained first: they are the interactive operations the GUI requests against a flow's target and that must run on a node that can reach it (`delivery-probe`, `delivery-read` and `delivery-delete`, src/SqlFlow.Delivery/Engine/Operations/DeliveryOperations.cs). They are routed by pool exactly like runs, on their own bounded gate, so a burst of them never starves run execution and a node full of long runs still answers them promptly.

The standalone worker idles on a plain delay; the control plane's in-process host idles on `RunQueueSignal` (a one-slot nudge) with `ControlPlane:Worker:PollMilliseconds` (default 2000) as the fallback, so a triggered run starts there within milliseconds.

### Claiming

The claim is one atomic T-SQL UPDATE; the leading SET pins the READ COMMITTED isolation level, which READPAST requires (a pooled connection can carry a leftover level from a prior transaction). From src/SqlFlow.Catalog/RunQueueStore.cs:

```sql
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
UPDATE [catalog].[Run]
SET [Status] = @running, [ClaimedByNode] = @node, [StartUtc] = @now, [Attempt] = [Attempt] + 1
OUTPUT inserted.[RunId], inserted.[Attempt]
WHERE [RunId] = (
    SELECT TOP (1) r.[RunId] FROM [catalog].[Run] AS r WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE r.[Status] = @queued AND {POOL_PREDICATE}
      AND (r.[GroupId] IS NULL OR NOT EXISTS (
          SELECT 1 FROM [catalog].[Run] AS s
          WHERE s.[GroupId] = r.[GroupId] AND s.[GroupWave] < r.[GroupWave]
            AND s.[Status] IN (@queued, @running)))
      AND (r.[GroupMaxConcurrency] IS NULL OR (
          SELECT COUNT(*) FROM [catalog].[Run] AS w
          WHERE w.[GroupId] = r.[GroupId] AND w.[Status] = @running) < r.[GroupMaxConcurrency])
      AND NOT EXISTS (
          SELECT 1 FROM [catalog].[Run] AS p
          WHERE p.[PipelineId] = r.[PipelineId] AND p.[Status] = @running)
    ORDER BY r.[EnqueuedUtc], r.[RunId])
  AND [Status] = @queued;
```

`UPDLOCK` takes the update lock up front, `READPAST` makes concurrent workers skip rows another worker has already locked (each claim gets a different run instead of blocking), and the statement's atomicity is what makes any number of workers safe. `{POOL_PREDICATE}` is `r.[TargetPool] IS NULL` for a worker with no pools, or `(r.[TargetPool] IS NULL OR r.[TargetPool] IN (@pool0, ...))` for a pooled worker; pool names are always bound as parameters.

Three gates filter rather than lock, so a blocked run is simply not selected and the worker moves on to the next eligible one: a member of a run group (a schedule fire that runs several flows) is claimable only once every member in a lower wave is terminal; a group carrying a concurrency cap admits only that many running members; and a run whose pipeline already has a `running` execution waits its turn, so a double trigger, or a schedule firing over a still-running run, queues behind it instead of racing it. That last gate is guaranteed by the database, not merely checked: the filtered unique index `UX_Run_RunningPipeline` rejects a second running row per pipeline, and a claim that loses the race retries against the next eligible run (up to three times per poll).

The claim increments `[Attempt]` and returns it alongside the run id. That value is the claim's fencing token: every outcome write this node makes for the run (complete, fail, cancel) is conditional on the row still being `running`, claimed by this node, at exactly this attempt. It doubles as the execution counter that bounds crash-recovery requeues (see "Crash recovery" below).

Run statuses are the strings `queued`, `running`, `succeeded`, `failed`, `cancelled` and `skipped` (a group member passed over because an earlier wave did not succeed), src/SqlFlow.Catalog/CatalogEntities.cs. Run ids are time-ordered (`Guid.CreateVersion7()`) and minted at enqueue, so the control plane's run API reflects a run from the moment it is queued.

### Cancellation

A queued run is cancelled outright (it never ran). A run this node is already executing is cancelled cooperatively: the cancel stamps `CancelRequestedUtc` on the row, the node observes it on its next loop iteration (and every 5 seconds during a drain), trips that run's cancellation token so the engine aborts its in-flight work, and records the run `cancelled` with the error `The run was cancelled by an operator while executing.` under the claim fence. A cancel that arrives after the run finished on its own changes nothing.

### Version pinning and flow file resolution

At enqueue time a run is pinned to a commit: an explicit `commitSha` is honored verbatim, and when omitted the run is pinned to the repo's last successfully synced commit (`LastSyncedSha` of the repo's managed-sync source), provided the repo has a remote URL to materialize from. Only a repo with no resolvable synced commit produces an unpinned run. The enqueue also stamps the run with the content hash of the pipeline's YAML (`FlowVersionHash`), staged in the catalog's content-addressed version store.

On the worker, the flow root is resolved in this order (src/SqlFlow.Node/RunWorker.cs):

1. **The catalog's YAML snapshot** is probed first. It can only serve a document that executes from a bare single-file copy, and a delivery flow never does: its mappings live beside it in the repository tree (`DeliveryFlowDocument.RequiresRepoTree` is always true), so for a delivery flow this step always hands over to git. A retrieval or cache flow needs nothing beside its file (`RequiresRepoTree` is false): a cache flow's versions are written into the catalog, never into the tree.
2. **A SHA-pinned run** is materialized from the repo's remote by `GitMaterializer` (src/SqlFlow.Node/GitMaterializer.cs) into a per-user cache at `<temp>/sqlflow/node-cache/<first-16-hex-of-sha256(remoteUrl)>/<commitSha>`. The clone is built in a staging directory and published atomically, so an interrupted clone never leaves a half-written checkout; a directory already checked out at the commit is reused across runs, and concurrent runs of the same commit share one clone. The git credential is the repo source's stored `${...}` credential reference, resolved by this node's own secret resolver (with the source's username when it declares one); a repo with no source or no reference falls back to `SQLFLOW_GIT_TOKEN` and `SQLFLOW_GIT_USERNAME` from the node's environment (an unset username defaults to `x-access-token`). A freshly provisioned node therefore needs no pre-synced repo state.
3. **An unpinned run** executes from the repo's locally synced root path on this node.

The flow file is the flow root combined with the pipeline's relative path from the catalog.

### Execution and recording

The flow file is loaded through the same `DocumentLoader.Load` every host uses; a secret-hygiene finding is logged as a warning prefixed with the run id. The run's parameters travel from the queue row into the engine here, the one handoff point: `RunParameters` (src/SqlFlow.Core/Runs/RunParameters.cs) carries the operation (`deliver`, `verify`, `plan`, `known-state`), `force`, the `--set` values, an explicit drop location, a submission id, the record keys the run is scoped to, and a known-state publish location. The stored JSON is parsed and re-validated before the engine sees it. The actor recorded on the ledger is who requested the run, or the trigger source (`schedule`, `manual`, `cli`) when nobody did.

While the run executes, `CatalogRunEventSink` (src/SqlFlow.Node/CatalogRunEventSink.cs) writes every canonical run event (step, message, level, rows, elapsed) as a `[catalog].[RunEvent]` row from a single background writer, so the GUI's run timeline streams while the run is still executing and survives a mid-run crash. The feed is best-effort: a failed write stops it, and completion fills the gap. That table is the run trace; the control plane's run-trace reaper applies retention to it.

The engine also writes the run history next to the flow file, under `.sqlflow/runs/<flow-name>/<yyyyMMdd-HHmmss>_<first-8-hex-of-run-id>/` (src/SqlFlow.Core/Runs/RunHistoryWriter.cs): `run.json` (the stable envelope: flow kind and name, run id, success, error, host, the result counts, the `events` array) and `run.log` (the rendered run log). The delivery kind writes no other artifact. On a worker the flow file sits inside the node cache, so that history accumulates per materialized commit; the writer keeps the newest 50 folders per flow.

On completion the outcome is recorded from `run.json` (`RunQueueStore.CompleteFromArtifactAsync`): status, timings, error, host, the submission and the record counts (planned, delivered, held, failed, skipped), under the claim fence. Only the tail of events the live feed did not write is appended, so the rows the trace stream already delivered keep their ids. If the artifact is missing, oversized (over 64 MiB), or corrupt, the run is still driven to `failed` with the reason so it never lingers in `running`; if no run folder was written at all, the run is failed with the engine's own error. Driving a run to `failed` is a no-op if the run is already terminal, so a late failure never overwrites a recorded success. If the fence rejects the write (the run was requeued out from under a node presumed dead, and possibly re-claimed), the node logs a warning and drops its result: the successor execution's outcome is authoritative. A failed or cancelled member of a run group marks the group's later-wave members `skipped` in the same transaction.

### Failure handling

One run's failure never tears down the loop or its sibling runs: a per-run catch drives the run to a terminal `failed` state (with a secret-redacted error) and the loop continues. A poll or claim error (a transient database outage) is logged as `Run queue poll error: <redacted>` and retried on the next tick.

Failure messages written to the run row, verbatim from src/SqlFlow.Node/RunWorker.cs, src/SqlFlow.Node/GitMaterializer.cs and src/SqlFlow.Catalog/RunQueueStore.cs:

| Message | Cause |
| --- | --- |
| `the run is not attributed to a repository.` | The run row has no repo id. |
| `the run's repository or pipeline is no longer in the catalog.` | The repo or pipeline row was removed between enqueue and claim. |
| `run is pinned to commit '<sha>' but repository '<name>' has no remote URL to materialize from.` | A pinned run on a repo without a remote. |
| `repository '<name>' has no synced root path on this node.` | An unpinned run on a node with no local copy. |
| `the flow file for '<flow>' was not found on this node.` | The resolved flow path does not exist. |
| `could not materialize '<remote>' at '<sha>': <cause>` | The git clone or checkout failed. |
| `commit '<sha>' was not found in '<remote>'.` | The pinned commit is not in the remote (a rewritten branch, a wrong SHA). |
| `the git credential reference '<ref>' resolved to an empty value; create the secret in the vault it points to.` | The repo source's credential reference resolves to nothing. |
| `the run executed but its result could not be recorded: <reason>.` | `run.json` was missing, oversized, or unreadable. |
| `The run was cancelled by an operator while executing.` | An operator cancel was honored mid-run. |
| `Run interrupted: its claiming node '<node>' stopped without recording an outcome, and this was execution attempt <N> of 3, so it is not requeued again (a run that repeatedly dies mid-flight is treated as the cause). Re-trigger the flow to run it once more.` | Crash recovery found the attempt budget exhausted (see below). |

### Crash recovery, the attempt budget, and the claim fence

Losing a worker mid-run is recoverable, never terminal for the pipeline. Two sweeps repair `running` rows whose executing process is gone:

- **Same-node restart recovery** (this worker's startup, step 4 above) matches orphans by this node's name.
- **The control plane's liveness reaper** (`OrphanRunReaper`, sweeping every `ControlPlane:Reaper:PollSeconds`, default 30) matches any run whose claiming node has not heartbeated within `ControlPlane:Reaper:StaleAfterSeconds` (default 180, floor 60); it covers pods that die and never return under the same name.

Both apply the same disposition, implemented in `RunQueueStore` (src/SqlFlow.Catalog/RunQueueStore.cs):

1. An orphan with a pending operator cancel is recorded `cancelled`: the cancel intent is authoritative, and a requeue would resurrect work the operator explicitly killed.
2. An orphan whose `Attempt` is under `MaxExecutionAttempts` (3) goes back to `queued` with the claim and start time cleared, for any eligible worker to claim again. The claim consumed the attempt, so the budget decrements even when the execution was lost.
3. An orphan that has consumed the whole budget is `failed` with the `Run interrupted` message above (its group's later waves skipped): a run that repeatedly dies with its node is treated as the cause, not the victim. This is the poison-run bound that stops a memory-exhausting flow from crash-looping the fleet forever.

Every recovery write is a conditional update guarded on the exact orphaned claim (still `running`, same node, same attempt), so a run its real node completes in the same instant is never overwritten, and concurrent reaper replicas are idempotent.

The fence closes the zombie race: a node that was only presumed dead (its heartbeats blocked, its process alive) may finish after its run was requeued and re-claimed. Its outcome writes present the old attempt and are dropped, with a warning saying the write was dropped by the claim fence; the successor's writes present the current attempt and land. A requeue is safe because a delivery run is idempotent at the record grain: the ledger leases each record to one attempt at a time, a lease a severed node held expires and the record is picked up by the next pass, and a record whose rendered document is unchanged is skipped by the change gate ([the ledger](../../delivery/ledger.md#leasing)).

### Shutdown and the drain

Both stop signals are intercepted, so the process is never killed abruptly on the first signal:

- **SIGTERM**, which is what an autoscaler reclaiming this replica, a revision swap, or `docker stop` sends. This is the one that matters in production; the runtime's default handling of it terminates the process on the spot.
- **SIGINT**, the interactive Ctrl+C.

Both are registered through `PosixSignalRegistration` with `Cancel = true` (src/SqlFlow.Cli/Program.cs), which suppresses the default termination and hands control back to the worker. An operator's restart request (`POST /api/v1/nodes/{name}/restart` stamps `RestartRequestedUtc` on the node's row; the worker honors it on its next heartbeat when it is newer than its own start, so a stale request never bounces a replacement) trips the same stop. The worker then:

1. **Stops claiming.** The queue is left alone, so queued runs stay available to other nodes.
2. **Drains.** Runs and compute tasks already in flight keep executing under a cancellation token that is deliberately NOT linked to the stopping token, so they finish and record their own outcomes. The log says `Stopping: no longer claiming; draining N in-flight item(s) so each records its own outcome (up to 540s).` Operator cancels are still polled every 5 seconds during the drain, so a drain can never trap a run an operator has asked to kill. The node keeps heartbeating for the whole drain, which is load-bearing: a silent node is declared dead by the liveness reaper after `StaleAfterSeconds` and its runs are requeued, so a draining node that stopped beating would have the very work it is finishing re-executed underneath it.
3. **Severs only on timeout.** If work is still in flight after `--drain-seconds`, it is cancelled and left `running` for recovery, with a warning naming the expired window (`Drain window of Ns expired with work still in flight; cancelling it. ...`). A node cannot drain forever, because the platform that asked it to stop will kill it regardless. When everything finishes in time the log says `Drained: every in-flight item finished and recorded its outcome.`
4. Stops the heartbeat, prints `Worker stopped.` and exits 0.

A **second** stop signal skips the drain: it falls through to the runtime's default termination, so a worker is never unkillable. The severed runs are then requeued exactly as a crash would leave them.

Why this matters: a severed run records no outcome at all, so it is recovered only by the reaper's requeue, which **consumes one of its three execution attempts** and repeats all of its work. In an autoscaled fleet where replicas are reclaimed routinely, three unlucky stops inside one run's life will fail a perfectly healthy run and report that the run itself was the cause. The drain is what stops routine scale-in from manufacturing those failures.

**Operational requirement:** the drain window is only real if the orchestrator waits for it. Set the platform's termination grace period ABOVE `--drain-seconds` (Kubernetes `terminationGracePeriodSeconds`, Container Apps `terminationGracePeriodSeconds`; both default to 30 seconds). deploy/k8s/worker-pool.yaml and deploy/bicep/worker.bicep set 600 against the default 540 second drain. If the grace period is left at the default, the drain starts correctly and is then killed 30 seconds in, which rescues only the runs that were nearly finished. See [Deployment](../guides/deployment.md#scale-in-must-not-sever-runs-the-grace-period-is-not-optional).

## Environment variables

[Environment variables](../../environment-variables.md) is the canonical list. The worker reads:

| Variable | Read by | Purpose |
| --- | --- | --- |
| `SQLFLOW_CATALOG_DB` | the default `--db` reference | The catalog connection string. |
| `SQLFLOW_GIT_TOKEN` | `GitMaterializer` | Token for private git remotes when the repo source declares no credential reference of its own (optional). |
| `SQLFLOW_GIT_USERNAME` | `GitMaterializer` | Username paired with the token (optional; defaults to `x-access-token`). |
| `SQLFLOW_AZURE_AUTH` | the secret resolver and the Azure storage paths | How `${keyvault:...}` references and Azure storage drop locations authenticate; see [sqlflow auth](auth.md). |
| `SQLFLOW_DELIVERY_ALLOW_LOOPBACK` | the delivery URL guard | `true` lets a flow target a loopback address (a local OSDU stub); off by default. |
| `SQLFLOW_WORKER_POOL` | deploy/docker/worker-entrypoint.sh (container image only) | Translated to `--pool`. |
| `SQLFLOW_WORKER_POLL_SECONDS` | deploy/docker/worker-entrypoint.sh (container image only) | Translated to `--poll-seconds`. |
| `SQLFLOW_WORKER_DRAIN_SECONDS` | deploy/docker/worker-entrypoint.sh (container image only) | Translated to `--drain-seconds`. |

In addition, every `${env:...}` reference the flows themselves use must resolve on this node: the sample flow (samples/recall-welllog/flows/recall-welllog.yaml) names `PETRODB_URL`, `OSDU_TOKEN_URL`, `OSDU_SCOPE`, `OSDU_CLIENT_ID`, `OSDU_CLIENT_SECRET` and `APIM_KEY`. Credentials are resolved at the edge, per node; the control plane carries none of them. For local development the CLI applies the nearest git-ignored `.sqlflow/env` file before resolving anything.

## Container image

Dockerfile.worker packages the worker as a container (a framework-dependent publish of src/SqlFlow.Cli inside the Linux SDK image, so LibGit2Sharp's linux-x64 native assets ship, plus `libssl3` on the runtime image for HTTPS remotes) whose entrypoint (deploy/docker/worker-entrypoint.sh) composes the `sqlflow worker` invocation from `SQLFLOW_WORKER_POOL`, `SQLFLOW_WORKER_POLL_SECONDS` and `SQLFLOW_WORKER_DRAIN_SECONDS`; the catalog connection stays on the CLI default `${env:SQLFLOW_CATALOG_DB}`, so it never appears in `ps` output. All three are optional, so a bare container drains untargeted runs on the default cadence. The container exposes no ports and needs only outbound SQL, git and HTTPS.

- deploy/compose/docker-compose.yml runs it as the `worker` service, with the control plane's in-process worker turned off (`ControlPlane__Worker__Enabled=false`) and the sample flow's six `${env:...}` references passed through from `.env`.
- deploy/k8s/worker-pool.yaml is the template for one pool: a Deployment plus a KEDA `ScaledObject` whose mssql query targets the greatest of three terms (claimable queued runs divided by the node's concurrency of 4, plus nodes busy within the last 60 seconds; the pool's `MinReplicas` floor; an active manual override from `[catalog].[WorkerPool]`), so occupied workers are never scaled away mid-run. It scales to zero when nothing is queued and no node is busy, keeps a warm worker for 300 seconds, and sets `terminationGracePeriodSeconds: 600`.
- deploy/bicep/worker.bicep deploys the same worker as an Azure Container App with the same scale rule and grace period.

See [Deployment](../guides/deployment.md).

## Examples

Run an untargeted worker on a VM that can reach the catalog:

```bash
export SQLFLOW_CATALOG_DB='Server=sql01;Database=SqlFlowCatalog;Integrated Security=True;TrustServerCertificate=True'
sqlflow worker --poll-seconds 5
```

```text
Worker 'NODE-01' draining the run queue (poll 5s, pools: untargeted runs only, drain 540s). Press Ctrl+C to stop, again to stop without draining.
```

Serve two pools with a faster poll and debug logging:

```bash
sqlflow worker --pool onprem,cloud --poll-seconds 2 -v
```

Point at a different catalog through an explicit reference:

```bash
sqlflow worker --db '${env:SQLFLOW_CATALOG_DB_PROD}' --poll-seconds 5
```

Run the containerized worker (built from the repository root), with the credentials the sample flow references supplied from the shell environment:

```bash
docker build -f Dockerfile.worker -t sqlflow-worker:latest .
docker run -d \
  -e SQLFLOW_CATALOG_DB="Server=catalog;Database=SqlFlowCatalog;User ID=sqlflow;Password=$SQL_PASSWORD;TrustServerCertificate=True" \
  -e SQLFLOW_WORKER_POOL='onprem' \
  -e SQLFLOW_GIT_TOKEN="$GIT_TOKEN" \
  -e PETRODB_URL="$PETRODB_URL" -e OSDU_TOKEN_URL="$OSDU_TOKEN_URL" -e OSDU_SCOPE="$OSDU_SCOPE" \
  -e OSDU_CLIENT_ID="$OSDU_CLIENT_ID" -e OSDU_CLIENT_SECRET="$OSDU_CLIENT_SECRET" -e APIM_KEY="$APIM_KEY" \
  sqlflow-worker:latest
```

Scale compute in the compose stack with no other change:

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --scale worker=3
```

## Exit behavior

| Condition | Exit code | Output |
| --- | --- | --- |
| Clean stop via SIGINT (Ctrl+C), SIGTERM, or an operator restart request, after the drain | 0 | `Worker stopped.` on stdout |
| The `--db` reference fails to resolve | 1 | `ERROR  <redacted message>` on stderr (two spaces after `ERROR`) |
| A malformed `.sqlflow/env` file | 1 | `ERROR  <path>(<line>): ...` on stderr |

Per-run failures do not affect the exit code; they are recorded on the run rows and logged, and the loop continues.

## See also

- [The control plane](../concepts/control-plane.md): the in-process worker, the orphan-run reaper and its settings, fleet visibility.
- [Deployment](../guides/deployment.md): the worker image, KEDA-scaled pools, the grace period.
- [The ledger](../../delivery/ledger.md): what a delivery run records per record, and the leasing that makes a requeue safe.
- [Environment variables](../../environment-variables.md): every variable and secret reference, in one place.
