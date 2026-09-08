// Vendored from SQLFlow (https://github.com/TahirRiaz/sqlflow-v3, commit ddd4ea12160bda044f75dcad2bbec5099c3a7263)
// src/SqlFlow.Acquire/Runtime/RetryPolicy.cs. Changes: namespace; configuration record is the flow's FlowRetry;
// a fixed-backoff option was added.
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Http;

/// <summary>The outcome of a retry decision: stop (surface the failure) or wait the given delay and retry.</summary>
public readonly record struct RetryDecision(bool ShouldRetry, TimeSpan Delay)
{
    public static readonly RetryDecision Stop = new(false, TimeSpan.Zero);

    public static RetryDecision Retry(TimeSpan delay) => new(true, delay);
}

/// <summary>
/// Exponential backoff with a retryable-status allowlist and Retry-After honouring. Transport failures (no status)
/// and the transient statuses 408/425/429/500/502/503/504 retry up to the attempt cap; every other status is
/// permanent. Retry-After is a floor raised to (never below) the computed backoff and bounded by the max delay.
/// </summary>
public sealed class RetryPolicy
{
    private static readonly HashSet<int> Retryable = [408, 425, 429, 500, 502, 503, 504];

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

    /// <param name="attempt">1-based attempt that just failed.</param>
    /// <param name="status">The HTTP status, or null for a transport failure (DNS/TCP/TLS/timeout).</param>
    /// <param name="headers">Response headers, for Retry-After; null for a transport failure.</param>
    public RetryDecision Next(int attempt, HttpStatusCode? status, HttpResponseHeaders? headers)
    {
        if (attempt >= _config.Attempts)
        {
            return RetryDecision.Stop;
        }

        var code = status is { } s ? (int)s : (int?)null;
        if (code is { } c && !Retryable.Contains(c))
        {
            return RetryDecision.Stop;
        }

        var backoff = Backoff(attempt);
        if (_config.HonorRetryAfter && headers?.RetryAfter is { } retryAfter && ParseRetryAfter(retryAfter) is { } floor)
        {
            backoff = floor > backoff ? floor : backoff;
        }

        var max = TimeSpan.FromMilliseconds(_config.MaxDelayMs);
        return RetryDecision.Retry(backoff > max ? max : backoff);
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
