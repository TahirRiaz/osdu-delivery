using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>
/// The properties the explorer offers to search, narrow to, group by and show as columns for a kind: the record's own, every
/// value the kind's schema declares that a query reaches (each asked as the schema has the platform index it, a value of a
/// nested array through it), and the values the records read hold beyond them, such as those an index augmentation adds,
/// which no schema declares. A value the schema declares that the index holds no comparable value of (inside an array of
/// objects the schema gives no indexing hint, an object, a value of no type) is left out and counted.
/// </summary>
internal static partial class ExplorerFieldCatalog
{
    /// <summary>Where a property was found: a property of every record.</summary>
    public const string RecordOrigin = "record";

    /// <summary>Where a property was found: declared by the schema of the kind read.</summary>
    public const string SchemaOrigin = "schema";

    /// <summary>Where a property was found: held by the records read, and not declared by the schema read.</summary>
    public const string RecordsOrigin = "records";

    /// <summary>A value of a record's data, as the records read hold it: its path, how a value like it is indexed, and whether a list of objects is on its way.</summary>
    internal readonly record struct Held(string Path, string Index, bool InList);

    /// <summary>
    /// The leaves of a record's data, each with how a value like the one it holds is indexed: every value and list of values,
    /// through objects and lists of objects alike, up to <paramref name="max"/> of them, the first record that holds a path
    /// typing it.
    /// </summary>
    public static void Collect(JsonObject data, Dictionary<string, Held> found, int max, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(found);
        Walk(data, SearchFields.DataRoot, 1, inList: false, found, max, maxDepth);
    }

    private static void Walk(JsonObject node, string path, int depth, bool inList, Dictionary<string, Held> found, int max, int maxDepth)
    {
        foreach (var (key, value) in node)
        {
            // A key a query cannot name (a space, a character of the syntax) is not a property a page can ask for; a key holding
            // dots, as an index augmentation names its properties (Augmented.WellboreName), reads as the path it spells.
            var child = $"{path}.{key}";
            if (!OsduPath.IsPath(child))
            {
                continue;
            }

            switch (value)
            {
                case JsonObject inner when depth < maxDepth:
                    Walk(inner, child, depth + 1, inList, found, max, maxDepth);
                    break;
                case JsonArray items when items.Count > 0 && items.All(i => i is JsonValue):
                    Add(found, new Held(child, IndexOf(items[0]!) ?? "text", inList), max);
                    break;
                case JsonArray items when depth < maxDepth:
                    foreach (var item in items.OfType<JsonObject>().Take(MaxItemsWalked))
                    {
                        Walk(item, child, depth + 1, inList: true, found, max, maxDepth);
                    }

                    break;
                case JsonValue leaf when IndexOf(leaf) is { } index:
                    Add(found, new Held(child, index, inList), max);
                    break;
            }
        }
    }

    /// <summary>How many items of one list of objects a record's walk reads: enough to see the forms a list's items take.</summary>
    private const int MaxItemsWalked = 25;

    private static void Add(Dictionary<string, Held> found, Held held, int max)
    {
        if (found.Count < max)
        {
            found.TryAdd(held.Path, held);
        }
    }

    /// <summary>How the indexer keeps a value like <paramref name="value"/>: text, a number, a boolean, or a date written as one.</summary>
    internal static string? IndexOf(JsonNode value)
    {
        if (value is not JsonValue leaf)
        {
            return null;
        }

        return leaf.GetValueKind() switch
        {
            JsonValueKind.String when leaf.TryGetValue<string>(out var text) && IsoInstant().IsMatch(text) => "date",
            JsonValueKind.String => "text",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => null,
        };
    }

    /// <summary>
    /// The values of the record's data <paramref name="schema"/> declares that a query reaches, each as the schema has the
    /// platform index it, in the order the schema declares them, with how many it declares that no query reaches. A list
    /// whose items are a choice of forms (a wellbore's geographic contexts) is read through its forms, one level deep.
    /// </summary>
    public static (List<ExplorerFieldInfo> Fields, int Unreached) Declared(SchemaSnapshot schema, int max, int maxDescription)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var fields = new List<ExplorerFieldInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unreached = 0;
        foreach (var variable in OsduTemplate.From(schema).Variables)
        {
            if (variable.Role != TemplateVariableRole.Mapping || variable.Path.Root != SearchFields.DataRoot || variable.Inside is not null || variable.KeyValueType is not null)
            {
                continue;
            }

            var path = variable.Path.SchemaPath;
            switch (variable.Shape)
            {
                case TemplateVariableShape.Value or TemplateVariableShape.ValueList:
                    if (!Offer(schema, path, variable.Title, variable.Description, fields, seen, max, maxDescription))
                    {
                        unreached++;
                    }

                    break;
                case TemplateVariableShape.Whole when variable.Type == "array" && !variable.Path.IsRepeated:
                    foreach (var (name, title, description) in FormProperties(schema, path))
                    {
                        // A property a form declares that the index does not reach is one of a form's objects, not a value: it is
                        // not counted, since the template never listed it.
                        Offer(schema, $"{path}.{name}", title, description, fields, seen, max, maxDescription);
                    }

                    break;
            }
        }

        return (fields, unreached);
    }

    /// <summary>Offers <paramref name="path"/> where the index holds a value of it a query compares; false where it does not.</summary>
    private static bool Offer(
        SchemaSnapshot schema, string path, string? title, string? description, List<ExplorerFieldInfo> fields, HashSet<string> seen, int max, int maxDescription)
    {
        if (SearchFields.ClassifyValue(schema, path).Field is not { } field)
        {
            return false;
        }

        if (seen.Add(path) && fields.Count < max)
        {
            fields.Add(new ExplorerFieldInfo(path, IndexName(field.Index), field.NestedPath, SchemaOrigin, Trimmed(title), Clipped(description, maxDescription)));
        }

        return true;
    }

    /// <summary>
    /// The values the forms of a list's items declare, each once: a wellbore's geographic contexts allow five forms, and only
    /// the field's declares <c>FieldID</c>. A property holding an object or a list is not a value, and is passed over.
    /// </summary>
    private static IEnumerable<(string Name, string? Title, string? Description)> FormProperties(SchemaSnapshot schema, string path)
    {
        if (schema.Resolve(path)?.Items is not { } items)
        {
            yield break;
        }

        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (form, _) in schema.FormsOf(schema.EffectiveOf(items)))
        {
            if (form["properties"] is not JsonObject properties)
            {
                continue;
            }

            foreach (var (name, node) in properties)
            {
                if (node is JsonObject property && named.Add(name))
                {
                    yield return (name, Text(property, "title"), Text(property, "description"));
                }
            }
        }
    }

    /// <summary>
    /// The title and description the schema gives a property a record holds that the template does not list on its own (one of
    /// a list's forms), through the forms on its way; nulls where it gives none.
    /// </summary>
    public static (string? Title, string? Description) Described(SchemaSnapshot schema, string path, int maxDescription)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var property = schema.ResolveThroughForms(path);
        return property is null ? (null, null) : (Trimmed(Text(property.Schema, "title")), Clipped(Text(property.Schema, "description"), maxDescription));
    }

    /// <summary>How a property is indexed, as the explorer's answers name it.</summary>
    public static string IndexName(OsduFieldIndex index) => index.ToString().ToLowerInvariant();

    private static string? Text(JsonObject node, string name) => node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Clipped(string? text, int max)
    {
        var trimmed = Trimmed(text);
        return trimmed is null || trimmed.Length <= max ? trimmed : string.Create(CultureInfo.InvariantCulture, $"{trimmed[..(max - 3)].TrimEnd()}...");
    }

    /// <summary>A date and time as OSDU writes one (ISO 8601, a date with a time after it).</summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}", RegexOptions.CultureInvariant)]
    private static partial Regex IsoInstant();
}
