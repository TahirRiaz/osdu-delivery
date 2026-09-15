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
- Continuous integration: the vendored-SQLFlow guard, `OsduDelivery.sln` built and tested, and both GUI trees
  built with the OSDU GUI also linted. The DB-backed suites skip in CI, which sets no `SQLFLOW_TEST_DB`.
- Documentation under `osdu/docs`: the architecture of the module on SQLFlow, the environment and secret contract,
  and the OSDU-specific reference pages beside the vendored SQLFlow's own generic documentation.

### Changed

- Data reaches OSDU through SQLFlow's own flows: a pre-ingestion flow lands the source files, an ingestion flow
  loads the keyed ingestion tables, and the OSDU flow reads those tables and delivers. Lineage orders the three.
- Image, Container App and Kubernetes resource names carry the product (`osdu-delivery-*`), because the vendored
  SQLFlow ships its own `sqlflow-*` images from its own deployment assets and the two are different artifacts.
  Project, namespace, binary and environment-variable names stay `SqlFlow.*` and `SQLFLOW_*`.

### Removed

- The previous implementation's drop path: the drop manifest and its encodings, the drop reader, the scope file
  readers, the replica, the SQL source extraction, inline drops, known-state publishing and the drop-off area,
  together with their tests, documents and deployment settings. SQLFlow's pre-ingestion and ingestion flows
  replace them.

[Unreleased]: ./
