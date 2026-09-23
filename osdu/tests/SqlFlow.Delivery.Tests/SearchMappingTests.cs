using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A mapping that searches, as the preflight gate and the mapping builder see it: every lookup checked against the schema
/// its search pins before anything renders, fixtures that say what the platform answers so they check the mapping and not
/// the platform's data of the day, and a document that survives being opened and written back by the builder.
/// </summary>
public class SearchMappingTests
{
    private const string ByNameThenAlias = """
          - target: osdu.data.WellboreID
            source: search.Wellbore.id
            findBy:
              - search.Wellbore.data.FacilityName = dataset.wb
              - search.Wellbore.data.NameAliases.AliasName = dataset.wb
        """;

    private static IReadOnlyList<ValidationIssue> Check(MappingDefinition mapping, ResolvedSearches? searches)
        => Preflight.Check(mapping, TestSchema.Build(), ReferenceSnapshot.Empty, TestSchema.Context(), sourceColumns: null, searches);

    private static IEnumerable<string> Errors(IReadOnlyList<ValidationIssue> issues)
        => issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message);

    /// <summary>The document a platform answering <paramref name="known"/> leads the row to, as canonical JSON.</summary>
    private static async Task<string> Rendered(Dictionary<string, string?> row, params (string Field, string Value, string Id)[] known)
    {
        var mapping = SearchSourceTests.Mapping(ByNameThenAlias);
        var renderer = new MappingRenderer(mapping, TestSchema.Build(), ReferenceSnapshot.Empty, TestSchema.Context(), SearchSourceTests.Resolve(mapping), new FixedRecordSearch(known));
        var result = await Samples.RenderSettledAsync(renderer, new SourceRecord { Row = SourceRow.FromStrings(row) });
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        return result.Canonical;
    }

    private static string Fixture(string searches, string expected) => $"""
        fixtures:
          - name: the wellbore of WB-1
        {searches}
            record:
              name: thing-1
              depth: "1"
              wb: WB-1
            expected: |
              {expected}
        """;

    private static readonly Dictionary<string, string?> Row = new() { ["name"] = "thing-1", ["depth"] = "1", ["wb"] = "WB-1" };

    [Fact]
    public void A_mapping_that_searches_is_not_passed_without_the_schemas_its_searches_pin()
    {
        var mapping = SearchSourceTests.Mapping(ByNameThenAlias);

        var error = Assert.Single(Errors(Check(mapping, searches: null)));

        Assert.Contains("the schemas those searches pin were not loaded", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_property_a_search_cannot_ask_for_fails_the_preflight_naming_the_entry_and_the_reason()
    {
        var mapping = SearchSourceTests.Mapping("""
              - target: osdu.data.WellboreID
                source: search.Wellbore.id
                findBy: search.Wellbore.data.SpudDate = dataset.wb
            """);

        var error = Assert.Single(Errors(Check(mapping, SearchSourceTests.Resolve(mapping))));

        Assert.Contains("osdu.data.WellboreID", error, StringComparison.Ordinal);
        Assert.Contains("search.Wellbore.data.SpudDate", error, StringComparison.Ordinal);
        Assert.Contains("indexes as a date", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_search_that_finds_another_kind_of_record_than_the_property_points_to_fails_the_preflight()
    {
        var mapping = SearchSourceTests.Mapping("""
              - target: osdu.data.Unit
                source: search.Wellbore.id
                findBy: search.Wellbore.data.FacilityName = dataset.wb
            """);

        var errors = Errors(Check(mapping, SearchSourceTests.Resolve(mapping))).ToList();

        Assert.Contains(
            errors,
            e => e.Contains("writes the id of a master-data--Wellbore record search 'Wellbore' finds, but the template points osdu.data.Unit to reference-data--UnitOfMeasure", StringComparison.Ordinal));
    }

    [Fact]
    public void A_search_cannot_fill_an_object_since_it_finds_one_id()
    {
        var mapping = SearchSourceTests.Mapping("""
              - target: osdu.data.Nested
                source: search.Wellbore.id
                findBy: search.Wellbore.data.FacilityName = dataset.wb
            """);

        var errors = Errors(Check(mapping, SearchSourceTests.Resolve(mapping))).ToList();

        Assert.Contains(errors, e => e.Contains("search.Wellbore.id is the id of the record a search finds, and osdu.data.Nested is an object", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fixture_renders_against_the_answers_it_declares_and_never_asks_the_platform()
    {
        var expected = await Rendered(Row, ("data.FacilityName", "WB-1", "dev:master-data--Wellbore:abc"));
        var mapping = new DeliveryDocumentLoader().ParseMapping(
            SearchSourceTests.Document(ByNameThenAlias, Fixture("""
                    searches:
                      - { search: Wellbore, field: data.FacilityName, value: WB-1, id: "dev:master-data--Wellbore:abc" }
                """, expected)),
            "thing.yaml");

        Assert.Empty(Errors(Check(mapping, SearchSourceTests.Resolve(mapping))));
    }

    [Fact]
    public async Task A_fixture_can_say_the_name_finds_nothing_and_the_alias_finds_the_record()
    {
        var expected = await Rendered(Row, ("data.NameAliases.AliasName", "WB-1", "dev:master-data--Wellbore:abc"));
        var mapping = new DeliveryDocumentLoader().ParseMapping(
            SearchSourceTests.Document(ByNameThenAlias, Fixture("""
                    searches:
                      - { search: Wellbore, field: data.FacilityName, value: WB-1 }
                      - { search: Wellbore, field: data.NameAliases.AliasName, value: WB-1, id: "dev:master-data--Wellbore:abc" }
                """, expected)),
            "thing.yaml");

        Assert.Empty(Errors(Check(mapping, SearchSourceTests.Resolve(mapping))));
    }

    [Fact]
    public async Task A_fixture_that_leaves_a_search_unanswered_fails_and_says_what_to_declare()
    {
        var expected = await Rendered(Row, ("data.FacilityName", "WB-1", "dev:master-data--Wellbore:abc"));
        var mapping = new DeliveryDocumentLoader().ParseMapping(SearchSourceTests.Document(ByNameThenAlias, Fixture(string.Empty, expected)), "thing.yaml");

        var error = Assert.Single(Errors(Check(mapping, SearchSourceTests.Resolve(mapping))));

        Assert.Contains("fixture 'the wellbore of WB-1' searches for what it declares no answer to", error, StringComparison.Ordinal);
        Assert.Contains("{ search: Wellbore, field: data.FacilityName, value: WB-1, id: <the record found, or leave it out for none> }", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ search: Other, field: data.FacilityName, value: WB-1 }", "names search 'Other', which the mapping does not declare")]
    [InlineData("{ search: Wellbore, field: data.WellID, value: WB-1 }", "which no findBy line of that search compares")]
    [InlineData("{ search: Wellbore, field: data.FacilityName }", "gives no value")]
    [InlineData("{ search: Wellbore, field: data.FacilityName, value: WB-1, id: \"dev:master-data--Well:abc\" }", "is not the id of a master-data--Wellbore record")]
    public void A_fixture_answer_that_cannot_be_asked_is_refused_where_it_is_written(string answer, string expected)
    {
        var document = SearchSourceTests.Document(ByNameThenAlias, Fixture($"""
                searches:
                  - {answer}
            """, "{}"));

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(document, "thing.yaml"));

        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fixture_that_answers_one_lookup_twice_is_refused()
    {
        var document = SearchSourceTests.Document(ByNameThenAlias, Fixture("""
                searches:
                  - { search: Wellbore, field: data.FacilityName, value: WB-1 }
                  - { search: Wellbore, field: data.FacilityName, value: WB-1, id: "dev:master-data--Wellbore:abc" }
            """, "{}"));

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(document, "thing.yaml"));

        Assert.Contains("a second time", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_searching_mapping_opened_in_the_builder_is_written_back_as_the_same_mapping()
    {
        var expected = await Rendered(Row, ("data.FacilityName", "WB-1", "dev:master-data--Wellbore:abc"));
        var loader = new DeliveryDocumentLoader();
        var original = loader.ParseMapping(
            SearchSourceTests.Document(ByNameThenAlias + "\n    required: false", Fixture("""
                    searches:
                      - { search: Wellbore, field: data.FacilityName, value: WB-1, id: "dev:master-data--Wellbore:abc" }
                """, expected)),
            "thing.yaml");
        var draft = MappingBuilder.FromDefinition(original);

        var yaml = MappingBuilder.ToYaml(draft);
        var reread = loader.ParseMapping(yaml, "thing.yaml");
        var again = MappingBuilder.FromDefinition(reread);

        var json = new JsonSerializerOptions { WriteIndented = false };
        Assert.Equal(JsonSerializer.Serialize(draft, json), JsonSerializer.Serialize(again, json));
        Assert.Equal(yaml, MappingBuilder.ToYaml(again));

        // The search, its pinned schema, the search entry and the fixture's answer all came back as they were.
        var search = Assert.Single(reread.Searches.Values);
        Assert.Equal(original.Searches["Wellbore"], search);
        var entry = Assert.Single(reread.Entries, e => e.Source?.Kind == MappingSourceKind.Search);
        Assert.Equal("search.Wellbore.id", entry.Source!.ToString());
        Assert.False(entry.Required);
        Assert.Equal(["data.FacilityName", "data.NameAliases.AliasName"], entry.FindBy.Select(f => f.Field));
        Assert.Equal(original.Fixtures.Single().Searches, reread.Fixtures.Single().Searches);
        Assert.Empty(Errors(Check(reread, SearchSourceTests.Resolve(reread))));
    }

    [Fact]
    public void A_mapping_that_names_its_records_by_identity_columns_keeps_them_through_the_builder()
    {
        var loader = new DeliveryDocumentLoader();
        var original = loader.ParseMapping(
            TestSchema.MappingDocument().Replace("  key: [dataset.name]", "  key: [dataset.name]\n  identity: [dataset.name, dataset.depth]", StringComparison.Ordinal),
            "thing.yaml");
        Assert.Equal(["name", "depth"], original.Dataset.Identity);

        var reread = loader.ParseMapping(MappingBuilder.ToYaml(MappingBuilder.FromDefinition(original)), "thing.yaml");

        Assert.Equal(original.Dataset.Identity, reread.Dataset.Identity);
    }

    [Fact]
    public void The_builder_says_what_a_search_entry_still_lacks()
    {
        var draft = new MappingDraft
        {
            Name = "Thing",
            Version = "1.0.0",
            TemplateKind = TestSchema.Kind,
            TemplateVersion = TestSchema.Build().Version,
            System = "test",
            Key = ["name"],
            Searches = [new MappingDraftSearch("Wellbore", "osdu:wks:master-data--Wellbore:*", string.Empty, string.Empty, null)],
            Entries =
            [
                new MappingDraftEntry { Target = "osdu.data.WellboreID", Input = MappingDraftInput.Search, CacheType = "Nowhere", CacheField = "data.FacilityName", FindBy = [new MappingDraftFind("FacilityName", "wb", null)] },
                new MappingDraftEntry { Target = "osdu.data.Unit", Input = MappingDraftInput.Search, CacheType = "Wellbore", CacheField = "id" },
            ],
        };

        var issues = MappingBuilder.Incomplete(draft);

        Assert.Contains(issues, i => i.Message.Contains("pick the saved template whose schema says how the kind it searches is indexed", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.WellboreID" && i.Message.Contains("choose one of the mapping's searches", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.WellboreID" && i.Message.Contains("reads the id of the record it finds", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.WellboreID" && i.Message.Contains("compares a property under data", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Unit" && i.Message.Contains("at least one findBy line", StringComparison.Ordinal));
    }
}
