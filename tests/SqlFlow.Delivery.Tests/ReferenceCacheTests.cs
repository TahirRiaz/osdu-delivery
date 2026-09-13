using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The cache the mappings resolve against: what a captured path yields (a scalar, a set, a nested object), how the
/// cache is matched and read, and how a cache definition is declared in YAML.
/// </summary>
public class ReferenceCacheTests
{
    private static JsonObject Hit(string json) => JsonNode.Parse(json) as JsonObject ?? throw new DeliveryException("not an object");

    [Fact]
    public void Path_selects_through_arrays_of_objects()
    {
        var hit = Hit("""
            {
              "id": "opendes:master-data--Wellbore:1",
              "data": {
                "FacilityName": "NO 1/1-A",
                "NameAlias": [
                  { "AliasName": "1/1-A", "AliasNameTypeID": "opendes:reference-data--AliasNameType:Short:" },
                  { "AliasName": "WELL A" }
                ],
                "Tags": { "source": "recall" }
              }
            }
            """);

        Assert.Equal("NO 1/1-A", Assert.Single(JsonPathReader.SelectNodes(hit, "data.FacilityName")).GetValue<string>());
        Assert.Equal(["1/1-A", "WELL A"], JsonPathReader.SelectNodes(hit, "data.NameAlias.AliasName").Select(n => n.GetValue<string>()));
        Assert.Equal("1/1-A", Assert.Single(JsonPathReader.SelectNodes(hit, "data.NameAlias[0].AliasName")).GetValue<string>());
        Assert.Equal("recall", Assert.Single(JsonPathReader.SelectNodes(hit, "data.Tags.source")).GetValue<string>());
        Assert.Empty(JsonPathReader.SelectNodes(hit, "data.NotThere"));
    }

    [Fact]
    public void Value_keeps_any_json_and_flattens_its_terms()
    {
        var set = ReferenceValue.From(JsonNode.Parse("""["1/1-A", "WELL A"]""")!);
        Assert.True(set.IsSet);
        Assert.Equal(2, set.Count);
        Assert.Equal(["1/1-A", "WELL A"], set.Terms);

        var scalar = ReferenceValue.From(JsonValue.Create(42));
        Assert.False(scalar.IsSet);
        Assert.Equal("42", scalar.Text);
        Assert.Equal(["42"], scalar.Terms);

        var nested = ReferenceValue.From(JsonNode.Parse("""{ "AliasName": "1/1-A", "Deep": { "Code": "A" } }""")!);
        Assert.Equal(["1/1-A", "A"], nested.Terms);
        Assert.Equal("1/1-A", nested.Select("AliasName")!.Text);
        Assert.Equal("A", nested.Select("Deep.Code")!.Text);
        Assert.Null(nested.Select("Missing"));
    }

    [Fact]
    public void A_set_matches_by_any_of_its_values()
    {
        var type = new ReferenceType("Wellbore", "master-data--Wellbore",
        [
            new ReferenceItem("dev:master-data--Wellbore:1", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["FacilityName"] = ReferenceValue.Of("NO 1/1-A"),
                ["Alias"] = ReferenceValue.From(JsonNode.Parse("""["1/1-A", "WELL A"]""")!),
            }),
            new ReferenceItem("dev:master-data--Wellbore:2", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["FacilityName"] = ReferenceValue.Of("NO 2/2-B"),
            }),
        ]);

        Assert.Equal("dev:master-data--Wellbore:1", type.Match("Alias", "1/1-A")!.Id);
        Assert.Equal("dev:master-data--Wellbore:1", type.Match("Alias", " well a ")!.Id);
        Assert.Equal("dev:master-data--Wellbore:1", type.Match("data.FacilityName", "NO 1/1-A")!.Id);
        Assert.Equal("dev:master-data--Wellbore:2", type.Match("id", "dev:master-data--Wellbore:2")!.Id);
        Assert.Null(type.Match("Alias", "nothing"));
        Assert.True(type.HasField("Alias"));
        Assert.False(type.HasField("Nope"));
        Assert.Equal(["Alias", "FacilityName"], type.FieldNames);
    }

    [Fact]
    public void Several_records_holding_a_value_exactly_match_none_of_them_and_are_all_named()
    {
        var type = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText("a", new Dictionary<string, string> { ["Code"] = "m" }),
            ReferenceItem.FromText("b", new Dictionary<string, string> { ["Code"] = "m" }),
            ReferenceItem.FromText("c", new Dictionary<string, string> { ["Code"] = "ft" }),
        ]);

        // Taking the first would write a reference nobody chose, so the value resolves to neither and both are listed.
        Assert.Null(type.Match("Code", "m"));
        var found = type.Find("Code", "m");
        Assert.True(found.IsCaseAmbiguous);
        Assert.Equal(ReferenceMatchKind.Exact, found.Kind);
        Assert.Equal("exactly", found.Loosening);
        Assert.Equal(["a", "b"], found.CaseVariants.Select(v => v.Id));
        Assert.True(type.IsAmbiguous("Code"));

        // A value only one record holds still resolves.
        Assert.Equal("c", type.Match("Code", "ft")!.Id);
    }

    [Fact]
    public void Codes_that_differ_only_by_case_are_different_records()
    {
        // As a live partition's search returned them: the femtotesla before the foot.
        var type = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText("test:reference-data--UnitOfMeasure:fT", new Dictionary<string, string> { ["Code"] = "fT", ["Name"] = "femtotesla" }),
            ReferenceItem.FromText("test:reference-data--UnitOfMeasure:ft", new Dictionary<string, string> { ["Code"] = "ft", ["Name"] = "foot" }),
            ReferenceItem.FromText("test:reference-data--UnitOfMeasure:gAPI", new Dictionary<string, string> { ["Code"] = "gAPI", ["Name"] = "API gamma ray" }),
        ]);

        Assert.Equal("test:reference-data--UnitOfMeasure:ft", type.Match("Code", "ft")!.Id);
        Assert.Equal("test:reference-data--UnitOfMeasure:fT", type.Match("Code", "fT")!.Id);
        Assert.Equal("test:reference-data--UnitOfMeasure:ft", type.Match("id", "test:reference-data--UnitOfMeasure:ft")!.Id);

        // One item under folded case is still found, so an upper-case drop value keeps resolving.
        Assert.Equal("test:reference-data--UnitOfMeasure:gAPI", type.Match("Code", "GAPI")!.Id);

        // Two items under folded case: neither is chosen, and both are named.
        var undecided = type.Find("Code", "FT");
        Assert.Null(undecided.Item);
        Assert.True(undecided.IsCaseAmbiguous);
        Assert.Equal(["test:reference-data--UnitOfMeasure:fT", "test:reference-data--UnitOfMeasure:ft"], undecided.CaseVariants.Select(i => i.Id));
        Assert.Null(type.Match("Code", "FT"));
        Assert.False(type.IsAmbiguous("Code"));
    }

    [Fact]
    public void Items_survive_the_snapshot_round_trip_whatever_their_shape()
    {
        var type = new ReferenceType("Wellbore", "master-data--Wellbore",
        [
            new ReferenceItem("dev:master-data--Wellbore:1", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["FacilityName"] = ReferenceValue.Of("NO 1/1-A"),
                ["Alias"] = ReferenceValue.From(JsonNode.Parse("""["1/1-A", "WELL A"]""")!),
                ["Depth"] = ReferenceValue.From(JsonValue.Create(1234.5)),
                ["Tags"] = ReferenceValue.From(JsonNode.Parse("""{ "source": "recall" }""")!),
            }),
        ]);

        var round = ReferenceType.FromJson("Wellbore", type.ToJson());
        var item = Assert.Single(round.Items);
        Assert.Equal(["1/1-A", "WELL A"], item.Select("Alias")!.Terms);
        Assert.Equal("1234.5", item.Select("Depth")!.Text);
        Assert.Equal("recall", item.Select("Tags.source")!.Text);
        Assert.Equal("dev:master-data--Wellbore:1", round.Value(item, "id")!.Text);
    }

    [Fact]
    public void Text_only_items_hash_as_they_did_before_sets_were_supported()
    {
        // The sample estate's snapshots carry recorded hashes, so a scalar capture must serialise unchanged.
        var type = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Code"] = "m", ["Name"] = "metre" }),
        ]);

        Assert.Equal(
            """{"entityType":"reference-data--UnitOfMeasure","items":[{"Code":"m","Name":"metre","id":"dev:reference-data--UnitOfMeasure:m"}]}""",
            CanonicalJson.ToString(type.ToJson()));
    }

    [Fact]
    public void Index_is_safe_under_the_parallel_renders_of_one_run()
    {
        var items = Enumerable.Range(0, 500)
            .Select(i => ReferenceItem.FromText($"dev:reference-data--UnitOfMeasure:{i}", new Dictionary<string, string>
            {
                ["Code"] = $"C{i}",
                ["Name"] = $"N{i}",
                ["ID"] = $"I{i}",
            }))
            .ToList();
        var type = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", items);

        Parallel.For(0, 512, i =>
        {
            var field = (i % 3) switch { 0 => "Code", 1 => "Name", _ => "ID" };
            var prefix = field switch { "Code" => "C", "Name" => "N", _ => "I" };
            var hit = type.Match(field, prefix + (i % 500));
            Assert.NotNull(hit);
        });
    }

    [Fact]
    public void Field_names_normalise_the_data_prefix()
    {
        Assert.Equal("Code", ReferenceField.Normalize("data.Code"));
        Assert.Equal("NameAlias.AliasName", ReferenceField.Normalize("$.data.NameAlias.AliasName"));
        Assert.Equal("legal.legaltags", ReferenceField.Normalize("legal.legaltags"));
        Assert.True(ReferenceField.IsId("data.id"));
        Assert.Equal([("NameAlias", "AliasName")], ReferenceField.Prefixes("data.NameAlias.AliasName"));
    }

    /// <summary>A cache flow over the given <c>types:</c> block (list items indented by two spaces).</summary>
    private static CacheDefinition CacheFlow(string types, string extra = "") => new DeliveryDocumentLoader().ParseCache(
        """
        flowType: cache
        name: osdu-reference-cache
        source:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: opendes }

        """ + extra + "\ntypes:\n" + types + "\n",
        "caches/osdu-reference-cache.yaml");

    [Fact]
    public void A_cache_flow_reads_paths_written_either_way()
    {
        var cache = CacheFlow("""
              - kind: "osdu:wks:master-data--Wellbore:*"
                name: Wellbore
                fields:
                  - data.FacilityName
                  - path: data.NameAlias.AliasName
                    as: Alias
            """);

        var type = Assert.Single(cache.Types);
        Assert.Equal(["FacilityName", "Alias"], type.Fields.Select(f => f.Name));
        Assert.Equal(["data.FacilityName", "data.NameAlias.AliasName"], type.Fields.Select(f => f.Path));
    }

    [Fact]
    public void A_cache_flow_refuses_two_paths_under_one_name()
    {
        var ex = Assert.Throws<FlowValidationException>(() => CacheFlow("""
              - kind: "osdu:wks:master-data--Wellbore:*"
                name: Wellbore
                fields:
                  - data.FacilityName
                  - path: data.NameAlias.AliasName
                    as: FacilityName
            """));
        Assert.Contains("two paths under the name 'FacilityName'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cached_field_called_ID_is_allowed_and_shadows_the_record_id()
    {
        // OSDU reference data carries data.ID, and the sample estate caches it. Only the exact key 'id' collides.
        var cache = CacheFlow("""
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                fields: [data.Code, data.ID]
            """);
        Assert.Equal(["Code", "ID"], Assert.Single(cache.Types).Fields.Select(f => f.Name));

        var type = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Code"] = "m", ["ID"] = "metre-id" }),
        ]);
        Assert.Equal("dev:reference-data--UnitOfMeasure:m", type.Match("ID", "metre-id")!.Id);
        Assert.Null(type.Match("id", "dev:reference-data--UnitOfMeasure:m"));
        Assert.False(type.MeansRecordId("id"));

        var ex = Assert.Throws<FlowValidationException>(() => CacheFlow("""
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                fields:
                  - path: data.Code
                    as: id
            """));
        Assert.Contains("the key the record id is written under", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cache_flow_declares_what_the_cache_holds()
    {
        var cache = CacheFlow(
            """
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                onChange: auto
                fields: [data.FacilityName]
              - kind: osdu:wks:reference-data--UnitOfMeasure:1.0.0
                query: data.Code:m*
                fields: [data.Code]
            """,
            "onChange: approve\n");

        Assert.Equal("osdu-reference-cache", cache.Name);
        Assert.True(cache.MakeCurrent);
        Assert.Equal("https://osdu.example.com", cache.Source.Endpoint);
        var wellbore = cache.Types[0];
        Assert.Equal("Wellbore", wellbore.Name);
        Assert.Equal("master-data--Wellbore", wellbore.EntityType);
        Assert.Equal("*", wellbore.Query);
        Assert.Equal(CacheChangeMode.Auto, wellbore.OnChange);
        var units = cache.Types[1];
        Assert.Equal("UnitOfMeasure", units.Name);
        Assert.Equal("data.Code:m*", units.Query);
        Assert.Equal(CacheChangeMode.Approve, units.OnChange);
    }

    [Fact]
    public void A_cached_type_names_its_kind_and_a_query_uses_only_declared_parameters()
    {
        var noKind = Assert.Throws<FlowValidationException>(() => CacheFlow("""
              - name: Wellbore
                entityType: master-data--Wellbore
                fields: [data.FacilityName]
            """));
        Assert.Contains("types[0].kind is required", noKind.Message, StringComparison.Ordinal);

        var token = Assert.Throws<FlowValidationException>(() => CacheFlow("""
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                query: data.Country:{country}
                fields: [data.FacilityName]
            """));
        Assert.Contains("'{country}', which is not declared under parameters", token.Message, StringComparison.Ordinal);

        var none = Assert.Throws<FlowValidationException>(() => CacheFlow(string.Empty));
        Assert.Contains("types is required", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retrieval_flow_declares_no_cache()
    {
        // What is cached is defined by a cache flow alone; a cache section on a retrieval flow is an unknown key.
        Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseRetrieval("""
            flowType: retrieval
            name: osdu-metadata-sync
            source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: opendes }
              kinds: [osdu:wks:master-data--Wellbore:1.0.0]
            target:
              location: lake/metadata
            cache:
              types:
                - fields: [data.FacilityName]
            """, "sync.yaml"));
    }

    [Fact]
    public void A_delivery_flow_pins_a_version_only_of_a_cache_it_names()
    {
        var sample = File.ReadAllText(Samples.Flow);
        var loader = new DeliveryDocumentLoader();
        Assert.Equal(Samples.SampleCacheName, loader.ParseFlow(sample, "flow.yaml").Render.Cache);

        var pinned = loader.ParseFlow(sample.Replace("cache: osdu-reference-cache", "cache: osdu-reference-cache\n  cacheVersion: 20260908T212727Z", StringComparison.Ordinal), "flow.yaml");
        Assert.Equal("20260908T212727Z", pinned.Render.CacheVersion);

        var ex = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(
            sample.Replace("cache: osdu-reference-cache", "cacheVersion: 20260908T212727Z", StringComparison.Ordinal), "flow.yaml"));
        Assert.Contains("render.cache names no cache", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mapping_that_reads_the_cache_renders_only_against_a_cache_the_flow_names_and_the_catalog_holds()
    {
        var engine = Samples.Engine(ledger: null);
        var values = new Dictionary<string, string> { ["logSource"] = "STAT_COMP" };
        var flow = Samples.LocalFlow(Samples.NewTempDirectory());

        using (var runtime = await FlowRuntime.CreateAsync(engine, flow, values, dropOverride: null))
        {
            Assert.Equal(Samples.SampleCacheName, runtime.Mapping.Context.CacheName);
            Assert.Equal("20260908T212727Z", runtime.Mapping.Context.CacheVersion);
        }

        var unnamed = await Assert.ThrowsAsync<FlowValidationException>(() => FlowRuntime.CreateAsync(engine, flow with { Render = flow.Render with { Cache = null } }, values, dropOverride: null));
        Assert.Contains("render.cache must name the cache it reads", unnamed.Message, StringComparison.Ordinal);

        var unknown = await Assert.ThrowsAsync<FlowValidationException>(() => FlowRuntime.CreateAsync(engine, flow with { Render = flow.Render with { Cache = "no-such-cache" } }, values, dropOverride: null));
        Assert.Contains("cache 'no-such-cache' has no current version", unknown.Message, StringComparison.Ordinal);

        var missingVersion = await Assert.ThrowsAsync<FlowValidationException>(() => FlowRuntime.CreateAsync(engine, flow with { Render = flow.Render with { CacheVersion = "19990101T000000Z" } }, values, dropOverride: null));
        Assert.Contains("which the catalog does not hold", missingVersion.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sample mapping once declared the source unit `V/V` as `%`, so a neutron porosity of 0.21 was published as 0.21
    /// percent, a hundredth of what the curve carries. `V/V` is a volume fraction, which OSDU's reference data calls
    /// `m3/m3`. The sample mapping's own curve unit entry is rendered here against the sample template and cache, so the
    /// mapping and the cached reference data have to agree for this to pass. The cache holds `%` as well as `m3/m3`, so
    /// replacing the unit back with `%` would resolve rather than hold: only the rendered id catches it.
    /// </summary>
    [Fact]
    public async Task The_sample_mapping_renders_the_porosity_unit_as_a_volume_fraction()
    {
        var mapping = new MappingCatalog(Samples.Mappings, new DeliveryDocumentLoader()).Load("WellLog@1.4.0");
        var curveUnit = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Curves[].CurveUnit");
        Assert.Equal("m3/m3", curveUnit.Modifiers.Single(m => m.Kind == ModifierKind.Replace).Replacements["V/V"]);

        var version = await Samples.SampleCache.CurrentVersionAsync(Samples.SampleCacheName);
        Assert.NotNull(version);
        var references = await Samples.SampleCache.LoadAsync(Samples.SampleCacheName, version);
        Assert.NotNull(references);
        var schema = await Samples.SampleTemplates.LoadAsync(mapping.Template);
        Assert.NotNull(schema);

        var context = new RenderContext
        {
            MappingReference = mapping.Reference,
            CacheName = Samples.SampleCacheName,
            CacheVersion = references.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "opendes" },
        };
        var renderer = new MappingRenderer(mapping, schema, references, context);
        var fixture = mapping.Fixtures.Single(f => f.Name.StartsWith("L-2001", StringComparison.Ordinal));
        var porosity = renderer.Render(new SourceRecord
        {
            Row = SourceRow.FromStrings(fixture.Record),
            Scopes = fixture.Datasets.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<SourceRow>)kv.Value.Select(SourceRow.FromStrings).ToList(),
                StringComparer.OrdinalIgnoreCase),
        });

        Assert.False(porosity.IsHeld, string.Join("; ", porosity.Holds));
        var nphi = porosity.Document["data"]!["Curves"]!.AsArray().Single(c => c!["CurveID"]!.GetValue<string>() == "NPHI");
        Assert.Equal("opendes:reference-data--UnitOfMeasure:m3%2Fm3:", nphi!["CurveUnit"]!.GetValue<string>());
    }
}

/// <summary>Cache sources that read a field out of the cached record rather than its id: building a document out of what the cache holds.</summary>
public class CachedLookupTests
{
    private static ReferenceSnapshot References() => new("refs-1", DateTimeOffset.UnixEpoch,
    [
        new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            new ReferenceItem("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Code"] = ReferenceValue.Of("m"),
                ["Name"] = ReferenceValue.Of("metre"),
                ["Symbol"] = ReferenceValue.Of("m"),
                ["Alias"] = ReferenceValue.From(JsonNode.Parse("""["meter", "metre"]""")!),
                ["Persistable"] = ReferenceValue.From(JsonNode.Parse("""{ "Scale": { "Code": "SI" } }""")!),
            }),
        ]),
        new ReferenceType("Wellbore", "master-data--Wellbore",
        [
            ReferenceItem.FromText("dev:master-data--Wellbore:abc", new Dictionary<string, string> { ["FacilityName"] = "NO 1/1-A" }),
        ]),
    ]);

    private static MappingRenderer Renderer(string entries)
        => new(TestSchema.Mapping(entries), TestSchema.Build(), References(), TestSchema.Context());

    private static SourceRecord Record(string unit = "m") => new()
    {
        Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = "well-1", ["depth"] = "12.5", ["unit"] = unit }),
        Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
    };

    private static string Read(string target, string field, string findBy = "Code") => $$"""
          - target: {{target}}
            source: cache.UnitOfMeasure.{{field}}
            findBy: cache.UnitOfMeasure.{{findBy}} = dataset.unit
        """;

    [Fact]
    public void Reads_a_cached_scalar_into_the_document()
    {
        var result = Renderer(Read("osdu.data.Symbol", "Symbol")).Render(Record());
        Assert.False(result.IsHeld);
        Assert.Equal("m", result.Document["data"]!["Symbol"]!.GetValue<string>());
    }

    [Fact]
    public void Reads_a_cached_set_into_an_array()
    {
        var result = Renderer(Read("osdu.data.Aliases", "Alias")).Render(Record());
        Assert.False(result.IsHeld);
        Assert.Equal(["meter", "metre"], result.Document["data"]!["Aliases"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Reads_a_path_inside_a_cached_object()
    {
        var result = Renderer(Read("osdu.data.Symbol", "Persistable.Scale.Code")).Render(Record());
        Assert.False(result.IsHeld);
        Assert.Equal("SI", result.Document["data"]!["Symbol"]!.GetValue<string>());
    }

    [Fact]
    public void A_scalar_target_wraps_into_an_array_and_a_set_holds()
    {
        var single = Renderer(Read("osdu.data.Aliases", "Name")).Render(Record());
        Assert.Equal(["metre"], single.Document["data"]!["Aliases"]!.AsArray().Select(n => n!.GetValue<string>()));

        var set = Renderer(Read("osdu.data.Symbol", "Alias")).Render(Record());
        Assert.True(set.IsHeld);
        Assert.Contains(set.Holds, h => h.Contains("2 values were given but the template takes one string", StringComparison.Ordinal));
    }

    [Fact]
    public void An_uncached_field_holds_the_record_and_names_what_is_cached()
    {
        var result = Renderer(Read("osdu.data.Symbol", "NotCached")).Render(Record());
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h =>
            h.Contains("caches nothing at 'NotCached'", StringComparison.Ordinal) && h.Contains("Cached: id, Alias, Code", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missed_match_holds_exactly_as_reading_the_id_does()
    {
        var result = Renderer(Read("osdu.data.Symbol", "Symbol")).Render(Record(unit: "furlong"));
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains("no UnitOfMeasure matches 'furlong'", StringComparison.Ordinal));
    }

    [Fact]
    public void Preflight_rejects_a_field_the_cache_cannot_answer()
    {
        var issues = Preflight.Check(TestSchema.Mapping(Read("osdu.data.Symbol", "NotCached")), TestSchema.Build(), References(), TestSchema.Context(), dropColumns: null);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("reads 'NotCached' out of UnitOfMeasure", StringComparison.Ordinal));
    }

    [Fact]
    public void Preflight_rejects_matching_by_fields_the_cache_does_not_hold()
    {
        var issues = Preflight.Check(TestSchema.Mapping(Read("osdu.data.Unit", "id", findBy: "NotCached")), TestSchema.Build(), References(), TestSchema.Context(), dropColumns: null);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("caches none of those", StringComparison.Ordinal));
    }
}
