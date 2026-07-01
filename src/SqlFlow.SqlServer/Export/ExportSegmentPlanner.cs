using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Export;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Ingestion;

namespace SqlFlow.SqlServer.Export;

/// <summary>One export chunk: the source SELECT, the file name (no extension), and its subfolder.</summary>
public sealed record ExportSegment(string Sql, string FileName, string SubFolder);

/// <summary>
/// Plans the per-chunk source SELECTs and file names for an export, porting CommonDB.GetSrcSelectBatched and the
/// ExpSegment file-name composition. Chunks by day/month/key (reusing <see cref="ChunkRanges"/>, shared with
/// InitLoad) or produces one full-table segment for 'F'; date intervals are half-open and key intervals
/// inclusive, with a trailing NULL-rows segment for D/M/K. Applies the static SrcFilter (legacy dropped it).
/// Pure and deterministic given the bounds and the run timestamp.
/// </summary>
public static class ExportSegmentPlanner
{
    public static IReadOnlyList<ExportSegment> Plan(ExportFlow flow, IReadOnlyList<string> columns, int keyMax, DateTime runTimestamp)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(columns);

        var by = flow.ExportBy.Trim().ToUpperInvariant();
        var prefix = string.IsNullOrWhiteSpace(flow.TrgFileName) ? flow.Source.Name : flow.TrgFileName!;
        var timestamp = flow.AddTimeStampToFileName ? "_" + runTimestamp.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : string.Empty;
        var columnList = string.Join(", ", columns.Select(c => $"[{Escape(c)}]"));

        // The full three-part name, as legacy GetSrcSelectBatched read it: the declared database is honored
        // even when the connection's default catalog differs (the source is always SQL Server, so the
        // cross-database form is always valid).
        var from = flow.Source.QualifiedName;
        var hint = TableHint(flow.SrcWithHint);
        var filter = string.IsNullOrWhiteSpace(flow.SrcFilter) ? string.Empty : " " + flow.SrcFilter.Trim();

        string Select(string batch) => $"SELECT {columnList} FROM {from}{hint} WHERE 1=1{filter}{batch}";

        var segments = new List<ExportSegment>();
        switch (by)
        {
            case "D":
            case "M":
            {
                if (string.IsNullOrWhiteSpace(flow.DateColumn))
                {
                    throw new SqlFlowException($"Export flow {flow.FlowId} uses ExportBy '{by}' but has no DateColumn.");
                }

                var dateColumn = Escape(flow.DateColumn);
                var from2 = flow.FromDate ?? DateOnly.FromDateTime(DateTime.Today.AddYears(-3));
                var to2 = flow.ToDate ?? DateOnly.FromDateTime(DateTime.Today);
                var ranges = by == "M" ? ChunkRanges.ByMonth(from2, to2, flow.ExportSize) : ChunkRanges.ByDay(from2, to2, flow.ExportSize);
                foreach (var (start, end) in ranges)
                {
                    var batch = $" AND ([{dateColumn}] >= '{Date(start)}' AND [{dateColumn}] < '{Date(end.AddDays(1))}')";
                    var postfix = start == end ? Date(start) : $"{Date(start)}-{Date(end)}";
                    segments.Add(new ExportSegment(Select(batch), $"{prefix}{timestamp}_{postfix}", SubFolder(flow, start)));
                }

                segments.Add(new ExportSegment(Select($" AND ([{dateColumn}] IS NULL)"), $"{prefix}{timestamp}_NullRows", string.Empty));
                break;
            }

            case "K":
            {
                if (string.IsNullOrWhiteSpace(flow.IncrementalColumn))
                {
                    throw new SqlFlowException($"Export flow {flow.FlowId} uses ExportBy 'K' but has no IncrementalColumn.");
                }

                var keyColumn = Escape(flow.IncrementalColumn);
                var width = Math.Max(1, keyMax).ToString(CultureInfo.InvariantCulture).Length;
                foreach (var (lo, hi) in ChunkRanges.ByKey(0, keyMax, flow.ExportSize))
                {
                    var batch = $" AND ([{keyColumn}] >= {lo} AND [{keyColumn}] <= {hi})";
                    var postfix = $"{lo.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}-{hi.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}";
                    segments.Add(new ExportSegment(Select(batch), $"{prefix}{timestamp}_{postfix}", string.Empty));
                }

                segments.Add(new ExportSegment(Select($" AND ([{keyColumn}] IS NULL)"), $"{prefix}{timestamp}_NullRows", string.Empty));
                break;
            }

            default:
                // 'F' (or an unknown unit): one full-table file.
                segments.Add(new ExportSegment(Select(string.Empty), prefix + timestamp, string.Empty));
                break;
        }

        return segments;
    }

    // Subfolders distribute D/M files by the chunk-start date: each YYYY/MM/DD token in the pattern is a folder
    // level (legacy PatternToSubfolderPath).
    private static string SubFolder(ExportFlow flow, DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(flow.Subfolderpattern))
        {
            return string.Empty;
        }

        var pattern = flow.Subfolderpattern.ToUpperInvariant();
        var parts = new List<string>();
        if (pattern.Contains("YYYY", StringComparison.Ordinal))
        {
            parts.Add(date.Year.ToString("D4", CultureInfo.InvariantCulture));
        }

        if (pattern.Contains("MM", StringComparison.Ordinal))
        {
            parts.Add(date.Month.ToString("D2", CultureInfo.InvariantCulture));
        }

        if (pattern.Contains("DD", StringComparison.Ordinal))
        {
            parts.Add(date.Day.ToString("D2", CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? string.Empty : string.Join("/", parts) + "/";
    }

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Renders the optional source table hint (legacy srcWithHint) as a single WITH (...) clause, accepting the
    // operator's value in any of the common spellings ("NOLOCK", "(NOLOCK)", "WITH (NOLOCK)", "NOLOCK, INDEX(..)")
    // and normalizing to exactly one WITH (...) so it is emitted right after the table reference. Blank means no
    // hint. The content is operator-supplied trusted config (raw-append, like SrcFilter). Shared with the runner
    // so the key-max probe reads the source under the same hint as the data SELECTs.
    internal static string TableHint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var inner = raw.Trim();
        if (inner.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            inner = inner[4..].Trim();
        }

        // Strip exactly one wrapping paren pair when the whole value is wrapped ("(NOLOCK)"), but never a paren
        // that belongs to a hint argument ("NOLOCK, INDEX(IX)"): only unwrap when the leading '(' is the one the
        // trailing ')' closes.
        if (IsWrappedInParens(inner))
        {
            inner = inner[1..^1].Trim();
        }

        return inner.Length == 0 ? string.Empty : $" WITH ({inner})";
    }

    private static bool IsWrappedInParens(string value)
    {
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')')
        {
            return false;
        }

        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '(')
            {
                depth++;
            }
            else if (value[i] == ')')
            {
                depth--;
                // The opening '(' closed before the end, so the value is not a single wrapped group.
                if (depth == 0 && i < value.Length - 1)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
