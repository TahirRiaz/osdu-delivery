using System.Runtime.CompilerServices;
using SqlFlow.Delivery.Rendering;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace SqlFlow.Delivery.Storage;

/// <summary>
/// Reads the source-shaped metadata rows of one scope file with Parquet.Net, one row group at a time with column
/// pruning, so peak memory is one row group's selected columns (design.md section 13.1). Only top-level scalar
/// columns are read; nested columns are ignored because the document model expresses collections as child scopes.
/// </summary>
public static class ParquetScopeReader
{
    /// <summary>
    /// Parquet needs a seekable stream (the footer is at the end). A forward-only stream is spilled to a temporary
    /// file that is deleted when the returned stream closes: disk, never memory, so a scope file of any size reads
    /// in bounded memory. Stores that can seek natively (local files, blobs through the range reader) never come here.
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

    /// <summary>The top-level scalar column names of a file.</summary>
    public static async Task<IReadOnlyList<string>> ReadColumnsAsync(Stream seekable, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(seekable);
        using var reader = await ParquetReader.CreateAsync(seekable, leaveStreamOpen: true, cancellationToken: ct).ConfigureAwait(false);
        return reader.Schema.Fields.OfType<DataField>().Select(f => f.Name).ToList();
    }

    public static async IAsyncEnumerable<SourceRow> ReadRowsAsync(
        Stream seekable,
        IReadOnlySet<string>? columns,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(seekable);
        using var reader = await ParquetReader.CreateAsync(seekable, leaveStreamOpen: true, cancellationToken: ct).ConfigureAwait(false);
        var fields = reader.Schema.Fields
            .OfType<DataField>()
            .Where(f => columns is null || columns.Contains(f.Name))
            .ToArray();

        for (var group = 0; group < reader.RowGroupCount; group++)
        {
            ct.ThrowIfCancellationRequested();
            using var rowGroup = reader.OpenRowGroupReader(group);
            var data = new Array[fields.Length];
            for (var i = 0; i < fields.Length; i++)
            {
                var column = await rowGroup.ReadColumnAsync(fields[i], ct).ConfigureAwait(false);
                data[i] = column.Data;
            }

            var rows = checked((int)rowGroup.RowCount);
            for (var r = 0; r < rows; r++)
            {
                var values = new Dictionary<string, object?>(fields.Length, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < fields.Length; i++)
                {
                    values[fields[i].Name] = r < data[i].Length ? Normalize(data[i].GetValue(r)) : null;
                }

                yield return new SourceRow(values);
            }
        }
    }

    /// <summary>Collapses the parquet CLR types to the small set the renderer understands.</summary>
    public static object? Normalize(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b,
        byte or sbyte or short or ushort or int or uint or long => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
        ulong ul => ul <= long.MaxValue ? (long)ul : (double)ul,
        float f => (double)f,
        double d => d,
        decimal m => (double)m,
        DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime()),
        DateTimeOffset dto => dto.ToUniversalTime(),
        DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
        Guid g => g,
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => value.ToString(),
    };

    /// <summary>Writes rows as one row group, for fixtures, tests and the sample drop generator.</summary>
    public static async Task WriteAsync(Stream target, IReadOnlyList<(string Name, Type ClrType)> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        var fields = columns.Select(c => new DataField(c.Name, MakeNullable(c.ClrType))).ToArray();
        var schema = new ParquetSchema(fields.Cast<Field>().ToArray());
        using var writer = await ParquetWriter.CreateAsync(schema, target, cancellationToken: ct).ConfigureAwait(false);
        writer.CompressionMethod = CompressionMethod.Snappy;
        using var rowGroup = writer.CreateRowGroup();
        for (var i = 0; i < fields.Length; i++)
        {
            var name = columns[i].Name;
            var clr = MakeNullable(columns[i].ClrType);
            var array = Array.CreateInstance(clr, rows.Count);
            for (var r = 0; r < rows.Count; r++)
            {
                var v = rows[r].TryGetValue(name, out var raw) ? raw : null;
                array.SetValue(v is null ? null : Convert.ChangeType(v, Nullable.GetUnderlyingType(clr) ?? clr, System.Globalization.CultureInfo.InvariantCulture), r);
            }

            await rowGroup.WriteColumnAsync(new DataColumn(fields[i], array), ct).ConfigureAwait(false);
        }
    }

    internal static Type MakeNullable(Type type)
        => type.IsValueType && Nullable.GetUnderlyingType(type) is null ? typeof(Nullable<>).MakeGenericType(type) : type;
}

/// <summary>Writes a parquet file one row group at a time, so a publication of any size never sits in memory whole.</summary>
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
        var fields = columns.Select(c => new DataField(c.Name, ParquetScopeReader.MakeNullable(c.ClrType))).ToArray();
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
            var clr = ParquetScopeReader.MakeNullable(_columns[i].ClrType);
            var array = Array.CreateInstance(clr, rows.Count);
            for (var r = 0; r < rows.Count; r++)
            {
                var v = rows[r].TryGetValue(name, out var raw) ? raw : null;
                array.SetValue(v is null ? null : Convert.ChangeType(v, Nullable.GetUnderlyingType(clr) ?? clr, System.Globalization.CultureInfo.InvariantCulture), r);
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
