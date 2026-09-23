using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// Leases, the events a worker appends under them, and the snapshot reads (design.md section 16.2, ledger.md "Leasing").
// A claim is one lease row, and the records it holds carry its token. While a worker delivers it writes nothing to the
// record table: it renews its one row and appends what it learns, and the lease applies that to the records when the
// worker checkpoints or closes it, or when the lease runs out and the next claim of its flow recovers it.
public sealed partial class OsduLedger
{
    /// <summary>How long a recovery holds the lease it took over; a recovery that dies is itself recovered after that.</summary>
    internal static readonly TimeSpan RecoveryHold = TimeSpan.FromMinutes(5);

    /// <summary>The most expired leases one recovery pass reads.</summary>
    private const int RecoveryPage = 100;

    /// <summary>A token is its worker's name, a slash and a 32-character id.</summary>
    private const int TokenSuffixLength = 33;

    private const string StepEvent = "step";

    private const string CompletionEvent = "completion";

    private const string StoppedNote = "the worker stopped mid-attempt and released the record for the next pass";

    private const string ExpiredNote = "the lease expired mid-attempt (the worker stopped) and the record was requeued";

    /// <summary>The owner a lease this process recovers is held under while the recovery applies and hands back its work.</summary>
    private static readonly string Recoverer = Truncate($"{Environment.MachineName}/{Environment.ProcessId}/recovery", DeliveryModel.MaxLeaseTokenLength)!;

    public async Task<ClaimedWorkBatch?> ClaimWorkBatchAsync(
        Guid flowId, Guid? submissionId, string owner, TimeSpan lease, DateTime nowUtc, Guid? runId = null, WaitRules? waits = null, CancellationToken ct = default)
    {
        var token = NewToken(owner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        await RecoverExpiredLeasesAsync(flowId, nowUtc, ct).ConfigureAwait(false);
        var queued = StatusText.Of(WorkBatchStatus.Queued);
        var pending = StatusText.Of(RecordStatus.Pending);
        var delivering = StatusText.Of(RecordStatus.Delivering);

        // Losing the race for a candidate is ordinary; the next candidate is tried a few times before answering "nothing now".
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var candidate = await ReadAsync(
                db => db.DeliveryWorkBatches
                    .Where(b => b.FlowId == flowId && (submissionId == null || b.SubmissionId == submissionId) && b.Status == queued)
                    .OrderBy(b => b.CreatedUtc)
                    .ThenBy(b => b.Index)
                    .Select(b => new { b.SubmissionId, b.Index })
                    .FirstOrDefaultAsync(ct),
                ct).ConfigureAwait(false);
            if (candidate is null)
            {
                return null;
            }

            var held = new DeliveryLease
            {
                Token = token,
                FlowId = flowId,
                SubmissionId = candidate.SubmissionId,
                WorkBatch = candidate.Index,
                Owner = OwnerOf(token),
                RunId = runId,
                AcquiredUtc = nowUtc,
                ExpiresUtc = nowUtc + lease,
            };
            if (!await RetryDeadlockAsync(() => TakeBatchAsync(held, ct), ct).ConfigureAwait(false))
            {
                continue;
            }

            // A due record of the batch that refers to a record of the ledger still to land is left waiting, and the lease
            // does not hold it; the batch counts it.
            var batchSubmission = candidate.SubmissionId;
            var batchIndex = candidate.Index;
            var waiting = await LeaveWaitingAsync(
                flowId,
                db => db.DeliveryRecords.Where(r => r.LastSubmissionId == batchSubmission && r.WorkBatch == batchIndex && r.FlowId == flowId
                    && r.Status == pending && (r.NextAttemptUtc == null || r.NextAttemptUtc <= nowUtc)),
                waits ?? WaitRules.WaitForAll,
                nowUtc,
                ct).ConfigureAwait(false);
            if (waiting.Count > 0)
            {
                var found = waiting.Count;
                await using var counting = Open();
                await RetryDeadlockAsync(
                    () => counting.DeliveryWorkBatches
                        .Where(b => b.SubmissionId == batchSubmission && b.Index == batchIndex)
                        .ExecuteUpdateAsync(s => s.SetProperty(b => b.Waiting, b => b.Waiting + found), ct),
                    ct).ConfigureAwait(false);
            }

            // The lease holds the batch's records that are due: the retry claim never sees them, and a lease that runs out
            // hands them back. They are marked a slice at a time however large the batch, found through the batch index,
            // and a record claimed elsewhere meanwhile is no longer due.
            await using (var db = Open())
            {
                await WriteEachAsync(
                    db.DeliveryRecords.Where(r => r.LastSubmissionId == candidate.SubmissionId && r.WorkBatch == candidate.Index && r.FlowId == flowId),
                    db.DeliveryRecords.Where(r => r.FlowId == flowId && r.LastSubmissionId == candidate.SubmissionId && r.WorkBatch == candidate.Index
                        && r.Status == pending && (r.NextAttemptUtc == null || r.NextAttemptUtc <= nowUtc)),
                    slice => slice.ExecuteUpdateAsync(
                        s => s
                            .SetProperty(r => r.Status, delivering)
                            .SetProperty(r => r.LeaseOwner, token)
                            .SetProperty(r => r.AttemptCount, r => r.AttemptCount + 1)
                            .SetProperty(r => r.UpdatedUtc, nowUtc),
                        ct),
                    ct).ConfigureAwait(false);
            }

            var batch = await ReadAsync(
                db => db.DeliveryWorkBatches.FirstAsync(b => b.SubmissionId == candidate.SubmissionId && b.Index == candidate.Index, ct),
                ct).ConfigureAwait(false);
            var state = ToState(held);
            return new ClaimedWorkBatch(ToState(batch) with { LeaseExpiresUtc = state.ExpiresUtc }, state, await LeasedAsync(state, ct).ConfigureAwait(false))
            {
                Waiting = waiting,
            };
        }

        return null;
    }

    /// <summary>Takes a queued batch under a new lease: the lease row and the batch's claim commit together, or neither does.</summary>
    private async Task<bool> TakeBatchAsync(DeliveryLease lease, CancellationToken ct)
    {
        var queued = StatusText.Of(WorkBatchStatus.Queued);
        var running = StatusText.Of(WorkBatchStatus.Running);
        var submissionId = lease.SubmissionId;
        var index = lease.WorkBatch;
        var token = lease.Token;
        var runId = lease.RunId;
        var started = lease.AcquiredUtc;
        await using var db = Open();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            db.DeliveryLeases.Add(Copy(lease));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var won = await db.DeliveryWorkBatches
                .Where(b => b.SubmissionId == submissionId && b.Index == index && b.Status == queued)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(b => b.Status, running)
                        .SetProperty(b => b.LeaseOwner, token)
                        .SetProperty(b => b.RunId, b => runId ?? b.RunId)
                        .SetProperty(b => b.StartedUtc, b => b.StartedUtc ?? started),
                    ct)
                .ConfigureAwait(false);
            if (won == 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    public async Task<ClaimedRecords> ClaimAsync(
        Guid flowId, Guid? submissionId, string owner, int max, TimeSpan lease, DateTime nowUtc, Guid? runId = null, WaitRules? waits = null, CancellationToken ct = default)
    {
        var token = NewToken(owner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        await RecoverExpiredLeasesAsync(flowId, nowUtc, ct).ConfigureAwait(false);
        var pending = StatusText.Of(RecordStatus.Pending);
        var delivering = StatusText.Of(RecordStatus.Delivering);
        var take = Math.Clamp(max, 1, ChunkSize);
        var candidates = await ReadAsync(
            db => db.DeliveryRecords
                .Where(r => r.FlowId == flowId
                    && (submissionId == null || r.LastSubmissionId == submissionId)
                    && r.Status == pending
                    && (r.NextAttemptUtc == null || r.NextAttemptUtc <= nowUtc))
                .OrderBy(r => r.NextAttemptUtc)
                .ThenBy(r => r.UpdatedUtc)
                .Select(r => r.DeliveryKey)
                .Take(take)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return ClaimedRecords.None;
        }

        // A candidate that refers to a record of the ledger still to land is left waiting rather than claimed.
        var waiting = await LeaveWaitingAsync(
            flowId,
            db => db.DeliveryRecords.Where(r => r.FlowId == flowId && candidates.Contains(r.DeliveryKey) && r.Status == pending
                && (r.NextAttemptUtc == null || r.NextAttemptUtc <= nowUtc)),
            waits ?? WaitRules.WaitForAll,
            nowUtc,
            ct).ConfigureAwait(false);
        if (waiting.Count > 0)
        {
            var left = waiting.Select(w => w.DeliveryKey.Value).ToHashSet();
            candidates = candidates.Where(k => !left.Contains(k)).ToList();
            if (candidates.Count == 0)
            {
                return ClaimedRecords.None with { Waiting = waiting };
            }
        }

        var held = new DeliveryLease
        {
            Token = token,
            FlowId = flowId,
            SubmissionId = submissionId,
            Owner = OwnerOf(token),
            RunId = runId,
            AcquiredUtc = nowUtc,
            ExpiresUtc = nowUtc + lease,
        };
        await RetryDeadlockAsync(() => AddLeaseAsync(held, ct), ct).ConfigureAwait(false);

        // The claim is one statement, and a record it leased is no longer pending, so running it again after a deadlock
        // takes what the first run would have.
        int claimed;
        await using (var db = Open())
        {
            claimed = await RetryDeadlockAsync(
                () => db.DeliveryRecords
                    .Where(r => r.FlowId == flowId
                        && candidates.Contains(r.DeliveryKey)
                        && r.Status == pending
                        && (r.NextAttemptUtc == null || r.NextAttemptUtc <= nowUtc))
                    .ExecuteUpdateAsync(
                        s => s
                            .SetProperty(r => r.Status, delivering)
                            .SetProperty(r => r.LeaseOwner, token)
                            .SetProperty(r => r.AttemptCount, r => r.AttemptCount + 1)
                            .SetProperty(r => r.UpdatedUtc, nowUtc),
                        ct),
                ct).ConfigureAwait(false);
        }

        if (claimed == 0)
        {
            await RetryDeadlockAsync(() => DeleteLeaseAsync(token, ct), ct).ConfigureAwait(false);
            return ClaimedRecords.None with { Waiting = waiting };
        }

        var state = ToState(held);
        return new ClaimedRecords(state, await LeasedAsync(state, ct).ConfigureAwait(false)) { Waiting = waiting };
    }

    public async Task<bool> RenewLeaseAsync(string token, TimeSpan lease, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        var owner = OwnerOf(token);
        var expires = nowUtc + lease;
        await using var db = Open();
        // A lease another worker took over after it ran out names that worker now, and is not this worker's to renew.
        var renewed = await RetryDeadlockAsync(
            () => db.DeliveryLeases
                .Where(l => l.Token == token && l.Owner == owner)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresUtc, expires), ct),
            ct).ConfigureAwait(false);
        return renewed > 0;
    }

    public async Task AppendAsync(Guid flowId, string token, LeaseAppend append, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(append);
        foreach (var completion in append.Completions)
        {
            if (completion.Attempt.DeliveryKey != completion.DeliveryKey)
            {
                throw new ArgumentException(
                    $"The completion of record {completion.DeliveryKey} carries an attempt of record {completion.Attempt.DeliveryKey}; an attempt belongs to the record it completes.",
                    nameof(append));
            }
        }

        foreach (var step in append.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.DocumentRef) || string.IsNullOrWhiteSpace(step.StepJson))
            {
                throw new ArgumentException(
                    $"The step of record {step.DeliveryKey} names no pending document or no steps; a step belongs to the document the record was claimed with.",
                    nameof(append));
            }
        }

        if (append.Steps.Count == 0 && append.Completions.Count == 0)
        {
            return;
        }

        // Every try belongs to a record of this flow's ledger, or its attempt would be history no record owns.
        var keys = append.Steps.Select(s => s.DeliveryKey.Value).Concat(append.Completions.Select(c => c.DeliveryKey.Value)).Distinct().ToList();
        foreach (var chunk in keys.Chunk(LookupChunk))
        {
            var wanted = chunk.ToList();
            var found = await ReadAsync(db => db.DeliveryRecords.Where(r => r.FlowId == flowId && wanted.Contains(r.DeliveryKey)).Select(r => r.DeliveryKey).ToListAsync(ct), ct).ConfigureAwait(false);
            if (found.Count < wanted.Count)
            {
                var missing = wanted.Except(found).First();
                throw new DeliveryException($"Record {missing} is not in the ledger of flow {flowId:D}, so nothing of its delivery was written.");
            }
        }

        // A record's steps come before its outcome: a try reports its steps, then ends.
        var events = append.Steps.Select(s => ToEvent(flowId, token, s))
            .Concat(append.Completions.Select(c => ToEvent(flowId, token, c)))
            .ToList();
        var attempts = append.Completions.Select(c => ToEntity(flowId, c.Attempt)).ToList();
        await using var db = Open();

        // A deadlock rolls the whole append back, attempts included, and the append is written again.
        await RetryDeadlockAsync(() => SqlServerLedgerBulk.AppendAsync(db, attempts, events, ct), ct).ConfigureAwait(false);
    }

    public async Task<LeaseApplied> CheckpointLeaseAsync(string token, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return new LeaseApplied(await ApplyEventsAsync(token, nowUtc, ct).ConfigureAwait(false), 0);
    }

    public Task<LeaseApplied> CloseLeaseAsync(string token, LeaseClosing closing, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(closing);
        return CloseAsync(token, closing, nowUtc, OwnerOf(token), ct);
    }

    /// <summary>
    /// Closes a lease held by <paramref name="owner"/>. The events appended under it are applied whoever holds it; the
    /// records, the batch and the lease row are settled only by its holder, so a worker whose lease was recovered
    /// meanwhile leaves them to the recovery.
    /// </summary>
    private async Task<LeaseApplied> CloseAsync(string token, LeaseClosing closing, DateTime nowUtc, string owner, CancellationToken ct)
    {
        var lease = await ReadAsync(db => db.DeliveryLeases.FirstOrDefaultAsync(l => l.Token == token, ct), ct).ConfigureAwait(false);
        var applied = await ApplyEventsAsync(token, nowUtc, ct).ConfigureAwait(false);
        if (lease is null || !string.Equals(lease.Owner, owner, StringComparison.Ordinal))
        {
            return new LeaseApplied(applied, 0);
        }

        var released = await ReleaseHeldAsync(token, closing.End, nowUtc, ct).ConfigureAwait(false);
        if (lease is { SubmissionId: { } submissionId, WorkBatch: { } index })
        {
            await SettleBatchAsync(submissionId, index, token, closing, nowUtc, ct).ConfigureAwait(false);
        }

        await RetryDeadlockAsync(() => DeleteLeaseAsync(token, ct), ct).ConfigureAwait(false);
        return new LeaseApplied(applied, released);
    }

    public async Task<int> RecoverExpiredLeasesAsync(Guid flowId, DateTime nowUtc, CancellationToken ct = default)
    {
        var settled = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var expired = await ReadAsync(
                db => db.DeliveryLeases
                    .Where(l => l.FlowId == flowId && l.ExpiresUtc < nowUtc)
                    .OrderBy(l => l.ExpiresUtc)
                    .Select(l => l.Token)
                    .Take(RecoveryPage)
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            foreach (var token in expired)
            {
                // Taking the lease over first means two recoveries never settle the same lease, and a worker that was only
                // slow can no longer renew it.
                if (await TakeOverAsync(token, nowUtc, ct).ConfigureAwait(false))
                {
                    var closed = await CloseAsync(token, new LeaseClosing { End = LeaseEnd.Expired }, nowUtc, Recoverer, ct).ConfigureAwait(false);
                    settled += closed.Applied + closed.Released;
                }
            }

            if (expired.Count < RecoveryPage)
            {
                return settled + await ApplyOrphanedEventsAsync(flowId, nowUtc, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Applies a flow's events whose lease is gone. A worker whose lease was recovered while it stalled appends what it
    /// still finishes and applies that when it closes; one that stops in between, or whose close fails, leaves them
    /// behind. Every other event is applied within a renewal of its lease, so only events older than a recovery's hold
    /// are looked at, through the flow's index on the time they were appended. Applying them is safe whenever it
    /// happens: a record another lease holds is left to that lease.
    /// </summary>
    private async Task<int> ApplyOrphanedEventsAsync(Guid flowId, DateTime nowUtc, CancellationToken ct)
    {
        var appendedBefore = nowUtc - RecoveryHold;
        var applied = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var tokens = await ReadAsync(
                db => db.DeliveryRecordEvents
                    .Where(e => e.FlowId == flowId && e.AtUtc < appendedBefore && !db.DeliveryLeases.Any(l => l.Token == e.LeaseToken))
                    .Select(e => e.LeaseToken)
                    .Distinct()
                    .Take(RecoveryPage)
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            foreach (var token in tokens)
            {
                applied += await ApplyEventsAsync(token, nowUtc, ct).ConfigureAwait(false);
            }

            if (tokens.Count < RecoveryPage)
            {
                return applied;
            }
        }
    }

    public Task<DateTime?> NextLeaseExpiryAsync(Guid flowId, Guid? submissionId, CancellationToken ct = default)
    {
        var delivering = StatusText.Of(RecordStatus.Delivering);
        return ReadAsync(
            db => db.DeliveryLeases
                .Where(l => l.FlowId == flowId
                    && (submissionId == null
                        || l.SubmissionId == submissionId
                        || db.DeliveryRecords.Any(r => r.LeaseOwner == l.Token && r.LastSubmissionId == submissionId && r.Status == delivering)))
                .MinAsync(l => (DateTime?)l.ExpiresUtc, ct),
            ct);
    }

    private async Task<bool> TakeOverAsync(string token, DateTime nowUtc, CancellationToken ct)
    {
        var holdUntil = nowUtc + RecoveryHold;
        await using var db = Open();
        var taken = await RetryDeadlockAsync(
            () => db.DeliveryLeases
                .Where(l => l.Token == token && l.ExpiresUtc < nowUtc)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Owner, Recoverer).SetProperty(l => l.ExpiresUtc, holdUntil), ct),
            ct).ConfigureAwait(false);
        return taken > 0;
    }

    private async Task<int> AddLeaseAsync(DeliveryLease lease, CancellationToken ct)
    {
        await using var db = Open();
        db.DeliveryLeases.Add(Copy(lease));
        return await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<int> DeleteLeaseAsync(string token, CancellationToken ct)
    {
        await using var db = Open();
        return await db.DeliveryLeases.Where(l => l.Token == token).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands the records a lease still holds back to pending. A try the worker did not finish is not charged when the lease
    /// ended on the worker's own terms; a lease that ran out charges it, since its worker stopped mid-attempt.
    /// </summary>
    private async Task<int> ReleaseHeldAsync(string token, LeaseEnd end, DateTime nowUtc, CancellationToken ct)
    {
        var delivering = StatusText.Of(RecordStatus.Delivering);
        var pending = StatusText.Of(RecordStatus.Pending);
        await using var db = Open();
        var find = db.DeliveryRecords.Where(r => r.LeaseOwner == token);
        var rows = db.DeliveryRecords.Where(r => r.LeaseOwner == token && r.Status == delivering);
        return end switch
        {
            LeaseEnd.Expired => await WriteEachAsync(
                find,
                rows,
                slice => slice.ExecuteUpdateAsync(
                    s => s
                        .SetProperty(r => r.Status, pending)
                        .SetProperty(r => r.LeaseOwner, (string?)null)
                        .SetProperty(r => r.LastError, ExpiredNote)
                        .SetProperty(r => r.UpdatedUtc, nowUtc),
                    ct),
                ct).ConfigureAwait(false),
            LeaseEnd.Stopped => await WriteEachAsync(
                find,
                rows,
                slice => slice.ExecuteUpdateAsync(
                    s => s
                        .SetProperty(r => r.Status, pending)
                        .SetProperty(r => r.LeaseOwner, (string?)null)
                        .SetProperty(r => r.AttemptCount, r => r.AttemptCount > 0 ? r.AttemptCount - 1 : 0)
                        .SetProperty(r => r.LastError, StoppedNote)
                        .SetProperty(r => r.UpdatedUtc, nowUtc),
                    ct),
                ct).ConfigureAwait(false),
            _ => await WriteEachAsync(
                find,
                rows,
                slice => slice.ExecuteUpdateAsync(
                    s => s
                        .SetProperty(r => r.Status, pending)
                        .SetProperty(r => r.LeaseOwner, (string?)null)
                        .SetProperty(r => r.AttemptCount, r => r.AttemptCount > 0 ? r.AttemptCount - 1 : 0)
                        .SetProperty(r => r.UpdatedUtc, nowUtc),
                    ct),
                ct).ConfigureAwait(false),
        };
    }

    /// <summary>Settles the batch a lease drained: done or failed with its counts, or queued again for the next claim.</summary>
    private async Task SettleBatchAsync(Guid submissionId, int index, string token, LeaseClosing closing, DateTime nowUtc, CancellationToken ct)
    {
        await using var db = Open();
        var batch = db.DeliveryWorkBatches.Where(b => b.SubmissionId == submissionId && b.Index == index && b.LeaseOwner == token);
        if (closing.End is LeaseEnd.Done or LeaseEnd.Failed)
        {
            var status = StatusText.Of(closing.End == LeaseEnd.Done ? WorkBatchStatus.Done : WorkBatchStatus.Failed);
            var failure = Truncate(closing.Failure, 2000);
            await RetryDeadlockAsync(
                () => batch.ExecuteUpdateAsync(
                    s => s
                        .SetProperty(b => b.Status, status)
                        .SetProperty(b => b.LeaseOwner, (string?)null)
                        .SetProperty(b => b.CompletedUtc, nowUtc)
                        .SetProperty(b => b.Delivered, closing.Delivered)
                        .SetProperty(b => b.Held, closing.Held)
                        .SetProperty(b => b.Failed, closing.Failed)
                        .SetProperty(b => b.Retrying, closing.Retrying)
                        .SetProperty(b => b.Error, failure),
                    ct),
                ct).ConfigureAwait(false);
            return;
        }

        var queued = StatusText.Of(WorkBatchStatus.Queued);
        await RetryDeadlockAsync(
            () => batch.ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, queued).SetProperty(b => b.LeaseOwner, (string?)null), ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>Applies a lease's appended events a slice of records at a time, until none is left; returns the tries it settled.</summary>
    private async Task<int> ApplyEventsAsync(string token, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        var applied = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            (int Records, int Applied, IReadOnlyList<string> Landed) slice;
            await using (var db = Open())
            {
                slice = await RetryDeadlockAsync(() => SqlServerLedgerBulk.ApplyEventsAsync(db, token, WriteSlice, nowUtc, ct), ct).ConfigureAwait(false);
            }

            // After the commit, never inside it: a claim deciding to wait for one of these records meanwhile either saw it
            // landed, or marked its record waiting before this release reads what waits.
            await ReleaseWaitersOfAsync(slice.Landed, nowUtc, ct).ConfigureAwait(false);
            applied += slice.Applied;
            if (slice.Records < WriteSlice)
            {
                return applied;
            }
        }
    }

    /// <summary>The records a lease holds, with its expiry.</summary>
    private async Task<IReadOnlyList<RecordState>> LeasedAsync(LeaseState lease, CancellationToken ct)
    {
        var rows = await ReadAsync(db => db.DeliveryRecords.Where(r => r.LeaseOwner == lease.Token).ToListAsync(ct), ct).ConfigureAwait(false);
        return rows.Select(r => ToState(r) with { LeaseExpiresUtc = lease.ExpiresUtc }).ToList();
    }

    /// <summary>A record as the ledger holds it, with the expiry of the lease that holds it, if any.</summary>
    private sealed record Leased(DeliveryRecord Record, DateTime? LeaseExpiresUtc);

    /// <summary>
    /// The records a query matches, each leased one carrying its lease's expiry, in ONE statement: the expiry is a
    /// correlated lookup inside the same query. Reading the records and then their leases would be two statements, and
    /// a lease closing between them leaves a record showing as delivering with an expiry it no longer has.
    /// </summary>
    private static Task<List<Leased>> ReadLeasedAsync(OsduDbContext db, IQueryable<DeliveryRecord> rows, CancellationToken ct)
        => rows
            .Select(r => new Leased(
                r,
                db.DeliveryLeases.Where(l => l.Token == r.LeaseOwner).Select(l => (DateTime?)l.ExpiresUtc).FirstOrDefault()))
            .ToListAsync(ct);

    /// <summary>What a caller sees: the ledger's record state, carrying the lease expiry read with it.</summary>
    private static IReadOnlyList<RecordState> ToStates(IEnumerable<Leased> rows)
        => rows.Select(r => ToState(r.Record) with { LeaseExpiresUtc = r.LeaseExpiresUtc }).ToList();

    /// <summary>
    /// Opens a context for one read, untracked. A read takes no transaction of its own: while a worker delivers it
    /// writes its lease row and the append-only event and attempt tables, never the record table, which changes only
    /// when a lease is claimed, checkpointed, closed or recovered, a thousand rows to a short transaction. A read that
    /// needs a record and its lease together asks for both in one statement (<see cref="ReadLeasedAsync"/>), so nothing
    /// here depends on the database allowing snapshot isolation.
    /// </summary>
    /// <remarks>
    /// Without snapshot isolation a read holds shared locks while its statement runs, so a writer taking the same rows in
    /// another order (a claim or a recovery on another node) can make it the victim of a deadlock. A read changes nothing,
    /// so it is read again, as a write the database rolled back is written again (<see cref="SqlServerLedgerBulk.IsContention"/>).
    /// </remarks>
    private async Task<T> ReadAsync<T>(Func<OsduDbContext, Task<T>> read, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var db = Open();
                db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
                return await read(db).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < ReadDeadlockAttempts && SqlServerLedgerBulk.IsDeadlock(ex))
            {
                // Waiting a moment, longer each time and never the same for two readers, keeps the read from meeting the
                // same writer the same way again.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50) * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>How many times a read the database chose as a deadlock victim is made before the deadlock is reported.</summary>
    private const int ReadDeadlockAttempts = 5;

    private static string NewToken(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var longest = DeliveryModel.MaxLeaseTokenLength - TokenSuffixLength;
        if (owner.Length > longest)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A worker's name is at most {longest} characters, so the tokens of its leases fit the ledger; this one is {owner.Length}."),
                nameof(owner));
        }

        return owner + "/" + Guid.NewGuid().ToString("N");
    }

    /// <summary>The worker a lease token was made for.</summary>
    internal static string OwnerOf(string token)
        => token.Length > TokenSuffixLength && token[^TokenSuffixLength] == '/' ? token[..^TokenSuffixLength] : token;

    private static DeliveryLease Copy(DeliveryLease lease) => new()
    {
        Token = lease.Token,
        FlowId = lease.FlowId,
        SubmissionId = lease.SubmissionId,
        WorkBatch = lease.WorkBatch,
        Owner = lease.Owner,
        RunId = lease.RunId,
        AcquiredUtc = lease.AcquiredUtc,
        ExpiresUtc = lease.ExpiresUtc,
    };

    private static LeaseState ToState(DeliveryLease lease) => new()
    {
        Token = lease.Token,
        FlowId = lease.FlowId,
        SubmissionId = lease.SubmissionId,
        WorkBatch = lease.WorkBatch,
        Owner = lease.Owner,
        RunId = lease.RunId,
        AcquiredUtc = DateTime.SpecifyKind(lease.AcquiredUtc, DateTimeKind.Utc),
        ExpiresUtc = DateTime.SpecifyKind(lease.ExpiresUtc, DateTimeKind.Utc),
    };

    private static DeliveryRecordEvent ToEvent(Guid flowId, string token, RecordStep step) => new()
    {
        LeaseToken = token,
        FlowId = flowId,
        DeliveryKey = step.DeliveryKey.Value,
        Kind = StepEvent,
        AtUtc = step.AtUtc,
        StepJson = step.StepJson,
        ClaimSubmissionId = step.SubmissionId,
        ClaimDocumentRef = step.DocumentRef,
    };

    private static DeliveryRecordEvent ToEvent(Guid flowId, string token, RecordCompletion completion)
    {
        var claim = completion.Claimed;
        return new DeliveryRecordEvent
        {
            LeaseToken = token,
            FlowId = flowId,
            DeliveryKey = completion.DeliveryKey.Value,
            Kind = CompletionEvent,
            AtUtc = completion.Attempt.CompletedUtc,
            Status = StatusText.Of(completion.Status),
            Promote = completion.Promote,
            NothingSent = completion.NothingSent,
            NextAttemptUtc = completion.NextAttemptUtc,
            Error = Truncate(completion.Error, 2000),
            TargetId = completion.TargetId,
            TargetVersion = completion.TargetVersion,
            TargetStateJson = completion.TargetStateJson,
            PendingStepJson = completion.PendingStepJson,
            ClaimSubmissionId = claim?.SubmissionId,
            ClaimDocumentRef = claim?.DocumentRef,
            ClaimRenderContext = claim?.RenderContext,
            ClaimSourceFingerprint = claim?.SourceFingerprint,
            ClaimSourceModifiedUtc = claim?.SourceModifiedUtc,
            ClaimSourceFileName = Truncate(claim?.Origin.FileName, DeliveryModel.MaxSourceFileNameLength),
            ClaimSourceRowNumber = claim?.Origin.RowNumber,
            ClaimSourceUpdatedUtc = claim?.Origin.UpdatedUtc,
            ClaimMetadataHash = claim?.MetadataHash,
            ClaimPayloadHash = claim?.PayloadHash,
            ClaimPayloadModifiedUtc = claim?.PayloadModifiedUtc,
            ClaimMetadata = claim?.Metadata ?? false,
            ClaimPayload = claim?.Payload ?? false,
        };
    }
}
