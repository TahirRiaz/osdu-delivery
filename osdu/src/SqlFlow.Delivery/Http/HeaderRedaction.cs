// Vendored from SQLFlow (https://github.com/TahirRiaz/sqlflow-v3, commit ddd4ea12160bda044f75dcad2bbec5099c3a7263)
// src/SqlFlow.Acquire/Runtime/HeaderRedaction.cs, plus a message redactor in the spirit of SecretHygiene, the URL a
// message names without its credentials, and the one test of whether a name holds a secret.
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// Redacts credentials so they never reach a log, an exception or the ledger: sensitive header values, the query string
/// and user info of a URL a message names, and the secrets a free-text message quotes. Whether a name holds a secret is
/// decided here once (<see cref="IsSecretName"/>), for the redactor and for the document loader, which refuses a
/// literal secret.
/// </summary>
public static partial class HeaderRedaction
{
    private static readonly HashSet<string> Sensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "cookie", "set-cookie", "proxy-authorization", "x-api-key", "x-auth-token", "ocp-apim-subscription-key",
    };

    /// <summary>The endings of a name that holds a key granting access by itself (an API key, a storage account key).</summary>
    private static readonly string[] SecretKeyEndings =
    [
        "apikey", "subscriptionkey", "accountkey", "accesskey", "secretkey", "privatekey", "signingkey", "masterkey",
    ];

    /// <summary>
    /// The endings of a query parameter a signed URL names its signer with (S3's <c>X-Amz-Credential</c> and
    /// <c>AWSAccessKeyId</c>, Google Cloud Storage's <c>X-Goog-Credential</c> and <c>GoogleAccessId</c>): not a secret on
    /// its own, but no message needs it, so it is redacted with the signature beside it.
    /// </summary>
    private static readonly string[] SignerEndings = ["credential", "credentials", "accesskeyid", "googleaccessid"];

    /// <summary>The parameters of an Azure shared access signature that are redacted with its <c>sig</c>.</summary>
    private static readonly HashSet<string> SharedAccessParameters = new(StringComparer.OrdinalIgnoreCase) { "sv", "se", "sp", "sr" };

    /// <summary>Whether a header with <paramref name="name"/> carries a credential: an auth or cookie header, any <c>x-api-*</c> header, or a header named as a secret is.</summary>
    public static bool IsSensitive(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Sensitive.Contains(name) || name.StartsWith("x-api-", StringComparison.OrdinalIgnoreCase) || IsSecretName(name);
    }

    /// <summary>
    /// Whether a field, parameter or header named <paramref name="name"/> holds a secret: a value that grants access by
    /// itself. Compared on the name's letters and digits alone, without case, so <c>client_secret</c>, <c>clientSecret</c>
    /// and <c>Client-Secret</c> are one name: a password, a secret, a token (<c>refresh_token</c>, <c>x-amz-security-token</c>),
    /// an assertion, a signature, an authorization or cookie, or a key such as an API or storage account key. An identifier
    /// (<c>client_id</c>, <c>username</c>) or a setting (<c>scope</c>, <c>token_type</c>) is not one.
    /// </summary>
    public static bool IsSecretName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var bare = Bare(name);
        return bare.Length > 0
            && (bare.Contains("secret", StringComparison.Ordinal)
                || bare.Contains("password", StringComparison.Ordinal)
                || bare.Contains("passwd", StringComparison.Ordinal)
                || bare is "pwd" or "sig" or "cookie" or "setcookie"
                || bare.EndsWith("token", StringComparison.Ordinal)
                || bare.EndsWith("assertion", StringComparison.Ordinal)
                || bare.EndsWith("signature", StringComparison.Ordinal)
                || bare.EndsWith("authorization", StringComparison.Ordinal)
                || SecretKeyEndings.Any(ending => bare.EndsWith(ending, StringComparison.Ordinal)));
    }

    /// <summary>
    /// A URL as a message, a log line or a stored error names it: its scheme, host, port and path, without the user info,
    /// query string and fragment, where a signed URL or a URL with a password carries its credential. A relative URL
    /// keeps what comes before its query.
    /// </summary>
    public static string DescribeUrl(Uri? url)
    {
        if (url is null)
        {
            return string.Empty;
        }

        // A scheme whose parser knows no query (ftp, say) keeps '?...' in the path, escaped, so the path is cut there too.
        return url.IsAbsoluteUri
            ? Unqueried(url.GetComponents(UriComponents.Scheme | UriComponents.Host | UriComponents.Port | UriComponents.Path, UriFormat.UriEscaped))
            : Unqueried(url.OriginalString);
    }

    /// <summary>
    /// A URL written as text, named as <see cref="DescribeUrl(Uri?)"/> names it. Text that is not an absolute URL (a
    /// relative path, a reference) and a local path keep what comes before a query.
    /// </summary>
    public static string DescribeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var absolute) && !absolute.IsFile
            ? DescribeUrl(absolute)
            : Unqueried(url);
    }

    /// <summary>What comes before a query or a fragment, written as is or escaped (<c>%3F</c>, <c>%23</c>).</summary>
    private static string Unqueried(string text)
    {
        var end = text.IndexOfAny(['?', '#']);
        foreach (var escaped in (ReadOnlySpan<string>)["%3F", "%23"])
        {
            var at = text.IndexOf(escaped, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (end < 0 || at < end))
            {
                end = at;
            }
        }

        return end < 0 ? text : text[..end];
    }

    /// <summary>A name's letters and digits, lower case: how <see cref="IsSecretName"/> compares names.</summary>
    private static string Bare(string name)
    {
        var bare = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                bare.Append(char.ToLowerInvariant(c));
            }
        }

        return bare.ToString();
    }

    /// <summary>Whether a query parameter or a JSON field named <paramref name="name"/> is redacted from a message.</summary>
    private static bool Redacted(string name)
    {
        if (IsSecretName(name) || SharedAccessParameters.Contains(name))
        {
            return true;
        }

        var bare = Bare(name);
        return SignerEndings.Any(ending => bare.EndsWith(ending, StringComparison.Ordinal));
    }

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
    /// Redacts the credentials a free-text message (an exception message, a response preview) may quote before it is
    /// logged or stored as a record's last error: bearer tokens and the other schemes of an Authorization header, the
    /// password in a URL's user info, every query parameter or <c>name=value</c> pair named as a secret (Azure's
    /// <c>sig</c> and the rest of a shared access signature, S3's <c>X-Amz-Signature</c>, <c>X-Amz-Credential</c> and
    /// <c>X-Amz-Security-Token</c>, Google Cloud Storage's <c>X-Goog-Signature</c> and <c>X-Goog-Credential</c>, API keys,
    /// passwords), and every JSON field named as one.
    /// </summary>
    public static string RedactMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var text = RedactAll(message);
        return text.Length <= 2000 ? text : text[..2000] + "...";
    }

    /// <summary>
    /// Whether <paramref name="text"/> quotes a credential <see cref="RedactMessage"/> would redact: a password in a URL's
    /// user info, a query parameter named as a secret or a signer, a bearer token. The document loader asks it of a URL a
    /// flow writes literally.
    /// </summary>
    public static bool QuotesCredential(string? text)
        => !string.IsNullOrEmpty(text) && !string.Equals(RedactAll(text), text, StringComparison.Ordinal);

    private static string RedactAll(string message)
    {
        var text = Bearer().Replace(message, "Bearer ***");
        text = AuthorizationScheme().Replace(text, "${lead}***");
        text = UserInfo().Replace(text, "${scheme}***@");
        text = NamedValue().Replace(text, match => Redacted(match.Groups["name"].Value) ? match.Groups["name"].Value + "=***" : match.Value);
        return JsonField().Replace(text, match => Redacted(match.Groups["name"].Value) ? $"\"{match.Groups["name"].Value}\":\"***\"" : match.Value);
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-\._~\+\/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    /// <summary>An Authorization header quoted with a scheme other than Bearer, which carries the credential after the scheme.</summary>
    [GeneratedRegex(@"(?<lead>\b(?:proxy-)?authorization[""']?\s*[:=]\s*[""']?(?:Basic|Digest|Negotiate|NTLM|SharedKey|SharedAccessSignature)\s+)[^\s""',;]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationScheme();

    /// <summary>The user name and password of a URL (<c>https://user:password@host</c>).</summary>
    [GeneratedRegex(@"(?<scheme>\b[A-Za-z][A-Za-z0-9+.\-]*://)[^\s/?#@:]+:[^\s/?#@]+@")]
    private static partial Regex UserInfo();

    /// <summary>
    /// A <c>name=value</c> pair: a query parameter, a form field, a connection string's setting, an AWS Authorization
    /// header's part. The value ends where the next pair of any of them begins, so every pair is looked at by its own name.
    /// </summary>
    [GeneratedRegex(@"(?<name>[A-Za-z0-9_.\-]+)=[^&\s""',;]+")]
    private static partial Regex NamedValue();

    /// <summary>A JSON field with a text value, escapes included.</summary>
    [GeneratedRegex(@"""(?<name>[^""\\]{1,128})""\s*:\s*""(?:[^""\\]|\\.)*""")]
    private static partial Regex JsonField();
}
