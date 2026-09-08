using SqlFlow.Cli;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Guards the CLI argument parser: a value-taking option must consume its value so the value never leaks into
/// the positional-argument list (an option missing from the value-taking set silently turns its value into a
/// positional), while a plain flag must not swallow the token after it.
/// </summary>
public sealed class CliArgumentParsingTests
{
    [Theory]
    [InlineData(new[] { "run", "pipe.yaml", "--log-level", "debug" }, new[] { "run", "pipe.yaml" })]
    [InlineData(new[] { "trigger", "--repo", "estate", "--flow", "wells" }, new[] { "trigger" })]
    [InlineData(new[] { "runs", "list", "--status", "failed", "--page-size", "5" }, new[] { "runs", "list" })]
    [InlineData(new[] { "schedules", "create", "--cron", "0 4 * * *", "--timezone", "Europe/Oslo" }, new[] { "schedules", "create" })]
    public void ValueTakingOption_DoesNotLeakItsValueIntoPositionals(string[] args, string[] expected)
        => Assert.Equal(expected, Program.PositionalArguments(args));

    [Fact]
    public void PlainFlag_DoesNotSwallowTheNextToken()
    {
        // --json is a boolean flag, not value-taking: the token after it is a real positional.
        Assert.Equal(["runs", "local", "folder"], Program.PositionalArguments(["runs", "--json", "local", "folder"]));
    }

    [Fact]
    public void TheEstateAndScheduleOptions_AreRegisteredValueTaking()
    {
        Assert.Contains("--cron", Program.ValueTakingOptions);
        Assert.Contains("--timezone", Program.ValueTakingOptions);
        Assert.Contains("--credential-ref", Program.ValueTakingOptions);
        Assert.Contains("--commit", Program.ValueTakingOptions);
    }
}
