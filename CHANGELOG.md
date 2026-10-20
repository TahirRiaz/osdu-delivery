# Changelog

All notable changes to OSDU Delivery are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- **The sample estate's lookup tables and reference records cover the sample's own curves.** The welldb curve
  dictionary lists the mnemonics the five sample logs carry and one spelling filed under another, the unit table the
  spellings those curves use, and the sample reference records the units, curve types and families they name, so the
  example reads as one small, consistent set ([Sample cache records](osdu/samples/cache-records/README.md)).
- **A Radix environment is complete in its configuration.** `radixconfig.yaml` names the workers' credential (the
  vault's `node-token`, generated before the first deploy in SQLFlow's token form) and the flow repository, as the
  Terraform estate does, so the control plane adopts the one and registers the other when it starts, and nothing is
  minted or registered by hand after the first deploy; `git-token` is read as a reference by both tiers rather than
  mounted. An environment deployed before this mints nothing new: store its token as `node-token` and add the
  repository's settings ([Setting up a Radix environment](osdu/docs/reference/guides/radix-setup.md)).

- **The Tests page is the Assertions page.** The GUI calls each test of an assertion flow an assertion, as the flow
  type names them, and each of its assertions a check: the page, a flow's tab, the board, the sheet, the run dialog
  (whose operation reads **Check**) and the reports say so. Flow documents, the API and the CLI keep `tests:`,
  `assert:` and the `test` operation ([The GUI](osdu/docs/reference/concepts/gui.md#in-osdu)).
- **The board says what an assertion's document says now.** A disabled assertion shows its reason, one whose proof
  values are past their day shows that it needs new proof values, the Definition tab says where the schema comes from
  and how long the proof values hold, and a skipped result no longer reads as an error
  ([The GUI](osdu/docs/reference/concepts/gui.md#in-osdu)).
- **A local start runs its work in workers, as a deployment does.** `dev.bat` and `./dev.sh` start the control plane
  with managed workers: worker processes it starts for queued runs and lets leave when idle, so Spawn, Always on and the
  target act on the Nodes page here too. `dev.bat single` (`./dev.sh single`) is the one-process start that ran by
  default before, and `managed` still names the default
  ([Managed workers on a developer's machine](osdu/docs/reference/guides/managed-workers.md#on-a-developers-machine-devbat)).
- **The Nodes page answers one question per tab.** A line of four facts (workers online, runs running, runs queued and
  how many could start, how workers start) opens on Workers (the nodes), Scaling (the pools and their controls, and the
  managed workers), Queue (what runs, what is queued and the gate holding it) and, with managed workers on, Activity
  (what the control plane started and stopped, and why); explanations sit behind an info mark instead of paragraphs
  ([Managed workers](osdu/docs/reference/guides/managed-workers.md#watching-it)).
- **The Nodes page says when nothing starts workers.** With managed workers off and the control plane running runs on
  its own node (`dev.bat single`, F5), Worker pools names that node and shows a pool short of its target as below target
  rather than starting, and Spawn and Always on say they only record a target
  ([The control plane](sqlflow/docs/reference/concepts/control-plane.md)).

### Added

- **Proof values of records found by what they hold.** An assertion flow's `knownValues` finds each record a sheet
  describes by full paths in the OSDU record and values, including lists of paths up to the record a reference names
  (`'[data.WellboreID, data.FacilityName]'`, as a dimension's label reads) and down to the records naming one, then
  holds it to the sheet's proof values,
  saying row by row which hold, which differ, which are not found and which are found twice
  ([Known values](osdu/docs/reference/flow/known-values.md)).
- **Tests switched off, and proof values that hold for a while.** A test, an assertion, a row of known values or a proof
  value takes `disabled: <why>` and is reported as skipped with its reason. A test or an assertion says when its values
  were stated (`asOf`) and how long they hold (`validFor: 90d`, or `validUntil`); past that it is not judged and is
  flagged as needing new proof values, a warning on the board. `asOfField: createTime` compares only the records OSDU had
  created by that day
  ([Disabling a test](osdu/docs/reference/flow/assertion.md#disabling-a-test-and-the-day-its-values-hold-until)).
- **Numbers a little off warn rather than fail.** A `count`, an `aggregate` or a known value's proof value takes
  `warnWithin: 10%` (or a distance, `warnWithin: 5`): a number that misses by no more is a warning, so its test is warned
  rather than failed, and the result says by how much it missed
  ([A warning band](osdu/docs/reference/flow/assertion.md#a-warning-band)).
- **A test names its template by the mapping.** `template: WellLog@1.5.0` checks a test against the template that
  mapping pins, so a test reads as the delivery does and follows the mapping when it is pinned to another capture; a
  template version (`template: 2f8a99cb38d32480`) is taken as before ([Tests](osdu/docs/reference/flow/assertion.md#tests)).
- **Aggregates that leave values out.** An aggregate's `except` lists values it does not count, such as a log's index
  curve `MD` among its curves ([Assertions](osdu/docs/reference/flow/assertion.md#assertions)).

### Fixed

- **A Radix environment delivers every flow.** The Radix application set only the LogType flow's access groups, so
  every other delivery flow, which reads `OSDU_ACL_OWNER` and `OSDU_ACL_VIEWER` unless it sets its own, could not
  resolve its groups; each partition now sets both, and the setup guide's SQL creates the `osdu` schema before it grants
  the worker's rights on it ([Setting up a Radix environment](osdu/docs/reference/guides/radix-setup.md)).

- **The Nodes page opens while managed workers run.** Each worker's state reached the page as a number rather than its
  name, so the page went blank as soon as a managed worker was listed, locally and on Radix; it now shows each worker
  as starting, ready, leaving and so on ([Managed workers](osdu/docs/reference/guides/managed-workers.md#watching-it)).
- **Local managed workers start when the osdu schema is in the catalog's database.** A setup with no `SQLFLOW_OSDU_DB`
  of its own, as `.sqlflow/env` usually is, started workers that exited at once ("exited with code 1 before its first
  poll"); each worker is now handed the module database the control plane uses, and no setting needs adding
  ([Managed workers on a developer's machine](osdu/docs/reference/guides/managed-workers.md#on-a-developers-machine-devbat)).
- **A tolerance is the number written.** A `tolerance` with few decimals, such as `0.0005`, was read as
  0.000500000024, which let a value a hair further off hold and showed that number in reports; it is now compared and
  shown as written. A test with such a tolerance is shown as changed once, until it runs again
  ([Conditions](osdu/docs/reference/flow/assertion.md#conditions)).
- **A worker pool asks only for workers that would get a run.** A schedule fire's members beyond its `maxConcurrency`,
  and a second queued run of a flow, counted as demand until the first started, so Azure and Kubernetes started workers
  that found nothing to run and were scaled away again. The target now counts what the pool's workers would start, and
  the Nodes page marks the members waiting their turn as at the group cap
  ([Deployment](sqlflow/docs/reference/guides/deployment.md)).

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
