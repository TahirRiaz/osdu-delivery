using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Reversals;

/// <summary>
/// What a reversal can do on a flow's route (docs/reversal-plan.md): write a record back as OSDU held it at an earlier
/// version, and remove a record the reversed run created, each with what it calls or why it cannot. Worked out from the
/// flow alone, as <see cref="RemovalEndpoints"/> are, so a preview shows it before anything is asked of OSDU, and the run
/// passes over what the route cannot do with the same reason.
/// </summary>
/// <param name="Restore">The calls a restore makes, or why the route cannot restore, in brackets.</param>
/// <param name="Remove">The call a removal makes, or why the route cannot remove reversibly, in brackets.</param>
/// <param name="RestoreRefusal">Why the route cannot restore, or null when it can.</param>
/// <param name="RemoveRefusal">Why the route cannot remove reversibly, or null when it can.</param>
public sealed record ReversalRoute(string Restore, string Remove, string? RestoreRefusal, string? RemoveRefusal)
{
    /// <summary>Whether the route writes an earlier version back.</summary>
    public bool Restores => RestoreRefusal is null;

    /// <summary>Whether the route removes a record reversibly.</summary>
    public bool Removes => RemoveRefusal is null;

    /// <summary>What a reversal can do on <paramref name="flow"/>'s route for records of <paramref name="kind"/>, the kind its mapping renders (null while it is not known).</summary>
    public static ReversalRoute Of(FlowDefinition flow, string? kind)
        => OfEntityType(flow, string.IsNullOrWhiteSpace(kind) ? null : OsduKind.EntityType(kind));

    /// <summary>What a reversal can do on <paramref name="flow"/>'s route for the record <paramref name="targetId"/>, by the entity type its id names.</summary>
    public static ReversalRoute ForRecord(FlowDefinition flow, string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        return OfEntityType(flow, DdmsRouting.EntityTypeOf(targetId));
    }

    private static ReversalRoute OfEntityType(FlowDefinition flow, string? entityType)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var removal = RemovalEndpoints.OfEntityType(flow, entityType);
        var removeRefusal = removal.Record.StartsWith('(') ? Unbracket(removal.Record) : null;
        var remove = removeRefusal is null ? $"{removal.RecordMethod} {removal.Record}" : removal.Record;
        var restoreRefusal = RefusalToRestore(flow, entityType);
        var options = flow.Target.ProtocolOptions;
        var restore = restoreRefusal is not null
            ? $"({restoreRefusal})"
            : flow.Target.Protocol == DeliveryProtocol.Ddms
                ? $"GET {OsduRecordProtocol.DefaultVerifyPath}/{{version}}, then PUT {OsduRecordProtocol.DefaultRecordPath} (the storage service, past the DDMS)"
                : $"GET {options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath}/{{version}}, then {(options.RecordMethod ?? "PUT").ToUpperInvariant()} {options.RecordPath ?? OsduRecordProtocol.DefaultRecordPath}";
        return new ReversalRoute(restore, remove, restoreRefusal, removeRefusal);
    }

    /// <summary>Why the route cannot write an earlier version of a record of <paramref name="entityType"/> back, or null when it can.</summary>
    private static string? RefusalToRestore(FlowDefinition flow, string? entityType)
    {
        switch (flow.Target.Protocol)
        {
            case DeliveryProtocol.Storage:
                var read = flow.Target.ProtocolOptions.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath;
                return RecordWriter.VersionsPath(read) is null
                    ? $"the flow reads its records at {read}, which is not the storage service's record path, so their earlier versions cannot be read"
                    : null;

            case DeliveryProtocol.Ddms:
                return string.IsNullOrWhiteSpace(entityType)
                    ? "the collection serving the records' entity type is known once the flow's mapping is synced"
                    : DdmsRestoreRefusalOf(DdmsRouting.Of(flow), entityType);

            case DeliveryProtocol.Dspdm:
                return "DSPDM keeps no earlier versions of a row";

            case DeliveryProtocol.Etp:
                return "the Reservoir DDMS keeps no earlier versions of an object";

            default:
                return $"the {DeliveryProtocols.Name(flow.Target.Protocol)} route sends files, datasets or a workflow's run beside the record, which a version of the record does not bring back";
        }
    }

    /// <summary>
    /// Why the ddms route cannot write the record <paramref name="targetId"/> back at an earlier version, or null when it
    /// can: a Wellbore DDMS record is a storage record whose version names its bulk data, written back through the storage
    /// service under a platform endpoint. The protocol asks it record by record, by the record's entity type.
    /// </summary>
    public static string? DdmsRestoreRefusal(DdmsRouting routing, string targetId)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        return DdmsRouting.EntityTypeOf(targetId) is { } entityType
            ? DdmsRestoreRefusalOf(routing, entityType)
            : $"the record id '{targetId}' names no entity type, so the ddms route cannot tell which DDMS keeps it";
    }

    private static string? DdmsRestoreRefusalOf(DdmsRouting routing, string entityType)
    {
        DdmsRecordPaths paths;
        try
        {
            paths = routing.For(entityType);
        }
        catch (DeliveryException ex)
        {
            return $"{entityType} records cannot be routed: {ex.Message}";
        }

        if (paths.Shape != DdmsShape.WellboreDdmsV3)
        {
            return $"{entityType} records go to a DDMS of the {DdmsCatalog.ShapeName(paths.Shape)} shape, which keeps data of its own beside the record that a version of the record does not bring back";
        }

        return routing.PlatformEndpoint
            ? null
            : "the flow's endpoint is the DDMS itself, so the storage service that keeps the record's versions is not under it; make the endpoint the platform root and give the DDMS its root under target.ddms";
    }

    private static string Unbracket(string text) => text.StartsWith('(') && text.EndsWith(')') ? text[1..^1] : text;
}
