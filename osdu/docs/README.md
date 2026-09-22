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
| [documents.md](documents.md) | The delivery flow (a single OSDU type, or a source with interfaces), retrieval flow, cache flow and mapping documents key by key. |
| [mapping-templates.md](mapping-templates.md) | Templates and mappings: the template an OSDU schema becomes and where it is saved, the mapping format entry by entry (sources, `findBy`, modifiers, `appliesWhen`, `required`, fixtures), the checks, and the mapping builder. |
| [ledger.md](ledger.md) | The ledger tables, the cache versions, the record lifecycle, leasing, the indexes behind every listing, retention. |
| [protocols.md](protocols.md) | The named delivery protocols (`storage`, `ddms`, `file`, `dataset`, `manifest`, `fileAndDdms`, `manifestAndDdms`, `workflow`), their steps and returned values, payload parts, and how to add one. |
| [operations.md](operations.md) | Running it: the API and the GUI surfaces, running a source, the CLI verbs, first deployment, the runbook. |
| [osdu-testing.md](osdu-testing.md) | The state of testing against OSDU: how it is tested, what was proven live per protocol and feature, the defects the live runs found and their fixes, and what is still missing. |
| [architecture.md](architecture.md) | The module on SQLFlow: the three-flow chain, the extension points it registers through, and the `osdu` schema. |
| [environment-variables.md](environment-variables.md) | Every variable and secret reference, and which tier reads it. |
| [reference/](reference/README.md) | The OSDU-specific reference pages: the CLI verbs, the control plane, authentication, deployment and notifications. |
| [walkthrough/](walkthrough/1-welllog-schema.md) | A worked example: the WellLog schema, and how it is populated. |
| [decisions/](decisions/README.md) | The decision records. |

## Where the code lives

| Path | Responsibility |
| --- | --- |
| `osdu/src/SqlFlow.Delivery` | The whole domain: model, identity, canonical JSON and hashing, the document loader and the three flow kinds, templates and the mapping builder, the cache store, rendering, planning, the preflight gate, the HTTP runtime, storage (work batch files, writers), the ledger, the engine (intake, worker, verifier, the protocols and the workflow contracts, fan-out, the retrieval runner, the cache refresh and its impact analysis), the run executors, the compute operations, and the sync of mappings and cache definitions. |
| `osdu/src/SqlFlow.Delivery.Data` | `OsduDbContext`: the `osdu` schema model, its migrations and its schema version. |
| `osdu/src/SqlFlow.Delivery.ControlPlane` | The delivery and template API under `/api/v1/delivery`, and the cache rollout and data definitions background services. |
| `osdu/src/SqlFlow.Delivery.Cli` | `sqlflow check`, `sqlflow cache` and `sqlflow template`. |
| `osdu/hosts` | The control plane, node and CLI hosts that compose SQLFlow with the module and its branding. |
| `osdu/gui` | The delivery overview, the flow tabs (stats, records, submissions; retrievals for a retrieval flow), the record page, the submission page with its batches, the audit trail, mappings, the OSDU cache page, templates and the mapping builder. |
| `osdu/samples/wells` | A complete sample estate, laid out the way a repository is: one folder for the wells source, holding the pre, ingestion and OSDU flows (`flows/`), the mappings (`mappings/`), the document that defines the cache its mappings read (`cache/wells-osdu-00-reference-cache.yaml`), and the drop-off folder the pre flows read (`data/`). |
| `osdu/samples/cache-records` | Sample records for that cache, one file per cached type, to import for work without an OSDU platform. Beside the source folders, never inside one: a cache lives in the module's database, captured there by a run, so a repository holds the flow document and nothing else about it. |
| `osdu/samples/templates` | The bundled OSDU schemas the suites and the e2e seed save as templates. They sit beside the source folders, not inside one: a template is a catalog object captured from OSDU's schema service through the Templates page, never a file a repository sync reads. |
| `osdu/tests` | The module's suites: the domain suites, and the delivery submission and template API suites. |
| `osdu/deploy` | The container images, and the compose, Kubernetes and Azure Container Apps assets. |

## A flow's repository

An OSDU flow lives in a git repository the control plane syncs, next to what it renders with and the flows that
feed it. The repository is laid out one folder per source, and that folder is what the catalog and the GUI call a
project:

```text
repo/
  wells/                                        one source, and nothing the product writes
    flows/wells-welllog-01-header-pre.yaml      lands the source files
    flows/wells-welllog-02-header-ing.yaml      loads the keyed ingestion table
    flows/wells-welllog-03-header-delivery.yaml flowType: delivery; reads those tables and delivers
    mappings/WellLog@1.4.0.yaml                 documentType: mapping, pinned by name and version
    cache/wells-osdu-00-reference-cache.yaml    flowType: cache: the OSDU types it caches
    data/welllog/                               the drop-off point the pre flow reads
```

A flow's name is `<source>-<type>-<counter>-<area>-<kind>`, so sorting the folder is reading the chain in the
order it runs: every `01` lands files, every `02` keys them into the ingestion tables, and `03` delivers what
those tables hold. The area separates a record from the collections hanging off it, so a well log's header and
its curves sit next to each other at each step.

```text
    wells-welllog-01-curves-pre     wells-wellbore-01-aliases-pre
    wells-welllog-01-header-pre     wells-wellbore-01-header-pre
    wells-welllog-02-curves-ing     wells-wellbore-02-aliases-ing
    wells-welllog-02-header-ing     wells-wellbore-02-header-ing
    wells-welllog-03-header-delivery wells-wellbore-03-header-delivery
```

The repository is the developers', and the product only reads it. Templates and cache versions are not in it: both
live in the module's database, saved through the Templates page or captured by a run of a cache flow. What a
repository holds about either is the document that declares it.

The delivery flow finds `mappings/` by walking up from its own file, or names it under `render.mappings`. A cache
flow can sit anywhere in the tree; the sample keeps it under `cache/`. The sync projects every flow as a
pipeline, and the mappings and the types each cache flow declares as read models the GUI lists. Lineage orders the
flows in waves, so the OSDU flow runs after the ingestion table it reads has been loaded, and shows the OSDU type each
delivery flow writes and the cache types it reads ([documents.md](documents.md#lineage)).

Neither templates nor cache contents are in the repository: the template a mapping pins is saved in the catalog,
captured from OSDU or imported from a bundled schema file ([mapping-templates.md](mapping-templates.md)), and every
version of a partition's cache is written into the catalog by the runs of the cache flows that fill it
([documents.md](documents.md#cache-flow)). OSDU Delivery only reads the repository.
