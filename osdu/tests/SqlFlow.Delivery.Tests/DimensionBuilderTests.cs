using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The explorer's dimension builder as YAML (osdu/docs/explorer.md, Building a dimension): a draft written as the item a
/// dimension flow lists, in the documented style, read back by the document loader into exactly the dimension it says; every
/// value YAML would misread quoted so it reads back as typed; what is missing said before anything is read; and what the
/// loader refuses said in its own words, without the check document's name, pointed at the part it is about.
/// </summary>
public class DimensionBuilderTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string CountryName = "data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName";

    /// <summary>The recall estate's Wellbore dimension (Welllog/flows/recall-welllog-05-dimensions.yaml), as the builder holds it.</summary>
    private static DimensionDraft Wellbore() => new()
    {
        Name = "Wellbore",
        Description = "The wellbore of each log, by its name.",
        Kind = WellLog,
        Path = "data.WellboreID",
        Label = ["data.FacilityName"],
        Unlabelled = "Not specified",
        Attributes =
        [
            new DimensionDraftAttributeSpec("UUID", ["data.FacilityID"], null),
            new DimensionDraftAttributeSpec("Field", ["data.GeoContexts.FieldID", "data.FieldName"], null),
            new DimensionDraftAttributeSpec("Country", ["data.GeoContexts.GeoPoliticalEntityID", CountryName], null),
            new DimensionDraftAttributeSpec("Source", null, "data.Source"),
        ],
    };

    private static DimensionSpec Read(DimensionDraft draft)
        => new DeliveryDocumentLoader().ParseDimension(DimensionBuilder.Document(DimensionBuilder.ToYaml(draft)), DimensionBuilder.CheckSource).Dimensions[0];

    private static string Refused(DimensionDraft draft)
        => Assert.Throws<FlowValidationException>(() => Read(draft)).Message;

    [Fact]
    public void The_recall_wellbore_dimension_is_written_as_its_flow_writes_it()
    {
        var yaml = DimensionBuilder.ToYaml(Wellbore());

        Assert.Equal(
            """
              - name: Wellbore
                description: The wellbore of each log, by its name.
                kind: osdu:wks:work-product-component--WellLog:1.4.0
                path: data.WellboreID
                label: data.FacilityName
                unlabelled: Not specified
                attributes:
                  UUID: data.FacilityID
                  Field:
                    - data.GeoContexts.FieldID
                    - data.FieldName
                  Country:
                    - data.GeoContexts.GeoPoliticalEntityID
                    - 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName'
                  Source: { collect: data.Source }

            """.ReplaceLineEndings("\n"),
            yaml);
    }

    [Fact]
    public void What_is_written_reads_back_as_the_dimension_it_says()
    {
        var spec = Read(Wellbore());

        Assert.Equal(("Wellbore", WellLog, "data.WellboreID"), (spec.Name, spec.Kind, spec.Path));
        Assert.Equal("The wellbore of each log, by its name.", spec.Description);
        Assert.Equal(["data.FacilityName"], spec.Label);
        Assert.Equal("Not specified", spec.Unlabelled);
        Assert.Equal(["UUID", "Field", "Country", "Source"], spec.Attributes.Select(a => a.Name));
        Assert.Equal(["data.GeoContexts.GeoPoliticalEntityID", CountryName], spec.Attributes[2].Steps);
        Assert.Equal("data.Source", spec.Attributes[3].Collect);
        Assert.Empty(spec.Attributes[3].Steps);

        // The table's two columns are named after what the dimension reads, as a flow's own are.
        Assert.Equal(("WellboreID", "FacilityName"), (spec.KeyColumn, spec.ValueColumn));
        Assert.Empty(spec.Clean);
        Assert.Equal((false, DimensionSpec.DefaultMaxValues), (spec.CountRecords, spec.MaxValues));
    }

    [Fact]
    public void The_columns_the_clean_steps_and_the_counting_are_written_and_read_back()
    {
        var draft = Wellbore() with
        {
            Query = "data.Source:\"Recall\" AND NOT data.Name:\"x, y\"",
            KeyColumn = "Wellbore_Id",
            ValueColumn = "WellboreName",
            Clean =
            [
                new DimensionDraftCleanStep("trim", null, null),
                new DimensionDraftCleanStep("collapseSpaces", null, null),
                new DimensionDraftCleanStep("replace", @"\s*\(.*\)$", ""),
                new DimensionDraftCleanStep("replace", "'quoted' \"both\"", "x\ty"),
                new DimensionDraftCleanStep("upper", null, null),
            ],
            CountRecords = true,
            MaxValues = 250_000,
        };

        var yaml = DimensionBuilder.ToYaml(draft);
        Assert.Contains("    query: 'data.Source:\"Recall\" AND NOT data.Name:\"x, y\"'\n", yaml, StringComparison.Ordinal);
        Assert.Contains("    columns: { key: Wellbore_Id, value: WellboreName }\n", yaml, StringComparison.Ordinal);
        Assert.Contains("      - replace: { pattern: '\\s*\\(.*\\)$', with: '' }\n", yaml, StringComparison.Ordinal);
        Assert.Contains("    countRecords: true\n    maxValues: 250000\n", yaml, StringComparison.Ordinal);

        var spec = Read(draft);
        Assert.Equal("data.Source:\"Recall\" AND NOT data.Name:\"x, y\"", spec.Query);
        Assert.Equal(("Wellbore_Id", "WellboreName"), (spec.KeyColumn, spec.ValueColumn));
        Assert.Equal(
            [CleanStepKind.Trim, CleanStepKind.CollapseSpaces, CleanStepKind.Replace, CleanStepKind.Replace, CleanStepKind.Upper],
            spec.Clean.Select(c => c.Kind));
        Assert.Equal((@"\s*\(.*\)$", string.Empty), (spec.Clean[2].Pattern, spec.Clean[2].With));
        Assert.Equal(("'quoted' \"both\"", "x\ty"), (spec.Clean[3].Pattern, spec.Clean[3].With));
        Assert.Equal((true, 250_000L), (spec.CountRecords, spec.MaxValues));
    }

    [Fact]
    public void A_key_that_is_its_own_value_writes_no_label_and_names_its_value_after_the_dimension()
    {
        var draft = new DimensionDraft { Name = "CurveMnemonic", Kind = "*:*:work-product-component--WellLog:*", Path = "data.Curves.Mnemonic", Clean = [new("upper", null, null)] };

        var yaml = DimensionBuilder.ToYaml(draft);
        Assert.Equal(
            "  - name: CurveMnemonic\n    kind: '*:*:work-product-component--WellLog:*'\n    path: data.Curves.Mnemonic\n    clean:\n      - upper\n",
            yaml);

        var spec = Read(draft);
        Assert.Empty(spec.Label);
        Assert.Null(spec.Unlabelled);
        Assert.Equal(("Mnemonic", "CurveMnemonic"), (spec.KeyColumn, spec.ValueColumn));
    }

    [Fact]
    public void Blank_parts_are_left_out_and_the_three_a_dimension_needs_are_asked_for_first()
    {
        var draft = new DimensionDraft
        {
            Name = "  ",
            Description = " ",
            Kind = "",
            Path = null,
            Label = [" ", ""],
            Attributes = [new DimensionDraftAttributeSpec("Field", [" "], null)],
            Clean = [new DimensionDraftCleanStep(" ", null, null)],
            KeyColumn = " ",
        };

        Assert.Equal("  - name: ''\n    attributes:\n      Field: []\n", DimensionBuilder.ToYaml(draft));

        var missing = DimensionBuilder.Incomplete(draft);
        Assert.Equal(["name", "kind", "path"], missing.Select(i => i.Target));
        Assert.All(missing, i => Assert.Equal(DimensionDraftIssue.Error, i.Severity));
        Assert.Empty(DimensionBuilder.Incomplete(Wellbore()));
    }

    /// <summary>Texts a person may type that YAML would read as something else, or not at all, written bare.</summary>
    public static TheoryData<string> Awkward => new()
    {
        "yes", "No", "TRUE", "null", "~", "1.0", "42", "1e5", "0x1F", "1_000", ".inf", "-12",
        "- a dash first", "# a hash first", "a: colon and space", "a #comment", "ends with a colon:", "*star", "&anchor", "!tag", "%percent", "@at",
        "`tick", "[bracket", "{brace", "|pipe", ">fold", "'single' quote", "\"double\" quote", "back\\slash", "two  spaces",
        "multi\nline", "tab\tinside", "bell\u0007", "unicode øæå 日本", "emoji 🚀", "a, comma", "(parenthesis",
    };

    [Theory]
    [MemberData(nameof(Awkward))]
    public void Every_text_reads_back_exactly_as_it_was_typed(string text)
    {
        var draft = Wellbore() with { Description = text, Unlabelled = text };

        var spec = Read(draft);

        Assert.Equal(text.Trim(), spec.Description);
        Assert.Equal(text.Trim(), spec.Unlabelled);
    }

    [Theory]
    [MemberData(nameof(Awkward))]
    public void A_clean_steps_pattern_and_replacement_read_back_exactly(string text)
    {
        var spec = Read(Wellbore() with { Clean = [new DimensionDraftCleanStep("replace", "x", text)] });

        Assert.Equal(text, spec.Clean[0].With);
    }

    [Theory]
    [InlineData("data.WellboreID", true)]
    [InlineData("osdu:wks:work-product-component--WellLog:1.4.0", true)]
    [InlineData("Not specified", true)]
    [InlineData("a, b", true)]
    [InlineData("*:*:master-data--Wellbore:*", false)]
    [InlineData("data[Type=x].Name", false)]
    [InlineData("dev:master-data--Wellbore:1:", false)]
    [InlineData("", false)]
    [InlineData("true", false)]
    [InlineData("2", false)]
    public void Only_what_reads_back_the_same_is_written_bare(string text, bool bare)
    {
        Assert.Equal(bare, DimensionBuilder.Scalar(text) == text);
        Assert.False(DimensionBuilder.FlowScalar("a, b") == "a, b");
    }

    [Fact]
    public void What_the_loader_refuses_is_said_in_its_words_without_the_check_documents_name()
    {
        var issue = DimensionBuilder.FromLoader(Refused(Wellbore() with { Attributes = [new DimensionDraftAttributeSpec("1st", ["data.X"], null)] }));

        Assert.Equal(DimensionDraftIssue.Error, issue.Severity);
        Assert.Equal("attributes.1st", issue.Target);
        Assert.StartsWith("attributes.1st is not an attribute name", issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(DimensionBuilder.CheckSource + ":", issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dimensions[0]", issue.Message, StringComparison.Ordinal);
    }

    /// <summary>A draft the loader refuses, and the part of the dimension the refusal points at.</summary>
    public static TheoryData<DimensionDraft, string> RefusedDrafts => new()
    {
        { Wellbore() with { Name = "Well bore" }, "name" },
        { Wellbore() with { Kind = "osdu:wks:WellLog" }, "kind" },
        { Wellbore() with { Path = "data..WellboreID" }, "path" },
        { Wellbore() with { Path = "acl.nothing" }, "path" },
        { Wellbore() with { Query = "data.X:{region}" }, "query" },
        { Wellbore() with { Label = ["data.A", "data.B", "data.C", "data.D"] }, "label" },
        { Wellbore() with { Label = ["data[Type=a]b].Name"] }, "label" },
        { Wellbore() with { Label = [], Attributes = [], Unlabelled = "None" }, "unlabelled" },
        { Wellbore() with { Attributes = [new DimensionDraftAttributeSpec("key", ["data.X"], null)] }, "attributes.key" },
        { Wellbore() with { Attributes = [new DimensionDraftAttributeSpec("Empty", [], null)] }, "attributes.Empty" },
        {
            Wellbore() with { Attributes = [new DimensionDraftAttributeSpec("A", null, "data.Source"), new DimensionDraftAttributeSpec("B", null, "data.Name")] },
            "attributes.B"
        },
        { Wellbore() with { Attributes = [new DimensionDraftAttributeSpec("FacilityName", ["data.FacilityName"], null)] }, "columns.value" },
        { Wellbore() with { KeyColumn = "id" }, "columns.key" },
        { Wellbore() with { KeyColumn = "FacilityName" }, "columns.key" },
        { Wellbore() with { MaxValues = 0 }, "maxValues" },
        { Wellbore() with { Clean = [new DimensionDraftCleanStep("shout", null, null)] }, "clean" },
        { Wellbore() with { Clean = [new DimensionDraftCleanStep("replace", "(?<=a)b", "c")] }, "clean" },
    };

    [Theory]
    [MemberData(nameof(RefusedDrafts))]
    public void Each_refusal_points_at_the_part_it_is_about(DimensionDraft draft, string target)
    {
        var issue = DimensionBuilder.FromLoader(Refused(draft));

        Assert.Equal(target, issue.Target);
        Assert.False(string.IsNullOrWhiteSpace(issue.Message));
        Assert.DoesNotContain("dimensions[0]", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_required_key_is_named_alone()
    {
        var issue = DimensionBuilder.FromLoader("builder: 'dimensions[0].kind' is required.");

        Assert.Equal(("kind is required.", "kind"), (issue.Message, issue.Target));
    }

    [Theory]
    [InlineData("path 'x' is not a property path", "path")]
    [InlineData("label[2] 'x' is not a property path", "label")]
    [InlineData("attributes.Country reads nothing", "attributes.Country")]
    [InlineData("columns.key 'value' is not", "columns.key")]
    [InlineData("clean[3]: 'shout' is not a clean step", "clean")]
    [InlineData("the key's column would be named 'id'", "columns.key")]
    [InlineData("the value's column would be named 'key'", "columns.value")]
    [InlineData("pathology is not a part", null)]
    [InlineData("dimensions must list at least one dimension", null)]
    public void A_message_points_at_the_part_it_starts_with(string message, string? target)
    {
        Assert.Equal(target, DimensionBuilder.TargetOf(message));
    }

    [Fact]
    public void The_check_document_lists_the_item_after_its_header_and_the_item_is_found_in_it()
    {
        var item = DimensionBuilder.ToYaml(Wellbore());
        var document = DimensionBuilder.Document(item);

        Assert.Equal(DimensionBuilder.DocumentHeaderLines, document[..document.IndexOf(item, StringComparison.Ordinal)].Count(c => c == '\n'));
        var block = DimensionYamlSource.Locate(document, "Wellbore");
        Assert.NotNull(block);
        Assert.Equal(DimensionBuilder.DocumentHeaderLines + 1, block.FirstLine);
        Assert.Equal(item.TrimEnd('\n').Split('\n'), block.Lines);
        var targets = block.Spans.Select(s => s.Target).ToHashSet();
        Assert.Superset(new HashSet<string> { "kind", "path", "label", "unlabelled", "attributes.Country", "attributes.Country.1", "attributes.Source.collect" }, targets);
    }

    [Fact]
    public void A_draft_of_every_part_at_its_limit_still_reads_back()
    {
        var draft = Wellbore() with
        {
            Attributes = Enumerable.Range(0, DimensionSpec.MaxAttributes - 1)
                .Select(i => new DimensionDraftAttributeSpec($"A{i}", ["data.GeoContexts.GeoPoliticalEntityID", "data.ParentID", CountryName], null))
                .Append(new DimensionDraftAttributeSpec("Source", null, "data.Source"))
                .ToList(),
            Clean = Enumerable.Range(0, DimensionSpec.MaxCleanSteps).Select(_ => new DimensionDraftCleanStep("trim", null, null)).ToList(),
        };

        var spec = Read(draft);

        Assert.Equal(DimensionSpec.MaxAttributes, spec.Attributes.Count);
        Assert.Equal(DimensionSpec.MaxCleanSteps, spec.Clean.Count);
        Assert.True(DimensionBuilder.ToYaml(draft).Length < Engine.Operations.LongArgument.MaxLength);
    }
}
