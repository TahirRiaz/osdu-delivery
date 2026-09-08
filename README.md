# OSDU Delivery

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg)](https://dotnet.microsoft.com/)

**Metadata-driven delivery of subsurface records into an OSDU platform, with every record traceable.**

OSDU Delivery publishes wells, wellbores, well logs, and the other OSDU record kinds from data drops (files
landed in a lake or a folder) into an OSDU instance. A flow document says where the drop is and which mapping
turns its rows into OSDU records; the platform schedules it, runs it on a compute node, and writes every
delivered record, every attempt, and every operator action to a ledger. A record can always be traced,
verified, re-delivered, or deleted from the GUI or the CLI.

It is built on the SQLFlow V3 platform: the control plane API, the SQL Server catalog, the durable run queue
with autoscaling worker pools, git-synced flow repositories, the scheduler, notifications, identity, the CLI,
and the React workbench GUI. The SQL Server ETL engines SQLFlow shipped with were removed; the delivery domain
takes their place.

## Status

The platform is stripped to what OSDU Delivery needs and the delivery domain is grafted onto it: the `delivery`
flow kind (mapping rendering, snapshots, the four OSDU protocols, the streaming intake with work batches and
fan-out, the lease-and-retry worker), the `retrieval` flow kind (OSDU's search index into files on the lake),
the ledger in the catalog, the delivery API, the CLI verbs, and the GUI pages (delivery overview, per-flow
stats, records and submissions, the record page with its history and interventions, the audit trail, mappings
and snapshots). The solution builds warning-free, the three test suites pass, and the GUI builds. What is
not there yet: lineage across delivery flows, and an OSDU emulator for end-to-end tests against a live target.

## Repository layout

| Project | Responsibility |
| --- | --- |
| `src/SqlFlow.Core` | The run model, run artifacts and events, secret references and hygiene, file stores, flow identity |
| `src/SqlFlow.Yaml` | The flow document envelope (schedule, mode, lifecycle) and the `flowType` kind registry |
| `src/SqlFlow.Execution` | Document loading and the execution registry a flow kind plugs into |
| `src/SqlFlow.Catalog` | The EF Core catalog: repos, pipelines, the run queue, schedules, nodes, users, notifications |
| `src/SqlFlow.SourceControl` | Git materialization, history, and proposals (pull requests) |
| `src/SqlFlow.Azure` | Azure credentials, Key Vault references, blob storage |
| `src/SqlFlow.Node` | The compute node: claims queued runs, executes them, streams the trace |
| `src/SqlFlow.ControlPlane` | The API and coordination host: auth, catalog, runs, schedules, sync, notifications |
| `src/SqlFlow.Delivery` | The delivery domain: the `delivery` and `retrieval` flow kinds, mapping rendering, snapshots, drops, the four OSDU protocols, the ledger over the catalog, the worker and the fan-out, the run executors, the compute operations |
| `src/SqlFlow.Cli` | The `sqlflow` command line: validate, check, snapshot, run, worker, db, and the remote verbs |
| `gui/` | The React + TypeScript workbench over the API |
| `samples/recall-welllog` | A complete sample estate: a flow, its mapping, captured snapshots, reference data, a generated drop |
| `tools/SampleDrop` | Generates realistic drops for the samples and the tests |
| `tests/` | The core, control plane and delivery suites |

The `SqlFlow.*` project, namespace, binary, image, and environment-variable names are kept from the platform on
purpose; the product name is OSDU Delivery.

## Build and test

```bash
dotnet build SqlFlow.sln
dotnet test SqlFlow.sln
cd gui && npm ci && npm run build
```

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download) and Node.js. The DB-backed suites need
`SQLFLOW_TEST_DB` pointing at a disposable SQL Server database (the git-ignored `.sqlflow/env` file is the usual
place); they skip when it is unset or unreachable, and the CLI binary suites skip until the CLI has been built.

## Running locally

```bash
# the control plane (API + scheduler + managed git sync), against a catalog database it migrates on start
SQLFLOW_CATALOG_DB="Server=localhost;Database=OsduDelivery;Trusted_Connection=True;TrustServerCertificate=True" \
ControlPlane__Jwt__SigningKey=<32+ byte secret> \
ControlPlane__Jwt__BootstrapSecret=<32+ byte secret> \
ControlPlane__Cors__AllowedOrigins__0=http://localhost:5173 \
dotnet run --project src/SqlFlow.ControlPlane

# a compute node draining the queue
dotnet run --project src/SqlFlow.Cli -- worker --db "$SQLFLOW_CATALOG_DB"

# the GUI
cd gui && npm run dev
```

`dev.bat` wires the same thing up on Windows. See [gui/README.md](gui/README.md) for the GUI and
[deploy/README.md](deploy/README.md) for containers, Azure Container Apps, and Kubernetes.

## Trying the sample

```bash
# the preflight gate over the sample estate: mapping against the schema snapshot, the reference snapshot, the drop
dotnet run --project src/SqlFlow.Cli -- check samples/recall-welllog/flows/recall-welllog.yaml --set logSource=demo

# render the demo drop and report what a delivery would do; nothing is written anywhere
dotnet run --project src/SqlFlow.Cli -- run samples/recall-welllog/flows/recall-welllog.yaml --operation plan --set logSource=demo

# a fresh drop with a metadata edit on one record, then plan it again
dotnet run --project tools/SampleDrop -- samples/recall-welllog/out demo --variant changed
```

Register the `samples/recall-welllog` folder (or a git repository holding it) as a repo source in the GUI to see
the flow, its mappings and snapshots, and to deliver, verify and intervene from the Delivery pages. See
[docs/delivery/README.md](docs/delivery/README.md).

## Documentation

- [docs/delivery/README.md](docs/delivery/README.md): the delivery domain (design, documents, the drop contract, the ledger, protocols, operations).

- [docs/architecture.md](docs/architecture.md): the platform and the delivery domain.
- [docs/environment-variables.md](docs/environment-variables.md): every environment variable and secret reference.
- [docs/reference/](docs/reference/README.md): the platform reference: the CLI verbs, the control plane and its authentication, deployment, notifications.
- [CLAUDE.MD](CLAUDE.MD): the engineering rules the codebase is held to.

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md).

## License

[MIT](LICENSE). OSDU Delivery is a fork of SQLFlow V3, also MIT.
