using System.Reflection;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The CLI carries its own copy of the run-trigger body, so the two records can drift apart without anything
/// failing to compile: the client simply stops sending a field and the server silently defaults it. That is
/// a trigger option could be accepted by 'sqlflow trigger', parsed into the run parameters, and then dropped on
/// the floor, turning a scoped re-run into an ordinary run with no error anywhere.
/// This pins the two shapes together so the next field added on either side has to be added on both.
/// </summary>
public sealed class RunTriggerContractParityTests
{
    private static IEnumerable<(string Name, Type Type)> Parameters(Type record)
        => record.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First()
            .GetParameters()
            .Select(p => (p.Name!, p.ParameterType));

    [Fact]
    public void CliRunTriggerRequest_MatchesTheControlPlaneContract()
    {
        var server = typeof(RunTriggerRequest);
        var client = typeof(SqlFlow.Cli.Remote.RunTriggerAccepted).Assembly.GetType("SqlFlow.Cli.Remote.RunTriggerRequest", throwOnError: true)!;

        Assert.Equal(Parameters(server).ToArray(), Parameters(client).ToArray());
    }

    [Theory]
    [InlineData("Operation")]
    [InlineData("Force")]
    [InlineData("Values")]
    [InlineData("Drop")]
    [InlineData("SubmissionId")]
    [InlineData("RecordKeys")]
    [InlineData("PublishTo")]
    public void EveryRunParameter_IsCarriedByBothSides(string parameter)
    {
        var client = typeof(SqlFlow.Cli.Remote.RunTriggerAccepted).Assembly.GetType("SqlFlow.Cli.Remote.RunTriggerRequest", throwOnError: true)!;

        Assert.Contains(Parameters(typeof(RunTriggerRequest)), p => p.Name == parameter);
        Assert.Contains(Parameters(client), p => p.Name == parameter);
    }
}
