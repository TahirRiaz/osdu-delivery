using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Core;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>What to capture into a reference snapshot: one entry per reference or master-data type.</summary>
public sealed record ReferenceCaptureSpec
{
    [JsonPropertyName("types")]
    public required List<ReferenceTypeSpec> Types { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ReferenceCaptureSpec Parse(string json, string source)
    {
        ReferenceCaptureSpec spec;
        try
        {
            spec = JsonSerializer.Deserialize<ReferenceCaptureSpec>(json, Options) ?? throw new FlowValidationException($"{source}: empty capture spec.");
        }
        catch (JsonException ex)
        {
            throw new FlowValidationException($"{source}: invalid capture spec - {ex.Message}", ex);
        }

        spec.Validate(source);
        return spec;
    }

    /// <summary>Rejects a spec that would capture nothing, or two types under one name.</summary>
    public void Validate(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (Types.Count == 0)
        {
            throw new FlowValidationException($"{source}: the capture spec declares no types.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in Types)
        {
            type.Validate();
            if (!seen.Add(type.Name))
            {
                throw new FlowValidationException($"{source}: the capture spec declares type '{type.Name}' more than once.");
            }
        }
    }
}

public sealed record ReferenceTypeSpec
{
    /// <summary>The short name mappings use (UnitOfMeasure).</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The OSDU entity type (reference-data--UnitOfMeasure).</summary>
    [JsonPropertyName("entityType")]
    public required string EntityType { get; init; }

    /// <summary>The search kind pattern (osdu:wks:reference-data--UnitOfMeasure:*).</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>
    /// The paths to capture (data.Code, data.Name, data.NameAlias.AliasName). Whatever a path yields is cached
    /// as it is: a scalar, a set of values, or a nested object. The id is always captured.
    /// </summary>
    [JsonPropertyName("fields")]
    public List<ReferenceFieldSpec> Fields { get; init; } =
        [new ReferenceFieldSpec("data.Code"), new ReferenceFieldSpec("data.Name"), new ReferenceFieldSpec("data.ID")];

    /// <summary>Optional search query narrowing the capture (default *).</summary>
    [JsonPropertyName("query")]
    public string Query { get; init; } = "*";

    /// <summary>Rejects a type that captures nothing, or two paths cached under one name.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new FlowValidationException("A cached type needs a name.");
        }

        if (string.IsNullOrWhiteSpace(EntityType))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs an entityType.");
        }

        if (string.IsNullOrWhiteSpace(Kind))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs a kind to search.");
        }

        if (Fields.Count == 0)
        {
            throw new FlowValidationException($"Cached type '{Name}' declares no fields to capture.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            // A field named ID (OSDU reference data has one) is fine and shadows the record id under that name;
            // the exact key 'id' is not, because that is the key the record id itself is written under.
            if (field.Name.Equals("id", StringComparison.Ordinal))
            {
                throw new FlowValidationException($"Cached type '{Name}' caches '{field.Path}' as 'id', which is the key the record id is written under; cache it under another name.");
            }

            if (!seen.Add(field.Name))
            {
                throw new FlowValidationException($"Cached type '{Name}' caches two paths under the name '{field.Name}'; give one of them a different 'as'.");
            }
        }
    }
}

/// <summary>
/// One path to cache, and the name the cache stores it under. Written either as the bare path
/// (<c>data.Code</c>, stored as <c>Code</c>) or as a path with an explicit name
/// (<c>{ "path": "data.NameAlias.AliasName", "as": "Alias" }</c>). A path crosses arrays implicitly, so a path
/// through an array of objects yields the set of values found along it.
/// </summary>
[JsonConverter(typeof(ReferenceFieldSpecConverter))]
public sealed record ReferenceFieldSpec
{
    public ReferenceFieldSpec(string path, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path.Trim();
        Name = string.IsNullOrWhiteSpace(name) ? ReferenceField.Normalize(Path) : name.Trim();
    }

    /// <summary>The path into the OSDU record, from its root (<c>data.Code</c>, <c>legal.legaltags</c>).</summary>
    [JsonPropertyName("path")]
    public string Path { get; }

    /// <summary>The name the value is cached under, and the name a mapping matches or selects by.</summary>
    [JsonPropertyName("as")]
    public string Name { get; }
}

/// <summary>Reads a field spec written either as a bare path or as a path with a name.</summary>
public sealed class ReferenceFieldSpecConverter : JsonConverter<ReferenceFieldSpec>
{
    public override ReferenceFieldSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var path = reader.GetString();
            return string.IsNullOrWhiteSpace(path)
                ? throw new JsonException("a captured field cannot be an empty path")
                : new ReferenceFieldSpec(path);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("a captured field is either a path or an object with 'path' and optional 'as'");
        }

        string? declaredPath = null;
        string? declaredName = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var property = reader.GetString();
            reader.Read();
            switch (property?.ToLowerInvariant())
            {
                case "path":
                    declaredPath = reader.GetString();
                    break;
                case "as":
                case "name":
                    declaredName = reader.GetString();
                    break;
                default:
                    throw new JsonException($"a captured field has no '{property}' setting; use 'path' and 'as'");
            }
        }

        return string.IsNullOrWhiteSpace(declaredPath)
            ? throw new JsonException("a captured field needs a 'path'")
            : new ReferenceFieldSpec(declaredPath, declaredName);
    }

    public override void Write(Utf8JsonWriter writer, ReferenceFieldSpec value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Name.Equals(ReferenceField.Normalize(value.Path), StringComparison.Ordinal))
        {
            writer.WriteStringValue(value.Path);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("path", value.Path);
        writer.WriteString("as", value.Name);
        writer.WriteEndObject();
    }
}
