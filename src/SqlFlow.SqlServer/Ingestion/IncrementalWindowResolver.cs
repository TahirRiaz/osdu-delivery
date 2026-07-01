using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;

namespace SqlFlow.SqlServer.Ingestion;

/// <summary>The computed incremental window: the source WHERE fragment to append to <c>WHERE 1=1</c> (it
/// begins with <c>" AND "</c> or is empty), and whether the run should take the full-load / insert-all apply
/// path (an empty or absent target, or a keyless flow with no predicate).</summary>
public sealed record IncrementalWindow
{
    public required string SourceWhere { get; init; }

    public required bool RunFullLoad { get; init; }

    /// <summary>The MAX watermark probe run against the target, surfaced for the run's SQL trace; null when
    /// no probe ran.</summary>
    public string? TargetMaxProbeSql { get; init; }

    /// <summary>The MIN watermark probe run against the source (FetchMinValuesFromSource), null when none ran.</summary>
    public string? SourceMinProbeSql { get; init; }
}

/// <summary>
/// Resolves the incremental read window for a relational flow, porting the legacy CommonDB.GetIncWhereExp +
/// ProcessIngestion watermark assembly. It probes MAX over the TARGET (and, when FetchMinValuesFromSource,
/// MIN over the SOURCE), then builds the source WHERE following the exact legacy precedence: a replace-filter
/// wins, then the FullLoad flag, then an empty/absent target, then the incremental-column predicate, then the
/// date predicate, with the append-filter concatenated last. The date watermark is moved back OverlapDays
/// days. Watermark literals are formatted from the introspected source column types (numeric, binary, date,
/// string) rather than re-deriving types inline. The legacy WITH(hint) and the Synapse FOR-XML shaping are
/// deliberately not ported (SQL Server target only).
/// </summary>
public sealed class IncrementalWindowResolver
{
    private static readonly Watermark[] NoMarks = [];

    private readonly ICatalogReader _catalog;
    private readonly IConnectionFactory _factory;

    public IncrementalWindowResolver(ICatalogReader catalog, IConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(factory);
        _catalog = catalog;
        _factory = factory;
    }

    public async Task<IncrementalWindow> ResolveAsync(
        IngestionFlow flow,
        ResolvedConnection source,
        string targetConnectionString,
        IReadOnlyList<SqlColumn> sourceColumns,
        ISourceSqlDialect? sourceDialect = null,
        CancellationToken ct = default)
    {
        sourceDialect ??= new SqlServerSourceDialect();
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceColumns);

        var keyless = flow.Load.KeyColumns.Count == 0;
        var incExp = string.Empty;
        var dateExp = string.Empty;
        bool targetEmpty;
        string? maxProbeSql = null;
        string? minProbeSql = null;

        if (!flow.Incremental.IsIncremental)
        {
            // No incremental configuration: read the whole source (optionally filtered). A keyless flow with
            // no predicate is a full-load apply (insert-all); a keyed flow upserts.
            targetEmpty = keyless;
        }
        else
        {
            var typesByName = sourceColumns.ToDictionary(c => c.Name, c => c.DataType, StringComparer.OrdinalIgnoreCase);
            var marks = BuildMarkList(flow, typesByName);

            var (targetExists, maxMarks, probeSql) = await ProbeMaxAsync(flow, targetConnectionString, marks, ct).ConfigureAwait(false);
            maxProbeSql = probeSql;
            if (!targetExists || maxMarks.All(m => m.Value is null))
            {
                // Absent or empty target: nothing to bound by, full load.
                targetEmpty = true;
            }
            else
            {
                targetEmpty = false;
                var effective = maxMarks;
                var op = ">";

                if (flow.Incremental.FetchMinValuesFromSource)
                {
                    var (minMarks, minSql) = await ProbeMinAsync(flow, source, marks, sourceDialect, ct).ConfigureAwait(false);
                    minProbeSql = minSql;
                    if (SourceMinIsLess(minMarks, maxMarks))
                    {
                        // Reprocess history: widen the window back to the source minimum.
                        effective = minMarks;
                        op = ">=";
                    }
                }

                (incExp, dateExp) = BuildPredicates(effective, op, sourceDialect);
            }
        }

        var runFullLoad = targetEmpty || (incExp.Length == 0 && dateExp.Length == 0 && keyless);
        var sourceWhere = AssembleWhere(flow.Source, flow.Incremental.FullLoad, incExp, dateExp, targetEmpty);
        return new IncrementalWindow
        {
            SourceWhere = sourceWhere,
            RunFullLoad = runFullLoad,
            TargetMaxProbeSql = maxProbeSql,
            SourceMinProbeSql = minProbeSql,
        };
    }

    // Precedence is load-bearing and matches legacy ProcessIngestion exactly: replace-filter, then the
    // FullLoad flag, then empty target, then the incremental predicate, then the date predicate; the
    // append-filter is concatenated last. The user filter is raw SQL carrying its own leading keyword (the
    // legacy contract: "Example AND SystemID = 13"); it is not normalized here.
    private static string AssembleWhere(IngestionSource sourceSpec, bool fullLoadFlag, string incExp, string dateExp, bool targetEmpty)
    {
        var filter = (sourceSpec.Filter ?? string.Empty).Trim();
        var hasFilter = filter.Length > 0;

        string sourceWhere;
        if (hasFilter && !sourceSpec.FilterIsAppend)
        {
            sourceWhere = " " + filter;
        }
        else if (fullLoadFlag || targetEmpty)
        {
            sourceWhere = string.Empty;
        }
        else if (incExp.Length > 0)
        {
            sourceWhere = incExp;
        }
        else if (dateExp.Length > 0)
        {
            sourceWhere = dateExp;
        }
        else
        {
            sourceWhere = string.Empty;
        }

        if (sourceSpec.FilterIsAppend && hasFilter)
        {
            sourceWhere += " " + filter;
        }

        return sourceWhere;
    }

    private async Task<(bool Exists, IReadOnlyList<Watermark> Marks, string? Sql)> ProbeMaxAsync(
        IngestionFlow flow, string targetConnectionString, IReadOnlyList<Watermark> marks, CancellationToken ct)
    {
        await using var connection = new SqlConnection(targetConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var live = await _catalog.IntrospectObjectAsync(connection, ToName(flow.Target.Table), ct).ConfigureAwait(false);
        if (live is null)
        {
            return (false, NoMarks, null);
        }

        if (marks.Count == 0)
        {
            return (true, NoMarks, null);
        }

        var projections = marks.Select(m => m.IsDate
            ? $"DATEADD(day, -{flow.Incremental.OverlapDays}, MAX([{Escape(m.Column)}])) AS [{Escape(m.Column)}]"
            : $"MAX([{Escape(m.Column)}]) AS [{Escape(m.Column)}]");
        var sql = $"SELECT {string.Join(", ", projections)} FROM {SchemaQualified(flow.Target.Table)} WHERE 1=1{Clause(flow.Source.IncrementalClause)};";

        return (true, await ReadMarksAsync(connection, sql, marks, ct).ConfigureAwait(false), sql);
    }

    private async Task<(IReadOnlyList<Watermark> Marks, string? Sql)> ProbeMinAsync(
        IngestionFlow flow, ResolvedConnection source, IReadOnlyList<Watermark> marks, ISourceSqlDialect dialect, CancellationToken ct)
    {
        if (marks.Count == 0)
        {
            return (NoMarks, null);
        }

        await using var connection = await _factory.OpenAsync(source, ct).ConfigureAwait(false);

        // The date watermark gets the same overlap subtraction as the MAX probe, so the source MIN and target
        // MAX are shifted equally and the strict-less comparison is symmetric (legacy applies DATEADD to both).
        // This SQL runs ON THE SOURCE, so identifiers and date arithmetic come from the source dialect.
        var projections = marks.Select(m => m.IsDate
            ? $"{dialect.DateSubtractDays($"MIN({dialect.QuoteIdentifier(m.Column)})", flow.Incremental.OverlapDays)} AS {dialect.QuoteIdentifier(m.Column)}"
            : $"MIN({dialect.QuoteIdentifier(m.Column)}) AS {dialect.QuoteIdentifier(m.Column)}");
        var sql = $"SELECT {string.Join(", ", projections)} FROM {dialect.QualifyObject(flow.Source.Table)} WHERE 1=1{Clause(flow.Source.IncrementalClause)};";
        return (await ReadMarksAsync(connection, sql, marks, ct).ConfigureAwait(false), sql);
    }

    private static async Task<IReadOnlyList<Watermark>> ReadMarksAsync(DbConnection connection, string sql, IReadOnlyList<Watermark> marks, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var result = new List<Watermark>(marks.Count);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            for (var i = 0; i < marks.Count; i++)
            {
                var value = await reader.IsDBNullAsync(i, ct).ConfigureAwait(false) ? null : reader.GetValue(i);
                result.Add(marks[i] with { Value = value });
            }
        }
        else
        {
            foreach (var mark in marks)
            {
                result.Add(mark with { Value = null });
            }
        }

        return result;
    }

    // The predicates are appended to the SOURCE read, so identifier quoting and binary literals come from
    // the source dialect.
    private static (string IncExp, string DateExp) BuildPredicates(IReadOnlyList<Watermark> marks, string op, ISourceSqlDialect dialect)
    {
        var incParts = new List<string>();
        var dateParts = new List<string>();
        foreach (var mark in marks)
        {
            if (mark.Value is null)
            {
                continue;
            }

            var predicate = $"{dialect.QuoteIdentifier(mark.Column)} {op} {FormatLiteral(mark.Value, mark.Type, dialect)}";
            (mark.IsDate ? dateParts : incParts).Add(predicate);
        }

        var incExp = incParts.Count > 0 ? " AND " + string.Join(" AND ", incParts) : string.Empty;
        var dateExp = dateParts.Count > 0 ? " AND " + string.Join(" AND ", dateParts) : string.Empty;
        return (incExp, dateExp);
    }

    private static bool SourceMinIsLess(IReadOnlyList<Watermark> mins, IReadOnlyList<Watermark> maxes)
    {
        var maxByName = maxes.ToDictionary(m => m.Column, StringComparer.OrdinalIgnoreCase);
        foreach (var min in mins)
        {
            if (min.Value is null || !maxByName.TryGetValue(min.Column, out var max) || max.Value is null)
            {
                continue;
            }

            if (CompareScalar(min.Value, max.Value) < 0)
            {
                return true;
            }
        }

        return false;
    }

    // The source MIN and target MAX values come from two different tables, so their CLR types can differ even
    // for the same logical column (for example after the target widened int to bigint). Coerce to a common
    // type per family rather than calling Comparer<object>.Default, which throws on mismatched types.
    private static int CompareScalar(object a, object b)
    {
        if (a is byte[] left && b is byte[] right)
        {
            var length = Math.Min(left.Length, right.Length);
            for (var i = 0; i < length; i++)
            {
                var diff = left[i].CompareTo(right[i]);
                if (diff != 0)
                {
                    return diff;
                }
            }

            return left.Length.CompareTo(right.Length);
        }

        if (IsNumeric(a) && IsNumeric(b))
        {
            try
            {
                return Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
            }
            catch (OverflowException)
            {
                return Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
            }
        }

        if (a is DateTimeOffset offsetA && b is DateTimeOffset offsetB)
        {
            return offsetA.CompareTo(offsetB);
        }

        if (a is DateTime || b is DateTime)
        {
            return Convert.ToDateTime(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDateTime(b, CultureInfo.InvariantCulture));
        }

        if (a.GetType() == b.GetType() && a is IComparable comparable)
        {
            return comparable.CompareTo(b);
        }

        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }

    private static bool IsNumeric(object value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static string FormatLiteral(object value, SqlDataType type, ISourceSqlDialect dialect)
    {
        var baseType = type.BaseType.ToLowerInvariant();
        return baseType switch
        {
            "bit" => Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? "1" : "0",
            "tinyint" or "smallint" or "int" or "bigint" => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            "decimal" or "numeric" or "money" or "smallmoney" => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            "float" or "real" => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
            "binary" or "varbinary" or "timestamp" or "rowversion" => dialect.FormatBinaryLiteral((byte[])value),
            "date" => "'" + ((DateTime)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'",
            "datetime" or "datetime2" or "smalldatetime" => "'" + Convert.ToDateTime(value, CultureInfo.InvariantCulture).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "'",
            "datetimeoffset" => "'" + ((DateTimeOffset)value).ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) + "'",
            "time" => "'" + ((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture) + "'",
            _ => "'" + (value.ToString() ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'",
        };
    }

    private static IReadOnlyList<Watermark> BuildMarkList(IngestionFlow flow, IReadOnlyDictionary<string, SqlDataType> typesByName)
    {
        var marks = new List<Watermark>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in flow.Incremental.Columns)
        {
            if (string.IsNullOrWhiteSpace(column) || !seen.Add(column))
            {
                continue;
            }

            marks.Add(new Watermark { Column = column, IsDate = false, Type = TypeFor(typesByName, column) });
        }

        var dateColumn = flow.Incremental.DateColumn;
        if (!string.IsNullOrWhiteSpace(dateColumn) && seen.Add(dateColumn))
        {
            marks.Add(new Watermark { Column = dateColumn, IsDate = true, Type = TypeFor(typesByName, dateColumn) });
        }

        return marks;
    }

    private static SqlDataType TypeFor(IReadOnlyDictionary<string, SqlDataType> typesByName, string column)
        => typesByName.TryGetValue(column, out var type)
            ? type
            : throw new SqlFlowException($"Incremental column '{column}' is not among the source columns.");

    private static string Clause(string? clause)
        => string.IsNullOrWhiteSpace(clause) ? string.Empty : " " + clause.Trim();

    private static ThreePartName ToName(RelationalObject relationalObject)
        => new() { Database = null, Schema = relationalObject.Schema, Name = relationalObject.Name };

    private static string SchemaQualified(RelationalObject relationalObject)
        => $"[{Escape(relationalObject.Schema)}].[{Escape(relationalObject.Name)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);

    private sealed record Watermark
    {
        public required string Column { get; init; }

        public required bool IsDate { get; init; }

        public required SqlDataType Type { get; init; }

        public object? Value { get; init; }
    }
}
