using System.Text.Json;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// How a run.json artifact becomes a run row, specifically the count fields the boards read. A kind names its
/// headline count in its own words (a file reader says <c>rowsLoaded</c>, an older writer said <c>totalRows</c>,
/// a delivery says <c>delivered</c>), and the projection has to understand all of them, in a fixed order, so a
/// run that moved thousands of records is never indistinguishable in the GUI from one that did nothing. Pure:
/// the projection is a function of the document.
/// </summary>
public sealed class RunArtifactProjectionTests
{
    private static readonly Guid RepoId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static CatalogRun Project(string json)
    {
        using var document = JsonDocument.Parse(json);
        var run = CatalogProjection.RunFromJson(document.RootElement, RepoId);
        Assert.NotNull(run);
        return run;
    }

    private static string Artifact(string flowKind, string resultFields) => $$"""
        {
          "schemaVersion": 1,
          "runId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
          "flowName": "demo_flow",
          "flowKind": "{{flowKind}}",
          "success": true,
          "writtenUtc": "2026-08-30T09:00:00Z",
          "result": { "startTimeUtc": "2026-08-30T08:59:58Z", "endTimeUtc": "2026-08-30T09:00:00Z", {{resultFields}} }
        }
        """;

    [Fact]
    public void RowsLoaded_IsReadFromTheArtifact_AlongsideTheChangeCounts()
    {
        var run = Project(Artifact("test", """ "rowsLoaded": 500, "rowsInserted": 480, "rowsUpdated": 15, "rowsDeleted": 5 """));

        Assert.Equal(500, run.RowsLoaded);
        Assert.Equal(480, run.RowsInserted);
        Assert.Equal(15, run.RowsUpdated);
        Assert.Equal(5, run.RowsDeleted);
    }

    [Fact]
    public void TotalRows_StillFillsRowsLoaded_ForAnArtifactThatNamesItThatWay()
    {
        var run = Project(Artifact("test", """ "totalRows": 42 """));

        Assert.Equal(42, run.RowsLoaded);
    }

    [Fact]
    public void DeliveredCount_FillsRowsLoaded_ForADeliveryArtifact()
    {
        // A delivery names its headline count for what it is: records delivered to the platform.
        var run = Project(Artifact("delivery", """ "delivered": 127, "rowsInserted": 120, "rowsUpdated": 7 """));

        Assert.Equal(127, run.RowsLoaded);
        Assert.Equal(120, run.RowsInserted);
        Assert.Equal(7, run.RowsUpdated);
    }

    [Fact]
    public void TheFieldsAreOrdered_NotMerged()
    {
        // An artifact carrying several must report the first-ranked field, never a sum or a later fallback: the
        // fallbacks exist to fill a gap, not to override a value the writer stated.
        var run = Project(Artifact("test", """ "rowsLoaded": 10, "totalRows": 20, "delivered": 30 """));

        Assert.Equal(10, run.RowsLoaded);
    }

    [Fact]
    public void ARunThatLoadedNothing_ReportsZero_NotNull()
    {
        // Zero and "not reported" must stay distinguishable: a genuinely empty run has to be readable as empty.
        var run = Project(Artifact("delivery", """ "delivered": 0, "rowsInserted": 0 """));

        Assert.Equal(0, run.RowsLoaded);
    }

    [Fact]
    public void AnArtifactWithNoRowFields_LeavesRowsLoadedNull()
    {
        var run = Project(Artifact("test", """ "filesWritten": 2 """));

        Assert.Null(run.RowsLoaded);
    }
}
