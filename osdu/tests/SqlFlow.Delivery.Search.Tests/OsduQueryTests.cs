using SqlFlow.Delivery.Search;
using Xunit;

namespace SqlFlow.Delivery.Search.Tests;

/// <summary>
/// The query strings the search service receives, checked against what the service does with them. Every expectation
/// here is a rule read out of the OSDU services rather than out of the API document: the escaping the service does not
/// do, the quote balance it refuses, the sub-field the indexer adds, the length it stops indexing at, and the form a
/// nested property is reached through.
/// </summary>
public class OsduQueryTests
{
    [Fact]
    public void An_exact_match_asks_the_keyword_sub_field_not_the_analysed_one()
    {
        // The indexer maps every string as text with a keyword sub-field. The analysed field matches a phrase anywhere
        // in the value, so a lookup that must find one record asks the sub-field, which holds the whole value.
        Assert.Equal(
            "data.FacilityName.keyword:\"NO 15/9-A-1\"",
            OsduQuery.Exact("data.FacilityName", "NO 15/9-A-1").Text);
    }

    [Fact]
    public void A_phrase_asks_the_analysed_field_and_says_so_by_naming_no_sub_field()
        => Assert.Equal("data.FacilityName:\"Chiting 06\"", OsduQuery.Phrase("data.FacilityName", "Chiting 06").Text);

    [Fact]
    public void A_value_is_quoted_so_its_words_are_one_value_and_not_an_or_of_words()
    {
        // The service sets defaultOperator(OR), so an unquoted NO 15/9-A-1 would ask for NO or 15/9-A-1.
        var text = OsduQuery.Exact("data.FacilityName", "NO 15/9-A-1").Text;
        Assert.StartsWith("data.FacilityName.keyword:\"", text, StringComparison.Ordinal);
        Assert.EndsWith("\"", text, StringComparison.Ordinal);
    }

    [Theory]
    // The service escapes nothing, and counts backslashes to decide whether a quote closes the phrase.
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\b", "\"a\\\\b\"")]
    // A trailing backslash is the case that breaks a naive escaper: unescaped, it would escape the closing quote and
    // leave the query unbalanced, which the service answers 400 to.
    [InlineData("ends\\", "\"ends\\\\\"")]
    [InlineData("both\"and\\", "\"both\\\"and\\\\\"")]
    [InlineData("plain", "\"plain\"")]
    public void A_value_is_escaped_so_the_phrase_always_closes(string value, string expected)
        => Assert.Equal(expected, LuceneText.Phrase(value));

    [Theory]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("ends\\")]
    [InlineData("\\\\")]
    [InlineData("\"")]
    public void Every_escaped_value_leaves_the_query_with_balanced_quotes(string value)
    {
        // The service refuses an unbalanced query outright (QueryParserUtil.hasBalancedQuotes), reading a quote as
        // escaped when an odd number of backslashes precedes it. This is that rule, applied to what we emit.
        var text = OsduQuery.Exact("data.FacilityName", value).Text;
        var open = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '"')
            {
                continue;
            }

            var backslashes = 0;
            for (var b = i - 1; b >= 0 && text[b] == '\\'; b--)
            {
                backslashes++;
            }

            if (backslashes % 2 == 0)
            {
                open ^= 1;
            }
        }

        Assert.Equal(0, open);
    }

    [Fact]
    public void A_value_longer_than_the_indexer_keeps_is_refused_rather_than_never_matching()
    {
        // ignore_above: 256 means the keyword sub-field never held it, so no query could find it.
        var refused = Assert.Throws<OsduQueryException>(
            () => OsduQuery.Exact("data.FacilityName", new string('x', OsduQuery.KeywordIgnoreAbove + 1)));

        Assert.Contains("257", refused.Message, StringComparison.Ordinal);
        Assert.Contains("ignore_above", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_exactly_at_the_limit_is_still_asked_for()
        => Assert.Contains(
            new string('x', OsduQuery.KeywordIgnoreAbove),
            OsduQuery.Exact("data.FacilityName", new string('x', OsduQuery.KeywordIgnoreAbove)).Text,
            StringComparison.Ordinal);

    [Fact]
    public void An_empty_value_is_refused_because_every_absent_property_would_answer_to_it()
        => Assert.Contains(
            "empty value",
            Assert.Throws<OsduQueryException>(() => OsduQuery.Exact("data.FacilityName", "")).Message,
            StringComparison.Ordinal);

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("tab\there")]
    [InlineData("null\0char")]
    public void A_control_character_is_refused_because_no_query_carries_it(string value)
        => Assert.Contains(
            "control character",
            Assert.Throws<OsduQueryException>(() => OsduQuery.Exact("data.FacilityName", value)).Message,
            StringComparison.Ordinal);

    [Fact]
    public void A_nested_property_is_reached_through_the_services_own_form()
    {
        // A wellbore's NameAliases is x-osdu-indexing: nested. A plain dotted path into it parses and matches nothing,
        // so the service's nested(path, (query)) form is what reaches the objects inside.
        Assert.Equal(
            "nested(data.NameAliases, (AliasName.keyword:\"WB-A\"))",
            OsduQuery.Nested("data.NameAliases", OsduQuery.Exact("AliasName", "WB-A")).Text);
    }

    [Fact]
    public void Several_criteria_are_grouped_so_the_default_operator_cannot_regroup_them()
        => Assert.Equal(
            "(data.FacilityName.keyword:\"A\") AND (data.FieldID.keyword:\"F\")",
            OsduQuery.All(
                OsduQuery.Exact("data.FacilityName", "A"),
                OsduQuery.Exact("data.FieldID", "F")).Text);

    [Fact]
    public void Alternatives_are_grouped_the_same_way()
        => Assert.Equal(
            "(data.FacilityName.keyword:\"A\") OR (data.FacilityName.keyword:\"B\")",
            OsduQuery.Any(
                OsduQuery.Exact("data.FacilityName", "A"),
                OsduQuery.Exact("data.FacilityName", "B")).Text);

    [Fact]
    public void One_criterion_is_not_wrapped_in_a_group_it_does_not_need()
        => Assert.Equal(
            "data.FacilityName.keyword:\"A\"",
            OsduQuery.All(OsduQuery.Exact("data.FacilityName", "A")).Text);

    [Fact]
    public void A_combination_of_no_criteria_is_refused_rather_than_matching_everything()
        => Assert.Contains(
            "asks nothing",
            Assert.Throws<OsduQueryException>(() => OsduQuery.All()).Message,
            StringComparison.Ordinal);

    [Fact]
    public void Case_insensitive_matching_asks_the_sub_field_a_platform_may_not_have()
    {
        // keywordLower exists only where the platform enables it, so a caller asks for it explicitly and never by
        // default: on a platform without it the sub-field is absent and the query matches nothing.
        Assert.Equal(
            "data.FacilityName.keywordLower:\"no 15/9-a-1\"",
            OsduQuery.Exact("data.FacilityName", "no 15/9-a-1", caseInsensitive: true).Text);
    }

    [Theory]
    [InlineData("data FacilityName")]
    [InlineData("data.Facility:Name")]
    [InlineData("data.(Facility)")]
    [InlineData("data..FacilityName")]
    [InlineData("1data.FacilityName")]
    [InlineData("")]
    public void A_path_that_would_be_read_as_syntax_is_refused_where_it_is_written(string path)
    {
        // A path is not quoted, so it cannot be escaped into safety the way a value can.
        Assert.Throws<OsduQueryException>(() => OsduQuery.Exact(path, "value"));
        Assert.False(OsduPath.IsPath(path));
    }

    [Theory]
    [InlineData("data.FacilityName")]
    [InlineData("AliasName")]
    [InlineData("data.NameAliases.AliasName")]
    [InlineData("_private.Field2")]
    public void A_path_that_can_be_written_plainly_is_accepted(string path)
    {
        Assert.True(OsduPath.IsPath(path));
        Assert.Equal(path, OsduPath.Of(path));
    }

    [Fact]
    public void A_path_longer_than_any_schema_is_refused()
        => Assert.Contains(
            "at most",
            Assert.Throws<OsduQueryException>(() => OsduPath.Of("d." + new string('x', OsduPath.MaxLength))).Message,
            StringComparison.Ordinal);

    [Fact]
    public void A_keyword_property_is_asked_for_itself_because_it_has_no_sub_field()
    {
        // A legacy ^srn link is mapped as a bare keyword (TypeMapper.getKeywordIndexerMapping): asking its keyword
        // sub-field would ask for a field that does not exist, and match nothing.
        Assert.Equal(
            "data.LegacyRef:\"srn:master-data/Well:1:\"",
            OsduQuery.Equal(OsduField.Keyword("data.LegacyRef"), "srn:master-data/Well:1:").Text);
    }

    [Fact]
    public void A_property_of_a_nested_array_is_asked_through_the_nested_form_by_its_path_inside_the_array()
        => Assert.Equal(
            "nested(data.NameAliases, (AliasName.keyword:\"WB-A\"))",
            OsduQuery.Equal(OsduField.Text("data.NameAliases.AliasName", "data.NameAliases"), "WB-A").Text);

    [Fact]
    public void A_nested_array_must_hold_the_property_it_is_named_for()
    {
        Assert.Throws<OsduQueryException>(() => OsduField.Text("data.FacilityName", "data.NameAliases"));
        Assert.Throws<OsduQueryException>(() => OsduField.Text("data.NameAliases", "data.NameAliases"));
        Assert.Throws<OsduQueryException>(() => OsduField.Text("data.NameAliasesX.AliasName", "data.NameAliases"));
        Assert.Equal("AliasName", OsduField.Text("data.NameAliases.AliasName", "data.NameAliases").QueryPath);
    }

    [Fact]
    public void The_text_the_indexer_stores_for_a_null_property_is_refused_rather_than_matching_every_record_without_a_value()
    {
        // The keyword sub-field is mapped with null_value: "null" (TypeMapper.getKeywordMap).
        var refused = Assert.Throws<OsduQueryException>(() => OsduQuery.Exact("data.FacilityName", "null"));
        Assert.Contains("null_value", refused.Message, StringComparison.Ordinal);

        // Case-insensitively, every spelling of it is the same indexed text.
        Assert.Throws<OsduQueryException>(() => OsduQuery.Exact("data.FacilityName", "NULL", caseInsensitive: true));

        // Case-sensitively, only the exact text is: NULL is a value like any other.
        Assert.Equal("data.FacilityName.keyword:\"NULL\"", OsduQuery.Exact("data.FacilityName", "NULL").Text);

        // A bare keyword has no null_value, so there it is a value like any other.
        Assert.Equal("data.LegacyRef:\"null\"", OsduQuery.Equal(OsduField.Keyword("data.LegacyRef"), "null").Text);
    }

    [Fact]
    public void A_keyword_cannot_be_compared_regardless_of_case_because_it_has_no_lowercase_sub_field()
        => Assert.Contains(
            OsduQuery.KeywordLowerSubField,
            Assert.Throws<OsduQueryException>(() => OsduQuery.Equal(OsduField.Keyword("data.LegacyRef"), "x", caseInsensitive: true)).Message,
            StringComparison.Ordinal);

    [Fact]
    public void A_keyword_value_no_term_could_hold_is_refused_by_its_bytes_not_its_characters()
    {
        // Three bytes of UTF-8 each: under the limit in characters, over it in bytes.
        var wide = new string('\u20AC', (OsduQuery.MaxTermBytes / 3) + 1);
        Assert.Throws<OsduQueryException>(() => OsduQuery.Equal(OsduField.Keyword("data.LegacyRef"), wide));

        var fits = new string('x', OsduQuery.MaxTermBytes);
        Assert.Contains(fits, OsduQuery.Equal(OsduField.Keyword("data.LegacyRef"), fits).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WB nested(1")]
    [InlineData("WB nested (1")]
    public void A_value_the_service_would_read_as_nested_syntax_is_refused_in_any_query(string value)
    {
        // QueryParserUtil switches to its nested parser when the query contains either text, quoted or not.
        var refused = Assert.Throws<OsduQueryException>(() => OsduQuery.Exact("data.FacilityName", value));
        Assert.Contains("nested", refused.Message, StringComparison.Ordinal);
        Assert.Throws<OsduQueryException>(() => OsduQuery.Phrase("data.FacilityName", value));
    }

    [Fact]
    public void The_nested_marker_is_matched_as_the_service_matches_it_with_case()
        => Assert.Equal("data.FacilityName.keyword:\"Nested(1\"", OsduQuery.Exact("data.FacilityName", "Nested(1").Text);

    [Theory]
    [InlineData("WB (A")]
    [InlineData("WB A)")]
    [InlineData("WB )(A")]
    public void A_value_with_unbalanced_parentheses_is_refused_inside_a_nested_query(string value)
    {
        // trimTrailingBrackets balances the inner query by counting every parenthesis, quoted ones included.
        var refused = Assert.Throws<OsduQueryException>(
            () => OsduQuery.Equal(OsduField.Text("data.NameAliases.AliasName", "data.NameAliases"), value));
        Assert.Contains("parentheses", refused.Message, StringComparison.Ordinal);

        // Outside a nested query nothing counts them, and the quoted phrase carries them.
        Assert.Contains(value, OsduQuery.Exact("data.FacilityName", value).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_with_balanced_parentheses_is_carried_inside_a_nested_query()
        => Assert.Equal(
            "nested(data.NameAliases, (AliasName.keyword:\"WB (A)\"))",
            OsduQuery.Equal(OsduField.Text("data.NameAliases.AliasName", "data.NameAliases"), "WB (A)").Text);

    [Theory]
    [InlineData("A AND B:1")]
    [InlineData("A OR B:1")]
    [InlineData("NOT B:1")]
    // The service's pattern has no word boundary, so a word ending in AND or OR is read the same way.
    [InlineData("BRAND X:1")]
    [InlineData("FLOOR X:1")]
    public void A_value_the_service_would_rewrite_as_a_property_is_refused_inside_a_nested_query(string value)
    {
        // intermediateStringQueryNestedPattern: (AND|OR|NOT)\s(\S+?):, applied to the raw inner query.
        var refused = Assert.Throws<OsduQueryException>(
            () => OsduQuery.Equal(OsduField.Text("data.NameAliases.AliasName", "data.NameAliases"), value));
        Assert.Contains("rewrites", refused.Message, StringComparison.Ordinal);

        // A plain query is not rewritten, so there the value is asked for as it is.
        Assert.Contains(value, OsduQuery.Exact("data.FacilityName", value).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A AND B")]
    [InlineData("SAND:1")]
    [InlineData("NO 15/9-F-1")]
    public void A_value_the_service_leaves_alone_is_carried_inside_a_nested_query(string value)
        => Assert.Contains(
            LuceneText.Phrase(value),
            OsduQuery.Equal(OsduField.Text("data.NameAliases.AliasName", "data.NameAliases"), value).Text,
            StringComparison.Ordinal);

    [Fact]
    public void A_nested_query_holds_one_comparison_because_a_group_does_not_survive_the_services_rewriting()
    {
        var group = OsduQuery.All(OsduQuery.Exact("AliasName", "A"), OsduQuery.Exact("AliasNameTypeID", "B"));
        Assert.Contains("one comparison", Assert.Throws<OsduQueryException>(() => OsduQuery.Nested("data.NameAliases", group)).Message, StringComparison.Ordinal);
    }
}
