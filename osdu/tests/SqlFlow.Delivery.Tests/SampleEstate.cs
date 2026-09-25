using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The sample estate as a running flow meets it: the sample logs in the in-memory ingestion tables, with the payload
/// files they point at written under the test's own root. It is the data the repository holds (the real Recall logs of
/// osdu/samples/recall, read by <see cref="SampleWellLogs"/>), so what the suites exercise is what an operator would see.
/// </summary>
public static class SampleEstate
{
    /// <summary>The file the pre-ingestion flow landed the record rows from, as the ingestion tables record it.</summary>
    public static string FileName => SampleWellLogs.LogFileName;

    /// <summary>The curve rows' file.</summary>
    public static string CurveFileName => SampleWellLogs.CurveFileName;

    /// <summary>The parameter values a run of the sample flow carries.</summary>
    public static IReadOnlyDictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["logSource"] = SampleWellLogs.LogSource,
    };

    /// <summary>The delivery key of the sample log at <paramref name="index"/>, as the mapping derives it.</summary>
    public static DeliveryKey Key(int index) => SampleWellLogs.Logs()[index].Key;

    /// <summary>The sample logs, in the order the estate holds them.</summary>
    public static IReadOnlyList<SampleLog> Logs(string logSource = SampleWellLogs.LogSource) => SampleWellLogs.Logs(logSource);

    /// <summary>
    /// Builds the estate: writes each log's payload files under <paramref name="root"/> and fills the ingestion tables
    /// with the record and curve rows, stamped with the update time and the origin the pre and ing flows would have left.
    /// </summary>
    public static async Task<MemoryIngestionTables> BuildAsync(
        string root, DateTime updatedUtc, string logSource = SampleWellLogs.LogSource, TimeProvider? time = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var tables = new MemoryIngestionTables(time);
        var logs = SampleWellLogs.Logs(logSource);
        for (var i = 0; i < logs.Count; i++)
        {
            var log = logs[i];
            await SampleWellLogs.WriteChunkAsync(PayloadFolder(root, log), log, ct).ConfigureAwait(false);
            tables.Add(Record(log, updatedUtc, i + 1));
        }

        return tables;
    }

    /// <summary>Where a log's payload files sit under a flow's payload root of <paramref name="root"/>/curves.</summary>
    public static string PayloadFolder(string root, SampleLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(log);
        return Path.Combine([root, SampleWellLogs.PayloadFolder, .. log.Folder.Split('/')]);
    }

    /// <summary>One log as its row in the record table, with its curve rows beside it.</summary>
    public static MemoryRecord Record(SampleLog log, DateTime updatedUtc, long rowNumber)
    {
        ArgumentNullException.ThrowIfNull(log);
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            // The identity primary key the ingestion flow gives the table (target.identityColumn): the row's place in it.
            ["RecId"] = rowNumber,
        };
        foreach (var (column, text) in SampleWellLogs.LogRow(log))
        {
            row[column] = text;
        }

        // The columns the pre flow types rather than keeps as text.
        row["index_min"] = log.IndexMin;
        row["index_max"] = log.IndexMax;
        row["index_increment"] = log.IndexIncrement;
        row["update_date"] = log.UpdateDateUtc;
        row["chunk_count"] = 1L;

        var record = new MemoryRecord
        {
            Row = row,
            UpdatedUtc = updatedUtc,
            FileName = FileName,
            RowNumber = rowNumber,
        };

        foreach (var curve in SampleWellLogs.CurveRows(log))
        {
            var child = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, text) in curve)
            {
                child[column] = text;
            }

            child["curve_ordinal"] = long.Parse(curve["curve_ordinal"]!, System.Globalization.CultureInfo.InvariantCulture);
            child["index_min"] = double.Parse(curve["index_min"]!, System.Globalization.CultureInfo.InvariantCulture);
            child["index_max"] = double.Parse(curve["index_max"]!, System.Globalization.CultureInfo.InvariantCulture);
            record.AddChild("curves", child);
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
        await SampleWellLogs.WriteChunkAsync(PayloadFolder(root, log), log, ct).ConfigureAwait(false);
        record.Row["payload_hash"] = log.GridHash();
        record.Row["update_date"] = updatedUtc;
        record.UpdatedUtc = updatedUtc;
    }
}
