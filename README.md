# OSDU Delivery, powered by SQLFlow

OSDU Delivery publishes subsurface records (wells, wellbores, well logs, and the other OSDU kinds) into an OSDU platform
and keeps every delivered record traceable: which source file and row it came from, which mapping and cache version
rendered it, every attempt, and the OSDU id and version it landed as.

It is built as a module on SQLFlow. SQLFlow lands and loads the source data; OSDU Delivery renders and delivers it:

| Step | Flow | What it does |
| --- | --- | --- |
| 1 | SQLFlow pre-ingestion | Lands the source files (CSV, JSON, Parquet) into a raw table and builds its typed view |
| 2 | SQLFlow ingestion (`ing`) | Loads the typed view into a keyed ingestion table, with schema evolution and change detection |
| 3 | OSDU flow | Reads the ingestion tables, renders each record with its pinned mapping and the OSDU cache, and delivers it, recording everything in the ledger |

SQLFlow's lineage orders the three like any other flows.

## Repository layout

| Path | What it is |
| --- | --- |
| `sqlflow/` | SQLFlow, vendored as a squashed git subtree and never edited here |
| `tools/check-vendored-sqlflow.sh` | Fails when `sqlflow/` differs from the SQLFlow commit it was vendored from |
| `docs/plan.md` | The rebuild plan: the stages, what each one changes, and the tests that close it |
| `CLAUDE.md` | The project's working rules |

## Updating SQLFlow

Changes to SQLFlow are made in the SQLFlow repository and brought in as one reviewable commit:

```bash
git subtree pull --prefix=sqlflow --squash B:/SQLFlowV3 main
tools/check-vendored-sqlflow.sh
```

## Status

The repository holds the vendored SQLFlow and the rebuild plan. The OSDU module arrives stage by stage, as `docs/plan.md`
describes.
