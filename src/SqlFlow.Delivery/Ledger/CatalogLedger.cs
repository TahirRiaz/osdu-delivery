using System.Runtime.CompilerServices;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The ledger over the catalog database (the <c>delivery</c> schema). Claims are compare-and-swap updates (the
/// platform's notification-delivery shape, design.md section 7.5): a batch of candidates is leased in one UPDATE
/// guarded by "not currently leased", then read back by the lease token, so two workers can never hold the same
/// record and a crashed worker's lease simply expires. The same shape claims a whole work batch and its records at
/// once (section 16.2). Every query the GUI issues is index-backed (see <see cref="DeliveryModel"/>). Each
/// operation opens its own context from the factory, so the ledger is safe to share across the worker's bounded
/// concurrency. The two volume writes (staging pending records, closing a drained batch) go through a bulk copy
/// on SQL Server (<see cref="SqlServerLedgerBulk"/>) and through the entity path everywhere else.
/// </summary>
public sealed class CatalogLedger : ILedger
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

    private readonly Func<CatalogDbContext> _factory;
    private readonly TimeProvider _time;

    public CatalogLedger(Func<CatalogDbContext> factory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>A tracking context: the host may pool no-tracking contexts (the control plane does), and the ledger's
    /// read-modify-write operations rely on tracking.</summary>
    private CatalogDbContext Open()
    {
        var db = _factory();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        return db;
    }

    public async Task<SubmissionState?> GetSubmissionAsync(Guid submissionId, CancellationToken ct = default)
    {
        await using var db = Open();
        var entity = await db.DeliverySubmissions.AsNoTracking().FirstOrDefaultAsync(s => s.SubmissionId == submissionId, ct).ConfigureAwait(false);
        return entity is null ? null : ToState(entity);
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
        await using var db = Open();
        var query = db.DeliverySubmissions.AsNoTracking();
        if (flowId is { } f)
        {
            query = query.Where(s => s.FlowId == f);
        }

        var list = await query.OrderByDescending(s => s.ReceivedUtc).Take(Math.Clamp(max, 1, 1000)).ToListAsync(ct).ConfigureAwait(false);
        return list.Select(ToState).ToList();
    }

    public async Task<IReadOnlyDictionary<DeliveryKey, RecordState>> GetRecordsAsync(Guid flowId, IEnumerable<DeliveryKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = new Dictionary<DeliveryKey, RecordState>();
        await using var db = Open();
        foreach (var chunk in keys.Select(k => k.Value).Distinct().Chunk(ChunkSize))
        {
            var rows = await db.DeliveryRecords.AsNoTracking().Where(r => r.FlowId == flowId && chunk.Contains(r.DeliveryKey)).ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                result[new DeliveryKey(row.DeliveryKey)] = ToState(row);
            }
        }

        return result;
    }

    public async Task<RecordState?> GetRecordAsync(Guid flowId, DeliveryKey key, CancellationToken ct = default)
    {
        await using var db = Open();
        var row = await db.DeliveryRecords.AsNoTracking().FirstOrDefaultAsync(r => r.FlowId == flowId && r.DeliveryKey == key.Value, ct).ConfigureAwait(false);
        return row is null ? null : ToState(row);
    }

    public async Task<RecordState?> FindRecordAsync(DeliveryKey key, CancellationToken ct = default)
    {
        await using var db = Open();
        var row = await db.DeliveryRecords.AsNoTracking().FirstOrDefaultAsync(r => r.DeliveryKey == key.Value, ct).ConfigureAwait(false);
        return row is null ? null : ToState(row);
    }

    public async Task<PendingStaging> UpsertPendingAsync(IReadOnlyList<RecordState> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return new PendingStaging(0, []);
        }

        var now = Now;
        await using var db = Open();
        if (SqlServerLedgerBulk.Applies(db))
        {
            return await SqlServerLedgerBulk.UpsertPendingAsync(db, records, now, ct).ConfigureAwait(false);
        }

        var delivering = StatusText.Of(RecordStatus.Delivering);
        var staged = 0;
        var refused = new List<DeliveryKey>();
        foreach (var chunk in records.Chunk(ChunkSize))
        {
            var keys = chunk.Select(r => r.DeliveryKey.Value).ToArray();
            var existing = await db.DeliveryRecords.Where(r => keys.Contains(r.DeliveryKey)).ToDictionaryAsync(r => r.DeliveryKey, ct).ConfigureAwait(false);
            foreach (var record in chunk)
            {
                var inFlight = false;
                if (!existing.TryGetValue(record.DeliveryKey.Value, out var entity))
                {
                    entity = new DeliveryRecord
                    {
                        DeliveryKey = record.DeliveryKey.Value,
                        FlowId = record.FlowId,
                        SourceKey = record.SourceKey,
                        MappingName = record.MappingName,
                        CreatedUtc = now,
                    };
                    db.DeliveryRecords.Add(entity);
                    existing[entity.DeliveryKey] = entity;
                }
                else if (HoldsNewerThan(entity, record))
                {
                    refused.Add(record.DeliveryKey);
                    continue;
                }
                else
                {
                    inFlight = entity.Status == delivering && entity.LeaseExpiresUtc is { } expires && expires > now;
                }

                // Current-state columns (what OSDU holds) are preserved; only the pending work is (re)written. A record
                // another worker is delivering right now keeps its status, lease and retry count: the new work queues
                // behind the delivery, whose completion leaves it pending for the next pass.
                entity.SourceKey = Truncate(record.SourceKey, 400)!;
                entity.Label = Truncate(record.Label, 400);
                entity.MappingName = record.MappingName;
                entity.TargetId ??= record.TargetId;
                entity.LastSubmissionId = record.LastSubmissionId;
                entity.NextAttemptUtc = null;
                if (!inFlight)
                {
                    entity.Status = StatusText.Of(RecordStatus.Pending);
                    entity.AttemptCount = 0;
                    entity.LastError = null;
                    entity.LeaseOwner = null;
                    entity.LeaseExpiresUtc = null;
                }

                entity.PendingDocumentRef = record.PendingDocumentRef;
                entity.WorkBatch = record.WorkBatch;
                entity.PendingStepJson = null;
                entity.PendingRenderContext = record.PendingRenderContext;
                entity.PendingSourceFingerprint = record.PendingSourceFingerprint;
                entity.PendingSourceModifiedUtc = record.PendingSourceModifiedUtc;
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

        return new PendingStaging(staged, refused);
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

                if (skip.Kind == SkipKind.Stale)
                {
                    // The record is left exactly as it is: the attempt is the whole of what happened, and says which
                    // version the drop carried and which one stands.
                    db.DeliveryAttempts.Add(new DeliveryAttempt
                    {
                        DeliveryKey = entity.DeliveryKey,
                        SubmissionId = submissionId,
                        RunId = skip.RunId,
                        Worker = "intake",
                        StartedUtc = now,
                        CompletedUtc = now,
                        Outcome = StatusText.Of(AttemptOutcome.Skipped),
                        Phase = AttemptPhases.Stale,
                        Error = Truncate(Http.HeaderRedaction.RedactMessage(skip.Reason), 2000),
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

    public async Task MarkHeldAsync(IEnumerable<RecordState> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var now = Now;
        await using var db = Open();
        foreach (var chunk in records.Chunk(ChunkSize))
        {
            var keys = chunk.Select(r => r.DeliveryKey.Value).ToArray();
            var existing = await db.DeliveryRecords.Where(r => keys.Contains(r.DeliveryKey)).ToDictionaryAsync(r => r.DeliveryKey, ct).ConfigureAwait(false);
            foreach (var record in chunk)
            {
                if (!existing.TryGetValue(record.DeliveryKey.Value, out var entity))
                {
                    entity = new DeliveryRecord
                    {
                        DeliveryKey = record.DeliveryKey.Value,
                        FlowId = record.FlowId,
                        SourceKey = Truncate(record.SourceKey, 400)!,
                        MappingName = record.MappingName,
                        CreatedUtc = now,
                    };
                    db.DeliveryRecords.Add(entity);
                }

                entity.Label = Truncate(record.Label, 400) ?? entity.Label;
                entity.TargetId ??= record.TargetId;
                entity.Status = StatusText.Of(RecordStatus.Held);
                entity.LastError = Truncate(record.LastError, 2000);
                entity.LastSubmissionId = record.LastSubmissionId;
                entity.LeaseOwner = null;
                entity.LeaseExpiresUtc = null;
                entity.PendingDocumentRef = null;
                entity.WorkBatch = null;
                entity.PendingStepJson = null;
                entity.PendingSourceFingerprint = record.PendingSourceFingerprint;
                entity.PendingSourceModifiedUtc = record.PendingSourceModifiedUtc;
                entity.PendingMetadata = false;
                entity.PendingPayload = false;
                entity.Blocked = true;
                entity.UpdatedUtc = now;
                db.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    DeliveryKey = record.DeliveryKey.Value,
                    SubmissionId = record.LastSubmissionId,
                    RunId = record.RunId,
                    Worker = "intake",
                    StartedUtc = now,
                    CompletedUtc = now,
                    Outcome = StatusText.Of(AttemptOutcome.Held),
                    Phase = "render",
                    Error = Truncate(record.LastError, 2000),
                });
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<RecordState>> ClaimAsync(Guid flowId, Guid? submissionId, string owner, int max, TimeSpan lease, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var token = owner + "/" + Guid.NewGuid().ToString("N");
        var expires = nowUtc + lease;
        var pending = StatusText.Of(RecordStatus.Pending);
        var delivering = StatusText.Of(RecordStatus.Delivering);

        await using var db = Open();
        var candidates = await db.DeliveryRecords
            .Where(r => r.FlowId == flowId
                && (submissionId == null || r.LastSubmissionId == submissionId)
                && ((r.Status == pending && (r.NextAttemptUtc == null || r.NextAttemptUtc <= nowUtc))
                    || (r.Status == delivering && r.LeaseExpiresUtc != null && r.LeaseExpiresUtc < nowUtc)))
            .OrderBy(r => r.NextAttemptUtc)
            .ThenBy(r => r.UpdatedUtc)
            .Select(r => r.DeliveryKey)
            .Take(Math.Clamp(max, 1, ChunkSize))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return [];
        }

        var claimed = await db.DeliveryRecords
            .Where(r => candidates.Contains(r.DeliveryKey)
                && ((r.Status == pending && (r.NextAttemptUtc == null || r.NextAttemptUtc <= nowUtc))
                    || (r.Status == delivering && r.LeaseExpiresUtc != null && r.LeaseExpiresUtc < nowUtc)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, delivering)
                .SetProperty(r => r.LeaseOwner, token)
                .SetProperty(r => r.LeaseExpiresUtc, expires)
                .SetProperty(r => r.AttemptCount, r => r.AttemptCount + 1)
                .SetProperty(r => r.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);

        if (claimed == 0)
        {
            return [];
        }

        var rows = await db.DeliveryRecords.AsNoTracking().Where(r => r.LeaseOwner == token).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    public async Task<bool> RenewLeaseAsync(DeliveryKey key, string owner, TimeSpan lease, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        var expires = nowUtc + lease;
        var affected = await db.DeliveryRecords
            .Where(r => r.DeliveryKey == key.Value && r.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeaseExpiresUtc, expires), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> ReleaseLeaseAsync(DeliveryKey key, string owner, bool countAttempt, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        await using var db = Open();
        var delivering = StatusText.Of(RecordStatus.Delivering);
        var pending = StatusText.Of(RecordStatus.Pending);
        var affected = await db.DeliveryRecords
            .Where(r => r.DeliveryKey == key.Value && r.LeaseOwner == owner && r.Status == delivering)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, pending)
                .SetProperty(r => r.LeaseOwner, (string?)null)
                .SetProperty(r => r.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(r => r.AttemptCount, r => countAttempt ? r.AttemptCount : (r.AttemptCount > 0 ? r.AttemptCount - 1 : 0))
                .SetProperty(r => r.LastError, "the worker stopped mid-attempt and released the record for the next pass")
                .SetProperty(r => r.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    public Task CompleteAsync(RecordCompletion completion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return CompleteManyAsync([completion], ct);
    }

    public async Task CompleteManyAsync(IReadOnlyList<RecordCompletion> completions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(completions);
        if (completions.Count == 0)
        {
            return;
        }

        var now = Now;
        await using var db = Open();
        if (SqlServerLedgerBulk.Applies(db) && completions.Count > 1)
        {
            await SqlServerLedgerBulk.CompleteManyAsync(db, completions, now, ct).ConfigureAwait(false);
            return;
        }

        foreach (var chunk in completions.Chunk(ChunkSize))
        {
            var keys = chunk.Select(c => c.DeliveryKey.Value).ToArray();
            var entities = await db.DeliveryRecords.Where(r => keys.Contains(r.DeliveryKey)).ToDictionaryAsync(r => r.DeliveryKey, ct).ConfigureAwait(false);
            foreach (var completion in chunk)
            {
                if (!entities.TryGetValue(completion.DeliveryKey.Value, out var entity))
                {
                    throw new DeliveryException($"Record {completion.DeliveryKey} is not in the ledger.");
                }

                db.DeliveryAttempts.Add(ToEntity(completion.Attempt));
                ApplyCompletion(entity, completion, now);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }


    private static void ApplyCompletion(DeliveryRecord entity, RecordCompletion completion, DateTime now)
    {
        entity.LeaseOwner = null;
        entity.LeaseExpiresUtc = null;
        entity.UpdatedUtc = now;

        // What the try did to the target is true whatever happened to the queue in the meantime.
        if (completion.TargetId is not null)
        {
            entity.TargetId = completion.TargetId;
        }

        if (completion.TargetVersion is not null)
        {
            entity.TargetVersion = completion.TargetVersion;
        }

        if (completion.TargetStateJson is not null)
        {
            entity.TargetStateJson = completion.TargetStateJson;
        }

        if (completion.Claimed is { } claimed && IsSuperseded(entity, claimed))
        {
            // Newer work was queued while this try was in flight. What the try delivered is now what OSDU holds, and
            // the newer work stays pending with its own step progress and a fresh retry budget: the next pass sends
            // it, after the final hash check against what just landed.
            if (completion.Promote)
            {
                entity.RenderContext = claimed.RenderContext ?? entity.RenderContext;
                entity.SourceFingerprint = claimed.SourceFingerprint ?? entity.SourceFingerprint;
                entity.SourceModifiedUtc = claimed.SourceModifiedUtc ?? entity.SourceModifiedUtc;
                if (claimed.Metadata)
                {
                    entity.MetadataHash = claimed.MetadataHash;
                }

                if (claimed.Payload)
                {
                    entity.PayloadHash = claimed.PayloadHash;
                    entity.PayloadModifiedUtc = claimed.PayloadModifiedUtc ?? entity.PayloadModifiedUtc;
                }

                if (!completion.NothingSent)
                {
                    entity.LastDeliveredUtc = now;
                    entity.LastVerifiedUtc = null;
                    entity.LastVerifyOutcome = null;
                }
            }

            entity.Status = StatusText.Of(RecordStatus.Pending);
            entity.Blocked = false;
            entity.NextAttemptUtc = null;
            entity.LastError = null;
            entity.AttemptCount = 0;
            return;
        }

        entity.Status = StatusText.Of(completion.Status);
        entity.Blocked = completion.Status is RecordStatus.Held or RecordStatus.Failed;
        entity.NextAttemptUtc = completion.NextAttemptUtc;
        entity.LastError = Truncate(completion.Error, 2000);
        entity.PendingStepJson = completion.PendingStepJson;
        if (completion.Promote)
        {
            entity.RenderContext = entity.PendingRenderContext ?? entity.RenderContext;
            entity.SourceFingerprint = entity.PendingSourceFingerprint ?? entity.SourceFingerprint;
            entity.SourceModifiedUtc = entity.PendingSourceModifiedUtc ?? entity.SourceModifiedUtc;
            if (entity.PendingMetadata)
            {
                entity.MetadataHash = entity.PendingMetadataHash;
            }

            if (entity.PendingPayload)
            {
                entity.PayloadHash = entity.PendingPayloadHash;
                entity.PayloadModifiedUtc = entity.PendingPayloadModifiedUtc ?? entity.PayloadModifiedUtc;
            }

            if (!completion.NothingSent)
            {
                entity.LastDeliveredUtc = now;
                entity.LastVerifiedUtc = null;
                entity.LastVerifyOutcome = null;
            }

            entity.PendingDocumentRef = null;
            entity.WorkBatch = null;
            entity.PendingMetadata = false;
            entity.PendingPayload = false;
            entity.PendingPayloadLocation = null;
            entity.AttemptCount = 0;
        }
    }

    /// <summary>
    /// Whether the record now carries other pending work than the claimed try: newer work queued behind it. A record
    /// whose pending document is gone (a removal while the try ran) is not superseded; the completion settles it.
    /// </summary>
    private static bool IsSuperseded(DeliveryRecord entity, ClaimedWork claimed)
        => entity.PendingDocumentRef is not null
            && (!string.Equals(entity.PendingDocumentRef, claimed.DocumentRef, StringComparison.Ordinal) || entity.LastSubmissionId != claimed.SubmissionId);

    public async Task SaveStepAsync(DeliveryKey key, Guid? submissionId, string documentRef, string stepJson, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentRef);
        await using var db = Open();
        await db.DeliveryRecords
            .Where(r => r.DeliveryKey == key.Value && r.PendingDocumentRef == documentRef && r.LastSubmissionId == submissionId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.PendingStepJson, stepJson), ct)
            .ConfigureAwait(false);
    }

    public async Task<long> CountAttemptsAsync(Guid submissionId, AttemptOutcome outcome, string? phase = null, CancellationToken ct = default)
    {
        await using var db = Open();
        var text = StatusText.Of(outcome);
        var query = db.DeliveryAttempts.AsNoTracking().Where(a => a.SubmissionId == submissionId && a.Outcome == text);
        if (phase is not null)
        {
            query = query.Where(a => a.Phase == phase);
        }

        return await query.Select(a => a.DeliveryKey).Distinct().LongCountAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> ReclaimExpiredLeasesAsync(Guid flowId, DateTime beforeUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        var delivering = StatusText.Of(RecordStatus.Delivering);
        var pending = StatusText.Of(RecordStatus.Pending);
        var running = StatusText.Of(WorkBatchStatus.Running);
        var queued = StatusText.Of(WorkBatchStatus.Queued);
        await db.DeliveryWorkBatches
            .Where(b => b.FlowId == flowId && b.Status == running && b.LeaseExpiresUtc != null && b.LeaseExpiresUtc < beforeUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, queued)
                .SetProperty(b => b.LeaseOwner, (string?)null)
                .SetProperty(b => b.LeaseExpiresUtc, (DateTime?)null), ct)
            .ConfigureAwait(false);
        return await db.DeliveryRecords
            .Where(r => r.FlowId == flowId && r.Status == delivering && r.LeaseExpiresUtc != null && r.LeaseExpiresUtc < beforeUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, pending)
                .SetProperty(r => r.LeaseOwner, (string?)null)
                .SetProperty(r => r.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(r => r.LastError, "the lease expired mid-attempt (the worker stopped) and the record was requeued"), ct)
            .ConfigureAwait(false);
    }

    public async Task<long> CountAsync(Guid flowId, Guid? submissionId, RecordStatus status, CancellationToken ct = default)
    {
        await using var db = Open();
        var text = StatusText.Of(status);
        return await db.DeliveryRecords.LongCountAsync(r => r.FlowId == flowId && (submissionId == null || r.LastSubmissionId == submissionId) && r.Status == text, ct).ConfigureAwait(false);
    }

    public async Task<bool> HasPendingAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        var pending = StatusText.Of(RecordStatus.Pending);
        var delivering = StatusText.Of(RecordStatus.Delivering);
        return await db.DeliveryRecords.AnyAsync(r => r.FlowId == flowId
            && (submissionId == null || r.LastSubmissionId == submissionId)
            && (r.Status == pending || r.Status == delivering), ct).ConfigureAwait(false);
    }

    public async Task<DateTime?> NextDueAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        var pending = StatusText.Of(RecordStatus.Pending);
        return await db.DeliveryRecords
            .Where(r => r.FlowId == flowId && (submissionId == null || r.LastSubmissionId == submissionId) && r.Status == pending && r.NextAttemptUtc != null && r.NextAttemptUtc > nowUtc)
            .MinAsync(r => r.NextAttemptUtc, ct)
            .ConfigureAwait(false);
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
                    .SetProperty(b => b.LeaseExpiresUtc, (DateTime?)null)
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

    public async Task<ClaimedWorkBatch?> ClaimWorkBatchAsync(Guid flowId, Guid? submissionId, string owner, TimeSpan lease, DateTime nowUtc, Guid? runId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var queued = StatusText.Of(WorkBatchStatus.Queued);
        var running = StatusText.Of(WorkBatchStatus.Running);
        var expires = nowUtc + lease;
        await using var db = Open();

        // Losing the race for a candidate is ordinary; the next candidate is tried a few times before answering "nothing now".
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var candidate = await db.DeliveryWorkBatches.AsNoTracking()
                .Where(b => b.FlowId == flowId
                    && (submissionId == null || b.SubmissionId == submissionId)
                    && (b.Status == queued || (b.Status == running && b.LeaseExpiresUtc != null && b.LeaseExpiresUtc < nowUtc)))
                .OrderBy(b => b.CreatedUtc)
                .ThenBy(b => b.Index)
                .Select(b => new { b.SubmissionId, b.Index })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (candidate is null)
            {
                return null;
            }

            var token = owner + "/" + Guid.NewGuid().ToString("N");
            var won = await db.DeliveryWorkBatches
                .Where(b => b.SubmissionId == candidate.SubmissionId && b.Index == candidate.Index
                    && (b.Status == queued || (b.Status == running && b.LeaseExpiresUtc != null && b.LeaseExpiresUtc < nowUtc)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, running)
                    .SetProperty(b => b.LeaseOwner, token)
                    .SetProperty(b => b.LeaseExpiresUtc, expires)
                    .SetProperty(b => b.RunId, b => runId ?? b.RunId)
                    .SetProperty(b => b.StartedUtc, b => b.StartedUtc ?? nowUtc), ct)
                .ConfigureAwait(false);
            if (won == 0)
            {
                continue;
            }

            // Lease the batch's records that are due under the same token: the individual claim path then never
            // sees them, and an expired lease hands them back exactly as for a single record.
            var pending = StatusText.Of(RecordStatus.Pending);
            var delivering = StatusText.Of(RecordStatus.Delivering);
            await db.DeliveryRecords
                .Where(r => r.LastSubmissionId == candidate.SubmissionId && r.WorkBatch == candidate.Index
                    && ((r.Status == pending && (r.NextAttemptUtc == null || r.NextAttemptUtc <= nowUtc))
                        || (r.Status == delivering && r.LeaseExpiresUtc != null && r.LeaseExpiresUtc < nowUtc)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, delivering)
                    .SetProperty(r => r.LeaseOwner, token)
                    .SetProperty(r => r.LeaseExpiresUtc, expires)
                    .SetProperty(r => r.AttemptCount, r => r.AttemptCount + 1)
                    .SetProperty(r => r.UpdatedUtc, nowUtc), ct)
                .ConfigureAwait(false);

            var batch = await db.DeliveryWorkBatches.AsNoTracking().FirstAsync(b => b.SubmissionId == candidate.SubmissionId && b.Index == candidate.Index, ct).ConfigureAwait(false);
            var rows = await db.DeliveryRecords.AsNoTracking().Where(r => r.LeaseOwner == token).ToListAsync(ct).ConfigureAwait(false);
            return new ClaimedWorkBatch(ToState(batch), rows.Select(ToState).ToList());
        }

        return null;
    }

    public async Task<bool> RenewWorkBatchLeaseAsync(Guid submissionId, int batch, string owner, TimeSpan lease, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        var expires = nowUtc + lease;
        var affected = await db.DeliveryWorkBatches
            .Where(b => b.SubmissionId == submissionId && b.Index == batch && b.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.LeaseExpiresUtc, expires), ct)
            .ConfigureAwait(false);
        if (affected == 0)
        {
            return false;
        }

        await db.DeliveryRecords
            .Where(r => r.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeaseExpiresUtc, expires), ct)
            .ConfigureAwait(false);
        return true;
    }

    public async Task CompleteWorkBatchAsync(Guid submissionId, int batch, string owner, WorkBatchStatus status, long delivered, long held, long failed, long retrying, string? failure, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        var text = StatusText.Of(status);
        await db.DeliveryWorkBatches
            .Where(b => b.SubmissionId == submissionId && b.Index == batch)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, text)
                .SetProperty(b => b.LeaseOwner, (string?)null)
                .SetProperty(b => b.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(b => b.CompletedUtc, nowUtc)
                .SetProperty(b => b.Delivered, delivered)
                .SetProperty(b => b.Held, held)
                .SetProperty(b => b.Failed, failed)
                .SetProperty(b => b.Retrying, retrying)
                .SetProperty(b => b.Error, Truncate(failure, 2000)), ct)
            .ConfigureAwait(false);

        // Anything still leased under the batch's token was never reached (a stop, a crash mid-batch): hand it back
        // without charging the try.
        await ReleaseRecordsAsync(db, owner, nowUtc, ct).ConfigureAwait(false);
    }

    public async Task<bool> ReleaseWorkBatchAsync(Guid submissionId, int batch, string owner, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        var queued = StatusText.Of(WorkBatchStatus.Queued);
        var affected = await db.DeliveryWorkBatches
            .Where(b => b.SubmissionId == submissionId && b.Index == batch && b.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, queued)
                .SetProperty(b => b.LeaseOwner, (string?)null)
                .SetProperty(b => b.LeaseExpiresUtc, (DateTime?)null), ct)
            .ConfigureAwait(false);
        await ReleaseRecordsAsync(db, owner, nowUtc, ct).ConfigureAwait(false);
        return affected > 0;
    }

    private static async Task ReleaseRecordsAsync(CatalogDbContext db, string owner, DateTime nowUtc, CancellationToken ct)
    {
        var delivering = StatusText.Of(RecordStatus.Delivering);
        var pending = StatusText.Of(RecordStatus.Pending);
        await db.DeliveryRecords
            .Where(r => r.LeaseOwner == owner && r.Status == delivering)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, pending)
                .SetProperty(r => r.LeaseOwner, (string?)null)
                .SetProperty(r => r.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(r => r.AttemptCount, r => r.AttemptCount > 0 ? r.AttemptCount - 1 : 0)
                .SetProperty(r => r.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkBatchState>> ListWorkBatchesAsync(Guid submissionId, int max, int offset, CancellationToken ct = default)
    {
        await using var db = Open();
        var rows = await db.DeliveryWorkBatches.AsNoTracking()
            .Where(b => b.SubmissionId == submissionId)
            .OrderBy(b => b.Index)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(max, 1, 1000))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    public async Task<long> CountWorkBatchesAsync(Guid submissionId, WorkBatchStatus? status, CancellationToken ct = default)
    {
        await using var db = Open();
        var query = db.DeliveryWorkBatches.AsNoTracking().Where(b => b.SubmissionId == submissionId);
        if (status is { } s)
        {
            var text = StatusText.Of(s);
            query = query.Where(b => b.Status == text);
        }

        return await query.LongCountAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecordState>> ListAsync(Guid flowId, RecordQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = Open();
        var rows = Filter(db, db.DeliveryRecords.AsNoTracking().Where(r => r.FlowId == flowId), query);
        var list = await rows
            .OrderByDescending(r => r.UpdatedUtc)
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Clamp(query.Max, 1, 1000))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return list.Select(ToState).ToList();
    }

    public async Task<int> CountAsync(Guid flowId, RecordQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = Open();
        return await Filter(db, db.DeliveryRecords.AsNoTracking().Where(r => r.FlowId == flowId), query).CountAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DeliveryKey>> ListKeysAsync(Guid flowId, RecordQuery query, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = Open();
        var keys = await Filter(db, db.DeliveryRecords.AsNoTracking().Where(r => r.FlowId == flowId), query)
            .OrderBy(r => r.DeliveryKey)
            .Select(r => r.DeliveryKey)
            .Take(Math.Clamp(max, 1, RemovalLimits.MaxSelection))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return keys.Select(k => new DeliveryKey(k)).ToList();
    }

    public async Task<IReadOnlyList<RecordState>> LookupAsync(string term, int max, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        await using var db = Open();
        var rows = await LookupFilter(db.DeliveryRecords.AsNoTracking(), term)
            .OrderByDescending(r => r.UpdatedUtc)
            .Take(Math.Clamp(max, 1, 200))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    public async Task<int> CountLookupAsync(string term, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        await using var db = Open();
        return await LookupFilter(db.DeliveryRecords.AsNoTracking(), term).CountAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A UUID is a delivery key; anything else is a prefix over the three identity columns, each with its own index.</summary>
    private static IQueryable<DeliveryRecord> LookupFilter(IQueryable<DeliveryRecord> rows, string term)
    {
        var t = term.Trim();
        if (Guid.TryParse(t, out var key))
        {
            return rows.Where(r => r.DeliveryKey == key);
        }

        return rows.Where(r => (r.TargetId != null && r.TargetId.StartsWith(t)) || r.SourceKey.StartsWith(t) || (r.Label != null && r.Label.StartsWith(t)));
    }

    private static IQueryable<DeliveryRecord> Filter(CatalogDbContext db, IQueryable<DeliveryRecord> rows, RecordQuery query)
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
            // The attempt table is indexed on RunId, so this is a semi-join over that index rather than a scan.
            var touched = db.DeliveryAttempts.AsNoTracking().Where(a => a.RunId == runId).Select(a => a.DeliveryKey);
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

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            if (Guid.TryParse(term, out var key))
            {
                rows = rows.Where(r => r.DeliveryKey == key);
            }
            else if (query.Mode == SearchMode.Contains)
            {
                rows = rows.Where(r => r.SourceKey.Contains(term) || (r.Label != null && r.Label.Contains(term)) || (r.TargetId != null && r.TargetId.Contains(term)));
            }
            else
            {
                // Prefix matches translate to LIKE 'term%' and use the (FlowId, column) indexes.
                rows = rows.Where(r => r.SourceKey.StartsWith(term) || (r.Label != null && r.Label.StartsWith(term)) || (r.TargetId != null && r.TargetId.StartsWith(term)));
            }
        }

        return rows;
    }

    public async Task<FlowStats> StatsAsync(Guid flowId, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        var counts = await db.DeliveryRecords
            .Where(r => r.FlowId == flowId)
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.LongCount() })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byStatus = counts.ToDictionary(c => c.Status, c => c.Count, StringComparer.Ordinal);
        var since = nowUtc.AddHours(-24);
        var drifted = await db.DeliveryRecords.LongCountAsync(r => r.FlowId == flowId && (r.LastVerifyOutcome == "drifted" || r.LastVerifyOutcome == "missing"), ct).ConfigureAwait(false);
        var last24 = await db.DeliveryRecords.LongCountAsync(r => r.FlowId == flowId && r.LastDeliveredUtc != null && r.LastDeliveredUtc >= since, ct).ConfigureAwait(false);
        var lastDelivered = await db.DeliveryRecords.Where(r => r.FlowId == flowId).MaxAsync(r => r.LastDeliveredUtc, ct).ConfigureAwait(false);
        var lastVerified = await db.DeliveryRecords.Where(r => r.FlowId == flowId).MaxAsync(r => r.LastVerifiedUtc, ct).ConfigureAwait(false);
        var submissions = await db.DeliverySubmissions.LongCountAsync(s => s.FlowId == flowId, ct).ConfigureAwait(false);
        var lastSubmission = await db.DeliverySubmissions.AsNoTracking().Where(s => s.FlowId == flowId).OrderByDescending(s => s.ReceivedUtc).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return new FlowStats
        {
            Total = counts.Sum(c => c.Count),
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

    public async Task<IReadOnlyList<AttemptRecord>> ListAttemptsAsync(DeliveryKey key, int max, CancellationToken ct = default)
    {
        await using var db = Open();
        var rows = await db.DeliveryAttempts.AsNoTracking()
            .Where(a => a.DeliveryKey == key.Value)
            .OrderByDescending(a => a.StartedUtc)
            .ThenByDescending(a => a.AttemptId)
            .Take(Math.Clamp(max, 1, 1000))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<AttemptRecord>> ListAttemptsForSubmissionAsync(Guid submissionId, int max, CancellationToken ct = default)
    {
        await using var db = Open();
        var rows = await db.DeliveryAttempts.AsNoTracking()
            .Where(a => a.SubmissionId == submissionId)
            .OrderByDescending(a => a.StartedUtc)
            .ThenByDescending(a => a.AttemptId)
            .Take(Math.Clamp(max, 1, 5000))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<RecordState>> ListForVerifyAsync(Guid flowId, DateTime? verifiedBeforeUtc, int max, CancellationToken ct = default)
    {
        await using var db = Open();
        var delivered = StatusText.Of(RecordStatus.Delivered);
        var query = db.DeliveryRecords.AsNoTracking().Where(r => r.FlowId == flowId && r.Status == delivered && r.TargetId != null);
        if (verifiedBeforeUtc is { } before)
        {
            query = query.Where(r => r.LastVerifiedUtc == null || r.LastVerifiedUtc < before);
        }

        var rows = await query.OrderBy(r => r.LastVerifiedUtc).Take(Math.Clamp(max, 1, 10_000)).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    public async Task RecordVerifyAsync(DeliveryKey key, VerifyOutcome outcome, long? observedVersion, DateTime nowUtc, bool requeue, CancellationToken ct = default)
    {
        await using var db = Open();
        var entity = await db.DeliveryRecords.FirstOrDefaultAsync(r => r.DeliveryKey == key.Value, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Record {key} is not in the ledger.");
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

        // Records that still hold their rendered document go straight back to the worker.
        var requeued = await blocked.Where(r => r.PendingDocumentRef != null).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, pending)
            .SetProperty(r => r.Blocked, false)
            .SetProperty(r => r.AttemptCount, 0)
            .SetProperty(r => r.NextAttemptUtc, (DateTime?)null)
            .SetProperty(r => r.LastError, (string?)null)
            .SetProperty(r => r.UpdatedUtc, nowUtc), ct).ConfigureAwait(false);

        // The others are unblocked: the next submission plans them again from the source.
        var unblocked = await blocked.Where(r => r.PendingDocumentRef == null).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Blocked, false)
            .SetProperty(r => r.LastError, "released; will be planned again on the next submission")
            .SetProperty(r => r.UpdatedUtc, nowUtc), ct).ConfigureAwait(false);

        return requeued + unblocked;
    }

    public async Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey> keys, RedeliverScope scope, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        await using var db = Open();
        var ids = keys.Select(k => k.Value).ToArray();
        var rows = db.DeliveryRecords.Where(r => r.FlowId == flowId && ids.Contains(r.DeliveryKey));
        var note = $"redelivery of {scope.ToString().ToLowerInvariant()} requested";
        return scope switch
        {
            RedeliverScope.Metadata => await rows.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.MetadataHash, (string?)null)
                .SetProperty(r => r.SourceFingerprint, (string?)null)
                .SetProperty(r => r.PendingSourceFingerprint, (string?)null)
                .SetProperty(r => r.LastError, note)
                .SetProperty(r => r.UpdatedUtc, nowUtc), ct).ConfigureAwait(false),
            RedeliverScope.Payload => await rows.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.PayloadHash, (string?)null)
                .SetProperty(r => r.PayloadModifiedUtc, (DateTime?)null)
                .SetProperty(r => r.SourceFingerprint, (string?)null)
                .SetProperty(r => r.PendingSourceFingerprint, (string?)null)
                .SetProperty(r => r.LastError, note)
                .SetProperty(r => r.UpdatedUtc, nowUtc), ct).ConfigureAwait(false),
            _ => await rows.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.MetadataHash, (string?)null)
                .SetProperty(r => r.PayloadHash, (string?)null)
                .SetProperty(r => r.PayloadModifiedUtc, (DateTime?)null)
                .SetProperty(r => r.SourceFingerprint, (string?)null)
                .SetProperty(r => r.PendingSourceFingerprint, (string?)null)
                .SetProperty(r => r.LastError, note)
                .SetProperty(r => r.UpdatedUtc, nowUtc), ct).ConfigureAwait(false),
        };
    }

    public async Task MarkRemovedAsync(IReadOnlyList<DeliveryKey> keys, RemovalScope scope, string worker, DateTime nowUtc, CancellationToken ct = default)
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
            var ids = chunk.Select(k => k.Value).ToList();
            var entities = await db.DeliveryRecords.Where(r => ids.Contains(r.DeliveryKey)).ToListAsync(ct).ConfigureAwait(false);
            if (entities.Count != chunk.Length)
            {
                var missing = ids.Except(entities.Select(e => e.DeliveryKey)).First();
                throw new DeliveryException($"Record {missing} is not in the ledger.");
            }

            foreach (var entity in entities)
            {
                MarkRemoved(db, entity, scope, worker, note, nowUtc);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static void MarkRemoved(CatalogDbContext db, DeliveryRecord entity, RemovalScope scope, string worker, string note, DateTime nowUtc)
    {
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            DeliveryKey = entity.DeliveryKey,
            SubmissionId = entity.LastSubmissionId,
            Worker = worker,
            StartedUtc = nowUtc,
            CompletedUtc = nowUtc,
            Outcome = StatusText.Of(scope == RemovalScope.History ? AttemptOutcome.HistoryPurged : AttemptOutcome.Deleted),
            Phase = scope == RemovalScope.History ? "purge-history" : "delete",
            TargetVersion = entity.TargetVersion,
            Error = note,
            ResultJson = entity.TargetStateJson,
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
        entity.LeaseExpiresUtc = null;
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

    public async Task<IReadOnlyList<KnownState>> KnownStateAsync(Guid flowId, CancellationToken ct = default)
    {
        var list = new List<KnownState>();
        await foreach (var row in StreamKnownStateAsync(flowId, 10_000, ct).ConfigureAwait(false))
        {
            list.Add(row);
        }

        return list;
    }

    public async IAsyncEnumerable<KnownState> StreamKnownStateAsync(Guid flowId, int pageSize = 10_000, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var size = Math.Clamp(pageSize, 100, 100_000);
        Guid? after = null;
        while (true)
        {
            List<KnownState> page;
            await using (var db = Open())
            {
                var query = db.DeliveryRecords.AsNoTracking().Where(r => r.FlowId == flowId);
                if (after is { } a)
                {
                    query = query.Where(r => r.DeliveryKey.CompareTo(a) > 0);
                }

                var rows = await query
                    .OrderBy(r => r.DeliveryKey)
                    .Select(r => new { r.DeliveryKey, r.SourceKey, r.SourceFingerprint, r.SourceModifiedUtc, r.MetadataHash, r.PayloadHash, r.PayloadModifiedUtc, r.Status, r.TargetId, r.TargetVersion })
                    .Take(size)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                page = rows.Select(r => new KnownState(
                    new DeliveryKey(r.DeliveryKey), r.SourceKey, r.SourceFingerprint, r.SourceModifiedUtc, r.MetadataHash, r.PayloadHash, r.PayloadModifiedUtc,
                    StatusText.ToRecordStatus(r.Status), r.TargetId, r.TargetVersion)).ToList();
            }

            foreach (var row in page)
            {
                yield return row;
            }

            if (page.Count < size)
            {
                yield break;
            }

            after = page[^1].DeliveryKey.Value;
        }
    }

    public async Task<long> EnsureCacheSetAsync(IReadOnlyList<Snapshots.CacheUsage> usages, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usages);
        var entries = Canonical(usages);
        var hash = SetHash(entries);
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
        await using var db = Open();
        var rows = await db.DeliveryCacheSetEntries.AsNoTracking()
            .Where(e => e.SetId == setId)
            .OrderBy(e => e.TypeName).ThenBy(e => e.Path)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(e => new CacheUse(e.TypeName, e.ItemId, e.Path, ToKind(e.Kind), e.ValueHash, e.ValueText)).ToList();
    }

    public async Task<IReadOnlyList<CacheSetUse>> FindCacheSetsAsync(string typeName, IReadOnlyList<string> itemIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0)
        {
            return [];
        }

        await using var db = Open();
        var found = new List<CacheSetUse>();
        foreach (var chunk in itemIds.Chunk(LookupChunk))
        {
            var ids = chunk.ToList();
            var rows = await db.DeliveryCacheSetEntries.AsNoTracking()
                .Where(e => e.TypeName == typeName && ids.Contains(e.ItemId))
                .ToListAsync(ct).ConfigureAwait(false);
            found.AddRange(rows.Select(e => new CacheSetUse(e.SetId, e.TypeName, e.ItemId, e.Path, ToKind(e.Kind), e.ValueHash, e.ValueText)));
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

        await using var db = Open();
        long total = 0;
        foreach (var chunk in setIds.Chunk(LookupChunk))
        {
            var ids = chunk.ToList();
            total += await db.DeliveryRecords.AsNoTracking()
                .Where(r => r.CacheSetId != null && ids.Contains(r.CacheSetId!.Value))
                .LongCountAsync(ct).ConfigureAwait(false);
        }

        return total;
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
                t => t.TypeName == tag.TypeName && t.ItemId == tag.ItemId && t.Path == tag.Path
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
    {
        await using var db = Open();
        return await db.DeliveryCacheSets.AsNoTracking().Where(c => c.Gated).Select(c => c.SetId).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UpdateTag>> ListTagsAsync(string? status, int max, int offset, CancellationToken ct = default)
    {
        await using var db = Open();
        var rows = await TagQuery(db, status)
            .OrderByDescending(t => t.TagId)
            .Skip(Math.Max(0, offset)).Take(Math.Clamp(max, 1, 1000))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToTag).ToList();
    }

    public async Task<int> CountTagsAsync(string? status, CancellationToken ct = default)
    {
        await using var db = Open();
        return await TagQuery(db, status).CountAsync(ct).ConfigureAwait(false);
    }

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

        // One bounded page of records, in key order from where the last pass stopped: a change over millions of
        // records never becomes one statement, and a pass that is interrupted resumes instead of starting over.
        var cursor = tag.Cursor;
        var keys = await db.DeliveryRecords.AsNoTracking()
            .Where(r => r.CacheSetId != null && sets.Contains(r.CacheSetId!.Value) && (cursor == null || r.DeliveryKey.CompareTo(cursor!.Value) > 0))
            .OrderBy(r => r.DeliveryKey)
            .Select(r => r.DeliveryKey)
            .Take(size)
            .ToListAsync(ct).ConfigureAwait(false);

        if (keys.Count > 0)
        {
            // A cache change rewrites the manifest row, never the payload: forgetting the metadata hash and the
            // fingerprint is what makes the next plan render and send the document again, and the curves that
            // were uploaded with it stay where they are. This is the same marking a metadata redelivery makes.
            var note = $"redelivery of metadata requested by cache change {tag.TagId}";
            await db.DeliveryRecords.Where(r => keys.Contains(r.DeliveryKey))
                .ExecuteUpdateAsync(
                    u => u.SetProperty(r => r.MetadataHash, (string?)null)
                          .SetProperty(r => r.SourceFingerprint, (string?)null)
                          .SetProperty(r => r.PendingSourceFingerprint, (string?)null)
                          .SetProperty(r => r.LastError, note)
                          .SetProperty(r => r.UpdatedUtc, nowUtc),
                    ct)
                .ConfigureAwait(false);
            tag.Cursor = keys[^1];
            tag.Processed += keys.Count;
        }

        tag.StartedUtc ??= nowUtc;
        tag.Status = keys.Count < size ? "applied" : "rolling";
        if (tag.Status == "applied")
        {
            tag.CompletedUtc = nowUtc;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new UpdateRolloutBatch(tagId, keys.Count, tag.Processed, tag.AffectedRecords, tag.Status == "applied");
    }

    public async Task<IReadOnlyList<UpdateTag>> ListRolloutQueueAsync(int max, CancellationToken ct = default)
    {
        await using var db = Open();
        var rows = await db.DeliveryUpdateTags.AsNoTracking()
            .Where(t => t.Status == "approved" || t.Status == "rolling")
            .OrderBy(t => t.DecidedUtc ?? t.DetectedUtc)
            .Take(Math.Clamp(max, 1, 1000))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToTag).ToList();
    }

    /// <summary>Gates or releases sets in one statement; the set table is small, the record table is never touched.</summary>
    private static async Task SetGateAsync(CatalogDbContext db, IReadOnlyList<long> setIds, bool gated, CancellationToken ct)
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
    private static async Task ReleaseUngatedAsync(CatalogDbContext db, IReadOnlyList<long> setIds, CancellationToken ct)
    {
        if (setIds.Count == 0)
        {
            return;
        }

        var pending = await db.DeliveryUpdateTags.AsNoTracking().Where(t => t.Status == "pending").Select(t => t.SetIds).ToListAsync(ct).ConfigureAwait(false);
        var stillGated = pending.SelectMany(ParseSets).ToHashSet();
        await SetGateAsync(db, setIds.Where(id => !stillGated.Contains(id)).ToList(), gated: false, ct).ConfigureAwait(false);
    }

    private static IQueryable<DeliveryUpdateTag> TagQuery(CatalogDbContext db, string? status)
    {
        var query = db.DeliveryUpdateTags.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(t => t.Status == s);
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

    private static string SetHash(IReadOnlyList<Snapshots.CacheUsage> entries)
        => Hashing.ContentHash.Of(string.Join('\n', entries.Select(e => $"{e.TypeName}|{e.ItemId}|{e.Path}|{KindText(e.Kind)}|{e.ValueHash}")));

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

    public async Task<IReadOnlyList<SourceWatermark>> GetWatermarksAsync(Guid flowId, string scope, CancellationToken ct = default)
    {
        await using var db = Open();
        var rows = await db.DeliveryWatermarks.AsNoTracking().Where(w => w.FlowId == flowId && w.Scope == scope).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(w => new SourceWatermark(w.FlowId, w.Scope, w.TableName, w.Version, w.RecordedUtc, w.ContextHash)).ToList();
    }

    public async Task SetWatermarksAsync(IEnumerable<SourceWatermark> watermarks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(watermarks);
        await using var db = Open();
        foreach (var w in watermarks)
        {
            var entity = await db.DeliveryWatermarks.FirstOrDefaultAsync(x => x.FlowId == w.FlowId && x.Scope == w.Scope && x.TableName == w.Table, ct).ConfigureAwait(false);
            if (entity is null)
            {
                entity = new DeliverySourceWatermark { FlowId = w.FlowId, Scope = w.Scope, TableName = w.Table };
                db.DeliveryWatermarks.Add(entity);
            }

            entity.Version = w.Version;
            entity.ContextHash = w.ContextHash;
            entity.RecordedUtc = w.RecordedUtc;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> PruneAttemptsAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        // Keep the latest attempt per record so a record's last outcome is always explainable.
        var latest = db.DeliveryAttempts.GroupBy(a => a.DeliveryKey).Select(g => g.Max(a => a.AttemptId));
        return await db.DeliveryAttempts
            .Where(a => a.StartedUtc < olderThanUtc && !latest.Contains(a.AttemptId))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
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
        await using var db = Open();
        var entity = await db.DeliveryRetrievals.AsNoTracking()
            .Where(r => r.FlowId == flowId && r.Status == status)
            .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.RetrievalId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return entity is null ? null : ToState(entity);
    }

    public async Task<IReadOnlyList<RetrievalState>> ListRetrievalsAsync(Guid flowId, int max, CancellationToken ct = default)
    {
        await using var db = Open();
        var rows = await db.DeliveryRetrievals.AsNoTracking()
            .Where(r => r.FlowId == flowId)
            .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.RetrievalId)
            .Take(Math.Clamp(max, 1, 1000))
            .ToListAsync(ct).ConfigureAwait(false);
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
        await using var db = Open();
        var entity = await db.DeliveryActivities.AsNoTracking().FirstOrDefaultAsync(a => a.ActivityId == activityId, ct).ConfigureAwait(false);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<ActivityRecord>> ListActivitiesAsync(ActivityQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = Open();
        var list = await FilterActivities(db.DeliveryActivities.AsNoTracking(), query)
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
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return list.Select(ToRecord).ToList();
    }

    public async Task<int> CountActivitiesAsync(ActivityQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = Open();
        return await FilterActivities(db.DeliveryActivities.AsNoTracking(), query).CountAsync(ct).ConfigureAwait(false);
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

    private static DeliveryAttempt ToEntity(AttemptRecord attempt) => new()
    {
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
        LeaseExpiresUtc = b.LeaseExpiresUtc,
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
        entity.DropLocation = s.DropLocation;
        entity.WorkLocation = Truncate(s.WorkLocation, 2000);
        entity.BatchCount = s.BatchCount;
        entity.Partitions = s.Partitions;
        entity.ParametersJson = s.ParametersJson;
        entity.RecordCount = s.RecordCount;
        entity.Status = StatusText.Of(s.Status);
        entity.ReceivedUtc = s.ReceivedUtc;
        entity.StartedUtc = s.StartedUtc;
        entity.CompletedUtc = s.CompletedUtc;
        entity.Planned = s.Planned;
        entity.SkippedUnchanged = s.SkippedUnchanged;
        entity.SkippedStale = s.SkippedStale;
        entity.UnchangedAtPush = s.UnchangedAtPush;
        entity.Blocked = s.Blocked;
        entity.Delivered = s.Delivered;
        entity.Held = s.Held;
        entity.Failed = s.Failed;
        entity.Error = Truncate(s.Error, 4000);
    }

    private static SubmissionState ToState(DeliverySubmission e) => new()
    {
        SubmissionId = e.SubmissionId,
        FlowId = e.FlowId,
        FlowName = e.FlowName,
        MappingReference = e.MappingReference,
        RenderContext = e.RenderContext,
        DropLocation = e.DropLocation,
        WorkLocation = e.WorkLocation,
        BatchCount = e.BatchCount,
        Partitions = e.Partitions,
        ParametersJson = e.ParametersJson,
        RecordCount = e.RecordCount,
        Status = StatusText.ToSubmissionStatus(e.Status),
        ReceivedUtc = e.ReceivedUtc,
        StartedUtc = e.StartedUtc,
        CompletedUtc = e.CompletedUtc,
        Planned = e.Planned,
        SkippedUnchanged = e.SkippedUnchanged,
        SkippedStale = e.SkippedStale,
        UnchangedAtPush = e.UnchangedAtPush,
        Blocked = e.Blocked,
        Delivered = e.Delivered,
        Held = e.Held,
        Failed = e.Failed,
        Error = e.Error,
    };

    private static RecordState ToState(DeliveryRecord r) => new()
    {
        DeliveryKey = new DeliveryKey(r.DeliveryKey),
        FlowId = r.FlowId,
        SourceKey = r.SourceKey,
        Label = r.Label,
        MappingName = r.MappingName,
        RenderContext = r.RenderContext,
        SourceFingerprint = r.SourceFingerprint,
        SourceModifiedUtc = r.SourceModifiedUtc,
        MetadataHash = r.MetadataHash,
        PayloadHash = r.PayloadHash,
        PayloadModifiedUtc = r.PayloadModifiedUtc,
        TargetId = r.TargetId,
        TargetVersion = r.TargetVersion,
        Status = StatusText.ToRecordStatus(r.Status),
        LastDeliveredUtc = r.LastDeliveredUtc,
        LastVerifiedUtc = r.LastVerifiedUtc,
        LastVerifyOutcome = StatusText.ToVerifyOutcome(r.LastVerifyOutcome),
        LeaseOwner = r.LeaseOwner,
        LeaseExpiresUtc = r.LeaseExpiresUtc,
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
        PendingMetadataHash = r.PendingMetadataHash,
        PendingPayloadHash = r.PendingPayloadHash,
        PendingPayloadModifiedUtc = r.PendingPayloadModifiedUtc,
        PendingPayloadLocation = r.PendingPayloadLocation,
        PendingMetadata = r.PendingMetadata,
        PendingPayload = r.PendingPayload,
        Blocked = r.Blocked,
        CacheSetId = r.CacheSetId,
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
