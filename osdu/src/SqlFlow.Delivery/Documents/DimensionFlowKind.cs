using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A dimension flow as the platform sees it: the OSDU endpoint is the source reference, the dimensions its builds keep in the
/// module database the target, and the source's secret references are what the hygiene check inspects. It needs the
/// repository tree only when a dimension cleans its values through a dictionary, whose file a build reads. Its lineage is
/// the kinds its dimensions read and the dimensions it writes (<see cref="DimensionLineage"/>).
/// </summary>
public sealed record DimensionFlowDocument : RegisteredFlowDocument
{
    /// <summary>What the pipeline row shows as a dimension flow's target: the dimensions its builds keep in the module database.</summary>
    public const string DimensionsTarget = "dimensions";

    public required DimensionFlowDefinition Flow { get; init; }

    public override string Name => Flow.Name;

    public override string Kind => DimensionFlowDefinition.FlowTypeName;

    public override string? Batch => Flow.Batch;

    public override string? SourceReference => Flow.Source.Endpoint;

    public override string? TargetReference => DimensionsTarget;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Flow.CredentialReferences();

    /// <summary>A flow cleaning through a dictionary reads the dictionary's file, so its node needs the repository's tree at the run's commit.</summary>
    public override bool RequiresRepoTree => Flow.ReadsDictionaries;

    public override RegisteredFlowLineage DescribeLineage(RegisteredLineageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return DimensionLineage.Describe(Flow, context);
    }
}

/// <summary>The <c>flowType: dimension</c> document kind, registered in every host next to its executor.</summary>
public sealed class DimensionFlowKind : IFlowDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;

    public DimensionFlowKind(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    public string FlowType => DimensionFlowDefinition.FlowTypeName;

    public string Description => "read every distinct key of attributes of OSDU records, label and clean each into the value a person picks, and keep both with the search filter each stands for";

    public IReadOnlyList<FlowKindOperation> Operations { get; } =
    [
        new(DeliveryOperations.Build, "Build", "Read every distinct key of each dimension (all of them, or those the payload names) from the search index, read each key's label where the dimension names one, clean them into values, and keep what changed.", WritesTarget: true),
        new(DeliveryOperations.Plan, "Plan", "Check every dimension against the templates of the kinds it reads and count the records each would read, reading no value and keeping nothing.", WritesTarget: false),
    ];

    public RegisteredFlowDocument Parse(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return new DimensionFlowDocument { Flow = _loader.ParseDimension(yaml, source) };
    }

    public void ValidateParameters(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DeliveryOperations.RefuseBuiltInOverrides(parameters, DimensionFlowDefinition.FlowTypeName, "a build reads every value the index holds; name the dimensions to build in the payload.");
        var payload = DeliveryRunPayload.Parse(parameters);
        if (payload.Force || payload.SubmissionId is not null || payload.RecordKeys.Count > 0 || payload.Redeliver is not null || payload.Rerender || payload.Slices.Count > 0
            || payload.Interface is not null || payload.Interfaces.Count > 0 || payload.SelectsTests || payload.SelectsInventories)
        {
            throw new SqlFlowException(
                "A dimension flow's payload names the dimensions a run builds (dimensions) and nothing else; a dimension has no submission, record, slice, interface, test or inventory to name.");
        }
    }

    /// <summary>The operation a dimension flow runs: build (its default) or plan.</summary>
    public static string Operation(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return (parameters.Operation ?? DeliveryOperations.Build) switch
        {
            DeliveryOperations.Build => DeliveryOperations.Build,
            DeliveryOperations.Plan => DeliveryOperations.Plan,
            var other => throw new SqlFlowException(
                $"A dimension flow runs the {DeliveryOperations.Build} and {DeliveryOperations.Plan} operations; '{other}' is not one of them."),
        };
    }
}
