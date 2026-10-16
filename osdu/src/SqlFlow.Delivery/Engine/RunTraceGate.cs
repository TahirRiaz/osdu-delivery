using Microsoft.Extensions.Logging;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// The gate between the engine and one run's log: every line the engine's parts log while they serve the run (the source,
/// the planner, the intake, the workers, the protocols, the record search, the calls to OSDU) passes here, under the step
/// the line is filed under, and only what the run's <see cref="RunTrace"/> admits goes on to the run's log and live
/// trace. It is what keeps a run of millions of records from writing millions of lines, whichever part would write them,
/// including one added later; the parts themselves log as they always have.
/// </summary>
internal sealed class RunTraceGate : ILoggerFactory
{
    private readonly ILoggerFactory _inner;
    private readonly RunTrace _trace;

    public RunTraceGate(ILoggerFactory inner, RunTrace trace)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(trace);
        _inner = inner;
        _trace = trace;
    }

    public ILogger CreateLogger(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);
        return new GatedLogger(_inner.CreateLogger(categoryName), _trace, RunLogLoggerFactory.StepName(categoryName));
    }

    /// <summary>Providers belong to the run's log, which the gate only stands in front of.</summary>
    public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

    public void Dispose()
    {
        // The run's log belongs to the run, which disposes it.
    }

    private sealed class GatedLogger(ILogger inner, RunTrace trace, string step) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel == LogLevel.None)
            {
                return;
            }

            var admission = trace.Admit(step, logLevel, eventId);
            if (admission.Write)
            {
                inner.Log(logLevel, eventId, state, exception, formatter);
            }

            if (admission.Note is { } note)
            {
                inner.Log(LogLevel.Information, RunTrace.Bounded, note, null, static (text, _) => text);
            }
        }
    }
}
