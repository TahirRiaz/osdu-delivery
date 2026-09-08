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
| `payloads.<name>` | Where the chunks live and which root column carries the logical payload hash. |

When the drop has child scopes or payloads, the root scope must carry a `deliveryKey` column
(the lower-case, hyphenated UUID).

## The notification

One HTTP call per run, after the manifest is written:

```http
POST /submissions
Content-Type: application/json

{ "submissionId": "7d5a2d4c-...", "flow": "recall-welllog", "drop": "abfss://lake@acct.dfs.core.windows.net/osdu-prepare/STAT_COMP", "parameters": { "logSource": "STAT_COMP" } }
```

The service answers `202 Accepted` with `Location: /submissions/{id}`.

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
metadataHash, payloadHash, status, targetId, targetVersion`) and `known-state.json` to a location Databricks
reads at the start of prepare. Rows whose `sourceFingerprint` and `payloadHash` are unchanged need no grid
built and no chunk written; the drop still lists them in the root scope so the delivery run can account for them.

## Source fingerprint

`source.fingerprint` names a root column that moves whenever the source row moves (for Recall,
`recallcommonmodel:WellLog__update_date`). Watermarks cannot see deletions; a periodic full pass without the
fingerprint gate, or Change Data Feed on the source tables, is the backstop.
