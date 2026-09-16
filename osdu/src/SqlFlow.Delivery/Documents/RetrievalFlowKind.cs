using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A retrieval flow as the platform sees it: the OSDU endpoint is the source reference, the lake location the
/// target reference, and the source's secret references are what the hygiene check inspects. It needs no
/// repository tree: nothing is rendered.
/// </summary>
public sealed record RetrievalFlowDocument : RegisteredFlowDocument
{
    public required RetrievalDefinition Flow { get; init; }

    public override string Name => Flow.Name;

    public override string Kind => RetrievalDefinition.FlowTypeName;

    public override string? Batch => Flow.Batch;

    public override string? SourceReference => Flow.Source.Endpoint;

    public override string? TargetReference => Flow.Target.Location;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Flow.CredentialReferences();

    public override bool RequiresRepoTree => false;
}

/// <summary>The <c>flowType: retrieval</c> document kind, registered in every host next to its executor.</summary>
public sealed class RetrievalFlowKind : IFlowDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;

    public RetrievalFlowKind(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    public string FlowType => RetrievalDefinition.FlowTypeName;

    public string Description => "retrieve records of OSDU kinds from the search index into JSON Lines files on the lake, with a manifest and a ledger row per run";

    public IReadOnlyList<FlowKindOperation> Operations { get; } =
    [
        new(DeliveryOperations.Retrieve, "Retrieve", "Retrieve the records the query matches into files on the lake, from where the last run stopped.", WritesTarget: true),
        new(DeliveryOperations.Plan, "Plan", "Count what the query matches and say where a retrieve would write, writing nothing.", WritesTarget: false),
    ];

    public RegisteredFlowDocument Parse(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return new RetrievalFlowDocument { Flow = _loader.ParseRetrieval(yaml, source) };
    }

    public void ValidateParameters(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DeliveryOperations.RefuseBuiltInOverrides(
            parameters, RetrievalDefinition.FlowTypeName, "force in the payload restarts an incremental retrieval at its declared start.");
        var payload = DeliveryRunPayload.Parse(parameters);
        if (payload.SubmissionId is not null || payload.RecordKeys.Count > 0 || payload.Redeliver is not null || payload.Slices.Count > 0)
        {
            throw new SqlFlowException("A retrieval flow's payload carries only force; a retrieval has no submission, records or slices to name.");
        }
    }

    /// <summary>Whether a retrieval run restarts an incremental flow at its declared start.</summary>
    public static bool Forced(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return DeliveryRunPayload.Parse(parameters).Force;
    }
}
