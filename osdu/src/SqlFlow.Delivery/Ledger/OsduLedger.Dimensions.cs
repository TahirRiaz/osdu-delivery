using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The dimensions of dimension flows (docs/dimension-plan.md, Tables): a row per dimension of a flow in a partition, a row per
/// build, and the dimension's members, originals and change log. Every table is a ledger table, written under the ledger
/// identity the build registered, so a partition's dimensions are kept together and read in one range of it.
/// </summary>
public sealed partial class OsduLedger
{
    /// <summary>The most lines of notes a build keeps.</summary>
    private const int MaxDimensionNotes = 100;

    /// <summary>The most members or originals one page reads.</summary>
    public const int MaxDimensionPage = 1000;

    public async Task<(DimensionState Dimension, DimensionRunState Run)> StartDimensionRunAsync(
        DimensionDeclaration declaration, Guid? runId, string actor, DateTime startedUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var partition = await WritePartitionAsync(declaration.FlowId, ct).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            await using var db = Open();
            var dimension = await db.DeliveryDimensions
                .FirstOrDefaultAsync(d => d.PartitionId == partition && d.FlowId == declaration.FlowId && d.Name == declaration.Name, ct).ConfigureAwait(false);
            if (dimension is null)
            {
                dimension = new DeliveryDimension { PartitionId = partition, FlowId = declaration.FlowId, CreatedUtc = startedUtc };
                db.DeliveryDimensions.Add(dimension);
            }

            dimension.FlowName = Truncate(declaration.FlowName, DeliveryLedger.MaxFlowNameLength)!;
            dimension.Name = declaration.Name;
            dimension.Description = Truncate(declaration.Description, 4000);
            dimension.Kind = declaration.Kind;
            dimension.Query = declaration.Query;
            dimension.Path = declaration.Path;
            dimension.CleanJson = declaration.CleanJson;
            dimension.LabelJson = declaration.LabelJson;
            dimension.DefinitionHash = declaration.DefinitionHash;
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (attempt < 3 && IsDuplicate(ex))
            {
                // Two builds registered the same new dimension at once; the other's row is the one to build under.
                continue;
            }

            var run = new DeliveryDimensionRun
            {
                PartitionId = partition,
                DimensionId = dimension.DimensionId,
                FlowId = declaration.FlowId,
                RunId = runId,
                Actor = Truncate(actor, 200)!,
                Status = DimensionRunStatus.Running,
                DefinitionHash = declaration.DefinitionHash,
                Query = declaration.Query,
                StartedUtc = startedUtc,
            };
            db.DeliveryDimensionRuns.Add(run);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var name = await PartitionNameAsync(partition, ct).ConfigureAwait(false);
            return (ToState(dimension) with { Partition = name }, ToState(run));
        }
    }

    public async Task<DimensionRunState> WriteDimensionAsync(DimensionWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        foreach (var original in write.Originals)
        {
            if (original.Original.Length > DeliveryDimensionValue.MaxOriginalLength)
            {
                throw new DeliveryException(
                    string.Create(CultureInfo.InvariantCulture, $"A key of dimension {write.DimensionId} is {original.Original.Length} characters, more than the {DeliveryDimensionValue.MaxOriginalLength} a dimension keeps; the build leaves such values out before it writes."));
            }
        }

        var partition = await WritePartitionAsync(write.FlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        DeliveryDimensionRun? closed = null;
        await SqlServerDimensionStore.WriteAsync(db, partition, write, (written, run) =>
        {
            run.Status = DimensionRunStatus.Completed;
            run.AggregateBy = write.Field?.AggregateBy;
            Apply(run, write.Read);
            run.Members = written.Members;
            run.Originals = written.Originals;
            run.LeftOut = write.Originals.LongCount(o => o.CleanValue is null);
            run.Unfilterable = write.Originals.LongCount(o => !o.Filterable && o.CleanValue is not null);
            run.MembersAdded = written.Changes.MembersAdded;
            run.MembersRemoved = written.Changes.MembersRemoved;
            run.MembersRestored = written.Changes.MembersRestored;
            run.OriginalsAdded = written.Changes.OriginalsAdded;
            run.OriginalsRemoved = written.Changes.OriginalsRemoved;
            run.OriginalsMoved = written.Changes.OriginalsMoved;
            run.OriginalsRestored = written.Changes.OriginalsRestored;
            run.CompletedUtc = write.CompletedUtc;
            run.Error = null;
            closed = run;
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        return ToState(closed!);
    }

    public async Task CloseDimensionRunAsync(long dimensionRunId, string status, DimensionReadCounts read, string? failure, DateTime completedUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(read);
        if (status is not (DimensionRunStatus.Failed or DimensionRunStatus.Cancelled))
        {
            throw new ArgumentException($"A build is closed as {DimensionRunStatus.Failed} or {DimensionRunStatus.Cancelled} here; a completed one is closed by the write of its values.", nameof(status));
        }

        await using var db = Open();
        var run = await db.DeliveryDimensionRuns.FirstOrDefaultAsync(r => r.DimensionRunId == dimensionRunId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Dimension build {dimensionRunId} is not in the ledger."));
        run.Status = status;
        Apply(run, read);
        run.Error = Truncate(failure, 4000);
        run.CompletedUtc = completedUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DimensionState>> ListDimensionsAsync(string? partition, Guid? flowId, CancellationToken ct = default)
    {
        short? partitionId = null;
        if (flowId is { } flow)
        {
            if (await PartitionOfAsync(flow, ct).ConfigureAwait(false) is not { } owning)
            {
                return [];
            }

            partitionId = owning;
        }
        else if (!string.IsNullOrWhiteSpace(partition))
        {
            if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } numbered)
            {
                return [];
            }

            partitionId = numbered;
        }

        var rows = await ReadAsync(
            db =>
            {
                var query = db.DeliveryDimensions.AsNoTracking();
                if (partitionId is { } p)
                {
                    query = query.Where(d => d.PartitionId == p);
                }

                if (flowId is { } f)
                {
                    query = query.Where(d => d.FlowId == f);
                }

                return query.OrderBy(d => d.FlowName).ThenBy(d => d.Name).Take(10_000).ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
        var named = new List<DimensionState>(rows.Count);
        foreach (var row in rows)
        {
            named.Add(ToState(row) with { Partition = await PartitionNameAsync(row.PartitionId, ct).ConfigureAwait(false) });
        }

        return named;
    }

    public async Task<DimensionState?> GetDimensionAsync(int dimensionId, CancellationToken ct = default)
    {
        var row = await ReadAsync(db => db.DeliveryDimensions.AsNoTracking().FirstOrDefaultAsync(d => d.DimensionId == dimensionId, ct), ct).ConfigureAwait(false);
        return row is null ? null : ToState(row) with { Partition = await PartitionNameAsync(row.PartitionId, ct).ConfigureAwait(false) };
    }

    public async Task<DimensionState?> FindDimensionAsync(Guid flowId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var wanted = name.Trim();
        var row = await ReadAsync(
            db => db.DeliveryDimensions.AsNoTracking().FirstOrDefaultAsync(d => d.PartitionId == partition && d.FlowId == flowId && d.Name == wanted, ct),
            ct).ConfigureAwait(false);
        return row is null ? null : ToState(row) with { Partition = await PartitionNameAsync(row.PartitionId, ct).ConfigureAwait(false) };
    }

    public async Task<IReadOnlyList<DimensionRunState>> ListDimensionRunsAsync(int dimensionId, int max, CancellationToken ct = default)
    {
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(max, 1, 500);
        var rows = await ReadAsync(
            db => db.DeliveryDimensionRuns.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.DimensionId == dimensionId)
                .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.DimensionRunId)
                .Take(take)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    public async Task<IReadOnlyList<DimensionRunState>> LatestDimensionRunsAsync(IReadOnlyCollection<int> dimensionIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dimensionIds);
        var found = new List<DimensionRunState>();
        foreach (var chunk in dimensionIds.Distinct().Chunk(LookupChunk))
        {
            var keys = await ReadAsync(
                db => db.DeliveryDimensions.AsNoTracking().Where(d => chunk.Contains(d.DimensionId)).Select(d => new { d.PartitionId, d.DimensionId }).ToListAsync(ct),
                ct).ConfigureAwait(false);
            foreach (var kept in keys.GroupBy(k => k.PartitionId))
            {
                // The newest build of each dimension, read down its builds by start within the partition it is kept in.
                var partition = kept.Key;
                var ids = kept.Select(k => k.DimensionId).ToList();
                var rows = await ReadAsync(
                    db => db.DeliveryDimensionRuns.AsNoTracking()
                        .Where(r => r.PartitionId == partition && ids.Contains(r.DimensionId))
                        .GroupBy(r => r.DimensionId)
                        .Select(g => g.OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.DimensionRunId).First())
                        .ToListAsync(ct),
                    ct).ConfigureAwait(false);
                found.AddRange(rows.Select(ToState));
            }
        }

        return found;
    }

    public async Task<IReadOnlyList<DimensionRunState>> GetDimensionRunsAsync(IReadOnlyCollection<long> dimensionRunIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dimensionRunIds);
        var found = new List<DimensionRunState>();
        foreach (var chunk in dimensionRunIds.Distinct().Chunk(LookupChunk))
        {
            var rows = await ReadAsync(
                db => db.DeliveryDimensionRuns.AsNoTracking().Where(r => chunk.Contains(r.DimensionRunId)).ToListAsync(ct),
                ct).ConfigureAwait(false);
            found.AddRange(rows.Select(ToState));
        }

        return found;
    }

    public async Task<IReadOnlyList<DimensionRunState>> DimensionRunsOfAsync(Guid runId, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db => db.DeliveryDimensionRuns.AsNoTracking().Where(r => r.RunId == runId).OrderBy(r => r.DimensionRunId).Take(DimensionFlowLimit).ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    /// <summary>The most builds one platform run makes: a flow's dimensions.</summary>
    private const int DimensionFlowLimit = 1000;

    public async Task<IReadOnlyList<DimensionMemberState>> ListDimensionMembersAsync(int dimensionId, DimensionMemberQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(query.Limit, 1, MaxDimensionPage);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        return await ReadAsync(
            db =>
            {
                var members = db.DeliveryDimensionMembers.AsNoTracking().Where(m => m.PartitionId == partition && m.DimensionId == dimensionId);
                if (!query.IncludeRemoved)
                {
                    members = members.Where(m => m.RemovedRunId == null);
                }

                if (search is not null)
                {
                    // A member is found by its clean value or by any original under it, ignoring case: people search by the
                    // spelling they know. The columns compare exactly, so the search asks them in a collation that folds case.
                    // An original no build finds any more finds its member only when removed ones are asked for as well.
                    var removed = query.IncludeRemoved;
                    members = members.Where(m => EF.Functions.Collate(m.Value, DeliveryModel.SearchCollation).Contains(search)
                        || db.DeliveryDimensionValues.Any(v => v.PartitionId == partition && v.DimensionId == dimensionId && v.MemberId == m.MemberId
                            && (removed || v.RemovedRunId == null)
                            && EF.Functions.Collate(v.Original, DeliveryModel.SearchCollation).Contains(search)));
                }

                // Each order pages by the columns it sorts on, so a page is one seek however deep it is; a member whose count
                // moves between two pages is read where it stands when its page is read.
                IOrderedQueryable<DeliveryDimensionMember> ordered;
                if (query.Order == DimensionMemberOrder.Records)
                {
                    if (query.After is { } after)
                    {
                        members = members.Where(m => m.Records < after.Records || (m.Records == after.Records && m.Value.CompareTo(after.Value) > 0));
                    }

                    ordered = members.OrderByDescending(m => m.Records).ThenBy(m => m.Value);
                }
                else
                {
                    if (query.After is { } after)
                    {
                        members = members.Where(m => m.Value.CompareTo(after.Value) > 0);
                    }

                    ordered = members.OrderBy(m => m.Value);
                }

                return ordered.Take(take).Select(m => new DimensionMemberState
                {
                    MemberId = m.MemberId,
                    DimensionId = m.DimensionId,
                    Value = m.Value,
                    Records = m.Records,
                    RecordsExact = m.RecordsExact,
                    Originals = m.Originals,
                    Unfilterable = m.Unfilterable,
                    Filter = m.Filter,
                    FilterParts = m.FilterParts,
                    FirstSeenRunId = m.FirstSeenRunId,
                    FirstSeenUtc = m.FirstSeenUtc,
                    RemovedRunId = m.RemovedRunId,
                    RemovedUtc = m.RemovedUtc,
                }).ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DimensionMemberState>> GetDimensionMembersAsync(
        int dimensionId, IReadOnlyCollection<long> memberIds, IReadOnlyCollection<string> values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        ArgumentNullException.ThrowIfNull(values);
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var found = new List<DimensionMemberState>();
        foreach (var chunk in memberIds.Distinct().Chunk(LookupChunk))
        {
            found.AddRange(await MembersAsync(partition, dimensionId, m => chunk.Contains(m.MemberId), ct).ConfigureAwait(false));
        }

        foreach (var chunk in values.Distinct(StringComparer.Ordinal).Chunk(LookupChunk))
        {
            found.AddRange(await MembersAsync(partition, dimensionId, m => chunk.Contains(m.Value), ct).ConfigureAwait(false));
        }

        return found.GroupBy(m => m.MemberId).Select(g => g.First()).OrderBy(m => m.Value, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<DimensionValueState>> ListDimensionValuesAsync(int dimensionId, DimensionValueQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(query.Limit, 1, MaxDimensionPage);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        return await ReadAsync(
            db =>
            {
                var values = db.DeliveryDimensionValues.AsNoTracking().Where(v => v.PartitionId == partition && v.DimensionId == dimensionId);
                if (!query.IncludeRemoved)
                {
                    values = values.Where(v => v.RemovedRunId == null);
                }

                if (query.MemberId is { } member)
                {
                    values = values.Where(v => v.MemberId == member);
                }

                if (query.LeftOutOnly)
                {
                    values = values.Where(v => v.MemberId == null);
                }

                if (search is not null)
                {
                    // A key is found by itself or by the label read for it, ignoring case: an id is searched by the name it
                    // stands for as often as by the id.
                    values = values.Where(v => EF.Functions.Collate(v.Original, DeliveryModel.SearchCollation).Contains(search)
                        || (v.Label != null && EF.Functions.Collate(v.Label, DeliveryModel.SearchCollation).Contains(search)));
                }

                IOrderedQueryable<DeliveryDimensionValue> ordered;
                if (query.Order == DimensionValueOrder.Count)
                {
                    if (query.After is { } after)
                    {
                        values = values.Where(v => v.Count < after.Count || (v.Count == after.Count && v.ValueId > after.ValueId));
                    }

                    ordered = values.OrderByDescending(v => v.Count).ThenBy(v => v.ValueId);
                }
                else
                {
                    if (query.After is { } after)
                    {
                        values = values.Where(v => v.ValueId > after.ValueId);
                    }

                    ordered = values.OrderBy(v => v.ValueId);
                }

                return ordered.Take(take)
                    .Select(v => new DimensionValueState
                    {
                        ValueId = v.ValueId,
                        DimensionId = v.DimensionId,
                        Original = v.Original,
                        MemberId = v.MemberId,
                        MemberValue = db.DeliveryDimensionMembers
                            .Where(m => m.PartitionId == v.PartitionId && m.MemberId == v.MemberId).Select(m => m.Value).FirstOrDefault(),
                        LeftOut = v.LeftOut,
                        Note = v.Note,
                        Label = v.Label,
                        LabelFrom = v.LabelFrom,
                        Filter = v.Filter,
                        Count = v.Count,
                        Filterable = v.Filterable,
                        FirstSeenRunId = v.FirstSeenRunId,
                        FirstSeenUtc = v.FirstSeenUtc,
                        MemberSinceRunId = v.MemberSinceRunId,
                        RemovedRunId = v.RemovedRunId,
                        RemovedUtc = v.RemovedUtc,
                    })
                    .ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DimensionValueState>> MemberOriginalsAsync(int dimensionId, IReadOnlyCollection<long> memberIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        if (memberIds.Count == 0 || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var found = new List<DimensionValueState>();
        foreach (var chunk in memberIds.Distinct().Chunk(LookupChunk))
        {
            var rows = await ReadAsync(
                db => db.DeliveryDimensionValues.AsNoTracking()
                    .Where(v => v.PartitionId == partition && v.DimensionId == dimensionId && v.RemovedRunId == null && v.MemberId != null && chunk.Contains(v.MemberId.Value))
                    .Join(db.DeliveryDimensionMembers.Where(m => m.PartitionId == partition), v => v.MemberId, m => m.MemberId, (v, m) => new { v, m.Value })
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            found.AddRange(rows.Select(r => ToState(r.v) with { MemberValue = r.Value }));
        }

        return found.OrderBy(v => v.MemberValue, StringComparer.Ordinal).ThenBy(v => v.Original, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<DimensionValueState>> TopMemberOriginalsAsync(
        int dimensionId, IReadOnlyCollection<long> memberIds, int perMember, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        if (memberIds.Count == 0 || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(perMember, 1, MaxTopOriginals);
        var found = new List<DimensionValueState>();
        foreach (var chunk in memberIds.Distinct().Chunk(LookupChunk))
        {
            // One read for the page: each member's most common originals, taken beside it, the member's own range of the index.
            var rows = await ReadAsync(
                db => db.DeliveryDimensionMembers.AsNoTracking()
                    .Where(m => m.PartitionId == partition && m.DimensionId == dimensionId && chunk.Contains(m.MemberId))
                    .SelectMany(m => db.DeliveryDimensionValues
                        .Where(v => v.PartitionId == partition && v.DimensionId == dimensionId && v.MemberId == m.MemberId && v.RemovedRunId == null)
                        .OrderByDescending(v => v.Count).ThenBy(v => v.ValueId)
                        .Take(take)
                        .Select(v => new { v, m.Value }))
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            found.AddRange(rows.Select(r => ToState(r.v) with { MemberValue = r.Value }));
        }

        return found;
    }

    /// <summary>The most originals of one member <see cref="TopMemberOriginalsAsync"/> reads.</summary>
    public const int MaxTopOriginals = 50;

    public async Task<IReadOnlyList<DimensionChangeState>> ListDimensionChangesAsync(int dimensionId, DimensionChangeQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(query.Limit, 1, MaxDimensionPage);
        var rows = await ReadAsync(
            db =>
            {
                var changes = db.DeliveryDimensionChanges.AsNoTracking().Where(c => c.PartitionId == partition && c.DimensionId == dimensionId);
                if (query.DimensionRunId is { } run)
                {
                    changes = changes.Where(c => c.DimensionRunId == run);
                }

                if (query.ValueId is { } value)
                {
                    changes = changes.Where(c => c.ValueId == value);
                }

                if (query.MemberId is { } member)
                {
                    changes = changes.Where(c => c.FromMemberId == member || c.ToMemberId == member);
                }

                if (!string.IsNullOrWhiteSpace(query.Change))
                {
                    var kind = query.Change.Trim();
                    changes = changes.Where(c => c.Change == kind);
                }

                if (query.Before is { } cursor)
                {
                    changes = changes.Where(c => c.ChangeId < cursor);
                }

                return changes.OrderByDescending(c => c.ChangeId).Take(take)
                    .Select(c => new
                    {
                        c.ChangeId,
                        c.DimensionRunId,
                        c.ValueId,
                        Original = db.DeliveryDimensionValues.Where(v => v.PartitionId == c.PartitionId && v.ValueId == c.ValueId).Select(v => v.Original).FirstOrDefault(),
                        c.Change,
                        c.FromMemberId,
                        FromValue = db.DeliveryDimensionMembers.Where(m => m.PartitionId == c.PartitionId && m.MemberId == c.FromMemberId).Select(m => m.Value).FirstOrDefault(),
                        c.ToMemberId,
                        ToValue = db.DeliveryDimensionMembers.Where(m => m.PartitionId == c.PartitionId && m.MemberId == c.ToMemberId).Select(m => m.Value).FirstOrDefault(),
                        c.ChangedUtc,
                    })
                    .ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
        return rows.Select(r => new DimensionChangeState(
            r.ChangeId, r.DimensionRunId, r.ValueId, r.Original ?? string.Empty, r.Change, r.FromMemberId, r.FromValue, r.ToMemberId, r.ToValue,
            DateTime.SpecifyKind(r.ChangedUtc, DateTimeKind.Utc))).ToList();
    }

    /// <summary>The partition number dimension <paramref name="dimensionId"/>'s rows are kept under, or null when there is no such dimension.</summary>
    private async Task<short?> DimensionPartitionAsync(int dimensionId, CancellationToken ct)
        => await ReadAsync(
            db => db.DeliveryDimensions.AsNoTracking().Where(d => d.DimensionId == dimensionId).Select(d => (short?)d.PartitionId).FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);

    private Task<List<DimensionMemberState>> MembersAsync(
        short partition, int dimensionId, System.Linq.Expressions.Expression<Func<DeliveryDimensionMember, bool>> which, CancellationToken ct)
        => ReadAsync(
            db => db.DeliveryDimensionMembers.AsNoTracking()
                .Where(m => m.PartitionId == partition && m.DimensionId == dimensionId)
                .Where(which)
                .Select(m => new DimensionMemberState
                {
                    MemberId = m.MemberId,
                    DimensionId = m.DimensionId,
                    Value = m.Value,
                    Records = m.Records,
                    RecordsExact = m.RecordsExact,
                    Originals = m.Originals,
                    Unfilterable = m.Unfilterable,
                    Filter = m.Filter,
                    FilterParts = m.FilterParts,
                    FirstSeenRunId = m.FirstSeenRunId,
                    FirstSeenUtc = m.FirstSeenUtc,
                    RemovedRunId = m.RemovedRunId,
                    RemovedUtc = m.RemovedUtc,
                })
                .ToListAsync(ct),
            ct);

    private static void Apply(DeliveryDimensionRun run, DimensionReadCounts read)
    {
        run.Records = read.Records;
        run.WithValue = read.WithValue;
        run.Nulls = read.Nulls;
        run.TooLong = read.TooLong;
        run.Unreadable = read.Unreadable;
        run.Aggregations = read.Aggregations;
        run.Slices = read.Slices;
        run.Splits = read.Splits;
        run.ScannedSlices = read.ScannedSlices;
        run.ScanPages = read.ScanPages;
        run.ScannedUnits = read.ScannedUnits;
        run.CountQueries = read.CountQueries;
        run.Labelled = read.Labelled;
        run.Unlabelled = read.Unlabelled;
        run.LabelQueries = read.LabelQueries;
        run.Templates = read.Templates;
        run.Notes = read.Notes.Count == 0 ? null : JsonSerializer.Serialize(read.Notes.Take(MaxDimensionNotes).Select(n => Truncate(n, 2000)).ToList());
    }

    private static DimensionState ToState(DeliveryDimension d) => new()
    {
        DimensionId = d.DimensionId,
        FlowId = d.FlowId,
        FlowName = d.FlowName,
        Name = d.Name,
        Description = d.Description,
        Kind = d.Kind,
        Query = d.Query,
        Path = d.Path,
        Field = d.FieldIndex is { } index && d.AggregateBy is { } aggregateBy ? new DimensionFieldState(index, d.NestedPath, aggregateBy, d.Repeats) : null,
        CleanJson = d.CleanJson,
        LabelJson = d.LabelJson,
        DefinitionHash = d.DefinitionHash,
        Members = d.Members,
        Originals = d.Originals,
        LastRunId = d.LastRunId,
        LastBuiltUtc = d.LastBuiltUtc is { } built ? DateTime.SpecifyKind(built, DateTimeKind.Utc) : null,
        CreatedUtc = DateTime.SpecifyKind(d.CreatedUtc, DateTimeKind.Utc),
    };

    private static DimensionRunState ToState(DeliveryDimensionRun r) => new()
    {
        DimensionRunId = r.DimensionRunId,
        DimensionId = r.DimensionId,
        FlowId = r.FlowId,
        RunId = r.RunId,
        Actor = r.Actor,
        Status = r.Status,
        DefinitionHash = r.DefinitionHash,
        Query = r.Query,
        AggregateBy = r.AggregateBy,
        Read = new DimensionReadCounts
        {
            Records = r.Records,
            WithValue = r.WithValue,
            Nulls = r.Nulls,
            TooLong = r.TooLong,
            Unreadable = r.Unreadable,
            Aggregations = r.Aggregations,
            Slices = r.Slices,
            Splits = r.Splits,
            ScannedSlices = r.ScannedSlices,
            ScanPages = r.ScanPages,
            ScannedUnits = r.ScannedUnits,
            CountQueries = r.CountQueries,
            Labelled = r.Labelled,
            Unlabelled = r.Unlabelled,
            LabelQueries = r.LabelQueries,
            Templates = r.Templates,
            Notes = r.Notes is null ? [] : JsonSerializer.Deserialize<List<string>>(r.Notes) ?? [],
        },
        Members = r.Members,
        Originals = r.Originals,
        LeftOut = r.LeftOut,
        Unfilterable = r.Unfilterable,
        Changes = new DimensionChangeCounts(r.MembersAdded, r.MembersRemoved, r.MembersRestored, r.OriginalsAdded, r.OriginalsRemoved, r.OriginalsMoved, r.OriginalsRestored),
        StartedUtc = DateTime.SpecifyKind(r.StartedUtc, DateTimeKind.Utc),
        CompletedUtc = r.CompletedUtc is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
        Error = r.Error,
    };

    private static DimensionValueState ToState(DeliveryDimensionValue v) => new()
    {
        ValueId = v.ValueId,
        DimensionId = v.DimensionId,
        Original = v.Original,
        MemberId = v.MemberId,
        LeftOut = v.LeftOut,
        Note = v.Note,
        Label = v.Label,
        LabelFrom = v.LabelFrom,
        Filter = v.Filter,
        Count = v.Count,
        Filterable = v.Filterable,
        FirstSeenRunId = v.FirstSeenRunId,
        FirstSeenUtc = DateTime.SpecifyKind(v.FirstSeenUtc, DateTimeKind.Utc),
        MemberSinceRunId = v.MemberSinceRunId,
        RemovedRunId = v.RemovedRunId,
        RemovedUtc = v.RemovedUtc is { } removed ? DateTime.SpecifyKind(removed, DateTimeKind.Utc) : null,
    };
}
