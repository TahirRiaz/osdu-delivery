using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// One segment of a path a label or an attribute is read through: a property name, and optionally a filter on the objects
/// it holds, so an array of objects is narrowed to the ones that match (<c>GeoContexts[GeoTypeID*=Country]</c>).
/// </summary>
/// <param name="Name">The property.</param>
/// <param name="FilterProperty">The property of each object the filter compares; null for no filter.</param>
/// <param name="FilterValue">The text the property is compared with.</param>
/// <param name="Compare">How the property is compared with the text.</param>
public sealed record DimensionPathSegment(string Name, string? FilterProperty, string? FilterValue, DimensionPathCompare Compare)
{
    /// <summary>Whether <paramref name="item"/> is one the filter keeps: any value its filter property holds matches.</summary>
    public bool Keeps(JsonNode? item)
    {
        if (FilterProperty is null)
        {
            return true;
        }

        if (item is not JsonObject obj || !obj.TryGetPropertyValue(FilterProperty, out var held))
        {
            return false;
        }

        var values = held is JsonArray array ? array.Select(n => n) : [held];
        return values.Any(v => v is JsonValue value && value.TryGetValue<string>(out var text) && Compare switch
        {
            DimensionPathCompare.Contains => text.Contains(FilterValue!, StringComparison.OrdinalIgnoreCase),
            DimensionPathCompare.EndsWith => text.EndsWith(FilterValue!, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(text, FilterValue, StringComparison.Ordinal),
        });
    }
}

/// <summary>How a path filter compares an object's property with its text.</summary>
public enum DimensionPathCompare
{
    /// <summary><c>[Property=text]</c>: equal, exactly.</summary>
    Equals,

    /// <summary><c>[Property*=text]</c>: containing it, ignoring case.</summary>
    Contains,

    /// <summary><c>[Property$=text]</c>: ending with it, ignoring case (<c>[GeoPoliticalEntityTypeID$=:Country:]</c>).</summary>
    EndsWith,
}

/// <summary>
/// A path a label or an attribute is read through, from a record's root (<c>data.FacilityName</c>), with a filter on any
/// segment holding objects (<c>data.GeoContexts[GeoTypeID*=Country].GeoPoliticalEntityID</c>: of the wellbore's geographic
/// contexts, the one whose type contains <c>Country</c>). A filter is <c>[Property=text]</c>, the property equal to the text
/// exactly, <c>[Property*=text]</c>, the property containing it ignoring case, or <c>[Property$=text]</c>, the property
/// ending with it ignoring case; the text holds no <c>]</c>. An array met on the way is stepped into, and a filter keeps the
/// objects that match.
/// </summary>
public sealed class DimensionPath
{
    private DimensionPath(string text, IReadOnlyList<DimensionPathSegment> segments)
    {
        Text = text;
        Segments = segments;
    }

    /// <summary>The path as it is written.</summary>
    public string Text { get; }

    public IReadOnlyList<DimensionPathSegment> Segments { get; }

    /// <summary>
    /// What a search is asked to return to read the path: the path without its filters, and the property each filter
    /// compares, so a record comes back holding what the path and its filters read.
    /// </summary>
    public IReadOnlyList<string> ReturnedFields
    {
        get
        {
            var names = Segments.Select(s => s.Name).ToList();
            var fields = new List<string> { string.Join('.', names) };
            for (var i = 0; i < Segments.Count; i++)
            {
                if (Segments[i].FilterProperty is { } property)
                {
                    fields.Add(string.Join('.', names.Take(i + 1)) + "." + property);
                }
            }

            return fields;
        }
    }

    /// <summary>The path <paramref name="text"/> writes, or null with why it is none.</summary>
    public static (DimensionPath? Path, string? Problem) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, "it is empty");
        }

        var trimmed = text.Trim();
        var segments = new List<DimensionPathSegment>();
        var at = 0;
        while (at < trimmed.Length)
        {
            var start = at;
            while (at < trimmed.Length && (char.IsAsciiLetterOrDigit(trimmed[at]) || trimmed[at] == '_'))
            {
                at++;
            }

            if (at == start)
            {
                return (null, "a segment is not a property name: letters, digits and underscores");
            }

            var name = trimmed[start..at];
            string? property = null;
            string? value = null;
            var compare = DimensionPathCompare.Equals;
            if (at < trimmed.Length && trimmed[at] == '[')
            {
                var close = trimmed.IndexOf(']', at);
                if (close < 0)
                {
                    return (null, $"the filter after '{name}' has no closing ]");
                }

                var filter = trimmed[(at + 1)..close];
                var equals = filter.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0)
                {
                    return (null, $"the filter [{filter}] is not [Property=text], [Property*=text] or [Property$=text]");
                }

                compare = filter[equals - 1] switch
                {
                    '*' => DimensionPathCompare.Contains,
                    '$' => DimensionPathCompare.EndsWith,
                    _ => DimensionPathCompare.Equals,
                };
                property = filter[..(compare == DimensionPathCompare.Equals ? equals : equals - 1)].Trim();
                value = filter[(equals + 1)..].Trim();
                if (property.Length == 0 || !property.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                {
                    return (null, $"the filter [{filter}] compares '{property}', which is not a property name");
                }

                if (value.Length == 0)
                {
                    return (null, $"the filter [{filter}] compares with nothing");
                }

                at = close + 1;
            }

            segments.Add(new DimensionPathSegment(name, property, value, compare));
            if (at == trimmed.Length)
            {
                break;
            }

            if (trimmed[at] != '.' || at == trimmed.Length - 1)
            {
                return (null, "segments are separated by one dot, and the path does not end with one");
            }

            at++;
        }

        return (new DimensionPath(trimmed, segments), null);
    }

    /// <summary>Every scalar the path reaches in <paramref name="node"/>, as text: arrays on the way stepped into, filters applied.</summary>
    public List<string> Read(JsonNode? node)
    {
        var found = new List<string>();
        Collect(node, 0, found);
        return found;
    }

    private void Collect(JsonNode? node, int at, List<string> into)
    {
        switch (node)
        {
            case null:
                return;
            case JsonArray array:
                foreach (var item in array)
                {
                    Collect(item, at, into);
                }

                return;
            case JsonObject obj when at < Segments.Count:
                var segment = Segments[at];
                if (!obj.TryGetPropertyValue(segment.Name, out var child) || child is null)
                {
                    return;
                }

                if (segment.FilterProperty is null)
                {
                    Collect(child, at + 1, into);
                    return;
                }

                foreach (var item in child is JsonArray items ? items.Select(i => i) : [child])
                {
                    if (segment.Keeps(item))
                    {
                        Collect(item, at + 1, into);
                    }
                }

                return;
            case JsonValue value when at == Segments.Count:
                var text = value.TryGetValue<string>(out var s) ? s : value.ToJsonString();
                if (!string.IsNullOrEmpty(text))
                {
                    into.Add(text);
                }

                return;
            default:
                return;
        }
    }
}
