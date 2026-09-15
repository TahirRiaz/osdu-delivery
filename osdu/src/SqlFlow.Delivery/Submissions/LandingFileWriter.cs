using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Hashing;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Submissions;

/// <summary>One landing file as bytes: what is written, and the hash the ledger records so a repeat is recognised.</summary>
public sealed record LandingContent(byte[] Bytes, string ContentHash)
{
    public long Length => Bytes.LongLength;
}

/// <summary>
/// Writes an API submission's landing files (docs/stage4-design.md section 4.1): the rows rendered in the form the pre
/// flow reads, written under a pending name first and promoted only once every byte is there, so a pre flow triggered
/// while the write is in flight never reads half a file. Writing the same submission twice is a no-op: the file already
/// there with the same content is the file this write would produce, and one with different content is refused rather
/// than overwritten, because a pre flow may already have loaded it.
/// </summary>
public static class LandingFileWriter
{
    /// <summary>The suffix a landing file carries while it is being written.</summary>
    public const string PendingSuffix = ".osdu-pending";

    /// <summary>Renders the file's rows in its declared form, with the hash of exactly those bytes.</summary>
    public static async Task<LandingContent> RenderAsync(LandingFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var bytes = file.Format switch
        {
            LandingFormats.Csv => Encoding.UTF8.GetBytes(Csv(file)),
            LandingFormats.Ndjson => Encoding.UTF8.GetBytes(Ndjson(file)),
            LandingFormats.Json => Encoding.UTF8.GetBytes(Json(file)),
            LandingFormats.Parquet => await ParquetAsync(file, ct).ConfigureAwait(false),
            _ => throw new DeliveryException($"'{file.Format}' is not a landing format; it is one of {string.Join(", ", LandingFormats.All)}."),
        };

        return new LandingContent(bytes, ContentHash.Of(bytes));
    }

    /// <summary>
    /// Writes the file unless it is already there with this content. Returns true when the bytes were written, false when
    /// an earlier write of the same submission had already put them there.
    /// </summary>
    public static async Task<bool> WriteAsync(FileStoreRegistry stores, LandingFile file, LandingContent content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(content);
        var location = file.Location;
        if (await stores.ExistsAsync(location, ct).ConfigureAwait(false))
        {
            var existing = await ReadAsync(stores, location, ct).ConfigureAwait(false);
            if (string.Equals(existing, content.ContentHash, StringComparison.Ordinal))
            {
                return false;
            }

            throw new DeliveryException(
                $"A file is already landed at {location} with different content; a submission's landing files are written once. "
                + "Send the records under a new submission id, or remove the file deliberately if its pre flow never read it.");
        }

        var pending = location + PendingSuffix;
        try
        {
            await stores.WriteAsync(pending, new MemoryStream(content.Bytes, writable: false), ct).ConfigureAwait(false);
            await stores.WriteAsync(location, new MemoryStream(content.Bytes, writable: false), ct).ConfigureAwait(false);
        }
        catch
        {
            await DeleteQuietlyAsync(stores, pending).ConfigureAwait(false);
            throw;
        }

        await DeleteQuietlyAsync(stores, pending).ConfigureAwait(false);
        return true;
    }

    private static async Task<string> ReadAsync(FileStoreRegistry stores, string location, CancellationToken ct)
    {
        await using var stream = await stores.OpenSeekableAsync(
            new Core.Model.FileRef { Path = location, Name = Path.GetFileName(location) }, ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return ContentHash.Of(buffer.ToArray());
    }

    private static async Task DeleteQuietlyAsync(FileStoreRegistry stores, string location)
    {
        try
        {
            await stores.DeleteAsync(location, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DeliveryException)
        {
            // The pending copy is named so it is never read as a landing file; leaving one behind after a failed write
            // costs storage, not correctness, and the next write of the same file replaces it.
        }
    }

    private static string Csv(LandingFile file)
    {
        var text = new StringBuilder();
        text.Append(string.Join(",", file.Columns.Select(Escape))).Append('\n');
        foreach (var row in file.Rows)
        {
            text.Append(string.Join(",", file.Columns.Select(column => Escape(SourceRow.Stringify(Value(row, column)))))).Append('\n');
        }

        return text.ToString();
    }

    private static string Ndjson(LandingFile file)
    {
        var text = new StringBuilder();
        foreach (var row in file.Rows)
        {
            text.Append(Object(file.Columns, row).ToJsonString()).Append('\n');
        }

        return text.ToString();
    }

    private static string Json(LandingFile file)
    {
        var array = new JsonArray();
        foreach (var row in file.Rows)
        {
            array.Add(Object(file.Columns, row));
        }

        return array.ToJsonString();
    }

    private static async Task<byte[]> ParquetAsync(LandingFile file, CancellationToken ct)
    {
        var columns = file.Columns.Select(column => (column, ClrType(file.Rows, column))).ToList();
        var rows = file.Rows
            .Select(row => (IReadOnlyDictionary<string, object?>)columns.ToDictionary(
                c => c.column,
                c => Convert(Value(row, c.column), c.Item2),
                StringComparer.OrdinalIgnoreCase))
            .ToList();
        using var buffer = new MemoryStream();
        await ParquetFiles.WriteAsync(buffer, columns, rows, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static JsonObject Object(IReadOnlyList<string> columns, IReadOnlyDictionary<string, object?> row)
    {
        var node = new JsonObject();
        foreach (var column in columns)
        {
            node[column] = Value(row, column) switch
            {
                null => null,
                bool b => JsonValue.Create(b),
                long l => JsonValue.Create(l),
                int i => JsonValue.Create((long)i),
                double d => JsonValue.Create(d),
                decimal m => JsonValue.Create(m),
                var other => JsonValue.Create(SourceRow.Stringify(other)),
            };
        }

        return node;
    }

    /// <summary>The one type a parquet column takes: the narrowest that holds every value in it.</summary>
    private static Type ClrType(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string column)
    {
        var integers = true;
        var numbers = true;
        var booleans = true;
        var moments = true;
        var seen = false;
        foreach (var row in rows)
        {
            var value = Value(row, column);
            if (value is null)
            {
                continue;
            }

            seen = true;
            integers &= value is long or int or short or byte;
            numbers &= value is long or int or short or byte or double or float or decimal;
            booleans &= value is bool;
            moments &= value is DateTime or DateTimeOffset;
        }

        if (!seen)
        {
            return typeof(string);
        }

        return booleans ? typeof(bool)
            : integers ? typeof(long)
            : numbers ? typeof(double)
            : moments ? typeof(DateTime)
            : typeof(string);
    }

    private static object? Convert(object? value, Type clrType)
    {
        if (value is null)
        {
            return null;
        }

        if (clrType == typeof(string))
        {
            return SourceRow.Stringify(value);
        }

        if (clrType == typeof(DateTime))
        {
            return value switch
            {
                DateTime dt => dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime(),
                DateTimeOffset dto => dto.UtcDateTime,
                _ => null,
            };
        }

        return System.Convert.ChangeType(value, clrType, CultureInfo.InvariantCulture);
    }

    private static object? Value(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) ? value : null;

    /// <summary>One CSV field: quoted when it holds a separator, a quote or a line break, with quotes doubled.</summary>
    private static string Escape(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return value.AsSpan().IndexOfAny(',', '"', '\n') >= 0 || value.Contains('\r', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
