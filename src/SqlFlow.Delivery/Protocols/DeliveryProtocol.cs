namespace SqlFlow.Delivery.Protocols;

/// <summary>
/// The closed vocabulary of OSDU delivery shapes (design.md section 8). Designed as a set so the four cohere;
/// <see cref="OsduRecord"/> and <see cref="OsduWellLog"/> are implemented, the other two are declared and
/// rejected at validation until a second kind makes them real.
/// </summary>
public enum DeliveryProtocol
{
    /// <summary>Plain record: one JSON document, upsert by id, batched arrays (storage service).</summary>
    OsduRecord,

    /// <summary>Record plus bulk: record, then binary payload, optionally via a session (wellbore DDMS).</summary>
    OsduWellLog,

    /// <summary>Record plus file: signed upload URL, upload, register metadata (file/dataset services). Not yet implemented.</summary>
    OsduFile,

    /// <summary>Manifest ingestion: assemble a manifest, trigger a DAG, poll (workflow service). Not yet implemented.</summary>
    OsduManifest,
}

public static class DeliveryProtocols
{
    public static bool IsImplemented(DeliveryProtocol protocol)
        => protocol is DeliveryProtocol.OsduRecord or DeliveryProtocol.OsduWellLog;

    public static bool CarriesPayload(DeliveryProtocol protocol)
        => protocol is DeliveryProtocol.OsduWellLog or DeliveryProtocol.OsduFile;
}
