using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Listeners;

/// <summary>
/// The completion callback that logs: submission, batch and verify events at information level (the operations
/// an operator follows), record-level events at debug (fifty million records must never become fifty million
/// lines in a host's log; the ledger's attempts are the per-record history). Holds and failures of individual
/// records surface at debug too; the worker itself logs the first few of each batch as warnings.
/// </summary>
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
        var level = evt.Kind.StartsWith("record.", StringComparison.Ordinal) ? LogLevel.Debug : LogLevel.Information;
        if (_logger.IsEnabled(level))
        {
            _logger.Log(
                level,
                "{Kind} flow={Flow} submission={Submission} key={Key} source={SourceKey} label={Label} target={TargetId} version={Version} worker={Worker} phase={Phase} duration={Duration} detail={Detail}",
                evt.Kind, evt.FlowName, evt.SubmissionId, evt.DeliveryKey, evt.SourceKey, evt.Label, evt.TargetId, evt.TargetVersion, evt.Worker, evt.Phase, evt.Duration, evt.Detail);
        }

        return ValueTask.CompletedTask;
    }
}
