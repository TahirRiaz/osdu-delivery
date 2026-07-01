using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer;

/// <summary>
/// Profiles raw columns server-side with a single set-based pass per column, using SQL Server's own
/// <c>TRY_CONVERT</c> as the oracle. Counts how many non-null values convert to each candidate type,
/// so the inferencer can only ever pick a type SQL Server can actually produce. Date and numeric
/// candidates are probed in the resolved locale's preferred order (and numbers are normalized through
/// <see cref="LocaleConversion"/>), so the counts the inferencer chooses from are locale-consistent.
/// </summary>
public sealed class SqlServerColumnProfiler : IColumnProfiler
{
    public async Task<IReadOnlyList<ColumnProfile>> ProfileAsync(
        string connectionString,
        string schema,
        string table,
        IReadOnlyList<string> columns,
        TypeInferencePolicy policy,
        ServerLocale locale,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(locale);

        // The locale-preferred order the inferencer will scan; identical for every column in the run.
        var dateTimeStyles = LocaleConversion.DateTimeStyleCandidates(locale.DateOrder);
        var dateStyles = LocaleConversion.DateStyleCandidates(locale.DateOrder);
        // Probe the invariant convention too only when it differs from the locale one (i.e. ','-decimal).
        var numericFormats = locale.DecimalSeparator == ','
            ? new[] { NumericFormat.Locale, NumericFormat.Invariant }
            : [NumericFormat.Locale];

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var qualified = $"[{Escape(schema)}].[{Escape(table)}]";
        var top = policy.SampleSize > 0 ? $"TOP ({policy.SampleSize.ToString(CultureInfo.InvariantCulture)}) " : string.Empty;

        var profiles = new List<ColumnProfile>(columns.Count);
        foreach (var columnName in columns)
        {
            var column = $"[{Escape(columnName)}]";
            var sql = BuildSql(qualified, column, top, dateTimeStyles, dateStyles, numericFormats, locale);

            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                continue;
            }

            long Long(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? 0 : Convert.ToInt64(reader[name], CultureInfo.InvariantCulture);
            int Int(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? 0 : Convert.ToInt32(reader[name], CultureInfo.InvariantCulture);
            long? NullableLong(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : Convert.ToInt64(reader[name], CultureInfo.InvariantCulture);

            profiles.Add(new ColumnProfile
            {
                ColumnName = columnName,
                Total = Long("Total"),
                NonNull = Long("NonNull"),
                AsBigInt = Long("AsBigInt"),
                AsGuid = Long("AsGuid"),
                AsBitTokens = Long("AsBitTokens"),
                LeadingZeroInts = Long("LeadingZeroInts"),
                MaxLen = Int("MaxLen"),
                MinValue = NullableLong("MinVal"),
                MaxValue = NullableLong("MaxVal"),
                DateTimeCandidates = dateTimeStyles
                    .Select(s => new DateConversionCandidate { Style = s, Count = Long($"T{s.ToString(CultureInfo.InvariantCulture)}") })
                    .ToList(),
                DateCandidates = dateStyles
                    .Select(s => new DateConversionCandidate { Style = s, Count = Long($"D{s.ToString(CultureInfo.InvariantCulture)}") })
                    .ToList(),
                NumericCandidates = numericFormats
                    .Select(f => new NumericConversionCandidate
                    {
                        Format = f,
                        AsDecimal = Long($"Dec{f}"),
                        AsFloat = Long($"Flt{f}"),
                        MaxScale = Int($"Scl{f}"),
                        MaxIntegerDigits = Int($"Int{f}"),
                    })
                    .ToList(),
            });
        }

        return profiles;
    }

    private static string BuildSql(
        string qualified,
        string column,
        string top,
        IReadOnlyList<int> dateTimeStyles,
        IReadOnlyList<int> dateStyles,
        IReadOnlyList<NumericFormat> numericFormats,
        ServerLocale locale)
    {
        var aggregates = new List<string>
        {
            "COUNT_BIG(*)                                                        AS Total",
            "COUNT_BIG(x)                                                        AS NonNull",
            "COUNT_BIG(TRY_CONVERT(bigint, x))                                   AS AsBigInt",
            "COUNT_BIG(TRY_CONVERT(uniqueidentifier, x))                         AS AsGuid",
            "COUNT_BIG(CASE WHEN LOWER(x) IN ('0','1','true','false','yes','no','y','n') THEN 1 END) AS AsBitTokens",
            "COUNT_BIG(CASE WHEN x LIKE '0%' AND LEN(x) > 1 AND TRY_CONVERT(bigint, x) IS NOT NULL THEN 1 END) AS LeadingZeroInts",
            "ISNULL(MAX(LEN(x)), 0)                                              AS MaxLen",
            "MIN(TRY_CONVERT(bigint, x))                                         AS MinVal",
            "MAX(TRY_CONVERT(bigint, x))                                         AS MaxVal",
        };

        // A genuine date carries a 4-digit year. This guard rejects short hyphen/slash codes such as
        // "11-800" that SQL Server's lenient parser would otherwise silently accept as a date.
        foreach (var style in dateTimeStyles)
        {
            var s = style.ToString(CultureInfo.InvariantCulture);
            aggregates.Add($"COUNT_BIG(CASE WHEN {DateShapeGuard} AND TRY_CONVERT(datetime2, x, {s}) IS NOT NULL THEN 1 END) AS T{s}");
        }

        foreach (var style in dateStyles)
        {
            var s = style.ToString(CultureInfo.InvariantCulture);
            aggregates.Add($"COUNT_BIG(CASE WHEN {DateShapeGuard} AND TRY_CONVERT(date, x, {s}) IS NOT NULL THEN 1 END) AS D{s}");
        }

        foreach (var format in numericFormats)
        {
            var n = LocaleConversion.NumericInput("x", format, locale);
            aggregates.Add($"COUNT_BIG(TRY_CONVERT(decimal(38,10), {n})) AS Dec{format}");
            aggregates.Add($"COUNT_BIG(TRY_CONVERT(float, {n})) AS Flt{format}");
            aggregates.Add(
                $"ISNULL(MAX(CASE WHEN CHARINDEX('.', {n}) > 0 AND TRY_CONVERT(decimal(38,10), {n}) IS NOT NULL " +
                $"THEN LEN({n}) - CHARINDEX('.', {n}) ELSE 0 END), 0) AS Scl{format}");
            aggregates.Add(
                $"ISNULL(MAX(CASE WHEN TRY_CONVERT(decimal(38,10), {n}) IS NOT NULL " +
                $"THEN LEN(REPLACE(REPLACE(CASE WHEN CHARINDEX('.', {n}) > 0 THEN LEFT({n}, CHARINDEX('.', {n}) - 1) ELSE {n} END, '-', ''), '+', '')) " +
                $"ELSE 0 END), 0) AS Int{format}");
        }

        var select = string.Join(",\n    ", aggregates);
        return $"""
            WITH v AS (
                SELECT {top}NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), {column}))), '') AS x
                FROM {qualified}
            )
            SELECT
                {select}
            FROM v;
            """;
    }

    // A date must contain four consecutive digits (a year); guards against short codes being read as dates.
    private const string DateShapeGuard = "x LIKE '%[0-9][0-9][0-9][0-9]%'";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
