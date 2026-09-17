using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// What a flow's route can deliver, checked against the kind its mapping renders before anything is sent. The routes that
/// reach a DDMS send a record to the collection of the DDMS serving its entity type (<see cref="DdmsRouting"/>): a kind
/// no DDMS the flow reaches serves could never be written, verified or removed through it, and bulk data sent to a
/// collection that holds records alone would never land (osdu/specs/wellbore-ddms/INTEGRATION.md section 2). The
/// Dataset service registers records of the dataset group only (osdu/specs/core/INTEGRATION.md section 2.5), so a route
/// that registers the record itself needs a dataset kind, and the file route, whose service mints its own dataset ids,
/// cannot deliver one.
/// </summary>
public static class RouteChecks
{
    /// <summary>The route names a flow reads: storage, file, dataset, manifest, ddms, fileAndDdms, manifestAndDdms and workflow.</summary>
    public static string Name(DeliveryProtocol protocol) => protocol switch
    {
        DeliveryProtocol.OsduRecord => "storage",
        DeliveryProtocol.OsduFile => "file",
        DeliveryProtocol.OsduDataset => "dataset",
        DeliveryProtocol.OsduManifest => "manifest",
        DeliveryProtocol.OsduWellLog => "ddms",
        DeliveryProtocol.OsduFileAndDdms => "fileAndDdms",
        DeliveryProtocol.OsduManifestAndDdms => "manifestAndDdms",
        DeliveryProtocol.OsduWorkflow => "workflow",
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

        var keys = KeyPaths.Of(flow);
        var route = Name(flow.Target.Protocol);
        var mapping = $"{keys.Name("render.mapping")} {flow.Render.Mapping}";
        var entityType = OsduKind.EntityType(kind)
            ?? throw new DeliveryException(
                $"{KeyPaths.Where(flow)}: {mapping} renders {kind}, which names no entity type, so the {route} route cannot tell where it goes.");
        var dataset = Protocols.DatasetService.IsDatasetType(entityType);
        switch (flow.Target.Protocol)
        {
            case DeliveryProtocol.OsduFile when dataset:
                throw new DeliveryException(
                    $"{KeyPaths.Where(flow)}: {mapping} renders {kind}, a dataset, and the file route registers each file as a dataset of its own and writes the record beside them. "
                    + $"Deliver a dataset with its files by the dataset route ({keys.Shared("route")}: dataset), which registers it under its own id.");
            case DeliveryProtocol.OsduDataset when !dataset && !Protocols.DatasetService.IsDatasetType(Identity.TargetId.EntityTypeFromKind(flow.Target.ProtocolOptions.DatasetKind)):
                throw new DeliveryException(
                    $"{KeyPaths.Where(flow)}: {mapping} renders {kind}, which is not a dataset, so its files are registered as a dataset of {keys.Shared("target.protocolOptions.datasetKind")}, "
                    + $"and '{flow.Target.ProtocolOptions.DatasetKind}' is not a dataset kind.");
            case DeliveryProtocol.OsduWorkflow when flow.Target.Workflow is { Anchor: WorkflowAnchor.Dataset } && !dataset:
                throw new DeliveryException(
                    $"{KeyPaths.Where(flow)}: {mapping} renders {kind}, which is not a dataset, and the workflow is anchored on a dataset registered with its files. "
                    + "Render the dataset the workflow reads (a descriptor such as dataset--File.Generic), or anchor the workflow on a storage record (anchor: storage).");
        }

        if (!DeliveryProtocols.ReachesDdms(flow.Target.Protocol))
        {
            return;
        }

        var sendsBulk = flow.Target.Protocol != DeliveryProtocol.OsduWellLog || Planning.Planner.PayloadName(flow) is not null;
        if ((routing ?? DdmsRouting.Of(flow)).Problem(entityType, sendsBulk) is { } problem)
        {
            throw new DeliveryException($"{problem} ({mapping} renders {kind}.)");
        }
    }
}
