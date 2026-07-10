---
id: flow-schedule
title: schedule section and the scheduler
type: flow-reference
summary: Declare when a flow runs with a top-level schedule block (cron or intervalSeconds); the control plane's scheduler turns it into queued runs.
keywords:
  - schedule
  - cron
  - intervalseconds
  - timezone
  - enabled
  - scheduler service
  - paused
  - schedules.yaml
  - shared schedule
  - schedule reference
  - schedule macro
yamlPath: schedule
related:
  - concept-control-plane
  - cli-db
  - cli-worker
  - flow-overview
sourceRefs:
  - src/SqlFlow.Yaml/YamlDocumentLoader.cs
  - src/SqlFlow.Yaml/YamlScheduleLibraryLoader.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Core/ScheduleSpec.cs
  - src/SqlFlow.Catalog/ScheduleClock.cs
  - src/SqlFlow.Catalog/ScheduleStore.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Catalog/RunQueueStore.cs
  - src/SqlFlow.ControlPlane/Background/SchedulerService.cs
  - src/SqlFlow.ControlPlane/Api/ScheduleEndpoints.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
---

# schedule

The top-level `schedule:` block declares WHEN a flow should run: either a cron expression evaluated in a time zone, or a fixed interval in seconds. It is available on every flow document kind (file flows and `flowType: ing`, `exp`, `sp`, `inv`, `hc`, `scm`, `batch`) because it is captured on the document envelope (`FlowDocument.Schedule` in src/SqlFlow.Yaml/YamlDocumentLoader.cs), not on any one flow model. The block is purely declarative: the engine itself never schedules anything. `sqlflow db sync` mirrors the block into the catalog schedule table, and the control plane's scheduler service fires it by enqueuing a run onto the same durable queue a manual trigger uses.

```yaml
name: orders
schedule:
  cron: "0 6 * * *"
  timezone: "Europe/Oslo"
source:
  type: csv
  location: ./orders.csv
target:
  connection: ${env:SQLFLOW_CONN_DWH}
  schema: dbo
  table: Orders
```

## Keys

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `cron` | string | one of `cron` / `intervalSeconds` | none | Cron expression: 5 fields (minute granularity) or 6 fields with a leading seconds field. Evaluated in `timezone`. |
| `intervalSeconds` | int | one of `cron` / `intervalSeconds` | none | Fixed number of seconds between fires. Must be positive. |
| `timezone` | string | no | `"UTC"` | IANA time zone id the cron expression is evaluated in, for example `Europe/Oslo`. Ignored for interval schedules. |
| `enabled` | bool | no | `true` | Whether the schedule is active. A disabled schedule is recorded in the catalog but never fires. |
| `catchup` | bool | no | `false` | Whether missed occurrences (the host was down past a fire) are backfilled. `false` skips the missed fire and resumes at the next occurrence after now; `true` fires one missed occurrence per scheduler tick until the schedule is current again. |

| `name` | string | no | none | Publishes this inline schedule under a name so other flows can reuse it with `schedule: <name>`. Metadata only: the cadence still applies to this flow. See [Reusable schedules](#reusable-schedules-define-once-reference-by-name). |

Exactly one of `cron` or `intervalSeconds` must be set for the schedule to be armed. A block that sets neither is treated as absent: the loader parses it to no schedule at all (src/SqlFlow.Yaml/YamlDocumentLoader.cs, `MapSchedule`), so an empty block is never stored as a broken schedule.

The `schedule:` value may also be written as a bare scalar (`schedule: nightly`) to reuse a schedule defined elsewhere by name, instead of an inline block. See [Reusable schedules](#reusable-schedules-define-once-reference-by-name).

### cron

A standard cron expression parsed by Cronos. The field count decides the format (src/SqlFlow.Catalog/ScheduleClock.cs): five fields is standard minute granularity; six or more fields is parsed with a leading seconds field. Whitespace around the value is trimmed.

Cron syntax is NOT validated at YAML parse time. The YAML layer has no scheduling-library dependency; validation happens where the schedule is armed:

- During `sqlflow db sync`, an invalid cron or time zone causes the schedule to be skipped with the warning `'<flow>' (<file>) has an invalid schedule: <reason>` (src/SqlFlow.Catalog/CatalogSync.cs). The pipeline itself still syncs, and because only valid schedules are kept, a schedule row synced by an earlier run is removed by the same sync.
- On the schedule API, `ScheduleClock.TryValidate` rejects the request with HTTP 400 before storage.

Cron evaluation is time-zone aware with correct daylight-saving handling. By default the next fire is computed strictly after now, so a missed occurrence is skipped; set `catchup: true` to backfill missed occurrences instead (one per scheduler tick until current).

### intervalSeconds

A fixed interval: the next fire is simply now plus N seconds. The value must be positive. The validator counts a non-positive interval as unset (src/SqlFlow.Catalog/ScheduleClock.cs, `TryValidate`), so `intervalSeconds: 0` on its own fails with `a schedule must set exactly one of 'cron' or 'intervalSeconds'.`; a non-positive interval alongside a `cron` fails with `'intervalSeconds' must be a positive number of seconds.` `timezone` has no effect on interval schedules.

Setting both `cron` and `intervalSeconds`, or (via the API) neither, fails validation with `a schedule must set exactly one of 'cron' or 'intervalSeconds'.`

### timezone

An IANA time zone id such as `Europe/Oslo`, resolved through `TimeZoneInfo.FindSystemTimeZoneById`. Blank or `UTC` (case-insensitive) resolves to UTC; the YAML loader normalizes a missing or blank value to `"UTC"`. An unknown time zone makes a cron schedule invalid: the catalog sync skips it with a warning, and the API rejects it with 400. On an interval schedule the value is ignored and not validated (`ScheduleClock.TryValidate` only resolves the time zone when a cron is present).

### enabled

Defaults to `true`. `enabled: false` stores the schedule but the scheduler never fires it. This is the declarative flag from the YAML (or the API create body); it is distinct from the operational `paused` flag described below.

## Reusable schedules: define once, reference by name

Repeating the same `schedule:` block across every flow of a source is a maintenance trap: change the cadence and you must edit every file. Instead a schedule can be defined once and referenced by name from any number of flows in the same repo. A flow references a shared schedule by writing the `schedule:` value as a bare scalar, the schedule's name:

```yaml
name: orders
schedule: nightly        # reuse the shared schedule named "nightly"
source:
  type: csv
  location: ./orders.csv
target:
  connection: ${env:SQLFLOW_CONN_DWH}
  schema: dbo
  table: Orders
```

A named schedule is defined in one of two ways, and both share a single per-repo namespace:

1. **A dedicated `schedules.yaml` library file.** Any file named `schedules.yaml` or ending in `.schedules.yaml`, anywhere in the repo, whose top-level `schedules:` key maps a name to a cadence. This is the natural home for a source's schedules (src/SqlFlow.Yaml/YamlScheduleLibraryLoader.cs):

   ```yaml
   # schedules.yaml
   schedules:
     nightly:   { cron: "0 6 * * *", timezone: "Europe/Oslo" }
     every-15m: { intervalSeconds: 900 }
     weekly:    { cron: "0 5 * * 1", timezone: "UTC", catchup: true }
   ```

   Each entry's fields are exactly a flow's inline `schedule:` block (`cron` or `intervalSeconds`, `timezone`, `enabled`, `catchup`); the map key is the reference name. A library file is not a flow document (the flow scan globs `*.flow.yaml`) and never becomes a pipeline. An entry that declares neither a cron nor an interval is dropped with a warning.

2. **A named inline block on a flow.** An inline `schedule:` block may carry a `name:` key to publish itself for reuse. That flow still runs on its own inline schedule, and any other flow in the repo can reference it by that name:

   ```yaml
   # invoices.flow.yaml — defines "nightly" inline and uses it
   name: invoices
   schedule:
     name: nightly
     cron: "0 6 * * *"
     timezone: "Europe/Oslo"
   # ... orders.flow.yaml elsewhere just writes:  schedule: nightly
   ```

References are resolved during the estate scan, where the whole repo is visible (src/SqlFlow.Lineage/Collection/FlowSetCollector.cs), after which each referencing flow carries the resolved cadence exactly as if it had been written inline. Resolution rules:

- The two definition sources share one case-insensitive namespace. If a name is defined more than once (across library files and named inline blocks), the first definition wins and the redefinition is warned.
- A reference to a name that nothing defines leaves that flow **unscheduled**, with the warning `'<flow>' (<file>) references schedule '<name>', which no schedules.yaml or named inline block defines; the flow is left unscheduled.` A dangling reference never produces a wrong or broken schedule.
- Cron/interval syntax of a resolved schedule is validated exactly as an inline one (see below); the resolved cadence flows through the same catalog sync.

A `schedule: <name>` reference is only resolvable by the full estate scan (`sqlflow db sync`), which sees the whole repo. The targeted per-run write-back that keeps a run-only catalog current parses a single flow file and cannot see the library, so it leaves a referenced schedule for the next full sync to reconcile rather than mirror an unresolved reference (src/SqlFlow.Catalog/CatalogSync.cs, `MirrorSingleFlowScheduleAsync`).

## From YAML to the catalog: sync semantics

`sqlflow db sync <dir>` mirrors git-declared schedules into the catalog schedule table with `Source = 'yaml'` (src/SqlFlow.Catalog/CatalogSync.cs):

- The schedule is refreshed from the YAML on every sync; git is the source of truth for `yaml` schedules.
- An operator's API pause survives a re-sync: `paused` is an operational override that a git refresh does not clear.
- Schedules created through the API (`Source = 'api'`) are never touched by sync.
- A schedule removed from git is removed from the table on the next sync.
- For duplicate flow names, the first wins (the duplicate was already warned about by the estate scan).
- The next fire is computed from the sync time (falling back to the sync time itself when none is computable), but it only replaces the stored `NextFireUtc` for a new schedule, one whose cron/intervalSeconds/timezone changed, or one with no next fire; an unchanged re-sync never disturbs the firing cadence (src/SqlFlow.Catalog/ScheduleStore.cs, `StageYamlUpsertAsync`).

## The scheduler service

`SchedulerService` (src/SqlFlow.ControlPlane/Background/SchedulerService.cs) is a control-plane background service that scans the catalog every `ControlPlane:Scheduler:PollSeconds` seconds (default 15, minimum 1; src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs) and processes at most 200 due schedules per tick.

A schedule fires when it is `Enabled`, not `Paused`, and its `NextFireUtc` has arrived. Firing:

1. Computes the next occurrence: strictly after now by default (a missed occurrence is skipped), or, when `catchup` is set, strictly after the occurrence just fired (so an overdue schedule advances one occurrence per tick, firing each missed window until it is current).
2. Claims the occurrence with a compare-and-swap on `NextFireUtc`, so multiple control-plane nodes never double-fire the same occurrence. The winning claim also stamps `LastFireUtc`.
3. Skips the occurrence if the pipeline is inactive or removed (logged: `Schedule {ScheduleId}: flow '{FlowName}' is inactive or removed; not enqueued this occurrence.`); the next fire has already advanced, so the schedule simply tries again later.
4. Enqueues a run onto the same durable queue a manual trigger uses, then records the enqueued run id as `LastRunId`.

A malformed cron, an unknown time zone, or a cron with no future occurrence has no computable next fire: the claim advances `NextFireUtc` to null and the schedule is parked (logged: `... has no computable next fire (invalid cron/timezone or exhausted) and was parked.`). A tick error such as a transient database outage is logged and retried on the next tick.

Scheduled runs are enqueued untargeted (any node may claim them) with no explicit commit pin on the request; enqueueing itself pins the run to the repo's last successfully synced commit when one is known (src/SqlFlow.Catalog/RunQueueStore.cs).

## The schedule API

The control plane also manages schedules directly (src/SqlFlow.ControlPlane/Api/ScheduleEndpoints.cs). All routes are mounted under `/api/v1`. Reads require the `read` scope; mutations require `operate`.

| Endpoint | Description |
| --- | --- |
| `GET /schedules` | List, filterable by `repoId`, `pipelineId`, `source` (`yaml` or `api`), `enabled`; paged with `page` and `pageSize`. |
| `GET /schedules/{id}` | One schedule; 404 when unknown. |
| `POST /schedules` | Create an ad-hoc `api` schedule from `{repoId, flowName, cron OR intervalSeconds, timezone?, enabled?, catchup?}`. 400 on a blank `flowName` or invalid timing; 404 when no active pipeline matches. |
| `POST /schedules/{id}/run` | Fire the schedule now, on demand (to test it): enqueues a run of its flow through the same durable path a scheduled fire uses and stamps `lastRunId`, without moving the next scheduled fire. 202 with `{runId}`; 404 when unknown; 409 when the flow is inactive or removed. |
| `POST /schedules/{id}/pause` | Set the operational `paused` flag. |
| `POST /schedules/{id}/resume` | Clear `paused` and recompute the next fire from now, so a long pause never releases a burst of missed fires. |
| `DELETE /schedules/{id}` | Remove the schedule; 204 on success, 404 when unknown. |

`ScheduleDto` fields: `id`, `repoId`, `pipelineId`, `flowName`, `cron`, `intervalSeconds`, `timezone`, `enabled`, `catchup`, `paused`, `source`, `nextFireUtc`, `lastFireUtc`, `lastRunId`, `createdUtc`, `updatedUtc`.

## Examples

An interval schedule, declared but not yet active:

```yaml
name: orders
schedule:
  intervalSeconds: 900
  enabled: false
source:
  type: csv
  location: ./orders.csv
target:
  connection: ${env:SQLFLOW_CONN_DWH}
  schema: dbo
  table: Orders
```

A second-granularity cron on an ingestion flow (6 fields, leading seconds field), fired every 30 seconds during business hours in Oslo:

```yaml
flowType: ing
name: staging-orders
schedule:
  cron: "*/30 * 8-17 * * MON-FRI"
  timezone: "Europe/Oslo"
connections:
  erp: ${env:SQLFLOW_SRC}
  dwh: ${env:SQLFLOW_DW}
source:
  server: erp
  object: AdventureWorks.Sales.Orders
target:
  server: dwh
  object: DW.raw.Orders
load:
  keyColumns: [OrderID]
```

Arm the schedules by syncing the repo into the catalog:

```bash
sqlflow db sync . --repo my-estate
```

## See also

- [Flow document overview](./overview.md)
- [The control plane](../concepts/control-plane.md)
- [worker command](../cli/worker.md)
