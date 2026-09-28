using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a mapping document says about how records render, as a hash: the document read as YAML, without its comments,
/// its layout and its fixtures, written as canonical JSON. An edit to the record tree, a lookup, a parameter's default
/// or the template it fills moves it; a comment, a reordered key or a fixture's expected record does not, so only an
/// edit that can change a record re-renders the scope.
/// </summary>
public static class MappingFingerprint
{
    /// <summary>The top-level keys that check the mapping rather than say how it renders.</summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal) { "fixtures", "fixtureDefaults" };

    public static string Of(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: {ex.Message}");
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return Hashing.ContentHash.Of(string.Empty);
        }

        return Hashing.ContentHash.Of(CanonicalJson.ToString(Object(root, Excluded)));
    }

    private static JsonNode? Node(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Value is null ? null : JsonValue.Create(scalar.Value),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(Node).ToArray()),
        YamlMappingNode mapping => Object(mapping, null),
        _ => JsonValue.Create(node.ToString()),
    };

    private static JsonObject Object(YamlMappingNode mapping, HashSet<string>? excluded)
    {
        var node = new JsonObject();
        foreach (var (key, value) in mapping.Children)
        {
            var name = key is YamlScalarNode scalar ? scalar.Value ?? string.Empty : key.ToString();
            if (excluded is null || !excluded.Contains(name))
            {
                node[name] = Node(value);
            }
        }

        return node;
    }
}
