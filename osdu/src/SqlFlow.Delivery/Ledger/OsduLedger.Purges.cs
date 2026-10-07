using System.Globalization;
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
                () => SqlServerLedgerBulk.PurgeSliceAsync(db, partition, flowId, slice, actor, activityId, nowUtc, everyState: false, ct), ct).ConfigureAwait(false));
        }

        return purged;
    }

    public async Task<LedgerDeletion> DeleteLedgerAsync(Guid flowId, string actor, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);

        // A lease that ran out is settled first, so the records it held are no lease's and go with the rest.
        await RecoverExpiredLeasesAsync(flowId, nowUtc, ct).ConfigureAwait(false);

        // Every record, a slice to a transaction, walked in key order: a record a lease still holds is passed over rather
        // than read again, and the run state below then refuses to go while it is there.
        var records = 0;
        Guid? after = null;
        await using var db = Open();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var from = after;
            var page = await ReadAsync(
                read =>
                {
                    var query = read.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId);
                    if (from is { } cursor)
                    {
                        query = query.Where(r => r.DeliveryKey.CompareTo(cursor) > 0);
                    }

                    return query.OrderBy(r => r.DeliveryKey).Select(r => r.DeliveryKey).Take(WriteSlice).ToListAsync(ct);
                },
                ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            var slice = page.Select(k => new DeliveryKey(k)).ToList();
            records += (await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.PurgeSliceAsync(db, partition, flowId, slice, actor, activityId, nowUtc, everyState: true, ct), ct).ConfigureAwait(false)).Count;
            after = page[^1];
        }

        var state = await RetryDeadlockAsync(() => SqlServerLedgerBulk.DeleteRunStateAsync(db, partition, flowId, nowUtc, ct), ct).ConfigureAwait(false);
        if (state.Leased || state.RecordsLeft > 0)
        {
            throw new DeliveryException(string.Create(
                CultureInfo.InvariantCulture,
                $"The ledger was not deleted whole: {records} record(s) were deleted, but {(state.Leased ? "a worker still holds a lease on its records" : $"{state.RecordsLeft} record(s) a lease held are still in it")}, so its submissions, watermarks and the rest of its runs were left as they were. Delete it again once that work has ended."));
        }

        return new LedgerDeletion(records, state.Submissions, state.WorkBatches, state.Leases, state.Events, state.Watermarks, state.Reversals);
    }

    public async Task<IReadOnlyList<RecentOsduChange>> RecentOsduChangesAsync(string partition, DateTime sinceUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        await using var db = Open();
        return await RetryDeadlockAsync(() => SqlServerLedgerBulk.RecentChangesAsync(db, id, sinceUtc, ct), ct).ConfigureAwait(false);
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
