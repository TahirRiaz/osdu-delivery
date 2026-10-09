---
id: delivery-cli-validate
title: "sqlflow validate for OSDU documents: what each delivery, cache, mapping and other document checks offline"
type: cli-command
summary: "What sqlflow validate checks in each of the eight documents OSDU Delivery adds, offline and with no database, and what it leaves to sqlflow check and the run."
keywords:
  - validate
  - offline validation
  - ci gate
  - validate a mapping
  - validate a delivery flow
  - unknown key
  - strict keys
  - flowType
  - documentType
  - pinned mapping
  - floating reference
  - dictionary
  - yaml error
related:
  - cli-validate
  - delivery-cli-check
  - delivery-flow-overview
  - delivery-flow-delivery
  - delivery-flow-mapping
  - delivery-flow-cache
  - delivery-flow-dictionary
  - concept-cli-conventions
sourceRefs:
  - sqlflow/src/SqlFlow.Cli/Program.cs
  - sqlflow/src/SqlFlow.Cli/LocalInspectVerbs.cs
  - sqlflow/src/SqlFlow.Yaml/YamlDocumentLoader.cs
  - sqlflow/src/SqlFlow.Execution/DocumentLoader.cs
  - sqlflow/src/SqlFlow.Core/Secrets/SecretHygiene.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryServices.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/AssertionFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/InventoryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DictionaryDocumentKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DictionaryMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/AssertionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/InventoryDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Model/DeclaredPartition.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/docs/census/README.md
---

# sqlflow validate for OSDU documents: what each delivery, cache, mapping and other document checks offline

`sqlflow validate` is SQLFlow's verb: it parses and validates a document, or every document under a folder, through
the loader a run uses, without a database and without executing anything. How the verb behaves, its options, the folder
mode and its `--json` report, the `.sqlflow/env` file, the secret-hygiene warning and the exit codes are on
[sqlflow validate](../../../../sqlflow/docs/reference/cli/validate.md). The command line is `sqlflow`: OSDU Delivery's
CLI is SQLFlow's CLI with the OSDU verbs added, and the module registers its document kinds with it, so `validate`
reads the documents OSDU Delivery adds as well. This page says what each of them checks.

Validation is offline: no catalog, no module database, no OSDU, and no secret resolution (`${env:...}` and
`${keyvault:...}` stay references). What needs the database (the template a mapping pins, the cache it reads, the
partition registry) is [`sqlflow check`](check.md); what needs OSDU or the ingestion tables is the run.

## The documents it recognises

| Document | Discriminator | OK line |
| --- | --- | --- |
| Delivery flow | `flowType: delivery` | `OK  '<name>' is valid (delivery: <record tables> -> <target.endpoint>).` |
| Retrieval flow | `flowType: retrieval` | `OK  '<name>' is valid (retrieval: <source.endpoint> -> <target.location>).` |
| Cache flow | `flowType: cache` | `OK  '<name>' is valid (cache: <source.endpoint, else source.connection> -> catalog).` |
| Assertion flow | `flowType: assertion` | `OK  '<name>' is valid (assertion: <source.endpoint> -> report).` |
| Dimension flow | `flowType: dimension` | `OK  '<name>' is valid (dimension: <source.endpoint> -> dimensions).` |
| Inventory flow | `flowType: inventory` | `OK  '<name>' is valid (inventory: <source.endpoint> -> inventories).` |
| Mapping | `documentType: mapping` | `OK  '<Name>@<version> -> <template.kind>' is valid (mapping).` |
| Dictionary | `documentType: dictionary` | `OK  '<name> (<n> entries, key <key>)' is valid (dictionary).` |

The six flow kinds take SQLFlow's envelope keys (`schedule`, `mode`, and the rest SQLFlow documents for every flow) as
any flow does. The mapping and the dictionary are companion documents: they are not flows, have no envelope, and are
reported under their own document type. A `flowType` no kind registers fails with SQLFlow's message, which ends by
listing the registered kinds (`Registered kinds: 'assertion' for ..., 'cache' for ..., 'delivery' for ..., ...`); a
`documentType` no kind registers fails with
`<file>: unknown documentType '<type>'. Registered document types: 'dictionary' for ..., 'mapping' for ...`.

In the folder mode each document is one line, and `--json` reports each document's `kind` as the discriminator above
(`delivery`, `mapping`, `dictionary`, ...).

## What every document checks

- **Strict keys.** Every loader refuses a key it does not know, rather than ignoring it, with the line and column, and a
  key written twice in one map:

  ```text
  ERROR  flows/welldb-wellbore-03-delivery.yaml: invalid YAML at line 14, column 1 - Property 'rendr' not found on type 'SqlFlow.Delivery.Documents.FlowYaml'.
  ERROR  flows/welldb-wellbore-03-delivery.yaml: invalid YAML at line 4, column 1 - Encountered duplicate key batch
  ```

  The keys each document accepts are listed in the key census, `osdu/docs/census/keys.<document>.json`, which a test
  holds against the loaders.
- **Required keys**, named as written: `'<key>' is required.`, for example `'source.work' is required.` on a delivery
  flow, `'dataset.system' is required.` on a mapping.
- **Partitions.** `partitions` names each partition literally, by its data-partition-id (letters, digits, underscore,
  hyphen and dot), never as a reference, at least one, at most 64, none twice. A flow that names its partitions, or
  leaves them to the registry, may not also set the `data-partition-id` header: each run sets it.

  ```text
  ERROR  <file>: partitions[0] is the reference '${env:OSDU_PARTITION}'. A partition is named literally, by its data-partition-id: the name is the key its cache, its ledger and its runs are kept under, and a key some host resolves is no key. Keep references for the endpoint and the credentials.
  ```

- **Parameters.** Every `{name}` token in a value that takes substitution names a declared parameter
  (`<file>: source.work uses '{region}', which is not declared under parameters.`).
- **Ranges and forms** of the settings: `reliability.concurrency must be at least 1.`, a service path starts with `/`,
  a kind is `authority:source:entityType:version`, and so on.
- **Credentials.** SQLFlow's credential-hygiene check warns about a flow's credential references that embed a secret
  keyword (`password=`, `secret=` and the like) without failing. The delivery and cache loaders go further and refuse a
  `source.connection` that carries a literal password:
  `<file>: source.connection carries a literal password. A flow document holds references only: put the connection string, or its password, behind ${keyvault:vault/secret} or ${env:NAME}, or connect with Azure AD (Authentication=Active Directory Default).`

## Delivery flow

- `name`, `source`, `source.connection`, `source.work`, `target` and `target.endpoint` are required; in the single form
  `render`, `render.mapping`, `target.protocol`, `source.record.object` and `source.record.key` too, and in the interface
  form each interface's `record` and `mapping`.
- The mapping is pinned as `Name@version`:
  `<file>: render.mapping 'Wellbore' must be pinned as 'Name@version'; floating references are not allowed.`
- `source.record.object` is a three-part name: `source.record.object 'silver.Wellbore' must be a three-part name [database].[schema].[table]: it has 2 part(s).`
  Its `key` names each column once.
- `target.protocol` (or an interface's `route`) is one of `storage`, `file`, `dataset`, `manifest`, `ddms`,
  `fileAndDdms`, `manifestAndDdms`, `workflow`, `dspdm`, `etp`, and an interface's route agrees with what it declares (a
  `files` or `bulk` part, a `workflow`); the route-specific blocks (`target.ddms`, `target.dspdm`, `target.etp`, a
  workflow's stages) are checked against the route that uses them.
- `target.auth` is complete for its type: `oauth2ClientCredentials` needs a `token` block, `bearer`, `apiKeyHeader` and
  `basic` need `secretRef`, and `apiKeyHeader` needs `headerName`.
- `change.detect`, `change.payloadDetect`, the `reliability` settings, `failWhen` and `target.protocolOptions` (media
  types, a manifest section, upload expiry, batch size up to 500) keep to their ranges; `reliability.fanOut` needs
  `source.record.primaryKey`.
- In the interface form: interface names are unique ignoring case, keys that belong to an interface are not written at
  the source level, `after:` names other interfaces of the document, and interfaces that wait for each other through
  `after:` are refused (`the interfaces wellbores -> welllogs -> wellbores wait for each other through after:, so none of them could run first.`).

Not checked here: the mapping file the flow pins is not read (a flow pinning a mapping that does not exist validates);
validate the `mappings/` folder with it, and `sqlflow check` loads it. Waits that come from the relationships a mapping
fills, which need the templates, are worked out by `check` too.

## Mapping

- `name`, `version`, `template.kind`, `template.version`, `dataset.system`, `dataset.key`, the `dataPartition` parameter
  and the `record` tree are required
  (`the mapping must declare the 'dataPartition' parameter; record ids and references are minted in that partition.`).
- `template.kind` is one type in one version, `authority:source:entityType:major.minor.patch`; `template.version` is the
  16 hexadecimal characters of a saved template. The template itself is not loaded.
- `dataset.key` and `dataset.identity` name columns of the dataset's own row, each once.
- `record` is laid out as the record is. Every word of the mapping language starts with `$`, and a word the language
  does not have is refused with the one it most likely meant:
  `record.data.FacilityName: '$form' is not a word of the mapping language. Did you mean '$from'? A property whose name starts with '$' is written $$form.`
  A map mixing `$` words and properties is refused, and so is a variable filled twice.
- Every modifier is one the language has (`trim`, `upper`, `lower`, `split`, `replace`, `equals`, `date`, `number`,
  `id`, `ref`) with its own settings checked:
  `record.data.FacilityName $modifiers[0]: 'trimm' is not a modifier. The modifiers are trim, upper, lower, split, replace, equals, date, number, id and ref.`
- Every `{$param.<name>}` names a declared parameter; every search a node reads is declared under `searches` and pins
  the schema of the kind it searches; a declared search or lookup that nothing reads is refused.
- `acl.owners`, `acl.viewers`, `legal.legaltags` and `legal.otherRelevantDataCountries` are literal lists of at least
  one text value.
- A mapping holds no `fixtures` block: `a mapping holds no fixtures; delete its fixtures block. ...`

The mapping's vocabulary is on [The mapping document](../flow/mapping.md) and the pages it links.

## Cache flow

- `types` is required, each type with exactly one origin: an OSDU `kind`, a `dictionary`, an ingestion `table`, or a
  `dimension` a dimension flow builds. Type names are unique.
- An OSDU type lists the paths it caches and needs an `entityType` when its kind names none; `source.endpoint` is
  required when any type is searched on OSDU, and `source.endpoint` and `source.auth` are refused when none is.
- A table type names its `key` and at least one column besides it; `source.connection` is required when any type reads
  a table, and refused when none does.
- A dictionary type takes nothing but its name: the dictionary document names its key and fields.
- A type that narrows `partitions` names partitions of the flow, and every partition the flow names is built by some
  type.

Not checked here: the dictionary file a type names is not read (a cache flow naming a dictionary that does not exist
validates); the refresh and the repository sync read it from the `dictionaries/` folder above the flow.

## Dictionary

- One document per file, with the keys `documentType`, `name`, `description`, `key`, `fields` and `entries` only.
- `name` is a letter followed by letters, digits, `_` or `-`, at most 200 characters; the file is
  `dictionaries/<name>.yaml`.
- `entries` maps each key to one text (or `~`), or, for a dictionary with `fields`, to a map of those fields. At most
  100,000 entries. Keys are unique as written and once trimmed, and a key with spaces around it is refused:
  `line 8: the key 'MD ' has spaces around it, and a value is matched trimmed.`
- Neither the key nor a field may be called `id`.

## Retrieval flow

- `name`, `source.endpoint`, `source.headers` with a non-empty `data-partition-id`, `target` and `target.location` are
  required, and `source.kind` (one kind) or `source.kinds` (a list, each once).
- `target.location` takes `{run}`, `{date}` and declared parameters only:
  `target.location uses '{region}', which is neither a run token ({run}, {date}) nor declared under parameters.`
- `target.format` is `jsonl`, `target.compression` `none` or `gzip`, `target.manifest` a file name, not a path;
  `source.pageSize`, `source.fetchParallelism`, `target.rollRecords` and the incremental window keep to their ranges.

## Assertion flow

- `name`, `source.endpoint` and 1 to 500 tests are required; each test has a unique `name`, one `kind` and 1 to 100
  assertions.
- A test reads one type in one version: a kind with wildcards is refused.
- A test reads the records its `ids` name or the records a search finds, never both:
  `tests[0] 'wellbores-landed' reads records by ids, and names a query as well: a test reads the records its ids name, or the records a search finds, not both.`
- Each assertion names one subject with the keys that subject takes, and operands that suit its operator; tags, sort,
  spatial filters, `maxRecords` and bulk reads are checked. A `{token}` is `{partition}` or a declared parameter.

Whether each path is a property of the kind is checked against the saved template when the tests run, or with
`sqlflow run <flow.yaml> --operation plan`.

## Dimension flow

- `name`, `source.endpoint` and 1 to 100 dimensions are required; each has a unique `name`, a `kind` (wildcards allowed
  per segment) and a `path`.
- A `path` is a property the search index holds:
  `dimensions[0] 'Wellbore': path FacilityName is not a property the index holds of the record. ...; its data is under data.`
- Labels, attributes, `clean` steps (each map step naming a dictionary), column names and `source.aggregationSize` are
  checked; two dimensions may not share a table name.

## Inventory flow

- `name`, `source.endpoint` and 1 to 100 inventories are required; each has a unique `name` and a `kind`.
- `source.read` is `search` or `storage`; a `query` is refused on a storage read.
- `owners` lists at most 50 identities, each once.
- A `removal` block names the findings an operator may remove, of `orphan`, `stale` and `forgotten` only:
  `removal.findings[0] is 'missing'; the ids an inventory may remove are those of orphan, stale, forgotten: what OSDU serves that no ledger holds live. ...`

## Examples

Validate the delivery flow and the mapping it pins:

```bash
sqlflow validate flows/welldb-wellbore-03-delivery.yaml
sqlflow validate mappings/Wellbore@1.0.0.yaml
```

```text
OK  'welldb-wellbore-03-delivery' is valid (delivery: OsduData.silver.Wellbore -> ${env:OSDU_URL}).
OK  'Wellbore@1.0.0 -> osdu:wks:master-data--Wellbore:1.3.0' is valid (mapping).
```

A source with two interfaces lists both record tables:

```text
OK  'welldb-03-delivery' is valid (delivery: OsduData.silver.Wellbore, OsduData.silver.WellLog -> ${env:OSDU_URL}).
```

Validate a whole repository, the CI gate:

```bash
sqlflow validate welldb
```

```text
OK      dictionaries\sampling-domain.yaml  (dictionary 'sampling-domain (3 entries, key key)')
OK      flows\welldb-03-delivery.yaml  (delivery 'welldb-03-delivery')
OK      flows\welldb-06-inventory.yaml  (inventory 'welldb-06-inventory')
OK      flows\welldb-lookups-00-cache.yaml  (cache 'welldb-lookups-00-cache')
OK      flows\welldb-retrieval-wellbores.yaml  (retrieval 'welldb-retrieval-wellbores')
OK      flows\welldb-wellbore-03-delivery.yaml  (delivery 'welldb-wellbore-03-delivery')
OK      flows\welldb-welllog-04-assertion.yaml  (assertion 'welldb-welllog-04-assertion')
OK      flows\welldb-welllog-05-dimensions.yaml  (dimension 'welldb-welllog-05-dimensions')
OK      mappings\Wellbore@1.0.0.yaml  (mapping 'Wellbore@1.0.0 -> osdu:wks:master-data--Wellbore:1.3.0')
OK      mappings\WellLog@1.0.0.yaml  (mapping 'WellLog@1.0.0 -> osdu:wks:work-product-component--WellLog:1.4.0')
10 valid, 0 broken of 10 document(s) under <folder>.
```

A mapping that names a modifier the language does not have:

```text
ERROR  mappings/Wellbore@1.0.0.yaml: record.data.FacilityName $modifiers[0]: 'trimm' is not a modifier. The modifiers are trim, upper, lower, split, replace, equals, date, number, id and ref.
```

## Exit behavior

SQLFlow's: 0 when the document (or every document of the folder) is valid, 1 otherwise, with one redacted
`ERROR  <message>` line for a single document. A credential-hygiene warning does not change the exit code. See
[sqlflow validate](../../../../sqlflow/docs/reference/cli/validate.md).

## See also

- [sqlflow validate](../../../../sqlflow/docs/reference/cli/validate.md): the verb, its folder mode and `--json` report.
- [sqlflow check](check.md): the mapping against its saved template and the cache, which needs the module database.
- [The documents OSDU Delivery adds](../flow/overview.md): the six flow kinds, the mapping and the dictionary.
- [Delivery flow](../flow/delivery.md), [The mapping document](../flow/mapping.md), [Cache flow](../flow/cache.md),
  [Dictionary](../flow/dictionary.md): every key each document takes.
- [CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md): options, streams and exit codes shared by every command.
