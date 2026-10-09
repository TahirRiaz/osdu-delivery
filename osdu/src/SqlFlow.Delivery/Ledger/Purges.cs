using System.Globalization;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// What the ledger keeps of a record deleted from it after it was removed from OSDU
/// (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting removed records from the ledger): its key, what it
/// was, the last version an attempt of it named, how many attempts went with it, and who deleted it, when, under which
/// intervention.
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

/// <summary>
/// What deleting a whole ledger deleted (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting the ledger):
/// its records, each kept as one line of what it was, and what the ledger kept of its runs. Its activities stay, as the
/// whole audit trail does.
/// </summary>
public sealed record LedgerDeletion(int Records, int Submissions, int WorkBatches, int Leases, int Events, int Watermarks, int Reversals)
{
    /// <summary>The line the activity trail and the run log carry.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Records} record(s) deleted from the ledger with {Submissions} submission(s), {WorkBatches} work batch(es), {Watermarks} watermark(s), {Reversals} reversal(s), {Leases} lease(s) and {Events} lease event(s); the next run reads every row and delivers each as a new record");
}

/// <summary>
/// What one delivery ledger changed in OSDU after a moment (docs: osdu/docs/reference/flow/assertion.md, Records the
/// index may not list yet): the records it wrote, the records it took out of OSDU or put back at an earlier version and
/// still holds, and the records it deleted from itself that OSDU had held, with the latest of those changes and one
/// OSDU id of the ledger, which names the entity type its records are of. What an assertion run reads before it judges
/// the search index, which can take a while to list a change.
/// </summary>
public sealed record RecentOsduChange(Guid FlowId, string LedgerName, long Written, long Removed, long Deleted, DateTime LatestUtc, string? SampleTargetId);
