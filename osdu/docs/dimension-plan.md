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
value and key. A dimension's own table goes one step further: its two columns are named after what the dimension reads
(`WellboreID` and `FacilityName`, not `key` and `value`), and so are the grids and the table export that show it (The
table).

Each stage lists what it changes and the tests that close it. A stage is finished only when those tests pass, SQL Server
suites included. All work is in `osdu/`; nothing in `sqlflow/` changes. Stages 1 to 17 are built (17: Full and incremental loads); the WellDB
estate (a local estate repository) holds a demo flow,
`welldb/flows/welldb-welllog-05-dimensions.yaml`, whose one dimension, Wellbore, carries the filters of the facade
service's log explorer (country, field, UUID, and the sources its logs collect) as its attributes, the table its
cascading selects read. The live check listed under Close-out has not been run.

## Decisions

| Decision | Choice | Why |
| --- | --- | --- |
| Where the data lives | A flow kind of its own with tables of its own, not a cache origin | The cache is loaded whole for every render (lookup tables stop at 100,000 rows) and one partition-wide version moves whenever anything in it does; a dimension carries counts and first and last seen that move with every build. Retrieval and assertion flows set the pattern: each reverse-direction job is its own kind reading through the shared search client. |
| What is stored | Distinct values only: values and the keys under them, with counts, labels and filters | Records stay in OSDU; a dimension is its vocabulary. |
| What a key is | The value exactly as the index holds it; for a reference, the id | A filter matches what the index holds, and an exact match on text reads the `keyword` sub-field, which holds the value as written. |
| What a value is | The label read from the record the key names, cleaned; the key cleaned when there is no label | A person picks by name (`Wellbore A-1`, `United States`); the search compares ids. Keeping both, with the filter written from the keys, lets the name choose and the id find. |
| How a label is read | By id through the search service, 500 ids a search, in the kind of the entity type the id names; up to three records deep; a segment holding objects can filter them | A search cannot join; reading each named record once per build, and keeping what it said, answers every later filter and search from the ledger. |
| Attributes | Read with the label, one row per key and attribute in `DimensionAttribute`, indexed by name and value | A filter panel narrows one dimension by another's facts (wellbores of a country); an indexed row answers that lookup in one seek at any size. |
| A key with no label | Valued by its id's code, escapes decoded, when it names a record | A value is shown in a drop-down: `us%2Fft` is `us/ft`, and a GUID id is still better than the whole id. |
| How values are read | The search's `aggregateBy`, paged by value ranges; a cursor scan where aggregation cannot answer | `aggregateBy` returns at most `aggregationSize` buckets (1000 by default, a platform setting, not a request parameter) and has no paging of its own [19 SRC/config/SearchConfigurationProperties.java:23; SRC/util/AggregationParserUtil.java:65-71]. |
| What cannot be a key | Values search cannot match exactly | A text value longer than 256 characters is not in the `keyword` sub-field at all (`ignore_above: 256`), and a null text is indexed there as the text `null` (`null_value`) [25 IC/util/TypeMapper.java:262-268]. Neither can be filtered on exactly, so both are counted and reported, never stored as keys. |
| Operation names | `build` (the default) and `plan` | `plan` reads templates and counts, as every kind's plan does, and writes nothing. |
| Views | Declared in the flow, joins and columns alike, and checked against the saved templates; never inferred | A view is what a pipeline reads: it changes only when its document does, the same on every host and in every partition, whichever templates are saved. |
| Where a view lives | `osdu.v_dim_<view>`, beside the dimension tables it reads, the second exception to "only migration-owned objects in `osdu`" | It reads nothing but dimension tables and comes and goes with them; a schema of its own would be one the module makes at run time, outside its migrations. |
| A view's columns | A T-SQL scalar expression and a data type each, read by SQL Server's own parser and written back by the module from a listed set of operators and functions | SQL Server is the module's only provider, so a person writes what they know; a view written from the parsed tree reads the dimensions it joins and nothing else, which text passed through could not promise. |
| How a join compares keys | The partition, a SHA-256 of the key (`key_hash`), and the text under `Latin1_General_100_BIN2` | A key is up to 1,024 characters, over SQL Server's 1,700-byte index key, and the database's collation may equate ids that differ only in case, which OSDU holds as different records. |
| What orders a pipeline reading a view | `target.connection`, the module's database as the pipeline names it, required with views and checked against the module's own connection | SQLFlow identifies a server by its reference as written, and the module's connection is configured per host, so only a reference the flow declares meets the pipeline's. |
| Full or incremental | Declared in the flow, an `incremental` block naming a record's unique key (`keyColumns`) and when it last changed (`dateColumns`, `[modifyTime, createTime]` by default); without it every build loads in full | A build reads what the document says, on every host; SQLFlow's own `fullLoad` and backfill window ask a single run for a full load or a window, as they do of SQLFlow's flows. |
| When a record changed | The first of `dateColumns` it holds | OSDU writes `createTime` alone on a record's first version and `modifyTime` from its second on, and the indexer indexes `modifyTime` only when storage holds one (storage-core `IngestionServiceImpl`, indexer-core `IndexerServiceImpl`), so a range on `modifyTime` alone never finds a record never modified. A nested object changes only with its record. |
| What an incremental load reads again | The keys the changed records hold now and held before, and the keys read through a record that changed, each read whole over the dimension's query | Counts, labels and collected values are exact for every key read again; what a changed record held before is known from the keys a load kept by its unique key (`DimensionRecord`), and the records a key's reads went through from `DimensionKeyRecord`. |
| What an incremental load cannot see | A record that left the index; its keys and counts stay until a full load (`fullLoadAfterHours`, `fullLoad`) | A soft-deleted record is gone from every search, so no read can say what it held; the load says how many at least have left. |

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
    columns: { key: OperatorID, value: Operator }   # optional: the table's two columns, else CurrentOperatorID and OrganisationName
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

`columns: { key, value }` names the two columns of the dimension's table that hold each key and its value, when the
names the dimension reads by will not do (The table): left out, they are the property `path` ends with and the property
`label` ends with, or the dimension's own name when it reads no label, the key's with `Key` at its end where the two
would be the same.

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
   nearest key that is not. The ranges still to read are asked several at a time, as many as the flow's
   `reliability.concurrency` (8 unless it says otherwise), the widest first; what each answer leads to is settled one
   answer at a time, so the counts are the same whatever was in flight.
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
   reads, of every political entity a wellbore names, the one whose own type is Country, as facade-api does, whether
   or not the wellbore's context says its type.
4. A key whose record the search does not hold, whose record holds no reference where a step reads one, or holds nothing
   at the last path, has no label; the build counts them (`Unlabelled`) and says why in its notes, with examples.

The key keeps the id, its label and the id of the record the label came from are kept beside it, and the label is what
the clean steps turn into the key's value. A key whose label is not read takes the dimension's `unlabelled` value when
it names one (`Not specified`, the option an application lists for a missing name); otherwise a key naming a record
with no label (none declared, or none read) starts cleaning from the code its id ends with, its escapes decoded
(`dev:reference-data--UnitOfMeasure:us%2Fft:` is `us/ft`), and a label or attribute that is itself a record reference
is kept the same way. Keys whose values are the same are one value (every wellbore of a country is one
`United States`). A key is left out of every value, with the reason kept on its row, when cleaning leaves nothing, when
the value is longer than 256 characters, or when `map` leaves it out. A key a query cannot carry (a control character,
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
them (`attr=Country:United%20States`: any value of one attribute, every attribute named, held by one key), an attribute
lists the values its keys hold with their keys and records (a drop-down's list), a search picks a dimension's keys by
them (the part compares exactly those keys, at most 1,000), and a cached dimension carries those its `fields` name, so a
mapping finds a key's attribute from the cache. With `unlabelled`, a key with no value of an attribute holds that value
instead.

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
- Otherwise one pass over the dimension's records through the search cursor, each record's key and value read together:
  a page per thousand records, however many values there are. This is the read for a long free-text list such as
  `data.Source`, which every producer of a partition writes in its own way. A cursor hands over its pages one after
  another, so the pass is cut into ranges of the dimension's keys holding about as many records each (none under 4,000
  records, four ranges a reader) and the ranges are read side by side, each through a cursor of its own. A record
  holding keys of two ranges is read in both and counted in each under the keys of that range alone, so every record
  and key is counted once. Each range, like every cursor scan of a build, is read whole or the build fails: a range
  that loses a page or comes back short of the search's exact total is read once more from its first page, handing on
  only the records the first read did not, and when that read fails too the dimension's build fails and writes
  nothing, so the dimension keeps its last build ([design.md](design.md) section 15.1).

The labels and the attributes read through a key's record, and the values a key collects, need nothing of each other, so
a build reads them side by side, the flow's concurrency shared between them; the records of a label's or an attribute's
step are asked a thousand ids a search, several searches at a time. A flow whose `reliability.concurrency` is 1 asks
one thing at a time throughout.

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

A dimension is one table in the database: `osdu.dim_<dimension>`, the dimension's name with whatever is not a letter,
a digit or an underscore made an underscore (`osdu.dim_Wellbore`). It is the dimension as a report, a query or a
cascade of selects reads it, and what a table of facts joins on. A build makes it and keeps it; nothing has to be
declared for it.

**A dimension's name must be unique.** The table is named after the dimension alone, with no flow in its name, so no
two flows of a database declare a dimension of the same name: the second to build is refused, naming the flow that
writes the table, until one of them is renamed.

| Column | Holds |
| --- | --- |
| `id` | The row's number: an identity, the table's clustered primary key, and what a table of facts joins on. It stays the same for as long as the dimension holds the row. |
| `partition` | The data partition the row was read in. A flow that builds in several partitions writes them all to this one table. |
| `key_id` | The key's number (`DimensionValue.ValueId`): the same in every row of the key. |
| the key's, named after the path (`WellboreID`) | The key exactly as the index holds it. |
| the value's, named after the label (`FacilityName`) | The key's value. |
| `records` | The records of the row: those holding the value the row collects, or every record of the key. |
| `filter` | The search filter finding the key's records; null when no query can carry the key. |
| one per attribute | The attribute's value for the row, under the name the dimension declares; null where the key has none. |

A row is a key and the value it collects: a dimension that collects an attribute has a row for each value a key holds,
any other a row a key. A key under no value (left out by cleaning), and a key no build finds any more, is no row.

**The key's and the value's columns are named after what the dimension reads**, as its attributes' are, so the table
reads as a table of wellbores and not as keys and values: `SELECT [FacilityName], [Country] FROM osdu.dim_Wellbore`.

- The key's column is the property `path` ends with: `data.WellboreID` is `WellboreID`, `legal.legaltags` is
  `legaltags`.
- The value's column is the property the last `label` path ends with, its filter aside: `data.FacilityName` is
  `FacilityName`. A dimension that reads no label values each key by the key itself, cleaned, so its value's column
  takes the dimension's own name, whatever is not a letter, a digit or an underscore made an underscore: `CurveMnemonic`
  beside `Mnemonic`.
- Where the two would be the same, ignoring case (a dimension `Source` reading `data.Source` with no label, which is
  how a dimension is most often named), the value's column keeps the name, being the one a person reads and picks by,
  and the key's takes `Key` at its end: `Source` and `SourceKey`, `Version` and `versionKey`. So no document has to
  name a column for that.
- The document names either one itself, `columns: { key: <name>, value: <name> }`, and has to where a name the
  dimension reads by cannot name a column: where it is a column every table has (`path: id`), an attribute's name, or
  no column name at all (a dimension whose name starts with a digit, and no label), and it cannot give the key's
  column the value's name. The document is refused where it is read, saying which name and how to give another. A name
  the document gives also stays when the path or the label changes.
- A name is a letter, then letters, digits and underscores, at most 64, as an attribute's is; none of `id`,
  `partition`, `key_id`, `records` and `filter`; and unlike the other and every attribute, ignoring case. The key's
  column cannot be named `value`, nor the value's `key`: those two words ask for the two columns whatever they are
  named (`order=value`, `--order key`), so a reader written for every dimension needs no name.
- `Dimension.KeyColumn` and `Dimension.ValueColumn` record what the two columns of the table are named now, which is
  what a reader names them by, and what a query written for every dimension asks.

**A column is renamed where it is.** When a build finds the table holding its key or its value under another name
than the dimension gives (its path or its label changed, its document names the column otherwise, or the table was
made before dimensions named their columns and holds `key` and `value`), it renames the column before it brings the
schema to the declaration: the rows, their `id`s and the indexes stay, and only the name moves. The rename and the
names recorded on every dimension writing the table (one a partition) are one transaction, under a lock of the table,
so a reader never names a column the table does not have; a read that began just before it is asked again. A name
another column of the table holds (an attribute the dimension declared before, a column somebody added) is not taken
from it: the build fails, saying to drop that column or name the dimension's otherwise, and renames nothing. Two
columns that take each other's names, and a name that changes only in case, pass through a name of their own on the
way. A query that names the old column has to be changed with the flow; giving the column a name in the document
(`columns`) keeps it through a change of path or label. A reader that makes a missing table ready knows no document,
so it keeps the names the table had, and for a dimension that never had a table takes the names its path, its label
and its name give.

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
its rows by and a join on a key seeks; and `IX_value` (`partition`, the value's column, `id`), which a page in value
order is read from and a count counts. Each is named after what its column is for, so it keeps its name when the
column is renamed. A page is found before it is read: the numbers of its rows are ordered first (a number and the
column ordered by, never a key or a filter), then those rows are read by number. Text is found anywhere in the key, the
value or an attribute, case-folded and compared exactly, and an attribute's value is matched exactly; either reads the
partition's rows once, for the count and the page together. Measured on a dimension of 200,000 keys collecting two
values a key (400,000 rows, beside two other dimensions of the same size) on a developer's SQL Server: a first page in
value order 49 ms, a page 100,000 rows in 70 ms, an attribute's value 44 to 114 ms, text found anywhere 0.4 s, a page
ordered by another column 0.45 to 1 s. A build that found nothing new checks the 400,000 rows in about 3 s; the first
build writes them in about 9 s.

**Who reads it.** The page's **Table** tab, the API's `GET /dimensions/{id}/table`, `sqlflow dimensions table`, the
`table` export, and any SQL client, all the same rows, the key and its value under the names their columns have: the
grid's headings, the CSV's header, each property of a JSON Lines row and of the CLI's `--json` rows. The API's typed
rows keep `key` and `value` and say beside them what the table names the two (`keyColumn`, `valueColumn`); the Values
and Keys tabs and the change log head their columns the same way. The values and keys exports, which list the ledger
and not the table, and a cached dimension's rows keep `value`, `key` and `keys`. A dimension built before dimensions had tables, or whose table
was dropped by hand, has it made with its rows by whoever next reads it. The table is no table of the module's model:
no migration creates or changes it, `Dimension.TableName` names it, and the migration that added that column drops the
tables builds made when it is taken back.

Two dimensions cannot write one table. A flow whose two dimensions' names differ only in characters a table's name
leaves out (`Well.Type` and `Well-Type`) is refused where it is read; a dimension another flow declares by the same
name (or one apart only in such characters), or a second ledger's copy of the same dimension in the same partition,
fails its build, naming the other. A table a dimension wrote under an earlier name is dropped by its next build, once
no dimension names it.

Each select of a cascade lists the distinct values of its column among the rows the other selects leave. The API and the
CLI also answer one select's list from the ledger (`attributes/<name>?attr=...&value=...`,
`sqlflow dimensions attributes --attr ... --value ...`), among the keys holding every other attribute value picked and
belonging to the values picked, a pick of the attribute itself aside. A list counts keys exactly; its records are those
of the keys holding each value (for a collected value, of its records).

## The blueprint

A dimension's YAML says what to read, and the templates say what the records hold; the blueprint puts the two together,
so a person sees how a build makes each column, before a build and after one (`GET
/flows/{pipelineId}/dimensions/{name}/blueprint`, the page's **Definition** tab, `DimensionBlueprints`). It reads the
catalog's copy of the flow, the saved templates and the ledger; nothing is sent to OSDU.

- **The YAML, line by line.** The dimension's item under `dimensions`, comments and all, with where each thing it
  declares is written (`DimensionYamlSource`): its keys, each path of its label and of each attribute by its place
  (`label.0`, `attributes.Country.1`), a collected path (`attributes.Source.collect`), each clean step and each column's
  name. A list or a map written in brackets ends after its closing bracket, and a folded text on its own last line, so
  pointing at a line finds exactly what it declares.
- **The dimension's own records.** The kinds its last build read, each with its records and the template version it was
  read against; before a build, or once the kind pattern has changed, the saved templates the pattern matches. The key
  and each collected path are classified as the build classifies them (`SearchFields.ClassifyValue`, or the indexer's
  mapping for a property of the record itself), so the field a build aggregates is known before it runs, and a path the
  index cannot read says why.
- **The records read by id.** Each step of the label and of each attribute is read in the records the step before names:
  the entity types the value names by the template's `x-osdu-relationship` (the logs' `data.WellboreID` names
  `master-data--Wellbore`), described by the newest saved template of that type. Steps are grouped as a build groups its
  searches, one entity type at one depth, so a wellbore read for its name, UUID, field and country is one set of records.
  Each path is read segment by segment through the template (`SchemaPathReader`): the forms of a `oneOf` or `anyOf` are
  looked into (a wellbore's `GeoContexts` holds five kinds of context, and only the field's declares `FieldID`), and a
  filter's property is looked up in the objects it compares. A type with no saved template is drawn and named, its paths
  read as written, unchecked; a step whose type no template names is named by the read before it.
- **What is wrong is said, not refused.** A key the template does not declare a reference while labels or attributes are
  read through it, a step before the last that names no record, a last step holding an object, a path the template does
  not declare: a build reads a label's records as they are, so none of these stops it, and the page marks each.
- **The table's columns**, in the table's order, each with the reads it is written from, and for each attribute how many
  of the keys a build finds now hold a value read for it, and how many values those are
  (`ILedger.DimensionAttributeCoverageAsync`, from the index on attribute and value).

The page draws it left to right: the records searched, each step of records found by id, then the table; a dashed line
runs from a read to the records its ids name, a solid one from a read to the column written from it, and a line that
passes a lane runs between its cards. Pointing at a line of the YAML, a read or a column lights everything that makes the
same column, in the diagram and in the YAML; a click explains it in the panel beside the YAML, with what the template says
of each segment of its path. Once built, an example key (of those with the most records) fills each part with what it
read: the key, its label and the record it came from, each attribute's value and the record it was read from, the values
it collects, and its row.

The explorer's dimension builder ([reference/concepts/explorer.md](reference/concepts/explorer.md#building-a-dimension),
Building a dimension) works the other way round: a person
browses the records OSDU holds and picks the key, the value and the attributes on the values themselves, following links
as a label's steps do, and the builder writes the item, reads it back with this loader, describes it with this blueprint
before any build, and makes an example key's row with a build's own labelling, cleaning and counting. The keys it
suggests are read from the saved template this blueprint describes the kind by. Nothing it writes reaches the catalog
until the YAML is put in a flow.

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
  the API, `cli:<user>@<machine>` from a workstation) and, as it ends, what went or why it failed.

## Views

A view puts dimensions of one flow side by side at the grain of one of them, so a pipeline, a report or a person reads
one table instead of writing the joins: each curve with its log, the log's sampling domain and the curve's unit. A flow
declares its views by name, with their joins and their columns, each column an expression converted to a data type. A
build writes each as `osdu.v_dim_<view>` and keeps it in step with the tables it reads.

```yaml
# The flow's dimensions, abridged (the reference's Elements example, with a BaseDepth field):
#   LogCurve               keyed by the log's id, a row a curve: WellLogID, WellLogName, element, Mnemonic,
#                          CurveUnitID (keep: id), TopDepth, BaseDepth
#   WellLog                keyed by id: WellLogID, WellLogName, SamplingDomainTypeID (keep: id), CreationDateTime
#   RefSamplingDomainType  keyed by id, valued by data.Name
#   RefUnitOfMeasure       keyed by id, valued by data.Name
target:
  connection: ${env:WELLDB_OSDU_DB}       # the module's database, named as the pipelines reading the views name it
views:
  - name: Curve                           # osdu.v_dim_Curve
    description: Every curve of every well log, with its log, the log's sampling domain and the curve's unit.
    from: LogCurve                        # the grain: a row of the view for each row of dim_LogCurve
    join:
      - { on: WellLogID, to: WellLog }                                              # the same key: the log's row
      - { on: WellLog.SamplingDomainTypeID, to: RefSamplingDomainType, as: Domain }  # through the log
      - { on: CurveUnitID, to: RefUnitOfMeasure, as: Unit }
    columns:
      WellLogID: WellLogID                # an expression alone: here a column, kept as text
      WellLog: WellLog.WellLogName
      Curve: Mnemonic
      Unit: Unit.Name
      Domain: Domain.Name
      TopDepth: { expression: TopDepth, dataType: float }
      BaseDepth: { expression: "NULLIF(TRY_CAST(BaseDepth AS float), -999.25)", dataType: float }
      Interval: { expression: "TRY_CAST(BaseDepth AS float) - TRY_CAST(TopDepth AS float)", dataType: "decimal(18,3)" }
      Created:
        expression: WellLog.CreationDateTime
        dataType: datetime2(3)
        description: When the log was made, in UTC.
```

A build writes it as (abridged to the first join and two columns):

```sql
CREATE OR ALTER VIEW [osdu].[v_dim_Curve] (
    [partition], [id], [WellLogID], [WellLog], ..., [Created]) AS
SELECT b.[partition], b.[id], b.[WellLogID], j1.[WellLogName], ...,
       CAST(SWITCHOFFSET(TRY_CAST(j1.[CreationDateTime] AS datetimeoffset(7)), '+00:00') AS datetime2(3))
FROM [osdu].[dim_LogCurve] AS b
LEFT JOIN [osdu].[dim_WellLog] AS j1
       ON j1.[partition] = b.[partition]
      AND j1.[key_hash] = CAST(HASHBYTES('SHA2_256', b.[WellLogID]) AS binary(32))
      AND j1.[WellLogID] COLLATE Latin1_General_100_BIN2 = b.[WellLogID] COLLATE Latin1_General_100_BIN2
...
```

| Key | Default | Meaning |
| --- | --- | --- |
| `target.connection` | none | The module's database, written as a delivery flow's `source.connection` is: a `${env:...}` or `${keyvault:...}` reference, or a SQL Server connection string whose password is one. Required when the flow declares views. |
| `views[].name` | required | The view's name: a letter, then letters, digits and underscores, at most 64; unique among the views of a database, ignoring case. The view is `osdu.v_dim_<name>`. |
| `views[].description` | none | What the view holds, shown with it. |
| `views[].from` | required | The dimension whose rows the view's rows are. |
| `views[].join[]` | none | `{ on, to, as }`: the column joined on, the dimension joined to, and the alias its columns are read by (the dimension's name unless given). At most 32. |
| `views[].columns` | every column, as text | Each column under its name, in order: an expression, or `{ expression, dataType, description }`. At most 256. |
| `views[].where` | every row | The condition a row of `from` is kept by, over the same columns, operators and functions as a column's expression (`element IS NOT NULL`). |

### What a view holds

- Every view begins with `partition` and `id`: the partition, and the number of the `from` row the view's row stands for.
  `id` is the view's key, the same for as long as the dimension holds the row, so a pipeline merges on it.
- Then its `columns`, in the order written. A view that lists none has every column of `from` but `id`, `partition`,
  `key_id` and `filter`, under the names the table gives them, then every column of each join the same way, prefixed by
  the join's alias and an underscore (`Unit_Name`), with the joined row's number as `<alias>_id`, all as text. The prefix
  is always given, so a dimension that gains an attribute adds a column and never renames or collides with another.
- A row of `from` is a row of the view, unless the view's `where` leaves it out: every join is a left join, and no join
  meets more than one row. `where` is a condition compiled as an expression is, written into the view's `WHERE` clause
  and into every statement its check reads it by, so the check counts the rows the view holds. A dimension with
  `elements` keeps one row with no element for a key whose records hold none; `where: element IS NOT NULL` leaves it
  out, as a table of the elements alone has it.
- A column's name follows the rules of a view's name, unlike every other column of the view ignoring case, and is neither
  `partition` nor `id`.

### Joins

A join names the column it joins `on` (a column of `from`, bare, or of an earlier join, `alias.column`), the dimension it
joins `to`, and its alias. Joins are read in order, so one can follow another and none can come back on itself; a
dimension can be joined twice under two aliases. Joins stay inside one flow, so one run settles every table a view reads.
Refused where the document is read, naming the view and the join:

- `to` not a dimension of the flow, or an alias used twice.
- `on` not a column of the table it names, or a column kept as `value`: shown as a value, it matches no key.
- Keys that would not meet. A key's column or a column kept as `key` joins a dimension keyed by the same path; a column
  kept as `id` joins a dimension keyed by `id`; two dimensions keyed by `id` have to read kinds of one entity type.
- A dimension holding more than one row a key: one with `elements` or a collected attribute. Joined, it would repeat the
  row of `from`; the refusal says to view it as `from`, or to join a dimension of the same key without them.

Every join compares the partition and the key exactly. `key_hash` finds the row through an index, and the text is
compared under `Latin1_General_100_BIN2`, since the database's own collation may equate ids that differ only in case: on
a `Latin1_General_CI_AS` database, `...:Wellbore:A:` equals `...:Wellbore:a:`, and a join on the text alone would give
a row both, or the wrong one (the local SQL Server, 2026-10-09).

A join is declared, never inferred, so a view is its document's alone. The saved templates check it
(`DimensionViewTemplates`): the column joined on is read through the newest saved template of the records it is read
from (`SchemaPathReader`, as the blueprint reads a label's path), and a join to a dimension of the entity type its
`x-osdu-relationship` (or id pattern) names agrees, one to another type differs, and one no template describes is
unchecked; a view's page shows each. A join of the wrong type finds nothing rather than a wrong row, since an id carries
its entity type, and the check counts what each join found. The same reading offers the joins a view of a dimension could
make (`GET /flows/{pipelineId}/dimensions/views/suggest`, `sqlflow dimensions views --suggest`, the flow's Views on
the GUI): each column holding a key, joined to the dimension keyed by what it holds, one row a key, of the type the
template names, and through each dimension so joined, as a `join:` block for a person to keep or change. A column no
template describes is offered nothing, since a record's id can name a record of any type.

### Expressions

A column's `expression` is a T-SQL scalar expression. SQL Server's own parser reads it (ScriptDom, which SQLFlow's lineage
already uses), and the module writes the view from what the parser read, never from the text as written. It can use:

- **Columns**: bare for `from`'s, `alias.column` for a join's, named as the document names them: the key's and the
  value's columns, an attribute, an element field, `element`, `records` and `id`.
- **Literals**: numbers, `'text'`, `N'text'` and `NULL`.
- **Operators**: `+ - * / %`, comparisons, `AND`, `OR`, `NOT`, `IS [NOT] NULL`, `[NOT] LIKE`, `[NOT] IN` a list of
  values, `[NOT] BETWEEN`, `CASE`, and parentheses.
- **Functions**: `COALESCE`, `NULLIF`, `ISNULL`, `IIF`; `TRY_CAST`, `TRY_CONVERT`, `CAST` and `CONVERT` without a style;
  `LEFT`, `RIGHT`, `SUBSTRING`, `LEN`, `UPPER`, `LOWER`, `TRIM`, `LTRIM`, `RTRIM`, `REPLACE`, `CHARINDEX`, `CONCAT`,
  `CONCAT_WS`; `ABS`, `ROUND`, `FLOOR`, `CEILING`, `POWER`, `SQRT`, `EXP`, `LOG`, `LOG10`, `SIGN`; `DATEADD`, `DATEDIFF`,
  `DATEPART`, `YEAR`, `MONTH`, `DAY`, `EOMONTH`.

Anything else is refused where the document is read, naming the column and what was found there: a subquery, a table, a
variable, a function not listed, a window (`OVER`), `COLLATE`, a value that moves with the clock (`GETDATE()`). So a
view reads the dimensions it joins and nothing else in the database, and gives the same rows for the same tables. An
expression is at most 4,000 characters.

An expression is typed as it is read: every column of a dimension's table is text but `id`, `key_id`, `records` and
`element`. Where SQL Server would convert implicitly, it is refused, since such a conversion fails the whole read at the
first value it cannot convert: `BaseDepth - TopDepth` is refused, saying to convert first
(`TRY_CAST(BaseDepth AS float) - TRY_CAST(TopDepth AS float)`), and so is text compared with a number. Text compared with
text follows the database's collation, as any query of the tables does.

What could fail a read is written so that it cannot:

- `/` and `%` divide by `NULLIF(<divisor>, 0)`: a division by zero is null.
- `SQRT`, `LOG` and `LOG10` outside their domain, `POWER` of zero to a negative or of a negative to a fraction, and `LEFT`,
  `RIGHT` and `SUBSTRING` of a negative length, are null. The guard nulls the argument, not the call: SQL Server folds a
  call of constants when it compiles the statement and raises there, whatever a `CASE` around it says (the local server
  raised `Invalid length parameter passed to the left function` for `CASE WHEN -1 < 0 THEN NULL ELSE LEFT(N'abc', -1) END`).
- Every conversion, whichever of the four forms is written, is the module's (Data types, below).

What is left, an arithmetic overflow or a `DATEADD` past the year 9999, the check finds (The check, below).

### Data types

`dataType` converts the column's value to a type as SQLFlow's schema evolution names it (`SqlDataType`): `bit`,
`tinyint`, `smallint`, `int`, `bigint`, `decimal(p,s)` and `numeric(p,s)`, `float`, `real`, `date`, `time(n)`,
`datetime2(n)`, `datetimeoffset(n)`, `uniqueidentifier`, and `nvarchar(n)` or `nvarchar(max)`. Left out, the column
keeps its expression's type: text for a column of a table. Any other type is refused where the document is read:
`varchar` and `char` would turn characters outside their code page into `?` without a word, `datetime` and
`smalldatetime` round and stop at 1753 where `datetime2` does neither, and no other type is one a dimension's text
converts to.

A conversion never fails a read and never changes a value without saying so: a value it cannot convert is null, and the
check counts it. Each is written for the text dimensions hold, as the local SQL Server answered on 2026-10-09:

| To | Reads | Where plain `TRY_CAST` differs |
| --- | --- | --- |
| `float`, `real` | `203.149`, `-999.25`, `1.5E3`; not `12,5` or `NaN` | It does not. |
| `decimal(p,s)`, `numeric(p,s)` | The same, rounded to `s` places: `203.149` as `decimal(18,2)` is `203.15` | `TRY_CAST` reads no exponent (`1.5E3` is null); a value it cannot read is read through `float`. |
| `tinyint` to `bigint` | A whole number in any of those forms: `7`, `7.0`, `7E0` | `TRY_CAST` reads `7.0` as null. A fraction (`7.5`) is null, never cut to `7`. |
| `bit` | `true`, `false`, `1` and `0`, ignoring case | It does not (`yes` is null). |
| `datetime2(n)` | ISO 8601 (`2013-03-22T11:16:03.123Z`, `...+02:00`), as the instant in UTC; no offset is UTC | `TRY_CAST` drops an offset without applying it (`11:16:03+02:00` is `11:16:03`); the text is read as `datetimeoffset` and moved to UTC. |
| `datetimeoffset(n)` | The same, keeping the offset | It does not. |
| `date`, `time(n)` | The date or the time of day as written, its offset aside | Read through `datetimeoffset`, so a text with an offset converts. |
| `nvarchar(n)` | A text of at most `n` characters | A cast cuts a longer text without a word; here it is null and counted. |
| `uniqueidentifier` | A GUID in its usual text forms | It does not. |

### The check

After writing its views, a build reads each once, the rows of the run's partition: how many rows it holds, for each join
how many rows found theirs, and for each converted column how many values did not convert, with three examples each
(the row's `id` and the text). They are kept with the build (`DimensionViewCheck`), shown with the view and counted in
the run's notes: `Unit: 1,204 of 88,310 rows name no row of RefUnitOfMeasure`, `TopDepth: 37 values are no float, for
example '12,5'`. A join that finds no row at all is a warning in the notes.

A read that fails (an overflow no expression could be written around) fails the build, naming the view, the column (read
alone to find it) and SQL Server's message, so the pipelines ordered after the flow do not run against a view that cannot
be read. The view keeps its definition: what fails is the data, and the next build that reads it whole passes.

### Writing a view

- **One writer.** A build run writes the flow's views at its end, after its dimensions, whichever dimensions it built and
  even when one failed (its table holds its last build). Each view is written in one transaction under an application
  lock of its own: every table it reads is made, empty, if no build has made it yet; rows without a `key_hash` are given
  theirs (in every partition when the view is written anew, else in the run's, which the index on the hash finds in one
  seek); the view is written with `CREATE OR ALTER VIEW` and the list of its columns, never `*`; and its row in
  `DimensionView` is written. A view whose declaration and tables' columns are as they were is not written again (its
  hash says so); the check still runs. A view that cannot be written keeps its last definition, the run fails naming it,
  and the next run writes it.
- **Only what it made.** `DimensionView` records each view a flow made: its name, the flow, the declaration as last
  written and its hash, the tables it reads, its columns with their types, the SQL, and the run that wrote it. A build
  writes a view it recorded, or a name no object of the schema holds; a name another flow's view holds, or an object the
  module did not make, is refused, naming it. A view its flow no longer declares is dropped by the flow's next build.
- **Never read wrongly.** A build that renames a key's or a value's column (The table) drops, in the transaction that
  renames, the views a build made that read that table, taking their locks before it renames, so a build writing a view
  and one renaming a column it reads never wait on each other; their records say why, and the run's view step writes them
  again from the document. A view is only written at the run's end, where every table it reads is settled: written in
  the rename's transaction, it would name the columns of tables other dimensions of the run have not renamed yet. Left in
  place, a view naming the old column would fail, or, where two columns swap names, read the other one. Between the
  rename and the run's end the view is not there, which a read is told plainly. A column added changes no view, since
  every view lists its columns. A dimension's removal is refused while a recorded view reads its table, naming the view,
  until the flow's next build has written the view without it.
- **`key_hash`.** Every dimension table gains `key_hash binary(32)`, SHA-256 of the key's text as SQL Server's
  `HASHBYTES` gives it, written with each row by the statements that write the rows and filled where it is missing, and
  the index `IX_key_hash` (`partition`, `key_hash`). It is a column the build writes and not a computed one: the local
  SQL Server refuses to rename a column a computed column reads (`participates in enforced dependencies`), and an index on
  a computed column refuses every write from a session without `QUOTED_IDENTIFIER`.
- **`plan`** compiles each view, says what would stop it and gives the SQL it would write, and writes nothing.

### Lineage and the module's database

With `target.connection`, the flow declares that it writes each dimension's table and each view (`DeclaredDataObject`
in the schema `osdu`, a view as `LineageNodeKind.View`), so a pipeline reading either is ordered after the flow. SQLFlow
identifies a server by its reference as written, so the flow and the pipeline name the database alike. The module's own
connection cannot stand in for it: it is `Osdu:Database:Connection`, `SQLFLOW_OSDU_DB` or the catalog's depending on the
host, and would match neither another host nor the pipeline.

Build and plan resolve the reference and ask the server, on it and on the module's connection, for the server's name,
the database's name and when the database was created, and refuse when the two differ, naming the reference and never
what it resolves to: `target.connection ${env:WELLDB_OSDU_DB} reaches another database than this host's module
database, so the lineage it declares would name tables the build does not write. Point it at the module's database.`

### Reading views

`GET /dimensions/views`, `GET /flows/{pipelineId}/dimensions/views` and `GET /dimensions/views/{name}` (the declaration,
the columns with their types and expressions, the SQL as written and as declared, the checks, the YAML),
`sqlflow dimensions views <flow.yaml> [--view <name>]`, and the GUI: a dimension flow's
Dimensions tab lists its views, and a view's page shows its columns, its joins with what each found, its conversions with
their counts and examples, and its SQL. A view whose flow is gone is removed by an admin as a dimension is
(`DELETE /dimensions/views/{name}`, `sqlflow dimensions remove-view <flow.yaml> --view <name>`), recorded as a
`remove-view` activity of the ledger of the build that last wrote it.

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
| `Dimension` | flow, partition and dimension name | The declaration as last built: kind, query, path, the label's paths, the attributes, the field asked, the field each collected attribute is read from, the clean steps, the definition hash, current value and key counts, the last build, the name of its own table, and what that table names the two columns holding its key and its value. |
| `DimensionRun` | dimension and build | Status, counts (records, values, keys, added, removed, moved, left out, unfilterable, too long, null, labelled, unlabelled), requests (label searches among them), slices, scanned records, templates used, notes. |
| `DimensionMember` | value | Stable id, the value, count and whether it is exact, keys, filter and parts, first and last seen, removed. |
| `DimensionValue` | key | Stable id, the key, its label and the record it came from, its value or why it has none, count, filterable, its filter, first and last seen, removed, the build it joined its value. |
| `DimensionAttributeName` | attribute of a dimension | The attribute's number, its name as declared, whether it is collected, and its place among the attributes the dimension declares now (none once it is no longer declared). |
| `DimensionAttribute` | key, attribute and value | The attribute by its number, the value, where it was read (the record, or a collected attribute's text), and for a collected value how many of the key's records hold it. |
| `DimensionCollectedText` | collected attribute and text | The attribute by its number, the text exactly as the index holds it, the value it is shown as, and the records holding it. |
| `DimensionChange` | key that moved, left, came back or arrived after the first build | The build, the key, from and to value. |
| `dim_<dimension>` | key and value it collects | The dimension as one table (The table, above): made and widened by builds, not by migrations. |
| `DimensionView` | view | The flow, the declaration as last written and its hash, the tables it reads, its columns with their types, the SQL, and the run that wrote it. Keyed by an identity and unique by name: a view spans every partition, so it belongs to no ledger partition. |
| `DimensionViewCheck` | view, partition and build | The rows, what each join found, and the values each conversion could not read, with examples. |
| `v_dim_<view>` | row of the view's `from` dimension | A view over the dimension tables (Views, above): written by builds, not by migrations. |

A build writes in one transaction: its values are copied into temporary tables, and set-based statements add, update and
mark removed, give each attribute its number, and bring the dimension's own table to what was kept. The attribute values
read are staged under their key's and their attribute's numbers and matched to the rows kept on those numbers and the
value, one row to one row: matched by the value alone, every key sharing a value (a country) would meet every other.
A build's duration counts its write. The write has no time limit, as SQLFlow's loads have none: it grows with the
dimension (one of well log curves stages a row for every field of every curve, tens of millions), and a fixed limit
fails a build that was only large, so it is bounded by its run, whose cancellation stops it with nothing written. An
application lock per dimension keeps two builds of it from interleaving, and bounds the wait for another build's write.
Only the table's schema is settled before the transaction: a
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
| Two texts of a collected path shown alike (`WELLDB` and `WELLDB `) | One value, standing for both; a search picking it asks for both. |
| A collected value a pick names that the attribute does not hold | Named as missing; a pick holding no value it does hold is refused. |
| Dimensions of different kinds picked in one search | Refused unless a kind is named that each dimension's kind covers. |
| A value that disappears and comes back | Keeps its id; the change log says when. |
| A dimension named after the property it reads, with no label (`Source` reading `data.Source`) | Its key's and its value's columns would both be `Source`: the value's keeps the name and the key's is `SourceKey`. |
| A path ending in a column every table has (`id`), or in an attribute's name; a key's column the document gives the value's name | Refused where the document is read, naming the column and how to name it otherwise. |
| A label path changed (`data.FacilityName` to `data.WellboreName`) | The value's column is renamed where it is by the next build; a query naming the old column is changed with the flow, or the column is given a name that stays (`columns: { value: Wellbore }`). |
| A table made before dimensions named their columns | Holds `key` and `value`, is read under those names, and has them renamed by its next build, every row keeping its `id`. |
| A name another column of the table already holds | The build fails before it renames anything, saying which column to drop or how to name the dimension's otherwise. |
| A build renames a column while a page of the table is being read | The read is asked again and finds the column under its name; an export that had begun says the table changed. |
| Two ids that differ only in case, on a case-insensitive database | Never joined to each other: a join compares the text under `Latin1_General_100_BIN2`. |
| A join to a dimension of more than one row a key | Refused where the document is read, naming the dimension and why. |
| A table a view reads that no build has made | Made empty by the view's write; its join finds nothing until the dimension is built. |
| A value a conversion cannot read (`12,5` as `float`, `7.5` as `int`, a text longer than `nvarchar(n)`) | Null; the check counts it, with examples. |
| An expression that fails on a value the check reads (an overflow) | The build fails, naming the view, the column and SQL Server's message; the view keeps its definition. |
| A view its flow no longer declares | Dropped by the flow's next build. |
| A view whose flow is gone | Kept, listed, and removed by an admin as a dimension is. |
| A view name another flow's view holds, or an object of the schema the module did not make | Refused, naming it; nothing is written. |
| A key's or a value's column renamed under a view | The views reading it are dropped in the transaction that renames, and the run's view step writes them again. |
| Removing a dimension a view reads | Refused, naming the view, until the flow's next build has written the view without it. |
| `target.connection` reaching another database than the module's | Build and plan are refused, naming the reference and never what it resolves to. |
| An expression using what is not listed (a subquery, `GETDATE()`, a window) or converting implicitly | Refused where the document is read, naming the column and what was found. |

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

The dimension flow reference (`reference/flow/dimension.md`), `design.md` section 15, the ledger
(`reference/concepts/ledger.md`), the GUI and the API (`reference/concepts/gui.md`, `reference/concepts/api.md`), the
CLI reference (`reference/cli/dimensions.md`), the decision record, the integration brief's aggregation facts, and the
samples README.

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
  a table under an earlier name dropped by the next build; a dropped table, and one never made, made on reading; a
  key no longer found leaving; removal dropping the table; a dimension another flow declares by the same name
  refused; the names; the document refusing two dimensions of one table name and an
  attribute named after a column; the API's table, its page, its refusals and its export; the migration up, down and up
  again with attribute rows kept.

### Stage 13: the table's columns named after what the dimension reads

- `DimensionColumnNames` (the names a path, a label and a dimension give, the key's with `Key` at its end where the
  two would be the same, and what cannot name a column); the
  document's `columns: { key, value }`; `DimensionSpec.KeyColumn` and `ValueColumn`, left out of the definition hash
  where the document does not give them, so a dimension built before keeps its hash.
- `Dimension.KeyColumn` and `ValueColumn` (migration `DimensionColumnNames`, module version 1.23.0, which records `key`
  and `value` for every table a build had made, and going down renames the columns back); the store settling the two
  columns before the schema (`SettleColumnsAsync`: renamed where they are, recorded with the rename, under a lock of the
  table); the table read, ordered and searched by its own names, the words `value` and `key` asking for the two
  whatever they are named.
- The API's `keyColumn` and `valueColumn` on a dimension and on a page of its table; the table export, the CLI's table
  and the GUI's grids under the names; the census.
- Tests: the names a document gives and the ones its paths give, a dimension named after the property it reads taking
  `Key` for its key's column, each refusal saying how to name the column, and the hash kept; a table built under its names; one holding `key` and `value` renamed by its next build with every row's
  number kept and the index following; a rename by the document, one in case alone and two columns swapping names; a
  name another column holds refused with nothing renamed; a reader making a dropped table under the names it had and a
  first table under the names the dimension reads by; a page ordered by a column's name and by the two words; the
  API's names and export; the migration up, down with a renamed table, and up again.

### Stage 14: the blueprint

- `DimensionYamlSource` (the dimension's YAML, and where each thing it declares is written), `SchemaPathReader` (a path
  through a template, the forms of a choice and a segment's filter included), `DimensionBlueprints` and `KindPatterns`;
  `ILedger.DimensionAttributeCoverageAsync`; `GET /flows/{pipelineId}/dimensions/{name}/blueprint`.
- The **Definition** tab drawn as the blueprint: the diagram, the YAML with every line linked to it, the panel explaining
  what is picked, and an example key; shown under the notice of a dimension no build has read yet, too.
- Tests: the YAML's spans (a folded description, a list on one line, a map in braces, the next item kept out); a path
  through a `oneOf` and a filter; the WellDB Wellbore dimension laid out through the logs, the wellbore, a field with no
  template saved and a country with one; the kinds a build read; no template saved; a key no template calls a reference
  and a path the index cannot read; kind patterns; the API's blueprint with its YAML, columns and coverage, a dimension
  the flow no longer declares, and the refusals (SQL Server).

### Stage 15: views

- The document: `target.connection` and `views` (`DimensionViewSpec`: the name, the description, `from`, the joins, and
  the columns with their expressions, data types and descriptions); every rule and refusal of Views; the YAML source
  spans of each view, join and column; the key census.
- `DimensionViewExpressions`: an expression read by ScriptDom's parser, checked against the listed operators, functions
  and columns, typed, refused where SQL Server would convert implicitly, and written back with the guards;
  `DimensionConversions`: each data type's conversion, as the table in Data types gives it.
- `DimensionTables`: `key_hash` (written with every row, filled where missing) and `IX_key_hash`; `DimensionViews`: a
  view's SQL from its declaration and the names its tables give their columns.
- The store: a view's write at the end of a build run (its lock, the tables made, the hashes filled,
  `CREATE OR ALTER VIEW`, its record); a rename dropping the views that read the table; the check with its
  examples; a view no longer declared dropped; `DimensionRemoval` refusing a dimension a view reads.
- Entities `DimensionView` and `DimensionViewCheck`, and the migration `DimensionViews` with its designer and snapshot
  (module version 1.36.0).
- `DimensionLineage`: the tables and the views written on `target.connection`; build and plan refusing a reference that
  reaches another database than the module's; `plan` giving each view's SQL and what stops it.
- `DimensionViewTemplates`: each view's joins with what the templates say of each `on` (`joinChecks` on a view's
  page), and the joins a view could make (`.../views/suggest`, `--suggest`, the GUI's Suggest joins).
- `GET /dimensions/views`, `GET /flows/{pipelineId}/dimensions/views`, `GET /dimensions/views/{name}`,
  `DELETE /dimensions/views/{name}` (admin); `sqlflow
  dimensions views` and `remove-view`; the GUI's views on a dimension flow's Dimensions tab and a view's page.
- Documentation: `reference/flow/dimension.md` (Views), `design.md` section 15, the ledger, API and CLI references, and
  `CLAUDE.md`, whose one exception names the views a dimension flow declares beside a dimension's own table.
- Tests:
  - The document: each refusal naming its place (a dimension the flow does not declare, a column kept as `value`, keys
    that would not meet, a join repeating rows, an alias or a column twice, a construct or function not listed, a
    subquery, an implicit conversion, a type not listed, `varchar`, a style), and views without `target.connection`.
  - Expressions: the SQL written for each operator and function, the guards, and every conversion of the Data types
    table run on SQL Server.
  - SQL Server: a view over three dimensions with a join through another and a dimension joined twice; ids differing
    only in case never joined; a table never built made empty and joined to nothing; a build of the same thing not
    writing the view again; a key's column renamed with the view dropped by the rename and written again; a view no longer
    declared dropped; a name another flow's view holds, and an object the module did not make, refused; a dimension's
    removal refused while a view reads it; the check's counts and examples; an overflow failing the build naming the
    view and the column; `key_hash` filled in a table built before it; the migration up, down and up again.
  - The flow kind's documents: a view laid out from its document alone, and the same document laying it out the same way.
  - Lineage: a pipeline reading a view ordered after the dimension flow; a `target.connection` reaching another
    database refused by build and plan.
  - The API's views and their refusals; the GUI's build and lint.

### Stage 16: a view's rows, and an element's long values

- `views[].where`: a condition compiled as a column's expression is (`DimensionViewExpressions.CompileCondition`),
  refused when it is a value alone or reads what a column may not; written into the view's `WHERE` clause and into each
  statement of its check, kept on the view's record and shown by the API and the GUI. `element IS NOT NULL` leaves out the
  one row a key of no element holds, so a view of elements has the rows a table of the elements alone has.
- An element's field keeps up to 4,000 characters (`DimensionSpec.MaxElementValueLength`), an attribute still 256: no
  index holds a field, while an attribute's value leads two. `DimensionElement.Value` is `nvarchar(4000)` (migration
  `DimensionElementValues`, module version 1.37.0); the staging tables, a dimension table's field columns and a view's
  type of them follow, and a table made before is widened by its next build through SQLFlow's schema evolution.
- A view is `osdu.v_dim_<name>`, read beside the `dim_` tables it joins; a view a build wrote under the former `dimv_`
  is dropped and written under its name now by the flow's next build, in one transaction, and its record follows.
- Tests: the condition's SQL in the view and in its check's statements, and its refusals; a view leaving out the row of
  a key with no element on SQL Server, its check counting the rows it holds; a field of a thousand characters kept whole
  in the ledger, the table and the view, after a table made at 256 is widened; the reader cutting at 4,000; the migration
  keeping the values written before.

### Stage 17: full and incremental loads

- `incremental` in the document (`DimensionIncremental`): `keyColumns` required, `dateColumns`, `lagMinutes`,
  `fullLoadAfterHours`, `fullLoad`; the key census and the reference with them. `RecordChanges.Within` writes a record's
  change time as the first of several dates it holds, which a retrieval's `modifyTime` window reads the same way.
- A build plans each dimension's load (`DimensionRunner.PlanLoadAsync`): in full without the block, when asked, when the
  dimension has no full load that kept its records by the flow's unique key, when its declaration or query changed, when
  its last full load is older than `fullLoadAfterHours`; incrementally otherwise, from the lag before up to when its
  completed builds read (`DimensionRun.WindowTo`), or over the run's backfill window.
- A full load of an incremental flow reads every record once more (`DimensionRecordReader`) and keeps the keys each
  holds by its unique key (`DimensionRecord`); every build keeps the records each key's label and attributes were read
  through (`DimensionKeyRecord`, from `KeyLabels.Records`).
- An incremental load (`DimensionRunner.Incremental.cs`) reads the records that changed and the keys they hold, looks up
  what they held before, finds the keys read through a record that changed, reads every such key again whole a query's
  worth at a time, and writes partially (`DimensionWrite.Partial`, `Removed`, `Records`): a key no record holds is
  removed, a value left with no key is removed, and nothing else changes. More than a quarter of the records or keys, or
  more than 100,000 changed records of a type labels are read through, loads in full instead.
- Each build records how it loaded, its window, the records it found changed, the keys it read again and the key it kept
  records by (migration `DimensionIncrementalLoads`, module version 1.38.0); the dimension its last full load. The API,
  the CLI's history and a plan say so.
- Tests: the document and its refusals; the run parameters a build takes; the migration keeping earlier builds as full
  loads; on SQL Server, a first full load and an incremental one reading again only the keys of what changed, a key moved
  away removed, a label and an attribute read again for a record they were read through, a deleted record kept until a
  full load a run asks for or the flow's age says, a backfill window that moves nothing back, a window of most records
  loading in full, and a flow that declares the block after loading in full loading in full once more.

## Close-out

A clean rebuild with zero warnings, the GUI build and lint, every suite with SQL Server, and
`tools/check-vendored-sqlflow.sh` passing. No live OSDU run is part of this plan; a live check of the range queries and the
numeric keys is listed and approved first, under the project's rules.
