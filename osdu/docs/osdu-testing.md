# OSDU testing: what is done and what is missing

What OSDU Delivery has been proven to do against a live OSDU platform, what the automated suites cover without one,
which defects the live runs found and how they were fixed, and what is still missing. The runbook is in
[operations.md](operations.md).

Two live waves are recorded here. **Section 0 is the wave of 2026-09-17 against the current build**, where data reaches
OSDU through SQLFlow's pre-ingestion and ingestion flows. **Sections 1 to 6 are the wave of 2026-09-12**, against the
implementation as it stood then, when a flow read a prepared drop.

> **Sections 1 to 6 are a record of runs made on 2026-09-12.** They are kept as the evidence of what those runs proved
> about the protocols, change detection, verify, the interventions and the recovery paths. The input is not the same:
> the drop, drop-off and known-state mechanics named below no longer exist ([architecture.md](architecture.md)), and
> neither does manual submission (records sent in the request, sections 2.8 to 2.10): records delivered by hand are
> files placed where a pre flow reads them ([design.md](design.md) section 3.3). Where the current build stands, route
> by route, is in [the go-live map](../../docs/go-live-map.md).

## 0. The live wave of 2026-09-17, on the current build

The current build, where data reaches OSDU through SQLFlow's pre-ingestion and ingestion flows, was run against a live
platform on 2026-09-17 (UTC): **Azure Data Manager for Energy, release 0.29, data partition `dev`**. The checks, the
ids each would create and how each would be removed were written out in [live-wave-one.md](../../docs/live-wave-one.md)
and approved before anything was sent. The estate is the sample estate (`osdu/samples/recall-welllog`) rebuilt into the
git-ignored `.sqlflow/live-e2e/repo` with the deployment's own partition, access groups and legal tag, and every record
it wrote carried `tags.RunMarker = ODLIVE20260918`.

Everything it created was removed the same night: twelve records, each soft-deleted through
`POST /api/storage/v2/records/{id}:delete` (204) with a GET that answered 404, then a search of the partition for the
run marker returning 0 results, and a forced verify of every flow reporting its records missing. Nothing was purged.
The ids, their history and what a soft delete leaves behind are in `.sqlflow/live-e2e/test-data.md`; every call is in
`.sqlflow/live-e2e/actions.log`.

### 0.1 What the deployment serves

Read with one info call per service, which is what decides which routes could be exercised at all:

| Serving | Not serving (404) |
| --- | --- |
| storage 0.29.4, search 0.29.2, legal 0.28.1, entitlements 0.29.3, schema 0.29.1, file 0.29.1, dataset 0.29.1, workflow 0.29.1 (with `Osdu_ingest` and `Osdu_ingest_by_reference` registered), indexer 0.29.1, notification 0.29.2, register 0.29.3, unit 0.29.2, CRS catalog and conversion 0.29.2, Wellbore DDMS 0.29 under `/api/os-wellbore-ddms`, Seismic Store v3, Rock and Fluid Samples DDMS 0.2.0 | Well Delivery DDMS, Reservoir DDMS (so no ETP), Production DSPDM, Production TimeSeries, Reservoir Management DDMS, External Data Services, secret, policy |

The app registration used has no `users.datalake.admins` entitlement, so no purge was possible and none was wanted.

### 0.2 The checks and what they proved

| # | Check | Result |
| --- | --- | --- |
| 1 | Reachability, per flow | Passed. Every target probe answered. |
| 2 | Cache refresh from the partition | Passed after a fix (0.3). The cache flow captured the reference types the mappings read. |
| 3 | Storage route, first delivery | Passed. Two `master-data--Wellbore` records written in one batch. |
| 4 | Storage route, unchanged re-run | Passed. Nothing sent. |
| 5 | Storage route, one revision | Passed. One record's description changed: that record sent, the other skipped. |
| 6 | DDMS route, metadata and bulk | Passed after a fix (0.3, twice). Three well logs in the Wellbore DDMS, each with its parquet chunk; the bulk read back with the fixture's own values (9, 5 and 4 rows, indexed by MD). |
| 7 | DDMS route, payload only | Passed. New curve values on one log: that log alone was sent, as a new bulk version of the same id, and the service returned the new values. |
| 8 | DDMS route, a session | Passed after a fix (0.3). One log's bulk as two chunks (5 and 4 rows), committed as one version through a session, and the committed-rows check confirmed nine rows. |
| 9 | DDMS route, refusals | Passed. Two chunks numbering rows from zero were held in 0.07s with no call; two chunks carrying the depth as the frame index were held in 0.08s with no call; a chunk whose depth repeated a value was refused by the service ("The reference curve 'MD' should not contains duplicated values.") and the record held with that message. |
| 10 | File route, four stages | Passed after a fix (0.3). Two documents: each file uploaded, registered as a `dataset--File.Generic`, then the record naming it; an unchanged re-run sent nothing; a description-only change took a new record version and registered no dataset; a changed file was uploaded and registered as a new dataset. |
| 11 | Manifest route | Passed. The file was uploaded and registered, search saw the dataset after 20 seconds, one manifest was sent to `Osdu_ingest`, the run finished after three polls (58 seconds end to end), and the record was read back. |
| 12 | Verify and drift | Passed, and found a deployment behaviour (0.4). Three well logs and the manifest document matched; both wellbores were reported drifted with the observed and expected versions. |
| 13 | Reconcile | Passed. With `verify.reconcile: true` the two drifted records were queued, redelivered as new versions of the same ids, and a forced verify then answered 2 match, 0 drifted. |
| 14 | Known state | Not applicable. The known-state operation this check was written for does not exist in this build ([design.md](design.md) section 7.5 records its removal); what it checked is now what verify does, which check 12 covers. |
| 15 | Removal and proof | Passed. Twelve ids soft-deleted, each proven gone. |

Two route types were not exercised: the `dataset` route (no sample estate delivers through the Dataset service) and the
`workflow`, `dspdm` and `etp` routes (this deployment serves none of the services behind them). The trajectory
interface of the sample source was not delivered either: its ids were outside the approved list.

### 0.3 The defects the wave found, and what was done about them

Each is fixed, with the suites that now hold it.

| What broke | Cause | Fix |
| --- | --- | --- |
| The cache capture kept only the first page of every reference type | A search cursor is documented as null when the paging is done; this deployment answers with the same cursor handle while the pages behind it advance and then run dry, so a capture that stopped on a repeated cursor stopped immediately | A capture ends a type when its pages stop bringing anything new (`SnapshotBuilder`, commit "Take a cache capture to the end of a type on a service that keeps its cursor") |
| Every well log's bulk upload answered `422 Bulk error: Unprocessable data` | The sample chunks carried a partial pandas entry (`{"index_columns":["MD"]}` with no column descriptors). A bulk service reads a chunk as a dataframe, and that entry raises in the reader before any row is read | The sample estate and the fixtures write the whole entry, and the preflight now holds a chunk a dataframe reader cannot read, naming what is missing (commit "Hold a bulk chunk a dataframe reader cannot read, and write chunks it can") |
| A session of two chunks uploaded both and then failed the commit with `422 reference curve 'MD' do not cover the entire bulk` | A session's commit requires the reference curve to be one of the bulk's columns; a chunk carrying it as the dataframe's row index is accepted and then refused at commit. A whole-bulk write of the same file is taken, so the rule is the session's alone. It is in neither the pinned contract nor the service's source | The preflight holds such a record before it opens a session, and the integration notes carry the observation (commit "Hold a session whose chunks do not carry the reference curve as a column") |
| The well logs could not be delivered at all at first | The flow named no DDMS root, and this deployment serves the Wellbore DDMS under `/api/os-wellbore-ddms` | The live estate's flow names the root. Nothing in the product changed: a deployment's roots belong in its documents |
| Held records could not be sent again from a node | The GUI could release held records; the CLI could not | `sqlflow records release` (commit "Release a flow's blocked records from the command line") |
| The source document's wellbores and welllogs interfaces held every record on a preflight error | Both named mappings that repeat the rows of a child dataset and declared no datasets | Both interfaces declare their dataset, and a suite now checks every committed delivery document of the estate against the mapping it names (commit "Declare the child datasets the sample source's mappings repeat") |

### 0.4 What the platform does that an operator has to know

- **It enriches master data after a write.** Within a second of our write, both wellbore records took another version,
  written by the platform's own identity, adding `data.TechnicalAssuranceTypeID` (a
  `reference-data--TechnicalAssuranceType:Unsuitable:` reference). A verify then reports drift on every delivered
  master-data record, correctly: the version really moved and the record's content really changed. Naming that key
  under the flow's `protocolOptions.preserveDataKeys` carries the platform's value into every update, and a verify then
  reads it as another system's write rather than drift. The live estate's wellbore flow now does this.
- **A soft delete is enough, and it is visible immediately.** Each `:delete` answered 204 and the following GET
  answered 404, with no wait. The search index dropped the records too: a query for the run marker returned nothing
  minutes later.
- **The Wellbore DDMS keeps bulk data behind a logically deleted record.** The three well logs' curve data is still in
  the DDMS after their records were soft-deleted, as the inventory records.
- **`Osdu_ingest` runs in about half a minute** on this deployment for a one-record manifest, after search takes about
  twenty seconds to see the registered dataset.

### 0.5 What this wave does not prove

- That the `dataset`, `workflow`, `dspdm` and `etp` routes work against a live service. The first has no sample estate;
  the other three have no service on this deployment. They stand on their suites and the pinned contracts.
- That the other DDMS shapes (Well Delivery, RAFS, Seismic Store, Reservoir Management, the production historian)
  behave as their contracts say. Seismic Store and RAFS are served here and were not exercised: no sample estate
  delivers to them, and their ids were not in the approved list.
- Anything about scale. The wave moved twelve records and a few kilobytes of bulk data. What the engine does at volume
  is covered by the SQL Server suites, not by this.
- The deployed hosting: this wave was driven by the CLI host from a workstation, not by the container images.


## 1. How it is tested

Testing runs at three levels.

| Level | What runs | OSDU involved |
| --- | --- | --- |
| Automated suites | 1,042 tests on 2026-09-12, all passing: `tests/SqlFlow.Delivery.Tests` (476: the domain, the ledger on SQLite, the engine end to end over the sample estate with a fake protocol, the HTTP runtime against stub handlers), `tests/SqlFlow.ControlPlane.Tests` (376), `tests/SqlFlow.Core.Tests` (190). These are executed tests, so a `[Theory]` counts once per case. The database suites need `SQLFLOW_TEST_DB` exported into the environment: without it 181 control plane tests skip and the run still reports success. | No |
| GUI e2e suite | `gui/e2e`, 14 specs, 61 tests, all passing on 2026-09-12. Flows run with the `plan` operation. | No |
| Live tests | An estate delivered into an Azure Data Manager for Energy test instance (an M26 service, data partition `test`) by a local control plane, driven through the control plane's API and GUI. | Yes |

### 1.1 The live estate

The live runs follow three rules, because the partition is shared and the app registration cannot purge:

- the dataset stays tiny: two wellbores, three well logs, two documents with one CSV file each;
- every action that writes to OSDU is appended to `.sqlflow/live-e2e/actions.log` before it is sent, with the ids it
  can touch, and every record carries a run marker (`ODLIVE20260910`, and `ODLIVE20260912` for the records sent in a
  submission request);
- nothing is purged; removal uses only the reversible record scope, and only on this estate's logged ids.

The estate is a git repository the control plane syncs (`.sqlflow/live-e2e/repo`, git-ignored), rendered with a
reference snapshot captured from the same partition. Every flow authenticates with OAuth2 client credentials whose
secret is the reference `${env:OSDU_CLIENT_SECRET}`.

These live runs predate cache flows. At the time the cache was kept as reference snapshots in a store the delivery
flows named, refreshed by the `e2e-cache-sync` retrieval flow, and this page records the runs as they happened. A
cache is now defined by a cache flow (`flowType: cache`), captured by its `refresh` runs, and its versions live only in
the catalog ([documents.md](documents.md#cache-flow)).

| Flow | Kind and protocol | What it delivers |
| --- | --- | --- |
| `e2e-wellbore` | delivery, `osduRecord` | 2 `master-data--Wellbore` records |
| `recall-welllog` | delivery, `osduWellLog` | 3 `work-product-component--WellLog` records and their wellbore DDMS bulk data: L-1001 (MD, GR, RHOB), L-1002 (MD, GR), L-2001 (MD, NPHI) |
| `e2e-document` | delivery, `osduManifest` | 1 `work-product-component--Document` with a CSV file, through `Osdu_ingest` |
| `e2e-file` | delivery, `osduFile` | 1 `work-product-component--Document` with a CSV file, through the file service |
| `e2e-cache-sync` | retrieval | Wellbore and reference data read into the OSDU cache that `recall-welllog` renders from |

Runs held by the live catalog between its re-mints on 2026-09-10 and 2026-09-12 (the second re-mint added
`osdu.InlineSubmission`; a full database backup was taken first, and the runs before the first re-mint are in the
ledger export taken then):

| Flow | Operation | Runs |
| --- | --- | --- |
| `e2e-cache-sync` | retrieve | 7 succeeded |
| `e2e-document` | deliver, verify | 6 and 1 succeeded |
| `e2e-file` | deliver, intake, drain, known-state, plan, verify | 9, 1, 1, 1, 1 and 6 succeeded |
| `e2e-wellbore` | deliver, verify | 10 and 1 succeeded |
| `recall-welllog` | deliver, known-state, verify | 19 succeeded and 1 failed, 1 and 1 succeeded |

The failed run is a preflight refusal during the cache change tests: the mapping read a wellbore description the
reference snapshot it rendered with did not cache. Nothing was sent.

## 2. Proven live

### 2.1 The four protocols

| Protocol | What was proven |
| --- | --- |
| `osduRecord` | First delivery of both wellbores; an unchanged re-run sends nothing; revising one wellbore sends that one and skips the other (`delivered=1 skippedUnchanged=1`). |
| `osduWellLog`, single request | Three logs delivered with metadata and bulk data. A payload-only change (new GR values, same metadata) sends only that log. The ledger's version is the version the bulk write created, so verify after a clean delivery finds no drift. A parquet file of 9 rows went to `POST /data` in one request with no session, and the rows the DDMS holds match the drop value for value. |
| `osduWellLog`, session | L-1001 split into two chunk files of 5 and 4 rows whose row index continues (0 to 4, 5 to 8, both as a stored index column and as a pandas `RangeIndex`) committed one version of 9 rows that match the drop; the log's description was read back after the commit and checked at 9 rows. |
| `osduWellLog`, refusals | Two chunks that both number their rows from zero are held before a session opens, naming both files, and nothing reaches the DDMS. One file whose stored index repeats labels is refused by the DDMS with HTTP 422 (duplicated index); the record is held and nothing is written. |
| `osduWellLog`, curves | For all three logs, the curves the record declares match the bulk data's columns, and the reference curve is one of them. |
| `osduManifest` | A document and its CSV delivered through `Osdu_ingest`: the file is uploaded, registered through the file service, retrievable through its download URL with a matching sha256, and the manifest is triggered only after search can see the dataset. A changed document delivered by a scheduled run lands as a new version. |
| `osduFile` | Four stages on one document: first delivery (upload, registration, record write); an unchanged re-run sends nothing; a metadata-only change writes a new record version with no upload and the same dataset; a payload change uploads the new file and registers a new dataset. The bytes in OSDU match the drop by size and sha256 at every stage. |

### 2.2 Change detection

| Area | What was proven |
| --- | --- |
| Last-modified watermark | A row whose update time is older than the version the ledger holds is recorded as stale and never sent. |
| Rendered document hash | A new cache version sends only the documents whose rendered form changed: after revising one wellbore, one wellbore and the one well log built from it were sent, and nothing else. |
| Scheduled incremental delivery | `e2e-document` on a two-minute schedule: the first fire delivered the changed document, the next found nothing to do. |
| Forced runs | A forced run re-plans past the whole-run gate and still skips records whose hashes match what OSDU holds. |

### 2.3 The OSDU cache and change rollout

| Area | What was proven |
| --- | --- |
| Retrieval | Seven cache refreshes from the partition, each minted into the snapshot store the delivery flows render from. |
| Automatic rollout | A revised wellbore description raised a cache change whose rollout redelivered the well logs built from it. |
| Approval gate | With `onChange: approve`, the pending change held the dependent well logs out of every plan; approving it from the GUI marked L-1001 and L-1002 for a metadata redelivery, and the next run delivered both with the approved description. |

### 2.4 Verify, drift, reconcile and known state

| Area | What was proven |
| --- | --- |
| Verify | Verify runs on all four delivery flows report no drift after a clean delivery. |
| Drift | A record changed outside OSDU Delivery (a storage `PUT` adding a tag) is reported drifted by verify. |
| Reconcile | With `verify.reconcile: true` the drifted record's hashes are cleared, the next drop re-sends it, the foreign tag is gone, and verify reports no drift again. |
| Known state | A known-state run publishes `known-state.parquet` and `known-state.json` for `recall-welllog` (3 records) and `e2e-file`. |
| Probe and read-back | The endpoint probe reaches the file service; read-back from the record page returns the record as OSDU holds it. |

### 2.5 Operator actions

| Action | What was proven |
| --- | --- |
| Redeliver | Through the API, under each scope (all, metadata, payload): the run sends what the scope names and reports its own counts. |
| Release | A held record, released after its cause is fixed, is sent by the next run of its drop, including a run that plans nothing new. |
| Remove | The reversible record scope removed all five wellbore and well log records twice (storage and the DDMS answer 404); release and a forced deliver brought them back. |
| Correlation ids | Every OSDU request of a delivery attempt carries the attempt's id, and storage echoes it, so an attempt can be found in OSDU's own logs. |

### 2.6 Batching, recovery and nodes

| Area | What was proven |
| --- | --- |
| Intake and drain | Intake plans a change into a work batch and sends nothing; drain delivers the batch. |
| Crash mid-delivery | The control plane was killed while an `osduFile` record was between its upload and its registration. After the restart the recovered run resumed the reported upload and registration, registered the file once, and delivered the record; the ledger matches OSDU and no duplicate dataset was created. An earlier interruption on the build before defect 14's fix left the record delivering; an ordinary deliver run of its drop repaired it. |
| Standalone worker | A `sqlflow worker` started under its own node name is listed online, claims a pooled verify run, and stops cleanly through the node restart endpoint. |

### 2.7 The GUI over live data

A walk over the live estate passed 10 of 10 checks: sign-in, the delivery overview listing the four delivery flows
with their statistics, a flow's records and statistics, the record page with its OSDU id and attempts, search by OSDU
id, the runs and nodes pages, and the cache updates tab, with no console errors and no failed or 5xx API calls.

### 2.8 Records sent in the request

Wellbore master data sent to `e2e-wellbore` as records in the request,
driven through `POST /api/v1/delivery/submissions` exactly as a source system would, on 2026-09-12 (markers
`ODLIVE20260912` and, after the flow attribute landed, `ODLIVE20260912B`):

| Stage | What was proven |
| --- | --- |
| The flow offers it | `e2e-wellbore` declares `source.manualSubmission`; `GET /delivery/manual-submission/flows` names it and no other, and with `all=true` reports `recall-welllog`, `e2e-file` and `e2e-document` as taking none, each saying it streams payload files. |
| Preview | `operation: plan` rendered the record and reported it; nothing reached OSDU. |
| Delivery | The run wrote the records as a drop under the flow's work location and delivered them through the ordinary intake: `test:master-data--Wellbore:94a321a3935358d6b059c613aaeb3a4c` at version 1789193436546764, read back from OSDU through the record's read-back task. |
| Idempotency | The same request under the same `submissionId` answered 200 with the run the first one started, and queued nothing. |
| Conflict | The same id with a changed record was refused with 409, naming the records as what differs. |
| Change | A later `update_date` with a changed description delivered one record and moved the OSDU version to 1789193442697039. |
| Stale | An older `update_date` was recorded as stale, and the delivered version did not move. |
| Traceability | Each submission's page shows the records as sent, who sent them and where the run wrote them; `GET /delivery/submissions/{id}/content` returns them from the ledger. |
| Cleanup | The record was removed at the reversible `record` scope; the ledger says deleted and a read-back finds nothing. |

The refusal reason in the first row is what that build gave: a flow whose protocol streamed payload files could not offer
manual submission at all. That restriction is gone, and section 2.9 is the pass that replaced it.

### 2.9 Records sent with their payload files

A submission is metadata plus where the payload files already are. Nothing is uploaded through the API and nothing is
staged: the record points at its files and the node opens that location with its own identity when the run delivers.
Proven against `e2e-file` (`osduFile`, one document with one attached file) on 2026-09-12, marker `ODLIVE20260912C`,
after opting the flow in with `source.manualSubmission` and a `manualSubmissionFileRoots` bounding it to this estate's
own file area:

| Stage | What was proven |
| --- | --- |
| The contract | `GET /delivery/manual-submission/flows` lists `e2e-file` with payload `files`; its source contract reports the payload, that a content hash is required (the flow decides payload changes by hash) and the one root a record may point inside. |
| The boundary | Five refusals, all 400 at accept time with nothing sent and nothing stored: a record pointing at no files, a location outside the flow's roots, a location containing `..`, no hash where the flow needs one, and files named for a payload the flow does not stream. |
| Preview | `operation: plan` planned one delivery with no holds and no issues, so the location resolved and its chunk files were listed. Nothing reached OSDU. |
| Delivery | The run uploaded the file it pointed at to the landing zone, registered `test:dataset--File.Generic:25899a0b-528c-4b37-a9f4-a8d232e567eb` and wrote `test:work-product-component--Document:d1f669f0ff525046bd5bcc798199b4a4` at version 1789204329562243, carrying the hash of a file the request never contained. |
| Read back | The record read back from storage with `data.Datasets` naming the registered dataset. |
| Cleanup | The document was removed through the ledger at the reversible `record` scope and the dataset soft-deleted directly (204); both then answer 404. |

### 2.10 Files dropped off first, then submitted

The two-step path: upload the files to the drop-off area, then submit records pointing at where they landed. Proven
against `e2e-file` on 2026-09-12, marker `ODLIVE20260912D`, with `SQLFLOW_DROPOFF_ROOT` set to the estate's own
`dropoff` folder and the live catalog re-minted for the new `osdu.DropOff` table (backup
`OsduDeliveryLiveE2E-20260912T101353Z.bak` first, repo source registered again afterwards):

| Stage | What was proven |
| --- | --- |
| The area | `GET /delivery/dropoff-area` reports the area enabled, where it is, 100 MB and 20 files per upload, and retention 0 (nothing removed automatically). |
| Upload | `POST /delivery/dropoffs` (multipart) landed the file, answered with the location and the file's SHA-256, and the ledger row says who uploaded it and when. The file was on disk at the location reported. |
| Submission | A submission to `e2e-file` pointing at `{location}/*.csv` with that hash was accepted with no file bytes in the request, and the run delivered `test:work-product-component--Document:5a5838b791e35cd6bc5f34812124b2fb` at version 1789208551595739 with dataset `test:dataset--File.Generic:091bd31b-3459-44e1-a336-8339652d6c48`. The flow's own `manualSubmissionFileRoots` names only its drop folder, so this also proved the drop-off area is allowed for every flow that takes submissions. |
| Read back | The record read back from storage with `data.Datasets` naming the registered dataset. |
| Cleanup | The document was removed through the ledger at the reversible `record` scope, the dataset soft-deleted directly (204), and both then answered 404. `DELETE /delivery/dropoffs/{id}` removed the uploaded file and the row says deleted. |

### 2.11 A drop the prepare side notifies

The other form of `POST /api/v1/delivery/submissions`: the drop is already written, and the request only says it is
there. Driven on 2026-09-12 against `e2e-wellbore` and the existing drop `drops/wellbore/ODLIVE20260910`:

| Stage | What was proven |
| --- | --- |
| Preview | `operation: plan` rendered the drop's records and reported them; nothing reached OSDU. |
| Delivery | The run delivered both wellbores (`delivered=2 held=0 failed=0`) under the submission id the drop manifest carries (`b54ec551-e320-564b-9647-7df0d0a32f87`), landing `test:master-data--Wellbore:8caec6614b605fe6809175534f9c9a1c` and `test:master-data--Wellbore:250b474c3f1550c59dc7a11c81ca63c3`, both at version 1789209164941463. |
| Repeat | A second notification of the same drop was started, expecting nothing sent and both records unchanged. Its result is not in the action log, so the repeat is not proven. |

The two records this left in the partition are in section 5.

## 3. Defects the live tests found

Each is fixed on `main`, and the live runs after each fix are in the action log.

| # | What happened live | Fix | Commit |
| --- | --- | --- | --- |
| 1 | The DDMS refused every sample log: the reference curve was not among the log's curves. | The drop describes its index curve; a log whose reference curve is not one of its curves is held before anything is sent. | 69c49ac, 8b1e909 |
| 2 | The partition holds `ft` (foot) and `fT` (femtotesla); a case-insensitive match could have delivered a log in feet as femtotesla. | OSDU codes and ids match case-sensitively. | b6551a0 |
| 3 | Verify straight after a clean delivery reported every well log drifted. | The ledger records the version the bulk write created. | 2d97a74 |
| 4 | Redelivering a record from the GUI or the API sent nothing. | A deliver run scoped to record keys carries and marks what it sends. | 179e547 |
| 5 | A run reported its submission's counts as its own. | A run reports what it did itself. | ff40609 |
| 6 | A manifest delivery created the dataset but not the document. | Dataset references take the form the schema requires; a record the workflow run did not write is resubmitted. | eff0cf0 |
| 7 | A released record stayed pending through a forced re-run that planned nothing. | The next run of its drop sends it. | 56fbfe2 |
| 8 | A manifest delivery's file stayed in the landing zone and its download URL answered 404. | Files are registered through the file service before the manifest names them. | 4ae5ebd |
| 9 | A scheduled manifest delivery settled as delivered while storage still held the earlier version. | A record counts as written only when its version moved, and the manifest waits for the index. | 573887f |
| 10 | A cache refresh run by the control plane minted into the copy of the repository the run executed from, so later refreshes and renders found no cache. | The cache store is named explicitly and never minted into a run's copy. | 1d0dba8 |
| 11 | Revising one wellbore re-sent identical documents on every cache refresh. | The hash covers the rendered document alone. | b52ee80 |
| 12 | A cache change could name the value before last as the old value. | Each cache set is judged by the value it holds. | e5de221 |
| 13 | Two chunks that both numbered their rows from zero committed a log of 5 rows instead of 9, and the commit succeeded. | Colliding row labels hold the record before a session opens; the committed log is read back and checked. | 1b70fbc |
| 14 | A run recovered after a crash finished while the dead worker's lease still held the record, leaving it delivering although OSDU held the new version. | The recovered run waits out the stopped lease and sends the record. | 64a5e57 |
| 15 | A standalone worker on the control plane's host took the same node name as the control plane's own worker. | A node can run under a configured name. | 525b485 |
| 16 | A submission to a flow that streams files wrote a drop its own reader refused: the root rows carried no delivery key, because the drop was keyed only when the mapping iterated a child scope, and a document mapping iterates none. | A drop is keyed whenever it declares a payload, and a regression test covers a payload flow whose mapping iterates nothing. | 2b0d740 |
| 17 | Every neutron porosity curve was published as a hundredth of its value: the mapping declared the source unit `V/V` as `%`. | `V/V` maps to `m3/m3`, which is what OSDU's reference data calls volume per volume, and the sample snapshot caches it. | 215bee2 |
| 18 | Records a cache change held back for approval were reported as unchanged, so a run said nothing was waiting while the estate waited on a decision. | They are counted apart as awaiting approval, from the plan through the submission, the run result, the API and the GUI. | 215bee2 |
| 19 | A registration whose answer never reached the ledger left a dataset record with nothing referencing it, because the step was marked only after the service answered. | The step is marked with its landing-zone path before the request, and a try that finds the mark asks which dataset that path became and takes it over. | 215bee2 |

## 4. What is missing

> As of the 2026-09-12 wave. What the current build still lacks live proof for is in section 0.5, and some of the rows
> below have since been answered: the preflight now checks every chunk's columns against the record's declared curves,
> and a session of several chunks was committed live on 2026-09-17.

### 4.1 Needs access or a decision

| Item | Why it matters | What it needs |
| --- | --- | --- |
| Key Vault secret references | Production holds the OSDU client secret in Key Vault. Every live flow uses `${env:...}`; `${keyvault:NAME}` is covered only by locator parsing tests (`AzureKeyVaultProviderTests`), never resolved against a vault. | A vault and a secret the control plane's identity can read. Nothing is created in the production subscription without the owner's decision. |
| Drops on ADLS | Production drops are external locations (`abfss://`) read with the service's identity. Every live drop was on local disk. | A storage container the delivery identity can read, holding one small drop. |
| Deployed hosting and managed identity | The live control plane ran locally from a Release build. Nothing ran in Azure, and the managed identity code paths have no tests. | A deployed control plane and worker. |
| A drop written by the real prepare job | Every live drop was written by local scripts and `tools/SampleDrop`. That a Databricks implementation derives the same delivery key and payload hash, and writes continuing row labels, is untested. | One small drop from the prepare job, delivered and verified. |

### 4.2 Can be tested with the access available now

| Item | Why it matters | What it takes |
| --- | --- | --- |
| Curves that do not match the bulk columns | A log whose record declares a curve its bulk data lacks, or the reverse, is delivered as it is. Only the reference curve is checked. | A preflight check comparing the declared curves with each chunk's columns, and one live log that trips it. |
| Sessions that split a log's curves | Chunks sharing row labels with different columns are allowed by the preflight and covered by engine tests, but never committed live. | One log re-prepared as two column chunks. |
| Payloads near the ceilings | Live chunks held 9 rows. The 10,000,000-value and 3,000-column ceilings are checked from parquet footers in tests only, and the largest body ADME accepts on a single `POST /data` is not known. | One larger log. It leaves a larger bulk version in a partition that cannot be purged, which the small-dataset rule weighs against. |
| Partitioned drops and fan-out | Every live drop was unpartitioned and every run a single member. Covered by `ScaleStorageTests` and `ScaleEngineTests`. | A partitioned copy of an existing drop, delivered with a fan-out. |
| OSDU error paths | Throttling (429), server errors and refusals are exercised against stub handlers (`HttpTests`, `EngineTests`). Live, only the DDMS 422 and the reference curve refusal were provoked. | Cases that can be provoked without writing data, such as an unknown legal tag or a missing ACL group. |
| Other OSDU kinds | Only Wellbore, WellLog, Document and `dataset--File.Generic` were delivered live. | A mapping and a small drop per further kind. |
| A crash inside a registration call | Defect 19's fix marks the step with its landing-zone path before the request and takes over the dataset that path became. Four tests in `FileProtocolTests` cover the mark, the takeover, the re-registration when search lists nothing and the hold when it lists two, but neither live crash test landed inside the call. | A crash injected between the request going out and its answer reaching the ledger, on a flow whose file is already staged. |
| A well log rendered with the corrected porosity unit | Defect 17 is fixed in the sample estate, pinned by a regression test that renders the sample mapping's own curve-unit entry against the sample template and cache, and the live cache holds `m3/m3`. No live record carries it: the catalog re-mint left the ledger without the versions OSDU holds, and the DDMS refuses to create a log it already has. | The ledger re-established against what OSDU holds (a known-state or verify run), then one metadata redelivery of L-2001. |
| Reserved drop-off uploads | A file of a few gigabytes is reserved, written straight to storage with a user delegation SAS, and the reservation completed (`POST /dropoffs/reserve`, `POST /dropoffs/{id}/complete`). `DeliveryDropOffReserveApiTests` covers the route, the refusals and the completion checks, but no reserved upload has run live: every live drop-off carried its bytes through the control plane. | The drop-off area on Azure Storage and the control plane's identity holding Storage Blob Delegator on the account, plus CORS for a browser writing directly. |
| A submission's own reference | A submission carries the caller's name for the work (a filename, a ticket, a job id), stored, indexed and searchable, and a repeat that relabels the work is refused. Covered by `SubmissionReferenceTests` and `DeliverySubmissionApiTests`. Never sent live. | One live submission carrying a reference, and one repeat that changes it. |
| Folded reference names | A mapping can match a name against the cache with punctuation and spacing folded away, opt-in per cache entry (`ignoreSeparators`), running only after exact and case-insensitive comparison find nothing. Covered by `ReferenceFoldTests` against a built cache. Never resolved against the live partition's own names. | One live mapping entry opted in, against a facility whose name OSDU spells differently from the drop. |
| Repeatable live tests | The live drivers are scripts outside the repository, so nobody else can re-run them, and the suites have no live integration tests. | The drivers moved into the repository, keeping the action log and marker rules. |

### 4.3 Known defects not yet fixed

None stand open. The three that did (the porosity unit, approval-held records counted as unchanged, and a
registration whose answer was lost) are fixed in 215bee2, and are rows 17 to 19 of section 3.

How far each was proven: rows 18 and 19 carry tests that fail on the code before them, and the re-minted live
catalog reports the new count on every run and submission. Row 17 is pinned by a regression test that renders
the sample mapping's own curve-unit entry against the sample template and cache, and by the live cache, which now
holds `m3/m3`. The live well logs were not re-rendered with it, because the catalog re-mint left the ledger
without the versions OSDU holds, and the wellbore DDMS refuses to create a log it already has.

Two of them are fixed without live proof, and section 4.2 carries what that would take: a crash inside a
registration call for row 19, and a well log rendered again with the corrected unit for row 17.

### 4.4 Deferred by decision

- A volume test: the partition has no purge, so volume would stay behind.
- Migrations: needed only once a production catalog exists.
- Purge and the irreversible removal scopes (`History`, `Everything`): they need `service.storage.admin`, which the
  app registration does not hold. They are covered by `RemovalTests`, `OsduWireTests` and `FileProtocolTests` against
  stub handlers.

## 5. What the tests left in the partition

No record is still live. The drop notification run of section 2.11 delivered two wellbores again at 10:33 UTC on
2026-09-12 (`test:master-data--Wellbore:8caec6614b605fe6809175534f9c9a1c` and
`test:master-data--Wellbore:250b474c3f1550c59dc7a11c81ca63c3`, both at version 1789209164941463). With the owner's
approval they were soft-deleted on 2026-09-17, and a GET on each answered 404, as did one on a wellbore of section 2.8
whose check had not been logged. `.sqlflow/live-e2e/test-data.md` lists every id the live tests created and its state;
the action log beside it holds every call.

Everything this estate created was removed at a reversible scope (a soft delete, `POST /records/{id}:delete`, or the
ledger's `record` scope) and then checked, and none of it resolves. OSDU can restore any of it, and a flow can send it
again. It carried the run marker, and every write and the cleanup itself are in the action log:

- the four wellbores: the two above, and `test:master-data--Wellbore:94a321a3935358d6b059c613aaeb3a4c` and
  `test:master-data--Wellbore:a67c8651d48d5cb4b303944c494a47b5`, which the records sent in the request (section 2.8)
  created;
- `test:work-product-component--WellLog:1f2c3bd61925503cad8694c176cd81b5` (L-1001),
  `test:work-product-component--WellLog:b1bf9310a1d65a1a83085d675c355c47` (L-1002) and
  `test:work-product-component--WellLog:27d8b3f959b552758cacd54907871c49` (L-2001), removed at the ledger's `record`
  scope (the Wellbore DDMS's logical delete);
- the four documents: `test:work-product-component--Document:cf34948ac9975026bcaf9edfda6ee755` (`e2e-document`),
  `test:work-product-component--Document:b57668a195ad5b129d8f4d51197e4ad8` (`e2e-file`), and the two that sections 2.9
  and 2.10 created and removed within their own test;
- the ten `dataset--File.Generic` records their files were registered as. A payload change registers a new dataset and
  leaves the earlier one in OSDU, as [protocols.md](protocols.md) describes.

A soft delete does not remove everything. What stays is the bulk data of the three well logs, which the Wellbore DDMS
keeps under its logical delete, and the files behind the ten `dataset--File.Generic` records, in the platform's file
storage.
