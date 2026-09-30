using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>What a dimension export holds: its members, or its originals with the member each belongs to.</summary>
public enum DimensionExportSet
{
    Members,
    Originals,
}

/// <summary>How a dimension export is written.</summary>
public enum DimensionExportFormat
{
    /// <summary>Comma-separated values with a header row (RFC 4180), for a spreadsheet.</summary>
    Csv,

    /// <summary>One JSON object a line, every value exactly as the ledger holds it.</summary>
    JsonLines,
}

/// <summary>
/// Writes a dimension's members or originals to a stream, a ledger page at a time, so an export of millions of originals
/// never holds more than one page (docs/dimension-plan.md, Stage 5). The API's download and the CLI's export both write
/// through here, so the two are the same file.
/// </summary>
/// <remarks>
/// A CSV cell a spreadsheet would run as a formula (text starting <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a
/// carriage return that is not a plain number) is written with a leading apostrophe, which a spreadsheet shows as text; the
/// JSON Lines export keeps every value exactly, for a program to read.
/// </remarks>
public static class DimensionExport
{
    /// <summary>How a CSV line ends (RFC 4180).</summary>
    private const string CsvLineEnd = "\r\n";

    /// <summary>How a JSON Lines line ends.</summary>
    private const string JsonLineEnd = "\n";

    private static readonly string[] MemberColumns =
        ["member_id", "value", "records", "records_exact", "originals", "unfilterable", "filter_parts", "filter", "first_seen_utc"];

    private static readonly string[] OriginalColumns =
        ["value_id", "original", "member", "left_out", "note", "count", "filterable", "first_seen_utc"];

    /// <summary>The set a query parameter names (<c>members</c> or <c>originals</c>), or null for any other text.</summary>
    public static DimensionExportSet? SetOf(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "members" => DimensionExportSet.Members,
        "originals" or "values" => DimensionExportSet.Originals,
        _ => null,
    };

    /// <summary>The format a query parameter names (<c>csv</c>, or <c>jsonl</c> for JSON Lines), or null for any other text.</summary>
    public static DimensionExportFormat? FormatOf(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "csv" => DimensionExportFormat.Csv,
        "jsonl" or "ndjson" or "json-lines" => DimensionExportFormat.JsonLines,
        _ => null,
    };

    /// <summary>The media type of a format.</summary>
    public static string MediaType(DimensionExportFormat format)
        => format == DimensionExportFormat.Csv ? "text/csv; charset=utf-8" : "application/x-ndjson; charset=utf-8";

    /// <summary>The file an export is saved as: the flow, the partition and the dimension, with the set and the format.</summary>
    public static string FileName(DimensionState dimension, DimensionExportSet set, DimensionExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        var stem = string.Join('-', new[] { dimension.FlowName, dimension.Partition ?? "partition", dimension.Name, set == DimensionExportSet.Members ? "members" : "originals" }
            .Select(Safe));
        return stem + (format == DimensionExportFormat.Csv ? ".csv" : ".jsonl");
    }

    /// <summary>
    /// Writes every member (or original) the dimension holds now to <paramref name="output"/> as UTF-8, in order of clean
    /// value (or of arrival), and returns how many rows it wrote. The stream is left open.
    /// </summary>
    public static async Task<long> WriteAsync(
        ILedger ledger, DimensionState dimension, DimensionExportSet set, DimensionExportFormat format, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            var rows = await WriteAsync(ledger, dimension, set, format, writer, ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            return rows;
        }
    }

    /// <summary>
    /// Writes every member (or original) the dimension holds now to <paramref name="output"/>, in order of clean value (or of
    /// arrival), and returns how many rows it wrote. Each line ends as its format says, whatever the writer's own line ending
    /// is: CRLF for CSV (RFC 4180), LF for JSON Lines.
    /// </summary>
    public static async Task<long> WriteAsync(
        ILedger ledger, DimensionState dimension, DimensionExportSet set, DimensionExportFormat format, TextWriter output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentNullException.ThrowIfNull(output);
        var ending = format == DimensionExportFormat.Csv ? CsvLineEnd : JsonLineEnd;
        return set == DimensionExportSet.Members
            ? await MembersAsync(ledger, dimension.DimensionId, format, output, ending, ct).ConfigureAwait(false)
            : await OriginalsAsync(ledger, dimension.DimensionId, format, output, ending, ct).ConfigureAwait(false);
    }

    private static async Task<long> MembersAsync(ILedger ledger, int dimensionId, DimensionExportFormat format, TextWriter writer, string ending, CancellationToken ct)
    {
        if (format == DimensionExportFormat.Csv)
        {
            await writer.WriteAsync(string.Join(',', MemberColumns) + ending).ConfigureAwait(false);
        }

        long rows = 0;
        DimensionMemberCursor? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ledger.ListDimensionMembersAsync(dimensionId, new DimensionMemberQuery(null, false, after, OsduLedger.MaxDimensionPage), ct).ConfigureAwait(false);
            foreach (var member in page)
            {
                var line = format == DimensionExportFormat.Csv
                    ? Csv(
                        Number(member.MemberId), member.Value, Number(member.Records), Bool(member.RecordsExact), Number(member.Originals),
                        Number(member.Unfilterable), Number(member.FilterParts), member.Filter, Instant(member.FirstSeenUtc))
                    : Json(w =>
                    {
                        w.WriteNumber("memberId", member.MemberId);
                        w.WriteString("value", member.Value);
                        w.WriteNumber("records", member.Records);
                        w.WriteBoolean("recordsExact", member.RecordsExact);
                        w.WriteNumber("originals", member.Originals);
                        w.WriteNumber("unfilterable", member.Unfilterable);
                        w.WriteNumber("filterParts", member.FilterParts);
                        w.WriteString("filter", member.Filter);
                        w.WriteString("firstSeenUtc", Instant(member.FirstSeenUtc));
                    });
                await writer.WriteAsync(line + ending).ConfigureAwait(false);
                rows++;
            }

            if (page.Count < OsduLedger.MaxDimensionPage)
            {
                return rows;
            }

            after = new DimensionMemberCursor(page[^1].Value, page[^1].Records);
        }
    }

    private static async Task<long> OriginalsAsync(ILedger ledger, int dimensionId, DimensionExportFormat format, TextWriter writer, string ending, CancellationToken ct)
    {
        if (format == DimensionExportFormat.Csv)
        {
            await writer.WriteAsync(string.Join(',', OriginalColumns) + ending).ConfigureAwait(false);
        }

        long rows = 0;
        DimensionValueCursor? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ledger.ListDimensionValuesAsync(
                dimensionId, new DimensionValueQuery(null, null, false, false, after, OsduLedger.MaxDimensionPage), ct).ConfigureAwait(false);
            foreach (var value in page)
            {
                var line = format == DimensionExportFormat.Csv
                    ? Csv(
                        Number(value.ValueId), value.Original, value.MemberValue, value.LeftOut, value.Note, Number(value.Count), Bool(value.Filterable),
                        Instant(value.FirstSeenUtc))
                    : Json(w =>
                    {
                        w.WriteNumber("valueId", value.ValueId);
                        w.WriteString("original", value.Original);
                        w.WriteString("member", value.MemberValue);
                        w.WriteString("leftOut", value.LeftOut);
                        w.WriteString("note", value.Note);
                        w.WriteNumber("count", value.Count);
                        w.WriteBoolean("filterable", value.Filterable);
                        w.WriteString("firstSeenUtc", Instant(value.FirstSeenUtc));
                    });
                await writer.WriteAsync(line + ending).ConfigureAwait(false);
                rows++;
            }

            if (page.Count < OsduLedger.MaxDimensionPage)
            {
                return rows;
            }

            after = new DimensionValueCursor(page[^1].ValueId, page[^1].Count);
        }
    }

    /// <summary>One CSV line: every cell quoted when it needs to be, a formula a spreadsheet would run made text.</summary>
    internal static string Csv(params string?[] cells)
    {
        var line = new StringBuilder();
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                line.Append(',');
            }

            var cell = Defused(cells[i] ?? string.Empty);
            if (cell.AsSpan().IndexOfAny(",\"\r\n") >= 0 || (cell.Length > 0 && (char.IsWhiteSpace(cell[0]) || char.IsWhiteSpace(cell[^1]))))
            {
                line.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                line.Append(cell);
            }
        }

        return line.ToString();
    }

    /// <summary>
    /// <paramref name="cell"/> as a spreadsheet will show it rather than run it: text starting like a formula gains a leading
    /// apostrophe, and a plain number, negative ones included, is left as it is.
    /// </summary>
    internal static string Defused(string cell)
    {
        if (cell.Length == 0 || cell[0] is not ('=' or '+' or '-' or '@' or '\t' or '\r'))
        {
            return cell;
        }

        return double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? cell : "'" + cell;
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        // A download is not embedded in a page, so a value's own characters are written as they are rather than escaped.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Instant(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>A part of a file name: letters, digits, dots, dashes and underscores, anything else a dash.</summary>
    private static string Safe(string part)
    {
        var safe = new StringBuilder(part.Length);
        foreach (var c in part)
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        }

        return safe.Length == 0 ? "dimension" : safe.ToString();
    }
}
