using System.Data.Common;

namespace SqlFlow.Core.Model;

/// <summary>
/// A record of one file processed during a load - the lightweight-mode equivalent of SQLFlow's
/// persistent file log (flw.SysFileLog). In stateless mode these are returned in the final result;
/// in full mode they would also be persisted for cross-run dedup.
/// </summary>
public sealed record ProcessedFile
{
    public required string Name { get; init; }
    public string? Path { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public DateTimeOffset ProcessedAt { get; init; } = DateTimeOffset.UtcNow;
    public long Rows { get; init; }
    public int Columns { get; init; }
}

/// <summary>
/// What a source reader returns: the forward-only data reader plus the manifest of files it read.
/// </summary>
public sealed record SourceReadResult
{
    public required DbDataReader Reader { get; init; }
    public IReadOnlyList<ProcessedFile> ProcessedFiles { get; init; } = [];
}
