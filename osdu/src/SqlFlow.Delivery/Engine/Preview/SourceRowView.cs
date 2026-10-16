using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Engine.Preview;

/// <summary>A child dataset's rows as a reader sees them: the first rows, how many there are, and whether more were left out.</summary>
public sealed record SourceRowsView(IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows, int Total, bool Truncated);

/// <summary>
/// A record's rows as the ingestion tables hold them, shaped for a reader: the record row's columns and each child
/// dataset's rows, every value as invariant text. The record page's source read and the preview both show a row this way.
/// The child rows are bounded, and a preview bounds each value too, so a record with a very large child dataset or a
/// very long text column still answers in a bounded result.
/// </summary>
public static class SourceRowView
{
    /// <summary>The row's columns and values; a value longer than <paramref name="maxCellChars"/> is cut and says how long it was.</summary>
    public static IReadOnlyDictionary<string, string?> Columns(SourceRow row, int? maxCellChars = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Columns.ToDictionary(c => c, c => Cell(row.GetString(c), maxCellChars), StringComparer.Ordinal);
    }

    /// <summary>Each child dataset's first <paramref name="maxRows"/> rows, with the count of all of them.</summary>
    public static IReadOnlyDictionary<string, SourceRowsView> Datasets(SourceRecord record, int maxRows, int? maxCellChars = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRows);
        return record.Scopes.ToDictionary(
            s => s.Key,
            s => new SourceRowsView(
                s.Value.Take(maxRows).Select(r => Columns(r, maxCellChars)).ToList(),
                s.Value.Count,
                s.Value.Count > maxRows),
            StringComparer.Ordinal);
    }

    private static string? Cell(string? value, int? maxCellChars)
        => value is null || maxCellChars is not { } max || value.Length <= max
            ? value
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value[..max]}... ({value.Length} characters)");
}
