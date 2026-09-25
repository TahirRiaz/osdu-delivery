# What the suites cover

Every route type and DDMS shape this module delivers through, the suite that proves it, and what that proof rests on.
It is the companion of [osdu-testing.md](osdu-testing.md), which records what has been proven against a live OSDU: this
page is what the automated suites establish, and they create nothing in any OSDU.

Two kinds of proof appear here, and they are not the same thing:

- **Against a fake built from the service's own contract or source.** Each fake answers as the service does, and every
  request the route sends is checked against the pinned contract in [../specs](../specs) (the OpenAPI document, or, for
  the Reservoir DDMS, the Avro protocol). A route that would send something the contract does not describe fails the
  suite.
- **Against the engine itself.** The ledger, the planner, the worker, leases, fan-out and the SQL Server behaviour run
  against a real SQL Server, not a stub.

Nothing here proves that a deployment's own OSDU agrees with its contract. That is what the live waves are for
(`docs/go-live-map.md`, LIVE-1 to LIVE-5). Wave one ran on 2026-09-17 against ADME 0.29: the storage, file, manifest
and ddms routes each delivered live and were read back ([osdu-testing.md](osdu-testing.md) section 0). The rows below
say what the suites prove; the live column says which of them a live platform has also answered for.

| Route | Proven live | Where |
| --- | --- | --- |
| `storage` | Yes, on 2026-09-17 | Two wellbores, a revision, a verify, a reconcile |
| `file` | Yes, on 2026-09-17 | Two documents, their datasets, a metadata-only change and a payload change |
| `manifest` | Yes, on 2026-09-17 | One document through `Osdu_ingest` |
| `ddms` (`wellboreDdmsV3`, well logs) | Yes, on 2026-09-17 | Three logs with bulk data, a bulk-only change, a two-chunk session, three refusals |
| `dataset`, `workflow`, `dspdm`, `etp`, the other DDMS shapes | No | No sample estate, or the deployment serves no such service |

## Route types

| Route | What it delivers | Proven by | Against |
| --- | --- | --- | --- |
| `storage` (`storage`) | Any kind through Storage v2, batched, upsert by id | `CoreTests`, `EndToEndTests`, `RemovalTests` | The fake platform, every request checked against the storage contract |
| `file` (`file`) | Files through the File service, registered, then the record naming them | `FileProtocolTests`, `ProtocolContractTests` | The fake file, dataset and storage services |
| `dataset` (`dataset`) | Files stored and registered through the Dataset service (Azure, MinIO, S3, Google Cloud Storage) | `DatasetRouteTests`, `ObjectStoreProtocolTests` | The fake dataset service and the four object stores, S3 signatures checked with the AWS SDK |
| `manifest` (`manifest`) | Files registered, then records through `Osdu_ingest`, inline or by reference | `ManifestByReferenceTests`, `WorkflowRouteTests` | The fake workflow, search and storage services |
| `ddms` (`ddms`) | The record through the collection of the DDMS serving its entity type, then its bulk data | `DdmsRouteTests`, `DdmsRoutingTests`, `WellboreDdmsRulesTests` | The fake Wellbore DDMS v3, every request checked against its OpenAPI |
| `fileAndDdms`, `manifestAndDdms` | The composed routes: files or a manifest first, then bulk data | `ComposedRouteTests` | The fakes of both halves |
| `workflow` (`workflow`) | The record written, its inputs registered, catalogued workflow runs, outputs read back | `WorkflowRouteTests`, `WorkflowTemplateTests`, `WorkflowDocumentsTests` | The fake workflow service and Airflow's REST API |
| `dspdm` (`dspdm`) | Rows of Production DDMS business objects, found again by a unique key | `DspdmRouteTests`, `DspdmDocumentsTests` | A fake of DSPDM built from its source, every request checked against its contract |
| `etp` (`etp`) | Energistics objects in dataspaces of the Reservoir DDMS over ETP 1.2 | `EtpRouteTests`, `EtpSessionTests`, `EtpSchemaTests`, `EtpBinaryTests`, `EtpObjectTests`, `EtpDocumentsTests` | A fake ETP server over a real WebSocket, every message round-tripped against the pinned Avro protocol |

## DDMS shapes of the `ddms` route

| Shape | Service | Proven by |
| --- | --- | --- |
| `wellboreDdmsV3` | Wellbore DDMS (nine collections, bulk for four of them) | `DdmsRouteTests`, `DdmsCatalogTests` |
| `wellDeliveryV1` | Well Delivery DDMS | `WellDeliveryRouteTests` |
| `rafsV2` | Rock and Fluid Samples DDMS | `RafsRouteTests` |
| `productionTimeSeriesV1` | The production historian | `ProductionTimeSeriesRouteTests` |
| `seismicStoreV3` | Seismic Store and its three object stores | `SeismicStoreRouteTests` |
| `reservoirManagement` | Reservoir Management DDMS | `ReservoirManagementRouteTests` |
| External Data Services | The records that configure EDS, on the routes that write them | `ExternalDataServicesTests` |

## The engine, against a real SQL Server

| Area | Proven by |
| --- | --- |
| The ledger: staging, claims, leases, attempts, work batches, waits | `SqlServerLedgerTests`, `LedgerTests`, `LeaseJournalTests`, `RecordWaitTests` |
| Migrations and the module's schema version | `SqlServerLedgerMigrationTests`, `MigrationScriptTests`, `SchemaVersionPipelinesTests` |
| Reading the ingestion tables, watermarks, fingerprints, key slices | `SqlServerIngestionSourceTests`, `IngestionFingerprintTests`, `KeySlicesTests` |
| The chain end to end, fanned out over several nodes | `SqlServerChainTests`, `ScaleEngineTests` |
| Isolation and lock escalation under six thousand records | `SqlServerLedgerTests` |

## Sources, interfaces and order

| Area | Proven by |
| --- | --- |
| A source's interfaces, their routes, ledgers and document rules | `InterfaceDocumentsTests`, `SourceRunRulesTests` |
| The order a run takes them in, and the cycles it refuses | `InterfaceOrderTests` |
| The read model the GUI and API list them from | `InterfaceCatalogSyncTests`, `DeliveryInterfacesApiTests` (control plane) |
| Records that wait for records they refer to | `RecordWaitTests`, `ReferenceCheckTests`, `ReferenceReaderTests` |
| Lineage: every interface's reads and writes | `LineageTests` |

## Rendering, templates and the cache

| Area | Proven by |
| --- | --- |
| Mappings, modifiers, repeaters, references and their refusals | `MappingShapeTests`, `MappingBuilderTests`, `ReferenceFoldTests` |
| Templates: capture, import, comparison, the variables a mapping may fill | `TemplateTests`, `TemplateComparisonTests`, `OsduDataDefinitionsTests` |
| The partition cache, its versions, changes and rollout | `ReferenceCacheTests`, `CacheChangeTests`, `CacheVersionComparisonTests`, `CacheCatalogSyncTests` |

## The guards

| Area | Proven by |
| --- | --- |
| The addresses a node may reach, every redirect hop, and names that resolve inward | `NetworkGuardTests` |
| What each route sends on the wire, against the pinned contracts | `ApiContractTests`, `ProtocolContractTests`, `OsduWireTests` |
| The pinned contracts themselves, and their provenance | `SpecProvenanceTests` |
| Metrics: settled tries and every HTTP attempt | `DeliveryMetricsTests` |
| The module's own architecture rules | `ArchitectureTests` |

## The sample estate

`osdu/samples/recall` is a runnable estate rather than a fixture: five real Recall well logs with their curves, and the
unit maps and curve dictionary petrodb-api translates Recall's values through. Its pre and ingestion flows load the files
into SQL Server, its lookups flow captures those tables into the partition's cache, and its delivery flow plans and
renders against the WellLog 1.4.0 template through the ddms route, with each log's curves as bulk data.

The suites put fixture documents (`osdu/tests/SqlFlow.Delivery.Tests/Fixtures/documents`) beside it for the kinds and
routes the sample does not deliver. Together they cover four kinds and three route types:

| Interface or flow | Kind | Route | From |
| --- | --- | --- | --- |
| `recall-welllog-03-header-delivery` | `work-product-component--WellLog` | ddms (well logs, with bulk data) | the sample |
| `wells-wellbore-03-header-delivery`, `wells-source-03-interfaces-delivery/wellbores` | `master-data--Wellbore` | storage | the fixtures |
| `wells-source-03-interfaces-delivery/welllogs` | `work-product-component--WellLog` | storage | the fixtures, rendered with the sample's mapping |
| `wells-source-03-interfaces-delivery/trajectories` | `work-product-component--WellboreTrajectory` | ddms (trajectories, with bulk data) | the fixtures |
| `wells-source-03-interfaces-delivery/documents` | `work-product-component--Document` | file | the fixtures |

The GUI's end-to-end suite runs the two together through the product: the repository is synced, the templates saved,
the fixture reference records imported as the cache's first version, the Recall chain run and its lookup tables
refreshed, a plan executed and an intake staged, and the fixture source read one interface at a time through the GUI and
the CLI. The fixture flows are synced and read but never run: their source files are not part of the estate, so their
ledgers stay empty.

## What no suite proves

- That a deployment's OSDU behaves as its contract says. Only a live run proves that.
- The kinds no sample mapping renders. The route each would take is decided by the rules in
  [documents.md](documents.md#routes), which are covered, but no sample renders a reference data kind or a dataset
  collection end to end.
- Reading bulk data back out of a DDMS, and reading arrays back out of the Reservoir DDMS: both are written and
  verified, and only the ETP array metadata is read back.
- The deployed hosting: managed identity, Key Vault references and the images are exercised only by the image smoke
  starts in CI.
