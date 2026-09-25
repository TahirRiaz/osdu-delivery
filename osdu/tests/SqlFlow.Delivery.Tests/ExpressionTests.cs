using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Expressions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The mapping's expression language (osdu/docs/mapping-templates.md, "Expressions"): what it reads, how it compares and
/// computes, each of its functions, the errors it refuses a mapping with, and how <c>$expr</c>, <c>$when</c> and
/// <c>$where</c> render.
/// </summary>
public class ExpressionTests
{
    private static MappingExpression Parse(string text, string? child = null)
        => MappingExpression.TryParse(text, child, out var problem) ?? throw new Xunit.Sdk.XunitException($"'{text}' was refused: {problem}");

    private static string Refusal(string text, string? child = null)
    {
        Assert.Null(MappingExpression.TryParse(text, child, out var problem));
        return problem!;
    }

    private static ExpressionInput Input(IReadOnlyDictionary<string, object?> row, string? region = null)
        => new(column => row.GetValueOrDefault(column.Column), parameter => parameter switch
        {
            "dataPartition" => "dev",
            "region" => region,
            _ => null,
        });

    private static object? Eval(string text, params (string Column, object? Value)[] row)
    {
        var values = row.ToDictionary(r => r.Column, r => r.Value, StringComparer.OrdinalIgnoreCase);
        Assert.True(Parse(text).TryEvaluate(Input(values), out var value, out var problem), problem);
        return value;
    }

    private static string EvalProblem(string text, params (string Column, object? Value)[] row)
    {
        var values = row.ToDictionary(r => r.Column, r => r.Value, StringComparer.OrdinalIgnoreCase);
        Assert.False(Parse(text).TryEvaluate(Input(values), out _, out var problem));
        return problem!;
    }

    private static bool Test(string text, params (string Column, object? Value)[] row)
    {
        var values = row.ToDictionary(r => r.Column, r => r.Value, StringComparer.OrdinalIgnoreCase);
        Assert.True(Parse(text).TryTest(Input(values), out var holds, out var problem), problem);
        return holds;
    }

    [Fact]
    public void Coalesce_takes_the_first_value_that_is_there()
    {
        Assert.Equal("x", Eval("coalesce(a, b, \"x\")", ("a", null), ("b", "   ")));
        Assert.Equal("A", Eval("coalesce(a, b, \"x\")", ("a", "A"), ("b", "B")));
        Assert.Equal("B", Eval("coalesce(a, b)", ("a", ""), ("b", "B")));
        Assert.Null(Eval("coalesce(a, b)", ("a", null), ("b", null)));
        Assert.Equal(12m, Eval("coalesce(a, 12)", ("a", null)));
    }

    [Fact]
    public void Nullif_drops_a_placeholder_whatever_type_the_row_holds_it_as()
    {
        Assert.Null(Eval("nullif(depth, -999)", ("depth", "-999")));
        Assert.Null(Eval("nullif(depth, -999)", ("depth", -999L)));
        Assert.Null(Eval("nullif(depth, -999)", ("depth", -999.0d)));
        Assert.Equal(12m, Eval("nullif(depth, -999)", ("depth", 12)));
        Assert.Null(Eval("nullif(status, \"n/a\")", ("status", " N/A ")));
    }

    [Fact]
    public void Text_compares_ignoring_case_and_the_spaces_around_it()
    {
        Assert.True(Test("status = \"final\"", ("status", " FINAL ")));
        Assert.False(Test("status != \"final\"", ("status", "Final")));
        Assert.True(Test("status != \"final\"", ("status", null)));
        Assert.False(Test("status = \"final\"", ("status", null)));
        Assert.True(Test("status < \"b\"", ("status", "A")));
    }

    [Fact]
    public void A_number_compares_with_a_number_or_text_that_is_one()
    {
        Assert.True(Test("depth > 100", ("depth", "150")));
        Assert.True(Test("depth > 100", ("depth", 150L)));
        Assert.True(Test("depth = 100", ("depth", "100.0")));
        Assert.False(Test("depth = 100", ("depth", "abc")));
        Assert.False(Test("depth > 100", ("depth", null)));
        Assert.Contains("depth is 'abc', which is not a number, and '>' compares it with the number 100", EvalProblem("depth > 100", ("depth", "abc")), StringComparison.Ordinal);
        Assert.Contains("is true or false, which '<' cannot order", EvalProblem("flag < 1", ("flag", true)), StringComparison.Ordinal);
    }

    [Fact]
    public void Dates_and_booleans_compare_as_what_they_are()
    {
        var spud = new DateTimeOffset(2021, 3, 4, 0, 0, 0, TimeSpan.Zero);
        Assert.True(Test("spud > \"2020-01-01\"", ("spud", spud)));
        Assert.True(Test("spud = \"2021-03-04\"", ("spud", new DateOnly(2021, 3, 4))));
        Assert.Contains("'yesterday', which is not an ISO 8601 date", EvalProblem("spud > \"yesterday\"", ("spud", spud)), StringComparison.Ordinal);

        Assert.True(Test("active = true", ("active", true)));
        Assert.True(Test("active = \"TRUE\"", ("active", true)));
        Assert.Equal("Y", Eval("iif(active, \"Y\", \"N\")", ("active", true)));
        Assert.Equal("N", Eval("iif(active, \"Y\", \"N\")", ("active", null)));
        Assert.Contains("flag is 'Y', which is not true or false; compare it, such as flag = 'Y'", EvalProblem("iif(flag, 1, 2)", ("flag", "Y")), StringComparison.Ordinal);
    }

    [Fact]
    public void And_or_not_and_in_decide_conditions()
    {
        Assert.True(Test("status in [\"A\", \"B\"] and not empty(name)", ("status", "b"), ("name", "x")));
        Assert.False(Test("status not in [\"A\", \"B\"]", ("status", "a")));
        Assert.True(Test("status = \"C\" or depth > 1", ("status", "A"), ("depth", 2)));
        Assert.True(Test("not (status = \"A\")", ("status", "B")));

        // The right side is read only when the left does not decide, so a value it cannot test does not matter then.
        Assert.False(Test("empty(depth) and depth > 1", ("depth", "abc")));
        Assert.True(Test("COALESCE(a, b) = 'x' AND NOT EMPTY(c)", ("a", null), ("b", "X"), ("c", "1")));
    }

    [Fact]
    public void Arithmetic_is_exact_on_decimals_and_gives_no_value_for_no_value()
    {
        Assert.Equal(30.48m, Eval("depth * 0.3048", ("depth", 100L)));
        Assert.Equal(3.81m, Eval("depth * 0.3048", ("depth", "12.5")));
        Assert.Equal(-2m, Eval("-depth + 1", ("depth", 3)));
        Assert.Equal(0.5m, Eval("1 / 2"));
        Assert.Null(Eval("depth + 1", ("depth", null)));
        Assert.Equal(5m, Eval("coalesce(depth, 0) + 5", ("depth", null)));
        Assert.Contains("depth / 0 divides by zero", EvalProblem("depth / 0", ("depth", 3)), StringComparison.Ordinal);
        Assert.Contains("depth is 'deep', which is not a number, and '*' computes with numbers", EvalProblem("depth * 2", ("depth", "deep")), StringComparison.Ordinal);
    }

    [Fact]
    public void Join_writes_each_value_as_its_text_and_no_value_as_nothing()
    {
        Assert.Equal("A-", Eval("a & \"-\" & b", ("a", "A"), ("b", null)));
        Assert.Equal("run 3 of 12.5 true", Eval("\"run \" & run & \" of \" & depth & \" \" & ok", ("run", 3L), ("depth", 12.50m), ("ok", true)));
    }

    [Fact]
    public void The_text_functions_count_characters_as_a_person_reads_them()
    {
        Assert.Equal("bcd", Eval("substring(t, 2, 3)", ("t", "abcdef")));
        Assert.Equal("cdef", Eval("substring(t, 3)", ("t", "abcdef")));
        Assert.Equal(string.Empty, Eval("substring(t, 10, 2)", ("t", "abc")));
        Assert.Equal("ab", Eval("left(t, 2)", ("t", "abc")));
        Assert.Equal("bc", Eval("right(t, 2)", ("t", "abc")));
        Assert.Equal("abc", Eval("right(t, 9)", ("t", "abc")));
        Assert.Equal(3m, Eval("length(t)", ("t", "a\U0001F44Db")));
        Assert.Equal("\U0001F44D", Eval("right(t, 1)", ("t", "a\U0001F44D")));
        Assert.Equal("é", Eval("right(t, 1)", ("t", "café")));
        Assert.Contains("substring counts characters from 1", EvalProblem("substring(t, 0, 1)", ("t", "abc")), StringComparison.Ordinal);
        Assert.Contains("left takes a count of characters, 0 or more", EvalProblem("left(t, -1)", ("t", "abc")), StringComparison.Ordinal);
        Assert.Contains("left takes a count of characters, 0 or more", EvalProblem("left(t, 1.5)", ("t", "abc")), StringComparison.Ordinal);
        Assert.Null(Eval("upper(t)", ("t", null)));
    }

    [Fact]
    public void Text_is_cleaned_and_searched_ignoring_case()
    {
        Assert.Equal("NO 1/1", Eval("upper(trim(t))", ("t", "  no 1/1 ")));
        Assert.Equal("gr api", Eval("lower(t)", ("t", "GR API")));
        Assert.Equal("MD-MD", Eval("replace(t, \"dept\", \"MD\")", ("t", "DEPT-Dept")));
        Assert.True(Test("contains(t, \"gamma\")", ("t", "Natural GAMMA ray")));
        Assert.True(Test("startsWith(t, \"no \")", ("t", "NO 1/1")));
        Assert.True(Test("endsWith(t, \"-A\")", ("t", "NO 1/1-a")));
        Assert.False(Test("contains(t, \"x\")", ("t", null)));
        Assert.Contains("replace needs the text to find", EvalProblem("replace(t, f, \"x\")", ("t", "abc"), ("f", "")), StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_are_read_rounded_and_written_as_text()
    {
        Assert.Equal(12.5m, Eval("number(t)", ("t", " 12.5 ")));
        Assert.Null(Eval("number(t)", ("t", "")));
        Assert.Contains("t is '12,5', which is not a number", EvalProblem("number(t)", ("t", "12,5")), StringComparison.Ordinal);
        Assert.Equal(3m, Eval("round(n)", ("n", "2.5")));
        Assert.Equal(-3m, Eval("round(n)", ("n", -2.5m)));
        Assert.Equal(1.23m, Eval("round(n, 2)", ("n", 1.2345m)));
        Assert.Equal(2.5d, Eval("abs(n)", ("n", -2.5d)));
        Assert.Equal("12.5", Eval("text(n)", ("n", 12.50m)));
        Assert.Contains("round keeps 0 to 15 decimals", EvalProblem("round(n, 20)", ("n", 1)), StringComparison.Ordinal);
    }

    [Fact]
    public void Iif_chooses_and_reads_only_the_branch_it_takes()
    {
        Assert.Equal("deep", Eval("iif(depth > 1000, \"deep\", \"shallow\")", ("depth", 1500)));
        Assert.Equal("shallow", Eval("iif(depth > 1000, \"deep\", \"shallow\")", ("depth", null)));
        Assert.Equal(1m, Eval("iif(empty(d), 1, d * 2)", ("d", null)));
    }

    [Fact]
    public void An_expression_names_the_columns_and_parameters_it_reads()
    {
        var expression = Parse("coalesce(`curve-id`, $dataset.log_id) & $param.region & curve_id & `curve-id`", child: "curves");
        Assert.Equal([new DatasetColumn("curves", "curve-id"), new DatasetColumn(null, "log_id"), new DatasetColumn("curves", "curve_id")], expression.Columns);
        Assert.Equal(["region"], expression.Parameters);
        Assert.False(expression.IsCondition);
        Assert.True(Parse("a = 1").IsCondition);

        var row = new Dictionary<string, object?> { ["x"] = "1" };
        Assert.True(Parse("x & $param.region").TryEvaluate(Input(row, "north"), out var value, out _));
        Assert.Equal("1north", value);
    }

    [Theory]
    [InlineData("a == b", "an expression compares with a single '='")]
    [InlineData("a <> b", "writes 'is not equal' as '!='")]
    [InlineData("a = 1 && b = 2", "joins conditions with the word and")]
    [InlineData("a || b", "joins text with '&'")]
    [InlineData("a ?? b", "coalesce(a, b)")]
    [InlineData("a > 1 ? b : c", "chooses with iif(condition, value, other)")]
    [InlineData("!a", "negates a condition with the word not")]
    [InlineData("$trim(a)", "a function is written without '$', such as trim(text)")]
    [InlineData("trimm(a)", "Did you mean trim(text)?")]
    [InlineData("len(a)", "Did you mean length(text)?")]
    [InlineData("isnull(a, b)", "Did you mean coalesce(value, value, ...)?")]
    [InlineData("concat(a, b)", "joins text with '&'")]
    [InlineData("if(a = 1, b, c)", "chooses with iif(condition, value, other)")]
    [InlineData("frobnicate(a)", "is not a function of the expression language, which has iif, coalesce")]
    [InlineData("dataset.a", "a column of the dataset's own row is $dataset.a")]
    [InlineData("a.b", "reads a path")]
    [InlineData("$row.a", "reads nothing the expression language knows")]
    [InlineData("$dataset", "is not a reference")]
    [InlineData("\"abc", "is not closed with \"")]
    [InlineData("'a\\q'", "'\\q' at character 3 is not an escape")]
    [InlineData("a = 1 = 2", "chains comparisons")]
    [InlineData("upper(a) and b = 1", "the left side of 'and' is 'upper(a)', which gives text, not true or false")]
    [InlineData("a + \"x\"", "join text with '&'")]
    [InlineData("1a", "a column whose name starts with a digit is written in backticks")]
    [InlineData("and", "a column named and is written in backticks, `and`")]
    [InlineData("trim", "is a function, called as trim(text)")]
    [InlineData("a in []", "'in' looks for a value among at least one")]
    [InlineData("a in b", "where 'in' takes its list of values")]
    [InlineData("status is FINAL", "'is' (at character 8) is not an operator of the expression language")]
    [InlineData("coalesce(a)", "it takes at least 2 values: coalesce(value, value, ...)")]
    [InlineData("trim(a, b)", "it takes 1 value: trim(text)")]
    [InlineData("iif(a, b)", "it takes 3: iif(condition, value, other)")]
    [InlineData("iif(upper(a), b, c)", "the condition iif tests is 'upper(a)', which gives text")]
    [InlineData("upper(a = 1)", "a value given to upper is 'a = 1', a condition")]
    [InlineData("(a = 1) & b", "a side of '&' is 'a = 1', a condition")]
    [InlineData("a = ", "the expression ends after 'a =' where a value belongs")]
    [InlineData("upper(a", "the expression ends in the call of upper")]
    [InlineData("a b", "'b' at character 3 is not expected after a complete expression")]
    [InlineData("`bad name`", "is not a column name")]
    [InlineData("$param.`x-y`", "does not name a parameter")]
    [InlineData("a ; b", "';' at character 3 is not part of the expression language")]
    [InlineData("   ", "the expression is empty")]
    public void An_expression_it_cannot_read_is_refused_with_what_to_write(string text, string problem)
        => Assert.Contains(problem, Refusal(text), StringComparison.Ordinal);

    [Fact]
    public void An_expression_is_bounded_in_length_and_depth()
    {
        Assert.Contains("is at most 2000", Refusal("a & " + new string('b', 2000)), StringComparison.Ordinal);
        Assert.Contains("nests more than 48 levels deep", Refusal(new string('(', 60) + "a" + new string(')', 60)), StringComparison.Ordinal);
        Assert.Contains("nests more than 48 levels deep", Refusal(string.Concat(Enumerable.Repeat("not ", 60)) + "empty(a)"), StringComparison.Ordinal);
        Assert.Equal("a", Eval(new string('(', 20) + "a" + new string(')', 20), ("a", "a")));
    }

    [Fact]
    public void Expr_where_and_when_render_through_a_mapping()
    {
        var renderer = Renderer(
            """
            Symbol:
              $expr: coalesce(wb, name) & " (" & upper(flag) & ")"
            Depth:
              $expr: nullif(depth, -999) * 0.3048
            Count:
              $expr: length(name)
              $when: flag in ["REGULAR", "IRREGULAR"] and depth > 10
            Unit:
              $expr: lower(unit)
              $modifiers:
                - id: "{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:"
            Curves:
              $forEach: curves
              $where: curve_id != "RHOB"
              $item:
                CurveID: { $expr: lower(curve_id) & "-" & $dataset.name }
                TopDepth: { $from: top }
            """);

        var result = renderer.Render(Record());
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        var data = result.Document["data"]!;
        Assert.Equal("NO 1/1-A (REGULAR)", data["Symbol"]!.GetValue<string>());
        Assert.Equal(3.81, data["Depth"]!.GetValue<double>());
        Assert.Equal(6, data["Count"]!.GetValue<long>());
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", data["Unit"]!.GetValue<string>());
        var curves = data["Curves"]!.AsArray();
        Assert.Single(curves);
        Assert.Equal("gr-well-1", curves[0]!["CurveID"]!.GetValue<string>());

        var placeholder = renderer.Render(Record(depth: "-999", wellbore: null));
        Assert.True(placeholder.IsHeld);
        Assert.Contains(placeholder.Holds, h => h == "osdu.data.Depth: nullif(depth, -999) * 0.3048 gives no value, and the entry is required");
        Assert.Equal("well-1 (REGULAR)", placeholder.Document["data"]!["Symbol"]!.GetValue<string>());
        Assert.Null(placeholder.Document["data"]!["Count"]);

        var wrong = renderer.Render(Record(depth: "deep"));
        Assert.Contains(wrong.Holds, h => h == "osdu.data.Depth: nullif(depth, -999) * 0.3048: nullif(depth, -999) is 'deep', which is not a number, and '*' computes with numbers");
        Assert.Contains(wrong.Holds, h => h.StartsWith("osdu.data.Count: flag in [\"REGULAR\", \"IRREGULAR\"] and depth > 10: depth is 'deep', which is not a number", StringComparison.Ordinal));
    }

    [Fact]
    public void A_where_that_keeps_no_row_leaves_a_required_array_empty_and_holds()
    {
        var renderer = Renderer(
            """
            Curves:
              $forEach: curves
              $where: top > 5
              $item:
                CurveID: { $from: curve_id }
            """);
        var result = renderer.Render(Record());
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains("osdu.data.Curves: dataset.curves has no rows", StringComparison.Ordinal));
    }

    [Fact]
    public void A_shape_says_which_rows_an_array_keeps()
    {
        var mapping = TestSchema.Mapping(
            """
            Curves:
              $forEach: curves
              $where: curve_id != "RHOB"
              $item:
                CurveID: { $expr: upper(curve_id) }
            """);
        var shape = MappingRenderer.Shape(mapping, TestSchema.Build(), new Dictionary<string, string> { ["dataPartition"] = "dev" });
        Assert.Contains(shape.Notes, n => n.Contains("one item per row of dataset.curves where curve_id != \"RHOB\"", StringComparison.Ordinal));
        Assert.Equal("<string from upper(curve_id)>", shape.Document["data"]!["Curves"]![0]!["CurveID"]!.GetValue<string>());
    }

    [Fact]
    public void The_preflight_checks_the_columns_and_parameters_an_expression_reads()
    {
        var document = TestSchema.MappingDocument(
            """
            Symbol:
              $expr: coalesce(wb, nick) & $param.region
              $when: $param.region != "south" and not empty(flag)
            """).Replace("  dataPartition: { required: true }", "  dataPartition: { required: true }\n  region: {}", StringComparison.Ordinal);
        var mapping = new DeliveryDocumentLoader().ParseMapping(document, "thing.yaml");
        var columns = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [SourceDatasets.Record] = new HashSet<string>(["name", "depth", "wb", "flag"], StringComparer.OrdinalIgnoreCase),
        };

        var issues = Preflight.Check(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context(), columns);
        Assert.Contains(issues, i => i.Message.Contains("reads $param.region in 'coalesce(wb, nick) & $param.region', which the flow supplies no value for", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("reads dataset.nick, which the record table does not hold", StringComparison.Ordinal));
    }

    [Fact]
    public void A_condition_in_the_old_form_is_refused_with_the_expression_to_write()
    {
        Assert.Equal("log_status = \"FINAL\"", MappingMapper.OldCondition("log_status is FINAL"));
        Assert.Equal("flag != \"say \\\"hi\\\"\"", MappingMapper.OldCondition("flag is not 'say \"hi\"'"));
        Assert.Equal("empty($dataset.depth)", MappingMapper.OldCondition("$dataset.depth is empty"));
        Assert.Equal("not empty(flag)", MappingMapper.OldCondition("flag is not empty"));
        Assert.Null(MappingMapper.OldCondition("flag = \"is\""));
    }

    private static MappingRenderer Renderer(string data)
        => new(TestSchema.Mapping(data, baseData: "Name: { $from: name }"), TestSchema.Build(), TestSchema.References(), TestSchema.Context());

    private static SourceRecord Record(string? depth = "12.5", string? wellbore = "NO 1/1-A")
        => new()
        {
            Row = SourceRow.FromStrings(new Dictionary<string, string?>
            {
                ["name"] = "well-1",
                ["depth"] = depth,
                ["unit"] = "M",
                ["wb"] = wellbore,
                ["flag"] = "regular",
            }),
            Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase)
            {
                ["curves"] =
                [
                    SourceRow.FromStrings(new Dictionary<string, string?> { ["curve_id"] = "GR", ["top"] = "1" }),
                    SourceRow.FromStrings(new Dictionary<string, string?> { ["curve_id"] = "RHOB", ["top"] = "2" }),
                ],
            },
        };
}
