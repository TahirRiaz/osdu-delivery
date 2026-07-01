using SqlFlow.Core.Ingestion;

namespace SqlFlow.Core.StoredProcedures;

/// <summary>The outcome of executing one stored-procedure flow.</summary>
public sealed record StoredProcedureRunResult
{
    public required Guid RunId { get; init; }

    public required int FlowId { get; init; }

    public required bool Success { get; init; }

    public DateTime StartTimeUtc { get; init; }

    public DateTime EndTimeUtc { get; init; }

    public int DurationSeconds { get; init; }

    /// <summary>The SQL the run executed (the EXEC statement), in execution order. Always captured, success or
    /// failure: the generated SQL is the debugging record.</summary>
    public IReadOnlyList<SqlTraceEntry> SqlTrace { get; init; } = [];

    /// <summary>The failure message (already redacted of any secret), or null on success.</summary>
    public string? Error { get; init; }
}
