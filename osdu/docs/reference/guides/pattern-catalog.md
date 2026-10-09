---
id: delivery-guide-pattern-catalog
title: "Pattern catalog: which delivery problem maps to which OSDU Delivery shape"
type: guide
summary: "Problem-first index: where a lookup belongs, one kind or interfaces, which route, retrieval or explorer, tests, orphans, and the page for each."
keywords:
  - patterns
  - which shape
  - how do i
  - where does a lookup belong
  - cache or dictionary or dimension
  - search per record
  - interfaces
  - which route
  - retrieval vs explorer
  - assertion tests
  - orphans
  - redeliver
  - partitions
related:
  - wiki-pattern-catalog
  - delivery-flow-overview
  - delivery-flow-mapping-lookups
  - delivery-flow-cache
  - delivery-flow-interfaces
  - delivery-flow-routes
  - delivery-guide-getting-started
  - delivery-concept-overview
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/CacheDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/InventoryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/AssertionFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Protocols/DeliveryProtocol.cs
  - osdu/src/SqlFlow.Delivery/Rendering/ReplaceTables.cs
  - osdu/src/SqlFlow.Delivery/Model/DeliveryDestination.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/ConfiguredRunDispatcher.cs
  - osdu/docs/census/keys.delivery.json
  - osdu/docs/census/keys.cache.json
  - osdu/docs/census/keys.mapping.json
  - osdu/docs/census/keys.assertion.json
  - osdu/docs/census/keys.inventory.json
  - osdu/gui/src/module.tsx
---

# Pattern catalog: which delivery problem maps to which OSDU Delivery shape

Start here when the question is "how do I deliver X" or "where does Y belong" rather than "what does key Z do". Each row
names a recurring delivery problem, the shape OSDU Delivery solves it with, and the page that documents that shape. Key
semantics live on the reference pages; this page only points. Problems that are SQLFlow's (landing files, incremental
ingestion, upserts, scheduling, secrets) are answered by
[SQLFlow's pattern catalog](../../../../sqlflow/docs/wiki/maps/pattern-catalog.md), and the first section here only
shows where they meet the OSDU flows.

The examples use one estate throughout: a well database exported as files (`welldb`), landed into `pre` tables, keyed into
`OsduData.silver.*` by `ing` flows, and delivered as wellbores and well logs to the partitions `dev` and `test`.

## Getting the source in

| Problem | Shape | Page |
| --- | --- | --- |
| Source files must reach OSDU | A file flow lands them, an `ing` flow keys them, a delivery flow reads the keyed table; three flows, ordered by lineage | [getting started](getting-started.md), [OSDU documents](../flow/overview.md) |
| The source is another database, not files | An `ing` flow straight from it into the keyed table; the delivery flow does not care how the table was filled | [SQLFlow: foreign databases](../../../../sqlflow/docs/reference/guides/foreign-db-ingestion.md) |
| A delivery must only see rows that changed | Keep the `ing` flow's `systemColumns.updatedDate` on: `UpdatedDate_DW` is the delivery's incremental window | [SQLFlow: ing flow](../../../../sqlflow/docs/reference/flow/ing.md), [delivery flow](../flow/delivery.md), [change detection](../concepts/change-detection.md) |
| Every delivered record must point back at its file and row | Nothing to add: the file flow's `FileName_DW` and `RowNumber_DW` ride through to the ledger (`source.systemColumns` renames them; a renamed provenance column the table lacks is refused) | [delivery flow](../flow/delivery.md), [the ledger](../concepts/ledger.md) |
| A record has child rows (a log's curves) | `source.datasets` with a `join` on the key, repeated in the mapping with `$forEach` | [delivery flow](../flow/delivery.md), [mapping values](../flow/mapping-values.md) |
| A large table should page fast and spread over nodes | `source.record.primaryKey` (the `ing` flow's `identityColumn`), then `reliability.fanOut` | [delivery flow](../flow/delivery.md), [submissions](../concepts/submissions.md) |
| One flow should serve several slices of one table (one log source each) | `parameters` plus `source.record.scope`, each run naming its value | [delivery flow](../flow/delivery.md) |

## Where a lookup belongs

The most common design question. A mapping can look a value up in six places; pick by where the truth lives and how big
it is.

| The lookup is | Shape | Page |
| --- | --- | --- |
| OSDU reference data the partition holds (units, curve types, sampling domains) | A cache flow type with `kind: "osdu:wks:reference-data--UnitOfMeasure:*"`, read in the mapping with `ref` or `$cache` and `$findBy` | [reference data cache](reference-data-cache.md), [cache flow](../flow/cache.md), [lookups](../flow/mapping-lookups.md) |
| Your own table, maintained outside OSDU (how the source spells a unit, as the partition's code) | Files, a pre flow, an `ing` flow to `OsduData.silver.UnitAlias`, a cache flow type with `table:`, then `replace: $cache.UnitAlias` in the mapping | [lookup table cache](lookup-table-cache.md), [modifiers](../flow/mapping-modifiers.md) |
| A handful of fixed pairs, reviewed in git | A dictionary document (`dictionaries/sampling-domain.yaml`), held by a cache flow type with `dictionary:` | [dictionary](../flow/dictionary.md) |
| Only one mapping needs it, and it is a few values | An inline table: `replace: { DEPTH: Depth }` in the mapping itself, no cache at all | [modifiers](../flow/mapping-modifiers.md) |
| The distinct values OSDU records actually hold (cleaned names, the keys behind each) | A dimension flow builds it; a cache flow type with `dimension:` and `dimensionFlow:` holds it for mappings | [dimension](../flow/dimension.md), [cache flow](../flow/cache.md), [dimensions guide](dimensions.md) |
| Master data far too large to cache (every wellbore of a partition) | A `searches` entry and `$search` with `$findBy`: asked of the platform as each record renders, never captured | [lookups](../flow/mapping-lookups.md) |

| Problem | Shape | Page |
| --- | --- | --- |
| A lookup table holds a key and one value | `replace: $cache.<Type>` matches the key and takes the one field beside it; with more fields, name one with `field:` | [modifiers](../flow/mapping-modifiers.md) |
| A value may be spelled several ways, or be missing from the table | `$coalesce`: the first alternative that gives a value (the table, then the partition's records, then the value as it stands) | [mapping values](../flow/mapping-values.md) |
| A reference should go out even before the partition holds its record | `$unverified: true` on the node building it: written, and recorded as unverified | [mapping values](../flow/mapping-values.md), [preflight](../concepts/preflight.md) |
| Several properties need the same looked-up record (a log's wellbore and its access groups) | A `lookups` entry, matched once per row and read with `$lookup` | [lookups](../flow/mapping-lookups.md) |
| A changed cached value must be reviewed before it reaches records | `onChange: approve` on the cache flow or the type: affected records wait for approval on the Cache page | [partition cache](../concepts/partition-cache.md), [cache flow](../flow/cache.md) |
| A delivery must render against a fixed cache, not the newest | `render.cacheVersion: <version label>` (a flow of one partition only) | [delivery flow](../flow/delivery.md) |

## Shaping the record

| Problem | Shape | Page |
| --- | --- | --- |
| Start a mapping from an OSDU schema | Save the template (`sqlflow template capture` or the Templates page), then the mapping builder | [templates](../concepts/templates.md), [writing a mapping](writing-a-mapping.md) |
| A property only applies to some rows | `$when: <condition>` on the node | [mapping values](../flow/mapping-values.md), [expressions](../flow/mapping-expressions.md) |
| An empty value should leave the property out rather than hold the record | `$required: false` | [mapping values](../flow/mapping-values.md) |
| A value needs computing (concatenation, arithmetic, a choice) | `$expr` | [expressions](../flow/mapping-expressions.md) |
| A reference id is built from a value | The `ref` modifier (`{$param.dataPartition}:<group>--<Entity>:{$value}:`, the entity type the template's property points to), or `id` with a template of your own | [modifiers](../flow/mapping-modifiers.md) |
| The record's OSDU id must be the source's own code, not a hash | `dataset.idFrom: key` | [mapping](../flow/mapping.md) |
| Find a record later by a wellbore name or UWI | `dataset.identity: [wellbore_name, wellbore_uwi]`: the ledger indexes each | [mapping](../flow/mapping.md), [the ledger](../concepts/ledger.md) |
| Know which rows will be refused before running | `sqlflow values` on the flow: the scope's rows (the first 10,000, or all with `--max-rows 0`) held to the template's rules | [values](../cli/values.md) |
| See one record exactly as it would be sent | `sqlflow preview`, or the flow's Preview tab | [preview](../cli/preview.md) |
| Hold a record that breaks its schema instead of sending it | `target.validation.mode: enforce` | [preflight](../concepts/preflight.md) |
| Hold a record that names an id OSDU does not hold | `target.verifyReferences: storage` | [delivery flow](../flow/delivery.md), [preflight](../concepts/preflight.md) |

## One kind per flow, or one source with interfaces

| Problem | Shape | Page |
| --- | --- | --- |
| A source delivers one kind | The single form: `source.record` and `render.mapping` | [delivery flow](../flow/delivery.md) |
| A source delivers several kinds that refer to each other (wellbores, then their well logs) | One document with `interfaces:`, one per kind, each with its own record table, mapping and ledger identity; order from `after:` and the template's relationships | [interfaces](../flow/interfaces.md), [multi-kind source](multi-kind-source.md) |
| A record refers to one that is not delivered yet | Nothing to add: it waits (status `Waiting`) and goes back to pending when the other lands | [interfaces](../flow/interfaces.md), [record lifecycle](../concepts/record-lifecycle.md) |
| Separate single-kind flows should become one source without delivering again | `interfaces.<name>.ledger` names the old flow whose ledger the interface keeps | [interfaces](../flow/interfaces.md) |
| A run should stop when failures pile up, not hold record after record | `failWhen` | [delivery flow](../flow/delivery.md) |

## Which route

The route is `target.protocol` (or, in a source with interfaces, follows from what each interface declares).

| What a record is | Route | Page |
| --- | --- | --- |
| A record with no files (master data, most reference data) | `storage`: the storage service, batched upserts | [routes](../flow/routes.md) |
| A record with files it lists as datasets | `file`: each file uploaded to a signed location, registered as a dataset, then the record | [routes](../flow/routes.md) |
| A dataset record and its files through the Dataset service | `dataset` | [routes](../flow/routes.md) |
| Records the platform's ingestion workflow must load | `manifest`: one manifest per batch, the workflow run polled, the records read back | [routes](../flow/routes.md) |
| A record whose DDMS keeps bulk data (a well log and its curves) | `ddms`: the record through the DDMS serving its entity type, then its bulk data, in a session when large | [DDMS](../flow/ddms.md), [bulk data guide](bulk-data.md) |
| Files and DDMS bulk data on one record | `fileAndDdms`; with a manifest, `manifestAndDdms` | [routes](../flow/routes.md) |
| A named workflow does the loading | `workflow`, declared stage by stage | [routes](../flow/routes.md) |
| Rows of the Production DDMS core service, not OSDU records | `dspdm` | [DDMS shapes and services](../flow/ddms-services.md#production-ddms-core-service-dspdm) |
| Energistics objects into the Reservoir DDMS | `etp` | [DDMS shapes and services](../flow/ddms-services.md#reservoir-ddms-etp) |

## Partitions and environments

| Problem | Shape | Page |
| --- | --- | --- |
| One flow delivers to `dev` and to `test` | `partitions: [dev, test]`; a run names one (`--set partition=test`, or the GUI's choice), and each partition keeps its own ledger and cache | [partitions](../concepts/partitions.md) |
| Every partition the estate registers, without listing them | No `partitions` and no `data-partition-id` header; register partitions with `sqlflow partition add` | [partitions](../concepts/partitions.md), [partition](../cli/partition.md) |
| A flow that delivered to one partition starts naming partitions | `partitions: [{ name: dev, keepLedger: true }, test]`: `dev` keeps the old ledger, so nothing is sent again | [partitions](../concepts/partitions.md) |
| The legal tag or groups differ per partition | Leave them to the kind's defaults (`${env:OSDU_LEGAL_TAG}`, `${env:OSDU_ACL_OWNER}`, `${env:OSDU_ACL_VIEWER}`) and set the values per partition in the central configuration | [config](../cli/config.md), [environment variables](../concepts/environment-variables.md) |
| Values must come from one place, not every node's environment | `sqlflow config set <NAME> --value ... [--partition ...]`: the control plane supplies them to every delivery, retrieval, cache, assertion, dimension and inventory run it queues | [config](../cli/config.md), [control plane](../concepts/control-plane.md) |

## Change, redelivery and repair

| Problem | Shape | Page |
| --- | --- | --- |
| Only send what renders differently | The default: `change.detect: renderedHash`, `change.onUnchanged: skip` | [change detection](../concepts/change-detection.md) |
| An older source version must never overwrite a newer one | `source.lastModified` names the row's business version | [delivery flow](../flow/delivery.md) |
| A mapping edit should reach every delivered record | Nothing to add: the next run sees that its mapping, template or parameters changed, reads every row of its scope once, and sends what renders differently; `replan` forces such a read | [change detection](../concepts/change-detection.md) |
| A record is held or failed; the cause is fixed | `sqlflow records issues`, then `records release` (or the Issues tab) | [records](../cli/records.md), [operations runbook](operations-runbook.md) |
| A record must be sent again although nothing changed | A redelivery from the record's page or the API | [removal and reversal](../concepts/removal-and-reversal.md), [operations runbook](operations-runbook.md) |
| A bad run must be undone | `sqlflow records reverse --run <id>` (or `--operation reverse`): what it created is removed, what it updated gets its earlier version back | [removal and reversal](../concepts/removal-and-reversal.md) |
| A delivery stopped halfway and left datasets behind | `--operation undo`; deliver and drain runs also end with the same sweep | [removal and reversal](../concepts/removal-and-reversal.md) |
| The ledger and the ingestion tables disagree | `--operation sync`: records what the ledger lacks, flags rows that changed unseen, reports rows that are gone; sends nothing | [removal and reversal](../concepts/removal-and-reversal.md) |
| What OSDU holds may have drifted from what was sent | `--operation verify`; `verify.reconcile: true` has the next deliver run send drifted or missing records again | [running an OSDU flow](../cli/run.md), [delivery flow](../flow/delivery.md#verify) |
| A run's legal tag is no longer valid | The deliver run checks it first and stops; fix the tag or the configuration | [protocols](../concepts/protocols.md) |
| The target answers a status that retrying will not fix | `reliability.skipStatusCodes`: hold instead of retrying (400, 403, 404, 405, 409, 413, 415 and 422 already hold) | [delivery flow](../flow/delivery.md), [record lifecycle](../concepts/record-lifecycle.md) |

## After delivery: reading OSDU

| Problem | Shape | Page |
| --- | --- | --- |
| Look at what OSDU holds for a type or one record, now | The explorer (GUI, any signed-in user), read live from search and storage | [explorer](../concepts/explorer.md) |
| OSDU records must land in tables for reporting | A retrieval flow writes JSON Lines files; a SQLFlow file flow loads them, ordered after it by lineage | [retrieval](../flow/retrieval.md), [retrieving records](retrieving-records.md) |
| Prove what landed: counts, values, references, schema, legal tags, bulk data | An assertion flow, with a report per run (JUnit for CI) | [assertion](../flow/assertion.md), [data quality tests](data-quality-tests.md) |
| Prove that everything the ledger says it delivered is in OSDU | An assertion with the `delivered` subject | [assertion](../flow/assertion.md) |
| Find ids in OSDU no ledger accounts for, and remove them | An inventory flow; `removal:` in the document allows removing the findings an operator picks | [inventory](../flow/inventory.md), [finding orphans](finding-orphans.md) |
| Let people find records by the source's own values | Search terms: the source columns the mappings read, searched in the explorer | [search terms](../concepts/search-terms.md) |
| Let people pick from the values records hold | A dimension flow, its values and search filters | [dimensions guide](dimensions.md) |

## Operating it

| Problem | Shape | Page |
| --- | --- | --- |
| Run the chain on a schedule | Join the pre, ing and OSDU flows to one schedule; a fire runs them in lineage order, and the schedule's `values` supply a flow's parameters | [SQLFlow: schedule](../../../../sqlflow/docs/reference/flow/schedule.md), [delivery flow](../flow/delivery.md) |
| Be told when a delivery fails | SQLFlow's notifications, for every OSDU kind; `lifecycle: development` while building | [notifications](notifications.md) |
| Nodes inside a customer network | A worker with `SQLFLOW_OSDU_DB` and the flows' references in its own environment | [worker](../cli/worker.md), [deployment](deployment.md) |
| Ask an assistant about the estate without exposing data | `osdu-delivery-mcp`: metadata only | [MCP server](mcp.md) |
| See why a flow runs after another | `sqlflow lineage <folder> --explain <flow>`, or the lineage graph | [lineage](../concepts/lineage.md) |

## Traps worth reading before authoring

| Trap | Page |
| --- | --- |
| OSDU documents refuse unknown keys (SQLFlow's ignore them): a typo fails validation, which is the point | [OSDU documents](../flow/overview.md) |
| `sqlflow validate` on a flow reads the mapping it pins and the dictionaries it names, but not the template or the cache; `sqlflow check` holds the mapping against the template and the partition's cache | [check](../cli/check.md) |
| A mapping's `template.version` is the saved template's hash, and another release of the same kind can give another one | [templates](../concepts/templates.md) |
| The flow's `source.record.key` and the mapping's `dataset.key` must name the same columns in the same order | [delivery flow](../flow/delivery.md) |
| Changing `dataset.system` or `dataset.key` derives new delivery keys: with the default `idFrom`, every row becomes a new record with a new OSDU id, beside the ones already delivered. A change that gives an existing record another id is held instead of sent | [mapping](../flow/mapping.md) |
| A mapping that reads the cache needs a cache version of the partition before the first run | [preflight](../concepts/preflight.md), [partition cache](../concepts/partition-cache.md) |
| Lineage shows fewer cache reads than a mapping makes until its template is saved | [lineage](../concepts/lineage.md) |
| The ingestion database must allow snapshot isolation, or set `source.incremental.isolation: readCommitted` | [delivery flow](../flow/delivery.md) |
