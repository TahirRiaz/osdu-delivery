using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Deliveries keep a type current (osdu/docs/reference/concepts/partition-cache.md): a cache type of OSDU records declares
/// <c>keepCurrent: true</c>, which a lookup table may not; the partition's declaration names the types a kind keeps current,
/// with every path the cache keeps for them; a delivery's records are merged as a capture that adds and takes nothing away,
/// under a holder named after its ledger; and a cache run may name the types it refreshes.
/// </summary>
public sealed class CacheKeepCurrentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private const string Kept = """
        flowType: cache
        name: welldb-reference-cache
        source:
          endpoint: ${env:OSDU_URL}
          headers:
            data-partition-id: dev
        types:
          - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
            fields: [data.Code]
            keepCurrent: true
          - kind: "osdu:wks:reference-data--UnitQuantity:*"
            fields: [data.Code]
        """;

    [Fact]
    public void A_type_of_OSDU_records_may_be_kept_current_by_the_deliveries_and_one_without_the_key_is_not()
    {
        var flow = new DeliveryDocumentLoader().ParseCache(Kept);

        Assert.True(flow.Types.Single(t => t.Name == "UnitOfMeasure").KeepCurrent);
        Assert.False(flow.Types.Single(t => t.Name == "UnitQuantity").KeepCurrent);
    }

    [Theory]
    [InlineData("dictionary")]
    [InlineData("table")]
    public void A_lookup_table_cannot_be_kept_current(string origin)
    {
        var type = origin == "table"
            ? """
                  - name: UnitAlias
                    table: OsduData.silver.UnitAlias
                    key: alias
                    fields: [unit]
                    keepCurrent: true
                """
            : """
                  - name: Units
                    dictionary: units
                    keepCurrent: true
                """;
        var yaml = $$"""
            flowType: cache
            name: welldb-lookups
            source:
              connection: ${env:OSDU_DATA_DB}
            types:
            {{type.TrimEnd()}}
            """;

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseCache(yaml));
        Assert.Contains("keepCurrent is for a type of OSDU records", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_narrowed_by_a_query_cannot_be_kept_current()
    {
        const string yaml = """
            flowType: cache
            name: welldb-reference-cache
            source:
              endpoint: ${env:OSDU_URL}
              headers:
                data-partition-id: dev
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                fields: [data.Code]
                query: "data.Source:Energistics"
                keepCurrent: true
            """;

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseCache(yaml));
        Assert.Contains("types[0].keepCurrent adds every record a delivery of the kind lands", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_partition_names_the_types_a_kind_keeps_current_with_every_path_any_flow_keeps_for_them()
    {
        var declaration = new CacheDeclaration("dev",
        [
            Declared("reference-cache", "UnitOfMeasure", "osdu:wks:reference-data--UnitOfMeasure:*", ["data.Code"], keepCurrent: true),
            Declared("other-cache", "UnitOfMeasure", "osdu:wks:reference-data--UnitOfMeasure:1.*", ["data.Name"]),
            Declared("reference-cache", "UnitQuantity", "osdu:wks:reference-data--UnitQuantity:*", ["data.Code"]),
            new CacheTypeDeclaration("lookups", "UnitAlias", "lookup--UnitAlias", null, "*", [new ReferenceFieldSpec("unit", "unit")], CacheChangeMode.Auto, CacheOrigin.Table, "alias", KeepCurrent: true),
        ]);

        var kept = Assert.Single(declaration.KeptCurrentBy("osdu:wks:reference-data--UnitOfMeasure:1.0.0"));
        Assert.Equal(("UnitOfMeasure", "reference-data--UnitOfMeasure"), (kept.TypeName, kept.EntityType));
        Assert.Equal(["Name", "Code"], kept.Fields.Select(f => f.Name));

        // A kind no kept pattern matches, and a type no flow keeps current, keep nothing current.
        Assert.Empty(declaration.KeptCurrentBy("osdu:wks:reference-data--UnitQuantity:1.0.0"));
        Assert.Empty(declaration.KeptCurrentBy("osdu:wks:master-data--Well:1.0.0"));
    }

    [Fact]
    public void A_delivery_holds_its_records_under_its_ledger_s_name_and_a_long_name_stays_distinct()
    {
        Assert.Equal("delivery:welldb-unit-delivery", CacheHolders.Delivery("welldb-unit-delivery"));
        Assert.True(CacheHolders.IsDelivery(CacheHolders.Delivery("welldb-unit-delivery")));
        Assert.False(CacheHolders.IsDelivery("welldb-reference-cache"));

        var a = CacheHolders.Delivery(new string('a', 250) + "1");
        var b = CacheHolders.Delivery(new string('a', 250) + "2");
        Assert.Equal(CacheHolders.MaxLength, a.Length);
        Assert.NotEqual(a, b);
        Assert.True(CacheHolders.IsDelivery(a));
    }

    [Fact]
    public void A_capture_that_adds_replaces_and_adds_records_and_takes_none_away()
    {
        var current = new ReferenceSnapshot("v1", T0, [Type("UnitOfMeasure", ("m", "metre"), ("ft", "foot"))], []);
        var members = new Dictionary<CacheMemberKey, IReadOnlySet<string>>(CacheMemberKey.Comparer);

        var added = CacheMerge.Apply(current, members, "delivery:welldb-unit-delivery", [Type("UnitOfMeasure", ("ft", "international foot"), ("s", "second"))], [], "v2", T0.AddHours(1), adds: true);
        Assert.Equal(
            ["ft=international foot", "m=metre", "s=second"],
            added.Snapshot.Type("UnitOfMeasure")!.Items.Select(i => $"{i.Id}={i.Fields["Code"].Text}").Order(StringComparer.Ordinal));

        // The same capture as a cache flow's takes out what it did not find and no other flow holds.
        var captured = CacheMerge.Apply(current, members, "reference-cache", [Type("UnitOfMeasure", ("ft", "international foot"), ("s", "second"))], [], "v2", T0.AddHours(1));
        Assert.Equal(["ft", "s"], captured.Snapshot.Type("UnitOfMeasure")!.Items.Select(i => i.Id).Order(StringComparer.Ordinal));

        // A record a delivery holds stays when a cache flow's capture does not find it.
        members[new CacheMemberKey("UnitOfMeasure", "m")] = new HashSet<string>(StringComparer.Ordinal) { "delivery:welldb-unit-delivery" };
        var kept = CacheMerge.Apply(current, members, "reference-cache", [Type("UnitOfMeasure", ("ft", "foot"))], [], "v2", T0.AddHours(1));
        Assert.Equal(["ft", "m"], kept.Snapshot.Type("UnitOfMeasure")!.Items.Select(i => i.Id).Order(StringComparer.Ordinal));

        // A lookup table is captured whole by its cache flow; nothing adds rows to one.
        var lookup = new ReferenceType("UnitAlias", ReferenceType.LookupEntityType("UnitAlias"), [ReferenceItem.FromText("M", new Dictionary<string, string> { ["unit"] = "m" })], "alias");
        Assert.Throws<DeliveryException>(() => CacheMerge.Apply(current, members, "delivery:x", [lookup], [], "v2", T0, adds: true));
    }

    [Fact]
    public void A_run_may_name_the_types_it_refreshes_and_a_name_the_flow_does_not_declare_is_refused()
    {
        var flow = new DeliveryDocumentLoader().ParseCache(Kept);

        Assert.Same(flow, CacheRefresher.Narrow(flow, []));
        Assert.Equal(["UnitQuantity"], CacheRefresher.Narrow(flow, ["unitquantity"]).Types.Select(t => t.Name));
        var refused = Assert.Throws<SqlFlowException>(() => CacheRefresher.Narrow(flow, ["UnitQuantity", "Well"]));
        Assert.Contains("'Well'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("it declares UnitOfMeasure, UnitQuantity", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_types_a_run_names_are_a_cache_run_s_and_no_other_kind_s()
    {
        var payload = DeliveryRunPayload.Parse("""{ "types": ["UnitOfMeasure"] }""");
        Assert.Equal(["UnitOfMeasure"], payload.Types);
        payload.RefuseOtherThan("cache", [DeliveryRunPayload.TypesProperty]);

        var refused = Assert.Throws<SqlFlowException>(() => payload.RefuseOtherThan("dimension", [DeliveryRunPayload.DimensionsProperty]));
        Assert.Contains("only a cache flow's runs select types", refused.Message, StringComparison.Ordinal);
        Assert.Equal(["UnitOfMeasure"], DeliveryRunPayload.Parse(payload.ToJson()).Types);
    }

    [Fact]
    public void A_run_s_result_carries_what_its_cache_step_did_in_one_sentence()
    {
        static string Summary(CacheKeptOutcome kept)
            => JsonDocument.Parse(JsonSerializer.Serialize(kept, JsonSerializerOptions.Web)).RootElement.GetProperty("summary").GetString()!;

        Assert.Equal(
            "The cache of partition 'dev' (UnitOfMeasure) holds the 2 record(s) the run landed as version v2; storage did not return 1.",
            Summary(new CacheKeptOutcome("dev", ["UnitOfMeasure"], 3, 2, 1, "v2", true, null)));
        Assert.Equal(
            "The cache of partition 'dev' (UnitOfMeasure) already held the 2 record(s) the run landed, at version v1.",
            Summary(new CacheKeptOutcome("dev", ["UnitOfMeasure"], 2, 2, 0, "v1", false, null)));
        Assert.Equal(
            "The run landed no record the cache of partition 'dev' keeps current (UnitOfMeasure).",
            Summary(new CacheKeptOutcome("dev", ["UnitOfMeasure"], 0, 0, 0, null, false, null)));
        Assert.Equal(
            "The cache of partition 'dev' was not brought up to date: storage did not answer.",
            Summary(new CacheKeptOutcome("dev", [], 0, 0, 0, null, false, "storage did not answer.")));
    }

    private static CacheTypeDeclaration Declared(string flow, string name, string kind, string[] paths, bool keepCurrent = false)
        => new(flow, name, kind.Split(':')[2], kind, "*", [.. paths.Select(p => new ReferenceFieldSpec(p))], CacheChangeMode.Auto, KeepCurrent: keepCurrent);

    private static ReferenceType Type(string name, params (string Id, string Code)[] items)
        => new(name, "reference-data--" + name, items.Select(i => ReferenceItem.FromText(i.Id, new Dictionary<string, string> { ["Code"] = i.Code })));
}
