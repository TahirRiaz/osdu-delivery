# OSDU Delivery documentation

OSDU Delivery extends SQLFlow, and its documentation is the reference corpus in [reference/](reference/README.md), in
SQLFlow's format: [cli/](reference/cli/) (the `sqlflow` verbs the extension adds and what it adds to SQLFlow's),
[flow/](reference/flow/) (the flow kinds, routes, mappings and dictionaries, key by key),
[concepts/](reference/concepts/) (the ledger, record states, change detection, partitions, the cache, the GUI and the
API) and [guides/](reference/guides/) (tasks end to end).
SQLFlow's own corpus ([../../sqlflow/docs/reference/](../../sqlflow/docs/reference/README.md)) documents the platform
underneath. Beside the corpus are the decision records ([decisions/](decisions/README.md)) and the key census of every
OSDU document ([census/](census/README.md)).

## Kept as history

These are the design records and plans the system was built from. They are not maintained as reference: where one
disagrees with the code or the reference pages, the code and the reference pages win.

- [design.md](design.md): the design, whose section numbers the code still cites.
- The plans: [atomic-delivery-plan.md](atomic-delivery-plan.md), [cache-lookups-plan.md](cache-lookups-plan.md),
  [dimension-plan.md](dimension-plan.md), [inventory-plan.md](inventory-plan.md),
  [reversal-plan.md](reversal-plan.md) and [validation-plan.md](validation-plan.md).
- [osdu-testing.md](osdu-testing.md) (the live test waves) and [test-matrix.md](test-matrix.md) (which suite proves
  what).
- [walkthrough/](walkthrough/): the WellLog 1.4.0 schema laid out section by section, and how each section is filled.
- The designs in the repository's [docs/](../../docs/): [assertions-design.md](../../docs/assertions-design.md),
  [interfaces-design.md](../../docs/interfaces-design.md), [lineage-design.md](../../docs/lineage-design.md),
  [partitions-design.md](../../docs/partitions-design.md) and [stage4-design.md](../../docs/stage4-design.md).
