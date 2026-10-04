# Plan: one validation module

Every judgement of whether a record meets its schema is made by one module, and every place that judges a record calls
it: the gate before a record is sent, a plan run and a record preview, Check values, the `conforms` assertion and the
explorer. A flow says what the gate does with a record that breaks its schema: send it and record why (`report`), or
hold it (`enforce`). Every verdict the gate reaches is written to the ledger with the schema it was checked against, so
whether a record was validated, when, against which schema and with what result is answered from its history alone.

Each stage lists what it changes and the tests that close it. A stage is finished only when those tests pass, SQL
Server suites included; nothing moves to the next stage before that. All work is in `osdu/`: nothing in `sqlflow/`
changes, and no OSDU table changes without its migration.

Line numbers below are at `b105e42`. Other change sets edit the same files, so go by the type and method named.

## Status

| Stage | State |
| --- | --- |
| 1. The module | Done: `Validation/SchemaRules`, `SchemaWalk`, `RecordValidator`, `ValidationVerdict`, `ReferenceResolver`, `ValidationTally`; Check values and the `conforms` assertion run on it |
| 2. The explorer validates | Next |
| 3. How well what OSDU holds conforms | With stage 2: the explorer's list counts by rule and path |
| 4. The route rules | Decided: they stay at their call sites (see Decisions) |
| 5. The flow key, the gate and the ledger | Done: `target.validation`, `ValidationGate`, migration `RecordValidation` (module 1.25.0) |
| 6. The verdict where people look | After stage 2 |

## Why

A record is judged in six places today, each with its own outcome, and only a failure leaves a trace:

| When | What is checked | Outcome |
| --- | --- | --- |
| Flow and mapping load | Unknown keys, whether the route can deliver the kind (`RouteChecks`) | The run fails |
| Preflight, before any render | The mapping against its pinned template, the cache, the columns, the fixtures (`Preflight.Check`) | The run fails |
| Render, per record | Conversion to the variable's type, dates, built ids against pattern and relationship, required `data` properties, EDS rules (`Planner.FinishRecordAsync`) | Held, no document kept |
| Claim | A reference to a record of the ledger that has not landed | Waiting, released when it lands |
| Before send | `target.verifyReferences: storage`; Wellbore DDMS record rules; payload shape and ceilings | Held, document kept |
| On demand | The full JSON Schema rules (`TemplateValueRules`): Check values over the ingestion rows, the `conforms` assertion over what OSDU holds | Reported, never held |

A delivered record carries no statement of what it was checked against, so "no hold" is the only sign it passed. The
rules that most often break against OSDU's schemas (`pattern` on text that is not a built id, `enum`, lengths, ranges,
required properties inside objects and list items, `additionalProperties: false`) never stop a send.

The manifest route shows why it matters. OSDU's ingestion workflow validates each entity against its schema and checks
its references through Search, and drops the entities that fail, logging their ids in Airflow and failing the run only
when a whole section goes (osdu/specs/workflows/INTEGRATION.md, Failure reporting). A record that breaks its schema is
then lost without a hold in the ledger.

## The principle

**One module judges a record. Its callers decide only what to do with the verdict.**

| Caller | When | What it does with the verdict |
| --- | --- | --- |
| The gate, in `DeliveryWorker` | Immediately before a document is sent, on every try | Writes it to the attempt and the record; holds or sends as `target.validation` says |
| A `plan` run, a record preview | When asked | Shows what the gate would decide, writes nothing |
| Check values | When asked, over the ingestion rows | Counts by variable and rule, as now |
| The `conforms` assertion | On an assertion flow's runs, over a query | Passes or fails, now with counts by rule and path |
| The explorer | When a reader asks, for a stored record or the records in view | Shows the problems on the record |

Two kinds of check stay where they are, because they answer other questions:

- **The render holds.** A value that cannot be converted to its variable's type, a required `data` property rendered
  empty, a built id that breaks its pattern or relationship. These are failures to produce the record, not findings
  about a record that exists, and they hold in every mode, as today.
- **Whether a referenced record exists.** A document does not carry what it refers to, so the gate asks one resolver
  (`ReferenceResolver`): the ledger first (a record it holds and has not delivered is the claim's to wait for, as
  today), then OSDU's storage service under `target.verifyReferences: storage`, which then decides every id the ledger
  does not hold and still holds a record whatever the mode, as that setting always has; else the cache version the flow
  renders with, for the entity types a cache flow captures from the partition (reference data, mostly). An id of a type
  the cache does not capture is not checked, and says so. Whether an id has the entity type its property allows is a
  rule of the schema (`x-osdu-relationship`) and is the walk's.

## What an author and an operator see when this is done

A flow says what the gate does:

```yaml
target:
  protocol: storage
  verifyReferences: storage
  validation:
    mode: enforce        # report (the default) | enforce
    unverified: send     # send (the default) | hold
```

- `mode: report`: every document is validated before it is sent and its verdict recorded. Nothing is held that is not
  held today.
- `mode: enforce`: a document that breaks a rule of its schema is held with its document kept, under an issue that
  names the rule and the path.
- `unverified: hold`: a document some part of which could not be checked is held too ([The verdict](#the-verdict) lists
  why a part cannot be checked).

In this project a pipeline is a flow, so the setting is a key of the flow, read wherever `target.verifyReferences` is
read today: a flow document, and a source document's interfaces. It is synced into the catalog with the flow and changed
through the repository, so every change is reviewed and dated. It is not a render key (design.md section 9.3): changing
it renders nothing again and redelivers nothing.

A record's history shows the verdict on every attempt:

```text
Attempt 3   2026-10-12 09:14   held   validation
  Checked against osdu:wks:master-data--Wellbore:1.2.0 (template 9f3c41d07a2b88e1), 214 rules
  invalid: 2 problems
    data.FacilityName                            maxLength  312 characters, longer than the 256 the template allows
    data.VerticalMeasurements[].VerticalCRSID    pattern    'EPSG:5714' does not match ^[\w\-\.]+:reference-data\-\-...
  references: 3 in the ledger, 1 in OSDU, none missing
```

A run's trace says once what the gate found, whatever the run's size:

```text
Validated 12,430 documents against osdu:wks:master-data--Wellbore:1.2.0: 12,100 valid, 300 invalid (held: enforce),
30 unverified (sent). Most broken: data.FacilityName maxLength (210), data.Status enum (90).
```

The explorer's record view has **Validate**: the record (any version) against the schema the partition's Schema
service holds for its kind, or a saved template of the reader's choice. Each problem is a row (path, rule, value, what
is wrong) that opens its element, each failing field carries a mark with the explanation in its tooltip, and the
references are looked up in OSDU. **Validate these records** does the same for the records in view, up to a cap, and
counts them by rule and path. The question "how much of what OSDU holds meets what OSDU expects" is answered there for a
list, and by an assertion flow's `conforms` test for a whole kind.

## The verdict

```text
outcome       valid | invalid | unverified | notValidated
schema        kind, template content version, where it came from (saved template, Schema service)
rules         how many rules were checked
problems      [{ path, rule, message, value }], at most 50 listed, the total counted exactly
unverified    [{ path, rule, why }], at most 20 listed, the total counted exactly
references    in the ledger, in OSDU, missing (each id with its property), or not checked
module        the version of the rules, so verdicts from different releases can be told apart
```

- **valid**: every rule that applies was checked and met.
- **invalid**: at least one rule is broken. A part that could not be checked does not change that.
- **unverified**: no rule is broken, and some part could not be checked: a pattern neither regular expression dialect
  reads, a pattern match that ran out of time, a list longer or a value deeper than the walk reads, a schema node whose
  reference does not resolve, the document's time budget spent, or an error of the module itself.
- **notValidated**: the try sent nothing of the document (a payload-only send, a removal).

A problem names the path as the record inspector does (`data.VerticalMeasurements[2].VerticalCRSID` for one item,
`[]` when it groups items), the JSON Schema keyword it breaks (or `relationship` for `x-osdu-relationship`), and the
value clipped to 200 characters and redacted as every stored error is.

## Edge cases

Each row is a test of the stage that implements it.

| Case | Behaviour | Stage |
| --- | --- | --- |
| The pinned template is not saved | The gate is never reached: the preflight refuses the run, as now. In the explorer: said so, with the Schema service or another saved version offered | 1, 2 |
| A queued document was rendered under an earlier mapping version | Validated against the template of the document's own kind and render context, which is what OSDU receives, not the flow's current pin | 5 |
| A Schema service schema refers to other schemas | Followed and bundled as the data definitions are bundled, each schema read once per read; a cycle or a reference that does not resolve leaves that part unverified, named | 2 |
| A pattern neither ECMAScript nor .NET reads | That rule is unverified at that path; the preflight warns when the mapping loads | 1 |
| A pattern that backtracks | Each match runs under the existing timeout (`IdValues.PatternTimeout`); a timeout makes the rule unverified, never invalid and never a hang | 1 |
| A document that takes too long overall | A fixed budget per document; past it the rest is unverified and the verdict says how far the walk got | 1 |
| A list longer than `MaxItems`, a value deeper than `MaxDepth` | The rest is unverified and counted. Today the walk stops silently and the value reads clean | 1 |
| More problems than are listed | The outcome is invalid, the list is capped, the total is exact | 1 |
| `oneOf` | Read as `anyOf`, as now, since OSDU's forms overlap; the problem quotes what the first form found | 1 |
| A `format` the module does not know | Not asserted, as JSON Schema allows; named once in the verdict's schema notes, not per record | 1 |
| `null` in a stored record | Allowed only where the type includes `null`. Today a null value is skipped | 1 |
| A draft-04 boolean `exclusiveMinimum` | No bound, as now | 1 |
| Text length | Counted in Unicode code points, as JSON Schema counts | 1 |
| A number beyond the decimal range | Compared as a double; `integer` judged by its whole value | 1 |
| A reference to a record the ledger holds and has not delivered | The claim keeps the record waiting, as now; the gate validates it when it is taken | 5 |
| A reference no ledger record holds, `verifyReferences: none` | Not looked up; the verdict says "not checked" | 5 |
| A reference storage does not hold (soft-deleted included), `verifyReferences: storage` | Held, as now; the verdict lists it under references | 5 |
| Storage cannot be reached for the reference check | The group is tried again later, as now; no verdict is written for that try | 5 |
| An id of an entity type its property does not allow | The `relationship` rule: invalid | 1 |
| The flow's mode changes while documents are queued | The gate applies the mode the flow has when the document is sent. Switching to `report` releases nothing held under `enforce`; a release by issue does | 5 |
| An operator releases a record `enforce` held | The release accepts that verdict for that document: the next send goes, its attempt says invalid and accepted, by whom and under which issue. The acceptance is bound to the document's metadata hash, so a document rendered differently is judged again | 5 |
| The source row is corrected | The fingerprint moves, the record is planned again (as now), and its new document gets a new verdict | 5 |
| Documents queued before this change | Validated by the gate like any other; nothing to migrate | 5 |
| A retry | Each try validates again (the same document gives the same verdict) and each attempt carries its own | 5 |
| A payload-only send, a removal | `notValidated` for the template's rules; the route's rules apply where they apply today | 4, 5 |
| A fan-out run | Each worker validates its share; the compiled schema is shared within a process, keyed by template content version, and the number kept is bounded | 1, 5 |
| A defect in the module throws | Caught per document: unverified, naming the error, logged with the record once per run; never a failed run, never a silent valid | 1 |
| A run of millions of records | The schema is compiled once per template version; no schema node is merged or copied per record; the run trace stays one summary per template, never a line per record | 1, 6 |
| The dspdm route | Validated against the DSPDM business object template its mapping pins; DSPDM's own value limits stay the route's rules | 4 |
| The manifest route | `enforce` holds what OSDU's ingestion would drop; under `report` the verdict explains a drop | 5 |
| Wellbore DDMS and EDS rules | Stay at their call sites and hold in every mode, as today, since the service refuses what breaks them | 4 |
| An id OSDU holds nothing under, in the explorer | The near ids, as now | 2 |
| A kind neither the Schema service nor the saved templates know | Said so; nothing is validated | 2 |
| Validating the records in view | At most 1,000 records, read through storage's batch read, cancellable, counted by rule and path; a whole kind is an assertion flow's `conforms` test | 2 |

## Before starting

- Build `b105e42` with `-t:Rebuild` and run every suite, SQL Server suites included, to have a baseline. The
  `SqlServerChainTests` fan-out tests have failed here before this work; compare against the baseline, do not chase them.
- Other sessions commit in this repository. Commit only this plan's paths, and check `git log` before each commit.
- Live OSDU checks follow [Live checks to approve](#live-checks-to-approve) and run only once approved.

## Stage 1: the module

**Changes**

- `Validation/RecordValidator` takes a document, a compiled schema and the route's rules, and returns the verdict. It
  walks the whole document (the root, `data` and everything under it) with the rules `TemplateValueRules` applies
  today, so nothing a check finds now is lost.
- `Validation/CompiledSchema` is built once per template content version: each schema node's `allOf` merged and
  `$ref` followed once and kept, each pattern compiled once. Today `SchemaSnapshot.EffectiveOf` merges and deep-copies a
  node's `allOf` branches on every call, which is once per value per record. The process keeps a bounded number of
  compiled schemas, keyed by content version.
- The walk reports what it could not check (lists past `MaxItems`, depth past `MaxDepth`, unreadable and timed-out
  patterns, unresolved references, the spent budget, its own errors) as unverified, and judges `null`.
- `TemplateValueRules.Check` becomes the per-variable entry of the same walk, so `ValueTally` (Check values) is
  unchanged for its callers. `ConformsEvaluator` calls `RecordValidator` instead of its own walk.

**Tests**

- One test per keyword and outcome: `type`, `format` (each known format), `pattern`, `enum`, `const`, the lengths,
  the bounds, `multipleOf`, the item counts, `uniqueItems`, `required` at every depth, `additionalProperties`,
  `oneOf`/`anyOf`, `relationship`.
- Real schemas from the OSDU data definitions (Wellbore, WellLog, a reference data type, a dataset type), with a valid
  record of each and records breaking each rule.
- Every row of [Edge cases](#edge-cases) marked stage 1.
- A compiled schema merges each node once, however many documents it validates; validating from several threads at
  once gives the same verdicts as one thread.
- Check values and the `conforms` assertion give the same results as before on their existing suites.

## Stage 2: the explorer validates

**Changes**

- A read of the Schema service (`GET /schema/{id}` of its OpenAPI description), with the schemas it refers to followed
  and bundled. The description does not say whether references come resolved; the first approved live check settles it
  and the bundling covers both.
- `POST /api/v1/delivery/explorer/validate` (operate): a record id, an optional version, and the schema (`osdu`, the
  default, or `saved` with a kind version). It runs as the explorer's other reads do (`delivery-explore`): the record
  through `ReadRecordOperation.ReadBackAsync`, the schema, then one batched storage lookup of the ids the record
  refers to (`StoragePresence.PresentAsync`). It answers the verdict.
- `POST /api/v1/delivery/explorer/validate-list` (operate): a kind and a query as the explorer sends them, at most
  1,000 records, read through storage's batch read. It answers counts by rule and path with example records.
- The record inspector (`OsduRecordInspector.tsx`, shared by the explorer and the record pages' OSDU tab) gains
  Validate: the summary line with the schema used, the problem rows that open their element, a mark on each failing
  field with the explanation in its tooltip, and the schema picker. The explorer's list gains Validate these records.
- The explorer reads only OSDU for this (the record, the Schema service, storage), unless the reader picks a saved
  template, which the explorer already reads for its element queries.

**Tests**

- Endpoint tests against fake OSDU services: a valid record, an invalid one, a version, a record OSDU does not hold, a
  kind the Schema service does not know, a schema that refers to others, a reference cycle, a reference storage does
  not hold, the cap on a list, a cancelled list.
- GUI tests against a mocked API: the summary, a problem row opening its element, the marks, the picker, the list
  counts. Both themes rendered.

## Stage 3: how well what OSDU holds conforms

**Changes**

- The `conforms` assertion's result gains counts by rule and path, with the values behind them and example records, as
  Check values gives for the ingestion rows. Its `maxRecords` (up to 1,000,000) and `sample` keep their meaning.
- The explorer's list validation says, when the list is cut at its cap, how to check the whole kind with an assertion
  flow.

**Tests**

- An assertion run over fake records breaking several rules gives the counts by rule and path; a sample says it is one;
  the result stays within its size bound however many records fail.

## Stage 4: the route rules

The Wellbore DDMS record rules (`WellboreDdmsRules`), the External Data Services rules (`EdsRecordRules`) and the DSPDM
value limits stay where they hold today. They are not rules of a template: they are what one service refuses, they hold
in every mode, and the EDS rules hold before a document is queued while the Wellbore DDMS rules need the payload the
protocol reads. Running them in the module as well would evaluate each twice for the same answer. A record they hold
carries their reason as its error and issue, as before.

## Stage 5: the flow key, the gate and the ledger

**Changes**

- `target.validation` (`mode`, `unverified`) in `TargetDefinition`, read by `DeliveryDocumentLoader` wherever
  `target.verifyReferences` is read (a flow and a source's routes), with unknown values refused at load, the census
  keys and the language server's schema updated.
- The gate in `DeliveryWorker`, in the step that runs `CheckReferencesAsync` before anything is sent: each document
  whose metadata is to be written is validated against the template of its own kind and render context; under
  `enforce` an invalid document is held (`RecordHeldException`, document kept), under `unverified: hold` so is an
  unverified one; the reference check runs for the documents still to be sent and its outcome joins their verdicts.
- A hold's error starts `validation:` and names the rule and the path, with the values quoted, so `ProblemSignature`
  groups the records broken the same way into one issue.
- The `osdu` model, with its migration, designer file and snapshot, and a schema version step:
  `osdu.Record` gains `ValidationOutcome`, `ValidationProblems`, `ValidatedUtc` and `AcceptedMetadataHash`, with an
  index on `(FlowId, ValidationOutcome)` for the counts. The full verdict goes into the attempt's `ResultJson`, capped
  at 16 KB.
- A release of a blocked record that still holds its rendered document (by name, every blocked record, or an issue's)
  sets `AcceptedMetadataHash` to its pending document hash, in the one statement every release runs; the release
  activity names the operator and the issue, as now. The gate sends a document whose hash is accepted, with the verdict
  marked accepted; the storage check of `target.verifyReferences` still holds it, as a release never bypassed that.
- The run's progress lines count the verdicts (`ValidationTally` on the run's `RunTrace`), and each drain ends with one
  line per template it validated against: counts by outcome, holds, acceptances and the five rules broken most often.

**Tests**

- SQL Server suites: the migration applies on a database at the previous version and refuses when out of order;
  counts by outcome come from the ledger alone.
- The gate under each mode and each `unverified` setting, with a valid, an invalid and an unverified document.
- Every row of [Edge cases](#edge-cases) marked stage 5: a mode change while queued, release and acceptance, a
  re-render after acceptance, a document queued before the change, a retry, a payload-only send, a fan-out run, a
  document rendered under an earlier mapping, the reference outcomes.
- A flow without `target.validation` sends exactly what it sent before, on the existing end-to-end suites.

## Stage 6: the verdict where people look

**Changes**

- The record page's journey (`RecordJourney.tsx`) shows each attempt's verdict, and a link opens the record in the
  explorer with Validate run, one way, as the record pages already link there.
- The flow's page shows the counts by outcome, from the ledger.
- `sqlflow records` shows the verdict of each attempt, in text and JSON.
- The record preview (`PreviewRecordOperation`) shows what the gate would decide for the rendered document.
- Docs: [mapping-templates.md](mapping-templates.md) (Checks), [ledger.md](ledger.md) (the columns, the lifecycle),
  [documents.md](documents.md) (the key), [protocols.md](protocols.md) (the gate's holds),
  [operations.md](operations.md) (a troubleshooting row for validation holds and their release),
  [explorer.md](explorer.md) (Validate and the API rows), the census and the CHANGELOG.

**Tests**

- GUI tests against a mocked API for the journey, the flow counts and the preview, both themes.
- CLI tests for the verdict in text and JSON.

## Live checks to approve

None runs before it is approved. Each id is logged in `.sqlflow/live-e2e/actions.log` when created and in
`.sqlflow/live-e2e/test-data.md`, every record carries the run marker `ODLIVE<date>`, and cleanup is the ledger's
`record` scope with a 404 confirmed on each id.

| Check | Creates | Cleanup |
| --- | --- | --- |
| Explorer: Validate three records the partition already holds, one of each kind type; read the Schema service for their kinds | Nothing | None |
| Gate, `mode: enforce`, one storage flow, three records: one valid, one invalid, one invalid then released | Two records (the valid one, the accepted one) | `record` scope, 404 on both |

## Close-out

- `-t:Rebuild` with zero warnings, the GUI build and lint clean, every suite green against the baseline.
- `tools/check-vendored-sqlflow.sh` passes (nothing in `sqlflow/` changes).
- Live checks run as approved, their ids removed and the inventory updated.

## Found on the way

- `TemplateValueRules` stops silently past `MaxItems` items and `MaxDepth` levels, so a long list reads clean (stage 1).
- `TemplateValueRules` skips `null` values, which a stored record can carry (stage 1).
- `SchemaSnapshot.EffectiveOf` merges and deep-copies a node's `allOf` branches on every call (stage 1).
- The `conforms` assertion answers pass or fail with examples, not counts by rule (stage 3).

## Not in this plan

- Holding less than today: the render holds stay in every mode.
- `oneOf` read strictly (exactly one form): OSDU's forms overlap, and storage never objects.
- The content of payloads (bulk data, files) beyond the route's existing checks.
- A run parameter that changes validation for one run: a run would then send what its flow says to hold, and the
  flow's setting would no longer say what the flow does.
- A reference storage does not hold waiting and being looked up again on later runs, instead of being held. It is a
  change of `target.verifyReferences`, worth its own plan.

## Decisions

These shape the stages above. Each has a recommendation; the plan is written to it.

1. **The default mode is `report`.** Every existing flow keeps sending exactly what it sends now and gains a recorded
   verdict; `enforce` is switched on flow by flow, after the explorer and Check values show what it would hold.
2. **The render holds stay in every mode.** `report` does not let a record missing a required `data` property through.
3. **A release of a validation hold accepts it** for that document, by name and by issue, recorded as the operator's.
   The alternative, a release that only plans the record again, would hold it again under `enforce` until the source
   changes.
4. **The explorer validates against the Schema service by default**, since the explorer shows what OSDU holds; a saved
   template is the reader's choice.
5. **The explorer comes before the gate** (stages 2 and 3 before 4 to 6), so how much of what OSDU and the sources hold
   would be held is known before any flow enforces.
