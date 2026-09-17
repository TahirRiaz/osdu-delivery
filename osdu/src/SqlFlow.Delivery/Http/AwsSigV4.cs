using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// AWS Signature Version 4, for the S3-compatible object stores OSDU services hand out temporary credentials for (Seismic
/// Store on anthos and IBM, osdu/specs/seismic-ddms/INTEGRATION.md section 4.3). A request is signed as S3 reads it: the
/// path's segments and the query encoded once, as RFC 3986 unreserved characters or percent escapes; the host, every
/// <c>x-amz-*</c> header and a <c>Content-MD5</c> signed; and the body left unsigned (<c>UNSIGNED-PAYLOAD</c>), since a
/// part streams and its integrity is carried by its <c>Content-MD5</c>. A request is signed again for every attempt.
/// </summary>
public static class AwsSigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    public const string DateHeader = "x-amz-date";

    public const string ContentHashHeader = "x-amz-content-sha256";

    public const string SessionTokenHeader = "x-amz-security-token";

    /// <summary>A key pair, and the session token of temporary credentials.</summary>
    public sealed record Credentials(string AccessKey, string SecretKey, string? SessionToken)
    {
        /// <summary>The key pair and session never show in a log or an attempt.</summary>
        public override string ToString() => "(S3 credentials)";
    }

    /// <summary>
    /// Signs <paramref name="request"/> in place for <paramref name="service"/> in <paramref name="region"/> at
    /// <paramref name="now"/>: sets <c>x-amz-date</c>, <c>x-amz-content-sha256</c>, the session token when there is one,
    /// and <c>Authorization</c>.
    /// </summary>
    public static void Sign(HttpRequestMessage request, Credentials credentials, string region, string service, DateTimeOffset now, string payloadHash = UnsignedPayload)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);
        var uri = request.RequestUri ?? throw new ArgumentException("The request has no URL to sign.", nameof(request));
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var day = stamp[..8];

        request.Headers.Remove(DateHeader);
        request.Headers.Remove(ContentHashHeader);
        request.Headers.Remove(SessionTokenHeader);
        request.Headers.TryAddWithoutValidation(DateHeader, stamp);
        request.Headers.TryAddWithoutValidation(ContentHashHeader, payloadHash);
        if (!string.IsNullOrEmpty(credentials.SessionToken))
        {
            request.Headers.TryAddWithoutValidation(SessionTokenHeader, credentials.SessionToken);
        }

        var signed = SignedHeaders(request, uri);
        var canonical = CanonicalRequest(request.Method.Method, uri, signed, payloadHash);
        var scope = $"{day}/{region}/{service}/aws4_request";
        var toSign = $"{Algorithm}\n{stamp}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = SigningKey(credentials.SecretKey, day, region, service);
        var signature = Hex(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(toSign)));
        var names = string.Join(';', signed.Keys);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            Algorithm, $"Credential={credentials.AccessKey}/{scope}, SignedHeaders={names}, Signature={signature}");
    }

    /// <summary>
    /// The canonical request S3 compares: the method, the path and query encoded once, the signed headers with their
    /// values trimmed and inner spaces collapsed, their names, and the payload hash.
    /// </summary>
    public static string CanonicalRequest(string method, Uri uri, IReadOnlyDictionary<string, string> signedHeaders, string payloadHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(signedHeaders);
        var builder = new StringBuilder();
        builder.Append(method.ToUpperInvariant()).Append('\n');
        builder.Append(CanonicalPath(uri)).Append('\n');
        builder.Append(CanonicalQuery(uri)).Append('\n');
        foreach (var (name, value) in signedHeaders)
        {
            builder.Append(name).Append(':').Append(value).Append('\n');
        }

        builder.Append('\n');
        builder.Append(string.Join(';', signedHeaders.Keys)).Append('\n');
        builder.Append(payloadHash);
        return builder.ToString();
    }

    /// <summary>The path as S3 canonicalises it: each segment decoded, then encoded once.</summary>
    public static string CanonicalPath(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var path = uri.AbsolutePath;
        if (path.Length == 0)
        {
            return "/";
        }

        return string.Join('/', path.Split('/').Select(segment => Encode(Uri.UnescapeDataString(segment))));
    }

    /// <summary>The query as SigV4 canonicalises it: every name and value encoded once, sorted by name, then by value.</summary>
    public static string CanonicalQuery(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return string.Empty;
        }

        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                var name = equals < 0 ? pair : pair[..equals];
                var value = equals < 0 ? string.Empty : pair[(equals + 1)..];
                return (Name: Encode(Unescape(name)), Value: Encode(Unescape(value)));
            })
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal);
        return string.Join('&', pairs.Select(p => p.Name + "=" + p.Value));
    }

    /// <summary>A value encoded once, as SigV4 requires: unreserved characters kept, everything else as uppercase escapes of its UTF-8 bytes.</summary>
    public static string Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>The key a day's signatures are made with.</summary>
    public static byte[] SigningKey(string secretKey, string day, string region, string service)
    {
        ArgumentNullException.ThrowIfNull(secretKey);
        var date = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secretKey), Encoding.UTF8.GetBytes(day));
        var regional = HMACSHA256.HashData(date, Encoding.UTF8.GetBytes(region));
        var serviced = HMACSHA256.HashData(regional, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(serviced, "aws4_request"u8);
    }

    /// <summary>The headers a request is signed with, by lowercase name in ordinal order: the host, every <c>x-amz-*</c> header, and a <c>Content-MD5</c>.</summary>
    private static SortedDictionary<string, string> SignedHeaders(HttpRequestMessage request, Uri uri)
    {
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IsDefaultPort ? uri.IdnHost : uri.IdnHost + ":" + uri.Port.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (name, values) in request.Headers.NonValidated)
        {
            var lower = name.ToLowerInvariant();
            if (lower.StartsWith("x-amz-", StringComparison.Ordinal))
            {
                signed[lower] = Trim(string.Join(',', values));
            }
        }

        if (request.Content?.Headers.ContentMD5 is { } md5)
        {
            signed["content-md5"] = Convert.ToBase64String(md5);
        }

        return signed;
    }

    private static string Trim(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Unescape(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
