using Microsoft.Extensions.Logging;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Routes the engine's <see cref="ILogger"/> output into one run's canonical log and live event stream, so what
/// the planner, the worker and the verifier say lands in <c>run.log</c>, the run's event array and the GUI's live
/// trace, exactly as any other kind's steps do. Trace entries stay in the log only (the platform's own bridge
/// rule); warnings and errors keep their level on the event and are marked in the log text, which has no level
/// above Info.
/// </summary>
public sealed class RunLogLoggerFactory : ILoggerFactory
{
    private readonly RunLogger _log;
    private readonly IFlowEventSink _events;
    private readonly Guid _runId;
    private readonly string _flowName;

    public RunLogLoggerFactory(RunLogger log, IFlowEventSink events, Guid runId, string flowName)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(events);
        _log = log;
        _events = events;
        _runId = runId;
        _flowName = flowName;
    }

    public ILogger CreateLogger(string categoryName) => new RunLogLogger(this, StepName(categoryName));

    /// <summary>The run log is the only provider; nothing else is attached.</summary>
    public void AddProvider(ILoggerProvider provider)
    {
        // The run log is the only provider; nothing else is attached.
    }

    public void Dispose()
    {
        // Nothing to release: the run logger and the event sink belong to the run.
    }

    /// <summary>Writes one line under a step name, with the platform's level rules.</summary>
    public void Write(LogLevel level, string step, string message)
    {
        var (runLevel, eventLevel, prefix) = level switch
        {
            LogLevel.Trace => (RunLogLevel.Trace, FlowEventLevel.Trace, string.Empty),
            LogLevel.Debug => (RunLogLevel.Debug, FlowEventLevel.Debug, string.Empty),
            LogLevel.Warning => (RunLogLevel.Info, FlowEventLevel.Warning, "warning: "),
            LogLevel.Error or LogLevel.Critical => (RunLogLevel.Info, FlowEventLevel.Error, "error: "),
            _ => (RunLogLevel.Info, FlowEventLevel.Info, string.Empty),
        };

        _log.Log(runLevel, step, prefix + message);
        if (runLevel == RunLogLevel.Trace)
        {
            return;
        }

        _events.Publish(new FlowEvent
        {
            RunId = _runId,
            FlowName = _flowName,
            Timestamp = DateTimeOffset.UtcNow,
            Level = eventLevel,
            Stage = step,
            Message = message,
        });
    }

    private static string StepName(string category)
    {
        var name = category.Length == 0 ? "engine" : category[(category.LastIndexOf('.') + 1)..];
        return name switch
        {
            "Planner" => "plan",
            "SubmissionIntake" => "intake",
            "DeliveryWorker" => "deliver",
            "Verifier" => "verify",
            "SqlServerIngestionSource" => "source",
            "LoggingDeliveryListener" => "record",
            _ => name.Length == 0 ? "engine" : char.ToLowerInvariant(name[0]) + name[1..],
        };
    }

    private sealed class RunLogLogger : ILogger
    {
        private readonly RunLogLoggerFactory _owner;
        private readonly string _step;

        public RunLogLogger(RunLogLoggerFactory owner, string step)
        {
            _owner = owner;
            _step = step;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel == LogLevel.None)
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null && !message.Contains(exception.Message, StringComparison.Ordinal))
            {
                message = $"{message} ({exception.GetType().Name}: {exception.Message})";
            }

            _owner.Write(logLevel, _step, message);
        }
    }
}
