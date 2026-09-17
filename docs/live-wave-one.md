# Live wave one: the test list to approve

The four routes the previous build proved live have never run against a live OSDU on the current build
([go-live-map.md](go-live-map.md), LIVE-1). This is the list of checks that would prove them again, written out so it
can be approved, amended or refused before anything is sent. **Nothing here runs until you approve it**, and an
approval covers these checks, not later ones (`CLAUDE.md`).

Every check below names what it creates, how many ids, and how each id is removed. The totals are at the end.

## What it needs from you

1. **The partition and endpoint** (decision DEC-1): which OSDU deployment and data partition wave one runs against.
   The previous runs used Azure Data Manager for Energy M26, partition `test`.
2. **The credentials**, as environment references only: the client id and secret the flows already name
   (`${env:OSDU_CLIENT_ID}`, `${env:OSDU_CLIENT_SECRET}`, `${env:OSDU_TOKEN_URL}`, `${env:OSDU_SCOPE}`), plus the
   endpoint and APIM key references. No literal secret is written anywhere.
3. **The entitlements** the app registration holds. Wave one needs to read and write records, use the file and dataset
   services, trigger `Osdu_ingest`, and write to the Wellbore DDMS. It does **not** need
   `users.datalake.admins`, and it never purges.
4. **Confirmation that these ids may be created** in that partition: at most 12 records, each carrying the run marker
   `ODLIVE<date>`, all soft-deleted at the end.

## The estate wave one uses

The live estate is rebuilt from the sample estate (`osdu/samples/recall-welllog`) into `.sqlflow/live-e2e/repo`, which
is git-ignored: two wellbores, three well logs with tiny bulk data, and one document with one CSV file. Its flows are
the ones the previous runs used, on the current build:

| Flow | Route | What it delivers |
| --- | --- | --- |
| `e2e-wellbore` | `storage` | 2 `master-data--Wellbore` |
| `recall-welllog` | `ddms` (Wellbore DDMS well logs) | 3 `work-product-component--WellLog` and their bulk data |
| `e2e-file` | `file` | 1 `work-product-component--Document` with one CSV |
| `e2e-document` | `manifest` | 1 `work-product-component--Document` with one CSV, through `Osdu_ingest` |
| `osdu-reference-cache` | cache | Reads reference data from the partition; creates nothing |

Every record carries the run marker in a data property, so the estate can be found again even if the log were lost.
Every id is appended to `.sqlflow/live-e2e/actions.log` before the call that creates it and again with what came back,
and `.sqlflow/live-e2e/test-data.md` is updated as each is created and removed.

## The checks

| # | Check | What it sends | Ids it creates |
| --- | --- | --- | --- |
| 1 | Reachability | `sqlflow check --connect`, then a target probe per flow | none |
| 2 | Cache refresh | One `refresh` run of the cache flow: reads the partition's reference data | none |
| 3 | Storage route, first delivery | `e2e-wellbore` deliver | 2 `master-data--Wellbore` |
| 4 | Storage route, unchanged re-run | The same run again: nothing is sent | none |
| 5 | Storage route, one revision | One wellbore's description changed: 1 sent, 1 skipped | none (a new version of an existing id) |
| 6 | DDMS route, metadata and bulk | `recall-welllog` deliver: 3 logs, each with a parquet chunk of 9 rows | 3 `work-product-component--WellLog` |
| 7 | DDMS route, payload only | New GR values for one log, same metadata: only that log is sent | none (a new bulk version) |
| 8 | DDMS route, a session | One log's bulk data split into two chunk files (5 and 4 rows), committed as one version | none |
| 9 | DDMS route, two refusals | Two chunks numbering rows from zero (held before a session opens); one file with repeated index labels (the DDMS refuses, the record is held) | none: nothing is written |
| 10 | File route, four stages | `e2e-file`: first delivery, unchanged re-run, metadata-only change, payload change | 1 `work-product-component--Document` and up to 2 `dataset--File.Generic` (the payload change registers a second) |
| 11 | Manifest route | `e2e-document` through `Osdu_ingest`: file uploaded, registered, manifest triggered once search sees the dataset | 1 `work-product-component--Document` and 1 `dataset--File.Generic` |
| 12 | Verify and drift | A verify run per flow; then one record edited in OSDU by hand and verified again, to see drift reported | none |
| 13 | Reconcile | `verify.reconcile: true` on one flow: the drifted record is queued and redelivered | none (a new version) |
| 14 | Known state | A known-state run against what OSDU holds, on a flow whose ledger is cleared first | none |
| 15 | Removal and proof | Every id above removed at the reversible `record` scope, then a GET on each | none |

Checks 1 to 14 are in order; a check that fails stops the wave, and what has been created so far is removed by check 15
before anything else is tried.

## What it creates, in total

| Kind | Count | How it is removed |
| --- | --- | --- |
| `master-data--Wellbore` | 2 | `POST /records/{id}:delete` (the ledger's `record` scope) |
| `work-product-component--WellLog` | 3 | The same, through the DDMS's logical delete where the route uses it |
| `work-product-component--Document` | 2 | The same |
| `dataset--File.Generic` | up to 3 | The same |
| **Total** | **at most 10 records** | all soft-deleted, each proven with a GET that answers 404 |

Nothing is purged. `DELETE /records/{id}` and `DELETE /records/{id}/versions` are never called.

## What a soft delete leaves behind

These stay in the partition after wave one, and the inventory records them as leftovers:

- the files uploaded for checks 10 and 11, which live behind the soft-deleted `dataset--File.Generic` records;
- the bulk data the Wellbore DDMS keeps for the soft-deleted well logs;
- the `Osdu_ingest` workflow run history for check 11;
- the earlier versions of every record, which only a purge removes.

## What wave one does not cover

The routes built since the previous live runs (`dataset`, `workflow`, the other DDMS shapes, `dspdm`, `etp`) are wave
two (LIVE-2), each with its own list and its own cleanup decision, because several of them cannot be removed
reversibly (DEC-2). The `history` and `everything` scopes are LIVE-3 and need `service.storage.admin`.

## After it runs

The results go into `osdu/docs/osdu-testing.md` (what was proven, and every defect it found), the inventory is brought
to zero live ids, and the go-live map's evidence column moves from **C** to live for the four routes.
