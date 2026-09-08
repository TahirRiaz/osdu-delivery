namespace SqlFlow.Delivery.Protocols;

/// <summary>
/// The closed vocabulary of OSDU delivery shapes (design.md section 8). Designed as a set so the four cohere: a flow
/// names one, the factory builds it, and the engine never references a concrete protocol beyond that.
/// </summary>
public enum DeliveryProtocol
{
    /// <summary>Plain record: one JSON document, upsert by id, batched arrays (storage service).</summary>
    OsduRecord,

    /// <summary>Record plus bulk: record, then binary payload, optionally via a session (wellbore DDMS).</summary>
    OsduWellLog,

    /// <summary>
    /// Record plus files: a signed upload location per file, the file streamed to it, its dataset record registered,
    /// then the record written with its dataset list (file and storage services).
    /// </summary>
    OsduFile,

    /// <summary>
    /// Manifest ingestion: the files uploaded, one manifest per batch handed to the ingestion workflow, the run
    /// polled, the records read back from storage (file, workflow and storage services).
    /// </summary>
    OsduManifest,
}

public static class DeliveryProtocols
{
    /// <summary>Protocols that stream a payload set from the drop alongside the record.</summary>
    public static bool CarriesPayload(DeliveryProtocol protocol)
        => protocol is DeliveryProtocol.OsduWellLog or DeliveryProtocol.OsduFile or DeliveryProtocol.OsduManifest;
}
