using System.Globalization;
using System.Text.Json;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace SqlFlow.Delivery.Storage;

/// <summary>The dimensions of a parquet file: its rows, and its top-level scalar columns.</summary>
public readonly record struct ParquetShape(long Rows, int Columns)
{
    /// <summary>Cells in the file, which is what a bulk service budgets against.</summary>
    public long Values => Model.WellboreDdmsBulkLimits.Values(Rows, Columns);

    /// <summary>
    /// The labels a dataframe reader gives the rows, or null when the footer cannot tell them: a multi-level index, a
    /// stored index column whose labels are not numbers, or pandas metadata that does not parse.
    /// </summary>
    public ParquetRowIndex? RowIndex { get; init; }

    /// <summary>The data columns, without a stored index column.</summary>
    public IReadOnlyList<string> ColumnNames { get; init; } = [];
}

/// <summary>
/// The row labels of a parquet file as a dataframe reader such as pandas gives them, which is how the wellbore DDMS
/// reads a bulk chunk: the lowest and the highest label, and where the labels come from.
/// </summary>
/// <param name="First">The lowest label.</param>
/// <param name="Last">The highest label.</param>
/// <param name="Source">Where the labels come from.</param>
public readonly record struct ParquetRowIndex(double First, double Last, ParquetRowIndexSource Source)
{
    /// <summary>True when the two ranges share at least one label.</summary>
    public bool Overlaps(ParquetRowIndex other) => First <= other.Last && other.First <= Last;

    /// <summary>True when both ranges run between the same two labels.</summary>
    public bool SameLabels(ParquetRowIndex other) => First.Equals(other.First) && Last.Equals(other.Last);
}

/// <summary>Where a parquet file's row labels come from.</summary>
public enum ParquetRowIndexSource
{
    /// <summary>No pandas metadata, or an empty index list: a reader numbers the rows from zero.</summary>
    Implicit,

    /// <summary>A pandas RangeIndex, described by its start and step in the file's metadata.</summary>
    Range,

    /// <summary>An index stored as a column that the pandas metadata names.</summary>
    Column,
}

/// <summary>
/// The parquet files the delivery domain touches without parsing their rows: a payload chunk's footer, measured against
/// the target's bulk ceilings and the other chunks of its session, a forward-only stream made seekable, the values a
/// parquet column type reads as, and the writers the sample estate and the tests use.
/// </summary>
public static class ParquetFiles
{
    /// <summary>The key-value metadata entry a pandas writer (pyarrow, fastparquet) leaves in the footer.</summary>
    public const string PandasMetadataKey = "pandas";

    /// <summary>
    /// Parquet needs a seekable stream (the footer is at the end). A forward-only stream is spilled to a temporary
    /// file that is deleted when the returned stream closes: disk, never memory, so a file of any size reads in bounded
    /// memory. Stores that can seek natively (local files, blobs through the range reader) never come here.
    /// </summary>
    public static async Task<Stream> EnsureSeekableAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek)
        {
            return stream;
        }

        var path = Path.Combine(Path.GetTempPath(), "osdu-delivery-spill-" + Guid.NewGuid().ToString("N") + ".parquet");
        var spill = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                await stream.CopyToAsync(spill, ct).ConfigureAwait(false);
            }

            spill.Position = 0;
            return spill;
        }
        catch
        {
            await spill.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The shape of a file from its footer: the schema's top-level scalar columns, the row counts the row group
    /// headers declare, and the labels a dataframe reader gives the rows. The labels come from the pandas metadata the
    /// writer left (a stored index column's statistics, or a range's start and step); a file without that metadata
    /// numbers its rows from zero. Only a stored index column whose row groups carry no statistics is read, and only
    /// that column, so the cost stays the footer whatever the file holds. This is how a payload chunk is measured
    /// against the target's bulk ceilings, and against the other chunks of its session, without parsing it
    /// (design.md sections 13.1 and 14.3).
    /// </summary>
    public static async Task<ParquetShape> ReadShapeAsync(Stream seekable, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(seekable);
        using var reader = await ParquetReader.CreateAsync(seekable, leaveStreamOpen: true, cancellationToken: ct).ConfigureAwait(false);
        var fields = reader.Schema.Fields.OfType<DataField>().ToList();
        var index = PandasIndex.From(reader.CustomMetadata);
        var indexField = index.Column is null ? null : fields.Find(f => string.Equals(f.Name, index.Column, StringComparison.Ordinal));

        // A metadata entry naming a column the file does not have leaves the labels unknown.
        var labelled = index.Kind != PandasIndexKind.Column || indexField is not null;
        long rows = 0;
        var low = double.PositiveInfinity;
        var high = double.NegativeInfinity;
        for (var group = 0; group < reader.RowGroupCount; group++)
        {
            ct.ThrowIfCancellationRequested();
            using var rowGroup = reader.OpenRowGroupReader(group);
            rows += rowGroup.RowCount;
            if (indexField is null || !labelled || rowGroup.RowCount == 0)
            {
                continue;
            }

            if (await LabelBoundsAsync(rowGroup, indexField, ct).ConfigureAwait(false) is { } bounds)
            {
                low = Math.Min(low, bounds.Low);
                high = Math.Max(high, bounds.High);
            }
            else
            {
                labelled = false;
            }
        }

        return new ParquetShape(rows, fields.Count)
        {
            RowIndex = labelled && rows > 0 ? index.Labels(rows, low, high) : null,
            ColumnNames = fields.Where(f => !ReferenceEquals(f, indexField)).Select(f => f.Name).ToList(),
        };
    }

    /// <summary>
    /// Collapses the parquet CLR types to the small set the renderer understands. A float is read as the number it was
    /// written as (12.3, not the 12.300000190734863 its bits widen to), a decimal and an unsigned 64-bit value keep their
    /// exact value, and a NaN is read as a missing value, which is how numpy and pandas store one.
    /// </summary>
    public static object? Normalize(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b,
        byte or sbyte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong ul => ul <= long.MaxValue ? (long)ul : (decimal)ul,
        float f => float.IsNaN(f) ? null : Rendering.NumberValues.Widen(f),
        double d => double.IsNaN(d) ? null : d,
        decimal m => m,
        DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime()),
        DateTimeOffset dto => dto.ToUniversalTime(),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g,
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => value.ToString(),
    };

    /// <summary>Writes rows as one row group, for the sample estate and the tests.</summary>
    public static Task WriteAsync(Stream target, IReadOnlyList<(string Name, Type ClrType)> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken ct = default)
        => WriteAsync(target, columns, rows, null, ct);

    /// <summary>
    /// Writes rows as one row group with the given key-value metadata in the footer, such as the pandas entry a
    /// dataframe writer leaves to say which column is the index.
    /// </summary>
    public static async Task WriteAsync(
        Stream target, IReadOnlyList<(string Name, Type ClrType)> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyDictionary<string, string>? metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        var fields = columns.Select(c => new DataField(c.Name, MakeNullable(c.ClrType))).ToArray();
        var schema = new ParquetSchema(fields.Cast<Field>().ToArray());
        using var writer = await ParquetWriter.CreateAsync(schema, target, cancellationToken: ct).ConfigureAwait(false);
        writer.CompressionMethod = CompressionMethod.Snappy;
        if (metadata is not null)
        {
            writer.CustomMetadata = metadata;
        }

        using var rowGroup = writer.CreateRowGroup();
        for (var i = 0; i < fields.Length; i++)
        {
            var name = columns[i].Name;
            var clr = MakeNullable(columns[i].ClrType);
            var array = Array.CreateInstance(clr, rows.Count);
            for (var r = 0; r < rows.Count; r++)
            {
                var v = rows[r].TryGetValue(name, out var raw) ? raw : null;
                array.SetValue(v is null ? null : Convert.ChangeType(v, Nullable.GetUnderlyingType(clr) ?? clr, CultureInfo.InvariantCulture), r);
            }

            await rowGroup.WriteColumnAsync(new DataColumn(fields[i], array), ct).ConfigureAwait(false);
        }
    }

    internal static Type MakeNullable(Type type)
        => type.IsValueType && Nullable.GetUnderlyingType(type) is null ? typeof(Nullable<>).MakeGenericType(type) : type;

    /// <summary>
    /// The lowest and highest label a stored index column holds in one row group: from the row group's statistics,
    /// or from that one column when the writer kept none. Null when a label is not a number.
    /// </summary>
    private static async Task<(double Low, double High)?> LabelBoundsAsync(ParquetRowGroupReader rowGroup, DataField field, CancellationToken ct)
    {
        var statistics = rowGroup.GetStatistics(field);
        if (statistics is not null && Number(statistics.MinValue) is { } min && Number(statistics.MaxValue) is { } max)
        {
            return (min, max);
        }

        var column = await rowGroup.ReadColumnAsync(field, ct).ConfigureAwait(false);
        double? low = null;
        double? high = null;
        foreach (var value in column.Data)
        {
            if (value is null)
            {
                continue;
            }

            if (Number(value) is not { } label)
            {
                return null;
            }

            low = low is { } l ? Math.Min(l, label) : label;
            high = high is { } h ? Math.Max(h, label) : label;
        }

        return low is { } lowest && high is { } highest ? (lowest, highest) : null;
    }

    private static double? Number(object? value) => value switch
    {
        byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        float f when float.IsFinite(f) => f,
        double d when double.IsFinite(d) => d,
        decimal m => (double)m,
        _ => null,
    };

    private enum PandasIndexKind
    {
        Implicit,
        Range,
        Column,
        Unknown,
    }

    /// <summary>What a file's pandas metadata says its index is (pandas' <c>index_columns</c> entry).</summary>
    private readonly record struct PandasIndex(PandasIndexKind Kind, string? Column, double Start, double Step)
    {
        private static readonly PandasIndex Implicit = new(PandasIndexKind.Implicit, null, 0, 1);

        private static readonly PandasIndex Unknown = new(PandasIndexKind.Unknown, null, 0, 0);

        public static PandasIndex From(IReadOnlyDictionary<string, string> metadata)
        {
            if (!metadata.TryGetValue(PandasMetadataKey, out var json) || string.IsNullOrWhiteSpace(json))
            {
                return Implicit;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                // A reader that cannot parse the entry cannot say what the labels are either. The labels stay unknown,
                // which leaves the session to the committed-row check rather than guessing an index for the chunk.
                return Unknown;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("index_columns", out var columns)
                    || columns.ValueKind != JsonValueKind.Array
                    || columns.GetArrayLength() == 0)
                {
                    return Implicit;
                }

                if (columns.GetArrayLength() > 1)
                {
                    return Unknown;
                }

                var only = columns[0];
                if (only.ValueKind == JsonValueKind.String)
                {
                    return new PandasIndex(PandasIndexKind.Column, only.GetString(), 0, 0);
                }

                return only.ValueKind == JsonValueKind.Object
                    && only.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.ValueEquals("range")
                    && only.TryGetProperty("start", out var start) && start.TryGetDouble(out var first)
                    && only.TryGetProperty("step", out var step) && step.TryGetDouble(out var stride) && stride != 0
                        ? new PandasIndex(PandasIndexKind.Range, null, first, stride)
                        : Unknown;
            }
        }

        public ParquetRowIndex? Labels(long rows, double low, double high) => Kind switch
        {
            PandasIndexKind.Implicit => new ParquetRowIndex(0, rows - 1, ParquetRowIndexSource.Implicit),
            PandasIndexKind.Range => new ParquetRowIndex(
                Math.Min(Start, Start + (Step * (rows - 1))), Math.Max(Start, Start + (Step * (rows - 1))), ParquetRowIndexSource.Range),
            PandasIndexKind.Column when low <= high => new ParquetRowIndex(low, high, ParquetRowIndexSource.Column),
            _ => null,
        };
    }
}

/// <summary>Writes a parquet file one row group at a time, so a file of any size never sits in memory whole.</summary>
public sealed class ParquetRowGroupWriter : IAsyncDisposable
{
    private readonly ParquetWriter _writer;
    private readonly DataField[] _fields;
    private readonly IReadOnlyList<(string Name, Type ClrType)> _columns;

    private ParquetRowGroupWriter(ParquetWriter writer, DataField[] fields, IReadOnlyList<(string Name, Type ClrType)> columns)
    {
        _writer = writer;
        _fields = fields;
        _columns = columns;
    }

    public static async Task<ParquetRowGroupWriter> CreateAsync(Stream target, IReadOnlyList<(string Name, Type ClrType)> columns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(columns);
        var fields = columns.Select(c => new DataField(c.Name, ParquetFiles.MakeNullable(c.ClrType))).ToArray();
        var schema = new ParquetSchema(fields.Cast<Field>().ToArray());
        var writer = await ParquetWriter.CreateAsync(schema, target, cancellationToken: ct).ConfigureAwait(false);
        writer.CompressionMethod = CompressionMethod.Snappy;
        return new ParquetRowGroupWriter(writer, fields, columns);
    }

    public async Task WriteRowGroupAsync(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using var rowGroup = _writer.CreateRowGroup();
        for (var i = 0; i < _fields.Length; i++)
        {
            var name = _columns[i].Name;
            var clr = ParquetFiles.MakeNullable(_columns[i].ClrType);
            var array = Array.CreateInstance(clr, rows.Count);
            for (var r = 0; r < rows.Count; r++)
            {
                var v = rows[r].TryGetValue(name, out var raw) ? raw : null;
                array.SetValue(v is null ? null : Convert.ChangeType(v, Nullable.GetUnderlyingType(clr) ?? clr, CultureInfo.InvariantCulture), r);
            }

            await rowGroup.WriteColumnAsync(new DataColumn(_fields[i], array), ct).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _writer.Dispose();
        return ValueTask.CompletedTask;
    }
}
