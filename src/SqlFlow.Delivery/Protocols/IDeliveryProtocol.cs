using System.Text.Json.Nodes;
using SqlFlow.Delivery.Drops;

namespace SqlFlow.Delivery.Protocols;

/// <summary>Opens a record's payload chunks for streaming. Each open returns a fresh stream, so retries re-open the blob.</summary>
public interface IPayloadSource
{
    Task<IReadOnlyList<PayloadChunk>> ListChunksAsync(CancellationToken ct = default);

    Task<Stream> OpenAsync(PayloadChunk chunk, CancellationToken ct = default);
}

/// <summary>What one delivery attempt must do for one record.</summary>
public sealed record DeliveryWork
{
    public required string TargetId { get; init; }

    public required JsonObject Document { get; init; }

    public required bool DeliverMetadata { get; init; }

    public required bool DeliverPayload { get; init; }

    public IPayloadSource? Payload { get; init; }

    /// <summary>The last known OSDU version, when updating; null when creating.</summary>
    public long? ExistingVersion { get; init; }
}

public sealed record DeliveryOutcome
{
    public required bool MetadataDelivered { get; init; }

    public required bool PayloadDelivered { get; init; }

    /// <summary>The OSDU version after the write, when the response reported one.</summary>
    public long? TargetVersion { get; init; }

    public int ChunksSent { get; init; }

    public string? Detail { get; init; }
}

public sealed record VerifyResult(Ledger.VerifyOutcome Outcome, long? ObservedVersion, string? Detail);

/// <summary>
/// A named delivery protocol implemented in code and parameterised by the flow (design.md section 8.4). The core is
/// protocol independent: identity, rendering, change detection, the ledger and idempotency; only this varies.
/// </summary>
public interface IDeliveryProtocol
{
    DeliveryProtocol Kind { get; }

    Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default);

    /// <summary>Reads the record back and compares the observed version with the expected one.</summary>
    Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default);

    /// <summary>
    /// Removes the record from OSDU: a logical (revertible) delete by default, a physical purge when
    /// <paramref name="purge"/> is set. A record that is already gone is not an error.
    /// </summary>
    Task<DeleteOutcome> DeleteAsync(string targetId, bool purge, CancellationToken ct = default);

    /// <summary>Reads the record back as the target holds it, or null when the target has no such record.</summary>
    Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default);

    /// <summary>A reachability and credential check against the service's info endpoint, under the flow's auth.</summary>
    Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default);
}

/// <summary>What a probe found: whether the service answered, with which status, and the path it was asked on.</summary>
public sealed record ProbeOutcome(bool Reachable, int Status, string Detail, string Path);

public sealed record DeleteOutcome(bool Deleted, bool AlreadyGone, string Detail);
