//! SQLFlow MCP server entry point.
//!
//! Serves the Model Context Protocol over stdio. Logging goes to stderr so it
//! never corrupts the protocol channel on stdout. A small `install` subcommand
//! prints ready-to-paste registration for common MCP clients.

mod config;
mod control_plane;
mod docs;
mod server;

use std::sync::Arc;

use control_plane::ControlPlane;
use docs::DocsIndex;
use rmcp::transport::stdio;
use rmcp::ServiceExt;
use server::SqlFlowMcp;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let args: Vec<String> = std::env::args().collect();
    match args.get(1).map(String::as_str) {
        Some("--version") | Some("-V") => {
            println!("sqlflow-mcp {}", env!("CARGO_PKG_VERSION"));
            return Ok(());
        }
        Some("install") => {
            print_install(args.get(2).map(String::as_str));
            return Ok(());
        }
        _ => {}
    }

    tracing_subscriber::fmt()
        .with_writer(std::io::stderr)
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_env("SQLFLOW_MCP_LOG")
                .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info")),
        )
        .init();

    let docs = Arc::new(DocsIndex::load());
    tracing::info!("loaded {} reference pages for {}", docs.len(), docs.product());
    let cp = Arc::new(ControlPlane::from_env());
    tracing::info!("control plane: {}", cp.base_url());

    let service = SqlFlowMcp::new(docs, cp).serve(stdio()).await?;
    service.waiting().await?;
    Ok(())
}

fn print_install(client: Option<&str>) {
    let exe = std::env::current_exe()
        .map(|p| p.display().to_string())
        .unwrap_or_else(|_| "sqlflow-mcp".to_string());
    match client {
        Some("claude") => {
            println!("claude mcp add sqlflow -- {exe}");
        }
        Some("codex") => {
            println!("[mcp_servers.sqlflow]\ncommand = \"{exe}\"");
        }
        Some("cursor") | Some("vscode") | None => {
            println!(
                "{{\n  \"mcpServers\": {{\n    \"sqlflow\": {{\n      \"command\": \"{}\",\n      \"env\": {{ \"SQLFLOW_CONTROL_PLANE_URL\": \"http://localhost:8080\" }}\n    }}\n  }}\n}}",
                exe.replace('\\', "\\\\")
            );
        }
        Some(other) => {
            eprintln!("Unknown client '{other}'. Try: claude, cursor, codex, vscode.");
        }
    }
}
