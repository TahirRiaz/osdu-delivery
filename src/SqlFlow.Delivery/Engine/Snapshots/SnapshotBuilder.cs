using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Snapshots;

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
        try
        {
            return JsonSerializer.Deserialize<ReferenceCaptureSpec>(json, Options) ?? throw new FlowValidationException($"{source}: empty capture spec.");
        }
        catch (JsonException ex)
        {
            throw new FlowValidationException($"{source}: invalid capture spec - {ex.Message}", ex);
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

    /// <summary>Data fields to capture for matching (data.Code, data.Name, data.ID). The id is always captured.</summary>
    [JsonPropertyName("fields")]
    public List<string> Fields { get; init; } = ["data.Code", "data.Name", "data.ID"];

    /// <summary>Optional search query narrowing the capture (default *).</summary>
    [JsonPropertyName("query")]
    public string Query { get; init; } = "*";
}

/// <summary>
/// The <c>snapshot</c> verb's engine (design.md section 11): captures schema and reference snapshots from OSDU, or
/// from local files for offline work, and mints immutable versions in the store.
/// </summary>
public sealed class SnapshotBuilder
{
    private readonly ISnapshotStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<SnapshotBuilder> _logger;

    public SnapshotBuilder(ISnapshotStore store, TimeProvider time, ILogger<SnapshotBuilder> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <summary>Bundles a kind's schema from a local data-definitions checkout and saves it.</summary>
    public async Task<SchemaSnapshot> SchemaFromDirectoryAsync(string dataRoot, string kind, CancellationToken ct = default)
    {
        var file = SchemaBundler.LocateKindFile(dataRoot, kind);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)) as JsonObject
            ?? throw new DeliveryException($"Schema file '{file}' is not a JSON object.");
        var bundled = await SchemaBundler.BundleAsync(root, file, SchemaBundler.DirectoryResolver(dataRoot), ct).ConfigureAwait(false);
        var snapshot = new SchemaSnapshot(kind, bundled, _time.GetUtcNow());
        await _store.SaveSchemaAsync(snapshot, ct).ConfigureAwait(false);
        _logger.LogInformation("Schema snapshot {Kind} version {Version} saved from {File}.", kind, snapshot.Version, file);
        return snapshot;
    }

    /// <summary>Fetches a kind's schema from the OSDU schema service, bundles its references and saves it.</summary>
    public async Task<SchemaSnapshot> SchemaFromOsduAsync(OsduConnection osdu, string kind, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        var root = await osdu.GetJsonAsync($"/api/schema-service/v1/schema/{Uri.EscapeDataString(kind)}", ct).ConfigureAwait(false);
        var bundled = await SchemaBundler.BundleAsync(root, kind, async (reference, _, token) =>
        {
            var id = reference.Replace("#/definitions/", string.Empty, StringComparison.Ordinal);
            var schema = await osdu.GetJsonAsync($"/api/schema-service/v1/schema/{Uri.EscapeDataString(id)}", token).ConfigureAwait(false);
            return (id, schema);
        }, ct).ConfigureAwait(false);
        var snapshot = new SchemaSnapshot(kind, bundled, _time.GetUtcNow());
        await _store.SaveSchemaAsync(snapshot, ct).ConfigureAwait(false);
        _logger.LogInformation("Schema snapshot {Kind} version {Version} saved from {Endpoint}.", kind, snapshot.Version, osdu.Endpoint);
        return snapshot;
    }

    /// <summary>Builds a reference snapshot from local type files ({Name}.json in the store's type format) and mints a version.</summary>
    public async Task<ReferenceSnapshot> ReferencesFromDirectoryAsync(string directory, bool makeCurrent, CancellationToken ct = default)
    {
        var types = new List<ReferenceType>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Equals("manifest", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var node = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)) as JsonObject
                ?? throw new DeliveryException($"Reference file '{file}' is not a JSON object.");
            types.Add(ReferenceType.FromJson(name, node));
        }

        var captured = _time.GetUtcNow();
        var snapshot = new ReferenceSnapshot(Storage.FileSnapshotStore.MintVersion(captured), captured, types);
        await _store.SaveReferencesAsync(snapshot, makeCurrent, ct).ConfigureAwait(false);
        _logger.LogInformation("Reference snapshot {Version} saved with {Count} type(s){Current}.", snapshot.Version, types.Count, makeCurrent ? " (current)" : string.Empty);
        return snapshot;
    }

    /// <summary>Captures reference and master data through the OSDU search service and mints a version.</summary>
    public async Task<ReferenceSnapshot> ReferencesFromOsduAsync(OsduConnection osdu, ReferenceCaptureSpec spec, bool makeCurrent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(spec);
        var types = new List<ReferenceType>();
        foreach (var typeSpec in spec.Types)
        {
            var items = new List<ReferenceItem>();
            string? cursor = null;
            var fieldNames = typeSpec.Fields.Select(f => f.StartsWith("data.", StringComparison.Ordinal) ? f[5..] : f).ToList();
            do
            {
                var body = new JsonObject
                {
                    ["kind"] = typeSpec.Kind,
                    ["query"] = typeSpec.Query,
                    ["limit"] = 1000,
                    ["returnedFields"] = new JsonArray(typeSpec.Fields.Select(f => (JsonNode)JsonValue.Create(f)).Prepend(JsonValue.Create("id")).ToArray()),
                };
                if (cursor is not null)
                {
                    body["cursor"] = cursor;
                }

                var page = await osdu.PostJsonAsync("/api/search/v2/query_with_cursor", body, ct).ConfigureAwait(false);
                if (page["results"] is JsonArray results)
                {
                    foreach (var hit in results.OfType<JsonObject>())
                    {
                        var id = hit["id"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }

                        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (hit["data"] is JsonObject data)
                        {
                            foreach (var name in fieldNames)
                            {
                                if (data[name] is JsonValue v)
                                {
                                    fields[name] = v.TryGetValue<string>(out var s) ? s : v.ToJsonString();
                                }
                            }
                        }

                        items.Add(new ReferenceItem(id, fields));
                    }
                }

                cursor = page["cursor"]?.GetValue<string>();
            }
            while (!string.IsNullOrEmpty(cursor));

            _logger.LogInformation("Captured {Count} {Type} item(s).", items.Count, typeSpec.Name);
            types.Add(new ReferenceType(typeSpec.Name, typeSpec.EntityType, items.OrderBy(i => i.Id, StringComparer.Ordinal)));
        }

        var captured = _time.GetUtcNow();
        var snapshot = new ReferenceSnapshot(Storage.FileSnapshotStore.MintVersion(captured), captured, types);
        await _store.SaveReferencesAsync(snapshot, makeCurrent, ct).ConfigureAwait(false);
        _logger.LogInformation("Reference snapshot {Version} saved with {Count} type(s){Current}.", snapshot.Version, types.Count, makeCurrent ? " (current)" : string.Empty);
        return snapshot;
    }
}

/// <summary>A read-only OSDU connection for snapshot capture: the flow's target auth and headers against an OSDU base URL.</summary>
public sealed class OsduConnection : IDisposable
{
    private readonly HttpRuntime _http;
    private readonly TargetAuth _auth;
    private readonly IReadOnlyDictionary<string, string> _headers;

    public OsduConnection(string endpoint, TargetAuth auth, IReadOnlyDictionary<string, string> headers, FlowReliability reliability, ISecretResolver secrets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(reliability);
        ArgumentNullException.ThrowIfNull(secrets);
        Endpoint = endpoint.TrimEnd('/');
        _auth = auth;
        _headers = headers;
        _http = new HttpRuntime(reliability, secrets);
    }

    public string Endpoint { get; }

    public async Task<JsonObject> GetJsonAsync(string path, CancellationToken ct)
    {
        var auth = await _http.AuthResolver.ResolveAsync(_auth, _http.Auth, ct).ConfigureAwait(false);
        var url = new Uri(Endpoint + "/" + path.TrimStart('/'));
        var result = await _http.Data.SendAsync(() => Build(HttpMethod.Get, url, auth, null), ct: ct).ConfigureAwait(false);
        return JsonNode.Parse(result.Body) as JsonObject ?? throw new DeliveryException($"{url} did not return a JSON object.");
    }

    public async Task<JsonObject> PostJsonAsync(string path, JsonObject body, CancellationToken ct)
    {
        var auth = await _http.AuthResolver.ResolveAsync(_auth, _http.Auth, ct).ConfigureAwait(false);
        var url = new Uri(Endpoint + "/" + path.TrimStart('/'));
        var bytes = CanonicalJson.ToBytes(body);
        var result = await _http.Data.SendAsync(() => Build(HttpMethod.Post, url, auth, bytes), ct: ct).ConfigureAwait(false);
        return JsonNode.Parse(result.Body) as JsonObject ?? throw new DeliveryException($"{url} did not return a JSON object.");
    }

    private HttpRequestMessage Build(HttpMethod method, Uri url, AppliedAuth auth, byte[]? body)
    {
        var request = new HttpRequestMessage(method, url);
        foreach (var (name, value) in _headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        auth.ApplyTo(request);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    public void Dispose() => _http.Dispose();
}
