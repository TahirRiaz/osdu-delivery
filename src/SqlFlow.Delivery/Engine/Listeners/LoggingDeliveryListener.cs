using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Listeners;

/// <summary>The default completion callback: one structured log line per event, so every completion is traceable in the host's logs.</summary>
public sealed class LoggingDeliveryListener : IDeliveryListener
{
    private readonly ILogger<LoggingDeliveryListener> _logger;

    public LoggingDeliveryListener(ILogger<LoggingDeliveryListener> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public ValueTask OnEventAsync(DeliveryEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var level = evt.Kind switch
        {
            "record.held" or "record.failed" or "verify.drifted" => LogLevel.Warning,
            "record.retry" => LogLevel.Information,
            _ => LogLevel.Information,
        };
        _logger.Log(
            level,
            "{Kind} flow={Flow} submission={Submission} key={Key} source={SourceKey} label={Label} target={TargetId} version={Version} worker={Worker} phase={Phase} duration={Duration} detail={Detail}",
            evt.Kind, evt.FlowName, evt.SubmissionId, evt.DeliveryKey, evt.SourceKey, evt.Label, evt.TargetId, evt.TargetVersion, evt.Worker, evt.Phase, evt.Duration, evt.Detail);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Keeps the most recent events in memory for a live activity feed. Bounded: the ledger's attempts and activities
/// are the durable history; this is the last few minutes at a glance.
/// </summary>
public sealed class RecentEventsListener : IDeliveryListener
{
    private readonly ConcurrentQueue<DeliveryEvent> _events = new();
    private readonly int _capacity;
    private long _sequence;

    public RecentEventsListener(int capacity = 1000)
    {
        _capacity = Math.Max(capacity, 10);
    }

    /// <summary>The sequence number of the newest event, so a client can poll for what it has not seen.</summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    public ValueTask OnEventAsync(DeliveryEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        _events.Enqueue(evt);
        Interlocked.Increment(ref _sequence);
        while (_events.Count > _capacity && _events.TryDequeue(out _))
        {
        }

        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<DeliveryEvent> Recent(int max = 200, Guid? flowId = null)
    {
        IEnumerable<DeliveryEvent> events = _events.Reverse();
        if (flowId is { } f)
        {
            events = events.Where(e => e.FlowId == f);
        }

        return events.Take(Math.Clamp(max, 1, _capacity)).ToList();
    }
}
