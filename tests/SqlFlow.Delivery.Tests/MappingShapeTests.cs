using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The shape of the records a mapping renders (docs/delivery/mapping-templates.md, "The record shape"): drawn by the
/// renderer's own assembly with placeholders in place of row and cache values, so its layout is the layout a render writes.
/// </summary>
public class MappingShapeTests
{
    private static readonly Dictionary<string, string> Dev = new(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "dev" };

    [Fact]
    public void Draws_typed_placeholders_static_values_and_one_item_per_repeater()
    {
        var mapping = TestSchema.Mapping("""
              - target: osdu.data.Unit
                source: cache.UnitOfMeasure.id
                findBy:
                  - cache.UnitOfMeasure.Code = dataset.unit
                  - cache.UnitOfMeasure.Name = dataset.unit
                modifiers: [trim]
              - target: osdu.data.IsRegular
                source: dataset.flag
                modifiers:
                  - equals: regular
                required: false
              - target: osdu.data.When
                source: dataset.when
                modifiers:
                  - date: dd.MM.yyyy
                appliesWhen: dataset.flag is REGULAR
              - target: osdu.data.Aliases
                source: dataset.alias
              - target: osdu.tags.Source
                static: "{param.dataPartition}-test"
              - target: osdu.data.Curves
                source: dataset.curves
              - target: osdu.data.Curves[].CurveID
                source: dataset.curves.curve_id
              - target: osdu.data.Curves[].TopDepth
                source: dataset.curves.top
            """);

        var shape = MappingRenderer.Shape(mapping, TestSchema.Build(), Dev);
        var document = shape.Document;

        Assert.Equal(["id", "kind"], document.Select(p => p.Key).Take(2));
        Assert.Equal("data", document.Last().Key);
        Assert.Equal("dev:work-product-component--Thing:<delivery key from test, dataset.name>", document["id"]!.GetValue<string>());
        Assert.Equal(TestSchema.Kind, document["kind"]!.GetValue<string>());
        Assert.Equal("owners@x", document["acl"]!["owners"]![0]!.GetValue<string>());
        Assert.Equal("dev-test", document["tags"]!["Source"]!.GetValue<string>());

        var data = document["data"]!;
        Assert.Equal("<string from dataset.name>", data["Name"]!.GetValue<string>());
        Assert.Equal("<number from dataset.depth>", data["Depth"]!.GetValue<string>());
        Assert.Equal("<string from cache.UnitOfMeasure.id by Code/Name = (dataset.unit | trim)>", data["Unit"]!.GetValue<string>());
        Assert.Equal("<boolean from dataset.flag | equals(regular), optional>", data["IsRegular"]!.GetValue<string>());
        Assert.Equal("<date-time string from dataset.when | date(dd.MM.yyyy), when dataset.flag is REGULAR>", data["When"]!.GetValue<string>());
        Assert.Equal("<string from dataset.alias>", Assert.Single(data["Aliases"]!.AsArray())!.GetValue<string>());

        var curve = Assert.Single(data["Curves"]!.AsArray())!;
        Assert.Equal("<string from dataset.curves.curve_id>", curve["CurveID"]!.GetValue<string>());
        Assert.Equal("<number from dataset.curves.top>", curve["TopDepth"]!.GetValue<string>());
        Assert.Equal(["osdu.data.Curves: one item per row of dataset.curves; a record without any is held"], shape.Notes);
    }

    [Fact]
    public void A_parameter_without_a_value_shows_its_token_where_a_render_would_refuse_to_start()
    {
        var mapping = TestSchema.Mapping("  - { target: osdu.tags.Source, static: \"{param.dataPartition}-test\" }");
        var none = new Dictionary<string, string>(StringComparer.Ordinal);

        var shape = MappingRenderer.Shape(mapping, TestSchema.Build(), none);
        Assert.StartsWith("{param.dataPartition}:work-product-component--Thing:", shape.Document["id"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("{param.dataPartition}-test", shape.Document["tags"]!["Source"]!.GetValue<string>());
        Assert.Contains(shape.Notes, n => n.StartsWith("parameter 'dataPartition' has no value", StringComparison.Ordinal));

        var context = TestSchema.Context() with { Parameters = none };
        Assert.Throws<FlowValidationException>(() => new MappingRenderer(mapping, TestSchema.Build(), TestSchema.References(), context));
    }

    [Fact]
    public void A_partition_that_is_not_an_id_segment_is_drawn_and_noted()
    {
        var shape = MappingRenderer.Shape(
            TestSchema.Mapping(), TestSchema.Build(), new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "bad partition" });

        Assert.StartsWith("bad partition:", shape.Document["id"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(shape.Notes, n => n.Contains("not a valid OSDU id segment", StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_value_on_an_object_is_noted_and_left_out_as_a_render_holds_it()
    {
        var shape = MappingRenderer.Shape(TestSchema.Mapping("  - { target: osdu.data.Nested, source: dataset.name }"), TestSchema.Build(), Dev);

        Assert.Null(shape.Document["data"]!["Nested"]);
        Assert.Contains("osdu.data.Nested: a single value from dataset.name cannot be written where the template takes an object", shape.Notes);
    }

    /// <summary>
    /// Every property the sample fixtures render is in the shape and the shape has nothing they do not: between them the
    /// fixtures fill every entry of the sample mappings, so the two layouts must agree path for path.
    /// </summary>
    [Theory]
    [InlineData("WellLog@1.4.0.yaml")]
    [InlineData("Wellbore@1.0.0.yaml")]
    public void The_shape_of_a_sample_mapping_lays_out_what_its_fixtures_render(string file)
    {
        var mapping = new DeliveryDocumentLoader().LoadMapping(Path.Combine(Samples.Mappings, file));
        var fixture = mapping.Fixtures[0];
        var shape = MappingRenderer.Shape(mapping, Samples.SampleTemplate(mapping.Kind), fixture.Parameters);

        var rendered = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var each in mapping.Fixtures)
        {
            rendered.UnionWith(Paths(JsonNode.Parse(each.Expected)));
        }

        Assert.Equal(rendered, Paths(shape.Document));
        Assert.DoesNotContain(shape.Notes, n => n.StartsWith("parameter", StringComparison.Ordinal));
        Assert.StartsWith(fixture.Parameters[RenderContext.DataPartitionParameter] + ":" + mapping.EntityType + ":<delivery key", shape.Document["id"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private static SortedSet<string> Paths(JsonNode? node)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        Collect(node, string.Empty, paths);
        return paths;
    }

    private static void Collect(JsonNode? node, string path, SortedSet<string> paths)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    Collect(child, path.Length == 0 ? name : path + "." + name, paths);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Collect(item, path + "[]", paths);
                }

                break;
            default:
                paths.Add(path);
                break;
        }
    }
}
