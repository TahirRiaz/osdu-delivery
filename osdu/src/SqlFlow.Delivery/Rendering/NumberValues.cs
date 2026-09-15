using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// How a value becomes the number a record carries (docs/delivery/documents.md). JSON (RFC 8259) has no NaN or Infinity,
/// and an IEEE 754 double is the precision every reader of a record agrees on, so what a value may become depends on the
/// property it fills: a <c>number</c> property takes any finite value, a whole one exactly and anything else as the nearest
/// double; an <c>integer</c> property takes a whole value inside the range its <c>int32</c> or <c>int64</c> format declares;
/// a text property takes the shortest text that reads back as the same number. Text is read only in the form it states,
/// never guessed at: <c>12,5</c> is twelve and a half or a hundred and twenty-five depending on the separators, so it is
/// read only with the separators a number modifier gives.
/// </summary>
internal static class NumberValues
{
    /// <summary>The decimal separator a value is read with when nothing else is said.</summary>
    public const string DecimalPoint = ".";

    /// <summary>The integer format whose range is 32 bits; any other integer property holds 64 bits.</summary>
    public const string Int32Format = "int32";

    /// <summary>The separators a number modifier accepts between digit groups; a space also stands for a no-break or thin space.</summary>
    public static readonly IReadOnlyList<string> GroupSeparators = [",", ".", " ", "'"];

    private const string NonFinite = "NaN and Infinity are not numbers a JSON record can carry (RFC 8259)";

    private const int MaxShown = 64;

    /// <summary>2^53: past it a double no longer holds every whole number, so a whole double there is not a known integer.</summary>
    private const double ExactDoubleLimit = 9007199254740992d;

    /// <summary>2^63: the first whole number a 64-bit integer cannot hold.</summary>
    private const double Int64Limit = 9223372036854775808d;

    /// <summary>Whether a value is a number already: a numeric column from the drop, or a number a modifier read.</summary>
    public static bool IsNumber(object? value)
        => value is long or int or short or sbyte or byte or ulong or uint or ushort or double or float or decimal;

    /// <summary>A float as the double it was written as: <c>12.3f</c> is <c>12.3</c>, not the <c>12.300000190734863</c> its bits widen to.</summary>
    public static double Widen(float value)
        => float.IsFinite(value) ? double.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture) : value;

    /// <summary>
    /// A number's shortest invariant text: a double or a float the shortest text that reads back as the same value, a
    /// decimal every digit it holds without trailing zeros, and never an exponent for a decimal.
    /// </summary>
    public static string Text(object number) => number switch
    {
        decimal exact => exact == 0m ? "0" : exact.ToString("0.############################", CultureInfo.InvariantCulture),
        float single => single.ToString("R", CultureInfo.InvariantCulture),
        double real => real.ToString("R", CultureInfo.InvariantCulture),
        IFormattable whole => whole.ToString(null, CultureInfo.InvariantCulture),
        _ => SourceRow.Stringify(number) ?? string.Empty,
    };

    /// <summary>
    /// Why a number modifier's separators cannot read numbers, as a phrase that follows "number", or null when they can.
    /// The decimal separator is '.' or ','; the group separator, when there is one, is ',', '.', a space or an apostrophe,
    /// and never the decimal separator.
    /// </summary>
    public static string? SeparatorsProblem(string? decimalSeparator, string? groupSeparator)
    {
        var point = decimalSeparator ?? DecimalPoint;
        if (point is not ("." or ","))
        {
            return $"takes '.' or ',' as its decimal separator, not '{point}'";
        }

        if (groupSeparator is null)
        {
            return null;
        }

        if (!GroupSeparators.Contains(groupSeparator, StringComparer.Ordinal))
        {
            return $"takes ',', '.', a space or an apostrophe as its group separator, not '{groupSeparator}'";
        }

        return groupSeparator == point ? $"cannot use '{point}' both between digit groups and before the decimals" : null;
    }

    /// <summary>
    /// Reads a value as a number for the number modifier: a number from the drop as it is, and text written with the given
    /// separators. The number is a <see cref="long"/>, a <see cref="decimal"/> or a <see cref="double"/>; the problem, when
    /// there is one, reads "value '...' is not a valid number (why)".
    /// </summary>
    public static bool TryRead(object value, string? decimalSeparator, string? groupSeparator, out object number, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(value);
        var point = (decimalSeparator ?? DecimalPoint)[0];
        char? group = groupSeparator is { Length: 1 } g ? g[0] : null;
        return TryReadAs(value, "number", point, group, out number, out problem);
    }

    /// <summary>The value as the JSON number a <c>number</c> property carries, or null with the reason it cannot be one.</summary>
    public static JsonValue? ToNumber(object raw, out string? problem)
    {
        if (!TryReadAs(raw, "number", '.', null, out var number, out problem))
        {
            return null;
        }

        return number switch
        {
            long whole => JsonValue.Create(whole),
            decimal exact when decimal.Truncate(exact) == exact && exact >= long.MinValue && exact <= long.MaxValue => JsonValue.Create(decimal.ToInt64(exact)),
            decimal exact => JsonValue.Create((double)exact),
            double real => JsonValue.Create(real),
            _ => throw new InvalidOperationException($"A number was read as a {number.GetType().Name}; a number is a long, a decimal or a double."),
        };
    }

    /// <summary>
    /// The value as the JSON integer an <c>integer</c> property carries, inside the range <paramref name="format"/> declares,
    /// or null with the reason it cannot be one: a fraction, a value out of range, or a double past 2^53, whose integer
    /// is not known exactly.
    /// </summary>
    public static JsonValue? ToInteger(object raw, string? format, out string? problem)
    {
        if (!TryReadAs(raw, "integer", '.', null, out var number, out problem))
        {
            return null;
        }

        long whole;
        switch (number)
        {
            case long l:
                whole = l;
                break;
            case decimal exact:
                if (decimal.Truncate(exact) != exact)
                {
                    problem = Invalid(raw, "integer", "it has a fraction");
                    return null;
                }

                if (exact < long.MinValue || exact > long.MaxValue)
                {
                    problem = Invalid(raw, "integer", "it is outside the 64-bit range an integer property holds");
                    return null;
                }

                whole = decimal.ToInt64(exact);
                break;
            case double real:
                if (Math.Floor(real) != real)
                {
                    problem = Invalid(raw, "integer", "it has a fraction");
                    return null;
                }

                if (real >= Int64Limit || real < -Int64Limit)
                {
                    problem = Invalid(raw, "integer", "it is outside the 64-bit range an integer property holds");
                    return null;
                }

                if (Math.Abs(real) > ExactDoubleLimit)
                {
                    problem = Invalid(raw, "integer",
                        "it is a double beyond 2^53, where a double no longer holds every whole number exactly, so the integer it stands for is not known; deliver it from an integer or decimal column, or as text");
                    return null;
                }

                whole = (long)real;
                break;
            default:
                throw new InvalidOperationException($"A number was read as a {number.GetType().Name}; a number is a long, a decimal or a double.");
        }

        if (format == Int32Format && whole is < int.MinValue or > int.MaxValue)
        {
            problem = Invalid(raw, "integer", string.Create(CultureInfo.InvariantCulture, $"the template declares int32 here, which holds {int.MinValue} to {int.MaxValue}"));
            return null;
        }

        return JsonValue.Create(whole);
    }

    /// <summary>A number from the drop as the text a string property carries, or null with the reason: NaN and Infinity are refused.</summary>
    public static string? ToText(object raw, out string? problem)
    {
        problem = null;
        if (raw is double real && !double.IsFinite(real) || raw is float single && !float.IsFinite(single))
        {
            problem = Invalid(raw, "number", NonFinite);
            return null;
        }

        return Text(raw);
    }

    private static bool TryReadAs(object value, string kind, char point, char? group, out object number, out string? problem)
    {
        number = 0L;
        problem = null;
        switch (value)
        {
            case double real when !double.IsFinite(real):
            case float single when !float.IsFinite(single):
                problem = Invalid(value, kind, NonFinite);
                return false;
            case float single:
                number = Widen(single);
                return true;
            case long or double or decimal:
                number = value;
                return true;
            case int or short or sbyte or byte or uint or ushort:
                number = System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return true;
            case ulong unsigned:
                number = unsigned <= long.MaxValue ? (long)unsigned : (decimal)unsigned;
                return true;
            case bool:
                problem = Invalid(value, kind, "a boolean is not a number");
                return false;
            case DateTimeOffset or DateTime or DateOnly:
                problem = Invalid(value, kind, "a date is not a number");
                return false;
        }

        return TryParse(SourceRow.Stringify(value) ?? string.Empty, kind, point, group, out number, out problem);
    }

    /// <summary>
    /// Reads text that states a number: an optional sign (a Unicode minus included), digits, with the group separator
    /// only between groups of three after the first, the decimal separator once, and an optional exponent. The number is a
    /// decimal when one holds it, and a double beyond that.
    /// </summary>
    private static bool TryParse(string text, string kind, char point, char? group, out object number, out string? problem)
    {
        number = 0L;
        problem = null;
        var value = text.Trim().Replace('−', '-');
        if (group == ' ')
        {
            value = value.Replace(' ', ' ').Replace(' ', ' ').Replace(' ', ' ');
        }

        var i = 0;
        var negative = false;
        if (i < value.Length && value[i] is '+' or '-')
        {
            negative = value[i] == '-';
            i++;
        }

        var whole = new StringBuilder();
        var runs = new List<int>();
        var run = 0;
        var grouped = false;
        while (i < value.Length)
        {
            var c = value[i];
            if (char.IsAsciiDigit(c))
            {
                whole.Append(c);
                run++;
                i++;
            }
            else if (group is { } separator && c == separator && run > 0 && i + 1 < value.Length && char.IsAsciiDigit(value[i + 1]))
            {
                runs.Add(run);
                run = 0;
                grouped = true;
                i++;
            }
            else
            {
                break;
            }
        }

        runs.Add(run);

        var fraction = new StringBuilder();
        if (i < value.Length && value[i] == point)
        {
            i++;
            while (i < value.Length && char.IsAsciiDigit(value[i]))
            {
                fraction.Append(value[i]);
                i++;
            }
        }

        var exponent = string.Empty;
        if (i < value.Length && value[i] is 'e' or 'E' && (whole.Length > 0 || fraction.Length > 0))
        {
            var j = i + 1;
            if (j < value.Length && value[j] is '+' or '-')
            {
                j++;
            }

            var digits = j;
            while (j < value.Length && char.IsAsciiDigit(value[j]))
            {
                j++;
            }

            if (j > digits)
            {
                exponent = value[(i + 1)..j];
                i = j;
            }
        }

        if (i != value.Length || (whole.Length == 0 && fraction.Length == 0) || (grouped && !WellGrouped(runs)))
        {
            problem = Invalid(text, kind, Unreadable(value, point, group));
            return false;
        }

        var nonZero = whole.ToString().Any(c => c != '0') || fraction.ToString().Any(c => c != '0');
        var invariant = (negative ? "-" : string.Empty)
            + (whole.Length == 0 ? "0" : whole.ToString())
            + (fraction.Length == 0 ? string.Empty : "." + fraction)
            + (exponent.Length == 0 ? string.Empty : "E" + exponent);

        if (decimal.TryParse(invariant, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact) && (exact != 0m || !nonZero))
        {
            // A negative zero is zero: it must not reach a record or a key as "-0".
            number = exact == 0m ? 0m : exact;
            return true;
        }

        if (!double.TryParse(invariant, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
        {
            problem = Invalid(text, kind, $"it is not written as a number: digits with an optional sign, {Describe(point, group)}, and an optional exponent");
            return false;
        }

        if (!double.IsFinite(real))
        {
            problem = Invalid(text, kind, "it is beyond the range of a double, about 1.8E+308");
            return false;
        }

        if (real == 0d && nonZero)
        {
            problem = Invalid(text, kind, "it is too close to zero for a double to tell it apart from zero");
            return false;
        }

        number = real;
        return true;
    }

    /// <summary>The first group holds one to three digits and every later group exactly three, so a misplaced separator is never read past.</summary>
    private static bool WellGrouped(List<int> runs) => runs[0] is >= 1 and <= 3 && runs.Skip(1).All(r => r == 3);

    private static string Unreadable(string value, char point, char? group)
    {
        var bare = value.TrimStart('+', '-');
        if (bare.Equals("NaN", StringComparison.OrdinalIgnoreCase) || bare.Equals("Infinity", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("Inf", StringComparison.OrdinalIgnoreCase) || bare == "∞")
        {
            return NonFinite;
        }

        if (value.Any(char.IsAsciiDigit) && value.All(c => char.IsAsciiDigit(c) || c is ',' or '.' or '\'' or '+' or '-' || char.IsWhiteSpace(c)))
        {
            return $"it is written with separators this entry does not read, which reads {Describe(point, group)}; "
                + "give the separators it is written with, such as number: { decimal: \",\", group: \" \" }";
        }

        return $"it is not written as a number: digits with an optional sign, {Describe(point, group)}, and an optional exponent";
    }

    private static string Describe(char point, char? group) => group is { } separator
        ? $"'{point}' before the decimals and {Name(separator)} between groups of three digits"
        : $"'{point}' before the decimals and no separator between digit groups";

    private static string Name(char separator) => separator == ' ' ? "a space" : $"'{separator}'";

    private static string Invalid(object value, string kind, string reason) => $"value '{Shown(value)}' is not a valid {kind} ({reason})";

    private static string Shown(object value)
    {
        var text = value switch
        {
            string s => s,
            _ when IsNumber(value) => Text(value),
            _ => SourceRow.Stringify(value) ?? string.Empty,
        };

        return text.Length <= MaxShown ? text : text[..MaxShown] + "...";
    }
}
