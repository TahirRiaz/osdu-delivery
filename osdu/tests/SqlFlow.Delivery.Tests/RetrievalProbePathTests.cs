using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A retrieval flow has no target probe: "Probe target" and the scheduled probe ask a delivery flow's route and record in the
/// delivery ledger's audit trail, and a retrieval run never calls an info path. So <c>source.probePath</c>, which a retrieval
/// once accepted and ignored, is refused by name, saying why, rather than promising a check nothing makes.
/// </summary>
public sealed class RetrievalProbePathTests
{
    private static string Yaml(string? probePath) => $$"""
        flowType: retrieval
        name: wellbores-out
        source:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: dev }
          kind: "osdu:wks:master-data--Wellbore:1.*.*"
        {{(probePath is null ? string.Empty : "  probePath: " + probePath)}}
        target:
          location: out/{run}
        """;

    [Fact]
    public void A_retrieval_naming_a_probe_path_is_refused_saying_the_probe_covers_delivery_flows_only()
    {
        var loader = new DeliveryDocumentLoader();
        Assert.Equal("wellbores-out", loader.ParseRetrieval(Yaml(null), "retrieval.yaml").Name);

        var refused = Assert.Throws<FlowValidationException>(() => loader.ParseRetrieval(Yaml("/api/search/v2/info"), "retrieval.yaml"));
        Assert.StartsWith("retrieval.yaml: source.probePath is not a key of a retrieval flow", refused.Message, StringComparison.Ordinal);
        Assert.Contains("the target probe covers delivery flows only", refused.Message, StringComparison.Ordinal);
        Assert.Contains("remove the key", refused.Message, StringComparison.Ordinal);
    }
}
