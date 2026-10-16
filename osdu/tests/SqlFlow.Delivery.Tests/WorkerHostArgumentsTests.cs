using SqlFlow.Delivery.Cli;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The worker host runs SQLFlow's <c>worker</c> verb with the node's options and nothing else: a verb or any other
/// positional argument is refused, and an option's value is the node's, even one spelled like the verb.
/// </summary>
public sealed class WorkerHostArgumentsTests
{
    [Fact]
    public void The_nodes_options_run_the_worker_verb()
    {
        var (commandLine, refusal) = WorkerHostArguments.Of(["--pool", "onprem", "--poll-seconds", "10", "-v"]);

        Assert.Null(refusal);
        Assert.Equal(["worker", "--pool", "onprem", "--poll-seconds", "10", "-v"], Assert.IsType<string[]>(commandLine));
        Assert.Equal(["worker"], Assert.IsType<string[]>(WorkerHostArguments.Of([]).CommandLine));
    }

    [Fact]
    public void A_pool_named_like_the_verb_is_a_pool_and_not_the_verb()
    {
        var (commandLine, refusal) = WorkerHostArguments.Of(["--pool", "worker"]);

        Assert.Null(refusal);
        Assert.Equal(["worker", "--pool", "worker"], Assert.IsType<string[]>(commandLine));
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("WORKER")]
    public void Naming_the_verb_is_refused_as_implied(string verb)
    {
        var (commandLine, refusal) = WorkerHostArguments.Of([verb, "--pool", "onprem"]);

        Assert.Null(commandLine);
        Assert.Contains("the 'worker' verb is implied", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_verb_or_argument_is_refused_by_name()
    {
        var (commandLine, refusal) = WorkerHostArguments.Of(["run", "flows/logs.yaml"]);

        Assert.Null(commandLine);
        Assert.Contains("'run' is not one of them", refusal, StringComparison.Ordinal);
    }
}
