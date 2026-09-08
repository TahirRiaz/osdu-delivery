using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The ledger over the catalog database (the <c>delivery</c> schema). Claims are compare-and-swap updates (the
/// platform's notification-delivery shape, design.md section 7.5): a batch of candidates is leased in one UPDATE
/// guarded by "not currently leased", then read back by the lease token, so two workers can never hold the same
/// record and a crashed worker's lease simply expires. Every query the GUI issues is index-backed (see
/// <see cref="DeliveryModel"/>). Each operation opens its own context from the factory, so the ledger is safe
/// to share across the worker's bounded concurrency.
/// </summary>
public sealed class CatalogLedger : ILedger
{
    private const int MaxLogLength = 200_000;

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
        foreach (var chunk in keys.Select(k => k.Value).Distinct().Chunk(500))
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

    public async Task UpsertPendingAsync(IEnumerable<RecordState> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var now = Now;
        await using var db = Open();
        foreach (var chunk in records.Chunk(200))
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
                        SourceKey = record.SourceKey,
                        MappingName = record.MappingName,
                        CreatedUtc = now,
                    };
                    db.DeliveryRecords.Add(entity);
                }

                // Current-state columns (what OSDU holds) are preserved; only the pending work is (re)written.
                entity.SourceKey = Truncate(record.SourceKey, 400)!;
                entity.Label = Truncate(record.Label, 400);
                entity.MappingName = record.MappingName;
                entity.TargetId ??= record.TargetId;
                entity.Status = StatusText.Of(RecordStatus.Pending);
                entity.LastSubmissionId = record.LastSubmissionId;
                entity.AttemptCount = 0;
                entity.NextAttemptUtc = null;
                entity.LastError = null;
                entity.LeaseOwner = null;
                entity.LeaseExpiresUtc = null;
                entity.PendingDocument = record.PendingDocument;
                entity.PendingRenderContext = record.PendingRenderContext;
                entity.PendingSourceFingerprint = record.PendingSourceFingerprint;
                entity.PendingMetadataHash = record.PendingMetadataHash;
                entity.PendingPayloadHash = record.PendingPayloadHash;
                entity.PendingPayloadLocation = record.PendingPayloadLocation;
                entity.PendingMetadata = record.PendingMetadata;
                entity.PendingPayload = record.PendingPayload;
                entity.Blocked = false;
                entity.UpdatedUtc = now;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task MarkSkippedAsync(Guid flowId, IEnumerable<DeliveryKey> keys, Guid submissionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var now = Now;
        await using var db = Open();
        foreach (var chunk in keys.Select(k => k.Value).Chunk(500))
        {
            await db.DeliveryRecords
                .Where(r => r.FlowId == flowId && chunk.Contains(r.DeliveryKey))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.LastSubmissionId, submissionId)
                    .SetProperty(r => r.UpdatedUtc, now), ct)
                .ConfigureAwait(false);
        }
    }

    public async Task MarkHeldAsync(IEnumerable<RecordState> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var now = Now;
        await using var db = Open();
        foreach (var chunk in records.Chunk(200))
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
                entity.LastError = record.LastError;
                entity.LastSubmissionId = record.LastSubmissionId;
                entity.LeaseOwner = null;
                entity.LeaseExpiresUtc = null;
                entity.PendingDocument = null;
                entity.PendingSourceFingerprint = record.PendingSourceFingerprint;
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
                    Error = record.LastError,
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
            .Take(Math.Clamp(max, 1, 500))
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

    public async Task CompleteAsync(RecordCompletion completion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var now = Now;
        await using var db = Open();
        var entity = await db.DeliveryRecords.FirstOrDefaultAsync(r => r.DeliveryKey == completion.DeliveryKey.Value, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Record {completion.DeliveryKey} is not in the ledger.");

        db.DeliveryAttempts.Add(ToEntity(completion.Attempt));

        entity.Status = StatusText.Of(completion.Status);
        entity.Blocked = completion.Status is RecordStatus.Held or RecordStatus.Failed;
        entity.LeaseOwner = null;
        entity.LeaseExpiresUtc = null;
        entity.NextAttemptUtc = completion.NextAttemptUtc;
        entity.LastError = completion.Error;
        entity.UpdatedUtc = now;
        if (completion.TargetId is not null)
        {
            entity.TargetId = completion.TargetId;
        }

        if (completion.TargetVersion is not null)
        {
            entity.TargetVersion = completion.TargetVersion;
        }

        if (completion.Promote)
        {
            entity.RenderContext = entity.PendingRenderContext ?? entity.RenderContext;
            entity.SourceFingerprint = entity.PendingSourceFingerprint ?? entity.SourceFingerprint;
            if (entity.PendingMetadata)
            {
                entity.MetadataHash = entity.PendingMetadataHash;
            }

            if (entity.PendingPayload)
            {
                entity.PayloadHash = entity.PendingPayloadHash;
            }

            entity.LastDeliveredUtc = now;
            entity.LastVerifiedUtc = null;
            entity.LastVerifyOutcome = null;
            entity.PendingDocument = null;
            entity.PendingMetadata = false;
            entity.PendingPayload = false;
            entity.PendingPayloadLocation = null;
            entity.AttemptCount = 0;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> ReclaimExpiredLeasesAsync(Guid flowId, DateTime beforeUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        var delivering = StatusText.Of(RecordStatus.Delivering);
        var pending = StatusText.Of(RecordStatus.Pending);
        return await db.DeliveryRecords
            .Where(r => r.FlowId == flowId && r.Status == delivering && r.LeaseExpiresUtc != null && r.LeaseExpiresUtc < beforeUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, pending)
                .SetProperty(r => r.LeaseOwner, (string?)null)
                .SetProperty(r => r.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(r => r.LastError, "the lease expired mid-attempt (the worker stopped) and the record was requeued"), ct)
            .ConfigureAwait(false);
    }

    public async Task<int> CountAsync(Guid flowId, Guid? submissionId, RecordStatus status, CancellationToken ct = default)
    {
        await using var db = Open();
        var text = StatusText.Of(status);
        return await db.DeliveryRecords.CountAsync(r => r.FlowId == flowId && (submissionId == null || r.LastSubmissionId == submissionId) && r.Status == text, ct).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<RecordState>> ListAsync(Guid flowId, RecordQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = Open();
        var rows = Filter(db.DeliveryRecords.AsNoTracking().Where(r => r.FlowId == flowId), query);
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
        return await Filter(db.DeliveryRecords.AsNoTracking().Where(r => r.FlowId == flowId), query).CountAsync(ct).ConfigureAwait(false);
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

    private static IQueryable<DeliveryRecord> Filter(IQueryable<DeliveryRecord> rows, RecordQuery query)
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

        if (query.Drifted)
        {
            rows = rows.Where(r => r.LastVerifyOutcome == "drifted" || r.LastVerifyOutcome == "missing");
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
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byStatus = counts.ToDictionary(c => c.Status, c => c.Count, StringComparer.Ordinal);
        var since = nowUtc.AddHours(-24);
        var drifted = await db.DeliveryRecords.CountAsync(r => r.FlowId == flowId && (r.LastVerifyOutcome == "drifted" || r.LastVerifyOutcome == "missing"), ct).ConfigureAwait(false);
        var last24 = await db.DeliveryRecords.CountAsync(r => r.FlowId == flowId && r.LastDeliveredUtc != null && r.LastDeliveredUtc >= since, ct).ConfigureAwait(false);
        var lastDelivered = await db.DeliveryRecords.Where(r => r.FlowId == flowId).MaxAsync(r => r.LastDeliveredUtc, ct).ConfigureAwait(false);
        var lastVerified = await db.DeliveryRecords.Where(r => r.FlowId == flowId).MaxAsync(r => r.LastVerifiedUtc, ct).ConfigureAwait(false);
        var submissions = await db.DeliverySubmissions.CountAsync(s => s.FlowId == flowId, ct).ConfigureAwait(false);
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
        var requeued = await blocked.Where(r => r.PendingDocument != null).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, pending)
            .SetProperty(r => r.Blocked, false)
            .SetProperty(r => r.AttemptCount, 0)
            .SetProperty(r => r.NextAttemptUtc, (DateTime?)null)
            .SetProperty(r => r.LastError, (string?)null)
            .SetProperty(r => r.UpdatedUtc, nowUtc), ct).ConfigureAwait(false);

        // The others are unblocked: the next submission plans them again from the source.
        var unblocked = await blocked.Where(r => r.PendingDocument == null).ExecuteUpdateAsync(s => s
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
                .SetProperty(r => r.SourceFingerprint, (string?)null)
                .SetProperty(r => r.PendingSourceFingerprint, (string?)null)
                .SetProperty(r => r.LastError, note)
                .SetProperty(r => r.UpdatedUtc, nowUtc), ct).ConfigureAwait(false),
            _ => await rows.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.MetadataHash, (string?)null)
                .SetProperty(r => r.PayloadHash, (string?)null)
                .SetProperty(r => r.SourceFingerprint, (string?)null)
                .SetProperty(r => r.PendingSourceFingerprint, (string?)null)
                .SetProperty(r => r.LastError, note)
                .SetProperty(r => r.UpdatedUtc, nowUtc), ct).ConfigureAwait(false),
        };
    }

    public async Task MarkDeletedAsync(DeliveryKey key, bool purged, string worker, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        await using var db = Open();
        var entity = await db.DeliveryRecords.FirstOrDefaultAsync(r => r.DeliveryKey == key.Value, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Record {key} is not in the ledger.");

        var note = purged ? $"purged from OSDU by {worker}" : $"deleted from OSDU (logical) by {worker}";
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            DeliveryKey = key.Value,
            SubmissionId = entity.LastSubmissionId,
            Worker = worker,
            StartedUtc = nowUtc,
            CompletedUtc = nowUtc,
            Outcome = StatusText.Of(AttemptOutcome.Deleted),
            Phase = "delete",
            TargetVersion = entity.TargetVersion,
            Error = note,
        });

        entity.Status = StatusText.Of(RecordStatus.Deleted);
        entity.Blocked = true;
        // The unchanged source keeps the record blocked; a source change or a release plans it again.
        entity.PendingSourceFingerprint = entity.SourceFingerprint;
        entity.TargetVersion = null;
        entity.MetadataHash = null;
        entity.PayloadHash = null;
        entity.SourceFingerprint = null;
        entity.LastVerifiedUtc = null;
        entity.LastVerifyOutcome = null;
        entity.LeaseOwner = null;
        entity.LeaseExpiresUtc = null;
        entity.NextAttemptUtc = null;
        entity.AttemptCount = 0;
        entity.PendingDocument = null;
        entity.PendingMetadata = false;
        entity.PendingPayload = false;
        entity.PendingPayloadLocation = null;
        entity.LastError = note;
        entity.UpdatedUtc = nowUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnownState>> KnownStateAsync(Guid flowId, CancellationToken ct = default)
    {
        await using var db = Open();
        var rows = await db.DeliveryRecords.AsNoTracking()
            .Where(r => r.FlowId == flowId)
            .OrderBy(r => r.DeliveryKey)
            .Select(r => new { r.DeliveryKey, r.SourceKey, r.SourceFingerprint, r.MetadataHash, r.PayloadHash, r.Status, r.TargetId, r.TargetVersion })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(r => new KnownState(new DeliveryKey(r.DeliveryKey), r.SourceKey, r.SourceFingerprint, r.MetadataHash, r.PayloadHash, StatusText.ToRecordStatus(r.Status), r.TargetId, r.TargetVersion)).ToList();
    }

    public async Task<IReadOnlyList<SourceWatermark>> GetWatermarksAsync(Guid flowId, string scope, CancellationToken ct = default)
    {
        await using var db = Open();
        var rows = await db.DeliveryWatermarks.AsNoTracking().Where(w => w.FlowId == flowId && w.Scope == scope).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(w => new SourceWatermark(w.FlowId, w.Scope, w.TableName, w.Version, w.RecordedUtc)).ToList();
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

    public async Task CompleteActivityAsync(long activityId, string outcome, string? summary, string? log, DateTime completedUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        var entity = await db.DeliveryActivities.FirstOrDefaultAsync(a => a.ActivityId == activityId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Activity {activityId} is not in the ledger.");
        entity.Outcome = outcome;
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

    private static string? Truncate(string? text, int max)
        => text is null ? null : text.Length <= max ? text : text[..max];

    private static DeliveryAttempt ToEntity(AttemptRecord attempt) => new()
    {
        DeliveryKey = attempt.DeliveryKey.Value,
        SubmissionId = attempt.SubmissionId,
        RunId = attempt.RunId,
        Worker = attempt.Worker,
        StartedUtc = attempt.StartedUtc,
        CompletedUtc = attempt.CompletedUtc,
        Outcome = StatusText.Of(attempt.Outcome),
        Phase = attempt.Phase,
        MetadataHash = attempt.MetadataHash,
        PayloadHash = attempt.PayloadHash,
        TargetVersion = attempt.TargetVersion,
        Error = Truncate(attempt.Error, 2000),
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

    private static void Apply(DeliverySubmission entity, SubmissionState s)
    {
        entity.SubmissionId = s.SubmissionId;
        entity.FlowId = s.FlowId;
        entity.FlowName = s.FlowName;
        entity.MappingReference = s.MappingReference;
        entity.RenderContext = s.RenderContext;
        entity.DropLocation = s.DropLocation;
        entity.ParametersJson = s.ParametersJson;
        entity.RecordCount = s.RecordCount;
        entity.Status = StatusText.Of(s.Status);
        entity.ReceivedUtc = s.ReceivedUtc;
        entity.StartedUtc = s.StartedUtc;
        entity.CompletedUtc = s.CompletedUtc;
        entity.Planned = s.Planned;
        entity.SkippedUnchanged = s.SkippedUnchanged;
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
        ParametersJson = e.ParametersJson,
        RecordCount = e.RecordCount,
        Status = StatusText.ToSubmissionStatus(e.Status),
        ReceivedUtc = e.ReceivedUtc,
        StartedUtc = e.StartedUtc,
        CompletedUtc = e.CompletedUtc,
        Planned = e.Planned,
        SkippedUnchanged = e.SkippedUnchanged,
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
        MetadataHash = r.MetadataHash,
        PayloadHash = r.PayloadHash,
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
        PendingDocument = r.PendingDocument,
        PendingRenderContext = r.PendingRenderContext,
        PendingSourceFingerprint = r.PendingSourceFingerprint,
        PendingMetadataHash = r.PendingMetadataHash,
        PendingPayloadHash = r.PendingPayloadHash,
        PendingPayloadLocation = r.PendingPayloadLocation,
        PendingMetadata = r.PendingMetadata,
        PendingPayload = r.PendingPayload,
        Blocked = r.Blocked,
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
}
