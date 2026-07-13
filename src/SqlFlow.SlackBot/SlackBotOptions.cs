namespace SqlFlow.SlackBot;

/// <summary>Which model provider answers questions. The MCP tool source, the instructions, and
/// the whole Slack experience are identical across providers; only the model endpoint and its
/// credential differ.</summary>
public enum AssistantProvider
{
    /// <summary>Azure AI Foundry via the Responses API, authenticated with the Azure credential
    /// (managed identity in the container). The default, and the only mode with no API key.</summary>
    AzureFoundry,

    /// <summary>The OpenAI platform directly (api.openai.com) via the same Responses API wire
    /// format, authenticated with an OpenAI API key. No Azure dependency.</summary>
    OpenAI,

    /// <summary>The Anthropic Claude API via the Messages API's MCP connector, authenticated with
    /// an Anthropic API key. No Azure dependency.</summary>
    Anthropic,
}

/// <summary>
/// Configuration for the Slack bot, bound from the <c>SlackBot</c> section (or the equivalent
/// <c>SlackBot__*</c> environment variables in a container). <see cref="Validate"/> runs once at
/// startup: it first normalizes legacy keys (the MCP tool settings historically lived under
/// <c>Foundry</c>), then fails fast with the full list of missing settings for the selected
/// provider, so a misconfigured deployment dies with one actionable message instead of a stream
/// of runtime errors.
/// </summary>
public sealed class SlackBotOptions
{
    /// <summary>The model provider answering questions. AzureFoundry preserves the original
    /// deployment shape; OpenAI and Anthropic run on a plain API key with no Azure dependency.</summary>
    public AssistantProvider Provider { get; set; } = AssistantProvider.AzureFoundry;

    public SlackOptions Slack { get; set; } = new();
    public McpOptions Mcp { get; set; } = new();
    public FoundryOptions Foundry { get; set; } = new();
    public OpenAIOptions OpenAI { get; set; } = new();
    public AnthropicOptions Anthropic { get; set; } = new();
    public SqlFlowOptions SqlFlow { get; set; } = new();

    /// <summary>Ceiling for one agent run before it is cancelled and reported as timed out.</summary>
    public int RunTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// How many questions are answered concurrently. A burst beyond this queues (each question
    /// already has its eyes acknowledgement) instead of stampeding the model provider's
    /// rate limits.
    /// </summary>
    public int MaxConcurrentAnswers { get; set; } = 4;

    /// <summary>
    /// How many prior Slack-thread messages are replayed when the provider-side conversation must
    /// be (re)built. The Slack thread is the durable transcript; provider state is only a cache
    /// of it (Anthropic has no server-side state at all and sends this many turns every call).
    /// </summary>
    public int MaxReplayMessages { get; set; } = 20;

    /// <summary>How many image attachments on one message are sent to the vision model. 0 disables image
    /// reading. Extra images past the cap are ignored.</summary>
    public int MaxImages { get; set; } = 4;

    /// <summary>Largest image (bytes) sent to the model; a larger attachment is skipped rather than
    /// inflating the request. Providers also enforce their own per-image ceilings.</summary>
    public long MaxImageBytes { get; set; } = 8_000_000;

    public void Validate()
    {
        NormalizeLegacyMcpSettings();

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
        Require(Mcp.ServerUrl, "SlackBot:Mcp:ServerUrl (https://<sqlflow-mcp host>/mcp)");
        Require(SqlFlow.AccessToken, "SlackBot:SqlFlow:AccessToken (sqlf_..., a read-scoped personal access token)");

        switch (Provider)
        {
            case AssistantProvider.AzureFoundry:
                Require(Foundry.ProjectEndpoint, "SlackBot:Foundry:ProjectEndpoint (https://<account>.services.ai.azure.com/api/projects/<project>)");
                Require(Foundry.ModelDeploymentName, "SlackBot:Foundry:ModelDeploymentName");
                break;
            case AssistantProvider.OpenAI:
                Require(OpenAI.ApiKey, "SlackBot:OpenAI:ApiKey (sk-..., an OpenAI platform API key)");
                Require(OpenAI.Model, "SlackBot:OpenAI:Model (a Responses-API + MCP-capable model, e.g. gpt-5-mini)");
                Require(OpenAI.BaseUrl, "SlackBot:OpenAI:BaseUrl");
                break;
            case AssistantProvider.Anthropic:
                Require(Anthropic.ApiKey, "SlackBot:Anthropic:ApiKey (sk-ant-..., an Anthropic API key)");
                Require(Anthropic.Model, "SlackBot:Anthropic:Model (e.g. claude-opus-4-8)");
                if (Anthropic.MaxTokens < 1024)
                {
                    missing.Add($"SlackBot:Anthropic:MaxTokens must be at least 1024 (was {Anthropic.MaxTokens})");
                }
                break;
            default:
                missing.Add($"SlackBot:Provider '{Provider}' is not a supported provider (AzureFoundry, OpenAI, Anthropic)");
                break;
        }

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

    /// <summary>
    /// The MCP tool settings originally lived under <c>Foundry</c> (McpServerUrl, McpServerLabel,
    /// AllowedTools); live deployments still set them there. They now live under <c>Mcp</c> because
    /// they apply to every provider. This is the single translation point: when <c>Mcp:ServerUrl</c>
    /// is not set explicitly, the legacy values are promoted, so existing configuration keeps
    /// working unchanged and all gateways read only <see cref="Mcp"/>.
    /// </summary>
    private void NormalizeLegacyMcpSettings()
    {
        if (!string.IsNullOrWhiteSpace(Mcp.ServerUrl))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(Foundry.McpServerUrl))
        {
            return;
        }
        Mcp.ServerUrl = Foundry.McpServerUrl;
        if (!string.IsNullOrWhiteSpace(Foundry.McpServerLabel))
        {
            Mcp.ServerLabel = Foundry.McpServerLabel;
        }
        if (Foundry.AllowedTools.Count > 0)
        {
            Mcp.AllowedTools = Foundry.AllowedTools;
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

/// <summary>The SQLFlow MCP server every provider uses as its tool source.</summary>
public sealed class McpOptions
{
    /// <summary>The deployed SQLFlow MCP server's endpoint, e.g. https://sqlflow-mcp.internal.example/mcp.</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>The MCP server label shared between the tool definition and its per-run resources.</summary>
    public string ServerLabel { get; set; } = "sqlflow";

    /// <summary>
    /// The MCP tools the assistant may call. The default is the read-only surface: no trigger_run or
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

/// <summary>Settings for <see cref="AssistantProvider.AzureFoundry"/> mode.</summary>
public sealed class FoundryOptions
{
    /// <summary>The Foundry project endpoint the assistant runs against.</summary>
    public string ProjectEndpoint { get; set; } = "";

    /// <summary>The model deployment (in the same Foundry account) the assistant runs on.</summary>
    public string ModelDeploymentName { get; set; } = "";

    /// <summary>Legacy alias for SlackBot:Mcp:ServerUrl; read only when the new key is unset.</summary>
    public string McpServerUrl { get; set; } = "";

    /// <summary>Legacy alias for SlackBot:Mcp:ServerLabel; read only when the new key is unset.</summary>
    public string McpServerLabel { get; set; } = "";

    /// <summary>Legacy alias for SlackBot:Mcp:AllowedTools; read only when the new key is unset.
    /// Empty here means "not customized" (the effective default list lives on <see cref="McpOptions"/>).</summary>
    public List<string> AllowedTools { get; set; } = [];
}

/// <summary>Settings for <see cref="AssistantProvider.OpenAI"/> mode: the OpenAI platform speaks
/// the same Responses API + MCP tool wire format as Foundry, so this mode reuses that gateway
/// with a different endpoint and credential.</summary>
public sealed class OpenAIOptions
{
    /// <summary>The OpenAI platform API key (sk-...).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The model, which must support the Responses API with the hosted MCP tool.</summary>
    public string Model { get; set; } = "gpt-5-mini";

    /// <summary>The API base; override only for an OpenAI-compatible proxy that supports the
    /// Responses API and its MCP tool.</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com";
}

/// <summary>Settings for <see cref="AssistantProvider.Anthropic"/> mode: the Claude Messages API
/// with the MCP connector calling the same SQLFlow MCP server.</summary>
public sealed class AnthropicOptions
{
    /// <summary>The Anthropic API key (sk-ant-...).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The Claude model id.</summary>
    public string Model { get; set; } = "claude-opus-4-8";

    /// <summary>Per-response output-token ceiling. 16000 keeps non-streaming requests inside SDK
    /// HTTP timeouts while leaving ample room for a thorough Slack answer.</summary>
    public int MaxTokens { get; set; } = 16_000;
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
