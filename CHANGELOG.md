# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

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

- Every SQL Server ETL flow kind and engine (ingestion, export, stored procedures, health checks, acquisition,
  copy, SFTP, translate, batch, calendar), the source readers and DuckDB, the foreign database providers,
  database-object lineage and schema snapshots, datasources and discovery, data streams, insights, the chat
  assistant, the MCP server, the Slack bot, and their GUI pages, samples, schemas, docs, and deployment assets.
- The SQL statement trace, run files, run assertions, surrogate keys, and health-check metrics, and the
  assertion-failed notification kind.

[Unreleased]: ./
