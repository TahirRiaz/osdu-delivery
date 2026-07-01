using System.Globalization;
using System.Text;

namespace SqlFlow.Core.Ingestion;

/// <summary>One generated SQL statement captured during a run, in execution order. The trace is the
/// debuggability contract: every statement the engine generated for a run can be read back afterwards,
/// on success and (especially) on failure.</summary>
public sealed record SqlTraceEntry
{
    /// <summary>1-based position in the run's execution order.</summary>
    public required int Sequence { get; init; }

    /// <summary>The run step that produced the statement (for example "staging.create", "target.evolve",
    /// "upsert.update").</summary>
    public required string Step { get; init; }

    public required string Sql { get; init; }
}

/// <summary>Renders an ordered SQL trace as readable text (the per-run trace file / flw.SysLog.TraceLog).</summary>
public static class SqlTrace
{
    public static string Render(IReadOnlyList<SqlTraceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            sb.Append("-- [").Append(entry.Sequence.ToString(CultureInfo.InvariantCulture))
              .Append("] ").AppendLine(entry.Step);
            sb.AppendLine(entry.Sql.TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
