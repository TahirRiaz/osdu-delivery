using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Hashing;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// A versioned, immutable capture of the OSDU reference and master data a mapping resolves against (design.md
/// section 6.2). Replaces the per-replica in-memory cache: every render under one version sees the same items.
/// </summary>
public sealed class ReferenceSnapshot
{
    private readonly Dictionary<string, ReferenceType> _types;

    public ReferenceSnapshot(string version, DateTimeOffset capturedUtc, IEnumerable<ReferenceType> types)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(types);
        Version = version;
        CapturedUtc = capturedUtc;
        _types = types.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string Version { get; }

    public DateTimeOffset CapturedUtc { get; }

    public IReadOnlyCollection<ReferenceType> Types => _types.Values;

    public bool HasType(string name) => _types.ContainsKey(name);

    public ReferenceType? Type(string name) => _types.GetValueOrDefault(name);

    /// <summary>An empty snapshot, for mappings that resolve no references.</summary>
    public static ReferenceSnapshot Empty { get; } = new("none", DateTimeOffset.UnixEpoch, []);

    /// <summary>Hash of the whole snapshot content, so a version label can be checked against what it holds.</summary>
    public string ContentHash()
    {
        var doc = new JsonObject();
        foreach (var type in _types.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            doc[type.Name] = type.ToJson();
        }

        return Hashing.ContentHash.Of(CanonicalJson.ToBytes(doc));
    }

    /// <summary>
    /// A new snapshot carrying this one's types with <paramref name="refreshed"/> replacing (or adding) the types it
    /// names. A capture that covers part of the estate mints a full version this way, so a snapshot version always
    /// describes the whole cache rather than the slice one run happened to refresh.
    /// </summary>
    public ReferenceSnapshot With(string version, DateTimeOffset capturedUtc, IEnumerable<ReferenceType> refreshed)
    {
        ArgumentNullException.ThrowIfNull(refreshed);
        var merged = new Dictionary<string, ReferenceType>(_types, StringComparer.OrdinalIgnoreCase);
        foreach (var type in refreshed)
        {
            merged[type.Name] = type;
        }

        return new ReferenceSnapshot(version, capturedUtc, merged.Values);
    }
}

/// <summary>All items of one reference (or master-data) type, indexed on the fields a mapping may match by.</summary>
public sealed class ReferenceType
{
    private readonly List<ReferenceItem> _items;

    // Built lazily per field because a snapshot holds more fields than any one mapping matches by, and concurrently
    // because one snapshot is shared by every render worker of a run. A GetOrAdd race builds the index twice and
    // keeps one; the loser is discarded, which is wasted work rather than a wrong answer.
    private readonly ConcurrentDictionary<string, FieldIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string>? _fieldNames;

    public ReferenceType(string name, string entityType, IEnumerable<ReferenceItem> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentNullException.ThrowIfNull(items);
        Name = name;
        EntityType = entityType;
        _items = items.ToList();
    }

    /// <summary>Short name used by mappings (UnitOfMeasure, Wellbore).</summary>
    public string Name { get; }

    /// <summary>OSDU entity type (reference-data--UnitOfMeasure, master-data--Wellbore).</summary>
    public string EntityType { get; }

    public IReadOnlyList<ReferenceItem> Items => _items;

    /// <summary>The captured field names, in the order a mapping would see them. <c>id</c> is always available too.</summary>
    public IReadOnlyList<string> FieldNames
    {
        get
        {
            if (_fieldNames is not null)
            {
                return _fieldNames;
            }

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items)
            {
                foreach (var name in item.Fields.Keys)
                {
                    if (seen.Add(name))
                    {
                        names.Add(name);
                    }
                }
            }

            names.Sort(StringComparer.Ordinal);
            _fieldNames = names;
            return names;
        }
    }

    /// <summary>True when at least one item carries the field (or path into a field), so a mapping can match on it.</summary>
    public bool HasField(string field) => Index(field).Count > 0;

    /// <summary>
    /// The value an item holds at <paramref name="path"/>: a cached field, a path into one, or the record id. A
    /// type that caches a field of its own called <c>ID</c> (OSDU reference data does) shadows the record id under
    /// that name, so a mapping matching by <c>ID</c> gets the cached field and one matching by <c>id</c> on a type
    /// that caches no such field gets the record id.
    /// </summary>
    public ReferenceValue? Value(ReferenceItem item, string path)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Select(path) ?? (MeansRecordId(path) ? ReferenceValue.Of(item.Id) : null);
    }

    /// <summary>True when the path names the record id rather than a cached field of the same name.</summary>
    public bool MeansRecordId(string path)
    {
        if (!ReferenceField.IsId(path))
        {
            return false;
        }

        var name = ReferenceField.Normalize(path);
        return !_items.Any(item => item.Fields.ContainsKey(name));
    }

    /// <summary>
    /// Finds the item whose <paramref name="field"/> holds <paramref name="value"/> (case-insensitive, trimmed).
    /// A field holding a set matches when any one of its values equals the value, so an item with three aliases is
    /// found by any of them. Ambiguous matches resolve to the first item in snapshot order, which is stable per
    /// version; <see cref="IsAmbiguous"/> reports where that happened.
    /// </summary>
    public ReferenceItem? Match(string field, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (value is null)
        {
            return null;
        }

        return Index(field).Lookup(value.Trim());
    }

    /// <summary>True when two or more items share a value under this field, so matching on it is order-dependent.</summary>
    public bool IsAmbiguous(string field) => Index(field).Ambiguous;

    private FieldIndex Index(string field) => _indexes.GetOrAdd(ReferenceField.Normalize(field), BuildIndex);

    private FieldIndex BuildIndex(string field)
    {
        var byTerm = new Dictionary<string, ReferenceItem>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = false;
        foreach (var item in _items)
        {
            if (Value(item, field) is not { } value)
            {
                continue;
            }

            // Every value the field holds is a term: a scalar contributes one, a set one per element, so an item
            // with three aliases is found by any of them.
            foreach (var term in value.Terms)
            {
                if (!byTerm.TryAdd(term, item))
                {
                    ambiguous = byTerm[term] != item || ambiguous;
                }
            }
        }

        return new FieldIndex(byTerm, ambiguous);
    }

    public JsonObject ToJson()
    {
        var items = new JsonArray();
        foreach (var item in _items)
        {
            var o = new JsonObject { ["id"] = item.Id };
            foreach (var f in item.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                o[f.Key] = f.Value.Node.DeepClone();
            }

            items.Add(o);
        }

        return new JsonObject { ["entityType"] = EntityType, ["items"] = items };
    }

    public static ReferenceType FromJson(string name, JsonObject node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var entityType = node["entityType"]?.GetValue<string>() ?? throw new DeliveryException($"Reference type '{name}' has no entityType.");
        var items = new List<ReferenceItem>();
        if (node["items"] is JsonArray arr)
        {
            foreach (var element in arr.OfType<JsonObject>())
            {
                var id = element["id"]?.GetValue<string>() ?? throw new DeliveryException($"Reference type '{name}' has an item without id.");
                var fields = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in element)
                {
                    // Any JSON shape survives the round trip: a scalar, a set of values, or a nested object.
                    if (kv.Key != "id" && kv.Value is { } value)
                    {
                        fields[kv.Key] = ReferenceValue.From(value);
                    }
                }

                items.Add(new ReferenceItem(id, fields));
            }
        }

        return new ReferenceType(name, entityType, items);
    }

    private sealed class FieldIndex
    {
        private readonly Dictionary<string, ReferenceItem> _byTerm;

        public FieldIndex(Dictionary<string, ReferenceItem> byTerm, bool ambiguous)
        {
            _byTerm = byTerm;
            Ambiguous = ambiguous;
        }

        public int Count => _byTerm.Count;

        public bool Ambiguous { get; }

        public ReferenceItem? Lookup(string term) => _byTerm.GetValueOrDefault(term);
    }
}

/// <summary>
/// One reference item: the OSDU record id (without version) and the captured fields a mapping may match on or read
/// values from. A field holds whatever OSDU returned at its path: a scalar, a set of values, or a nested object.
/// </summary>
public sealed record ReferenceItem(string Id, IReadOnlyDictionary<string, ReferenceValue> Fields)
{
    /// <summary>An item whose fields are all plain text, which is what most reference types capture.</summary>
    public static ReferenceItem FromText(string id, IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var values = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in fields)
        {
            values[name] = ReferenceValue.Of(value);
        }

        return new ReferenceItem(id, values);
    }

    /// <summary>
    /// The cached value at <paramref name="path"/>: a field by name, or a path into a field
    /// (<c>NameAlias.AliasName</c> where <c>NameAlias</c> was cached whole). Null when the item caches nothing
    /// there. The record id is not a cached field; <see cref="ReferenceType.Value"/> answers that too, because
    /// whether <c>id</c> means the record id depends on what the type caches.
    /// </summary>
    public ReferenceValue? Select(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalised = ReferenceField.Normalize(path);
        if (Fields.TryGetValue(normalised, out var direct))
        {
            return direct;
        }

        // The path may reach inside a field that was captured as a whole object or set.
        foreach (var prefix in ReferenceField.Prefixes(normalised))
        {
            if (Fields.TryGetValue(prefix.Field, out var value))
            {
                return value.Select(prefix.Remainder);
            }
        }

        return null;
    }
}

/// <summary>One captured value: any JSON the path yielded, with the text and terms the cache matches and renders by.</summary>
public sealed class ReferenceValue
{
    private readonly JsonNode _node;
    private IReadOnlyList<string>? _terms;

    private ReferenceValue(JsonNode node) => _node = node;

    /// <summary>The captured JSON: a scalar, an array (a set of values), or an object.</summary>
    public JsonNode Node => _node;

    /// <summary>True when the value holds a set rather than a single value.</summary>
    public bool IsSet => _node is JsonArray;

    /// <summary>How many values the set holds; 1 for a scalar or an object.</summary>
    public int Count => _node is JsonArray array ? array.Count : 1;

    /// <summary>
    /// The value as one string: the scalar itself, the single element of a one-element set, or canonical JSON for
    /// anything composite. Never null, so it is always renderable and always loggable.
    /// </summary>
    public string Text => _node switch
    {
        JsonValue value => Scalar(value) ?? CanonicalJson.ToString(_node),
        JsonArray { Count: 1 } single when single[0] is JsonValue only => Scalar(only) ?? CanonicalJson.ToString(_node),
        _ => CanonicalJson.ToString(_node),
    };

    /// <summary>
    /// Every scalar the value holds, flattened out of arrays and objects, trimmed, without blanks or duplicates.
    /// These are the terms the cache indexes: one for a scalar, one per element for a set.
    /// </summary>
    public IReadOnlyList<string> Terms
    {
        get
        {
            if (_terms is not null)
            {
                return _terms;
            }

            var terms = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Flatten(_node, terms, seen);
            _terms = terms;
            return terms;
        }
    }

    /// <summary>The value at a path inside this one, or null when it holds nothing there.</summary>
    public ReferenceValue? Select(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var hits = JsonPathReader.SelectNodes(_node, path);
        return hits.Count switch
        {
            0 => null,
            1 => From(hits[0]),
            _ => new ReferenceValue(new JsonArray(hits.Select(h => h.DeepClone()).ToArray())),
        };
    }

    /// <summary>Captures a node as a value. The node is cloned, so the snapshot never aliases the response it came from.</summary>
    public static ReferenceValue From(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new ReferenceValue(node.DeepClone());
    }

    /// <summary>Captures the nodes a path selected: one value, or a set when the path fanned out.</summary>
    public static ReferenceValue OfMany(IReadOnlyList<JsonNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return nodes.Count == 1
            ? From(nodes[0])
            : new ReferenceValue(new JsonArray(nodes.Select(n => n.DeepClone()).ToArray()));
    }

    /// <summary>Captures plain text.</summary>
    public static ReferenceValue Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ReferenceValue(JsonValue.Create(text));
    }

    public override string ToString() => Text;

    private static void Flatten(JsonNode? node, List<string> terms, HashSet<string> seen)
    {
        switch (node)
        {
            case JsonValue value:
                {
                    var text = Scalar(value);
                    if (!string.IsNullOrWhiteSpace(text) && seen.Add(text.Trim()))
                    {
                        terms.Add(text.Trim());
                    }

                    break;
                }

            case JsonArray array:
                foreach (var item in array)
                {
                    Flatten(item, terms, seen);
                }

                break;
            case JsonObject obj:
                foreach (var kv in obj)
                {
                    Flatten(kv.Value, terms, seen);
                }

                break;
        }
    }

    private static string? Scalar(JsonValue value)
    {
        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag ? "true" : "false";
        }

        return CanonicalJson.ToString(value);
    }
}

/// <summary>
/// How a mapping names a cached field. Capture declares paths (<c>data.Code</c>, <c>data.NameAlias.AliasName</c>)
/// and stores them under the path without its <c>data.</c> root, so a mapping can match by either spelling.
/// </summary>
public static class ReferenceField
{
    private const string DataPrefix = "data.";

    /// <summary>The name a captured path is stored and matched under.</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        if (trimmed.StartsWith('$'))
        {
            trimmed = trimmed[1..].TrimStart('.');
        }

        return trimmed.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase) ? trimmed[DataPrefix.Length..] : trimmed;
    }

    /// <summary>True for the record id, which every item carries outside its captured fields.</summary>
    public static bool IsId(string field) => Normalize(field).Equals("id", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The ways a dotted path can split into a captured field and a path inside it, longest field first, so
    /// <c>NameAlias.AliasName</c> resolves against a whole <c>NameAlias</c> capture.
    /// </summary>
    public static IEnumerable<(string Field, string Remainder)> Prefixes(string path)
    {
        var segments = Normalize(path).Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var take = segments.Length - 1; take >= 1; take--)
        {
            yield return (string.Join('.', segments[..take]), string.Join('.', segments[take..]));
        }
    }
}
