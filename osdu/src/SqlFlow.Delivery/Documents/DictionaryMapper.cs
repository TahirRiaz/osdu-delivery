using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Reads a dictionary document. It is read node by node rather than deserialized into types, because a dictionary's keys
/// and values are text exactly as written: a deserializer that types scalars would read the key <c>NO</c> or <c>true</c> as a
/// boolean and <c>1.10</c> as the number 1.1. Only an unquoted <c>~</c>, <c>null</c> or empty value means no value; a quoted
/// one is the text it spells. Every refusal names the file, and the line where the document is wrong.
/// </summary>
internal static partial class DictionaryMapper
{
    private static readonly string[] Keys = ["documentType", "name", "description", "key", "fields", "entries"];

    public static DictionaryDefinition Map(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var root = Root(yaml, source);
        foreach (var topLevel in root.Children.Keys)
        {
            var written = Text(topLevel, source, "a top-level key");
            if (!Keys.Contains(written, StringComparer.Ordinal))
            {
                throw Error(source, topLevel, $"'{written}' is not a key of a dictionary document; it takes {string.Join(", ", Keys)}.");
            }
        }

        var documentType = Scalar(root, "documentType", source);
        if (!string.Equals(documentType?.Trim(), DictionaryDefinition.DocumentTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'documentType: {DictionaryDefinition.DocumentTypeName}', found '{documentType}'.");
        }

        var name = Scalar(root, "name", source)?.Trim();
        if (string.IsNullOrEmpty(name) || !NamePattern().IsMatch(name))
        {
            throw new FlowValidationException(
                $"{source}: name is required and is a letter followed by letters, digits, '_' or '-' (at most 200 characters): a mapping reads the table as cache.<name>, and its file is dictionaries/<name>.yaml.");
        }

        var key = Scalar(root, "key", source)?.Trim() is { Length: > 0 } declaredKey ? declaredKey : DictionaryDefinition.DefaultKey;
        CheckFieldName(key, "key", source);
        if (LookupKeys.IsIdName(key))
        {
            throw new FlowValidationException($"{source}: key '{key}' names the entries' key 'id'; an entry's key is its id, so call it something else.");
        }

        var fields = Fields(root, key, source);
        var pairs = fields is null;
        var names = fields ?? [DictionaryDefinition.ValueField];
        var entries = Entries(root, key, names, pairs, source);
        return new DictionaryDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = Scalar(root, "description", source)?.Trim() is { Length: > 0 } description ? description : null,
            Key = key,
            Fields = names,
            IsPairs = pairs,
            Entries = entries,
        };
    }

    private static YamlMappingNode Root(string yaml, string source)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            // A key written twice is refused here too, with its position: which of the two was meant is not the reader's to say.
            throw new FlowValidationException($"{source}: line {ex.Start.Line}: {ex.Message}", ex);
        }

        if (stream.Documents.Count != 1)
        {
            throw new FlowValidationException($"{source}: a dictionary file holds exactly one document; it holds {stream.Documents.Count}.");
        }

        return stream.Documents[0].RootNode as YamlMappingNode
            ?? throw new FlowValidationException($"{source}: a dictionary document is a mapping of documentType, name, key, fields and entries.");
    }

    /// <summary>The fields the document lists, or null when it lists none, which makes it a dictionary of pairs.</summary>
    private static List<string>? Fields(YamlMappingNode root, string key, string source)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("fields"), out var node) || IsNull(node))
        {
            return null;
        }

        if (node is not YamlSequenceNode list || list.Children.Count == 0)
        {
            throw Error(source, node, "fields lists the names of the values each entry gives, such as fields: [type, family]; leave it out for a dictionary of pairs.");
        }

        var names = new List<string>(list.Children.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        foreach (var item in list.Children)
        {
            var name = Text(item, source, "a field name").Trim();
            CheckFieldName(name, "a field", source);
            if (LookupKeys.IsIdName(name))
            {
                throw Error(source, item, $"field '{name}' is called id; an entry's key is its id, so call the field something else.");
            }

            if (!seen.Add(name))
            {
                throw Error(source, item, string.Equals(name, key, StringComparison.OrdinalIgnoreCase)
                    ? $"field '{name}' is the key's own name; the key is kept under it already."
                    : $"field '{name}' is listed twice (names are compared ignoring case).");
            }

            names.Add(name);
        }

        return names;
    }

    private static List<DictionaryEntry> Entries(YamlMappingNode root, string key, IReadOnlyList<string> fields, bool pairs, string source)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("entries"), out var node) || node is not YamlMappingNode map || map.Children.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: entries is required and maps each key to {(pairs ? "its value" : "its values")}, such as {(pairs ? "entries: { M: m }" : "entries: { GR: { family: Gamma Ray } }")}.");
        }

        if (map.Children.Count > DictionaryDefinition.MaxEntries)
        {
            throw new FlowValidationException(
                $"{source}: entries lists {map.Children.Count} keys, and a dictionary holds at most {DictionaryDefinition.MaxEntries}: every entry is loaded with the cache version a render reads. Keep a table that large in an ingestion table.");
        }

        var entries = new List<DictionaryEntry>(map.Children.Count);
        var known = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);
        foreach (var (keyNode, valueNode) in map.Children)
        {
            if (IsNull(keyNode))
            {
                throw Error(source, keyNode, "an entry has no key; every entry is keyed by the text before its colon.");
            }

            var entryKey = Text(keyNode, source, "an entry's key");
            if (LookupKeys.Problem(entryKey) is { } problem)
            {
                throw Error(source, keyNode, $"the key {problem}.");
            }

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (pairs)
            {
                values[DictionaryDefinition.ValueField] = IsNull(valueNode)
                    ? null
                    : valueNode is YamlScalarNode scalar
                        ? scalar.Value ?? string.Empty
                        : throw Error(source, valueNode, $"'{entryKey}' maps to something that is not text; a dictionary of pairs maps each key to one text, or ~ for no value. List fields for a key with several values.");
            }
            else if (!IsNull(valueNode))
            {
                if (valueNode is not YamlMappingNode named)
                {
                    throw Error(source, valueNode, $"'{entryKey}' gives {Describe(valueNode)}; a dictionary with fields gives each key a map of them, such as {{ {fields[0]}: ... }}.");
                }

                foreach (var (fieldNode, fieldValue) in named.Children)
                {
                    var field = Text(fieldNode, source, "a field name").Trim();
                    if (!known.Contains(field))
                    {
                        throw Error(source, fieldNode, $"'{entryKey}' gives '{field}', which fields does not list; it lists {string.Join(", ", fields)}.");
                    }

                    values[fields.First(f => f.Equals(field, StringComparison.OrdinalIgnoreCase))] = IsNull(fieldValue)
                        ? null
                        : fieldValue is YamlScalarNode text
                            ? text.Value ?? string.Empty
                            : throw Error(source, fieldValue, $"'{entryKey}' gives {field} as {Describe(fieldValue)}; a value is one text, or ~ for no value.");
                }
            }

            entries.Add(new DictionaryEntry(entryKey, values));
        }

        return entries;
    }

    private static void CheckFieldName(string name, string what, string source)
    {
        if (!FieldPattern().IsMatch(name))
        {
            throw new FlowValidationException(
                $"{source}: {what} '{name}' is not a name a mapping can read: a letter or '_' followed by letters, digits or '_', at most 128 characters.");
        }
    }

    /// <summary>A top-level scalar, or null when the document leaves it out or writes it as no value.</summary>
    private static string? Scalar(YamlMappingNode root, string key, string source)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode(key), out var node) || IsNull(node))
        {
            return null;
        }

        return node is YamlScalarNode scalar ? scalar.Value : throw Error(source, node, $"{key} is one text, not {Describe(node)}.");
    }

    private static string Text(YamlNode node, string source, string what)
        => node is YamlScalarNode scalar ? scalar.Value ?? string.Empty : throw Error(source, node, $"{what} is one text, not {Describe(node)}.");

    /// <summary>An unquoted <c>~</c>, <c>null</c> or empty scalar is no value; a quoted one is the text it spells.</summary>
    private static bool IsNull(YamlNode node)
        => node is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any } scalar
           && scalar.Value is null or "" or "~" or "null" or "Null" or "NULL";

    private static string Describe(YamlNode node) => node switch
    {
        YamlSequenceNode => "a list",
        YamlMappingNode => "a map",
        _ => "text",
    };

    private static FlowValidationException Error(string source, YamlNode node, string message)
        => new($"{source}: line {node.Start.Line}: {message}");

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_\-]{0,199}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex FieldPattern();
}
