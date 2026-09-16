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
| `Osdu:Database:Connection` / `SQLFLOW_OSDU_DB` | nodes (and the CLI) | The `osdu` module database, as a `${env:...}` or `${keyvault:...}` reference. A node opens no catalog connection, so this is how it reaches the ledger, the templates and the caches; the login needs rights on schema `osdu` alone. A literal secret is refused at startup. |
| Repository layout | flow repositories | `mappings/` next to the flows (or named under `render.mappings`), and the cache flows (`flowType: cache`) that fill the partition caches the mappings read, committed and synced. The templates the mappings pin and every version of every cache live in the catalog, never in the repository; nothing writes to the repository. |

## First deployment

1. Give the nodes what the flows read and write: the ingestion database the OSDU flow's `source.connection`
   names, read on every payload root and landing folder a flow declares, write on the work location
   (`source.work`), and read/write on the retrieval locations. A node opens no catalog connection, so it also
   needs the `osdu` module database connection of its own (`SQLFLOW_OSDU_DB`), with rights on schema `osdu`
   alone ([../architecture.md](architecture.md)).
2. Provision the databases: the control plane applies SQLFlow's and the module's migrations on start, or
   `sqlflow db migrate --db <ref>`. The ledger's `osdu` schema comes with it.
3. Save the template the mapping pins into the catalog:

   ```bash
   sqlflow template capture --kind osdu:wks:work-product-component--WellLog:1.4.0 --db <ref>
   ```

   The template capture reads the kind's schema from the OSDU data definitions, the Open Group's public repository
   (the newest release, or `--release <tag>`), and reports the template version, which is the version the mapping pins
   under `template`. Without access to the data definitions,
   `sqlflow template import` saves a bundled schema file instead (a file as the data definitions publish it needs a
   release of them for the shared schemas it refers to), and once the repository is synced (step 4) the GUI's
   Templates page browses OSDU and saves a template through the flow's connection on a node.
4. Register the repository as a source in the GUI (Repos) and sync it. The delivery flow appears as a pipeline of kind
   `delivery`, and its mappings appear under Mappings, each with the template it pins. The cache flow
   that fills the partition the delivery flow delivers to (the `data-partition-id` both declare, `opendes` in the
   sample) appears as a pipeline of kind `cache`, and the partition's cache on the OSDU cache page, with no version yet.
5. Capture the cache: run the cache flow with the refresh operation (Refresh now on the OSDU cache page, the Trigger
   run dialog on its pipeline, or `sqlflow run caches/osdu-reference-cache.yaml --db <ref>` on a workstation), then
   check the delivery flow against its template and the cache version it now reads:

   ```bash
   sqlflow check flows/recall-welllog.yaml --set logSource=STAT_COMP --db <ref>
   ```

   The refresh searches the endpoint the cache flow declares, with the cache flow's own credentials, and merges what it
   found into the cache of its partition, writing that cache's first version into the catalog; the newest version is
   always the current one. Without access to OSDU,
   `sqlflow cache import caches/osdu-reference-cache.yaml --from-dir <dir> --db <ref>` merges type files into the
   partition's cache instead. `sqlflow cache list opendes --db <ref>` lists the versions. The cache flow's `schedule` keeps the cache refreshed ahead of the deliveries.
6. Load the ingestion tables and plan before anything touches OSDU: run the pre-ingestion flow and then the
   ingestion flow beside the OSDU flow (lineage puts them in that order, so triggering the chain runs them in
   waves), then trigger the OSDU flow with operation `plan` and the flow parameters (the GUI's Trigger run
   dialog, or `sqlflow run flows/recall-welllog.yaml --operation plan --set logSource=STAT_COMP`).
7. Deliver the canary log source (a run with operation `deliver`), then the rest. Put the hourly deliver on
   a schedule.

## The API

Every delivery route lives under `/api/v1/delivery` and uses the platform's tokens and scopes.

| Route | Scope | Purpose |
| --- | --- | --- |
| `POST /submissions` | operate | Records sent by a source: `{ pipelineId or flow (+ repoId), records, parameters, submissionId, operation, force, reference, reland }` ([submitting-records.md](submitting-records.md)). The rows are landed as files for the flow's declared pre-ingestion flows and the chain of pre, ingestion and OSDU runs is queued with them; 202 carries the submission id, the chain group and the OSDU run. A repeat of a request already accepted answers 200 with the chain it queued, and a different request under the same `submissionId` is 409. |
| `GET /flows/{pipelineId}/stats` | read | Record counts by state, drift, the last 24 hours, the last submission. |
| `GET /flows/{pipelineId}/records` | read | Paged, filtered records: `search` (a delivery key, or a prefix over label, source key and OSDU id; `mode=contains` for substring), `status`, `submissionId`, `runId` (the records that run touched, through its attempts), `drifted`. |
| `GET /flows/{pipelineId}/target` | read | Where the flow's records live: endpoint as declared, data partition, protocol, auth type, and the path each removal scope calls. |
| `GET /flows/{pipelineId}/submissions` | read | The flow's submissions, newest first. `reference` narrows them to the ones whose caller-supplied reference contains it, which is how a source finds work it knows by its own name. |
| `GET /flows/{pipelineId}/retrievals` | read | A retrieval flow's runs, newest first: window, location, counts, outcome. |
| `GET /manual-submission/flows` | read | The flows records can be submitted to by hand (those declaring `source.submissions`), with what each renders with (the mapping, and the template it fills as `templateKind` and `templateVersion`, both null when the mapping is not synced or is invalid), the parameters a submission carries and the payloads its records point at. `all=true` lists the other delivery flows too, each with the reason it takes none. |
| `GET /flows/{pipelineId}/source-contract` | read | What a source sends the flow ([submitting-records.md](submitting-records.md) section 4): the parameters it declares, the template version its pinned mapping fills and whether it is saved (`template`), the mapping's `system`, record `key` and `label`, the dataset row's `columns` with the entries each serves (as the value, a `findBy` value or an `appliesWhen` condition), the child `datasets` with the lists they fill and their columns, the version column, the ingestion table it reads (`sourceObject`) and the system column its incremental reads window on (`updatedColumn`), the payloads its records point at (with whether a content hash is required and the roots a location may sit inside), and whether it takes records (and why not). |
| `GET /records/{key}`, `/attempts`, `/activities` | read | One record, its delivery history, its interventions. |
| `GET /submissions/{id}`, `/attempts` | read | One submission with the runs that carried it, and its attempts. |
| `GET /submissions/{id}/batches` | read | The submission's work batches, paged, filterable by `status`. |
| `GET /submissions/{id}/content` | read | The records an API submission carried, as the ledger holds them: who sent them and when, the operation, how far it got (`status`, `landedUtc`, `groupId`, `osduRunId`, `error`), the file landed for each dataset with the pre flow and pre run that took it (`landings`), and the runs that took them. 404 for a submission the ledger holds no records for. |
| `GET /activities`, `GET /activities/{id}` | read | The audit trail, filtered by flow, kind, actor, outcome, time; one activity with its captured log. |
| `GET /mappings`, `/mappings/{id}` | read | The mapping documents the repositories hold. |
| `GET /templates` | read | The saved template versions, by kind and newest first, each with where it came from, who saved it and how many synced mappings pin it ([mapping-templates.md](mapping-templates.md)). |
| `GET /templates/detail`, `GET /templates/schema` | read | One saved version (`kind`, `version`): laid out variable by variable (type, shape, requiredness, who writes it, relationships, unit context and OSDU's description; with `scope`, the types of that partition's cache each variable can be read from), or the bundled schema itself. |
| `POST /templates/preview` | read | A bundled schema (`kind`, `schema`, optional `scope` and `release`) laid out the same way without saving it, with the saved version when it is already saved. |
| `GET /templates/osdu/releases` | read | The releases of the OSDU data definitions (the Open Group's public schema repository), newest first: each tag, the commit it names, its schema folder on the web, and whether it is in the control plane's local copy (`local`); `syncedUtc` is when the list was last read from the repository. 502 when the repository cannot be read and no list is on disk. |
| `POST /templates/osdu/sync` | operate | Reads the release list again from the repository and downloads `release` (the newest when omitted) into the local copy when it is not on disk: the list, when it was read, and the releases downloaded. |
| `GET /templates/osdu/schemas` | read | Every record kind a release publishes (`release`, the newest when omitted): kind, entity type, version, status, and its file with a link to it. 404 for a release the repository does not have. |
| `GET /templates/osdu/compare` | read | Two versions of one kind (`fromRelease`, `fromKind`, `toRelease`, `toKind`; a release left out is the newest) compared: whether the published files are the same or differ only in their version identifiers, whether they save as the same template, the counts of breaking, additive and wording changes, every variable that differs with each field's value before and after, both files as published, and every shared schema file they refer to that differs (paired by name across versions, with its text and link on each side) with how many are the same. 400 for two different kinds. |
| `GET /templates/osdu/schema` | read | One kind's schema from a release (`kind`, `release`), bundled with every file it refers to, read at the release's commit: the template version it saves as, and the `origin` a save records. Nothing is saved; 404 for a kind the release does not publish. |
| `POST /templates` | author | Saves a bundled schema (`kind`, `schema`, `origin`) as a template version: `outcome` is `created`, or `unchanged` for a version already saved. |
| `DELETE /templates` | author | Deletes a version (`kind`, `version`); 409 while a synced mapping pins it. |
| `GET /mapping-builder/repos` | read | The repositories a mapping can be written for: the source a proposal is opened against, and the delivery flows with their endpoint, what they render with and the partition whose cache they read (`cacheScope`). |
| `GET /mapping-builder/caches` | read | Every partition whose cache a synced cache flow fills: the partition (`scope`), the cache flows filling it, its current version and its cached types: what the builder's Cache picker offers. |
| `POST /mapping-builder/draft` | read | A new mapping (`scope`, `kind`, `version`, `name`, `mappingVersion`, `system`) for a saved template: the four access and legal entries to fill, and a cache entry for every variable outside a repeater that points to an entity type the cache of that partition holds. |
| `POST /mapping-builder/compose` | read | A draft written as YAML and checked: what is still missing, whether it loads, and the preflight against its template and the current version of the cache of `scope`, with the `parameters` given. |
| `POST /mapping-builder/parse` | read | A mapping document (`yaml`) as a draft the builder edits. |
| `POST /mapping-builder/shape` | read | The shape of the records a mapping document (`yaml`, optional `path` and `parameters`) renders, drawn against its saved template without a row or a cache: the record with a placeholder naming the type and the source wherever a value comes from a row or the cache, the parameters the mapping declares with the value used, notes on what the placeholders cannot say, and the issue that stopped it when the document does not load or its template is not saved ([mapping-templates.md](mapping-templates.md)). |
| `GET /caches` | read | One entry per partition whose cache the synced cache flows fill, narrowed by `repoId` to the partitions that repository's cache flows fill: the partition (`scope`); the `flows` filling it, each with its name, repository, `relativePath`, `pipelineId`, the `endpoint` it searches as declared, the schedules that refresh it and the types it declares; the `types` the cache holds as those flows together declare them, each with its entity type, its `sources` (each flow's kind, query and `onChange`), the `fields` it keeps (the path, the name it is cached as, and the flows declaring it), the `onChange` in effect (`approve` when any flow asks for it) and how many records the current version holds of it; the `current` version with the cache flow that wrote it, who asked and in which run; and how many `versions` the cache holds. |
| `GET /cache/items` | read | The cached records of one partition's cache (`scope`) at one `version` (the current one when none is named), paged, filtered by `type` and searched with `search` over every value they hold. |
| `GET /cache/versions` | read | The versions of one partition's cache (`scope`), newest first, each with whether it is current, the cache flow that wrote it, when it was captured, by whom and in which run, where its content came from, and its types and record counts. |
| `GET /cache/history` | read | The versions of one partition's cache (`scope`), newest first, each with the version captured before it and how many records it changed, added and removed; `type` narrows the counts to one cached type. |
| `GET /cache/diff` | read | What changed in one partition's cache (`scope`) between two versions: `from` (required) and `to` (the current version when omitted), counts per type, and a page of the records that changed, were added or were removed, with the captured values on each side. Narrowed by `type`, `search`, and `change` (the items only). 404 for a version the cache does not hold. |
| `GET /cache/tags` | read | The cache changes delivered records were built from, paged, by `status` (pending, approved, rolling, rejected, applied), each naming the partition whose cache the refresh found it in, with what it reaches and how far the rollout has carried it; `scope` narrows the list to one partition. |
| `POST /cache/tags/decide` | operate | Approves or rejects changes (`tagIds`, `approve`). Approving hands the change to the batched rollout; rejecting leaves OSDU as it is. |
| `GET /records/{key}/cache` | read | What one record read out of the cache when it was rendered: the partition, the cached item, the path and the value. |
| `POST /flows/{pipelineId}/release` | operate | Release the flow's blocked records (all, or `keys`). |
| `POST /flows/{pipelineId}/probe` | operate | Queue a target probe on a node; poll `GET /api/v1/compute/tasks/{taskId}`. |
| `POST /records/{key}/release`, `/redeliver`, `/verify` | operate | Release one record; redeliver it (`scope` all, metadata or payload; `run` true queues a deliver run scoped to the record, which reads it from the ingestion tables by key under its last submission's parameter values, marks it with that scope and sends it); queue a verify run scoped to it. |
| `POST /records/{key}/source` | operate | Queue a read of the record's rows as the ingestion tables hold them now, on a node: the record row with its system columns, its child datasets, and the origin file and row. Nothing is planned or delivered; poll `GET /api/v1/compute/tasks/{taskId}`. |
| `POST /records/{key}/read` | operate | Queue a read-back of the record as OSDU holds it, on a node. |
| `POST /records/{key}/delete` | operate | Queue a removal of one record (`scope`: `record`, `history` or `everything`) on a node. |
| `POST /flows/{pipelineId}/records/remove` | operate | Queue a removal of many records: `scope`, and either `keys` or `filter` (the listing, every match of which goes). `expected` is refused with 409 when the filter no longer resolves to it. |
| `POST /flows/{pipelineId}/records/remove/preview` | read | What that removal would act on: how many records, how many OSDU was ever given, and the target it is aimed at. |
| `POST /ledger/prune` | admin | Age out attempts older than `olderThanDays`, keeping the latest per record. |

A run carries its `operation` (`deliver`, `plan`, `intake`, `drain`, `verify` or `replan`) and the flow's `values` on
the platform's trigger (`POST /api/v1/runs`), with the kind's own arguments in the run payload: `force` (lift the
whole-run gates), `submissionId` (the submission to work on), `recordKeys` (scope the run to named records, at most
1,000), `redeliver` (what a run scoped to `recordKeys` sends again: `all`, the default, `metadata` or `payload`),
`slices` (the key slices a fan-out intake member plans) and `reland` (the submission's landing files were written
again first). The payload is parsed strictly: an unknown property, a wrong type, or one that does not apply to the
operation is refused, naming it. Reading every row of the scope again is the `replan` operation rather than a payload
flag. The run row records them, the delivery counts are projected onto it when the run completes (a run's own work:
what it planned, sent and held, with the submission's totals across every run under `submission`; a fan-out root
reports the submission its members worked on), and its result (the operation's outcome as JSON) and its fan-out
membership (root, slot, count) are on the run detail.

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
- **A flow's page** (Pipelines): the Delivery tab (stats, submit records, probe the target, release blocked),
  the Records tab (search and filters, every row opens the record), the Submissions tab, which says for each
  submission which selection it read. Rows tick: a selection
  bar offers "select all N matching" and Remove from OSDU, so a removal can be aimed at exactly the ticked rows
  or at the whole filtered set. A run page links here filtered to the records that run touched.
- **A record's page**: custody state, hashes, versions, the pending document, the render context; the
  history of attempts and interventions; Verify, Redeliver, Read back, Source row (the record's rows as the
  ingestion tables hold them now, read on a node), Release and Remove from OSDU.
- **The removal dialog**: one surface for both. It names the target first (endpoint as declared, data partition,
  protocol, auth) because that is which OSDU the records are about to leave, then the three scopes side by side
  with what each destroys, whether it can be undone, what the ledger will do, and the exact call it makes. The
  two permanent scopes ask the operator to type the data partition back before the button enables.
- **A submission's page**: which selection it read and the window it covered, the ingestion table and connection it
  read from, its counts, the runs that carried it and its chain group, its work batches, its attempts, a link to its
  records, and for records sent through the API the Landed files tab (the file written for each dataset with its pre
  flow, format, row count, hash and the pre run that took it) and the Records sent tab (the records as sent, who sent
  them and when, and how far the submission got).
- **Manual submission** (Operate): every flow whose document declares where its submissions land, with what it renders
  with (the mapping and the template kind it fills) and the parameters a submission carries; Submit records opens the
  same sheet for the flow chosen. A switch lists the flows that take no records too, each saying why.
- **Submit records** (a flow's Delivery tab): one record through a form built from the flow's source contract (each
  field with what it fills, each child dataset with the list it fills, and the template kind and version next to the
  mapping), or any number as JSON in the shape of a mapping fixture (`record` and `datasets`), with the flow parameters,
  a preview (plan) switch, force and an optional submission id. It makes the same `POST /submissions` a source system
  makes and opens the run it queued. A flow whose document takes no records shows why, and for a flow that streams
  payload files each record also says where the files of each payload already are.
- **A retrieval flow's page** (Pipelines): the Retrievals tab, every run with its window, location, counts and
  outcome; a row opens the platform run.
- **A cache flow's page** (Pipelines): the Cache versions tab names the partition the flow fills and how many other
  cache flows fill it too, and lists every version of that partition's cache with the flow that wrote each, with a link
  to the OSDU cache page for what the cache holds and the changes waiting for approval.
- **Audit trail** (Operate): every run and intervention across flows, by actor, with parameters and log.
- **Mappings** (Workspace): the mapping documents the repositories hold, each mapping with the template it pins and a
  link to the Mapping builder.
- **Templates**: browse the schemas OSDU publishes through a delivery flow's connection (a node runs the search and the
  fetch with the flow's credentials), look at one laid out as a template (every variable with its type, requiredness,
  relationships, unit context and OSDU's description), and save it; or import a bundled schema file. The saved
  templates are listed with where each version came from and how many mappings pin it, and a version no mapping pins
  can be deleted.
- **Mapping builder**: pick the repository, a saved template and the partition whose cache the mapping is checked
  against (the Partition cache picker lists every partition a synced cache flow fills, with those flows, and defaults
  to the partition the repository's delivery flow delivers to), and the page lists every template variable, with a
  cache entry prefilled for each variable outside a repeater that points to an entity type that cache holds. Each
  entry takes its value from the dataset, a repeater, the cache or a static value, with its modifiers, condition and
  required flag, and the YAML and its checks against the template and the cache's current version follow every edit. The mapping is copied, or proposed to the repository as a pull request through the proposal endpoint
  (`POST /api/v1/repos/sources/{id}/proposals`). An existing synced mapping opens with its entries filled in.
- **OSDU cache** (Workspace): the reference and master data every delivered document is built from, one cache per OSDU
  partition. The header names the partition and the cache flow file that fills it (or how many flows fill it, with
  every file on hover), with Cache files and Refresh now. Cache files opens Pipelines filtered to the cache flows
  (`?kind=cache`, and `?repo=` when one repository holds every flow filling the partition), since a partition can be
  filled by several files; Refresh now opens the trigger dialog on the cache flow with the refresh operation, and is a
  menu naming the flows when several fill the partition, since a refresh runs one flow's capture. A link to Pipelines
  can set its repository and kind filters (`?repo=`, `?kind=`), and a kind filter opens the groups it narrows the tree
  to. A partition picker joins them when the synced cache flows fill more than
  one partition. A summary row follows: the current version with the flow that wrote it, when and for whom, how many
  records it holds in how many types, whether a schedule refreshes it, and either that changes are automatic or how
  many wait for approval. Whenever a change waits, a banner says so with Review changes. A searchable type picker at
  the end of the tab bar, on the Records and Versions tabs, lists the types with their family, record count and whether
  their changes need approval; picking one scopes those tabs, and Clear filter lifts the scope. The tab, the partition
  (`?scope=`) and the type are in the URL, so a link opens the same view; a link naming a cache flow (`?flow=`, as a
  cache run's page uses) opens the partition that flow fills. The definition is read-only because it lives in the cache
  flow files in git; the decision on a change is the one thing made here.
- **Records.** The cached records at the version being read, with the search over every value they hold (id, code,
  name, alias) and the version picker in the tab's own toolbar. With a type in scope the table has one column per
  captured name, so a unit's code, name and id read down the page; over every type each row names its type, shows the
  part of the id that tells the records apart (the whole id on hover) and folds the values into one column. A row
  opens the record: its captured values by name, how a mapping reads it (each source, `cache.<Type>.id` and
  `cache.<Type>.<name>`, next to the value it reads for this record, and a `source` plus `findBy` entry to copy that
  selects the record by one of its values), the JSON as captured, and the version it is the record as of.
- **Reading the cache as it stood.** The version picker opens on the current version, the one deliveries render
  against unless a flow pins another; picking an earlier one says so above the table, because nothing shown then is
  what a render would read today. Every version the partition's cache flows wrote stays readable.
- **Definition.** What the partition's cache flow files declare, as the last sync found them. A guide says how a
  mapping reads the cache (`source: cache.<Type>.id` or `cache.<Type>.<name>`, and `findBy` lines), with an entry to
  copy, and that a mapping never names the cache. The cache flows follow, each with its repository, file, the endpoint
  it searches, its schedules and the types it declares, with View YAML and Refresh in its row; then each type as the
  flows together declare it: the kind and query each flow searches it with, every path kept (the source that reads it
  and the flows declaring it on hover), and what a changed value does (next run, or needs approval when any flow asks).
- **Versions.** Every version, newest first, each with the cache flow that wrote it and what captured it (the run and
  who asked, or an import from files) and what it changed compared with the version captured before it (so many changed, added, removed, or no
  changes), with the counts for the type in scope when one is picked and the versions that left it untouched folded
  away. Picking a version raises its changes in the workbench bottom panel, so the list stays in view: changed
  records with the captured values that moved, before and after, records the version added, and records it no longer
  holds, narrowed by a change kind and a search. A row opens both sides. Versions covers the whole cache, which is
  what separates it from Changes: Changes holds only the changes that reach records already delivered, so a refresh
  that moved values nothing was built from shows in the versions and leaves Changes empty.
- **Changes.** The changes this cache's refreshes found in values delivered records were built from, by state
  (waiting for approval, approved, rolling out, rolled out, rejected, or all), each with the value before and after,
  how many delivered records it reaches and how far the rollout has carried it. A line above the list says which types
  ask for approval. By default every type updates automatically (`onChange: auto`), so a change is approved as it is
  found and shows here as a rollout, and the list opens on all changes; when a type asks for approval
  (`onChange: approve`) the list opens on the changes waiting, and each waiting row carries Approve and Reject.
  Picking a change raises it in the bottom panel with both values in full, the versions it moved between, who decided
  it and when, and the same decision while it is still open. Selecting waiting changes raises a toolbar that says how
  many delivered records the decision would redeliver, and approves or rejects them in bulk.
- **Runs**: a delivery run is a platform run; its trace streams live and its parameters, record counts and
  result show on the run page; a fan-out member shows its root and slot. Re-run repeats the same parameters.
  The trigger dialog offers the operations the flow's kind runs.

## The CLI

| Verb | Purpose |
| --- | --- |
| `sqlflow validate <flow.yaml>` | The platform's document validation (the CI gate for a folder). |
| `sqlflow check <flow.yaml> [--connect] [--set name=value]... [--db <ref>] [--json]` | The delivery preflight: the flow, its mappings, its templates and its payload roots. With `--connect` it opens the flow's source connection on this machine and reports the tables, their columns, the key types, the system columns, the current watermark window and the candidate counts. |
| `sqlflow run <flow.yaml> [--operation deliver\|verify\|plan\|intake\|drain\|replan\|retrieve\|refresh] [--force] [--set name=value]... [--submission <id>] [--record <key>]... [--db <ref>]` | A run on the workstation. A delivery flow's runs need the module database connection: rendering reads the template and the cache version saved there, and the ledger lives there. A retrieval flow runs `retrieve` by default, and runs without one. A cache flow runs `refresh` by default, which merges into the cache of its partition and so needs it. |
| `sqlflow cache list <partition\|cache.yaml> [--db <ref>] [--json]` | The versions of a partition's cache (a cache flow's file lists the partition it fills), newest first: the cache flow that wrote each, when it was captured, by whom and in which run, and what it holds. |
| `sqlflow cache import <cache.yaml> --from-dir <dir> [--db <ref>] [--json]` | Merge type files (`{Name}.json`) into the cache of the flow's partition as that flow's capture, for work without OSDU. The files must match the types, entity types and captured names the cache flow declares. A cache is captured from OSDU with `sqlflow run <cache.yaml>`. |
| `sqlflow template capture --kind <kind> [--release <tag>]` | Save a kind's schema from the OSDU data definitions as a template version (the newest release by default). |
| `sqlflow template import <schema.json> --kind <kind> [--release <tag>]`, `sqlflow template import --from-dir <dir> --kind <kind>` | Save a template from a schema file, or from a local checkout of the OSDU data definitions. A bundled file is saved as it is; a file as the data definitions publish it, referring to `../abstract/...` schemas, has those read from `--release` (the newest by default), and the template's origin names the release. |
| `sqlflow template list \| show --kind <kind> [--version <v>] \| delete --kind <kind> --version <v>` | The saved templates, one laid out variable by variable, and deleting a version no synced mapping pins. Every `template` verb needs the catalog connection (`--db <ref>`). |
| `sqlflow trigger --repo <r> --flow <f> [the same run options]` | Queue a run on the fleet. |

See [../reference/cli/delivery.md](../reference/cli/delivery.md).

## Runbook

| Symptom | Where to look | Action |
| --- | --- | --- |
| Submission `failed` with a validation message | The submission page; the run's trace | Fix the documents or the source rows, then run the flow again. |
| An API submission stuck at `accepted` or `landed` | The submission's Landed files tab | The control plane's resume service lands the files and queues the chain again on its own; the files it wrote are left alone when their hash already matches. |
| A record is held: the ingestion tables hold no row for its key | The hold names the landing file and the pre and ingestion flows expected to have loaded it | Look at those runs in the chain; the OSDU run reads the record back by key once they have loaded it. |
| Records `held` | The Records tab filtered to held | Read the last error. Fix the data (reference miss, empty key) or the mapping; then Release (one record, or all blocked). |
| Records `failed` | The record's History tab | The retry budget is spent; the last error is redacted but specific. Release after fixing the cause. |
| Records stuck `delivering` | `Lease` on the record page in the past | A worker stopped mid-delivery. The flow's next deliver run (the recovered run, a re-run of the submission, or `drain`) waits out the lease, reclaims it and sends the record; nothing else to do unless a node is wedged. |
| A verify run reports drift | The Records tab with Drifted only | Decide whether the edit in OSDU was legitimate. Redeliver the record, or set `verify.reconcile: true` so verify runs queue redelivery. |
| Everything re-renders after a change | The render context on the record | Only `render.*` and the template version its mapping pins enter the render context; a moved mapping version, template version or cache version renders every record that uses it again. Only a record whose rendered document differs is sent; the rest are skipped as unchanged and take the new context. |
| A run fails: the mapping pins a template that is not saved in the catalog | The run's error names the mapping, the kind and the version | Save that version (the Templates page, `sqlflow template capture` or `import`) and run again. A schema that changed since saves as another version, which the mapping then has to pin. |
| Is OSDU reachable with the flow's credentials? | Probe target on the flow's Delivery tab | The probe runs on a node and reports the status of the service's info endpoint. |
| A submission stays `running` with batches `queued` | The submission's batches; the run page's fan-out family | A drain member failed or a node went away. The parent settles what it can; re-run the submission (or trigger `drain` with the submission) to drain the rest. |
| Records pending with `workflow run ... failed` | The record's attempts: the `workflow` step names the run | The ingestion DAG failed; its own log says why. The next try triggers a new run automatically; fix the data or the manifest section first when the DAG rejected the content. |
| A retrieval run `failed` | The Retrievals tab: the row's error; the run's trace | The watermark did not move, so the next run covers the same window. Fix the cause (credentials, the query, the lake location) and run again; a run's directory is never reused. |
| A delivery run fails: the mapping reads the cache of partition '&lt;partition&gt;', which holds no version yet | The run's error names the mapping and the partition | Run a cache flow whose `source.headers.data-partition-id` is that partition with the refresh operation, or merge type files into it with `sqlflow cache import`, and run again. |
| A flow fails to load: `render.cache is not a setting any more` | The error names the flow file | Remove `render.cache`: a flow reads the cache of the partition in its `target.headers.data-partition-id`. |
| A delivery run fails: `render.cacheVersion` pins a version of the cache of partition '&lt;partition&gt;' the catalog does not hold | The run's error names the version and the partition | Pin a version `sqlflow cache list <partition>` shows, or remove the pin to read the current version. |
| A cache flow fails to load: `makeCurrent is not a setting any more` | The error names the cache flow file | Remove `makeCurrent`: every version a refresh writes becomes the current version of its partition's cache. A delivery flow that has to stay on an earlier version pins it with `render.cacheVersion`. |
| The sync warns that a type is left out of the cache of partition '&lt;partition&gt;' | The repository's sync warnings name both cache flows and what they disagree on | Two cache flows of the partition declare the type with different entity types, or cache one name from different paths. Make the declarations agree, or give one type another name; until then a refresh of the flow left out fails before capturing, saying the same. |
| A cache refresh fails: another refresh of the partition wrote a version at the same time | The run's error names the cache flow and the partition | Nothing of the capture was kept. Run the refresh again. |
| A cache refresh wrote no version | The run's trace: the partition's cache is reported unchanged at its current version | The merge changed no cached content (the flow's membership is still recorded), so nothing moved and nothing renders again. That is the expected outcome of a refresh with nothing new. |
| A failure has to be followed into OSDU's own logs | The record's History tab: the attempt's result names its `correlationId`, and a refused request's error quotes `(correlation-id ...)` | Give the OSDU operators that id: every request of the try carried it. |

## Size ceilings

Set `MaxRequestBodySize` on petrodb-api, the ingress limit and the APIM limit to one deliberate number, and
derive the chunk cell limit in the preparing job from it ([design.md](design.md) section 14.3). A 413 holds
the record here rather than creating a duplicate, but the ceiling still needs to be intentional. The control
plane's own ceiling is `ControlPlane:MaxRequestBodyMegabytes`.
