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
///
/// Only a request that is safe to repeat is ever repeated. Repeating one that is not turns a lost response into a
/// second effect: a session chunk sent twice lands twice in the committed bulk, a file registration sent twice
/// leaves an orphan dataset record. The method decides by default (GET, HEAD, PUT, DELETE, OPTIONS and TRACE are
/// idempotent per RFC 9110; POST and PATCH are not), and a caller that knows a POST is safe by the service's own
/// semantics (a search, a read by ids, a replace-the-whole-bulk write) says so. A request that is not safe to repeat
/// is sent once: after a status it will not retry, and equally after a transport failure, where the service may
/// well have acted on it before the connection went. The OSDU C# client draws the same line
/// (<c>ReadRetryHandler</c>). The durable retry above this is the worker's, which resumes from the steps the ledger
/// recorded rather than replaying blind.
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
    /// <param name="idempotent">
    /// Whether the request may be repeated. Null takes it from the method; true marks a POST or PATCH the service
    /// treats as safe to repeat; false forbids repeating even an idempotent method.
    /// </param>
    /// <param name="ct">Cancels the send, including the backoff wait between attempts.</param>
    public async Task<HttpFetchResult> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        IReadOnlySet<int>? allowStatuses = null,
        bool? idempotent = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);

        bool? repeatable = null;
        for (var attempt = 1; ; attempt++)
        {
            await _rateLimiter.AcquireAsync(ct).ConfigureAwait(false);

            using var request = requestFactory();
            repeatable ??= idempotent ?? IsIdempotent(request.Method);
            if (request.RequestUri is { } uri)
            {
                _urlGuard.Check(uri);
            }

            // A request that must not be repeated is decided as if it were already on its last attempt.
            var decisionAttempt = repeatable.Value ? attempt : int.MaxValue;

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

                var decision = _retry.Next(decisionAttempt, status, response.Headers);
                if (!decision.ShouldRetry)
                {
                    var preview = await PreviewAsync(response, ct).ConfigureAwait(false);
                    throw new HttpStatusException(code, $"HTTP {code} {status} from {request.Method} {Describe(request.RequestUri)}{CorrelationNote(response, request)}: {preview}", decision.RetryAfter);
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                var decision = _retry.Next(decisionAttempt, null, null);
                if (!decision.ShouldRetry)
                {
                    throw new DeliveryException($"HTTP transport failure calling {request.Method} {Describe(request.RequestUri)}{CorrelationNote(null, request)}: {ex.Message}", ex);
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                // A connection reset or premature close while streaming the response body surfaces here as a raw
                // IOException because the send uses ResponseHeadersRead. Treat it as the transient transport failure
                // it is and retry from the factory. The response-size cap throws DeliveryException, so an oversized
                // body is still permanent.
                var decision = _retry.Next(decisionAttempt, null, null);
                if (!decision.ShouldRetry)
                {
                    throw new DeliveryException($"HTTP transport failure reading the response from {Describe(request.RequestUri)}: {ex.Message}", ex);
                }

                await Task.Delay(decision.Delay, _time, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // A per-request timeout (not caller cancellation): treat as a transient transport failure.
                var decision = _retry.Next(decisionAttempt, null, null);
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

    /// <summary>RFC 9110 section 9.2.2: the methods whose repetition has the same effect as sending them once.</summary>
    internal static bool IsIdempotent(HttpMethod method)
        => method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Put
            || method == HttpMethod.Delete || method == HttpMethod.Options || method == HttpMethod.Trace;

    /// <summary>
    /// The correlation id to quote for a failed call, so it can be found in the service's logs: the id the service
    /// answered with, else the one the call sent; nothing when there is neither.
    /// </summary>
    private static string CorrelationNote(HttpResponseMessage? response, HttpRequestMessage request)
    {
        string? id = null;
        if (response is not null && response.Headers.TryGetValues(OsduCorrelation.HeaderName, out var answered))
        {
            id = answered.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(id) && request.Headers.TryGetValues(OsduCorrelation.HeaderName, out var sent))
        {
            id = sent.FirstOrDefault();
        }

        return string.IsNullOrWhiteSpace(id) ? string.Empty : $" (correlation-id {id})";
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

    /// <summary>What the service said about the failure: its own message when the body is an OSDU error shape (see <see cref="OsduError"/>), else a bounded preview.</summary>
    private static async Task<string> PreviewAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return OsduError.Describe(text);
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
