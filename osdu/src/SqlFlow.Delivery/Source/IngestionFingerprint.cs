using System.Globalization;
using System.Text;
using SqlFlow.Delivery.Hashing;

namespace SqlFlow.Delivery.Source;

/// <summary>What one child dataset contributes to a record's fingerprint: how many rows it holds and the newest of their update times.</summary>
public readonly record struct DatasetVersion(int Rows, DateTime? MaxUpdatedUtc);

/// <summary>
/// The version the ingestion tables give a record (docs/stage4-design.md section 2.5): a SHA-256 over the record row's
/// <c>UpdatedDate_DW</c> and, per child dataset in name order, its row count and the newest <c>UpdatedDate_DW</c> among its
/// rows. It changes when any row of the record is inserted, updated or removed, which is what lets a run decide a record is
/// unchanged without rendering it, and it is stable across runs, nodes and machines, which is what lets the ledger compare
/// today's read with the version it holds.
/// </summary>
public static class IngestionFingerprint
{
    /// <summary>The fingerprint of one record.</summary>
    /// <param name="recordUpdatedUtc">The record row's update time, or null when the row carries none.</param>
    /// <param name="datasets">Each child dataset by name, with its row count and newest update time.</param>
    public static string Of(DateTime? recordUpdatedUtc, IReadOnlyDictionary<string, DatasetVersion> datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        var text = new StringBuilder();
        text.Append(SourceDatasets.Record).Append((char)0x1F).Append(Ticks(recordUpdatedUtc)).Append('\n');
        foreach (var name in datasets.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            var dataset = datasets[name];
            text.Append(name).Append((char)0x1F)
                .Append(dataset.Rows.ToString(CultureInfo.InvariantCulture)).Append((char)0x1F)
                .Append(Ticks(dataset.MaxUpdatedUtc)).Append('\n');
        }

        return ContentHash.Of(text.ToString());
    }

    private static string Ticks(DateTime? value)
        => value is { } moment
            ? (moment.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(moment, DateTimeKind.Utc) : moment.ToUniversalTime()).Ticks.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
}
