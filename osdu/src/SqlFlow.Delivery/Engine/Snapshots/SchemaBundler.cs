using System.Text.Json.Nodes;
using SqlFlow.Core;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// Turns an OSDU JSON Schema with external references into one self-contained document whose every <c>$ref</c> is
/// <c>#/definitions/{name}</c>. The schemas come from an OSDU data definitions tree (the <c>Generated</c> folder of the
/// public repository, read over its API or from a local checkout), whose references are file paths relative to the
/// file they appear in (<c>../abstract/AbstractLegalTags.1.0.0.json</c>).
/// </summary>
public static class SchemaBundler
{
    /// <summary>
    /// Bundles <paramref name="root"/>, resolving each non-local reference through <paramref name="resolve"/>
    /// (which receives the raw reference and the name of the document it appears in, and returns the
    /// referenced document and its canonical name).
    /// </summary>
    public static async Task<JsonObject> BundleAsync(JsonObject root, string rootName, Func<string, string, CancellationToken, Task<(string Name, JsonObject Schema)>> resolve, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(resolve);

        var bundled = (JsonObject)root.DeepClone();
        // Where the schema keeps its definitions, so the bundle puts them back in the same place. Only the keys after that
        // place move when both are removed, so the earlier of the two is still the position to restore.
        var positions = new[] { bundled.IndexOf("definitions"), bundled.IndexOf("$defs") }.Where(i => i >= 0).ToList();
        var position = positions.Count > 0 ? positions.Min() : -1;
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

        // The schema keeps the order it was written in: its definitions go back where it had them (at the end when it had
        // none), its own definitions first, then each referenced file as its first reference is reached. The version is the
        // hash of the canonical form, so none of this moves it.
        if (position >= 0)
        {
            bundled.Insert(Math.Min(position, bundled.Count), "definitions", definitions);
        }
        else
        {
            bundled["definitions"] = definitions;
        }

        return bundled;
    }

    /// <summary>
    /// Bundles the schema file at <paramref name="rootPath"/> of an OSDU data definitions tree. <paramref name="read"/>
    /// reads a file by its path from the tree's root, with forward slashes, and is asked for each file once. A referenced
    /// file is bundled as the definition named by its file name without <c>.json</c>, so a tree bundles the same way, to
    /// the same template version, wherever it is read from.
    /// </summary>
    public static async Task<JsonObject> BundleTreeAsync(string rootPath, Func<string, CancellationToken, Task<JsonObject>> read, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(read);
        var root = await read(rootPath, ct).ConfigureAwait(false);
        return await BundleTreeAsync(rootPath, root, read, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Bundles <paramref name="root"/> as though it were the file at <paramref name="rootPath"/> of an OSDU data definitions
    /// tree, such as a published schema file brought in from outside the tree: its references are resolved relative to that
    /// path, and <paramref name="read"/> is asked only for the files it refers to, each once.
    /// </summary>
    public static async Task<JsonObject> BundleTreeAsync(
        string rootPath, JsonObject root, Func<string, CancellationToken, Task<JsonObject>> read, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(read);

        // The file each definition was read from, by the name it is bundled under: a reference is relative to the file it is in.
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { [rootPath] = rootPath };
        var loaded = new Dictionary<string, JsonObject>(StringComparer.Ordinal) { [rootPath] = root };
        return await BundleAsync(root, rootPath, async (reference, owner, token) =>
        {
            if (!files.TryGetValue(owner, out var ownerPath))
            {
                throw new DeliveryException(
                    $"{rootPath}: the reference '{reference}' is in definition '{owner}', which no file of the data definitions holds, so there is nothing it is relative to.");
            }

            var path = TreePath(rootPath, ownerPath, reference);
            var name = FileStem(path);
            if (files.TryGetValue(name, out var existing) && !string.Equals(existing, path, StringComparison.Ordinal))
            {
                throw new DeliveryException($"{rootPath}: '{existing}' and '{path}' would both be bundled as definition '{name}'.");
            }

            files[name] = path;
            if (!loaded.TryGetValue(path, out var schema))
            {
                schema = await read(path, token).ConfigureAwait(false);
                loaded[path] = schema;
            }

            return (name, schema);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The path of a kind's schema file from the root of a data definitions tree: its group folder and file, so
    /// <c>osdu:wks:master-data--Wellbore:1.3.0</c> is <c>master-data/Wellbore.1.3.0.json</c>.
    /// </summary>
    public static string KindPath(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var parts = kind.Split(':');
        if (parts.Length != 4)
        {
            throw new FlowValidationException($"Kind '{kind}' must be 'authority:source:entityType:version'.");
        }

        var entity = parts[2];
        var sep = entity.IndexOf("--", StringComparison.Ordinal);
        var file = $"{(sep < 0 ? entity : entity[(sep + 2)..])}.{parts[3]}.json";
        return sep < 0 ? file : $"{entity[..sep]}/{file}";
    }

    /// <summary>A reference resolved against the file it is in, kept inside the tree.</summary>
    private static string TreePath(string rootPath, string ownerPath, string reference)
    {
        if (reference.Contains('#') || reference.Contains('\\') || reference.StartsWith('/') || reference.Contains("://", StringComparison.Ordinal))
        {
            throw new DeliveryException($"{rootPath}: the reference '{reference}' is not a file of the data definitions.");
        }

        var segments = ownerPath.Split('/')[..^1].ToList();
        foreach (var segment in reference.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new DeliveryException($"{rootPath}: the reference '{reference}' leads outside the data definitions.");
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0
            ? throw new DeliveryException($"{rootPath}: the reference '{reference}' names no file.")
            : string.Join('/', segments);
    }

    private static string FileStem(string path)
    {
        var file = path[(path.LastIndexOf('/') + 1)..];
        return file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? file[..^".json".Length] : file;
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
