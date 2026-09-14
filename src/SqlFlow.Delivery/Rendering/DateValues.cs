using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// How the date modifier reads a value and how a date is written into a record (docs/delivery/documents.md). OSDU schemas
/// are JSON Schema draft-07, whose <c>date-time</c>, <c>date</c> and <c>time</c> formats are the RFC 3339 <c>date-time</c>,
/// <c>full-date</c> and <c>full-time</c> forms. A value is read only from a form that states its year, month and day: an
/// ISO 8601 date or date-time, or exactly the format a mapping gives. Nothing is guessed: <c>01/02/2026</c> could be
/// either month, and a time alone would take the day the render ran, so the same row would render differently tomorrow.
/// </summary>
internal static partial class DateValues
{
    /// <summary>The schema format of an RFC 3339 full-date: <c>2026-09-01</c>.</summary>
    public const string DateFormat = "date";

    /// <summary>The schema format of an RFC 3339 date-time, which is written in UTC: <c>2026-09-01T10:15:30Z</c>.</summary>
    public const string DateTimeFormat = "date-time";

    private const string FullDate = "yyyy-MM-dd";

    private const string AsWritten = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz";

    /// <summary>The letters .NET reads as date and time specifiers in a custom format.</summary>
    private const string Specifiers = "dfFghHKmMstyz";

    private static readonly string[] IsoDateTimes = IsoDateTimeForms();

    /// <summary>A date every usable format can write and read back; its hour is below twelve so a 12-hour format round-trips.</summary>
    private static readonly DateTimeOffset Sample = new(2026, 9, 1, 10, 15, 30, 125, TimeSpan.Zero);

    /// <summary>Whether a date can be written where the template's format is <paramref name="format"/>: no format, date or date-time.</summary>
    public static bool Writes(string? format) => format is null or DateFormat or DateTimeFormat;

    /// <summary>Whether a value is already a date: a timestamp read from the drop, or a date the modifier read.</summary>
    public static bool IsDate(object? value) => value is DateTimeOffset or DateTime or DateOnly;

    /// <summary>
    /// Reads a date: a <see cref="DateOnly"/> when the value states no time of day, otherwise a <see cref="DateTimeOffset"/>.
    /// A value without an offset is UTC. False when the value is not a date in the form expected.
    /// </summary>
    /// <param name="value">The incoming value: a date already, or text.</param>
    /// <param name="format">The .NET date format the text is written in, or null for ISO 8601.</param>
    /// <param name="date">The date read.</param>
    public static bool TryRead(object value, string? format, out object date)
    {
        switch (value)
        {
            case DateTimeOffset instant:
                date = instant;
                return true;
            case DateTime moment:
                date = Instant(moment);
                return true;
            case DateOnly day:
                date = day;
                return true;
        }

        date = default(DateOnly);
        var text = SourceRow.Stringify(value)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return format is null ? TryReadIso(text, out date) : TryReadExact(text, format, out date);
    }

    /// <summary>
    /// Writes a date where the template's format is <paramref name="format"/>: an RFC 3339 full-date for <c>date</c>, and
    /// an RFC 3339 date-time in UTC otherwise, the form OSDU's own timestamps take. An instant with a time of day is not
    /// written as a date, because that would drop the time; <paramref name="problem"/> says so and the result is null.
    /// </summary>
    public static string? Write(object date, string? format, out string? problem)
    {
        problem = null;
        switch (date)
        {
            case DateOnly day:
                return format == DateFormat
                    ? day.ToString(FullDate, CultureInfo.InvariantCulture)
                    : Json.CanonicalJson.FormatDateTime(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
            case DateTime moment:
                return Write(Instant(moment), format, out problem);
            case DateTimeOffset instant when format == DateFormat:
                if (instant.TimeOfDay != TimeSpan.Zero)
                {
                    problem = $"'{instant.ToString(AsWritten, CultureInfo.InvariantCulture)}' has a time of day, but the template takes a date; "
                        + "read only the date part, such as with split: { separator: T, part: 1 } before date";
                    return null;
                }

                return DateOnly.FromDateTime(instant.DateTime).ToString(FullDate, CultureInfo.InvariantCulture);
            case DateTimeOffset instant:
                return Json.CanonicalJson.FormatDateTime(instant);
            default:
                throw new ArgumentException($"'{date.GetType().Name}' is not a date.", nameof(date));
        }
    }

    /// <summary>
    /// Why a date modifier's format cannot read dates, as a phrase that follows "the date format '...'", or null when it
    /// can. The format must name a four-digit year, a month and a day of the month, and read back a date it writes.
    /// </summary>
    public static string? FormatProblem(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (string.IsNullOrWhiteSpace(format))
        {
            return "is empty; write date alone to read ISO 8601";
        }

        if (format.Trim().Length == 1)
        {
            return "is one letter, which .NET reads as a standard pattern whose layout is not written out; write the pattern, such as dd.MM.yyyy";
        }

        if (!TryTokens(format, out var tokens))
        {
            return "has a quote that is never closed";
        }

        if (!tokens.Exists(t => t.Letter == 'y'))
        {
            return "has no year (yyyy)";
        }

        if (!tokens.Exists(t => t.Letter == 'y' && t.Length >= 4))
        {
            return "reads a two-digit year, which does not say its century; the values must carry a four-digit year (yyyy)";
        }

        if (!tokens.Exists(t => t.Letter == 'M'))
        {
            return "has no month (MM, or MMM for its name)";
        }

        if (!tokens.Exists(t => t.Letter == 'd' && t.Length <= 2))
        {
            return "has no day of the month (dd)";
        }

        string written;
        try
        {
            written = Sample.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException ex)
        {
            return $"is not a .NET date format ({ex.Message})";
        }

        return DateTimeOffset.TryParseExact(written, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)
            ? null
            : $"cannot read back '{written}', a date it writes itself, so no value would match it";
    }

    private static bool TryReadIso(string text, out object date)
    {
        if (DateOnly.TryParseExact(text, FullDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            date = day;
            return true;
        }

        // RFC 3339 allows a lower-case t and z, and ISO 8601 also writes an offset without its colon (+0200), as some of
        // OSDU's own schema examples do.
        var normalized = CompactOffset().Replace(text.ToUpperInvariant(), "$1:$2");
        if (DateTimeOffset.TryParseExact(normalized, IsoDateTimes, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
        {
            date = instant;
            return true;
        }

        date = default(DateOnly);
        return false;
    }

    private static bool TryReadExact(string text, string format, out object date)
    {
        if (!DateTimeOffset.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            date = default(DateOnly);
            return false;
        }

        // The wall-clock date as written: without an offset it is the value itself, with one it is the date in that offset.
        date = HasTimeOfDay(format) ? parsed : DateOnly.FromDateTime(parsed.DateTime);
        return true;
    }

    private static DateTimeOffset Instant(DateTime moment)
        => new(moment.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(moment, DateTimeKind.Utc) : moment.ToUniversalTime());

    private static bool HasTimeOfDay(string format)
        => TryTokens(format, out var tokens) && tokens.Exists(t => t.Letter is 'H' or 'h' or 'm' or 's' or 'f' or 'F');

    /// <summary>The specifier runs of a custom .NET date format, skipping quoted and escaped literals. False when a quote is never closed.</summary>
    private static bool TryTokens(string format, out List<Token> tokens)
    {
        tokens = [];
        var i = 0;
        while (i < format.Length)
        {
            var c = format[i];
            if (c is '\'' or '"')
            {
                var close = i + 1;
                while (close < format.Length && format[close] != c)
                {
                    close += format[close] == '\\' ? 2 : 1;
                }

                if (close >= format.Length)
                {
                    return false;
                }

                i = close + 1;
            }
            else if (c == '\\')
            {
                i += 2;
            }
            else if (Specifiers.Contains(c, StringComparison.Ordinal))
            {
                var end = i;
                while (end < format.Length && format[end] == c)
                {
                    end++;
                }

                tokens.Add(new Token(c, end - i));
                i = end;
            }
            else
            {
                i++;
            }
        }

        return true;
    }

    /// <summary>A date and a time of hours and minutes, optionally seconds and fractions, after T or a space, with Z, an offset, or nothing.</summary>
    private static string[] IsoDateTimeForms()
    {
        var forms = new List<string>();
        foreach (var separator in new[] { "'T'", "' '" })
        {
            foreach (var time in new[] { "HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF" })
            {
                foreach (var zone in new[] { string.Empty, "'Z'", "zzz" })
                {
                    forms.Add(FullDate + separator + time + zone);
                }
            }
        }

        return [.. forms];
    }

    private readonly record struct Token(char Letter, int Length);

    [GeneratedRegex(@"^(.{11,}[+-]\d{2})(\d{2})$")]
    private static partial Regex CompactOffset();
}
