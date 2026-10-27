using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What keeps a delivery flow without <c>enrich:</c> steps exactly as it was (osdu/docs/reference/flow/enrich.md): its render
/// context, its document's hash and its definition. A moved context would render every record of every flow again, and a moved
/// document hash would orphan a renamed flow's ledger, so these run in the tiers on every pull request, beside the on-demand
/// suite of the steps themselves (<see cref="EnrichmentSuite"/>).
/// </summary>
public sealed class EnrichmentGuardTests
{
    [Fact]
    public void A_render_context_without_steps_is_written_as_it_always_was()
    {
        var context = TestSchema.Context();

        Assert.Equal(
            $$"""{"cache":"dev","cacheVersion":"refs-1","mapping":"Thing@1.0.0","parameters":{"dataPartition":"dev"},"schema":"{{TestSchema.Build().Version}}"}""",
            context.Canonical());
    }

    [Fact]
    public void A_render_context_with_steps_names_them_in_order_and_reads_back_as_written()
    {
        var context = TestSchema.Context() with
        {
            Steps =
            [
                new RenderStep("lasfile", "WellLogFile@1.0.0", "fp-1", TestSchema.Build().Version, new Dictionary<string, string>(StringComparer.Ordinal)),
                new RenderStep("quality", "Quality@2.0.0", null, TestSchema.Build().Version, new Dictionary<string, string>(StringComparer.Ordinal) { ["grade"] = "A" }),
            ],
        };

        var canonical = context.Canonical();
        Assert.Contains("\"enrich\":[{\"mapping\":\"WellLogFile@1.0.0\",\"mappingFingerprint\":\"fp-1\",\"name\":\"lasfile\"", canonical, StringComparison.Ordinal);
        Assert.NotEqual(TestSchema.Context().Hash(), context.Hash());
        Assert.NotEqual(TestSchema.Context().RulesHash(), context.RulesHash());
        var read = RenderContext.Parse(canonical);
        Assert.Equal(canonical, read.Canonical());
        Assert.Equal(["lasfile", "quality"], read.Steps.Select(s => s.Name));
    }

    [Fact]
    public void A_flow_without_enrich_has_no_steps_and_hashes_as_it_did_before_steps_existed()
    {
        var source = new DeliveryDocumentLoader().LoadSource(Samples.Flow);
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal) { ["WellLog@1.4.0"] = "mapping-hash" };

        Assert.All(source.Interfaces, flow => Assert.Null(flow.Enrich));
        Assert.Equal(GuardHashes.SampleFlow, DeliveryDocumentHash.Of(source, mappings));
    }
}

/// <summary>The document hash of the sample well log flow as the code before <c>enrich:</c> computed it.</summary>
internal static class GuardHashes
{
    public const string SampleFlow = "3b9d78e6597cb6008b59ed29fa95121f7ab9889733530ba045570602f5d2278d";
}
