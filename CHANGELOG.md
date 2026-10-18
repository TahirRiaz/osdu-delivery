# Changelog

All notable changes to OSDU Delivery are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.1] - 2026-10-19

Module schema 1.46.0 and SQLFlow `fe87877c`, as in 1.0.0; the one change to `sqlflow/` since is to its tests
([docs/sqlflow-changes.md](docs/sqlflow-changes.md)).

### Changed

- **A local start installs the pre-push tests.** In a git clone with no hooks path set, a start of `dev.bat` or
  `./dev.sh` points git at `tools/git-hooks`, whose pre-push runs the unit tests (`bash tools/test.sh`) before every
  push; `git push --no-verify` skips it once and `git config --unset core.hooksPath` removes it
  ([CONTRIBUTING.md](CONTRIBUTING.md#tests)).
- **A start without .NET offers to install it, on every platform.** `dev.bat` offers winget, and `./dev.sh`
  Homebrew on a Mac and Microsoft's `dotnet-install.sh` into `~/.dotnet` on Linux; then the setup is built from the
  checkout as on any start ([osdu-delivery-setup](osdu/docs/reference/cli/dev.md)).

### Removed

- **The setup's binaries.** A release no longer carries `osdu-delivery-setup` for each platform, and the launchers
  no longer download one: they build it from the checkout, in seconds, once .NET is there. A release is its tag and
  its notes ([CONTRIBUTING.md](CONTRIBUTING.md#releases)).

## [1.0.0] - 2026-10-18

The first release of OSDU Delivery, powered by SQLFlow: subsurface data delivered into an OSDU platform by
configuration rather than code, with every record traceable in a ledger. The [README](README.md) says what it is and the
[reference documentation](osdu/docs/reference/README.md) describes what it does. This log starts at this release.

### Added

- **Delivery as configuration.** A pre-ingestion flow lands source files, an ingestion flow loads them into a table
  keyed by record with the source file and row of each row, and an OSDU flow delivers from that table; lineage orders
  the three. A mapping turns one row into one record of a saved OSDU template, with lookups, expressions, conditions and
  business rules ([flow/mapping.md](osdu/docs/reference/flow/mapping.md)).
- **Every OSDU write path.** The Storage, File and Dataset services, manifest and workflow ingestion, and the Wellbore,
  Seismic, Rock and Fluid Samples, Well Delivery, Production and Reservoir domain data stores, with their files and bulk
  data ([docs/osdu-coverage-plan.md](docs/osdu-coverage-plan.md)).
- **A cache per data partition.** Reference data captured from OSDU, lookup tables from files, databases and ingestion
  tables, and dictionaries kept in the repository, held as versions that mappings read
  ([concepts/partition-cache.md](osdu/docs/reference/concepts/partition-cache.md)).
- **Only what changed is sent.** The same source row always becomes the same OSDU record, a run sends only the records
  that render differently, and a change of mapping, template or cache sends exactly the records it affects
  ([concepts/change-detection.md](osdu/docs/reference/concepts/change-detection.md)).
- **The delivery ledger.** Each record's source row, the mapping, template and cache versions that rendered it, every
  attempt and what OSDU answered, the OSDU id and version it landed as, and every operator action with who and when;
  statistics count from it, and a record is found by its key, OSDU id or source file
  ([concepts/ledger.md](osdu/docs/reference/concepts/ledger.md)).
- **Checked before sending.** `sqlflow check`, `preview` and `plan`; mappings are checked against their schema, legal
  tags are confirmed with OSDU, and each document is validated before it goes out, its verdict recorded.
- **Safe when things fail, and correctable.** A run resumes from its last recorded step, problems that need a person are
  held for release, and a delivery that fails halfway is undone. Records are redelivered, removed, restored to their
  previous version, or a whole run reversed, each through the ledger
  ([concepts/removal-and-reversal.md](osdu/docs/reference/concepts/removal-and-reversal.md)).
- **After delivery.** Verify and reconcile against OSDU, inventory flows that find orphans and records OSDU lost,
  retrieval of OSDU records into files, dimension flows, data quality tests, and an explorer of what OSDU holds.
- **The GUI and the command line.** The workbench pages for records, flows, the cache, mappings, tests and the audit
  trail, a mapping builder and a YAML editor that documents and checks every document, and `sqlflow` verbs for the same.
- **A local run set up by answering questions.** `dev.bat` (Windows) and `./dev.sh` (macOS, Linux) set a machine up on
  its first start: `osdu-delivery-setup` asks for the SQL Server (starting SQL Server 2022 in Docker where there is
  none), the OSDU platform and its client, and a flow repository, writes `.sqlflow/env` from `dev.env.example`, creates
  the databases and registers the partition. Every start then reports each tool, setting and database with its fix,
  and stops only when a start would fail. The release carries the setup for Windows, macOS and Linux, so a machine
  without .NET runs it too and is offered the install ([Run it locally](README.md#run-it-locally),
  [osdu-delivery-setup](osdu/docs/reference/cli/dev.md)).
- **An AI assistant.** `osdu-delivery-mcp` serves Claude, Cursor, VS Code, Codex, Slack and the GUI's chat from metadata
  and documentation, and answers questions over data only through AI contexts an admin approves
  ([guides/mcp.md](osdu/docs/reference/guides/mcp.md)).
- **Deployment.** A control plane, workers that fetch their work, the GUI and the MCP server, over two SQL Server
  databases, with Docker Compose, Kubernetes, Bicep, Terraform and Radix templates; dev, test and prod environments
  deployed from GitHub, each with its OSDU partitions in its configuration
  ([osdu/deploy/README.md](osdu/deploy/README.md)).
- **Ready to use when a deployment ends.** The Terraform (and its Bicep mirror) sets the whole Azure estate up from its
  configuration alone: the deployment generates the JWT signing key, the initial admin's password and the node
  credential into the vault, creates the apps' database users, and the control plane applies the partitions,
  databases, landing storage and flow repositories the configuration names at every start, in a start-up pass reported
  at `/health/startup`. A person puts in the vault only each partition's client secret and each private repository's
  token, and no secret reaches the Terraform. An environment's two files can be kept outside the repository, on a
  workstation (`OSDU_ENVIRONMENT_DIR`) or in its GitHub environment, so the committed ones stay placeholders.
- **Its own database schema.** Every OSDU table lives in the `osdu` schema under its own migrations: module schema
  1.46.0 (migration `20261014094057_PurgedRecordReadAgain`), requiring SQLFlow catalog migration
  `20261014215105_RunSuppliedReferences` or later. The hosts refuse to run against pending migrations.
- **Built on SQLFlow** `fe87877c` ([github.com/TahirRiaz/SQLFlow](https://github.com/TahirRiaz/SQLFlow)),
  vendored in `sqlflow/` with the extension points and fixes this project needed, each recorded with its reason
  ([docs/sqlflow-changes.md](docs/sqlflow-changes.md)). It runs on .NET 9, with every package on its newest release
  for it.

### Security

- Secrets are references only (`${env:NAME}`, `${keyvault:...}`): a document holding a literal secret is refused when it
  is read, and errors are redacted before they are stored or shown.
