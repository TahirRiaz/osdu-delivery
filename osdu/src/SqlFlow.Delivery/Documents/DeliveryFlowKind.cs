using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A delivery flow as the platform sees it: the parsed <see cref="SourceDefinition"/> behind the headers every catalog
/// consumer reads (name, batch, the record tables as the source reference, the source connection reference, the OSDU
/// endpoint as the target reference, the credential references the hygiene check inspects), and its lineage: the
/// ingestion tables, payload files and mapping each interface reads, what the mapping reads in turn, and the OSDU types
/// the interface writes (<see cref="DeliveryLineage"/>). A delivery flow always needs its repository tree: its mappings
/// live next to it.
/// </summary>
public sealed record DeliveryFlowDocument : RegisteredFlowDocument
{
    /// <summary>What lineage reads the flows' mappings with. The loader holds no state, so one serves every document.</summary>
    private static readonly DeliveryDocumentLoader MappingDocuments = new();

    public required SourceDefinition Source { get; init; }

    /// <summary>
    /// What lineage reads the templates the flows' mappings pin with, on a host that has the module database; null on one
    /// that reads none. Not public: the catalog keeps a document's public properties as the pipeline's definition, and
    /// this is how the document is described, not part of what it says.
    /// </summary>
    internal MappingTemplateSource? Templates { get; init; }

    public override string Name => Source.Name;

    public override string Kind => FlowDefinition.FlowTypeName;

    public override string? Batch => Source.Batch;

    public override string? SourceConnectionReference => Source.Connection;

    /// <summary>The record tables the interfaces read, in document order.</summary>
    public override string? SourceReference => string.Join(", ", Source.Interfaces.Select(i => i.Source.Record.Object).Distinct(StringComparer.OrdinalIgnoreCase));

    public override string? TargetReference => Source.Endpoint;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Source.CredentialReferences();

    public override bool RequiresRepoTree => true;

    public override IReadOnlyList<DeclaredDataObject> DeclaredObjects
        => Source.Interfaces.SelectMany(DeliveryLineage.DeclaredObjects).Distinct().ToList();

    public override RegisteredFlowLineage DescribeLineage(RegisteredLineageContext context)
        => DeliveryLineage.Describe(Source, context, MappingDocuments, Templates);
}

/// <summary>The <c>flowType: delivery</c> document kind, registered in every host next to its executor; it also owns
/// the mapping documents (<c>documentType: mapping</c>) the flows pin, so validate reports them under their own type.</summary>
public sealed class DeliveryFlowKind : IFlowDocumentKind, ICompanionDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;
    private readonly MappingTemplateSource? _templates;

    /// <summary>
    /// The kind over <paramref name="loader"/>. <paramref name="templates"/> reads the templates mappings pin, so the
    /// lineage of a flow shows what a <c>ref</c> written without its entity type is checked against; a host without the
    /// module database passes none, and that part of a flow's lineage is then left out with a warning saying so.
    /// </summary>
    public DeliveryFlowKind(DeliveryDocumentLoader loader, MappingTemplateSource? templates = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
        _templates = templates;
    }

    /// <summary>The operations of a delivery run, the default first.</summary>
    public static IReadOnlyList<FlowKindOperation> DeliveryOperationList { get; } =
    [
        new(DeliveryOperations.Deliver, "Deliver", "Plan the rows the ingestion tables changed and deliver what renders differently.", WritesTarget: true),
        new(DeliveryOperations.Plan, "Plan", "Plan the rows the ingestion tables changed and report what a delivery would send, changing nothing.", WritesTarget: false),
        new(DeliveryOperations.Intake, "Intake", "Plan the rows into work batches without delivering them: a fan-out member's share of a plan.", WritesTarget: false),
        new(DeliveryOperations.Drain, "Drain", "Deliver the work batches a submission already planned.", WritesTarget: true),
        new(DeliveryOperations.Verify, "Verify", "Compare what OSDU holds with what the ledger recorded, and optionally queue redelivery of drift.", WritesTarget: false),
        new(DeliveryOperations.Replan, "Replan", "Read every row of the scope again and deliver what renders differently now.", WritesTarget: true),
        new(DeliveryOperations.Sync, "Sync timelines", "Read the ledger's records from the ingestion tables and consolidate the ledger: record what it lacks, flag rows that changed unseen for the next run, report rows that are gone. Sends nothing.", WritesTarget: false),
        new(DeliveryOperations.Reverse, "Reverse", "Put OSDU back as it was before one run or submission (payload runId or submissionId): remove what it created, restore the version it replaced, and block those records until their source changes or they are released.", WritesTarget: true),
        new(DeliveryOperations.Undo, "Undo unfinished deliveries", "Undo what deliveries that did not complete left in OSDU: remove what they created, reversibly, and write back the version a write replaced, writing each undo to the record's history. Deliver and drain runs end with the same sweep; force also retries undos that failed as often as the sweep tries.", WritesTarget: true),
        new(DeliveryOperations.DeleteLedger, "Delete ledger", "Remove every record of the ledger from OSDU (reversible), then delete the whole ledger, so the next run reads every row and delivers each as a new record. The payload names the partition as confirmation (confirm).", WritesTarget: true),
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
        return new DeliveryFlowDocument { Source = _loader.ParseSource(yaml, source), Templates = _templates };
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
