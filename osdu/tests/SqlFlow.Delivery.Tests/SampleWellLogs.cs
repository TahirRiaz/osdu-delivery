using System.Globalization;
using System.Text;
using Parquet;
using Parquet.Schema;
using SqlFlow.Delivery.Hashing;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Tests;

/// <summary>One curve of a sample log, as the Recall curve metadata describes it.</summary>
/// <param name="CurveId">The curve's mnemonic, which is also its column in the payload files.</param>
/// <param name="Unit">The unit as Recall writes it (GAPI, OHMM, G/CC), which the mapping translates through the cache.</param>
/// <param name="Description">The curve description the record carries.</param>
/// <param name="BusinessValue">Recall's business value (HIGH), or null where the source gives none.</param>
/// <param name="TopDepth">The first depth the curve holds a value at.</param>
/// <param name="BaseDepth">The last depth the curve holds a value at.</param>
/// <param name="Version">The curve version, or null where the source gives none.</param>
/// <param name="DateStamp">When the curve was last written in Recall (ISO 8601, UTC), or null.</param>
public sealed record SampleCurve(
    string CurveId, string Unit, string Description, string? BusinessValue, double TopDepth, double BaseDepth, string? Version, string? DateStamp);

/// <summary>
/// One Recall well log of the sample estate: its header row, its curves, and the curve grid its payload file carries (one
/// row per measured depth, one column per curve, a gap where a curve has no value at that depth).
/// </summary>
public sealed record SampleLog(
    string SourceProject,
    string LogId,
    string WellboreUwi,
    string LogSource,
    string LogRun,
    string RecallLogSource,
    string IndexType,
    string IndexUnit,
    double IndexMin,
    double IndexMax,
    double IndexIncrement,
    string DepthCoding,
    string ElevMeasRef,
    string LogsMeasFrom,
    string Creator,
    string LogService,
    string LogVersion,
    string DataType,
    string LoggingContractor,
    string LogPass,
    string LogPassType,
    string NativeUid,
    DateTime UpdateDateUtc,
    IReadOnlyList<SampleCurve> Curves,
    IReadOnlyList<double> Depths,
    IReadOnlyDictionary<string, IReadOnlyList<double?>> Values)
{
    /// <summary>Where the log's payload files sit, relative to the flow's payload root. A Recall log id holds a slash.</summary>
    public string Folder => $"{SourceProject}/{LogId.Replace('/', '_')}";

    /// <summary>The delivery key the mapping derives for this log, which is what the ledger holds it under.</summary>
    public DeliveryKey Key => DeliveryKey.Derive(SampleWellLogs.System, [SourceProject, LogId]);

    /// <summary>The columns of the payload files: the index curve first, then every other curve in its declared order.</summary>
    public IReadOnlyList<string> Columns()
        => [SampleWellLogs.IndexCurveId, .. Curves.Select(c => c.CurveId).Where(id => id != SampleWellLogs.IndexCurveId)];

    /// <summary>The curve grid: one row per depth, the index curve holding the depth itself.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Grid()
    {
        var columns = Columns();
        var rows = new List<IReadOnlyDictionary<string, object?>>(Depths.Count);
        for (var i = 0; i < Depths.Count; i++)
        {
            var row = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal) { [SampleWellLogs.IndexCurveId] = Depths[i] };
            foreach (var column in columns.Skip(1))
            {
                row[column] = Values[column][i];
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The same log with one curve's values changed, as a new recording of that curve would change them.</summary>
    public SampleLog WithValues(string curveId, Func<double?, double?> change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(curveId);
        ArgumentNullException.ThrowIfNull(change);
        if (!Values.TryGetValue(curveId, out var current))
        {
            throw new ArgumentException($"The log {LogId} has no curve '{curveId}'.", nameof(curveId));
        }

        var values = new Dictionary<string, IReadOnlyList<double?>>(Values, StringComparer.Ordinal) { [curveId] = current.Select(change).ToList() };
        return this with { Values = values };
    }

    /// <summary>
    /// The payload's content hash: a SHA-256 over the grid written as text, one line per depth, values separated by the
    /// unit separator, written round-trip, and a gap written as nothing. It is what the record row carries in
    /// <c>payload_hash</c>, and what a plan compares to decide whether the curve values moved.
    /// </summary>
    public string GridHash()
    {
        var columns = Columns();
        var text = new StringBuilder();
        foreach (var row in Grid())
        {
            text.Append(string.Join('\u001F', columns.Select(c => row[c] is double value ? value.ToString("R", CultureInfo.InvariantCulture) : string.Empty))).Append('\n');
        }

        return ContentHash.Of(text.ToString());
    }
}

/// <summary>
/// The sample estate's data, in one place: five real Recall STAT_COMP well logs exported from the Databricks tables
/// recall_to_osdu delivers from (osdu/tools/SampleData), and the files that carry them: the CSV files the pre flows read
/// and the parquet chunk per log the delivery streams to the wellbore DDMS. The exporter writes the committed files
/// through <see cref="WriteAsync"/>, and the suites read those same files back through <see cref="Logs"/>, so what the
/// suites exercise and what the repository holds can never drift.
/// </summary>
public static class SampleWellLogs
{
    /// <summary>The source system the delivery keys are derived under, matching the sample mapping's <c>dataset.system</c>.</summary>
    public const string System = "recall";

    /// <summary>The index curve, whose values are the depths the other curves are sampled at.</summary>
    public const string IndexCurveId = "MD";

    /// <summary>The log source the sample flow's schedule fires for, and the scope its records sit in.</summary>
    public const string LogSource = "STAT_COMP";

    /// <summary>The payload file of one log; each sample log fits one chunk.</summary>
    public const string ChunkFileName = "chunk_00000.parquet";

    /// <summary>The folder under the data root the pre flow reads the well log header files from.</summary>
    public const string LogFolder = "welllog";

    /// <summary>The folder under the data root the pre flow reads the curve metadata files from.</summary>
    public const string CurveFolder = "curves-meta";

    /// <summary>The folder under the data root the payload chunks sit in, one folder per log below it.</summary>
    public const string PayloadFolder = "curves";

    /// <summary>The columns of the well log file the pre flow reads, in the order it writes them.</summary>
    public static IReadOnlyList<string> LogColumns { get; } =
    [
        "source_project", "log_id", "wellbore_uwi", "log_source", "log_run", "recall_log_source", "index_type", "index_unit",
        "index_min", "index_max", "index_increment", "depth_coding", "elev_meas_ref", "logs_meas_from", "creator",
        "log_service", "log_version", "data_type", "logging_contractor", "log_pass", "log_pass_type", "native_uid",
        "update_date", "curve_folder", "payload_hash", "chunk_count",
    ];

    /// <summary>The columns of the curve file the pre flow reads.</summary>
    public static IReadOnlyList<string> CurveColumns { get; } =
    [
        "source_project", "log_id", "curve_ordinal", "curve_id", "curve_unit", "index_unit", "index_min", "index_max",
        "curve_description", "curve_version", "business_value", "update_date",
    ];

    /// <summary>The committed sample data, as the build copies it next to the binaries.</summary>
    public static string DataRoot => Path.Combine(AppContext.BaseDirectory, "samples", "recall", "data");

    private static readonly Lazy<IReadOnlyList<SampleLog>> Committed = new(() => LoadAsync(DataRoot).GetAwaiter().GetResult());

    /// <summary>The committed sample logs, in the order their file lists them, under <paramref name="logSource"/>.</summary>
    public static IReadOnlyList<SampleLog> Logs(string logSource = LogSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logSource);
        return logSource == LogSource ? Committed.Value : Committed.Value.Select(l => l with { LogSource = logSource }).ToList();
    }

    /// <summary>The file the committed well log header rows sit in, as the pre flow lands it.</summary>
    public static string LogFileName => Path.GetFileName(SingleCsv(Path.Combine(DataRoot, LogFolder)));

    /// <summary>The file the committed curve rows sit in.</summary>
    public static string CurveFileName => Path.GetFileName(SingleCsv(Path.Combine(DataRoot, CurveFolder)));

    /// <summary>When the committed log at <paramref name="index"/> was last changed in Recall.</summary>
    public static DateTime UpdatedUtc(int index) => Committed.Value[index].UpdateDateUtc;

    /// <summary>
    /// Reads the sample logs under <paramref name="dataRoot"/>: the header and curve files, and each log's payload chunk,
    /// checking that the header's payload hash is the hash of the grid its chunk holds.
    /// </summary>
    public static async Task<IReadOnlyList<SampleLog>> LoadAsync(string dataRoot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var headers = ReadCsv(SingleCsv(Path.Combine(dataRoot, LogFolder)));
        var curves = ReadCsv(SingleCsv(Path.Combine(dataRoot, CurveFolder)))
            .GroupBy(r => (r["source_project"]!, r["log_id"]!))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => int.Parse(r["curve_ordinal"]!, CultureInfo.InvariantCulture)).ToList());

        var logs = new List<SampleLog>(headers.Count);
        foreach (var row in headers)
        {
            var key = (row["source_project"]!, row["log_id"]!);
            if (!curves.TryGetValue(key, out var curveRows))
            {
                throw new InvalidDataException($"The sample log {key.Item1}/{key.Item2} has no curve rows.");
            }

            var header = FromRow(row, curveRows.Select(CurveFromRow).ToList());
            var (depths, values) = await ReadChunkAsync(Path.Combine(dataRoot, PayloadFolder, row["curve_folder"]!, ChunkFileName), ct).ConfigureAwait(false);
            var log = header with { Depths = depths, Values = values };
            if (!string.Equals(log.GridHash(), row["payload_hash"], StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The sample log {log.Folder} says its payload hash is {row["payload_hash"]}, but its chunk hashes to {log.GridHash()}.");
            }

            logs.Add(log);
        }

        return logs;
    }

    /// <summary>
    /// Writes the sample logs under <paramref name="dataRoot"/>: the header and curve files named for <paramref name="date"/>
    /// (yyyyMMdd), and one parquet chunk per log under <c>curves/&lt;project&gt;/&lt;log&gt;</c>. Writing the same logs
    /// again produces exactly the same bytes, so a new export shows only what moved in the source.
    /// </summary>
    public static async Task WriteAsync(string dataRoot, IReadOnlyList<SampleLog> logs, string date, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        await WriteCsvAsync(Path.Combine(dataRoot, LogFolder, $"stat_comp_welllog_{date}.csv"), LogColumns, logs.Select(LogRow).ToList(), ct).ConfigureAwait(false);
        await WriteCsvAsync(Path.Combine(dataRoot, CurveFolder, $"stat_comp_curves_{date}.csv"), CurveColumns, logs.SelectMany(CurveRows).ToList(), ct).ConfigureAwait(false);
        foreach (var log in logs)
        {
            await WriteChunkAsync(Path.Combine(dataRoot, PayloadFolder, log.Folder), log, ct).ConfigureAwait(false);
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
        // several chunks aggregates by depth rather than by position; it needs a descriptor for every column, the index too.
        var metadata = PandasMetadata.Stored(columns, IndexCurveId);

        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await ParquetFiles.WriteAsync(file, columns, log.Grid(), metadata, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>The header row of one log, as the file carries it.</summary>
    public static IReadOnlyDictionary<string, string?> LogRow(SampleLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["source_project"] = log.SourceProject,
            ["log_id"] = log.LogId,
            ["wellbore_uwi"] = log.WellboreUwi,
            ["log_source"] = log.LogSource,
            ["log_run"] = log.LogRun,
            ["recall_log_source"] = log.RecallLogSource,
            ["index_type"] = log.IndexType,
            ["index_unit"] = log.IndexUnit,
            ["index_min"] = Number(log.IndexMin),
            ["index_max"] = Number(log.IndexMax),
            ["index_increment"] = Number(log.IndexIncrement),
            ["depth_coding"] = log.DepthCoding,
            ["elev_meas_ref"] = log.ElevMeasRef,
            ["logs_meas_from"] = log.LogsMeasFrom,
            ["creator"] = log.Creator,
            ["log_service"] = log.LogService,
            ["log_version"] = log.LogVersion,
            ["data_type"] = log.DataType,
            ["logging_contractor"] = log.LoggingContractor,
            ["log_pass"] = log.LogPass,
            ["log_pass_type"] = log.LogPassType,
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
                ["index_min"] = Number(curve.TopDepth),
                ["index_max"] = Number(curve.BaseDepth),
                ["curve_description"] = curve.Description,
                ["curve_version"] = curve.Version,
                ["business_value"] = curve.BusinessValue,
                ["update_date"] = curve.DateStamp,
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

    /// <summary>Reads a CSV file this class wrote: a header row, then rows whose empty cells are no value.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> ReadCsv(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var records = ParseCsv(File.ReadAllText(path, Encoding.UTF8));
        if (records.Count == 0)
        {
            throw new InvalidDataException($"{path} has no header row.");
        }

        var header = records[0];
        var rows = new List<IReadOnlyDictionary<string, string?>>(records.Count - 1);
        foreach (var record in records.Skip(1))
        {
            if (record.Count != header.Count)
            {
                throw new InvalidDataException($"{path}: a row has {record.Count} cells where the header names {header.Count}.");
            }

            var row = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < header.Count; i++)
            {
                row[header[i]] = record[i].Length == 0 ? null : record[i];
            }

            rows.Add(row);
        }

        return rows;
    }

    private static SampleLog FromRow(IReadOnlyDictionary<string, string?> row, IReadOnlyList<SampleCurve> curves) => new(
        Text(row, "source_project"), Text(row, "log_id"), Text(row, "wellbore_uwi"), Text(row, "log_source"), Text(row, "log_run"),
        Text(row, "recall_log_source"), Text(row, "index_type"), Text(row, "index_unit"), Double(row, "index_min"),
        Double(row, "index_max"), Double(row, "index_increment"), Text(row, "depth_coding"), Text(row, "elev_meas_ref"),
        Text(row, "logs_meas_from"), Text(row, "creator"), Text(row, "log_service"), Text(row, "log_version"), Text(row, "data_type"),
        Text(row, "logging_contractor"), Text(row, "log_pass"), Text(row, "log_pass_type"), Text(row, "native_uid"),
        DateTime.SpecifyKind(DateTime.ParseExact(Text(row, "update_date"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc),
        curves, [], new Dictionary<string, IReadOnlyList<double?>>(StringComparer.Ordinal));

    private static SampleCurve CurveFromRow(IReadOnlyDictionary<string, string?> row) => new(
        Text(row, "curve_id"), Text(row, "curve_unit"), Text(row, "curve_description"), row["business_value"],
        Double(row, "index_min"), Double(row, "index_max"), row["curve_version"], row["update_date"]);

    private static async Task<(IReadOnlyList<double> Depths, IReadOnlyDictionary<string, IReadOnlyList<double?>> Values)> ReadChunkAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        using var reader = await ParquetReader.CreateAsync(file, cancellationToken: ct).ConfigureAwait(false);
        var fields = reader.Schema.GetDataFields();
        var columns = fields.ToDictionary(f => f.Name, _ => new List<double?>(), StringComparer.Ordinal);
        for (var group = 0; group < reader.RowGroupCount; group++)
        {
            using var rowGroup = reader.OpenRowGroupReader(group);
            foreach (DataField field in fields)
            {
                var column = await rowGroup.ReadColumnAsync(field, ct).ConfigureAwait(false);
                foreach (var value in column.Data)
                {
                    columns[field.Name].Add(value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture));
                }
            }
        }

        if (!columns.Remove(IndexCurveId, out var index) || index.Any(d => d is null))
        {
            throw new InvalidDataException($"{path} has no complete '{IndexCurveId}' column.");
        }

        return (index.Select(d => d!.Value).ToList(), columns.ToDictionary(c => c.Key, c => (IReadOnlyList<double?>)c.Value, StringComparer.Ordinal));
    }

    private static string SingleCsv(string folder)
    {
        var files = Directory.GetFiles(folder, "*.csv");
        return files.Length == 1
            ? files[0]
            : throw new InvalidDataException($"{folder} holds {files.Length} CSV files; the sample holds exactly one.");
    }

    private static string Text(IReadOnlyDictionary<string, string?> row, string column)
        => row.TryGetValue(column, out var value) && value is not null ? value : throw new InvalidDataException($"The sample row has no '{column}'.");

    private static double Double(IReadOnlyDictionary<string, string?> row, string column)
        => double.Parse(Text(row, column), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    cell.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    record.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\n':
                    record.Add(cell.ToString());
                    cell.Clear();
                    records.Add(record);
                    record = [];
                    break;
                case '\r':
                    break;
                default:
                    cell.Append(c);
                    break;
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("A CSV cell opens a quote it never closes.");
        }

        if (cell.Length > 0 || record.Count > 0)
        {
            record.Add(cell.ToString());
            records.Add(record);
        }

        return records;
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
