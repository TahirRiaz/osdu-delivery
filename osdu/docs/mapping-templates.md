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

- **Browse OSDU.** The canonical OSDU schemas are the OSDU data definitions, the Open Group's public repository at
  <https://community.opengroup.org/osdu/data/data-definitions>. Pick one of its releases (a version tag; the newest is
  the default) and search the record kinds it publishes. Its `Generated/SchemaStatus.json` lists every kind of the
  release, and the list holds the record schemas among them, the kinds a template can be laid out from: the abstract
  building blocks, the manifest and the content schemas (which describe what sits inside a record rather than a record)
  declare no `data` and are left out. A kind's schema is the file that declares it in its `x-osdu-schema-source`,
  wherever the release keeps it, which is not always the folder the kind's entity group names: the generic kinds
  (`osdu:wks:dataset--GenericDataset:1.0.0` and its four siblings) live under `manifest/`. The control
  plane keeps a local copy of the repository on disk: the first time a release is read, its whole `Generated` folder is
  downloaded as one archive and unpacked, and every file of it is read from disk from then on, across restarts. The
  release list is kept beside it, read again when it is older than `ControlPlane:SchemaRepository:RefreshMinutes` (a
  day) and whenever someone presses **Sync with the repository**, which also downloads the release in view when it is
  not on disk; the page says when the list was last read, and marks the releases on disk. The newest release is
  downloaded when the control plane starts. The schemas are public, so no flow, credential or node is involved.
  `ControlPlane:SchemaRepository` points it at a mirror (`ApiUrl`, `WebUrl`) when community.opengroup.org is out of
  reach, and `CacheDirectory` moves the local copy.
- **Look at a schema.** Open a kind to see it laid out as a template: every variable with its type, requiredness,
  relationships, unit context and OSDU's description. The control plane reads the kind's file under `Generated` at the
  commit the release tag names, with every file it refers to, and bundles them; nothing is stored yet. The kind links
  to its file in the repository.
- **Compare versions.** Pick two versions of a kind, each from any release, and see whether anything changed. The
  verdict comes first: no change (the same file, or files that differ only in the version identifiers each carries), or
  how many breaking, additive and wording changes there are. Every variable that differs is listed with what changed
  about it. Breaking means a mapping written for the older version can stop rendering or render a record the newer
  version does not accept: a variable removed, its type, format, pattern, unit context or writer changed, a property
  that became required where the older version already has the object holding it, or an entity type it no longer
  points to. Additive means something a mapping may now use: a new optional variable, a property no longer required, a
  new entity type it points to. Wording means only a title or description changed. The two schema files are shown side
  by side exactly as the release publishes them, and so is every shared schema they refer to that differs, paired by
  name across versions (`AbstractFacility` 1.0.0 against 1.1.0): a kind's own file can be the same in two releases
  while a schema it refers to changed under the same version, and that is where its template's changes come from. A release's versions are taken as they are; nothing is judged by
  the status a release gives them.
- **Save it.** Saving stores exactly the schema that was shown as a template version, and from then on mappings can
  pin it. The template's origin names the release, its commit and the file.
- **Import a file.** A schema of one's own, which the data definitions do not publish, is uploaded as a bundled schema
  file and saved the same way.

**Where templates are stored.** In the catalog database, table `osdu.Template`. The control plane runs as a
container whose disk does not survive a restart, and the catalog is where everything durable already lives. A row
holds the kind, the version, the schema itself, when it was saved, by whom, and where from.

**Template versions do not change.** A version is its content, so it can never be edited. Saving the same schema again
changes nothing. Saving a schema that differs from every saved version of its kind adds a new version beside them,
which a mapping only uses once it pins it. A version can be deleted only while no synced mapping pins it. A mapping
document that fails to load still pins the version its `template` block names, so a template cannot be deleted from
under a mapping that is waiting for a fix. The Templates page disables Delete, and says which pins hold it, while any
does; the API and the CLI refuse the delete with the mappings named.

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
description: Well logs, one record per logging run.

dataset:
  system: wells
  key: [dataset.source_project, dataset.log_id]
  label: "{dataset.wellbore_uwi} / {dataset.log_source} / run {dataset.log_run} ({dataset.log_id})"
  identity: [dataset.wellbore_uwi, dataset.log_id]   # indexed for lookup; never in the record

parameters:
  dataPartition: { required: true }
  aclOwner: { required: true }

searches:
  Wellbore:
    kind: "osdu:wks:master-data--Wellbore:*"
    schema: { kind: osdu:wks:master-data--Wellbore:1.3.0, version: 58d6bdbd9d066a06 }

mappings:
  - target: osdu.acl.owners
    static: ["{param.aclOwner}"]

  - target: osdu.data.Name
    source: dataset.log_source
    modifiers: [trim]

  - target: osdu.data.SamplingInterval
    source: dataset.index_increment
    appliesWhen: dataset.depth_coding is REGULAR

  - target: osdu.data.WellboreID
    source: search.Wellbore.id
    findBy:
      - search.Wellbore.data.FacilityName = dataset.wellbore_uwi
      - search.Wellbore.data.NameAliases.AliasName = dataset.wellbore_uwi

  - target: osdu.data.Curves
    source: dataset.curves

  - target: osdu.data.Curves[].CurveUnit
    source: cache.UnitOfMeasure.id
    findBy: cache.UnitOfMeasure.Code = dataset.curves.curve_unit
    modifiers:
      - replace: cache.RecallUnits      # the source's unit spellings, a dictionary held in the cache

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
| `dataset.system` | The source system. It enters the delivery key, and so the OSDU id: two mappings that deliver the same rows into the same entity type and partition need different systems or keys (the OSDU id carries the entity type, not the kind's version), because one OSDU record belongs to one flow ([ledger.md](ledger.md#one-source-several-flows)). |
| `dataset.key` | The dataset columns that identify a record, in order. The delivery key, and so the OSDU id, is derived from them. |
| `dataset.label` | Optional display text for the ledger and the GUI, with `{dataset.column}` tokens. It never enters the record. |
| `dataset.identity` | Optional list of the dataset's own columns whose values identify the record to a person (a wellbore id, a well name, a log id). The ledger indexes every value, so the Records page finds the record by any of them across every flow, without knowing which flow delivered it. Search only: like the label, an identity never enters the record or its hash. |
| `parameters` | Values the flow supplies under `render.parameters`. `dataPartition` is always declared. |
| `searches` | The record sets the mapping's `search.` sources look in, each with the saved schema that says how its kind is indexed. See [Searches](#searches). |
| `mappings` | The entries, described below. |
| `fixtures` | Example rows and the exact record each must render to, with the answers they assume the platform gives to the searches they make. Every run checks them before rendering. |

### An entry

Every entry has one `target` and one input. The input is either `source` or `static`.

| Key | Meaning |
| --- | --- |
| `target` | The template variable to fill, such as `osdu.data.Name` or `osdu.data.Curves[].CurveID`. |
| `source` | Where the value comes from. The first word says where: `dataset`, `cache` or `search`. |
| `static` | A fixed value: a string, a number, a boolean, a list or an object. `{param.name}` tokens are replaced with the flow's parameter values. |
| `findBy` | Only with a cache or a search source. Which record to read. |
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
| `cache.<Type>.<field>` | A field of that cached record, such as `Name` or `NameAliases.AliasName`. |
| `search.<name>.id` | The OSDU id of the one record a search of the platform finds by `findBy`, in the same reference form. See [Searches](#searches). |

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

### Searches

Some references point at records that are business data rather than a vocabulary. A partition's wellbores grow without
bound, change daily and number in the hundreds of thousands; capturing them all into the cache so that each record can
name one costs more every day and is always behind. A mapping resolves such a reference by searching the platform, one
value at a time, for the one record that holds exactly that value.

```yaml
searches:
  Wellbore:
    kind: "osdu:wks:master-data--Wellbore:*"
    schema:
      kind: osdu:wks:master-data--Wellbore:1.3.0
      version: 58d6bdbd9d066a06
    description: The partition's wellbores, found by name or by any of their aliases.

mappings:
  - target: osdu.data.WellboreID
    source: search.Wellbore.id
    findBy:
      - search.Wellbore.data.FacilityName = dataset.wellbore_uwi
      - search.Wellbore.data.NameAliases.AliasName = dataset.wellbore_uwi
```

| Key | Meaning |
| --- | --- |
| `searches.<name>` | The name entries read the search by, as `search.<name>.id`: letters, digits, underscores and hyphens. One search per kind. |
| `kind` | The kind searched: one entity type, at one version or at every version (`*`). |
| `schema.kind`, `schema.version` | The saved template of that entity type whose schema says how its properties are indexed, pinned the way the mapping pins its own template. When `kind` names a version, the schema is of that version. |
| `description` | Free text. |

A search source reads `id` only: the OSDU id of the record found. Its `findBy` lines name properties under `data`,
written as the schema names them, and are tried in order; the first line that finds exactly one record wins, and a line
is asked only when every line before it found nothing.

Each line's query is built from how the pinned schema has the platform index the property, never from the path alone.
The rules are the OSDU indexer's and search service's own, read from their source and kept in
`osdu/src/SqlFlow.Delivery.Search`:

| The property is | The query asks |
| --- | --- |
| a string, or a list of strings | its `keyword` sub-field, which holds the whole value: `data.FacilityName.keyword:"NO 15/9-F-1"` matches that value exactly, case included |
| inside an array the schema marks `x-osdu-indexing: nested` | the array through the service's nested form: `nested(data.NameAliases, (AliasName.keyword:"15/9-F-1"))` |
| inside an array marked `flattened`, or a legacy link whose pattern starts `^srn` | the property itself, which the platform stores as a keyword |

A property the platform cannot match exactly fails the mapping's check, naming the reason: a date or a number, an
object, an array of objects with no indexing hint (the platform does not index inside one), a nested array inside a
nested array, or a property the schema does not have. A value that cannot be asked for is refused on its row rather than
sent: an empty value, a value longer than the 256 characters the keyword sub-field keeps, the text `null` (what the
platform stores for a property that has no value), a control character, or text the search service would read as query
syntax even inside quotes: `nested(` anywhere, and inside a nested query unbalanced parentheses or a word ending in AND,
OR or NOT before a colon.

| The platform answers | The entry |
| --- | --- |
| Exactly one record | Renders its id. |
| No record, on every line | Holds the record when required, and is left out otherwise, as a cache miss is. |
| Several records | Holds the record whatever `required` says, naming the first five. |
| No record exactly, and several once case is ignored | Holds the record whatever `required` says, naming them. |
| The query is refused (400) | Holds the record whatever `required` says, quoting the service. |
| A line could not be asked, and no other line found the record | Holds the record whatever `required` says: a value that cannot be searched for proves nothing about whether the record exists. |
| Nothing: the platform is unreachable, refuses the credentials, or fails past the flow's retries | Fails the run. A missing answer is never taken for "no record". |

A value that already is an OSDU id of the searched entity type names its record without a search; an id of another
entity type holds the record.

The `keyword` sub-field keeps the value as written, so an exact match respects case. A partition can also have its
indexer keep a lowercased copy of every text property, the `keywordLower` sub-field. Whether it does is one of the
partition's system properties, `featureFlag.keywordLower.enabled`, which every capture of the partition's cache reads
from the platform (see [the partition cache](documents.md#the-partition-cache)). Where it is on, a line that finds no
record exactly asks once more on the copy, `data.FacilityName.keywordLower:"NO 15/9-F-1"` (the platform lowercases the
value it is asked for as it did the values it indexed), and:

- an exact answer always wins: the copy is asked only when no record holds the value exactly;
- one record once case is ignored renders its id;
- several hold the record whatever `required` says, since codes that differ only by case are different records. Make the
  incoming value exact with a `replace` modifier;
- none is no record on that line, and the next line is asked.

A partition whose setting is off, or unknown because no capture has read it or its services do not publish it, is asked
exact questions alone, and so is a host without the module's database. A property kept as a keyword (a legacy link, or a
value inside a flattened array) has no lowercased copy, and neither does the value `null` in any case, which the copy
holds for every record without a value; those are asked exactly alone.

The render itself does no I/O. A row whose lookup the run has not asked yet renders unfinished, naming the question; the
plan collects a batch's questions, asks each distinct one once, and renders the waiting rows again, so a row whose first
line found nothing asks its next line in the next round. Answers are kept for the run, found or not, so the thousandth
log of one wellbore asks nothing, and at most eight queries are in flight at a time. Each query asks for the ids of at
most five matching records, under the flow's own target, credentials and `data-partition-id`: a search finds only what
the flow's identity may view in the partition it delivers to.

- The search index is eventually consistent: a wellbore delivered moments ago is found once the platform's indexer has
  picked it up. A record held because its wellbore was not found stays held until it is released or its row changes,
  like any other held record.
- The render context names the mapping version, which pins the searches and their schemas, and the state of the
  partition's system properties the lookups rely on, but not the answers. A delivered record is rendered again when its
  row, its mapping or the partition's `keywordLower` setting changes, not when the platform's wellbores do. A mapping
  that only searches renders against no version of the cache, so a capture that changes reference data it never reads
  renders none of its records again.
- In lineage the flow reads the searched kind, so the flow that delivers wellbores to the partition is ordered before a
  flow that searches for them.

A fixture declares the answers it assumes, and renders against them without asking the platform, so it checks the
mapping rather than the platform's data of the day. An answer without `id` says the platform holds no such record. A
fixture whose render asks something it does not answer fails, naming the answer to add:

```yaml
fixtures:
  - name: L-1001, three curves in metres
    searches:
      - { search: Wellbore, field: data.FacilityName, value: OSDU-DEV-1-A, id: "dev:master-data--Wellbore:OSDU-DEV-1-A" }
    record:
      wellbore_uwi: OSDU-DEV-1-A
```

### Modifiers

Modifiers change incoming dataset values only. On a cache or a search source they change the `findBy` value before it
is compared or searched for. Cache values and platform records are OSDU's own and are never modified.

| Modifier | Written as | Incoming value | Result |
| --- | --- | --- | --- |
| trim | `- trim` | `" STAT_COMP "` | `"STAT_COMP"` |
| upper, lower | `- upper` | `"gapi"` | `"GAPI"` |
| split | `- split: { separator: ",", part: 1 }` | `"MAIN,REPEAT"` | `"MAIN"` |
| replace | `- replace: { GAPI: gAPI, NONE: ~ }` | `"GAPI"`, `"NONE"` | `"gAPI"`, no value |
| replace from the cache | `- replace: cache.CurveClasses` with `field: curve_family` | `"GR"` | `"Gamma Ray"`, as the cached row keyed `GR` gives it |
| equals | `- equals: REGULAR` | `"REGULAR"` or `"DISCRETE"` | `true` or `false` |
| date | `- date` or `- date: dd.MM.yyyy` | `"01.09.2026"` | `"2026-09-01T00:00:00Z"`, or `"2026-09-01"` where the template takes a date |
| number | `- number` or `- number: { decimal: ",", group: " " }` | `"1 234,5"` | `1234.5` |

`part` counts from one. A separator of a single space splits on any run of whitespace. `replace` matches the trimmed
value by the cache's rules (an exact key, then the one key that matches ignoring case); `~` replaces with no value; and
`otherwise`, written beside it, says what a value the table does not list becomes: unchanged when it is left out, no
value for `~`, or a text ([documents.md](documents.md#replace)). A replace reading `cache.<Type>` takes its table from the
partition's cache: a dictionary, an ingestion table or OSDU reference data, matched on `match` (by default the table's key)
and replaced by `field` (by default a lookup table's one field beside its key); every row it used is recorded, so a changed
entry tags the records built from it ([documents.md](documents.md#a-table-read-from-the-cache)). `equals` compares trimmed text and ignores case. `date` writes the RFC 3339 form the
property's `format` names: a full-date for `date`, and a UTC date-time otherwise. Without a format it reads ISO 8601
only and never guesses at a form such as `01/02/2026`; [documents.md](documents.md#date) has the rules. `number` reads
text written with the separators it is given; how any value becomes a number, an integer in its format's range, or text
is in [documents.md](documents.md#number).

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
| No record on the platform matches a search, on any line | Record held | Variable left out |
| Several records on the platform match, the query is refused, or a value could not be searched for and nothing was found | Record held | Record held |
| A repeater's child dataset has no rows | Record held | Variable left out |
| `appliesWhen` is false | Variable left out | Variable left out |

`required: false` never introduces a value. A held record is never sent, and the ledger records the reason.

### What the record contains

The engine starts from nothing, writes `id` and `kind`, then writes each entry's value at its target. Types come from
the template: `"1000"` becomes the number `1000` where the schema says number. A value that cannot take the schema's
type holds the record. A variable with no entry, an entry that does not apply, and an optional entry with no value
are all left out, and so is an object or array left with nothing in it.

### The record shape

The Mappings page shows, next to a mapping's YAML, the shape of the records it renders, without a source row or a
cache. The shape is drawn by the renderer delivery uses, so `id`, `kind`, the envelope, the nesting, the arrays and the
types are the ones a render writes:

- A value read from the dataset or the cache is a placeholder naming the type the template gives the property and where
  the value comes from, with its modifiers and whether it is optional or conditional:
  `"SamplingStart": "<number from dataset.index_min>"`,
  `"WellboreID": "<string from search.Wellbore.id by data.FacilityName/data.NameAliases.AliasName = dataset.wellbore_uwi>"`.
- A static value shows as it renders, with the parameter values given on the page. A parameter without a value shows as
  its `{param.name}` token, in static values and in the partition of `id`.
- A repeated array has one item, and a note says it takes one item per row of its child dataset.
- A list of values filled from one source is a list of one placeholder, as a render writes a list of one.

Nothing is read, rendered for delivery or stored. `POST /api/v1/delivery/mapping-builder/shape` draws the same for any
mapping document.

### What a mapping covers

The Mappings page shows a mapping as the template it pins with the mapping laid over it: the template's variables as
the record's tree, each row carrying a glyph for whether the mapping fills it, and nothing more. What fills it is read
on the row's hover, and whole beside the tree: the entry drawn as the pipeline that fills it (`dataset.facility_name`
then `trim`; `search.Wellbore.id` found by `data.FacilityName = dataset.wellbore_uwi`; `static ["NO"]`), with each lookup
line in the order it is tried, the modifiers as the steps they are, the condition, and whether the value may be left
out. The filter matches that as well as a variable's path and description, so a source column, a cached type or a
modifier answers with the variables it reaches. A mapping pinning a template version nobody saved has no tree to lay
itself over, and lists its own entries instead.

It opens as an overview: what the mapping fills, every required variable a check names, and the holders on the way to
them, so the first read answers what a mapping is doing without a click. A property required inside an object nothing
fills is left out, as it is left out of the findings: the record holds no such object. Show everything adds the rest of
the template, and Show required narrows it to what the schema demands.

Two switches ask about the whole template rather than narrowing the overview, and they answer two different questions:

- **Show missing** is the validation: what this record requires and the mapping does not fill on every row, which is
  every variable a check names. An empty tree is the answer that the mapping satisfies the schema, and the tree says
  so in words. It is the same set the counts line calls "required missing", and the same set the gate stops a delivery
  for wherever the gate looks.
- **Show unfilled** is the wider question: every variable of the template nothing fills, whether or not the schema
  asks for it, which is what a mapping could carry and does not. An entry that may leave a value out is not one of
  those, because the mapping does fill that variable; what such an entry costs is said where the schema requires it,
  by the check that names it.

An entry filling a free key of an object that takes them (`osdu.tags.DeliveredBy`) is a row under that object, and an
entry the template does not let a mapping fill is named above the tree, so no entry of the document goes unseen.

Each row says how the document reaches its variable.

- **Filled** is a static value, or a required entry that always applies: the record carries it on every row.
- **Sometimes** is an entry that may leave it out, `required: false` or an `appliesWhen` that only holds on some rows.
- **Not filled** is a variable no entry names, and nothing fills anything it holds. It is left out of the record.

**An object is only as good as the weakest thing it promises.** It takes the worst state among the variables it holds
that the mapping fills or the schema requires: one property the schema requires and nothing fills makes the object
holding it missing, however many of its siblings are filled, and one property filled on some rows only makes it that.
A property nothing fills and nothing requires promises nothing, so it is passed over rather than dragging its object
down: `osdu.acl` reads as filled when its `owners` and `viewers` are, whatever else the schema allows beside them.

An object's own entry decides it only when nothing inside it is promised. A repeater filling an array that may be left
out still carries items whose properties are always written, so the branch reads as filled and the entry alone is
marked optional, beside the tree where the entry is read.

What a variable shows is not what its findings are judged on: a required property is missing because nothing writes
it, never because something beside it is.

A required variable the mapping leaves empty is the point of the view. The errors are the delivery gate's own
(check 5 below, the required properties of `data`); the other required variables are warnings, because the gate does not
stop a delivery for them today. A property required inside an object nothing fills is not reported at all: the record
holds no such object, so nothing is missing from it. What OSDU Delivery writes, what OSDU sets and a list inside a
repeated item are left out of the view, because no mapping fills those.

One rule decides both: the gate runs it (`MappingCoverage.RequiredIssues`) as its check 5, and the view runs the same
rule over the whole template. `POST /api/v1/delivery/mapping-builder/coverage` answers the same for any mapping
document; nothing is rendered and no cache is read.

## Checks

When a mapping is read:

- The header keys, entry keys, sources, `findBy` lines, modifiers and conditions parse, and each entry has exactly
  one input. Unknown keys are refused.
- No two entries fill the same target.
- Every `search.<name>` source names a search the `searches` block declares and reads `id`, and its `findBy` lines
  compare properties under `data`. Every declared search is read by an entry, no two look in one kind, and each pins a
  schema of the entity type it searches. A fixture's answers name a declared search, a property its `findBy` lines
  compare, and an id of the entity type searched, once per lookup.

Before any row is rendered (the preflight):

1. The pinned template version is saved in the catalog.
2. Every target is a variable of the template, with an agreeing shape: a repeater only on an array of objects, `[]`
   only under a repeater, a plain value only on a scalar or an array of scalars, an object only from `static`.
3. No entry fills `osdu.id`, `osdu.kind` or a property OSDU sets.
4. `osdu.acl.owners`, `osdu.acl.viewers`, `osdu.legal.legaltags` and `osdu.legal.otherRelevantDataCountries` are
   static, non-empty and free of repeats, so the legal service can check the tags before a run.
5. Every property the schema requires has an entry, and none of those entries is `required: false`. A required
   object may instead be filled by entries for its properties, at least one of them static or required without
   `appliesWhen`.
6. Every dataset column and child dataset exists in the flow's ingestion tables, when those are known.
7. Every cache type exists in the cache, and holds the fields `findBy` compares and the field the source reads. Every
   search's pinned schema is saved, and every property its `findBy` lines compare can be matched exactly on the
   platform, as that schema says the property is indexed.
8. A cache source resolves to the entity type the schema expects for its target. `osdu.data.WellboreID` can only be
   read from a cached type of `master-data--Wellbore`.
9. A static value on a relationship property exists in the cache when the cache holds that entity type.
10. Every fixture renders exactly as declared, against the search answers it declares.

## The mapping builder

The GUI's Mapping builder answers "I want to populate this OSDU kind; how do I write the mapping?":

1. Pick the repository the mapping will live in, a saved template, and the partition whose cache the mapping is checked
   against (the Partition cache picker lists every partition a synced cache flow fills, and defaults to the partition
   the repository's delivery flow delivers to, its `target.headers.data-partition-id`). The mapping itself never names
   a cache: it reads whichever partition the flow rendering it delivers to. A kind without a template is browsed,
   looked at and saved on the Templates page first.
2. The page lists every template variable with its type, requiredness, relationship and OSDU description.
3. **Entries are prefilled from the cache.** Every variable that points to an entity type the picked partition's cache holds
   gets a cache entry: `source: cache.<Type>.id` and a `findBy` on the type's first cached field. The person completes
   the incoming side.
4. For each variable the person chooses dataset, repeater, cache, search (once the mapping declares a search) or
   static, and adds modifiers, a condition and the required flag.
5. The page shows the resulting YAML, checks it against the template and the current version of the picked partition's cache, and either
   copies it or opens a pull request against the repository through the existing proposal path.
6. The check renders with the values a run of the repository's delivery flow would: what the flow writes under
   `render.parameters`, and for each of `dataPartition`, `aclOwner`, `aclViewer` and `legalTag` it leaves out, the
   kind's own reference, resolved from the repository's central configuration and then the control plane's
   environment. Each value read from a reference names it; a reference the control plane cannot resolve (one only the
   nodes hold) is named with a request for a value to check with. What is typed there is never written into the mapping.

An existing mapping opens in the builder with its entries filled in.

## What is removed

- The property list format: `source`/`identity`/`envelope`/`properties`/`definitions` blocks, per-property
  `examples`, and the `constant`, `template`, `map`, `reference`, `lookup` and `deliveredReference` transforms.
  `replace`, `equals`, `split`, `date` and cache sources cover what the sample estate used, and what the `map` and
  `lookup` transforms did now lives in the partition's cache: a table a replace reads (`replace: cache.<Type>`) from a
  dictionary, an ingestion table or OSDU reference data, and a cache source a `findBy` resolves.
- Schema snapshots in the repository's snapshot store, and the `sqlflow snapshot <flow> schema` verb. Templates
  replace them.
- Manual submission: the API that took records in a request, the page and dialog that sent them, and the source contract
  that described a flow's columns to a sender. Records delivered by hand go through the flow's pre and ingestion flows
  like any other ([design.md](design.md) section 3.3).

Rendering now needs the catalog, because templates live there. A CLI run without a catalog can still validate
documents. The reference snapshots, which this change left in the repository, have since gone the same way: what is cached is
defined by cache flows, and the versions of each partition's cache live in the catalog ([documents.md](documents.md#cache-flow)).

## Implementation plan

1. **Catalog.** Add `DeliveryTemplate` to the EF model (schema `osdu`), and a template store over it:
   load by kind, save immutably, list, delete while unused. This is a schema change, so development catalogs are
   re-minted.
2. **Template model.** Build the variable tree from a schema: paths, types, requiredness, relationships, unit
   contexts, descriptions, and the variables a mapping may not fill.
3. **Mapping format.** Replace the mapping model and loader with the header, entries, sources, `findBy`, modifiers,
   `appliesWhen`, `required` and fixtures, each parsed with errors that name the file and the entry.
4. **Engine.** Rewrite the renderer's walk over entries, keeping type coercion, cache resolution, cache usage
   tracking and hashing as they are. Rewrite the preflight with the checks above. Resolve templates from the catalog
   in the render resolver. Adapt the planner, the legal tag check and the catalog sync summary.
5. **Browse, fetch and save.** Read the OSDU data definitions' releases, a release's index of record kinds, and one
   kind's schema bundled with the files it refers to, a save path that stores a fetched or imported schema, and CLI
   verbs `sqlflow template capture | import | list | show`.
6. **API.** List the data definitions' releases and record kinds and fetch one for preview; list, show, save, import and delete templates; draft a mapping for a repository and a template with
   cache prefills; convert between the builder's entries and YAML; check a mapping against its template and the
   repository's cache.
7. **GUI.** A Templates page to browse OSDU schemas, view a schema as a template and save it, and the Mapping
   builder, with the pull request action.
8. **Samples, tests and docs.** Convert both sample mappings, proving the converted WellLog mapping renders every
   record of the sample estate byte for byte as before. Import the sample schemas as templates in the tests and the
   GUI end-to-end setup. Rewrite the mapping tests, add template, builder and end-to-end coverage, and update the
   documentation that describes mappings.

Verification: `dotnet build SqlFlow.sln` with zero warnings, `dotnet test SqlFlow.sln`, `npm run build` in `gui/`
(which type-checks the app and the end-to-end specs; `gui/` has no lint script), and the GUI end-to-end suite.
