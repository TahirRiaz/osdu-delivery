//! The SQLFlow MCP server: tool definitions and dispatch.
//!
//! Two capability tiers, mirroring DeltaForge's design:
//!   * **Offline** — the embedded reference corpus (doc tools) and the
//!     `.flow.yaml` analysis engine (`validate_flow`, key lookup). No network.
//!   * **Online** — a proxy over the control plane's `/api/v1` read surface
//!     (catalog, lineage, runs, schedules, search, summary) plus the operate
//!     surface (trigger/cancel), gated by device-flow authentication.

use std::sync::Arc;

use rmcp::handler::server::router::tool::ToolRouter;
use rmcp::handler::server::wrapper::Parameters;
use rmcp::model::{ServerCapabilities, ServerInfo};
use rmcp::{schemars, tool, tool_handler, tool_router, ServerHandler};
use schemars::JsonSchema;
use serde::Deserialize;
use serde_json::{json, Value};

use crate::control_plane::{ControlPlane, PollOutcome};
use crate::docs::DocsIndex;
use sqlflow_lang::census::Census;

#[derive(Clone)]
pub struct SqlFlowMcp {
    docs: Arc<DocsIndex>,
    cp: Arc<ControlPlane>,
    /// True when serving over HTTP: every request carries the caller's own bearer
    /// (scoped around dispatch in `call_tool`), so the server-side sign-in tools are
    /// inert, the control-plane URL is operator-fixed, and tools that read files on
    /// the server host are disabled.
    http_mode: bool,
    tool_router: ToolRouter<Self>,
}

impl SqlFlowMcp {
    /// A stdio-mode server: credentials are managed locally (device flow / pasted
    /// token) and host-local tools are available.
    pub fn new(docs: Arc<DocsIndex>, cp: Arc<ControlPlane>) -> Self {
        Self::with_mode(docs, cp, false)
    }

    /// An HTTP-mode server instance, created per session by the HTTP transport.
    pub fn new_http(docs: Arc<DocsIndex>, cp: Arc<ControlPlane>) -> Self {
        Self::with_mode(docs, cp, true)
    }

    fn with_mode(docs: Arc<DocsIndex>, cp: Arc<ControlPlane>, http_mode: bool) -> Self {
        SqlFlowMcp {
            docs,
            cp,
            http_mode,
            tool_router: Self::tool_router(),
        }
    }
}

/// Replies for tools that do not apply when serving over HTTP.
const HTTP_MODE_AUTH_NOTE: &str = "Not applicable over HTTP: this server authenticates every \
request with the Authorization bearer supplied by the connecting client, so there is no \
server-side sign-in to start, poll, store, or clear.";
const HTTP_MODE_URL_NOTE: &str = "Not applicable over HTTP: the control-plane URL is fixed by \
the server operator (SQLFLOW_CONTROL_PLANE_URL) and cannot be changed by a client.";
const HTTP_MODE_DISCOVER_NOTE: &str = "discover_source is disabled over HTTP because it reads \
sample files on the server host, not on yours. Run sqlflow-mcp locally over stdio to use it.";

// --- Parameter structs -----------------------------------------------------

#[derive(Debug, Deserialize, JsonSchema)]
pub struct SearchDocsInput {
    /// Keywords to match across doc titles, keywords, and summaries. Prime the
    /// query with SQLFlow terms: "job/trigger" → schedule; "folder" → repo;
    /// "upsert" → ingestion load; "wave" → lineage execution plan.
    pub query: String,
    /// Optional type filter: cli-command, flow-reference, source-type, concept, guide.
    #[serde(rename = "type")]
    pub doc_type: Option<String>,
    /// Maximum results (default 15, max 50).
    pub limit: Option<usize>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct IdInput {
    /// The doc id from the manifest (e.g. "cli-run", "flow-source", "concept-lineage").
    pub id: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct YamlPathInput {
    /// A `.flow.yaml` dot-path, e.g. "source.options.header" or "load.mode".
    #[serde(rename = "yamlPath")]
    pub yaml_path: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct CliCommandInput {
    /// A top-level CLI command, e.g. "run", "validate", "lineage", "catalog".
    pub command: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ListDocsInput {
    /// Optional type filter: cli-command, flow-reference, source-type, concept, guide.
    #[serde(rename = "type")]
    pub doc_type: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ValidateFlowInput {
    /// The full `.flow.yaml` document text to validate.
    pub yaml: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct FlowTypeInput {
    /// The flow kind: omit for the file flow, or one of ing, exp, sp, inv, hc, scm, batch.
    #[serde(rename = "flowType")]
    pub flow_type: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct DescribeKeyInput {
    /// The census path, e.g. "source.type" or "load.mode".
    pub path: String,
    /// The flow kind selecting which census applies (see list_flow_keys).
    #[serde(rename = "flowType")]
    pub flow_type: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct UrlInput {
    /// The control-plane base URL, e.g. "https://sqlflow.example.com".
    pub url: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct TokenInput {
    /// A bearer access token issued by the control plane.
    pub token: String,
    /// Space-delimited scopes the token carries (default "read operate").
    pub scope: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct CheckAuthInput {
    /// The device code from `login`; omit to use the in-flight login.
    #[serde(rename = "deviceCode")]
    pub device_code: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct GuidInput {
    /// A catalog entity id (GUID).
    pub id: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct RepoIdInput {
    /// A repository id (GUID).
    #[serde(rename = "repoId")]
    pub repo_id: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct RunIdInput {
    /// A run id (GUID).
    #[serde(rename = "runId")]
    pub run_id: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ListPipelinesInput {
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// Flow kind filter (e.g. ing, exp, sp).
    pub kind: Option<String>,
    /// Filter by active state.
    pub active: Option<bool>,
    /// Substring name filter.
    pub name: Option<String>,
    /// Batch (source-system grouping) filter; "default" also matches flows that declare no batch.
    pub batch: Option<String>,
    pub page: Option<i64>,
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ListBatchesInput {
    /// Repository id (GUID) filter (optional).
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// Filter by active state (optional).
    pub active: Option<bool>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct DependenciesInput {
    /// A repository id (GUID).
    #[serde(rename = "repoId")]
    pub repo_id: String,
    /// Keep only the dependency edges touching this flow, in either direction (optional).
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct CatalogTreeInput {
    /// Server reference: give it to browse below one server.
    #[serde(rename = "serverRef")]
    pub server_ref: Option<String>,
    /// Database: give it (with serverRef) to browse below one database.
    pub database: Option<String>,
    /// Schema: give it to get the schema's object-kind groups with counts.
    pub schema: Option<String>,
    /// Object kind (Table, View, Procedure, Function, Trigger, Synonym, File): give it to list that
    /// kind's objects in the schema, paged.
    pub kind: Option<String>,
    pub page: Option<i64>,
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct PipelineColumnsInput {
    /// The pipeline id (GUID).
    pub id: String,
    /// Column set: "declared" or "detected".
    pub kind: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ListRunsInput {
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
    /// Run status filter (queued, running, succeeded, failed, cancelled).
    pub status: Option<String>,
    #[serde(rename = "flowName")]
    pub flow_name: Option<String>,
    /// Grouping-label (source system) filter.
    pub batch: Option<String>,
    /// Only the latest run per pipeline.
    pub latest: Option<bool>,
    pub page: Option<i64>,
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct LineageObjectsInput {
    /// Substring name filter.
    pub name: Option<String>,
    /// Server reference filter.
    #[serde(rename = "serverRef")]
    pub server_ref: Option<String>,
    /// Database filter (exact).
    pub database: Option<String>,
    /// Schema filter (exact, e.g. "dbo").
    pub schema: Option<String>,
    /// Object kind filter (table, view, proc, function, file).
    pub kind: Option<String>,
    pub page: Option<i64>,
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ListSchemasInput {
    /// Server reference filter (optional).
    #[serde(rename = "serverRef")]
    pub server_ref: Option<String>,
    /// Database filter (optional).
    pub database: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct KeyInput {
    /// The catalog object key (serverRef+db+schema+name composite).
    pub key: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct SearchInput {
    /// The search term.
    pub query: String,
    pub page: Option<i64>,
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct InsightsWindowInput {
    /// The analysis window in days (1-90; default 7).
    pub days: Option<i64>,
    /// Restrict to one repository (GUID, optional).
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// Restrict to one batch (source-system grouping, optional).
    pub batch: Option<String>,
    /// Most advisories to return (default 20; the counts in the answer cover everything found).
    pub limit: Option<i64>,
    /// Set true to include ready-to-review SQL suggestions inline. Default false: items report
    /// hasSuggestedSql instead, keeping the briefing small; re-ask with includeSql for the ones you act on.
    #[serde(rename = "includeSql")]
    pub include_sql: Option<bool>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct InsightsFlowsInput {
    /// The analysis window in days (1-90; default 7).
    pub days: Option<i64>,
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    pub batch: Option<String>,
    /// Most flows to return, ordered by total processing time (default 100, max 500).
    pub limit: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct InsightsStepsInput {
    /// The pipeline id (GUID) to drill into.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: String,
    /// The analysis window in days (1-90; default 30).
    pub days: Option<i64>,
    /// Set true to include one sample SQL statement per step (can be large). Default false: timings only.
    #[serde(rename = "includeSql")]
    pub include_sql: Option<bool>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct WarehouseHealthInput {
    /// Which DMV probe to run: missingIndexes, statisticsHealth, indexUsage, or topQueries.
    pub operation: String,
    /// The datasource connection reference (a whole ${env:...} / ${keyvault:...} token the estate's pipelines
    /// declare, or an @alias). Omit to probe the estate's busiest target datasource (the warehouse).
    pub reference: Option<String>,
    /// The database to scope the probe to; omit for the connection's default database.
    pub database: Option<String>,
    /// Most rows to return (default 20, max 1000). The full row set persists on the compute task either way;
    /// the GUI's warehouse panel shows it, so ask for more rows only when you will read them.
    pub limit: Option<i64>,
    /// Route the probe to a worker pool that can reach the source (optional).
    pub pool: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct TriggerRunInput {
    /// The repository id (GUID) that owns the flow.
    #[serde(rename = "repoId")]
    pub repo_id: String,
    /// The flow name to run.
    #[serde(rename = "flowName")]
    pub flow_name: String,
    /// Target worker pool (optional).
    pub pool: Option<String>,
    /// Pin to a specific commit sha (optional; defaults to last synced).
    #[serde(rename = "commitSha")]
    pub commit_sha: Option<String>,
    /// Full (re)load instead of incremental.
    #[serde(rename = "fullLoad")]
    pub full_load: Option<bool>,
    /// Backfill window start (ISO-8601).
    #[serde(rename = "backfillFrom")]
    pub backfill_from: Option<String>,
    /// Backfill window end (ISO-8601).
    #[serde(rename = "backfillTo")]
    pub backfill_to: Option<String>,
    /// File-pattern override for file flows.
    #[serde(rename = "filePattern")]
    pub file_pattern: Option<String>,
}

// --- Helpers ---------------------------------------------------------------

fn done(result: anyhow::Result<String>) -> String {
    match result {
        Ok(s) => s,
        Err(e) => format!("Error: {e:#}"),
    }
}

fn json_str(v: &Value) -> String {
    serde_json::to_string_pretty(v).unwrap_or_else(|_| v.to_string())
}

/// Caps every string in a JSON document to `max` characters (marking the cut), recursively. The warehouse
/// probes carry statement bodies that can run to kilobytes each; the model reading the tool result needs the
/// shape and the head of the text, and the untruncated document stays on the compute task for the GUI.
fn truncate_long_strings(value: &mut Value, max: usize) {
    match value {
        Value::String(s) => {
            if s.chars().count() > max {
                let head: String = s.chars().take(max).collect();
                *s = format!("{head}... [truncated]");
            }
        }
        Value::Array(items) => {
            for item in items {
                truncate_long_strings(item, max);
            }
        }
        Value::Object(map) => {
            for (_, item) in map.iter_mut() {
                truncate_long_strings(item, max);
            }
        }
        _ => {}
    }
}

/// The `sqlflow` CLI executable the discover tool shells out to: `SQLFLOW_CLI` when
/// set (a full path), otherwise `sqlflow` resolved from `PATH`.
fn sqlflow_cli_command() -> String {
    match std::env::var("SQLFLOW_CLI") {
        Ok(path) if !path.trim().is_empty() => path,
        _ => "sqlflow".to_string(),
    }
}

/// Runs the `sqlflow` CLI with the given arguments and returns its stdout on success.
/// The child is spawned on a blocking thread (the workspace tokio build omits the
/// `process` feature) so it never stalls the async stdio transport. A non-zero exit
/// surfaces the CLI's own stderr; a missing binary surfaces an actionable message.
async fn run_sqlflow_cli(args: Vec<String>) -> Result<String, String> {
    let command = sqlflow_cli_command();
    let display = format!("{} {}", command, args.join(" "));

    let result = tokio::task::spawn_blocking(move || {
        std::process::Command::new(&command)
            .args(&args)
            .output()
            .map_err(|e| (command, e))
    })
    .await
    .map_err(|e| format!("Failed to run the SQLFlow CLI: {e}"))?;

    match result {
        Ok(output) if output.status.success() => Ok(String::from_utf8_lossy(&output.stdout).into_owned()),
        Ok(output) => {
            let stderr = String::from_utf8_lossy(&output.stderr);
            let stdout = String::from_utf8_lossy(&output.stdout);
            let detail = if stderr.trim().is_empty() { stdout.trim() } else { stderr.trim() };
            Err(format!(
                "The SQLFlow CLI exited with {} for `{display}`:\n{detail}",
                output.status
            ))
        }
        Err((command, e)) if e.kind() == std::io::ErrorKind::NotFound => Err(format!(
            "The SQLFlow CLI ('{command}') was not found. Install it and put it on PATH, or set the \
SQLFLOW_CLI environment variable to the full path of the `sqlflow` executable."
        )),
        Err((_, e)) => Err(format!("Failed to launch the SQLFlow CLI: {e}")),
    }
}

// --- Tools -----------------------------------------------------------------

#[tool_router]
impl SqlFlowMcp {
    // ---- Documentation (offline) -----------------------------------------

    #[tool(
        description = "Search the embedded SQLFlow reference corpus (CLI commands, flow YAML reference, source types, concepts, guides). Returns ranked {id, path, title, summary}."
    )]
    async fn search_docs(&self, Parameters(input): Parameters<SearchDocsInput>) -> String {
        let limit = input.limit.unwrap_or(15).clamp(1, 50);
        let hits = self.docs.search(&input.query, input.doc_type.as_deref(), limit);
        if hits.is_empty() {
            return format!("No docs matched '{}'.", input.query);
        }
        let list: Vec<Value> = hits
            .iter()
            .map(|h| {
                json!({
                    "id": h.meta.id,
                    "type": h.meta.doc_type,
                    "title": h.meta.title,
                    "summary": h.meta.summary,
                    "yamlPath": h.meta.yaml_path,
                    "cliCommand": h.meta.cli_command,
                    "score": h.score,
                })
            })
            .collect();
        json_str(&json!({ "query": input.query, "results": list }))
    }

    #[tool(description = "Fetch the full markdown body of a reference page by its manifest id.")]
    async fn get_doc(&self, Parameters(input): Parameters<IdInput>) -> String {
        match (self.docs.get_meta(&input.id), self.docs.body(&input.id)) {
            (Some(meta), Some(body)) => {
                format!("# {}\n\n_id: {} · type: {} · path: {}_\n\n{}", meta.title, meta.id, meta.doc_type, meta.path, body)
            }
            _ => format!("No doc with id '{}'. Use search_docs or list_docs to find one.", input.id),
        }
    }

    #[tool(
        description = "Jump from a `.flow.yaml` dot-path to the reference page that documents it (exact, then longest documented prefix)."
    )]
    async fn get_doc_by_yaml_path(&self, Parameters(input): Parameters<YamlPathInput>) -> String {
        match self.docs.by_yaml_path(&input.yaml_path) {
            Some(meta) => match self.docs.body(&meta.id) {
                Some(body) => format!("# {} (yamlPath: {})\n\n{}", meta.title, meta.yaml_path.clone().unwrap_or_default(), body),
                None => json_str(&json!({ "id": meta.id, "title": meta.title })),
            },
            None => format!("No reference page documents the YAML path '{}'.", input.yaml_path),
        }
    }

    #[tool(description = "Look up the reference page for a top-level CLI command.")]
    async fn get_doc_by_cli_command(&self, Parameters(input): Parameters<CliCommandInput>) -> String {
        match self.docs.by_cli_command(&input.command) {
            Some(meta) => match self.docs.body(&meta.id) {
                Some(body) => format!("# {}\n\n{}", meta.title, body),
                None => json_str(&json!({ "id": meta.id, "title": meta.title })),
            },
            None => format!("No CLI command page for '{}'.", input.command),
        }
    }

    #[tool(description = "List the 'see also' pages related to a given reference page id.")]
    async fn related_docs(&self, Parameters(input): Parameters<IdInput>) -> String {
        let related = self.docs.related(&input.id);
        if related.is_empty() {
            return format!("No related docs recorded for '{}'.", input.id);
        }
        let list: Vec<Value> = related
            .iter()
            .map(|m| json!({ "id": m.id, "type": m.doc_type, "title": m.title, "summary": m.summary }))
            .collect();
        json_str(&json!({ "id": input.id, "related": list }))
    }

    #[tool(description = "List every reference page (optionally filtered by type) as {id, type, title}.")]
    async fn list_docs(&self, Parameters(input): Parameters<ListDocsInput>) -> String {
        let list: Vec<Value> = self
            .docs
            .all()
            .iter()
            .filter(|m| input.doc_type.as_deref().is_none_or(|t| m.doc_type.eq_ignore_ascii_case(t)))
            .map(|m| json!({ "id": m.id, "type": m.doc_type, "title": m.title }))
            .collect();
        json_str(&json!({ "count": list.len(), "docs": list }))
    }

    // ---- Flow language (offline) -----------------------------------------

    #[tool(
        description = "Validate a `.flow.yaml` document against SQLFlow's key census: reports parse errors, unknown keys, invalid enum values, and missing required keys with line numbers."
    )]
    async fn validate_flow(&self, Parameters(input): Parameters<ValidateFlowInput>) -> String {
        let diags = sqlflow_lang::analyze(&input.yaml);
        if diags.is_empty() {
            return "OK: no problems found.".to_string();
        }
        let list: Vec<Value> = diags
            .iter()
            .map(|d| {
                let sev = match d.severity {
                    sqlflow_lang::features::Severity::Error => "error",
                    sqlflow_lang::features::Severity::Warning => "warning",
                    sqlflow_lang::features::Severity::Information => "info",
                    sqlflow_lang::features::Severity::Hint => "hint",
                };
                json!({
                    "severity": sev,
                    "line": d.range.start.line + 1,
                    "column": d.range.start.character + 1,
                    "code": d.code,
                    "message": d.message,
                })
            })
            .collect();
        json_str(&json!({ "problems": list }))
    }

    #[tool(description = "List every documented attribute for a flow kind: {path, type, required}.")]
    async fn list_flow_keys(&self, Parameters(input): Parameters<FlowTypeInput>) -> String {
        let census = Census::for_flow_type(input.flow_type.as_deref());
        let list: Vec<Value> = census
            .entries
            .iter()
            .map(|e| json!({ "path": e.path, "type": e.ty, "required": e.required }))
            .collect();
        json_str(&json!({ "flowType": input.flow_type, "count": list.len(), "keys": list }))
    }

    #[tool(description = "Describe one `.flow.yaml` attribute: type, required, default, allowed values, and the full description with validation notes.")]
    async fn describe_flow_key(&self, Parameters(input): Parameters<DescribeKeyInput>) -> String {
        let census = Census::for_flow_type(input.flow_type.as_deref());
        let target = sqlflow_lang::census::parse_path(&input.path);
        let entry = census.entries.iter().find(|e| e.segs == target || e.path == input.path);
        match entry {
            Some(e) => json_str(&json!({
                "path": e.path,
                "type": e.ty,
                "required": e.required,
                "default": e.default,
                "enumValues": e.enum_values,
                "appliesWhen": e.applies_when,
                "definedIn": e.defined_in,
                "validation": e.validation,
                "description": e.description,
            })),
            None => format!("No attribute '{}' for this flow kind. Use list_flow_keys to browse.", input.path),
        }
    }

    // ---- Control-plane setup & auth --------------------------------------

    #[tool(description = "Report the configured control-plane URL and whether the server is authenticated.")]
    async fn get_control_plane_url(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        if self.http_mode {
            return json_str(&json!({
                "url": self.cp.base_url(),
                "authMode": "per-request: each call runs as the bearer on the inbound Authorization header",
            }));
        }
        json_str(&json!({
            "url": self.cp.base_url(),
            "authenticated": self.cp.is_authenticated(),
        }))
    }

    #[tool(description = "Set (and persist) the control-plane base URL.")]
    async fn set_control_plane_url(&self, Parameters(input): Parameters<UrlInput>) -> String {
        if self.http_mode {
            return HTTP_MODE_URL_NOTE.to_string();
        }
        self.cp.set_base_url(&input.url);
        format!("Control-plane URL set to {}", self.cp.base_url())
    }

    #[tool(description = "Check connectivity to the control plane (its readiness probe).")]
    async fn check_connectivity(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        done(self.cp.check_connectivity().await.map(|s| format!("Reachable: {s}")))
    }

    #[tool(
        description = "Begin device-flow sign-in. Returns a URL and user code to approve in a browser; then call check_auth_status to complete."
    )]
    async fn login(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        if self.http_mode {
            return HTTP_MODE_AUTH_NOTE.to_string();
        }
        match self.cp.start_device_auth().await {
            Ok(auth) => {
                let complete = auth
                    .verification_uri_complete
                    .clone()
                    .unwrap_or_else(|| auth.verification_uri.clone());
                format!(
                    "To sign in, open:\n  {}\nand enter the code:\n  {}\n\n(Direct link: {})\nThe code expires in {}s. After approving, call check_auth_status (poll about every {}s).",
                    auth.verification_uri, auth.user_code, complete, auth.expires_in, auth.interval
                )
            }
            Err(e) => format!("Error starting sign-in: {e:#}. Alternatively, paste a token with set_access_token."),
        }
    }

    #[tool(description = "Poll the pending device-flow sign-in (or report current auth state).")]
    async fn check_auth_status(&self, Parameters(input): Parameters<CheckAuthInput>) -> String {
        if self.http_mode {
            return HTTP_MODE_AUTH_NOTE.to_string();
        }
        let code = input.device_code.or_else(|| self.cp.pending_device_code());
        let Some(code) = code else {
            return if self.cp.is_authenticated() {
                "Authenticated.".to_string()
            } else {
                "Not authenticated and no sign-in in progress. Call login first.".to_string()
            };
        };
        match self.cp.poll_device_token(&code).await {
            Ok(PollOutcome::Approved(t)) => {
                // The device grant hands back a short-lived session token. Exchange it for a long-lived, self-
                // rotating personal access token so the user does not have to sign in again; if the control plane
                // cannot mint one, the device token stands and sign-in still succeeds.
                let scope = if t.scope.is_empty() { "read operate".to_string() } else { t.scope.clone() };
                match self.cp.provision_managed_token(&scope).await {
                    Ok(()) => format!(
                        "Signed in. A long-lived access token (scopes: {scope}) was provisioned and will refresh automatically; you will not need to sign in again while this client stays in use."
                    ),
                    Err(_) => format!(
                        "Signed in. Scopes: {}. (Could not provision a long-lived token; this session token expires and will need a fresh sign-in.)",
                        if scope.is_empty() { "(none reported)".into() } else { scope }
                    ),
                }
            }
            Ok(PollOutcome::Pending) => "Still waiting for approval. Approve in the browser, then check again.".to_string(),
            Ok(PollOutcome::SlowDown) => "Polling too fast; wait a few seconds and check again.".to_string(),
            Ok(PollOutcome::Denied) => "Sign-in was denied.".to_string(),
            Ok(PollOutcome::Expired) => "The sign-in code expired. Call login again.".to_string(),
            Err(e) => format!("Error: {e:#}"),
        }
    }

    #[tool(description = "Store a bearer access token directly (alternative to device-flow login).")]
    async fn set_access_token(&self, Parameters(input): Parameters<TokenInput>) -> String {
        if self.http_mode {
            return HTTP_MODE_AUTH_NOTE.to_string();
        }
        let token = input.token.trim().to_string();
        let is_pat = token.starts_with("sqlf_");
        let scope = input.scope.unwrap_or_else(|| "read operate".to_string());
        self.cp.set_token(crate::config::TokenCache {
            access_token: token,
            scope: scope.clone(),
            expires_at: None,
            token_id: None,
            // A pasted personal access token is already long-lived and owned by the user; we do not manage its
            // lifecycle. A pasted session token is short-lived, so we exchange it below for one we do manage.
            renewable: false,
        });
        if is_pat {
            "Access token stored.".to_string()
        } else {
            match self.cp.provision_managed_token(&scope).await {
                Ok(()) => "Access token stored and exchanged for a long-lived, self-refreshing token.".to_string(),
                Err(_) => "Access token stored.".to_string(),
            }
        }
    }

    #[tool(description = "Forget the stored access token.")]
    async fn logout(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        if self.http_mode {
            return HTTP_MODE_AUTH_NOTE.to_string();
        }
        self.cp.clear_token();
        "Signed out.".to_string()
    }

    // ---- Catalog (read) --------------------------------------------------

    #[tool(description = "List the synced repositories in the catalog.")]
    async fn list_repos(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get("/api/v1/repos", &[]).await
    }

    #[tool(description = "Get one repository by id.")]
    async fn get_repo(&self, Parameters(input): Parameters<GuidInput>) -> String {
        self.get(&format!("/api/v1/repos/{}", input.id), &[]).await
    }

    #[tool(description = "List pipelines (flows), filterable by repo, kind, active state, name, and batch.")]
    async fn list_pipelines(&self, Parameters(i): Parameters<ListPipelinesInput>) -> String {
        let q = vec![
            ("repoId", i.repo_id.unwrap_or_default()),
            ("kind", i.kind.unwrap_or_default()),
            ("active", i.active.map(|b| b.to_string()).unwrap_or_default()),
            ("name", i.name.unwrap_or_default()),
            ("batch", i.batch.unwrap_or_default()),
            ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
            ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/pipelines", &q).await
    }

    #[tool(
        description = "List the flow batches (source-system groupings) per repository with flow counts; a \
            flow that declares no batch reports under the \"default\" batch. Then browse one batch's flows \
            with list_pipelines(repoId, batch)."
    )]
    async fn list_flow_batches(&self, Parameters(i): Parameters<ListBatchesInput>) -> String {
        let q = vec![
            ("repoId", i.repo_id.unwrap_or_default()),
            ("active", i.active.map(|b| b.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/pipelines/batches", &q).await
    }

    #[tool(description = "Get one pipeline (flow) by id.")]
    async fn get_pipeline(&self, Parameters(input): Parameters<GuidInput>) -> String {
        self.get(&format!("/api/v1/pipelines/{}", input.id), &[]).await
    }

    #[tool(description = "Get a pipeline's normalized flow definition as JSON.")]
    async fn pipeline_definition(&self, Parameters(input): Parameters<GuidInput>) -> String {
        self.get(&format!("/api/v1/pipelines/{}/definition", input.id), &[]).await
    }

    #[tool(description = "Get a pipeline's pre-ingestion transform columns (kind: declared|detected).")]
    async fn pipeline_columns(&self, Parameters(input): Parameters<PipelineColumnsInput>) -> String {
        let q = vec![("kind", input.kind.unwrap_or_default())];
        self.get(&format!("/api/v1/pipelines/{}/columns", input.id), &q).await
    }

    // ---- Runs (read) -----------------------------------------------------

    #[tool(description = "List runs, filterable by repo, pipeline, status, flow name, batch, and latest-only.")]
    async fn list_runs(&self, Parameters(i): Parameters<ListRunsInput>) -> String {
        let q = vec![
            ("repoId", i.repo_id.unwrap_or_default()),
            ("pipelineId", i.pipeline_id.unwrap_or_default()),
            ("status", i.status.unwrap_or_default()),
            ("flowName", i.flow_name.unwrap_or_default()),
            ("batch", i.batch.unwrap_or_default()),
            ("latest", i.latest.map(|b| b.to_string()).unwrap_or_default()),
            ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
            ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/runs", &q).await
    }

    #[tool(description = "Get one run by id (status, timings, row counts).")]
    async fn get_run(&self, Parameters(i): Parameters<RunIdInput>) -> String {
        self.get(&format!("/api/v1/runs/{}", i.run_id), &[]).await
    }

    #[tool(description = "Get the SQL statement trace for a run.")]
    async fn run_statements(&self, Parameters(i): Parameters<RunIdInput>) -> String {
        self.get(&format!("/api/v1/runs/{}/statements", i.run_id), &[]).await
    }

    #[tool(description = "Get the assertion results for a run.")]
    async fn run_assertions(&self, Parameters(i): Parameters<RunIdInput>) -> String {
        self.get(&format!("/api/v1/runs/{}/assertions", i.run_id), &[]).await
    }

    #[tool(description = "Get the processed files for a run.")]
    async fn run_files(&self, Parameters(i): Parameters<RunIdInput>) -> String {
        self.get(&format!("/api/v1/runs/{}/files", i.run_id), &[]).await
    }

    #[tool(description = "Get the health-check metrics captured by a run.")]
    async fn run_health_metrics(&self, Parameters(i): Parameters<RunIdInput>) -> String {
        self.get(&format!("/api/v1/runs/{}/health-metrics", i.run_id), &[]).await
    }

    // ---- Lineage (read) --------------------------------------------------

    #[tool(
        description = "List the schema hierarchy: each (server, database, schema) grouping in the catalog with \
            its object count. Start here to browse what schemas exist, then list objects within one. Filter by \
            serverRef and database."
    )]
    async fn list_schemas(&self, Parameters(i): Parameters<ListSchemasInput>) -> String {
        let q = vec![
            ("serverRef", i.server_ref.unwrap_or_default()),
            ("database", i.database.unwrap_or_default()),
        ];
        self.get("/api/v1/lineage/schemas", &q).await
    }

    #[tool(description = "List catalog lineage objects (tables/views/procs/functions/files), filterable by name, server, database, schema, and kind.")]
    async fn lineage_objects(&self, Parameters(i): Parameters<LineageObjectsInput>) -> String {
        let q = vec![
            ("name", i.name.unwrap_or_default()),
            ("serverRef", i.server_ref.unwrap_or_default()),
            ("database", i.database.unwrap_or_default()),
            ("schema", i.schema.unwrap_or_default()),
            ("kind", i.kind.unwrap_or_default()),
            ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
            ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/lineage/objects", &q).await
    }

    #[tool(description = "Get a lineage object's detail (including its module definition) by catalog key.")]
    async fn lineage_object_detail(&self, Parameters(i): Parameters<KeyInput>) -> String {
        self.get("/api/v1/lineage/objects/detail", &[("key", i.key)]).await
    }

    #[tool(description = "Get a lineage object's columns by catalog key.")]
    async fn lineage_object_columns(&self, Parameters(i): Parameters<KeyInput>) -> String {
        self.get("/api/v1/lineage/objects/columns", &[("key", i.key)]).await
    }

    #[tool(
        description = "Describe one object in a single payload for text-to-query: its identity, columns \
            (name/type/nullability), interpreted key (keyColumns/keyOrigin, read from the codebase since \
            warehouses rarely declare physical keys), generating script and module body, the lineage edges \
            that reference it, and the interpreted data-model relationships (references/referencedBy: how \
            this table JOINS other tables, inferred from the codebase's own join predicates and constraint \
            clauses, with occurrence counts ranking the canonical join path). The primary tool for answering \
            questions about, or authoring SQL against, a specific table or view."
    )]
    async fn describe_object(&self, Parameters(i): Parameters<KeyInput>) -> String {
        self.get("/api/v1/lineage/objects/dossier", &[("key", i.key)]).await
    }

    #[tool(
        description = "List the file sources of the catalog: every file endpoint decomposed to its canonical \
            parent, so files group by the system they live in exactly as tables group by their database. Each \
            entry has an originKind, which is also the PROVIDER to group under (AzureStorage / AmazonS3 / \
            GoogleCloud / Sftp / NetworkShare / Local / Other), the origin (the storage account, S3/GCS \
            bucket, or SFTP host:port; the constant filesystem for Local), the container (Azure container or \
            UNC share; null otherwise), the folder path, and the leaf name, plus the object key. Fold them \
            into provider > origin > container > folder > file, and use describe_object on a key for which \
            flows read or write that file."
    )]
    async fn list_file_sources(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get("/api/v1/lineage/file-tree", &[]).await
    }

    #[tool(
        description = "A file source's provenance: given a file object key, the pipelines that PRODUCE it \
            (where it comes from) and the pipelines that CONSUME it, each with the tables the data LANDS in \
            (where it goes). Answers 'what pipelines use this source and where does the data land' in one call, \
            the source-to-target chain for a file."
    )]
    async fn file_provenance(&self, Parameters(i): Parameters<KeyInput>) -> String {
        self.get("/api/v1/lineage/file-flows", &[("key", i.key)]).await
    }

    #[tool(
        description = "Browse the catalog as a folder tree (the semantic layer): with no arguments it lists \
            every (server, database, schema) grouping with object counts; add serverRef and/or database to \
            narrow; add schema to get that schema's object-kind groups (Tables/Views/Procedures/...) with \
            counts; add kind to list the objects in that group, paged. This is the database branch; use \
            list_file_sources for the file branch (storage accounts / SFTP servers) and list_flow_batches / \
            list_pipelines for the flow branch. Use describe_object on any returned key for a node's full \
            details (columns, key, code, relationships)."
    )]
    async fn catalog_tree(&self, Parameters(i): Parameters<CatalogTreeInput>) -> String {
        // The argument ladder mirrors the tree's levels; a deeper argument without its parents would silently
        // aggregate across the missing level, so it is refused with directions instead.
        if i.kind.is_some() && i.schema.is_none() {
            return "Give schema (and serverRef/database) together with kind: kind lists the objects of one \
                schema's kind group. Call catalog_tree with schema first to see the kind groups."
                .to_string();
        }
        if i.schema.is_some() && i.server_ref.is_none() {
            return "Give serverRef (and database) together with schema: schema names are only unique within \
                a database. Call catalog_tree with no arguments to see the schema hierarchy."
                .to_string();
        }

        if let Some(kind) = i.kind {
            let q = vec![
                ("serverRef", i.server_ref.unwrap_or_default()),
                ("database", i.database.unwrap_or_default()),
                ("schema", i.schema.unwrap_or_default()),
                ("kind", kind),
                ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
                ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
            ];
            return self.get("/api/v1/lineage/objects", &q).await;
        }
        if i.schema.is_some() {
            let q = vec![
                ("serverRef", i.server_ref.unwrap_or_default()),
                ("database", i.database.unwrap_or_default()),
                ("schema", i.schema.unwrap_or_default()),
            ];
            return self.get("/api/v1/lineage/schemas/kinds", &q).await;
        }
        let q = vec![
            ("serverRef", i.server_ref.unwrap_or_default()),
            ("database", i.database.unwrap_or_default()),
        ];
        self.get("/api/v1/lineage/schemas", &q).await
    }

    #[tool(description = "List the lineage edges (read/write relations) for a repository.")]
    async fn lineage_edges(&self, Parameters(i): Parameters<RepoIdInput>) -> String {
        self.get(&format!("/api/v1/repos/{}/lineage/edges", i.repo_id), &[]).await
    }

    #[tool(description = "Get the execution waves (concurrency plan) for a repository's flows.")]
    async fn lineage_waves(&self, Parameters(i): Parameters<RepoIdInput>) -> String {
        self.get(&format!("/api/v1/repos/{}/waves", i.repo_id), &[]).await
    }

    #[tool(
        description = "Get the flow-to-flow execution dependencies for a repository; pass pipelineId to keep \
            only the edges touching one flow (what it waits for and what it unblocks)."
    )]
    async fn lineage_dependencies(&self, Parameters(i): Parameters<DependenciesInput>) -> String {
        let q = vec![("pipelineId", i.pipeline_id.unwrap_or_default())];
        self.get(&format!("/api/v1/repos/{}/dependencies", i.repo_id), &q).await
    }

    // ---- Search (read) ---------------------------------------------------

    #[tool(description = "Full-text search catalog objects by name.")]
    async fn search_objects(&self, Parameters(i): Parameters<SearchInput>) -> String {
        let q = vec![
            ("name", i.query),
            ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
            ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/search/objects", &q).await
    }

    #[tool(description = "Full-text search catalog columns by name.")]
    async fn search_columns(&self, Parameters(i): Parameters<SearchInput>) -> String {
        let q = vec![
            ("name", i.query),
            ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
            ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/search/columns", &q).await
    }

    #[tool(description = "Full-text search object definitions (module bodies).")]
    async fn search_definitions(&self, Parameters(i): Parameters<SearchInput>) -> String {
        let q = vec![
            ("q", i.query),
            ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
            ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/search/definitions", &q).await
    }

    // ---- Schedules, nodes, sources, summary (read) -----------------------

    #[tool(
        description = "List schedules: when each fires (cron/interval, timezone, next fire) and its scope \
                       ('flow' runs one flow; 'node'/'batch' expand through lineage and run a whole set in wave \
                       order). Start here to answer 'when does <source> get updated'."
    )]
    async fn list_schedules(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get("/api/v1/schedules", &[]).await
    }

    #[tool(description = "Get one schedule by id.")]
    async fn get_schedule(&self, Parameters(i): Parameters<GuidInput>) -> String {
        self.get(&format!("/api/v1/schedules/{}", i.id), &[]).await
    }

    #[tool(
        description = "When a source next gets updated AND exactly what that run executes: the schedule's cadence \
                       (cron, timezone, next/last fire) plus the lineage-resolved flows in wave order. Each member \
                       carries its wave; members sharing a wave run concurrently, and a wave starts only once the \
                       previous wave has finished. Use this to answer 'when does <source> update', 'what runs when \
                       it fires', and 'in what order'."
    )]
    async fn get_schedule_plan(&self, Parameters(i): Parameters<GuidInput>) -> String {
        self.get(&format!("/api/v1/schedules/{}/plan", i.id), &[]).await
    }

    #[tool(description = "List worker nodes (the compute fleet) and their heartbeats.")]
    async fn list_nodes(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get("/api/v1/nodes", &[]).await
    }

    #[tool(description = "List the registered git repo sources the control plane auto-syncs.")]
    async fn list_repo_sources(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get("/api/v1/repos/sources", &[]).await
    }

    #[tool(description = "Get the dashboard summary rollup (repos, pipelines, runs, health).")]
    async fn summary(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get("/api/v1/summary", &[]).await
    }

    // ---- Insights (read) -------------------------------------------------

    #[tool(
        description = "Per-flow performance over a window: run/failure counts, avg/max/total durations, \
            rows/sec throughput, last-run outcome, and the duration trend versus the previous window. Ordered \
            by total processing time, so the first rows ARE 'where the time goes'. Start here for 'what is \
            slow' and 'what got slower'."
    )]
    async fn insights_flows(&self, Parameters(i): Parameters<InsightsFlowsInput>) -> String {
        let q = vec![
            ("days", i.days.map(|n| n.to_string()).unwrap_or_default()),
            ("repoId", i.repo_id.unwrap_or_default()),
            ("batch", i.batch.unwrap_or_default()),
            // A context-frugal default: the list is ordered by total time, so 25 rows carry the story.
            ("limit", i.limit.unwrap_or(25).to_string()),
        ];
        self.get("/api/v1/insights/flows", &q).await
    }

    #[tool(
        description = "The ranked 'what needs attention' list computed from the window's run history: flows \
            failing repeatedly, whose last run failed, getting slower, gone quiet (loaded rows before, none \
            now), dominating processing time, or active-but-silent. Deduplicated (one item per flow, extra \
            findings in an 'Also:' note) and batch-collapsed (a source whose flows tripped together reads as \
            one item with flowCount). Capped at limit; totalItems and the severity counts cover everything \
            found."
    )]
    async fn insights_attention(&self, Parameters(i): Parameters<InsightsWindowInput>) -> String {
        let q = vec![
            ("days", i.days.map(|n| n.to_string()).unwrap_or_default()),
            ("repoId", i.repo_id.unwrap_or_default()),
            ("batch", i.batch.unwrap_or_default()),
            ("limit", i.limit.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/insights/attention", &q).await
    }

    #[tool(
        description = "The one-call optimization briefing: run-history advisories merged with the newest \
            warehouse DMV probe results (missing indexes, stale statistics, unused indexes, expensive queries) \
            into a single ranked list, deduplicated and capped at limit (default 20; totalItems and the \
            severity counts cover everything found). Compact by default: items report hasSuggestedSql; pass \
            includeSql=true to carry the ready-to-review statements (CREATE INDEX, UPDATE STATISTICS, DROP \
            INDEX) - present them for human review, never execute them unreviewed. The warehouseProbes field \
            reports how fresh each DMV dimension is; when it is empty or stale, run analyze_warehouse_health \
            first and re-read. The best first call for 'what should we optimize'."
    )]
    async fn insights_recommendations(&self, Parameters(i): Parameters<InsightsWindowInput>) -> String {
        let q = vec![
            ("days", i.days.map(|n| n.to_string()).unwrap_or_default()),
            ("repoId", i.repo_id.unwrap_or_default()),
            ("batch", i.batch.unwrap_or_default()),
            ("limit", i.limit.map(|n| n.to_string()).unwrap_or_default()),
            ("includeSql", i.include_sql.map(|b| b.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/insights/recommendations", &q).await
    }

    #[tool(
        description = "Drill one flow down to its engine steps: avg/max/total elapsed per step across the \
            window's runs, rows processed, and a sample of the SQL the step executed in the newest traced run. \
            Use after insights_flows/attention names a slow flow, to see WHICH step (and which SQL) eats the time."
    )]
    async fn insights_steps(&self, Parameters(i): Parameters<InsightsStepsInput>) -> String {
        let q = vec![
            ("days", i.days.map(|n| n.to_string()).unwrap_or_default()),
            ("includeSql", i.include_sql.map(|b| b.to_string()).unwrap_or_default()),
        ];
        self.get(&format!("/api/v1/insights/pipelines/{}/steps", i.pipeline_id), &q)
            .await
    }

    #[tool(
        description = "Run a live warehouse DMV probe on a worker node (requires the 'operate' scope) and wait \
            for its result: missingIndexes (sys.dm_db_missing_index_*, with CREATE INDEX suggestions), \
            statisticsHealth (sys.dm_db_stats_properties staleness, with UPDATE STATISTICS suggestions), \
            indexUsage (sys.dm_db_index_usage_stats, flagging write-only indexes), or topQueries \
            (sys.dm_exec_query_stats by total elapsed time). Omit reference to probe the estate's busiest \
            target datasource (the warehouse). SQL Server / Azure SQL sources only. The result also feeds \
            insights_recommendations for later calls."
    )]
    async fn analyze_warehouse_health(&self, Parameters(i): Parameters<WarehouseHealthInput>) -> String {
        done(self.run_warehouse_probe(i).await)
    }

    // ---- Operate (write) -------------------------------------------------

    #[tool(
        description = "Trigger a run of a flow (requires the 'operate' scope). Returns the queued run id; poll get_run for the outcome."
    )]
    async fn trigger_run(&self, Parameters(i): Parameters<TriggerRunInput>) -> String {
        let mut body = json!({
            "repoId": i.repo_id,
            "flowName": i.flow_name,
            "fullLoad": i.full_load.unwrap_or(false),
        });
        if let Some(p) = i.pool { body["pool"] = json!(p); }
        if let Some(c) = i.commit_sha { body["commitSha"] = json!(c); }
        if let Some(f) = i.backfill_from { body["backfillFrom"] = json!(f); }
        if let Some(t) = i.backfill_to { body["backfillTo"] = json!(t); }
        if let Some(fp) = i.file_pattern { body["filePattern"] = json!(fp); }
        done(self.cp.post("/api/v1/runs", body).await.map(|v| json_str(&v)))
    }

    #[tool(description = "Cancel an in-flight or queued run (requires the 'operate' scope).")]
    async fn cancel_run(&self, Parameters(i): Parameters<RunIdInput>) -> String {
        done(
            self.cp
                .post(&format!("/api/v1/runs/{}/cancel", i.run_id), json!({}))
                .await
                .map(|v| if v.is_null() { "Cancellation requested.".to_string() } else { json_str(&v) }),
        )
    }

    #[tool(
        description = "Propose pipelines to a tracked repo source as a pull request (requires the 'author' scope). \
Writes the given flow files onto a fresh branch off the source's tracked branch, commits them under the calling \
user, and opens a pull request for review; it never writes the catalog directly, so a human reviews and merges \
before the managed sync imports the flows. Returns the pull-request URL and number, the pushed head branch, and \
the head commit SHA. Feed that commitSha to trigger_run to test the proposal pinned to the PR commit before it \
merges (a changed flow already in the catalog runs this way; a brand-new flow is not in the catalog until the PR \
merges and syncs). Generate the files with discover_source and validate each with validate_flow before proposing. \
Works over both stdio and HTTP (it only proxies the control plane)."
    )]
    async fn propose_pipelines(&self, Parameters(i): Parameters<ProposePipelinesInput>) -> String {
        let files: Vec<Value> = i
            .files
            .into_iter()
            .map(|f| json!({ "path": f.path, "content": f.content }))
            .collect();
        let mut body = json!({ "title": i.title, "files": files });
        if let Some(b) = i.body {
            body["body"] = json!(b);
        }
        if let Some(bb) = i.base_branch {
            body["baseBranch"] = json!(bb);
        }
        if let Some(hb) = i.head_branch {
            body["headBranch"] = json!(hb);
        }
        done(
            self.cp
                .post(&format!("/api/v1/repos/sources/{}/proposals", i.source_id), body)
                .await
                .map(|v| json_str(&v)),
        )
    }

    #[tool(
        description = "Scan a JSON/NDJSON/XML sample file or folder and generate a ready-to-run `.flow.yaml` \
stub (mode=flatten), or report its path structure (mode=paths|discover). The record grain (JSON rootPath / \
XML rowXPath) is auto-detected from sample statistics, so an envelope like {items:[...]} or an RSS feed \
produces one row per record without a hand-written config. Runs the local `sqlflow` CLI, so it reads any \
path (local, UNC, or cloud) the CLI's file stores can reach. Validate the emitted YAML with validate_flow \
before returning it."
    )]
    async fn discover_source(&self, Parameters(i): Parameters<DiscoverSourceInput>) -> String {
        if self.http_mode {
            return HTTP_MODE_DISCOVER_NOTE.to_string();
        }
        let mode = i.mode.as_deref().unwrap_or("flatten").to_ascii_lowercase();
        let subcommand = match mode.as_str() {
            "flatten" | "paths" | "discover" => mode.as_str(),
            other => {
                return format!("Unknown mode '{other}'. Use 'flatten', 'paths', or 'discover'.");
            }
        };

        let mut args: Vec<String> = vec![subcommand.to_string(), i.path.clone()];
        if let Some(p) = &i.pattern {
            args.push("--pattern".into());
            args.push(p.clone());
        }
        if i.recursive.unwrap_or(false) {
            args.push("--recursive".into());
        }
        if let Some(n) = i.max_files {
            args.push("--max-files".into());
            args.push(n.to_string());
        }
        if let Some(n) = i.max_records {
            args.push("--max-records".into());
            args.push(n.to_string());
        }
        if let Some(n) = i.max_depth {
            args.push("--max-depth".into());
            args.push(n.to_string());
        }
        if let Some(r) = &i.root {
            args.push("--root".into());
            args.push(r.clone());
        }

        // Flatten-only rule flags (the other modes ignore them).
        if subcommand == "flatten" {
            for (flag, value) in [
                ("--include", &i.include),
                ("--exclude", &i.exclude),
                ("--explode", &i.explode),
                ("--keep", &i.keep),
                ("--array", &i.array),
                ("--separator", &i.separator),
            ] {
                if let Some(v) = value {
                    args.push(flag.into());
                    args.push(v.clone());
                }
            }
        }

        match run_sqlflow_cli(args).await {
            Ok(output) => output,
            Err(message) => message,
        }
    }
}

impl SqlFlowMcp {
    /// Shared GET-and-render used by every read tool.
    async fn get(&self, path: &str, query: &[(&str, String)]) -> String {
        done(self.cp.get(path, query).await.map(|v| json_str(&v)))
    }

    /// Enqueues one warehouse-health compute task and long-polls it to a terminal state. The default
    /// datasource is the estate's busiest resolvable SQL Server target (by pipelines writing through it):
    /// in this product's model that IS the warehouse. The poll budget comfortably exceeds the server's
    /// two-minute probe budget, so a hung probe still terminates here with the task's own timeout error.
    async fn run_warehouse_probe(&self, input: WarehouseHealthInput) -> anyhow::Result<String> {
        const OPERATIONS: [&str; 4] = ["missingIndexes", "statisticsHealth", "indexUsage", "topQueries"];
        if !OPERATIONS.contains(&input.operation.as_str()) {
            anyhow::bail!(
                "Unknown warehouse-health operation '{}'. Valid operations: {}.",
                input.operation,
                OPERATIONS.join(", ")
            );
        }

        let reference = match input.reference {
            Some(reference) if !reference.trim().is_empty() => reference.trim().to_string(),
            _ => {
                let datasources = self.cp.get("/api/v1/datasources", &[]).await?;
                datasources
                    .as_array()
                    .into_iter()
                    .flatten()
                    .filter(|d| {
                        d["resolvable"].as_bool() == Some(true)
                            && matches!(d["kind"].as_str(), None | Some("MSSQL") | Some("AZDB"))
                    })
                    .max_by_key(|d| d["targetPipelines"].as_i64().unwrap_or(0))
                    .and_then(|d| d["reference"].as_str().map(String::from))
                    .ok_or_else(|| anyhow::anyhow!(
                        "No resolvable SQL Server datasource found to probe. Pass `reference` explicitly \
                         (see the datasources list)."
                    ))?
            }
        };

        let mut body = json!({ "reference": reference, "operation": input.operation });
        if let Some(database) = input.database.filter(|d| !d.trim().is_empty()) {
            body["database"] = json!(database.trim());
        }
        // Context-frugal default: 20 ranked rows answer the question; the task row keeps whatever ran.
        body["limit"] = json!(input.limit.unwrap_or(20));
        if let Some(pool) = input.pool.filter(|p| !p.trim().is_empty()) {
            body["pool"] = json!(pool);
        }

        let accepted = self.cp.post("/api/v1/datasources/tasks", body).await?;
        let task_id = accepted["taskId"]
            .as_str()
            .map(String::from)
            .ok_or_else(|| anyhow::anyhow!("The control plane's accept response carried no taskId: {accepted}"))?;

        // Each poll long-polls server-side for up to 20s; twelve rounds outlast the probe's own budget.
        for _ in 0..12 {
            let mut task = self
                .cp
                .get(&format!("/api/v1/datasources/tasks/{task_id}"), &[("waitMs", "20000".to_string())])
                .await?;
            match task["status"].as_str() {
                Some("succeeded") | Some("failed") | Some("cancelled") | Some("skipped") => {
                    truncate_long_strings(&mut task, 400);
                    return Ok(json_str(&task));
                }
                _ => {}
            }
        }

        anyhow::bail!(
            "The {} probe (task {task_id}) did not reach a terminal state in time; check it with the \
             datasources task list.",
            input.operation
        )
    }
}

/// Tools that take no arguments still need a parameter type for the macro.
#[derive(Debug, Deserialize, JsonSchema)]
pub struct EmptyInput {}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct DiscoverSourceInput {
    /// Path to a JSON/NDJSON/XML sample file, or a folder of them. Local or UNC
    /// paths, and any cloud location the SQLFlow CLI's file stores handle. For a
    /// folder, set `pattern` (and `recursive` to walk sub-folders).
    pub path: String,
    /// One of: "flatten" (default) emits a runnable `.flow.yaml` stub with the
    /// resolved column schema; "paths" lists every addressable path; "discover"
    /// reports the path structure and the starter options. All three auto-detect
    /// the record grain (JSON rootPath / XML rowXPath) from sample statistics.
    pub mode: Option<String>,
    /// Glob narrowing a folder scan, e.g. "*.json" or "orders_*.xml". Ignored for a single file.
    pub pattern: Option<String>,
    /// Walk sub-folders of a folder path (default false).
    pub recursive: Option<bool>,
    /// Cap the number of sample files scanned (default 100).
    #[serde(rename = "maxFiles")]
    pub max_files: Option<u32>,
    /// Cap the number of sample records scanned (0 = unbounded).
    #[serde(rename = "maxRecords")]
    pub max_records: Option<u32>,
    /// Max nesting depth to inspect (flatten/paths default 20, discover 10).
    #[serde(rename = "maxDepth")]
    pub max_depth: Option<u32>,
    /// Pin the record grain instead of auto-detecting it: a JSON rootPath (e.g.
    /// "$.items") or an XML rowXPath (e.g. "/rss/channel/item"). Overrides detection.
    pub root: Option<String>,
    /// Comma-separated paths to flatten (whitelist). Flatten mode only.
    pub include: Option<String>,
    /// Comma-separated paths whose subtree is dropped. Flatten mode only.
    pub exclude: Option<String>,
    /// Comma-separated repeating paths to explode into one row each. Flatten mode only.
    pub explode: Option<String>,
    /// Comma-separated paths kept verbatim as one string column (jsonPaths/xmlPaths). Flatten mode only.
    pub keep: Option<String>,
    /// JSON array handling: to_json | first_element | join | count | skip | explode. Flatten mode only.
    pub array: Option<String>,
    /// Column-name separator (default "_"). Flatten mode only.
    pub separator: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ProposePipelinesInput {
    /// The tracked repo source to propose into (its id from list_repo_sources).
    #[serde(rename = "sourceId")]
    pub source_id: String,
    /// The pull-request title; also the commit summary.
    pub title: String,
    /// Optional pull-request description / commit body.
    pub body: Option<String>,
    /// Branch to open the pull request against; defaults to the source's tracked branch.
    #[serde(rename = "baseBranch")]
    pub base_branch: Option<String>,
    /// Feature branch to push; defaults to a content-derived `sqlflow/proposal-*` name.
    #[serde(rename = "headBranch")]
    pub head_branch: Option<String>,
    /// The flow files to write: each a repo-relative path plus its full content.
    pub files: Vec<ProposeFileInput>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ProposeFileInput {
    /// Repo-relative path ending in a flow extension (.flow.yaml / .yaml / .yml / .sql / .json / .md),
    /// e.g. "flows/orders.01_pre.flow.yaml".
    pub path: String,
    /// The file's full content.
    pub content: String,
}

// --- Server handler --------------------------------------------------------

/// The bearer on an inbound HTTP request's `Authorization` header, when present and
/// well-formed (`Bearer <non-empty token>`, scheme case-insensitive per RFC 6750).
pub(crate) fn bearer_token(headers: &http::HeaderMap) -> Option<String> {
    let value = headers.get(http::header::AUTHORIZATION)?.to_str().ok()?;
    let (scheme, rest) = value.trim().split_once(' ')?;
    if !scheme.eq_ignore_ascii_case("bearer") {
        return None;
    }
    let token = rest.trim();
    (!token.is_empty()).then(|| token.to_string())
}

#[tool_handler]
impl ServerHandler for SqlFlowMcp {
    /// Hand-written dispatch (the #[tool_handler] macro only generates `call_tool`
    /// when the impl lacks one): over HTTP, rmcp injects the request's
    /// `http::request::Parts` into the context extensions, and the caller's bearer is
    /// scoped into `HTTP_BEARER` around the tool call so every control-plane request
    /// runs as that caller. Over stdio there are no parts and dispatch is unchanged.
    async fn call_tool(
        &self,
        request: rmcp::model::CallToolRequestParams,
        context: rmcp::service::RequestContext<rmcp::RoleServer>,
    ) -> Result<rmcp::model::CallToolResult, rmcp::ErrorData> {
        let bearer = context
            .extensions
            .get::<http::request::Parts>()
            .and_then(|parts| bearer_token(&parts.headers));
        let tcc = rmcp::handler::server::tool::ToolCallContext::new(self, request, context);
        match bearer {
            Some(token) => {
                crate::control_plane::HTTP_BEARER
                    .scope(token, self.tool_router.call(tcc))
                    .await
            }
            None => self.tool_router.call(tcc).await,
        }
    }

    fn get_info(&self) -> ServerInfo {
        let online_setup = if self.http_mode { ONLINE_SETUP_HTTP } else { ONLINE_SETUP_STDIO };
        let discovery = if self.http_mode { "" } else { DISCOVERY_STDIO };
        ServerInfo::new(ServerCapabilities::builder().enable_tools().build())
            .with_instructions(format!(
                "{INSTRUCTIONS_OFFLINE}{discovery}\n{online_setup}{INSTRUCTIONS_ONLINE_TAIL}"
            ))
    }
}

const INSTRUCTIONS_OFFLINE: &str = "\
SQLFlow MCP server. Two tiers of tools:

OFFLINE (always available):
- Docs: search_docs, get_doc, get_doc_by_yaml_path, get_doc_by_cli_command, related_docs, list_docs.
  For ANY question about a SQLFlow CLI command, a `.flow.yaml` key, a source type, or a concept,
  search the docs FIRST and cite the page id; do not answer from memory.
- Flow language: validate_flow (parse + census diagnostics), list_flow_keys, describe_flow_key.
  Before returning any `.flow.yaml` you authored or edited, run validate_flow and fix what it reports.
";

const DISCOVERY_STDIO: &str = "\
- Source discovery: discover_source scans a JSON/NDJSON/XML sample (local, UNC, or cloud) and generates a
  runnable `.flow.yaml` stub (mode=flatten) or reports its path structure (mode=paths|discover). It auto-
  detects the record grain, so an envelope or nested-repeater document yields one row per record. Prefer it
  over hand-writing a file-source flow; then validate_flow the result. Needs the `sqlflow` CLI on PATH (or
  SQLFLOW_CLI set).
";

const ONLINE_SETUP_STDIO: &str = "\
ONLINE (needs the control plane; sign in first):
- Setup: get/set_control_plane_url, check_connectivity, login (device flow) then check_auth_status,
  or set_access_token to paste a bearer token.
";

const ONLINE_SETUP_HTTP: &str = "\
ONLINE (needs the control plane):
- Auth is per request: every call to this server already carries the caller's bearer token, and
  control-plane requests run as that caller with scopes enforced server-side. There is no login
  step; check_connectivity probes reachability.
";

const INSTRUCTIONS_ONLINE_TAIL: &str = "\
- Read: list_repos, list_pipelines, get_pipeline, pipeline_definition, pipeline_columns, list_runs,
  get_run, run_statements/assertions/files/health_metrics, lineage_objects/_detail/_columns/_edges/
  _waves/_dependencies, search_objects/_columns/_definitions, list_schedules, get_schedule,
  get_schedule_plan, list_nodes, list_repo_sources, summary.
- \"When does <source> update?\": list_schedules finds the schedule (its cron/timezone/next fire), then
  get_schedule_plan returns both the cadence AND the wave-ordered flows that fire runs. A schedule whose
  scope is 'node'/'batch' runs a whole set resolved through lineage, so the plan (not the schedule's own
  flow name) is what actually gets updated; members sharing a wave run concurrently.
- Browse the schema: list_schemas gives every (server, database, schema) with its object count; then
  lineage_objects filters by database/schema/kind/name to enumerate the tables and views in one. This is how
  you answer open schema questions without a pre-known object key.
- Ask about an object (text-to-query): describe_object returns one object's identity, columns, generating
  script, module body, and lineage edges in one call: start here to reason about, or author SQL against, a
  specific table or view.
- Performance and optimization: insights_recommendations is the one-call briefing (run-history advisories
  merged with warehouse DMV findings, each with ready-to-review SQL); insights_flows ranks flows by
  processing time with trends; insights_attention lists what is failing/degrading/silent; insights_steps
  drills a slow flow to its hot steps and their SQL. When the warehouse dimension is missing or stale, run
  analyze_warehouse_health (missingIndexes / statisticsHealth / indexUsage / topQueries) and re-read.
  Suggested SQL from these tools is for human review, never for unreviewed execution.
- Operate (privileged): trigger_run, cancel_run, analyze_warehouse_health.

SQLFlow authors T-SQL against SQL Server and orchestrates it with `.flow.yaml` documents. It is a
distinct product from DeltaForge; use these tools and the embedded corpus as the source of truth.";
