# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

This repository is a rebuild of OSDU Delivery as a dedicated solution on its own vendored copy of SQLFlow. The
previous implementation's history is not carried over here; `docs/plan.md` describes the stages of the rebuild and
`osdu/README.md` records what was copied from that implementation and what was deliberately left behind.

## [Unreleased]

### Added

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
- Records that wait for records: a record whose document refers to a record the ledger holds and has not delivered is
  left waiting by the claim, which charges nothing, and goes out when that record lands. Waits are decided under one
  lock of the ledger and never lead back to the record deciding, so two records never wait for each other; an operator
  can send one as it is. `target.verifyReferences: storage` asks OSDU's storage service about the ids the ledger does
  not hold and holds a record that would write a dangling reference.
- `docs/go-live-map.md`, the checklist from here to production, and an inventory of every id a live OSDU test creates,
  with the rule that no live test runs without approval (`CLAUDE.md`).
- Generic extension points in the vendored SQLFlow, each in a `sqlflow:` commit: a registered flow kind describes its
  files and datasets in lineage (anchored at the flow's folder, bounded to the catalog's widths, and swept once no
  declaration names them), a flow's file selection is read from its stored definition, a search contributor can
  assert the result contract, and a link can set the pipelines page's repo and kind filters.

### Changed

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
