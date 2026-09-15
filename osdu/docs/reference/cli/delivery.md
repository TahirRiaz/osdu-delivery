# sqlflow check, cache, template, and the delivery run options

## check

```bash
sqlflow check <flow.yaml> [--drop <location>] [--set name=value]... [--db <ref>] [--json]
```

Everything checkable without OSDU: the flow and the pinned mapping parse, the template the mapping pins loads from the
catalog, the version of the cache of the partition the flow delivers to (its `target.headers.data-partition-id`)
loads from the catalog (its current version, or the one `render.cacheVersion` pins), the render context is built, and the mapping is checked against the template and
the cache (the preflight gate, [mapping-templates.md](../../delivery/mapping-templates.md#checks)). A mapping that reads
nothing from a cache is checked without one, and the output says so (`cache  none (the mapping reads nothing from a
cache)`); otherwise it names what was read (`cache  partition <partition> version <version> (<n> type(s))`). When the drop is present at the flow's declared location (or named with `--drop`), the manifest is parsed and
every column and child dataset the mapping reads is checked against what it declares. Exit 0 on success, 1 on a
validation failure, which names the file.

Templates and caches live in the catalog, so `check` needs the catalog connection: `--db <ref>`, or
`SQLFLOW_CATALOG_DB`. Without one it fails saying so; the check that needs no catalog is `sqlflow validate`
([validate.md](validate.md)).

`--set` supplies the flow's own parameters (`logSource=STAT_COMP`), which the drop location is rendered from.
`--json` prints the resolved facts (flow id, mapping reference, the template's kind and version, render context,
layout, the cache read with its `partition`, `version` and `types` count (null when the mapping reads no cache), and the manifest
and warnings when the drop was checked).

## cache

```bash
sqlflow cache list <partition | cache.yaml> [--db <ref>] [--json]
sqlflow cache import <cache.yaml> --from-dir <dir> [--db <ref>] [--json]
```

The versions of a partition's cache, which live in the catalog and nowhere else
([documents.md](../../delivery/documents.md#the-partition-cache)). A catalog keeps one cache per OSDU data partition,
filled by every cache flow (`flowType: cache`) whose `source.headers.data-partition-id` names the partition. Both forms need the catalog connection (`--db <ref>`, or `SQLFLOW_CATALOG_DB`); without one they fail with
`Caches live in the catalog. Run 'sqlflow cache' with --db <conn-ref>, or set the catalog variable.`

| Verb | What it does |
| --- | --- |
| `list` | Takes a partition (`opendes`), or a cache flow's file, which lists the partition the flow fills, and prints every version, newest first: its label, `current` against the current one, how many records in how many types, the cache flow that wrote it, when it was captured, for whom, and in which run. A value that is neither a partition id (letters, digits, underscore, hyphen and dot) nor a `${env:...}` or `${keyvault:...}` reference is refused. A partition whose cache has no version yet says to run a cache flow of the partition with the refresh operation. With `--json`, each version also carries its `partition`, its `flow`, its sequence, the version before it, where its content came from and the record count per type. |
| `import` | Merges the type files in `--from-dir` into the cache of the flow's partition as that cache flow's capture, for work without an OSDU platform (the sample estate keeps such files under `samples/recall-welllog/references`). A file is `{Name}.json`: the type's entity type and its records, each an `id` and the captured values. The files have to be exactly what the cache flow declares: a file for every declared type and none for a type it does not declare, each under the declared entity type, and no value under a name the type does not capture. Anything else is refused, naming every mismatch, and nothing is written. The merge is the one a refresh makes ([documents.md](../../delivery/documents.md#the-partition-cache)): a record in the files replaces what the cache held for it, a record the flow's last capture held that the files leave out goes only when no other cache flow's capture still holds it, and types the files do not cover are left as they are. When the merge changes the cached content, a version is written, recorded as written by the cache flow and captured by `cli:<user>` with no run, and it becomes current. |

Files that add nothing the current version does not already hold write nothing, as a refresh that finds nothing new
writes nothing: `cache of partition <partition>: the files add nothing version <version> does not already hold, so
nothing was written`. With `--json` an import reports the `partition`, the `flow`, the `version`, whether it was
`written`, and the `types` and `records` counts.

A cache is captured from OSDU by running a cache flow of its partition, the same run its schedule fires: `sqlflow run <cache.yaml>`
(the `refresh` operation, a cache flow's default), or `--operation plan` to count what each type's search matches
without writing anything. Nothing about a cache is written to the repository.

## template

```bash
sqlflow template capture --kind <kind> [--release <tag>] [--db <ref>] [--json]
sqlflow template import <schema.json> --kind <kind> [--release <tag>] [--db <ref>] [--json]
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
| `capture` | Reads the kind's schema from the OSDU data definitions, the Open Group's public repository (<https://community.opengroup.org/osdu/data/data-definitions>): its file under `Generated` at the commit the release tag names, with every file it refers to, bundled into one document and saved. A release is downloaded once, as its `Generated` folder in one archive, into the local copy under the temp folder (`sqlflow/osdu-data-definitions`), the same copy a control plane on the machine uses, and read from disk after that. The newest release unless `--release <tag>` names one; the origin records the release, commit and file. |
| `import <schema.json>` | Saves a schema file of one's own. A bundled file, one whose every `$ref` points into its own `definitions` (the form `capture` saves, and the one the sample estate keeps under `samples/recall-welllog/templates`), is saved as it is. A file as the data definitions publish it, such as a release's schema downloaded and given a kind of its own, refers to the shared schemas beside it (`../abstract/...`); those are read from `--release` (the newest by default) and bundled in exactly as `capture` bundles them, and the template's origin names the release and its commit. |
| `import --from-dir <dir>` | Bundles the kind's schema from a local checkout of the OSDU data definitions (its `Generated` folder) and saves it, exactly as `capture` bundles it from the repository. |
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
| `--operation deliver\|verify\|plan\|known-state\|intake\|drain\|retrieve\|refresh` | What the run does. Default deliver; a retrieval flow runs retrieve by default and accepts plan; a cache flow runs refresh by default (deliver is taken as refresh) and accepts plan. `intake` plans a drop into work batches without delivering, `drain` delivers the pending batches of a submission (`--submission`) or of the whole flow without reading the drop. A refresh captures every type the cache flow declares, so it takes no `--drop`, `--submission` or `--record`. |
| `--force` | Push past the change gates: plan every record even when no source table advanced, re-plan a completed submission, verify recently verified records. |
| `--set name=value` | A flow parameter value; repeatable. |
| `--drop <location>` | Read this drop instead of the flow's declared source location. |
| `--submission <id>` | Re-run one submission from its own drop, with the parameters it was received with. |
| `--record <key>` | Scope the run to this delivery key; repeatable. With deliver, the records are redelivered regardless of what OSDU holds, and the run re-plans the drop even when no source table advanced or the submission already completed, so the redelivery is never skipped; with verify, only they are checked. |
| `--redeliver all\|metadata\|payload` | With `--record` on a deliver run: what of those records is sent again. Default all. |
| `--publish-to <location>` | Where a known-state publication is written; without it the flow's `source.knownState` (with the flow parameters substituted) is used. |
| `--db <ref>` | The catalog connection (default `${env:SQLFLOW_CATALOG_DB}`). With it the ledger is live and the run is recorded. A delivery flow needs it for every operation: `deliver`, `plan` and `intake` render against the template and the cache version saved there, and `verify`, `known-state` and `drain` work on the ledger. A cache flow's `refresh` needs it, because the versions it writes live there. A retrieval flow runs without it. |

The parameters are validated once, at the boundary, and recorded on the run so the history says what was
asked. The run's result carries the submission and the record counts (planned, delivered, held, failed,
unchanged), which the run page and the runs list show; a refresh's result carries the partition and the cache flow, the
version the partition's cache holds after it, the version it replaced, whether a version was written, when it was
captured, and per type what was captured and what its changes reach; a plan on a cache flow carries the partition, the
flow, the current version and what each type's search matches.

## Exit codes

`check`, `cache` and `template` exit 0 on success and 1 on a failure, which prints one `ERROR` line naming the
problem. `run` exits 0 when the run succeeded and 1 when it failed; Ctrl+C exits 130.
