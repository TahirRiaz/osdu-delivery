---
id: delivery-concept-templates
title: "Templates: OSDU schemas saved as the records a mapping fills, with versions mappings pin"
type: concept
summary: "What a template is, where it comes from (the OSDU data definitions or a file), how its version is a content hash a mapping pins, and comparing versions."
keywords:
  - template
  - osdu schema
  - template version
  - data definitions
  - capture
  - import
  - pin
  - template variables
  - compare versions
  - breaking change
  - kind
  - sqlflow template
  - osdu:schemarepository
  - save template
related:
  - delivery-flow-mapping
  - delivery-cli-template
  - delivery-concept-preflight
  - delivery-flow-mapping-lookups
  - delivery-guide-writing-a-mapping
  - delivery-concept-lineage
  - delivery-concept-architecture
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Templates/OsduTemplate.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateStore.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateSources.cs
  - osdu/src/SqlFlow.Delivery/Templates/OsduDataDefinitions.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateComparison.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplatePath.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/SchemaSnapshot.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryModuleOptions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/gui/src/features/delivery/TemplatesPage.tsx
  - osdu/gui/src/features/delivery/TemplatesBrowseTab.tsx
  - osdu/samples/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
  - osdu/samples/templates/osdu_wks_master-data--Wellbore_1.3.0.json
---

# Templates: OSDU schemas saved as the records a mapping fills, with versions mappings pin

A template is an OSDU schema saved in OSDU Delivery as the record of one kind with a variable for every property the
schema declares. A [mapping](../flow/mapping.md) fills one template: it pins the template's kind and version under
`template`, and says for each variable it fills where the value comes from. Nobody edits a template; it is generated
from the schema, and a saved version never changes. You save a template before you write the mapping that fills it,
from the Templates page or with `sqlflow template`, and you save a new version when OSDU publishes a new version of the
kind and you mean to move to it.

```yaml
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  version: 26a3c3441882db4f
```

## What a template holds

Every property of the schema becomes a variable, named by its path in the record and prefixed with `osdu.`; `[]` marks
the step into the items of an array of objects. Each variable carries the schema's type, whether the schema requires
it in the object that holds it, the entity types it points to (`x-osdu-relationship`), its pattern, its unit context
(`x-osdu-frame-of-reference`), its title and OSDU's description. A few variables of the WellLog 1.4.0 template:

| Variable | Shape | What the schema says |
| --- | --- | --- |
| `osdu.id` | string | written by OSDU Delivery |
| `osdu.kind` | string | required; written by OSDU Delivery |
| `osdu.acl.owners` | list of string | required |
| `osdu.legal.legaltags` | list of string | required |
| `osdu.tags` | object with free keys | each key takes a string; a mapping fills `osdu.tags.<name>` |
| `osdu.data.Name` | string | |
| `osdu.data.WellboreID` | string | points to `master-data--Wellbore` |
| `osdu.data.TopMeasuredDepth` | number | unit context `UOM:length` |
| `osdu.data.VerticalMeasurement.VerticalMeasurement` | number | inside an object |
| `osdu.data.Curves` | list of objects | a `$forEach` or a list of objects fills it |
| `osdu.data.Curves[].CurveUnit` | string | points to `reference-data--UnitOfMeasure` |
| `osdu.data.ExtensionProperties` | open object | a mapping lays out anything inside it |

A variable takes one of these shapes:

| Shape | Filled by |
| --- | --- |
| A value (text, number, integer, boolean) | One value node or literal. |
| A list of values | A literal list, a value node (one value becomes a list of one), or a [list of values](../flow/mapping-values.md#lists-of-values). |
| An object | The nodes of the properties it holds, or a `$value` written whole. |
| A list of objects | A `$forEach` over a child dataset, or a [list of objects](../flow/mapping-values.md#lists-of-objects). |
| Whole | An object or list the schema does not break into properties (a list of a choice of shapes, such as `meta` or `data.GeoContexts`): a literal written whole, or a cached field holding one. An object with free keys (`tags`) takes entries under any key, and an [open object](../flow/mapping-values.md#open-objects) (`data.ExtensionProperties`) takes anything laid out inside it. |

Some variables are not a mapping's to fill. `osdu.id` and `osdu.kind` are written by OSDU Delivery (the id from the
mapping's key, the kind from the template), and `osdu.version`, `osdu.createTime`, `osdu.createUser`, `osdu.modifyTime`
and `osdu.modifyUser` are set by OSDU when the record is stored. A list of objects inside the items of another array is
listed for reference and cannot be filled, since a path steps into one array at most. The walk follows nested objects
twelve levels deep.

## Template versions

A template's version is its content: the first 16 hexadecimal characters of the SHA-256 of the bundled schema written
as canonical JSON (keys sorted, no whitespace). Two consequences follow.

- **A saved version never changes.** Saving the same schema again changes nothing (`template <kind> version <version>
  was already saved`). Saving a schema that differs from every saved version of its kind adds a new version beside them,
  which a mapping uses only once it pins it.
- **The version identifies the schema exactly**, whatever release or file it came from, so a mapping always renders
  against the schema it was written for. The render refuses any other
  (`the mapping fills template <kind> version <v>, but it was given template <kind> version <w>.`).

The template version enters every record's render context. Moving a mapping to another template version, like changing
the mapping itself, makes the next run read the whole scope again; each record's own document hash decides whether it
is sent ([change detection](change-detection.md)).

Templates live in the module's database, in the `osdu` schema (`[osdu].[Template]`): the kind, the version, the bundled
schema, when it was saved, by whom, and where from. They are not files of the repository and not tied to a partition:
one saved version serves every partition and every repository. The control plane's disk does not have to survive a
restart for them, since the database is where everything durable lives ([architecture](architecture.md)).

## Where templates come from

### The OSDU data definitions

The canonical OSDU schemas are the OSDU data definitions, the Open Group's public repository
(<https://community.opengroup.org/osdu/data/data-definitions>). A release is one of its version tags (`v` and a dotted
version); the newest is the default everywhere. A release's `Generated/SchemaStatus.json` lists every kind it publishes,
each with its status (published, in development or obsolete). Only record schemas are offered: the abstract building
blocks, the manifest and the content schemas declare no `data`, so no template can be laid out from them. A kind's
schema is the file that declares it, bundled with every shared schema it refers to.

The control plane keeps a local copy of the repository on disk. The first time a release is read, its whole `Generated`
folder is downloaded as one archive through the repository's GitLab API and unpacked, and every file of it is read from
disk from then on, across restarts. The release list is kept beside it and read again when it is older than
`RefreshMinutes`, and whenever someone presses **Sync with the repository** (any signed-in user). The newest release is
downloaded when the control plane starts. The schemas are public, so no flow, credential or node is involved.

On the GUI's Templates page, the **Browse OSDU** tab:

1. **Picks a release and searches the record kinds it publishes.**
2. **Looks at a schema**: a kind laid out as a template, every variable with its type, requiredness, relationships, unit
   context and OSDU's description, read from the release and bundled; nothing is stored yet. The kind links to its file
   in the repository.
3. **Compares versions** of a kind ([below](#comparing-versions)).
4. **Saves it** (**Save template**, any signed-in user): exactly the schema that was shown is stored as a template version.
   Its origin names the release, the first 12 characters of its commit and the file:
   `OSDU data definitions <release> (<commit>) Generated/<path of the kind's file>`.

`sqlflow template capture --kind <kind> [--release <tag>]` saves a kind's schema the same way from the CLI, which always
reads the public repository and keeps its copy in the default place. With `--out <file.json>` it also writes the bundled
schema to a file, so a repository can keep the schemas its mappings pin and import them again without the network
([template](../cli/template.md)).

### A schema file

A schema the data definitions do not publish (a kind of your own, or a version not released yet) is imported as a
file, on the Templates page's **Import file** tab or with `sqlflow template import <schema.json> --kind <kind>`:

- A bundled file, every `$ref` pointing into its own `definitions` (or `$defs`), is saved as it is. That is the form
  `capture --out` writes, and the form `osdu/samples/templates` keeps the sample schemas in.
- A file as the data definitions publish it refers to the shared schemas beside it (`../abstract/...`). Those are read
  from a release (`--release`, the newest by default) and bundled in exactly as a capture bundles them; the origin then
  also names that release.
- `sqlflow template import --from-dir <dir> --kind <kind>` bundles the kind from a local checkout of the data definitions
  (its `Generated` folder).

Whatever the source, a schema is saved only when it describes the kind named (`x-osdu-schema-source`), refers to nothing
outside itself once bundled, and declares the `data` property every OSDU record carries:

| Problem | Message |
| --- | --- |
| Another kind | `<where>: the schema describes '<source>', not '<kind>'.` |
| A reference outside the file | `<where>: the schema refers to '<ref>' outside itself. A template is saved from a bundled schema, where every reference is resolved into its definitions.` |
| A missing definition | `<where>: the schema refers to definition '<name>', which it does not contain.` |
| No `data` | `<where>: the schema declares no 'data' property, so it does not describe an OSDU record.` |
| Not JSON | `<where>: the schema is not valid JSON (...).` |
| A kind that is not a record kind | `Kind '<kind>' must be 'authority:source:entityType:major.minor.patch'.` |

## How mappings pin a template

- **A mapping pins its own template** under `template` (`kind` and `version`), and a delivery flow's run loads exactly
  that version. A version that is not saved stops the run before anything renders:
  `mapping WellLog@1.0.0 pins template <kind> version <v>, which is not saved. Save it on the Templates page, or with 'sqlflow template import'.`
- **A search pins one too.** Each of a mapping's `searches` pins the saved template of the kind it searches
  (`schema.kind`, `schema.version`), which says how the platform indexes the properties its `$findBy` lines compare
  ([mapping lookups](../flow/mapping-lookups.md)). The preflight fails when it is not saved.
- **The preflight checks the mapping against the pinned template** before any row renders: every property is a variable
  of it, with an agreeing shape, and every variable it requires is filled ([preflight](preflight.md)).
- **Lineage reads the template** to tell which entity type a `ref` modifier written without one references. A mapping
  synced before its template was saved has that part of its lineage computed again by the first repository sync after
  the save ([lineage](lineage.md)).
- **A mapping that fails to load still pins** the version its `template` block names, so a template cannot be deleted
  from under a mapping that is only waiting for a fix.

## Deleting a version

A version can be deleted (**Delete** on the Templates page, `sqlflow template delete --kind <kind> --version <v>`, or
`DELETE /api/v1/delivery/templates`, any signed-in user) only while no synced mapping pins it as its own template. Otherwise
the delete is refused, naming up to 20 of the mappings, and the API answers 409:

```text
The template <kind> version <v> is pinned by mapping(s) WellLog@1.0.0, so it cannot be deleted. Move those mappings to another template version first.
```

The Templates page does not offer Delete for a version a synced mapping pins. Only a mapping's own `template` counts as a
pin: a version that only a `searches` block pins can be deleted, and the mappings that search with it then fail their
preflight until it is saved again.

## Comparing versions

Before moving a mapping to a newer version of its kind, compare the two. On the Browse OSDU tab, **Compare versions**
picks two versions of one kind, each from any release of the data definitions
(`GET /api/v1/delivery/templates/osdu/compare?fromRelease=&fromKind=&toRelease=&toKind=`). The verdict comes first: the
same file, files that differ only in the version identifiers each carries (`x-osdu-schema-source` and `$id`), whether
the two save as the same template version, and how many variables changed of each impact:

| Impact | Means | Changes of this impact |
| --- | --- | --- |
| Breaking | A mapping written for the older version can stop rendering, or render a record the newer version does not accept. | A variable removed; its type or shape, format, pattern, free-key type, unit context or writer changed; a list of objects that became nested inside another array; a property that became required; an entity type it no longer points to; a new required property inside an object the older version already has. |
| Additive | Something a mapping may now use. | A new optional variable (or a new required one inside a new object); a property no longer required; a new entity type it points to. |
| Wording | Only how it reads. | A title or a description. |

Every variable that differs is listed with each field that changed and its value on both sides. The two schema files
are shown as the release publishes them, and so is every shared schema they refer to that differs, paired by name
across versions (`AbstractFacility` 1.0.0 against 1.1.0): a kind's own file can be the same in two releases while a
schema it refers to changed, and that is where its template's changes come from. A release's statuses are shown but
judge nothing.

Moving a mapping to the newer version: save the newer template, give the mapping a new version that pins it, fix what
the comparison calls breaking, check it ([check](../cli/check.md)), and run the flow's `plan` operation to see which
records would be delivered again before delivering ([running an OSDU flow](../cli/run.md)).

## Where to work with templates

| Task | GUI (Templates page) | CLI | API (`/api/v1/delivery`) |
| --- | --- | --- | --- |
| List saved versions, with how many synced mappings pin each | Saved tab | `sqlflow template list` | `GET /templates` |
| Lay one version out as variables | Saved tab, a row | `sqlflow template show --kind <kind> [--version <v>]` | `GET /templates/detail?kind=&version=` |
| Read the bundled schema | | | `GET /templates/schema?kind=&version=` |
| List releases, list a release's record kinds | Browse OSDU | | `GET /templates/osdu/releases`, `GET /templates/osdu/schemas?release=` |
| Read a kind from a release | Browse OSDU | | `GET /templates/osdu/schema?release=&kind=` |
| Lay out a schema without saving it | Browse OSDU, Import file | | `POST /templates/preview` |
| Compare two versions of a kind | Browse OSDU, Compare versions | | `GET /templates/osdu/compare` |
| Read the release list again and download a release | Sync with the repository | | `POST /templates/osdu/sync` |
| Save a version | Save template | `sqlflow template capture`, `sqlflow template import` | `POST /templates` |
| Delete a version | Delete | `sqlflow template delete` | `DELETE /templates?kind=&version=` |

Reading is open to any signed-in user; syncing the data definitions is an operate action; saving and deleting change
what mappings can pin, so they are author actions ([authentication and identity](authentication-and-identity.md)). Every
CLI form needs the module's database (`--db <conn-ref>`, or the catalog variable). The mapping builder starts a mapping
from a saved template ([writing a mapping](../guides/writing-a-mapping.md)).

## Configuration: Osdu:SchemaRepository

The control plane's local copy of the data definitions is configured in the section `Osdu:SchemaRepository`. Point both
URLs at a mirror when community.opengroup.org is out of reach.

| Key | Default | Meaning |
| --- | --- | --- |
| `ApiUrl` | `https://community.opengroup.org/api/v4/projects/osdu%2Fdata%2Fdata-definitions/` | The GitLab API URL of the data definitions project. An absolute http or https URL. |
| `WebUrl` | `https://community.opengroup.org/osdu/data/data-definitions/` | The project's web page, which the Templates page links schema files on. |
| `CacheDirectory` | `sqlflow/osdu-data-definitions` under the temp folder | Where the local copy lives. |
| `RefreshMinutes` | `1440` (a day) | How old the release list may be before it is read again; 1 to 10080. |
| `DownloadTimeoutMinutes` | `15` | How long downloading one release may take; 1 to 240. |
| `WarmOnStart` | `true` | Read the release list, and download the newest release when it is not on disk, when the control plane starts. |

The section is validated when the control plane starts, and a value outside its range stops it with the setting named
(`Osdu:SchemaRepository:RefreshMinutes must be between 1 and 10080 (a week).`). The data definitions answering with an
error make the Templates page say so (a 502, `OSDU data definitions unavailable`); a release or kind they do not hold is
a 404 (`The OSDU data definitions have no release '<tag>'; the latest is <tag>.`).

## Related

- [Mapping](../flow/mapping.md): the `template` block and what a mapping fills.
- [`sqlflow template`](../cli/template.md): capture, import, list, show and delete.
- [Preflight](preflight.md): checking a mapping against its template.
- [Writing a mapping](../guides/writing-a-mapping.md): from a saved template to a mapping.
