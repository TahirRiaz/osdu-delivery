# Plan: dimension flows

A dimension is the set of distinct values one attribute of an OSDU kind holds, each kept exactly as the search index holds
it (the original) and grouped under a cleaned value (the member). Its purpose is filtering OSDU search: a person or an
application picks clean values, and the dimension gives the query that finds every record holding any of their originals.
OSDU has no way to list the distinct values of an attribute beyond the first thousand, and none to group spellings, so
this is a flow kind of its own, `flowType: dimension`, with its own tables in the `osdu` schema, and a cache origin that
lets a mapping read a dimension like any lookup table.

Each stage lists what it changes and the tests that close it. A stage is finished only when those tests pass, SQL Server
suites included. All work is in `osdu/`; nothing in `sqlflow/` changes. Stages 1 to 7 are built; the recall estate
(`B:\osdu-recall-metadata`) holds a demo flow, `recall/flows/recall-welllog-05-dimensions.yaml`. The live check listed
under Close-out has not been run.

## Decisions

| Decision | Choice | Why |
| --- | --- | --- |
| Where the data lives | A flow kind of its own with tables of its own, not a cache origin | The cache is loaded whole for every render (lookup tables stop at 100,000 rows) and one partition-wide version moves whenever anything in it does; a dimension carries counts and first and last seen that move with every build. Retrieval and assertion flows set the pattern: each reverse-direction job is its own kind reading through the shared search client. |
| What is stored | Distinct values only: members and the originals under them, with counts | Records stay in OSDU; a dimension is its vocabulary. |
| What an original is | The value exactly as the index holds it | A filter matches what the index holds, and an exact match on text reads the `keyword` sub-field, which holds the value as written. |
| How values are read | The search's `aggregateBy`, paged by value ranges; a cursor scan where aggregation cannot answer | `aggregateBy` returns at most `aggregationSize` buckets (1000 by default, a platform setting, not a request parameter) and has no paging of its own [19 SRC/config/SearchConfigurationProperties.java:23; SRC/util/AggregationParserUtil.java:65-71]. |
| What cannot be a member | Values search cannot match exactly | A text value longer than 256 characters is not in the `keyword` sub-field at all (`ignore_above: 256`), and a null text is indexed there as the text `null` (`null_value`) [25 IC/util/TypeMapper.java:262-268]. Neither can be filtered on exactly, so both are counted and reported, never stored as members. |
| Operation names | `build` (the default) and `plan` | `plan` reads templates and counts, as every kind's plan does, and writes nothing. |

Sources, read on 2026-09-30 through the GitLab API at the head of `master`:

| Key | Project | Commit | Prefix |
| --- | --- | --- | --- |
| 19 | `osdu/platform/system/search-service` | `731e6f5748416003b58b2e96eb018a82d4ddd8c9` | `SRC/` = `search-core/src/main/java/org/opengroup/osdu/search/` |
| 25 | `osdu/platform/system/indexer-service` | `423a9aee9099528361b738f3961d6a03aa13bd8b` | `IC/` = `indexer-core/src/main/java/org/opengroup/osdu/indexer/` |

What the source says, and this plan relies on:

- `aggregateBy` builds one terms aggregation of `aggregationSize` buckets, ordered by count descending and then key ascending,
  with `minDocCount` 1; a `nested(path, field)` value wraps it in a nested aggregation, and `nested(a, nested(b, field))`
  nests deeper [19 SRC/util/AggregationParserUtil.java:49-135]. It runs on `POST /query` with the request's `query`
  applied; `query_with_cursor` takes no `aggregateBy` [19 docs/docs/api.md:1313].
- A bucket key is the string term, or for a long or double term its `key_as_string`, which Elasticsearch gives only for a
  formatted field (a date, a boolean) [19 SRC/provider/impl/CoreQueryBase.java:203-249, 452-470]. A plain number can come
  back with no key, so a numeric attribute is read by scanning when its buckets carry none.
- Inside `nested(...)` the counts are the array's objects, not records.
- The query string reaches Elasticsearch as `query_string` with `escape(false)`, so a range `field:["a" TO "m"}` is read
  by Elasticsearch; the service's own nested rewriting applies only inside `nested(...)` (`SqlFlow.Delivery.Search`).
- The indexer maps record properties as follows [25 IC/util/TypeMapper.java:51-69, 180-230, 295-300]: `id`, `kind`,
  `type`, `namespace`, `x-acl`, `acl.viewers`, `acl.owners`, `legal.legaltags`, `legal.otherRelevantDataCountries`,
  `legal.status`, `ancestry.parents`, `createUser`, `modifyUser` and `collaborationId` as keywords; `authority` and
  `source` as constant keywords; `version` as a long; `createTime` and `modifyTime` as dates; `tags` as a flattened map.
  A `data` property follows its schema (`SearchFields`).

## The document

```yaml
flowType: dimension
name: wells-dimensions
batch: reference
partitions: [dev, test]              # or source.headers.data-partition-id, or neither for every registered partition
source:
  endpoint: ${env:OSDU_URL}
  auth: { type: oauth2ClientCredentials, ... }
  aggregationSize: 1000              # the platform's AGGREGATION_SIZE; lower only costs requests, higher loses values
dimensions:
  - name: CurveMnemonic
    description: Every curve mnemonic the well logs of the partition hold.
    kind: "osdu:wks:work-product-component--WellLog:*"
    query: "*"                       # optional; narrows the records read, with {parameter} and {partition} tokens
    path: data.Curves.Mnemonic       # any property search can match exactly: data, acl, legal, tags, ancestry, ...
    clean:
      - trim
      - upper
      - map: { dictionary: CurveAliases, otherwise: keep }
    countRecords: true               # a record count per member where the index counts objects or repeated values
  - name: Operator
    kind: "osdu:wks:master-data--Wellbore:*"
    path: data.CurrentOperatorID
reliability: { concurrency: 4, timeoutSeconds: 100 }
schedule: { cron: "0 3 * * *" }
```

Clean steps, applied in order to each original: `trim`, `collapseSpaces`, `upper`, `lower`, `nfc`, `nfkc`,
`foldSeparators` (letters and digits kept, every run of anything else one hyphen, lower-cased: the cache's own separator
fold), `replace: { pattern, with }` (a regular expression run without backtracking), and `map: { dictionary, field,
otherwise }` (a dictionary document of the repository; `otherwise` is `keep`, `drop` or a text). No steps keep the
original as the clean value.

## Reading every value

For each dimension, in its partition:

1. **Kinds and shape.** One aggregation by `kind` lists the concrete kinds the pattern and query match. A `data` path is
   resolved against the saved template of every one of them (`SearchFields`); all must index it the same way, and a kind
   without a saved template refuses the dimension, naming the kinds and their record counts. A record property has its
   shape from the indexer's own mapping, with no template needed.
2. **The field asked.** Text is aggregated by its `keyword` sub-field, a keyword, number, boolean or date as it is named, a
   property of a nested array through `nested(...)`, and a value inside a flattened array as its dotted path.
3. **Paging.** Aggregate over the whole query. A slice that returns fewer buckets than `aggregationSize` is complete. One
   that returns that many is split at the middle of its keys (in the index's order: code point order for text, numeric
   order for numbers), and each half is asked again with the range added to the query. A slice keeps only the keys inside
   its own range, since a record holding several values brings buckets of other slices too. Every split leaves both
   halves with fewer distinct keys than the whole, so paging ends. A split point the service would misread (a key holding
   `nested(`, or inside a nested array one whose parentheses do not balance or that holds `AND x:`) is passed over for the
   nearest key that is not.
4. **Scanning.** A slice that cannot be split (one key left in range, or no usable split point), and a numeric attribute
   whose buckets carry no key, are read through the search cursor instead, returning only the attribute, and counted the
   way the aggregation counts.
5. **Completeness.** Beside the members, each build counts the records the query matches, and for text outside a nested
   array the records holding a value but no keyword (values over 256 characters) and those whose value is null. Buckets
   that sum to less than the records holding a value say the index moved during the build or values are repeated; the
   build says so and keeps what it read.

## Cleaning and grouping

Each original is cleaned; originals whose clean value is the same are one member. An original is left out of every member,
with the reason kept on its row, when cleaning leaves nothing, when the clean value is longer than 256 characters, or when
`map` drops it. An original a query cannot carry (a control character, `nested(`, or inside a nested array a value the
service rewrites) stays under its member and is marked unfilterable; the member's filter covers the rest and says how
many it cannot.

## The filter

A member's filter is the query that finds every record holding any of its filterable originals, written the way the index
holds the attribute: `data.X.keyword:("a" OR "b")` for text, `data.X:("a" OR "b")` for a keyword, numbers unquoted, and
`nested(data.C, (M.keyword:"a")) OR nested(data.C, (M.keyword:"b"))` inside a nested array, one nested clause per original,
since the service rewrites `OR x:` inside one. A filter holds at most 500 originals, leaving room in the service's 1024
clauses for the query it is combined with; a member with more has its filter in parts. The kind is the dimension's.

## Tables

All in the `osdu` schema, keyed by the ledger partition first, under a ledger of kind `dimension` per flow and partition.

| Table | One row per | Holds |
| --- | --- | --- |
| `Dimension` | flow, partition and dimension name | The declaration as last built: kind, query, path, the field asked, the clean steps, the definition hash, current member and original counts, the last build. |
| `DimensionRun` | dimension and build | Status, counts (records, members, originals, added, removed, moved, left out, unfilterable, too long, null), requests, slices, scanned records, templates used, notes. |
| `DimensionMember` | clean value | Stable id, clean value, count and whether it is exact, originals, filter and parts, first and last seen, removed. |
| `DimensionValue` | original | Stable id, the original, its member or why it has none, count, filterable, first and last seen, removed, the build it joined its member. |
| `DimensionChange` | original that moved, left, came back or arrived after the first build | The build, the original, from and to member. |

A build writes in one transaction: its values are copied into temporary tables, and set-based statements add, update and
mark removed; an application lock per dimension keeps two builds of it from interleaving. A failed build writes nothing
but its run row. Nothing is deleted: a member or original a build no longer finds is marked removed, and one that comes
back keeps its id.

## Edge cases

| Case | Handling |
| --- | --- |
| More distinct values than one aggregation returns | Paged by value ranges until every slice is complete. |
| A record holding many values (arrays, nested arrays) | Out-of-range buckets are dropped per slice; a slice they fill is split, and one that cannot split is scanned. |
| A split point the service would misread | The nearest usable key is taken; with none, the slice is scanned. |
| Numbers with no bucket key | Scanned. |
| Text over 256 characters | Not in the keyword field: counted as too long, never a member. |
| Null text | Indexed as `null`: counted as null, never a member; a literal text `null` cannot be told apart and is treated the same. |
| An attribute no record holds | Zero members; the build says so. |
| An attribute the index cannot match exactly (unindexed arrays of objects, geo shapes, objects) | Refused when the flow is loaded or when the template is read, naming why. |
| A kind whose versions index the path differently, or one without a saved template | Refused, naming the kinds. |
| Aggregation answers nothing though records hold the attribute | The dimension fails, saying the field asked and suggesting the template does not match the index. |
| The index changes during a build | Slices are disjoint, so nothing is counted twice; counts may lag, and the build says when the totals disagree. |
| Clean steps that produce nothing, or text over 256 | The original is kept with the reason, under no member. |
| A regular expression that would backtrack | Run with the non-backtracking engine; a pattern it cannot run is refused at load. |
| A dictionary that is missing or invalid | That dimension fails; the others build. |
| More originals than `maxValues` (1,000,000 by default, at most 5,000,000) | That dimension fails before writing, naming the count. |
| Two builds of one dimension at once | The second waits for the first's lock and fails with a clear message if it cannot get it. |
| A search failure part way | Retried by the flow's reliability settings; a dimension that still fails writes nothing and the others build. |
| A member whose filter would pass the clause limit | Its filter is in parts; the API and the CLI hand every part. |
| A member that disappears and comes back | Keeps its id; the change log says when. |

## Stages

### Stage 1: reading every value

- `SqlFlow.Delivery.Search`: a range clause and an any-of clause over an `OsduField` (text, keyword, number, boolean,
  date), with the nested forms; the aggregation field of a property (shared with the `groupBy` assertion, whose nested
  case is wrong today).
- `OsduSearch`: buckets keep a missing key apart from the text `null`.
- `DistinctValues`: the paging and scanning engine, with its counters.
- Tests: the query builders; the engine against a fake search that caps buckets and applies ranges, with tens of
  thousands of values, repeated values, nested arrays, unusable split points, numbers without keys and a scan fallback.

### Stage 2: the document

- `DimensionFlowDefinition`, its YAML model and mapper, the clean steps, the record property shapes, the flow kind, its
  lineage (reads the kind), the run payload's `dimensions` selection, and the census file for the editor.
- Tests: every rule of the document, each refusal naming the place; the clean steps; the census.

### Stage 3: the tables

- Entities, the migration with its designer and snapshot, the schema version, and the ledger's write and read methods.
- Tests, SQL Server: a first build, a second that adds, removes, moves and brings back values, stable ids, the change log,
  a failed build writing nothing, the lock, and the migration from the previous version.

### Stage 4: the build

- `DimensionRunner` (build and plan) and `DimensionExecutor`, registered in every host.
- Tests: a build end to end against a fake platform into SQL Server; plan; a failing dimension beside a passing one.

### Stage 5: reading dimensions

- API endpoints (dimensions, one dimension, its members and values, filters, builds and changes), CLI verbs, and the GUI
  (a Dimensions page, a dimension page with members, originals, filters and builds, and the pipeline panels).
- Tests: API tests; GUI build and lint.

### Stage 6: dimensions in the cache

- A cache type `dimension: <name>` with `dimensionFlow: <flow>` (a dimension is named by its flow, since two flows of a
  partition may each declare one of a name) holds the members the dimension's last completed build wrote as a lookup
  table keyed by the clean value (`value`), with `originals` (a set, which a lookup matches on any one of) and `records`
  beside it, so a mapping conforms a source value to its clean form with `replace from $cache.<name> (originals to value)`.
  A dimension over 100,000 members or 500,000 originals refuses the capture.
- Tests: the capture, a refresh before the first build, lineage ordering the dimension flow first.

### Stage 7: documentation

`documents.md`, `design.md` section 15, `ledger.md`, `operations.md`, the CLI reference, the decision record, the
integration brief's aggregation facts, and the samples README.

## Close-out

A clean rebuild with zero warnings, the GUI build and lint, every suite with SQL Server, and
`tools/check-vendored-sqlflow.sh` passing. No live OSDU run is part of this plan; a live check of the range queries and the
numeric keys is listed and approved first, under the project's rules.
