using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Notifications;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// The notification pipeline's engine room: each tick detects new failure events from the run history, turns due
/// subscriptions into composed messages on the durable outbox, and sends what the outbox holds. The three phases
/// are deliberately decoupled through the catalog (events, then deliveries), so every step is claim-based and
/// multi-replica safe, a restart never loses a message, and a channel outage backs up in the outbox instead of
/// dropping alerts.
/// </summary>
/// <remarks>
/// Anti-spam lives in dispatch, not in sending: however many failures a window holds, a subscription produces at
/// most one message per claim, covering everything since its cursor. Immediate subscriptions re-arm after their
/// cooldown; digest subscriptions advance on their fixed interval and skip empty windows entirely. Every phase
/// error is logged (secret-redacted) and confined: one subscription's bad address or one channel's outage never
/// stops the others, and the loop itself never dies.
/// </remarks>
public sealed partial class NotificationService : BackgroundService
{
    private const int MaxSubscriptionsPerTick = 200;

    private const int MaxDeliveriesPerTick = 50;

    // Dispatch and send fan out on their own scopes (a DbContext is not thread-safe) behind a small gate: enough
    // to ride out one slow SMTP conversation without stampeding the catalog or the channel providers.
    private const int MaxConcurrency = 4;

    /// <summary>How often the housekeeping phase (stuck-send recovery, retention pruning) runs.</summary>
    private static readonly TimeSpan HousekeepingInterval = TimeSpan.FromMinutes(15);

    /// <summary>A delivery claimed longer than this is considered orphaned by a dead node and requeued.</summary>
    private static readonly TimeSpan StuckSendingAfter = TimeSpan.FromMinutes(10);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<NotificationService> _logger;
    private readonly NotificationOptions _options;
    private readonly TimeSpan _pollInterval;
    private readonly IReadOnlyDictionary<string, INotificationChannel> _channels;
    private DateTime _lastHousekeepingUtc;

    public NotificationService(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<ControlPlaneOptions> options,
        IEnumerable<INotificationChannel> channels,
        ILogger<NotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _clock = clock;
        _options = options.Value.Notifications;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds));
        _channels = channels.ToDictionary(c => c.Name, StringComparer.Ordinal);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_channels.Count == 0 ? "(none)" : string.Join(", ", _channels.Keys.Order(StringComparer.Ordinal)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown has disposed the DI container and the loggers out from under this tick. No work
                // remains and logging would itself throw (the disposed Windows EventLog provider), so leave the loop
                // quietly instead of escalating shutdown noise into a "BackgroundService failed".
                break;
            }
            catch (Exception ex)
            {
                LogTickError(SecretHygiene.RedactedMessage(ex.Message));
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>One pipeline pass. Each phase carries its own error confinement (a detection outage must not stop
    /// the outbox from draining, and vice versa); only cancellation escapes.</summary>
    private async Task TickAsync(CancellationToken ct)
    {
        await RunPhaseAsync(DetectAsync, "detection", ct).ConfigureAwait(false);
        await RunPhaseAsync(DispatchAsync, "dispatch", ct).ConfigureAwait(false);
        await RunPhaseAsync(SendAsync, "send", ct).ConfigureAwait(false);
        await RunPhaseAsync(HousekeepAsync, "housekeeping", ct).ConfigureAwait(false);
    }

    private async Task RunPhaseAsync(Func<CancellationToken, Task> phase, string name, CancellationToken ct)
    {
        try
        {
            await phase(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogPhaseError(name, SecretHygiene.RedactedMessage(ex.Message));
        }
    }

    /// <summary>Detection runs unconditionally (not only when subscribers exist): the watermark must keep pace
    /// with the run history, so a user opting in later starts from "now" instead of triggering a flood of
    /// back-detected history, and the event table doubles as an audit of what would have been notified.</summary>
    private async Task DetectAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var result = await NotificationStore.DetectAsync(
            catalog, _clock.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(_options.DetectionOverlapMinutes), ct)
            .ConfigureAwait(false);
        if (result.RunEvents > 0 || result.AssertionEvents > 0)
        {
            LogDetected(result.RunEvents, result.AssertionEvents);
        }
    }

    private async Task DispatchAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        long maxEventId;
        IReadOnlyList<CatalogNotificationSubscription> due;
        await using (var scope = _services.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            maxEventId = await NotificationStore.MaxEventIdAsync(catalog, ct).ConfigureAwait(false);
            due = await NotificationStore.ListDueSubscriptionsAsync(catalog, now, maxEventId, MaxSubscriptionsPerTick, ct)
                .ConfigureAwait(false);
        }

        if (due.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        await Task.WhenAll(due.Select(subscription =>
            DispatchGuardedAsync(subscription, maxEventId, now, gate, ct))).ConfigureAwait(false);
    }

    /// <summary>Dispatches one due subscription behind the gate, on its own scope; a failure is logged and
    /// confined to this subscription (the claim it took simply re-arms at its next due time).</summary>
    private async Task DispatchGuardedAsync(
        CatalogNotificationSubscription subscription, long maxEventId, DateTime now, SemaphoreSlim gate,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await DispatchOneAsync(catalog, subscription, maxEventId, now, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogDispatchError(subscription.Id, SecretHygiene.RedactedMessage(ex.Message));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DispatchOneAsync(
        CatalogDbContext catalog, CatalogNotificationSubscription subscription, long maxEventId, DateTime now,
        CancellationToken ct)
    {
        // Claim the window by advancing the next-due instant: immediate re-arms after its cooldown, digest rolls
        // to the end of the next interval (drift-free from the observed value). Losing the compare-and-swap means
        // another replica owns this window.
        var newNextDue = subscription.Mode == NotificationModes.Digest
            ? NextDigestDue(subscription.NextDueUtc, subscription.DigestIntervalMinutes, now)
            : subscription.CooldownMinutes <= 0 ? now : now.AddMinutes(subscription.CooldownMinutes);
        var claimed = await NotificationStore.TryClaimSubscriptionAsync(
            catalog, subscription.Id, subscription.NextDueUtc, newNextDue, now, ct).ConfigureAwait(false);
        if (!claimed)
        {
            return;
        }

        var slice = await NotificationStore.ListEventsAfterAsync(
            catalog, subscription.LastEventId, maxEventId, NotificationComposer.MaxEventsPerMessage + 1, ct)
            .ConfigureAwait(false);
        var truncated = slice.Count > NotificationComposer.MaxEventsPerMessage;
        if (truncated)
        {
            slice.RemoveAt(slice.Count - 1);
        }

        // The cursor advances over everything CONSIDERED (matched or not) so non-matching events never hold a
        // subscription "due" forever; when the slice was truncated it advances only to the last event actually
        // taken, so the remainder flows into the next message instead of being skipped.
        var newLastEventId = truncated ? slice[^1].Id : maxEventId;
        var matched = slice.Where(NotificationSubscriptionFilter.Build(subscription)).ToList();
        if (matched.Count == 0)
        {
            await NotificationStore.CompleteDispatchAsync(
                catalog, subscription.Id, newLastEventId, sentUtc: null, delivery: null, now, ct).ConfigureAwait(false);
            return;
        }

        var message = NotificationComposer.Compose(new NotificationComposition(
            subscription.Channel, subscription.Mode, matched, truncated, _options.GuiBaseUrl, now));
        var target = await NotificationTargetResolver.ResolveAsync(catalog, subscription, ct).ConfigureAwait(false);
        var delivery = new CatalogNotificationDelivery
        {
            Id = Guid.CreateVersion7(),
            SubscriptionId = subscription.Id,
            UserId = subscription.UserId,
            Channel = subscription.Channel,
            Target = target ?? "(unresolved)",
            Subject = message.Subject,
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody,
            SlackBlocksJson = message.SlackBlocksJson,
            EventCount = matched.Count,
            FirstEventId = matched[0].Id,
            LastEventId = matched[^1].Id,
            Status = NotificationDeliveryStatuses.Queued,
            CreatedUtc = now,
        };
        if (target is null)
        {
            // No destination can be resolved (the user has no email and the subscription no override): the
            // message is recorded as failed instead of silently skipped, so the owner sees WHY nothing arrived.
            delivery.Status = NotificationDeliveryStatuses.Failed;
            delivery.LastError = NotificationTargetResolver.UnresolvedReason(subscription.Channel);
            LogNoTarget(subscription.Id, subscription.Channel);
        }

        await NotificationStore.CompleteDispatchAsync(
            catalog, subscription.Id, newLastEventId, sentUtc: now, delivery, now, ct).ConfigureAwait(false);
        LogDispatched(subscription.Id, subscription.Channel, matched.Count, delivery.Id);
    }

    /// <summary>The end of the next digest window: advanced drift-free from the observed due instant (so a 6-hour
    /// digest stays on its rhythm no matter when ticks land), skipping straight past windows missed while the host
    /// was down (they are covered by the cursor anyway, in one combined message, not one message per missed window).</summary>
    internal static DateTime NextDigestDue(DateTime? observedDueUtc, int intervalMinutes, DateTime nowUtc)
    {
        var interval = Math.Max(1, intervalMinutes);
        if (observedDueUtc is not { } observed || observed > nowUtc)
        {
            return nowUtc.AddMinutes(interval);
        }

        var intervalsBehind = (long)Math.Floor((nowUtc - observed).TotalMinutes / interval) + 1;
        var next = observed.AddMinutes(intervalsBehind * interval);
        return next > nowUtc ? next : next.AddMinutes(interval);
    }

    private async Task SendAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        IReadOnlyList<CatalogNotificationDelivery> sendable;
        await using (var scope = _services.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            sendable = await NotificationStore.ListSendableDeliveriesAsync(catalog, now, MaxDeliveriesPerTick, ct)
                .ConfigureAwait(false);
        }

        if (sendable.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        await Task.WhenAll(sendable.Select(delivery => SendGuardedAsync(delivery, gate, ct))).ConfigureAwait(false);
    }

    private async Task SendGuardedAsync(CatalogNotificationDelivery delivery, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await SendOneAsync(catalog, delivery, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mid-send: the row stays 'sending' and the stuck-claim recovery requeues it.
            throw;
        }
        catch (Exception ex)
        {
            LogSendError(delivery.Id, SecretHygiene.RedactedMessage(ex.Message));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SendOneAsync(CatalogDbContext catalog, CatalogNotificationDelivery delivery, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        if (!await NotificationStore.TryClaimDeliverySendAsync(catalog, delivery.Id, now, ct).ConfigureAwait(false))
        {
            return;
        }

        var attempt = delivery.Attempts + 1; // the claim just counted this attempt
        if (!_channels.TryGetValue(delivery.Channel, out var channel))
        {
            await NotificationStore.MarkDeliveryFailedAsync(
                catalog, delivery.Id,
                $"the '{delivery.Channel}' channel is not configured on this control plane; the message could not be sent.",
                ct).ConfigureAwait(false);
            LogChannelUnavailable(delivery.Id, delivery.Channel);
            return;
        }

        try
        {
            await channel.SendAsync(delivery, ct).ConfigureAwait(false);
            await NotificationStore.MarkDeliverySentAsync(catalog, delivery.Id, _clock.GetUtcNow().UtcDateTime, ct)
                .ConfigureAwait(false);
            LogSent(delivery.Id, delivery.Channel, delivery.EventCount, attempt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var retryable = ex is not NotificationSendException { Retryable: false };
            var error = SecretHygiene.RedactedMessage(ex.Message);
            if (!retryable || attempt >= _options.MaxDeliveryAttempts)
            {
                await NotificationStore.MarkDeliveryFailedAsync(catalog, delivery.Id, error, ct).ConfigureAwait(false);
                LogSendFailedPermanently(delivery.Id, delivery.Channel, attempt, error);
            }
            else
            {
                var nextAttempt = _clock.GetUtcNow().UtcDateTime + RetryBackoff(attempt);
                await NotificationStore.RequeueDeliveryAsync(catalog, delivery.Id, error, nextAttempt, ct).ConfigureAwait(false);
                LogSendRetrying(delivery.Id, delivery.Channel, attempt, error);
            }
        }
    }

    /// <summary>The retry ladder: quick for a blip, patient for an outage, and never more than an hour, so a
    /// long channel outage drains promptly once it ends.</summary>
    internal static TimeSpan RetryBackoff(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(60),
    };

    private async Task HousekeepAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        if (now - _lastHousekeepingUtc < HousekeepingInterval)
        {
            return;
        }

        _lastHousekeepingUtc = now;
        await using var scope = _services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var recovered = await NotificationStore.RecoverStuckDeliveriesAsync(catalog, now - StuckSendingAfter, ct)
            .ConfigureAwait(false);
        if (recovered > 0)
        {
            LogRecoveredStuck(recovered);
        }

        var (events, deliveries) = await NotificationStore.PurgeExpiredAsync(
            catalog, now.AddDays(-_options.EventRetentionDays), now.AddDays(-_options.DeliveryRetentionDays), ct)
            .ConfigureAwait(false);
        if (events > 0 || deliveries > 0)
        {
            LogPurged(events, deliveries);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification service started; configured channels: {Channels}.")]
    private partial void LogStarted(string channels);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification detection recorded {RunEvents} run event(s) and {AssertionEvents} assertion event(s).")]
    private partial void LogDetected(int runEvents, int assertionEvents);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription {SubscriptionId} dispatched one {Channel} message covering {EventCount} event(s) as delivery {DeliveryId}.")]
    private partial void LogDispatched(Guid subscriptionId, string channel, int eventCount, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Subscription {SubscriptionId} ({Channel}) has no resolvable destination; the message was recorded as failed.")]
    private partial void LogNoTarget(Guid subscriptionId, string channel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivery {DeliveryId} sent over {Channel} ({EventCount} event(s), attempt {Attempt}).")]
    private partial void LogSent(Guid deliveryId, string channel, int eventCount, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery {DeliveryId} over {Channel} failed on attempt {Attempt} and will be retried: {Error}")]
    private partial void LogSendRetrying(Guid deliveryId, string channel, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {DeliveryId} over {Channel} failed permanently on attempt {Attempt}: {Error}")]
    private partial void LogSendFailedPermanently(Guid deliveryId, string channel, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {DeliveryId} names channel '{Channel}', which is not configured; it was recorded as failed.")]
    private partial void LogChannelUnavailable(Guid deliveryId, string channel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Requeued {Count} notification delivery(ies) stuck in sending (claiming node stopped mid-send).")]
    private partial void LogRecoveredStuck(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification retention pruned {Events} event(s) and {Deliveries} delivery(ies).")]
    private partial void LogPurged(int events, int deliveries);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification {Phase} phase error: {Error}")]
    private partial void LogPhaseError(string phase, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification tick error: {Error}")]
    private partial void LogTickError(string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Subscription {SubscriptionId} dispatch error: {Error}")]
    private partial void LogDispatchError(Guid subscriptionId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {DeliveryId} send error: {Error}")]
    private partial void LogSendError(Guid deliveryId, string error);
}
