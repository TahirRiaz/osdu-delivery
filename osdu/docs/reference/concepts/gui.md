---
id: delivery-concept-gui
title: "The OSDU pages of the workbench: what each page and tab shows, and its actions"
type: concept
summary: "The OSDU menu the SQLFlow workbench gains (Ledger, In OSDU, Build, Setup), each page's tabs and buttons, the flow and run page additions, and who may use them."
keywords:
  - gui
  - workbench
  - osdu menu
  - delivery page
  - records page
  - audit trail
  - record page
  - flow page tabs
  - removal dialog
  - partition picker
  - cache page
  - mapping builder
  - templates page
  - tests board
related:
  - concept-control-plane
  - concept-authentication-and-identity
  - delivery-concept-explorer
  - delivery-concept-search-terms
  - delivery-concept-ledger
  - delivery-concept-removal-and-reversal
  - delivery-concept-partition-cache
  - delivery-guide-operations-runbook
sourceRefs:
  - osdu/gui/src/module.tsx
  - osdu/gui/src/features/delivery/PartitionSwitcher.tsx
  - osdu/gui/src/features/delivery/DeliveryOverviewPage.tsx
  - osdu/gui/src/features/delivery/DeliveryRecordsPage.tsx
  - osdu/gui/src/features/delivery/DeliveryActivityPage.tsx
  - osdu/gui/src/features/delivery/DeliveryActivitySheet.tsx
  - osdu/gui/src/features/delivery/DeliveryRecordPage.tsx
  - osdu/gui/src/features/delivery/RecordJourney.tsx
  - osdu/gui/src/features/delivery/RecordSourceTab.tsx
  - osdu/gui/src/features/delivery/RecordRenderTab.tsx
  - osdu/gui/src/features/delivery/RecordOsduView.tsx
  - osdu/gui/src/features/delivery/RecordArtifacts.tsx
  - osdu/gui/src/features/delivery/DeliverySubmissionPage.tsx
  - osdu/gui/src/features/delivery/DeliveryFlowPanel.tsx
  - osdu/gui/src/features/delivery/DeliveryIssuesPanel.tsx
  - osdu/gui/src/features/delivery/DeliveryPreviewPanel.tsx
  - osdu/gui/src/features/delivery/DeliveryOpenUndos.tsx
  - osdu/gui/src/features/delivery/RemovalDialog.tsx
  - osdu/gui/src/features/delivery/DeleteLedgerDialog.tsx
  - osdu/gui/src/features/delivery/RedeliverDialog.tsx
  - osdu/gui/src/features/delivery/ReverseDialog.tsx
  - osdu/gui/src/features/delivery/DeliveryRunHeader.tsx
  - osdu/gui/src/features/delivery/DeliveryTriggerFields.tsx
  - osdu/gui/src/features/delivery/RetrievalFlowPanel.tsx
  - osdu/gui/src/features/delivery/DeliveryCacheFlowVersions.tsx
  - osdu/gui/src/features/delivery/assertions/AssertionBoard.tsx
  - osdu/gui/src/features/delivery/assertions/AssertionBoardGrid.tsx
  - osdu/gui/src/features/delivery/assertions/AssertionTestSheet.tsx
  - osdu/gui/src/features/delivery/assertions/AssertionReportPage.tsx
  - osdu/gui/src/features/delivery/inventories/InventoryReport.tsx
  - osdu/gui/src/features/delivery/dimensions/DeliveryDimensionsPage.tsx
  - osdu/gui/src/features/delivery/dimensions/DimensionWorkspace.tsx
  - osdu/gui/src/features/delivery/DeliveryDocumentsPage.tsx
  - osdu/gui/src/features/delivery/TemplateVariableExplorer.tsx
  - osdu/gui/src/features/delivery/MappingBuilderPage.tsx
  - osdu/gui/src/features/delivery/TemplatesPage.tsx
  - osdu/gui/src/features/delivery/TemplatesBrowseTab.tsx
  - osdu/gui/src/features/delivery/DeliveryCachePage.tsx
  - osdu/gui/src/features/delivery/DeliveryCacheApprovals.tsx
  - osdu/gui/src/features/delivery/DeliveryPartitionsPage.tsx
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryPartitionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryConfigEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryDimensionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/RecordSearchContributor.cs
  - sqlflow/src/SqlFlow.ControlPlane/Hosting/ControlPlaneHost.cs
  - sqlflow/gui/src/auth/AuthContext.tsx
---

# The OSDU pages of the workbench

OSDU Delivery's GUI is SQLFlow's workbench with the OSDU Delivery module loaded. The workbench itself (Pipelines, runs,
schedules, lineage, the catalog, the trigger dialog, the assistant) is SQLFlow's and is documented there: the
[control plane](../../../../sqlflow/docs/reference/concepts/control-plane.md) behind it, [sign-in and
scopes](../../../../sqlflow/docs/reference/concepts/authentication-and-identity.md), and the
[assistant](../../../../sqlflow/docs/reference/guides/chat-assistant.md). This page covers what the module adds:

- the product name, "OSDU Delivery, powered by SQLFlow", on the login page and the title bar;
- an **OSDU** menu group of its own, straight after Workspace, with the pages below;
- a **Partition** picker in the title bar, which every OSDU page reads in;
- tabs and header actions on the pipeline and run pages of the six kinds it adds (delivery, retrieval, cache, assertion,
  dimension, inventory), and their fields in the trigger dialog;
- a **records** category in the workbench's search.

No flow is configured in the OSDU group. Flows of every kind, these included, are defined, synced and scheduled in
Workspace, and a flow's own page is reached through **Pipelines** like any other. On these kinds' pipeline pages,
SQLFlow's Transforms, Files and Definition tabs are hidden, and on their run pages its Files, Statements, Surrogate keys,
Assertions and Health metrics tabs.

## Who may use what

The control plane puts every OSDU endpoint in one of SQLFlow's authorization groups:

| Group | What it covers |
| --- | --- |
| read | every page's reads: the ledger, records, submissions, the audit trail, mappings, templates, caches, partitions, search terms, boards and reports |
| operate | anything that runs or reaches OSDU: the trigger dialog, release, redeliver, verify, sync, probe, read back, render, preview, value checks, removals, Delete ledger, reversals, cache approvals, inventory removals, every explorer read, syncing the data definitions |
| author | saving and deleting templates, refining, deleting and restoring search terms, proposing a mapping from the builder (SQLFlow's proposal endpoint) |
| admin | registering, describing, making default and removing partitions; setting and removing central configuration; removing a dimension its flow no longer declares; pruning the ledger |

SQLFlow resolves `read`, `operate` and `author` to "signed in", and only `admin` consults the token's scopes, in the
control plane and in the GUI alike. So **any signed-in user can use every page and action here except the admin ones**,
which the GUI shows only to an admin (see [authentication and identity](authentication-and-identity.md)). Every
intervention is recorded in the ledger under the name of who asked.

## The partition picker

The title bar's **Partition** chip names the OSDU partition every OSDU page is read in, with a count beside it when
cache changes wait for a decision there. Its menu lists each partition: **default**, **not registered** (a flow
hard-codes it, or something is kept under it from before it was removed), the cache changes waiting, its cache's
current version, and how many delivery flows deliver there; **Partitions** opens the registry. Every OSDU request
carries the choice (as the `X-Osdu-Partition` header), so a flow that names its partitions is read in the picked one,
and a link that names a partition (`?partition=`) makes it the title bar's. See [partitions](partitions.md).

## The OSDU menu

| Section | Entry | Page |
| --- | --- | --- |
| Ledger | Delivery | `/delivery` |
| | Records | `/delivery/records` |
| | Audit trail | `/delivery/activity` |
| In OSDU | Explorer | `/delivery/explorer` |
| | Tests | `/delivery/assertions` |
| | Inventories | `/delivery/inventories` |
| | Dimensions | `/delivery/dimensions` |
| Build | Mappings | `/delivery/documents` (and the Mapping builder, `/delivery/mappings/build`) |
| | Templates | `/delivery/templates` |
| | Cache | `/delivery/cache` |
| Setup | Partitions | `/delivery/partitions` |
| | Search terms | `/delivery/search-terms` |

**Ledger** is what the delivery system sent and keeps; **In OSDU** is what OSDU holds, read live by the explorer or by
the assertion, inventory and dimension flows; **Build** is what a delivered document is built from; **Setup** is what is
kept in the GUI rather than in a flow. The command palette lists the same entries, headed `OSDU · Ledger`, `OSDU · In
OSDU` and so on.

## Ledger

### Delivery

Every delivery flow as a card, counted in the title bar's partition: a delivered bar, **Records**, **Delivered**,
**Pending**, **Held**, **Failed** and **Last 24h**, with the last delivery, the last verify and the last submission. A
flow that delivers only to other partitions says where, without counts. The header line totals what the flows hold
(delivered of total, pending, held, failed, drifted). **Find a record** with **Look up** opens Records looked up for
what is typed. A card opens the flow's Delivery tab. The cards refresh every 15 seconds.

### Records

Where an operator starts from what they hold rather than from a flow. With nothing typed, the page lists the latest
records the delivery system took in or sent, newest first, refreshing every ten seconds. Typed, it lists the records any
of whose values start with the term, across every flow: an identity the mapping declares (`dataset.identity`), the
source key or one of its key columns, a word of the label, the OSDU id, the delivery key (which lands on one record),
or the ingestion file. Each hit says which value matched. A status picker (`pending`, `waiting`, `delivering`,
`delivered`, `held`, `failed`, `deleted`, `reverted`) and a flow picker narrow both. A row shows the status, the record
(label, source key, ingestion file and row), the flow, when it last changed and was last delivered, and its OSDU type,
a link to the record's OSDU tab; a row opens the record's page. The term, status and flow are in the address, so a
lookup is a link.

### Audit trail

Every run and intervention across flows, newest first: when, flow, partition, **Action**, **By** (a schedule, a person,
the command line), **Result** and **Target** (the record, else the submission, else the run). It narrows by actor, flow,
action (`deliver`, `intake`, `drain`, `verify`, `probe`, `sync`, `release`, `redeliver`, `rerender`, `delete`,
`restore-previous`, `purge`, `reverse`, `undo`, `delete-ledger`, `remove-dimension`, `inventory-remove`) and outcome. Runs
that changed nothing are left out until **Show idle runs**. An entry opens on its ids, **Parameters**, its captured
**Log** (downloadable), and for a `deliver`, `replan`, `drain` or `intake` run that has ended, **Reverse this run**; a
`reverse` entry shows its **Reversal**.

### A record's page

`/delivery/records/{flowId}/{key}`, opened from any records list.

**The header** names the record (its label, status and a blocked mark), its **osdu** id, **flow**, **partition** and
**submission** as chips, and the one situation its state calls for (held or failed with the error and next try, waiting
for another record, under a lease, blocked, removed, records waiting on this one). Its buttons:

| Button | What it does |
| --- | --- |
| **Sync timeline** | reads the record's row from its ingestion table and consolidates the ledger with it; nothing is sent |
| **Verify** | queues a verify run for this record, comparing what OSDU holds with the ledger |
| **Redeliver** | brings the record up to date from its current row, or sends it again (the Redeliver dialog) |
| **Release** | while blocked: lets the record be sent again |
| **Send without waiting** | while waiting: sends it on its next claim without waiting for the record it refers to |
| **Remove** | opens the removal dialog for this record |

**The milestones** under it: **From file**, **Loaded**, **Last change**, **Delivered**, **Verified**, and **Removed**
for a record that was.

**The tabs:**

- **Timeline**: what happened to the record, newest first, in chapters (each started by a row arriving, changing or
  going, or by someone's request), on three lanes (**Source**, **Ledger**, **OSDU**); every try with what OSDU answered,
  and every request with who made it. It narrows to one lane or to the requests people made.
- **Source**: where the row came from (ingestion file and row, when received, source key, delivery key, mapping, key
  columns with a copy of the key as the ledger holds it), a newer row a waiting document is built from, and a read of the
  rows as the ingestion tables hold them now, with the flow's own connection.
- **Render**: **Render** builds the record from its current row with the mapping and the cache as they are now, and reads
  what OSDU holds, at once; nothing is sent. It shows the mapping and cache versions used beside those that rendered what
  OSDU holds, the route, what the next run would do, why a delivery would hold the record, the preflight's warnings, and
  the document **Beside OSDU** (the differences) or **Whole document**.
- **OSDU**: **Read** reads the record as OSDU holds it through the flow's route, in the record inspector the explorer uses
  (outline, fields or JSON, versions and **Compare**, linked records, Validation). Icons open it in the explorer, or in a
  window of its own (`/delivery/records/{flowId}/{key}/osdu`).
- **Artifacts**: what deliveries of the record created in OSDU, with each one's state, and the ones an undo still has to
  take; the header says so while an unfinished delivery left something.

### A submission's page

`/delivery/submissions/{id}`: what the submission read (the ingestion table, connection, change window or the whole
scope), its counts, the runs that carried it, and tabs **Attempts**, **Batches**, **Parameters** and **Render context**.
**Records it delivered** and **Records it last planned** open the flow's Records tab narrowed to each set.
**Reverse this submission** opens the reverse dialog while the submission has anything to reverse; once asked, the
reversal's card shows its state, its records by outcome and its runs.

### The dialogs

- **Remove** (the removal dialog): names the target first (flow, endpoint, route, data partition), then **In OSDU**, one
  choice of **Remove the record** (reversible: it stops resolving, every version kept), **Restore the previous version**,
  **Purge earlier versions**, **Purge everything**, or **Leave as it is** (for records already removed), each with the
  call it makes and whether it can be undone; then **In the ledger**, **Delete from the ledger**, offered with a choice
  that leaves the records out of OSDU. A permanent choice, or deleting from the ledger, asks for the data partition to be
  typed back. See [removal and reversal](removal-and-reversal.md).
- **Delete ledger** (a flow's Delivery tab): removes every record of every interface from OSDU, reversibly, then deletes
  the ledgers whole; the next run reads every row and delivers each as a new record. The partition is typed back first.
- **Redeliver**: **Bring up to date** renders the records again and sends only what renders differently, showing first
  what that would send; **Send again** sends them whatever their hashes say, **Everything** or one part (the record,
  files, bulk data, the workflow run, or every part of the payload). With **Queue a deliver run now** ticked, a run takes
  the records at once; otherwise the flow's next run does.
- **Reverse this run / submission**: says how many records it delivered and what reversing would do with each (what it
  created is removed reversibly, what it updated gets back the version OSDU held before, nothing is purged), then queues
  the reverse run. Every record put back is blocked until its source changes or it is released.

## A delivery flow's page

A delivery flow's pipeline page (Pipelines) gains five tabs, Delivery first. A source that delivers several interfaces
is read one interface at a time, through an interface picker whose choice is in the address; a flow that names its
partitions is read in the title bar's partition.

- **Delivery**: the source's counts and status bar, with **Probe target**, **Release blocked (N)**, **Redeliver**,
  **Sync timelines**, **Undo unfinished** (runs the `undo` operation now rather than at the end of the next deliver run)
  and **Delete ledger**; the last submission; for a source, the **Interfaces** in the order a run takes them (wave, route,
  what each waits for, its counts); the target probe's answer; and unfinished deliveries left to undo.
- **Records**: the ledger's records, searched by delivery key, label, source key or OSDU id, with a status picker,
  **Drifted only** and **Match anywhere**; links from elsewhere narrow it to one run (`?run=`), what a submission last
  planned (`?submission=`) or delivered (`?delivered=`), or one issue (`?issue=`). Rows tick; the selection bar offers
  **Select all N matching**, **Redeliver**, **Sync timelines** and **Remove**.
- **Issues**: the blocked records grouped by the issue that keeps them blocked, each a **Set error** or **Row errors**,
  with its records and when it last changed. The issue picked shows samples, each with **Check** (opens it on its Render
  tab) and **Try** (releases it alone and queues a deliver run), the files its records came from, **Records** (the
  Records tab narrowed to it) and **Release N**, which can queue a deliver run straight after.
- **Submissions**: every submission with what it read and its counts; a row opens its page.
- **Preview**: **Preview a record** renders one record exactly as a delivery would and sends nothing: the first record of
  the scope, or the one a key names, with a field for each parameter the flow declares. The answer shows what the next run
  would do, the document, the route's requests, the payload files, the records it refers to and its source rows, and
  **Download JSON**.

A parameter the flow's `source.record.scope` binds to a column lists the values that column holds, with their row counts,
wherever a scope is given values (the Preview tab, the value check, the trigger dialog).

## Runs and the trigger dialog

The trigger dialog of these kinds adds the **Partition** a run writes to (the title bar's), **Flow parameters**, and per
operation: **Force** and **Send again** (Nothing, What renders differently, or a part) for a deliver run, **Redeliver
these records** (delivery keys), a submission to work on or drain, and the **Interfaces** of a source a run takes. See
[running an OSDU flow](../cli/run.md) for the operations.

A run page gains, by kind:

| Kind | Header actions | Counts |
| --- | --- | --- |
| delivery | **Records of this run**; **Reverse this run** once a `deliver`, `replan`, `drain` or `intake` run has ended | Planned, Delivered, Held, Failed, Unchanged, Waiting; the submission as a chip |
| cache | **Open the cache** | |
| assertion | **Open the report** | Tests, Passed, Failed, Warned, Errored, Skipped |
| dimension | **Open dimensions** | Built, Failed, Skipped, Values, Keys |
| inventory | **Open inventories** | Inventories, Would read, Completed, Failed, Listed, Raised |

The other kinds' pipeline pages gain one tab each, which they open on: a retrieval flow **Retrievals** (every run with
its window, location, counts and outcome); a cache flow **Cache versions** (the partition it fills and every version of
its cache, with a **Cache page** link); an assertion flow **Tests**, **History** (the tests against recent runs, each with
its pass rate and how often it flipped) and **Reports**; a dimension flow **Dimensions**; an inventory flow
**Inventories**.

## In OSDU

- **Explorer**: what the partition holds, read live from OSDU. See [the explorer](explorer.md).
- **Tests**: every assertion flow's tests in the partition, one line per flow, what needs a look first. A strip counts
  **Failing**, **Errored**, **Warned**, **Not run**, **Passing**, **Unfit** and **Changed**, each a filter; a search and
  **Types** and **Tags** pickers narrow further. A flow runs every test, or **Run N** picked tests, through the trigger
  dialog. A test opens in a sheet with **Latest report** and **Run**, and tabs **Checks** and **Definition**. A report
  (`/delivery/assertions/runs/{n}`) shows one run's tests by outcome, with **Run the N tests not passing again**,
  **Run again**, and the report as HTML, Markdown, JSON or JUnit XML. See [assertion flows](../flow/assertion.md).
- **Inventories**: every inventory flow's inventories in the partition (kind, findings raised, when reconciled). One
  opens its report: the findings (Orphan, Foreign, Stale, Forgotten and the rest), tabs **Ids**, **Runs** and
  **Removals**, **Run pipeline**, **Export the ids as CSV**, and **Remove** for picked ids where the flow allows it,
  through the removal dialog. An id opens as OSDU holds it in the workbench's bottom panel. See
  [inventory flows](../flow/inventory.md).
- **Dimensions**: a strip of facts (dimensions, values, what needs a look, when last built), then each dimension flow's
  dimensions as cards, with **Run pipeline** per flow and **Build a search**. One dimension opens on the page's width
  with **Export** (the table, values or keys as CSV or JSON Lines), **Run pipeline**, and tabs **Table**, **Values**,
  **Keys**, **Changes**, **Builds** and **Definition**. A dimension its flow no longer declares can be removed for good
  by an admin with **Remove**. See [dimension flows](../flow/dimension.md).

## Build

### Mappings

The mapping documents the repositories hold, as the last sync found them, filtered by repository, with **New mapping**
(opens the builder). A mapping opens in a sheet with the template version it pins (a link to Templates), **Open in
builder**, and three tabs:

- **Properties**: the template the mapping pins as the record's tree, beside the selected attribute. The views
  **Filled**, **Missing**, **Unfilled**, **Failing** (after a data check) and **All** each carry a count; **View** adds
  **Required**, **Minted** and **Nested**. The selected attribute reads in **Filled by**, **Data** and **Schema**. The
  data check line picks a flow that renders with the mapping, its scope's values and how many rows, and **Check all
  attributes**; an attribute's **Check values** checks it alone. See [writing a mapping](../guides/writing-a-mapping.md).
- **YAML**: the document as written.
- **Record shape**: the record the mapping renders, placeholders in angle brackets.

### Mapping builder

Opened from Mappings (`/delivery/mappings/build`, or `?mappingId=` for a synced mapping). **Setup** picks the
**Repository**, the **Partition cache** the mapping is checked against, the saved **Template**, and the mapping's
**Name**, **Mapping version**, **Source system** and **Description**, then **Start mapping**. **Dataset** sets the **Key
columns**, how the **OSDU id** is made (from the delivery key, or the key's values) and the **Label**. Every template
variable is listed, each filled as a **Dataset column**, **Repeat child rows**, **Cache**, **Lookup**, **Platform
search**, **Expression**, **Static value**, **First value of**, **List of values** or **Object**, or left **Not
filled**; the YAML and its check against the template and the cache's current version follow every edit. **Copy YAML**
takes it; **Propose to repository** opens a pull request through SQLFlow's proposal endpoint (**Open pull request**);
**Start over** discards the draft. A mapping that cannot be opened says why.

### Templates

OSDU record schemas saved as templates. Three tabs:

- **Saved**: the saved versions, filtered by kind, version, origin or who saved it, each with where it came from and how
  many mappings pin it. A version opens laid out as a template; **Delete** is offered only while no synced mapping pins
  it.
- **Browse OSDU**: the OSDU data definitions, the Open Group's public repository of OSDU schemas, by **Release**,
  searched by kind, with **Every version**, **Compare versions**, **Save template**, and **Sync with the repository**,
  which reads the release list again and downloads the release in view.
- **Import file**: a schema file or pasted JSON, with **Preview** and **Save template**.

See [templates](templates.md).

### Cache

The cache of the title bar's partition. The header names the cache flow files that fill it, with **Cache files** (opens
Pipelines filtered to them) and **Refresh now** (the trigger dialog on a cache flow's refresh; a menu when several flows
fill the partition). A summary row gives the **Current version** with the flow that wrote it, its records and types, and
whether changes wait for approval; a banner with **Review changes** shows while any do. Four tabs:

- **Records**: the cached records at the version being read, by type, searched over every value, with a version picker.
- **History**: every version and what it changed; a version raises its changes in the bottom panel.
- **Deliveries**: **Updated by the cache**, by state (**Missing from cache**, **Waiting for approval**, **Approved**,
  **Rolling out**, **Rolled out**, **Rejected**, **All changes**); a waiting change has **Approve** and **Reject**, alone
  or for the selection.
- **Setup**: how the cache is filled, as a tree of the partition, its cache flows and their types, the partition's OSDU
  feature flags, and how a mapping reads the cache. Read-only: the setup lives in the cache flow files.

See [the partition cache](partition-cache.md).

## Setup

- **Partitions**: the registry. Each partition with its description, whether it is the default, its cache and the flows
  that serve it. An admin sees **Register partition**, **Make default**, **Describe** and **Remove**; removing deletes
  nothing kept under the partition (caches, ledgers and runs stay). See [partitions](partitions.md).
- **Search terms**: the source columns the explorer searches by. See [search terms](search-terms.md).

## Records in the workbench's search

The module adds a **records** category to the workbench's search: a delivery key lands on its record, and an OSDU id, a
source key, a label or an ingestion file name lists the records that start with it, across every flow of the title bar's
partition, from the ledger's indexed lookup, the same one the Records page reads.
