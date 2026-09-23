using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Hashing;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// A versioned, immutable capture of the OSDU reference and master data a mapping resolves against (design.md
/// section 6.2), with the partition's own system properties as the platform reported them at the capture. Replaces the
/// per-replica in-memory cache: every render under one version sees the same items and the same properties.
/// </summary>
public sealed class ReferenceSnapshot
{
    private readonly Dictionary<string, ReferenceType> _types;

    /// <param name="version">The version label.</param>
    /// <param name="capturedUtc">When the capture was made.</param>
    /// <param name="types">The cached types with their records.</param>
    /// <param name="systemProperties">The partition's system properties; none for a cache no capture has asked the platform about.</param>
    public ReferenceSnapshot(string version, DateTimeOffset capturedUtc, IEnumerable<ReferenceType> types, IEnumerable<SystemProperty>? systemProperties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(types);
        Version = version;
        CapturedUtc = capturedUtc;
        _types = types.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        SystemProperties = Snapshots.SystemProperties.Ordered(systemProperties ?? []);
    }

    public string Version { get; }

    public DateTimeOffset CapturedUtc { get; }

    public IReadOnlyCollection<ReferenceType> Types => _types.Values;

    /// <summary>
    /// The partition's system properties as the capture that wrote the version found them, by service and name. They are
    /// kept apart from the types: they describe how the platform indexes and searches the partition, not any record of it.
    /// </summary>
    public IReadOnlyList<SystemProperty> SystemProperties { get; }

    public bool HasType(string name) => _types.ContainsKey(name);

    public ReferenceType? Type(string name) => _types.GetValueOrDefault(name);

    /// <summary>The system property <paramref name="name"/> of <paramref name="service"/>, or null when the version does not name it.</summary>
    public SystemProperty? SystemPropertyOf(string service, string name)
        => SystemProperties.FirstOrDefault(p => string.Equals(p.Service, service, StringComparison.Ordinal) && string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>An empty snapshot, for mappings that resolve no references.</summary>
    public static ReferenceSnapshot Empty { get; } = new("none", DateTimeOffset.UnixEpoch, []);

    /// <summary>
    /// Hash of the whole snapshot content, so a version label can be checked against what it holds. The system properties
    /// enter it by service, name and state, which is what a render reads of them, and not by the words a service or a
    /// failed read explained them with, which change without the property changing. A version without system properties
    /// hashes as every version did before a capture recorded them, so the versions written then still load.
    /// </summary>
    public string ContentHash()
    {
        var doc = new JsonObject();
        foreach (var type in _types.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            doc[type.Name] = type.ToJson();
        }

        if (SystemProperties.Count == 0)
        {
            return Hashing.ContentHash.Of(CanonicalJson.ToBytes(doc));
        }

        var properties = new JsonArray();
        foreach (var property in SystemProperties)
        {
            properties.Add(new JsonObject
            {
                ["service"] = property.Service,
                ["name"] = property.Name,
                ["state"] = property.State.ToString(),
            });
        }

        return Hashing.ContentHash.Of(CanonicalJson.ToBytes(new JsonObject { ["types"] = doc, ["systemProperties"] = properties }));
    }

    /// <summary>
    /// The same content with every type's records in ordinal order of their ids: the order a version is stored and loaded
    /// in, so the content hash of what a capture found and of what the catalog holds for it can be compared.
    /// </summary>
    public ReferenceSnapshot Normalized()
        => new(
            Version,
            CapturedUtc,
            _types.Values.Select(t => new ReferenceType(t.Name, t.EntityType, t.Items.OrderBy(i => i.Id, StringComparer.Ordinal), t.Key)),
            SystemProperties);
}

/// <summary>All items of one reference (or master-data) type, indexed on the fields a mapping may match by.</summary>
public sealed class ReferenceType
{
    /// <summary>
    /// The entity type prefix of a type whose records are not OSDU records: a lookup table filled from an ingestion table or
    /// a dictionary file, or a replace's own table. No OSDU group is called lookup, so it can never be mistaken for one.
    /// </summary>
    public const string LookupEntityTypePrefix = "lookup--";

    private readonly List<ReferenceItem> _items;

    // Built lazily per field because a snapshot holds more fields than any one mapping matches by, and concurrently
    // because one snapshot is shared by every render worker of a run. A GetOrAdd race builds the index twice and
    // keeps one; the loser is discarded, which is wasted work rather than a wrong answer.
    private readonly ConcurrentDictionary<string, FieldIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string>? _fieldNames;
    private Dictionary<string, ReferenceItem>? _byId;

    /// <param name="name">The short name mappings use.</param>
    /// <param name="entityType">The OSDU entity type, or <see cref="LookupEntityType"/> of the name for a lookup table.</param>
    /// <param name="items">The records.</param>
    /// <param name="key">For a lookup table, the name its key is kept under; null for a type of OSDU records.</param>
    public ReferenceType(string name, string entityType, IEnumerable<ReferenceItem> items, string? key = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentNullException.ThrowIfNull(items);
        Name = name;
        EntityType = entityType;
        _items = items.ToList();
        var lookup = entityType.StartsWith(LookupEntityTypePrefix, StringComparison.Ordinal);
        if (lookup != key is not null)
        {
            throw new DeliveryException(lookup
                ? $"Lookup table '{name}' ({entityType}) names no key; every lookup row is kept under its key."
                : $"Type '{name}' holds OSDU records ({entityType}), which are kept under their ids, so it names no key.");
        }

        Key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    /// <summary>The entity type a lookup table named <paramref name="name"/> is kept under.</summary>
    public static string LookupEntityType(string name) => LookupEntityTypePrefix + name;

    /// <summary>Short name used by mappings (UnitOfMeasure, Wellbore).</summary>
    public string Name { get; }

    /// <summary>OSDU entity type (reference-data--UnitOfMeasure, master-data--Wellbore), or lookup--&lt;Name&gt; for a lookup table.</summary>
    public string EntityType { get; }

    /// <summary>
    /// For a lookup table, the name its key is kept under: each row's id is its key, and the key is a field under this name
    /// too, so a mapping matches on it by name. Null for a type of OSDU records.
    /// </summary>
    public string? Key { get; }

    /// <summary>True for a table whose rows are not OSDU records: filled from an ingestion table or a dictionary, it has no ids to write.</summary>
    public bool IsLookup => Key is not null;

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
    /// The item whose <paramref name="field"/> holds <paramref name="value"/>, as <see cref="Find"/> matches it: null
    /// when nothing matches, and when the value names several items.
    /// </summary>
    public ReferenceItem? Match(string field, string value, bool ignoreSeparators = false)
        => Find(field, value, ignoreSeparators).Item;

    /// <summary>
    /// Matches <paramref name="value"/> (trimmed) against what <paramref name="field"/> holds. A field holding a set
    /// matches when any one of its values does, so an item with three aliases is found by any of them.
    /// <para>
    /// An exact match of exactly one item wins. OSDU codes that differ only by case are different records (<c>ft</c> is
    /// the foot and <c>fT</c> the femtotesla; <c>s/m</c> is second per metre and <c>S/m</c> siemens per metre), so case is
    /// ignored only when that finds exactly one item. A value that names several items, exactly or once case is ignored,
    /// matches none of them and lists them as <see cref="ReferenceMatch.CaseVariants"/>, instead of resolving to
    /// whichever comes first: picking one would write a reference nobody chose.
    /// </para>
    /// <para>
    /// With <paramref name="ignoreSeparators"/> a third and last attempt folds punctuation and spacing away on both
    /// sides (<see cref="ReferenceKeyFold"/>), so a name a source writes as <c>NO 15/9-19 SR</c> finds the record OSDU
    /// holds as <c>NO_15_9-19_SR</c>. It is opt-in per mapping entry, because a fold that helps a facility name is
    /// exactly wrong for a unit code, and it keeps the same discipline as the case tier: several items under one folded
    /// key match none of them and are listed, rather than one being picked.
    /// </para>
    /// </summary>
    public ReferenceMatch Find(string field, string value, bool ignoreSeparators = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        return value is null ? ReferenceMatch.None : Index(field).Lookup(value.Trim(), ignoreSeparators);
    }

    /// <summary>True when two or more items hold exactly the same value under this field, so matching on it is order-dependent.</summary>
    public bool IsAmbiguous(string field) => Index(field).Ambiguous;

    /// <summary>
    /// The item whose record id is exactly <paramref name="id"/>, whatever the type caches under a field of its own called
    /// <c>ID</c>: where a value already is an OSDU record id, the record it names is looked for by that id, never by a
    /// captured field that happens to share the name. Built once, on first use, and shared by every render worker.
    /// </summary>
    public ReferenceItem? ById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var byId = LazyInitializer.EnsureInitialized(
            ref _byId,
            () => _items.GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));
        return byId.GetValueOrDefault(id);
    }

    private FieldIndex Index(string field) => _indexes.GetOrAdd(ReferenceField.Normalize(field), BuildIndex);

    private FieldIndex BuildIndex(string field)
    {
        var exact = new Dictionary<string, ReferenceItem>(StringComparer.Ordinal);
        // Terms that more than one distinct item holds exactly, with every such item in snapshot order.
        var duplicates = new Dictionary<string, List<ReferenceItem>>(StringComparer.Ordinal);
        var folded = new Dictionary<string, List<ReferenceItem>>(StringComparer.OrdinalIgnoreCase);
        // Built with the other two rather than on demand: it costs one dictionary per indexed field, and building it
        // later would mean a second pass over every item of a type that can hold hundreds of thousands of them.
        var separatorFolded = new Dictionary<string, List<ReferenceItem>>(StringComparer.Ordinal);
        var ambiguous = false;
        var recordId = MeansRecordId(field);
        foreach (var item in _items)
        {
            if (Value(item, field) is not { } value)
            {
                continue;
            }

            // Every value the field holds is a term: a scalar contributes one, a set one per element, so an item
            // with three aliases is found by any of them. The record id is found by its code as well.
            foreach (var term in recordId ? RecordIdTerms(item.Id) : value.Terms)
            {
                if (!exact.TryAdd(term, item) && exact[term] != item)
                {
                    ambiguous = true;
                    if (!duplicates.TryGetValue(term, out var holders))
                    {
                        holders = [exact[term]];
                        duplicates[term] = holders;
                    }

                    if (!holders.Contains(item))
                    {
                        holders.Add(item);
                    }
                }

                // The same term under folded case, every distinct item that holds it, in snapshot order: a lookup
                // that has no exact match takes it only when there is exactly one.
                if (!folded.TryGetValue(term, out var variants))
                {
                    variants = [];
                    folded[term] = variants;
                }

                if (!variants.Contains(item))
                {
                    variants.Add(item);
                }

                // A term that folds to nothing (punctuation only) would collect every such term under one empty key and
                // make the fold tier useless, so it contributes nothing to it.
                if (ReferenceKeyFold.Separators(term) is { Length: > 0 } key)
                {
                    if (!separatorFolded.TryGetValue(key, out var byKey))
                    {
                        byKey = [];
                        separatorFolded[key] = byKey;
                    }

                    if (!byKey.Contains(item))
                    {
                        byKey.Add(item);
                    }
                }
            }
        }

        return new FieldIndex(exact, duplicates, folded, separatorFolded, ambiguous);
    }

    /// <summary>
    /// The terms a record is found by through its id: the id itself, and the code it ends with, both as the id writes it
    /// (<c>Gamma%20Ray</c>) and decoded (<c>Gamma Ray</c>). A reference to OSDU reference data is the partition, the entity
    /// type and that code (<c>{partition}:reference-data--LogCurveFamily:Gamma%20Ray:</c>), so a table that names reference
    /// data by its code (a curve dictionary giving each mnemonic its family) finds the one record of the type it names.
    /// </summary>
    private static IEnumerable<string> RecordIdTerms(string id)
    {
        yield return id;
        var parts = id.Split(':');
        if (parts.Length != 3 || parts[2].Length == 0)
        {
            yield break;
        }

        var code = parts[2];
        yield return code;
        // A malformed escape is left as the id writes it.
        var decoded = Uri.UnescapeDataString(code);
        if (!string.Equals(decoded, code, StringComparison.Ordinal))
        {
            yield return decoded;
        }
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

        // The key is written only for a lookup table, so a type of OSDU records hashes as it always has and every version
        // written before lookup tables existed still loads.
        var json = new JsonObject { ["entityType"] = EntityType };
        if (Key is not null)
        {
            json["key"] = Key;
        }

        json["items"] = items;
        return json;
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

        return new ReferenceType(name, entityType, items, node["key"]?.GetValue<string>());
    }

    private sealed class FieldIndex
    {
        private readonly Dictionary<string, ReferenceItem> _exact;
        private readonly Dictionary<string, List<ReferenceItem>> _duplicates;
        private readonly Dictionary<string, List<ReferenceItem>> _folded;
        private readonly Dictionary<string, List<ReferenceItem>> _separatorFolded;

        public FieldIndex(
            Dictionary<string, ReferenceItem> exact, Dictionary<string, List<ReferenceItem>> duplicates, Dictionary<string, List<ReferenceItem>> folded,
            Dictionary<string, List<ReferenceItem>> separatorFolded, bool ambiguous)
        {
            _exact = exact;
            _duplicates = duplicates;
            _folded = folded;
            _separatorFolded = separatorFolded;
            Ambiguous = ambiguous;
        }

        public int Count => _exact.Count;

        public bool Ambiguous { get; }

        public ReferenceMatch Lookup(string term, bool ignoreSeparators)
        {
            if (_duplicates.TryGetValue(term, out var holders))
            {
                return new ReferenceMatch(null, holders, ReferenceMatchKind.Exact);
            }

            if (_exact.TryGetValue(term, out var item))
            {
                return ReferenceMatch.Of(item, ReferenceMatchKind.Exact);
            }

            if (_folded.TryGetValue(term, out var variants))
            {
                return variants.Count == 1
                    ? ReferenceMatch.Of(variants[0], ReferenceMatchKind.IgnoringCase)
                    : new ReferenceMatch(null, variants, ReferenceMatchKind.IgnoringCase);
            }

            // Only now, and only when the mapping asked: the looser a tier is, the later it runs, so a value that
            // resolves exactly is never decided by a fold.
            if (!ignoreSeparators || ReferenceKeyFold.Separators(term) is not { Length: > 0 } key)
            {
                return ReferenceMatch.None;
            }

            if (!_separatorFolded.TryGetValue(key, out var folded))
            {
                return ReferenceMatch.None;
            }

            return folded.Count == 1
                ? ReferenceMatch.Of(folded[0], ReferenceMatchKind.IgnoringSeparators)
                : new ReferenceMatch(null, folded, ReferenceMatchKind.IgnoringSeparators);
        }
    }
}

/// <summary>
/// How a value was matched against a cached field, loosest last. It is reported so a refusal can say which tier could
/// not decide, and so a caller can tell a name that matched as written from one that matched only after folding.
/// </summary>
public enum ReferenceMatchKind
{
    /// <summary>Nothing matched.</summary>
    None,

    /// <summary>The value is what the field holds, character for character.</summary>
    Exact,

    /// <summary>The value matches what the field holds once case is ignored.</summary>
    IgnoringCase,

    /// <summary>The value matches once case, punctuation and spacing are ignored. Only ever reached by a mapping that asked for it.</summary>
    IgnoringSeparators,
}

/// <summary>
/// The folded form of a name, for matching one system's spelling of it against another's.
/// <para>
/// Source systems and OSDU write the same facility name differently, because each grew its own convention for the
/// spaces, slashes, underscores and hyphens between the parts that carry the meaning: <c>NO 15/9-19 SR</c>,
/// <c>NO_15_9-19_SR</c> and <c>no-15-9-19-sr</c> all name one wellbore. Folding keeps the letters and digits, in order,
/// and replaces every run of anything else with a single separator, so those three fold to one key while two genuinely
/// different names stay apart.
/// </para>
/// <para>
/// It is not a general normaliser and deliberately does nothing clever: no transliteration, no accent stripping, no
/// abbreviation. Letters outside ASCII are kept (Norwegian names carry æ, ø and å), lower-cased invariantly, so folding
/// never depends on the machine's locale.
/// </para>
/// </summary>
public static class ReferenceKeyFold
{
    /// <summary>
    /// The folded key of <paramref name="value"/>, or the empty string when it carries no letter or digit at all (a
    /// value made only of punctuation folds to nothing, and nothing is not a key anything should match on).
    /// </summary>
    public static string Separators(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSeparator = false;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingSeparator = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// What matching a value against one cached field found: the item it names, or nothing. When the value names several
/// items, <see cref="Item"/> is null and <see cref="CaseVariants"/> lists them in snapshot order, so the caller can say
/// which records it could not choose between.
/// </summary>
public readonly record struct ReferenceMatch(
    ReferenceItem? Item, IReadOnlyList<ReferenceItem> CaseVariants, ReferenceMatchKind Kind = ReferenceMatchKind.None)
{
    public static ReferenceMatch None => new(null, [], ReferenceMatchKind.None);

    /// <summary>Several items answer to the value, exactly or once a tier loosened the comparison, so none of them is taken.</summary>
    public bool IsCaseAmbiguous => Item is null && CaseVariants.Count > 1;

    /// <summary>How the tier that could not decide was comparing, for a refusal that says what it tried.</summary>
    public string Loosening => Kind switch
    {
        ReferenceMatchKind.Exact => "exactly",
        ReferenceMatchKind.IgnoringSeparators => "once case, punctuation and spacing are ignored",
        _ => "once case is ignored",
    };

    public static ReferenceMatch Of(ReferenceItem item, ReferenceMatchKind kind = ReferenceMatchKind.Exact) => new(item, [], kind);
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
