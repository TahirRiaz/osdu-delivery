using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using SqlFlow.Delivery.Diagnostics;

namespace SqlFlow.Delivery.Tests;

/// <summary>What a listener on the delivery meter hears (docs/go-live-map.md, OPS-1), read through the .NET metrics API.</summary>
internal sealed class MetricsCapture : IDisposable
{
    private readonly MeterListener _listener = new();

    public MetricsCapture()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == DeliveryMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    public ConcurrentQueue<(string Instrument, double Value, IReadOnlyDictionary<string, string?> Tags)> Measurements { get; } = new();

    /// <summary>The measurements of <paramref name="instrument"/> whose <paramref name="tag"/> is <paramref name="value"/>.</summary>
    public List<(double Value, IReadOnlyDictionary<string, string?> Tags)> Of(string instrument, string tag, string value)
        => Measurements
            .Where(m => m.Instrument == instrument && m.Tags.TryGetValue(tag, out var v) && v == value)
            .Select(m => (m.Value, m.Tags))
            .ToList();

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, tagValue) in tags)
        {
            copy[key] = tagValue?.ToString();
        }

        Measurements.Enqueue((instrument.Name, value, copy));
    }
}
