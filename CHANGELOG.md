# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- A cache is a flow of its own: `flowType: cache` declares the OSDU platform to search (`source`: endpoint, auth, and
  headers with `data-partition-id`), the types to cache (each a `kind`, an optional `query`, the `fields` to keep as
  bare paths or `path`/`as` pairs, and `onChange: auto | approve`), `makeCurrent`, a default `onChange` (`auto` unless the flow opts into approval), `parameters`,
  `reliability` and a `schedule`. The cache flow's name is the cache's identity. Its contents live only in the catalog
  (`delivery.CacheVersion`, `delivery.CacheItem`): every version is kept, a record is stored once per run of versions
  that held it unchanged (`FromSequence` to `ToSequence`), and a version's content hash is checked every time it is
  loaded. Nothing writes cache data to a repository any more; the repository sync projects each cache flow's declared
  types into `delivery.CacheDefinition` and ignores any snapshot folder. The sample estate's cache is
  `samples/recall-welllog/caches/osdu-reference-cache.yaml`.
- The `refresh` operation, a cache flow's default (`deliver` is taken as refresh; `plan` counts what each type's search
  matches): it captures every declared type and writes a version, labelled from the capture instant
  (`20260908T212727Z`) and holding exactly the declared types, only when the content differs from the current version.
  A version records the run that captured it. Changed values that delivered records were built from are tagged as
  before, waiting for approval under `approve` and going out on the next run under `auto`. A refresh takes no drop,
  submission or record scope.
- `sqlflow cache list <cache.yaml | name>` lists a cache's versions, and
  `sqlflow cache import <cache.yaml> --from-dir <dir> [--no-current]` writes type files (`{Name}.json`, which must match the declared types, entity types and
  captured names) as a version for work without OSDU. A cache is refreshed from OSDU with `sqlflow run <cache.yaml>`,
  and `sqlflow check` prints the cache version a flow reads.
- `GET /api/v1/delivery/caches` (optionally by `repoId`) describes every cache: the defining file and pipeline, the
  endpoint, `makeCurrent`, the declared types, the schedules that refresh it, the current version and the version
  count. `GET /cache/items`, `/cache/versions`, `/cache/history` and `/cache/diff` now require `cache=<name>`, and
  update tags carry the cache. `GET /mapping-builder/caches` lists the caches for the mapping builder, whose draft,
  compose, template preview and template detail take `cache` in place of `repoId`.
- The OSDU cache page names the cache and the file that defines it, with View YAML and Refresh now (the trigger dialog
  with the refresh operation) beside it. A summary row gives the current version with who captured it, the records it
  holds, how it is refreshed, and whether changes are automatic or how many wait for approval; a banner with Review
  changes appears whenever any do. A searchable type picker in the tab bar scopes the Records and Versions tabs;
  the Changes tab lists what refreshes changed, with Approve and Reject in each waiting row, and the Definition tab reads
  back the endpoint, the schedules and each type's kind, query, kept paths and `onChange`. The tab, cache and type are
  in the URL. `GET /cache/tags` takes `cache` to list one cache's changes. A cache flow's pipeline page has a Cache versions tab, and the mapping
  builder has a Cache picker defaulting to the cache the repository's delivery flow names.
- A file too large to send through the control plane is written straight to storage:
  `POST /api/v1/delivery/dropoffs/reserve` writes the drop-off row, then hands out one write-only URL per file, and
  `POST /dropoffs/{id}/complete` closes it once they are written. Completion is decided by what storage holds, not by
  what the caller says: a reserved file that is missing, a different size than reserved, or an unexpected file present
  fails it and leaves the reservation open to finish. The bytes never touch the control plane, so nothing here hashes
  them; a hash given at completion is recorded as the uploader's claim (`hashSource: "client"`, against `"computed"` for
  a streamed upload and `"none"` for none at all), because a ledger that showed them the same way would imply a check
  that never happened. One file is at most 64 GB by default (`ControlPlane:DropOff:MaxSignedFileGigabytes`) against a
  streamed upload's 100 MB, and a reservation's URLs last an hour (`:SignedUploadExpiryMinutes`). Only Azure Storage can
  issue one, signed as a user delegation SAS so no account key exists anywhere, and the control plane's identity needs
  **Storage Blob Delegator** on the account; without it `GET /dropoff-area` answers `signedUploads: false` and says so.
  The GUI's Drop-off page takes this route on its own for any file past the streamed ceiling.
- A submission carries the caller's own name for it: `reference` on `POST /api/v1/delivery/submissions`, and `reference`
  in a prepared drop's manifest. It is a filename, a ticket or a job id, at most 200 characters, stored, searchable
  (`GET /flows/{id}/submissions?reference=...`) and never interpreted, so a source that keeps its own records can find
  what became of work it sent without holding this system's ids. It is part of the request a `submissionId` names, so a
  repeat that relabels the work is refused saying so rather than quietly rewriting what the ledger says it was called.
  The submissions list shows each submission under the name its source gave it, and the submit dialog offers the field.
- A mapping can match a name against the reference cache with punctuation and spacing folded away: `ignoreSeparators` on
  a `reference` or `lookup` transform, off by default. Source systems and OSDU write one facility name differently
  (`NO 15/9-19 SR`, `NO_15_9-19_SR`, `no-15-9-19-sr`), and the fold finds one record for all three. It runs only after
  exact and case-insensitive comparison have both found nothing, so it never moves a value that already resolved, and a
  folded key several records answer to resolves to none of them, naming them, as an ambiguous case fold does. It is for
  names and not codes, which is why it is opt-in: `s/m` and `S.M` would fold together and must not.
- A drop-off area, the pre-step to a submission: `POST /api/v1/delivery/dropoffs` uploads files (multipart) into a place
  the compute nodes can read, and answers with the location a submission then points at, so the two steps are upload and
  submit rather than one request carrying everything. `GET /dropoffs` lists what has been dropped off with who uploaded
  it, when, and each file's size and SHA-256; `DELETE /dropoffs/{id}` takes one back; `GET /dropoff-area` says whether
  the deployment offers one at all. The GUI has a Drop-off page (upload, list, copy the location, delete) next to Manual
  submission. Where uploads land is `SQLFLOW_DROPOFF_ROOT`, read by the control plane (which writes there) and by every
  node (which reads there), and a submission may point inside it whatever its flow's own roots allow. Nothing is removed
  automatically, because re-processing a submission reads its files again; a deployment whose uploads are single-use sets
  `ControlPlane:DropOff:RetentionDays` and a sweep then removes drop-offs that completed longer ago than that, never one
  that failed or stopped halfway.
- A flow says whether it takes records sent in a request: `source.manualSubmission` in the flow document, opt-in. The
  GUI's Manual submission page (Operate) lists every flow that offers it, with what it renders with, the parameters a
  submission carries and the payload its records point at, and submits to the one chosen;
  `GET /api/v1/delivery/manual-submission/flows` is the same list (`all=true` adds the flows that take none, with the
  reason).
- A submission can deliver payload files without carrying them: a record names where its files already sit
  (`"files": { "curves": "abfss://.../L-1001/chunk_*.parquet" }`, or `{ "location", "hash" }`), the drop written from it
  declares the payload by `locationColumn` instead of a path template, and the node opens that location with its own
  identity when it delivers and on every retry. Nothing is uploaded through the API and nothing is staged. A flow limits
  where a submission may point with `source.manualSubmissionFileRoots`, defaulting to the fixed part of its own
  `source.location`; a location outside them, one containing `..`, a record that points at nothing, and a missing content
  hash where the flow decides payload changes by hash are all refused when the request is accepted. This is what lets the
  wellbore DDMS, file and manifest flows take manual submissions, which were previously refused outright.
- Records can be submitted to a flow directly, instead of being prepared as a drop: `POST /api/v1/delivery/submissions`
  takes `records` (each in the shape of a mapping fixture) in place of `drop`, with the caller's own `submissionId` as
  the idempotency key and `operation: plan` for a preview. The control plane stores the records in the ledger in the
  same transaction as the run that takes them, so a repeat of a request answers with the run it started and a different
  request under the same id is refused; the run writes them out as a drop under the flow's work location and delivers
  them through the regular intake, change detection, ledger and drain. A flow whose protocol streams payload files takes
  a drop as before. `GET /delivery/flows/{pipelineId}/source-contract` says what a source sends a flow, and
  `GET /delivery/submissions/{id}/content` returns the records one carried. The GUI has Submit records on a flow's
  Delivery tab (a form for one record, or JSON for many) and a Records sent tab on the submission. Documented in
  [docs/delivery/submitting-records.md](docs/delivery/submitting-records.md).

### Fixed

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
  page with its history and actions, the submission page, the audit trail, the mappings page and the OSDU cache page.
- The CLI verbs `sqlflow check` and `sqlflow cache`, and the delivery run options on `run` and `trigger`
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
