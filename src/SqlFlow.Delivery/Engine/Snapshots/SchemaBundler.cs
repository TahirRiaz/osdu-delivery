using System.Text.Json.Nodes;
using SqlFlow.Core;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// Turns an OSDU JSON Schema with external references into one self-contained document whose every <c>$ref</c>
/// is <c>#/definitions/{name}</c>. Two sources are supported: the schema service, whose refs are schema ids
/// (<c>osdu:wks:AbstractAccessControlList:1.0.0</c>, sometimes already under <c>#/definitions/</c>), and a local
/// checkout of the OSDU data definitions, whose refs are relative file paths (<c>../abstract/X.1.0.0.json</c>).
/// </summary>
public static class SchemaBundler
{
    /// <summary>
    /// Bundles <paramref name="root"/>, resolving each non-local reference through <paramref name="resolve"/>
    /// (which receives the raw reference and the reference of the document it appears in, and returns the
    /// referenced document and its canonical name).
    /// </summary>
    public static async Task<JsonObject> BundleAsync(JsonObject root, string rootName, Func<string, string, CancellationToken, Task<(string Name, JsonObject Schema)>> resolve, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(resolve);

        var bundled = (JsonObject)root.DeepClone();
        var definitions = bundled["definitions"] as JsonObject ?? new JsonObject();
        bundled.Remove("definitions");
        bundled.Remove("$defs");
        var queue = new Queue<(JsonObject Node, string Owner)>();
        queue.Enqueue((bundled, rootName));
        foreach (var kv in definitions.ToList())
        {
            if (kv.Value is JsonObject d)
            {
                queue.Enqueue((d, kv.Key));
            }
        }

        var seen = new HashSet<string>(definitions.Select(d => d.Key), StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var (node, owner) = queue.Dequeue();
            foreach (var reference in CollectRefs(node))
            {
                var raw = reference.Value;
                if (raw.StartsWith("#/definitions/", StringComparison.Ordinal) || raw.StartsWith("#/$defs/", StringComparison.Ordinal))
                {
                    var local = raw[(raw.LastIndexOf('/') + 1)..];
                    reference.Holder["$ref"] = "#/definitions/" + local;
                    if (seen.Contains(local) || definitions[local] is not null)
                    {
                        continue;
                    }

                    // A local ref to a definition that is not present: resolve it by name.
                    var (name, schema) = await resolve(local, owner, ct).ConfigureAwait(false);
                    reference.Holder["$ref"] = "#/definitions/" + name;
                    if (seen.Add(name))
                    {
                        var copy = Strip(schema);
                        definitions[name] = copy;
                        queue.Enqueue((copy, name));
                    }

                    continue;
                }

                if (raw.StartsWith('#'))
                {
                    continue;
                }

                var (resolvedName, resolvedSchema) = await resolve(raw, owner, ct).ConfigureAwait(false);
                reference.Holder["$ref"] = "#/definitions/" + resolvedName;
                if (seen.Add(resolvedName))
                {
                    var copy = Strip(resolvedSchema);
                    foreach (var kv in (resolvedSchema["definitions"] as JsonObject ?? new JsonObject()).ToList())
                    {
                        if (kv.Value is JsonObject inner && seen.Add(kv.Key))
                        {
                            var innerCopy = (JsonObject)inner.DeepClone();
                            definitions[kv.Key] = innerCopy;
                            queue.Enqueue((innerCopy, kv.Key));
                        }
                    }

                    definitions[resolvedName] = copy;
                    queue.Enqueue((copy, resolvedName));
                }
            }
        }

        var sorted = new JsonObject();
        foreach (var kv in definitions.OrderBy(d => d.Key, StringComparer.Ordinal).ToList())
        {
            definitions.Remove(kv.Key);
            sorted[kv.Key] = kv.Value;
        }

        bundled["definitions"] = sorted;
        return bundled;
    }

    /// <summary>A resolver over a local checkout of the OSDU data definitions (the osdu-client repo's Specifications/Data layout).</summary>
    public static Func<string, string, CancellationToken, Task<(string Name, JsonObject Schema)>> DirectoryResolver(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return (reference, owner, _) =>
        {
            var ownerDir = Path.GetDirectoryName(owner) ?? dataRoot;
            var candidate = Path.GetFullPath(Path.Combine(ownerDir, reference.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(candidate))
            {
                candidate = FindByName(dataRoot, reference)
                    ?? throw new DeliveryException($"Schema reference '{reference}' (from {owner}) was not found under '{dataRoot}'.");
            }

            var node = JsonNode.Parse(File.ReadAllText(candidate)) as JsonObject
                ?? throw new DeliveryException($"Schema file '{candidate}' is not a JSON object.");
            var name = Path.GetFileNameWithoutExtension(candidate);
            return Task.FromResult((name, node));
        };
    }

    /// <summary>Finds the file for a kind (<c>osdu:wks:work-product-component--WellLog:1.4.0</c>) under a data definitions checkout.</summary>
    public static string LocateKindFile(string dataRoot, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var parts = kind.Split(':');
        if (parts.Length != 4)
        {
            throw new FlowValidationException($"Kind '{kind}' must be 'authority:source:entityType:version'.");
        }

        var entity = parts[2];
        var version = parts[3];
        var sep = entity.IndexOf("--", StringComparison.Ordinal);
        var group = sep < 0 ? string.Empty : entity[..sep];
        var name = sep < 0 ? entity : entity[(sep + 2)..];
        var file = $"{name}.{version}.json";
        var direct = Path.Combine(dataRoot, group, file);
        if (File.Exists(direct))
        {
            return direct;
        }

        return FindByName(dataRoot, file) ?? throw new DeliveryException($"No schema file '{file}' for kind '{kind}' under '{dataRoot}'.");
    }

    private static string? FindByName(string root, string reference)
    {
        var fileName = Path.GetFileName(reference.Replace('\\', '/'));
        return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static JsonObject Strip(JsonObject schema)
    {
        var copy = (JsonObject)schema.DeepClone();
        copy.Remove("definitions");
        copy.Remove("$defs");
        copy.Remove("$schema");
        copy.Remove("$id");
        return copy;
    }

    private static List<(JsonObject Holder, string Value)> CollectRefs(JsonObject node)
    {
        var refs = new List<(JsonObject, string)>();
        Walk(node, refs);
        return refs;
    }

    private static void Walk(JsonNode? node, List<(JsonObject Holder, string Value)> refs)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"] is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    refs.Add((obj, s));
                }

                foreach (var kv in obj)
                {
                    if (kv.Key != "$ref")
                    {
                        Walk(kv.Value, refs);
                    }
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    Walk(item, refs);
                }

                break;
        }
    }
}
