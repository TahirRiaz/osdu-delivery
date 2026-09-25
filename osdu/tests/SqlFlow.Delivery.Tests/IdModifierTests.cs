using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The id modifier: an OSDU id built from a template of the entry's value, the row's columns, a cached lookup table's
/// fields and the mapping's parameters, encoded and checked against what the template gives the variable, and never
/// written half-built.
/// </summary>
public sealed class IdModifierTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string UnitId = "\"{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:\"";

    private static ReferenceType CurveClasses() => LookupCacheTests.Lookup(
        "CurveClasses",
        "mnemonic",
        ("GR", Values(("curve_family", "Gamma Ray"), ("unit", "gAPI"))),
        ("RHOB", Values(("curve_family", "Bulk Density"), ("unit", "g/cm3"))),
        ("XX", Values(("unit", "m"))));

    private static IReadOnlyDictionary<string, string?> Values(params (string Name, string? Value)[] values)
        => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private static ReferenceSnapshot Cache(params ReferenceType[] types) => new("refs-1", T0, TestSchema.References().Types.Concat(types));

    private static MappingRenderer Renderer(string entries, ReferenceSnapshot? cache = null)
        => new(TestSchema.Mapping(entries), TestSchema.Build(), cache ?? Cache(CurveClasses()), TestSchema.Context());

    private static SourceRecord Record(params (string Column, string? Value)[] columns)
    {
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "well-1", ["depth"] = "1" };
        foreach (var (column, value) in columns)
        {
            row[column] = value;
        }

        return new SourceRecord { Row = SourceRow.FromStrings(row), Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase) };
    }

    private static string? Data(RenderResult result, string property) => result.Document["data"]![property]?.GetValue<string>();

    private static string Entry(string property, string modifiers, string source = "unit", string extra = "") => $"""
          {property}:
            $from: {source}
            $modifiers:
        {modifiers}
        {extra}
        """;

    [Fact]
    public void An_id_template_is_read_into_its_tokens_and_the_columns_cache_types_and_parameters_it_reads()
    {
        var mapping = TestSchema.Mapping(
            Entry("Unit", $"      - trim\n      - id: {UnitId}")
            + Entry("WellboreID", "      - id: \"{$param.dataPartition}:master-data--Wellbore:{source}-{$value}:{version}\"", "uwi")
            + Entry("Symbol", "      - id: \"{$param.dataPartition}:reference-data--LogCurveFamily:{$cache.CurveClasses.curve_family}:\"", "mnemonic"));

        var unit = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Unit").Modifiers[^1];
        Assert.Equal(ModifierKind.Id, unit.Kind);
        Assert.Equal("id({$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:)", unit.ToString());
        Assert.Equal("reference-data--UnitOfMeasure", unit.Id!.EntityType);
        Assert.Equal(
            [IdTokenKind.Parameter, IdTokenKind.Text, IdTokenKind.Value, IdTokenKind.Text],
            unit.Id.Tokens.Select(t => t.Kind));
        Assert.True(unit.Id.ReadsValue);
        Assert.Equal(["dataPartition"], unit.Id.Parameters);

        var wellbore = mapping.Entries.Single(e => e.Target.Text == "osdu.data.WellboreID");
        Assert.Equal(["dataset.uwi", "dataset.source", "dataset.version"], wellbore.Columns.Select(c => c.ToString()));

        // A cache token is a type the mapping reads, for the render, the intake and lineage alike.
        Assert.Equal(["CurveClasses"], mapping.CacheTypesRead());
        Assert.Equal(["CurveClasses"], DeliveryLineage.CacheTypes(mapping));
    }

    [Theory]
    [InlineData("      - id", "'id' needs the template the id is built from")]
    [InlineData("      - id: {$value}", "id takes the template the id is built from as text")]
    [InlineData("      - id: \"\"", "this one is empty")]
    [InlineData("      - id: \"{$param.dataPartition}:reference-data--X:{$value\"", "opens a token that no '}' closes")]
    [InlineData("      - id: \"dev}:reference-data--X:{$value}:\"", "the '}' at position 4 closes a token that no '{' opens")]
    [InlineData("      - id: \"dev:reference-data--X:{$nope}:\"", "{$nope} is not a token; an id template reads {$value}, {<column>}")]
    [InlineData("      - id: \"dev:reference-data--X:{dataset.code}:\"", "the mapping's own tokens start with '$', so write {$dataset.code}")]
    [InlineData("      - id: \"dev:reference-data--X:{dataset.a.b.c}:\"", "the mapping's own tokens start with '$', so write {$dataset.a.b.c}")]
    [InlineData("      - id: \"dev:reference-data--X:{$cache.T}:\"", "reads a cached lookup table as {$cache.<Type>.<field>}")]
    [InlineData("      - id: \"dev:reference-data--X:{$param.a-b}:\"", "reads a parameter as {$param.<name>}")]
    [InlineData("      - id: \"dev:reference-data--X:{$value} x:\"", "'U+0020' at position 31 is not a character an OSDU id carries")]
    [InlineData("      - id: \"dev:reference-data--X:{$value}/x:\"", "'/' at position 31 is not a character an OSDU id carries")]
    [InlineData("      - id: \"dev:reference-data--X:{$value}%zz:\"", "the '%' at position 31 starts no percent-escape")]
    [InlineData("      - id: \"{$param.dataPartition}:reference-data--X:fixed:\"", "reads nothing that changes from record to record")]
    [InlineData("      - id: \"{$param.dataPartition}-{$value}\"", "the template writes 0 colon(s) of its own")]
    [InlineData("      - id: \"{$param.dataPartition}:Wellbore:{$value}:\"", "and the template writes 'Wellbore' there")]
    [InlineData("      - id: \"{$param.other}:reference-data--X:{$value}:\"", "builds an id from {$param.other}, but the mapping declares no parameter 'other'")]
    [InlineData("      - id: \"{$param.dataPartition}:reference-data--X:{$dataset.curves.unit}:\"", "reads a column of the dataset's own row as {$dataset.<column>}, and a column of the row the node reads as {<column>}")]
    [InlineData("      - id: " + UnitId + "\n      - id: " + UnitId, "a node builds one id, and its $modifiers list id and id; keep one")]
    [InlineData("      - id: " + UnitId + "\n      - trim", "id builds what the node writes, so it is the last modifier; move trim before it")]
    public void A_template_that_could_never_give_an_id_is_refused_when_the_mapping_is_read(string modifiers, string expected)
    {
        var ex = Assert.Throws<FlowValidationException>(() => TestSchema.Mapping(Entry("Unit", modifiers)));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_id_is_refused_on_a_source_that_gives_the_id_itself()
    {
        var ex = Assert.Throws<FlowValidationException>(() => TestSchema.Mapping($"""
              Unit:
                $cache: UnitOfMeasure.id
                $findBy: Code = unit
                $modifiers:
                  - id: {UnitId}
            """));
        Assert.Contains("a $cache node already gives what it writes", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" m/s ", "m%2Fs")]
    [InlineData("deg C", "deg%20C")]
    [InlineData("m%2Fs", "m%2Fs")]
    [InlineData("m%2fs", "m%2Fs")]
    [InlineData("100%", "100%25")]
    [InlineData("a~b", "a%7Eb")]
    [InlineData("\u03A9\u00B7m", "%CE%A9%C2%B7m")]
    [InlineData("\U0001F600", "%F0%9F%98%80")]
    [InlineData("Projected:EPSG::23031", "Projected:EPSG::23031")]
    [InlineData("gAPI_1-2.3", "gAPI_1-2.3")]
    public void A_value_is_trimmed_and_percent_encoded_once_keeping_what_an_id_carries(string value, string code)
    {
        var result = Renderer(Entry("Unit", $"      - id: {UnitId}")).Render(Record(("unit", value)));
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal($"dev:reference-data--UnitOfMeasure:{code}:", Data(result, "Unit"));
    }

    [Fact]
    public void Text_that_is_not_valid_unicode_holds_the_record()
    {
        var result = Renderer(Entry("Unit", $"      - id: {UnitId}", extra: "    $required: false")).Render(Record(("unit", "a\uD800b")));
        Assert.True(result.IsHeld);
        Assert.Contains("{$value} holds text that is not valid Unicode, which no id can carry", Assert.Single(result.Holds), StringComparison.Ordinal);
    }

    [Fact]
    public void Row_columns_parameters_and_a_version_build_a_master_data_reference_the_schema_pattern_takes()
    {
        var renderer = Renderer(Entry(
            "WellboreID",
            "      - id: \"{$param.dataPartition}:master-data--Wellbore:{source}-{$value}:{version}\"",
            "uwi"));

        var pinned = renderer.Render(Record(("uwi", "NO 15/5-7"), ("source", "recall"), ("version", "3")));
        Assert.False(pinned.IsHeld, string.Join("; ", pinned.Holds));
        Assert.Equal("dev:master-data--Wellbore:recall-NO%2015%2F5-7:3", Data(pinned, "WellboreID"));

        // A token with no value leaves no id: a half-built id is never written, and a required entry names the token.
        var unversioned = renderer.Render(Record(("uwi", "NO 15/5-7"), ("source", "recall"), ("version", " ")));
        Assert.True(unversioned.IsHeld);
        Assert.Contains(
            "osdu.data.WellboreID: the id {$param.dataPartition}:master-data--Wellbore:{source}-{$value}:{version} cannot be built, since {version} has no value, and the entry is required",
            Assert.Single(unversioned.Holds),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_value_or_token_is_left_out_by_an_optional_entry_and_holds_a_required_one()
    {
        const string Modifier = "      - id: \"{$param.dataPartition}:reference-data--UnitOfMeasure:{code}:\"";
        var required = Renderer(Entry("Unit", Modifier));
        var noCode = required.Render(Record(("unit", "m"), ("code", null)));
        Assert.Contains("since {code} has no value, and the entry is required", Assert.Single(noCode.Holds), StringComparison.Ordinal);
        var noUnit = required.Render(Record(("unit", ""), ("code", "m")));
        Assert.Contains("osdu.data.Unit: dataset.unit is empty, and the entry is required", Assert.Single(noUnit.Holds), StringComparison.Ordinal);

        var optional = Renderer(Entry("Unit", Modifier, extra: "    $required: false")).Render(Record(("unit", "m"), ("code", "")));
        Assert.False(optional.IsHeld, string.Join("; ", optional.Holds));
        Assert.Null(Data(optional, "Unit"));
    }

    [Fact]
    public void A_value_that_already_is_an_osdu_id_is_written_as_it_is_when_it_is_of_the_entity_type_built()
    {
        var renderer = Renderer(Entry("Unit", $"      - id: {UnitId}"));
        Assert.Equal("dev:reference-data--UnitOfMeasure:ft:", Data(renderer.Render(Record(("unit", "dev:reference-data--UnitOfMeasure:ft"))), "Unit"));
        Assert.Equal("osdu:reference-data--UnitOfMeasure:m:", Data(renderer.Render(Record(("unit", "osdu:reference-data--UnitOfMeasure:m:"))), "Unit"));

        var other = renderer.Render(Record(("unit", "dev:master-data--Wellbore:abc:")));
        Assert.Contains(
            "'dev:master-data--Wellbore:abc:' is already an OSDU id, of a master-data--Wellbore record, and the id modifier builds reference-data--UnitOfMeasure ids",
            Assert.Single(other.Holds),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_id_the_variable_does_not_take_holds_the_record_whatever_required_says()
    {
        // No colon before the version: the schema's pattern for a reference refuses it.
        var unpinned = Renderer(Entry("WellboreID", "      - id: \"{$param.dataPartition}:master-data--Wellbore:{$value}\"", "uwi", "    $required: false"))
            .Render(Record(("uwi", "abc")));
        Assert.Contains("'dev:master-data--Wellbore:abc' does not match the pattern the template gives the variable", Assert.Single(unpinned.Holds), StringComparison.Ordinal);

        // A token writes the entity type, so only the id built says what it names.
        var named = Renderer(Entry("Unit", "      - id: \"{$param.dataPartition}:{group}--UnitOfMeasure:{$value}:\"", extra: "    $required: false"));
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", Data(named.Render(Record(("unit", "m"), ("group", "reference-data"))), "Unit"));
        var wrong = named.Render(Record(("unit", "m"), ("group", "master-data")));
        Assert.Contains(
            "'dev:master-data--UnitOfMeasure:m:' is the id of a master-data--UnitOfMeasure record, and the template points the variable to reference-data--UnitOfMeasure",
            Assert.Single(wrong.Holds),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_cache_token_reads_the_row_keyed_by_the_value_and_records_every_row_and_unlisted_key()
    {
        const string Modifier = "      - id: \"{$param.dataPartition}:reference-data--LogCurveFamily:{$cache.CurveClasses.curve_family}:\"";
        var required = Renderer(Entry("Symbol", Modifier));

        var gr = required.Render(Record(("unit", "GR")));
        Assert.False(gr.IsHeld, string.Join("; ", gr.Holds));
        Assert.Equal("dev:reference-data--LogCurveFamily:Gamma%20Ray:", Data(gr, "Symbol"));
        Assert.Contains(new CacheUsage("CurveClasses", "GR", "mnemonic", "GR", CacheUsageKind.Match), gr.CacheUsages);
        Assert.Contains(new CacheUsage("CurveClasses", "GR", "curve_family", "Gamma Ray", CacheUsageKind.Value), gr.CacheUsages);

        // The key matches ignoring case when that finds one row.
        var rhob = required.Render(Record(("unit", "rhob")));
        Assert.Equal("dev:reference-data--LogCurveFamily:Bulk%20Density:", Data(rhob, "Symbol"));
        Assert.Contains(new CacheUsage("CurveClasses", "RHOB", "mnemonic", "rhob", CacheUsageKind.Match), rhob.CacheUsages);

        // A row with nothing in the field gives no value, and that it gave nothing is recorded.
        var xx = required.Render(Record(("unit", "XX")));
        Assert.Contains("since {$cache.CurveClasses.curve_family} has no value, and the entry is required", Assert.Single(xx.Holds), StringComparison.Ordinal);
        Assert.Contains(new CacheUsage("CurveClasses", "XX", "curve_family", string.Empty, CacheUsageKind.Empty), xx.CacheUsages);

        // A key the table does not list gives no value, and the key is recorded, so a table that comes to list it reaches the record.
        var zz = Renderer(Entry("Symbol", Modifier, extra: "    $required: false")).Render(Record(("unit", "zz")));
        Assert.False(zz.IsHeld, string.Join("; ", zz.Holds));
        Assert.Null(Data(zz, "Symbol"));
        Assert.Contains(new CacheUsage("CurveClasses", "ZZ", "curve_family", "zz", CacheUsageKind.Unlisted), zz.CacheUsages);
    }

    [Fact]
    public void A_cache_token_holds_what_the_cache_cannot_answer_for_certain()
    {
        string Held(string modifier, ReferenceSnapshot cache, string unit)
        {
            var result = Renderer(Entry("Symbol", modifier, extra: "    $required: false"), cache).Render(Record(("unit", unit)));
            Assert.True(result.IsHeld, $"'{unit}' was not held");
            return Assert.Single(result.Holds);
        }

        Assert.Contains(
            "the id reads {$cache.CurveClasses.curve_family}, and version refs-1 of the cache of partition 'dev' holds no type 'CurveClasses'",
            Held("      - id: \"dev:reference-data--X:{$cache.CurveClasses.curve_family}:\"", Cache(), "GR"),
            StringComparison.Ordinal);
        Assert.Contains(
            "the id reads {$cache.UnitOfMeasure.Code}, and UnitOfMeasure holds OSDU records (reference-data--UnitOfMeasure)",
            Held("      - id: \"dev:reference-data--X:{$cache.UnitOfMeasure.Code}:\"", Cache(), "m"),
            StringComparison.Ordinal);

        var clash = Held(
            "      - id: \"dev:reference-data--UnitOfMeasure:{$cache.RecallUnits.value}:\"",
            Cache(LookupCacheTests.Pairs("RecallUnits", ("Ft", "ft"), ("FT", "foot"))),
            "ft");
        Assert.Contains("'ft' matches the RecallUnits rows ", clash, StringComparison.Ordinal);
        Assert.Contains("and they hold different values at 'value'", clash, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gate_checks_what_an_id_reads_and_the_ids_it_gives_before_any_row_arrives()
    {
        var schema = TestSchema.Build();
        IReadOnlyList<ValidationIssue> Check(string entries, ReferenceSnapshot? cache = null, IReadOnlyDictionary<string, IReadOnlySet<string>>? columns = null)
            => Preflight.Check(TestSchema.Mapping(entries), schema, cache ?? Cache(CurveClasses()), TestSchema.Context(), sourceColumns: columns);

        Assert.DoesNotContain(Check(Entry("Unit", $"      - id: {UnitId}")), i => i.Target == "osdu.data.Unit");
        Assert.DoesNotContain(Check(Entry("WellboreID", "      - id: \"{$param.dataPartition}:master-data--Wellbore:{$value}:\"", "uwi")), i => i.Target == "osdu.data.WellboreID");

        string Error(string target, string modifier, ReferenceSnapshot? cache = null)
            => Assert.Single(Check(Entry(target, modifier), cache), i => i.Severity == IssueSeverity.Error).Message;
        string Warning(string target, string modifier, ReferenceSnapshot? cache = null)
            => Assert.Single(Check(Entry(target, modifier), cache), i => i.Severity == IssueSeverity.Warning).Message;

        Assert.Contains(
            "the id {$param.dataPartition}:master-data--Wellbore:{$value}: names a master-data--Wellbore record, but the template points osdu.data.Unit to reference-data--UnitOfMeasure.",
            Error("Unit", "      - id: \"{$param.dataPartition}:master-data--Wellbore:{$value}:\""),
            StringComparison.Ordinal);
        Assert.Contains(
            "gives ids such as 'dev:master-data--Wellbore:x', which osdu.data.WellboreID does not take: 'dev:master-data--Wellbore:x' does not match the pattern",
            Error("WellboreID", "      - id: \"{$param.dataPartition}:master-data--Wellbore:{$value}\""),
            StringComparison.Ordinal);
        Assert.Contains("the id modifier gives an OSDU id, written as text, but the template takes a number at osdu.data.Weight.", Error("Weight", $"      - id: {UnitId}"), StringComparison.Ordinal);
        Assert.Contains("but the template takes a date-time string at osdu.data.When.", Error("When", $"      - id: {UnitId}"), StringComparison.Ordinal);
        Assert.Contains("record.data.Symbol writes an OSDU id, but the template does not mark osdu.data.Symbol as a relationship.", Warning("Symbol", $"      - id: {UnitId}"), StringComparison.Ordinal);
        Assert.Contains(
            "a token writes the entity type of the id {$param.dataPartition}:{group}--Wellbore:{$value}:, so whether each id is one osdu.data.WellboreID takes is checked record by record",
            Warning("WellboreID", "      - id: \"{$param.dataPartition}:{group}--Wellbore:{$value}:\""),
            StringComparison.Ordinal);

        const string Family = "      - id: \"{$param.dataPartition}:reference-data--LogCurveFamily:{$cache.CurveClasses.";
        Assert.Contains("the id reads {$cache.CurveClasses.curve_family}, and cache version 'refs-1' holds no CurveClasses. Cached: UnitOfMeasure, Wellbore.", Error("Symbol", Family + "curve_family}:\"", Cache()), StringComparison.Ordinal);
        Assert.Contains("and no CurveClasses row holds 'colour' in cache version 'refs-1'. Cached: curve_family, mnemonic, unit.", Error("Symbol", Family + "colour}:\""), StringComparison.Ordinal);
        Assert.Contains("the key CurveClasses is looked up by, which is the value itself; write {$value}.", Check(Entry("Symbol", Family + "mnemonic}:\"")).Single(i => i.Message.Contains("itself", StringComparison.Ordinal)).Message, StringComparison.Ordinal);
        Assert.Contains(
            "UnitOfMeasure holds OSDU records (reference-data--UnitOfMeasure), which have no key to look the value up by",
            Error("Symbol", "      - id: \"{$param.dataPartition}:reference-data--X:{$cache.UnitOfMeasure.Code}:\""),
            StringComparison.Ordinal);
        var empty = Check(Entry("Symbol", "      - id: \"{$param.dataPartition}:reference-data--X:{$cache.Empty.value}:\""), Cache(new ReferenceType("Empty", ReferenceType.LookupEntityType("Empty"), [], "key")));
        Assert.Contains(empty, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("and Empty holds no rows in cache version 'refs-1', so no record gets an id from it.", StringComparison.Ordinal));

        // A required parameter without a value stops the gate before any entry; an optional one without a default reaches the id.
        var optionalParameter = new DeliveryDocumentLoader().ParseMapping(
            TestSchema.MappingDocument(Entry("Unit", "      - id: \"{$param.dataPartition}:reference-data--UnitOfMeasure:{$param.system}-{$value}:\""))
                .Replace("  dataPartition: { required: true }", "  dataPartition: { required: true }\n  system: { required: false }", StringComparison.Ordinal),
            "thing.yaml");
        var noSystem = Preflight.Check(optionalParameter, schema, Cache(CurveClasses()), TestSchema.Context(), sourceColumns: null);
        Assert.Contains(noSystem, i => i.Severity == IssueSeverity.Error && i.Message.Contains("reads {$param.system}, which the flow supplies no value for.", StringComparison.Ordinal));

        // A column a token reads is a column the entry reads, checked against the flow's source like any other.
        var columns = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [SourceDatasets.Record] = new HashSet<string>(["name", "depth", "uwi"], StringComparer.OrdinalIgnoreCase),
        };
        var missingColumn = Check(Entry("WellboreID", "      - id: \"{$param.dataPartition}:master-data--Wellbore:{$value}:{version}\"", "uwi"), columns: columns);
        Assert.Contains(missingColumn, i => i.Severity == IssueSeverity.Error && i.Message.Contains("reads dataset.version, which the record table does not hold", StringComparison.Ordinal));
    }

    [Fact]
    public void An_id_modifier_is_written_back_by_the_builder_and_its_mistakes_are_named_by_target()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var loader = new DeliveryDocumentLoader();
        var original = loader.ParseMapping(TestSchema.MappingDocument(
            Entry("Unit", $"      - trim\n      - id: {UnitId}")
            + Entry("Symbol", "      - id: \"{$param.dataPartition}:reference-data--LogCurveFamily:{$cache.CurveClasses.curve_family}:\"")), "m.yaml");
        var draft = MappingBuilder.FromDefinition(original);
        Assert.Empty(MappingBuilder.Incomplete(draft));

        var yaml = MappingBuilder.ToYaml(draft);
        var again = MappingBuilder.FromDefinition(loader.ParseMapping(yaml, "m.yaml"));
        Assert.Equal(JsonSerializer.Serialize(draft, json), JsonSerializer.Serialize(again, json));
        Assert.Contains("- id: \"{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:\"", yaml, StringComparison.Ordinal);

        MappingDraft With(MappingDraftEntry entry) => draft with { Entries = [.. draft.Entries.Where(e => e.Target != entry.Target), entry] };
        var unit = draft.Entries.Single(e => e.Target == "osdu.data.Unit");
        var id = unit.Modifiers[^1];

        Assert.Contains(MappingBuilder.Incomplete(With(unit with { Modifiers = [id with { Text = "{$value}" }] })),
            i => i.Target == "osdu.data.Unit" && i.Message.Contains("id: an OSDU id is <partition>:<group>--<Entity>:<code>", StringComparison.Ordinal));
        Assert.Contains(MappingBuilder.Incomplete(With(unit with { Modifiers = [id, new MappingDraftModifier("trim")] })),
            i => i.Target == "osdu.data.Unit" && i.Message.Contains("so it is the last modifier", StringComparison.Ordinal));
        Assert.Contains(MappingBuilder.Incomplete(With(unit with { Modifiers = [id, id] })),
            i => i.Target == "osdu.data.Unit" && i.Message.Contains("an entry builds one id", StringComparison.Ordinal));
        Assert.Contains(
            MappingBuilder.Incomplete(With(unit with
            {
                Input = MappingDraftInput.Cache,
                CacheType = "UnitOfMeasure",
                CacheField = "id",
                FindBy = [new MappingDraftFind("Code", "unit", null)],
            })),
            i => i.Target == "osdu.data.Unit" && i.Message.Contains("choose a dataset column or an expression as the input", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("      - ref")]
    [InlineData("      - ref: UnitOfMeasure")]
    [InlineData("      - ref: reference-data--UnitOfMeasure")]
    public void Ref_references_the_record_whose_code_is_the_value_of_the_type_the_property_points_to(string modifier)
    {
        var renderer = Renderer(Entry("Unit", "      - trim\n" + modifier));
        var result = renderer.Render(Record(("unit", " m ")));
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", Data(result, "Unit"));

        // A value that is already an id of that type is written as it is; a code is percent-encoded as any id token is.
        Assert.Equal("dev:reference-data--UnitOfMeasure:ft:", Data(renderer.Render(Record(("unit", "dev:reference-data--UnitOfMeasure:ft"))), "Unit"));
        Assert.Equal("dev:reference-data--UnitOfMeasure:g%2Fcm3:", Data(renderer.Render(Record(("unit", "g/cm3"))), "Unit"));
        Assert.Null(Data(renderer.Render(Record(("unit", null))), "Unit"));
    }

    [Fact]
    public void Ref_is_refused_where_the_template_does_not_say_which_type_it_references()
    {
        var schema = TestSchema.Build();
        IReadOnlyList<ValidationIssue> Check(string entries)
            => Preflight.Check(TestSchema.Mapping(entries), schema, Cache(CurveClasses()), TestSchema.Context(), sourceColumns: null);
        string Error(string target, string modifier)
            => Assert.Single(Check(Entry(target, modifier)), i => i.Severity == IssueSeverity.Error).Message;

        Assert.DoesNotContain(Check(Entry("Unit", "      - ref")), i => i.Target == "osdu.data.Unit");
        Assert.DoesNotContain(Check(Entry("WellboreID", "      - ref: Wellbore", "uwi")), i => i.Target == "osdu.data.WellboreID" && i.Severity == IssueSeverity.Error);
        Assert.Contains(
            "osdu.data.Unit points to reference-data--UnitOfMeasure, and none of them is a Wellbore; name one of them, or write the type in full.",
            Error("Unit", "      - ref: Wellbore"),
            StringComparison.Ordinal);
        Assert.Contains(
            "ref builds a reference of the entity type osdu.data.Symbol points to, and the template names none for it; write the type in full, such as ref: reference-data--UnitOfMeasure.",
            Error("Symbol", "      - ref"),
            StringComparison.Ordinal);
        Assert.Contains(
            "names a master-data--Wellbore record, but the template points osdu.data.Unit to reference-data--UnitOfMeasure.",
            Error("Unit", "      - ref: master-data--Wellbore"),
            StringComparison.Ordinal);

        // The render holds with the same reason the gate gives, so a mapping that reaches a run anyway delivers nothing wrong.
        var held = Renderer(Entry("Unit", "      - ref: Wellbore")).Render(Record(("unit", "m")));
        Assert.Contains(held.Holds, h => h == "osdu.data.Unit: osdu.data.Unit points to reference-data--UnitOfMeasure, and none of them is a Wellbore; name one of them, or write the type in full");
    }

    [Theory]
    [InlineData("      - ref\n      - trim", "ref builds what the node writes, so it is the last modifier; move trim before it.")]
    [InlineData("      - id: \"{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:\"\n      - ref", "a node builds one id, and its $modifiers list id and ref; keep one.")]
    [InlineData("      - ref: 12bad", "ref '12bad' is not an entity type")]
    [InlineData("      - ref: [UnitOfMeasure]", "ref names the entity type of the record it references")]
    public void A_ref_the_loader_cannot_place_is_refused(string modifiers, string problem)
    {
        var refused = Assert.Throws<FlowValidationException>(() => TestSchema.Mapping(Entry("Unit", modifiers)));
        Assert.Contains(problem, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ref_on_a_cache_node_is_refused()
    {
        var refused = Assert.Throws<FlowValidationException>(() => TestSchema.Mapping("""
            Unit:
              $cache: UnitOfMeasure.id
              $findBy: Code = unit
              $modifiers: [ref]
            """));
        Assert.Contains("the ref modifier builds the id a node writes from a dataset value, and a $cache node already gives what it writes", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ref_is_written_back_by_the_builder_as_it_was_written()
    {
        var loader = new DeliveryDocumentLoader();
        var original = loader.ParseMapping(TestSchema.MappingDocument(Entry("Unit", "      - ref") + Entry("WellboreID", "      - ref: Wellbore", "uwi")), "m.yaml");
        var draft = MappingBuilder.FromDefinition(original);
        Assert.Empty(MappingBuilder.Incomplete(draft));
        Assert.Equal(new MappingDraftModifier("ref"), draft.Entries.Single(e => e.Target == "osdu.data.Unit").Modifiers.Single());
        Assert.Equal("Wellbore", draft.Entries.Single(e => e.Target == "osdu.data.WellboreID").Modifiers.Single().Text);

        var yaml = MappingBuilder.ToYaml(draft).ReplaceLineEndings("\n");
        Assert.Contains("        - ref\n", yaml, StringComparison.Ordinal);
        Assert.Contains("        - ref: Wellbore\n", yaml, StringComparison.Ordinal);
        Assert.Equal(yaml, MappingBuilder.ToYaml(MappingBuilder.FromDefinition(loader.ParseMapping(yaml, "again.yaml"))).ReplaceLineEndings("\n"));

        var unit = draft.Entries.Single(e => e.Target == "osdu.data.Unit");
        Assert.Contains(
            MappingBuilder.Incomplete(draft with { Entries = [.. draft.Entries.Where(e => e.Target != unit.Target), unit with { Modifiers = [new MappingDraftModifier("ref", Text: "not a type")] }] }),
            i => i.Target == "osdu.data.Unit" && i.Message.Contains("ref 'not a type' is not an entity type", StringComparison.Ordinal));
    }
}
