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
| `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` | nodes, the CLI | The private ranges (CIDR, comma separated) a flow may reach: an OSDU, its storage accounts or a proxy behind a private endpoint. Empty by default: every private address is refused, whether a URL names it or a host name resolves to it. Link-local and cloud metadata addresses are never reachable. |
| `ControlPlane:MaxRequestBodyMegabytes` | control plane | The API's request body ceiling, set on purpose rather than left at Kestrel's default. Default 64. |
| `Osdu:Database:Connection` / `SQLFLOW_OSDU_DB` | nodes (and the CLI) | The `osdu` module database, as a `${env:...}` or `${keyvault:...}` reference. A node opens no catalog connection, so this is how it reaches the ledger, the templates and the caches; the login needs rights on schema `osdu` alone. A literal secret is refused at startup. |
| Repository layout | flow repositories | `mappings/` next to the flows (or named under `render.mappings`), and the cache flows (`flowType: cache`) that fill the partition caches the mappings read, committed and synced. The templates the mappings pin and every version of every cache live in the catalog, never in the repository; nothing writes to the repository. |

## First deployment

1. Give the nodes what the flows read and write: the ingestion database the OSDU flow's `source.connection`
   names, read on every payload root a flow declares, write on the work location
   (`source.work`), and read/write on the retrieval locations. A node opens no catalog connection, so it also
   needs the `osdu` module database connection of its own (`SQLFLOW_OSDU_DB`), with rights on schema `osdu`
   alone ([../architecture.md](architecture.md)).
2. Provision the databases: the control plane applies SQLFlow's and the module's migrations on start, or
   `sqlflow db migrate --db <ref>`. The ledger's `osdu` schema comes with it. The ledger reads under snapshot
   isolation, so allow it once on the database that holds the `osdu` schema (Azure SQL Database allows it by
   default): `ALTER DATABASE [<database>] SET ALLOW_SNAPSHOT_ISOLATION ON;`
   ([ledger.md](ledger.md#provisioning)).
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
| `GET /flows/{pipelineId}/stats` | read | Record counts by state, drift, the last 24 hours, the last submission. For a source with several interfaces, the counts of every interface added up (`interfaces` says how many, `flowId` is empty), or one interface's with `?interface=`. |
| `GET /flows/{pipelineId}/interfaces` | read | The flow's interfaces in document order: each one's name, ledger identity (`flowId`) and the name it is derived from (`ledger`), route and why (`route`, `routeReason`), mapping, the kind the mapping fills as the last sync read it, record table, what the document declares it waits for (`after`), its counts, and the order a run takes: its `wave`, `waitsFor` and `notWaitedFor` (each an interface with `origin`, `after` or `schema`, and `why`). When the order cannot be worked out (a mapping the repository's sync did not read, a template the catalog does not hold, interfaces that wait for each other), `orderProblem` says why and the order shown is `after:` alone. A flow in the single form lists one entry with no name. |
| `GET /flows/{pipelineId}/records` | read | Paged, filtered records: `search` (a delivery key, or a prefix over label, source key and OSDU id; `mode=contains` for substring), `status`, `submissionId`, `runId` (the records that run touched, through its attempts), `drifted`. |
| `GET /flows/{pipelineId}/target` | read | Where the flow's records live: endpoint as declared, data partition, protocol, auth type, the path each removal scope calls, and the method the record scope calls its path with (`recordMethod`: `POST`, or `DELETE` for a DDMS's own removal). On the ddms route the paths are those of the collection serving the kind the flow's synced mapping renders, and `ddms` says which collection of which DDMS that is (null on the other routes); a scope the flow cannot route reads `(not routable: ...)`, and one its DDMS refuses (the Well Delivery DDMS's history scope) `(refused: ...)`; neither is offered. |
| `GET /flows/{pipelineId}/submissions` | read | The flow's submissions, newest first. |
| `GET /flows/{pipelineId}/retrievals` | read | A retrieval flow's runs, newest first: window, location, counts, outcome. |
| `GET /records/{flowId}/{key}`, `/attempts`, `/activities` | read | One flow's record, its delivery history, its interventions. A record is addressed by the ledger's flow id and the delivery key together, because the same row read by several flows is one record per flow. |
| `GET /submissions/{id}`, `/attempts` | read | One submission with the runs that carried it, and its attempts. |
| `GET /submissions/{id}/batches` | read | The submission's work batches, paged, filterable by `status`. |
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
| `GET /records/{flowId}/{key}/cache` | read | What one record read out of the cache when it was rendered: the partition, the cached item, the path and the value. |
| `POST /flows/{pipelineId}/release` | operate | Release the flow's blocked records (all, or `keys`). |
| `POST /flows/{pipelineId}/probe` | operate | Queue a target probe on a node; poll `GET /api/v1/compute/tasks/{taskId}`. |
| `POST /records/{flowId}/{key}/release`, `/redeliver`, `/verify` | operate | Release one record; redeliver it (`scope`: `all`, `record`, or `files`, `bulk` or `workflow` as the record's route sends them, a part the route does not send being refused with 400; on the fileAndDdms, manifestAndDdms and workflow routes a part is sent alone, the others staying as OSDU holds them; `metadata` names the record and `payload` every part; `run` true queues a deliver run scoped to the record, which reads it from the ingestion tables by key under its last submission's parameter values, marks it with that scope and sends it); queue a verify run scoped to it. |
| `POST /records/{flowId}/{key}/source` | operate | Queue a read of the record's rows as the ingestion tables hold them now, on a node: the record row with its system columns, its child datasets, and the origin file and row. Nothing is planned or delivered; poll `GET /api/v1/compute/tasks/{taskId}`. |
| `POST /records/{flowId}/{key}/read` | operate | Queue a read-back, on a node, of the OSDU record the flow's record claimed; a record that never queued a document has none to read. |
| `POST /records/{flowId}/{key}/delete` | operate | Queue a removal of one record (`scope`: `record`, `history` or `everything`) on a node. |
| `POST /flows/{pipelineId}/records/remove` | operate | Queue a removal of many records: `scope`, and either `keys` or `filter` (the listing, every match of which goes). `expected` is refused with 409 when the filter no longer resolves to it. |
| `POST /flows/{pipelineId}/records/remove/preview` | read | What that removal would act on: how many records, how many OSDU was ever given, and the target it is aimed at. |
| `POST /ledger/prune` | admin | Age out attempts older than `olderThanDays`, keeping the latest per record. |

A route under `/flows/{pipelineId}` that acts on records (`records`, `target`, `submissions`, `release`, `probe`,
`records/remove` and its preview, and `GET /activities?pipelineId=`) works on one interface of the flow. A flow in the
single form, or a source of one interface, needs no name. For a source of several, the request names the interface
with `?interface=<name>`: without it the answer is 400 (`Interface required`, listing the interfaces), and a name the
flow does not declare is 404 (`No such interface`). A record's own routes need no name, because the ledger identity in
their path is the interface's; the record's answer names its `interface`, and so do a submission's and a target's. A
task a route queues for a node (a probe, a read-back, a source read, a removal) carries the interface, and the node
acts through that interface alone.

A run carries its `operation` (`deliver`, `plan`, `intake`, `drain`, `verify` or `replan`) and the flow's `values` on
the platform's trigger (`POST /api/v1/runs`), with the kind's own arguments in the run payload: `force` (lift the
whole-run gates), `submissionId` (the submission to work on), `recordKeys` (scope the run to named records, at most
1,000), `redeliver` (what a run scoped to `recordKeys` sends again: `all`, the default, `record`, or `files` on the file, dataset, manifest, fileAndDdms and manifestAndDdms routes (and on the workflow route when it declares files), `bulk` on the ddms and composed routes, and `workflow`, a new run of the workflow route's stages; on a route that sends its payload in parts the named part goes alone; `metadata` names the record and `payload` every part, and a part the route does not send fails the run),
`slices` (the key slices a fan-out intake member plans), `interface` (the one interface of a source the run works on)
and `interfaces` (the interfaces a run of a source runs; every interface when left out). A run on a submission, on
records or on slices works on one interface and never takes `interfaces`: a run on records or slices of a source of
several names its `interface`, and a run on a submission works on the interface whose ledger registered it (an
`interface` it names has to be that one). The payload is parsed strictly: an unknown property, a wrong type, or one that does not apply to the
operation is refused, naming it. Reading every row of the scope again is the `replan` operation rather than a payload
flag. The run row records them, the delivery counts are projected onto it when the run completes (a run's own work:
what it planned, sent and held, with the submission's totals across every run under `submission`; a fan-out root
reports the submission its members worked on), and its result (the operation's outcome as JSON) and its fan-out
membership (root, slot, count) are on the run detail.

## Running a source

A run of a flow that declares interfaces ([documents.md](documents.md#a-source-with-interfaces)) runs the interfaces
its payload selects, every one when it selects none, in three steps:

1. **Preflight.** Every selected interface is checked before anything is planned or sent, and every finding is
   reported at once: the ledger answers a read under snapshot isolation; each mapping, its template and the cache
   version it reads load; each route can deliver the kind its mapping renders; the interfaces can be ordered (the
   `after:` of the document and the references their mappings fill, [documents.md](documents.md#order)); each record
   table exists with the shape the interface declares (the columns, the identity primary key, a unique record key);
   each route's service answers with the flow's credentials (probed once per distinct service; unreachable, HTTP 401,
   403 or 5xx is a finding); and, for a run that plans, each mapping's legal tags are valid. A run with findings fails
   with `The preflight of '<source>' found <n> problem(s), so nothing was planned or sent:` and the findings, each
   naming its interface. Fix them and run again.
2. **Waves.** The interfaces run in the waves that order puts them in, up to `reliability.parallelInterfaces` at once.
   The run log says what each interface waits for and why, and which references are not waited for. Each interface
   plans, fans out over member runs and drains exactly as a run of that interface alone does, under its own ledger
   identity and its own failure guard. Every member run the interface fans out to carries the interface in its
   payload, so the node that runs it works on that interface only.
3. **Outcome.** An interface ends `completed`, `stopped` (something failed for it as a whole, or its records'
   failures crossed its `failWhen` rules) or `skipped` (it waits for an interface that did not complete). A stopped
   interface takes only the interfaces waiting for it along; the others carry on. The run succeeds when every
   interface completed, and fails otherwise with a message saying how many completed and which stopped or were
   skipped, and why.

The run's `result` lists the interfaces in document order, each with its ledger identity, route and why, what it
waited for and why (`waitsFor`, `waitReasons`), its wave, its state and reason, when it started and ended, and the outcome a run of it alone returns. The
totals (`planned`, `delivered`, `held`, `failed`, and `rowsLoaded`, which the run row shows) add the interfaces up. The
trace carries `interface.started`, `interface.completed`, `interface.stopped` and `interface.skipped` beside the batch
and record events, and every line an interface logs starts with its name in brackets. The run's activities and the
events of its records name the interface's ledger (`recall/welllogs`).

A failed run is never redone from the start. An interface that completed moved its watermark, so the next run finds
nothing new for it; a stopped interface closed its submission as failed with the records it had not sent still
pending, so its next run sends them before planning anything new. To run only some interfaces, name them:

```bash
sqlflow run flows/recall.yaml --set logSource=STAT_COMP --payload '{"interfaces":["welllogs"]}'
```

A run that selects interfaces does not wait for the ones it leaves out: their records are read from the ledger as they
are. A run with `interface` instead works on that one interface alone and returns what a run of a flow in the single
form returns. A plan, verify or drain of the whole source runs the same way, with that operation for every interface.

The GUI does not name interfaces yet. A flow in the single form, and a source of one interface, are shown as before.
For a source with several interfaces, the Delivery tab shows the counts of all its interfaces added up, and a record's
and a submission's pages work as for any flow. Its Records and Submissions tabs and its removal dialog answer that an
interface has to be named, so read and act on them through the API with `?interface=` and through the CLI. The GUI's
interface views are stage 9 of [../../docs/osdu-coverage-plan.md](../../docs/osdu-coverage-plan.md).

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

The ddms route calls what the DDMS serving the records offers instead, and the target view names each call
([protocols.md](protocols.md#osduwelllog-the-ddms-route)): the Wellbore DDMS, the Well Delivery DDMS and RAFS remove a
record with a `DELETE` of their own. The Production DDMS historian's records are Storage records and go through the
three calls above; the historian has no delete for the points of their series, so those stay in the historian, and
each removal's outcome says so. Seismic Store's dataset records are Storage records too. Seismic Store has no
reversible delete, so the record scope leaves the dataset and its files in place, and the outcome says so. Removing
everything deletes the dataset with its files, then purges the record. On a gc deployment that removal is refused,
since one dataset's delete there removes the files of every dataset in its subproject; the target view reads
`(refused: ...)` for it when the flow declares `provider: gc`, and the removal refuses it whenever the deployment says
it runs there. The Reservoir Management DDMS's records are Storage records as well; the service's own delete purges, so
it is never called. The record scope leaves the rows the service keeps for the record; removing everything deletes
those rows, then purges the record, and the service's copy of the record stays in its database.

The dspdm route writes rows of the Production DDMS core service, not OSDU records. DSPDM keeps no deleted rows and no
versions, so only `everything` applies: it deletes each row for good (`DELETE {root}/delete/{boName}/{id}`), and the
target view reads `(refused: ...)` for the other two scopes.

A removal names its records by key or by filter. The filter form is resolved on the node when the removal runs,
so "every record this run delivered" travels as the filter rather than as tens of thousands of ids, and covers
records no page ever rendered. One removal takes at most 25,000 records; a larger one is several removals.
Records are removed in chunks of 500, batched into a single request where the protocol and the scope allow it
(only the reversible scope has a bulk endpoint), and each record gets its own ledger attempt. A record OSDU has
already lost is reported as already gone, not as a failure, and a record with no OSDU id at all is skipped. A
removal acts on one flow's records and only on the OSDU ids they claimed: a record that never queued a document (one
held at render, or held because another flow owns its OSDU id) is skipped, so a removal in one flow never reaches
another flow's OSDU record ([ledger.md](ledger.md#one-source-several-flows)).

The `record` and `everything` scopes mark the record deleted and blocked here; `history` leaves it delivered,
because OSDU still holds it at the version the ledger knows. The task result carries the counts and up to 200
per-record outcomes (failures first); every record's outcome is in its own attempt regardless.

## The GUI

- **Delivery** (Operate): every delivery flow with delivered versus total, pending, held, failed, drifted, and
  its last submission.
- **A flow's page** (Pipelines): the Delivery tab (stats, probe the target, release blocked),
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
  read from, its counts, the runs that carried it, its work batches, its attempts, and a link to its records.
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
| `sqlflow check <flow.yaml> [--interface <name>] [--connect] [--set name=value]... [--db <ref>] [--json]` | The delivery preflight: the flow, its mappings, its templates and its payload roots. With `--connect` it opens the flow's source connection on this machine and reports the tables, their columns, the key types, the system columns, the current watermark window and the candidate counts. A source is checked one interface at a time, each with its route, its ledger, its wave, what it waits for and why, and the references it does not wait for, after a first line giving the order the interfaces run in; `--interface` checks one. Interfaces that wait for each other in a way nothing cuts fail the check, naming them. With `--json`, a source answers `flow`, `order` and one object per interface (with `wave`, `waitsFor` and `notWaitedFor`). |
| `sqlflow run <flow.yaml> [--operation deliver\|verify\|plan\|intake\|drain\|replan\|retrieve\|refresh] [--set name=value]... [--payload <json>\|@<file>] [--db <ref>]` | A run on the workstation. The payload carries the delivery kind's arguments (`force`, `submissionId`, `recordKeys`, `redeliver`, `interface`, `interfaces`; [The API](#the-api)). A delivery flow's runs need the module database connection: rendering reads the template and the cache version saved there, and the ledger lives there. A retrieval flow runs `retrieve` by default, and runs without one. A cache flow runs `refresh` by default, which merges into the cache of its partition and so needs it. |
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
| A record-scoped run plans nothing: the ingestion tables hold no row for its key | The run's trace names the record table and the key | Look at the pre and ingestion runs that load that table; a run scoped to the record reads it by key once they have loaded it. |
| Records `held` | The Records tab filtered to held | Read the last error. Fix the data (reference miss, empty key) or the mapping; then Release (one record, or all blocked). |
| Records `failed` | The record's History tab | The retry budget is spent; the last error is redacted but specific. Release after fixing the cause. |
| Records stuck `delivering` | `Lease` on the record page in the past | A worker stopped mid-delivery. The flow's next deliver run (the recovered run, a re-run of the submission, or `drain`) waits out the lease, recovers it (applying what the stopped worker had sent) and sends the rest; nothing else to do unless a node is wedged. |
| Records show `delivering` while the run's trace says they were sent | The run's trace: `batch.progress` for the batch | Expected while the batch runs: a worker applies what it sent to the records at each renewal of its lease and when the batch closes, so the records trail the trace by at most one renewal. The attempts are there at once. |
| A run fails: `source.record.primaryKey` names a column that is not an identity column, or not the table's primary key | The error names the table and what the column lacks, with the statement that adds the key | A table SQLFlow created before its ing flow set `target.identityColumn` has no identity key (or a plain `RecId` column the setting added and nothing fills). Stop the table's loads, drop that plain column, run the `ALTER TABLE ... ADD [RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT ... PRIMARY KEY CLUSTERED` from the message (it rewrites the table), and run again ([documents.md](documents.md#the-identity-primary-key)). |
| A flow fails to load: `reliability.fanOut` needs `source.record.primaryKey` | The error names the flow file | Name the record table's identity primary key under `source.record.primaryKey`, or set `fanOut: 0`. |
| A run fails: the database does not allow snapshot isolation | The error names the database and the statement | Run the `ALTER DATABASE ... SET ALLOW_SNAPSHOT_ISOLATION ON` it names, once, and run again. Nothing was claimed. |
| A verify run reports drift | The Records tab with Drifted only | Decide whether the edit in OSDU was legitimate. Redeliver the record, or set `verify.reconcile: true` so verify runs queue redelivery. A newer version that changed only data keys other systems write (the flow's `preserveDataKeys`, or the run state External Data Services writes on a data job after a fetch) is not drift: the verify says so, on every route that writes records through storage or a manifest (all but ddms and fileAndDdms). |
| Records held with `... already holds row N with ..., which this record did not write` | The record's last error names the business object, the row and its key | Look at the row in DSPDM. If the flow is to maintain rows loaded before it, set `target.dspdm.existingRows: update` and Release; otherwise remove the row or correct the key in the source, then Release ([documents.md](documents.md#the-production-ddms-core-service)). |
| Records held with `the row cannot be saved in ...` or `DSPDM refused the row: ...` | The record's last error names each attribute and why | Fix the source rows or the mapping (attribute names, types, lengths, mandatory attributes), then Release. |
| A run on the dspdm route fails its preflight naming a business object | The run's trace | Name the business object and its key under `target.dspdm.businessObjects`, or give the business object a unique constraint in DSPDM's metadata. |
| Records held with `External Data Services could not use this ...` | The record's last error names every rule it breaks | Fix the source rows or the mapping so the registry entry, data job or proxy dataset carries what EDS needs, or say what the deployment needs under `target.eds` (a partition whose jobs fetch no files: `retrieval: false`; a gc build: `build: gc`), then Release ([documents.md](documents.md#external-data-services)). |
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
| A run of a source fails: `The preflight of '<source>' found <n> problem(s), so nothing was planned or sent` | The run's error lists every finding, each naming its interface | Nothing was planned, sent or claimed. Fix each finding (a missing template, a record table without its identity key, a service refusing the credentials, an invalid legal tag) and run again. |
| A run of a source fails: `<n> of <m> interface(s) completed` | The run's result lists each interface's state and reason; the trace has its `interface.stopped` and `interface.skipped` events | What completed is in the ledger. Fix what stopped the interface (the reason quotes the last failure) and run again: the stopped interface sends what it had left, then the interfaces that waited for it run. `--payload '{"interfaces":[...]}'` runs only those. |
| An interface stopped: `an outage: <n> records in a row could not reach the service` | The interface's reason in the run's result; the records' attempts | The service was down or unreachable, or refused the credentials (`were refused by the service`). Nothing was held or failed for it: the records it tried are pending with those tries charged, and the rest are pending untried. Run again once the service answers. |
| An interface stopped: `<n> of the <m> records ... were held or failed ... at or above failWhen.failedPercent` | The Records view of the interface filtered to held and failed | The data or the mapping is wrong for many records at once. Fix it, release the records, and run again. |
| A run of a source fails: `the order of the interfaces: The interfaces a -> b -> a wait for each other` | The finding names the interfaces and the properties that refer across; `sqlflow check` prints the same | Two interfaces of one OSDU group refer to each other's kind (a wellbore to its kick-off wellbore). Add `after:` to the one that should wait. |
| An interface waits for another the document does not name | `sqlflow check`, or the run log's `waits for` lines | Its mapping fills a property that refers to the kind the other delivers. That is intended: the referring records land after the ones they refer to. |
| An API call answers 400 `Interface required` | The message lists the flow's interfaces | The flow is a source of several interfaces: add `?interface=<name>`. |
| A flow fails to load: `the document declares interfaces, so these belong to an interface rather than the source` | The error names each misplaced key | Move `source.record`, `source.datasets`, `source.payloads`, `render.mapping`, `target.protocol` or `target.protocolOptions.payload` under the interface they belong to ([documents.md](documents.md#a-source-with-interfaces)). |
| The sync warns that a ledger is kept by two flows | The warning names the interface and the flow keeping it | An interface adopted the ledger (`ledger:`) of a flow the repository still holds. Remove the old flow, or the adoption; until then both deliver into one ledger. |
| A failure has to be followed into OSDU's own logs | The record's History tab: the attempt's result names its `correlationId`, and a refused request's error quotes `(correlation-id ...)` | Give the OSDU operators that id: every request of the try carried it. |

## Size ceilings

Set `MaxRequestBodySize` on petrodb-api, the ingress limit and the APIM limit to one deliberate number, and
derive the chunk cell limit in the preparing job from it ([design.md](design.md) section 14.3). A 413 holds
the record here rather than creating a duplicate, but the ceiling still needs to be intentional. The control
plane's own ceiling is `ControlPlane:MaxRequestBodyMegabytes`.
