using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A delivery flow as the platform sees it: the parsed <see cref="FlowDefinition"/> behind the headers every
/// catalog consumer reads (name, batch, the drop as the source reference, the OSDU endpoint as the target
/// reference, the credential references the hygiene check inspects). A delivery flow always needs its
/// repository tree: the mappings and snapshots live next to it, never inside the flow file.
/// </summary>
public sealed record DeliveryFlowDocument : FlowDocument
{
    public required FlowDefinition Flow { get; init; }

    public override string Name => Flow.Name;

    public override string Kind => FlowDefinition.FlowTypeName;

    public override string? Batch => Flow.Batch;

    public override string? SourceReference => Flow.Source.Location;

    public override string? TargetReference => Flow.Target.Endpoint;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Flow.CredentialReferences();

    public override bool RequiresRepoTree => true;
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

    public string FlowType => FlowDefinition.FlowTypeName;

    public string Description => "deliver prepared records from a drop into OSDU (record and well log protocols)";

    /// <summary>The mapping documents a delivery flow pins (<c>documentType: mapping</c>).</summary>
    public string DocumentType => MappingDefinition.DocumentTypeName;

    public string ParseCompanion(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var mapping = _loader.ParseMapping(yaml, source);
        return $"{mapping.Reference} -> {mapping.Kind}";
    }

    public FlowDocument Parse(string yaml, string source, FlowDocumentEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(envelope);
        return new DeliveryFlowDocument
        {
            Flow = _loader.ParseFlow(yaml, source),
            Schedule = envelope.Schedule,
            Mode = envelope.Mode,
            Lifecycle = envelope.Lifecycle,
        };
    }
}
