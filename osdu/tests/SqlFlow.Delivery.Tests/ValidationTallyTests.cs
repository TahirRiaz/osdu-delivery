using System.Text.Json.Nodes;
using SqlFlow.Delivery.Validation;
using Xunit;
using static SqlFlow.Delivery.Tests.ValidationFixtures;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a run says once about what the gate found (<see cref="ValidationTally"/>): counted by template and outcome, the
/// rules broken most often named, bounded however many records it counts, and exact when concurrent deliveries report.
/// </summary>
public sealed class ValidationTallyTests
{
    private static readonly DateTime At = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private static ValidationVerdict Verdict(Action<JsonObject>? change = null)
        => ValidationVerdict.Of(
            RecordValidator.Check(change is null ? ValidRecord() : Record(change), Rules),
            Rules,
            VerdictSchema.Template,
            new Dictionary<string, ReferenceAnswer>(StringComparer.Ordinal),
            At);

    [Fact]
    public void Verdicts_are_counted_by_template_and_outcome_with_the_rules_broken_most_often_first()
    {
        var tally = new ValidationTally();
        for (var i = 0; i < 5; i++)
        {
            tally.Add(Verdict(), held: false);
        }

        for (var i = 0; i < 3; i++)
        {
            tally.Add(Verdict(r => DataOf(r)["Status"] = "Planned"), held: true);
        }

        tally.Add(Verdict(r => DataOf(r)["Code"] = "x"), held: false);
        tally.Add(Verdict(r => DataOf(r)["Code"] = "y") with { Accepted = true }, held: false);
        tally.Add(ValidationVerdict.NotChecked("payload alone", At), held: false);

        Assert.Equal(10, tally.Validated);
        var line = Assert.Single(tally.Lines());
        Assert.Equal(
            $"Validated 10 document(s) against {Kind}: 5 valid, 5 invalid, 0 unverified; 3 held for it, 1 sent as a release accepted them. Most broken: data.Status enum (3), data.Code pattern (2).",
            line);
        Assert.Equal("10 validated (5 invalid, 0 unverified, 3 held for it)", tally.Brief());
    }

    [Fact]
    public void Nothing_checked_says_nothing()
    {
        var tally = new ValidationTally();
        tally.Add(ValidationVerdict.NotChecked("payload alone", At), held: false);

        Assert.Equal(0, tally.Validated);
        Assert.Empty(tally.Lines());
    }

    [Fact]
    public void The_most_checked_templates_are_named_and_the_rest_counted_together()
    {
        var tally = new ValidationTally();
        for (var kind = 0; kind < 8; kind++)
        {
            for (var n = 0; n <= kind; n++)
            {
                tally.Add(Verdict() with { Schema = new VerdictSchema($"test:wks:master-data--Kind{kind}:1.0.0", "v", VerdictSchema.Template) }, held: false);
            }
        }

        var lines = tally.Lines(maxKinds: 3);
        Assert.Equal(4, lines.Count);
        Assert.Contains("Kind7", lines[0], StringComparison.Ordinal);
        Assert.Equal("and 5 more template(s), 15 document(s) between them", lines[^1]);
    }

    [Fact]
    public void A_template_keeps_a_bounded_number_of_rules_by_name_and_counts_the_rest()
    {
        var tally = new ValidationTally();
        for (var i = 0; i < ValidationTally.MaxRulesKept + 25; i++)
        {
            var verdict = Verdict(r => DataOf(r)["Status"] = "Planned");
            var renamed = verdict.Problems.Select(p => p with { At = $"data.Field{i}" }).ToList();
            tally.Add(verdict with { Problems = renamed }, held: false);
        }

        Assert.EndsWith(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"; 25 more break(s) of rules past the first {ValidationTally.MaxRulesKept} kept."), Assert.Single(tally.Lines()), StringComparison.Ordinal);
    }

    [Fact]
    public void Concurrent_deliveries_reporting_at_once_are_all_counted()
    {
        var tally = new ValidationTally();
        var valid = Verdict();
        var invalid = Verdict(r => DataOf(r)["Status"] = "Planned");

        Parallel.For(0, 10_000, i => tally.Add(i % 4 == 0 ? invalid : valid, held: i % 8 == 0));

        Assert.Equal(10_000, tally.Validated);
        Assert.Equal("10,000 validated (2,500 invalid, 0 unverified, 1,250 held for it)", tally.Brief());
    }
}
