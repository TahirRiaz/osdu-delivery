using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Http;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A supplied property that is its own reference (<c>OSDU_URL = ${env:OSDU_URL}</c>) is not refused: it defers to the
/// node's environment, which is how a repository or a partition overrides a value the control plane sets for every one.
/// Properties do not chain, so two naming each other cannot loop, and where a reference got its value says the node for a
/// property that deferred to it.
/// </summary>
public sealed class SuppliedReferenceDeferralTests
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
        var node = new NodeEnvironment(("OSDU_URL", "https://node.example.test"));

        Assert.Equal("https://central.example.test", SuppliedReferenceResolver.For(configuration.For("partition-a"), node).Resolve("${env:OSDU_URL}"));
        Assert.Equal("https://node.example.test", SuppliedReferenceResolver.For(configuration.For("partition-b"), node).Resolve("${env:OSDU_URL}"));
        Assert.Equal("https://node.example.test", await SuppliedReferenceResolver.For(configuration.For("partition-b"), node).ResolveAsync("${env:OSDU_URL}"));
    }

    [Fact]
    public void Two_properties_naming_each_other_do_not_chain_and_cannot_loop()
    {
        var node = new NodeEnvironment(("FIRST", "first-from-node"), ("SECOND", "second-from-node"));
        var resolver = SuppliedReferenceResolver.For(Properties(("FIRST", "${env:SECOND}"), ("SECOND", "${env:FIRST}")), node);

        // Each substitution is resolved once, by the node: what FIRST names is read from the node's environment.
        Assert.Equal("second-from-node", resolver.Resolve("${env:FIRST}"));
        Assert.Equal("first-from-node", resolver.Resolve("${env:SECOND}"));
    }

    [Fact]
    public void A_property_that_defers_to_the_node_is_listed_as_the_nodes_answer()
    {
        var supplied = Properties(("OSDU_URL", "${env:OSDU_URL}"), ("OSDU_DATA_PARTITION", "dev"), ("OSDU_LEGAL_TAG", "${env:ESTATE_LEGAL_TAG}"));
        var sources = new[] { "OSDU_ACL_OWNER", "OSDU_DATA_PARTITION", "OSDU_LEGAL_TAG", "OSDU_URL" }.Select(name => ReferenceSource.Of(name, supplied));

        Assert.Equal(
            [
                ("OSDU_ACL_OWNER", ReferenceOrigin.Node, false),
                ("OSDU_DATA_PARTITION", ReferenceOrigin.ControlPlane, false),
                ("OSDU_LEGAL_TAG", ReferenceOrigin.ControlPlane, false),
                ("OSDU_URL", ReferenceOrigin.Node, true),
            ],
            sources.Select(s => (s.Name, s.Origin, s.Deferred)));
    }

    [Theory]
    [InlineData("OSDU_URL", "${env:OSDU_URL}", true)]
    [InlineData("OSDU_URL", "${env:OSDU_URL_2}", false)]
    [InlineData("OSDU_URL", "https://${env:OSDU_URL}", false)]
    [InlineData("OSDU_URL", "${keyvault:vault/OSDU_URL}", false)]
    [InlineData("OSDU_URL", "", false)]
    public void Only_a_value_that_is_exactly_its_own_reference_defers_to_the_node(string name, string value, bool defers)
        => Assert.Equal(defers, SuppliedReferenceResolver.DefersToNode(name, value));
}
