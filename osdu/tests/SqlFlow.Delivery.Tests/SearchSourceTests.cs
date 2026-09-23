using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A mapping that resolves a reference by searching the platform rather than out of the partition's cache, for the
/// records a delivery refers to: business data that grows without bound, where capturing the set to answer one lookup
/// costs more every day.
/// </summary>
/// <remarks>
/// The render stays synchronous and does no I/O. It asks what the run already knows; a question nobody has asked leaves
/// the render unfinished, naming what it needs, and the caller asks the platform once and renders again. These tests
/// are that protocol, and what each answer the platform can give does to the record.
/// </remarks>
public class SearchSourceTests
{
    /// <summary>A wellbore schema with one property of every shape the indexer gives a property.</summary>
    internal static SchemaSnapshot WellboreSchema() => SchemaSnapshot.Parse("osdu:wks:master-data--Wellbore:1.3.0", """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "kind": { "type": "string" },
            "data": {
              "allOf": [
                { "$ref": "#/definitions/AbstractMaster.1.0.0" },
                {
                  "type": "object",
                  "properties": {
                    "FacilityName": { "type": "string" },
                    "SpudDate": { "type": "string", "format": "date" },
                    "Uri": { "type": "string", "format": "uri" },
                    "TotalDepth": { "type": "number" },
                    "Count": { "type": "string", "format": "int64" },
                    "LegacyRef": { "type": "string", "pattern": "^srn:master-data/Well:[^:]+:[0-9]*$" },
                    "WellID": { "type": "string", "pattern": "^[a-z]+:master-data--Well:.+$" },
                    "Codes": { "type": "array", "items": { "type": "string" } },
                    "LegacyRefs": { "type": "array", "items": { "type": "string", "pattern": "^srn:.+$" } },
                    "Depths": { "type": "array", "items": { "type": "number" } },
                    "Untyped": { },
                    "Location": { "type": "object", "properties": { "Label": { "type": "string" } } },
                    "Plain": { "type": "array", "items": { "type": "object", "properties": { "Code": { "type": "string" } } } },
                    "FacilitySpecifications": {
                      "type": "array",
                      "x-osdu-indexing": { "type": "flattened" },
                      "items": { "type": "object", "properties": { "FacilitySpecificationText": { "type": "string" }, "Size": { "type": "number" }, "Inner": { "type": "object", "properties": { "Deep": { "type": "string" } } } } }
                    },
                    "VerticalMeasurements": {
                      "type": "array",
                      "x-osdu-indexing": { "type": "nested" },
                      "items": {
                        "type": "object",
                        "properties": {
                          "VerticalMeasurementID": { "type": "string" },
                          "Reference": { "type": "object", "properties": { "Label": { "type": "string" } } },
                          "Readings": { "type": "array", "x-osdu-indexing": { "type": "nested" }, "items": { "type": "object", "properties": { "Value": { "type": "string" } } } }
                        }
                      }
                    }
                  }
                }
              ]
            }
          },
          "definitions": {
            "AbstractMaster.1.0.0": {
              "type": "object",
              "properties": {
                "NameAliases": { "type": "array", "x-osdu-indexing": { "type": "nested" }, "items": { "$ref": "#/definitions/AbstractAliasNames.1.0.0" } }
              }
            },
            "AbstractAliasNames.1.0.0": { "type": "object", "properties": { "AliasName": { "type": "string" }, "AliasNameTypeID": { "type": "string" } } }
          }
        }
        """, DateTimeOffset.UnixEpoch);

    internal const string SearchedKind = "osdu:wks:master-data--Wellbore:*";

    internal static string SearchesBlock(SchemaSnapshot schema) => $"""
        searches:
          Wellbore:
            kind: "{SearchedKind}"
            schema:
              kind: {schema.Kind}
              version: {schema.Version}
        """;

    /// <summary>A mapping over the test template whose entries include <paramref name="entries"/>, declaring the Wellbore search.</summary>
    internal static MappingDefinition Mapping(string entries, string fixtures = "")
        => new DeliveryDocumentLoader().ParseMapping(Document(entries, fixtures), "thing.yaml");

    internal static string Document(string entries, string fixtures = "")
        => TestSchema.MappingDocument(entries, fixtures).Replace("parameters:", SearchesBlock(WellboreSchema()) + "\nparameters:", StringComparison.Ordinal);

    internal static ResolvedSearches Resolve(MappingDefinition mapping)
    {
        var schema = WellboreSchema();
        return ResolvedSearches.Resolve(mapping, new Dictionary<TemplateReference, SchemaSnapshot> { [new TemplateReference(schema.Kind, schema.Version)] = schema });
    }

    private const string ByNameThenAlias = """
          - target: osdu.data.WellboreID
            source: search.Wellbore.id
            findBy:
              - search.Wellbore.data.FacilityName = dataset.wb
              - search.Wellbore.data.NameAliases.AliasName = dataset.wb
        """;

    private const string OptionalByNameThenAlias = ByNameThenAlias + """

            required: false
        """;

    private static MappingRenderer Renderer(IRecordSearch search, string entries = ByNameThenAlias)
    {
        var mapping = Mapping(entries);
        return new MappingRenderer(mapping, TestSchema.Build(), ReferenceSnapshot.Empty, TestSchema.Context(), Resolve(mapping), search);
    }

    /// <summary>A row carrying the wellbore the search looks for and the schema-required property, so a hold names a search.</summary>
    private static SourceRecord Row(string? wellbore)
        => new() { Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = "x", ["depth"] = "1", ["wb"] = wellbore }) };

    private static FixedRecordSearch Platform(params (string Field, string Value, string Id)[] known) => new(known);

    /// <summary>Renders the row, answering what it asks between renders, until it finishes; returns the result and how many renders it took.</summary>
    private static async Task<(RenderResult Result, int Renders)> Settle(MappingRenderer renderer, IRecordSearch search, SourceRecord row)
    {
        for (var renders = 1; renders <= 10; renders++)
        {
            var result = renderer.Render(row);
            if (!result.IsIncomplete)
            {
                return (result, renders);
            }

            await search.AnswerAsync(result.Unanswered);
        }

        throw new InvalidOperationException("the render never finished");
    }

    private static string? WellboreId(RenderResult result) => result.Document["data"]?["WellboreID"]?.GetValue<string>();

    [Fact]
    public void A_question_the_run_has_not_asked_leaves_the_render_unfinished_rather_than_guessing()
    {
        var search = Platform(("data.FacilityName", "WB-1", "dev:master-data--Wellbore:abc"));
        var result = Renderer(search).Render(Row("WB-1"));

        Assert.True(result.IsIncomplete);
        var question = Assert.Single(result.Unanswered);
        Assert.Equal(new SearchQuestion(SearchedKind, "data.FacilityName", "WB-1", "data.FacilityName.keyword:\"WB-1\""), question);

        // Nothing was asked of the platform by the render itself: that is the caller's to do.
        Assert.Empty(search.Asked);
    }

    [Fact]
    public async Task The_second_render_finishes_once_the_question_has_been_answered()
    {
        var search = Platform(("data.FacilityName", "WB-1", "dev:master-data--Wellbore:abc"));
        var (result, renders) = await Settle(Renderer(search), search, Row("WB-1"));

        Assert.Equal(2, renders);
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("dev:master-data--Wellbore:abc:", WellboreId(result));
    }

    [Fact]
    public async Task A_question_is_asked_of_the_platform_once_however_many_rows_refer_to_it()
    {
        var search = Platform(("data.FacilityName", "WB-1", "dev:master-data--Wellbore:abc"));
        var renderer = Renderer(search);

        await search.AnswerAsync(renderer.Render(Row("WB-1")).Unanswered);
        foreach (var _ in Enumerable.Range(0, 50))
        {
            Assert.False(renderer.Render(Row("WB-1")).IsIncomplete);
        }

        // Fifty-one rows, one round trip: this is what makes a search cheaper than capturing the set it searches.
        Assert.Single(search.Asked);
    }

    [Fact]
    public async Task The_lines_are_asked_in_turn_and_the_alias_only_when_the_name_finds_nothing()
    {
        var search = Platform(("data.NameAliases.AliasName", "OLD-NAME", "dev:master-data--Wellbore:abc"));
        var renderer = Renderer(search);

        var first = renderer.Render(Row("OLD-NAME"));
        Assert.Equal("data.FacilityName", Assert.Single(first.Unanswered).Field);
        await search.AnswerAsync(first.Unanswered);

        // The name found nothing, so the alias is asked next, through the nested form the schema says it needs.
        var second = renderer.Render(Row("OLD-NAME"));
        var alias = Assert.Single(second.Unanswered);
        Assert.Equal("nested(data.NameAliases, (AliasName.keyword:\"OLD-NAME\"))", alias.Query);
        await search.AnswerAsync(second.Unanswered);

        var third = renderer.Render(Row("OLD-NAME"));
        Assert.False(third.IsHeld, string.Join("; ", third.Holds));
        Assert.Equal("dev:master-data--Wellbore:abc:", WellboreId(third));
    }

    [Fact]
    public async Task A_name_that_finds_the_record_is_not_followed_by_a_search_of_the_aliases()
    {
        var search = Platform(("data.FacilityName", "WB-1", "dev:master-data--Wellbore:abc"));
        await Settle(Renderer(search), search, Row("WB-1"));

        Assert.Equal("data.FacilityName", Assert.Single(search.Asked).Field);
    }

    [Fact]
    public async Task A_value_every_line_finds_nothing_for_holds_a_required_entry_and_says_what_was_asked()
    {
        var search = Platform();
        var (result, _) = await Settle(Renderer(search), search, Row("MISSING"));

        Assert.True(result.IsHeld);
        var hold = Assert.Single(result.Holds);
        Assert.Contains("no Wellbore on the platform", hold, StringComparison.Ordinal);
        Assert.Contains("data.FacilityName 'MISSING' found no record", hold, StringComparison.Ordinal);
        Assert.Contains("data.NameAliases.AliasName 'MISSING' found no record", hold, StringComparison.Ordinal);

        // Found-nothing is an answer: asking again on every render would turn one missing wellbore into a call per row.
        Assert.Equal(2, search.Asked.Count);
    }

    [Fact]
    public async Task A_value_every_line_finds_nothing_for_leaves_an_optional_entry_out()
    {
        var search = Platform();
        var (result, _) = await Settle(Renderer(search, OptionalByNameThenAlias), search, Row("MISSING"));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Null(WellboreId(result));
    }

    [Fact]
    public async Task A_value_several_records_answer_to_holds_the_record_even_when_the_entry_is_optional()
    {
        var search = Platform(
            ("data.FacilityName", "TWIN", "dev:master-data--Wellbore:a"),
            ("data.FacilityName", "TWIN", "dev:master-data--Wellbore:b"),
            ("data.NameAliases.AliasName", "TWIN", "dev:master-data--Wellbore:c"));
        var (result, _) = await Settle(Renderer(search, OptionalByNameThenAlias), search, Row("TWIN"));

        var hold = Assert.Single(result.Holds);
        Assert.Contains("found 2 records (dev:master-data--Wellbore:a, dev:master-data--Wellbore:b)", hold, StringComparison.Ordinal);

        // Several answers to the name are doubt, not a miss: the alias is not asked to break the tie.
        Assert.Single(search.Asked);
    }

    [Fact]
    public async Task A_query_the_platform_refused_holds_the_record_with_what_the_platform_said()
    {
        var search = new RefusingSearch("Malformed query");
        var (result, _) = await Settle(Renderer(search, OptionalByNameThenAlias), search, Row("WB-1"));

        var hold = Assert.Single(result.Holds);
        Assert.Contains("was refused by the search service: Malformed query", hold, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_no_line_can_ask_for_holds_the_record_whatever_required_says()
    {
        // The keyword sub-field indexes a null property as the text "null", so neither line can look it up.
        var search = Platform();
        var (result, _) = await Settle(Renderer(search, OptionalByNameThenAlias), search, Row("null"));

        var hold = Assert.Single(result.Holds);
        Assert.Contains("cannot be searched for", hold, StringComparison.Ordinal);
        Assert.Contains("proves nothing about whether the record exists", hold, StringComparison.Ordinal);
        Assert.Empty(search.Asked);
    }

    [Fact]
    public async Task A_line_that_cannot_ask_and_a_line_that_finds_nothing_hold_even_an_optional_entry()
    {
        // The name can be asked for; the alias cannot, since the service would rewrite 'AND X:' inside a nested query.
        var search = Platform();
        var (result, _) = await Settle(Renderer(search, OptionalByNameThenAlias), search, Row("BRAND X:1"));

        var hold = Assert.Single(result.Holds);
        Assert.Contains("data.FacilityName 'BRAND X:1' found no record", hold, StringComparison.Ordinal);
        Assert.Contains("data.NameAliases.AliasName 'BRAND X:1' cannot be searched for", hold, StringComparison.Ordinal);
        Assert.Equal("data.FacilityName", Assert.Single(search.Asked).Field);
    }

    [Fact]
    public async Task A_line_that_cannot_ask_does_not_keep_a_later_line_from_finding_the_record()
    {
        const string AliasFirst = """
              - target: osdu.data.WellboreID
                source: search.Wellbore.id
                findBy:
                  - search.Wellbore.data.NameAliases.AliasName = dataset.wb
                  - search.Wellbore.data.FacilityName = dataset.wb
            """;
        var search = Platform(("data.FacilityName", "BRAND X:1", "dev:master-data--Wellbore:abc"));
        var (result, _) = await Settle(Renderer(search, AliasFirst), search, Row("BRAND X:1"));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("dev:master-data--Wellbore:abc:", WellboreId(result));
    }

    [Fact]
    public void A_value_that_already_is_an_osdu_id_of_the_kind_searched_names_its_record_without_asking()
    {
        var search = Platform();
        var result = Renderer(search).Render(Row("dev:master-data--Wellbore:abc"));

        Assert.False(result.IsIncomplete);
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("dev:master-data--Wellbore:abc:", WellboreId(result));
        Assert.Empty(search.Asked);
    }

    [Fact]
    public void An_osdu_id_of_another_kind_holds_the_record_rather_than_refer_to_the_wrong_kind_of_record()
    {
        var result = Renderer(Platform()).Render(Row("dev:master-data--Well:abc"));

        var hold = Assert.Single(result.Holds);
        Assert.Contains("of a master-data--Well record", hold, StringComparison.Ordinal);
        Assert.Contains("looks for master-data--Wellbore records", hold, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_value_asks_nothing_and_holds_a_required_entry()
    {
        var search = Platform();
        var (result, renders) = await Settle(Renderer(search), search, Row("   "));

        Assert.Equal(1, renders);
        Assert.Contains(result.Holds, h => h.Contains("dataset.wb is empty, so there is nothing to search for", StringComparison.Ordinal));
        Assert.Empty(search.Asked);
    }

    [Fact]
    public async Task What_was_consulted_is_kept_so_a_record_says_how_its_reference_resolved()
    {
        var search = Platform(("data.NameAliases.AliasName", "OLD", "dev:master-data--Wellbore:abc"));
        var (result, _) = await Settle(Renderer(search), search, Row("OLD"));

        Assert.Collection(
            result.SearchUsages,
            name => Assert.Equal(new SearchUsage(SearchedKind, "data.FacilityName", "OLD", "data.FacilityName.keyword:\"OLD\"", SearchOutcome.NotFound, null), name),
            alias => Assert.Equal(
                new SearchUsage(SearchedKind, "data.NameAliases.AliasName", "OLD", "nested(data.NameAliases, (AliasName.keyword:\"OLD\"))", SearchOutcome.Found, "dev:master-data--Wellbore:abc"),
                alias));
    }

    [Fact]
    public void A_mapping_that_searches_and_is_given_no_platform_finds_nothing_and_holds()
    {
        var mapping = Mapping(ByNameThenAlias);
        var result = new MappingRenderer(mapping, TestSchema.Build(), ReferenceSnapshot.Empty, TestSchema.Context(), Resolve(mapping)).Render(Row("WB-1"));

        Assert.False(result.IsIncomplete);
        Assert.True(result.IsHeld);
    }

    [Fact]
    public void A_search_the_render_was_not_given_the_schema_of_holds_rather_than_guess_at_a_query()
    {
        var mapping = Mapping(ByNameThenAlias);
        var result = new MappingRenderer(mapping, TestSchema.Build(), ReferenceSnapshot.Empty, TestSchema.Context(), searches: null, Platform()).Render(Row("WB-1"));

        Assert.Contains(result.Holds, h => h.Contains("has not been resolved against the schema search 'Wellbore' pins", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Renders_on_many_threads_over_one_renderer_keep_their_questions_apart()
    {
        // The plan renders on several threads over one renderer, so what one render asks must never reach another's result.
        var search = Platform();
        var renderer = Renderer(search);
        var rows = Enumerable.Range(0, 400).Select(i => $"WB-{i}").ToList();
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, RenderResult>();

        await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 8 }, (wellbore, _) =>
        {
            results[wellbore] = renderer.Render(Row(wellbore));
            return ValueTask.CompletedTask;
        });

        foreach (var wellbore in rows)
        {
            var question = Assert.Single(results[wellbore].Unanswered);
            Assert.Equal(wellbore, question.Value);
        }
    }

    [Fact]
    public void A_search_a_mapping_does_not_declare_is_refused_when_the_document_is_read()
    {
        var undeclared = TestSchema.MappingDocument(ByNameThenAlias);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(undeclared, "thing.yaml"));

        Assert.Contains("declares no searches", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declared_search_nothing_reads_is_refused_rather_than_left_to_cost_calls()
    {
        var refused = Assert.Throws<FlowValidationException>(() => Mapping("""
              - target: osdu.data.Unit
                source: dataset.name
            """));

        Assert.Contains("nothing reads it", refused.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> UnanswerableEntries => new()
    {
        {
            """
              - target: osdu.data.WellboreID
                source: search.Wellbore.data.FacilityName
                findBy: search.Wellbore.data.FacilityName = dataset.wb
            """,
            "a search returns only the record's id"
        },
        {
            """
              - target: osdu.data.WellboreID
                source: search.Wellbore.id
                findBy: search.Wellbore.FacilityName = dataset.wb
            """,
            "a search compares a property under data"
        },
        {
            """
              - target: osdu.data.WellboreID
                source: search.Wellbore.id
                findBy: search.Wellbore.data.Facility-Name = dataset.wb
            """,
            "a search compares a property under data"
        },
        {
            """
              - target: osdu.data.WellboreID
                source: search.Wellbore.id
                findBy: cache.Wellbore.data.FacilityName = dataset.wb
            """,
            "findBy selects a record of the set the entry reads"
        },
        {
            """
              - target: osdu.data.WellboreID
                source: search.Wellbore.id
                findBy: search.Wellbore.data.FacilityName = dataset.wb
                ignoreSeparators: true
            """,
            "only applies to a cache source"
        },
        {
            """
              - target: osdu.data.WellboreID
                source: search.Wellbore.id
                findBy: search.Wellbore.data.FacilityName = 'NO 15/9-F-1'
                modifiers: [trim]
            """,
            "what a search finds is never modified"
        },
    };

    [Theory]
    [MemberData(nameof(UnanswerableEntries))]
    public void A_search_entry_the_platform_could_not_answer_is_refused_where_it_is_written(string entry, string expected)
        => Assert.Contains(expected, Assert.Throws<FlowValidationException>(() => Mapping(entry)).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("osdu:wks:*:*", "only the version may be '*'")]
    [InlineData("osdu:wks:master-data--Well:*", "another entity type")]
    [InlineData("osdu:wks:master-data--Wellbore:1.2.0", "pin the schema of the version it searches")]
    [InlineData("not a kind", "is not an OSDU kind")]
    public void A_search_whose_kind_its_schema_cannot_describe_is_refused(string kind, string expected)
    {
        var schema = WellboreSchema();
        var document = TestSchema.MappingDocument(ByNameThenAlias).Replace("parameters:", $"""
            searches:
              Wellbore:
                kind: "{kind}"
                schema:
                  kind: {schema.Kind}
                  version: {schema.Version}

            parameters:
            """, StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(document, "thing.yaml"));

        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_search_that_pins_no_schema_is_refused_and_told_where_to_save_one()
    {
        var document = TestSchema.MappingDocument(ByNameThenAlias).Replace("parameters:", """
            searches:
              Wellbore:
                kind: "osdu:wks:master-data--Wellbore:*"

            parameters:
            """, StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(document, "thing.yaml"));

        Assert.Contains("pins no schema", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Templates page", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_searches_of_one_kind_are_refused_so_a_question_always_belongs_to_one_search()
    {
        var schema = WellboreSchema();
        var document = TestSchema.MappingDocument(ByNameThenAlias + """

              - target: osdu.data.Unit
                source: search.Other.id
                findBy: search.Other.data.FacilityName = dataset.wb
            """).Replace("parameters:", SearchesBlock(schema) + $"""

              Other:
                kind: "{SearchedKind}"
                schema:
                  kind: {schema.Kind}
                  version: {schema.Version}

            parameters:
            """, StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(document, "thing.yaml"));

        Assert.Contains("both look in osdu:wks:master-data--Wellbore:*", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A platform that refuses every query, saying why.</summary>
    private sealed class RefusingSearch(string reason) : IRecordSearch
    {
        private readonly Dictionary<SearchQuestion, SearchAnswer> _answers = [];

        public bool TryAnswer(SearchQuestion question, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchAnswer? answer)
            => _answers.TryGetValue(question, out answer);

        public Task AnswerAsync(IReadOnlyCollection<SearchQuestion> questions, CancellationToken ct = default)
        {
            foreach (var question in questions)
            {
                _answers[question] = SearchAnswer.Refused(reason);
            }

            return Task.CompletedTask;
        }
    }
}
