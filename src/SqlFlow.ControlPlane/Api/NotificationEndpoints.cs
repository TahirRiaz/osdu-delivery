using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Notifications;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The caller's own notification surface, mapped under the <c>read</c> scope like the token self-service: any
/// authenticated user (with a backing account) manages their own opt-ins, never another user's. Subscriptions
/// choose a channel (email / Slack), a pacing (immediate with a cooldown, or a digest interval), the event kinds,
/// and an optional flow filter; the deliveries list answers "did it actually go out"; the test send proves the
/// channel and destination work before the first real failure does.
/// </summary>
public static partial class NotificationEndpoints
{
    /// <summary>The most opt-ins one user may hold: far beyond real use, close enough to stop a runaway client
    /// from turning the dispatch scan into their personal workload.</summary>
    private const int MaxSubscriptionsPerUser = 20;

    private const int MinDigestIntervalMinutes = 5;

    private const int MaxDigestIntervalMinutes = 10080; // one week

    private const int MaxCooldownMinutes = 1440; // one day

    private const int MaxFlowPatternLength = 400;

    private const int MaxEmailLength = 320;

    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/me/notifications/options", GetOptionsAsync)
            .WithTags("Notifications").WithName("GetMyNotificationOptions");
        group.MapGet("/me/notifications/subscriptions", ListSubscriptionsAsync)
            .WithTags("Notifications").WithName("ListMyNotificationSubscriptions");
        group.MapPost("/me/notifications/subscriptions", CreateSubscriptionAsync)
            .WithTags("Notifications").WithName("CreateMyNotificationSubscription");
        group.MapPut("/me/notifications/subscriptions/{id:guid}", UpdateSubscriptionAsync)
            .WithTags("Notifications").WithName("UpdateMyNotificationSubscription");
        group.MapDelete("/me/notifications/subscriptions/{id:guid}", DeleteSubscriptionAsync)
            .WithTags("Notifications").WithName("DeleteMyNotificationSubscription");
        group.MapPost("/me/notifications/subscriptions/{id:guid}/test", TestSubscriptionAsync)
            .WithTags("Notifications").WithName("TestMyNotificationSubscription");
        group.MapGet("/me/notifications/deliveries", ListDeliveriesAsync)
            .WithTags("Notifications").WithName("ListMyNotificationDeliveries");

        return group;
    }

    private static async Task<Results<Ok<NotificationOptionsDto>, ProblemHttpResult>> GetOptionsAsync(
        CatalogDbContext catalog, IOptions<ControlPlaneOptions> options, HttpContext httpContext, CancellationToken ct)
    {
        if (!TryGetUserId(httpContext.User, out var userId))
        {
            return NoUserAccount();
        }

        var notifications = options.Value.Notifications;
        var userEmail = await catalog.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok(new NotificationOptionsDto(
            notifications.Enabled,
            new NotificationChannelAvailabilityDto(
                notifications.Enabled && notifications.EmailConfigured,
                notifications.EmailConfigured ? notifications.Email.Provider.ToLowerInvariant() : null),
            new NotificationChannelAvailabilityDto(notifications.Enabled && notifications.SlackConfigured, null),
            NotificationEventKinds.All.ToList(),
            SplitKinds(NotificationEventKinds.DefaultKinds),
            [NotificationModes.Immediate, NotificationModes.Digest],
            userEmail,
            DefaultDigestIntervalMinutes: 360,
            DefaultCooldownMinutes: 5));
    }

    private static async Task<Results<Ok<IReadOnlyList<NotificationSubscriptionDto>>, ProblemHttpResult>> ListSubscriptionsAsync(
        CatalogDbContext catalog, HttpContext httpContext, CancellationToken ct)
    {
        if (!TryGetUserId(httpContext.User, out var userId))
        {
            return NoUserAccount();
        }

        var subscriptions = await NotificationStore.ListSubscriptionsForUserAsync(catalog, userId, ct).ConfigureAwait(false);
        IReadOnlyList<NotificationSubscriptionDto> dto = subscriptions.Select(ToDto).ToList();
        return TypedResults.Ok(dto);
    }

    private static async Task<Results<Created<NotificationSubscriptionDto>, ProblemHttpResult>> CreateSubscriptionAsync(
        CreateNotificationSubscriptionRequest request, CatalogDbContext catalog, IOptions<ControlPlaneOptions> options,
        TimeProvider clock, HttpContext httpContext, CancellationToken ct)
    {
        if (!TryGetUserId(httpContext.User, out var userId))
        {
            return NoUserAccount();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Channel))
        {
            return Invalid("A channel is required", "Choose 'email' or 'slack'.");
        }

        var channel = request.Channel.Trim().ToLowerInvariant();
        if (!NotificationChannels.IsKnown(channel))
        {
            return Invalid("Unknown channel", "Choose 'email' or 'slack'.");
        }

        if (ChannelUnavailable(options.Value.Notifications, channel) is { } unavailable)
        {
            return unavailable;
        }

        var existing = await catalog.NotificationSubscriptions.AsNoTracking()
            .CountAsync(s => s.UserId == userId, ct).ConfigureAwait(false);
        if (existing >= MaxSubscriptionsPerUser)
        {
            return Invalid("Subscription limit reached",
                $"You already hold {existing} subscriptions (the limit is {MaxSubscriptionsPerUser}); delete one first.");
        }

        var mode = (request.Mode ?? NotificationModes.Immediate).Trim().ToLowerInvariant();
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var subscription = new CatalogNotificationSubscription
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Channel = channel,
            Mode = mode,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
        };

        var problem = await ApplySettingsAsync(
            catalog, subscription, userId, mode,
            request.Kinds, request.FlowPattern, request.EmailAddress, request.SlackTarget,
            request.DigestIntervalMinutes ?? 360, request.CooldownMinutes ?? 5, ct).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        subscription.NextDueUtc = mode == NotificationModes.Digest
            ? nowUtc.AddMinutes(subscription.DigestIntervalMinutes)
            : null;

        await NotificationStore.CreateSubscriptionAsync(catalog, subscription, ct).ConfigureAwait(false);
        return TypedResults.Created($"/api/v1/me/notifications/subscriptions/{subscription.Id}", ToDto(subscription));
    }

    private static async Task<Results<Ok<NotificationSubscriptionDto>, ProblemHttpResult>> UpdateSubscriptionAsync(
        Guid id, UpdateNotificationSubscriptionRequest request, CatalogDbContext catalog,
        IOptions<ControlPlaneOptions> options, TimeProvider clock, HttpContext httpContext, CancellationToken ct)
    {
        if (!TryGetUserId(httpContext.User, out var userId))
        {
            return NoUserAccount();
        }

        if (request is null)
        {
            return Invalid("A request body is required", "Send the fields to change; null keeps the current value.");
        }

        var subscription = await NotificationStore.GetSubscriptionForUserAsync(catalog, userId, id, ct).ConfigureAwait(false);
        if (subscription is null)
        {
            return NotFound();
        }

        var mode = (request.Mode ?? subscription.Mode).Trim().ToLowerInvariant();
        var wasEnabled = subscription.Enabled;
        var previousMode = subscription.Mode;
        var previousInterval = subscription.DigestIntervalMinutes;

        subscription.Mode = mode;
        subscription.Enabled = request.Enabled ?? subscription.Enabled;

        var problem = await ApplySettingsAsync(
            catalog, subscription, userId, mode,
            request.Kinds ?? SplitKinds(subscription.Kinds),
            Coalesce(request.FlowPattern, subscription.FlowPattern),
            Coalesce(request.EmailAddress, subscription.EmailAddress),
            Coalesce(request.SlackTarget, subscription.SlackTarget),
            request.DigestIntervalMinutes ?? subscription.DigestIntervalMinutes,
            request.CooldownMinutes ?? subscription.CooldownMinutes, ct).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        subscription.UpdatedUtc = nowUtc;

        // Re-arm the pacing when it changed (or the subscription came back to life): a digest starts a fresh
        // window from now, an immediate subscription is due as soon as an event exists.
        var reEnabled = !wasEnabled && subscription.Enabled;
        var pacingChanged = previousMode != subscription.Mode
            || (subscription.Mode == NotificationModes.Digest && previousInterval != subscription.DigestIntervalMinutes);
        if (reEnabled || pacingChanged)
        {
            subscription.NextDueUtc = subscription.Mode == NotificationModes.Digest
                ? nowUtc.AddMinutes(subscription.DigestIntervalMinutes)
                : null;
        }

        await NotificationStore.SaveSubscriptionAsync(catalog, subscription, fastForwardCursor: reEnabled, ct)
            .ConfigureAwait(false);
        return TypedResults.Ok(ToDto(subscription));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteSubscriptionAsync(
        Guid id, CatalogDbContext catalog, HttpContext httpContext, CancellationToken ct)
    {
        if (!TryGetUserId(httpContext.User, out var userId))
        {
            return NoUserAccount();
        }

        var deleted = await NotificationStore.DeleteSubscriptionForUserAsync(catalog, userId, id, ct).ConfigureAwait(false);
        return deleted ? TypedResults.NoContent() : NotFound();
    }

    /// <summary>Enqueues a test message onto the real outbox for this subscription's channel and destination, so
    /// a green test is a true rehearsal (composition, resolution, credentials, transport) and its outcome shows up
    /// in the deliveries history like any other message. Works while disabled: that is exactly when you test.</summary>
    private static async Task<Results<Accepted<NotificationTestSendDto>, ProblemHttpResult>> TestSubscriptionAsync(
        Guid id, CatalogDbContext catalog, IOptions<ControlPlaneOptions> options, TimeProvider clock,
        HttpContext httpContext, CancellationToken ct)
    {
        if (!TryGetUserId(httpContext.User, out var userId))
        {
            return NoUserAccount();
        }

        var subscription = await NotificationStore.GetSubscriptionForUserAsync(catalog, userId, id, ct).ConfigureAwait(false);
        if (subscription is null)
        {
            return NotFound();
        }

        var notifications = options.Value.Notifications;
        if (ChannelUnavailable(notifications, subscription.Channel) is { } unavailable)
        {
            return unavailable;
        }

        var target = await NotificationTargetResolver.ResolveAsync(catalog, subscription, ct).ConfigureAwait(false);
        if (target is null)
        {
            return Invalid("No destination", NotificationTargetResolver.UnresolvedReason(subscription.Channel));
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var message = NotificationComposer.ComposeTest(subscription.Channel, notifications.GuiBaseUrl, nowUtc);
        var delivery = new CatalogNotificationDelivery
        {
            Id = Guid.CreateVersion7(),
            SubscriptionId = subscription.Id,
            UserId = userId,
            Channel = subscription.Channel,
            Target = target,
            Subject = message.Subject,
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody,
            SlackBlocksJson = message.SlackBlocksJson,
            EventCount = 0,
            FirstEventId = 0,
            LastEventId = 0,
            Status = NotificationDeliveryStatuses.Queued,
            CreatedUtc = nowUtc,
        };
        await NotificationStore.EnqueueDeliveryAsync(catalog, delivery, ct).ConfigureAwait(false);
        return TypedResults.Accepted(
            $"/api/v1/me/notifications/deliveries", new NotificationTestSendDto(delivery.Id));
    }

    private static async Task<Results<Ok<IReadOnlyList<NotificationDeliveryDto>>, ProblemHttpResult>> ListDeliveriesAsync(
        CatalogDbContext catalog, HttpContext httpContext, CancellationToken ct, int take = 50)
    {
        if (!TryGetUserId(httpContext.User, out var userId))
        {
            return NoUserAccount();
        }

        var deliveries = await NotificationStore.ListDeliveriesForUserAsync(catalog, userId, take, ct).ConfigureAwait(false);
        IReadOnlyList<NotificationDeliveryDto> dto = deliveries.Select(d => new NotificationDeliveryDto(
            d.Id, d.SubscriptionId, d.Channel, d.Target, d.Subject, d.Status, d.Attempts, d.EventCount,
            d.LastError, d.CreatedUtc, d.SentUtc)).ToList();
        return TypedResults.Ok(dto);
    }

    /// <summary>Validates and applies the shared mutable settings onto the subscription; returns the problem to
    /// answer with, or null when everything landed. One method serves create and update, so the two paths can
    /// never drift apart in what they accept.</summary>
    private static async Task<ProblemHttpResult?> ApplySettingsAsync(
        CatalogDbContext catalog, CatalogNotificationSubscription subscription, Guid userId, string mode,
        IReadOnlyList<string>? kinds, string? flowPattern, string? emailAddress, string? slackTarget,
        int digestIntervalMinutes, int cooldownMinutes, CancellationToken ct)
    {
        if (!NotificationModes.IsKnown(mode))
        {
            return Invalid("Unknown mode", "Choose 'immediate' or 'digest'.");
        }

        var normalizedKinds = (kinds is { Count: > 0 } ? kinds : SplitKinds(NotificationEventKinds.DefaultKinds))
            .Select(k => k?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedKinds.Count == 0)
        {
            return Invalid("No event kinds", "Subscribe to at least one event kind.");
        }

        var unknown = normalizedKinds.FirstOrDefault(k => !NotificationEventKinds.IsKnown(k));
        if (unknown is not null)
        {
            return Invalid("Unknown event kind",
                $"'{unknown}' is not a known kind. Valid kinds: {string.Join(", ", NotificationEventKinds.All)}.");
        }

        flowPattern = Normalize(flowPattern);
        if (flowPattern is not null)
        {
            if (flowPattern.Length > MaxFlowPatternLength)
            {
                return Invalid("Flow pattern too long", $"A flow pattern may be at most {MaxFlowPatternLength} characters.");
            }

            try
            {
                NotificationSubscriptionFilter.BuildFlowPattern(flowPattern);
            }
            catch (ArgumentException)
            {
                return Invalid("Invalid flow pattern",
                    "Give one or more comma-separated wildcard patterns, e.g. 'sales_*, finance_??_load'.");
            }
        }

        emailAddress = Normalize(emailAddress);
        if (emailAddress is not null)
        {
            if (emailAddress.Length > MaxEmailLength || !System.Net.Mail.MailAddress.TryCreate(emailAddress, out _))
            {
                return Invalid("Invalid email address", $"'{emailAddress}' is not a valid email address.");
            }
        }

        slackTarget = Normalize(slackTarget);
        if (slackTarget is not null && !SlackTargetPattern().IsMatch(slackTarget))
        {
            return Invalid("Invalid Slack target",
                "Give a Slack conversation id (for example C0123ABCD), or leave it empty to be direct-messaged.");
        }

        if (digestIntervalMinutes is < MinDigestIntervalMinutes or > MaxDigestIntervalMinutes)
        {
            return Invalid("Invalid digest interval",
                $"The digest interval must be between {MinDigestIntervalMinutes} minutes and one week ({MaxDigestIntervalMinutes} minutes).");
        }

        if (cooldownMinutes is < 0 or > MaxCooldownMinutes)
        {
            return Invalid("Invalid cooldown",
                $"The cooldown must be between 0 and {MaxCooldownMinutes} minutes.");
        }

        subscription.Kinds = string.Join(',', normalizedKinds);
        subscription.FlowPattern = flowPattern;
        subscription.EmailAddress = emailAddress;
        subscription.SlackTarget = subscription.Channel == NotificationChannels.Slack ? slackTarget : null;
        subscription.DigestIntervalMinutes = digestIntervalMinutes;
        subscription.CooldownMinutes = cooldownMinutes;

        // The subscription must have SOME reachable destination now, not at first failure: an override address,
        // an explicit Slack conversation, or the account email the defaults fall back to.
        if (await NotificationTargetResolver.ResolveAsync(catalog, subscription, ct).ConfigureAwait(false) is null)
        {
            return Invalid("No destination",
                subscription.Channel == NotificationChannels.Email
                    ? "Your account has no email address; set one on the subscription (or ask an admin to add one to your account)."
                    : "Give a Slack conversation id, or add an email address to your account so the direct message can be resolved.");
        }

        return null;
    }

    private static ProblemHttpResult? ChannelUnavailable(NotificationOptions notifications, string channel)
    {
        if (!notifications.Enabled)
        {
            return Invalid("Notifications are disabled",
                "The notification service is disabled on this control plane (ControlPlane:Notifications:Enabled).");
        }

        if (channel == NotificationChannels.Email && !notifications.EmailConfigured)
        {
            return Invalid("Email is not configured",
                "No email provider is configured on this control plane (ControlPlane:Notifications:Email:Provider).");
        }

        if (channel == NotificationChannels.Slack && !notifications.SlackConfigured)
        {
            return Invalid("Slack is not configured",
                "No Slack bot token is configured on this control plane (ControlPlane:Notifications:Slack:BotTokenReference).");
        }

        return null;
    }

    private static NotificationSubscriptionDto ToDto(CatalogNotificationSubscription s) => new(
        s.Id, s.Channel, s.Mode, SplitKinds(s.Kinds), s.FlowPattern, s.EmailAddress, s.SlackTarget,
        s.DigestIntervalMinutes, s.CooldownMinutes, s.Enabled, s.LastSentUtc, s.NextDueUtc, s.CreatedUtc, s.UpdatedUtc);

    private static IReadOnlyList<string> SplitKinds(string kinds)
        => kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The update contract's tri-state for clearable strings: null keeps, empty clears, a value sets.</summary>
    private static string? Coalesce(string? requested, string? current)
        => requested is null ? current : Normalize(requested);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
        => Guid.TryParse(user.FindFirst("uid")?.Value, out userId);

    private static ProblemHttpResult NoUserAccount()
        => TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
            title: "No user account",
            detail: "Notification subscriptions belong to a user account; this session is not backed by one.");

    private static ProblemHttpResult NotFound()
        => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found",
            detail: "No notification subscription with that id belongs to you.");

    private static ProblemHttpResult Invalid(string title, string detail)
        => TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, detail: detail);

    [GeneratedRegex("^[CGDUW][A-Z0-9]{4,63}$")]
    private static partial Regex SlackTargetPattern();
}
