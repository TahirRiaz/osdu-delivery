---
id: delivery-cli-template
title: "sqlflow template: save, list, show and delete the OSDU schemas a mapping pins"
type: cli-command
summary: "Capture a kind's schema from the OSDU data definitions or import a schema file as a template, and list, show or delete saved templates."
keywords:
  - template
  - template capture
  - template import
  - osdu schema
  - data definitions
  - kind version
  - schema bundle
  - mapping template pin
  - release tag
  - osdu.template
  - offline schema
cliCommand: template
related:
  - delivery-concept-templates
  - delivery-flow-mapping
  - delivery-guide-writing-a-mapping
  - delivery-cli-check
  - delivery-guide-getting-started
  - delivery-concept-gui
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateStore.cs
  - osdu/src/SqlFlow.Delivery/Templates/OsduDataDefinitions.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateSources.cs
  - osdu/src/SqlFlow.Delivery/Templates/OsduTemplate.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/SchemaSnapshot.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryModuleOptions.cs
  - sqlflow/src/SqlFlow.Core/Runs/RunActors.cs
---

# sqlflow template

OSDU Delivery's command line is `sqlflow`: SQLFlow's CLI with the OSDU verbs added. `template` is one of those verbs. It
saves OSDU schemas as templates in the module's database, where a mapping pins one by kind and version, and lists, shows
and deletes the saved ones. What a template is, how a mapping pins it and how versions compare is in
[Templates](../concepts/templates.md); this page is the verb.

## Synopsis

```bash
sqlflow template capture --kind <kind> [--release <tag>] [--out <file.json>] [--db <conn-ref>] [--json]
sqlflow template import <schema.json> --kind <kind> [--release <tag>]       [--db <conn-ref>] [--json]
sqlflow template import --from-dir <dir> --kind <kind>                      [--db <conn-ref>] [--json]
sqlflow template list                                                       [--db <conn-ref>] [--json]
sqlflow template show --kind <kind> [--version <version>]                   [--db <conn-ref>] [--json]
sqlflow template delete --kind <kind> --version <version>                   [--db <conn-ref>]
```

## Description

A template is one OSDU kind's JSON schema, bundled so that every `$ref` points into its own `definitions`, and saved in
`[osdu].[Template]`. Its version is its content: the first 16 hexadecimal characters of the hash of the canonical schema.
Saving a schema that is already saved changes nothing; a schema that differs from every saved version of its kind is
saved beside them as a new version. A saved version never changes. A mapping pins one under `template` (its `kind` and
`version`), and `sqlflow check` and every run read the pinned version from the database, so a template has to be saved
before its mappings can be checked or delivered.

Every subcommand works on the database directly, not through the control plane, so it needs the catalog connection
(`--db`, else `${env:SQLFLOW_CATALOG_DB}`), or `SQLFLOW_OSDU_DB` when the `osdu` schema has a database of its own (see
[sqlflow db](db.md)). The GUI's Templates page saves templates through the control plane instead, into the same table.

Every saved template is checked the same way, whatever its source:

- the schema describes the kind asked for (its `x-osdu-schema-source`, when it declares one, is that kind);
- every reference is resolved inside the schema;
- it declares a `data` property, as every OSDU record does.

### Where `capture` reads from

`capture` reads the OSDU data definitions, the Open Group's public repository of the OSDU schemas
(`https://community.opengroup.org/osdu/data/data-definitions`), through its GitLab API. A release is a tag of that
repository named `v` and a dotted version; `--release` names one exactly, and without it the newest release is used. The
first time a release is read, its whole `Generated` folder is downloaded as one archive and unpacked into a local copy,
and every later read of that release comes from disk:

| Item | Value |
| --- | --- |
| Local copy | `sqlflow/osdu-data-definitions` under the user's temp folder, one folder per release commit. A control plane on the same machine shares it unless `Osdu:SchemaRepository:CacheDirectory` moves its own. |
| Release list | Kept on disk beside the copy and read again from the repository when it is older than a day. A release name the list does not hold makes it read the list again when the list is over a minute old. When the repository cannot be reached, a list read before is used. |
| Download timeout | 15 minutes per release; 2 minutes per page of the tag list. |
| Repository | Always the public one. The mirror settings (`Osdu:SchemaRepository:ApiUrl`, `WebUrl`) apply to the control plane only. |

The kind's file is the one under `Generated` whose `x-osdu-schema-source` is the kind. It is bundled with every shared
schema it refers to (`../abstract/...`) from the same release. The saved template's origin records the release, the
first 12 characters of its commit and the file:
`OSDU data definitions <release> (<commit>) Generated/<path>`.

### What `import` accepts

- **A bundled file** (every `$ref` already points into its own `definitions`, the form `capture --out` writes) is saved as
  it is, and reads nothing from the network. Its origin is `file <name>`.
- **A file as the data definitions publish it**, which refers to the shared schemas beside it, is bundled with those
  schemas read from `--release` (the newest when left out), exactly as `capture` bundles. Its origin is
  `file <name>, references from OSDU data definitions <release> (<commit>)`.
- **`--from-dir <dir>`** bundles the kind's schema from a local checkout of the data definitions, with no network
  access. `<dir>` is the checkout's `Generated` folder, the one holding the group folders such as `master-data`,
  `work-product-component` and `abstract`; the kind's file is read from `<group>/<Entity>.<version>.json` under it.
  A file not at its expected path is found by its file name anywhere under the folder. Its origin is
  `data definitions under <full path>`.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<subcommand>` | yes | One of `capture`, `import`, `list`, `show`, `delete` (case-insensitive). |
| `<schema.json>` | `import` without `--from-dir` | The schema file to save. `capture` takes no file and refuses one. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--kind <kind>` | OSDU kind | none | Required for every subcommand but `list`: `authority:source:entityType:major.minor.patch`, such as `osdu:wks:work-product-component--WellLog:1.4.0`. |
| `--release <tag>` | release tag | the newest release | `capture`, and `import` of an unbundled file: the release of the data definitions to read. |
| `--out <file.json>` | path | none | `capture` only: also write the bundled schema to this file (folders are created), so a repository can keep it beside the mapping and import it again without the network. |
| `--from-dir <dir>` | path | none | `import` only: bundle from a local checkout instead of a file. |
| `--version <version>` | template version | the most recently saved | `show`: the version to lay out. `delete`: required. |
| `--db <conn-ref>` | connection reference | `${env:SQLFLOW_CATALOG_DB}` | The catalog database, which holds the `osdu` schema unless `SQLFLOW_OSDU_DB` names its own. |
| `--json` | flag | off | Writes JSON instead of text (not for `delete`). |

## Behavior and output

### capture and import

Both print one line: `saved template <kind> version <version>` for a new version, or
`template <kind> version <version> was already saved` when the content is a saved version. With `--json` they print the
template as `{ kind, version, capturedUtc, capturedBy, origin, outcome }`, where `outcome` is `created` or `unchanged`.
`capture --out` writes the file before the template is saved, and notes `Wrote the bundled schema of <kind> to <path>` on
stderr, so the file is there even when the save then fails. The template records who saved it as
`cli:<user>@<machine>`.

### list

Every saved version, by kind and newest first:

```text
osdu:wks:master-data--Wellbore:1.3.0  58d6bdbd9d066a06  saved 2026-10-09 07:52:10Z by cli:alex@WS-0142  (OSDU data definitions <release> (<commit>) Generated/master-data/Wellbore.1.3.0.json)
osdu:wks:work-product-component--WellLog:1.4.0  26a3c3441882db4f  saved 2026-10-09 07:53:44Z by cli:alex@WS-0142  (file WellLog.1.4.0.json)
```

With none saved it prints `no templates saved yet`. `--json` prints the array of `{ kind, version, capturedUtc,
capturedBy, origin }`.

### show

One version laid out as a template: a header line `<kind> version <version>: <n> variable(s)`, then one line per variable
with its path, its shape and its notes. A shape is the value's type, `list of <type>`, `object`, `list of objects` or
`whole <type>`. The notes say `required` when the schema requires it, `written by OSDU Delivery` (the record id and kind)
or `set by OSDU` (what OSDU sets when it stores the record) for a variable no mapping fills, `points to <entity types>`
for a reference, and `unit <context>` for a value with a unit. With `--json` each variable is
`{ path, shape, type, itemType, required, role, relationships, unitContext, description }`, where `role` is `Mapping`,
`Engine` or `Osdu`.

### delete

Deletes one version and prints `deleted template <kind> version <version>`. A version a synced mapping pins is refused,
naming up to 20 of the mappings that pin it: move them to another version (and sync) first.

### Errors

| Message | Cause |
| --- | --- |
| `Kind '<kind>' must be 'authority:source:entityType:major.minor.patch'.` | `capture` or `import` with a `--kind` that is not an OSDU kind. |
| `'sqlflow template capture' takes no flow: it saves the kind's schema from the OSDU data definitions.` | A file was given to `capture`. |
| `The OSDU data definitions have no release '<tag>'; the latest is <tag>.` | `--release` names no release tag. |
| `The OSDU data definitions have no Generated/<path> at <release>.` | The release publishes no schema for the kind. |
| `The OSDU data definitions at <url> could not be reached for <what>: <cause>` | No network path to the repository. Use `import --from-dir` or a bundled file instead. |
| `<where>: the schema describes '<other kind>', not '<kind>'.` | The file is another kind's schema. |
| `<where>: the schema refers to '<ref>' outside itself. A template is saved from a bundled schema, where every reference is resolved into its definitions.` | A reference was left unresolved. |
| `<where>: the schema declares no 'data' property, so it does not describe an OSDU record.` | The file is not a record schema (an abstract building block, for example). |
| `The data definitions folder '<dir>' does not exist.` / `No schema file '<path>' under '<dir>'.` | `--from-dir` names the wrong folder. |
| `There is no saved template for '<kind>'.` / `There is no template <kind> version <version>.` | `show` or `delete` of something not saved. |
| `The template <kind> version <version> is pinned by mapping(s) <list>, so it cannot be deleted. Move those mappings to another template version first.` | `delete` of a pinned version. |

Each is printed as `ERROR  <message>` on stderr; a missing `--kind`, file or `--version` is a usage error followed by the
verb's usage lines.

## Examples

Save the schema a WellLog mapping pins, from the newest release, and keep the bundled file in the repository:

```bash
sqlflow template capture --kind osdu:wks:work-product-component--WellLog:1.4.0 --out templates/WellLog.1.4.0.json
```

```text
saved template osdu:wks:work-product-component--WellLog:1.4.0 version <16 hexadecimal characters>
```

The version printed is the one the mapping pins under `template.version`.

Save the same template on a machine with no internet access, from the file kept in the repository:

```bash
sqlflow template import templates/WellLog.1.4.0.json --kind osdu:wks:work-product-component--WellLog:1.4.0 --db '${env:SQLFLOW_CATALOG_DB}'
```

Bundle from a local checkout of the data definitions:

```bash
sqlflow template import --from-dir ~/src/data-definitions/Generated --kind osdu:wks:master-data--Wellbore:1.3.0
```

See what a mapping has to fill, then remove a version nothing pins any more:

```bash
sqlflow template show --kind osdu:wks:master-data--Wellbore:1.3.0
sqlflow template delete --kind osdu:wks:master-data--Wellbore:1.3.0 --version 58d6bdbd9d066a06
```

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| 0 | The subcommand did what it was asked, including a save of a version that was already saved. |
| 1 | A usage error or a refusal from the table above, printed as `ERROR  <message>`, or a database connection that could not be resolved. |
| 130 | Interrupted with Ctrl+C. |

## See also

- [Templates](../concepts/templates.md): what a template holds, pinning, versions and comparing them.
- [The mapping document](../flow/mapping.md): the `template` key a mapping pins a version with.
- [Writing a mapping](../guides/writing-a-mapping.md): from a saved template to a valid mapping.
- [sqlflow check](check.md): checks a flow's mapping against the template it pins.
- [The GUI](../concepts/gui.md): the Templates page, which browses the data definitions and saves templates through the control plane.
