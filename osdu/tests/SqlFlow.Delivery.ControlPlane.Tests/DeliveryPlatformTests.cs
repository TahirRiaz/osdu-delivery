using System.Text.Json;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Documents;
using SqlFlow.SourceControl.Proposals;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// What the platform does with the module's own documents and run artifacts, from the platform's side: a mapping the
/// mapping builder proposes is preflighted as the companion document the delivery kind declares, and a delivery run's
/// artifact projects onto the run row with the count a delivery is measured by. Both are pure: no database, no host.
/// </summary>
public sealed class DeliveryPlatformTests
{
    /// <summary>The delivery kind, which also owns the mapping documents (<c>documentType: mapping</c>) a builder proposes.</summary>
    private static readonly DeliveryFlowKind Kind = new(new DeliveryDocumentLoader());

    /// <summary>The loader a host's managed sync parses with when the OSDU module is composed into it: SQLFlow's own
    /// kinds, plus the delivery kind as both a flow kind and the companion document kind it declares.</summary>
    private static readonly YamlDocumentLoader DeliveryDocuments = YamlDocumentLoader.CreateDefault([Kind], [Kind]);

    [Fact]
    public void MappingFromTheBuilder_PassesAsTheCompanionDocumentItDeclares()
    {
        var result = FlowProposalPreflight.Run([new ProposalFile("mappings/Wellbore@1.0.0.yaml", SampleMapping())], [], DeliveryDocuments);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void MappingThatDoesNotLoad_IsAnError_NamingWhyTheSyncWouldRecordItInvalid()
    {
        // A target written without its origin is not a template variable, so the sync would record the mapping as invalid.
        var yaml = SampleMapping().Replace("target: osdu.data.FacilityName", "target: data.FacilityName", StringComparison.Ordinal);

        var result = FlowProposalPreflight.Run([new ProposalFile("mappings/Wellbore@1.0.0.yaml", yaml)], [], DeliveryDocuments);

        var error = Assert.Single(result.Errors);
        Assert.Equal("mappings/Wellbore@1.0.0.yaml", error.Path);
        Assert.Contains("does not parse as a companion document", error.Message, StringComparison.Ordinal);
        // And it names the cause the sync would have recorded the mapping invalid for.
        Assert.Contains("osdu.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveredCount_FillsRowsLoaded_ForADeliveryArtifact()
    {
        // A delivery names its headline count for what it is: records delivered to the platform. The artifact carries it
        // as the platform's own rowsLoaded as well, so a run that delivered thousands is never shown as an empty one.
        var run = Project(Artifact("delivery", """ "delivered": 127, "rowsLoaded": 127, "rowsInserted": 120, "rowsUpdated": 7 """));

        Assert.Equal(127, run.RowsLoaded);
        Assert.Equal(120, run.RowsInserted);
        Assert.Equal(7, run.RowsUpdated);
    }

    [Fact]
    public void ARunThatDeliveredNothing_ReportsZero_NotNull()
    {
        // Zero and "not reported" must stay distinguishable: a genuinely empty delivery has to be readable as empty.
        var run = Project(Artifact("delivery", """ "delivered": 0, "rowsLoaded": 0, "rowsInserted": 0 """));

        Assert.Equal(0, run.RowsLoaded);
    }

    [Fact]
    public void ADeliveryArtifactsResultObject_IsRecordedOnTheRun_SoAFanOutRootCanReadItsMembersOutcome()
    {
        var json = Artifact("delivery", """ "operation": "intake", "planned": 12, "batches": 2 """);
        using var document = JsonDocument.Parse(json);

        var result = CatalogProjection.RunResultJson(document.RootElement);

        Assert.NotNull(result);
        Assert.Contains("\"planned\":12", result, StringComparison.Ordinal);
    }

    /// <summary>The sample estate's wellbore mapping, as the mapping builder writes one into a proposal.</summary>
    private static string SampleMapping()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "mappings", "Wellbore@1.0.0.yaml"));

    private static CatalogRun Project(string json)
    {
        using var document = JsonDocument.Parse(json);
        var run = CatalogProjection.RunFromJson(document.RootElement, Guid.Parse("11111111-1111-1111-1111-111111111111"));
        Assert.NotNull(run);
        return run;
    }

    private static string Artifact(string flowKind, string resultFields) => $$"""
        {
          "schemaVersion": 1,
          "runId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
          "flowName": "recall-wellbore",
          "flowKind": "{{flowKind}}",
          "success": true,
          "writtenUtc": "2026-09-16T09:00:00Z",
          "result": { "startTimeUtc": "2026-09-16T08:59:58Z", "endTimeUtc": "2026-09-16T09:00:00Z", {{resultFields}} }
        }
        """;
}
