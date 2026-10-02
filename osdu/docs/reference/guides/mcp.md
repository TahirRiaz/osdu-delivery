# The MCP server

OSDU Delivery ships a Model Context Protocol server, `osdu-delivery-mcp`, so an AI assistant can answer questions
about the estate from the same control plane the GUI and the CLI use. It is SQLFlow's MCP server composed with the
delivery module: everything SQLFlow's server answers about the catalog, lineage, runs, schedules and its documentation,
plus the `delivery_*` tools over the ledger and the module's definitions. SQLFlow documents its own tools in
[../../../../sqlflow/tools/README.md](../../../../sqlflow/tools/README.md); this page covers what OSDU Delivery adds
and changes.

## Metadata only

The server answers from metadata. That is a rule of the product, and it holds in both halves of the server.

| Returned | Never returned |
| --- | --- |
| The ledger's bookkeeping about a record: its custody state, the OSDU id and version it landed as, hashes, attempts with their outcome and error, the source file and row it came from, who acted on it and when. | The record's content: what OSDU holds for it, what a mapping renders, its source row. |
| Definitions: flows, interfaces, mappings (the YAML), templates and OSDU schemas, cache flows and the types they declare, assertion tests, dimensions. | Cached values, and the value a cached record held before and after a change. |
| Counts and health: records per state, what a submission planned and delivered, cache versions and how many records each changed, test outcomes, dimension builds. | A dimension's values and keys, and the records an assertion quotes as examples (they are counted). |
| Configuration: partitions, and the central configuration's values, which are plain values or secret references, never secrets. | Rows of any table, and the contents of any file. |

A record is still identified: its OSDU id, its source key, its label and the name of the file it came from are how a
record is found and traced, and they are part of every answer about it.

SQLFlow's server has tools that read rows or reach a datasource. This host leaves them out, so they are neither listed
nor callable: `prepare_query`, `run_query`, `detect_unique_key`, `check_duplicate_keys`, `compare_baseline`,
`dataops_capabilities`, `analyze_warehouse_health`, `discover_source` and `scaffold_ingestion_flow`. The list is
`WITHHELD` in `osdu/hosts/osdu-delivery-mcp/src/lib.rs`; a tool SQLFlow adds later is offered until it is added there,
and `tests/server.rs` fails when a new tool of SQLFlow's has not been decided on.

## The tools

Every `delivery_*` tool calls an endpoint under `/api/v1/delivery` and nothing else, so what a tool can do is what the
caller's token can do. Reading needs the `read` scope; the six operator actions need `operate`.

| Question | Tool |
| --- | --- |
| Was this record delivered? Which records came from this file? | `delivery_find_records` |
| Why did it fail? Who deleted it? Which file and row is it from? | `delivery_record` |
| How is this flow doing? Where does it deliver? What does a run need? | `delivery_flow` |
| Which records of this flow failed, are held, drifted, or came from that run? | `delivery_flow_records` |
| What did the last run plan and deliver? Which batch is stuck? | `delivery_submissions` |
| What did the last retrieval bring back, and where is it? | `delivery_retrievals` |
| Who did what, and when? | `delivery_activities` |
| Which partitions are there, and what do we deliver to each? | `delivery_partitions` |
| Which OSDU does this flow really deliver to? | `delivery_config` |
| Is the cache fresh? Where does this type come from? What reads it? | `delivery_caches` |
| When did the cache last change? | `delivery_cache_versions` |
| What awaits approval, and what will it redeliver? | `delivery_cache_changes` |
| Which mapping does this flow use? Which flows does this mapping affect? | `delivery_mappings` |
| Is this mapping valid? What does it leave required and empty? | `delivery_check_mapping` |
| Start a mapping for a template. | `delivery_scaffold_mapping` |
| Which properties does this kind require? What type is this field? | `delivery_templates` |
| Which versions of a kind does a release publish? | `delivery_osdu_schemas` |
| What breaks if we move to the next version of a kind? | `delivery_osdu_schema_compare` |
| Are the data quality tests passing? What failed last night? | `delivery_assertions`, `delivery_assertion_runs` |
| Which dimensions exist, and are they up to date? | `delivery_dimensions` |

The operator actions, each written to the audit trail under the caller's name by the endpoint it calls:

| Action | Tool |
| --- | --- |
| Ask a flow's OSDU whether it still answers. | `delivery_probe_target` |
| Have one record compared with what OSDU holds, and drift recorded. | `delivery_verify_record` |
| Reconcile the ledger with the ingestion tables. Sends nothing. | `delivery_sync_with_source` |
| Put held records back to pending. | `delivery_release_records` |
| Send one record to OSDU again. | `delivery_redeliver_record` |
| Approve or reject pending cache changes. | `delivery_decide_cache_changes` |

An OSDU flow is run with SQLFlow's `trigger_run`, which carries the kind's own arguments: `operation`, `values` and
`payload`, as `sqlflow trigger` does ([../cli/delivery.md](../cli/delivery.md#the-run-options)).

Removing records from OSDU is not offered. It is an operator's decision made in the GUI, where the confirmation shows
how many records it takes away and from which endpoint and partition.

The doc tools (`search_docs`, `get_doc`) cover this documentation beside SQLFlow's reference, section by section, with
ids that start with `delivery-`. The flow-language tools (`validate_flow`, `list_flow_keys`, `describe_flow_key`) know
the flow kinds and documents the module adds, from the key census in [../../census](../../census/README.md).

Results carry links into the GUI: a record's page, a submission's, an assertion run's report, the cache page of a
partition. Set `SQLFLOW_GUI_URL` to the GUI's public address to make them absolute.

## Installing it for a person

Build the binary, then register it with the assistant. `install` prints the registration for a client.

```bash
cargo build --release --manifest-path osdu/hosts/osdu-delivery-mcp/Cargo.toml
osdu/hosts/osdu-delivery-mcp/target/release/osdu-delivery-mcp install claude
# claude mcp add osdu-delivery -- <path>/osdu-delivery-mcp
```

The server reads `SQLFLOW_CONTROL_PLANE_URL` (the control plane's address; `http://localhost:5000` for a development
estate started with `dev.bat`). From the assistant, run the `login` tool: it prints an address and a code to approve
in a browser, and the server then holds a personal access token it renews by itself. The token carries the scopes of
the person who approved it, so the assistant can do what that person can and no more. The server keeps its address and
token in `~/.sqlflow/osdu-delivery-mcp-config.json` and `~/.sqlflow/osdu-delivery-mcp-token.json`, apart from a
`sqlflow-mcp` installed on the same machine.

## Running it as a service

`osdu-delivery-mcp http` serves streamable HTTP for remote clients, and it is what the control plane's chat assistant
calls. It holds no credential: every request must carry `Authorization: Bearer <token>`, and the server forwards that
token to the control plane, which enforces its scopes as it does for the GUI.

```bash
docker build -f osdu/deploy/docker/mcp.Dockerfile -t osdu-delivery-mcp:latest .
docker run -p 8787:8080 -e SQLFLOW_CONTROL_PLANE_URL=http://controlplane:8080 osdu-delivery-mcp:latest
```

| Variable | Meaning |
| --- | --- |
| `SQLFLOW_CONTROL_PLANE_URL` | The control plane the tools call. Required. |
| `SQLFLOW_GUI_URL` | The GUI's public address, for absolute links in results. |
| `SQLFLOW_MCP_HTTP_BIND` | The listen address. The image sets `0.0.0.0:8080`; a bare start listens on loopback. |
| `SQLFLOW_MCP_HTTP_ALLOWED_HOSTS` | A `Host` header allowlist. Unset disables the check, which is safe because every request needs a bearer token. |
| `SQLFLOW_MCP_LOG` | The log filter (default `info`). Logs go to stderr. |

`GET /healthz` answers without a token, for the platform's probe; the MCP endpoint is `/mcp`. The compose stack has the
service under the `mcp` profile ([../../../deploy/README.md](../../../deploy/README.md)).

## The chat assistant

The control plane's chat assistant (`ControlPlane:Assistant`, off by default) uses an MCP server as its tool source.
Point `ControlPlane:Assistant:Mcp:ServerUrl` at this server's `/mcp`. The assistant forwards the signed-in user's own
token, so it reads what that user may read.

The assistant is given an allowlist of tools. OSDU Delivery's control plane host sets it to SQLFlow's read-only
metadata tools and the `delivery_*` read tools; the operator actions are left off, as `trigger_run` is, so a chat
answer never starts work. A deployment that wants another list sets `ControlPlane:Assistant:Mcp:AllowedTools`.

## Where it lives

| Path | What |
| --- | --- |
| `osdu/hosts/osdu-delivery-mcp` | The host crate: the delivery module's tools, instructions, documentation index and links, and the program. |
| `sqlflow/tools/sqlflow-mcp` | SQLFlow's server, a library since this project made it composable ([../../../../docs/sqlflow-changes.md](../../../../docs/sqlflow-changes.md)). |
| `osdu/deploy/docker/mcp.Dockerfile` | The image. |

```bash
cargo test --manifest-path osdu/hosts/osdu-delivery-mcp/Cargo.toml
```

The tests run against a control plane of their own that answers the module's routes, so they need no database, no
OSDU and no network.
