using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SqlFlow.Core;

namespace SqlFlow.Acquire.Runtime;

/// <summary>The raw outcome of one HTTP fetch: status, body bytes, and the response/content headers.</summary>
public sealed record HttpFetchResult(
    HttpStatusCode Status,
    byte[] Body,
    HttpResponseHeaders Headers,
    HttpContentHeaders? ContentHeaders)
{
    /// <summary>The response Content-Type media type, or null.</summary>
    public string? ContentType => ContentHeaders?.ContentType?.MediaType;

    /// <summary>The RFC 5988 <c>Link: &lt;url&gt;; rel="next"</c> URL from the response, or null.</summary>
    public string? NextLink => Headers.TryGetValues("Link", out var values) ? ParseNextLink(string.Join(", ", values)) : null;

    private static string? ParseNextLink(string linkHeader)
    {
        foreach (var part in linkHeader.Split(','))
        {
            var segments = part.Split(';');
            if (segments.Length < 2)
            {
                continue;
            }

            var url = segments[0].Trim().Trim('<', '>', ' ');
            for (var i = 1; i < segments.Length; i++)
            {
                var rel = segments[i].Trim();
                if (rel.StartsWith("rel", StringComparison.OrdinalIgnoreCase)
                    && rel.Contains("next", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Executes a single HTTP request through the shared reliability stack: rate limit, SSRF guard, bounded retry with
/// backoff, and a hard response-size cap. Retries rebuild the request from the factory (a message cannot be resent),
/// so every attempt is a clean request. A 2xx returns the bytes; a non-retryable or exhausted failure throws with
/// the status and a truncated body preview.
/// </summary>
public sealed class HttpExecutor
{
    private readonly HttpClient _client;
    private readonly RetryPolicy _retry;
    private readonly RateLimiter _rateLimiter;
    private readonly UrlGuard _urlGuard;
    private readonly long _maxResponseBytes;
    private readonly TimeProvider _time;

    public HttpExecutor(HttpClient client, RetryPolicy retry, RateLimiter rateLimiter, UrlGuard urlGuard, long maxResponseBytes, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        ArgumentNullException.ThrowIfNull(urlGuard);
        ArgumentNullException.ThrowIfNull(time);
        _client = client;
        _retry = retry;
        _rateLimiter = rateLimiter;
        _urlGuard = urlGuard;
        _maxResponseBytes = maxResponseBytes;
        _time = time;
    }

    /// <param name="requestFactory">Builds a fresh request per attempt.</param>
    /// <param name="allowStatuses">Non-2xx statuses to return instead of throwing (e.g. a 202 pagination sentinel).</param>
    public async Task<HttpFetchResult> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        IReadOnlySet<int>? allowStatuses = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);

        for (var attempt = 1; ; attempt++)
        {
            await _rateLimiter.AcquireAsync(ct).ConfigureAwait(false);

            using var request = requestFactory();
            if (request.RequestUri is { } uri)
            {
                _urlGuard.Check(uri);
            }

            HttpResponseMessage? response = null;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var status = response.StatusCode;
                var code = (int)status;

                if (response.IsSuccessStatusCode || (allowStatuses?.Contains(code) ?? false))
                {
                    var body = await ReadCappedAsync(response, ct).ConfigureAwait(false);
                    return new HttpFetchResult(status, body, response.Headers, response.Content.Headers);
                }

                var decision = _retry.Next(attempt, status, response.Headers);
                if (!decision.ShouldRetry)
                {
                    var preview = await PreviewAsync(response, ct).ConfigureAwait(false);
                    throw new SqlFlowException($"HTTP {code} {status} from {request.RequestUri}: {preview}");
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                var decision = _retry.Next(attempt, null, null);
                if (!decision.ShouldRetry)
                {
                    throw new SqlFlowException($"HTTP transport failure calling {request.RequestUri}: {ex.Message}", ex);
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // A per-request timeout (not caller cancellation): treat as a transient transport failure.
                var decision = _retry.Next(attempt, null, null);
                if (!decision.ShouldRetry)
                {
                    throw new SqlFlowException($"HTTP request to {request.RequestUri} timed out after {_client.Timeout.TotalSeconds:0}s.", ex);
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    private async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[1 << 16];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _maxResponseBytes)
            {
                throw new SqlFlowException(
                    $"Response from {response.RequestMessage?.RequestUri} exceeds the {_maxResponseBytes / (1024 * 1024)} MB limit. " +
                    "Raise reliability.maxResponseBytes or narrow the request window.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task<string> PreviewAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Length <= 512 ? text : text[..512] + "…";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
        {
            return "(response body unavailable)";
        }
    }
}

/// <summary>
/// The concrete auth material to merge onto a request: header additions (already including any prefix and encoding)
/// and query additions. Precomputed once (or per iteration) by the auth resolver so a transport only merges. It
/// never carries the raw secret separately: the values are the final header/query values.
/// </summary>
public sealed record AppliedAuth(
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Query)
{
    public static readonly AppliedAuth None = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.Ordinal));

    public static AppliedAuth Header(string name, string value) => new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = value },
        new Dictionary<string, string>(StringComparer.Ordinal));

    public static AppliedAuth QueryParam(string name, string value) => new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value });

    public static string BasicHeader(string username, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
}
