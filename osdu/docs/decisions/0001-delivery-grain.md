# 0001: Delivery grain is (source project, log id)

Status: proposed. Owner: the well-log domain team. Design reference: sections 5.1 and 17.1.

## Context

The previous pipeline keyed on (wellbore, log source) and collapsed several logging runs of the same type on
one wellbore into one record with first-value aggregates for `LogRun`, `LogActivity`, `LogVersion` and
`WellLogNativeUID`. The source models a log as (source project, log id), which is finer. Everything in the
ledger keys on the grain, so it has to be settled before anything is delivered.

## Decision

The sample mapping's dataset key is `[dataset.source_project, dataset.log_id]`: one OSDU WellLog per Recall log. The
sample mapping also writes both values into the document (`LogSource` and `LogRun`) as provenance.

## Consequences

- No first-value aggregation: every logging run keeps its own metadata and its own curve grid.
- Wellbores with several runs of the same type get several WellLog records in OSDU, each referencing the
  wellbore.
- If the domain decides the coarser grain is right after all, the change is confined to the mapping's
  `dataset.key` and to the prepare step's grouping. Because identity is immutable once a mapping
  version has delivered, that is a new mapping version and a re-key of the estate, which is why this must be
  confirmed before production delivery.

In the template format the grain is the mapping's `dataset.key`: the dataset columns the delivery key, and so the OSDU
id, is derived from, declared apart from the entries that fill the record ([mapping-templates.md](../mapping-templates.md)).
