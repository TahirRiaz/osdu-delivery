using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Profiling;

namespace SqlFlow.SqlServer.Profiling;

/// <summary>
/// The SQL Server measurement behind <see cref="UniqueKeyDetector"/>, shaped to stay cheap on a large table.
/// <see cref="MeasureManyAsync"/> measures many column sets in one scan: each set contributes a
/// <c>COUNT_BIG(DISTINCT ...)</c> (a single column directly, a composite via a <c>HASHBYTES</c> of its non-null
/// values) and a non-null count, so a whole greedy level of trial combinations is one query, not one per trial.
/// When a sample is requested - or auto-selected for a large table - the sample is materialized once into a session
/// temp table and every set is measured against that same fixed set, so the search is coherent (a bare <c>TOP</c>
/// re-evaluated per query would sample different rows each time). <see cref="VerifyAsync"/> confirms a finalist
/// against the whole table with short-circuiting <c>EXISTS</c> probes for a null or a duplicate group, which stop at
/// the first offending row instead of counting every distinct value. The probe owns one open connection for its
/// lifetime, released on <see cref="DisposeAsync"/>.
/// </summary>
public sealed class SqlServerUniquenessProbe : IUniquenessProbe
{
    /// <summary>Above this row count, a run with no explicit sample size auto-samples the search.</summary>
    public const int AutoSampleThreshold = 2_000_000;

    /// <summary>The sample size used when a large table is auto-sampled.</summary>
    public const int AutoSampleSize = 500_000;

    private readonly SqlConnection _connection;
    private readonly string _table;
    private readonly string _workingSet;

    private SqlServerUniquenessProbe(SqlConnection connection, string table, string workingSet, long totalRows, long workingRows, bool sampled)
    {
        _connection = connection;
        _table = table;
        _workingSet = workingSet;
        TotalRows = totalRows;
        WorkingRows = workingRows;
        Sampled = sampled;
    }

    public long TotalRows { get; }

    public long WorkingRows { get; }

    public bool Sampled { get; }

    /// <summary>
    /// Opens the probe. <paramref name="sampleSize"/> null auto-samples a table over <see cref="AutoSampleThreshold"/>
    /// rows (0 forces a full scan; a positive value sets an explicit sample). When sampling, the chosen number of rows
    /// of the candidate columns is copied into a session temp table to measure against; the connection is held open
    /// for the probe's lifetime because that temp table lives on it.
    /// </summary>
    public static async Task<SqlServerUniquenessProbe> CreateAsync(
        string connectionString, string qualifiedTable, IReadOnlyList<string> columns, int? sampleSize, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedTable);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("At least one column is required to profile.", nameof(columns));
        }

        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var total = await ScalarLongAsync(connection, $"SELECT COUNT_BIG(*) FROM {qualifiedTable};", ct).ConfigureAwait(false);

            var resolved = sampleSize ?? (total > AutoSampleThreshold ? AutoSampleSize : 0);
            if (resolved > 0 && total > resolved)
            {
                var columnList = string.Join(", ", columns.Select(Bracket));
                var top = resolved.ToString(CultureInfo.InvariantCulture);
                await ExecuteAsync(connection, $"SELECT TOP ({top}) {columnList} INTO #uk_work FROM {qualifiedTable};", ct).ConfigureAwait(false);
                var working = await ScalarLongAsync(connection, "SELECT COUNT_BIG(*) FROM #uk_work;", ct).ConfigureAwait(false);
                return new SqlServerUniquenessProbe(connection, qualifiedTable, "#uk_work", total, working, sampled: true);
            }

            return new SqlServerUniquenessProbe(connection, qualifiedTable, qualifiedTable, total, total, sampled: false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<SetMeasure>> MeasureManyAsync(IReadOnlyList<IReadOnlyList<string>> sets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sets);
        if (sets.Count == 0)
        {
            return [];
        }

        // One scan, two aggregates per set: distinct non-null tuples, and the non-null row count.
        var select = new StringBuilder("SELECT COUNT_BIG(*) AS Scanned");
        for (var i = 0; i < sets.Count; i++)
        {
            var set = sets[i];
            if (set.Count == 0)
            {
                throw new ArgumentException("A measured column set must have at least one column.", nameof(sets));
            }

            select.Append(",\n    ").Append(DistinctExpression(set)).Append(" AS D").Append(i);
            select.Append(",\n    ").Append(NonNullExpression(set)).Append(" AS V").Append(i);
        }

        select.Append("\nFROM ").Append(_workingSet).Append(';');

        await using var command = new SqlCommand(select.ToString(), _connection);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The uniqueness measurement returned no row.");
        }

        var scanned = reader.GetInt64(0);
        var results = new List<SetMeasure>(sets.Count);
        for (var i = 0; i < sets.Count; i++)
        {
            var distinct = reader.GetInt64(1 + (i * 2));
            var nonNull = reader.GetInt64(2 + (i * 2));
            results.Add(new SetMeasure
            {
                Columns = sets[i].ToList(),
                Scanned = scanned,
                Distinct = distinct,
                Nulls = scanned - nonNull,
                Exact = !Sampled,
            });
        }

        return results;
    }

    public async Task<SetVerdict> VerifyAsync(IReadOnlyList<string> columns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("At least one column is required to verify.", nameof(columns));
        }

        var columnList = string.Join(", ", columns.Select(Bracket));
        var anyNull = string.Join(" OR ", columns.Select(c => $"{Bracket(c)} IS NULL"));
        var allNotNull = string.Join(" AND ", columns.Select(c => $"{Bracket(c)} IS NOT NULL"));

        // Two short-circuiting existence probes: stop at the first null row and at the first repeated tuple. Neither
        // scans the whole table when the set is not a key; only a genuinely unique set forces a full aggregate.
        var sql = $"""
            SELECT
                CONVERT(bit, IIF(EXISTS (SELECT 1 FROM {_table} WHERE {anyNull}), 1, 0)) AS HasNulls,
                CONVERT(bit, IIF(EXISTS (SELECT 1 FROM {_table} WHERE {allNotNull} GROUP BY {columnList} HAVING COUNT_BIG(*) > 1), 1, 0)) AS HasDuplicate;
            """;

        await using var command = new SqlCommand(sql, _connection);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The uniqueness verification returned no row.");
        }

        var hasNulls = reader.GetBoolean(0);
        var hasDuplicate = reader.GetBoolean(1);
        return new SetVerdict
        {
            Columns = columns.ToList(),
            IsUnique = TotalRows > 0 && !hasNulls && !hasDuplicate,
            HasNulls = hasNulls,
            Rows = TotalRows,
        };
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);

    /// <summary>The distinct-tuple count of a column set over the non-null rows: a single column directly, a composite
    /// through a <c>HASHBYTES</c> of its length-safe concatenation so many composites fit in one query.</summary>
    private static string DistinctExpression(IReadOnlyList<string> columns)
    {
        if (columns.Count == 1)
        {
            return $"COUNT_BIG(DISTINCT {Bracket(columns[0])})";
        }

        var allNotNull = string.Join(" AND ", columns.Select(c => $"{Bracket(c)} IS NOT NULL"));
        // NCHAR(31) (unit separator) delimits the parts; every part is converted to nvarchar(max) so the hash reflects
        // the exact tuple. A hash collision could only make the count too low (a key would be missed, never invented),
        // and the final candidate is confirmed exactly by VerifyAsync.
        var concat = "CONCAT(" + string.Join(", NCHAR(31), ", columns.Select(c => $"CONVERT(nvarchar(max), {Bracket(c)})")) + ")";
        return $"COUNT_BIG(DISTINCT CASE WHEN {allNotNull} THEN HASHBYTES('SHA2_256', {concat}) END)";
    }

    private static string NonNullExpression(IReadOnlyList<string> columns)
    {
        if (columns.Count == 1)
        {
            return $"COUNT_BIG({Bracket(columns[0])})";
        }

        var allNotNull = string.Join(" AND ", columns.Select(c => $"{Bracket(c)} IS NOT NULL"));
        return $"COUNT_BIG(CASE WHEN {allNotNull} THEN 1 END)";
    }

    private static async Task<long> ScalarLongAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Bracket(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
