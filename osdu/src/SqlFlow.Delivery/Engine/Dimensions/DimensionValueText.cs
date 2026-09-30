using System.Globalization;
using System.Text;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>What a scan found at a dimension's path in one record: the JSON kind of the value, and its text.</summary>
public enum ScannedKind
{
    /// <summary>A JSON string.</summary>
    Text,

    /// <summary>A JSON number, kept as its digits were written.</summary>
    Number,
    True,
    False,
    Null,

    /// <summary>An object or an array where a value was expected: no value a dimension can hold.</summary>
    Composite,
}

/// <summary>One value a scan found at a dimension's path, as its JSON wrote it.</summary>
/// <param name="Kind">The JSON kind of the value.</param>
/// <param name="Text">The string itself, a number's digits as written, or empty for the other kinds.</param>
public readonly record struct ScannedValue(ScannedKind Kind, string Text)
{
    public static ScannedValue OfString(string text) => new(ScannedKind.Text, text);

    public static ScannedValue OfNumber(string digits) => new(ScannedKind.Number, digits);

    public static ScannedValue OfBoolean(bool value) => new(value ? ScannedKind.True : ScannedKind.False, string.Empty);

    public static ScannedValue Null { get; } = new(ScannedKind.Null, string.Empty);

    public static ScannedValue Composite { get; } = new(ScannedKind.Composite, string.Empty);
}

/// <summary>What one value a dimension read comes to.</summary>
public enum ValueReading
{
    /// <summary>A value, in its canonical text.</summary>
    Value,

    /// <summary>
    /// No value: a text property that is null, which the index keeps as the text <c>null</c> in its keyword sub-field
    /// (<c>null_value</c>), so a literal text <c>null</c> reads the same.
    /// </summary>
    Null,

    /// <summary>A value the index holds that is not of the field's type, or an object where a value was expected.</summary>
    Unreadable,
}

/// <summary>
/// How a dimension's values are written and ordered, per the way the index stores the field. A value is kept in one
/// canonical text whether an aggregation or a scan found it, so the two agree on what is one value; and slices of the
/// field are ordered the way Elasticsearch orders the field, so a range the service is asked for holds exactly the values
/// the build counts inside it.
/// </summary>
public static class DimensionValueText
{
    /// <summary>The canonical text of a date: UTC with milliseconds, the form Elasticsearch prints a date field's keys in.</summary>
    public const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private const NumberStyles NumberText = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    /// <summary>The order the index keeps a field of <paramref name="index"/> in.</summary>
    public static IComparer<string> Order(OsduFieldIndex index) => index switch
    {
        OsduFieldIndex.Number => NumberOrder.Instance,
        OsduFieldIndex.Date => DateOrder.Instance,
        OsduFieldIndex.Boolean => BooleanOrder.Instance,
        _ => CodePointOrder.Instance,
    };

    /// <summary>What an aggregation's key comes to, for a field of <paramref name="index"/>.</summary>
    public static (ValueReading Reading, string Text) FromKey(OsduFieldIndex index, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (index == OsduFieldIndex.Text && key == OsduQuery.KeywordNullValue)
        {
            return (ValueReading.Null, key);
        }

        return index switch
        {
            OsduFieldIndex.Number => CanonicalNumber(key) is { } number ? (ValueReading.Value, number) : (ValueReading.Unreadable, key),
            OsduFieldIndex.Boolean => CanonicalBoolean(key) is { } flag ? (ValueReading.Value, flag) : (ValueReading.Unreadable, key),
            OsduFieldIndex.Date => CanonicalDate(key) is { } date ? (ValueReading.Value, date) : (ValueReading.Unreadable, key),
            _ => (ValueReading.Value, key),
        };
    }

    /// <summary>What a value a scan found comes to, for a field of <paramref name="index"/>.</summary>
    public static (ValueReading Reading, string Text) FromScan(OsduFieldIndex index, ScannedValue value)
    {
        if (value.Kind == ScannedKind.Composite)
        {
            return (ValueReading.Unreadable, string.Empty);
        }

        if (value.Kind == ScannedKind.Null)
        {
            // Only text keeps a null in the index (as the text null); for any other field it is simply no value, which
            // the aggregation never sees either, so it counts as nothing at all.
            return index == OsduFieldIndex.Text ? (ValueReading.Null, OsduQuery.KeywordNullValue) : (ValueReading.Unreadable, string.Empty);
        }

        var text = value.Kind switch
        {
            ScannedKind.True => "true",
            ScannedKind.False => "false",
            _ => value.Text,
        };
        return index switch
        {
            OsduFieldIndex.Text or OsduFieldIndex.Keyword => text == OsduQuery.KeywordNullValue && index == OsduFieldIndex.Text
                ? (ValueReading.Null, text)
                : (ValueReading.Value, text),
            OsduFieldIndex.Date when value.Kind == ScannedKind.Number && long.TryParse(value.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var millis)
                => FromEpochMillis(millis),
            _ => FromKey(index, text),
        };
    }

    /// <summary>
    /// A number in one text: a decimal without exponent or trailing zeros where it fits a decimal, and the shortest text
    /// that reads back to the same double otherwise; null for text that is not a finite number.
    /// </summary>
    public static string? CanonicalNumber(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (decimal.TryParse(text, NumberText, CultureInfo.InvariantCulture, out var exact))
        {
            // The format prints no trailing zero and no exponent, so 12.50, 12.5 and 1.25E1 are one text; zero has one
            // text whatever its sign or scale.
            return exact == 0m ? "0" : exact.ToString("0.############################", CultureInfo.InvariantCulture);
        }

        return double.TryParse(text, NumberText, CultureInfo.InvariantCulture, out var approximate) && double.IsFinite(approximate)
            ? approximate.ToString("R", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary><c>true</c> or <c>false</c>, from those texts or from the 1 and 0 a boolean term is kept as; null otherwise.</summary>
    public static string? CanonicalBoolean(string text) => text switch
    {
        "true" or "1" => "true",
        "false" or "0" => "false",
        _ => null,
    };

    /// <summary>A date as UTC with milliseconds, from any ISO 8601 text or epoch milliseconds; null for anything else.</summary>
    public static string? CanonicalDate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text.Trim().Length == text.Length
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
        {
            return date.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);
        }

        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var millis) ? FromEpochMillis(millis).Text : null;
    }

    private static (ValueReading Reading, string Text) FromEpochMillis(long millis)
    {
        try
        {
            return (ValueReading.Value, DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture));
        }
        catch (ArgumentOutOfRangeException)
        {
            return (ValueReading.Unreadable, millis.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Text in the order of its characters' Unicode code points, which is the order of their UTF-8 bytes and so the order
    /// Elasticsearch keeps a keyword's terms in. An ordinal comparison of .NET strings compares UTF-16 code units, which
    /// puts a character beyond the basic plane before one from U+E000 to U+FFFF; a range split on that order would put a
    /// value in the wrong slice.
    /// </summary>
    public sealed class CodePointOrder : IComparer<string>
    {
        public static CodePointOrder Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var i = 0;
            var j = 0;
            while (i < x.Length && j < y.Length)
            {
                var a = Next(x, ref i);
                var b = Next(y, ref j);
                if (a != b)
                {
                    return a < b ? -1 : 1;
                }
            }

            return (i < x.Length).CompareTo(j < y.Length);
        }

        /// <summary>The code point at <paramref name="at"/>, moving past it; a lone surrogate counts as itself.</summary>
        private static int Next(string text, ref int at)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(at), out var rune, out var used) == System.Buffers.OperationStatus.Done)
            {
                at += used;
                return rune.Value;
            }

            return text[at++];
        }
    }

    private sealed class NumberOrder : IComparer<string>
    {
        public static NumberOrder Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null || y is null)
            {
                return x is null ? -1 : 1;
            }

            if (decimal.TryParse(x, NumberText, CultureInfo.InvariantCulture, out var a) && decimal.TryParse(y, NumberText, CultureInfo.InvariantCulture, out var b))
            {
                return a.CompareTo(b);
            }

            var left = double.TryParse(x, NumberText, CultureInfo.InvariantCulture, out var da) ? da : double.NaN;
            var right = double.TryParse(y, NumberText, CultureInfo.InvariantCulture, out var db) ? db : double.NaN;
            var compared = left.CompareTo(right);
            return compared != 0 ? compared : string.CompareOrdinal(x, y);
        }
    }

    private sealed class DateOrder : IComparer<string>
    {
        public static DateOrder Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null || y is null)
            {
                return x is null ? -1 : 1;
            }

            var a = DateTimeOffset.TryParse(x, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var left);
            var b = DateTimeOffset.TryParse(y, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var right);
            if (a && b)
            {
                var compared = left.CompareTo(right);
                return compared != 0 ? compared : string.CompareOrdinal(x, y);
            }

            // Text that is no date sorts after every date, in ordinal order among itself.
            return a != b ? (a ? -1 : 1) : string.CompareOrdinal(x, y);
        }
    }

    private sealed class BooleanOrder : IComparer<string>
    {
        public static BooleanOrder Instance { get; } = new();

        public int Compare(string? x, string? y)
            => Rank(x).CompareTo(Rank(y)) is var compared and not 0 ? compared : string.CompareOrdinal(x, y);

        private static int Rank(string? text) => text switch
        {
            null => -1,
            "false" => 0,
            "true" => 1,
            _ => 2,
        };
    }
}
