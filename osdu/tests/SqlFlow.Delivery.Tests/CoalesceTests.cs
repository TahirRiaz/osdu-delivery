using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The <c>$coalesce</c> node: a property filled from the first of several alternatives that gives a value, each a node of
/// its own (columns in a chosen order, the OSDU records a cache flow captured, a lookup table the database fills, a search,
/// a literal default), so the outgoing record is as complete as its sources allow. A miss passes to the next alternative,
/// a mistake holds the record, and an id written without a record the partition holds only goes out where the mapping
/// says <c>$unverified</c>, recorded as such.
/// </summary>
public sealed class CoalesceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The units a capture of the partition found (m, ft), and a lookup table translating a source's spellings.</summary>
    private static ReferenceSnapshot Cache() => new("refs-1", T0, TestSchema.References().Types.Append(
        LookupCacheTests.Pairs("RecallUnits", ("FEET", "ft"), ("PSIA", "psia"))));

    private static MappingRenderer Renderer(string entries, ReferenceSnapshot? cache = null)
        => new(TestSchema.Mapping(entries), TestSchema.Build(), cache ?? Cache(), TestSchema.Context());

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

    /// <summary>A unit read from the partition's records first, then translated through the database's table, then written unverified.</summary>
    private const string UnitFromOsduThenTable = """
          Unit:
            $coalesce:
              - $cache: UnitOfMeasure.id
                $findBy: Code = unit
              - $from: unit
                $modifiers:
                  - replace: $cache.RecallUnits
                  - ref
              - $from: unit
                $unverified: true
                $modifiers:
                  - replace: $cache.RecallUnits
                  - ref
        """;

    [Fact]
    public void A_coalesce_node_reads_its_alternatives_in_order_and_its_own_settings_decide_for_all_of_them()
    {
        var mapping = TestSchema.Mapping("""
              Symbol:
                $coalesce:
                  - $from: symbol
                    $modifiers: [trim]
                  - $expr: coalesce(code, alias)
                  - $cache: UnitOfMeasure.Code
                    $findBy: Name = unit_name
                  - $value: unknown
                $when: kind = "curve"
                $required: false
                $description: The symbol the source gives, else the unit's code.
            """);

        var entry = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Symbol");
        Assert.True(entry.IsCoalesce);
        Assert.Equal(MappingSourceKind.DatasetColumn, entry.Source!.Kind);
        Assert.Equal([ModifierKind.Trim], entry.Modifiers.Select(m => m.Kind));
        Assert.Equal(3, entry.Alternatives.Count);
        Assert.Equal(MappingSourceKind.Expression, entry.Alternatives[0].Source!.Kind);
        Assert.Equal(MappingSourceKind.Cache, entry.Alternatives[1].Source!.Kind);
        Assert.Equal("unknown", entry.Alternatives[2].Static!.GetValue<string>());
        Assert.Equal("record.data.Symbol.$coalesce[3]", entry.Alternatives[2].Location);
        Assert.False(entry.Required);
        Assert.NotNull(entry.AppliesWhen);
        Assert.Equal("The symbol the source gives, else the unit's code.", entry.Description);
        Assert.All(entry.Alternatives, alternative => Assert.Null(alternative.AppliesWhen));

        // Whatever asks what the entry reads asks every alternative: the columns, the cached types, the node count.
        Assert.Equal(
            ["dataset.alias", "dataset.code", "dataset.kind", "dataset.symbol", "dataset.unit_name"],
            entry.Columns.Select(c => c.ToString()).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(["UnitOfMeasure"], mapping.CacheTypesRead());
        Assert.Equal(4, entry.ValueNodes.Count());
    }

    [Fact]
    public void The_first_alternative_that_gives_a_value_wins_each_with_its_own_modifiers_and_a_literal_is_the_default()
    {
        var renderer = Renderer("""
              Symbol:
                $coalesce:
                  - $from: symbol
                    $modifiers: [trim]
                  - $from: alias
                    $modifiers: [upper]
                  - $value: unknown
            """);

        Assert.Equal("GR", Data(renderer.Render(Record(("symbol", "  GR "), ("alias", "gamma"))), "Symbol"));
        Assert.Equal("GAMMA", Data(renderer.Render(Record(("symbol", " "), ("alias", "gamma"))), "Symbol"));
        Assert.Equal("unknown", Data(renderer.Render(Record(("symbol", null), ("alias", ""))), "Symbol"));
    }

    [Fact]
    public void A_required_node_none_of_whose_alternatives_gives_a_value_holds_naming_why_each_gave_nothing()
    {
        const string Symbol = """
              Symbol:
                $coalesce:
                  - $from: symbol
                  - $cache: UnitOfMeasure.Code
                    $findBy: Name = unit_name
            """;

        var held = Renderer(Symbol).Render(Record(("symbol", ""), ("unit_name", "furlong")));
        Assert.True(held.IsHeld);
        var reason = Assert.Single(held.Holds);
        Assert.StartsWith("osdu.data.Symbol: none of the 2 alternatives of $coalesce gives a value, and the entry is required: 1. dataset.symbol is empty; 2. ", reason, StringComparison.Ordinal);
        Assert.Contains("no UnitOfMeasure matches 'furlong' by Name", reason, StringComparison.Ordinal);

        var optional = Renderer(Symbol + "\n    $required: false").Render(Record(("symbol", ""), ("unit_name", "furlong")));
        Assert.False(optional.IsHeld, string.Join("; ", optional.Holds));
        Assert.Null(Data(optional, "Symbol"));
    }

    [Fact]
    public void A_mistake_in_an_alternative_holds_the_record_instead_of_passing_to_the_next()
    {
        // A value that is there and cannot be read says nothing about whether the next alternative is right.
        var result = Renderer("""
              When:
                $coalesce:
                  - $from: spud
                    $modifiers: [date]
                  - $from: completed
                    $modifiers: [date]
            """).Render(Record(("spud", "last tuesday"), ("completed", "2026-09-01")));

        Assert.True(result.IsHeld);
        Assert.Contains("'last tuesday' is not an ISO 8601 date or date-time", Assert.Single(result.Holds), StringComparison.Ordinal);
    }

    [Fact]
    public void A_unit_is_read_from_the_partition_s_records_first_then_through_the_database_s_table_then_unverified()
    {
        var renderer = Renderer(UnitFromOsduThenTable);

        // The partition holds the unit under the spelling the source gives, ignoring case where only one record answers.
        var direct = renderer.Render(Record(("unit", "M")));
        Assert.False(direct.IsHeld, string.Join("; ", direct.Holds));
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", Data(direct, "Unit"));
        Assert.Contains(new CacheUsage("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", "Code", "M", CacheUsageKind.Match), direct.CacheUsages);

        // The partition holds no unit spelled FEET; the table translates it, and the reference built names a record it holds.
        var translated = renderer.Render(Record(("unit", "FEET")));
        Assert.Equal("dev:reference-data--UnitOfMeasure:ft:", Data(translated, "Unit"));
        Assert.Contains(new CacheUsage("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:ft", "id", "dev:reference-data--UnitOfMeasure:ft", CacheUsageKind.Match), translated.CacheUsages);
        Assert.DoesNotContain(translated.CacheUsages, u => u.Kind == CacheUsageKind.Unverified);

        // Neither holds psia: the last alternative writes the translated reference anyway, recorded as unverified in the type
        // that would hold it, so a capture that brings the unit in reaches the record.
        var unverified = renderer.Render(Record(("unit", "PSIA")));
        Assert.False(unverified.IsHeld, string.Join("; ", unverified.Holds));
        Assert.Equal("dev:reference-data--UnitOfMeasure:psia:", Data(unverified, "Unit"));
        Assert.Contains(new CacheUsage("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:psia", "id", "dev:reference-data--UnitOfMeasure:psia:", CacheUsageKind.Unverified), unverified.CacheUsages);
    }

    [Fact]
    public void Without_an_unverified_alternative_a_reference_the_partition_does_not_hold_is_never_written()
    {
        var result = Renderer("""
              Unit:
                $coalesce:
                  - $cache: UnitOfMeasure.id
                    $findBy: Code = unit
                  - $from: unit
                    $modifiers:
                      - replace: $cache.RecallUnits
                      - ref
            """).Render(Record(("unit", "PSIA")));

        Assert.True(result.IsHeld);
        var reason = Assert.Single(result.Holds);
        Assert.Contains("none of the 2 alternatives of $coalesce gives a value", reason, StringComparison.Ordinal);
        Assert.Contains("holds no such reference-data--UnitOfMeasure record in UnitOfMeasure, so the reference would point at nothing", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unverified_node_of_its_own_writes_the_id_it_builds_and_records_whether_the_partition_holds_it()
    {
        var renderer = Renderer("""
              Unit:
                $from: unit
                $unverified: true
                $modifiers: [ref]
            """);

        var held = renderer.Render(Record(("unit", "m")));
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", Data(held, "Unit"));
        Assert.Contains(held.CacheUsages, u => u.Kind == CacheUsageKind.Match && u.ItemId == "dev:reference-data--UnitOfMeasure:m");

        var missing = renderer.Render(Record(("unit", "psia")));
        Assert.False(missing.IsHeld, string.Join("; ", missing.Holds));
        Assert.Equal("dev:reference-data--UnitOfMeasure:psia:", Data(missing, "Unit"));
        Assert.Contains(missing.CacheUsages, u => u.Kind == CacheUsageKind.Unverified && u.ItemId == "dev:reference-data--UnitOfMeasure:psia");
    }

    [Fact]
    public async Task An_alternative_waiting_for_a_search_stops_the_node_until_the_answer_is_in()
    {
        var mapping = SearchSourceTests.Mapping("""
              WellboreID:
                $coalesce:
                  - $search: Wellbore
                    $findBy: data.FacilityName = wb
                  - $from: wb
                    $modifiers:
                      - id: "{$param.dataPartition}:master-data--Wellbore:{$value}:"
            """);
        var search = new FixedRecordSearch([("data.FacilityName", "WB-1", "dev:master-data--Wellbore:1234")]);
        var renderer = new MappingRenderer(mapping, TestSchema.Build(), ReferenceSnapshot.Empty, TestSchema.Context(), SearchSourceTests.Resolve(mapping), search);

        // Before the platform has answered, the later alternative never stands in for the search.
        var first = renderer.Render(Record(("wb", "WB-1")));
        Assert.True(first.IsIncomplete);

        var found = await Samples.RenderSettledAsync(renderer, Record(("wb", "WB-1")));
        Assert.Equal("dev:master-data--Wellbore:1234:", Data(found, "WellboreID"));

        // A wellbore the platform does not hold passes to the id built from the value.
        var built = await Samples.RenderSettledAsync(renderer, Record(("wb", "WB-2")));
        Assert.False(built.IsHeld, string.Join("; ", built.Holds));
        Assert.Equal("dev:master-data--Wellbore:WB-2:", Data(built, "WellboreID"));
    }

    [Fact]
    public void The_gate_checks_every_alternative_and_names_the_one_it_finds_wrong()
    {
        var issues = Preflight.Check(
            TestSchema.Mapping("""
                  Unit:
                    $coalesce:
                      - $cache: UnitOfMeasure.id
                        $findBy: Code = unit
                      - $cache: Units.id
                        $findBy: Code = unit
                """),
            TestSchema.Build(), Cache(), TestSchema.Context(), sourceColumns: null);

        var error = Assert.Single(issues, i => i.Severity == IssueSeverity.Error);
        Assert.Contains("record.data.Unit.$coalesce[1]", error.Message, StringComparison.Ordinal);
        Assert.Contains("Units", error.Message, StringComparison.Ordinal);
        Assert.Equal("osdu.data.Unit", error.Target);
    }

    [Fact]
    public void A_record_s_shape_names_every_alternative_in_the_order_they_are_tried()
    {
        var shape = MappingRenderer.Shape(
            TestSchema.Mapping("""
                  Symbol:
                    $coalesce:
                      - $from: symbol
                      - $from: alias
                        $modifiers: [upper]
                      - $value: unknown
                """),
            TestSchema.Build(),
            new Dictionary<string, string> { ["dataPartition"] = "dev" });

        Assert.Equal("<string from dataset.symbol, else dataset.alias | upper, else \"unknown\">", shape.Document["data"]!["Symbol"]!.GetValue<string>());
    }

    [Fact]
    public void The_render_says_which_alternative_gave_each_value_and_which_references_it_wrote_unverified()
    {
        var symbol = Renderer("""
              Symbol:
                $coalesce:
                  - $from: symbol
                  - $from: alias
                    $modifiers: [upper]
            """).Render(Record(("symbol", ""), ("alias", "gamma")));
        Assert.Equal(new CoalesceChoice("osdu.data.Symbol", 2, 2, "dataset.alias | upper", 1, false), Assert.Single(symbol.Choices));

        var unit = Renderer(UnitFromOsduThenTable).Render(Record(("unit", "PSIA")));
        var chosen = Assert.Single(unit.Choices);
        Assert.Equal((3, 3, true), (chosen.Alternative, chosen.Of, chosen.Unverified));
        Assert.DoesNotContain("unverified", chosen.Origin, StringComparison.Ordinal);

        // A node that is not a $coalesce makes no choice.
        Assert.Empty(Renderer("  Symbol: { $from: symbol }").Render(Record(("symbol", "GR"))).Choices);
    }

    [Fact]
    public void A_coalesce_node_opens_in_the_builder_and_is_written_back_as_it_was_read()
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var loader = new Documents.DeliveryDocumentLoader();
        var original = loader.ParseMapping(TestSchema.MappingDocument("""
              Symbol:
                $coalesce:
                  - $from: symbol
                    $modifiers: [trim]
                  - $cache: UnitOfMeasure.Code
                    $findBy: Name = unit_name
                  - $value: unknown
                $required: false
                $description: The source's symbol, else the unit's code.
              Unit:
                $coalesce:
                  - $cache: UnitOfMeasure.id
                    $findBy: Code = unit
                  - $from: unit
                    $unverified: true
                    $modifiers: [ref]
            """), "m.yaml");

        var draft = Templates.MappingBuilder.FromDefinition(original);
        Assert.Empty(Templates.MappingBuilder.Incomplete(draft));
        var symbol = draft.Entries.Single(e => e.Target == "osdu.data.Symbol");
        Assert.Equal(Templates.MappingDraftInput.Coalesce, symbol.Input);
        Assert.Equal(
            [Templates.MappingDraftInput.Dataset, Templates.MappingDraftInput.Cache, Templates.MappingDraftInput.Static],
            symbol.Alternatives.Select(a => a.Input));
        Assert.False(symbol.Required);
        Assert.Equal("The source's symbol, else the unit's code.", symbol.Description);
        Assert.True(draft.Entries.Single(e => e.Target == "osdu.data.Unit").Alternatives[1].Unverified);

        var yaml = Templates.MappingBuilder.ToYaml(draft).ReplaceLineEndings("\n");
        Assert.Contains("$coalesce:\n", yaml, StringComparison.Ordinal);
        Assert.Contains("- $value: unknown\n", yaml, StringComparison.Ordinal);
        Assert.Contains("$unverified: true\n", yaml, StringComparison.Ordinal);

        var again = Templates.MappingBuilder.FromDefinition(loader.ParseMapping(yaml, "again.yaml"));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(draft, json), System.Text.Json.JsonSerializer.Serialize(again, json));
        Assert.Equal(yaml, Templates.MappingBuilder.ToYaml(again).ReplaceLineEndings("\n"));

        // What the builder refuses in a coalesce entry, named by the alternative.
        Templates.MappingDraft With(Templates.MappingDraftEntry entry) => draft with { Entries = [.. draft.Entries.Where(e => e.Target != entry.Target), entry] };
        var literalFirst = symbol with { Alternatives = [symbol.Alternatives[2], symbol.Alternatives[0]] };
        Assert.Contains(Templates.MappingBuilder.Incomplete(With(literalFirst)),
            i => i.Target == "osdu.data.Symbol" && i.Message.StartsWith("osdu.data.Symbol alternative 2 is never tried", StringComparison.Ordinal));
        Assert.Contains(Templates.MappingBuilder.Incomplete(With(symbol with { Alternatives = [symbol.Alternatives[0]] })),
            i => i.Message.Contains("takes the first of two or more alternatives", StringComparison.Ordinal));
        var unverifiedWithoutId = symbol with { Alternatives = [symbol.Alternatives[0] with { Unverified = true }, symbol.Alternatives[1]] };
        Assert.Contains(Templates.MappingBuilder.Incomplete(With(unverifiedWithoutId)),
            i => i.Message.StartsWith("osdu.data.Symbol alternative 1: $unverified lets an id built with id or ref go out", StringComparison.Ordinal));
    }

    [Fact]
    public void A_coalesce_with_a_literal_last_fills_its_property_on_every_row_whatever_its_required_flag()
    {
        var mapping = TestSchema.Mapping(
            data: """
                  Depth:
                    $coalesce:
                      - $from: depth
                      - $value: 0
                    $required: false
                """,
            baseData: "Name: { $from: name }");

        var depth = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Depth");
        Assert.True(MappingCoverage.FillsEveryRow(depth));
        Assert.Empty(MappingCoverage.RequiredIssues(mapping, TestSchema.Build(), "m.yaml"));

        var optional = TestSchema.Mapping(
            data: """
                  Depth:
                    $coalesce:
                      - $from: depth
                      - $from: md
                    $required: false
                """,
            baseData: "Name: { $from: name }");
        Assert.Contains(MappingCoverage.RequiredIssues(optional, TestSchema.Build(), "m.yaml"), i => i.Message.Contains("is $required: false, but the template requires osdu.data.Depth", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("$coalesce: [ { $from: a } ]", "$coalesce lists 1 alternative, and it takes the first of two or more that gives a value")]
    [InlineData("$coalesce: { $from: a }", "$coalesce lists the alternatives the value is taken from, in order")]
    [InlineData("$coalesce: [ a, b ]", "record.data.Symbol.$coalesce[0] is not a node")]
    [InlineData("$coalesce: [ { $from: a, $when: 'b = \"x\"' }, { $from: b } ]", "$when decides for the whole $coalesce node; write it beside $coalesce")]
    [InlineData("$coalesce: [ { $from: a, $required: false }, { $from: b } ]", "$required decides for the whole $coalesce node")]
    [InlineData("$coalesce: [ { $from: a }, { $coalesce: [ { $from: b }, { $from: c } ] } ]", "lists alternatives of its own; write every alternative in the one $coalesce list")]
    [InlineData("$coalesce: [ { $value: x }, { $from: b } ]", "record.data.Symbol.$coalesce[1] is never tried: the literal before it always gives a value")]
    [InlineData("$coalesce: [ { $from: a }, { $from: b } ]\n    $modifiers: [trim]", "$modifiers belongs to one of the alternatives $coalesce lists")]
    [InlineData("$coalesce: [ { $from: a, $unverified: true }, { $from: b } ]", "$unverified lets an id the node builds with id or ref go out when the cache holds no record under it, and this node builds no id")]
    [InlineData("$coalesce: [ { $value: x, $unverified: true }, { $from: b } ]", "a literal $value takes only $when and $description beside it")]
    [InlineData("$from: a\n    $coalesce: [ { $from: a }, { $from: b } ]", "reads its value with $from and $coalesce; a node reads one of")]
    [InlineData("$coalesce: [ { $from: a }, { $forEach: curves, $item: { CurveID: { $from: curve_id } } } ]", "repeats rows with $forEach, and an alternative of $coalesce reads one value")]
    public void A_coalesce_that_could_never_work_as_written_is_refused_when_the_mapping_is_read(string node, string expected)
    {
        var refused = Assert.Throws<FlowValidationException>(() => TestSchema.Mapping($"""
              Symbol:
                {node}
            """));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }
}
