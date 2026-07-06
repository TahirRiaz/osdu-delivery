# SQLFlow developer tooling

Language intelligence, editor integration, and AI tooling for SQLFlow
`.flow.yaml` projects. Three components share one Cargo workspace plus a
TypeScript VSCode extension; all of them are driven by the machine-readable
reference corpus under [`../docs/reference`](../docs/reference).

| Component | Language | What it is |
| --- | --- | --- |
| [`sqlflow-lang`](sqlflow-lang) | Rust (lib) | Protocol-free analysis engine: parses `.flow.yaml`, resolves cursor→path against the key census, and produces completion / hover / diagnostics / document symbols / code actions. |
| [`sqlflow-lsp`](sqlflow-lsp) | Rust (bin) | Language Server Protocol server over stdio, a thin shell over `sqlflow-lang`. |
| [`sqlflow-mcp`](sqlflow-mcp) | Rust (bin) | Model Context Protocol server: the embedded reference corpus (doc tools) plus a live proxy over the control plane's `/api/v1` surface (catalog, lineage, runs, schedules, search) with device-flow auth. |
| [`sqlflow-vscode`](sqlflow-vscode) | TypeScript | VSCode extension: registers the `.flow.yaml` language, launches the LSP, browses the catalog / runs / schedules, triggers runs, searches the bundled docs, and generates MCP registration snippets. |

The design mirrors DeltaForge's protocol/analysis split, but SQLFlow authors
**YAML flow documents** (not SQL), so the analysis engine is driven by the
per-`flowType` `keys*.json` census files rather than a SQL parser.

## Build

```sh
# Rust binaries (release)
cargo build --release -p sqlflow-lsp -p sqlflow-mcp

# VSCode extension: typecheck, then bundle (also stages the binaries + docs)
cd sqlflow-vscode
npm install
npm run bundle          # or: npm run package   (produces a .vsix)
```

`esbuild.mjs` copies `../docs/reference` into `dist/reference` (so the docs
browser works offline) and stages the built `sqlflow-lsp` / `sqlflow-mcp`
binaries into `bin/`. The extension also discovers binaries from
`../target/{release,debug}` when run from a checkout, or from the
`sqlflow.lspBinaryPath` / `sqlflow.mcpBinaryPath` settings.

## Test

```sh
cargo test --workspace
```

## MCP server

Register the built binary with an AI assistant (or use the extension's
**SQLFlow: Set Up MCP Server** command):

```sh
claude mcp add sqlflow --env SQLFLOW_CONTROL_PLANE_URL=http://localhost:8080 -- /path/to/sqlflow-mcp
```

Then, from the assistant, run the `login` tool (device flow) or
`set_access_token`. The device flow is served by the control plane at
`POST /api/v1/auth/device` and approved in a browser at `/device`.

To answer schema questions, browse with `list_schemas` (every server/database/
schema and its object count) and `lineage_objects` (filter by database, schema,
kind, or name). Then, for a specific object, `describe_object` returns its
identity, columns, generating script, module body, and lineage edges in one
payload.

Environment: `SQLFLOW_CONTROL_PLANE_URL` (default `http://localhost:8080`),
`SQLFLOW_CONTROL_PLANE_TOKEN` (optional token override), `SQLFLOW_MCP_LOG`
(log filter). Config and token are persisted under `~/.sqlflow/`.
