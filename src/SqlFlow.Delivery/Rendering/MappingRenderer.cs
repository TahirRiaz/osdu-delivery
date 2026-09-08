using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Hashing;
using ContentHash = SqlFlow.Delivery.Hashing.ContentHash;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// Interprets a mapping at render time (design.md section 4.2): walks the declared properties, applies the closed
/// transform vocabulary, coerces to the pinned schema's types, and assembles the OSDU envelope. The output is a
/// pure function of the source record and the <see cref="RenderContext"/>.
/// </summary>
public sealed class MappingRenderer
{
    private readonly MappingDefinition _mapping;
    private readonly SchemaSnapshot _schema;
    private readonly ReferenceSnapshot _references;
    private readonly RenderContext _context;
    private readonly IReadOnlyList<MappingProperty> _keyProperties;
    private readonly IReadOnlyList<string> _requiredData;

    public MappingRenderer(MappingDefinition mapping, SchemaSnapshot schema, ReferenceSnapshot references, RenderContext context)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(context);
        _mapping = mapping;
        _schema = schema;
        _references = references;
        _context = context;

        _keyProperties = mapping.Identity.NaturalKey.Select(path =>
            mapping.Properties.FirstOrDefault(p => p.Target.Equals(path, StringComparison.Ordinal))
            ?? throw new FlowValidationException($"{Where(mapping)}: identity.naturalKey names '{path}', which is not a mapped property.")).ToList();

        foreach (var key in _keyProperties)
        {
            if (string.IsNullOrWhiteSpace(key.Source))
            {
                throw new FlowValidationException($"{Where(mapping)}: natural key property '{key.Target}' has no source binding.");
            }
        }

        foreach (var (name, parameter) in mapping.Parameters)
        {
            if (parameter.Required && !context.Parameters.ContainsKey(name) && parameter.Default is null)
            {
                throw new FlowValidationException($"{Where(mapping)}: parameter '{name}' is required but the flow supplies no value under render.parameters.");
            }
        }

        _requiredData = schema.RequiredAt("data");
    }

    public MappingDefinition Mapping => _mapping;

    public RenderContext Context => _context;

    /// <summary>Source columns of the natural key, in order, so callers can derive the key without rendering.</summary>
    public IReadOnlyList<string> NaturalKeyColumns => _keyProperties.Select(p => p.Source!).ToList();

    /// <summary>
    /// The human-readable label for a row from the mapping's identity.label template ("{wellbore_uwi} {log_name}").
    /// Display only: it is stored on the ledger record for search and never enters the document or the hash.
    /// </summary>
    public string? Label(SourceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(_mapping.Identity.Label))
        {
            return null;
        }

        var label = System.Text.RegularExpressions.Regex.Replace(
            _mapping.Identity.Label,
            @"\{(?<name>[A-Za-z0-9_\-\.]+)\}",
            m => row.GetString(m.Groups["name"].Value) ?? string.Empty).Trim();
        return label.Length == 0 ? null : label.Length <= 400 ? label : label[..400];
    }

    /// <summary>Derives the delivery key from a root row without rendering anything else.</summary>
    public DeliveryKey? DeriveKey(SourceRow row, out IReadOnlyList<string?> values)
    {
        ArgumentNullException.ThrowIfNull(row);
        var list = new List<string?>();
        foreach (var column in NaturalKeyColumns)
        {
            list.Add(row.GetString(column));
        }

        values = list;
        return list.Any(string.IsNullOrWhiteSpace) ? null : DeliveryKey.Derive(_mapping.Source.System, list);
    }

    public RenderResult Render(SourceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var holds = new List<string>();

        var key = DeriveKey(record.Row, out var keyValues);
        var sourceKey = SourceKey.Display(_mapping.Source.System, keyValues);
        if (key is null)
        {
            holds.Add($"natural key incomplete ({sourceKey}): every key column must be non-empty");
        }

        if (key is { } k && record.DeclaredDeliveryKey is { } declared && declared != k.Value)
        {
            holds.Add($"drop declares deliveryKey {declared:D} but the mapping derives {k.Value:D} from {sourceKey}; the two halves disagree on identity");
        }

        var partition = _context.DataPartition;
        var document = new JsonObject
        {
            ["kind"] = _mapping.Kind,
            ["acl"] = new JsonObject
            {
                ["owners"] = ToArray(_mapping.Envelope.Acl.Owners),
                ["viewers"] = ToArray(_mapping.Envelope.Acl.Viewers),
            },
            ["legal"] = new JsonObject
            {
                ["legaltags"] = ToArray(_mapping.Envelope.LegalTags),
                ["otherRelevantDataCountries"] = ToArray(_mapping.Envelope.OtherRelevantDataCountries),
            },
            ["data"] = new JsonObject(),
        };

        string? targetId = null;
        if (key is { } dk)
        {
            targetId = TargetId.Compose(partition, _mapping.EntityType, dk);
            document["id"] = targetId;
        }

        if (_mapping.Envelope.Tags.Count > 0)
        {
            var tags = new JsonObject();
            foreach (var kv in _mapping.Envelope.Tags)
            {
                tags[kv.Key] = kv.Value;
            }

            document["tags"] = tags;
        }

        foreach (var property in _mapping.Properties)
        {
            RenderInto(document, property, record.Row, record, string.Empty, holds);
        }

        if (document["data"] is JsonObject data)
        {
            foreach (var required in _requiredData)
            {
                if (data[required] is null)
                {
                    holds.Add($"schema-required property data.{required} rendered empty");
                }
            }
        }

        var normalized = (JsonObject)CanonicalJson.Normalize(document)!;
        var canonical = CanonicalJson.ToString(normalized);
        var metadataHash = ContentHash.OfParts(canonical, _context.Canonical());

        return new RenderResult
        {
            Key = key,
            SourceKey = sourceKey,
            TargetId = targetId,
            Document = normalized,
            Canonical = canonical,
            MetadataHash = metadataHash,
            Holds = holds,
        };
    }

    /// <summary>Renders one scalar property against a row, for fixtures and diagnostics. Null means omitted.</summary>
    public JsonNode? RenderScalar(MappingProperty property, SourceRow row, string pathPrefix, List<string> holds)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(holds);
        var fullPath = Join(pathPrefix, property.Target);
        var schemaProperty = _schema.Resolve(fullPath);
        var raw = Transforms.Apply(property, row, this, fullPath, holds, out var omit);
        if (omit || raw is null)
        {
            return null;
        }

        var type = schemaProperty?.Type ?? SchemaType.Any;
        if (type == SchemaType.Array && schemaProperty?.ItemScalarType is { } itemType && raw is not JsonArray)
        {
            // A scalar bound to an array of scalars becomes a one-element array.
            var single = Coerce(raw, itemType, fullPath, holds);
            return single is null ? null : new JsonArray(single);
        }

        return Coerce(raw, type, fullPath, holds);
    }

    internal ReferenceSnapshot References => _references;

    internal SchemaSnapshot Schema => _schema;

    internal string DataPartition => _context.DataPartition;

    internal string SourceSystem => _mapping.Source.System;

    internal string? ParameterValue(string name)
    {
        if (_context.Parameters.TryGetValue(name, out var value))
        {
            return value;
        }

        return _mapping.Parameters.TryGetValue(name, out var declared) ? declared.Default : null;
    }

    private void RenderInto(JsonObject root, MappingProperty property, SourceRow row, SourceRecord record, string pathPrefix, List<string> holds)
    {
        var fullPath = Join(pathPrefix, property.Target);
        if (property.Collection)
        {
            var scope = property.Scope ?? property.Source
                ?? throw new FlowValidationException($"{Where(_mapping)}: collection property '{fullPath}' names no scope.");
            var rows = record.ScopeRows(scope);
            var items = new JsonArray();
            foreach (var itemRow in rows)
            {
                var item = new JsonObject();
                foreach (var child in ChildProperties(property, fullPath))
                {
                    RenderInto(item, child, itemRow, record, fullPath, holds);
                }

                if (item.Count > 0)
                {
                    items.Add(item);
                }
            }

            if (items.Count > 0)
            {
                SetPath(root, property.Target, items);
            }

            return;
        }

        if (property.IsObject)
        {
            var obj = new JsonObject();
            foreach (var child in ChildProperties(property, fullPath))
            {
                RenderInto(obj, child, row, record, fullPath, holds);
            }

            if (obj.Count > 0)
            {
                SetPath(root, property.Target, obj);
            }

            return;
        }

        var value = RenderScalar(property, row, pathPrefix, holds);
        if (value is not null)
        {
            SetPath(root, property.Target, value);
        }
    }

    private IReadOnlyList<MappingProperty> ChildProperties(MappingProperty property, string fullPath)
    {
        if (property.Definition is { } name)
        {
            return _mapping.Definitions.TryGetValue(name, out var defined)
                ? defined
                : throw new FlowValidationException($"{Where(_mapping)}: property '{fullPath}' references definition '{name}', which does not exist.");
        }

        return property.Properties;
    }

    private static void SetPath(JsonObject root, string dottedPath, JsonNode value)
    {
        var segments = dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject next)
            {
                next = new JsonObject();
                current[segments[i]] = next;
            }

            current = next;
        }

        current[segments[^1]] = value;
    }

    private static JsonNode? Coerce(object raw, SchemaType type, string path, List<string> holds)
    {
        try
        {
            switch (type)
            {
                case SchemaType.String:
                    return JsonValue.Create(SourceRow.Stringify(raw));
                case SchemaType.Number:
                    return raw switch
                    {
                        double d => JsonValue.Create(d),
                        float f => JsonValue.Create((double)f),
                        long l => JsonValue.Create(l),
                        int i => JsonValue.Create((long)i),
                        decimal m => JsonValue.Create((double)m),
                        bool => throw new FormatException("boolean is not a number"),
                        _ => JsonValue.Create(double.Parse(SourceRow.Stringify(raw)!, NumberStyles.Float, CultureInfo.InvariantCulture)),
                    };
                case SchemaType.Integer:
                    return raw switch
                    {
                        long l => JsonValue.Create(l),
                        int i => JsonValue.Create((long)i),
                        double d when Math.Floor(d) == d => JsonValue.Create((long)d),
                        _ => JsonValue.Create(long.Parse(SourceRow.Stringify(raw)!, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                    };
                case SchemaType.Boolean:
                    return raw switch
                    {
                        bool b => JsonValue.Create(b),
                        _ => JsonValue.Create(ParseBool(SourceRow.Stringify(raw)!)),
                    };
                case SchemaType.Object:
                case SchemaType.Array:
                    return raw as JsonNode ?? throw new FormatException($"a scalar cannot be written to the {type} at {path}");
                default:
                    return raw as JsonNode ?? JsonValue.Create(Native(raw));
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            holds.Add($"{path}: value '{SourceRow.Stringify(raw)}' is not a valid {type.ToString().ToLowerInvariant()} ({ex.Message})");
            return null;
        }
    }

    private static object Native(object raw) => raw switch
    {
        string or bool or long or double => raw,
        int i => (long)i,
        short s => (long)s,
        float f => (double)f,
        decimal m => (double)m,
        _ => SourceRow.Stringify(raw)!,
    };

    private static bool ParseBool(string text)
    {
        var t = text.Trim();
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t is "1" || t.Equals("yes", StringComparison.OrdinalIgnoreCase) || t.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t is "0" || t.Equals("no", StringComparison.OrdinalIgnoreCase) || t.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new FormatException($"'{text}' is not a boolean");
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values)
        {
            arr.Add(v);
        }

        return arr;
    }

    internal static string Join(string prefix, string target) => string.IsNullOrEmpty(prefix) ? target : prefix + "." + target;

    internal static string Where(MappingDefinition mapping) => mapping.SourcePath ?? mapping.Reference;
}

/// <summary>The outcome of rendering one record.</summary>
public sealed record RenderResult
{
    public DeliveryKey? Key { get; init; }

    public required string SourceKey { get; init; }

    public string? TargetId { get; init; }

    public required JsonObject Document { get; init; }

    /// <summary>The canonical JSON of <see cref="Document"/>.</summary>
    public required string Canonical { get; init; }

    /// <summary>H(canonical document, render context).</summary>
    public required string MetadataHash { get; init; }

    /// <summary>Reasons the record cannot be delivered as it stands. Empty means deliverable.</summary>
    public required IReadOnlyList<string> Holds { get; init; }

    public bool IsHeld => Holds.Count > 0 || Key is null;
}
