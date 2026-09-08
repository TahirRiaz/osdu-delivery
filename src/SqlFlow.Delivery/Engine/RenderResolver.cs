using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine;

/// <summary>A flow's render inputs pinned together: the mapping, both snapshots, the context and a renderer over them.</summary>
public sealed record ResolvedMapping(
    MappingDefinition Mapping,
    SchemaSnapshot Schema,
    ReferenceSnapshot References,
    RenderContext Context,
    MappingRenderer Renderer);

/// <summary>
/// Resolves a flow's <c>render</c> block into a <see cref="ResolvedMapping"/> (design.md section 4.1): the pinned
/// mapping from the catalog, the schema snapshot the mapping's kind pins, the reference snapshot the flow pins (or
/// the store's current one for <c>pinned</c>), and the mapping parameters the flow supplies.
/// </summary>
public sealed class RenderResolver
{
    private readonly MappingCatalog _mappings;
    private readonly ISnapshotStore _snapshots;

    public RenderResolver(MappingCatalog mappings, ISnapshotStore snapshots)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(snapshots);
        _mappings = mappings;
        _snapshots = snapshots;
    }

    public async Task<ResolvedMapping> ResolveAsync(FlowDefinition flow, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var where = flow.SourcePath ?? flow.Name;
        var mapping = _mappings.Load(flow.Render.Mapping);

        var schema = await _snapshots.LoadSchemaAsync(mapping.Kind, ct).ConfigureAwait(false)
            ?? throw new FlowValidationException(
                $"{where}: no schema snapshot for kind '{mapping.Kind}' under '{DescribeStore()}'. Capture one with 'sqlflow snapshot schema --kind {mapping.Kind}'.");

        var usesReferences = UsesReferences(mapping);
        ReferenceSnapshot references;
        if (flow.Render.References.Equals("pinned", StringComparison.OrdinalIgnoreCase))
        {
            var current = await _snapshots.CurrentReferenceVersionAsync(ct).ConfigureAwait(false);
            if (current is null)
            {
                if (usesReferences)
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

    private string DescribeStore() => _snapshots is Storage.FileSnapshotStore f ? f.Root : _snapshots.GetType().Name;

    private static bool UsesReferences(MappingDefinition mapping)
    {
        var uses = false;
        Preflight.WalkProperties(mapping, mapping.Properties, string.Empty, "record", (p, _, _) =>
        {
            if (p.Transform == MappingTransform.Reference)
            {
                uses = true;
            }
        });
        return uses;
    }
}
