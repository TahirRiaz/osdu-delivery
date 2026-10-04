using System.Text.Json.Nodes;
using SqlFlow.Delivery.Validation;
using Xunit;
using static SqlFlow.Delivery.Tests.ValidationFixtures;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a check's findings come to for the person fixing the record (<see cref="ValidationGuide"/>): for every rule, the value
/// found, what the schema takes there in its own words, how to meet it, and the value OSDU's example record holds there; and a
/// record read back from storage, whose empty optional blocks mean it has none (<see cref="RecordForm.Stored"/>).
/// </summary>
public sealed class ValidationGuideTests
{
    private const string Kind = "osdu:wks:master-data--Thing:1.0.0";

    private const string SchemaJson = """
        {
          "type": "object",
          "required": ["kind", "acl", "legal"],
          "properties": {
            "kind": { "type": "string" },
            "acl": { "type": "object" },
            "legal": { "type": "object" },
            "ancestry": { "type": "object", "properties": { "parents": { "type": "array", "items": { "type": "string" } } }, "required": ["parents"] },
            "tags": { "type": "object", "additionalProperties": { "type": "string" } },
            "meta": {
              "title": "Frame of Reference Meta Data",
              "description": "The meta data section linking the 'unitKey', 'crsKey' to self-contained definitions.",
              "type": "array",
              "items": { "$ref": "#/definitions/MetaItem" }
            },
            "data": {
              "allOf": [
                { "$ref": "#/definitions/Facility" },
                {
                  "type": "object",
                  "properties": {
                    "Name": { "title": "Name", "description": "The name of the thing.", "type": "string", "minLength": 1, "maxLength": 10, "example": "Alpha" },
                    "Code": { "type": "string", "pattern": "^[A-Z]{2}-[0-9]+$", "examples": ["AB-12", "CD-3"] },
                    "Status": { "type": "string", "enum": ["Active", "Retired"] },
                    "Depth": { "type": "number", "minimum": 0, "exclusiveMaximum": 20000 },
                    "Count": { "type": "integer" },
                    "SpudDate": { "type": "string", "format": "date-time" },
                    "IsActive": { "type": "boolean" },
                    "Aliases": { "type": "array", "uniqueItems": true, "maxItems": 3, "items": { "type": "string" } },
                    "WellID": {
                      "description": "The well this thing belongs to.",
                      "type": "string",
                      "pattern": "^[\\w\\-\\.]+:master-data\\-\\-Well:[\\w\\-\\.\\:\\%]+:[0-9]*$",
                      "x-osdu-relationship": [ { "GroupType": "master-data", "EntityType": "Well" } ],
                      "example": "namespace:master-data--Well:W-1:"
                    },
                    "Unit": { "description": "Written beside the reference.", "$ref": "#/definitions/UnitRef" },
                    "Strict": { "type": "object", "properties": { "A": { "type": "string" } }, "additionalProperties": false },
                    "Choice": { "oneOf": [ { "type": "object", "properties": { "Left": { "type": "string", "pattern": "^L" } } }, { "type": "object", "properties": { "Right": { "type": "integer" } } } ] },
                    "ExtensionProperties": { "type": "object" },
                    "Mode": { "const": "Fixed" }
                  },
                  "required": ["Name"],
                  "additionalProperties": false
                }
              ]
            }
          },
          "definitions": {
            "Facility": { "type": "object", "properties": { "FacilityName": { "type": "string", "description": "Name of the facility." } } },
            "MetaItem": {
              "title": "AbstractMetaItem",
              "type": "object",
              "properties": { "kind": { "type": "string", "enum": ["Unit", "CRS", "DateTime", "AzimuthReference"] }, "name": { "type": "string" } },
              "required": ["kind"]
            },
            "UnitRef": {
              "title": "Unit reference",
              "description": "Written inside the reference.",
              "type": "string",
              "x-osdu-relationship": [ { "GroupType": "reference-data", "EntityType": "UnitOfMeasure" } ]
            }
          }
        }
        """;

    private static readonly SchemaRules Thing = SchemaRules.Of(SchemaOf(SchemaJson, Kind));

    /// <summary>The example record the data definitions publish for the kind, as their Examples folder holds one.</summary>
    private static readonly OfficialExample Example = new(
        Kind,
        "v0.30.0",
        "Examples/master-data/Thing.1.0.0.json",
        new Uri("https://community.opengroup.org/osdu/data/data-definitions/-/blob/v0.30.0/Examples/master-data/Thing.1.0.0.json"),
        JsonNode.Parse("""
            {
              "kind": "osdu:wks:master-data--Thing:1.0.0",
              "meta": [ { "kind": "Unit", "name": "m" } ],
              "data": { "Name": "Example Name", "Code": "EX-1", "WellID": "namespace:master-data--Well:Example:", "Aliases": ["Example Alias"], "Depth": 12345.6 }
            }
            """)!.AsObject());

    private static JsonObject Valid() => JsonNode.Parse("""
        {
          "kind": "osdu:wks:master-data--Thing:1.0.0",
          "acl": {},
          "legal": {},
          "data": { "Name": "Alpha", "Code": "AB-12", "Status": "Active", "Depth": 12.5, "Count": 3, "IsActive": true, "Aliases": ["a"] }
        }
        """)!.AsObject();

    private static JsonObject With(string path, JsonNode? value)
    {
        var record = Valid();
        var at = path.Split('.');
        var holder = record;
        foreach (var step in at[..^1])
        {
            holder = holder[step]!.AsObject();
        }

        holder[at[^1]] = value;
        return record;
    }

    private static JsonObject Without(string path)
    {
        var record = Valid();
        var at = path.Split('.');
        var holder = record;
        foreach (var step in at[..^1])
        {
            holder = holder[step]!.AsObject();
        }

        holder.Remove(at[^1]);
        return record;
    }

    private static (ValidationVerdict Verdict, ValidationGuidance Guidance) Guide(JsonObject record, RecordForm form = RecordForm.Sent, OfficialExample? example = null)
    {
        var findings = RecordValidator.Check(record, Thing, form: form);
        var verdict = ValidationVerdict.Of(findings, Thing, VerdictSchema.SchemaService, new Dictionary<string, ReferenceAnswer>(), DateTime.UnixEpoch);
        return (verdict, ValidationGuide.Of(Thing, verdict, record, example));
    }

    /// <summary>The one problem a record has, with its guide and what the schema expects where it is.</summary>
    private static (SchemaFinding Problem, FindingGuide Guide, ValueExpectation? Expected) Only(JsonObject record, OfficialExample? example = null)
    {
        var (verdict, guidance) = Guide(record, example: example);
        var problem = Assert.Single(verdict.Problems);
        var guide = Assert.Single(guidance.Problems);
        Assert.Equal((problem.Path, problem.Rule), (guide.Path, guide.Rule));
        return (problem, guide, guide.Expected is { } key ? guidance.Expectations[key] : null);
    }

    [Fact]
    public void A_valid_record_has_no_guidance_to_give()
    {
        var (verdict, guidance) = Guide(Valid());
        Assert.Equal(ValidationOutcome.Valid, verdict.Outcome);
        Assert.Empty(guidance.Problems);
        Assert.Empty(guidance.Unverified);
        Assert.Empty(guidance.Expectations);
    }

    [Fact]
    public void A_null_where_a_list_is_optional_says_to_leave_it_out_and_shows_the_schema_and_OSDUs_example()
    {
        var (problem, guide, expected) = Only(With("meta", null), Example);

        Assert.Equal("type", problem.Rule);
        Assert.Equal("null where the schema takes a list", problem.Message);
        Assert.Equal("null", guide.Found);
        Assert.NotNull(expected);
        Assert.Equal("meta", expected.At);
        Assert.False(expected.Required);
        Assert.Equal("Frame of Reference Meta Data", expected.Title);
        Assert.StartsWith("The meta data section", expected.Description, StringComparison.Ordinal);
        Assert.Equal("a list of objects (AbstractMetaItem)", expected.Summary);
        Assert.Equal("""[{"kind":"Unit","name":"m"}]""", expected.OsduExample);
        Assert.Equal(
            """Leave meta out, or give it a list of objects (AbstractMetaItem): the schema does not take null here, and meta is not required. For example: [{"kind":"Unit","name":"m"}]""",
            guide.Advice);
    }

    [Fact]
    public void A_null_where_a_value_is_required_says_to_give_one()
    {
        var (_, guide, expected) = Only(With("data.Name", null));
        Assert.True(expected!.Required);
        Assert.Equal("Give Name text of 1 to 10 characters: the schema requires Name and does not take null for it. For example: Alpha", guide.Advice);
    }

    [Fact]
    public void A_missing_required_property_is_found_absent_and_the_advice_says_what_to_add()
    {
        var (problem, guide, expected) = Only(Without("data.Name"));

        Assert.Equal("required", problem.Rule);
        Assert.Equal("absent", guide.Found);
        Assert.Equal("data.Name", expected!.At);
        Assert.Equal("The name of the thing.", expected.Description);
        Assert.Equal("Add Name: the schema requires it, and it takes text of 1 to 10 characters. For example: Alpha", guide.Advice);
    }

    [Fact]
    public void A_pattern_broken_names_the_pattern_and_the_schemas_example()
    {
        var (_, guide, expected) = Only(With("data.Code", "bad"));
        Assert.Equal("'bad'", guide.Found);
        Assert.Equal(["AB-12", "CD-3"], expected!.Examples);
        Assert.Equal("text matching ^[A-Z]{2}-[0-9]+$", expected.Summary);
        Assert.Equal("Change Code so it matches ^[A-Z]{2}-[0-9]+$. For example: AB-12", guide.Advice);
    }

    [Fact]
    public void A_value_outside_an_enumeration_lists_the_values_allowed()
    {
        var (_, guide, expected) = Only(With("data.Status", "Gone"));
        Assert.Equal(["Active", "Retired"], expected!.Allowed);
        Assert.Equal(2, expected.AllowedCount);
        Assert.Equal("one of 2 values", expected.Summary);
        Assert.Equal("Use one of the values the schema allows: Active, Retired.", guide.Advice);
    }

    [Fact]
    public void A_constant_says_the_one_value_to_write()
    {
        var (_, guide, _) = Only(With("data.Mode", "Other"));
        Assert.Equal("Write Mode as exactly Fixed.", guide.Advice);
    }

    [Theory]
    [InlineData("data.Depth", "\"12.5\"", " Write the number itself, without quotes: 12.5.")]
    [InlineData("data.IsActive", "\"true\"", " Write true or false without quotes.")]
    [InlineData("data.Code", "12", " Write it as text, in quotes: \"12\".")]
    [InlineData("data.Count", "1.5", " Give a whole number; the schema takes no fraction here.")]
    [InlineData("data.Aliases", "\"a\"", " Put the value in a list, a list of one if there is one value.")]
    [InlineData("data.Name", "[\"Alpha\"]", " Give a single value, not a list.")]
    [InlineData("data.Name", "{\"text\":\"Alpha\"}", " Give a single value, not an object.")]
    public void A_value_of_the_wrong_type_says_how_to_write_it(string path, string json, string hint)
    {
        var (problem, guide, _) = Only(With(path, JsonNode.Parse(json)));
        Assert.Equal("type", problem.Rule);
        Assert.Contains(hint, guide.Advice, StringComparison.Ordinal);
        Assert.StartsWith("Write ", guide.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_in_another_form_says_the_form_to_write()
    {
        var (_, guide, expected) = Only(With("data.SpudDate", "yesterday"));
        Assert.Equal("a date and time (RFC 3339)", expected!.Summary);
        Assert.Equal("Write SpudDate as an RFC 3339 date and time with an offset or Z, such as 2026-09-01T10:15:30Z.", guide.Advice);
    }

    [Fact]
    public void Bounds_broken_say_the_range_to_keep_to()
    {
        Assert.Equal("Write Name with 1 to 10 characters. For example: Alpha", Only(With("data.Name", "ABCDEFGHIJKL")).Guide.Advice);
        Assert.Equal("Write Depth as a number at least 0 and below 20000.", Only(With("data.Depth", -1)).Guide.Advice);
        Assert.Equal("Write Depth as a number at least 0 and below 20000.", Only(With("data.Depth", 20000)).Guide.Advice);
        Assert.Equal("Give Aliases at most 3 items.", Only(With("data.Aliases", new JsonArray("a", "b", "c", "d"))).Guide.Advice);
        Assert.Equal("Remove the repeated item from Aliases: the schema takes each item once.", Only(With("data.Aliases", new JsonArray("a", "a"))).Guide.Advice);
        Assert.Equal("a list of text values with at most 3 items, each item once", Only(With("data.Aliases", new JsonArray("a", "a"))).Expected!.Summary);
    }

    [Fact]
    public void A_property_the_schema_does_not_describe_lists_those_it_does_and_where_ones_own_go()
    {
        var (problem, guide, _) = Only(With("data.Bogus", "x"));
        Assert.Equal("additionalProperties", problem.Rule);
        Assert.StartsWith("Remove Bogus, or name it as one of the properties the schema describes (", guide.Advice, StringComparison.Ordinal);
        Assert.Contains("FacilityName", guide.Advice, StringComparison.Ordinal);
        Assert.EndsWith("A property of your own belongs under data.ExtensionProperties.", guide.Advice, StringComparison.Ordinal);

        // An object that holds no ExtensionProperties has no such place to offer.
        var strict = Only(With("data.Strict", new JsonObject { ["A"] = "a", ["B"] = "b" })).Guide;
        Assert.Equal("Remove B, or name it as one of the properties the schema describes (A): the object holding it allows no other.", strict.Advice);
    }

    [Fact]
    public void A_relationship_written_as_a_name_or_to_the_wrong_type_says_whose_id_to_write()
    {
        var (_, name, expected) = Only(With("data.WellID", "W-1"), Example);
        Assert.Contains("master-data--Well", expected!.Summary, StringComparison.Ordinal);
        Assert.Equal("The well this thing belongs to.", expected.Description);
        Assert.Equal("namespace:master-data--Well:Example:", expected.OsduExample);
        Assert.StartsWith("Write the id of a master-data--Well record, not its name or code", name.Advice, StringComparison.Ordinal);
        Assert.EndsWith("For example: namespace:master-data--Well:W-1:", name.Advice, StringComparison.Ordinal);

        var (_, other, _) = Only(With("data.WellID", "dev:master-data--Field:F-1:"));
        Assert.StartsWith("Point WellID to a master-data--Well record", other.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_OSDU_does_not_hold_says_to_deliver_it_first_or_correct_the_id()
    {
        var record = With("data.WellID", "dev:master-data--Well:W-9:");
        var findings = RecordValidator.Check(record, Thing);
        var answers = new Dictionary<string, ReferenceAnswer> { [ReferenceResolver.Key("dev:master-data--Well:W-9:")] = new(ReferenceState.Missing, "OSDU's storage service holds no such record") };
        var verdict = ValidationVerdict.Of(findings, Thing, VerdictSchema.SchemaService, answers, DateTime.UnixEpoch);
        var guide = Assert.Single(ValidationGuide.Of(Thing, verdict, record).Problems);

        Assert.Equal("reference", guide.Rule);
        Assert.Equal("'dev:master-data--Well:W-9:'", guide.Found);
        Assert.Equal("Deliver the record dev:master-data--Well:W-9: to OSDU before this one, or correct the id: its partition, entity type and code must name a record OSDU holds.", guide.Advice);
    }

    [Fact]
    public void A_problem_with_an_item_of_a_list_is_guided_by_what_each_item_is()
    {
        var (problem, guide, expected) = Only(With("data.Aliases", new JsonArray(1)));
        Assert.Equal(("data.Aliases[0]", "data.Aliases"), (problem.Path, problem.At));
        Assert.Equal("data.Aliases[]", guide.Expected);
        Assert.Equal("text", expected!.Summary);
        Assert.False(expected.Required);
        Assert.Equal("Write Aliases[0] as text. Write it as text, in quotes: \"1\".", guide.Advice);
    }

    [Fact]
    public void A_property_of_a_choice_is_described_by_the_form_that_names_it()
    {
        var expected = ValidationGuide.Expect(Thing, "data.Choice.Left");
        Assert.NotNull(expected);
        Assert.Equal("text matching ^L", expected.Summary);
        Assert.Equal(2, ValidationGuide.Expect(Thing, "data.Choice")!.Forms);
    }

    [Fact]
    public void The_words_beside_a_reference_are_the_propertys_own_and_the_rest_comes_from_what_it_refers_to()
    {
        var unit = ValidationGuide.Expect(Thing, "data.Unit")!;
        Assert.Equal("Written beside the reference.", unit.Description);
        Assert.Equal("Unit reference", unit.Title);
        Assert.Contains("UnitOfMeasure", unit.Summary, StringComparison.Ordinal);

        // A property of a branch of the data block, and the data block itself, are found as the record names them.
        Assert.Equal("Name of the facility.", ValidationGuide.Expect(Thing, "data.FacilityName")!.Description);
        var data = ValidationGuide.Expect(Thing, "data")!;
        Assert.Contains("ExtensionProperties", data.Properties);
        Assert.Equal(["Name"], data.RequiredProperties);
        Assert.True(data.OnlyNamedProperties);
        Assert.Null(ValidationGuide.Expect(Thing, "data.NoSuchThing"));
    }

    [Fact]
    public void The_record_itself_and_each_item_of_a_list_have_expectations_of_their_own()
    {
        var record = ValidationGuide.Expect(Thing, string.Empty)!;
        Assert.Equal(["kind", "acl", "legal"], record.RequiredProperties);
        Assert.False(record.Required);

        var item = ValidationGuide.Expect(Thing, "meta[]", Example)!;
        Assert.Equal("AbstractMetaItem", item.Title);
        Assert.Equal(["kind"], item.RequiredProperties);
        Assert.Equal("""{"kind":"Unit","name":"m"}""", item.OsduExample);
        Assert.Equal("one of 4 values", ValidationGuide.Expect(Thing, "meta[].kind")!.Summary);
        Assert.Equal("Unit", ValidationGuide.Expect(Thing, "meta[].kind", Example)!.OsduExample);
    }

    [Fact]
    public void A_part_not_checked_says_there_is_nothing_to_change_for_it()
    {
        var rules = SchemaRules.Of(SchemaOf("""{ "type": "object", "properties": { "Code": { "type": "string", "pattern": "(?<open" } } }"""));
        var record = JsonNode.Parse("""{ "Code": "x" }""")!;
        var verdict = ValidationVerdict.Of(RecordValidator.Check(record, rules), rules, VerdictSchema.SchemaService, new Dictionary<string, ReferenceAnswer>(), DateTime.UnixEpoch);
        var guide = Assert.Single(ValidationGuide.Of(rules, verdict, record).Unverified);
        Assert.Equal("pattern", guide.Rule);
        Assert.StartsWith("Nothing to change in the record for this", guide.Advice, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_record_the_findings_own_values_are_said()
    {
        var record = With("data.Code", "bad");
        var verdict = Guide(record).Verdict;
        var guide = Assert.Single(ValidationGuide.Of(Thing, verdict, record: null).Problems);
        Assert.Equal("bad", guide.Found);
        Assert.NotNull(guide.Advice);
    }

    [Fact]
    public void The_guidance_names_the_example_it_quotes_and_lists_one_guide_per_finding_in_order()
    {
        var record = With("data.Code", "bad");
        record["data"]!["Status"] = "Gone";
        record["meta"] = null;
        var (verdict, guidance) = Guide(record, example: Example);

        Assert.Equal(verdict.Problems.Select(p => (p.Path, p.Rule)), guidance.Problems.Select(g => (g.Path, g.Rule)));
        Assert.All(guidance.Problems, g => Assert.True(g.Expected is null || guidance.Expectations.ContainsKey(g.Expected)));
        Assert.Equal(("v0.30.0", "Examples/master-data/Thing.1.0.0.json"), (guidance.Example!.Release, guidance.Example.Path));
        Assert.Null(guidance.ExampleNote);
    }

    [Fact]
    public void A_schemas_words_are_kept_within_bounds()
    {
        var long_ = new string('w', 5_000);
        var rules = SchemaRules.Of(SchemaOf($$"""
            { "type": "object", "properties": { "A": { "type": "string", "description": "{{long_}}", "examples": ["1", "2", "3", "4", "5"], "example": "0" } } }
            """));
        var a = ValidationGuide.Expect(rules, "A")!;
        Assert.Equal(SchemaDocs.MaxDescription + 3, a.Description!.Length);
        Assert.EndsWith("...", a.Description, StringComparison.Ordinal);
        Assert.Equal(["0", "1", "2"], a.Examples);
    }

    [Fact]
    public void A_stored_record_with_an_empty_optional_block_reads_as_having_none_and_says_so()
    {
        var record = Valid();
        record["meta"] = null;
        record["ancestry"] = new JsonObject();
        record["tags"] = new JsonObject();
        var stored = RecordValidator.Check(record, Thing, form: RecordForm.Stored);

        Assert.Empty(stored.Problems);
        Assert.Equal(["ancestry", "meta", "tags"], stored.ReadAsAbsent);
        Assert.Contains(stored.Notes, n => n.StartsWith("meta is null", StringComparison.Ordinal));
        Assert.Contains(stored.Notes, n => n.StartsWith("ancestry is empty", StringComparison.Ordinal));
        var verdict = ValidationVerdict.Of(stored, Thing, VerdictSchema.SchemaService, new Dictionary<string, ReferenceAnswer>(), DateTime.UnixEpoch);
        Assert.Equal(ValidationOutcome.Valid, verdict.Outcome);
        Assert.Contains(verdict.Notes, n => n.StartsWith("meta is null", StringComparison.Ordinal));

        // The same document about to be sent is judged whole: a null meta and an ancestry without its parents are the
        // sender's own, and the gate holds them to the schema.
        var sent = RecordValidator.Check(record, Thing);
        Assert.Equal(["meta", "ancestry.parents"], sent.Problems.Select(p => p.Path).OrderByDescending(p => p == "meta"));
        Assert.Empty(sent.ReadAsAbsent);

        // Only the optional blocks: a null data block is judged in either form, and a block holding something is checked.
        var data = RecordValidator.Check(With("data", null), Thing, form: RecordForm.Stored);
        Assert.Contains(data.Problems, p => p.Path == "data");
        record["meta"] = new JsonArray(new JsonObject { ["name"] = "m" });
        Assert.Equal("meta[0].kind", Assert.Single(RecordValidator.Check(record, Thing, form: RecordForm.Stored).Problems).Path);
    }
}
