using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// What a record the engine writes holds where its template declares a list (docs: documents.md, What the record contains).
/// OSDU takes no null where a schema does not allow one, and the engine never writes one: a value of any other type that has
/// no value is left out, since an empty text, a zero or an empty object would be a value of its own. A list is the one type
/// with an empty value that says nothing, and Storage holds a list of its own record (<c>meta</c>) that a record leaves out
/// as null, so a list is never left out. In every object the record holds that its template describes (the record itself,
/// its data, an object inside them, an item of a list of objects), each list the template declares and the object does not
/// carry, or carries as null, is written empty, and an item of a list that is null is dropped where the template's items
/// take no null. An object the record does not hold is not made for its lists. A list the template requires items of
/// (<c>minItems</c>) is left out, since an empty one would break it, and a value that takes one of several forms
/// (<c>oneOf</c>, <c>anyOf</c>) is not looked into, since which form it takes is the value's own.
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
    /// Writes each list <paramref name="record"/> leaves out as an empty list and drops the null items its lists may not
    /// hold, where <paramref name="covers"/> answers true for the list's template path (<c>osdu.data.Curves[].NameAliases</c>);
    /// null covers every list.
    /// </summary>
    public void Complete(JsonObject record, Func<string, bool>? covers = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        Complete(record, string.Empty, TemplatePath.Prefix, covers);
    }

    private void Complete(JsonObject value, string schemaPath, string target, Func<string, bool>? covers)
    {
        var level = _levels.GetOrAdd(schemaPath, Declared);
        foreach (var list in level.Lists)
        {
            if (covers is not null && !covers($"{target}.{list.Name}"))
            {
                continue;
            }

            switch (value[list.Name])
            {
                case null when list.Empty:
                    value[list.Name] = new JsonArray();
                    break;
                case JsonArray items when !list.ItemsTakeNull:
                    for (var i = items.Count - 1; i >= 0; i--)
                    {
                        if (items[i] is null)
                        {
                            items.RemoveAt(i);
                        }
                    }

                    break;
            }
        }

        foreach (var (name, intoItems) in level.Inner)
        {
            var path = Join(schemaPath, name);
            switch (value[name])
            {
                case JsonObject inner when !intoItems:
                    Complete(inner, path, $"{target}.{name}", covers);
                    break;
                case JsonArray items when intoItems:
                    var itemTarget = $"{target}.{name}[]";
                    foreach (var item in items)
                    {
                        if (item is JsonObject each)
                        {
                            Complete(each, path, itemTarget, covers);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>What the object at <paramref name="schemaPath"/> declares: its lists, and the objects and lists of objects to look into.</summary>
    private Level Declared(string schemaPath)
    {
        var lists = new List<DeclaredList>();
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
                    lists.Add(new DeclaredList(name, MinItems(property.Schema) == 0, TakesNull(property.Items)));
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

        return new Level(lists, inner);
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

    /// <summary>How many items the schema requires a list to hold, 0 when it says nothing a reader can take as a count.</summary>
    private static long MinItems(JsonObject schema)
        => schema["minItems"] is JsonValue value
            && decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var count)
            && count > 0
                ? (long)decimal.Ceiling(count)
                : 0;

    /// <param name="Name">The list's property name.</param>
    /// <param name="Empty">Whether the list is written empty when the object leaves it out: false where the template requires items of it.</param>
    /// <param name="ItemsTakeNull">Whether the template's items allow null, so a null item is kept.</param>
    private sealed record DeclaredList(string Name, bool Empty, bool ItemsTakeNull);

    /// <param name="Lists">The lists the object declares.</param>
    /// <param name="Inner">The objects (false) and lists of objects (true) the object declares, whose own lists are completed in turn.</param>
    private sealed record Level(IReadOnlyList<DeclaredList> Lists, IReadOnlyList<(string Name, bool IntoItems)> Inner);
}
