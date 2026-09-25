// Vendored from SQLFlow (https://github.com/TahirRiaz/sqlflow-v3, commit ddd4ea12160bda044f75dcad2bbec5099c3a7263)
// src/SqlFlow.Acquire/Runtime/RetryPolicy.cs. Changes: namespace; configuration record is the flow's FlowRetry;
// a fixed-backoff option was added; the attempt ceiling is exposed for the executor's observer.
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// The outcome of a retry decision: stop (surface the failure) or wait the given delay and retry. A stop also
/// carries the wait the service asked for, when it named one, so whoever tries again later knows the earliest it
/// may.
/// </summary>
public readonly record struct RetryDecision(bool ShouldRetry, TimeSpan Delay, TimeSpan? RetryAfter = null)
{
    public static readonly RetryDecision Stop = new(false, TimeSpan.Zero);

    public static RetryDecision Retry(TimeSpan delay) => new(true, delay);

    public static RetryDecision StopFor(TimeSpan? retryAfter) => new(false, TimeSpan.Zero, retryAfter);
}

/// <summary>
/// Exponential backoff with a transient-status allowlist and Retry-After honouring, for requests that are safe to
/// repeat (the executor decides that; this decides when).
///
/// Transient means the service said so: 429 and 503 (busy), 504 (a gateway gave up waiting), and 408 and 425, which
/// RFC 9110 and RFC 8470 define as safe to repeat. 500 and 502 are not repeated here. The OSDU services answer 500
/// for failures that are deterministic as often as for passing ones (an unexpected record shape, a legal tag the
/// service cannot resolve), so an immediate replay tends to fail again; the OSDU C# client declines to retry them
/// for the same reason. They are not given up on: the worker retries the record on its own backoff, measured in
/// minutes rather than milliseconds.
///
/// Retry-After is honoured and never shortened. A requested wait no longer than the max delay becomes the floor of
/// the backoff; a longer one is not waited out inline, because that would pin a worker for as long as the service
/// likes, so the request stops and the requested wait travels with the failure for the record-level retry to honour.
/// </summary>
public sealed class RetryPolicy
{
    private static readonly HashSet<int> Retryable = [408, 425, 429, 503, 504];

    private readonly FlowRetry _config;
    private readonly TimeProvider _time;

    public RetryPolicy(FlowRetry config, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        _config = config;
        _time = time;
    }

    public static bool IsRetryableStatus(int code) => Retryable.Contains(code);

    /// <summary>The most attempts one request that is safe to repeat is given, the first included.</summary>
    public int MaxAttempts => _config.Attempts;

    /// <param name="attempt">1-based attempt that just failed.</param>
    /// <param name="status">The HTTP status, or null for a transport failure (DNS/TCP/TLS/timeout).</param>
    /// <param name="headers">Response headers, for Retry-After; null for a transport failure.</param>
    public RetryDecision Next(int attempt, HttpStatusCode? status, HttpResponseHeaders? headers)
    {
        var requested = _config.HonorRetryAfter && headers?.RetryAfter is { } retryAfter ? ParseRetryAfter(retryAfter) : null;
        var code = status is { } s ? (int)s : (int?)null;
        if (code is { } c && !Retryable.Contains(c))
        {
            return RetryDecision.StopFor(requested);
        }

        if (attempt >= _config.Attempts)
        {
            return RetryDecision.StopFor(requested);
        }

        var max = TimeSpan.FromMilliseconds(_config.MaxDelayMs);
        if (requested is { } wait && wait > max)
        {
            // Waiting less than the service asked would be a retry it told us not to make.
            return RetryDecision.StopFor(wait);
        }

        var backoff = Backoff(attempt);
        if (backoff > max)
        {
            backoff = max;
        }

        return RetryDecision.Retry(requested is { } floor && floor > backoff ? floor : backoff);
    }

    private TimeSpan Backoff(int attempt)
    {
        if (_config.Backoff == BackoffKind.Fixed)
        {
            return TimeSpan.FromMilliseconds(Math.Min(_config.BaseDelayMs, _config.MaxDelayMs));
        }

        var exponent = Math.Min(attempt - 1, 20);
        var millis = (double)_config.BaseDelayMs * Math.Pow(2, exponent);
        var capped = Math.Min(millis, _config.MaxDelayMs);
        return TimeSpan.FromMilliseconds(capped);
    }

    private TimeSpan? ParseRetryAfter(RetryConditionHeaderValue retryAfter)
    {
        if (retryAfter.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    /// <summary>Parses a raw Retry-After header value (delta-seconds or HTTP-date) for callers holding the string form.</summary>
    public static bool TryParseHeader(string value, TimeProvider time, out TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(time);
        delay = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            delay = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var wait = date - time.GetUtcNow();
            delay = wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Record-level backoff between worker passes (design.md section 7.5): exponential in minutes from the flow's
    /// recordBaseDelayMinutes, capped at recordMaxDelayMinutes.
    /// </summary>
    public static TimeSpan RecordBackoff(FlowRetry config, int attempt)
    {
        ArgumentNullException.ThrowIfNull(config);
        var exponent = Math.Clamp(attempt - 1, 0, 20);
        var minutes = Math.Min(config.RecordBaseDelayMinutes * Math.Pow(2, exponent), config.RecordMaxDelayMinutes);
        return TimeSpan.FromMinutes(Math.Max(minutes, 0));
    }
}
