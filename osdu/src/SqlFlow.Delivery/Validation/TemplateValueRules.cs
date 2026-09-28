using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Validation;

/// <summary>One way a value written to a variable breaks what the template says of that variable.</summary>
/// <param name="At">
/// The variable the problem is at, as a template path: the variable written, or a property inside the value written there
/// (<c>osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID</c> for a list of objects written whole).
/// </param>
/// <param name="Rule">
/// The schema rule the value breaks: <c>type</c>, <c>format</c>, <c>pattern</c>, <c>enum</c>, <c>const</c>, <c>minLength</c>,
/// <c>maxLength</c>, <c>minimum</c>, <c>maximum</c>, <c>exclusiveMinimum</c>, <c>exclusiveMaximum</c>, <c>multipleOf</c>,
/// <c>minItems</c>, <c>maxItems</c>, <c>uniqueItems</c>, <c>required</c>, <c>additionalProperties</c>, <c>relationship</c>
/// (<c>x-osdu-relationship</c>) or <c>anyOf</c> (a value matching none of the forms a <c>oneOf</c> or <c>anyOf</c> allows).
/// </param>
/// <param name="Message">What is wrong, naming the value and, inside a list or an object, where in it.</param>
/// <param name="Value">The offending value as text, clipped to a readable length.</param>
public sealed record ValueProblem(string At, string Rule, string Message, string Value);

/// <summary>
/// What the template says a value of a variable must be, applied to a value a render wrote: the JSON Schema rules OSDU's
/// schemas state (draft-07) on the property the value lands on and on everything inside it. The renderer already converts
/// a value to the type its variable takes and checks the ids it builds; these rules are the rest of what a schema says, so
/// a value the record would carry to OSDU without meeting its schema is found before it is sent. Nothing here changes what
/// a delivery sends: a check reports what it finds.
/// </summary>
/// <remarks>
/// A <c>oneOf</c> is read as an <c>anyOf</c>: a value matching one of its forms passes, since OSDU's forms overlap in ways a
/// storage write never objects to. A format the rules do not know is not asserted, as JSON Schema lets a validator do. The
/// walk is bounded: <see cref="MaxProblems"/> problems a value, <see cref="MaxItems"/> items a list and
/// <see cref="MaxDepth"/> levels down, so a value of any size is checked in bounded time.
/// </remarks>
public static partial class TemplateValueRules
{
    /// <summary>The most problems reported for one value.</summary>
    public const int MaxProblems = 10;

    /// <summary>The most items of one list checked.</summary>
    public const int MaxItems = 1000;

    /// <summary>The deepest level of a value checked.</summary>
    public const int MaxDepth = 24;

    /// <summary>The longest text a problem quotes a value with.</summary>
    public const int MaxQuoted = 200;

    /// <summary>
    /// The problems <paramref name="value"/> has as the value of <paramref name="target"/> in <paramref name="schema"/>;
    /// empty when it meets every rule, and when the template does not describe the variable (a free key of an object that
    /// takes any value).
    /// </summary>
    public static IReadOnlyList<ValueProblem> Check(JsonNode? value, TemplatePath target, SchemaSnapshot schema)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(schema);
        if (value is null || schema.Resolve(target.SchemaPath) is not { } property)
        {
            return [];
        }

        var problems = new List<ValueProblem>();
        new Walk(schema, problems).Node(value, property.Schema, new Place(target.Text, string.Empty), 0);
        return problems;
    }

    /// <summary>
    /// Where a walk is: the variable path problems are reported at, and the position inside the value written there
    /// (<c>[2].Name</c>), which a message names when it is not the value itself.
    /// </summary>
    private readonly record struct Place(string Variable, string Inside)
    {
        public string Prefix => Inside.Length == 0 ? string.Empty : $"at {Inside}: ";

        /// <summary>The place of property <paramref name="name"/> of an object here; an item of a list of objects names its variable with []. </summary>
        public Place Property(string name, bool inItem)
            => new(inItem ? $"{Variable}[].{name}" : $"{Variable}.{name}", $"{Inside}.{name}".TrimStart('.'));

        public Place Item(int index) => this with { Inside = string.Create(CultureInfo.InvariantCulture, $"{Inside}[{index}]") };
    }

    private sealed class Walk(SchemaSnapshot schema, List<ValueProblem> problems)
    {
        public void Node(JsonNode value, JsonObject raw, Place place, int depth, bool inItem = false)
        {
            if (depth > MaxDepth || problems.Count >= MaxProblems)
            {
                return;
            }

            var rules = schema.EffectiveOf(raw);
            if (!Forms(value, rules, place, depth, inItem))
            {
                return;
            }

            var types = Types(rules);
            if (types.Count > 0 && !types.Any(type => IsType(value, type)))
            {
                Add(place, "type", $"{Describe(value)} where the template takes {string.Join(" or ", types.Select(Article))}", value);
                return;
            }

            if (rules["const"] is { } constant && !JsonNode.DeepEquals(constant, value))
            {
                Add(place, "const", $"{Describe(value)} is not {Quote(constant)}, the one value the template allows", value);
            }

            if (rules["enum"] is JsonArray allowed && !allowed.Any(option => JsonNode.DeepEquals(option, value)))
            {
                var listed = string.Join(", ", allowed.Take(8).Select(Quote)) + (allowed.Count > 8 ? string.Create(CultureInfo.InvariantCulture, $" and {allowed.Count - 8} more") : string.Empty);
                Add(place, "enum", $"{Describe(value)} is not one of the {allowed.Count} values the template allows ({listed})", value);
            }

            switch (value)
            {
                case JsonObject obj:
                    Object(obj, rules, place, depth, inItem);
                    break;
                case JsonArray list:
                    List(list, rules, place, depth);
                    break;
                case JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String:
                    Text(scalar.GetValue<string>(), rules, place, value);
                    break;
                case JsonValue scalar when scalar.GetValueKind() == JsonValueKind.Number:
                    Number(scalar, rules, types, place);
                    break;
            }
        }

        /// <summary>
        /// Whether the value matches one of the forms a <c>oneOf</c> or an <c>anyOf</c> allows; true when the node lists none.
        /// A value matching none is a problem of its own, naming what the first form found wrong with it.
        /// </summary>
        private bool Forms(JsonNode value, JsonObject rules, Place place, int depth, bool inItem)
        {
            foreach (var keyword in (ReadOnlySpan<string>)["oneOf", "anyOf"])
            {
                if (rules[keyword] is not JsonArray forms || forms.Count == 0)
                {
                    continue;
                }

                List<ValueProblem>? first = null;
                var matched = false;
                foreach (var form in forms.OfType<JsonObject>())
                {
                    var trial = new List<ValueProblem>();
                    new Walk(schema, trial).Node(value, form, place, depth + 1, inItem);
                    if (trial.Count == 0)
                    {
                        matched = true;
                        break;
                    }

                    first ??= trial;
                }

                if (!matched)
                {
                    var why = first is { Count: > 0 } ? $": {first[0].Message}" : string.Empty;
                    Add(place, "anyOf", string.Create(CultureInfo.InvariantCulture, $"{Describe(value)} matches none of the {forms.Count} forms the template allows{why}"), value);
                    return false;
                }
            }

            return true;
        }

        private void Object(JsonObject obj, JsonObject rules, Place place, int depth, bool inItem)
        {
            if (rules["required"] is JsonArray required)
            {
                foreach (var name in required.OfType<JsonValue>().Select(n => n.TryGetValue<string>(out var text) ? text : null).OfType<string>())
                {
                    if (obj[name] is null)
                    {
                        var at = place.Property(name, inItem);
                        Add(at, "required", $"{place.Prefix}the value has no {name}, which the template requires", obj);
                    }
                }
            }

            var properties = rules["properties"] as JsonObject;
            foreach (var (name, child) in obj)
            {
                if (child is null)
                {
                    continue;
                }

                var at = place.Property(name, inItem);
                if (properties?[name] is JsonObject declared)
                {
                    Node(child, declared, at, depth + 1);
                }
                else if (rules["additionalProperties"] is JsonObject free)
                {
                    Node(child, free, at, depth + 1);
                }
                else if (rules["additionalProperties"] is JsonValue open && open.TryGetValue<bool>(out var allowed) && !allowed)
                {
                    Add(at, "additionalProperties", $"{place.Prefix}the value has {name}, which the template does not describe and allows no other property", child);
                }
            }
        }

        private void List(JsonArray list, JsonObject rules, Place place, int depth)
        {
            if (Whole(rules, "minItems") is { } fewest && list.Count < fewest)
            {
                Add(place, "minItems", string.Create(CultureInfo.InvariantCulture, $"{place.Prefix}the list holds {list.Count} item(s), fewer than the {fewest} the template requires"), list);
            }

            if (Whole(rules, "maxItems") is { } most && list.Count > most)
            {
                Add(place, "maxItems", string.Create(CultureInfo.InvariantCulture, $"{place.Prefix}the list holds {list.Count} items, more than the {most} the template allows"), list);
            }

            if (rules["uniqueItems"] is JsonValue unique && unique.TryGetValue<bool>(out var distinct) && distinct)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in list.Take(MaxItems))
                {
                    if (!seen.Add(CanonicalJson.ToString(item)))
                    {
                        Add(place, "uniqueItems", $"{place.Prefix}{Quote(item)} is in the list more than once, and the template takes each item once", item);
                        break;
                    }
                }
            }

            if (rules["items"] is not JsonObject items)
            {
                return;
            }

            var index = 0;
            foreach (var item in list.Take(MaxItems))
            {
                if (item is not null)
                {
                    Node(item, items, place.Item(index), depth + 1, inItem: true);
                }

                index++;
            }
        }

        private void Text(string text, JsonObject rules, Place place, JsonNode value)
        {
            var relationships = OsduTemplate.Relationships(rules, rules);
            var related = false;
            if (relationships.Count > 0)
            {
                related = true;
                if (IdValues.EntityType(text.Trim()) is not { } entityType)
                {
                    Add(place, "relationship", $"{place.Prefix}{Quote(value)} is not an OSDU id, and the template points the variable to {string.Join(" or ", relationships)}", value);
                    return;
                }

                if (!IdValues.Allows(relationships, entityType))
                {
                    Add(place, "relationship", $"{place.Prefix}{Quote(value)} is the id of a {entityType} record, and the template points the variable to {string.Join(" or ", relationships)}", value);
                    return;
                }
            }

            // A pattern neither regular expression dialect reads cannot be applied; the preflight names it, so nothing is claimed of it here.
            if (rules["pattern"] is JsonValue patternNode && patternNode.TryGetValue<string>(out var pattern) && IdValues.Pattern(pattern) is { } regex)
            {
                try
                {
                    if (!regex.IsMatch(text))
                    {
                        Add(place, "pattern", related
                            ? $"{place.Prefix}{Quote(value)} does not match the pattern of the ids the template takes, {pattern}"
                            : $"{place.Prefix}{Quote(value)} does not match the pattern the template gives, {pattern}", value);
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    Add(place, "pattern", $"{place.Prefix}{Quote(value)} could not be checked against the pattern {pattern} within the time a check allows", value);
                }
            }

            var length = text.EnumerateRunes().Count();
            if (Whole(rules, "minLength") is { } shortest && length < shortest)
            {
                Add(place, "minLength", string.Create(CultureInfo.InvariantCulture, $"{place.Prefix}{Quote(value)} is {length} character(s) long, shorter than the {shortest} the template requires"), value);
            }

            if (Whole(rules, "maxLength") is { } longest && length > longest)
            {
                Add(place, "maxLength", string.Create(CultureInfo.InvariantCulture, $"{place.Prefix}the text is {length} characters long, longer than the {longest} the template allows"), value);
            }

            if (rules["format"] is JsonValue formatNode && formatNode.TryGetValue<string>(out var format) && FormatProblem(text, format) is { } wrong)
            {
                Add(place, "format", $"{place.Prefix}{Quote(value)} {wrong}", value);
            }
        }

        private void Number(JsonValue scalar, JsonObject rules, IReadOnlyList<string> types, Place place)
        {
            var text = scalar.ToJsonString();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return;
            }

            var exact = decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var precise) ? precise : (decimal?)null;
            if (Bound(rules, "minimum") is { } minimum && number < minimum)
            {
                Add(place, "minimum", $"{place.Prefix}{text} is below the minimum of {Format(minimum)} the template gives", scalar);
            }

            if (Bound(rules, "maximum") is { } maximum && number > maximum)
            {
                Add(place, "maximum", $"{place.Prefix}{text} is above the maximum of {Format(maximum)} the template gives", scalar);
            }

            if (Bound(rules, "exclusiveMinimum") is { } above && number <= above)
            {
                Add(place, "exclusiveMinimum", $"{place.Prefix}{text} is not above {Format(above)}, which the template requires", scalar);
            }

            if (Bound(rules, "exclusiveMaximum") is { } below && number >= below)
            {
                Add(place, "exclusiveMaximum", $"{place.Prefix}{text} is not below {Format(below)}, which the template requires", scalar);
            }

            if (rules["multipleOf"] is JsonValue stepNode
                && decimal.TryParse(stepNode.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var step) && step > 0
                && exact is { } value && decimal.Remainder(value, step) != 0)
            {
                Add(place, "multipleOf", $"{place.Prefix}{text} is not a multiple of {stepNode.ToJsonString()}, which the template requires", scalar);
            }

            if (types.Contains("integer") && rules["format"] is JsonValue formatNode && formatNode.TryGetValue<string>(out var format))
            {
                var (low, high) = format switch
                {
                    "int32" => (int.MinValue, (double)int.MaxValue),
                    "int64" => ((double)long.MinValue, (double)long.MaxValue),
                    _ => (double.NegativeInfinity, double.PositiveInfinity),
                };
                if (number < low || number > high)
                {
                    Add(place, "format", $"{place.Prefix}{text} is outside the range of an {format} integer, which the template takes", scalar);
                }
            }
        }

        private void Add(Place place, string rule, string message, JsonNode? value)
        {
            if (problems.Count < MaxProblems)
            {
                problems.Add(new ValueProblem(place.Variable, rule, message, Clip(value)));
            }
        }
    }

    /// <summary>
    /// Why <paramref name="text"/> is not of <paramref name="format"/>, or null when it is, or when the format is not one
    /// the rules assert: RFC 3339 <c>date-time</c>, <c>date</c> and <c>time</c>, and <c>uri</c>, <c>uri-reference</c>,
    /// <c>email</c>, <c>uuid</c>, <c>ipv4</c> and <c>ipv6</c>.
    /// </summary>
    public static string? FormatProblem(string text, string format)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(format);
        switch (format)
        {
            case "date-time":
                return DateTimeShape().IsMatch(text)
                    && DateTimeOffset.TryParse(text.ToUpperInvariant(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                    ? null
                    : "is not an RFC 3339 date-time, such as 2026-09-01T10:15:30Z; give the date modifier the form it is written in";
            case "date":
                return DateShape().IsMatch(text) && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    ? null
                    : "is not an RFC 3339 date, such as 2026-09-01; give the date modifier the form it is written in";
            case "time":
                return TimeShape().Match(text) is { Success: true } time && TimeOnly.TryParseExact(time.Groups["clock"].Value, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    ? null
                    : "is not an RFC 3339 time, such as 10:15:30Z";
            case "uri":
                return Uri.TryCreate(text, UriKind.Absolute, out _) ? null : "is not an absolute URI";
            case "uri-reference":
                return Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out _) ? null : "is not a URI reference";
            case "email":
                return EmailShape().IsMatch(text) ? null : "is not an email address";
            case "uuid":
                return Guid.TryParseExact(text, "D", out _) ? null : "is not a UUID, such as 0f8fad5b-d9cb-469f-a165-70867728950e";
            case "ipv4":
                return IPAddress.TryParse(text, out var v4) && v4.AddressFamily == AddressFamily.InterNetwork && text.Count(c => c == '.') == 3 ? null : "is not an IPv4 address";
            case "ipv6":
                return IPAddress.TryParse(text, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6 ? null : "is not an IPv6 address";
            default:
                return null;
        }
    }

    /// <summary>The JSON types a schema node allows, less <c>null</c>, which a render never writes; empty allows any.</summary>
    private static List<string> Types(JsonObject rules) => rules["type"] switch
    {
        JsonValue single when single.TryGetValue<string>(out var type) && type != "null" => [type],
        JsonArray many => many.OfType<JsonValue>().Select(t => t.TryGetValue<string>(out var type) ? type : null).OfType<string>().Where(t => t != "null").Distinct(StringComparer.Ordinal).ToList(),
        _ => [],
    };

    private static bool IsType(JsonNode value, string type) => type switch
    {
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && IsWhole(v.ToJsonString()),
        _ => true,
    };

    private static bool IsWhole(string number)
        => decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
            ? decimal.Truncate(exact) == exact
            : double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var wide) && double.IsFinite(wide) && Math.Floor(wide) == wide;

    private static string Article(string type) => type switch
    {
        "object" => "an object",
        "array" => "a list",
        "integer" => "an integer",
        "string" => "text",
        "boolean" => "true or false",
        _ => "a " + type,
    };

    /// <summary>A value as a message names it: its kind, and a scalar's text.</summary>
    private static string Describe(JsonNode value) => value switch
    {
        JsonObject => "an object",
        JsonArray list => string.Create(CultureInfo.InvariantCulture, $"a list of {list.Count}"),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => $"the text {Quote(value)}",
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => $"the number {v.ToJsonString()}",
        _ => value.ToJsonString(),
    };

    /// <summary>A value quoted in a message: text in single quotes, anything else as JSON, clipped.</summary>
    private static string Quote(JsonNode? value)
        => value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? $"'{Clip(value)}'" : Clip(value);

    /// <summary>A value as text, no longer than <see cref="MaxQuoted"/> characters.</summary>
    private static string Clip(JsonNode? value)
    {
        var text = value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : value is null ? "null" : CanonicalJson.ToString(value);
        return text.Length <= MaxQuoted ? text : text[..MaxQuoted] + "...";
    }

    /// <summary>A whole number a keyword gives (a length, a count), or null when it gives none.</summary>
    private static long? Whole(JsonObject rules, string keyword)
        => rules[keyword] is JsonValue node && long.TryParse(node.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) ? whole : null;

    /// <summary>A number a keyword gives (a bound), or null when it gives none; a draft-04 boolean exclusive bound gives none.</summary>
    private static double? Bound(JsonObject rules, string keyword)
        => rules[keyword] is JsonValue node && node.GetValueKind() == JsonValueKind.Number
            && double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bound) ? bound : null;

    private static string Format(double number) => number.ToString("R", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+\-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimeShape();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateShape();

    [GeneratedRegex(@"^(?<clock>\d{2}:\d{2}:\d{2})(\.\d+)?([Zz]|[+\-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeShape();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailShape();
}
