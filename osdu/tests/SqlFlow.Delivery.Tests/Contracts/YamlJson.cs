using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Reads a YAML document into the JSON tree an OpenAPI contract is: mappings become objects, sequences arrays, and a
/// plain scalar takes the type YAML 1.2's core schema gives it (null, a boolean, an integer or a number), so an
/// <c>enum</c>, a <c>minimum</c> or a <c>required: true</c> reads as the value it is. Quoted scalars stay text.
/// </summary>
internal static partial class YamlJson
{
    public static JsonNode? Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode);
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode mapping => Object(mapping),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(Convert).ToArray()),
        YamlScalarNode scalar => Scalar(scalar),
        _ => throw new InvalidOperationException($"A YAML node of type {node.NodeType} at {node.Start} has no JSON form."),
    };

    private static JsonObject Object(YamlMappingNode mapping)
    {
        var result = new JsonObject();
        foreach (var (key, value) in mapping.Children)
        {
            var name = key is YamlScalarNode scalar
                ? scalar.Value ?? string.Empty
                : throw new InvalidOperationException($"A YAML mapping key at {key.Start} is not a scalar, so it cannot be a JSON property name.");
            result[name] = Convert(value);
        }

        return result;
    }

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;
        if (scalar.Style != YamlDotNet.Core.ScalarStyle.Plain)
        {
            return JsonValue.Create(value);
        }

        if (value.Length == 0 || value is "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        if (value is "true" or "True" or "TRUE")
        {
            return JsonValue.Create(true);
        }

        if (value is "false" or "False" or "FALSE")
        {
            return JsonValue.Create(false);
        }

        if (IntegerText().IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return JsonValue.Create(integer);
        }

        if (NumberText().IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(value);
    }

    [GeneratedRegex(@"^[-+]?[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerText();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberText();
}
