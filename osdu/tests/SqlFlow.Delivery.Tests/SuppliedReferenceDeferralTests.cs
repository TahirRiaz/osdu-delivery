using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Engine;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A supplied property that is its own reference (<c>OSDU_URL = ${env:OSDU_URL}</c>) is not refused: it defers to the
/// node's environment, which is how a repository or a partition overrides a value the control plane sets for every one.
/// Properties do not chain, so two naming each other cannot loop, and where a reference got its value says the node for a
/// property that deferred to it. A node here is the engine's own resolver over an environment holding only the variables
/// a test names.
/// </summary>
public sealed class SuppliedReferenceDeferralTests
{
    private static SecretResolver NodeEnvironment(params (string Name, string Value)[] variables)
        => new([new HeldEnvironment(variables)]);

    private static ISecretResolver Supplied(IReadOnlyDictionary<string, string> supplied, ISecretResolver node)
        => new ScopedReferenceResolver(supplied, node);

    private sealed class HeldEnvironment((string Name, string Value)[] variables) : ISecretProvider
    {
        private readonly Dictionary<string, string> _held = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

        public string Scheme => "env";

        public string Resolve(string locator)
            => _held.TryGetValue(locator, out var value) ? value : throw new SqlFlowException($"Environment variable '{locator}' is not set.");
    }

    private static Dictionary<string, string> Properties(params (string Name, string Value)[] values)
        => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    [Fact]
    public async Task A_partition_that_sets_a_property_to_its_own_reference_takes_the_nodes_value_over_the_control_planes()
    {
        var configuration = new DeliveryConfiguration(
            Properties(("OSDU_URL", "https://central.example.test")),
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["partition-b"] = Properties(("OSDU_URL", "${env:OSDU_URL}")),
            });
        var node = NodeEnvironment(("OSDU_URL", "https://node.example.test"));

        Assert.Equal("https://central.example.test", Supplied(configuration.For("partition-a"), node).Resolve("${env:OSDU_URL}"));
        Assert.Equal("https://node.example.test", Supplied(configuration.For("partition-b"), node).Resolve("${env:OSDU_URL}"));
        Assert.Equal("https://node.example.test", await Supplied(configuration.For("partition-b"), node).ResolveAsync("${env:OSDU_URL}"));
    }

    [Fact]
    public void A_partition_deferring_to_the_node_also_sets_aside_what_the_run_carries_for_every_partition()
    {
        var node = NodeEnvironment(("OSDU_URL", "https://node.example.test"));

        // The value set for no partition reaches the node as the run's own scope; the partition's deferral is laid over it.
        using var run = SuppliedReferences.Supply(Properties(("OSDU_URL", "https://central.example.test")));

        Assert.Equal("https://node.example.test", Supplied(Properties(("OSDU_URL", "${env:OSDU_URL}")), node).Resolve("${env:OSDU_URL}"));
        Assert.Equal("https://central.example.test", Supplied(Properties(), node).Resolve("${env:OSDU_URL}"));
    }

    [Fact]
    public void Two_properties_naming_each_other_do_not_chain_and_cannot_loop()
    {
        var node = NodeEnvironment(("FIRST", "first-from-node"), ("SECOND", "second-from-node"));
        var resolver = Supplied(Properties(("FIRST", "${env:SECOND}"), ("SECOND", "${env:FIRST}")), node);

        // Each substitution is resolved once, by the node: what FIRST names is read from the node's environment.
        Assert.Equal("second-from-node", resolver.Resolve("${env:FIRST}"));
        Assert.Equal("first-from-node", resolver.Resolve("${env:SECOND}"));
    }

    [Fact]
    public async Task A_property_that_defers_to_the_node_is_listed_as_the_nodes_answer()
    {
        var reports = new List<SuppliedReferenceUse>();
        var resolver = Supplied(
            Properties(("OSDU_URL", "${env:OSDU_URL}"), ("OSDU_DATA_PARTITION", "dev"), ("OSDU_LEGAL_TAG", "${env:ESTATE_LEGAL_TAG}")),
            NodeEnvironment(("OSDU_ACL_OWNER", "owners"), ("OSDU_URL", "https://node.example.test"), ("ESTATE_LEGAL_TAG", "tag")));

        await Task.Run(() =>
        {
            SuppliedReferences.ReportTo(reports.Add);
            foreach (var name in new[] { "OSDU_ACL_OWNER", "OSDU_DATA_PARTITION", "OSDU_LEGAL_TAG", "OSDU_URL" })
            {
                resolver.Resolve("${env:" + name + "}");
            }
        });

        Assert.Equal(
            [
                ("OSDU_ACL_OWNER", false, false),
                ("OSDU_DATA_PARTITION", true, false),
                ("OSDU_LEGAL_TAG", true, false),
                ("OSDU_URL", false, true),
            ],
            reports.Select(r => (r.Name, r.Supplied, r.Deferred)));
    }

    [Theory]
    [InlineData("OSDU_URL", "${env:OSDU_URL}", true)]
    [InlineData("OSDU_URL", "${env:OSDU_URL_2}", false)]
    [InlineData("OSDU_URL", "https://${env:OSDU_URL}", false)]
    [InlineData("OSDU_URL", "${keyvault:vault/OSDU_URL}", false)]
    [InlineData("OSDU_URL", "", false)]
    public void Only_a_value_that_is_exactly_its_own_reference_defers_to_the_node(string name, string value, bool defers)
        => Assert.Equal(defers, SuppliedReferences.DefersToNode(name, value));
}
