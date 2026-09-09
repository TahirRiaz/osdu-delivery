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

/// <summary>
/// The <c>snapshot</c> verb's engine (design.md section 11): captures schema and reference snapshots from OSDU, or
/// from local files for offline work, and mints immutable versions in the store.
/// </summary>
public sealed partial class SnapshotBuilder
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
            types.Add(await CaptureTypeAsync(osdu, typeSpec, ct).ConfigureAwait(false));
        }

        var captured = _time.GetUtcNow();
        var version = Storage.FileSnapshotStore.MintVersion(captured);

        // A capture covers the types it declares, which may be part of the estate. Merging onto the current
        // snapshot keeps a version meaning "the whole cache as of this capture", so a mapping that resolves a type
        // this spec does not mention still finds it.
        var current = await CurrentAsync(ct).ConfigureAwait(false);
        var snapshot = current is null
            ? new ReferenceSnapshot(version, captured, types)
            : current.With(version, captured, types);

        await _store.SaveReferencesAsync(snapshot, makeCurrent, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Reference snapshot {Version} saved with {Count} type(s), {Refreshed} of them refreshed{Current}.",
            snapshot.Version, snapshot.Types.Count, types.Count, makeCurrent ? " (current)" : string.Empty);
        return snapshot;
    }

    /// <summary>The snapshot a refresh builds on: the current version, or null when the store holds none.</summary>
    private async Task<ReferenceSnapshot?> CurrentAsync(CancellationToken ct)
    {
        var version = await _store.CurrentReferenceVersionAsync(ct).ConfigureAwait(false);
        return version is null ? null : await _store.LoadReferencesAsync(version, ct).ConfigureAwait(false);
    }
}

/// <summary>Pages one type out of the OSDU search index and caches the declared paths of every hit.</summary>
public sealed partial class SnapshotBuilder
{
    private const int SearchPageSize = 1000;

    /// <summary>Captures one reference type: every hit of its search kind, projected onto the paths it declares.</summary>
    public async Task<ReferenceType> CaptureTypeAsync(OsduConnection osdu, ReferenceTypeSpec typeSpec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(typeSpec);
        typeSpec.Validate();

        var items = new List<ReferenceItem>();
        var coverage = typeSpec.Fields.ToDictionary(f => f.Name, _ => 0, StringComparer.OrdinalIgnoreCase);
        string? cursor = null;
        do
        {
            var body = new JsonObject
            {
                ["kind"] = typeSpec.Kind,
                ["query"] = typeSpec.Query,
                ["limit"] = SearchPageSize,
                ["returnedFields"] = new JsonArray(typeSpec.Fields
                    .Select(f => (JsonNode)JsonValue.Create(f.Path))
                    .Prepend(JsonValue.Create("id"))
                    .ToArray()),
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
                    if (Project(hit, typeSpec.Fields, coverage) is { } item)
                    {
                        items.Add(item);
                    }
                }
            }

            cursor = page["cursor"]?.GetValue<string>();
        }
        while (!string.IsNullOrEmpty(cursor));

        foreach (var field in typeSpec.Fields.Where(f => coverage[f.Name] == 0))
        {
            // Silence here used to look like bad source data at render time, so an empty path is reported at capture.
            _logger.LogWarning(
                "Reference type {Type}: no item carried '{Path}', so nothing is cached under '{Name}'. Check the path against the kind {Kind}.",
                typeSpec.Name, field.Path, field.Name, typeSpec.Kind);
        }

        _logger.LogInformation(
            "Captured {Count} {Type} item(s) with {Fields}.",
            items.Count, typeSpec.Name, string.Join(", ", typeSpec.Fields.Select(f => $"{f.Name}={coverage[f.Name]}")));
        return new ReferenceType(typeSpec.Name, typeSpec.EntityType, items.OrderBy(i => i.Id, StringComparer.Ordinal));
    }

    /// <summary>Projects one search hit onto the declared paths, keeping whatever shape each path yields.</summary>
    private static ReferenceItem? Project(JsonObject hit, IReadOnlyList<ReferenceFieldSpec> fields, Dictionary<string, int> coverage)
    {
        var id = hit["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var values = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            var hits = JsonPathReader.SelectNodes(hit, field.Path);
            if (hits.Count == 0)
            {
                continue;
            }

            values[field.Name] = ReferenceValue.OfMany(hits);
            coverage[field.Name]++;
        }

        return new ReferenceItem(id, values);
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
