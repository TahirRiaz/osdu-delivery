# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
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
