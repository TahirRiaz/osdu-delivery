using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a render judges a mapping's assertions (osdu/docs/reference/flow/mapping-assertions.md): the record stage on the
/// record as it will be sent, the incoming stage on what the row gives, what each failure does to the document, and what
/// the findings carry.
/// </summary>
public sealed class MappingAssertionRenderTests
{
    [Fact]
    public void A_value_that_meets_its_assertion_is_written_and_counted()
    {
        var result = Render("""
            Count:
              $from: count
              $assert:
                - between: [0, 250]
            """, Row(("count", "120")));

        Assert.Equal(120, result.Document["data"]!["Count"]!.GetValue<long>());
        var findings = result.Assertions!;
        Assert.Equal("Thing@1.0.0", findings.Mapping);
        Assert.Equal(1, findings.Checked);
        Assert.Equal(0, findings.Failed);
        Assert.False(findings.Holds);
        Assert.False(result.IsHeld);
    }

    [Fact]
    public void A_record_that_fails_a_holding_assertion_keeps_its_document_and_the_failure()
    {
        var result = Render("""
            Count:
              $from: count
              $assert:
                - between: [0, 250]
                  name: count-range
            """, Row(("count", "312")));

        // The document is the one a release would send: the check before sending holds it, not the render.
        Assert.False(result.IsHeld);
        Assert.Equal(312, result.Document["data"]!["Count"]!.GetValue<long>());
        var findings = result.Assertions!;
        Assert.Equal(1, findings.Failed);
        Assert.Equal(1, findings.Held);
        Assert.True(findings.Holds);
        var failure = Assert.Single(findings.Failures);
        Assert.Equal(new AssertionFailure("data.Count", "data.Count", "count-range", "record", "hold", "is 312, outside 0 to 250", "312"), failure);
    }

    [Fact]
    public void A_reported_failure_is_recorded_and_changes_nothing_in_the_document()
    {
        var plain = Render("Count: { $from: count }", Row(("count", "312")));
        var reported = Render("""
            Count:
              $from: count
              $assert:
                - atMost: 250
                  onFail: report
            """, Row(("count", "312")));

        Assert.Equal(1, reported.Assertions!.Reported);
        Assert.False(reported.Assertions.Holds);
        Assert.Equal(plain.MetadataHash, reported.MetadataHash);
    }

    [Fact]
    public void An_omitted_value_is_left_out_of_the_document_it_failed_in()
    {
        var result = Render("""
            Count:
              $from: count
              $required: false
              $assert:
                - atMost: 250
                  onFail: omit
            """, Row(("count", "312")));

        Assert.Null(result.Document["data"]!["Count"]);
        Assert.Equal(1, result.Assertions!.Omitted);
        Assert.Equal("omit", Assert.Single(result.Assertions.Failures).OnFail);
        Assert.NotEqual(Render("Count: { $from: count }", Row(("count", "312"))).MetadataHash, result.MetadataHash);
    }

    [Fact]
    public void An_incoming_assertion_judges_the_rows_value_before_the_modifiers_change_it()
    {
        var result = Render("""
            Symbol:
              $from: symbol
              $modifiers: [trim]
              $assert:
                - stage: incoming
                  notMatches: '^\s|\s$'
                  onFail: report
            """, Row(("symbol", " GR ")));

        Assert.Equal("GR", result.Document["data"]!["Symbol"]!.GetValue<string>());
        var failure = Assert.Single(result.Assertions!.Failures);
        Assert.Equal("incoming", failure.Stage);
        Assert.Equal("dataset.symbol", failure.Path);
        Assert.Equal(" GR ", failure.Value);
    }

    [Fact]
    public void An_incoming_placeholder_left_out_leaves_the_property_out_whatever_its_modifiers_say()
    {
        var mapping = """
            Weight:
              $from: weight
              $required: false
              $modifiers:
                - number: { decimal: "," }
              $assert:
                - stage: incoming
                  notIn: ["-999", "-999,25"]
                  onFail: omit
            """;

        var placeholder = Render(mapping, Row(("weight", "-999,25")));
        Assert.Null(placeholder.Document["data"]!["Weight"]);
        Assert.False(placeholder.IsHeld);
        Assert.Equal(1, placeholder.Assertions!.Omitted);

        var weighed = Render(mapping, Row(("weight", "12,5")));
        Assert.Equal(12.5, weighed.Document["data"]!["Weight"]!.GetValue<double>());
        Assert.Equal(0, weighed.Assertions!.Failed);
    }

    [Fact]
    public void An_incoming_condition_reads_a_column_of_the_row_and_selects_when_the_assertion_is_judged()
    {
        var mapping = """
            Weight:
              $from: weight
              $assert:
                - stage: incoming
                  atMost: 100
                  where:
                    - column: weight_unit
                      equals: KG
            """;

        Assert.Equal(1, Render(mapping, Row(("weight", "150"), ("weight_unit", "KG"))).Assertions!.Failed);
        var pounds = Render(mapping, Row(("weight", "150"), ("weight_unit", "LB"))).Assertions!;
        Assert.Equal(0, pounds.Checked);
        Assert.Equal(0, pounds.Failed);
    }

    [Fact]
    public void An_item_assertion_judges_every_item_and_names_the_one_that_fails()
    {
        var result = Render(
            """
            Curves:
              $forEach: curves
              $item:
                CurveID:
                  $from: curve_id
                  $assert:
                    - matches: '^[A-Z]+$'
                      onFail: report
                    - stage: incoming
                      notEquals: DEPT
                      onFail: report
                TopDepth: { $from: top }
            """,
            Row(),
            ("curves", [Curve("GR", "1"), Curve("rhob", "2"), Curve("DEPT", "3")]));

        var failures = result.Assertions!.Failures;
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.Path == "data.Curves[1].CurveID" && f.Value == "rhob" && f.At == "data.Curves[].CurveID");
        Assert.Contains(failures, f => f.Path == "dataset.curves[2].curve_id" && f.Value == "DEPT" && f.Stage == "incoming");
        Assert.Equal(6, result.Assertions.Checked);
    }

    [Fact]
    public void A_where_field_inside_the_same_array_reads_the_same_item()
    {
        var result = Render(
            """
            Curves:
              $forEach: curves
              $item:
                CurveID:
                  $from: curve_id
                  $assert:
                    - equals: GR
                      onFail: report
                      where:
                        - field: data.Curves.TopDepth
                          greaterThan: 1
                TopDepth: { $from: top }
            """,
            Row(),
            ("curves", [Curve("SP", "1"), Curve("GR", "2"), Curve("RHOB", "3")]));

        // Only the curves deeper than 1 are judged: GR meets it, RHOB does not; SP is passed over.
        Assert.Equal(2, result.Assertions!.Checked);
        Assert.Equal("data.Curves[2].CurveID", Assert.Single(result.Assertions.Failures).Path);
    }

    [Fact]
    public void A_where_field_outside_the_array_reads_the_record()
    {
        var mapping = """
            Symbol: { $from: symbol, $required: false }
            Curves:
              $forEach: curves
              $item:
                CurveID:
                  $from: curve_id
                  $assert:
                    - equals: GR
                      onFail: report
                      where:
                        - field: data.Symbol
                          equals: LOG
            """;

        Assert.Equal(2, Render(mapping, Row(("symbol", "LOG")), ("curves", [Curve("SP", "1"), Curve("GR", "2")])).Assertions!.Checked);
        Assert.Equal(0, Render(mapping, Row(("symbol", "MUD")), ("curves", [Curve("SP", "1"), Curve("GR", "2")])).Assertions!.Checked);
    }

    [Fact]
    public void An_omitted_item_property_leaves_out_an_item_left_with_nothing()
    {
        var result = Render(
            """
            Curves:
              $forEach: curves
              $required: false
              $item:
                CurveID:
                  $from: curve_id
                  $required: false
                  $assert:
                    - notEquals: DEPT
                      onFail: omit
            """,
            Row(),
            ("curves", [Curve("GR", "1"), Curve("DEPT", "2"), Curve("SP", "3")]));

        var curves = result.Document["data"]!["Curves"]!.AsArray();
        Assert.Equal(["GR", "SP"], curves.Select(c => c!["CurveID"]!.GetValue<string>()));
        Assert.Equal(1, result.Assertions!.Omitted);
    }

    [Fact]
    public void An_omitted_item_property_keeps_the_rest_of_its_item()
    {
        var result = Render(
            """
            Curves:
              $forEach: curves
              $item:
                CurveID:
                  $from: curve_id
                  $required: false
                  $assert:
                    - notEquals: DEPT
                      onFail: omit
                TopDepth: { $from: top }
            """,
            Row(),
            ("curves", [Curve("GR", "1"), Curve("DEPT", "2")]));

        var curves = result.Document["data"]!["Curves"]!.AsArray();
        Assert.Equal(2, curves.Count);
        Assert.Null(curves[1]!["CurveID"]);
        Assert.Equal("2", curves[1]!["TopDepth"]!.ToJsonString());
    }

    [Fact]
    public void A_list_is_judged_value_by_value_and_whole_by_the_conditions_that_judge_a_list()
    {
        var mapping = """
            Aliases:
              $from: alias
              $modifiers:
                - split: { separator: ";", part: 1 }
              $assert:
                - startsWith: W
                  onFail: report
                - length: { atLeast: 2 }
                  onFail: report
            """;

        var result = Render(mapping, Row(("alias", "X-1;Y")));
        Assert.Equal(["X-1"], result.Document["data"]!["Aliases"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Contains(result.Assertions!.Failures, f => f.Path == "data.Aliases[0]" && f.Assertion == "startsWith \"W\"");
        Assert.Contains(result.Assertions.Failures, f => f.Path == "data.Aliases" && f.Assertion == "has length atLeast 2" && f.Message == "has length 1");
    }

    [Fact]
    public void An_omitted_value_of_a_list_leaves_the_list_written_empty()
    {
        var result = Render("""
            Aliases:
              $from: alias
              $required: false
              $assert:
                - startsWith: W
                  onFail: omit
            """, Row(("alias", "X-1")));

        Assert.Empty(result.Document["data"]!["Aliases"]!.AsArray());
        Assert.Equal(1, result.Assertions!.Omitted);
    }

    [Fact]
    public void Any_value_holds_when_one_of_the_values_meets_it()
    {
        var mapping = """
            Curves:
              $forEach: curves
              $item:
                CurveID:
                  $from: curve_id
                  $assert:
                    - equals: GR
                      values: any
                      name: a gamma ray curve
            """;

        Assert.Equal(0, Render(mapping, Row(), ("curves", [Curve("SP", "1"), Curve("GR", "2")])).Assertions!.Failed);
        var none = Render(mapping, Row(), ("curves", [Curve("SP", "1"), Curve("RHOB", "2")])).Assertions!;
        Assert.Equal(1, none.Checked);
        Assert.Contains("none of its 2 values meets it", Assert.Single(none.Failures).Message);
    }

    [Fact]
    public void A_value_the_record_does_not_carry_is_judged_only_by_exists_and_empty()
    {
        var mapping = """
            Symbol:
              $from: symbol
              $required: false
              $assert:
                - matches: '^[A-Z]+$'
                - exists: true
                  onFail: report
            """;

        var absent = Render(mapping, Row()).Assertions!;
        Assert.Equal(1, absent.Checked);
        var failure = Assert.Single(absent.Failures);
        Assert.Equal("has no value", failure.Message);
        Assert.Equal("(no value)", failure.Value);
        Assert.False(absent.Holds);
    }

    [Fact]
    public void An_array_is_judged_as_a_whole()
    {
        var mapping = """
            Curves:
              $forEach: curves
              $assert:
                - length: { atLeast: 2 }
                  onFail: report
              $item:
                CurveID: { $from: curve_id }
            """;

        Assert.Equal(1, Render(mapping, Row(), ("curves", [Curve("GR", "1")])).Assertions!.Failed);
        Assert.Equal(0, Render(mapping, Row(), ("curves", [Curve("GR", "1"), Curve("SP", "2")])).Assertions!.Failed);
    }

    [Fact]
    public void A_coalesce_node_asserts_on_the_value_its_alternatives_gave()
    {
        var result = Render("""
            Symbol:
              $coalesce:
                - $from: symbol
                - $from: fallback
              $assert:
                - in: [GR, SP]
                  onFail: report
            """, Row(("fallback", "CALI")));

        Assert.Equal("CALI", Assert.Single(result.Assertions!.Failures).Value);
    }

    [Fact]
    public void A_date_compares_as_an_instant_and_unicode_text_as_the_assertion_flows_compare_it()
    {
        var result = Render("""
            When:
              $from: when
              $modifiers: [date]
              $assert:
                - greaterThan: "2020-01-01T00:00:00+02:00"
                  onFail: report
            Symbol:
              $from: symbol
              $assert:
                - matches: '^[\p{L}]+$'
                  onFail: report
                - length: 6
                  onFail: report
            """, Row(("when", "2019-12-31T23:30:00Z"), ("symbol", "Dražen")));

        // 23:30 UTC on the 31st is after midnight at +02:00, so the date meets it; the name is letters and six characters long.
        Assert.Equal(0, result.Assertions!.Failed);
        Assert.Equal(3, result.Assertions.Checked);
    }

    [Fact]
    public void A_pattern_that_runs_out_of_time_fails_its_assertion_rather_than_hang()
    {
        var result = Render("""
            Symbol:
              $from: symbol
              $assert:
                - matches: '^(a+)+$'
                  onFail: report
            """, Row(("symbol", new string('a', 40) + "!")));

        Assert.Contains("took longer than 1s to match", Assert.Single(result.Assertions!.Failures).Message);
    }

    [Fact]
    public void A_record_lists_fifty_failures_and_counts_every_one()
    {
        var curves = Enumerable.Range(0, 80).Select(i => Curve("bad" + i, "1")).ToList();
        var result = Render(
            """
            Curves:
              $forEach: curves
              $item:
                CurveID:
                  $from: curve_id
                  $assert:
                    - matches: '^[A-Z]+$'
                      onFail: report
            """,
            Row(),
            ("curves", curves));

        Assert.Equal(80, result.Assertions!.Failed);
        Assert.Equal(AssertionFindings.MaxListed, result.Assertions.Failures.Count);
        var shortened = result.Assertions.ToJson(10);
        Assert.Equal(10, shortened["failures"]!.AsArray().Count);
        Assert.True(shortened["shortened"]!.GetValue<bool>());
    }

    [Fact]
    public void A_mapping_without_assertions_carries_no_findings()
        => Assert.Null(Render("Count: { $from: count }", Row(("count", "1"))).Assertions);

    [Fact]
    public void Findings_read_back_as_they_were_written()
    {
        var findings = Render("""
            Count:
              $from: count
              $assert:
                - atMost: 250
                  onFail: report
            """, Row(("count", "312"))).Assertions!;

        Assert.Equal(findings with { Failures = [] }, AssertionFindings.FromText(findings.ToText())! with { Failures = [] });
        Assert.Equal(findings.Failures, AssertionFindings.FromText(findings.ToText())!.Failures);
        Assert.Null(AssertionFindings.FromText("not json"));
        Assert.Null(AssertionFindings.FromText(null));
    }

    [Fact]
    public void A_secret_in_a_value_is_redacted_before_it_is_kept()
    {
        var result = Render("""
            Symbol:
              $from: symbol
              $assert:
                - equals: GR
                  onFail: report
            """, Row(("symbol", "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl")));

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", Assert.Single(result.Assertions!.Failures).Value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_record_shape_says_what_each_assertion_does()
    {
        var mapping = TestSchema.Mapping("""
            Count:
              $from: count
              $assert:
                - between: [0, 250]
                  name: count-range
            Weight:
              $from: weight
              $required: false
              $assert:
                - stage: incoming
                  notEquals: "-999"
                  onFail: omit
            """);
        var shape = MappingRenderer.Shape(mapping, TestSchema.Build(), new Dictionary<string, string> { ["dataPartition"] = "dev" });

        Assert.Contains("osdu.data.Count: asserts between 0 and 250 of the value the record carries (count-range); a record that fails it is held, its document kept until a release accepts it", shape.Notes);
        Assert.Contains("osdu.data.Weight: asserts notEquals \"-999\" of the value the row gives, before its modifiers; a value that fails it is left out, and the failure recorded", shape.Notes);
    }

    [Theory]
    [InlineData("""
        Count:
          $from: count
          $assert:
            - matches: '^[0-9]+$'
        """, "'data.Count' is an integer in test:wks:work-product-component--Thing:1.0.0")]
    [InlineData("""
        Count:
          $from: count
          $assert:
            - equals: many
        """, "'data.Count' is an integer, and \"many\" is not a number.")]
    [InlineData("""
        IsRegular:
          $from: flag
          $assert:
            - atLeast: 1
        """, "a boolean has no order")]
    [InlineData("""
        When:
          $from: when
          $modifiers: [date]
          $assert:
            - atLeast: 20200101
        """, "is a date-time, and 20200101 is a number")]
    [InlineData("""
        Curves:
          $forEach: curves
          $assert:
            - equals: GR
          $item:
            CurveID: { $from: curve_id }
        """, "an assertion on the list asks whether it is there (exists), whether it is empty (empty) or how many items it holds (length), and equals on a property of its items is written beside that property")]
    [InlineData("""
        Nested:
          Inner:
            $from: inner
            $assert:
              - equals: x
                where:
                  - field: data.Nme
                    equals: x
        """, "where: 'data.Nme' is not a property of test:wks:work-product-component--Thing:1.0.0")]
    public void The_preflight_refuses_an_assertion_that_does_not_fit_its_property(string data, string expected)
    {
        var issues = Preflight.Check(TestSchema.Mapping(data), TestSchema.Build(), TestSchema.References(), TestSchema.Context(), null);
        Assert.Contains(issues, issue => issue.Severity == IssueSeverity.Error && issue.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void The_preflight_checks_the_columns_an_incoming_condition_reads()
    {
        var mapping = TestSchema.Mapping("""
            Weight:
              $from: weight
              $assert:
                - stage: incoming
                  atMost: 100
                  where:
                    - column: weight_unit
                      equals: KG
            """);
        var columns = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [SourceDatasets.Record] = new HashSet<string>(["name", "depth", "weight"], StringComparer.OrdinalIgnoreCase),
        };

        var issues = Preflight.Check(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context(), columns);
        Assert.Contains(issues, i => i.Message.Contains("reads dataset.weight_unit, which the record table does not hold", StringComparison.Ordinal));
    }

    [Fact]
    public void A_mapping_whose_assertions_fit_passes_the_preflight()
    {
        var mapping = TestSchema.Mapping("""
            Count:
              $from: count
              $assert:
                - between: [0, 250]
            Aliases:
              $from: alias
              $assert:
                - startsWith: W
                - contains: W-1
                - length: { atMost: 5 }
            Curves:
              $forEach: curves
              $assert:
                - length: { atLeast: 1 }
              $item:
                CurveID:
                  $from: curve_id
                  $assert:
                    - in: [GR, SP]
                      where:
                        - field: data.Curves.TopDepth
                          atLeast: 0
                TopDepth: { $from: top }
            """);

        var errors = Preflight.Check(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context(), null).Where(i => i.Severity == IssueSeverity.Error);
        Assert.Empty(errors);
    }

    private static RenderResult Render(string data, Dictionary<string, string?> row, params (string Child, List<SourceRow> Rows)[] children)
    {
        var mapping = TestSchema.Mapping(data);
        var renderer = new MappingRenderer(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context());
        var scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (child, rows) in children)
        {
            scopes[child] = rows;
        }

        return renderer.Render(new SourceRecord { Row = SourceRow.FromStrings(row), Scopes = scopes });
    }

    private static Dictionary<string, string?> Row(params (string Column, string? Value)[] values)
    {
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "thing-1", ["depth"] = "12.5" };
        foreach (var (column, value) in values)
        {
            row[column] = value;
        }

        return row;
    }

    private static SourceRow Curve(string id, string top)
        => SourceRow.FromStrings(new Dictionary<string, string?> { ["curve_id"] = id, ["top"] = top });
}
