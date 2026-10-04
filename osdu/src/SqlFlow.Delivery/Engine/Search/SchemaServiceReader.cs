using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>A kind's schema as a partition's Schema service holds it, bundled, with what bundling it could not resolve.</summary>
/// <param name="Schema">The schema, every reference the service could answer for made local; null when the service holds no schema of the kind.</param>
/// <param name="Read">The schema ids read from the service: the kind's, then each one it refers to, in the order they were read.</param>
/// <param name="Unresolved">The references left as they were, each with why: a schema the service does not hold, a reference of a form the reader does not follow, or a bound reached.</param>
public sealed record SchemaServiceRead(SchemaSnapshot? Schema, IReadOnlyList<string> Read, IReadOnlyList<string> Unresolved);

/// <summary>
/// Reads a kind's schema from the Schema service (openapi schema_service, <c>GET /schema/{id}</c>) and bundles it as a
/// saved template is bundled: a reference to another schema by its id (<c>osdu:wks:AbstractCommonResources:1.0.0</c>,
/// with or without a fragment into it) is read from the service too and placed among the bundle's definitions, so every
/// reference the bundle holds is local. The OpenAPI description does not say whether the service answers with references
/// already resolved; a schema that comes back bundled needs nothing more, and one that does not is bundled here.
/// </summary>
/// <remarks>
/// Each schema is read once per bundle; a schema that refers back to one already read is answered from what was read. A
/// definition another schema names the same and holds differently is kept under a name of its own. A reference of a form
/// the reader does not follow (a web address, a pointer into a part of another schema other than its definitions) is left
/// as it is and named, and a check of a value it describes reports that part as not checked. The bundle is bounded:
/// <see cref="MaxSchemas"/> schemas at most.
/// </remarks>
public sealed partial class SchemaServiceReader
{
    /// <summary>The Schema service's read of one schema by its id.</summary>
    public const string DefaultPath = "/api/schema-service/v1/schema";

    /// <summary>The most schemas one bundle reads: the kind's and the ones it refers to, however deep.</summary>
    public const int MaxSchemas = 200;

    private readonly OsduHttpClient _client;
    private readonly string _path;
    private readonly TimeProvider _time;

    public SchemaServiceReader(OsduHttpClient client, TimeProvider time, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        _client = client;
        _time = time;
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path.TrimEnd('/');
    }

    /// <summary>
    /// The schema of <paramref name="kind"/>, bundled; its <see cref="SchemaServiceRead.Schema"/> is null when the service
    /// holds no schema of the kind. A refusal other than a missing schema (403, 5xx) throws, so a reader sees the service's
    /// answer rather than a schema that is merely absent.
    /// </summary>
    public async Task<SchemaServiceRead> ReadAsync(string kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var root = await FetchAsync(kind.Trim(), ct).ConfigureAwait(false);
        if (root is null)
        {
            return new SchemaServiceRead(null, [], []);
        }

        var bundle = new Bundle(kind.Trim(), root);
        var queue = new Queue<(string Id, JsonObject Schema, string? Prefix)>();
        queue.Enqueue((kind.Trim(), root, null));
        var read = new List<string> { kind.Trim() };
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (id, schema, prefix) = queue.Dequeue();
            // Each id Rewrite answers is one it has just named and no earlier schema asked for, so each is read once.
            foreach (var target in bundle.Rewrite(id, schema, prefix))
            {
                if (read.Count >= MaxSchemas)
                {
                    bundle.Unresolve(target, $"'{target}' was not read: a bundle reads at most {MaxSchemas} schemas");
                    continue;
                }

                var fetched = await FetchAsync(target, ct).ConfigureAwait(false);
                read.Add(target);
                if (fetched is null)
                {
                    bundle.Unresolve(target, $"the Schema service holds no schema '{target}'");
                    continue;
                }

                var name = bundle.Adopt(target, fetched);
                queue.Enqueue((target, fetched, name));
            }
        }

        var snapshot = new SchemaSnapshot(kind.Trim(), bundle.Root, _time.GetUtcNow());
        return new SchemaServiceRead(snapshot, read, bundle.Unresolved);
    }

    /// <summary>A schema as the service answers for <paramref name="id"/>, or null when it holds none (404).</summary>
    private async Task<JsonObject?> FetchAsync(string id, CancellationToken ct)
    {
        var result = await _client.SendJsonAsync(HttpMethod.Get, _client.Url(_path + "/{id}", id), null, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(result.Body) as JsonObject
                ?? throw new DeliveryException($"The Schema service answered for '{id}' with something other than a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"The Schema service answered for '{id}' with text that is not JSON: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The bundle being made: the kind's schema, its definitions gaining every schema it refers to, and every reference
    /// rewritten to the definition it now names.
    /// </summary>
    private sealed class Bundle
    {
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _unresolved = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<JsonObject>> _hoisted = new(StringComparer.Ordinal);
        private readonly JsonObject _definitions;

        public Bundle(string kind, JsonObject root)
        {
            Root = root;

            // Draft 2019 writes $defs where draft-07 writes definitions: the bundle keeps one, as a saved template does.
            if (root["definitions"] is null && root["$defs"] is JsonObject defs)
            {
                root.Remove("$defs");
                root["definitions"] = defs;
            }

            _definitions = root["definitions"] as JsonObject ?? new JsonObject();
            root["definitions"] = _definitions;
            _names[kind] = string.Empty;
        }

        public JsonObject Root { get; }

        public IReadOnlyList<string> Unresolved => _unresolved.Values.ToList();

        /// <summary>Whether <paramref name="id"/> was read already, or was found not to be readable.</summary>
        public bool Has(string id) => _names.ContainsKey(id) || _unresolved.ContainsKey(id);

        public void Unresolve(string id, string why) => _unresolved.TryAdd(id, why);

        /// <summary>
        /// Places a schema read for <paramref name="id"/> among the definitions, its own definitions with it, under a name no
        /// other definition holds differently; answers the prefix its own local references are rewritten with.
        /// </summary>
        public string Adopt(string id, JsonObject schema)
        {
            // The name was reserved when the first reference to the schema was rewritten; the schema takes it.
            var name = _names.TryGetValue(id, out var reserved) && reserved.Length > 0 ? reserved : Free(DefinitionName(id), schema);
            _names[id] = name;
            var hoisted = new List<JsonObject>();
            _hoisted[id] = hoisted;

            if ((schema["definitions"] ?? schema["$defs"]) is JsonObject inner)
            {
                schema.Remove("definitions");
                schema.Remove("$defs");
                foreach (var (key, value) in inner.ToList())
                {
                    inner.Remove(key);
                    if (value is not JsonObject definition)
                    {
                        continue;
                    }

                    var placed = Free(key, definition);
                    if (_definitions[placed] is null)
                    {
                        _definitions[placed] = definition;
                        hoisted.Add(definition);
                    }

                    _names[$"{id}#/definitions/{key}"] = placed;
                }
            }

            _definitions[name] = schema;
            return name;
        }

        /// <summary>
        /// Rewrites every reference inside <paramref name="schema"/> to a local one, and answers the schema ids it refers to
        /// that are not read yet. <paramref name="prefix"/> is null for the kind's own schema.
        /// </summary>
        public IReadOnlyList<string> Rewrite(string id, JsonObject schema, string? prefix)
        {
            var wanted = new List<string>();
            var parts = _hoisted.TryGetValue(id, out var hoisted) ? hoisted.Prepend(schema) : [schema];
            foreach (var node in parts.SelectMany(Walk))
            {
                if (node["$ref"] is not JsonValue value || !value.TryGetValue<string>(out var reference))
                {
                    continue;
                }

                var local = Local(id, prefix, reference, wanted);
                if (local is not null)
                {
                    node["$ref"] = local;
                }
            }

            return wanted;
        }

        /// <summary>The local reference <paramref name="reference"/> becomes, or null to leave it as it is.</summary>
        private string? Local(string id, string? prefix, string reference, List<string> wanted)
        {
            if (reference.StartsWith("#/definitions/", StringComparison.Ordinal) || reference.StartsWith("#/$defs/", StringComparison.Ordinal))
            {
                // A reference into the schema's own definitions: the kind's are where they were; an adopted schema's moved.
                if (prefix is null)
                {
                    return reference.StartsWith("#/$defs/", StringComparison.Ordinal) ? "#/definitions/" + reference["#/$defs/".Length..] : null;
                }

                var key = reference[(reference.IndexOf('/', 2) + 1)..];
                return _names.TryGetValue($"{id}#/definitions/{key}", out var placed) ? "#/definitions/" + placed : null;
            }

            if (reference == "#")
            {
                return prefix is null ? null : "#/definitions/" + prefix;
            }

            var hash = reference.IndexOf('#', StringComparison.Ordinal);
            var target = hash < 0 ? reference : reference[..hash];
            var fragment = hash < 0 ? string.Empty : reference[hash..];
            if (target.Length == 0)
            {
                Unresolve(reference, $"the reference '{reference}' points into the schema itself other than its definitions, which the checks do not follow");
                return null;
            }

            if (!SchemaId().IsMatch(target))
            {
                Unresolve(reference, $"the reference '{reference}' is not to a schema by its id, which the reader does not follow");
                return null;
            }

            if (fragment.Length > 1 && !fragment.StartsWith("#/definitions/", StringComparison.Ordinal))
            {
                Unresolve(reference, $"the reference '{reference}' points into a part of '{target}' other than its definitions, which the reader does not follow");
                return null;
            }

            if (!Has(target))
            {
                wanted.Add(target);

                // Named now and placed when it is read; a schema that turns out not to be held leaves the reference dangling,
                // which a check reports as not checked.
                _names[target] = Free(DefinitionName(target), null);
            }

            if (fragment.Length > 1)
            {
                var key = fragment["#/definitions/".Length..];
                return "#/definitions/" + (_names.TryGetValue($"{target}#/definitions/{key}", out var placed) ? placed : key);
            }

            // The kind's own schema is the bundle's root.
            return _names.TryGetValue(target, out var name) ? (name.Length > 0 ? "#/definitions/" + name : "#") : null;
        }

        /// <summary>A definition name for <paramref name="wanted"/> that no other definition holds differently.</summary>
        private string Free(string wanted, JsonObject? content)
        {
            var name = wanted;
            for (var i = 2; _definitions[name] is JsonObject held && (content is null || !JsonNode.DeepEquals(held, content)); i++)
            {
                name = $"{wanted}~{i}";
            }

            return name;
        }

        /// <summary>How a schema id is named among the definitions, as the data definitions name their bundled ones.</summary>
        private static string DefinitionName(string id)
        {
            var parts = id.Split(':');
            return parts.Length == 4 ? $"{parts[2]}.{parts[3]}" : id.Replace(':', '.');
        }

        private static IEnumerable<JsonObject> Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    yield return obj;
                    foreach (var (_, child) in obj.ToList())
                    {
                        foreach (var inner in Walk(child))
                        {
                            yield return inner;
                        }
                    }

                    break;
                case JsonArray list:
                    foreach (var item in list.ToList())
                    {
                        foreach (var inner in Walk(item))
                        {
                            yield return inner;
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>A schema id: <c>authority:source:entity:major.minor.patch</c>.</summary>
    [GeneratedRegex(@"^[\w\-\.]+:[\w\-\.]+:[\w\-\.]+:[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaId();
}
