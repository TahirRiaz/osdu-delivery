using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// How an id modifier writes a token's value into an id, and what every id it builds is checked against before it is
/// written: OSDU's id shape, the variable's schema pattern, and the entity types its relationship allows.
/// </summary>
internal static partial class IdValues
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    // One entry per distinct schema pattern: a closed set, since patterns come from the templates a deployment holds.
    private static readonly ConcurrentDictionary<string, Regex?> Patterns = new(StringComparer.Ordinal);

    /// <summary>
    /// A token's value as an id carries it: ASCII letters, digits, '_', '-', '.' and ':' as they stand (':' parts the
    /// code of an id such as a CRS's <c>Projected:EPSG::23031</c>), a percent-escape already there kept, so nothing is
    /// encoded twice, and every other character percent-encoded as UTF-8. Null when the value is not valid Unicode text.
    /// </summary>
    public static string? Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var written = new StringBuilder(value.Length);
        Span<byte> bytes = stackalloc byte[4];
        for (var i = 0; i < value.Length;)
        {
            var c = value[i];
            if (IdTemplate.IsIdCharacter(c))
            {
                written.Append(c);
                i++;
                continue;
            }

            if (c == '%' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                written.Append('%').Append(char.ToUpperInvariant(value[i + 1])).Append(char.ToUpperInvariant(value[i + 2]));
                i += 3;
                continue;
            }

            if (Rune.DecodeFromUtf16(value.AsSpan(i), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                return null;
            }

            var length = rune.EncodeToUtf8(bytes);
            for (var b = 0; b < length; b++)
            {
                written.Append('%').Append(bytes[b].ToString("X2", CultureInfo.InvariantCulture));
            }

            i += consumed;
        }

        return written.ToString();
    }

    /// <summary>The entity type an id names (<c>master-data--Wellbore</c>), or null when it does not have OSDU's id shape.</summary>
    public static string? EntityType(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var match = IdShape().Match(id);
        return match.Success ? match.Groups["entity"].Value : null;
    }

    /// <summary>
    /// Why <paramref name="id"/> cannot be written to <paramref name="property"/>, or null when it can: it has OSDU's id
    /// shape, matches the pattern the schema gives the property (or each item of a list of them), and names an entity type
    /// the property's relationship allows.
    /// </summary>
    public static string? Problem(string id, SchemaProperty? property)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (EntityType(id) is not { } entityType)
        {
            return $"'{id}' is not an OSDU id, <partition>:<group>--<Entity>:<code>, written with ASCII letters, digits, '_', '-', '.', ':' and percent-escapes";
        }

        if (property is null)
        {
            return null;
        }

        var relationships = Relationships(property);
        if (relationships.Count > 0 && !Allows(relationships, entityType))
        {
            return $"'{id}' is the id of a {entityType} record, and the template points the variable to {string.Join(" or ", relationships)}";
        }

        if (PatternText(property) is not { } patternText || Pattern(patternText) is not { } pattern)
        {
            return null;
        }

        try
        {
            return pattern.IsMatch(id) ? null : $"'{id}' does not match the pattern the template gives the variable, {patternText}";
        }
        catch (RegexMatchTimeoutException)
        {
            return $"checking '{id}' against the pattern the template gives the variable, {patternText}, took longer than {PatternTimeout.TotalSeconds:0} second(s)";
        }
    }

    /// <summary>The pattern a text value written to the property (or to each item of a list of them) must match, or null.</summary>
    public static string? PatternText(SchemaProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.Pattern ?? (property.Items?["pattern"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null);
    }

    /// <summary>
    /// The schema pattern as a regular expression, read the way JSON Schema reads it (ECMAScript, where <c>\w</c> is ASCII)
    /// and, when it uses what that dialect lacks, as .NET reads it. Null when neither reads it, so only the id's shape is
    /// checked; the preflight says so.
    /// </summary>
    public static Regex? Pattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return Patterns.GetOrAdd(pattern, static text =>
        {
            try
            {
                return new Regex(text, RegexOptions.ECMAScript, PatternTimeout);
            }
            catch (ArgumentException)
            {
                try
                {
                    return new Regex(text, RegexOptions.CultureInvariant, PatternTimeout);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
        });
    }

    /// <summary>The entity types a property, or each item of a list of them, points to.</summary>
    public static IReadOnlyList<string> Relationships(SchemaProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        var own = OsduTemplate.Relationships(property.Schema, property.Schema);
        return own.Count > 0 || property.Items is null ? own : OsduTemplate.Relationships(property.Items, property.Items);
    }

    /// <summary>Whether an entity type is one a relationship allows; a group type alone (<c>dataset</c>) allows every entity of the group.</summary>
    public static bool Allows(IReadOnlyList<string> relationships, string entityType)
        => relationships.Any(r => string.Equals(r, entityType, StringComparison.Ordinal)
            || (!r.Contains("--", StringComparison.Ordinal) && entityType.StartsWith(r + "--", StringComparison.Ordinal)));

    /// <summary>
    /// The id a template builds with a stand-in for every value a row gives and each parameter's value, for a check before
    /// any row arrives. Null when a parameter has no value.
    /// </summary>
    public static string? Sample(IdTemplate template, Func<string, string?> parameter)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(parameter);
        var written = new StringBuilder();
        foreach (var token in template.Tokens)
        {
            switch (token.Kind)
            {
                case IdTokenKind.Text:
                    written.Append(token.Text);
                    break;
                case IdTokenKind.Parameter:
                    if (parameter(token.Parameter!) is not { } value || string.IsNullOrWhiteSpace(value) || Encode(value.Trim()) is not { } encoded)
                    {
                        return null;
                    }

                    written.Append(encoded);
                    break;
                default:
                    written.Append(SampleValue);
                    break;
            }
        }

        return written.ToString();
    }

    /// <summary>What <see cref="Sample"/> writes for a value a row gives.</summary>
    public const char SampleValue = 'x';

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+:(?<entity>[A-Za-z0-9_\-\.]+--[A-Za-z0-9_\-\.]+):[A-Za-z0-9_\-\.:%]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdShape();
}
