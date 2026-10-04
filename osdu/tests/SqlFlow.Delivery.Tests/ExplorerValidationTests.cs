using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;
using static SqlFlow.Delivery.Tests.ValidationFixtures;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The explorer's checks of what OSDU holds against what OSDU expects (osdu/docs/explorer.md, Validate): a kind's schema
/// read from the partition's Schema service and bundled, whatever references it makes to other schemas; one record checked
/// at its latest or a version, against that schema or a saved template, with the records it refers to looked up in
/// storage; and the records a search finds checked up to a bound, counted by outcome and rule.
/// </summary>
public sealed class ExplorerValidationTests : IDisposable
{
    private readonly FakeOsduPlatform _platform = new();
    private readonly HttpRuntime _runtime;
    private readonly OsduHttpClient _client;
    private readonly TestClock _clock = new();

    public ExplorerValidationTests()
    {
        _runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), _clock, _platform, allowLoopback: true);
        _client = new OsduHttpClient(
            _runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
    }

    /// <summary>Saved templates, read only.</summary>
    private sealed class SavedTemplates(params SchemaSnapshot[] schemas) : ITemplateStore
    {
        public Task<SchemaSnapshot?> LoadAsync(TemplateReference reference, CancellationToken ct = default)
            => Task.FromResult(schemas.FirstOrDefault(s => s.Kind == reference.Kind && s.Version == reference.Version));

        public Task<TemplateSaved> SaveAsync(SchemaSnapshot schema, string origin, string actor, CancellationToken ct = default)
            => throw new NotSupportedException("The explorer only reads saved templates.");

        public Task<IReadOnlyList<TemplateInfo>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TemplateInfo>>(schemas.Select(s => new TemplateInfo(s.Kind, s.Version, s.CapturedUtc.UtcDateTime, "tests", "memory")).ToList());

        public Task DeleteAsync(TemplateReference reference, CancellationToken ct = default)
            => throw new NotSupportedException("The explorer only reads saved templates.");
    }

    private ExplorerChecks Checks(ITemplateStore? templates = null)
        => new(_client, new OsduRecordProtocol(_client, new ProtocolOptions(), _clock), OsduRecordProtocol.DefaultVerifyBatchPath, templates, _clock);

    private SchemaServiceReader Reader() => new(_client, _clock);

    /// <summary>Puts the fixture's record and the three records it refers to in storage, and its schema in the Schema service.</summary>
    private string Stored(Action<JsonObject>? change = null, bool references = true)
    {
        var record = change is null ? ValidRecord() : Record(change);
        _platform.Put(record);
        _platform.Schemas[Kind] = JsonNode.Parse(SchemaJson)!.AsObject();
        if (references)
        {
            foreach (var id in new[] { "dev:master-data--Well:W-1", "dev:reference-data--MeasurementType:KB", "dev:master-data--Field:F-1" })
            {
                _platform.Put(FakeOsduPlatform.Record(id, "osdu:wks:" + id.Split(':')[1] + ":1.0.0"));
            }
        }

        return record["id"]!.GetValue<string>();
    }

    private static ValidationVerdict VerdictOf(ExplorerValidation answer) => ValidationVerdict.FromJson(answer.Verdict) ?? throw new InvalidOperationException(answer.Problem);

    [Fact]
    public async Task A_schema_the_service_holds_bundled_is_read_as_it_is()
    {
        _platform.Schemas[Kind] = JsonNode.Parse(SchemaJson)!.AsObject();

        var read = await Reader().ReadAsync(Kind);

        Assert.NotNull(read.Schema);
        Assert.Equal([Kind], read.Read);
        Assert.Empty(read.Unresolved);
        Assert.Equal(Schema.Version, read.Schema!.Version);
        Assert.Empty(RecordValidator.Check(ValidRecord(), SchemaRules.Of(read.Schema)).Problems);
    }

    [Fact]
    public async Task A_schema_referring_to_other_schemas_by_id_reads_each_once_and_bundles_them_with_their_own_definitions()
    {
        _platform.Schemas["test:wks:master-data--Thing:2.0.0"] = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "acl": { "$ref": "osdu:wks:AbstractAccessControlList:1.0.0" },
                "data": { "allOf": [ { "$ref": "osdu:wks:AbstractCommon:1.0.0" }, { "properties": { "Name": { "type": "string" } } } ] },
                "meta": { "$ref": "osdu:wks:AbstractCommon:1.0.0#/definitions/Code.1.0.0" }
              }
            }
            """)!.AsObject();
        _platform.Schemas["osdu:wks:AbstractCommon:1.0.0"] = JsonNode.Parse("""
            {
              "type": "object",
              "definitions": { "Code.1.0.0": { "type": "string", "pattern": "^[A-Z]+$" } },
              "properties": { "Code": { "$ref": "#/definitions/Code.1.0.0" }, "Acl": { "$ref": "osdu:wks:AbstractAccessControlList:1.0.0" } }
            }
            """)!.AsObject();
        _platform.Schemas["osdu:wks:AbstractAccessControlList:1.0.0"] = JsonNode.Parse("""
            { "type": "object", "properties": { "owners": { "type": "array", "items": { "type": "string" } } }, "required": ["owners"] }
            """)!.AsObject();

        var read = await Reader().ReadAsync("test:wks:master-data--Thing:2.0.0");

        Assert.Equal(["test:wks:master-data--Thing:2.0.0", "osdu:wks:AbstractAccessControlList:1.0.0", "osdu:wks:AbstractCommon:1.0.0"], read.Read);
        Assert.Empty(read.Unresolved);
        var rules = SchemaRules.Of(read.Schema!);
        Assert.DoesNotContain(rules.Notes, n => n.Contains("does not hold", StringComparison.Ordinal));

        var found = RecordValidator.Check(JsonNode.Parse("""{ "acl": {}, "data": { "Code": "lower", "Acl": { "owners": [1] } }, "meta": "x1" }""")!, rules);
        Assert.Equal(
            [("acl.owners", "required"), ("data.Acl.owners", "type"), ("data.Code", "pattern"), ("meta", "pattern")],
            found.Problems.Select(p => (p.At.Replace("[]", string.Empty, StringComparison.Ordinal), p.Rule)).Order());
    }

    [Fact]
    public async Task Schemas_referring_to_each_other_are_each_read_once_and_the_kind_s_own_schema_is_the_bundle_s_root()
    {
        _platform.Schemas["osdu:wks:master-data--Loop:1.0.0"] = JsonNode.Parse("""{ "type": "object", "properties": { "Other": { "$ref": "osdu:wks:AbstractOther:1.0.0" } } }""")!.AsObject();
        _platform.Schemas["osdu:wks:AbstractOther:1.0.0"] = JsonNode.Parse("""{ "type": "object", "properties": { "Back": { "$ref": "osdu:wks:master-data--Loop:1.0.0" }, "N": { "type": "integer" } } }""")!.AsObject();

        var read = await Reader().ReadAsync("osdu:wks:master-data--Loop:1.0.0");

        Assert.Equal(2, read.Read.Count);
        var found = RecordValidator.Check(JsonNode.Parse("""{ "Other": { "Back": { "Other": { "N": "x" } } } }""")!, SchemaRules.Of(read.Schema!));
        Assert.Equal("Other.Back.Other.N", Assert.Single(found.Problems).Path);
    }

    [Theory]
    [InlineData("osdu:wks:AbstractGone:1.0.0", "holds no schema 'osdu:wks:AbstractGone:1.0.0'")]
    [InlineData("https://schema.osdu.opengroup.org/json/abstract/AbstractCommonResources.1.0.0.json", "is not to a schema by its id")]
    [InlineData("#/properties/data", "points into the schema itself")]
    [InlineData("osdu:wks:AbstractCommon:1.0.0#/properties/Code", "points into a part of 'osdu:wks:AbstractCommon:1.0.0' other than its definitions")]
    public async Task A_reference_the_reader_cannot_resolve_is_named_and_the_part_it_describes_is_not_checked(string reference, string why)
    {
        _platform.Schemas["test:wks:master-data--Odd:1.0.0"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["Part"] = new JsonObject { ["$ref"] = reference }, ["N"] = new JsonObject { ["type"] = "integer" } },
        };
        _platform.Schemas["osdu:wks:AbstractCommon:1.0.0"] = JsonNode.Parse("""{ "type": "object", "properties": { "Code": { "type": "string" } } }""")!.AsObject();

        var read = await Reader().ReadAsync("test:wks:master-data--Odd:1.0.0");

        Assert.Contains(read.Unresolved, u => u.Contains(why, StringComparison.Ordinal));
        var found = RecordValidator.Check(JsonNode.Parse("""{ "Part": "x", "N": "not a number" }""")!, SchemaRules.Of(read.Schema!));
        Assert.Equal("N", Assert.Single(found.Problems).At);
        Assert.Equal("Part", Assert.Single(found.Unverified).At);
    }

    [Fact]
    public async Task A_kind_the_service_holds_no_schema_of_reads_as_none_and_an_answer_that_is_not_json_fails_the_read()
    {
        var none = await Reader().ReadAsync("osdu:wks:master-data--Nothing:1.0.0");
        Assert.Null(none.Schema);
        Assert.Empty(none.Read);

        _platform.SchemaTexts["osdu:wks:master-data--Broken:1.0.0"] = "<html>not a schema</html>";
        var broken = await Assert.ThrowsAsync<DeliveryException>(() => Reader().ReadAsync("osdu:wks:master-data--Broken:1.0.0"));
        Assert.Contains("not JSON", broken.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chain_of_references_past_the_bound_reads_at_most_the_bound_and_names_what_it_did_not_read()
    {
        for (var i = 0; i < SchemaServiceReader.MaxSchemas + 5; i++)
        {
            _platform.Schemas[$"osdu:wks:AbstractStep{i}:1.0.0"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["Next"] = new JsonObject { ["$ref"] = $"osdu:wks:AbstractStep{i + 1}:1.0.0" } },
            };
        }

        var read = await Reader().ReadAsync("osdu:wks:AbstractStep0:1.0.0");

        Assert.Equal(SchemaServiceReader.MaxSchemas, read.Read.Count);
        Assert.Contains(read.Unresolved, u => u.Contains("at most 200 schemas", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_schema_writing_defs_where_draft07_writes_definitions_is_bundled_all_the_same()
    {
        _platform.Schemas["test:wks:master-data--Defs:1.0.0"] = JsonNode.Parse("""
            { "type": "object", "properties": { "Code": { "$ref": "#/$defs/Code" } }, "$defs": { "Code": { "type": "string", "maxLength": 2 } } }
            """)!.AsObject();

        var read = await Reader().ReadAsync("test:wks:master-data--Defs:1.0.0");

        Assert.Empty(read.Unresolved);
        Assert.Equal("maxLength", Assert.Single(RecordValidator.Check(JsonNode.Parse("""{ "Code": "long" }""")!, SchemaRules.Of(read.Schema!)).Problems).Rule);
    }

    [Fact]
    public async Task A_record_meeting_its_schema_with_every_record_it_refers_to_in_OSDU_is_valid_against_the_Schema_service_s()
    {
        var id = Stored();

        var answer = await Checks().RecordAsync(id, null, ExplorerSchemaSource.Osdu, null);

        Assert.True(answer.Found);
        Assert.Null(answer.Problem);
        Assert.Equal(Kind, answer.Kind);
        Assert.Equal(VerdictSchema.SchemaService, answer.Schema!.Source);
        Assert.Equal([Kind], answer.Schema.Read);
        var verdict = VerdictOf(answer);
        Assert.Equal(ValidationOutcome.Valid, verdict.Outcome);
        Assert.Equal(3, verdict.References.InOsdu);
        Assert.Equal(_platform.Records[id]["version"]!.GetValue<long>(), answer.Version);
    }

    [Fact]
    public async Task A_record_breaking_its_schema_or_naming_a_record_OSDU_does_not_hold_is_invalid_where_it_breaks()
    {
        var id = Stored(r => DataOf(r)["Status"] = "Planned", references: false);

        var verdict = VerdictOf(await Checks().RecordAsync(id, null, ExplorerSchemaSource.Osdu, null));

        Assert.Equal(ValidationOutcome.Invalid, verdict.Outcome);
        Assert.Contains(verdict.Problems, p => p.Rule == "enum" && p.Path == "data.Status");
        Assert.Equal(3, verdict.References.Missing);
        Assert.Contains(verdict.Problems, p => p.Rule == "reference" && p.Path == "data.WellID" && p.Message.Contains("storage service holds no such record", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_record_is_checked_at_the_version_asked_and_one_storage_does_not_hold_says_so()
    {
        var id = Stored();
        var held = _platform.Records[id]["version"]!.GetValue<long>();

        Assert.Equal(held, (await Checks().RecordAsync(id, held, ExplorerSchemaSource.Osdu, null)).Version);
        var other = await Checks().RecordAsync(id, held + 7, ExplorerSchemaSource.Osdu, null);
        Assert.False(other.Found);
        Assert.Contains($"no version {held + 7}", other.Problem, StringComparison.Ordinal);

        var missing = await Checks().RecordAsync("dev:master-data--Thing:Gone", null, ExplorerSchemaSource.Osdu, null);
        Assert.False(missing.Found);
        Assert.Contains("holds no record dev:master-data--Thing:Gone", missing.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kind_the_Schema_service_does_not_hold_offers_the_saved_templates_and_checks_against_one_asked_for()
    {
        _platform.Put(ValidRecord());
        var saved = new SavedTemplates(Schema);

        var unheld = await Checks(saved).RecordAsync("dev:master-data--Thing:T-1", null, ExplorerSchemaSource.Osdu, null);
        Assert.Null(unheld.Verdict);
        Assert.Contains($"holds no schema of {Kind}", unheld.Problem, StringComparison.Ordinal);
        Assert.Equal([Schema.Version], unheld.SavedVersions);

        var checkedAgainstSaved = await Checks(saved).RecordAsync("dev:master-data--Thing:T-1", null, ExplorerSchemaSource.Saved, null);
        Assert.Equal(VerdictSchema.Template, checkedAgainstSaved.Schema!.Source);
        Assert.Equal(Schema.Version, VerdictOf(checkedAgainstSaved).Schema!.Version);

        var unknownVersion = await Checks(saved).RecordAsync("dev:master-data--Thing:T-1", null, ExplorerSchemaSource.Saved, "0000000000000000");
        Assert.Contains("version 0000000000000000 is not saved", unknownVersion.Problem, StringComparison.Ordinal);

        var noneSaved = await Checks(new SavedTemplates()).RecordAsync("dev:master-data--Thing:T-1", null, ExplorerSchemaSource.Saved, null);
        Assert.Contains($"No template of {Kind} is saved", noneSaved.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_naming_no_kind_is_found_and_not_checked()
    {
        var record = ValidRecord();
        record["kind"] = "";
        _platform.Put(record);

        var answer = await Checks().RecordAsync("dev:master-data--Thing:T-1", null, ExplorerSchemaSource.Osdu, null);

        Assert.True(answer.Found);
        Assert.Contains("names no kind", answer.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_records_a_search_finds_are_read_checked_and_counted_by_outcome_and_rule_with_an_example_of_each()
    {
        Stored();
        _platform.Put(Record(r => { r["id"] = "dev:master-data--Thing:T-2"; DataOf(r)["Status"] = "Planned"; }));
        _platform.Put(Record(r => { r["id"] = "dev:master-data--Thing:T-3"; DataOf(r)["Status"] = "Drilling"; DataOf(r)["Code"] = "x"; }));
        _platform.Put(FakeOsduPlatform.Record("dev:master-data--Other:O-1", "osdu:wks:master-data--Other:1.0.0"));
        _platform.Search = (_, _) => ["dev:master-data--Thing:T-1", "dev:master-data--Thing:T-2", "dev:master-data--Thing:T-3", "dev:master-data--Other:O-1", "dev:master-data--Thing:Gone"];

        var list = await Checks().ListAsync(new RecordExplorer(_client, "dev", NullLogger.Instance), new ExplorerSearch { Kind = "*:*:*:*" }, ExplorerChecks.MaxRecords, ExplorerSchemaSource.Osdu);

        Assert.Equal(5, list.Matched);
        Assert.Equal(4, list.Read);
        Assert.Equal(["dev:master-data--Thing:Gone"], list.NotFound);
        Assert.Equal((1L, 2L, 0L, 1L), (list.Valid, list.Invalid, list.Unverified, list.NotChecked));
        var status = list.Rules[0];
        Assert.Equal(("data.Status", "enum", 2L), (status.At, status.Rule, status.Records));
        Assert.Equal("dev:master-data--Thing:T-2", status.ExampleId);
        Assert.Contains(list.Rules, r => r.At == "data.Code" && r.Rule == "pattern" && r.Records == 1);
        var other = Assert.Single(list.Unavailable);
        Assert.Equal(("osdu:wks:master-data--Other:1.0.0", 1L), (other.Kind, other.Records));
        Assert.Equal(["valid", "invalid", "invalid", "notValidated"], list.Records.Select(r => r.Outcome));
        Assert.StartsWith("data.Status enum: ", list.Records[1].First, StringComparison.Ordinal);
        Assert.False(list.Cut);

        // The Schema service is asked once for each kind, however many records of it are checked.
        Assert.Single(_platform.Calls, c => Uri.UnescapeDataString(c.Uri.AbsolutePath).EndsWith(Kind, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_search_finding_more_than_a_check_reads_is_cut_at_the_bound_and_says_so()
    {
        Stored();
        _platform.Search = (_, _) => Enumerable.Range(0, 5).Select(i => i == 0 ? "dev:master-data--Thing:T-1" : $"dev:master-data--Thing:Gone{i}").ToList();

        var list = await Checks().ListAsync(new RecordExplorer(_client, "dev", NullLogger.Instance), new ExplorerSearch(), 2, ExplorerSchemaSource.Osdu);

        Assert.True(list.Cut);
        Assert.Equal(2, list.Asked);
        Assert.Equal(1, list.Read);
        Assert.Single(list.NotFound);
    }

    public void Dispose()
    {
        _runtime.Dispose();
        _platform.Dispose();
    }
}
