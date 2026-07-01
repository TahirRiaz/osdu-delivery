namespace SqlFlow.Core.Runs;

/// <summary>
/// The canonical envelope of a run.json artifact in the .sqlflow run history. The header fields are a STABLE,
/// VERSIONED contract: every run.json, for every flow kind, always carries these same top-level fields, so the
/// flat files can be bulk-loaded into a database or fed into reporting without per-kind parsing. The
/// kind-specific detail (counts, trace, assertions) rides in <see cref="Result"/>; additions to the header are
/// backward-compatible and any breaking change increments <see cref="CurrentSchemaVersion"/>.
/// </summary>
public sealed record RunArtifact
{
    /// <summary>The current run.json schema version written by this build.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The flow document kind: <c>ing</c> for table-to-table ingestion, <c>file</c> for a file flow,
    /// <c>exp</c> for a file export, <c>sp</c> for a stored-procedure flow, <c>inv</c> for a standalone
    /// invoke, <c>hc</c> for an ML health check.</summary>
    public required string FlowKind { get; init; }

    public required string FlowName { get; init; }

    public required Guid RunId { get; init; }

    public required bool Success { get; init; }

    public required DateTime WrittenUtc { get; init; }

    public string? Error { get; init; }

    /// <summary>The machine that produced the run, its node identity, so run history aggregated from many nodes
    /// into one catalog is attributable per node. Defaults to the local machine name; a run is always written by
    /// the machine that executed it. Part of the stable header, read back as <c>host</c>.</summary>
    public string? Host { get; init; } = Environment.MachineName;

    /// <summary>The full kind-specific result (an IngestionRunResult, FlowResult, ExportRunResult,
    /// StoredProcedureRunResult, InvokeResult, or HealthCheckRunResult), serialized as-is.</summary>
    public required object Result { get; init; }
}
