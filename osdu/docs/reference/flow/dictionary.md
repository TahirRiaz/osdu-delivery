---
id: delivery-flow-dictionary
title: "Dictionary document (documentType: dictionary): a small lookup table kept in the repository for mappings to read"
type: flow-reference
summary: "A dictionary document: a small static lookup table filed as dictionaries/<name>.yaml, held in the cache by a cache flow and read by mappings as $cache.<name>."
keywords:
  - dictionary
  - documenttype dictionary
  - dictionaries folder
  - static lookup
  - lookup table
  - value map
  - entries
  - "$cache"
  - replace
  - translate source values
  - code list
yamlPath: "(root, documentType: dictionary)"
related:
  - delivery-flow-cache
  - delivery-guide-lookup-table-cache
  - delivery-concept-partition-cache
  - delivery-flow-mapping-modifiers
  - delivery-flow-mapping-lookups
  - delivery-cli-validate
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DictionaryMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DictionaryCatalog.cs
  - osdu/src/SqlFlow.Delivery/Documents/DictionaryDocumentKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheLineage.cs
  - osdu/src/SqlFlow.Delivery/Model/DictionaryDefinition.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheOrigin.cs
  - osdu/src/SqlFlow.Delivery/Engine/Snapshots/CacheRefresh.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Rendering/ReplaceTables.cs
  - osdu/docs/census/keys.dictionary.json
---

# Dictionary document (documentType: dictionary): a small lookup table kept in the repository for mappings to read

A dictionary is one small lookup table written as YAML and reviewed with the flows: how the source writes a value and
what the partition calls it, or a few named facts per code. It is filed as `dictionaries/<name>.yaml`, one table per
file. A dictionary does nothing on its own: a [cache flow](cache.md) holds it in a partition's cache
(`- dictionary: <name>`), and every mapping that delivers to that partition reads it the way it reads any cached type,
most often with `replace: $cache.<Type>`.

Use a dictionary for a short, static table that people maintain by hand and want to review in a pull request. A table
another system keeps, one that arrives as files, or one too large to review belongs in an ingestion table that a cache
flow captures with a `table` type ([lookup table guide](../guides/lookup-table-cache.md)); values that come from what
OSDU already holds belong in a [dimension](dimension.md).

## Two shapes

A dictionary of pairs maps each key to one value:

```yaml
documentType: dictionary
name: sampling-domain
description: How the well database writes a log's sampling domain, as the codes of the partition's WellLogSamplingDomainType records.
entries:
  MD: Depth
  DEPTH: Depth
  TVD: Depth
  TIME: Time
  UNKNOWN: ~
```

Its key is kept as `key` and its value as `value`. `UNKNOWN: ~` is an entry with no value: a replace that meets
`UNKNOWN` gives no value, and the property's `$required` decides what happens.

A dictionary with `fields` gives each key several named values:

```yaml
documentType: dictionary
name: curve-class
description: What each curve mnemonic of the well database measures, and the unit it is recorded in.
key: mnemonic
fields: [family, unit]
entries:
  GR: { family: Gamma Ray, unit: gAPI }
  RHOB: { family: Bulk Density, unit: g/cm3 }
  NPHI: { family: Neutron Porosity, unit: "%" }
  CALI: { family: Caliper }
```

Here each row is keyed by `mnemonic` and carries `family` and `unit`. An entry may leave a field out (`CALI` gives no
`unit`), and that field then reads as no value.

```bash
sqlflow validate dictionaries/curve-class.yaml
```

prints `OK  'curve-class (4 entries, key mnemonic)' is valid (dictionary).`

## Keys reference

| Key | Required | Default | Meaning |
| --- | --- | --- | --- |
| `documentType` | yes | none | `dictionary`. |
| `name` | yes | none | A letter followed by letters, digits, `_` or `-`, at most 200 characters. The file is named by it (`dictionaries/<name>.yaml` or `.yml`), and the file must declare the name it is filed under. |
| `description` | no | none | Free text. |
| `key` | no | `key` | The name each entry's key is kept under; a mapping matches on it by that name. |
| `fields` | no | none | The names of the values each entry gives. Left out, the dictionary is one of pairs and each value is kept as `value`. |
| `entries` | yes | none | At least one entry and at most 100,000: every entry is loaded with the cache version a render reads. |

No other top-level key is accepted: `'<key>' is not a key of a dictionary document; it takes documentType, name,
description, key, fields, entries.` A key name and a field name are identifiers a mapping can read: a letter or `_`
followed by letters, digits or `_`, at most 128 characters.

## The rules for keys and values

- **Everything is text exactly as written.** The document is read node by node, never typed: `NO`, `true` and `1.10`
  are those texts, never a boolean or a number. The template decides the type a value is written as.
- **No value is `~`, `null` or nothing, unquoted.** A quoted `"~"` is the text `~`.
- **A key is unique, trimmed, not empty and at most 256 characters,** with no control characters. A key written twice is
  refused with its line (`line 5: Duplicate key MD`); a key with spaces around it is refused (`the key ' MD' has spaces
  around it, and a value is matched trimmed.`).
- **Nothing is called `id`,** in any casing: an entry's key is its id in the cache. `key: id` and a field `ID` are
  refused.
- **The key's own name is never listed among the fields** (`field 'mnemonic' is the key's own name; the key is kept under
  it already.`), and a field is listed once, compared without case.
- **A value is one text.** A list or a map is refused (`'MD' maps to something that is not text; a dictionary of pairs
  maps each key to one text, or ~ for no value. List fields for a key with several values.`), and so is a value under a
  name `fields` does not list (`'GR' gives 'colour', which fields does not list; it lists type, unit.`).

Every refusal names the file, and the line where the document is wrong.

## Holding it in the cache

A cache flow declares a dictionary type by the dictionary's name, optionally under another name. In the `types` of a
cache flow such as `welldb-lookups-00-cache`:

```yaml
types:
  - dictionary: sampling-domain
    name: SamplingDomain
  - dictionary: curve-class
```

A flow of dictionaries alone reaches neither OSDU nor a database, and still writes `source: {}`.

- **Where the file is found.** In the nearest `dictionaries/` folder walking up from the cache flow's folder (at most 16
  levels; the repository sync looks only inside the repository), so a cache flow several folders deep still finds the
  repository's shared dictionaries.
  A name with `/`, `\` or `..` is refused before any file is read.
- **What the type holds.** One row per entry, keyed by the entry's key, kept as the lookup table `lookup--<name>`. The
  dictionary type takes no `fields`, `key`, `kind`, `entityType` or `query`: the document names its key and fields.
- **Who may declare it.** A lookup table is declared by one cache flow of a partition. A second cache flow declaring the
  same type name for that partition is left out of the catalog at sync, with a warning naming both flows.
- **When it is read.** The repository sync reads every dictionary a cache flow declares and leaves a type whose
  dictionary is missing or invalid out of the cache with a warning naming the file, so a broken file is reported when it
  is pushed. A refresh reads the file again on the node that runs it, at the run's commit; a missing or invalid file
  fails the refresh with `Cache flow '<flow>' could not read dictionary <name> for type <type>, so nothing was captured:
  ...`, and the cache keeps its version.

`sqlflow validate` on a cache flow finds and reads the file the way a refresh does, and refuses the flow when it is
missing or does not load: `<file>: type '<type>': Dictionary '<name>' was not found under '../dictionaries'. Expected <name>.yaml or <name>.yml.`
`sqlflow lineage <folder>` looks for it too, and warns when a declared dictionary is not found: `type '<type>' holds dictionary <name>, which was not found in a dictionaries/
directory above the flow, so its lineage leaves the file out.` The proposal preflight checks a dictionary document
before it is pushed, as it checks a mapping.

## Reading it in a mapping

A mapping reads a dictionary type like any lookup table:

```yaml
SamplingDomainTypeID:
  $from: sampling_domain
  $modifiers:
    - replace: $cache.SamplingDomain
    - ref
```

`replace: $cache.SamplingDomain` matches the incoming value on the table's key (trimmed; an exact key wins, and case is
ignored only when that finds one entry) and replaces it with the one field beside the key, `value` for a dictionary of
pairs. `ref` then writes the reference `{dataPartition}:reference-data--WellLogSamplingDomainType:Depth:`; when the
partition's cache holds WellLogSamplingDomainType records, the render checks that the id names one of them. A value the
dictionary does not list passes on unchanged unless the replace says `otherwise:`.

A dictionary with fields names the field a replace gives (`field: family`), or is read one field at a time:

```yaml
CurveDescription:
  $cache: curve-class.family
  $findBy: mnemonic = curve_mnemonic
  $required: false
```

Both fragments come from a mapping validated with `sqlflow validate`. A dictionary row is not an OSDU record, so
`$cache: <Type>.id` on it is refused by the preflight: read one of its fields. [Modifiers](mapping-modifiers.md) and
[lookups](mapping-lookups.md) describe both forms in full.

## Changing a dictionary

Editing the file changes nothing until the cache flow runs. Push the change, let the repository sync it, then refresh
the cache flow (its schedule, **Refresh now** on the Cache page, or the **Refresh** menu there when several cache flows
fill the partition, or `sqlflow run <cache flow> --set partition=<name>`).
The refresh writes a new cache version and tags exactly the delivered records whose render read a changed entry; an
entry for a key that records looked up and found missing tags those records too. Under `onChange: auto` they are
delivered again on their flow's next run; under `onChange: approve` they wait for a decision on the Cache page
([The partition cache](../concepts/partition-cache.md)).

## Related

- [Cache flow](cache.md): the `dictionary` origin beside `kind`, `table` and `dimension`.
- [Getting your own lookup table into the cache](../guides/lookup-table-cache.md): when a table outgrows a dictionary.
- [`sqlflow validate`](../cli/validate.md): what validate checks in each OSDU document.
