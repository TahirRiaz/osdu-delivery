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
| Repository layout | flow repositories | `mappings/` next to the flows (or named under `render.mappings`), and the cache flows (`flowType: cache`) that define the caches the mappings read, committed and synced. The templates the mappings pin and every version of every cache live in the catalog, never in the repository; nothing writes to the repository. |

## First deployment

1. Grant the nodes' identity read on the drop container, write on the work location (`source.work`, or the
   drop's `.work` folder by default), and read/write on the known-state and retrieval locations (the
   Unity Catalog external-location grant is on the critical path for Databricks; see [design.md](design.md)
   section 3.1).
2. Provision the catalog: the control plane migrates it on start, or `sqlflow db migrate --db <ref>`. The
   ledger's tables come with it.
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
   `delivery`, and its mappings appear under Mappings, each with the template it pins. The cache flow its mapping
   reads (the one `render.cache` names) appears as a pipeline of kind `cache`, and its cache on the OSDU cache page,
   with no version yet.
5. Capture the cache: run the cache flow with the refresh operation (Refresh now on the OSDU cache page, the Trigger
   run dialog on its pipeline, or `sqlflow run caches/osdu-reference-cache.yaml --db <ref>` on a workstation), then
   check the delivery flow against its template and the cache version it now reads:

   ```bash
   sqlflow check flows/recall-welllog.yaml --set logSource=STAT_COMP --db <ref>
   ```

   The refresh searches the endpoint the cache flow declares, with the cache flow's own credentials, and writes the
   cache's first version into the catalog, current from then on. Without access to OSDU,
   `sqlflow cache import caches/osdu-reference-cache.yaml --from-dir <dir> --db <ref>` writes type files as a version
   instead. The cache flow's `schedule` keeps the cache refreshed ahead of the deliveries.
6. Have the preparing side write a drop and plan it before anything touches OSDU: trigger a run with
   operation `plan` and the flow parameters (the GUI's Trigger run dialog, or
   `sqlflow run flows/recall-welllog.yaml --operation plan --set logSource=STAT_COMP`).
7. Deliver the canary log source (a run with operation `deliver`), then the rest. Put the hourly deliver on
   a schedule.

## The API

Every delivery route lives under `/api/v1/delivery` and uses the platform's tokens and scopes.

| Route | Scope | Purpose |
| --- | --- | --- |
| `POST /submissions` | operate | A submission: the manifest notification `{ pipelineId or flow (+ repoId), drop, parameters, force }`, or records sent inline `{ pipelineId or flow (+ repoId), records, parameters, submissionId, operation, force }` ([submitting-records.md](submitting-records.md)). Queues the run and answers 202 with its id; a repeat of an inline request already accepted answers 200 with the run it started, and a different request under the same `submissionId` is 409. |
| `GET /flows/{pipelineId}/stats` | read | Record counts by state, drift, the last 24 hours, the last submission. |
| `GET /flows/{pipelineId}/records` | read | Paged, filtered records: `search` (a delivery key, or a prefix over label, source key and OSDU id; `mode=contains` for substring), `status`, `submissionId`, `runId` (the records that run touched, through its attempts), `drifted`. |
| `GET /flows/{pipelineId}/target` | read | Where the flow's records live: endpoint as declared, data partition, protocol, auth type, and the path each removal scope calls. |
| `GET /flows/{pipelineId}/submissions` | read | The flow's submissions, newest first. `reference` narrows them to the ones whose caller-supplied reference contains it, which is how a source finds work it knows by its own name. |
| `GET /flows/{pipelineId}/retrievals` | read | A retrieval flow's runs, newest first: window, location, counts, outcome. |
| `GET /manual-submission/flows` | read | The flows records can be submitted to by hand (those declaring `source.manualSubmission`), with what each renders with (the mapping, and the template it fills as `templateKind` and `templateVersion`, both null when the mapping is not synced or is invalid), the parameters a submission carries and the payload its records point at. `all=true` lists the other delivery flows too, each with the reason it takes none. |
| `POST /dropoffs` | operate | Uploads files (multipart) into the deployment's drop-off area, for a submission to point at afterwards. Answers with the location they landed under, each file's size and SHA-256, and the drop-off's id. Refused when no drop-off area is configured (`SQLFLOW_DROPOFF_ROOT`). |
| `POST /dropoffs/reserve` | operate | Reserves a drop-off the caller uploads into itself, for a file too large to send through the control plane: the row is written first, then one write-only URL per file, valid until `reservedUntilUtc`. Needs a drop-off area on Azure Storage and the Storage Blob Delegator role on the account; refused saying so otherwise. |
| `POST /dropoffs/{id}/complete` | operate | Closes a reservation once its files are written. What landed is read from storage and is what the ledger records: a reserved file that is missing, a different size, or an unexpected file present fails the completion and leaves the reservation open. Hashes given here are the uploader's own, recorded as asserted. |
| `GET /dropoffs`, `GET /dropoffs/{id}` | read | The drop-offs, newest first (filterable by `status` and `search`), and one of them with its files, how the bytes got there (`uploadMode`) and where each file's hash came from (`hashSource`). |
| `DELETE /dropoffs/{id}` | operate | Removes a drop-off's files from storage; the row stays, saying when they went. Re-processing a submission that pointed at them will no longer find its payload. |
| `GET /dropoff-area` | read | Whether this deployment offers a drop-off area, where it is, what one upload may carry, whether it can hand out upload URLs and what one of those may carry, and how long a completed drop-off is kept (0 for indefinitely). |
| `GET /flows/{pipelineId}/source-contract` | read | What a source sends the flow ([submitting-records.md](submitting-records.md) section 4): the parameters it declares, the template version its pinned mapping fills and whether it is saved (`template`), the mapping's `system`, dataset `key` and `label`, the dataset row's `columns` with the entries each serves (as the value, a `findBy` value or an `appliesWhen` condition), the child `datasets` with the lists they fill and their columns, the version column, the payload its records point at (with whether a content hash is required and the roots a location may sit inside), and whether it takes records inline (and why not). |
| `GET /records/{key}`, `/attempts`, `/activities` | read | One record, its delivery history, its interventions. |
| `GET /submissions/{id}`, `/attempts` | read | One submission with the runs that carried it, and its attempts. |
| `GET /submissions/{id}/batches` | read | The submission's work batches, paged, filterable by `status`. |
| `GET /submissions/{id}/content` | read | The records an inline submission carried, as the ledger holds them: who sent them and when, the operation, where a run wrote them as a drop, and the runs that took them. 404 for a drop's submission. |
| `GET /activities`, `GET /activities/{id}` | read | The audit trail, filtered by flow, kind, actor, outcome, time; one activity with its captured log. |
| `GET /mappings`, `/mappings/{id}` | read | The mapping documents the repositories hold. |
| `GET /templates` | read | The saved template versions, by kind and newest first, each with where it came from, who saved it and how many synced mappings pin it ([mapping-templates.md](mapping-templates.md)). |
| `GET /templates/detail`, `GET /templates/schema` | read | One saved version (`kind`, `version`): laid out variable by variable (type, shape, requiredness, who writes it, relationships, unit context and OSDU's description; with `cache`, the types of that cache each variable can be read from), or the bundled schema itself. |
| `POST /templates/preview` | read | A bundled schema (`kind`, `schema`, optional `cache` and `release`) laid out the same way without saving it, with the saved version when it is already saved. |
| `GET /templates/osdu/releases` | read | The releases of the OSDU data definitions (the Open Group's public schema repository), newest first: each tag, the commit it names, its schema folder on the web, and whether it is in the control plane's local copy (`local`); `syncedUtc` is when the list was last read from the repository. 502 when the repository cannot be read and no list is on disk. |
| `POST /templates/osdu/sync` | operate | Reads the release list again from the repository and downloads `release` (the newest when omitted) into the local copy when it is not on disk: the list, when it was read, and the releases downloaded. |
| `GET /templates/osdu/schemas` | read | Every record kind a release publishes (`release`, the newest when omitted): kind, entity type, version, status, and its file with a link to it. 404 for a release the repository does not have. |
| `GET /templates/osdu/compare` | read | Two versions of one kind (`fromRelease`, `fromKind`, `toRelease`, `toKind`; a release left out is the newest) compared: whether the published files are the same or differ only in their version identifiers, whether they save as the same template, the counts of breaking, additive and wording changes, every variable that differs with each field's value before and after, both files as published, and every shared schema file they refer to that differs (paired by name across versions, with its text and link on each side) with how many are the same. 400 for two different kinds. |
| `GET /templates/osdu/schema` | read | One kind's schema from a release (`kind`, `release`), bundled with every file it refers to, read at the release's commit: the template version it saves as, and the `origin` a save records. Nothing is saved; 404 for a kind the release does not publish. |
| `POST /templates` | author | Saves a bundled schema (`kind`, `schema`, `origin`) as a template version: `outcome` is `created`, or `unchanged` for a version already saved. |
| `DELETE /templates` | author | Deletes a version (`kind`, `version`); 409 while a synced mapping pins it. |
| `GET /mapping-builder/repos` | read | The repositories a mapping can be written for: the source a proposal is opened against, and the delivery flows with their endpoint, what they render with and the cache they name. |
| `GET /mapping-builder/caches` | read | Every cache a synced cache flow declares, with the repository that declares it, its current version and its cached types: what the builder's Cache picker offers. |
| `POST /mapping-builder/draft` | read | A new mapping (`cache`, `kind`, `version`, `name`, `mappingVersion`, `system`) for a saved template: the four access and legal entries to fill, and a cache entry for every variable outside a repeater that points to an entity type the named cache holds. |
| `POST /mapping-builder/compose` | read | A draft written as YAML and checked: what is still missing, whether it loads, and the preflight against its template and the current version of `cache`, with the `parameters` given. |
| `POST /mapping-builder/parse` | read | A mapping document (`yaml`) as a draft the builder edits. |
| `POST /mapping-builder/shape` | read | The shape of the records a mapping document (`yaml`, optional `path` and `parameters`) renders, drawn against its saved template without a row or a cache: the record with a placeholder naming the type and the source wherever a value comes from a row or the cache, the parameters the mapping declares with the value used, notes on what the placeholders cannot say, and the issue that stopped it when the document does not load or its template is not saved ([mapping-templates.md](mapping-templates.md)). |
| `GET /caches` | read | Every cache the synced cache flows declare, narrowed to one repository by `repoId`: its name and repository, the cache flow's `relativePath` and `pipelineId`, the `endpoint` it is captured from as declared, `makeCurrent`, each declared type (kind, query, kept paths, `onChange`, and how many records the current version holds of it), the schedules that refresh it, the current version with who captured it and in which run, and how many versions it holds. |
| `GET /cache/items` | read | The cached records of one `cache` at one `version` (the current one when none is named), paged, filtered by `type` and searched with `search` over every value they hold. |
| `GET /cache/versions` | read | The versions of one `cache`, newest first, each with whether it is current, when it was captured, by whom and in which run, where its content came from, and its types and record counts. |
| `GET /cache/history` | read | The versions of one `cache`, newest first, each with the version captured before it and how many records it changed, added and removed; `type` narrows the counts to one cached type. |
| `GET /cache/diff` | read | What changed in one `cache` between two versions: `from` (required) and `to` (the current version when omitted), counts per type, and a page of the records that changed, were added or were removed, with the captured values on each side. Narrowed by `type`, `search`, and `change` (the items only). 404 for a version the cache does not hold. |
| `GET /cache/tags` | read | The cache changes delivered records were built from, paged, by `status` (pending, approved, rolling, rejected, applied), each naming the cache whose refresh found it, with what it reaches and how far the rollout has carried it. |
| `POST /cache/tags/decide` | operate | Approves or rejects changes (`tagIds`, `approve`). Approving hands the change to the batched rollout; rejecting leaves OSDU as it is. |
| `GET /records/{key}/cache` | read | What one record read out of the cache when it was rendered: the cached item, the path and the value. |
| `POST /flows/{pipelineId}/release` | operate | Release the flow's blocked records (all, or `keys`). |
| `POST /flows/{pipelineId}/probe` | operate | Queue a target probe on a node; poll `GET /api/v1/compute/tasks/{taskId}`. |
| `POST /records/{key}/release`, `/redeliver`, `/verify` | operate | Release one record; redeliver it (`scope` all, metadata or payload; `run` true queues a deliver run of the record's last submission, scoped to the record, that marks it with that scope and sends it); queue a verify run scoped to it. |
| `POST /records/{key}/read` | operate | Queue a read-back of the record as OSDU holds it, on a node. |
| `POST /records/{key}/delete` | operate | Queue a removal of one record (`scope`: `record`, `history` or `everything`) on a node. |
| `POST /flows/{pipelineId}/records/remove` | operate | Queue a removal of many records: `scope`, and either `keys` or `filter` (the listing, every match of which goes). `expected` is refused with 409 when the filter no longer resolves to it. |
| `POST /flows/{pipelineId}/records/remove/preview` | read | What that removal would act on: how many records, how many OSDU was ever given, and the target it is aimed at. |
| `POST /ledger/prune` | admin | Age out attempts older than `olderThanDays`, keeping the latest per record. |

Runs carry the delivery parameters on the platform's trigger (`POST /api/v1/runs`): `operation`, `force`,
`values`, `drop`, `submissionId`, `recordKeys`, `redeliver` (what a deliver run scoped to `recordKeys` sends again:
`all`, the default, `metadata` or `payload`), `publishTo`, and for a fan-out member `partitions`. The run row
records them, the delivery counts are projected onto it when the run completes (a run's own work: what it planned,
sent and held, with the submission's totals across every run under `submission`; a fan-out root reports the submission
its members worked on), and its result (the operation's
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
- **A flow's page** (Pipelines): the Delivery tab (stats, submit a drop, submit records, probe the target, release blocked),
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
  records, and for records sent inline the Records sent tab: the records as sent, who sent them and when, and where
  the run wrote them as a drop.
- **Manual submission** (Operate): every flow whose document offers manual submission, with what it renders with (the
  mapping and the template kind it fills) and the parameters a submission carries; Submit records opens the same sheet
  for the flow chosen. A switch lists the flows that take no records too, each saying why.
- **Submit records** (a flow's Delivery tab): one record through a form built from the flow's source contract (each
  field with what it fills, each child dataset with the list it fills, and the template kind and version next to the
  mapping), or any number as JSON in the shape of a mapping fixture (`record` and `datasets`), with the flow parameters,
  a preview (plan) switch, force and an optional submission id. It makes the same `POST /submissions` a source system
  makes and opens the run it queued. A flow whose document offers no manual submission shows why it takes no records,
  and for a flow that streams payload files each record also says where its files already are.
- **A retrieval flow's page** (Pipelines): the Retrievals tab, every run with its window, location, counts and
  outcome; a row opens the platform run.
- **A cache flow's page** (Pipelines): the Cache versions tab, every version the cache flow captured into the catalog,
  with a link to the OSDU cache page for what the cache holds and the changes waiting for approval.
- **Audit trail** (Operate): every run and intervention across flows, by actor, with parameters and log.
- **Mappings** (Workspace): the mapping documents the repositories hold, each mapping with the template it pins and a
  link to the Mapping builder.
- **Templates**: browse the schemas OSDU publishes through a delivery flow's connection (a node runs the search and the
  fetch with the flow's credentials), look at one laid out as a template (every variable with its type, requiredness,
  relationships, unit context and OSDU's description), and save it; or import a bundled schema file. The saved
  templates are listed with where each version came from and how many mappings pin it, and a version no mapping pins
  can be deleted.
- **Mapping builder**: pick the repository, a saved template and the cache the mapping reads (the Cache picker
  defaults to the cache the repository's delivery flow names), and the page lists every template variable, with a
  cache entry prefilled for each variable outside a repeater that points to an entity type that cache holds. Each
  entry takes its value from the dataset, a repeater, the cache or a static value, with its modifiers, condition and
  required flag, and the YAML and its checks against the template and the cache's current version follow every edit. The mapping is copied, or proposed to the repository as a pull request through the proposal endpoint
  (`POST /api/v1/repos/sources/{id}/proposals`). An existing synced mapping opens with its entries filled in.
- **OSDU cache** (Workspace): the reference and master data every delivered document is built from. A cache picker in
  the header chooses among the caches the synced cache flows declare. A definition card comes first and says what the
  cache is: the file that defines it, View YAML (the cache flow's pipeline page) and Refresh now (the trigger dialog on
  the cache flow, with the refresh operation), the endpoint it is captured from, the schedules that refresh it, the
  current version with when and by whom it was captured and a link to the run, how many versions it holds, and the
  declared types, each with the kind it is searched as and its query, the paths it keeps, what a changed value does
  (waits for approval, or goes out on the next run) and how many records the current version holds. Picking a type
  scopes the tabs below to it. One filter row carries the search over every cached value, id and alias (typing brings
  the Records tab forward), a searchable type picker (each type with its family, how many records it holds and the
  names it captures) and the version picker. The definition is read-only because it lives in the cache flow's file in
  git; the decision on a change is the one thing made here.
- **Records.** The cached records at the version being read, searched over every value they hold (id, code, name,
  alias). With a type in scope the table has one column per captured name, so a unit's code, name and id read down
  the page; over every type the values fold into one column. The repeated part of an OSDU id (partition and entity
  type) steps back so the column reads by what tells the records apart. A row opens the record: its captured values
  by name, the JSON as captured, and the version it is the record as of.
- **Reading the cache as it stood.** The version picker in the filter row reads the records at one version. It opens
  on the current version, the one deliveries render against unless a flow pins another; picking an earlier one says
  so on the page, because nothing shown then is what a render would read today. Every version the cache flow's runs
  captured stays readable.
- **Versions.** Every version, newest first, each with what captured it (the run and who asked, or an import from
  files) and what it changed compared with the version captured before it (so many changed, added, removed, or no
  changes), with the counts for the type in scope when one is picked and the versions that left it untouched folded
  away. Picking a version raises its changes in the workbench bottom panel, so the list stays in view: changed
  records with the captured values that moved, before and after, records the version added, and records it no longer
  holds, narrowed by a change kind and a search. A row opens both sides. Versions covers the whole cache, which is
  what separates it from Approvals: Approvals holds only the changes that reach records already delivered, so a refresh
  that moved values nothing was built from shows in the history and leaves Approvals empty.
- **Approvals.** The cache changes that reach records already delivered, by state (awaiting approval, approved,
  rolling out, rolled out, rejected), each with the value before and after, how many delivered records it reaches
  and how far the rollout has carried it. Picking a change raises it in the bottom panel with both values in full,
  the versions it moved between, who decided it and when, and the Approve and Reject buttons while it is still open.
  Selecting changes raises a toolbar in the table that says how many delivered records the decision would redeliver,
  and approves or rejects them in bulk.
- **Runs**: a delivery run is a platform run; its trace streams live and its parameters, record counts and
  result show on the run page; a fan-out member shows its root and slot. Re-run repeats the same parameters.
  The trigger dialog offers the operations the flow's kind runs.

## The CLI

| Verb | Purpose |
| --- | --- |
| `sqlflow validate <flow.yaml>` | The platform's document validation (the CI gate for a folder). |
| `sqlflow check <flow.yaml> [--drop <location>] [--set name=value]... [--db <ref>] [--json]` | The delivery preflight: the mapping against its pinned template and the version of the cache the flow names, both loaded from the catalog, and the drop's manifest when present. |
| `sqlflow run <flow.yaml> [--operation deliver\|verify\|plan\|known-state\|intake\|drain\|retrieve\|refresh] [--force] [--set name=value]... [--drop <location>] [--submission <id>] [--record <key>]... [--publish-to <location>] [--db <ref>]` | A run on the workstation. A delivery flow's runs need the catalog connection: rendering reads the template and the cache version saved there, and the ledger lives there. A retrieval flow runs `retrieve` by default, and runs without one. A cache flow runs `refresh` by default, which writes the cache's versions into the catalog and so needs it. |
| `sqlflow cache list <cache.yaml\|name> [--db <ref>] [--json]` | The versions of a cache, newest first: when each was captured, by whom and in which run, and what it holds. |
| `sqlflow cache import <cache.yaml> --from-dir <dir> [--no-current] [--db <ref>] [--json]` | Write type files (`{Name}.json`) as a version of the cache, for work without OSDU. The files must match the types, entity types and captured names the cache flow declares. A cache is captured from OSDU with `sqlflow run <cache.yaml>`. |
| `sqlflow template capture --kind <kind> [--release <tag>]` | Save a kind's schema from the OSDU data definitions as a template version (the newest release by default). |
| `sqlflow template import <schema.json> --kind <kind> [--release <tag>]`, `sqlflow template import --from-dir <dir> --kind <kind>` | Save a template from a schema file, or from a local checkout of the OSDU data definitions. A bundled file is saved as it is; a file as the data definitions publish it, referring to `../abstract/...` schemas, has those read from `--release` (the newest by default), and the template's origin names the release. |
| `sqlflow template list \| show --kind <kind> [--version <v>] \| delete --kind <kind> --version <v>` | The saved templates, one laid out variable by variable, and deleting a version no synced mapping pins. Every `template` verb needs the catalog connection (`--db <ref>`). |
| `sqlflow trigger --repo <r> --flow <f> [the same run options]` | Queue a run on the fleet. |

See [../reference/cli/delivery.md](../reference/cli/delivery.md).

## Runbook

| Symptom | Where to look | Action |
| --- | --- | --- |
| Submission `failed` with a validation message | The submission page; the run's trace | Fix the drop or the documents; submit the drop again (the same id is fine). |
| Records `held` | The Records tab filtered to held | Read the last error. Fix the data (reference miss, empty key) or the mapping; then Release (one record, or all blocked). |
| Records `failed` | The record's History tab | The retry budget is spent; the last error is redacted but specific. Release after fixing the cause. |
| Records stuck `delivering` | `Lease` on the record page in the past | A worker stopped mid-delivery. The next deliver run of the record's drop (the recovered run, a re-run of the submission, or `drain`) waits out the lease, reclaims it and sends the record; nothing else to do unless a node is wedged. |
| A verify run reports drift | The Records tab with Drifted only | Decide whether the edit in OSDU was legitimate. Redeliver the record, or set `verify.reconcile: true` so verify runs queue redelivery. |
| Everything re-renders after a change | The render context on the record | Only `render.*` and the template version its mapping pins enter the render context; a moved mapping version, template version or cache version renders every record that uses it again. Only a record whose rendered document differs is sent; the rest are skipped as unchanged and take the new context. |
| A run fails: the mapping pins a template that is not saved in the catalog | The run's error names the mapping, the kind and the version | Save that version (the Templates page, `sqlflow template capture` or `import`) and run again. A schema that changed since saves as another version, which the mapping then has to pin. |
| Is OSDU reachable with the flow's credentials? | Probe target on the flow's Delivery tab | The probe runs on a node and reports the status of the service's info endpoint. |
| A submission stays `running` with batches `queued` | The submission's batches; the run page's fan-out family | A drain member failed or a node went away. The parent settles what it can; re-run the submission (or trigger `drain` with the submission) to drain the rest. |
| Records pending with `workflow run ... failed` | The record's attempts: the `workflow` step names the run | The ingestion DAG failed; its own log says why. The next try triggers a new run automatically; fix the data or the manifest section first when the DAG rejected the content. |
| A retrieval run `failed` | The Retrievals tab: the row's error; the run's trace | The watermark did not move, so the next run covers the same window. Fix the cause (credentials, the query, the lake location) and run again; a run's directory is never reused. |
| A delivery run fails: cache '&lt;name&gt;' has no current version | The run's error names the cache | Run the cache flow of that name with the refresh operation (Refresh now on the OSDU cache page), or write a version with `sqlflow cache import`, and run again. |
| A delivery run fails: the mapping reads the cache, so `render.cache` must name the cache it reads | The run's error names the flow and the mapping | Name the cache flow under `render.cache` in the delivery flow. A pinned `render.cacheVersion` the catalog does not hold fails the same way: pin a version `sqlflow cache list` shows, or remove the pin to read the current version. |
| A cache refresh wrote no version | The run's trace: the cache is reported unchanged at its current version | The capture found exactly what the current version holds, so nothing moved and nothing renders again. That is the expected outcome of a refresh with nothing new. |
| A failure has to be followed into OSDU's own logs | The record's History tab: the attempt's result names its `correlationId`, and a refused request's error quotes `(correlation-id ...)` | Give the OSDU operators that id: every request of the try carried it. |

## Size ceilings

Set `MaxRequestBodySize` on petrodb-api, the ingress limit and the APIM limit to one deliberate number, and
derive the chunk cell limit in the preparing job from it ([design.md](design.md) section 14.3). A 413 holds
the record here rather than creating a duplicate, but the ceiling still needs to be intentional. The control
plane's own ceiling is `ControlPlane:MaxRequestBodyMegabytes`.
