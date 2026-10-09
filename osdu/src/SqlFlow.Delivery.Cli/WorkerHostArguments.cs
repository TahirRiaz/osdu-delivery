using SqlFlow.Cli.Hosting;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The command line of the OSDU Delivery worker host: SQLFlow's <c>worker</c> verb with the node's own options and nothing
/// else. The verb is the host's identity, not something a caller chooses, so a command line that names a verb, or any
/// other positional argument, is refused: it would make the image behave as a different program, or be silently ignored.
/// An option's value is never taken for one (<c>--pool worker</c> serves the pool named worker).
/// </summary>
public static class WorkerHostArguments
{
    /// <summary>The verb the host runs.</summary>
    public const string Verb = "worker";

    /// <summary>
    /// The command line SQLFlow's CLI runs for <paramref name="args"/> (the <c>worker</c> verb, then the node's options),
    /// or the reason it is refused.
    /// </summary>
    public static (string[]? CommandLine, string? Refusal) Of(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var positionals = new CliArguments(args).Positionals;
        if (positionals.Count == 0)
        {
            return ([Verb, .. args], null);
        }

        var first = positionals[0];
        return (null, string.Equals(first, Verb, StringComparison.OrdinalIgnoreCase)
            ? "this host is the OSDU Delivery worker; pass the node's options only (the 'worker' verb is implied)."
            : $"this host is the OSDU Delivery worker and takes the node's options only (--url, --token, --pool, --poll-seconds, --drain-seconds, -v); '{first}' is not one of them.");
    }
}
