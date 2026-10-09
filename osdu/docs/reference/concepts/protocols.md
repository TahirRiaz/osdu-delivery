---
id: delivery-concept-protocols
title: "How a delivery runs on any route: resumable steps, payload parts, legal tags and unfinished deliveries"
type: concept
summary: "What every delivery route shares: resumable steps and returned values, payload parts, the legal tag check before a run, request rules and the undo."
keywords:
  - delivery protocol
  - retry-after
  - resumable steps
  - returned values
  - target state
  - payload parts
  - legal tag check
  - validatelegaltags
  - unit of work
  - unfinished delivery
  - undo
  - artifact
  - inline retry
  - redirects
related:
  - delivery-flow-routes
  - delivery-flow-ddms
  - delivery-concept-record-lifecycle
  - delivery-concept-removal-and-reversal
  - delivery-concept-ledger
  - delivery-concept-change-detection
  - delivery-cli-records
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Protocols/DeliveryProtocol.cs
  - osdu/src/SqlFlow.Delivery/Protocols/IDeliveryProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/ProtocolFactory.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/LegalTagValidator.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduRecordProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/FileUploads.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/WellboreDdmsV3Shape.cs
  - osdu/src/SqlFlow.Delivery/Model/PayloadParts.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/SourceRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/UndoRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/ArtifactUndo.cs
  - osdu/src/SqlFlow.Delivery/Ledger/Artifacts.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery/Http/HttpExecutor.cs
  - osdu/src/SqlFlow.Delivery/Http/RetryPolicy.cs
  - osdu/src/SqlFlow.Delivery/Http/UrlGuard.cs
  - osdu/src/SqlFlow.Delivery/Http/NetworkPolicy.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/specs/core/legal/openapi.yaml
---

# How a delivery runs on any route: resumable steps, payload parts, legal tags and unfinished deliveries

A delivery flow sends its records by one of ten routes: `storage`, `file`, `dataset`, `manifest`, `ddms`,
`fileAndDdms`, `manifestAndDdms`, `workflow`, `dspdm` and `etp`. A flow in the single form names its route with
`target.protocol`; an interface of a source gets one from what it declares. What each route sends, and its keys, is on
[Routes](../flow/routes.md); the DDMSs the `ddms` routes reach are on [DDMSs](../flow/ddms.md). This page is what every
route does the same way: identity, rendering, change detection, the ledger and the preflight gate never depend on the
route, and every route reports its work to the ledger in the same form.

## What a route does with a record

Each route is code, parameterised by the flow, and answers the same set of operations. The operations a run, a verify
pass, the record pages and a removal ask of it are these:

| Operation | What it does |
| --- | --- |
| Deliver | Sends one record, or a batch of records where the service takes arrays, and reports every step. |
| Verify | Reads the record back and compares the version OSDU holds with the one the ledger recorded. The `storage`, `file`, `dataset`, `manifest`, `manifestAndDdms` and `workflow` routes read up to 100 ids per request (`POST /api/storage/v2/query/records`), so a drift pass costs a handful of requests; `ddms` and `fileAndDdms` read each record from its DDMS; `dspdm` reads rows by the key DSPDM gave them, and `etp` lists each dataspace's objects once. |
| Read back | Returns the record as the target holds it. |
| Remove | Takes the record out of OSDU at one of three scopes: `record` (reversible), `history` (earlier versions purged) or `everything` (the record and every version purged). What each scope calls differs by route; see [Routes](../flow/routes.md#what-a-removal-does). |
| Probe | Asks the services the route reaches whether they answer, with the flow's credentials. |
| Undo | Takes back what an unfinished delivery left in OSDU ([below](#when-a-delivery-does-not-complete)). The `storage` route makes one atomic write and has nothing to undo. |
| Restore | Writes a record back as OSDU held it at an earlier version, for a reversal. Only the `storage` route, and the `ddms` route for Wellbore DDMS records under a platform endpoint, can; the others refuse, naming why ([Removal and reversal](removal-and-reversal.md)). |

## Steps and returned values

Every route reports each step that changes the target (a record write, an upload, a registration, a session, a workflow
trigger, a row save) as soon as it completes, with what the target returned. The worker writes the step onto the record
before the route moves on, so a crash never repeats a completed step and never loses an id a step minted. A retry of the
same pending work resumes after the last step that succeeded:

- a file the previous try uploaded is registered, not uploaded again;
- a Wellbore DDMS record the previous try wrote is not written again, and its bulk data goes next;
- a workflow run the previous try triggered is polled, not triggered again;
- a registration whose answer was lost is looked up (by its landing-zone path, through the search service) instead of
  registered a second time.

Three places in the `osdu` schema keep this:

| Where | What |
| --- | --- |
| `osdu.Record.PendingStepJson` | The completed steps of the pending delivery and what each returned, keyed by step. A retry reads it. |
| `osdu.Attempt.ResultJson` | Every step of one try, in order: its name, when it started and ended, the status the target answered, the values it returned, whether it was resumed from an earlier try, and its error. |
| `osdu.Record.TargetStateJson` | The values the target returned, merged over every delivery of the record: the record id and version, dataset ids, file sources, a session id, workflow run ids, a DSPDM row key, an ETP URI. A verify, a removal and an undo read it. |

The step names are what `sqlflow records show` and the record's history in the GUI list. The main ones:

| Route | Steps |
| --- | --- |
| `storage` | `records` |
| `file` | `upload-<n>` and `register-<n>` per file, `record-intent`, `records` |
| `dataset` | `storage-<set>` (the files staged, `<set>` being the payload set's name), `register-intent`, `register`, and `records` for a record written through storage |
| `manifest` | `upload-<n>` and `register-<n>` per file, `indexed`, `manifest`, `workflow`, `records`, `manifest-removed` |
| `ddms` (Wellbore DDMS) | `metadata-intent`, `metadata`, `session`, `bulk`, `payload` |
| `ddms` (other shapes) | `metadata` and the shape's own: `version` (Well Delivery), `content-<type>` (RAFS), `points-<n>` (historian), `lock`, `register`, `upload`, `close` (Seismic Store), `sync`, `rows-begin`, `rows-<n>`, `rows-done` (Reservoir Management) |
| `workflow` | `anchor-intent`, `anchor` (with `storage-<set>` when the anchor is registered with its files), `register-<input>`, `stage-<n>`, `results` |
| `dspdm` | `find`, `save-begin`, `save` |
| `etp` | `dataspace`, `transaction`, `objects`, `arrays`, `commit-intent`, `commit`, `fill`, `lock` |

A step whose name ends in `-intent` is written before a call whose id the service chooses, so the ledger knows what to
look for when the answer is lost ([When a delivery does not complete](#when-a-delivery-does-not-complete)).

## Payload parts

Most routes send at most one payload set beside the record, and the ledger keeps it as one folder and one content hash.
Four routes send their payload in parts:

| Route | Parts |
| --- | --- |
| `fileAndDdms` | `files`, then `bulk` |
| `manifestAndDdms` | `files` (when the flow declares them), then `bulk` |
| `workflow` | `files` (the anchor's own files, when declared), one part per workflow input, and the workflow run itself |
| `etp` | `files` (the object's XML) and `bulk` (the values of its arrays), each optional |

The plan resolves each part's folder and content hash from the record's row, as it does for a single payload. The record's
payload hash is a hash of the parts' hashes, so a change to any part is a payload change, and the pending payload lists
each part with its location and hash. The record's target state keeps the hash each part was last delivered with
(`payload.<set>`), and a delivery sends a part only when its hash differs from that one. New curves therefore do not
upload a record's files again, and new files do not send its curves.

A part also goes whatever its hash says when the work forces it: the record never delivered its payload, the flow sends
its payload every time (`change.payloadDetect: always`, or `change.onUnchanged: deliver`), or a redelivery names the part
(`files`, `bulk` or `workflow`). A workflow input declared `optional: true` that a record names no folder for is a part
without files. How a change is detected in the first place is on [Change detection](change-detection.md).

## Before a run: the legal tag check

Every record a mapping renders carries the same legal tags, and the storage service refuses a record whose tag is
unknown or expired, on every record that carries it. So the runs that plan or send (`deliver`, `intake` and `replan`)
ask the legal service about the mapping's tags first (`POST /api/legal/v1/legaltags:validate` under the endpoint, at most
25 names per request, which is the service's own limit). When it refuses any tag, the run fails before anything is
planned or sent:

```text
The legal service refuses 1 of the legal tag(s) mapping WellLog@1.0.0 puts on every record, so nothing was planned or sent: dev-welldb-public: <the reason the service gives>
```

A run over the interfaces of a source asks for each interface during its preflight, which reports every problem it
found at once (`The preflight of '<flow>' found <n> problem(s), so nothing was planned or sent: ...`).

| Setting | Effect |
| --- | --- |
| `target.protocolOptions.validateLegalTags: false` | The run does not ask, and logs that the tags were not checked. The storage service then refuses a bad tag record by record. |
| `target.protocolOptions.legalValidatePath` | Where to ask: a path under the endpoint, or an absolute http(s) URL. Needed only by a `ddms` flow whose endpoint is the DDMS itself (no `ddmsRoot`, no DDMS root under `target.ddms`), which does not reach the legal service by a path. Without it such a flow logs that the tags were not checked. Not checked is never read as valid. |

A verdict on a tag is kept for ten minutes, so the batches of one run do not ask again. The service answers 404 for a
request naming tags it does not know without saying which, so such a request is asked again name by name. A 404 that is
not the legal service's own error body (a gateway, a facade in front of a DDMS) fails the run as the service being
unreachable, not as every tag being invalid. A mapping of DSPDM rows renders no legal tags, and its runs skip the check.

## What happens when a try fails

A call that still fails after the executor's own repeats ([below](#requests-every-route-makes)) ends the try. The worker
then leaves the record pending for a later try with backoff, holds it until an operator releases it (a status no retry
can fix, or a rule the route checks before sending), or fails it once `reliability.retry.attempts` tries are used. Which
statuses do which, the record backoff, waiting records and releases are on
[Record lifecycle](record-lifecycle.md#retry-hold-fail).

## Requests every route makes

All routes send their HTTP requests through one executor, so these rules hold on every route:

- **What is repeated inside one call.** A request is repeated only when repeating it is safe: by its method (GET, HEAD,
  PUT, DELETE) or because the route declares the call safe by the service's own semantics (a record write under a
  client-supplied id, a reversible delete, a read by id, a search, the whole-bulk write of the Wellbore DDMS, a workflow
  trigger that names its own run id). It is repeated on 408, 425, 429, 503 and 504 and on a transport failure. 500 and
  502 are not repeated inline: the services answer them for deterministic failures as often as for passing ones, so they
  fall to the record's own backoff.
- **Never repeated.** A call whose service mints something on every accepted request: a file registration
  (`POST /api/file/v2/files/metadata`), a bulk session create, a session chunk and a session commit, and the uploads a
  storage provider takes by POST or PATCH. Not after a status, and not after a transport failure either.
- **Retry-After.** A wait the service asks for is never shortened (unless `reliability.retry.honorRetryAfter` is false,
  which ignores the header). A wait within `reliability.retry.maxDelayMs` is the
  floor of the backoff; a longer one travels with the failure, and the record's next try is no sooner than the service
  asked.
- **Content-Type.** Every request carries one, a bodiless request included, because the storage service answers a
  request without one with 415.
- **Redirects.** Followed by the executor, at most five. Every hop passes the URL guard (the scheme, the addresses the host
  name resolves to, `reliability.urlAllowlist`); a redirect from https to http is refused; a hop to another host carries
  none of the request's credentials or the flow's headers.
- **Addresses.** A connection opens only to an address the deployment reaches; `SQLFLOW_DELIVERY_PRIVATE_NETWORKS` names
  the private networks a node may reach ([Environment variables](environment-variables.md)). A refused address fails
  the request at once, without a retry.
- **Errors.** An error for a refused status, a transport failure or a timeout names the request URL without its query
  string, so a signed URL's credential stays out of it, and carries what the service said (its error body read for the
  message) rather than raw JSON. Every error is redacted again before the ledger stores it.

## When a delivery does not complete

A delivery of one record's pending work is a unit of work: it begins with the first call that can change OSDU, spans
every try while the record stays pending, and ends committed or aborted. Every object a unit creates in OSDU, or sets out
to create, is an artifact in `osdu.Artifact`, written in the same transaction as the step that made it. A call whose id
the service mints, and whose answer can be lost, is preceded by an intent: the artifact is written before the call with
what finds the object without its id (a file's landing-zone path, the record a session belongs to), and completed with
the id when the answer comes.

An aborted unit is undone:

| When | What undoes it |
| --- | --- |
| A try ends held or failed | The worker, at once, under the record's lease. |
| Newer work is planned for a record while a unit is unfinished | The claim that next takes the record, before the newer work is sent. |
| Anything left: an undo that failed and is past its backoff, a unit nothing claimed again | The sweep at the end of every `deliver` run and flow-wide `drain`, and the `undo` operation (`sqlflow run <flow.yaml> --operation undo`). |
| A removal, and deleting the ledger | The removal, before it takes the record out of OSDU. |

An undo removes what the unit created at the route's reversible scope (a soft delete, a logical DDMS delete, an abandoned
session, a released lock), writes back the version the unit replaced where the route can, and keeps, with why, what no
call removes. A record is removed only when the storage service's `createTime` says the unit created it (no earlier than
the unit's start, less five minutes for clocks); an update is given back its earlier version, never deleted. What a DDMS
made beside the record goes back before the record, and the record before the datasets it names; when one of them cannot
be undone yet, the others wait with it, so OSDU never serves a record naming what the undo already removed.

An undo is idempotent. One that cannot reach OSDU, or that OSDU refuses, is tried again after 1, 2, 4 ... minutes (at
most six hours apart), up to ten times, then left for an operator: `sqlflow records undos <flow.yaml>` lists what is
left, and `sqlflow run <flow.yaml> --operation undo --payload '{"force":true}'` tries the exhausted ones again. Newer
work for the record waits, uncharged, until the undo finishes, and the record is held once the undo has used its tries
([Waiting for an undo](record-lifecycle.md#waiting-for-an-undo)).

Some things no call removes, and an undo keeps them, named: the series versions the production historian accepted, a
Seismic Store dataset registered on gc (where one dataset's delete takes the files of every dataset in its subproject), RAFS content
kept in its own blob store, what a Wellbore DDMS session aggregated, and the files a registration left in a landing zone
or staging area. `sqlflow records artifacts <flow.yaml> --key <key>` lists every artifact of a
record with where it stands.
