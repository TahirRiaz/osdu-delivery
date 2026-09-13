using System.Net;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>How a mapping addresses a template variable: <c>osdu.</c>, the record path, and <c>[]</c> for the step into an array.</summary>
public class TemplatePathTests
{
    [Fact]
    public void Parses_a_plain_path_and_a_repeated_one()
    {
        Assert.True(TemplatePath.TryParse("osdu.data.Name", out var plain, out _));
        Assert.Equal("data.Name", plain!.SchemaPath);
        Assert.False(plain.IsRepeated);
        Assert.Null(plain.Repeater);
        Assert.Equal("data", plain.Root);
        Assert.Equal("osdu.data", plain.Parent!.Text);

        Assert.True(TemplatePath.TryParse(" osdu.data.Curves[].CurveUnit ", out var repeated, out _));
        Assert.Equal("osdu.data.Curves[].CurveUnit", repeated!.Text);
        Assert.Equal("data.Curves.CurveUnit", repeated.SchemaPath);
        Assert.True(repeated.IsRepeated);
        Assert.Equal("osdu.data.Curves", repeated.Repeater!.Text);
        Assert.Equal(["CurveUnit"], repeated.WithinItem);
        Assert.Equal(repeated, TemplatePath.TryParse("osdu.data.Curves[].CurveUnit", out var again, out _) ? again : null);
    }

    [Theory]
    [InlineData(null, "a target is required")]
    [InlineData("data.Name", "must start with 'osdu.'")]
    [InlineData("osdu.data.Curves[]", "names the items of an array")]
    [InlineData("osdu.data.Curves[].Points[].X", "steps into more than one array")]
    [InlineData("osdu.data..Name", "invalid property name")]
    [InlineData("osdu.data.Na me", "invalid property name")]
    public void Refuses_what_is_not_a_template_path(string? text, string reason)
    {
        Assert.False(TemplatePath.TryParse(text, out var path, out var error));
        Assert.Null(path);
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }
}

/// <summary>The template built from an OSDU schema: every property a variable, with what the schema says about it.</summary>
public class OsduTemplateTests
{
    private static OsduTemplate WellLog() => OsduTemplate.From(Samples.SampleTemplate(Samples.WellLogKind));

    private static TemplateVariable Variable(OsduTemplate template, string path)
        => template.Find(TemplatePath.TryParse(path, out var parsed, out _) ? parsed! : throw new ArgumentException(path))
            ?? throw new Xunit.Sdk.XunitException($"the template has no variable {path}");

    [Fact]
    public void Every_property_of_the_well_log_schema_is_a_variable()
    {
        var template = WellLog();
        Assert.Equal(Samples.WellLogKind, template.Kind);
        Assert.Equal("26a3c3441882db4f", template.Version);

        // The record root, the inherited parts of data and WellLog's own properties are all there.
        foreach (var path in new[] { "osdu.acl.owners", "osdu.legal.legaltags", "osdu.data.Name", "osdu.data.ExistenceKind", "osdu.data.Datasets", "osdu.data.LogRun", "osdu.data.Curves[].Mnemonic", "osdu.data.VerticalMeasurement.VerticalMeasurementTypeID" })
        {
            Variable(template, path);
        }

        Assert.Equal(template.Variables.Count, template.Variables.Select(v => v.Path.Text).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Variables_carry_their_shape_type_requiredness_relationships_and_description()
    {
        var template = WellLog();

        var curves = Variable(template, "osdu.data.Curves");
        Assert.Equal(TemplateVariableShape.GroupList, curves.Shape);

        var unit = Variable(template, "osdu.data.Curves[].CurveUnit");
        Assert.Equal(TemplateVariableShape.Value, unit.Shape);
        Assert.Equal("string", unit.Type);
        Assert.Contains("reference-data--UnitOfMeasure", unit.Relationships);

        var wellbore = Variable(template, "osdu.data.WellboreID");
        Assert.Equal(["master-data--Wellbore"], wellbore.Relationships);

        var depth = Variable(template, "osdu.data.TopMeasuredDepth");
        Assert.Equal("number", depth.Type);
        Assert.Equal("UOM:length", depth.UnitContext);

        Assert.StartsWith("Log Run", Variable(template, "osdu.data.LogRun").Description, StringComparison.Ordinal);
        Assert.True(Variable(template, "osdu.acl").Required);
        Assert.True(Variable(template, "osdu.acl.owners").Required);
        Assert.Equal(TemplateVariableShape.ValueList, Variable(template, "osdu.acl.owners").Shape);
        Assert.Equal(TemplateVariableShape.Group, Variable(template, "osdu.data.VerticalMeasurement").Shape);
    }

    [Fact]
    public void The_engine_and_OSDU_own_their_variables_and_free_keys_and_choices_of_shape_are_marked()
    {
        var template = WellLog();
        Assert.Equal(TemplateVariableRole.Engine, Variable(template, "osdu.id").Role);
        Assert.Equal(TemplateVariableRole.Engine, Variable(template, "osdu.kind").Role);
        Assert.Equal(TemplateVariableRole.Osdu, Variable(template, "osdu.version").Role);
        Assert.Equal(TemplateVariableRole.Osdu, Variable(template, "osdu.modifyUser").Role);
        Assert.DoesNotContain(template.Fillable, v => v.Role != TemplateVariableRole.Mapping);

        var tags = Variable(template, "osdu.tags");
        Assert.Equal("string", tags.KeyValueType);
        var tag = Variable(template, "osdu.tags.WellLogNativeUID");
        Assert.Equal(TemplateVariableShape.Value, tag.Shape);
        Assert.Equal("string", tag.Type);

        Assert.Equal(TemplateVariableShape.Whole, Variable(template, "osdu.meta").Shape);

        // A list of objects inside a repeated item is listed, and marked as out of a mapping's reach.
        var reviewers = Variable(template, "osdu.data.TechnicalAssurances[].Reviewers");
        Assert.True(reviewers.Nested);
        Assert.DoesNotContain(template.Fillable, v => v.Path.Text == "osdu.data.TechnicalAssurances[].Reviewers");
    }
}

/// <summary>The template store over the catalog: immutable versions, pinned by mappings.</summary>
public sealed class CatalogTemplateStoreTests : IDisposable
{
    private readonly SqliteCatalog _db = new();

    public void Dispose() => _db.Dispose();

    private static SchemaSnapshot Variant(string property)
        => SchemaSnapshot.Parse(TestSchema.Kind, TestSchema.Build().Root.ToJsonString().Replace("\"Symbol\"", "\"" + property + "\"", StringComparison.Ordinal), DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Saving_the_same_schema_again_changes_nothing_and_a_different_schema_is_a_new_version()
    {
        var store = _db.Templates();
        var first = await store.SaveAsync(TestSchema.Build(), "file thing.json", "tests");
        Assert.Equal(TemplateSaveOutcome.Created, first.Outcome);
        Assert.Equal(TestSchema.Template, first.Template.Reference);

        var again = await store.SaveAsync(TestSchema.Build(), "file thing.json", "someone else");
        Assert.Equal(TemplateSaveOutcome.Unchanged, again.Outcome);
        Assert.Equal("tests", again.Template.CapturedBy);

        var changed = Variant("Mark");
        var second = await store.SaveAsync(changed, "OSDU https://osdu.example.test", "tests");
        Assert.Equal(TemplateSaveOutcome.Created, second.Outcome);
        Assert.NotEqual(first.Template.Version, second.Template.Version);

        var listed = await store.ListAsync();
        Assert.Equal(2, listed.Count);
        Assert.All(listed, t => Assert.Equal(TestSchema.Kind, t.Kind));

        var loaded = await _db.Templates().LoadAsync(TestSchema.Template);
        Assert.NotNull(loaded);
        Assert.Equal(TestSchema.Build().Version, loaded.Version);
        Assert.Equal(SchemaType.Number, loaded.Resolve("data.Depth")!.Type);
        Assert.Null(await store.LoadAsync(new TemplateReference(TestSchema.Kind, "0000000000000000")));
    }

    [Fact]
    public async Task A_version_a_synced_mapping_pins_cannot_be_deleted()
    {
        var store = _db.Templates();
        await store.SaveAsync(TestSchema.Build(), "file thing.json", "tests");
        await using (var db = _db.CreateDbContext())
        {
            db.DeliveryMappings.Add(new DeliveryMapping
            {
                Id = Guid.NewGuid(),
                RepoId = Guid.NewGuid(),
                Reference = "Thing@1.0.0",
                Name = "Thing",
                Version = "1.0.0",
                Kind = TestSchema.Kind,
                TemplateVersion = TestSchema.Build().Version,
                RelativePath = "mappings/Thing@1.0.0.yaml",
                ContentHash = new string('0', 64),
                Yaml = "documentType: mapping",
                SummaryJson = "{}",
                Status = "valid",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var refused = await Assert.ThrowsAsync<DeliveryException>(() => store.DeleteAsync(TestSchema.Template));
        Assert.Contains("pinned by mapping(s) Thing@1.0.0", refused.Message, StringComparison.Ordinal);

        await using (var db = _db.CreateDbContext())
        {
            await db.DeliveryMappings.ExecuteDeleteAsync();
        }

        await store.DeleteAsync(TestSchema.Template);
        Assert.Null(await store.LoadAsync(TestSchema.Template));
        await Assert.ThrowsAsync<DeliveryException>(() => store.DeleteAsync(TestSchema.Template));
    }

    [Fact]
    public async Task A_template_whose_stored_schema_no_longer_hashes_to_its_version_is_refused()
    {
        await _db.Templates().SaveAsync(TestSchema.Build(), "file thing.json", "tests");
        await using (var db = _db.CreateDbContext())
        {
            var row = await db.DeliveryTemplates.AsTracking().SingleAsync();
            row.SchemaJson = row.SchemaJson.Replace("\"Symbol\"", "\"Tampered\"", StringComparison.Ordinal);
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => _db.Templates().LoadAsync(TestSchema.Template));
        Assert.Contains("damaged", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>Where a template's schema comes from: a bundled file, or OSDU's schema service.</summary>
public class TemplateSourcesTests
{
    private const string Kind = "test:wks:work-product-component--Thing:1.0.0";

    [Fact]
    public void The_sample_schemas_are_saved_as_the_versions_the_sample_mappings_pin()
    {
        Assert.Equal("26a3c3441882db4f", Samples.SampleTemplate(Samples.WellLogKind).Version);
        Assert.Equal("a110ad82c3b60a1e", Samples.SampleTemplate(Samples.WellboreKind).Version);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""{"type":"object","properties":{"data":{"$ref":"osdu:wks:AbstractCommon:1.0.0"}}}""", "outside itself")]
    [InlineData("""{"type":"object","properties":{"data":{"$ref":"#/definitions/Missing"}},"definitions":{}}""", "does not contain")]
    [InlineData("""{"x-osdu-schema-source":"test:wks:work-product-component--Other:1.0.0","type":"object","properties":{"data":{"type":"object"}}}""", "describes 'test:wks:work-product-component--Other:1.0.0'")]
    [InlineData("""{"type":"object","properties":{"acl":{"type":"object"}}}""", "declares no 'data' property")]
    public void A_bundled_file_that_is_not_a_record_schema_is_refused(string json, string reason)
    {
        var ex = Assert.Throws<DeliveryException>(() => TemplateSources.FromBundledJson(json, Kind, DateTimeOffset.UnixEpoch, "upload"));
        Assert.StartsWith("upload:", ex.Message, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Searching_OSDU_asks_the_schema_service_and_reads_what_it_lists()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/api/schema-service/v1/schema", HttpStatusCode.OK, """
            {
              "schemaInfos": [
                {
                  "schemaIdentity": { "authority": "osdu", "source": "wks", "entityType": "master-data--Wellbore", "schemaVersionMajor": 1, "schemaVersionMinor": 3, "schemaVersionPatch": 0, "id": "osdu:wks:master-data--Wellbore:1.3.0" },
                  "status": "PUBLISHED", "scope": "SHARED", "dateCreated": "2024-01-01T00:00:00Z", "createdBy": "someone"
                },
                { "schemaIdentity": { "authority": "osdu", "source": "wks", "entityType": "master-data--Well", "schemaVersionMajor": 1, "schemaVersionMinor": 2, "schemaVersionPatch": 0 } }
              ],
              "offset": 0, "count": 2, "totalCount": 7
            }
            """);
        using var osdu = await Connect(handler);

        var found = await TemplateSources.SearchAsync(osdu, new OsduSchemaQuery { Authority = "osdu", EntityType = "master-data--Wellbore", Limit = 50 });

        var request = Assert.Single(handler.Calls);
        Assert.Contains("authority=osdu", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("entityType=master-data--Wellbore", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("latestVersion=true", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("limit=50", request.Uri.Query, StringComparison.Ordinal);
        Assert.Equal(7, found.TotalCount);
        Assert.Equal(["osdu:wks:master-data--Wellbore:1.3.0", "osdu:wks:master-data--Well:1.2.0"], found.Schemas.Select(s => s.Kind));
        Assert.Equal("PUBLISHED", found.Schemas[0].Status);
        await Assert.ThrowsAsync<DeliveryException>(() => TemplateSources.SearchAsync(osdu, new OsduSchemaQuery { Limit = 500 }));
    }

    [Fact]
    public async Task Fetching_a_schema_resolves_every_schema_it_refers_to()
    {
        var handler = new FakeHttpHandler()
            .OnMatch(r => Uri.UnescapeDataString(r.RequestUri!.AbsolutePath).EndsWith("/schema/" + Kind, StringComparison.Ordinal), _ => FakeHttpHandler.Json(HttpStatusCode.OK, """
                { "x-osdu-schema-source": "test:wks:work-product-component--Thing:1.0.0", "type": "object",
                  "properties": { "acl": { "$ref": "osdu:wks:AbstractAccessControlList:1.0.0" }, "data": { "type": "object", "properties": { "Name": { "type": "string" } } } } }
                """))
            .OnMatch(r => Uri.UnescapeDataString(r.RequestUri!.AbsolutePath).EndsWith("/schema/osdu:wks:AbstractAccessControlList:1.0.0", StringComparison.Ordinal), _ => FakeHttpHandler.Json(HttpStatusCode.OK, """
                { "type": "object", "properties": { "owners": { "type": "array", "items": { "type": "string" } } } }
                """));
        using var osdu = await Connect(handler);

        var schema = await TemplateSources.FetchAsync(osdu, Kind, new TestClock());

        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(SchemaType.Array, schema.Resolve("acl.owners")!.Type);
        var template = OsduTemplate.From(schema);
        Assert.NotNull(template.Find(TemplatePath.TryParse("osdu.data.Name", out var name, out _) ? name! : throw new InvalidOperationException()));
    }

    private static Task<OsduConnection> Connect(FakeHttpHandler handler)
        => OsduConnection.CreateAsync(
            "https://osdu.example.test", new TargetAuth { Type = TargetAuthType.None }, new Dictionary<string, string> { ["data-partition-id"] = "opendes" },
            new FlowReliability(), new SecretResolver([new EnvSecretProvider()]), handler);
}
