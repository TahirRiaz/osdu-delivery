using SqlFlow.Cli.Hosting;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Hosting;

namespace SqlFlow.Delivery.Cli.Host;

/// <summary>
/// The <c>sqlflow</c> command line of OSDU Delivery: every SQLFlow verb (validate, run, db, worker, the control plane
/// verbs) plus the OSDU module's own (check, cache, template), with the module's flow kinds registered in every service
/// provider the CLI builds, so a local run behaves exactly as a run on a node does.
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args)
        => CliHost.RunAsync(args, OsduDeliveryBranding.Product, new DeliveryCliModule());
}
