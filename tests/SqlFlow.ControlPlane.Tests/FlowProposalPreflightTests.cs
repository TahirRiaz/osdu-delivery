using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Documents;
using SqlFlow.SourceControl.Proposals;
using SqlFlow.Tests;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The proposal preflight's pure core: the same document loader the managed sync parses with, applied before any
/// git push. Errors are the silent-failure modes (a file the sync would ignore, a valid flow under an
/// undiscoverable extension); warnings are the design signals a reviewer must see (an endpoint change on a revised
/// flow, a duplicate flow name). No database or git is involved: the catalog side is handed in as plain records,
/// and the documents are the test-only kind, or the delivery kind for the mapping documents a mapping builder
/// proposal carries, so no engine is needed.
/// </summary>
public sealed class FlowProposalPreflightTests
{
    private static readonly YamlDocumentLoader Documents = TestFlowKind.Loader();

    /// <summary>The delivery kind, which owns the mapping documents (<c>documentType: mapping</c>) the mapping builder proposes.</summary>
    private static readonly YamlDocumentLoader DeliveryDocuments = new([new DeliveryFlowKind(new DeliveryDocumentLoader())]);

    private const string Flow = """
        flowType: test
        name: orders-delivery
        source: lake://raw/orders/
        target: osdu://partition/orders
        credentials:
          lake: ${env:SQLFLOW_LAKE}
        """;

    private static ProposalPreflightResult Run(
        IReadOnlyList<ProposalFile> files, params FlowProposalPreflight.ExistingPipeline[] existing)
        => FlowProposalPreflight.Run(Documents, files, existing);

    [Fact]
    public void ValidFlow_PassesClean()
    {
        var result = Run([new ProposalFile("flows/orders.flow.yaml", Flow)]);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void MappingFromTheBuilder_PassesAsTheCompanionDocumentItDeclares()
    {
        var result = FlowProposalPreflight.Run(DeliveryDocuments, [new ProposalFile("mappings/Wellbore@1.0.0.yaml", SampleMapping())], []);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void MappingThatDoesNotLoad_IsAnError_NamingWhyTheSyncWouldRecordItInvalid()
    {
        // A target written without its origin is not a template variable, so the sync would record the mapping as invalid.
        var yaml = SampleMapping().Replace("target: osdu.data.FacilityName", "target: data.FacilityName", StringComparison.Ordinal);
        var result = FlowProposalPreflight.Run(DeliveryDocuments, [new ProposalFile("mappings/Wellbore@1.0.0.yaml", yaml)], []);

        var error = Assert.Single(result.Errors);
        Assert.Equal("mappings/Wellbore@1.0.0.yaml", error.Path);
        Assert.Contains("does not parse as the companion document it declares", error.Message, StringComparison.Ordinal);
        Assert.Contains("must start with 'osdu.'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The sample estate's wellbore mapping, as the mapping builder writes one into a proposal.</summary>
    private static string SampleMapping()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "mappings", "Wellbore@1.0.0.yaml"));

    [Fact]
    public void UnparseableFlow_IsAnError_NamingTheLoaderCause()
    {
        // A missing source: the sync would treat this as a non-flow yaml and silently import nothing.
        var yaml = "flowType: test\nname: broken\n";
        var result = Run([new ProposalFile("flows/broken.flow.yaml", yaml)]);

        var error = Assert.Single(result.Errors);
        Assert.Equal("flows/broken.flow.yaml", error.Path);
        Assert.Contains("would never land", error.Message, StringComparison.Ordinal);
        Assert.Contains("source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownKind_IsAnError_NamingTheKindsThisHostKnows()
    {
        var result = Run([new ProposalFile("flows/legacy.flow.yaml", "flowType: ing\nname: legacy\n")]);

        var error = Assert.Single(result.Errors);
        Assert.Contains("unknown flowType 'ing'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'test'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidFlowUnderYmlExtension_IsAnError_BecauseTheSyncNeverDiscoversIt()
    {
        var result = Run([new ProposalFile("flows/orders.flow.yml", Flow)]);

        var error = Assert.Single(result.Errors);
        Assert.Contains(".yaml", error.Message, StringComparison.Ordinal);
        Assert.Contains("never import", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionFiles_AreNotPreflighted()
    {
        var result = Run(
        [
            new ProposalFile("flows/mapping.json", "{ \"not\": \"a flow\" }"),
            new ProposalFile("flows/README.md", "# notes"),
        ]);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void RevisionThatRepointsTheTarget_WarnsWithTheExactEndpointDiff()
    {
        var revised = Flow.Replace("osdu://partition/orders", "osdu://partition/orders_v2", StringComparison.Ordinal);
        var result = Run(
            [new ProposalFile("flows/orders.flow.yaml", revised)],
            new FlowProposalPreflight.ExistingPipeline("orders-delivery", "flows/orders.flow.yaml", Flow));

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("CHANGES its declared endpoints", warning.Message, StringComparison.Ordinal);
        Assert.Contains("osdu://partition/orders", warning.Message, StringComparison.Ordinal);
        Assert.Contains("osdu://partition/orders_v2", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RevisionThatOnlyTunesBehavior_DoesNotWarn()
    {
        // Joining a schedule and naming a batch changes no endpoint, so the revision is silent.
        var revised = Flow + "\nschedule: nightly\nbatch: nightly\n";
        var result = Run(
            [new ProposalFile("flows/orders.flow.yaml", revised)],
            new FlowProposalPreflight.ExistingPipeline("orders-delivery", "flows/orders.flow.yaml", Flow));

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void NewFileReusingAnExistingFlowName_Warns()
    {
        var result = Run(
            [new ProposalFile("flows/orders-copy.flow.yaml", Flow)],
            new FlowProposalPreflight.ExistingPipeline("orders-delivery", "flows/orders.flow.yaml", Flow));

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("already declared by 'flows/orders.flow.yaml'", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoProposalFilesDeclaringOneFlowName_Warn()
    {
        var result = Run(
        [
            new ProposalFile("flows/a.flow.yaml", Flow),
            new ProposalFile("flows/b.flow.yaml", Flow),
        ]);

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("same proposal", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduleLibrary_IsCheckedWithItsOwnLoader_AndSurfacesWarningsWithoutBlocking()
    {
        // A schedule with neither cron, interval, nor after is silently dropped by the sync; the preflight
        // surfaces the library loader's own warning instead of misreading the file as a broken flow.
        var library = "schedules:\n  citybike_daily:\n    enabled: false\n";
        var result = Run([new ProposalFile("flows/schedules.yaml", library)]);

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("citybike_daily", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RevisionThatRepointsTheSource_Warns()
    {
        var revised = Flow.Replace("lake://raw/orders/", "lake://raw/voi/", StringComparison.Ordinal);
        var result = Run(
            [new ProposalFile("flows/orders.flow.yaml", revised)],
            new FlowProposalPreflight.ExistingPipeline("orders-delivery", "flows/orders.flow.yaml", Flow));

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("lake://raw/orders/", warning.Message, StringComparison.Ordinal);
        Assert.Contains("lake://raw/voi/", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RevisionWhoseStoredCopyNoLongerParses_WarnsAndDoesNotBlock()
    {
        var result = Run(
            [new ProposalFile("flows/orders.flow.yaml", Flow)],
            new FlowProposalPreflight.ExistingPipeline(
                "orders-delivery", "flows/orders.flow.yaml", "flowType: test\nname: orders-delivery\n"));

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("could not be compared", warning.Message, StringComparison.Ordinal);
    }
}
