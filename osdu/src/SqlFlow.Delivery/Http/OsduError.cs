using System.Text;
using System.Text.Json;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// Reads an error response body into the sentence it carries, for the message a failed request surfaces with and
/// the ledger stores on the attempt.
///
/// The OSDU services answer in three shapes, and the useful part of each sits in a different place. The Java
/// services (storage, search, legal, file, workflow, schema) send <c>AppError</c>: <c>{"code", "reason",
/// "message"}</c>, where <c>reason</c> is the status phrase and <c>message</c> is what went wrong. Spring answers a
/// request its controller never reached (a 415 for a missing Content-Type) with a problem document:
/// <c>{"title", "detail", "status"}</c>. The wellbore DDMS is FastAPI and sends <c>{"detail": "..."}</c>, or for a
/// validation failure <c>{"detail": [{"loc": [...], "msg": "...", "type": "..."}]}</c>, which only makes sense with
/// the field each message is about. A raw preview of those bodies cuts the message off or buries it in JSON; the
/// OSDU C# client surfaces <c>AppError.message</c> and <c>reason</c> typed for the same reason.
///
/// Anything else (HTML from a gateway, plain text, JSON in some other shape) is kept as a bounded preview, so nothing
/// the service said is lost. The result is bounded either way, because it lands in log lines and ledger rows.
/// </summary>
public static class OsduError
{
    /// <summary>The longest description returned; longer ones are cut with an ellipsis.</summary>
    public const int MaxLength = 512;

    /// <summary>The body described: the service's own message when the body is a shape it recognises, else a preview.</summary>
    public static string Describe(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty response body)";
        }

        var trimmed = body.Trim();
        if (trimmed[0] is '{' && TryDescribeJson(trimmed) is { Length: > 0 } described)
        {
            return Bound(described);
        }

        return Bound(trimmed);
    }

    private static string? TryDescribeJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // FastAPI (wellbore DDMS): detail is a sentence, or a list of per-field validation errors. The Reservoir
            // Management DDMS also answers {"detail": {"message": ...}}.
            if (root.TryGetProperty("detail", out var detail) && !root.TryGetProperty("title", out _))
            {
                return detail.ValueKind switch
                {
                    JsonValueKind.String => detail.GetString(),
                    JsonValueKind.Array => ValidationErrors(detail),
                    JsonValueKind.Object => Text(detail, "message"),
                    _ => null,
                };
            }

            // AppError (the Java services): the reason is the status phrase, the message is the substance.
            var message = Text(root, "message");
            var reason = Text(root, "reason");
            if (message is not null || reason is not null)
            {
                return Join(message, reason);
            }

            // Spring problem details, for a request the controller never reached.
            var title = Text(root, "title");
            var problem = Text(root, "detail");
            if (title is not null || problem is not null)
            {
                return problem is null ? title : title is null ? problem : $"{title}: {problem}";
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>field.path: message</c> for each validation error, in the order the service listed them.</summary>
    private static string? ValidationErrors(JsonElement errors)
    {
        var parts = new List<string>();
        foreach (var error in errors.EnumerateArray())
        {
            if (error.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var msg = Text(error, "msg");
            if (msg is null)
            {
                continue;
            }

            var location = error.TryGetProperty("loc", out var loc) && loc.ValueKind == JsonValueKind.Array
                ? string.Join(".", loc.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : l.GetRawText()).Where(l => l is not "body"))
                : null;
            parts.Add(string.IsNullOrEmpty(location) ? msg : $"{location}: {msg}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>"message (reason)", leaving out whichever the service did not send or repeated.</summary>
    private static string? Join(string? message, string? reason)
    {
        if (message is null)
        {
            return reason;
        }

        return reason is null || string.Equals(message, reason, StringComparison.OrdinalIgnoreCase)
            ? message
            : $"{message} ({reason})";
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;

    private static string Bound(string text)
    {
        var single = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            single.Append(char.IsControl(c) ? ' ' : c);
        }

        var flat = single.ToString();
        return flat.Length <= MaxLength ? flat : flat[..MaxLength] + "...";
    }
}
