# Preparing a drop

For the team that prepares data for OSDU Delivery (Databricks, or any other producer). A **drop** is one folder of
files that describes a set of records to deliver: the rows that become the OSDU documents, the payload files that go
with them (well log curves, document files), and a manifest that ties them together. OSDU Delivery reads the drop,
renders each record with the flow's pinned mapping, and sends only what changed.

This guide is the practical side. [drop-contract.md](drop-contract.md) is the reference for the algorithms (the
delivery key, the payload hash, the known state), and [protocols.md](protocols.md) says what each OSDU protocol does with
the files. Everything below is checked by the delivery side; where a rule was learned on a live OSDU service, that is
said.

## 1. The drop as a whole

- One drop per prepare run of one flow and one set of flow parameter values (for the Recall well logs: one drop per
  log source, such as `STAT_COMP`).
- The folder is readable by the delivery service's identity: an `abfss://` path or an external volume with an explicit
  location, or a local path for development.
- Write every data file first and `manifest.json` **last**. The manifest's presence is what says the drop is complete.
- Do not change a drop after its manifest is written. A new set of changes is a new drop, with a new `submissionId`.
- File paths in the manifest are relative to the drop folder and stay inside it (no absolute paths, no `..`).

## 2. Layout

A well log drop:

```text
{drop}/
  manifest.json                                  written last
  metadata/part-00000.parquet                    root scope: one row per well log
  curves-meta/part-00000.parquet                 child scope: one row per curve, keyed by the log's delivery key
  curves/{deliveryKey}/chunk_00000.parquet       the log's bulk data (one file, or several: see section 6)
```

A document drop with attached files:

```text
{drop}/
  manifest.json
  metadata/part-00000.parquet                    one row per document
  files/{deliveryKey}/report.csv                 the document's file(s)
```

Folder and file names are free as long as the manifest lists the scope files and the payload path template matches the
payload files. The names above are the ones the sample estate and the flows use.

## 3. The manifest

```json
{
  "manifestVersion": 1,
  "submissionId": "7d5a2d4c-3f0e-4b6b-9c1a-0d2e8f7a6b51",
  "flow": "recall-welllog",
  "parameters": { "logSource": "STAT_COMP" },
  "mapping": "WellLog@1.4.1",
  "createdUtc": "2026-09-11T12:00:00Z",
  "recordCount": 3,
  "partitioned": false,
  "sourceVersions": { "wl_pipelines_dsis_intermediate.recall_logcurve_enriched": 4012 },
  "scopes": {
    "record": {
      "files": ["metadata/part-00000.parquet"],
      "columns": [
        { "name": "deliveryKey", "type": "string" },
        { "name": "log_run", "type": "string" },
        { "name": "update_date", "type": "string" },
        { "name": "payloadHash", "type": "string" },
        { "name": "chunkCount", "type": "long" }
      ]
    },
    "curves": {
      "files": ["curves-meta/part-00000.parquet"],
      "parentKey": "deliveryKey",
      "orderBy": "curve_ordinal",
      "columns": [
        { "name": "deliveryKey", "type": "string" },
        { "name": "curve_ordinal", "type": "long" },
        { "name": "curve_id", "type": "string" }
      ]
    }
  },
  "payloads": {
    "curves": {
      "pathTemplate": "curves/{deliveryKey}/chunk_*.parquet",
      "hashColumn": "payloadHash",
      "chunkCountColumn": "chunkCount",
      "contentType": "application/x-parquet"
    }
  }
}
```

(The column lists are shortened here; a real manifest declares every column its files carry.)

| Field | Rule |
| --- | --- |
| `manifestVersion` | `1`. |
| `submissionId` | A new UUID for every prepare run. It is the idempotency key: posting the same drop again never delivers it twice, and a drop that reuses the id of a submission already delivered is treated as already processed, so it sends nothing new. |
| `flow` | Exactly the flow's name. A different name refuses the drop. |
| `mapping` | Exactly the `Name@version` the flow pins (`render.mapping`). A different version refuses the drop; re-prepare it, or promote the flow deliberately. |
| `parameters` | The flow parameter values the drop was prepared with. They must equal the values of the run that delivers it. |
| `createdUtc`, `recordCount` | When the drop was prepared and how many root rows it holds. Informational. |
| `sourceVersions` | The commit version of each source table the drop was built from. A run in which no source table advanced since the last delivery, and the render context (mapping, template and cache versions) did not move either, skips the whole drop without reading it (unless forced). So these must increase whenever the source data moved. |
| `partitioned` | `false` unless the scopes are co-partitioned: then root file *i* and each child scope's file *i* hold the same records, every scope lists the same number of files, and every file is sorted by the delivery key's text. An unsorted file is refused. `false` works for any layout. |
| `scopes.record` | The root scope: `files` and `columns`. Required. |
| `scopes.<child>` | Child scopes (for example `curves`): `files`, `columns`, `parentKey` (the column holding the parent's delivery key, required) and `orderBy` (orders the rows within one parent). |
| `payloads.<name>` | Where a record's payload files are: `pathTemplate` (must contain `{deliveryKey}`; the last segment is a file pattern), `hashColumn`, `chunkCountColumn`, `contentType`. The name must be the one the flow streams (`target.protocolOptions.payload`). |

Column types are `string`, `long`, `double`, `boolean` and `timestamp`. Parquet files need only top-level scalar
columns. Keys the manifest does not define are a parse error, and every refusal names the manifest and the rule.

Every column the mapping reads must be declared in the scope it reads it from, and so must the columns the flow names
(`source.lastModified` or `source.fingerprint`, the payload's `hashColumn` and `chunkCountColumn`). A run checks this
before it reads a row and refuses the drop naming the missing column.

## 4. The root rows

One row per record to deliver.

- **`deliveryKey`**: required when the drop has child scopes or payloads. The lower-case, hyphenated UUID derived from
  the mapping's dataset key (section 5). A row whose declared key differs from the one the delivery side derives is held.
- **The dataset key columns** (the columns the mapping's `dataset.key` names): never null. A
  null makes the key underivable and the record is held.
- **Every column the mapping reads**, with the values the mapping expects. The mapping YAML in the flow's repository is
  the list: its entries name the columns of the dataset's row as `dataset.<column>` (in `source`, `findBy` and
  `appliesWhen`), and a child dataset's as `dataset.<child>.<column>`, read from the scope of that name.
- **The version column** the flow names:
  - `source.lastModified` (for Recall, `update_date`): when the row last changed, declared as `timestamp` or as `string`
    holding RFC 3339 text (`2026-09-11T12:00:00Z`). It must move forward whenever the row or its payload changes. A row
    carrying an **older** moment than the version already delivered is recorded as stale and never sent. A row carrying
    the same moment, the same payload hash and the same render context is skipped without rendering.
  - or `source.fingerprint`: any value that changes whenever the row changes, compared only for equality.
- **The payload columns**, when the record has payload files: the `hashColumn` (section 6.1) and, recommended, the
  `chunkCountColumn`.

Seen live: a re-prepared row whose `update_date` was earlier than the one already delivered was skipped as stale, so the
change in it never left. When a row changes, its moment has to be later than any the record has been delivered at.

## 5. The delivery key

Both sides derive the key independently and must agree.

```python
import uuid

ROOT = uuid.UUID("6b6d1c3e-3a3c-5d0a-9f76-0f4a2c1e8d21")
KEY_NS = uuid.uuid5(ROOT, "delivery-key")

def delivery_key(source_system: str, *values: str) -> str:
    name = source_system.strip().lower() + "".join("\x1f" + v.strip() for v in values)
    return str(uuid.uuid5(KEY_NS, name))

# delivery_key("recall", "NO_15_9", "L-1001") == "ac3a5843-e5cc-5e7e-9ecf-83fc05872909"
```

`source_system` is the mapping's `dataset.system`; the values are those of the columns its `dataset.key` names, in the
order the mapping lists them. The OSDU record id is `{partition}:{entityType}:{deliveryKey without hyphens}`. See
[drop-contract.md](drop-contract.md#the-delivery-key) for the exact byte-level definition.

## 6. Payload files

### 6.1 Rules for every payload

- Put a record's files under the payload's `pathTemplate` with `{deliveryKey}` filled in, for example
  `curves/ac3a5843-e5cc-5e7e-9ecf-83fc05872909/chunk_00000.parquet`.
- Files are taken in **ordinal file-name order**, so zero-pad the numbers: `chunk_00009` then `chunk_00010`. Unpadded
  names sort `chunk_10` before `chunk_9`.
- Never write an empty or partial file. A record whose hash column has a value but whose folder holds no matching
  files is held.
- **`hashColumn`**: a value that changes when, and only when, the payload's content changes. The delivery side compares
  it with what it delivered last: a new value sends the payload again, the same value does not. Hash the logical
  content rather than the file bytes, so rewriting the same data does not look like a change
  ([drop-contract.md](drop-contract.md#the-payload-hash) has the reference algorithm). A flow that sets
  `change.payloadDetect: lastModified` uses the files' modified times instead and needs no hash column.
- **`chunkCountColumn`**: the number of files for the record. It spares the delivery side a storage listing per record
  when planning, so declare it, and keep it equal to the files written.
- A file larger than the estate's request body ceiling holds the record before anything is sent, when the flow declares
  that ceiling (`reliability.maxRequestBodyBytes`).

### 6.2 Well logs (protocol `osduWellLog`)

The bulk data of a well log is a Parquet file with one row per sample (depth or time) and one column per curve,
including the reference (index) curve. It goes to the wellbore DDMS in one of two ways, decided by the number of files:

| Files for the record | How it is sent |
| --- | --- |
| One | A single request (`POST /welllogs/{id}/data`) that replaces the log's bulk. Verified live. |
| Two or more | A DDMS session: one request per file, in file-name order, then a commit that aggregates them into one version. |

**Write one file per well log whenever you can.** The wellbore DDMS takes one file in a single request up to:

- **10,000,000 values** per file (rows times columns), and
- **3,000 columns** per file (500 if the target runs OSDU M23 or M25).

These limits belong to the DDMS and cannot be raised by configuration. The delivery side reads each file's Parquet
footer before sending and holds a record whose file exceeds either one, naming the file and the limit. The request body
size allowed by the estate (the API gateway, the ingress, the web server in front of the DDMS) is a separate ceiling:
agree it with the platform team and declare it as `reliability.maxRequestBodyBytes` so an oversized file is held with a
clear message instead of failing in transit.

**Every row label in a file must be unique.** The DDMS refuses a file whose row index repeats a label
(`422 Bulk error: Duplicated index found`, seen live); the record is held and nothing is written.

**Only split a log when it is above the limits.** The DDMS aggregates a session's files **by row label**: the index a
dataframe reader such as pandas gives each file. A file whose labels another file already used replaces those rows
instead of adding its own, and the commit still reports success. Seen live: a 9-row log split into files of 5 and 4 rows
that both numbered their rows from zero committed a log of **5 rows**. So:

- **Splitting rows** (the usual case): the row index must **continue** from one file to the next. File 1 holds labels
  0 to 4, file 2 holds 5 to 8, and so on. Write it either as a stored index column or as a pandas `RangeIndex` that
  starts where the previous file ended:

  ```python
  import pandas as pd

  def write_row_chunks(grid: pd.DataFrame, folder: str, rows_per_file: int) -> int:
      """Split one log's grid into files whose row index continues across them."""
      grid = grid.reset_index(drop=True)            # labels 0..n-1 for the whole log
      count = 0
      for start in range(0, len(grid), rows_per_file):
          part = grid.iloc[start:start + rows_per_file]   # keeps the labels start..start+len-1
          part.to_parquet(f"{folder}/chunk_{count:05d}.parquet", index=True)
          count += 1
      return count
  ```

  `iloc` keeps the whole log's labels on each slice, and `index=True` writes them. Do **not** call `reset_index` per
  file: that restarts every file at zero.

- **Splitting curves** (a log with more curves than the column limit): every file carries **the same index for the same
  rows**, each with its own subset of curves. Files with exactly the same labels and different curves are aggregated side
  by side. (This case follows the DDMS description and is covered by the delivery side's tests; it has not yet been
  exercised against a live DDMS.)

- **Spark writers**: a Parquet file written by Spark carries no pandas metadata, so the DDMS numbers its rows from zero.
  That is fine for a single file, and wrong for any split: write split files with pandas (for example inside the grouped
  map that already holds the grid) so each file carries its row labels.

The delivery side enforces this. Before a session opens it reads every file's labels from its footer and holds the record
when two files give the same labels to different rows, naming both files. After the commit it reads the log back and
holds the record when the log holds fewer rows or curves than the files carried.

### 6.3 Documents with files (protocols `osduFile` and `osduManifest`)

- Any file type. The flow declares the content type the files are uploaded with
  (`target.protocolOptions.payloadContentType`, for example `text/csv`).
- Each file becomes one dataset record in OSDU, and the document's `Datasets` list points at them.
- No empty files, and no file above `reliability.maxRequestBodyBytes` when the flow declares it: either holds the record.
- A changed file (a new hash value) is uploaded and registered as a new dataset, and the document is rewritten to point
  at it. The earlier dataset stays in OSDU. A changed document with unchanged files rewrites only the document.

## 7. Handing the drop over

Two ways, depending on how the flow is run. (A third way in needs no drop: a source with a handful of metadata records
and no payload files sends them in the call itself, and the run writes them out as a drop. See
[submitting-records.md](submitting-records.md).)

**Notify the service** (a drop per folder). After the manifest is written, one authenticated call with a token that has
the `operate` scope:

```http
POST /api/v1/delivery/submissions
Authorization: Bearer <token>
Content-Type: application/json

{
  "flow": "recall-welllog",
  "drop": "abfss://lake@acct.dfs.core.windows.net/osdu-prepare/STAT_COMP/2026-09-11T1200",
  "parameters": { "logSource": "STAT_COMP" }
}
```

Name the flow by `flow` (add `repoId` when the same flow name exists in more than one repository) or by `pipelineId`.
Optional: `force` (re-plan past the change gates) and `pool` (route the run to a worker pool). The service answers
`202 Accepted` with the run id (`{ "runId", "pipelineId", "flowName", "status" }`) and `Location: /api/v1/runs/{runId}`;
`GET /api/v1/runs/{runId}` reports the run's progress and outcome.

**Write to the flow's declared location** (a scheduled flow). The flow's `source.location` (with its `{parameter}` tokens
filled in) is where each scheduled run reads. Replace the drop there as a whole: remove the previous `manifest.json`
first, write the data files, and write the new `manifest.json` last, so a run never reads a half-written drop.

## 8. Incremental prepare and the known state

A drop may carry every record, or only the ones that changed: a run plans the rows it is given and leaves every other
record as it is. To prepare only what changed, read the **known state** the delivery side publishes
(`known-state.parquet` and `known-state.json`, at the flow's `source.knownState` location) at the start of each prepare:

| Column | Use |
| --- | --- |
| `deliveryKey`, `sourceKey` | Which record the row is. |
| `sourceFingerprint`, `sourceModifiedUtc` | The version delivered. Include a row whose source moved past it. |
| `metadataHash`, `payloadHash` | What was delivered. A row whose payload hash is unchanged needs no grid built and no file written. |
| `status` | Include every key whose status is not `delivered`: a held, failed or deleted record needs its row again once it is released. |
| `targetId`, `targetVersion` | The OSDU id and version it landed as. |

A record whose delivered copy drifted in OSDU (someone edited it there) and was reconciled by a verify run loses its
fingerprint and hashes in the known state, so the next incremental prepare includes it again. Watermarks cannot see
deletions: run a full prepare periodically, or use the source tables' change feed.

## 9. What happens when something is wrong

| What you see | Why | What to do |
| --- | --- | --- |
| The run fails: "the drop was prepared for flow/mapping ..." | `flow` or `mapping` does not match the flow | Re-prepare for the flow's pinned mapping. |
| The run fails: "parameter ... was prepared with ..." | The run's parameter values differ from the manifest's | Submit with the values the drop was prepared for. |
| The run fails: "... names column ..., which the drop's root scope does not declare" | A column the mapping or flow needs is not in the manifest | Declare it (and write it). |
| The run fails: "invalid manifest" | Unknown key, missing root scope, scope without files or columns, absolute path, `pathTemplate` without `{deliveryKey}` | Fix the manifest as the message says. |
| A record is `held`: key mismatch or incomplete dataset key | The declared `deliveryKey` differs from the derived one, or a key value is null | Fix the row, re-prepare, and release the record. |
| A record is `held`: no chunk files, empty file, file too large | The payload folder is empty, a file is empty, or a file is above a ceiling | Write the files correctly (section 6), re-prepare, release. |
| A well log is `held`: "give the same row labels to different rows" | Split files restart or overlap their row index | Write continuing labels (section 6.2), re-prepare, release. |
| A well log is `held`: "Duplicated index found" | A file repeats a row label | Make the labels unique, re-prepare, release. |
| A record is skipped as `stale` | Its `update_date` is older than the version delivered | Give the changed row a later moment. |
| Nothing is sent for a changed drop | The `submissionId` was reused, or `sourceVersions` did not move | Use a new `submissionId`; advance the source versions. |

Held records stay held until an operator releases them (the flow's Records tab, or
`POST /api/v1/delivery/flows/{pipelineId}/release`), so a fix is a new drop plus a release.

## 10. Checklist

- [ ] One folder per prepare run; `manifest.json` written last; nothing changed afterwards.
- [ ] A new `submissionId` for every run; `flow`, `mapping` and `parameters` match the flow exactly.
- [ ] `sourceVersions` increase whenever the source moved.
- [ ] Every column the mapping and the flow need is declared, with a supported type.
- [ ] `deliveryKey` derived with the shared algorithm; dataset key values never null.
- [ ] `update_date` (or the fingerprint) moves forward on every change, as a timestamp or RFC 3339 text.
- [ ] Payload files under the `pathTemplate`, zero-padded names, none empty.
- [ ] `payloadHash` changes exactly when the content changes; `chunkCount` equals the files written.
- [ ] Well logs: **one file** per log below 10,000,000 values and 3,000 columns; unique row labels in every file.
- [ ] Split well logs only above the limits: row splits continue the row index, curve splits share it, written with pandas.
- [ ] Documents: the files the document references, none empty, within the declared size ceiling.
- [ ] The drop is handed over: a submission call, or a complete drop at the scheduled location.
