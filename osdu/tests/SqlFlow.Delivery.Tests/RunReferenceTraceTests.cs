using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Http;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A run's trace says where each reference the run resolves takes its value: the central configuration the control plane
/// supplied with the run, or the node's own environment (<see cref="RunReferenceTrace"/>). Where a record goes and under
/// whose access and legal terms are such references, so a run that resolved one from a node's stale environment says so.
/// Names and origins only: no value, supplied or the node's, ever reaches the trace, and each reference is named once.
/// </summary>
public sealed class RunReferenceTraceTests
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

    /// <summary>A context serving a run over <paramref name="node"/>, and the events its trace writes.</summary>
    private static (EngineContext Context, RunEventCollector Events) ServingARun(ISecretResolver node)
    {
        var events = new RunEventCollector();
        var loggers = new RunLogLoggerFactory(new RunLogger(RunLogLevel.Debug), events, Guid.NewGuid(), "references");
        return ((Samples.Engine(null) with { Secrets = node }).ForRun(loggers), events);
    }

    private static List<string> RunLines(RunEventCollector events)
        => [.. events.Records.Where(r => r.Step == RunTrace.RunStep).Select(r => r.Message)];

    [Fact]
    public async Task A_run_names_where_each_reference_took_its_value_once_and_never_the_value()
    {
        var node = new NodeEnvironment(
            ("OSDU_URL", "https://node.example.test"),
            ("OSDU_LEGAL_TAG", "legal-tag-from-node"),
            ("OSDU_DATA_PARTITION", "partition-from-node"));
        var (context, events) = ServingARun(node);
        var run = context.WithSuppliedReferences(
            Properties(("OSDU_DATA_PARTITION", "partition-from-control-plane"), ("OSDU_LEGAL_TAG", "${env:OSDU_LEGAL_TAG}")));

        // The run resolves exactly as it did before: the configuration first, the node for the rest and for what defers to it.
        Assert.Equal("https://node.example.test/api/storage", run.Secrets.Resolve("${env:OSDU_URL}/api/storage"));
        Assert.Equal("partition-from-control-plane", await run.Secrets.ResolveAsync("${env:OSDU_DATA_PARTITION}"));
        Assert.Equal("legal-tag-from-node", run.Secrets.Resolve("${env:OSDU_LEGAL_TAG}"));

        // Resolved again, or a value without a reference: nothing more is said.
        Assert.Equal("https://node.example.test", run.Secrets.Resolve("${env:OSDU_URL}"));
        Assert.Equal("a literal value", run.Secrets.Resolve("a literal value"));

        var lines = RunLines(events);
        Assert.Equal(
            [
                "Reference ${env:OSDU_URL} takes its value from this node's environment: the central configuration does not set it.",
                "Reference ${env:OSDU_DATA_PARTITION} takes its value from the central configuration the control plane supplied with this run.",
                "Reference ${env:OSDU_LEGAL_TAG} takes its value from this node's environment: the central configuration names it as its own reference, which leaves it to the node.",
            ],
            lines);
        foreach (var value in new[] { "node.example.test", "partition-from-control-plane", "partition-from-node", "legal-tag-from-node" })
        {
            Assert.DoesNotContain(lines, line => line.Contains(value, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_run_given_no_configuration_names_every_reference_as_the_nodes()
    {
        var (context, events) = ServingARun(new NodeEnvironment(("OSDU_URL", "https://node.example.test"), ("OSDU_ACL_OWNER", "owners@example.test")));
        var run = context.WithSuppliedReferences(Properties());

        Assert.Equal("https://node.example.test owners@example.test", run.Secrets.Resolve("${env:OSDU_URL} ${env:OSDU_ACL_OWNER}"));

        Assert.Equal(
            [
                "Reference ${env:OSDU_URL} takes its value from this node's environment: the run carries no central configuration.",
                "Reference ${env:OSDU_ACL_OWNER} takes its value from this node's environment: the run carries no central configuration.",
            ],
            RunLines(events));
    }

    [Fact]
    public void A_reference_the_node_cannot_answer_is_named_before_the_error_that_follows()
    {
        var (context, events) = ServingARun(new NodeEnvironment());
        var run = context.WithSuppliedReferences(Properties(("OSDU_DATA_PARTITION", "dev")));

        var missing = Assert.Throws<SqlFlowException>(() => run.Secrets.Resolve("${env:OSDU_URL}"));

        Assert.Contains("OSDU_URL", missing.Message, StringComparison.Ordinal);
        Assert.Equal(
            ["Reference ${env:OSDU_URL} takes its value from this node's environment: the central configuration does not set it."],
            RunLines(events));
    }

    /// <summary>
    /// A reference first resolved while the run works on a record its trace does not describe is still named: the line is
    /// the run's own, bounded by the references it declares, not by its records.
    /// </summary>
    [Fact]
    public void A_reference_first_resolved_for_a_record_the_trace_leaves_out_is_still_named()
    {
        var (context, events) = ServingARun(new NodeEnvironment(("OSDU_URL", "https://node.example.test")));
        var run = context.WithSuppliedReferences(Properties());

        using (RunTrace.AboutRecords(described: false))
        {
            run.Secrets.Resolve("${env:OSDU_URL}");
        }

        Assert.Single(RunLines(events));
    }

    [Fact]
    public void A_context_that_serves_no_run_resolves_as_before_and_says_nothing()
    {
        var node = new NodeEnvironment(("OSDU_URL", "https://node.example.test"));
        var engine = Samples.Engine(null) with { Secrets = node };

        Assert.Same(node, engine.WithSuppliedReferences(Properties()).Secrets);
        Assert.IsType<SuppliedReferenceResolver>(engine.WithSuppliedReferences(Properties(("OSDU_DATA_PARTITION", "dev"))).Secrets);
    }

    [Fact]
    public void A_run_names_at_most_its_allowance_and_says_once_where_it_stops()
    {
        var events = new RunEventCollector();
        var log = new RunLogLoggerFactory(new RunLogger(RunLogLevel.Debug), events, Guid.NewGuid(), "references").CreateLogger(RunTrace.RunStep);
        var node = new NodeEnvironment(("FIRST", "1"), ("SECOND", "2"), ("THIRD", "3"), ("FOURTH", "4"));
        var trace = new RunReferenceTrace(node, Properties(), log, max: 2);

        Assert.Equal("1 2 3 4", trace.Resolve("${env:FIRST} ${env:SECOND} ${env:THIRD} ${env:FOURTH}"));
        trace.Resolve("${env:THIRD}");
        trace.Resolve("${env:FIRST}");

        var lines = RunLines(events);
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("Reference ${env:FIRST} ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("Reference ${env:SECOND} ", lines[1], StringComparison.Ordinal);
        Assert.Equal("This run resolves more than 2 references; the trace says where the ones named above take their values, and names no other.", lines[2]);
    }

    [Fact]
    public async Task References_resolved_at_once_from_parallel_work_are_each_named_once()
    {
        var (context, events) = ServingARun(new NodeEnvironment(("OSDU_URL", "https://node.example.test"), ("OSDU_ACL_VIEWER", "viewers@example.test")));
        var run = context.WithSuppliedReferences(Properties(("OSDU_DATA_PARTITION", "dev")));

        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
        {
            await run.Secrets.ResolveAsync(i % 2 == 0 ? "${env:OSDU_URL}" : "${env:OSDU_ACL_VIEWER}");
            await run.Secrets.ResolveAsync("${env:OSDU_DATA_PARTITION}");
        })));

        var lines = RunLines(events);
        Assert.Equal(3, lines.Count);
        Assert.Single(lines, line => line.StartsWith("Reference ${env:OSDU_URL} ", StringComparison.Ordinal));
        Assert.Single(lines, line => line.StartsWith("Reference ${env:OSDU_ACL_VIEWER} ", StringComparison.Ordinal));
        Assert.Single(lines, line => line.StartsWith("Reference ${env:OSDU_DATA_PARTITION} ", StringComparison.Ordinal));
    }
}
