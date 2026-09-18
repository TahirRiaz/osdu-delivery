# OSDU Delivery go-live map

Where each route stands, what separates OSDU Delivery from production, and the order to close the gaps in. It is the
working checklist for that work: each item has an id, is ticked when it is done, and the progress log at the end names
the commit that closed it. Every statement rests on the repository, or on the source code of the OSDU services it cites.

As of 17 September 2026, `main` at `de0c6d5`. Coverage plan ([osdu-coverage-plan.md](osdu-coverage-plan.md)): stages 1
to 8 done, stages 9 and 10 not started.

## Where it stands

| | Count | What it means |
| --- | --- | --- |
| Built and contract-tested | 15 of 15 routes and DDMS shapes | 1,726 delivery tests (SQL Server suites included) and 22 control-plane tests passed on 17 September. Every request a route sends is checked against its service's pinned OpenAPI contract, and every ETP message against the pinned Avro protocol. |
| Proven on a live OSDU | 4, all on the previous build | Azure Data Manager for Energy M26, partition `test`, runs up to 12 September ([../osdu/docs/osdu-testing.md](../osdu/docs/osdu-testing.md)): storage, file, manifest (inline), Wellbore DDMS well log bulk data. |
| Not built | 0 | Every route and DDMS shape the coverage plan names is built. |

**Nothing from the current build has run against a live OSDU yet.** Proving it live, route by route, is the largest
step between here and production.

## Route by route

A live test must remove what it creates at a reversible scope and prove that every id answers 404 (the project rules in
`CLAUDE.md`). Where a route cannot, the last column says why, and that route needs a cleanup decision before its live
test.

Evidence: **L-prev** proven live on the previous build; **C** built and contract-tested only. No route is yet proven
live on the current build.

| Route | What it delivers | Evidence | Removal and live cleanup |
| --- | --- | --- | --- |
| `storage` (osduRecord) | Any kind through Storage v2, batched, upsert by id. | L-prev | Soft delete (bulk); 404 proof works. |
| `file` (osduFile) | Files through the File service, registered, then the record naming them. | L-prev | Record and its `dataset--File.Generic` soft-deleted; the files behind them stay. |
| `manifest` (osduManifest) | `Osdu_ingest`, inline or by reference; records read back. | L-prev (inline only) | As `file`. By reference has never run live. |
| `dataset` (osduDataset) | Dataset service storage and registration: Azure, MinIO, S3, Google Cloud Storage. | C | Dataset service reversible delete. IBM collections are held; staged collection files stay. |
| `workflow` (osduWorkflow) | Anchor record, inputs, up to four catalogued workflow runs, outputs read back. | C | Anchor reversible; records a run wrote are removed only when the flow names them. Needs the workflows registered in the partition. |
| `ddms`, Wellbore DDMS v3 | Nine collections; bulk data for WellLog, WellboreTrajectory, PPFGDataset, WellPressureTestRawMeasurement. | L-prev (WellLog) | DDMS logical delete; bulk data stays. The other eight collections never ran live. |
| `ddms`, Well Delivery | An entity per write under a version, versioned references, its Storage copy. | C | On Azure and IBM a soft-deleted entity still reads back, so the 404 proof fails. History scope refused. |
| `ddms`, RAFS v2 | Rock and Fluid Sample records and their content tables. | C | Logical delete; what a read answers afterwards is unconfirmed. |
| `ddms`, Production historian | ProductionValues records through Storage, their points through the historian. | C | Storage soft delete; points cannot be deleted. |
| `ddms`, Seismic Store | `dataset--FileCollection.*` as `seismicmeta`; files to Azure Blob, GCS or S3. | C | No reversible dataset delete: the record scope leaves the dataset and its files. Full removal refused on gc. |
| `ddms`, Reservoir Management | Header records through Storage, the rows of the tables below them. | C | Soft delete leaves the rows and the service's copy. Records past the first 100 of a kind need an operator. |
| `fileAndDdms`, `manifestAndDdms` | Files or a manifest first, then each record's bulk data through its DDMS. | C | As their parts. |
| EDS checks (storage, manifest, workflow) | Registry entries, data jobs and proxy datasets checked for what External Data Services needs; its run state kept. | C | As the route. Secrets a registry entry names cannot be checked. |
| `dspdm` (osduDspdm) | Production DDMS business object rows, found again by a unique key before every save. | C | Hard delete only: DSPDM keeps no deleted rows. Relies on DSPDM's shipped update and time zone settings. |
| `etp` (Reservoir DDMS) | Energistics data objects in dataspaces over ETP 1.2 (WebSocket, Avro), with the arrays they name. | C | The object is deleted outright: the store keeps no deleted objects. A dataspace is never deleted, since the server purges its Storage record when it is, so a live test leaves its dataspace behind. |

## Workstreams

The five workstreams run side by side; the order of work below says what goes first.

### Prove it live

The largest gap. The suites prove the routes do what their contracts and the services' code say; only a live run proves
the deployment agrees. Every live run needs a test list approved first (`CLAUDE.md`).

- [x] **LIVE-0** Confirm the deployment facts the briefs leave open: which DDMSs run on the target (DSPDM, Reservoir
  Management, Seismic v3 or v4, Well Delivery), their gateway paths, whether Airflow's REST API is reachable, which
  workflows each partition registers, and what the Register service lists.
  Done on 2026-09-17 against ADME 0.29, partition `dev`, and corrected on 2026-09-18: the platform services, the
  Wellbore DDMS under `/api/os-wellbore-ddms`, Seismic Store v3, the Rock and Fluid Samples DDMS and the **Reservoir
  DDMS with its ETP 1.2 server** answer; Well Delivery, DSPDM, Production TimeSeries, Reservoir Management and eds-dms
  are not routed at all, at any prefix their specs or this module use. Seismic Store answers 403 to these credentials,
  which is an entitlement rather than an absence. The workflow service registers `Osdu_ingest`,
  `Osdu_ingest_by_reference`, `csv-parser`, two SEG-Y conversions and three EDS DAGs
  ([osdu-testing.md](../osdu/docs/osdu-testing.md) section 0.1). The first listing asked each service for `/info` or
  `/about` and read the Reservoir DDMS's 404 as absence; it has neither path.
- [x] **LIVE-1** Wave one: re-verify the four routes the previous build proved (storage, file, manifest inline, Wellbore
  DDMS well logs) on the current build.
  Done on 2026-09-17: checks 1 to 13 and 15 passed (check 14 covered a known-state operation this build does not have),
  the storage, file, manifest and ddms routes each delivered and read back, and all twelve ids were soft-deleted with a
  404 each. It found six defects, all fixed, and three deployment behaviours worth knowing
  ([osdu-testing.md](../osdu/docs/osdu-testing.md) section 0).
- [ ] **LIVE-2** Wave two: every other route and shape, each following its cleanup decision. What `dev` could serve
  today, once a test list is approved: the `dataset` route (the Dataset service answers with an Azure provider), the
  `workflow` route (`csv-parser` is registered), the manifest route's by-reference form
  (`Osdu_ingest_by_reference`), the `rafsV2` shape, and the `etp` route (the Reservoir DDMS runs its ETP 1.2 server
  there). Seismic Store needs an entitlement these credentials lack (403). Well Delivery, DSPDM, the historian,
  Reservoir Management and eds-dms need a deployment that runs them.
- [ ] **LIVE-3** The `history` and `everything` scopes, which never ran live: the app registration lacks
  `service.storage.admin`.
- [ ] **LIVE-4** The deployed hosts end to end: `${keyvault:}` references, managed identity, the control plane and worker
  images.
- [ ] **LIVE-5** A controlled volume run in a non-production partition.

Done when every route above reads "proven live on the current build" and the test data inventory
(`.sqlflow/live-e2e/test-data.md`) shows no id still live.

### Finish the build

Closes coverage plan stages 7 to 10.

- [x] **BLD-1** The `etp` route: an ETP 1.2 client over WebSocket with Avro messages, against a fake ETP server.
  The messages and data types the route uses are C# records written from the pinned protocol and round-tripped against a
  codec driven by that same file; the route writes Energistics objects into dataspaces inside one transaction each, with
  the arrays they name.
- [x] **BLD-2** Stage 8: records that wait for other records (a reference table and its migration, hold and release).
  What a record refers to is kept beside its document on the record; a claim leaves it waiting, charging nothing, and a
  delivery releases what waited for it. `target.verifyReferences: storage` checks the ids the ledger does not hold.
- [x] **BLD-3** Stage 9: interface views in the GUI, API and CLI filters, lineage per interface, and a record's attempt
  history from the CLI. The GUI names the interface every view is about (a multi-interface source's Records and
  Submissions tabs could not be opened before), lists a source's interfaces in run order, and lets a run name the
  interfaces it takes; `sqlflow records list|show` reads the ledger and a record's every try from a terminal.
- [x] **BLD-4** Stage 10: a sample estate across the kinds matrix, the test matrix, the docs. The estate carries a
  source of four interfaces over three route types (storage, ddms with bulk data, file) and four kinds, the schemas
  captured from the OSDU data definitions, and `osdu/docs/test-matrix.md` says what every suite proves and what none
  does. The live verification ran on 2026-09-17 (LIVE-1) and its report is
  [osdu-testing.md](../osdu/docs/osdu-testing.md) section 0.

Done when the coverage plan marks stages 7 to 10 done, each with its tests.

### Harden security

Verified in the code on 17 September.

- [x] **SEC-1** The URL guard (`osdu/src/SqlFlow.Delivery/Http/UrlGuard.cs`) checks literal IP addresses only. A host
  name that resolves to a loopback, link-local, cloud metadata or private address passes, because the connect callback
  (`HttpClientBuilder.KeepAliveConnectAsync`) resolves names without checking what they resolve to. Check every
  resolved address before connecting, and give deployments whose OSDU sits on a private network (Azure Private Link, a
  VNet-integrated environment) an explicit list of the ranges they may reach.
- [x] **SEC-2** The HTTP client follows up to five redirects itself, so a redirect target skips the URL guard (scheme,
  address and `reliability.urlAllowlist`). Follow redirects in the executor, check each hop, and keep credentials from
  another host.
- [ ] **SEC-3** `osdu/tools/dev-setup.ps1` copies the live container apps' secrets to developer machines. Give
  development its own estate and credentials.
- [ ] **SEC-4** SSH.NET 2024.2.0 (NU1903, high severity) in vendored SQLFlow: take the fix upstream, then a subtree pull.
- [ ] **SEC-5** Decide whether production may use the `verifyTls: false` opt-in.

Done when each item is fixed or accepted in writing, with tests for the guard.

### Make the gates automatic

The project's rules are enforced by hand today (`.github/workflows/ci.yml`).

- [x] **CI-1** CI skips the SQL Server suites (`SQLFLOW_TEST_DB` is unset): add a SQL Server service container.
- [x] **CI-2** The GUI end-to-end suite (Playwright, 6 specs) does not run in CI.
- [x] **CI-3** Zero warnings is a rule, but `TreatWarningsAsErrors` is false and CI does not pass `-warnaserror`.
- [x] **CI-4** OSDU migrations touching only the `osdu` schema is checked at runtime (`EnsureShape`); no test over the
  generated migration scripts was found.
- [x] **CI-5a** No image build and no changelog entries: CI now builds the three images, starts the control plane
  image against a SQL Server of its own and the GUI image, and `CHANGELOG.md` covers the work since it was written.
- [x] **CI-5b** No published images or release tags from CI; `deploy-prod.ps1` ran by hand. CI now publishes the three
  images to whatever registry the repository names (`IMAGE_REGISTRY`, with `REGISTRY_USERNAME` and `REGISTRY_PASSWORD`,
  or the workflow's own token for `ghcr.io`), tagged with the commit sha, `latest` from `main` and the version on a
  `v*` tag. It stays dormant until a registry is named, so DEC-9 is now which registry and which sign-in, not whether
  the pipeline can publish at all. Deploying from CI still waits on that decision.

Done when a change cannot merge without every suite, the end-to-end run and a warning-free build. Work goes to `main`
directly (`CLAUDE.md`), so CI reports on a change after it lands; stopping one before it lands needs pull requests and
a branch rule that requires the CI jobs (DEC-10).

### Run it in production

What an operator needs once real data flows.

- [x] **OPS-1a** Metrics: none were emitted. The engine now publishes settled tries and their duration per flow,
  route and outcome, and every HTTP call's result, duration and retries, on the meter `SqlFlow.Delivery`
  ([../osdu/docs/operations.md](../osdu/docs/operations.md#metrics)).
- [ ] **OPS-1b** Export the metrics and set the alerts: the exporter needs DEC-6 and the dependency approval of
  `osdu/docs/design.md` section 14.
- [x] **OPS-2** Health: `/health/live` and `/health/ready` exist; OSDU reachability was only the operator's probe.
  A scheduled probe now covers every active delivery flow, once per interface, through the same node operation the
  operator's button queues; every probe is an activity in the audit trail and a count on `osdu_delivery.probes`, and
  [../osdu/docs/operations.md](../osdu/docs/operations.md#watching-the-targets) says what to alert on. Off unless a
  deployment turns it on, since a pass costs a live request per interface. Setting the alerts is OPS-1b's exporter.
- [x] **OPS-3** Availability: the control plane runs one replica (the dispatch lease). What that costs, what breaks
  while it is down, what a worker keeps doing, what the control plane heals by itself and what an operator then checks
  are written down in [../osdu/docs/operations.md](../osdu/docs/operations.md#availability-and-recovery). Whether to
  accept one replica or route the node protocol to the lease holder stays DEC-5.
- [x] **OPS-4** Refresh the stale pages: the state in `osdu/README.md`, the deployables and size ceilings in
  `osdu/docs/operations.md`, `osdu/docs/osdu-testing.md` from the drop era.
- [x] **OPS-5** Set the ledger's retention and backup policy: what every table of the `osdu` schema holds, what grows,
  what may be pruned and what never may, what a backup must include and how to restore it
  ([../osdu/docs/operations.md](../osdu/docs/operations.md#retention-and-backup),
  [decisions/0005-ledger-retention.md](../osdu/docs/decisions/0005-ledger-retention.md)). The retention pass now also
  clears the captured run log of settled activities, which was the schema's only column with no ceiling.

Done when an on-call engineer can see, be alerted on and recover every failure the runbook lists.

## Order of work

Each step protects the ones after it. Live runs happen only with an approved test list.

1. Close the security gaps and automate the gates (SEC, CI). No live access is needed, and every later change then
   passes through the suites, the end-to-end run and a warning-free build.
2. Confirm the target and settle the cleanup policies (LIVE-0, DEC). Any read-only check against the platform is itself
   a live call and is approved first.
3. Live wave one (LIVE-1), alongside step 4. It checks the rebuilt core (ingestion tables, ledger, planner) against OSDU
   before more is built on it.
4. Finish the build (BLD-2, BLD-1, BLD-3, BLD-4), alongside step 3.
5. Live wave two (LIVE-2, LIVE-3, LIVE-4).
6. Make it operable (OPS).
7. Volume run and go-live review (LIVE-5), with the final report separating what was proven live from what rests on
   contracts alone.

## Decisions only the team can make

- [ ] **DEC-1** The go-live target: which OSDU deployment and partition, and which DDMSs run there.
- [ ] **DEC-2** Cleanup where no soft delete exists: DSPDM rows, Seismic Store files, historian points, Well Delivery on
  Azure and IBM, files behind soft-deleted datasets.
- [ ] **DEC-3** The purge scopes: whether production may use `history` and `everything`, and granting
  `service.storage.admin` where it may.
- [ ] **DEC-4** The ETP dataspace delete: deleting a Reservoir DDMS dataspace purges a Storage record; allow it, or keep
  dataspaces.
- [ ] **DEC-5** Control plane availability: one replica with a documented recovery, or failover.
- [ ] **DEC-6** Where metrics and alerts go: Application Insights, another OpenTelemetry backend, or the platform's own.
- [ ] **DEC-7** A development estate with its own resources and credentials.
- [ ] **DEC-8** DSPDM rows loaded before a flow: taken over (`existingRows: update`) or held for review.
- [ ] **DEC-9** The release path: which registry CI publishes the images to, how CI signs in to Azure (a federated
  credential rather than a stored secret), and whether a release tag deploys by itself or an operator still runs
  `deploy-prod.ps1`.
- [ ] **DEC-10** Whether changes reach `main` through pull requests with the CI jobs required, or keep landing directly
  with CI reporting afterwards.
- [ ] **DEC-11** Whether a mapping may name the record another interface of the same source delivers, so a child refers
  to its parent by the key they share. Today a reference comes from the partition cache (a record OSDU already holds), a
  column or a static value, so a child rendered before its parent ever landed is held by the cache lookup rather than
  left waiting for it (docs/interfaces-design.md section 7).

## What this rests on

- [osdu-coverage-plan.md](osdu-coverage-plan.md) and [plan.md](plan.md) for stages; the git history to `de0c6d5`.
- `osdu/specs/*/INTEGRATION.md` for each service's calls, limits and open questions.
- [../osdu/docs/osdu-testing.md](../osdu/docs/osdu-testing.md) for the live runs on the previous build;
  `.sqlflow/live-e2e/test-data.md` for the test data inventory.
- The code, read on 17 September: `Http/UrlGuard.cs`, `Http/HttpClientBuilder.cs`, `.github/workflows/ci.yml`,
  `osdu/deploy/README.md`, `osdu/tools/dev-setup.ps1`.
- Test counts from local runs on 17 September; no test ran against a live OSDU.

## Progress log

| Date | Items | Commit | What changed |
| --- | --- | --- | --- |
| 2026-09-17 | | `de0c6d5` | The map written; the `dspdm` route (stage 7) delivered before it. |
| 2026-09-17 | SEC-1, SEC-2 | `7cbd4e6` | Resolved addresses and every redirect hop checked; private ranges only when the deployment lists them (`SQLFLOW_DELIVERY_PRIVATE_NETWORKS`). |
| 2026-09-17 | CI-1, CI-3, CI-4, OPS-4 | `a7a77ea` | CI runs every suite against its own SQL Server and fails on a warning; a test checks which schemas the migration scripts write; a schema race the chain fixtures had on a new database fixed; the stale pages describe the current build. |
| 2026-09-17 | OPS-1a | `04b3e04` | The engine publishes settled tries per flow, route and outcome, and every HTTP call attempt, on the meter `SqlFlow.Delivery`; a retried attempt releases its connection before the backoff. |
| 2026-09-17 | CI-2, CI-5a | `c10dee4` | CI runs the GUI end-to-end suite and builds the three images, starting the control plane and GUI images; the changelog covers the work since it was written. |
| 2026-09-17 | BLD-2 | `2ea0806` | A record whose document refers to a record the ledger has not delivered is left waiting by the claim and goes out when that record lands; `target.verifyReferences: storage` checks the ids the ledger does not hold. Both CI jobs green. |
| 2026-09-17 | BLD-4 (part) | | The sample estate gains documents (file route) and directional surveys (ddms route with bulk data) with their real captured schemas, `sqlflow template capture --out`, and `osdu/docs/test-matrix.md`. |
| 2026-09-17 | BLD-3 | | Every view of a source is about one interface: the GUI's picker and interface listing, the trigger dialog's interfaces, `sqlflow records list|show`, the activities filter tested, and a source's lineage proven to carry every interface's reads and writes. |
| 2026-09-17 | BLD-1 | `de40610` | The `etp` route: the module's own ETP 1.2 client (Avro codec from the pinned protocol, framing, session) and the route over it, against a fake ETP server on a real WebSocket. |
| 2026-09-18 | LIVE-0, LIVE-1, BLD-4 | `d8403d1` | Wave one run against ADME 0.29, partition `dev`: the storage, file, manifest and ddms routes delivered and read back, verify and reconcile exercised, the refusals provoked, and all twelve ids removed with a 404 each. Six defects found and fixed (`2beff98`, `adf74d3`, `549580b`, `2a6b71e`, `a28a404`), three platform behaviours recorded. |
| 2026-09-18 | OPS-3, OPS-5 | `7b7e131`, `8ce07bd` | What one control plane replica costs and how an operator recovers; the ledger's retention and backup policy, with the retention pass clearing the captured run log of settled activities. |
| 2026-09-18 | OPS-2 | `cd6e13b` | A scheduled target probe: every active flow's OSDU asked whether it still answers, once per interface, recorded in the audit trail and counted, off unless a deployment asks for it. |
