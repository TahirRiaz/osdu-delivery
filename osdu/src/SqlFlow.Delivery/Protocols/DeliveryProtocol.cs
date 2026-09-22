namespace SqlFlow.Delivery.Protocols;

/// <summary>
/// The closed vocabulary of OSDU delivery shapes (design.md section 8; docs/interfaces-design.md section 5.4): the
/// route types. Designed as a set so they cohere: a flow names one, the factory builds it, and the engine never
/// references a concrete protocol beyond that. The names are the route names a flow writes (<c>route:</c>,
/// <c>target.protocol</c>), the names errors quote and the names the ledger's <c>Route</c> column holds, so one
/// vocabulary runs from the flow document to the GUI.
/// </summary>
public enum DeliveryProtocol
{
    /// <summary>Plain record: one JSON document, upsert by id, batched arrays (storage service).</summary>
    Storage,

    /// <summary>
    /// Record plus files: a signed upload location per file, the file streamed to it, its dataset record registered,
    /// then the record written with its dataset list (file and storage services).
    /// </summary>
    File,

    /// <summary>
    /// The dataset route: files stored where the Dataset service says and registered with it, a record of a dataset kind
    /// as that dataset under its own id, any other record referring to one dataset holding its files (dataset and storage
    /// services).
    /// </summary>
    Dataset,

    /// <summary>
    /// Manifest ingestion: the files uploaded, one manifest per batch handed to the ingestion workflow (inline, or stored
    /// as a dataset and handed over by reference), the run polled, the records read back from storage (file, dataset,
    /// workflow and storage services).
    /// </summary>
    Manifest,

    /// <summary>
    /// The ddms route: the record through the collection of the DDMS serving its entity type, then, on a collection that
    /// keeps bulk data, its bulk data, optionally via a session (docs/interfaces-design.md section 5.4). One route for
    /// every DDMS, not for well logs alone: the shape is picked from the record's entity type.
    /// </summary>
    Ddms,

    /// <summary>The composed route of files and DDMS bulk data: the files registered, the record through its DDMS referring to them, then the bulk data.</summary>
    FileAndDdms,

    /// <summary>The composed route of a manifest and DDMS bulk data: the records by manifest, the run settled, then each record's bulk data through its DDMS.</summary>
    ManifestAndDdms,

    /// <summary>
    /// The workflow route: the record written or registered, its inputs registered, a named workflow triggered with a
    /// payload built from the interface's declaration (in stages when the workflow only translates), the runs polled and
    /// what they created found and read back (dataset, workflow, storage and search services).
    /// </summary>
    Workflow,

    /// <summary>
    /// The dspdm route: rows of the Production DDMS core service's business objects, not OSDU records. Each row is found
    /// again by a unique key of its business object and saved under the primary key DSPDM gave it, and read back and
    /// deleted by that key (osdu/specs/production-dspdm/INTEGRATION.md).
    /// </summary>
    Dspdm,

    /// <summary>
    /// The etp route: Energistics data objects in a dataspace of the Reservoir DDMS, written over ETP 1.2 on a WebSocket
    /// rather than through an OSDU service. Each record is one object, identified by the uuid and type its own XML
    /// carries, delivered with the arrays it names inside one transaction per dataspace
    /// (osdu/specs/reservoir-ddms/INTEGRATION.md).
    /// </summary>
    Etp,
}

public static class DeliveryProtocols
{
    /// <summary>
    /// Every route name, indexed by <see cref="DeliveryProtocol"/>: the enum member with its first letter lowered, built
    /// once. The names are the enum's own, so this list cannot drift from the vocabulary a flow writes.
    /// </summary>
    private static readonly string[] Names = Enum.GetNames<DeliveryProtocol>()
        .Select(n => char.ToLowerInvariant(n[0]) + n[1..])
        .ToArray();

    /// <summary>
    /// The route name a flow reads and writes: storage, file, dataset, manifest, ddms, fileAndDdms, manifestAndDdms,
    /// workflow, dspdm and etp. It is what <c>route:</c> and <c>target.protocol</c> name, what errors quote, and what the
    /// ledger's <c>Route</c> column holds, so one vocabulary runs from the flow document to the GUI.
    /// </summary>
    public static string Name(DeliveryProtocol protocol)
    {
        var index = (int)protocol;
        return index >= 0 && index < Names.Length
            ? Names[index]
            : throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "not a delivery protocol");
    }

    /// <summary>Protocols that stream a payload alongside the record. The workflow route's run counts as its payload.</summary>
    public static bool CarriesPayload(DeliveryProtocol protocol)
        => protocol is not (DeliveryProtocol.Storage or DeliveryProtocol.Dspdm);

    /// <summary>Protocols whose records are not OSDU storage records, and so have no OSDU id, version or soft delete.</summary>
    public static bool OutsideStorage(DeliveryProtocol protocol)
        => protocol is DeliveryProtocol.Dspdm or DeliveryProtocol.Etp;

    /// <summary>Protocols that reach a DDMS, and so read the flow's <c>target.ddms</c>.</summary>
    public static bool ReachesDdms(DeliveryProtocol protocol)
        => protocol is DeliveryProtocol.Ddms or DeliveryProtocol.FileAndDdms or DeliveryProtocol.ManifestAndDdms;
}
