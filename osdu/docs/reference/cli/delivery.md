# sqlflow check, cache, template, and the OSDU run options

## check

```bash
sqlflow check <flow.yaml> [--interface <name>] [--connect] [--set name=value]... [--db <ref>] [--json]
```

Everything checkable without OSDU: the flow and the pinned mapping parse, the template the mapping pins loads from
the catalog, the version of the cache of the partition the flow delivers to (its `target.headers.data-partition-id`)
loads from the catalog (its current version, or the one `render.cacheVersion` pins), the render context is built,
and the mapping is checked against the template and the cache (the preflight gate,
[mapping-templates.md](../../mapping-templates.md#checks)). A mapping that reads nothing from a cache is checked
without one, and the output says so (`cache  none (the mapping reads nothing from a cache)`); otherwise it names
what was read (`cache  partition <partition> version <version> (<n> type(s))`). Exit 0 on success, 1 on a
validation failure, which names the file.

Templates and caches live in the catalog, so `check` needs the catalog connection: `--db <ref>`, or
`SQLFLOW_CATALOG_DB`. Without one it fails saying so; the check that needs no catalog is `sqlflow validate`
([validate.md](validate.md)).

`--set` supplies the flow's own parameters (`logSource=STAT_COMP`). `--json` prints the resolved facts (flow id,
mapping reference, the template's kind and version, render context, layout, and the cache read with its
`partition`, `version` and `types` count, null when the mapping reads no cache). A flow on the `ddms` route also gets a
`ddms` line, and `ddms` in its JSON, saying which collection of which DDMS its records go to
(`work-product-component--WellLog records go to the welllogs collection of the DDMS 'wellbore'
(/api/os-wellbore-ddms).`), or why no DDMS the flow reaches takes them; a DDMS the flow names by its registration is
read from the Register service when the flow runs, not by `check` ([documents.md](../../documents.md#the-ddmss-a-flow-delivers-to)).

A flow that declares interfaces ([documents.md](../../documents.md#a-source-with-interfaces)) is checked one
interface at a time, the same checks for each, plus whether its route can deliver the kind its mapping renders, and
then the order the interfaces run in, worked out as a run works it out ([documents.md](../../documents.md#order)). The
text output starts with a line giving that order (`recall: 2 of 2 interface(s) checked, in the order they run:
wellbores then welllogs`), and each interface's block adds its ledger, its route with the reason, its wave, a
`waits for` line for each interface it waits for with why (`wellbores: osdu.data.WellboreID refers to
master-data--Wellbore, which wellbores delivers (osdu:wks:master-data--Wellbore:1.3.0)`), and a `not waited` line for
each reference left out of a cycle. Interfaces that wait for each other in a way nothing cuts fail the check with an
`ERROR` naming the file, the interfaces and the properties. `--interface <name>` checks that interface alone. With
`--json` the answer is the flow's name, its `order`, and an `interfaces` array holding one object per interface, each
with its `interface`, `ledger`, `route` (`name` and `reason`), `after`, `wave`, `waitsFor` and `notWaitedFor` (each an
`interface`, its `origin`, `after` or `schema`, and `why`) beside the facts above.

## cache

```bash
sqlflow cache list <partition | cache.yaml> [--db <ref>] [--json]
sqlflow cache import <cache.yaml> --from-dir <dir> [--db <ref>] [--json]
```

The versions of a partition's cache, which live in the catalog and nowhere else
([documents.md](../../documents.md#the-partition-cache)). A catalog keeps one cache per OSDU data partition,
filled by every cache flow (`flowType: cache`) whose `source.headers.data-partition-id` names the partition. Both
forms need the catalog connection (`--db <ref>`, or `SQLFLOW_CATALOG_DB`); without one they fail with
`Caches live in the catalog. Run 'sqlflow cache' with --db <conn-ref>, or set the catalog variable.`

| Verb | What it does |
| --- | --- |
| `list` | Takes a partition (`opendes`), or a cache flow's file, which names the partition the flow fills, and prints every version, newest first: its label, `current` against the current one, how many records in how many types, the cache flow that wrote it, when it was captured, for whom, and in which run. A value that is neither a partition id (letters, digits, underscore, hyphen and dot) nor a `${env:...}` or `${keyvault:...}` reference is refused. A partition whose cache has no version yet says to run a cache flow of the partition with the refresh operation. With `--json`, each version also carries its `partition`, its `flow`, its sequence, the version before it, where its content came from and the record count per type. |
| `import` | Merges the type files in `--from-dir` into the cache of the flow's partition as that cache flow's capture, for work without an OSDU platform (the sample estate keeps such files under `osdu/samples/recall-welllog/references`). A file is `{Name}.json`: the type's entity type and its records, each an `id` and the captured values. The files have to be exactly what the cache flow declares: a file for every declared type and none for a type it does not declare, each under the declared entity type, and no value under a name the type does not capture. Anything else is refused, naming every mismatch, and nothing is written. The merge is the one a refresh makes: a record in the files replaces what the cache held for it, a record the flow's last capture held that the files leave out goes only when no other cache flow's capture still holds it, and types the files do not cover are left as they are. When the merge changes the cached content, a version is written, recorded as written by the cache flow and captured by `cli:<user>` with no run, and it becomes current. |

Files that add nothing the current version does not already hold write nothing, as a refresh that finds nothing new
writes nothing: `cache of partition <partition>: the files add nothing version <version> does not already hold, so
nothing was written`. With `--json` an import reports the `partition`, the `flow`, the `version`, whether it was
`written`, and the `types` and `records` counts.

A cache is captured from OSDU by running a cache flow of its partition, the same run its schedule fires:
`sqlflow run <cache.yaml>` (the `refresh` operation, a cache flow's default), or `--operation plan` to count what
each type's search matches without writing anything. Nothing about a cache is written to the repository.

## template

```bash
sqlflow template capture --kind <kind> [--release <tag>] [--db <ref>] [--json]
sqlflow template import <schema.json> --kind <kind> [--release <tag>] [--db <ref>] [--json]
sqlflow template import --from-dir <dir> --kind <kind> [--db <ref>] [--json]
sqlflow template list [--db <ref>] [--json]
sqlflow template show --kind <kind> [--version <version>] [--db <ref>] [--json]
sqlflow template delete --kind <kind> --version <version> [--db <ref>]
```

The templates in the catalog ([mapping-templates.md](../../mapping-templates.md#templates)): OSDU schemas, one
kind each, every saved version immutable and identified by the kind and its content version (16 hexadecimal
characters), which a mapping pins under `template`. Every form needs the catalog connection (`--db <ref>`, or
`SQLFLOW_CATALOG_DB`).

| Verb | What it does |
| --- | --- |
| `capture` | Reads the kind's schema from the OSDU data definitions, the Open Group's public repository (<https://community.opengroup.org/osdu/data/data-definitions>): its file under `Generated` at the commit the release tag names, with every file it refers to, bundled into one document and saved. A release is downloaded once, as its `Generated` folder in one archive, into the local copy under the temp folder, the same copy a control plane on the machine uses, and read from disk after that. The newest release unless `--release <tag>` names one; the origin records the release, commit and file. |
| `import <schema.json>` | Saves a schema file of one's own. A bundled file, one whose every `$ref` points into its own `definitions` (the form `capture` saves, and the one the sample estate keeps under `osdu/samples/recall-welllog/templates`), is saved as it is. A file as the data definitions publish it refers to the shared schemas beside it (`../abstract/...`); those are read from `--release` (the newest by default) and bundled in exactly as `capture` bundles them. |
| `import --from-dir <dir>` | Bundles the kind's schema from a local checkout of the OSDU data definitions (its `Generated` folder) and saves it, exactly as `capture` bundles it from the repository. |
| `list` | Every saved version: kind, version, when and by whom it was saved, and where it came from. |
| `show` | One version laid out as a template: every variable with its shape, whether the schema requires it, who writes it (a mapping, OSDU Delivery or OSDU), the entity types it points to and its unit context. Without `--version`, the most recently saved version of the kind. |
| `delete` | Deletes a version. Refused while a synced mapping pins it, naming the mappings. |

A version is its content: saving a schema that is already saved changes nothing and says so
(`template <kind> version <version> was already saved`), and a schema that differs from every saved version of its
kind is saved beside them as a new version. With `--json` the saved template is reported with `outcome` `created`
or `unchanged`. A schema that describes another kind, refers to anything outside itself, or declares no `data`
property is refused.

## The run options

An OSDU flow is run by `sqlflow run` (on this machine) or `sqlflow trigger` (queued on the fleet). Both take the
platform's three generic kind arguments, parsed and validated once for both, and recorded on the run so the
history says exactly what was asked:

| Option | Meaning |
| --- | --- |
| `--operation <name>` | Which of the kind's operations the run performs. Omitted takes the kind's default. A name is at most 32 characters, lower-case letters, digits and hyphens. |
| `--set name=value` | A flow parameter value; repeatable, at most 32 per run. A name is at most 64 characters and a value at most 1000, without control characters. |
| `--payload <json>` or `--payload @<file>` | One JSON object whose shape the flow kind owns, inline or read from a file. At most 64,000 characters. |
| `--db <ref>` | The catalog connection (default `${env:SQLFLOW_CATALOG_DB}`) for a local run. With it the ledger is live and the run is recorded. |

### The operations

| Flow kind | Operations | Default |
| --- | --- | --- |
| `delivery` | `deliver` (read the changed records, plan against the ledger, deliver what changed), `plan` (render and compare, report what would be delivered, change nothing), `intake` (plan into work batches without delivering), `drain` (deliver the pending batches without re-reading the source), `verify` (read delivered records back from OSDU and compare versions) | `deliver` |
| `retrieval` | `retrieve`, `plan` | `retrieve` |
| `cache` | `refresh` (capture every declared type and merge it into the partition's cache), `plan` (count what each type's search matches, write nothing) | `refresh` |

A delivery flow needs the catalog for every operation: `deliver`, `plan` and `intake` render against the template
and the cache version saved there, and `verify` and `drain` work on the ledger. A cache flow's `refresh` needs it
because the versions it writes live there. A retrieval flow runs without it.

### The payload

Everything that scopes or forces a run beyond its operation travels in the `--payload` JSON object, because the
platform's own flags are deliberately kind-agnostic: forcing past the change gates, re-running one submission,
scoping the run to particular record keys, and choosing what of a scoped record is sent again. The kind validates
the payload at every trust boundary, so a payload no engine path could honor is refused before anything is queued
rather than half-applied.

| Field | Meaning | Operations |
| --- | --- | --- |
| `force` | `true` lifts the whole-run gates (the tier 0 skip and an already completed submission); each record's own hashes still decide what is sent. | all but `drain` |
| `submissionId` | The submission the run works on: a re-run, or a fan-out member's share. | all but `verify` and `replan` |
| `recordKeys` | The delivery keys (UUIDs) the run is scoped to, at most 1,000, each once. Not with `submissionId`. | `deliver`, `plan`, `intake`, `verify` |
| `redeliver` | What a run scoped to `recordKeys` sends again: `all` (the default), `record` (the record document; its datasets and bulk data keep what OSDU holds), `files` (uploaded and registered again, on the file, dataset, manifest and composed routes, and the workflow route with files), `bulk` (a new version of the bulk data, on the ddms and composed routes) or `workflow` (the workflow route's stages run again). On the composed and workflow routes the named part goes alone. `metadata` names the record and `payload` every part. A part the route does not send fails the run. | `deliver` |
| `slices` | The key slices of `submissionId` a fan-out intake member plans (indexes 0 to 1023, each once). | `intake` |
| `interface` | The one interface of a source the run works on. A run on records or slices of a source with several interfaces has to name it. | all |
| `interfaces` | The interfaces a run of a source runs, each once; every interface when left out. Not with `interface`, `submissionId`, `recordKeys` or `slices`. | all |

```bash
# plan one log source, forcing past the change gates
sqlflow run flows/recall-welllog.yaml --operation plan --set logSource=STAT_COMP --payload '{"force":true}'

# drain the pending batches of one submission
sqlflow trigger --repo recall --flow recall-welllog --operation drain --payload @submission.json

# deliver two interfaces of a source, and nothing else of it
sqlflow run flows/recall.yaml --set logSource=STAT_COMP --payload '{"interfaces":["wellbores","welllogs"]}'

# send the curves of one well log again
sqlflow run flows/recall.yaml --set logSource=STAT_COMP --payload '{"interface":"welllogs","recordKeys":["<key>"],"redeliver":"bulk"}'
```

### The result

The run's result carries the submission and the record counts (planned, delivered, held, failed, unchanged),
which the run page and the runs list show. A refresh's result carries the partition and the cache flow, the
version the partition's cache holds after it, the version it replaced, whether a version was written, when it was
captured, and per type what was captured and what its changes reach; a plan on a cache flow carries the partition,
the flow, the current version and what each type's search matches.

## Exit codes

`check`, `cache` and `template` exit 0 on success and 1 on a failure, which prints one `ERROR` line naming the
problem. `run` exits 0 when the run succeeded and 1 when it failed; Ctrl+C exits 130.
