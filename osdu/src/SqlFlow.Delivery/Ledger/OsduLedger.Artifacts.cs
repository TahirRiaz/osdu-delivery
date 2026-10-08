using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

// What deliveries created in OSDU, or set out to create (docs/atomic-delivery-plan.md): artifacts written with the steps that
// created them, settled with the completions and undos of their units, and read by the worker, the sweep and a record's page.
public sealed partial class OsduLedger
{
    /// <summary>The open states, as the ledger writes them: what the open artifacts' index filters on.</summary>
    private static readonly string[] OpenStates =
    [
        ArtifactStatuses.Name(ArtifactStatus.Intent),
        ArtifactStatuses.Name(ArtifactStatus.Pending),
        ArtifactStatuses.Name(ArtifactStatus.Due),
        ArtifactStatuses.Name(ArtifactStatus.Failed),
    ];

    /// <summary>
    /// What one append writes of artifacts: what its steps reported, how its completions ended their units, and what its undos
    /// settled; null when it writes none.
    /// </summary>
    private static ArtifactWrites? ArtifactWritesOf(short partition, Guid flowId, LeaseAppend append, DateTime nowUtc)
    {
        var upserts = new List<ArtifactUpsert>();
        foreach (var step in append.Steps)
        {
            if (step.Artifacts.Count == 0)
            {
                continue;
            }

            var unit = step.Unit ?? throw new ArgumentException(
                $"The step of record {step.DeliveryKey} reports artifacts outside a unit of work, so nothing could tie them to the delivery that made them.",
                nameof(append));
            foreach (var artifact in step.Artifacts)
            {
                artifact.Validate();
                upserts.Add(new ArtifactUpsert(step.DeliveryKey.Value, unit, artifact, step.SubmissionId, step.RunId, step.AtUtc));
            }
        }

        var ends = append.Completions
            .Where(c => c.UnitId is not null && c.Status is RecordStatus.Delivered or RecordStatus.Held or RecordStatus.Failed)
            .Select(c => new UnitEnd(c.DeliveryKey.Value, c.UnitId!.Value, c.Status == RecordStatus.Delivered, c.Superseded))
            .ToList();
        var settlements = append.Undos.SelectMany(SettlementsOf).ToList();
        var writes = new ArtifactWrites(partition, flowId, upserts, ends, settlements, nowUtc) { Moves = MovesOf(append.Undos) };
        return writes.IsEmpty ? null : writes;
    }

    /// <summary>The record versions <paramref name="undos"/> moved, each with its record.</summary>
    private static IReadOnlyList<(Guid DeliveryKey, RecordVersionMove Move)> MovesOf(IEnumerable<RecordUndo> undos)
        => undos.Where(u => u.Moved is not null).Select(u => (u.DeliveryKey.Value, u.Moved!)).ToList();

    /// <summary>The live and superseded states of a minted id, and the roles deleting the ledger removes.</summary>
    private static readonly string[] MintedStates = [ArtifactStatuses.Name(ArtifactStatus.Live), ArtifactStatuses.Name(ArtifactStatus.Superseded)];

    private static readonly string[] RetiredRoles = [ArtifactRoles.Dataset, ArtifactRoles.Content, ArtifactRoles.Output];

    private static IEnumerable<ArtifactSettle> SettlementsOf(RecordUndo undo) => SettlementsOf(undo, minted: false);

    private static IEnumerable<ArtifactSettle> SettlementsOf(RecordUndo undo, bool minted)
        => undo.Settlements.Select(s =>
        {
            if (!ArtifactStatuses.IsUndoOutcome(s.Status))
            {
                throw new ArgumentException(
                    $"The undo of record {undo.DeliveryKey} settles artifact {s.ArtifactId} as {ArtifactStatuses.Name(s.Status)}, which is not an outcome an undo can have.",
                    nameof(undo));
            }

            return new ArtifactSettle(s.ArtifactId, ArtifactStatuses.Name(s.Status), s.Note, s.Status == ArtifactStatus.Failed ? s.RetryAtUtc : null, undo.RunId, undo.SettledBy, minted);
        });

    public async Task<IReadOnlyList<LedgerArtifact>> OpenArtifactsAsync(Guid flowId, IReadOnlyCollection<DeliveryKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0 || await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var found = new List<LedgerArtifact>();
        foreach (var chunk in keys.Select(k => k.Value).Distinct().Chunk(LookupChunk))
        {
            var wanted = chunk.ToList();
            var rows = await ReadAsync(
                db => db.DeliveryArtifacts
                    .Where(a => a.PartitionId == partition && a.FlowId == flowId && wanted.Contains(a.DeliveryKey) && OpenStates.Contains(a.State))
                    .OrderBy(a => a.ArtifactId)
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            found.AddRange(rows.Select(ToLedgerArtifact));
        }

        return found.OrderBy(a => a.ArtifactId).ToList();
    }

    public async Task<IReadOnlyList<LedgerArtifact>> RecordArtifactsAsync(Guid flowId, DeliveryKey key, int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var rows = await ReadAsync(
            db => db.DeliveryArtifacts
                .Where(a => a.PartitionId == partition && a.FlowId == flowId && a.DeliveryKey == key.Value)
                .OrderByDescending(a => a.ArtifactId)
                .Take(max)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToLedgerArtifact).ToList();
    }

    public async Task<ArtifactSweepPage> SweepArtifactsAsync(Guid flowId, DateTime nowUtc, DeliveryKey? after, int maxRecords, bool exhausted, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecords, 1);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return ArtifactSweepPage.Empty;
        }

        IReadOnlyList<long> ids;
        Guid? last;
        await using (var db = Open())
        {
            (ids, last) = await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.SweepArtifactIdsAsync(db, partition, flowId, nowUtc, after?.Value, maxRecords, exhausted, ct), ct).ConfigureAwait(false);
        }

        if (ids.Count == 0)
        {
            return ArtifactSweepPage.Empty;
        }

        var found = new List<LedgerArtifact>(ids.Count);
        foreach (var chunk in ids.Chunk(LookupChunk))
        {
            var wanted = chunk.ToList();
            var rows = await ReadAsync(
                db => db.DeliveryArtifacts.Where(a => a.PartitionId == partition && wanted.Contains(a.ArtifactId)).ToListAsync(ct),
                ct).ConfigureAwait(false);
            found.AddRange(rows.Select(ToLedgerArtifact));
        }

        return new ArtifactSweepPage(found.OrderBy(a => a.ArtifactId).ToList(), last is { } key ? new DeliveryKey(key) : null);
    }

    public async Task<IReadOnlyList<LedgerArtifact>> MintedArtifactsAsync(Guid flowId, IReadOnlyCollection<DeliveryKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0 || await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var found = new List<LedgerArtifact>();
        foreach (var chunk in keys.Select(k => k.Value).Distinct().Chunk(LookupChunk))
        {
            var wanted = chunk.ToList();
            var rows = await ReadAsync(
                db => db.DeliveryArtifacts
                    .Where(a => a.PartitionId == partition && a.FlowId == flowId && wanted.Contains(a.DeliveryKey)
                        && MintedStates.Contains(a.State) && RetiredRoles.Contains(a.Role) && a.TargetId != null)
                    .OrderBy(a => a.ArtifactId)
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            found.AddRange(rows.Select(ToLedgerArtifact));
        }

        return found.OrderBy(a => a.ArtifactId).ToList();
    }

    public async Task SettleArtifactsAsync(Guid flowId, IReadOnlyList<RecordUndo> undos, bool minted = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(undos);
        if (undos.Count == 0)
        {
            return;
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        foreach (var undo in undos)
        {
            if (undo.Attempt.DeliveryKey != undo.DeliveryKey)
            {
                throw new ArgumentException(
                    $"The undo of record {undo.DeliveryKey} carries an attempt of record {undo.Attempt.DeliveryKey}; an attempt belongs to the record it undoes.",
                    nameof(undos));
            }
        }

        var attempts = undos.Select(u => ToEntity(partition, flowId, u.Attempt)).ToList();
        var writes = new ArtifactWrites(partition, flowId, [], [], undos.SelectMany(u => SettlementsOf(u, minted)).ToList(), Now) { Moves = MovesOf(undos) };
        await using var db = Open();
        await RetryDeadlockAsync(() => SqlServerLedgerBulk.SettleArtifactsAsync(db, attempts, writes, ct), ct).ConfigureAwait(false);
    }

    public async Task<ArtifactCounts> ArtifactCountsAsync(Guid flowId, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return ArtifactCounts.None;
        }

        var failed = ArtifactStatuses.Name(ArtifactStatus.Failed);
        var rows = await ReadAsync(
            db => db.DeliveryArtifacts
                .Where(a => a.PartitionId == partition && a.FlowId == flowId && OpenStates.Contains(a.State))
                .GroupBy(a => new { a.State, Exhausted = a.State == failed && a.UndoAttempts >= ArtifactLimits.MaxUndoAttempts })
                .Select(g => new { g.Key.State, g.Key.Exhausted, Count = g.LongCount() })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        long Count(ArtifactStatus status) => rows.Where(r => r.State == ArtifactStatuses.Name(status)).Sum(r => r.Count);
        return new ArtifactCounts(
            Count(ArtifactStatus.Intent),
            Count(ArtifactStatus.Pending),
            Count(ArtifactStatus.Due),
            Count(ArtifactStatus.Failed),
            rows.Where(r => r.Exhausted).Sum(r => r.Count));
    }

    public async Task<OpenArtifactRecordPage> OpenArtifactRecordsAsync(Guid flowId, int offset, int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return OpenArtifactRecordPage.Empty;
        }

        var intent = ArtifactStatuses.Name(ArtifactStatus.Intent);
        var pending = ArtifactStatuses.Name(ArtifactStatus.Pending);
        var due = ArtifactStatuses.Name(ArtifactStatus.Due);
        var failed = ArtifactStatuses.Name(ArtifactStatus.Failed);
        var (rows, total) = await ReadAsync(
            async db =>
            {
                // The open states are written into the statement as literals (a static array is), which is what lets the
                // optimizer match the filtered open index.
                var open = db.DeliveryArtifacts
                    .Where(a => a.PartitionId == partition && a.FlowId == flowId && OpenStates.Contains(a.State));
                var page = await open
                    .GroupBy(a => a.DeliveryKey)
                    .Select(g => new
                    {
                        Key = g.Key,
                        Intent = g.LongCount(a => a.State == intent),
                        Pending = g.LongCount(a => a.State == pending),
                        Due = g.LongCount(a => a.State == due),
                        Failed = g.LongCount(a => a.State == failed),
                        Exhausted = g.LongCount(a => a.State == failed && a.UndoAttempts >= ArtifactLimits.MaxUndoAttempts),
                        Oldest = g.Min(a => a.CreatedUtc),
                        Next = g.Min(a => a.NextUndoUtc),
                    })
                    .OrderByDescending(r => r.Exhausted)
                    .ThenByDescending(r => r.Failed)
                    .ThenByDescending(r => r.Due)
                    .ThenBy(r => r.Oldest)
                    .ThenBy(r => r.Key)
                    .Skip(offset)
                    .Take(max)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                var count = page.Count < max && offset == 0
                    ? page.Count
                    : await open.Select(a => a.DeliveryKey).Distinct().LongCountAsync(ct).ConfigureAwait(false);
                return (page, count);
            },
            ct).ConfigureAwait(false);
        return new OpenArtifactRecordPage(
            rows.Select(r => new OpenArtifactRecord(
                new DeliveryKey(r.Key), r.Intent, r.Pending, r.Due, r.Failed, r.Exhausted,
                DateTime.SpecifyKind(r.Oldest, DateTimeKind.Utc),
                r.Next is { } next ? DateTime.SpecifyKind(next, DateTimeKind.Utc) : null)).ToList(),
            total);
    }

    private static LedgerArtifact ToLedgerArtifact(DeliveryArtifact a) => new()
    {
        ArtifactId = a.ArtifactId,
        Key = new DeliveryKey(a.DeliveryKey),
        UnitId = a.UnitId,
        UnitStartedUtc = DateTime.SpecifyKind(a.UnitStartedUtc, DateTimeKind.Utc),
        Slot = a.Slot,
        Role = a.Role,
        TargetId = a.TargetId,
        Locator = a.Locator,
        Version = a.Version,
        PriorVersion = a.PriorVersion,
        Status = ArtifactStatuses.Parse(a.State),
        Note = a.Note,
        UndoAttempts = a.UndoAttempts,
        NextUndoUtc = a.NextUndoUtc is { } next ? DateTime.SpecifyKind(next, DateTimeKind.Utc) : null,
        SubmissionId = a.SubmissionId,
        CreatedRunId = a.CreatedRunId,
        CreatedUtc = DateTime.SpecifyKind(a.CreatedUtc, DateTimeKind.Utc),
        UpdatedUtc = DateTime.SpecifyKind(a.UpdatedUtc, DateTimeKind.Utc),
        SettledUtc = a.SettledUtc is { } settled ? DateTime.SpecifyKind(settled, DateTimeKind.Utc) : null,
        SettledRunId = a.SettledRunId,
        SettledBy = a.SettledBy,
    };
}
