using Microsoft.Extensions.Logging;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// The engine's loggers for one interface of a source: every line says which interface it is about, so the run log and
/// the live trace of a source whose interfaces run side by side read one interface at a time. Everything else (the step
/// names, the levels, where the lines go) is the wrapped factory's.
/// </summary>
public sealed class InterfaceLoggerFactory : ILoggerFactory
{
    private readonly ILoggerFactory _inner;
    private readonly string _prefix;

    public InterfaceLoggerFactory(ILoggerFactory inner, string interfaceName)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);
        _inner = inner;
        _prefix = $"[{interfaceName}] ";
    }

    public ILogger CreateLogger(string categoryName) => new PrefixedLogger(_inner.CreateLogger(categoryName), _prefix);

    /// <summary>Providers belong to the wrapped factory, which the run owns.</summary>
    public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

    public void Dispose()
    {
        // The wrapped factory belongs to the run, which disposes it.
    }

    private sealed class PrefixedLogger(ILogger inner, string prefix) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            inner.Log(logLevel, eventId, state, exception, (s, e) => prefix + formatter(s, e));
        }
    }
}
