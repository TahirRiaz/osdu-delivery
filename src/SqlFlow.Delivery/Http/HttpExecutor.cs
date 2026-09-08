// Vendored from SQLFlow (https://github.com/TahirRiaz/sqlflow-v3, commit ddd4ea12160bda044f75dcad2bbec5099c3a7263)
// src/SqlFlow.Acquire/Runtime/HttpExecutor.cs. Changes: namespace, exception types, the charset normalisation and
// code-page provider were dropped (OSDU speaks UTF-8 JSON), and SendAsync exposes the response headers for
// non-JSON bodies. The request-factory contract is unchanged: a fresh request per attempt, so a StreamContent over a
// re-opened blob stream retries correctly (design.md section 12.4).
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SqlFlow.Core;

namespace SqlFlow.Delivery.Http;

/// <summary>The raw outcome of one HTTP call: status, body bytes, and the response/content headers.</summary>
public sealed record HttpFetchResult(
    HttpStatusCode Status,
    byte[] Body,
    HttpResponseHeaders Headers,
    HttpContentHeaders? ContentHeaders)
{
    public string? ContentType => ContentHeaders?.ContentType?.MediaType;

    public string BodyText => Encoding.UTF8.GetString(Body);
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

    /// <param name="requestFactory">Builds a fresh request per attempt (and may open a fresh payload stream).</param>
    /// <param name="allowStatuses">Non-2xx statuses to return instead of throwing.</param>
    /// <param name="ct">Cancels the send, including the backoff wait between attempts.</param>
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
                    throw new HttpStatusException(code, $"HTTP {code} {status} from {request.Method} {Describe(request.RequestUri)}: {preview}");
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                var decision = _retry.Next(attempt, null, null);
                if (!decision.ShouldRetry)
                {
                    throw new DeliveryException($"HTTP transport failure calling {request.Method} {Describe(request.RequestUri)}: {ex.Message}", ex);
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                // A connection reset or premature close while streaming the response body surfaces here as a raw
                // IOException because the send uses ResponseHeadersRead. Treat it as the transient transport failure
                // it is and retry from the factory. The response-size cap throws DeliveryException, so an oversized
                // body is still permanent.
                var decision = _retry.Next(attempt, null, null);
                if (!decision.ShouldRetry)
                {
                    throw new DeliveryException($"HTTP transport failure reading the response from {Describe(request.RequestUri)}: {ex.Message}", ex);
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // A per-request timeout (not caller cancellation): treat as a transient transport failure.
                var decision = _retry.Next(attempt, null, null);
                if (!decision.ShouldRetry)
                {
                    throw new DeliveryException($"HTTP request to {Describe(request.RequestUri)} timed out after {_client.Timeout.TotalSeconds:0}s.", ex);
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    /// <summary>The request URL without its query string: a signed upload URL carries its credential there.</summary>
    private static string Describe(Uri? uri)
        => uri is null ? string.Empty : uri.IsAbsoluteUri ? uri.GetLeftPart(UriPartial.Path) : uri.ToString();

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
                throw new DeliveryException(
                    $"Response from {response.RequestMessage?.RequestUri} exceeds the {_maxResponseBytes / (1024 * 1024)} MB limit. Raise reliability.maxResponseBytes.");
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
            return text.Length <= 512 ? text : text[..512] + "...";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
        {
            return "(response body unavailable)";
        }
    }
}

/// <summary>
/// The concrete auth material to merge onto a request: header additions (already including any prefix and
/// encoding) and query additions. It never carries the raw secret separately.
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

    public static string BasicHeader(string username, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

    /// <summary>Applies the headers to a request (query additions are the caller's job when building the URI).</summary>
    public void ApplyTo(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var (name, value) in Headers)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
