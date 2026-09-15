# The delivery domain

How OSDU Delivery turns rows in the ingestion tables into OSDU records and keeps every record traceable. The shape
of the whole thing, and how the module meets the vendored SQLFlow, is in [architecture.md](architecture.md); the
platform underneath is documented in the vendored tree
([../../sqlflow/docs/architecture.md](../../sqlflow/docs/architecture.md)). These pages describe the flow kinds
that run on it, `flowType: delivery`, `flowType: retrieval` and `flowType: cache`, and the ledger behind them.

Data arrives through SQLFlow's own flows: a pre-ingestion flow lands the source files, an ingestion flow loads the
keyed ingestion tables, and the OSDU flow reads those tables. There is no drop manifest, no drop reader and no
replica.

| Page | What it covers |
| --- | --- |
| [design.md](design.md) | The design: the render inputs and the render context, identity, change detection and the cache, the ledger, the delivery protocols, the document model, the preflight gate, streaming, the retrieval kind (section 15), the streaming intake, work batches, returned values and fan-out (section 16). Section numbers are referenced from the code. |
| [documents.md](documents.md) | The delivery flow, retrieval flow, cache flow and mapping documents key by key. |
| [mapping-templates.md](mapping-templates.md) | Templates and mappings: the template an OSDU schema becomes and where it is saved, the mapping format entry by entry (sources, `findBy`, modifiers, `appliesWhen`, `required`, fixtures), the checks, and the mapping builder. |
| [submitting-records.md](submitting-records.md) | The other way in, for a source system: sending records' metadata in the submission itself. What a record looks like, the request and its answers, idempotency, the preview, and what each mistake leads to. |
| [ledger.md](ledger.md) | The ledger tables, the cache versions, the record lifecycle, leasing, the indexes behind every listing, retention. |
| [protocols.md](protocols.md) | The named delivery protocols (`osduRecord`, `osduWellLog`, `osduFile`, `osduManifest`), their steps and returned values, and how to add one. |
| [operations.md](operations.md) | Running it: the API and the GUI surfaces, the CLI verbs, first deployment, the runbook. |
| [osdu-testing.md](osdu-testing.md) | The state of testing against OSDU: how it is tested, what was proven live per protocol and feature, the defects the live runs found and their fixes, and what is still missing. |
| [architecture.md](architecture.md) | The module on SQLFlow: the three-flow chain, the extension points it registers through, and the `osdu` schema. |
| [environment-variables.md](environment-variables.md) | Every variable and secret reference, and which tier reads it. |
| [reference/](reference/README.md) | The OSDU-specific reference pages: the CLI verbs, the control plane, authentication, deployment and notifications. |
| [walkthrough/](walkthrough/1-welllog-schema.md) | A worked example: the WellLog schema, and how it is populated. |
| [decisions/](decisions/README.md) | The decision records. |

> Some pages still describe the previous implementation's drop path in places. The architecture is the one above;
> stage 4 of [../../docs/plan.md](../../docs/plan.md) moves the engine's input to the ingestion tables and brings
> the remaining prose with it.

## Where the code lives

| Path | Responsibility |
| --- | --- |
| `osdu/src/SqlFlow.Delivery` | The whole domain: model, identity, canonical JSON and hashing, the document loader and the three flow kinds, templates and the mapping builder, the cache store, rendering, planning, the preflight gate, the HTTP runtime, storage (work batch files, writers), the ledger, the engine (intake, worker, verifier, the four protocols, fan-out, the retrieval runner, the cache refresh and its impact analysis), the run executors, the compute operations, and the sync of mappings and cache definitions. |
| `osdu/src/SqlFlow.Delivery.Data` | `OsduDbContext`: the `osdu` schema model, its migrations and its schema version. |
| `osdu/src/SqlFlow.Delivery.ControlPlane` | The delivery and template API under `/api/v1/delivery`, and the cache rollout and data definitions background services. |
| `osdu/src/SqlFlow.Delivery.Cli` | `sqlflow check`, `sqlflow cache` and `sqlflow template`. |
| `osdu/hosts` | The control plane, node and CLI hosts that compose SQLFlow with the module and its branding. |
| `osdu/gui` | The delivery overview, the flow tabs (stats, records, submissions; retrievals for a retrieval flow), the record page, the submission page with its batches, the audit trail, mappings, the OSDU cache page, templates and the mapping builder. |
| `osdu/samples/recall-welllog` | A complete sample estate: the pre, ingestion and OSDU flows, the mappings, the cache flow its mappings read (`caches/`), the bundled schemas its templates are saved from (`templates/`), and sample cache records to import for work without OSDU (`references/`). |
| `osdu/tests` | The module's suites: the domain suites, and the delivery submission and template API suites. |
| `osdu/deploy` | The container images, and the compose, Kubernetes and Azure Container Apps assets. |

## A flow's repository

An OSDU flow lives in a git repository the control plane syncs, next to what it renders with and the flows that
feed it:

```text
repo/
  flows/recall-welllog-pre.yaml      the pre-ingestion flow: lands the source files
  flows/recall-welllog-ing.yaml      the ingestion flow: loads the keyed ingestion table
  flows/recall-welllog.yaml          flowType: delivery; reads those tables and delivers
  mappings/WellLog@1.4.0.yaml        documentType: mapping, pinned by name and version
  caches/osdu-reference-cache.yaml   flowType: cache: the OSDU types it caches for its partition
```

The delivery flow finds `mappings/` by walking up from its own file, or names it under `render.mappings`. A cache
flow can sit anywhere in the tree; the sample keeps it under `caches/`. The sync projects every flow as a
pipeline, and the mappings and the types each cache flow declares as read models the GUI lists. Lineage orders the
three flows in waves, so the OSDU flow runs after the ingestion table it reads has been loaded.

Neither templates nor cache contents are in the repository: the template a mapping pins is saved in the catalog,
captured from OSDU or imported from a bundled schema file ([mapping-templates.md](mapping-templates.md)), and every
version of a partition's cache is written into the catalog by the runs of the cache flows that fill it
([documents.md](documents.md#cache-flow)). OSDU Delivery only reads the repository.
