# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

This repository is a rebuild of OSDU Delivery as a dedicated solution on its own vendored copy of SQLFlow. The
previous implementation's history is not carried over here; `docs/plan.md` describes the stages of the rebuild and
`osdu/README.md` records what was copied from that implementation and what was deliberately left behind.

## [Unreleased]

### Added

- **Send a whole flow's records again.** A deliver run's `redeliver` no longer needs `recordKeys`: without them it sends
  again, changed or not, every record the flow has delivered in the partition (the metadata and payload, the record, or
  the payload or one of its parts), marked a slice at a time and sent at most 5,000 a run, the flow's next runs sending
  the rest. The trigger dialog shows **Send again** beside **Force** on every deliver run, with "send only what changed"
  as its default, and says what each choice sends; **Force** now says it looks at every record and still sends only
  what changed. A run on a submission takes no `redeliver`. The run page names a flow-wide redelivery as such.
- **A flow's scope offers the values its column holds.** A parameter a delivery flow's `source.record.scope` binds to a
  column of its record table is a pick list wherever the scope is given its values: a flow's Preview tab, a mapping's
  Check values, and the trigger dialog, which asks for each parameter the flow declares instead of `name=value` lines.
  The list is the distinct values the column holds in rows not marked deleted, the most rows first with each one's row
  count, read on a node from the flow's own table with its own connection (`POST
  /api/v1/delivery/flows/{pipelineId}/scope-values`, a node task `delivery-scope-values`), so a source scoping by any
  column offers its own values and no list is kept to go stale. A value can still be typed, and one the column holds no
  row of is flagged. The interfaces listing names each parameter's `scopeColumn`.
- **Check a mapping's values against a flow's rows.** The Mappings page's Properties tab gains **Check values**: it
  reads the rows of a flow that renders with the mapping, renders each on a node exactly as a delivery renders it
  (columns, expressions, cache lookups and `$findAll`, searches, `$coalesce`, lists, repeated items and every
  modifier), holds every value written to what the template says of its attribute (type, format, pattern, allowed
  values, lengths, ranges, list sizes, required and unknown properties of an object written whole, the entity types a
  reference may point to), and says for each attribute which rows are held, write a value the template does not
  accept, leave it out, or are not meant to have it, with every reason, the values behind it and the records (source
  key, label, file and row, item). One attribute is checked from its own properties, or every attribute at once; the
  tree then marks each attribute with its failing rows and narrows to the ones that fail. Counts are exact and what is
  listed is bounded, so a check reads the same for a million failing rows as for ten, and pages further records on
  request. The API is `GET /api/v1/delivery/mappings/{id}/flows` and `POST /api/v1/delivery/flows/{pipelineId}/check-values`
  (a node task, `delivery-check-values`); the CLI is `sqlflow values`, whose `--rows` writes every failing row to CSV.
  Nothing is sent or written.
- **Each cached type carries its own content hash.** A version of a partition's cache is the whole cache and is written
  when anything in it moves, so every type it held looked changed with it. Each type a version holds now records its
  own content hash (over exactly the bytes the type contributes to the version's hash), how it compares with the
  version before (`added`, `changed` or `unchanged`) and the version its content dates from, in `osdu.CacheVersion`'s
  type list, the version API, `sqlflow cache list` and a refresh's result. A merge reads and rewrites only the records
  of the types that moved, a refresh analyses only those types for what they reach, and a load checks every type
  against its own hash and names the one whose records were altered. The Versions tab lists the types each version
  moved, and a type's header on the Records tab says whether the version being read changed it or holds it unchanged
  since an earlier one. Versions written before are read as they were, and the next version dates their types from the
  records' ranges.
- **A type's own versions on the cache page.** With a type in view, the Versions tab lists the versions of the cache at
  which the type's content hash moved (its hash, its record count, and what it added, changed or removed), each
  compared with the version of the type before it, and the Records tab's version picker offers those versions alone,
  so any two versions of a type picked there differ. The cache history API gives each type a version moved its hash
  and record count there (`hash`, `items`).

- **A property no entry of its own fills says what does.** The coverage of a mapping (`POST
  /api/v1/delivery/mapping-builder/coverage`) names, for every variable an entry further up writes, that entry
  (`writtenBy`) and the values a static value gives it (`values`), so the Mappings view shows the
  `TechnicalAssuranceTypeID` of a static `TechnicalAssurances` list as "the static value of
  `osdu.data.TechnicalAssurances`" with `{$param.dataPartition}:reference-data--TechnicalAssuranceType:Unevaluated:`,
  where it said only "the entries filling what it holds". The same holds for a `$coalesce`'s literal alternative, and
  for an object written whole from a cached field, whose properties now read as written by that entry on some rows
  instead of as filled by nothing (which raised a false warning for a property the schema requires in it). The mapping
  builder's variable list says the same, offers no cache entry for such a variable, and opens the entry that writes it.
  Beside the tree, an `$unverified` entry and a lookup with `$ignoreSeparators` say so.
- **`$coalesce`: a property taken from the first of several sources that gives a value.** A `$coalesce` node lists two
  or more alternatives, each a value node of its own (a column, an expression, a cached record, a search or a literal
  default, with its own `$findBy`, `$modifiers`, `$ignoreSeparators` and `$unverified`), tried in order; `$when`,
  `$required` and `$description` beside the list decide for all of them. A miss passes to the next alternative, a
  mistake (a date that is not a date, a value several records answer to) holds the record, a search not answered yet
  stops the node until it is, and a literal can only be the last. When none gives a value, a required node holds the
  record naming why each gave nothing. What every alternative tried read from the cache is recorded, so a later cache
  version that would let an earlier one give a value reaches the record. The preflight checks each alternative and names
  it (`record.data.Unit.$coalesce[1]`), the mapping builder edits it as "First value of" with each alternative an input
  of its own, and the record's Render tab, the preview and `sqlflow preview` say which alternative gave each value.
- **`$unverified`: an id written without a record the cache holds, where the mapping says so.** On a node that builds an
  id with `id` or `ref`, `$unverified: true` writes the id even when the cache holds records of its entity type and not
  this one. The render records it as an unverified reference (cache usage kind `unverified`), the preview lists the
  references a record carries unverified, and a later refresh that holds the record tags the change `found`, so the
  record is built again against it.
- **The recall WellLog mapping combines the partition's reference data with the database's tables.** Its units are a
  `$coalesce`: the Recall spelling through the unit table as a unit the partition holds, else the partition's own units
  by ID, Code or Name as petrodb-api matches a unit, else the table's translation unverified; the curve dictionary's type
  and family codes go out `$unverified`. The five sample logs render unchanged; on dev, a curve in psia or mD/mD, or a
  curve type the partition does not hold yet, now goes out with an unverified reference instead of holding its log or
  going without it.
- **A built reference has to name a record the partition holds.** Where the version of the cache a render reads holds
  records of the entity type an `id` or `ref` modifier builds, captured from the partition by a cache flow, the id has
  to name one of them, looked up by its exact id. One that names none holds the record when the entry is required and
  leaves the property out when it is optional, with the id and the types looked in as the reason, instead of reaching
  OSDU as a reference to nothing; a record found is a dependency of the render, so a later version that drops it reaches
  the record. A version holding no record of that entity type answers nothing, so a mapping whose cache holds no
  reference data renders as before. This is petrodb-api's OSDU unit cache (units matched against the partition's
  UnitOfMeasure records), widened to every reference a mapping builds. Looking the id up rather than a code finds a
  record whatever its code says (dev holds `LogCurveType:Equinor-RW` with the code `Equionr:RW`, and
  `LogCurveFamily:EQ-CPI%20Qual%20Flag` with `EQ-CPI Quality Flag`), and a stray record beside it decides nothing (dev
  holds `UnitOfMeasure:degC:` beside `UnitOfMeasure:degC`).
- **The sample estate captures its partition's reference data from OSDU.** `recall/cache/recall-reference-00-cache.yaml`
  searches the nine reference types a Recall well log points to (UnitOfMeasure, VerticalMeasurementType,
  WellLogSamplingDomainType, LogType, LogCurveBusinessValue, TechnicalAssuranceType, LogCurveType, LogCurveMainFamily,
  LogCurveFamily) with the delivery flow's own endpoint, credentials and partition, beside the lookup tables
  `recall-lookups-00-cache` reads from the database: the WellLog mapping translates a Recall value through a table and
  the id it builds is checked against the captured records. On dev it captures 47,330 records (43,423 of them curve
  types) in about 20 seconds, and the five sample logs render unchanged against it. The suites import
  `osdu/samples/cache-records` as its capture, and the GUI end-to-end seed does too.
- **One cache flow may combine types from OSDU, the database and the repository.** A flow declaring a `kind`, a `table`
  and a `dictionary` loads with both its `endpoint` and its `connection`, and one refresh captures all three into one
  version of the partition's cache; a test now pins it.
- **Sync timeline consolidates the ledger with the ingestion tables.** A button on a record's page (Sync timeline), on
  a flow's Delivery tab, above its Records list and in its selection bar (Sync timelines) reads the records' rows from the ingestion tables (one
  record, the ticked or filtered ones, or every record of an interface) and consolidates the ledger with them: an
  arrival it lacks is written, a row that changed or was marked deleted without any run planning it is asked to be
  planned by the flow's next run, a row gone from the table is put on its record's history as `skipped` /
  `source-missing` (once, the record keeping its status), and a deleted-row hold whose row came back is counted for
  release. It is a `sync` run of the flow, recorded as an activity with who asked, and it renders nothing and sends
  nothing to OSDU. Each row is read by the key the ledger stored, in the scope its last submission ran with, a page of
  1,000 records at a time. `POST /api/v1/delivery/records/{flowId}/{key}/sync` and
  `POST /api/v1/delivery/flows/{pipelineId}/sync` (`keys`, `filter` or neither) queue it.
- **The ledger keeps every change of a record's row, and when the row arrived.** A record keeps when the ingestion
  table first inserted its row (`InsertedDate_DW`, `source.systemColumns.inserted`, opted out of with `~`), which later
  changes never move. A row the ingestion table changed that renders the document OSDU already holds, or the one already
  queued, is recorded as an attempt (`skipped`, phase `identical`) with the row's new origin, and the record's origin
  moves to that row with its fingerprint: before, only the fingerprint moved and the change left no trace. It is written
  only when the row's stamp moved, so a cache or mapping rollout that renders the same rows again writes nothing. The
  hold of a row the ingestion table marked deleted is its own phase (`held`, `source-deleted`) and carries the moment of
  the deletion. Migration `RecordSourceArrivalAndDeletion`, module version 1.13.0: two nullable columns
  (`Record.SourceInsertedUtc`, `Attempt.SourceDeletedUtc`); a record learns its arrival the next time a plan reads its
  row.
- **A record can be previewed before it is sent, and read as OSDU holds it.** A delivery flow's new Preview tab
  renders one record on a node exactly as a delivery would, and sends nothing: the first record of the scope, or the one
  a key names (a source key as the Records page shows it, a delivery key, an OSDU id the ledger holds, or the key's
  parts, a part holding a slash included), with the flow's parameters asked for as it declares them. It shows what the
  next run would do with the record and why, the document as the route sends it (on the file, manifest and composed
  routes with a placeholder for each dataset id the File service mints, on the dataset route with the id derived from
  the record's), the route's requests in order, the payload files with each parquet file's rows and columns from its
  footer, the records the document refers to, and the record's source rows; it downloads as JSON. The same preview is
  `sqlflow preview <flow.yaml> [--key <key>]`, and `POST /api/v1/delivery/flows/{pipelineId}/preview`. A record's page
  gains an In OSDU tab (the record as OSDU holds it, read through its flow's route: its version, who changed it, its
  access and legal tags, the document, and every record it refers to, readable in turn through
  `POST /api/v1/delivery/flows/{pipelineId}/osdu/read`) and a Compare tab (what OSDU holds beside what a delivery would
  send now, side by side and path by path, OSDU's own fields set aside). Every records list carries In OSDU on each row
  with an OSDU id. A preview writes nothing to OSDU, the ledger or the work location, and a record of any size answers
  within a node task's bounds.

- **A mapping computes values and decides conditions with expressions.** `$expr` gives a property a value computed
  from the row (`$expr: coalesce(log_name, log_source)`), `$when` is now an expression giving true or false
  (`$when: depth_coding = "REGULAR" and not empty(index_increment)`), and a `$forEach` node's new `$where` keeps the
  child rows that hold one (`$where: curve_id != "DEPT"`). The language is OSDU Delivery's own, small and fixed:
  columns as `$from` names them, `$dataset.<column>` and `$param.<name>`; `=`, `!=`, `<`, `<=`, `>`, `>=`,
  `in [...]`, `and`, `or`, `not`, `&` for text and `+ - * /`; and `iif`, `coalesce`, `nullif`, `empty`, `trim`,
  `upper`, `lower`, `substring`, `left`, `right`, `replace`, `length`, `contains`, `startsWith`, `endsWith`, `number`,
  `text`, `round` and `abs`, named and behaving as in SQL, so whoever writes the ingestion SQL reads a mapping without
  learning another language. Nothing an expression calls reads a clock, a random source or anything outside the row.
  Missing and blank values are no value, text compares trimmed and ignoring case, numbers and dates compare as what they
  are, and a value an expression cannot work with holds the record, naming the expression, the part and the value.
  Every column and parameter an expression reads is known when the mapping is read, so the preflight checks them against
  the ingestion tables and the flow's parameters like any other, and lineage and coverage see them. Mistakes are refused
  with what to write instead, including a condition in the old `column is text` form, which is refused with the
  expression that replaces it. Heavy computation stays in the ingestion SQL: an expression is at most 2000 characters and
  nests at most 48 levels ([osdu/docs/mapping-templates.md](osdu/docs/mapping-templates.md#expressions)).
- **The ref modifier builds a reference from the entity type the property points to.** `- ref` writes
  `{$param.dataPartition}:<group>--<Entity>:{$value}:` for a property whose template relationship names one entity type;
  `- ref: UnitOfMeasure` picks one of several, and `- ref: reference-data--UnitOfMeasure` names any. It is checked and
  rendered exactly as an `id` template is, and one the template does not settle fails the preflight with the types the
  property points to. The sample WellLog mapping's seven `{$value}` references use it
  ([osdu/docs/documents.md](osdu/docs/documents.md#ref)).
- **Fixtures share their parameters, and `sqlflow fixtures update` writes their expected records.** `fixtureDefaults.parameters`
  gives every fixture the parameter values it renders with, and a fixture's own `parameters` replace them name by name.
  `sqlflow fixtures update <flow.yaml>` renders each fixture as the preflight does and writes what it renders into its
  `expected` block, touching nothing else in the file, leaving a fixture that already matches as written, and skipping,
  with the reason and a failing exit code, one whose record would be held or whose `expected` cannot be edited in place;
  `--dry-run` says what would change ([osdu/docs/reference/cli/delivery.md](osdu/docs/reference/cli/delivery.md#fixtures)).
- **The mapping builder edits expressions.** An entry can be an Expression input, its condition is an expression typed
  in one line, a repeat entry takes the condition its rows are kept by, and the modifier list offers `ref`; the builder
  checks each with the loader's own rules as the draft is composed, and writes them back as the tree reads them.

- **The sample estate translates Recall values with petrodb-api's own tables.** Its unit maps and curve dictionary were
  stand-ins written for the samples; they are now the tables petrodb-api applies, held as data rather than code. The
  curve unit map (78 entries), the depth and vertical unit map (8) and the curve dictionary (498 mnemonics) are CSV files
  in `samples/wells/cache/data/`, each loaded into an ingestion table by a pre and an ing flow of its own
  (`wells-units-01/02-curve`, `wells-units-01/02-depth`, `wells-curvedictionary-01/02`) and captured by the lookups
  cache flow as `RecallUnits`, `RecallDepthUnits` and `CurveDictionary`. Being static data, the files and the flows that
  load them sit in the source's cache folder beside the cache flows, and `flows/` holds only the flows of its data. petrodb-api matches a unit spelling ignoring
  case, so its spellings that differ only by case (`MPA` and `mpa`) are one row, as the tables' keys hold them. The well
  log mapping renders what petrodb-api renders from them: curve units through the curve map, depth and vertical units
  through the depth map, each resolved against the partition's units by `ID`, `Code`, then `Name`, and every curve's
  `LogCurveTypeID`, `LogCurveMainFamilyID` and `LogCurveFamilyID` from the dictionary by its mnemonic, which the reference
  cache flow now captures the two new types for. The sample cache records carry a record for every code the tables give.
  The dictionary document `RecallUnits.yaml` is gone from the samples; the dictionary form itself is unchanged.
- **A `findBy` on a cached type's `id` finds the record by the code its id ends with.** A reference to OSDU reference
  data is the partition, the entity type and a code (`dev:reference-data--LogCurveFamily:Gamma%20Ray:`), so a lookup
  table that names reference data by its code (a curve dictionary giving each mnemonic its family) finds the record it
  names, whether the code is written as the id encodes it or decoded. It applies to a type that caches no field of its own
  called `ID`, and keeps the rule every match keeps: case is ignored only when that finds exactly one record.

- **The Records page narrows to one flow.** A searchable flow picker beside the status filter lists every flow the
  synced repositories name that holds records (a source that delivers several interfaces offers one choice per
  interface, and an interface that has delivered nothing is not offered), with an interface's choice led by the
  interface so a source's choices never read alike (`GET /api/v1/delivery/records/flows`). Whether a flow holds a
  record is one index seek per flow, whatever the ledger's size. The choice narrows both the recency listing and a
  search, and travels in the URL with the term and the status (`flowId` on `GET /api/v1/delivery/records`). A search
  narrowed to one flow reads that flow's own identity tokens (migration `RecordIdentityFlowToken`, module version
  1.12.1: `IX_RecordIdentity_FlowId_Token`), so a prefix other flows share cannot use up the candidate bound before
  its records are reached; the recency listing of one flow reads the existing per-flow recency indexes.
- **The YAML editor documents and checks every OSDU Delivery document.** Delivery, retrieval and cache flows, mappings
  and dictionaries had the editor's analysis switched off, because SQLFlow's engine knew only its own flow kinds. The
  engine now takes a module's key census files (a generic extension point, `ca779a3`), and the module ships one for
  each of its documents (`osdu/docs/census`): hovering a key shows its documentation, allowed values and defaults, and
  an unknown key is flagged as the error the loader would raise. The pipeline, run, mapping and builder views turn the
  analysis on. `EditorCensusTests` keeps the census and the loaders in step, key for key.
- **A cached field holding OSDU record ids is written to a relationship.** OSDU's own translations
  (`ExternalUnitOfMeasure.UnitOfMeasureID`, `ExternalReferenceValueMapping.SimpleMap.ReferenceValueID`) cache the id of
  the platform record a source value stands for, and a mapping can now write that field as the reference. The gate
  refuses the mapping when a value the field holds is not an OSDU record id or names a record of an entity type the
  relationship does not allow, and warns about ids naming records the cache holds that type of and not that record; a
  render meeting one holds the record whatever `required` says. An id is written with the version colon added where
  the cached value leaves it out, and the record it names is recorded among the record's cache dependencies. The
  documents describe caching only the mappings OSDU marks identical
  ([osdu/docs/documents.md](osdu/docs/documents.md#record-ids-held-in-cached-fields)).
- **A replace reads its table from the partition's cache.** `replace: cache.<Type>` matches the incoming value on
  `match` (by default the table's key) and replaces it by the matched row's `field` (by default the one field a lookup
  table holds beside its key, `value` for a dictionary of pairs); `otherwise` works as for a written table. Any cached
  type is a table: a dictionary, an ingestion table, or OSDU reference data naming both fields. A row with nothing at
  `field` gives no value; a row holding several values there, rows that answer to the value only once case is ignored
  and give different values, a type the cache version does not hold, and fields the table cannot settle hold the
  record. Every row a replacement used is recorded with the record's cache dependencies, and so is a key a lookup table
  does not list, so a new version of the table that changes, empties, removes or comes to list an entry tags exactly
  the records built from it: a change a record read and found empty now reads as `changed` from no value, and a key a
  table comes to list as `listed`. A mapping that reads the cache only through a replace renders against the cache
  version, and lineage orders it after the flow that fills the table. The gate checks the table and its fields, and,
  where the replaced value is looked up in the cache, lists the values a replace (written or cached) can give that find
  nothing there. The mapping builder offers a replace as values listed in the mapping or a table in the cache, with the
  partition's lookup tables and their fields to pick from, and the builder's cache types carry a lookup table's key. The
  sample WellLog and WellboreTrajectory mappings translate units through `cache.RecallUnits`, and WellLog fills
  `LogCurveFamilyID` through `CurveClasses` and the reference cache's new `LogCurveFamily` type
  ([osdu/docs/documents.md](osdu/docs/documents.md#a-table-read-from-the-cache)).
- **A cache flow reads lookup tables out of ingestion tables.** A type with `table:` (a three-part name), `key:` (the
  column rows are keyed by) and `fields:` (columns, bare or `{ column, as }`) is read over the flow's
  `source.connection`, declared and checked as a delivery flow's is, so the same estate that loads a table through its
  pre-ingestion and ingestion flows can classify records by it. A refresh reads every row the ingestion flow has not
  marked deleted in one statement, keeps each value as the text the delivery reader gives it, trims keys, and refuses
  the capture, naming the rows, when a key is empty, over 256 characters or held by two rows, or when the table holds
  more than 100,000 rows. A plan counts the rows. Lineage orders the cache flow after the ingestion flow that loads its
  table. The sample estate lands a curve dictionary (`data/curve-dictionary`, `wells-curvedictionary-01-pre`,
  `wells-curvedictionary-02-ing`) and holds it as `CurveClasses` in `wells-lookups-00-cache`
  ([osdu/docs/documents.md](osdu/docs/documents.md#cache-flow)).
- **Dictionary documents: lookup tables kept in the repository and held in the partition cache.** A dictionary
  (`documentType: dictionary`, one table per file, `dictionaries/<name>.yaml`) is a dictionary of pairs, or gives each
  key the named values its `fields` list; every key and value is text exactly as written, and `~` is no value. A cache
  flow holds one with `types: [{ dictionary: <name> }]`, needing no endpoint when it holds only lookup tables, and a
  refresh reads the file at the run's commit (such a flow asks the node for the repository's tree) and captures it into
  the same version as the flow's other types, so an edited entry tags only the records it reaches. The repository sync
  records each dictionary type's key, fields and file, and leaves one whose file is missing or invalid out with a
  warning; lineage shows the file the cache flow reads; the proposal preflight checks a dictionary before it is pushed.
  The OSDU cache page shows where each type comes from, a lookup table's key, and how a mapping reads a lookup row. The
  sample estate holds `dictionaries/RecallUnits.yaml` through `cache/wells-lookups-00-cache.yaml`
  ([osdu/docs/documents.md](osdu/docs/documents.md#dictionary)).
- **The partition cache holds lookup tables beside OSDU records.** A cached type now has an origin: `osdu` (searched on
  the platform, as before), `table` (an ingestion table) or `dictionary` (a dictionary document in the repository). A
  lookup table's rows are kept under their keys as `lookup--<Name>`, and are versioned, traced and rolled out like every
  cached value. `[osdu].[CacheDefinition]` records each declaration's origin and that origin's settings (migration
  `LookupCacheTypes`, module version 1.12.0: `Origin`, `Connection`, `SourceObject`, `KeyField`, `DictionaryPath`, and
  `Endpoint` and `Kind` optional). A lookup table is declared by one cache flow of a partition and its capture replaces
  the whole type; a key is trimmed, not empty and at most 256 characters, and is never called `id`, since a lookup row's
  key is its id. `cache.<Type>.id` on a lookup table is refused by the gate and holds at render: its rows are not OSDU
  records, so it has no id to write. A refresh or a plan result names each type's origin and source
  ([osdu/docs/cache-lookups-plan.md](osdu/docs/cache-lookups-plan.md)).
- **A replace can give no value, and say what a value its table does not list becomes.** `NONE: ~` replaces with no
  value, so `required` decides, and `otherwise`, written beside the table, makes an unlisted value no value (`~`) or a
  fixed text instead of passing it on unchanged ([osdu/docs/documents.md](osdu/docs/documents.md#replace)). The mapping
  builder writes and reopens both.

- **Every capture of a partition's cache reads the partition's system properties, and a search asks regardless of case
  where the partition allows it.** A refresh, and a cache flow's plan, asks the indexer's and the search service's
  `GET /info` for the feature flags they report for the partition (`featureFlagStates`), and the refresh keeps them with
  the version (`[osdu].[CacheVersion].SystemPropertiesJson`, migration `CacheSystemProperties`, module version 1.11.0),
  tagged as system properties apart from the cached records ([osdu/docs/documents.md](osdu/docs/documents.md#cache-flow)).
  A service that cannot be asked fails nothing and changes nothing the cache knew of it; a property the engine relies on
  that no service reports is recorded as unknown, with the reason; a changed state writes a version and a failed read
  never does; an import keeps the current properties. Where the indexer reports `featureFlag.keywordLower.enabled` on,
  a search that finds no record exactly asks the `keywordLower` sub-field once, and takes its answer only when it is one
  record: an exact answer always wins, and several that match once case is ignored hold the record
  ([osdu/docs/mapping-templates.md](osdu/docs/mapping-templates.md#searches)). The render context of a mapping that
  searches pins the state of the properties its lookups rely on (`systemProperties`); a mapping that only searches pins
  them in place of a cache version, read from the version's row without its records, so a capture that changes
  reference data renders none of its records again. The OSDU cache page lists them on a System properties tab,
  `sqlflow cache list` prints them with the current version, and `sqlflow check` prints how a flow's searches are
  written.

- **A mapping can search the platform for the record a reference names, instead of reading it out of the cache.** A
  `searches:` block declares the kind searched and pins the saved template whose schema says how that kind is indexed,
  and an entry reads `search.<name>.id` found by `findBy` lines tried in order
  ([osdu/docs/mapping-templates.md](osdu/docs/mapping-templates.md#searches), decision
  [0009](osdu/docs/decisions/0009-searched-references.md)). Each lookup asks the search service, under the flow's own
  target, credentials and partition, for the one record whose property is exactly the value, with the query written the
  way the schema has the platform index the property. Exactly one record is the answer and none is a miss; several, a
  refused query, or a value that cannot be asked for hold the record whatever `required` says; a platform that cannot be
  asked fails the run. The render stays free of I/O: the plan asks a batch's questions once each and renders the
  waiting rows again, and answers are kept for the run. Fixtures declare the answers they assume and never search. The
  sample WellLog and WellboreTrajectory mappings find their wellbore by name and then by alias this way.
- **`SqlFlow.Delivery.Search` builds the query strings the OSDU search service receives, canonically.** Every rule is
  read from the indexer's and the search service's source: a string is asked through its `keyword` sub-field, a legacy
  `^srn` link and a value inside a flattened array through the property itself, a property of a nested array through
  the service's `nested(...)` form; and a value the index could never match (longer than the 256 characters the
  keyword keeps, the text `null` a property with no value is indexed as, a control character) or the service's own
  parser would misread (`nested(` anywhere; unbalanced parentheses or a word ending in AND, OR or NOT before a colon
  inside a nested query) is refused rather than sent.
- **The end-to-end suite runs a stand-in for the OSDU platform** (`osdu/gui/e2e/osdu-standin.mjs`), which answers a
  token and the wellbore searches the sample mappings make, and declares the OSDU references every process of the
  estate resolves, so the suite no longer depends on the shell that started it.

- **A central configuration the control plane supplies to the runs it queues.** `[osdu].[ConfigProperty]` (migration
  `CentralConfigProperties`, module version 1.10.0) holds a property for the whole control plane, or for one
  repository, which overrides it for that estate's flows. Every run of a delivery, cache or retrieval flow is queued
  carrying what its repository resolves to, and the node resolves `${env:NAME}` from that before its own environment,
  so an estate names where it delivers in one place instead of on every node. A reference the configuration does not
  name still comes from the node, so the two can be mixed. It is attached by decorating `IRunDispatcher`, which every
  run passes through, so a scheduled delivery and one someone pressed a button for resolve the same references. A
  property holds a non-secret value or a `${env:...}` or `${keyvault:...}` reference the node resolves, never a
  secret. `sqlflow config list | effective | set | remove`, and `GET/PUT/DELETE /api/v1/delivery/config`.

- **The OSDU flow kind owns where a record goes.** `dataPartition`, `aclOwner`, `aclViewer` and `legalTag` are
  properties of the kind, defaulting to `${env:OSDU_DATA_PARTITION}`, `${env:OSDU_ACL_OWNER}`,
  `${env:OSDU_ACL_VIEWER}` and `${env:OSDU_LEGAL_TAG}`, so a mapping declares what it fills and a flow no longer
  repeats them; a flow that names its own value under `render.parameters` still wins. The sample flows lost their
  `render.parameters` blocks entirely.

- **A record is found by what an operator holds, and its page shows the whole chain.** The ledger keeps an identity
  index (`osdu.RecordIdentity`, migration `RecordIdentityIndex`, module version 1.9.0): one row per value a record is
  known by, folded for comparison and kept as written for display, covering the columns a mapping declares as
  identities (`dataset.identity`), the source key and its columns, the words of the label, the OSDU id and its own
  part, and the ingestion file. One seek answers any of them across every flow, and each hit says which value matched
  and what it is. A staging rewrites a record's rows as a set, so a record is found by what it is now; records held
  before the index existed are filled in by a background pass.
- **The record's journey spans pre-ingestion, ingestion and OSDU.** `GET
  /api/v1/delivery/records/{flowId}/{key}/chain` names the runs that handled the record's ingestion file, read from
  the platform's own record of processed files, so the milestone strip and the timeline show where a row is in the
  whole estate rather than only in the delivery ledger. A file no run recorded says so instead of showing a blank.
- **Records** (Operate) in the GUI: a record is found from what an operator holds (a source key, a label, an OSDU
  id, a delivery key or an ingestion file name) across every flow, narrowed by status, without knowing which flow
  delivered it; the Delivery page carries the same field. The API behind it is
  `GET /api/v1/delivery/records?search=&status=`, the ledger's indexed lookup that the combined search already read.
- **The Records page opens on what the delivery system last took in or sent.** With nothing typed, the same route
  lists the ledger's most recently updated records across every flow, newest first, refreshed every ten seconds, and
  a status narrows that listing as it narrows a search; a row opens the record's journey exactly as a hit does. It is
  read from the end of a recency index (migration `RecentRecordsIndex`, module version 1.9.1: `IX_Record_UpdatedUtc`,
  and `IX_Record_Status_UpdatedUtc` for one state), so it costs what it shows rather than what the ledger holds, and
  it reaches back no further than the candidate bound the lookup already used.
- A record's page opens on its **journey**: a strip answering when the row was received (with the ingestion file
  and row), when it was planned, how many dispatches and how many failed, when it landed and as which version, what
  the last verify found and whether it was removed, over a timeline of every dated fact the ledger holds, oldest
  first, with every dispatch's phase, duration, worker, run, submission and origin, and every intervention with
  who asked for it. The header now names the file and row the record was received from.
- A submission's page undoes the batch it ran: **Remove what it delivered from OSDU** is one removal aimed at
  exactly the records that submission delivered, through the same removal dialog, with the count read the way the
  removal resolves it. The records filter gained `deliveredBy` for that set (`?delivered=` on a flow's Records
  tab), beside `submissionId`, which names the records a submission last planned and which a later submission
  moves on; the submission's page links to both sets.
- The repository shape: SQLFlow vendored under `sqlflow/` as a squashed git subtree, everything OSDU Delivery adds
  under `osdu/`, and `OsduDelivery.sln` building both. `tools/check-vendored-sqlflow.sh` names the SQLFlow commit
  `sqlflow/` was vendored from, lists every file changed here since, and fails when a commit mixes `sqlflow/` with
  other paths or when a line added to `sqlflow/` mentions OSDU or the delivery module.
- The OSDU module copied from the previous implementation into `osdu/`: the delivery, retrieval and cache flow
  kinds, the ledger, the protocols, rendering, templates and the mapping builder, the OSDU cache, the delivery and
  template endpoints, the `check`, `cache` and `template` CLI verbs, the GUI pages and their e2e specs, the sample
  estate, and the domain suites.
- `osdu/src/SqlFlow.Delivery.Data`: the module's EF Core context over the dedicated `osdu` schema, with its own
  migration history and schema version, so the ledger can be upgraded in production without touching SQLFlow's
  catalog.
- Three container images, all built from the repository root because the hosts span `osdu/` and `sqlflow/` and the
  GUI compiles the vendored SQLFlow sources in place: the control plane, a compute node and the GUI
  (`osdu/deploy/docker`).
- The deployment estate under `osdu/deploy`: Azure Container Apps via Bicep (the full estate, one template per
  tier, and the Entra app registration users sign in with), a docker compose stack, and Kubernetes manifests with
  a KEDA-scaled node pool. Nodes take work from the control plane's dispatcher with a node-scoped personal access
  token and open no catalog connection.
- `deploy-prod.bat` and `deploy-prod.ps1`: prod container deploys that build from a git archive of tracked files,
  verify each build by its ACR run id, deploy only what changed since the tag each app serves, and roll back an
  app that does not come up serving the new tag.
- Local development: `dev.bat` brings up the GUI and the control plane against a real estate after applying
  pending SQLFlow and OSDU migrations, and `osdu/tools/dev-setup.ps1` generates the git-ignored `.sqlflow/env` it
  reads from the live container app secrets.
- Continuous integration: the vendored-SQLFlow guard; `OsduDelivery.sln` built with warnings as errors (the SSH.NET
  advisory in vendored SQLFlow excepted until SQLFlow takes the fix) and every suite run against a SQL Server
  container; a check that the OSDU migrations write only to the `osdu` schema and SQLFlow's never touch it; both GUI
  trees built, the OSDU GUI linted and its end-to-end suite run; and the three images built, with the control plane
  and GUI images started and checked.
- Documentation under `osdu/docs`: the architecture of the module on SQLFlow, the environment and secret contract,
  and the OSDU-specific reference pages beside the vendored SQLFlow's own generic documentation.
- The OSDU Delivery hosts: the control plane, the worker node and the CLI, each composing SQLFlow with the module,
  which serves its delivery surface, CLI verbs and flow kinds over its own database.
- Delivery from ingestion tables: a delivery flow reads its records from keyed ingestion tables (a window, keyset
  pages by the table's identity key, child datasets, key slices and the ingestion fingerprint), fans out over the
  platform's run groups and keeps a watermark per flow scope. Each flow keeps a ledger of its own, so one ingestion
  table can feed several OSDU flows, and a source's interfaces are delivered in the waves their references wait for.
  The sample estate carries the pre-ingestion and ingestion flows that fill its tables.
- Routes to every OSDU ingestion path and DDMS, built from the services' pinned OpenAPI contracts and source code
  (`docs/osdu-coverage-plan.md`): storage, file, manifest (inline and by reference), dataset and the ingestion
  workflows; the Wellbore DDMS's nine collections through one `ddms` route; Well Delivery, Rock and Fluid Samples,
  the Production DDMS historian, Seismic Store and Reservoir Management as shapes of that route; Production DDMS
  business objects through `dspdm`; and checks of the records External Data Services reads. The suites check every
  request a route sends against its service's contract.
- OSDU flows in lineage, with the OSDU types, cache types and files on both sides, and each record's origin served
  from the ledger.
- Delivery metrics on the meter `SqlFlow.Delivery`: settled tries per flow, route and outcome, and every HTTP call
  attempt with its result, duration and retries (`osdu/docs/operations.md`).
- The sample estate covers three route types, each decided by what an interface declares: the source document now
  delivers documents through the file service and directional surveys with their stations through the Wellbore DDMS,
  beside the wellbores and well logs it already carried. Their schemas are the real ones, captured from the OSDU data
  definitions; `sqlflow template capture --out <file>` writes the bundled schema beside the mapping that pins it, so a
  repository carries what it pins and can import it again without the network.
- `osdu/docs/test-matrix.md`: for every route type, DDMS shape and engine area, the suite that proves it, what that
  proof rests on (a fake built from the service's own contract, or a real SQL Server), and what no suite proves.
- Every view of a source is about one of its interfaces (`osdu/docs/operations.md`): the GUI carries an interface
  picker whose choice travels in the URL, a source's Delivery tab lists its interfaces in the order a run takes them
  with each one's route, what it waits for and its counts, and the probe, the release and every removal act on the
  interface that is showing. A multi-interface source's Records and Submissions tabs could not be opened before this,
  because the API asks which interface a request is about and the GUI never said. The trigger dialog names the
  interfaces a run takes.
- `sqlflow records list` and `sqlflow records show`: an interface's records from the ledger, and one record with every
  try it took, its steps and its errors, from a terminal or a node with no control plane to reach. The source key finds
  a record as surely as the delivery key.
- The `etp` route, which writes Energistics data objects into dataspaces of the Reservoir DDMS over ETP 1.2 on a
  WebSocket instead of through an OSDU service (`osdu/docs/documents.md`, `osdu/docs/protocols.md`). The client is the
  module's own: the messages and data types it uses are C# records written from the pinned protocol, and a test
  round-trips every one of them against a codec driven by that same file. An object's identity is read out of its own
  XML and checked before a session opens, a dataspace is created only when it is missing and with the record's own ACLs
  and legal tags, a batch's objects and arrays go inside one transaction per dataspace, an array too large for a message
  is declared and then filled slice by slice, and a refused commit is rolled back. No dataspace is ever deleted, because
  the server purges its OSDU record when one is.
- Records that wait for records: a record whose document refers to a record the ledger holds and has not delivered is
  left waiting by the claim, which charges nothing, and goes out when that record lands. Waits are decided under one
  lock of the ledger and never lead back to the record deciding, so two records never wait for each other; an operator
  can send one as it is. `target.verifyReferences: storage` asks OSDU's storage service about the ids the ledger does
  not hold and holds a record that would write a dangling reference.
- `docs/go-live-map.md`, the checklist from here to production, and an inventory of every id a live OSDU test creates,
  with the rule that no live test runs without approval (`CLAUDE.md`).
- A scheduled target probe: every active delivery flow's OSDU is asked whether it still answers, once per interface,
  through the same node operation the operator's "Probe target" queues. Each probe is an activity of kind `probe` in
  the audit trail and a count on `osdu_delivery.probes`; a pass settles what an earlier pass or an earlier life of the
  host left open, so no probe stays open and a restart loses nothing. Off unless `Osdu:TargetProbe:Enabled` says
  otherwise, because a pass costs a token exchange and a live request per interface.
- `sqlflow records release`: an operator on a node releases a flow's held, failed and removal-marked records back to
  pending, the whole interface or the keys given, without a control plane to reach.
- What one control plane replica costs and how an operator recovers from its absence, and the ledger's retention and
  backup policy: what every table of the `osdu` schema holds, what grows, what may be pruned and what never may
  (`osdu/docs/operations.md`, `osdu/docs/decisions/0005-ledger-retention.md`). The retention pass now clears the
  captured run log of settled activities as well as aging out attempts, and answers both counts.
- Generic extension points in the vendored SQLFlow, each in a `sqlflow:` commit: a registered flow kind describes its
  files and datasets in lineage (anchored at the flow's folder, bounded to the catalog's widths, and swept once no
  declaration names them), a flow's file selection is read from its stored definition, a search contributor can
  assert the result contract, and a link can set the pipelines page's repo and kind filters.

### Changed

- **A mapping's Properties show what needs a look, and little else.** The row of switches is one view control, Filled,
  Missing, Unfilled, Failing (after a data check) and All, each with how many attributes it holds; Required, Minted and
  Nested wait under View. A row of the tree is marked only for a finding, a partial fill, an attribute nothing fills or
  failing rows, never for one that is fine. The selected attribute reads in three tabs (Filled by, Data, Schema), the
  data check sits on one line with its summary under it, and the explanations moved into hovers. The sheet's header is
  two lines (the mapping with its kind and file, then the tabs with the template it pins and Open in builder), and the
  tree and the properties take the height that leaves.
- **A value its modifiers turn into nothing says so.** A required entry whose column holds a value that a `replace` to
  `~` or a `split` with too few parts turns into nothing is held with `dataset.unit is 'NONE', which its modifiers
  (replace(NONE: ~, M: m)) turn into no value`, where it said the column was empty.
- **The cache page asks four questions, a tab each.** Records (what the cache holds), History (how it changed, was
  Versions), Deliveries (what it means for the records already in OSDU) and Setup (how it is filled). Deliveries holds
  the changes being carried out to delivered records (was Changes) and what delivered records were built without (was
  Built without, now Missing from cache), and says that with `onChange: auto` neither needs anyone: the next delivery
  carries the change. Setup (was Definition and OSDU feature flags) is a navigator rather than one long page: a tree of
  the cache flows with the types each declares, and the partition's feature flags and how a mapping reads the cache,
  beside the one part picked, opening on a few lines about the partition whose every name opens its part. A type's part
  holds everything about it, down to the mapping entry that reads it, and opens it on Records or History. Each tab says
  what it is for on hover of its name, each section on its info mark, and a link to an earlier tab lands on the tab
  that holds it now, scrolled to its section. The cache streams API names the new tabs in its notices' `action`
  (`history`, `deliveries`).

- **The cache page's header and tabs are quieter.** The header says on one line which partition it is and which cache
  flow files fill it, by file name with each path on hover, and puts what a cache flow is and how to change what it
  holds behind an info mark; Cache files and Refresh sit at its top right, where every page header keeps its actions
  now however long its subtitle runs. No tab carries a count any more: the cache page's Versions, Changes and OSDU
  feature flags, a record preview's Steps, Payload and References, and a template comparison's Changes are named for
  what they hold, which each says once it is open, and what waits for approval is in the cache page's summary and
  banner.

- **The OSDU cache page's records read one type at a time, and its System properties tab is OSDU feature flags.** The
  records over every type folded each record's values into one column of name and value pairs, which read as noise.
  The types are now listed beside the records by family, with their counts, and every table reads one type in its own
  columns: the record's identity first (the part of its OSDU id after the partition and entity type, stated once in the
  type's header, or a lookup table's key), then one column per captured name, sized to its values. A value that only
  repeats the identity or a value to its left is muted, and an id's `%20`-style escapes step back, so what is new in a
  row stands out. All types shows each type as a section of its first records that opens the type's own table. A search
  counts the matches in every type in the list, keeps a section only for the types with matches, and marks each match.
  The tab that listed the partition's system properties did not say whose settings they were; it is now OSDU feature
  flags, and says they are set on the OSDU platform and never changed by OSDU Delivery. It gives one block per service
  with the endpoint read, each flag's state as an on or off chip, where the service says it was set, and the version
  whose refresh found it so, and marks the flag OSDU Delivery reads with what it changes. A `?tab=system` link still
  opens it ([osdu/docs/operations.md](osdu/docs/operations.md)).
- **A record's Document and Compare tabs are one tab, Render.** The Document tab showed the ledger's bookkeeping about
  a document it could not show: where a waiting document sat in its work batch (already on the situation line), three
  hashes (already on each dispatch in the timeline), the ids OSDU returned (already in the header and on the
  timeline's dispatches), and the submission's whole render context (the same for every record of it, and on the
  submission's page). Render builds the record's output from the data available now: its manifest, from the current
  source row with the flow's mapping and the cache, on a node and sending nothing, beside what OSDU holds or whole.
  Only the manifest is built, not the DDMS sections a DDMS route sends besides it, and the tab says so. The mapping and
  cache version that rendered OSDU's copy sit beside those the render used, flagged where either changed, with what the
  next run would do and why a delivery would hold the record. The steps an earlier try of a waiting delivery completed
  move to the situation line. `?tab=document`, `?tab=compare` and `?tab=context` open the Render tab. The In OSDU tab
  is named OSDU, after the place the record is read from, as Source is. The References tab is gone: the ids a waiting
  document refers to were empty for every delivered record, and the one that matters, the record it waits for, is on
  the situation line already; the records waiting for this one become a situation line of their own, shown only when
  there are any, and `?tab=references` opens the timeline. The OSDU tab's **Open in a window** opened the whole
  record page again in a pop-up; it now opens the OSDU explorer alone (`/delivery/records/{flowId}/{key}/osdu`), the
  inspector filling a window with nothing of the workbench around it, read as the window opens.

- **A record's timeline shows the changes of its row, not the pipeline's runs.** It listed every run that had processed
  a file of the record's file name, so a file landed again every hour filled it with runs that changed nothing (a real
  record showed 41 entries, 36 of them re-landings after its only change). It now lists what happened to the record:
  its row's arrival in the ingestion table with the landing that brought its file in, every later change of the row and
  its deletion, when it entered the ledger and what the ledger decided, every operation against OSDU, and every
  intervention. The ingestion flow restamps a row only when it changes, so each distinct stamp the ledger recorded is
  one change; a run appears only as the evidence of a change: the ingestion run that was writing the table when the
  row was stamped, and the last landing of its file before that run, each named only when the catalog proves it.
  `GET /api/v1/delivery/records/{flowId}/{key}/chain` returns the table, the row's arrival and its changes, newest
  first, each with those runs; the ingestion run is found with one index seek per flow writing the table. The
  milestone strip reads from file, loaded, last change, in OSDU and verified, and the timeline narrows to the source
  changes, to what was done against OSDU, or to the interventions.

- **A mapping is laid out the way the record it renders is.** The flat `mappings:` list, where every entry named its
  variable by a `target` path (`osdu.data.Curves[].CurveUnit`) and read with `source`, `static` and `appliesWhen`, is
  replaced by a `record` tree: `acl`, `legal`, `tags` and `data`, and below them each property at the place the record
  has it, so reading the mapping reads the record. Every word of the mapping language starts with `$` (`$from`,
  `$value`, `$cache`, `$search`, `$findBy`, `$modifiers`, `$when`, `$required`, `$forEach`, `$item`...) and every other
  key is a property of the record, so no template's property name and no column can collide with the language; a
  property whose own name starts with `$` is written `$$name`, a map mixing the language with properties is refused,
  and a misspelled word is refused with the one it most likely meant (`$form` is `$from`). Columns are named as they
  are: a bare name reads the row a node is in (under `$forEach`, the item's row), and `$dataset.<column>` the dataset's
  own row; `dataset.key`, `dataset.identity` and the label's `{column}` tokens name columns bare. Tokens carry the
  marker too: a literal reads `{$param.name}`, an id template `{$value}`, `{<column>}`, `{$dataset.<column>}`,
  `{$cache.<Type>.<field>}` and `{$param.name}`, and a replace names its cached table `replace: $cache.<Type>`. A token
  of the old vocabulary written without its marker is refused with the token it meant, rather than written into a
  record as text. A fixture's source row is `row`. Messages name a node by where the document writes it
  (`record.data.Curves.$item.CurveUnit`). The tree is read into the same model the renderer, the preflight, coverage
  and lineage read, so a converted mapping renders the same records: its content hash changes, so each record renders
  once more, and the rendered hash does not, so `onUnchanged: skip` sends nothing again. The mapping builder writes the
  tree from its draft, placing each entry by its variable and leaving out, with a comment and a check that names it, an
  entry the tree has no place for; the GUI's hints, the cache page's copyable snippets and the editor census speak the
  new vocabulary. Every mapping in the samples and the test fixtures is converted
  ([osdu/docs/mapping-templates.md](osdu/docs/mapping-templates.md), [osdu/docs/documents.md](osdu/docs/documents.md#mapping)).
- **The GUI end-to-end suite runs the recall estate.** Its fixture repository is the sample `osdu/samples/recall` (the
  well log chain, the lookup tables and the delivery flow) with the suites' fixture wellbore flows, interfaces source,
  retrieval flow and mappings beside it, the estate the control plane suites compose; it copied `samples/wells` before,
  which no longer exists, so the suite had failed in its global setup. The seed saves the four templates, imports the
  fixture reference records (`osdu/tests/SqlFlow.Delivery.Tests/Fixtures/cache-records`) for
  `fixtures-osdu-00-reference-cache`, runs the recall chain into its `arc` tables and refreshes `recall-lookups-00-cache`.
  The OSDU stand-in holds the wellbores of the five Recall logs under the ids the delivery suites give them, and the
  record trace finds a Recall log by its source key, its wellbore name and its log id. A local `OsduDeliveryE2E` seeded
  before this change still holds the old estate's ingestion rows and cache versions, which fail the cache and records
  specs: drop it once, and the next run creates it again.
- **The docs describe the recall sample.** The READMEs, the operations guide, the CLI references and the notifications
  guide name the recall flows and its lookups cache, the test matrix describes the recall estate with the fixtures
  beside it, and the walkthrough of how a WellLog 1.4.0 record is filled is written against the recall mapping, its data
  and its first fixture.
- **A key written twice in one map is refused.** The document loader read the last of two identical keys and dropped the
  first without a word, which in a mapping's record tree would lose a property; every delivery, retrieval and cache
  flow and every mapping now fails to load, naming the line, instead.
- **The ledger has one write path.** Staging pending records, appending and applying a lease's events, and marking
  and releasing waits each had an entity path beside the set-based SQL Server statements, for a provider no deployment
  runs on; the entity paths are gone, and so are the model built per provider (the binary collation on OSDU ids always
  applies) and the provider switch in comparing cache versions. The model on SQL Server is unchanged, so there is no
  migration, and a new test fails whenever the model and its last migration differ.
- **The module's suites run on SQL Server, the only provider the module supports.** The ledger, store and engine suites
  ran on an in-memory SQLite copy of the module's database, which proved nothing about the provider production runs
  on. Every suite now runs on SQL Server, in one database: `OsduDeliveryTests` on `localhost` under Windows
  authentication (created when missing), or the one `SQLFLOW_TEST_DB` names. The tests that use it form one xUnit
  collection that runs a test at a time and holds the database against every other test process through a session
  lock; a test empties the module's schema when it takes the database, so the chain suite no longer finds the claims a
  run whose process died left on OSDU ids. The few tests that need a second database (a migration from an empty schema,
  a database without snapshot isolation, a catalog kept apart from the module) share `<database>_scratch`, created when
  one of them starts and dropped when it ends. A server that does not answer fails the suites; none skips any more. The
  SQLite package is gone from the solution. The e2e suite keeps its catalog, the module's schema and the sample tables
  in one database, `OsduDeliveryE2E`, which every run reuses, and writes its fixture repository to a folder named after
  that database, so a development estate synced from a fixture repository of its own is never repointed by a run.
- **Inline replace tables are matched by the cache's own rules.** One matcher serves inline and cached tables: an exact
  key wins, and case is ignored only when that finds one key. A value several keys answer to only once case is ignored
  now holds the record, naming them, unless they all give the same value; before, it passed on unchanged. Keys that are
  the same once trimmed, or empty once trimmed, are refused when the mapping is read.
- **`sqlflow cache import` accepts only OSDU records of the declared entity type in the flow's partition.** Every
  imported id has to be `<partition>:<entityType>:<code>`, so an import cannot pass off hand-made rows as the
  platform's, and a lookup table is never imported.

- **The sample cache flow no longer captures wellbores.** They were 163,818 of the 165,381 records the `dev` cache held,
  and they are what the sample mappings now search for. The next refresh after the sync drops the type from the
  partition's cache, as it drops any type no synced flow declares; `osdu/samples/cache-records/Wellbore.json` is gone,
  since an import refuses a file for a type the flow does not declare.
- **A delivery flow's lineage reads the kind its mapping searches**, so the flow that delivers those records to the
  partition is ordered before it, as the cache flow used to order it. A search source's `findBy` lines no longer
  count as reads of a cache type of the same name.
- **The mapping builder carries searches, search entries, fixture answers and `dataset.identity` through a round
  trip.** A mapping opened in the builder and written back kept none of them before: a search source came back as a
  cache source, and a mapping's identity columns were dropped. The GUI edits a search entry beside a cache entry.

- **The cache captures every field a reference record is named by, and a lookup matches on all of them.** A
  reference-data record is named by its code, its name or its own `ID`, and a source may use any of the three. The
  cache flow captured `data.ID` for `UnitOfMeasure` alone, and no mapping matched on it; the other three reference
  types captured only `Code` and `Name`. All four now capture `data.Code`, `data.Name` and `data.ID`, which is the
  same set the working implementation's loaders request, and every `findBy` offers all of them as alternatives,
  except where a partition carries no `data.ID` at all: a capture reported that of `LogCurveBusinessValue` and
  `VerticalMeasurementType`, so those two declare the code and the name alone rather than an empty path that would
  be reported on every nightly run. A
  wellbore is matched by `FacilityName` or by any of its aliases, which is what the `Alias` field was captured for
  and what nothing had used. A source spelling a unit `m`, `metre` or by its id now resolves the same record
  instead of holding the row.

- **The sample cache flow reads a wellbore's aliases from the property the schema actually has.** It declared
  `data.NameAlias.AliasName`, and `master-data--Wellbore` has no `NameAlias`: the property is `NameAliases`, an array
  of `AbstractAliasNames` whose items carry `AliasName`. Nothing was ever cached under `Alias`, which a capture
  reported and which nothing yet read, so it cost nothing until the first lookup by alias failed to match for no
  visible reason. Corrected in the sample estate and in the four documents that taught the path.

- **The sample estate is written for the `dev` partition.** It used `opendes`, OSDU's own documented example
  partition, which read as a real destination in a repository whose estates deliver to `dev`. Every occurrence
  outside `osdu/specs` is now `dev`: the mapping fixtures and the records they pin, the sample cache records, the
  suites, the docs and the walkthroughs. `osdu/specs` keeps `opendes` because those are OSDU's own OpenAPI documents
  and integration briefs, and this project verifies platform behaviour against them as the vendor wrote them.
  The sample cache records now carry `dev:` ids, which makes one mistake easier to make and so louder to find: they
  are made-up records, and importing them into an estate that delivers for real puts ids into its cache the platform
  may never have held. Their README says so first.

- **A cache is keyed by the partition a flow reaches, not by the text its document spells it with.** An estate whose
  documents name their partition `${env:OSDU_DATA_PARTITION}` was keying its cache by that literal string: a capture
  under the real partition could never be read, two estates sharing a variable name but delivering to different
  partitions would have shared one cache, and two documents naming one partition differently would each have needed
  their own copy of identical reference data. The partition is now resolved wherever a cache is keyed: the render's
  read, a refresh's capture, an offline `sqlflow cache import`, and the catalog sync that records a partition's
  declarations. The sync resolves once per document and keys everything from that, and a control plane that cannot
  resolve a reference records it as written rather than failing the sync.

- **The sample estate stops asking for an API Management key.** Its flows sent
  `Ocp-Apim-Subscription-Key: ${env:APIM_KEY}` on every request, which is an Azure API Management gateway key and
  nothing to do with OSDU: a platform reached directly takes a bearer token and no such header. The estate demanded a
  credential its target does not use, so a run failed resolving a reference no one could supply. The header is gone
  from the flows, the cache flow, the deploy templates and the docs. Redaction still covers it: a flow that does sit
  behind a gateway and sets the header keeps it out of logs and off redirects, which the platform's own suites assert.

- **An estate names where it delivers, rather than writing it into its documents.** The partition a flow delivers
  to (`target.headers.data-partition-id` and the `dataPartition` render parameter), the entitlements groups every
  record is owned and readable by, and the legal tag it carries are all `${env:...}` references the node holds:
  `OSDU_DATA_PARTITION`, `OSDU_ACL_OWNER`, `OSDU_ACL_VIEWER` and `OSDU_LEGAL_TAG`, beside the `OSDU_*` references
  already there. The sample mappings declare `aclOwner`, `aclViewer` and `legalTag` and fill `osdu.acl.owners`,
  `osdu.acl.viewers` and `osdu.legal.legaltags` from them through `{param....}`, so one set of documents serves
  every partition an estate runs against. A flow's own render parameters are resolved before the render context is
  built, so the context, the rendered hash and the ledger row hold the value that reached the record, never the
  reference; a mapping's own default stays literal. A cache is still scoped by the partition its flows declare, as
  written, which is how lineage already identifies a platform, so a cache flow and the delivery flows reading its
  cache agree by naming the partition the same way. Deployments wire the four new references (compose, k8s), and a
  node that has not gained them fails the run naming the one it is missing.

- **The sample estate names its OSDU endpoint `${env:OSDU_URL}`.** The reference was `${env:PETRODB_URL}`, named
  after the facade an earlier estate delivered through, which read as a dependency the module does not have: the
  variable holds the OSDU API base a flow's `target.endpoint` (or a cache or retrieval flow's `source.endpoint`)
  resolves, and it now sits with the `OSDU_*` references beside it. A deployment renames the variable on its nodes
  when it takes this build; a flow document that still says `${env:PETRODB_URL}` resolves whatever a node holds
  under that name, so the two can be moved separately.

- The current build has been run against a live OSDU: Azure Data Manager for Energy 0.29, partition `dev`, on
  2026-09-17. The storage, file, manifest and ddms routes each delivered and were read back, verify and reconcile were
  exercised, and every id created was removed at the reversible scope with a GET answering 404
  (`osdu/docs/osdu-testing.md` section 0). Six defects it found are fixed: a cache capture now ends a reference type
  when its pages stop bringing anything new (this deployment hands back the same search cursor for every page); a bulk
  chunk whose pandas entry no dataframe reader can read is held before it is sent, and the sample estate and every
  fixture write the whole entry; a session whose chunks carry the reference curve as the row index rather than as a
  column is held before the session is opened, because the service accepts every chunk and then refuses the commit; and
  the sample source's wellbores and welllogs interfaces declare the child datasets their mappings repeat, with a suite
  that reads every committed delivery document and checks it against the mapping it names.

- Data reaches OSDU through SQLFlow's own flows: a pre-ingestion flow lands the source files, an ingestion flow
  loads the keyed ingestion tables, and the OSDU flow reads those tables and delivers. Lineage orders the three.
- Image, Container App and Kubernetes resource names carry the product (`osdu-delivery-*`), because the vendored
  SQLFlow ships its own `sqlflow-*` images from its own deployment assets and the two are different artifacts.
  Project, namespace, binary and environment-variable names stay `SqlFlow.*` and `SQLFLOW_*`.
- The ledger's concurrency: a worker keeps its writes off the record table with one lease row and an event log, so
  many nodes write the ledger without locking each other out, and the cache tables are clustered by partition, so
  partitions refreshing together never deadlock.
- A delivery no longer refuses a flow whose record key column is declared nullable, which every table SQLFlow's
  ingestion creates is; opening the source probes the run's own scope for records whose key is actually unknown.
- The production deploy scripts name the estate they deploy to instead of carrying one.
- The repository is licensed under GPLv3, matching the SQLFlow it is built on.

### Removed

- The previous implementation's drop path: the drop manifest and its encodings, the drop reader, the scope file
  readers, the replica, the SQL source extraction, inline drops, known-state publishing and the drop-off area,
  together with their tests, documents and deployment settings. SQLFlow's pre-ingestion and ingestion flows
  replace them.
- Manual submission: records reach OSDU only through the regular flows, and records delivered by hand are files
  placed where a pre-ingestion flow reads them. The SQLFlow extension point it alone used, a run group enqueued
  together with rows of its own, went with it.

### Fixed

- **A warning that does not hold reads as a warning.** An assertion of severity `warning` or `info` that did not hold was
  shown as a failure everywhere a single assertion shows: a red cross, red found and failing counts, a red "hold" count
  on the report, the board and the test's sheet, and a red square in its history, so a run that passed with one warned
  test read all red. Each now wears its severity's tone (amber for a warning, blue for an info, noted), the "hold" counts
  take the tone of the heaviest assertion that does not hold, and the HTML report badges them the same way.
- **A test of bulk data reads the Wellbore DDMS's "NaN" as a gap.** The DDMS writes a missing value of a float curve as
  the text `NaN`, which a condition on the column took for a string and failed ("is a string (NaN), which does not
  order against the number 0"), so an `optional` range over a curve with gaps failed every log that had one. It is read
  as no value now, as a JSON null always was.
- **A test groups a text property by its keyword sub-field.** The search refuses to aggregate text, so a `groupBy` on a
  property of `data` the schema indexes as text errored; it asks for the `.keyword` sub-field instead.
- **A refresh no longer tags delivered records whose cached values did not move.** A cached record is asked about as
  a whole when any of its values moves, and each set was judged only against the new version, so a path that read the
  same before and after was told as a change whenever the set held something else: a change someone had rejected was
  raised again (as the same value to the same value) when another field of the record moved, a change waiting for a
  decision was rewritten as found in the later version, and a path holding an empty set was told as changed to `[]`. A
  set is now judged only where the refresh moved what it reads. A change whose path or item is longer than the ledger
  keeps is also found again instead of raised as a new one on every refresh.

- **An incremental run reads a record row marked deleted.** SQLFlow's key match stamps `DeletedDate_DW` without
  touching `UpdatedDate_DW`, and the incremental read windowed on the update column alone, so a deleted row was held
  only by a full or keyed run and otherwise stayed delivered with nothing on its history. The record table's delete
  column now joins the window, as a child dataset's already did.
- **The control plane suites expect the recall estate.** The module database tests expect the two mappings a copy of
  the sample estate holds (the recall well log mapping and the wellbore fixture beside it), and the mapping builder test
  expects the recall mapping as it is: a unit translated through `$cache.RecallUnits` and then made a reference, a curve
  family built by an `id` template from the curve dictionary, the three vertical measurement types the sample records
  hold, `recall` as the system in the record's shape, and a check without a partition that names the unit table the
  empty cache does not hold. Its hand-made wrong draft drops the fixtures' shared parameters with the fixtures.
- **Coverage counts what a literal writes.** A literal object or list fills the properties it holds, so the sample's
  `TechnicalAssurances` list no longer shows its `TechnicalAssuranceTypeID` as a required property nothing fills, and
  Show missing answers that the recall mapping satisfies the WellLog schema. A property only some items of a literal
  list carry is filled on some rows.

- **The Records flow picker no longer crashes the control plane when an estate holds many flows.** It asked which
  flows hold records with one query per flow joined by `UNION`, which nested as deep as the flow list was long, and
  the SQL Server provider's query translation overflowed the stack walking it, taking the host down. It is one
  statement now, an `EXISTS` per flow over the list passed as JSON, for any number of flows. Moving the suites to SQL
  Server found it: the in-memory stand-in never ran that translation.
- **An interface that names the etp route is delivered by it.** `route: etp` on an interface fell through to the route
  its payloads implied, so an Energistics object was sent to the storage service (or the file service, or a DDMS) and
  refused there. It now goes by the etp route, with its XML and its arrays as optional parts, as the single form does.
- **A source's `target.etp` is refused when no interface takes the etp route**, as its `target.dspdm` is when no
  interface takes the dspdm route. It was dropped without a word.
- **`change.detect` and `change.payloadDetect` each take their own name for comparing hashes.** `detect: contentHash`
  and `payloadDetect: renderedHash` were accepted and behaved like the other key's name, while the documentation and
  the editor named one value each; the loader now refuses the other key's name and says which to write.
- **`reliability.renderParallelism: 0` is documented as the code sets it**: half the machine's processors, at least one,
  not every core.
- **The YAML editor's hover heads a key with its type as `key`: `type`**, without the em dash SQLFlow's engine wrote
  there (a SQLFlow fix, recorded in `docs/sqlflow-changes.md`).
- **The mapping builder checks a synced mapping with the values a run would use.** Since the flow kind took over
  `dataPartition`, `aclOwner`, `aclViewer` and `legalTag`, the sample flows no longer write them, and the builder
  prefilled its check values only from what a flow writes: every synced mapping opened with those four empty and failed
  its check. The builder now fills them as the render resolver does, from the kind's references resolved through the
  repository's central configuration and then the control plane's environment, says which reference each value came
  from, and names a reference only the nodes can resolve so the author gives a value in its place.
- **A flow run from the CLI into a registered repository keeps its folder on the Pipelines page.** A run recorded
  with `--db` and `--repo` took the flow file's folder as the repository root, so the pipeline moved to the repository's
  top level, and the repo's root with it, until the next sync. It now records the path the sync gives the flow (SQLFlow
  `0ed0bda`).
- **A dictionary type in a cache flow refuses a `key`.** The dictionary document names its own key, and a `key` written
  beside `dictionary:` was dropped without a word; the loader now refuses it, as it refuses `kind`, `query` and
  `fields` there.
- **A record id is looked for by the id, even on a type that caches a field called `ID`.** OSDU reference data caches
  its own `data.ID` (the sample's `UnitOfMeasure` does), and a record-id lookup read that field instead: a static
  reference to a cached unit was refused by the gate as not in the cache, a value that already was a unit's id was
  written without the dependency on the unit it named, and a record that wrote a unit's id was tagged as changed (from
  the id to the unit's `ID`) whenever anything else about the unit moved. The id now answers by the record id, and a
  usage of a record's own id reads the same as long as the record is there.
- **A ledger read the database chose as a deadlock victim is read again.** Without snapshot isolation a read holds
  shared locks while it runs, so a claim or a lease recovery on one node could fail with a deadlock against another
  node's claim. Every ledger read now retries a deadlock a few times with a growing, jittered pause, as its writes
  already did; the two concurrent chain suites that met it intermittently pass run after run.
- **The OSDU cache page names the flows that fill a partition.** With two or three cache flows (one capturing OSDU
  reference data and one holding the estate's lookup tables, say) the header names each file instead of counting them.
  The cache flow's operations say that a refresh and a plan cover dictionaries and ingestion tables as well as OSDU.
- **The mapping document kind is registered where the loader, the CLI and the proposal preflight look for it.** The
  delivery kind owns the mapping documents, but was registered only as a flow kind, so in a composed host a proposed
  mapping met "unknown documentType 'mapping'". It is now registered as the companion document kind as well, beside the
  new dictionary kind.
- **The repository sync takes only a top-level `documentType: mapping` for a mapping.** Any YAML that held the text
  `documentType:` and the word `mapping` anywhere was stored as a broken mapping row; a file starting with a byte
  order mark is now read as it should be.

- A cache flow's `plan` operation read the partition's cache under the text its `data-partition-id` is written with,
  so a flow naming its partition `${env:...}` planned against a cache no capture writes to; it now resolves the
  partition as a refresh does.
- `sqlflow cache list` resolves the partition it is given, as a partition reference or through a cache flow's file,
  before reading the versions, so it lists the cache a capture of that partition writes rather than none.

### Security

- The delivery nodes check every address a host name resolves to, and every redirect hop, before connecting: cloud
  metadata and link-local addresses are never reached, loopback only when the deployment allows it, and private
  ranges only when it lists them in `SQLFLOW_DELIVERY_PRIVATE_NETWORKS`. A redirect to another host carries no
  credentials, and one from https to http is refused.

[Unreleased]: ./
