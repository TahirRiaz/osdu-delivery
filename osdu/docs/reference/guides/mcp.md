---
id: delivery-guide-mcp
title: "The OSDU Delivery MCP server: connecting an AI assistant to the ledger, metadata only"
type: guide
summary: "osdu-delivery-mcp: SQLFlow's MCP server with the delivery_* tools, metadata only, its documentation search, install and HTTP service."
keywords:
  - mcp
  - mcp server
  - osdu-delivery-mcp
  - ai assistant
  - claude
  - delivery_* tools
  - delivery_find_records
  - metadata only
  - withheld tools
  - search_docs
  - streamable http
  - chat assistant
  - sqlflow_control_plane_url
  - operator action
related:
  - guide-chat-assistant
  - guide-slack-assistant
  - concept-authentication-and-identity
  - delivery-concept-api
  - delivery-concept-authentication-and-identity
  - delivery-guide-deployment
  - delivery-cli-run
sourceRefs:
  - osdu/hosts/osdu-delivery-mcp/Cargo.toml
  - osdu/hosts/osdu-delivery-mcp/build.rs
  - osdu/hosts/osdu-delivery-mcp/src/lib.rs
  - osdu/hosts/osdu-delivery-mcp/src/main.rs
  - osdu/hosts/osdu-delivery-mcp/src/docs.rs
  - osdu/hosts/osdu-delivery-mcp/src/instructions.rs
  - osdu/hosts/osdu-delivery-mcp/src/links.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/records.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/flows.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/documents.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/cache.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/assertions.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/dimensions.rs
  - osdu/hosts/osdu-delivery-mcp/src/tools/operate.rs
  - osdu/hosts/osdu-delivery-mcp/tests/server.rs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryAssistantTools.cs
  - osdu/deploy/docker/mcp.Dockerfile
  - osdu/deploy/k8s/mcp.yaml
  - osdu/deploy/compose/docker-compose.yml
  - sqlflow/tools/sqlflow-mcp/src/host.rs
  - sqlflow/tools/sqlflow-mcp/src/control_plane.rs
  - sqlflow/tools/sqlflow-mcp/src/config.rs
  - sqlflow/tools/sqlflow-mcp/src/http_server.rs
---

# The OSDU Delivery MCP server

`osdu-delivery-mcp` is the Model Context Protocol server an AI assistant (Claude, Cursor, VS Code, Codex, or the GUI's own
chat assistant) uses to answer questions about an OSDU Delivery estate. It is SQLFlow's MCP server
(`sqlflow/tools/sqlflow-mcp`, a library) composed with the delivery module: everything SQLFlow's server answers about
the catalog, runs, schedules, lineage, search and its documentation, plus 27 `delivery_*` tools over the ledger and the
module's definitions, minus the SQLFlow tools that read data. It calls the same control plane the GUI and `sqlflow` call,
with the caller's own token, so it can do what that person can do and no more.

## Metadata only

The server answers from metadata, never from the data itself:

| Returned | Never returned |
| --- | --- |
| The ledger's bookkeeping about a record: custody state, OSDU id and version, attempts with their outcome and error, the source file and row, who acted on it and when. | The record's content: what OSDU holds, what a mapping renders, the source row. |
| Definitions: flows, interfaces, mappings (their YAML), templates and OSDU schemas, cache flows and the types they declare, assertion tests, dimension definitions. | Cached values, and the values a cache change moved from and to. |
| Counts and health: records per state, submissions, cache versions and what each changed, test outcomes, dimension builds. | A dimension's values and keys, and the example records an assertion quotes (they are counted). |
| Configuration: partitions, and central configuration values, which are plain values or secret references. | Rows of any table, and the content of any file. |

A record is still identified: its OSDU id, source key, label and source file name are how it is found and traced.

Nine of SQLFlow's tools are withheld, so they are neither listed nor callable: `prepare_query`, `run_query`,
`detect_unique_key`, `check_duplicate_keys`, `compare_baseline`, `dataops_capabilities`, `analyze_warehouse_health`,
`discover_source` and `scaffold_ingestion_flow`. The list is `WITHHELD` in `osdu/hosts/osdu-delivery-mcp/src/lib.rs`;
the server's tests fail when SQLFlow adds a tool this product has not decided to offer or withhold. No tool removes
records from OSDU or reads what OSDU holds: removal is an operator's decision made in the GUI.

## The delivery tools

Every `delivery_*` tool calls a route under `/api/v1/delivery` and nothing else ([the delivery API](../concepts/api.md)).

| Question | Tool |
| --- | --- |
| Was this record delivered? Which records came from this file? | `delivery_find_records` (indexed prefix search; with no search, the newest records) |
| Why did it fail? Who deleted it? Which file and row is it from? | `delivery_record` (state, attempts, chain, activities) |
| How is this flow doing? Where does it deliver? What does a run need? | `delivery_flow` |
| Which records of this flow failed, are held, drifted or came from that run? | `delivery_flow_records` |
| What did the last run plan and deliver? Which batch is stuck? | `delivery_submissions` |
| What did the last retrieval bring back, and where is it? | `delivery_retrievals` |
| Who did what, and when? | `delivery_activities` |
| Which partitions are there, and what do we deliver to each? | `delivery_partitions` |
| Which OSDU does this flow really deliver to? | `delivery_config` |
| Is the cache fresh? Where does this type come from? What reads it? | `delivery_caches` |
| When did the cache last change? | `delivery_cache_versions` |
| What awaits approval, and what will it redeliver? | `delivery_cache_changes` |
| Which mapping does this flow use? Which flows does it affect? | `delivery_mappings` |
| Is this mapping valid? What does it leave required and empty? | `delivery_check_mapping` |
| Start a mapping for a template. | `delivery_scaffold_mapping` |
| Which properties does this kind require? | `delivery_templates` |
| Which versions of a kind does a release publish? | `delivery_osdu_schemas` |
| What breaks if we move to the next version of a kind? | `delivery_osdu_schema_compare` |
| Are the data quality tests passing? What failed last night? | `delivery_assertions`, `delivery_assertion_runs` |
| Which dimensions exist, and are they up to date? | `delivery_dimensions` |

Six tools act. Each describes itself as an "(operator action)": the marker says the tool changes state, and the
assistant is told to use one only when the person asked for that action. The control plane admits any signed-in user to
these routes (only admin routes check a scope), so in practice a token that can sign in can use them
([authentication and identity](../concepts/authentication-and-identity.md)). Each is recorded in the audit trail under the
caller's name.

| Action | Tool |
| --- | --- |
| Ask a flow's OSDU whether it still answers (answers in the request, writes nothing to OSDU, and is recorded in the audit trail as a `probe` activity). | `delivery_probe_target` |
| Queue a verify run that compares one record with what OSDU holds. | `delivery_verify_record` |
| Queue a sync run that reconciles the ledger with the ingestion tables; sends nothing. | `delivery_sync_with_source` |
| Release held records so the next run tries them again. | `delivery_release_records` |
| Send one record to OSDU again, writing a new version. | `delivery_redeliver_record` |
| Approve or reject pending cache changes. | `delivery_decide_cache_changes` |

An OSDU flow is run with SQLFlow's `trigger_run`, which carries the kind's `operation`, `values` and `payload`
([running an OSDU flow](../cli/run.md)). Results carry links into the GUI (a record's page, a submission's, an assertion
run's report, a partition's cache page, a mapping in the builder); set `SQLFLOW_GUI_URL` to make them absolute.

## Documentation and flow language

SQLFlow's documentation tools (`search_docs`, `get_doc`, `list_docs`, `related_docs`, `get_doc_by_cli_command`,
`get_doc_by_yaml_path`) answer from this product's documentation beside SQLFlow's: the reference pages under
`osdu/docs/reference` (ids starting with `delivery-`) and the decision records under `osdu/docs/decisions`. Their
frontmatter is compiled into `osdu/docs/reference/manifest.json`, which the build embeds with every page it lists, so a
page edit reaches the server once the manifest is regenerated and the server rebuilt. The instructions a client reads at
`initialize` introduce the product as an extension of SQLFlow, send a "how do I" question to
`delivery-guide-pattern-catalog` first, and say which tool answers which question, the vocabulary the tools share
(record, interface, partition, submission) and the operations `trigger_run` takes per kind. The flow-language tools
(`validate_flow`, `list_flow_keys`, `describe_flow_key`) know the module's flow kinds and documents from the key census in
`osdu/docs/census`.

## Installing it for a person

```bash
cargo build --release --manifest-path osdu/hosts/osdu-delivery-mcp/Cargo.toml
osdu/hosts/osdu-delivery-mcp/target/release/osdu-delivery-mcp install claude
# prints: claude mcp add osdu-delivery -- <path to osdu-delivery-mcp>
```

`install` also prints a registration for `cursor`, `vscode` and `codex`. Run by a client over stdio, the server reads
`SQLFLOW_CONTROL_PLANE_URL` (else the address it was last given with `set_control_plane_url`, else
`http://localhost:8080`). Sign in with the `login` tool: it returns an address and a code to approve in a browser, then
`check_auth_status` completes it, and the server mints and keeps a personal access token it rotates itself, carrying no
more than the approving person's scopes. `set_access_token` stores a token directly, and `SQLFLOW_CONTROL_PLANE_TOKEN`
supplies one from the environment. The address and token are kept in `~/.sqlflow/osdu-delivery-mcp-config.json` and
`~/.sqlflow/osdu-delivery-mcp-token.json`, apart from a `sqlflow-mcp` on the same machine.

## Running it as a service

`osdu-delivery-mcp http [--bind <addr>] [--allowed-hosts <h1,h2>]` serves streamable HTTP for remote clients and the
control plane's chat assistant. It holds no credential: every request to `/mcp` must carry `Authorization: Bearer <token>`
(otherwise 401 `missing or malformed Authorization header; expected: Bearer <SQLFlow access token>`), and the server
forwards that token to the control plane, which authorizes it as it does the GUI. `GET /healthz` answers without a token
for the platform's probe.

| Variable | Meaning |
| --- | --- |
| `SQLFLOW_CONTROL_PLANE_URL` | The control plane the tools call. |
| `SQLFLOW_GUI_URL` | The GUI's public address, for absolute links; unset, links are root-relative. |
| `SQLFLOW_MCP_HTTP_BIND` | The listen address; `127.0.0.1:8787` unless set (the image sets `0.0.0.0:8080`). `--bind` wins. |
| `SQLFLOW_MCP_HTTP_ALLOWED_HOSTS` | A `Host` header allowlist, comma separated; unset disables the check, since every request needs a bearer token. |
| `SQLFLOW_MCP_LOG` | The log filter (default `info`); logs go to stderr. |

```bash
docker build -f osdu/deploy/docker/mcp.Dockerfile -t osdu-delivery-mcp:latest .
docker run -p 8787:8080 -e SQLFLOW_CONTROL_PLANE_URL=http://controlplane:8080 osdu-delivery-mcp:latest
```

The compose stack has it behind the `mcp` profile, and `osdu/deploy/k8s/mcp.yaml` adds it as `/mcp` on the estate's host.
The Bicep templates do not deploy it ([deployment](deployment.md)).

## The GUI chat assistant

The control plane's chat assistant ([SQLFlow's chat assistant guide](../../../../sqlflow/docs/reference/guides/chat-assistant.md))
uses this server when `ControlPlane:Assistant:Mcp:ServerUrl` points at its `/mcp`, and forwards the signed-in user's own
token. OSDU Delivery's control plane gives it SQLFlow's metadata tools and the 21 `delivery_*` read tools; the six action
tools and `trigger_run` are left off, so a chat answer never starts work. A deployment that sets
`ControlPlane:Assistant:Mcp:AllowedTools` keeps its own list.

## Where it lives

| Path | What |
| --- | --- |
| `osdu/hosts/osdu-delivery-mcp` | The host crate: the tools, the instructions a client reads at `initialize`, the documentation index, the GUI link rules, `WITHHELD`, and the program. |
| `sqlflow/tools/sqlflow-mcp` | SQLFlow's server, as a library a host composes. |
| `osdu/deploy/docker/mcp.Dockerfile` | The image, built from the repository root. |

```bash
cargo test --manifest-path osdu/hosts/osdu-delivery-mcp/Cargo.toml
```

The tests run against a control plane of their own, so they need no database, no OSDU and no network.

## See also

- [SQLFlow: the GUI chat assistant](../../../../sqlflow/docs/reference/guides/chat-assistant.md)
- [SQLFlow: the Slack assistant](../../../../sqlflow/docs/reference/guides/slack-assistant.md)
- [The delivery API](../concepts/api.md)
- [Authentication and identity](../concepts/authentication-and-identity.md)
