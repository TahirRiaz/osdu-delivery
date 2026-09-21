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
/// current version, or the one the flow pins) from the catalog, and the mapping parameters the flow supplies. The preflight
/// runs before anything is returned.
/// </summary>
public sealed class RenderResolver
{
    private readonly MappingCatalog _mappings;
    private readonly ICacheStore? _cache;
    private readonly ITemplateStore? _templates;
    private readonly ISecretResolver _secrets;

    public RenderResolver(MappingCatalog mappings, ICacheStore? cache, ITemplateStore? templates, ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(secrets);
        _mappings = mappings;
        _cache = cache;
        _templates = templates;
        _secrets = secrets;
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

        var (references, scope) = await CacheAsync(flow, mapping, where, ct).ConfigureAwait(false);

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
        };

        var issues = Preflight.Check(mapping, schema, references, context, sourceColumns: null);
        Preflight.ThrowIfFailed(issues, where);
        var renderer = new MappingRenderer(mapping, schema, references, context);
        return new ResolvedMapping(mapping, schema, references, context, renderer);
    }

    /// <summary>
    /// The version of the cache a render reads, and the partition it belongs to: the partition the flow delivers to
    /// (<c>target.headers.data-partition-id</c>). A mapping that reads nothing from the cache renders against no cache at all,
    /// so refreshing a cache never moves the render context of records that never read it.
    /// </summary>
    private async Task<(ReferenceSnapshot References, string? Scope)> CacheAsync(FlowDefinition flow, MappingDefinition mapping, string where, CancellationToken ct)
    {
        var readsCache = mapping.Entries.Any(e => e.Source?.Kind == MappingSourceKind.Cache);
        if (!readsCache)
        {
            return (ReferenceSnapshot.Empty, null);
        }

        var scope = CacheScope.Of(flow.Target.Headers, $"{where}: target");
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
        return (references, scope);
    }
}
