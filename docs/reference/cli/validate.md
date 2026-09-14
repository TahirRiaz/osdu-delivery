# sqlflow validate

## Synopsis

```bash
sqlflow validate <flow.yaml|mapping.yaml> [-v|--verbose]
sqlflow validate <folder> [--json] [-v|--verbose]
sqlflow validate <document> --json
```

## Description

Parses and validates a delivery document offline: no catalog, no OSDU, no secret resolution, nothing executed. A flow document is loaded through the same `DocumentLoader.Load` that `sqlflow run` and a compute node use (src/SqlFlow.Execution/DocumentLoader.cs), so a document that validates cleanly is the exact document a run would load: the same kind dispatch, the same strict parse, the same secret-hygiene check. A mapping document is recognised by its `documentType` and parsed by the delivery kind that owns it.

On success the command prints one `OK  ...` line to stdout and exits 0. On any load or validation failure it prints one `ERROR  ...` line to stderr (credential values redacted) and exits 1. Given a folder it validates every document under it, one line per file; that is the CI gate.

`${env:NAME}` and `${keyvault:vault/secret}` references are kept as literal references during validation; they resolve only at run time, so validation needs neither the environment variables nor the vault to exist. What validate does not do is the delivery preflight: checking the mapping against its pinned template and the version of the cache the flow reads (both in the catalog), and reading a drop's manifest, is `sqlflow check` ([delivery.md](delivery.md)).

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<document>` or `<folder>` | yes | A flow document (`flowType: delivery`), a mapping document (`documentType: mapping`), or a folder to validate recursively. Invoking `validate` without an argument prints the usage text and exits 1. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--json` | flag | off | Switches stdout to the machine-readable report array (see "Validating a whole estate"), for a folder or a single document. |
| `-v`, `--verbose` | flag | off | Prints diagnostics to stderr, including which git-ignored `.sqlflow/env` file was applied and how many variables it contributed. |
| `-h`, `--help` | flag | off | Prints the usage text. Exits 0 when a document argument is also present, 1 otherwise. |

## Behavior and output notes

### Document kinds

The loader (`YamlDocumentLoader`, src/SqlFlow.Yaml/YamlDocumentLoader.cs) sniffs the document's root `flowType` key (compared case-insensitively) and dispatches to the registered `IFlowDocumentKind` whose `flowType` matches; a kind that also implements `ICompanionDocumentKind` owns a second document family keyed by a top-level `documentType` (both seams are in src/SqlFlow.Yaml/FlowDocument.cs). This build registers one kind, the delivery flow (src/SqlFlow.Delivery/Documents/DeliveryFlowKind.cs), which also owns the mapping documents its flows pin:

| Document | Discriminator | OK line |
| --- | --- | --- |
| Delivery flow | `flowType: delivery` | `OK  '<name>' is valid (delivery: <source.location> -> <target.endpoint>).` |
| Mapping | `documentType: mapping` | `OK  '<name>@<version> -> <kind>' is valid (mapping).` |

The flow line shows the drop location and the OSDU endpoint exactly as the document declares them (a `{parameter}` token stays a token; a `${env:...}` endpoint stays a reference). The mapping line shows the pinned reference a flow names under `render.mapping`, and the OSDU kind it renders.

A document with neither discriminator, or an unknown one, fails with the loader's error naming the kinds this host knows:

```text
ERROR  <file>: 'flowType' is required. Known kinds: 'delivery' (deliver prepared records from a drop into OSDU (record and well log protocols)).
ERROR  <file>: unknown flowType '<value>'. Known kinds: 'delivery' (deliver prepared records from a drop into OSDU (record and well log protocols)).
ERROR  <file>: no registered kind owns documentType '<value>'.
```

### What the delivery kind checks

The delivery loader (src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs) parses both families with a strict deserializer: an unknown or misspelled key is a hard error, reported as `<file>: invalid YAML at line <L>, column <C> - <parser detail>`, never silently ignored. After the parse it applies the document rules, each reported as `<file>: <rule>`. For a flow:

- `name`, `source`, `source.location`, `render`, `render.mapping`, `target`, `target.endpoint` and `target.protocol` are required.
- `render.mapping` must be pinned as `Name@version`; a floating reference is refused.
- `target.protocol` must be a protocol this version implements (`osduRecord`, `osduWellLog`).
- every `{token}` in `source.location` and `source.knownState` must be declared under `parameters`.
- every `source.payloads.<name>` template must contain `{deliveryKey}`, and a protocol that carries a payload must name a declared one.
- `target.auth`: `oauth2ClientCredentials` needs a `token` block; `bearer`, `apiKeyHeader` and `basic` need `secretRef`; `apiKeyHeader` needs `headerName`.
- `reliability.concurrency` and `reliability.retry.attempts` must be at least 1.

For a mapping (src/SqlFlow.Delivery/Documents/MappingMapper.cs):

- `name`, `version`, `template.kind`, `template.version`, `dataset.system`, `dataset.key`, the `dataPartition` parameter and at least one entry under `mappings` are required.
- `template.kind` must be `authority:source:entityType:major.minor.patch`, and `template.version` 16 lower-case hexadecimal characters. The template itself is not loaded; that is `sqlflow check`.
- `dataset.key` names columns of the dataset's own row (`dataset.<column>`), each once, and every `{dataset.<column>}` token in `dataset.label` names such a column too.
- every entry has a `target` that starts with `osdu.` and steps into at most one array (`[]`, never last), and exactly one of `source` and `static`. A static entry takes only `appliesWhen` and `description` besides its value.
- a `source` is `dataset.<column>`, `dataset.<child>.<column>`, `dataset.<child>` on an array other entries fill (a repeater), or `cache.<type>.<field>`. A cache source needs `findBy`, each line comparing a field of the same cached type with `dataset.<column>`, `dataset.<child>.<column>` or a quoted text; `findBy` and `ignoreSeparators` are refused on any other entry.
- the modifiers are `trim`, `upper`, `lower`, `date` (optionally with its input format), `number` (optionally its `decimal` and `group` separators, which must differ), `split` (a `separator` and a `part` counting from one), `replace` (at least one pair) and `equals` (one text). A repeater takes none, and a cache source takes them only when a `findBy` line reads a dataset column.
- `appliesWhen` reads `dataset.<column> is <text>`, `is not <text>`, `is empty` or `is not empty`; a repeater's reads the dataset's own row.
- no two entries fill the same target; an entry inside `X[]` needs a repeater on `X` and reads only that repeater's child dataset; a child dataset's column read outside a repeater is an error; every `{param.name}` token names a declared parameter.
- `osdu.acl.owners`, `osdu.acl.viewers`, `osdu.legal.legaltags` and `osdu.legal.otherRelevantDataCountries` each have a static list of at least one text, without repeats and without `appliesWhen`.
- every fixture has a `name`, a `record` and `expected`.

Every key is described in [documents.md](../../delivery/documents.md) and [mapping-templates.md](../../delivery/mapping-templates.md).

### Environment file

Before the document loads, the CLI applies the nearest git-ignored `.sqlflow/env` file (`KEY=VALUE` lines), searched from the document's directory (or the folder itself) upward; the process environment always wins. With `--verbose`, `env: applied N variable(s) from <path>` prints to stderr. A malformed file fails the command before anything is parsed, with exit 1:

```text
ERROR  <path>(<line>): expected KEY=VALUE (or a # comment).
ERROR  <path>(<line>): '<name>' is not a valid environment variable name (letters, digits, '_').
```

The offending line's content is never echoed, since in a secrets file it may be the secret.

### Secret hygiene

Every flow load runs the embedded-credential check from src/SqlFlow.Core/Secrets/SecretHygiene.cs over the flow's credential references (`FlowDefinition.CredentialReferences`): `target.auth.secretRef`, `target.auth.secondarySecretRef`, and every `target.endpoint`, `target.headers.<name>` and `target.auth.token.body.<name>` value that carries a `${...}` reference. A value that is not itself a bare `${...}` reference or an `@alias`, and that contains one of the keywords `password=`, `pwd=`, `client secret=`, `clientsecret=`, `secret=`, `accesskey=`, `access key=` or `sharedaccesskey=` (compared case-insensitively), produces a warning on stderr:

```text
WARN  <file>: target.auth.secretRef embeds a credential in the document. Files under source control must carry references instead: use ${env:NAME} or ${keyvault:vault/secret}; put local values in the git-ignored .sqlflow/env file. See docs/environment-variables.md.
```

The value itself is never echoed, and the warning does not change the exit code: the document still validates and the command still exits 0. The same check runs on `sqlflow run` and on a compute node, and the catalog sync warns on the same keywords. The check keys on connection-string keywords, so a bare literal secret with no keyword is not detectable and passes unflagged; the rule in [environment-variables.md](../../environment-variables.md) applies regardless. A mapping carries no credentials by design and is never checked.

### Error output and redaction

A single-document failure prints exactly one line to stderr in the form `ERROR  <message>` and exits 1. The message passes through `SecretHygiene.RedactedMessage` first: any value following a secret-bearing keyword (a connection string quoted by a parser error, say) collapses to `[redacted]`. Representative messages:

```text
ERROR  Pipeline file not found: '<path>'.
ERROR  <file>: invalid YAML - <parser message>
ERROR  <file>: render.mapping 'WellLog' must be pinned as 'Name@version'; floating references are not allowed.
ERROR  <file>: source.location uses '{logSource}', which is not declared under parameters.
```

## Validating a whole estate

Given a folder, `validate` collects every `*.yaml` and `*.yml` under it (recursively; the `.sqlflow` work area and the shared-schedule library files, `schedules.yaml` and `*.schedules.yaml`, are excluded), sorts them, and validates each through the exact same loader (src/SqlFlow.Cli/LocalInspectVerbs.cs): a mapping under its own type, a flow through `DocumentLoader.Load` with its hygiene warnings on stderr. One line per document, then a summary:

```text
OK      <relative path>  (<kind> '<name>')
BROKEN  <relative path>: <error>
<valid> valid, <broken> broken of <N> document(s) under <folder>.
```

The exit code is 0 only when every document is valid. Any broken document, an empty estate (`ERROR  no *.yaml/*.yml documents under <folder>.`), or a path that is neither a document nor a folder (`ERROR  '<path>' is neither a document nor a folder.`) exits 1. A `BROKEN` line carries the loader's message as raised. The relative path uses the platform's directory separator.

With `--json` (on a folder or a single document) stdout becomes a report array of `{file, ok, kind, name, error}`, so a CI step can gate on it structurally:

```json
[
  { "file": "flows/recall-welllog.yaml", "ok": true, "kind": "delivery", "name": "recall-welllog", "error": null },
  { "file": "mappings/WellLog@1.4.0.yaml", "ok": true, "kind": "mapping", "name": "WellLog@1.4.0 -> osdu:wks:work-product-component--WellLog:1.4.0", "error": null }
]
```

## Examples

Validate the sample flow (samples/recall-welllog/flows/recall-welllog.yaml):

```bash
sqlflow validate samples/recall-welllog/flows/recall-welllog.yaml
```

```text
OK  'recall-welllog' is valid (delivery: samples/recall-welllog/out/{logSource} -> ${env:PETRODB_URL}).
```

Validate the mapping it pins (samples/recall-welllog/mappings/WellLog@1.4.0.yaml):

```bash
sqlflow validate samples/recall-welllog/mappings/WellLog@1.4.0.yaml
```

```text
OK  'WellLog@1.4.0 -> osdu:wks:work-product-component--WellLog:1.4.0' is valid (mapping).
```

Validate the whole sample estate as a CI gate:

```bash
sqlflow validate samples/recall-welllog
```

```text
OK      flows/recall-welllog.yaml  (delivery 'recall-welllog')
OK      mappings/WellLog@1.4.0.yaml  (mapping 'WellLog@1.4.0 -> osdu:wks:work-product-component--WellLog:1.4.0')
2 valid, 0 broken of 2 document(s) under /work/osdu-delivery/samples/recall-welllog.
```

A flow that embeds a keyword-bearing literal still validates, but warns on stderr. Given a flow whose auth reads `secretRef: "secret=hunter2"` instead of `secretRef: ${env:OSDU_CLIENT_SECRET}`:

```bash
sqlflow validate leaky.yaml
```

```text
WARN  leaky.yaml: target.auth.secretRef embeds a credential in the document. Files under source control must carry references instead: use ${env:NAME} or ${keyvault:vault/secret}; put local values in the git-ignored .sqlflow/env file. See docs/environment-variables.md.
OK  'leaky' is valid (delivery: /drops/leaky -> https://osdu.example.com).
```

## Exit behavior

| Exit code | Condition |
| --- | --- |
| 0 | The document loaded and validated (one `OK` line), or every document under the folder did. Secret-hygiene warnings do not affect the exit code. |
| 1 | No argument was given (usage printed); the path does not exist; the `.sqlflow/env` file failed to apply; the YAML failed to parse; the document failed kind dispatch or a document rule; or, for a folder, any document is broken or none was found. |

## See also

- [sqlflow check, cache, template, and the delivery run options](delivery.md): the preflight gate that checks the mapping against its pinned template and the cache, the cache and template verbs, and the run verbs.
- [Document reference](../../delivery/documents.md): every key of the flow and mapping documents.
- [Environment variables](../../environment-variables.md): secret references and the `.sqlflow/env` file.
- [sqlflow db](db.md): the sync that projects validated documents into the catalog.
