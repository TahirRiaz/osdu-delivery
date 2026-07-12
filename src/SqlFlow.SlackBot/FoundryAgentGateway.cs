using System.Collections.Concurrent;
using System.Text;
using Azure.AI.Agents.Persistent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlFlow.Azure;

namespace SqlFlow.SlackBot;

/// <summary>One prior message of a Slack thread, replayed when a Foundry thread must be rebuilt.</summary>
/// <param name="FromBot">True when the message was posted by this bot (an assistant turn).</param>
/// <param name="Text">The message text as Slack delivered it.</param>
public readonly record struct ConversationTurn(bool FromBot, string Text);

/// <summary>
/// The bridge to the Azure AI Foundry agent. Owns three concerns: ensuring the agent definition
/// exists and matches this build (name, model, instructions, MCP tool with its allowlist),
/// mapping Slack threads to Foundry threads (an in-memory cache; on a miss the Foundry thread is
/// rebuilt from the Slack transcript, so a bot restart loses nothing), and executing one run with
/// the SQLFlow access token attached as the MCP Authorization header.
/// </summary>
public sealed class FoundryAgentGateway
{
    private readonly PersistentAgentsClient _client;
    private readonly SlackBotOptions _options;
    private readonly ILogger<FoundryAgentGateway> _logger;

    /// <summary>Slack "channel:threadTs" to Foundry thread. Bounded; see <see cref="RememberThread"/>.</summary>
    private readonly ConcurrentDictionary<string, PersistentAgentThread> _threads = new(StringComparer.Ordinal);
    private const int MaxCachedThreads = 2000;

    /// <summary>Single-flights <see cref="EnsureAgentAsync"/>; the agent once resolved.</summary>
    private readonly SemaphoreSlim _agentLock = new(1, 1);
    private PersistentAgent? _agent;

    public FoundryAgentGateway(
        IAzureCredentialFactory credentialFactory,
        IOptions<SlackBotOptions> options,
        ILogger<FoundryAgentGateway> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new PersistentAgentsClient(_options.Foundry.ProjectEndpoint, credentialFactory.Create());
    }

    /// <summary>
    /// Answers one question in the context of a Slack thread. <paramref name="priorTurns"/> is the
    /// thread's transcript excluding the new question; it is only consumed when no Foundry thread
    /// is cached for the key and one has to be rebuilt.
    /// </summary>
    public async Task<string> AskAsync(
        string channel,
        string threadTs,
        IReadOnlyList<ConversationTurn> priorTurns,
        string question,
        CancellationToken ct)
    {
        var agent = await EnsureAgentAsync(ct).ConfigureAwait(false);
        var key = $"{channel}:{threadTs}";
        var thread = await GetOrCreateThreadAsync(key, priorTurns, ct).ConfigureAwait(false);

        try
        {
            return await RunAsync(agent, thread, question, ct).ConfigureAwait(false);
        }
        catch (global::Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // The cached Foundry thread was deleted server-side (retention, manual cleanup).
            // Drop the mapping and rebuild once from the Slack transcript.
            _logger.LogWarning("Foundry thread {ThreadId} for {Key} is gone (404); rebuilding from the Slack transcript", thread.Id, key);
            _threads.TryRemove(key, out _);
            thread = await GetOrCreateThreadAsync(key, priorTurns, ct).ConfigureAwait(false);
            return await RunAsync(agent, thread, question, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates or converges the hosted agent so the definition in this codebase is the source of
    /// truth: rerunning after changing instructions, the model, or the tool allowlist updates the
    /// hosted agent in place instead of accumulating stale copies.
    /// </summary>
    private async Task<PersistentAgent> EnsureAgentAsync(CancellationToken ct)
    {
        if (_agent is { } cached)
        {
            return cached;
        }
        await _agentLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_agent is { } resolved)
            {
                return resolved;
            }

            var f = _options.Foundry;
            var mcpTool = new MCPToolDefinition(f.McpServerLabel, f.McpServerUrl);
            foreach (var tool in f.AllowedTools)
            {
                mcpTool.AllowedTools.Add(tool);
            }
            var instructions = BuildInstructions();

            PersistentAgent? existing = null;
            await foreach (var agent in _client.Administration.GetAgentsAsync(cancellationToken: ct).ConfigureAwait(false))
            {
                if (string.Equals(agent.Name, f.AgentName, StringComparison.Ordinal))
                {
                    existing = agent;
                    break;
                }
            }

            PersistentAgent ensured;
            if (existing is null)
            {
                ensured = await _client.Administration.CreateAgentAsync(
                    model: f.ModelDeploymentName,
                    name: f.AgentName,
                    instructions: instructions,
                    tools: [mcpTool],
                    cancellationToken: ct).ConfigureAwait(false);
                _logger.LogInformation("Created Foundry agent '{Name}' ({Id}) on deployment '{Model}'", f.AgentName, ensured.Id, f.ModelDeploymentName);
            }
            else
            {
                ensured = await _client.Administration.UpdateAgentAsync(
                    existing.Id,
                    model: f.ModelDeploymentName,
                    name: f.AgentName,
                    instructions: instructions,
                    tools: [mcpTool],
                    cancellationToken: ct).ConfigureAwait(false);
                _logger.LogInformation("Converged Foundry agent '{Name}' ({Id}) on deployment '{Model}'", f.AgentName, ensured.Id, f.ModelDeploymentName);
            }

            _agent = ensured;
            return ensured;
        }
        finally
        {
            _agentLock.Release();
        }
    }

    private async Task<PersistentAgentThread> GetOrCreateThreadAsync(
        string key,
        IReadOnlyList<ConversationTurn> priorTurns,
        CancellationToken ct)
    {
        if (_threads.TryGetValue(key, out var cachedThread))
        {
            return cachedThread;
        }

        // Replay the tail of the Slack transcript so a follow-up after a restart keeps its context.
        var replay = priorTurns
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .TakeLast(_options.MaxReplayMessages)
            .Select(t => new ThreadMessageOptions(t.FromBot ? MessageRole.Agent : MessageRole.User, t.Text))
            .ToList();

        PersistentAgentThread thread = await _client.Threads.CreateThreadAsync(
            messages: replay.Count > 0 ? replay : null,
            cancellationToken: ct).ConfigureAwait(false);

        RememberThread(key, thread);
        return thread;
    }

    private void RememberThread(string key, PersistentAgentThread thread)
    {
        _threads[key] = thread;
        // Bounded cache: past the cap, drop an arbitrary batch. Evicted threads are rebuilt from
        // the Slack transcript on next use, so eviction costs a little latency, never context.
        if (_threads.Count > MaxCachedThreads)
        {
            foreach (var stale in _threads.Keys.Take(MaxCachedThreads / 10))
            {
                _threads.TryRemove(stale, out _);
            }
        }
    }

    private async Task<string> RunAsync(
        PersistentAgent agent,
        PersistentAgentThread thread,
        string question,
        CancellationToken ct)
    {
        await _client.Messages.CreateMessageAsync(thread.Id, MessageRole.User, question, cancellationToken: ct)
            .ConfigureAwait(false);

        var mcpResource = new MCPToolResource(_options.Foundry.McpServerLabel);
        mcpResource.UpdateHeader("Authorization", "Bearer " + _options.SqlFlow.AccessToken);
        mcpResource.RequireApproval = new MCPApproval("never");

        ThreadRun run = await _client.Runs.CreateRunAsync(thread, agent, mcpResource.ToToolResources(), ct)
            .ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(_options.RunTimeoutSeconds);
        var transientPollFailures = 0;
        while (run.Status == RunStatus.Queued || run.Status == RunStatus.InProgress || run.Status == RunStatus.RequiresAction)
        {
            if (run.Status == RunStatus.RequiresAction)
            {
                // MCP approval is configured off and the agent has no function tools, so any
                // required action is an unexpected protocol state: cancel rather than hang until
                // the run expires server-side.
                await _client.Runs.CancelRunAsync(thread.Id, run.Id, ct).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Agent run {run.Id} stopped for an unexpected required action ({run.RequiredAction?.GetType().Name ?? "unknown"}); the run was cancelled.");
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                await _client.Runs.CancelRunAsync(thread.Id, run.Id, ct).ConfigureAwait(false);
                throw new TimeoutException(
                    $"Agent run {run.Id} exceeded {_options.RunTimeoutSeconds}s and was cancelled.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            try
            {
                run = await _client.Runs.GetRunAsync(thread.Id, run.Id, ct).ConfigureAwait(false);
                transientPollFailures = 0;
            }
            catch (global::Azure.RequestFailedException ex) when (IsTransient(ex) && ++transientPollFailures <= MaxTransientPollFailures)
            {
                // A blip while polling must not abandon a run that is still executing; the
                // deadline above still bounds the total wait. Azure.Core has already retried
                // the individual request before this surfaces.
                _logger.LogWarning("Transient failure {Count}/{Max} polling run {RunId}: {Status} {Error}",
                    transientPollFailures, MaxTransientPollFailures, run.Id, ex.Status, ex.ErrorCode ?? ex.Message);
            }
        }

        if (run.Status != RunStatus.Completed)
        {
            var detail = run.LastError is null ? "no error detail" : $"{run.LastError.Code}: {run.LastError.Message}";
            throw new InvalidOperationException($"Agent run {run.Id} ended as {run.Status} ({detail}).");
        }

        return await LatestAnswerAsync(thread.Id, run.Id, ct).ConfigureAwait(false);
    }

    private const int MaxTransientPollFailures = 3;

    /// <summary>Server-side or throttling failures worth riding out while a run is in flight.</summary>
    private static bool IsTransient(global::Azure.RequestFailedException ex)
        => ex.Status is 0 or 408 or 429 or >= 500;

    private async Task<string> LatestAnswerAsync(string threadId, string runId, CancellationToken ct)
    {
        var answer = new StringBuilder();
        await foreach (var message in _client.Messages
                           .GetMessagesAsync(threadId, runId: runId, order: ListSortOrder.Ascending, cancellationToken: ct)
                           .ConfigureAwait(false))
        {
            if (message.Role != MessageRole.Agent)
            {
                continue;
            }
            foreach (var content in message.ContentItems)
            {
                if (content is MessageTextContent text)
                {
                    if (answer.Length > 0)
                    {
                        answer.AppendLine();
                    }
                    answer.Append(text.Text);
                }
            }
        }

        if (answer.Length == 0)
        {
            throw new InvalidOperationException(
                $"Agent run {runId} completed but produced no text answer (thread {threadId}).");
        }
        return answer.ToString();
    }

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
