# sqlflow worker: an OSDU Delivery node

```bash
sqlflow worker --url <control-plane> [--token <ref>] [--pool a,b] [--poll-seconds N] [--drain-seconds N]
```

The node runtime is SQLFlow's: the claim loop, the heartbeat, version pinning and git materialization, execution
and recording, failure handling, crash recovery, the drain on shutdown and the options above are documented in
[../../../../sqlflow/docs/reference/cli/worker.md](../../../../sqlflow/docs/reference/cli/worker.md). In an
OSDU Delivery deployment the node is
`osdu/hosts/SqlFlow.Delivery.Worker.Host`: the same runtime with the OSDU module installed, so a delivery,
retrieval or cache flow executes on it exactly as it does from the command line.

This page covers what an OSDU Delivery node needs that a plain SQLFlow node does not.

## It takes work outbound, and holds no catalog connection

The node polls the control plane's dispatcher over HTTP and is handed a run with everything it needs: the run's
definition, its snapshotted YAML, its lineage context, and the channel its trace and outcome travel back on. It
opens no catalog connection. `--url` defaults to `SQLFLOW_URL`, and the credential is a personal access token
minted with the `node` scope (`--token`, or `SQLFLOW_TOKEN`, as a value or a `${env:...}` / `${keyvault:...}`
reference resolved here on the node).

Every connection is outbound: HTTPS to the control plane, SQL to the ingestion database, HTTPS to the OSDU
endpoint and the payload storage, and git to the flow repository's remote for a pinned run.

## The ledger is read and written here

The delivery engine reads and writes the `osdu` schema per record while it plans and delivers: that is what makes
every attempt and every outcome traceable. Because the node has no catalog connection, **the OSDU module database
needs a connection reference of its own on this tier**, supplied as an environment variable on the node. Without
it the module reports that its database uses the catalog connection but a worker node has none, and says to give
it a reference of its own.

## What else the node holds

Every `${env:...}` reference the pool's flows declare resolves here, never in the control plane:

| Reference | Used for |
| --- | --- |
| The ingestion database connection | Reading the keyed ingestion tables the OSDU flow delivers from. |
| The payload storage credential | Reading the payload files a record points at, and writing the run's work batch files. Resolved through `SQLFLOW_AZURE_AUTH` and the `AZURE_*` family on Azure storage. |
| The OSDU endpoint credential | The OAuth2 client credentials, and any API management key the target requires. |

`SQLFLOW_DELIVERY_ALLOW_LOOPBACK=true` lets a flow target a loopback address (a local OSDU stub, the tests). It is
off by default: the URL guard refuses loopback and private targets, so a misconfigured endpoint cannot quietly
deliver to something inside the node's own network.

## Compute tasks run here too

A probe of a flow's target, a read-back of a delivered record, and a removal are queued as compute tasks and drain
on a node, because only a node can reach the OSDU endpoint. They are drained ahead of runs on their own bounded
gate, so a node busy with long deliveries still answers the GUI promptly, and a burst of them never starves run
execution.

## The drain is what keeps a scale-in cheap

A stopping node finishes the runs it already holds, for up to `--drain-seconds` (default 540), and keeps
heartbeating for the whole drain so the liveness sweep leaves that work alone. **Set the orchestrator's
termination grace period above that window.** A severed delivery loses no records (the ledger leases each record
to one attempt at a time, and a lease the severed node held expires), but the run is requeued, that consumes one
of its execution attempts, and the work is repeated. See
[../guides/deployment.md](../guides/deployment.md#scale-in-must-not-sever-a-delivery).

## Container image

`osdu/deploy/docker/worker.Dockerfile` packages the node. Its entrypoint composes the invocation from
`SQLFLOW_WORKER_POOL`, `SQLFLOW_WORKER_POLL_SECONDS` and `SQLFLOW_WORKER_DRAIN_SECONDS`, so the control plane URL
and the token never appear on the command line or in `ps` output. All three are optional, so a bare container
takes untargeted runs on the default cadence. The container exposes no ports.

## See also

- [../../environment-variables.md](../../environment-variables.md): every variable a node reads.
- [../../../deploy/README.md](../../../deploy/README.md): minting the node token, and the pool manifests.
- [delivery.md](delivery.md): the operations a run on a node performs.
