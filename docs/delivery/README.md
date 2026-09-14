# The delivery domain

How OSDU Delivery turns a prepared drop into OSDU records and keeps every record traceable. The platform
(control plane, catalog, nodes, schedules, GUI) is described in [../architecture.md](../architecture.md); these
pages describe the flow kinds that run on it, `flowType: delivery`, `flowType: retrieval` and `flowType: cache`, and
the ledger behind them.

| Page | What it covers |
| --- | --- |
| [design.md](design.md) | The design: why the drop is a storage handoff, the four render inputs and the render context, identity, change detection and the cache, the ledger, the delivery protocols, the document model, the preflight gate, streaming, the retrieval kind (section 15), the streaming intake, work batches, returned values and fan-out (section 16). Section numbers are referenced from the code. |
| [documents.md](documents.md) | The delivery flow, retrieval flow, cache flow and mapping documents key by key. |
| [mapping-templates.md](mapping-templates.md) | Templates and mappings: the template an OSDU schema becomes and where it is saved, the mapping format entry by entry (sources, `findBy`, modifiers, `appliesWhen`, `required`, fixtures), the checks, and the mapping builder. |
| [drop-contract.md](drop-contract.md) | What the preparing side writes: the manifest, the parquet scopes, the payload chunks, the delivery key and the payload hash it must derive. |
| [preparing-a-drop.md](preparing-a-drop.md) | The practical guide for the preparing side: what a drop folder must contain, the manifest field by field, well log files (one file per log, unique and continuing row labels when split), document files, how to hand a drop over, incremental prepare, what each mistake leads to, and a checklist. |
| [submitting-records.md](submitting-records.md) | The other way in, for a source system: sending records' metadata in the submission itself instead of preparing a drop. What a record looks like, the request and its answers, idempotency, the preview, and what each mistake leads to. |
| [osdu-testing.md](osdu-testing.md) | The state of testing against OSDU: how it is tested, what was proven live per protocol and feature, the defects the live runs found and their fixes, and what is still missing. |
| [ledger.md](ledger.md) | The ledger tables in the catalog's `delivery` schema, the cache versions, the record lifecycle, leasing, the indexes behind every listing, retention. |
| [protocols.md](protocols.md) | The named delivery protocols (`osduRecord`, `osduWellLog`, `osduFile`, `osduManifest`), their steps and returned values, and how to add one. |
| [operations.md](operations.md) | Running it: the API and the GUI surfaces, the CLI verbs, first deployment, the runbook. |
| [decisions/](decisions/README.md) | The decision records. |

## Where the code lives

| Path | Responsibility |
| --- | --- |
| `src/SqlFlow.Delivery` | The whole domain: model, identity, canonical JSON and hashing, the document loader and the three flow kinds, templates (the template model, the catalog store, schema capture and import) and the mapping builder, the cache store over the catalog, rendering, planning, the preflight gate, the HTTP runtime, storage (drop reader, disk spill, work batch files, writers), the ledger over the catalog, the engine (intake, worker, verifier, known state, the four protocols, fan-out, the retrieval runner, the cache refresh and its impact analysis), the run executors, the compute operations, and the sync of mappings and cache definitions into the catalog. |
| `src/SqlFlow.Catalog/DeliveryEntities.cs` | The ledger's EF model (schema `delivery`), the cache versions included. |
| `src/SqlFlow.ControlPlane/Api/DeliveryEndpoints.cs` | The delivery API under `/api/v1/delivery`. |
| `src/SqlFlow.ControlPlane/Api/DeliveryTemplateEndpoints.cs` | Templates and the mapping builder under `/api/v1/delivery`. |
| `src/SqlFlow.Cli/DeliveryVerbs.cs` | `sqlflow check`, `sqlflow cache` and `sqlflow template`. |
| `gui/src/features/delivery` | The delivery overview, the flow tabs (stats, records, submissions; retrievals for a retrieval flow), the record page, the submission page with its batches, the audit trail, mappings, the OSDU cache page, templates and the mapping builder. |
| `samples/recall-welllog` | A complete sample estate: a flow, its mappings (the well logs it delivers from a drop, and wellbore master data for the records a source submits through the API), the cache flow its mappings read (`caches/`), the bundled schemas its templates are saved from (`templates/`), sample cache records to import for work without OSDU (`references/`), and a generated drop. |
| `tools/SampleDrop` | Generates realistic drops for the samples and the tests. |
| `tests/SqlFlow.Delivery.Tests` | The domain suites: core, documents, storage, HTTP, the ledger on SQLite, and the engine end to end over the sample estate with a fake protocol. |

## A flow's repository

A delivery flow lives in a git repository the control plane syncs, next to what it renders with:

```text
repo/
  flows/recall-welllog.yaml          flowType: delivery; reads the cache of the partition it delivers to
  mappings/WellLog@1.4.0.yaml        documentType: mapping, pinned by name and version
  caches/osdu-reference-cache.yaml   flowType: cache: the OSDU types it caches for its partition and the paths it keeps
```

The flow finds `mappings/` by walking up from its own file, or names it under `render.mappings`. A cache flow can sit
anywhere in the tree; the sample keeps it under `caches/`. The sync projects every flow as a pipeline, and the
mappings and the types each cache flow declares as read models the GUI lists. Neither templates nor cache contents are
in the repository: the template a mapping pins is saved in the catalog, captured from OSDU or imported from a bundled
schema file ([mapping-templates.md](mapping-templates.md)), and every version of a partition's cache is written into the
catalog by the runs of the cache flows that fill it ([documents.md](documents.md#cache-flow)). OSDU Delivery only reads the repository.
