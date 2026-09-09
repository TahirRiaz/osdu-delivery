# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- A version picker on the OSDU cache page: the type counts and the cached records are read at one reference
  snapshot version, the current one unless another is named, so the cache can be read as it stood at an earlier
  capture. `GET /api/v1/delivery/cache/versions` lists the versions; `GET /cache` and `GET /cache/items` take
  `version`. The repository sync now carries the records of the current version and the nine newest captures
  behind it, dropping the records (never the snapshot row or its counts) of versions that fall out of that window.

### Fixed

- The cached records list showed every snapshot version's records at once. Items were only ever written for the
  current version but were never removed when a version stopped being current, and the listing was scoped by
  repository and type but not by version, so a second capture would have shown each cached record once per version
  with nothing to tell them apart and counted it as many. Every read of the cache read model is now scoped to one
  version per repository.

- Schedules carry the flow parameter values every fire supplies (`values:` in a flow's inline `schedule` block or a
  schedule library entry, `values` on `POST /api/v1/schedules`). Without them a flow that declares a required
  parameter could not be scheduled at all: the fire supplied nothing and every run failed validation. A run-now's
  values override the schedule's name by name.
- Removal of delivered records at three scopes, named for what they take rather than for the verb: `record`
  (`POST /records/{id}:delete`, reversible in OSDU), `history` (`DELETE /records/{id}/versions`, the earlier
  versions destroyed and the latest left live) and `everything` (`DELETE /records/{id}`, the record and every
  version). OSDU has no call that removes only the latest version, so none is offered.
- Bulk removal: `POST /api/v1/delivery/flows/{pipelineId}/records/remove` takes either explicit `keys` or the
  listing `filter` whose every match goes (resolved on the node when the removal runs, up to 25,000 records),
  with `expected` refused on 409 when the set has moved; `.../remove/preview` answers what it would act on. The
  reversible scope batches through the storage service's bulk soft delete (`POST /records/delete`), falling back
  to one request per record on a 207 so every record reports its own outcome.
- The records list filters by `runId` (through the attempts that run wrote), ticks rows, offers "select all N
  matching", and opens one removal dialog that names the target endpoint and data partition, shows the three
  scopes with the call each makes, and gates the permanent ones behind typing the partition back. A run page
  links to the records it touched, and `GET /api/v1/delivery/flows/{pipelineId}/target` is where the GUI reads
  the target from.

- Streaming intake at any drop size: root rows and child scopes stream through a merge join (partitioned drops)
  or a disk-backed hash spill, rendering runs on a bounded pipeline, and the rendered documents go to work batch
  files on the flow's work location (`source.work`); the ledger keys pending records to a batch and a byte range
  (`delivery.WorkBatch`, `Record.WorkBatch`, `Record.PendingDocumentRef`).
- Batched delivery with steps and returned values: the record, file and manifest protocols write up to
  `protocolOptions.batchSize` records per request; every protocol reports each step it took and what the target
  returned, a retry resumes after the last completed step, and attempts and records carry the returned values
  (`Attempt.ResultJson`, `Record.TargetStateJson`, `Record.PendingStepJson`).
- Fan-out: a large submission spreads its intake and its drains over member runs across the fleet
  (`reliability.fanOut`, `reliability.fanOutMinRecords`), tracked as one run family (`Run.FanOutRoot`, `FanOutSlot`,
  `FanOutCount`); the `intake` and `drain` operations; the run's result on the run row (`Run.ResultJson`).
- The `osduFile` protocol (signed upload URL, streamed upload, dataset registration, then the record with its
  dataset list; purge deletes the datasets and their files) and the `osduManifest` protocol (uploads, one manifest
  per batch handed to the ingestion workflow, the run polled and resumed across tries, the records read back from
  storage), with their `protocolOptions` keys.
- The `retrieval` flow kind (`flowType: retrieval`): OSDU's search index paged into JSON Lines files on the lake
  per kind, optionally with the full records read back from storage, incremental by watermark, with a manifest per
  run and a row per run in `delivery.Retrieval`; the `retrieve` operation, the Retrievals tab and
  `GET /api/v1/delivery/flows/{pipelineId}/retrievals`.
- Work batches on the submission page and `GET /api/v1/delivery/submissions/{id}/batches`; fan-out membership,
  the result and the returned values on the run and record pages.
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

- The catalog schema is created from the EF model and there are no migrations: `CatalogDatabase.ProvisionAsync`
  (explicit) and `ProvisionExistingAsync` (guarded) replace the migrate pair, both verifying the database against
  the model afterwards and refusing to run when a table the model declares is missing, naming it. `sqlflow db
  status` reports whether the catalog is provisioned and what it lacks. A schema change now means dropping the
  database and provisioning it again; the trade is that a production catalog would have no incremental upgrade
  path, so migrations would have to be reintroduced before one exists.
- HTTP errors name the request URL without its query string, so a signed upload URL's credential never reaches
  an error message, a log line or the ledger.
- Manifests may declare `partitioned` drops (root file i and child file i sorted by delivery key), which the
  intake merge-joins without a spill and a fan-out spreads over member runs.
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
