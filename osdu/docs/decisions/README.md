# Decision records

The design left these decisions open. This implementation had to choose for each of them to ship working
code. Every choice below is recorded with its status so the owning teams can confirm or overturn it; the code
isolates each one so overturning it is a contained change.

| # | Decision | Status |
| --- | --- | --- |
| [0001](0001-delivery-grain.md) | Delivery grain is (source project, log id). | Proposed |
| [0002](0002-client-supplied-ids.md) | Deterministic, client-supplied OSDU ids. | Proposed, needs partition confirmation |
| [0003](0003-rendering-location.md) | Rendering runs in the delivery service. | Proposed |
| [0004](0004-preserved-keys.md) | `Datasets`, `DDMSDatasets`, `ExtensionProperties` are OSDU's and are preserved. | Proposed |
| [0005](0005-ledger-retention.md) | Attempts are pruned by age, keeping the latest per record. | Proposed |
| [0006](0006-work-batches.md) | Rendered documents live in work batch files, not in the ledger; large submissions fan out. | Proposed |
| [0007](0007-manifest-dataset-ids.md) | Manifest datasets take ids derived from their record's id. | Proposed |
| [0008](0008-retrieval-lands-raw-records.md) | The retrieval kind lands records as OSDU holds them. | Proposed |
| [0009](0009-searched-references.md) | References to business data are searched for on the platform, not cached. | Proposed |
| [0010](0010-assertion-flows-read-only.md) | Assertion flows read OSDU, check themselves against templates, and keep whole reports. | Proposed |
| [0011](0011-dimension-flows.md) | Dimension flows read every distinct value by ranges, and keep each key beside the value a person picks. | Proposed |
| [0012](0012-atomic-delivery.md) | Every delivery is a unit of work that completes or is undone, and the ledger names every id it minted. | Proposed |
| [0013](0013-inventory-flows.md) | Inventory flows read what OSDU serves and compare it with every ledger of the partition. | Proposed |
| [0014](0014-inventory-removals.md) | An inventory flow may remove the orphan, stale and forgotten ids it found, when its document allows it. | Proposed |
| [0015](0015-lost-records.md) | A record OSDU lost is delivered again under its own id, and under a new one only when an operator asks. | Proposed |
| [0016](0016-osdu-order.md) | OSDU's relationships order a repository's waves, and dependencies between schedules are shown, not enforced. | Proposed |
| [0017](0017-deliveries-keep-cache-current.md) | A delivery keeps the cache types of its kind current as its run ends. | Proposed |

Not decided here, and still open: storage access grants, Change Data Feed, library ownership between
osdu-client and osdu-csharp-client, and whether the other packages adopt the same contract.
