---
id: flow-inv
title: "Invoke flow (flowType: inv) and the invokes block"
type: flow-reference
summary: "flowType: inv triggers one ADF pipeline or Automation runbook and polls it; the same invokes/servicePrincipals blocks back pre/post invoke hooks."
keywords:
  - invoke
  - adf
  - automation runbook
  - pipeline
  - polling
  - onerrorresume
  - serviceprincipal
yamlPath: "(root, flowType: inv) / invokes"
related:
  - flow-overview
  - flow-service-principals
  - cli-auth
  - flow-hooks
sourceRefs:
  - src/SqlFlow.Yaml/YamlInvokeFlowLoader.cs
  - src/SqlFlow.Yaml/YamlInvokeParts.cs
  - src/SqlFlow.Yaml/InvokeYaml.cs
  - src/SqlFlow.Core/Invoke/InvokeType.cs
  - src/SqlFlow.Core/Invoke/InvokeDefinition.cs
  - src/SqlFlow.Core/Invoke/IInvokeDispatcher.cs
  - src/SqlFlow.Core/Connections/ServicePrincipalProfile.cs
  - src/SqlFlow.Azure/Invoke/AzureDataFactoryInvokeExecutor.cs
  - src/SqlFlow.Azure/Invoke/AzureAutomationInvokeExecutor.cs
  - src/SqlFlow.Azure/Invoke/AzureServicePrincipalResolver.cs
  - src/SqlFlow.Azure/Invoke/InvokeParameterJson.cs
  - samples/invoke/trigger-refresh.flow.yaml
---

# Invoke flow (flowType: inv) and the invokes block

A `flowType: inv` document triggers one named Azure resource, an Azure Data Factory pipeline (`type: adf`) or an Azure Automation runbook (`type: aut`), passes it typed parameters, and polls the run until it reaches a terminal state. Anything but success fails the run. The definition carries only *what* to run (a resource name plus parameters), never executable code: the legacy host-execution invoke types and their code-carrying columns (Code, InvokeFile, InvokePath, Arguments) were removed. The same `invokes:` and `servicePrincipals:` blocks are also accepted inside ing, exp, and sp documents, where hook keys reference the named entries around the load (ing accepts `preInvoke` and `postInvoke`; exp and sp accept `postInvoke` only). One mapping layer (src/SqlFlow.Yaml/YamlInvokeParts.cs) validates the blocks in every document kind, so fields, defaults, and error wording are identical everywhere.

## Minimal example

```yaml
flowType: inv
name: trigger-refresh

servicePrincipals:
  deploy:
    subscriptionId: 00000000-0000-0000-0000-000000000000
    resourceGroup: rg-data
    dataFactoryName: adf-prod

invoke:
  type: adf
  pipeline: pl_refresh_marts
  servicePrincipal: deploy
```

No `clientSecret`, `tenantId`, or `clientId` means the deployment's ambient Azure credential (managed identity, az login, or environment default) authenticates the call.

## Keys reference

Top-level document keys (`flowType: inv`):

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `flowType` | string | yes | none | Must be `inv` for a standalone invoke document. |
| `name` | string | yes | none | The flow's unique alias (InvokeAlias). Missing name fails: `'name' is required for an invoke flow.` |
| `batch` | string | no | none | The batch this flow belongs to. |
| `servicePrincipals` | map | no | empty | Named service-principal profiles the invoke authenticates with. |
| `invoke` | map | yes | none | The action to run. Missing block fails: `'invoke' is required.` |
| `onErrorResume` | bool | no | `true` | Batch behavior when this flow fails. The document-level value overrides `invoke.onErrorResume`. |

Keys of the `invoke:` block, and of each named entry under `invokes:` in ing/exp/sp documents:

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `type` | string | no | `aut` | `adf` (Data Factory pipeline) or `aut` (Automation runbook). Blank maps to `aut`. |
| `pipeline` | string | adf only | none | The ADF pipeline name. Required when `type: adf`, forbidden when `type: aut`. |
| `runbook` | string | aut only | none | The Automation runbook name. Required when `type: aut`, forbidden when `type: adf`. |
| `servicePrincipal` | string | yes | none | A name declared under `servicePrincipals:`. |
| `parameters` | map | no | none | Pipeline or runbook parameters; YAML scalar types are preserved into JSON. |
| `onErrorResume` | bool | no | `true` | Per-invoke failure behavior. In a standalone document the top-level `onErrorResume` wins when set. |

Keys of each entry under `servicePrincipals:`:

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `tenantId` | string | with clientSecret | none | Azure AD tenant id (a coordinate, not a secret). |
| `clientId` | string | with clientSecret | none | Application (client) id. |
| `clientSecret` | string | no | none | A whole `${env:NAME}` or `${keyvault:vault/secret}` reference ONLY. Omit to use the ambient credential. |
| `subscriptionId` | string | yes | none | ARM subscription id. |
| `resourceGroup` | string | yes | none | ARM resource group. |
| `dataFactoryName` | string | for adf invokes | none | The Data Factory instance an `adf` invoke targets. |
| `automationAccountName` | string | for aut invokes | none | The Automation account an `aut` invoke targets. |
| `keyVaultName` | string | no | none | The Key Vault backing the secret reference (a hint, not a secret). |

## invoke.type

Allowed values are `adf` (Azure Data Factory pipeline) and `aut` (Azure Automation runbook). A blank or omitted value defaults to `aut`. Parsing lives in src/SqlFlow.Core/Invoke/InvokeType.cs:

- The removed legacy host-execution codes fail fast: `InvokeType 'ps' (host script/code execution) has been removed as a code-injection risk. Invoke now only triggers Azure Data Factory pipelines ('adf') and Azure Automation runbooks ('aut').` (same for `cs`).
- Any other value fails: `Unknown InvokeType '<code>'. Expected one of: adf, aut.`

## invoke.pipeline and invoke.runbook

Exactly one of the two must be set, matching the type. The cross-field checks fail at parse, not mid-run:

- `type: adf` without `pipeline`: `'<section>.pipeline' is required when type is adf.`
- `type: adf` with `runbook`: `'<section>.runbook' is set, but type adf runs a pipeline; remove it or use type aut.`
- `type: aut` without `runbook`: `'<section>.runbook' is required when type is aut.`
- `type: aut` with `pipeline`: `'<section>.pipeline' is set, but type aut runs a runbook; remove it or use type adf.`

`<section>` is `invoke` in a standalone document and `invokes.<name>` inside an ing/exp/sp document.

## invoke.servicePrincipal

Required; names an entry declared under `servicePrincipals:` in the same document (lookup is case-insensitive). Failures:

- Missing: `'<section>.servicePrincipal' is required (a name declared under 'servicePrincipals:').`
- Unknown: `'<section>.servicePrincipal' references '<name>', which is not declared under 'servicePrincipals:'.`
- An `adf` invoke whose principal has no `dataFactoryName`: `'<section>' is an adf invoke, but service principal '<name>' has no dataFactoryName.`
- An `aut` invoke whose principal has no `automationAccountName`: `'<section>' is an aut invoke, but service principal '<name>' has no automationAccountName.`

The parsed definition stores the reference as `@<name>` (`TargetServicePrincipalReference`), the secretless alias form the resolver expects.

## invoke.parameters

A map of parameter name to value, converted to the JSON object string the executors pass to ADF or Automation. Scalar types survive the trip:

- An unquoted `true` or `42` in the YAML arrives as a JSON boolean or number; a quoted scalar (`"42"`) arrives as a string.
- Nested maps and sequences become JSON objects and arrays, recursively.
- `null` values are preserved as JSON null.

Validation failures:

- Empty parameter name: `'<section>.parameters' has a parameter with an empty name.`
- Empty nested key: `'<section>.parameters.<path>' has a nested key that is empty.`
- A value that is not a scalar, map, or sequence: `'<section>.parameters.<name>' has an unsupported value of type '<T>'. Use scalars, maps, or sequences.`

An empty or omitted `parameters:` map produces no parameter JSON at all.

## invoke.onErrorResume and the document-level onErrorResume

Both default to `true`. In a standalone `inv` document the effective value is: document-level `onErrorResume` if set, else `invoke.onErrorResume` if set, else `true`. Inside ing/exp/sp documents each `invokes:` entry carries its own `onErrorResume` (default `true`).

## servicePrincipals

A map of profile name to profile. Names (and invoke names, see below) must be non-empty and use only ASCII letters, digits, `_`, `.`, or `-`; an invalid name fails with `service-principal name '<name>' is invalid. Use letters, digits, '_', '.', or '-'.` A repeated name fails with `service principal '<name>' is declared more than once.` An entry that is not a map fails with `'servicePrincipals.<name>' must be a map of service-principal fields.`

The profile is secretless (src/SqlFlow.Core/Connections/ServicePrincipalProfile.cs): plain coordinates rest as text, but the client secret rests ONLY as a reference. The legacy plaintext flw.SysServicePrincipal.ClientSecret column is gone.

- `clientSecret` must be a whole `${scheme:value}` reference such as `${env:NAME}` or `${keyvault:vault/secret}`. Any other value fails: `'servicePrincipals.<name>.clientSecret' must be a whole ${env:NAME} or ${keyvault:vault/secret} reference; the secret value itself never rests in the document. Omit it to authenticate with the ambient Azure credential.`
- Setting `clientSecret` makes `tenantId` and `clientId` required: `'servicePrincipals.<name>' sets clientSecret, so tenantId and clientId are required.`
- `subscriptionId` and `resourceGroup` are always required, each with its own `... is required.` error.

At execution time the resolver (src/SqlFlow.Azure/Invoke/AzureServicePrincipalResolver.cs) expands the secret reference transiently through the shared secret resolver; a reference that resolves to an empty value fails loudly: `Service principal '<alias>' resolved an empty client secret from '<ref>'.` When no `clientSecret` reference is present, the deployment's ambient credential (managed identity, Azure CLI, environment default) is used instead, which is the preferred production posture.

## invokes: in ing/exp/sp documents

Ingestion, export, and stored-procedure documents accept the same two blocks, `servicePrincipals:` and `invokes:` (a map of invoke name to an invoke block with the exact fields above). Hook keys then name entries under `invokes:` to run around the load: ingestion documents accept `preInvoke:` and `postInvoke:`; export and stored-procedure documents accept `postInvoke:` only. Rules:

- Invoke names follow the same character rule as service-principal names; duplicates fail with `invoke '<name>' is declared more than once.`
- An entry that is not a map fails with `'invokes.<name>' must be a map of invoke fields.`
- A `preInvoke`/`postInvoke` value that names an undeclared invoke fails with `'<field>' references '<name>', which is not declared under 'invokes:'.`

Each parsed definition carries `FlowId = StableFlowId(alias)`, a deterministic positive id derived from the name, so logs key consistently across runs without a database assigning ids.

## Execution behavior

The dispatcher (src/SqlFlow.Core/Invoke/IInvokeDispatcher.cs) routes the definition to the executor for its type, times the call, and returns a uniform result. It never throws for an execution failure or an unsupported type; both surface as `Success = false` with a precise error. A standalone `inv` flow runs under the orchestrator-assigned run id; a nested pre/post invoke hook mints its own run id.

`type: adf` (src/SqlFlow.Azure/Invoke/AzureDataFactoryInvokeExecutor.cs):

- Fetches the named pipeline via ARM, creates a run with the parameter JSON, and polls every 15 seconds.
- Each parameter reaches the pipeline as its raw JSON value (src/SqlFlow.Azure/Invoke/InvokeParameterJson.cs): a string stays quoted, a number or boolean stays a literal, and a nested map or array stays structured JSON.
- Non-terminal statuses are `Queued`, `InProgress`, and `Canceling`; an empty (not yet populated) status also keeps polling. Any other status is terminal, so an unrecognized new service status resolves as a failure, never as a success.
- A terminal status other than `Succeeded` throws: `ADF pipeline '<pipeline>' run <runId> ended with status '<status>'. <message>`

`type: aut` (src/SqlFlow.Azure/Invoke/AzureAutomationInvokeExecutor.cs):

- Starts a job named `{runbook}_SQLFLW_{guid}` on the Automation account, passing the flow's parameters (the legacy engine dropped them; V3 passes them), and polls every 10 seconds.
- Each parameter reaches the runbook as a string input (src/SqlFlow.Azure/Invoke/InvokeParameterJson.cs): a JSON string is unwrapped to its raw text with no surrounding quotes, and anything structured (a number, boolean, map, or array) arrives as its JSON text for the runbook to parse.
- Terminal statuses are `Completed`, `Failed`, `Stopped`, and `Suspended`; only `Completed` succeeds. A failure includes the job's exception or status detail: `Automation runbook '<runbook>' job '<job>' ended with status '<status>'. <detail>`

Both executors resolve the `@alias` reference through the single resolver code path. A missing reference fails with `This invoke targets Azure but has no service principal: set trgServicePrincipalAlias on the flw.Invoke flow.`; a non-alias reference fails with `Service-principal reference '<ref>' must be an '@alias'.`; and the resolved profile must carry `SubscriptionId` and `ResourceGroup` (`Service principal '<alias>' is missing the required '<field>'.`). The caller's cancellation token bounds every poll loop.

## Full example

Adapted from samples/invoke/trigger-refresh.flow.yaml. Validate and run it with the CLI:

```bash
sqlflow validate trigger-refresh.flow.yaml
sqlflow run trigger-refresh.flow.yaml
```

```yaml
flowType: inv
name: trigger-refresh
batch: nightly

servicePrincipals:
  deploy:
    tenantId: 00000000-0000-0000-0000-000000000000
    clientId: 00000000-0000-0000-0000-000000000000
    clientSecret: ${env:SQLFLOW_DEPLOY_SP_SECRET}
    subscriptionId: 00000000-0000-0000-0000-000000000000
    resourceGroup: rg-data
    dataFactoryName: adf-prod

invoke:
  type: adf
  pipeline: pl_refresh_marts
  servicePrincipal: deploy
  parameters:
    environment: prod        # string
    fullLoad: true           # JSON boolean
    batchSize: 5000          # JSON number
    windows:                 # JSON array of objects
      - from: "2026-01-01"
        to: "2026-01-31"

onErrorResume: false
```

The same blocks as hooks inside an ingestion document:

```yaml
flowType: ing
name: orders-ingestion
# source/target omitted; see the ingestion reference.

servicePrincipals:
  deploy:
    subscriptionId: 00000000-0000-0000-0000-000000000000
    resourceGroup: rg-data
    dataFactoryName: adf-prod

invokes:
  notify-pipeline:
    type: adf
    pipeline: pl_notify
    servicePrincipal: deploy

postInvoke: notify-pipeline
```

## See also

- [Service principals](service-principals.md)
- [Pre/post invoke hooks](hooks.md)
- [sqlflow auth](../cli/auth.md)
- [Flow document overview](overview.md)
