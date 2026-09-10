using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
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
    public void Ambiguity_is_first_wins_and_reported()
    {
        var type = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText("a", new Dictionary<string, string> { ["Code"] = "m" }),
            ReferenceItem.FromText("b", new Dictionary<string, string> { ["Code"] = "m" }),
        ]);

        Assert.Equal("a", type.Match("Code", "m")!.Id);
        Assert.True(type.IsAmbiguous("Code"));
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

    [Fact]
    public void Capture_spec_reads_paths_written_either_way()
    {
        var spec = ReferenceCaptureSpec.Parse("""
            {
              "types": [
                {
                  "name": "Wellbore",
                  "entityType": "master-data--Wellbore",
                  "kind": "osdu:wks:master-data--Wellbore:*",
                  "fields": ["data.FacilityName", { "path": "data.NameAlias.AliasName", "as": "Alias" }]
                }
              ]
            }
            """, "spec.json");

        var type = Assert.Single(spec.Types);
        Assert.Equal(["FacilityName", "Alias"], type.Fields.Select(f => f.Name));
        Assert.Equal(["data.FacilityName", "data.NameAlias.AliasName"], type.Fields.Select(f => f.Path));
    }

    [Fact]
    public void Capture_spec_rejects_two_paths_under_one_name()
    {
        var ex = Assert.Throws<FlowValidationException>(() => ReferenceCaptureSpec.Parse("""
            {
              "types": [
                {
                  "name": "Wellbore",
                  "entityType": "master-data--Wellbore",
                  "kind": "osdu:wks:master-data--Wellbore:*",
                  "fields": ["data.FacilityName", { "path": "data.NameAlias.AliasName", "as": "FacilityName" }]
                }
              ]
            }
            """, "spec.json"));
        Assert.Contains("two paths under the name 'FacilityName'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cached_field_called_ID_is_allowed_and_shadows_the_record_id()
    {
        // OSDU reference data carries data.ID, and the sample estate caches it. Only the exact key 'id' collides.
        var spec = ReferenceCaptureSpec.Parse("""
            {
              "types": [
                {
                  "name": "UnitOfMeasure",
                  "entityType": "reference-data--UnitOfMeasure",
                  "kind": "osdu:wks:reference-data--UnitOfMeasure:*",
                  "fields": ["data.Code", "data.ID"]
                }
              ]
            }
            """, "spec.json");
        Assert.Equal(["Code", "ID"], Assert.Single(spec.Types).Fields.Select(f => f.Name));

        var type = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Code"] = "m", ["ID"] = "metre-id" }),
        ]);
        Assert.Equal("dev:reference-data--UnitOfMeasure:m", type.Match("ID", "metre-id")!.Id);
        Assert.Null(type.Match("id", "dev:reference-data--UnitOfMeasure:m"));
        Assert.False(type.MeansRecordId("id"));

        var ex = Assert.Throws<FlowValidationException>(() => ReferenceCaptureSpec.Parse("""
            {
              "types": [
                {
                  "name": "UnitOfMeasure",
                  "entityType": "reference-data--UnitOfMeasure",
                  "kind": "osdu:wks:reference-data--UnitOfMeasure:*",
                  "fields": [{ "path": "data.Code", "as": "id" }]
                }
              ]
            }
            """, "spec.json"));
        Assert.Contains("the key the record id is written under", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Retrieval_flow_declares_the_cache_it_maintains()
    {
        var flow = new DeliveryDocumentLoader().ParseRetrieval("""
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
                - fields:
                    - data.FacilityName
                    - path: data.NameAlias.AliasName
                      as: Alias
            """, "sync.yaml");

        Assert.NotNull(flow.Cache);
        var cache = flow.Cache!;
        Assert.True(cache.MakeCurrent);
        var type = Assert.Single(cache.Types);
        Assert.Equal("Wellbore", type.Name);
        Assert.Equal("master-data--Wellbore", type.EntityType);
        Assert.Equal("osdu:wks:master-data--Wellbore:1.0.0", type.Kind);
        Assert.Equal("*", type.Query);
        Assert.Equal(["FacilityName", "Alias"], type.Fields.Select(f => f.Name));
    }

    [Fact]
    public void Retrieval_flow_needs_a_kind_per_cached_type_when_it_syncs_several()
    {
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseRetrieval("""
            flowType: retrieval
            name: osdu-metadata-sync
            source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: opendes }
              kinds:
                - osdu:wks:master-data--Wellbore:1.0.0
                - osdu:wks:reference-data--UnitOfMeasure:1.0.0
            target:
              location: lake/metadata
            cache:
              types:
                - fields: [data.FacilityName]
            """, "sync.yaml"));
        Assert.Contains("needs a kind", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>The lookup transform: building a document out of what the cache holds.</summary>
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

    private static MappingRenderer Renderer(params MappingProperty[] extra)
        => new(TestSchema.Mapping(extra), TestSchema.Build(), References(), TestSchema.Context());

    private static SourceRecord Record(string unit = "m") => new()
    {
        Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = "well-1", ["depth"] = "12.5", ["unit"] = unit }),
        Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
    };

    private static MappingProperty Lookup(string target, string select, string? type = "UnitOfMeasure")
        => new()
        {
            Target = target,
            Source = "unit",
            Transform = MappingTransform.Lookup,
            Config = new TransformConfig { Type = type, MatchBy = ["Code"], Select = select },
        };

    [Fact]
    public void Reads_a_cached_scalar_into_the_document()
    {
        var result = Renderer(Lookup("data.Symbol", "Symbol")).Render(Record());
        Assert.False(result.IsHeld);
        Assert.Equal("m", result.Document["data"]!["Symbol"]!.GetValue<string>());
    }

    [Fact]
    public void Reads_a_cached_set_into_an_array()
    {
        var result = Renderer(Lookup("data.Aliases", "Alias")).Render(Record());
        Assert.False(result.IsHeld);
        Assert.Equal(["meter", "metre"], result.Document["data"]!["Aliases"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Reads_a_path_inside_a_cached_object()
    {
        var result = Renderer(Lookup("data.Symbol", "Persistable.Scale.Code")).Render(Record());
        Assert.False(result.IsHeld);
        Assert.Equal("SI", result.Document["data"]!["Symbol"]!.GetValue<string>());
    }

    [Fact]
    public void A_scalar_target_wraps_into_an_array_and_a_set_holds()
    {
        var single = Renderer(Lookup("data.Aliases", "Name")).Render(Record());
        Assert.Equal(["metre"], single.Document["data"]!["Aliases"]!.AsArray().Select(n => n!.GetValue<string>()));

        var set = Renderer(Lookup("data.Symbol", "Alias")).Render(Record());
        Assert.True(set.IsHeld);
        Assert.Contains(set.Holds, h => h.Contains("2 values were selected but the schema type is string", StringComparison.Ordinal));
    }

    [Fact]
    public void An_uncached_path_holds_the_record_and_names_what_is_cached()
    {
        var result = Renderer(Lookup("data.Symbol", "NotCached")).Render(Record());
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h =>
            h.Contains("caches nothing at 'NotCached'", StringComparison.Ordinal) && h.Contains("Cached: id, Alias, Code", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missed_match_holds_exactly_as_a_reference_does()
    {
        var result = Renderer(Lookup("data.Symbol", "Symbol")).Render(Record(unit: "furlong"));
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains("no UnitOfMeasure matches 'furlong'", StringComparison.Ordinal));
    }

    [Fact]
    public void Preflight_rejects_a_lookup_the_cache_cannot_answer()
    {
        var mapping = TestSchema.Mapping(Lookup("data.Symbol", "NotCached"));
        var issues = Preflight.Check(mapping, TestSchema.Build(), References(), TestSchema.Context(), dropColumns: null);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("reads 'NotCached'", StringComparison.Ordinal));
    }

    [Fact]
    public void Preflight_rejects_matching_by_fields_the_cache_does_not_hold()
    {
        var property = new MappingProperty
        {
            Target = "data.Unit",
            Source = "unit",
            Transform = MappingTransform.Reference,
            Config = new TransformConfig { Type = "UnitOfMeasure", MatchBy = ["NotCached"] },
        };
        var issues = Preflight.Check(TestSchema.Mapping(property), TestSchema.Build(), References(), TestSchema.Context(), dropColumns: null);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("caches none of those", StringComparison.Ordinal));
    }
}
