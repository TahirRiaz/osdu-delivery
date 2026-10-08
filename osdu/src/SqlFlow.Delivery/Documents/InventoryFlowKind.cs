using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// An inventory flow as the platform sees it: the OSDU endpoint is the source reference, the inventories its builds keep in the
/// module database the target, and the source's secret references are what the hygiene check inspects. It needs no repository
/// tree. Its lineage is the kinds it reads (<see cref="InventoryLineage"/>), so it runs after the flows that write them.
/// </summary>
public sealed record InventoryFlowDocument : RegisteredFlowDocument
{
    /// <summary>What the pipeline row shows as an inventory flow's target: the inventories its builds keep in the module database.</summary>
    public const string InventoriesTarget = "inventories";

    public required InventoryFlowDefinition Flow { get; init; }

    public override string Name => Flow.Name;

    public override string Kind => InventoryFlowDefinition.FlowTypeName;

    public override string? Batch => Flow.Batch;

    public override string? SourceReference => Flow.Source.Endpoint;

    public override string? TargetReference => InventoriesTarget;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Flow.CredentialReferences();

    public override bool RequiresRepoTree => false;

    public override RegisteredFlowLineage DescribeLineage(RegisteredLineageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return InventoryLineage.Describe(Flow);
    }
}

/// <summary>The <c>flowType: inventory</c> document kind, registered in every host next to its executor.</summary>
public sealed class InventoryFlowKind : IFlowDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;

    public InventoryFlowKind(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    public string FlowType => InventoryFlowDefinition.FlowTypeName;

    public string Description => "keep every id and version OSDU kinds hold in a partition, and report where the ledgers disagree with it: orphans, missing records, undos left to do, and ids a ledger forgot";

    public IReadOnlyList<FlowKindOperation> Operations { get; } =
    [
        new(DeliveryOperations.Build, "Build", "Read every id each inventory's kind holds (all of them, or those the payload names), keep what changed, and compare every id with the ledgers of the partition.", WritesTarget: true),
        new(DeliveryOperations.Reconcile, "Reconcile", "Compare each inventory, as its last build left it, with the ledgers as they stand now, reading from storage only the ids a ledger expects.", WritesTarget: true),
        new(DeliveryOperations.Plan, "Plan", "Count the records each inventory would read, and say what would stop it, reading no id and keeping nothing.", WritesTarget: false),
    ];

    public RegisteredFlowDocument Parse(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return new InventoryFlowDocument { Flow = _loader.ParseInventory(yaml, source) };
    }

    public void ValidateParameters(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DeliveryOperations.RefuseBuiltInOverrides(parameters, InventoryFlowDefinition.FlowTypeName, "a build reads every id the kind holds; name the inventories to build in the payload.");
        var payload = DeliveryRunPayload.Parse(parameters);
        if (payload.Force || payload.SubmissionId is not null || payload.RecordKeys.Count > 0 || payload.Redeliver is not null || payload.Rerender || payload.Slices.Count > 0
            || payload.Interface is not null || payload.Interfaces.Count > 0 || payload.SelectsTests || payload.SelectsDimensions)
        {
            throw new SqlFlowException(
                "An inventory flow's payload names the inventories a run builds or reconciles (inventories) and nothing else; an inventory has no submission, record, slice, interface, test or dimension to name.");
        }
    }

    /// <summary>The operation an inventory flow runs: build (its default), reconcile or plan.</summary>
    public static string Operation(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return (parameters.Operation ?? DeliveryOperations.Build) switch
        {
            DeliveryOperations.Build => DeliveryOperations.Build,
            DeliveryOperations.Reconcile => DeliveryOperations.Reconcile,
            DeliveryOperations.Plan => DeliveryOperations.Plan,
            var other => throw new SqlFlowException(
                $"An inventory flow runs the {DeliveryOperations.Build}, {DeliveryOperations.Reconcile} and {DeliveryOperations.Plan} operations; '{other}' is not one of them."),
        };
    }
}

/// <summary>
/// What an inventory flow contributes to SQLFlow's lineage (docs/lineage-design.md section 3): it reads the kind (wildcards
/// allowed) of each inventory on its platform and partition, and writes nothing another flow reads. It is so ordered after the
/// delivery flows writing the kinds it reads, and a schedule firing both builds the inventory once the records have landed.
/// </summary>
public static class InventoryLineage
{
    /// <summary>
    /// Everything the flow contributes, the kinds once each in declaration order. A flow that names its partitions reads in every
    /// one of them; one that leaves them to the registry is described once, under the partition that stands for every registered one.
    /// </summary>
    public static RegisteredFlowLineage Describe(InventoryFlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var who = $"inventory flow '{flow.Name}'";
        var warnings = new List<string>();
        var partitions = flow.DeclaresPartitions && flow.Partition is null
            ? flow.Partitions
            : flow.FollowsRegistry && flow.Partition is null
                ? [PartitionNames.Every]
                : OsduLineage.Partition(flow.Source.Headers, who, "source.headers", warnings) is { } one ? [one] : [];

        var datasets = new List<DeclaredDataset>();
        foreach (var partition in partitions)
        {
            foreach (var inventory in flow.Inventories)
            {
                if (OsduLineage.Type(LineageRelation.Reads, flow.Source.Endpoint, partition, inventory.Kind, who, $"inventory '{inventory.Name}'", warnings) is { } read
                    && !datasets.Contains(read))
                {
                    datasets.Add(read);
                }
            }
        }

        return new RegisteredFlowLineage { Datasets = datasets, Warnings = warnings };
    }
}
