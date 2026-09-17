using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Parquet;
using Parquet.Schema;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Protocols.Ddms;

/// <summary>The value kind a historian series stores, fixed by its <c>ParameterKindID</c> (osdu/specs/production-timeseries/INTEGRATION.md section 2).</summary>
internal enum SeriesKind
{
    Double,
    Integer,
    Boolean,
    String,

    /// <summary>ISO-8601 date-times, whose points the ingestion service's checks appear to refuse (section 3.4); not delivered.</summary>
    Timestamp,

    /// <summary>Arrays of unique strings (section 3.4), taken from JSON points files only.</summary>
    SetString,

    /// <summary>A kind the ingestion service answers 400 for ("Unknown data type").</summary>
    Unknown,
}

/// <summary>One series a ProductionValues record defines: its id, the <c>ParameterKindID</c> it names, and the kind that fixes.</summary>
internal sealed record SeriesDefinition(string Id, string ParameterKind, SeriesKind Kind)
{
    private const string KindMarker = "reference-data--ParameterKind:";

    /// <summary>
    /// The kind a <c>ParameterKindID</c> names: the code after <c>reference-data--ParameterKind:</c> (Double, Integer,
    /// Boolean, String, Timestamp), or SET-STRING wherever the text says <c>set-string</c> or <c>setstring</c>, as the
    /// ingestion service reads it (section 2).
    /// </summary>
    public static SeriesKind KindOf(string parameterKindId)
    {
        ArgumentNullException.ThrowIfNull(parameterKindId);
        if (parameterKindId.Contains("set-string", StringComparison.OrdinalIgnoreCase) || parameterKindId.Contains("setstring", StringComparison.OrdinalIgnoreCase))
        {
            return SeriesKind.SetString;
        }

        var at = parameterKindId.IndexOf(KindMarker, StringComparison.OrdinalIgnoreCase);
        var code = at < 0 ? parameterKindId : parameterKindId[(at + KindMarker.Length)..];
        var colon = code.IndexOf(':', StringComparison.Ordinal);
        code = (colon < 0 ? code : code[..colon]).Trim();
        return code.ToUpperInvariant() switch
        {
            "DOUBLE" => SeriesKind.Double,
            "INTEGER" => SeriesKind.Integer,
            "BOOLEAN" => SeriesKind.Boolean,
            "STRING" => SeriesKind.String,
            "TIMESTAMP" => SeriesKind.Timestamp,
            _ => SeriesKind.Unknown,
        };
    }
}

/// <summary>How one point's value is written: an integer, a real, an exact decimal, a boolean, a text, or JSON it was read as.</summary>
internal readonly struct PointValue
{
    private readonly Form _form;
    private readonly long _integer;
    private readonly double _real;
    private readonly decimal _exact;
    private readonly string? _text;
    private readonly byte[]? _raw;

    private PointValue(Form form, long integer = 0, double real = 0, decimal exact = 0, string? text = null, byte[]? raw = null)
    {
        _form = form;
        _integer = integer;
        _real = real;
        _exact = exact;
        _text = text;
        _raw = raw;
    }

    private enum Form
    {
        Integer,
        Real,
        Exact,
        Boolean,
        Text,
        Raw,
    }

    public static PointValue Of(long value) => new(Form.Integer, integer: value);

    public static PointValue Of(double value) => new(Form.Real, real: value);

    public static PointValue Of(decimal value) => new(Form.Exact, exact: value);

    public static PointValue Of(bool value) => new(Form.Boolean, integer: value ? 1 : 0);

    public static PointValue Of(string value) => new(Form.Text, text: value);

    /// <summary>A value written exactly as it was read: a JSON number's own digits, or a set of strings.</summary>
    public static PointValue Json(byte[] value) => new(Form.Raw, raw: value);

    public void WriteTo(Utf8JsonWriter writer)
    {
        switch (_form)
        {
            case Form.Integer:
                writer.WriteNumberValue(_integer);
                break;
            case Form.Real:
                writer.WriteNumberValue(_real);
                break;
            case Form.Exact:
                writer.WriteNumberValue(_exact);
                break;
            case Form.Boolean:
                writer.WriteBooleanValue(_integer != 0);
                break;
            case Form.Text:
                writer.WriteStringValue(_text);
                break;
            default:
                writer.WriteRawValue(_raw!, skipInputValidation: true);
                break;
        }
    }
}

/// <summary>How a points file holds its points.</summary>
internal enum PointFileForm
{
    /// <summary>A wide table: a <c>timestamp</c> column and one column per series, named by its <c>DDMSDatasetID</c>.</summary>
    Parquet,

    /// <summary>The ingestion service's own body: <c>{"timeseries":[{"timeseriesId","points":[{"timestamp","value"}]}]}</c>.</summary>
    Json,
}

/// <summary>One points file of a record's payload, with its form.</summary>
internal sealed record PointFile(PayloadFile File, PointFileForm Form, string Name);

/// <summary>
/// Reading a ProductionValues record's points from its payload files (osdu/specs/production-timeseries/INTEGRATION.md
/// sections 2 to 4). A parquet file is a wide table: a <c>timestamp</c> column in epoch milliseconds (an integer, a
/// timestamp, or a date, read as midnight UTC) and one column per series, named by its <c>DDMSDatasetID</c>, whose empty
/// cells are no point; a pandas index column is left out unless it is the timestamp. A JSON file is the ingestion service's
/// own body. Every point is checked as the ingestion service checks it, and as the historian stores it: a timestamp in
/// milliseconds, a value of the series' kind, one value per timestamp in increasing order, each series in one file. A
/// file that breaks a rule holds the record with the reason, before anything is sent.
/// </summary>
internal static class TimeSeriesPoints
{
    /// <summary>The name of the column, and of the point property, holding a point's epoch-millisecond timestamp.</summary>
    public const string TimestampName = "timestamp";

    /// <summary>The largest JSON points file read: it is parsed whole, so a larger set of points goes in a parquet file.</summary>
    public const long MaxJsonBytes = 64L * 1024 * 1024;

    private const string ValueName = "value";
    private const string SeriesName = "timeseriesId";
    private const string PointsName = "points";
    private const string SeriesListName = "timeseries";

    /// <summary>-2^63, the smallest whole number a 64-bit integer holds, as a real.</summary>
    private const double Int64Floor = -9223372036854775808d;

    /// <summary>2^63, the first whole number past what a 64-bit integer holds, as a real.</summary>
    private const double Int64Ceiling = 9223372036854775808d;

    /// <summary>The form a payload file holds its points in, from its extension; null for any other file.</summary>
    public static PointFileForm? FormOf(string fileName)
        => fileName.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase) ? PointFileForm.Parquet
            : fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? PointFileForm.Json
            : null;

    /// <summary>
    /// Reads every point of <paramref name="files"/> in order (file by file; in a parquet file row group by row group and
    /// series by series; in a JSON file series by series) and hands each to <paramref name="each"/>.
    /// </summary>
    public static async Task ScanAsync(
        IPayloadSource payload, IReadOnlyList<PointFile> files, IReadOnlyDictionary<string, SeriesDefinition> series,
        Func<string, long, PointValue, ValueTask> each, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(each);
        var order = new SeriesOrder();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Form == PointFileForm.Parquet)
            {
                await ScanParquetAsync(payload, file, series, order, each, ct).ConfigureAwait(false);
            }
            else
            {
                await ScanJsonAsync(payload, file, series, order, each, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task ScanParquetAsync(
        IPayloadSource payload, PointFile file, IReadOnlyDictionary<string, SeriesDefinition> series, SeriesOrder order,
        Func<string, long, PointValue, ValueTask> each, CancellationToken ct)
    {
        var opened = await payload.OpenAsync(file.File, ct).ConfigureAwait(false);
        Stream seekable;
        try
        {
            seekable = await ParquetFiles.EnsureSeekableAsync(opened, ct).ConfigureAwait(false);
        }
        catch
        {
            await opened.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await using (seekable.ConfigureAwait(false))
        {
            if (await FrameProblemAsync(seekable, ct).ConfigureAwait(false) is { } frame)
            {
                throw new RecordHeldException($"the points file {file.Name} is declared as parquet but is not a parquet file: {frame}");
            }

            using var reader = await FromParquet(file, () => ParquetReader.CreateAsync(seekable, leaveStreamOpen: true, cancellationToken: ct)).ConfigureAwait(false);
            if (reader.Schema.Fields.FirstOrDefault(f => f is not DataField) is { } nested)
            {
                throw new RecordHeldException(
                    $"the points file {file.Name} has the column {nested.Name}, which is not a column of single values; a parquet points file holds a {TimestampName} column and one column "
                    + "of values per series, and a SET-STRING series is sent from a JSON points file");
            }

            var fields = reader.Schema.Fields.OfType<DataField>().ToList();
            var timestamp = fields.Find(f => string.Equals(f.Name, TimestampName, StringComparison.Ordinal))
                ?? throw new RecordHeldException(
                    $"the points file {file.Name} has no {TimestampName} column; a parquet points file holds the epoch-millisecond timestamp of each row in a column named {TimestampName}");
            var index = ParquetFiles.PandasIndexColumn(reader.CustomMetadata);
            var columns = new List<(DataField Field, SeriesDefinition Series)>();
            foreach (var field in fields)
            {
                if (ReferenceEquals(field, timestamp) || string.Equals(field.Name, index, StringComparison.Ordinal))
                {
                    continue;
                }

                var definition = Series(series, field.Name, file);
                if (definition.Kind == SeriesKind.SetString)
                {
                    throw new RecordHeldException(
                        $"{definition.Id} is a SET-STRING series ({definition.ParameterKind}), whose values are lists of strings; send its points from a JSON points file");
                }

                order.Claim(definition.Id, file.Name);
                columns.Add((field, definition));
            }

            if (columns.Count == 0)
            {
                throw new RecordHeldException($"the points file {file.Name} has no column of values; name each column after the DDMSDatasetID of the series it holds");
            }

            long rows = 0;
            for (var group = 0; group < reader.RowGroupCount; group++)
            {
                ct.ThrowIfCancellationRequested();
                using var rowGroup = await FromParquet(file, () => Task.FromResult(reader.OpenRowGroupReader(group))).ConfigureAwait(false);
                if (rowGroup.RowCount == 0)
                {
                    continue;
                }

                var stamps = Timestamps((await FromParquet(file, () => rowGroup.ReadColumnAsync(timestamp, ct)).ConfigureAwait(false)).Data, file, rows);
                foreach (var (field, definition) in columns)
                {
                    var values = (await FromParquet(file, () => rowGroup.ReadColumnAsync(field, ct)).ConfigureAwait(false)).Data;
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (Value(values.GetValue(i), definition, file, rows + i) is not { } value)
                        {
                            continue;
                        }

                        order.Next(definition.Id, stamps[i], file, rows + i, row: true);
                        await each(definition.Id, stamps[i], value).ConfigureAwait(false);
                    }
                }

                rows += rowGroup.RowCount;
            }
        }
    }

    private static async Task ScanJsonAsync(
        IPayloadSource payload, PointFile file, IReadOnlyDictionary<string, SeriesDefinition> series, SeriesOrder order,
        Func<string, long, PointValue, ValueTask> each, CancellationToken ct)
    {
        if (file.File.Size > MaxJsonBytes)
        {
            throw new RecordHeldException(TooLarge(file, file.File.Size));
        }

        byte[] bytes;
        var stream = await payload.OpenAsync(file.File, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[1 << 16];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxJsonBytes)
                {
                    throw new RecordHeldException(TooLarge(file, buffer.Length + read));
                }

                buffer.Write(chunk, 0, read);
            }

            bytes = buffer.ToArray();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new RecordHeldException($"the points file {file.Name} is not JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(SeriesListName, out var list) || list.ValueKind != JsonValueKind.Array
                || root.EnumerateObject().Any(p => p.Name != SeriesListName))
            {
                throw new RecordHeldException(
                    $"the points file {file.Name} is not in the form the ingestion service takes for one record: an object holding a {SeriesListName} list alone, "
                    + "{\"timeseries\":[{\"timeseriesId\":\"...\",\"points\":[{\"timestamp\":978307200000,\"value\":1.5}]}]}");
            }

            var item = 0;
            foreach (var entry in list.EnumerateArray())
            {
                item++;
                var at = string.Create(CultureInfo.InvariantCulture, $"entry {item} of {file.Name}");
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new RecordHeldException($"{at} is not an object with {SeriesName} and {PointsName}");
                }

                foreach (var property in entry.EnumerateObject())
                {
                    if (property.Name == "unit")
                    {
                        throw new RecordHeldException(
                            $"{at} names a unit, which the ingestion service ignores: the historian stores values as they are sent, in the UnitOfMeasureID of the series. Send the values in that unit and leave unit out");
                    }

                    if (property.Name is not (SeriesName or PointsName))
                    {
                        throw new RecordHeldException($"{at} has the property {property.Name}; an entry holds {SeriesName} and {PointsName} alone");
                    }
                }

                if (!entry.TryGetProperty(SeriesName, out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                {
                    throw new RecordHeldException($"{at} names no {SeriesName}");
                }

                var definition = Series(series, id.GetString()!, file);
                if (!entry.TryGetProperty(PointsName, out var points) || points.ValueKind != JsonValueKind.Array)
                {
                    throw new RecordHeldException($"{at} ({definition.Id}) has no {PointsName} list");
                }

                order.Claim(definition.Id, file.Name);
                var index = 0L;
                foreach (var point in points.EnumerateArray())
                {
                    var (stamp, value) = Point(point, definition, file, index);
                    order.Next(definition.Id, stamp, file, index, row: false);
                    await each(definition.Id, stamp, value).ConfigureAwait(false);
                    index++;
                }
            }
        }
    }

    /// <summary>One point of a JSON points file: its timestamp and its value, checked against the series' kind.</summary>
    private static (long Timestamp, PointValue Value) Point(JsonElement point, SeriesDefinition series, PointFile file, long index)
    {
        var at = string.Create(CultureInfo.InvariantCulture, $"point {index + 1} of {series.Id} in {file.Name}");
        if (point.ValueKind != JsonValueKind.Object)
        {
            throw new RecordHeldException($"{at} is not an object with {TimestampName} and {ValueName}");
        }

        foreach (var property in point.EnumerateObject())
        {
            if (property.Name is not (TimestampName or ValueName))
            {
                throw new RecordHeldException(property.Name == "point"
                    ? $"{at} names its time point, which the ingestion service reads from {TimestampName} (its contract's Point schema calls it point, its code does not)"
                    : $"{at} has the property {property.Name}; a point holds {TimestampName} and {ValueName} alone");
            }
        }

        if (!point.TryGetProperty(TimestampName, out var stamp) || stamp.ValueKind != JsonValueKind.Number || !stamp.TryGetInt64(out var timestamp))
        {
            throw new RecordHeldException($"{at} has no {TimestampName} in whole epoch milliseconds");
        }

        if (!point.TryGetProperty(ValueName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new RecordHeldException($"{at} has no {ValueName}; leave out a point that has none");
        }

        PointValue? converted = series.Kind switch
        {
            SeriesKind.Double when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var real) && double.IsFinite(real)
                => PointValue.Json(JsonMarshal.GetRawUtf8Value(value).ToArray()),
            SeriesKind.Integer when value.ValueKind == JsonValueKind.Number => Whole(value),
            SeriesKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False => PointValue.Of(value.GetBoolean()),
            SeriesKind.String when value.ValueKind == JsonValueKind.String => PointValue.Of(value.GetString()!),
            SeriesKind.SetString when value.ValueKind == JsonValueKind.Array => StringSet(value),
            _ => null,
        };
        return converted is { } ok
            ? (timestamp, ok)
            : throw new RecordHeldException($"{at} has the value {Preview(value.GetRawText())}, which is not {Expected(series)}");
    }

    private static PointValue? Whole(JsonElement value)
    {
        if (value.TryGetInt64(out var whole))
        {
            return PointValue.Of(whole);
        }

        return value.TryGetDecimal(out var exact) && decimal.IsInteger(exact) && exact >= long.MinValue && exact <= long.MaxValue
            ? PointValue.Of((long)exact)
            : null;
    }

    private static PointValue? StringSet(JsonElement value)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !seen.Add(item.GetString()!))
            {
                return null;
            }
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, RequestWriter.Options))
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                writer.WriteStringValue(item.GetString());
            }

            writer.WriteEndArray();
        }

        return PointValue.Json(buffer.WrittenSpan.ToArray());
    }

    /// <summary>The timestamps of one row group, in epoch milliseconds.</summary>
    private static long[] Timestamps(Array data, PointFile file, long firstRow)
    {
        var stamps = new long[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            var cell = data.GetValue(i);
            var row = firstRow + i + 1;
            stamps[i] = cell switch
            {
                null => throw new RecordHeldException(string.Create(CultureInfo.InvariantCulture, $"row {row} of {file.Name} has no {TimestampName}")),
                long l => l,
                int n => n,
                short s => s,
                sbyte s => s,
                byte b => b,
                ushort u => u,
                uint u => u,
                ulong u when u <= long.MaxValue => (long)u,
                DateTime dt => Millis(dt.Kind switch
                {
                    DateTimeKind.Local => new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero),
                    _ => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
                }, file, row),
                DateTimeOffset dto => Millis(dto, file, row),
                DateOnly d => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero).ToUnixTimeMilliseconds(),
                _ => throw new RecordHeldException(
                    string.Create(CultureInfo.InvariantCulture, $"row {row} of {file.Name} has the {TimestampName} {Preview(Convert.ToString(cell, CultureInfo.InvariantCulture) ?? string.Empty)} ({cell.GetType().Name}); ")
                    + "the column holds epoch milliseconds as whole numbers, timestamps or dates"),
            };
        }

        return stamps;
    }

    private static long Millis(DateTimeOffset value, PointFile file, long row)
        => value.UtcTicks % TimeSpan.TicksPerMillisecond == 0
            ? value.ToUnixTimeMilliseconds()
            : throw new RecordHeldException(string.Create(
                CultureInfo.InvariantCulture,
                $"row {row} of {file.Name} has the {TimestampName} {value.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'}, finer than the milliseconds the historian keeps; round it to the millisecond"));

    /// <summary>A cell of a parquet series column as the series' kind writes it, or null for an empty cell.</summary>
    private static PointValue? Value(object? cell, SeriesDefinition series, PointFile file, long rowIndex)
    {
        switch (cell)
        {
            case null:
                return null;
            case double d when double.IsNaN(d):
                return null;
            case float f when float.IsNaN(f):
                return null;
        }

        PointValue? value = series.Kind switch
        {
            SeriesKind.Double => cell switch
            {
                double d when double.IsFinite(d) => PointValue.Of(d),
                float f when float.IsFinite(f) => PointValue.Of(NumberValues.Widen(f)),
                decimal m => PointValue.Of(m),
                ulong u when u > long.MaxValue => PointValue.Of((decimal)u),
                long or int or short or sbyte or byte or ushort or uint or ulong => PointValue.Of(Convert.ToInt64(cell, CultureInfo.InvariantCulture)),
                _ => null,
            },
            SeriesKind.Integer => cell switch
            {
                ulong u when u > long.MaxValue => null,
                long or int or short or sbyte or byte or ushort or uint or ulong => PointValue.Of(Convert.ToInt64(cell, CultureInfo.InvariantCulture)),
                double d when double.IsInteger(d) && d >= Int64Floor && d < Int64Ceiling => PointValue.Of((long)d),
                float f when float.IsInteger(f) && f >= Int64Floor && f < Int64Ceiling => PointValue.Of((long)f),
                decimal m when decimal.IsInteger(m) && m >= long.MinValue && m <= long.MaxValue => PointValue.Of((long)m),
                _ => null,
            },
            SeriesKind.Boolean => cell is bool b ? PointValue.Of(b) : null,
            SeriesKind.String => cell is string s ? PointValue.Of(s) : null,
            _ => null,
        };
        return value ?? throw new RecordHeldException(string.Create(
            CultureInfo.InvariantCulture,
            $"row {rowIndex + 1} of {file.Name} holds {Preview(Convert.ToString(cell, CultureInfo.InvariantCulture) ?? string.Empty)} ({cell.GetType().Name}) for {series.Id}, which is not {Expected(series)}"));
    }

    private static SeriesDefinition Series(IReadOnlyDictionary<string, SeriesDefinition> series, string id, PointFile file)
    {
        if (!series.TryGetValue(id, out var definition))
        {
            throw new RecordHeldException(
                $"the points file {file.Name} holds points of {id}, which the record does not define; the ingestion service takes the series a record lists in "
                + $"data.ProductionMetricValues[].DDMSDatasetID ({DdmsShapeValues.Bounded(series.Keys.Order(StringComparer.Ordinal).ToList())})");
        }

        return definition.Kind switch
        {
            SeriesKind.Timestamp => throw new RecordHeldException(
                $"{id} is a date-time series ({definition.ParameterKind}); the ingestion service's value check has no date-time kind and refuses every such point "
                + "(osdu/specs/production-timeseries/INTEGRATION.md section 3.4), so its points are not sent until a deployment is confirmed to take them"),
            SeriesKind.Unknown => throw new RecordHeldException(
                $"{id} names the kind {definition.ParameterKind}, which the ingestion service does not know (Double, Integer, Boolean, String, Timestamp, or a SET-STRING kind); it refuses every point of such a series"),
            _ => definition,
        };
    }

    private static string Expected(SeriesDefinition series) => series.Kind switch
    {
        SeriesKind.Double => $"a finite number, as the Double series {series.Id} takes",
        SeriesKind.Integer => $"a whole number of 64 bits, as the Integer series {series.Id} takes",
        SeriesKind.Boolean => $"true or false, as the Boolean series {series.Id} takes",
        SeriesKind.String => $"text, as the String series {series.Id} takes",
        _ => $"a list of distinct strings, as the SET-STRING series {series.Id} takes",
    };

    private static string TooLarge(PointFile file, long size)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"the JSON points file {file.Name} is {size} bytes or more, above the {MaxJsonBytes} bytes a JSON points file is read up to; send that many points in a parquet file");

    private static string Preview(string text) => text.Length <= 60 ? text : text[..60] + "...";

    /// <summary>
    /// Why <paramref name="seekable"/> is not framed as a parquet file, or null: it starts and ends with the parquet marker
    /// (<c>PAR1</c>), and the footer length before the last marker fits the file. The reader would say so less plainly.
    /// </summary>
    private static async Task<string?> FrameProblemAsync(Stream seekable, CancellationToken ct)
    {
        const int Marker = 4;
        if (seekable.Length < 3 * Marker)
        {
            return string.Create(CultureInfo.InvariantCulture, $"it is {seekable.Length} bytes long, shorter than the smallest parquet file");
        }

        var head = new byte[Marker];
        var end = new byte[2 * Marker];
        seekable.Position = 0;
        await seekable.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        seekable.Position = seekable.Length - end.Length;
        await seekable.ReadExactlyAsync(end, ct).ConfigureAwait(false);
        seekable.Position = 0;
        if (!"PAR1"u8.SequenceEqual(head) || !"PAR1"u8.SequenceEqual(end.AsSpan(Marker)))
        {
            return "it does not start and end with the parquet marker";
        }

        var footer = BitConverter.ToInt32(BitConverter.IsLittleEndian ? end : [end[3], end[2], end[1], end[0]], 0);
        return footer <= 0 || footer > seekable.Length - (3 * Marker)
            ? string.Create(CultureInfo.InvariantCulture, $"its footer is said to be {footer} bytes long, which the file cannot hold")
            : null;
    }

    /// <summary>
    /// A call into the parquet reader over a file the payload store holds: whatever it fails with holds the record, as the
    /// Wellbore DDMS shape's footer read does (<see cref="ParquetPayloads"/>), since the reader reports a malformed file
    /// with the same <see cref="IOException"/> as a failed read, and a payload file does not change.
    /// </summary>
    private static async Task<T> FromParquet<T>(PointFile file, Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or SqlFlowException))
        {
            throw new RecordHeldException(
                $"the points file {file.Name} is declared as parquet but could not be read: {HeaderRedaction.RedactMessage(ex.Message)}", ex);
        }
    }

    /// <summary>Where each series' points come from, and the last timestamp each has had.</summary>
    private sealed class SeriesOrder
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _last = new(StringComparer.Ordinal);

        public void Claim(string series, string file)
        {
            if (!_files.TryAdd(series, file))
            {
                throw new RecordHeldException(_files[series] == file
                    ? $"the points file {file} lists {series} more than once; list each series once, with all its points"
                    : $"{series} has points in both {_files[series]} and {file}; keep the points of a series in one file");
            }
        }

        /// <summary>Checks the next point of <paramref name="series"/>, the <paramref name="index"/>th row or point of its file counting from 0.</summary>
        public void Next(string series, long timestamp, PointFile file, long index, bool row)
        {
            var at = row
                ? string.Create(CultureInfo.InvariantCulture, $"the point of {series} in row {index + 1} of {file.Name}")
                : string.Create(CultureInfo.InvariantCulture, $"point {index + 1} of {series} in {file.Name}");
            if (timestamp == long.MaxValue)
            {
                throw new RecordHeldException($"{at} is at the largest timestamp there is, which the query service's exclusive end cannot reach, so it could never be read back");
            }

            if (_last.TryGetValue(series, out var previous) && timestamp <= previous)
            {
                var where = timestamp == previous
                    ? "the timestamp of the point before it"
                    : string.Create(CultureInfo.InvariantCulture, $"before the point ahead of it ({previous})");
                throw new RecordHeldException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{at} is at {timestamp}, {where}; a points file holds each series in increasing timestamp order, one value per timestamp"));
            }

            _last[series] = timestamp;
        }
    }
}

/// <summary>A range of points of one series in one request, read back as one query.</summary>
/// <param name="Start">The first point's timestamp.</param>
/// <param name="End">The last point's timestamp.</param>
/// <param name="Points">How many points it holds.</param>
internal readonly record struct SettleWindow(long Start, long End, int Points);

/// <summary>One series of a request: how many points it carries, their first and last timestamps, and the ranges its read back checks.</summary>
internal sealed record RequestSeries(string Id, int Points, long Start, long End, IReadOnlyList<SettleWindow> Windows);

/// <summary>One request to the ingestion service: its number in the record's plan, its body, the body's hash, and its series in body order.</summary>
internal sealed record TimeSeriesRequest(int Number, byte[] Body, string Hash, IReadOnlyList<RequestSeries> Series)
{
    public long Points => Series.Sum(s => (long)s.Points);
}

/// <summary>The writer settings every request body and value is written with.</summary>
internal static class RequestWriter
{
    public static readonly JsonWriterOptions Options = new()
    {
        Indented = false,
        SkipValidation = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// Splits a record's points into requests to the ingestion service (<c>POST /production-values/{id}/timeseries</c>,
/// osdu/specs/production-timeseries/INTEGRATION.md section 3.2), each body at most the flow's limit, in the order the
/// points come. A request lists each of its series once. The split depends only on the points and the limit, so a later
/// try splits them the same way and knows each request by its hash. Each series of a request keeps the ranges its read
/// back checks, each of at most <see cref="SettleWindowPoints"/> points, well within the pages one query of the
/// historian reads.
/// </summary>
internal sealed class TimeSeriesRequestBuilder : IDisposable
{
    /// <summary>The most points one read back asks for.</summary>
    public const int SettleWindowPoints = 10_000;

    private static readonly byte[] EnvelopeStart = "{\"timeseries\":["u8.ToArray();
    private static readonly byte[] ItemIdStart = "{\"timeseriesId\":"u8.ToArray();
    private static readonly byte[] ItemPointsStart = ",\"points\":["u8.ToArray();
    private static readonly byte[] Close = "]}"u8.ToArray();
    private static readonly JsonEncodedText Timestamp = JsonEncodedText.Encode(TimeSeriesPoints.TimestampName);
    private static readonly JsonEncodedText Value = JsonEncodedText.Encode("value");

    private readonly long _limit;
    private readonly ArrayBufferWriter<byte> _scratch = new(256);
    private readonly Utf8JsonWriter _writer;
    private readonly Dictionary<string, byte[]> _encodedIds = new(StringComparer.Ordinal);
    private readonly List<SeriesBuffer> _order = [];
    private readonly Dictionary<string, SeriesBuffer> _current = new(StringComparer.Ordinal);
    private long _size = EnvelopeLength;
    private int _number;

    public TimeSeriesRequestBuilder(long limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, EnvelopeLength + 64);
        _limit = limit;
        _writer = new Utf8JsonWriter(_scratch, RequestWriter.Options);
    }

    private static long EnvelopeLength => EnvelopeStart.Length + Close.Length;

    /// <summary>The requests finished so far.</summary>
    public int Requests => _number;

    /// <summary>Adds a point; returns the request it finished when the point does not fit beside the ones before it.</summary>
    public TimeSeriesRequest? Add(string series, long timestamp, PointValue value)
    {
        var point = Encode(timestamp, value);
        var id = EncodedId(series);
        var existing = _current.TryGetValue(series, out var buffer);
        var growth = Growth(point.Length, id, existing);
        TimeSeriesRequest? finished = null;
        if (_size + growth > _limit && _order.Count > 0)
        {
            finished = Flush();
            existing = false;
            buffer = null;
            growth = Growth(point.Length, id, existing: false);
        }

        if (_size + growth > _limit)
        {
            throw new RecordHeldException(string.Create(
                CultureInfo.InvariantCulture,
                $"the point of {series} at {timestamp} takes {growth + EnvelopeLength} bytes in a request, above the {_limit} bytes a request to the ingestion service is given; shorten the value or raise maxRequestBytes"));
        }

        if (!existing)
        {
            buffer = new SeriesBuffer(series, id);
            _current[series] = buffer;
            _order.Add(buffer);
        }
        else
        {
            buffer!.Points.Write([(byte)',']);
        }

        buffer!.Points.Write(point);
        buffer.Track(timestamp);
        _size += growth;
        return finished;
    }

    /// <summary>The last request, or null when no point is waiting.</summary>
    public TimeSeriesRequest? Finish() => _order.Count == 0 ? null : Flush();

    public void Dispose() => _writer.Dispose();

    private long Growth(int point, byte[] id, bool existing)
        => existing
            ? point + 1
            : point + ItemIdStart.Length + id.Length + ItemPointsStart.Length + Close.Length + (_order.Count > 0 ? 1 : 0);

    private ReadOnlySpan<byte> Encode(long timestamp, PointValue value)
    {
        _scratch.ResetWrittenCount();
        _writer.Reset(_scratch);
        _writer.WriteStartObject();
        _writer.WriteNumber(Timestamp, timestamp);
        _writer.WritePropertyName(Value);
        value.WriteTo(_writer);
        _writer.WriteEndObject();
        _writer.Flush();
        return _scratch.WrittenSpan;
    }

    private byte[] EncodedId(string series)
    {
        if (!_encodedIds.TryGetValue(series, out var encoded))
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, RequestWriter.Options))
            {
                writer.WriteStringValue(series);
            }

            encoded = buffer.WrittenSpan.ToArray();
            _encodedIds[series] = encoded;
        }

        return encoded;
    }

    private TimeSeriesRequest Flush()
    {
        var body = new byte[_size];
        var at = 0;
        Append(body, ref at, EnvelopeStart);
        var series = new List<RequestSeries>(_order.Count);
        for (var i = 0; i < _order.Count; i++)
        {
            var buffer = _order[i];
            if (i > 0)
            {
                body[at++] = (byte)',';
            }

            Append(body, ref at, ItemIdStart);
            Append(body, ref at, buffer.Id);
            Append(body, ref at, ItemPointsStart);
            Append(body, ref at, buffer.Points.WrittenSpan);
            Append(body, ref at, Close);
            series.Add(buffer.Close());
        }

        Append(body, ref at, Close);
        if (at != body.Length)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"A request to the ingestion service was measured at {body.Length} bytes and written as {at}."));
        }

        _order.Clear();
        _current.Clear();
        _size = EnvelopeLength;
        _number++;
        return new TimeSeriesRequest(_number, body, Hashing.ContentHash.Of(body), series);
    }

    private static void Append(byte[] body, ref int at, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(body.AsSpan(at));
        at += bytes.Length;
    }

    /// <summary>The points of one series waiting in the current request.</summary>
    private sealed class SeriesBuffer(string series, byte[] id)
    {
        private readonly List<SettleWindow> _windows = [];
        private long _windowStart;
        private int _windowPoints;
        private int _points;
        private long _start;
        private long _end;

        public byte[] Id { get; } = id;

        public ArrayBufferWriter<byte> Points { get; } = new();

        public void Track(long timestamp)
        {
            if (_points == 0)
            {
                _start = timestamp;
            }

            if (_windowPoints == 0)
            {
                _windowStart = timestamp;
            }

            _points++;
            _windowPoints++;
            _end = timestamp;
            if (_windowPoints == SettleWindowPoints)
            {
                _windows.Add(new SettleWindow(_windowStart, timestamp, _windowPoints));
                _windowPoints = 0;
            }
        }

        public RequestSeries Close()
        {
            if (_windowPoints > 0)
            {
                _windows.Add(new SettleWindow(_windowStart, _end, _windowPoints));
                _windowPoints = 0;
            }

            return new RequestSeries(series, _points, _start, _end, _windows.ToList());
        }
    }
}
