# sqlflow validate for OSDU documents

```bash
sqlflow validate <flow.yaml|mapping.yaml> [-v|--verbose]
sqlflow validate <folder> [--json] [-v|--verbose]
```

`validate` parses and validates a document offline: no catalog, no OSDU, no secret resolution, nothing executed. A
document is loaded through the same loader a run uses, so a document that validates cleanly is the exact document
a run would load. How the verb behaves, its options, the estate mode and its `--json` report, the `.sqlflow/env`
handling, the secret-hygiene warning and the exit codes are documented in
[../../../../sqlflow/docs/reference/cli/validate.md](../../../../sqlflow/docs/reference/cli/validate.md).

This page lists what the OSDU kinds themselves check. What validate deliberately does **not** do is the delivery
preflight: checking the mapping against its pinned template and the version of the cache the flow reads, both of
which live in the catalog, is [`sqlflow check`](delivery.md).

## The documents it recognises

| Document | Discriminator |
| --- | --- |
| Delivery flow | `flowType: delivery` |
| Retrieval flow | `flowType: retrieval` |
| Cache flow | `flowType: cache` |
| Mapping | `documentType: mapping` |

Every one is parsed with a strict deserializer: an unknown or misspelled key is a hard error, reported with the
line and column, never silently ignored. A YAML with no discriminator is simply not one of these documents, and a
document declaring an unknown `flowType` fails naming the kinds the host knows.

## What a delivery flow must satisfy

- `name`, `source`, `render`, `render.mapping`, `target`, `target.endpoint` and `target.protocol` are required.
- `render.mapping` is pinned as `Name@version`; a floating reference is refused, so a run can never silently pick
  up a changed mapping.
- `target.protocol` is a route type this version implements (`storage`, `file`, `dataset`, `manifest`, `ddms`,
  `fileAndDdms`, `manifestAndDdms`, `workflow`) or the protocol it maps onto (`storage`, `file`, `dataset`,
  `manifest`, `ddms`, `fileAndDdms`, `manifestAndDdms`, `workflow`); the workflow route needs
  `target.workflow`, and its templates and contexts are checked against the workflows' contracts.
- `protocolOptions.payloadContentType` and `filesContentType` are media types.
- `target.auth` is complete for its kind: `oauth2ClientCredentials` needs a `token` block; `bearer`,
  `apiKeyHeader` and `basic` need `secretRef`; `apiKeyHeader` needs `headerName`.
- Every `{token}` in a value that takes parameter substitution is declared under `parameters`.
- `reliability.concurrency` and `reliability.retry.attempts` are at least 1.

## What a mapping must satisfy

- `name`, `version`, `template.kind`, `template.version`, `dataset.system`, `dataset.key`, the `dataPartition`
  parameter and the `record` tree are required.
- `template.kind` is `authority:source:entityType:major.minor.patch`, and `template.version` is 16 lower-case
  hexadecimal characters. The template itself is not loaded; that is `sqlflow check`.
- `dataset.key` names columns of the dataset's own row, each once and as it is, and every `{<column>}` token in
  `dataset.label` names such a column. `dataset.identity` names such columns too, each once: their values are what
  the ledger indexes so the record can be looked up by them.
- `record` is laid out as the record is. Every word of the mapping language starts with `$` and every other key is a
  property of the record; a map mixing the two, a word the language does not have (named with the one it most likely
  meant), a property name that is a path, and a key written twice in one map are refused.
- A value node reads its value one way: `$from` (a column of the row it is in, or `$dataset.<column>`), `$value`,
  `$cache: <Type>.<field>` or `$search: <name>`. `$cache` and `$search` need `$findBy`; `$findBy` and
  `$ignoreSeparators` are refused on any other node.
- The modifiers are `trim`, `upper`, `lower`, `date`, `number`, `split`, `replace`, `equals` and `id`, each with
  its own rules; an id template's tokens are `{$value}`, `{<column>}`, `{$dataset.<column>}`,
  `{$cache.<Type>.<field>}` and `{$param.<name>}`, and `id` is the last modifier.
- A `$forEach` node names a child dataset and lays out its items under `$item`, which read that child's rows; it
  takes no modifiers, and a `$forEach` inside its items is not supported. Every `{$param.name}` token names a declared
  parameter, and a literal reads no other token.
- `acl.owners`, `acl.viewers`, `legal.legaltags` and `legal.otherRelevantDataCountries` each hold a literal list of
  at least one text, without repeats and without `$when`.
- Every fixture has a `name`, a `row` and `expected`.

Every key is described in [../../documents.md](../../documents.md) and
[../../mapping-templates.md](../../mapping-templates.md).

## The estate gate

Given a folder, `validate` checks every document under it and exits 0 only when all of them are valid, which is
the CI gate for a flow repository:

```bash
sqlflow validate osdu/samples/recall
```

`--json` turns stdout into a `{file, ok, kind, name, error}` report array, so a CI step can gate on it
structurally.

## See also

- [delivery.md](delivery.md): `sqlflow check`, the preflight that needs the catalog.
- [../../documents.md](../../documents.md): every key of the flow, mapping and cache documents.
