using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>What a mapping covers of its template: the state of every variable, and what it leaves required and empty.</summary>
public class MappingCoverageTests
{
    private static CoverageReport Cover(string data = "", string baseData = TestSchema.BaseData, string record = "")
        => MappingCoverage.Of(TestSchema.Mapping(data, baseData: baseData, record: record), OsduTemplate.From(TestSchema.Build()));

    /// <summary>The same mapping over a schema the test changed: the document pins whatever version the change makes.</summary>
    private static CoverageReport Cover(SchemaSnapshot schema, string data)
    {
        var yaml = TestSchema.MappingDocument(data).Replace($"version: {TestSchema.Build().Version}", $"version: {schema.Version}", StringComparison.Ordinal);
        return MappingCoverage.Of(new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml"), OsduTemplate.From(schema));
    }

    /// <summary>The test schema with one object made to require a property of its own, deeper than the gate looks.</summary>
    private static SchemaSnapshot Requiring(string find, string replace)
        => SchemaSnapshot.Parse(TestSchema.Kind, TestSchema.Build().Root.ToJsonString().Replace(find, replace, StringComparison.Ordinal), DateTimeOffset.UnixEpoch);

    private static VariableCoverage Variable(CoverageReport report, string target)
        => Assert.Single(report.Variables, v => v.Target == target);

    private static void Has(CoverageReport report, IssueSeverity severity, string text)
        => Assert.Contains(report.Issues, i => i.Severity == severity && i.Message.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void An_entry_fills_its_variable_and_every_object_on_the_way_to_it()
    {
        var report = Cover();

        // A static value and a required entry (the default) reach the record on every row.
        Assert.Equal(CoverageState.Always, Variable(report, "osdu.acl.owners").State);
        Assert.True(Variable(report, "osdu.acl.owners").Direct);
        Assert.Equal(CoverageState.Always, Variable(report, "osdu.data.Name").State);

        // An object with no entry of its own is filled by what it holds, and says so.
        var acl = Variable(report, "osdu.acl");
        Assert.Equal(CoverageState.Always, acl.State);
        Assert.False(acl.Direct);
        Assert.True(acl.Required);

        // A variable no entry names, and whose holders nothing fills, is left out of the record.
        Assert.Equal(CoverageState.Empty, Variable(report, "osdu.data.Symbol").State);
        Assert.Equal(CoverageState.Empty, Variable(report, "osdu.data.Curves").State);
        Assert.Equal(CoverageState.Empty, Variable(report, "osdu.tags").State);
    }

    [Fact]
    public void An_entry_that_may_be_left_out_fills_its_variable_only_sometimes()
    {
        Assert.Equal(CoverageState.Sometimes, Variable(Cover("Symbol: { $from: sym, $required: false }"), "osdu.data.Symbol").State);
        Assert.Equal(
            CoverageState.Sometimes,
            Variable(Cover("Symbol: { $from: sym, $when: flag = \"yes\" }"), "osdu.data.Symbol").State);

        // An object reads from what it holds: an array whose own entry may be left out still carries items whose
        // properties are always written, so the branch reads as filled and only the entry itself is optional.
        var repeated = Cover("""
            Curves:
              $forEach: curves
              $when: flag = "yes"
              $item:
                CurveID: { $from: curve_id }
            """);
        Assert.Equal(CoverageState.Always, Variable(repeated, "osdu.data.Curves").State);
        Assert.True(Variable(repeated, "osdu.data.Curves").Direct);
        Assert.Equal(CoverageState.Always, Variable(repeated, "osdu.data.Curves[].CurveID").State);

        // With nothing inside it always written, the array is only as good as what it holds: an item property that may be
        // left out makes the conditional array reach only some rows.
        var optionalItems = Cover("""
            Curves:
              $forEach: curves
              $when: flag = "yes"
              $item:
                CurveID: { $from: curve_id, $required: false }
            """);
        Assert.Equal(CoverageState.Sometimes, Variable(optionalItems, "osdu.data.Curves").State);
    }

    [Fact]
    public void An_object_is_only_as_good_as_the_weakest_thing_it_promises()
    {
        // One property filled on some rows only makes the object holding it that, however its siblings are filled.
        var partly = Cover("""
            Curves:
              $forEach: curves
              $item:
                CurveID: { $from: curve_id }
                TopDepth: { $from: top, $required: false }
            """);
        Assert.Equal(CoverageState.Sometimes, Variable(partly, "osdu.data.Curves").State);
        Assert.Equal(CoverageState.Always, Variable(partly, "osdu.data.Curves[].CurveID").State);

        // A property nothing fills and nothing requires promises nothing, so it does not drag its object down.
        Assert.Equal(CoverageState.Empty, Variable(partly, "osdu.data.Symbol").State);
        Assert.Equal(CoverageState.Always, Variable(Cover(), "osdu.acl").State);

        // A property the schema requires and nothing fills makes the object holding it missing, filled siblings and all.
        var missing = Requiring("\"TopDepth\":{\"type\":\"number\"}}}", "\"TopDepth\":{\"type\":\"number\"}},\"required\":[\"TopDepth\"]}");
        var incomplete = Cover(missing, """
            Curves:
              $forEach: curves
              $item:
                CurveID: { $from: curve_id }
            """);
        Assert.Equal(CoverageState.Empty, Variable(incomplete, "osdu.data.Curves").State);
        Assert.Equal(CoverageState.Always, Variable(incomplete, "osdu.data.Curves[].CurveID").State);

        // The finding still names the property nothing writes, not the object beside it that is filled.
        Has(incomplete, IssueSeverity.Warning, "requires osdu.data.Curves[].TopDepth, which the mapping does not fill");
        Assert.DoesNotContain(incomplete.Issues, i => i.Target == "osdu.data.Curves");
    }

    [Fact]
    public void Only_the_variables_a_mapping_may_fill_are_covered()
    {
        var report = Cover();
        Assert.DoesNotContain(report.Variables, v => v.Target is "osdu.id" or "osdu.kind" or "osdu.version" or "osdu.createTime");
        Assert.Contains(report.Variables, v => v.Target == "osdu.data.Depth");
    }

    [Fact]
    public void An_entry_filling_a_free_key_is_covered_under_the_object_that_takes_them()
    {
        // osdu.tags takes keys of its own, so an entry fills a target the template has no variable for.
        var report = Cover(record: "tags: { DeliveredBy: osdu-delivery }");
        var key = Variable(report, "osdu.tags.DeliveredBy");
        Assert.Equal(CoverageState.Always, key.State);
        Assert.True(key.Direct);
        Assert.False(key.Required);

        // The object holding the keys reads as filled through them, as any object filled by what it holds does.
        Assert.Equal(CoverageState.Always, Variable(report, "osdu.tags").State);
        Assert.False(Variable(report, "osdu.tags").Direct);
        Assert.Equal(CoverageState.Empty, Variable(Cover(), "osdu.tags").State);
    }

    [Fact]
    public void What_the_gate_stops_a_delivery_for_is_an_error_and_what_it_does_not_check_is_a_warning()
    {
        var report = Cover(baseData: "Name: { $from: name }");

        // The gate's own rule, unchanged: a required property of data that nothing fills stops a delivery, and the finding
        // now names the variable it is about.
        Has(report, IssueSeverity.Error, "requires osdu.data.Depth, which the mapping does not fill");
        Assert.Equal("osdu.data.Depth", Assert.Single(report.Issues, i => i.Severity == IssueSeverity.Error).Target);

        // A property required deeper than the gate looks is a warning: the delivery is not stopped for it today.
        var nested = Requiring("\"Inner\":{\"type\":\"string\"}}}", "\"Inner\":{\"type\":\"string\"}},\"required\":[\"Inner\"]}");
        var optional = Cover(nested, "Nested: { Inner: { $from: name, $required: false } }");
        Has(optional, IssueSeverity.Warning, "requires osdu.data.Nested.Inner, and what fills it may leave it out");
        Assert.DoesNotContain(optional.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.Equal(CoverageState.Sometimes, Variable(optional, "osdu.data.Nested").State);
    }

    [Fact]
    public void A_property_required_inside_an_object_nothing_fills_is_not_missing()
    {
        // A schema that requires a property of an item of Curves, which the base mapping does not fill at all.
        var schema = Requiring("\"TopDepth\":{\"type\":\"number\"}}}", "\"TopDepth\":{\"type\":\"number\"}},\"required\":[\"CurveID\"]}");

        Assert.True(Variable(Cover(schema, string.Empty), "osdu.data.Curves[].CurveID").Required);
        Assert.DoesNotContain(Cover(schema, string.Empty).Issues, i => i.Target == "osdu.data.Curves[].CurveID");

        // Once the array is filled, the property it requires is missing from every item it renders.
        Has(
            Cover(schema, "Curves: { $forEach: curves, $item: { TopDepth: { $from: top } } }"),
            IssueSeverity.Warning,
            "requires osdu.data.Curves[].CurveID, which the mapping does not fill");
    }

    [Fact]
    public async Task The_sample_mappings_cover_their_templates_with_nothing_required_left_empty()
    {
        foreach (var reference in new[] { "WellLog@1.4.0", "Wellbore@1.0.0" })
        {
            var mapping = new MappingCatalog(Samples.FixtureMappings, new DeliveryDocumentLoader()).Load(reference);
            var schema = await Samples.SampleTemplates.LoadAsync(mapping.Template);
            Assert.NotNull(schema);

            var report = MappingCoverage.Of(mapping, OsduTemplate.From(schema));
            Assert.DoesNotContain(report.Issues, i => i.Severity == IssueSeverity.Error);
            Assert.Contains(report.Variables, v => v.State == CoverageState.Always);
            Assert.Contains(report.Variables, v => v.State == CoverageState.Empty);
        }
    }
}
