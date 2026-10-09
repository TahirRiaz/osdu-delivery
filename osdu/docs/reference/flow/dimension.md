---
id: delivery-flow-dimension
title: "Dimension flow (flowType: dimension): every distinct value of an OSDU property, labelled and cleaned for filtering search"
type: flow-reference
summary: "The flowType: dimension document: every distinct value of an OSDU property, labelled and cleaned for search, its tables joined into views with typed columns."
keywords:
  - dimension flow
  - distinct values
  - facet values
  - drop-down filter
  - label
  - attributes
  - collect
  - clean steps
  - unlabelled
  - aggregationsize
  - maxvalues
  - "osdu.dim_"
  - search filter
  - views
  - join dimension tables
  - typed columns
  - "osdu.dimv_"
  - target.connection
  - datatype
  - key_hash
yamlPath: "(root, flowType: dimension)"
related:
  - delivery-guide-dimensions
  - delivery-cli-dimensions
  - delivery-flow-cache
  - delivery-flow-dictionary
  - delivery-concept-templates
  - delivery-concept-partitions
  - concept-schema-evolution
  - flow-schedule
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DimensionFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionColumnNames.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionPath.cs
  - osdu/src/SqlFlow.Delivery/Engine/DimensionExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionCleaner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionLabeler.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionCollector.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DistinctValues.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionFilters.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionRemoval.cs
  - osdu/src/SqlFlow.Delivery/Rendering/SearchFields.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/ReferenceSnapshot.cs
  - osdu/src/SqlFlow.Delivery/Ledger/DimensionTables.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Dimensions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionStore.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionViewMapper.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionViews.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionViewExpressions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionViewRemoval.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerDimensionViewStore.cs
  - osdu/docs/census/keys.dimension.json
  - osdu/specs/core/INTEGRATION.md
---

# Dimension flow (flowType: dimension): every distinct value of an OSDU property, labelled and cleaned for filtering search

A dimension flow reads every distinct value one property of an OSDU kind holds in a partition (a curve mnemonic, a
wellbore id, a legal tag), turns each into the value a person picks from a list, and keeps both in the module's
database with the search filter that finds the records holding it. Use it when an application or a person needs a
complete pick list over what OSDU holds, wants spellings grouped, or wants to filter records by a fact of another record
(the wellbore's name, its country) that a search alone cannot join. OSDU's own aggregation returns at most 1,000
distinct values by default and pages none of them; a dimension build reads them all.

A dimension flow only reads OSDU. Nothing it does is written to OSDU, and nothing it keeps changes what a delivery
sends. Building is a run like any other flow's run; reading what it built is the [`sqlflow dimensions`](../cli/dimensions.md)
verb, the GUI's **Dimensions** page, or the dimension's own table in SQL. The [dimensions guide](../guides/dimensions.md)
walks through one end to end.

## Keys, values and attributes

A dimension keeps three things apart:

- A **key** is a value exactly as the search index holds it: the text a query compares. For a reference such as
  `data.WellboreID`, the key is the record id (`dev:master-data--Wellbore:WB-0001:`), never a name.
- A **value** is what a person picks. With a `label`, it is read from the record the key names (the wellbore's
  `data.FacilityName`) and cleaned; without one, it is the key itself, cleaned. A key that names an OSDU record and has
  no label is valued by the code its id ends with, its escapes decoded (`dev:reference-data--UnitOfMeasure:us%2Fft:` is
  `us/ft`). Keys whose values come out the same are one value: with an `upper` step, `GR`, `gr` and `GR ` become one `GR`.
- An **attribute** is a further fact of each key, read the way a label is (a wellbore's country or operator), or
  collected from the dimension's own records (the logging services of a wellbore's well logs). Values and keys are
  looked up by attribute, and a search picks keys by them.

Every key carries the filter that finds exactly its records, and every value the filter that finds the records holding
any of its keys. Values picked across several dimensions compose one search.

## Example

A dimension flow over the well logs the welldb flows deliver, built in the `dev` and `test` partitions,
`flows/welldb-welllog-05-dimensions.yaml`:

```yaml
flowType: dimension
name: welldb-welllog-05-dimensions
description: The wellbores, curve mnemonics and units of the well logs the welldb flows deliver, ready for a search panel.
batch: welldb
partitions: [dev, test]

parameters:
  deliveredBy: { default: welldb }

source:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body: { scope: "${env:OSDU_SCOPE}" }

reliability: { concurrency: 4 }

dimensions:
  - name: Wellbore
    description: Every wellbore the well logs name, by its name, with its country and operator.
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"{deliveredBy}"'
    path: data.WellboreID
    label: data.FacilityName
    unlabelled: Not specified
    attributes:
      Country: [data.GeoContexts.GeoPoliticalEntityID, 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName']
      Operator: [data.CurrentOperatorID, data.OrganisationName]
      LoggingService: { collect: data.LoggingService }

  - name: CurveMnemonic
    description: The curve mnemonics of the well logs, spellings grouped.
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"{deliveredBy}"'
    path: data.Curves.Mnemonic
    clean:
      - trim
      - collapseSpaces
      - upper
      - replace: { pattern: '^(\w+)_\d+$', with: '$1' }
      - map: curve-aliases
    countRecords: true

  - name: CurveUnit
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'tags.DeliveredBy:"{deliveredBy}"'
    path: data.Curves.CurveUnit
    columns: { key: UnitID, value: Unit }

  - name: LegalTag
    kind: "*:*:*:*"
    path: legal.legaltags
    countRecords: true
    partitions: [dev]
```

`Wellbore` keys each well log's wellbore id, values it by the wellbore's name, and keeps each wellbore's country (read
through its geopolitical contexts, keeping the one whose type is a country), its operator's name, and the logging
services its well logs name. `CurveMnemonic` groups spellings through its clean steps and a dictionary
(`dictionaries/curve-aliases.yaml`, a [dictionary document](dictionary.md) of pairs). `CurveUnit` keys unit references
and values each by its decoded code. `LegalTag` reads a property of every record of every kind, in `dev` only.

## Top-level keys

Unknown keys are refused when the document is read (`invalid YAML at line 5, column 3 - Property 'bogus' not found on
type ...`).

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `flowType` | text | required | `dimension`. |
| `name` | text | required | The flow's name: its pipeline identity, and the name its dimensions are kept under. A flow that builds in partitions keeps them per partition, as `name@partition`. |
| `description` | text | none | What the dimensions are for. |
| `batch` | text | none | The grouping label the pipeline is filed under (`welldb`). |
| `parameters` | map | none | Parameters a dimension's `query` uses as `{name}` tokens, each with `default`, `required` and `description`. `partition` cannot be declared: `{partition}` is always the partition the run builds in. |
| `partitions` | list | none | The partitions the flow builds in, each a literal `data-partition-id`. See [Partitions](#partitions). |
| `source` | map | required | The OSDU platform the dimensions are read from. See [source](#source). |
| `dimensions` | list | required | The dimensions, at least 1 and at most 100, names unique ignoring case. |
| `target` | map | none | `connection`: the module's database, as the pipelines reading the flow's tables and views name it. Required with `views`. See [Views](#views). |
| `views` | list | none | Views over the flow's dimension tables, at most 50. See [Views](#views). |
| `reliability` | map | delivery defaults | The HTTP settings a delivery flow takes ([delivery flow](delivery.md)), and `concurrency` (default 8, at least 1): how many dimensions build at once, and how many requests each asks of the search at once (value ranges, label and attribute searches, cursors). `parallelInterfaces` is refused. |
| `schedule`, `mode`, `lifecycle` | | none | SQLFlow's envelope keys ([schedule](../../../../sqlflow/docs/reference/flow/schedule.md), [flow overview](../../../../sqlflow/docs/reference/flow/overview.md)). A schedule's `values` may name the partition (`partition: dev`) and the flow's parameters. |

### source

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `endpoint` | text | required | The platform's base URL, usually `${env:OSDU_URL}`. |
| `auth` | map | `type: none` | How requests authenticate, written as a delivery flow's `target.auth` is. Secrets are `${env:NAME}` or `${keyvault:NAME}` references only; a literal is refused when the flow is read. |
| `headers` | map | none | Headers sent with every request. `data-partition-id` here hard-codes the one partition the flow builds in; a flow that names `partitions` may not set it. A header that carries a credential holds a reference; a literal is refused when the flow is read. |
| `queryPath` | text | `/api/search/v2/query` | The search service's offset search: aggregations, counts, and the searches that read labels by id. |
| `searchPath` | text | `/api/search/v2/query_with_cursor` | The cursor search a build scans records through where an aggregation cannot answer. |
| `aggregationSize` | integer | 1000 | How many groups the platform's aggregation returns (its `AGGREGATION_SIZE`), 10 to 10,000. A range answered with fewer groups is taken as complete, so this must not be more than the platform's setting; less only costs requests. |

A path must start with `/` and hold no whitespace, query or fragment.

### Partitions

A flow names its partitions in one of three ways:

- `partitions: [dev, test]`: a run builds in the partition it names (`--set partition=test`), or, naming none, in the
  registry's default when the flow lists it, else in the flow's only partition.
- `source.headers.data-partition-id`: the flow builds in that one partition, and a run names none.
- Neither: the flow builds in every partition registered with the catalog, one per run, the one the run names or the
  registry's default.

A run builds in one partition; `*` is refused. A dimension's own `partitions` narrows it to some of the flow's
partitions, and a build elsewhere skips it. See [partitions](../concepts/partitions.md).

## A dimension

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `name` | text | required | A letter or digit, then letters, digits, `.`, `_` and `-`, at most 100. Unique in the flow ignoring case, and unique among the flows of the database, since the dimension's table is named after it alone (see [The dimension's table](#the-dimensions-table)). |
| `description` | text | none | What the dimension is for. |
| `kind` | text | required | The kind read, `authority:source:entityType:version`, wildcards per segment. |
| `query` | text | every record | A Lucene query narrowing the records read, with `{parameter}` and `{partition}` tokens. A token no parameter declares is refused. |
| `path` | text | required | The key: a property search can match exactly (see [Paths](#paths)). |
| `label` | path or list | none | Where a key's value is read: a path of the record the key names, or up to 3 paths, each but the last reading the reference the next record is found by. |
| `unlabelled` | text | none | The value of whatever is not read, at most 256 characters, for a dimension that reads a label or attributes: a key whose label is not read, an attribute a key has none of, and a collected attribute's records holding none of its values. |
| `attributes` | map | none | Further facts of each key, by name, at most 20. See [Labels and attributes](#labels-and-attributes). |
| `elements` | map | none | The objects of a nested array of each record, a row each with its fields. See [Elements](#elements). |
| `columns` | map | from the paths | `{ key: <name>, value: <name> }`: the names of the two columns of the dimension's table holding the key and its value. |
| `clean` | list | none | The steps each value is cleaned by, at most 20. See [Clean steps](#clean-steps). |
| `countRecords` | boolean | `false` | Count each value's records exactly, with one count of the search per value whose keys could share a record. Without it, a value's count is exact where a record holds the path once, and otherwise the sum of its keys' counts. |
| `maxValues` | integer | 1,000,000 | The most distinct keys a build reads, 1 to 5,000,000. A field holding more fails the build before it writes anything. |
| `partitions` | list | every partition of the flow | The flow's partitions this dimension is built in. Refused on a flow whose partition is its header's. |

### Paths

A path is segments of letters, digits and underscores separated by dots. It goes into a query unquoted.

- **A property of the record itself**, which the indexer maps the same way for every kind and needs no template: `id`,
  `kind`, `type`, `namespace`, `authority`, `source`, `version`, `createUser`, `modifyUser`, `createTime`,
  `modifyTime`, `collaborationId`, `acl.viewers`, `acl.owners`, `legal.legaltags`, `legal.otherRelevantDataCountries`,
  `legal.status`, `ancestry.parents`, `index.statusCode`, and one tag as `tags.<name>`.
- **A property of data** (`data.Curves.Mnemonic`), whose shape is read from the saved template of every kind the
  pattern matches in the partition. Each such kind needs its template saved ([templates](../concepts/templates.md)), and
  all of them must index the path the same way.

How the index stores the path decides how keys are read and compared: text through its `keyword` sub-field (the whole
value, up to 256 characters), a keyword, number, boolean or date as it is, and a property inside a nested array through
a nested aggregation, counted per object rather than per record. A number reads in its canonical form (`1.5`), a date as
`yyyy-MM-ddTHH:mm:ss.fffZ`. A string with a `date-time`, `date` or `time` format is read as a date, and one with an
`int32`, `integer` or `int64` format as a number. An object, a list of objects, an array of objects with no
`x-osdu-indexing` hint, a property without a type, and a nested array inside a nested array are refused, naming why and
what to read instead.

### Labels and attributes

A label and an attribute read through steps. With one path, it is read from the record the key names. With a list, each
step but the last reads the references the next records are found by:

```yaml
label: data.FacilityName
attributes:
  Country: [data.GeoContexts.GeoPoliticalEntityID, 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName']
  Operator: [data.CurrentOperatorID, data.OrganisationName]
```

- A build finds the records by id through the search service, 500 ids a search, in the kind of the entity type the id
  names. A label and attributes that start at the same records are read in the same searches.
- Every reference a step reads is followed, at most 20 a key; the first record reached that holds a non-empty value at
  the last path gives it. A label is cut at 1,024 characters, an attribute value at 256.
- A segment holding objects can filter them: `[Property=text]` keeps those whose property equals the text,
  `[Property*=text]` those whose property contains it ignoring case, `[Property$=text]` those whose property ends with it
  ignoring case. So the `Country` attribute above keeps, of every geopolitical entity a wellbore names, the one whose type
  is a country.
- A key that names no record, a record the search does not hold, and one holding nothing at the last path, have no label
  (or no such attribute); the build's notes count them and say why.
- A label or an attribute value that is itself a record reference is kept as the code its id ends with, decoded.

An attribute's name is a letter, then letters, digits and underscores, at most 64, unique ignoring case, none of
`value`, `keys`, `key`, `key_id`, `records`, `filter`, `label`, `id`, `partition`, and not the name of the dimension's
key or value column.

**Keeping a key, so tables join.** An attribute is written as settings to give it a `keep`:
`WellboreID: { path: data.WellboreID, keep: key }`. There are three:

| `keep` | Kept as | `dev:reference-data--UnitOfMeasure:us%2Fft:` becomes | Joins to |
| --- | --- | --- | --- |
| `value` (default) | As a value shows it: a record reference by the code its id ends with, its escapes decoded | `us/ft` | nothing; it is for reading |
| `key` | Exactly as the record holds it | `dev:reference-data--UnitOfMeasure:us%2Fft:` | the key column of a dimension keyed by the same path |
| `id` | A reference as the id of the record it names, its version (or the latest's trailing colon) taken off | `dev:reference-data--UnitOfMeasure:us%2Fft` | the key column of a dimension keyed by `id` |

```sql
SELECT l.WellLogName, w.Country, s.SamplingDomainTypeName
FROM osdu.dim_WellLog AS l
LEFT JOIN osdu.dim_Wellbore AS w                 -- keyed by data.WellboreID; l.WellboreID is keep: key
    ON w.partition = l.partition AND w.WellboreID = l.WellboreID
LEFT JOIN osdu.dim_RefSamplingDomainType AS s    -- keyed by id; l.SamplingDomainTypeID is keep: id
    ON s.partition = l.partition AND s.SamplingDomainTypeID = l.SamplingDomainTypeID
```

`path` takes a path or a list of paths, exactly as the bare form does, and `keep` works on a collected attribute and an
element's field too. A value longer than the 256 characters a dimension keeps is cut under `keep: value` and left out
under `key` and `id`, since a key cut joins to nothing; the build's notes count them.

**Collected attributes.** `{ collect: <path> }` collects the values of the dimension's own records instead of reading
the record a key names: `LoggingService: { collect: data.LoggingService }` on a dimension keyed by the well logs'
`data.WellboreID` gives each wellbore every logging service its logs name, with how many logs hold each. Picking a
collected value in a search finds the records holding it, not every record of the keys holding it. A build reads the
values the cheaper way: a read per value when there are few, or one pass over the records through the search cursor,
split into ranges read side by side. A collected path is a path search matches exactly, like the dimension's own. A
dimension collects one attribute: a second is refused, since two would pair values no record holds together.

### Elements

`elements` makes a row of every object of a nested array, beside the key of the record holding it, with every field of
one object on its row: what a collected attribute cannot do, since it collects one value a row. On a dimension keyed by a
well log's `id`, `elements: { path: data.Curves, ... }` is a table of curves, each curve's mnemonic, unit and depths
together:

```yaml
- name: LogCurve
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  path: id
  label: data.Name
  columns: { key: WellLogID, value: WellLogName }
  elements:
    path: data.Curves
    fields:
      Mnemonic: Mnemonic
      CurveUnit: CurveUnit                          # m, gAPI: the code a person reads
      CurveUnitID: { path: CurveUnit, keep: id }    # joins dim_RefUnitOfMeasure, keyed by id
      TopDepth: TopDepth
      LogVersion: { path: LogVersion, up: 1 }       # the log's own, on every curve's row
```

| Key | Default | Meaning |
| --- | --- | --- |
| `path` | required | The array, from the record's root, written as a label's path is. Every array on the way is stepped into, so `data.Curves.Columns` makes a row of every object of every curve's `Columns`; any segment can filter the objects it holds (`data.GeoContexts[GeoTypeID$=:Field:]`). What it ends at is the element: an object, or a plain value. |
| `fields` | required | The columns, each under its name: a path inside the element (`CurveUnit`, `Quantity.Code`, `Values[Type=Top].Depth`), `@` for the element itself (an array of plain values), or settings. |
| `fields.<name>.keep` | `value` | As an attribute's `keep`. |
| `fields.<name>.up` | 0 | How many objects up the element's path the field is read from: 1 the object holding the array, as many as the path has segments for the record. Not with `@`. |
| `fields.<name>.many` | `first` | A path reaching several values in one element: `first`, the first that is not empty, or `join`, each once, in order, joined by `; `. |

The table gains an `element` column: each key's objects numbered from 1, in the order of the records' ids and then of
the objects in each record, so the same records number the same way build after build and a row keeps its `id`. A key
whose records hold no object is one row with no element. A field reaching no value is null on its row; a number reads
as written (`203.149`), a boolean as `true` or `false`; a null in the array is no element.

A build reads the elements in one pass over the dimension's records through the search cursor, asking only for the
fields read (and the properties the filters compare), cut into ranges of keys read side by side; at most 5,000,000
objects a build, past which it fails before writing. Attributes and fields together are at most 20 columns, field names
are unlike every attribute's and `element`, and a dimension with elements collects no attribute, since both make a row
per value.

### Clean steps

Each value is cleaned by its steps in order: the label when one was read, the `unlabelled` text for a key whose label was
not read, otherwise the key (for a key naming a record, its decoded code). The result is trimmed. A step is written as
its name, or as a one-key map with its settings:

| Step | What it does |
| --- | --- |
| `trim` | Removes white space at both ends. |
| `collapseSpaces` | Makes every run of white space one space. |
| `upper`, `lower` | Changes case by the invariant culture's rules. |
| `nfc`, `nfkc` | Unicode normalization form C, or KC (compatibility characters folded). |
| `foldSeparators` | Keeps letters and digits in order, makes every run of anything else one hyphen, and lower-cases: `Gamma_Ray (API)` is `gamma-ray-api`. |
| `replace: { pattern, with }` | Replaces a regular expression's matches; `$1` names a group, `with: ''` removes them. The pattern runs on .NET's non-backtracking engine, at most one second a value, so a backreference, lookaround or atomic group is refused when the document is read. |
| `map: <dictionary>` or `map: { dictionary, field, otherwise }` | Looks the value up in a [dictionary document](dictionary.md) (`dictionaries/<name>.yaml` above the flow), exactly or ignoring case when that finds one entry, and replaces it with the entry's value (`field` names which, for a dictionary of several fields). `otherwise` says what an unlisted value comes to: left out, it is kept; `~`, the key is left out of every value; a text, that text. |

A key is left out of every value, with the reason kept on it, when cleaning leaves nothing, when the value is longer
than 256 characters, when `map` leaves it out, or when a step fails on it. `sqlflow validate` finds and reads the
dictionary each `map` step names, in the nearest `dictionaries/` folder walking up from the flow file, and refuses a flow
whose dictionary is missing or does not load
(`<file>: dimension '<name>': Dictionary '<dictionary>' was not found under '../dictionaries'. Expected <dictionary>.yaml or <dictionary>.yml.`).
A build of a dimension whose dictionary cannot be read fails, and the other dimensions build.

## The dimension's table

Each dimension is one table in the module's database, `osdu.dim_<dimension>` (`osdu.dim_Wellbore`): the dimension's
name with anything not a letter, digit or underscore made an underscore. It is what a report, a query, a cascade of
drop-downs or a table of facts reads. A build makes it and keeps it; no line of the document asks for it, and no
migration creates it.

| Column | Holds |
| --- | --- |
| `id` | The row's number: an identity, the clustered primary key, and what a table of facts joins on. It stays the same for as long as the dimension holds the row. |
| `partition` | The partition the row was read in. A flow building in several partitions writes them all to this table. |
| `key_id` | The key's number, the same in every row of the key. |
| the key's column | The key exactly as the index holds it. |
| the value's column | The key's value. |
| `records` | The records of the row: those holding the collected value, or every record of the key. |
| `filter` | The search filter finding the key's records; null when no query can carry the key. |
| one per attribute | The attribute's value for the row, under the attribute's name; null where the key has none. |

A row is a key and the value it collects: a dimension with a collected attribute has a row for each value a key holds,
any other a row a key. A key left out by cleaning, and a key no build finds any more, is no row.

**Column names.** The key's column is named after the property `path` ends with (`data.WellboreID` is `WellboreID`);
the value's after the property the last `label` path ends with (`FacilityName`), or after the dimension's own name when
it reads no label (`CurveMnemonic` beside `Mnemonic`). Where the two would be the same ignoring case (a dimension
`Source` reading `data.Source` with no label), the value's column keeps the name and the key's takes `Key` at its end:
`Source` and `SourceKey`. `columns: { key, value }` names either one; it has to where the name would be one of `id`,
`partition`, `key_id`, `records` or `filter`, an attribute's name, or no column name at all. A name is a letter, then
letters, digits and underscores, at most 64; the key's column cannot be named `value` nor the value's `key`, the words
that ask for the two columns whatever they are named.

**The schema follows the document.** SQLFlow's [schema evolution](../../../../sqlflow/docs/reference/concepts/schema-evolution.md)
brings the table to the declaration before each write: an attribute added gets its column on the next build, and
nothing is dropped or narrowed. An attribute taken out keeps its column, emptied; a column somebody added is left alone.
When the key's or value's column is named otherwise (the path or label changed, or `columns` says so), the next build
renames it where it is, keeping every row and its `id`; a query naming the old column has to change with the flow, and a
name given under `columns` stays through a change of path or label. A build writes only the rows that differ, in the
transaction that writes the rest of the dimension. When the collected attribute is added, taken out or replaced, the
partition's rows are written again under new numbers.

The table is read through two indexes the build makes: `IX_key` (`partition`, `key_id`) and `IX_value` (`partition`, the
value's column, `id`).

## Views

A view puts dimensions of one flow side by side at the grain of one of them, so a pipeline, a report or a person reads
one table instead of writing the joins. A build writes each as `osdu.dimv_<name>` after the flow's dimensions, checks it,
and drops a view the flow no longer declares.

```yaml
target:
  connection: ${env:SQLFLOW_OSDU_DB}      # the module's database, named as the pipelines reading the views name it
views:
  - name: Curve                           # osdu.dimv_Curve
    description: Every curve of every well log, with its log and the curve's unit.
    from: LogCurve                        # a row of the view for each row of dim_LogCurve
    join:
      - { on: WellLogID, to: WellLog, as: Log }       # the same key: the log's own row
      - { on: CurveUnitID, to: RefUnitOfMeasure, as: Unit }
      - { on: Log.SamplingDomainTypeID, to: RefSamplingDomainType, as: Domain }   # through the log
    columns:
      WellLogID: WellLogID
      Log: Log.WellLogName
      Curve: Mnemonic
      Unit: Unit.UnitCode
      Domain: Domain.SamplingDomainTypeName
      TopDepth: { expression: TopDepth, dataType: float }
      Interval: { expression: "TRY_CAST(BaseDepth AS float) - TRY_CAST(TopDepth AS float)", dataType: "decimal(18,3)" }
      Created:
        expression: Log.CreationDateTime
        dataType: datetime2(0)
        description: When the log was made, in UTC.
```

| Key | Default | Meaning |
| --- | --- | --- |
| `target.connection` | required with `views` | The module's database, written as a delivery flow's `source.connection` is: a `${env:...}` or `${keyvault:...}` reference, or a connection string whose password is one. |
| `views[].name` | required | A letter, then letters, digits and underscores, at most 64; unique among the views of a database, ignoring case. |
| `views[].description` | none | What the view holds, shown with it. |
| `views[].from` | required | The dimension of the flow whose rows the view's rows are. |
| `views[].join[]` | none | `{ on, to, as }`: the column joined on (bare for a column of `from`, `alias.column` for one of an earlier join), the dimension of the flow joined, and the alias its columns are read by (the dimension's name unless given). At most 16. |
| `views[].columns.<name>` | every column | An expression, or `{ expression, dataType, description }`. At most 256, beside `partition` and `id`. |

**What a view holds.** Every view begins with `partition` and `id`, the number of the `from` row each of its rows stands
for, which stays the same for as long as the dimension holds the row: a pipeline merges on it. Then its `columns`, in
order. A view that lists none holds every column of `from` but its numbers, partition and filter, then every column of
each join prefixed by its alias (`Unit_UnitCode`, `Unit_id`), as the tables hold them. Every join is a left join that
meets at most one row, so a row of `from` is a row of the view.

**Joins**, refused where the document is read, naming the view and the join:

- A join is on a key: a dimension's key column, or an attribute or an element's field kept as `key` or `id`
  ([keeping a key](#labels-and-attributes)). A key's column or a `keep: key` column joins a dimension keyed by the same
  path; a `keep: id` column joins a dimension keyed by `id`; two dimensions keyed by `id` read kinds of one entity type.
- A dimension with `elements` or a collected attribute holds several rows a key, and is refused as a join: it would
  repeat the view's rows. Make it the view's `from`.
- Joins stay inside one flow, so one run settles every table a view reads. Each compares the partition, the key's
  SHA-256 (`key_hash`, which every dimension table keeps and indexes) and the key's text under a binary collation, so
  ids that differ only in case never meet, whatever the database's collation.

**Expressions.** A column is a T-SQL scalar expression, read by SQL Server's own parser and written into the view by the
module from what it read. It uses numbers, `'text'`, `N'text'`, `NULL`, `+ - * / %`, comparisons, `AND`, `OR`, `NOT`,
`IS NULL`, `LIKE`, `IN` a list, `BETWEEN`, `CASE`, and the functions `COALESCE`, `NULLIF`, `ISNULL`, `IIF`, `TRY_CAST`,
`TRY_CONVERT`, `CAST`, `CONVERT` (no style), `LEFT`, `RIGHT`, `SUBSTRING`, `LEN`, `UPPER`, `LOWER`, `TRIM`, `LTRIM`,
`RTRIM`, `REPLACE`, `CHARINDEX`, `CONCAT`, `CONCAT_WS`, `ABS`, `ROUND`, `FLOOR`, `CEILING`, `POWER`, `SQRT`, `EXP`, `LOG`,
`LOG10`, `SIGN`, `DATEADD`, `DATEDIFF`, `DATEPART`, `YEAR`, `MONTH`, `DAY` and `EOMONTH`. A subquery, a variable, a
window, `COLLATE`, a value of the clock (`GETDATE()`) and any other function are refused. Every column of a dimension's
table is text but `id`, `key_id`, `records` and `element`; where SQL Server would convert implicitly (`BaseDepth -
TopDepth`, text compared with a number, `CONCAT` of a number) the expression is refused, saying to convert first. A
division by zero, a root or a logarithm outside its domain and a negative length are null. Quote an expression that
starts with `[`, and a type with a comma in a flow mapping (`dataType: "decimal(18,3)"`).

**Data types.** `dataType` converts by the module's conversion, which never fails a read: a value it cannot convert is
null, and the build counts it. The types are `bit`, `tinyint`, `smallint`, `int`, `bigint`, `decimal(p,s)`,
`numeric(p,s)`, `float`, `real`, `date`, `time(n)`, `datetime2(n)`, `datetimeoffset(n)`, `uniqueidentifier`,
`nvarchar(n)` and `nvarchar(max)`; `varchar`, `char`, `datetime` and `smalldatetime` are refused. The same conversion
runs for `CAST`, `CONVERT`, `TRY_CAST` and `TRY_CONVERT` inside an expression.

| To | Reads | Unlike `TRY_CAST` |
| --- | --- | --- |
| `float`, `real` | `203.149`, `-999.25`, `1.5E3`; not `12,5` or `NaN` | The same. |
| `decimal`, `numeric` | The same, rounded to its scale | Reads an exponent (`1.5E3`), through `float`. |
| `tinyint` to `bigint` | A whole number: `7`, `7.0`, `7E0` | Reads `7.0`; a fraction (`7.5`) is null, never cut. |
| `bit` | `true`, `false`, `1`, `0`, ignoring case | `2` and `yes` are null. |
| `datetime2(n)` | ISO 8601, as the instant in UTC (`11:16:03+02:00` is `09:16:03`); no offset is UTC | Applies the offset, which a cast drops; reads ISO 8601 alone, whatever the session's date format (`03/04/2013` is null). |
| `datetimeoffset(n)`, `date`, `time(n)` | ISO 8601, keeping the offset, or the date or time of day as written | ISO 8601 alone. |
| `nvarchar(n)` | A text of at most `n` characters; a number with every digit, a date as ISO 8601 | A longer text is null, never cut. |
| `uniqueidentifier` | A GUID's text | The same. |

**The check.** After writing its views, a build reads each one's rows of the run's partition: the rows, for each join
how many rows found theirs and some values that found none, and for each converted column how many values did not
convert, with examples (the row's `id` and the text). The run's result says so (`CurveUnitID: 1,204 of 88,310 rows name
no row of RefUnitOfMeasure`, `TopDepth: 37 value(s) did not convert, for example '12,5'`). A value no expression could be
written around (an arithmetic overflow) fails the build, naming the view and the column, so the pipelines ordered after
the flow do not read a view that cannot be read.

**Writing.** A build writes each view in one transaction under a lock of its own, only when its statement changed, with
its column list (never `*`), making a table it reads that no build has made yet (empty, so its join finds nothing). A
view whose name another flow's view holds, or an object no build made, is refused, naming it, and nothing is written
over. A build that renames a key's or a value's column drops the views reading that table in the same transaction, and
the run writes them again at its end. A dimension a view reads is not removed until the flow's next build has written
the view without it.

**Lineage.** With `target.connection`, the flow declares that it writes each `osdu.dim_<dimension>` table and each
`osdu.dimv_<view>` view on that connection, so a pipeline reading them through the same reference is ordered after it.
SQLFlow tells servers apart by the reference as written, so the two name it alike. A build and a plan ask the server
which database the reference reaches and refuse one other than the module's own, naming the reference and never what it
resolves to. A table of facts for analytical queries is an ingestion flow reading the view (keyed by `partition` and
`id`) into a table with a clustered columnstore index:

```yaml
flowType: ing
name: welldb-welllog-08-curve-table-ing
connections:
  osduDelivery: ${env:SQLFLOW_OSDU_DB}    # the same reference as the dimension flow's target.connection
  warehouse: ${env:WAREHOUSE_DB}
source:
  server: osduDelivery
  object: OsduDelivery.osdu.dimv_Curve
target:
  server: warehouse
  object: Warehouse.arc.Curve
  columnStoreIndex: true                  # made when the run first creates the table
load:
  keyColumns: [partition, id]
  matchKeysInSourceAndTarget: true
matchKeys:
  action: delete                          # a curve the view no longer holds leaves the table
```

## Operations

| Operation | What it does |
| --- | --- |
| `build` (default) | Reads every distinct key of each dimension, reads labels and attributes, cleans keys into values, and keeps what changed. |
| `plan` | Settles each dimension's field from the templates and counts the records it would read, and gives each view's statement and what would stop a build writing it; reads no value and keeps nothing. |

The run payload takes one field, `dimensions`, the names of the dimensions to build; a run naming none builds every
one. A name the flow does not declare fails the run, listing the flow's dimensions. Anything else in the payload is
refused before the run starts, for example
`payload tests does not apply to a dimension flow: only an assertion flow's runs select tests; a dimension flow's payload names only dimensions.`
SQLFlow's backfill flags (`--full`, `--from`, ...) are refused too, with or without a payload.

```bash
sqlflow run flows/welldb-welllog-05-dimensions.yaml --set partition=dev --payload '{"dimensions":["Wellbore"]}'
sqlflow run flows/welldb-welllog-05-dimensions.yaml --set partition=dev --operation plan
```

Both operations read the saved templates in the module's database, and a build keeps what it read there. See
[running an OSDU flow](../cli/run.md) for the run options.

## What a build does

For each dimension, in the run's partition:

1. **Kinds and the field.** For a `data` path, one aggregation by `kind` lists the concrete kinds the pattern and query
   match, and the newest saved template of each says how the index stores the path. A kind without a saved template
   fails the dimension. A partition holding no record of the kind leaves the dimension with no value and says so.
2. **Every distinct key.** The search aggregates the field over the whole query. A range answered with fewer groups
   than `aggregationSize` is complete; one answered with that many is split at a key it returned and each half is asked
   again, until every range answers whole. A range that cannot be split is read record by record through the search
   cursor. Ranges are asked `concurrency` at a time. A text value over 256 characters is not in the `keyword` field and
   is counted, never a key; a key over 1,024 characters is counted and left out.
3. **Labels, attributes and collected values**, read side by side when the dimension has both.
4. **Values and filters.** Each key is cleaned into its value. A key's filter is written the way the index holds the
   field; a value's filter holds at most 500 keys a query, and a value with more has its filter in parts. A key no query
   can carry (a control character, `nested(`, or inside a nested array a value the service rewrites) stays under its
   value, marked unfilterable.
5. **The write**, in one transaction under a lock per dimension: new keys and values are added, changed ones rewritten,
   what the build no longer found marked removed (keeping its id should a later build find it again), every key that
   arrived, left, moved or came back logged, and the dimension's table brought to the same rows. The write has no time
   limit: it grows with the dimension (one of well log curves stages a row for every field of every curve), so it takes
   as long as its rows do, and cancelling the run stops it with nothing written.

Dimensions build `concurrency` at a time. A dimension that fails keeps what the build before it wrote, and the run ends
failed with every other dimension built. A second build of the same dimension waits for the first's lock and fails after
two minutes: `Another build of dimension <id> held its write lock for more than 120 seconds (sp_getapplock answered ...),
so this build wrote nothing. Build it again when the other has finished.`

A build reads the index as it stands: a record written seconds before may not be in it yet. When the counts read do not
add up to the records holding a value, the build keeps what it read and says so in its notes.

## What a run returns

A build's result (`run.json`, the run page) is the flow, the partition, how many dimensions were built, failed and
skipped (not built in this partition), and per dimension its status (`completed`, `failed` or `skipped`), build number,
values, keys, keys of no value, unfilterable keys, labelled keys, what it changed (values and keys added, removed,
restored, keys moved), requests, the field it aggregated, its error, and its first 5 notes. Every note is on the build in
the ledger (`sqlflow dimensions history`). A plan's result is, per dimension, the query, the field it would read,
whether a record holds it more than once, the records it would read, the kinds with their records and templates, and
what stops it. Both say, per view, what the build did or would do: a build's `views` give each view's status
(`written`, `unchanged` or `failed`), its check (`passed` or `failed`), its rows and its first notes, and `viewsDropped`
the views it dropped; a plan's `views` give each view's tables, its statement, what stops it and what a build will do. A
view that cannot be written or read fails the run, every dimension still built.

## Removing a dimension

A dimension taken out of the flow is built no more and keeps what its last build wrote; declaring it again brings its
ids and history back. An admin removes it for good with **Remove** on the GUI, `DELETE /api/v1/delivery/dimensions/{id}`,
or [`sqlflow dimensions remove`](../cli/dimensions.md#remove). A removal is refused for a dimension the flow still
declares and for one a cache flow of its partition captures, takes everything of the dimension in its partition (its
rows in its table, which is dropped when no partition writes it any more), and is recorded as a `remove-dimension`
activity of the flow with who asked. A dimension a view reads is refused too, naming the view.

A view the flow no longer declares is dropped by its next build. One whose flow is gone is removed by an admin with
**Remove** on the view's page, `DELETE /api/v1/delivery/dimensions/views/{name}`, or
[`sqlflow dimensions remove-view`](../cli/dimensions.md), recorded as a `remove-view` activity.

## Lineage, the GUI and the cache

- **Lineage.** A dimension flow reads each dimension's kind on its platform and partition, the dictionary files its
  `map` steps name, and writes each dimension. It is ordered after the delivery flows writing the kinds it reads, and a
  cache flow holding one of its dimensions is ordered after it ([lineage](../concepts/lineage.md)). With
  `target.connection` it also writes its tables and views on that connection ([Views](#views)).
- **The GUI.** A dimension flow's pipeline has a **Dimensions** tab. **OSDU**, **In OSDU**, **Dimensions** lists every
  dimension with the search builder; a dimension's page has the tabs Table, Values, Keys, Changes, Builds and
  Definition (the YAML beside a diagram of how each column is read).
- **The cache.** A [cache flow](cache.md) can hold a dimension's values as a lookup table (`dimension:` and
  `dimensionFlow:` in a cache type), so a mapping turns a raw key into its value as the dimension does.

## Errors when the document is read

| Message (abridged) | Cause |
| --- | --- |
| `dimensions must list at least one dimension; a dimension flow without dimensions has nothing to build.` | No `dimensions`. |
| `dimensions[1] 'Well-Type' would share its table with 'Well.Type': a table's name keeps the letters, digits and underscores of a dimension's name, and theirs are the same. Rename one of them.` | Two names that make the same table name. |
| `path legal.foo is not a value the index holds of the record; under legal it holds legal.legaltags, legal.otherRelevantDataCountries, legal.status.` | A record property the indexer does not map. |
| `the key's column would be named 'id', after the property its path ends with, which is a column every dimension's table has already (id, partition, key_id, records, filter). Name it in the document: columns: { key: <name> }.` | `path: id` without `columns.key`. |
| `the value's column would be named 'FacilityName', after the property its label ends with, which is the name of its attribute FacilityName, ...` | An attribute named like a column. |
| `unlabelled is the value of a key whose label or attribute is not read, and the dimension reads neither.` | `unlabelled` without `label` or `attributes`. |
| `attributes.B is a second collected attribute beside A; a dimension collects one, ...` | Two `collect` attributes. |
| `label reads through 4 records; a label reads through at most 3, a search per step for every key.` | Too many steps. |
| `clean[0]: the pattern /(a)\1/ cannot be used: it uses something the non-backtracking engine cannot run ...` | A pattern needing backtracking. |
| `query uses '{logSource}', which is not declared under parameters.` | An undeclared token. |
| `source.headers names 'data-partition-id', and the flow names its partitions: every run sets the header to the partition it builds in. Remove the header.` | Both ways of naming a partition. |
| `source.aggregationSize is 20000; ... between 10 and 10000.` | Out of range. |
| `views needs target.connection: the module's database as the pipelines reading the views name it ...` | Views without `target.connection`. |
| `views[0] 'Curve': join[0].on 'Mnemonic' holds no key: Mnemonic is a field kept as a value ...` | A join on a column holding no key. |
| `views[0] 'Curve': join[0] joins 'WellLogID' to RefUnitOfMeasure, and it holds the id of a work-product-component--WellLog and the dimension is keyed by the id of a reference-data--UnitOfMeasure, ...` | Keys that never meet. |
| `views[0] 'Curve': join[0] joins Wellbore, whose table holds a row per value it collects of each key, so a join to it would repeat the view's rows. ...` | A join to a dimension of several rows a key. |
| `views[0] 'Curve': columns.Interval: the expression 'BaseDepth - TopDepth' gives the operator - text (BaseDepth), where it takes a number; ...` | An implicit conversion. |
| `views[0] 'Curve': columns.Curve: the expression '...' uses a subquery, which a view's expression may not: ...` | Something an expression may not use. |
| `views[0] 'Curve': columns.Depth: dataType converts to 'varchar(10)'; varchar and char would turn every character outside their code page into ? ...` | A type a view does not convert to. |

## Errors when a dimension builds

| Message (abridged) | What to do |
| --- | --- |
| `Dimension Wellbore reads data.WellboreID of <kind>, and no template is saved for <kind> (<n> record(s)), so how the index stores the field there is not known. Capture each on the Templates page, or with 'sqlflow template capture --kind <kind>'.` | Save the template of every kind the pattern matches. |
| `Dimension <name> reads <path>, which the kinds its pattern matches index differently: ... One field is read one way; narrow the kind to the versions that agree.` | Narrow `kind`. |
| `<path> holds more than <n> distinct values, the most this dimension keeps (maxValues). Narrow the dimension's query, or raise maxValues if the partition really holds that many.` | Narrow or raise `maxValues`. |
| `Dimension <name> of <flow> would write the table osdu.dim_<name>, which dimension <name> of <other flow> writes: ... Rename one of them.` | Dimension names are unique across the flows of a database. |
| `Dimension <name> could not read dictionary <dictionary>: ...` | Add or fix `dictionaries/<name>.yaml`. |
| `The dimension's table osdu.dim_<name> has a column <name> already ... Drop that column, or name the <role>'s column otherwise in the flow: columns: { <role>: <name> }.` | A rename would take a name another column holds. |
| `Dimension flow '<flow>': target.connection ${env:<NAME>} reaches another database than this host's module database, ... Point it at the module's database.` | Point the reference at the module's database. Nothing was built. |
| `View <name> is declared by dimension flow '<other>' as well, and a view's name is unique among the flows of a database, so osdu.dimv_<name> was not written. ...` | Rename one of the views, or remove the other flow's. |
| `osdu.dimv_<name> is in the database, and no build of a dimension flow made it, so it is not written over. ...` | Drop the object, or name the view otherwise. |
| `Column <column> of view <name> could not be computed for every row of partition '<partition>': Arithmetic overflow ...` | Write the expression around the value. |
