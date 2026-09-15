# OSDU Delivery, powered by SQLFlow

OSDU Delivery publishes subsurface records (wells, wellbores, well logs, and the other OSDU kinds) into an OSDU platform
and keeps every delivered record traceable: which source file and row it came from, which mapping and cache version
rendered it, every attempt, and the OSDU id and version it landed as.

It is a separate project built as a module on its own copy of SQLFlow. SQLFlow lands and loads the source data; OSDU
Delivery renders and delivers it:

| Step | Flow | What it does |
| --- | --- | --- |
| 1 | SQLFlow pre-ingestion | Lands the source files (CSV, JSON, Parquet) into a raw table and builds its typed view |
| 2 | SQLFlow ingestion (`ing`) | Loads the typed view into a keyed ingestion table, with schema evolution and change detection |
| 3 | OSDU flow | Reads the ingestion tables, renders each record with its pinned mapping and the OSDU cache, and delivers it, recording everything in the ledger |

SQLFlow's lineage orders the three like any other flows.

## Repository layout

| Path | What it is |
| --- | --- |
| `sqlflow/` | SQLFlow, vendored as a squashed git subtree; this project adds only generic extension points to it |
| `osdu/` | Everything OSDU Delivery adds: the module's code, GUI pages, tests, docs and sample estate (see `osdu/README.md`) |
| `tools/check-vendored-sqlflow.sh` | Lists the changes this project made to `sqlflow/` and fails when one mixes in other paths or OSDU code |
| `docs/plan.md` | The rebuild plan: the stages, what each one changes, and the tests that close it |
| `CLAUDE.md` | The project's working rules |

## Updating SQLFlow

SQLFlow improvements are pulled in as one reviewable commit. A conflict can only arise in the files that carry this
project's extension points:

```bash
git subtree pull --prefix=sqlflow --squash B:/SQLFlowV3 main
tools/check-vendored-sqlflow.sh
```

## Status

The repository holds the vendored SQLFlow, the rebuild plan, and the OSDU code copied from the previous implementation into
`osdu/`. That code does not build yet: the stages in `docs/plan.md` add SQLFlow's extension points, wire the module onto
them, and move its data input to ingestion tables.
