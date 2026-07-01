using System.Data.Common;
using System.Globalization;
using System.Text;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using SqlFlow.Core.Export;

namespace SqlFlow.SqlServer.Export;

/// <summary>Writes the rows of a reader to one export file (CSV or Parquet) and returns the row count.</summary>
public interface IExportFileWriter
{
    Task<long> WriteAsync(DbDataReader reader, Stream destination, CancellationToken ct = default);
}

/// <summary>Picks the writer for a flow's <c>trgFiletype</c>.</summary>
public static class ExportFileWriterFactory
{
    public static IExportFileWriter Create(ExportFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.TrgFiletype.Trim().ToLowerInvariant() switch
        {
            "parquet" or "prq" => new ParquetExportFileWriter(flow.CompressionType),
            _ => new CsvExportFileWriter(new CsvWriteOptions
            {
                Delimiter = flow.ColumnDelimiter,
                Qualifier = string.IsNullOrEmpty(flow.TextQualifier) ? '"' : flow.TextQualifier[0],
                Encoding = ResolveEncoding(flow.TrgEncoding),
            }),
        };
    }

    private static Encoding ResolveEncoding(string? name) => (name?.Trim().ToUpperInvariant()) switch
    {
        "UNICODE" or "UTF16" or "UTF-16" => Encoding.Unicode,
        "UTF32" or "UTF-32" => Encoding.UTF32,
        "ASCII" => Encoding.ASCII,
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };
}

/// <summary>Settings for the CSV writer.</summary>
public sealed record CsvWriteOptions
{
    public required string Delimiter { get; init; }

    public required char Qualifier { get; init; }

    public required Encoding Encoding { get; init; }

    public bool WriteHeader { get; init; } = true;

    /// <summary>Quote every string-typed column (legacy parity); non-string columns are quoted only when their
    /// value contains the delimiter, qualifier, or a line break.</summary>
    public bool QuoteStringColumnsOnly { get; init; } = true;
}

/// <summary>
/// Streams a reader to a delimited (CSV) file, honoring the configured delimiter, text qualifier, and encoding
/// (legacy hardcoded these and is corrected here). A field is quoted when it is a string column or contains the
/// delimiter, the qualifier, CR, or LF; an embedded qualifier is doubled. NULL writes empty. Values are
/// formatted with the invariant culture so a locale separator never collides with the delimiter.
/// </summary>
public sealed class CsvExportFileWriter : IExportFileWriter
{
    private const string RecordSeparator = "\r\n";

    private readonly CsvWriteOptions _options;

    public CsvExportFileWriter(CsvWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async Task<long> WriteAsync(DbDataReader reader, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(destination);

        var fieldCount = reader.FieldCount;
        var isStringColumn = new bool[fieldCount];
        var names = new string[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            names[i] = reader.GetName(i);
            isStringColumn[i] = reader.GetFieldType(i) == typeof(string);
        }

        await using var writer = new StreamWriter(destination, _options.Encoding, leaveOpen: true);
        if (_options.WriteHeader)
        {
            await writer.WriteAsync(string.Join(_options.Delimiter, names.Select(n => Escape(n, isStringColumn: false)))).ConfigureAwait(false);
            await writer.WriteAsync(RecordSeparator).ConfigureAwait(false);
        }

        long rows = 0;
        var fields = new string[fieldCount];
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            for (var i = 0; i < fieldCount; i++)
            {
                fields[i] = await reader.IsDBNullAsync(i, ct).ConfigureAwait(false)
                    ? string.Empty
                    : Escape(FormatValue(reader.GetValue(i)), isStringColumn[i]);
            }

            await writer.WriteAsync(string.Join(_options.Delimiter, fields)).ConfigureAwait(false);
            await writer.WriteAsync(RecordSeparator).ConfigureAwait(false);
            rows++;
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
        return rows;
    }

    private string Escape(string field, bool isStringColumn)
    {
        var mustQuote = (_options.QuoteStringColumnsOnly && isStringColumn)
            || field.Contains(_options.Delimiter, StringComparison.Ordinal)
            || field.Contains(_options.Qualifier)
            || field.Contains('\r')
            || field.Contains('\n');
        if (!mustQuote)
        {
            return field;
        }

        var q = _options.Qualifier;
        return q + field.Replace(q.ToString(), new string(q, 2), StringComparison.Ordinal) + q;
    }

    private static string FormatValue(object value) => value switch
    {
        string s => s,
        bool b => b ? "True" : "False",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        double db => db.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

/// <summary>
/// Streams a reader to an Apache Parquet file using Parquet.Net (the columnar inverse of the reader's type
/// mapping): each reader column becomes a typed, nullable Parquet field, rows are buffered into bounded row
/// groups, and each group is written column-by-column. Legacy never wrote Parquet; this is a V3 capability.
/// </summary>
public sealed class ParquetExportFileWriter : IExportFileWriter
{
    private const int RowGroupSize = 50_000;

    private readonly CompressionMethod _compression;

    public ParquetExportFileWriter(string? compressionType) => _compression = ResolveCompression(compressionType);

    public async Task<long> WriteAsync(DbDataReader reader, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(destination);

        var fieldCount = reader.FieldCount;
        var schemaTable = await reader.GetSchemaTableAsync(ct).ConfigureAwait(false);
        var columns = new ColumnWriter[fieldCount];
        var fields = new Field[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            columns[i] = PlanColumn(reader.GetName(i), reader.GetFieldType(i), DecimalInfo(schemaTable, i));
            fields[i] = columns[i].Field;
        }

        using var writer = await ParquetWriter.CreateAsync(new ParquetSchema(fields), destination, cancellationToken: ct).ConfigureAwait(false);
        writer.CompressionMethod = _compression;

        long total = 0;
        var buffer = new List<object?[]>(RowGroupSize);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new object?[fieldCount];
            for (var i = 0; i < fieldCount; i++)
            {
                row[i] = await reader.IsDBNullAsync(i, ct).ConfigureAwait(false) ? null : reader.GetValue(i);
            }

            buffer.Add(row);
            if (buffer.Count >= RowGroupSize)
            {
                await FlushAsync(writer, columns, buffer, ct).ConfigureAwait(false);
                total += buffer.Count;
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            await FlushAsync(writer, columns, buffer, ct).ConfigureAwait(false);
            total += buffer.Count;
        }

        return total;
    }

    private static async Task FlushAsync(ParquetWriter writer, ColumnWriter[] columns, List<object?[]> buffer, CancellationToken ct)
    {
        using var rowGroup = writer.CreateRowGroup();
        for (var c = 0; c < columns.Length; c++)
        {
            var values = new object?[buffer.Count];
            for (var r = 0; r < buffer.Count; r++)
            {
                values[r] = buffer[r][c];
            }

            await rowGroup.WriteColumnAsync(new DataColumn(columns[c].Field, columns[c].Build(values)), ct).ConfigureAwait(false);
        }
    }

    private static ColumnWriter PlanColumn(string name, Type clr, (int Precision, int Scale)? dec)
    {
        if (clr == typeof(bool)) return NullableValue<bool>(name, v => (bool)v);
        if (clr == typeof(byte)) return NullableValue<int>(name, v => (byte)v);
        if (clr == typeof(short)) return NullableValue<int>(name, v => (short)v);
        if (clr == typeof(int)) return NullableValue<int>(name, v => (int)v);
        if (clr == typeof(long)) return NullableValue<long>(name, v => (long)v);
        if (clr == typeof(float)) return NullableValue<float>(name, v => (float)v);
        if (clr == typeof(double)) return NullableValue<double>(name, v => (double)v);
        if (clr == typeof(decimal)) return DecimalColumn(name, dec?.Precision ?? 38, dec?.Scale ?? 18);
        if (clr == typeof(DateTime)) return NullableValue<DateTime>(name, v => (DateTime)v);
        if (clr == typeof(DateTimeOffset)) return NullableValue<DateTimeOffset>(name, v => (DateTimeOffset)v);
        if (clr == typeof(TimeSpan)) return NullableValue<TimeSpan>(name, v => (TimeSpan)v);
        if (clr == typeof(Guid)) return NullableValue<Guid>(name, v => (Guid)v);
        if (clr == typeof(byte[])) return BinaryColumn(name);
        return StringColumn(name);
    }

    private static ColumnWriter NullableValue<T>(string name, Func<object, T> convert) where T : struct
        => new(new DataField<T?>(name), values =>
        {
            var array = new T?[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                array[i] = values[i] is null ? null : convert(values[i]!);
            }

            return array;
        });

    private static ColumnWriter DecimalColumn(string name, int precision, int scale)
        => new(new DecimalDataField(name, precision, scale, isNullable: true), values =>
        {
            var array = new decimal?[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                array[i] = values[i] is null ? null : Convert.ToDecimal(values[i], CultureInfo.InvariantCulture);
            }

            return array;
        });

    private static ColumnWriter StringColumn(string name)
        => new(new DataField<string>(name), values =>
        {
            var array = new string?[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                array[i] = values[i] is null ? null : (values[i] as string ?? Convert.ToString(values[i], CultureInfo.InvariantCulture));
            }

            return array;
        });

    private static ColumnWriter BinaryColumn(string name)
        => new(new DataField<byte[]>(name), values =>
        {
            var array = new byte[values.Length][];
            for (var i = 0; i < values.Length; i++)
            {
                array[i] = values[i] as byte[] ?? [];
            }

            return array;
        });

    private static (int Precision, int Scale)? DecimalInfo(System.Data.DataTable? schema, int ordinal)
    {
        if (schema is null || ordinal >= schema.Rows.Count)
        {
            return null;
        }

        var row = schema.Rows[ordinal];
        if (row["NumericPrecision"] is DBNull || row["NumericScale"] is DBNull)
        {
            return null;
        }

        var precision = Math.Clamp(Convert.ToInt32(row["NumericPrecision"], CultureInfo.InvariantCulture), 1, 38);
        var scale = Math.Clamp(Convert.ToInt32(row["NumericScale"], CultureInfo.InvariantCulture), 0, precision);
        return (precision, scale);
    }

    private static CompressionMethod ResolveCompression(string? type) => (type?.Trim().ToLowerInvariant()) switch
    {
        "snappy" => CompressionMethod.Snappy,
        "none" or "" or null => CompressionMethod.None,
        _ => CompressionMethod.Gzip,
    };

    private sealed record ColumnWriter(DataField Field, Func<object?[], Array> Build);
}
