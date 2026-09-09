# Operations

## Deployables

The platform's own: the control plane (`src/SqlFlow.ControlPlane`) and compute nodes (`sqlflow worker`),
plus the CLI on a workstation. The delivery domain adds no process. Nodes deliver; the control plane
schedules, records and answers. See [../architecture.md](../architecture.md) and
[../../deploy/README.md](../../deploy/README.md).

## Configuration

Everything the platform already reads ([../environment-variables.md](../environment-variables.md)), plus:

| Setting | Where | Purpose |
| --- | --- | --- |
| Flow secrets | nodes, the CLI | Whatever the flows reference: `${env:PETRODB_URL}`, `${keyvault:vault/name}`, and so on. A node holds the references its pool's flows need. |
| `SQLFLOW_DELIVERY_ALLOW_LOOPBACK` | nodes, the CLI | `true` lets a flow target `localhost` (local OSDU stubs, tests). Off by default: the URL guard refuses loopback and private targets. |
| `ControlPlane:MaxRequestBodyMegabytes` | control plane | The API's request body ceiling, set on purpose rather than left at Kestrel's default. Default 64. |
| Repository layout | flow repositories | `mappings/` and `snapshots/` next to the flows (or named under `render`), committed and synced. |

## First deployment

1. Grant the nodes' identity read on the drop container, write on the work location (`source.work`, or the
   drop's `.work` folder by default), and read/write on the snapshot, known-state and retrieval locations (the
   Unity Catalog external-location grant is on the critical path for Databricks; see [design.md](design.md)
   section 3.1).
2. Provision the catalog: the control plane migrates it on start, or `sqlflow db migrate --db <ref>`. The
   ledger's tables come with it.
3. In the flow repository, capture the snapshots and commit them:

   ```bash
   sqlflow snapshot flows/recall-welllog.yaml schema --kind osdu:wks:work-product-component--WellLog:1.4.0
   sqlflow snapshot flows/recall-welllog.yaml references --spec capture-spec.json
   sqlflow check flows/recall-welllog.yaml --set logSource=STAT_COMP
   ```

   The schema and reference calls use the flow's target endpoint and credentials (`--endpoint` overrides the
   endpoint, `--from-dir` reads a local checkout instead).
4. Register the repository as a source in the GUI (Repos) and sync it. The flow appears as a pipeline of kind
   `delivery`; its mappings and snapshots appear under Mappings.
5. Have the preparing side write a drop and plan it before anything touches OSDU: trigger a run with
   operation `plan` and the flow parameters (the GUI's Trigger run dialog, or
   `sqlflow run flows/recall-welllog.yaml --operation plan --set logSource=STAT_COMP`).
6. Deliver the canary log source (a run with operation `deliver`), then the rest. Put the hourly deliver on
   a schedule.

## The API

Every delivery route lives under `/api/v1/delivery` and uses the platform's tokens and scopes.

| Route | Scope | Purpose |
| --- | --- | --- |
| `POST /submissions` | operate | The manifest notification: `{ pipelineId or flow (+ repoId), drop, parameters, force }`. Queues the deliver run and answers 202 with its id. |
| `GET /flows/{pipelineId}/stats` | read | Record counts by state, drift, the last 24 hours, the last submission. |
| `GET /flows/{pipelineId}/records` | read | Paged, filtered records: `search` (a delivery key, or a prefix over label, source key and OSDU id; `mode=contains` for substring), `status`, `submissionId`, `runId` (the records that run touched, through its attempts), `drifted`. |
| `GET /flows/{pipelineId}/target` | read | Where the flow's records live: endpoint as declared, data partition, protocol, auth type, and the path each removal scope calls. |
| `GET /flows/{pipelineId}/submissions` | read | The flow's submissions, newest first. |
| `GET /flows/{pipelineId}/retrievals` | read | A retrieval flow's runs, newest first: window, location, counts, outcome. |
| `GET /records/{key}`, `/attempts`, `/activities` | read | One record, its delivery history, its interventions. |
| `GET /submissions/{id}`, `/attempts` | read | One submission with the runs that carried it, and its attempts. |
| `GET /submissions/{id}/batches` | read | The submission's work batches, paged, filterable by `status`. |
| `GET /activities`, `GET /activities/{id}` | read | The audit trail, filtered by flow, kind, actor, outcome, time; one activity with its captured log. |
| `GET /mappings`, `/mappings/{id}`, `GET /snapshots` | read | What the repositories hold. |
| `GET /cache` | read | The OSDU cache as the retrieval flows declare it: each cached type, the paths it captures, and how many records the current snapshot holds for it. |
| `GET /cache/items` | read | The cached records, paged, filtered by `type` and searched with `search` over every value they hold. |
| `POST /flows/{pipelineId}/release` | operate | Release the flow's blocked records (all, or `keys`). |
| `POST /flows/{pipelineId}/probe` | operate | Queue a target probe on a node; poll `GET /api/v1/compute/tasks/{taskId}`. |
| `POST /records/{key}/release`, `/redeliver`, `/verify` | operate | Release one record; redeliver it (`scope` all, metadata or payload, `run` true queues the deliver run); queue a verify run scoped to it. |
| `POST /records/{key}/read` | operate | Queue a read-back of the record as OSDU holds it, on a node. |
| `POST /records/{key}/delete` | operate | Queue a removal of one record (`scope`: `record`, `history` or `everything`) on a node. |
| `POST /flows/{pipelineId}/records/remove` | operate | Queue a removal of many records: `scope`, and either `keys` or `filter` (the listing, every match of which goes). `expected` is refused with 409 when the filter no longer resolves to it. |
| `POST /flows/{pipelineId}/records/remove/preview` | read | What that removal would act on: how many records, how many OSDU was ever given, and the target it is aimed at. |
| `POST /ledger/prune` | admin | Age out attempts older than `olderThanDays`, keeping the latest per record. |

Runs carry the delivery parameters on the platform's trigger (`POST /api/v1/runs`): `operation`, `force`,
`values`, `drop`, `submissionId`, `recordKeys`, `publishTo`, and for a fan-out member `partitions`. The run row
records them, the delivery counts are projected onto it when the run completes, and its result (the operation's
outcome as JSON) and its fan-out membership (root, slot, count) are on the run detail.

## Removing records from OSDU

OSDU offers three removals and they are not degrees of one thing (openapi storage v2). The API, the node
operation and the GUI all name them the same way:

| Scope | Call | What goes | Reversible |
| --- | --- | --- | --- |
| `record` | `POST /records/{id}:delete`, or `POST /records/delete` for a set | The record stops resolving. Nothing is destroyed. | Yes, in OSDU |
| `history` | `DELETE /records/{id}/versions` | Every earlier version. The latest stays live. | No |
| `everything` | `DELETE /records/{id}` | The record and every version. | No |

There is no OSDU call that removes only the latest version and promotes the previous one, so the GUI does not
offer one.

A removal names its records by key or by filter. The filter form is resolved on the node when the removal runs,
so "every record this run delivered" travels as the filter rather than as tens of thousands of ids, and covers
records no page ever rendered. One removal takes at most 25,000 records; a larger one is several removals.
Records are removed in chunks of 500, batched into a single request where the protocol and the scope allow it
(only the reversible scope has a bulk endpoint), and each record gets its own ledger attempt. A record OSDU has
already lost is reported as already gone, not as a failure, and a record with no OSDU id at all is skipped.

The `record` and `everything` scopes mark the record deleted and blocked here; `history` leaves it delivered,
because OSDU still holds it at the version the ledger knows. The task result carries the counts and up to 200
per-record outcomes (failures first); every record's outcome is in its own attempt regardless.

## The GUI

- **Delivery** (Operate): every delivery flow with delivered versus total, pending, held, failed, drifted, and
  its last submission.
- **A flow's page** (Pipelines): the Delivery tab (stats, submit a drop, probe the target, release blocked),
  the Records tab (search and filters, every row opens the record), the Submissions tab. Rows tick: a selection
  bar offers "select all N matching" and Remove from OSDU, so a removal can be aimed at exactly the ticked rows
  or at the whole filtered set. A run page links here filtered to the records that run touched.
- **A record's page**: custody state, hashes, versions, the pending document, the render context; the
  history of attempts and interventions; Verify, Redeliver, Read back, Release and Remove from OSDU.
- **The removal dialog**: one surface for both. It names the target first (endpoint as declared, data partition,
  protocol, auth) because that is which OSDU the records are about to leave, then the three scopes side by side
  with what each destroys, whether it can be undone, what the ledger will do, and the exact call it makes. The
  two permanent scopes ask the operator to type the data partition back before the button enables.
- **A submission's page**: counts, the runs that carried it, its work batches, its attempts, a link to its
  records.
- **A retrieval flow's page** (Pipelines): the Retrievals tab, every run with its window, location, counts and
  outcome; a row opens the platform run.
- **Audit trail** (Operate): every run and intervention across flows, by actor, with parameters and log.
- **Mappings** (Workspace): the mapping documents and snapshots the repositories hold.
- **OSDU cache** (Workspace): the reference and master data the mappings resolve against. What each retrieval flow
  declares it caches and at which paths, how many records the current snapshot holds, and a search over the cached
  values by id, code, name or alias. Read-only: the definition lives in the flow document in git.
- **Runs**: a delivery run is a platform run; its trace streams live and its parameters, record counts and
  result show on the run page; a fan-out member shows its root and slot. Re-run repeats the same parameters.
  The trigger dialog offers the operations the flow's kind runs.

## The CLI

| Verb | Purpose |
| --- | --- |
| `sqlflow validate <flow.yaml>` | The platform's document validation (the CI gate for a folder). |
| `sqlflow check <flow.yaml> [--drop <location>] [--set name=value]... [--json]` | The delivery preflight: mapping against the schema snapshot, the reference snapshot, the drop's manifest when present. |
| `sqlflow run <flow.yaml> [--operation deliver\|verify\|plan\|known-state\|intake\|drain\|retrieve] [--force] [--set name=value]... [--drop <location>] [--submission <id>] [--record <key>]... [--publish-to <location>] [--db <ref>]` | A run on the workstation. With a catalog connection the ledger is live; without one the engine plans and checks only. A retrieval flow runs `retrieve` by default. |
| `sqlflow snapshot <flow.yaml> schema --kind <kind> [--from-dir <dir> \| --endpoint <url>]` | Capture a schema snapshot. |
| `sqlflow snapshot <flow.yaml> references [--from-dir <dir> \| --spec <spec.json> [--endpoint <url>]] [--no-current]` | Capture a reference snapshot and move the pin. A capture from OSDU refreshes the types its spec declares and merges them onto the current snapshot, so the minted version holds the whole cache. |
| `sqlflow snapshot <flow.yaml> list` | What the flow's snapshot store holds. |
| `sqlflow trigger --repo <r> --flow <f> [the same run options]` | Queue a run on the fleet. |

See [../reference/cli/delivery.md](../reference/cli/delivery.md).

## Runbook

| Symptom | Where to look | Action |
| --- | --- | --- |
| Submission `failed` with a validation message | The submission page; the run's trace | Fix the drop or the documents; submit the drop again (the same id is fine). |
| Records `held` | The Records tab filtered to held | Read the last error. Fix the data (reference miss, empty key) or the mapping; then Release (one record, or all blocked). |
| Records `failed` | The record's History tab | The retry budget is spent; the last error is redacted but specific. Release after fixing the cause. |
| Records stuck `delivering` | `Lease` on the record page in the past | The next claim reclaims them; nothing to do unless a node is wedged. |
| A verify run reports drift | The Records tab with Drifted only | Decide whether the edit in OSDU was legitimate. Redeliver the record, or set `verify.reconcile: true` so verify runs queue redelivery. |
| Everything re-renders after a change | The render context on the record | Only `render.*` and the mapping enter the hash. Check that the mapping version or a snapshot moved. |
| Is OSDU reachable with the flow's credentials? | Probe target on the flow's Delivery tab | The probe runs on a node and reports the status of the service's info endpoint. |
| A submission stays `running` with batches `queued` | The submission's batches; the run page's fan-out family | A drain member failed or a node went away. The parent settles what it can; re-run the submission (or trigger `drain` with the submission) to drain the rest. |
| Records pending with `workflow run ... failed` | The record's attempts: the `workflow` step names the run | The ingestion DAG failed; its own log says why. The next try triggers a new run automatically; fix the data or the manifest section first when the DAG rejected the content. |
| A retrieval run `failed` | The Retrievals tab: the row's error; the run's trace | The watermark did not move, so the next run covers the same window. Fix the cause (credentials, the query, the lake location) and run again; a run's directory is never reused. |

## Size ceilings

Set `MaxRequestBodySize` on petrodb-api, the ingress limit and the APIM limit to one deliberate number, and
derive the chunk cell limit in the preparing job from it ([design.md](design.md) section 14.3). A 413 holds
the record here rather than creating a duplicate, but the ceiling still needs to be intentional. The control
plane's own ceiling is `ControlPlane:MaxRequestBodyMegabytes`.
