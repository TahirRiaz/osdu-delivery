---
id: cli-delivery
title: sqlflow check, snapshot, and the delivery run options
type: cli-command
summary: The delivery kind's CLI surface. check runs the preflight gate offline, snapshot captures schema and reference snapshots into the flow's repository, and run and trigger take the delivery operation and its scope.
keywords:
  - delivery
  - check
  - snapshot
  - operation
  - plan
  - verify
  - known-state
  - redeliver
cliCommand: check
related:
  - cli-validate
  - cli-run
  - cli-control-plane
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/DeliveryVerbs.cs
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Core/Runs/RunParameters.cs
  - src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
---

# sqlflow check, snapshot, and the delivery run options

## check

```bash
sqlflow check <flow.yaml> [--drop <location>] [--set name=value]... [--json]
```

Everything checkable without OSDU: the flow and the pinned mapping parse, the schema snapshot and the
reference snapshot the flow renders with load from the repository's snapshot store, the render context is
built, and the mapping is checked against the schema (the preflight gate). When the drop is present at the
flow's declared location (or named with `--drop`), the manifest is parsed and the source bindings are checked
against the columns it declares. Exit 0 on success, 2 on a validation failure that names the file.

`--set` supplies the flow's own parameters (`logSource=STAT_COMP`), which the drop location is rendered from.
`--json` prints the resolved facts (flow id, mapping reference and kind, render context, layout, manifest).

## snapshot

```bash
sqlflow snapshot <flow.yaml> schema --kind <authority:source:entityType:version> [--from-dir <dir> | --endpoint <url>]
sqlflow snapshot <flow.yaml> references [--from-dir <dir> | --spec <spec.json> [--endpoint <url>]] [--no-current]
sqlflow snapshot <flow.yaml> list
```

Captures into the snapshot store the flow's repository layout locates (`snapshots/` next to the flow, or
`render.snapshots`). A schema snapshot is bundled so every `$ref` is local, and content-addressed by hash. A
reference snapshot mints an immutable version (`yyyyMMddTHHmmssZ`) and, unless `--no-current`, moves the
pointer `pinned` resolves to. `--from-dir` reads a local checkout (a data-definitions clone, a folder of
reference JSON); otherwise the flow's target endpoint, auth and headers are used (`--endpoint` overrides the
endpoint). Commit the snapshot store with the flow.

## The delivery run options

`sqlflow run` (a run on this machine) and `sqlflow trigger` (a run queued on the fleet) take the same options:

| Option | Meaning |
| --- | --- |
| `--operation deliver\|verify\|plan\|known-state` | What the run does. Default deliver. |
| `--force` | Push past the change gates: plan every record even when no source table advanced, re-plan a completed submission, verify recently verified records. |
| `--set name=value` | A flow parameter value; repeatable. |
| `--drop <location>` | Read this drop instead of the flow's declared source location. |
| `--submission <id>` | Re-run one submission from its own drop, with the parameters it was received with. |
| `--record <key>` | Scope the run to this delivery key; repeatable. With deliver, the records are redelivered regardless of what OSDU holds; with verify, only they are checked. |
| `--publish-to <location>` | Where a known-state publication is written. |
| `--db <ref>` | The catalog connection (default `${env:SQLFLOW_CATALOG_DB}`). With it the ledger is live and the run is recorded; without it `run` plans and checks only. |

The parameters are validated once, at the boundary, and recorded on the run so the history says what was
asked. The run's result carries the submission and the record counts (planned, delivered, held, failed,
unchanged), which the run page and the runs list show.

## Exit codes

`check` exits 2 on a validation failure. `run` exits 0 when the run succeeded and 1 when it failed; Ctrl+C
exits 130.
