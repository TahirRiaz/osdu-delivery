using System.Globalization;
using System.Text;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Hashing;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Planning;

/// <summary>
/// What a drop row says about the version of its source: the opaque fingerprint a flow names with
/// <c>source.fingerprint</c>, or the moment the row last changed, from <c>source.lastModified</c>. A flow declares at
/// most one of the two, so at most one is set.
/// </summary>
public readonly record struct SourceVersion(string? Fingerprint, DateTime? ModifiedUtc)
{
    public static SourceVersion Of(string? fingerprint) => new(fingerprint, null);

    public static SourceVersion At(DateTime modifiedUtc) => new(null, modifiedUtc);
}

/// <summary>Reads a source row's last-modified column as a UTC moment.</summary>
public static class LastModifiedColumn
{
    private const int MaxQuoted = 64;

    /// <summary>
    /// The row's last-modified moment. A timestamp column is taken as it is (an unzoned one as UTC); text is parsed
    /// as RFC 3339 or ISO 8601, without an offset meaning UTC. False with a reason naming the column and the value
    /// when the column is empty or holds something that is not a moment, because such a row cannot be ordered
    /// against the version the ledger holds.
    /// </summary>
    public static bool TryRead(SourceRow row, string column, out DateTime utc, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        utc = default;
        problem = null;
        switch (row.Get(column))
        {
            case DateTimeOffset dto:
                utc = dto.UtcDateTime;
                return true;
            case DateTime dt:
                utc = dt.Kind switch
                {
                    DateTimeKind.Utc => dt,
                    DateTimeKind.Local => dt.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                };
                return true;
            case string text when !string.IsNullOrWhiteSpace(text):
                if (DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    utc = parsed.UtcDateTime;
                    return true;
                }

                var quoted = text.Length <= MaxQuoted ? text : text[..MaxQuoted] + "...";
                problem = $"source.lastModified column '{column}' holds '{quoted}', which is not a date and time (use a timestamp column, or RFC 3339 text such as 2026-09-01T10:15:00Z)";
                return false;
            case null or string:
                problem = $"source.lastModified column '{column}' is empty, so the row cannot be ordered against the version the ledger holds";
                return false;
            case var other:
                problem = $"source.lastModified column '{column}' holds a {other.GetType().Name}, not a date and time";
                return false;
        }
    }
}

/// <summary>
/// The chunk files of one record's payload as storage lists them: how many, the newest modified time among them
/// (the payload's watermark), and a signature over every file's name, size and modified time, which changes when any
/// file is rewritten, added or removed.
/// </summary>
public sealed record PayloadFiles(int Count, DateTime? ModifiedUtc, string Signature)
{
    public static PayloadFiles Of(IReadOnlyList<PayloadChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        DateTime? newest = null;
        var text = new StringBuilder();
        // Names, not paths: the same files read from a drop at another location are the same payload.
        foreach (var chunk in chunks.OrderBy(c => FileName(c.Path), StringComparer.Ordinal))
        {
            var modified = chunk.Modified?.UtcDateTime;
            if (modified is { } m && (newest is null || m > newest))
            {
                newest = m;
            }

            text.Append(FileName(chunk.Path)).Append((char)0x1F)
                .Append(chunk.Size.ToString(CultureInfo.InvariantCulture)).Append((char)0x1F)
                .Append(modified?.Ticks.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('\n');
        }

        return new PayloadFiles(chunks.Count, newest, ContentHash.Of(text.ToString()));
    }

    private static string FileName(string path)
    {
        var cut = path.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? path : path[(cut + 1)..];
    }
}
