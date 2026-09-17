# osdu/

Everything OSDU Delivery adds to SQLFlow lives in this folder. `sqlflow/` next to it is the engine; nothing here is copied
from SQLFlow, and `sqlflow/` only ever gains generic extension points, never OSDU code.

## Where the code came from

The code was copied from the previous implementation, `D:\Projects\eq\src\osdu-delivery` at commit `7377609`, taken from
the committed tree (no build output, no local files).

| Here | From |
| --- | --- |
| `src/SqlFlow.Delivery/` | `src/SqlFlow.Delivery/`: the delivery flow, retrieval and cache kinds, ledger, protocols, rendering, templates and mapping builder, OSDU cache, worker, verifier, removal, fan-out |
| `src/SqlFlow.Delivery.Data/DeliveryEntities.cs` | `src/SqlFlow.Catalog/DeliveryEntities.cs`: the ledger's entity model, to become `OsduDbContext` in the `osdu` schema |
| `src/SqlFlow.Delivery.ControlPlane/` | `src/SqlFlow.ControlPlane/Api/DeliveryEndpoints.cs`, `DeliveryTemplateEndpoints.cs`, `Background/CacheUpdateRolloutService.cs`, `Background/DataDefinitionsWarmupService.cs` |
| `src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs` | `src/SqlFlow.Cli/DeliveryVerbs.cs`: the `check`, `cache` and `template` verbs |
| `gui/src/features/delivery/`, `gui/src/api/delivery.ts` | The OSDU GUI pages and their API client |
| `gui/e2e/` | The OSDU specs (seed, runs, pipelines, cache, templates and mapping builder) with the e2e setup and helpers |
| `tests/SqlFlow.Delivery.Tests/` | The delivery domain suites |
| `tests/SqlFlow.Delivery.ControlPlane.Tests/` | The module, plan run, record origin and template API suites, and the sample estate they use |
| `docs/` | `docs/delivery/` and `docs/reference/cli/delivery.md` |
| `samples/recall-welllog/` | The sample estate: flows, mappings, the cache flow, bundled templates, reference records |

## What was left behind, and why

SQLFlow's own pre-ingestion and ingestion flows replace the way data used to arrive, so these were not copied:

- the drop manifest, its file encodings and the drop reader, and the disk-backed join of drop scopes;
- the CSV and JSON scope readers (`Formats/`), which SQLFlow's file flow provides;
- the replica (`Replica/`), whose typing, schema evolution and upsert SQLFlow's pre-ingestion and ingestion flows provide;
- the SQL Server source extraction into drops (`Engine/SqlSource/`), which an ingestion flow provides;
- inline drops, replica slices and origins, and known-state publishing;
- the drop-off area (endpoints, retention service, GUI page, API tests) and the submit-a-drop dialog;
- the sample drops (`samples/recall-welllog/out/`) and the sample drop generator (`tools/SampleDrop`);
- the tests and documents of all of the above.

## State

The module builds with SQLFlow and its suites pass: the delivery, ledger and route suites against fakes of the OSDU
services, whose every request is checked against the service's pinned contract, and the SQL Server suites against a
disposable database. The routes are tracked in [../docs/osdu-coverage-plan.md](../docs/osdu-coverage-plan.md). Nothing
of this build has run against a live OSDU yet (stage 5 of [../docs/plan.md](../docs/plan.md)); what stands between it
and production, and the order to close it in, is [../docs/go-live-map.md](../docs/go-live-map.md).
[docs/osdu-testing.md](docs/osdu-testing.md) records the live runs of the previous implementation.
