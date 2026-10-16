using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Templates;

/// <summary>
/// A path segment's filter as the schema describes the property it compares: the property, how it compares
/// (<c>equals</c>, <c>contains</c>, <c>endsWith</c>), the text, whether the objects the segment holds declare the property,
/// and what the schema says of it.
/// </summary>
public sealed record SchemaFilterReading(string Property, string Compare, string Text, bool Known, string? Description);

/// <summary>
/// One segment of a path as a schema describes it: the property, its filter, whether the schema declares it, its JSON Schema
/// type (and its items' for a list), format, title and description, how the indexer stores an array of objects
/// (<c>nested</c>, <c>flattened</c>, or none), the entity types it names (<c>master-data--Wellbore</c>) and, when only some
/// of the forms a <c>oneOf</c> or <c>anyOf</c> allows declare it, those forms by their titles (<c>AbstractGeoFieldContext</c>).
/// </summary>
public sealed record SchemaSegmentReading(
    string Name, SchemaFilterReading? Filter, bool Found, string? Type, string? ItemType, string? Format, string? Title, string? Description,
    string? Indexing, IReadOnlyList<string> References, string? Branch);

/// <summary>
/// A path as one kind's schema describes it, segment by segment, and why the description stops where a segment is not in the
/// schema. It describes; it decides nothing: how a search reads a path is <see cref="Rendering.SearchFields"/>'s, and a
/// label's path is read from the records as they are.
/// </summary>
public sealed record SchemaPathReading(string Kind, IReadOnlyList<SchemaSegmentReading> Segments, string? Problem)
{
    /// <summary>Whether the schema declares every segment.</summary>
    [JsonIgnore]
    public bool Complete => Segments.All(s => s.Found);

    /// <summary>The last segment, when the schema declares every one.</summary>
    [JsonIgnore]
    public SchemaSegmentReading? Leaf => Complete && Segments.Count > 0 ? Segments[^1] : null;

    /// <summary>The entity types the path's value names, as the schema declares them; none for a value that is no reference, or one the schema does not describe.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> References => Leaf?.References ?? [];
}

/// <summary>
/// Reads a dimension path (<see cref="DimensionPath"/>) through a kind's schema the way a person reads it: every segment
/// with what the schema says of it, the forms of a <c>oneOf</c> or <c>anyOf</c> looked into for the segment (a wellbore's
/// <c>GeoContexts</c> holds five kinds of context, and only the field's declares <c>FieldID</c>), and a filter's property
/// looked up in the objects the segment holds.
/// </summary>
public static class SchemaPathReader
{
    /// <summary>Describes <paramref name="path"/> in <paramref name="schema"/>.</summary>
    public static SchemaPathReading Read(SchemaSnapshot schema, DimensionPath path)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(path);
        var segments = new List<SchemaSegmentReading>(path.Segments.Count);
        var holder = schema.EffectiveRootObject;
        string? problem = null;
        var walked = new List<string>(path.Segments.Count);
        foreach (var segment in path.Segments)
        {
            walked.Add(segment.Name);
            if (problem is not null)
            {
                segments.Add(Unknown(segment));
                continue;
            }

            var found = Find(schema, holder, segment.Name);
            if (found is null)
            {
                problem = Missing(schema, holder, string.Join('.', walked), segment.Name);
                segments.Add(Unknown(segment));
                continue;
            }

            var (raw, own, branch) = found.Value;
            var type = TypeOf(schema, own);
            JsonObject? items = null;
            JsonObject? rawItems = null;
            if (type == SchemaType.Array && own["items"] is JsonObject itemNode)
            {
                rawItems = itemNode;
                items = schema.EffectiveOf(itemNode);
            }

            var references = OsduTemplate.Relationships(raw, own).ToList();
            if (rawItems is not null && items is not null)
            {
                foreach (var reference in OsduTemplate.Relationships(rawItems, items))
                {
                    if (!references.Contains(reference, StringComparer.Ordinal))
                    {
                        references.Add(reference);
                    }
                }
            }

            // The objects the segment holds: an array's items, or the object itself; a filter compares a property of each.
            var holds = items ?? own;
            segments.Add(new SchemaSegmentReading(
                segment.Name,
                segment.FilterProperty is null ? null : Filter(schema, holds, segment),
                Found: true,
                Name(type),
                items is null ? null : Name(TypeOf(schema, items)),
                Text(raw, "format") ?? Text(own, "format"),
                Text(raw, "title") ?? Text(own, "title"),
                Text(raw, "description") ?? Text(own, "description"),
                own["x-osdu-indexing"] is JsonObject hint ? Text(hint, "type") : null,
                references,
                branch));
            holder = holds;
        }

        return new SchemaPathReading(schema.Kind, segments, problem);
    }

    /// <summary>
    /// The property <paramref name="name"/> of the object <paramref name="holder"/> describes: its own, or one the forms of its
    /// <c>oneOf</c> or <c>anyOf</c> declare (named by the forms that declare it), or the schema of its free keys.
    /// </summary>
    private static (JsonObject Raw, JsonObject Own, string? Branch)? Find(SchemaSnapshot schema, JsonObject holder, string name)
    {
        if (holder["properties"] is JsonObject own && own[name] is JsonObject direct)
        {
            return (direct, schema.EffectiveOf(direct), null);
        }

        var declaring = Forms(schema, holder)
            .Where(f => f.Schema["properties"] is JsonObject props && props[name] is JsonObject)
            .ToList();
        if (declaring.Count > 0)
        {
            var raw = (JsonObject)((JsonObject)declaring[0].Schema["properties"]!)[name]!;
            var titles = declaring.Select(f => f.Title).Distinct(StringComparer.Ordinal).ToList();
            var all = Forms(schema, holder).Count();
            return (raw, schema.EffectiveOf(raw), titles.Count == all ? null : string.Join(", ", titles));
        }

        if (holder["additionalProperties"] is JsonObject free)
        {
            return (free, schema.EffectiveOf(free), null);
        }

        if (holder["additionalProperties"] is JsonValue flag && flag.TryGetValue<bool>(out var allowed) && allowed)
        {
            return (new JsonObject(), new JsonObject(), null);
        }

        return null;
    }

    /// <summary>Every form a <c>oneOf</c> or <c>anyOf</c> of <paramref name="holder"/> allows, forms of forms included, each with its title.</summary>
    private static IEnumerable<(JsonObject Schema, string Title)> Forms(SchemaSnapshot schema, JsonObject holder) => schema.FormsOf(holder);

    /// <summary>
    /// The type a node declares; for one that declares none but allows a choice of forms (a wellbore's geographic context),
    /// the type every form shares.
    /// </summary>
    private static SchemaType TypeOf(SchemaSnapshot schema, JsonObject node) => schema.TypeWithForms(node);

    private static SchemaFilterReading Filter(SchemaSnapshot schema, JsonObject holds, DimensionPathSegment segment)
    {
        var compared = Find(schema, holds, segment.FilterProperty!);
        var description = compared is { } c ? Text(c.Raw, "description") ?? Text(c.Own, "description") : null;
        return new SchemaFilterReading(segment.FilterProperty!, Compare(segment.Compare), segment.FilterValue!, compared is not null, description);
    }

    private static string Missing(SchemaSnapshot schema, JsonObject holder, string at, string name)
    {
        var forms = Forms(schema, holder).Select(f => f.Title).Distinct(StringComparer.Ordinal).ToList();
        if (forms.Count > 0)
        {
            return $"{schema.Kind} declares no {name} at {at}: the object there is one of {forms.Count} forms ({string.Join(", ", forms)}), and none declares it.";
        }

        var known = holder["properties"] is JsonObject props ? props.Select(p => p.Key).ToList() : [];
        return known.Count == 0
            ? $"{schema.Kind} declares no {name} at {at}: the value there holds no properties."
            : $"{schema.Kind} declares no {name} at {at}.";
    }

    private static SchemaSegmentReading Unknown(DimensionPathSegment segment)
        => new(segment.Name, segment.FilterProperty is null ? null : new SchemaFilterReading(segment.FilterProperty, Compare(segment.Compare), segment.FilterValue!, false, null),
            Found: false, null, null, null, null, null, null, [], null);

    /// <summary>How a filter compares, as the API names it.</summary>
    public static string Compare(DimensionPathCompare compare) => compare switch
    {
        DimensionPathCompare.Contains => "contains",
        DimensionPathCompare.EndsWith => "endsWith",
        _ => "equals",
    };

    private static string Name(SchemaType type) => type switch
    {
        SchemaType.String => "string",
        SchemaType.Number => "number",
        SchemaType.Integer => "integer",
        SchemaType.Boolean => "boolean",
        SchemaType.Object => "object",
        SchemaType.Array => "array",
        _ => "any",
    };

    private static string? Text(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}
