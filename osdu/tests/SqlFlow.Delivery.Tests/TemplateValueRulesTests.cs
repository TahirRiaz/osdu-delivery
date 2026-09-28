using System.Text.Json.Nodes;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a template says a value of a variable must be, applied to a value a render wrote
/// (<see cref="TemplateValueRules"/>): the JSON Schema rules of the property it lands on and of everything inside it, each
/// problem naming the variable it is at, the rule, and the value.
/// </summary>
public sealed class TemplateValueRulesTests
{
    private static readonly SchemaSnapshot Schema = SchemaSnapshot.Parse("test:wks:work-product-component--Rules:1.0.0", """
        {
          "type": "object",
          "properties": {
            "data": {
              "allOf": [
                { "$ref": "#/definitions/AbstractRefs.1.0.0" },
                {
                  "type": "object",
                  "properties": {
                    "Code": { "type": "string", "pattern": "^[A-Z]{2}-[0-9]+$", "minLength": 4, "maxLength": 8 },
                    "Kind": { "type": "string", "enum": ["Log", "Survey"] },
                    "Fixed": { "const": "yes" },
                    "When": { "type": "string", "format": "date-time" },
                    "Day": { "type": "string", "format": "date" },
                    "Clock": { "type": "string", "format": "time" },
                    "Link": { "type": "string", "format": "uri" },
                    "Guid": { "type": "string", "format": "uuid" },
                    "Mail": { "type": "string", "format": "email" },
                    "Custom": { "type": "string", "format": "osdu-free-text" },
                    "Percent": { "type": "number", "minimum": 0, "maximum": 100 },
                    "Positive": { "type": "number", "exclusiveMinimum": 0 },
                    "Step": { "type": "number", "multipleOf": 0.5 },
                    "Small": { "type": "integer", "format": "int32" },
                    "Tags": { "type": "array", "minItems": 1, "maxItems": 3, "uniqueItems": true, "items": { "type": "string", "maxLength": 5 } },
                    "Items": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": { "Name": { "type": "string" }, "Size": { "type": "integer" } },
                        "required": ["Name"],
                        "additionalProperties": false
                      }
                    },
                    "Either": { "oneOf": [ { "type": "number" }, { "type": "string", "pattern": "^n/a$" } ] },
                    "Nullable": { "type": ["string", "null"], "maxLength": 3 },
                    "Free": { }
                  }
                }
              ]
            }
          },
          "definitions": {
            "AbstractRefs.1.0.0": {
              "type": "object",
              "properties": {
                "WellboreID": { "type": "string", "pattern": "^[\\w\\-\\.]+:master-data\\-\\-Wellbore:[\\w\\-\\.\\:\\%]+:[0-9]*$", "x-osdu-relationship": [ { "GroupType": "master-data", "EntityType": "Wellbore" } ] },
                "ReferenceIDs": { "type": "array", "items": { "type": "string", "x-osdu-relationship": [ { "GroupType": "reference-data" } ] } }
              }
            }
          }
        }
        """, DateTimeOffset.UnixEpoch);

    private static IReadOnlyList<ValueProblem> Check(string target, string json)
        => TemplateValueRules.Check(JsonNode.Parse(json), Path(target), Schema);

    private static TemplatePath Path(string text)
        => TemplatePath.TryParse(text, out var path, out var error) ? path! : throw new ArgumentException(error, nameof(text));

    [Theory]
    [InlineData("osdu.data.Code", "\"AB-12\"")]
    [InlineData("osdu.data.Kind", "\"Survey\"")]
    [InlineData("osdu.data.Fixed", "\"yes\"")]
    [InlineData("osdu.data.When", "\"2026-09-01T10:15:30Z\"")]
    [InlineData("osdu.data.When", "\"2026-09-01T10:15:30.125+02:00\"")]
    [InlineData("osdu.data.Day", "\"2026-02-28\"")]
    [InlineData("osdu.data.Clock", "\"10:15:30Z\"")]
    [InlineData("osdu.data.Link", "\"https://example.org/a\"")]
    [InlineData("osdu.data.Guid", "\"0f8fad5b-d9cb-469f-a165-70867728950e\"")]
    [InlineData("osdu.data.Mail", "\"a@example.org\"")]
    [InlineData("osdu.data.Custom", "\"anything at all\"")]
    [InlineData("osdu.data.Percent", "100")]
    [InlineData("osdu.data.Positive", "0.001")]
    [InlineData("osdu.data.Step", "2.5")]
    [InlineData("osdu.data.Small", "2147483647")]
    [InlineData("osdu.data.Tags", "[\"a\", \"b\"]")]
    [InlineData("osdu.data.Items", "[{ \"Name\": \"x\", \"Size\": 3 }]")]
    [InlineData("osdu.data.Either", "\"n/a\"")]
    [InlineData("osdu.data.Either", "4.5")]
    [InlineData("osdu.data.Nullable", "\"abc\"")]
    [InlineData("osdu.data.Free", "{ \"any\": [1, 2] }")]
    [InlineData("osdu.data.WellboreID", "\"dev:master-data--Wellbore:NO-1:\"")]
    [InlineData("osdu.data.ReferenceIDs", "[\"dev:reference-data--UnitOfMeasure:m:\"]")]
    [InlineData("osdu.data.Unknown", "\"a variable the template does not describe\"")]
    public void A_value_the_template_accepts_has_no_problem(string target, string json)
        => Assert.Empty(Check(target, json));

    [Theory]
    [InlineData("osdu.data.Code", "\"ab-12\"", "pattern")]
    [InlineData("osdu.data.Code", "\"A-12\"", "pattern")]
    [InlineData("osdu.data.Code", "\"AB-X1\"", "pattern")]
    [InlineData("osdu.data.Code", "\"AB-1234567\"", "maxLength")]
    [InlineData("osdu.data.Kind", "\"Core\"", "enum")]
    [InlineData("osdu.data.Fixed", "\"no\"", "const")]
    [InlineData("osdu.data.When", "\"2026-09-01 10:15\"", "format")]
    [InlineData("osdu.data.When", "\"2026-09-01T10:15:30\"", "format")]
    [InlineData("osdu.data.When", "\"2026-13-01T10:15:30Z\"", "format")]
    [InlineData("osdu.data.Day", "\"01.09.2026\"", "format")]
    [InlineData("osdu.data.Day", "\"2026-02-30\"", "format")]
    [InlineData("osdu.data.Clock", "\"25:00:00Z\"", "format")]
    [InlineData("osdu.data.Link", "\"not a link\"", "format")]
    [InlineData("osdu.data.Guid", "\"1234\"", "format")]
    [InlineData("osdu.data.Mail", "\"nobody\"", "format")]
    [InlineData("osdu.data.Percent", "100.5", "maximum")]
    [InlineData("osdu.data.Percent", "-1", "minimum")]
    [InlineData("osdu.data.Positive", "0", "exclusiveMinimum")]
    [InlineData("osdu.data.Step", "2.4", "multipleOf")]
    [InlineData("osdu.data.Small", "2147483648", "format")]
    [InlineData("osdu.data.Small", "1.5", "type")]
    [InlineData("osdu.data.Code", "12", "type")]
    [InlineData("osdu.data.Tags", "[]", "minItems")]
    [InlineData("osdu.data.Tags", "[\"a\", \"b\", \"c\", \"d\"]", "maxItems")]
    [InlineData("osdu.data.Tags", "[\"a\", \"a\"]", "uniqueItems")]
    [InlineData("osdu.data.Either", "\"n/b\"", "anyOf")]
    [InlineData("osdu.data.Nullable", "\"abcd\"", "maxLength")]
    [InlineData("osdu.data.WellboreID", "\"NO 1/1-A\"", "relationship")]
    [InlineData("osdu.data.WellboreID", "\"dev:master-data--Well:NO-1:\"", "relationship")]
    [InlineData("osdu.data.ReferenceIDs", "[\"dev:master-data--Well:NO-1:\"]", "relationship")]
    public void A_value_breaking_a_rule_is_a_problem_naming_the_rule_and_the_value(string target, string json, string rule)
    {
        var problem = Assert.Single(Check(target, json));
        Assert.Equal(rule, problem.Rule);
        Assert.Equal(target, problem.At);
        Assert.False(string.IsNullOrWhiteSpace(problem.Message));
    }

    [Fact]
    public void A_problem_inside_a_list_of_objects_is_at_the_property_of_its_items_and_says_which_item()
    {
        var problems = Check("osdu.data.Items", """[{ "Name": "a" }, { "Size": 1.5 }, { "Name": "c", "Colour": "red" }]""");

        Assert.Equal(3, problems.Count);
        var missing = Assert.Single(problems, p => p.Rule == "required");
        Assert.Equal("osdu.data.Items[].Name", missing.At);
        Assert.StartsWith("at [1]: the value has no Name", missing.Message, StringComparison.Ordinal);

        var size = Assert.Single(problems, p => p.Rule == "type");
        Assert.Equal("osdu.data.Items[].Size", size.At);
        Assert.Equal("1.5", size.Value);

        var extra = Assert.Single(problems, p => p.Rule == "additionalProperties");
        Assert.Equal("osdu.data.Items[].Colour", extra.At);
        Assert.Contains("[2]", extra.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_item_of_a_list_of_values_is_held_to_the_rules_of_its_items()
    {
        var problem = Assert.Single(Check("osdu.data.Tags", """["ok", "far too long"]"""));
        Assert.Equal("maxLength", problem.Rule);
        Assert.StartsWith("at [1]: ", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_is_reported_with_at_most_a_bounded_number_of_problems_and_a_long_value_is_clipped()
    {
        var many = new JsonArray(Enumerable.Range(0, 50).Select(i => (JsonNode?)new JsonObject { ["Size"] = 1 }).ToArray());
        Assert.Equal(TemplateValueRules.MaxProblems, TemplateValueRules.Check(many, Path("osdu.data.Items"), Schema).Count);

        var problem = Assert.Single(Check("osdu.data.Kind", $"\"{new string('x', 1000)}\""));
        Assert.Equal(TemplateValueRules.MaxQuoted + 3, problem.Value.Length);
    }

    [Theory]
    [InlineData("date-time", "2026-09-01T10:15:30Z", true)]
    [InlineData("date-time", "2026-09-01t10:15:30z", true)]
    [InlineData("date-time", "2026-09-01T10:15:30", false)]
    [InlineData("date", "2026-09-01", true)]
    [InlineData("date", "2026-9-1", false)]
    [InlineData("time", "10:15:30.5+01:00", true)]
    [InlineData("time", "10:15", false)]
    [InlineData("uri-reference", "../a/b", true)]
    [InlineData("ipv4", "10.0.0.1", true)]
    [InlineData("ipv4", "10.0.1", false)]
    [InlineData("ipv6", "::1", true)]
    [InlineData("int64", "anything", true)]
    public void A_format_is_asserted_as_JSON_Schema_states_it_and_an_unknown_one_is_not(string format, string text, bool accepted)
        => Assert.Equal(accepted, TemplateValueRules.FormatProblem(text, format) is null);
}
