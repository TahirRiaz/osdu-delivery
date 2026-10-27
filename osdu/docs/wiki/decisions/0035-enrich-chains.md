---
id: delivery-wiki-0035-enrich-chains
title: "Decision 0035: a record built in steps is rendered as one chain by its own flow, and sent once for any change"
type: decision
summary: "Why steps extending a record are an enrich block of its delivery flow, rendered with its mapping and written once, not flows writing the record in OSDU."
keywords:
  - enrich
  - fragment
  - "$base"
  - chained rendering
  - one change one write
  - enhance a record
  - multi-step record
  - storage patch
  - record versions
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Rendering/RecordComposition.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.Chain.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/tests/SqlFlow.Delivery.Tests/Enrichment/EnrichDeliveryTests.cs
  - osdu/tests/SqlFlow.Delivery.Tests/EnrichmentGuardTests.cs
referenceRefs:
  - delivery-flow-enrich
  - delivery-flow-mapping-fragment
  - delivery-concept-change-detection
related:
  - delivery-wiki-0004-preserved-keys
  - delivery-wiki-0019-linked-templates
  - delivery-wiki-0023-child-links
updated: 2026-10-27
---
# 0035: A record built in steps is rendered as one chain by its own flow, and sent once for any change

Status: accepted. Documented in [enrich](../../reference/flow/enrich.md) and [fragments](../../reference/flow/mapping-fragment.md).

## Context

A record OSDU holds is often built from more than one source: a well log's header and curves from one table, its original
file from a file register, a grade from a quality system, a list of its children from another table again. Each arrives at
its own time. The owner asked for a way to enhance a record the ledger already holds, without creating another record,
over more than two steps, and set three rules: building a record step by step through OSDU is acceptable for its first
version; once the base structure is there the rendering engine takes over; and one small change must never create several
OSDU versions.

What OSDU and the module already have shaped the choice:

- `IX_Record_ClaimedTargetId` is unique: one OSDU record belongs to one record of one flow, so one ledger record is its
  writer.
- Storage's `PATCH /records` with JSON Patch writes a new version on every data patch, even an unchanged one, adds an
  array item again when a try resumes (`add .../-`), and checks no version; merge patch replaces arrays whole and answers
  with no version (osdu/specs/core/INTEGRATION.md section 2.3). The Wellbore DDMS refuses a record write whose `bulkURI`
  differs from the one it holds.
- `preserveDataKeys` carries whole `data` keys from the stored record into every update, so two writers cannot share one
  key such as `Datasets` ([0004](0004-preserved-keys.md)).
- A change to a child dataset's rows already makes its record a candidate of the run's window and moves its ingestion
  fingerprint, and payload parts already go only when their own hash moved.

## Decision

- **A step is part of the record's delivery flow.** `enrich:` on the delivery document (and on an interface) lists the
  steps; each renders a **fragment** (`fragment: true`), the paths it adds, from rows of its own read as a child dataset
  under its name. A flow without the block is as it was.
- **The chain renders as one.** The flow's mapping renders the record, each step renders over the record as it stands and
  reads it with `$base`, and the record with every step in it is the one document hashed and sent, through the flow's own
  route, under its own lease. A change to any part of the chain renders the whole chain again, so the record is written at
  most once for it.
- **A fragment only adds.** It sets a value where the record holds none, adds items a list does not hold, and goes into
  objects; anything that would take back what is there is a conflict that holds the record. Both operations are
  idempotent, which is what makes rendering the whole chain on every change safe.
- **The render context names every fragment**, its fingerprint, template version and parameters, so a fragment edit
  renders the records again, and the ledger says what rendered every part of a document.
- **The preflight refuses what could never render**: a step writing what another writes, writing under a preserved key,
  reading what nothing before it writes, or reading rows it does not have.
- **A step that holds holds the record.** OSDU keeps the version it has until the record renders whole.

## Alternatives considered

- **A flow kind of its own for a step**, writing the record OSDU holds. Not taken: it needs a second ledger per record and
  a second writer of an id the ledger gives one writer; the owner's next write erases what it added unless every key is
  preserved; storage's patch checks no version and mints one per call; and a change to the original re-runs the first step
  only, so the later steps are stale or each costs a version.
- **Steps that land in OSDU one after another on every change**, each landing planning the next, as `$child` links do
  ([0023](0023-child-links.md)). Not taken for updates: a change costs a version per step. It remains acceptable for a
  record's first version, which the owner allowed; this decision renders every step in the first delivery already.
- **The flow recomposing stored fragments** each step left in the ledger. Not taken: a fragment that read the record would
  be stale when the record changed, which is the case rendering the chain again answers.
- **`preserveDataKeys` for each step's keys.** Not taken: it shares no key between writers and keeps a value only from the
  record OSDU holds, never from what the step would render now.
- **Keeping a held step's last fragment while the rest goes ahead.** Not taken in this release: it needs each step's
  delivered fragment kept in the ledger, which the steps that register files and records need too; until then a held step
  holds the record and nothing is lost.

## Consequences

- One change to a record, its steps' rows, a fragment or the cache values any of them read sends the record once, and a
  change that renders the same record sends nothing; the suite proves it end to end (`EnrichDeliveryTests`).
- A flow without steps keeps its render context, its document hash and its records exactly, which the guard tests pin in
  the tiers on every pull request (`EnrichmentGuardTests`, the document hash taken from the code before steps existed).
- Steps share the flow's source connection, its key, its runs and its schedule; a step owned elsewhere is added by editing
  the flow's document.
- A step adds paths in this release; registering files, datasets or records of its own, and running a workflow or writing
  bulk data, are what a step does not do yet. They go as parts of the same one delivery, so the rule of one write per
  change holds for them, with an id a service mints kept in the record's target state and reused while its file is
  unchanged.
- Adding steps to a flow moves its rules, so its scope is read whole once; records the steps add nothing to render the
  same and are not sent.
