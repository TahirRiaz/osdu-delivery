using System.Globalization;
using System.Text.Json.Nodes;
using Parquet;
using Parquet.Schema;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// One array a record delivers into its dataspace: where it lives, what it holds, and where its values come from
/// (docs/documents.md, "The etp route"). The path is the array's identity inside the dataspace and is what the
/// object's XML names (osdu/specs/reservoir-ddms/INTEGRATION.md sections 5.1 and 5.3).
/// </summary>
/// <param name="Path">The array's path in the dataspace, without a leading slash.</param>
/// <param name="Type">The transport type its values cross in.</param>
/// <param name="Dimensions">Its shape, at most four dimensions.</param>
/// <param name="Column">The column of the record's bulk payload holding its values, or null when they are inline.</param>
/// <param name="Values">The values written into the document, or null when they come from the bulk payload.</param>
/// <param name="OwnerUri">The object URI the array hangs under; the record's own object when the document names none.</param>
public sealed record EtpArrayDeclaration(
    string Path,
    AnyArrayType Type,
    IReadOnlyList<long> Dimensions,
    string? Column,
    JsonArray? Values,
    string? OwnerUri)
{
    /// <summary>How many elements the shape calls for.</summary>
    public long ElementCount => Dimensions.Aggregate(1L, (total, dimension) => total * dimension);
}

/// <summary>
/// Reads a record's array declarations, fills them from the document or the record's bulk payload, and plans how they
/// cross: whole when they fit a message, and otherwise declared once and filled slice by slice
/// (osdu/specs/reservoir-ddms/INTEGRATION.md sections 4.6 and 5.4).
/// </summary>
public static class EtpArrays
{
    /// <summary>The transport types the ETP server stores, by the name a mapping writes (the schema's own symbols).</summary>
    private static readonly Dictionary<string, AnyArrayType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arrayOfBoolean"] = AnyArrayType.ArrayOfBoolean,
        ["arrayOfInt"] = AnyArrayType.ArrayOfInt,
        ["arrayOfLong"] = AnyArrayType.ArrayOfLong,
        ["arrayOfFloat"] = AnyArrayType.ArrayOfFloat,
        ["arrayOfDouble"] = AnyArrayType.ArrayOfDouble,
        ["bytes"] = AnyArrayType.Bytes,
    };

    /// <summary>The most dimensions the server takes (section 4.6).</summary>
    public const int MaxRank = 4;

    /// <summary>
    /// The arrays a rendered document declares under <c>data.Arrays</c>, each checked before anything is sent. A record
    /// that declares none delivers its object alone.
    /// </summary>
    public static IReadOnlyList<EtpArrayDeclaration> Read(JsonObject? data, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        if (data?["Arrays"] is not { } declared)
        {
            return [];
        }

        if (declared is not JsonArray entries)
        {
            throw new RecordHeldException($"{what} has a data.Arrays that is not a list of arrays");
        }

        var arrays = new List<EtpArrayDeclaration>();
        foreach (var entry in entries)
        {
            if (entry is not JsonObject array)
            {
                throw new RecordHeldException($"{what} has an entry in data.Arrays that is not an object");
            }

            arrays.Add(One(array, what, arrays.Count));
        }

        foreach (var duplicate in arrays.GroupBy(a => a.Path, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            throw new RecordHeldException(
                $"{what} declares the array path {duplicate.Key} twice; a path is an array's identity in its dataspace, so the second would overwrite the first");
        }

        return arrays;
    }

    private static EtpArrayDeclaration One(JsonObject array, string what, int index)
    {
        var where = $"{what}: data.Arrays[{index.ToString(CultureInfo.InvariantCulture)}]";
        var path = Text(array, "Path")?.TrimStart('/')
            ?? throw new RecordHeldException($"{where} names no Path, which is the array's identity in the dataspace");
        var declared = Text(array, "Type")
            ?? throw new RecordHeldException($"{where} names no Type; it is one of {string.Join(", ", Types.Keys)}");
        if (!Types.TryGetValue(declared, out var type))
        {
            throw new RecordHeldException($"{where} has Type '{declared}', which the Reservoir DDMS does not store; it is one of {string.Join(", ", Types.Keys)}");
        }

        if (array["Dimensions"] is not JsonArray dimensions || dimensions.Count == 0)
        {
            throw new RecordHeldException($"{where} names no Dimensions, which the Reservoir DDMS needs to shape the array");
        }

        if (dimensions.Count > MaxRank)
        {
            throw new RecordHeldException($"{where} has {dimensions.Count} dimensions, and the Reservoir DDMS stores at most {MaxRank}");
        }

        var shape = new List<long>(dimensions.Count);
        foreach (var dimension in dimensions)
        {
            if (!TryWhole(dimension, out var size) || size < 0)
            {
                throw new RecordHeldException($"{where} has a dimension that is not a whole number of zero or more");
            }

            shape.Add(size);
        }

        var column = Text(array, "Column");
        var values = array["Values"] as JsonArray;
        if (column is null && values is null)
        {
            throw new RecordHeldException($"{where} names neither a Column of the record's bulk payload nor inline Values, so it has nothing to send");
        }

        if (column is not null && values is not null)
        {
            throw new RecordHeldException($"{where} names both a Column and inline Values; an array's values come from one of the two");
        }

        return new EtpArrayDeclaration(path, type, shape, column, values, Text(array, "Uri"));
    }

    /// <summary>The values a declaration holds inline, as the transport type it declares.</summary>
    public static AnyArray Inline(EtpArrayDeclaration declaration, string what)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var values = declaration.Values
            ?? throw new RecordHeldException($"{what}: the array {declaration.Path} has no inline values");
        try
        {
            return declaration.Type switch
            {
                AnyArrayType.ArrayOfBoolean => AnyArray.Of(values.Select(Flag).ToArray().AsMemory()),
                AnyArrayType.ArrayOfInt => AnyArray.Of(values.Select(v => checked((int)Whole(v))).ToArray().AsMemory()),
                AnyArrayType.ArrayOfLong => AnyArray.Of(values.Select(Whole).ToArray().AsMemory()),
                AnyArrayType.ArrayOfFloat => AnyArray.Of(values.Select(v => (float)Number(v)).ToArray().AsMemory()),
                AnyArrayType.ArrayOfDouble => AnyArray.Of(values.Select(Number).ToArray().AsMemory()),
                _ => AnyArray.OfBytes(values.Select(v => checked((byte)Whole(v))).ToArray().AsMemory()),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            throw new RecordHeldException($"{what}: the inline values of the array {declaration.Path} are not all {declaration.Type} values");
        }
    }

    /// <summary>
    /// The values a declaration reads from one column of the record's bulk payload. The column's parquet values are
    /// converted to the transport type the declaration names, and a column whose values that type does not take holds
    /// the record, so a delivery never quietly changes what the source holds. The column's chunks are kept as the typed
    /// arrays parquet read them into until the whole array is assembled, so a million values are never a million boxes.
    /// </summary>
    public static async Task<AnyArray> FromParquetAsync(
        EtpArrayDeclaration declaration,
        IPayloadSource payload,
        long maxBytes,
        string what,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(payload);
        var column = declaration.Column ?? throw new RecordHeldException($"{what}: the array {declaration.Path} names no column");
        var files = await payload.ListChunksAsync(ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            throw new RecordHeldException($"{what}: the array {declaration.Path} reads the column {column}, and the record carries no bulk payload");
        }

        var budget = declaration.ElementCount * (Width(declaration.Type) ?? 10);
        if (budget > maxBytes)
        {
            throw new RecordHeldException(
                $"{what}: the array {declaration.Path} is {declaration.ElementCount} elements, about {budget} bytes, past the {maxBytes} bytes one delivery reads into memory; deliver it as several arrays");
        }

        var chunks = new List<Array>();
        var found = false;
        var elements = 0L;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = await payload.OpenAsync(file, ct).ConfigureAwait(false);
            var seekable = await ParquetFiles.EnsureSeekableAsync(stream, ct).ConfigureAwait(false);
            await using (seekable.ConfigureAwait(false))
            {
                using var reader = await Reader(seekable, file.Path, what, ct).ConfigureAwait(false);
                var field = reader.Schema.Fields.OfType<DataField>().FirstOrDefault(f => string.Equals(f.Name, column, StringComparison.Ordinal));
                if (field is null)
                {
                    continue;
                }

                found = true;
                for (var group = 0; group < reader.RowGroupCount; group++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var rowGroup = reader.OpenRowGroupReader(group);
                    var read = (await rowGroup.ReadColumnAsync(field, ct).ConfigureAwait(false)).Data;
                    elements += read.Length;
                    if (elements > declaration.ElementCount)
                    {
                        throw Mismatch(declaration, elements, what);
                    }

                    chunks.Add(read);
                }
            }
        }

        if (!found)
        {
            throw new RecordHeldException($"{what}: no file of the record's bulk payload has a column named {column}, which the array {declaration.Path} reads");
        }

        if (elements != declaration.ElementCount)
        {
            throw Mismatch(declaration, elements, what);
        }

        return Convert(declaration, chunks, (int)elements, what);
    }

    private static RecordHeldException Mismatch(EtpArrayDeclaration declaration, long elements, string what)
        => new($"{what}: the array {declaration.Path} is shaped {string.Join(" x ", declaration.Dimensions)}, which is {declaration.ElementCount} elements, and its column holds {elements}");

    private static async Task<ParquetReader> Reader(Stream seekable, string name, string what, CancellationToken ct)
    {
        try
        {
            return await ParquetReader.CreateAsync(seekable, leaveStreamOpen: true, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RecordHeldException)
        {
            throw new RecordHeldException($"{what}: the bulk payload file {name} is not a parquet file the arrays can be read from ({ex.Message})");
        }
    }

    /// <summary>The column's chunks as one array of the declared transport type, each chunk copied as it stands where it already is one.</summary>
    private static AnyArray Convert(EtpArrayDeclaration declaration, IReadOnlyList<Array> chunks, int elements, string what)
    {
        try
        {
            return declaration.Type switch
            {
                AnyArrayType.ArrayOfBoolean => AnyArray.Of(Assemble<bool>(chunks, elements, v => System.Convert.ToBoolean(v, CultureInfo.InvariantCulture))),
                AnyArrayType.ArrayOfInt => AnyArray.Of(Assemble<int>(chunks, elements, v => System.Convert.ToInt32(v, CultureInfo.InvariantCulture))),
                AnyArrayType.ArrayOfLong => AnyArray.Of(Assemble<long>(chunks, elements, v => System.Convert.ToInt64(v, CultureInfo.InvariantCulture))),
                AnyArrayType.ArrayOfFloat => AnyArray.Of(Assemble<float>(chunks, elements, v => System.Convert.ToSingle(v, CultureInfo.InvariantCulture))),
                AnyArrayType.ArrayOfDouble => AnyArray.Of(Assemble<double>(chunks, elements, v => System.Convert.ToDouble(v, CultureInfo.InvariantCulture))),
                _ => AnyArray.OfBytes(Assemble<byte>(chunks, elements, v => System.Convert.ToByte(v, CultureInfo.InvariantCulture))),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentNullException)
        {
            throw new RecordHeldException(
                $"{what}: the column {declaration.Column} does not hold {declaration.Type} values for the array {declaration.Path} ({ex.Message})");
        }
    }

    /// <summary>
    /// One array of <typeparamref name="T"/> from the chunks parquet read, copied wholesale where a chunk is already
    /// that type and converted element by element where it is not.
    /// </summary>
    private static ReadOnlyMemory<T> Assemble<T>(IReadOnlyList<Array> chunks, int elements, Func<object, T> convert)
    {
        var values = new T[elements];
        var at = 0;
        foreach (var chunk in chunks)
        {
            if (chunk is T[] exact)
            {
                exact.CopyTo(values, at);
                at += exact.Length;
                continue;
            }

            foreach (var value in chunk)
            {
                values[at++] = convert(value ?? throw new ArgumentNullException(nameof(chunks), "an ETP array holds a value for every element, and this column has an empty one"));
            }
        }

        return values;
    }

    /// <summary>
    /// How an array crosses under a byte budget: whole, or the slices that fill it once it is declared. Slices run
    /// along the first dimension that fits, and step into the next when one of its units is itself too large, which is
    /// what the reference importer does (section 5.4). Every slice is a contiguous run of a row-major array.
    /// </summary>
    public static IReadOnlyList<EtpArraySlice> Slices(IReadOnlyList<long> dimensions, long elementBytes, long budgetBytes)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentOutOfRangeException.ThrowIfLessThan(elementBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetBytes, 1);
        var perSlice = Math.Max(1, budgetBytes / elementBytes);
        var slices = new List<EtpArraySlice>();
        Cut(dimensions, perSlice, 0, new long[dimensions.Count], 0, slices);
        return slices;
    }

    private static void Cut(IReadOnlyList<long> dimensions, long perSlice, int level, long[] starts, long offset, List<EtpArraySlice> slices)
    {
        var unit = 1L;
        for (var i = level + 1; i < dimensions.Count; i++)
        {
            unit *= dimensions[i];
        }

        if (unit > perSlice && level + 1 < dimensions.Count)
        {
            // One step of this dimension is itself too large: step through it and cut the next one instead.
            for (var i = 0L; i < dimensions[level]; i++)
            {
                starts[level] = i;
                Cut(dimensions, perSlice, level + 1, starts, offset + (i * unit), slices);
            }

            starts[level] = 0;
            return;
        }

        var steps = Math.Max(1, perSlice / Math.Max(1, unit));
        for (var taken = 0L; taken < dimensions[level]; taken += steps)
        {
            var count = Math.Min(steps, dimensions[level] - taken);
            var sliceStarts = starts.ToArray();
            sliceStarts[level] = starts[level] + taken;
            var counts = new long[dimensions.Count];
            for (var i = 0; i < dimensions.Count; i++)
            {
                counts[i] = i < level ? 1 : i == level ? count : dimensions[i];
            }

            slices.Add(new EtpArraySlice(sliceStarts, counts, offset + (taken * unit), count * unit));
        }
    }

    /// <summary>The bytes one element of a transport type takes, or null for the types whose elements vary.</summary>
    private static long? Width(AnyArrayType type) => type switch
    {
        AnyArrayType.ArrayOfBoolean or AnyArrayType.Bytes => 1,
        AnyArrayType.ArrayOfInt => 5,
        AnyArrayType.ArrayOfLong => 10,
        AnyArrayType.ArrayOfFloat => sizeof(float),
        AnyArrayType.ArrayOfDouble => sizeof(double),
        _ => null,
    };

    /// <summary>
    /// A number a document carries, whatever the node is backed by. A rendered document's values come from parsed JSON
    /// and a test's from the runtime's own types, and the typed readers of <see cref="JsonValue"/> convert neither, so
    /// every number is read from the JSON text it would be written as.
    /// </summary>
    private static bool TryWhole(JsonNode? node, out long whole)
    {
        whole = 0;
        return node is JsonValue value && long.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out whole);
    }

    private static long Whole(JsonNode? node)
        => TryWhole(node, out var whole) ? whole : throw new FormatException($"'{node?.ToJsonString()}' is not a whole number.");

    private static double Number(JsonNode? node)
        => node is JsonValue value && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException($"'{node?.ToJsonString()}' is not a number.");

    private static bool Flag(JsonNode? node)
        => node is JsonValue value && bool.TryParse(value.ToJsonString(), out var flag)
            ? flag
            : throw new FormatException($"'{node?.ToJsonString()}' is not true or false.");

    private static string? Text(JsonObject entry, string name)
        => entry[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}

/// <summary>
/// One slice of an array being filled: where it starts and how much of each dimension it covers, and the contiguous
/// run of elements it takes from the whole array (osdu/specs/reservoir-ddms/INTEGRATION.md section 4.6).
/// </summary>
public sealed record EtpArraySlice(IReadOnlyList<long> Starts, IReadOnlyList<long> Counts, long Offset, long Length);
