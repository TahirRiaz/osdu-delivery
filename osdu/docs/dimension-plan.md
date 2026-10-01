# Plan: dimension flows

A dimension is the set of distinct values one attribute of an OSDU kind holds, each kept exactly as the search index holds
it (the **key**: the text a search compares, the id for a reference) and grouped under the human-friendly **value** a
person picks (the **label** read from the record the key names, or the key itself, cleaned). Its purpose is filtering
OSDU search: a person or an application picks values, in one dimension or across several, and the dimensions give the
query that finds every record holding their keys. OSDU has no way to list the distinct values of an attribute beyond the
first thousand, none to group spellings, and none to join a log to its wellbore's name or country, so this is a flow
kind of its own, `flowType: dimension`, with its own tables in the `osdu` schema, and a cache origin that lets a mapping
read a dimension like any lookup table.

A key can carry **attributes**, further facts read the way its label is (a wellbore's country and field), which values
and keys are looked up by and a search picks keys by. A value is ready for a drop-down: a key naming an OSDU record and
no label read is valued by the code its id ends with, its escapes decoded, never the escaped id.

In code and in the tables, a value is a **member** (`DimensionMember`) and a key an **original** (`DimensionValue`), the
names they were built with; everything a person reads (the API, the CLI, the GUI, the exports, the cache columns) says
value and key.

Each stage lists what it changes and the tests that close it. A stage is finished only when those tests pass, SQL Server
suites included. All work is in `osdu/`; nothing in `sqlflow/` changes. Stages 1 to 12 are built; the recall estate
(`B:\osdu-recall-metadata`) holds a demo flow, `recall/flows/recall-welllog-05-dimensions.yaml`, whose one dimension,
Wellbore, carries the filters of PetroDB's Log Explorer (country, field, UUID, and the sources its logs collect) as its
attributes, the table its cascading selects read. The live check listed under Close-out has not been run.

## Decisions

| Decision | Choice | Why |
| --- | --- | --- |
| Where the data lives | A flow kind of its own with tables of its own, not a cache origin | The cache is loaded whole for every render (lookup tables stop at 100,000 rows) and one partition-wide version moves whenever anything in it does; a dimension carries counts and first and last seen that move with every build. Retrieval and assertion flows set the pattern: each reverse-direction job is its own kind reading through the shared search client. |
| What is stored | Distinct values only: values and the keys under them, with counts, labels and filters | Records stay in OSDU; a dimension is its vocabulary. |
| What a key is | The value exactly as the index holds it; for a reference, the id | A filter matches what the index holds, and an exact match on text reads the `keyword` sub-field, which holds the value as written. |
| What a value is | The label read from the record the key names, cleaned; the key cleaned when there is no label | A person picks by name (`15/9-F-1`, `Norway`); the search compares ids. Keeping both, with the filter written from the keys, lets the name choose and the id find. |
| How a label is read | By id through the search service, 500 ids a search, in the kind of the entity type the id names; up to three records deep; a segment holding objects can filter them | A search cannot join; reading each named record once per build, and keeping what it said, answers every later filter and search from the ledger. |
| Attributes | Read with the label, one row per key and attribute in `DimensionAttribute`, indexed by name and value | A filter panel narrows one dimension by another's facts (wellbores of a country); an indexed row answers that lookup in one seek at any size. |
| A key with no label | Valued by its id's code, escapes decoded, when it names a record | A value is shown in a drop-down: `us%2Fft` is `us/ft`, and a GUID id is still better than the whole id. |
| How values are read | The search's `aggregateBy`, paged by value ranges; a cursor scan where aggregation cannot answer | `aggregateBy` returns at most `aggregationSize` buckets (1000 by default, a platform setting, not a request parameter) and has no paging of its own [19 SRC/config/SearchConfigurationProperties.java:23; SRC/util/AggregationParserUtil.java:65-71]. |
| What cannot be a key | Values search cannot match exactly | A text value longer than 256 characters is not in the `keyword` sub-field at all (`ignore_above: 256`), and a null text is indexed there as the text `null` (`null_value`) [25 IC/util/TypeMapper.java:262-268]. Neither can be filtered on exactly, so both are counted and reported, never stored as keys. |
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
    countRecords: true               # a record count per value where the index counts objects or repeated values
  - name: Operator
    kind: "osdu:wks:master-data--Wellbore:*"
    path: data.CurrentOperatorID     # keys: the organisation ids
    label: data.OrganisationName     # values: each organisation's name
  - name: Country
    kind: "osdu:wks:work-product-component--WellLog:*"
    path: data.WellboreID            # keys: the wellbore ids
    label: [data.GeoContexts.GeoPoliticalEntityID, data.GeoPoliticalEntityName]   # through the wellbore to its country
reliability: { concurrency: 4, timeoutSeconds: 100 }
schedule: { cron: "0 3 * * *" }
```

Clean steps, applied in order to each key's label (or the key, when it has none): `trim`, `collapseSpaces`, `upper`,
`lower`, `nfc`, `nfkc`, `foldSeparators` (letters and digits kept, every run of anything else one hyphen, lower-cased: the
cache's own separator fold), `replace: { pattern, with }` (a regular expression run without backtracking), and `map: {
dictionary, field, otherwise }` (a dictionary document of the repository; `otherwise` left out keeps an unlisted value,
`~` leaves the key out of every value, and a text replaces it). No steps keep the label, or the key, as the value.

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
5. **Completeness.** Beside the keys, each build counts the records the query matches, and for text outside a nested
   array the records holding a value but no keyword (values over 256 characters) and those whose value is null. Buckets
   that sum to less than the records holding a value say the index moved during the build or values are repeated; the
   build says so and keeps what it read.

## Keys and values

With a `label`, a build reads each key's label after it has read the keys:

1. A key that is a record reference (`partition:entity-type:id:` with an optional version) names a record; any other key
   has no label.
2. The ids (without their version) are grouped by the entity type they name and searched in that type's kind
   (`*:*:master-data--Wellbore:*`), 500 ids a search, `id:("a" OR "b" ...)`, returning only `id` and the step's path.
3. For every step but the last, every record reference the path holds in every record reached is followed (in order,
   each once, at most 20 a key); at the last step, the first record reached whose path holds a non-empty text gives the
   label, cut at 1,024 characters. A segment holding objects can filter them: `[Property=text]` keeps those whose
   property equals the text, `[Property*=text]` those whose property contains it ignoring case, `[Property$=text]` those
   whose property ends with it ignoring case; the search is asked to return the filter's property with the path. So
   `[data.GeoContexts.GeoPoliticalEntityID, 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName']`
   reads, of every political entity a wellbore names, the one whose own type is Country, as petrodb-api does, whether
   or not the wellbore's context says its type.
4. A key whose record the search does not hold, whose record holds no reference where a step reads one, or holds nothing
   at the last path, has no label; the build counts them (`Unlabelled`) and says why in its notes, with examples.

The key keeps the id, its label and the id of the record the label came from are kept beside it, and the label is what
the clean steps turn into the key's value. A key whose label is not read takes the dimension's `unlabelled` value when
it names one (`Not specified`, the option an application lists for a missing name); otherwise a key naming a record
with no label (none declared, or none read) starts cleaning from the code its id ends with, its escapes decoded
(`dev:reference-data--UnitOfMeasure:us%2Fft:` is `us/ft`), and a label or attribute that is itself a record reference
is kept the same way. Keys whose values are the same are one value (every wellbore of a country is
one `Norway`). A key is left out of every value, with the reason kept on its row, when cleaning leaves nothing, when the
value is longer than 256 characters, or when `map` leaves it out. A key a query cannot carry (a control character,
`nested(`, or inside a nested array a value the service rewrites) stays under its value and is marked unfilterable; the
value's filter covers the rest and says how many it cannot.

## Attributes

A dimension's `attributes` name further facts of each key, each read as a label is (a path of the record the key names,
or up to three through its references, filters allowed). A build reads every chain at once, step by step: at each step,
every record of one entity type any chain needs is found in the same searches, asking for every path needed there, so a
wellbore's label, country and field take one search per 500 wellbores, then one per 500 countries and fields. A key keeps
each attribute's value (the first non-empty one the path reaches, at most 256 characters) and the record it came from,
one row per key and attribute in `DimensionAttribute`; a build rewrites the attributes of the keys it found, and a key
with no value for one keeps none (the notes count them, by reason).

The attributes are read, never searched in OSDU: they answer from the ledger. A page of values or keys is narrowed by
them (`attr=Country:Norway`: any value of one attribute, every attribute named, held by one key), an attribute lists the
values its keys hold with their keys and records (a drop-down's list), a search picks a dimension's keys by them (the
part compares exactly those keys, at most 1,000), and a cached dimension carries those its `fields` name, so a mapping
finds a key's attribute from the cache. With `unlabelled`, a key with no value of an attribute holds that value instead.

## Collected attributes

An attribute can be collected from the dimension's own records rather than read from the record a key names:
`Source: { collect: data.Source }` on a dimension keyed by the logs' `data.WellboreID` gives each wellbore the source of
each of its logs. A build reads the path's distinct values as it reads any field's (as many as the dimension's
`maxValues`), and shows each as a label is (a reference by its decoded code, trimmed); a text no query can carry, or one
shown as nothing, is no value and counts as none. It then reads which keys hold each value the cheaper of two ways, both
exact:

- With few values (at most 500, which one query can exclude) and fewer reads than a pass would take pages: a distinct
  read of the dimension's own field per value, narrowed to the records holding one of its texts, so a value held by many
  keys pages as any field does; with `unlabelled`, one more read narrowed to the records holding none of them.
- Otherwise one pass of the search cursor over the dimension's records, each record's key and value read together: a
  page per thousand records, however many values there are. This is the read for a long free-text list such as
  `data.Source`, which every producer of a partition writes in its own way.

A key holds a row per value (`DimensionAttribute` is keyed by value as well), each with its records. The dimension keeps
the field the attribute is read from (`CollectedJson`) and every text with the value it is shown as and its records
(`DimensionCollectedText`, indexed by value), which a search picking a value asks for: the records holding one of its
texts, or, for the `unlabelled` value, every record holding none (`_exists_:id AND NOT (...)`, since the service refuses
a query that only excludes). That exclusion holds at most 1,000 texts, so an attribute of more values refuses a pick of
its `unlabelled` value, saying why; its values can always be picked.

A dimension collects one attribute: two would pair values no record holds together (one log's source with another's
type), and its table would offer combinations a search then finds nothing for. The document refuses a second one,
naming the first.

## The table

A dimension is one table in the database: `osdu.dim_<flow>_<dimension>`, the flow's and the dimension's names with
whatever is not a letter, a digit or an underscore made an underscore (`osdu.dim_recall_welllog_05_dimensions_Wellbore`).
It is the dimension as a report, a query or a cascade of selects reads it, and what a table of facts joins on. A build
makes it and keeps it; nothing has to be declared for it.

| Column | Holds |
| --- | --- |
| `id` | The row's number: an identity, the table's clustered primary key, and what a table of facts joins on. It stays the same for as long as the dimension holds the row. |
| `partition` | The data partition the row was read in. A flow that builds in several partitions writes them all to this one table. |
| `key_id` | The key's number (`DimensionValue.ValueId`): the same in every row of the key. |
| `key` | The key exactly as the index holds it. |
| `value` | The key's value. |
| `records` | The records of the row: those holding the value the row collects, or every record of the key. |
| `filter` | The search filter finding the key's records; null when no query can carry the key. |
| one per attribute | The attribute's value for the row, under the name the dimension declares; null where the key has none. |

A row is a key and the value it collects: a dimension that collects an attribute has a row for each value a key holds,
any other a row a key. A key under no value (left out by cleaning), and a key no build finds any more, is no row.

**The schema follows the declaration.** SQLFlow's schema evolution, the same that widens an ingestion table
(`SchemaSyncService`: it reads the table as it is, plans the difference, and applies it), brings the table to the
columns the declaration asks for before each write: a first build creates the table with its identity key, and an
attribute the flow starts to declare gets its column on the next build, with no migration and nothing done by hand.
Nothing is ever dropped or narrowed. An attribute the flow stops declaring keeps its column, emptied by the next
build, so a query that names it still runs; a column somebody added to the table is theirs, and is left as it is. A
table changed by hand into something a build cannot write (its `id` no longer a number, say) fails the build, saying so.

**The rows are written with the dimension.** The build lays the dimension out from what it has just kept (each
attribute read from a key's record once a key, the collected attribute once a value) and writes what differs from the
rows the table holds, in the transaction that writes everything else: a row that left is deleted, one that changed
rewritten where it is, a new one added. A build that found the same thing writes no row, and a row keeps its `id`. When
the attribute that makes the rows changes (the collected one is added, taken out or replaced), no row can be matched to
what it was, and the partition's rows are written again under new numbers.

**It is read through two indexes** the build makes with it: `IX_key` (`partition`, `key_id`), which the build matches
its rows by and a join on a key seeks; and `IX_value` (`partition`, `value`, `id`), which a page in value order is read
from and a count counts. A page is found before it is read: the numbers of its rows are ordered first (a number and the
column ordered by, never a key or a filter), then those rows are read by number. Text is found anywhere in the key, the
value or an attribute, case-folded and compared exactly, and an attribute's value is matched exactly; either reads the
partition's rows once, for the count and the page together. Measured on a dimension of 200,000 keys collecting two
values a key (400,000 rows, beside two other dimensions of the same size) on a developer's SQL Server: a first page in
value order 49 ms, a page 100,000 rows in 70 ms, an attribute's value 44 to 114 ms, text found anywhere 0.4 s, a page
ordered by another column 0.45 to 1 s. A build that found nothing new checks the 400,000 rows in about 3 s; the first
build writes them in about 9 s.

**Who reads it.** The page's **Table** tab, the API's `GET /dimensions/{id}/table`, `sqlflow dimensions table`, the
`table` export, and any SQL client, all the same rows. A dimension built before dimensions had tables, or whose table
was dropped by hand, has it made with its rows by whoever next reads it. The table is no table of the module's model:
no migration creates or changes it, `Dimension.TableName` names it, and the migration that added that column drops the
tables builds made when it is taken back.

Two dimensions cannot write one table. A flow whose two dimensions' names differ only in characters a table's name
leaves out (`Well.Type` and `Well-Type`) is refused where it is read; a dimension of another flow whose table has the
same name, or a second ledger's copy of the same dimension in the same partition, fails its build, naming the other.

Each select of a cascade lists the distinct values of its column among the rows the other selects leave. The API and the
CLI also answer one select's list from the ledger (`attributes/<name>?attr=...&value=...`,
`sqlflow dimensions attributes --attr ... --value ...`), among the keys holding every other attribute value picked and
belonging to the values picked, a pick of the attribute itself aside. A list counts keys exactly; its records are those
of the keys holding each value (for a collected value, of its records).

## Removing a dimension

A dimension its flow no longer declares is built no more, and keeps what its last build wrote, so taking it out of the
YAML loses nothing and declaring it again brings back its ids and history. It stays out of the way: the flow's
Dimensions tab lists it under **No longer declared**, and the Dimensions page only when asked to. When it is not wanted
any more, an admin removes it for good (`DELETE /dimensions/{dimensionId}`, the GUI's **Remove**, or
`sqlflow dimensions remove`, all through `DimensionRemoval`):

- Only a dimension its flow no longer declares, read from the flow as the catalog (or the file, for the CLI) holds it
  now; one whose flow cannot be read now is refused, since whether it still declares the dimension cannot be told.
- Not one a cache flow of its partition captures (`dimension:` in a cache type): its refresh would read a dimension that
  is gone. The refusal names the cache flow and the type.
- Everything of it in its partition and nothing else: its rows in its own table, its collected texts, attribute values,
  change log, keys, values, builds, the numbers of its attributes and then its own row, each a batch of 20,000 at a
  time under the dimension's write lock, so a build writing it finishes first; its own row goes last, so a removal that
  stops part way leaves it listed and removing it again finishes the work. Its table is dropped when no partition is
  left writing it.
- Recorded as a `remove-dimension` activity of its flow, before anything is deleted, with the actor (`user:<name>` from
  the API, `cli:<user>` from a workstation) and, as it ends, what went or why it failed.

## The filter

A key's filter is the query that finds exactly the records holding it; a value's is the query that finds every record
holding any of its filterable keys. Both are written the way the index holds the attribute: `data.X.keyword:("a" OR "b")`
for text, `data.X:("a" OR "b")` for a keyword, numbers unquoted, and `nested(data.C, (M.keyword:"a")) OR nested(data.C,
(M.keyword:"b"))` inside a nested array, one nested clause per key, since the service rewrites `OR x:` inside one. A
filter holds at most 500 keys, leaving room in the service's 1024 clauses for the query it is combined with; a value with
more has its filter in parts. The kind is the dimension's.

## The search

Values picked across dimensions compose one search (`POST /dimensions/search`, `sqlflow dimensions search`, the search
builder of the Dimensions page):

- Within a dimension, a record holding any key of the values picked there: the value filters joined with OR.
- Across dimensions, a record matching every dimension picked in: the parts joined with AND.
- Each dimension's own query is added once (dimensions of one flow usually share it), then a query of one's own.
- Every term is parenthesised when there are several, so an OR inside one never reaches across the AND between them.
- The dimensions have to read one kind, or the kind named, which each dimension's kind has to cover segment by segment:
  a dimension's keys filter only the kind they were read from.
- At most 20 dimensions and 1,000 clauses (each key compared is one, and each text of a collected value; the service
  allows 1,024). A pick past that is refused, saying how many keys and clauses the picks hold, rather than cut short.
- A collected value picks records, not keys: the part adds the records holding one of its texts to the keys compared,
  and collected values picked alone add them to the records holding a key.
- A value no build finds any more, and a name that is no value, are said to be left out; a key no query can carry is
  counted in the notes.

The search reads the ledger only; nothing is sent to OSDU. It answers the query, the request body the search service
takes, each dimension's part (the values it holds now, the keys compared, its filter) and the clause count.

## Tables

All in the `osdu` schema, under a ledger of kind `dimension` per flow and partition. Each is keyed by the ledger
partition's number and an identity of its own, which is what the other tables name its rows by: every join between them
is on numbers, never on a text.

| Table | One row per | Holds |
| --- | --- | --- |
| `Dimension` | flow, partition and dimension name | The declaration as last built: kind, query, path, the label's paths, the attributes, the field asked, the field each collected attribute is read from, the clean steps, the definition hash, current value and key counts, the last build, and the name of its own table. |
| `DimensionRun` | dimension and build | Status, counts (records, values, keys, added, removed, moved, left out, unfilterable, too long, null, labelled, unlabelled), requests (label searches among them), slices, scanned records, templates used, notes. |
| `DimensionMember` | value | Stable id, the value, count and whether it is exact, keys, filter and parts, first and last seen, removed. |
| `DimensionValue` | key | Stable id, the key, its label and the record it came from, its value or why it has none, count, filterable, its filter, first and last seen, removed, the build it joined its value. |
| `DimensionAttributeName` | attribute of a dimension | The attribute's number, its name as declared, whether it is collected, and its place among the attributes the dimension declares now (none once it is no longer declared). |
| `DimensionAttribute` | key, attribute and value | The attribute by its number, the value, where it was read (the record, or a collected attribute's text), and for a collected value how many of the key's records hold it. |
| `DimensionCollectedText` | collected attribute and text | The attribute by its number, the text exactly as the index holds it, the value it is shown as, and the records holding it. |
| `DimensionChange` | key that moved, left, came back or arrived after the first build | The build, the key, from and to value. |
| `dim_<flow>_<dimension>` | key and value it collects | The dimension as one table (The table, above): made and widened by builds, not by migrations. |

A build writes in one transaction: its values are copied into temporary tables, and set-based statements add, update and
mark removed, give each attribute its number, and bring the dimension's own table to what was kept; an application lock
per dimension keeps two builds of it from interleaving. Only the table's schema is settled before the transaction: a
table made or a column added stays when the write does not, empty, and the next write finds it there. A failed build writes nothing
but its run row. Nothing is deleted: a value or key a build no longer finds is marked removed, and one that comes back
keeps its id. A key whose label, value or filter changed is rewritten, and a move to another value is logged.

## Edge cases

| Case | Handling |
| --- | --- |
| More distinct values than one aggregation returns | Paged by value ranges until every slice is complete. |
| A record holding many values (arrays, nested arrays) | Out-of-range buckets are dropped per slice; a slice they fill is split, and one that cannot split is scanned. |
| A split point the service would misread | The nearest usable key is taken; with none, the slice is scanned. |
| Numbers with no bucket key | Scanned. |
| Text over 256 characters | Not in the keyword field: counted as too long, never a key. |
| Null text | Indexed as `null`: counted as null, never a key; a literal text `null` cannot be told apart and is treated the same. |
| An attribute no record holds | Zero keys; the build says so. |
| An attribute the index cannot match exactly (unindexed arrays of objects, geo shapes, objects) | Refused when the flow is loaded or when the template is read, naming why. |
| A kind whose versions index the path differently, or one without a saved template | Refused, naming the kinds. |
| Aggregation answers nothing though records hold the attribute | The dimension fails, saying the field asked and suggesting the template does not match the index. |
| The index changes during a build | Slices are disjoint, so nothing is counted twice; counts may lag, and the build says when the totals disagree. |
| Clean steps that produce nothing, or text over 256 | The key is kept with the reason, of no value. |
| A key that names no record, or one the search does not hold | No label: the key is its own value, and the build's notes count them with examples. |
| A label path holding several values (an array) | The first non-empty one is read; for a step before the last, the first record reference. |
| A label that changes (a wellbore renamed) | Read again by the next build; the key moves to the new value and the change log says so. |
| A wellbore in a region and a country | Every entity is followed, and a filter on the entity's own type (`data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:]`) keeps the country. |
| An attribute a key's record does not hold | The key holds no row for it; the build's notes count such keys, by reason. |
| An id escaping what it cannot hold (`%2F`) | The key keeps it; the value decodes it. |
| A regular expression that would backtrack | Run with the non-backtracking engine; a pattern it cannot run is refused at load. |
| A dictionary that is missing or invalid | That dimension fails; the others build. |
| More keys than `maxValues` (1,000,000 by default, at most 5,000,000) | That dimension fails before writing, naming the count. |
| Two builds of one dimension at once | The second waits for the first's lock and fails with a clear message if it cannot get it. |
| A search failure part way | Retried by the flow's reliability settings; a dimension that still fails writes nothing and the others build. |
| A value whose filter would pass the clause limit | Its filter is in parts; the API and the CLI hand every part. |
| Values picked across dimensions holding more than 1,000 keys | The search is refused, saying how many keys they hold. |
| A collected attribute with many values (a free-text source every producer writes its own way) | Read in one pass over the records; a pick of its `unlabelled` value is refused past 1,000 values, since one search cannot exclude more. |
| A second collected attribute | Refused at load, naming the first. |
| Two texts of a collected path shown alike (`RECALL` and `RECALL `) | One value, standing for both; a search picking it asks for both. |
| A collected value a pick names that the attribute does not hold | Named as missing; a pick holding no value it does hold is refused. |
| Dimensions of different kinds picked in one search | Refused unless a kind is named that each dimension's kind covers. |
| A value that disappears and comes back | Keeps its id; the change log says when. |

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

- API endpoints (dimensions, one dimension, its values and keys, filters, builds and changes), CLI verbs, and the GUI
  (a Dimensions page, a dimension page with values, keys, filters and builds, and the pipeline panels).
- Tests: API tests; GUI build and lint.

### Stage 6: dimensions in the cache

- A cache type `dimension: <name>` with `dimensionFlow: <flow>` (a dimension is named by its flow, since two flows of a
  partition may each declare one of a name) holds the values the dimension's last completed build wrote as a lookup
  table keyed by the value (`value`), with `keys` (a set, which a lookup matches on any one of), `records` and `filter`
  beside it, so a mapping turns a raw key into its value with `replace from $cache.<name> (keys to value)`. A dimension
  over 100,000 values or 500,000 keys refuses the capture.
- Tests: the capture, a refresh before the first build, lineage ordering the dimension flow first.

### Stage 7: documentation

`documents.md`, `design.md` section 15, `ledger.md`, `operations.md`, the CLI reference, the decision record, the
integration brief's aggregation facts, and the samples README.

### Stage 8: keys, values, labels and the search

- The document's `label` (a path, or up to three); `DimensionLabeler` reading labels by id through the search service;
  each key's label, the record it came from and its own filter kept; the migration `DimensionLabels` (module version
  1.18.0) with the build's label counts.
- `DimensionSearch` composing the search across dimensions, `POST /dimensions/search`, `sqlflow dimensions search`, and
  the GUI's search builder; the API, CLI, exports and cache columns named in keys and values.
- Tests: the label rules of the document; a build labelling through one record and through two against a fake search that
  returns records by id, the composed search run against the same fake and finding exactly the expected records; the API
  (keys with labels and filters, a key found by its label, the search, its refusals); the migration from the previous
  version keeping every key.

### Stage 9: attributes, filtered paths and values ready for a drop-down

- The document's `attributes`; a path segment's filter (`[Property=text]`, `[Property*=text]`); `DimensionLabeler`
  reading the label and every attribute in shared searches; `DimensionAttribute` and the declaration's `AttributesJson`
  (migration `DimensionAttributes`, module version 1.19.0); values and keys narrowed by attributes, an attribute's
  values, a search picked by attributes, attributes in the exports and in a cached dimension's `fields`; the API, the
  CLI (`attributes`, `--attr`, `--where`) and the GUI (attribute columns, the attribute filter, the builder's "where").
- A key naming a record with no label is valued by the code its id ends with, its escapes decoded, or by the dimension's
  `unlabelled` value (`Not specified`).
- Tests: the attribute and filter rules of the document; the path reader; the decoding; a build reading attributes
  through a filtered context and two records, looked up, listed, searched by (the query run against the fake), and read
  again after a rename; the cache carrying an attribute; the API; the migration from the previous version.

### Stage 10: collected attributes and the table

- The document's `{ collect: <path> }` (one per dimension) and `unlabelled` for attributes; the build's collected reads;
  `DimensionAttribute` keyed by value with `Records`, and the dimension's `CollectedJson` (migration
  `DimensionCollectedAttributes`, module version 1.20.0); a collected value picking records in a search; an attribute's
  values narrowed by the other picks (ledger, API, CLI); the export set `table`.
- Collecting reads per value or in one pass, whichever is cheaper, with no limit but `maxValues`, and the texts a value
  stands for move to `DimensionCollectedText` (migration `DimensionCollectedTexts`, module version 1.21.0), after the dev
  partition's `data.Source` proved to hold more than the 200 values the first version allowed.
- Tests: the collect rules of the document; a build collecting sources from logs, with a text spelled two ways and records
  holding none; the lists of a cascade, each narrowed by the others; the table; searches picking a collected value alone,
  with Not specified and with a country, run against the fake; a rebuild; the API's narrowed list and table; the
  migration up and back down; few values over 3,600 logs read per value with no cursor; 1,100 values read in one pass,
  one picked, and Not specified refused; the texts migration up and back down.

### Stage 11: removing a dimension

- `DimensionRemoval`, `ILedger.RemoveDimensionAsync` and `DimensionCapturesAsync`; `DELETE /dimensions/{dimensionId}`
  (admin), `sqlflow dimensions remove`; the GUI listing dimensions no longer declared apart and an admin's **Remove**.
- Tests: a removal taking every row of the dimension from all seven tables and nothing of the other dimension, refused
  while a cache flow captures it, recorded as an activity, and failing when nothing is left; the API refusing an operator
  (403) and a declared dimension (409), and removing a retired one for an admin.

### Stage 12: the dimension as a table, joined by numbers

- `DimensionTables` (the table's name, its columns, its indexes, the statements that write its rows) on SQLFlow's
  schema evolution; the build's write laying the dimension out and writing what differs; `DimensionTable`, which reads
  it and makes it when it is missing; `ILedger.EnsureDimensionTableAsync`, `DimensionTableShapeAsync`,
  `ReadDimensionTableAsync` and `StreamDimensionTableAsync`.
- `DimensionAttributeName`: attributes by number; `DimensionAttribute` and `DimensionCollectedText` keyed by an identity
  and naming their attribute by number; `Dimension.TableName`; `IX_DimensionValue_PartitionId_DimensionId_ValueId`
  (migration `DimensionTables`, module version 1.22.0, which keeps every attribute row).
- `GET /dimensions/{dimensionId}/table`; `sqlflow dimensions table`; the `table` export written from the table; the
  page's **Table** tab, a dimension's first, and its **Definition** tab in four parts.
- Tests: a first build making the table with its identity key and indexes; a page searched, narrowed, ordered and paged;
  a build of the same thing writing no row; an attribute added gaining its column and a collected one rewriting the
  rows; a changed row rewritten under its number; a retired attribute's column emptied and somebody's own column left;
  a dropped table, and one never made, made on reading; a key no longer found leaving; removal dropping the table; a
  table name another flow writes refused; the names; the document refusing two dimensions of one table name and an
  attribute named after a column; the API's table, its page, its refusals and its export; the migration up, down and up
  again with attribute rows kept.

## Close-out

A clean rebuild with zero warnings, the GUI build and lint, every suite with SQL Server, and
`tools/check-vendored-sqlflow.sh` passing. No live OSDU run is part of this plan; a live check of the range queries and the
numeric keys is listed and approved first, under the project's rules.
