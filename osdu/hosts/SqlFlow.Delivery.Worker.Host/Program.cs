using SqlFlow.Cli.Hosting;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Hosting;

namespace SqlFlow.Delivery.Worker.Host;

/// <summary>
/// An OSDU Delivery compute node: SQLFlow's node runtime, with the OSDU module installed so the node can execute
/// delivery, retrieval and cache flows. It is the CLI's <c>worker</c> verb with nothing else on the command line, which
/// keeps one drain loop, one set of node options and one place where a run is executed, however the node was started.
/// </summary>
/// <remarks>
/// The container passes only the node's own options (<c>--pool</c>, <c>--poll-seconds</c>, <c>--drain-seconds</c>, and
/// <c>--url</c> / <c>--token</c> when the environment does not carry them); every credential, including the module
/// database connection, is resolved on the node from its own environment.
/// </remarks>
internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // The verb is this host's identity, not something a caller chooses: a node process runs the node loop and
        // nothing else, so an argument that named another verb would make the image behave as a different program.
        if (Array.Exists(args, argument => string.Equals(argument, "worker", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("ERROR  this host is the OSDU Delivery worker; pass the node's options only (the 'worker' verb is implied).");
            return Task.FromResult(1);
        }

        return CliHost.RunAsync(["worker", .. args], OsduDeliveryBranding.Product, new DeliveryCliModule());
    }
}
