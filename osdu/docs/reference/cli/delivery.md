# sqlflow check, preview, values, fixtures, cache, template, assertions, and the OSDU run options

## check

```bash
sqlflow check <flow.yaml> [--interface <name>] [--partition <name>] [--connect] [--set name=value]... [--db <ref>] [--json]
```

Everything checkable without OSDU: the flow and the pinned mapping parse, the template the mapping pins loads from
the catalog, the version of the cache of the partition the flow delivers to (the one `--partition` names for a flow that
names its partitions, the only one when it names one; its `target.headers.data-partition-id` for a flow that names none)
loads from the catalog (its current version, or the one `render.cacheVersion` pins), the render context is built,
and the mapping is checked against the template and the cache (the preflight gate,
[mapping-templates.md](../../mapping-templates.md#checks)). A mapping that reads nothing from a cache is checked
without one, and the output says so (`cache  none (the mapping reads nothing from a cache)`); otherwise it names
what was read (`cache  partition <partition> version <version> (<n> type(s))`). A mapping that searches the platform
gets a `searches` line saying how its lookups are written and the partition's system properties that decide it
(`searches  exact, then regardless of case where one record answers (indexer featureFlag.keywordLower.enabled
enabled)`, or `exact only (...)` when the setting is off or unknown). Exit 0 on success, 1 on a validation failure,
which names the file.

Templates and caches live in the catalog, so `check` needs the catalog connection: `--db <ref>`, or
`SQLFLOW_CATALOG_DB`. Without one it fails saying so; the check that needs no catalog is `sqlflow validate`
([validate.md](validate.md)).

`--set` supplies the flow's own parameters (`logSource=STAT_COMP`). A flow that names its partitions is checked in one
partition, as a run of it is ([documents.md](../../documents.md#partitions)): `--partition` names it, a flow naming one
needs none, and a partition the flow does not name is refused. The mapping's fixtures render against the caches of the
partitions they are written for, whichever partition is checked. `--json` prints the resolved facts (flow id,
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

## preview

```text
sqlflow preview <flow.yaml> [--interface <name>] [--partition <name>] [--key <key>] [--set name=value]... [--out <file.json>] [--db <ref>] [--json]
```

Renders one record as a delivery would render it, and sends nothing: the same preview a delivery flow's Preview tab
shows ([operations.md](../../operations.md#previewing-a-record)). It opens the flow's ingestion tables on this machine,
through the flow's own connection reference, renders with the template and the cache version `check` reads, and asks
the platform's search only what the mapping's searches ask a run. Nothing is written: not OSDU, not the ledger, not the
work location. It needs `--db <ref>` or `SQLFLOW_CATALOG_DB`, as `check` does.

Without `--key` it previews the first record of the scope in key order, passing over rows that cannot render (marked
deleted, a key part empty, held by the source) and naming them. `--key` names a record the way an operator holds it: a
source key as the ledger shows it (`recall:NORWAY_WELLDB/12359/1`, the system's prefix optional), its parts as `a | b`
or as a JSON array (`'["NORWAY_WELLDB", "12359/1"]'`), a delivery key, or an OSDU id the ledger holds. A key that names
no row ends the command with 1 and says why. `--set` fills the flow's parameters, the declared defaults filling the rest.

The text answer names the record, how it was picked, the file and row it came from, the mapping, kind and cache
version, the route, what the next run would do with it and why (`next run    skip (fingerprint): source version,
payload hash and render context unchanged`), what the ledger holds for it and whether the document is the one it holds,
the document's id, size and hash, why a delivery would hold it, the records it refers to, the payload files (with each
parquet file's rows and columns from its footer), the route's requests in order, and then the document itself as the
route sends it, with a line naming each placeholder where the platform gives a value (a dataset id the File service
mints). `--json` prints the preview as JSON; `--out` writes it to a file, whatever the size of its document.

A source that declares interfaces is previewed one interface at a time, each with its own first record; `--interface`
previews one, and a key needs it, since a key names a record of one interface.

## values

```text
sqlflow values <flow.yaml> [--interface <name>] [--partition <name>] [--target <osdu.path>]... [--set name=value]...
               [--max-rows <n>] [--samples <n>] [--skip <n>] [--rows <file.csv>] [--out <file.json>] [--db <ref>] [--json]
```

Finds the rows that will not give the mapping's attributes the values the template expects: the same check as Check
values on the Mappings page ([operations.md](../../operations.md#checking-a-mappings-values)). It opens the flow's
ingestion tables on this machine, through the flow's own connection reference, renders every row it reads as a delivery
would (with the template and the cache version `check` reads, asking the platform's search only what the mapping's
searches ask a run), and holds every value written to what the template says of its attribute. Nothing is written: not
OSDU, not the ledger, not the work location. It needs `--db <ref>` or `SQLFLOW_CATALOG_DB`, as `check` does.

`--target` names an attribute as a template path (`osdu.data.WellboreID`, `osdu.data.Curves[].CurveUnit`, or a group
such as `osdu.data.VerticalMeasurement` for everything it holds); repeat it for more, and leave it out for every
attribute. `--max-rows` reads that many rows of the scope in the order a run reads them (10,000 by default, 0 for the
whole scope). `--samples` names that many example records for each reason (20 by default, at most 500), after the first
`--skip`. `--set` fills the flow's parameters, the declared defaults filling the rest.

The text answer says how many rows were checked and of how many, how many are held, write a value the template does not
accept, leave an attribute out, or are clean, and why rows were passed over; then each attribute some row fails, with
its rows by outcome, every reason with its count, the values behind it and the first example records. `--json` prints
the whole check as JSON, and `--out` writes it to a file.

`--rows` writes every failing row to a CSV file as the check meets it, however many there are: one line per row (or item
of a repeated array) an attribute is held, invalid or empty for, with the columns `flow`, `variable`, `at`, `outcome`,
`rule`, `reason`, `value`, `source_key`, `label`, `delivery_key`, `file`, `row` and `item`. It is the full list the
answer's example records are the first of.

A source that declares interfaces is checked one interface at a time; `--interface` checks one, and `--target` needs it,
since an attribute is one of an interface's mapping.

## fixtures

```text
sqlflow fixtures update <flow.yaml> [--interface <name>] [--partition <name>] [--dry-run] [--db <ref>] [--json]
```

Writes what each fixture of the flow's mapping renders into its `expected` block
([mapping-templates.md](../../mapping-templates.md#fixtures)). The mapping is resolved and checked exactly as `check`
does, against the template it pins and the cache version it reads, except that its fixtures are not compared: they are
rendered, each over its own rows, with its parameters over `fixtureDefaults.parameters` and the flow's, against the
search answers it declares and never the platform. Templates and caches live in the catalog, so it needs `--db <ref>` or
`SQLFLOW_CATALOG_DB`, as `check` does.

Only the lines of the `expected` blocks that change are written; the rest of the file, its comments and its layout stay
as they were. A written record reads the way the sample fixtures do: `id` and `kind`, then the properties in the order
the mapping's record tree writes them, an object or a list of plain values on one line when it fits. The file is written
through a temporary file beside it and read back as a mapping first, so an interrupted run leaves it as it was.

| Fixture | What happens |
| --- | --- |
| Renders what it expects (compared canonically, as the gate compares) | `unchanged`: its text is left exactly as written. |
| Renders a record a delivery would send, and expects another | `updated`: its `expected` block now holds the record. A value that was not a block (`expected: "{}"`) becomes one. |
| Renders a record that would be held, fails to render, or asks a search it declares no answer to | `skipped`, with the reason: a fixture expects a record a delivery would send. |
| Is written as a flow mapping (`- { name: ..., expected: ... }`), or its `expected` is anchored or followed by more on its line | `skipped`, naming what to write instead. |

`--dry-run` says what would change (`differs`) and writes nothing. A source with interfaces updates the mapping of each
interface, and a mapping file two interfaces share once; `--interface <name>` updates one. `--json` answers one object
per mapping file: `mapping`, `path`, `written`, and `fixtures`, each with its `name`, `outcome` and `reason`.

## cache

```bash
sqlflow cache list <partition | cache.yaml> [--partition <name>] [--db <ref>] [--json]
sqlflow cache import <cache.yaml> --from-dir <dir> [--partition <name>] [--db <ref>] [--json]
```

The versions of a partition's cache, which live in the catalog and nowhere else
([documents.md](../../documents.md#the-partition-cache)). A catalog keeps one cache per OSDU data partition,
filled by every cache flow (`flowType: cache`) that names the partition under `partitions`, or whose
`source.headers.data-partition-id` names it. Both
forms need the catalog connection (`--db <ref>`, or `SQLFLOW_CATALOG_DB`); without one they fail with
`Caches live in the catalog. Run 'sqlflow cache' with --db <conn-ref>, or set the catalog variable.`

| Verb | What it does |
| --- | --- |
| `list` | Takes a partition (`dev`), or a cache flow's file, which names the partition the flow fills (its `data-partition-id` reference resolved; every partition a flow naming its partitions builds, one after another, or the one `--partition` names), and prints every version, newest first: its label, `current` against the current one, how many records in how many types, the cache flow that wrote it, when it was captured, for whom, and in which run, and under it the types the version added or changed by their own content hash (or that no type changed); a version written before types were hashed says nothing there. Then the system properties the current version holds, which are settings of the platform rather than cached records: each one's service, name and state, what the service took it from, and why it is unknown ([documents.md](../../documents.md#cache-flow)). A value that is neither a partition id (letters, digits, underscore, hyphen and dot) nor a `${env:...}` or `${keyvault:...}` reference is refused. A partition whose cache has no version yet says to run a cache flow of the partition with the refresh operation. With `--json`, each version also carries its `partition`, its `flow`, its sequence, the version before it, where its content came from, the record count per type with the type's content `hash`, its `change` against the version before (`added`, `changed` or `unchanged`) and the version its content dates from (`since`), each null for a version written before types were hashed, and its `systemProperties` (`service`, `name`, `state`, `source`, `detail`). |
| `import` | Merges the type files in `--from-dir` into the cache of the flow's partition as that cache flow's capture (a flow that names several partitions needs `--partition`, and the files are that partition's records, their ids in it), for work without an OSDU platform (the sample estate keeps such files in `osdu/samples/cache-records`, beside the source folders rather than in one, because a cache lives in the module database and never in a repository). A file is `{Name}.json`: the type's entity type and its records, each an `id` and the captured values. The files have to be exactly what the cache flow declares: a file for every declared OSDU type and none for a type it does not declare, each under the declared entity type, every record an id of that entity type in the flow's partition (`<partition>:<entityType>:<code>`), and no value under a name the type does not capture. A lookup table the flow fills from a table or a dictionary is neither required nor accepted: it is captured from its origin. Anything else is refused, naming every mismatch, and nothing is written. The merge is the one a refresh makes: a record in the files replaces what the cache held for it, a record the flow's last capture held that the files leave out goes only when no other cache flow's capture still holds it, and types the files do not cover are left as they are. When the merge changes the cached content, a version is written, recorded as written by the cache flow and captured by `cli:<user>` with no run, and it becomes current; the line says which types it moved, and `--json` lists them (`typesMoved`, `typesRemoved`). |

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
| `import <schema.json>` | Saves a schema file of one's own. A bundled file, one whose every `$ref` points into its own `definitions` (the form `capture` saves, and the one the sample estate keeps under `osdu/samples/templates`), is saved as it is. A file as the data definitions publish it refers to the shared schemas beside it (`../abstract/...`); those are read from `--release` (the newest by default) and bundled in exactly as `capture` bundles them. |
| `import --from-dir <dir>` | Bundles the kind's schema from a local checkout of the OSDU data definitions (its `Generated` folder) and saves it, exactly as `capture` bundles it from the repository. |
| `list` | Every saved version: kind, version, when and by whom it was saved, and where it came from. |
| `show` | One version laid out as a template: every variable with its shape, whether the schema requires it, who writes it (a mapping, OSDU Delivery or OSDU), the entity types it points to and its unit context. Without `--version`, the most recently saved version of the kind. |
| `delete` | Deletes a version. Refused while a synced mapping pins it, naming the mappings. |

A version is its content: saving a schema that is already saved changes nothing and says so
(`template <kind> version <version> was already saved`), and a schema that differs from every saved version of its
kind is saved beside them as a new version. With `--json` the saved template is reported with `outcome` `created`
or `unchanged`. A schema that describes another kind, refers to anything outside itself, or declares no `data`
property is refused.

## assertions

```bash
sqlflow assertions list <flow.yaml> [--partition <name>] [--max <n>] [--db <ref>] [--json]
sqlflow assertions status <flow.yaml> [--partition <name>] [--db <ref>] [--json]
sqlflow assertions report <flow.yaml> [--partition <name>] [--run <n>] [--format json|md|html|junit] [--out <file>] [--db <ref>]
```

The reports an assertion flow's runs keep in the module database
([docs/assertions-design.md](../../../../docs/assertions-design.md)), read where an operator already is, at a terminal or
in a CI job, without a control plane. Every form needs the module database (`--db <ref>`). A flow that names several
partitions reads the one `--partition` names; one that names one reads it.

| Verb | What it does |
| --- | --- |
| `list` | The flow's runs in the partition, newest first (20 unless `--max` says otherwise, at most 1,000): the report number, status, when, the counts of each outcome, who ran it, and why a run failed. |
| `status` | Where each test of the flow stands: its latest outcome and report number, what it matched, how many assertions did not hold and why it errored, and whether the test changed since that result. A test with no result yet says `not run`. |
| `report` | A run's full report, the latest unless `--run` names one, as JSON (the default), Markdown, HTML or JUnit XML, rendered by the same code as the control plane's. `--out` writes it to a file, which a CI job publishes: JUnit XML for its test view, HTML as an artifact to read. A run of another flow, or of the flow in another partition, is refused. |

Running the tests is a run like any other: `sqlflow run <flow.yaml>` runs every test, and the payload picks some
(`--payload '{"tests":["log-headers"],"tags":["smoke"]}'`). A CI job runs the tests, then writes the report:

```bash
sqlflow run flows/recall-welllog-04-header-assertion.yaml --set partition=dev --db osdu
sqlflow assertions report flows/recall-welllog-04-header-assertion.yaml --partition dev --format junit --out tests.xml --db osdu
```

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

A flow that names its partitions ([documents.md](../../documents.md#partitions)) runs in the partition the run value
`partition` names: `--set partition=test`, as a run started in the GUI takes the partition picked in its title bar and a schedule's
`values: { partition: test }` give it. The kind takes it off the flow's parameters and binds the flow to it, so the ids,
the header, the cache, the ledger and the configuration of the run are that partition's. A delivery flow that names
several partitions refuses a run that names none, and one that names one runs in it; a cache flow refreshes the one
named, or every partition it names in turn; an assertion flow tests the one named, one partition per run, and refuses
`*`. A flow that names none refuses the value, unless it declares a parameter of that name, which then takes it as any
parameter; a retrieval flow refuses it.

### The operations

| Flow kind | Operations | Default |
| --- | --- | --- |
| `delivery` | `deliver` (read the changed records, plan against the ledger, deliver what changed), `plan` (render and compare, report what would be delivered, change nothing), `intake` (plan into work batches without delivering), `drain` (deliver the pending batches without re-reading the source), `verify` (read delivered records back from OSDU and compare versions), `sync` (read the records' rows from the ingestion tables and consolidate the ledger with them; sends nothing) | `deliver` |
| `retrieval` | `retrieve`, `plan` | `retrieve` |
| `cache` | `refresh` (capture every declared type and merge it into the partition's cache), `plan` (count what each type's search matches, write nothing) | `refresh` |
| `assertion` | `test` (run the tests and keep their report), `plan` (check each test against its template and count what it matches and would read; record nothing) | `test` |

A delivery flow needs the catalog for every operation: `deliver`, `plan` and `intake` render against the template
and the cache version saved there, and `verify` and `drain` work on the ledger. A cache flow's `refresh` needs it
because the versions it writes live there. An assertion flow's `test` needs it for the report it keeps and the
templates its tests are checked against. A retrieval flow runs without it.

### The payload

Everything that scopes or forces a run beyond its operation travels in the `--payload` JSON object, because the
platform's own flags are deliberately kind-agnostic: forcing past the change gates, re-running one submission,
scoping the run to particular record keys, and choosing what of a scoped record is sent again. The kind validates
the payload at every trust boundary, so a payload no engine path could honor is refused before anything is queued
rather than half-applied.

| Field | Meaning | Operations |
| --- | --- | --- |
| `force` | `true` lifts the whole-run gates (the tier 0 skip and an already completed submission); each record's own hashes still decide what is sent. | all but `drain` and `sync` |
| `submissionId` | The submission the run works on: a re-run, or a fan-out member's share. | all but `verify`, `replan` and `sync` |
| `recordKeys` | The delivery keys (UUIDs) the run is scoped to, at most 1,000, each once. Not with `submissionId`. | `deliver`, `plan`, `intake`, `verify`, `sync` |
| `redeliver` | What a run scoped to `recordKeys` sends again: `all` (the default), `record` (the record document; its datasets and bulk data keep what OSDU holds), `files` (uploaded and registered again, on the file, dataset, manifest and composed routes, and the workflow route with files), `bulk` (a new version of the bulk data, on the ddms and composed routes) or `workflow` (the workflow route's stages run again). On the composed and workflow routes the named part goes alone. `metadata` names the record and `payload` every part. A part the route does not send fails the run. | `deliver` |
| `slices` | The key slices of `submissionId` a fan-out intake member plans (indexes 0 to 1023, each once). | `intake` |
| `interface` | The one interface of a source the run works on. A run on records or slices of a source with several interfaces has to name it. | all |
| `interfaces` | The interfaces a run of a source runs, each once; every interface when left out. Not with `interface`, `submissionId`, `recordKeys` or `slices`. | all |

An assertion flow's payload takes two fields and no other: `tests`, the names of the tests to run, and `tags`, running
every test that carries one of them, each at most 500 and each named once. With neither, every test runs. A name that is
not a test of the flow, or a tag no test carries, fails the run, naming the tests and tags the flow has.

```bash
# plan one log source, forcing past the change gates
sqlflow run flows/recall-welllog-03-header-delivery.yaml --operation plan --set logSource=STAT_COMP --payload '{"force":true}'

# drain the pending batches of one submission
sqlflow trigger --repo recall --flow recall-welllog-03-header-delivery --operation drain --payload @submission.json

# deliver two interfaces of a source, and nothing else of it
sqlflow run flows/recall.yaml --set logSource=STAT_COMP --payload '{"interfaces":["wellbores","welllogs"]}'

# send the curves of one well log again
sqlflow run flows/recall.yaml --set logSource=STAT_COMP --payload '{"interface":"welllogs","recordKeys":["<key>"],"redeliver":"bulk"}'

# run the smoke tests of an assertion flow, and one more by name
sqlflow run flows/recall-welllog-04-header-assertion.yaml --set partition=dev --payload '{"tags":["smoke"],"tests":["log-curves"]}'
```

### The result

The run's result carries the submission and the record counts (planned, delivered, held, failed, unchanged),
which the run page and the runs list show. A refresh's result carries the partition and the cache flow, the
version the partition's cache holds after it, the version it replaced, whether a version was written, when it was
captured, and per type what was captured and what its changes reach; a plan on a cache flow carries the partition,
the flow, the current version and what each type's search matches. A cache flow that names several partitions, run for
all of them, carries one such result per partition, with the error of any that failed: the run fails when one did, and
says which, while the others stay refreshed. A test run's result names the report (`assertionRunId`), the partition, the
status, the count of each outcome and the tests that did not pass (the first 50, the rest counted); the run fails when
the flow's `failRunOn` says its outcomes do (a failed or errored test by default), and its error names them. A plan's
result lists each test with what it matches, would read, the template it fits and its problems.

## Exit codes

`check`, `cache`, `template` and `assertions` exit 0 on success and 1 on a failure, which prints one `ERROR` line naming
the problem; `assertions report` writes the report of a run whatever its tests found, and exits 0 when it did. `preview` exits 1 as well when no record was found to preview, after saying why. `values` exits 1 as well when
a row checked is held, writes a value the template does not accept, or has an empty key part; a row that leaves an
optional attribute out is reported without failing it. `fixtures update` exits 1 as well when any fixture was skipped, after writing the others, so a script never
takes a partial update for a complete one. `run` exits 0 when the run succeeded and 1 when it failed (for an assertion flow, when its tests failed it as its
`failRunOn` says); Ctrl+C exits 130.
