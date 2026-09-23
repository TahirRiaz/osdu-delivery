# Plan: lookups through the cache

Every translation or lookup a mapping needs becomes a table in the partition's cache, and a mapping reads every table the
same way. Unit spellings, type codes and the curve dictionary stop being custom value maps inside individual mappings.
They are loaded once into the cache, from OSDU, from an ingestion table or from a dictionary file, and are versioned,
traced and rolled out like any other cached value.

Each stage lists what it changes and the tests that close it. A stage is finished only when those tests pass, SQL
Server suites included; nothing moves to the next stage before that. All work is in `osdu/`, except the generic
language-server extension point of stage 7, which lands in `sqlflow/` as its own `sqlflow:` commit.

Line numbers below are at `710d522`. The files are being edited by another change set (see
[Before starting](#before-starting)), so go by the type and method named, not the exact line.

## The principle

**The mapping owns the target, the dataset owns the source, and lookup data lives in the cache.**

| Kind | Where it lives | Example |
| --- | --- | --- |
| OSDU's reference and master data | Cache, from OSDU | `UnitOfMeasure`, `LogCurveFamily` |
| Translations OSDU already models | Cache, from OSDU, when the partition holds them | `ExternalUnitOfMeasure`, `ExternalReferenceValueMapping` |
| Company translations and dictionaries | Cache, from a dictionary file (one per file) or an ingestion table | unit spellings, the curve dictionary |
| How a mapping uses them | Mapping: `findBy`, and `replace` reading a cached type | mnemonic, then family name, then OSDU id |
| The record's shape | Mapping syntax | nested objects, repeaters |
| Cleaning the source | Pre-ingestion and ingestion flows | `NULLIF`, `COALESCE`, splitting a column |

Inline `replace` stays for a translation used by one field of one mapping. Everything shared or vocabulary-like goes to
the cache. A mapping never reads a file or a table of its own: data reaches OSDU only through SQLFlow's flows and the
cache, and outside lookups are not supported.

## What an author writes when this is done

A dictionary file holds one table. The file `dictionaries/RecallUnits.yaml` holds pairs:

```yaml
documentType: dictionary
name: RecallUnits
description: Unit spellings in Recall, as the partition's UnitOfMeasure codes write them.
entries:
  M: m
  METRE: m
  METRES.: m
  FT: ft
  GAPI: gAPI
  G/CM3: g/cm3
  V/V: m3/m3
  NONE: ~
```

A dictionary with several values per key names its key and fields:

```yaml
documentType: dictionary
name: CurveDictionary
description: Curve mnemonics and what each measures.
key: mnemonic
fields: [type, family, mainFamily, unit]
entries:
  GR: { type: Equinor-GR, family: Gamma Ray, mainFamily: GammaRay, unit: gAPI }
  LFP_AI: { type: Equinor-AI, family: EQ-Acoustic Impedance Compressional, mainFamily: Geophysics, unit: kPa.s/m }
```

A cache flow declares where each type comes from, one origin per type:

```yaml
flowType: cache
name: wells-lookups-00-cache
source:
  connection: ${env:INGESTION_DB}          # for table types
  headers:
    data-partition-id: dev                 # the partition whose cache the flow fills
types:
  - dictionary: RecallUnits                # dictionaries/RecallUnits.yaml
  - name: CurveClasses
    table: OsduSample.ing.CurveDictionary  # an ingestion table SQLFlow's flows load
    key: mnemonic
    fields: [curve_type, curve_family, curve_main_family, unit]
```

A mapping reads cached tables through `replace` and `findBy`:

```yaml
  - target: osdu.data.Curves[].CurveUnit
    source: cache.UnitOfMeasure.id
    findBy: cache.UnitOfMeasure.Code = dataset.curves.curve_unit
    modifiers:
      - replace: cache.RecallUnits           # the key matches, the value replaces

  - target: osdu.data.Curves[].LogCurveFamilyID
    source: cache.LogCurveFamily.id
    findBy: cache.LogCurveFamily.Name = dataset.curves.curve_id
    modifiers:
      - replace: cache.CurveClasses
        field: curve_family                  # match defaults to the key, mnemonic
        otherwise: ~                         # an unlisted mnemonic gives no value
    required: false

  - target: osdu.tags.CurveFamily            # a plain value read straight from a cached table
    source: cache.CurveClasses.curve_family
    findBy: cache.CurveClasses.mnemonic = dataset.curves.curve_id
```

Where the partition holds OSDU's own translations, they are cached from OSDU like any reference data, and a cached
field that holds an OSDU id is written directly:

```yaml
  # cache flow: the type's query keeps the Recall namespace and only the mappings OSDU marks identical;
  # the exact query is written against the partition's namespace and map state ids in stage 6
  - kind: "osdu:wks:reference-data--ExternalUnitOfMeasure:*"
    name: RecallUnitAliases
    fields: [data.Code, data.UnitOfMeasureID]

  # mapping
  - target: osdu.data.Curves[].CurveUnit
    source: cache.RecallUnitAliases.UnitOfMeasureID
    findBy: cache.RecallUnitAliases.Code = dataset.curves.curve_unit
```

## Before starting

**Wait for the cache system properties change set.** Another session has an uncommitted change set that touches the same
files: `ReferenceSnapshot.cs`, `CacheDeclaration.cs`, `ICacheStore.cs`, `OsduCacheStore.cs`, `SnapshotBuilder.cs`,
`CacheRefresh.cs`, `EntryValues.cs`, `RenderResolver.cs`, `Planner.cs`, `DeliveryEntities.cs`, `OsduDbContext.cs`,
`DeliveryEndpoints.cs`, `gui/src/api/delivery.ts` and `TestSupport.cs`, with new files `SystemProperty.cs`,
`SystemPropertyCapture.cs`, `DeliveryCacheSystemProperties.tsx` and the migration `20260923132848_CacheSystemProperties`.
It adds a `readings` parameter to `ICacheStore.MergeAsync`, `CacheMerge.Apply` and `SnapshotBuilder.WriteAsync`. Stage 1
starts after it is committed. Every migration in this plan comes after `20260923132848`, and every capture that is not
an OSDU search passes `readings: []`, as an import does.

**Baseline.** A clean rebuild (`-t:Rebuild`) with zero warnings, the GUI build and lint, every suite green with
`SQLFLOW_TEST_DB` set, and `tools/check-vendored-sqlflow.sh` passing.

**Decisions to confirm.** Each has a recommendation; the plan is written with it.

| Decision | Recommendation |
| --- | --- |
| May one cache flow mix origins? | Yes. Each type has one origin; the flow's `source` carries what its types need. |
| A changed dictionary file: when does it reach the cache? | When the cache flow next runs (its schedule or a manual run), like any cache change. A refresh queued by the sync can follow later if wanted. |
| Inline `replace` moves to the cache's matching rules | Accept. The one change: a value that matches two keys only when case is ignored holds the record, instead of passing through unchanged, unless both keys give the same value. |
| Largest table or dictionary a lookup type may hold | 100,000 rows, as a module option, refused above it with the count. |
| Longest key | 256 characters, so the cache's composite indexes stay inside SQL Server's key-size limits. |

## Stage 1: `replace` gains "no value" and `otherwise`

Inline tables first, because every later stage builds on the same modifier.

**Syntax.** Settings sit beside `replace`, never inside its table, so no incoming value is ever mistaken for a keyword:

```yaml
- replace: { M: m, FT: ft, NONE: ~ }
  otherwise: ~
```

- A table value of `~` gives no value, and `required` decides what that does.
- `otherwise` applies to any value the table does not list: absent keeps the value (today's rule), `~` gives no value,
  text gives that text.
- A value is matched by the cache's rules: an exact match wins; case is ignored only when that finds one key; a value
  that matches several keys only when case is ignored holds the record with a reason naming the keys, unless every one
  of them gives the same value.

**Changes.**

- Model, `Model/MappingDefinition.cs`: `Modifier.Replacements` (230) becomes `IReadOnlyDictionary<string, string?>`;
  a new `ReplaceFallback` (keep, no value, or a text) on `Modifier`; `ToString` (244).
- Parser, `Documents/MappingMapper.cs`: `ParseModifier` (691-727) accepts a `replace` item with an `otherwise` sibling
  and refuses any other sibling by name; `Replace` (809-829) accepts null values; `Example` (831-837) shows both.
- Matching: the inline table is built once, when the mapping is read, into an in-memory lookup type, and matched through
  `ReferenceType.Find` (`Snapshots/ReferenceSnapshot.cs`, `FieldIndex.Lookup`). One matcher serves inline and cached
  tables.
- Renderer, `Rendering/EntryValues.cs`: `TryModify` (186) replace case and `Replace` (265-280); an ambiguous match adds
  a hold; `ModifierText` (156-161) shows `otherwise`.
- Builder, `Templates/MappingBuilder.cs`: `MappingDraftReplacement.To` (117) becomes nullable; the draft carries the
  fallback; `ModifierText` (815-824) writes `~` and `otherwise`; `ModifierIssue` (778-813).
- GUI: `gui/src/api/delivery.ts` (draft types), `features/delivery/mappingDraft.ts` (`newModifier`, `modifierText`),
  `MappingEntryEditor.tsx` (the replace editor: a "no value" choice per row and an `otherwise` field).
- Docs: `documents.md` and `mapping-templates.md` Modifiers.

**Tests.**

- `CoreTests.cs` (`MappingRendererTests`): exact, case-folded and ambiguous matches; `~` with `required` true and
  false; `otherwise` keep, no value and text; a `~` mid-chain stops the later modifiers.
- `DocumentsTests.cs`: a sibling other than `otherwise` is refused by name; a list or map as a table value is refused.
- `MappingBuilderTests.cs`: the YAML round trip keeps `~` and `otherwise`.
- `MappingShapeTests.cs`: the placeholder text.
- `gui/e2e/17-templates-and-builder.spec.ts`: the replace editor writes both.

**Closes when** the suites above pass, the sample mappings render their fixtures unchanged, and the build and GUI lint
are clean.

## Stage 2: lookup types in the cache model

The cache learns about types that hold no OSDU records. Nothing produces them yet; the tests build them directly.

**Rules.**

- A type has an origin: `osdu` (today's types), `table` or `dictionary`. A type of the last two is a lookup type.
- A lookup type's records are keyed by its key's text. The key is also stored as a field under its own name, so
  `findBy` and `replace` can match on it. Its entity type is `lookup--<Name>`, which no OSDU group uses.
- A key is trimmed, non-empty, unique within the type (exact comparison), and at most 256 characters. Keys that differ
  only by case are two records, as `ft` and `fT` are.
- No key or field of a lookup type is called `id` in any casing, because a cached `ID` field shadows the record id.
- `cache.<Type>.id` on a lookup type holds the record at render and is refused by the gate: a key is not an OSDU id,
  and writing one would produce `LFP_AI:` as a reference.
- A lookup type is declared by exactly one cache flow of a partition. A second declaration, or a declaration of the same
  name with another origin, is a conflict: the sync leaves it out with a warning, and a refresh of either flow refuses
  to run.
- A capture of a lookup type replaces the whole type. Widening does not apply, because only one flow declares it.

**Changes.**

- Snapshot, `Snapshots/ReferenceSnapshot.cs`: `ReferenceType` gains `Origin` and `Key`, written by `ToJson` and read by
  `FromJson` (250-292) into the version's type list, so no migration is needed for versions or items.
- Declaration, `Snapshots/ReferenceCaptureSpec.cs`: `ReferenceTypeSpec` carries the origin and the origin's settings,
  and `Validate` (53-90) checks each origin's rules. `Snapshots/CacheDeclaration.cs`: `Conflicts` (83-106) adds the
  rules above; `CacheMerge.Apply` (173-241) replaces a lookup type whole.
- Flow model, `Model/CacheDefinition.cs`: `CacheSource.Endpoint` (87) is required only when an OSDU type is declared;
  `CredentialReferences` (49-78) and `CacheFlowDocument.SourceReference` follow the origins.
- Database, `SqlFlow.Delivery.Data`: one migration on `osdu.CacheDefinition`: `Origin nvarchar(16) not null default
  'osdu'`, `Connection nvarchar(1000) null`, `SourceObject nvarchar(400) null`, `KeyField nvarchar(128) null`,
  `DictionaryPath nvarchar(1000) null`; `Endpoint` and `Kind` become nullable. The designer file and model snapshot
  come with it, and the module's schema version (`OsduSchema.cs`) moves with it, so hosts refuse a database without it.
- Sync, `Catalog/DeliveryCatalogSync.cs`: `SyncCacheDefinitionsAsync` (384-575) writes the new columns; the
  endpoint-mismatch warning (544-557) compares OSDU types only.
- Render and gate: `EntryValues.Select` refuses `.id` on a lookup type with a hold; `Validation/Preflight.cs`
  `CheckCache` (422-471) refuses it as an error.
- Import, `Engine/Snapshots/SnapshotBuilder.cs` `CheckDeclared` (99-155): lookup types are neither required nor
  accepted in an import directory, and every imported record's id must name the declared entity type in the flow's
  partition, so an import cannot pass off hand-made records as OSDU ones.
- API and GUI: `DeliveryEndpoints.cs` cache DTOs (140-225) carry the origin; `DeliveryCacheDefinition.tsx` shows each
  type's origin in place of "Captured from" and "Searched as"; `DeliveryCacheRecords.tsx`, `DeliveryCacheHistory.tsx`
  and `cacheFormat.ts` say "Key" instead of "OSDU id" for a lookup type.

**Tests.**

- `ReferenceCacheTests.cs`: matching on a key and on fields; `.id` refused; the id-shadowing rule.
- `CacheDeclarationTests`: the one-declarer and origin conflicts.
- `StorageTests.cs` (`OsduCacheStoreTests`): a round trip with keys `METRES.`, `LFP_AI`, `ft` and `fT`.
- `SqlServerLedgerTests.cs` (SQL Server): the same keys on the real collation, and a key that differs only by
  trailing spaces is refused before it reaches the unique index.
- `SqlServerLedgerMigrationTests.cs`: the migration applies to a database at the previous version and leaves existing
  rows as `osdu`.
- `SnapshotVersioningTests.cs`: the import rules.
- `CacheCatalogSyncTests.cs`: the new columns and the warnings.
- `gui/e2e/14-osdu-cache.spec.ts`: the definition tab shows origins.

**Closes when** the suites above pass on SQLite and SQL Server, and an existing cache keeps rendering every sample
fixture unchanged.

## Stage 3: dictionary files as a cache origin

**The document.** `documentType: dictionary`, one table per file, in a `dictionaries/` folder found the way `mappings/`
is (`Documents/DeliveryLayout.cs`), named `<name>.yaml` or `<name>.yml`, and the declared name must match the file name.

- `entries` is required and non-empty. Either every entry is a scalar (the type's fields are the key and `value`) or
  every entry is a map using only the declared `fields`; a field an entry leaves out is absent for that record.
- Values are text, numbers, booleans or `~`. Lists and nested maps are refused.
- `key` names the key field (default `key`). Key and field names are identifiers, unique ignoring case, never `id`.
- The key rules of stage 2 apply, and at most 100,000 entries.
- There is no `version`: the cache version that captures the file is its version, and the repository history holds the
  file's history.

**Changes.**

- Loader: a `DictionaryDefinition` model, a strict YAML model in `Documents/YamlModels.cs`, and
  `DeliveryDocumentLoader.ParseDictionary` beside `ParseMapping` (164-175).
- Companion registration: an `ICompanionDocumentKind` for `dictionary`, registered in `DeliveryServices.AddDeliveryKind`
  (60-62). The mapping companion is registered there too, because today it is registered only as a flow kind (see
  [Found on the way](#found-on-the-way)). With both registered, SQLFlow's proposal preflight
  (`FlowProposalPreflight.Run`) checks dictionary files like mappings.
- Cache flow: a type written `dictionary: <name>`, whose name defaults to the dictionary's.
  `CacheFlowDocument.RequiresRepoTree` (`Documents/CacheFlowKind.cs:34`) is true when the flow declares one, so the node
  materializes the repository and the file is read at the commit the run was given.
- Capture: `SnapshotBuilder` gains a dictionary producer beside `CaptureAsync` and `ImportDirectoryAsync`, feeding the
  same `WriteAsync`. `CacheRefresher.RefreshAsync` (35-85) branches only where it captures (53-55), and the capture's
  origin text names the file. `PlanAsync` (88-111) counts entries for a dictionary type.
- Sync: `SyncCacheDefinitionsAsync` resolves and parses every dictionary a cache flow declares, and leaves the type out
  with a warning naming the file when it is missing or invalid. `LooksLikeMapping` (664-665) matches the `documentType`
  value exactly, so a dictionary that mentions the word "mapping" is not taken for one.
- Lineage, `Documents/CacheLineage.cs`: a dictionary type adds a `DeclaredFileLocation` read.
- Samples: `samples/wells/dictionaries/RecallUnits.yaml`, and a lookups cache flow `wells-lookups-00-cache.yaml` for
  partition `dev` declaring it.
- Docs: a "Dictionary" section in `documents.md`, the dictionary origin in its Cache flow section, and the samples
  README.

**Tests.**

- `DocumentsTests.cs`: every rule of the document, each refusal naming the file and the entry.
- `CacheCatalogSyncTests.cs`: a dictionary type syncs with its path; a missing or invalid file leaves the type out with
  a warning; a dictionary containing the word "mapping" is not stored as a mapping.
- A new `DictionaryCaptureTests.cs`: a refresh writes a version holding the entries; an unchanged file writes none; an
  edited entry raises a change tag reaching only the records that used it, under `auto` and `approve`.
- `LineageTests.cs`: the file read.
- `DeliveryModuleTests.cs` (ControlPlane): both companion kinds are registered.
- `DeliveryPlatformTests.cs`: the proposal preflight, with the loader built through DI rather than by hand, accepts a
  valid dictionary and a valid mapping and refuses an invalid dictionary.
- `gui/e2e/14-osdu-cache.spec.ts`: a dictionary type's records and origin.

**Closes when** the suites above pass and the sample lookups cache flow refreshes in the SQL Server chain suite.

## Stage 4: ingestion tables as a cache origin

**The type.** `table:` names a three-part object; `key:` names one column; `fields:` names columns, bare or as
`{ column: <name>, as: <field> }`. The flow's `source.connection` is declared and checked exactly as a delivery flow's
is (`IngestionConnection.CheckDeclared`).

**The read.**

- Opens with `IngestionConnection.OpenAsync` (95-134) and the run's secret resolver.
- Checks the table and every column against `sys.columns` (`IngestionSql.Columns`), and refuses a key column of a type
  that cannot be compared, with the same messages as the delivery reader.
- Selects the key and the fields only, in one statement (so no snapshot isolation is needed on the database), and
  leaves out rows whose `DeletedDate_DW` is set when the table has that column.
- Values go through `SourceValues.Normalize` and `SourceRow.Stringify`, the delivery reader's own rules, and are kept as
  that text, like a dictionary's; a null is an absent field.
- A key is trimmed. A null, empty or over-long key, or one two rows hold once trimmed, refuses the capture, naming the
  rows; so does a table over the row limit. Nothing is written.

**Changes.**

- Cache flow model and loader: `source.connection` on a cache flow, required when a table type is declared;
  `CacheFlowDocument` overrides `SourceConnectionReference` and `DeclaredObjects`.
- Reader: `SourceColumn` and `ReadColumnsAsync` are lifted out of `Source/SqlServerIngestionSource.cs` (451-479) so the
  delivery reader and the table capture share them. A `TableCapture` in `Engine/Snapshots` produces the type for
  `WriteAsync`. `PlanAsync` counts rows.
- Lineage: `CacheLineage` adds `DeclaredDataObject.Reads(connection, table)`, so SQLFlow orders the ingestion flow
  before the cache flow, and the cache flow before the delivery flows that read its types.
- Samples: a curve dictionary CSV under `samples/wells/data/`, a pre-ingestion and an ingestion flow loading
  `OsduSample.ing.CurveDictionary`, and the table type in the lookups cache flow.
- Docs: the table origin in `documents.md`, and the lineage order in `architecture.md`.

**Tests.**

- `DocumentsTests.cs`: the type's rules and the connection check, a literal password refused.
- SQL Server, `SqlServerIngestionFixture.cs` and new table capture tests: a capture writes a version; soft-deleted rows
  are left out; SQL types become the values the delivery reader gives; a duplicate, null or over-long key and an
  over-limit table are refused with nothing written; a missing column names the table's columns.
- `SqlServerChainTests.cs`: files, pre-ingestion, ingestion, the lookups cache flow and a delivery with a fake protocol,
  ordered in lineage waves.
- `LineageTests.cs`: the table read and the order.

**Closes when** the chain runs end to end on SQL Server and every suite passes.

## Stage 5: `replace` reads a cached type

**Syntax.**

```yaml
- replace: cache.<Type>
  match: <field>        # default: the type's key; required for an OSDU type
  field: <field>        # default: value, for a dictionary of pairs; otherwise required
  otherwise: ~          # as in stage 1
```

- The incoming value is matched on `match` by the cache's rules, and replaced by the matched record's `field`.
- A matched record whose `field` is empty gives no value; `otherwise` applies only when nothing matches.
- A `field` holding several values holds the record, unless it holds exactly one.
- Any cached type works, OSDU ones included.

**Changes.**

- Parser: `ParseModifier` accepts a scalar `cache.<Type>` after `replace`, with `match`, `field` and `otherwise` as
  siblings. The defaults are settled against the cache version, by the gate and the render alike.
- Which types a mapping reads: a single `MappingDefinition` answer covering sources and `replace` modifiers, used by
  `RenderResolver.CacheAsync` (the `readsCache` test at 175), `SubmissionIntake` (401-410, which refuses usages without a
  cache scope) and `DeliveryLineage.CacheTypes` (133-152). All three ask it instead of looking at sources alone.
- Renderer: `TryModify` takes the renderer and the usage list; `Searched` (295) passes its usages through. A hit records
  a `Match` usage on `match` and a `Value` usage on `field`, so a changed dictionary entry tags exactly the records that
  used it.
- Gate, `Preflight.cs`: the type exists in the cache version the render reads and holds `match` and `field`. On an entry
  whose `findBy` resolves the replaced value (a reference), every value the table can produce, inline or cached, is
  looked up in the target type, and the ones that find nothing are listed as a warning.
- Shape, coverage and builder: `EntryValues.Describe` and `ModifierText` write
  `replace from cache.CurveClasses (mnemonic to curve_family)`; the builder's replace editor offers the cached types of
  the chosen partition with pickers for `match` and `field`; `mappingDraft.ts`, `MappingEntryDetail.tsx` and
  `MappingPropertiesView.tsx` describe it.
- Samples: the WellLog and WellboreTrajectory mappings replace their inline unit tables with `replace: cache.RecallUnits`;
  WellLog fills `LogCurveFamilyID` through `CurveClasses`; the sample cache records gain the `LogCurveFamily` records the
  fixtures resolve; the fixtures are re-derived and reviewed.
- Docs: `replace` in `documents.md` and `mapping-templates.md`; the "What is removed" line updated.

**Tests.**

- `CoreTests.cs`: hit, miss with each `otherwise`, ambiguous hold, set-valued field, an OSDU type as the table.
- `CacheChangeTests.cs`: a render records both usages; an edited dictionary entry tags only the records that used it.
- A render test where only a modifier reads the cache: the cache is loaded, and the record's render context names the
  cache version.
- `PreflightTests`: a missing type or field; unresolvable defaults; the list of values that do not resolve.
- `LineageTests.cs`: a delivery flow reading a type only through `replace` declares it.
- `MappingBuilderTests.cs`, `MappingShapeTests.cs`, `DeliveryTemplateApiTests.cs` (builder over a cache) and
  `gui/e2e/17-templates-and-builder.spec.ts`.

**Closes when** the suites above pass, the sample chain delivers with units and families resolved through the lookups
cache, and fixtures match.

**As built.**

- `field` defaults to the one field a lookup table holds beside its key, which is `value` for a dictionary of pairs and
  also covers a table type with one field; a table whose rows hold no field beside the key reads `value`, which gives
  no value for every row, exactly as such a dictionary says.
- Two usage kinds join `Match` and `Value`, stored as `empty` and `unlisted` in the existing `Kind` column (no
  migration): `Empty` records a field a render read and found empty (a dictionary entry of `~`, and a cache source's
  field too), and `Unlisted` records a key a lookup table did not list, folded without case. The impact analyzer tags
  an `Empty` read that gains a value as `changed` from no value, and asks the ledger about keys a lookup table newly
  lists, tagging an `Unlisted` read as `listed`. A value a type of OSDU records does not hold on a non-key field depends
  on no row and is not recorded.
- The builder's cache types carry a lookup table's key (`CachedTypeInfo.Key`, read from `[osdu].[CacheDefinition]`),
  so the replace editor can show the settled match and field.
- Found on the way: ledger reads did not retry a deadlock, which the concurrent chain suites met intermittently; every
  ledger read now retries one, as the writes did.

## Stage 6: OSDU ids held in cached fields

This is what makes OSDU's own `ExternalUnitOfMeasure` and `ExternalReferenceValueMapping` usable.

**As built.** Checked against the published schemas: `ExternalUnitOfMeasure` 1.0.0 holds `NamespaceID`, `MapStateID`
(a `CatalogMapStateType`: identical, corrected, unsupported) and `UnitOfMeasureID`; `ExternalReferenceValueMapping`
1.0.0 holds the target in `SimpleMap.ReferenceValueID`. A shared helper (`CachedReferences`) reads an id the same way in
the gate and the render. Found on the way: a record id was looked for through `Match("id", ...)`, which on a type caching
a field of its own called `ID` (the sample's `UnitOfMeasure`) reads that field instead, so a static reference to a unit
was refused and a value that already was a unit id recorded no dependency; and the impact analyzer read a written id
back through the same field, raising a false `changed` tag whenever such a record moved. `ReferenceType.ById` answers by
the record id, and the analyzer treats a usage of a record's own id as unchanged while the record is there.

**Changes.**

- Gate, `Preflight.cs` `CheckCache`: it stops returning early (455-458) for a field that is not `id` when the target
  is a relationship.
  - Every distinct value the field holds in the cache version must be an OSDU id of an entity type the relationship
    allows; anything else is an error naming the count and the first values.
  - When the cache holds that entity type, a value naming a record it does not hold is a warning listing them. This
    reuses the existence test of `CheckStatic` (384-390).
- Renderer: such a value holds the record when the cache holds the entity type and not the record, so a broken
  reference is never written.
- Docs: a section on translations OSDU already models, with the `ExternalUnitOfMeasure` example and why its query keeps
  only `identical` mappings (a `corrected` unit needs its values converted, and an `unsupported` one has no equivalent).

**Tests.** `PreflightTests` and `CoreTests.cs` with an `ExternalUnitOfMeasure` fixture type: a valid id, an id of
another entity type, a missing record, and a partition whose cache holds no records of the target type.

**Closes when** the suites pass and the example in the docs renders in a fixture.

## Stage 7: the language server knows the delivery documents

SQLFlow's language engine (`sqlflow/tools/sqlflow-lang`, served by `sqlflow-lsp`, built to WebAssembly for the GUI)
compiles in SQLFlow's own key census files, one per `flowType`, and knows nothing of the documents this module adds, so
the GUI shows mappings with the language server off.

**Changes.**

- `sqlflow/`, a generic extension point in its own `sqlflow:` commit, recorded in `docs/sqlflow-changes.md`: the census
  accepts census files a module supplies, each naming the `flowType` or the `documentType` it describes, at run time. The
  language server reads them from the directories its initialization names, the WebAssembly engine takes them through a
  registration call, and the unknown-`flowType` diagnostic accepts every kind a module registered.
- `osdu/`: census files for the cache flow (every origin), the mapping (entries, sources, `findBy`, every modifier with
  `replace`'s `otherwise`, `match` and `field`) and the dictionary document, beside the docs they are written from. The
  GUI module registers them, and the mapping and cache views turn the language server on.

**Tests.** The engine's own census tests for a registered kind (completion, hover, an unknown key, an enum value), and a
census test in `osdu/` that every key the loader accepts is in the census and every census key is one the loader
accepts.

**Closes when** the Rust workspace tests pass, the WebAssembly package is rebuilt, and the mapping and cache views
complete and flag keys in the GUI.

**As built.** The generic registry is `ca779a3` (`census::register`, `initializationOptions.censusDirectories`,
`register_census`, `GuiModule.census`), with `strictKeys`, `includeEnvelope`, `includeShared` and `freeForm`. The census
covers every document the module adds, not only the three the plan named: the delivery and retrieval flows too, since
their pipeline pages had the editor's analysis off as well. The files are in `osdu/docs/census`, and `EditorCensusTests`
reads the accepted keys off the loaders' YAML models by reflection and the modifier and dictionary keys off the names
`MappingMapper` and `DictionaryMapper` accept. Every sample flow, cache flow, mapping and dictionary analyses clean. Found
on the way: SQLFlow's file flow census did not document `schedule.operation` and `schedule.values`, which the envelope
schedule reads, and the cache loader dropped a `key` written on a dictionary type without a word; the first is fixed in
the same `sqlflow:` commit, the second refuses the key now.

## Close-out

- A clean rebuild with zero warnings; the GUI build and lint; every suite, SQL Server included.
- `tools/check-vendored-sqlflow.sh` passes; no commit touches `sqlflow/`.
- The docs match the code: `documents.md`, `mapping-templates.md`, `design.md` section 6.2, `architecture.md`,
  `reference/cli/delivery.md` and the samples README. The gate's checks carry one numbering everywhere (the code and
  `mapping-templates.md` number them one higher than `documents.md` today).
- No live OSDU run is part of this plan. A live verification, if wanted, is listed and approved first under the
  project's live rules.

## Found on the way

Not part of this plan's stages unless named in one. Each needs confirming before it is acted on.

1. **Mapping proposals may fail in the composed control plane.** `DeliveryFlowKind` implements `ICompanionDocumentKind`,
   but DI registers it only as `IFlowDocumentKind` (`DeliveryServices.cs:60-62`). SQLFlow's proposal preflight tries a
   YAML file as a companion first and refuses an unregistered `documentType`. The test that covers proposals builds
   its loader by hand, so it cannot see this. Stage 3 fixes and tests it.
2. **Supplied references may not reach `source.connection`.** `EngineContext.WithSuppliedReferences`
   (`FlowRuntime.cs:63-64`) swaps the secret resolver but not the source factory, which captured the node's resolver
   when it was built. So an `${env:NAME}` value the control plane supplies with a run may not reach a delivery flow's
   connection. Stage 4 opens its connection with the run's resolver; the delivery reader's behaviour should be
   checked separately.
3. **Mapping YAML is stored raw.** `DeliveryMapping.Yaml` is described as secret-redacted, but the sync stores the text
   as it is (`DeliveryCatalogSync.cs:351`).
4. **Any YAML mentioning "mapping" is treated as a mapping** (`LooksLikeMapping`). Stage 3 fixes it.
5. **`sqlflow cache import` accepts any record content.** Stage 2 restricts it to ids of the declared entity type.

## Not in this plan

The other mapping gaps, planned separately: numbered array items filled from the record's own row, `choose` with the
new condition forms, the `substring` modifier, and access and legal values read from the dataset (once its form is
decided).
