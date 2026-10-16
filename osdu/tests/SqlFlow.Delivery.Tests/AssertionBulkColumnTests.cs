using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The columns a test's bulk read takes and the columns its assertions name agree: a document whose bulk.columns leaves out a
/// column an assertion names is refused where it is read, and a page the DDMS answers without a column the read asked for
/// fails the record saying so rather than stopping the test. Also the bound a unique assertion keeps to.
/// </summary>
public sealed class AssertionBulkColumnTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";

    private static readonly DeliveryDocumentLoader Loader = new();

    private static string Flow(string bulk, string assertions) => $$"""
        flowType: assertion
        name: welldb-welllog-04-assertion
        partitions: [dev]
        source:
          endpoint: http://localhost
        tests:
          - name: curves
            kind: {{WellLog}}
            {{bulk}}
            assert:
        {{assertions}}
        """;

    private static AssertionTest Test(string bulk, string assertions) => Assert.Single(Loader.ParseAssertion(Flow(bulk, assertions), "flow.yaml").Tests);

    [Theory]
    [InlineData("      - { column: GR, between: [0, 300] }", "'GR' (assert[1])")]
    [InlineData("      - { column: MD, between: [0, 300], where: [{ column: GR, greaterThan: 1 }] }", "'GR' (assert[1])")]
    [InlineData("      - { aggregate: max, column: GR, atMost: 500 }", "'GR' (assert[1])")]
    [InlineData("      - { column: DEPT, monotonic: increasing }", "'DEPT' (assert[1])")]
    public void A_bulk_column_list_that_leaves_out_a_column_an_assertion_names_is_refused(string assertion, string named)
    {
        var refused = Assert.Throws<FlowValidationException>(() => Test("bulk: { columns: [MD] }", "      - rowCount: { atLeast: 1 }\n" + assertion));

        Assert.Contains("flow.yaml: tests[0] 'curves': bulk.columns lists the only columns the test reads, and leaves out " + named, refused.Message, StringComparison.Ordinal);
        Assert.Contains("add it to bulk.columns, or leave bulk.columns out to read exactly the columns the assertions name", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bulk_column_list_naming_every_column_the_assertions_name_is_read_as_listed_and_one_left_out_reads_what_they_name()
    {
        var listed = Test("bulk: { columns: [MD, GR, CALI] }", "      - { column: GR, between: [0, 300], where: [{ column: MD, greaterThan: 1 }] }\n      - { column: MD, monotonic: increasing }");
        Assert.Equal(["MD", "GR", "CALI"], listed.Bulk!.Columns);

        var named = Test("bulk: { maxRows: 10 }", "      - { column: GR, between: [0, 300], where: [{ column: MD, greaterThan: 1 }] }\n      - { aggregate: min, column: DEPT, atLeast: 0 }");
        Assert.Equal(["GR", "MD", "DEPT"], named.Bulk!.Columns);

        // Several columns left out are named together, each with the first assertion that names it.
        var refused = Assert.Throws<FlowValidationException>(() => Test("bulk: { columns: [CALI] }", "      - { column: GR, between: [0, 300] }\n      - { column: MD, monotonic: increasing }"));
        Assert.Contains("leaves out 'GR' (assert[0]), 'MD' (assert[1]); add them to bulk.columns", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A page of bulk data in the DDMS's split orientation, as the reader hands it to every check.</summary>
    private static BulkFrame Frame(string[] columns, string rows)
    {
        using var document = JsonDocument.Parse(rows);
        return new BulkFrame(columns, document.RootElement.Clone(), 0);
    }

    [Fact]
    public void A_page_the_DDMS_answers_without_a_column_the_read_asked_for_fails_the_record_saying_so()
    {
        var test = Test(
            "bulk: { columns: [MD, GR] }",
            "      - { column: GR, between: [0, 300], where: [{ column: MD, greaterThan: 1 }] }\n      - { aggregate: max, column: GR, atMost: 500 }\n      - { column: GR, monotonic: increasing }");
        var shape = new BulkShape(["MD", "GR"], 2);
        var withoutGr = Frame(["MD"], "[[1.0], [2.0]]");
        var evaluators = new BulkEvaluator[]
        {
            new ColumnValueEvaluator(0, (ValueAssertion)test.Assertions[0], 5),
            new ColumnAggregateEvaluator(1, (AggregateAssertion)test.Assertions[1], 5),
            new MonotonicEvaluator(2, (MonotonicAssertion)test.Assertions[2], 5),
        };

        foreach (var evaluator in evaluators)
        {
            var check = evaluator.Start("dev:work-product-component--WellLog:l1", shape, null);
            Assert.True(check.ReadsRows);
            check.Observe(withoutGr);
            check.Observe(Frame(["MD", "GR"], "[[3.0, 10.0]]"));

            var verdict = check.Finish();

            Assert.False(verdict.Passed);
            Assert.Equal("the DDMS answered the page of its bulk data from row 1 without the column GR, which the read asked for and its description lists", verdict.Reason);
        }

        // A where condition's column left out of a page fails the record the same way.
        var filtered = new ColumnValueEvaluator(0, (ValueAssertion)test.Assertions[0], 5).Start("dev:work-product-component--WellLog:l2", shape, null);
        filtered.Observe(Frame(["GR"], "[[10.0]]"));
        Assert.Contains("without the column MD", filtered.Finish().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_holding_every_column_asked_for_is_judged_by_its_values()
    {
        var test = Test("bulk: { columns: [MD, GR] }", "      - { column: GR, between: [0, 300] }");
        var check = new ColumnValueEvaluator(0, (ValueAssertion)test.Assertions[0], 5).Start("dev:work-product-component--WellLog:l1", new BulkShape(["MD", "GR"], 2), null);
        check.Observe(Frame(["MD", "GR"], "[[1.0, 10.0], [2.0, 400.0]]"));

        var verdict = check.Finish();

        Assert.False(verdict.Passed);
        Assert.Equal("row 2: 400", verdict.Value);
    }

    [Fact]
    public void A_cell_asked_for_at_no_column_has_no_value()
    {
        using var row = JsonDocument.Parse("[1.0, 2.0]");
        Assert.Null(BulkEvaluator.Cell(row.RootElement, -1));
        Assert.Null(BulkEvaluator.Cell(row.RootElement, 2));
        Assert.Equal(2.0, BulkEvaluator.Cell(row.RootElement, 1)!.GetValue<double>());
    }

    /// <summary>
    /// A unique assertion keeps one entry per record it observes and a test observes at most its maxRecords, so it is held to
    /// the bound a distinct count holds its values to as long as no test may read more records than that bound.
    /// </summary>
    [Fact]
    public void A_unique_assertion_holds_no_more_entries_than_a_distinct_count_holds_values()
    {
        Assert.True(AssertionDefaults.MaxRecordsCeiling <= AggregateEvaluator.MaxDistinct,
            $"maxRecords may reach {AssertionDefaults.MaxRecordsCeiling}, past the {AggregateEvaluator.MaxDistinct} values a distinct count keeps; bound the unique assertion's entries the same way.");
        var refused = Assert.Throws<FlowValidationException>(() => Test("maxRecords: 1000001", "      - { unique: [data.Name] }"));
        Assert.Contains("maxRecords must be between 1 and 1,000,000", refused.Message, StringComparison.Ordinal);
    }
}
