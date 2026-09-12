# OSDU testing: what is done and what is missing

The state of OSDU Delivery's testing against OSDU on 2026-09-12: what has been proven against a live OSDU platform,
what the automated suites cover without one, which defects the live runs found and how they were fixed, and what is
still missing. The practical guide for the preparing side is [preparing-a-drop.md](preparing-a-drop.md); the runbook
is in [operations.md](operations.md).

## 1. How it is tested

Testing runs at three levels.

| Level | What runs | OSDU involved |
| --- | --- | --- |
| Automated suites | `tests/SqlFlow.Delivery.Tests` (340 test methods: the domain, the ledger on SQLite, the engine end to end over the sample estate with a fake protocol, the HTTP runtime against stub handlers), `tests/SqlFlow.ControlPlane.Tests` (328), `tests/SqlFlow.Core.Tests` (131). The database suites run against the disposable `SQLFLOW_TEST_DB` catalog. A `[Theory]` counts once. | No |
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

| Flow | Kind and protocol | What it delivers |
| --- | --- | --- |
| `e2e-wellbore` | delivery, `osduRecord` | 2 `master-data--Wellbore` records |
| `recall-welllog` | delivery, `osduWellLog` | 3 `work-product-component--WellLog` records and their wellbore DDMS bulk data: L-1001 (MD, GR, RHOB), L-1002 (MD, GR), L-2001 (MD, NPHI) |
| `e2e-document` | delivery, `osduManifest` | 1 `work-product-component--Document` with a CSV file, through `Osdu_ingest` |
| `e2e-file` | delivery, `osduFile` | 1 `work-product-component--Document` with a CSV file, through the file service |
| `e2e-cache-sync` | retrieval | Wellbore and reference data read into the OSDU cache that `recall-welllog` renders from |

Runs held by the live catalog between its re-mints on 2026-09-10 and 2026-09-12 (the second re-mint added
`delivery.InlineSubmission`; a full database backup was taken first, and the runs before the first re-mint are in the
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

Wellbore master data sent to `e2e-wellbore` as records in the request ([submitting-records.md](submitting-records.md)),
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

## 4. What is missing

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
| The drop notification endpoint | `POST /api/v1/delivery/submissions` with a `drop` is how the prepare side starts a run. `DeliverySubmissionApiTests` now covers the route (both forms), but the drop form has never been used live: the live runs were started through `POST /api/v1/runs` or with inline records. | One live notification of an existing drop. |
| Curves that do not match the bulk columns | A log whose record declares a curve its bulk data lacks, or the reverse, is delivered as it is. Only the reference curve is checked. | A preflight check comparing the declared curves with each chunk's columns, and one live log that trips it. |
| Sessions that split a log's curves | Chunks sharing row labels with different columns are allowed by the preflight and covered by engine tests, but never committed live. | One log re-prepared as two column chunks. |
| Payloads near the ceilings | Live chunks held 9 rows. The 10,000,000-value and 3,000-column ceilings are checked from parquet footers in tests only, and the largest body ADME accepts on a single `POST /data` is not known. | One larger log. It leaves a larger bulk version in a partition that cannot be purged, which the small-dataset rule weighs against. |
| Partitioned drops and fan-out | Every live drop was unpartitioned and every run a single member. Covered by `ScaleStorageTests` and `ScaleEngineTests`. | A partitioned copy of an existing drop, delivered with a fan-out. |
| OSDU error paths | Throttling (429), server errors and refusals are exercised against stub handlers (`HttpTests`, `EngineTests`). Live, only the DDMS 422 and the reference curve refusal were provoked. | Cases that can be provoked without writing data, such as an unknown legal tag or a missing ACL group. |
| Other OSDU kinds | Only Wellbore, WellLog, Document and `dataset--File.Generic` were delivered live. | A mapping and a small drop per further kind. |
| Repeatable live tests | The live drivers are scripts outside the repository, so nobody else can re-run them, and the suites have no live integration tests. | The drivers moved into the repository, keeping the action log and marker rules. |

### 4.3 Known defects not yet fixed

| Defect | Effect | State |
| --- | --- | --- |
| Neutron porosity unit | [WellLog@1.4.0.yaml line 142](../../samples/recall-welllog/mappings/WellLog@1.4.0.yaml#L142) (and the live estate's `WellLog@1.4.1`) maps the source unit `V/V` to `%`, while NPHI holds fractions (0.19 to 0.22), so OSDU consumers read porosity 100 times too small. | Waiting on the choice between a volume fraction unit from the reference data and a conversion in prepare. |
| Approval-held records in run counts | Records held behind a cache change waiting for approval are counted as `skippedUnchanged`, so a run report does not show that they wait. | Needs a ledger column, which means re-minting the catalog. |
| Registration between a crash and its step report | If a process dies after registering a dataset but before the ledger records that step, the recovered run registers a second dataset and the first stays in OSDU unreferenced. Not observed in either crash test. | Open. |

### 4.4 Deferred by decision

- A volume test: the partition has no purge, so volume would stay behind.
- Migrations: needed only once a production catalog exists.
- Purge and the irreversible removal scopes (`History`, `Everything`): they need `service.storage.admin`, which the
  app registration does not hold. They are covered by `RemovalTests`, `OsduWireTests` and `FileProtocolTests` against
  stub handlers.

## 5. What the tests left in the partition

All live, all carrying the marker, with every write in the action log:

- `test:master-data--Wellbore:8caec6614b605fe6809175534f9c9a1c` and `test:master-data--Wellbore:250b474c3f1550c59dc7a11c81ca63c3`;
- `test:work-product-component--WellLog:1f2c3bd61925503cad8694c176cd81b5` (L-1001),
  `test:work-product-component--WellLog:b1bf9310a1d65a1a83085d675c355c47` (L-1002) and
  `test:work-product-component--WellLog:27d8b3f959b552758cacd54907871c49` (L-2001), with their bulk data versions;
- `test:work-product-component--Document:cf34948ac9975026bcaf9edfda6ee755` (`e2e-document`) and
  `test:work-product-component--Document:b57668a195ad5b129d8f4d51197e4ad8` (`e2e-file`);
- the `dataset--File.Generic` records their files were registered as. A payload change registers a new dataset and
  leaves the earlier one in OSDU, as [protocols.md](protocols.md) describes.
