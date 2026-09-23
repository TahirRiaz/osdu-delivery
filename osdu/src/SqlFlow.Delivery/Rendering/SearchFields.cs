using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Rendering;

/// <summary>One search a mapping declares, with how each property its findBy lines compare is indexed.</summary>
/// <param name="Declaration">The search as the mapping declares it.</param>
/// <param name="Fields">Each property compared, by the path the mapping writes, as the searched kind's schema indexes it.</param>
public sealed record ResolvedSearch(MappingSearch Declaration, IReadOnlyDictionary<string, OsduField> Fields);

/// <summary>
/// A mapping's searches resolved against the schemas they pin: for every property a findBy line compares, how the
/// platform indexes it, and so how a query asks for it. What cannot be asked is listed rather than thrown, so a
/// preflight reports every problem at once.
/// </summary>
public sealed class ResolvedSearches
{
    private ResolvedSearches(IReadOnlyDictionary<string, ResolvedSearch> searches, IReadOnlyList<string> problems)
    {
        Searches = searches;
        Problems = problems;
    }

    /// <summary>A mapping that searches nothing.</summary>
    public static ResolvedSearches None { get; } = new(new Dictionary<string, ResolvedSearch>(StringComparer.Ordinal), []);

    /// <summary>The searches that resolved, by the name entries write.</summary>
    public IReadOnlyDictionary<string, ResolvedSearch> Searches { get; }

    /// <summary>Why a search or a property it compares cannot be asked for, one line each, naming the mapping's own words.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>
    /// Resolves <paramref name="mapping"/>'s searches against the schemas in <paramref name="schemas"/>, which holds the
    /// saved template each search pins, or lacks it when it is not saved.
    /// </summary>
    public static ResolvedSearches Resolve(MappingDefinition mapping, IReadOnlyDictionary<TemplateReference, SchemaSnapshot> schemas)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(schemas);
        if (mapping.Searches.Count == 0)
        {
            return None;
        }

        var where = mapping.SourcePath ?? mapping.Reference;
        var problems = new List<string>();
        var resolved = new Dictionary<string, ResolvedSearch>(StringComparer.Ordinal);
        foreach (var (name, search) in mapping.Searches.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!schemas.TryGetValue(search.Schema, out var schema))
            {
                problems.Add(
                    $"{where}: search '{name}' pins template {search.Schema}, which is not saved. Save it on the Templates page, or with 'sqlflow template import'.");
                continue;
            }

            var fields = new Dictionary<string, OsduField>(StringComparer.Ordinal);
            var compared = mapping.Entries
                .Where(e => e.Source?.Kind == MappingSourceKind.Search && string.Equals(e.Source.CacheType, name, StringComparison.Ordinal))
                .SelectMany(e => e.FindBy.Select(f => (Entry: e, f.Field)));
            foreach (var (entry, path) in compared)
            {
                if (fields.ContainsKey(path))
                {
                    continue;
                }

                var (field, problem) = SearchFields.Classify(schema, path);
                if (field is null)
                {
                    problems.Add($"{where}: {entry.Where} compares search.{name}.{path}, which cannot be searched: {problem}");
                    continue;
                }

                fields[path] = field;
            }

            resolved[name] = new ResolvedSearch(search, fields);
        }

        return new ResolvedSearches(resolved, problems);
    }

    /// <summary>How the search <paramref name="name"/> compares <paramref name="path"/>, when both resolved.</summary>
    public bool TryField(string name, string path, out ResolvedSearch? search, out OsduField? field)
    {
        field = null;
        return Searches.TryGetValue(name, out search) && search.Fields.TryGetValue(path, out field);
    }
}

/// <summary>
/// How the platform indexes a property of a kind, read from that kind's schema the way the indexer reads it
/// (<c>PropertiesProcessor</c>, <c>TypeMapper</c>, <c>SchemaConverterPropertiesConfig</c>), so a query asks for what
/// was actually stored.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// A string is indexed as text with a <c>keyword</c> sub-field, unless its pattern starts <c>^srn</c>, which makes it a
/// link and a bare keyword, or its format is <c>date-time</c>, <c>date</c> or <c>time</c>, which makes it a date. Any
/// other format is indexed as text. An OSDU id reference is text: its pattern does not start <c>^srn</c>.
/// </description></item>
/// <item><description>
/// A list of strings is typed by its items' pattern and type alone; the items' format is not read.
/// </description></item>
/// <item><description>
/// An object's properties are indexed under dotted paths. An array of objects is indexed by its
/// <c>x-osdu-indexing</c> hint: <c>nested</c> keeps each object whole for a nested query, <c>flattened</c> stores every
/// value inside it as a keyword, and with no hint the array is mapped as an object whose properties are not indexed at
/// all, since every index is created with <c>dynamic: false</c>.
/// </description></item>
/// </list>
/// </remarks>
public static class SearchFields
{
    /// <summary>What the indexer reads at the start of a pattern to type a string as a link.</summary>
    private const string LinkPatternPrefix = "^srn";

    private const string IndexingHint = "x-osdu-indexing";

    /// <summary>
    /// How <paramref name="schema"/> has the platform index <paramref name="path"/>, or why a search cannot compare it.
    /// </summary>
    public static (OsduField? Field, string? Problem) Classify(SchemaSnapshot schema, string path)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OsduPath.IsPath(path))
        {
            return (null, $"'{path}' is not a property path a query can name.");
        }

        var segments = path.Split('.');
        string? nested = null;
        string? flattened = null;
        for (var i = 1; i < segments.Length; i++)
        {
            var prefix = string.Join('.', segments.Take(i));
            var property = schema.Resolve(prefix);
            if (property is null)
            {
                return (null, $"the schema of {schema.Kind} has no property {prefix}.");
            }

            if (flattened is not null)
            {
                // Inside a flattened array every value is a keyword under its dotted path, however deep.
                continue;
            }

            if (property.Type == SchemaType.Array)
            {
                if (!IsObjectItems(property))
                {
                    return (null, $"{prefix} in the schema of {schema.Kind} is a list of values, which has no properties to compare.");
                }

                switch (Hint(property))
                {
                    case "nested" when nested is not null:
                        return (null, $"{prefix} is a nested array inside the nested array {nested}; a lookup reaches into one nested array, since the search service rewrites the property names inside a nested query by pattern and a query nested twice does not survive that.");
                    case "nested":
                        nested = prefix;
                        break;
                    case "flattened":
                        flattened = prefix;
                        break;
                    default:
                        return (null, $"{prefix} is an array of objects the schema of {schema.Kind} gives no x-osdu-indexing hint, and the platform maps such an array as an object whose properties it does not index, so no query reaches inside it.");
                }

                continue;
            }

            if (property.Type != SchemaType.Object)
            {
                return (null, $"{prefix} in the schema of {schema.Kind} is a {Name(property.Type)}, which has no properties to compare.");
            }
        }

        var leaf = schema.Resolve(path);
        if (leaf is null)
        {
            return (null, $"the schema of {schema.Kind} has no property {path}.");
        }

        var (index, problem) = flattened is not null ? FlattenedLeaf(leaf, path) : Leaf(leaf, path);
        if (index is null)
        {
            return (null, problem);
        }

        return (index == OsduFieldIndex.Text ? OsduField.Text(path, nested) : OsduField.Keyword(path, nested), null);
    }

    /// <summary>How a property outside any flattened array is indexed, by the indexer's order: pattern, then format, then type.</summary>
    private static (OsduFieldIndex? Index, string? Problem) Leaf(SchemaProperty leaf, string path)
    {
        var node = leaf.Schema;
        var type = leaf.Type;
        if (type == SchemaType.Array)
        {
            if (leaf.Items is not { } items || IsObjectItems(leaf))
            {
                return (null, $"{path} is a list of objects, not a value; compare a property of the objects in it.");
            }

            // A list is typed by its items' pattern, then their type; their format is not read.
            if ((Pattern(node) is { } listPattern && listPattern.StartsWith(LinkPatternPrefix, StringComparison.Ordinal))
                || (Pattern(items) is { } itemPattern && itemPattern.StartsWith(LinkPatternPrefix, StringComparison.Ordinal)))
            {
                return (OsduFieldIndex.Keyword, null);
            }

            return SchemaSnapshot.TypeOfNode(items) == SchemaType.String
                ? (OsduFieldIndex.Text, null)
                : (null, $"{path} is a list of {Name(SchemaSnapshot.TypeOfNode(items))} values, and a search compares text.");
        }

        if (type == SchemaType.Object)
        {
            return (null, $"{path} is an object, not a value; compare one of its properties.");
        }

        if (Pattern(node) is { } pattern && pattern.StartsWith(LinkPatternPrefix, StringComparison.Ordinal))
        {
            return (OsduFieldIndex.Keyword, null);
        }

        if (leaf.Format is { } format)
        {
            switch (format)
            {
                case "date-time" or "date" or "time":
                    return (null, $"{path} is a {format} string, which the platform indexes as a date rather than as text, so a search cannot compare it exactly.");
                case "int32" or "integer" or "int64":
                    return (null, $"{path} is a string of format {format}, which the platform indexes as a number, and a search compares text.");
            }
        }

        return type switch
        {
            SchemaType.String => (OsduFieldIndex.Text, null),
            SchemaType.Any => (null, $"{path} declares no type in the schema, and the platform does not index a property without one."),
            _ => (null, $"{path} is a {Name(type)}, and a search compares text."),
        };
    }

    /// <summary>A value inside a flattened array: every value is a keyword, and only an object or a list of objects has none.</summary>
    private static (OsduFieldIndex? Index, string? Problem) FlattenedLeaf(SchemaProperty leaf, string path)
        => leaf.Type == SchemaType.Object || (leaf.Type == SchemaType.Array && IsObjectItems(leaf))
            ? (null, $"{path} is an object inside a flattened array, not a value; compare one of its properties.")
            : (OsduFieldIndex.Keyword, null);

    /// <summary>Whether an array's items are objects the indexer walks into: a schema with a reference, branches or properties.</summary>
    private static bool IsObjectItems(SchemaProperty array)
        => array.Items is { } items
            && (items["properties"] is JsonObject || SchemaSnapshot.TypeOfNode(items) == SchemaType.Object);

    private static string? Hint(SchemaProperty property)
        => property.Schema[IndexingHint] is JsonObject hint && hint["type"] is JsonValue value && value.TryGetValue<string>(out var type)
            ? type
            : null;

    private static string? Pattern(JsonObject node)
        => node["pattern"] is JsonValue value && value.TryGetValue<string>(out var pattern) ? pattern : null;

    private static string Name(SchemaType type) => type switch
    {
        SchemaType.Number => "number",
        SchemaType.Integer => "integer",
        SchemaType.Boolean => "boolean",
        SchemaType.Object => "object",
        SchemaType.Array => "list",
        SchemaType.String => "string",
        _ => "value without a type",
    };
}
