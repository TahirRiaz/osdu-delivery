using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Delivery.Hosting;

namespace SqlFlow.Delivery.ControlPlane.Host;

/// <summary>
/// The OSDU Delivery control plane: SQLFlow's whole control plane (the catalog, the run queue and dispatcher, the
/// scheduler, the managed git sync, identity, notifications and the node protocol) composed with the OSDU module, which
/// adds the delivery, retrieval and cache flow kinds, the delivery ledger over the <c>osdu</c> schema, and every delivery
/// endpoint. Nothing about the platform is forked: this program is the composition, and the branding it runs under.
/// </summary>
public sealed class Program
{
    private Program()
    {
    }

    public static Task Main(string[] args)
        => ControlPlaneHost.RunAsync(args, OsduDeliveryBranding.Product, new DeliveryControlPlaneModule());
}
