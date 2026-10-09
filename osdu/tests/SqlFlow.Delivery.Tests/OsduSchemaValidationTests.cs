using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The checks against the schemas OSDU publishes, as the suites carry them from the data definitions: every document the
/// sample estate renders meets the template its mapping pins, as the route sends it; and a wellbore record that meets the
/// Wellbore 1.3.0 schema stops meeting it, at the place and by the rule OSDU's own schema states, when any one of the
/// envelope's, the data's, the meta items' or the relationships' rules is broken.
/// </summary>
public sealed class OsduSchemaValidationTests : IDisposable
{
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";

    private static readonly SchemaRules Wellbore = SchemaRules.Of(Samples.SampleTemplate(WellboreKind));

    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private static JsonObject WellboreRecord() => JsonNode.Parse("""
        {
          "id": "dev:master-data--Wellbore:WB-1",
          "kind": "osdu:wks:master-data--Wellbore:1.3.0",
          "acl": { "owners": ["data.default.owners@dev.example.com"], "viewers": ["data.default.viewers@dev.example.com"] },
          "legal": { "legaltags": ["dev-public"], "otherRelevantDataCountries": ["US"], "status": "compliant" },
          "tags": { "DeliveredBy": "osdu-delivery" },
          "meta": [
            {
              "kind": "Unit",
              "name": "m",
              "persistableReference": "{\"abcd\":{\"a\":0.0,\"b\":1.0,\"c\":1.0,\"d\":0.0},\"symbol\":\"m\",\"baseMeasurement\":{\"ancestry\":\"L\",\"type\":\"UM\"},\"type\":\"UAD\"}",
              "unitOfMeasureID": "dev:reference-data--UnitOfMeasure:m:",
              "propertyNames": ["VerticalMeasurements[].VerticalMeasurement"]
            }
          ],
          "data": {
            "FacilityName": "WB 1/1-1",
            "WellID": "dev:master-data--Well:WB-1:",
            "SequenceNumber": 1,
            "VerticalMeasurements": [
              { "VerticalMeasurementID": "KB", "VerticalMeasurement": 25.0, "VerticalMeasurementTypeID": "dev:reference-data--VerticalMeasurementType:KB:" }
            ],
            "NameAliases": [ { "AliasName": "WB-1", "AliasNameTypeID": "dev:reference-data--AliasNameType:Name:" } ],
            "GeoContexts": [
              { "GeoPoliticalEntityID": "dev:master-data--GeoPoliticalEntity:US:", "GeoTypeID": "dev:reference-data--GeoPoliticalEntityType:Country:" }
            ],
            "ExtensionProperties": { "anything": ["goes", 1, { "here": true }] }
          }
        }
        """)!.AsObject();

    [Fact]
    public async Task Every_document_the_sample_estate_renders_meets_the_schema_its_mapping_pins_as_its_route_sends_it()
    {
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        var engine = Samples.Engine(ledger: null, _clock, sources: tables);
        using var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        runtime.Selection = SourceSelection.Full();

        var plan = await runtime.PlanAsync();
        var rules = SchemaRules.Of(runtime.Mapping.Schema);

        Assert.Equal(SampleEstate.Logs().Count, plan.Entries.Count);
        foreach (var entry in plan.Entries)
        {
            var document = entry.Render!.Document;
            var found = RecordValidator.Check(document, rules, ValidationGate.RouteFilled(runtime.Flow, document));
            Assert.True(
                found.ProblemCount == 0 && found.UnverifiedCount == 0,
                $"{entry.SourceKey}: " + string.Join("; ", found.Problems.Concat(found.Unverified).Select(p => $"{p.Path} {p.Rule}: {p.Message}")));
            Assert.NotEmpty(found.References);
        }
    }

    [Fact]
    public void A_wellbore_meeting_the_published_schema_has_no_problem_and_refers_to_its_well_and_reference_data()
    {
        var found = RecordValidator.Check(WellboreRecord(), Wellbore);

        Assert.Empty(found.Problems);
        Assert.Equal(0, found.UnverifiedCount);
        Assert.Contains(found.References, r => r.Id == "dev:master-data--Well:WB-1:" && r.At == "data.WellID");
        Assert.Contains(found.References, r => r.Id == "dev:reference-data--UnitOfMeasure:m:" && r.At == "meta[].unitOfMeasureID");
        Assert.Contains(found.References, r => r.EntityType == "master-data--GeoPoliticalEntity" && r.Path == "data.GeoContexts[0].GeoPoliticalEntityID");
    }

    [Theory]
    [MemberData(nameof(WellboreBreaks))]
    public void A_wellbore_breaking_one_rule_of_the_published_schema_is_found_where_and_by_the_rule_the_schema_states(string change, string rule, string path)
    {
        var found = RecordValidator.Check(Changed(change), Wellbore);

        var problem = Assert.Single(found.Problems);
        Assert.Equal(rule, problem.Rule);
        Assert.Equal(path, problem.Path);
    }

    public static TheoryData<string, string, string> WellboreBreaks() => new()
    {
        { "id=\"dev:master-data--Well:WB-1\"", "pattern", "id" },
        { "kind=\"osdu:wks:master-data--Wellbore\"", "pattern", "kind" },
        { "-acl", "required", "acl" },
        { "acl.owners=[\"owners at dev\"]", "pattern", "acl.owners[0]" },
        { "acl.other=[]", "additionalProperties", "acl.other" },
        { "-legal.otherRelevantDataCountries", "required", "legal.otherRelevantDataCountries" },
        { "legal.otherRelevantDataCountries=[\"United States\"]", "pattern", "legal.otherRelevantDataCountries[0]" },
        { "legal.status=\"pending\"", "pattern", "legal.status" },
        { "tags.Count=1", "type", "tags.Count" },
        { "source=\"welldb\"", "additionalProperties", "source" },
        { "version=\"one\"", "type", "version" },
        { "meta=[{\"kind\":\"Unit\",\"name\":\"m\"}]", "anyOf", "meta[0]" },
        { "data.FacilityName=12", "type", "data.FacilityName" },
        { "data.WellID=\"dev:master-data--Wellbore:WB-1:\"", "relationship", "data.WellID" },
        { "data.SequenceNumber=1.5", "type", "data.SequenceNumber" },
        { "data.VerticalMeasurements=[{\"VerticalMeasurement\":\"high\"}]", "type", "data.VerticalMeasurements[0].VerticalMeasurement" },
        { "data.VerticalMeasurements=[{\"VerticalMeasurementTypeID\":\"dev:reference-data--UnitOfMeasure:m:\"}]", "relationship", "data.VerticalMeasurements[0].VerticalMeasurementTypeID" },
        { "data.NameAliases=[{\"AliasName\":\"x\",\"AliasNameTypeID\":\"Name\"}]", "relationship", "data.NameAliases[0].AliasNameTypeID" },
        { "data.NameAliases=[{\"AliasName\":\"x\",\"EffectiveDateTime\":\"yesterday\"}]", "format", "data.NameAliases[0].EffectiveDateTime" },
        { "data.ExtensionProperties=[]", "type", "data.ExtensionProperties" },
    };

    [Fact]
    public void A_wellbore_naming_an_organisation_of_either_entity_type_a_relationship_allows_meets_it_and_any_other_does_not()
    {
        Assert.Empty(RecordValidator.Check(Changed("data.NameAliases=[{\"AliasName\":\"x\",\"DefinitionOrganisationID\":\"dev:master-data--Organisation:Operator-A:\"}]"), Wellbore).Problems);
        Assert.Empty(RecordValidator.Check(Changed("data.NameAliases=[{\"AliasName\":\"x\",\"DefinitionOrganisationID\":\"dev:reference-data--StandardsOrganisation:PPDM:\"}]"), Wellbore).Problems);

        var problem = Assert.Single(RecordValidator.Check(Changed("data.NameAliases=[{\"AliasName\":\"x\",\"DefinitionOrganisationID\":\"dev:master-data--Field:F:\"}]"), Wellbore).Problems);
        Assert.Equal("relationship", problem.Rule);
        Assert.Contains("reference-data--StandardsOrganisation or master-data--Organisation", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wellbore_whose_route_fills_its_dataset_list_is_judged_without_it()
    {
        var record = Changed("data.Datasets=[\"<dataset id>\"]");

        // The Wellbore schema has no Datasets, and allows no property it does not name at the root alone, so data takes it.
        Assert.Empty(RecordValidator.Check(record, Wellbore).Problems);
    }

    private static JsonObject Changed(string change)
    {
        var record = WellboreRecord();
        RecordValidatorTests.Apply(record, change);
        return record;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory a reader still holds open is left for the operating system to reclaim.
        }

        GC.SuppressFinalize(this);
    }
}
