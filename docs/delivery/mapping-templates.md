# Templates and mappings

This document is the design and the implementation plan for how OSDU Delivery turns an incoming dataset into OSDU
records. It replaces the property list mapping format.

## The idea in one paragraph

OSDU publishes a schema for every kind. OSDU Delivery turns that schema into a **template**: the OSDU record with a
variable for every property, owned by the system and stored in the catalog database. A person never edits a
template. What a person writes is a **mapping**: a YAML document that says, for each template variable they want
filled, where its value comes from. A value comes from the incoming dataset, from the metadata cache, or is a static
value. Every variable the mapping does not fill is left out of the record.

## Templates

A template is generated from an OSDU schema. It is identified by the OSDU kind it was generated from, such as
`osdu:wks:work-product-component--WellLog:1.4.0`, and by its version: the hash of the saved schema, such as
`26a3c3441882db4f`. A mapping pins both, so it is always known exactly which template version it fills.

**Where templates come from.** The GUI's Templates page is where a person looks at OSDU schemas and turns one into a
template:

- **Browse OSDU.** Pick a flow whose OSDU connection to use, and search the schemas OSDU publishes by authority,
  source and entity type (openapi schema_service, `GET /schema`). A node runs the search with that flow's credentials,
  resolved from the node's own environment exactly as for a run.
- **Look at a schema.** Open a kind to see it laid out as a template: every variable with its type, requiredness,
  relationships, unit context and OSDU's description. The node fetches the schema (`GET /schema/{id}`) and every schema
  it references, and nothing is stored yet.
- **Save it.** Saving stores exactly the schema that was shown as a template version, and from then on mappings can
  pin it.
- **Import a file.** A bundled schema file can be uploaded and saved the same way, for an environment without access
  to OSDU, and for tests.

**Where templates are stored.** In the catalog database, table `delivery.Template`. The control plane runs as a
container whose disk does not survive a restart, and the catalog is where everything durable already lives. A row
holds the kind, the version, the schema itself, when it was saved, by whom, and where from.

**Template versions do not change.** A version is its content, so it can never be edited. Saving the same schema again
changes nothing. Saving a schema that differs from every saved version of its kind adds a new version beside them,
which a mapping only uses once it pins it. A version can be deleted only while no synced mapping pins it.

**What a template looks like.** Every property of the schema becomes a variable, named by its path in the record and
prefixed with `osdu.`:

```text
osdu.acl.owners                          array of string   required
osdu.legal.legaltags                     array of string   required
osdu.tags.<name>                         string
osdu.data.Name                           string
osdu.data.WellboreID                     string            points to master-data--Wellbore
osdu.data.SamplingInterval               number            unit context UOM
osdu.data.VerticalMeasurement.VerticalMeasurement            number
osdu.data.Curves                         array of objects
osdu.data.Curves[].CurveID               string
osdu.data.Curves[].CurveUnit             string            points to reference-data--UnitOfMeasure
```

`[]` marks a step into an array of objects. Each variable carries the schema's type, whether the schema requires it,
which entity types it points to, its unit context, and OSDU's description. Objects the schema defines through a
choice of shapes (`meta`, geometry) are one variable taking a whole object.

Four variables are never filled by a mapping. `osdu.id` is written by the engine from the dataset key, `osdu.kind` from
the template, and `osdu.version`, `osdu.createTime`, `osdu.createUser`, `osdu.modifyTime` and `osdu.modifyUser` are set
by OSDU.

## Mappings

A mapping is a YAML file in the flow repository, under `mappings/`, named `Name@version.yaml`. It is reviewed and
versioned like the flows.

```yaml
documentType: mapping
name: WellLog
version: 1.4.0
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  version: 26a3c3441882db4f
description: Recall well logs, one record per logging run.

dataset:
  system: recall
  key: [dataset.source_project, dataset.log_id]
  label: "{dataset.wellbore_uwi} / {dataset.log_name} / run {dataset.log_run} ({dataset.log_id})"

parameters:
  dataPartition: { required: true }

mappings:
  - target: osdu.acl.owners
    static: [data.default.owners@opendes.dataservices.energy]

  - target: osdu.data.Name
    source: dataset.log_name
    modifiers: [trim]

  - target: osdu.data.SamplingInterval
    source: dataset.index_increment
    appliesWhen: dataset.depth_coding is REGULAR

  - target: osdu.data.WellboreID
    source: cache.Wellbore.id
    findBy: cache.Wellbore.FacilityName = dataset.wellbore_uwi

  - target: osdu.data.Curves
    source: dataset.curves

  - target: osdu.data.Curves[].CurveUnit
    source: cache.UnitOfMeasure.id
    findBy: cache.UnitOfMeasure.Code = dataset.curves.curve_unit
    modifiers:
      - replace: { GAPI: gAPI, G/CM3: g/cm3 }

  - target: osdu.data.Curves[].LogCurveBusinessValueID
    source: cache.LogCurveBusinessValue.id
    findBy: cache.LogCurveBusinessValue.Code = dataset.curves.business_value
    required: false
```

### The header

| Key | Meaning |
| --- | --- |
| `documentType` | Always `mapping`. |
| `name`, `version` | The mapping's reference, `Name@version`, which a flow pins under `render.mapping`. The file name must agree. |
| `template.kind`, `template.version` | The saved template version this mapping fills. A run refuses to render against any other. |
| `description` | Free text. |
| `dataset.system` | The source system. It enters the delivery key. |
| `dataset.key` | The dataset columns that identify a record, in order. The delivery key, and so the OSDU id, is derived from them. |
| `dataset.label` | Optional display text for the ledger and the GUI, with `{dataset.column}` tokens. It never enters the record. |
| `parameters` | Values the flow supplies under `render.parameters`. `dataPartition` is always declared. |
| `mappings` | The entries, described below. |
| `fixtures` | Example rows and the exact record each must render to. Every run checks them before rendering. |

### An entry

Every entry has one `target` and one input. The input is either `source` or `static`.

| Key | Meaning |
| --- | --- |
| `target` | The template variable to fill, such as `osdu.data.Name` or `osdu.data.Curves[].CurveID`. |
| `source` | Where the value comes from. The first word says where: `dataset` or `cache`. |
| `static` | A fixed value: a string, a number, a boolean, a list or an object. `{param.name}` tokens are replaced with the flow's parameter values. |
| `findBy` | Only with a cache source. Which cached record to read. |
| `modifiers` | Changes to an incoming dataset value, applied top to bottom. |
| `appliesWhen` | When the entry applies to a row. When it does not, the variable is left out for that row. |
| `required` | What happens when the value is empty. Default `true`. |
| `description` | Free text. |

### Sources

| Written as | Reads |
| --- | --- |
| `dataset.<column>` | A column of the incoming dataset's row. |
| `dataset.<child>.<column>` | A column of a child dataset's row. Only valid under a repeated target. |
| `dataset.<child>` | On a target that is an array of objects: one array item per row of the child dataset. This is the repeater. |
| `cache.<Type>.id` | The OSDU id of the cached record `findBy` selects, in the reference form OSDU relationships use (ending in `:`). |
| `cache.<Type>.<field>` | A field of that cached record, such as `Name` or `NameAlias.AliasName`. |

A repeated target's entries (`osdu.data.Curves[].X`) read the child dataset the repeater names. They can also read the
parent row's columns with `dataset.<column>`. A repeater inside a repeated item is not supported.

### findBy

```yaml
findBy: cache.UnitOfMeasure.Code = dataset.curves.curve_unit
```

Reads as: the cached `UnitOfMeasure` whose `Code` equals the incoming `curve_unit`. The left side names a field of the
cached type the source reads. The right side is `dataset.<column>`, `dataset.<child>.<column>`, or a quoted literal
such as `'KellyBushing'`. A list of `findBy` lines is tried in order, and the first that finds a record wins:

```yaml
findBy:
  - cache.UnitOfMeasure.Code = dataset.curves.curve_unit
  - cache.UnitOfMeasure.Name = dataset.curves.curve_unit
```

An exact match wins. Case is ignored only when that finds exactly one record, because OSDU codes that differ only by
case are different records (`ft` the foot, `fT` the femtotesla). Several matching records always hold the record.
`ignoreSeparators: true` on the entry adds a last attempt with punctuation and spacing folded away, for names such as
`NO 15/9-19` and `NO_15_9-19`. It is meant for names, never for codes.

### Modifiers

Modifiers change incoming dataset values only. On a cache source they change the `findBy` value before it is
compared. Cache values are OSDU's own and are never modified.

| Modifier | Written as | Incoming value | Result |
| --- | --- | --- | --- |
| trim | `- trim` | `" STAT_COMP "` | `"STAT_COMP"` |
| upper, lower | `- upper` | `"gapi"` | `"GAPI"` |
| split | `- split: { separator: ",", part: 1 }` | `"MAIN,REPEAT"` | `"MAIN"` |
| replace | `- replace: { GAPI: gAPI }` | `"GAPI"` | `"gAPI"` |
| equals | `- equals: REGULAR` | `"REGULAR"` or `"DISCRETE"` | `true` or `false` |
| date | `- date` or `- date: dd.MM.yyyy` | `"01.09.2026"` | `"2026-09-01T00:00:00Z"` |

`part` counts from one. A separator of a single space splits on any run of whitespace. `replace` leaves a value it
does not list unchanged. `equals` compares trimmed text and ignores case.

### appliesWhen

```yaml
appliesWhen: dataset.depth_coding is REGULAR
```

The forms are `<value> is <text>`, `<value> is not <text>`, `<value> is empty` and `<value> is not empty`, where the
value is `dataset.<column>` or `dataset.<child>.<column>`. Text comparison is trimmed and ignores case. A false
condition leaves the variable out for that row. It never holds a record.

### required

| Situation | `required: true` (default) | `required: false` |
| --- | --- | --- |
| The dataset value is empty after modifiers | Record held | Variable left out |
| The cache has no matching record | Record held | Variable left out |
| The cache has several matching records | Record held | Record held |
| A repeater's child dataset has no rows | Record held | Variable left out |
| `appliesWhen` is false | Variable left out | Variable left out |

`required: false` never introduces a value. A held record is never sent, and the ledger records the reason.

### What the record contains

The engine starts from nothing, writes `id` and `kind`, then writes each entry's value at its target. Types come from
the template: `"1000"` becomes the number `1000` where the schema says number. A value that cannot take the schema's
type holds the record. A variable with no entry, an entry that does not apply, and an optional entry with no value
are all left out, and so is an object or array left with nothing in it.

## Checks

When a mapping is read:

- The header keys, entry keys, sources, `findBy` lines, modifiers and conditions parse, and each entry has exactly
  one input. Unknown keys are refused.
- No two entries fill the same target.

Before any row is rendered (the preflight):

1. The pinned template version is saved in the catalog.
2. Every target is a variable of the template, with an agreeing shape: a repeater only on an array of objects, `[]`
   only under a repeater, a plain value only on a scalar or an array of scalars, an object only from `static`.
3. No entry fills `osdu.id`, `osdu.kind` or a property OSDU sets.
4. `osdu.acl.owners`, `osdu.acl.viewers`, `osdu.legal.legaltags` and `osdu.legal.otherRelevantDataCountries` are
   static, non-empty and free of repeats, so the legal service can check the tags before a run.
5. Every property the schema requires has an entry, and none of those entries is `required: false`.
6. Every dataset column and child dataset exists in the drop, when the drop is known.
7. Every cache type exists in the cache, and holds the fields `findBy` compares and the field the source reads.
8. A cache source resolves to the entity type the schema expects for its target. `osdu.data.WellboreID` can only be
   read from a cached type of `master-data--Wellbore`.
9. A static value on a relationship property exists in the cache when the cache holds that entity type.
10. Every fixture renders exactly as declared.

## The mapping builder

The GUI's Mapping builder answers "I want to populate this OSDU kind; how do I write the mapping?":

1. Pick the repository the mapping will live in, and a saved template. A kind without a template is browsed, looked
   at and saved on the Templates page first.
2. The page lists every template variable with its type, requiredness, relationship and OSDU description.
3. **Entries are prefilled from the cache.** Every variable that points to an entity type the repository's cache holds
   gets a cache entry: `source: cache.<Type>.id` and a `findBy` on the type's first cached field. The person completes
   the incoming side.
4. For each variable the person chooses dataset, repeater, cache or static, and adds modifiers, a condition and the
   required flag.
5. The page shows the resulting YAML, checks it against the template and the repository's current cache, and either
   copies it or opens a pull request against the repository through the existing proposal path.

An existing mapping opens in the builder with its entries filled in.

## Manual submission

Records sent by hand, or by a source system through `POST /api/v1/delivery/submissions`, follow the same standard
(docs/delivery/submitting-records.md):

- **A record is a fixture's input.** `record` holds the dataset row the mapping reads as `dataset.<column>`, and
  `datasets` holds the rows of each child dataset it reads as `dataset.<child>.<column>`. The run writes each child
  dataset as the drop scope of the same name.
- **The source contract speaks in template terms.** `GET /api/v1/delivery/flows/{pipelineId}/source-contract` names the
  template version the flow's mapping pins and whether the catalog holds it, the dataset's system, key and label, every
  column with the template variables it fills (as the value, to find a cached record, or to decide whether an entry
  applies), and every child dataset with the lists its rows fill.
- **A mapping that pins an unsaved template** still lists its columns, and the contract says no run can render the
  records until that version is saved.
- **The manual submission list** names the template kind each flow's mapping fills, and the Submit records dialog shows
  under every field what the column fills.

## What is removed

- The property list format: `source`/`identity`/`envelope`/`properties`/`definitions` blocks, per-property
  `examples`, and the `constant`, `template`, `map`, `reference`, `lookup` and `deliveredReference` transforms.
  `replace`, `equals`, `split`, `date` and cache sources cover what the sample estate used.
- Schema snapshots in the repository's snapshot store, and the `sqlflow snapshot <flow> schema` verb. Templates
  replace them. Reference snapshots (the cache) stay where they are.

Rendering now needs the catalog, because templates live there. A CLI run without a catalog can still validate
documents, and the reference cache capture is unchanged.

## Implementation plan

1. **Catalog.** Add `DeliveryTemplate` to the EF model (schema `delivery`), and a template store over the catalog:
   load by kind, save immutably, list, delete while unused. This is a schema change, so development catalogs are
   re-minted.
2. **Template model.** Build the variable tree from a schema: paths, types, requiredness, relationships, unit
   contexts, descriptions, and the variables a mapping may not fill.
3. **Mapping format.** Replace the mapping model and loader with the header, entries, sources, `findBy`, modifiers,
   `appliesWhen`, `required` and fixtures, each parsed with errors that name the file and the entry.
4. **Engine.** Rewrite the renderer's walk over entries, keeping type coercion, cache resolution, cache usage
   tracking and hashing as they are. Rewrite the preflight with the checks above. Resolve templates from the catalog
   in the render resolver. Adapt the planner, the legal tag check and the catalog sync summary, and move manual
   submission onto the standard: records carry `datasets`, and the source contract describes each column by the
   template variables it fills.
5. **Browse, fetch and save.** Node operations that search OSDU's schemas and fetch one kind's bundled schema
   through a chosen flow's connection, a save path that stores a fetched or imported schema, and CLI verbs
   `sqlflow template capture | import | list | show`.
6. **API.** Search OSDU schemas and fetch one for preview; list, show, save, import and delete templates; draft a mapping for a repository and a template with
   cache prefills; convert between the builder's entries and YAML; check a mapping against its template and the
   repository's cache.
7. **GUI.** A Templates page to browse OSDU schemas, view a schema as a template and save it, and the Mapping
   builder, with the pull request action.
8. **Samples, tests and docs.** Convert both sample mappings, proving the converted WellLog mapping renders every
   record of the sample drops byte for byte as before. Import the sample schemas as templates in the tests and the
   GUI end-to-end setup. Rewrite the mapping tests, add template, builder and end-to-end coverage, and update the
   documentation that describes mappings.

Verification: `dotnet build SqlFlow.sln` with zero warnings, `dotnet test SqlFlow.sln`, `npm run build` in `gui/`
(which type-checks the app and the end-to-end specs; `gui/` has no lint script), and the GUI end-to-end suite.
