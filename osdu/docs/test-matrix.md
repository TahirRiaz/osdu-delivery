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
(`docs/go-live-map.md`, LIVE-1 to LIVE-5).

## Route types

| Route | What it delivers | Proven by | Against |
| --- | --- | --- | --- |
| `storage` (`osduRecord`) | Any kind through Storage v2, batched, upsert by id | `CoreTests`, `EndToEndTests`, `RemovalTests` | The fake platform, every request checked against the storage contract |
| `file` (`osduFile`) | Files through the File service, registered, then the record naming them | `FileProtocolTests`, `ProtocolContractTests` | The fake file, dataset and storage services |
| `dataset` (`osduDataset`) | Files stored and registered through the Dataset service (Azure, MinIO, S3, Google Cloud Storage) | `DatasetRouteTests`, `ObjectStoreProtocolTests` | The fake dataset service and the four object stores, S3 signatures checked with the AWS SDK |
| `manifest` (`osduManifest`) | Files registered, then records through `Osdu_ingest`, inline or by reference | `ManifestByReferenceTests`, `WorkflowRouteTests` | The fake workflow, search and storage services |
| `ddms` (`osduWellLog`) | The record through the collection of the DDMS serving its entity type, then its bulk data | `DdmsRouteTests`, `DdmsRoutingTests`, `WellboreDdmsRulesTests` | The fake Wellbore DDMS v3, every request checked against its OpenAPI |
| `fileAndDdms`, `manifestAndDdms` | The composed routes: files or a manifest first, then bulk data | `ComposedRouteTests` | The fakes of both halves |
| `workflow` (`osduWorkflow`) | The record written, its inputs registered, catalogued workflow runs, outputs read back | `WorkflowRouteTests`, `WorkflowTemplateTests`, `WorkflowDocumentsTests` | The fake workflow service and Airflow's REST API |
| `dspdm` (`osduDspdm`) | Rows of Production DDMS business objects, found again by a unique key | `DspdmRouteTests`, `DspdmDocumentsTests` | A fake of DSPDM built from its source, every request checked against its contract |
| `etp` (`osduEtp`) | Energistics objects in dataspaces of the Reservoir DDMS over ETP 1.2 | `EtpRouteTests`, `EtpSessionTests`, `EtpSchemaTests`, `EtpBinaryTests`, `EtpObjectTests`, `EtpDocumentsTests` | A fake ETP server over a real WebSocket, every message round-tripped against the pinned Avro protocol |

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

`osdu/samples/recall-welllog` is a runnable estate rather than a fixture: the pre and ingestion flows load its files
into SQL Server, and the delivery flows plan and render against real templates and a real cache version. It covers four
kinds and three route types:

| Interface or flow | Kind | Route |
| --- | --- | --- |
| `recall-wellbore`, `recall-source/wellbores` | `master-data--Wellbore` | storage |
| `recall-welllog` | `work-product-component--WellLog` | ddms (well logs, with bulk data) |
| `recall-source/welllogs` | `work-product-component--WellLog` | storage |
| `recall-source/trajectories` | `work-product-component--WellboreTrajectory` | ddms (trajectories, with bulk data) |
| `recall-source/documents` | `work-product-component--Document` | file |

The GUI's end-to-end suite runs that estate through the product: the repository is synced, the templates saved, the
cache imported, the chain run, a plan executed, and the source read one interface at a time through the GUI and the
CLI.

## What no suite proves

- That a deployment's OSDU behaves as its contract says. Only a live run proves that.
- The kinds no sample mapping renders. The route each would take is decided by the rules in
  [documents.md](documents.md#routes), which are covered, but no sample renders a reference data kind or a dataset
  collection end to end.
- Reading bulk data back out of a DDMS, and reading arrays back out of the Reservoir DDMS: both are written and
  verified, and only the ETP array metadata is read back.
- The deployed hosting: managed identity, Key Vault references and the images are exercised only by the image smoke
  starts in CI.
