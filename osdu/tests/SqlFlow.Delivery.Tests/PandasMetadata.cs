using System.Text.Json.Nodes;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The footer entry a pandas writer (pyarrow, fastparquet) leaves in a parquet file, as the fixtures and the sample
/// estate write it. A reader takes the row labels from <c>index_columns</c>, either a range described in place or the
/// name of a column the file stores, and it needs a descriptor in <c>columns</c> for every column the file holds, the
/// stored index included. An entry without the descriptors raises in the reader rather than returning rows, and a bulk
/// service that reads a chunk as a dataframe then refuses the file as malformed: the delivery holds such a chunk
/// before it sends it (<see cref="ParquetFiles.PandasDefect"/>), so a fixture that wrote a partial entry would prove
/// nothing about a real service.
/// </summary>
public static class PandasMetadata
{
    /// <summary>The entry for a file whose rows a reader numbers from a range, over the columns the file stores.</summary>
    public static Dictionary<string, string> Range(IReadOnlyList<(string Name, Type ClrType)> columns, long start, long stop, long step = 1)
        => Entry(columns, new JsonArray(new JsonObject
        {
            ["kind"] = "range",
            ["name"] = null,
            ["start"] = start,
            ["stop"] = stop,
            ["step"] = step,
        }));

    /// <summary>The entry for a file that stores its row labels in one of its own columns.</summary>
    public static Dictionary<string, string> Stored(IReadOnlyList<(string Name, Type ClrType)> columns, string index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        return Entry(columns, new JsonArray(index));
    }

    /// <summary>The entry as JSON, with a descriptor for every column of the file.</summary>
    public static string Json(IReadOnlyList<(string Name, Type ClrType)> columns, JsonArray index)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(index);
        return new JsonObject
        {
            ["index_columns"] = index,
            ["column_indexes"] = new JsonArray(),
            ["columns"] = new JsonArray(columns.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name,
                ["field_name"] = c.Name,
                ["pandas_type"] = PandasType(c.ClrType),
                ["numpy_type"] = NumpyType(c.ClrType),
                ["metadata"] = null,
            }).ToArray()),
        }.ToJsonString();
    }

    private static Dictionary<string, string> Entry(IReadOnlyList<(string Name, Type ClrType)> columns, JsonArray index)
        => new(StringComparer.Ordinal) { [ParquetFiles.PandasMetadataKey] = Json(columns, index) };

    /// <summary>What pandas calls the type of a column the parquet file holds as <paramref name="clr"/>.</summary>
    private static string PandasType(Type clr) => Underlying(clr) switch
    {
        var t when t == typeof(double) || t == typeof(float) => "float64",
        var t when t == typeof(long) || t == typeof(int) || t == typeof(short) || t == typeof(byte) => "int64",
        var t when t == typeof(bool) => "bool",
        var t when t == typeof(DateTime) || t == typeof(DateTimeOffset) => "datetime",
        var t when t == typeof(string) => "unicode",
        _ => "object",
    };

    /// <summary>The numpy type that pandas column reads back as.</summary>
    private static string NumpyType(Type clr) => Underlying(clr) switch
    {
        var t when t == typeof(double) || t == typeof(float) => "float64",
        var t when t == typeof(long) || t == typeof(int) || t == typeof(short) || t == typeof(byte) => "int64",
        var t when t == typeof(bool) => "bool",
        var t when t == typeof(DateTime) || t == typeof(DateTimeOffset) => "datetime64[ns]",
        _ => "object",
    };

    private static Type Underlying(Type clr)
    {
        ArgumentNullException.ThrowIfNull(clr);
        return Nullable.GetUnderlyingType(clr) ?? clr;
    }
}
