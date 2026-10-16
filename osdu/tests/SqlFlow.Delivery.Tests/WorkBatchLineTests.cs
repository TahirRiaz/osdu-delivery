using System.Text;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A line of a work batch carries a rendered document and, beside it, what the mapping's assertions found of that document
/// (osdu/docs/reference/flow/mapping-assertions.md); a line written before mappings could assert reads as one without them.
/// </summary>
public sealed class WorkBatchLineTests
{
    private static readonly Guid Key = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f");

    [Fact]
    public void A_line_carries_the_findings_beside_its_document()
    {
        var log = new AssertionLog("WellLog@1.4.0");
        log.Judged();
        log.Fail(
            "data.Name", "data.Name",
            new NodeAssertion { Label = "named", Location = "record.data.Name.$assert[0]", Condition = new ValueCondition { Operator = ValueOperator.Exists } },
            AssertionAction.Hold, "(no value)", "has no value");
        var item = new WorkItem(Key, "dev:work-product-component--WellLog:abc", """{"kind":"k","data":{}}""", log.Findings().ToText());

        var read = WorkBatchFile.Decode(WorkBatchFile.Encode(item));

        Assert.Equal(item.Key, read.Key);
        Assert.Equal(item.TargetId, read.TargetId);
        Assert.Equal(item.Document, read.Document);
        var findings = AssertionFindings.FromText(read.Assertions)!;
        Assert.Equal(1, findings.Held);
        Assert.Equal("named", Assert.Single(findings.Failures).Assertion);
    }

    [Fact]
    public void A_line_without_findings_reads_as_a_document_its_mapping_asserted_nothing_of()
    {
        var written = WorkBatchFile.Encode(new WorkItem(Key, "dev:x--Y:1", """{"kind":"k"}"""));
        Assert.DoesNotContain("assertions", Encoding.UTF8.GetString(written), StringComparison.Ordinal);
        Assert.Null(WorkBatchFile.Decode(written).Assertions);

        // A line an earlier release wrote, before mappings could assert.
        var earlier = Encoding.UTF8.GetBytes($$$"""{"key":"{{{Key:D}}}","targetId":"dev:x--Y:1","document":{"kind":"k"}}""");
        Assert.Null(WorkBatchFile.Decode(earlier).Assertions);

        // Findings written as empty text are never written at all, so a line always reads back.
        Assert.Null(WorkBatchFile.Decode(WorkBatchFile.Encode(new WorkItem(Key, "dev:x--Y:1", """{"kind":"k"}""", string.Empty))).Assertions);
    }
}
