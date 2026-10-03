using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The queries the explorer shows for an element of a record (osdu/docs/explorer.md, The query of an element): each
/// written as the module's own searches are, by how the saved template of the record's kind has the platform index it
/// (text with its whole value in a keyword sub-field, a keyword, a number; in a nested list, reached with nested(...); in a
/// flattened list; not indexed at all), said in words, with why none finds an element where none can.
/// </summary>
public class ExplorerElementQueriesTests
{
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";

    private static SchemaSnapshot Schema(string kind)
    {
        var file = Path.Combine(Samples.TemplateFiles, kind.Replace(':', '_') + ".json");
        return SchemaSnapshot.Parse(kind, File.ReadAllText(file), DateTimeOffset.UnixEpoch);
    }

    private static readonly SchemaSnapshot Wellbore = Schema(WellboreKind);

    private static ExplorerElementRequest Value(string path, object? value, string kind = WellboreKind) => new()
    {
        Kind = kind,
        Path = path,
        Value = JsonSerializer.SerializeToElement(value),
    };

    private static ExplorerElementRequest Section(string path, string kind = WellboreKind) => new() { Kind = kind, Path = path, Section = true };

    private static Dictionary<string, string> Queries(ExplorerElementAnswer answer) => answer.Queries.ToDictionary(q => q.Purpose, q => q.Query);

    [Fact]
    public void A_text_is_found_whole_by_its_keyword_by_its_words_and_by_being_there()
    {
        var answer = ExplorerElementQueries.Describe(Value("data.FacilityName", "NO 16/2-9 S"), Wellbore);

        Assert.Null(answer.Problem);
        Assert.Equal(WellboreKind, answer.Template!.Kind);
        Assert.Equal(new ExplorerElementField("data.FacilityName", "text", null, null), answer.Field);
        var queries = Queries(answer);
        Assert.Equal("data.FacilityName.keyword:\"NO 16/2-9 S\"", queries["equal"]);
        Assert.Equal("data.FacilityName:\"NO 16/2-9 S\"", queries["words"]);
        Assert.Equal("_exists_:data.FacilityName", queries["exists"]);
        Assert.Contains("data.FacilityName.keyword, which an exact match asks", answer.Reading, StringComparison.Ordinal);

        // Each query says what it finds, in words.
        Assert.Equal("The records whose FacilityName is exactly \"NO 16/2-9 S\", the whole value as written.", answer.Queries.Single(q => q.Purpose == "equal").Says);
        Assert.All(answer.Queries, q => Assert.False(string.IsNullOrWhiteSpace(q.Says)));
    }

    [Fact]
    public void A_value_of_a_nested_list_is_found_inside_nested_and_whether_one_is_there_is_not_asked()
    {
        var answer = ExplorerElementQueries.Describe(Value("data.NameAliases[2].AliasName", "NO 16/2-9 S"), Wellbore);

        var queries = Queries(answer);
        Assert.Equal("nested(data.NameAliases, (AliasName.keyword:\"NO 16/2-9 S\"))", queries["equal"]);
        Assert.Equal("nested(data.NameAliases, (AliasName:\"NO 16/2-9 S\"))", queries["words"]);
        Assert.False(queries.ContainsKey("exists"));
        Assert.Equal(("data.NameAliases.AliasName", "data.NameAliases"), (answer.Field!.Path, answer.Field.NestedPath));
        Assert.Contains("nested(data.NameAliases, (...))", answer.Reading, StringComparison.Ordinal);
        Assert.Contains(answer.Notes, n => n.Contains("sits in the nested list data.NameAliases", StringComparison.Ordinal));

        // A value of a list is one of several a record holds, and is said so.
        Assert.StartsWith("The records holding \"NO 16/2-9 S\" as one of their AliasName values", answer.Queries.Single(q => q.Purpose == "equal").Says, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_one_form_of_a_choice_declares_is_found_inside_its_nested_list()
    {
        // GeoContexts holds one of several kinds of context; its political entity's id is asked inside nested(...).
        var answer = ExplorerElementQueries.Describe(Value("data.GeoContexts[3].GeoPoliticalEntityID", "dev:master-data--GeoPoliticalEntity:Norway:"), Wellbore);

        Assert.Equal("nested(data.GeoContexts, (GeoPoliticalEntityID.keyword:\"dev:master-data--GeoPoliticalEntity:Norway:\"))", Queries(answer)["equal"]);
        Assert.Equal("data.GeoContexts", answer.Field!.NestedPath);

        // A record's id is found by its whole value; its words are not offered.
        Assert.False(Queries(answer).ContainsKey("words"));

        var section = ExplorerElementQueries.Describe(Section("data.GeoContexts[3]"), Wellbore);
        Assert.Empty(section.Queries);
        Assert.Contains(section.Notes, n => n.Contains("data.GeoContexts is a nested list", StringComparison.Ordinal));
    }

    [Fact]
    public void A_number_is_compared_as_one_and_has_no_words()
    {
        var answer = ExplorerElementQueries.Describe(Value("data.VerticalMeasurements[0].VerticalMeasurement", 25.5), Wellbore);

        Assert.Equal("number", answer.Field!.Index);
        var queries = Queries(answer);
        Assert.StartsWith("nested(data.VerticalMeasurements, (VerticalMeasurement:", queries["equal"], StringComparison.Ordinal);
        Assert.Contains("25.5", queries["equal"], StringComparison.Ordinal);
        Assert.False(queries.ContainsKey("words"));
    }

    [Fact]
    public void A_value_of_a_flattened_list_is_a_keyword_under_its_dotted_path()
    {
        var answer = ExplorerElementQueries.Describe(Value("data.FacilitySpecifications[0].FacilitySpecificationText", "Rated"), Wellbore);

        Assert.Equal(new ExplorerElementField("data.FacilitySpecifications.FacilitySpecificationText", "keyword", null, "data.FacilitySpecifications"), answer.Field);
        var queries = Queries(answer);
        Assert.Equal("data.FacilitySpecifications.FacilitySpecificationText:\"Rated\"", queries["equal"]);
        Assert.Equal("_exists_:data.FacilitySpecifications.FacilitySpecificationText", queries["exists"]);
        Assert.Contains("flattened list data.FacilitySpecifications", answer.Reading, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_is_found_by_being_there_where_a_query_reaches_it()
    {
        var flattened = ExplorerElementQueries.Describe(Section("data.FacilitySpecifications"), Wellbore);
        Assert.Equal("_exists_:data.FacilitySpecifications", Queries(flattened)["exists"]);

        // An item of a flattened list is found by the list it is in, and says so.
        var item = ExplorerElementQueries.Describe(Section("data.FacilitySpecifications[3]"), Wellbore);
        Assert.Equal("_exists_:data.FacilitySpecifications", Queries(item)["exists"]);
        Assert.Contains(item.Notes, n => n.StartsWith("An item of data.FacilitySpecifications", StringComparison.Ordinal));

        // A nested list is reached only by a value of its items.
        var nested = ExplorerElementQueries.Describe(Section("data.NameAliases"), Wellbore);
        Assert.Empty(nested.Queries);
        Assert.Null(nested.Problem);
        Assert.Equal("data.NameAliases", nested.Field!.NestedPath);
        Assert.Contains(nested.Notes, n => n.Contains("data.NameAliases is a nested list", StringComparison.Ordinal));

        // Every record holds data, so it is no section to search by.
        Assert.Contains("Every record holds data", ExplorerElementQueries.Describe(Section("data"), Wellbore).Problem, StringComparison.Ordinal);
    }

    /// <summary>A schema with an object and a list of objects the template gives no indexing hint.</summary>
    private static SchemaSnapshot Unhinted() => new("osdu:wks:master-data--Thing:1.0.0", JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "data": {
              "type": "object",
              "properties": {
                "Place": { "type": "object", "properties": { "Name": { "type": "string" } } },
                "Parts": { "type": "array", "items": { "type": "object", "properties": { "Code": { "type": "string" } } } }
              }
            }
          }
        }
        """)!.AsObject(), DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_list_the_platform_does_not_index_inside_says_why_no_query_reaches_it()
    {
        var schema = Unhinted();

        var section = ExplorerElementQueries.Describe(Section("data.Parts", "osdu:wks:master-data--Thing:1.0.0"), schema);
        Assert.Empty(section.Queries);
        Assert.Contains("gives no x-osdu-indexing hint", section.Problem, StringComparison.Ordinal);

        var value = ExplorerElementQueries.Describe(Value("data.Parts[0].Code", "A1", "osdu:wks:master-data--Thing:1.0.0"), schema);
        Assert.Empty(value.Queries);
        Assert.Contains("no query reaches inside it", value.Problem, StringComparison.Ordinal);
        Assert.Equal("data.Parts.Code", value.Path);

        // An object is a section a record holds or not.
        var place = ExplorerElementQueries.Describe(Section("data.Place", "osdu:wks:master-data--Thing:1.0.0"), schema);
        Assert.Equal("_exists_:data.Place", Queries(place)["exists"]);
    }

    [Fact]
    public void The_records_own_values_are_asked_as_the_indexer_maps_them_for_every_kind()
    {
        var tag = ExplorerElementQueries.Describe(Value("legal.legaltags[0]", "dev-public"), Wellbore);
        Assert.Equal("legal.legaltags:\"dev-public\"", Queries(tag)["equal"]);
        Assert.Equal("keyword", tag.Field!.Index);

        var id = ExplorerElementQueries.Describe(Value("id", "dev:master-data--Wellbore:W1"), Wellbore);
        Assert.Equal("id:\"dev:master-data--Wellbore:W1\"", Queries(id)["equal"]);

        Assert.Equal("_exists_:acl", Queries(ExplorerElementQueries.Describe(Section("acl"), Wellbore))["exists"]);
    }

    [Fact]
    public void Without_a_template_the_value_is_read_as_most_are_and_a_list_of_objects_is_said_to_be_unknown()
    {
        var text = ExplorerElementQueries.Describe(Value("data.Remark", "deep"), null);
        Assert.Null(text.Template);
        Assert.Equal("data.Remark.keyword:\"deep\"", Queries(text)["equal"]);
        Assert.Contains(text.Notes, n => n.StartsWith("No saved template of", StringComparison.Ordinal));

        var number = ExplorerElementQueries.Describe(Value("data.Depth", 1200), null);
        Assert.Equal("number", number.Field!.Index);
        Assert.False(Queries(number).ContainsKey("words"));

        var date = ExplorerElementQueries.Describe(Value("data.Spudded", "2024-03-01T12:00:00Z"), null);
        Assert.Equal("date", date.Field!.Index);

        var inList = ExplorerElementQueries.Describe(Value("data.GeoContexts[1].GeoTypeID", "x"), null);
        Assert.Contains(inList.Notes, n => n.StartsWith("data.GeoContexts is a list of objects", StringComparison.Ordinal));
    }

    [Fact]
    public void Null_is_found_only_by_whether_a_record_holds_a_value()
    {
        var answer = ExplorerElementQueries.Describe(Value("data.FacilityName", null), Wellbore);

        Assert.Equal(["exists"], answer.Queries.Select(q => q.Purpose));
        Assert.Contains(answer.Notes, n => n.Contains("holds null here", StringComparison.Ordinal));
    }

    [Fact]
    public void A_value_no_whole_match_can_find_still_has_its_words_and_says_why()
    {
        var answer = ExplorerElementQueries.Describe(Value("data.FacilityName", new string('x', 300)), Wellbore);

        var queries = Queries(answer);
        Assert.False(queries.ContainsKey("equal"));
        Assert.True(queries.ContainsKey("words"));
        Assert.Contains(answer.Notes, n => n.StartsWith("No query finds this whole value", StringComparison.Ordinal));

        var tooLong = ExplorerElementQueries.Describe(Value("data.FacilityName", new string('x', ExplorerElementRequest.MaxValueLength + 1)), Wellbore);
        Assert.Empty(tooLong.Queries);
        Assert.Contains("a query is written for a value of at most", tooLong.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("data..FacilityName")]
    [InlineData("[0].FacilityName")]
    [InlineData("data.Facility Name")]
    [InlineData("data.FacilityName[")]
    [InlineData("data.Names[x]")]
    [InlineData(".data")]
    public void A_path_that_is_not_one_is_said_to_be_not_one(string path)
    {
        var answer = ExplorerElementQueries.Describe(Value(path, "x"), Wellbore);

        Assert.Empty(answer.Queries);
        Assert.Contains("is not the path of an element of a record", answer.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "data.X", "Name the kind")]
    [InlineData("osdu:wks:WellLog", "data.X", "osdu:wks:WellLog")]
    [InlineData(WellboreKind, " ", "Name the element's path")]
    public void A_request_that_cannot_be_read_is_refused(string kind, string path, string why)
    {
        var request = new ExplorerElementRequest { Kind = kind, Path = path };

        Assert.Contains(why, request.Problem(), StringComparison.Ordinal);
        Assert.Contains("an object or a list is a section", (request with { Kind = WellboreKind, Path = "data.X", Value = JsonSerializer.SerializeToElement(new { a = 1 }) }).Problem(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kind_with_no_template_saved_is_read_by_the_newest_saved_of_its_type_and_says_so()
    {
        var answer = await ExplorerElementQueries.DescribeAsync(
            Value("data.FacilityName", "NO 16/2-9 S", "osdu:wks:master-data--Wellbore:1.1.0"), new Templates(Wellbore), CancellationToken.None);

        Assert.Equal(WellboreKind, answer.Template!.Kind);
        Assert.Contains(answer.Notes, n => n.Contains("No template of osdu:wks:master-data--Wellbore:1.1.0 is saved, so it is read by osdu:wks:master-data--Wellbore:1.3.0", StringComparison.Ordinal));
        Assert.Equal("data.FacilityName.keyword:\"NO 16/2-9 S\"", Queries(answer)["equal"]);

        var none = await ExplorerElementQueries.DescribeAsync(Value("data.FacilityName", "x", "osdu:wks:master-data--Field:1.0.0"), new Templates(Wellbore), CancellationToken.None);
        Assert.Null(none.Template);
        Assert.Contains(none.Notes, n => n.StartsWith("No saved template of osdu:wks:master-data--Field:1.0.0", StringComparison.Ordinal));

        await Assert.ThrowsAsync<DeliveryException>(() => ExplorerElementQueries.DescribeAsync(Value("data.X", "x", "bad"), new Templates(), CancellationToken.None));
    }

    /// <summary>Templates held in memory: the saved versions a case names, and no others.</summary>
    private sealed class Templates(params SchemaSnapshot[] schemas) : ITemplateStore
    {
        public Task<SchemaSnapshot?> LoadAsync(TemplateReference reference, CancellationToken ct = default)
            => Task.FromResult(schemas.FirstOrDefault(s => s.Kind == reference.Kind && s.Version == reference.Version));

        public Task<TemplateSaved> SaveAsync(SchemaSnapshot schema, string origin, string actor, CancellationToken ct = default)
            => throw new NotSupportedException("The element queries only read templates.");

        public Task<IReadOnlyList<TemplateInfo>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TemplateInfo>>(schemas.Select(s => new TemplateInfo(s.Kind, s.Version, s.CapturedUtc.UtcDateTime, "tests", "memory")).ToList());

        public Task DeleteAsync(TemplateReference reference, CancellationToken ct = default)
            => throw new NotSupportedException("The element queries only read templates.");
    }
}
