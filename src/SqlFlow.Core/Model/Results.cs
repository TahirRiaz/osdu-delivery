namespace SqlFlow.Core.Model;

public enum FlowStatus
{
    Success,
    Failed,
}

/// <summary>One traced operation: what ran, how long it took, and whether it succeeded.</summary>
public sealed record TraceEntry
{
    public required string Operation { get; init; }
    public required double ElapsedMs { get; init; }
    public required bool Succeeded { get; init; }
    public long? Rows { get; init; }
    public string? Detail { get; init; }
}

/// <summary>The result of planning a flow: the diff, the exact SQL that would run, and a trace.</summary>
public sealed record FlowPlan
{
    public required FlowDefinition Flow { get; init; }
    public required IReadOnlyList<SourceColumn> SourceColumns { get; init; }
    public required TableSchema Desired { get; init; }
    public TableSchema? Actual { get; init; }
    public required SchemaDelta Delta { get; init; }
    public required IReadOnlyList<string> DdlStatements { get; init; }
    public IReadOnlyList<TraceEntry> Trace { get; init; } = [];
}

/// <summary>
/// The result of executing a flow. Always carries a <see cref="Trace"/> of the main operations and
/// their durations, so a caller can see what the engine did and exactly where it failed.
/// </summary>
public sealed record FlowResult
{
    /// <summary>Unique identifier for this run; matches the RunId stamped on the run's events.</summary>
    public Guid RunId { get; init; }

    /// <summary>Stable identity of the flow definition (same across runs); matches events' FlowId.</summary>
    public Guid FlowId { get; init; }

    public required string FlowName { get; init; }
    public FlowStatus Status { get; init; } = FlowStatus.Success;
    public long RowsLoaded { get; init; }
    public IReadOnlyList<string> DdlExecuted { get; init; } = [];

    /// <summary>The files processed by this run (the in-result "file log" for stateless mode).</summary>
    public IReadOnlyList<ProcessedFile> ProcessedFiles { get; init; } = [];

    public IReadOnlyList<TraceEntry> Trace { get; init; } = [];
    public double TotalMs { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// The typed transformation view generated over the loaded table as the run's post-process (null when the
    /// flow does not generate one). Carries the resolved per-column projection - the detected/declared transforms
    /// the catalog records for the pipeline - and the exact DDL that ran.
    /// </summary>
    public TransformViewResult? TransformView { get; init; }
}

/// <summary>
/// The outcome of the transformation-view post-process: the view refreshed over the flow's loaded table and the
/// resolved column projection it exposes (authored transforms merged with inferred types; the downstream chained
/// flow reads this view to get correctly-typed data).
/// </summary>
public sealed record TransformViewResult
{
    /// <summary>The view's unqualified name (<c>v&lt;Table&gt;</c>), in the loaded table's schema and database.</summary>
    public required string ViewName { get; init; }

    /// <summary>The exact <c>CREATE OR ALTER VIEW</c> statement the run executed.</summary>
    public required string Ddl { get; init; }

    /// <summary>The resolved, ordered projection the view exposes (one entry per output column).</summary>
    public required IReadOnlyList<InferredColumn> Columns { get; init; }
}
