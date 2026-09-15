using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Submissions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The caller's own name for a submission: what is taken, what is refused, and that one accepted reference reads the
/// same through every door it travels (the accepted request, the drop's manifest, the ledger's submission).
/// </summary>
public class SubmissionReferenceTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\t\n ", null)]
    [InlineData("L-1001.las", "L-1001.las")]
    [InlineData("  L-1001.las  ", "L-1001.las")]
    public void Blank_is_no_reference_at_all_and_surrounding_space_never_survives(string? given, string? stored)
        => Assert.Equal(stored, SubmissionReference.Normalize(given));

    [Fact]
    public void A_name_is_taken_and_anything_that_is_not_a_name_is_refused()
    {
        Assert.Null(SubmissionReference.Refusal("NO 15/9-19 SR___GR.las"));
        Assert.Null(SubmissionReference.Refusal(null));
        Assert.Null(SubmissionReference.Refusal(new string('x', SubmissionReference.MaxLength)));

        // Surrounding space is not content, so it never pushes a reference over the ceiling.
        Assert.Null(SubmissionReference.Refusal("  " + new string('x', SubmissionReference.MaxLength) + "  "));

        var tooLong = SubmissionReference.Refusal(new string('x', SubmissionReference.MaxLength + 1));
        Assert.NotNull(tooLong);
        Assert.Contains(SubmissionReference.MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture), tooLong, StringComparison.Ordinal);

        var control = SubmissionReference.Refusal("jobid");
        Assert.NotNull(control);
        Assert.Contains("control character", control, StringComparison.Ordinal);
    }

    [Fact]
    public void The_field_the_refusal_names_is_the_key_the_caller_wrote()
    {
        Assert.StartsWith("reference is at most", SubmissionReference.Refusal(new string('x', 500))!, StringComparison.Ordinal);
        Assert.StartsWith("label is at most", SubmissionReference.Refusal(new string('x', 500), "label")!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_is_part_of_the_request_the_submission_id_names()
    {
        var flow = new DeliveryDocumentLoader().LoadFlow(Samples.Flow);
        var id = Guid.NewGuid();
        var values = new Dictionary<string, string> { ["logSource"] = "STAT_COMP" };
        var records = InlineRecords.Parse("""[{"record":{"x":1}}]""");

        var accepted = InlineSubmissionState.Accept(id, flow, "deliver", false, values, records, DateTime.UtcNow, "api:source", "L-1001.las");
        var repeat = InlineSubmissionState.Accept(id, flow, "deliver", false, values, records, DateTime.UtcNow, "api:another", "  L-1001.las  ");
        Assert.Empty(accepted.Differences(repeat));
        Assert.Equal(accepted.RequestHash, repeat.RequestHash);
        Assert.Equal("L-1001.las", accepted.Reference);

        // A retry that relabels the work is a different request, and the conflict says which way round.
        var relabelled = InlineSubmissionState.Accept(id, flow, "deliver", false, values, records, DateTime.UtcNow, "api:source", "L-1002.las");
        Assert.Equal(["the reference ('L-1001.las', not 'L-1002.las')"], accepted.Differences(relabelled));
        Assert.NotEqual(accepted.RequestHash, relabelled.RequestHash);

        var unlabelled = InlineSubmissionState.Accept(id, flow, "deliver", false, values, records, DateTime.UtcNow, "api:source");
        Assert.Equal(["the reference ('L-1001.las', not none)"], accepted.Differences(unlabelled));
        Assert.Null(unlabelled.Reference);
        Assert.Empty(unlabelled.Differences(InlineSubmissionState.Accept(id, flow, "deliver", false, values, records, DateTime.UtcNow, "api:x", "   ")));
    }

    [Fact]
    public void A_reference_that_is_not_a_name_never_becomes_an_accepted_request()
    {
        var flow = new DeliveryDocumentLoader().LoadFlow(Samples.Flow);
        var values = new Dictionary<string, string> { ["logSource"] = "STAT_COMP" };
        var records = InlineRecords.Parse("""[{"record":{"x":1}}]""");
        var ex = Assert.Throws<ArgumentException>(() => InlineSubmissionState.Accept(
            Guid.NewGuid(), flow, "deliver", false, values, records, DateTime.UtcNow, "api:source", new string('x', 5000)));
        Assert.Contains("at most", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_travels_onto_the_submission_the_flow_registers_for_the_records()
    {
        // The name the caller gave the work is what an operator searches by afterwards, so it has to read the same on
        // the accepted request and on the submission the OSDU flow registers when it plans those records.
        var flow = new DeliveryDocumentLoader().LoadFlow(Samples.Flow);
        var accepted = InlineSubmissionState.Accept(
            Guid.NewGuid(), flow, "deliver", false, new Dictionary<string, string> { ["logSource"] = "STAT_COMP" },
            InlineRecords.Parse("""[{"record":{"x":1}}]"""), DateTime.UtcNow, "api:source", "  NO 15/9-19 SR___GR.las  ");

        var submission = new SubmissionState
        {
            SubmissionId = accepted.SubmissionId,
            FlowId = accepted.FlowId,
            FlowName = accepted.FlowName,
            MappingReference = accepted.MappingReference,
            RenderContext = "{}",
            Kind = SubmissionKinds.Inline,
            Reference = accepted.Reference,
            ReceivedUtc = accepted.ReceivedUtc,
        };

        Assert.Equal("NO 15/9-19 SR___GR.las", submission.Reference);
        Assert.Null(SubmissionReference.Refusal(submission.Reference));
    }
}
