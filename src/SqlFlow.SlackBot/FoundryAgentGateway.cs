using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlFlow.Azure;

namespace SqlFlow.SlackBot;

/// <summary>One prior message of a Slack thread, replayed when a Foundry conversation must be rebuilt.</summary>
/// <param name="FromBot">True when the message was posted by this bot (an assistant turn).</param>
/// <param name="Text">The message text as Slack delivered it.</param>
public readonly record struct ConversationTurn(bool FromBot, string Text);

/// <summary>
/// The bridge to Azure AI Foundry, over the OpenAI Responses API. Each question is one
/// <c>POST /openai/responses</c> carrying the model, the assistant instructions, the MCP tool (the
/// deployed SQLFlow MCP server, with the read-scoped access token as its Authorization header and the
/// read-only tool allowlist), and either the new turn plus a <c>previous_response_id</c> that chains
/// the Slack thread server-side, or - when that link is lost (bot restart, server-side expiry) - the
/// Slack transcript replayed as the input. The Responses API supersedes the persistent-agents API for
/// MCP tools: current model deployments (for example gpt-5-mini) support the MCP tool only through
/// this surface. Authentication is the shared Azure credential (managed identity in the container),
/// which needs the Cognitive Services OpenAI User role on the Foundry account.
/// </summary>
public sealed class FoundryAgentGateway : IDisposable
{
    private const string ApiVersion = "2025-04-01-preview";
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly SlackBotOptions _options;
    private readonly ILogger<FoundryAgentGateway> _logger;
    private readonly Uri _endpoint;
    private readonly string _instructions;

    /// <summary>Slack "channel:threadTs" to the id of the last Responses API response in that thread, so a
    /// follow-up chains server-side via previous_response_id. Bounded; an evicted key is rebuilt from the
    /// Slack transcript (the durable record), so eviction costs a little latency, never context.</summary>
    private readonly ConcurrentDictionary<string, string> _threadResponses = new(StringComparer.Ordinal);
    private const int MaxCachedThreads = 2000;

    public FoundryAgentGateway(
        IAzureCredentialFactory credentialFactory,
        IOptions<SlackBotOptions> options,
        ILogger<FoundryAgentGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(credentialFactory);
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
        _credential = credentialFactory.Create();
        _endpoint = BuildResponsesEndpoint(_options.Foundry.ProjectEndpoint);
        _instructions = BuildInstructions();
        // The Responses API returns only when the run completes (synchronous, non-background), so the
        // client timeout is the run ceiling plus headroom for the request itself.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(_options.RunTimeoutSeconds + 15) };
    }

    /// <summary>
    /// Answers one question in the context of a Slack thread. <paramref name="priorTurns"/> is the
    /// thread's transcript excluding the new question; it is only consumed when the thread has no
    /// cached response to chain from and one has to be rebuilt.
    /// </summary>
    public async Task<string> AskAsync(
        string channel,
        string threadTs,
        IReadOnlyList<ConversationTurn> priorTurns,
        string question,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(priorTurns);
        var key = $"{channel}:{threadTs}";

        // A cached previous-response id chains the conversation server-side: send only the new turn.
        if (_threadResponses.TryGetValue(key, out var previousId))
        {
            try
            {
                return await RunAsync(key, previousId, [new ConversationTurn(false, question)], ct).ConfigureAwait(false);
            }
            catch (ResponseLinkLostException)
            {
                // The stored response was pruned server-side (retention). Drop the mapping and rebuild
                // the context once from the Slack transcript below.
                _logger.LogWarning("Previous response {Id} for {Key} is gone; rebuilding from the Slack transcript", previousId, key);
                _threadResponses.TryRemove(key, out _);
            }
        }

        // Cold thread (or a lost link): seed the input with the replayed transcript plus the new question.
        var seeded = new List<ConversationTurn>(priorTurns.Count + 1);
        seeded.AddRange(priorTurns.TakeLast(_options.MaxReplayMessages));
        seeded.Add(new ConversationTurn(false, question));
        return await RunAsync(key, previousResponseId: null, seeded, ct).ConfigureAwait(false);
    }

    private async Task<string> RunAsync(
        string key,
        string? previousResponseId,
        IReadOnlyList<ConversationTurn> turns,
        CancellationToken ct)
    {
        var body = BuildRequestBody(previousResponseId, turns);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // A chained request whose previous_response_id no longer exists comes back 404 (or 400 naming
        // that id); either way the fix is to rebuild from the transcript, which the caller does.
        if (previousResponseId is not null && (response.StatusCode == HttpStatusCode.NotFound
            || (response.StatusCode == HttpStatusCode.BadRequest && payload.Contains(previousResponseId, StringComparison.Ordinal))))
        {
            throw new ResponseLinkLostException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Foundry Responses API returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(payload)}");
        }

        return ParseAnswer(key, payload);
    }

    private string BuildRequestBody(string? previousResponseId, IReadOnlyList<ConversationTurn> turns)
    {
        var input = new JsonArray();
        foreach (var turn in turns)
        {
            if (string.IsNullOrWhiteSpace(turn.Text))
            {
                continue;
            }
            input.Add(new JsonObject
            {
                ["role"] = turn.FromBot ? "assistant" : "user",
                ["content"] = turn.Text,
            });
        }

        var mcpTool = new JsonObject
        {
            ["type"] = "mcp",
            ["server_label"] = _options.Foundry.McpServerLabel,
            ["server_url"] = _options.Foundry.McpServerUrl,
            // The bot shares one identity across a channel, so writes stay off this path; approval is
            // off because there is no human in the loop to approve a tool call mid-run.
            ["require_approval"] = "never",
            ["headers"] = new JsonObject
            {
                // The MCP server forwards this verbatim to the control plane, which enforces the token's
                // scopes; a read-scoped token is the bot's whole authority.
                ["Authorization"] = "Bearer " + _options.SqlFlow.AccessToken,
            },
        };
        if (_options.Foundry.AllowedTools.Count > 0)
        {
            var allowed = new JsonArray();
            foreach (var tool in _options.Foundry.AllowedTools)
            {
                allowed.Add(tool);
            }
            mcpTool["allowed_tools"] = allowed;
        }

        var body = new JsonObject
        {
            ["model"] = _options.Foundry.ModelDeploymentName,
            ["instructions"] = _instructions,
            ["input"] = input,
            ["tools"] = new JsonArray { mcpTool },
            // Persist the response so a follow-up can chain from it via previous_response_id.
            ["store"] = true,
        };
        if (previousResponseId is not null)
        {
            body["previous_response_id"] = previousResponseId;
        }
        return body.ToJsonString();
    }

    private string ParseAnswer(string key, string payload)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(payload)?.AsObject()
                ?? throw new InvalidOperationException("Foundry response body was not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Foundry response was not valid JSON: {Truncate(payload)}", ex);
        }

        var status = (string?)root["status"];
        if (!string.Equals(status, "completed", StringComparison.Ordinal))
        {
            // 'incomplete' carries incomplete_details (e.g. a token cap); a hard failure carries error.
            var detail = root["error"]?.ToJsonString() ?? root["incomplete_details"]?.ToJsonString() ?? "no detail";
            throw new InvalidOperationException($"Foundry response ended as '{status ?? "unknown"}' ({Truncate(detail)}).");
        }

        if ((string?)root["id"] is { Length: > 0 } id)
        {
            RememberResponse(key, id);
        }

        var answer = new StringBuilder();
        if (root["output"] is JsonArray output)
        {
            foreach (var item in output)
            {
                if (item is not JsonObject message || (string?)message["type"] != "message"
                    || message["content"] is not JsonArray content)
                {
                    continue;
                }
                foreach (var part in content)
                {
                    if (part is JsonObject textPart
                        && (string?)textPart["type"] == "output_text"
                        && (string?)textPart["text"] is { Length: > 0 } text)
                    {
                        if (answer.Length > 0)
                        {
                            answer.AppendLine();
                        }
                        answer.Append(text);
                    }
                }
            }
        }

        if (answer.Length == 0)
        {
            throw new InvalidOperationException(
                $"Foundry response {(string?)root["id"] ?? "(no id)"} completed but produced no text answer.");
        }
        return answer.ToString();
    }

    private void RememberResponse(string key, string responseId)
    {
        _threadResponses[key] = responseId;
        // Bounded cache: past the cap, drop an arbitrary batch. Evicted threads are rebuilt from the
        // Slack transcript on next use, so eviction costs a little latency, never context.
        if (_threadResponses.Count > MaxCachedThreads)
        {
            foreach (var stale in _threadResponses.Keys.Take(MaxCachedThreads / 10))
            {
                _threadResponses.TryRemove(stale, out _);
            }
        }
    }

    /// <summary>
    /// Builds the Azure OpenAI Responses endpoint from the Foundry project endpoint: the account is the
    /// first host label of <c>https://&lt;account&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</c>,
    /// and the Responses API lives at <c>https://&lt;account&gt;.openai.azure.com/openai/responses</c>.
    /// </summary>
    private static Uri BuildResponsesEndpoint(string projectEndpoint)
    {
        if (!Uri.TryCreate(projectEndpoint, UriKind.Absolute, out var parsed))
        {
            throw new InvalidOperationException(
                $"SlackBot:Foundry:ProjectEndpoint '{projectEndpoint}' is not an absolute URL.");
        }
        var account = parsed.Host.Split('.', 2)[0];
        if (account.Length == 0)
        {
            throw new InvalidOperationException(
                $"Could not read the Foundry account name from ProjectEndpoint '{projectEndpoint}'.");
        }
        return new Uri($"https://{account}.openai.azure.com/openai/responses?api-version={ApiVersion}");
    }

    private static string Truncate(string value)
        => value.Length <= 600 ? value : value[..600] + "...";

    public void Dispose() => _http.Dispose();

    /// <summary>Raised when a chained request's previous_response_id no longer exists server-side, so the
    /// caller rebuilds the conversation from the Slack transcript.</summary>
    private sealed class ResponseLinkLostException : Exception;

    private string BuildInstructions()
    {
        var gui = _options.SqlFlow.GuiBaseUrl.TrimEnd('/');
        var linkGuidance = gui.Length > 0
            ? $"""
               When you reference a run, link it as <{gui}/runs/RUN_ID|the run>; a pipeline as
               <{gui}/pipelines/PIPELINE_ID|the pipeline>. Use real ids from tool results.
               """
            : "Reference runs and pipelines by their names and ids from tool results.";

        return $"""
            You are the SQLFlow assistant in Slack. SQLFlow is a data-integration platform: T-SQL
            against SQL Server, orchestrated by .flow.yaml documents, with a control plane that
            tracks repos, pipelines (flows), runs, lineage, schedules, and worker nodes.

            Answer questions using your SQLFlow tools; never invent catalog state. For any question
            about product behavior, CLI commands, or .flow.yaml keys, search the docs tools first
            and ground the answer in them. For operational questions (what failed, what ran, what a
            table contains, where data flows), query the live tools: summary and list_runs for
            status, get_run plus run_statements/run_assertions for diagnosing one run,
            describe_object for a specific table or view, list_schemas and lineage_objects to
            browse, the search tools when only a name fragment is known.

            You have read-only access. If asked to trigger, cancel, or change anything, explain
            that this Slack assistant is read-only and point to the SQLFlow GUI or CLI.

            You are talking in Slack: format for Slack mrkdwn. *bold* for emphasis (never
            double-asterisk), bullet lists with the - character, `inline code` for object and flow
            names, code blocks only for SQL or YAML. Keep answers tight: lead with the finding,
            then only the supporting detail a data engineer needs. {linkGuidance}

            SQLFlow is a distinct product from DeltaForge; your tools and their docs corpus are the
            only source of truth.
            """;
    }
}
