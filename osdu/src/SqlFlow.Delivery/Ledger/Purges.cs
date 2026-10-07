using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// What the ledger keeps of a record deleted from it after it was removed from OSDU (docs/ledger.md, Deleting a removed
/// record from the ledger): its key, what it was, the last version an attempt of it named, how many attempts went with it,
/// and who deleted it, when, under which intervention.
/// </summary>
public sealed record PurgedRecordState
{
    public required Guid FlowId { get; init; }

    public required DeliveryKey DeliveryKey { get; init; }

    public required string SourceKey { get; init; }

    public string? Label { get; init; }

    /// <summary>The OSDU id it was delivered and removed under; null for a record that never had one.</summary>
    public string? TargetId { get; init; }

    public long? LastVersion { get; init; }

    public required int Attempts { get; init; }

    public long? ActivityId { get; init; }

    public required string PurgedBy { get; init; }

    public required DateTime PurgedUtc { get; init; }
}
