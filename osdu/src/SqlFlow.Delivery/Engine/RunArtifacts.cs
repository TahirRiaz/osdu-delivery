using System.Text.Json;
using SqlFlow.Core.Runs;
using SqlFlow.Execution;
using SqlFlow.Orchestration;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// The run-history plumbing the module's executors share, built on the platform's own pieces: the canonical run log
/// (<c>run.log</c>), the event collector whose records become the <c>events</c> array of <c>run.json</c>, the bridge
/// that feeds both from one log call, and the artifact write. Every kind this module registers writes the same three
/// files in the same shapes as the platform's built-in kinds, so a delivery run reads back like any other run.
/// </summary>
internal static class RunArtifacts
{
    /// <summary>
    /// The per-run event plumbing: the run log at the asked-for level (echoed live when the host wants it), the
    /// collector that keeps every event for the artifact and forwards it to the host's live sink, and the bridge the
    /// engine logs through so one call reaches both.
    /// </summary>
    public static (RunLogger RunLogger, RunEventCollector Events, RunLogEventBridge Sink) BuildEventPlumbing(
        DocumentExecutionOptions options, string flowName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        var runLogger = new RunLogger(options.LogLevel, options.Echo);
        var events = new RunEventCollector(options.EventSink);
        return (runLogger, events, new RunLogEventBridge(runLogger, events, options.RunId, flowName));
    }

    /// <summary>The canonical <c>run.json</c> envelope of one run of this module's kinds.</summary>
    public static RunArtifact Artifact(
        string kind, string flowName, Guid runId, bool success, string? error, object result, IReadOnlyList<RunEventRecord>? events = null)
        => new()
        {
            FlowKind = kind,
            FlowName = flowName,
            RunId = runId,
            Success = success,
            WrittenUtc = DateTime.UtcNow,
            Error = error,
            Result = result,
            Events = events ?? [],
        };

    /// <summary>
    /// Writes <c>run.json</c>, <c>run.log</c> and the (empty for this module) SQL trace into the run history next to the
    /// flow document, and returns the run folder. The run is over, so a history write that fails warns rather than
    /// failing the run, exactly as the platform's own path does.
    /// </summary>
    public static string? Write(string flowFile, string flowName, Guid runId, RunArtifact artifact, string runLog, string? trace, Action<string>? onWarning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(artifact);
        return RunHistory.Write(
            flowFile,
            flowName,
            runId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["run.json"] = JsonSerializer.Serialize(artifact, ExecutionJson.Options),
                ["run.log"] = runLog,
                ["trace.sql"] = trace ?? string.Empty,
            },
            onWarning);
    }
}
