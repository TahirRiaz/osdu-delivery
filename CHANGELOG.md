# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `subscribers.<name>.notes`: free-form remarks about a consumer's STATE (stale, superseded,
  unopenable, or an incomplete dataset naming what could not be resolved), kept apart from
  `description`, which says what the consumer is for. Stored unbounded in `catalog.Subscriber`,
  searchable from `GET /lineage/subscribers`, shown in the subscriber list and details.
- `subscribers.<name>.url` (the report URL or workbook path, which already existed) is now searchable
  and shown in the subscriber list and search results. A non-http location renders as plain text
  rather than a link that silently does nothing when clicked.
- Search ranks the warehouse above the reporting layer: `objects` leads every result set and
  `subscribers` trails it, in the API payload, the workbench, and the MCP follow-up plan, because a
  bare term is far more often a table or a column than the name of a report.
- Subscribers are now a surface of the global search (`GET /search/subscribers` and the `subscribers`
  category of `/search/all`), matched on name, type, owner, description, notes, and declaring file.
  A subscriber is neither a database object nor a flow, so searching a report by name previously
  returned nothing and the consumer looked absent rather than unsearched.
- Editor support for subscriber libraries: hover, key completion, and unknown-key diagnostics,
  driven by a new `docs/reference/flow/keys.subscribers.json` key model. The analysis engine
  detects a library by its root `subscribers:` key rather than by file name.
- Initial v3 rebuild scaffold on .NET 9.
- Stateless engine (lightweight mode): infer source schema → introspect target → diff →
  generate DDL → bulk load.
- CSV source reader with type inference.
- SQL Server provider: schema introspection, DDL generation, `SqlBulkCopy` loader, CLR→SQL type
  mapping.
- YAML pipeline loader with mapping/validation.
- `sqlflow` CLI: `validate`, `plan`, `run`.
- Open-source project scaffolding: license, contribution guide, code of conduct, security policy,
  CI, and central package management.

[Unreleased]: https://github.com/sqlflow/sqlflow/commits/main
