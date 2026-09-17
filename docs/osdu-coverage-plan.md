# OSDU coverage plan: every kind, every DDMS, one delivery engine

Status: in progress. This plan is executed stage by stage; each stage lands with its tests, its documentation and its
migration (where it changes the module's model), builds with zero warnings, and is committed on its own.

The architecture is [interfaces-design.md](interfaces-design.md). This document is the inventory of what is missing
and the order it is built in.

## 1. The rule every stage follows

**Every route is built from OSDU's own code and contracts, never from memory.**

- Core services: the OpenAPI specifications in `D:\Projects\eq\src\osdu-csharp-client-main\openapi_specs`.
- Every DDMS and ingestion workflow: the contracts pinned in `osdu/specs`, with the project, file and commit each was
  read at in `osdu/specs/sources.json`. Where a service publishes no static contract (the Reservoir Management DDMS
  generates its OpenAPI at runtime), the route is built from the service's source files at the pinned commit, and
  the brief names each file.
- Each route type ships with an integration brief (`osdu/specs/<service>/INTEGRATION.md`) that cites the contract for
  every call it makes, and with contract tests that check every request the route sends against the pinned contract.

## 2. What OSDU accepts, and what the engine covers today

| OSDU write surface | Contract | Covered today | Gap |
| --- | --- | --- | --- |
| Storage records (every kind) | storage v2 | `osduRecord` | none for plain records |
| File datasets (File service) | file v2 | `osduFile` | none for single files |
| Dataset service (registry, storage instructions, file collections) | dataset v1 | `dataset` (stage 6): single files and file collections, uploaded as Azure, MinIO, S3 and Google Cloud Storage take them | IBM collections: the location names no endpoint for its credentials, so the record is held |
| Manifest ingestion (`Osdu_ingest`) | workflow v1 | `osduManifest`, by reference when the flow asks or a manifest is above the inline limit (stage 6) | none |
| Other ingestion workflows (CSV parser, Energistics parsers, SEG-Y to VDS, ZGY, MDIO, External Data Services) | workflow v1 + DAG sources | `workflow` (stage 6): every deliverable workflow the workflows and EDS briefs describe | none |
| Files and bulk data of one record | file v2, dataset v1, workflow v1, `osdu/specs/wellbore-ddms` | `fileAndDdms` and `manifestAndDdms` (stage 6) | none |
| Wellbore DDMS, bulk kinds (WellLog, WellboreTrajectory, PPFGDataset, WellPressureTestRawMeasurement) | `osdu/specs/wellbore-ddms` | every bulk collection (`ddms`, stage 5) | none |
| Wellbore DDMS, record kinds (Well, Wellbore, WellboreMarkerSet, WellboreIntervalSet, WellLogAcquisition) | `osdu/specs/wellbore-ddms` | every record collection (`ddms`, stage 5) | none |
| Seismic DDMS (Seismic Store) | `osdu/specs/seismic-ddms` | ddms shape `seismicStoreV3` (stage 7): `dataset--FileCollection.*` records as datasets' `seismicmeta`, their files uploaded to Azure Blob Storage, Google Cloud Storage or S3 as Seismic Store's clients write them | the record scope leaves the dataset (Seismic Store has no reversible delete); removing everything is refused on gc; the v4 service and work product components (the storage route) are not this shape's |
| Reservoir DDMS (Open ETP server, ETP 1.2) | `osdu/specs/reservoir-ddms` | no | route type `etp` |
| Rock and Fluid Samples DDMS | `osdu/specs/rafs-ddms` | ddms shape `rafsV2` (stage 7): every collection, records and content tables | none |
| Well Delivery DDMS | `osdu/specs/well-delivery-ddms` | ddms shape `wellDeliveryV1` (stage 7): every entity type it knows, versioned references, its Storage copy | none |
| Production DDMS (DSPDM) | `osdu/specs/production-dspdm` | route type `dspdm` (stage 7): business object rows of any business object whose rows one unique constraint finds, read from DSPDM's metadata, found again before every save, inserted and updated in one save | the typed spatial API and child rows in one save are not used; rows are removed for good only (DSPDM keeps no deleted rows); the route relies on DSPDM's shipped update and time zone settings |
| Production time series (historian) | `osdu/specs/production-timeseries` | ddms shape `productionTimeSeriesV1` (stage 7): ProductionValues records through Storage, their points in requests under the body limit, every accepted version read back | none (the historian has no delete for points) |
| Reservoir Management DDMS | `osdu/specs/reservoir-management-ddms` | ddms shape `reservoirManagement` (stage 7): the nine header kinds through Storage, taken into the service's database by its list call, and the rows of the tables below them posted with their keys fed down | the service's copy of a record keeps only its id and parent (its own write would write the record again without its id); records past the first 100 of a kind, and Kr syntheses, need an operator to insert the copy |
| External Data Services | `osdu/specs/eds-dms` | the storage (or manifest) and workflow routes (stage 7): connected source registry entries, data jobs and proxy datasets checked for what eds-dms and the EDS workflows need before they are sent (`target.eds`), a job's run state carried into every rewrite and a version EDS writes not taken for drift, a fetch run by the workflow route | not checked: whether the secrets a registry entry names exist (no Secret service contract is pinned), whether a job's scheme name is one of its entry's (the brief leaves it open), and whether the proxy datasets of one entry share a source partition (it spans records EDS writes itself) |
| DDMS discovery (Register service) | register v1 | `target.ddms`, with a registration read by id (stage 5) | none |

And the engine itself:

| Area | Today | Gap |
| --- | --- | --- |
| Flow document | one kind, one protocol per file | source files with interfaces; the single form kept |
| Route choice | by hand (`target.protocol`) | resolved from the kind, the schema and the DDMS registry; override kept |
| Order between kinds | lineage between separate flows | order between interfaces from schema relationships, cycles, `after:` |
| Run | one flow | one source: preflight, waves, stop rules, one outcome |
| Record dependencies | none | a record waits for the record it refers to, and is released when it lands |
| Partial redelivery | record, files, bulk, metadata, payload, all, per route (stage 5); each part of a composed or workflow route, the workflow run included (stage 6) | none |
| Catalog lookup of a ledger identity | pipeline name hash | an indexed read model of sources and interfaces |
| Views | a flow's records and runs | sources, interfaces, filters and roll-ups in the GUI, API and CLI |

## 3. Stages

Each stage lists what it builds and what proves it done. A stage is done when its tests pass (the fast suites, the SQL
Server suites against a real database, and the contract tests of its routes), the solution builds with zero warnings,
the GUI builds and lints clean, the documentation describes what shipped, and the change is committed.

### Stage 1: contracts and briefs

- Every contract pinned in `osdu/specs` with its provenance.
- One integration brief per service, citing the contract for every call.
- A contract test harness: it loads a pinned OpenAPI document and checks a request (method, path, parameters,
  headers, body schema) against it; every route type's tests run their requests through it.

Done when every service in section 2 has a pinned contract and a brief, and the harness validates the existing four
protocols' requests against the core specifications.

Status: done. The harness checks every request the four protocols send against the core specifications, which are
copied into `osdu/specs/core` with their provenance (Partition, Unit and the two CRS services included). Every service
in section 2 has a brief (`osdu/specs/<service>/INTEGRATION.md`, the core services and the Register service in
`osdu/specs/core`), each citing the pinned contract or the source file and commit behind every statement, and marking
what it infers. The Reservoir Management DDMS's contract is generated from its source at the pinned commit by
`tools/generate-rmddms-openapi.py` and pinned with the generator named in `sources.json`. A test checks that every
pinned file has its provenance row at the size recorded, with a full commit (or its generator), and that every service
folder holds its brief.

### Stage 2: source documents and interfaces

- The document model: `interfaces`, the shared defaults and per-interface overrides, `ledger:` adoption, the single
  form read as a source with one interface.
- Ledger identity per interface (`FlowId.Of("<flow>/<interface>")`, or the adopted name).
- The catalog read model of sources and interfaces (a module table written by the repository sync, with its
  migration), and every lookup of a ledger identity going through it.
- Route resolution from the facts that need no network: the kind, the template's relationships, what the interface
  declares; the refusals.

Done when every existing flow loads unchanged with the same ledger identity, a source file with several interfaces
loads with the right identities and routes, and every refusal is covered by a test.

Status: done. The route is resolved from what an interface declares (`files`, `bulk`, `route:`), and the run's
preflight checks it against the kind the mapping renders. Choosing a route from the template's relationships and the
DDMS registry belongs to stages 4 and 5; an interface declaring both `files` and `bulk`, and `manifest` with `bulk`,
are refused until stage 6 builds the composed routes. The read model is `osdu.Interface` (module version 1.6.0), and
every lookup of a pipeline by ledger identity goes through it.

### Stage 3: the source runtime

- A run of a source: interface selection (`interfaces` run parameter, a member's interface, a record's interface),
  preflight across every selected interface, waves in dependency order, parallel interfaces in a wave, each
  interface's plan, fan-out and drain as today.
- The stop rules: the whole run at preflight, an interface and its dependents on an interface failure, never for a
  record; `failWhen` thresholds and the outage breaker.
- The run outcome (one per interface, and the source's totals) and the trace events.
- Fan-out members and drains carry their interface.

Done when a source with several interfaces runs end to end on SQL Server, a failing interface stops its dependents
and not the others, a re-run does only what was left, and a single-interface run's outcome is unchanged.

Status: done. Interfaces are selected by the run payload (`interfaces`, and `interface` for one), and a stopped run
closes its submission as failed, so the next run sends what it left. The SQL Server chain suite runs a source of
wellbores and the well logs waiting for them through the document executor, each interface fanned out over member runs
under its own ledger.

### Stage 4: order from the schemas

- The dependency graph from the templates' `x-osdu-relationship` annotations, limited to properties the mapping
  fills and kinds another interface of the source delivers; group order as the tie-break; cycles cut or refused;
  `after:`.
- `plan --explain` and the flow page's explanation of routes and order.

Done when the ordering tests cover a chain, a diamond, a cycle cut by group order, an unresolved cycle, and `after:`.

Status: done. The order is worked out where the templates are: the run's preflight (a cycle nothing cuts is a finding),
`sqlflow check` (which is the explanation: every interface with its route, wave, what it waits for and why, and the
references left out of a cycle; `--json` carries the same), and the API's listing of a flow's interfaces. The group
order is grounded in the OSDU data definitions, and OSDU's manifest ingestion, which orders a manifest's records by
their references and refuses a cycle among them, was read from its library (docs/interfaces-design.md section 6). The
flow page's explanation is part of stage 9.

### Stage 5: route types on a common footing

- Protocols are built from a resolved route rather than from `target.protocol`; the four existing protocols become
  the `storage`, `file`, `manifest` and `ddms` route types; `target.protocol` maps onto them.
- The generic tabular DDMS route (the Wellbore DDMS shape, parameterised by collection), serving every bulk kind the
  Wellbore DDMS serves; the well log checks become tabular bulk checks.
- The DDMS catalog (`target.ddms`) and discovery through the Register service.
- The DDMS record route for DDMS record kinds.
- Partial redelivery by part.

Done when the route contract tests pass for every Wellbore DDMS kind, and a source delivers a trajectory and a well
log through the same route type.

Status: done. The four protocols are the four route types, and `target.protocol` takes the route names as well as the
protocols they map onto. The ddms route sends each record to the collection of the DDMS serving its entity type, which
the record id names: a DDMS under `target.ddms` (its root, shape and collections, or its Register service
registration, read by id when the protocol is built), then the Wellbore DDMS, whose nine collections are catalogued
from its pinned contract. Bulk collections keep their tabular checks, now per collection (curve ids and widths, or
trajectory station properties), with the Wellbore DDMS's record rules checked before anything is sent and the bulk link
carried on metadata updates; record collections take records alone and purge through storage. The contract tests
write, read and remove through every Wellbore DDMS collection, and a source delivers surveys and well logs through the
one route, a survey whose stations its record does not describe held before anything is sent (the sample estate gained
the WellboreTrajectory template, mapping and the reference values its stations resolve against). Redelivery takes the
part a route sends (`record`, `files`, `bulk`, `metadata`, `payload`, `all`). The Register service's lookup by type
cannot carry an entity type with its group, so discovery reads a registration the flow names.

### Stage 6: datasets and workflows

- The `dataset` route through the Dataset service: single files and file collections, on every provider's staging area.
- The composed routes: files with DDMS bulk data; manifests followed by DDMS bulk data.
- Manifest by reference.
- The generic `workflow` route: files uploaded and registered, a named workflow triggered with a payload built from
  the interface's declaration, the run polled, the created records found and settled.

Done when every workflow in `osdu/specs/workflows` has a contract test for its payload and a route test end to end
against a fake workflow service.

Status: done. The dataset route registers a record of a dataset kind under its own id, its files uploaded where the
Dataset service says, the way each provider takes them (Azure's Data Lake directory, the POST policies of MinIO and S3,
Google Cloud Storage's folder token), and any other record refers to one dataset of its files under an id derived from
its own; an IBM location, whose credentials are for an endpoint it does not name, holds the record. The composed routes
are `fileAndDdms` (the files through the file service, then the record and its bulk data through its DDMS) and
`manifestAndDdms` (the manifest, then the bulk data, with the DDMS's bulk link carried into every manifest that rewrites
a record holding bulk data). Each part of a record's payload goes when its content hash moves or a redelivery names it,
and the ledger keeps the parts in the payload hash and location it already had, so the module's model is unchanged. A
manifest goes by reference when the flow asks, or when it is above the inline limit and the partition registers the
by-reference workflow; without that workflow a batch is split into manifests under the limit. The workflow route writes
its anchor record, registers its inputs through the Dataset service, runs up to four stages of the workflows the
catalog describes (every context checked against its workflow's contract before it is sent; the test DAG refused),
reads each stage's outputs from a template or from XCom (the Workflow service's `latestInfo`, or Airflow's REST API on
Airflow 2 or 3), and finds what the run wrote by its anchor, ids, artefact, search or manifest. A record's files go as
`application/octet-stream` beside bulk data unless the flow names their type (`filesContentType`). The route tests run
every workflow end to end against a fake Workflow service and platform, and the dataset and composed routes on every
provider; a source delivers well logs with LAS files and curves through the executor, where new curves send only the
curves and a redelivery of the files sends only the files. Every request keeps to the pinned contracts, the Airflow
contracts pinned from the releases OSDU deployments run; the one difference the tests let through is the Workflow
contract's object typing of context values (`osdu/specs/workflows/INTEGRATION.md` section 7).

### Stage 7: the other DDMSs

One call pattern per service the briefs identify, each with its contract tests:

- Seismic DDMS (ddms shape `seismicStoreV3`).
- Reservoir DDMS (route type `etp`): an ETP 1.2 client over WebSocket with the Avro encoding of the messages it uses.
- Rock and Fluid Samples DDMS (ddms shape `rafsV2`).
- Well Delivery DDMS (ddms shape `wellDeliveryV1`).
- Production DDMS (route type `dspdm`) and production time series (ddms shape `productionTimeSeriesV1`).
- Reservoir Management DDMS (ddms shape `reservoirManagement`).
- External Data Services (the storage and workflow routes).

Done when each has a route test end to end against a fake of its service built from its contract, and the kinds it
serves resolve to it.

Status: in progress. A DDMS that takes an OSDU record and keeps data of its own for it is a shape of the `ddms` route,
found by the record's entity type under `target.ddms`; one whose unit is not an OSDU record is a route type of its own
(docs/interfaces-design.md section 5.10). Built: the Well Delivery DDMS (`wellDeliveryV1`: an entity per write under a
version recorded first, references given the versions the DDMS holds, content rewritten in place, the Storage copy
removed with the entity), the Rock and Fluid Sample DDMS (`rafsV2`: records in arrays, content tables per type and
schema version checked against the service's catalogue, both storage modes, the content datasets removed with the
record) and the production historian (`productionTimeSeriesV1`: ProductionValues records through Storage with their
link to their points, the points read from parquet or the service's JSON and checked against the record's series,
requests under the body limit sent once each, every accepted version read back through the query service) and Seismic
Store (`seismicStoreV3`: `dataset--FileCollection.*` records registered as their datasets' `seismicmeta` under a lock
id each record keeps, the files uploaded to Azure Blob Storage, Google Cloud Storage or S3 with the credentials the
service issues, renewed when they expire, a try resumed past the objects that landed, the dataset closed with its file
metadata, the record's version read from Storage) and the Reservoir Management DDMS (`reservoirManagement`: header
records through Storage, taken into the service's database by its list call until Search serves them, the rows of the
tables below them posted one per call with their keys fed down and recorded, found again after a failed try, replaced
on redelivery, and never the service's own record write or purging delete), each against a fake of the service (and,
for Seismic Store, of the three object stores, the S3 one checking every signature with the AWS SDK) with every request
to an OSDU service checked against its pinned contract, and the ddms route split into one writer per shape. The
Reservoir DDMS is the route type `etp` (`osduEtp`): Energistics data objects in dataspaces of its own store, reached over
ETP 1.2 on a WebSocket. The client is the module's own, built from the pinned protocol
(`osdu/specs/reservoir-ddms/etp-1.2.avpr`): the 47 messages and 37 data types the route uses as C# records with an Avro
binary codec, every one of them round-tripped in a test against a codec driven by that same file, so the checked-in
records and the schema cannot drift apart. Over it sit the framing (one message per WebSocket message, gzip both ways,
even ids, replies collected by correlation id until FIN, the per-item error shapes), the session (the upgrade with the
subprotocol and the flow's headers and credentials, the negotiation, the keep-alive ping, the close) and the route: the
object's identity read out of its own XML and checked before a session opens, the dataspace created only when it is
missing with the record's own ACLs and legal tags (its OSDU record's id logged), one transaction per dataspace carrying
a batch's objects and the arrays that fit, a large array declared and then filled slice by slice, a refused commit rolled
back, a verify that lists the dataspace once, and a removal that deletes the object but never a dataspace, whose delete
purges an OSDU record. It is tested against a fake ETP server built from the brief, over a real WebSocket on the loopback
interface. External
Data Services takes no pushed data, so it is served by the routes that exist: the records that configure it (registry
entries, data jobs and proxy datasets) are checked when a plan renders them, with the flow's `target.eds` saying what the
deployment needs, and held with every rule they break; a data job's run state, which EDS writes after every fetch, is
carried into every rewrite on the storage, manifest and workflow routes (the manifest route now carries a flow's
`preserveDataKeys` as the others do); a write that carries keys records the hash of the content the flow owns, so a verify
tells a version another system wrote from drift; and a fetch is the stage 6 workflow route running `eds_ingest` or
`eds_scheduler`. Every rule is tested against the brief's cases, and the three routes against the fake platform, a job
rewritten, fetched and verified on each. The Production DDMS core service is the route type `dspdm` (`osduDspdm`): rows of
business objects, whose templates' kinds have the source `dspdm`, checked against the business object DSPDM's metadata
describes, found again by one of its unique constraints before they are saved, inserted and updated in one save with a
refused save sent again row by row, a save whose answer was lost found again by the marker recorded before it, rows
another system wrote held unless the flow takes them over, versions from the rows' change dates, and a delete that is for
good; tested against a fake of DSPDM built from its code, every request checked against its contract. Every service of
the stage is built.

### Stage 8: records that wait for other records

- The reference table and its migration; references kept at render time; the hold and the release; the optional
  storage check for references outside the ledger.

Done when a child waits for its parent and goes out when the parent lands, across interfaces and across runs.

Status: done. What a rendered record refers to is kept beside its document on the record itself rather than in a table
of its own (docs/interfaces-design.md section 7): an estate holds hundreds of millions of records, and only the work
still to be sent needs its references. A claim leaves a record waiting rather than taking it, so a wait charges no
attempt; the record says which id it waits for, and a filtered index finds the waiters of an id when it lands. Waits
are decided under one lock of the ledger and never lead back to the record deciding, so two records never wait for each
other. A delivery releases what waited for it, a run ends the waits nothing holds any more before it plans, and an
operator can send one waiting record as it is. `target.verifyReferences: storage` asks storage about the ids the ledger
does not hold and holds a record that would write a dangling reference. Module version 1.7.0 (`RecordWaits`).

### Stage 9: views and operations

- GUI: a source's page lists its interfaces with their route, order, state and counts; the records, submissions and
  runs views filter by interface; the run page shows each interface.
- API and CLI: the same filters and roll-ups; `run --set interfaces=...`; redelivery by part.
- Lineage: one write per interface.

Done when the GUI builds and lints clean, the GUI end-to-end suite covers a multi-interface source, and the API and
CLI tests cover the filters.

### Stage 10: samples, documentation and verification

- The sample estate as one source file adopting the existing ledgers, plus sample interfaces covering the kinds
  matrix.
- The documentation: document reference, protocols (route types), operations, ledger.
- A test matrix across every route type; live verification against the OSDU services the test environment runs,
  every created id logged and removed.

Done when the full suites pass, the live verification report lists what was verified live and what against contracts
only, and every created OSDU id is removed.

## 4. How coverage is proven

| Level | What it shows | Where |
| --- | --- | --- |
| Contract tests | every request a route sends matches the pinned contract | per route type |
| Route tests | a route delivers, resumes, verifies and removes against a fake service built from the contract | per route type |
| Engine tests | resolution, order, stops, waits, partial redelivery | in memory and on SQL Server |
| End to end | a multi-interface source through the platform, GUI included | SQL Server chain, GUI end-to-end suite |
| Live | a route against the real service | the test environment, for every service it runs |

A service the test environment does not run is proven to the contract level only, and the final report says so.
