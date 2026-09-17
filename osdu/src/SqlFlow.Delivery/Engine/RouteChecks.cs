using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// What a flow's route can deliver, checked against the kind its mapping renders before anything is sent. The ddms route
/// sends a record to the collection of the DDMS serving its entity type (<see cref="DdmsRouting"/>): a kind no DDMS the
/// flow reaches serves could never be written, verified or removed through it, and bulk data sent to a collection that
/// holds records alone would never land (osdu/specs/wellbore-ddms/INTEGRATION.md section 2).
/// </summary>
public static class RouteChecks
{
    /// <summary>The route names a flow reads: storage, file, manifest and ddms.</summary>
    public static string Name(DeliveryProtocol protocol) => protocol switch
    {
        DeliveryProtocol.OsduRecord => "storage",
        DeliveryProtocol.OsduFile => "file",
        DeliveryProtocol.OsduManifest => "manifest",
        DeliveryProtocol.OsduWellLog => "ddms",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "not a delivery protocol"),
    };

    /// <summary>
    /// Throws a <see cref="DeliveryException"/> when the flow's route cannot deliver records of <paramref name="kind"/>.
    /// <paramref name="routing"/> is the ddms route's routing with its registrations read (the protocol's); without it
    /// the flow's declarations are checked as they stand, and a type a DDMS named by registration may serve is let through.
    /// </summary>
    public static void Check(FlowDefinition flow, string kind, DdmsRouting? routing = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (flow.Target.Protocol != DeliveryProtocol.OsduWellLog)
        {
            return;
        }

        var keys = KeyPaths.Of(flow);
        var entityType = OsduKind.EntityType(kind)
            ?? throw new DeliveryException(
                $"{KeyPaths.Where(flow)}: {keys.Name("render.mapping")} {flow.Render.Mapping} renders {kind}, which names no entity type, so the ddms route cannot tell which DDMS collection takes it.");
        if ((routing ?? DdmsRouting.Of(flow)).Problem(entityType, sendsBulk: Planning.Planner.PayloadName(flow) is not null) is { } problem)
        {
            throw new DeliveryException($"{problem} ({keys.Name("render.mapping")} {flow.Render.Mapping} renders {kind}.)");
        }
    }
}
