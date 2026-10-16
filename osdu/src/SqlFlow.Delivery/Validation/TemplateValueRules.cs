using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// schemas state (draft-07) on the property the value lands on and on everything inside it, checked by the one walk every
/// validation shares (<see cref="SchemaWalk"/>, over the template's compiled rules, <see cref="SchemaRules"/>). The
/// renderer already converts a value to the type its variable takes and checks the ids it builds; these rules are the
/// rest of what a schema says, so a value the record would carry to OSDU without meeting its schema is found before it is
/// sent. Nothing here changes what a delivery sends: a check reports what it finds.
/// </summary>
/// <remarks>
/// A <c>oneOf</c> is read as an <c>anyOf</c>: a value matching one of its forms passes, since OSDU's forms overlap in ways a
/// storage write never objects to. A format the rules do not know is not asserted, as JSON Schema lets a validator do. The
/// walk is bounded: <see cref="MaxProblems"/> problems a value, <see cref="MaxItems"/> items a list and
/// <see cref="MaxDepth"/> levels down, so a value of any size is checked in bounded time. A whole record is checked by
/// <see cref="RecordValidator"/>, which counts what it could not check instead of passing over it.
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
        if (value is null || SchemaRules.Of(schema).At(target.SchemaPath) is not { } rules)
        {
            return [];
        }

        var found = SchemaWalk.Run(value, rules, new SchemaWalk.Place(target.Text, string.Empty), WalkLimits.ForValue);
        return found.Problems.Select(p => new ValueProblem(p.At, p.Rule, p.Message, p.Value)).ToList();
    }

    /// <summary>Whether <paramref name="format"/> is one the rules assert (<see cref="FormatProblem"/>).</summary>
    public static bool AssertsFormat(string format)
        => format is "date-time" or "date" or "time" or "uri" or "uri-reference" or "email" or "uuid" or "ipv4" or "ipv6";

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

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+\-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimeShape();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateShape();

    [GeneratedRegex(@"^(?<clock>\d{2}:\d{2}:\d{2})(\.\d+)?([Zz]|[+\-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeShape();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailShape();
}
