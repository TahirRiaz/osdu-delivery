# 0006: Rendered documents live in work batch files, not in the ledger

Status: proposed. Design reference: sections 16.1, 16.2 and 16.4.

## Context

A drop can hold millions of rows. Holding the rendered documents on the ledger's record rows made every
intake a write of the whole drop into SQL Server and every drain a read of it back, and it put the intake's
memory at the mercy of the drop's size.

## Decision

- The intake streams the drop (a merge join for partitioned drops, a disk-backed hash spill otherwise) through
  a bounded rendering pipeline and writes the rendered documents to JSON Lines work batch files under the
  flow's work location (`source.work`, default `.work` under the drop), `reliability.batchRecords` documents
  per file.
- The record row carries the batch number and the document's byte range (`WorkBatch`, `PendingDocumentRef`);
  `delivery.WorkBatch` carries one row per file with its status, lease and counts.
- A drain leases a whole batch, reads the documents by range, and hands the protocol up to `batchSize`
  records per request. Completion is bulk (a table-valued merge on SQL Server).
- A submission above `reliability.fanOutMinRecords` on a flow with `reliability.fanOut` spreads its intake
  (by drop partition) and its drains (by batch) over member runs across the fleet, as one run family.

## Consequences

- Intake memory is bounded by the pipeline's capacity, not the drop; the ledger holds pointers, not documents.
- The work location needs write access from every node that runs the flow, and its files are part of the
  submission's evidence until the submission completes.
- A record's pending document is read from storage by the nodes; the API and the GUI show its reference and
  batch, not its bytes.
