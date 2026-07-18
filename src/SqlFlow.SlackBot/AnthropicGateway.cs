using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SqlFlow.SlackBot;

/// <summary>
/// The bridge to the Anthropic Claude API, via the Messages API's MCP connector: each question is
/// one (or, after a <c>pause_turn</c>, a few chained) <c>POST /v1/messages</c> calls carrying the
/// model, the shared assistant instructions as the system prompt, and the SQLFlow MCP server as an
/// <c>mcp_servers</c> entry whose tools Claude calls server-side. The read-scoped SQLFlow access
/// token rides as the connector's authorization token, and the read-only allowlist is enforced
/// through the <c>mcp_toolset</c> configuration. Anthropic keeps no server-side conversation
/// state, so every call sends the Slack-thread transcript (already capped by MaxReplayMessages);
/// the Slack thread stays the single durable record, which also means a bot restart loses nothing.
/// </summary>
public sealed class AnthropicGateway : IAssistantGateway, IDisposable
{
    private const string McpBeta = "mcp-client-2025-11-20";

    /// <summary>The server-side MCP tool loop pauses after its iteration cap; each resume re-sends the
    /// conversation with the paused turn appended. Five resumes is far beyond any real SQLFlow question
    /// and bounds a pathological loop.</summary>
    private const int MaxPauseResumes = 5;

    private readonly AnthropicClient _client;
    private readonly SlackBotOptions _options;
    private readonly ILogger<AnthropicGateway> _logger;
    private readonly string _instructions;

    public AnthropicGateway(IOptions<SlackBotOptions> options, ILogger<AnthropicGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
        _instructions = AssistantInstructions.Build(_options);
        _client = new AnthropicClient
        {
            ApiKey = _options.Anthropic.ApiKey,
            // One question is one long-running call (the MCP tool loop runs inside it), so the
            // request timeout is the run ceiling plus headroom. The SDK retries 429/5xx itself.
            Timeout = TimeSpan.FromSeconds(_options.RunTimeoutSeconds + 15),
        };
    }

    /// <inheritdoc />
    public async Task<string> AskAsync(
        string channel,
        string threadTs,
        IReadOnlyList<ConversationTurn> priorTurns,
        string question,
        IReadOnlyList<string> imageDataUris,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(priorTurns);
        ArgumentNullException.ThrowIfNull(imageDataUris);

        var messages = BuildMessages(priorTurns, question, imageDataUris);

        for (var resumes = 0; ; resumes++)
        {
            var response = await _client.Beta.Messages.Create(
                BuildParams(messages), cancellationToken: ct).ConfigureAwait(false);

            var stopReason = response.StopReason?.ToString() ?? "";
            if (string.Equals(stopReason, "pause_turn", StringComparison.OrdinalIgnoreCase))
            {
                if (resumes >= MaxPauseResumes)
                {
                    throw new InvalidOperationException(
                        $"Anthropic run for {channel}:{threadTs} was still paused after {MaxPauseResumes} resumes; giving up.");
                }
                // The server-side MCP loop hit its iteration cap mid-turn. Echo the paused assistant
                // turn back verbatim and re-send: the API detects the trailing server-tool state and
                // resumes where it left off. The blocks are echoed as raw JSON deliberately, because a
                // resume must return them byte-faithfully, not rebuilt through typed variants.
                _logger.LogInformation("Anthropic paused the tool loop for {Channel}:{ThreadTs}; resuming ({Resume}/{Max})",
                    channel, threadTs, resumes + 1, MaxPauseResumes);
                messages.Add(new BetaMessageParam
                {
                    Role = Role.Assistant,
                    Content = response.Content.Select(block => new BetaContentBlockParam(block.Json)).ToList(),
                });
                continue;
            }

            if (string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Anthropic declined to answer this question (safety refusal). Rephrase the question or contact an admin if it looks wrong.");
            }

            var answer = ExtractText(response);
            if (answer.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Anthropic response {response.ID} ended as '{stopReason}' but produced no text answer.");
            }
            if (string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
            {
                // A truncated answer is still worth posting; note the cut so the user can ask to continue.
                _logger.LogWarning("Anthropic answer for {Channel}:{ThreadTs} hit the {Max}-token output cap and was truncated",
                    channel, threadTs, _options.Anthropic.MaxTokens);
                answer += "\n_(answer hit the output limit; ask me to continue for the rest)_";
            }
            return answer;
        }
    }

    private MessageCreateParams BuildParams(List<BetaMessageParam> messages)
    {
        // Allowlist mode when tools are configured: everything off by default, each permitted tool
        // enabled explicitly, mirroring the read-only surface the other providers get via allowed_tools.
        var mcpToolset = _options.Mcp.AllowedTools.Count > 0
            ? new BetaMcpToolset
            {
                McpServerName = _options.Mcp.ServerLabel,
                DefaultConfig = new BetaMcpToolDefaultConfig { Enabled = false },
                Configs = _options.Mcp.AllowedTools.ToDictionary(
                    tool => tool,
                    _ => new BetaMcpToolConfig { Enabled = true },
                    StringComparer.Ordinal),
            }
            : new BetaMcpToolset { McpServerName = _options.Mcp.ServerLabel };

        return new MessageCreateParams
        {
            Model = _options.Anthropic.Model,
            MaxTokens = _options.Anthropic.MaxTokens,
            System = _instructions,
            Betas = [McpBeta],
            Thinking = new BetaThinkingConfigAdaptive(),
            McpServers =
            [
                new BetaRequestMcpServerUrlDefinition
                {
                    Name = _options.Mcp.ServerLabel,
                    Url = _options.Mcp.ServerUrl,
                    // Sent by the connector as "Authorization: Bearer <token>"; the MCP server
                    // forwards it verbatim to the control plane, which enforces the token's scopes.
                    AuthorizationToken = _options.SqlFlow.AccessToken,
                },
            ],
            Tools = [new BetaToolUnion(mcpToolset)],
            Messages = messages,
        };
    }

    /// <summary>
    /// The full conversation for one call: the capped Slack transcript, then the new question with
    /// its image attachments. Leading assistant turns are dropped because the Messages API requires
    /// the first message to be a user turn; the last message is always the user question, so the
    /// no-trailing-assistant-prefill rule holds by construction.
    /// </summary>
    private List<BetaMessageParam> BuildMessages(
        IReadOnlyList<ConversationTurn> priorTurns,
        string question,
        IReadOnlyList<string> imageDataUris)
    {
        var messages = new List<BetaMessageParam>();
        foreach (var turn in priorTurns.TakeLast(_options.MaxReplayMessages))
        {
            if (string.IsNullOrWhiteSpace(turn.Text))
            {
                continue;
            }
            if (messages.Count == 0 && turn.FromBot)
            {
                continue;
            }
            messages.Add(new BetaMessageParam
            {
                Role = turn.FromBot ? Role.Assistant : Role.User,
                Content = turn.Text,
            });
        }

        if (imageDataUris.Count == 0)
        {
            messages.Add(new BetaMessageParam { Role = Role.User, Content = question });
            return messages;
        }

        var parts = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = question } };
        foreach (var dataUri in imageDataUris)
        {
            if (TryParseDataUri(dataUri, out var mediaType, out var base64))
            {
                parts.Add(new BetaImageBlockParam
                {
                    Source = new BetaBase64ImageSource { MediaType = mediaType, Data = base64 },
                });
            }
            else
            {
                _logger.LogWarning("Skipping an attachment that is not a base64 image data URI");
            }
        }
        messages.Add(new BetaMessageParam { Role = Role.User, Content = parts });
        return messages;
    }

    /// <summary>Splits a <c>data:&lt;mime&gt;;base64,&lt;payload&gt;</c> URI (the shape the Slack handler
    /// produces) into the media type and payload the Anthropic image source expects.</summary>
    internal static bool TryParseDataUri(string dataUri, out string mediaType, out string base64)
    {
        mediaType = "";
        base64 = "";
        if (!dataUri.StartsWith("data:", StringComparison.Ordinal))
        {
            return false;
        }
        var comma = dataUri.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return false;
        }
        var header = dataUri[5..comma];
        if (!header.EndsWith(";base64", StringComparison.Ordinal))
        {
            return false;
        }
        mediaType = header[..^";base64".Length];
        base64 = dataUri[(comma + 1)..];
        return mediaType.Length > 0 && base64.Length > 0;
    }

    private static string ExtractText(BetaMessage response)
    {
        var answer = new System.Text.StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text) && !string.IsNullOrEmpty(text.Text))
            {
                if (answer.Length > 0)
                {
                    answer.AppendLine();
                }
                answer.Append(text.Text);
            }
        }
        return answer.ToString();
    }

    public void Dispose() => _client.Dispose();
}
