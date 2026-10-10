# Plan: assertions in a mapping

A mapping says, beside a property, what its value must be: a business rule such as "the top depth lies between 0 and
250", "the unit is never NONE", "a curve's mnemonic matches the dictionary's form". A rule is judged on every record the
mapping renders, either on the value the row gives the node (before its modifiers) or on the value the record carries
(as OSDU will hold it), and says what a record that breaks it does: it is held, it is sent and the failure recorded, or
the value is left out. The outcome is part of the record's validation verdict, recorded on every attempt, so whether a
record met the mapping's rules, which it broke and with what value is answered from its history alone. The same rules
are judged on what OSDU already holds, from the explorer and from an assertion flow.

The conditions are the assertion flows' own (osdu/docs/reference/flow/assertion.md): one vocabulary, one parser and one
comparer, so a rule a mapping states and a test an assertion flow states mean the same thing.

Each stage lists what it changes and the tests that close it. A stage is finished only when those tests pass, SQL Server
suites included. All work is in `osdu/`: nothing in `sqlflow/` changes, and the `osdu` model change ships with its
migration.

## Status

| Stage | State |
| --- | --- |
| 1. The word and the shared conditions | Done |
| 2. Judging a record | Done |
| 3. The check before sending and the ledger | Done |
| 4. Where people look | Done |
| 5. What OSDU holds | Done |
| 6. The mapping builder | Done |
| 7. Documentation, census and editor | Done |

## What an author writes

```yaml
record:
  data:
    TopMeasuredDepth:
      $from: index_min
      $assert:
        - between: [0, 250]
          name: top-depth-range
          description: A log of the well database starts within the first 250 m.
    SamplingInterval:
      $from: index_increment
      $required: false
      $assert:
        - stage: incoming
          notIn: [-999, -999.25]
          onFail: omit                 # the source's placeholder leaves the property out
    Curves:
      $forEach: curves
      $assert:
        - length: { atLeast: 2 }       # judges the array: at least the index curve and one other
          onFail: report
      $item:
        Mnemonic:
          $from: curve_id
          $assert:
            - matches: '^[A-Z0-9_]{1,16}$'
              where:
                - field: data.Curves.CurveID   # inside the same array: the same curve
                  notEquals: DEPT
```

`$assert` is a list of assertions. Each is written with the assertion flows' words, and carries no `$`, since its keys
are words of the assertion, not of the record (as a modifier's settings are):

| Key | Default | Meaning |
| --- | --- | --- |
| one condition | required | `equals`, `notEquals`, `in`, `notIn`, `atLeast`, `atMost`, `greaterThan`, `lessThan`, `between`, `matches`, `notMatches`, `startsWith`, `endsWith`, `contains`, `notContains`, `exists`, `empty`, `type`, `length`, with `ignoreCase` and `tolerance`, exactly as an assertion flow's `field` takes them. |
| `stage` | `record` | Which value is judged: `record`, the value the record carries, after the modifiers and the conversion to the property's type, which is what OSDU holds; or `incoming`, the value the row gives the node before its modifiers (the column, or what `$expr` computes). |
| `onFail` | `hold` | What a record that breaks it does: `hold` (not sent, its document kept, until the row or the mapping changes or a release accepts it), `report` (sent, the failure recorded), `omit` (the value left out, the failure recorded). |
| `values` | `all` | For a property holding several values (the items of a repeated array, a list): `all`, every value meets it, or `any`, one does. Record stage only. |
| `where` | none | 1 to 10 conditions selecting when the assertion is judged: on the record stage each names a `field` of the record (a field inside the same repeated array as the property reads the same item); on the incoming stage each names a `column` of the row the node reads, or `$dataset.<column>`. |
| `name`, `description` | a label read off the condition | What messages, the ledger and the reports call it. |

`resolves` is not a condition of a mapping: whether a referenced record exists is the `ref` and `id` modifiers' cache
check and the flow's `target.verifyReferences`.

## Where an assertion is written

| Node | Record stage | Incoming stage |
| --- | --- | --- |
| `$from`, `$expr` | yes | yes |
| `$cache`, `$search`, `$lookup`, `$coalesce` | yes | no: the value comes from the cache or the platform, not from the row |
| `$forEach` | yes: the array as a whole (the preflight takes `length`, `empty` and `exists` there) | no |
| A property of an item of a list of objects | yes | yes, as its node allows |
| `$value`, a literal | no: the value is the same for every record | no |
| An alternative of `$coalesce`, an item of a list of values | no: `$assert` goes beside `$coalesce`; a list of values has no node of its own | no |

## Judging a record

- **What is judged.** A property's value once (a single value), or each value it holds (an item of a repeated array, a
  value of a list). On a list, `length`, `contains`, `notContains` and `empty` judge the list as a whole. A property the
  record does not carry (no value, a `$when` that does not hold) is judged only by `exists` and `empty`, on the record
  stage too: `exists: true` fails on a property a `$when` left out, so a rule meant only where the property is written
  says so with `where`. Whether it must be there otherwise is `$required`'s and the schema's to say.
- **Incoming.** Judged as the node reads its value, for the row (or the child row) it reads, only when its `$when` holds.
  A blank value is no value.
- **Record.** Judged once the record is assembled, on the record as it will be sent, so the same assertion gives the same
  answer for a document rendered here and for the record OSDU stores.
- **Values compare** as assertion flows compare them (`ValueComparer`): numbers as numbers, ISO 8601 dates as instants,
  text ordinally or ignoring case, text holding a number against a number; `length` counts UTF-16 code units, as an
  assertion flow's does. A value an assertion cannot judge (a pattern match that runs out of its second) fails it.
- **`where`.** A condition reads a field (record stage) or a column (incoming stage). A field the record does not carry
  holds only for `exists: false` and `empty: true`, so such a condition selects the values where the field is absent.
- **`omit`.** The value that fails is left out: the property, or the property of that item; an object left with nothing
  is left out with it, and a list the mapping defines is written empty. It needs `$required: false`, since the record may
  then go without the property, and is not taken with `values: any`.
- **Findings.** Each failure keeps the property (`data.Curves[].Mnemonic`), where it was found (`data.Curves[3].Mnemonic`,
  or for the incoming stage the source, `dataset.curves[3].curve_id`), the assertion, its stage and action, the value
  (clipped to 200 characters and redacted) and why it fails. A record lists at most 50 and counts every one.

## The check before sending, the ledger and releases

The findings travel with the document they judge: the work batch line holds them beside it, so the check before sending
(`ValidationGate`) reads the findings of exactly the document it sends.

- The verdict gains `assertions`: the mapping, how many judgements were made, how many failed, held, reported and
  omitted, and the failures listed. It goes on the attempt (`result.validation`) like the rest of the verdict.
- A failure whose action is `hold` holds the record with its document kept, under an error that names the property and the
  assertion (values single-quoted, so the records broken by one assertion are one issue). A release sets the record's
  accepted hash to that document, and the next try sends it, the verdict marked accepted. `target.verifyReferences:
  storage` still holds a record whatever a release accepted, as it always did.
- `osdu.Record.AssertionFailures` keeps how many judgements failed on the last document checked (null when it judged
  none; eight failing curves count eight), written with the verdict through the record's events, under a filtered index
  by flow as `ValidationOutcome` has one (migration `RecordAssertions`). The failures themselves are in each attempt's
  `result.validation.assertions`.
- The run trace counts failures by action, and each drain's line per template names the assertions failed most.
- A record whose document is the one OSDU already holds is not sent again, so a new or changed assertion is judged as
  records next change. What OSDU holds is judged by the explorer and an assertion flow (stage 5).

## Stages

### 1. The word and the shared conditions

- `MappingMapper` reads `$assert` on the nodes that take it and refuses it where it does not apply, each refusal naming
  the node and what to write instead. `NodeAssertion` on `MappingEntry`.
- `ConditionReader` is the one parser of a condition: the assertion flow's `field` conditions and a mapping's assertions
  both call it, so their words, operands and messages are the same.
- Tests: every refusal, every condition, the assertion flow's suites unchanged.

### 2. Judging a record

- `MappingAssertionJudge`: the record stage over a record (rendered or stored), the incoming stage over a node's value,
  and the omissions. `RenderResult.Assertions`; the record shape notes each assertion.
- `Preflight`: each record-stage condition suits its property's type and its operands are values the property holds
  (`AssertionTemplates`); every `where` field is a property; every `column` a mapping reads in an assertion is checked
  against the ingestion tables.
- Tests: each operator on both stages; lists, repeated items, `where` inside and outside an item; absence; `omit` at
  every depth; the limits; a pattern that runs out of time.

### 3. The check before sending and the ledger

- `WorkItem.Assertions`, written by the intake, read by the worker; `ValidationGate` adds the findings to the verdict and
  holds; `ValidationVerdict.Assertions`; `ValidationTally` counts them.
- Migration `RecordAssertions` (module 1.39.0): `Record.AssertionFailures`, `RecordEvent.AssertionFailures`, an index.
- Tests: a hold, a report, an omission; a release that accepts; a document rendered before the change; the issue a hold
  makes; the SQL Server ledger writes.

### 4. Where people look

- The preview and a plan show what the assertions found; Check values counts the rows failing each assertion;
  `sqlflow records show`, `values` and `preview` print them.
- The GUI: the verdict view's assertions, each attempt's verdict on the record's timeline, the preview, Check values, and
  each property's assertions on the mapping's Properties tab.

### 5. What OSDU holds

- The explorer's Validate and Validate these records judge a mapping's record-stage assertions on the records OSDU holds;
  the record page's check uses the assertions of the flow's own mapping.
- An assertion flow's subject `mapping: Name@version` holds every record a test reads to the mapping's record-stage
  assertions.

### 6. The mapping builder

- The draft carries each entry's assertions, compose writes them, the entry editor lists, adds, edits and removes them,
  and compose's preflight checks them.

### 7. Documentation, census and editor

- `flow/mapping-assertions.md`, and the pages that name the verdict, the gate, the explorer, assertion flows, the CLI
  verbs and the ledger; the key census (`record.<name>`, the assertion flow's `mapping`), which the language server and
  the GUI editor read; the CHANGELOG; the manifest.

## Edge cases

| Case | Behaviour |
| --- | --- |
| A row whose value an incoming assertion cannot read (text where a number is compared) | The assertion fails with the reason; its action decides |
| A value nothing holds (null, blank text, a property left out) | Judged only by `exists` and `empty` |
| A `$when` that does not hold | The property is not written; an incoming assertion is not judged |
| A render that holds for another reason | Held at render as before; the findings are reported by a preview and Check values, and nothing is queued |
| `omit` on a property the schema requires | Refused: `omit` needs `$required: false`, which the preflight refuses on a required property |
| `omit` leaving an item or an object empty | The item or object is left out; a list the mapping defines stays, empty |
| Several assertions on one value | Each judged on its own; each failure recorded; any `hold` holds the record |
| A `where` field the record does not carry | Holds only for `exists: false` and `empty: true`; any other condition selects nothing, so the assertion is not judged for that value |
| A queued document from before this change | Carries no findings: judged by its schema alone, as before |
| A held record whose mapping then changes | Keeps the document and the findings it was held for; a release sends it as it is; a redeliver plans it again under the new mapping |
| The same document as the one delivered | Not sent again and not judged again; judge what OSDU holds with the explorer or an assertion flow |
| A thousand curves failing one assertion | 50 listed, all counted; the hold names the first three |
| A pattern that backtracks | One second per value; a timeout fails the assertion, never hangs a render |
| A defect in the judge | Caught per document, recorded as an assertion that could not be judged, which fails under its action; an `omit` that could not be judged is reported, since nothing is left out of a value no one judged |
| Unicode text | Compared ordinally as UTF-16, `length` counts as the assertion flows count |
| A secret-looking value | Every value and message redacted before it is stored or shown |

## Not in this plan

- A flow or run setting that changes what an assertion does: the mapping says what its rules do, so every flow that
  pins it treats records alike, and a change of rule is a change of mapping, versioned and reviewed.
- Assertions across records (uniqueness, counts, aggregates): those are an assertion flow's, over what OSDU holds, and
  SQLFlow's `assertions:` on the ingestion flow, over the ingestion table.
- `resolves`: reference existence stays with the `ref` and `id` modifiers' cache check and `target.verifyReferences`.
- Failing a run on an assertion: a record is the unit of delivery and of traceability, so a broken rule holds or marks
  the record, never stops the others.
