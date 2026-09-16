using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// What a flow's route can deliver, checked against the kind its mapping renders before anything is sent. The ddms route
/// writes to the wellbore DDMS's well log collection unless the flow names its own paths (osdu/specs/wellbore-ddms/openapi.json,
/// <c>POST /ddms/v3/welllogs</c>). That collection reads, lists the versions of and deletes only the ids of
/// <c>work-product-component--WellLog</c> records (the <c>record_id</c> pattern of its <c>GET</c> and <c>DELETE</c>
/// routes), so a record of another entity type sent there could never be verified or removed through it.
/// </summary>
public static class RouteChecks
{
    /// <summary>The entity type the ddms route's default collection serves.</summary>
    public const string WellLogEntityType = "work-product-component--WellLog";

    /// <summary>The route names a flow reads: storage, file, manifest and ddms.</summary>
    public static string Name(DeliveryProtocol protocol) => protocol switch
    {
        DeliveryProtocol.OsduRecord => "storage",
        DeliveryProtocol.OsduFile => "file",
        DeliveryProtocol.OsduManifest => "manifest",
        DeliveryProtocol.OsduWellLog => "ddms",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "not a delivery protocol"),
    };

    /// <summary>Throws a <see cref="DeliveryException"/> when the flow's route cannot deliver records of <paramref name="kind"/>.</summary>
    public static void Check(FlowDefinition flow, string kind)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (flow.Target.Protocol != DeliveryProtocol.OsduWellLog || flow.Target.ProtocolOptions.RecordPath is not null)
        {
            return;
        }

        if (!string.Equals(OsduKind.EntityType(kind), WellLogEntityType, StringComparison.OrdinalIgnoreCase))
        {
            var keys = KeyPaths.Of(flow);
            throw new DeliveryException(
                $"{KeyPaths.Where(flow)}: the ddms route writes to the wellbore DDMS's well log collection (POST /ddms/v3/welllogs), which serves {WellLogEntityType}, "
                + $"and {keys.Name("render.mapping")} {flow.Render.Mapping} renders {kind}. For another collection, name its paths under "
                + $"{keys.Shared("target.protocolOptions")} (recordPath, dataPath, sessionPath, sessionDataPath, sessionCommitPath, verifyPath, deletePath).");
        }
    }
}
