# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

This repository is a rebuild of OSDU Delivery as a dedicated solution on its own vendored copy of SQLFlow. The
previous implementation's history is not carried over here; `docs/plan.md` describes the stages of the rebuild and
`osdu/README.md` records what was copied from that implementation and what was deliberately left behind.

## [Unreleased]

### Added

- **Every capture of a partition's cache reads the partition's system properties, and a search asks regardless of case
  where the partition allows it.** A refresh, and a cache flow's plan, asks the indexer's and the search service's
  `GET /info` for the feature flags they report for the partition (`featureFlagStates`), and the refresh keeps them with
  the version (`[osdu].[CacheVersion].SystemPropertiesJson`, migration `CacheSystemProperties`, module version 1.11.0),
  tagged as system properties apart from the cached records ([osdu/docs/documents.md](osdu/docs/documents.md#cache-flow)).
  A service that cannot be asked fails nothing and changes nothing the cache knew of it; a property the engine relies on
  that no service reports is recorded as unknown, with the reason; a changed state writes a version and a failed read
  never does; an import keeps the current properties. Where the indexer reports `featureFlag.keywordLower.enabled` on,
  a search that finds no record exactly asks the `keywordLower` sub-field once, and takes its answer only when it is one
  record: an exact answer always wins, and several that match once case is ignored hold the record
  ([osdu/docs/mapping-templates.md](osdu/docs/mapping-templates.md#searches)). The render context of a mapping that
  searches pins the state of the properties its lookups rely on (`systemProperties`); a mapping that only searches pins
  them in place of a cache version, read from the version's row without its records, so a capture that changes
  reference data renders none of its records again. The OSDU cache page lists them on a System properties tab,
  `sqlflow cache list` prints them with the current version, and `sqlflow check` prints how a flow's searches are
  written.

- **A mapping can search the platform for the record a reference names, instead of reading it out of the cache.** A
  `searches:` block declares the kind searched and pins the saved template whose schema says how that kind is indexed,
  and an entry reads `search.<name>.id` found by `findBy` lines tried in order
  ([osdu/docs/mapping-templates.md](osdu/docs/mapping-templates.md#searches), decision
  [0009](osdu/docs/decisions/0009-searched-references.md)). Each lookup asks the search service, under the flow's own
  target, credentials and partition, for the one record whose property is exactly the value, with the query written the
  way the schema has the platform index the property. Exactly one record is the answer and none is a miss; several, a
  refused query, or a value that cannot be asked for hold the record whatever `required` says; a platform that cannot be
  asked fails the run. The render stays free of I/O: the plan asks a batch's questions once each and renders the
  waiting rows again, and answers are kept for the run. Fixtures declare the answers they assume and never search. The
  sample WellLog and WellboreTrajectory mappings find their wellbore by name and then by alias this way.
- **`SqlFlow.Delivery.Search` builds the query strings the OSDU search service receives, canonically.** Every rule is
  read from the indexer's and the search service's source: a string is asked through its `keyword` sub-field, a legacy
  `^srn` link and a value inside a flattened array through the property itself, a property of a nested array through
  the service's `nested(...)` form; and a value the index could never match (longer than the 256 characters the
  keyword keeps, the text `null` a property with no value is indexed as, a control character) or the service's own
  parser would misread (`nested(` anywhere; unbalanced parentheses or a word ending in AND, OR or NOT before a colon
  inside a nested query) is refused rather than sent.
- **The end-to-end suite runs a stand-in for the OSDU platform** (`osdu/gui/e2e/osdu-standin.mjs`), which answers a
  token and the wellbore searches the sample mappings make, and declares the OSDU references every process of the
  estate resolves, so the suite no longer depends on the shell that started it.

- **A central configuration the control plane supplies to the runs it queues.** `[osdu].[ConfigProperty]` (migration
  `CentralConfigProperties`, module version 1.10.0) holds a property for the whole control plane, or for one
  repository, which overrides it for that estate's flows. Every run of a delivery, cache or retrieval flow is queued
  carrying what its repository resolves to, and the node resolves `${env:NAME}` from that before its own environment,
  so an estate names where it delivers in one place instead of on every node. A reference the configuration does not
  name still comes from the node, so the two can be mixed. It is attached by decorating `IRunDispatcher`, which every
  run passes through, so a scheduled delivery and one someone pressed a button for resolve the same references. A
  property holds a non-secret value or a `${env:...}` or `${keyvault:...}` reference the node resolves, never a
  secret. `sqlflow config list | effective | set | remove`, and `GET/PUT/DELETE /api/v1/delivery/config`.

- **The OSDU flow kind owns where a record goes.** `dataPartition`, `aclOwner`, `aclViewer` and `legalTag` are
  properties of the kind, defaulting to `${env:OSDU_DATA_PARTITION}`, `${env:OSDU_ACL_OWNER}`,
  `${env:OSDU_ACL_VIEWER}` and `${env:OSDU_LEGAL_TAG}`, so a mapping declares what it fills and a flow no longer
  repeats them; a flow that names its own value under `render.parameters` still wins. The sample flows lost their
  `render.parameters` blocks entirely.

- **A record is found by what an operator holds, and its page shows the whole chain.** The ledger keeps an identity
  index (`osdu.RecordIdentity`, migration `RecordIdentityIndex`, module version 1.9.0): one row per value a record is
  known by, folded for comparison and kept as written for display, covering the columns a mapping declares as
  identities (`dataset.identity`), the source key and its columns, the words of the label, the OSDU id and its own
  part, and the ingestion file. One seek answers any of them across every flow, and each hit says which value matched
  and what it is. A staging rewrites a record's rows as a set, so a record is found by what it is now; records held
  before the index existed are filled in by a background pass.
- **The record's journey spans pre-ingestion, ingestion and OSDU.** `GET
  /api/v1/delivery/records/{flowId}/{key}/chain` names the runs that handled the record's ingestion file, read from
  the platform's own record of processed files, so the milestone strip and the timeline show where a row is in the
  whole estate rather than only in the delivery ledger. A file no run recorded says so instead of showing a blank.
- **Records** (Operate) in the GUI: a record is found from what an operator holds (a source key, a label, an OSDU
  id, a delivery key or an ingestion file name) across every flow, narrowed by status, without knowing which flow
  delivered it; the Delivery page carries the same field. The API behind it is
  `GET /api/v1/delivery/records?search=&status=`, the ledger's indexed lookup that the combined search already read.
- **The Records page opens on what the delivery system last took in or sent.** With nothing typed, the same route
  lists the ledger's most recently updated records across every flow, newest first, refreshed every ten seconds, and
  a status narrows that listing as it narrows a search; a row opens the record's journey exactly as a hit does. It is
  read from the end of a recency index (migration `RecentRecordsIndex`, module version 1.9.1: `IX_Record_UpdatedUtc`,
  and `IX_Record_Status_UpdatedUtc` for one state), so it costs what it shows rather than what the ledger holds, and
  it reaches back no further than the candidate bound the lookup already used.
- A record's page opens on its **journey**: a strip answering when the row was received (with the ingestion file
  and row), when it was planned, how many dispatches and how many failed, when it landed and as which version, what
  the last verify found and whether it was removed, over a timeline of every dated fact the ledger holds, oldest
  first, with every dispatch's phase, duration, worker, run, submission and origin, and every intervention with
  who asked for it. The header now names the file and row the record was received from.
- A submission's page undoes the batch it ran: **Remove what it delivered from OSDU** is one removal aimed at
  exactly the records that submission delivered, through the same removal dialog, with the count read the way the
  removal resolves it. The records filter gained `deliveredBy` for that set (`?delivered=` on a flow's Records
  tab), beside `submissionId`, which names the records a submission last planned and which a later submission
  moves on; the submission's page links to both sets.
- The repository shape: SQLFlow vendored under `sqlflow/` as a squashed git subtree, everything OSDU Delivery adds
  under `osdu/`, and `OsduDelivery.sln` building both. `tools/check-vendored-sqlflow.sh` names the SQLFlow commit
  `sqlflow/` was vendored from, lists every file changed here since, and fails when a commit mixes `sqlflow/` with
  other paths or when a line added to `sqlflow/` mentions OSDU or the delivery module.
- The OSDU module copied from the previous implementation into `osdu/`: the delivery, retrieval and cache flow
  kinds, the ledger, the protocols, rendering, templates and the mapping builder, the OSDU cache, the delivery and
  template endpoints, the `check`, `cache` and `template` CLI verbs, the GUI pages and their e2e specs, the sample
  estate, and the domain suites.
- `osdu/src/SqlFlow.Delivery.Data`: the module's EF Core context over the dedicated `osdu` schema, with its own
  migration history and schema version, so the ledger can be upgraded in production without touching SQLFlow's
  catalog.
- Three container images, all built from the repository root because the hosts span `osdu/` and `sqlflow/` and the
  GUI compiles the vendored SQLFlow sources in place: the control plane, a compute node and the GUI
  (`osdu/deploy/docker`).
- The deployment estate under `osdu/deploy`: Azure Container Apps via Bicep (the full estate, one template per
  tier, and the Entra app registration users sign in with), a docker compose stack, and Kubernetes manifests with
  a KEDA-scaled node pool. Nodes take work from the control plane's dispatcher with a node-scoped personal access
  token and open no catalog connection.
- `deploy-prod.bat` and `deploy-prod.ps1`: prod container deploys that build from a git archive of tracked files,
  verify each build by its ACR run id, deploy only what changed since the tag each app serves, and roll back an
  app that does not come up serving the new tag.
- Local development: `dev.bat` brings up the GUI and the control plane against a real estate after applying
  pending SQLFlow and OSDU migrations, and `osdu/tools/dev-setup.ps1` generates the git-ignored `.sqlflow/env` it
  reads from the live container app secrets.
- Continuous integration: the vendored-SQLFlow guard; `OsduDelivery.sln` built with warnings as errors (the SSH.NET
  advisory in vendored SQLFlow excepted until SQLFlow takes the fix) and every suite run against a SQL Server
  container; a check that the OSDU migrations write only to the `osdu` schema and SQLFlow's never touch it; both GUI
  trees built, the OSDU GUI linted and its end-to-end suite run; and the three images built, with the control plane
  and GUI images started and checked.
- Documentation under `osdu/docs`: the architecture of the module on SQLFlow, the environment and secret contract,
  and the OSDU-specific reference pages beside the vendored SQLFlow's own generic documentation.
- The OSDU Delivery hosts: the control plane, the worker node and the CLI, each composing SQLFlow with the module,
  which serves its delivery surface, CLI verbs and flow kinds over its own database.
- Delivery from ingestion tables: a delivery flow reads its records from keyed ingestion tables (a window, keyset
  pages by the table's identity key, child datasets, key slices and the ingestion fingerprint), fans out over the
  platform's run groups and keeps a watermark per flow scope. Each flow keeps a ledger of its own, so one ingestion
  table can feed several OSDU flows, and a source's interfaces are delivered in the waves their references wait for.
  The sample estate carries the pre-ingestion and ingestion flows that fill its tables.
- Routes to every OSDU ingestion path and DDMS, built from the services' pinned OpenAPI contracts and source code
  (`docs/osdu-coverage-plan.md`): storage, file, manifest (inline and by reference), dataset and the ingestion
  workflows; the Wellbore DDMS's nine collections through one `ddms` route; Well Delivery, Rock and Fluid Samples,
  the Production DDMS historian, Seismic Store and Reservoir Management as shapes of that route; Production DDMS
  business objects through `dspdm`; and checks of the records External Data Services reads. The suites check every
  request a route sends against its service's contract.
- OSDU flows in lineage, with the OSDU types, cache types and files on both sides, and each record's origin served
  from the ledger.
- Delivery metrics on the meter `SqlFlow.Delivery`: settled tries per flow, route and outcome, and every HTTP call
  attempt with its result, duration and retries (`osdu/docs/operations.md`).
- The sample estate covers three route types, each decided by what an interface declares: the source document now
  delivers documents through the file service and directional surveys with their stations through the Wellbore DDMS,
  beside the wellbores and well logs it already carried. Their schemas are the real ones, captured from the OSDU data
  definitions; `sqlflow template capture --out <file>` writes the bundled schema beside the mapping that pins it, so a
  repository carries what it pins and can import it again without the network.
- `osdu/docs/test-matrix.md`: for every route type, DDMS shape and engine area, the suite that proves it, what that
  proof rests on (a fake built from the service's own contract, or a real SQL Server), and what no suite proves.
- Every view of a source is about one of its interfaces (`osdu/docs/operations.md`): the GUI carries an interface
  picker whose choice travels in the URL, a source's Delivery tab lists its interfaces in the order a run takes them
  with each one's route, what it waits for and its counts, and the probe, the release and every removal act on the
  interface that is showing. A multi-interface source's Records and Submissions tabs could not be opened before this,
  because the API asks which interface a request is about and the GUI never said. The trigger dialog names the
  interfaces a run takes.
- `sqlflow records list` and `sqlflow records show`: an interface's records from the ledger, and one record with every
  try it took, its steps and its errors, from a terminal or a node with no control plane to reach. The source key finds
  a record as surely as the delivery key.
- The `etp` route, which writes Energistics data objects into dataspaces of the Reservoir DDMS over ETP 1.2 on a
  WebSocket instead of through an OSDU service (`osdu/docs/documents.md`, `osdu/docs/protocols.md`). The client is the
  module's own: the messages and data types it uses are C# records written from the pinned protocol, and a test
  round-trips every one of them against a codec driven by that same file. An object's identity is read out of its own
  XML and checked before a session opens, a dataspace is created only when it is missing and with the record's own ACLs
  and legal tags, a batch's objects and arrays go inside one transaction per dataspace, an array too large for a message
  is declared and then filled slice by slice, and a refused commit is rolled back. No dataspace is ever deleted, because
  the server purges its OSDU record when one is.
- Records that wait for records: a record whose document refers to a record the ledger holds and has not delivered is
  left waiting by the claim, which charges nothing, and goes out when that record lands. Waits are decided under one
  lock of the ledger and never lead back to the record deciding, so two records never wait for each other; an operator
  can send one as it is. `target.verifyReferences: storage` asks OSDU's storage service about the ids the ledger does
  not hold and holds a record that would write a dangling reference.
- `docs/go-live-map.md`, the checklist from here to production, and an inventory of every id a live OSDU test creates,
  with the rule that no live test runs without approval (`CLAUDE.md`).
- A scheduled target probe: every active delivery flow's OSDU is asked whether it still answers, once per interface,
  through the same node operation the operator's "Probe target" queues. Each probe is an activity of kind `probe` in
  the audit trail and a count on `osdu_delivery.probes`; a pass settles what an earlier pass or an earlier life of the
  host left open, so no probe stays open and a restart loses nothing. Off unless `Osdu:TargetProbe:Enabled` says
  otherwise, because a pass costs a token exchange and a live request per interface.
- `sqlflow records release`: an operator on a node releases a flow's held, failed and removal-marked records back to
  pending, the whole interface or the keys given, without a control plane to reach.
- What one control plane replica costs and how an operator recovers from its absence, and the ledger's retention and
  backup policy: what every table of the `osdu` schema holds, what grows, what may be pruned and what never may
  (`osdu/docs/operations.md`, `osdu/docs/decisions/0005-ledger-retention.md`). The retention pass now clears the
  captured run log of settled activities as well as aging out attempts, and answers both counts.
- Generic extension points in the vendored SQLFlow, each in a `sqlflow:` commit: a registered flow kind describes its
  files and datasets in lineage (anchored at the flow's folder, bounded to the catalog's widths, and swept once no
  declaration names them), a flow's file selection is read from its stored definition, a search contributor can
  assert the result contract, and a link can set the pipelines page's repo and kind filters.

### Changed

- **The sample cache flow no longer captures wellbores.** They were 163,818 of the 165,381 records the `dev` cache held,
  and they are what the sample mappings now search for. The next refresh after the sync drops the type from the
  partition's cache, as it drops any type no synced flow declares; `osdu/samples/cache-records/Wellbore.json` is gone,
  since an import refuses a file for a type the flow does not declare.
- **A delivery flow's lineage reads the kind its mapping searches**, so the flow that delivers those records to the
  partition is ordered before it, as the cache flow used to order it. A search source's `findBy` lines no longer
  count as reads of a cache type of the same name.
- **The mapping builder carries searches, search entries, fixture answers and `dataset.identity` through a round
  trip.** A mapping opened in the builder and written back kept none of them before: a search source came back as a
  cache source, and a mapping's identity columns were dropped. The GUI edits a search entry beside a cache entry.

- **The cache captures every field a reference record is named by, and a lookup matches on all of them.** A
  reference-data record is named by its code, its name or its own `ID`, and a source may use any of the three. The
  cache flow captured `data.ID` for `UnitOfMeasure` alone, and no mapping matched on it; the other three reference
  types captured only `Code` and `Name`. All four now capture `data.Code`, `data.Name` and `data.ID`, which is the
  same set the working implementation's loaders request, and every `findBy` offers all of them as alternatives,
  except where a partition carries no `data.ID` at all: a capture reported that of `LogCurveBusinessValue` and
  `VerticalMeasurementType`, so those two declare the code and the name alone rather than an empty path that would
  be reported on every nightly run. A
  wellbore is matched by `FacilityName` or by any of its aliases, which is what the `Alias` field was captured for
  and what nothing had used. A source spelling a unit `m`, `metre` or by its id now resolves the same record
  instead of holding the row.

- **The sample cache flow reads a wellbore's aliases from the property the schema actually has.** It declared
  `data.NameAlias.AliasName`, and `master-data--Wellbore` has no `NameAlias`: the property is `NameAliases`, an array
  of `AbstractAliasNames` whose items carry `AliasName`. Nothing was ever cached under `Alias`, which a capture
  reported and which nothing yet read, so it cost nothing until the first lookup by alias failed to match for no
  visible reason. Corrected in the sample estate and in the four documents that taught the path.

- **The sample estate is written for the `dev` partition.** It used `opendes`, OSDU's own documented example
  partition, which read as a real destination in a repository whose estates deliver to `dev`. Every occurrence
  outside `osdu/specs` is now `dev`: the mapping fixtures and the records they pin, the sample cache records, the
  suites, the docs and the walkthroughs. `osdu/specs` keeps `opendes` because those are OSDU's own OpenAPI documents
  and integration briefs, and this project verifies platform behaviour against them as the vendor wrote them.
  The sample cache records now carry `dev:` ids, which makes one mistake easier to make and so louder to find: they
  are made-up records, and importing them into an estate that delivers for real puts ids into its cache the platform
  may never have held. Their README says so first.

- **A cache is keyed by the partition a flow reaches, not by the text its document spells it with.** An estate whose
  documents name their partition `${env:OSDU_DATA_PARTITION}` was keying its cache by that literal string: a capture
  under the real partition could never be read, two estates sharing a variable name but delivering to different
  partitions would have shared one cache, and two documents naming one partition differently would each have needed
  their own copy of identical reference data. The partition is now resolved wherever a cache is keyed: the render's
  read, a refresh's capture, an offline `sqlflow cache import`, and the catalog sync that records a partition's
  declarations. The sync resolves once per document and keys everything from that, and a control plane that cannot
  resolve a reference records it as written rather than failing the sync.

- **The sample estate stops asking for an API Management key.** Its flows sent
  `Ocp-Apim-Subscription-Key: ${env:APIM_KEY}` on every request, which is an Azure API Management gateway key and
  nothing to do with OSDU: a platform reached directly takes a bearer token and no such header. The estate demanded a
  credential its target does not use, so a run failed resolving a reference no one could supply. The header is gone
  from the flows, the cache flow, the deploy templates and the docs. Redaction still covers it: a flow that does sit
  behind a gateway and sets the header keeps it out of logs and off redirects, which the platform's own suites assert.

- **An estate names where it delivers, rather than writing it into its documents.** The partition a flow delivers
  to (`target.headers.data-partition-id` and the `dataPartition` render parameter), the entitlements groups every
  record is owned and readable by, and the legal tag it carries are all `${env:...}` references the node holds:
  `OSDU_DATA_PARTITION`, `OSDU_ACL_OWNER`, `OSDU_ACL_VIEWER` and `OSDU_LEGAL_TAG`, beside the `OSDU_*` references
  already there. The sample mappings declare `aclOwner`, `aclViewer` and `legalTag` and fill `osdu.acl.owners`,
  `osdu.acl.viewers` and `osdu.legal.legaltags` from them through `{param....}`, so one set of documents serves
  every partition an estate runs against. A flow's own render parameters are resolved before the render context is
  built, so the context, the rendered hash and the ledger row hold the value that reached the record, never the
  reference; a mapping's own default stays literal. A cache is still scoped by the partition its flows declare, as
  written, which is how lineage already identifies a platform, so a cache flow and the delivery flows reading its
  cache agree by naming the partition the same way. Deployments wire the four new references (compose, k8s), and a
  node that has not gained them fails the run naming the one it is missing.

- **The sample estate names its OSDU endpoint `${env:OSDU_URL}`.** The reference was `${env:PETRODB_URL}`, named
  after the facade an earlier estate delivered through, which read as a dependency the module does not have: the
  variable holds the OSDU API base a flow's `target.endpoint` (or a cache or retrieval flow's `source.endpoint`)
  resolves, and it now sits with the `OSDU_*` references beside it. A deployment renames the variable on its nodes
  when it takes this build; a flow document that still says `${env:PETRODB_URL}` resolves whatever a node holds
  under that name, so the two can be moved separately.

- The current build has been run against a live OSDU: Azure Data Manager for Energy 0.29, partition `dev`, on
  2026-09-17. The storage, file, manifest and ddms routes each delivered and were read back, verify and reconcile were
  exercised, and every id created was removed at the reversible scope with a GET answering 404
  (`osdu/docs/osdu-testing.md` section 0). Six defects it found are fixed: a cache capture now ends a reference type
  when its pages stop bringing anything new (this deployment hands back the same search cursor for every page); a bulk
  chunk whose pandas entry no dataframe reader can read is held before it is sent, and the sample estate and every
  fixture write the whole entry; a session whose chunks carry the reference curve as the row index rather than as a
  column is held before the session is opened, because the service accepts every chunk and then refuses the commit; and
  the sample source's wellbores and welllogs interfaces declare the child datasets their mappings repeat, with a suite
  that reads every committed delivery document and checks it against the mapping it names.

- Data reaches OSDU through SQLFlow's own flows: a pre-ingestion flow lands the source files, an ingestion flow
  loads the keyed ingestion tables, and the OSDU flow reads those tables and delivers. Lineage orders the three.
- Image, Container App and Kubernetes resource names carry the product (`osdu-delivery-*`), because the vendored
  SQLFlow ships its own `sqlflow-*` images from its own deployment assets and the two are different artifacts.
  Project, namespace, binary and environment-variable names stay `SqlFlow.*` and `SQLFLOW_*`.
- The ledger's concurrency: a worker keeps its writes off the record table with one lease row and an event log, so
  many nodes write the ledger without locking each other out, and the cache tables are clustered by partition, so
  partitions refreshing together never deadlock.
- A delivery no longer refuses a flow whose record key column is declared nullable, which every table SQLFlow's
  ingestion creates is; opening the source probes the run's own scope for records whose key is actually unknown.
- The production deploy scripts name the estate they deploy to instead of carrying one.
- The repository is licensed under GPLv3, matching the SQLFlow it is built on.

### Removed

- The previous implementation's drop path: the drop manifest and its encodings, the drop reader, the scope file
  readers, the replica, the SQL source extraction, inline drops, known-state publishing and the drop-off area,
  together with their tests, documents and deployment settings. SQLFlow's pre-ingestion and ingestion flows
  replace them.
- Manual submission: records reach OSDU only through the regular flows, and records delivered by hand are files
  placed where a pre-ingestion flow reads them. The SQLFlow extension point it alone used, a run group enqueued
  together with rows of its own, went with it.

### Fixed

- A cache flow's `plan` operation read the partition's cache under the text its `data-partition-id` is written with,
  so a flow naming its partition `${env:...}` planned against a cache no capture writes to; it now resolves the
  partition as a refresh does.
- `sqlflow cache list` resolves the partition it is given, as a partition reference or through a cache flow's file,
  before reading the versions, so it lists the cache a capture of that partition writes rather than none.

### Security

- The delivery nodes check every address a host name resolves to, and every redirect hop, before connecting: cloud
  metadata and link-local addresses are never reached, loopback only when the deployment allows it, and private
  ranges only when it lists them in `SQLFLOW_DELIVERY_PRIVATE_NETWORKS`. A redirect to another host carries no
  credentials, and one from https to http is refused.

[Unreleased]: ./
