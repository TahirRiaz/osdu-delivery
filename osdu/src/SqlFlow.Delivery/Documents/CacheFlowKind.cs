using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A cache flow as the platform sees it: the OSDU endpoint is the source reference, the catalog the target, and the
/// source's secret references are what the hygiene check inspects. It needs no repository tree: everything a refresh
/// needs is in the document. Its lineage is the OSDU types it reads and the partition cache types it writes
/// (<see cref="CacheLineage"/>).
/// </summary>
public sealed record CacheFlowDocument : RegisteredFlowDocument
{
    /// <summary>What the pipeline row shows as a cache flow's target: the versions a refresh writes live in the module database.</summary>
    public const string CatalogTarget = "catalog";

    public required CacheDefinition Flow { get; init; }

    public override string Name => Flow.Name;

    public override string Kind => CacheDefinition.FlowTypeName;

    public override string? Batch => Flow.Batch;

    public override string? SourceReference => Flow.Source.Endpoint;

    public override string? TargetReference => CatalogTarget;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Flow.CredentialReferences();

    public override bool RequiresRepoTree => false;

    public override RegisteredFlowLineage DescribeLineage(RegisteredLineageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CacheLineage.Describe(Flow);
    }
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

    public string Description => "capture the reference and master data of OSDU kinds into a versioned cache in the module database, which delivery flows render against";

    public IReadOnlyList<FlowKindOperation> Operations { get; } =
    [
        new(DeliveryOperations.Refresh, "Refresh", "Capture every declared type from OSDU and merge it into the partition's cache, writing a version when the content moved.", WritesTarget: true),
        new(DeliveryOperations.Plan, "Plan", "Count what each declared type's search matches, writing nothing.", WritesTarget: false),
    ];

    public RegisteredFlowDocument Parse(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return new CacheFlowDocument { Flow = _loader.ParseCache(yaml, source) };
    }

    public void ValidateParameters(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DeliveryOperations.RefuseBuiltInOverrides(parameters, CacheDefinition.FlowTypeName, "a refresh sweeps every declared type in full.");
        if (parameters.Payload is not null)
        {
            throw new SqlFlowException("A cache flow takes no payload; a run carries only the flow's parameter values.");
        }
    }
}
