using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The central configuration: the properties the control plane supplies with a run, how they reach a node, and how a
/// node resolves them beside its own environment.
/// </summary>
public sealed class CentralConfigTests
{
    /// <summary>A resolver standing in for a node: it answers only what the node's environment holds.</summary>
    private sealed class NodeEnvironment(params (string Name, string Value)[] variables) : ISecretResolver
    {
        private readonly Dictionary<string, string> _held = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

        public string Resolve(string value)
        {
            foreach (var (name, held) in _held)
            {
                value = value.Replace("${env:" + name + "}", held, StringComparison.Ordinal);
            }

            if (value.Contains("${env:", StringComparison.Ordinal))
            {
                var at = value.IndexOf("${env:", StringComparison.Ordinal);
                var end = value.IndexOf('}', at);
                throw new SqlFlowException($"Environment variable '{value[(at + 6)..end]}' is not set.");
            }

            return value;
        }

        public Task<string> ResolveAsync(string value, CancellationToken ct = default) => Task.FromResult(Resolve(value));
    }

    [Fact]
    public void A_supplied_property_answers_before_the_node_and_the_node_answers_the_rest()
    {
        var node = new NodeEnvironment(("OSDU_DATA_PARTITION", "from-node"), ("OSDU_URL", "https://node.example.test"));
        var resolver = SuppliedReferenceResolver.For(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["OSDU_DATA_PARTITION"] = "from-control-plane" },
            node);

        Assert.Equal("from-control-plane", resolver.Resolve("${env:OSDU_DATA_PARTITION}"));

        // What the configuration does not name is still the node's to answer, so an estate can hold some values
        // centrally and leave the rest where they were.
        Assert.Equal("https://node.example.test", resolver.Resolve("${env:OSDU_URL}"));
    }

    [Fact]
    public void A_run_given_nothing_keeps_the_nodes_own_resolver()
    {
        var node = new NodeEnvironment(("OSDU_DATA_PARTITION", "from-node"));
        Assert.Same(node, SuppliedReferenceResolver.For(new Dictionary<string, string>(StringComparer.Ordinal), node));
    }

    [Fact]
    public void A_supplied_value_may_itself_be_a_reference_the_node_resolves()
    {
        // The control plane points the estate at a secret without ever holding it.
        var node = new NodeEnvironment(("ESTATE_PARTITION", "resolved-on-the-node"));
        var resolver = SuppliedReferenceResolver.For(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["OSDU_DATA_PARTITION"] = "${env:ESTATE_PARTITION}" },
            node);

        Assert.Equal("resolved-on-the-node", resolver.Resolve("${env:OSDU_DATA_PARTITION}"));
    }

    [Fact]
    public void A_property_whose_value_is_its_own_reference_is_left_to_the_node_rather_than_looping()
    {
        var node = new NodeEnvironment(("OSDU_DATA_PARTITION", "from-node"));
        var resolver = SuppliedReferenceResolver.For(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["OSDU_DATA_PARTITION"] = "${env:OSDU_DATA_PARTITION}" },
            node);

        Assert.Equal("from-node", resolver.Resolve("${env:OSDU_DATA_PARTITION}"));
    }

    [Fact]
    public void A_reference_the_configuration_does_not_name_still_fails_with_the_name_it_was_missing()
    {
        var resolver = SuppliedReferenceResolver.For(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["OSDU_DATA_PARTITION"] = "dev" },
            new NodeEnvironment());

        var missing = Assert.Throws<SqlFlowException>(() => resolver.Resolve("${env:OSDU_URL}"));
        Assert.Contains("OSDU_URL", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Properties_ride_a_run_payload_and_come_back_as_they_went()
    {
        var payload = new DeliveryRunPayload
        {
            References = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OSDU_LEGAL_TAG"] = "dev-default",
                ["OSDU_DATA_PARTITION"] = "dev",
            },
        };

        var json = payload.ToJson();
        Assert.NotNull(json);

        // Ordered by name, so the same configuration writes the same payload and two runs stay comparable.
        Assert.Contains("\"OSDU_DATA_PARTITION\":\"dev\",\"OSDU_LEGAL_TAG\":\"dev-default\"", json, StringComparison.Ordinal);
        Assert.Equal(payload.References, DeliveryRunPayload.Parse(json).References);
    }

    [Fact]
    public void A_payload_carrying_no_properties_is_still_empty()
        => Assert.Null(new DeliveryRunPayload().ToJson());

    [Theory]
    [InlineData("{\"references\":[]}", "must be a JSON object")]
    [InlineData("{\"references\":{\"1BAD\":\"x\"}}", "does not name a reference")]
    [InlineData("{\"references\":{\"OK\":\"\"}}", "non-empty string")]
    [InlineData("{\"references\":{\"OK\":7}}", "non-empty string")]
    public void A_payload_refuses_what_cannot_be_a_property_and_names_it(string json, string expected)
        => Assert.Contains(expected, Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse(json)).Message, StringComparison.Ordinal);

    [Fact]
    public void One_run_carries_no_more_properties_than_the_bound()
    {
        var many = string.Join(",", Enumerable.Range(0, DeliveryConfigNames.MaxPerRun + 1).Select(i => $"\"NAME_{i}\":\"v\""));
        var refused = Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse($"{{\"references\":{{{many}}}}}"));
        Assert.Contains($"at most {DeliveryConfigNames.MaxPerRun}", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OSDU_DATA_PARTITION", true)]
    [InlineData("_private", true)]
    [InlineData("1LEADING", false)]
    [InlineData("has-hyphen", false)]
    [InlineData("", false)]
    public void A_property_is_named_as_a_flow_spells_it(string name, bool named)
        => Assert.Equal(named, DeliveryConfigNames.IsName(name));

    [Fact]
    public void A_property_holds_no_control_character_so_it_stays_printable_in_a_log_line()
    {
        Assert.False(DeliveryConfigNames.IsValue("two\nlines"));
        Assert.False(DeliveryConfigNames.IsValue(new string('x', DeliveryConfigNames.MaxValueLength + 1)));
        Assert.True(DeliveryConfigNames.IsValue("${keyvault:estate/partition}"));
    }

    [Fact]
    public void The_kind_owns_where_a_record_goes_so_a_flow_need_not_say_it()
    {
        Assert.Equal(
            ["dataPartition", "aclOwner", "aclViewer", "legalTag"],
            DeliveryDestination.Parameters);

        Assert.Equal("${env:OSDU_DATA_PARTITION}", DeliveryDestination.ReferenceFor(DeliveryDestination.DataPartitionParameter));
        Assert.Equal("${env:OSDU_ACL_OWNER}", DeliveryDestination.ReferenceFor(DeliveryDestination.AclOwnerParameter));
        Assert.Equal("${env:OSDU_ACL_VIEWER}", DeliveryDestination.ReferenceFor(DeliveryDestination.AclViewerParameter));
        Assert.Equal("${env:OSDU_LEGAL_TAG}", DeliveryDestination.ReferenceFor(DeliveryDestination.LegalTagParameter));

        // A parameter the kind does not own is the flow's alone to fill, so nothing is invented for it.
        Assert.Null(DeliveryDestination.ReferenceFor("logSource"));
        Assert.False(DeliveryDestination.Owns("logSource"));
    }

    [Fact]
    public void A_flow_renders_with_what_it_supplies_and_the_kinds_reference_for_what_it_leaves_out()
    {
        var flow = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dataPartition"] = "dev",
            ["region"] = "south",
        };

        var supplied = DeliveryDestination.Supplied(flow, ["dataPartition", "legalTag", "logSource"]);

        // The flow's own value wins, and every value it supplies is kept whether or not it was asked for. The kind fills the
        // parameter it owns that the flow left out, and nothing is invented for one only the flow can fill.
        Assert.Equal(
            new Dictionary<string, string> { ["dataPartition"] = "dev", ["region"] = "south", ["legalTag"] = "${env:OSDU_LEGAL_TAG}" },
            supplied);

        // Asking for every parameter the kind owns, as the builder does before it knows the mapping, fills each one left out.
        Assert.Equal(
            ["aclOwner", "aclViewer", "dataPartition", "legalTag", "region"],
            DeliveryDestination.Supplied(flow, DeliveryDestination.Parameters).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_run_says_which_references_came_from_the_control_plane_and_which_from_the_node()
    {
        var sources = ReferenceSource.Of(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["OSDU_DATA_PARTITION"] = "dev" },
            ["OSDU_URL", "OSDU_DATA_PARTITION"]);

        Assert.Equal(
            [("OSDU_DATA_PARTITION", ReferenceOrigin.ControlPlane), ("OSDU_URL", ReferenceOrigin.Node)],
            sources.Select(s => (s.Name, s.Origin)));
    }
}
