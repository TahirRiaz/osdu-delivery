using SqlFlow.Core;
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
/// template version the mapping pins from the catalog, the reference snapshot the flow pins (or the store's current one for
/// <c>pinned</c>), and the mapping parameters the flow supplies. The preflight runs before anything is returned.
/// </summary>
public sealed class RenderResolver
{
    private readonly MappingCatalog _mappings;
    private readonly ISnapshotStore _snapshots;
    private readonly ITemplateStore? _templates;

    public RenderResolver(MappingCatalog mappings, ISnapshotStore snapshots, ITemplateStore? templates)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(snapshots);
        _mappings = mappings;
        _snapshots = snapshots;
        _templates = templates;
    }

    public async Task<ResolvedMapping> ResolveAsync(FlowDefinition flow, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var where = flow.SourcePath ?? flow.Name;
        var mapping = _mappings.Load(flow.Render.Mapping);

        if (_templates is null)
        {
            throw new FlowValidationException(
                $"{where}: mapping {mapping.Reference} fills template {mapping.Template}, and templates live in the catalog, which this host was started without. Start it with the catalog connection (--db, or the catalog variable).");
        }

        var schema = await _templates.LoadAsync(mapping.Template, ct).ConfigureAwait(false)
            ?? throw new FlowValidationException(
                $"{where}: mapping {mapping.Reference} pins template {mapping.Template}, which is not saved in the catalog. Save it on the Templates page, or with 'sqlflow template import'.");

        var usesCache = mapping.Entries.Any(e => e.Source?.Kind == MappingSourceKind.Cache);
        ReferenceSnapshot references;
        if (flow.Render.References.Equals("pinned", StringComparison.OrdinalIgnoreCase))
        {
            var current = await _snapshots.CurrentReferenceVersionAsync(ct).ConfigureAwait(false);
            if (current is null)
            {
                if (usesCache)
                {
                    throw new FlowValidationException(
                        $"{where}: render.references is 'pinned' but the snapshot store has no current reference snapshot. Capture one with 'sqlflow snapshot references'.");
                }

                references = ReferenceSnapshot.Empty;
            }
            else
            {
                references = await _snapshots.LoadReferencesAsync(current, ct).ConfigureAwait(false)
                    ?? throw new FlowValidationException($"{where}: the current reference snapshot '{current}' is missing from the store.");
            }
        }
        else
        {
            references = await _snapshots.LoadReferencesAsync(flow.Render.References, ct).ConfigureAwait(false)
                ?? throw new FlowValidationException($"{where}: reference snapshot '{flow.Render.References}' does not exist in the store.");
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, declared) in mapping.Parameters)
        {
            if (declared.Default is not null)
            {
                parameters[name] = declared.Default;
            }
        }

        foreach (var (name, value) in flow.Render.Parameters)
        {
            parameters[name] = value;
        }

        var context = new RenderContext
        {
            MappingReference = mapping.Reference,
            ReferenceSnapshotVersion = references.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = parameters,
        };

        var issues = Preflight.Check(mapping, schema, references, context, dropColumns: null);
        Preflight.ThrowIfFailed(issues, where);
        var renderer = new MappingRenderer(mapping, schema, references, context);
        return new ResolvedMapping(mapping, schema, references, context, renderer);
    }
}
