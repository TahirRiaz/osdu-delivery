namespace SqlFlow.SlackBot;

/// <summary>
/// Configuration for the Slack bot, bound from the <c>SlackBot</c> section (or the equivalent
/// <c>SlackBot__*</c> environment variables in a container). <see cref="Validate"/> runs once at
/// startup and fails fast with the full list of missing settings, so a misconfigured deployment
/// dies with one actionable message instead of a stream of runtime errors.
/// </summary>
public sealed class SlackBotOptions
{
    public SlackOptions Slack { get; set; } = new();
    public FoundryOptions Foundry { get; set; } = new();
    public SqlFlowOptions SqlFlow { get; set; } = new();

    /// <summary>Ceiling for one agent run before it is cancelled and reported as timed out.</summary>
    public int RunTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// How many questions are answered concurrently. A burst beyond this queues (each question
    /// already has its eyes acknowledgement) instead of stampeding the Foundry deployment's
    /// rate limits.
    /// </summary>
    public int MaxConcurrentAnswers { get; set; } = 4;

    /// <summary>
    /// How many prior Slack-thread messages are replayed into a fresh Foundry thread when the
    /// in-memory mapping was lost (bot restart). The Slack thread is the durable transcript;
    /// the Foundry thread is only a cache of it.
    /// </summary>
    public int MaxReplayMessages { get; set; } = 20;

    /// <summary>How many image attachments on one message are sent to the vision model. 0 disables image
    /// reading. Extra images past the cap are ignored.</summary>
    public int MaxImages { get; set; } = 4;

    /// <summary>Largest image (bytes) sent to the model; a larger attachment is skipped rather than
    /// inflating the request. The Responses API also enforces its own per-image ceiling.</summary>
    public long MaxImageBytes { get; set; } = 8_000_000;

    public void Validate()
    {
        var missing = new List<string>();
        void Require(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(name);
            }
        }

        Require(Slack.AppToken, "SlackBot:Slack:AppToken (xapp-..., Socket Mode app-level token)");
        Require(Slack.BotToken, "SlackBot:Slack:BotToken (xoxb-..., bot user OAuth token)");
        Require(Foundry.ProjectEndpoint, "SlackBot:Foundry:ProjectEndpoint (https://<account>.services.ai.azure.com/api/projects/<project>)");
        Require(Foundry.ModelDeploymentName, "SlackBot:Foundry:ModelDeploymentName");
        Require(Foundry.McpServerUrl, "SlackBot:Foundry:McpServerUrl (https://<sqlflow-mcp host>/mcp)");
        Require(SqlFlow.AccessToken, "SlackBot:SqlFlow:AccessToken (sqlf_..., a read-scoped personal access token)");

        if (RunTimeoutSeconds < 10)
        {
            missing.Add($"SlackBot:RunTimeoutSeconds must be at least 10 (was {RunTimeoutSeconds})");
        }
        if (MaxConcurrentAnswers < 1)
        {
            missing.Add($"SlackBot:MaxConcurrentAnswers must be at least 1 (was {MaxConcurrentAnswers})");
        }
        if (MaxReplayMessages < 0)
        {
            missing.Add($"SlackBot:MaxReplayMessages must not be negative (was {MaxReplayMessages})");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "SqlFlow.SlackBot configuration is incomplete:\n  - " + string.Join("\n  - ", missing));
        }
    }
}

public sealed class SlackOptions
{
    /// <summary>App-level token with the <c>connections:write</c> scope; opens the Socket Mode websocket.</summary>
    public string AppToken { get; set; } = "";

    /// <summary>Bot user OAuth token; posts messages, adds reactions, reads thread replies.</summary>
    public string BotToken { get; set; } = "";
}

public sealed class FoundryOptions
{
    /// <summary>The Foundry project endpoint the agent lives in.</summary>
    public string ProjectEndpoint { get; set; } = "";

    /// <summary>The model deployment (in the same Foundry account) the assistant runs on.</summary>
    public string ModelDeploymentName { get; set; } = "";

    /// <summary>The deployed SQLFlow MCP server's endpoint, e.g. https://sqlflow-mcp.internal.example/mcp.</summary>
    public string McpServerUrl { get; set; } = "";

    /// <summary>The MCP server label shared between the tool definition and its per-run resources.</summary>
    public string McpServerLabel { get; set; } = "sqlflow";

    /// <summary>
    /// The MCP tools the agent may call. The default is the read-only surface: no trigger_run or
    /// cancel_run (everyone in a channel shares the bot's identity, so writes stay off this path),
    /// and no stdio-only or sign-in tools (inert over HTTP anyway). An empty list means all tools,
    /// so leave this populated unless the MCP server itself is restricted.
    /// </summary>
    public List<string> AllowedTools { get; set; } =
    [
        "search_docs", "get_doc", "get_doc_by_yaml_path", "get_doc_by_cli_command", "related_docs", "list_docs",
        "validate_flow", "list_flow_keys", "describe_flow_key",
        "check_connectivity", "get_control_plane_url",
        "list_repos", "get_repo", "list_pipelines", "get_pipeline", "pipeline_definition", "pipeline_columns",
        "list_runs", "get_run", "run_statements", "run_assertions", "run_files", "run_health_metrics",
        "list_schemas", "lineage_objects", "lineage_object_detail", "lineage_object_columns", "describe_object",
        "lineage_edges", "lineage_waves", "lineage_dependencies",
        "search_objects", "search_columns", "search_definitions",
        "list_schedules", "get_schedule", "list_nodes", "list_repo_sources", "summary",
    ];
}

public sealed class SqlFlowOptions
{
    /// <summary>
    /// The SQLFlow personal access token forwarded to the MCP server as the Authorization header
    /// on every agent run. Mint it read-scoped: this is the bot's whole authority.
    /// </summary>
    public string AccessToken { get; set; } = "";

    /// <summary>Optional GUI base URL; when set, answers link entities to their GUI pages.</summary>
    public string GuiBaseUrl { get; set; } = "";
}
