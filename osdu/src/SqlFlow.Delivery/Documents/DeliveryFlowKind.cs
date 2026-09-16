using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A delivery flow as the platform sees it: the parsed <see cref="FlowDefinition"/> behind the headers every catalog
/// consumer reads (name, batch, the record table as the source reference, the source connection reference, the OSDU
/// endpoint as the target reference, the credential references the hygiene check inspects), and its lineage: the
/// ingestion tables, payload files and cache types it reads and the OSDU types it writes (<see cref="DeliveryLineage"/>).
/// A delivery flow always needs its repository tree: its mappings live next to it.
/// </summary>
public sealed record DeliveryFlowDocument : RegisteredFlowDocument
{
    /// <summary>What lineage reads the flow's mapping with. The loader holds no state, so one serves every document.</summary>
    private static readonly DeliveryDocumentLoader MappingDocuments = new();

    public required FlowDefinition Flow { get; init; }

    public override string Name => Flow.Name;

    public override string Kind => FlowDefinition.FlowTypeName;

    public override string? Batch => Flow.Batch;

    public override string? SourceConnectionReference => Flow.Source.Connection;

    public override string? SourceReference => Flow.Source.Record.Object;

    public override string? TargetReference => Flow.Target.Endpoint;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Flow.CredentialReferences();

    public override bool RequiresRepoTree => true;

    public override IReadOnlyList<DeclaredDataObject> DeclaredObjects => DeliveryLineage.DeclaredObjects(Flow);

    public override RegisteredFlowLineage DescribeLineage(RegisteredLineageContext context)
        => DeliveryLineage.Describe(Flow, context, MappingDocuments);
}

/// <summary>The <c>flowType: delivery</c> document kind, registered in every host next to its executor; it also owns
/// the mapping documents (<c>documentType: mapping</c>) the flows pin, so validate reports them under their own type.</summary>
public sealed class DeliveryFlowKind : IFlowDocumentKind, ICompanionDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;

    public DeliveryFlowKind(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    /// <summary>The operations of a delivery run, the default first.</summary>
    public static IReadOnlyList<FlowKindOperation> DeliveryOperationList { get; } =
    [
        new(DeliveryOperations.Deliver, "Deliver", "Plan the rows the ingestion tables changed and deliver what renders differently to OSDU.", WritesTarget: true),
        new(DeliveryOperations.Plan, "Plan", "Plan the rows the ingestion tables changed and report what a delivery would send, changing nothing.", WritesTarget: false),
        new(DeliveryOperations.Intake, "Intake", "Plan the rows into work batches without delivering them: a fan-out member's share of a plan.", WritesTarget: false),
        new(DeliveryOperations.Drain, "Drain", "Deliver the work batches a submission already planned.", WritesTarget: true),
        new(DeliveryOperations.Verify, "Verify", "Compare what OSDU holds with what the ledger recorded, and optionally queue redelivery of drift.", WritesTarget: false),
        new(DeliveryOperations.Replan, "Replan", "Read every row of the scope again and deliver what renders differently now.", WritesTarget: true),
    ];

    public string FlowType => FlowDefinition.FlowTypeName;

    public string Description => "deliver records from ingestion tables into OSDU";

    public IReadOnlyList<FlowKindOperation> Operations => DeliveryOperationList;

    /// <summary>The mapping documents a delivery flow pins (<c>documentType: mapping</c>).</summary>
    public string DocumentType => MappingDefinition.DocumentTypeName;

    public string ParseCompanion(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var mapping = _loader.ParseMapping(yaml, source);
        return $"{mapping.Reference} -> {mapping.Kind}";
    }

    public RegisteredFlowDocument Parse(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return new DeliveryFlowDocument { Flow = _loader.ParseFlow(yaml, source) };
    }

    public void ValidateParameters(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DeliveryOperations.RefuseBuiltInOverrides(
            parameters, FlowDefinition.FlowTypeName,
            "the replan operation reads every row of the scope again, and recordKeys in the payload scope a run to chosen records.");
        DeliveryRunPayload.Parse(parameters).Validate(DeliveryOperations.Of(parameters));
    }
}
