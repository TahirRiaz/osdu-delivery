using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SqlFlow.Delivery.Diagnostics;

/// <summary>
/// The delivery engine's telemetry, on the .NET metrics API (meter <see cref="MeterName"/>): how the tries of records settle
/// per route and how long they take, and how the calls to OSDU and its storage end and how long they take. These are rates
/// to watch and alert on. The delivered, pending, held and failed counts the product shows come from the ledger, never from
/// here. Any listener reads them: <c>dotnet-counters monitor --counters SqlFlow.Delivery</c> on a node, or an OpenTelemetry
/// exporter the host adds. Every tag has few values: a flow, a route, an outcome, a method, a host, a status class.
/// </summary>
public static class DeliveryMetrics
{
    /// <summary>The meter's name, which a listener subscribes to.</summary>
    public const string MeterName = "SqlFlow.Delivery";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> Records = Meter.CreateCounter<long>(
        "osdu_delivery.records", "{record}", "Delivery tries settled, by flow, route and outcome (delivered, unchanged, retry, held, failed).");

    private static readonly Histogram<double> RecordDuration = Meter.CreateHistogram<double>(
        "osdu_delivery.record.duration", "s", "How long a delivery try of one record took, from its claim to its outcome.");

    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "osdu_delivery.http.requests", "{request}", "HTTP call attempts to OSDU services and their storage, by method, host and result (2xx to 5xx, transport, timeout, refused, cancelled, error).");

    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "osdu_delivery.http.request.duration", "s", "How long an HTTP call attempt took, its redirects and response body included.");

    private static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "osdu_delivery.http.retries", "{retry}", "HTTP calls repeated after a passing failure, by method, host and the failure that caused it.");

    /// <summary>Counts a settled delivery try of one record: <paramref name="outcome"/> is delivered, unchanged, retry, held or failed.</summary>
    public static void RecordSettled(string flow, string route, string outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "flow", flow },
            { "route", route },
            { "outcome", outcome },
        };
        Records.Add(1, tags);
        RecordDuration.Record(Math.Max(0, duration.TotalSeconds), tags);
    }

    /// <summary>
    /// Counts an HTTP call attempt that ended: <paramref name="result"/> is the status class of its answer (<c>2xx</c> to
    /// <c>5xx</c>), or what ended it otherwise: <c>transport</c>, <c>timeout</c>, <c>refused</c> (the URL guard),
    /// <c>cancelled</c> (the caller stopped waiting) or <c>error</c> (anything else, such as a redirect loop).
    /// </summary>
    public static void RequestEnded(string method, string host, string result, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "method", method },
            { "host", host },
            { "result", result },
        };
        Requests.Add(1, tags);
        RequestDuration.Record(Math.Max(0, duration.TotalSeconds), tags);
    }

    /// <summary>Counts a call about to be repeated after <paramref name="reason"/> (a status code, <c>transport</c> or <c>timeout</c>).</summary>
    public static void RequestRetried(string method, string host, string reason)
        => Retries.Add(1, new TagList { { "method", method }, { "host", host }, { "reason", reason } });

    /// <summary>The status class a status code falls in: <c>2xx</c>, <c>3xx</c>, <c>4xx</c>, <c>5xx</c>.</summary>
    public static string StatusClass(int status) => status switch
    {
        >= 200 and < 300 => "2xx",
        >= 300 and < 400 => "3xx",
        >= 400 and < 500 => "4xx",
        >= 500 and < 600 => "5xx",
        _ => "other",
    };
}
