# The delivery domain

How OSDU Delivery turns a prepared drop into OSDU records and keeps every record traceable. The platform
(control plane, catalog, nodes, schedules, GUI) is described in [../architecture.md](../architecture.md); these
pages describe the flow kinds that run on it, `flowType: delivery` and `flowType: retrieval`, and the ledger
behind them.

| Page | What it covers |
| --- | --- |
| [design.md](design.md) | The design: why the drop is a storage handoff, the four render inputs and the render context, identity, change detection, the ledger, the delivery protocols, the document model, the preflight gate, streaming, the retrieval kind (section 15), the streaming intake, work batches, returned values and fan-out (section 16). Section numbers are referenced from the code. |
| [documents.md](documents.md) | The delivery flow, retrieval flow and mapping documents key by key. |
| [drop-contract.md](drop-contract.md) | What the preparing side writes: the manifest, the parquet scopes, the payload chunks, the delivery key and the payload hash it must derive. |
| [ledger.md](ledger.md) | The ledger tables in the catalog's `delivery` schema, the record lifecycle, leasing, the indexes behind every listing, retention. |
| [protocols.md](protocols.md) | The named delivery protocols (`osduRecord`, `osduWellLog`, `osduFile`, `osduManifest`), their steps and returned values, and how to add one. |
| [operations.md](operations.md) | Running it: the API and the GUI surfaces, the CLI verbs, first deployment, the runbook. |
| [decisions/](decisions/README.md) | The decision records. |

## Where the code lives

| Path | Responsibility |
| --- | --- |
| `src/SqlFlow.Delivery` | The whole domain: model, identity, canonical JSON and hashing, the document loader and the two flow kinds, snapshots, rendering, planning, the preflight gate, the HTTP runtime, storage (drop reader, disk spill, work batch files, snapshot store, writers), the ledger over the catalog, the engine (intake, worker, verifier, known state, the four protocols, fan-out, the retrieval runner, snapshot capture), the run executors, the compute operations, and the sync of mappings and snapshots into the catalog. |
| `src/SqlFlow.Catalog/DeliveryEntities.cs` | The ledger's EF model (schema `delivery`). |
| `src/SqlFlow.ControlPlane/Api/DeliveryEndpoints.cs` | The delivery API under `/api/v1/delivery`. |
| `src/SqlFlow.Cli/DeliveryVerbs.cs` | `sqlflow check` and `sqlflow snapshot`. |
| `gui/src/features/delivery` | The delivery overview, the flow tabs (stats, records, submissions; retrievals for a retrieval flow), the record page, the submission page with its batches, the audit trail, mappings and snapshots. |
| `samples/recall-welllog` | A complete sample estate: a flow, its mapping, captured snapshots, reference data and a generated drop. |
| `tools/SampleDrop` | Generates realistic drops for the samples and the tests. |
| `tests/SqlFlow.Delivery.Tests` | The domain suites: core, documents, storage, HTTP, the ledger on SQLite, and the engine end to end over the sample estate with a fake protocol. |

## A flow's repository

A delivery flow lives in a git repository the control plane syncs, next to what it renders with:

```text
repo/
  flows/recall-welllog.yaml        flowType: delivery
  mappings/WellLog@1.4.0.yaml      documentType: mapping, pinned by name and version
  snapshots/
    schemas/<kind>.json            bundled JSON Schema, plus .meta.json
    references/<version>/...       one immutable directory per reference snapshot version
    references/current             the version "pinned" resolves to
```

The flow finds `mappings/` and `snapshots/` by walking up from its own file, or names them under `render`. The
sync projects the flow as a pipeline and the mappings and snapshots as read models the GUI lists.
