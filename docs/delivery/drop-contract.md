# The drop contract

What the preparing side (Databricks) writes and what OSDU Delivery reads ([design.md](design.md) sections 3.1 to 3.3). The
drop is an **external** location (an external volume with an explicit `LOCATION`, or a plain `abfss://`
path) the delivery service's identity can read.

## Layout

```text
{location}/
  manifest.json                          written last: its presence means the drop is complete
  metadata/part-*.parquet                root scope: one row per deliverable
  curves-meta/part-*.parquet             child scope: rows keyed by the parent's delivery key
  curves/{deliveryKey}/chunk_00000.parquet   payload chunks, opaque to the delivery service
```

File names are free; the manifest lists them. Parquet scope files need only top-level scalar columns
(string, integer, double, boolean, timestamp).

## Manifest

```json
{
  "manifestVersion": 1,
  "submissionId": "7d5a2d4c-3f0e-4b6b-9c1a-0d2e8f7a6b51",
  "flow": "recall-welllog",
  "parameters": { "logSource": "STAT_COMP" },
  "mapping": "WellLog@1.4.0",
  "createdUtc": "2026-09-07T12:00:00Z",
  "recordCount": 3,
  "partitioned": false,
  "sourceVersions": { "wl_pipelines_dsis_intermediate.recall_logcurve_enriched": 4012 },
  "scopes": {
    "record": { "files": ["metadata/part-00000.parquet"], "columns": [ { "name": "deliveryKey", "type": "string" }, ... ] },
    "curves": { "files": ["curves-meta/part-00000.parquet"], "parentKey": "deliveryKey", "orderBy": "curve_ordinal", "columns": [ ... ] }
  },
  "payloads": {
    "curves": { "pathTemplate": "curves/{deliveryKey}/chunk_*.parquet", "hashColumn": "payloadHash", "chunkCountColumn": "chunkCount" }
  }
}
```

| Field | Meaning |
| --- | --- |
| `submissionId` | The idempotency key. A UUID minted per prepare run. Re-posting the same id never delivers twice. |
| `flow`, `mapping` | Must equal the flow's name and pinned mapping, or the drop is refused. |
| `parameters` | The flow parameter values the drop was prepared with. |
| `sourceVersions` | Delta commit version per source table, for the tier-0 whole-run gate. |
| `partitioned` | When true, root file i and each child scope's file i hold the same records, every scope declares the same number of files, and every file is sorted by the delivery key's text: the intake joins them partition by partition without a spill, and a fan-out spreads the partitions over member runs. An unsorted file is refused. Without it, child scopes are joined through a disk spill, which works for any layout. |
| `scopes.record` | The root scope. Must declare every column the mapping binds (the preflight gate checks). |
| `scopes.<child>` | Child scopes: `parentKey` names the column holding the parent's delivery key; `orderBy` orders rows within a parent. |
| `payloads.<name>` | Where the chunks live and which root column carries the logical payload hash. The `hashColumn` may be left out only when the flow takes the chunk files' modified times as the payload watermark (`change.payloadDetect: lastModified`). |

When the drop has child scopes or payloads, the root scope must carry a `deliveryKey` column
(the lower-case, hyphenated UUID).

## The notification

One authenticated HTTP call per run, after the manifest is written, with a token that has the `operate` scope:

```http
POST /api/v1/delivery/submissions
Content-Type: application/json

{ "flow": "recall-welllog", "drop": "abfss://lake@acct.dfs.core.windows.net/osdu-prepare/STAT_COMP", "parameters": { "logSource": "STAT_COMP" } }
```

The flow is named by `flow` (with `repoId` when the name exists in more than one repository) or by `pipelineId`;
`force` and `pool` are optional. The submission id is the manifest's, read from the drop. The service answers
`202 Accepted` with `{ "runId", "pipelineId", "flowName", "status" }` and `Location: /api/v1/runs/{runId}`. A scheduled
flow needs no call: its runs read the flow's `source.location`. [preparing-a-drop.md](preparing-a-drop.md) is the
practical guide for the preparing side.

A source with a handful of records and no payload files need not write a drop at all: the same call takes the records
themselves under `records`, and the run writes them out as a drop before it reads anything, so everything below still
describes what is delivered. See [submitting-records.md](submitting-records.md).

## The delivery key

Both halves derive the key independently and must agree ([design.md](design.md) section 5.2). The
renderer holds any record whose declared key differs from the derived one.

```text
root      = 6b6d1c3e-3a3c-5d0a-9f76-0f4a2c1e8d21
keyNs     = uuid5(root, "delivery-key")
name      = lower(trim(sourceSystem)) + ( "\x1f" + trim(value) ) for each natural-key value in order
deliveryKey = uuid5(keyNs, name)
```

`uuid5` is RFC 4122 version 5 (SHA-1) over the namespace bytes in network order followed by the UTF-8 name.
The natural-key values are the source columns of the mapping's `identity.naturalKey` properties, in order.
A null value makes the key underivable and the record is held.

Python reference (standard library only):

```python
import uuid

ROOT = uuid.UUID("6b6d1c3e-3a3c-5d0a-9f76-0f4a2c1e8d21")
KEY_NS = uuid.uuid5(ROOT, "delivery-key")

def delivery_key(source_system: str, *values: str) -> str:
    name = source_system.strip().lower() + "".join("\x1f" + v.strip() for v in values)
    return str(uuid.uuid5(KEY_NS, name))

# delivery_key("recall", "NO_15_9", "L-1001") == "ac3a5843-e5cc-5e7e-9ecf-83fc05872909"
```

The OSDU record id is `{partition}:{entityType}:{deliveryKey without hyphens}`.

## Chunking the payload

The prepare side decides how a wellbore's grid is split into chunk files, and the delivery side refuses a chunk
the target cannot accept. Both sides answer to the same two numbers, which belong to the wellbore DDMS and
cannot be raised by configuring the estate:

- at most **10,000,000 values** per chunk, counting cells (rows times columns);
- at most **3,000 columns** per chunk (500 if the target runs OSDU M23 or M25).

Source: the OpenAPI description of `POST /ddms/v3/welllogs/{record_id}/data` ("> 10 millions values or > 3000
columns" must go through the chunking APIs), and the service's own
`app/bulk_persistence/constants.py` (`WRITE_MAX_TOTAL_VALUES_COUNT`, `WRITE_MAX_COLUMNS_COUNT`). The delivery
side holds them in `SqlFlow.Delivery.Model.WellboreDdmsBulkLimits` and checks every chunk's parquet footer
against them before it sends anything ([protocols.md](protocols.md)); a chunk above either one holds the record
with a message naming the file, the shape and the ceiling it broke.

Slicing by rows keeps every curve in every chunk and is what the ceilings are usually hit by. A wellbore with
more curves than the column ceiling cannot be fixed by row slicing at all: those curves have to be split across
chunks, which the session commit aggregates back into one version.

The chunks of one record have to say which rows they hold, because the session aggregates them by row label (the
index a dataframe reader such as pandas gives each file): a chunk whose labels another chunk already used replaces
those rows instead of adding its own, and the commit still succeeds. Seen live on an M26 service, two chunks of five
and four rows that both numbered their rows from zero committed a log of five rows. So:

- chunks that split a log's rows carry a row index that continues from one chunk to the next, either stored as an
  index column (an explicit integer or depth index written with `to_parquet(index=True)`) or as a pandas `RangeIndex`
  whose start is where the previous chunk ended. A file written without pandas metadata numbers its rows from zero,
  which only a record with a single chunk can afford;
- chunks that split a log's curves carry the same index for the same rows.

The delivery side reads every chunk's labels from its footer before a session opens and holds the record when two
chunks give the same labels to different rows, naming both files. After the commit it reads the log's description
back and holds the record when the log lacks rows or curves the chunks carried.

## The payload hash

Hash the logical grid, never the parquet bytes ([design.md](design.md) section 6.4). Computed inside the
grouped map that already holds the grid in memory, carried in the root scope's `hashColumn`.

```text
lines = [ join(columns, "\x1f") ] + [ join(cell(v) for v in row, "\x1f") for row in rows in index order ]
payloadHash = sha256(("\n".join(lines) + "\n").encode("utf-8")).hexdigest()
cell(v):  "" for null; shortest round-trip repr for floats (Python repr); str(int); "true"/"false"; str otherwise
```

Columns are in the grid's column order (the index column first, then curves sorted by curve id).

```python
import hashlib

def payload_hash(columns, rows):
    def cell(v):
        if v is None: return ""
        if isinstance(v, bool): return "true" if v else "false"
        if isinstance(v, float): return repr(v) if v != int(v) or abs(v) >= 1e15 else str(int(v))
        return str(v)
    text = "\x1f".join(columns) + "\n" + "".join("\x1f".join(cell(v) for v in row) + "\n" for row in rows)
    return hashlib.sha256(text.encode("utf-8")).hexdigest()
```

Note the integral-float rule: `1000.0` is written as `1000`, matching .NET's shortest round-trip form.
The `tools/SampleDrop` project is the reference implementation in C#; its output is what the tests assert.

## The known-state snapshot

After a run a known-state run publishes `known-state.parquet` (`deliveryKey, sourceKey, sourceFingerprint,
sourceModifiedUtc, metadataHash, payloadHash, payloadModifiedUtc, status, targetId, targetVersion`) and
`known-state.json` to a location Databricks reads at the start of prepare. The two `...ModifiedUtc` columns are UTC
timestamps, null unless the flow declares the matching last-modified watermark. Rows whose `sourceFingerprint` (or
`sourceModifiedUtc`) and `payloadHash` are unchanged need no grid built and no chunk written.

A drop may carry every record, or only the records that changed since the last run: the delivery run plans the rows it
is given and leaves every other record as it is. An incremental prepare selects the rows modified after the
`sourceModifiedUtc` the known state holds for their key, plus every key whose `status` is not `delivered` (a held,
failed or deleted record needs its row again once it is released).

## Source version: fingerprint or last modified

`source.fingerprint` names a root column that moves whenever the source row moves, compared only for equality.
`source.lastModified` names a root column saying when the row last changed (for Recall,
`recallcommonmodel:WellLog__update_date`), declared as `timestamp` or as `string` holding RFC 3339 text; it is ordered
as well as compared, so a row older than the version delivered or queued is recorded as stale and never sent. A flow
declares one of the two.

With `change.payloadDetect: lastModified` the payload chunk files are the payload's watermark: storage's modified
times, names and sizes decide whether a payload changed, and chunk files older than what was delivered are stale. Write
chunk files in place of the ones they replace, or keep their modified times, so an unchanged payload is not taken for
a new one.

Watermarks cannot see deletions; a periodic full pass without the gate, or Change Data Feed on the source tables, is
the backstop.
