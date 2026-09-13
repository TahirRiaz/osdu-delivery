using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Two versions of a template compared variable by variable: what was added, removed and changed, and whether each
/// difference breaks a mapping written for the older version, adds something it may use, or only reads differently.
/// </summary>
public class TemplateComparisonTests
{
    private const string Before = """
        { "type": "object", "properties": {
          "kind": { "type": "string" },
          "data": { "type": "object", "required": ["Name"], "properties": {
            "Name": { "type": "string", "description": "The name." },
            "Depth": { "type": "number" },
            "Status": { "type": "string" },
            "WellboreID": { "type": "string" },
            "Legacy": { "type": "string" },
            "Curves": { "type": "array", "items": { "type": "object", "properties": { "Mnemonic": { "type": "string" } } } } } } } }
        """;

    private const string After = """
        { "type": "object", "properties": {
          "kind": { "type": "string" },
          "data": { "type": "object", "required": ["Name", "Status", "Operator"], "properties": {
            "Name": { "type": "string", "description": "The name the operator gives it." },
            "Depth": { "type": "string" },
            "Status": { "type": "string" },
            "WellboreID": { "type": "string" },
            "Operator": { "type": "string" },
            "Alias": { "type": "string" },
            "Curves": { "type": "array", "items": { "type": "object", "properties": { "Mnemonic": { "type": "string" }, "CurveUnit": { "type": "string" } } } },
            "Location": { "type": "object", "required": ["Latitude"], "properties": { "Latitude": { "type": "number" } } } } } } }
        """;

    private static OsduTemplate Template(string json, string version)
        => OsduTemplate.From(TemplateSources.FromBundledJson(json, $"test:wks:master-data--Thing:{version}", DateTimeOffset.UnixEpoch, "test"));

    [Fact]
    public void Each_difference_is_classified_by_what_it_means_for_a_mapping_of_the_older_version()
    {
        var before = Template(Before, "1.0.0");
        var comparison = TemplateComparer.Compare(before, Template(After, "1.1.0"));

        TemplateVariableChange Change(string path) => Assert.Single(comparison.Changes, c => c.Path == path);

        var name = Change("osdu.data.Name");
        Assert.Equal((TemplateVariableChangeKind.Changed, TemplateChangeImpact.Wording), (name.Change, name.Impact));
        Assert.Equal("description", Assert.Single(name.Fields).Field);

        var depth = Change("osdu.data.Depth");
        Assert.Equal((TemplateVariableChangeKind.Changed, TemplateChangeImpact.Breaking), (depth.Change, depth.Impact));
        var type = Assert.Single(depth.Fields);
        Assert.Equal(("type", "number", "string"), (type.Field, type.Before, type.After));

        var status = Change("osdu.data.Status");
        Assert.Equal(TemplateChangeImpact.Breaking, status.Impact);
        Assert.Equal(("required", "optional", "required"), (status.Fields[0].Field, status.Fields[0].Before, status.Fields[0].After));

        Assert.Equal((TemplateVariableChangeKind.Removed, TemplateChangeImpact.Breaking), (Change("osdu.data.Legacy").Change, Change("osdu.data.Legacy").Impact));

        // A new required property of an object the older version already has breaks its mappings; a new optional one does not.
        Assert.Equal((TemplateVariableChangeKind.Added, TemplateChangeImpact.Breaking), (Change("osdu.data.Operator").Change, Change("osdu.data.Operator").Impact));
        Assert.Equal(TemplateChangeImpact.Additive, Change("osdu.data.Alias").Impact);
        Assert.Equal(TemplateChangeImpact.Additive, Change("osdu.data.Curves[].CurveUnit").Impact);

        // Under a new object, a required property is part of something new, not a demand on existing mappings.
        Assert.Equal(TemplateChangeImpact.Additive, Change("osdu.data.Location").Impact);
        Assert.Equal(TemplateChangeImpact.Additive, Change("osdu.data.Location.Latitude").Impact);

        Assert.DoesNotContain(comparison.Changes, c => c.Path is "osdu.data.WellboreID" or "osdu.data.Curves[].Mnemonic" or "osdu.kind");
        Assert.Equal(before.Variables.Count - 4, comparison.Unchanged);
        // Breaking: Depth, Status, Legacy, Operator. Additive: Alias, Curves[].CurveUnit, Location, Location.Latitude. Wording: Name.
        Assert.Equal((4, 4, 1), (comparison.Count(TemplateChangeImpact.Breaking), comparison.Count(TemplateChangeImpact.Additive), comparison.Count(TemplateChangeImpact.Wording)));

        // A removed variable is listed where it stood: after the variables before it, before the ones after it.
        var paths = comparison.Changes.Select(c => c.Path).ToList();
        Assert.True(paths.IndexOf("osdu.data.Status") < paths.IndexOf("osdu.data.Legacy"));
        Assert.True(paths.IndexOf("osdu.data.Legacy") < paths.IndexOf("osdu.data.Operator"));
    }

    [Fact]
    public void A_template_compared_with_itself_has_no_change()
    {
        var template = Template(Before, "1.0.0");
        var comparison = TemplateComparer.Compare(template, Template(Before, "1.0.0"));

        Assert.Empty(comparison.Changes);
        Assert.Equal(template.Variables.Count, comparison.Unchanged);
    }

    [Fact]
    public void The_shared_schemas_two_bundles_refer_to_pair_by_name_whatever_their_versions()
    {
        var pairs = TemplateComparer.PairReferencedFiles(
            ["master-data/Wellbore.1.3.0.json", "abstract/AbstractFacility.1.0.0.json", "abstract/AbstractLegalTags.1.0.0.json", "abstract/AbstractOld.1.0.0.json"],
            ["master-data/Wellbore.1.5.1.json", "abstract/AbstractLegalTags.1.0.0.json", "abstract/AbstractFacility.1.1.0.json", "abstract/AbstractNew.1.0.0.json"]);

        Assert.Equal(
            new[]
            {
                new ReferencedFilePair("AbstractFacility", "abstract/AbstractFacility.1.0.0.json", "abstract/AbstractFacility.1.1.0.json"),
                new ReferencedFilePair("AbstractLegalTags", "abstract/AbstractLegalTags.1.0.0.json", "abstract/AbstractLegalTags.1.0.0.json"),
                new ReferencedFilePair("AbstractNew", null, "abstract/AbstractNew.1.0.0.json"),
                new ReferencedFilePair("AbstractOld", "abstract/AbstractOld.1.0.0.json", null),
            },
            pairs);

        // A bundle that refers to two versions of one shared schema pairs them version by version.
        var both = TemplateComparer.PairReferencedFiles(
            ["master-data/Thing.1.0.0.json", "abstract/AbstractMaster.1.0.0.json", "abstract/AbstractMaster.1.1.0.json"],
            ["master-data/Thing.1.1.0.json", "abstract/AbstractMaster.1.1.0.json"]);
        Assert.Equal(
            new[]
            {
                new ReferencedFilePair("AbstractMaster.1.0.0", "abstract/AbstractMaster.1.0.0.json", null),
                new ReferencedFilePair("AbstractMaster.1.1.0", "abstract/AbstractMaster.1.1.0.json", "abstract/AbstractMaster.1.1.0.json"),
            },
            both);
    }

    [Fact]
    public void Files_that_differ_only_in_their_own_version_identifiers_say_the_same_thing()
    {
        const string before = """{ "$id": "https://schema.osdu.opengroup.org/json/master-data/Wellbore.1.0.0.json", "x-osdu-schema-source": "osdu:wks:master-data--Wellbore:1.0.0", "type": "object", "properties": { "data": { "type": "object" } } }""";
        const string renamed = """{"type":"object","x-osdu-schema-source":"osdu:wks:master-data--Wellbore:1.1.0","$id":"https://schema.osdu.opengroup.org/json/master-data/Wellbore.1.1.0.json","properties":{"data":{"type":"object"}}}""";
        const string changed = """{"type":"object","x-osdu-schema-source":"osdu:wks:master-data--Wellbore:1.1.0","$id":"https://schema.osdu.opengroup.org/json/master-data/Wellbore.1.1.0.json","properties":{"data":{"type":"object","properties":{"Name":{"type":"string"}}}}}""";

        Assert.True(TemplateComparer.SameContent(before, before.Replace(" ", string.Empty, StringComparison.Ordinal)));
        Assert.False(TemplateComparer.SameContent(before, renamed));
        Assert.True(TemplateComparer.DifferOnlyInIdentifiers(before, "osdu:wks:master-data--Wellbore:1.0.0", renamed, "osdu:wks:master-data--Wellbore:1.1.0"));
        Assert.False(TemplateComparer.DifferOnlyInIdentifiers(before, "osdu:wks:master-data--Wellbore:1.0.0", changed, "osdu:wks:master-data--Wellbore:1.1.0"));
        Assert.False(TemplateComparer.DifferOnlyInIdentifiers("not json", "osdu:wks:master-data--Wellbore:1.0.0", renamed, "osdu:wks:master-data--Wellbore:1.1.0"));
    }
}
