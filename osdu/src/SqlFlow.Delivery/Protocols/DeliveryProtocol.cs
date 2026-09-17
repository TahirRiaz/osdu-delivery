namespace SqlFlow.Delivery.Protocols;

/// <summary>
/// The closed vocabulary of OSDU delivery shapes (design.md section 8; docs/interfaces-design.md section 5.4): the
/// route types. Designed as a set so they cohere: a flow names one, the factory builds it, and the engine never
/// references a concrete protocol beyond that.
/// </summary>
public enum DeliveryProtocol
{
    /// <summary>Plain record: one JSON document, upsert by id, batched arrays (storage service).</summary>
    OsduRecord,

    /// <summary>
    /// The ddms route: the record through the collection of the DDMS serving its entity type, then, on a collection that
    /// keeps bulk data, its bulk data, optionally via a session (docs/interfaces-design.md section 5.4). Named after the
    /// well log collection it first served.
    /// </summary>
    OsduWellLog,

    /// <summary>
    /// Record plus files: a signed upload location per file, the file streamed to it, its dataset record registered,
    /// then the record written with its dataset list (file and storage services).
    /// </summary>
    OsduFile,

    /// <summary>
    /// Manifest ingestion: the files uploaded, one manifest per batch handed to the ingestion workflow (inline, or stored
    /// as a dataset and handed over by reference), the run polled, the records read back from storage (file, dataset,
    /// workflow and storage services).
    /// </summary>
    OsduManifest,

    /// <summary>
    /// The dataset route: files stored where the Dataset service says and registered with it, a record of a dataset kind
    /// as that dataset under its own id, any other record referring to one dataset holding its files (dataset and storage
    /// services).
    /// </summary>
    OsduDataset,

    /// <summary>The composed route of files and DDMS bulk data: the files registered, the record through its DDMS referring to them, then the bulk data.</summary>
    OsduFileAndDdms,

    /// <summary>The composed route of a manifest and DDMS bulk data: the records by manifest, the run settled, then each record's bulk data through its DDMS.</summary>
    OsduManifestAndDdms,

    /// <summary>
    /// The workflow route: the record written or registered, its inputs registered, a named workflow triggered with a
    /// payload built from the interface's declaration (in stages when the workflow only translates), the runs polled and
    /// what they created found and read back (dataset, workflow, storage and search services).
    /// </summary>
    OsduWorkflow,

    /// <summary>
    /// The dspdm route: rows of the Production DDMS core service's business objects, not OSDU records. Each row is found
    /// again by a unique key of its business object and saved under the primary key DSPDM gave it, and read back and
    /// deleted by that key (osdu/specs/production-dspdm/INTEGRATION.md).
    /// </summary>
    OsduDspdm,

    /// <summary>
    /// The etp route: Energistics data objects in a dataspace of the Reservoir DDMS, written over ETP 1.2 on a WebSocket
    /// rather than through an OSDU service. Each record is one object, identified by the uuid and type its own XML
    /// carries, delivered with the arrays it names inside one transaction per dataspace
    /// (osdu/specs/reservoir-ddms/INTEGRATION.md).
    /// </summary>
    OsduEtp,
}

public static class DeliveryProtocols
{
    /// <summary>Protocols that stream a payload alongside the record. The workflow route's run counts as its payload.</summary>
    public static bool CarriesPayload(DeliveryProtocol protocol)
        => protocol is not (DeliveryProtocol.OsduRecord or DeliveryProtocol.OsduDspdm);

    /// <summary>Protocols whose records are not OSDU storage records, and so have no OSDU id, version or soft delete.</summary>
    public static bool OutsideStorage(DeliveryProtocol protocol)
        => protocol is DeliveryProtocol.OsduDspdm or DeliveryProtocol.OsduEtp;

    /// <summary>Protocols that reach a DDMS, and so read the flow's <c>target.ddms</c>.</summary>
    public static bool ReachesDdms(DeliveryProtocol protocol)
        => protocol is DeliveryProtocol.OsduWellLog or DeliveryProtocol.OsduFileAndDdms or DeliveryProtocol.OsduManifestAndDdms;
}
