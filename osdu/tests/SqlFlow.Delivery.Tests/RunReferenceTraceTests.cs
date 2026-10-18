using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Orchestration;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A run of this module's kinds says on its trace where each reference it resolves takes its value: the central
/// configuration the control plane supplied with the run (the values set for no partition, which every run carries, and its
/// partition's own over them), or the node's own environment. Where a record goes and under whose access and legal terms
/// are such references, so a run that resolved one from a node's stale environment says so, a run given no configuration
/// included. Names and origins only: no value, supplied or the node's, reaches the trace, and each reference is named once.
/// The run's collector is built the way every executor of the module builds it (<see cref="RunArtifacts.BuildEventPlumbing"/>),
/// and the run's own values are the scope its node opens for it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunReferenceTraceTests
{
    private static SecretResolver NodeEnvironment(params (string Name, string Value)[] variables)
        => new([new HeldEnvironment(variables)]);

    private sealed class HeldEnvironment((string Name, string Value)[] variables) : ISecretProvider
    {
        private readonly Dictionary<string, string> _held = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

        public string Scheme => "env";

        public string Resolve(string locator)
            => _held.TryGetValue(locator, out var value) ? value : throw new SqlFlowException($"Environment variable '{locator}' is not set.");
    }

    private static Dictionary<string, string> Properties(params (string Name, string Value)[] values)
        => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="work"/> as a run of the module given <paramref name="run"/> as its own values, on an engine over
    /// <paramref name="node"/>, and answers the lines its trace says about references.
    /// </summary>
    private static async Task<List<string>> TraceOfAsync(
        ISecretResolver node, IReadOnlyDictionary<string, string> run, Func<EngineContext, Task> work)
    {
        RunEventCollector? events = null;
        await Task.Run(async () =>
        {
            using var scope = run.Count > 0 ? SuppliedReferences.Supply(run) : null;
            events = RunArtifacts.BuildEventPlumbing(new DocumentExecutionOptions(), "references").Events;
            await work(Samples.Engine(null) with { Secrets = node });
        });

        return [.. events!.Records.Where(r => r.Step == DocumentExecutionOptions.ReferenceStage).Select(r => r.Message)];
    }

    [Fact]
    public async Task A_run_names_where_each_reference_took_its_value_once_and_never_the_value()
    {
        var node = NodeEnvironment(
            ("OSDU_URL", "https://node.example.test"),
            ("OSDU_LEGAL_TAG", "legal-tag-from-node"),
            ("OSDU_DATA_PARTITION", "partition-from-node"));

        var lines = await TraceOfAsync(node, Properties(("OSDU_ACL_OWNER", "owners-from-the-estate")), async engine =>
        {
            var partition = engine.WithSuppliedReferences(
                Properties(("OSDU_DATA_PARTITION", "partition-from-control-plane"), ("OSDU_LEGAL_TAG", "${env:OSDU_LEGAL_TAG}")));

            // The partition's values first, the run's own under them, and the node for the rest and for what defers to it.
            Assert.Equal("https://node.example.test/api/storage", partition.Secrets.Resolve("${env:OSDU_URL}/api/storage"));
            Assert.Equal("partition-from-control-plane", await partition.Secrets.ResolveAsync("${env:OSDU_DATA_PARTITION}"));
            Assert.Equal("legal-tag-from-node", partition.Secrets.Resolve("${env:OSDU_LEGAL_TAG}"));
            Assert.Equal("owners-from-the-estate", partition.Secrets.Resolve("${env:OSDU_ACL_OWNER}"));

            // Resolved again, or a value without a reference: nothing more is said.
            Assert.Equal("https://node.example.test", partition.Secrets.Resolve("${env:OSDU_URL}"));
            Assert.Equal("a literal value", partition.Secrets.Resolve("a literal value"));
        });

        Assert.Equal(
            [
                "Reference OSDU_URL takes its value from this node's environment.",
                "Reference OSDU_DATA_PARTITION takes its value from the configuration the control plane supplied with this run.",
                "Reference OSDU_LEGAL_TAG takes its value from this node's environment: the supplied configuration hands it there.",
                "Reference OSDU_ACL_OWNER takes its value from the configuration the control plane supplied with this run.",
            ],
            lines);
        foreach (var value in new[] { "node.example.test", "partition-from-control-plane", "partition-from-node", "legal-tag-from-node", "owners-from-the-estate" })
        {
            Assert.DoesNotContain(lines, line => line.Contains(value, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_run_given_no_configuration_names_every_reference_as_the_nodes()
    {
        var node = NodeEnvironment(("OSDU_URL", "https://node.example.test"), ("OSDU_ACL_OWNER", "owners@example.test"));

        var lines = await TraceOfAsync(node, Properties(), engine =>
        {
            Assert.Equal(
                "https://node.example.test owners@example.test",
                engine.WithSuppliedReferences(Properties()).Secrets.Resolve("${env:OSDU_URL} ${env:OSDU_ACL_OWNER}"));
            return Task.CompletedTask;
        });

        Assert.Equal(
            [
                "Reference OSDU_URL takes its value from this node's environment.",
                "Reference OSDU_ACL_OWNER takes its value from this node's environment.",
            ],
            lines);
    }

    [Fact]
    public async Task A_reference_the_node_cannot_answer_is_named_before_the_error_that_follows()
    {
        var lines = await TraceOfAsync(NodeEnvironment(), Properties(), engine =>
        {
            var missing = Assert.Throws<SqlFlowException>(
                () => engine.WithSuppliedReferences(Properties(("OSDU_DATA_PARTITION", "dev"))).Secrets.Resolve("${env:OSDU_URL}"));
            Assert.Contains("OSDU_URL", missing.Message, StringComparison.Ordinal);
            return Task.CompletedTask;
        });

        Assert.Equal(["Reference OSDU_URL takes its value from this node's environment."], lines);
    }

    [Fact]
    public async Task References_resolved_at_once_from_parallel_work_are_each_named_once()
    {
        var node = NodeEnvironment(("OSDU_URL", "https://node.example.test"), ("OSDU_ACL_VIEWER", "viewers@example.test"));

        var lines = await TraceOfAsync(node, Properties(), async engine =>
        {
            var partition = engine.WithSuppliedReferences(Properties(("OSDU_DATA_PARTITION", "dev")));
            await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
            {
                await partition.Secrets.ResolveAsync(i % 2 == 0 ? "${env:OSDU_URL}" : "${env:OSDU_ACL_VIEWER}");
                await partition.Secrets.ResolveAsync("${env:OSDU_DATA_PARTITION}");
            })));
        });

        Assert.Equal(3, lines.Count);
        Assert.Single(lines, line => line.StartsWith("Reference OSDU_URL ", StringComparison.Ordinal));
        Assert.Single(lines, line => line.StartsWith("Reference OSDU_ACL_VIEWER ", StringComparison.Ordinal));
        Assert.Single(lines, line => line.StartsWith("Reference OSDU_DATA_PARTITION ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_context_given_values_twice_lays_the_second_over_the_first()
    {
        var engine = Samples.Engine(null) with { Secrets = NodeEnvironment(("OSDU_URL", "https://node.example.test")) };

        var twice = engine
            .WithSuppliedReferences(Properties(("OSDU_DATA_PARTITION", "first"), ("OSDU_LEGAL_TAG", "first-tag")))
            .WithSuppliedReferences(Properties(("OSDU_DATA_PARTITION", "second")));

        Assert.Equal("second first-tag https://node.example.test", twice.Secrets.Resolve("${env:OSDU_DATA_PARTITION} ${env:OSDU_LEGAL_TAG} ${env:OSDU_URL}"));
        Assert.Equal(Properties(("OSDU_DATA_PARTITION", "second"), ("OSDU_LEGAL_TAG", "first-tag")), twice.Supplied);
    }

    [Fact]
    public void A_context_given_a_written_out_credential_is_refused()
    {
        var engine = Samples.Engine(null) with { Secrets = NodeEnvironment() };

        var refused = Assert.Throws<SqlFlowException>(() => engine.WithSuppliedReferences(Properties(("OSDU_CLIENT_SECRET", "s3cr3t"))));

        Assert.Contains("OSDU_CLIENT_SECRET names a credential", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", refused.Message, StringComparison.Ordinal);
    }
}
