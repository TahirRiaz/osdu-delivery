# Interfaces design: one flow per source, every OSDU type through one generic pipeline

Status: accepted, and built stage by stage ([osdu-coverage-plan.md](osdu-coverage-plan.md)). Built so far: the
document model with both forms, the ledger identity per interface and `ledger:` adoption, the read model of sources
and interfaces, route resolution from what an interface declares with its refusals, the order from the schemas'
relationships and `after:` with its waves, the source runtime with its preflight, stop rules and outcome, and the API
and CLI selection and explanation of interfaces (sections 3, 4, 6, 8 and 10, and the routes of section 5.2 that the
four existing protocols deliver). The rest of this document is the design
the later stages build. What shipped is documented in [../osdu/docs/documents.md](../osdu/docs/documents.md#a-source-with-interfaces)
and [../osdu/docs/operations.md](../osdu/docs/operations.md#running-a-source).

## 1. Why

Today a delivery flow delivers one OSDU kind through one protocol its author names. A source system that produces
fields, wells, wellbores, documents, trajectories and seismic needs one flow file per kind, each repeating the same
connection, target, credentials and schedule, each with a protocol chosen by hand, and nothing checks that the
protocol suits the kind. With the hundreds of kinds OSDU defines, that does not scale, and the protocol set carries
well log assumptions a generic system must not have.

This design keeps what works (the ledger, change detection, fan-out, leases, the failure handling per record) and
changes three things:

1. **One flow file per source.** The file lists the source's interfaces, one per OSDU type it delivers.
2. **The system decides how and when.** Each interface's route (how OSDU accepts it) and its place in the order are
   worked out from what OSDU itself publishes: the kind, its schema and the DDMS registry. No kind is named in code.
3. **One run executes the whole source and tracks everything**, with clear rules for when a record, an interface or
   the whole run stops.

## 2. Principles

- **No kind is special.** The code never tests for a kind or an entity type. Well logs, wellbores and seismic are
  test data, not branches.
- **A new kind needs a mapping, not code.** A new kind of OSDU API (a DDMS with its own call pattern) needs one route
  type, once, and then serves every kind that API accepts.
- **OSDU behaviour comes from OSDU's own code and contracts, never from memory.** The core services are verified
  against the OpenAPI specifications in `D:\Projects\eq\src\osdu-csharp-client-main\openapi_specs`; every DDMS and
  ingestion workflow against the contracts pinned in `osdu/specs` (with the project, file and commit each was read
  at in `osdu/specs/sources.json`). An API without a pinned contract is not designed for until it has one.
- **Both forms are first-class.** A source file with many interfaces and a flow with a single interface run on the
  same engine, so a type that is better managed on its own stays on its own. There is one runtime, one ledger path
  and one set of views for both.
- **Any part can be delivered again on its own.** An interface, a record, or one part of a record (its record, its
  files, its DDMS bulk data) can be sent again without redoing the rest.
- **Traceability stays whole.** Every interface keeps its own ledger identity, so every record still answers which
  row it came from, what it became in OSDU and everything that happened to it.

## 3. The flow file

```yaml
flowType: delivery
name: petrel-project-x                        # the source; one pipeline in the catalog
parameters: { ... }                           # shared by every interface
schedule: { ... }

source:
  connection: ${env:PETREL_DB}                 # one ingestion database for the source
  systemColumns: { ... }                       # defaults for every interface
  incremental: { ... }
  work: ../.work/petrel-project-x

target:
  endpoint: ${env:OSDU_URL}
  auth: { ... }
  headers: { data-partition-id: opendes }
  ddms:                                       # optional: where DDMSs live when the registry does not say (section 5.3)
    wellbore: { root: /api/os-wellbore-ddms, shape: wellboreDdmsV3, entityTypes: { WellLog: welllogs, WellboreTrajectory: wellboretrajectories } }

render: { cacheVersion: current, parameters: { ... } }   # defaults for every interface
change: { ... }
reliability: { ... }

interfaces:
  fields:
    record: { object: Petrel.ing.Field, key: [field_name], primaryKey: RecId }
    mapping: Field@1.0.0
  wells:
    record: { object: Petrel.ing.Well, key: [uwi], primaryKey: RecId }
    mapping: Well@1.2.0
  wellbores:
    record: { object: Petrel.ing.Wellbore, key: [uwbi], primaryKey: RecId }
    datasets: { aliases: { object: Petrel.ing.WellboreAlias, join: { uwbi: uwbi } } }
    mapping: Wellbore@1.3.0
  documents:
    record: { object: Petrel.ing.Document, key: [doc_id], primaryKey: RecId }
    files: { root: ../data/documents, locationColumn: doc_folder, pattern: "*.pdf" }
    mapping: Document@1.0.0
  trajectories:
    record: { object: Petrel.ing.Trajectory, key: [uwbi, survey_id], primaryKey: RecId }
    bulk: { root: ../data/trajectories, locationColumn: survey_folder, pattern: "*.parquet" }
    mapping: WellboreTrajectory@1.1.0
  seismic:
    record: { object: Petrel.ing.Seismic, key: [survey, line], primaryKey: RecId }
    files: { root: ../data/seismic, locationColumn: segy_folder, pattern: "*.segy" }
    route: manifest
    mapping: SeismicTraceData@1.3.0
```

An interface states **what** it is:

| Key | Meaning |
| --- | --- |
| `record` | The interface's record table: `object`, `key` (the record identity), `primaryKey` (the identity column it is paged and fanned out by), `scope`. |
| `datasets` | Child tables joined to the record, as today. |
| `mapping` | The pinned mapping. Its template fixes the interface's kind. |
| `files` | Files the record carries as datasets, uploaded and registered before the record. |
| `bulk` | Tabular bulk data a DDMS stores for the record, written after the record. |
| `route` | Optional: the route, instead of the one the system chooses (section 5). `manifest` sends the interface's records through the ingestion workflow in batches. |
| `after` | Optional list of interfaces this one waits for, beside the ones the schemas imply (section 6). |
| `failWhen` | Optional thresholds that turn record failures into an interface failure (section 8). |
| `render`, `change`, `reliability` | Optional overrides of the source's defaults. |

The shared blocks are written once. The existing top-level `source.record`, `source.datasets`, `source.payloads`,
`render.mapping` and `target.protocol` form stays valid: it is read as a source with one interface (section 11).

## 4. Identity and tracking

- **One pipeline per file.** The catalog, the schedules, the run queue and lineage see one pipeline for the source.
- **One ledger identity per interface.** An interface's records, submissions, watermark, OSDU id claims and
  statistics are keyed by `FlowId.Of("<flow>/<interface>")`. The ledger schema does not change: `FlowId` is already a
  derived id, and one row producing records of two kinds lands under two identities, as one row read by two flows
  does today.
- **Adopting an existing ledger.** `ledger: <old flow name>` on an interface keeps an existing flow's identity, so
  consolidating today's single-kind flows into a source file loses no history and re-sends nothing.
- **One run, many submissions.** A run registers one submission per interface it plans, each with its own window,
  slices and batches. The run page lists the interfaces with their state and counts; the records, submissions and
  statistics views filter by interface and roll up to the source.
- **The run trace** carries `interface.started`, `interface.completed`, `interface.stopped` and
  `interface.skipped` beside the existing batch and record events.
- **Lineage** declares one write per interface (its kind in the target partition) and one read per record and child
  table, so the graph shows everything the source feeds.

## 5. Routes: how each interface is delivered

### 5.1 The facts, for any kind

1. **The kind.** `authority:source:group--Entity:version` names the group type (reference data, master data, work
   product, work product component, dataset) and the entity type. Parsed the same way for every kind.
2. **The schema.** The mapping's template carries OSDU's `x-osdu-relationship` annotations: which properties refer to
   which group and entity types, including the dataset group a record's `Datasets` refer to. The template code
   already reads them.
3. **The DDMS registry.** OSDU's Register service lists DDMS registrations by type
   (`GET /api/register/v1/ddms?type=...`, openapi register), each with the entity types it serves and an OpenAPI
   document for each.

### 5.2 The decision

| The interface declares | And | The route |
| --- | --- | --- |
| no `files`, no `bulk` | | `storage`: the record through the Storage service |
| `files` | the schema refers to the dataset group | `file`: every file uploaded and registered (File service), then the record referring to its datasets |
| `files` naming a collection kind | | `datasetCollection`: the files stored and registered as one dataset (Dataset service), then the record |
| `bulk` | a DDMS serves the entity type | `ddms`: the record through that DDMS, then the bulk data |
| `files` and `bulk` | both of the above | `fileAndDdms`: files registered, the record written through the DDMS referring to them, then the bulk data |
| `route: manifest` | any of the above but `ddms` | `manifest`: files registered first, then the records in manifests through the ingestion workflow |
| `route: manifest` and `bulk` | | `manifestAndDdms`: files registered, the records by manifest, the run polled, then the bulk data through the DDMS |

`route:` overrides the choice. A route that cannot deliver what the interface declares is refused when the flow is
read (files on `storage`, bulk data without a DDMS). What needs no network is checked when the flow is read; the
DDMS lookup happens in the run's preflight, on the node.

### 5.3 Finding a DDMS

In order:

1. `target.ddms` in the flow: a named DDMS, its root or URL, its route shape, and the collection each entity type is
   served under. The collection is always stated, never derived: the Wellbore DDMS serves `WellLog` under `welllogs`
   and `PPFGDataset` under `ppfgdataset`, so no rule turns an entity type into its path.
2. The Register service's registrations, when the flow sets `target.ddmsDiscovery: register`: the registration's
   OpenAPI document is matched against the known route shapes, and the collection is read from its paths.
3. Neither: the interface is refused in preflight, naming the entity type and the two ways to declare its DDMS.

### 5.4 Route types in code

A route type is one call pattern, written once:

| Route type | Services (openapi) | Status |
| --- | --- | --- |
| `storage` | storage v2 | exists (`osduRecord`) |
| `file` | file v2, storage v2 | exists (`osduFile`) |
| `manifest` | file v2, workflow v1, storage v2 | exists (`osduManifest`) |
| `ddms` with shape `wellboreDdmsV3` | wellbore DDMS v3: `POST /{collection}`, `/{collection}/{id}/data`, `/{collection}/{id}/sessions` | generalised from `osduWellLog`; serves WellLog, WellboreTrajectory, PPFGDataset, WellPressureTestRawMeasurement and any DDMS with the same shape |
| `datasetCollection` | dataset v1: `storageInstructions`, `registerDataset` | new |
| `fileAndDdms`, `manifestAndDdms` | the above, composed | new |
| other DDMS shapes (seismic, reservoir, ...) | their own specifications | one route type each, once their specifications are added |

The well log specific checks become checks of any tabular bulk upload: the row labels of each chunk read before a
session, and the rows and columns read back after it.

## 6. Order

- **Dependencies from the schemas.** Interface A waits for interface B when A's mapping fills a property whose
  relationship targets B's kind, and B is in the same source. A mapping that does not fill the property creates no
  dependency. A relationship that names only a group (`Datasets[]` names `dataset`) targets every kind of the group.
- **Group order:** reference data, master data, datasets, work product components, work products. It follows the
  direction OSDU's schemas refer in: a work product lists its components (`WorkProduct.1.0.0`, `Components[]`), a
  component refers to its datasets (`AbstractWPCGroupType.1.0.0`, `Datasets[]`) and to master data, and a dataset refers
  to reference data alone (`AbstractDataset.1.0.0`) (project 91, osdu/data/data-definitions, `Generated/` at
  `99f8fc88d8ad838b5738ac5ad92ac643538b5766`). It orders the interfaces within a wave and cuts cycles. OSDU's own
  manifest ingestion does not order by group: it orders a manifest's records by the ids each record's content refers
  to, each record after every record of the manifest it refers to, and does not wait for an id outside the manifest
  (project 823, osdu/platform/data-flow/ingestion/osdu-ingestion-lib, `osdu_ingestion/libs/manifest_analyzer.py`,
  `ManifestAnalyzer`, at `b09eca721fd0aea002878bf7313f20398ab001ed`). The order is the same principle at the level
  of interfaces.
- **Cycles.** OSDU schemas refer both ways (a wellbore refers to its definitive trajectory, the trajectory to its
  wellbore). A cycle is cut first by `after:` (a reference the document orders the other way is not waited for), then
  by group order (a reference from an earlier group to a later one points back); a cycle within one group that
  `after:` does not decide is refused, naming the interfaces. Templates live in the catalog, so the order is worked
  out by the run's preflight, `sqlflow check` and the API, not when the flow is read. The references that point back
  are written as they are: the ids are deterministic, so they resolve once the other side lands. One manifest cannot
  carry both sides of a cycle: the ingestion library sorts a manifest's records with `toposort` 1.6, which raises
  `CircularDependencyError` on a cycle, and its processor catches only errors of single records
  (`osdu_ingestion/libs/processors/single_manifest_processor.py`, `process_manifest`). The manifest route keeps records
  that refer to each other out of one manifest (section 7).
- **`after:`** adds dependencies the schemas do not show.
- **Waves.** Interfaces with nothing left to wait for run together; each still fans out over slices of its own
  primary key, and drains its own batches.

## 7. Records that wait for other records

Order between interfaces is not enough: a wellbore can be held while its well logs are ready.

- When a record is rendered, the values it puts into relationship properties are the OSDU ids it refers to. Each
  reference is kept in a new ledger table, `osdu.RecordReference` (the record, and the OSDU id it refers to), indexed
  by the id.
- A referred id that another record of this ledger has claimed and not delivered holds the referring record,
  saying which record it waits for. An id the ledger does not hold is not waited for: it was delivered by another
  system or already exists. `target.verifyReferences: storage` asks storage for those ids before sending, for
  sources that must not write dangling references.
- When a record is delivered, the records waiting on its id go back to pending, and the next pass sends them. A
  waiting record costs no attempt.
- This needs a module migration (the reference table and its index), which ships with the model change.

## 8. When things stop

### 8.1 The whole run, before anything is sent

The run's preflight checks every interface and stops the run with every finding at once when:

- the flow document is invalid, or the interfaces' dependencies form an unresolved cycle;
- a mapping or template is missing, or a kind has no route;
- a record table is unreachable or has the wrong shape (identity primary key, unique record key);
- the target is unreachable, the credentials are refused, or a legal tag is invalid;
- a DDMS an interface needs cannot be found;
- the ledger database does not allow snapshot isolation.

Nothing is planned or sent, so fixing the cause and running again is all it takes.

### 8.2 One interface, and what depends on it

An interface stops when something fails for the interface as a whole: the credentials are refused on every call, a
service stays unavailable past the retry budget, the source or the work location becomes unreadable, or its
planning fails. Interfaces that depend on it are skipped; the others carry on. The run ends failed and lists which
interfaces completed, stopped and were skipped. Everything already delivered is in the ledger, so the next run
continues where this one stopped.

`failWhen` turns record failures into an interface failure: `failedPercent` (for example 20) or `consecutiveFailures`
(for example 50, counted per error class). By default, 25 consecutive connection or permission failures stop an
interface: that is an outage, not bad data.

### 8.3 Never for one record

A record with a data problem is held, one that keeps failing is failed after its retry budget, a passing problem is
retried later, and a record waiting for another waits (section 7). The interface goes on, and the run succeeds with
its held, failed and waiting counts in the outcome.

### 8.4 A stop

A cancelled run stops every interface, hands its leases back and charges no attempt, as today.

### 8.5 Running again after a failure

A failed run is never redone from the start. What finished is in the ledger, and the next run only does what is left:

| What finished | What the next run does with it |
| --- | --- |
| An interface that completed | Its watermark moved, so the next run finds no changed rows and reads nothing (the tier 0 skip). |
| A record that was delivered | Nothing: its source fingerprint and rendered hash match, so it is skipped without a request. |
| A batch that completed | Nothing: the batch is done in its submission. |
| A record halfway through its steps | The completed steps are kept (a file uploaded and registered, a manifest run started, a DDMS record written), so the next try resumes after the last one. |
| A lease a stopped worker held | Recovered with what the worker had written, and the rest sent. |
| An interface that stopped, and the ones skipped after it | Planned again from their own watermark, and delivered. |

A run whose payload names `interfaces` (or the run page's "run the stopped interfaces") runs only the interfaces
that did not complete. A record can be redelivered or released on its own, as today.

## 9. Keeping a source manageable

- **Every view works per interface.** The run page shows each interface's state, counts and trace on its own line;
  the records, submissions, statistics and history views filter by interface. A problem in one interface is read
  without reading the others.
- **Interfaces are independent units.** Each has its own ledger, watermark, submissions, fan-out and stop rule. A
  large source is many small pipelines run together, not one large one.
- **Order and routes are shown, not guessed.** `sqlflow plan <flow> --explain` and the flow page print every
  interface with its kind, its route, what it waits for and why (the schema property or the `after:`), before
  anything runs.
- **A source can still be split.** One file per source is the recommended shape, not a rule: a very large source can
  use one file per domain (wells, seismic, documents), and dependencies between files are ordered by lineage, as
  flows are today.

## 10. Running part of a source

- `sqlflow run <flow> --payload '{"interfaces":["wells","wellbores"]}'` plans and delivers only those, and the
  interfaces they depend on are not run: their records are read from the ledger as they are.
- A record-scoped run (redeliver, release) names the interface in its payload (`interface`) beside the record keys.
- A redelivery can name the part to send again: `record`, `files`, `bulk`, or `all`. Only that part is sent; the
  others keep what OSDU holds (the record keeps its dataset references and its DDMS bulk link, and a bulk resend
  writes a new bulk version on the same record).
- A type that is often redelivered on its own can live in a flow of its own (the individual form), with its own
  schedule, and still refer to records another source file delivers.
- `drain` drains every interface's pending batches, or the named ones.

## 11. Migration

- A flow without `interfaces` is a source with one interface; its ledger identity stays `FlowId.Of(<flow>)`, so
  existing ledgers, OSDU id claims and history carry on unchanged.
- The sample estate becomes one source file, `recall.yaml`, whose `wellbores` and `welllogs` interfaces adopt the
  ledgers of `recall-wellbore` and `recall-welllog` with `ledger:`.
- `target.protocol` keeps working in the single form: `osduRecord` is `storage`, `osduFile` is `file`,
  `osduManifest` is `manifest`, `osduWellLog` is `ddms` with the well log collection.
- The GUI, API and CLI gain the interface as a filter and the source as a roll-up; the record routes keep their
  ledger identity in the path.

## 12. Tests

- **A matrix of kinds**, none of them special: a reference data kind, a master data kind, a work product component
  with files, a dataset collection, a DDMS kind that is not a well log (WellboreTrajectory), and a manifest batch.
- **Route resolution:** every row of section 5.2, the refusals, a DDMS found in the flow and one found through a fake
  Register service.
- **Order:** dependencies from relationships, a cycle cut by group order, a cycle refused, `after:`, parallel waves.
- **Records that wait:** a child held for its parent and released when the parent lands; an unknown parent not
  waited for.
- **Stops:** each preflight finding, an interface stopped with its dependents skipped, `failWhen`, a cancellation.
- **Compatibility:** every existing single-kind flow reads and runs unchanged; the sample estate consolidated
  without re-sending anything.
- **SQL Server:** a source with several interfaces, fanned out over several nodes, end to end.

## 13. Build order

1. The document model: `interfaces`, the shared defaults, the single-interface reading of today's form, the ledger
   identities, and route resolution from the facts that need no network, with its refusals.
2. The runtime: preflight across interfaces, waves, per-interface planning, fan-out and drains, the stop rules and
   the run outcome.
3. Order from the schemas' relationships, cycles and `after:`.
4. The generic `ddms` route, the `target.ddms` catalog and Register discovery.
5. Records that wait: the reference table, its migration, the hold and the release.
6. `datasetCollection` and the composed routes.
7. The GUI, API, CLI and lineage views of sources and interfaces.
8. The sample estate, the documentation and the test matrix.

## 14. Open questions

1. **Which DDMSs are in scope?** Answered: every DDMS. Each has its contract pinned in `osdu/specs` before its route
   type is built ([osdu-coverage-plan.md](osdu-coverage-plan.md)).
2. **Is the Register service's DDMS registry filled in your deployments?** If not, `target.ddms` is the way DDMSs
   are declared.
3. **References to records outside the ledger:** trust them (the default here) or check them in storage before
   sending?
4. **Manifests too large to send inline:** OSDU documents a manifest stored as a dataset and the workflow started
   with its record id. It needs its specification before it is designed.
5. **The name `interfaces`:** kept.
