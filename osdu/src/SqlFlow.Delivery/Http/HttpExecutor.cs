// Vendored from SQLFlow (https://github.com/TahirRiaz/sqlflow-v3, commit ddd4ea12160bda044f75dcad2bbec5099c3a7263)
// src/SqlFlow.Acquire/Runtime/HttpExecutor.cs. Changes: namespace, exception types, the charset normalisation and
// code-page provider were dropped (OSDU speaks UTF-8 JSON), and SendAsync exposes the response headers for
// non-JSON bodies; redirects are followed here, each hop checked by the URL guard; every attempt and every retry is
// reported to an optional observer. The request-factory contract is unchanged: a fresh request per attempt (and per
// redirect), so a StreamContent over a re-opened blob stream retries correctly (design.md section 12.4).
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SqlFlow.Core;
using SqlFlow.Delivery.Diagnostics;

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
/// Executes a single HTTP request through the shared reliability stack: rate limit, SSRF guard (on the request and on
/// every redirect it follows), bounded retry with backoff, and a hard response-size cap. Retries rebuild the request from the factory (a message cannot be resent),
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
    private readonly IHttpObserver? _observer;

    /// <param name="client">The client every attempt is sent with.</param>
    /// <param name="retry">Decides whether and when a failed attempt is repeated.</param>
    /// <param name="rateLimiter">Paces the attempts to the flow's rate.</param>
    /// <param name="urlGuard">Checks every URL, and every redirect, against the addresses the deployment reaches.</param>
    /// <param name="maxResponseBytes">The largest response body read.</param>
    /// <param name="time">The clock the backoff waits on.</param>
    /// <param name="observer">Told of every attempt as it ends and of every retry before its wait; null tells nobody.</param>
    public HttpExecutor(HttpClient client, RetryPolicy retry, RateLimiter rateLimiter, UrlGuard urlGuard, long maxResponseBytes, TimeProvider time, IHttpObserver? observer = null)
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
        _observer = observer;
    }

    /// <summary>The most redirects one request follows.</summary>
    public const int MaxRedirects = 5;

    /// <summary>The request headers a redirect to another host keeps: none that could carry a credential.</summary>
    private static readonly HashSet<string> CarriedAcrossHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Encoding", "Accept-Language", "User-Agent", OsduCorrelation.HeaderName,
    };

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

            using var first = requestFactory();
            repeatable ??= idempotent ?? IsIdempotent(first.Method);
            var method = first.Method.Method;
            var host = first.RequestUri is { IsAbsoluteUri: true } absolute ? absolute.IdnHost : "(none)";
            if (first.RequestUri is { } uri)
            {
                try
                {
                    _urlGuard.Check(uri);
                }
                catch (UrlRefusedException)
                {
                    DeliveryMetrics.RequestEnded(method, host, "refused", TimeSpan.Zero);
                    throw;
                }
            }

            // A request that must not be repeated is decided as if it were already on its last attempt.
            var decisionAttempt = repeatable.Value ? attempt : int.MaxValue;

            using var hops = new Hops(first);
            HttpResponseMessage? response = null;

            // Each attempt is counted once, when its response is released: by what it ended with (any failure named
            // below by its cause, any other, such as a redirect loop, as an error) and how long it took, its body included
            // and the wait before the next attempt not.
            var began = _time.GetTimestamp();
            var result = "error";
            var wait = TimeSpan.Zero;

            // What the observer is told of the attempt: the status that came, or what ended it without one.
            int? answered = null;
            string? failure = null;
            try
            {
                response = await SendFollowingAsync(hops, requestFactory, allowStatuses, ct).ConfigureAwait(false);
                var request = hops.Current;
                var status = response.StatusCode;
                var code = (int)status;
                answered = code;
                result = DeliveryMetrics.StatusClass(code);

                if (response.IsSuccessStatusCode || (allowStatuses?.Contains(code) ?? false))
                {
                    var body = await ReadCappedAsync(response, ct).ConfigureAwait(false);
                    return new HttpFetchResult(status, body, response.Headers, response.Content.Headers);
                }

                var decision = _retry.Next(decisionAttempt, status, response.Headers);
                if (!decision.ShouldRetry)
                {
                    var preview = await PreviewAsync(response, ct).ConfigureAwait(false);
                    throw new OsduStatusException(code, $"HTTP {code} {status} from {request.Method} {Describe(request.RequestUri)}{CorrelationNote(response, request)}: {preview}", decision.RetryAfter);
                }

                DeliveryMetrics.RequestRetried(method, host, code.ToString(System.Globalization.CultureInfo.InvariantCulture));
                wait = decision.Delay;
            }
            catch (UrlRefusedException ex)
            {
                // A redirect the guard refused.
                result = "refused";
                failure = ex.Message;
                throw;
            }
            catch (HttpRequestException ex) when (Refusal(ex) is { } refused)
            {
                // The connection would have opened to an address the deployment does not reach: a verdict on the URL,
                // which no retry changes.
                result = "refused";
                failure = refused.Message;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(refused);
            }
            catch (HttpRequestException ex)
            {
                result = "transport";
                failure = $"transport failure: {ex.Message}";
                var decision = _retry.Next(decisionAttempt, null, null);
                if (!decision.ShouldRetry)
                {
                    throw new DeliveryException($"HTTP transport failure calling {hops.Current.Method} {Describe(hops.Current.RequestUri)}{CorrelationNote(null, hops.Current)}: {ex.Message}", ex);
                }

                DeliveryMetrics.RequestRetried(method, host, result);
                wait = decision.Delay;
            }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                // A connection reset or premature close while streaming the response body surfaces here as a raw
                // IOException because the send uses ResponseHeadersRead. Treat it as the transient transport failure
                // it is and retry from the factory. The response-size cap throws DeliveryException, so an oversized
                // body is still permanent.
                result = "transport";
                failure = $"the response was cut off: {ex.Message}";
                var decision = _retry.Next(decisionAttempt, null, null);
                if (!decision.ShouldRetry)
                {
                    throw new DeliveryException($"HTTP transport failure reading the response from {Describe(hops.Current.RequestUri)}: {ex.Message}", ex);
                }

                DeliveryMetrics.RequestRetried(method, host, result);
                wait = decision.Delay;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // A per-request timeout (not caller cancellation): treat as a transient transport failure.
                result = "timeout";
                failure = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"timed out after {_client.Timeout.TotalSeconds:0}s");
                var decision = _retry.Next(decisionAttempt, null, null);
                if (!decision.ShouldRetry)
                {
                    throw new DeliveryException($"HTTP request to {Describe(hops.Current.RequestUri)} timed out after {_client.Timeout.TotalSeconds:0}s.", ex);
                }

                DeliveryMetrics.RequestRetried(method, host, result);
                wait = decision.Delay;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller stopped waiting, even after the status arrived.
                result = "cancelled";
                failure ??= "cancelled";
                throw;
            }
            catch (Exception ex) when (answered is null && failure is null)
            {
                // Anything else that ended the attempt before a status came (a redirect loop): the observer is told what.
                failure = ex.Message;
                throw;
            }
            finally
            {
                // Released before the wait, so an attempt that is repeated does not hold its connection through the backoff.
                response?.Dispose();
                var elapsed = _time.GetElapsedTime(began);
                DeliveryMetrics.RequestEnded(method, host, result, elapsed);
                _observer?.Ended(new HttpAttempt(method, Describe(hops.Current.RequestUri), attempt, answered, Redacted(failure), elapsed));
            }

            _observer?.Retrying(new HttpRetry(
                method, Describe(hops.Current.RequestUri), attempt, _retry.MaxAttempts,
                answered is { } retried ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"HTTP {retried}") : Redacted(failure) ?? result,
                wait));
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends the request and follows the redirects it is answered with, at most <see cref="MaxRedirects"/>, the way
    /// SocketsHttpHandler would (300, 301 and 302 turn a POST into a GET, 303 turns anything but a HEAD into one, 307 and
    /// 308 keep the method and the body), except that every hop passes the URL guard first and a hop to another host
    /// carries none of the request's credentials or the flow's headers. A redirect status the caller takes as an answer
    /// (<paramref name="allowStatuses"/>, a resumable upload's 308) and one without a <c>Location</c> are not followed.
    /// </summary>
    private async Task<HttpResponseMessage> SendFollowingAsync(Hops hops, Func<HttpRequestMessage> factory, IReadOnlySet<int>? allowStatuses, CancellationToken ct)
    {
        var response = await _client.SendAsync(hops.Current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        for (var followed = 0; ; followed++)
        {
            var code = (int)response.StatusCode;
            if (code is not (300 or 301 or 302 or 303 or 307 or 308)
                || (allowStatuses?.Contains(code) ?? false)
                || response.Headers.Location is not { } location)
            {
                return response;
            }

            try
            {
                var from = hops.Current.RequestUri
                    ?? throw new DeliveryException("A redirected request has no URL to resolve its redirect against.");
                var target = location.IsAbsoluteUri ? location : new Uri(from, location);
                if (followed == MaxRedirects)
                {
                    throw new DeliveryException(
                        $"{Describe(hops.First.RequestUri)} was redirected more than {MaxRedirects} times; the last redirect named {Describe(target)}.");
                }

                _urlGuard.CheckRedirect(from, target);
                var next = factory();
                hops.Add(next);
                var method = ForcesGet(code, hops.Previous.Method) ? HttpMethod.Get : hops.Previous.Method;
                if (method != next.Method)
                {
                    next.Content?.Dispose();
                    next.Content = null;
                    next.Method = method;
                }

                next.RequestUri = target;
                if (!SameAuthority(hops.First.RequestUri!, target))
                {
                    foreach (var name in next.Headers.Select(h => h.Key).Where(n => !CarriedAcrossHosts.Contains(n)).ToList())
                    {
                        next.Headers.Remove(name);
                    }
                }
            }
            finally
            {
                response.Dispose();
            }

            response = await _client.SendAsync(hops.Current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Whether a redirect with <paramref name="status"/> turns a request of <paramref name="method"/> into a GET without a body.</summary>
    private static bool ForcesGet(int status, HttpMethod method) => status switch
    {
        300 or 301 or 302 => method == HttpMethod.Post,
        303 => method != HttpMethod.Get && method != HttpMethod.Head,
        _ => false,
    };

    /// <summary>Whether <paramref name="target"/> is the host <paramref name="origin"/> named, on its port or on https's where the origin was plain http.</summary>
    private static bool SameAuthority(Uri origin, Uri target)
        => string.Equals(origin.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase)
           && (origin.Port == target.Port
               || (origin.Scheme == Uri.UriSchemeHttp && target.Scheme == Uri.UriSchemeHttps && origin.IsDefaultPort && target.IsDefaultPort));

    /// <summary>The URL guard's refusal a failed connection carries, when that is why it failed.</summary>
    private static UrlRefusedException? Refusal(Exception failure)
    {
        for (var current = failure.InnerException; current is not null; current = current.InnerException)
        {
            if (current is UrlRefusedException refused)
            {
                return refused;
            }
        }

        return null;
    }

    /// <summary>The requests one attempt sent: the first, and one per redirect it followed, each disposed with the attempt.</summary>
    private sealed class Hops(HttpRequestMessage first) : IDisposable
    {
        private readonly List<HttpRequestMessage> _followed = [];

        public HttpRequestMessage First { get; } = first;

        /// <summary>The request in flight.</summary>
        public HttpRequestMessage Current => _followed.Count == 0 ? First : _followed[^1];

        /// <summary>The request before the one in flight.</summary>
        public HttpRequestMessage Previous => _followed.Count <= 1 ? First : _followed[^2];

        public void Add(HttpRequestMessage next) => _followed.Add(next);

        public void Dispose()
        {
            foreach (var request in _followed)
            {
                request.Dispose();
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

    /// <summary>What ended an attempt, as the observer is told it: with any credential the text carries redacted.</summary>
    private static string? Redacted(string? failure) => failure is null ? null : HeaderRedaction.RedactMessage(failure);

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
