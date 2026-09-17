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
| Dataset service (registry, storage instructions, file collections) | dataset v1 | no | route type `datasetCollection` |
| Manifest ingestion (`Osdu_ingest`) | workflow v1 | `osduManifest` | manifest by reference for large batches |
| Other ingestion workflows (CSV parser, Energistics parsers, SEG-Y to VDS, ZGY, MDIO, PDMS CSV) | workflow v1 + DAG sources | no | route type `workflow` |
| Wellbore DDMS, bulk kinds (WellLog, WellboreTrajectory, PPFGDataset, WellPressureTestRawMeasurement) | `osdu/specs/wellbore-ddms` | WellLog only (`osduWellLog`) | generic tabular DDMS route |
| Wellbore DDMS, record kinds (Well, Wellbore, WellboreMarkerSet, WellboreIntervalSet, WellLogAcquisition) | `osdu/specs/wellbore-ddms` | through Storage only | DDMS record route |
| Seismic DDMS (Seismic Store) | `osdu/specs/seismic-ddms` | no | route type `seismicStore` |
| Reservoir DDMS (Open ETP server, ETP 1.2) | `osdu/specs/reservoir-ddms` | no | route type `etp` |
| Rock and Fluid Samples DDMS | `osdu/specs/rafs-ddms` | no | DDMS routes (shape from its brief) |
| Well Delivery DDMS | `osdu/specs/well-delivery-ddms` | no | DDMS record route (shape from its brief) |
| Production DDMS (DSPDM) | `osdu/specs/production-dspdm` | no | route from its brief |
| Production time series (historian) | `osdu/specs/production-timeseries` | no | route type `timeSeries` |
| Reservoir Management DDMS | `osdu/specs/reservoir-management-ddms` | no | route from its brief |
| External Data Services | `osdu/specs/eds-dms` | no | route from its brief |
| DDMS discovery (Register service) | register v1 | no | registry client and `target.ddms` catalog |

And the engine itself:

| Area | Today | Gap |
| --- | --- | --- |
| Flow document | one kind, one protocol per file | source files with interfaces; the single form kept |
| Route choice | by hand (`target.protocol`) | resolved from the kind, the schema and the DDMS registry; override kept |
| Order between kinds | lineage between separate flows | order between interfaces from schema relationships, cycles, `after:` |
| Run | one flow | one source: preflight, waves, stop rules, one outcome |
| Record dependencies | none | a record waits for the record it refers to, and is released when it lands |
| Partial redelivery | metadata, payload, all | record, files, bulk, all, per route |
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

### Stage 6: datasets and workflows

- `datasetCollection` through the Dataset service.
- The composed routes: files with DDMS bulk data; manifests followed by DDMS bulk data.
- Manifest by reference.
- The generic `workflow` route: files uploaded and registered, a named workflow triggered with a payload built from
  the interface's declaration, the run polled, the created records found and settled.

Done when every workflow in `osdu/specs/workflows` has a contract test for its payload and a route test end to end
against a fake workflow service.

### Stage 7: the other DDMSs

One route type per call pattern the briefs identify, each with its contract tests:

- Seismic DDMS (`seismicStore`).
- Reservoir DDMS (`etp`): an ETP 1.2 client over WebSocket with the Avro encoding of the messages it uses.
- Rock and Fluid Samples DDMS.
- Well Delivery DDMS.
- Production DDMS and production time series.
- Reservoir Management DDMS.
- External Data Services.

Done when each has a route test end to end against a fake of its service built from its contract, and the kinds it
serves resolve to it.

### Stage 8: records that wait for other records

- The reference table and its migration; references kept at render time; the hold and the release; the optional
  storage check for references outside the ledger.

Done when a child waits for its parent and goes out when the parent lands, across interfaces and across runs.

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
