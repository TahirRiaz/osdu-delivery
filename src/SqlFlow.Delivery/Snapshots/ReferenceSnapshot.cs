using System.Text.Json;
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
}

/// <summary>All items of one reference (or master-data) type, indexed on the fields a mapping may match by.</summary>
public sealed class ReferenceType
{
    private readonly List<ReferenceItem> _items;
    private readonly Dictionary<string, Dictionary<string, ReferenceItem>> _indexes = new(StringComparer.OrdinalIgnoreCase);

    public ReferenceType(string name, string entityType, IEnumerable<ReferenceItem> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        Name = name;
        EntityType = entityType;
        _items = items.ToList();
    }

    /// <summary>Short name used by mappings (UnitOfMeasure, Wellbore).</summary>
    public string Name { get; }

    /// <summary>OSDU entity type (reference-data--UnitOfMeasure, master-data--Wellbore).</summary>
    public string EntityType { get; }

    public IReadOnlyList<ReferenceItem> Items => _items;

    /// <summary>
    /// Finds the item whose <paramref name="field"/> equals <paramref name="value"/> (case-insensitive, trimmed).
    /// Ambiguous matches resolve to the first item in snapshot order, which is stable per version.
    /// </summary>
    public ReferenceItem? Match(string field, string value)
    {
        if (!_indexes.TryGetValue(field, out var index))
        {
            index = new Dictionary<string, ReferenceItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items)
            {
                var v = field.Equals("id", StringComparison.OrdinalIgnoreCase) ? item.Id : item.Fields.GetValueOrDefault(field);
                if (!string.IsNullOrWhiteSpace(v))
                {
                    index.TryAdd(v.Trim(), item);
                }
            }

            _indexes[field] = index;
        }

        return index.GetValueOrDefault(value.Trim());
    }

    public JsonObject ToJson()
    {
        var items = new JsonArray();
        foreach (var item in _items)
        {
            var o = new JsonObject { ["id"] = item.Id };
            foreach (var f in item.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                o[f.Key] = f.Value;
            }

            items.Add(o);
        }

        return new JsonObject { ["entityType"] = EntityType, ["items"] = items };
    }

    public static ReferenceType FromJson(string name, JsonObject node)
    {
        var entityType = node["entityType"]?.GetValue<string>() ?? throw new DeliveryException($"Reference type '{name}' has no entityType.");
        var items = new List<ReferenceItem>();
        if (node["items"] is JsonArray arr)
        {
            foreach (var element in arr.OfType<JsonObject>())
            {
                var id = element["id"]?.GetValue<string>() ?? throw new DeliveryException($"Reference type '{name}' has an item without id.");
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in element)
                {
                    if (kv.Key != "id" && kv.Value is JsonValue v)
                    {
                        fields[kv.Key] = v.TryGetValue<string>(out var s) ? s : v.ToJsonString();
                    }
                }

                items.Add(new ReferenceItem(id, fields));
            }
        }

        return new ReferenceType(name, entityType, items);
    }
}

/// <summary>One reference item: the OSDU record id (without version) and the fields a mapping may match on.</summary>
public sealed record ReferenceItem(string Id, IReadOnlyDictionary<string, string> Fields);
