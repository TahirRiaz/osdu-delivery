using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// Records that wait for other records (docs/interfaces-design.md section 7). A rendered record keeps the OSDU ids its
// relationship properties hold. A claim takes a due record only when no other record of the ledger holds one of those
// ids without having delivered it; otherwise the record is left waiting for that id, which charges nothing, and goes back
// to pending when the record holding the id lands. Every decision to wait is taken under one application lock, and a
// record never waits for a record whose own wait leads back to it, so records never wait for each other in a circle.
public sealed partial class OsduLedger
{
    /// <summary>The application lock every decision to wait is taken under on SQL Server, so two claims never decide at once.</summary>
    internal const string WaitLockResource = "osdu.record-waits";

    /// <summary>How long a claim waits for another claim's decision to wait, in milliseconds; a decision takes a few reads.</summary>
    internal const int WaitLockTimeoutMs = 60_000;

    /// <summary>
    /// How many records a wait is followed through when a claim checks that it does not lead back to the record it is
    /// deciding: a longer chain is taken as one that might, and the record is sent rather than left waiting for good.
    /// </summary>
    internal const int MaxWaitChain = 64;

    /// <summary>How long a waiting record's reason may be.</summary>
    private const int MaxReasonLength = 2000;

    /// <summary>A due record a claim is about to take, with the document it would send and what that document refers to.</summary>
    private sealed record WaitCandidate(Guid DeliveryKey, string? TargetId, string DocumentRef, IReadOnlyList<RecordReference> References);

    /// <summary>A record holding an OSDU id, as a decision to wait for that id needs to know it.</summary>
    private sealed record IdHolder(
        Guid FlowId, Guid DeliveryKey, string TargetId, bool Claimed, string Status, bool Landed, string? WaitingFor, string? Label, string SourceKey, string? FlowName);

    /// <summary>A decision to wait: the record, the document it holds, the id it waits for and why.</summary>
    private sealed record WaitDecision(Guid DeliveryKey, string DocumentRef, string WaitingFor, string Reason);

    public async Task<int> ReleaseResolvedWaitsAsync(Guid flowId, IReadOnlyCollection<DeliveryKey>? keys, DateTime nowUtc, CancellationToken ct = default)
        => (await ReleaseResolvedAsync(flowId, keys?.Select(k => k.Value).ToList(), nowUtc, ct).ConfigureAwait(false)).Count;

    public async Task<IReadOnlyList<RecordState>> ListWaitingForAsync(string targetId, int max, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var waiting = StatusText.Of(RecordStatus.Waiting);
        var take = Math.Clamp(max, 1, 1000);
        return await ReadAsync(
            async db => await WithLeasesAsync(
                db,
                (await db.DeliveryRecords
                    .Where(r => r.WaitingFor != null && r.WaitingFor == targetId && r.Status == waiting)
                    .OrderByDescending(r => r.UpdatedUtc)
                    .Take(take)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                    .Where(r => string.Equals(r.WaitingFor, targetId, StringComparison.Ordinal))
                    .ToList(),
                ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> HeldIdsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var holders = await ReadAsync(db => HoldersAsync(db, wanted, ct), ct).ConfigureAwait(false);
        var deleted = StatusText.Of(RecordStatus.Deleted);
        return holders
            .Where(h => h.Value.Any(r => r.Status != deleted))
            .Select(h => h.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<RecordState>> ListHoldersAsync(string targetId, int max, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var take = Math.Clamp(max, 1, 100);
        return await ReadAsync(
            async db => await WithLeasesAsync(
                db,
                (await db.DeliveryRecords
                    .Where(r => r.TargetId != null && r.TargetId == targetId)
                    .OrderBy(r => r.ClaimedTargetId == null)
                    .ThenBy(r => r.FlowId)
                    .Take(take)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                    .Where(r => string.Equals(r.TargetId, targetId, StringComparison.Ordinal))
                    .ToList(),
                ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Leaves waiting the due records of <paramref name="due"/> that refer to a record another record of the ledger holds
    /// and has not delivered, as <paramref name="rules"/> says, before a claim takes the rest. Returns the records left
    /// waiting. Nothing is locked when none of them has anything to wait for, which is what a claim finds almost always.
    /// </summary>
    private async Task<IReadOnlyList<WaitingRecord>> LeaveWaitingAsync(
        Guid flowId, Func<OsduDbContext, IQueryable<DeliveryRecord>> due, WaitRules rules, DateTime nowUtc, CancellationToken ct)
    {
        var rows = await ReadAsync(
            db => due(db)
                .Where(r => r.PendingReferences != null && r.PendingDocumentRef != null)
                .Select(r => new { r.DeliveryKey, r.TargetId, r.PendingDocumentRef, r.PendingReferences })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        var candidates = rows
            .Select(r => new WaitCandidate(r.DeliveryKey, r.TargetId, r.PendingDocumentRef!, RecordReferences.Decode(r.PendingReferences)))
            .Where(c => c.References.Count > 0)
            .OrderBy(c => c.DeliveryKey)
            .ToList();
        var ids = candidates.SelectMany(c => c.References).Select(r => r.Id).Distinct(StringComparer.Ordinal).ToList();
        var holders = await ReadAsync(db => HoldersAsync(db, ids, ct), ct).ConfigureAwait(false);
        if (!candidates.Any(c => c.References.Any(r => Waitable(flowId, c, r.Id, holders, rules) is not null)))
        {
            return [];
        }

        IReadOnlyList<WaitDecision> decided;
        await using (var db = Open())
        {
            decided = await InWaitLockAsync(
                db,
                async () =>
                {
                    // Read again under the lock: another claim may have decided, or a record landed, since the check above.
                    var fresh = await HoldersAsync(db, ids, ct).ConfigureAwait(false);
                    var chains = await WaitChainsAsync(db, fresh.Values.SelectMany(h => h), ct).ConfigureAwait(false);
                    var decisions = Decide(flowId, candidates, fresh, chains, rules);
                    var marked = await MarkWaitingAsync(db, flowId, decisions, nowUtc, ct).ConfigureAwait(false);
                    return decisions.Where(d => marked.Contains(d.DeliveryKey)).ToList();
                },
                ct).ConfigureAwait(false);
        }

        if (decided.Count == 0)
        {
            return [];
        }

        // A record whose wait ended between the read above and the commit goes back to pending at once, and the claim
        // that follows takes it. A record landing later releases its waiters itself.
        var released = (await ReleaseResolvedAsync(flowId, decided.Select(d => d.DeliveryKey).ToList(), nowUtc, ct).ConfigureAwait(false)).ToHashSet();
        return decided
            .Where(d => !released.Contains(d.DeliveryKey))
            .Select(d => new WaitingRecord(new DeliveryKey(d.DeliveryKey), d.WaitingFor, d.Reason))
            .ToList();
    }

    /// <summary>
    /// The decisions, record by record in key order: a record waits for the first id it refers to that a record it may wait
    /// for holds (<see cref="Waitable"/>), unless that record's wait leads back to it. A record decided here to wait counts
    /// as waiting for the records decided after it, so two records of one claim never wait for each other either.
    /// </summary>
    private static List<WaitDecision> Decide(
        Guid flowId, IReadOnlyList<WaitCandidate> candidates, IReadOnlyDictionary<string, List<IdHolder>> holders, Dictionary<string, string> chains, WaitRules rules)
    {
        var decisions = new List<WaitDecision>();
        var decided = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            foreach (var reference in candidate.References)
            {
                if (Waitable(flowId, candidate, reference.Id, holders, rules) is not { } holder
                    || LeadsBack(holder, candidate.TargetId, chains, decided))
                {
                    continue;
                }

                decisions.Add(new WaitDecision(candidate.DeliveryKey, candidate.DocumentRef, reference.Id, Reason(reference, holder)));
                if (candidate.TargetId is { } own)
                {
                    decided[own] = reference.Id;
                }

                break;
            }
        }

        return decisions;
    }

    /// <summary>
    /// The record <paramref name="candidate"/> waits for when it refers to <paramref name="id"/>, or null: none when no
    /// record of the ledger holds the id (OSDU's, or another system's), when one that holds it has landed, when the only
    /// ones left were removed from OSDU, when the id is the candidate's own, or when the record holding it belongs to a
    /// flow <paramref name="rules"/> does not wait for. The record that claimed the id is waited for before one only held.
    /// </summary>
    private static IdHolder? Waitable(Guid flowId, WaitCandidate candidate, string id, IReadOnlyDictionary<string, List<IdHolder>> holders, WaitRules rules)
    {
        if (string.Equals(id, candidate.TargetId, StringComparison.Ordinal) || !holders.TryGetValue(id, out var list) || list.Any(h => h.Landed))
        {
            return null;
        }

        var deleted = StatusText.Of(RecordStatus.Deleted);
        var holder = list
            .Where(h => h.Status != deleted && !(h.FlowId == flowId && h.DeliveryKey == candidate.DeliveryKey))
            .OrderByDescending(h => h.Claimed)
            .ThenBy(h => h.FlowId)
            .ThenBy(h => h.DeliveryKey)
            .FirstOrDefault();
        return holder is null || rules.NotWaitedFor.Contains(holder.FlowId) ? null : holder;
    }

    /// <summary>
    /// Whether <paramref name="holder"/> waits, directly or through the records it waits for, for <paramref name="targetId"/>.
    /// A chain that loops without reaching it, or runs longer than <see cref="MaxWaitChain"/>, counts as leading back:
    /// waiting on it could be for good.
    /// </summary>
    private static bool LeadsBack(IdHolder holder, string? targetId, Dictionary<string, string> chains, Dictionary<string, string> decided)
    {
        if (targetId is null)
        {
            return false;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var next = decided.TryGetValue(holder.TargetId, out var inClaim) ? inClaim : holder.WaitingFor;
        for (var depth = 0; depth < MaxWaitChain; depth++)
        {
            if (next is null)
            {
                return false;
            }

            if (string.Equals(next, targetId, StringComparison.Ordinal) || !visited.Add(next))
            {
                return true;
            }

            next = decided.TryGetValue(next, out var decidedNext) ? decidedNext : chains.GetValueOrDefault(next);
        }

        return true;
    }

    private static string Reason(RecordReference reference, IdHolder holder)
    {
        var what = holder.Label is { Length: > 0 } label ? $"'{label}'" : $"'{holder.SourceKey}'";
        var flow = holder.FlowName is { Length: > 0 } name ? name : $"flow {holder.FlowId:D}";
        var reason = $"waits for {reference.Id} ({reference.Property}): record {what} of {flow} holds it and has not delivered it yet ({holder.Status})";
        return reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength];
    }

    /// <summary>
    /// The records holding each of <paramref name="ids"/>, compared exactly as OSDU compares ids. A record holds the id it
    /// is delivered to whether or not it claimed it: a record the intake held keeps its id without claiming it.
    /// </summary>
    private static async Task<Dictionary<string, List<IdHolder>>> HoldersAsync(OsduDbContext db, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var delivered = StatusText.Of(RecordStatus.Delivered);
        var holders = new Dictionary<string, List<IdHolder>>(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(LookupChunk))
        {
            var wanted = chunk.ToList();
            var rows = await db.DeliveryRecords.AsNoTracking()
                .Where(r => r.TargetId != null && wanted.Contains(r.TargetId))
                .Select(r => new
                {
                    r.FlowId,
                    r.DeliveryKey,
                    TargetId = r.TargetId!,
                    Claimed = r.ClaimedTargetId != null,
                    r.Status,
                    Landed = r.Status == delivered || r.TargetVersion != null,
                    r.WaitingFor,
                    r.Label,
                    r.SourceKey,
                    FlowName = db.DeliverySubmissions.Where(s => s.SubmissionId == r.LastSubmissionId).Select(s => s.FlowName).FirstOrDefault(),
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var row in rows.Where(r => wanted.Contains(r.TargetId, StringComparer.Ordinal)))
            {
                if (!holders.TryGetValue(row.TargetId, out var list))
                {
                    holders[row.TargetId] = list = [];
                }

                list.Add(new IdHolder(row.FlowId, row.DeliveryKey, row.TargetId, row.Claimed, row.Status, row.Landed, row.WaitingFor, row.Label, row.SourceKey, row.FlowName));
            }
        }

        return holders;
    }

    /// <summary>
    /// The waits the database already holds that a decision may follow, from the waiting records among
    /// <paramref name="holders"/> onwards: each waiting record's id, and the id it waits for, a level of the chain per read.
    /// </summary>
    private static async Task<Dictionary<string, string>> WaitChainsAsync(OsduDbContext db, IEnumerable<IdHolder> holders, CancellationToken ct)
    {
        var waiting = StatusText.Of(RecordStatus.Waiting);
        var chains = new Dictionary<string, string>(StringComparer.Ordinal);
        var frontier = new HashSet<string>(StringComparer.Ordinal);
        foreach (var holder in holders.Where(h => h.Status == waiting && h.WaitingFor is not null))
        {
            chains[holder.TargetId] = holder.WaitingFor!;
            frontier.Add(holder.WaitingFor!);
        }

        for (var depth = 0; depth < MaxWaitChain && frontier.Count > 0; depth++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in frontier.Where(id => !chains.ContainsKey(id)).Chunk(LookupChunk))
            {
                var wanted = chunk.ToList();
                var rows = await db.DeliveryRecords.AsNoTracking()
                    .Where(r => r.Status == waiting && r.TargetId != null && wanted.Contains(r.TargetId) && r.WaitingFor != null)
                    .Select(r => new { TargetId = r.TargetId!, WaitingFor = r.WaitingFor! })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                foreach (var row in rows.Where(r => wanted.Contains(r.TargetId, StringComparer.Ordinal)))
                {
                    if (chains.TryAdd(row.TargetId, row.WaitingFor))
                    {
                        next.Add(row.WaitingFor);
                    }
                }
            }

            frontier = next;
        }

        return chains;
    }

    /// <summary>
    /// Runs <paramref name="decide"/> in a transaction holding the lock every decision to wait is taken under, so no two
    /// run at once: on SQL Server an application lock, elsewhere the database's own single writer.
    /// </summary>
    private static async Task<T> InWaitLockAsync<T>(OsduDbContext db, Func<Task<T>> decide, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            if (SqlServerLedgerBulk.Applies(db))
            {
                await SqlServerLedgerBulk.TakeWaitLockAsync(db, WaitLockResource, WaitLockTimeoutMs, ct).ConfigureAwait(false);
            }

            var result = await decide().ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks the decided records waiting, a slice at a time, each only while it is still pending with the document the
    /// decision read and no lease holds it. Returns the records marked.
    /// </summary>
    private async Task<HashSet<Guid>> MarkWaitingAsync(OsduDbContext db, Guid flowId, IReadOnlyList<WaitDecision> decisions, DateTime nowUtc, CancellationToken ct)
    {
        var marked = new HashSet<Guid>();
        if (decisions.Count == 0)
        {
            return marked;
        }

        if (SqlServerLedgerBulk.Applies(db))
        {
            foreach (var slice in decisions.Chunk(WriteSlice))
            {
                marked.UnionWith(await SqlServerLedgerBulk.MarkWaitingAsync(
                    db, flowId, slice.Select(d => (d.DeliveryKey, d.DocumentRef, d.WaitingFor, d.Reason)).ToList(), nowUtc, ct).ConfigureAwait(false));
            }

            return marked;
        }

        var pending = StatusText.Of(RecordStatus.Pending);
        var waiting = StatusText.Of(RecordStatus.Waiting);
        foreach (var decision in decisions)
        {
            var key = decision.DeliveryKey;
            var reference = decision.DocumentRef;
            var waitingFor = decision.WaitingFor;
            var reason = decision.Reason;
            var written = await db.DeliveryRecords
                .Where(r => r.FlowId == flowId && r.DeliveryKey == key && r.Status == pending && r.LeaseOwner == null && r.PendingDocumentRef == reference)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(r => r.Status, waiting)
                        .SetProperty(r => r.WaitingFor, waitingFor)
                        .SetProperty(r => r.LastError, reason)
                        .SetProperty(r => r.UpdatedUtc, nowUtc),
                    ct)
                .ConfigureAwait(false);
            if (written > 0)
            {
                marked.Add(key);
            }
        }

        return marked;
    }

    /// <summary>
    /// Sends back to pending the records waiting for any of <paramref name="landed"/>, the ids of records that just landed,
    /// in whatever flow they are: found through the index of what records wait for, and written a slice at a time. The
    /// next claim of each decides again, so a record that still refers to another undelivered record waits again.
    /// </summary>
    private async Task<int> ReleaseWaitersOfAsync(IReadOnlyCollection<string> landed, DateTime nowUtc, CancellationToken ct)
    {
        if (landed.Count == 0)
        {
            return 0;
        }

        var waiting = StatusText.Of(RecordStatus.Waiting);
        var pending = StatusText.Of(RecordStatus.Pending);
        var released = 0;
        await using var db = Open();
        foreach (var chunk in landed.Distinct(StringComparer.Ordinal).Chunk(LookupChunk))
        {
            var ids = chunk.ToList();
            var waiters = db.DeliveryRecords.Where(r => r.WaitingFor != null && ids.Contains(r.WaitingFor) && r.Status == waiting);
            released += await WriteEachAsync(
                waiters,
                slice => slice.ExecuteUpdateAsync(
                    s => s
                        .SetProperty(r => r.Status, pending)
                        .SetProperty(r => r.WaitingFor, (string?)null)
                        .SetProperty(r => r.NextAttemptUtc, (DateTime?)null)
                        .SetProperty(r => r.LastError, (string?)null)
                        .SetProperty(r => r.UpdatedUtc, nowUtc),
                    ct),
                ct).ConfigureAwait(false);
        }

        return released;
    }

    /// <summary>
    /// Sends back to pending the flow's waiting records (the ones named, or all of them) whose wait is over: the record they
    /// wait for landed, or no record the ledger holds (other than a removed one) holds the id any more. Returns their keys.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ReleaseResolvedAsync(Guid flowId, IReadOnlyList<Guid>? keys, DateTime nowUtc, CancellationToken ct)
    {
        await using var db = Open();
        if (SqlServerLedgerBulk.Applies(db))
        {
            var released = new List<Guid>();
            IEnumerable<IReadOnlyList<Guid>?> slices = keys is null ? [null] : keys.Distinct().Chunk(WriteSlice).Select(c => (IReadOnlyList<Guid>?)c).ToList();
            foreach (var slice in slices)
            {
                while (true)
                {
                    var written = await RetryDeadlockAsync(
                        () => SqlServerLedgerBulk.ReleaseResolvedWaitsAsync(db, flowId, slice, WriteSlice, nowUtc, ct), ct).ConfigureAwait(false);
                    released.AddRange(written);
                    if (written.Count < WriteSlice)
                    {
                        break;
                    }
                }
            }

            return released;
        }

        var waiting = StatusText.Of(RecordStatus.Waiting);
        var pending = StatusText.Of(RecordStatus.Pending);
        var query = db.DeliveryRecords.AsNoTracking().Where(r => r.FlowId == flowId && r.Status == waiting);
        if (keys is not null)
        {
            var wanted = keys.ToList();
            query = query.Where(r => wanted.Contains(r.DeliveryKey));
        }

        var rows = await query.Select(r => new { r.DeliveryKey, r.WaitingFor }).ToListAsync(ct).ConfigureAwait(false);
        var holders = await HoldersAsync(db, rows.Select(r => r.WaitingFor).OfType<string>().Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false);
        var deleted = StatusText.Of(RecordStatus.Deleted);
        var result = new List<Guid>();
        foreach (var row in rows)
        {
            var over = row.WaitingFor is null
                || !holders.TryGetValue(row.WaitingFor, out var list)
                || list.Any(h => h.Landed)
                || list.All(h => h.Status == deleted || (h.FlowId == flowId && h.DeliveryKey == row.DeliveryKey));
            if (!over)
            {
                continue;
            }

            var key = row.DeliveryKey;
            var waitingFor = row.WaitingFor;
            var written = await db.DeliveryRecords
                .Where(r => r.FlowId == flowId && r.DeliveryKey == key && r.Status == waiting && r.WaitingFor == waitingFor)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(r => r.Status, pending)
                        .SetProperty(r => r.WaitingFor, (string?)null)
                        .SetProperty(r => r.NextAttemptUtc, (DateTime?)null)
                        .SetProperty(r => r.LastError, (string?)null)
                        .SetProperty(r => r.UpdatedUtc, nowUtc),
                    ct)
                .ConfigureAwait(false);
            if (written > 0)
            {
                result.Add(key);
            }
        }

        return result;
    }
}
