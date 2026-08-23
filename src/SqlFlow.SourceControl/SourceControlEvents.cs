using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;

namespace SqlFlow.SourceControl;

/// <summary>
/// The source-control run's voice: turns each stage of a snapshot into a <see cref="FlowEvent"/> on the run's
/// canonical event stream, the same stream every other flow kind publishes to. That stream is what the live
/// trace panel subscribes to and what <c>run.json</c> persists, so wiring it here is what makes a snapshot
/// visible while it runs instead of only after it ends.
///
/// A null sink turns every call into a no-op, so a direct call to <see cref="SourceControlService"/> (the
/// offline tests, a CLI run with no orchestrator) costs nothing and needs no plumbing.
/// </summary>
internal sealed class SourceControlEvents
{
    private readonly IFlowEventSink? _sink;
    private readonly Guid _runId;
    private readonly string _flowName;

    /// <summary>The last category a progress report was published for, so a category change is announced even
    /// when it lands between two round-number reports.</summary>
    private string? _category;

    public SourceControlEvents(IFlowEventSink? sink, Guid runId, string flowName)
    {
        _sink = sink;
        _runId = runId;
        _flowName = flowName;
    }

    public void Info(string stage, string message, long? rows = null, double? elapsedMs = null)
        => Publish(FlowEventLevel.Info, stage, message, rows, elapsedMs);

    public void Warn(string stage, string message) => Publish(FlowEventLevel.Warning, stage, message);

    public void Error(string stage, string message) => Publish(FlowEventLevel.Error, stage, message);

    /// <summary>
    /// The scripter's progress callback. A warning is republished as a warning event; a plain count reports as
    /// "Table: 400 of 1,204 scripted", which is the legacy per-object progress in a form that does not flood the
    /// trace (the scripter reports on a round number, not per object).
    /// </summary>
    public void Progress(ScriptProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.Message is { Length: > 0 } warning)
        {
            Warn("script", warning);
            return;
        }

        _category = progress.Category;
        Publish(
            FlowEventLevel.Debug,
            "script",
            $"{progress.Category}: {progress.Scripted.ToString("N0", CultureInfo.InvariantCulture)} of "
            + $"{progress.Total.ToString("N0", CultureInfo.InvariantCulture)} scripted.",
            progress.Scripted);
    }

    private void Publish(FlowEventLevel level, string stage, string message, long? rows = null, double? elapsedMs = null)
    {
        if (_sink is null)
        {
            return;
        }

        _sink.Publish(new FlowEvent
        {
            RunId = _runId,
            FlowName = _flowName,
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            Stage = stage,
            // Every message crosses into a persisted, user-visible stream, so it passes the same redaction the
            // rest of the run's diagnostics do: a connection string or a token must never reach the trace.
            Message = SecretHygiene.RedactedMessage(message),
            Rows = rows,
            ElapsedMs = elapsedMs,
        });
    }
}
