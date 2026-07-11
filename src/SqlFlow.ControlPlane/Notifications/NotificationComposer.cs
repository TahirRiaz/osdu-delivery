using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Notifications;

/// <summary>Everything one message is composed from: the subscription's matched events (oldest first, never
/// empty), whether more were pending than fit (they follow in the next message), and the rendering context.</summary>
public sealed record NotificationComposition(
    string Channel,
    string Mode,
    IReadOnlyList<CatalogNotificationEvent> Events,
    bool MorePending,
    string? GuiBaseUrl,
    DateTime NowUtc);

/// <summary>
/// Renders a subscription's pending events into one message. The shape is the anti-spam contract made visible:
/// however many events a window holds, the reader gets one message, grouped per flow with a repeat count ("failed
/// x12"), the most recent error excerpt, and a link into the GUI, ordered failures first. Pure and deterministic
/// (no I/O, no clock reads beyond the supplied instant), so composition is directly unit-testable.
/// </summary>
public static class NotificationComposer
{
    /// <summary>The most events one message covers; a bigger backlog continues in the next message. Bounds both
    /// the dispatch query and the composed payload size.</summary>
    public const int MaxEventsPerMessage = 500;

    /// <summary>How many flow groups are listed in full; the rest fold into "and N more flows". Slack tops out at
    /// 50 blocks and email readability tops out far earlier, so one bound serves both channels.</summary>
    public const int MaxFlowSections = 15;

    private const int ErrorExcerptLength = 300;

    private const int SubjectFlowNameLength = 120;

    /// <summary>One flow's aggregated slice of the window: the unit a reader scans.</summary>
    private sealed record FlowGroup(
        string FlowName, string FlowKind, string Kind, int Count, DateTime LastOccurredUtc, Guid LastRunId,
        string? LastError);

    public static NotificationMessage Compose(NotificationComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        if (composition.Events.Count == 0)
        {
            throw new ArgumentException("A message needs at least one event; empty windows send nothing.", nameof(composition));
        }

        var groups = GroupEvents(composition.Events);
        var subject = ComposeSubject(composition, groups);
        var text = ComposeText(composition, groups, subject);
        var html = composition.Channel == NotificationChannels.Email ? ComposeHtml(composition, groups, subject) : null;
        var blocks = composition.Channel == NotificationChannels.Slack ? ComposeSlackBlocks(composition, groups, subject) : null;
        return new NotificationMessage(subject, text, html, blocks);
    }

    /// <summary>The message a test send delivers: proof the channel, address, and credentials work end to end.</summary>
    public static NotificationMessage ComposeTest(string channel, string? guiBaseUrl, DateTime nowUtc)
    {
        const string subject = "SQLFlow test notification";
        var settingsUrl = SettingsUrl(guiBaseUrl);
        var text = "This is a test notification from SQLFlow, sent at "
            + Stamp(nowUtc)
            + ". If you can read this, the subscription's channel and destination are configured correctly."
            + (settingsUrl is null ? string.Empty : $"\n\nManage your notification settings: {settingsUrl}");

        string? html = null;
        string? blocks = null;
        if (channel == NotificationChannels.Email)
        {
            var body = new StringBuilder();
            body.Append("<div style=\"font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:14px;color:#1f2328\">");
            body.Append("<h2 style=\"font-size:16px;margin:0 0 8px\">SQLFlow test notification</h2>");
            body.Append("<p>This is a test notification, sent at ").Append(WebUtility.HtmlEncode(Stamp(nowUtc)))
                .Append(". If you can read this, the subscription's channel and destination are configured correctly.</p>");
            if (settingsUrl is not null)
            {
                body.Append("<p style=\"color:#57606a\"><a href=\"").Append(WebUtility.HtmlEncode(settingsUrl))
                    .Append("\">Manage your notification settings</a></p>");
            }

            body.Append("</div>");
            html = body.ToString();
        }
        else
        {
            var array = new JsonArray
            {
                HeaderBlock(subject),
                SectionBlock("This is a test notification, sent at " + MrkdwnEscape(Stamp(nowUtc))
                    + ". If you can read this, the subscription's channel and destination are configured correctly."),
            };
            if (settingsUrl is not null)
            {
                array.Add(ContextBlock($"<{MrkdwnEscape(settingsUrl)}|Manage your notification settings>"));
            }

            blocks = array.ToJsonString();
        }

        return new NotificationMessage(subject, text, html, blocks);
    }

    /// <summary>The GUI deep link for a run, when a GUI base URL is configured.</summary>
    public static string? RunUrl(string? guiBaseUrl, Guid runId)
        => string.IsNullOrWhiteSpace(guiBaseUrl) ? null : $"{guiBaseUrl.TrimEnd('/')}/runs/{runId}";

    /// <summary>The GUI notification-settings link, when a GUI base URL is configured.</summary>
    public static string? SettingsUrl(string? guiBaseUrl)
        => string.IsNullOrWhiteSpace(guiBaseUrl) ? null : $"{guiBaseUrl.TrimEnd('/')}/settings/notifications";

    private static List<FlowGroup> GroupEvents(IReadOnlyList<CatalogNotificationEvent> events)
        => events
            .GroupBy(e => (e.FlowName, e.Kind))
            .Select(g =>
            {
                var last = g.OrderBy(e => e.OccurredUtc).ThenBy(e => e.Id).Last();
                return new FlowGroup(g.Key.FlowName, last.FlowKind, g.Key.Kind, g.Count(), last.OccurredUtc, last.RunId, last.Error);
            })
            .OrderBy(g => KindRank(g.Kind))
            .ThenByDescending(g => g.LastOccurredUtc)
            .ToList();

    private static int KindRank(string kind) => kind switch
    {
        NotificationEventKinds.RunFailed => 0,
        NotificationEventKinds.AssertionFailed => 1,
        NotificationEventKinds.RunCancelled => 2,
        _ => 3,
    };

    private static string ComposeSubject(NotificationComposition composition, List<FlowGroup> groups)
    {
        var prefix = composition.Mode == NotificationModes.Digest ? "SQLFlow digest: " : "SQLFlow: ";
        if (composition.Events.Count == 1)
        {
            var only = groups[0];
            var flow = Truncate(only.FlowName, SubjectFlowNameLength);
            return only.Kind switch
            {
                NotificationEventKinds.RunFailed => $"{prefix}flow '{flow}' failed",
                NotificationEventKinds.AssertionFailed => $"{prefix}assertions failed on flow '{flow}'",
                NotificationEventKinds.RunCancelled => $"{prefix}run of flow '{flow}' was cancelled",
                _ => $"{prefix}run of flow '{flow}' was skipped",
            };
        }

        var counts = composition.Events
            .GroupBy(e => e.Kind)
            .OrderBy(g => KindRank(g.Key))
            .Select(g => $"{g.Count().ToString(CultureInfo.InvariantCulture)} {KindNoun(g.Key, g.Count())}");
        var flows = groups.Select(g => g.FlowName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return $"{prefix}{string.Join(", ", counts)} across {flows.ToString(CultureInfo.InvariantCulture)} {Plural(flows, "flow", "flows")}";
    }

    private static string KindNoun(string kind, int count) => kind switch
    {
        NotificationEventKinds.RunFailed => Plural(count, "failed run", "failed runs"),
        NotificationEventKinds.AssertionFailed => Plural(count, "assertion failure", "assertion failures"),
        NotificationEventKinds.RunCancelled => Plural(count, "cancelled run", "cancelled runs"),
        _ => Plural(count, "skipped run", "skipped runs"),
    };

    private static string KindVerb(string kind, int count) => kind switch
    {
        NotificationEventKinds.RunFailed => count == 1 ? "failed" : $"failed x{count.ToString(CultureInfo.InvariantCulture)}",
        NotificationEventKinds.AssertionFailed => count == 1 ? "assertions failed" : $"assertions failed x{count.ToString(CultureInfo.InvariantCulture)}",
        NotificationEventKinds.RunCancelled => count == 1 ? "was cancelled" : $"cancelled x{count.ToString(CultureInfo.InvariantCulture)}",
        _ => count == 1 ? "was skipped (upstream failure)" : $"skipped x{count.ToString(CultureInfo.InvariantCulture)} (upstream failures)",
    };

    private static string WindowLine(NotificationComposition composition)
    {
        var first = composition.Events.Min(e => e.OccurredUtc);
        var last = composition.Events.Max(e => e.OccurredUtc);
        var count = composition.Events.Count.ToString(CultureInfo.InvariantCulture);
        return composition.Events.Count == 1
            ? $"At {Stamp(first)}."
            : $"{count} events between {Stamp(first)} and {Stamp(last)}.";
    }

    private static string ComposeText(NotificationComposition composition, List<FlowGroup> groups, string subject)
    {
        var text = new StringBuilder();
        text.AppendLine(subject);
        text.AppendLine(WindowLine(composition));
        text.AppendLine();
        foreach (var group in groups.Take(MaxFlowSections))
        {
            text.Append("- ").Append(group.FlowName).Append(" (").Append(group.FlowKind).Append("): ")
                .Append(KindVerb(group.Kind, group.Count)).Append(", last ").AppendLine(Stamp(group.LastOccurredUtc));
            if (!string.IsNullOrWhiteSpace(group.LastError))
            {
                text.Append("  ").AppendLine(Excerpt(group.LastError));
            }

            if (RunUrl(composition.GuiBaseUrl, group.LastRunId) is { } url)
            {
                text.Append("  ").AppendLine(url);
            }
        }

        if (groups.Count > MaxFlowSections)
        {
            var more = groups.Count - MaxFlowSections;
            text.Append("... and ").Append(more.ToString(CultureInfo.InvariantCulture))
                .Append(" more ").Append(Plural(more, "flow", "flows")).AppendLine(".");
        }

        if (composition.MorePending)
        {
            text.AppendLine();
            text.AppendLine("More events were pending than fit in one message; the remainder follows in the next one.");
        }

        if (SettingsUrl(composition.GuiBaseUrl) is { } settings)
        {
            text.AppendLine();
            text.Append("Manage your notification settings: ").AppendLine(settings);
        }

        return text.ToString();
    }

    private static string ComposeHtml(NotificationComposition composition, List<FlowGroup> groups, string subject)
    {
        var html = new StringBuilder();
        html.Append("<div style=\"font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:14px;color:#1f2328\">");
        html.Append("<h2 style=\"font-size:16px;margin:0 0 4px\">").Append(WebUtility.HtmlEncode(subject)).Append("</h2>");
        html.Append("<p style=\"margin:0 0 12px;color:#57606a\">").Append(WebUtility.HtmlEncode(WindowLine(composition))).Append("</p>");
        html.Append("<table style=\"border-collapse:collapse;width:100%\">");
        foreach (var group in groups.Take(MaxFlowSections))
        {
            html.Append("<tr>");
            html.Append("<td style=\"padding:6px 8px;border-top:1px solid #d0d7de;vertical-align:top\">")
                .Append("<strong>").Append(WebUtility.HtmlEncode(group.FlowName)).Append("</strong>")
                .Append(" <span style=\"color:#57606a\">(").Append(WebUtility.HtmlEncode(group.FlowKind)).Append(")</span>");
            if (!string.IsNullOrWhiteSpace(group.LastError))
            {
                html.Append("<div style=\"margin-top:4px;font-family:Consolas,monospace;font-size:12px;color:#82071e\">")
                    .Append(WebUtility.HtmlEncode(Excerpt(group.LastError))).Append("</div>");
            }

            html.Append("</td>");
            html.Append("<td style=\"padding:6px 8px;border-top:1px solid #d0d7de;white-space:nowrap;vertical-align:top\">")
                .Append(WebUtility.HtmlEncode(KindVerb(group.Kind, group.Count))).Append("</td>");
            html.Append("<td style=\"padding:6px 8px;border-top:1px solid #d0d7de;white-space:nowrap;vertical-align:top\">")
                .Append(WebUtility.HtmlEncode(Stamp(group.LastOccurredUtc)));
            if (RunUrl(composition.GuiBaseUrl, group.LastRunId) is { } url)
            {
                html.Append(" <a href=\"").Append(WebUtility.HtmlEncode(url)).Append("\">Open run</a>");
            }

            html.Append("</td></tr>");
        }

        html.Append("</table>");
        if (groups.Count > MaxFlowSections)
        {
            var more = groups.Count - MaxFlowSections;
            html.Append("<p style=\"color:#57606a\">... and ").Append(more.ToString(CultureInfo.InvariantCulture))
                .Append(" more ").Append(Plural(more, "flow", "flows")).Append(".</p>");
        }

        if (composition.MorePending)
        {
            html.Append("<p style=\"color:#57606a\">More events were pending than fit in one message; the remainder follows in the next one.</p>");
        }

        if (SettingsUrl(composition.GuiBaseUrl) is { } settings)
        {
            html.Append("<p style=\"margin-top:12px;color:#57606a;font-size:12px\">You receive this because you subscribed to SQLFlow notifications. <a href=\"")
                .Append(WebUtility.HtmlEncode(settings)).Append("\">Manage your settings</a>.</p>");
        }

        html.Append("</div>");
        return html.ToString();
    }

    private static string ComposeSlackBlocks(NotificationComposition composition, List<FlowGroup> groups, string subject)
    {
        var blocks = new JsonArray
        {
            HeaderBlock(subject),
            ContextBlock(MrkdwnEscape(WindowLine(composition))),
        };

        foreach (var group in groups.Take(MaxFlowSections))
        {
            var section = new StringBuilder();
            section.Append('*').Append(MrkdwnEscape(group.FlowName)).Append("* (")
                .Append(MrkdwnEscape(group.FlowKind)).Append(") ").Append(MrkdwnEscape(KindVerb(group.Kind, group.Count)))
                .Append(", last ").Append(MrkdwnEscape(Stamp(group.LastOccurredUtc)));
            if (!string.IsNullOrWhiteSpace(group.LastError))
            {
                section.Append("\n```").Append(MrkdwnEscape(Excerpt(group.LastError))).Append("```");
            }

            if (RunUrl(composition.GuiBaseUrl, group.LastRunId) is { } url)
            {
                section.Append("\n<").Append(MrkdwnEscape(url)).Append("|Open run>");
            }

            blocks.Add(SectionBlock(Truncate(section.ToString(), 3000)));
        }

        if (groups.Count > MaxFlowSections)
        {
            var more = groups.Count - MaxFlowSections;
            blocks.Add(ContextBlock($"... and {more.ToString(CultureInfo.InvariantCulture)} more {Plural(more, "flow", "flows")}."));
        }

        if (composition.MorePending)
        {
            blocks.Add(ContextBlock("More events were pending than fit in one message; the remainder follows in the next one."));
        }

        if (SettingsUrl(composition.GuiBaseUrl) is { } settings)
        {
            blocks.Add(ContextBlock($"<{MrkdwnEscape(settings)}|Manage your notification settings>"));
        }

        return blocks.ToJsonString();
    }

    private static JsonObject HeaderBlock(string text) => new()
    {
        // Slack caps a header's plain text at 150 characters.
        ["type"] = "header",
        ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = Truncate(text, 150), ["emoji"] = false },
    };

    private static JsonObject SectionBlock(string mrkdwn) => new()
    {
        ["type"] = "section",
        ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = mrkdwn },
    };

    private static JsonObject ContextBlock(string mrkdwn) => new()
    {
        ["type"] = "context",
        ["elements"] = new JsonArray(new JsonObject { ["type"] = "mrkdwn", ["text"] = mrkdwn }),
    };

    /// <summary>Slack mrkdwn requires exactly these three escapes; everything else is literal.</summary>
    private static string MrkdwnEscape(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Stamp(DateTime utc)
        => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>A single-line error excerpt: whitespace runs (including newlines) collapse to one space so the
    /// excerpt reads inline, bounded so one enormous engine error never bloats a message.</summary>
    private static string Excerpt(string error)
    {
        var collapsed = new StringBuilder(Math.Min(error.Length, ErrorExcerptLength + 16));
        var pendingSpace = false;
        foreach (var ch in error)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = collapsed.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                collapsed.Append(' ');
                pendingSpace = false;
            }

            collapsed.Append(ch);
            if (collapsed.Length >= ErrorExcerptLength)
            {
                break;
            }
        }

        var result = collapsed.ToString();
        return result.Length >= ErrorExcerptLength ? result + "..." : result;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 3)] + "...";

    private static string Plural(int count, string singular, string plural) => count == 1 ? singular : plural;
}
