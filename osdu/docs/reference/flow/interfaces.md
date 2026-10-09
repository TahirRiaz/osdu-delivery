---
id: delivery-flow-interfaces
title: "A source with interfaces (interfaces): several OSDU kinds of one source in one delivery flow"
type: flow-reference
summary: "One delivery document for a whole source: what an interface declares, its ledger, the order interfaces run in, records that wait, and when an interface stops."
keywords:
  - interfaces
  - source with interfaces
  - several kinds one flow
  - interface order
  - "after"
  - waves
  - parallelinterfaces
  - ledger adoption
  - waiting record
  - x-osdu-relationship
  - failwhen
  - outagefailures
  - interface stopped
yamlPath: "interfaces"
related:
  - delivery-flow-delivery
  - delivery-guide-multi-kind-source
  - delivery-flow-routes
  - delivery-concept-record-lifecycle
  - delivery-concept-ledger
  - delivery-cli-check
  - delivery-cli-run
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlOverlay.cs
  - osdu/src/SqlFlow.Delivery/Model/SourceDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/InterfaceOrder.cs
  - osdu/src/SqlFlow.Delivery/Engine/InterfaceSchemas.cs
  - osdu/src/SqlFlow.Delivery/Engine/SourceRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/ReferenceReader.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/FailureGuard.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Waits.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryInterfaceCatalog.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
---

# A source with interfaces (interfaces): several OSDU kinds of one source in one delivery flow

A delivery document can describe a whole source system: the connection, the target and the defaults written once, and
under `interfaces` one entry for each OSDU kind the source delivers. Each interface has its own record table, mapping,
route and ledger; a run of the document runs the interfaces in the order their references need (wellbores before the
well logs that point at them), and an interface that fails stops only itself and what waits for it.

Use the interface form when one source feeds several kinds that refer to each other, or that share a connection,
credentials and schedule. A document without `interfaces` (the single form,
[delivery flow](delivery.md)) is a source with one interface; both forms run on the same engine. A kind delivered again
often, or managed by other people, can stay in a document of its own and still refer to records this one delivers.

## Example

```yaml
flowType: delivery
name: welldb-03-delivery
batch: welldb
description: The well database's wellbores and well logs, delivered as one source.

partitions: [dev, test]

source:
  connection: ${env:OSDU_DATA_DB}
  lastModified: update_date
  work: ../.work/welldb

target:
  endpoint: ${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: ${env:OSDU_CLIENT_ID}
    secretRef: ${env:OSDU_CLIENT_SECRET}
    token:
      url: ${env:OSDU_TOKEN_URL}
      body:
        scope: ${env:OSDU_SCOPE}

reliability:
  concurrency: 8
  parallelInterfaces: 2

failWhen:
  failedPercent: 20

interfaces:
  wellbores:
    description: One master-data--Wellbore record per wellbore.
    record:
      object: OsduData.silver.Wellbore
      key: [wellbore_id]
      primaryKey: RecId
    mapping: Wellbore@1.0.0

  welllogs:
    description: One work-product-component--WellLog record per log, referring to its wellbore.
    record:
      object: OsduData.silver.WellLog
      key: [log_id]
      primaryKey: RecId
    datasets:
      curves:
        object: OsduData.silver.WellLogCurve
        join:
          log_id: log_id
        orderBy: [curve_id]
    mapping: WellLog@1.0.0
    failWhen:
      consecutiveFailures: 50

schedule:
  cron: "0 * * * *"
  timezone: UTC
  values:
    partition: dev
```

The well log mapping fills `data.WellboreID`, which the WellLog template marks as a reference to
`master-data--Wellbore`, the entity type the `wellbores` interface delivers, so `welllogs` runs after `wellbores` without
an `after:`. [One source, several kinds](../guides/multi-kind-source.md) builds this document step by step.

## What the source declares for every interface

Everything outside `interfaces` is the source's, shared by every interface: `name`, `batch`, `parameters`, `partitions`,
`keepLedger`, `schedule`, `source.connection`, `source.work`, `render.mappings`, `target` (the endpoint, auth, headers and
the services under it), and the defaults an interface may override: `source.lastModified`, `source.systemColumns`,
`source.incremental`, `render.cacheVersion`, `render.parameters`, `change`, `reliability`, `verify`, `failWhen`,
`target.protocolOptions` and `target.validation`. The keys are the single form's ([delivery flow](delivery.md)).

What belongs to one interface is refused at the source level, each by name:

```text
the document declares interfaces, so these belong to an interface rather than the source: source.record (each interface's record table is interfaces.<name>.record).
```

The same message names `source.datasets`, `source.payloads`, `render.mapping`, `target.protocol`,
`target.protocolOptions.payload` and `target.workflow` when any of them is written at the source level.

## What an interface declares

| Key | Meaning |
| --- | --- |
| `record` | The interface's record table, as `source.record` in the single form: `object`, `key`, `primaryKey`, `scope`. Required. |
| `datasets` | Its child tables, as `source.datasets`. |
| `mapping` | The pinned mapping, `Name@version`, as `render.mapping`. Required. |
| `files` | The files each record carries (a payload set: `root`, `locationColumn`, `pattern`, `hashColumn`, `chunkCountColumn`), uploaded and registered before the record, or on the dataset route stored as the record. |
| `bulk` | The bulk data a DDMS keeps for each record, a payload set of the same shape, written after the record. |
| `workflow` | The workflow each record starts ([routes](routes.md)). |
| `route` | Names the route instead of letting what the interface declares decide it ([routes](routes.md)). |
| `ledger` | The name of an existing flow whose ledger the interface keeps. See [Ledger identity](#ledger-identity). |
| `after` | Interfaces of this document it waits for, beside the ones its references imply. See [Order](#order). |
| `failWhen` | Its stop rules, laid over the source's. See [When an interface stops](#when-an-interface-stops). |
| `description` | Free text. |
| `lastModified`, `systemColumns`, `incremental` | The source's settings, overridden for this interface. |
| `render` | `cacheVersion`, and `parameters` merged over the source's `render.parameters`. |
| `change`, `reliability`, `verify`, `protocolOptions`, `validation` | The source's `change`, `reliability`, `verify`, `target.protocolOptions` and `target.validation`, with the interface's keys laid over them. |

### How an interface's settings lay over the source's

An interface's block is laid over the source's key by key: a key the interface sets replaces the source's, a key it leaves
out keeps the source's value, and a nested block (`reliability.retry`) is laid over the same way. A map
(`render.parameters`, `protocolOptions.uploadHeaders`, `protocolOptions.workflowPayload`) is merged entry by entry, the
interface's entries winning. A list (`reliability.skipStatusCodes`, `protocolOptions.preserveDataKeys`) replaces the
source's list whole. `systemColumns` is merged column by column, and a column the interface opts out of (`fileName: ~`)
stays opted out for it. What one interface lays over a shared block never reaches another.

`reliability.parallelInterfaces` is the source's own setting: it is refused on an interface, and in a document that
declares no interfaces.

Every message about an interface names its own keys (`interfaces.welllogs.record.key`), and a message about a shared
setting names the interface it applied to (`failWhen.failedPercent of interface 'wellbores' must be above 0 and at most
100.`).

### Names and limits

An interface name is a letter followed by letters, digits, `_` and `-`, at most 64 characters, unique in its document
ignoring case. A document declares at least one interface and at most 200.

### The route of an interface

What an interface's records carry decides how they are delivered: nothing beside the record goes by the `storage` route,
`files` by the `file` route, `bulk` by the `ddms` route, both by `fileAndDdms`, and a `workflow` by the `workflow` route.
`route:` names one outright (`dataset`, `manifest`, `manifestAndDdms`, `dspdm` and `etp` are only ever named), and a route that cannot
deliver what the interface declares is refused when the document loads:

```text
interfaces.welllogs.route is storage, which writes the record alone, so interfaces.welllogs.files would never be sent. Remove it, or choose the route that delivers it.
```

An interface delivered through a DDMS needs to know where it is under the source's endpoint: a DDMS under `target.ddms`
with its root, or `protocolOptions.ddmsRoot` on the source or the interface. The full resolution table, every route's
options and the DDMS rules are on [routes](routes.md#an-interfaces-route) and [DDMSs](ddms.md#where-a-record-goes). `sqlflow check` prints each interface's route
and why it took it.

## Ledger identity

Every interface keeps a ledger of its own: its records, submissions, watermarks, OSDU id claims and statistics are kept
under the name `<flow>/<interface>` (`welldb-03-delivery/welllogs`). In a flow that works in partitions, each partition
keeps its own, `welldb-03-delivery/welllogs@test`, except the partition marked `keepLedger`
([partitions](../concepts/partitions.md)). A document in the single form keeps the ledger of its own name.

`ledger: <name>` keeps the ledger of an existing flow instead. Moving single-form flows into one source that way loses no
history and sends nothing again that has not changed:

```yaml
interfaces:
  wellbores:
    ledger: welldb-wellbore-03-delivery
    record:
      object: OsduData.silver.Wellbore
      key: [wellbore_id]
      primaryKey: RecId
    mapping: Wellbore@1.0.0
```

Remove the flow whose ledger was adopted. While two flows keep one ledger, the repository sync warns:
`the ledger '<name>' is kept by ... Every one of them delivers the same records under it; remove or rename the flow
whose ledger an interface adopted, so only one of them keeps it.` Two interfaces of one document never keep the same
ledger, and a ledger name is at most 200 characters.

## Order

An interface waits for another in two cases:

- **Its records refer to what the other delivers.** A property its mapping fills, however it fills it (a column, the
  cache, a search, a literal), whose template marks it with `x-osdu-relationship`, refers to the entity types named
  there: `data.WellboreID` of a WellLog refers to `master-data--Wellbore`. The interface waits for every other interface
  of the document whose mapping renders that entity type. A relationship that names only a group (`Datasets[]` refers to
  `dataset`) waits for every interface delivering a kind of that group. A reference to a kind no other interface delivers
  is not waited for: those records are OSDU's already, or another source's.
- **`after:` names the other.** It adds what the templates do not show.

A run takes the interfaces in waves. The first wave holds every interface that waits for nothing; each later wave the
interfaces whose dependencies all ran before it. Within a wave the interfaces are taken by OSDU group (reference data,
master data, datasets, work product components, work products, then any other group) and then as the document lists
them, up to `reliability.parallelInterfaces` (default 4, 1 to 32) at once. Each one plans, fans out and drains exactly as
a run of that interface alone does. A run deleting the ledger takes the waves backwards, so a record goes only after
everything of the source that refers to it.

### Cycles

OSDU's templates refer both ways (a wellbore to its definitive trajectory, the trajectory to its wellbore), so two
interfaces can wait for each other. Such a cycle is cut, and the reference that is not waited for is reported with why:

1. A reference the document's `after:` orders the other way is not waited for.
2. Otherwise, a reference from an earlier group in the order above to a later one is not waited for (the wellbores run
   before the trajectories). The reference points back, and it resolves once the other interface has delivered.
3. Interfaces of one group that refer to each other are refused until `after:` says which one waits:
   `The interfaces a -> b -> a wait for each other (...), and neither after: nor their OSDU groups say which goes first.
   Name the interface that waits for the other with after:.`

The references are read from the mappings and the templates they pin, which live in the module's database, so the order
is worked out when a run starts (its preflight refuses a cycle nothing cuts), by `sqlflow check`, and by the control
plane's listing of a flow's interfaces. When the document is read only `after:` is checked: naming an interface the
document does not declare, the interface itself or one interface twice is refused, and so are interfaces whose `after:`
wait for each other:

```text
the interfaces wellbores -> welllogs -> wellbores wait for each other through after:, so none of them could run first.
```

`sqlflow check` prints the order and each interface's wave and waits:

```text
welldb-03-delivery: 2 of 2 interface(s) checked, in the order they run: wellbores then welllogs
OK  welldb-03-delivery/wellbores (...)
    ledger      welldb-03-delivery/wellbores@dev
    route       storage: interfaces.wellbores declares no files and no bulk, so each record is written through the storage service
    wave        1
    waits for   nothing
...
OK  welldb-03-delivery/welllogs (...)
    ...
    wave        2
    waits for   wellbores: osdu.data.WellboreID refers to master-data--Wellbore, which wellbores delivers (osdu:wks:master-data--Wellbore:1.3.0)
```

## Records that wait for records

Order between interfaces is not the whole story: one well log can be ready while its wellbore is held. When a document is
rendered to be sent, the OSDU ids its relationship properties hold (each without its version, the record's own id left
out) are kept beside it. When a worker claims the record, it is left **waiting** instead of sent if one of those ids is
held by another record of the ledger, in the same partition and of any flow, that has not landed yet. Its history says
which record it waits for:

```text
waits for dev:master-data--Wellbore:WB-0001 (data.WellboreID): record 'Wellbore A-1' of welldb-03-delivery/wellbores holds it and has not delivered it yet (pending)
```

Waiting charges no attempt and needs no worker. When the record it waits for lands, every record waiting for that id
goes back to pending and the next claim decides again, so a record that still refers to something undelivered waits
again, for that one. A run also sends back the waits nothing holds any more before it plans (the record left the ledger,
or a verify found it landed).

An id is not waited for when no record of the ledger holds it (it is OSDU's, or another system's), when only a removed
record holds it, or when it belongs to an interface the source's order does not wait for, because the order cut the
reference as one that points back. Every decision is taken under one lock, and a record never waits for a record whose
own wait leads back to it, so records never wait for each other in a circle; a chain of waits longer than 64 records
counts as leading back, and the record is sent.

Releasing a waiting record by its key sends it without waiting. `target.verifyReferences: storage` goes further for a
source that must not write a reference to nothing: the ids no record of the ledger holds are asked of OSDU's storage
service before the record is sent ([preflight](../concepts/preflight.md#references-checked-in-storage)). Statuses, attempts
and releases are on [record lifecycle](../concepts/record-lifecycle.md).

## When an interface stops

A run of the source first checks every interface it selected, before anything is planned or sent: the ledger, each
mapping with its template and cache, each route against the kind its mapping renders, the order, each record table's
shape, and, for the operations that use them, each route's service and credentials and each mapping's legal tags. Every
finding is reported at once and nothing runs:

```text
The preflight of 'welldb-03-delivery' found 2 problem(s), so nothing was planned or sent: interface 'wellbores': ... | interface 'welllogs': ...
```

After that, a record with a data problem is held and its interface goes on. An interface stops when something fails for
it as a whole (its planning, its source, its work location) or when its records' failures cross its `failWhen` rules:

| Key | The interface stops when | Default |
| --- | --- | --- |
| `outageFailures` | this many records in a row could not reach the service (a transport failure, a timeout, HTTP 408, 429 or 5xx) or were refused by it (HTTP 401 or 403), with none delivered in between; 0 turns the rule off | 25 |
| `consecutiveFailures` | this many records in a row failed with problems of one class (data, connection or permission), with none delivered in between; at least 1 | not set |
| `failedPercent` | held and failed records reach this share (above 0, at most 100) of the records the run settled, judged once `minRecords` have settled. A planning pass that holds this share of the records it took up stops the interface before it sends anything | not set |
| `minRecords` | how many records settle before `failedPercent` is judged; at least 1 | 100 |

A record scheduled for another try counts toward the failures in a row, not toward the share. A stopped interface hands
back the records it had not sent without charging them a try and closes its submission as failed (`stopped: <why>`).
The interfaces waiting for it are skipped (`it waits for wellbores, which did not complete`), and the others carry on.
The run ends failed, and its outcome lists every interface with its route, wave, what it waited for and its state
(`completed`, `stopped` or `skipped`) with the reason; the trace carries `interface.started`, `interface.completed`,
`interface.stopped` and `interface.skipped`. What finished is in the ledger, so the next deliver run does what is left.

## Running part of a source

| What the run names | What runs |
| --- | --- |
| nothing | every interface, in waves |
| `interfaces` in the payload | only those, in waves; an interface they depend on is not run, and its records are read from the ledger as they are |
| `interface` in the payload | that one interface, as a run of it alone |
| a submission | the interface whose ledger registered it |

```bash
sqlflow run flows/welldb-03-delivery.yaml --set partition=dev --payload '{"interfaces":["welllogs"]}'
```

A run on records, on fan-out slices, or a reversal works in one interface's ledger and names it with `interface`. The
module's verbs take `--interface` (`sqlflow check`, `preview`, `values`, `records`); `check` without it checks every
interface ([running an OSDU flow](../cli/run.md)).

## Load errors

| Cause | Message |
| --- | --- |
| No interface listed | `interfaces lists no interface. List each OSDU type the source delivers under it, or write the document in the single form.` |
| A bad name | `interfaces names an interface '1logs'; an interface name is a letter followed by letters, digits, '_' and '-', at most 64 characters.` |
| Two names differing by case | `interfaces declares 'Wellbores' more than once (interface names are compared ignoring case, as the ledger identities made from them are).` |
| `after:` naming an unknown interface | `interfaces.welllogs.after names 'logs', which is not an interface of this document; it declares wellbores, welllogs.` |
| `after:` naming itself | `interfaces.welllogs.after names the interface itself.` |
| One ledger kept twice | `the interfaces wellbores, welllogs would keep the same ledger ('welldb-wellbore-03-delivery'); each interface keeps a ledger of its own.` |
| `parallelInterfaces` on an interface | `interfaces.welllogs.reliability.parallelInterfaces is the source's setting: how many of its interfaces run at once. Set it under reliability.` |
| `bulk` with no DDMS location | `interfaces.welllogs is delivered through a DDMS, and the source's endpoint is the platform every interface reaches its services under. Say where the DDMS is under it: ...` |

## See also

- [Delivery flow](delivery.md): every key both forms share.
- [One source, several kinds](../guides/multi-kind-source.md): wellbores then well logs, end to end.
- [Routes](routes.md): how what an interface declares decides its route.
- [Record lifecycle](../concepts/record-lifecycle.md): waiting, held, failed and released records.
- [Submissions](../concepts/submissions.md): what one interface's run plans and drains.
