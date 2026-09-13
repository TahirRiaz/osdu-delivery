using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Templates;

/// <summary>
/// Where a template's schema comes from: the OSDU data definitions, read from the public repository
/// (<see cref="OsduDataDefinitions"/>) or from a local checkout of it, or a bundled schema file for a schema of one's own.
/// Each produces the same thing, a <see cref="SchemaSnapshot"/> whose every <c>$ref</c> is resolved into its definitions,
/// which the template store saves.
/// </summary>
public static class TemplateSources
{
    /// <summary>Bundles a kind's schema from a local checkout of the OSDU data definitions (the repository's <c>Generated</c> folder).</summary>
    public static async Task<SchemaSnapshot> FromDirectoryAsync(string dataRoot, string kind, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(time);
        RequireKind(kind);
        var root = Path.GetFullPath(dataRoot);
        if (!Directory.Exists(root))
        {
            throw new DeliveryException($"The data definitions folder '{root}' does not exist.");
        }

        var path = SchemaBundler.KindPath(kind);
        var bundled = await SchemaBundler.BundleTreeAsync(path, (file, token) => ReadCheckoutFileAsync(root, file, token), ct).ConfigureAwait(false);
        return Validated(kind, bundled, time.GetUtcNow(), Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// Reads a bundled schema (every <c>$ref</c> under <c>#/definitions/</c> and present), as the data definitions bundle
    /// it and as the import accepts it. <paramref name="where"/> names the file or upload in errors.
    /// </summary>
    public static SchemaSnapshot FromBundledJson(string json, string kind, DateTimeOffset capturedUtc, string where)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        RequireKind(kind);
        return Validated(kind, ParseObject(json, where), capturedUtc, where);
    }

    /// <summary>
    /// Whether the schema refers to anything outside itself, as a schema file the OSDU data definitions publish refers to the
    /// shared schemas beside it (<c>../abstract/AbstractAccessControlList.1.0.0.json</c>).
    /// </summary>
    public static bool RefersOutside(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return References(schema).Any(reference => !reference.StartsWith('#'));
    }

    /// <summary>Schema text as the JSON object a schema is; <paramref name="where"/> names it in the error.</summary>
    internal static JsonObject ParseObject(string json, string where)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? throw new DeliveryException($"{where}: the schema is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"{where}: the schema is not valid JSON ({ex.Message}).", ex);
        }
    }

    /// <summary>A bundled schema as a template is saved from it: it describes the kind, refers to nothing outside itself, and declares <c>data</c>.</summary>
    internal static SchemaSnapshot Validated(string kind, JsonObject bundled, DateTimeOffset capturedUtc, string where)
    {
        if (bundled["x-osdu-schema-source"] is JsonValue declared && declared.TryGetValue<string>(out var source)
            && !string.IsNullOrWhiteSpace(source) && !string.Equals(source, kind, StringComparison.Ordinal))
        {
            throw new DeliveryException($"{where}: the schema describes '{source}', not '{kind}'.");
        }

        var definitions = bundled["definitions"] as JsonObject ?? bundled["$defs"] as JsonObject;
        foreach (var reference in References(bundled))
        {
            var name = reference.StartsWith("#/definitions/", StringComparison.Ordinal)
                ? reference["#/definitions/".Length..]
                : reference.StartsWith("#/$defs/", StringComparison.Ordinal) ? reference["#/$defs/".Length..] : null;
            if (name is null)
            {
                throw new DeliveryException(
                    $"{where}: the schema refers to '{reference}' outside itself. A template is saved from a bundled schema, where every reference is resolved into its definitions.");
            }

            if (definitions?[name] is not JsonObject)
            {
                throw new DeliveryException($"{where}: the schema refers to definition '{name}', which it does not contain.");
            }
        }

        var snapshot = new SchemaSnapshot(kind, bundled, capturedUtc);
        if (snapshot.EffectiveRootObject["properties"] is not JsonObject properties || properties["data"] is null)
        {
            throw new DeliveryException($"{where}: the schema declares no 'data' property, so it does not describe an OSDU record.");
        }

        return snapshot;
    }

    internal static void RequireKind(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (!FlowMapper.IsRecordKind(kind))
        {
            throw new FlowValidationException($"Kind '{kind}' must be 'authority:source:entityType:major.minor.patch'.");
        }
    }

    private static async Task<JsonObject> ReadCheckoutFileAsync(string root, string path, CancellationToken ct)
    {
        var file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file))
        {
            // A checkout that keeps the files in other folders (the osdu-client repository's Specifications/Data) still holds them by name.
            file = Directory.EnumerateFiles(root, Path.GetFileName(file), SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new DeliveryException($"No schema file '{path}' under '{root}'.");
        }

        try
        {
            return JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)) as JsonObject
                ?? throw new DeliveryException($"Schema file '{file}' is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"Schema file '{file}' is not valid JSON ({ex.Message}).", ex);
        }
    }

    private static IEnumerable<string> References(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference))
                {
                    yield return reference;
                }

                foreach (var (_, child) in obj)
                {
                    foreach (var inner in References(child))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var inner in References(item))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }
}
