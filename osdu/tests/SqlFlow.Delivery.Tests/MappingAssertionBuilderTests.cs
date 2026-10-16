using System.Text.Json;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A mapping's assertions in the mapping builder (osdu/docs/reference/flow/mapping-assertions.md): a mapping opened in the
/// builder keeps every assertion of every node that takes one and is written back as the same assertions, a draft's
/// assertions are written as an author writes them, and what the loader would refuse is named on the entry it is about.
/// </summary>
public sealed class MappingAssertionBuilderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly DeliveryDocumentLoader Loader = new();

    /// <summary>Assertions on a column, an expression, a coalesce, a lookup, a repeat, an item of a repeat and an object of a list.</summary>
    private const string Data = """
        Weight:
          $from: weight
          $required: false
          $assert:
            - name: weight-placeholder
              description: The source writes -999 for an unknown weight.
              stage: incoming
              notIn: [-999, -999.25]
              onFail: omit
              where:
                - column: weight_unit
                  equals: KG
                  ignoreCase: true
            - atMost: 10000
              tolerance: 0.5
              onFail: report
        Count:
          $expr: depth * 2
          $assert:
            - between: [0, 250]
              where:
                - field: data.Name
                  exists: true
        Symbol:
          $from: symbol
          $assert:
            - matches: '^[A-Z]+$'
            - notMatches: '\s'
            - in: [GR, 'it''s', '5']
            - type: string
            - length: { atLeast: 1, atMost: 16 }
            - empty: false
        Unit:
          $coalesce:
            - $cache: UnitOfMeasure.id
              $findBy: Code = unit
            - $from: unit_id
          $assert:
            - startsWith: 'dev:'
        WellboreID:
          $lookup: well.id
          $assert:
            - contains: Wellbore
              onFail: report
        Curves:
          $forEach: curves
          $required: false
          $assert:
            - length: { atLeast: 2 }
              onFail: report
          $item:
            CurveID:
              $from: curve_id
              $assert:
                - equals: GR
                  values: any
                  name: a gamma ray curve
                - stage: incoming
                  notEquals: DEPT
                  where:
                    - column: curve_unit
                      exists: true
                    - column: $dataset.name
                      startsWith: W
            TopDepth: { $from: top }
        """;

    private static string Document()
        => TestSchema.MappingDocument(Data).ReplaceLineEndings("\n")
            .Replace("record:\n", "lookups:\n  well:\n    $cache: Wellbore\n    $findBy: FacilityName = name\nrecord:\n", StringComparison.Ordinal);

    /// <summary>Every assertion of a mapping as one line: where it is, what it asserts and how, and the conditions it is judged under.</summary>
    private static List<string> Described(MappingDefinition mapping)
        => mapping.Assertions()
            .Select(pair => pair.Assertion)
            .Select(a => string.Join(
                " | ",
                a.Location, a.Label, a.Named, a.Stage, a.OnFail, a.AnyValue, a.Description,
                a.Condition.Operator, ConditionReader.Written(a.Condition), a.Condition.IgnoreCase, a.Condition.Tolerance,
                string.Join(" & ", a.Where.Select(w => $"{w.Field}{w.Column} {w.Condition.Operator} {ConditionReader.Written(w.Condition)} {w.Condition.IgnoreCase}"))))
            .ToList();

    [Fact]
    public void A_mappings_assertions_opened_in_the_builder_are_written_back_as_the_same_assertions()
    {
        var original = Loader.ParseMapping(Document(), "thing.yaml");
        Assert.Equal(14, original.Assertions().Count());

        var draft = MappingBuilder.FromDefinition(original);
        Assert.Empty(MappingBuilder.Incomplete(draft));
        var yaml = MappingBuilder.ToYaml(draft);
        var reread = Loader.ParseMapping(yaml, "thing.yaml");
        var again = MappingBuilder.FromDefinition(reread);

        Assert.Equal(Described(original), Described(reread));
        Assert.Equal(JsonSerializer.Serialize(draft, Json), JsonSerializer.Serialize(again, Json));
        Assert.Equal(yaml, MappingBuilder.ToYaml(again));

        // The coalesce entry keeps the assertion; its alternatives hold none.
        var unit = draft.Entries.Single(e => e.Target == "osdu.data.Unit");
        Assert.Single(unit.Assertions);
        Assert.All(unit.Alternatives, alternative => Assert.Empty(alternative.Assertions));
    }

    [Fact]
    public void A_drafts_assertions_are_written_as_an_author_writes_them()
    {
        var draft = MappingBuilder.FromDefinition(Loader.ParseMapping(Document(), "thing.yaml"));
        var yaml = MappingBuilder.ToYaml(draft).ReplaceLineEndings("\n");

        Assert.Contains(
            """
                Weight:
                  $from: weight
                  $required: false
                  $assert:
                    - notIn: [-999, -999.25]
                      name: weight-placeholder
                      stage: incoming
                      onFail: omit
                      where:
                        - column: weight_unit
                          equals: 'KG'
                          ignoreCase: true
                      description: The source writes -999 for an unknown weight.
                    - atMost: 10000
                      onFail: report
                      tolerance: 0.5
            """.ReplaceLineEndings("\n"),
            yaml,
            StringComparison.Ordinal);

        // Text stays text ('5'), a quote inside text is doubled, and a pattern keeps its backslash.
        Assert.Contains("        - in: ['GR', 'it''s', '5']\n", yaml, StringComparison.Ordinal);
        Assert.Contains("        - notMatches: '\\s'\n", yaml, StringComparison.Ordinal);
        Assert.Contains("        - length: { atLeast: 1, atMost: 16 }\n", yaml, StringComparison.Ordinal);

        // A condition of an item reads the item's row by its own name, and the row the array is in through $dataset.
        Assert.Contains("              where:\n                - column: curve_unit\n                  exists: true\n", yaml, StringComparison.Ordinal);
        Assert.Contains("startsWith: 'W'", yaml, StringComparison.Ordinal);
        Assert.Contains("$dataset.name", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_property_of_an_object_in_a_list_keeps_its_assertions()
    {
        var mapping = Loader.ParseMapping(TestSchema.MappingDocument("""
            Curves:
              - CurveID:
                  $from: curve_id
                  $assert:
                    - notEquals: DEPT
                      onFail: report
                TopDepth: { $from: top }
            """), "thing.yaml");
        var draft = MappingBuilder.FromDefinition(mapping);
        Assert.Empty(MappingBuilder.Incomplete(draft));

        var reread = Loader.ParseMapping(MappingBuilder.ToYaml(draft), "thing.yaml");
        Assert.Equal(Described(mapping), Described(reread));
        Assert.Equal("record.data.Curves[0].CurveID.$assert[0]", Assert.Single(reread.Assertions()).Assertion.Location);
    }

    [Fact]
    public void What_the_loader_would_refuse_of_an_assertion_is_named_on_its_entry()
    {
        static MappingDraftAssertion Check(string op, string operand) => new() { Operator = op, Operand = operand };
        var draft = MappingBuilderTests.BaseDraft() with
        {
            Entries =
            [
                .. MappingBuilderTests.BaseDraft().Entries,
                new MappingDraftEntry
                {
                    Target = "osdu.data.Weight",
                    Input = MappingDraftInput.Dataset,
                    Column = "weight",
                    Assertions =
                    [
                        Check("resolves", "true"),
                        Check("between", string.Empty),
                        Check("equals", "a\nb"),
                        Check("atMost", "10") with { Stage = "later", OnFail = "drop" },
                        Check("atMost", "10") with { OnFail = AssertionWords.Omit, AnyValue = true },
                        Check("atLeast", "0") with { Name = "same" },
                        Check("atLeast", "1") with { Name = "same" },
                        Check("atLeast", "2") with { Where = [new MappingDraftAssertionFilter("column", "weight_unit", "equals", "'KG'")] },
                        Check("atLeast", "3") with { Stage = AssertionWords.Incoming, Where = [new MappingDraftAssertionFilter("field", "data.Name", "exists", "true")] },
                    ],
                },
                new MappingDraftEntry
                {
                    Target = "osdu.data.Unit",
                    Input = MappingDraftInput.Cache,
                    CacheType = "UnitOfMeasure",
                    CacheField = "id",
                    FindBy = [new MappingDraftFind("Code", "unit", null)],
                    Assertions = [Check("startsWith", "'dev:'") with { Stage = AssertionWords.Incoming }],
                },
                new MappingDraftEntry { Target = "osdu.data.Symbol", Input = MappingDraftInput.Static, Static = "\"GR\"", Assertions = [Check("equals", "'GR'")] },
                new MappingDraftEntry
                {
                    Target = "osdu.data.Aliases",
                    Input = MappingDraftInput.List,
                    Items = [new MappingDraftEntry { Target = "osdu.data.Aliases", Input = MappingDraftInput.Dataset, Column = "alias", Assertions = [Check("equals", "'A'")] }],
                },
                new MappingDraftEntry
                {
                    Target = "osdu.data.Nested.Inner",
                    Input = MappingDraftInput.Coalesce,
                    Alternatives =
                    [
                        new MappingDraftEntry { Target = "osdu.data.Nested.Inner", Input = MappingDraftInput.Dataset, Column = "inner", Assertions = [Check("equals", "'x'")] },
                        new MappingDraftEntry { Target = "osdu.data.Nested.Inner", Input = MappingDraftInput.Static, Static = "\"none\"" },
                    ],
                },
            ],
        };

        var issues = MappingBuilder.Incomplete(draft);
        void Named(string target, string text)
            => Assert.Contains(issues, i => i.Target == target && i.Message.Contains(text, StringComparison.Ordinal));

        Named("osdu.data.Weight", "assertion 1: 'resolves' is not a condition; use one of equals");
        Named("osdu.data.Weight", "assertion 2: give between what it compares with, such as [0, 250].");
        Named("osdu.data.Weight", "assertion 3: write what equals compares with on one line");
        Named("osdu.data.Weight", "assertion 4: its stage is record or incoming.");
        Named("osdu.data.Weight", "assertion 4: what a failure does is hold, report or omit.");
        Named("osdu.data.Weight", "assertion 5: omit leaves the value out of the record, and the property is required");
        Named("osdu.data.Weight", "assertion 5: omit leaves out each value that fails");
        Named("osdu.data.Weight", "assertion 7: another assertion of this property is named 'same'");
        Named("osdu.data.Weight", "assertion 8: each of its conditions reads a field of the record, such as data.Name; name the field.");
        Named("osdu.data.Weight", "assertion 9: each of its conditions reads a column of the row");
        Named("osdu.data.Unit", "assertion 1: the incoming stage judges the value the row gives");
        Named("osdu.data.Symbol", "a fixed value gives every record the same one; remove its assertions");
        Named("osdu.data.Aliases", "osdu.data.Aliases item 1: an assertion judges the value of a property, and an item gives one of the values of the list");
        Named("osdu.data.Nested.Inner", "osdu.data.Nested.Inner alternative 1: an assertion decides for the whole $coalesce entry");
        Assert.DoesNotContain(issues, i => i.Message.Contains("assertion 6", StringComparison.Ordinal));

        // The YAML written beside the issues keeps its shape: the operand with a line break is written on one line.
        Assert.Contains("- equals: a b", MappingBuilder.ToYaml(draft), StringComparison.Ordinal);
    }

    [Fact]
    public void An_assertion_with_its_settings_left_empty_takes_their_defaults()
    {
        var draft = MappingBuilderTests.BaseDraft() with
        {
            Entries =
            [
                .. MappingBuilderTests.BaseDraft().Entries,
                new MappingDraftEntry
                {
                    Target = "osdu.data.Count",
                    Input = MappingDraftInput.Dataset,
                    Column = "count",
                    Assertions = [new MappingDraftAssertion { Operator = "between", Operand = "[0, 250]", Stage = null!, OnFail = " ", Name = " " }],
                },
            ],
        };

        Assert.Empty(MappingBuilder.Incomplete(draft));
        var assertion = Assert.Single(Loader.ParseMapping(MappingBuilder.ToYaml(draft), "thing.yaml").Assertions()).Assertion;
        Assert.Equal((AssertionStage.Record, AssertionAction.Hold, false, "between 0 and 250"), (assertion.Stage, assertion.OnFail, assertion.Named, assertion.Label));
    }
}
