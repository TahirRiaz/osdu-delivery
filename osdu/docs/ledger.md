# The ledger

The ledger is the system ([design.md](design.md) section 7): the tables in the `osdu` schema that say, per record,
what OSDU holds, what is waiting, and everything that ever happened to it. Everything an operator or a dashboard asks
is answered from here, and every answer is index-backed.

The `osdu` schema is the module's own: its own EF context (`OsduDbContext`), its own migration history and its own
schema version, with no foreign keys into SQLFlow's tables ([architecture.md](architecture.md)). It lives in the
catalog's database, beside SQLFlow's own schema, which is what the shipped deployments do: one metadata database,
one backup, and a run row joined to the attempts it produced by id. Because that join is by id and never by a
foreign key, nothing here depends on the two being in one database, and an estate that has to keep them apart can
give the module a database of its own (`SQLFLOW_OSDU_DB`), which is the only way to separate them on Azure SQL; a
restore then has to bring both to the same instant.
The control plane reaches the schema on the catalog's connection, or through the module's reference when the
estate gave it one; a node, which opens no catalog connection at all, always reaches it through the reference.

## Tables

### `osdu.Submission`: one plan of a flow over its ingestion tables

| Column | Purpose |
| --- | --- |
| `SubmissionId` | Primary key and idempotency key. |
| `FlowId`, `FlowName`, `MappingReference`, `RenderContext` | What produced the run. |
| `ParametersJson`, `RecordCount` | The parameter values the plan read with, and how many records it covered. |
| `WorkLocation`, `BatchCount`, `Slices` | Where the intake wrote its work batches, how many, and how many key slices it cut the work into for its fan-out members. |
| `Kind` | Which selection was read: `incremental` (the rows changed in a window after the scope's watermark), `full` (every row of the scope), or `keys` (named record keys: a record-scoped run, or the records the ledger asked to plan again). |
| `SourceConnection`, `SourceObject` | The ingestion database's connection reference exactly as the flow declares it (never a resolved secret), and the three-part name of the record table read. |
| `WindowFromUtc`, `WindowToUtc` | The `UpdatedDate_DW` window the plan covered. Both null for a keys plan with no window; a full plan records its upper bound. |
| `SourceWindowJson` | What else bounded the read: the child dataset objects, the overlap seconds, the scope values, the slice boundaries and the identity primary key column they are values of (`slicedOn`), the key count, and for a keys plan the key digest. |
| `RunId` | The run that coordinated the submission. |
| `Untracked` | Rows the plan read that carried no complete record key, so nothing could be delivered under them. |
| `Status` | `received`, `planned`, `running`, `completed`, `failed`. |
| `Planned`, `SkippedUnchanged`, `AwaitingApproval`, `SkippedStale`, `UnchangedAtPush`, `Blocked`, `Delivered`, `Held`, `Failed` | Counts scoped to the records this submission touched. `AwaitingApproval` counts the records a cache change waiting for a decision held back: rendered and ready, and not unchanged, so a run says how many wait on an approval instead of hiding them among the records it had no reason to send. `Planned`, `SkippedUnchanged`, `SkippedStale` and `Blocked` describe its latest planning pass: a re-run of the submission plans it again and replaces them. `Delivered` and `UnchangedAtPush` count the distinct records the submission's attempts delivered, or found OSDU already holding at the final hash check, across every pass. `Held` and `Failed` count the records whose last submission this is and that are held or failed now. A re-run that re-sends one record of three already delivered therefore shows 1 planned and 3 delivered; what that run itself did is on the run (`recordsPlanned`, `recordsDelivered`). `SkippedStale` counts rows older than the version delivered or queued. |
| `ReceivedUtc`, `StartedUtc`, `CompletedUtc`, `Error` | Timeline. |

The platform's run row carries the submission too: `Run.SubmissionId` when a run was asked to re-run one, and
`Run.ResultSubmissionId` for the submission a deliver run registered or completed, so a submission page lists
the runs that carried it.

### `osdu.RecordIdentity`: what a record is findable by

One row per value a record is known by, so an operator holding a wellbore id, a well name, a log id, an OSDU id or
the name of the file a record arrived in finds it across every flow without knowing which flow delivered it.

| Column | Purpose |
| --- | --- |
| `Token`, `FlowId`, `DeliveryKey` | Primary key, the token first: a prefix search seeks it, and a record of any flow is reached from the value alone. |
| `Token` | The value folded to upper case, which is what a term is compared against (terms are folded the same way). |
| `Display` | The value as it was read, for showing beside a hit. |
| `Kind` | Where the value came from: `identity` (a column the mapping declares in `dataset.identity`), `key` (the source key or one of its columns), `label` (a word of the rendered label), `osdu` (the id, and its part after the last colon), `file` (the ingestion file). |

A record's rows are written when it is staged, as a set: a staging deletes what the record no longer is and inserts
what it now is, so a row that moved to a new file, a new wellbore id or a renamed label is found by what it is now.
A record contributes at most `RecordIdentityLimits.MaxPerRecord` rows, each at most `MaxTokenLength` characters, so
a record costs a known number of small rows however wide its source row is. Values shorter than `MinTokenLength`
are not stored, because they match too much to be worth an index row; a short *term* still seeks, since the floor is
on what is stored, not on what is typed.

Records a ledger held before this table existed are filled in by a background pass in the control plane
(`RecordIdentityBackfillService`), a page at a time in key order, skipping records the index already holds, so it is
resumable, repeatable and costs nothing once complete. It derives tokens from the record row alone, so a record gains
the identities its mapping declares when it is next staged.

### `osdu.Record`: the current state of one deliverable

| Column | Purpose |
| --- | --- |
| `FlowId`, `DeliveryKey` | Primary key. The flow, and the deterministic key derived from source data. The same row read by several flows is one record per flow ([One source, several flows](#one-source-several-flows)). |
| `SourceKey`, `Label`, `MappingName` | Provenance. `Label` is the mapping's `dataset.label` rendered for the row (a wellbore name, a log name), for search and display only. |
| `RenderContext`, `SourceFingerprint`, `MetadataHash`, `PayloadHash` | What OSDU holds: the gates for tiers 1 and 2. `SourceFingerprint` is the ingestion fingerprint, computed over the record row's `UpdatedDate_DW` and, per child dataset, its row count and newest `UpdatedDate_DW`. |
| `SourceModifiedUtc`, `PayloadModifiedUtc` | The last-modified moment of the source row, and the newest modified time of the payload files, that OSDU's document and payload were built from: the watermarks an incremental run is ordered against. |
| `SourceKeyJson` | The record's key tuple as a JSON array, in `source.record.key` order: what a key-scoped read of the ingestion tables uses. |
| `SourceFileName`, `SourceRowNumber`, `SourceUpdatedUtc` | Where the version OSDU holds came from: the ingestion row's `FileName_DW`, `RowNumber_DW` and `UpdatedDate_DW`. |
| `PendingSourceFileName`, `PendingSourceRowNumber`, `PendingSourceUpdatedUtc` | The same for the queued version, or for the state a held, failed, deleted or reverted record was left in. |
| `SourceInsertedUtc` | When the ingestion table first inserted the record's row (`InsertedDate_DW`), which later changes never move: the row's arrival, as the last plan that read it saw it. Null while the table does not carry the column, or until a plan reads the row. |
| `PlanRequestedUtc` | Set when the ledger asks for the record to be planned again (a redeliver of named records or of every delivered record, a request to bring records up to date, a release with no pending document, a cache rollout); the next run pages these records and plans them as a keys selection, and planning clears it. |
| `TargetId`, `TargetVersion` | The OSDU id and the last known version (the drift handle). |
| `ClaimedTargetId` | The OSDU id the record claimed for its flow when it first queued a document, kept for good. Unique across the ledger: one OSDU record belongs to one flow. Null for a record that was only ever held. |
| `Status` | `pending`, `delivering`, `delivered`, `held`, `failed`, `deleted`, `reverted` (a reversal gave OSDU back the version it held before a run, [Reversals](#reversals)). |
| `Blocked` | Set when the record was held, failed, deleted or reverted and not released since. |
| `ProblemHash` | While the record is blocked, held or failed: the issue that keeps it so ([Issues](#issues)), the hash of its last error with every part that names the record replaced, which every record refused for the same reason shares. Null for any other record, a removed one included. |
| `ValidationOutcome`, `ValidationProblems`, `ValidatedUtc` | What the last check of the record's document against its schema came to (`valid`, `invalid`, `unverified`), how many problems it found and when ([documents.md](documents.md#validation-before-a-record-is-sent)). Written by the try that checked it; a try that sent the payload alone leaves them as they were. Null until a document of the record was checked. A filtered index counts a flow's records by outcome. |
| `AcceptedMetadataHash` | The metadata hash of the pending document a release accepted as it is: the gate sends that document whatever its verdict says. A release of a blocked record that still holds its rendered document writes it. |
| `ValidationOutcome`, `ValidationProblems`, `ValidatedUtc` | What the last check of the record's document against its schema came to (`valid`, `invalid`, `unverified`), how many problems it found and when ([documents.md](documents.md#validation-before-a-record-is-sent)). Written by the try that checked it; a try that sent the payload alone leaves them as they were. Null until a document of the record was checked. A filtered index counts a flow's records by outcome. |
| `AcceptedMetadataHash` | The metadata hash of the pending document a release accepted as it is: the gate sends that document whatever its verdict says. A release of a blocked record that still holds its rendered document writes it. |
| `LastDeliveredUtc`, `LastVerifiedUtc`, `LastVerifyOutcome` | Custody timestamps. |
| `LeaseOwner` | The token of the lease that holds the record while a worker delivers it ([Leasing](#leasing)); the lease row says when it runs out. |
| `LastSubmissionId`, `AttemptCount`, `NextAttemptUtc`, `LastError` | The pending work's progress; `LastError` is redacted. |
| `Pending*` | The hashes, context, fingerprint and payload location of the work waiting to be delivered. |
| `WorkBatch`, `PendingDocumentRef` | Where the pending rendered document is: the work batch and its `batch:offset:length` range in the batch file. The document itself lives on storage, never here. |
| `PendingStepJson` | The steps an earlier try of the pending work completed, with what the target returned, so the next try resumes after them. |
| `TargetStateJson` | Every value the target returned across the record's deliveries (record id and version, dataset ids, file sources, a workflow run id): what OSDU holds for the record. |

#### Payloads in parts

A route that sends its payload in parts (the composed routes and the workflow route,
[protocols.md](protocols.md#payload-parts)) keeps them in the same columns, so the module's model does not change:

- `PayloadHash` and `PendingPayloadHash` are the SHA-256 of one `role:payload=hash` line per part, so a change to any
  part is a payload change, and a part's folder moving is not.
- `PendingPayloadLocation` is a JSON object, `{"composite":"1","forced":[...],"parts":[{"role","payload","hash","location"}]}`,
  of at most 2000 characters; a plan whose parts do not fit holds the record and names the limit. A part without files
  has no `location` and the hash `none`.
- `TargetStateJson` keeps the hash each part was last delivered with as `payload.<set>`, which is how a delivery knows
  which part moved.
- A redelivery of some parts writes `redeliver:<roles>` (sorted, as in `redeliver:bulk,files`) into `PayloadHash`. The
  next plan sees a payload change and lists those roles as forced, and the payload hash is replaced when the payload
  lands. A redelivery of the payload or of everything clears `PayloadHash` as for any record, and every part is sent
  then, as it is under `change.payloadDetect: always` or `onUnchanged: deliver`, and for a record that never delivered
  its payload.
- A pending payload the worker cannot read as parts (one a later version wrote, or one planned for another route)
  holds the record; a redelivery plans it again.

### `osdu.Attempt`: append-only, one row per delivery try

The record it belongs to (`FlowId`, `DeliveryKey`), worker, start and end, outcome (`delivered`, `skipped`, `failed`, `held`, `deleted`, `historypurged`, `restored`), the
phase delivered (`metadata`, `payload`, `metadata+payload`, `delete`, `purge-history`, `reverse`, `restore-previous`, `none`), the hashes
established, the version returned, the origin of the row it was built from (`SourceFileName`, `SourceRowNumber`,
`SourceUpdatedUtc`, and `SourceDeletedUtc` for the hold of a row the ingestion table marked deleted),
the redacted error (for a held or failed try only: a try that did not fail keeps its note, chunks sent or why nothing
was sent or what a removal took, as `detail` in its result), the platform `RunId` the attempt happened in, the `WorkBatch` it was drained from, and
`ResultJson`: every step the protocol took (name, timing, status, what the target returned, whether an earlier
try had completed it), the values returned, and under `validation` the verdict the gate reached on the try's document:
its outcome, the template kind and content version it was checked against, how many rules it applied, the problems and
the parts it could not check (listed up to a bound and counted exactly), what was found of the records it refers to,
whether a release accepted it, and when ([documents.md](documents.md#validation-before-a-record-is-sent)). Render-time holds are written by the intake with worker
`intake`; deletions by the actor who asked for them.

A delivered try also records, under `replaced`, the version OSDU held of the record when it was sent
(`{"version": 7}`, or `{"version": null}` for a record OSDU did not hold), so what a run replaced is known from its own
attempts however much of the record's history is pruned later. A reversal's tries have the phase `reverse`
([Reversals](#reversals)): `restored` (the version written back, with the hashes and origin of the attempt that
delivered it), `deleted` (a record the source created, removed again), `skipped` (passed over, with why) or `failed`,
each with the reversal's id, its source and its outcome under `reversal` in the result.

The intake also writes the plan decisions that are changes of the record although nothing is sent, so the record's
history holds every change of its row:

| Outcome, phase | When |
| --- | --- |
| `held`, `source-deleted` | The ingestion table marked the record row deleted. `SourceDeletedUtc` is the moment it did. |
| `skipped`, `identical` | The ingestion table changed the row since the version the ledger stands at, delivered or queued, and it renders the document OSDU holds or the one already queued. The record's origin moves to the new row with its fingerprint, so a later plan that renders the same row again (a cache or mapping rollout) writes nothing. |
| `skipped`, `stale` | The row is older than the version the record holds ([Record lifecycle](#record-lifecycle)). |
| `skipped`, `source-missing` | Written by a timeline sync (worker `sync`): the row is gone from the ingestion table, or outside the record's scope. Written once; the record keeps its status. |

An attempt's result names the `correlationId` every OSDU request of that try carried in the `correlation-id` header,
so the attempt can be found in the services' own logs (a removal names the id its chunk's calls carried, with what OSDU
held for the record under `returned`), and the error of a refused or failed request quotes the id the
service answered with. The OpenAPI descriptions do not declare the header; the storage service answers with the id it
is sent, and with one of its own when it is sent none.

### `osdu.WorkBatch`: one file of rendered documents

| Column | Purpose |
| --- | --- |
| `SubmissionId`, `Index` | Primary key: the batch's place in its submission. |
| `FlowId`, `Location`, `RecordCount` | Whose it is, where the JSON Lines file is, how many documents it holds. |
| `Status` | `queued`, `running`, `done`, `failed`. |
| `LeaseOwner`, `RunId` | The token of the lease its drain holds, and the run it is being drained in. |
| `CreatedUtc`, `StartedUtc`, `CompletedUtc` | Timeline. |
| `Delivered`, `Held`, `Failed`, `Retrying`, `Error` | How its drain ended. |

A drain claims the oldest queued batch of the flow (or of one submission) under a lease of its own and marks the
batch's due records with the lease's token; the records' rows point at the batch and their range in its file. A batch
whose drain stopped is recovered with its records once the lease runs out.

### `osdu.Lease`: one claim of a worker

| Column | Purpose |
| --- | --- |
| `Token` | Primary key: the worker's name, a slash and a 32-character id, minted per claim. The records the claim holds carry it in `LeaseOwner`, and so does a claimed batch. |
| `FlowId`, `SubmissionId`, `WorkBatch` | The flow; the submission the claim was limited to; the batch a drain claimed, null for a claim of records outside a running batch. |
| `Owner` | Who holds the lease: the worker that claimed it, or `{machine}/{process}/recovery` once a recovery has taken it over. |
| `RunId`, `AcquiredUtc`, `ExpiresUtc` | The run it was claimed in, when, and when it runs out unless it is renewed. |

A worker renews its one lease row, however many records the lease holds.

### `osdu.RecordEvent`: what a worker learned and its lease has not applied yet

One row per step a try completed and per try that ended, appended while the lease is out and deleted as the lease
applies it ([Leasing](#leasing)).

| Column | Purpose |
| --- | --- |
| `EventId` | Primary key, ever-increasing: the order the events were appended in. |
| `LeaseToken`, `FlowId`, `DeliveryKey` | The lease it was appended under, and the record it concerns. |
| `Kind`, `AtUtc` | `step` (a step completed; `StepJson` holds the steps so far) or `completion` (a try ended), and when. |
| `Status`, `Promote`, `NothingSent`, `NextAttemptUtc`, `Error`, `ProblemHash`, `TargetId`, `TargetVersion`, `TargetStateJson`, `PendingStepJson` | For a completion: how the try settles the record, and for one that holds or fails it, the issue its error names, read when the event is appended. |
| `ValidationOutcome`, `ValidationProblems`, `ValidatedUtc` | For a completion of a try that checked its document: what the check came to, which the record keeps. |
| `ValidationOutcome`, `ValidationProblems`, `ValidatedUtc` | For a completion of a try that checked its document: what the check came to, which the record keeps. |
| `Claim*` | What the try was claimed with (submission, document, render context, fingerprint, origin, hashes), so a completion promotes what the try actually delivered even when newer work was queued meanwhile, and a step is kept only while the record still holds that document. |

A try's attempt is written to `osdu.Attempt` in the transaction that appends its completion, so the record's history
is complete as soon as the try ends.

### `osdu.Retrieval`: one run of a retrieval flow

| Column | Purpose |
| --- | --- |
| `RetrievalId` | Primary key. |
| `FlowId`, `FlowName`, `RunId`, `Actor` | Whose it is, the platform run, who asked. |
| `Kinds`, `Query` | The kinds covered and the query as it ran, window included. |
| `WindowField`, `WindowFrom`, `WindowTo` | The incremental window; `WindowTo` of the last done run is the next run's lower bound. |
| `Location`, `ManifestLocation` | The run's directory on the lake and its manifest. |
| `Status` | `running`, `done`, `failed`, `cancelled`. |
| `Records`, `Files`, `Bytes` | What was written (bytes uncompressed). |
| `StartedUtc`, `CompletedUtc`, `Error` | Timeline and the redacted error. |

### `osdu.AssertionRun` and `osdu.AssertionResult`: the reports of assertion flows

An assertion flow's run keeps its report here ([docs/assertions-design.md](../../docs/assertions-design.md) section 7):
one `AssertionRun` row per run of its tests in a partition, and one `AssertionResult` row per test it ran.

| Column | Purpose |
| --- | --- |
| `AssertionRun.AssertionRunId` | The report's number, unique across partitions: what the report page and a download name. |
| `FlowId`, `FlowName`, `RunId`, `Actor` | The flow's ledger identity in the partition, its name, the platform run, who asked. |
| `Selection` | The tests and tags the run was asked for, as JSON; null when it ran every test. |
| `Status` | `running`, `passed`, `failed`, `errored`, `cancelled`. |
| `Tests`, `Passed`, `Failed`, `Warned`, `Errored`, `Skipped` | How the run's tests came out, written when it closes. |
| `DefinitionsHash` | The hash of the definitions of the tests it ran. |
| `StartedUtc`, `CompletedUtc`, `Error` | Timeline, and the redacted reason a run failed or stopped. |
| `AssertionResult.ResultId`, `AssertionRunId`, `FlowId`, `TestName`, `Kind` | One test's result in one run. |
| `Outcome`, `Severity` | `passed`, `failed`, `warned`, `errored` or `skipped`, and the heaviest severity that failed. |
| `Matched`, `Evaluated`, `Sampled` | What the test's selection matched, the records it held its assertions to, and whether those were a sample. |
| `Assertions`, `FailedAssertions` | How many assertions it held, and how many failed or errored. |
| `DefinitionHash` | The test's definition when it ran: a board shows a result whose hash differs from the test's current one as changed. |
| `DurationMs`, `Error`, `StartedUtc`, `CompletedUtc` | How long it took, and the redacted reason it could not be evaluated. |
| `Detail` | The whole result as JSON (every assertion, what it expected and found, and the records that failed it), at most 1,000,000 characters: a larger one keeps fewer examples, marked as cut back. |

A result is written the moment its test is evaluated, so a report shows a long run's progress and keeps what a stopped run
found. The run registers its ledger in the partition first, with the directory's kind `assertion`, like any ledger.

### `osdu.Dimension`, `osdu.DimensionRun`, `osdu.DimensionMember`, `osdu.DimensionValue`, `osdu.DimensionAttributeName`, `osdu.DimensionAttribute`, `osdu.DimensionCollectedText`, `osdu.DimensionChange` and `osdu.dim_<dimension>`: dimensions

A dimension flow's builds keep here what they found ([dimension-plan.md](dimension-plan.md), Tables): one `Dimension`
row per dimension of a flow in a partition, one `DimensionRun` row per build, its values (`DimensionMember`) and its keys
(`DimensionValue`), the attributes it has had, each under a number (`DimensionAttributeName`), each key's attributes
(`DimensionAttribute`), and a log of what each build changed of a key. The tables keep the names they were created with: a
**member** is a value, the human-friendly form a person picks, and an **original** is a key, exactly what the index holds
(an id, for a reference). Every table is keyed by the ledger partition's number and an identity of its own, which is the
order it is stored in and what the other tables name its rows by, so they join on numbers and never on a text. A value,
a key and a label compare in the binary collation, exactly, as OSDU ids do; a search of them folds case.

Beside them a build keeps the dimension as one table of its own, `osdu.dim_<dimension>` (named after the dimension
alone, so a dimension's name is unique among the flows of a database)
([dimension-plan.md](dimension-plan.md), The table): a row per key and value it collects, keyed by an identity `id`,
with a column per attribute. It is the one object of the schema no migration creates: builds make it and widen it
through SQLFlow's schema evolution as the flow declares more, `Dimension.TableName` names it, removing the dimension
from its last partition drops it, and taking the `DimensionTables` migration back drops every one.

| Column | Purpose |
| --- | --- |
| `Dimension.DimensionId`, `FlowId`, `FlowName`, `Name` | The dimension, unique by its flow's ledger identity in the partition and its name. |
| `Kind`, `Query`, `Path`, `LabelJson`, `AttributesJson`, `CleanJson`, `DefinitionHash` | The declaration its last build read with: the query with its tokens filled, the label's paths (null when keys are their own values), and the attributes it reads (`[{ "name", "steps" }]`, or `{ "name", "collect" }` for a collected one; null for none). |
| `CollectedJson` | What the last build that settled the field collected: for each collected attribute its name and path, how the index stores the path, and the value records holding none were given. Null when the dimension collects nothing. |
| `FieldIndex`, `NestedPath`, `AggregateBy`, `Repeats` | How the index stores the field, as a build settled it from the templates: text, keyword, number, boolean or date, the nested array it sits in, the aggregation that reads it, and whether a record holds it more than once. |
| `Members`, `Originals`, `LastRunId`, `LastBuiltUtc` | The values and keys it holds now, and the build that wrote them. |
| `TableName` | The dimension's own table (`dim_<dimension>`), as the last build wrote it; null until one has. Indexed, since a build asks which dimensions write a table before it writes, and a removal before it drops one. |
| `KeyColumn`, `ValueColumn` | What the dimension's table names the two columns that hold each key and its value, as the table has them now: after what the dimension reads (`WellboreID`, `FacilityName`) or as its document names them, and `key` and `value` in a table made before dimensions named their columns, until its next build renames them. Recorded in the transaction that renames a column, on every dimension writing the table, so a reader names the columns the table has. Null until the table has been made ready. |
| `DimensionRun.DimensionRunId`, `RunId`, `Actor`, `Status` | One build: the platform run, who asked, and `running`, `completed`, `failed` or `cancelled`. |
| `Records`, `WithValue`, `Nulls`, `TooLong`, `Unreadable` | How complete it read: the records its query matched, those holding a key the index aggregates, null values, records holding only text too long for the exact field, values not of the field's type. |
| `Aggregations`, `Slices`, `Splits`, `ScannedSlices`, `ScanPages`, `ScannedUnits`, `CountQueries` | How it read: the aggregations asked, the ranges answered whole, the ranges split, those read by cursor and their pages, and the counts of values' records. |
| `Labelled`, `Unlabelled`, `LabelQueries` | For a dimension with a label: the keys a label was read for and those left without one (each valued by itself); and the searches that read the labels and attributes. |
| `MembersAdded` ... `OriginalsRestored`, `Templates`, `Notes`, `Error` | What it changed of values and keys, the kinds and templates it read, what it had to say (why keys have no label among it), and the redacted reason it failed. |
| `DimensionMember.MemberId`, `Value`, `Records`, `RecordsExact`, `Originals`, `Unfilterable`, `Filter`, `FilterParts` | A value, unique in its dimension, with the records holding any of its keys (exact, or the sum of its keys' counts), its keys and those no query can carry, and its search filter when one query holds it. |
| `DimensionValue.ValueId`, `Original`, `OriginalHash`, `MemberId`, `LeftOut`, `Note`, `Count`, `Filterable` | A key exactly as the index holds it, unique by its SHA-256, with its value or why it has none (`empty`, `tooLong`, `dropped`, `failed`), what cleaning said, and its count. |
| `Label`, `LabelFrom`, `Filter` | The label read for the key (at most 1,024 characters) and the id of the record it was read from, null when none was; and the search filter finding exactly the records holding the key, null when no query can carry it. |
| `DimensionAttributeName.AttributeId`, `DimensionId`, `Name`, `Ordinal`, `Collected` | An attribute of a dimension under its number, unique by its name (compared exactly): whether it is collected, and its place among the attributes the dimension declares now, from 1, which is the order of the attribute columns as the dimension's table is read. An attribute the dimension no longer declares keeps its number and has no place. |
| `DimensionCollectedText.TextId`, `AttributeId`, `TextHash`, `Text`, `Value`, `Records` | One text a collected attribute's records hold, unique by the attribute's number and the text's SHA-256: the text exactly as the index holds it, the value it is shown as (several texts shown alike are one value), and the records holding it. Indexed by attribute and value, so a search finds the texts of the values it picks in one seek. A build that settles its field replaces the dimension's rows. |
| `DimensionAttribute.AttributeValueId`, `ValueId`, `AttributeId`, `Value`, `ValueFrom`, `Records` | One value of one attribute of one key, unique by the key, the attribute's number and the value (at most 256 characters, compared exactly): one row for an attribute read from the record the key names, with that record in `ValueFrom`; a row per value for a collected attribute, with the text the key's records hold in `ValueFrom` and how many hold it in `Records` (null for an attribute read from the record). Indexed by attribute and value, so the keys an attribute value holds, and an attribute's values, are one seek. A build rewrites the attributes of the keys it found; a key no build finds any more keeps what its last build read. |
| `FirstSeenRunId`, `FirstSeenUtc`, `RemovedRunId`, `RemovedUtc`, `MemberSinceRunId` | When a value or key arrived, and when a build no longer found it: it is kept, and keeps its id if a later build finds it again. |
| `DimensionChange.ChangeId`, `DimensionRunId`, `ValueId`, `Change`, `FromMemberId`, `ToMemberId` | What a build did to one key: `added`, `removed`, `moved` (to another value) or `restored`. A dimension's first build logs no arrivals. |

A build writes its dimension in one transaction under an application lock per dimension, so a reader sees what one build
left and never half of the next, and two builds of one dimension write one after the other. A build writes a row only
where something of it changed; a key whose label, value or filter changed is rewritten, and so is an attribute read
differently (the build's notes count them). The tables came with
`20260930103543_DimensionFlows` (module version 1.17.0); `20260930175349_DimensionLabels` (module version 1.18.0) added
the label and filter of a key, the declaration's label and a build's label counts; `20260930190046_DimensionAttributes`
(module version 1.19.0) added `osdu.DimensionAttribute` and the declaration's attributes;
A dimension its flow no longer declares keeps what its last build wrote until an admin removes it
(`DELETE /dimensions/{dimensionId}`, `sqlflow dimensions remove`): every row of it in its partition goes, a table at a
time and its own row last, and the removal is an activity of its flow.
`20261001084750_DimensionCollectedAttributes` (module version 1.20.0) keyed an attribute by its value as well, with the
records holding a collected value, and added `CollectedJson`; `20261001102750_DimensionCollectedTexts` (module version
1.21.0) added `osdu.DimensionCollectedText`; `20261001163440_DimensionTables` (module version 1.22.0) gave attributes
their numbers and the two attribute tables keys of their own, and added `TableName`;
`20261001200214_DimensionColumnNames` (module version 1.23.0) added `KeyColumn` and `ValueColumn`. A dimension built before any of
them keeps its keys and values, with no label, key filter or attribute until its next build, and no table of its own
until its next build or the first read of its table.

### `osdu.Activity`: the audit trail of runs and interventions

One row per operator or scheduler action. The runs are `deliver`, `intake` and `drain` (a fan-out member's share),
`verify`, `reverse` ([Reversals](#reversals)), and the scheduled reachability `probe`; the interventions are `sync`, `release`, `redeliver`, `rerender` and `delete`,
and an admin's `remove-dimension` (a dimension its flow no longer declares, removed with everything kept of it).
Each carries the actor (`schedule:<name>` for a run a schedule fired, the requesting user or `manual:<user>` for a run
started by hand, `user:<name>` for an intervention from the GUI or the API, `cli:<user>` from a workstation,
`service:<name>` for the control plane's own; `unknown` on runs recorded before the platform named who started them),
start and end, outcome (`running`, `completed`, `failed`, `cancelled`), the parameters as JSON, the submission, record
and platform run it targeted when it targeted one, a summary and, for runs, the captured run log.

The summary of a run names only the counts that are not zero: what it planned, delivered, found unchanged or stale,
held, blocked, failed, left retrying or waiting ("3 delivered, 2 unchanged"), or that it had nothing to deliver. The
counts are the run's own; the submission holds its totals across every run that worked on it, and the run's result and
log hold every count.

`Idle` marks a run that completed having changed nothing: a deliver or intake run that planned, held and blocked no
record and sent none, left none waiting on an approval or on a record it refers to, a drain that settled nothing, a
verify that found nothing to check. Rows read and found unchanged or stale change nothing. The run sets it when it
completes; a failed or cancelled run never is idle, and neither is an intervention, which always stays in view. A
schedule firing every hour writes mostly idle runs, so the audit trail leaves them out unless asked (`idle=false` on
`GET /activities`, the **Show idle runs** switch in the GUI) and counts what it left out; the rows themselves stay, as
the record that the schedule fired and found nothing.

Record history is the attempts; run and intervention history is the activities. A record's page in the GUI
shows both, plus its verify outcomes; a run's page links to what it did to each record through the run id. An idle run
wrote nothing to any record, so a record's history never misses one.

### `osdu.ActivityRecord`: the records a release or a redelivery reached

An intervention made for one record names it on its activity (`DeliveryKey`). A release, a redelivery or a reversal
reaches many at once: the keys an operator ticked, every blocked or every delivered record of a flow, every record one
issue keeps blocked, or every record a run delivered, which can be a million. Each record it changes is named here, one row of
`(PartitionId, FlowId, DeliveryKey, ActivityId)`, written by the statement that changes it, in its transaction, so a
record is named exactly when it was released or marked. A record's activities (`GET /records/{flowId}/{key}/activities`,
its Timeline tab) are those that name it on their row and those that name it here, so its history shows every request
that reached it, with who asked, when and for what. A cache change's rollout marks records under no activity: the change
itself, on the cache page, is its record. The rows are never pruned: like the activity they point at, they are the
record's history.

### `osdu.Reversal` and `osdu.ReversalItem`: what a reversal did to each record

A reversal puts OSDU back as it was before one run or one submission ([Reversals](#reversals),
[reversal-plan.md](reversal-plan.md)). `osdu.Reversal` holds one row per source of a ledger: unique on
`(PartitionId, FlowId, SourceKind, SourceId)`, so asking again resumes the reversal rather than opening a second one.

| Column | Purpose |
| --- | --- |
| `PartitionId`, `ReversalId` | Primary key; `ReversalId` is an identity, unique across partitions, so a reversal is named by its number alone. |
| `FlowId`, `FlowName` | The ledger reversed. |
| `SourceKind`, `SourceId` | `run` or `submission`, and its id. |
| `SubmissionsJson` | The submissions the source covers, fixed when the reversal is opened. |
| `Status` | `capturing` (listing what the source delivered), `reversing`, `completed`, `failed`, `cancelled`. |
| `RequestedBy`, `RequestedUtc` | Who asked first, and when. |
| `CapturedUtc` | When every record the source delivered was listed; null while the listing is not finished. |
| `StartedUtc`, `CompletedUtc`, `LastRunId`, `Error` | The latest reverse run that worked on it, and how it ended (`Error` redacted). |

`osdu.ReversalItem` holds one row per record the source delivered, keyed by `(PartitionId, ReversalId, DeliveryKey)`:

| Column | Purpose |
| --- | --- |
| `TargetId` | The OSDU id the record claimed (binary collation, as the claim). |
| `FirstAttemptId`, `FirstVersion`, `RunVersion` | The source's first delivered attempt of the record, the first version it wrote, and the version the source left. |
| `Prior`, `PriorVersion`, `PriorAttemptId` | What OSDU held before the source: `version` (with the version and the attempt that delivered or restored it), `none`, or `unknown` (the attempts before it were pruned, so OSDU's version list decides). |
| `State` | `pending`, `sending` (an OSDU write is under way), `done`, `skipped` or `failed`. |
| `Outcome`, `Detail` | What came of it (`restored`, `removed`, `already-gone`, or why it was passed over or failed) and the reason, redacted. |
| `RestoredVersion`, `NewVersion`, `RunId`, `UpdatedUtc` | The version put back and the version OSDU gave it; the reverse run that settled it, and when. |

The counts a page shows (by state and by outcome) are read from the items through their state index; nothing keeps a
running total. The rows are never pruned: they are what the reversal did, and the attempts of each record point at the
reversal by its id.

### `osdu.SourceWatermark`: tier 0

One row per `(FlowId, Scope)`, where the scope is the flow's parameter set: `UpdatedThroughUtc` is the upper bound of
the last completed whole-scope plan, with the `SubmissionId` that wrote it, when it was recorded, and the render
context it was written under. The next run reads the window `(UpdatedThroughUtc - overlapSeconds, now]`, and a scope
with no watermark is read in full.

It moves only when a whole-scope plan finishes and every one of its fan-out members succeeded, so a failed member
leaves the watermark where it was and the next run covers the same rows again. A `keys` or `inline` plan never moves
it. When no row changed in the window and no record is waiting to be planned again, the run is skipped entirely,
unless it is forced.

### `osdu.Mapping` and `osdu.CacheDefinition`: what the repositories declare

Read models the repository sync writes: every mapping document (its reference, kind, the template version it pins
in `TemplateVersion`, a parsed summary, the YAML, and whether it parses) and every type a cache flow declares (the
cache flow's `FlowName` and `RelativePath`, the partition it fills as `Scope`, the `Endpoint` it searches as declared,
the type's `Name`, `EntityType`, `Kind` and `Query`, the kept paths as `FieldsJson`, and `OnChange`). A type is one
row per repository, cache flow and name (unique on `RepoId`, `FlowName`, `Name`), indexed on `Scope` and `Name`
because a refresh reads every declaration of its partition to know the paths the cache keeps for a type. A declaration
that disagrees with another flow's declaration of the same type for the partition is left out with a warning. They
back the GUI's Mappings and OSDU cache pages, and the templates listing counts each template version's pins from
`osdu.Mapping`; nothing writes them but the sync.

### `osdu.Interface`: the sources and interfaces the repositories declare

A read model the repository sync writes: one row for every interface of every delivery flow, and one row, with an
empty interface name, for a flow in the single form ([documents.md](documents.md#a-source-with-interfaces)). It is how
the API and the GUI find the pipeline behind a ledger identity, and the interfaces of a pipeline, without parsing a
document.

| Column | Purpose |
| --- | --- |
| `Id` | Primary key, derived from the repository, the flow's name and the interface's name. |
| `RepoId`, `FlowName`, `Interface`, `Ordinal` | The pipeline, the interface's name (empty for the single form) and its place in the document. Unique on `(RepoId, FlowName, Interface)`. |
| `LedgerFlowId`, `LedgerName` | The ledger identity every ledger row of the interface carries, and the name it is derived from: the flow's name, `<flow>/<interface>`, or the ledger the interface adopts. Indexed on `LedgerFlowId`. |
| `Route`, `RouteReason` | `storage`, `file`, `manifest` or `ddms`, and why (null for the single form, whose document names its protocol). |
| `MappingReference`, `Kind`, `RecordObject` | The mapping it pins, the kind that mapping fills when the repository holds a valid mapping of that reference (empty otherwise), and the record table it reads. |
| `AfterJson` | The interfaces it waits for, as a JSON array of names. |
| `RelativePath`, `Active`, `FirstSeenUtc`, `LastSeenUtc` | The document it is declared in, whether the repository still declares it, and when the sync first and last found it. |

The row of an interface the repository no longer declares is kept with `Active` false, so the records it delivered
still lead to their flow, while the flow's own listing of interfaces leaves it out. A document that does not parse
describes nothing: the rows of its interfaces turn inactive until it parses again, and the sync counts it as invalid. The
sync warns when two flows keep one ledger (an
interface that adopted the ledger of a flow the repository still holds, or two repositories declaring one flow). The
control plane describes, once when it starts, every repository whose delivery pipelines were synced before this table
existed, from the catalog's copies of their documents, so their records' pages work before their next sync.

### `osdu.CacheVersion`, `osdu.CacheItem` and `osdu.CacheMember`: one cache per partition

What a cache holds lives here and nowhere else ([design.md](design.md) section 6.2): nothing about it is written to a
repository. There is one cache per OSDU data partition, keyed by the partition (`Scope`, the `data-partition-id` the
cache flows declare, with its references resolved), and every cache flow of the partition writes into it. A version is written by a refresh run whose
merge changed the cached content, or by `sqlflow cache import`, and never changes afterwards; the newest version of a
partition is its current one. Every version is kept, because a delivered record's
render context names the version it was rendered against.

| Column | Purpose |
| --- | --- |
| `Id` | Primary key, derived from the partition and the version label. |
| `Scope`, `Version`, `Sequence` | The partition, the label minted from the capture instant (`20260908T212727Z`, with the sequence appended when two captures of the partition share a second, as in `20260908T212727Z-7`), and the version's place in the partition's history, 1 for the first. `(Scope, Version)` and `(Scope, Sequence)` are both unique; the sequence is what a concurrent write of the same partition collides on, so it fails rather than interleaving. |
| `FlowName` | The cache flow whose capture, or import, wrote the version. |
| `CapturedUtc`, `CapturedBy`, `RunId`, `Origin` | When it was captured; who asked (the run's trigger, or `cli:<user>` for an import); the platform run that captured it, null for an import; the endpoint reference searched or the directory imported. |
| `ContentHash` | The hash of the whole content, checked on every load: a version whose records were altered after it was written is refused, and nothing renders against it. |
| `PreviousVersion`, `Current` | The version that was current when this one was written, which the capture was merged onto, and whether this is the newest version, the one deliveries render against unless a flow pins another. |
| `TypesJson`, `Items` | The types the version holds, each with its entity type, record count and (for a lookup table) key, and the records across them. Each type also carries its own content hash (`hash`, over exactly the bytes the type contributes to `ContentHash`), how it compares with the version before by that hash (`change`: `added`, `changed` or `unchanged`), and the version its content dates from (`since`: the version that last added or changed it). The version moves whenever anything in the partition's cache does; a type's `hash` and `since` move only when that type does, so a type that only rode along with another type's change, or with a changed system property, is told apart. A load checks every type against its `hash` as well as the whole against `ContentHash`, and names the type whose records no longer match. A version written before types were hashed has none of the three; it loads by `ContentHash` alone, and the next version dates its types from what the records' ranges and the earlier versions hold. |
| `SystemPropertiesJson` | The partition's system properties the capture found: the settings the platform's indexer and search service report for the partition, each a `service`, `name` and `state` (`Enabled`, `Disabled` or `Unknown`), with the `source` the service took it from and the `detail` saying why it is unknown. They are not cached records and have no record id; they enter `ContentHash` by service, name and state, so a changed setting is a new version. `[]` for a version written before captures recorded them, which hashes as it always did ([documents.md](documents.md#cache-flow)). |

`osdu.CacheItem` keeps the cached records by version range rather than by copy. A row is one record of one partition's cache
(`Scope`, `TypeName`, `RecordId`) with its captured values (`FieldsJson`, with every scalar also in `Terms` for search)
as a run of consecutive versions held them: from the version at `FromSequence` up to, and not including, the one at
`ToSequence`, which is null while the newest version still holds the record unchanged. A record is stored once per
partition however many cache flows capture it, and a merge writes rows only for the records that changed, arrived or
left, so keeping every version costs rows in proportion to what moved. A merge reads and compares the rows of the types
whose content hash moved alone: a type whose hash did not move keeps its open ranges exactly as they stand, since the
version it is merged onto was loaded from them and checked against that hash.

Both tables are stored in partition order: `osdu.CacheVersion` is clustered on `(Scope, Sequence)` and
`osdu.CacheItem` on `(Scope, ItemId)`, with their ids as nonclustered primary keys, and `osdu.CacheMember`'s key starts
with the partition too. Whatever a refresh reads or writes of its partition is then a range seek of that partition,
however small the tables are, so refreshes of different partitions never lock each other's rows. Clustered on their
ids, two partitions refreshing at once each waited on the other's new version row, and the database ended one of them as
a deadlock victim.

`osdu.CacheMember` is current state, not history: one row per partition, type, record and cache flow (`Scope`,
`TypeName`, `RecordId`, `FlowName`, which together are the key) saying that the flow's last capture of the type held
the record. It is what lets several cache flows share one partition's cache. A merge replaces the capturing flow's rows
for the types it captured, and a record the capturing flow no longer finds leaves the cache only when no other flow's
row still holds it. The rows of a type the merge removes go with it, and the repository sync deletes a flow's rows for
a type the flow stops declaring. What each version held is in `osdu.CacheItem`.

### `osdu.CacheSet`, `osdu.CacheSetEntry` and `osdu.UpdateTag`: what a cache change reaches

A `osdu.CacheSet` is one distinct combination of cached values a render consumed, shared by every record that read
the same values through the record's `CacheSetId`. Each `osdu.CacheSetEntry` is one value in it: the partition
whose cache it was read from (`Scope`, the partition the delivery flow delivers to), the type, the cached record, the
path, the value as it was read, and `Kind`: `match` (what the record was found by), `value` (what went into the
document), `empty` (a field read that held nothing), or `unlisted` (a key a lookup table listed no row under, held
without case as the cached record, with the field a row would have given as the path). A `osdu.UpdateTag` is one change a refresh found in values delivered records
were built from: the partition (`Scope`), the type, the cached record, the
path, `Change` (`changed`, `removed`, `unmatched`, or `listed` for a key a lookup table now lists), the value before
and after, the versions it moved between, `Mode` (`approve` or `auto`), `Status` (`pending`,
`approved`, `rejected`, `rolling` while its records are marked, `delivering` once all are marked and some flow still
builds one from the old value, `applied` when none does), how many delivered records it reaches and how far the rollout has got
([design.md](design.md) section 6.2).

### `osdu.Template`: the templates mappings pin

The OSDU schemas mappings are checked and rendered against ([mapping-templates.md](mapping-templates.md)). They are
saved in the catalog rather than in a repository, because the control plane runs as a container whose disk does not
survive a restart. A row is written by the GUI's Templates page, `POST /api/v1/delivery/templates`, or
`sqlflow template capture` and `import`, and never changes afterwards.

| Column | Purpose |
| --- | --- |
| `Id` | Primary key, derived from the kind and the version. |
| `Kind`, `Version` | The OSDU kind, and the content version: the first 16 hexadecimal characters of the hash of the canonical bundled schema. Unique together; a mapping pins both. |
| `SchemaJson` | The bundled JSON Schema, every reference resolved into its definitions. |
| `Origin`, `CapturedBy`, `CapturedUtc` | Where the schema came from (the release, commit and file of the OSDU data definitions, a local data definitions folder, or an imported file), who saved it and when. |

Saving a schema that is already saved adds no row. A version is deleted only while no `osdu.Mapping` row pins it
(the `(Kind, TemplateVersion)` index answers that), so a template a synced mapping pins cannot be removed from under
it.

## Record lifecycle

```text
                 intake                     worker
   (new/changed) ──────▶ pending ──claim──▶ delivering ──▶ delivered ◀── verify (drift → hashes cleared → next plan updates)
                            ▲                  │
                            │ backoff          ├──▶ held    (data problem, non-retryable status, or the gate's verdict; Blocked)
                            └──────────────────┤
                                               └──▶ failed  (retry budget exhausted; Blocked)
   operator removal ──▶ deleted (not blocked; OSDU no longer holds it) [scope record or everything]
                   ├─▶ reverted (Blocked; OSDU holds the version before the latest again) [scope previous]
                   └─▶ (no change)                                  [scope history: OSDU still holds it]
   operator reversal ──▶ reverted (Blocked; OSDU holds the version from before the run again)  [the run updated it]
                     └─▶ deleted  (Blocked; removed again, reversibly)                       [the run created it]
   operator release ──▶ pending (when a rendered document is still there) or unblocked for the next plan
                        (a reverted record is delivered again, at the version the reversal wrote)
   claim ──▶ waiting (the document refers to a record of the ledger that has not landed) ──lands──▶ pending
```

A **waiting** record holds a rendered document that refers to a record another record of the ledger holds and has not
delivered (docs/interfaces-design.md section 7). The claim decides it, so nothing is charged and no worker is
involved: the record says which OSDU id it waits for, and goes back to pending when the record holding that id lands.
It is not blocked and needs no operator; a release by name sends it as it is, without waiting, and drops its
references. An id no record of the ledger holds is not waited for, because it is OSDU's or another system's; nor is
one whose record was removed from OSDU, nor one of an interface the source's order says this one does not wait for.
With `target.verifyReferences: storage` the ids the ledger does not hold are asked of storage before the record is
sent, and a record naming one storage does not hold is held instead.

A record the gate before sending holds for its verdict (`target.validation`) keeps its rendered document, so a release
puts it back to pending with that document and accepts it as it is: the gate sends it whatever its verdict says, once.

A **blocked** record (held, failed, deleted or reverted and not released) is skipped by every later plan as `blocked`
until either the source row changes (its fingerprint moves, or its last-modified moment passes the one it was left
at) or an operator releases it. That is what "do not retry without intervention" means in practice: a re-run of the
same data never re-attempts a known problem, while a corrected source row flows through on its own.

A release names the records it reaches by key, as every blocked record of the flow, or as every record one issue
keeps blocked ([Issues](#issues)). A released record that still holds its rendered document goes back to pending in
its submission, and the flow's next deliver run sends it, whatever that run itself plans: the row is what the record
already queues, so a run whose plan finds nothing new for it still sends it. Once a run's own records are sent it takes
the due records of every completed or failed submission, ten submissions at a time, each once, and recomputes their
totals. A `drain` run sends it at any time. A record released without a rendered document is stamped to be planned again
instead, and the next run reads it from the ingestion tables by key, every such record before its own pass: 5,000 to a
pass (`RequestedPerPass`), each pass a submission of its own, as many passes as there are records, walking the requests
in the order they were made so each is met once. A record a pass cannot plan (its row gone from the table, or outside
the run's scope) keeps its request for the run that can, and does not hold this one up. So a release of a whole set,
or a redelivery of every delivered record, is sent by the next run whole.

Versions never go backwards. A row older than the version a record holds, delivered or queued, is skipped with an
attempt (`skipped`, phase `stale`) naming both versions, and staging refuses work older than what the ledger holds,
so two intakes racing for one record leave the newer version standing. Work planned for a record another worker is
delivering right now is written behind the delivery rather than dropped: the record keeps its lease, the completion
promotes what that try actually delivered (from the claim it carries, not from the columns the newer work replaced)
and leaves the newer work pending, and the try's step progress is kept only while the record still holds its
document. Before anything is sent, the worker compares the queued document and payload hashes with what the record
says OSDU holds at that moment and sends only the halves that differ; when neither does, it settles the record with
an attempt (`skipped`, phase `unchanged`) and sends nothing.

**Redeliver** forgets the hashes of what OSDU holds (all of them, or only the record's or only its files' or bulk data's) and stamps the
record to be planned again, so the next plan that reaches it sends that part again. On a route that sends its payload
in parts, a redelivery of `files`, `bulk` or `workflow` names that part on the delivered payload hash instead
([Payloads in parts](#payloads-in-parts)), so the other parts are not sent again. From the GUI, redeliver also
queues a deliver run scoped to the record, which reads it from the ingestion tables by key, so the redelivery happens
at once and is recorded under the user who asked. It never bypasses the render: the document sent is always the one
the pinned mapping produces from the current source rows.

**Bringing records up to date** (the `rerender` intervention, [operations](operations.md#redelivering-records)) keeps
the hashes of what OSDU holds and forgets instead the source version the record was last planned under: its
fingerprint and modified time, of the delivered version and of any queued work. It stamps the record to be planned
again, so whichever run meets it next, a scheduled one included, cannot pass it as unchanged without rendering it, and
the hashes decide what is sent: only a part that renders differently goes. A plan that finds the record unchanged
writes the source version back with its skip. It reaches only records OSDU holds (delivered, with an id); a named
record in any other state is left as it is. An intervention that names more than 100 records records how many in its
parameters, and the ledger names each one under it (`osdu.ActivityRecord`).

**Removal** takes the record out of OSDU through the flow's protocol, to one of three depths (see
[operations](operations.md#removing-records-from-osdu)). `record` and `everything` write a `delete` attempt and
forget the hashes and version. The record is not blocked: a removal is a clean-up, and the record follows its source
again. The next run that reads its row (a full read, or the row changed under an incremental one) finds nothing
delivered and creates it again; that is ownership, not an accident. A row the ingestion table marks deleted is never
sent, and a record that should stay out of OSDU while its row stays is taken out of the source. A reversal's removal of
a record the run created does block it, since a reversal undoes a run that went wrong and must not be redone by the
next one.

`history` is the exception: it destroys the record's earlier versions and leaves the record itself live in OSDU
at the version the ledger already holds. Its custody state is therefore still true and is not disturbed; the
purge is written as a `purge-history` attempt and nothing else changes. Every removal, at every depth, names the
scope and the operator on the attempt and in the activity trail.

`previous` takes the latest version out of being current without deleting anything (OSDU has no call for that): the
version OSDU held before the write that left the latest is written back as a new version
([reversal-plan.md](reversal-plan.md#restoring-the-previous-version)). The ledger tells which version that is: the one
the delivered attempt recorded it replaced (`replaced.version`), the one an earlier step back recorded it replaced
(`previous.replacedVersion`), else the version of the attempt before. The record becomes `reverted` and blocked, as a
reversal leaves it, with the hashes and origin of the attempt that delivered the version put back, and gets a
`restored` attempt (phase `restore-previous`) naming both versions. A record already blocked by an earlier step back
keeps the source version it is blocked at. Its activity kind is `restore-previous`, not `delete`: nothing was removed.

### Deleting a removed record from the ledger

A removal that takes a record out of OSDU (`record` or `everything`) can take it out of the ledger too, as an extra step
the operator asks for (`purgeLedger`). Once OSDU has answered for the record (removed, or already gone), the ledger
deletes its attempts, its search entries and its row, in the same chunk, a slice of records to a transaction. Only a
record the ledger marks `deleted` goes, and none a lease holds: a record whose removal failed, or one OSDU still holds,
stays as it was, whatever is asked. History purges and `previous` never delete from the ledger; the API and the node
refuse the extra step with them.

The ledger keeps one line of each record it deletes, in `osdu.PurgedRecord`: its key, source key and label, the OSDU id
it was delivered and removed under, the last version an attempt of it named, how many attempts went with it, who deleted
it, when, and under which intervention. The intervention names the record in `osdu.ActivityRecord` as well, and the
activities that named it before stay, as the whole audit trail does. A record's page asked for a deleted record answers
404 titled "Deleted from the ledger", saying who deleted it and when, with that line under `purged`. A reversal's item
of the record stays: it is the reversal's.

A record removed from OSDU earlier is deleted from the ledger alone, asking nothing of OSDU, by the same deletion: the
removal dialog's "Leave as it is" with the ledger step, or `POST /flows/{pipelineId}/records/purge` (keys, a filter, or
every record removed) and `POST /records/{flowId}/{key}/purge`. It runs in the control plane as an intervention of kind
`purge`, and a record OSDU may still hold is left as it is and counted as left (the record route refuses it with 409).

What goes is the record's history, for good. The ledger's watermarks go with it: a watermark says every row of its scope
up to it was planned, which is no longer true of a row whose record was deleted, so the next run reads every row once (as
after its rules moved), even one the schedule starts, and delivers a row still in the source as a record the ledger never
held, under the OSDU id its mapping gives now; the line of the earlier one stays. Every other row is decided by its
record's own hashes, so only the deleted records are sent. A run of the ledger that was already reading when the records
were deleted writes its own watermark when it ends; a run with **Force** then reads the rows again.

### Reversals

A **reversal** undoes what one run or one submission delivered ([reversal-plan.md](reversal-plan.md),
[operations](operations.md#reversing-a-run)). It is a run of the flow with the operation `reverse`, so nothing else
delivers the flow while it works. For each record the source delivered it reads from the record's own attempts what OSDU
held before the source's first delivery of it, and:

- a record the source **updated** gets that version back: it is read from storage and written as a new version, as it
  was (only the keys External Data Services writes are carried from the latest version). The record becomes
  `reverted`, blocked, at the new version, with the hashes and the origin of the attempt that delivered the version put
  back, so the ledger says again what OSDU holds (a version found in OSDU's version list has no attempt, so its hashes
  are left empty and the next delivery after a release sends the record whole);
- a record the source **created** is removed again at the `record` scope (reversible), and becomes `deleted`, blocked,
  as a removal leaves it.

A record is reversed only while it is still the record the source left: a record a later run delivered again, or one
restored or removed since (`superseded`), one OSDU holds at another version than the source left (`changed-in-osdu`),
one with work queued or in flight (`busy`), and one the flow never claimed (`not-claimed`) are passed over, each with a
`skipped` attempt that says why. To reverse a run a later run superseded, reverse the later one first.

Each record's attempt, its custody change, its `osdu.ActivityRecord` row under the reverse run's activity and its
reversal item are written in one transaction, at most 1,000 records to a transaction, and only while the record still
stands at the version the source left: a record that moved between the check and the write is settled as `failed`, not
put back over what moved it. A run stopped anywhere loses nothing: the next run of the same reversal takes what is still
pending, what failed and what was passed over as `busy`, and checks an item it left `sending` against OSDU before
writing it again. A source that delivered nothing to the ledger, and one whose reversal has nothing left to take, are
refused before a reversal is opened, so the ledger never holds an empty reversal or a run that changed nothing in one.

A reverted record stays blocked while its source row is unchanged, so a scheduled run does not send the same rows again.
A corrected row flows through on its own; a release makes the record `delivered` again, and the next run plans it and
sends only what renders differently from what OSDU holds now.

## Issues

A blocked record says why in its last error, and an error names the record: the value it read, an OSDU id, a moment, a
row, a file, the correlation id of the request OSDU refused. A million records held for one reason carry a million
different errors. The ledger keeps, beside each blocked record, the **issue** its error names: the error with every
part that names the record replaced by a placeholder (the **pattern**), hashed. Records refused for the same reason share
an issue, so a flow with a million blocked records shows the handful of issues they share, and an operator fixes a
cause, checks one record of its issue, and releases the issue's records together. The code calls an issue a problem
(`ProblemHash`, `ProblemSignature`, `ProblemGroup`); everything an operator reads or types (the Issues tab, the API's
`/issues` routes, `sqlflow records issues`) calls it an issue.

**The pattern** is made in one place (`ProblemSignature`), from the redacted error as the record keeps it, on every path
(the intake's holds, the worker's failures, the backfill, every read that names a group), so the same error is the
same issue wherever it was written. What it replaces, in order:

| Part | Becomes |
| --- | --- |
| The correlation id a refused request names, whatever the service spelled it with | `<id>` |
| An OSDU kind (`osdu:wks:master-data--Wellbore:1.0.0`) | kept: it names what was refused, not which record |
| A web address | its scheme, host and the words of its path, every other segment `<id>`, the query `<query>`; the service that refused stays |
| Any other address (a lake, a seismic store) and a file path | `<path>` |
| A moment (ISO 8601, a cache version's label, a time of day) | `<time>` |
| A UUID or 32 hexadecimal characters | `<id>` |
| An OSDU record id | `<osdu id>` |
| A run of 16 or more hexadecimal characters | `<hash>` |
| A value in single, back or typographic quotes, and an escaped value inside a quoted answer | `'<value>'` in its own quotes |
| A number standing alone (a row, an index, a count, a status code) | `<n>`; a digit inside a word (`Tag4`, `v2`) stays |

A double-quoted string is kept, because that is how a service's answer spells its reason and its message, and runs of
whitespace collapse to one space. An error with nothing in it is the issue `no reason recorded`. The hash is the
first eight bytes of the pattern's SHA-256, written as sixteen hexadecimal characters wherever an issue is named (the
API, the CLI, a link). Two errors that differ in a part the rules keep are two issues: the rules split an issue rather
than merge two, which is the safer way to be wrong when a whole issue is released at once. A change to the rules
changes the issue of an error they reach, so it ships with a migration that clears `ProblemHash` on every blocked
record, and the backfill sorts them again under the new rules.

**Who writes it.** An issue is written by the write that blocks the record and cleared by every write that lets it go,
so `ProblemHash` is set exactly while the record is blocked, held or failed:

- the intake's hold (`MarkHeldAsync`) writes it with the error;
- a worker's completion that holds or fails the record carries it on its event (`RecordEvent.ProblemHash`), computed when
  the event is appended, and the lease applies it with the rest of the completion; a completion superseded by newer
  work, or one that delivers, leaves none;
- staging new work, a release and a removal clear it (a removed record is blocked, but by an operator, not an issue).

**Records blocked before the ledger kept issues** have none. The filtered index `IX_Record_Unsorted` holds exactly
the blocked, held or failed records without one, and the control plane's backfill (`RecordProblemBackfillService`)
reads it 500 records at a time, a quarter of a second apart, and sorts each into its issue from its stored error,
writing only while the record is still the blocked record that error was read from. A record sorted leaves the index,
so the pass needs no cursor, resumes where it stopped after a restart, and costs one seek once the backlog is gone; it
looks again every ten minutes for a completion an older node appended. Until a record is sorted, a flow's issues say
how many of its blocked records are not sorted yet.

**Reading issues.** A flow's issues (`GET /flows/{pipelineId}/issues`, the flow page's Issues tab,
`sqlflow records issues`) are counted from the records through the filtered index
`(PartitionId, FlowId, ProblemHash, UpdatedUtc) INCLUDE (Status, PendingSourceFileName) WHERE ProblemHash IS NOT NULL`,
which holds the blocked records alone: per issue its records, held and failed, and when they last changed, the most
records first, with how many issues and records there are in all, in one pass over the flow's range of the index. Each
issue names its most recently changed record as its example, one seek each, and its pattern is read from the
example's error. One issue's files (`GET /flows/{pipelineId}/issues/{issue}`) are counted from the file the index
includes, and its records are the records listing narrowed to it (`issue=`), read newest first in the index's own
order. Like the statistics, these are counts of the records and never kept beside them.

**Set errors and row errors.** Most issues are **set errors**: a prepared dataset, a cache entry, the mapping or a
legal tag is wrong for every record of a set, so every record carries the same error. Fixed once, they are released
together. Some are **row errors**: a value missing or wrong in one row, which each row has to have fixed in the source,
and a corrected row is planned again on its own. An issue tells them apart by the values its records' errors name
(`ProblemSignature.Values`, the parts the pattern writes as `'<value>'`): when they name none, or every record names the
same ones (the same bad unit in every row of a dataset), the issue is a set error; when records name different values,
each its own row's, it is row errors. The listing reads it from each issue's newest and oldest record, one more seek
each. **Samples** (`ListProblemSamplesAsync`, five unless asked for up to twenty) are the records at the newest, the
oldest and even steps between of the issue's order, read in one pass over its range of the index, so records loaded
at different times and from different files stand side by side; the issue's own page reads its shape from them, which
can only turn a set error the two ends agreed on into row errors, never the reverse. They are what an operator checks
before releasing an issue: each can be rendered as it would be now, which sends nothing, or released and tried alone.

**Releasing an issue** (`POST /flows/{pipelineId}/issues/{issue}/release`, `sqlflow records release --issue`)
walks the issue's range of the index in its order, a page of 1,000 keys at a time, and releases each page in a
statement of its own: a record still holding its rendered document goes back to pending, any other is asked to be
planned again. A released record leaves the index and the walk only moves forward, so it ends; a record a run blocks
with the same issue while it walks is released too. The walk is not bounded by the moment the release began, because
the moments it would compare were written by the clocks of the nodes that held the records, not this one's. It is
recorded as a `release` activity naming the issue and the pattern the operator was shown, and each record it released
is named under it ([`osdu.ActivityRecord`](#osduactivityrecord-the-records-a-release-or-a-redelivery-reached)), and the
deliver run asked for with it plans and sends every one of them. A release of every blocked record walks the status index
the same way, one custody state at a time, and so does a redelivery of every delivered record, which passes over the
records it marked itself. A redelivery leaves a record still blocked by an issue its error, which the issue was read
from.

## One source, several flows

An ingestion table can feed several OSDU flows, each rendering the rows with its own mapping into its own OSDU kind
([design.md](design.md) section 5.4). The rows are loaded once; each flow keeps its own ledger over them.

- **A record is one flow's.** Its key is the flow and the delivery key together, so a row read by two flows is two
  records, each with its own state, attempts, activities, submissions and watermark. Every write the engine makes
  (staging, holds, claims, leases, completions, step progress, verify outcomes, removals) names the flow, and none of
  them reaches another flow's record of the same row. Statistics are counted per flow.
- **A record is addressed by both parts.** The API's record routes are `/api/v1/delivery/records/{flowId}/{deliveryKey}`
  and the GUI's record page is `/delivery/records/{flowId}/{deliveryKey}`. The flow id is the ledger's (derived from the
  flow's name), not the pipeline's, so a record's history stays reachable after its flow leaves the repository. The
  search box, given a delivery key, lists one record per flow that reads the row.
- **Every interface of a source is a flow here.** An interface keeps the ledger identity derived from
  `<flow>/<interface>` (or the one its `ledger:` adopts), so everything above holds for it: its records, submissions,
  watermark, claims and statistics are its own, and a source's counts are its interfaces' added up. The name its rows
  carry (`FlowName` on a submission, an activity and an event) is that ledger name. `osdu.Interface` leads a ledger
  identity back to its pipeline and interface, and an adopted ledger carries on under the interface with its whole
  history.
- **One OSDU record, one flow.** The OSDU id is `{partition}:{entityType}:{deliveryKey}`, or the key's own values for
  a mapping with `dataset.idFrom: key`, so flows delivering to different entity types or partitions write different
  records. Staging claims a record's OSDU id the first time the record queues a document (`ClaimedTargetId`, unique
  across the ledger). Work whose id another flow's record has claimed is not staged: the record is held, with the
  owning flow named in its error, and nothing is sent. A release plans it again and meets the same conflict. To resolve
  it, deliver the second flow to another partition, or give its mapping a `dataset.system` or key that yields other ids.
  A new mapping has delivered nothing, so changing its identity re-keys nothing.
- **One OSDU record, one record of the ledger.** An id made from key values can be given by two records of one flow (a
  mapping re-keyed under a new `dataset.system` gives its rows new delivery keys and the same ids). Staging refuses an id
  another record of the same flow claimed, as it refuses another flow's, naming that record's key; two records of one
  staging that give one unclaimed id stage the one with the lowest key and hold the others naming it.
- **A record keeps the OSDU id it claimed.** The plan holds a record whose render gives another id than the one it
  claimed, naming both, and sends nothing; staging keeps the claimed id whatever the work names, and the worker sends
  nothing when the queued document's `id` is not the record's id. A record that only ever was held claimed nothing: it
  names the id its latest render gives, unless another record claimed that one.
- **An id made from a key is claimed only when it is free.** A record about to claim such an id for the first time has
  never sent anything, so a record OSDU already holds there is not its own: the intake asks the flow's target before
  staging, and holds the record, unclaimed and naming the id, when OSDU holds a record at an id no record of the ledger
  claimed, or cannot say. An id another record of the ledger claimed is left to staging, which names that record.
- **A flow acts only on ids it claimed.** The worker sends only a claimed id. A read back reads the claimed id, and a
  removal skips a record that claimed nothing, so a record that was only ever held can never reach another flow's
  OSDU record. A held record is not given an id another flow has claimed.
- **Races settle in the database.** Two flows' intakes staging the same new id at once are serialized by the unique
  index: the loser's slice of staging is rolled back, runs again, and then holds its record naming the winner. A slice
  the database ends as a deadlock victim is run again the same way (see [Many nodes, one table](#many-nodes-one-table)).

## Partitions

One control plane delivers to several OSDU partitions at once: a flow that names its partitions, or follows the registry,
delivers some rows to `dev` and others to `test` from the same instance, and a flow whose partition is its
`data-partition-id` header delivers to the one its header resolves to ([partitions-design.md](../../docs/partitions-design.md)).
Every element of the ledger is therefore one partition's, and the partition is part of every ledger table's key.

- **Every ledger table leads with the partition.** `osdu.Record`, `RecordIdentity`, `Attempt`, `Submission`,
  `WorkBatch`, `Lease`, `RecordEvent`, `SourceWatermark`, `Activity`, `ActivityRecord`, `Retrieval`, `AssertionRun`,
  `AssertionResult`, `Reversal` and `ReversalItem` each carry `PartitionId`, and each
  primary key starts with it, so one partition's rows are one range of every clustered index and of every index that
  serves a listing. A partition's rows never interleave with another's, a partition's reads never touch another's pages,
  and a partition can later be moved to a filegroup or a table partition of its own without a key change.
- **The key is a number.** A data-partition-id is up to 200 characters; carried in every key it would push the
  source-file index (`SourceFileName` is 800 characters) past SQL Server's 1,700-byte limit on a nonclustered key, and
  widen every index of the largest tables. `osdu.LedgerPartition (PartitionId smallint identity, Name unique)` numbers
  each partition once, the first time a ledger is kept under it, and the tables carry the two-byte number.
- **The directory says which partition a ledger belongs to.** `osdu.Ledger (PartitionId, FlowId unique, Kind, FlowName,
  Interface, LedgerName, RegisteredUtc)` holds one row per ledger identity. A ledger belongs to one partition for its
  whole life: its records' OSDU ids, and every record a later run compares with, are that partition's. The ledger
  resolves a flow id to its partition number once per process and keeps it.
- **A run registers its ledger before it writes a row of it.** The runtime (`LedgerRegistration`) registers the ledger in
  the partition it delivers to: the one the flow is bound to, or the one its header resolves to on the node. The first
  registration creates the directory row; every later one confirms it. A registration naming another partition than the
  directory holds is refused before anything runs, naming both: this is how a header that resolves differently than when
  the ledger was written (an `${env:}` reference set otherwise on another node) is caught before it mixes two partitions'
  records. A retrieval flow registers its ledger the same way, in the partition its source header names. The control
  plane registers a ledger it writes to outside a run (a scheduled probe's activity, an intervention) the same way,
  resolving the header with the central configuration as a run of the flow would. A write to a ledger no one registered
  is refused; a read of one answers empty.
- **The upgrade places what it can, and a run places the rest.** `LedgerPartitions` fills the directory from what the
  ledger already says: the partition `osdu.Interface` describes a ledger in, else the one partition its records' OSDU ids
  name. A ledger it cannot place (no interface row, and records in no partition or in several) is kept under partition
  number 0, unassigned, with its rows intact. The first run that registers it adopts it: every row moves to the run's
  partition, a slice of each table at a time, and then the directory row. Adoption is refused when the ledger holds
  records delivered to another partition than the run's, since they would become that partition's records.
- **Reads name their partition.** A read of one ledger reads its partition's range. A read across ledgers (the Records
  page's lookup and recent listing, the audit trail, the submissions listing, the search box) takes a partition: the one
  a request names, else the workbench's (the `X-Osdu-Partition` header every call from the GUI carries), else every
  partition, each read through its own range of the partition-first index and merged. A ledger and a partition named
  together are both filters: a ledger of another partition reads empty. Waits, holders and held ids are the partition's
  of the ledger asked about, since an OSDU id is referred to within its partition.
- **Records of a partition taken out of the registry stay readable.** Removing a partition from the registry deletes
  nothing; its ledgers stay in the directory. A flow's pages, the Records page and the audit trail read them when that
  partition is picked, and the Partitions page keeps listing the partition while a ledger is kept under it. A run in it
  asks for it to be registered again.
- **Telemetry is tagged by partition.** `osdu_delivery.records` and `osdu_delivery.probes` carry a `partition` tag, the
  partition the record's ledger is kept under, so a dashboard reads one partition or adds them up.

## Leasing

A worker does not write the record table while it delivers. It writes its lease row, the events it appends and the
attempts; the records change when the lease applies the events.

```text
claim:      INSERT Lease (Token, FlowId, SubmissionId, WorkBatch, Owner, ExpiresUtc = @now + lease)
            UPDATE Record SET Status='delivering', LeaseOwner=@token, AttemptCount+=1
              WHERE FlowId=@flow AND DeliveryKey IN (@due) AND Status='pending' AND <due>   (1,000 keys a statement)
renew:      UPDATE Lease SET ExpiresUtc=@now + lease WHERE Token=@token AND Owner=@worker   (one row)
append:     INSERT Attempt ...; INSERT RecordEvent ...                                      (one transaction a write)
checkpoint: apply the lease's events to their records, 1,000 records a transaction, deleting what was applied
close:      checkpoint; hand back what the lease still holds; settle its batch; DELETE Lease
recover:    UPDATE Lease SET Owner=@recoverer, ExpiresUtc=@now + 5 min WHERE Token=@token AND ExpiresUtc < @now
            then close it as expired
```

- **Claim.** A drain claims the oldest queued batch of the flow (or of one submission): the lease row and the batch's
  move from `queued` to `running` commit together, so two workers never hold one batch. The batch's due records are
  then marked with the lease's token, a thousand to a statement. A claim outside the batches takes up to 500 due
  records of the flow under a lease of its own. Either way the record's move from `pending` to `delivering` is a
  compare-and-swap, so two leases never hold one record, and any number of nodes share the ledger. That is what lets a
  submission's drains spread over the fleet ([design.md](design.md) section 16.4).
- **Renew.** The worker renews its lease at half its length (at least once a second apart), and at each renewal
  checkpoints the lease and reports its progress to the delivery listeners (`batch.progress`: how many of its records
  are settled so far, by outcome). The run's trace says how far the run's deliveries have got, from the run's totals, at a
  pace that slows as the run goes on. A renewal that finds the lease taken over stops the worker: it stops sending, applies
  what it appended, and leaves the rest to whoever took the lease over. A renewal that fails on a database error is
  tried again until the lease would run out.
- **Append.** The worker's concurrent deliveries hand their completed steps and ended tries to the lease's journal,
  which writes them with group commit: one write carries whatever was handed over while the previous write was in
  flight, at most 500 entries. A delivery waits until its entry is written, so a completed step is in the ledger
  before the next step starts, and a try's `record.*` event reaches the listeners, and the run's trace its outcome
  line, only after its attempt is stored.
- **Apply.** A checkpoint applies the lease's events a thousand records to a transaction, in record order, each record
  taking its latest event. A completion settles the record as the try said (status, backoff, the pending state
  promoted, the target state) and releases it from the lease; a step keeps the step progress on the record while the
  record still holds the document the try was claimed with. A record another lease holds by then is left to that
  lease: the event is dropped, and the try's attempt stays in the history. The events go in the transaction that
  applies them.
- **Close.** A worker that ends its lease applies it and hands back what it still holds: records its batch never
  reached, or that a stop interrupted, go back to pending without charging the try, and the batch is settled (`done`
  or `failed` with its counts, or `queued` again after a stop). A worker whose lease was taken over applies what it
  appended, so a try it finished lands on a record no one else holds, and settles nothing else.
- **Recover.** Every claim of a flow first recovers the flow's leases that ran out. It takes each one over, a
  compare-and-swap on the lease row, so two recoveries never settle one lease and the stalled worker can no longer
  renew it. It applies what the stopped worker appended, hands the rest of its records back to pending with the try
  charged (`LastError` says the lease expired mid-attempt), and queues its batch again. A recovery that stops holds
  the lease for five minutes, and the next claim after that recovers it. The recovery also applies the flow's events
  that no lease will: a worker whose lease was taken over applies what it still appends when it closes, and one that
  stops before closing leaves those events behind. Events older than the five minutes whose lease row is gone are
  applied like any others.

The record table is therefore the read model of the deliveries: it shows what every lease has applied, which trails
what the workers have sent by at most one renewal. The attempts are written with each append, and the run's trace
carries the live progress.

A run recovered after its worker stopped (a control plane or node killed mid-delivery, whose run the reaper
requeued) finds its submission already planned, and the records its dead worker was sending still leased. The
platform runs one execution of a flow at a time, so such a lease belongs to nobody alive: the run waits until it
runs out, recovers it and sends the record, resuming after the steps the dead attempt had appended. Seen live, a recovered run that only passed over the submission finished `succeeded` with
the record left `delivering` and its change never recorded. A lease running out further ahead than the flow's own
`reliability.leaseSeconds` is left alone with a warning, because only a running worker renews a lease that far.

### Many nodes, one table

A fanned-out submission has every node staging, leasing, renewing and completing records in `osdu.Record` at once,
and a ledger of hundreds of millions of records must keep them from waiting on each other. What the ledger does on
SQL Server:

- **No statement writes more than 1,000 records.** SQL Server turns the row locks a statement holds on one index into
  a lock on the whole table once they reach 5,000, and a lock on the record table would stop every node of every flow
  while it lasted. Staging copies a batch into a staging table once and writes it a thousand records to a transaction.
  A batch's claim and hand-back and a cache change's marking first read the keys of the records they reach and then
  write them a thousand keys to a statement. A release or a redelivery of named records writes them a thousand to a
  statement; a release of every blocked record or of an issue's records, and a redelivery of every delivered record,
  read a page of a thousand keys from the index that holds them and write it before they read the next. Every index of the table ends with the
  table's key, so such a statement finds each record with one seek and locks no record it does not write. A lease
  applies its events a thousand records to a transaction, a worker's append carries at most 500 events with their
  attempts, and pruning deletes 4,000 attempts to a statement.
- **A worker does not write the record table while it delivers.** Keeping a lease is one row, however many records it
  holds, and what the worker learns goes to `osdu.RecordEvent`, whose ever-increasing key takes every node's appends
  at its end without touching a record. The records change when a lease is checkpointed, closed or recovered.
- **Staging locks only records that exist.** A slice finds the records it will update by key and update-locks them to
  the end of its transaction, and inserts the rest. It never locks a range of keys, so intakes of one flow, whose new
  keys interleave, do not wait on each other. A record another staging inserted after the slice looked is refused by
  the table's key; the slice is rolled back and runs again, finds the record and compares its work with it, so newer
  work is never overwritten. The claim check is one seek of the claim index per record.
- **Reads meet the writers rarely, and need no isolation level of their own.** A worker writing its lease row and
  appending to the event and attempt tables never touches the record table, so the reads above (a worker's, the
  planner's, the GUI's, the API's and the CLI's) meet only the short, chunked transactions of a claim, a checkpoint,
  a close or a recovery. The claim's reads are answered from `(FlowId, Status, NextAttemptUtc)` and
  `(FlowId, LastSubmissionId, Status, NextAttemptUtc)` alone. A read that needs a record and the lease holding it
  asks for both in one statement, so a lease closing mid-read cannot show a record as delivering with an expiry it
  no longer has.
- **A deadlock victim runs again.** SQL Server ends a deadlock by rolling one statement back. A staging slice, an
  append, an application slice, a claim and a lease statement are each run again, up to five times, after a short wait
  that grows with each try and differs between nodes; each is written so a second run does what the first would have.
  Anything else fails with the database's error.

Nothing else is maintained inside a record write. An indexed view once kept a flow's counts in a handful of rows and
SQL Server maintained it in the transaction of every write to the record table, so every node delivering a flow met
every other one on those rows: a serialization point that grew with the fleet rather than with the data. It is gone
(`RetireRecordCountView`, module version 1.8.0), and the statistics are counted from the records.

## Indexes and search

Listings are index-backed so the GUI answers in milliseconds at any estate size. Every key and every index that serves
a listing leads with `PartitionId` ([Partitions](#partitions)), so a partition's rows are one range of it; the few that
do not are seeks on an id that is unique across partitions, where the partition would only widen the key.

| Index | Serves |
| --- | --- |
| `Record` primary key `(PartitionId, FlowId, DeliveryKey)` | one flow's record, and key-ordered walks of one flow: a removal's key list, a keys selection's pages, the identity backfill's cursor |
| `Record (PartitionId, FlowId, Status, NextAttemptUtc) INCLUDE (LastSubmissionId, UpdatedUtc, PendingDocumentRef)`, `(PartitionId, FlowId, LastSubmissionId, Status, NextAttemptUtc) INCLUDE (UpdatedUtc)` | the worker's claim and its other reads (what is due next, the settled submissions with due work), for a flow and for one submission, from the index alone |
| `Record (PartitionId, FlowId, Status, UpdatedUtc)`, `(PartitionId, FlowId, LastSubmissionId, UpdatedUtc)` | a status's or a submission's records, most recent first, read in index order |
| `Record (PartitionId, FlowId, Label)`, `(PartitionId, FlowId, SourceKey)`, `(PartitionId, FlowId, TargetId)` | prefix search (`LIKE 'term%'`) on the three identity columns inside one flow |
| `Record (PartitionId, FlowId, UpdatedUtc)`, `(PartitionId, FlowId, LastDeliveredUtc)`, `(PartitionId, FlowId, LastVerifiedUtc)`, `(PartitionId, FlowId, LastVerifyOutcome)` | recency listings, the last delivery and the part-hour of the 24-hour count, the verify sweep, drift |
| `Record (PartitionId, DeliveryKey)`, `(PartitionId, TargetId)`, `(PartitionId, SourceKey)`, `(PartitionId, Label)` | the lookup across every flow of a partition: a delivery key (one record per flow that reads the row), and the prefixes the identity index does not hold yet |
| `Record (PartitionId, UpdatedUtc)`, `(PartitionId, Status, UpdatedUtc)` | the Records page's recent listing, of a partition and of one custody state, newest first |
| `Record (PartitionId, FlowId, SourceFileName, SourceRowNumber)`, `(PartitionId, SourceFileName)` | "which records came from this file", inside one flow and across a partition |
| `Record (PartitionId, FlowId, PlanRequestedUtc) WHERE PlanRequestedUtc IS NOT NULL` | the records the planner pages each run, so it stays as small as the backlog |
| `Record (PartitionId, WaitingFor) WHERE WaitingFor IS NOT NULL` | the records waiting for an id, released when the record holding it lands |
| `Record (PartitionId, FlowId, ProblemHash, UpdatedUtc) INCLUDE (Status, PendingSourceFileName) WHERE ProblemHash IS NOT NULL` | a flow's issues counted, an issue's records listed newest first and walked by its release, its files ([Issues](#issues)); as small as what is blocked |
| `Record (PartitionId, FlowId) WHERE ProblemHash IS NULL AND Blocked = 1 AND Status IN ('held', 'failed')` (`IX_Record_Unsorted`) | the blocked records not sorted into an issue yet, which the backfill reads and the issues count; empty but for that backlog |
| `Record (ClaimedTargetId) WHERE ClaimedTargetId IS NOT NULL`, unique, binary collation | one flow per OSDU id: the claim check staging runs, and the database's refusal of a second claim. An OSDU id names its partition, so it is unique without one |
| `Record (CacheSetId, DeliveryKey, FlowId) WHERE CacheSetId IS NOT NULL` | a cache change's rollout, in key and then flow order from its cursor; a cache is one partition's, and so is a change to it |
| `Record (LastSubmissionId, WorkBatch)`, `(LeaseOwner)` | a batch's records, the records one lease holds (its hand-back, and the expiry a listing shows); a submission and a lease token are unique across partitions |
| `RecordIdentity` primary key `(PartitionId, Token, FlowId, DeliveryKey)`, `(PartitionId, FlowId, DeliveryKey)`, `(PartitionId, FlowId, Token)` | the lookup by what a record is known by, in a partition and in one flow; a record's rows, replaced as a set when it is staged |
| `Submission (PartitionId, SubmissionId)` primary key, unique `(SubmissionId)`, `(PartitionId, FlowId, ReceivedUtc)`, `(PartitionId, FlowId, Status)`, `(PartitionId, FlowId, Kind, ReceivedUtc)`, `(PartitionId, ReceivedUtc)`, `(RunId)` | a submission by id from anywhere, a flow's submissions newest first, the open ones, the last of a kind, a partition's newest, a run's |
| `WorkBatch (PartitionId, SubmissionId, Index)` primary key, unique `(SubmissionId, Index) INCLUDE (Status)`, `(PartitionId, FlowId, Status, CreatedUtc)` | the batch claim, and the submission's batch list |
| `Lease (PartitionId, Token)` primary key, unique `(Token)`, `(PartitionId, FlowId, ExpiresUtc)`, `(SubmissionId, ExpiresUtc)` | a lease by token, the leases of a flow that ran out, for the recovery; the next expiry a run waits for, for a flow and for one submission |
| `RecordEvent (PartitionId, EventId)` primary key, `(LeaseToken, FlowId, DeliveryKey, EventId)`, `(PartitionId, FlowId, AtUtc) INCLUDE (LeaseToken)` | one lease's events in record order, a slice at a time, for its checkpoint; a flow's old events, for the recovery of those whose lease is gone |
| `Attempt (PartitionId, AttemptId)` primary key, `(PartitionId, FlowId, DeliveryKey, StartedUtc)`, `(SubmissionId, Outcome, Phase) INCLUDE (DeliveryKey)`, `(RunId, PartitionId, FlowId, DeliveryKey)`, `(StartedUtc)` | record timeline and the later attempt pruning looks for, the submission view and the counts a closing submission reads from the index alone, a run's records, pruning in start order across every partition; a reversal's listing of what a submission or a run delivered, a page of 1,000 at a time in index order |
| `ActivityRecord (PartitionId, FlowId, DeliveryKey, ActivityId)` primary key | the releases, redeliveries and reversals that reached one record, for its history |
| `Reversal (PartitionId, ReversalId)` primary key, unique `(ReversalId)`, unique `(PartitionId, FlowId, SourceKind, SourceId)`, `(PartitionId, FlowId, RequestedUtc)` | a reversal by number, the one reversal of a source, a ledger's reversals newest first |
| `ReversalItem (PartitionId, ReversalId, DeliveryKey)` primary key, `(PartitionId, ReversalId, State, DeliveryKey) INCLUDE (Outcome)`, `(PartitionId, ReversalId, Outcome, DeliveryKey)` | a reversal's listing added to in key order, its next page to settle and its counts by state and outcome from the index alone, its records by outcome a page at a time |
| `Activity (PartitionId, ActivityId)` primary key, unique `(ActivityId)`, `(PartitionId, FlowId, StartedUtc)`, `(PartitionId, FlowId, DeliveryKey, StartedUtc)`, `(PartitionId, Kind, StartedUtc)`, `(PartitionId, Actor, StartedUtc)`, `(PartitionId, StartedUtc)`, `(PartitionId, Idle, StartedUtc)`, `(SubmissionId)`, `(RunId)` | an activity by id, the audit views and their filters in a partition, the trail without its idle runs and the count of them, one record's interventions, a submission's and a run's |
| `Retrieval (PartitionId, RetrievalId)` primary key, unique `(RetrievalId)`, `(PartitionId, FlowId, StartedUtc)`, `(PartitionId, FlowId, Status, StartedUtc)`, `(RunId)` | a retrieval by id, a retrieval flow's runs, the watermark chain (the last done run), the run's row |
| `AssertionRun (PartitionId, AssertionRunId)` primary key, unique `(AssertionRunId)`, `(PartitionId, FlowId, StartedUtc)`, `(RunId)` | a report by number, an assertion flow's runs newest first, the platform run's report |
| `AssertionResult (PartitionId, ResultId)` primary key, unique `(ResultId)`, `(PartitionId, AssertionRunId)`, `(PartitionId, FlowId, TestName, AssertionRunId)` | a run's results, and each test's latest result and history, one seek per flow, for the boards, the matrix and a test's trend |
| `SourceWatermark (PartitionId, FlowId, Scope)` primary key | a flow's watermark per scope |
| `Ledger (PartitionId, FlowId)` primary key, unique `(FlowId)`, `LedgerPartition (Name)` unique | a ledger's partition, a partition's ledgers, a partition's number |
| `UpdateTag (Scope, Status)` | a partition's cache changes waiting for a decision, counted for the switcher and the Partitions page |
| `Run (SubmissionId)`, `Run (ResultSubmissionId)`, `Run (PipelineId, Operation)` | a submission's runs, a flow's runs by operation |

The attempt table's primary key and start-time index, and the record event table's primary key, grow at their end as
every drain appends; they are set to `OPTIMIZE_FOR_SEQUENTIAL_KEY` where the server has it.

A search term that parses as a UUID matches the delivery key exactly (in a flow's listing, that flow's record; in the
lookup across every flow, each flow's record of the row); anything else is a prefix over label, source key and target
id, and a lookup across flows matches each record on its own values. A slower "contains" mode exists for the rare
case, and the API names it explicitly.
`SourceKey` is capped at 400 characters so it fits an index key.

### Bounds

Every listing reads a bounded part of the ledger, however many records a flow holds:

- A listing counts no further than 25,000 matches, the removal selection limit. Up to that the total is exact; past it
  the API returns `totalCapped: true` with the total as a floor, and the GUI shows "25,000+". Pages reach the first
  25,000 records of the order; a page past them is refused with a 400 that says to narrow the filter.
- Records are listed most recently updated first, ties broken by delivery key, so pages partition a batch that one bulk
  write stamped with a single update time.
- A prefix search takes at most 25,000 candidates from each identity column's index, in index order, and applies the rest
  of the filter to those. When a column runs into that bound while another filter narrows the listing, the count is
  reported as a floor.
- A contains term has no index. It runs only when the rest of the filter leaves at most 100,000 records, counted first
  and no further than that; a broader filter is refused with a 400 that names the ways to narrow it.
- The lookup across every flow (the search box) takes at most 1,000 candidates from each identity index and reports a
  larger match as "1,000+".

## Statistics

A flow's statistics (`GET /api/v1/delivery/flows/{id}/stats`: the records by status, the drifted ones, the deliveries
of the last 24 hours) are counted from the records, through the record table's status indexes, on every database the
module runs on. The counts are derived from the ledger and exact, as they have to be: nothing keeps a running total
beside the records.

They were read from an `osdu.RecordCount` indexed view until module version 1.8.0. It was maintained by SQL Server
inside the transaction of every record write, which is what made it cheap to read and what made every node delivering
one flow queue behind every other on the few rows holding that flow's counts. Reads then had to avoid those rows,
which is why the ledger read under snapshot isolation and why the database had to allow it. Counting from the records
costs a status index range per call and removes all of that.

## Retention

Attempts grow per delivery try. `POST /api/v1/delivery/ledger/prune` (admin scope) with `olderThanDays`
deletes older attempts while keeping the latest of every flow's record, so a record's last outcome is always explainable.
It deletes 4,000 attempts per statement, oldest first, each statement its own short transaction, so a prune of years of
history never holds a long lock on the table every drain appends to, and never enough row locks for SQL Server to lock
the whole table instead; an attempt goes only when a later attempt of the same record exists, which is one seek of the
record's timeline.
The same pass clears the captured run log of settled activities past the cut-off, and removes whole the assertion runs
past it whose every result a later result of the same test superseded, and answers the three counts
(`attemptsPruned`, `activityLogsCleared`, `assertionRunsPruned`): a run log is up to 200,000 characters written once per
run, which grows without bound, while the audit row itself, its flow, kind, actor, times, parameters, outcome and summary,
is never deleted. An assertion run goes with all its results in one transaction, so a report kept is whole, and a run
holding a test's latest result stays however old. Partition either table by time in the model if volume demands it (see
[decisions/0005-ledger-retention.md](decisions/0005-ledger-retention.md)).

The identity index is not pruned, and does not grow with activity: it grows with the number of records and their
values, so it is rewritten by a staging and only ever holds what the current records are known by. Nothing deletes a
record row, so no identity row is ever orphaned.

For the analytical view, snapshot the tables into Delta when one is needed. The ledger is a live status store, not a
reporting table.

## Provisioning

The ledger is the module's own schema: `osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs` declares the entities,
`DeliveryModel.Configure` maps them into schema `osdu`, and `OsduDbContext` owns them. Every model change ships with
its EF migration, with its own history table (`[osdu].[__EFMigrationsHistory]`) and its own schema version
(`[osdu].[SchemaVersion]`, which also records the minimum SQLFlow catalog migration it requires), so the ledger is
upgraded in place without touching SQLFlow's catalog.

`LedgerPerFlow` (module version 1.2.0) keyed the record table by flow and delivery key. It gives every existing
attempt the flow of its record (or of its submission, when the record is missing), claims the OSDU id of every record
that queued or delivered a document, and completes an interrupted rollout's cursor with its record's flow. It stops,
naming the count and how to find them, when an attempt belongs to no record and no submission or when two records hold
one OSDU id; a failed migration leaves the ledger as it was. Going back down is refused while any delivery key has
records in more than one flow.

It is ordered for a ledger of hundreds of millions of records: the backfills run while the record table is still keyed
by delivery key and before the table is rebuilt, the record table's nonclustered indexes are dropped before its
clustered key changes and built once after (rather than rebuilt by the key drop and again by the key build), and the
attempt table's two ever-increasing indexes are set to `OPTIMIZE_FOR_SEQUENTIAL_KEY` where the server has it (SQL
Server 2019 and later, Azure SQL), because every drain appends to them at once. It still rewrites the record table and
updates every attempt in one transaction, so on a large ledger it needs log space for both and runs while no host is
up (the hosts refuse to start against a pending migration anyway).

`CoverWorkerReads` (module version 1.4.0) lets the worker's reads answer from an index alone (see
[Many nodes, one table](#many-nodes-one-table)). It rebuilds `(FlowId, Status, NextAttemptUtc)` in place with its
included columns (`DROP_EXISTING`, so the table always has the index and the rebuild reads the old index rather than
the table) and builds `(FlowId, LastSubmissionId, Status, NextAttemptUtc)`. Both are index builds over the record
table, sized by its row count, and run while no host is up.

`LeasesAndRecordEvents` (module version 1.5.0) moves the worker's writes off the record table (see
[Leasing](#leasing)). It creates `osdu.Lease` and `osdu.RecordEvent`, and turns every lease a stopped worker left
behind into a lease row, so the next claim of its flow recovers it like any other: a batch's lease keeps its batch, run
and expiry, and the records a claim outside the batches leased make one lease per token, expiring with the last of
them. It then drops the expiry columns of `osdu.Record` and `osdu.WorkBatch` with the indexes that served the old
lease sweep, and rebuilds the claim's two covering indexes in place without the expiry. Going back down is refused
while `osdu.RecordEvent` holds an event no lease has applied; a revert puts each lease's expiry back on its batch and
its records. The rebuilds are index builds over the record table, sized by its row count, and run while no host is up.

`DeliveryInterfaces` (module version 1.6.0) creates `osdu.Interface`, the read model of sources and interfaces (see
[`osdu.Interface`](#osduinterface-the-sources-and-interfaces-the-repositories-declare)). It adds a table and changes
nothing that exists: the ledger identities of existing flows are unchanged, because a flow in the single form keeps the
id of its own name. The rows are written by the next repository sync, and by the control plane once when it starts.

`RecordWaits` (module version 1.7.0) lets a record wait for a record it refers to (see the record lifecycle above). It
adds two columns to `osdu.Record`: `PendingReferences`, the OSDU ids the pending document refers to, written and
cleared with that document, and `WaitingFor`, the id a waiting record waits for, with a filtered index that finds the
waiters of an id when it lands. It adds a `Waiting` count to `osdu.Submission` and `osdu.WorkBatch`. Every column is
added empty, so an existing ledger takes the migration without a rewrite; the index is built over the record table,
sized by its row count, and runs while no host is up.

`RetireRecordCountView` (module version 1.8.0) drops the `osdu.RecordCount` indexed view. SQL Server maintained it
inside the transaction of every write to the record table, and it grouped a flow into a handful of rows, so every node
delivering that flow met every other one there. The statistics are counted from the records instead, and going back
down recreates the view. It holds no data of its own, so nothing is lost either way.

`ActivityIdle` (module version 1.16.0) adds `Idle` to `osdu.Activity` and the index `(PartitionId, Idle, StartedUtc)`
the audit trail opens on. A run written before the column is marked idle from what the ledger still says of it: a
`deliver` or `intake` run that completed, whose submission planned, delivered, held, blocked and failed nothing and leaves
nothing waiting, and under whose run id no attempt was made. Every other row stays as it was, including a run that held
records its submission has since let go of, which the ledger no longer shows. It is one update over the activity table,
one row per run and intervention, and one index build over it; going back down drops both.

`DimensionLabels` (module version 1.18.0) adds to the dimension tables what keys and values need to stay apart:
`LabelJson` on `osdu.Dimension`; `Labelled`, `Unlabelled` and `LabelQueries` on `osdu.DimensionRun`; and `Label`,
`LabelFrom` and `Filter` on `osdu.DimensionValue`, the key's label, the record it was read from and the search filter
finding exactly its records. Every column is nullable or defaults to zero, so a dimension built before it keeps every key
and value as it was, with no label or key filter until its next build writes them. It adds columns only; going back
down drops them.

`DimensionAttributes` (module version 1.19.0) creates `osdu.DimensionAttribute`, one row per key and attribute keyed by
the partition, the dimension, the key and the attribute's name, with the index `(PartitionId, DimensionId, Name, Value)`
an attribute lookup seeks; and adds `AttributesJson` to `osdu.Dimension`. It holds nothing until a dimension declaring
attributes is built. Going back down drops the table and the column.

`DimensionCollectedAttributes` (module version 1.20.0) keys `osdu.DimensionAttribute` by the value as well, so a key
holds several values of a collected attribute; adds `Records` to it, how many of a key's records hold a collected value;
and adds `CollectedJson` to `osdu.Dimension`. Every row it finds keeps its value, with no records, and needs no rebuild.
Going back down first removes the collected values (and any second value of one attribute of a key), then restores the
earlier key and drops both columns.

`DimensionCollectedTexts` (module version 1.21.0) creates `osdu.DimensionCollectedText`, one row per collected attribute
and text keyed by the partition, the dimension, the attribute's name and the text's SHA-256, with the index
`(PartitionId, DimensionId, Name, Value)` a search seeks. A build before it kept the texts in `CollectedJson`, which it
clears, so a search picking a collected value asks for the dimension to be built again rather than finding no text; every
key keeps its values. Going back down drops the table.

`DimensionTables` (module version 1.22.0) makes dimensions tables joined by numbers. It creates
`osdu.DimensionAttributeName` and fills it from the names the attribute rows hold, each told collected or not by what
its rows held; rebuilds `osdu.DimensionAttribute` and `osdu.DimensionCollectedText` around an identity of their own
(`AttributeValueId`, `TextId`) with `AttributeId` in place of the name, keeping every row, with unique indexes on what
their keys were (`(PartitionId, DimensionId, ValueId, AttributeId, Value)`, `(PartitionId, DimensionId, AttributeId,
TextHash)`) and the indexes a lookup by attribute and value seeks; adds `Dimension.TableName` with its index; and adds
`IX_DimensionValue_PartitionId_DimensionId_ValueId`, which reads a dimension's keys in the order they arrived as one
range of the dimension alone. The two tables are rebuilt, so it takes as long as they are large. It creates no
`osdu.dim_...` table: builds do. Going back down drops every table a dimension names, gives each attribute row its name
back, restores the earlier keys and indexes, and drops the new table and column.

`RecordProblems` (module version 1.24.0) groups blocked records by issue ([Issues](#issues)). It adds
`Record.ProblemHash` and `RecordEvent.ProblemHash`, both empty, so neither table is rewritten; builds the issue index
over the record table, which holds nothing until records are sorted, and `IX_Record_Unsorted`, which holds every record
blocked when it runs, for the control plane's backfill to sort; and creates `osdu.ActivityRecord`. The pattern is made
in code, so no statement of the migration computes it. The two index builds read the record table, sized by its row
count, and run while no host is up. Going back down drops the table, the indexes and both columns.

`DimensionColumnNames` (module version 1.23.0) lets a dimension's table name its key's and its value's columns after
what the dimension reads ([dimension-plan.md](dimension-plan.md), The table). It adds `Dimension.KeyColumn` and
`Dimension.ValueColumn`, and records `key` and `value` on every dimension whose table a build had made, which is what
those tables hold. It changes no `osdu.dim_...` table: each dimension's next build renames the two columns where they
are, keeping every row and its `id`, and until then the table is read under the names recorded. Going back down
renames the columns of every table builds have renamed back to `key` and `value`, which is what the code before it
reads them by, and drops the two columns.

`LedgerPartitions` (module version 1.14.0) keys the ledger by partition (see [Partitions](#partitions)). It creates
`osdu.LedgerPartition` and `osdu.Ledger` and fills them from what the ledger already says, before anything else changes:
every ledger identity any ledger table holds gets a directory row, in the partition `osdu.Interface` describes it in when
that is one partition, else the one partition its records' OSDU ids name, else unassigned (0), which the ledger's next
run adopts. It then drops every nonclustered index of the ten ledger tables, adds `PartitionId` to each and fills it from
the directory, stops (naming the table and the count) if a row belongs to no directory row, makes the column required,
rebuilds each primary key with the partition first, and builds every index once, partition first, rather than
rebuilding each as the keys change. It sets the attempt and event tables' ever-increasing keys to
`OPTIMIZE_FOR_SEQUENTIAL_KEY` again, and adds `UpdateTag (Scope, Status)`. It rewrites every ledger table, so on a large
ledger it needs log space for the largest of them, and runs while no host is up; a failed migration leaves the ledger
as it was. Going back down restores the earlier keys and indexes and drops the directory.

`RecordPurges` (module version 1.27.0) creates `osdu.PurgedRecord`
([Deleting a removed record from the ledger](#deleting-a-removed-record-from-the-ledger)), keyed by the partition first,
indexed on `(PartitionId, FlowId, DeliveryKey)` for a record's page and on `(PartitionId, TargetId)` for a lookup by OSDU
id, its OSDU id compared byte for byte as a record's claim of it is. No other table changes; it is created empty, and going
back down drops it.

`RecordReversals` (module version 1.26.0) creates `osdu.Reversal` and `osdu.ReversalItem` ([`osdu.Reversal` and
`osdu.ReversalItem`](#osdureversal-and-osdureversalitem-what-a-reversal-did-to-each-record)), both keyed by the partition
first. The custody state `reverted`, the attempt outcome `restored` and the phase `reverse` are values of existing
columns, so no other table changes and nothing is rewritten. A delivery made before it records no `replaced` version, so
a reversal of a run delivered before it reads what OSDU held from the record's earlier attempts, or from OSDU's version
list where those were pruned. Both tables are created empty; going back down drops them.

The ledger asks nothing of the database but its own schema. 1.5.0 to 1.7.0 read every listing, wait and claim in a
snapshot transaction, so the database holding the `osdu` schema had to allow snapshot isolation, and one that did not
stopped every node from claiming any work; that requirement is gone ([Many nodes, one table](#many-nodes-one-table)).
The **source** database a delivery flow reads is a different matter: a record and its child rows come back as several
result sets, and a flow reads them as one instant unless it says otherwise ([documents.md](documents.md), `isolation`).

The control plane applies pending migrations on start, and `sqlflow db migrate --db <ref>` does it by hand. Both
hosts and `sqlflow db status` refuse to run against pending migrations, a database newer than the code, or a catalog
older than the module requires, naming the migration or version. The module runs on SQL Server alone, and so do its
tests: every suite's database is built by the same migrations a deployment applies.
