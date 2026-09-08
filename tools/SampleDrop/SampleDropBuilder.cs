using System.Globalization;
using System.Text;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Hashing;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.SampleDrop;

public sealed record SampleCurve(string CurveId, string Unit, string Description, string BusinessValue, string Version, double[] Values);

public sealed record SampleRecord(
    string SourceProject,
    string LogId,
    string WellboreUwi,
    string LogName,
    string LogRun,
    string LogSource,
    double IndexMin,
    double IndexMax,
    double Increment,
    string IndexUnit,
    string DepthCoding,
    string ElevMeasRef,
    string Creator,
    string LogVersion,
    string LogPass,
    string NativeUid,
    string UpdateDate,
    IReadOnlyList<SampleCurve> Curves)
{
    /// <summary>The same derivation the delivery service performs (design.md section 5.2): both halves agree with no coordination.</summary>
    public DeliveryKey Key => DeliveryKey.Derive("recall", [SourceProject, LogId]);

    public IReadOnlyList<double> Depths()
    {
        var count = (int)Math.Round((IndexMax - IndexMin) / Increment) + 1;
        return Enumerable.Range(0, count).Select(i => IndexMin + (i * Increment)).ToList();
    }
}

/// <summary>
/// Writes a drop the way Databricks would: one parquet file per scope, one payload chunk per record, and the
/// manifest last. The payload hash is computed over the logical grid, never the parquet bytes (design.md
/// section 6.4), with the algorithm documented in docs/drop-contract.md.
/// </summary>
public static class SampleDropBuilder
{
    public const string FlowName = "recall-welllog";
    public const string MappingReference = "WellLog@1.4.0";
    public const string SourceTable = "wl_pipelines_dsis_intermediate.recall_logcurve_enriched";

    public static IReadOnlyList<SampleRecord> DefaultRecords(string logSource = "STAT_COMP") =>
    [
        new(
            SourceProject: "NO_15_9", LogId: "L-1001", WellboreUwi: "OSDU-DEV-1-A", LogName: logSource, LogRun: "1", LogSource: "RECALL",
            IndexMin: 1000, IndexMax: 1004, Increment: 0.5, IndexUnit: "M", DepthCoding: "REGULAR", ElevMeasRef: "23.5 M", Creator: "SLB",
            LogVersion: "1", LogPass: "MAIN,REPEAT", NativeUid: "NO_15_9:L-1001,extra", UpdateDate: "2026-09-01T10:15:00Z",
            Curves:
            [
                new("GR", "GAPI", "Gamma ray", "HIGH", "1", [45.2, 47.1, 50.3, 52.0, 49.8, 47.2, 44.9, 43.3, 46.0]),
                new("RHOB", "G/CM3", "Bulk density", "HIGH", "1", [2.31, 2.33, 2.35, 2.34, 2.36, 2.38, 2.37, 2.35, 2.33]),
            ]),
        new(
            SourceProject: "NO_15_9", LogId: "L-1002", WellboreUwi: "OSDU-DEV-1-A", LogName: logSource, LogRun: "2", LogSource: "RECALL",
            IndexMin: 2000, IndexMax: 2002, Increment: 0.5, IndexUnit: "M", DepthCoding: "REGULAR", ElevMeasRef: "23.5 M", Creator: "SLB",
            LogVersion: "1", LogPass: "MAIN", NativeUid: "NO_15_9:L-1002", UpdateDate: "2026-09-02T08:00:00Z",
            Curves:
            [
                new("GR", "GAPI", "Gamma ray", "HIGH", "1", [61.0, 63.5, 60.2, 58.9, 62.1]),
            ]),
        new(
            SourceProject: "NO_16_2", LogId: "L-2001", WellboreUwi: "OSDU-DEV-1-B", LogName: logSource, LogRun: "1", LogSource: "RECALL",
            IndexMin: 1500, IndexMax: 1501.5, Increment: 0.5, IndexUnit: "FT", DepthCoding: "DISCRETE", ElevMeasRef: "18 FT", Creator: "BHGE",
            LogVersion: "2", LogPass: "MAIN", NativeUid: "NO_16_2:L-2001", UpdateDate: "2026-08-20T00:00:00Z",
            Curves:
            [
                new("NPHI", "V/V", "Neutron porosity", "MEDIUM", "1", [0.21, 0.22, 0.20, 0.19]),
            ]),
    ];

    public static async Task<DropManifest> WriteAsync(
        string root,
        string logSource,
        IReadOnlyList<SampleRecord> records,
        Guid submissionId,
        long sourceVersion,
        int partitions = 1,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitions, 1);
        Directory.CreateDirectory(Path.Combine(root, "metadata"));
        Directory.CreateDirectory(Path.Combine(root, "curves-meta"));

        var rootColumns = new (string, Type)[]
        {
            ("deliveryKey", typeof(string)), ("source_project", typeof(string)), ("log_id", typeof(string)),
            ("wellbore_uwi", typeof(string)), ("log_name", typeof(string)), ("log_run", typeof(string)), ("log_source", typeof(string)),
            ("index_min", typeof(double)), ("index_max", typeof(double)), ("index_increment", typeof(double)), ("index_unit", typeof(string)),
            ("depth_coding", typeof(string)), ("elev_meas_ref", typeof(string)), ("creator", typeof(string)), ("log_version", typeof(string)),
            ("log_pass", typeof(string)), ("native_uid", typeof(string)), ("update_date", typeof(string)),
            ("payloadHash", typeof(string)), ("chunkCount", typeof(long)),
        };
        var curveColumns = new (string, Type)[]
        {
            ("deliveryKey", typeof(string)), ("curve_ordinal", typeof(long)), ("curve_id", typeof(string)), ("curve_unit", typeof(string)),
            ("index_unit", typeof(string)), ("index_min", typeof(double)), ("index_max", typeof(double)),
            ("curve_description", typeof(string)), ("curve_version", typeof(string)), ("business_value", typeof(string)),
        };

        var rootRows = new List<IReadOnlyDictionary<string, object?>>();
        var curveRows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var record in records)
        {
            var key = record.Key.ToString();
            var depths = record.Depths();
            var grid = BuildGrid(record, depths);
            var payloadHash = HashGrid(grid.Columns, grid.Rows);
            var chunkDir = Path.Combine(root, "curves", key);
            Directory.CreateDirectory(chunkDir);
            await using (var chunk = File.Create(Path.Combine(chunkDir, "chunk_00000.parquet")))
            {
                await ParquetScopeReader.WriteAsync(chunk, grid.Columns.Select(c => (c, typeof(double))).ToList(), grid.Rows, ct).ConfigureAwait(false);
            }

            rootRows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deliveryKey"] = key,
                ["source_project"] = record.SourceProject,
                ["log_id"] = record.LogId,
                ["wellbore_uwi"] = record.WellboreUwi,
                ["log_name"] = record.LogName,
                ["log_run"] = record.LogRun,
                ["log_source"] = record.LogSource,
                ["index_min"] = record.IndexMin,
                ["index_max"] = record.IndexMax,
                ["index_increment"] = record.Increment,
                ["index_unit"] = record.IndexUnit,
                ["depth_coding"] = record.DepthCoding,
                ["elev_meas_ref"] = record.ElevMeasRef,
                ["creator"] = record.Creator,
                ["log_version"] = record.LogVersion,
                ["log_pass"] = record.LogPass,
                ["native_uid"] = record.NativeUid,
                ["update_date"] = record.UpdateDate,
                ["payloadHash"] = payloadHash,
                ["chunkCount"] = 1L,
            });

            var ordinal = 0L;
            foreach (var curve in record.Curves)
            {
                curveRows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["deliveryKey"] = key,
                    ["curve_ordinal"] = ordinal++,
                    ["curve_id"] = curve.CurveId,
                    ["curve_unit"] = curve.Unit,
                    ["index_unit"] = record.IndexUnit,
                    ["index_min"] = record.IndexMin,
                    ["index_max"] = record.IndexMax,
                    ["curve_description"] = curve.Description,
                    ["curve_version"] = curve.Version,
                    ["business_value"] = curve.BusinessValue,
                });
            }
        }

        // A partitioned drop (docs/drop-contract.md): root file i and child file i hold the same records, every file
        // sorted by its key, so the delivery service streams each partition as a merge join. Keys are dealt
        // round-robin in sorted order, which keeps the partitions balanced.
        var rootFiles = new List<string>();
        var curveFiles = new List<string>();
        var ordered = rootRows.OrderBy(r => (string)r["deliveryKey"]!, StringComparer.Ordinal).ToList();
        for (var partition = 0; partition < partitions; partition++)
        {
            var name = "part-" + partition.ToString("D5", CultureInfo.InvariantCulture) + ".parquet";
            var mine = partitions == 1 ? ordered : ordered.Where((_, i) => i % partitions == partition).ToList();
            var keys = new HashSet<string>(mine.Select(r => (string)r["deliveryKey"]!), StringComparer.Ordinal);
            var curves = curveRows
                .Where(r => keys.Contains((string)r["deliveryKey"]!))
                .OrderBy(r => (string)r["deliveryKey"]!, StringComparer.Ordinal)
                .ThenBy(r => (long)r["curve_ordinal"]!)
                .ToList();

            await using (var file = File.Create(Path.Combine(root, "metadata", name)))
            {
                await ParquetScopeReader.WriteAsync(file, rootColumns, mine, ct).ConfigureAwait(false);
            }

            await using (var file = File.Create(Path.Combine(root, "curves-meta", name)))
            {
                await ParquetScopeReader.WriteAsync(file, curveColumns, curves, ct).ConfigureAwait(false);
            }

            rootFiles.Add("metadata/" + name);
            curveFiles.Add("curves-meta/" + name);
        }

        var manifest = new DropManifest
        {
            SubmissionId = submissionId,
            Flow = FlowName,
            Mapping = MappingReference,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["logSource"] = logSource },
            CreatedUtc = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero),
            RecordCount = records.Count,
            SourceVersions = new Dictionary<string, long>(StringComparer.Ordinal) { [SourceTable] = sourceVersion },
            Partitioned = partitions > 1,
            Scopes = new Dictionary<string, ManifestScope>(StringComparer.Ordinal)
            {
                ["record"] = new()
                {
                    Files = rootFiles,
                    Columns = rootColumns.Select(c => new ManifestColumn { Name = c.Item1, Type = TypeName(c.Item2) }).ToList(),
                },
                ["curves"] = new()
                {
                    Files = curveFiles,
                    ParentKey = "deliveryKey",
                    OrderBy = "curve_ordinal",
                    Columns = curveColumns.Select(c => new ManifestColumn { Name = c.Item1, Type = TypeName(c.Item2) }).ToList(),
                },
            },
            Payloads = new Dictionary<string, ManifestPayload>(StringComparer.Ordinal)
            {
                ["curves"] = new() { PathTemplate = "curves/{deliveryKey}/chunk_*.parquet", HashColumn = "payloadHash", ChunkCountColumn = "chunkCount" },
            },
        };

        // The manifest is written last: its presence means the drop is complete.
        await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), manifest.ToJson(), ct).ConfigureAwait(false);
        return manifest;
    }

    private static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows) BuildGrid(SampleRecord record, IReadOnlyList<double> depths)
    {
        var columns = new List<string> { "MD" };
        columns.AddRange(record.Curves.Select(c => c.CurveId).OrderBy(c => c, StringComparer.Ordinal));
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (var i = 0; i < depths.Count; i++)
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["MD"] = depths[i] };
            foreach (var curve in record.Curves)
            {
                row[curve.CurveId] = i < curve.Values.Length ? curve.Values[i] : null;
            }

            rows.Add(row);
        }

        return (columns, rows);
    }

    /// <summary>
    /// The logical grid hash (docs/drop-contract.md): SHA-256 over UTF-8 lines, the first line the column names and
    /// then one line per row in index order, cells joined by the unit separator (U+001F), numbers in shortest
    /// round-trip form, nulls empty. Databricks computes the same in Python.
    /// </summary>
    public static string HashGrid(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        sb.Append(string.Join((char)0x1F, columns)).Append('\n');
        foreach (var row in rows)
        {
            var first = true;
            foreach (var column in columns)
            {
                if (!first)
                {
                    sb.Append((char)0x1F);
                }

                first = false;
                var value = row.TryGetValue(column, out var v) ? v : null;
                sb.Append(value switch
                {
                    null => string.Empty,
                    double d => d.ToString("R", CultureInfo.InvariantCulture),
                    float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
                    long l => l.ToString(CultureInfo.InvariantCulture),
                    int i => i.ToString(CultureInfo.InvariantCulture),
                    bool b => b ? "true" : "false",
                    _ => value.ToString(),
                });
            }

            sb.Append('\n');
        }

        return ContentHash.Of(sb.ToString());
    }

    private static string TypeName(Type type) => type == typeof(double) ? "double" : type == typeof(long) ? "long" : type == typeof(bool) ? "boolean" : "string";
}
