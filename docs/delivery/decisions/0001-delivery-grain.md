# 0001: Delivery grain is (source project, log id)

Status: proposed. Owner: the well-log domain team. Design reference: section 5.1 and 16.1.

## Context

The previous pipeline keyed on (wellbore, log source) and collapsed several logging runs of the same type on
one wellbore into one record with first-value aggregates for `LogRun`, `LogActivity`, `LogVersion` and
`WellLogNativeUID`. The source models a log as (source project, log id), which is finer. Everything in the
ledger keys on the grain, so it has to be settled before anything is delivered.

## Decision

The sample mapping's natural key is `[data.LogSource, data.LogRun]`, bound to `source_project` and `log_id`:
one OSDU WellLog per Recall log. The renderer carries both values into the document as provenance.

## Consequences

- No first-value aggregation: every logging run keeps its own metadata and its own curve grid.
- Wellbores with several runs of the same type get several WellLog records in OSDU, each referencing the
  wellbore.
- If the domain decides the coarser grain is right after all, the change is confined to the mapping's
  `identity.naturalKey` and to the prepare step's grouping. Because identity is immutable once a mapping
  version has delivered, that is a new mapping version and a re-key of the estate, which is why this must be
  confirmed before production delivery.
