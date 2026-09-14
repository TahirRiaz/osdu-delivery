using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Hashing;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// One OSDU kind's JSON Schema, bundled so every <c>$ref</c> is local (<c>#/definitions/...</c>), immutable and
/// content-addressed (design.md section 4.1). The version is the hash of the canonical schema, so an upstream schema
/// edit under the same kind string still moves the render context.
/// </summary>
public sealed class SchemaSnapshot
{
    private readonly JsonObject _root;
    private readonly JsonObject? _definitions;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SchemaProperty?> _resolved = new(StringComparer.Ordinal);
    private JsonObject? _effectiveRoot;

    public SchemaSnapshot(string kind, JsonObject bundledSchema, DateTimeOffset capturedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(bundledSchema);
        Kind = kind;
        _root = bundledSchema;
        _definitions = bundledSchema["definitions"] as JsonObject ?? bundledSchema["$defs"] as JsonObject;
        CapturedUtc = capturedUtc;
        Version = ContentHash.Of(CanonicalJson.ToBytes(bundledSchema))[..16];
    }

    public string Kind { get; }

    /// <summary>Content hash prefix of the bundled schema. Enters the render context.</summary>
    public string Version { get; }

    public DateTimeOffset CapturedUtc { get; }

    public JsonObject Root => _root;

    /// <summary>Parses a bundled schema document as saved by the snapshot store.</summary>
    public static SchemaSnapshot Parse(string kind, string json, DateTimeOffset capturedUtc)
    {
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new DeliveryException($"Schema snapshot for '{kind}' is not a JSON object.");
        return new SchemaSnapshot(kind, node, capturedUtc);
    }

    /// <summary>
    /// Resolves a dotted path from the record root (for example <c>data.Curves.CurveID</c>, where an array segment
    /// is stepped into implicitly) to its effective schema, merging <c>allOf</c> branches and following local refs.
    /// Returns null when no such property exists.
    /// </summary>
    public SchemaProperty? Resolve(string dottedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dottedPath);
        return _resolved.GetOrAdd(dottedPath, ResolveUncached);
    }

    private SchemaProperty? ResolveUncached(string dottedPath)
    {
        var current = EffectiveRoot;
        SchemaProperty? result = null;
        foreach (var segment in dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Child(current, segment);
            if (next is null)
            {
                return null;
            }

            result = next;
            current = next.Schema;
            // A collection property binds its children to the array item schema.
            if (next.Type == SchemaType.Array && next.Items is not null)
            {
                current = next.Items;
            }
        }

        return result;
    }

    /// <summary>The required property names of the object at <paramref name="dottedPath"/> (empty path is the record root).</summary>
    public IReadOnlyList<string> RequiredAt(string dottedPath)
    {
        var effective = string.IsNullOrEmpty(dottedPath)
            ? EffectiveRoot
            : Resolve(dottedPath) is { } p ? (p.Type == SchemaType.Array && p.Items is not null ? p.Items : p.Schema) : null;
        if (effective is null)
        {
            return [];
        }

        return effective["required"] is JsonArray required
            ? required.Select(r => r?.GetValue<string>()).Where(r => r is not null).Cast<string>().ToList()
            : [];
    }

    /// <summary>The property names of the object at <paramref name="dottedPath"/>.</summary>
    public IReadOnlyList<string> PropertiesAt(string dottedPath)
    {
        var effective = string.IsNullOrEmpty(dottedPath)
            ? EffectiveRoot
            : Resolve(dottedPath) is { } p ? (p.Type == SchemaType.Array && p.Items is not null ? p.Items : p.Schema) : null;
        return effective?["properties"] is JsonObject props ? props.Select(kv => kv.Key).ToList() : [];
    }

    private JsonObject EffectiveRoot => _effectiveRoot ??= Effective(_root);

    /// <summary>The effective root object: every <c>allOf</c> branch merged and every <c>$ref</c> followed.</summary>
    internal JsonObject EffectiveRootObject => EffectiveRoot;

    /// <summary>A schema node with its <c>allOf</c> branches merged and its <c>$ref</c> followed, for walks over the whole schema.</summary>
    internal JsonObject EffectiveOf(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return Effective(schema);
    }

    /// <summary>The JSON Schema type an effective node declares, or implies through its properties or items.</summary>
    internal static SchemaType TypeOfNode(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return TypeOf(schema);
    }

    private SchemaProperty? Child(JsonObject effectiveObject, string name)
    {
        if (effectiveObject["properties"] is JsonObject props && props[name] is JsonObject child)
        {
            return Describe(name, Effective(child));
        }

        // additionalProperties: {schema} (for example OSDU tags: string values keyed by anything).
        if (effectiveObject["additionalProperties"] is JsonObject additional)
        {
            return Describe(name, Effective(additional));
        }

        if (effectiveObject["additionalProperties"] is JsonValue flag && flag.TryGetValue<bool>(out var allowed) && allowed)
        {
            return new SchemaProperty(name, SchemaType.Any, new JsonObject(), null, null, false);
        }

        return null;
    }

    private SchemaProperty Describe(string name, JsonObject effective)
    {
        var type = TypeOf(effective);
        JsonObject? items = null;
        if (type == SchemaType.Array && effective["items"] is JsonObject itemSchema)
        {
            items = Effective(itemSchema);
        }

        var pattern = effective["pattern"]?.GetValue<string>();
        var relationship = effective["x-osdu-relationship"] is JsonArray;
        return new SchemaProperty(name, type, effective, items, pattern, relationship);
    }

    /// <summary>Merges <c>allOf</c> branches and resolves <c>$ref</c> into one object schema.</summary>
    private JsonObject Effective(JsonObject schema)
    {
        if (schema["$ref"] is JsonValue refValue && refValue.TryGetValue<string>(out var reference))
        {
            var target = ResolveRef(reference);
            return Effective(target);
        }

        if (schema["allOf"] is not JsonArray allOf)
        {
            return schema;
        }

        var merged = new JsonObject();
        var mergedProps = new JsonObject();
        var mergedRequired = new JsonArray();
        foreach (var kv in schema)
        {
            if (kv.Key is "allOf")
            {
                continue;
            }

            merged[kv.Key] = kv.Value?.DeepClone();
        }

        var branches = allOf.OfType<JsonObject>().Select(Effective).Append(merged).ToList();
        foreach (var branch in branches)
        {
            if (branch["properties"] is JsonObject props)
            {
                foreach (var p in props)
                {
                    mergedProps[p.Key] = p.Value?.DeepClone();
                }
            }

            if (branch["required"] is JsonArray req)
            {
                foreach (var r in req)
                {
                    if (r is not null && !mergedRequired.Any(existing => existing?.GetValue<string>() == r.GetValue<string>()))
                    {
                        mergedRequired.Add(r.DeepClone());
                    }
                }
            }

            foreach (var kv in branch)
            {
                if (kv.Key is "properties" or "required" or "allOf" or "$ref")
                {
                    continue;
                }

                merged[kv.Key] ??= kv.Value?.DeepClone();
            }
        }

        merged["properties"] = mergedProps;
        merged["required"] = mergedRequired;
        merged["type"] ??= "object";
        return merged;
    }

    private JsonObject ResolveRef(string reference)
    {
        const string prefix = "#/definitions/";
        const string prefix2 = "#/$defs/";
        string name;
        if (reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            name = reference[prefix.Length..];
        }
        else if (reference.StartsWith(prefix2, StringComparison.Ordinal))
        {
            name = reference[prefix2.Length..];
        }
        else
        {
            throw new DeliveryException(
                $"Schema snapshot for '{Kind}' contains a non-local reference '{reference}'. Snapshots must be bundled so every $ref is '#/definitions/...'.");
        }

        return _definitions?[name] as JsonObject
            ?? throw new DeliveryException($"Schema snapshot for '{Kind}' has no definition '{name}' (referenced as '{reference}').");
    }

    private static SchemaType TypeOf(JsonObject schema)
    {
        var typeNode = schema["type"];
        string? type = typeNode switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonArray arr => arr.Select(a => a?.GetValue<string>()).FirstOrDefault(a => a is not null and not "null"),
            _ => null,
        };

        if (type is null)
        {
            if (schema["properties"] is not null || schema["allOf"] is not null)
            {
                return SchemaType.Object;
            }

            if (schema["items"] is not null)
            {
                return SchemaType.Array;
            }

            return SchemaType.Any;
        }

        return type switch
        {
            "string" => SchemaType.String,
            "number" => SchemaType.Number,
            "integer" => SchemaType.Integer,
            "boolean" => SchemaType.Boolean,
            "object" => SchemaType.Object,
            "array" => SchemaType.Array,
            _ => SchemaType.Any,
        };
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The names mirror JSON Schema type keywords.")]
public enum SchemaType
{
    Any,
    String,
    Number,
    Integer,
    Boolean,
    Object,
    Array,
}

/// <summary>A resolved property of a schema snapshot.</summary>
public sealed record SchemaProperty(
    string Name,
    SchemaType Type,
    JsonObject Schema,
    JsonObject? Items,
    string? Pattern,
    bool IsRelationship)
{
    public string? Format => Schema["format"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The format of each item of a list of values (a list of dates, say), when the schema declares one.</summary>
    public string? ItemFormat => Items?["format"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The element type of an array of scalars, or null for arrays of objects / non-arrays.</summary>
    public SchemaType? ItemScalarType
    {
        get
        {
            if (Type != SchemaType.Array || Items is null)
            {
                return null;
            }

            var t = Items["type"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            return t switch
            {
                "string" => SchemaType.String,
                "number" => SchemaType.Number,
                "integer" => SchemaType.Integer,
                "boolean" => SchemaType.Boolean,
                "object" => SchemaType.Object,
                _ => null,
            };
        }
    }
}
