using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

public sealed partial class OsduLedger
{
    public async Task<IReadOnlyList<DeliveryKey>> PurgeRecordsAsync(
        Guid flowId, IReadOnlyList<DeliveryKey> keys, string actor, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        if (keys.Count == 0)
        {
            return [];
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var purged = new List<DeliveryKey>(keys.Count);
        await using var db = Open();
        foreach (var slice in keys.Distinct().Chunk(WriteSlice))
        {
            ct.ThrowIfCancellationRequested();
            purged.AddRange(await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.PurgeSliceAsync(db, partition, flowId, slice, actor, activityId, nowUtc, ct), ct).ConfigureAwait(false));
        }

        return purged;
    }

    public async Task<PurgedRecordState?> FindPurgedAsync(Guid flowId, DeliveryKey key, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        return await ReadAsync(
            db => db.DeliveryPurgedRecords.AsNoTracking()
                .Where(p => p.PartitionId == partition && p.FlowId == flowId && p.DeliveryKey == key.Value)
                .OrderByDescending(p => p.PurgedRecordId)
                .Select(p => new PurgedRecordState
                {
                    FlowId = p.FlowId,
                    DeliveryKey = new DeliveryKey(p.DeliveryKey),
                    SourceKey = p.SourceKey,
                    Label = p.Label,
                    TargetId = p.TargetId,
                    LastVersion = p.LastVersion,
                    Attempts = p.Attempts,
                    ActivityId = p.ActivityId,
                    PurgedBy = p.PurgedBy,
                    PurgedUtc = DateTime.SpecifyKind(p.PurgedUtc, DateTimeKind.Utc),
                })
                .FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
    }
}
