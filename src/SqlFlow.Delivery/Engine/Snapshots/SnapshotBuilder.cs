using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// The cache capture's engine (design.md section 6.2): captures a cache flow's types from OSDU, or reads them from type
/// files for offline work, and merges them into the cache of the flow's partition, which writes a version when the cached
/// content moved. A capture holds every record the flow's queries match with every path the partition keeps for its types,
/// so what one project captures is complete for every pipeline that reads the partition.
/// </summary>
public sealed partial class SnapshotBuilder
{
    private readonly ICacheStore _store;
    private readonly string _scope;
    private readonly string _flow;
    private readonly TimeProvider _time;
    private readonly ILogger<SnapshotBuilder> _logger;

    /// <param name="store">Where the partition's cache lives.</param>
    /// <param name="scope">The partition whose cache the capture merges into.</param>
    /// <param name="flowName">The cache flow the capture is made for, which the version and the membership record.</param>
    /// <param name="time">The clock the capture instant is read from.</param>
    /// <param name="logger">Where the capture reports what it found.</param>
    public SnapshotBuilder(ICacheStore store, string scope, string flowName, TimeProvider time, ILogger<SnapshotBuilder> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _scope = scope;
        _flow = flowName;
        _time = time;
        _logger = logger;
    }

    /// <summary>Mints a version label from a capture instant: sortable, one per second.</summary>
    public static string MintVersion(DateTimeOffset capturedUtc) => CacheVersionLabel.Mint(capturedUtc);

    /// <summary>
    /// Reads every type file in <paramref name="directory"/> (<c>{Name}.json</c>: the entity type and its items, each an
    /// <c>id</c> and the cached values) and merges them into the partition's cache as the flow's capture. The files have to
    /// hold exactly the types the cache flow declares, each under its declared entity type and with no value the partition's
    /// cache does not keep for the type, because the cache flow is the definition of what it contributes and an import is no
    /// way around it. An import is checked against the partition's declaration exactly as a refresh is: a flow that disagrees
    /// with another flow of the partition is refused, and because a record the import holds replaces the cached record whole,
    /// the files of a type have to carry every value another flow of the partition keeps for it, or the import would drop
    /// those values from every record it holds.
    /// </summary>
    public async Task<CacheWrite> ImportDirectoryAsync(
        string directory, IReadOnlyList<ReferenceTypeSpec> declared, CacheCapture capture, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(capture);
        if (!Directory.Exists(directory))
        {
            throw new DeliveryException($"The directory '{directory}' to import cached types from does not exist.");
        }

        var partition = await _store.DeclarationAsync(_scope, ct).ConfigureAwait(false);
        partition.ThrowOnConflicts(_flow, declared);

        var types = new List<ReferenceType>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            JsonObject node;
            try
            {
                node = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)) as JsonObject
                    ?? throw new DeliveryException($"Cached type file '{file}' is not a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new DeliveryException($"Cached type file '{file}' is not valid JSON ({ex.Message}).", ex);
            }

            types.Add(ReferenceType.FromJson(Path.GetFileNameWithoutExtension(file), node));
        }

        if (types.Count == 0)
        {
            throw new DeliveryException($"The directory '{directory}' holds no cached type files ({{Name}}.json), so there is nothing to import.");
        }

        CheckDeclared(types, declared, partition, directory);
        return await WriteAsync(types, capture, ct).ConfigureAwait(false);
    }

    private void CheckDeclared(IReadOnlyList<ReferenceType> types, IReadOnlyList<ReferenceTypeSpec> declared, CacheDeclaration partition, string directory)
    {
        var problems = new List<string>();
        foreach (var spec in declared.Where(spec => !types.Any(t => t.Name.Equals(spec.Name, StringComparison.OrdinalIgnoreCase))))
        {
            problems.Add($"{spec.Name}.json is missing, and cache flow '{_flow}' declares {spec.Name}");
        }

        foreach (var type in types)
        {
            if (declared.FirstOrDefault(spec => spec.Name.Equals(type.Name, StringComparison.OrdinalIgnoreCase)) is not { } spec)
            {
                problems.Add($"{type.Name}.json holds a type cache flow '{_flow}' does not declare");
                continue;
            }

            if (!type.EntityType.Equals(spec.EntityType, StringComparison.Ordinal))
            {
                problems.Add($"{type.Name}.json holds entity type {type.EntityType}, and cache flow '{_flow}' declares {spec.EntityType}");
            }

            // The widened type is what a refresh of the flow would fetch: its own paths, then every path the partition keeps.
            var kept = partition.Widen(spec).Fields;
            var keptNames = kept.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in type.FieldNames.Where(name => !keptNames.Contains(name)))
            {
                problems.Add($"{type.Name}.json holds values under '{name}', which neither cache flow '{_flow}' nor any other cache flow of partition '{_scope}' captures for {spec.Name}");
            }

            if (type.Items.Count == 0)
            {
                // A type holding no record replaces no cached record, so it drops no value.
                continue;
            }

            var own = spec.Fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var carried = type.FieldNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = kept.Where(f => !own.Contains(f.Name) && !carried.Contains(f.Name)).ToList();
            if (missing.Count > 0)
            {
                var named = missing.Select(field =>
                {
                    var flows = partition.Of(spec.Name)
                        .Where(d => d.Fields.Any(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase)))
                        .Select(d => $"'{d.FlowName}'");
                    return $"'{field.Name}' (from {field.Path}, kept by cache flow {string.Join(", ", flows)})";
                });
                problems.Add(
                    $"{type.Name}.json holds no values under {string.Join(", ", named)}, which the cache of partition '{_scope}' keeps for {spec.Name}; a record the import holds replaces the cached record whole, so importing it would drop those values. Add them to the file");
            }
        }

        if (problems.Count > 0)
        {
            throw new DeliveryException($"The files under '{directory}' are not what cache flow '{_flow}' declares, so nothing was imported: {string.Join("; ", problems)}.");
        }
    }

    /// <summary>Captures every type of <paramref name="spec"/> through the OSDU search service and merges them into the partition's cache.</summary>
    public async Task<CacheWrite> CaptureAsync(OsduConnection osdu, ReferenceCaptureSpec spec, CacheCapture capture, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(capture);
        var types = new List<ReferenceType>(spec.Types.Count);
        foreach (var typeSpec in spec.Types)
        {
            types.Add(await CaptureTypeAsync(osdu, typeSpec, ct).ConfigureAwait(false));
        }

        return await WriteAsync(types, capture, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges the types into the partition's cache, which writes the next version unless the merge changes no cached content.
    /// A version label is a timestamp and it enters the render context, so writing one for a capture that found nothing new
    /// would change the metadata hash of every record built from the cache and deliver them all again for no reason.
    /// Comparing content makes refreshing a cache as often as anyone likes free.
    /// </summary>
    private async Task<CacheWrite> WriteAsync(IReadOnlyList<ReferenceType> types, CacheCapture capture, CancellationToken ct)
    {
        var write = await _store.MergeAsync(_scope, _flow, types, capture, _time.GetUtcNow(), ct).ConfigureAwait(false);
        if (write.Written)
        {
            _logger.LogInformation(
                "Cache of partition {Scope}: version {Version} written from cache flow {Flow}, holding {Types} type(s) and {Items} record(s).",
                _scope, write.Snapshot.Version, _flow, write.Snapshot.Types.Count, write.Snapshot.Types.Sum(t => t.Items.Count));
        }
        else
        {
            _logger.LogInformation(
                "Cache of partition {Scope}: cache flow {Flow} found exactly what version {Version} holds, so no version was written and nothing built from the cache renders again.",
                _scope, _flow, write.Snapshot.Version);
        }

        return write;
    }
}

/// <summary>Pages one type out of the OSDU search index and caches the declared paths of every hit.</summary>
public sealed partial class SnapshotBuilder
{
    private const int SearchPageSize = 1000;

    /// <summary>The cursor search the capture pages through (openapi search v2, POST /query_with_cursor).</summary>
    private const string SearchPath = "/api/search/v2/query_with_cursor";

    /// <summary>Captures one reference type: every hit of its search kind, projected onto the paths it declares.</summary>
    public async Task<ReferenceType> CaptureTypeAsync(OsduConnection osdu, ReferenceTypeSpec typeSpec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(typeSpec);
        typeSpec.Validate();

        var items = new Dictionary<string, ReferenceItem>(StringComparer.Ordinal);
        var repeated = 0;
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
                        if (Project(hit, typeSpec.Fields) is not { } item)
                        {
                            continue;
                        }

                        // A record the index hands back twice across pages is the same record: it is cached once, and
                        // counted once towards what each path covered.
                        if (!items.TryAdd(item.Id, item))
                        {
                            repeated++;
                            continue;
                        }

                        foreach (var name in item.Fields.Keys)
                        {
                            coverage[name]++;
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

        if (repeated > 0)
        {
            _logger.LogWarning(
                "Reference type {Type}: the search returned {Repeated} record(s) of kind {Kind} more than once; each is cached once.",
                typeSpec.Name, repeated, typeSpec.Kind);
        }

        if (items.Count == 0)
        {
            // Nothing matched at all: the paths are not the question, the kind and the query are.
            _logger.LogWarning(
                "Reference type {Type}: the search matched no record of kind {Kind} for query {Query}, so nothing is cached for it. Check the kind and the query.",
                typeSpec.Name, typeSpec.Kind, typeSpec.Query);
        }
        else
        {
            foreach (var field in typeSpec.Fields.Where(f => coverage[f.Name] == 0))
            {
                // Silence here used to look like bad source data at render time, so an empty path is reported at capture.
                _logger.LogWarning(
                    "Reference type {Type}: no item carried '{Path}', so nothing is cached under '{Name}'. Check the path against the kind {Kind}.",
                    typeSpec.Name, field.Path, field.Name, typeSpec.Kind);
            }
        }

        _logger.LogInformation(
            "Captured {Count} {Type} item(s) with {Fields}.",
            items.Count, typeSpec.Name, string.Join(", ", typeSpec.Fields.Select(f => $"{f.Name}={coverage[f.Name]}")));
        return new ReferenceType(typeSpec.Name, typeSpec.EntityType, items.Values.OrderBy(i => i.Id, StringComparer.Ordinal));
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
    private static ReferenceItem? Project(JsonObject hit, IReadOnlyList<ReferenceFieldSpec> fields)
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
        }

        return new ReferenceItem(id, values);
    }
}

/// <summary>A read-only OSDU connection for a cache capture: a flow's auth and headers against an OSDU base URL.</summary>
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
