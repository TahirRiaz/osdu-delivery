using SqlFlow.Delivery.Search;
using Xunit;

namespace SqlFlow.Delivery.Search.Tests;

/// <summary>
/// The ranges a dimension build pages an aggregation with, the filters a dimension member hands to a search, and the field
/// an aggregation names. The rules are the service's: <c>aggregateBy</c> takes the unanalysed field and the
/// <c>nested(path, field)</c> form (<c>AggregationParserUtil</c>), Elasticsearch parses a phrase compared with a number, a
/// boolean or a date as that type, and the service rewrites what follows <c>AND</c>, <c>OR</c> or <c>NOT</c> inside a
/// nested query.
/// </summary>
public class OsduRangeAndFilterTests
{
    private static readonly OsduField Name = OsduField.Text("data.FacilityName");
    private static readonly OsduField Mnemonic = OsduField.Text("data.Curves.Mnemonic", "data.Curves");
    private static readonly OsduField Kind = OsduField.Keyword("kind");
    private static readonly OsduField Depth = OsduField.Number("data.TotalDepth");
    private static readonly OsduField Active = OsduField.Boolean("data.IsActive");
    private static readonly OsduField Spud = OsduField.Date("data.SpudDate");

    [Fact]
    public void Text_is_aggregated_by_its_keyword_sub_field_and_everything_else_by_itself()
    {
        Assert.Equal("data.FacilityName.keyword", Name.AggregateBy);
        Assert.Equal("kind", Kind.AggregateBy);
        Assert.Equal("data.TotalDepth", Depth.AggregateBy);
        Assert.Equal("data.IsActive", Active.AggregateBy);
        Assert.Equal("data.SpudDate", Spud.AggregateBy);
    }

    [Fact]
    public void A_property_of_a_nested_array_is_aggregated_through_the_services_nested_form()
    {
        // AggregationParserUtil reads nested(<path>, <field>) and aggregates <path>.<field> over the array's objects.
        Assert.Equal("nested(data.Curves, Mnemonic.keyword)", Mnemonic.AggregateBy);
        Assert.Equal("nested(data.Markers, MarkerMeasuredDepth)", OsduField.Number("data.Markers.MarkerMeasuredDepth", "data.Markers").AggregateBy);
    }

    [Fact]
    public void A_range_is_half_open_so_two_ranges_that_meet_hold_every_value_once()
    {
        Assert.Equal("data.FacilityName.keyword:[\"A\" TO \"M\"}", OsduQuery.Range(Name, "A", "M").Text);
        Assert.Equal("data.FacilityName.keyword:[\"M\" TO *]", OsduQuery.Range(Name, "M", null).Text);
        Assert.Equal("data.FacilityName.keyword:[* TO \"M\"}", OsduQuery.Range(Name, null, "M").Text);
    }

    [Fact]
    public void A_range_open_at_both_ends_is_refused_because_it_asks_for_everything()
        => Assert.Contains("open at both ends", Assert.Throws<OsduQueryException>(() => OsduQuery.Range(Name, null, null)).Message, StringComparison.Ordinal);

    [Fact]
    public void A_range_bound_is_escaped_as_a_phrase_is()
        => Assert.Equal("data.FacilityName.keyword:[\"a\\\"b\" TO \"c\\\\\"}", OsduQuery.Range(Name, "a\"b", "c\\").Text);

    [Fact]
    public void A_range_bound_of_text_may_be_any_position_in_the_order_even_the_null_text_or_a_long_one()
    {
        // A bound is where a slice begins or ends, not a value a record must hold.
        Assert.Contains("\"null\"", OsduQuery.Range(Name, "null", null).Text, StringComparison.Ordinal);
        Assert.Contains(new string('x', 300), OsduQuery.Range(Name, new string('x', 300), null).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_range_inside_a_nested_array_is_carried_by_the_nested_form()
        => Assert.Equal(
            "nested(data.Curves, (Mnemonic.keyword:[\"DT\" TO \"GR\"}))",
            OsduQuery.Range(Mnemonic, "DT", "GR").Text);

    [Theory]
    [InlineData("A OR B:1")]
    [InlineData("WB (A")]
    public void A_range_bound_the_service_would_rewrite_inside_a_nested_array_is_refused(string bound)
        => Assert.Throws<OsduQueryException>(() => OsduQuery.Range(Mnemonic, bound, null));

    [Fact]
    public void A_range_bound_holding_the_nested_marker_is_refused_anywhere()
        => Assert.Throws<OsduQueryException>(() => OsduQuery.Range(Name, "x nested(y", null));

    [Fact]
    public void A_range_of_numbers_takes_numbers_and_refuses_anything_else()
    {
        Assert.Equal("data.TotalDepth:[\"-12.5\" TO \"1E+20\"}", OsduQuery.Range(Depth, "-12.5", "1E+20").Text);
        Assert.Throws<OsduQueryException>(() => OsduQuery.Range(Depth, "12 m", null));
        Assert.Throws<OsduQueryException>(() => OsduQuery.Range(Depth, "NaN", null));
        Assert.Throws<OsduQueryException>(() => OsduQuery.Range(Depth, " 12", null));
        Assert.Throws<OsduQueryException>(() => OsduQuery.Range(Depth, "1,000", null));
    }

    [Fact]
    public void Values_of_a_number_a_boolean_or_a_date_are_quoted_phrases_the_index_parses_as_the_type()
    {
        Assert.Equal("data.TotalDepth:\"3500.5\"", OsduQuery.Equal(Depth, "3500.5").Text);
        Assert.Equal("data.IsActive:\"true\"", OsduQuery.Equal(Active, "true").Text);
        Assert.Equal("data.SpudDate:\"2020-01-02T00:00:00.000Z\"", OsduQuery.Equal(Spud, "2020-01-02T00:00:00.000Z").Text);
    }

    [Fact]
    public void A_value_that_is_not_one_of_its_fields_type_is_refused_rather_than_failing_the_whole_query()
    {
        Assert.Throws<OsduQueryException>(() => OsduQuery.Equal(Depth, "deep"));
        Assert.Throws<OsduQueryException>(() => OsduQuery.Equal(Active, "yes"));
        Assert.Throws<OsduQueryException>(() => OsduQuery.Equal(Active, "True"));
        Assert.Throws<OsduQueryException>(() => OsduQuery.Equal(Spud, "the day we spudded"));
    }

    [Fact]
    public void Only_text_can_be_compared_regardless_of_case()
    {
        Assert.Throws<OsduQueryException>(() => OsduQuery.Equal(Depth, "1", caseInsensitive: true));
        Assert.Throws<OsduQueryException>(() => OsduQuery.Equal(Active, "true", caseInsensitive: true));
    }

    [Fact]
    public void A_filter_of_several_values_outside_a_nested_array_is_one_grouped_comparison()
        => Assert.Equal(
            "data.FacilityName.keyword:(\"Wellbore A/1-A-1\" OR \"wellbore a/1-a-1\")",
            OsduQuery.AnyOf(Name, ["Wellbore A/1-A-1", "wellbore a/1-a-1"]).Text);

    [Fact]
    public void A_filter_of_one_value_is_that_value_compared_exactly()
        => Assert.Equal("data.FacilityName.keyword:\"A\"", OsduQuery.AnyOf(Name, ["A"]).Text);

    [Fact]
    public void A_filter_asks_a_value_given_twice_once()
        => Assert.Equal("kind:(\"a:b:c:1.0.0\" OR \"a:b:c:2.0.0\")", OsduQuery.AnyOf(Kind, ["a:b:c:1.0.0", "a:b:c:2.0.0", "a:b:c:1.0.0"]).Text);

    [Fact]
    public void A_filter_inside_a_nested_array_ors_one_nested_query_per_value()
    {
        // Inside nested(...) the service rewrites 'OR x:' as the next property, and an OSDU id holds colons, so each value
        // is a nested query of its own and the OR stands outside, where nothing is rewritten.
        Assert.Equal(
            "(nested(data.Curves, (Mnemonic.keyword:\"osdu:reference-data--X:GR:\"))) OR (nested(data.Curves, (Mnemonic.keyword:\"gr\")))",
            OsduQuery.AnyOf(Mnemonic, ["osdu:reference-data--X:GR:", "gr"]).Text);
    }

    [Fact]
    public void A_filter_of_no_values_is_refused_rather_than_matching_everything()
        => Assert.Throws<OsduQueryException>(() => OsduQuery.AnyOf(Name, []));

    [Fact]
    public void A_filter_holding_a_value_no_query_can_carry_is_refused_naming_it()
    {
        var refused = Assert.Throws<OsduQueryException>(() => OsduQuery.AnyOf(Name, ["fine", "null"]));
        Assert.Contains("null_value", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_are_filtered_as_numbers()
        => Assert.Equal("data.TotalDepth:(\"1\" OR \"2.5\")", OsduQuery.AnyOf(Depth, ["1", "2.5"]).Text);

    [Theory]
    [InlineData("fine", null)]
    [InlineData("null", "null_value")]
    [InlineData("two\nlines", "control character")]
    [InlineData("", "empty")]
    [InlineData("a nested(b", "nested")]
    public void Whether_a_value_can_be_asked_for_is_answered_without_throwing(string value, string? problem)
    {
        var found = OsduQuery.EqualProblem(Name, value);
        if (problem is null)
        {
            Assert.Null(found);
        }
        else
        {
            Assert.NotNull(found);
            Assert.Contains(problem, found, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_value_the_nested_rewriting_would_alter_is_a_problem_only_inside_a_nested_array()
    {
        Assert.Null(OsduQuery.EqualProblem(Name, "BRAND X:1"));
        Assert.NotNull(OsduQuery.EqualProblem(Mnemonic, "BRAND X:1"));
    }

    [Fact]
    public void A_text_value_longer_than_the_keyword_holds_is_a_problem_but_a_long_keyword_is_not()
    {
        Assert.NotNull(OsduQuery.EqualProblem(Name, new string('x', OsduQuery.KeywordIgnoreAbove + 1)));
        Assert.Null(OsduQuery.EqualProblem(Kind, new string('x', OsduQuery.KeywordIgnoreAbove + 1)));
    }

    [Fact]
    public void A_shape_the_indexer_has_no_name_for_is_refused()
        => Assert.Throws<OsduQueryException>(() => OsduField.Of("data.X", (OsduFieldIndex)42));
}
