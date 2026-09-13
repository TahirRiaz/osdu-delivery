# sqlflow check, snapshot, template, and the delivery run options

## check

```bash
sqlflow check <flow.yaml> [--drop <location>] [--set name=value]... [--db <ref>] [--json]
```

Everything checkable without OSDU: the flow and the pinned mapping parse, the template the mapping pins loads from the
catalog, the reference snapshot the flow renders with loads from the repository's snapshot store, the render context
is built, and the mapping is checked against the template and the cache (the preflight gate,
[mapping-templates.md](../../delivery/mapping-templates.md#checks)). When the drop is present at the flow's declared
location (or named with `--drop`), the manifest is parsed and every column and child dataset the mapping reads is
checked against what it declares. Exit 0 on success, 1 on a validation failure, which names the file.

Templates live in the catalog, so `check` needs the catalog connection: `--db <ref>`, or `SQLFLOW_CATALOG_DB`. Without
one it fails saying so; the check that needs no catalog is `sqlflow validate` ([validate.md](validate.md)).

`--set` supplies the flow's own parameters (`logSource=STAT_COMP`), which the drop location is rendered from.
`--json` prints the resolved facts (flow id, mapping reference, the template's kind and version, render context,
layout, and the manifest and warnings when the drop was checked).

## snapshot

```bash
sqlflow snapshot <flow.yaml> references [--from-dir <dir> | --spec <spec.json> [--endpoint <url>]] [--no-current]
sqlflow snapshot <flow.yaml> list [--db <ref>]
```

`references` captures a reference snapshot of the OSDU cache into the snapshot store the flow's repository layout
locates (`snapshots/` next to the flow, or `render.snapshots`). It mints an immutable version (`yyyyMMddTHHmmssZ`) and,
unless `--no-current`, moves the pointer `pinned` resolves to. `--from-dir` reads a folder of reference JSON; otherwise
`--spec` names the capture spec and the flow's target endpoint, auth and headers are used (`--endpoint` overrides the
endpoint). Commit the snapshot store with the flow.

`list` prints the store's reference snapshot versions, marking the current one, and the template the flow's mapping
pins: saved (with when), not saved, or not checked when no catalog connection was given. Templates are not in the
snapshot store; `sqlflow template` saves them in the catalog.

## template

```bash
sqlflow template capture <flow.yaml> --kind <kind> [--endpoint <url>] [--db <ref>] [--json]
sqlflow template import <schema.json> --kind <kind> [--db <ref>] [--json]
sqlflow template import --from-dir <dir> --kind <kind> [--db <ref>] [--json]
sqlflow template list [--db <ref>] [--json]
sqlflow template show --kind <kind> [--version <version>] [--db <ref>] [--json]
sqlflow template delete --kind <kind> --version <version> [--db <ref>]
```

The templates in the catalog ([mapping-templates.md](../../delivery/mapping-templates.md#templates)): OSDU schemas,
one kind each, every saved version immutable and identified by the kind and its content version (16 hexadecimal
characters), which a mapping pins under `template`. Every form needs the catalog connection (`--db <ref>`, or
`SQLFLOW_CATALOG_DB`).

| Verb | What it does |
| --- | --- |
| `capture` | Fetches the kind's schema from OSDU's schema service (`GET /api/schema-service/v1/schema/{id}`) and every schema it refers to, bundles them into one document, and saves it. The connection is the flow's: a delivery flow's target or a retrieval flow's source, its auth and headers resolved from this machine's environment (`--endpoint` overrides the endpoint). |
| `import <schema.json>` | Saves a bundled schema file, one whose every `$ref` points into its own `definitions`: the form `capture` produces, and the one the sample estate keeps under `samples/recall-welllog/templates`. |
| `import --from-dir <dir>` | Bundles the kind's schema from a local checkout of the OSDU data definitions and saves it. |
| `list` | Every saved version: kind, version, when and by whom it was saved, and where it came from. |
| `show` | One version laid out as a template: every variable with its shape, whether the schema requires it, who writes it (a mapping, OSDU Delivery or OSDU), the entity types it points to and its unit context. Without `--version`, the most recently saved version of the kind. |
| `delete` | Deletes a version. Refused while a synced mapping pins it, naming the mappings. |

A version is its content: saving a schema that is already saved changes nothing and says so
(`template <kind> version <version> was already saved`), and a schema that differs from every saved version of its
kind is saved beside them as a new version. With `--json` the saved template is reported with `outcome` `created` or
`unchanged`. A schema that describes another kind, refers to anything outside itself, or declares no `data` property is
refused.

## The delivery run options

`sqlflow run` (a run on this machine) and `sqlflow trigger` (a run queued on the fleet) take the same options:

| Option | Meaning |
| --- | --- |
| `--operation deliver\|verify\|plan\|known-state\|intake\|drain\|retrieve` | What the run does. Default deliver; a retrieval flow runs retrieve by default and accepts plan. `intake` plans a drop into work batches without delivering, `drain` delivers the pending batches of a submission (`--submission`) or of the whole flow without reading the drop. |
| `--force` | Push past the change gates: plan every record even when no source table advanced, re-plan a completed submission, verify recently verified records. |
| `--set name=value` | A flow parameter value; repeatable. |
| `--drop <location>` | Read this drop instead of the flow's declared source location. |
| `--submission <id>` | Re-run one submission from its own drop, with the parameters it was received with. |
| `--record <key>` | Scope the run to this delivery key; repeatable. With deliver, the records are redelivered regardless of what OSDU holds, and the run re-plans the drop even when no source table advanced or the submission already completed, so the redelivery is never skipped; with verify, only they are checked. |
| `--redeliver all\|metadata\|payload` | With `--record` on a deliver run: what of those records is sent again. Default all. |
| `--publish-to <location>` | Where a known-state publication is written; without it the flow's `source.knownState` (with the flow parameters substituted) is used. |
| `--db <ref>` | The catalog connection (default `${env:SQLFLOW_CATALOG_DB}`). With it the ledger is live and the run is recorded. A delivery flow needs it for every operation: `deliver`, `plan` and `intake` render against the template saved there, and `verify`, `known-state` and `drain` work on the ledger. A retrieval flow runs without it. |

The parameters are validated once, at the boundary, and recorded on the run so the history says what was
asked. The run's result carries the submission and the record counts (planned, delivered, held, failed,
unchanged), which the run page and the runs list show.

## Exit codes

`check`, `snapshot` and `template` exit 0 on success and 1 on a failure, which prints one `ERROR` line naming the
problem. `run` exits 0 when the run succeeded and 1 when it failed; Ctrl+C exits 130.
