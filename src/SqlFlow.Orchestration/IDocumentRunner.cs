using SqlFlow.Core.Runs;

namespace SqlFlow.Orchestration;

/// <summary>Run-time options threaded into a single document run, the subset that matters when a batch drives a
/// member (the rest of the CLI's run flags are presentation concerns).</summary>
public sealed record DocumentExecutionOptions
{
    public RunLogLevel LogLevel { get; init; } = RunLogLevel.Info;

    /// <summary>Force fresh health-check models (the hc <c>--retrain</c> switch).</summary>
    public bool Retrain { get; init; }

    /// <summary>Push a source-control snapshot to its remote (the scm default; false is <c>--no-push</c>).</summary>
    public bool ScmPush { get; init; } = true;

    /// <summary>Script and write a source-control snapshot without committing (the scm <c>--dry-run</c>).</summary>
    public bool ScmDryRun { get; init; }

    /// <summary>Where the live run log is echoed, line by line; null keeps it to the run-history file only. A
    /// single CLI run echoes to the console; a batch leaves this null so concurrent members do not interleave
    /// on the console (each member's log still lands in its own run folder).</summary>
    public Action<string>? Echo { get; init; }

    /// <summary>An orchestrator-assigned run id stamped on the run instead of the runner minting its own. The
    /// control-plane trigger hands this id back before the run executes, so the recorded run resolves at
    /// <c>GET /api/v1/runs/{id}</c>. Null mints a fresh id (every direct CLI run), so existing behavior is
    /// unchanged. Threaded into each kind's runner through the single execution path.</summary>
    public Guid? RunId { get; init; }
}

/// <summary>The uniform outcome of running one flow document, whatever its kind. The batch needs only the
/// terminal facts; the kind-specific detail already lives in the member's own run-history folder.</summary>
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

/// <summary>
/// Runs one flow document end to end (build the runner, execute, write the run-history artifacts) and returns a
/// uniform outcome. This is the single execution pathway shared by the CLI's <c>run</c> verb and the batch
/// orchestrator, so a batch member runs through exactly the same code as a directly-invoked flow.
/// </summary>
public interface IDocumentRunner
{
    /// <param name="flowFile">The absolute path to the member flow document.</param>
    /// <param name="options">Run-time options threaded into the run (log level, retrain, scm push/dry-run).</param>
    /// <param name="ct">Cancellation for the run.</param>
    Task<DocumentRunOutcome> RunAsync(string flowFile, DocumentExecutionOptions options, CancellationToken ct = default);
}
