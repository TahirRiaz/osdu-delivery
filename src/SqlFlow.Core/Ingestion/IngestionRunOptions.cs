using SqlFlow.Core.Runs;

namespace SqlFlow.Core.Ingestion;

/// <summary>
/// Per-execution run parameters that are not part of the flow definition (legacy ExecMode and the batch run
/// id). Passed to the runner per call, with sensible defaults, rather than baked into the immutable flow.
/// </summary>
public sealed record IngestionRunOptions
{
    /// <summary>How the run was launched (legacy ExecMode), for the run log. Null leaves it unset.</summary>
    public string? ExecMode { get; init; }

    /// <summary>The batch run instance id (legacy BatchID), carried for the deferred SysLogBatch status.</summary>
    public string? BatchId { get; init; }

    /// <summary>Receives the run's canonical event log as it happens (a <see cref="RunLogger"/> in YAML mode,
    /// streamed live and written to the .sqlflow run folder). Null records nothing.</summary>
    public IRunEventSink? Events { get; init; }

    /// <summary>An orchestrator-assigned run id the runner stamps on the run instead of minting its own. The
    /// control-plane trigger hands a run id to its caller before the run executes, so the runner must record the
    /// run under that exact id for <c>GET /runs/{id}</c> to resolve. Null mints a fresh id, which is what every
    /// direct CLI run does, so the existing behavior is unchanged.</summary>
    public Guid? RunId { get; init; }
}
