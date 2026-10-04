using System.Text.Json.Nodes;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;
using static SqlFlow.Delivery.Tests.ValidationFixtures;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A template compiled into the rules a check applies (<see cref="SchemaRules"/>): compiled once per template version and
/// shared; <c>allOf</c> branches combined so every branch holds; references followed, cycles ended, and a reference the
/// bundle does not hold turned into a part that says it cannot be checked; and a property found by its path exactly where
/// the template's own reading finds it, on every OSDU schema the suites carry.
/// </summary>
public sealed class SchemaRulesTests
{
    /// <summary>The OSDU schemas the suites carry, saved from the data definitions.</summary>
    public static TheoryData<string> OsduKinds() => new()
    {
        "osdu:wks:master-data--Wellbore:1.3.0",
        "osdu:wks:work-product-component--WellLog:1.4.0",
        "osdu:wks:work-product-component--WellLog:1.5.0",
        "osdu:wks:work-product-component--WellboreTrajectory:1.3.0",
        "osdu:wks:work-product-component--Document:1.0.0",
        "osdu:wks:dataset--File.Generic:1.0.0",
    };

    [Fact]
    public void A_template_is_compiled_once_and_every_check_of_it_shares_the_rules()
    {
        Assert.Same(SchemaRules.Of(Schema), SchemaRules.Of(Schema));
        Assert.Same(Rules.Root, SchemaRules.Of(Schema).Root);
        Assert.Same(Rules.At("data.Name"), Rules.At("data.Name"));

        // Another snapshot is another template version, compiled on its own.
        Assert.NotSame(Rules, SchemaRules.Of(SchemaSnapshot.Parse(Kind, SchemaJson, DateTimeOffset.UnixEpoch)));
        Assert.Equal(Kind, Rules.Kind);
        Assert.Equal(Schema.Version, Rules.Version);
    }

    [Fact]
    public void A_property_two_allOf_branches_describe_holds_every_rule_of_both()
    {
        var name = Rules.At("data.Name")!;
        Assert.Equal(["string"], name.Types);
        Assert.Equal("^[^ ]+$", Assert.Single(name.Patterns).Text);
        Assert.Equal(1, name.MinLength);
        Assert.Equal(10, name.MaxLength);
        Assert.Contains("Name", Rules.At("data")!.Required);
    }

    [Fact]
    public void Branches_combine_to_the_tightest_bounds_the_types_both_allow_and_every_pattern_enumeration_and_constant()
    {
        var rules = SchemaRules.Of(SchemaOf("""
            {
              "type": "object",
              "properties": {
                "V": {
                  "allOf": [
                    { "type": "number", "minimum": 0, "maximum": 100, "pattern": "a", "enum": [1, 2, 3] },
                    { "type": "integer", "minimum": 5, "maximum": 50, "pattern": "b", "enum": [2, 3, 4], "const": 3 }
                  ]
                }
              }
            }
            """));
        var v = rules.At("V")!;
        Assert.Equal(["integer"], v.Types);
        Assert.Equal(5, v.Minimum);
        Assert.Equal(50, v.Maximum);
        Assert.Equal(["a", "b"], v.Patterns.Select(p => p.Text));
        Assert.Equal(2, v.Enumerations.Count);
        Assert.Single(v.Constants);

        // A value meeting one branch and not the other breaks the combined rules.
        Assert.Contains(RecordValidator.Check(new JsonObject { ["V"] = 1 }, rules).Problems, p => p.Rule == "minimum");
        Assert.Contains(RecordValidator.Check(new JsonObject { ["V"] = 4 }, rules).Problems, p => p.Rule == "enum");
    }

    [Fact]
    public void A_schema_built_of_branches_none_of_which_names_a_type_is_an_object()
    {
        var rules = SchemaRules.Of(SchemaOf("""{ "type": "object", "properties": { "D": { "allOf": [ { "properties": { "A": { "type": "string" } } } ] } } }"""));
        Assert.Equal(["object"], rules.At("D")!.Types);
        Assert.Equal("type", Assert.Single(RecordValidator.Check(new JsonObject { ["D"] = "text" }, rules).Problems).Rule);
    }

    [Fact]
    public void A_schema_that_refers_to_itself_through_its_properties_compiles_and_checks_a_value_of_any_depth()
    {
        var rules = SchemaRules.Of(SchemaOf("""
            {
              "type": "object",
              "properties": { "Root": { "$ref": "#/definitions/Node.1.0.0" } },
              "definitions": {
                "Node.1.0.0": { "type": "object", "properties": { "Name": { "type": "string" }, "Child": { "$ref": "#/definitions/Node.1.0.0" } } }
              }
            }
            """));

        Assert.Same(rules.At("Root"), rules.At("Root.Child"));
        var value = new JsonObject { ["Name"] = "a" };
        var deepest = value;
        for (var i = 0; i < 10; i++)
        {
            var child = new JsonObject { ["Name"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            deepest["Child"] = child;
            deepest = child;
        }

        deepest["Name"] = 7;
        var problem = Assert.Single(RecordValidator.Check(new JsonObject { ["Root"] = value }, rules).Problems);
        Assert.Equal("Root.Child.Child.Child.Child.Child.Child.Child.Child.Child.Child.Name", problem.Path);
    }

    [Theory]
    [InlineData("""{ "type": "object", "properties": { "L": { "$ref": "#/definitions/Loop.1.0.0" } }, "definitions": { "Loop.1.0.0": { "$ref": "#/definitions/Loop.1.0.0" } } }""", "names itself")]
    [InlineData("""{ "type": "object", "properties": { "L": { "$ref": "#/definitions/A.1.0.0" } }, "definitions": { "A.1.0.0": { "$ref": "#/definitions/B.1.0.0" }, "B.1.0.0": { "$ref": "#/definitions/A.1.0.0" } } }""", "more than")]
    [InlineData("""{ "type": "object", "properties": { "L": { "$ref": "#/definitions/A.1.0.0" } }, "definitions": { "A.1.0.0": { "allOf": [ { "$ref": "#/definitions/A.1.0.0" } ] } } }""", "refers back")]
    [InlineData("""{ "type": "object", "properties": { "L": { "$ref": "#/definitions/Gone.1.0.0" } } }""", "does not hold")]
    [InlineData("""{ "type": "object", "properties": { "L": { "$ref": "osdu:wks:AbstractCommonResources:1.0.0" } } }""", "does not hold")]
    public void A_reference_that_never_reaches_a_schema_compiles_to_a_part_that_says_why_it_cannot_be_checked(string json, string why)
    {
        var rules = SchemaRules.Of(SchemaOf(json));

        Assert.Contains(why, rules.At("L")!.Unchecked, StringComparison.Ordinal);
        Assert.Contains(rules.Notes, n => n.Contains(why, StringComparison.Ordinal));
        var notChecked = Assert.Single(RecordValidator.Check(new JsonObject { ["L"] = "x" }, rules).Unverified);
        Assert.Equal("L", notChecked.At);
    }

    [Fact]
    public void What_a_schema_states_that_no_check_asserts_is_noted_once()
    {
        var rules = SchemaRules.Of(SchemaOf("""
            {
              "type": "object",
              "properties": {
                "A": { "type": "string", "format": "osdu-free-text" },
                "B": { "type": "string", "format": "osdu-free-text" },
                "C": { "type": "string", "format": "date-time" },
                "D": { "type": "integer", "format": "int64" },
                "E": { "type": "string", "pattern": "(?<open" },
                "F": { "type": "object", "patternProperties": { "^x": { "type": "string" } } }
              }
            }
            """));

        Assert.Single(rules.Notes, n => n.Contains("'osdu-free-text'", StringComparison.Ordinal));
        Assert.DoesNotContain(rules.Notes, n => n.Contains("date-time", StringComparison.Ordinal) || n.Contains("int64", StringComparison.Ordinal));
        Assert.Single(rules.Notes, n => n.Contains("(?<open", StringComparison.Ordinal));
        Assert.Single(rules.Notes, n => n.Contains("'patternProperties'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_draft04_boolean_exclusive_bound_and_a_type_of_null_alone_constrain_nothing()
    {
        var rules = SchemaRules.Of(SchemaOf("""
            { "type": "object", "properties": { "N": { "type": "number", "minimum": 5, "exclusiveMinimum": true }, "Z": { "type": "null" } } }
            """));

        Assert.Null(rules.At("N")!.ExclusiveMinimum);
        Assert.Empty(RecordValidator.Check(new JsonObject { ["N"] = 5, ["Z"] = "anything" }, rules).Problems);
    }

    [Theory]
    [MemberData(nameof(OsduKinds))]
    public void Every_variable_of_an_OSDU_template_is_found_by_its_path_exactly_where_the_template_finds_it(string kind)
    {
        var schema = Samples.SampleTemplate(kind);
        var rules = SchemaRules.Of(schema);
        var variables = OsduTemplate.From(schema).Variables;
        Assert.NotEmpty(variables);
        foreach (var variable in variables)
        {
            var path = variable.Path.SchemaPath;
            Assert.True(
                (rules.At(path) is null) == (schema.Resolve(path) is null),
                $"{kind}: {path} is {(rules.At(path) is null ? "not " : string.Empty)}found by the rules and {(schema.Resolve(path) is null ? "not " : string.Empty)}by the template.");
        }
    }

    [Theory]
    [MemberData(nameof(OsduKinds))]
    public void Every_reference_of_an_OSDU_template_resolves_so_no_part_of_it_goes_unchecked(string kind)
    {
        var rules = SchemaRules.Of(Samples.SampleTemplate(kind));

        Assert.True(rules.Nodes > 10);
        Assert.DoesNotContain(rules.Notes, n => n.Contains("does not hold", StringComparison.Ordinal) || n.Contains("refers back", StringComparison.Ordinal));
        Assert.Null(rules.Root.Unchecked);
        Assert.Equal(["kind", "acl", "legal"], rules.Root.Required);
    }
}
