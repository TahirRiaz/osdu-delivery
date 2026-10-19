using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A warning band (warnWithin): a count, an aggregate or a proof value that misses what it expects by no more than the band
/// warns rather than fails, the band a distance or a percentage of each number expected, every bound widened its own way;
/// and a band that could warn of nothing is refused where the document is read.
/// </summary>
public sealed class WarningBandTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";

    private static readonly DeliveryDocumentLoader Loader = new();

    private static string Flow(string assertions, string bulk) => $$"""
        flowType: assertion
        name: qc-key-numbers
        partitions: [dev]
        source:
          endpoint: http://localhost
        tests:
          - name: logs
            kind: {{WellLog}}
            {{bulk}}
            assert:
        {{assertions}}
        """;

    private static AssertionTest Test(string assertions, string bulk = "maxRecords: 100")
        => Assert.Single(Loader.ParseAssertion(Flow(assertions, bulk), "flow.yaml").Tests);

    private static string Refusal(string assertions)
        => Assert.Throws<FlowValidationException>(() => Test(assertions, "bulk: { maxRows: 10 }")).Message;

    private static Comparison Equals(double value) => new([new ComparisonTerm(ComparisonOperator.EqualTo, ExpectedValue.OfNumber(value))]);

    private static Comparison Of(ComparisonOperator op, double value, double? upper = null)
        => new([new ComparisonTerm(op, ExpectedValue.OfNumber(value), upper is { } high ? ExpectedValue.OfNumber(high) : null)]);

    [Fact]
    public void A_count_and_an_aggregate_take_a_band_of_a_distance_or_a_percentage()
    {
        var test = Test("      - { count: 62, warnWithin: 10% }\n      - { aggregate: count, field: data.Curves.CurveID, except: [MD], equals: 7842, warnWithin: 250 }");

        var count = (CountAssertion)test.Assertions[0];
        Assert.Equal(new WarningBand(10, Percent: true), count.WarnWithin);
        Assert.Equal("equals 62 (warning within 10%)", count.Expected);

        var aggregate = (AggregateAssertion)test.Assertions[1];
        Assert.Equal(new WarningBand(250, Percent: false), aggregate.WarnWithin);
        Assert.Equal("equals 7842 (warning within 250), leaving out \"MD\"", aggregate.Expected);
    }

    [Fact]
    public void A_proof_value_takes_its_own_band_or_the_one_its_assertion_gives_where_it_compares_a_number()
    {
        var known = (KnownValuesAssertion)Test(string.Join('\n',
            "      - knownValues:",
            "          warnWithin: 5%",
            "          locateBy: { data.Name: COMPOSITE }",
            "          check:",
            "            data.SamplingStart: 200",
            "            data.LogSource: OPERATOR",
            "            data.SamplingStop: { atMost: 3000, warnWithin: 100 }",
            "            data.Curves.CurveID: { aggregate: count, equals: 20 }",
            "            data.Curves.TopDepth: { exists: true }",
            "            data.SamplingInterval: { equals: 0, tolerance: 0.001 }")).Assertions[0];

        var checks = known.Rows.Single().Checks;
        Assert.Equal(new WarningBand(5, Percent: true), checks[0].Column.WarnWithin);
        Assert.Null(checks[1].Column.WarnWithin);
        Assert.Equal(new WarningBand(100, Percent: false), checks[2].Column.WarnWithin);
        Assert.Equal(new WarningBand(5, Percent: true), checks[3].Column.WarnWithin);
        Assert.Null(checks[4].Column.WarnWithin);

        // 5% of 0 widens nothing, so the assertion's band leaves that proof value as it is rather than refusing it.
        Assert.Null(checks[5].Column.WarnWithin);
    }

    [Theory]
    [InlineData("      - { count: 62, warnWithin: 0 }", "is not a warning band: a distance greater than zero")]
    [InlineData("      - { count: 62, warnWithin: 150% }", "a percentage is at most 100%")]
    [InlineData("      - { count: 62, warnWithin: lots }", "'lots' is not a warning band")]
    [InlineData("      - { count: { notEquals: 0 }, warnWithin: 1 }", "notEquals says what the value must not be, which has nothing to widen")]
    [InlineData("      - { count: 0, warnWithin: 10% }", "warnWithin 10% of 0 is nothing, so nothing would warn; give a distance")]
    [InlineData("      - { aggregate: sum, field: data.A, equals: 10, tolerance: 2, warnWithin: 1 }", "warnWithin 1 is no wider than the tolerance 2")]
    [InlineData("      - { aggregate: max, field: data.D, atMost: '2026-01-01', warnWithin: 1 }", "is a distance between numbers, and \"2026-01-01\" is not a number")]
    [InlineData("      - { field: data.A, equals: 1, warnWithin: 1 }", "is a field assertion, which does not take warnWithin")]
    [InlineData("      - { column: GR, atMost: 1, warnWithin: 1 }", "is a column assertion, which does not take warnWithin")]
    [InlineData("      - { rowCount: 10, warnWithin: 1 }", "is a rowCount assertion, which does not take warnWithin")]
    [InlineData("      - knownValues: { locateBy: { data.Name: X }, check: { data.A: { equals: abc, warnWithin: 10% } } }", "\"abc\" is not a number")]
    [InlineData("      - knownValues: { locateBy: { data.Name: X }, check: { data.A: { matches: '^A', warnWithin: 1 } } }", "and the value is compared by matches")]
    [InlineData("      - knownValues: { locateBy: { data.Name: X }, check: { data.A: { equals: 1, warnWithin: ~ } } }", "warnWithin has no value")]
    [InlineData("      - knownValues: { locateBy: { data.Name: { equals: X, warnWithin: 1 } } }", "has 'warnWithin'; a row is located by values its record equals")]
    [InlineData("      - knownValues: { locateBy: { data.Name: X }, warnWithin: nope }", "'nope' is not a warning band")]
    public void A_band_that_could_warn_of_nothing_is_refused(string assertions, string expected)
        => Assert.Contains(expected, Refusal(assertions), StringComparison.Ordinal);

    [Fact]
    public void A_band_widens_an_equality_both_ways_a_lower_bound_down_and_an_upper_bound_up()
    {
        var tenPercent = new WarningBand(10, Percent: true);
        Assert.True(ValueComparer.Within(Equals(7842), Measured.Of(7600), null, tenPercent));
        Assert.True(ValueComparer.Within(Equals(7842), Measured.Of(8626), null, tenPercent));
        Assert.False(ValueComparer.Within(Equals(7842), Measured.Of(8700), null, tenPercent));
        Assert.False(ValueComparer.Within(Equals(7842), Measured.Of(7000), null, tenPercent));

        var five = new WarningBand(5, Percent: false);
        Assert.True(ValueComparer.Within(Of(ComparisonOperator.AtLeast, 100), Measured.Of(96), null, five));
        Assert.False(ValueComparer.Within(Of(ComparisonOperator.AtLeast, 100), Measured.Of(94), null, five));
        Assert.True(ValueComparer.Within(Of(ComparisonOperator.AtMost, 100), Measured.Of(104), null, five));
        Assert.False(ValueComparer.Within(Of(ComparisonOperator.LessThan, 100), Measured.Of(105), null, five));

        // A range widens each end by its own share: 10% of 10 below, 10% of 20 above.
        var range = Of(ComparisonOperator.Between, 10, 20);
        Assert.True(ValueComparer.Within(range, Measured.Of(9.1), null, tenPercent));
        Assert.False(ValueComparer.Within(range, Measured.Of(8.9), null, tenPercent));
        Assert.True(ValueComparer.Within(range, Measured.Of(21.9), null, tenPercent));
        Assert.False(ValueComparer.Within(range, Measured.Of(22.1), null, tenPercent));

        // The tolerance stays what holds; a band narrower than it adds nothing. A date is never within a band.
        Assert.True(ValueComparer.Within(Equals(100), Measured.Of(104), 5, new WarningBand(1, Percent: false)));
        Assert.False(ValueComparer.Within(Equals(100), Measured.Of(DateTimeOffset.UnixEpoch), null, five));
    }

    [Fact]
    public void A_miss_is_said_from_the_number_it_crosses_with_its_share_of_it()
    {
        Assert.Equal("242 below 7842 (3.09%)", ValueComparer.Miss(Equals(7842), Measured.Of(7600), null));
        Assert.Equal("1.9 above 20 (9.5%)", ValueComparer.Miss(Of(ComparisonOperator.Between, 10, 20), Measured.Of(21.9), null));
        Assert.Equal("0.9 below 10 (9%)", ValueComparer.Miss(Of(ComparisonOperator.Between, 10, 20), Measured.Of(9.1), null));
        Assert.Equal("0.5 above 0", ValueComparer.Miss(Equals(0), Measured.Of(0.5), null));
        Assert.Null(ValueComparer.Miss(Equals(7842), Measured.Of(7842), null));

        var nearest = new ValueCondition { Operator = ValueOperator.In, Operands = [ExpectedValue.OfNumber(10), ExpectedValue.OfNumber(100)] };
        Assert.Equal("7 below 100 (7%)", ValueComparer.Miss(nearest, JsonValue.Create(93.0)));
        Assert.True(ValueComparer.Within(nearest, JsonValue.Create(93.0), new WarningBand(10, Percent: true)));
        Assert.True(ValueComparer.Within(nearest, JsonValue.Create("93"), new WarningBand(10, Percent: true)));
        Assert.False(ValueComparer.Within(nearest, JsonValue.Create("ninety"), new WarningBand(10, Percent: true)));
    }

    [Fact]
    public async Task A_count_within_its_band_warns_and_one_beyond_it_fails()
    {
        var test = Test("      - { count: 62, warnWithin: 10% }\n      - { count: 62, warnWithin: 10%, severity: info }");
        var near = await new CountEvaluator(0, (CountAssertion)test.Assertions[0], 5).CompleteAsync(null!, new TestSubject(58, 0, false, null, []), CancellationToken.None);
        Assert.Equal((TestOutcomes.Failed, "warning"), (near.Outcome, near.Severity));
        Assert.Equal("The test matches 58 record(s); it expects equals 62; 4 below 62 (6.45%), within the warning band of 10%, so it warns.", near.Message);
        Assert.Equal(TestOutcomes.Warned, AssertionReport.Standing(near));

        var far = await new CountEvaluator(0, (CountAssertion)test.Assertions[0], 5).CompleteAsync(null!, new TestSubject(50, 0, false, null, []), CancellationToken.None);
        Assert.Equal((TestOutcomes.Failed, "error"), (far.Outcome, far.Severity));

        var exact = await new CountEvaluator(0, (CountAssertion)test.Assertions[0], 5).CompleteAsync(null!, new TestSubject(62, 0, false, null, []), CancellationToken.None);
        Assert.Equal(TestOutcomes.Passed, exact.Outcome);

        // An assertion of info stays info within its band: a band never makes an assertion weigh more.
        var noted = await new CountEvaluator(1, (CountAssertion)test.Assertions[1], 5).CompleteAsync(null!, new TestSubject(58, 0, false, null, []), CancellationToken.None);
        Assert.Equal("info", noted.Severity);
    }

    [Fact]
    public async Task An_aggregate_within_its_band_warns_saying_by_how_much_it_missed()
    {
        var test = Test("      - { aggregate: count, field: data.Curves.CurveID, except: [MD], equals: 4.2, warnWithin: 5% }");
        var evaluator = new AggregateEvaluator(0, (AggregateAssertion)test.Assertions[0], 5);
        foreach (var (id, curves) in new[] { ("a", "[\"MD\", \"DT\", \"GR\"]"), ("b", "[\"MD\", \"RD\", \"NPHI\"]") })
        {
            evaluator.Observe($"dev:work-product-component--WellLog:{id}", JsonNode.Parse($$"""{ "data": { "Curves": {{CurvesOf(curves)}} } }""")!);
        }

        var outcome = await evaluator.CompleteAsync(null!, new TestSubject(2, 2, false, null, []), CancellationToken.None);
        Assert.Equal((TestOutcomes.Failed, "warning"), (outcome.Outcome, outcome.Severity));
        Assert.EndsWith("0.2 below 4.2 (4.76%), within the warning band of 5%, so it warns.", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_column_aggregate_warns_when_every_record_that_misses_misses_within_its_band()
    {
        var test = Test("      - { aggregate: max, column: GR, atMost: 200, warnWithin: 10% }", "bulk: { columns: [MD, GR] }");
        var assertion = (AggregateAssertion)test.Assertions[0];
        var shape = new BulkShape(["MD", "GR"], 2);

        BulkVerdict Judge(string rows)
        {
            var check = new ColumnAggregateEvaluator(0, assertion, 5).Start("dev:work-product-component--WellLog:l1", shape, null);
            check.Observe(Frame(["MD", "GR"], rows));
            return check.Finish();
        }

        var held = Judge("[[1.0, 150.0], [2.0, 190.0]]");
        var near = Judge("[[1.0, 150.0], [2.0, 215.0]]");
        var far = Judge("[[1.0, 150.0], [2.0, 260.0]]");
        Assert.True(held.Passed);
        Assert.Equal((false, true), (near.Passed, near.WithinBand));
        Assert.EndsWith("15 above 200 (7.5%), within the warning band of 10%, so it warns", near.Reason, StringComparison.Ordinal);
        Assert.Equal((false, false), (far.Passed, far.WithinBand));

        var warned = new ColumnAggregateEvaluator(0, assertion, 5);
        warned.Settle("dev:work-product-component--WellLog:l1", held);
        warned.Settle("dev:work-product-component--WellLog:l2", near);
        var outcome = await warned.CompleteAsync(null!, new TestSubject(2, 2, false, null, []), CancellationToken.None);
        Assert.Equal((TestOutcomes.Failed, "warning"), (outcome.Outcome, outcome.Severity));

        var failed = new ColumnAggregateEvaluator(0, assertion, 5);
        failed.Settle("dev:work-product-component--WellLog:l1", near);
        failed.Settle("dev:work-product-component--WellLog:l2", far);
        Assert.Equal("error", (await failed.CompleteAsync(null!, new TestSubject(2, 2, false, null, []), CancellationToken.None)).Severity);
    }

    private static string CurvesOf(string ids)
        => "[" + string.Join(", ", JsonSerializer.Deserialize<string[]>(ids)!.Select(id => $$"""{ "CurveID": "{{id}}" }""")) + "]";

    private static BulkFrame Frame(string[] columns, string data)
    {
        using var document = JsonDocument.Parse(data);
        return new BulkFrame(columns, document.RootElement.Clone(), 0);
    }
}
