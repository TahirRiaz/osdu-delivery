using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A list of objects some of whose properties read values (docs/mapping-templates.md, Lists of objects): each item an
/// object laid out as the record tree, its properties literals, value nodes, <c>$coalesce</c> nodes, objects or lists of
/// values, filling the variables of the list's items and reading the row the list is in. The list is the objects its items
/// give, in order, an object given twice written once, and an item none of whose properties gives a value adds nothing.
/// </summary>
public sealed class ListOfObjectsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Kind = "test:wks:work-product-component--Assured:1.0.0";

    private const string Certified = "dev:reference-data--TechnicalAssuranceType:Certified:";

    private const string Unevaluated = "dev:reference-data--TechnicalAssuranceType:Unevaluated:";

    /// <summary>The Recall well log's technical assurance: certified for a log source starting "stat_", and unevaluated otherwise.</summary>
    private const string Assurance = """
        TechnicalAssurances:
          - TechnicalAssuranceTypeID:
              $expr: iif(startsWith(log_source, "stat_"), "Certified", "Unevaluated")
              $modifiers:
                - id: "{$param.dataPartition}:reference-data--TechnicalAssuranceType:{$value}:"
            Comment: Set by the conversion
        """;

    private static SchemaSnapshot Schema() => SchemaSnapshot.Parse(Kind, """
        {
          "$id": "https://example.org/Assured.1.0.0.json",
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "kind": { "type": "string" },
            "acl": { "type": "object", "properties": { "owners": { "type": "array", "items": { "type": "string" } }, "viewers": { "type": "array", "items": { "type": "string" } } }, "required": ["owners", "viewers"] },
            "legal": { "type": "object", "properties": { "legaltags": { "type": "array", "items": { "type": "string" } }, "otherRelevantDataCountries": { "type": "array", "items": { "type": "string" } } }, "required": ["legaltags", "otherRelevantDataCountries"] },
            "data": {
              "type": "object",
              "properties": {
                "Name": { "type": "string" },
                "Depth": { "type": "number" },
                "Aliases": { "type": "array", "items": { "type": "string" } },
                "Curves": { "type": "array", "items": { "type": "object", "properties": { "CurveID": { "type": "string" } } } },
                "TechnicalAssurances": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "TechnicalAssuranceTypeID": { "type": "string", "pattern": "^[\\w\\-\\.]+:reference-data\\-\\-TechnicalAssuranceType:[\\w\\-\\.\\:\\%]+:[0-9]*$", "x-osdu-relationship": [ { "GroupType": "reference-data", "EntityType": "TechnicalAssuranceType" } ] },
                      "UnitID": { "type": "string", "x-osdu-relationship": [ { "GroupType": "reference-data", "EntityType": "UnitOfMeasure" } ] },
                      "Comment": { "type": "string" },
                      "Score": { "type": "integer" },
                      "Reviewers": { "type": "array", "items": { "type": "string" } },
                      "Detail": { "type": "object", "properties": { "Note": { "type": "string" }, "Level": { "type": "number" } } },
                      "Links": { "type": "array", "items": { "type": "object", "properties": { "Url": { "type": "string" } } } }
                    }
                  }
                }
              },
              "required": ["Depth"]
            }
          },
          "required": ["kind", "acl", "legal"]
        }
        """, T0);

    private static ReferenceSnapshot Cache() => new("refs-1", T0,
    [
        new ReferenceType("TechnicalAssuranceType", "reference-data--TechnicalAssuranceType",
        [
            ReferenceItem.FromText("dev:reference-data--TechnicalAssuranceType:Certified", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Code"] = "Certified" }),
            ReferenceItem.FromText("dev:reference-data--TechnicalAssuranceType:Unevaluated", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Code"] = "Unevaluated" }),
        ]),
        new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Code"] = "m" }),
            ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:ft", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Code"] = "ft" }),
        ]),
    ]);

    private static RenderContext Context() => new()
    {
        MappingReference = "Assured@1.0.0",
        CacheScope = "dev",
        CacheVersion = "refs-1",
        SchemaSnapshotVersion = Schema().Version,
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "dev" },
    };

    /// <summary>
    /// A mapping document over <see cref="Schema"/>: the envelope (<see cref="TestSchema.Envelope"/> unless one is given), the
    /// name and depth, then <paramref name="data"/>, then <paramref name="fixtures"/>.
    /// </summary>
    private static string Document(string data, string fixtures = "", string lookups = "", string envelope = TestSchema.Envelope)
        => $"""
            documentType: mapping
            name: Assured
            version: 1.0.0
            template:
              kind: {Kind}
              version: {Schema().Version}
            dataset:
              system: test
              key: [name]
            parameters:
              dataPartition: {"{"} required: true {"}"}

            """ + lookups + "\nrecord:\n" + TestSchema.Indented(envelope, 2) + "  data:\n"
            + TestSchema.Indented("Name: { $from: name }\nDepth: { $from: depth }", 4) + TestSchema.Indented(data, 4) + "\n" + fixtures + "\n";

    private static MappingDefinition Mapping(string data, string fixtures = "", string lookups = "", string envelope = TestSchema.Envelope)
        => new DeliveryDocumentLoader().ParseMapping(Document(data, fixtures, lookups, envelope), "assured.yaml");

    private static string Refused(string data)
        => Assert.Throws<FlowValidationException>(() => Mapping(data)).Message;

    private static MappingRenderer Renderer(MappingDefinition mapping) => new(mapping, Schema(), Cache(), Context());

    private static SourceRecord Row(params (string Column, string? Value)[] values)
    {
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "log-1", ["depth"] = "1" };
        foreach (var (column, value) in values)
        {
            row[column] = value;
        }

        return new SourceRecord
        {
            Row = SourceRow.FromStrings(row),
            Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static RenderResult Rendered(MappingDefinition mapping, params (string Column, string? Value)[] values)
    {
        var result = Renderer(mapping).Render(Row(values));
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        return result;
    }

    /// <summary>The rendered list, canonical, as a record is compared: properties by name.</summary>
    private static string? Assurances(RenderResult result)
        => result.Document["data"]!["TechnicalAssurances"] is { } list ? CanonicalJson.ToString(list) : null;

    /// <summary>What a list is expected to render to, canonical.</summary>
    private static string Canonical(string json) => CanonicalJson.Canonicalize(json);

    [Fact]
    public void A_list_whose_item_is_an_object_with_a_value_node_inside_is_a_list_of_objects()
    {
        var mapping = Mapping(Assurance + "\n" + """
              - TechnicalAssuranceTypeID: "dev:reference-data--TechnicalAssuranceType:Unevaluated:"
            """);

        var list = mapping.Entries.Single(e => e.Target.Text == "osdu.data.TechnicalAssurances");
        Assert.True(list.IsList);
        Assert.Equal(2, list.Parts.Count);

        // The first item is an object whose properties are entries of their own, filling the variables of the list's items.
        var item = list.Parts[0];
        Assert.True(item.IsObject);
        Assert.False(item.IsStatic);
        Assert.Equal("record.data.TechnicalAssurances[0]", item.Location);
        Assert.Equal(
            ["osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID", "osdu.data.TechnicalAssurances[].Comment"],
            item.Properties.Select(p => p.Target.Text));
        Assert.Equal("record.data.TechnicalAssurances[0].TechnicalAssuranceTypeID", item.Properties[0].Location);
        Assert.Equal(MappingSourceKind.Expression, item.Properties[0].Source!.Kind);
        Assert.Equal(ModifierKind.Id, Assert.Single(item.Properties[0].Modifiers).Kind);
        Assert.Equal("Set by the conversion", item.Properties[1].Static!.GetValue<string>());

        // The second is written as the literal it is, which every record carries.
        Assert.True(list.Parts[1].IsPlainLiteral);
        Assert.Equal("""[{"TechnicalAssuranceTypeID":"dev:reference-data--TechnicalAssuranceType:Unevaluated:"}]""", list.Static!.ToJsonString());

        // What the list reads is what its items' properties read.
        Assert.Equal(["log_source"], list.Columns.Select(c => c.Column).Distinct());
        Assert.Contains(list.ValueNodes, node => node.Source?.Kind == MappingSourceKind.Expression);
        Assert.Contains(list.Expressions, e => e.Text.StartsWith("iif(", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_item_is_the_object_its_properties_give_computed_from_the_row()
    {
        var mapping = Mapping(Assurance);

        Assert.Equal(
            Canonical($$"""[{"TechnicalAssuranceTypeID":"{{Certified}}","Comment":"Set by the conversion"}]"""),
            Assurances(Rendered(mapping, ("log_source", "STAT_COMP"))));
        Assert.Equal(
            Canonical($$"""[{"TechnicalAssuranceTypeID":"{{Unevaluated}}","Comment":"Set by the conversion"}]"""),
            Assurances(Rendered(mapping, ("log_source", "RECALL"))));

        // A blank source is not "stat_", so the failsafe holds for it too.
        Assert.Equal(
            Canonical($$"""[{"TechnicalAssuranceTypeID":"{{Unevaluated}}","Comment":"Set by the conversion"}]"""),
            Assurances(Rendered(mapping, ("log_source", null))));
    }

    [Fact]
    public void Each_property_is_converted_to_the_type_its_own_variable_takes_and_nested_objects_and_lists_are_laid_out()
    {
        var mapping = Mapping("""
            TechnicalAssurances:
              - Score: { $from: score }
                Detail:
                  Note: { $from: note }
                  Level: 2
                Reviewers:
                  - reviewer@x
                  - $from: reviewer
                    $required: false
                $$Odd: kept
              - Comment: fixed
                Score: 7
            """);

        Assert.Equal(
            Canonical("""[{"Score":3,"Detail":{"Note":"checked","Level":2},"Reviewers":["reviewer@x","second@x"],"$Odd":"kept"},{"Comment":"fixed","Score":7}]"""),
            Assurances(Rendered(mapping, ("score", "3"), ("note", "checked"), ("reviewer", "second@x"))));

        // An optional value that is empty leaves its property out of the item; the rest of the item stays.
        Assert.Equal(
            Canonical("""[{"Score":3,"Detail":{"Note":"checked","Level":2},"Reviewers":["reviewer@x"],"$Odd":"kept"},{"Comment":"fixed","Score":7}]"""),
            Assurances(Rendered(mapping, ("score", "3"), ("note", "checked"), ("reviewer", " "))));

        // A value its variable's type cannot take holds the record, naming the variable inside the items.
        var held = Renderer(mapping).Render(Row(("score", "many"), ("note", "n"), ("reviewer", "r@x")));
        Assert.True(held.IsHeld);
        Assert.Contains(held.Holds, h => h.StartsWith("osdu.data.TechnicalAssurances[].Score: value 'many' is not a valid integer", StringComparison.Ordinal));
    }

    [Fact]
    public void An_item_none_of_whose_properties_applies_adds_nothing_and_a_list_left_with_none_is_left_out()
    {
        var mapping = Mapping("""
            TechnicalAssurances:
              - Comment:
                  $value: reviewed
                  $when: status = "REVIEWED"
              - Comment:
                  $from: remark
                  $required: false
            """);

        Assert.Equal(Canonical("""[{"Comment":"reviewed"},{"Comment":"late"}]"""), Assurances(Rendered(mapping, ("status", "reviewed"), ("remark", "late"))));
        Assert.Equal(Canonical("""[{"Comment":"late"}]"""), Assurances(Rendered(mapping, ("status", "DRAFT"), ("remark", "late"))));
        Assert.Null(Assurances(Rendered(mapping, ("status", "DRAFT"), ("remark", null))));
    }

    [Fact]
    public void An_object_two_items_give_alike_is_written_once_where_it_is_first_given()
    {
        var mapping = Mapping("""
            TechnicalAssurances:
              - Comment: { $from: first }
              - Comment: { $from: second }
              - Comment: { $from: third }
            """);

        Assert.Equal(Canonical("""[{"Comment":"a"},{"Comment":"b"}]"""), Assurances(Rendered(mapping, ("first", "a"), ("second", "b"), ("third", "a"))));
    }

    [Fact]
    public void A_required_property_that_gives_nothing_holds_the_record_naming_the_variable_inside_the_items()
    {
        var mapping = Mapping("""
            TechnicalAssurances:
              - Comment: { $from: remark }
            """);

        var result = Renderer(mapping).Render(Row(("remark", null)));
        Assert.True(result.IsHeld);
        Assert.Equal("osdu.data.TechnicalAssurances[].Comment: dataset.remark is empty, and the entry is required", Assert.Single(result.Holds));
    }

    [Fact]
    public void A_reference_is_built_for_the_variable_inside_the_items_and_checked_against_the_cache()
    {
        var mapping = Mapping("""
            TechnicalAssurances:
              - TechnicalAssuranceTypeID:
                  $from: assurance
                  $modifiers: [trim, ref]
                UnitID:
                  $coalesce:
                    - $cache: UnitOfMeasure.id
                      $findBy: Code = unit
                    - $cache: UnitOfMeasure.id
                      $findBy: Code = 'm'
            """);

        // ref reads the entity type from the relationship of the property it fills, which the list itself has none of.
        var result = Rendered(mapping, ("assurance", " Certified "), ("unit", "ft"));
        Assert.Equal(
            Canonical($$"""[{"TechnicalAssuranceTypeID":"{{Certified}}","UnitID":"dev:reference-data--UnitOfMeasure:ft:"}]"""),
            Assurances(result));
        Assert.Contains(result.CacheUsages, u => u.TypeName == "UnitOfMeasure");
        var choice = Assert.Single(result.Choices);
        Assert.Equal(("osdu.data.TechnicalAssurances[].UnitID", 1), (choice.Target, choice.Alternative));

        // The coalesce falls to its second alternative when the first finds nothing.
        Assert.Equal(
            Canonical($$"""[{"TechnicalAssuranceTypeID":"{{Certified}}","UnitID":"dev:reference-data--UnitOfMeasure:m:"}]"""),
            Assurances(Rendered(mapping, ("assurance", "Certified"), ("unit", "yards"))));

        // A code the cache does not hold is a miss on that property, which holds the record.
        var missing = Renderer(mapping).Render(Row(("assurance", "Bogus"), ("unit", "m")));
        Assert.True(missing.IsHeld);
        Assert.Contains(missing.Holds, h => h.StartsWith("osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID:", StringComparison.Ordinal) && h.Contains("Bogus", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lookup_read_only_inside_an_item_counts_as_read_and_its_types_are_read_by_the_mapping()
    {
        var mapping = Mapping(
            """
            TechnicalAssurances:
              - UnitID: { $lookup: unit.id }
            """,
            lookups: """
            lookups:
              unit:
                $cache: UnitOfMeasure
                $findBy: Code = unit
            """);

        Assert.Equal(["UnitOfMeasure"], mapping.CacheTypesRead());
        Assert.Equal(Canonical("""[{"UnitID":"dev:reference-data--UnitOfMeasure:m:"}]"""), Assurances(Rendered(mapping, ("unit", "m"))));
    }

    [Theory]
    [InlineData("""
        TechnicalAssurances:
          - Comment: { $from: remark }
          - plain text
        """, "record.data.TechnicalAssurances[1] is a value, and record.data.TechnicalAssurances[0] is an object: the items of a list are all objects, or all values.")]
    [InlineData("""
        TechnicalAssurances:
          - Comment: { $from: remark }
          - [a, b]
        """, "record.data.TechnicalAssurances[1] is a list, and record.data.TechnicalAssurances[0] is an object")]
    [InlineData("""
        TechnicalAssurances:
          - Comment: { $from: remark }
          - $value: { Comment: fixed }
            $when: not empty(remark)
        """, "record.data.TechnicalAssurances[1] is a node of the mapping language, and the items of a list of objects are objects laid out as the record tree")]
    [InlineData("""
        TechnicalAssurances:
          - Comment: { $from: remark }
          - {}
        """, "record.data.TechnicalAssurances[1] is an empty object; name the properties the item holds, or remove it.")]
    [InlineData("""
        TechnicalAssurances:
          - Comment: { $from: remark }
          - ~
        """, "record.data.TechnicalAssurances[1] is empty; give the item the properties it holds, or remove it.")]
    [InlineData("""
        TechnicalAssurances:
          - Comment: { $from: remark }
            Score:
        """, "record.data.TechnicalAssurances[0].Score has no value; give it one, or remove it.")]
    [InlineData("""
        TechnicalAssurances:
          - Links:
              $forEach: curves
              $item:
                Url: { $from: url }
        """, "record.data.TechnicalAssurances[0].Links repeats rows inside an item of the list of objects at record.data.TechnicalAssurances; a repeated array inside the item of a list is not supported.")]
    [InlineData("""
        TechnicalAssurances:
          - Links:
              - Url: { $from: url }
        """, "record.data.TechnicalAssurances[0].Links is a list of objects inside the items of osdu.data.TechnicalAssurances, and a list of objects inside the items of another array is not supported.")]
    [InlineData("""
        Curves:
          $forEach: curves
          $item:
            CurveID: { $from: curve_id }
            Links:
              - Url: { $from: url }
        """, "is a list of objects inside the items of osdu.data.Curves, and a list of objects inside the items of another array is not supported.")]
    [InlineData("""
        TechnicalAssurances:
          - [ { $from: remark } ]
        """, "record.data.TechnicalAssurances[0] is a list, and an item of a list is a value or an object, never a list of its own")]
    [InlineData("""
        TechnicalAssurances:
          - Comment: { $from: curves.remark }
        """, "record.data.TechnicalAssurances[0].Comment: $from: 'curves.remark' is not a column; name a column of the dataset's own row as it is")]
    [InlineData("""
        TechnicalAssurances:
          - Comment: { $from: remark }
            Note: "{$param.missing}"
        """, "uses {$param.missing}, but the mapping declares no parameter 'missing'.")]
    [InlineData("""
        TechnicalAssurances:
          - Comment: { $form: remark }
        """, "record.data.TechnicalAssurances[0].Comment: '$form' is not a word of the mapping language. Did you mean '$from'?")]
    public void A_list_of_objects_that_cannot_be_rendered_is_refused_where_it_is_written(string data, string message)
        => Assert.Contains(message, Refused(data), StringComparison.Ordinal);

    [Fact]
    public void A_list_of_values_keeps_refusing_what_it_always_refused_and_names_the_list_of_objects()
    {
        // A node beside a literal object makes the list one of objects, whose node item is refused by name.
        Assert.Contains(
            "record.data.TechnicalAssurances[1] is a node of the mapping language",
            Refused("""
                TechnicalAssurances:
                  - Comment: fixed
                  - $from: remark
                """),
            StringComparison.Ordinal);

        // A list of values reads as before.
        var values = Mapping("""
            Aliases:
              - fixed
              - $from: alias
            """).Entries.Single(e => e.Target.Text == "osdu.data.Aliases");
        Assert.True(values.IsList);
        Assert.DoesNotContain(values.Parts, part => part.IsObject);
    }

    [Fact]
    public void The_access_and_legal_lists_are_lists_of_values_and_refuse_objects_by_name()
    {
        // The legal lists are literal, and name the item that reads values.
        var legal = Assert.Throws<FlowValidationException>(() => Mapping(string.Empty, envelope: """
            acl:
              owners: [owners@x]
              viewers: [viewers@x]
            legal:
              legaltags:
                - Tag: { $from: tag }
              otherRelevantDataCountries: [NO]
            """)).Message;
        Assert.Contains("record.legal.legaltags reads values with record.legal.legaltags[0], and the legal tags are a literal list", legal, StringComparison.Ordinal);

        // The access lists keep a literal text every record carries, which a list of objects never is.
        var viewers = Assert.Throws<FlowValidationException>(() => Mapping(string.Empty, envelope: """
            acl:
              owners: [owners@x]
              viewers:
                - Group: { $from: group }
            legal:
              legaltags: [tag]
              otherRelevantDataCountries: [NO]
            """)).Message;
        Assert.Contains("record.acl.viewers must list at least one literal text value", viewers, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interface_s_references_include_those_the_items_of_a_list_of_objects_write()
    {
        var schema = InterfaceSchemas.Describe("logs", Mapping(Assurance), OsduTemplate.From(Schema()));

        var reference = Assert.Single(schema.References);
        Assert.Equal("osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID", reference.Property);
        Assert.Equal(["reference-data--TechnicalAssuranceType"], reference.Targets);
    }

    [Fact]
    public void The_preflight_checks_each_property_against_the_variable_it_fills_and_names_it_on_the_list()
    {
        static IReadOnlyList<ValidationIssue> Checked(MappingDefinition mapping, IReadOnlyDictionary<string, IReadOnlySet<string>>? columns = null)
            => Preflight.Check(mapping, Schema(), Cache(), Context(), columns);

        Assert.DoesNotContain(Checked(Mapping(Assurance)), i => i.Severity == IssueSeverity.Error);

        // A property the items do not have.
        var unknown = Assert.Single(Checked(Mapping("""
            TechnicalAssurances:
              - Bogus: { $from: remark }
            """)), i => i.Severity == IssueSeverity.Error);
        Assert.Contains("record.data.TechnicalAssurances[0].Bogus fills a variable that template", unknown.Message, StringComparison.Ordinal);
        Assert.Equal("osdu.data.TechnicalAssurances", unknown.Target);

        // A value where the variable inside the items takes an object.
        var shape = Assert.Single(Checked(Mapping("""
            TechnicalAssurances:
              - Detail: { $from: remark }
            """)), i => i.Severity == IssueSeverity.Error);
        Assert.Contains("record.data.TechnicalAssurances[0].Detail: dataset.remark is one value, and osdu.data.TechnicalAssurances[].Detail is an object with properties of its own", shape.Message, StringComparison.Ordinal);

        // A ref on a property whose variable points to no entity type.
        Assert.Contains(
            Checked(Mapping("""
                TechnicalAssurances:
                  - Comment: { $from: remark, $modifiers: [ref] }
                """)),
            i => i.Severity == IssueSeverity.Error && i.Target == "osdu.data.TechnicalAssurances"
                && i.Message.Contains("record.data.TechnicalAssurances[0].Comment", StringComparison.Ordinal)
                && i.Message.Contains("osdu.data.TechnicalAssurances[].Comment", StringComparison.Ordinal));

        // A list of objects where the template takes a list of values.
        var aliases = Assert.Single(Checked(Mapping("""
            Aliases:
              - Comment: { $from: remark }
            """)), i => i.Severity == IssueSeverity.Error);
        Assert.Contains("record.data.Aliases is a list of objects whose properties read values, which fills a list of objects the template breaks into properties, and osdu.data.Aliases is a list of string.", aliases.Message, StringComparison.Ordinal);

        // A list of objects inside the items fills a variable no mapping fills, whether it reads values or not.
        Assert.Contains(
            Checked(Mapping("""
                TechnicalAssurances:
                  - Comment: { $from: remark }
                    Links: [ { Url: fixed } ]
                """)),
            i => i.Severity == IssueSeverity.Error
                && i.Message.Contains("osdu.data.TechnicalAssurances[].Links is a list of objects inside the items of another array, which a mapping does not fill", StringComparison.Ordinal));

        // A column an item reads is one the flow's source must hold.
        var columns = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["record"] = new HashSet<string>(["name", "depth"], StringComparer.OrdinalIgnoreCase),
        };
        Assert.Contains(
            Checked(Mapping(Assurance), columns),
            i => i.Severity == IssueSeverity.Error && i.Message.Contains("reads dataset.log_source, which the record table does not hold", StringComparison.Ordinal));
    }

    [Fact]
    public void Coverage_shows_what_the_items_write_and_how_surely()
    {
        var mapping = Mapping(Assurance + "\n" + """
              - TechnicalAssuranceTypeID: "dev:reference-data--TechnicalAssuranceType:Unevaluated:"
                Score:
                  $from: score
                  $required: false
            """);

        var report = MappingCoverage.Of(mapping, OsduTemplate.From(Schema()));
        VariableCoverage Variable(string target) => report.Variables.Single(v => v.Target == target);

        // Both items write the type on every row: the first computes it, the second is literal.
        var type = Variable("osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID");
        Assert.Equal(CoverageState.Always, type.State);
        Assert.False(type.Direct);
        Assert.Equal("osdu.data.TechnicalAssurances", type.WrittenBy);
        Assert.Equal([Unevaluated], type.Values);

        // Only the first item carries the comment, and the second's score may be left out.
        Assert.Equal(CoverageState.Sometimes, Variable("osdu.data.TechnicalAssurances[].Comment").State);
        Assert.Equal(["Set by the conversion"], Variable("osdu.data.TechnicalAssurances[].Comment").Values);
        Assert.Equal(CoverageState.Sometimes, Variable("osdu.data.TechnicalAssurances[].Score").State);
        Assert.Equal(CoverageState.Empty, Variable("osdu.data.TechnicalAssurances[].Reviewers").State);

        // The list itself is written directly by its entry.
        Assert.True(Variable("osdu.data.TechnicalAssurances").Direct);
        Assert.DoesNotContain(report.Issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void A_mapping_s_shape_draws_each_item_with_its_properties_placeholders()
    {
        var shape = MappingRenderer.Shape(
            Mapping(Assurance + "\n" + """
                  - Comment: fixed
                """),
            Schema(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["dataPartition"] = "dev" });

        var items = shape.Document["data"]!["TechnicalAssurances"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.StartsWith("<string from iif(startsWith(log_source, \"stat_\"), \"Certified\", \"Unevaluated\")", items[0]!["TechnicalAssuranceTypeID"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Set by the conversion", items[0]!["Comment"]!.GetValue<string>());
        Assert.Equal("""{"Comment":"fixed"}""", items[1]!.ToJsonString());
    }

    [Fact]
    public void A_fixture_is_rewritten_with_each_item_s_properties_in_the_order_the_item_writes_them()
    {
        const string Fixtures = """
            fixtures:
              - name: scored
                row: { name: w, depth: "1", score: "3" }
                expected: "{}"
            """;
        var yaml = Document(
            """
            TechnicalAssurances:
              - Score: { $from: score }
                Comment: fixed
            """,
            Fixtures).ReplaceLineEndings("\n");
        var mapping = new DeliveryDocumentLoader().ParseMapping(yaml, "assured.yaml");

        var rewrite = FixtureRewriter.Rewrite(yaml, mapping, Preflight.RenderFixtures(mapping, Renderer(mapping)), "assured.yaml");

        Assert.Equal(FixtureOutcomeKind.Updated, Assert.Single(rewrite.Outcomes).Kind);
        Assert.Contains("\"TechnicalAssurances\": [\n", rewrite.Text, StringComparison.Ordinal);
        Assert.Contains("{ \"Score\": 3, \"Comment\": \"fixed\" }\n", rewrite.Text, StringComparison.Ordinal);

        // What was written passes the gate's comparison.
        var reread = new DeliveryDocumentLoader().ParseMapping(rewrite.Text, "assured.yaml");
        Assert.Equal(FixtureOutcomeKind.Unchanged, Assert.Single(FixtureRewriter.Rewrite(rewrite.Text, reread, Preflight.RenderFixtures(reread, Renderer(reread)), "assured.yaml").Outcomes).Kind);
    }

    [Fact]
    public void The_builder_opens_a_list_of_objects_and_writes_it_back_as_the_same_mapping()
    {
        var original = Mapping(Assurance + "\n" + """
              - TechnicalAssuranceTypeID: "dev:reference-data--TechnicalAssuranceType:Unevaluated:"
              - Detail:
                  Note: { $from: note, $required: false }
                  Level: 2
                Reviewers: [reviewer@x, { $from: reviewer, $when: not empty(note) }]
                UnitID:
                  $coalesce:
                    - $cache: UnitOfMeasure.id
                      $findBy: Code = unit
                    - $value: "dev:reference-data--UnitOfMeasure:m:"
            """);

        var draft = MappingBuilder.FromDefinition(original);
        Assert.Empty(MappingBuilder.Incomplete(draft));

        var list = draft.Entries.Single(e => e.Target == "osdu.data.TechnicalAssurances");
        Assert.Equal(MappingDraftInput.List, list.Input);
        Assert.Equal([MappingDraftInput.Group, MappingDraftInput.Static, MappingDraftInput.Group], list.Items.Select(i => i.Input));
        Assert.Equal(
            ["osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID", "osdu.data.TechnicalAssurances[].Comment"],
            list.Items[0].Properties.Select(p => p.Target));
        Assert.Equal(MappingDraftInput.Expression, list.Items[0].Properties[0].Input);
        Assert.Equal(
            ["osdu.data.TechnicalAssurances[].Detail.Note", "osdu.data.TechnicalAssurances[].Detail.Level", "osdu.data.TechnicalAssurances[].Reviewers", "osdu.data.TechnicalAssurances[].UnitID"],
            list.Items[2].Properties.Select(p => p.Target));
        Assert.Equal([MappingDraftInput.Dataset, MappingDraftInput.Static, MappingDraftInput.List, MappingDraftInput.Coalesce], list.Items[2].Properties.Select(p => p.Input));

        var yaml = MappingBuilder.ToYaml(draft).ReplaceLineEndings("\n");
        Assert.Contains(
            "    TechnicalAssurances:\n      - TechnicalAssuranceTypeID:\n          $expr: ",
            yaml,
            StringComparison.Ordinal);
        Assert.Contains("        Comment: Set by the conversion\n      - TechnicalAssuranceTypeID: ", yaml, StringComparison.Ordinal);
        Assert.Contains("      - Detail:\n          Note:\n", yaml, StringComparison.Ordinal);

        var reread = new DeliveryDocumentLoader().ParseMapping(yaml, "assured.yaml");
        var again = MappingBuilder.FromDefinition(reread);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(JsonSerializer.Serialize(draft, json), JsonSerializer.Serialize(again, json));
        Assert.Equal(yaml, MappingBuilder.ToYaml(again).ReplaceLineEndings("\n"));

        // What it wrote renders as the original does.
        var row = new[] { ("log_source", (string?)"stat_x"), ("note", "n"), ("reviewer", "r@x"), ("unit", "ft") };
        Assert.Equal(Assurances(Rendered(original, row)), Assurances(Rendered(reread, row)));
    }

    [Fact]
    public void The_builder_names_what_an_item_of_a_list_of_objects_lacks_on_the_list()
    {
        const string Target = "osdu.data.TechnicalAssurances";
        static MappingDraftEntry Property(string name, MappingDraftInput input) => new() { Target = $"{Target}[].{name}", Input = input };
        static MappingDraftEntry Group(params MappingDraftEntry[] properties) => new() { Target = Target, Input = MappingDraftInput.Group, Properties = properties };

        var draft = MappingBuilder.FromDefinition(Mapping(string.Empty));
        draft = draft with
        {
            Entries =
            [
                .. draft.Entries,
                new MappingDraftEntry
                {
                    Target = Target,
                    Input = MappingDraftInput.List,
                    Items =
                    [
                        Group(),
                        Group(Property("Comment", MappingDraftInput.Dataset)) with { When = "not empty(name)" },
                        Group(
                            Property("Links", MappingDraftInput.Repeat) with { Child = "curves" },
                            new MappingDraftEntry { Target = "osdu.data.Aliases", Input = MappingDraftInput.Static, Static = "\"x\"" },
                            Property("Comment", MappingDraftInput.Expression) with { Expression = "1 +" },
                            Property("Comment", MappingDraftInput.Static) with { Static = "\"again\"" },
                            Property("Detail", MappingDraftInput.Static) with { Static = """{"Note":"n"}""" },
                            Property("Detail.Level", MappingDraftInput.Static) with { Static = "2" },
                            Property("Reviewers", MappingDraftInput.List) with { Items = [Group(Property("Url", MappingDraftInput.Static) with { Static = "\"u\"" })] }),
                        new MappingDraftEntry { Target = Target, Input = MappingDraftInput.Dataset, Column = "name" },
                        new MappingDraftEntry { Target = Target, Input = MappingDraftInput.Static, Static = """{"Comment":"x"}""", When = "not empty(name)" },
                    ],
                },
                Property("Score", MappingDraftInput.Group),
                new MappingDraftEntry { Target = "osdu.data.Name", Input = MappingDraftInput.Group },
                new MappingDraftEntry { Target = "osdu.data.Curves", Input = MappingDraftInput.Repeat, Child = "curves" },
                new MappingDraftEntry
                {
                    Target = "osdu.data.Curves[].Links",
                    Input = MappingDraftInput.List,
                    Items = [new MappingDraftEntry { Target = "osdu.data.Curves[].Links", Input = MappingDraftInput.Group, Properties = [new MappingDraftEntry { Target = "osdu.data.Curves[].Links[].Url", Input = MappingDraftInput.Dataset, Column = "url" }] }],
                },
            ],
        };

        var issues = MappingBuilder.Incomplete(draft);
        void Named(string target, string text)
            => Assert.Contains(issues, i => i.Target == target && i.Message.Contains(text, StringComparison.Ordinal));

        Named(Target, $"{Target} item 1: add the item's properties");
        Named(Target, $"{Target} item 2: an object item is written as its properties alone, so it takes no condition");
        Named(Target, $"{Target} item 2: choose the dataset column");
        Named(Target, $"{Target}[].Links in {Target} item 3 repeats a child dataset's rows, and a repeated array inside an item of a list is not supported.");
        Named(Target, $"{Target} item 3: osdu.data.Aliases is not a variable of the items of {Target}");
        Named(Target, $"{Target}[].Comment in {Target} item 3: ");
        Named(Target, $"{Target}[].Comment in {Target} item 3 has more than one entry.");
        Named(Target, $"{Target} item 3: {Target}[].Detail.Level lies inside {Target}[].Detail, which an entry fills whole.");
        Named(Target, $"{Target}[].Reviewers in {Target} item 3 is a list of objects inside an item of a list, which is not supported.");
        Named(Target, $"{Target} item 4: the items of a list are all objects or all values");
        Named(Target, $"{Target} item 5: a fixed item is written as the object it is, so it takes no condition or description");
        Named("osdu.data.Name", "an object input is an item of a list of objects");
        Named("osdu.data.Curves[].Links", "osdu.data.Curves[].Links is a list of objects inside the items of osdu.data.Curves, and a list of objects inside the items of another array is not supported.");
        Assert.Contains(issues, i => i.Message.Contains($"{Target}[].Score fills a property of the items of {Target}, whose items a list writes; fill it in an item of that list", StringComparison.Ordinal));
    }
}
