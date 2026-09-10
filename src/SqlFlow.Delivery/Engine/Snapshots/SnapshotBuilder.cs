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
        var root = await osdu.GetJsonAsync($"{SchemaPath}/{Http.UrlPath.EscapeSegment(kind)}", ct).ConfigureAwait(false);
        var bundled = await SchemaBundler.BundleAsync(root, kind, async (reference, _, token) =>
        {
            var id = reference.Replace("#/definitions/", string.Empty, StringComparison.Ordinal);
            var schema = await osdu.GetJsonAsync($"{SchemaPath}/{Http.UrlPath.EscapeSegment(id)}", token).ConfigureAwait(false);
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
        if (await UnchangedAsync(snapshot, ct).ConfigureAwait(false) is { } unchanged)
        {
            return unchanged;
        }

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

        if (await UnchangedAsync(snapshot, ct).ConfigureAwait(false) is { } unchanged)
        {
            return unchanged;
        }

        await _store.SaveReferencesAsync(snapshot, makeCurrent, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Reference snapshot {Version} saved with {Count} type(s), {Refreshed} of them refreshed{Current}.",
            snapshot.Version, snapshot.Types.Count, types.Count, makeCurrent ? " (current)" : string.Empty);
        return snapshot;
    }

    /// <summary>
    /// The current snapshot when a capture produced byte-identical content, or null when the content really moved.
    ///
    /// A reference version is a timestamp, and it enters the render context, so minting one for a capture that
    /// found nothing new would change every record's metadata hash and redeliver the whole estate for no reason.
    /// Schema snapshots are already content-addressed and immune to this; comparing content here gives reference
    /// snapshots the same property, so recapturing defensively is free.
    /// </summary>
    private async Task<ReferenceSnapshot?> UnchangedAsync(ReferenceSnapshot captured, CancellationToken ct)
    {
        var current = await CurrentAsync(ct).ConfigureAwait(false);
        if (current is null || !string.Equals(current.ContentHash(), captured.ContentHash(), StringComparison.Ordinal))
        {
            return null;
        }

        _logger.LogInformation(
            "Reference capture matched the current snapshot {Version} exactly; keeping it rather than minting a version that would re-render every record.",
            current.Version);
        return current;
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

    /// <summary>The cursor search the capture pages through (openapi search v2, POST /query_with_cursor).</summary>
    private const string SearchPath = "/api/search/v2/query_with_cursor";

    /// <summary>The schema service's read endpoint (openapi schema_service v1, GET /schema/{id}).</summary>
    private const string SchemaPath = "/api/schema-service/v1/schema";

    /// <summary>Captures one reference type: every hit of its search kind, projected onto the paths it declares.</summary>
    public async Task<ReferenceType> CaptureTypeAsync(OsduConnection osdu, ReferenceTypeSpec typeSpec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(typeSpec);
        typeSpec.Validate();

        var items = new List<ReferenceItem>();
        var coverage = typeSpec.Fields.ToDictionary(f => f.Name, _ => 0, StringComparer.OrdinalIgnoreCase);
        string? cursor = null;
        string? previousCursor = null;
        var finished = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
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

                var page = await osdu.PostJsonAsync(SearchPath, body, ct).ConfigureAwait(false);
                var results = page["results"] as JsonArray;
                var inPage = results?.Count ?? 0;
                if (results is not null)
                {
                    foreach (var hit in results.OfType<JsonObject>())
                    {
                        if (Project(hit, typeSpec.Fields, coverage) is { } item)
                        {
                            items.Add(item);
                        }
                    }
                }

                previousCursor = cursor;
                cursor = page["cursor"] is JsonValue value && value.TryGetValue<string>(out var next) ? next : null;

                // The search service hands back a cursor for the page after the last one too, and that page is
                // empty; ending only on a null cursor would page forever. An empty page is the end, and a cursor
                // that has not moved would be the same page again.
                if (inPage == 0 || string.IsNullOrEmpty(cursor))
                {
                    finished = true;
                    break;
                }

                if (string.Equals(cursor, previousCursor, StringComparison.Ordinal))
                {
                    throw new DeliveryException(
                        $"Reference type {typeSpec.Name}: the search service returned the same cursor twice for kind {typeSpec.Kind} after {items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} item(s), so the capture would not advance.");
                }
            }
        }
        finally
        {
            if (!finished && cursor is not null)
            {
                await CloseCursorAsync(osdu, cursor).ConfigureAwait(false);
            }
        }

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

    /// <summary>
    /// Releases the search context a capture stopped part way through (openapi search v2,
    /// DELETE /query_with_cursor/{cursor}), so an abandoned scroll does not hold index resources until it expires.
    /// The capture's own failure is what the caller sees; failing to close is logged and nothing more.
    /// </summary>
    private async Task CloseCursorAsync(OsduConnection osdu, string cursor)
    {
        try
        {
            await osdu.DeleteAsync(SearchPath + "/" + Http.UrlPath.EscapeSegment(cursor), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException)
        {
            _logger.LogWarning("Could not close the search cursor after the capture stopped: {Message}", HeaderRedaction.RedactMessage(ex.Message));
        }
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

    private OsduConnection(
        string endpoint,
        TargetAuth auth,
        IReadOnlyDictionary<string, string> headers,
        FlowReliability reliability,
        ISecretResolver secrets,
        HttpMessageHandler? handler,
        bool allowLoopback)
    {
        Endpoint = endpoint;
        _auth = auth;
        _headers = headers;
        _http = new HttpRuntime(reliability, secrets, handler: handler, allowLoopback: allowLoopback);
    }

    /// <summary>
    /// A connection over an endpoint and headers as a flow declares them, every <c>${env:...}</c> and
    /// <c>${keyvault:...}</c> reference in them resolved here, the way <see cref="Protocols.ProtocolFactory.ClientAsync"/>
    /// resolves a delivery target's. A declared value is never used as a URL or a header unresolved.
    /// </summary>
    /// <remarks>
    /// <paramref name="handler"/> replaces the built transport (null builds the configured one) and
    /// <paramref name="allowLoopback"/> lets the URL guard accept a loopback endpoint; both exist for tests.
    /// </remarks>
    /// <exception cref="DeliveryException">The endpoint does not resolve to an absolute http or https URL.</exception>
    public static async Task<OsduConnection> CreateAsync(
        string endpoint,
        TargetAuth auth,
        IReadOnlyDictionary<string, string> headers,
        FlowReliability reliability,
        ISecretResolver secrets,
        HttpMessageHandler? handler = null,
        bool allowLoopback = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(reliability);
        ArgumentNullException.ThrowIfNull(secrets);

        var resolved = (await secrets.ResolveAsync(endpoint, ct).ConfigureAwait(false)).Trim().TrimEnd('/');
        if (!Uri.TryCreate(resolved, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            // The declared value is named, never what it resolved to: a reference is safe to show, its value may not be.
            throw new DeliveryException($"The OSDU endpoint '{endpoint}' does not resolve to an absolute http or https URL.");
        }

        var resolvedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            resolvedHeaders[name] = await secrets.ResolveAsync(value, ct).ConfigureAwait(false);
        }

        return new OsduConnection(resolved, auth, resolvedHeaders, reliability, secrets, handler, allowLoopback);
    }

    public string Endpoint { get; }

    public async Task<JsonObject> GetJsonAsync(string path, CancellationToken ct)
    {
        var url = new Uri(Endpoint + "/" + path.TrimStart('/'));
        var result = await SendAsync(auth => _http.Data.SendAsync(() => Build(HttpMethod.Get, url, auth, null), ct: ct), ct).ConfigureAwait(false);
        return JsonNode.Parse(result.Body) as JsonObject ?? throw new DeliveryException($"{url} did not return a JSON object.");
    }

    public async Task<JsonObject> PostJsonAsync(string path, JsonObject body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var url = new Uri(Endpoint + "/" + path.TrimStart('/'));
        var bytes = CanonicalJson.ToBytes(body);
        // Only ever a search: a read, safe to repeat.
        var result = await SendAsync(auth => _http.Data.SendAsync(() => Build(HttpMethod.Post, url, auth, bytes), ct: ct, idempotent: true), ct).ConfigureAwait(false);
        return JsonNode.Parse(result.Body) as JsonObject ?? throw new DeliveryException($"{url} did not return a JSON object.");
    }

    /// <summary>A DELETE whose body is not read, for releasing a server-side resource such as a search cursor.</summary>
    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        var url = new Uri(Endpoint + "/" + path.TrimStart('/'));
        await SendAsync(auth => _http.Data.SendAsync(() => Build(HttpMethod.Delete, url, auth, null), new HashSet<int> { 404 }, ct: ct), ct).ConfigureAwait(false);
    }

    /// <summary>Sends under the resolved auth, retrying once with a fresh token when the service answers 401.</summary>
    private async Task<HttpFetchResult> SendAsync(Func<AppliedAuth, Task<HttpFetchResult>> send, CancellationToken ct)
    {
        var auth = await _http.AuthResolver.ResolveAsync(_auth, _http.Auth, ct).ConfigureAwait(false);
        try
        {
            return await send(auth).ConfigureAwait(false);
        }
        catch (HttpStatusException ex) when (ex.StatusCode == 401)
        {
            _http.AuthResolver.Invalidate();
            var refreshed = await _http.AuthResolver.ResolveAsync(_auth, _http.Auth, ct).ConfigureAwait(false);
            return await send(refreshed).ConfigureAwait(false);
        }
    }

    private HttpRequestMessage Build(HttpMethod method, Uri url, AppliedAuth auth, byte[]? body)
    {
        var request = new HttpRequestMessage(method, url);
        foreach (var (name, value) in _headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        auth.ApplyTo(request);
        if (OsduCorrelation.Current is { } correlation)
        {
            request.Headers.TryAddWithoutValidation(OsduCorrelation.HeaderName, correlation);
        }

        // A bodiless request still carries Content-Type: application/json, as every OSDU call from this system does
        // (see OsduHttpClient.JsonBody for why the services insist).
        request.Content = Protocols.OsduHttpClient.JsonBody(body);
        return request;
    }

    public void Dispose() => _http.Dispose();
}
