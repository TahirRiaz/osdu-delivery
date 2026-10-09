//! The OSDU Delivery MCP server: SQLFlow's MCP server composed with the delivery module.
//!
//! SQLFlow's server is a library a host composes (`sqlflow/tools/sqlflow-mcp`), as its control plane and its CLI are.
//! This crate is that host for OSDU Delivery. [`module`] is what the delivery module adds: the `delivery_*` tools over
//! the module's endpoints, its section of the instructions, the product's documentation, the GUI routes of its rows,
//! and the key census of the flow kinds and documents it adds. [`host`] is the program: its name, where it keeps its
//! sign-in, and the tools of SQLFlow's it does not offer.
//!
//! The server answers from metadata. A delivery tool returns the ledger's bookkeeping (states, ids, counts, history,
//! errors) and the module's definitions (flows, mappings, templates, what a cache is made of), never record content,
//! a source row, a cached value or a dimension's values; and the tools of SQLFlow's that read rows are left out
//! ([`WITHHELD`]).

pub mod docs;
pub mod instructions;
pub mod links;
mod support;
pub mod tools;

use sqlflow_mcp::{McpHost, McpModule};

use crate::tools::DeliveryTools;

/// The program's name: what `--version` prints, what the server reports itself as, and the label of the access token
/// it mints, as the control plane's token list shows it.
pub const SERVER_NAME: &str = "osdu-delivery-mcp";

/// The name `install` registers the server under in a client (`claude mcp add osdu-delivery ...`).
pub const REGISTRATION: &str = "osdu-delivery";

/// The module's name, as the control plane's module is named.
pub const MODULE_NAME: &str = "delivery";

/// The tools of SQLFlow's own server this product does not offer: every one that reaches a datasource or reads a
/// source's content. OSDU Delivery's assistant answers from metadata; the rows of an ingestion table, a sample file
/// or a warehouse are not its to read.
pub const WITHHELD: &[&str] = &[
    // Run a query against a datasource and return its rows.
    "prepare_query",
    "run_query",
    // Profile a table's rows on a node: its keys, its duplicates, its difference from another estate's table.
    "detect_unique_key",
    "check_duplicate_keys",
    "compare_baseline",
    // Describes the surface the four above belong to.
    "dataops_capabilities",
    // Runs probes against a datasource's management views.
    "analyze_warehouse_health",
    // Read a sample file, or introspect and sample a source table, to generate a flow from it.
    "discover_source",
    "scaffold_ingestion_flow",
];

/// The delivery module: its tools, its instructions, the product's documentation, its GUI links and its key census.
pub fn module() -> McpModule {
    let mut module = McpModule::new(MODULE_NAME)
        .tools(|ctx| (DeliveryTools::new(ctx), DeliveryTools::router()))
        .instructions(instructions::INSTRUCTIONS)
        .docs(docs::pages())
        .link_rule(links::delivery_links);
    for census in docs::CENSUS {
        module = module.census(*census);
    }
    module
}

/// The OSDU Delivery MCP server: SQLFlow's, with the delivery module, under the product's own name and with a
/// sign-in of its own, so it can be installed beside `sqlflow-mcp` and pointed at another control plane.
pub fn host() -> McpHost {
    McpHost::new(SERVER_NAME, env!("CARGO_PKG_VERSION"))
        .registered_as(REGISTRATION)
        .state_prefix(SERVER_NAME)
        .introduced_as(instructions::INTRODUCTION)
        .without_tools(WITHHELD.iter().copied())
        .with_module(module())
}
