using System.Globalization;

namespace SqlFlow.Delivery.Expressions;

/// <summary>One function of the expression language: its name, what it takes, what it gives, and what it does.</summary>
/// <param name="Name">The name, as the documentation writes it; a call reads it ignoring case.</param>
/// <param name="Signature">How it is called, such as <c>coalesce(value, value, ...)</c>.</param>
/// <param name="Description">What it gives, in one sentence.</param>
/// <param name="MinArguments">The fewest values it takes.</param>
/// <param name="MaxArguments">The most values it takes; <see cref="int.MaxValue"/> for any number.</param>
public sealed record ExpressionFunction(string Name, string Signature, string Description, int MinArguments, int MaxArguments)
{
    internal ExpressionKind Kind { get; init; } = ExpressionKind.Any;

    internal Func<FunctionCall, object?> Invoke { get; init; } = _ => null;
}

/// <summary>A call being evaluated: the values given, and the parts of the expression they came from, for messages.</summary>
internal readonly record struct FunctionCall(ExpressionFunction Function, object?[] Values, IReadOnlyList<ExpressionNode> Arguments)
{
    public object? this[int index] => index < Values.Length ? Values[index] : null;

    public bool Has(int index) => index < Values.Length;

    /// <summary>The value at <paramref name="index"/> as text, or null for no value.</summary>
    public string? Text(int index) => ExpressionValues.Text(this[index]);

    /// <summary>The value at <paramref name="index"/> as a whole number, which it has to be, and 0 or more where <paramref name="nonNegative"/>.</summary>
    public int Whole(int index, string what, bool nonNegative)
    {
        var value = this[index];
        if (ExpressionValues.IsEmpty(value))
        {
            throw new ExpressionEvaluationException($"{Arguments[index].Written} is no value, and {Function.Name} takes {what}");
        }

        var number = ExpressionValues.Number(value, Arguments[index], $"{Function.Name} takes {what}");
        var (isWhole, whole) = number is decimal exact
            ? (exact == decimal.Truncate(exact) && exact >= int.MinValue && exact <= int.MaxValue, exact >= int.MinValue && exact <= int.MaxValue ? (int)exact : 0)
            : ((double)number == Math.Truncate((double)number) && (double)number >= int.MinValue && (double)number <= int.MaxValue, (double)number >= int.MinValue && (double)number <= int.MaxValue ? (int)(double)number : 0);
        if (!isWhole || (nonNegative && whole < 0))
        {
            throw new ExpressionEvaluationException($"{Arguments[index].Written} is {ExpressionValues.Quote(value)}, and {Function.Name} takes {what}");
        }

        return whole;
    }
}

/// <summary>
/// The functions of the expression language. The set is closed and small on purpose: each reads, tests, cleans or
/// combines the values of one row, and is named and behaves as its SQL counterpart does where SQL has one, so whoever
/// writes the ingestion SQL reads a mapping without learning another language. Text is matched ignoring case, as the
/// rest of the mapping matches it, and counted in characters as a person reads them.
/// </summary>
public static class ExpressionFunctions
{
    private static readonly IReadOnlyList<ExpressionFunction> Functions =
    [
        new("coalesce", "coalesce(value, value, ...)", "The first of the values that is there: not missing and not blank text.", 2, int.MaxValue)
        {
            Invoke = call => call.Values.FirstOrDefault(value => !ExpressionValues.IsEmpty(value)),
        },
        new("nullif", "nullif(value, other)", "No value when value is the same as other, otherwise value; nullif(depth, -999) drops a placeholder.", 2, 2)
        {
            Invoke = call => ExpressionValues.Same(call[0], call[1]) ? null : call[0],
        },
        new("empty", "empty(value)", "True when the value is missing or blank text.", 1, 1)
        {
            Kind = ExpressionKind.Boolean,
            Invoke = call => ExpressionValues.IsEmpty(call[0]),
        },
        new("trim", "trim(text)", "The text without the spaces around it.", 1, 1)
        {
            Kind = ExpressionKind.Text,
            Invoke = call => call.Text(0)?.Trim(),
        },
        new("upper", "upper(text)", "The text in upper case.", 1, 1)
        {
            Kind = ExpressionKind.Text,
            Invoke = call => call.Text(0)?.ToUpperInvariant(),
        },
        new("lower", "lower(text)", "The text in lower case.", 1, 1)
        {
            Kind = ExpressionKind.Text,
            Invoke = call => call.Text(0)?.ToLowerInvariant(),
        },
        new("substring", "substring(text, start, length)", "Part of the text: from the character at start, counting from 1, and length characters long, or to the end when length is left out.", 2, 3)
        {
            Kind = ExpressionKind.Text,
            Invoke = Substring,
        },
        new("left", "left(text, count)", "The first count characters of the text.", 2, 2)
        {
            Kind = ExpressionKind.Text,
            Invoke = call => call.Text(0) is { } text ? Characters(text, 0, call.Whole(1, "a count of characters, 0 or more", nonNegative: true)) : null,
        },
        new("right", "right(text, count)", "The last count characters of the text.", 2, 2)
        {
            Kind = ExpressionKind.Text,
            Invoke = call =>
            {
                if (call.Text(0) is not { } text)
                {
                    return null;
                }

                var count = call.Whole(1, "a count of characters, 0 or more", nonNegative: true);
                var length = new StringInfo(text).LengthInTextElements;
                return Characters(text, Math.Max(0, length - count), count);
            },
        },
        new("replace", "replace(text, find, with)", "The text with every occurrence of find, in any case, replaced by with.", 3, 3)
        {
            Kind = ExpressionKind.Text,
            Invoke = call =>
            {
                if (call.Text(0) is not { } text)
                {
                    return null;
                }

                var find = call.Text(1);
                if (string.IsNullOrEmpty(find))
                {
                    throw new ExpressionEvaluationException($"{call.Arguments[1].Written} is no text, and replace needs the text to find");
                }

                return text.Replace(find, call.Text(2) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            },
        },
        new("length", "length(text)", "How many characters the text holds.", 1, 1)
        {
            Kind = ExpressionKind.Number,
            Invoke = call => call.Text(0) is { } text ? (decimal)new StringInfo(text).LengthInTextElements : null,
        },
        new("contains", "contains(text, part)", "True when the text holds part, in any case.", 2, 2)
        {
            Kind = ExpressionKind.Boolean,
            Invoke = call => call.Text(0) is { } text && call.Text(1) is { } part && text.Contains(part, StringComparison.OrdinalIgnoreCase),
        },
        new("startsWith", "startsWith(text, part)", "True when the text starts with part, in any case.", 2, 2)
        {
            Kind = ExpressionKind.Boolean,
            Invoke = call => call.Text(0) is { } text && call.Text(1) is { } part && text.StartsWith(part, StringComparison.OrdinalIgnoreCase),
        },
        new("endsWith", "endsWith(text, part)", "True when the text ends with part, in any case.", 2, 2)
        {
            Kind = ExpressionKind.Boolean,
            Invoke = call => call.Text(0) is { } text && call.Text(1) is { } part && text.EndsWith(part, StringComparison.OrdinalIgnoreCase),
        },
        new("number", "number(value)", "The value as a number; text is read with '.' before the decimals (the number modifier reads other forms).", 1, 1)
        {
            Kind = ExpressionKind.Number,
            Invoke = call => ExpressionValues.IsEmpty(call[0]) ? null : ExpressionValues.Number(call[0], call.Arguments[0], "number reads numbers written with '.' before the decimals"),
        },
        new("text", "text(value)", "The value as text: a number in its shortest form, true or false, a date in RFC 3339.", 1, 1)
        {
            Kind = ExpressionKind.Text,
            Invoke = call => call.Text(0),
        },
        new("round", "round(number, digits)", "The number rounded to digits decimals (0 when left out), halves away from zero as SQL rounds them.", 1, 2)
        {
            Kind = ExpressionKind.Number,
            Invoke = Round,
        },
        new("abs", "abs(number)", "The number without its sign.", 1, 1)
        {
            Kind = ExpressionKind.Number,
            Invoke = call => ExpressionValues.IsEmpty(call[0])
                ? null
                : ExpressionValues.Number(call[0], call.Arguments[0], "abs takes a number") switch
                {
                    decimal exact => Math.Abs(exact),
                    var real => Math.Abs((double)real),
                },
        },
    ];

    private static readonly Dictionary<string, ExpressionFunction> ByName = Functions.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every function, in the order the documentation lists them.</summary>
    public static IReadOnlyList<ExpressionFunction> All => Functions;

    /// <summary>The function names, as a message lists them.</summary>
    public static string NameList => string.Join(", ", Functions.Select(f => f.Name));

    /// <summary>The function of that name, read ignoring case, or null.</summary>
    public static ExpressionFunction? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>
    /// The function another language's name for it means (<c>len</c>, <c>isnull</c>), or else the function whose name is an
    /// obvious near miss of <paramref name="written"/>, or null.
    /// </summary>
    internal static ExpressionFunction? Nearest(string written)
    {
        var lower = written.ToLowerInvariant();
        return Synonym(lower) ?? Functions
            .Select(f => (Function: f, Distance: Distance(lower, f.Name.ToLowerInvariant())))
            .Where(c => c.Distance <= 2)
            .OrderBy(c => c.Distance)
            .Select(c => c.Function)
            .FirstOrDefault();
    }

    /// <summary>What another language calls a function this one has, so a habit from SQL or JavaScript still finds it.</summary>
    private static ExpressionFunction? Synonym(string written) => written switch
    {
        "isnull" or "nvl" or "ifnull" => ByName["coalesce"],
        "len" or "char_length" => ByName["length"],
        "substr" or "mid" => ByName["substring"],
        "ucase" or "touppercase" => ByName["upper"],
        "lcase" or "tolowercase" => ByName["lower"],
        "string" or "tostring" or "cast" or "convert" => ByName["text"],
        "tonumber" or "parse" or "val" => ByName["number"],
        "isempty" or "isblank" => ByName["empty"],
        _ => null,
    };

    private static object? Substring(FunctionCall call)
    {
        if (call.Text(0) is not { } text)
        {
            return null;
        }

        var start = call.Whole(1, "the character to start at, counting from 1", nonNegative: false);
        if (start < 1)
        {
            throw new ExpressionEvaluationException($"{call.Arguments[1].Written} is {start}, and substring counts characters from 1");
        }

        var length = new StringInfo(text).LengthInTextElements;
        var count = call.Has(2) ? call.Whole(2, "a count of characters, 0 or more", nonNegative: true) : length;
        return Characters(text, start - 1, count);
    }

    private static object? Round(FunctionCall call)
    {
        if (ExpressionValues.IsEmpty(call[0]))
        {
            return null;
        }

        var number = ExpressionValues.Number(call[0], call.Arguments[0], "round takes a number");
        var digits = call.Has(1) ? call.Whole(1, "a count of decimals from 0 to 15", nonNegative: true) : 0;
        if (digits > 15)
        {
            throw new ExpressionEvaluationException($"{call.Arguments[1].Written} is {digits}, and round keeps 0 to 15 decimals");
        }

        return number is decimal exact
            ? Math.Round(exact, digits, MidpointRounding.AwayFromZero)
            : Math.Round((double)number, digits, MidpointRounding.AwayFromZero);
    }

    /// <summary>Up to <paramref name="count"/> characters of the text from <paramref name="start"/>, counting characters as a person reads them.</summary>
    private static string Characters(string text, int start, int count)
    {
        var info = new StringInfo(text);
        var length = info.LengthInTextElements;
        if (start >= length || count == 0)
        {
            return string.Empty;
        }

        return info.SubstringByTextElements(start, Math.Min(count, length - start));
    }

    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2)
        {
            return int.MaxValue;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
