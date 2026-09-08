using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Tests;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

public class CanonicalJsonTests
{
    [Fact]
    public void Sorts_keys_removes_nulls_and_normalises_numbers()
    {
        var canonical = CanonicalJson.Canonicalize("""{ "b": 1.0, "a": null, "c": { "z": 1e2, "y": [3, null, 0.5] }, "d": "x" }""");
        Assert.Equal("""{"b":1,"c":{"y":[3,null,0.5],"z":100},"d":"x"}""", canonical);
    }

    [Fact]
    public void Is_byte_stable_across_whitespace_and_key_order()
    {
        var a = CanonicalJson.Canonicalize("""{"x": {"k2": 2, "k1": 1}}""");
        var b = CanonicalJson.Canonicalize("""{ "x" : { "k1" : 1 , "k2" : 2 } }""");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Formats_doubles_shortest_round_trip()
    {
        Assert.Equal("0.1", CanonicalJson.FormatDouble(0.1));
        Assert.Equal("1E+21", CanonicalJson.FormatDouble(1e21));
        Assert.Throws<InvalidOperationException>(() => CanonicalJson.FormatDouble(double.NaN));
    }

    [Fact]
    public void Keeps_array_order()
    {
        var canonical = CanonicalJson.Canonicalize("""[{"b":1,"a":2},{"a":1}]""");
        Assert.Equal("""[{"a":2,"b":1},{"a":1}]""", canonical);
    }
}

public class DeterministicGuidTests
{
    [Fact]
    public void Matches_the_rfc_4122_version_5_test_vector()
    {
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        Assert.Equal(new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"), DeterministicGuid.V5(dns, "www.example.com"));
    }

    [Fact]
    public void Delivery_key_is_deterministic_and_separator_safe()
    {
        var a = DeliveryKey.Derive("recall", ["NO_15_9", "L-1001"]);
        var b = DeliveryKey.Derive("Recall ", ["NO_15_9", "L-1001"]);
        Assert.Equal(a, b);
        Assert.NotEqual(DeliveryKey.Derive("recall", ["a|b", "c"]), DeliveryKey.Derive("recall", ["a", "b|c"]));
        Assert.Equal('5', a.ToString()[14]);
    }

    [Fact]
    public void Flow_id_ignores_case_and_whitespace()
    {
        Assert.Equal(FlowId.Of("recall-welllog"), FlowId.Of(" Recall-WellLog "));
        Assert.NotEqual(FlowId.Of("a"), FlowId.Of("b"));
    }

    [Fact]
    public void Target_id_uses_the_key_without_hyphens()
    {
        var key = DeliveryKey.Derive("recall", ["x"]);
        var id = TargetId.Compose("dev", "work-product-component--WellLog", key);
        Assert.StartsWith("dev:work-product-component--WellLog:", id, StringComparison.Ordinal);
        Assert.DoesNotContain("-", id[(id.LastIndexOf(':') + 1)..], StringComparison.Ordinal);
        Assert.Equal("work-product-component--WellLog", TargetId.EntityTypeFromKind("osdu:wks:work-product-component--WellLog:1.4.0"));
    }
}

public class ContentHashTests
{
    [Fact]
    public void Parts_are_length_prefixed_so_concatenation_cannot_collide()
    {
        Assert.NotEqual(Hashing.ContentHash.OfParts("ab", "c"), Hashing.ContentHash.OfParts("a", "bc"));
        Assert.Equal(64, Hashing.ContentHash.Of("x").Length);
    }
}

public class SchemaSnapshotTests
{
    [Fact]
    public void Resolves_through_allOf_refs_arrays_and_additional_properties()
    {
        var schema = TestSchema.Build();
        Assert.Equal(SchemaType.String, schema.Resolve("data.Name")!.Type);
        Assert.Equal(SchemaType.Number, schema.Resolve("data.Depth")!.Type);
        Assert.Equal(SchemaType.Array, schema.Resolve("data.Curves")!.Type);
        Assert.Equal(SchemaType.Number, schema.Resolve("data.Curves.TopDepth")!.Type);
        Assert.Equal(SchemaType.String, schema.Resolve("tags.Anything")!.Type);
        Assert.Equal(SchemaType.Array, schema.Resolve("acl.owners")!.Type);
        Assert.True(schema.Resolve("data.WellboreID")!.IsRelationship);
        Assert.Null(schema.Resolve("data.Missing"));
    }

    [Fact]
    public void Reports_required_properties_per_level()
    {
        var schema = TestSchema.Build();
        Assert.Equal(["kind", "acl", "legal"], schema.RequiredAt(string.Empty));
        Assert.Equal(["Depth"], schema.RequiredAt("data"));
    }

    [Fact]
    public void Version_is_content_addressed()
    {
        var a = TestSchema.Build();
        var b = TestSchema.Build();
        Assert.Equal(a.Version, b.Version);
        Assert.Equal(16, a.Version.Length);
    }

    [Fact]
    public void Non_local_refs_are_rejected()
    {
        var schema = SchemaSnapshot.Parse("k:s:t:1.0.0", """{ "type": "object", "properties": { "data": { "$ref": "../other.json" } } }""", DateTimeOffset.UnixEpoch);
        Assert.Throws<DeliveryException>(() => schema.Resolve("data.X"));
    }
}

public class MappingRendererTests
{
    private static SourceRecord Record(string name = "well-1", string? depth = "12.5", string? unit = "m")
        => new()
        {
            Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = name, ["depth"] = depth, ["unit"] = unit, ["wb"] = "NO 1/1-A", ["flag"] = "REGULAR" }),
            Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase)
            {
                ["curves"] = [SourceRow.FromStrings(new Dictionary<string, string?> { ["curve_id"] = "GR", ["top"] = "1" }), SourceRow.FromStrings(new Dictionary<string, string?> { ["curve_id"] = "RHOB", ["top"] = "2" })],
            },
        };

    private static MappingRenderer Renderer(params MappingProperty[] extra)
        => new(TestSchema.Mapping(extra), TestSchema.Build(), TestSchema.References(), TestSchema.Context());

    [Fact]
    public void Renders_envelope_id_and_coerced_scalars()
    {
        var result = Renderer().Render(Record());
        Assert.False(result.IsHeld);
        Assert.Equal("dev:work-product-component--Thing:" + result.Key!.Value.Value.ToString("N"), result.TargetId);
        var data = result.Document["data"]!;
        Assert.Equal(12.5, data["Depth"]!.GetValue<double>());
        Assert.Equal("well-1", data["Name"]!.GetValue<string>());
        Assert.Equal("test:wks:work-product-component--Thing:1.0.0", result.Document["kind"]!.GetValue<string>());
        Assert.Equal("tag", result.Document["legal"]!["legaltags"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Renders_references_collections_and_nested_objects()
    {
        var renderer = Renderer(
            new MappingProperty { Target = "data.Unit", Source = "unit", Transform = MappingTransform.Reference, Config = new TransformConfig { Type = "UnitOfMeasure", MatchBy = ["Code"] } },
            new MappingProperty { Target = "data.WellboreID", Source = "wb", Transform = MappingTransform.Reference, Config = new TransformConfig { Type = "Wellbore", MatchBy = ["FacilityName"] } },
            new MappingProperty { Target = "data.IsRegular", Source = "flag", Transform = MappingTransform.Equals, Config = new TransformConfig { Resolve = "regular" } },
            new MappingProperty { Target = "data.Nested", Properties = [new MappingProperty { Target = "Inner", Source = "name", Transform = MappingTransform.Upper }] },
            new MappingProperty { Target = "data.Curves", Collection = true, Scope = "curves", Properties = [new MappingProperty { Target = "CurveID", Source = "curve_id" }, new MappingProperty { Target = "TopDepth", Source = "top" }] });
        var result = renderer.Render(Record());
        Assert.False(result.IsHeld);
        var data = result.Document["data"]!;
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", data["Unit"]!.GetValue<string>());
        Assert.Equal("dev:master-data--Wellbore:abc:", data["WellboreID"]!.GetValue<string>());
        Assert.True(data["IsRegular"]!.GetValue<bool>());
        Assert.Equal("WELL-1", data["Nested"]!["Inner"]!.GetValue<string>());
        Assert.Equal(2, data["Curves"]!.AsArray().Count);
        Assert.Equal(2, data["Curves"]![1]!["TopDepth"]!.GetValue<long>());
    }

    [Fact]
    public void Holds_on_missing_reference_and_incomplete_key()
    {
        var renderer = Renderer(new MappingProperty { Target = "data.Unit", Source = "unit", Transform = MappingTransform.Reference, Config = new TransformConfig { Type = "UnitOfMeasure", MatchBy = ["Code"] } });
        var missingUnit = renderer.Render(Record(unit: "furlong"));
        Assert.True(missingUnit.IsHeld);
        Assert.Contains(missingUnit.Holds, h => h.Contains("no UnitOfMeasure matches 'furlong'", StringComparison.Ordinal));

        var noKey = renderer.Render(Record(name: " "));
        Assert.True(noKey.IsHeld);
        Assert.Null(noKey.Key);
    }

    [Fact]
    public void Holds_on_schema_required_property_missing_and_bad_number()
    {
        var result = Renderer().Render(Record(depth: null));
        Assert.Contains(result.Holds, h => h.Contains("schema-required property data.Depth", StringComparison.Ordinal));
        var bad = Renderer().Render(Record(depth: "deep"));
        Assert.Contains(bad.Holds, h => h.Contains("not a valid number", StringComparison.Ordinal));
    }

    [Fact]
    public void Hash_moves_with_the_render_context_but_not_with_operational_settings()
    {
        var a = Renderer().Render(Record());
        var other = new MappingRenderer(TestSchema.Mapping(), TestSchema.Build(), TestSchema.References(), TestSchema.Context() with { ReferenceSnapshotVersion = "refs-2" });
        var b = other.Render(Record());
        Assert.Equal(a.Canonical, b.Canonical);
        Assert.NotEqual(a.MetadataHash, b.MetadataHash);
        Assert.Equal(a.MetadataHash, Renderer().Render(Record()).MetadataHash);
    }

    [Fact]
    public void Disagreeing_declared_key_is_held()
    {
        var record = Record() with { DeclaredDeliveryKey = Guid.NewGuid() };
        var result = Renderer().Render(record);
        Assert.Contains(result.Holds, h => h.Contains("two halves disagree", StringComparison.Ordinal));
    }

    [Fact]
    public void Constant_expands_parameters_and_split_omits_missing_segment()
    {
        var renderer = Renderer(
            new MappingProperty { Target = "data.Description", Transform = MappingTransform.Constant, Config = new TransformConfig { Value = "{param:dataPartition}-x" } },
            new MappingProperty { Target = "data.Nested", Properties = [new MappingProperty { Target = "Inner", Source = "name", Transform = MappingTransform.Split, Config = new TransformConfig { Delimiter = ",", Index = 1 } }] });
        var result = renderer.Render(Record());
        Assert.Equal("dev-x", result.Document["data"]!["Description"]!.GetValue<string>());
        Assert.Null(result.Document["data"]!["Nested"]);
    }
}

public class PreflightTests
{
    private static IReadOnlyDictionary<string, IReadOnlySet<string>> Columns(params string[] rootColumns)
        => new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["record"] = new HashSet<string>(rootColumns, StringComparer.OrdinalIgnoreCase),
            ["curves"] = new HashSet<string>(["curve_id", "top"], StringComparer.OrdinalIgnoreCase),
        };

    [Fact]
    public void Passes_a_consistent_combination()
    {
        var issues = Preflight.Check(TestSchema.Mapping(), TestSchema.Build(), TestSchema.References(), TestSchema.Context(), Columns("name", "depth"));
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Fails_on_missing_source_column_unknown_target_and_missing_reference_type()
    {
        var mapping = TestSchema.Mapping(
            new MappingProperty { Target = "data.Nope", Source = "depth" },
            new MappingProperty { Target = "data.Unit", Source = "unit", Transform = MappingTransform.Reference, Config = new TransformConfig { Type = "Country" } });
        var issues = Preflight.Check(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context(), Columns("name"));
        Assert.Contains(issues, i => i.Message.Contains("binds to column 'depth'", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("'data.Nope' does not exist", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("reference type 'Country'", StringComparison.Ordinal));
        Assert.Throws<FlowValidationException>(() => Preflight.ThrowIfFailed(issues, "test"));
    }

    [Fact]
    public void Fails_when_a_schema_required_property_is_unbound()
    {
        var mapping = TestSchema.Mapping() with { Properties = [new MappingProperty { Target = "data.Name", Source = "name" }] };
        var issues = Preflight.Check(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context(), null);
        Assert.Contains(issues, i => i.Message.Contains("requires data.Depth", StringComparison.Ordinal));
    }

    [Fact]
    public void Fails_on_undeclared_parameter_and_example_mismatch()
    {
        var mapping = TestSchema.Mapping(new MappingProperty { Target = "data.Description", Source = "name", Transform = MappingTransform.Upper, Examples = [new PropertyExample { Source = "abc", Target = "abc" }] });
        var context = TestSchema.Context() with { Parameters = new Dictionary<string, string> { ["dataPartition"] = "dev", ["extra"] = "1" } };
        var issues = Preflight.Check(mapping, TestSchema.Build(), TestSchema.References(), context, null);
        Assert.Contains(issues, i => i.Message.Contains("parameter 'extra'", StringComparison.Ordinal));

        var examples = Preflight.Check(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context(), null);
        Assert.Contains(examples, i => i.Message.Contains("rendered \"ABC\", expected \"abc\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Fixtures_are_rendered_and_compared_canonically()
    {
        var expected = """{"id":"dev:work-product-component--Thing:%KEY%","kind":"test:wks:work-product-component--Thing:1.0.0","acl":{"owners":["owners@x"],"viewers":["viewers@x"]},"legal":{"legaltags":["tag"],"otherRelevantDataCountries":["NO"]},"data":{"Name":"w","Depth":1}}""";
        var key = DeliveryKey.Derive("test", ["w"]).Value.ToString("N");
        var mapping = TestSchema.Mapping() with
        {
            Fixtures = [new MappingFixture { Name = "f", Record = new Dictionary<string, string?> { ["name"] = "w", ["depth"] = "1" }, Expected = expected.Replace("%KEY%", key, StringComparison.Ordinal) }],
        };
        var issues = Preflight.Check(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context(), null);
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);

        var wrong = mapping with { Fixtures = [mapping.Fixtures[0] with { Expected = expected.Replace("%KEY%", key, StringComparison.Ordinal).Replace("\"Depth\":1", "\"Depth\":2", StringComparison.Ordinal) }] };
        var failing = Preflight.Check(wrong, TestSchema.Build(), TestSchema.References(), TestSchema.Context(), null);
        Assert.Contains(failing, i => i.Message.Contains("~ data.Depth: 2 -> 1", StringComparison.Ordinal));
    }
}

public class ChangeDetectorTests
{
    private static RecordState Delivered(string fingerprint = "f1", string meta = "m1", string payload = "p1", string context = "c1") => new()
    {
        DeliveryKey = new DeliveryKey(Guid.NewGuid()),
        FlowId = Guid.NewGuid(),
        SourceKey = "k",
        MappingName = "M",
        Status = RecordStatus.Delivered,
        SourceFingerprint = fingerprint,
        MetadataHash = meta,
        PayloadHash = payload,
        RenderContext = context,
        TargetVersion = 5,
    };

    [Fact]
    public void Tier1_skips_only_when_fingerprint_payload_and_context_all_match()
    {
        var change = new FlowChange();
        Assert.True(ChangeDetector.CanSkipWithoutRender(Delivered(), "f1", "p1", "c1", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(Delivered(), "f2", "p1", "c1", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(Delivered(), "f1", "p2", "c1", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(Delivered(), "f1", "p1", "c2", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(null, "f1", "p1", "c1", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(Delivered() with { Status = RecordStatus.Held }, "f1", "p1", "c1", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(Delivered(), "f1", "p1", "c1", new FlowChange { OnUnchanged = UnchangedAction.Deliver }));
    }

    [Fact]
    public void Tier2_decides_metadata_and_payload_independently()
    {
        var change = new FlowChange();
        Assert.Equal(PlannedAction.Create, ChangeDetector.Decide(null, "m1", "p1", true, change).Action);
        Assert.Equal(PlannedAction.Skip, ChangeDetector.Decide(Delivered(), "m1", "p1", true, change).Action);
        Assert.Equal(PlannedAction.UpdateMetadata, ChangeDetector.Decide(Delivered(), "m2", "p1", true, change).Action);
        Assert.Equal(PlannedAction.UpdatePayload, ChangeDetector.Decide(Delivered(), "m1", "p2", true, change).Action);
        Assert.Equal(PlannedAction.UpdateBoth, ChangeDetector.Decide(Delivered(), "m2", "p2", true, change).Action);
        Assert.Equal(PlannedAction.Skip, ChangeDetector.Decide(Delivered(), "m1", "p2", false, change).Action);
        Assert.Equal(PlannedAction.UpdateBoth, ChangeDetector.Decide(Delivered(), "m1", "p1", true, new FlowChange { OnUnchanged = UnchangedAction.Deliver }).Action);
    }
}

public class DropManifestTests
{
    private const string Valid = """
        {
          "manifestVersion": 1,
          "submissionId": "7d5a2d4c-3f0e-4b6b-9c1a-0d2e8f7a6b51",
          "flow": "recall-welllog",
          "mapping": "WellLog@1.4.0",
          "recordCount": 1,
          "scopes": { "record": { "files": ["metadata/a.parquet"], "columns": [ { "name": "deliveryKey" } ] } },
          "payloads": { "curves": { "pathTemplate": "curves/{deliveryKey}/chunk_*.parquet", "hashColumn": "payloadHash" } }
        }
        """;

    [Fact]
    public void Parses_and_round_trips()
    {
        var manifest = DropManifest.Parse(Valid, "m");
        Assert.Equal("recall-welllog", manifest.Flow);
        Assert.Equal(new Guid("7d5a2d4c-3f0e-4b6b-9c1a-0d2e8f7a6b51"), manifest.SubmissionId);
        var again = DropManifest.Parse(manifest.ToJson(), "m");
        Assert.Equal(manifest.Payloads["curves"].PathTemplate, again.Payloads["curves"].PathTemplate);
        Assert.Contains("deliveryKey", manifest.DeclaredColumns()["record"]);
    }

    [Fact]
    public void Unknown_keys_and_missing_root_scope_are_errors()
    {
        Assert.Throws<FlowValidationException>(() => DropManifest.Parse(Valid.Replace("\"recordCount\"", "\"recordcount\"", StringComparison.Ordinal), "m"));
        Assert.Throws<FlowValidationException>(() => DropManifest.Parse(Valid.Replace("\"record\":", "\"records\":", StringComparison.Ordinal), "m"));
        Assert.Throws<FlowValidationException>(() => DropManifest.Parse(Valid.Replace("WellLog@1.4.0", "WellLog", StringComparison.Ordinal), "m"));
        Assert.Throws<FlowValidationException>(() => DropManifest.Parse(Valid.Replace("curves/{deliveryKey}/chunk_*.parquet", "curves/chunk_*.parquet", StringComparison.Ordinal), "m"));
    }
}

public class JsonPathAndDiffTests
{
    [Fact]
    public void Reads_paths_indices_and_wildcards()
    {
        var root = JsonDocument.Parse("""{ "recordIdVersions": ["a:b:12", "c:d:13"], "data": { "token": "t" }, "items": [ { "id": 1 }, { "id": 2 } ] }""").RootElement;
        Assert.Equal("a:b:12", JsonPathReader.SelectValue(root, "recordIdVersions[0]"));
        Assert.Equal("t", JsonPathReader.SelectValue(root, "$.data.token"));
        Assert.Equal(["1", "2"], JsonPathReader.SelectValues(root, "items[*].id"));
        Assert.Null(JsonPathReader.SelectValue(root, "nope.x"));
        Assert.Equal(2, JsonPathReader.CountRecords(root, null));
    }

    [Fact]
    public void Diff_reports_added_removed_and_changed_leaves()
    {
        var diff = DocumentDiff.Compute(JsonNode.Parse("""{"a":1,"b":[1,2],"c":"x"}"""), JsonNode.Parse("""{"a":2,"b":[1],"d":true}"""));
        Assert.Contains("~ a: 1 -> 2", diff, StringComparison.Ordinal);
        Assert.Contains("- b[1]: 2", diff, StringComparison.Ordinal);
        Assert.Contains("- c: \"x\"", diff, StringComparison.Ordinal);
        Assert.Contains("+ d: true", diff, StringComparison.Ordinal);
        Assert.Equal("(no differences)", DocumentDiff.Compute(JsonNode.Parse("{\"a\":1}"), JsonNode.Parse("{\"a\":1.0}")));
    }
}

public class RenderContextTests
{
    [Fact]
    public void Canonical_form_round_trips_and_orders_parameters()
    {
        var context = new RenderContext
        {
            MappingReference = "M@1",
            ReferenceSnapshotVersion = "r",
            SchemaSnapshotVersion = "s",
            Parameters = new Dictionary<string, string> { ["z"] = "1", ["a"] = "2", ["dataPartition"] = "dev" },
        };
        var canonical = context.Canonical();
        Assert.StartsWith("{\"mapping\":\"M@1\",\"parameters\":{\"a\":\"2\",\"dataPartition\":\"dev\",\"z\":\"1\"}", canonical, StringComparison.Ordinal);
        Assert.Equal(canonical, RenderContext.Parse(canonical).Canonical());
        Assert.Equal("dev", RenderContext.Parse(canonical).DataPartition);
    }
}
