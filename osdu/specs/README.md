# OSDU contracts

The API contracts the delivery engine is built against, pinned to the commit each was read at. The engine's routes
are designed from these files and from the core service specifications in
`D:\Projects\eq\src\osdu-csharp-client-main\openapi_specs`, never from memory
([docs/osdu-coverage-plan.md](../../docs/osdu-coverage-plan.md)).

| Folder | Service | Contract |
| --- | --- | --- |
| `wellbore-ddms` | Wellbore DDMS | OpenAPI (`openapi.json`) |
| `seismic-ddms` | Seismic DDMS (Seismic Store) | OpenAPI (`openapi.yaml`) |
| `reservoir-ddms` | Reservoir DDMS (Open ETP server) | the ETP 1.2 Avro protocol (`etp-1.2.avpr`), the server's README and its client best practices |
| `rafs-ddms` | Rock and Fluid Samples DDMS | OpenAPI (`openapi.yaml`) |
| `well-delivery-ddms` | Well Delivery DDMS | Swagger (`swagger.yaml`) |
| `production-dspdm` | Production DDMS (DSPDM) | Swagger (`swagger-api.json`) and the business API stubs |
| `production-timeseries` | Production historian time series | OpenAPI of the ingestion and query services |
| `reservoir-management-ddms` | Reservoir Management DDMS | its Postman collection and README (the service generates its OpenAPI at runtime) |
| `eds-dms` | External Data Services DMS | OpenAPI (`openapi.yaml`) |
| `workflows` | Ingestion workflows (manifest ingestion, CSV parser, Energistics parsers, SEG-Y conversions, external data) | each project's README |

`sources.json` lists every file with the OSDU project it came from, the path in that project, the commit and its
date. The files are copied as published, except that an em dash in a text file becomes plain punctuation, because
this repository allows none. Each service's `INTEGRATION.md` is the brief its route type is built from: every call the route makes, with the
contract it comes from.

## Refreshing

`node tools/vendor-osdu-specs.js` downloads every file again at the head of its project's default branch and
rewrites `sources.json`. Review the diff: a changed contract is a change to the route built on it, and ships with that
route's update and its contract tests.
