using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a property of a searched kind is indexed, read from its schema the way the indexer reads it
/// (<c>PropertiesProcessor</c>, <c>TypeMapper</c>): the shape decides the query, so a shape read wrongly is a lookup that
/// silently finds nothing.
/// </summary>
public class SearchFieldsTests
{
    private static (OsduField? Field, string? Problem) Classify(string path) => SearchFields.Classify(SearchSourceTests.WellboreSchema(), path);

    [Theory]
    [InlineData("data.FacilityName")]
    // Any format but a date one is indexed as text, as the indexer maps a storage type it does not know.
    [InlineData("data.Uri")]
    // An OSDU id reference is text: only a pattern starting ^srn makes a link.
    [InlineData("data.WellID")]
    // An object's properties are indexed under dotted paths.
    [InlineData("data.Location.Label")]
    // A list is typed by its items.
    [InlineData("data.Codes")]
    public void A_string_is_text_with_a_keyword_sub_field(string path)
    {
        var (field, problem) = Classify(path);

        Assert.Null(problem);
        Assert.Equal(OsduField.Text(path), field);
    }

    [Theory]
    [InlineData("data.LegacyRef")]
    [InlineData("data.LegacyRefs")]
    public void A_legacy_srn_link_is_a_bare_keyword(string path)
        => Assert.Equal(OsduField.Keyword(path), Classify(path).Field);

    [Fact]
    public void A_property_of_a_nested_array_is_asked_inside_that_array()
    {
        Assert.Equal(OsduField.Text("data.NameAliases.AliasName", "data.NameAliases"), Classify("data.NameAliases.AliasName").Field);

        // An object inside the nested items is still inside the array.
        Assert.Equal(
            OsduField.Text("data.VerticalMeasurements.Reference.Label", "data.VerticalMeasurements"),
            Classify("data.VerticalMeasurements.Reference.Label").Field);
    }

    [Theory]
    [InlineData("data.FacilitySpecifications.FacilitySpecificationText")]
    // Everything inside a flattened array is a keyword, a number or a deeper object's value alike.
    [InlineData("data.FacilitySpecifications.Size")]
    [InlineData("data.FacilitySpecifications.Inner.Deep")]
    public void A_value_inside_a_flattened_array_is_a_keyword(string path)
        => Assert.Equal(OsduField.Keyword(path), Classify(path).Field);

    [Theory]
    [InlineData("data.Plain.Code", "no x-osdu-indexing hint")]
    [InlineData("data.VerticalMeasurements.Readings.Value", "a nested array inside the nested array")]
    [InlineData("data.SpudDate", "indexes as a date")]
    [InlineData("data.TotalDepth", "a search compares text")]
    [InlineData("data.Count", "indexes as a number")]
    [InlineData("data.Depths", "list of number values")]
    [InlineData("data.Location", "is an object, not a value")]
    [InlineData("data.NameAliases", "list of objects, not a value")]
    [InlineData("data.FacilitySpecifications.Inner", "an object inside a flattened array")]
    [InlineData("data.Codes.Anything", "a list of values, which has no properties")]
    [InlineData("data.TotalDepth.Anything", "has no properties to compare")]
    [InlineData("data.Untyped", "declares no type")]
    [InlineData("data.NoSuchThing", "has no property data.NoSuchThing")]
    [InlineData("data.NoSuchThing.Deeper", "has no property data.NoSuchThing")]
    public void A_property_no_exact_query_can_reach_is_refused_with_the_reason(string path, string reason)
    {
        var (field, problem) = Classify(path);

        Assert.Null(field);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bundled_wellbore_schema_indexes_names_as_text_and_aliases_as_a_nested_array()
    {
        // The schema the sample mappings' searches pin, as the platform publishes it.
        var wellbore = Samples.SampleTemplate(Samples.WellboreKind);

        Assert.Equal(OsduField.Text("data.FacilityName"), SearchFields.Classify(wellbore, "data.FacilityName").Field);
        Assert.Equal(OsduField.Text("data.NameAliases.AliasName", "data.NameAliases"), SearchFields.Classify(wellbore, "data.NameAliases.AliasName").Field);
        Assert.Equal(
            OsduField.Keyword("data.FacilitySpecifications.FacilitySpecificationText"),
            SearchFields.Classify(wellbore, "data.FacilitySpecifications.FacilitySpecificationText").Field);
        Assert.Contains("a search compares text", SearchFields.Classify(wellbore, "data.VerticalMeasurements.VerticalMeasurement").Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mapping_is_resolved_against_the_schema_its_search_pins_and_every_problem_is_listed()
    {
        var mapping = SearchSourceTests.Mapping("""
              - target: osdu.data.WellboreID
                source: search.Wellbore.id
                findBy:
                  - search.Wellbore.data.FacilityName = dataset.wb
                  - search.Wellbore.data.SpudDate = dataset.wb
                  - search.Wellbore.data.Plain.Code = dataset.wb
            """);

        var resolved = SearchSourceTests.Resolve(mapping);

        Assert.True(resolved.TryField("Wellbore", "data.FacilityName", out _, out var name));
        Assert.Equal(OsduField.Text("data.FacilityName"), name);
        Assert.Equal(2, resolved.Problems.Count);
        Assert.Contains(resolved.Problems, p => p.Contains("search.Wellbore.data.SpudDate", StringComparison.Ordinal) && p.Contains("date", StringComparison.Ordinal));
        Assert.Contains(resolved.Problems, p => p.Contains("search.Wellbore.data.Plain.Code", StringComparison.Ordinal) && p.Contains("x-osdu-indexing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_search_whose_pinned_schema_is_not_saved_is_named_with_where_to_save_it()
    {
        var mapping = SearchSourceTests.Mapping("""
              - target: osdu.data.WellboreID
                source: search.Wellbore.id
                findBy: search.Wellbore.data.FacilityName = dataset.wb
            """);

        var resolved = ResolvedSearches.Resolve(mapping, new Dictionary<Templates.TemplateReference, Snapshots.SchemaSnapshot>());

        var problem = Assert.Single(resolved.Problems);
        Assert.Contains("which is not saved", problem, StringComparison.Ordinal);
        Assert.Contains("sqlflow template import", problem, StringComparison.Ordinal);
        Assert.Empty(resolved.Searches);
    }
}
