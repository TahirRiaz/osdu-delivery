// Vendored from SQLFlow (https://github.com/TahirRiaz/sqlflow-v3, commit ddd4ea12160bda044f75dcad2bbec5099c3a7263)
// src/SqlFlow.Acquire/Runtime/HeaderRedaction.cs, plus a message redactor in the spirit of SecretHygiene.
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Http;

/// <summary>Redacts sensitive HTTP header values so credentials never reach a log or the ledger.</summary>
public static partial class HeaderRedaction
{
    private static readonly HashSet<string> Sensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "cookie", "set-cookie", "proxy-authorization", "x-api-key", "x-auth-token", "ocp-apim-subscription-key",
    };

    public static bool IsSensitive(string name)
        => Sensitive.Contains(name) || name.StartsWith("x-api-", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            result[name] = IsSensitive(name) ? "***" : value;
        }

        return result;
    }

    /// <summary>
    /// Redacts bearer tokens, key-like query parameters and long opaque secrets from a free-text message (an
    /// exception message, a response preview) before it is stored as a record's last error.
    /// </summary>
    public static string RedactMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var text = Bearer().Replace(message, "Bearer ***");
        text = KeyParam().Replace(text, "${name}=***");
        text = JsonSecret().Replace(text, "\"${name}\":\"***\"");
        return text.Length <= 2000 ? text : text[..2000] + "...";
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-\._~\+\/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?<name>(api[_-]?key|subscription[_-]?key|token|secret|password|sig|sv|se|sp|sr))=[^&\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyParam();

    [GeneratedRegex(@"""(?<name>(access_token|refresh_token|client_secret|password|token|secret))""\s*:\s*""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecret();
}
