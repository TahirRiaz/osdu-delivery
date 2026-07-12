using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.Events;
using SlackNet.WebApi;

namespace SqlFlow.SlackBot;

/// <summary>
/// Handles the two ways users reach the bot: an @-mention in a channel (<see cref="AppMention"/>)
/// and a direct message (<see cref="MessageEvent"/> with channel type <c>im</c>). Each question is
/// acknowledged with an eyes reaction, answered by the Foundry agent in the context of its Slack
/// thread, and the answer posted as a threaded reply. Processing is offloaded so the Socket Mode
/// event loop is never blocked by a long agent run.
/// </summary>
public sealed partial class SlackAssistantHandler : IEventHandler<AppMention>, IEventHandler<MessageEvent>
{
    private readonly ISlackApiClient _slack;
    private readonly FoundryAgentGateway _gateway;
    private readonly SlackBotOptions _options;
    private readonly ILogger<SlackAssistantHandler> _logger;

    /// <summary>The bot's own user id, resolved once from auth.test: used to ignore our own
    /// messages and to strip our mention from question text.</summary>
    private readonly Lazy<Task<string>> _selfUserId;

    /// <summary>
    /// Recently handled message keys (channel:ts). A mention in a DM arrives as both an
    /// app_mention and a message event, and Slack redelivers events it thinks were not acked in
    /// time; both must not produce a second answer.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _handled = new(StringComparer.Ordinal);

    /// <summary>Caps concurrent agent runs; a burst of questions queues behind this instead of
    /// stampeding the model deployment. Questions are acknowledged before they queue.</summary>
    private readonly SemaphoreSlim _concurrency;

    [GeneratedRegex("<@[A-Z0-9]+>")]
    private static partial Regex MentionRegex();

    public SlackAssistantHandler(
        ISlackApiClient slack,
        FoundryAgentGateway gateway,
        SlackBotOptions options,
        ILogger<SlackAssistantHandler> logger)
    {
        _slack = slack;
        _gateway = gateway;
        _options = options;
        _logger = logger;
        _concurrency = new SemaphoreSlim(options.MaxConcurrentAnswers, options.MaxConcurrentAnswers);
        _selfUserId = new Lazy<Task<string>>(async () => (await _slack.Auth.Test().ConfigureAwait(false)).UserId);
    }

    public Task Handle(AppMention slackEvent)
    {
        Dispatch(slackEvent.Channel, slackEvent.User, slackEvent.Text, slackEvent.Ts, slackEvent.ThreadTs);
        return Task.CompletedTask;
    }

    public async Task Handle(MessageEvent slackEvent)
    {
        // Only plain user DMs: channel traffic is handled via app_mention, and edits, joins, and
        // bot posts (including our own answers) carry a subtype or a bot message type.
        if (slackEvent.ChannelType != "im" || slackEvent is BotMessage || slackEvent.Subtype is not null)
        {
            return;
        }
        var self = await _selfUserId.Value.ConfigureAwait(false);
        if (slackEvent.User == self || string.IsNullOrEmpty(slackEvent.User))
        {
            return;
        }
        Dispatch(slackEvent.Channel, slackEvent.User, slackEvent.Text, slackEvent.Ts, slackEvent.ThreadTs);
    }

    /// <summary>Dedupes, then runs the full question pipeline on the thread pool. Fire-and-forget
    /// by design: the Socket Mode loop must ack promptly, and every failure path inside ends in a
    /// Slack error reply plus a log line, never an unobserved exception.</summary>
    private void Dispatch(string channel, string user, string text, string ts, string? threadTs)
    {
        var key = $"{channel}:{ts}";
        var now = DateTimeOffset.UtcNow;
        if (!_handled.TryAdd(key, now))
        {
            return;
        }
        PruneHandled(now);

        _ = Task.Run(() => AnswerAsync(channel, user, text, ts, threadTs ?? ts));
    }

    private void PruneHandled(DateTimeOffset now)
    {
        if (_handled.Count <= 500)
        {
            return;
        }
        foreach (var entry in _handled)
        {
            if (now - entry.Value > TimeSpan.FromMinutes(15))
            {
                _handled.TryRemove(entry.Key, out _);
            }
        }
    }

    private async Task AnswerAsync(string channel, string user, string text, string ts, string threadTs)
    {
        var gated = false;
        try
        {
            await AcknowledgeAsync(channel, ts).ConfigureAwait(false);

            var question = MentionRegex().Replace(text ?? "", "").Trim();
            if (question.Length == 0)
            {
                await PostAsync(channel, threadTs,
                    "Ask me anything about SQLFlow: pipeline and run status, why a run failed, " +
                    "what a table contains, lineage, schedules, or how a `.flow.yaml` key works.")
                    .ConfigureAwait(false);
                return;
            }

            var priorTurns = await PriorTurnsAsync(channel, ts, threadTs).ConfigureAwait(false);

            await _concurrency.WaitAsync().ConfigureAwait(false);
            gated = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.RunTimeoutSeconds + 30));
            var answer = await _gateway.AskAsync(channel, threadTs, priorTurns, question, timeout.Token)
                .ConfigureAwait(false);

            await PostAsync(channel, threadTs, SlackMrkdwn.FromMarkdown(answer)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to answer {Channel}:{Ts} from {User}", channel, ts, user);
            var reason = ex is TimeoutException
                ? "the answer took too long and was cancelled"
                : "something went wrong while answering";
            try
            {
                await PostAsync(channel, threadTs, $":warning: Sorry, {reason}. Please try again; if it keeps failing, ask an admin to check the SQLFlow Slack bot logs.")
                    .ConfigureAwait(false);
            }
            catch (Exception postEx)
            {
                _logger.LogError(postEx, "Could not post the error reply to {Channel}:{Ts}", channel, threadTs);
            }
        }
        finally
        {
            if (gated)
            {
                _concurrency.Release();
            }
        }
    }

    /// <summary>Best-effort eyes reaction so the user sees the question was picked up. A missing
    /// reactions:write scope or a duplicate reaction must never fail the answer.</summary>
    private async Task AcknowledgeAsync(string channel, string ts)
    {
        try
        {
            await _slack.Reactions.AddToMessage("eyes", channel, ts).ConfigureAwait(false);
        }
        catch (SlackException ex)
        {
            _logger.LogDebug("Could not add the acknowledgement reaction in {Channel}: {Error}", channel, ex.Message);
        }
    }

    /// <summary>
    /// The Slack-thread transcript before the current message, oldest first, for rebuilding a
    /// Foundry thread after a restart. A fresh (non-thread) question has no prior turns; for a
    /// follow-up the thread replies are fetched from Slack, which stays the durable transcript.
    /// </summary>
    private async Task<IReadOnlyList<ConversationTurn>> PriorTurnsAsync(string channel, string ts, string threadTs)
    {
        if (threadTs == ts)
        {
            return [];
        }
        var self = await _selfUserId.Value.ConfigureAwait(false);

        // conversations.replies pages oldest-first (default page size is only 10); walk the
        // cursor so long threads are seen in full, capped well past what is ever replayed.
        const int maxFetched = 500;
        var messages = new List<SlackNet.Events.MessageEvent>();
        string? cursor = null;
        do
        {
            var page = await _slack.Conversations
                .Replies(channel, threadTs, limit: 200, cursor: cursor)
                .ConfigureAwait(false);
            messages.AddRange(page.Messages);
            cursor = page.HasMore ? page.ResponseMetadata?.NextCursor : null;
        }
        while (!string.IsNullOrEmpty(cursor) && messages.Count < maxFetched);

        return messages
            .Where(m => m.Ts != ts && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => new ConversationTurn(
                m is BotMessage || m.User == self,
                MentionRegex().Replace(m.Text, "").Trim()))
            .Where(t => t.Text.Length > 0)
            .ToList();
    }

    /// <summary>Posts with a short retry on Slack rate limiting (chat.postMessage is limited to
    /// roughly one message per second per channel); other Slack errors surface immediately.</summary>
    private async Task PostAsync(string channel, string threadTs, string text)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _slack.Chat.PostMessage(new Message
                {
                    Channel = channel,
                    ThreadTs = threadTs,
                    Text = text,
                    UnfurlLinks = false,
                }).ConfigureAwait(false);
                return;
            }
            catch (SlackException ex) when (attempt < maxAttempts && ex.ErrorCode is "ratelimited" or "rate_limited")
            {
                _logger.LogWarning("Rate limited posting to {Channel}; retrying (attempt {Attempt}/{Max})", channel, attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt)).ConfigureAwait(false);
            }
        }
    }
}
