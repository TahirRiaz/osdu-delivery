using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Search;
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
        return await WriteAsync(types, capture, readings: [], ct).ConfigureAwait(false);
    }

    private void CheckDeclared(IReadOnlyList<ReferenceType> types, IReadOnlyList<ReferenceTypeSpec> declared, CacheDeclaration partition, string directory)
    {
        var problems = new List<string>();

        // A lookup table is filled from its own origin, a table or a dictionary, wherever the flow runs, so it is neither
        // required in an import nor accepted from one: the files stand in for OSDU alone.
        foreach (var spec in declared.Where(spec => !spec.IsLookup && !types.Any(t => t.Name.Equals(spec.Name, StringComparison.OrdinalIgnoreCase))))
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

            if (spec.IsLookup)
            {
                problems.Add($"{type.Name}.json holds {spec.Name}, which cache flow '{_flow}' fills from {spec.Describe()}; a lookup table is captured from its origin, never imported");
                continue;
            }

            if (!type.EntityType.Equals(spec.EntityType, StringComparison.Ordinal))
            {
                problems.Add($"{type.Name}.json holds entity type {type.EntityType}, and cache flow '{_flow}' declares {spec.EntityType}");
            }

            // The files stand in for what a search of the partition would return, so every record is an OSDU record of the
            // declared entity type in this partition. Anything else would pass off hand-made rows as the platform's.
            var foreign = type.Items.Where(item => !IsRecordOf(item.Id, spec.EntityType)).Select(item => item.Id).Take(5).ToList();
            if (foreign.Count > 0)
            {
                problems.Add(
                    $"{type.Name}.json holds records whose ids are not ids of {spec.EntityType} records in partition '{_scope}' ({string.Join(", ", foreign)}); an import holds what a search of the partition would return, each id written {_scope}:{spec.EntityType}:<code>");
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

    /// <summary>Whether <paramref name="id"/> is an OSDU id (without version) of a record of <paramref name="entityType"/> in this partition.</summary>
    private bool IsRecordOf(string id, string entityType)
    {
        var parts = id.Split(':', 3);
        return parts.Length == 3
            && string.Equals(parts[0], _scope, StringComparison.Ordinal)
            && string.Equals(parts[1], entityType, StringComparison.Ordinal)
            && parts[2].TrimEnd(':').Length > 0;
    }

    /// <summary>
    /// Captures every type of <paramref name="spec"/> through the OSDU search service, reads the partition's system
    /// properties from the platform's services (<see cref="SystemPropertyCapture"/>), which every capture does whatever
    /// the flow declares, and merges both into the partition's cache.
    /// </summary>
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

        var readings = await SystemPropertyCapture.ReadAsync(osdu, _scope, _logger, ct).ConfigureAwait(false);
        return await WriteAsync(types, capture, readings, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges the types into the partition's cache, which writes the next version unless the merge changes no cached content.
    /// A version label is a timestamp and it enters the render context, so writing one for a capture that found nothing new
    /// would change the metadata hash of every record built from the cache and deliver them all again for no reason.
    /// Comparing content makes refreshing a cache as often as anyone likes free. Every producer ends here, whatever the origin
    /// of the types it captured, so one refresh of a flow writes one version.
    /// </summary>
    public async Task<CacheWrite> WriteAsync(
        IReadOnlyList<ReferenceType> types, CacheCapture capture, IReadOnlyList<SystemPropertyReading> readings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(readings);
        var write = await _store.MergeAsync(_scope, _flow, types, capture, _time.GetUtcNow(), readings, ct).ConfigureAwait(false);
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

/// <summary>Reads one type out of the OSDU search index, whole, and caches the declared paths of every record.</summary>
public sealed partial class SnapshotBuilder
{
    private const int SearchPageSize = OsduSearch.MaxPage;

    /// <summary>The cursor search the capture pages through (openapi search v2, POST /query_with_cursor).</summary>
    private const string SearchPath = "/api/search/v2/query_with_cursor";

    /// <summary>The plain search that counts a type whose cursor names no exact total (openapi search v2, POST /query).</summary>
    private const string QueryPath = "/api/search/v2/query";

    /// <summary>
    /// Captures one reference type: every record of its search kind, projected onto the paths it declares. The records are
    /// read through the shared cursor reader (<see cref="OsduSearch.PagesAsync"/>), which hands each one out once and every
    /// one of them or fails, reading a type again once when its first read fails part way or comes back short. A type that
    /// cannot be read whole fails the capture, and with it the refresh, before anything is written, so the partition's
    /// cache keeps the version it had rather than one missing records.
    /// </summary>
    /// <exception cref="DeliveryException">The type could not be read whole.</exception>
    public async Task<ReferenceType> CaptureTypeAsync(OsduConnection osdu, ReferenceTypeSpec typeSpec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(typeSpec);
        typeSpec.Validate();

        var items = new Dictionary<string, ReferenceItem>(StringComparer.Ordinal);
        // One pool for the capture: a value many records hold (the field a thousand wellbores lie in) is kept once.
        var pool = new StringPool();
        var coverage = typeSpec.Fields.ToDictionary(f => f.Name, _ => 0, StringComparer.OrdinalIgnoreCase);
        var search = new OsduSearch(osdu.Client, QueryPath, SearchPath, _logger);
        var query = new OsduSearchQuery
        {
            Kind = typeSpec.Kind ?? throw new DeliveryException($"Reference type {typeSpec.Name} names no kind, so there is nothing to search for it."),
            Query = typeSpec.Query,
            ReturnedFields = typeSpec.Fields.Select(f => f.Path).Prepend("id").ToList(),
        };
        await foreach (var page in search.PagesAsync(query, SearchPageSize, ct).ConfigureAwait(false))
        {
            foreach (var hit in page.Hits)
            {
                // The reader hands out only records with an id, each once: a hit that is neither is a fault of the reader,
                // which no capture may cache around.
                var record = JsonNode.Parse(hit.GetRawText()) as JsonObject
                    ?? throw new DeliveryException($"Reference type {typeSpec.Name}: the search of kind {typeSpec.Kind} handed on a hit that is not a record.");
                var item = Project(record, typeSpec.Fields, pool)
                    ?? throw new DeliveryException($"Reference type {typeSpec.Name}: the search of kind {typeSpec.Kind} handed on a record without an id.");
                if (!items.TryAdd(item.Id, item))
                {
                    throw new DeliveryException($"Reference type {typeSpec.Name}: the search of kind {typeSpec.Kind} handed on record {item.Id} twice.");
                }

                foreach (var name in item.Fields.Keys)
                {
                    coverage[name]++;
                }
            }
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

    /// <summary>Projects one search hit onto the declared paths, keeping whatever shape each path yields.</summary>
    private static ReferenceItem? Project(JsonObject hit, IReadOnlyList<ReferenceFieldSpec> fields, StringPool pool)
    {
        var id = hit["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var values = new List<KeyValuePair<string, ReferenceValue>>(fields.Count);
        foreach (var field in fields)
        {
            var hits = JsonPathReader.SelectNodes(hit, field.Path);
            if (hits.Count == 0)
            {
                continue;
            }

            values.Add(KeyValuePair.Create(field.Name, ReferenceValue.OfMany(hits, pool)));
        }

        return new ReferenceItem(id, ReferenceFields.Of(values, pool));
    }
}

/// <summary>A read-only OSDU connection for a cache capture: a flow's auth and headers against an OSDU base URL.</summary>
public sealed class OsduConnection : IDisposable
{
    private readonly HttpRuntime _http;

    private OsduConnection(
        string endpoint,
        TargetAuth auth,
        IReadOnlyDictionary<string, string> headers,
        FlowReliability reliability,
        ISecretResolver secrets,
        HttpMessageHandler? handler,
        bool allowLoopback,
        IHttpObserver? observer)
    {
        Endpoint = endpoint;
        _http = new HttpRuntime(reliability, secrets, handler: handler, allowLoopback: allowLoopback, observer: observer);
        Client = new Protocols.OsduHttpClient(_http, endpoint, auth, headers);
    }

    /// <summary>
    /// A connection over an endpoint and headers as a flow declares them, every <c>${env:...}</c> and
    /// <c>${keyvault:...}</c> reference in them resolved here, the way <see cref="Protocols.ProtocolFactory.ClientAsync"/>
    /// resolves a delivery target's. A declared value is never used as a URL or a header unresolved.
    /// </summary>
    /// <remarks>
    /// <paramref name="handler"/> replaces the built transport (null builds the configured one) and
    /// <paramref name="allowLoopback"/> lets the URL guard accept a loopback endpoint; both exist for tests.
    /// <paramref name="observer"/> is told of every call the capture sends, which a cache run puts on its trace.
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
        IHttpObserver? observer = null,
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

        return new OsduConnection(resolved, auth, resolvedHeaders, reliability, secrets, handler, allowLoopback, observer);
    }

    public string Endpoint { get; }

    /// <summary>
    /// The connection as the protocols' client, over the same transport, auth and headers: what the shared search reader
    /// sends through, so a capture pages its types exactly as every other reader of the search service does.
    /// </summary>
    public Protocols.OsduHttpClient Client { get; }

    /// <summary>A GET of a service's JSON object, under the flow's auth and headers.</summary>
    public async Task<JsonObject> GetJsonAsync(string path, CancellationToken ct)
    {
        var url = Url(path);
        var result = await Client.SendJsonAsync(HttpMethod.Get, url, null, null, ct).ConfigureAwait(false);
        return JsonNode.Parse(result.Body) as JsonObject ?? throw new DeliveryException($"{HeaderRedaction.DescribeUrl(url)} did not return a JSON object.");
    }

    /// <summary>A POST of a search, a read the service answers alike however often it is asked, so it is repeated like any read.</summary>
    public async Task<JsonObject> PostJsonAsync(string path, JsonObject body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var url = Url(path);
        var result = await Client.SendJsonAsync(HttpMethod.Post, url, body, null, ct, idempotent: true).ConfigureAwait(false);
        return JsonNode.Parse(result.Body) as JsonObject ?? throw new DeliveryException($"{HeaderRedaction.DescribeUrl(url)} did not return a JSON object.");
    }

    /// <summary>A path under the endpoint, taken as written: no token in it is substituted.</summary>
    private Uri Url(string path) => new(Endpoint + "/" + path.TrimStart('/'));

    public void Dispose() => _http.Dispose();
}
