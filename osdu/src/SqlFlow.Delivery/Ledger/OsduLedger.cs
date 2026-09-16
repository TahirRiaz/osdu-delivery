using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The ledger over the module database's <c>osdu</c> schema (<see cref="OsduDbContext"/>). A claim is one lease row
/// (design.md section 16.2): the worker takes a work batch, or a group of records due for a retry, under a new token in
/// one compare-and-swap, the records it holds carry the token, and a crashed worker's lease simply runs out and is
/// recovered by the next claim of its flow. While it delivers, a worker renews its one row and appends what it learns;
/// the lease applies that to the records a slice at a time. Reads run under snapshot isolation on SQL Server, so they
/// never wait for a writer. Every query the GUI issues is index-backed (see <see cref="DeliveryModel"/>). Each operation
/// opens its own context from the factory, so the ledger is safe to share across the worker's bounded concurrency. The
/// volume writes (staging pending records, appending and applying a lease's events) go through a bulk copy and
/// set-based statements on SQL Server (<see cref="SqlServerLedgerBulk"/>) and through the entity path everywhere else.
/// </summary>
public sealed partial class OsduLedger : ILedger
{
    /// <summary>Cached item ids or set ids per lookup, well inside the parameter ceiling of one command.</summary>
    private const int LookupChunk = 500;

    /// <summary>
    /// Cache sets this ledger has already resolved, by hash. A run stages hundreds of thousands of records across
    /// a handful of distinct dependency sets, so this turns the trail into a few queries rather than one per record.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _cacheSets = new(StringComparer.Ordinal);

    private const int MaxLogLength = 200_000;

    private const int ChunkSize = 500;

    private readonly Func<OsduDbContext> _factory;
    private readonly TimeProvider _time;

    /// <summary>The most records a contains search reads (<see cref="RecordListing.ContainsScanLimit"/>); tests lower it.</summary>
    internal int ContainsScanLimit { get; init; } = RecordListing.ContainsScanLimit;

    /// <summary>
    /// The most records one statement writes when a write can reach more (1,000; tests lower it): a staging slice, a
    /// batch's lease, a release or redelivery. SQL Server turns the row locks of a statement holding 5,000 of them on one
    /// index into a lock on the whole table, which would stop every other node and flow writing records while it ran; a
    /// record's write takes a lock in each index it changes, and at most two there.
    /// </summary>
    internal int WriteSlice { get; init; } = 1_000;

    /// <summary>How many times a claim or lease statement runs when the database ends a deadlock by rolling it back.</summary>
    private const int DeadlockAttempts = 5;

    public OsduLedger(Func<OsduDbContext> factory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>A tracking context: the host may pool no-tracking contexts (the control plane does), and the ledger's
    /// read-modify-write operations rely on tracking.</summary>
    private OsduDbContext Open()
    {
        var db = _factory();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        return db;
    }

    public async Task<SubmissionState?> GetSubmissionAsync(Guid submissionId, CancellationToken ct = default)
    {
        var entity = await ReadAsync(db => db.DeliverySubmissions.FirstOrDefaultAsync(s => s.SubmissionId == submissionId, ct), ct).ConfigureAwait(false);
        return entity is null ? null : ToState(entity);
    }

    public async Task<IReadOnlyList<PlanRequestedRecord>> ListPlanRequestedAsync(Guid flowId, DeliveryKey? after, int max, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db =>
            {
                var query = db.DeliveryRecords.Where(r => r.FlowId == flowId && r.PlanRequestedUtc != null);
                if (after is { } cursor)
                {
                    var from = cursor.Value;
                    query = query.Where(r => r.DeliveryKey.CompareTo(from) > 0);
                }

                return query
                    .OrderBy(r => r.DeliveryKey)
                    .Select(r => new { r.DeliveryKey, r.SourceKeyJson, r.PlanRequestedUtc })
                    .Take(Math.Clamp(max, 1, 10_000))
                    .ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
        return rows.Select(r => new PlanRequestedRecord(new DeliveryKey(r.DeliveryKey), r.SourceKeyJson, DateTime.SpecifyKind(r.PlanRequestedUtc!.Value, DateTimeKind.Utc))).ToList();
    }

    public async Task ClearPlanRequestedAsync(Guid flowId, IReadOnlyList<DeliveryKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        await using var db = Open();
        foreach (var chunk in keys.Select(k => k.Value).Distinct().Chunk(ChunkSize))
        {
            await db.DeliveryRecords
                .Where(r => r.FlowId == flowId && chunk.Contains(r.DeliveryKey) && r.PlanRequestedUtc != null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.PlanRequestedUtc, (DateTime?)null), ct)
                .ConfigureAwait(false);
        }
    }

    public async Task<(SubmissionState Submission, bool Created)> RegisterSubmissionAsync(SubmissionState submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        await using var db = Open();
        var existing = await db.DeliverySubmissions.AsNoTracking().FirstOrDefaultAsync(s => s.SubmissionId == submission.SubmissionId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return (ToState(existing), false);
        }

        var entity = new DeliverySubmission();
        Apply(entity, submission with { ReceivedUtc = submission.ReceivedUtc == default ? Now : submission.ReceivedUtc });
        db.DeliverySubmissions.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Lost a race with another intake of the same id: idempotent by construction.
            var raced = await db.DeliverySubmissions.AsNoTracking().FirstAsync(s => s.SubmissionId == submission.SubmissionId, ct).ConfigureAwait(false);
            return (ToState(raced), false);
        }

        return (ToState(entity), true);
    }

    public async Task UpdateSubmissionAsync(SubmissionState submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        await using var db = Open();
        var entity = await db.DeliverySubmissions.FirstOrDefaultAsync(s => s.SubmissionId == submission.SubmissionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Submission {submission.SubmissionId} is not in the ledger.");
        Apply(entity, submission);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SubmissionState>> ListSubmissionsAsync(Guid? flowId, int max, CancellationToken ct = default)
    {
        var list = await ReadAsync(
            db =>
            {
                var query = db.DeliverySubmissions.AsQueryable();
                if (flowId is { } f)
                {
                    query = query.Where(s => s.FlowId == f);
                }

                return query.OrderByDescending(s => s.ReceivedUtc).Take(Math.Clamp(max, 1, 1000)).ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
        return list.Select(ToState).ToList();
    }

    public async Task<IReadOnlyDictionary<DeliveryKey, RecordState>> GetRecordsAsync(Guid flowId, IEnumerable<DeliveryKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = new Dictionary<DeliveryKey, RecordState>();
        foreach (var chunk in keys.Select(k => k.Value).Distinct().Chunk(ChunkSize))
        {
            var states = await ReadAsync(
                async db => await WithLeasesAsync(db, await db.DeliveryRecords.Where(r => r.FlowId == flowId && chunk.Contains(r.DeliveryKey)).ToListAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false),
                ct).ConfigureAwait(false);
            foreach (var state in states)
            {
                result[state.DeliveryKey] = state;
            }
        }

        return result;
    }

    public async Task<RecordState?> GetRecordAsync(Guid flowId, DeliveryKey key, CancellationToken ct = default)
    {
        var states = await ReadAsync(
            async db => await WithLeasesAsync(db, await db.DeliveryRecords.Where(r => r.FlowId == flowId && r.DeliveryKey == key.Value).Take(1).ToListAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);
        return states.Count == 0 ? null : states[0];
    }

    public async Task<PendingStaging> UpsertPendingAsync(Guid flowId, IReadOnlyList<RecordState> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        RequireFlow(flowId, records.Select(r => (r.FlowId, r.DeliveryKey)));
        if (records.Count == 0)
        {
            return PendingStaging.Empty;
        }

        var now = Now;
        await using var db = Open();
        if (SqlServerLedgerBulk.Applies(db))
        {
            return await SqlServerLedgerBulk.UpsertPendingAsync(db, flowId, records, WriteSlice, now, ct).ConfigureAwait(false);
        }

        var delivering = StatusText.Of(RecordStatus.Delivering);
        var staged = 0;
        var refused = new List<DeliveryKey>();
        var conflicts = new List<TargetIdConflict>();
        foreach (var chunk in records.Chunk(ChunkSize))
        {
            var keys = chunk.Select(r => r.DeliveryKey.Value).ToArray();
            var existing = await db.DeliveryRecords.Where(r => r.FlowId == flowId && keys.Contains(r.DeliveryKey)).ToDictionaryAsync(r => r.DeliveryKey, ct).ConfigureAwait(false);
            var claims = await ClaimsOfOtherFlowsAsync(
                db, flowId, chunk.Select(r => existing.GetValueOrDefault(r.DeliveryKey.Value)?.TargetId ?? r.TargetId), ct).ConfigureAwait(false);
            var tokens = existing.Values.Where(r => r.Status == delivering).Select(r => r.LeaseOwner).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            var live = (await db.DeliveryLeases.Where(l => tokens.Contains(l.Token) && l.ExpiresUtc > now).Select(l => l.Token).ToListAsync(ct).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var record in chunk)
            {
                var inFlight = false;
                existing.TryGetValue(record.DeliveryKey.Value, out var entity);
                if (entity is not null && HoldsNewerThan(entity, record))
                {
                    refused.Add(record.DeliveryKey);
                    continue;
                }

                // The id the record is delivered to: the one it already carries, or the one this work was rendered with.
                var targetId = entity?.TargetId ?? record.TargetId;
                if (targetId is not null && claims.TryGetValue(targetId, out var owner))
                {
                    conflicts.Add(new TargetIdConflict(record.DeliveryKey, targetId, owner.FlowId, owner.FlowName));
                    continue;
                }

                if (entity is null)
                {
                    entity = new DeliveryRecord
                    {
                        DeliveryKey = record.DeliveryKey.Value,
                        FlowId = flowId,
                        SourceKey = record.SourceKey,
                        MappingName = record.MappingName,
                        CreatedUtc = now,
                    };
                    db.DeliveryRecords.Add(entity);
                    existing[entity.DeliveryKey] = entity;
                }
                else
                {
                    inFlight = entity.Status == delivering && entity.LeaseOwner is { } token && live.Contains(token);
                }

                // Current-state columns (what OSDU holds) are preserved; only the pending work is (re)written. A record
                // another worker is delivering right now keeps its status, lease and retry count: the new work queues
                // behind the delivery, whose completion leaves it pending for the next pass.
                entity.SourceKey = Truncate(record.SourceKey, 400)!;
                entity.Label = Truncate(record.Label, 400);
                entity.MappingName = record.MappingName;
                entity.TargetId ??= record.TargetId;
                // Queueing a document is what claims the id for the flow; the claim stays with the record from then on.
                entity.ClaimedTargetId ??= entity.TargetId;
                entity.LastSubmissionId = record.LastSubmissionId;
                entity.NextAttemptUtc = null;
                if (!inFlight)
                {
                    entity.Status = StatusText.Of(RecordStatus.Pending);
                    entity.AttemptCount = 0;
                    entity.LastError = null;
                    entity.LeaseOwner = null;
                }

                entity.PendingDocumentRef = record.PendingDocumentRef;
                entity.WorkBatch = record.WorkBatch;
                entity.PendingStepJson = null;
                entity.PendingRenderContext = record.PendingRenderContext;
                entity.SourceKeyJson = Truncate(record.SourceKeyJson, 2000) ?? entity.SourceKeyJson;
                entity.PendingSourceFingerprint = record.PendingSourceFingerprint;
                entity.PendingSourceModifiedUtc = record.PendingSourceModifiedUtc;
                entity.PendingSourceFileName = Truncate(record.PendingSourceFileName, DeliveryModel.MaxSourceFileNameLength);
                entity.PendingSourceRowNumber = record.PendingSourceRowNumber;
                entity.PendingSourceUpdatedUtc = record.PendingSourceUpdatedUtc;
                entity.PlanRequestedUtc = null;
                entity.PendingMetadataHash = record.PendingMetadataHash;
                entity.PendingPayloadHash = record.PendingPayloadHash;
                entity.PendingPayloadModifiedUtc = record.PendingPayloadModifiedUtc;
                entity.PendingPayloadLocation = record.PendingPayloadLocation;
                entity.PendingMetadata = record.PendingMetadata;
                entity.PendingPayload = record.PendingPayload;
                entity.CacheSetId = record.CacheSetId;
                entity.Blocked = false;
                entity.UpdatedUtc = now;
                staged++;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new PendingStaging(staged, refused, conflicts);
    }

    /// <summary>Refuses records of another flow than the one a flow-scoped write was called for, naming the first.</summary>
    private static void RequireFlow(Guid flowId, IEnumerable<(Guid FlowId, DeliveryKey Key)> records)
    {
        foreach (var (recordFlow, key) in records)
        {
            if (recordFlow != flowId)
            {
                throw new ArgumentException(
                    $"Record {key} belongs to flow {recordFlow:D}, and this write is for flow {flowId:D}: a flow writes only its own records.",
                    nameof(records));
            }
        }
    }

    /// <summary>
    /// Which of <paramref name="targetIds"/> another flow's record has claimed, with the owning flow and its name as its
    /// last submission recorded it. Ids compare exactly, as OSDU compares them.
    /// </summary>
    private static async Task<Dictionary<string, (Guid FlowId, string? FlowName)>> ClaimsOfOtherFlowsAsync(
        OsduDbContext db, Guid flowId, IEnumerable<string?> targetIds, CancellationToken ct)
    {
        var claims = new Dictionary<string, (Guid FlowId, string? FlowName)>(StringComparer.Ordinal);
        var ids = targetIds.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        foreach (var chunk in ids.Chunk(LookupChunk))
        {
            var wanted = chunk.ToList();
            var owners = await db.DeliveryRecords.AsNoTracking()
                .Where(r => r.FlowId != flowId && r.ClaimedTargetId != null && wanted.Contains(r.ClaimedTargetId))
                .Select(r => new
                {
                    ClaimedTargetId = r.ClaimedTargetId!,
                    r.FlowId,
                    FlowName = db.DeliverySubmissions.Where(s => s.SubmissionId == r.LastSubmissionId).Select(s => s.FlowName).FirstOrDefault(),
                })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var owner in owners.Where(o => wanted.Contains(o.ClaimedTargetId, StringComparer.Ordinal)))
            {
                claims[owner.ClaimedTargetId] = (owner.FlowId, owner.FlowName);
            }
        }

        return claims;
    }

    /// <summary>
    /// Whether the record already holds, delivered or queued, a source version or a payload newer than the work
    /// carries. The planner decided against the record as it read it; another intake can have staged or delivered a
    /// newer version since, and that version must stand. The SQL Server path applies the same test in set form.
    /// </summary>
    private static bool HoldsNewerThan(DeliveryRecord entity, RecordState work)
    {
        var queued = entity.PendingDocumentRef is not null
            && (entity.Status == StatusText.Of(RecordStatus.Pending) || entity.Status == StatusText.Of(RecordStatus.Delivering));
        if (work.PendingSourceModifiedUtc is { } source
            && ((entity.SourceModifiedUtc is { } delivered && source < delivered)
                || (queued && entity.PendingSourceModifiedUtc is { } pending && source < pending)))
        {
            return true;
        }

        return work.PendingPayload && work.PendingPayloadModifiedUtc is { } payload
            && ((entity.PayloadModifiedUtc is { } deliveredPayload && payload < deliveredPayload)
                || (queued && entity.PendingPayload && entity.PendingPayloadModifiedUtc is { } pendingPayload && payload < pendingPayload));
    }

    public async Task MarkSkippedAsync(Guid flowId, IReadOnlyList<SkippedRecord> records, Guid submissionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        var now = Now;
        var pending = StatusText.Of(RecordStatus.Pending);
        var delivering = StatusText.Of(RecordStatus.Delivering);
        var delivered = StatusText.Of(RecordStatus.Delivered);
        await using var db = Open();

        // An unchanged record carries nothing to write but the submission pointer: one statement per chunk, however many
        // a quiet run skips. A record with queued work stays with the submission that queued it, or that submission's
        // drain would never find it.
        foreach (var chunk in records.Where(r => r.Kind == SkipKind.Unchanged).Select(r => r.DeliveryKey.Value).Chunk(ChunkSize))
        {
            await db.DeliveryRecords
                .Where(r => r.FlowId == flowId && chunk.Contains(r.DeliveryKey) && !(r.PendingDocumentRef != null && (r.Status == pending || r.Status == delivering)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.LastSubmissionId, submissionId)
                    .SetProperty(r => r.UpdatedUtc, now), ct)
                .ConfigureAwait(false);
        }

        // A plan that saw a record has answered the request to plan it again, whatever it decided about it.
        foreach (var chunk in records.Select(r => r.DeliveryKey.Value).Distinct().Chunk(ChunkSize))
        {
            await db.DeliveryRecords
                .Where(r => r.FlowId == flowId && chunk.Contains(r.DeliveryKey) && r.PlanRequestedUtc != null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.PlanRequestedUtc, (DateTime?)null), ct)
                .ConfigureAwait(false);
        }

        foreach (var chunk in records.Where(r => r.Kind != SkipKind.Unchanged).Chunk(ChunkSize))
        {
            var keys = chunk.Select(r => r.DeliveryKey.Value).ToArray();
            var entities = await db.DeliveryRecords.Where(r => r.FlowId == flowId && keys.Contains(r.DeliveryKey)).ToDictionaryAsync(r => r.DeliveryKey, ct).ConfigureAwait(false);
            foreach (var skip in chunk)
            {
                if (!entities.TryGetValue(skip.DeliveryKey.Value, out var entity))
                {
                    throw new DeliveryException($"Record {skip.DeliveryKey} is not in the ledger, but a {skip.Kind.ToString().ToLowerInvariant()} skip is only ever decided against a record the ledger holds.");
                }

                entity.SourceKeyJson = Truncate(skip.SourceKeyJson, 2000) ?? entity.SourceKeyJson;
                if (skip.Kind == SkipKind.Stale)
                {
                    // The record is left exactly as it is: the attempt is the whole of what happened, and says which
                    // version and origin the source carried and which version stands.
                    db.DeliveryAttempts.Add(new DeliveryAttempt
                    {
                        FlowId = flowId,
                        DeliveryKey = entity.DeliveryKey,
                        SubmissionId = submissionId,
                        RunId = skip.RunId,
                        Worker = "intake",
                        StartedUtc = now,
                        CompletedUtc = now,
                        Outcome = StatusText.Of(AttemptOutcome.Skipped),
                        Phase = AttemptPhases.Stale,
                        ResultJson = AttemptResult.WithDetail(null, Truncate(Http.HeaderRedaction.RedactMessage(skip.Reason), 2000)),
                        SourceFileName = Truncate(skip.Origin.FileName, DeliveryModel.MaxSourceFileNameLength),
                        SourceRowNumber = skip.Origin.RowNumber,
                        SourceUpdatedUtc = skip.Origin.UpdatedUtc,
                    });
                    continue;
                }

                if (entity.PendingDocumentRef is not null && (entity.Status == pending || entity.Status == delivering))
                {
                    // Identical to the queued work: the version the drop carried belongs to that work and lands with it.
                    entity.PendingSourceFingerprint = skip.SourceFingerprint ?? entity.PendingSourceFingerprint;
                    entity.PendingSourceModifiedUtc = Latest(entity.PendingSourceModifiedUtc, skip.SourceModifiedUtc);
                    if (entity.PendingPayload)
                    {
                        entity.PendingPayloadModifiedUtc = Latest(entity.PendingPayloadModifiedUtc, skip.PayloadModifiedUtc);
                    }

                    entity.UpdatedUtc = now;
                    continue;
                }

                entity.LastSubmissionId = submissionId;
                entity.UpdatedUtc = now;
                if (entity.Status == delivered)
                {
                    // OSDU holds a document identical to this render, so it is as true of the version and context just
                    // rendered as of the ones it was built from: the next plan decides the record without rendering.
                    entity.SourceFingerprint = skip.SourceFingerprint ?? entity.SourceFingerprint;
                    entity.SourceModifiedUtc = Latest(entity.SourceModifiedUtc, skip.SourceModifiedUtc);
                    entity.PayloadModifiedUtc = Latest(entity.PayloadModifiedUtc, skip.PayloadModifiedUtc);
                    entity.RenderContext = skip.RenderContext ?? entity.RenderContext;
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static DateTime? Latest(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : a > b ? a : b;

    public async Task MarkHeldAsync(Guid flowId, IEnumerable<RecordState> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var list = records as IReadOnlyList<RecordState> ?? records.ToList();
        RequireFlow(flowId, list.Select(r => (r.FlowId, r.DeliveryKey)));
        var now = Now;
        await using var db = Open();
        foreach (var chunk in list.Chunk(ChunkSize))
        {
            var keys = chunk.Select(r => r.DeliveryKey.Value).ToArray();
            var existing = await db.DeliveryRecords.Where(r => r.FlowId == flowId && keys.Contains(r.DeliveryKey)).ToDictionaryAsync(r => r.DeliveryKey, ct).ConfigureAwait(false);
            // A held record claims nothing, and it is not given an id another flow's record holds: nothing this flow
            // does to it (a removal, a read back) may reach that flow's OSDU record.
            var claims = await ClaimsOfOtherFlowsAsync(
                db, flowId, chunk.Where(r => existing.GetValueOrDefault(r.DeliveryKey.Value)?.TargetId is null).Select(r => r.TargetId), ct).ConfigureAwait(false);
            foreach (var record in chunk)
            {
                if (!existing.TryGetValue(record.DeliveryKey.Value, out var entity))
                {
                    entity = new DeliveryRecord
                    {
                        DeliveryKey = record.DeliveryKey.Value,
                        FlowId = flowId,
                        SourceKey = Truncate(record.SourceKey, 400)!,
                        MappingName = record.MappingName,
                        CreatedUtc = now,
                    };
                    db.DeliveryRecords.Add(entity);
                    existing[entity.DeliveryKey] = entity;
                }

                entity.Label = Truncate(record.Label, 400) ?? entity.Label;
                if (record.TargetId is { } targetId && !claims.ContainsKey(targetId))
                {
                    entity.TargetId ??= targetId;
                }
                entity.Status = StatusText.Of(RecordStatus.Held);
                entity.LastError = Truncate(record.LastError, 2000);
                entity.LastSubmissionId = record.LastSubmissionId;
                entity.LeaseOwner = null;
                entity.PendingDocumentRef = null;
                entity.WorkBatch = null;
                entity.PendingStepJson = null;
                entity.SourceKeyJson = Truncate(record.SourceKeyJson, 2000) ?? entity.SourceKeyJson;
                entity.PendingSourceFingerprint = record.PendingSourceFingerprint;
                entity.PendingSourceModifiedUtc = record.PendingSourceModifiedUtc;
                entity.PendingSourceFileName = Truncate(record.PendingSourceFileName, DeliveryModel.MaxSourceFileNameLength);
                entity.PendingSourceRowNumber = record.PendingSourceRowNumber;
                entity.PendingSourceUpdatedUtc = record.PendingSourceUpdatedUtc;
                entity.PlanRequestedUtc = null;
                entity.PendingMetadata = false;
                entity.PendingPayload = false;
                entity.Blocked = true;
                entity.UpdatedUtc = now;
                db.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    FlowId = flowId,
                    DeliveryKey = record.DeliveryKey.Value,
                    SubmissionId = record.LastSubmissionId,
                    RunId = record.RunId,
                    Worker = "intake",
                    StartedUtc = now,
                    CompletedUtc = now,
                    Outcome = StatusText.Of(AttemptOutcome.Held),
                    Phase = "render",
                    Error = Truncate(record.LastError, 2000),
                    SourceFileName = Truncate(record.PendingSourceFileName, DeliveryModel.MaxSourceFileNameLength),
                    SourceRowNumber = record.PendingSourceRowNumber,
                    SourceUpdatedUtc = record.PendingSourceUpdatedUtc,
                });
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/>, and again when the database ended a deadlock by rolling it back. Only for a read, or
    /// for one statement whose second run does what the first would have: a claim, a lease, a release.
    /// </summary>
    private static async Task<T> RetryDeadlockAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await work().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < DeadlockAttempts && SqlServerLedgerBulk.IsDeadlock(ex))
            {
                // Waiting a moment, longer each time and never the same for two writers, keeps the two from meeting in
                // the same way again.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50) * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    public Task<long> CountAttemptsAsync(Guid submissionId, AttemptOutcome outcome, string? phase = null, CancellationToken ct = default)
    {
        var text = StatusText.Of(outcome);
        return ReadAsync(
            db =>
            {
                var query = db.DeliveryAttempts.Where(a => a.SubmissionId == submissionId && a.Outcome == text);
                if (phase is not null)
                {
                    query = query.Where(a => a.Phase == phase);
                }

                return query.Select(a => a.DeliveryKey).Distinct().LongCountAsync(ct);
            },
            ct);
    }

    public Task<long> CountAsync(Guid flowId, Guid? submissionId, RecordStatus status, CancellationToken ct = default)
    {
        var text = StatusText.Of(status);
        return ReadAsync(
            db => db.DeliveryRecords.LongCountAsync(r => r.FlowId == flowId && (submissionId == null || r.LastSubmissionId == submissionId) && r.Status == text, ct),
            ct);
    }

    public Task<bool> HasPendingAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, CancellationToken ct = default)
    {
        var pending = StatusText.Of(RecordStatus.Pending);
        var delivering = StatusText.Of(RecordStatus.Delivering);
        return ReadAsync(
            db => db.DeliveryRecords.AnyAsync(
                r => r.FlowId == flowId
                    && (submissionId == null || r.LastSubmissionId == submissionId)
                    && (r.Status == pending || r.Status == delivering),
                ct),
            ct);
    }

    public Task<DateTime?> NextDueAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, CancellationToken ct = default)
    {
        var pending = StatusText.Of(RecordStatus.Pending);
        return ReadAsync(
            db => db.DeliveryRecords
                .Where(r => r.FlowId == flowId && (submissionId == null || r.LastSubmissionId == submissionId) && r.Status == pending && r.NextAttemptUtc != null && r.NextAttemptUtc > nowUtc)
                .MinAsync(r => r.NextAttemptUtc, ct),
            ct);
    }

    public async Task<IReadOnlyList<Guid>> ListSettledSubmissionsWithDueWorkAsync(Guid flowId, Guid? except, DateTime nowUtc, int max, CancellationToken ct = default)
    {
        var pending = StatusText.Of(RecordStatus.Pending);
        var completed = StatusText.Of(SubmissionStatus.Completed);
        var failed = StatusText.Of(SubmissionStatus.Failed);
        return await ReadAsync(
            db =>
            {
                var settled = db.DeliverySubmissions
                    .Where(s => s.FlowId == flowId && (s.Status == completed || s.Status == failed) && (except == null || s.SubmissionId != except))
                    .Select(s => s.SubmissionId);
                return db.DeliveryRecords
                    .Where(r => r.FlowId == flowId
                        && r.Status == pending
                        && r.PendingDocumentRef != null
                        && (r.NextAttemptUtc == null || r.NextAttemptUtc <= nowUtc)
                        && r.LastSubmissionId != null
                        && settled.Contains(r.LastSubmissionId.Value))
                    .Select(r => r.LastSubmissionId!.Value)
                    .Distinct()
                    .Take(Math.Clamp(max, 1, 100))
                    .ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
    }

    public async Task AddWorkBatchAsync(WorkBatchState batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await using var db = Open();
        var exists = await db.DeliveryWorkBatches.AnyAsync(b => b.SubmissionId == batch.SubmissionId && b.Index == batch.Index, ct).ConfigureAwait(false);
        if (exists)
        {
            await db.DeliveryWorkBatches
                .Where(b => b.SubmissionId == batch.SubmissionId && b.Index == batch.Index)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Location, batch.Location)
                    .SetProperty(b => b.RecordCount, batch.RecordCount)
                    .SetProperty(b => b.Status, StatusText.Of(WorkBatchStatus.Queued))
                    .SetProperty(b => b.LeaseOwner, (string?)null)
                    .SetProperty(b => b.CompletedUtc, (DateTime?)null)
                    .SetProperty(b => b.Error, (string?)null), ct)
                .ConfigureAwait(false);
            return;
        }

        db.DeliveryWorkBatches.Add(new DeliveryWorkBatch
        {
            SubmissionId = batch.SubmissionId,
            Index = batch.Index,
            FlowId = batch.FlowId,
            Location = Truncate(batch.Location, 2000)!,
            RecordCount = batch.RecordCount,
            Status = StatusText.Of(WorkBatchStatus.Queued),
            CreatedUtc = batch.CreatedUtc == default ? Now : batch.CreatedUtc,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="write"/> over every record <paramref name="rows"/> matches: the records' keys are read first,
    /// by a plain read that holds no lock past the row it is on, and then written <see cref="WriteSlice"/> to a statement
    /// (<see cref="WriteByKeyAsync"/>). The keys take 32 bytes a record.
    /// </summary>
    private Task<int> WriteEachAsync(IQueryable<DeliveryRecord> rows, Func<IQueryable<DeliveryRecord>, Task<int>> write, CancellationToken ct)
        => WriteEachAsync(rows, rows, write, ct);

    /// <summary>
    /// As <see cref="WriteEachAsync(IQueryable{DeliveryRecord}, Func{IQueryable{DeliveryRecord}, Task{int}}, CancellationToken)"/>,
    /// with the keys read from <paramref name="find"/>: records that include every one <paramref name="rows"/> matches, and
    /// that one index answers alone, however many other records the table holds. A found record <paramref name="rows"/>
    /// does not match is left alone by its write.
    /// </summary>
    private async Task<int> WriteEachAsync(IQueryable<DeliveryRecord> find, IQueryable<DeliveryRecord> rows, Func<IQueryable<DeliveryRecord>, Task<int>> write, CancellationToken ct)
    {
        var keys = await RetryDeadlockAsync(() => find.Select(r => new RecordKey(r.FlowId, r.DeliveryKey)).ToListAsync(ct), ct).ConfigureAwait(false);
        return await WriteByKeyAsync(rows, keys, write, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="write"/> over the records of <paramref name="rows"/> that <paramref name="keys"/> names,
    /// <see cref="WriteSlice"/> to a statement. Each statement names its flow and keys, which every index of the record
    /// table ends with, so the database finds each record with one seek and reads, and locks, no record it does not write.
    /// <paramref name="rows"/> still applies, so a record that changed since its key was read is left as it now is.
    /// </summary>
    private async Task<int> WriteByKeyAsync(IQueryable<DeliveryRecord> rows, IEnumerable<RecordKey> keys, Func<IQueryable<DeliveryRecord>, Task<int>> write, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        var written = 0;
        foreach (var flow in keys.GroupBy(k => k.FlowId))
        {
            var flowId = flow.Key;
            foreach (var slice in flow.Select(k => k.DeliveryKey).Distinct().Chunk(WriteSlice))
            {
                ct.ThrowIfCancellationRequested();
                written += await RetryDeadlockAsync(() => write(rows.Where(r => r.FlowId == flowId && slice.Contains(r.DeliveryKey))), ct).ConfigureAwait(false);
            }
        }

        return written;
    }

    /// <summary>A record's key: its flow and its delivery key.</summary>
    private readonly record struct RecordKey(Guid FlowId, Guid DeliveryKey);

    public Task<IReadOnlyList<WorkBatchState>> ListWorkBatchesAsync(Guid submissionId, int max, int offset, CancellationToken ct = default)
        => ReadAsync<IReadOnlyList<WorkBatchState>>(
            async db =>
            {
                // A running batch shows when its lease runs out.
                var rows = await db.DeliveryWorkBatches
                    .Where(b => b.SubmissionId == submissionId)
                    .OrderBy(b => b.Index)
                    .Skip(Math.Max(0, offset))
                    .Take(Math.Clamp(max, 1, 1000))
                    .Select(b => new { Batch = b, Expires = db.DeliveryLeases.Where(l => l.Token == b.LeaseOwner).Select(l => (DateTime?)l.ExpiresUtc).FirstOrDefault() })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                return rows.Select(r => ToState(r.Batch) with { LeaseExpiresUtc = r.Expires }).ToList();
            },
            ct);

    public Task<long> CountWorkBatchesAsync(Guid submissionId, WorkBatchStatus? status, CancellationToken ct = default)
        => ReadAsync(
            db =>
            {
                var query = db.DeliveryWorkBatches.Where(b => b.SubmissionId == submissionId);
                if (status is { } s)
                {
                    var text = StatusText.Of(s);
                    query = query.Where(b => b.Status == text);
                }

                return query.LongCountAsync(ct);
            },
            ct);

    public async Task<IReadOnlyList<RecordState>> ListAsync(Guid flowId, RecordQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        if (query.Offset >= RecordListing.CountLimit)
        {
            throw new RecordQueryTooBroadException(string.Create(
                CultureInfo.InvariantCulture,
                $"A record listing pages through its first {RecordListing.CountLimit} records, and offset {query.Offset} is past them. Narrow the filter (a status, a submission, a run or a search) to reach the records beyond."));
        }

        return await ReadAsync(
            async db =>
            {
                var rows = await MatchingAsync(db, flowId, query, RecordListing.CountLimit, ct).ConfigureAwait(false);

                // Ties broken by key: a bulk write stamps a whole batch with one update time, and the pages must still partition it.
                var list = await rows
                    .OrderByDescending(r => r.UpdatedUtc)
                    .ThenByDescending(r => r.DeliveryKey)
                    .Skip(query.Offset)
                    .Take(Math.Clamp(query.Max, 1, 1000))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                return await WithLeasesAsync(db, list, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<BoundedCount> CountAsync(Guid flowId, RecordQuery query, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return await ReadAsync(
            async db =>
            {
                var rows = await MatchingAsync(db, flowId, query, limit, ct).ConfigureAwait(false);
                var count = await rows.Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false);
                if (count >= limit)
                {
                    return new BoundedCount(count, Exact: false);
                }

                // Below the limit the count is exact, unless a prefix search left out candidates another filter would have kept.
                var truncated = PrefixTerm(query) is { } term
                    && Narrows(query)
                    && await PrefixCandidatesTruncatedAsync(db.DeliveryRecords.AsNoTracking().Where(r => r.FlowId == flowId), term, limit, ct).ConfigureAwait(false);
                return new BoundedCount(count, Exact: !truncated);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DeliveryKey>> ListKeysAsync(Guid flowId, RecordQuery query, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var keys = await ReadAsync(
            async db =>
            {
                var rows = await MatchingAsync(db, flowId, query, RecordListing.CountLimit + 1, ct).ConfigureAwait(false);
                return await rows
                    .OrderBy(r => r.DeliveryKey)
                    .Select(r => r.DeliveryKey)
                    .Take(Math.Clamp(max, 1, RemovalLimits.MaxSelection))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        return keys.Select(k => new DeliveryKey(k)).ToList();
    }

    public async Task<IReadOnlyList<RecordState>> LookupAsync(string term, int max, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        return await ReadAsync(
            async db =>
            {
                var rows = await LookupFilter(db, term, RecordListing.LookupCandidateLimit)
                    .OrderByDescending(r => r.UpdatedUtc)
                    .ThenByDescending(r => r.DeliveryKey)
                    .Take(Math.Clamp(max, 1, 200))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                return await WithLeasesAsync(db, rows, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<BoundedCount> CountLookupAsync(string term, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var count = await ReadAsync(db => LookupFilter(db, term, limit).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct), ct).ConfigureAwait(false);
        return new BoundedCount(count, Exact: count < limit);
    }

    /// <summary>
    /// A UUID is a delivery key, which every flow reading the row holds a record under (a seek of the key index); anything
    /// else is a prefix over the identity columns across every flow, at most <paramref name="candidates"/> from each
    /// column's own index. A candidate is a record, flow and key together, so a match in one flow never brings in another
    /// flow's record of the same row, and the records are read by joining from the few candidates to the primary key, so
    /// the read stays the size of the candidates however many records the ledger holds.
    /// </summary>
    private static IQueryable<DeliveryRecord> LookupFilter(OsduDbContext db, string term, int candidates)
    {
        var t = term.Trim();
        var rows = db.DeliveryRecords.AsNoTracking();
        if (Guid.TryParse(t, out var key))
        {
            return rows.Where(r => r.DeliveryKey == key);
        }

        return PrefixRecordCandidates(rows, t, candidates)
            .Distinct()
            .Join(rows, m => new { m.FlowId, m.DeliveryKey }, r => new { r.FlowId, r.DeliveryKey }, (_, r) => r);
    }

    /// <summary>
    /// The records a listing matches, as a query each of whose paths reads a bounded part of the ledger. The filters
    /// other than the search seek their <c>(FlowId, column)</c> indexes. A prefix search takes at most
    /// <paramref name="candidates"/> records from each identity column's index, in index order, and the rest of the
    /// filter applies to those. A contains term has no index, so the records the rest of the filter leaves are counted
    /// first, no further than the scan limit, and a filter that leaves more is refused.
    /// </summary>
    private async Task<IQueryable<DeliveryRecord>> MatchingAsync(OsduDbContext db, Guid flowId, RecordQuery query, int candidates, CancellationToken ct)
    {
        var flow = db.DeliveryRecords.AsNoTracking().Where(r => r.FlowId == flowId);
        var rows = Narrow(db, flowId, flow, query);
        if (string.IsNullOrWhiteSpace(query.Search))
        {
            return rows;
        }

        var term = query.Search.Trim();
        if (Guid.TryParse(term, out var key))
        {
            return rows.Where(r => r.DeliveryKey == key);
        }

        if (query.Mode != SearchMode.Contains)
        {
            var matches = PrefixCandidates(flow, term, candidates);
            return rows.Where(r => matches.Contains(r.DeliveryKey));
        }

        var scanned = await rows.Select(r => r.DeliveryKey).Take(ContainsScanLimit + 1).CountAsync(ct).ConfigureAwait(false);
        if (scanned > ContainsScanLimit)
        {
            throw new RecordQueryTooBroadException(string.Create(
                CultureInfo.InvariantCulture,
                $"A contains search reads every record the rest of the filter leaves, and this filter leaves more than {ContainsScanLimit}. Use a prefix search, which is indexed, or narrow by status, submission or run first."));
        }

        return rows.Where(r => r.SourceKey.Contains(term) || (r.Label != null && r.Label.Contains(term)) || (r.TargetId != null && r.TargetId.Contains(term)));
    }

    /// <summary>The listing's filters other than its search, over one flow's records; each one seeks an index.</summary>
    private static IQueryable<DeliveryRecord> Narrow(OsduDbContext db, Guid flowId, IQueryable<DeliveryRecord> rows, RecordQuery query)
    {
        if (query.Status is { } status)
        {
            var text = StatusText.Of(status);
            rows = rows.Where(r => r.Status == text);
        }

        if (query.SubmissionId is { } submissionId)
        {
            rows = rows.Where(r => r.LastSubmissionId == submissionId);
        }

        if (query.RunId is { } runId)
        {
            // The attempt table is indexed on (RunId, FlowId, DeliveryKey), so this is a semi-join over that index rather than a scan.
            var touched = db.DeliveryAttempts.AsNoTracking().Where(a => a.RunId == runId && a.FlowId == flowId).Select(a => a.DeliveryKey);
            rows = rows.Where(r => touched.Contains(r.DeliveryKey));
        }

        if (query.Drifted)
        {
            rows = rows.Where(r => r.LastVerifyOutcome == "drifted" || r.LastVerifyOutcome == "missing");
        }

        if (query.EverDelivered is { } everDelivered)
        {
            rows = everDelivered ? rows.Where(r => r.LastDeliveredUtc != null) : rows.Where(r => r.LastDeliveredUtc == null);
        }

        return rows;
    }

    /// <summary>Whether the listing has a filter besides its search.</summary>
    private static bool Narrows(RecordQuery query)
        => query.Status is not null || query.SubmissionId is not null || query.RunId is not null || query.Drifted || query.EverDelivered is not null;

    /// <summary>The listing's search term when it is a prefix search: not empty, not a delivery key, not a contains search.</summary>
    private static string? PrefixTerm(RecordQuery query)
        => string.IsNullOrWhiteSpace(query.Search) || query.Mode == SearchMode.Contains || Guid.TryParse(query.Search.Trim(), out _)
            ? null
            : query.Search.Trim();

    /// <summary>
    /// The delivery keys of one flow's records whose source key, label, OSDU id or origin file name starts with
    /// <paramref name="term"/>: at most <paramref name="limit"/> from each column, each read in the order of its own index
    /// so the read stops there. A record matching on two columns appears twice, which a membership test does not mind.
    /// Keys identify records only inside one flow, so <paramref name="scope"/> is one flow's records.
    /// </summary>
    private static IQueryable<Guid> PrefixCandidates(IQueryable<DeliveryRecord> scope, string term, int limit)
        => scope.Where(r => r.SourceKey.StartsWith(term)).OrderBy(r => r.SourceKey).Select(r => r.DeliveryKey).Take(limit)
            .Concat(scope.Where(r => r.Label != null && r.Label.StartsWith(term)).OrderBy(r => r.Label).Select(r => r.DeliveryKey).Take(limit))
            .Concat(scope.Where(r => r.TargetId != null && r.TargetId.StartsWith(term)).OrderBy(r => r.TargetId).Select(r => r.DeliveryKey).Take(limit))
            .Concat(scope.Where(r => r.SourceFileName != null && r.SourceFileName.StartsWith(term)).OrderBy(r => r.SourceFileName).Select(r => r.DeliveryKey).Take(limit));

    /// <summary>
    /// The records, of any flow, whose source key, label, OSDU id or origin file name starts with <paramref name="term"/>,
    /// as flow and key pairs: at most <paramref name="limit"/> from each column, read in the order of its own index.
    /// </summary>
    private static IQueryable<RecordIdentity> PrefixRecordCandidates(IQueryable<DeliveryRecord> scope, string term, int limit)
        => scope.Where(r => r.SourceKey.StartsWith(term)).OrderBy(r => r.SourceKey).Select(r => new RecordIdentity { FlowId = r.FlowId, DeliveryKey = r.DeliveryKey }).Take(limit)
            .Concat(scope.Where(r => r.Label != null && r.Label.StartsWith(term)).OrderBy(r => r.Label).Select(r => new RecordIdentity { FlowId = r.FlowId, DeliveryKey = r.DeliveryKey }).Take(limit))
            .Concat(scope.Where(r => r.TargetId != null && r.TargetId.StartsWith(term)).OrderBy(r => r.TargetId).Select(r => new RecordIdentity { FlowId = r.FlowId, DeliveryKey = r.DeliveryKey }).Take(limit))
            .Concat(scope.Where(r => r.SourceFileName != null && r.SourceFileName.StartsWith(term)).OrderBy(r => r.SourceFileName).Select(r => new RecordIdentity { FlowId = r.FlowId, DeliveryKey = r.DeliveryKey }).Take(limit));

    /// <summary>A record's identity in a query: its flow and its delivery key.</summary>
    private sealed class RecordIdentity
    {
        public Guid FlowId { get; init; }

        public Guid DeliveryKey { get; init; }
    }

    /// <summary>Whether any identity column has at least <paramref name="limit"/> prefix matches, so candidates were left out.</summary>
    private static async Task<bool> PrefixCandidatesTruncatedAsync(IQueryable<DeliveryRecord> scope, string term, int limit, CancellationToken ct)
        => await scope.Where(r => r.SourceKey.StartsWith(term)).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false) >= limit
            || await scope.Where(r => r.Label != null && r.Label.StartsWith(term)).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false) >= limit
            || await scope.Where(r => r.TargetId != null && r.TargetId.StartsWith(term)).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false) >= limit
            || await scope.Where(r => r.SourceFileName != null && r.SourceFileName.StartsWith(term)).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false) >= limit;

    public Task<FlowStats> StatsAsync(Guid flowId, DateTime nowUtc, CancellationToken ct = default)
        => ReadAsync(db => StatsAsync(db, flowId, nowUtc, ct), ct);

    /// <summary>A flow's statistics, every count read from one snapshot of the ledger.</summary>
    private static async Task<FlowStats> StatsAsync(OsduDbContext db, Guid flowId, DateTime nowUtc, CancellationToken ct)
    {
        var since = nowUtc.AddHours(-24);
        var (byStatus, drifted, last24) = SqlServerLedgerBulk.Applies(db)
            ? await CountFromViewAsync(db, flowId, since, ct).ConfigureAwait(false)
            : await CountFromRecordsAsync(db, flowId, since, ct).ConfigureAwait(false);
        var lastDelivered = await db.DeliveryRecords.Where(r => r.FlowId == flowId).MaxAsync(r => r.LastDeliveredUtc, ct).ConfigureAwait(false);
        var lastVerified = await db.DeliveryRecords.Where(r => r.FlowId == flowId).MaxAsync(r => r.LastVerifiedUtc, ct).ConfigureAwait(false);
        var submissions = await db.DeliverySubmissions.LongCountAsync(s => s.FlowId == flowId, ct).ConfigureAwait(false);
        var lastSubmission = await db.DeliverySubmissions.Where(s => s.FlowId == flowId).OrderByDescending(s => s.ReceivedUtc).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return new FlowStats
        {
            Total = byStatus.Values.Sum(),
            Pending = byStatus.GetValueOrDefault("pending"),
            Delivering = byStatus.GetValueOrDefault("delivering"),
            Delivered = byStatus.GetValueOrDefault("delivered"),
            Held = byStatus.GetValueOrDefault("held"),
            Failed = byStatus.GetValueOrDefault("failed"),
            Deleted = byStatus.GetValueOrDefault("deleted"),
            Drifted = drifted,
            DeliveredLast24h = last24,
            LastDeliveredUtc = lastDelivered,
            LastVerifiedUtc = lastVerified,
            Submissions = submissions,
            LastSubmission = lastSubmission is null ? null : ToState(lastSubmission),
        };
    }

    private const string RecordCountSql =
        "SELECT [FlowId], [Status], [LastVerifyOutcome], [DeliveredHour], [Records] FROM [" + DeliveryModel.SchemaName + "].[" + DeliveryModel.RecordCountView + "] WITH (NOEXPAND)";

    /// <summary>
    /// A flow's counts from the <c>osdu.RecordCount</c> indexed view, which SQL Server maintains in the transaction of
    /// every record write: a few rows per flow are read however many records the flow holds. The deliveries of the last
    /// 24 hours are the view's whole hours inside the window plus an index count of the part-hour the window opens in,
    /// so the count is exact to the tick and the index range it reads is under an hour of deliveries.
    /// </summary>
    private static async Task<(Dictionary<string, long> ByStatus, long Drifted, long DeliveredSince)> CountFromViewAsync(
        OsduDbContext db, Guid flowId, DateTime since, CancellationToken ct)
    {
        var counts = db.DeliveryRecordCounts.FromSqlRaw(RecordCountSql).Where(c => c.FlowId == flowId);
        var byStatus = await counts
            .GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, Count = g.Sum(c => c.Records) })
            .ToDictionaryAsync(c => c.Status, c => c.Count, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
        var drifted = await counts
            .Where(c => c.LastVerifyOutcome == "drifted" || c.LastVerifyOutcome == "missing")
            .SumAsync(c => c.Records, ct)
            .ConfigureAwait(false);

        var wholeHours = new DateTime(since.Ticks - (since.Ticks % TimeSpan.TicksPerHour), since.Kind);
        if (wholeHours < since)
        {
            wholeHours = wholeHours.AddHours(1);
        }

        var inWholeHours = await counts.Where(c => c.DeliveredHour >= wholeHours).SumAsync(c => c.Records, ct).ConfigureAwait(false);
        var inPartHour = wholeHours == since
            ? 0
            : await db.DeliveryRecords
                .LongCountAsync(r => r.FlowId == flowId && r.LastDeliveredUtc >= since && r.LastDeliveredUtc < wholeHours, ct)
                .ConfigureAwait(false);
        return (byStatus, drifted, inWholeHours + inPartHour);
    }

    /// <summary>The same counts read from the records themselves, for a catalog without the indexed view (the SQLite test catalog).</summary>
    private static async Task<(Dictionary<string, long> ByStatus, long Drifted, long DeliveredSince)> CountFromRecordsAsync(
        OsduDbContext db, Guid flowId, DateTime since, CancellationToken ct)
    {
        var byStatus = await db.DeliveryRecords
            .Where(r => r.FlowId == flowId)
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(c => c.Status, c => c.Count, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
        var drifted = await db.DeliveryRecords
            .LongCountAsync(r => r.FlowId == flowId && (r.LastVerifyOutcome == "drifted" || r.LastVerifyOutcome == "missing"), ct)
            .ConfigureAwait(false);
        var deliveredSince = await db.DeliveryRecords
            .LongCountAsync(r => r.FlowId == flowId && r.LastDeliveredUtc != null && r.LastDeliveredUtc >= since, ct)
            .ConfigureAwait(false);
        return (byStatus, drifted, deliveredSince);
    }

    public async Task<IReadOnlyList<AttemptRecord>> ListAttemptsAsync(Guid flowId, DeliveryKey key, int max, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db => db.DeliveryAttempts
                .Where(a => a.FlowId == flowId && a.DeliveryKey == key.Value)
                .OrderByDescending(a => a.StartedUtc)
                .ThenByDescending(a => a.AttemptId)
                .Take(Math.Clamp(max, 1, 1000))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<AttemptRecord>> ListAttemptsForSubmissionAsync(Guid submissionId, int max, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db => db.DeliveryAttempts
                .Where(a => a.SubmissionId == submissionId)
                .OrderByDescending(a => a.StartedUtc)
                .ThenByDescending(a => a.AttemptId)
                .Take(Math.Clamp(max, 1, 5000))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<RecordState>> ListForVerifyAsync(Guid flowId, DateTime? verifiedBeforeUtc, int max, CancellationToken ct = default)
    {
        var delivered = StatusText.Of(RecordStatus.Delivered);
        var rows = await ReadAsync(
            db =>
            {
                var query = db.DeliveryRecords.Where(r => r.FlowId == flowId && r.Status == delivered && r.TargetId != null);
                if (verifiedBeforeUtc is { } before)
                {
                    query = query.Where(r => r.LastVerifiedUtc == null || r.LastVerifiedUtc < before);
                }

                return query.OrderBy(r => r.LastVerifiedUtc).Take(Math.Clamp(max, 1, 10_000)).ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    public async Task RecordVerifyAsync(Guid flowId, DeliveryKey key, VerifyOutcome outcome, long? observedVersion, DateTime nowUtc, bool requeue, CancellationToken ct = default)
    {
        await using var db = Open();
        var entity = await db.DeliveryRecords.FirstOrDefaultAsync(r => r.FlowId == flowId && r.DeliveryKey == key.Value, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Record {key} is not in the ledger of flow {flowId:D}.");
        entity.LastVerifiedUtc = nowUtc;
        entity.LastVerifyOutcome = StatusText.Of(outcome);
        entity.UpdatedUtc = nowUtc;
        if (requeue && outcome is VerifyOutcome.Drifted or VerifyOutcome.Missing)
        {
            // Force redelivery of everything we hold: clear the hashes so the next intake sees a change.
            entity.MetadataHash = null;
            entity.PayloadHash = null;
            entity.PayloadModifiedUtc = null;
            entity.SourceFingerprint = null;
            entity.LastError = $"verify: {outcome.ToString().ToLowerInvariant()} (observed version {observedVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}, expected {entity.TargetVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}); redelivery queued on next submission";
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> ReleaseAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        var held = StatusText.Of(RecordStatus.Held);
        var failed = StatusText.Of(RecordStatus.Failed);
        var deleted = StatusText.Of(RecordStatus.Deleted);
        var pending = StatusText.Of(RecordStatus.Pending);
        var blocked = db.DeliveryRecords.Where(r => r.FlowId == flowId && r.Blocked && (r.Status == held || r.Status == failed || r.Status == deleted));
        if (keys is not null)
        {
            var ids = keys.Select(k => k.Value).ToArray();
            blocked = blocked.Where(r => ids.Contains(r.DeliveryKey));
        }

        // A release of a whole flow is a slice at a time like any other.
        // Records that still hold their rendered document go straight back to the worker.
        var requeued = await WriteEachAsync(
            blocked.Where(r => r.PendingDocumentRef != null),
            slice => slice.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, pending)
                .SetProperty(r => r.Blocked, false)
                .SetProperty(r => r.AttemptCount, 0)
                .SetProperty(r => r.NextAttemptUtc, (DateTime?)null)
                .SetProperty(r => r.LastError, (string?)null)
                .SetProperty(r => r.UpdatedUtc, nowUtc), ct),
            ct).ConfigureAwait(false);

        // The others are unblocked and asked to be planned again: the flow's next run reads their rows by key.
        var unblocked = await WriteEachAsync(
            blocked.Where(r => r.PendingDocumentRef == null),
            slice => slice.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Blocked, false)
                .SetProperty(r => r.PlanRequestedUtc, nowUtc)
                .SetProperty(r => r.LastError, "released; the flow's next run plans it again from its ingestion rows")
                .SetProperty(r => r.UpdatedUtc, nowUtc), ct),
            ct).ConfigureAwait(false);

        return requeued + unblocked;
    }

    public async Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey> keys, RedeliverScope scope, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        await using var db = Open();
        var note = $"redelivery of {scope.ToString().ToLowerInvariant()} requested";
        return await WriteByKeyAsync(db.DeliveryRecords, keys.Select(k => new RecordKey(flowId, k.Value)), rows => RedeliverAsync(rows, scope, note, nowUtc, ct), ct).ConfigureAwait(false);
    }

    private static Task<int> RedeliverAsync(IQueryable<DeliveryRecord> rows, RedeliverScope scope, string note, DateTime nowUtc, CancellationToken ct)
        => scope switch
        {
            RedeliverScope.Metadata => rows.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.MetadataHash, (string?)null)
                .SetProperty(r => r.SourceFingerprint, (string?)null)
                .SetProperty(r => r.PendingSourceFingerprint, (string?)null)
                .SetProperty(r => r.PlanRequestedUtc, nowUtc)
                .SetProperty(r => r.LastError, note)
                .SetProperty(r => r.UpdatedUtc, nowUtc), ct),
            RedeliverScope.Payload => rows.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.PayloadHash, (string?)null)
                .SetProperty(r => r.PayloadModifiedUtc, (DateTime?)null)
                .SetProperty(r => r.SourceFingerprint, (string?)null)
                .SetProperty(r => r.PendingSourceFingerprint, (string?)null)
                .SetProperty(r => r.PlanRequestedUtc, nowUtc)
                .SetProperty(r => r.LastError, note)
                .SetProperty(r => r.UpdatedUtc, nowUtc), ct),
            _ => rows.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.MetadataHash, (string?)null)
                .SetProperty(r => r.PayloadHash, (string?)null)
                .SetProperty(r => r.PayloadModifiedUtc, (DateTime?)null)
                .SetProperty(r => r.SourceFingerprint, (string?)null)
                .SetProperty(r => r.PendingSourceFingerprint, (string?)null)
                .SetProperty(r => r.PlanRequestedUtc, nowUtc)
                .SetProperty(r => r.LastError, note)
                .SetProperty(r => r.UpdatedUtc, nowUtc), ct),
        };

    public async Task MarkRemovedAsync(Guid flowId, IReadOnlyList<DeliveryKey> keys, RemovalScope scope, string worker, DateTime nowUtc, string? correlationId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        if (keys.Count == 0)
        {
            return;
        }

        var note = scope switch
        {
            RemovalScope.Record => $"removed from OSDU (reversible) by {worker}",
            RemovalScope.History => $"earlier versions purged from OSDU by {worker}; the latest version is still live",
            RemovalScope.Everything => $"purged from OSDU (the record and every version) by {worker}",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

        // A removal of many records settles in chunks: one query for the rows and one save for the whole chunk,
        // rather than a round trip per record.
        foreach (var chunk in keys.Chunk(ChunkSize))
        {
            await using var db = Open();
            var ids = chunk.Select(k => k.Value).Distinct().ToList();
            var entities = await db.DeliveryRecords.Where(r => r.FlowId == flowId && ids.Contains(r.DeliveryKey)).ToListAsync(ct).ConfigureAwait(false);
            if (entities.Count != ids.Count)
            {
                var missing = ids.Except(entities.Select(e => e.DeliveryKey)).First();
                throw new DeliveryException($"Record {missing} is not in the ledger of flow {flowId:D}.");
            }

            foreach (var entity in entities)
            {
                MarkRemoved(db, entity, scope, worker, note, nowUtc, correlationId);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static void MarkRemoved(OsduDbContext db, DeliveryRecord entity, RemovalScope scope, string worker, string note, DateTime nowUtc, string? correlationId)
    {
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            FlowId = entity.FlowId,
            DeliveryKey = entity.DeliveryKey,
            SubmissionId = entity.LastSubmissionId,
            Worker = worker,
            StartedUtc = nowUtc,
            CompletedUtc = nowUtc,
            Outcome = StatusText.Of(scope == RemovalScope.History ? AttemptOutcome.HistoryPurged : AttemptOutcome.Deleted),
            Phase = scope == RemovalScope.History ? "purge-history" : "delete",
            TargetVersion = entity.TargetVersion,
            ResultJson = AttemptResult.Removal(correlationId, entity.TargetStateJson, note),
        });

        // A history purge leaves the record live in OSDU at the version the ledger already holds, so its custody
        // state is still true and must not be disturbed: the attempt above is the whole of what happened.
        if (scope == RemovalScope.History)
        {
            entity.UpdatedUtc = nowUtc;
            return;
        }

        entity.Status = StatusText.Of(RecordStatus.Deleted);
        entity.Blocked = true;
        // The unchanged source keeps the record blocked; a source change or a release plans it again. Under a
        // last-modified flow a record that never carried a moment is held at the removal itself, so only a row
        // modified after it was taken out brings it back.
        entity.PendingSourceFingerprint = entity.SourceFingerprint;
        entity.PendingSourceModifiedUtc = entity.SourceModifiedUtc ?? nowUtc;
        entity.TargetVersion = null;
        entity.TargetStateJson = null;
        entity.MetadataHash = null;
        entity.PayloadHash = null;
        entity.PayloadModifiedUtc = null;
        entity.SourceFingerprint = null;
        entity.SourceModifiedUtc = null;
        entity.LastVerifiedUtc = null;
        entity.LastVerifyOutcome = null;
        entity.LeaseOwner = null;
        entity.NextAttemptUtc = null;
        entity.AttemptCount = 0;
        entity.PendingDocumentRef = null;
        entity.WorkBatch = null;
        entity.PendingStepJson = null;
        entity.PendingMetadata = false;
        entity.PendingPayload = false;
        entity.PendingPayloadLocation = null;
        entity.PendingPayloadModifiedUtc = null;
        entity.LastError = note;
        entity.UpdatedUtc = nowUtc;
    }

    public async Task<long> EnsureCacheSetAsync(string scope, IReadOnlyList<Snapshots.CacheUsage> usages, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(usages);
        var entries = Canonical(usages);
        var hash = SetHash(scope, entries);
        if (_cacheSets.TryGetValue(hash, out var known))
        {
            return known;
        }

        var now = Now;
        await using var db = Open();
        var existing = await db.DeliveryCacheSets.FirstOrDefaultAsync(c => c.SetHash == hash, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.LastSeenUtc = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _cacheSets[hash] = existing.SetId;
            return existing.SetId;
        }

        var set = new DeliveryCacheSet
        {
            SetHash = hash,
            EntryCount = entries.Count,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
        db.DeliveryCacheSets.Add(set);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var usage in entries)
        {
            db.DeliveryCacheSetEntries.Add(new DeliveryCacheSetEntry
            {
                SetId = set.SetId,
                Scope = scope,
                TypeName = usage.TypeName,
                ItemId = Truncate(usage.ItemId, 512)!,
                Path = Truncate(usage.Path, 400)!,
                Kind = KindText(usage.Kind),
                ValueHash = usage.ValueHash,
                ValueText = usage.Display,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two workers of one run can mint the same set at the same time; the unique hash decides, and the
            // loser reads the winner's id rather than failing a staging batch over a race.
            var winner = await db.DeliveryCacheSets.AsNoTracking().FirstOrDefaultAsync(c => c.SetHash == hash, ct).ConfigureAwait(false);
            if (winner is null)
            {
                throw;
            }

            _cacheSets[hash] = winner.SetId;
            return winner.SetId;
        }

        _cacheSets[hash] = set.SetId;
        return set.SetId;
    }

    public async Task<IReadOnlyList<CacheUse>> ListCacheSetAsync(long setId, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db => db.DeliveryCacheSetEntries
                .Where(e => e.SetId == setId)
                .OrderBy(e => e.Scope).ThenBy(e => e.TypeName).ThenBy(e => e.Path)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(e => new CacheUse(e.Scope, e.TypeName, e.ItemId, e.Path, ToKind(e.Kind), e.ValueHash, e.ValueText)).ToList();
    }

    public async Task<IReadOnlyList<CacheSetUse>> FindCacheSetsAsync(string scope, string typeName, IReadOnlyList<string> itemIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0)
        {
            return [];
        }

        var found = new List<CacheSetUse>();
        foreach (var chunk in itemIds.Chunk(LookupChunk))
        {
            var ids = chunk.ToList();
            var rows = await ReadAsync(
                db => db.DeliveryCacheSetEntries
                    .Where(e => e.Scope == scope && e.TypeName == typeName && ids.Contains(e.ItemId))
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            found.AddRange(rows.Select(e => new CacheSetUse(e.SetId, e.Scope, e.TypeName, e.ItemId, e.Path, ToKind(e.Kind), e.ValueHash, e.ValueText)));
        }

        return found;
    }

    public async Task<long> CountRecordsInSetsAsync(IReadOnlyList<long> setIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(setIds);
        if (setIds.Count == 0)
        {
            return 0;
        }

        return await ReadAsync(
            async db =>
            {
                long total = 0;
                foreach (var chunk in setIds.Chunk(LookupChunk))
                {
                    var ids = chunk.ToList();
                    total += await db.DeliveryRecords
                        .Where(r => r.CacheSetId != null && ids.Contains(r.CacheSetId!.Value))
                        .LongCountAsync(ct).ConfigureAwait(false);
                }

                return total;
            },
            ct).ConfigureAwait(false);
    }

    public async Task<int> TagUpdatesAsync(IReadOnlyList<UpdateTag> tags, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count == 0)
        {
            return 0;
        }

        var written = 0;
        var gate = new List<long>();
        await using var db = Open();
        foreach (var tag in tags)
        {
            var open = await db.DeliveryUpdateTags.FirstOrDefaultAsync(
                t => t.Scope == tag.Scope && t.TypeName == tag.TypeName && t.ItemId == tag.ItemId && t.Path == tag.Path
                     && (t.Status == "pending" || t.Status == "approved" || t.Status == "rolling"),
                ct).ConfigureAwait(false);
            if (open is not null)
            {
                // The same value moved again. An approval was for what someone looked at, so a further move
                // reopens the question rather than riding on the old decision.
                var moved = !string.Equals(open.NewValue, Truncate(tag.NewValue, 400), StringComparison.Ordinal);
                open.NewValue = Truncate(tag.NewValue, 400);
                open.ToVersion = tag.ToVersion;
                open.Change = tag.Change;
                open.DetectedUtc = nowUtc;
                open.SetIds = string.Join(',', tag.SetIds);
                open.AffectedRecords = tag.AffectedRecords;
                if (moved && open.Status != "pending" && open.Mode.Equals("approve", StringComparison.OrdinalIgnoreCase))
                {
                    open.Status = "pending";
                    open.DecidedUtc = null;
                    open.DecidedBy = null;
                    gate.AddRange(tag.SetIds);
                }

                continue;
            }

            var approved = tag.Mode.Equals("auto", StringComparison.OrdinalIgnoreCase);
            db.DeliveryUpdateTags.Add(new DeliveryUpdateTag
            {
                Kind = tag.Kind,
                Scope = tag.Scope,
                TypeName = tag.TypeName,
                ItemId = Truncate(tag.ItemId, 512)!,
                Path = Truncate(tag.Path, 400)!,
                Change = tag.Change,
                OldValue = Truncate(tag.OldValue, 400),
                NewValue = Truncate(tag.NewValue, 400),
                FromVersion = tag.FromVersion,
                ToVersion = tag.ToVersion,
                Mode = tag.Mode,
                Status = approved ? "approved" : "pending",
                SetIds = string.Join(',', tag.SetIds),
                AffectedRecords = tag.AffectedRecords,
                DetectedUtc = nowUtc,
                DecidedUtc = approved ? nowUtc : null,
                DecidedBy = approved ? "system:auto" : null,
            });
            written++;
            if (!approved)
            {
                gate.AddRange(tag.SetIds);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await SetGateAsync(db, gate, gated: true, ct).ConfigureAwait(false);
        return written;
    }

    public async Task<IReadOnlyList<long>> GatedCacheSetsAsync(CancellationToken ct = default)
        => await ReadAsync(db => db.DeliveryCacheSets.Where(c => c.Gated).Select(c => c.SetId).ToListAsync(ct), ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<UpdateTag>> ListTagsAsync(string? status, int max, int offset, string? scope = null, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db => TagQuery(db, status, scope)
                .OrderByDescending(t => t.TagId)
                .Skip(Math.Max(0, offset)).Take(Math.Clamp(max, 1, 1000))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToTag).ToList();
    }

    public Task<int> CountTagsAsync(string? status, string? scope = null, CancellationToken ct = default)
        => ReadAsync(db => TagQuery(db, status, scope).CountAsync(ct), ct);

    public async Task<int> DecideTagsAsync(IReadOnlyList<long> tagIds, bool approve, string actor, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (tagIds.Count == 0)
        {
            return 0;
        }

        await using var db = Open();
        var rows = await db.DeliveryUpdateTags.Where(t => tagIds.Contains(t.TagId) && t.Status == "pending").ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.Status = approve ? "approved" : "rejected";
            row.DecidedUtc = nowUtc;
            row.DecidedBy = actor;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await ReleaseUngatedAsync(db, rows.SelectMany(r => ParseSets(r.SetIds)).Distinct().ToList(), ct).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task<UpdateRolloutBatch> RollOutTagAsync(long tagId, int batchSize, DateTime nowUtc, CancellationToken ct = default)
    {
        var size = Math.Clamp(batchSize, 1, 100_000);
        await using var db = Open();
        var tag = await db.DeliveryUpdateTags.FirstOrDefaultAsync(t => t.TagId == tagId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Update tag {tagId} is not in the ledger.");
        if (tag.Status is not ("approved" or "rolling"))
        {
            return new UpdateRolloutBatch(tagId, 0, tag.Processed, tag.AffectedRecords, tag.Status == "applied");
        }

        var sets = ParseSets(tag.SetIds);
        if (sets.Count == 0)
        {
            tag.Status = "applied";
            tag.CompletedUtc = nowUtc;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new UpdateRolloutBatch(tagId, 0, tag.Processed, tag.AffectedRecords, true);
        }

        // One bounded page of records, in key and then flow order from where the last pass stopped: a change over millions
        // of records never becomes one statement, and a pass that is interrupted resumes instead of starting over. The
        // same row read by several flows is one record per flow, and each is marked in its own flow.
        var records = SetRecords(db, sets, tag.Cursor, tag.CursorFlowId);
        var page = await records
            .OrderBy(r => r.DeliveryKey)
            .ThenBy(r => r.FlowId)
            .Select(r => new { r.FlowId, r.DeliveryKey })
            .Take(size)
            .ToListAsync(ct).ConfigureAwait(false);

        if (page.Count > 0)
        {
            // A cache change rewrites the manifest row, never the payload: forgetting the metadata hash and the
            // fingerprint is what makes the next plan render and send the document again, and the curves that
            // were uploaded with it stay where they are. This is the same marking a metadata redelivery makes.
            var note = $"redelivery of metadata requested by cache change {tag.TagId}";
            await WriteByKeyAsync(
                db.DeliveryRecords,
                page.Select(r => new RecordKey(r.FlowId, r.DeliveryKey)),
                rows => RedeliverAsync(rows, RedeliverScope.Metadata, note, nowUtc, ct),
                ct).ConfigureAwait(false);

            tag.Cursor = page[^1].DeliveryKey;
            tag.CursorFlowId = page[^1].FlowId;
            tag.Processed += page.Count;
        }

        tag.StartedUtc ??= nowUtc;
        tag.Status = page.Count < size ? "applied" : "rolling";
        if (tag.Status == "applied")
        {
            tag.CompletedUtc = nowUtc;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new UpdateRolloutBatch(tagId, page.Count, tag.Processed, tag.AffectedRecords, tag.Status == "applied");
    }

    /// <summary>
    /// The records built from any of <paramref name="sets"/> that come after the rollout cursor in key and then flow order.
    /// A cursor without a flow is past every record of its key. The key's lower bound is stated on its own, so each set's
    /// page is a range seek of the rollout index from the cursor on, however many records the sets hold.
    /// </summary>
    private static IQueryable<DeliveryRecord> SetRecords(OsduDbContext db, IReadOnlyList<long> sets, Guid? cursor, Guid? cursorFlow)
    {
        var rows = db.DeliveryRecords.AsNoTracking().Where(r => r.CacheSetId != null && sets.Contains(r.CacheSetId!.Value));
        if (cursor is not { } key)
        {
            return rows;
        }

        return cursorFlow is { } flow
            ? rows.Where(r => r.DeliveryKey.CompareTo(key) >= 0 && (r.DeliveryKey != key || r.FlowId.CompareTo(flow) > 0))
            : rows.Where(r => r.DeliveryKey.CompareTo(key) > 0);
    }

    public async Task<IReadOnlyList<UpdateTag>> ListRolloutQueueAsync(int max, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db => db.DeliveryUpdateTags
                .Where(t => t.Status == "approved" || t.Status == "rolling")
                .OrderBy(t => t.DecidedUtc ?? t.DetectedUtc)
                .Take(Math.Clamp(max, 1, 1000))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToTag).ToList();
    }

    /// <summary>Gates or releases sets in one statement; the set table is small, the record table is never touched.</summary>
    private static async Task SetGateAsync(OsduDbContext db, IReadOnlyList<long> setIds, bool gated, CancellationToken ct)
    {
        if (setIds.Count == 0)
        {
            return;
        }

        foreach (var chunk in setIds.Distinct().Chunk(LookupChunk))
        {
            var ids = chunk.ToList();
            await db.DeliveryCacheSets.Where(c => ids.Contains(c.SetId) && c.Gated != gated)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Gated, gated), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Releases the sets no undecided tag covers any more.</summary>
    private static async Task ReleaseUngatedAsync(OsduDbContext db, IReadOnlyList<long> setIds, CancellationToken ct)
    {
        if (setIds.Count == 0)
        {
            return;
        }

        var pending = await db.DeliveryUpdateTags.AsNoTracking().Where(t => t.Status == "pending").Select(t => t.SetIds).ToListAsync(ct).ConfigureAwait(false);
        var stillGated = pending.SelectMany(ParseSets).ToHashSet();
        await SetGateAsync(db, setIds.Where(id => !stillGated.Contains(id)).ToList(), gated: false, ct).ConfigureAwait(false);
    }

    private static IQueryable<DeliveryUpdateTag> TagQuery(OsduDbContext db, string? status, string? scope)
    {
        var query = db.DeliveryUpdateTags.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(t => t.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(scope))
        {
            var cache = scope.Trim();
            query = query.Where(t => t.Scope == cache);
        }

        return query;
    }

    /// <summary>The set's entries in a stable order, without duplicates: two renders reading the same values hash alike.</summary>
    private static IReadOnlyList<Snapshots.CacheUsage> Canonical(IReadOnlyList<Snapshots.CacheUsage> usages)
        => usages
            .GroupBy(u => (u.TypeName, u.ItemId, u.Path, u.Kind))
            .Select(g => g.First())
            .OrderBy(u => u.TypeName, StringComparer.Ordinal)
            .ThenBy(u => u.ItemId, StringComparer.Ordinal)
            .ThenBy(u => u.Path, StringComparer.Ordinal)
            .ThenBy(u => u.Kind)
            .ToList();

    private static string SetHash(string scope, IReadOnlyList<Snapshots.CacheUsage> entries)
        => Hashing.ContentHash.Of(scope + "\n" + string.Join('\n', entries.Select(e => $"{e.TypeName}|{e.ItemId}|{e.Path}|{KindText(e.Kind)}|{e.ValueHash}")));

    private static string KindText(Snapshots.CacheUsageKind kind) => kind == Snapshots.CacheUsageKind.Match ? "match" : "value";

    private static Snapshots.CacheUsageKind ToKind(string kind)
        => kind.Equals("match", StringComparison.OrdinalIgnoreCase) ? Snapshots.CacheUsageKind.Match : Snapshots.CacheUsageKind.Value;

    private static List<long> ParseSets(string setIds)
        => setIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => long.TryParse(v, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();

    private static UpdateTag ToTag(DeliveryUpdateTag t) => new()
    {
        TagId = t.TagId,
        Kind = t.Kind,
        Scope = t.Scope,
        TypeName = t.TypeName,
        ItemId = t.ItemId,
        Path = t.Path,
        Change = t.Change,
        OldValue = t.OldValue,
        NewValue = t.NewValue,
        FromVersion = t.FromVersion,
        ToVersion = t.ToVersion,
        Mode = t.Mode,
        Status = t.Status,
        SetIds = ParseSets(t.SetIds),
        AffectedRecords = t.AffectedRecords,
        Processed = t.Processed,
        DetectedUtc = t.DetectedUtc,
        DecidedUtc = t.DecidedUtc,
        DecidedBy = t.DecidedBy,
        StartedUtc = t.StartedUtc,
        CompletedUtc = t.CompletedUtc,
    };

    /// <summary>The longest scope key (the flow's parameter values) a watermark is kept under.</summary>
    public const int MaxWatermarkScopeLength = 400;

    public async Task<SourceWatermark?> GetWatermarkAsync(Guid flowId, string scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var row = await ReadAsync(db => db.DeliverySourceWatermarks.FirstOrDefaultAsync(w => w.FlowId == flowId && w.Scope == scope, ct), ct).ConfigureAwait(false);
        return row is null ? null : ToWatermark(row);
    }

    public async Task SetWatermarkAsync(SourceWatermark watermark, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        if (watermark.Scope.Length > MaxWatermarkScopeLength)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture, $"The parameter values of this run make a scope key of {watermark.Scope.Length} characters; the ledger keeps a watermark under at most {MaxWatermarkScopeLength}. Shorten the flow's parameter values."));
        }

        var through = DateTime.SpecifyKind(watermark.UpdatedThroughUtc, DateTimeKind.Utc);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db = Open();
            var entity = await db.DeliverySourceWatermarks.FirstOrDefaultAsync(x => x.FlowId == watermark.FlowId && x.Scope == watermark.Scope, ct).ConfigureAwait(false);
            if (entity is null)
            {
                entity = new DeliverySourceWatermark { FlowId = watermark.FlowId, Scope = watermark.Scope };
                db.DeliverySourceWatermarks.Add(entity);
            }
            else if (through < entity.UpdatedThroughUtc)
            {
                // A re-run of an older plan completes after a newer one: the scope's watermark stays where the newer left it.
                return;
            }

            entity.UpdatedThroughUtc = through;
            entity.SubmissionId = watermark.SubmissionId;
            entity.ContextHash = watermark.ContextHash;
            entity.RecordedUtc = DateTime.SpecifyKind(watermark.RecordedUtc, DateTimeKind.Utc);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Two plans of the scope finished together and both inserted the first watermark: read the winner and compare again.
            }
        }
    }

    private static SourceWatermark ToWatermark(DeliverySourceWatermark w)
        => new(w.FlowId, w.Scope, DateTime.SpecifyKind(w.UpdatedThroughUtc, DateTimeKind.Utc), w.SubmissionId, DateTime.SpecifyKind(w.RecordedUtc, DateTimeKind.Utc), w.ContextHash);

    /// <summary>
    /// The most attempts one prune statement removes (4,000; tests lower it). Each statement is its own short transaction,
    /// so pruning an estate's history never holds a long lock on the attempt table, which every drain appends to, or writes
    /// one huge log record; and it stays under the 5,000 row locks at which SQL Server would lock the whole table instead.
    /// </summary>
    internal int PruneBatch { get; init; } = 4_000;

    public async Task<int> PruneAttemptsAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(PruneBatch, 1);
        var pruned = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = Open();
            // An attempt goes only when a later attempt of the same flow's record exists, so a record's last outcome is
            // always explainable. The old attempts are read in start order from the start index, and the later one is a
            // seek of the record's own timeline.
            var deleted = await db.DeliveryAttempts
                .Where(a => a.StartedUtc < olderThanUtc
                    && db.DeliveryAttempts.Any(b => b.FlowId == a.FlowId && b.DeliveryKey == a.DeliveryKey && b.AttemptId > a.AttemptId))
                .OrderBy(a => a.StartedUtc)
                .Take(PruneBatch)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            pruned += deleted;
            if (deleted < PruneBatch)
            {
                return pruned;
            }
        }
    }

    // ---- Retrievals ----------------------------------------------------------------------------------------------

    public async Task<RetrievalState> StartRetrievalAsync(RetrievalState retrieval, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(retrieval);
        await using var db = Open();
        var entity = new DeliveryRetrieval
        {
            FlowId = retrieval.FlowId,
            FlowName = retrieval.FlowName,
            RunId = retrieval.RunId,
            Actor = retrieval.Actor,
            Kinds = retrieval.Kinds,
            Query = retrieval.Query,
            WindowField = retrieval.WindowField,
            WindowFrom = retrieval.WindowFrom,
            WindowTo = retrieval.WindowTo,
            Location = retrieval.Location,
            ManifestLocation = retrieval.ManifestLocation,
            Status = retrieval.Status,
            Records = retrieval.Records,
            Files = retrieval.Files,
            Bytes = retrieval.Bytes,
            StartedUtc = retrieval.StartedUtc,
            CompletedUtc = retrieval.CompletedUtc,
            Error = Truncate(retrieval.Error, 4000),
        };
        db.DeliveryRetrievals.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return retrieval with { RetrievalId = entity.RetrievalId };
    }

    public async Task CompleteRetrievalAsync(long retrievalId, string status, long records, int files, long bytes, string? manifestLocation, string? failure, DateTime completedUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        await using var db = Open();
        var entity = await db.DeliveryRetrievals.FirstOrDefaultAsync(r => r.RetrievalId == retrievalId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Retrieval {retrievalId} is not in the ledger.");
        entity.Status = status;
        entity.Records = records;
        entity.Files = files;
        entity.Bytes = bytes;
        entity.ManifestLocation = manifestLocation ?? entity.ManifestLocation;
        entity.Error = Truncate(failure, 4000);
        entity.CompletedUtc = completedUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<RetrievalState?> LastRetrievalAsync(Guid flowId, string status, CancellationToken ct = default)
    {
        var entity = await ReadAsync(
            db => db.DeliveryRetrievals
                .Where(r => r.FlowId == flowId && r.Status == status)
                .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.RetrievalId)
                .FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
        return entity is null ? null : ToState(entity);
    }

    public async Task<IReadOnlyList<RetrievalState>> ListRetrievalsAsync(Guid flowId, int max, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db => db.DeliveryRetrievals
                .Where(r => r.FlowId == flowId)
                .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.RetrievalId)
                .Take(Math.Clamp(max, 1, 1000))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    private static RetrievalState ToState(DeliveryRetrieval r) => new()
    {
        RetrievalId = r.RetrievalId,
        FlowId = r.FlowId,
        FlowName = r.FlowName,
        RunId = r.RunId,
        Actor = r.Actor,
        Kinds = r.Kinds,
        Query = r.Query,
        WindowField = r.WindowField,
        WindowFrom = r.WindowFrom is { } from ? DateTime.SpecifyKind(from, DateTimeKind.Utc) : null,
        WindowTo = r.WindowTo is { } to ? DateTime.SpecifyKind(to, DateTimeKind.Utc) : null,
        Location = r.Location,
        ManifestLocation = r.ManifestLocation,
        Status = r.Status,
        Records = r.Records,
        Files = r.Files,
        Bytes = r.Bytes,
        StartedUtc = DateTime.SpecifyKind(r.StartedUtc, DateTimeKind.Utc),
        CompletedUtc = r.CompletedUtc is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
        Error = r.Error,
    };

    public async Task<ActivityRecord> StartActivityAsync(ActivityRecord activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        await using var db = Open();
        var entity = new DeliveryActivity
        {
            FlowId = activity.FlowId,
            FlowName = activity.FlowName,
            Kind = activity.Kind,
            Actor = Truncate(activity.Actor, 200)!,
            StartedUtc = activity.StartedUtc == default ? Now : activity.StartedUtc,
            CompletedUtc = activity.CompletedUtc,
            Outcome = activity.Outcome,
            ParametersJson = activity.ParametersJson,
            SubmissionId = activity.SubmissionId,
            DeliveryKey = activity.DeliveryKey,
            RunId = activity.RunId,
            Summary = Truncate(activity.Summary, 2000),
            Log = Truncate(activity.Log, MaxLogLength),
        };
        db.DeliveryActivities.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToRecord(entity);
    }

    public async Task CompleteActivityAsync(long activityId, string outcome, string? summary, string? log, DateTime completedUtc, Guid? submissionId = null, CancellationToken ct = default)
    {
        await using var db = Open();
        var entity = await db.DeliveryActivities.FirstOrDefaultAsync(a => a.ActivityId == activityId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Activity {activityId} is not in the ledger.");
        entity.Outcome = outcome;
        entity.SubmissionId ??= submissionId;
        entity.Summary = Truncate(summary, 2000);
        entity.Log = Truncate(log, MaxLogLength);
        entity.CompletedUtc = completedUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<ActivityRecord?> GetActivityAsync(long activityId, CancellationToken ct = default)
    {
        var entity = await ReadAsync(db => db.DeliveryActivities.FirstOrDefaultAsync(a => a.ActivityId == activityId, ct), ct).ConfigureAwait(false);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<ActivityRecord>> ListActivitiesAsync(ActivityQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var list = await ReadAsync(
            db => FilterActivities(db.DeliveryActivities, query)
                .OrderByDescending(a => a.StartedUtc)
                .ThenByDescending(a => a.ActivityId)
                .Skip(Math.Max(0, query.Offset))
                .Take(Math.Clamp(query.Max, 1, 1000))
                .Select(a => new DeliveryActivity
                {
                    ActivityId = a.ActivityId,
                    FlowId = a.FlowId,
                    FlowName = a.FlowName,
                    Kind = a.Kind,
                    Actor = a.Actor,
                    StartedUtc = a.StartedUtc,
                    CompletedUtc = a.CompletedUtc,
                    Outcome = a.Outcome,
                    ParametersJson = a.ParametersJson,
                    SubmissionId = a.SubmissionId,
                    DeliveryKey = a.DeliveryKey,
                    RunId = a.RunId,
                    Summary = a.Summary,
                    // The log is fetched per activity, not in listings.
                    Log = null,
                })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return list.Select(ToRecord).ToList();
    }

    public Task<int> CountActivitiesAsync(ActivityQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadAsync(db => FilterActivities(db.DeliveryActivities, query).CountAsync(ct), ct);
    }

    private static IQueryable<DeliveryActivity> FilterActivities(IQueryable<DeliveryActivity> rows, ActivityQuery query)
    {
        if (query.FlowId is { } flowId)
        {
            rows = rows.Where(a => a.FlowId == flowId);
        }

        if (query.DeliveryKey is { } key)
        {
            rows = rows.Where(a => a.DeliveryKey == key);
        }

        if (query.SubmissionId is { } submissionId)
        {
            rows = rows.Where(a => a.SubmissionId == submissionId);
        }

        if (query.RunId is { } runId)
        {
            rows = rows.Where(a => a.RunId == runId);
        }

        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            rows = rows.Where(a => a.Kind == query.Kind);
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            var actor = query.Actor.Trim();
            rows = rows.Where(a => a.Actor.StartsWith(actor));
        }

        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            rows = rows.Where(a => a.Outcome == query.Outcome);
        }

        if (query.SinceUtc is { } since)
        {
            rows = rows.Where(a => a.StartedUtc >= since);
        }

        if (query.UntilUtc is { } until)
        {
            rows = rows.Where(a => a.StartedUtc < until);
        }

        return rows;
    }

    internal static string? Truncate(string? text, int max)
        => text is null ? null : text.Length <= max ? text : text[..max];

    private static DeliveryAttempt ToEntity(Guid flowId, AttemptRecord attempt) => new()
    {
        FlowId = flowId,
        DeliveryKey = attempt.DeliveryKey.Value,
        SubmissionId = attempt.SubmissionId,
        RunId = attempt.RunId,
        Worker = Truncate(attempt.Worker, 200)!,
        StartedUtc = attempt.StartedUtc,
        CompletedUtc = attempt.CompletedUtc,
        Outcome = StatusText.Of(attempt.Outcome),
        Phase = Truncate(attempt.Phase, 32)!,
        MetadataHash = attempt.MetadataHash,
        PayloadHash = attempt.PayloadHash,
        TargetVersion = attempt.TargetVersion,
        Error = Truncate(attempt.Error, 2000),
        ResultJson = attempt.ResultJson,
        WorkBatch = attempt.WorkBatch,
        SourceFileName = Truncate(attempt.SourceFileName, DeliveryModel.MaxSourceFileNameLength),
        SourceRowNumber = attempt.SourceRowNumber,
        SourceUpdatedUtc = attempt.SourceUpdatedUtc,
    };

    private static AttemptRecord ToRecord(DeliveryAttempt a) => new()
    {
        AttemptId = a.AttemptId,
        DeliveryKey = new DeliveryKey(a.DeliveryKey),
        SubmissionId = a.SubmissionId,
        RunId = a.RunId,
        Worker = a.Worker,
        StartedUtc = a.StartedUtc,
        CompletedUtc = a.CompletedUtc,
        Outcome = StatusText.ToAttemptOutcome(a.Outcome),
        Phase = a.Phase,
        MetadataHash = a.MetadataHash,
        PayloadHash = a.PayloadHash,
        TargetVersion = a.TargetVersion,
        Error = a.Error,
        ResultJson = a.ResultJson,
        WorkBatch = a.WorkBatch,
        SourceFileName = a.SourceFileName,
        SourceRowNumber = a.SourceRowNumber,
        SourceUpdatedUtc = a.SourceUpdatedUtc is { } updated ? DateTime.SpecifyKind(updated, DateTimeKind.Utc) : null,
    };

    private static ActivityRecord ToRecord(DeliveryActivity a) => new()
    {
        ActivityId = a.ActivityId,
        FlowId = a.FlowId,
        FlowName = a.FlowName,
        Kind = a.Kind,
        Actor = a.Actor,
        StartedUtc = a.StartedUtc,
        CompletedUtc = a.CompletedUtc,
        Outcome = a.Outcome,
        ParametersJson = a.ParametersJson,
        SubmissionId = a.SubmissionId,
        DeliveryKey = a.DeliveryKey,
        RunId = a.RunId,
        Summary = a.Summary,
        Log = a.Log,
    };

    private static WorkBatchState ToState(DeliveryWorkBatch b) => new()
    {
        SubmissionId = b.SubmissionId,
        FlowId = b.FlowId,
        Index = b.Index,
        Location = b.Location,
        RecordCount = b.RecordCount,
        Status = StatusText.ToWorkBatchStatus(b.Status),
        LeaseOwner = b.LeaseOwner,
        RunId = b.RunId,
        CreatedUtc = b.CreatedUtc,
        StartedUtc = b.StartedUtc,
        CompletedUtc = b.CompletedUtc,
        Delivered = b.Delivered,
        Held = b.Held,
        Failed = b.Failed,
        Retrying = b.Retrying,
        Error = b.Error,
    };

    private static void Apply(DeliverySubmission entity, SubmissionState s)
    {
        entity.SubmissionId = s.SubmissionId;
        entity.FlowId = s.FlowId;
        entity.FlowName = s.FlowName;
        entity.MappingReference = s.MappingReference;
        entity.RenderContext = s.RenderContext;
        entity.WorkLocation = Truncate(s.WorkLocation, 2000);
        entity.BatchCount = s.BatchCount;
        entity.Slices = s.Slices;
        entity.ParametersJson = s.ParametersJson;
        entity.RecordCount = s.RecordCount;
        entity.Status = StatusText.Of(s.Status);
        entity.ReceivedUtc = s.ReceivedUtc;
        entity.StartedUtc = s.StartedUtc;
        entity.CompletedUtc = s.CompletedUtc;
        entity.Planned = s.Planned;
        entity.SkippedUnchanged = s.SkippedUnchanged;
        entity.AwaitingApproval = s.AwaitingApproval;
        entity.SkippedStale = s.SkippedStale;
        entity.UnchangedAtPush = s.UnchangedAtPush;
        entity.Blocked = s.Blocked;
        entity.Delivered = s.Delivered;
        entity.Held = s.Held;
        entity.Failed = s.Failed;
        entity.Error = Truncate(s.Error, 4000);
        entity.Kind = SubmissionKinds.All.Contains(s.Kind, StringComparer.Ordinal)
            ? s.Kind
            : throw new DeliveryException($"Submission {s.SubmissionId:D}: '{s.Kind}' is not a submission kind; it is one of {string.Join(", ", SubmissionKinds.All)}.");
        entity.Untracked = s.Untracked;
        entity.SourceConnection = Truncate(s.SourceConnection, 400)!;
        entity.SourceObject = Truncate(s.SourceObject, 400)!;
        entity.WindowFromUtc = s.WindowFromUtc;
        entity.WindowToUtc = s.WindowToUtc;
        entity.SourceWindowJson = s.SourceWindowJson;
        entity.RunId = s.RunId;
    }

    private static SubmissionState ToState(DeliverySubmission e) => new()
    {
        SubmissionId = e.SubmissionId,
        FlowId = e.FlowId,
        FlowName = e.FlowName,
        MappingReference = e.MappingReference,
        RenderContext = e.RenderContext,
        WorkLocation = e.WorkLocation,
        BatchCount = e.BatchCount,
        Slices = e.Slices,
        ParametersJson = e.ParametersJson,
        RecordCount = e.RecordCount,
        Status = StatusText.ToSubmissionStatus(e.Status),
        ReceivedUtc = e.ReceivedUtc,
        StartedUtc = e.StartedUtc,
        CompletedUtc = e.CompletedUtc,
        Planned = e.Planned,
        SkippedUnchanged = e.SkippedUnchanged,
        AwaitingApproval = e.AwaitingApproval,
        SkippedStale = e.SkippedStale,
        UnchangedAtPush = e.UnchangedAtPush,
        Blocked = e.Blocked,
        Delivered = e.Delivered,
        Held = e.Held,
        Failed = e.Failed,
        Error = e.Error,
        Kind = e.Kind,
        Untracked = e.Untracked,
        SourceConnection = e.SourceConnection,
        SourceObject = e.SourceObject,
        WindowFromUtc = e.WindowFromUtc is { } from ? DateTime.SpecifyKind(from, DateTimeKind.Utc) : null,
        WindowToUtc = e.WindowToUtc is { } to ? DateTime.SpecifyKind(to, DateTimeKind.Utc) : null,
        SourceWindowJson = e.SourceWindowJson,
        RunId = e.RunId,
    };

    private static RecordState ToState(DeliveryRecord r) => new()
    {
        DeliveryKey = new DeliveryKey(r.DeliveryKey),
        FlowId = r.FlowId,
        SourceKey = r.SourceKey,
        SourceKeyJson = r.SourceKeyJson,
        Label = r.Label,
        MappingName = r.MappingName,
        RenderContext = r.RenderContext,
        SourceFingerprint = r.SourceFingerprint,
        SourceModifiedUtc = r.SourceModifiedUtc,
        SourceFileName = r.SourceFileName,
        SourceRowNumber = r.SourceRowNumber,
        SourceUpdatedUtc = r.SourceUpdatedUtc,
        MetadataHash = r.MetadataHash,
        PayloadHash = r.PayloadHash,
        PayloadModifiedUtc = r.PayloadModifiedUtc,
        TargetId = r.TargetId,
        ClaimedTargetId = r.ClaimedTargetId,
        TargetVersion = r.TargetVersion,
        Status = StatusText.ToRecordStatus(r.Status),
        LastDeliveredUtc = r.LastDeliveredUtc,
        LastVerifiedUtc = r.LastVerifiedUtc,
        LastVerifyOutcome = StatusText.ToVerifyOutcome(r.LastVerifyOutcome),
        LeaseOwner = r.LeaseOwner,
        LastSubmissionId = r.LastSubmissionId,
        AttemptCount = r.AttemptCount,
        NextAttemptUtc = r.NextAttemptUtc,
        LastError = r.LastError,
        PendingDocumentRef = r.PendingDocumentRef,
        WorkBatch = r.WorkBatch,
        TargetStateJson = r.TargetStateJson,
        PendingStepJson = r.PendingStepJson,
        PendingRenderContext = r.PendingRenderContext,
        PendingSourceFingerprint = r.PendingSourceFingerprint,
        PendingSourceModifiedUtc = r.PendingSourceModifiedUtc,
        PendingSourceFileName = r.PendingSourceFileName,
        PendingSourceRowNumber = r.PendingSourceRowNumber,
        PendingSourceUpdatedUtc = r.PendingSourceUpdatedUtc,
        PendingMetadataHash = r.PendingMetadataHash,
        PendingPayloadHash = r.PendingPayloadHash,
        PendingPayloadModifiedUtc = r.PendingPayloadModifiedUtc,
        PendingPayloadLocation = r.PendingPayloadLocation,
        PendingMetadata = r.PendingMetadata,
        PendingPayload = r.PendingPayload,
        Blocked = r.Blocked,
        CacheSetId = r.CacheSetId,
        PlanRequestedUtc = r.PlanRequestedUtc,
        CreatedUtc = r.CreatedUtc,
        UpdatedUtc = r.UpdatedUtc,
    };
}

/// <summary>The short status strings the ledger tables store, and their enum forms.</summary>
internal static class StatusText
{
    public static string Of(RecordStatus status) => status switch
    {
        RecordStatus.Pending => "pending",
        RecordStatus.Delivering => "delivering",
        RecordStatus.Delivered => "delivered",
        RecordStatus.Held => "held",
        RecordStatus.Failed => "failed",
        RecordStatus.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static RecordStatus ToRecordStatus(string text) => text switch
    {
        "pending" => RecordStatus.Pending,
        "delivering" => RecordStatus.Delivering,
        "delivered" => RecordStatus.Delivered,
        "held" => RecordStatus.Held,
        "failed" => RecordStatus.Failed,
        "deleted" => RecordStatus.Deleted,
        _ => throw new DeliveryException($"Unknown record status '{text}' in the ledger."),
    };

    public static string Of(SubmissionStatus status) => status.ToString().ToLowerInvariant();

    public static SubmissionStatus ToSubmissionStatus(string text)
        => Enum.TryParse<SubmissionStatus>(text, ignoreCase: true, out var s) ? s : throw new DeliveryException($"Unknown submission status '{text}' in the ledger.");

    public static string Of(AttemptOutcome outcome) => outcome.ToString().ToLowerInvariant();

    public static AttemptOutcome ToAttemptOutcome(string text)
        => Enum.TryParse<AttemptOutcome>(text, ignoreCase: true, out var o) ? o : throw new DeliveryException($"Unknown attempt outcome '{text}' in the ledger.");

    public static string Of(VerifyOutcome outcome) => outcome.ToString().ToLowerInvariant();

    public static VerifyOutcome? ToVerifyOutcome(string? text)
        => text is null ? null : Enum.TryParse<VerifyOutcome>(text, ignoreCase: true, out var o) ? o : null;

    public static string Of(WorkBatchStatus status) => status.ToString().ToLowerInvariant();

    public static WorkBatchStatus ToWorkBatchStatus(string text)
        => Enum.TryParse<WorkBatchStatus>(text, ignoreCase: true, out var s) ? s : throw new DeliveryException($"Unknown work batch status '{text}' in the ledger.");
}
