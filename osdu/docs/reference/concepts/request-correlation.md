---
id: delivery-concept-request-correlation
title: "The correlation id every OSDU request carries, and how to follow a run into the platform's logs"
type: concept
summary: "Every OSDU request carries correlation-id: osdu-delivery-<repository>-<operation>-<run id>; where each part comes from and where the value is kept."
keywords:
  - correlation id
  - correlation-id header
  - telemetry
  - telemetry.operation
  - operation code
  - repository code
  - run id
  - azure logs
  - application insights
  - log analytics
  - trace a run
  - osdu-delivery header
related:
  - delivery-flow-repository
  - delivery-flow-overview
  - delivery-concept-ledger
  - delivery-concept-run-trace-and-metrics
  - delivery-concept-protocols
  - delivery-concept-explorer
  - delivery-guide-operations-runbook
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Http/OsduCorrelation.cs
  - osdu/src/SqlFlow.Delivery/Engine/RunCorrelation.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowTelemetry.cs
  - osdu/src/SqlFlow.Delivery/Documents/RepositoryDocuments.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduHttpClient.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Etp/EtpConnection.cs
  - osdu/src/SqlFlow.Delivery/Engine/Workflows/WorkflowXCom.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/DeliveryOperations.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/PartitionSchemaIndex.cs
  - osdu/src/SqlFlow.Delivery/Http/HttpExecutor.cs
  - osdu/src/SqlFlow.Delivery.Setup/OsduSignIn.cs
---

# The correlation id every OSDU request carries, and how to follow a run into the platform's logs

Every request OSDU Delivery sends to an OSDU service carries a `correlation-id` header whose value says what sent it:
which repository, which operation, which run. The platform's services log that header with everything they do for the
request, so a run, or every run of one repository, can be found in the platform's own logs by its value.

```text
correlation-id: osdu-delivery-wl-dim-0192f3a1-7c4e-7b2a-9d10-5e8f2c6a4b31
                ^             ^  ^   ^
                product       |  |   the run's id
                              |  the flow's operation code
                              the repository's code
```

## The value

| Sent by | Value |
| --- | --- |
| A run of a flow | `osdu-delivery-<repository>-<operation>-<run id>` |
| A run of a flow with no repository document above it | `osdu-delivery-<operation>-<run id>` |
| An operation of the product's own (a probe, a read the explorer makes) | `osdu-delivery-<operation>-<id>`, a new id each time |
| A request sent outside both (a tool driving a route itself) | `osdu-delivery-<id>`, a new id per request |

Where each part of a run's value comes from:

| Part | Comes from | When there is none |
| --- | --- | --- |
| `osdu-delivery` | The product: every value starts with it. | |
| The repository's code | `code` in the [repository document](../flow/repository.md), `osdu-delivery.yaml`, nearest above the flow's file. | The part is left out, and the run log says why. |
| The operation code | The flow's `telemetry.operation`. | The flow kind's code ([operation codes](#operation-codes)). |
| The run's id | The run: the id it is listed under in the GUI, the API and `sqlflow run`. Nothing new is minted for it. | |

Two files set the codes, both versioned with the flows. At the repository's root:

```yaml
# osdu-delivery.yaml
documentType: repository
code: wl
```

In a flow, only to name a code other than its kind's:

```yaml
telemetry:
  operation: whdr
```

Every request of a run carries the same value: its searches, its writes, its retries and its read-backs. One value
finds the whole run, and a prefix finds every run of a repository or a flow. Each part of a delivery split over nodes is
a run of its own and carries its own id.

A code is a lower-case letter, then lower-case letters and digits, at most 16 in all, with no hyphen. The value therefore
splits on its hyphens: `osdu-delivery`, then the codes, and the id is always its last 36 characters.

### Operation codes

A flow names its own code under `telemetry` (any kind):

```yaml
flowType: delivery
name: welldb-welllog-03-delivery
telemetry:
  operation: whdr
```

Left out, a run carries its kind's code:

| Kind | Code |
| --- | --- |
| delivery | `dlv` |
| cache | `cache` |
| retrieval | `ret` |
| assertion | `ast` |
| dimension | `dim` |
| inventory | `inv` |
| removal | `rmv` |

The code names the flow, not the run's operation: a `plan` and a `deliver` of the same flow carry the same code, and the
run's id says which run it was. A code that breaks the rule is refused when the document loads:

```text
ERROR  flows/welldb-welllog-03-delivery.yaml: telemetry.operation 'Header-Log' is not an operation code: a lower-case letter, then lower-case letters and digits, at most 16 in all and no hyphen, so the correlation id splits on its hyphens. Leave it out to take the kind's own, 'dlv'.
```

The product's own operations carry their own code whichever flow's target they read through, and whatever repository
that flow is in:

| Code | Operation |
| --- | --- |
| `probe` | A target probe (`delivery-probe`), a person's or the scheduled one's. |
| `read` | A record read back from OSDU for its record page (`delivery-read`). |
| `explore` | The explorer's reads of a partition (`delivery-explore`): types, searches, a record, validation, dimension samples. |
| `preview` | A record rendered from its source row and sent nowhere (`delivery-preview`). |
| `values` | A mapping's value check (`delivery-check-values`). |
| `row` | A record's source row (`delivery-source`), which reads the ingestion tables. |
| `scope` | The values a run form offers for a parameter (`delivery-scope-values`), read from the ingestion tables. |
| `schema` | A reading of a partition's schemas, kept for the explorer's referenced-by. It serves every later ask, so it carries an id of its own rather than the ask's that started it. |
| `signin` | The local setup's check of a flow's OSDU client (`osdu-delivery-setup`, `sqlflow dev`). |

## What carries it

Every request to an OSDU service: storage, search, schema, file, dataset, legal, workflow and the DDMSs, which all go
through the client that applies a flow's headers, and beside them the Reservoir DDMS's ETP session (on its WebSocket
upgrade), the Airflow REST API behind the Workflow service, and the setup check's read of a legal tag.

Two kinds of request carry nothing: the token endpoint's, which is not OSDU, and a request to a location a service signed
(a landing zone, a dataset's retrieval URL, an object store a DDMS hands out), which carries its own authorisation and
none of the flow's headers, since some object stores refuse a request that also carries a bearer.

A flow cannot set the header itself. A `correlation-id` in any `headers` block is refused:

```text
ERROR  flows/welldb-retrieval-01-wellbores.yaml: source.headers names 'correlation-id', which every request of a run carries as the run sets it: osdu-delivery, the code of the repository (osdu-delivery.yaml), the flow's operation code (telemetry.operation) and the run's id. Remove the header.
```

## Where the value is kept

| Where | What it shows |
| --- | --- |
| The run log and live trace | The run's first lines say what its requests carry, and which document gave the repository's code. |
| A record's attempt in the ledger | `correlationId`: what the try's requests carried ([the ledger](ledger.md#osduattempt-every-try-and-every-decision-that-changed-the-record)). |
| A refused request's error | `(correlation-id <value>)`: the id the service answered with, else the one sent. |
| An explorer or read-back answer | `correlationId`, the value its reads carried. |

```text
2026-10-29 09:13:38.658Z INFO  run   dimension flow 'welldb-welllog-05-dimensions': plan (parameters: operation plan) requested by cli:jane@host, run 01a186ef-b444-7732-bb2e-9a7f4dfd4767
2026-10-29 09:13:38.658Z INFO  run   OSDU requests of this run carry correlation-id osdu-delivery-wl-dim-01a186ef-b444-7732-bb2e-9a7f4dfd4767, the repository's code from /repos/estate/osdu-delivery.yaml
```

A flow with no repository document above it says so instead:

```text
INFO  run   OSDU requests of this run carry correlation-id osdu-delivery-dim-01a186ef-b444-7732-bb2e-9a7f4dfd4767; no osdu-delivery.yaml in the flow's folder or above it names the repository's code
```

## Finding a run in the platform's logs

On Azure, OSDU's services read the header through the platform's Azure library (`os-core-lib-azure`): each request's
`correlation-id` goes on every log line written while the service handles it (`Slf4jMDCFilter`), its transaction log
records it beside the partition and the content type, the only request headers it records (`TransactionLogFilter`), and
the messages the service publishes for others (the indexer, the workflow engine) carry it on. A header of any other name
would not reach those logs, which is why the value is carried in this one.

Filter the logs on the value or a prefix of it:

| To find | Filter on |
| --- | --- |
| One run | The whole value, `osdu-delivery-wl-dim-01a186ef-b444-7732-bb2e-9a7f4dfd4767` |
| Every run of one repository | `osdu-delivery-wl-` |
| Every run of one flow, or of a kind's flows | `osdu-delivery-wl-dim-` |
| The product's own operations | `osdu-delivery-explore-`, `osdu-delivery-probe-`, ... |

A record's attempt names the value its requests carried, so a failed try on a record's Timeline leads to what the
service logged for it.

## See also

- [The repository document](../flow/repository.md): the code and where a flow finds it.
- [What every OSDU document shares](../flow/overview.md#what-every-osdu-document-shares).
- [The delivery ledger](ledger.md) and [what a run's trace says](run-trace-and-metrics.md).
