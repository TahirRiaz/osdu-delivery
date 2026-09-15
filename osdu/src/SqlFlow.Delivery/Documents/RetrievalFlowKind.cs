using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A retrieval flow as the platform sees it: the OSDU endpoint is the source reference, the lake location the
/// target reference, and the source's secret references are what the hygiene check inspects. It needs no
/// repository tree: nothing is rendered.
/// </summary>
public sealed record RetrievalFlowDocument : FlowDocument
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

    public FlowDocument Parse(string yaml, string source, FlowDocumentEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(envelope);
        return new RetrievalFlowDocument
        {
            Flow = _loader.ParseRetrieval(yaml, source),
            Schedule = envelope.Schedule,
            Mode = envelope.Mode,
            Lifecycle = envelope.Lifecycle,
        };
    }
}
