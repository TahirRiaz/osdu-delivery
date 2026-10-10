using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>A number or an instant an aggregate comes to, compared with what an assertion expects.</summary>
public readonly record struct Measured(double? Number, DateTimeOffset? Instant)
{
    public static Measured Of(double number) => new(number, null);

    public static Measured Of(DateTimeOffset instant) => new(null, instant);

    public override string ToString() => Number is { } n
        ? RecordValues.Format(n)
        : Instant?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture) ?? "nothing";
}

/// <summary>
/// How a value a record holds is read and compared (docs/assertions-design.md section 4): numbers as numbers, within a
/// tolerance when one is given; text that is an ISO 8601 date and time as the instant it names; other text ordinally, or
/// ignoring case; true and false as booleans. A value of another type than the one compared with is not equal to it, and
/// cannot be ordered against it: the reason says which types met.
/// </summary>
public static partial class RecordValues
{
    /// <summary>Every value <paramref name="path"/> reaches in <paramref name="record"/>, arrays crossed implicitly; nulls are no value.</summary>
    public static IReadOnlyList<JsonNode> Select(JsonNode record, string path) => JsonPathReader.SelectNodes(record, path);

    /// <summary>A value as text: a string as it is, a number in invariant form, true or false, and anything else as JSON.</summary>
    public static string Text(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => node.ToJsonString(),
            _ => CanonicalJson.ToString(node),
        };
    }

    /// <summary>The values a record yields as an example quotes them: one as it is, several as a list.</summary>
    public static string Describe(IReadOnlyList<JsonNode> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Count switch
        {
            0 => "(no value)",
            1 => TestResults.Quote(Text(values[0])),
            _ => TestResults.Quote("[" + string.Join(", ", values.Take(20).Select(Text)) + (values.Count > 20 ? $", ... {values.Count - 20} more" : string.Empty) + "]"),
        };
    }

    /// <summary>A number as reports write it: invariant, as short as it stays exact.</summary>
    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// A JSON number as a double, whatever holds it: a number read from a response (a JSON element) or one a check made
    /// (an int, a long, a decimal), read from the JSON text it writes, so every kind of number is read the same way.
    /// </summary>
    public static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue && node.GetValueKind() == JsonValueKind.Number
            && double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    public static bool TryText(JsonNode? node, out string text)
    {
        text = string.Empty;
        if (node is JsonValue v && node.GetValueKind() == JsonValueKind.String && v.TryGetValue<string>(out var s))
        {
            text = s;
            return true;
        }

        return false;
    }

    /// <summary>Whether text is an ISO 8601 date (and time), and the instant it names; a date without a zone is taken as UTC.</summary>
    public static bool TryInstant(string text, out DateTimeOffset instant)
    {
        instant = default;
        return IsoDate().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out instant);
    }

    /// <summary>Whether text is a number written in invariant form.</summary>
    public static bool TryParseNumber(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>A value's JSON type, by JSON Schema's word for it.</summary>
    public static string TypeOf(JsonNode? node) => node is null
        ? "null"
        : node.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            _ => "null",
        };

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();
}

/// <summary>The comparisons an assertion makes, each giving a verdict and, when it fails, a reason a reader can act on.</summary>
public static class ValueComparer
{
    /// <summary>
    /// Whether one value a record holds meets <paramref name="condition"/>. Exists, empty and resolves are the caller's to
    /// decide, since they depend on whether there is a value at all, or on the platform.
    /// </summary>
    public static bool Holds(ValueCondition condition, JsonNode value, out string reason)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(value);
        reason = string.Empty;
        switch (condition.Operator)
        {
            case ValueOperator.EqualTo:
                return Verdict(Equal(value, condition.Operands[0], condition, out var why), $"is {Shown(value)}, not {condition.Operands[0]}" + why, out reason);
            case ValueOperator.NotEqualTo:
                return Verdict(!Equal(value, condition.Operands[0], condition, out _), $"is {condition.Operands[0]}", out reason);
            case ValueOperator.In:
                return Verdict(condition.Operands.Any(o => Equal(value, o, condition, out _)), $"is {Shown(value)}, which is not one of the values allowed", out reason);
            case ValueOperator.NotIn:
                var hit = condition.Operands.FirstOrDefault(o => Equal(value, o, condition, out _));
                return Verdict(hit is null, $"is {hit}, one of the values not allowed", out reason);
            case ValueOperator.AtLeast or ValueOperator.AtMost or ValueOperator.GreaterThan or ValueOperator.LessThan:
                var order = Compare(value, condition.Operands[0], condition.Tolerance, out var incomparable);
                if (order is not { } sign)
                {
                    reason = incomparable;
                    return false;
                }

                var holds = condition.Operator switch
                {
                    ValueOperator.AtLeast => sign >= 0,
                    ValueOperator.AtMost => sign <= 0,
                    ValueOperator.GreaterThan => sign > 0,
                    _ => sign < 0,
                };
                return Verdict(holds, $"is {Shown(value)}", out reason);
            case ValueOperator.Between:
                var low = Compare(value, condition.Operands[0], condition.Tolerance, out var lowWhy);
                var high = Compare(value, condition.Operands[1], condition.Tolerance, out var highWhy);
                if (low is null || high is null)
                {
                    reason = low is null ? lowWhy : highWhy;
                    return false;
                }

                return Verdict(low >= 0 && high <= 0, $"is {Shown(value)}, outside {condition.Operands[0]} to {condition.Operands[1]}", out reason);
            case ValueOperator.Matches or ValueOperator.NotMatches:
                var matched = Match(condition.Pattern!, value, out var timedOut);
                if (timedOut)
                {
                    reason = $"took longer than {Documents.AssertionMapper.PatternTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s to match; the expression backtracks too much for a value this long";
                    return false;
                }

                return condition.Operator == ValueOperator.Matches
                    ? Verdict(matched, $"is {Shown(value)}, which does not match", out reason)
                    : Verdict(!matched, $"is {Shown(value)}, which matches", out reason);
            case ValueOperator.StartsWith or ValueOperator.EndsWith:
                if (!RecordValues.TryText(value, out var text))
                {
                    reason = $"is a {RecordValues.TypeOf(value)}, not text";
                    return false;
                }

                var comparison = condition.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var affix = condition.Operands[0].Text;
                return condition.Operator == ValueOperator.StartsWith
                    ? Verdict(text.StartsWith(affix, comparison), $"is {Shown(value)}", out reason)
                    : Verdict(text.EndsWith(affix, comparison), $"is {Shown(value)}", out reason);
            case ValueOperator.Contains or ValueOperator.NotContains:
                var contains = Contains(value, condition.Operands[0], condition, out var containsWhy);
                if (contains is not { } found)
                {
                    reason = containsWhy;
                    return false;
                }

                return condition.Operator == ValueOperator.Contains
                    ? Verdict(found, $"is {Shown(value)}, which does not contain {condition.Operands[0]}", out reason)
                    : Verdict(!found, $"is {Shown(value)}, which contains {condition.Operands[0]}", out reason);
            case ValueOperator.Empty:
                var empty = IsEmpty(value);
                return Verdict(empty == condition.Flag, empty ? "is empty" : $"is {Shown(value)}", out reason);
            case ValueOperator.Type:
                return Verdict(IsType(value, condition.JsonType!.Value), $"is a {RecordValues.TypeOf(value)}: {Shown(value)}", out reason);
            case ValueOperator.Length:
                var length = value.GetValueKind() switch
                {
                    JsonValueKind.String => (long?)value.GetValue<string>().Length,
                    JsonValueKind.Array => value.AsArray().Count,
                    _ => null,
                };
                if (length is not { } n)
                {
                    reason = $"is a {RecordValues.TypeOf(value)}, which has no length";
                    return false;
                }

                return Verdict(Evaluate(condition.Length!, Measured.Of(n), null), $"has length {n}", out reason);
            case ValueOperator.Exists:
                return Verdict(condition.Flag, $"is {Shown(value)}", out reason);
            default:
                throw new InvalidOperationException($"'{AssertionText.Of(condition.Operator)}' is decided by the assertion, not by one value.");
        }
    }

    /// <summary>
    /// Whether the values a <c>where</c> condition reads select what it is written for (a record, a row, an item): <c>exists</c>
    /// asks whether there is a value at all, <c>empty</c> holds of no value as of an empty one, and any other condition holds
    /// when one of the values meets it. An assertion flow's conditions and a mapping's assertions select alike.
    /// </summary>
    public static bool Selects(ValueCondition condition, IReadOnlyList<JsonNode> found)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(found);
        return condition.Operator switch
        {
            ValueOperator.Exists => condition.Flag == found.Count > 0,
            ValueOperator.Empty when found.Count == 0 => condition.Flag,
            _ => found.Any(v => Holds(condition, v, out _)),
        };
    }

    /// <summary>Whether a value is empty: null, empty text, an empty list or an empty object.</summary>
    public static bool IsEmpty(JsonNode? value) => value is null || value.GetValueKind() switch
    {
        JsonValueKind.Null => true,
        JsonValueKind.String => value.GetValue<string>().Length == 0,
        JsonValueKind.Array => value.AsArray().Count == 0,
        JsonValueKind.Object => value.AsObject().Count == 0,
        _ => false,
    };

    /// <summary>Whether a value is of a JSON type; an integer is a number without a fractional part.</summary>
    public static bool IsType(JsonNode value, JsonValueType type) => type switch
    {
        JsonValueType.Text => value.GetValueKind() == JsonValueKind.String,
        JsonValueType.Number => value.GetValueKind() == JsonValueKind.Number,
        JsonValueType.WholeNumber => RecordValues.TryNumber(value, out var n) && n == Math.Floor(n),
        JsonValueType.Boolean => value.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        JsonValueType.Mapping => value.GetValueKind() == JsonValueKind.Object,
        JsonValueType.Array => value.GetValueKind() == JsonValueKind.Array,
        _ => value.GetValueKind() == JsonValueKind.Null,
    };

    /// <summary>
    /// Whether a value equals what is expected: numbers as numbers (within the tolerance, else exactly), text as text
    /// (ignoring case when asked, and as instants when both are ISO 8601 dates), booleans as booleans. Text holding a number
    /// equals that number, and text holding true or false that boolean, since OSDU keeps some numbers as text.
    /// </summary>
    public static bool Equal(JsonNode? actual, ExpectedValue expected, ValueCondition? condition, out string why)
    {
        ArgumentNullException.ThrowIfNull(expected);
        why = string.Empty;
        var tolerance = condition?.Tolerance;
        var ignoreCase = condition?.IgnoreCase ?? false;
        if (expected.Kind == ExpectedValueKind.Null)
        {
            return actual is null || actual.GetValueKind() == JsonValueKind.Null;
        }

        if (actual is null)
        {
            return false;
        }

        switch (actual.GetValueKind())
        {
            case JsonValueKind.Number when expected.Kind == ExpectedValueKind.Number:
                return NumbersEqual(actual, expected, tolerance);
            case JsonValueKind.Number when expected.Kind == ExpectedValueKind.Text:
                return RecordValues.TryParseNumber(expected.Text, out var e) && RecordValues.TryNumber(actual, out var a) && Near(a, e, tolerance);
            case JsonValueKind.String:
                var text = actual.GetValue<string>();
                if (expected.Kind == ExpectedValueKind.Number)
                {
                    return RecordValues.TryParseNumber(text, out var n) && Near(n, expected.Number!.Value, tolerance);
                }

                if (expected.Kind == ExpectedValueKind.Boolean)
                {
                    return string.Equals(text, expected.Text, StringComparison.OrdinalIgnoreCase);
                }

                if (string.Equals(text, expected.Text, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    return true;
                }

                return RecordValues.TryInstant(text, out var x) && RecordValues.TryInstant(expected.Text, out var y) && x == y;
            case JsonValueKind.True or JsonValueKind.False:
                var flag = actual.GetValueKind() == JsonValueKind.True;
                return expected.Kind == ExpectedValueKind.Boolean
                    ? expected.Boolean == flag
                    : expected.Kind == ExpectedValueKind.Text && string.Equals(expected.Text, flag ? "true" : "false", StringComparison.OrdinalIgnoreCase);
            case JsonValueKind.Array:
                why = "; the value is a list, so compare its items (a path ending in [*]) or use contains";
                return false;
            case JsonValueKind.Object:
                why = "; the value is an object, so compare a property inside it";
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// How a value orders against what is expected: negative below, zero equal (within the tolerance), positive above; null
    /// when the two cannot be ordered, with the reason. Numbers order as numbers, ISO 8601 dates as instants, other text ordinally.
    /// </summary>
    public static int? Compare(JsonNode actual, ExpectedValue expected, double? tolerance, out string why)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);
        why = string.Empty;
        if (expected.Kind == ExpectedValueKind.Number)
        {
            var number = RecordValues.TryNumber(actual, out var a)
                ? a
                : RecordValues.TryText(actual, out var t) && RecordValues.TryParseNumber(t, out var parsed) ? parsed : (double?)null;
            if (number is not { } value)
            {
                why = $"is a {RecordValues.TypeOf(actual)} ({Shown(actual)}), which does not order against the number {expected}";
                return null;
            }

            return Near(value, expected.Number!.Value, tolerance) ? 0 : value.CompareTo(expected.Number!.Value);
        }

        if (expected.Kind == ExpectedValueKind.Text)
        {
            if (RecordValues.TryText(actual, out var text))
            {
                if (RecordValues.TryInstant(text, out var x) && RecordValues.TryInstant(expected.Text, out var y))
                {
                    return x.CompareTo(y);
                }

                return string.CompareOrdinal(text, expected.Text) switch { < 0 => -1, > 0 => 1, _ => 0 };
            }

            if (RecordValues.TryNumber(actual, out var a) && RecordValues.TryParseNumber(expected.Text, out var e))
            {
                return Near(a, e, tolerance) ? 0 : a.CompareTo(e);
            }

            why = $"is a {RecordValues.TypeOf(actual)} ({Shown(actual)}), which does not order against the text {expected}";
            return null;
        }

        why = $"cannot be ordered against {expected}";
        return null;
    }

    /// <summary>Whether a measured number or instant meets every term of a comparison.</summary>
    public static bool Evaluate(Comparison comparison, Measured actual, double? tolerance)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        foreach (var term in comparison.Terms)
        {
            var low = Order(actual, term.Value, tolerance);
            if (low is not { } sign)
            {
                return false;
            }

            var holds = term.Operator switch
            {
                ComparisonOperator.EqualTo => sign == 0,
                ComparisonOperator.NotEqualTo => sign != 0,
                ComparisonOperator.AtLeast => sign >= 0,
                ComparisonOperator.AtMost => sign <= 0,
                ComparisonOperator.GreaterThan => sign > 0,
                ComparisonOperator.LessThan => sign < 0,
                _ => sign >= 0 && Order(actual, term.Upper!, tolerance) is <= 0,
            };
            if (!holds)
            {
                return false;
            }
        }

        return true;
    }

    private static int? Order(Measured actual, ExpectedValue expected, double? tolerance)
    {
        if (actual.Number is { } number && expected.Number is { } value)
        {
            return Near(number, value, tolerance) ? 0 : number.CompareTo(value);
        }

        if (actual.Instant is { } instant && expected.Kind == ExpectedValueKind.Text && RecordValues.TryInstant(expected.Text, out var other))
        {
            return instant.CompareTo(other);
        }

        return null;
    }

    private static bool NumbersEqual(JsonNode actual, ExpectedValue expected, double? tolerance)
    {
        if (tolerance is null
            && decimal.TryParse(actual.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
            && decimal.TryParse(expected.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var wanted))
        {
            return exact == wanted;
        }

        return RecordValues.TryNumber(actual, out var a) && Near(a, expected.Number!.Value, tolerance);
    }

    private static bool Near(double a, double b, double? tolerance)
        => tolerance is { } t ? Math.Abs(a - b) <= t : a.Equals(b);

    private static bool? Contains(JsonNode value, ExpectedValue expected, ValueCondition condition, out string why)
    {
        why = string.Empty;
        switch (value.GetValueKind())
        {
            case JsonValueKind.Array:
                return value.AsArray().Any(item => Equal(item, expected, condition, out _));
            case JsonValueKind.String when expected.Kind == ExpectedValueKind.Text:
                return value.GetValue<string>().Contains(expected.Text, condition.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            default:
                why = $"is a {RecordValues.TypeOf(value)} ({Shown(value)}); contains looks in a list, or for text in text";
                return null;
        }
    }

    private static bool Match(Regex pattern, JsonNode value, out bool timedOut)
    {
        timedOut = false;
        try
        {
            return pattern.IsMatch(RecordValues.Text(value));
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
            return false;
        }
    }

    private static bool Verdict(bool holds, string failure, out string reason)
    {
        reason = holds ? string.Empty : failure;
        return holds;
    }

    private static string Shown(JsonNode value) => TestResults.Quote(RecordValues.Text(value));
}
