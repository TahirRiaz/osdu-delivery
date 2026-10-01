# Operations

## Deployables

Three hosts in `osdu/hosts`, each composing SQLFlow with the OSDU module: the control plane
(`SqlFlow.Delivery.ControlPlane.Host`, one replica, which schedules, records and answers), the worker nodes
(`SqlFlow.Delivery.Worker.Host`, scaled on the control plane's replica target, which deliver), and the CLI on a
workstation (`SqlFlow.Delivery.Cli.Host`). The GUI is a static image. See [architecture.md](architecture.md) and
[../deploy/README.md](../deploy/README.md). Why the control plane is one replica, and what to do when it is not
running, is [Availability and recovery](#availability-and-recovery).

Images are built by CI on every push and published when the repository names a registry: set the `IMAGE_REGISTRY`
variable (`ghcr.io/<owner>`, `<name>.azurecr.io`, `docker.io/<org>` or a private mirror) and, for anything but
`ghcr.io`, the `REGISTRY_USERNAME` and `REGISTRY_PASSWORD` secrets. Every push to `main` publishes the commit sha and
`latest`; a `v*` tag publishes the version as well, so a deployment can name an exact build and a rollback has
something to name. With no registry named, CI builds and starts the images as before and publishes nothing.

## Configuration

Everything the platform already reads ([environment-variables.md](environment-variables.md)), plus:

| Setting | Where | Purpose |
| --- | --- | --- |
| Flow secrets | nodes, the CLI | Whatever the flows reference: `${env:OSDU_URL}`, `${keyvault:vault/name}`, and so on. A node holds the references its pool's flows need. |
| `SQLFLOW_DELIVERY_ALLOW_LOOPBACK` | nodes, the CLI | `true` lets a flow target `localhost` (local OSDU stubs, tests). Off by default: the URL guard refuses loopback and private targets. |
| `SQLFLOW_DELIVERY_ALLOW_INSECURE_TLS` | nodes, the CLI | `true` lets a flow that declares `reliability.verifyTls: false` run here. Off by default: a repository document cannot take a node off TLS on its own, and a run that asks is refused, naming this variable. Set it only on the nodes that reach a target whose certificate cannot be trusted any other way; trusting the issuing authority on those nodes is the better answer. |
| `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` | nodes, the CLI | The private ranges (CIDR, comma separated) a flow may reach: an OSDU, its storage accounts or a proxy behind a private endpoint. Empty by default: every private address is refused, whether a URL names it or a host name resolves to it. Link-local and cloud metadata addresses are never reachable. |
| `ControlPlane:MaxRequestBodyMegabytes` | control plane | The API's request body ceiling, set on purpose rather than left at Kestrel's default. Default 64. |
| `Osdu:TargetProbe:Enabled` | control plane | Runs the scheduled target probe ([Watching the targets](#watching-the-targets)). Off by default: a pass costs a token exchange and a request against a live OSDU for every interface it covers. The operator's Probe target button is there either way. |
| `Osdu:TargetProbe:IntervalMinutes` | control plane | Minutes between passes. Default 15, and refused below 5, which is the floor the cost of a pass sets. |
| `Osdu:TargetProbe:Pipelines` | control plane | The delivery flows to probe, by pipeline name, comma separated. Empty means every active delivery pipeline; a name no active delivery pipeline carries is named in the pass's log rather than quietly probing nothing. |
| `Osdu:Telemetry:Exporter` | control plane | Where the metrics go: `none` (default), `otlp`, `azuremonitor` or `console` ([Metrics](#metrics)). The meters publish either way; this decides whether the measurements leave the process. |
| `Osdu:Telemetry:OtlpEndpoint` / `:OtlpProtocol` / `:OtlpHeadersRef` | control plane | The collector or backend OTLP reaches (`http://collector:4317` for `grpc`, `http://collector:4318/v1/metrics` for `httpprotobuf`), and the headers a hosted backend takes its API key in, as a `${env:...}` or `${keyvault:...}` reference. Left out, the exporter reads the standard `OTEL_EXPORTER_OTLP_*` variables. |
| `Osdu:Telemetry:AzureMonitorConnectionRef` | control plane | The Azure Monitor connection string, as a reference; required when the exporter is `azuremonitor`. |
| `Osdu:Telemetry:ExportSeconds` / `:ServiceName` / `:ServiceInstanceId` | control plane | How often the metrics are sent (default 60, never under 5), and what a backend groups them under (default `osdu-delivery`, and the machine name). |
| `OSDU_TELEMETRY_EXPORTER` and `OSDU_TELEMETRY_*` | nodes | The same settings for a node, which takes every setting from its environment: `_OTLP_ENDPOINT`, `_OTLP_PROTOCOL`, `_OTLP_HEADERS`, `_AZURE_MONITOR_CONNECTION`, `_EXPORT_SECONDS`, `_SERVICE_NAME`, `_SERVICE_INSTANCE`. A one-shot CLI command exports nothing: it ends before the first export. |
| `Osdu:TargetProbe:SettleSeconds` / `:MaxPerPass` | control plane | How long a pass waits for the probes it queued before moving on (default 60, 0 not to wait; whatever has not come back is recorded by the next pass), and how many interfaces one pass probes across every flow (default 200, the rest on the passes after it once the estate is narrowed). |
| `Osdu:Database:Connection` / `SQLFLOW_OSDU_DB` | every tier | Where the `osdu` schema is (the ledger, the mappings, the templates and the caches), as a `${env:...}` or `${keyvault:...}` reference; the login needs rights on schema `osdu` alone, and a literal secret is refused at startup. Left unset it is the catalog's own database, which is what the shipped deployments do: one metadata database, the two schemas beside each other. An estate that must keep them in two databases names the second here, on the control plane, which migrates and verifies it after the catalog, and on every node. A node opens no catalog connection at all, so a node always needs this, pointed at whichever database holds the schema; without it a node validates and plans but delivers nothing. Which of the two shapes an estate is cannot be changed by editing the setting afterwards, so choose before the first migrate. |
| Repository layout | flow repositories | `mappings/` next to the flows (or named under `render.mappings`), and the cache flows (`flowType: cache`) that fill the partition caches the mappings read, committed and synced. The templates the mappings pin and every version of every cache live in the catalog, never in the repository; nothing writes to the repository. |

## First deployment

1. Give the nodes what the flows read and write: the ingestion database the OSDU flow's `source.connection`
   names, read on every payload root a flow declares, write on the work location
   (`source.work`), and read/write on the retrieval locations. A node opens no catalog connection, so it also
   needs the `osdu` module database connection of its own (`SQLFLOW_OSDU_DB`), with rights on schema `osdu`
   alone ([../architecture.md](architecture.md)).
2. Provision the databases: the control plane applies SQLFlow's migrations to the catalog and the module's to the
   module database on start, or `sqlflow db migrate --db <ref>`. The ledger's `osdu` schema comes with it, and asks
   nothing else of the database. The **source** database a flow reads is allowed snapshot isolation once, so a
   record and its child rows are read as one instant (Azure SQL Database allows it by default):
   `ALTER DATABASE [<database>] SET ALLOW_SNAPSHOT_ISOLATION ON;` A flow that cannot have it declares
   `isolation: readCommitted` ([ledger.md](ledger.md#provisioning)).
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
   that fills the partition the delivery flow delivers to (the `data-partition-id` both declare, `dev` in the
   sample) appears as a pipeline of kind `cache`, and the partition's cache on the OSDU cache page, with no version yet.
5. Capture the cache: run the cache flow with the refresh operation (Refresh now on the OSDU cache page, the Trigger
   run dialog on its pipeline, or `sqlflow run cache/recall-lookups-00-cache.yaml --db <ref>` and
   `sqlflow run cache/recall-reference-00-cache.yaml --db <ref>` on a workstation), then check the delivery flow against
   its template and the cache version it now reads:

   ```bash
   sqlflow check flows/recall-welllog-03-header-delivery.yaml --set logSource=STAT_COMP --db <ref>
   ```

   A cache flow of OSDU types, as the sample's reference flow is, searches the endpoint it declares, with its own
   credentials, and merges what it found into the cache of its partition; a cache flow of lookup tables, as the sample's
   lookups flow is, reads the ingestion tables its pre and ing flows load, so those run first (the sample's
   `recall-cache` schedule runs the three in that order). `--operation plan` counts what each OSDU type's search
   matches before anything is captured. Either writes
   the cache's first version into the catalog, and the newest version is always the current one. Without access to OSDU,
   `sqlflow cache import <cache flow> --from-dir <dir> --db <ref>` merges type files into the partition's cache instead
   (`osdu/samples/cache-records` holds made-up reference data for the sample reference flow's nine types).
   `sqlflow cache list dev --db <ref>` lists the versions. The cache flow's schedule keeps the cache refreshed ahead of
   the deliveries.
6. Load the ingestion tables and plan before anything touches OSDU: run the pre-ingestion flow and then the
   ingestion flow beside the OSDU flow (lineage puts them in that order, so triggering the chain runs them in
   waves), then trigger the OSDU flow with operation `plan` and the flow parameters (the GUI's Trigger run
   dialog, or `sqlflow run flows/recall-welllog-03-header-delivery.yaml --operation plan --set logSource=STAT_COMP`).
7. Deliver the canary log source (a run with operation `deliver`), then the rest. Put the hourly deliver on
   a schedule.

## The API

Every delivery route lives under `/api/v1/delivery` and uses the platform's tokens and scopes.

| Route | Scope | Purpose |
| --- | --- | --- |
| `GET /flows/{pipelineId}/stats` | read | Record counts by state, drift, the last 24 hours, the last submission. For a source with several interfaces, the counts of every interface added up (`interfaces` says how many, `flowId` is empty), or one interface's with `?interface=`. For a flow that names its partitions, one partition's with `?partition=` (`partition` names it), or every partition's added up without it (`partitions` lists them). |
| `GET /flows/{pipelineId}/interfaces` | read | The flow's interfaces in document order: each one's name, ledger identity (`flowId`) and the name it is derived from (`ledger`), route and why (`route`, `routeReason`), mapping, the kind the mapping fills as the last sync read it, record table, what the document declares it waits for (`after`), its counts, and the order a run takes: its `wave`, `waitsFor` and `notWaitedFor` (each an interface with `origin`, `after` or `schema`, and `why`). When the order cannot be worked out (a mapping the repository's sync did not read, a template the catalog does not hold, interfaces that wait for each other), `orderProblem` says why and the order shown is `after:` alone. Each entry also lists the `parameters` the flow declares (`name`, `required`, `default`, `description`, and `scopeColumn`, the record table's column its scope binds the parameter to, or null for one the scope does not read), whose values fill its record scope, and the record table's `keyColumns` in the order a key's parts are named: what the Preview tab asks for. A flow in the single form lists one entry with no name. A flow that names its partitions lists its interfaces in the partition `?partition=` names, or without it in every partition, in the order the flow names them; each entry names its `partition`, and its `flowId` and `ledger` are that partition's (`<ledger>@<partition>` for a partition that does not keep the flow's own ledger). |
| `GET /flows/{pipelineId}/records` | read | Paged, filtered records: `search` (a delivery key, or a prefix over label, source key and OSDU id; `mode=contains` for substring), `status`, `submissionId` (the records the submission last planned), `deliveredBy` (the records it delivered, which stay its own however many submissions touch them afterwards), `runId` (the records that run touched, through its attempts), `drifted`. |
| `GET /records?search=&status=&flowId=` | read | One record from anywhere, across every flow: `search` is a delivery key, or the start of any value the record is known by (an identity the mapping declares, the source key or one of its columns, a word of the label, the OSDU id or its own part, the ingestion file). Paged; each hit carries the values that matched and what each is, and the ingestion file and row the record's newest version came from (the queued version's while work waits). `flowId` narrows it to one flow's ledger identity (one of `GET /records/flows`); without a term it is that flow's recency listing. It seeks `osdu.RecordIdentity` (by `[FlowId, Token]` for one flow), so it answers at production volume and counts no further than its candidate bound. |
| `GET /records/flows` | read | The flows the lookup can be narrowed to: one entry per ledger identity the synced repositories name that holds at least one record (`flowId`), with its `pipelineId`, `flowName` and `interface` (null for the single form), named as a hit names them and ordered by flow, then interface. An interface that has delivered nothing yet, and a ledger no synced pipeline holds, are left out. Whether an identity holds a record is one index seek each, whatever the ledger's size. |
| `GET /flows/{pipelineId}/target` | read | Where the flow's records live: endpoint as declared, data partition, protocol, auth type, the path each removal scope calls, and the method the record scope calls its path with (`recordMethod`: `POST`, or `DELETE` for a DDMS's own removal). On the ddms route the paths are those of the collection serving the kind the flow's synced mapping renders, and `ddms` says which collection of which DDMS that is (null on the other routes); a scope the flow cannot route reads `(not routable: ...)`, and one its DDMS refuses (the Well Delivery DDMS's history scope) `(refused: ...)`; neither is offered. |
| `GET /flows/{pipelineId}/submissions` | read | The flow's submissions, newest first. |
| `GET /flows/{pipelineId}/retrievals` | read | A retrieval flow's runs, newest first: window, location, counts, outcome. |
| `GET /assertions` | read | The board of every active assertion flow ([docs/assertions-design.md](../../docs/assertions-design.md) section 8), in the partition `?partition=` names, else the workbench's (`X-Osdu-Partition`): per flow its partitions and whether it tests this one (`testsPartition`), the `parameters` a run takes, `failRunOn`, its last run, and every test with its declaration, the template version it is checked against and the `problems` that keep it from being evaluated, its latest result that was not skipped, its last 12 outcomes (`history`, newest first) and whether it `changed` since that result; and the `totals` over every test that runs in the partition, with the pass rate of those evaluated. A flow whose document does not parse, or that does not test the partition, says why in `problem`. |
| `GET /flows/{pipelineId}/assertions` | read | One assertion flow's board, as above. 409 for a pipeline that is not an assertion flow. |
| `GET /flows/{pipelineId}/assertion-runs?max=` | read | The flow's runs in the partition, newest first (50 by default, at most 500): status, counts, what each was asked to run (`selection`), the platform run, the actor, when, and why it failed. |
| `GET /flows/{pipelineId}/assertion-matrix?runs=` | read | The flow's recent runs (30 by default, at most 200) against its tests: every declared test (and any a run recorded that the document no longer declares) with its outcome in each run, null where a run did not run it. |
| `GET /flows/{pipelineId}/assertions/{test}/history?runs=` | read | One test's whole results over its recent runs, newest first: what every assertion expected and found, and the records that failed it. 400 for a name that is not a test name. |
| `GET /assertion-runs/{assertionRunId}` | read | One run with every test's whole result, and the pipeline it belongs to. |
| `GET /assertion-runs/{assertionRunId}/report?format=` | read | The run's report as a file: `json`, `md`, `html` (a page with no script, which prints) or `junit` (a suite per kind, a case per test). 400 for another format. |
| `GET /dimensions` | read | The dimensions of every active dimension flow ([dimension-plan.md](dimension-plan.md)) in the partition `?partition=` names, else the workbench's: per flow its partitions and whether it builds in this one (`buildsPartition`), its ledger, the `parameters` a run takes, and each dimension it declares (and each its ledger keeps that it no longer declares) with its declaration, how the index stores its field, what it holds, the build that wrote that (`current`), the newest build when that is another one (`latest`: failed, cancelled or running), and whether its declaration `changed` since; and the `totals`. A flow whose document does not parse, or that does not build in the partition, says why in `problem`. |
| `GET /flows/{pipelineId}/dimensions` | read | One dimension flow's dimensions, as above. 409 for a pipeline that is not a dimension flow. |
| `GET /dimensions/{dimensionId}` | read | One dimension, with the pipeline that declares it and the parameters a build takes. |
| `GET /dimensions/{dimensionId}/values?search=&order=&removed=&attr=&after=&limit=` | read | A page of its values (100 by default, at most 1,000) in value order or with the most records first (`order=records`), found by the value or any key (`search`, ignoring case) and narrowed by attributes (`attr=Country:Norway`, repeated: any value of one attribute, all attributes, held by one key), each with its records, its search filter, the keys most records hold (`top`, each with its label) and the values its keys' attributes hold. `next` is the cursor of the next page, handed back as `after`. |
| `GET /dimensions/{dimensionId}/values/{valueId}` | read | One value: its keys, the most records first, each with its label and filter; the value's filter, or why none can be written; and the changes that brought keys to it or took them away. |
| `GET /dimensions/{dimensionId}/keys?search=&value=&leftOut=&removed=&attr=&order=&after=&limit=` | read | A page of its keys, exactly as the index holds them: every one, one value's, or those of no value (`leftOut=true`), in arrival order or with the most records first (`order=count`), found by the key or its label and narrowed by attributes (`attr`, as above); each with its `label` and the record it was read from (`labelFrom`), its value, its count, the search `filter` finding exactly its records, and its `attributes`. |
| `GET /dimensions/{dimensionId}/attributes/{name}?search=&limit=` | read | The values an attribute holds among its keys, the most records first, each with its keys and records: the list a drop-down of the attribute reads. 404 for an attribute the dimension does not read. |
| `GET /dimensions/{dimensionId}/builds?max=` | read | Its builds, newest first: what each found, how it read the index and the labels (`labelled`, `unlabelled`, `labelQueries`), how complete it is, what it changed, its notes. |
| `GET /dimensions/{dimensionId}/changes?build=&key=&value=&change=&before=&limit=` | read | A page of its change log, newest first, narrowed to a build, a key, a value or one kind of change (`added`, `removed`, `moved`, `restored`). |
| `POST /dimensions/{dimensionId}/filter` | read | The search filter of the values `{ "valueIds": [...], "values": [...] }` names (at most 1,000, holding at most 50,000 keys): the kind, the filter queries (500 keys each) and each joined with the dimension's query (`searches`), and what it leaves out: keys no query can carry, values no build finds any more, names that are no value. Reads only. |
| `POST /dimensions/search` | read | The OSDU search finding the records that hold one of the values picked in each dimension, `{ "picks": [{ "dimensionId", "valueIds", "values", "attributes": [{ "name", "values" }] }], "kind", "within" }` (a dimension picked by attributes compares the keys holding them, those of its values picked when there are some): OR within a dimension, AND across them, each dimension's own query once, `within` added. The dimensions have to read one kind, or `kind`, which each dimension's kind has to cover; at most 20 dimensions and 1,000 clauses. It answers the `kind`, the `query`, the `request` body for `POST /api/search/v2/query`, each dimension's part (the values it holds now, the keys compared, its filter), the clauses, and what the picks left out (`missing`, `removed`, `notes`). Reads only; nothing is sent to OSDU. |
| `GET /dimensions/{dimensionId}/table?search=&attr=&order=&dir=&offset=&limit=` | read | A page of the dimension's own table, `osdu.dim_<dimension>` ([dimension-plan.md](dimension-plan.md), The table): its name, its attribute columns, and the rows (100 by default, at most 1,000), each with its `id`, `keyId`, key, value, attributes in column order, records and filter; `total` on a first page, and on any page that is searched or narrowed. `search` finds text anywhere in the key, the value or an attribute, ignoring case; `attr=Name:value` (repeatable) keeps the rows holding the value exactly; `order` is `value` (the default), `key`, `records`, `id` or an attribute's name, `dir` `asc` or `desc`. A dimension built before dimensions had a table has it made by the first read. 400 for a column the table does not have. |
| `GET /dimensions/{dimensionId}/export?set=values\|keys\|table&format=csv\|jsonl` | read | The whole dimension as a file, written a page at a time: its values or keys, every row with its search filter, or its table, row for row as the database holds it. CSV (RFC 4180; a cell a spreadsheet would run as a formula starts with an apostrophe) or JSON Lines (every value exactly). |
| `GET /runs/{runId}/dimension-builds` | read | The builds a platform run made, each with the dimension it built. |
| `GET /records/{flowId}/{key}`, `/attempts`, `/activities` | read | One flow's record, its delivery history, its interventions. A record is addressed by the ledger's flow id and the delivery key together, because the same row read by several flows is one record per flow. |
| `GET /records/{flowId}/{key}/chain` | read | The record's row through its ingestion table: the table (`sourceTable`), when the row first arrived (`insertedUtc`), and every change of it the ledger recorded, newest first: `loaded` (the insert), `reloaded` (inserted again after earlier versions), `earliest` (the earliest version held, when the arrival is not known), `changed` and `deleted`, each with its file and row, the ingestion run that was writing the table when the row was stamped (`loading`), and the last landing of its file before that run (`landing`). A run that reloaded the row unchanged made no change and is not listed; a run the catalog does not prove is left out rather than guessed, and `note` says what could not be named. At most 50 changes, the newest and the arrival (`truncated`). |
| `GET /submissions/{id}`, `/attempts` | read | One submission with the runs that carried it, and its attempts. |
| `GET /submissions/{id}/batches` | read | The submission's work batches, paged, filterable by `status`. |
| `GET /activities`, `GET /activities/{id}` | read | The audit trail, filtered by flow, kind, actor, outcome, time and `idle` (`false` leaves out the runs that changed nothing, `true` lists only them); one activity with its captured log. |
| `GET /mappings`, `/mappings/{id}` | read | The mapping documents the repositories hold. |
| `GET /mappings/{id}/flows` | read | The interfaces of the repository's delivery flows that render with the mapping, as the last sync described them, in every partition: each one's pipeline, flow, interface, partition, ledger identity, record table and route. What a value check of the mapping reads the rows of ([Checking a mapping's values](#checking-a-mappings-values)). An interface the repository no longer declares, or whose pipeline the catalog does not hold as an active delivery flow, is left out. |
| `GET /templates` | read | The saved template versions, by kind and newest first, each with where it came from, who saved it and how many synced mappings pin it ([mapping-templates.md](mapping-templates.md)). |
| `GET /templates/detail`, `GET /templates/schema` | read | One saved version (`kind`, `version`): laid out variable by variable (type, shape, requiredness, who writes it, relationships, unit context and OSDU's description; with `scope`, the types of that partition's cache each variable can be read from), or the bundled schema itself. |
| `POST /templates/preview` | read | A bundled schema (`kind`, `schema`, optional `scope` and `release`) laid out the same way without saving it, with the saved version when it is already saved. |
| `GET /templates/osdu/releases` | read | The releases of the OSDU data definitions (the Open Group's public schema repository), newest first: each tag, the commit it names, its schema folder on the web, and whether it is in the control plane's local copy (`local`); `syncedUtc` is when the list was last read from the repository. 502 when the repository cannot be read and no list is on disk. |
| `POST /templates/osdu/sync` | operate | Reads the release list again from the repository and downloads `release` (the newest when omitted) into the local copy when it is not on disk: the list, when it was read, and the releases downloaded. |
| `GET /templates/osdu/schemas` | read | Every record kind a release publishes (`release`, the newest when omitted): kind, entity type, version, status, and its file with a link to it, which is the file that declares the kind wherever the release keeps it. A kind the release publishes no record schema for (an abstract building block, the manifest, a content schema) is not in the list: no template can be laid out from it. 404 for a release the repository does not have. |
| `GET /templates/osdu/compare` | read | Two versions of one kind (`fromRelease`, `fromKind`, `toRelease`, `toKind`; a release left out is the newest) compared: whether the published files are the same or differ only in their version identifiers, whether they save as the same template, the counts of breaking, additive and wording changes, every variable that differs with each field's value before and after, both files as published, and every shared schema file they refer to that differs (paired by name across versions, with its text and link on each side) with how many are the same. 400 for two different kinds. |
| `GET /templates/osdu/schema` | read | One kind's schema from a release (`kind`, `release`), bundled with every file it refers to, read at the release's commit: the template version it saves as, and the `origin` a save records. Nothing is saved; 404 for a kind the release does not publish. |
| `POST /templates` | author | Saves a bundled schema (`kind`, `schema`, `origin`) as a template version: `outcome` is `created`, or `unchanged` for a version already saved. |
| `DELETE /templates` | author | Deletes a version (`kind`, `version`); 409 while a synced mapping pins it. |
| `GET /mapping-builder/repos` | read | The repositories a mapping can be written for: the source a proposal is opened against, and the delivery flows with their endpoint, the parameters a run of each renders with (its own `render.parameters` and the kind's references for the ones it leaves out, resolved as a run resolves them, with `parameterReferences` naming the reference each value is read from and a value left out when the control plane cannot resolve it) and the partition whose cache they read (`cacheScope`). A flow that names its partitions is offered once per partition (`partition`), each with that partition's values. |
| `GET /mapping-builder/caches` | read | Every partition whose cache a synced cache flow fills: the partition (`scope`), the cache flows filling it, its current version and its cached types: what the builder's Cache picker offers. |
| `POST /mapping-builder/draft` | read | A new mapping (`scope`, `kind`, `version`, `name`, `mappingVersion`, `system`) for a saved template: the four access and legal entries to fill, and a cache entry for every variable outside a repeater that points to an entity type the cache of that partition holds. |
| `POST /mapping-builder/compose` | read | A draft written as YAML and checked: what is still missing, whether it loads, and the preflight against its template and the current version of the cache of `scope`, with the `parameters` given. |
| `POST /mapping-builder/parse` | read | A mapping document (`yaml`) as a draft the builder edits. |
| `POST /mapping-builder/shape` | read | The shape of the records a mapping document (`yaml`, optional `path` and `parameters`) renders, drawn against its saved template without a row or a cache: the record with a placeholder naming the type and the source wherever a value comes from a row or the cache, the parameters the mapping declares with the value used, notes on what the placeholders cannot say, and the issue that stopped it when the document does not load or its template is not saved ([mapping-templates.md](mapping-templates.md)). |
| `POST /mapping-builder/coverage` | read | What a mapping document (`yaml`, optional `path`) fills of the template version it pins: the kind and version, every variable a mapping may fill with whether the document reaches it on every row, on some rows or never, and whether an entry fills the variable itself or something it holds; plus what the mapping leaves required and empty, as the errors the delivery gate raises and warnings for the required variables the gate does not check ([mapping-templates.md](mapping-templates.md)). Nothing is rendered and no cache is read. |
| `GET /caches` | read | One entry per partition whose cache the synced cache flows fill, narrowed by `repoId` to the partitions that repository's cache flows fill: the partition (`scope`); the `flows` filling it, each with its name, repository, `relativePath`, `pipelineId`, the `endpoint` it searches as declared, the schedules that refresh it, the types it declares and, for a flow that names its partitions, the `partitions` it builds; the `types` the cache holds as those flows together declare them, each with its entity type, its `sources` (each flow's kind, query and `onChange`), the `fields` it keeps (the path, the name it is cached as, and the flows declaring it), the `onChange` in effect (`approve` when any flow asks for it) and how many records the current version holds of it; the `current` version with the cache flow that wrote it, who asked and in which run; and how many `versions` the cache holds. |
| `GET /cache/items` | read | The cached records of one partition's cache (`scope`) at one `version` (the current one when none is named), paged, filtered by `type` and searched with `search` over every value they hold. |
| `GET /cache/versions` | read | The versions of one partition's cache (`scope`), newest first, each with whether it is current, the cache flow that wrote it, when it was captured, by whom and in which run, where its content came from, its types and record counts, and the partition's `systemProperties` the capture found (each a `service`, `name`, `state` of `Enabled`, `Disabled` or `Unknown`, the `source` the service took it from and the `detail` saying why it is unknown), which are settings of the platform rather than cached records ([documents.md](documents.md#cache-flow)). |
| `GET /cache/history` | read | The versions of one partition's cache (`scope`), newest first, each with the version captured before it and how many records it changed, added and removed; `type` narrows the counts to one cached type. |
| `GET /cache/diff` | read | What changed in one partition's cache (`scope`) between two versions: `from` (required) and `to` (the current version when omitted), counts per type, and a page of the records that changed, were added or were removed, with the captured values on each side. Narrowed by `type`, `search`, and `change` (the items only). 404 for a version the cache does not hold. |
| `GET /cache/tags` | read | The cache changes delivered records were built from, paged, by `status` (pending, approved, rolling, rejected, applied; `rolling` includes the changes whose records are all marked and wait for their flows, reported as `delivering`), each naming the partition whose cache the refresh found it in, with what it reaches, how many records are marked (`processed`), and how many of which flows are still built from the old value (`waiting`, `waitingFlows`); `scope` narrows the list to one partition. |
| `POST /cache/tags/decide` | operate | Approves or rejects changes (`tagIds`, `approve`). Approving hands the change to the batched rollout; rejecting leaves OSDU as it is. |
| `GET /cache/gaps` | read | What delivered records of the partition `scope` names were built without, most records first, paged (`page`, `pageSize`) with the count of every gap: each value no cached record answered to (`unlisted`, a wellbore the cache does not hold yet), each key a `$findAll` found no row under in any form it was asked for (`listed`, a field no access group lists; a reference with and without its version separator is one key), and each id written without its record (`unverified`), with how many records were built so; `type` narrows to one cached type, and `empty=true` adds the paths read that held nothing (a wellbore without a field). Every gap is filled by the refresh that brings what is missing, which tags the records and redelivers them. |
| `GET /records/{flowId}/{key}/cache` | read | What one record read out of the cache when it was rendered: the partition, the cached item, the path and the value. |
| `POST /flows/{pipelineId}/release` | operate | Release the flow's blocked records (all, or `keys`). |
| `POST /flows/{pipelineId}/probe` | operate | Queue a target probe on a node; poll `GET /api/v1/compute/tasks/{taskId}`. |
| `POST /flows/{pipelineId}/sync` | operate | Queue a timeline sync of the interface's records ([Syncing the ledger with the source](#syncing-the-ledger-with-the-source)): the ones `keys` names, every one `filter` matches (resolved when queued, at most 1,000, the most a run names; refused with 409 when it no longer matches `expected`), or, with neither, every record. Answers the run to follow. Nothing reaches OSDU. |
| `POST /records/{flowId}/{key}/sync` | operate | Queue a timeline sync of one record: a run that reads its row by its stored key and consolidates the ledger with it. |
| `POST /records/{flowId}/{key}/release`, `/redeliver`, `/verify` | operate | Release one record; redeliver it (`scope`: `all`, `record`, or `files`, `bulk` or `workflow` as the record's route sends them, a part the route does not send being refused with 400; on the fileAndDdms, manifestAndDdms and workflow routes a part is sent alone, the others staying as OSDU holds them; `metadata` names the record and `payload` every part; `run` true queues a deliver run scoped to the record, which reads it from the ingestion tables by key under its last submission's parameter values, marks it with that scope and sends it); queue a verify run scoped to it. |
| `POST /records/{flowId}/{key}/source` | operate | Queue a read of the record's rows as the ingestion tables hold them now, on a node: the record row with its system columns, its child datasets, and the origin file and row. Nothing is planned or delivered; poll `GET /api/v1/compute/tasks/{taskId}`. |
| `POST /records/{flowId}/{key}/read` | operate | Queue a read-back, on a node, of the OSDU record the flow's record claimed; a record that never queued a document has none to read. |
| `POST /flows/{pipelineId}/preview` | operate | Queue a preview of one record on a node ([Previewing a record](#previewing-a-record)): `key` names it (a delivery key, an OSDU id the ledger holds, a source key as the Records page shows it, or a JSON array of the key's parts), or is left out for the first record of the scope; `values` fill the flow's parameters, the declared defaults filling the rest. The record is rendered as a delivery would render it and nothing is sent or written. An undeclared parameter, a required one without a value, a key with a control character or over 4,000 characters, and values over 4,000 characters as JSON are refused with 400 before anything is queued. Poll `GET /api/v1/compute/tasks/{taskId}`: a key that names no row is an answer (`found` false with the `reason`), not a failure. |
| `POST /flows/{pipelineId}/scope-values` | operate | Queue a read of the values each parameter of the interface's scope can take, on a node: for every parameter `source.record.scope` binds to a column of the record table, the distinct values that column holds in rows not marked deleted, the most rows first with each one's row count, up to 500 (`more` when it holds more). It needs no parameter value, since it is what a value is picked from, reads with the flow's own connection whatever the source and its columns, and writes nothing. What the Preview tab, a mapping's value check and the trigger dialog offer for a scope's value. Poll `GET /api/v1/compute/tasks/{taskId}`. |
| `POST /flows/{pipelineId}/check-values` | operate | Queue a value check of one interface's rows on a node ([Checking a mapping's values](#checking-a-mappings-values)): `targets` names the attributes to check as template paths (none checks every attribute; at most 200), `values` fill the flow's parameters, `maxRows` is how many rows to read (10,000 when left out, 0 for the whole scope), `samples` how many example records each finding names (20 when left out, at most 500) and `skipSamples` how many it passes over first, and `mapping` the mapping it is asked of (`Name@version`), which the flow must render with. Nothing is sent or written. A target that is not a template path, an undeclared parameter, a required one without a value, a negative count, and arguments over 4,000 characters as JSON are refused with 400 before anything is queued; a target no entry of the mapping reaches fails the task, naming it. Poll `GET /api/v1/compute/tasks/{taskId}`. |
| `POST /records/{flowId}/{key}/preview` | operate | The same preview for one record of the ledger, read in the scope it was last planned under: what the record renders to now, which its page compares with what OSDU holds. |
| `POST /flows/{pipelineId}/osdu/read` | operate | Queue a read of any OSDU record by `targetId`, on a node, through the flow's route and credentials: a record a document refers to, which the ledger may never have delivered. A version or the trailing colon of a reference is dropped, and the record is read at its latest version. An id that is not `partition:group--Entity:unique` is refused with 400. Nothing is written. |
| `POST /records/{flowId}/{key}/delete` | operate | Queue a removal of one record (`scope`: `record`, `history` or `everything`) on a node. |
| `POST /flows/{pipelineId}/records/remove` | operate | Queue a removal of many records: `scope`, and either `keys` or `filter` (the listing, every match of which goes). `expected` is refused with 409 when the filter no longer resolves to it. |
| `POST /flows/{pipelineId}/records/remove/preview` | read | What that removal would act on: how many records, how many OSDU was ever given, and the target it is aimed at. |
| `DELETE /dimensions/{dimensionId}` | admin | Removes a dimension its flow no longer declares, and everything kept of it in its partition (its builds, values, keys, attribute values, collected texts, change log and its rows in its own table, which is dropped when no partition is left writing it), for good, and nothing of any other dimension or partition. Refused with 409 for a dimension the flow declares, for one whose flow cannot be read now (whether it still declares it cannot be told), and for one a cache flow of its partition captures, naming it. Rows go a batch at a time under the dimension's write lock, its own row last, so a removal that stops part way is finished by asking again. Recorded as a `remove-dimension` activity of the flow under the admin who asked. Answers what went, table by table. |
| `POST /ledger/prune` | admin | The ledger's retention pass at one cut-off (`olderThanDays`): ages out attempts older than it, keeping the latest of every record, clears the captured run log of the activities older than it that have finished, and removes whole the assertion runs older than it whose every result a later one superseded. No row of the audit trail is deleted. Answers what it took ([Retention and backup](#retention-and-backup)). |

A route under `/flows/{pipelineId}` that acts on records (`records`, `target`, `submissions`, `release`, `probe`,
`preview`, `osdu/read`, `records/remove` and its preview, and `GET /activities?pipelineId=`) works on one interface of the flow. A flow in the
single form, or a source of one interface, needs no name. For a source of several, the request names the interface
with `?interface=<name>`: without it the answer is 400 (`Interface required`, listing the interfaces), and a name the
flow does not declare is 404 (`No such interface`). A record's own routes need no name, because the ledger identity in
their path is the interface's; the record's answer names its `interface`, and so do a submission's and a target's. A
task a route queues for a node (a probe, a read-back, a read by id, a source read, a preview, a removal) carries the
interface, and the node acts through that interface alone.

A flow that names its partitions ([documents.md](documents.md#partitions)) keeps a ledger per partition as well, so the
same routes work in one partition of it: the request names it with `?partition=<name>`. Without it the answer is 400
(`Partition required`, listing the partitions) for a flow that names several; a flow that names one runs in it without
being told; and a partition the flow does not name is 400 (`No such partition`). The interface listing and the counts
are the exceptions: without a partition they cover every partition. A record's own routes need no partition, because the
ledger identity in their path is that partition's; the record's answer names its `partition`, and so do a submission's,
a hit of the record lookup and a choice of `GET /records/flows`. A task a route queues for a node carries the partition,
and the node acts in that partition alone, with its own configuration.

A run carries its `operation` (`deliver`, `plan`, `intake`, `drain`, `verify`, `replan` or `sync`) and the flow's `values` on
the platform's trigger (`POST /api/v1/runs`), with the kind's own arguments in the run payload: `force` (lift the
whole-run gates so the run looks at every record; what did not change is still not sent), `submissionId` (the submission to work on), `recordKeys` (scope the run to named records, at most
1,000), `redeliver` (what the run sends again, changed or not, of the records `recordKeys` names or, without it, of every record the flow has delivered, at most 5,000 a run with the flow's next runs sending the rest; not with `submissionId`: `all`, the default for named records, `record`, or `files` on the file, dataset, manifest, fileAndDdms and manifestAndDdms routes (and on the workflow route when it declares files), `bulk` on the ddms and composed routes, and `workflow`, a new run of the workflow route's stages; on a route that sends its payload in parts the named part goes alone; `metadata` names the record and `payload` every part, and a part the route does not send fails the run),
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

An assertion flow's run carries the operation `test` (the default) or `plan`, the flow's `values` (its parameters, and
`partition`), and in the payload the tests it runs: `tests` (names) and `tags` (the tests carrying any of them), every
test when both are left out. A name that is not a test of the flow, or a tag no test carries, fails the run naming it,
and a delivery flow's payload keys are refused. Its result names the report (`assertionRunId`), the counts, and the tests
that did not pass.

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
events of its records name the interface's ledger (`wells/welllogs`).

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

The etp route writes Energistics objects into dataspaces of the Reservoir DDMS, not OSDU records. The store keeps no
deleted objects and no earlier versions, so only `everything` applies: it deletes the object from its dataspace
(`DeleteDataObjects`), and the target view reads `(refused: ...)` for the other two scopes. The route never deletes a
dataspace, whatever the scope: the server purges the dataspace's own OSDU record when it does, which this project's
cleanup rule forbids. A dataspace that is to go is an operator's decision, taken in the Reservoir DDMS.

A removal names its records by key or by filter. The filter form is resolved on the node when the removal runs,
so "every record this run delivered" travels as the filter rather than as tens of thousands of ids, and covers
records no page ever rendered. The filter has two ways of naming a submission's records, because they are two
sets: `submissionId` is the records the submission last planned, which a later submission moves on (a record it
finds unchanged becomes that submission's), and `deliveredBy` is the records the submission delivered, resolved
through the delivered attempts it wrote, which stay its own however many submissions touch them afterwards.
"Remove the batch we ran" is a `deliveredBy` removal. One removal takes at most 25,000 records; a larger one is
several removals.
Records are removed in chunks of 500, batched into a single request where the protocol and the scope allow it
(only the reversible scope has a bulk endpoint), and each record gets its own ledger attempt. A record OSDU has
already lost is reported as already gone, not as a failure, and a record with no OSDU id at all is skipped. A
removal acts on one flow's records and only on the OSDU ids they claimed: a record that never queued a document (one
held at render, or held because another flow owns its OSDU id) is skipped, so a removal in one flow never reaches
another flow's OSDU record ([ledger.md](ledger.md#one-source-several-flows)).

The `record` and `everything` scopes mark the record deleted and blocked here; `history` leaves it delivered,
because OSDU still holds it at the version the ledger knows. The task result carries the counts and up to 200
per-record outcomes (failures first); every record's outcome is in its own attempt regardless.

## Syncing the ledger with the source

A timeline sync consolidates the ledger with the ingestion tables, for one record (**Sync timeline** on its page), for
the records ticked or filtered on a flow's Records tab, or for every record of an interface (**Sync timelines**). It is a `sync` run of the flow on a
node, recorded as an activity with who asked for it, and it renders nothing and sends nothing to OSDU. Each record's
row is read by the key tuple the ledger stored, in the scope its last submission ran with, a page of 1,000 records at a
time, and compared with what the ledger holds:

| What the sync finds | What it does |
| --- | --- |
| The row's arrival (`InsertedDate_DW`) is missing from the ledger, or differs | Writes it to the record. It is a fact of the row, so no decision is involved. |
| The row's fingerprint moved past both versions the ledger holds, and no plan saw it | Asks for the record to be planned (`PlanRequestedUtc`): the flow's next run reads it by key, records the change and delivers it if it renders differently. |
| The ingestion table marked the row deleted, and the ledger does not know | Asks for it to be planned the same way; the next run holds it as a deleted row. |
| The row is gone from the table, or outside the record's scope | Puts a `skipped` attempt, phase `source-missing`, on the record's history, once. The record keeps its status: removing it from OSDU stays a removal someone asks for. |
| A record held because its row was deleted, whose row is no longer deleted | Counts it, for an operator to release. |
| Everything agrees | Nothing. |

The run's result counts each of these, with the source keys of the first rows it did not find. A second sync of the
same rows writes nothing new. Rows the ledger has no record of are not a sync's to find: a row's delivery key comes
from its mapping, so a plan finds them (`plan` to see them, the flow's next run or `replan` to deliver them).

## Previewing a record

A preview answers "what would this flow send for this record?" before anything is sent. It renders one record on a
node exactly as a delivery renders it: the same planner, the mapping the flow pins, the template it pins and the
partition's current cache version, and the platform's search asked only what the mapping's searches ask a run. Nothing
is written anywhere: not OSDU, not the ledger, not the work location. It is not a run, so it is not in the run history;
it is a node task, like a read-back, because only a node holds the flow's connection to its ingestion tables.

- **Which record.** The first record of the scope in key order, or the one a key names. A key is what an operator
  holds: a source key as the Records page shows it (`recall:NORWAY_WELLDB/12359/1`, or just `NORWAY_WELLDB/12359/1`),
  its parts as `a | b` or as a JSON array (`["NORWAY_WELLDB", "12359/1"]`), a delivery key, or an OSDU id the ledger
  holds. A part may hold a slash itself (a Recall log id does): every way the text splits into the key's parts is tried,
  the ledger first, then the table, and a text that reads as two different rows asks for the JSON form. The first
  record passes over rows that cannot render (marked deleted, a key part empty, held by the source) and names them, up
  to 1,000; a key names its row whatever it holds. The flow's parameters fill the scope, the declared defaults filling
  what is not given.
- **What it answers.** What the next run would do with the record against the ledger (create, update, skip as
  unchanged, hold, blocked) and why, and whether the document rendered now is the one the ledger holds. The document
  itself is rendered as a first delivery would render it, so a record a run would skip as unchanged still shows its
  document. Where the route adds to the document, the preview shows it as the route sends it: on the file, manifest and
  composed routes `data.Datasets` lists a placeholder for each dataset id the File service mints when a file is
  registered (`<dataset id the File service returns for chunk_0.parquet>:`), and on the dataset route the id derived
  from the record's own. The requests the route makes follow in order, with the paths the flow's options give, and the
  body of a file registration as it is built from the record. Then the files the record's payload would upload, with
  each parquet file's rows and columns read from its footer (the first ten), the records the document refers to with
  the ledger's record of each, the searches the render made, the preflight's warnings, and the record's rows as the
  ingestion tables hold them.
- **Bounds.** Child rows are shown to 100 per dataset and values to 4,000 characters; payload files to 50 per part; a
  document over 2,000,000 characters is described by its size and hash and left out. A node's answer stays under the
  8,000,000 characters a task result holds, leaving out the child rows, then the document, then the record row if it
  must, and saying so. `sqlflow preview --out` writes a preview whole.
- **What it does not do.** It sends nothing, so what a DDMS would answer, and the values it keeps on the record (a bulk
  data link), are not in it; the route's steps say where those come from.

A record's page compares the other way round: its **Render** tab renders the record afresh from its current source
row and reads what OSDU holds, both on a node, and shows the two side by side with OSDU's own fields (`version`,
`createUser`, `createTime`, `modifyUser`, `modifyTime`) set aside, keys in order, and a placeholder named as a value
the platform gives rather than as a change. The ledger keeps what it sent as a hash, not as the document, so the
comparison is with what the record renders to now.

## Checking a mapping's values

A preview shows one record. A value check answers the question across a flow's rows: "which rows will not give this
attribute the value its template expects, and why?" It renders every row it reads on a node exactly as a delivery renders
it (the render's own assembly, entry by entry, over the flow's ingestion tables, the partition's current cache version
and the platform's search, asked in rounds as a run asks), and then holds every value written to what the template says
of its attribute. Nothing is written anywhere: not OSDU, not the ledger, not the work location. Like a preview it is a
node task, not a run.

- **What each row comes to, for each attribute.** Every computation a mapping can make is covered, because the check is
  the render: a column, an expression, a static value, a cache lookup (`$cache` with its `findBy` lines and a
  `$findAll`), a platform search, a `$coalesce` and its alternatives, a list of values, a repeated array's items, and
  every modifier (`trim`, `split`, `replace` from pairs or from the cache, `equals`, `date`, `number`, `id`, `ref`). An
  attribute is:
  - **held** when no value could be produced where one is needed, and the record is not delivered: a required column
    that is empty, a date that is not a date, no cache record or search answer for a value, a reference to a record the
    partition does not hold, several records answering one value, a data property the schema requires left empty. The
    reason is the render's own hold reason.
  - **invalid** when a value is written that breaks a rule of the template, and the record is still sent: its type,
    `format` (RFC 3339 `date-time`, `date` and `time`, `uri`, `email`, `uuid`, an integer's `int32` or `int64` range),
    `pattern`, `enum` and `const`, `minLength` and `maxLength`, `minimum`, `maximum` and their exclusive forms,
    `multipleOf`, a list's `minItems`, `maxItems` and `uniqueItems`, the properties an object written whole requires
    or does not allow, a value matching none of a `oneOf` or `anyOf`, and the entity types an `x-osdu-relationship`
    allows a reference to point to. A value written whole (an object from a cached field, a static list) is checked all
    the way down, and a problem inside it is reported at the property it is at
    (`osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID`).
  - **empty** when an optional entry gives no value, and the record is sent without the attribute. The reason says why
    it gave none, as a required entry would have held for it: `dataset.log_run is empty`, `no UnitOfMeasure matches
    'furlong' by Code in ...`, `dataset.unit is 'NONE', which its modifiers (replace(NONE: ~)) turn into no value`.
  - **not applicable** when the mapping means no value for the row: the entry's `$when` does not hold, or an entry of a
    repeated item meets a row with no child row its repeater takes. This is no failure.
  - **valid** otherwise.
- **One attribute or all of them.** A check names the attributes it is about, or none for every attribute. An attribute
  checks every entry that fills it, something inside it (a group checks what it holds), or a value holding it (a list
  written whole holds the properties of its items), and nothing else: a check of one attribute reads only what that
  attribute needs, and asks the platform nothing for another. What the record checks as a whole (a data property the
  schema requires, a required array with no item) is checked only when every entry that could fill it is.
- **What it answers, and how it scales.** Every count is exact however many rows fail: each attribute's rows by outcome
  (and an item attribute's items), and the rows as records (held, with an invalid value, leaving an attribute out,
  clean, a key with an empty part, and the rows a delivery never renders, such as a row marked deleted). What is listed
  is bounded, so the answer is the same size for a thousand failing rows as for a million: the reasons are grouped
  (rows failing for one reason are one finding whatever value caused it), up to 50 a variable; each finding lists the
  values behind it, the most frequent first, and names example records (the source key, the label, the file and row the
  row was landed from, the item, the value and the reason), in the order the scope is read. A check pages further
  examples of a finding (`skipSamples`), and `sqlflow values --rows` writes every failing row to CSV as it meets it.
  The answer stays under the 8,000,000 characters a task result holds, cutting example records and then listed values
  before any count, and saying so.
- **Which rows.** The first 10,000 rows of the scope by default, in the order a run reads them, or any number, or the
  whole scope. The flow's parameters fill the scope, as a preview's do.

On the **Mappings** page the check is on the mapping's Properties tab (see [The GUI](#the-gui)). `POST
/flows/{pipelineId}/check-values` queues one, and `GET /mappings/{id}/flows` lists the flows a mapping's rows can be
read from.

## The GUI

Everything this product adds sits in one navigation group, **OSDU**, straight after the platform's Workspace group:
Delivery, Records, Tests, Audit trail, Mappings, Templates, Cache, Dimensions, Partitions and Mapping builder. The platform's own groups (Operate,
Workspace, Tools, Explore) hold only its generic surfaces, so a delivery flow's own page is still reached through
Pipelines like any other flow.

- **Delivery** (OSDU): every delivery flow with delivered versus total, pending, held, failed, drifted, and
  its last submission, and a field that opens Records looked up for whatever is typed into it.
- **Records** (OSDU): where an operator starts from what they hold rather than from a flow. **With nothing typed the
  page lists what the delivery system last took in or sent, newest first, refreshing every ten seconds**, so "what has
  come in?" is answered before anything is asked; a status narrows that listing the same way it narrows a search. Any
  value a record is known by lists the records that start with it, across every flow: a wellbore id or a well name the
  mapping declares in `dataset.identity`, the source key or one of its key columns, a word of the label, the OSDU id
  or its own part, and the ingestion file the record came from. A delivery key lands on that record. Every row of a
  search says, under the record, which of its values matched, and what each value is on hover; the recency listing
  shows none, because nothing was typed for a value to match. A flow picker narrows either to one flow, a source that
  delivers several interfaces offering one choice per interface. **A row shows what an operator reads it by, whole,
  and the rest in a few characters**, so the grid fits its panel instead of scrolling sideways: the status; the record,
  its label with the source key and the ingestion file and row its newest version came from under it, wrapping rather
  than clipping; the flow that delivers it, named in full; when the record last changed, which orders the list, with
  when it last landed in OSDU under it; and last its OSDU type, which links to what OSDU holds, the whole id on hover
  and a copy beside it. The delivery key and the id's hash are not shown: they name nothing an operator knows. The
  delivery key is in the address of the record's page, which a row opens. One route serves both
  (`GET /api/v1/delivery/records?search=&status=&flowId=`): with a term it is the ledger's identity index, the same
  lookup the combined search reads, one seek per term (of `IX_RecordIdentity_FlowId_Token` for one flow, so other flows
  sharing the prefix cannot use up the candidate bound); with none it is the recency index (`IX_Record_UpdatedUtc`,
  `IX_Record_Status_UpdatedUtc` for a state, and their `FlowId` counterparts for one flow), read from its end. Both
  count no further than the candidate bound, and a page past it is empty rather than a scan. The term, the status and
  the flow are in the URL, so a lookup is a link that can be sent on, and a row opens the record with its whole journey.
- **The identity index.** `osdu.RecordIdentity` holds one row per value per record, the value folded to upper case
  for comparison and kept as written for display, with the kind it came from. A staging writes a record's rows as a
  set, so a row that moves on (a new file, a renamed label, a new wellbore id) is found by what it is now and no
  longer by what it was. A mapping declares which of its dataset's columns are identities; without a declaration a
  record is still found by its key, label, OSDU id and file. Records a ledger held before the index existed are
  filled in by a background pass, a page at a time, which repeats every few hours and costs nothing once done.
- **A flow's page** (Pipelines): the Delivery tab (stats, probe the target, release blocked, sync timelines),
  the Records tab (search and filters, every row opens the record), the Submissions tab, which says for each
  submission which selection it read. **A source that delivers several interfaces is read one interface at a time**,
  because each has a ledger of its own: the page carries an interface picker whose choice travels in the URL (so a
  link to a source's records is a link to one interface's records), the Delivery tab lists the interfaces in the
  order a run takes them, with each one's route, what it waits for and its counts, and the probe, the release and
  every removal act on the interface that is showing. The counts at the top of the Delivery tab are the source's.
  A flow in the single form has one interface and no picker. **A flow that names its partitions is read one partition
  at a time**: the partition picked in the title bar, which every page follows and none picks for itself, and every
  count, record, submission, preview and action below it is that partition's; a flow that does not serve it says so, and
  which partitions it does serve. A link that names a partition (`?partition=`) makes it the title bar's. The pipeline
  header lists the partitions, the Delivery overview's card counts each flow in the title bar's partition, the record
  page names its partition and links to the flow's page in it, and a run started from the trigger dialog writes to the
  title bar's partition, which the dialog shows; it refuses a flow that does not serve it. Rows tick: a selection
  bar offers "select all N matching", Sync timelines and Remove from OSDU, so a sync or a removal can be aimed at
  exactly the ticked rows or at the whole filtered set; Sync all from source above the list reads every record. A run page links here filtered to the records that run touched, and a submission's
  page links here twice: to the records it last planned (`?submission=`, which a later submission moves on) and to
  the records it delivered (`?delivered=`, which stay its own however many submissions touch them afterwards).
  The **Preview** tab renders one record as a delivery would and sends nothing ([Previewing a record](#previewing-a-record)):
  the first record of the scope, or the one a key names, with an input for each parameter the flow declares (a
  required one without a default must be filled before Preview enables). The answer shows what the next run would do
  with the record, the document as the route sends it (or as the mapping renders it), the route's requests in order,
  the payload files, the records it refers to and its source rows, and downloads as JSON. Every records list, a
  flow's and the Records page's, ends with the OSDU type of each record the ledger has an OSDU id for, and the type is
  the link that opens the record's page reading it from OSDU (a deleted record keeps its type and copy, without the
  link). A flow's list shows the record whole with the file it came from and its last error under it, and when it was
  delivered in a few characters; the OSDU version, sixteen digits that repeat the delivery time, is on the record's
  OSDU tab.
- **A record's page**: three layers, each fact in one place, and as little as answers the question. The **header** is
  who the record is and where it stands: its label, custody state and blocked flag; its OSDU id, flow and last
  submission as chips; the **situation** its state calls for and no other (held or failed with the error and the next
  try; waiting, with the record it waits for; being delivered under a lease, or a lease that ran out; a rendered
  document waiting to go, with its work batch and the steps an earlier try of it completed; blocked; removed; and the
  records waiting for this one, the first five named and the rest a click away, a warning while this record failed, is
  held or is blocked, since it keeps them waiting), so a delivered record with nothing wrong has no situation line at
  all; and the operations: Sync timeline, Verify, Redeliver, Release (while blocked), Send without
  waiting (while waiting) and Remove from OSDU. An OSDU id is long and its start repeats down a whole flow, so everywhere the GUI names
  one (the header chip, the timeline, the records and search tables, the preview) it shows the type and
  the unique part alone, clipped to the room it has, with the whole id and kind on hover and a copy beside it that hands
  the id over verbatim; the facts a tab lists (worker, correlation id, hashes, files, paths) sit one to a line beside
  their captions, each clipped at its cell with the same hover and copy. The **milestones** strip under it is five cells in the order a record moves:
  **from file** (when the landing that brought in the file its row arrived from ran, and the file), **loaded** (when
  the row reached the table the delivery flow reads), **last change** (when the row last changed there and how often
  since it was loaded, or that it was deleted), **in OSDU** (when, as which version; until then how many tries and how
  many failed) and **verified** (when, what it found), with **removed** added for a record that was. The first three
  come from the record's changes (`GET /api/v1/delivery/records/{flowId}/{key}/chain`): the versions of its row the
  ledger recorded, each proved by the runs the platform recorded, so a run that is not recorded says so rather than
  being guessed. Then the **tabs**, one question each:
  - **Timeline**: what happened to this record, newest first, in a box that takes the rest of the page and scrolls
    inside it, so a record with hundreds of tries keeps its filter in view and the page does not grow with it. It is
    the record's own story and nothing else: its row's arrival in the ingestion table with the landing that brought
    its file in, every later change of the row and its deletion, when the ledger picked it up, what the ledger decided
    (a hold, a change that rendered what was delivered, an older version), every try with what OSDU answered, and
    every request someone made of it. The last thing that happened heads the list. Where the record stands is the
    header's and the milestones' to say, not a row of the timeline, so a delivered record's current state never reads
    as one more delivery; while work on the record is in flight (queued, being sent, waiting for a record it refers
    to), the latest chapter's band says so.
    - **Chapters.** The story is cut where something set the record moving: its row arrived, changed or was deleted,
      or someone asked for a redelivery, a release or a removal. Each chapter opens with a band naming what started
      it and how it ended (delivered, delivered and verified, held because OSDU refused it, nothing to send, queued),
      numbered in the order the chapters happened. Steps that set the record moving one after another before anything
      came of them (a file landing and the load of its row, a request asked twice) open one chapter together.
    - **The rail.** Every step sits on one of three lanes, the ingestion table, the ledger and OSDU, and a path runs
      between them in the order the steps are listed, so the record's movement reads at a glance. A node's colour is
      how the step went; a step someone asked for carries the initial of who asked. Times share one column, in local
      time, with the day on each chapter's band.
    - **Picked up for delivery** is when the ledger took the record in: a deliver run read its row, rendered the
      document with the mapping and the cache, queued it, and reserved the record's OSDU id for the flow. The id is
      computed from the record key, so it is reserved before anything is sent.
    - **What OSDU answered.** A try that OSDU refused sits on the OSDU lane, titled with the status (`Refused: HTTP
      404 Not Found`), with the request, what OSDU said, and what the refusal left the record in (held, because a 404
      is not retried). Tries that ended alike one after another (a throttled service, five times) fold into one
      entry that opens to each try.
    - **Requests.** A redelivery, release, removal or verify names who asked and how many records it named: `this
      record only` for the record page's own buttons, which act on this record alone, or the count of a request made
      for several records at once. A verify and its result are one entry.
    - **Ids** of runs and submissions show their last six characters (a UUIDv7 starts with a timestamp, so a run and
      the submission it created share their first characters); the whole id is on hover and in the entry.
    - The syncs that read the row's history from the ingestion table into the ledger sit under the list: they are
      bookkeeping of the timeline itself, not steps of the record.

    A pipeline run appears only as the evidence of a change it made to this row: the ingestion flow restamps a row
    only when it changes, so a file landed and loaded again unchanged adds nothing. An entry opens to the rest known
    about it (the ingestion run that wrote a change and the landing of its file; the steps a try took and what each
    returned, its worker, correlation id, work batch and the origin it sent; a request's parameters). The timeline
    narrows to one lane, or to the requests someone made.
  - **Source**: where the row came from: the source key, the ingestion file and row the delivered document was built
    from (and the newer row a waiting document is built from), when the row was received and the source last
    modified, the key columns that find it (each value beside the column the flow's `source.record.key` names, with a
    copy of the key as the ledger holds it), and a read of its rows as the ingestion tables hold them now, on a node
    with the flow's own connection.
  - **Render**: the record's output, built from the data available now. **Render** builds the record's manifest from
    its current source row with the flow's mapping and the cache, on a node, and reads what OSDU holds through the
    flow's route; nothing is sent. Only the manifest is built, not the DDMS sections a DDMS route sends besides it (bulk
    data, series, rows). Above the result, the mapping and cache version that rendered the document OSDU holds sit
    beside those the render used, each flagged where it changed since. The result names what the route does with the
    manifest (the DDMS it reaches, and the route's own notes: the link a DDMS keeps on the record, keys carried forward
    from OSDU, a dataset already registered), which explains the differences a comparison alone cannot; what the next
    run would do and why; why a delivery would hold the record; the preflight's warnings; and the document, either
    beside OSDU's copy (a side-by-side comparison with the unchanged stretches folded away, the differences counted by
    kind) or whole, with the searches the render made. Nothing is shown twice: the comparison is the list of what
    differs. The render stays on the tab while another is looked at. An old `?tab=document`, `?tab=compare` or
    `?tab=context` link opens it.
  - **OSDU**: the record as OSDU holds it, read on a node through the flow's route, in an **inspector** of fixed
    height, so a record of ten thousand values is read the way a file tree is, never as one tall page.
    - **One location bar** says where the reader is, and nothing below it repeats it. It has a row of its own across
      the inspector, and a path longer than the row wraps onto a further line rather than clipping. The page's own
      record is named by its type alone, as the page names it above (the whole id and kind on hover), then the path
      inside it. The record's name stands for its content, the `data` block every record keeps it in, so a path inside
      it follows the name without a `data` step, while the other sections (`meta`, `tags`, `ancestry`) and the views
      (System fields, Full document, Access & legal, Linked records) are named steps; the copy beside the last step
      yields the full path. A record opened from a link shows its own path alone, named by its type and, where its id
      reads as a name, the unique part of it (an id minted as a hash or a GUID gives the type alone), after one way
      back that names the record it was opened from: `<- WellLog | Wellbore NO-15-5-7-AT2 > NameAliases > [0]`. The
      way back says on hover where that record named this one, and returns to it as the reader left it. The row below
      holds the view (Fields or JSON) and the **version picker** on the left, and the page's read controls on the
      right.
    - **The version picker** shows the version in view, marked latest or older (amber), with a check when it is the
      version this flow delivered; its menu lists every version OSDU keeps, newest first. A picked version is read on
      a node and replaces the record in place, the outline and the place in it kept. **Compare with latest** then
      opens, in a dialog of its own, what changed since that version: counts of values changed, added and removed,
      the two side by side with the unchanged stretches folded away, and every changed value with its path. A target
      that keeps no version list, such as a DDMS, says so.
    - **The outline** on the left never moves. Record: **Full document** (the whole record, envelope and all, as OSDU
      returned it), **System fields** (id, kind, version, who created and last modified it and when, the read's
      correlation id) and **Access & legal** (viewers, owners, legal tags, countries, legal status). Content: the
      record's own blocks by OSDU's names (`data`, `meta`, `tags`, `ancestry`) and the objects within them, with
      their sizes. References: **Linked records**, every record the document names, with where it names them.
    - **The detail** on the right shows one level at a time. As **Fields**, an object is rows of field and value, a
      nested object a step into it, and a list of items a table with a row per item and a column per shared field,
      each row a step into its item. As **JSON**, the branch folds at every object and list, a key that holds a
      branch steps into it, and a list's item opens from its brace. Long lists show their first hundred and the rest
      on a click. In both, a value that names another OSDU record is itself the link, with no icon beside it: a click
      opens that record in the inspector, read through the same route, and the copy beside it yields the id.
    - **Search** counts its matches on every branch of the outline, marks them where they stand, and steps from match
      to match. The branches a reader opens are remembered in the browser for the record's kind. **Open in a window**
      opens the explorer alone in a browser window of its own (`/delivery/records/{flowId}/{key}/osdu`): the record
      named on one line and the inspector filling the rest, read as the window opens, with nothing of the workbench
      around it, so it can sit on a second screen. One window per record: opening it again brings that window back. A
      page opened with `?tab=osdu` reads the record at once.
- **The removal dialog**: one surface for both. It names the target first (endpoint as declared, data partition,
  protocol, auth) because that is which OSDU the records are about to leave, then the three scopes side by side
  with what each destroys, whether it can be undone, what the ledger will do, and the exact call it makes. The
  two permanent scopes ask the operator to type the data partition back before the button enables.
- **A submission's page**: which selection it read and the window it covered, the ingestion table and connection it
  read from, its counts, the runs that carried it, its work batches, its attempts, and its two record sets as links:
  what it delivered and what it last planned. The submission is the batch, and **Remove what it delivered from OSDU**
  is how a batch is undone: one removal aimed at exactly the records this submission delivered, through the same
  removal dialog (the target, the three scopes, the typed confirmation for the permanent ones), with the count read
  the way the removal will resolve it and refused if that count has moved by the time it runs. The button is off
  while the submission has delivered nothing that is still in OSDU under its name.
- **A retrieval flow's page** (Pipelines): the Retrievals tab, every run with its window, location, counts and
  outcome; a row opens the platform run.
- **A cache flow's page** (Pipelines): the Cache versions tab names the partition the flow fills and how many other
  cache flows fill it too, and lists every version of that partition's cache with the flow that wrote each, with a link
  to the OSDU cache page for what the cache holds and the changes waiting for approval.
- **Tests** (OSDU): how the data stands now, by every test of every assertion flow in the workbench's partition
  ([docs/assertions-design.md](../../docs/assertions-design.md) section 8). A test is worth what its last run found, so
  every surface leads with that, says each thing once, and leaves earlier runs to the flow's History and Reports tabs. A
  strip heads the board: the pass rate with its bar, then one count per standing (failing, errored, warned, not run,
  passing, and unfit or changed when a test is), each the filter to the tests it counts. Search (names, types, tags,
  queries, checks) and a tag picker narrow it further. Each flow is a card: its name (its description and what fails a
  run of it on hover), the type its tests read, its last run with the report it kept, **Report** (open or download) and
  **Run all**, which becomes **Run N picked** once tests are picked with their boxes. Each test is one line: its outcome,
  its name (its description on hover; a mark when it does not fit its schema or changed since its result), the verdict
  of its last result ("2 of 9 checks failed", "all 3 checks passed"), and a button that runs it alone; a test whose
  last result comes from an earlier run than the flow's last (a run that left it out) says which run and when. Every run
  goes through the platform's trigger dialog, opened on the tests asked for.
- **A test** opens in a sheet (its address carries `?test=`, `testTab=` and `check=`, so a link opens exactly it): its
  outcome and name, **Latest report** and **Run**, one line saying how its checks came out with what it matched, how long
  it took and when, and its description. **Checks** opens on the checks that ask for a look (every one, when all of them
  passed), the others a click away; a check that passed shows only its mark, one that did not shows what it found. A
  check opens on its own: what it expects (when its name does not already say), what it found, why, and the records that
  failed it (id to copy, the value found, why it fails). A test not run yet lists the checks its first run will make; one that
  does not fit its schema, errored, or changed since its result says so above them. **Definition** says what the test
  reads: the type, the schema it is checked against, the records it selects (the query to copy), how it reads them, its
  checks' default severity, tags and partitions.
- **Dimensions** (OSDU): the dimensions of every dimension flow in the workbench's partition
  ([dimension-plan.md](dimension-plan.md)). A dimension keeps a **key** (exactly what the index holds, an id for a
  reference) apart from its **value** (the human-friendly form a person picks, read from the record a key names and
  cleaned), and every key and value carries the search that finds its records. The page is three views of one address.
  **Every dimension**: a strip of facts (how many dimensions are built, the values and keys they hold, what needs a
  look, when anything was last built), then each flow's dimensions as cards, a card naming where the dimension stands,
  what it reads, the five values most records hold as bars, and when it was built; **Run pipeline** on a flow's heading
  runs the flow, which builds every dimension it declares, and **Build a search** opens the search builder. **One dimension**, on the page's whole width:
  its heading is one block, the way back (**Dimensions**), its name and where it stands, a switcher to the other
  dimensions, what it is for on hover, **Export** and **Run pipeline** (the flow's run dialog, opened with that
  dimension picked); under the
  name, in a line, its facts (values and keys, keys without a label, keys of no value, the share of the records read that
  hold a key, when it was built), each opening the tab that explains it, and the kind, path and flow it reads. Under
  that, what its newest build or declaration asks of the reader (the failure with **Run pipeline**, a build running, a
  changed declaration), and six tabs. Each tab's grid scrolls inside the page under column headers that stay in place,
  as tall as the window leaves, and reads the next page as it is scrolled, its foot saying how many rows are in view of
  how many; **View** leaves columns out (remembered per dimension in the browser) and lists what builds no longer find.
  **Table**, the tab a dimension opens on: the dimension as one table, read from its own table in the database (named
  beside **View**, to copy), a row per key and value it collects with the value, the key, a column per attribute, the
  records of the row and the key's search to copy; found by text in any column, narrowed by **Attribute**, and ordered
  by any column under **Sorted by** (picking the column again turns the order round). Its `id` column, the number a
  table of facts joins on, is left out until **View** asks for it.
  **Values**: each value with its records beside a bar, the keys it stands for (their count where keys are ids read for
  a name, the ids on hover; the spellings themselves, with their counts, where keys are cleaned into a value), a column
  per attribute (the first value, and how many more), and its search to copy; searched by the value or any key, with the
  most records first or from A to Z. Values are picked with their boxes, and **Write their search** opens the OSDU
  search that finds all their records: each query to copy, the whole search request, and what it leaves out. A value
  opens in a sheet: its records, its attributes a line each (a collected attribute's values with the records holding
  each), its keys, its search, and every change that moved a key to it or away. **Keys**: every key exactly as the index
  holds it, cut short where ids begin alike so the end that tells them apart stays in view (whole on hover), what a
  reader could not see (a space at an end, a tab, a control character) drawn as a mark, with the value it went to (the
  label read for it and the record it was read from on hover) or why it has none, its records, a column per attribute,
  and the search finding exactly its records to copy; **Of no value** narrows it to what cleaning left out. Values and
  keys are both narrowed by **Attribute**: pick an attribute, then one of its values from the list the dimension holds
  (found by what is typed); a chip per attribute shows what narrows the grid. A value the dimension gives what it could
  not read (Not specified) is drawn faint. **Changes**: the change log by build, narrowed to what arrived, left, moved
  or came back. **Builds**: every build with its outcome, duration, values, coverage, changes and how it read, each
  opening whole (its notes, the kinds and templates it read, the keys labelled, every count). **Definition**, in four
  parts whose facts line up: **Table** (the table's name, its columns with what each holds on hover, what it is joined
  on, and the query that reads it, each to copy), **Reads** (the kind, path, query, how the index stores the field and
  the aggregation that reads it, how records are counted, the most keys read), **Values** (the label as a path from the
  key's record to the text read, what a key without one is valued, and the clean steps as a pipeline to the value) and
  **Attributes** (each attribute's path, or the path it collects); then the kinds its last build read. **Export**
  downloads the whole dimension as CSV or JSON Lines: its **table** (row for row as the database holds it), its values,
  or its keys. **Build a search** composes the OSDU search a filter
  panel needs: pick the kind, then values in any of its dimensions through a picker that finds a value by itself or any
  key, and **where** an attribute holds one of the values picked (every wellbore where Country is Norway); the control
  plane composes the query (OR within a dimension, AND across them, within their own queries and a query of one's own),
  shown with its clause count, each dimension's part, what the picks left out, and the query and the request body to
  copy. Nothing is sent to OSDU from the page. The dimension, tab and value open in a sheet, and the builder's kind and
  picks, are in the address, so a link lands on the same view. A dimension flow's page (Pipelines) has a **Dimensions**
  tab listing its dimensions, each opening on the Dimensions page; the page's own **Trigger run** runs the pipeline, and
  its dialog picks the dimensions a run builds, so the tab adds no button for that. A run's header counts the values and
  keys it built. A dimension its flow no longer declares keeps what
  its last build wrote, out of the way: the tab lists such dimensions under **No longer declared**, and the Dimensions
  page only when **Show n dimensions no longer declared** is clicked. An admin removes one for good with **Remove** (on
  that row, or on the dimension's own page), after a confirmation saying what goes; one a cache flow still captures is
  refused.
- **An assertion flow's page** (Pipelines): **Tests** is that flow's board. **History** and **Reports** hold the runs
  before the last, for whoever asks: History lays the tests against the last 30 runs with each test's pass rate and how
  often it flipped (three or more is marked), a column opening its run's report; Reports lists the runs with their
  outcomes as a bar and every download.
- **A test report** (`/delivery/assertions/runs/<n>`): one run as the report of how the partition stood then: the flow,
  partition, time, duration, actor and what it ran; the same strip as the board over the run's tests; the tests by type as
  bars when there are several types; and every test on one line that opens in place on what it read as it ran and its
  checks that ask for a look, the failed and errored ones open. The run's error is shown only when the run itself stopped;
  a run failed by its tests says so through them. **Run the tests not passing again** opens the trigger dialog on the
  failed, errored and warned tests; **Run again** repeats the run's pick; **Report** opens the HTML report in a tab of its
  own or downloads it as HTML, Markdown, JSON or JUnit XML. The platform run's page links here (**Open the report**) and
  shows the counts in place of row counts.
- **Audit trail** (OSDU): every run and intervention across flows, newest first: when, flow, action, who started it (a
  schedule, a person, the command line or a service), the result (how it ended and the counts that are not zero) and
  what it worked on (the record, else the submission, else the run). The runs that changed nothing are left out and
  counted beside **Show idle runs**, which brings them back dimmed. An entry opens on the whole ids of its record,
  submission and run, its parameters and, for a run, its captured log. The table fits its page: the flow and the result
  clip with the whole value on hover rather than scrolling sideways.
- **Mappings** (OSDU): the mapping documents the repositories hold, each mapping with the template it pins and a
  link to the Mapping builder. A mapping opens on its Properties, laid out so that what needs a look is what stands
  out: the data check on one line, then the template the mapping pins as the record's tree beside the selected
  attribute.

  - **The tree marks exceptions only.** A row is marked where something asks for attention: a red or amber sign for a
    finding, a half circle for an attribute filled on some rows only, an open circle for one nothing fills, and after a
    data check the count of its failing rows in the tone of the worst outcome. An attribute filled on every row, or
    checked clean, carries no mark (its state is still read by assistive tech and on the row's hover).
  - **One view control asks the question.** Filled (what the mapping fills and every required attribute a check names,
    the overview it opens on), Missing (what the record requires and the mapping does not fill on every row, the
    validation against the schema), Unfilled (every attribute nothing fills, what the mapping could carry and does
    not), Failing (after a data check, the attributes it found failing rows for) and All. Each carries how many
    attributes it holds, so the answer shows before the view is picked, and picking the view in view again returns to
    Filled. **View** holds the options used now and then: Required (only what the schema requires), Minted (what OSDU
    Delivery writes and OSDU sets) and Nested (lists inside a repeated item). The search matches what fills an
    attribute as well as its path and description, so a source column answers which attributes it reaches, and says
    how many more it finds in the whole template when a narrower view hides them.
  - **The selected attribute reads in three tabs.** Its name, path, requiredness and any finding sit on top with its
    **Check values** button; below, **Filled by** lays out what fills it as the pipeline that fills it (the value's
    origin, a dataset column, a cached field with every line the lookup tries in order, a repeater or a static value;
    the modifiers as the steps they are, drawn inside the lookup for a cache entry since they change the value compared
    rather than the cached field; and the condition that decides whether the value is written at all), **Data** is what
    a data check found of it, and **Schema** is what the template says of it.

  A mapping pinning a template version nobody saved has no tree to lay itself over, and lists its own entries instead.
  The YAML tab is the document as written, and the Record shape tab draws the record the mapping renders.

  The **data check** line reads the rows of a flow that renders with the mapping in the title bar's partition and finds
  the ones that will not give an attribute the value its template expects ([Checking a mapping's
  values](#checking-a-mappings-values)). Pick the flow, its scope's parameters (each offering the values its column
  holds, see below) and how many rows to read, and **Check all attributes**; or select an attribute and use its own
  **Check values**, which checks it and what it holds and names more example records. Under the controls one line sums
  the answer up (the rows read, then held, invalid and left out, each narrowing the tree to the attributes behind it,
  then clean), and narrowing selects the first failing attribute with its Data tab open. The Data tab shows the
  attribute's rows as a bar by outcome, the outcomes some row came to in its legend (by item for a repeated array's
  property), then the rows that will not give an expected value grouped by reason, each with the values behind it and,
  opened, its records: the source key, the label, the file and row, the item and the value, each opening in the flow's
  Preview tab on that record, with the next hundred listed on request and the loaded ones copied as keys or saved as
  CSV. The values written, the most frequent first, are a click away under the reasons, and the tab's label carries the
  count of failing rows. Each check ends with a notification.

  **A scope's values are picked, not typed.** Wherever a flow's scope is given its values (the value check here, a
  flow's Preview tab, and the trigger dialog of a delivery flow), a parameter the flow's `source.record.scope` binds to a
  column of its record table lists the values that column holds, with how many rows hold each, read on a node from the
  flow's own table (`POST /flows/{pipelineId}/scope-values`), so every source offers its own column's values without a
  list kept anywhere. Any value can still be typed, and one the column holds no row of is flagged, since the scope would
  read nothing. A parameter the scope does not read (one naming the work location) is typed.
- **Templates**: browse the schemas OSDU publishes through a delivery flow's connection (a node runs the search and the
  fetch with the flow's credentials), look at one laid out as a template (every variable with its type, requiredness,
  relationships, unit context and OSDU's description), and save it; or import a bundled schema file. The saved
  templates are listed with where each version came from and how many mappings pin it, and a version no mapping pins
  can be deleted.
- **Mapping builder**: pick the repository, a saved template and the partition whose cache the mapping is checked
  against (the Partition cache picker lists every partition a synced cache flow fills, with those flows, and defaults
  to the partition the repository's delivery flow delivers to), and the page lists every template variable, with a
  cache entry prefilled for each variable outside a repeater that points to an entity type that cache holds. Each
  entry takes its value from the dataset, a repeater, the cache (one record by `$findBy`, or every matching row by
  `$findAll`), a lookup, a search, a static value, the first of several alternatives, or for a list a list of values,
  with its modifiers, condition and required flag; the mapping's lookups are edited in a card of their own. The YAML and
  its checks against the template and the cache's current version follow every edit. The mapping is copied, or proposed
  to the repository as a pull request through the proposal endpoint (`POST /api/v1/repos/sources/{id}/proposals`). An
  existing synced mapping opens with everything it declares, fixtures' cached rows included; the builder writes the
  document whole, without the comments a hand-written one carries.
- **Cache** (OSDU): the reference and master data every delivered document is built from, one cache per OSDU
  partition. The header names the partition and, on one line, the cache flow files that fill it by file name, each
  with its path in the repository on hover (or how many when more than three fill it, every file on hover), with an
  info mark that says what a cache flow is, who reads the cache and how to change what it holds, and Cache files and
  Refresh now at its top right. Four tabs follow, each answering one question, with what it is for on hover of its
  name: Records (what the cache holds), History (how it changed), Deliveries (what it means for the records already in
  OSDU) and Setup (how it is filled). They carry no counts: what waits for a decision is in the summary row and the
  banner. Cache files opens Pipelines filtered to the cache flows
  (`?kind=cache`, and `?repo=` when one repository holds every flow filling the partition), since a partition can be
  filled by several files; Refresh now opens the trigger dialog on the cache flow with the refresh operation, and is a
  menu naming the flows when several fill the partition, since a refresh runs one flow's capture. A link to Pipelines
  can set its repository and kind filters (`?repo=`, `?kind=`), and a kind filter opens the groups it narrows the tree
  to. The cache shown is the one of the partition picked in the title bar, which the header names; a partition with no
  cache says so. A cache flow that names its partitions says which it builds, and its Refresh builds the title bar's
  partition. A summary row follows: the current version with the flow that wrote it, when and for whom, how many
  records it holds in how many types, whether a schedule refreshes it, and either that changes are automatic or how
  many wait for approval. Whenever a change waits, a banner says so with Review changes. The Records tab lists the
  types beside the records, grouped by family, each with its record count and a shield when its changes need approval;
  the History tab keeps a searchable type picker at the end of the tab bar that lists the types with their family,
  record count and whether their changes need approval, as the Records tab does on a screen too narrow for the list.
  Picking a type scopes those tabs; All types, or Clear filter in the picker, lifts the scope. The tab and the type are
  in the URL, so a link opens the same view, and a link from when the page had other tabs (`?tab=versions`, `changes`,
  `gaps`, `definition`, `flags` or `system`) lands on the tab that holds it now, scrolled to its section. A link naming a
  partition (`?partition=`) or a cache flow (`?flow=`, as a cache run's page uses) makes that partition the title bar's.
  The setup is read-only because it lives in the cache flow files in git; the decision on a change waiting for approval
  is the one thing made here.
- **Records.** The cached records at the version being read, with the search over every value they hold (id, code,
  name, alias) and the version picker in the tab's own toolbar. Every table reads one type in that type's own columns:
  what tells the records apart first (the part of the OSDU id after the partition and entity type, which the type's
  header states once, or a lookup table's key), then one column per captured name, each as wide as its values need so a
  lookup table with many names fits the page. A value that only repeats the row's identity or a value to its left (a
  Code equal to the id, an ID equal to the Code) is drawn muted, so the values that say something new stand out, and
  the escapes an id carries (`%20`, `%5B`) are kept as captured but stepped back so the words between them read. All
  types shows each type as a section with its first five records and how many it holds, and opens the type's own paged
  table from there. The type's header says where its records come from (the kind searched on OSDU, or the ingestion
  table or dictionary and the key it holds them by) and which cache flow declares it. While a search is typed, the type
  list counts the matches in every type and steps back the types nothing matches in, only the types with matches keep a
  section (the others are named under them), and every match is marked in its cell. A row opens the record: its captured values by name, how a mapping reads it (each source, `cache.<Type>.id` and
  `cache.<Type>.<name>`, next to the value it reads for this record, and a `source` plus `findBy` entry to copy that
  selects the record by one of its values), the JSON as captured, and the version it is the record as of. A type's
  header also says how the version being read holds it: changed or added in this version, or unchanged since the
  version that last changed it, with its content hash on hover.
- **Reading the cache as it stood.** The version picker opens on the current version, the one deliveries render
  against unless a flow pins another; picking an earlier one says so above the table, because nothing shown then is
  what a render would read today. Every version the partition's cache flows wrote stays readable. With a type in view
  the picker offers that type's own versions instead: the versions of the cache at which its content hash moved, each
  with its hash and whether it added or changed the type. The newest of them holds what the current version holds, so
  Current version stands for it, and any version picked holds other records or values of the type than the current
  one: Compare with current always has a change to show. A version whose content came back to what the current one
  holds is marked as the same as current, and offers no comparison.
- **History.** Every version, newest first, each with the cache flow that wrote it and what captured it (the run and
  who asked, or an import from files), the types it changed (each tinted by whether it arrived, changed or left, the
  rest counted and named on hover, or no type changed when the version was written for something else, such as a
  changed OSDU feature flag), and what it changed compared with the version captured before it (so many changed, added,
  removed, or no changes). With a type in scope the list is that type's own versions: the versions of the cache at
  which its content hash moved, each with the hash, how many records of the type it holds, and whether it added,
  changed or removed the type with how many records; the versions that held it unchanged are counted above the list
  and left out, since comparing them would show nothing. The newest is marked current when the current version still
  holds the type, and picking one compares it with the version of the type before it. A version is the whole partition's cache and is written when anything in it moves, so a type
  is listed only when its own content hash moved: a type that rode along with another's change is not. Picking a version raises its changes in the workbench bottom panel, so the list stays in view: changed
  records with the captured values that moved, before and after, records the version added, and records it no longer
  holds, narrowed by a change kind and a search. A row opens both sides. History covers the whole cache, which is
  what separates it from Deliveries: Deliveries holds only what reaches records already delivered, so a refresh that
  moved values nothing was built from shows in the history and leaves Deliveries empty.
- **Deliveries.** What the cache means for the records already in OSDU, in one list, *Updated by the cache*, filtered by
  state: Missing from cache, then the states of a change. With `onChange: auto`, the default, neither asks anything of
  anyone: the next delivery of each record carries the change.
  - *Changes* (waiting for approval, approved, rolling out, rolled out, rejected, or all changes): the changes this
    cache's refreshes found in values delivered records were built from, each with the value before and after, how many
    delivered records it reaches and how many of them their flows have rendered again. A change is rolling out from
    the moment its records are marked for redelivery until no flow still builds one of them from the old value: several
    flows can read one cache, each renders its own records on its own next run, and the change is rolled out when the
    last of them has. Its row says how many flows are still to run, and its panel names each with its records and a
    link to the pipeline. A line above the list says
    which types ask for approval. A type set to `onChange: auto` has its change approved as it is found, and it shows
    here as a rollout that reaches each record on its next delivery; when a type asks
    for approval (`onChange: approve`) the list opens on the changes waiting, and each waiting row carries Approve and
    Reject. Picking a change raises it in the bottom panel with both values in full, the versions it moved between, who
    decided it and when, and the same decision while it is still open. Selecting waiting changes raises a toolbar that
    says how many delivered records the decision would redeliver, and approves or rejects them in bulk.
  - *Missing from cache* (what the list opens on when nothing waits for approval and something is missing): what records
    already in OSDU were built without, because the cache did not hold it when they
    were rendered, most records first, paged: a value no cached record answered to (not found: a wellbore loaded after its
    logs), a `$findAll` none of whose keys found a row (no rows: a field no access group lists yet; a key that found
    nothing beside one that found rows, such as a wellbore's second GeoPoliticalEntity, is none), and an id written without
    its record because the mapping says `$unverified` (unverified), each with its type, what was looked for and how many
    records were built so. Include paths read empty adds the paths that held nothing (a wellbore without a field).
    Nothing needs doing here: once a refresh brings what was missing, it leaves this list for the changes, and the
    records are updated like any other cache change, on their next delivery. A gap that should not be there points at
    the source.
- **Setup.** How the cache is filled, as a navigator rather than one long page: a tree on the left of the partition, each
  cache flow with the types it declares under it (a type several flows declare is under each), and the partition's
  OSDU feature flags and how a mapping reads the cache; the part picked is on the right, each with what it is for on its
  info mark. On a screen too narrow for the tree beside it, the tree is a picker over the part. The part picked is in
  the URL (`?node=overview`, `flow:<name>`, `type:<name>`, `flags` or `mapping`), and a link to `?tab=flags` or
  `?tab=system` opens the flags.
  - *The partition* (what Setup opens on): one line each for the cache flows that fill it, the types they declare, what
    a changed value does (updated on each record's next delivery, or the types that wait for approval), when a new
    version is written, the feature flag the engine reads and its state, and how a mapping reads the cache. Every name
    opens its part.
  - *A cache flow*: its file and repository, where it captures from, when it runs, the partitions it builds, and the
    types it declares with where each comes from, what it keeps and what a changed value does, with View YAML and
    Refresh.
  - *A type*: where its records come from, per flow declaring it (the kind searched and its query, or the ingestion
    table or dictionary and its key), what a changed value does, how many records the current version holds, every
    value kept by the name a mapping reads it by (`$cache: <Type>.<name>`) with the path it is read from and the flows
    declaring it, and the mapping entry that reads it, ready to copy (with the replace a lookup table translates a value
    by). Records and History open the type on those tabs.
  - *Reading it in a mapping*: how a mapping reads the cache (`$cache: <Type>.id` or `$cache: <Type>.<name>`, and
    `$findBy` lines), with a node to copy, and that a mapping never names the cache.
  - *OSDU feature flags*: the partition's feature flags as the current version holds them: settings of the OSDU
    platform, which OSDU Delivery reads with every refresh and never changes (they were once a tab called System
    properties, and a link to `?tab=system` still lands on them). One block per service names the endpoint the flags were read from (the
  indexer's `GET /api/indexer/v2/info`, the search service's `GET /api/search/v2/info`), with how many are on and off,
  and lists each flag with its state, the configuration the service says set it, and the version whose refresh found it
  in that state; a line says so once when no flag has changed since it was first read. The flag OSDU Delivery reads
  (`featureFlag.keywordLower.enabled`) is marked, with what it changes on hover. A service that gave the version no
  flags says so in a line: it reported none for the partition, or could not be asked, which the refresh's log says.
- **Runs**: a delivery run is a platform run; its trace streams live and its parameters, record counts and
  result show on the run page; a fan-out member shows its root and slot. Re-run repeats the same parameters.
  The trigger dialog offers the operations the flow's kind runs. For a deliver run, **Force** looks at every record past
  the whole-run gates and still sends only what changed; **Send again** sends records whatever their hashes say: the
  metadata and payload, the metadata or the payload of the records named under **Redeliver these records**, or, with none
  named, of every record the flow has delivered in the partition. A record's own page redelivers that record alone.

## The CLI

| Verb | Purpose |
| --- | --- |
| `sqlflow validate <flow.yaml>` | The platform's document validation (the CI gate for a folder). |
| `sqlflow check <flow.yaml> [--interface <name>] [--partition <name>] [--connect] [--set name=value]... [--db <ref>] [--json]` | The delivery preflight: the flow, its mappings, its templates and its payload roots. With `--connect` it opens the flow's source connection on this machine and reports the tables, their columns, the key types, the system columns, the current watermark window and the candidate counts. A source is checked one interface at a time, each with its route, its ledger, its wave, what it waits for and why, and the references it does not wait for, after a first line giving the order the interfaces run in; `--interface` checks one. Interfaces that wait for each other in a way nothing cuts fail the check, naming them. With `--json`, a source answers `flow`, `order` and one object per interface (with `wave`, `waitsFor` and `notWaitedFor`). |
| `sqlflow preview <flow.yaml> [--interface <name>] [--partition <name>] [--key <key>] [--set name=value]... [--out <file.json>] [--db <ref>] [--json]` | One record rendered as a delivery would render it, and nothing sent ([Previewing a record](#previewing-a-record)): the first record of the scope, or the one `--key` names. It says what the next run would do with the record, lists the payload files and the route's requests, and prints the document as the route sends it. A source is previewed one interface at a time, each with its own first record; a key names a record of one interface, so it needs `--interface`. `--out` writes the whole preview as JSON, however large its document. Ends with 1 when no record was found. |
| `sqlflow values <flow.yaml> [--interface <name>] [--partition <name>] [--target <osdu.path>]... [--set name=value]... [--max-rows <n>] [--samples <n>] [--skip <n>] [--rows <file.csv>] [--out <file.json>] [--db <ref>] [--json]` | The rows that will not give the mapping's attributes the values the template expects ([Checking a mapping's values](#checking-a-mappings-values)): each attribute's rows by outcome, every reason with the values behind it and example records. `--target` checks one attribute and what it holds (repeat it for more); `--max-rows` reads that many rows (10,000 by default, 0 for the whole scope); `--rows` writes every failing row to CSV, however many; `--out` writes the whole check as JSON. Ends with 1 when a row is held or writes a value the template does not accept. |
| `sqlflow run <flow.yaml> [--operation deliver\|verify\|plan\|intake\|drain\|replan\|sync\|retrieve\|refresh\|test] [--set name=value]... [--payload <json>\|@<file>] [--db <ref>]` | A run on the workstation. The payload carries the delivery kind's arguments (`force`, `submissionId`, `recordKeys`, `redeliver`, `interface`, `interfaces`; [The API](#the-api)). A delivery flow's runs need the module database connection: rendering reads the template and the cache version saved there, and the ledger lives there. A retrieval flow runs `retrieve` by default, and runs without one. A cache flow runs `refresh` by default, which merges into the cache of its partition and so needs it. `--set partition=<name>` names the partition a flow that works in partitions runs in; without it a run takes the registry's default when the flow serves it, else the flow's only listed partition. A cache flow given `--set partition=*` builds every partition it serves in turn. An assertion flow runs `test` by default, with `--payload '{"tests":["log-headers"],"tags":["smoke"]}'` to run some of its tests, and keeps its report in the module database; `--operation plan` checks and counts the tests and records nothing. |
| `sqlflow assertions list <flow.yaml> [--partition <name>] [--max <n>] [--db <ref>] [--json]` | An assertion flow's runs in a partition, newest first: status, counts, when and by whom, and why a run failed. |
| `sqlflow assertions status <flow.yaml> [--partition <name>] [--db <ref>] [--json]` | Where each test of the flow stands: its latest outcome and report, what it matched, how many of its assertions failed and why it errored, and whether the test changed since. |
| `sqlflow assertions report <flow.yaml> [--partition <name>] [--run <n>] [--format json\|md\|html\|junit] [--out <file>] [--db <ref>]` | A run's full report (the latest when `--run` is left out), rendered by the same code as the API's; `--out` writes it to a file, which a CI job publishes (JUnit XML for its test view). |
| `sqlflow dimensions list\|table\|values\|keys\|attributes\|filter\|search\|history\|changes\|export\|remove <flow.yaml> [--dimension <name>] [--partition <name>] [--db <ref>] [--json]` | A dimension flow's dimensions in a partition, read from the module database: each dimension with its table and its last build, a page of a dimension's table, a dimension's values or keys (each key with its label, attributes and filter; `--attr <attribute>=<value>` narrows them), an attribute's values, the search that finds the records of the values `--value` names (one query a line, ready to send), the search across dimensions from the values `--pick <dimension>=<value>` and the attributes `--where <dimension>.<attribute>=<value>` name, its builds, its change log, and the whole of it as CSV or JSON Lines, written by the same code as the API's ([cli/delivery.md](reference/cli/delivery.md#dimensions)). A dimension flow builds with `sqlflow run <flow.yaml> --payload '{"dimensions":["Wellbore"]}'`. |
| `sqlflow records list <flow.yaml> [--interface <name>] [--partition <name>] [--search <term>] [--contains] [--status <status>] [--max <n>] [--db <ref>] [--json]` | The interface's records from the ledger: the delivery key, the status, the source key, the OSDU id and version, what a waiting record waits for, and the last error of each. A source is read one interface at a time, and says which interfaces it has when it is not told. |
| `sqlflow records show <flow.yaml> --key <delivery key \| source key> [--interface <name>] [--partition <name>] [--attempts <n>] [--db <ref>] [--json]` | One record and every try it took: the custody state and where the row came from, then each attempt with its outcome, what it delivered, how long it took, the steps it ran with what the target answered, and the error that stopped it. The source key finds the record as surely as the delivery key, because that is what an operator holds. |
| `sqlflow cache list <partition\|cache.yaml> [--partition <name>] [--db <ref>] [--json]` | The versions of a partition's cache (a cache flow's file lists the partition it fills, or every partition it serves, or the one `--partition` names), newest first: the cache flow that wrote each, when it was captured, by whom and in which run, and what it holds. |
| `sqlflow partition list \| add <name> [--description <text>] [--default] \| describe <name> --description <text> \| default <name> \| remove <name> [--db <ref>] [--json]` | The partition registry ([docs/partitions-design.md](../../docs/partitions-design.md)): the partitions a flow that names none serves, and the default a run that names none runs in. The first partition registered becomes the default. Removing one deletes nothing kept under it, and the default is removed only when it is the last. Each repository describes a change at its next sync; the control plane's own upkeep (the Partitions page) makes every repository due at once. |
| `sqlflow cache import <cache.yaml> --from-dir <dir> [--partition <name>] [--db <ref>] [--json]` | Merge type files (`{Name}.json`) into the cache of the flow's partition as that flow's capture, for work without OSDU; without `--partition`, the partition a refresh would take. The files must match the types, entity types and captured names the cache flow declares. A cache is captured from OSDU with `sqlflow run <cache.yaml>`. |
| `sqlflow template capture --kind <kind> [--release <tag>]` | Save a kind's schema from the OSDU data definitions as a template version (the newest release by default). |
| `sqlflow template import <schema.json> --kind <kind> [--release <tag>]`, `sqlflow template import --from-dir <dir> --kind <kind>` | Save a template from a schema file, or from a local checkout of the OSDU data definitions. A bundled file is saved as it is; a file as the data definitions publish it, referring to `../abstract/...` schemas, has those read from `--release` (the newest by default), and the template's origin names the release. |
| `sqlflow template list \| show --kind <kind> [--version <v>] \| delete --kind <kind> --version <v>` | The saved templates, one laid out variable by variable, and deleting a version no synced mapping pins. Every `template` verb needs the catalog connection (`--db <ref>`). |
| `sqlflow trigger --repo <r> --flow <f> [the same run options]` | Queue a run on the fleet. |

See [reference/cli/delivery.md](reference/cli/delivery.md).

## What a run's trace says

A delivery run streams what it is doing to its live trace (the GUI's trace panel, `sqlflow trigger --follow`) as it
does it, and the same lines are its `run.log` and the `events` of its `run.json`. Each line is filed under the step
that said it:

| Step | What it says |
| --- | --- |
| `run` | The run and who asked for it; the mapping, template, cache version and partition it renders with (never the mapping's other parameter values, which can be secret references); the route it delivers by; whether the legal service accepts the mapping's legal tags; the window it reads; its outcome. |
| `source` | The ingestion table it opened, the window it fixed and how many candidate records are in it; how a fan-out cut them into ranges. |
| `plan`, `intake` | The mapping's preflight warnings; the tier-0 skip; the submission being planned and how far the planning has got; held records with their reason; the submission's counts. |
| `deliver` | The batches claimed and finished; how far the run's deliveries have got (`Delivering: <n> of <m> planned record(s) settled after <t> (<rate> a second): ...`), from the run's totals; held, failed and retried records with their reason. |
| `target`, `search` | What a protocol or the render's search says while it works: a record written again with its bulk link, a session read back, a workflow's status. |
| `http` | The calls made to OSDU with their status and duration, and each retry: why it is repeated (the status, a timeout, a transport failure) and how long it waits first. |

A run moves millions of records, and every line of its trace is streamed to the GUI and stored in the catalog, so what
one run writes is bounded by its allowances, never by its size. Every line the engine logs while it serves the run
passes one gate that applies them, whichever part of the engine logged it:

| Allowance | What the trace carries |
| --- | --- |
| The described records | The first 20 records the run sends or holds are described in full: the plan line, `Sending <record> to <id>` with what is sent and what an earlier try already did, each step the target answered and each call made for it (debug), and how it ended (`Delivered <record> as <id> version <n>` with the steps it took, or why it was held, failed or put back for a retry). Nothing is said about any other record on its own. |
| Each step's lines | Outside the described records, each step writes 100 lines at most (the batch lines, the calls made outside any record, the search lines, what a protocol says). |
| Problems | The run names 100 warnings at most: held, failed and retried records, drift a verify found, what a protocol warns about. The run's own warnings (`run`: a submission left open, a lease that could not be reclaimed, a member run that failed) take from that step's lines instead, so no number of record problems crowds them out. |
| Retries | The run names 50 retried calls at most. |
| Progress | Every 15 seconds for the first two minutes of a run, every minute up to an hour, every five minutes after that: a run of any length says it a bounded number of times, from its totals rather than per batch. |

Errors are always written. When an allowance runs out the trace says so once, so it shows where its lines of that sort
stop; the progress lines and the submission's counts keep counting what it leaves out. Nothing a trace leaves out is
lost: every record's attempts, with each step, status and correlation id, are in its history.

A URL on the trace never carries its query string, where a signed upload URL keeps its credential, and every message is
redacted before it is written.

## Runbook

| Symptom | Where to look | Action |
| --- | --- | --- |
| Submission `failed` with a validation message | The submission page; the run's trace | Fix the documents or the source rows, then run the flow again. |
| A record-scoped run plans nothing: the ingestion tables hold no row for its key | The run's trace names the record table and the key | Look at the pre and ingestion runs that load that table; a run scoped to the record reads it by key once they have loaded it. |
| The ledger disagrees with the ingestion tables (records without an arrival, a row changed or deleted that no run planned, a row gone from the table) | A record's Timeline tab; the flow's Records tab | Sync timeline for the record, or Sync timelines for the selection or the whole interface. It sends nothing; the flow's next run plans what it asked for. |
| Records `held` | The Records tab filtered to held | Read the last error. Fix the data (reference miss, empty key) or the mapping; then Release (one record, or all blocked). |
| Records `failed` | The record's page: the situation line under its header, and the failed dispatches on its Timeline tab | The retry budget is spent; the last error is redacted but specific. Release after fixing the cause. |
| Records `waiting` | The Records tab filtered to waiting; the record page says what it waits for | Each refers to a record of the ledger that has not landed. Nothing is charged and nothing is needed: they go out on their own when that record is delivered. When the record they wait for is held or failed, fix that one and release it. To send one as it is, with the reference pointing at nothing until the other lands, use "Send without waiting" on its page. |
| Records held with `refers to ... which neither the ledger nor OSDU's storage service holds` | The record's last error names the ids and the properties | The flow declares `target.verifyReferences: storage`, and the records it names are in neither the ledger nor OSDU. Deliver them (another flow, another system), or correct the mapping or the source rows, then Release. |
| Records stuck `delivering` | `Lease` on the record page in the past | A worker stopped mid-delivery. The flow's next deliver run (the recovered run, a re-run of the submission, or `drain`) waits out the lease, recovers it (applying what the stopped worker had sent) and sends the rest; nothing else to do unless a node is wedged. |
| Records show `delivering` while the run's trace says they were sent | The run's trace: its progress lines (`Delivering: <n> of <m> planned record(s) settled ...`) | Expected while the batch runs: a worker applies what it sent to the records at each renewal of its lease and when the batch closes, so the records trail the trace by at most one renewal. The attempts are there at once. |
| A run fails: `source.record.primaryKey` names a column that is not an identity column, or not the table's primary key | The error names the table and what the column lacks, with the statement that adds the key | A table SQLFlow created before its ing flow set `target.identityColumn` has no identity key (or a plain `RecId` column the setting added and nothing fills). Stop the table's loads, drop that plain column, run the `ALTER TABLE ... ADD [RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT ... PRIMARY KEY CLUSTERED` from the message (it rewrites the table), and run again ([documents.md](documents.md#the-identity-primary-key)). |
| A flow fails to load: `reliability.fanOut` needs `source.record.primaryKey` | The error names the flow file | Name the record table's identity primary key under `source.record.primaryKey`, or set `fanOut: 0`. |
| A run fails: the source database does not allow snapshot isolation | The error names the database and the statement | Run the `ALTER DATABASE ... SET ALLOW_SNAPSHOT_ISOLATION ON` it names, once, or declare `isolation: readCommitted` on the flow, and run again. Nothing was claimed. |
| A verify run reports drift | The Records tab with Drifted only | Decide whether the edit in OSDU was legitimate. Redeliver the record, or set `verify.reconcile: true` so verify runs queue redelivery. A newer version that changed only data keys other systems write (the flow's `preserveDataKeys`, or the run state External Data Services writes on a data job after a fetch) is not drift: the verify says so, on every route that writes records through storage or a manifest (all but ddms and fileAndDdms). |
| Records held with `... already holds row N with ..., which this record did not write` | The record's last error names the business object, the row and its key | Look at the row in DSPDM. If the flow is to maintain rows loaded before it, set `target.dspdm.existingRows: update` and Release; otherwise remove the row or correct the key in the source, then Release ([documents.md](documents.md#the-production-ddms-core-service)). |
| Records held with `the row cannot be saved in ...` or `DSPDM refused the row: ...` | The record's last error names each attribute and why | Fix the source rows or the mapping (attribute names, types, lengths, mandatory attributes), then Release. |
| A run on the dspdm route fails its preflight naming a business object | The run's trace | Name the business object and its key under `target.dspdm.businessObjects`, or give the business object a unique constraint in DSPDM's metadata. |
| Records on the etp route held with `carries no uuid`, `no Citation`, `does not file` or `stored unreachable` | The record's last error names what the XML lacks | The object's XML is what the Reservoir DDMS reads its identity from. Fix the source XML or the mapping that renders it (a root `uuid` and `schemaVersion`, a `Citation` directly under the root, a namespace and version the store files, and the `obj_` spelling for RESQML 2.0 and EML 2.0), then Release ([documents.md](documents.md#the-reservoir-ddms)). |
| Records on the etp route held with `names the array path(s) ...` or `declares the array(s) ...` | The record's last error names the paths | The object's XML and its `data.Arrays` must name the same array paths: the store refuses the commit of a transaction whose objects name arrays nobody supplied, or that leaves an array no object claims. Fix the mapping, then Release. |
| Records on the etp route held with `refused the commit of the transaction` | The record's last error carries the server's reason | A missing or orphan array or a dangling reference: the batch was rolled back and nothing landed. Fix the estate (deliver the object a reference names, or correct the arrays), then Release. |
| A delivery on the etp route waits, logging `is being written by another session` | The run's trace | The Reservoir DDMS allows one write transaction per dataspace. The route waits six times, up to two seconds apart, then fails the batch for a retry. Two flows writing one dataspace at once is the usual cause; give them dataspaces of their own, or run them one after the other. |
| A run on the etp route fails with `does not serve ETP protocol(s)` | The run's error names the protocols | The endpoint is an ETP server that does not serve what the route needs (Store, DataArray, Transaction, Dataspace, DataspaceOSDU, Discovery). Check `target.etp.path` and the deployment's version. |
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
| An API call answers 400 `Partition required` | The message lists the flow's partitions | The flow names several partitions: add `?partition=<name>`. |
| A run refuses to start: `no partition keeps the ledger` | The message names the records the flow's own ledger holds and the partitions they went to | The flow moved from its header to named partitions: mark the partition those records were delivered to `keepLedger: true`, so its ledger stays the one the flow kept. Nothing ran. |
| A flow fails to load: `the document declares interfaces, so these belong to an interface rather than the source` | The error names each misplaced key | Move `source.record`, `source.datasets`, `source.payloads`, `render.mapping`, `target.protocol` or `target.protocolOptions.payload` under the interface they belong to ([documents.md](documents.md#a-source-with-interfaces)). |
| The sync warns that a ledger is kept by two flows | The warning names the interface and the flow keeping it | An interface adopted the ledger (`ledger:`) of a flow the repository still holds. Remove the old flow, or the adoption; until then both deliver into one ledger. |
| A test `errored`: it does not fit the template of its kind | The Tests board marks it; its sheet lists every problem | A path that is not a variable of the kind (the nearest one is suggested), an operator that does not suit the variable's type, or a template not saved (`sqlflow template capture --kind <kind>`). Fix the flow document, sync, and run the test again. |
| A test's assertions `skipped`: the query matches more records than the test reads | The assertion's message names the count and `maxRecords` | Narrow the query, raise `maxRecords` (at most 1,000,000), or set `sample: true` to evaluate the first records. |
| A test `errored` with OSDU's answer | The result's error quotes the service's status and reason | A query the search service refuses (a leading wildcard, a query of nothing but `NOT`), a service down, or credentials refused. Fix the query or the service and run the test again. |
| An assertion run fails its platform run | The run page's **Open the report**; the run's error names the tests | The flow's `failRunOn` says which outcomes fail a run (`error` by default). Fix what the failing tests found, or the tests; run the tests not passing again from the report. |
| A test flips between passing and failing | The flow's History tab marks it | A test run in the same wave as the delivery reads an index still catching up (about thirty seconds): schedule the assertion flow after the delivery, as lineage orders it. Otherwise the data itself is changing between runs. |
| A failure has to be followed into OSDU's own logs | The record's Timeline tab: the dispatch, opened, names its correlation id, and a refused request's error quotes `(correlation-id ...)` | Give the OSDU operators that id: every request of the try carried it. |

## Metrics

The engine publishes its telemetry on the .NET metrics API, under the meter `SqlFlow.Delivery`:

| Instrument | Unit | Tags | What it measures |
| --- | --- | --- | --- |
| `osdu_delivery.records` | record | `flow`, `route`, `outcome` | Delivery tries settled: `delivered`, `unchanged`, `retry`, `held`, `failed`; and `waiting`, a record left waiting for a record it refers to, which is not a try and carries no duration. |
| `osdu_delivery.record.duration` | s | `flow`, `route`, `outcome` | How long a try of one record took, from its claim to its outcome. |
| `osdu_delivery.http.requests` | request | `method`, `host`, `result` | Call attempts to OSDU services and their storage, each counted once. `result` is the answer's status class, `2xx` to `5xx`, or what ended the attempt otherwise: `transport`, `timeout`, `refused` (the URL guard), `cancelled` (the node stopped waiting) or `error` (anything else, such as a redirect loop). |
| `osdu_delivery.http.request.duration` | s | `method`, `host`, `result` | How long an attempt took, its redirects and response body included, the wait before the next attempt not. |
| `osdu_delivery.http.retries` | retry | `method`, `host`, `reason` | Calls repeated after a passing failure: a status code, `transport` or `timeout`. |
| `osdu_delivery.probes` | probe | `flow`, `interface`, `outcome` | Target probes settled: `reachable`, `unreachable` (the service refused the call or did not answer), `error` (the probe could not run at all) or `cancelled`. Counted whether a schedule or an operator asked for it. |

They are rates to watch and alert on. The delivered, pending, held and failed counts the GUI and the CLI show are read
from the ledger, never from these. On a node, `dotnet-counters monitor --counters SqlFlow.Delivery -p <pid>` reads them
whatever else is configured.

**Where they go** is `Osdu:Telemetry:Exporter` (see [Configuration](#configuration)), and nothing leaves the process
until a deployment names one:

| Exporter | What it is for |
| --- | --- |
| `none` (default) | The metrics stay in the process, where `dotnet-counters` and any in-process listener read them. |
| `otlp` | The standard protocol: an OpenTelemetry collector, or any backend that speaks it directly (Grafana, Datadog, Honeycomb, Dynatrace, New Relic). This is the one that fits a deployment whose telemetry already goes somewhere. |
| `azuremonitor` | Application Insights, through Azure Monitor's own exporter, which is what this product's own Azure deployment runs on. |
| `console` | The process's own output, for a node an operator is watching. |

The exporter runs in the control plane and on every node, under one service name and one instance id, so a backend can
tell two replicas apart and add them up. A setting that carries a secret (the OTLP headers, the Azure Monitor connection
string) is a `${env:...}` or `${keyvault:...}` reference the host resolves, never a value in a configuration file, and a
reference with no resolver is refused at startup rather than exported to the wrong place.

Once they are exported, alert on a rising share of `held` or `failed` outcomes per flow, on `5xx`, `transport` and
`timeout` results per host, and on any `refused` result, which is a URL the guard would not let a node reach. A rising
`waiting` share says an estate is delivering children faster than the records they refer to; the flow's waiting count
in the GUI says whether they are moving.

## Watching the targets

A delivery flow's target is an OSDU that can stop answering for reasons no run reveals until one is due: a rotated
secret, a revoked entitlement, a gateway path that moved, a service taken down for maintenance. The GUI's **Probe
target** asks one interface's target whether it still answers under that flow's own credentials, and the same question
can be put on a schedule so nobody has to press it.

The schedule is off unless a deployment turns it on, and deliberately so: a pass costs a token exchange and one request
against a live OSDU for every interface it covers. Configuration is under `Osdu:TargetProbe` (see
[Configuration](#configuration)): `Enabled`, `IntervalMinutes` (default 15, never under 5), `Pipelines` to narrow it to
the flows that matter, `MaxPerPass` to bound one pass of a large estate, and `SettleSeconds` for how long a pass waits
for what it queued.

What a pass does, in order:

1. **Settles what is still open.** Every probe an earlier pass, or an earlier life of the host, left running is found
   again from the ledger, not from memory: a probe whose task has finished is recorded with its outcome, and one whose
   task the queue no longer holds is recorded as an error rather than left open forever. A restart loses nothing.
2. **Queues this pass's probes**, once per interface of each flow it covers, through the same path the operator's
   button takes (`delivery-probe` on a node, under the flow's own credentials), so a schedule and a button report the
   same thing. The task and the activity are recorded under the actor `service:schedule`.
3. **Waits up to `SettleSeconds`** for them, and records whatever has come back. The rest is settled by the next pass.

Every probe, scheduled or not, is an activity of kind `probe` in the ledger's audit trail, so the last result per flow
and interface is on the flow's Activity tab and in `sqlflow` alongside every other operator action, with who asked and
when. Each settled probe is also counted on `osdu_delivery.probes` (see [Metrics](#metrics)).

What to alert on: any `unreachable` outcome for a flow that is supposed to be delivering, and a run of `error`
outcomes, which says the probes themselves are not getting through (no node is taking the tasks, the flow file is not
on the node, a credential will not resolve) rather than anything about the OSDU.

A probe writes nothing to OSDU: it reads what the route's own probe path reads, which is the service's health or
version endpoint under the flow's target.

## Availability and recovery

The control plane runs one replica, and this section says exactly what that costs and how an operator gets back from
it. Everything below was read in the code on 17 September; the file and class are named wherever knowing them helps.

### Why one replica

`osdu/deploy/bicep/control-plane.bicep` pins `minReplicas` and `maxReplicas` to 1 (`main.bicep` passes
`controlPlaneMinReplicas` and `controlPlaneMaxReplicas`, both 1). The reason is the dispatch lease: one row named
`dispatch` in SQLFlow's catalog (`sqlflow/src/SqlFlow.Catalog/DispatchLeaseStore.cs`), acquired and renewed by one
conditional UPDATE. `DispatchService` (`sqlflow/src/SqlFlow.ControlPlane/Dispatch/DispatchService.cs`) tries for it
every `Dispatch:OwnershipRenewSeconds` (10 by default) and holds it for `Dispatch:OwnershipTtlSeconds` (30), and only
the replica holding it activates the `Dispatcher` (`sqlflow/src/SqlFlow.Dispatch/Dispatcher.cs`), which owns the run
queue and hands work to nodes. A replica that does not hold it answers every node protocol call 503
(`DispatchInactiveException`), and the node retries.

The lease is per estate, not per replica, so the code is already safe under more than one replica. What a second
replica **would** do today:

- Serve the whole API, including triggering runs. An enqueue is written to the catalog first and only then notified to
  the local dispatcher (`InProcessRunDispatcher`); on a replica whose dispatcher is passive the notify is a no-op and
  the owner's reconcile picks the row up within `Dispatch:ReconcileSeconds` (5).
- Fire schedules. `SchedulerService` runs on every replica and claims each occurrence by a compare-and-swap on the
  schedule's next fire, so an occurrence is enqueued exactly once however many replicas scan.
- Sync repositories. `RepoSyncService` claims each source's next sync the same way.
- Roll approved cache changes out. `CacheUpdateRolloutService`
  (`osdu/src/SqlFlow.Delivery.ControlPlane/Background/CacheUpdateRolloutService.cs`) is hosted on every replica; its
  batches are idempotent and its cursor only moves forward.
- Answer the autoscaler. `GET /api/v1/node/scale-target` is computed from the catalog by `ScaleTargetStore`, not from
  the dispatcher, so every replica answers the same number whether it owns dispatch or not.
- Take dispatch over when the owner goes. Within one TTL (30 seconds) after a crash, and at once after a graceful
  stop, because `DispatchService` releases the lease on shutdown instead of letting it lapse.

What a second replica **would not** do:

- Dispatch in parallel. One replica hands work out; a passive one holds no queue at all.
- Make hand-outs faster. The container app declares plain ingress with no session affinity, so a node's poll lands on a
  passive replica about as often as not and is answered 503; the node then backs off from 1 second up to 20
  (`sqlflow/src/SqlFlow.Node/RunWorker.cs`). Two replicas therefore slow hand-outs down. That is what the
  `maxReplicas` comment in `control-plane.bicep` records, and it is the whole of the case for one replica.

**This needs a decision (DEC-5 in [../../docs/go-live-map.md](../../docs/go-live-map.md)):** accept one replica with the
recovery below, or make the node protocol reach the lease holder (affinity does not do it, because affinity is per
client and the owner can move) and raise the cap. Nothing in the code forces either answer.

### While the control plane is down

- The API answers nothing, so the GUI (a separate image, still served) draws empty pages, and the CLI's remote verbs
  fail. `sqlflow run` on a workstation still works: a delivery run opens the module database itself and never calls the
  control plane, and it takes the ledger's own leases, which any number of nodes share safely.
- No run is queued and none is handed out. A schedule that should have fired does not fire, and is not backfilled
  afterwards unless the schedule declares `catchup: true` (`SchedulerService`; the loader defaults it to false).
- No repository sync runs, and no cache change rolls out.
- The worker pool is not scaled. KEDA's metrics-api rule in `osdu/deploy/bicep/worker.bicep` reads the replica target
  from `GET /api/v1/node/scale-target` on the control plane itself, so while the control plane is down the metric
  cannot be read at all, and the worker app's own `minReplicas` is 0. What the platform does with a scale rule it
  cannot read is Azure's behaviour and not this repository's: assume the pool does not grow, and confirm what it
  actually does on the target deployment (LIVE-4).

A worker node that is already running keeps working (`RunWorker`):

- It keeps executing what it holds and never exits. A failed poll is logged and retried with a jittered backoff from
  1 second to 20.
- A delivery in flight keeps delivering. The node reaches OSDU with its own credentials and the ledger with its own
  module database connection (`SQLFLOW_OSDU_DB`), neither of which passes through the control plane, so records keep
  being claimed, sent and settled and every attempt keeps being written.
- What a run needs from the control plane while it executes (the flow version, its lineage context) is retried for
  about half a minute (`DispatcherRetry.SupportWaits`) and then fails the run.
- The live trace stops reaching the GUI. `NodeTraceFeed` retries a batch for 60 seconds, then breaks the feed and
  keeps draining and discarding so the run is never stalled by it; the gap is filled from the run's own artifact when
  the run reports its outcome, so a run whose outcome never lands has no trace either.
- The outcome report is retried for about a minute (`DispatcherRetry.OutcomeWaits`). Past that the result never
  reaches the queue and the run is recovered by the dispatcher's lease expiry instead.

What an operator does meanwhile: check `/health/live` (no dependencies) and `/health/ready` (the catalog and every
module database) to tell an outage from a schema refusal, and **leave the worker nodes alone**. They are delivering,
and a stop or a restart costs the runs they hold their progress: a severed run records no outcome at all and is
recovered only by the lease expiry, which charges one of its three executions.

### Recovery, in order

The control plane recovers most of this by itself, in this order:

1. **Migrations and the version check.** `BootstrapProvisioningService` applies SQLFlow's catalog migrations and then
   every module database's, and stops the host when one is missing, behind or ahead of the build.
   `ModuleDatabaseVerification` carries the refusal on `/health/ready`. A control plane that will not come back after
   an image change is this check, not the outage.
2. **Dispatch ownership.** The replica that wins the lease activates and rebuilds the queue from the catalog
   (`Dispatcher.ActivateAsync`): every queued run is queued again, and every running run and compute task is leased
   back to the node that held it under a grace lease of `Dispatch:LeaseSeconds` (90), so a node still executing it
   reattaches on its next poll and loses nothing.
3. **Runs whose node did not come back.** The grace lease lapses and `Dispatcher.ExpireLeasesAsync` requeues the run
   while it has attempts left (`Dispatch:MaxExecutionAttempts`, 3) or fails it once they are spent. A requeued delivery
   run finds its submission already planned and its records still leased in the ledger; it waits the delivery lease out
   (`reliability.leaseSeconds`, 300 by default), recovers it, applies what the stopped worker had appended and sends
   the rest ([ledger.md](ledger.md#leasing)). Nothing is delivered twice.
4. **Queued runs.** Nothing expires a queued run: one queued before the outage is handed out as soon as a node polls.
5. **Compute tasks.** A probe, a read-back, a source read or a removal that was *queued* is failed when the control
   plane returns if it has waited longer than `Dispatch:TaskQueuedExpiryMinutes` (15), with "No worker claimed the task
   within N minutes"; the wait is measured from when it was enqueued, so a requeue does not reset it. One that was
   *running* is requeued and runs again, which is safe: a removal that runs again reports what has already gone as
   already gone and writes its own attempt.
6. **Schedules.** The next occurrence after now fires; the ones the outage covered do not, unless the schedule declares
   `catchup: true`, which fires one missed occurrence per scheduler tick (`ControlPlane:Scheduler:PollSeconds`, 15)
   until it is current. For a delivery flow this rarely matters: a deliver run plans from the watermark, so one run
   after the outage covers every row that changed during it.
7. **Repository sync.** Every source whose next sync fell due is synced on the first tick
   (`ControlPlane:ManagedSync:PollSeconds`, 30). Nothing is backfilled, because a sync reconciles state rather than
   replaying events.
8. **Cache rollout.** Each approved change resumes from its own cursor, so a rollout interrupted mid-way carries on
   where it stopped.

Then the operator's own pass, in this order:

1. `sqlflow db status --db <ref>` and `/health/ready`, before anything else. Exit 2 from `db status` means migrations
   are pending; a red readiness naming a module database means the schema and the build disagree, and the message
   names the migration or version.
2. **The dispatcher.** The GUI's Nodes page (`/nodes`, the Dispatcher panel) or `GET /api/v1/dispatch`: the owner and
   when it activated, the per-pool backlog, every queued run with the gate holding it back, and every lease with its
   node and expiry. `active: false` on the only replica means a dead incarnation still holds the lease; it lapses
   within the 30 second TTL on its own.
3. **The fleet.** The same page, or `sqlflow nodes`. A pool with a backlog and no online node is the scale rule not
   read yet; it recovers on the scaler's next poll. If the pool stays at zero with a backlog, the rule is still not
   being read: check that the control plane answers `GET /api/v1/node/scale-target?pool=<pool>` with the node-scope
   token the worker app presents.
4. **Runs.** The run list for anything failed with an interrupted message, and the fan-out family of every submission
   that was running (a run page names its root and slot). A submission left `running` with batches `queued` is drained
   by re-running the submission, or by triggering `drain` with its `submissionId` (see the Runbook).
5. **Records.** Each flow's Records tab filtered to `delivering`, where the lease on the record page is in the past.
   Nothing has to be done: the flow's next deliver run recovers them. If no run is due, trigger `drain`.
6. **Compute tasks.** Queue the probes, read-backs, source reads and removals that were failed with "No worker claimed
   the task" again.
7. **Schedules.** For a flow whose occurrence the outage covered and whose data has to be current before the next one,
   trigger it once by hand.

No step above needs a repair script or a hand edit of the ledger, and neither exists: every recovery path goes through
the ledger, which is what keeps a delivered record reconstructible.

## Retention and backup

The `osdu` schema is a live status store, not a reporting table. Traceability is the product, so what may be aged out
of it is exactly bounded: **every delivered record must stay reconstructible from the ledger alone.** That rules out
deleting a record, its submission, the cache version it was rendered against, or any audit row.

### What each table holds, and what may go

| Table | What it holds | Grows with | May be pruned |
| --- | --- | --- | --- |
| `osdu.Record` | The current state of one deliverable per flow: its custody, hashes, OSDU id and version, origin file and row. | Deliverables | **Never.** It is the state and the anchor of every trail. |
| `osdu.Attempt` | One delivery try, append-only: outcome, phase, error, steps, and the file and row it sent. | Tries | **Yes**, by age, and only where a later try of the same record exists. |
| `osdu.Activity` | The audit trail of runs and interventions: flow, kind, actor, times, parameters, outcome, summary, and the captured run log. | Runs and interventions | The **row: never** (it is the operator action the traceability rule keeps). Its `Log`: **yes**, by age, once the activity has finished. |
| `osdu.Submission` | One plan of a flow over its ingestion tables; a record points at the submission that planned it. | Plans | **Never** by this tool. |
| `osdu.WorkBatch` | One file of rendered documents of a submission, with its counts and outcome; an attempt names its batch. | Submissions, and batches inside them | **Never** by this tool: it belongs to its submission. |
| `osdu.Lease`, `osdu.RecordEvent` | A worker's live hold and what it has appended and not yet applied. | In-flight work only | **Self-clearing**: applying a lease deletes its events in the same transaction, and closing or recovering it deletes the lease. |
| `osdu.SourceWatermark` | One row per flow scope: how far the last whole-scope plan read. | Scopes | **Never**: it is state, and losing it re-reads everything. |
| `osdu.Retrieval` | One run of a retrieval flow: window, location, counts, outcome. | Retrieval runs | **Never** by this tool. |
| `osdu.AssertionRun`, `osdu.AssertionResult` | One run of an assertion flow's tests in a partition, and one result per test with its whole detail. | Test runs | **Yes**, by age, a run whole and only once a later result of the same test superseded every one of its results; a run holding a test's latest result stays. |
| `osdu.CacheVersion`, `osdu.CacheItem` | Every version of every partition's cache, and the records each version held (rows only for what changed, arrived or left). | Refreshes that changed something | **Never.** A delivered record's render context names the version it was rendered against, and the ledger has to be able to show what that version held. |
| `osdu.CacheMember` | That a cache flow's last capture held a record: current state, not history. | Cached records and the flows capturing them | Maintained by a refresh's merge and by the repository sync; not an operator's to prune. |
| `osdu.CacheSet`, `osdu.CacheSetEntry` | The distinct combinations of cached values renders consumed; a record points at its set. | Distinct combinations (a few thousand, not one per record) | **Never**: it is how a record says what it read. |
| `osdu.UpdateTag` | One cache change and how far its rollout has carried it. | Cache changes | **Never** by this tool. |
| `osdu.Mapping`, `osdu.CacheDefinition`, `osdu.Interface` | The read model of what the repositories declare. | Documents | Maintained by the repository sync. An interface a repository no longer declares is kept, inactive, so its records still lead to their flow. |
| `osdu.Template` | One saved schema version per kind and schema hash. | Saved versions | `DELETE /api/v1/delivery/templates` (author scope), refused with 409 while a synced mapping pins it. |
| `osdu.SchemaVersion` | One row: the module version, the last migration, when and by whom, and the minimum catalog migration. | Nothing | **Never.** |

Two things grow without bound and have a path to prune: **attempts**, and the **captured log on an activity**. An
activity row is small and bounded, but its log is up to 200,000 characters written once per run, which is the only
column in the schema with no ceiling on how much of it accumulates. SQLFlow sweeps its own run trace (`RunEvent`,
`RunStatement`) on a cadence of its own (`ControlPlane:RunTrace`); this log is the module's own copy of a delivery
run's log and had no sweep at all, which is why the retention pass clears it.

Everything else that grows is one small row per plan, per batch, per retrieval run, per cache change or per changed
cached record, and none of it is pruned, because each is part of a trail the ledger has to be able to show: a record
names the submission that planned it, an attempt names its work batch, a record's render context names the cache
version it was built against, a cache change is the reason a set of records went out again, and a retrieval flow's
watermark rests on its last completed retrieval row. **If volume ever demands it, partition those tables by time in the
model rather than delete from them** ([decisions/0005-ledger-retention.md](decisions/0005-ledger-retention.md)); that is
a decision the team takes with a measured table in front of it, not in advance.

### How to prune

One admin call, one cut-off, everything that may be aged out at that cut-off:

```bash
curl -sS -X POST "$CONTROL_PLANE/api/v1/delivery/ledger/prune" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"olderThanDays": 90}'
```

It answers `{"attemptsPruned": n, "activityLogsCleared": n, "assertionRunsPruned": n}`. `olderThanDays` below 1 is refused with 400, and the
route needs the `admin` scope: operating the estate is not administering it.

What it does, precisely:

- **Attempts.** Deletes tries started before the cut-off, but only where a later try of the same flow's record exists,
  so every record's last outcome stays explainable from the ledger alone. It deletes 4,000 per statement, oldest
  first, each statement its own short transaction, so a prune of years of history never holds a long lock on the table
  every drain appends to and never takes enough row locks for SQL Server to escalate to the whole table.
- **Activity logs.** Clears the `Log` of activities that started before the cut-off and have finished, 1,000 per
  statement on the same reasoning. The row stays with its flow, kind, actor, times, parameters, outcome and summary.
  An activity still running is left alone whatever its age, because its log is not written until it completes.
- **Assertion reports.** Removes finished assertion runs that started before the cut-off, each with all its results in
  one transaction, once a later result of the same test superseded every one of its results (a later outcome of a test
  that ran, or anything later of a test that was skipped). A kept report is so always whole, and a run holding a test's
  latest result stays however old, so the board always shows where every test stands.

It is safe to interrupt and repeat: every statement is its own transaction, and a second pass at the same cut-off
finds nothing left to take.

**Recommended retention: 90 days, weekly.** That is the window
[decisions/0005-ledger-retention.md](decisions/0005-ledger-retention.md) proposes for attempts, and the same cut-off
suits the logs, which are read while a run is recent and never after. Start there and widen it only for a deployment
that has a reason.

There is no CLI verb and no GUI page for the prune: the GUI's API client carries the call
(`osdu/gui/src/api/delivery.ts`) but no page uses it, and a schedule fires a flow rather than an API call. So the
weekly pass is a job of the estate's own scheduler (a cron job, an Azure automation task) holding an admin token, run
with a generous client timeout, because the first pass over years of history is the long one. Watch what it reports:
a pass that suddenly prunes far more than the last says a flow is retrying hard.

### What a backup must include

- **The whole `osdu` schema, with the SQLFlow catalog it belongs to.** By default the module has no connection of its
  own and the `osdu` schema lives in the catalog's database, so one backup covers both. Where a deployment gives the
  module its own database (`SQLFLOW_OSDU_DB`), the two must be restorable to the same instant: a run row in the
  catalog and the submission, records and attempts it wrote in `osdu` are one unit of work, committed on one
  connection and one transaction. Two backups taken at different times do not restore together.
- **`[osdu].[__EFMigrationsHistory]` and `[osdu].[SchemaVersion]`.** They are in the schema, so a schema backup has
  them, but a restore is only usable with a build that matches: a database behind the build is migrated on start, and
  one ahead of it stops the host by name. Restore the image that goes with the backup, or migrate forward. Never edit
  the version row.
- **The catalog's own migration history.** `SchemaVersion.MinimumCatalogMigration` names the oldest SQLFlow catalog
  migration the schema works with (`20260915212521_RunFanOutAndResult` for module version 1.8.0), and the hosts and
  `sqlflow db status` refuse to run against a catalog older than it. A restore that pairs a new `osdu` schema with an
  old catalog is refused rather than half-working.
- **`ALLOW_SNAPSHOT_ISOLATION` on the restored database.** The ledger reads under it. A restore from a backup keeps
  database options; a database rebuilt from scripts does not, and the first run fails naming the statement to run
  ([ledger.md](ledger.md#provisioning)).

What is not in the database and has to be restored beside it:

- **The repositories.** The flow, mapping and cache flow documents live in git; the catalog holds a synced copy and a
  repository sync rebuilds it. Restore the repository at the commit the catalog records.
- **The secrets.** Flows carry references only (`${env:...}`, `${keyvault:...}`), so the vault and the nodes'
  environment are their own backup and nothing sensitive is in a database backup to begin with.
- **The work locations and payload roots.** A work batch names a file under the flow's `source.work`; a restore that
  brings the database back without those files cannot drain the batches that were still queued. Plan those scopes
  again (`replan`); the records already delivered are skipped as unchanged.
- **The OSDU partition the ledger describes.** A backup is only meaningful beside the partition it was taken against.
  Restoring a ledger older than the partition it describes leaves records whose OSDU copy has since moved on: a verify
  run finds them as drift and the Drifted filter lists them.

## Size ceilings

Set the request body ceilings on the way to OSDU (the service's own server, the ingress, an API gateway) to one
deliberate number, and declare it on the flows as `reliability.maxRequestBodyBytes`: a bulk data file or a request
above it then holds its record before anything is sent, instead of failing after the record was written
([protocols.md](protocols.md#the-bulk-ceilings); [design.md](design.md) section 14.3 has the history). A 413 also holds
the record rather than creating a duplicate, but the ceiling still needs to be intentional, and the files the
pre-ingestion side produces sized under it. The control plane's own ceiling is `ControlPlane:MaxRequestBodyMegabytes`.

The identity index has a ceiling of its own, and it is per record rather than per request: one record contributes at
most 24 rows of at most 200 characters each, so a ledger of ten million records carries on the order of ten gigabytes
of index for a typical estate (eight values of a few dozen characters each). That is the price of finding a record by
any name it is known by. A mapping that declares many `dataset.identity` columns pays more of it; one that declares
none still gets its key, label, OSDU id and file.
