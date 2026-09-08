# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- The delivery domain as the platform's one flow kind (`src/SqlFlow.Delivery`): `flowType: delivery` documents with
  pinned mappings, schema and reference snapshots kept in the flow's repository, the drop reader, the mapping
  renderer with the preflight gate, two-tier change detection, the OSDU record and well log protocols, and the
  lease-and-retry worker.
- The ledger in the catalog's `delivery` schema: submissions, records, append-only attempts, source watermarks,
  the audit trail of runs and interventions (with the requesting user and the run id), and the mapping and
  snapshot read models the repository sync writes.
- The delivery API under `/api/v1/delivery`: the manifest notification, per-flow stats, indexed record search,
  submissions, record history, the audit trail, mappings and snapshots, and the interventions (release,
  redeliver, verify, read back, delete, probe); the compute task read API under `/api/v1/compute/tasks`.
- The GUI delivery pages: the delivery overview, the per-flow Delivery, Records and Submissions tabs, the record
  page with its history and actions, the submission page, the audit trail, and the mappings and snapshots page.
- The CLI verbs `sqlflow check` and `sqlflow snapshot`, and the delivery run options on `run` and `trigger`
  (`--operation`, `--force`, `--set`, `--drop`, `--submission`, `--record`, `--publish-to`).
- The sample estate `samples/recall-welllog`, the drop generator `tools/SampleDrop`, and the delivery test suite.
- Runs record who requested them and the delivery counts they produced; the request body ceiling is an explicit
  setting (`ControlPlane:MaxRequestBodyMegabytes`).
- Schedules carry the operation they fire (`schedule.operation` in the flow, `operation` in the schedule library,
  `--operation` on `schedules create`), so a nightly verify pass is a schedule.
- The search box answers from the ledger as well: a delivery key, or an OSDU id, source key or label prefix,
  across every flow, from indexed columns.
- `source.knownState` declares where a known-state run publishes when the run names no location.

### Changed

- Per-run parameters are delivery operations (deliver, verify, plan, known-state) with force, flow parameter
  values, an explicit drop, a submission to re-run, a record scope and a publication target; the SQL-era backfill
  parameters are gone from the API, the CLI, the run row and the GUI.
- Schedule run-now takes `force` instead of a backfill window.

- Forked from SQLFlow V3 (commit `ddd4ea12`) as OSDU Delivery and stripped to the platform: the control plane
  (auth, users, tokens, repos with managed git sync and proposals, the catalog, schedules, the run queue, nodes
  and worker pools, notifications, maintenance, activity, search), the compute node, Azure integration, the
  CLI, and the GUI workbench shell (dashboard, runs, nodes, repos, pipelines, schedules, search, users, tokens,
  notifications, maintenance).
- Flow kinds are now registered through `IFlowDocumentKind`, executed through `IFlowDocumentExecutor`, and
  compute tasks through `IComputeOperation`; the platform never references a concrete kind. Every document
  carries the same headers (name, kind, batch, source and target reference, credential references, whether it
  needs the repository tree).
- The run trace is the run events alone, paged and streamed the same way as before.
- Run groups skip every member queued in a later wave when a member fails.
- The catalog migration history was squashed into one `Initial` migration.
- The product name shown in the GUI, the CLI, notifications, and the deployment templates is OSDU Delivery;
  the `SqlFlow.*` project, namespace, binary, image, and environment-variable names are kept.

### Removed

- The SQL-era reference pages (architecture and execution modes, CLI conventions, connections and secrets, the
  reference copy of the environment variables, flow identity, run artifacts, the shadow catalog, getting started)
  and the reference manifest tooling; the platform pages that remain (the CLI verbs, the control plane,
  authentication, deployment, notifications) are rewritten for the delivery platform.
- Every SQL Server ETL flow kind and engine (ingestion, export, stored procedures, health checks, acquisition,
  copy, SFTP, translate, batch, calendar), the source readers and DuckDB, the foreign database providers,
  database-object lineage and schema snapshots, datasources and discovery, data streams, insights, the chat
  assistant, the MCP server, the Slack bot, and their GUI pages, samples, schemas, docs, and deployment assets.
- The SQL statement trace, run files, run assertions, surrogate keys, and health-check metrics, and the
  assertion-failed notification kind.

### Fixed

- The device approval page forwards the session's `accessToken`; approving a CLI sign-in from the page sent an
  undefined bearer token before.
- The compose stack lets bootstrap create its catalog (`ControlPlane__Bootstrap__AllowCreate`), so the first
  `docker compose up` becomes ready; the Kubernetes and Bicep notes say when the database must exist beforehand.
- Folder validation redacts the error text of a broken document, as single-file validation does.
- The `sqlflow db sync` summary punctuation, the `schedules run` hint (it names `runs trace`), and the shell
  completions, which list the delivery run options instead of the removed backfill flags.

[Unreleased]: ./
