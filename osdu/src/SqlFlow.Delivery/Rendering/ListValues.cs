using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// What a record the engine writes holds in the lists its template declares (docs: documents.md, What the record contains).
/// OSDU takes no null where a schema does not allow one, and the engine never writes one: a value with no value is left out
/// of the record, and an item of a list that is null (a list read whole from the cache may hold one) is dropped where the
/// template's items take no null. Every object the record holds that its template describes is looked into (the record
/// itself, its data, an object inside them, an item of a list of objects). A list the record leaves out stays out, as any
/// other value does: Storage keeps a record's data as it is sent, so an absent list never reads back as null, and an empty
/// one would only add to the record. A value that takes one of several forms (<c>oneOf</c>, <c>anyOf</c>) is not looked
/// into, since which form it takes is the value's own.
/// </summary>
/// <remarks>
/// What each object of the template declares is worked out once per schema path and kept, so a render walks only the
/// objects its record holds. One instance serves the threads a plan renders on.
/// </remarks>
internal sealed class ListValues
{
    private readonly SchemaSnapshot _schema;
    private readonly ConcurrentDictionary<string, Level> _levels = new(StringComparer.Ordinal);

    public ListValues(SchemaSnapshot schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schema = schema;
    }

    /// <summary>
    /// Drops the null items of each list of <paramref name="record"/> whose template items take no null, where
    /// <paramref name="covers"/> answers true for the list's template path (<c>osdu.data.Curves[].NameAliases</c>); null
    /// covers every list.
    /// </summary>
    public void RemoveNullItems(JsonObject record, Func<string, bool>? covers = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        RemoveNullItems(record, string.Empty, TemplatePath.Prefix, covers);
    }

    private void RemoveNullItems(JsonObject value, string schemaPath, string target, Func<string, bool>? covers)
    {
        var level = _levels.GetOrAdd(schemaPath, Declared);
        foreach (var name in level.NoNullItems)
        {
            if (value[name] is not JsonArray items || (covers is not null && !covers($"{target}.{name}")))
            {
                continue;
            }

            for (var i = items.Count - 1; i >= 0; i--)
            {
                if (items[i] is null)
                {
                    items.RemoveAt(i);
                }
            }
        }

        foreach (var (name, intoItems) in level.Inner)
        {
            var path = Join(schemaPath, name);
            switch (value[name])
            {
                case JsonObject inner when !intoItems:
                    RemoveNullItems(inner, path, $"{target}.{name}", covers);
                    break;
                case JsonArray items when intoItems:
                    var itemTarget = $"{target}.{name}[]";
                    foreach (var item in items)
                    {
                        if (item is JsonObject each)
                        {
                            RemoveNullItems(each, path, itemTarget, covers);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>What the object at <paramref name="schemaPath"/> declares: its lists whose items take no null, and the objects and lists of objects to look into.</summary>
    private Level Declared(string schemaPath)
    {
        var noNullItems = new List<string>();
        var inner = new List<(string, bool)>();
        foreach (var name in _schema.PropertiesAt(schemaPath))
        {
            if (_schema.Resolve(Join(schemaPath, name)) is not { } property)
            {
                continue;
            }

            switch (property.Type)
            {
                case SchemaType.Array:
                    if (!TakesNull(property.Items))
                    {
                        noNullItems.Add(name);
                    }

                    if (property.Items?["properties"] is JsonObject)
                    {
                        inner.Add((name, true));
                    }

                    break;
                case SchemaType.Object when property.Schema["properties"] is JsonObject:
                    inner.Add((name, false));
                    break;
            }
        }

        return new Level(noNullItems, inner);
    }

    /// <summary>
    /// Whether an item schema allows null: it names <c>null</c> among its types, names no type at all, or takes one of
    /// several forms of which one does. No item schema allows anything.
    /// </summary>
    private bool TakesNull(JsonObject? items)
    {
        if (items is null)
        {
            return true;
        }

        if (DeclaredTypes(items) is { } types)
        {
            return types.Contains("null", StringComparer.Ordinal);
        }

        if (items["oneOf"] is not JsonArray && items["anyOf"] is not JsonArray)
        {
            return true;
        }

        // A form that only holds forms of its own defers to them, and they are listed after it.
        var forms = _schema.FormsOf(items).Select(f => f.Schema).ToList();
        return forms.Count == 0 || forms.Any(form => DeclaredTypes(form) is { } named
            ? named.Contains("null", StringComparer.Ordinal)
            : form["oneOf"] is not JsonArray && form["anyOf"] is not JsonArray);
    }

    /// <summary>The types a schema names, or null when it names none.</summary>
    private static IReadOnlyList<string>? DeclaredTypes(JsonObject schema) => schema["type"] switch
    {
        JsonValue single when single.TryGetValue<string>(out var type) => [type],
        JsonArray many => many.OfType<JsonValue>().Select(t => t.TryGetValue<string>(out var name) ? name : null).OfType<string>().ToList(),
        _ => null,
    };

    private static string Join(string schemaPath, string name) => schemaPath.Length == 0 ? name : $"{schemaPath}.{name}";

    /// <param name="NoNullItems">The lists the object declares whose items take no null, so a null item is dropped from them.</param>
    /// <param name="Inner">The objects (false) and lists of objects (true) the object declares, whose own lists are looked into in turn.</param>
    private sealed record Level(IReadOnlyList<string> NoNullItems, IReadOnlyList<(string Name, bool IntoItems)> Inner);
}
