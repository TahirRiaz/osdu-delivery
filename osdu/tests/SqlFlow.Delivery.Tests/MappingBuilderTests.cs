using System.Text.Json;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>The mapping builder: drafts from a template and the cache, what a draft still lacks, and the YAML it is written as.</summary>
public class MappingBuilderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static MappingDefinition Sample(string reference) => new MappingCatalog(Samples.Mappings, new DeliveryDocumentLoader()).Load(reference);

    private static async Task<ReferenceSnapshot> SampleCacheAsync()
        => (await Samples.SampleCache.LoadAsync(Samples.SampleCacheScope, (await Samples.SampleCache.CurrentVersionAsync(Samples.SampleCacheScope))!))!;

    [Theory]
    [InlineData("WellLog@1.4.0")]
    [InlineData("Wellbore@1.0.0")]
    public async Task A_sample_mapping_opened_in_the_builder_is_written_back_as_the_same_mapping(string reference)
    {
        var original = Sample(reference);
        var draft = MappingBuilder.FromDefinition(original);
        Assert.Empty(MappingBuilder.Incomplete(draft));

        var yaml = MappingBuilder.ToYaml(draft);
        var reread = new DeliveryDocumentLoader().ParseMapping(yaml, reference + ".yaml");
        var again = MappingBuilder.FromDefinition(reread);

        Assert.Equal(JsonSerializer.Serialize(draft, Json), JsonSerializer.Serialize(again, Json));
        Assert.Equal(yaml, MappingBuilder.ToYaml(again));

        // What the builder wrote passes the preflight against the sample template and cache, fixtures included.
        var schema = await Samples.SampleTemplates.LoadAsync(reread.Template);
        Assert.NotNull(schema);
        var references = await SampleCacheAsync();
        var context = new RenderContext
        {
            MappingReference = reread.Reference,
            CacheScope = Samples.SampleCacheScope,
            CacheVersion = references.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RenderContext.DataPartitionParameter] = "dev",
                ["aclOwner"] = "data.default.owners@dev.dataservices.energy",
                ["aclViewer"] = "data.default.viewers@dev.dataservices.energy",
                ["legalTag"] = "dev-reference-data-default",
            },
        };
        var searches = await RenderResolver.SearchesAsync(Samples.SampleTemplates, reread);
        var issues = Preflight.Check(reread, schema, references, context, sourceColumns: null, searches);
        Assert.True(issues.All(i => i.Severity != IssueSeverity.Error), string.Join(Environment.NewLine, issues));
    }

    [Fact]
    public async Task A_new_draft_prefills_the_variables_the_cache_can_answer_outside_a_repeater()
    {
        var template = OsduTemplate.From(Samples.SampleTemplate(Samples.WellLogKind));
        var cache = (await SampleCacheAsync()).Types.Select(t => new CachedTypeInfo(t.Name, t.EntityType, t.FieldNames)).ToList();

        var draft = MappingBuilder.Draft(template, cache, "WellLog", "2.0.0", "wells");

        Assert.Equal(new TemplateReference(template.Kind, template.Version), new TemplateReference(draft.TemplateKind, draft.TemplateVersion));
        Assert.Equal(MappingBuilder.EnvelopeTargets, draft.Entries.Take(4).Select(e => e.Target));
        Assert.All(draft.Entries.Take(4), e => Assert.Equal(MappingDraftInput.Static, e.Input));

        var type = Assert.Single(draft.Entries, e => e.Target == "osdu.data.VerticalMeasurement.VerticalMeasurementTypeID");
        Assert.True(type.Prefilled);
        Assert.Equal(MappingDraftInput.Cache, type.Input);
        Assert.Equal("VerticalMeasurementType", type.CacheType);
        Assert.Equal("id", type.CacheField);
        Assert.Equal("Code", Assert.Single(type.FindBy).Field);

        Assert.Equal("UnitOfMeasure", Assert.Single(draft.Entries, e => e.Target == "osdu.data.VerticalMeasurement.VerticalMeasurementUnitOfMeasureID").CacheType);
        Assert.DoesNotContain(draft.Entries, e => e.Target.Contains("[]", StringComparison.Ordinal));

        // Wellbores are searched for rather than cached, so nothing in the cache answers the wellbore reference, and the
        // draft leaves it to be written as a search.
        Assert.DoesNotContain(draft.Entries, e => e.Target == "osdu.data.WellboreID");

        // A draft says what is still missing: the envelope values, the key, and the dataset side of every cache entry.
        var missing = MappingBuilder.Incomplete(draft);
        Assert.Contains(missing, i => i.Target == "osdu.acl.owners" && i.Message.Contains("at least one value", StringComparison.Ordinal));
        Assert.Contains(missing, i => i.Target is null && i.Message.Contains("identify a record", StringComparison.Ordinal));
        Assert.Contains(missing, i => i.Target == "osdu.data.VerticalMeasurement.VerticalMeasurementTypeID" && i.Message.Contains("findBy Code needs the dataset column", StringComparison.Ordinal));
    }

    [Fact]
    public void A_number_modifier_is_written_back_with_its_separators()
    {
        var loader = new DeliveryDocumentLoader();
        var original = loader.ParseMapping(TestSchema.MappingDocument("""
              - { target: osdu.data.Weight, source: dataset.a, modifiers: [number] }
              - { target: osdu.data.Symbol, source: dataset.b, modifiers: [{ number: { decimal: ",", group: " " } }] }
              - { target: osdu.data.Count, source: dataset.c, modifiers: [{ number: { decimal: ",", group: "." } }] }
              - { target: osdu.data.Big, source: dataset.d, modifiers: [{ number: { group: "'" } }] }
            """), "m.yaml");
        var draft = MappingBuilder.FromDefinition(original);

        var yaml = MappingBuilder.ToYaml(draft);
        var again = MappingBuilder.FromDefinition(loader.ParseMapping(yaml, "m.yaml"));

        Assert.Equal(JsonSerializer.Serialize(draft, Json), JsonSerializer.Serialize(again, Json));
        Assert.Contains("- number\n", yaml.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("number: { decimal: \",\", group: \" \" }", yaml, StringComparison.Ordinal);
        Assert.Contains("number: { decimal: \".\", group: \"'\" }", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Incomplete_entries_are_named_by_their_target()
    {
        var draft = new MappingDraft
        {
            Name = "Thing",
            Version = "1.0.0",
            TemplateKind = TestSchema.Kind,
            TemplateVersion = TestSchema.Build().Version,
            System = "test",
            Key = ["name"],
            Entries =
            [
                new MappingDraftEntry { Target = "osdu.data.Name", Input = MappingDraftInput.Dataset },
                new MappingDraftEntry { Target = "osdu.data.Curves", Input = MappingDraftInput.Repeat },
                new MappingDraftEntry { Target = "osdu.data.Unit", Input = MappingDraftInput.Cache, CacheType = "UnitOfMeasure", CacheField = "id" },
                new MappingDraftEntry { Target = "osdu.data.Symbol", Input = MappingDraftInput.Static, Static = "{not json" },
                new MappingDraftEntry { Target = "osdu.data.Description", Input = MappingDraftInput.Dataset, Column = "a", Modifiers = [new MappingDraftModifier("split", ",")] },
                new MappingDraftEntry { Target = "osdu.data.Count", Input = MappingDraftInput.Dataset, Column = "a", AppliesWhen = new MappingDraftCondition("a", "is", null) },
                new MappingDraftEntry { Target = "data.Nope", Input = MappingDraftInput.Dataset, Column = "a" },
                new MappingDraftEntry { Target = "osdu.data.When", Input = MappingDraftInput.Dataset, Column = "a", AppliesWhen = new MappingDraftCondition("a", "is", "one\ntwo") },
                new MappingDraftEntry { Target = "osdu.data.Name", Input = MappingDraftInput.Dataset, Column = "name" },
                new MappingDraftEntry { Target = "osdu.data.Day", Input = MappingDraftInput.Dataset, Column = "a", Modifiers = [new MappingDraftModifier("date", Text: "dd.MM.yy")] },
                new MappingDraftEntry { Target = "osdu.data.Weight", Input = MappingDraftInput.Dataset, Column = "a", Modifiers = [new MappingDraftModifier("number", DecimalSeparator: ",", GroupSeparator: ",")] },
            ],
        };

        var issues = MappingBuilder.Incomplete(draft);
        Assert.Contains(issues, i => i.Target == "osdu.data.Weight" && i.Message.Contains("number cannot use ',' both between digit groups and before the decimals", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Day" && i.Message.Contains("the date format 'dd.MM.yy' reads a two-digit year", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Name" && i.Message.Contains("choose the dataset column", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Name" && i.Message.Contains("more than one entry", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Curves" && i.Message.Contains("child dataset", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Unit" && i.Message.Contains("at least one findBy line", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Symbol" && i.Message.Contains("not valid JSON", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Description" && i.Message.Contains("split needs", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Count" && i.Message.Contains("text the column is compared with", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "data.Nope" && i.Message.Contains("must start with 'osdu.'", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.When" && i.Message.Contains("text on one line", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1.0")]
    [InlineData("")]
    [InlineData("a: b")]
    [InlineData("{dataset.x} run")]
    [InlineData("V/V")]
    [InlineData("say \"hi\" #1")]
    [InlineData(" padded ")]
    [InlineData("osdu.data.Curves[].X")]
    [InlineData("line one\nline two")]
    public void Awkward_text_survives_being_written_and_read_back(string text)
    {
        var draft = BaseDraft() with
        {
            Description = text.Length == 0 ? null : text,
            Entries =
            [
                .. BaseDraft().Entries,
                new MappingDraftEntry { Target = "osdu.data.Symbol", Input = MappingDraftInput.Static, Static = JsonSerializer.Serialize(text) },
                new MappingDraftEntry { Target = "osdu.data.Aliases", Input = MappingDraftInput.Static, Static = JsonSerializer.Serialize(new[] { text, "x" }) },
                new MappingDraftEntry
                {
                    Target = "osdu.data.Description",
                    Input = MappingDraftInput.Dataset,
                    Column = "name",
                    Modifiers = [new MappingDraftModifier("replace", Replacements: [new MappingDraftReplacement(text.Length == 0 ? "empty" : text, text)])],
                    AppliesWhen = new MappingDraftCondition("name", "isNot", ConditionText(text)),
                },
            ],
        };

        var mapping = new DeliveryDocumentLoader().ParseMapping(MappingBuilder.ToYaml(draft), "awkward.yaml");
        Assert.Equal(text, mapping.Entries.Single(e => e.Target.Text == "osdu.data.Symbol").Static!.GetValue<string>());
        Assert.Equal([text, "x"], mapping.Entries.Single(e => e.Target.Text == "osdu.data.Aliases").Static!.AsArray().Select(v => v!.GetValue<string>()));
        var description = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Description");
        Assert.Equal(text, description.Modifiers.Single().Replacements.Values.Single());
        Assert.Equal(ConditionText(text), description.AppliesWhen!.Text);
        if (text.Length > 0)
        {
            Assert.Equal(text.Trim(), mapping.Description?.Trim());
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("keep", null)]
    [InlineData("empty", null)]
    [InlineData("text", "Unevaluated")]
    [InlineData("text", "~")]
    [InlineData("text", "a: b")]
    public void A_replace_writes_no_value_and_otherwise_and_reads_them_back(string? otherwiseKind, string? otherwiseText)
    {
        var draft = BaseDraft() with
        {
            Entries =
            [
                .. BaseDraft().Entries,
                new MappingDraftEntry
                {
                    Target = "osdu.data.Symbol",
                    Input = MappingDraftInput.Dataset,
                    Column = "name",
                    Required = false,
                    Modifiers =
                    [
                        new MappingDraftModifier(
                            "replace",
                            Replacements: [new MappingDraftReplacement("M", "m"), new MappingDraftReplacement("NONE", null), new MappingDraftReplacement("~", "tilde")],
                            OtherwiseKind: otherwiseKind,
                            OtherwiseText: otherwiseText),
                        new MappingDraftModifier("upper"),
                    ],
                },
            ],
        };

        var yaml = MappingBuilder.ToYaml(draft);
        var mapping = new DeliveryDocumentLoader().ParseMapping(yaml, "replace.yaml");
        var modifiers = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Symbol").Modifiers;
        Assert.Equal([ModifierKind.Replace, ModifierKind.Upper], modifiers.Select(m => m.Kind));
        var replace = modifiers[0];
        Assert.Equal("m", replace.Replacements["M"]);
        Assert.Null(replace.Replacements["NONE"]);
        Assert.Equal("tilde", replace.Replacements["~"]);
        var expected = otherwiseKind switch
        {
            "empty" => ReplaceFallback.Empty,
            "text" => ReplaceFallback.Of(otherwiseText),
            _ => ReplaceFallback.Keep,
        };
        Assert.Equal(expected, replace.Otherwise);

        // The draft read back from the document says the same, so the builder reopens what it wrote.
        var reopened = MappingBuilder.FromDefinition(mapping).Entries.Single(e => e.Target == "osdu.data.Symbol").Modifiers[0];
        Assert.Equal(otherwiseKind is null or "keep" ? "keep" : otherwiseKind, reopened.OtherwiseKind);
        Assert.Equal(otherwiseKind == "text" ? otherwiseText : null, reopened.OtherwiseText);
        Assert.Null(reopened.Replacements!.Single(r => r.From == "NONE").To);
    }

    [Fact]
    public void A_replace_that_lists_a_value_twice_or_an_unknown_otherwise_is_an_issue()
    {
        string Issue(MappingDraftModifier modifier)
        {
            var draft = BaseDraft() with
            {
                Entries = [.. BaseDraft().Entries, new MappingDraftEntry { Target = "osdu.data.Symbol", Input = MappingDraftInput.Dataset, Column = "name", Modifiers = [modifier] }],
            };
            return Assert.Single(MappingBuilder.Incomplete(draft), issue => issue.Target == "osdu.data.Symbol").Message;
        }

        Assert.Contains("lists 'M' more than once", Issue(new MappingDraftModifier("replace", Replacements: [new("M", "m"), new(" M ", "metre")])), StringComparison.Ordinal);
        Assert.Contains("not 'sometimes'", Issue(new MappingDraftModifier("replace", Replacements: [new("M", "m")], OtherwiseKind: "sometimes")), StringComparison.Ordinal);
        Assert.Contains("needs the text an unlisted value becomes", Issue(new MappingDraftModifier("replace", Replacements: [new("M", "m")], OtherwiseKind: "text", OtherwiseText: " ")), StringComparison.Ordinal);
    }

    [Fact]
    public void Static_objects_and_lists_of_objects_are_written_as_yaml_and_read_back_as_the_same_json()
    {
        const string Meta = """[{"kind":"Unit","name":"ft","persistableReference":"{\"abcd\":{\"a\":0.0}}","propertyNames":["SamplingStart","SamplingStop"]}]""";
        var draft = BaseDraft() with
        {
            Entries =
            [
                .. BaseDraft().Entries,
                new MappingDraftEntry { Target = "osdu.data.Nested", Input = MappingDraftInput.Static, Static = """{"Inner":"x: y","Deeper":{"Count":3,"Flag":false}}""" },
                new MappingDraftEntry { Target = "osdu.tags.Meta", Input = MappingDraftInput.Static, Static = Meta },
            ],
        };

        var mapping = new DeliveryDocumentLoader().ParseMapping(MappingBuilder.ToYaml(draft), "statics.yaml");
        Assert.Equal("""{"Inner":"x: y","Deeper":{"Count":3,"Flag":false}}""", mapping.Entries.Single(e => e.Target.Text == "osdu.data.Nested").Static!.ToJsonString());
        // Compared as JSON, not as text: writing a string back out may escape its quotes differently.
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(Meta), mapping.Entries.Single(e => e.Target.Text == "osdu.tags.Meta").Static));
    }

    /// <summary>A condition compares with one line of text, so the awkward texts are put on one line for it.</summary>
    private static string ConditionText(string text) => text.Length == 0 ? "empty" : text.Replace('\n', ' ');

    private static MappingDraft BaseDraft() => new()
    {
        Name = "Thing",
        Version = "1.0.0",
        TemplateKind = TestSchema.Kind,
        TemplateVersion = TestSchema.Build().Version,
        System = "test",
        Key = ["name"],
        Parameters = [new MappingDraftParameter("dataPartition", true, null, null)],
        Entries =
        [
            new MappingDraftEntry { Target = "osdu.acl.owners", Input = MappingDraftInput.Static, Static = """["owners@x"]""" },
            new MappingDraftEntry { Target = "osdu.acl.viewers", Input = MappingDraftInput.Static, Static = """["viewers@x"]""" },
            new MappingDraftEntry { Target = "osdu.legal.legaltags", Input = MappingDraftInput.Static, Static = """["tag"]""" },
            new MappingDraftEntry { Target = "osdu.legal.otherRelevantDataCountries", Input = MappingDraftInput.Static, Static = """["NO"]""" },
            new MappingDraftEntry { Target = "osdu.data.Name", Input = MappingDraftInput.Dataset, Column = "name" },
        ],
    };
}
