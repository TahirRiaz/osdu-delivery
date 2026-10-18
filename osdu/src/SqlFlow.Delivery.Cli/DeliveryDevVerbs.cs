using SqlFlow.Cli.Hosting;
using SqlFlow.Delivery.Setup;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// <c>sqlflow dev</c>: the local run's setup and check (osdu/docs/reference/cli/dev.md) from the command line, the same
/// code the <c>osdu-delivery-setup</c> host runs (<see cref="DevCommand"/>). Run from the command line, the setup runs the
/// command line itself again for what only it does (creating the catalog, registering the partition).
/// </summary>
public static class DeliveryDevVerbs
{
    /// <summary>The verb's usage lines.</summary>
    public static IReadOnlyList<string> Usage { get; } = DevCommand.UsageFor("sqlflow dev");

    public static Task<int> DevAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return DevCommand.RunAsync(
            Arguments(context.Arguments.All, context.Verb), "sqlflow dev", context.Out, context.Error, Directory.GetCurrentDirectory(),
            DevCommandLineKind.Self, context.CancellationToken);
    }

    /// <summary>The command line's arguments without the verb itself: what the setup's own reading takes.</summary>
    internal static IReadOnlyList<string> Arguments(IReadOnlyList<string> all, string verb)
    {
        ArgumentNullException.ThrowIfNull(all);
        var index = all.ToList().FindIndex(a => string.Equals(a, verb, StringComparison.OrdinalIgnoreCase));
        return [.. all.Where((_, i) => i != index)];
    }
}
