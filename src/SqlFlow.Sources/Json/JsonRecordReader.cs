using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlFlow.Core;

namespace SqlFlow.Sources.Json;

/// <summary>
/// Turns a JSON file's bytes into a sequence of records, auto-detecting the three shapes the original
/// SQLFlow JSON ingestion accepted: a single object, a top-level array of objects, and newline-delimited
/// JSON (one object per line). The configured root path selects where the records live; once navigated, an
/// array fans out to one record per element and an object yields a single record.
///
/// The backing <see cref="JsonDocument"/> is kept alive only for the duration of one iteration step, so a
/// consumer must finish with each yielded <see cref="JsonElement"/> (typically by flattening it) before
/// requesting the next. The reader flattens immediately, so this holds.
/// </summary>
public static class JsonRecordReader
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Enumerates the records in <paramref name="data"/>. <paramref name="sourceType"/> picks line mode
    /// for ndjson/jsonl up front; for plain json the whole document is parsed and only on failure does the
    /// reader fall back to line-delimited parsing.
    /// </summary>
    public static IEnumerable<JsonElement> ReadRecords(byte[] data, string sourceType, string rootPath, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);
        var body = StripBom(data);

        if (body.Length == 0)
        {
            yield break;
        }

        if (IsLineDelimited(sourceType) || !TryParseWhole(body, out var document))
        {
            foreach (var record in ReadLineDelimited(body, rootPath, fileName))
            {
                yield return record;
            }

            yield break;
        }

        using (document)
        {
            if (!TryNavigate(document.RootElement, rootPath, fileName, out var root))
            {
                yield break;
            }

            foreach (var record in ExpandRoot(root))
            {
                yield return record;
            }
        }
    }

    /// <summary>
    /// Enumerates the top-level documents in <paramref name="data"/> WITHOUT navigating a root path or
    /// expanding arrays into records: the whole parsed value per JSON document (one for a plain file, one per
    /// line for ndjson/jsonl). This is the raw view the record-anchor detector reasons over, before any grain is
    /// chosen. Like <see cref="ReadRecords"/>, each yielded element is valid only until the next is requested,
    /// so a consumer must finish with it (the detector copies out its small aggregate) before continuing.
    /// </summary>
    public static IEnumerable<JsonElement> ReadTopLevelDocuments(byte[] data, string sourceType, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);
        var body = StripBom(data);

        if (body.Length == 0)
        {
            yield break;
        }

        if (IsLineDelimited(sourceType) || !TryParseWhole(body, out var document))
        {
            foreach (var line in ReadLineDelimitedDocuments(body, fileName))
            {
                yield return line;
            }

            yield break;
        }

        using (document)
        {
            yield return document.RootElement;
        }
    }

    private static IEnumerable<JsonElement> ReadLineDelimitedDocuments(ReadOnlyMemory<byte> body, string fileName)
    {
        var text = Encoding.UTF8.GetString(body.Span);
        var lineNumber = 0;
        using var lines = new StringReader(text);
        while (lines.ReadLine() is { } raw)
        {
            lineNumber++;
            var line = raw.Trim();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line, ParseOptions);
            }
            catch (JsonException ex)
            {
                throw new SqlFlowException(
                    $"Invalid JSON in '{fileName}' at line {lineNumber.ToString(CultureInfo.InvariantCulture)}: {ex.Message}", ex);
            }

            using (document)
            {
                yield return document.RootElement;
            }
        }
    }

    private static IEnumerable<JsonElement> ReadLineDelimited(ReadOnlyMemory<byte> body, string rootPath, string fileName)
    {
        var text = Encoding.UTF8.GetString(body.Span);
        var lineNumber = 0;
        using var lines = new StringReader(text);
        while (lines.ReadLine() is { } raw)
        {
            lineNumber++;
            var line = raw.Trim();

            // JSON values never start with '#'; treat such lines (and blanks) as non-data.
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line, ParseOptions);
            }
            catch (JsonException ex)
            {
                throw new SqlFlowException(
                    $"Invalid JSON in '{fileName}' at line {lineNumber.ToString(CultureInfo.InvariantCulture)}: {ex.Message}", ex);
            }

            using (document)
            {
                if (TryNavigate(document.RootElement, rootPath, fileName, out var root))
                {
                    foreach (var record in ExpandRoot(root))
                    {
                        yield return record;
                    }
                }
            }
        }
    }

    /// <summary>An array fans out to one record per element; an object is a single record; scalars are not records.</summary>
    private static IEnumerable<JsonElement> ExpandRoot(JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var element in root.EnumerateArray())
                {
                    yield return element;
                }

                break;

            case JsonValueKind.Object:
                yield return root;
                break;
        }
    }

    private static bool TryParseWhole(ReadOnlyMemory<byte> body, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(body, ParseOptions);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    /// <summary>Walks the root path (e.g. <c>$.data.records</c>, with optional <c>[index]</c> steps).</summary>
    private static bool TryNavigate(JsonElement value, string rootPath, string fileName, out JsonElement result)
    {
        result = value;
        if (string.IsNullOrWhiteSpace(rootPath) || rootPath == "$")
        {
            return true;
        }

        var trimmed = rootPath.StartsWith("$.", StringComparison.Ordinal)
            ? rootPath[2..]
            : rootPath.StartsWith('$') ? rootPath[1..] : rootPath;

        foreach (var segment in trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = segment;
            var bracket = key.IndexOf('[', StringComparison.Ordinal);
            var indices = Array.Empty<int>();

            if (bracket >= 0)
            {
                indices = ParseIndices(key[bracket..], rootPath, fileName);
                key = key[..bracket];
            }

            if (key.Length > 0)
            {
                if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(key, out result))
                {
                    return false;
                }
            }

            foreach (var index in indices)
            {
                if (result.ValueKind != JsonValueKind.Array || index >= result.GetArrayLength())
                {
                    return false;
                }

                result = result[index];
            }
        }

        return true;
    }

    private static int[] ParseIndices(string brackets, string rootPath, string fileName)
    {
        var indices = new List<int>();
        var i = 0;
        while (i < brackets.Length)
        {
            if (brackets[i] != '[')
            {
                throw new SqlFlowException($"Invalid rootPath '{rootPath}' for '{fileName}': expected '[' at '{brackets[i..]}'.");
            }

            var close = brackets.IndexOf(']', i);
            if (close < 0)
            {
                throw new SqlFlowException($"Invalid rootPath '{rootPath}' for '{fileName}': missing ']'.");
            }

            var token = brackets[(i + 1)..close];
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
            {
                throw new SqlFlowException($"Invalid rootPath index '[{token}]' in '{rootPath}' for '{fileName}'.");
            }

            indices.Add(index);
            i = close + 1;
        }

        return [.. indices];
    }

    private static bool IsLineDelimited(string sourceType)
        => sourceType.Equals("ndjson", StringComparison.OrdinalIgnoreCase)
            || sourceType.Equals("jsonl", StringComparison.OrdinalIgnoreCase);

    private static ReadOnlyMemory<byte> StripBom(byte[] data)
        => data.Length >= 3 && data[0] == Utf8Bom[0] && data[1] == Utf8Bom[1] && data[2] == Utf8Bom[2]
            ? data.AsMemory(3)
            : data;
}
