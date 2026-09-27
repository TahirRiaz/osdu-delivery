using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The partitions a flow names (docs/partitions-design.md sections 2 to 4): how a delivery flow and a cache flow declare
/// them, what the loader refuses beside them, how a definition is bound to the one partition a run targets, and the ledger
/// identity each partition keeps, with a flow that names none left exactly as it was.
/// </summary>
public sealed class PartitionDocumentsTests
{
    private readonly DeliveryDocumentLoader _loader = new();

    /// <summary>A delivery flow in the single form naming <paramref name="partitions"/> (a YAML value), with no partition header.</summary>
    private static string Delivery(string partitions, string headers = "{ }", string parameters = "{ region: north }", string name = "recall-welllog") => $$"""
        flowType: delivery
        name: {{name}}
        partitions: {{partitions}}
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.arc.WellLog
            key: [log_id]
          lastModified: update_date
          work: work/welllog
        render:
          mapping: WellLog@1.4.0
          parameters: {{parameters}}
        target:
          endpoint: ${env:OSDU_URL}
          headers: {{headers}}
          protocol: storage
        """.ReplaceLineEndings("\n");

    /// <summary>A source with two interfaces naming <paramref name="partitions"/>.</summary>
    private static string Source(string partitions) => $$"""
        flowType: delivery
        name: petrel
        partitions: {{partitions}}
        source:
          connection: ${env:PETREL_DB}
          lastModified: update_date
          work: work/petrel
        target:
          endpoint: ${env:OSDU_URL}
        interfaces:
          wells:
            record: { object: Petrel.ing.Well, key: [uwi] }
            mapping: Well@1.2.0
          wellbores:
            record: { object: Petrel.ing.Wellbore, key: [uwi] }
            mapping: Wellbore@1.1.0
            ledger: petrel-wellbores
        """.ReplaceLineEndings("\n");

    /// <summary>A cache flow naming <paramref name="partitions"/> over the given <c>types:</c> block (items indented two spaces).</summary>
    private static string Cache(string partitions, string types, string headers = "{ }") => $$"""
        flowType: cache
        name: recall-lookups-00-cache
        partitions: {{partitions}}
        source:
          connection: ${env:OSDU_DATA_DB}
          headers: {{headers}}
        types:
        {{types}}
        """.ReplaceLineEndings("\n");

    private const string Lookups = """
          - table: OsduData.arc.CacheCurveDictionary
            name: CurveDictionary
            key: mnemonic
            fields: [log_curve_type_id]
          - table: OsduData.arc.CacheRecallUnits
            name: RecallUnits
            key: source_unit
            fields: [osdu_unit]
        """;

    [Fact]
    public void A_delivery_flow_names_its_partitions_as_names_or_as_a_name_with_settings()
    {
        var flow = _loader.ParseFlow(Delivery("[{ name: dev, keepLedger: true }, test, prod]"), "flows/welllog.yaml");

        Assert.True(flow.DeclaresPartitions);
        Assert.Equal(["dev", "test", "prod"], flow.Partitions.Select(p => p.Name));
        Assert.Equal([true, false, false], flow.Partitions.Select(p => p.KeepsLedger));
        Assert.Null(flow.Partition);
        Assert.True(flow.IsUnbound);
    }

    [Fact]
    public void A_flow_that_names_no_partitions_reads_and_keeps_its_ledger_exactly_as_before()
    {
        var yaml = Delivery("[dev]", headers: "{ data-partition-id: dev }")
            .Replace("partitions: [dev]\n", string.Empty, StringComparison.Ordinal);
        var flow = _loader.ParseFlow(yaml, "flows/welllog.yaml");

        Assert.False(flow.DeclaresPartitions);
        Assert.False(flow.IsUnbound);
        Assert.Equal("recall-welllog", flow.LedgerName);
        Assert.Equal(FlowId.Of("recall-welllog"), flow.Id);
        Assert.Empty(flow.Partitions);
        Assert.Equal("dev", flow.Target.Headers["data-partition-id"]);
    }

    [Fact]
    public void Binding_sets_the_header_and_the_ledger_of_the_partition_a_run_targets()
    {
        var flow = _loader.ParseFlow(Delivery("[{ name: dev, keepLedger: true }, test]"), "flows/welllog.yaml");

        var dev = flow.ForPartition("dev");
        Assert.Equal("dev", dev.Partition);
        Assert.Equal("dev", dev.Target.Headers["data-partition-id"]);
        Assert.True(dev.KeepsOwnLedger);
        Assert.Equal("recall-welllog", dev.LedgerName);
        Assert.Equal(FlowId.Of("recall-welllog"), dev.Id);

        var test = flow.ForPartition("TEST");
        Assert.Equal("test", test.Partition);
        Assert.Equal("test", test.Target.Headers["data-partition-id"]);
        Assert.False(test.KeepsOwnLedger);
        Assert.Equal("recall-welllog@test", test.LedgerName);
        Assert.Equal(FlowId.Of("recall-welllog", "test"), test.Id);
        Assert.NotEqual(dev.Id, test.Id);
    }

    [Fact]
    public void An_unbound_flow_that_names_its_partitions_refuses_to_name_a_ledger()
    {
        var flow = _loader.ParseFlow(Delivery("[dev, test]"), "flows/welllog.yaml");

        var ex = Assert.Throws<InvalidOperationException>(() => flow.Id);
        Assert.Contains("dev, test", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => flow.LedgerName);
    }

    [Fact]
    public void A_partition_keeps_a_ledger_id_no_flow_name_can_derive()
    {
        Assert.Equal(FlowId.Of("recall-welllog", "test"), FlowId.Of(" Recall-WellLog ", "TEST"));
        Assert.NotEqual(FlowId.Of("recall-welllog@test"), FlowId.Of("recall-welllog", "test"));
        Assert.NotEqual(FlowId.Of("recall-welllog", "test"), FlowId.Of("recall-welllog", "dev"));
    }

    [Fact]
    public void Binding_refuses_a_partition_the_flow_does_not_name()
    {
        var flow = _loader.ParseFlow(Delivery("[dev, test]"), "flows/welllog.yaml");

        var ex = Assert.Throws<DeliveryException>(() => flow.ForPartition("prod"));
        Assert.Contains("'prod'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dev, test", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_is_bound_to_the_partition_a_run_names_or_to_its_only_one()
    {
        var one = _loader.ParseSource(Delivery("[dev]"), "flows/welllog.yaml");
        Assert.Equal("dev", one.ForPartition(null).Partition);

        var several = _loader.ParseSource(Delivery("[dev, test]"), "flows/welllog.yaml");
        var ex = Assert.Throws<DeliveryException>(() => several.ForPartition(null));
        Assert.Contains("dev, test", ex.Message, StringComparison.Ordinal);
        Assert.Equal("test", several.ForPartition(" test ").Partition);

        var none = _loader.ParseSource(
            Delivery("[dev]", headers: "{ data-partition-id: dev }").Replace("partitions: [dev]\n", string.Empty, StringComparison.Ordinal),
            "flows/welllog.yaml");
        Assert.Same(none, none.ForPartition(null));
        Assert.Throws<DeliveryException>(() => none.ForPartition("dev"));
    }

    [Fact]
    public void Every_interface_of_a_source_serves_the_source_s_partitions_with_a_ledger_per_partition()
    {
        var source = _loader.ParseSource(Source("[{ name: dev, keepLedger: true }, test]"), "flows/petrel.yaml");

        Assert.All(source.Interfaces, i => Assert.Equal(["dev", "test"], i.Partitions.Select(p => p.Name)));

        var test = source.ForPartition("test");
        Assert.Equal(["petrel/wells@test", "petrel-wellbores@test"], test.Interfaces.Select(i => i.LedgerName));
        Assert.All(test.Interfaces, i => Assert.Equal("test", i.Target.Headers["data-partition-id"]));

        var dev = source.ForPartition("dev");
        Assert.Equal(["petrel/wells", "petrel-wellbores"], dev.Interfaces.Select(i => i.LedgerName));

        var ledgers = source.EveryLedger().ToList();
        Assert.Equal(4, ledgers.Count);
        Assert.Equal(4, ledgers.Select(l => l.Id).Distinct().Count());
    }

    [Fact]
    public void An_unbound_source_finds_an_interface_by_the_ledger_of_any_of_its_partitions()
    {
        var source = _loader.ParseSource(Source("[{ name: dev, keepLedger: true }, test]"), "flows/petrel.yaml");
        var wellsInTest = FlowId.Of("petrel/wells", "test");

        var found = source.ByFlowId(wellsInTest);

        Assert.NotNull(found);
        Assert.Equal("wells", found.Interface);
        Assert.Equal("test", found.Partition);
        Assert.Equal("dev", source.ByFlowId(FlowId.Of("petrel/wells"))!.Partition);
        Assert.Null(source.ByFlowId(FlowId.Of("petrel/wells", "prod")));
    }

    [Theory]
    [InlineData("{ data-partition-id: dev }", "{ region: north }", "target.headers names 'data-partition-id'")]
    [InlineData("{ }", "{ dataPartition: dev }", "sets 'dataPartition'")]
    public void A_flow_that_names_its_partitions_leaves_the_header_and_the_data_partition_to_the_run(string headers, string parameters, string expected)
    {
        var ex = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Delivery("[dev]", headers, parameters), "flows/welllog.yaml"));

        Assert.StartsWith("flows/welllog.yaml:", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]", "names no partition")]
    [InlineData("['${env:OSDU_DATA_PARTITION}']", "is the reference")]
    [InlineData("['dev partition']", "is not a data-partition-id")]
    [InlineData("[dev, DEV]", "more than once")]
    [InlineData("[1.5]", "write the partition name in quotes")]
    [InlineData("[true]", "write the partition name in quotes")]
    [InlineData("[{ name: dev, keepLedger: true }, { name: test, keepLedger: true }]", "marks dev, test with keepLedger")]
    [InlineData("[{ name: dev, keep: true }]", "has no 'keep' setting")]
    [InlineData("[{ keepLedger: true }]", "is empty")]
    [InlineData("[{ name: dev, keepLedger: maybe }]", "keepLedger must be true or false")]
    public void A_partition_list_that_names_no_valid_partition_once_is_refused(string partitions, string expected)
    {
        var ex = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Delivery(partitions), "flows/welllog.yaml"));

        Assert.StartsWith("flows/welllog.yaml:", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_partition_written_as_a_whole_number_is_a_name()
    {
        var flow = _loader.ParseFlow(Delivery("[2024]"), "flows/welllog.yaml");

        Assert.Equal("2024", flow.Partitions.Single().Name);
    }

    [Fact]
    public void A_cache_version_pin_is_refused_on_a_flow_of_several_partitions_and_kept_on_a_flow_of_one()
    {
        var several = Delivery("[dev, test]").Replace("  mapping: WellLog@1.4.0\n", "  mapping: WellLog@1.4.0\n  cacheVersion: 20260908T212727Z\n", StringComparison.Ordinal);
        var ex = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(several, "flows/welllog.yaml"));

        Assert.Contains("render.cacheVersion pins version 20260908T212727Z", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the flow names 2 partitions", ex.Message, StringComparison.Ordinal);

        var one = Delivery("[dev]").Replace("  mapping: WellLog@1.4.0\n", "  mapping: WellLog@1.4.0\n  cacheVersion: 20260908T212727Z\n", StringComparison.Ordinal);
        Assert.Equal("20260908T212727Z", _loader.ParseFlow(one, "flows/welllog.yaml").Render.CacheVersion);
    }

    [Fact]
    public void A_partition_ledger_name_that_would_not_fit_the_ledger_is_refused()
    {
        var name = new string('w', FlowDefinition.MaxLedgerNameLength - 2);
        var ex = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Delivery("[dev, test]", name: name), "flows/welllog.yaml"));

        Assert.Contains("in partition 'dev'", ex.Message, StringComparison.Ordinal);

        // Kept under the flow's own name, the partition that keeps the ledger adds nothing to it.
        var kept = _loader.ParseFlow(Delivery("[{ name: dev, keepLedger: true }]", name: name), "flows/welllog.yaml");
        Assert.Equal(name, kept.ForPartition("dev").LedgerName);
    }

    [Fact]
    public void A_cache_flow_names_its_partitions_and_builds_every_type_for_each_unless_a_type_narrows_them()
    {
        var cache = _loader.ParseCache(Cache("[dev, test, prod]", Lookups + "\n" + """
              - table: OsduData.arc.CacheProdUnits
                name: ProdUnits
                key: source_unit
                fields: [osdu_unit]
                partitions: [PROD]
            """), "cache/lookups.yaml");

        Assert.Equal(["dev", "test", "prod"], cache.Partitions);
        Assert.Empty(cache.Types[0].Partitions);
        Assert.Equal(["prod"], cache.Types[2].Partitions);

        var dev = cache.ForPartition("dev");
        Assert.Equal("dev", dev.Scope);
        Assert.Equal("dev", dev.Source.Headers["data-partition-id"]);
        Assert.Equal(["CurveDictionary", "RecallUnits"], dev.Types.Select(t => t.Name));

        var prod = cache.ForPartition("prod");
        Assert.Equal(["CurveDictionary", "RecallUnits", "ProdUnits"], prod.Types.Select(t => t.Name));
    }

    [Fact]
    public void A_cache_flow_that_names_its_partitions_refuses_to_name_a_scope_until_it_is_bound()
    {
        var cache = _loader.ParseCache(Cache("[dev, test]", Lookups), "cache/lookups.yaml");

        var ex = Assert.Throws<InvalidOperationException>(() => cache.Scope);
        Assert.Contains("dev, test", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cache_run_refreshes_the_partition_it_names_or_every_partition_in_turn()
    {
        var cache = _loader.ParseCache(Cache("[dev, test]", Lookups), "cache/lookups.yaml");

        Assert.Equal(["dev", "test"], cache.ForRun(null).Select(c => c.Scope));
        Assert.Equal(["test"], cache.ForRun("Test").Select(c => c.Scope));
        Assert.Throws<DeliveryException>(() => cache.ForRun("prod"));

        var single = _loader.ParseCache(
            Cache("[dev]", Lookups, headers: "{ data-partition-id: dev }").Replace("partitions: [dev]\n", string.Empty, StringComparison.Ordinal),
            "cache/lookups.yaml");
        Assert.Equal("dev", single.Scope);
        Assert.Same(single, single.ForRun(null).Single());
        Assert.Throws<DeliveryException>(() => single.ForRun("dev"));
    }

    [Fact]
    public void Two_declarations_may_share_a_type_name_when_their_partitions_do_not_overlap()
    {
        var cache = _loader.ParseCache(Cache("[dev, prod]", """
              - table: OsduData.arc.CacheCurveDictionary
                name: CurveDictionary
                key: mnemonic
                fields: [log_curve_type_id]
                partitions: [dev]
              - table: OsduData.arc.CacheCurveDictionaryProd
                name: CurveDictionary
                key: mnemonic
                fields: [log_curve_type_id]
                partitions: [prod]
            """), "cache/lookups.yaml");

        Assert.Equal("OsduData.arc.CacheCurveDictionary", cache.ForPartition("dev").Types.Single().Table);
        Assert.Equal("OsduData.arc.CacheCurveDictionaryProd", cache.ForPartition("prod").Types.Single().Table);
    }

    [Theory]
    [InlineData("[dev]", "{ data-partition-id: dev }", "", "source.headers names 'data-partition-id'")]
    [InlineData("[dev, test]", "{ }", "    partitions: [staging]\n", "'staging' is not a partition of the flow")]
    [InlineData("[dev, test]", "{ }", "    partitions: []\n", "names no partition")]
    public void A_cache_flow_s_partitions_are_refused_where_they_disagree(string partitions, string headers, string typePartitions, string expected)
    {
        var types = Lookups.TrimEnd('\n') + "\n" + typePartitions;
        var ex = Assert.Throws<FlowValidationException>(() => _loader.ParseCache(Cache(partitions, types, headers), "cache/lookups.yaml"));

        Assert.StartsWith("cache/lookups.yaml:", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_that_narrows_the_partitions_of_a_flow_that_names_none_is_refused()
    {
        var yaml = Cache("[dev]", Lookups.TrimEnd('\n') + "\n    partitions: [dev]\n", headers: "{ data-partition-id: dev }")
            .Replace("partitions: [dev]\nsource", "source", StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => _loader.ParseCache(yaml, "cache/lookups.yaml"));

        Assert.Contains("the flow names none", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_declarations_of_one_name_for_one_partition_and_a_partition_with_no_type_are_refused()
    {
        var overlap = Assert.Throws<FlowValidationException>(() => _loader.ParseCache(Cache("[dev, prod]", """
              - table: OsduData.arc.CacheCurveDictionary
                name: CurveDictionary
                key: mnemonic
                fields: [log_curve_type_id]
              - table: OsduData.arc.CacheCurveDictionaryProd
                name: CurveDictionary
                key: mnemonic
                fields: [log_curve_type_id]
                partitions: [prod]
            """), "cache/lookups.yaml"));
        Assert.Contains("CurveDictionary more than once for partition 'prod'", overlap.Message, StringComparison.Ordinal);

        var empty = Assert.Throws<FlowValidationException>(() => _loader.ParseCache(Cache("[dev, prod]", """
              - table: OsduData.arc.CacheCurveDictionary
                name: CurveDictionary
                key: mnemonic
                fields: [log_curve_type_id]
                partitions: [dev]
            """), "cache/lookups.yaml"));
        Assert.Contains("no type is built for partition 'prod'", empty.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_that_names_neither_a_partition_header_nor_partitions_is_told_it_may_name_either()
    {
        var yaml = Cache("[dev]", Lookups).Replace("partitions: [dev]\n", string.Empty, StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => _loader.ParseCache(yaml, "cache/lookups.yaml"));

        Assert.Contains($"'{CacheScope.PartitionHeader}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'partitions'", ex.Message, StringComparison.Ordinal);
    }
}
