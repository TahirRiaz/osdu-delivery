using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Yaml;

namespace SqlFlow.Execution;

/// <summary>Run-time options threaded into a single document run: the subset that matters whoever drives the run
/// (the CLI, a worker node, a schedule fire).</summary>
public sealed record DocumentExecutionOptions
{
    public RunLogLevel LogLevel { get; init; } = RunLogLevel.Info;

    /// <summary>Where the live run log is echoed, line by line; null keeps it to the run-history file only. A
    /// single CLI run echoes to the console; a server-side run leaves this null.</summary>
    public Action<string>? Echo { get; init; }

    /// <summary>An orchestrator-assigned run id stamped on the run instead of the runner minting its own. The
    /// control-plane trigger hands this id back before the run executes, so the recorded run resolves at
    /// <c>GET /api/v1/runs/{id}</c>. Null mints a fresh id (every direct CLI run).</summary>
    public Guid? RunId { get; init; }

    /// <summary>Which flow of the document to execute, for a document that could declare more than one. The node
    /// worker passes the claimed run's flow name; null (every plain CLI run) executes the document's flow. A set
    /// name that does not match the document is refused rather than silently running the wrong flow.</summary>
    public string? FlowName { get; init; }

    /// <summary>Per-run parameters: trigger-time operational overrides applied for this run only. Defaults to
    /// <see cref="RunParameters.None"/>, which changes nothing.</summary>
    public RunParameters Parameters { get; init; } = RunParameters.None;

    /// <summary>Who asked for the run, for kinds that keep an audit trail: the run's trigger source on a node
    /// (manual:&lt;user&gt;, schedule:&lt;name&gt;), cli:&lt;user&gt; for a direct CLI run. Null records "unknown".</summary>
    public string? Actor { get; init; }

    /// <summary>Receives every canonical run event as it happens, for live persistence: the node streams them into
    /// the catalog so the run detail updates while the run executes. Null (the default, and every direct CLI run)
    /// records nothing live; the events are still collected into the run.json <c>events</c> array and projected
    /// at completion, so the artifact stays authoritative either way.</summary>
    public IFlowEventSink? EventSink { get; init; }

    /// <summary>True when the flow executes from a copy of its repository the node made for runs: a staged YAML version
    /// or a commit checkout, shared by version and never pushed back. Whatever a run writes beside its flow there is seen
    /// by no other run, so a kind that keeps durable state next to its documents must not write it there. The CLI and a
    /// run from the node's synced repository root leave it false.</summary>
    public bool EphemeralWorkingCopy { get; init; }
}

/// <summary>The uniform outcome of running one flow document, whatever its kind: the terminal facts. The
/// kind-specific detail already lives in the run's own run-history folder.</summary>
public sealed record DocumentRunOutcome
{
    public required string FlowName { get; init; }

    public required string FlowKind { get; init; }

    public required bool Success { get; init; }

    public string? Error { get; init; }

    public Guid RunId { get; init; }

    public string? RunDirectory { get; init; }

    public double DurationSeconds { get; init; }
}

/// <summary>Runs one flow document end to end (build the runner, execute, write the run-history artifacts) and
/// returns a uniform outcome. This is the single execution pathway shared by the CLI's <c>run</c> verb and the
/// worker node, so every run goes through exactly the same code.</summary>
public interface IDocumentRunner
{
    /// <param name="flowFile">The absolute path to the flow document.</param>
    /// <param name="options">Run-time options threaded into the run.</param>
    /// <param name="ct">Cancellation for the run.</param>
    Task<DocumentRunOutcome> RunAsync(string flowFile, DocumentExecutionOptions options, CancellationToken ct = default);
}

/// <summary>The rich result of one document run: the terminal facts plus the kind-specific result object (what the
/// CLI's <c>--json</c> prints and what <c>run.json</c> carries under <c>result</c>).</summary>
public sealed record DocumentExecutionResult
{
    public required string FlowName { get; init; }
    public required string FlowKind { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public Guid RunId { get; init; }
    public string? RunDirectory { get; init; }
    public double DurationSeconds { get; init; }

    /// <summary>The full kind-specific result object, serialized as-is into the artifact.</summary>
    public required object Result { get; init; }
}

/// <summary>
/// One document kind's executor: the engine that knows how to run a document of that kind. Registered in the
/// host's composition root next to its <see cref="IFlowDocumentKind"/>, so the execution layer never depends on
/// the engines it dispatches to.
/// </summary>
public interface IFlowDocumentExecutor
{
    bool CanExecute(FlowDocument document);

    Task<DocumentExecutionResult> ExecuteAsync(FlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct);
}

/// <summary>
/// The single execution pathway: loads a document (with the same secret-hygiene check as <c>validate</c>),
/// dispatches it to the registered executor of its kind, and hands back a uniform outcome. It never writes to the
/// console; the caller prints from the returned result. Secret-hygiene and run-history warnings surface through an
/// optional warning sink supplied at construction.
/// </summary>
public sealed class DocumentExecutor : IDocumentRunner
{
    private readonly IServiceProvider _provider;
    private readonly Action<string>? _warningSink;

    /// <param name="provider">The composition root the loaders and executors are pulled from.</param>
    /// <param name="warningSink">Where secret-hygiene and run-history warnings are surfaced; null suppresses them.
    /// The CLI wires this to standard error.</param>
    public DocumentExecutor(IServiceProvider provider, Action<string>? warningSink = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _warningSink = warningSink;
    }

    /// <summary>Where this executor's warnings go (the run-history writer of a kind's executor uses the same sink).</summary>
    public Action<string>? WarningSink => _warningSink;

    public async Task<DocumentRunOutcome> RunAsync(string flowFile, DocumentExecutionOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(options);
        var documents = _provider.GetRequiredService<YamlDocumentLoader>();

        FlowDocument document;
        try
        {
            document = DocumentLoader.Load(documents, flowFile, _warningSink);
        }
        catch (Exception ex) when (ex is SqlFlowException or IOException or UnauthorizedAccessException)
        {
            return new DocumentRunOutcome
            {
                FlowName = Path.GetFileName(flowFile),
                FlowKind = "unknown",
                Success = false,
                Error = SecretHygiene.RedactedMessage(ex),
            };
        }

        var result = await ExecuteAsync(document, flowFile, options, ct).ConfigureAwait(false);
        return new DocumentRunOutcome
        {
            FlowName = result.FlowName,
            FlowKind = result.FlowKind,
            Success = result.Success,
            Error = result.Error,
            RunId = result.RunId,
            RunDirectory = result.RunDirectory,
            DurationSeconds = result.DurationSeconds,
        };
    }

    /// <summary>Runs an already-loaded document through the executor registered for its kind and returns the rich result.</summary>
    public Task<DocumentExecutionResult> ExecuteAsync(
        FlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(options);

        // The claimed run's flow name selects WHICH flow executes. A name matching nothing is refused loudly:
        // silently running a different flow than the caller asked for would be the worst outcome.
        if (options.FlowName is { } requested && !string.Equals(requested, document.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlFlowException(
                $"This document declares flow '{document.Name}', not '{requested}'. The file and the catalog have drifted; re-sync the repo.");
        }

        var executor = _provider.GetServices<IFlowDocumentExecutor>().FirstOrDefault(e => e.CanExecute(document))
            ?? throw new SqlFlowException($"Cannot run document kind '{document.Kind}': no executor is registered for it in this host.");
        return executor.ExecuteAsync(document, flowFile, options, ct);
    }
}

/// <summary>The pieces every kind's executor assembles the same way: the run's event plumbing, the artifact
/// envelope, and the run-history write. Shared so a run records identically whichever kind produced it.</summary>
public static class RunArtifacts
{
    /// <summary>Builds the per-run event plumbing shared by every run-log-driven flow kind: the canonical
    /// <see cref="RunLogger"/> (rendered to <c>run.log</c>), the collector whose records become the run.json
    /// <c>events</c> array (forwarding live to the node's sink when one is attached), and the bridge the runner
    /// logs through so one <c>Log</c> call feeds both.</summary>
    public static (RunLogger RunLogger, RunEventCollector Events, RunLogEventBridge Sink) BuildEventPlumbing(
        DocumentExecutionOptions options, string flowName)
    {
        ArgumentNullException.ThrowIfNull(options);
        var runLogger = new RunLogger(options.LogLevel, options.Echo);
        var events = new RunEventCollector(options.EventSink);
        return (runLogger, events, new RunLogEventBridge(runLogger, events, options.RunId, flowName));
    }

    /// <summary>The stable <c>run.json</c> envelope.</summary>
    public static RunArtifact Artifact(
        string kind, string flowName, Guid runId, bool success, string? error, object result,
        IReadOnlyList<RunEventRecord>? events = null)
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

    /// <summary>Writes the standard artifact set (<c>run.json</c>, <c>run.log</c>) plus any kind-specific extras
    /// into the run history next to the flow document. Returns the run folder, or null when it could not be
    /// written (the warning sink hears why; the run itself already finished).</summary>
    public static string? Write(
        string flowFile, string flowName, Guid runId, RunArtifact artifact, string runLog,
        IReadOnlyDictionary<string, string>? extras, Action<string>? warningSink)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run.json"] = JsonSerializer.Serialize(artifact, ExecutionJson.Options),
            ["run.log"] = runLog,
        };
        if (extras is not null)
        {
            foreach (var (name, content) in extras)
            {
                files[name] = content;
            }
        }

        return RunHistory.Write(flowFile, flowName, runId, files, warningSink);
    }
}
