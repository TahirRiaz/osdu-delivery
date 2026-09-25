using System.Globalization;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Expressions;

/// <summary>
/// The values an expression works with and the rules it compares and computes them by (osdu/docs/mapping-templates.md,
/// "Expressions"). A value is no value (null), text, a number (a decimal, or a double where the row held one), true or
/// false, or a date as the row held it. Missing and blank text are both no value. Text compares ignoring case and the
/// spaces around it, as the rest of the mapping matches text; a number compares with a number, or with text that is one.
/// </summary>
internal static class ExpressionValues
{
    /// <summary>How long a value is quoted in a message before it is cut.</summary>
    private const int Shown = 80;

    /// <summary>A row's value as the expression reads it: numbers as decimals where they fit exactly, a Guid as its text.</summary>
    public static object? Normalize(object? value) => value switch
    {
        null => null,
        string or bool or decimal or double or DateTimeOffset or DateTime or DateOnly => value,
        long or int or short or sbyte or byte or ulong or uint or ushort => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        float single => NumberValues.Widen(single),
        _ => SourceRow.Stringify(value),
    };

    /// <summary>True for no value: missing, or text that is blank.</summary>
    public static bool IsEmpty(object? value) => value is null || (value is string text && string.IsNullOrWhiteSpace(text));

    /// <summary>A value's text, as it would be written: a number in its shortest form, true or false, a date in RFC 3339.</summary>
    public static string? Text(object? value) => value switch
    {
        null => null,
        string text => text,
        decimal or double => NumberValues.Text(value),
        _ => SourceRow.Stringify(value),
    };

    /// <summary>Whether a condition's value holds: true holds, false and no value do not, and anything else cannot be tested.</summary>
    public static bool Truth(object? value, ExpressionNode node) => value switch
    {
        bool flag => flag,
        _ when IsEmpty(value) => false,
        _ => throw new ExpressionEvaluationException(
            $"{node.Written} is {Quote(value)}, which is not true or false; compare it, such as {node.Written} = {Quote(value)}"),
    };

    /// <summary>A value as a number, a decimal or a double; text is read with '.' as its decimal separator.</summary>
    public static object Number(object? value, ExpressionNode node, string what)
    {
        if (TryNumber(value, out var number))
        {
            return number;
        }

        throw new ExpressionEvaluationException($"{node.Written} is {Quote(value)}, which is not a number, and {what}");
    }

    public static bool TryNumber(object? value, out object number)
    {
        switch (value)
        {
            case decimal exact:
                number = exact;
                return true;
            case double real when double.IsFinite(real):
                number = real;
                return true;
            case string text:
                var trimmed = text.Trim();
                if (decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    number = parsed;
                    return true;
                }

                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var large) && double.IsFinite(large))
                {
                    number = large;
                    return true;
                }

                break;
        }

        number = 0m;
        return false;
    }

    /// <summary>A sum, difference, product or quotient: exact while both sides are decimals and it fits, a double otherwise.</summary>
    public static object Arithmetic(char op, object a, object b, ExpressionNode node)
    {
        if (a is decimal x && b is decimal y)
        {
            if (op == '/' && y == 0m)
            {
                throw new ExpressionEvaluationException($"{node.Written} divides by zero");
            }

            try
            {
                return op switch
                {
                    '+' => x + y,
                    '-' => x - y,
                    '*' => x * y,
                    _ => x / y,
                };
            }
            catch (OverflowException)
            {
                // Past the range a decimal holds exactly; the double below gives the nearest value, or refuses it.
            }
        }

        var p = ToDouble(a);
        var q = ToDouble(b);
        if (op == '/' && q == 0d)
        {
            throw new ExpressionEvaluationException($"{node.Written} divides by zero");
        }

        var result = op switch
        {
            '+' => p + q,
            '-' => p - q,
            '*' => p * q,
            _ => p / q,
        };
        if (!double.IsFinite(result))
        {
            throw new ExpressionEvaluationException($"{node.Written} gives a number too large to hold");
        }

        return result;
    }

    /// <summary>
    /// Whether two values are the same. No value is the same only as no value. True or false is the same as the text
    /// true or false; a number as a number of the same value, or text that reads as one; a date as the same instant or
    /// day; and text as text that differs at most in case and the spaces around it.
    /// </summary>
    public static bool Same(object? a, object? b)
    {
        var aEmpty = IsEmpty(a);
        var bEmpty = IsEmpty(b);
        if (aEmpty || bEmpty)
        {
            return aEmpty && bEmpty;
        }

        if (a is bool || b is bool)
        {
            return Flag(a) is { } x && Flag(b) is { } y && x == y;
        }

        if (a is decimal or double || b is decimal or double)
        {
            return TryNumber(a, out var x) && TryNumber(b, out var y) && CompareNumbers(x, y) == 0;
        }

        if (IsDate(a) || IsDate(b))
        {
            return TryDate(a, out var x) && TryDate(b, out var y) && x == y;
        }

        return string.Equals(Text(a)!.Trim(), Text(b)!.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How two values order, or null when either is no value (so no ordering comparison holds for it). Numbers order as
    /// numbers, dates as instants and text ignoring case; true or false has no order, and a number or a date compared with
    /// a value that is not one cannot be ordered.
    /// </summary>
    public static int? Order(object? a, object? b, ExpressionNode left, ExpressionNode right, string op)
    {
        if (IsEmpty(a) || IsEmpty(b))
        {
            return null;
        }

        if (a is bool || b is bool)
        {
            var flag = a is bool ? left : right;
            throw new ExpressionEvaluationException($"{flag.Written} is true or false, which '{op}' cannot order");
        }

        if (a is decimal or double || b is decimal or double)
        {
            var x = Number(a, left, $"'{op}' compares it with the number {Quote(b)}");
            var y = Number(b, right, $"'{op}' compares it with the number {Quote(a)}");
            return CompareNumbers(x, y);
        }

        if (IsDate(a) || IsDate(b))
        {
            if (!TryDate(a, out var x))
            {
                throw new ExpressionEvaluationException($"{left.Written} is {Quote(a)}, which is not an ISO 8601 date, and '{op}' compares it with a date");
            }

            if (!TryDate(b, out var y))
            {
                throw new ExpressionEvaluationException($"{right.Written} is {Quote(b)}, which is not an ISO 8601 date, and '{op}' compares it with a date");
            }

            return x.CompareTo(y);
        }

        return string.Compare(Text(a)!.Trim(), Text(b)!.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A value quoted for a message: text in quotes and cut when long, anything else as its text.</summary>
    public static string Quote(object? value)
    {
        if (value is null)
        {
            return "no value";
        }

        var text = Text(value) ?? string.Empty;
        var shown = text.Length <= Shown ? text : text[..Shown] + "...";
        return value is string ? "'" + shown + "'" : shown;
    }

    private static bool? Flag(object? value) => value switch
    {
        bool flag => flag,
        string text when string.Equals(text.Trim(), "true", StringComparison.OrdinalIgnoreCase) => true,
        string text when string.Equals(text.Trim(), "false", StringComparison.OrdinalIgnoreCase) => false,
        _ => null,
    };

    private static bool IsDate(object? value) => value is DateTimeOffset or DateTime or DateOnly;

    /// <summary>A value as an instant: a date at midnight UTC, and text in ISO 8601.</summary>
    private static bool TryDate(object? value, out DateTimeOffset instant)
    {
        instant = default;
        if (value is null || !DateValues.TryRead(value, null, out var date))
        {
            return false;
        }

        instant = date switch
        {
            DateOnly day => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            DateTimeOffset moment => moment,
            _ => default,
        };
        return date is DateOnly or DateTimeOffset;
    }

    private static int CompareNumbers(object x, object y)
        => x is decimal a && y is decimal b ? a.CompareTo(b) : ToDouble(x).CompareTo(ToDouble(y));

    private static double ToDouble(object number) => number is decimal exact ? (double)exact : (double)number;
}
