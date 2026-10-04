using System.Text.Json.Nodes;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Validation;
using Xunit;
using static SqlFlow.Delivery.Tests.ValidationFixtures;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a check of a record comes to (<see cref="ValidationVerdict"/>): its outcome from its problems, the parts it could
/// not check and the records it refers to; the form it is kept in on an attempt, bounded and read back whole; and the error
/// a held record carries, which records broken the same way share as one issue.
/// </summary>
public sealed class ValidationVerdictTests
{
    private static readonly DateTime At = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyDictionary<string, ReferenceAnswer> NoAnswers = new Dictionary<string, ReferenceAnswer>(StringComparer.Ordinal);

    private static ValidationVerdict Verdict(JsonObject record, IReadOnlyDictionary<string, ReferenceAnswer>? answers = null, ValidationLimits? limits = null)
        => ValidationVerdict.Of(RecordValidator.Check(record, Rules, limits: limits), Rules, VerdictSchema.Template, answers ?? NoAnswers, At);

    [Fact]
    public void A_record_meeting_every_rule_is_valid_against_its_schema_and_says_how_much_was_checked()
    {
        var verdict = Verdict(ValidRecord());

        Assert.Equal(ValidationOutcome.Valid, verdict.Outcome);
        Assert.Equal(new VerdictSchema(Kind, Schema.Version, VerdictSchema.Template), verdict.Schema);
        Assert.True(verdict.Rules > 0);
        Assert.Equal(3, verdict.References.Total);
        Assert.Equal(3, verdict.References.NotChecked);
        Assert.Equal(At, verdict.CheckedUtc);
        Assert.Equal(ValidationVerdict.CurrentRulesVersion, verdict.RulesVersion);
        Assert.StartsWith($"valid against {Kind}: ", verdict.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_rule_makes_a_record_invalid_and_a_part_not_checked_alone_makes_it_unverified()
    {
        Assert.Equal(ValidationOutcome.Invalid, Verdict(Record(r => DataOf(r)["Status"] = "Planned")).Outcome);

        var deep = Verdict(ValidRecord(), limits: new ValidationLimits { MaxDepth = 2 });
        Assert.Equal(ValidationOutcome.Unverified, deep.Outcome);
        Assert.Equal(0, deep.ProblemCount);
        Assert.StartsWith("unverified against", deep.Summary(), StringComparison.Ordinal);

        // A problem outweighs what was not checked.
        var both = Verdict(Record(r => r["legal"]!["status"] = "pending"), limits: new ValidationLimits { MaxDepth = 2 });
        Assert.Equal(ValidationOutcome.Invalid, both.Outcome);
        Assert.True(both.UnverifiedCount > 0);
    }

    [Fact]
    public void A_record_referring_to_one_nothing_holds_is_invalid_by_the_rule_reference_and_names_it()
    {
        var answers = new Dictionary<string, ReferenceAnswer>(StringComparer.Ordinal)
        {
            ["dev:master-data--Well:W-1"] = new(ReferenceState.InLedger),
            ["dev:reference-data--MeasurementType:KB"] = new(ReferenceState.Missing, "version v7 of the cache of partition 'dev' holds no such reference-data--MeasurementType record in MeasurementType"),
            ["dev:master-data--Field:F-1"] = new(ReferenceState.InOsdu),
        };

        var verdict = Verdict(ValidRecord(), answers);

        Assert.Equal(ValidationOutcome.Invalid, verdict.Outcome);
        Assert.Equal(1, verdict.ProblemCount);
        var problem = Assert.Single(verdict.Problems);
        Assert.Equal("reference", problem.Rule);
        Assert.Equal("data.Measurements[].TypeID", problem.At);
        Assert.Equal("data.Measurements[0].TypeID", problem.Path);
        Assert.Contains("'dev:reference-data--MeasurementType:KB:'", problem.Message, StringComparison.Ordinal);
        Assert.Contains("holds no such", problem.Message, StringComparison.Ordinal);
        Assert.Equal((3, 1, 0, 1, 1, 0), (verdict.References.Total, verdict.References.InLedger, verdict.References.InCache, verdict.References.InOsdu, verdict.References.Missing, verdict.References.NotChecked));
        Assert.Equal("dev:reference-data--MeasurementType:KB:", Assert.Single(verdict.References.MissingListed).Id);
    }

    [Fact]
    public void A_reference_nothing_could_say_anything_about_leaves_the_outcome_as_the_schema_left_it()
    {
        var verdict = Verdict(ValidRecord(), new Dictionary<string, ReferenceAnswer>(StringComparer.Ordinal)
        {
            ["dev:master-data--Well:W-1"] = new(ReferenceState.NotChecked),
        });

        Assert.Equal(ValidationOutcome.Valid, verdict.Outcome);
        Assert.Equal(3, verdict.References.NotChecked);
    }

    [Fact]
    public void Messages_and_values_are_redacted_as_every_stored_error_is()
    {
        var secret = "Bearer abcdefghijklmnopqrstuvwxyz0123456789";
        var verdict = Verdict(Record(r => DataOf(r)["Code"] = secret));

        var problem = Assert.Single(verdict.Problems);
        Assert.DoesNotContain("abcdefghijklmnop", problem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", problem.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", verdict.ToJson().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_verdict_kept_on_an_attempt_reads_back_as_it_was_reached()
    {
        var verdict = Verdict(Record(r =>
        {
            DataOf(r)["Status"] = "Planned";
            DataOf(r)["Measurements"] = new JsonArray(new JsonObject { ["Value"] = 1 });
        })) with { Accepted = true };

        var result = AttemptResult.WithValidation("""{"correlationId":"c-1","steps":[]}""", verdict);
        var read = AttemptResult.Validation(result)!;

        Assert.Equal(verdict.Outcome, read.Outcome);
        Assert.Equal(verdict.Schema, read.Schema);
        Assert.Equal(verdict.Rules, read.Rules);
        Assert.Equal(verdict.ProblemCount, read.ProblemCount);
        Assert.Equal(verdict.Problems, read.Problems);
        Assert.Equal(verdict.UnverifiedCount, read.UnverifiedCount);
        Assert.Equal(verdict.References.Total, read.References.Total);
        Assert.Equal(verdict.Notes, read.Notes);
        Assert.True(read.Accepted);
        Assert.Equal(verdict.CheckedUtc, read.CheckedUtc);
        Assert.Equal("c-1", JsonNode.Parse(result!)!["correlationId"]!.GetValue<string>());

        Assert.Null(AttemptResult.Validation("""{"steps":[]}"""));
        Assert.Null(AttemptResult.Validation(null));
        Assert.Null(AttemptResult.Validation("not json"));
        Assert.Null(ValidationVerdict.FromJson(new JsonObject { ["outcome"] = "maybe" }));
    }

    [Fact]
    public void A_verdict_of_any_size_fits_its_bound_on_an_attempt_and_says_it_was_shortened()
    {
        var items = new JsonArray(Enumerable.Range(0, 400).Select(i => (JsonNode?)new JsonObject { ["TypeID"] = new string('x', 500) + i }).ToArray());
        var verdict = Verdict(Record(r => DataOf(r)["Measurements"] = items), limits: new ValidationLimits { MaxProblemsListed = 50 });

        var json = verdict.ToJson();
        Assert.True(json.ToJsonString().Length <= ValidationVerdict.MaxJsonChars, $"{json.ToJsonString().Length} characters");
        Assert.True(json["shortened"]!.GetValue<bool>());
        Assert.Equal(400, json["problemCount"]!.GetValue<long>());

        Assert.False(Verdict(ValidRecord()).ToJson().ContainsKey("shortened"));
    }

    [Fact]
    public void Records_held_for_the_same_rules_share_one_issue_whatever_values_broke_them_and_other_rules_are_another()
    {
        static string Hold(string status, string code) => Verdict(Record(r =>
        {
            DataOf(r)["Status"] = status;
            DataOf(r)["Code"] = code;
        })).HoldMessage("validation.mode is enforce");

        var first = Hold("Planned", "ab-1");
        var second = Hold("Drilling", "zz-99");
        Assert.StartsWith($"validation: the record breaks the schema of {Kind}: ", first, StringComparison.Ordinal);
        Assert.Contains("validation.mode is enforce, so it is held", first, StringComparison.Ordinal);
        Assert.Contains("release it to send this document as it is", first, StringComparison.Ordinal);
        Assert.Equal(ProblemSignature.Of(first), ProblemSignature.Of(second));
        Assert.NotEqual(ProblemSignature.Of(first), ProblemSignature.Of(Verdict(Record(r => DataOf(r)["Status"] = "Planned")).HoldMessage("validation.mode is enforce")));
    }

    [Fact]
    public void A_hold_names_the_first_problems_and_counts_the_rest()
    {
        var verdict = Verdict(Record(r =>
        {
            DataOf(r)["Status"] = "Planned";
            DataOf(r)["Code"] = "x";
            DataOf(r)["Depth"] = -1;
            DataOf(r)["Count"] = 1.5;
            DataOf(r)["Name"] = "";
        }));

        // An empty name breaks both its length and its pattern: six problems, three named.
        var hold = verdict.HoldMessage("validation.mode is enforce");
        Assert.Equal(6, verdict.ProblemCount);
        Assert.Contains("; and 3 more.", hold, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unverified_hold_says_the_record_could_not_be_fully_checked_and_which_setting_held_it()
    {
        var verdict = Verdict(ValidRecord(), limits: new ValidationLimits { MaxDepth = 2 });
        var hold = verdict.HoldMessage("validation.unverified is hold");

        Assert.StartsWith("validation: the record could not be fully checked against the schema of", hold, StringComparison.Ordinal);
        Assert.Contains("validation.unverified is hold", hold, StringComparison.Ordinal);
    }

    [Fact]
    public void A_try_that_sent_nothing_of_the_document_says_why_nothing_was_checked()
    {
        var verdict = ValidationVerdict.NotChecked("the try sends the payload alone", At);

        Assert.Equal(ValidationOutcome.NotValidated, verdict.Outcome);
        Assert.Equal("not validated: the try sends the payload alone", verdict.Summary());
        Assert.Equal("notValidated", verdict.ToJson()["outcome"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(ValidationOutcome.Valid, "valid")]
    [InlineData(ValidationOutcome.Invalid, "invalid")]
    [InlineData(ValidationOutcome.Unverified, "unverified")]
    [InlineData(ValidationOutcome.NotValidated, "notValidated")]
    public void An_outcome_is_written_and_read_by_one_name(ValidationOutcome outcome, string name)
    {
        Assert.Equal(name, ValidationOutcomes.Name(outcome));
        Assert.Equal(outcome, ValidationOutcomes.Parse(name));
        Assert.Null(ValidationOutcomes.Parse(name.ToUpperInvariant()));
    }
}
