using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Hashing;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Tests;

/// <summary>One curve of a sample log: what it is called, what it holds, and the straight line its values follow.</summary>
/// <param name="CurveId">The curve's identifier, which is also its column in the payload files.</param>
/// <param name="Unit">The unit the source names, as the mapping's cache lookup sees it.</param>
/// <param name="Description">The curve description the record carries.</param>
/// <param name="BusinessValue">The business value the mapping resolves against the cache; null for the index curve.</param>
/// <param name="First">The value at the first depth.</param>
/// <param name="Step">How much the value moves per sample.</param>
public sealed record SampleCurve(string CurveId, string Unit, string Description, string? BusinessValue, double First, double Step);

/// <summary>One sample logging run: the metadata row the ingestion tables hold, and the curve grid its payload files carry.</summary>
public sealed record SampleLog(
    string SourceProject,
    string LogId,
    string WellboreUwi,
    string LogName,
    string LogRun,
    string LogVersion,
    string LogPass,
    string Creator,
    string NativeUid,
    string DepthCoding,
    string ElevMeasRef,
    string IndexUnit,
    double IndexMin,
    double IndexMax,
    double IndexIncrement,
    DateTime UpdateDateUtc,
    IReadOnlyList<SampleCurve> Curves)
{
    /// <summary>Where the log's payload files sit, relative to the flow's payload root.</summary>
    public string Folder => $"{SourceProject}/{LogId}";

    /// <summary>The delivery key the mapping derives for this log, which is what the ledger holds it under.</summary>
    public DeliveryKey Key => DeliveryKey.Derive(SampleWellLogs.System, [SourceProject, LogId]);

    /// <summary>The depths the grid is sampled at, inclusive of both ends.</summary>
    public IReadOnlyList<double> Depths()
    {
        var depths = new List<double>();
        var steps = (int)Math.Round((IndexMax - IndexMin) / IndexIncrement, MidpointRounding.AwayFromZero);
        for (var i = 0; i <= steps; i++)
        {
            depths.Add(Math.Round(IndexMin + (i * IndexIncrement), 6, MidpointRounding.AwayFromZero));
        }

        return depths;
    }

    /// <summary>The columns of the payload files: the index curve first, then every other curve in its declared order.</summary>
    public IReadOnlyList<string> Columns()
        => Curves.Select(c => c.CurveId).OrderBy(id => id == SampleWellLogs.IndexCurveId ? 0 : 1).ToList();

    /// <summary>The curve grid: one row per depth, one column per curve, the index curve holding the depth itself.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Grid()
    {
        var columns = Columns();
        var depths = Depths();
        var rows = new List<IReadOnlyDictionary<string, object?>>(depths.Count);
        for (var i = 0; i < depths.Count; i++)
        {
            var row = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
            foreach (var column in columns)
            {
                var curve = Curves.First(c => c.CurveId == column);
                row[column] = column == SampleWellLogs.IndexCurveId
                    ? depths[i]
                    : Math.Round(curve.First + (i * curve.Step), 6, MidpointRounding.AwayFromZero);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// The payload's content hash: a SHA-256 over the grid written as text, one line per depth, values separated by the
    /// unit separator and written round-trip. It is what the record row carries in <c>payload_hash</c>, and what a plan
    /// compares to decide whether the curve values moved.
    /// </summary>
    public string GridHash()
    {
        var columns = Columns();
        var text = new StringBuilder();
        foreach (var row in Grid())
        {
            text.Append(string.Join('', columns.Select(c => ((double)row[c]!).ToString("R", CultureInfo.InvariantCulture)))).Append('\n');
        }

        return ContentHash.Of(text.ToString());
    }
}

/// <summary>One sample wellbore: the master-data row the well logs reference, and its alternative names.</summary>
public sealed record SampleWellbore(string FacilityName, string? Description, string? FacilityId, DateTime UpdateDateUtc, IReadOnlyList<string> Aliases);

/// <summary>
/// The sample estate's data, in one place: the logging runs, their curves and the wellbores they hang off, plus the
/// files that carry them (the CSV files the pre flows read, and the parquet chunks the delivery streams to the wellbore
/// DDMS). The tool <c>osdu/tools/SampleData</c> writes the committed sample files from here, and the tests build the
/// same data into a temporary estate, so what the suites exercise and what the repository holds can never drift.
/// </summary>
public static class SampleWellLogs
{
    /// <summary>The source system the delivery keys are derived under, matching the sample mappings' <c>dataset.system</c>.</summary>
    public const string System = "recall";

    /// <summary>The index curve, whose values are the depths the other curves are sampled at.</summary>
    public const string IndexCurveId = "MD";

    /// <summary>The log source the sample flow's schedule fires for, and the scope its records sit in.</summary>
    public const string LogName = "STAT_COMP";

    /// <summary>The payload files of one log; the sample writes one chunk per log.</summary>
    public const string ChunkFileName = "chunk_00000.parquet";

    /// <summary>The moment the sample rows were last changed, which the delivery orders versions by.</summary>
    public static readonly DateTime UpdatedUtc = new(2026, 9, 1, 6, 30, 0, DateTimeKind.Utc);

    /// <summary>The logging runs of the sample estate, in the order their file lists them.</summary>
    public static IReadOnlyList<SampleLog> Logs(string logName = LogName) =>
    [
        new SampleLog(
            "NO_15_9", "L-1001", "OSDU-DEV-1-A", logName, "1", "1", "MAIN,REPEAT", "SLB", "NO_15_9:L-1001,extra",
            "REGULAR", "23.5 M", "M", 1000, 1004, 0.5, UpdatedUtc,
            [
                new SampleCurve(IndexCurveId, "M", "Measured depth", null, 1000, 0.5),
                new SampleCurve("GR", "GAPI", "Gamma ray", "HIGH", 45.2, 0.35),
                new SampleCurve("RHOB", "G/CM3", "Bulk density", "HIGH", 2.31, 0.004),
            ]),
        new SampleLog(
            "NO_15_9", "L-1002", "OSDU-DEV-1-A", logName, "2", "1", "MAIN", "SLB", "NO_15_9:L-1002",
            "REGULAR", "23.5 M", "M", 1010, 1012, 0.5, UpdatedUtc,
            [
                new SampleCurve(IndexCurveId, "M", "Measured depth", null, 1010, 0.5),
                new SampleCurve("GR", "GAPI", "Gamma ray", "MEDIUM", 51.8, -0.25),
            ]),
        new SampleLog(
            "NO_16_2", "L-2001", "OSDU-DEV-1-B", logName, "1", "2", "MAIN", "BHGE", "NO_16_2:L-2001",
            "DISCRETE", "18 FT", "FT", 1500, 1501.5, 0.5, UpdatedUtc,
            [
                new SampleCurve(IndexCurveId, "FT", "Measured depth", null, 1500, 0.5),
                new SampleCurve("NPHI", "V/V", "Neutron porosity", "MEDIUM", 0.21, 0.002),
            ]),
    ];

    /// <summary>The wellbores the sample logs reference.</summary>
    public static IReadOnlyList<SampleWellbore> Wellbores { get; } =
    [
        new SampleWellbore("OSDU-DEV-1-A", "Sample wellbore A", "srn:master-data/Wellbore:A", UpdatedUtc, ["WB-A", "15/9-A"]),
        new SampleWellbore("OSDU-DEV-1-B", "Sample wellbore B", "srn:master-data/Wellbore:B", UpdatedUtc, ["WB-B"]),
    ];

    /// <summary>The columns of the well log file the pre flow reads, in the order it writes them.</summary>
    public static IReadOnlyList<string> LogColumns { get; } =
    [
        "source_project", "log_id", "wellbore_uwi", "log_name", "log_run", "log_source", "index_min", "index_max",
        "index_increment", "index_unit", "depth_coding", "elev_meas_ref", "creator", "log_version", "log_pass",
        "native_uid", "update_date", "curve_folder", "payload_hash", "chunk_count",
    ];

    /// <summary>The columns of the curve file the pre flow reads.</summary>
    public static IReadOnlyList<string> CurveColumns { get; } =
    [
        "source_project", "log_id", "curve_ordinal", "curve_id", "curve_unit", "index_unit", "index_min", "index_max",
        "curve_description", "curve_version", "business_value",
    ];

    /// <summary>The columns of the wellbore file the pre flow reads.</summary>
    public static IReadOnlyList<string> WellboreColumns { get; } = ["facility_name", "facility_description", "facility_id", "update_date"];

    /// <summary>The columns of the wellbore alias file the pre flow reads.</summary>
    public static IReadOnlyList<string> AliasColumns { get; } = ["facility_name", "alias_name"];

    /// <summary>
    /// Writes the whole sample estate's data under <paramref name="dataRoot"/>: the four CSV files the pre flows read,
    /// and one parquet chunk per log under <c>curves/&lt;project&gt;/&lt;log&gt;</c>. Writing it again produces exactly
    /// the same bytes, so the committed files and a freshly generated estate compare equal.
    /// </summary>
    public static async Task WriteAsync(string dataRoot, string logName = LogName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var logs = Logs(logName);
        await WriteCsvAsync(Path.Combine(dataRoot, "welllog", "welllog_20260901.csv"), LogColumns, logs.Select(LogRow).ToList(), ct).ConfigureAwait(false);
        await WriteCsvAsync(Path.Combine(dataRoot, "curves-meta", "welllog_curves_20260901.csv"), CurveColumns, logs.SelectMany(CurveRows).ToList(), ct).ConfigureAwait(false);
        await WriteCsvAsync(
            Path.Combine(dataRoot, "wellbore", "wellbore_20260901.csv"),
            WellboreColumns,
            Wellbores.Select(w => new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["facility_name"] = w.FacilityName,
                ["facility_description"] = w.Description,
                ["facility_id"] = w.FacilityId,
                ["update_date"] = Moment(w.UpdateDateUtc),
            }).ToList<IReadOnlyDictionary<string, string?>>(),
            ct).ConfigureAwait(false);
        await WriteCsvAsync(
            Path.Combine(dataRoot, "wellbore-aliases", "wellbore_aliases_20260901.csv"),
            AliasColumns,
            Wellbores.SelectMany(w => w.Aliases.Select(alias => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["facility_name"] = w.FacilityName,
                ["alias_name"] = alias,
            })).ToList(),
            ct).ConfigureAwait(false);

        foreach (var log in logs)
        {
            await WriteChunkAsync(Path.Combine(dataRoot, "curves", log.SourceProject, log.LogId), log, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Writes one log's payload file (its whole curve grid as one row group) and returns the file's path.</summary>
    public static async Task<string> WriteChunkAsync(string folder, SampleLog log, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(log);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, ChunkFileName);
        var columns = log.Columns().Select(c => (c, typeof(double))).ToList();
        // The pandas entry is what a dataframe reader (and the wellbore DDMS) takes as the row labels, so a session of
        // several chunks aggregates by depth rather than by position.
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ParquetFiles.PandasMetadataKey] = new JsonObject { ["index_columns"] = new JsonArray(IndexCurveId) }.ToJsonString(),
        };

        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await ParquetFiles.WriteAsync(file, columns, log.Grid(), metadata, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>The record row of one log, as the file carries it.</summary>
    public static IReadOnlyDictionary<string, string?> LogRow(SampleLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["source_project"] = log.SourceProject,
            ["log_id"] = log.LogId,
            ["wellbore_uwi"] = log.WellboreUwi,
            ["log_name"] = log.LogName,
            ["log_run"] = log.LogRun,
            ["log_source"] = log.SourceProject,
            ["index_min"] = Number(log.IndexMin),
            ["index_max"] = Number(log.IndexMax),
            ["index_increment"] = Number(log.IndexIncrement),
            ["index_unit"] = log.IndexUnit,
            ["depth_coding"] = log.DepthCoding,
            ["elev_meas_ref"] = log.ElevMeasRef,
            ["creator"] = log.Creator,
            ["log_version"] = log.LogVersion,
            ["log_pass"] = log.LogPass,
            ["native_uid"] = log.NativeUid,
            ["update_date"] = Moment(log.UpdateDateUtc),
            ["curve_folder"] = log.Folder,
            ["payload_hash"] = log.GridHash(),
            ["chunk_count"] = "1",
        };
    }

    /// <summary>The curve rows of one log, in the order the record renders them.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> CurveRows(SampleLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var rows = new List<IReadOnlyDictionary<string, string?>>(log.Curves.Count);
        for (var i = 0; i < log.Curves.Count; i++)
        {
            var curve = log.Curves[i];
            rows.Add(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["source_project"] = log.SourceProject,
                ["log_id"] = log.LogId,
                ["curve_ordinal"] = i.ToString(CultureInfo.InvariantCulture),
                ["curve_id"] = curve.CurveId,
                ["curve_unit"] = curve.Unit,
                ["index_unit"] = log.IndexUnit,
                ["index_min"] = Number(log.IndexMin),
                ["index_max"] = Number(log.IndexMax),
                ["curve_description"] = curve.Description,
                ["curve_version"] = "1",
                ["business_value"] = curve.BusinessValue,
            });
        }

        return rows;
    }

    /// <summary>Writes one CSV file: a header row, then the rows, with the same bytes every time.</summary>
    public static async Task WriteCsvAsync(string path, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        var text = new StringBuilder();
        text.Append(string.Join(",", columns.Select(Escape))).Append('\n');
        foreach (var row in rows)
        {
            text.Append(string.Join(",", columns.Select(c => Escape(row.TryGetValue(c, out var value) ? value : null)))).Append('\n');
        }

        await File.WriteAllTextAsync(path, text.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Moment(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.AsSpan().IndexOfAny(',', '"', '\n') >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
