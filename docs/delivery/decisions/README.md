# Decision records

[design.md](../design.md) section 16 lists the decisions the design left open. This implementation had to
choose for each of them to ship working code. Every choice below is recorded with its status so the owning
teams can confirm or overturn it; the code isolates each one so overturning it is a contained change.

| # | Decision | Status |
| --- | --- | --- |
| [0001](0001-delivery-grain.md) | Delivery grain is (source project, log id). | Proposed |
| [0002](0002-client-supplied-ids.md) | Deterministic, client-supplied OSDU ids. | Proposed, needs partition confirmation |
| [0003](0003-rendering-location.md) | Rendering runs in the delivery service. | Proposed |
| [0004](0004-preserved-keys.md) | `Datasets`, `DDMSDatasets`, `ExtensionProperties` are OSDU's and are preserved. | Proposed |
| [0005](0005-ledger-retention.md) | Attempts are pruned by age, keeping the latest per record. | Proposed |

Not decided here, and still open: storage access grants (design 16.4), Change Data Feed (16.7), library
ownership between osdu-client and osdu-csharp-client (16.8), and whether the other packages adopt the same
contract (16.9).
