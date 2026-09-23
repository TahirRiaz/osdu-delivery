using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine;

/// <summary>A flow's render inputs pinned together: the mapping, its template, the cache, the context and a renderer over them.</summary>
public sealed record ResolvedMapping(
    MappingDefinition Mapping,
    SchemaSnapshot Schema,
    ReferenceSnapshot References,
    RenderContext Context,
    MappingRenderer Renderer);

/// <summary>
/// Resolves a flow's <c>render</c> block into a <see cref="ResolvedMapping"/>: the pinned mapping from the repository, the
/// template version the mapping pins from the catalog, the version of the cache of the partition the flow delivers to (its
/// current version, or the one the flow pins) from the catalog, the schemas the mapping's searches pin, and the mapping
/// parameters the flow supplies. The preflight runs before anything is returned.
/// </summary>
public sealed class RenderResolver
{
    private readonly MappingCatalog _mappings;
    private readonly ICacheStore? _cache;
    private readonly ITemplateStore? _templates;
    private readonly ISecretResolver _secrets;
    private readonly IRecordSearch? _search;

    /// <param name="mappings">Where the flow's mapping is read from.</param>
    /// <param name="cache">The partitions' caches, for a mapping that reads one.</param>
    /// <param name="templates">The saved templates: the mapping's own, and those its searches pin.</param>
    /// <param name="secrets">Resolves the references the flow supplies as parameters.</param>
    /// <param name="search">
    /// Where the mapping's search sources are answered: the platform the flow delivers to. A mapping that searches and is
    /// resolved without one renders every search as finding nothing, which holds the records that need it.
    /// </param>
    public RenderResolver(MappingCatalog mappings, ICacheStore? cache, ITemplateStore? templates, ISecretResolver secrets, IRecordSearch? search = null)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(secrets);
        _mappings = mappings;
        _cache = cache;
        _templates = templates;
        _secrets = secrets;
        _search = search;
    }

    public async Task<ResolvedMapping> ResolveAsync(FlowDefinition flow, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var where = KeyPaths.Where(flow);
        var mapping = _mappings.Load(flow.Render.Mapping);

        if (_templates is null)
        {
            throw new FlowValidationException(
                $"{where}: mapping {mapping.Reference} fills template {mapping.Template}, and templates live in the module's database, which this host was started without. Start it with the module's connection (Osdu:Database:Connection or SQLFLOW_OSDU_DB), or with --db when the catalog's database holds the osdu schema.");
        }

        var schema = await _templates.LoadAsync(mapping.Template, ct).ConfigureAwait(false)
            ?? throw new FlowValidationException(
                $"{where}: mapping {mapping.Reference} pins template {mapping.Template}, which is not saved. Save it on the Templates page, or with 'sqlflow template import'.");

        var (references, scope, systemProperties) = await CacheAsync(flow, mapping, where, ct).ConfigureAwait(false);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, declared) in mapping.Parameters)
        {
            if (declared.Default is not null)
            {
                parameters[name] = declared.Default;
            }
        }

        // Where a record goes and under whose access and legal terms belongs to the kind, not to any one mapping: a
        // parameter the kind owns that the flow leaves out takes the reference the kind names for it. A flow that names
        // its own value still wins, so a document can pin a destination when it has to.
        var supplied = new Dictionary<string, string>(flow.Render.Parameters, StringComparer.Ordinal);
        foreach (var declared in mapping.Parameters.Keys)
        {
            if (!supplied.ContainsKey(declared) && DeliveryDestination.ReferenceFor(declared) is { } reference)
            {
                supplied[declared] = reference;
            }
        }

        // What reaches a mapping from outside it is deployment configuration, not mapping content: the partition, the
        // legal tag and the access groups of an estate all differ between test and production while the mapping stays the
        // same. So these carry ${env:NAME} and ${keyvault:vault/secret} references exactly as target.endpoint and
        // target.headers do, and they are expanded here, before the render context is built, so the context (and the
        // rendered hash and the ledger row that records what rendered a document) holds the value that reached the
        // record, never the reference that produced it. A mapping's own default is mapping content and stays literal.
        foreach (var (name, value) in supplied)
        {
            parameters[name] = await _secrets.ResolveAsync(value, ct).ConfigureAwait(false);
        }

        var context = new RenderContext
        {
            MappingReference = mapping.Reference,
            CacheScope = scope,
            CacheVersion = references.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = parameters,
            SystemProperties = systemProperties,
        };

        var searches = await SearchesAsync(_templates, mapping, ct).ConfigureAwait(false);
        var issues = Preflight.Check(mapping, schema, references, context, sourceColumns: null, searches);
        Preflight.ThrowIfFailed(issues, where);
        var renderer = new MappingRenderer(mapping, schema, references, context, searches, _search);
        return new ResolvedMapping(mapping, schema, references, context, renderer);
    }

    /// <summary>
    /// <paramref name="mapping"/>'s searches resolved against the templates they pin, read from
    /// <paramref name="templates"/>: how each property a search compares is indexed. A pinned template that is not saved
    /// is reported among the problems, with every other, for the preflight to list.
    /// </summary>
    public static async Task<ResolvedSearches> SearchesAsync(ITemplateStore templates, MappingDefinition mapping, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Searches.Count == 0)
        {
            return ResolvedSearches.None;
        }

        var schemas = new Dictionary<TemplateReference, SchemaSnapshot>();
        foreach (var pinned in mapping.Searches.Values.Select(s => s.Schema).Distinct())
        {
            if (await templates.LoadAsync(pinned, ct).ConfigureAwait(false) is { } schema)
            {
                schemas[pinned] = schema;
            }
        }

        return ResolvedSearches.Resolve(mapping, schemas);
    }

    /// <summary>
    /// The version of the cache a render reads, and the partition it belongs to: the partition the flow delivers to
    /// (<c>target.headers.data-partition-id</c>). A mapping that reads nothing from the cache renders against no cache at all,
    /// so refreshing a cache never moves the render context of records that never read it.
    /// </summary>
    /// <summary>
    /// The partition a flow's requests carry, resolved: its <c>data-partition-id</c> header with every reference expanded,
    /// checked to be a partition a cache can be named by.
    /// </summary>
    private async Task<string> PartitionAsync(FlowDefinition flow, string where, CancellationToken ct)
    {
        var declared = flow.Target.Headers
            .FirstOrDefault(h => h.Key.Equals(CacheScope.PartitionHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(declared))
        {
            throw new FlowValidationException(
                $"{where}: the headers declare no '{CacheScope.PartitionHeader}', so there is no partition whose cache the flow uses.");
        }

        return CacheScope.Normalize(await _secrets.ResolveAsync(declared, ct).ConfigureAwait(false), where);
    }

    /// <summary>
    /// The version of the partition's cache the mapping renders against, the partition, and the system properties its
    /// searches are written under (<see cref="SystemProperties.Pinned"/>; none when it declares no searches).
    /// </summary>
    /// <remarks>
    /// A mapping that reads the cache renders against a version, the current one or the one the flow pins, and its
    /// searches under that version's properties. A mapping that only searches reads no cached record, so it renders
    /// against no version and is pinned to the properties alone: those of the version the flow names, read without its
    /// records. Pinning it to the version would render every one of its records again, and ask the platform every one of
    /// their questions again, whenever a capture changed reference data it never reads.
    /// </remarks>
    private async Task<(ReferenceSnapshot References, string? Scope, IReadOnlyList<SystemProperty> SystemProperties)> CacheAsync(
        FlowDefinition flow, MappingDefinition mapping, string where, CancellationToken ct)
    {
        var readsCache = mapping.Entries.Any(e => e.Source?.Kind == MappingSourceKind.Cache);
        var searches = mapping.Searches.Count > 0;
        if (!readsCache && !searches)
        {
            return (ReferenceSnapshot.Empty, null, []);
        }

        // The cache holds one partition's reference data, so it is keyed by the partition the flow actually reaches, not
        // by the text the document spells it with. An estate that names its partition ${env:...} would otherwise key its
        // cache by that text: the same reference in two estates delivering to two partitions would share one cache, and
        // two documents naming one partition different ways would each need their own capture of identical data.
        var scope = await PartitionAsync(flow, $"{where}: target", ct).ConfigureAwait(false);
        if (!readsCache)
        {
            return (ReferenceSnapshot.Empty, null, SystemProperties.Pinned(await SearchPropertiesAsync(flow, scope, where, ct).ConfigureAwait(false)));
        }

        if (_cache is null)
        {
            throw new FlowValidationException(
                $"{where}: mapping {mapping.Reference} reads the cache of partition '{scope}', and caches live in the module's database, which this host was started without. Start it with the module's connection (Osdu:Database:Connection or SQLFLOW_OSDU_DB), or with --db when the catalog's database holds the osdu schema.");
        }

        var version = flow.Render.CacheVersion;
        if (version.Equals(FlowRender.CurrentCacheVersion, StringComparison.OrdinalIgnoreCase))
        {
            version = await _cache.CurrentVersionAsync(scope, ct).ConfigureAwait(false)
                ?? throw new FlowValidationException(
                    $"{where}: mapping {mapping.Reference} reads the cache of partition '{scope}', which holds no version yet. Run a cache flow whose source.headers.data-partition-id is '{scope}' with the refresh operation to capture one.");
        }

        var references = await _cache.LoadAsync(scope, version, ct).ConfigureAwait(false)
            ?? throw new FlowValidationException($"{where}: render.cacheVersion pins version {version} of the cache of partition '{scope}', which the catalog does not hold.");
        return (references, scope, searches ? SystemProperties.Pinned(references.SystemProperties) : []);
    }

    /// <summary>
    /// The system properties of the version of the partition's cache the flow names, read from the version's row alone:
    /// none when it names the current version and the partition holds none yet, or when the host has no module database,
    /// and a search then asks exact questions only. A version the flow pins that the catalog does not hold is refused, as
    /// it is for a mapping that reads the cache.
    /// </summary>
    private async Task<IReadOnlyList<SystemProperty>> SearchPropertiesAsync(FlowDefinition flow, string scope, string where, CancellationToken ct)
    {
        if (_cache is null)
        {
            return [];
        }

        var pinned = flow.Render.CacheVersion.Equals(FlowRender.CurrentCacheVersion, StringComparison.OrdinalIgnoreCase) ? null : flow.Render.CacheVersion;
        var version = await _cache.VersionAsync(scope, pinned, ct).ConfigureAwait(false);
        if (version is not null)
        {
            return version.SystemProperties;
        }

        return pinned is null
            ? []
            : throw new FlowValidationException($"{where}: render.cacheVersion pins version {pinned} of the cache of partition '{scope}', which the catalog does not hold.");
    }
}
