---
id: delivery-decision-0017
title: "Decision 0017: A delivery keeps the cache types of its kind current as its run ends"
type: decision
summary: "Why a delivery run ends by putting what it landed into the cache types declared keepCurrent, read from storage, and how a refresh keeps those records."
keywords:
  - keepcurrent
  - keep the cache current
  - cache step
  - cache update during a schedule
  - refresh one type
related:
  - delivery-concept-partition-cache
  - delivery-flow-cache
  - delivery-concept-lineage
  - delivery-decision-0016
---
# 0017: A delivery keeps the cache types of its kind current as its run ends

Status: proposed. Documented in [the partition cache](../reference/concepts/partition-cache.md#deliveries-keep-a-type-current)
and [the cache flow](../reference/flow/cache.md). Answers what [0016](0016-osdu-order.md) left open.

## Context

A schedule runs one repository's flows in its waves: reference data first, then the records that refer to it. A mapping
resolves its references through the partition's cache, which a cache flow's refresh fills. A schedule that delivered
units and then the wells referring to them rendered the wells against a cache that did not hold the new units yet: the
reference went out unverified, or the record waited for a later refresh and its redelivery.

Running the cache flow between the two deliveries does not close the gap well. A refresh searches every record of each
type it declares, a full sweep to add the few records one run landed, and the search index holds a record only a moment
after it is written, so a refresh straight after the delivery can miss what the delivery just sent.

## Decision

- A cache type of OSDU records may declare `keepCurrent: true`. Every delivery run of a kind it matches, into a partition
  it is built for, ends with a step that reads the records the run landed back from OSDU's storage service by id, keeps
  the paths the partition keeps for the type, and merges them into the partition's cache: a version when anything moved,
  and the changes tagged for the records they reach, as a refresh does.
- The step takes what landed from the ledger and reads the records from storage, not search: storage holds a record once
  its write returns.
- The delivery holds what it added under `delivery:<ledger name>`. Its merge adds and replaces records and removes none.
  A refresh that does not find a record a delivery holds asks storage about it, and lets it go only once storage no
  longer holds it, so neither the index's lag nor a deleted record leaves the cache wrong past the next refresh.
- The step is bounded: a run landing more than 50,000 records reads none and says to run the refresh. Its failure is not
  the run's: what the run delivered stands, its result says what the step could not do, and the next refresh brings the
  records in.
- Not on a lookup table, which its cache flow captures whole, and not beside `query`, which a delivered record need not
  match.
- A delivery of a kind that keeps a type current writes that type in lineage, so the flows reading it run after the
  delivery within its schedule. Nothing waits across schedules ([0016](0016-osdu-order.md)).
- A cache run may name the types it refreshes or plans (payload `types`), so one type can be refreshed without sweeping
  the others.

## Consequences

- A schedule that delivers reference data and then the records referring to it renders the second against the first
  with no refresh in between, once the reference types are declared `keepCurrent`.
- The catalog records the declaration (`[osdu].[CacheDefinition].[KeepCurrent]`, module schema 1.48.0), and the cache's
  membership names deliveries beside cache flows.
- A delivery run's result carries `cacheKept`, its page shows it, and the Cache page marks a type the deliveries keep
  current.
- Each run that lands records of a kept type can write a cache version. It is a capture like any other: a version that
  changes nothing is not written, and retention prunes the rest.
