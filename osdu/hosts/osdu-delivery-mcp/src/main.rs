//! The OSDU Delivery MCP server's entry point.
//!
//! SQLFlow's whole MCP server (its reference corpus, the flow-language tools, and the proxy over the control plane's
//! catalog, lineage, runs, schedules and search) composed with the delivery module, which adds the ledger's tools,
//! the OSDU documentation and the key census of the OSDU flow kinds. Nothing about the server is forked: this
//! program is the composition, and the name it runs under.
//!
//! It takes the server's own arguments: nothing for stdio, `http [--bind <addr>] [--allowed-hosts <h1,h2>]` for
//! streamable HTTP, `install [client]` for a registration snippet, `--version`.

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    sqlflow_mcp::run(osdu_delivery_mcp::host()).await
}
