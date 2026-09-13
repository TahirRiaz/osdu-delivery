using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A cache flow as the platform sees it: the OSDU endpoint is the source reference, the catalog the target, and the
/// source's secret references are what the hygiene check inspects. It needs no repository tree: everything a refresh
/// needs is in the document.
/// </summary>
public sealed record CacheFlowDocument : FlowDocument
{
    /// <summary>What the pipeline row shows as a cache flow's target: the versions a refresh writes live in the catalog.</summary>
    public const string CatalogTarget = "catalog";

    public required CacheDefinition Flow { get; init; }

    public override string Name => Flow.Name;

    public override string Kind => CacheDefinition.FlowTypeName;

    public override string? Batch => Flow.Batch;

    public override string? SourceReference => Flow.Source.Endpoint;

    public override string? TargetReference => CatalogTarget;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Flow.CredentialReferences();

    public override bool RequiresRepoTree => false;
}

/// <summary>The <c>flowType: cache</c> document kind, registered in every host next to its executor.</summary>
public sealed class CacheFlowKind : IFlowDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;

    public CacheFlowKind(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    public string FlowType => CacheDefinition.FlowTypeName;

    public string Description => "capture the reference and master data of OSDU kinds into a versioned cache in the catalog, which delivery flows render against";

    public FlowDocument Parse(string yaml, string source, FlowDocumentEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(envelope);
        return new CacheFlowDocument
        {
            Flow = _loader.ParseCache(yaml, source),
            Schedule = envelope.Schedule,
            Mode = envelope.Mode,
            Lifecycle = envelope.Lifecycle,
        };
    }
}
