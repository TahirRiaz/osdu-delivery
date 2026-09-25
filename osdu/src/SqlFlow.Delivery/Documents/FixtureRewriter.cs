using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Validation;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SqlFlow.Delivery.Documents;

/// <summary>What happened to one fixture when its expected record was written again.</summary>
public enum FixtureOutcomeKind
{
    /// <summary>Its <c>expected</c> now holds what it renders.</summary>
    Updated,

    /// <summary>It already expected what it renders, so its text was left exactly as written.</summary>
    Unchanged,

    /// <summary>It was left as written, for the reason given.</summary>
    Skipped,
}

/// <summary>One fixture's outcome, with why it was skipped.</summary>
public sealed record FixtureOutcome(string Name, FixtureOutcomeKind Kind, string? Reason = null);

/// <summary>A mapping document with its fixtures' expected records written again, and what happened to each.</summary>
public sealed record FixtureRewrite(string Text, IReadOnlyList<FixtureOutcome> Outcomes)
{
    public bool Changed => Outcomes.Any(o => o.Kind == FixtureOutcomeKind.Updated);
}

/// <summary>
/// Writes what each fixture of a mapping renders into its <c>expected</c> block (<c>sqlflow fixtures update</c>). Only
/// the lines of those blocks change: the rest of the document, its comments and its layout, stay exactly as written. A
/// fixture is left alone when its render is not a record a delivery would send (it holds, fails, or asks a search the
/// fixture does not answer), since writing that would make the suite expect a broken record; and when its expected
/// record is written in a form this cannot edit in place (a flow mapping, an anchored or aliased value), which is named
/// so it can be written as a block.
/// </summary>
/// <remarks>
/// A record is written the way a person lays one out: the id and kind, then the properties in the order the mapping's
/// record tree writes them (so the envelope before the data), an object or a list of plain values on one line when it
/// fits, and everything else a line per value. The gate compares records canonically, so the layout is for the reader.
/// </remarks>
public static class FixtureRewriter
{
    private const string ExpectedKey = "expected";

    /// <summary>How wide a line may grow before an object or a list of plain values is written a line per value.</summary>
    private const int Width = 120;

    private static readonly JsonSerializerOptions ValueOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="yaml">The mapping document as it is on disk.</param>
    /// <param name="mapping">The mapping the document holds, whose record tree orders what a record is written in.</param>
    /// <param name="renders">Its fixtures as the preflight renders them, in the order the document lists them.</param>
    /// <param name="where">The document's path, for messages.</param>
    public static FixtureRewrite Rewrite(string yaml, MappingDefinition mapping, IReadOnlyList<FixtureRender> renders, string where)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(renders);
        var order = Order(mapping);
        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        var items = FixtureItems(yaml, where);
        if (items.Count != renders.Count)
        {
            throw new FlowValidationException($"{where}: the document lists {items.Count} fixture(s) and the mapping read {renders.Count}; read it again and retry.");
        }

        var outcomes = new List<FixtureOutcome>(renders.Count);
        var edits = new List<(int First, int Last, List<string> Lines)>();
        for (var i = 0; i < renders.Count; i++)
        {
            var (fixture, result, problem) = renders[i];
            if (problem is not null)
            {
                outcomes.Add(new FixtureOutcome(fixture.Name, FixtureOutcomeKind.Skipped, problem));
                continue;
            }

            if (result!.Holds.Count > 0)
            {
                outcomes.Add(new FixtureOutcome(
                    fixture.Name,
                    FixtureOutcomeKind.Skipped,
                    $"renders a record that is held ({string.Join("; ", result.Holds)}); a fixture expects a record a delivery would send, so fix the mapping or the row first"));
                continue;
            }

            if (Same(fixture.Expected, result.Canonical))
            {
                outcomes.Add(new FixtureOutcome(fixture.Name, FixtureOutcomeKind.Unchanged));
                continue;
            }

            if (Edit(items[i], lines, Written(result.Document, order), out var edit) is { } reason)
            {
                outcomes.Add(new FixtureOutcome(fixture.Name, FixtureOutcomeKind.Skipped, reason));
                continue;
            }

            edits.Add(edit);
            outcomes.Add(new FixtureOutcome(fixture.Name, FixtureOutcomeKind.Updated));
        }

        // Bottom up, so an edit never moves the lines of one still to come.
        foreach (var (first, last, replacement) in edits.OrderByDescending(e => e.First))
        {
            lines.RemoveRange(first, last - first + 1);
            lines.InsertRange(first, replacement);
        }

        return new FixtureRewrite(string.Join(newline, lines), outcomes);
    }

    /// <summary>
    /// Where each property of a rendered record sorts: the position in the mapping's record tree of the first entry that
    /// writes it or a property inside it, by its path of names (<c>data.Curves.CurveID</c>, an array's items under the
    /// array's own name). The id and the kind the engine writes come first.
    /// </summary>
    private static Dictionary<string, int> Order(MappingDefinition mapping)
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal) { ["id"] = -2, ["kind"] = -1 };
        foreach (var entry in mapping.Entries)
        {
            var path = string.Empty;
            foreach (var segment in entry.Target.Segments)
            {
                path = path.Length == 0 ? segment.Name : path + "." + segment.Name;
                order.TryAdd(path, entry.Index);
            }
        }

        return order;
    }

    /// <summary>A record as the lines of its expected block, without the block's indentation.</summary>
    private static string Written(JsonNode document, IReadOnlyDictionary<string, int> order)
    {
        var lines = new List<string>();
        Write(document, string.Empty, string.Empty, string.Empty, 0, order, lines);
        return string.Join('\n', lines);
    }

    private static void Write(JsonNode? node, string head, string tail, string path, int indent, IReadOnlyDictionary<string, int> order, List<string> lines)
    {
        var pad = new string(' ', indent);
        if (Inline(node, order, path) is { } inline && pad.Length + head.Length + inline.Length + tail.Length <= Width)
        {
            lines.Add(pad + head + inline + tail);
            return;
        }

        switch (node)
        {
            case JsonObject obj:
            {
                lines.Add(pad + head + "{");
                var keys = Ordered(obj, path, order);
                for (var i = 0; i < keys.Count; i++)
                {
                    var key = keys[i];
                    Write(obj[key], Name(key) + ": ", i < keys.Count - 1 ? "," : string.Empty, Child(path, key), indent + 2, order, lines);
                }

                lines.Add(pad + "}" + tail);
                return;
            }

            case JsonArray array:
                lines.Add(pad + head + "[");
                for (var i = 0; i < array.Count; i++)
                {
                    Write(array[i], string.Empty, i < array.Count - 1 ? "," : string.Empty, path, indent + 2, order, lines);
                }

                lines.Add(pad + "]" + tail);
                return;

            default:
                lines.Add(pad + head + Scalar(node) + tail);
                return;
        }
    }

    /// <summary>
    /// A value on one line, or null when it holds more than plain values: an object of plain values and lists of them, or
    /// a list of plain values.
    /// </summary>
    private static string? Inline(JsonNode? node, IReadOnlyDictionary<string, int> order, string path) => node switch
    {
        JsonObject { Count: 0 } => "{}",
        JsonArray { Count: 0 } => "[]",
        JsonObject obj when obj.All(kv => kv.Value is not JsonObject && (kv.Value is not JsonArray list || list.All(v => v is not (JsonObject or JsonArray))))
            => "{ " + string.Join(", ", Ordered(obj, path, order).Select(key => Name(key) + ": " + Inline(obj[key], order, Child(path, key)))) + " }",
        JsonArray array when array.All(v => v is not (JsonObject or JsonArray))
            => "[" + string.Join(", ", array.Select(Scalar)) + "]",
        JsonObject or JsonArray => null,
        _ => Scalar(node),
    };

    /// <summary>An object's keys in the order the mapping writes them; any it does not name follow, by name.</summary>
    private static List<string> Ordered(JsonObject obj, string path, IReadOnlyDictionary<string, int> order)
        => obj.Select(kv => kv.Key)
            .OrderBy(key => order.TryGetValue(Child(path, key), out var at) ? at : int.MaxValue)
            .ThenBy(key => key, StringComparer.Ordinal)
            .ToList();

    private static string Child(string path, string key) => path.Length == 0 ? key : path + "." + key;

    private static string Name(string key) => JsonSerializer.Serialize(key, ValueOptions);

    private static string Scalar(JsonNode? node) => node is null ? "null" : node.ToJsonString(ValueOptions);

    /// <summary>Whether the expected text already reads as the rendered record, compared canonically as the gate compares them.</summary>
    private static bool Same(string expected, string canonical)
    {
        try
        {
            return string.Equals(CanonicalJson.ToString(CanonicalJson.Normalize(JsonNode.Parse(expected))), canonical, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The mapping nodes of the document's fixtures, in order.</summary>
    private static List<YamlNode> FixtureItems(string yaml, string where)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{where}: the document is not YAML the fixtures can be found in: {ex.Message}", ex);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new FlowValidationException($"{where}: the document is not a mapping document.");
        }

        return root.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode { Value: "fixtures" }).Value switch
        {
            null => [],
            YamlSequenceNode sequence => sequence.Children.ToList(),
            _ => throw new FlowValidationException($"{where}: fixtures is a list."),
        };
    }

    /// <summary>
    /// The lines that replace a fixture's expected value, or why it cannot be edited in place. The key line is kept, with
    /// its indicator made a literal block (<c>|</c>); the block's lines are the record pretty-printed at the block's own
    /// indentation, or two spaces past the key when the value was not a block.
    /// </summary>
    private static string? Edit(YamlNode item, List<string> lines, string record, out (int First, int Last, List<string> Lines) edit)
    {
        edit = default;
        if (item is not YamlMappingNode map)
        {
            return "is not written as a map";
        }

        if (map.Style == YamlDotNet.Core.Events.MappingStyle.Flow)
        {
            return "is written as a flow mapping ({ ... }); write it as a block, with expected: | on a line of its own, to have it updated";
        }

        var (key, value) = map.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode { Value: ExpectedKey });
        if (key is null || value is not YamlScalarNode scalar)
        {
            return "has no expected text to write";
        }

        if (!scalar.Anchor.IsEmpty || !key.Anchor.IsEmpty)
        {
            return "has an anchored expected value, which other places may share; write it without the anchor to have it updated";
        }

        var keyLine = checked((int)key.Start.Line) - 1;
        var keyColumn = checked((int)key.Start.Column) - 1;
        var head = lines[keyLine];
        if (head.Length < keyColumn + ExpectedKey.Length || !head.AsSpan(keyColumn).StartsWith(ExpectedKey, StringComparison.Ordinal))
        {
            return "has an expected key this cannot find in the text";
        }

        int last;
        int indent;
        if (scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded)
        {
            // A block runs until the first line that is not blank and not indented past the key.
            last = keyLine;
            indent = -1;
            for (var i = keyLine + 1; i < lines.Count; i++)
            {
                var text = lines[i];
                if (text.Trim().Length == 0)
                {
                    continue;
                }

                var leading = text.Length - text.TrimStart(' ').Length;
                if (leading <= keyColumn)
                {
                    break;
                }

                indent = indent < 0 ? leading : indent;
                last = i;
            }

            indent = indent < 0 ? keyColumn + 2 : indent;
        }
        else
        {
            last = checked((int)scalar.End.Line) - 1;
            var after = lines[last][Math.Min(lines[last].Length, checked((int)scalar.End.Column) - 1)..].Trim();
            if (after.Length > 0 && !after.StartsWith('#'))
            {
                return "has an expected value followed by more on its line; write it as a block to have it updated";
            }

            indent = keyColumn + 2;
        }

        var replacement = new List<string> { head[..keyColumn] + ExpectedKey + ": |" };
        var pad = new string(' ', indent);
        replacement.AddRange(record.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Select(line => line.Length == 0 ? string.Empty : pad + line));
        edit = (keyLine, last, replacement);
        return null;
    }
}
