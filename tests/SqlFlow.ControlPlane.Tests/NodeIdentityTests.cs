using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Execution;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>The tests that set <see cref="NodeIdentity.EnvironmentVariable"/>, kept apart from every test that reads it.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NodeNameEnvironment
{
    public const string Name = "Node name environment";
}

/// <summary>
/// The name a compute node registers, claims runs and recovers its orphans under. Two node processes on one host (a
/// control plane hosting its in-process worker beside a standalone <c>sqlflow worker</c>) used to share the machine name,
/// so the startup recovery of either took the runs the other was executing for orphans of its own and requeued them.
/// </summary>
[Collection(NodeNameEnvironment.Name)]
public sealed class NodeIdentityTests
{
    [Fact]
    public void A_configured_name_is_used_trimmed()
    {
        Assert.Equal("node-a", NodeIdentity.Resolve("  node-a ").Name);
    }

    [Fact]
    public void Without_a_configured_name_a_node_takes_the_environment_and_then_the_machine_name()
    {
        var saved = Environment.GetEnvironmentVariable(NodeIdentity.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NodeIdentity.EnvironmentVariable, "node-from-environment");
            Assert.Equal("node-from-environment", NodeIdentity.Resolve(null).Name);
            Assert.Equal("node-from-environment", NodeIdentity.Resolve("   ").Name);
            Assert.Equal("configured", NodeIdentity.Resolve("configured").Name);

            Environment.SetEnvironmentVariable(NodeIdentity.EnvironmentVariable, null);
            Assert.Equal(Environment.MachineName, NodeIdentity.Resolve(null).Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NodeIdentity.EnvironmentVariable, saved);
        }
    }

    [Fact]
    public void A_name_the_catalog_cannot_hold_is_refused()
    {
        var tooLong = Assert.Throws<ArgumentException>(() => NodeIdentity.Resolve(new string('n', NodeIdentity.MaxLength + 1)));
        Assert.Contains("at most 256 characters", tooLong.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => NodeIdentity.Resolve("nodea"));
        Assert.Equal(NodeIdentity.MaxLength, NodeIdentity.Resolve(new string('n', NodeIdentity.MaxLength)).Name.Length);
    }

    [Fact]
    public void A_worker_claims_and_recovers_under_the_identity_it_is_given()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var worker = new RunWorker(
            provider, new DocumentExecutor(provider), TimeProvider.System, NullLogger<RunWorker>.Instance, NodeIdentity.Resolve("worker-beside-the-control-plane"));

        Assert.Equal("worker-beside-the-control-plane", worker.NodeName);
    }

    [Fact]
    public void A_control_plane_refuses_a_node_name_it_could_not_store()
    {
        // Valid in every other respect (a synthetic signing key, not a secret), so the refusal is the node name's.
        var options = new Configuration.ControlPlaneOptions();
        options.Jwt.SigningKey = new string('x', 64);
        options.Validate();
        options.Worker.NodeName = new string('n', NodeIdentity.MaxLength + 1);

        var refused = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("ControlPlane:Worker:NodeName", refused.Message, StringComparison.Ordinal);
    }
}
