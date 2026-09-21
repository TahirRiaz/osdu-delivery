# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

This repository is a rebuild of OSDU Delivery as a dedicated solution on its own vendored copy of SQLFlow. The
previous implementation's history is not carried over here; `docs/plan.md` describes the stages of the rebuild and
`osdu/README.md` records what was copied from that implementation and what was deliberately left behind.

## [Unreleased]

### Added

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

### Security

- The delivery nodes check every address a host name resolves to, and every redirect hop, before connecting: cloud
  metadata and link-local addresses are never reached, loopback only when the deployment allows it, and private
  ranges only when it lists them in `SQLFLOW_DELIVERY_PRIVATE_NETWORKS`. A redirect to another host carries no
  credentials, and one from https to http is refused.

[Unreleased]: ./
