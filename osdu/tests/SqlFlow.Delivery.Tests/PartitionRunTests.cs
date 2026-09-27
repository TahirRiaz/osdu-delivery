using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a run carries the partition it targets and the configuration of each partition (docs/partitions-design.md sections
/// 3 and 5): the partition as a run value the flow's own parameters never see, the central configuration in two layers with
/// a partition's own values first, the mapping's dataPartition as the bound partition, and a cache run's payload holding the
/// configuration and nothing else.
/// </summary>
public sealed class PartitionRunTests
{
    private static readonly IReadOnlyDictionary<string, string> Base = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["OSDU_URL"] = "https://osdu.example.com",
        ["OSDU_LEGAL_TAG"] = "shared-legal",
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Partitions =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["test"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["OSDU_LEGAL_TAG"] = "test-legal", ["OSDU_ACL_OWNER"] = "owners@test" },
            ["prod"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["OSDU_URL"] = "https://prod.osdu.example.com" },
        };

    [Fact]
    public void A_payload_carries_each_partition_s_configuration_and_reads_it_back()
    {
        var payload = new DeliveryRunPayload { References = Base, PartitionReferences = Partitions };

        var json = payload.ToJson();
        var read = DeliveryRunPayload.Parse(json);

        Assert.Equal(json, read.ToJson());
        Assert.True(read.CarriesOnlyConfiguration);
        Assert.Equal(["prod", "test"], read.PartitionReferences.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("owners@test", read.PartitionReferences["TEST"]["OSDU_ACL_OWNER"]);
    }

    [Fact]
    public void A_run_resolves_with_its_partition_s_values_over_the_ones_set_for_no_partition()
    {
        var payload = new DeliveryRunPayload { References = Base, PartitionReferences = Partitions };

        var test = payload.ReferencesFor("test");
        Assert.Equal("test-legal", test["OSDU_LEGAL_TAG"]);
        Assert.Equal("owners@test", test["OSDU_ACL_OWNER"]);
        Assert.Equal("https://osdu.example.com", test["OSDU_URL"]);

        Assert.Equal("https://prod.osdu.example.com", payload.ReferencesFor("PROD")["OSDU_URL"]);
        Assert.Same(Base, payload.ReferencesFor("dev"));
        Assert.Same(Base, payload.ReferencesFor(null));
    }

    [Theory]
    [InlineData("""{"partitionReferences":{"dev partition":{"OSDU_URL":"x"}}}""", "not a data-partition-id")]
    [InlineData("""{"partitionReferences":{"dev":{"OSDU_URL":"x"},"DEV":{"OSDU_URL":"y"}}}""", "more than once")]
    [InlineData("""{"partitionReferences":{"dev":{"not a name":"x"}}}""", "partitionReferences.dev property 'not a name'")]
    [InlineData("""{"partitionReferences":["dev"]}""", "must be a JSON object")]
    public void A_payload_whose_partition_configuration_is_malformed_is_refused(string json, string expected)
    {
        var ex = Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse(json));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_partition_a_run_targets_never_reaches_the_flow_s_parameters()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["partition"] = " test ", ["logSource"] = "STAT_COMP" };

        var (partition, rest) = PartitionNames.SplitRunValues(values, keptAsParameter: false);

        Assert.Equal("test", partition);
        Assert.Equal(["logSource"], rest.Keys);

        var (none, same) = PartitionNames.SplitRunValues(new Dictionary<string, string> { ["logSource"] = "STAT_COMP" }, keptAsParameter: false);
        Assert.Null(none);
        Assert.Equal(["logSource"], same.Keys);

        // A flow written before partitions existed, naming no partitions and declaring a parameter of that name, keeps it.
        var (kept, own) = PartitionNames.SplitRunValues(values, keptAsParameter: true);
        Assert.Null(kept);
        Assert.Same(values, own);
    }

    [Fact]
    public void The_kind_supplies_the_bound_partition_as_data_partition_to_a_mapping_that_declares_it()
    {
        var none = new Dictionary<string, string>(StringComparer.Ordinal);

        var bound = DeliveryDestination.Supplied(none, ["dataPartition", "legalTag"], "test");
        Assert.Equal("test", bound["dataPartition"]);
        Assert.Equal("${env:OSDU_LEGAL_TAG}", bound["legalTag"]);

        // A mapping that does not declare it is given nothing more, so its render context, and its records, do not move.
        Assert.DoesNotContain("dataPartition", DeliveryDestination.Supplied(none, ["legalTag"], "test").Keys);
        Assert.Equal("${env:OSDU_DATA_PARTITION}", DeliveryDestination.Supplied(none, ["dataPartition"])["dataPartition"]);
    }

    [Fact]
    public void A_cache_run_takes_the_configuration_and_its_partition_and_nothing_else()
    {
        var kind = new CacheFlowKind(new DeliveryDocumentLoader());
        var configured = new RunParameters
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["partition"] = "test" },
            Payload = new DeliveryRunPayload { References = Base, PartitionReferences = Partitions }.ToJson(),
        };

        kind.ValidateParameters(configured);

        var ex = Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { Payload = """{"submissionId":"0d1e6f3c-9a4b-4c1e-8e7a-2b3c4d5e6f70"}""" }));
        Assert.Contains("carries only the central configuration", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_central_configuration_is_read_in_two_layers_and_flattened_for_one_partition()
    {
        var configuration = new DeliveryConfiguration(Base, Partitions);

        Assert.False(configuration.IsEmpty);
        Assert.True(DeliveryConfiguration.None.IsEmpty);
        Assert.Equal("test-legal", configuration.For("Test")["OSDU_LEGAL_TAG"]);
        Assert.Equal("shared-legal", configuration.For("dev")["OSDU_LEGAL_TAG"]);
        Assert.Same(Base, configuration.For(null));
    }
}
