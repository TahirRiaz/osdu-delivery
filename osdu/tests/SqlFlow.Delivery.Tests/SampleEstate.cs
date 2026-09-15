using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The sample estate as a running flow meets it: the sample logs in the in-memory ingestion tables, with the payload
/// files they point at written under the test's own root. It is the same data the repository holds (one fixture,
/// <see cref="SampleWellLogs"/>), so what the suites exercise is what an operator would see.
/// </summary>
public static class SampleEstate
{
    /// <summary>The file the pre-ingestion flow landed the record rows from, as the ingestion tables record it.</summary>
    public const string FileName = "welllog_20260901.csv";

    /// <summary>The curve rows' file.</summary>
    public const string CurveFileName = "welllog_curves_20260901.csv";

    /// <summary>The parameter values a run of the sample flow carries.</summary>
    public static IReadOnlyDictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["logSource"] = SampleWellLogs.LogName,
    };

    /// <summary>The delivery key of the sample log at <paramref name="index"/>, as the mapping derives it.</summary>
    public static DeliveryKey Key(int index) => SampleWellLogs.Logs()[index].Key;

    /// <summary>The sample logs, in the order the estate holds them.</summary>
    public static IReadOnlyList<SampleLog> Logs(string logName = SampleWellLogs.LogName) => SampleWellLogs.Logs(logName);

    /// <summary>
    /// Builds the estate: writes each log's payload files under <paramref name="root"/> and fills the ingestion tables
    /// with the record and curve rows, stamped with the update time and the origin the pre and ing flows would have left.
    /// </summary>
    public static async Task<MemoryIngestionTables> BuildAsync(
        string root, DateTime updatedUtc, string logName = SampleWellLogs.LogName, TimeProvider? time = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var tables = new MemoryIngestionTables(time);
        var logs = SampleWellLogs.Logs(logName);
        for (var i = 0; i < logs.Count; i++)
        {
            var log = logs[i];
            await SampleWellLogs.WriteChunkAsync(Path.Combine(root, "curves", log.SourceProject, log.LogId), log, ct).ConfigureAwait(false);
            tables.Add(Record(log, updatedUtc, i + 1));
        }

        return tables;
    }

    /// <summary>One log as its row in the record table, with its curve rows beside it.</summary>
    public static MemoryRecord Record(SampleLog log, DateTime updatedUtc, long rowNumber)
    {
        ArgumentNullException.ThrowIfNull(log);
        var record = new MemoryRecord
        {
            Row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["source_project"] = log.SourceProject,
                ["log_id"] = log.LogId,
                ["wellbore_uwi"] = log.WellboreUwi,
                ["log_name"] = log.LogName,
                ["log_run"] = log.LogRun,
                ["log_source"] = log.SourceProject,
                ["index_min"] = log.IndexMin,
                ["index_max"] = log.IndexMax,
                ["index_increment"] = log.IndexIncrement,
                ["index_unit"] = log.IndexUnit,
                ["depth_coding"] = log.DepthCoding,
                ["elev_meas_ref"] = log.ElevMeasRef,
                ["creator"] = log.Creator,
                ["log_version"] = log.LogVersion,
                ["log_pass"] = log.LogPass,
                ["native_uid"] = log.NativeUid,
                ["update_date"] = log.UpdateDateUtc,
                ["curve_folder"] = log.Folder,
                ["payload_hash"] = log.GridHash(),
                ["chunk_count"] = 1L,
            },
            UpdatedUtc = updatedUtc,
            FileName = FileName,
            RowNumber = rowNumber,
        };

        for (var i = 0; i < log.Curves.Count; i++)
        {
            var curve = log.Curves[i];
            record.AddChild("curves", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["source_project"] = log.SourceProject,
                ["log_id"] = log.LogId,
                ["curve_ordinal"] = (long)i,
                ["curve_id"] = curve.CurveId,
                ["curve_unit"] = curve.Unit,
                ["index_unit"] = log.IndexUnit,
                ["index_min"] = log.IndexMin,
                ["index_max"] = log.IndexMax,
                ["curve_description"] = curve.Description,
                ["curve_version"] = "1",
                ["business_value"] = curve.BusinessValue,
            });
        }

        record.DatasetUpdatedUtc["curves"] = updatedUtc;
        return record;
    }

    /// <summary>
    /// Changes one record as the source would: the value moves, the business version moves with it, and the ingestion
    /// tables stamp the row as changed at <paramref name="updatedUtc"/>.
    /// </summary>
    public static MemoryRecord Change(MemoryRecord record, string column, object? value, DateTime updatedUtc, DateTime? businessVersion = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.Row[column] = value;
        record.Row["update_date"] = businessVersion ?? updatedUtc;
        record.UpdatedUtc = updatedUtc;
        return record;
    }

    /// <summary>Rewrites one log's payload files with new curve values, and moves the record's payload hash with them.</summary>
    public static async Task RewritePayloadAsync(string root, MemoryRecord record, SampleLog log, DateTime updatedUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(log);
        await SampleWellLogs.WriteChunkAsync(Path.Combine(root, "curves", log.SourceProject, log.LogId), log, ct).ConfigureAwait(false);
        record.Row["payload_hash"] = log.GridHash();
        record.Row["update_date"] = updatedUtc;
        record.UpdatedUtc = updatedUtc;
    }
}
