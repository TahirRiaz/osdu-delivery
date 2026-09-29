# Assertion flows: tests of what OSDU holds

An assertion flow (`flowType: assertion`) holds tests of what an OSDU partition holds once the data has landed: that the
records are there, that they carry the values they should, that their references resolve, that their bulk data is in
its DDMS, and that the ledger and OSDU agree. Each run keeps a report of how every test came out, so the status of a
partition after an upload is one page, one download, or one CI artifact away. The tests only read. Nothing a test does
writes to OSDU.

This document is the design. The document reference is [osdu/docs/documents.md](../osdu/docs/documents.md#assertion-flow),
the operator's view (API, GUI, CLI, retention) is [osdu/docs/operations.md](../osdu/docs/operations.md), and the decision
record is [decisions/0010](../osdu/docs/decisions/0010-assertion-flows-read-only.md).

## 1. Purpose

A delivery run says what it sent and what OSDU answered. It does not say what a user of the partition sees: whether a
search finds the records, whether the values the mapping wrote are the ones the source meant, whether the wellbore a log
points at exists, whether the curves are in the Wellbore DDMS, whether a record OSDU stored could not be indexed. Those
are questions about the partition, not about one run, and they are asked after the fact, often by someone who did not
write the mapping. An assertion flow asks them the same way every time, keeps the answers, and shows how they change.

The patterns come from DeltaForge's `ASSERT` statement: a severity per check (error, warning, info), a row count, a value
compared under a condition, a set of values the data must fall in, a result set compared exactly, in order, as a subset
or as excluded rows, uniqueness, and for every check what it expected, what it found and why it failed. The flow applies
them to OSDU's data model: records of a kind, their JSON paths, their references, their legal tags, their index state,
their bulk data, and the ledger that delivered them.

## 2. Where it sits

An assertion flow is a flow kind of the OSDU module on SQLFlow's flow kind registry, like the delivery, retrieval and
cache kinds. It is synced from a repository, scheduled, triggered and traced like any other flow, and a run is a
platform run with an operation and a payload. It reads the OSDU type of every test, so lineage orders it after the
flows that write those types: a schedule that fires a delivery flow and its assertion flow runs the tests once the
records have landed ([docs/lineage-design.md](lineage-design.md)). It writes no lineage object.

A run tests one partition. The partition is chosen the way a delivery flow's is
([docs/partitions-design.md](partitions-design.md)): the flow names its partitions under `partitions:` (or `"*"` for every
registered one), or it names none and tests the partition its `source.headers` name. A run names the partition with the
run value `partition`; one that names none takes the registry's default when the flow tests it, else the flow's only
partition. `*` is refused: a report describes one partition. Each partition keeps its own ledger identity for the flow,
so a partition's report history is its own.

## 3. The document

A flow holds tests; a test holds assertions. A test says what it reads: one kind, and the records of it a query, a list
of ids or a spatial filter selects. Its assertions say what must hold of what it read.

```yaml
flowType: assertion
name: recall-welllog-04-header-assertion
partitions: [dev]
parameters:
  logSource: { default: STAT_COMP }
source:
  endpoint: ${env:OSDU_URL}
  auth: { ... }                          # as a retrieval flow's source.auth
  ddmsRoot: /api/os-wellbore-ddms
defaults: { maxRecords: 10000, examples: 20, read: storage }
failRunOn: error
tests:
  - name: log-headers
    tags: [smoke]
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"osdu-delivery" AND data.Name:"{logSource}"'
    assert:
      - conforms: true
      - field: data.WellboreID
        resolves: master-data--Wellbore
      - field: data.ReferenceCurveID
        equals: MD
      - unique: [data.WellboreID, data.Name, data.LogRun, data.LogVersion]
        severity: warning
```

Every assertion names exactly one subject, and carries only the keys that subject takes; a key it does not take is a
parse error naming the assertion. The subjects:

| Subject | Asks | Reads |
| --- | --- | --- |
| `count` | How many records the test matches: a number, or a comparison (`atLeast`, `atMost`, `greaterThan`, `lessThan`, `between`, `equals`, `notEquals`). | The index's exact count |
| `field` | A condition on a JSON path of every record read, with a quantifier (`for`), a `where` filter, `values: any` for a path that holds several, and `optional` for a path a record may lack. | Records |
| `aggregate` | `min`, `max`, `sum`, `avg`, `count`, `distinct` or `missing` of a path over the records read (or of a bulk `column`), compared to a value, with a `tolerance` for numbers. | Records, or bulk data |
| `unique` | No two records share the values of these paths. | Records |
| `groupBy` | The records by the values of a path: named `groups` with their counts (`mode: includes` or `exact`), groups that must be `absent`, and a `groupCount`. | The index's aggregation |
| `recordSet` | The rows of `columns` the records hold, against expected `rows`: `exact`, `ordered`, `includes` or `excludes`. | Records |
| `conforms` | Every record meets the schema of its kind, as the saved template lays it out. | Records |
| `indexed` | No record of the selection has an index error (`index.statusCode` 201 or above). | The index |
| `legal` | `valid`: every legal tag the records carry is valid now. | Records, then Legal |
| `delivered` | Every record a delivery flow's ledger holds as delivered in the partition is matched; with `exact`, no other record is. `interface` names one interface of a source. | Ids, then the ledger |
| `rowCount` | The rows of each record's bulk data. | Bulk data |
| `columns` | The columns of each record's bulk data: `includes`, `excludes`, `equals`. | Bulk data |
| `column` | A condition on a bulk column's values, as `field` has on a path. | Bulk data |
| `monotonic` | A bulk column's values only go one way: `increasing`, `decreasing`, `strictlyIncreasing`, `strictlyDecreasing`. | Bulk data |

A condition on a value is one of `equals`, `notEquals`, `in`, `notIn`, `atLeast`, `atMost`, `greaterThan`, `lessThan`,
`between`, `matches`, `notMatches`, `startsWith`, `endsWith`, `contains`, `notContains`, `exists`, `empty`, `type`
(JSON Schema's words), `length` and `resolves`, with `ignoreCase` for text and `tolerance` for numbers. `for` is `all` (the default), `any`,
`none` or a share such as `99%`. `severity` is `error` (the default), `warning` or `info`; a test's `severity` is the
default of its assertions.

A test reads storage by default (`read: storage`, the record as OSDU keeps it) and the index with `read: index` (the
projection search returns, which is what a user of search sees). It reads at most `maxRecords` records (10,000 by
default, at most 1,000,000); when more match, the assertions that need records are not evaluated and say why, unless
`sample: true`, which evaluates the first ones and marks the result as a sample. `ids` names records directly instead of
a query; `spatial` is the search's `spatialFilter`; `sort` orders what a sample reads. `partitions` narrows a test to
some of the flow's partitions; in another it is skipped. `bulk` reads each record's bulk data from the Wellbore DDMS
(`columns` narrows the columns read, `maxRows` bounds the rows, 1,000,000 by default).

Parameters fill `{name}` tokens in queries, ids and expected text (never in a regular expression), with `{partition}`
always available. A token the flow does not declare is a parse error. The partition is not a parameter.

## 4. Checked against the template, and how values compare

A test is correctly mapped when every path it reads is a path of its kind. Before a run reads anything, every test that
reads fields of an exact kind is checked against the saved template of that kind (the newest saved version, or the one
`template` pins): each path must be a variable of the schema (arrays crossed implicitly, `[*]` and `[n]` allowed, and
the record's system properties such as `id`, `kind`, `acl`, `legal`, `tags` and `createTime` always there), each operator
must suit the variable's type (a number compared as a number, a pattern on text), and each operand must be a value the
variable can hold. A path that is not a variable is reported with the nearest one ("did you mean"). A test that does not
fit is not evaluated: it is errored, its assertions skipped, and its problems say what to fix, including how to save a
template that is missing (`sqlflow template capture --kind <kind>`). A test of a kind with wildcards, or one reading no
field, needs no template.

Values compare by what they are. Numbers compare as numbers, exactly as written (a decimal in the document is compared
at its written precision, never through a binary float), within a `tolerance` when one is given; text that holds a number
compares as a number with a number. Instants compare as instants whatever their offset. Text compares by ordinal
characters, or ignoring case with `ignoreCase`. `exists` is true of a path the record has, even when it holds null;
`empty` is true of null, an empty text, an empty array or an empty object. A path that crosses an array yields every
value under it: a condition holds of the record when it holds of all of them, or of any with `values: any`.

### 4.6 Bulk data

A test with `bulk` reads, for every record it matched, the record's bulk data from the Wellbore DDMS
(`GET {ddmsRoot}/ddms/v3/{collection}/{id}/data`): first its description (`describe=true`), which says its rows and
columns, then its values a page at a time (`offset`, `limit`, `orient=split`), only the columns the assertions need. The
collection is the one the DDMS serves the kind's bulk data from: `welllogs` for a WellLog, `wellboretrajectories` for a
trajectory, `ppfgdataset` and `wellpressuretestrawmeasurement`; a test with `bulk` on a kind the DDMS keeps no bulk data
for is a parse error. A record without bulk data fails a bulk assertion rather than being skipped: a log with no
curves is what the test is there to find. Records are read several at once, up to the flow's concurrency.

## 5. Evaluating a test

A test first counts what its selection matches (`POST /api/search/v2/query` with `trackTotalCount`, or for `ids` a read
of the ids from storage). The assertions that answer from the index (`count`, `groupBy`, `indexed`) are answered with
their own queries. When an assertion needs the records, the test pages through them with a cursor
(`POST /api/search/v2/query_with_cursor`, the cursor closed when the walk stops) and, reading storage, reads each page's
ids back a hundred at a time (`POST /api/storage/v2/query/records`), retrying the ids storage asks to retry. Every record
is handed to every assertion once, as it arrives, so memory holds a page, never the selection. A test that names ids
reads them from storage directly; an id storage does not return is noted, not silently dropped.

Each assertion's outcome is `passed`, `failed`, `errored` (it could not find out: a query OSDU refused, a service that
did not answer) or `skipped` (it was not evaluated, and says why). A failed assertion keeps what it expected, what it
found, how many it checked and how many failed it, the number it measured (for a trend across runs), and the first
records that failed it (`examples`, 20 by default), each with the id, the value it held and why it failed.

A test's outcome adds its assertions up: `errored` when one errored, else `failed` when an error-severity assertion
failed, `warned` when only warnings failed, and `passed` otherwise (a failed info assertion is reported and never fails
the test). A test skipped because it does not test the run's partition is `skipped`. What the ledger keeps of one result
is bounded at 1,000,000 characters: a result past it keeps fewer examples, marked as cut back.

## 6. A run

The operations are `test` (the default) and `plan`. A `test` run selects the tests the payload names (`tests`, by name,
and `tags`, the tests carrying any of them; both empty runs every test), registers the flow's ledger in the partition,
opens a report row, checks the tests against their templates, and evaluates them as many at once as
`reliability.concurrency` allows. Each result is written the moment it is known, so a long run shows its progress and a
stopped one keeps what it found. The run closes the report as `failed` (a test failed), `errored` (none failed, one
errored) or `passed`, and its result names the report, the counts and the tests that did not pass.

`failRunOn` says when the tests fail the platform run: `error` (a failed or errored test, the default), `warning` (a
warned one as well) or `never`. A run failed by its tests still reports its outcome, so the schedule's notifications
name the tests.

A `plan` run selects and checks the same tests and counts what each matches and would read, and records nothing. It is
how a new test is tried against a partition before it is trusted.

The payload is parsed strictly: `tests` and `tags` are the only keys the kind takes, a name that is not a test of the
flow or a tag no test carries fails the run naming it, and a delivery flow's keys (`force`, `recordKeys` and the rest)
are refused. The same run starts from the GUI's trigger dialog, from a schedule, from `sqlflow run <flow.yaml> --payload
'{"tests":["log-headers"]}'`, or from `POST /api/v1/runs`.

## 7. The ledger

Two tables in the `osdu` schema keep the reports, each keyed by the partition number first like every ledger table
([osdu/docs/ledger.md](../osdu/docs/ledger.md)):

- `osdu.AssertionRun`: one row per run of a flow's tests in a partition: the flow's ledger identity and name, the
  platform run, the actor, the selection it was asked for, its status and counts, the hash of the definitions it ran,
  when it started and ended, and why it failed.
- `osdu.AssertionResult`: one row per test per run: the test, its kind, outcome, heaviest failed severity, matched and
  evaluated counts, whether it sampled, how many assertions it held and how many did not hold, the hash of the test's
  definition, how long it took, its error, and the whole result as JSON (`Detail`).

The board reads each test's latest result that was not skipped, one seek per flow on
`(PartitionId, FlowId, TestName, AssertionRunId)`; a result whose definition hash differs from the test's current one
is shown as changed, since it answers what the test asked before. Nothing is counted separately: the board's totals and a
run's counts are read from these rows. The retention pass removes a finished run older than its cut-off whole, once a
later result of the same test superseded every one of its results, so a kept report is whole and a run holding a test's
latest result stays however old.

## 8. The report

The report of a run is rendered from its rows by one piece of code (`AssertionReport`), which the API and the CLI share:

- **JSON**: the run and every result whole.
- **Markdown**: a summary table, then each test with its assertions, for a pull request or a wiki.
- **HTML**: a page with no script and no external reference: the pass rate, the outcome tiles, the tests by kind, and
  each test with its assertions and examples; it prints.
- **JUnit XML**: a suite per kind and a case per test, for a CI dashboard: a failed test is a failure, an errored one
  an error, a skipped one skipped, and a warned test passes with its warnings in its output.

The control plane serves the boards and the reports under `/api/v1/delivery`: the board of every assertion flow in a
partition, one flow's board, its runs, the matrix of its tests against its runs, one test's history with every result
whole, a run with every result, and a run's report in each format ([osdu/docs/operations.md](../osdu/docs/operations.md#the-api)).
Every read is in a partition: the one the request names, else the workbench's (`X-Osdu-Partition`), else the one a run
would test.

The GUI puts every test of every assertion flow on one board (OSDU, **Tests**): the pass rate, outcome tiles that filter
the tests they count, a search over names, kinds, tags, queries and assertions, the tags as filters, and the tests by
flow and by the kind they read, each with its outcome, how many of its assertions hold, what it matched, its last runs
as a strip and a button that runs it alone. A test opens in a sheet with what every assertion found and the records that
failed it, each assertion's outcome over the last runs, the trend of what it measured, and its declaration. A pipeline of
the kind has three tabs: **Tests** (its board), **History** (its tests against its runs, with how often each flipped)
and **Reports** (its runs). A report page shows one run and runs the tests that did not pass again. Running is always
the platform's one trigger dialog, opened on the tests asked for.

The CLI reads the same rows: `sqlflow assertions list | status | report <flow.yaml>`.

## 9. The OSDU services it calls

Every request is one the OpenAPI specifications in `osdu-csharp-client-main/openapi_specs` describe, and the contract
tests hold the fake platform to them:

| Service | Request | For |
| --- | --- | --- |
| Search | `POST /api/search/v2/query` (`trackTotalCount`, `aggregateBy`, `spatialFilter`, `returnedFields`, `sort`) | Counts, groups, index errors |
| Search | `POST /api/search/v2/query_with_cursor`, `DELETE .../query_with_cursor/{cursor}` | Paging through records |
| Storage | `POST /api/storage/v2/query/records` (at most 100 ids) | Records, ids, references (`attributes: ["id"]`) |
| Legal | `POST /api/legal/v1/legaltags:validate` (at most 25 names) | Legal tags |
| Wellbore DDMS | `GET /api/os-wellbore-ddms/ddms/v3/{collection}/{id}/data` | Bulk data |

The search service's quirks are handled where they are met: a cursor that comes back unchanged is the same page again,
three pages in a row that add nothing end the walk, a leading wildcard or a query of nothing but `NOT` is refused by
OSDU and so reported as the test's error, and the index trails storage by about thirty seconds, which is why a schedule
runs the tests after the delivery rather than in the same wave.

## 10. Limits and safety

- At most 500 tests per flow, and at most 500 names and 500 tags in one run's payload.
- A reference check looks up at most 200,000 distinct ids per assertion; `distinct` and `unique` hold at most 1,000,000
  distinct values; a legal check at most 10,000 tags. Past a limit the assertion errors, saying how to narrow the test.
- Regular expressions run with a one second timeout.
- Credentials are references (`${env:NAME}`, `${keyvault:NAME}`); an error is redacted before it is stored or shown.
- A test never writes. Running the flow against a live partition is still a live OSDU run: it follows the approval rule
  for live runs (CLAUDE.md), though it creates no id and so leaves nothing to clean up.

## 11. What it reads today: storage and the DDMSs

The state as of 2026-09-29. A test reads records through search and storage for every kind, and bulk data from one
DDMS, the Wellbore DDMS. Everything below "Not yet" is outside what a test can assert on today.

### Storage and search: every kind

| What | How a test uses it |
| --- | --- |
| Records as storage keeps them | `read: storage`, the default: every assertion on fields (conditions, aggregates, uniqueness, record sets, `conforms`, `legal`) holds the records to what storage returns (`POST /api/storage/v2/query/records`). |
| Records as search returns them | `read: index`: the same assertions on the projection a user of search sees, and `count`, `groupBy`, `indexed` and `delivered` from the index. |
| Records named by id | `ids`: read from storage directly; an id storage does not return is noted in the result. |
| References | `resolves`: every id a field holds is looked up in storage, of the entity type named. |

A record a DDMS writes is registered in storage as well, so its header (the WellLog record beside its curves, a record
the Well Delivery or Reservoir Management DDMS keeps) is tested at the record level like any other.

### Bulk data: the Wellbore DDMS

A test with `bulk` reads each record's bulk data from the Wellbore DDMS v3
(`GET {ddmsRoot}/ddms/v3/{collection}/{id}/data`) and holds it to `rowCount`, `columns`, a condition on a `column`, an
`aggregate` over a column, and `monotonic`, record by record. The kinds it keeps bulk data for are the ones it reads:

| Entity type | Collection |
| --- | --- |
| `work-product-component--WellLog` | `welllogs` |
| `work-product-component--WellboreTrajectory` | `wellboretrajectories` |
| `work-product-component--PPFGDataset` | `ppfgdataset` |
| `work-product-component--WellPressureTestRawMeasurement` | `wellpressuretestrawmeasurement` |

`bulk` on any other kind is refused when the flow loads. The Wellbore DDMS's collections without bulk data (wells,
wellbores, marker and interval sets, log acquisitions) are tested through storage, as above.

### Not yet

- **The other DDMSs the delivery side writes to**: the Well Delivery DDMS, the Rock and Fluid Sample DDMS (RAFS), the
  Reservoir DDMS (ETP), the Reservoir Management DDMS, the Production DDMS (its core service, DSPDM, and its historian's
  time series), Seismic Store, and External Data Services. What each of these keeps beyond the storage record (RAFS
  content tables, ETP objects and arrays, DSPDM rows, historian points, seismic files, EDS registrations) cannot be
  asserted on; the storage records they register can.
- **File contents**: the files behind a `dataset--File.*` or `dataset--FileCollection.*` record are not read; only the
  dataset record is.
- **A Wellbore DDMS elsewhere**: a test reads bulk data under the flow's own endpoint at `source.ddmsRoot`; a Wellbore
  DDMS the delivery side reaches through `target.ddms` (another endpoint, or one the Register service names) is not
  reached from a test.

A DDMS added here gets its reader beside `WellboreBulk`, its collections and call pattern from the DDMS catalog the
delivery side already keeps (`DdmsCatalog`), and its requests held to its OpenAPI specification by the contract tests,
as the Wellbore DDMS's are.
