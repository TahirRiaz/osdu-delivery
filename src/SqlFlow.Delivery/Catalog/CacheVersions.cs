using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.Delivery.Catalog;

/// <summary>How one cached record differs between two snapshot versions.</summary>
public enum CacheItemChange
{
    /// <summary>Both versions hold the record, and at least one captured value differs.</summary>
    Changed,

    /// <summary>Only the later version holds the record.</summary>
    Added,

    /// <summary>Only the earlier version holds the record.</summary>
    Removed,
}

/// <summary>One reference snapshot a cache read is scoped to.</summary>
public sealed record CacheSnapshotRef(Guid Id, Guid RepoId, string Version, DateTime? CapturedUtc, bool Current);

/// <summary>
/// A comparison of two cache versions. <see cref="ToVersion"/> null means each repository's current version. The type
/// and search filters narrow the counts and the items alike; the change filter narrows the items only, so the counts
/// keep describing every kind of change.
/// </summary>
public sealed record CacheComparisonQuery(string FromVersion)
{
    public string? ToVersion { get; init; }

    public Guid? RepoId { get; init; }

    public string? Type { get; init; }

    public CacheItemChange? Change { get; init; }

    public string? Search { get; init; }

    public int Skip { get; init; }

    /// <summary>How many differing records to return; 0 answers the counts alone.</summary>
    public int Take { get; init; } = 50;
}

/// <summary>One snapshot version of a repository's cache, and how many records the catalog carries for it.</summary>
public sealed record CacheVersionInfo(Guid RepoId, string Version, DateTime? CapturedUtc, bool Current, long Items)
{
    /// <summary>Whether the catalog still holds this version's records; an aged-out version keeps its row but not its items.</summary>
    public bool Carried => Items > 0;
}

/// <summary>How many cached records one version changed, added and removed.</summary>
public sealed record CacheChangeCounts(long Changed, long Added, long Removed)
{
    public long Total => Changed + Added + Removed;
}

/// <summary>
/// One version in the cache's history: the version of the same repository captured before it, and what it changed
/// against that version. <see cref="Changes"/> is null when there is nothing to compare: the first version, or a side
/// whose records the catalog no longer carries.
/// </summary>
public sealed record CacheHistoryEntry(CacheVersionInfo Version, CacheVersionInfo? Previous, CacheChangeCounts? Changes);

/// <summary>How many records of one cached type changed, arrived and left between the two versions.</summary>
public sealed record CacheComparisonTypeCount(string TypeName, long Changed, long Added, long Removed);

/// <summary>A repository in scope whose cache could not be compared, and why.</summary>
public sealed record CacheComparisonGap(Guid RepoId, string Reason);

/// <summary>
/// One cached record that differs: the captured values as JSON on each side (null on the side that does not hold it),
/// and, for a changed record, the captured names whose value moved.
/// </summary>
public sealed record CacheComparisonItem(
    Guid RepoId, string TypeName, string EntityType, string RecordId, CacheItemChange Change, string? BeforeJson, string? AfterJson,
    IReadOnlyList<string> ChangedFields);

/// <summary>What changed in the cache between two versions: counts per type, the gaps, and one page of the records that differ.</summary>
public sealed record CacheComparison(
    string FromVersion, string? ToVersion, IReadOnlyList<CacheComparisonTypeCount> Types, IReadOnlyList<CacheComparisonGap> Gaps, long Total,
    IReadOnlyList<CacheComparisonItem> Items)
{
    public long Changed => Types.Sum(t => t.Changed);

    public long Added => Types.Sum(t => t.Added);

    public long Removed => Types.Sum(t => t.Removed);
}

/// <summary>
/// Reads of the OSDU cache across the snapshot versions the catalog carries. A version label is minted from the capture
/// instant rather than owned by one repository, so naming one selects that version wherever it exists, and every read is
/// scoped to exactly one version per repository: the catalog carries several, and their items are otherwise
/// indistinguishable from one another.
/// </summary>
public static class CacheVersions
{
    /// <summary>The most snapshot rows one cache read resolves.</summary>
    public const int MaxVersions = 500;

    /// <summary>
    /// The snapshot versions of the cache, newest capture first, each with how many records the catalog carries for it.
    /// Older versions stay listed after their items are aged out; the snapshot files stay complete either way.
    /// </summary>
    public static async Task<IReadOnlyList<CacheVersionInfo>> ListAsync(CatalogDbContext db, Guid? repoId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var query = db.DeliverySnapshots.AsNoTracking().Where(s => s.Kind == "references");
        if (repoId is { } r)
        {
            query = query.Where(s => s.RepoId == r);
        }

        var snapshots = await query
            .OrderByDescending(s => s.CapturedUtc)
            .ThenByDescending(s => s.Version)
            .Take(MaxVersions)
            .Select(s => new { s.Id, s.RepoId, s.Version, s.CapturedUtc, s.Current })
            .ToListAsync(ct).ConfigureAwait(false);
        if (snapshots.Count == 0)
        {
            return [];
        }

        var ids = snapshots.Select(s => s.Id).ToList();
        var counts = (await db.DeliverySnapshotItems.AsNoTracking()
            .Where(i => ids.Contains(i.SnapshotId))
            .GroupBy(i => i.SnapshotId)
            .Select(g => new { SnapshotId = g.Key, Items = g.LongCount() })
            .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(c => c.SnapshotId, c => c.Items);
        return snapshots
            .Select(s => new CacheVersionInfo(s.RepoId, s.Version, s.CapturedUtc, s.Current, counts.GetValueOrDefault(s.Id)))
            .ToList();
    }

    /// <summary>
    /// The cache's version history, newest capture first: each version with the version of the same repository captured
    /// before it and, when the catalog carries both, how many records it changed, added and removed. With a
    /// <paramref name="type"/> the counts cover that cached type alone, which is what lets a reader find the versions
    /// that changed it.
    /// </summary>
    public static async Task<IReadOnlyList<CacheHistoryEntry>> HistoryAsync(CatalogDbContext db, Guid? repoId, string? type, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var versions = await ListAsync(db, repoId, ct).ConfigureAwait(false);
        var history = new List<CacheHistoryEntry>(versions.Count);
        for (var i = 0; i < versions.Count; i++)
        {
            var version = versions[i];
            var previous = versions.Skip(i + 1).FirstOrDefault(v => v.RepoId == version.RepoId);
            CacheChangeCounts? changes = null;
            if (version.Carried && previous is { Carried: true })
            {
                var diff = await CompareAsync(
                    db,
                    new CacheComparisonQuery(previous.Version) { ToVersion = version.Version, RepoId = version.RepoId, Type = type, Take = 0 },
                    ct).ConfigureAwait(false);
                if (diff is { Gaps.Count: 0 })
                {
                    changes = new CacheChangeCounts(diff.Changed, diff.Added, diff.Removed);
                }
            }

            history.Add(new CacheHistoryEntry(version, previous, changes));
        }

        return history;
    }

    /// <summary>
    /// The snapshot rows a cache read is scoped to: the named version of each repository in scope, or each repository's
    /// current version when none is named.
    /// </summary>
    public static async Task<IReadOnlyList<CacheSnapshotRef>> ResolveAsync(CatalogDbContext db, Guid? repoId, string? version, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var query = db.DeliverySnapshots.AsNoTracking().Where(s => s.Kind == "references");
        if (repoId is { } r)
        {
            query = query.Where(s => s.RepoId == r);
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            query = query.Where(s => s.Current);
        }
        else
        {
            var v = version.Trim();
            query = query.Where(s => s.Version == v);
        }

        return await query
            .Select(s => new CacheSnapshotRef(s.Id, s.RepoId, s.Version, s.CapturedUtc, s.Current))
            .Take(MaxVersions)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Compares two versions of the cache over the items the catalog carries, per repository: the records both hold whose
    /// captured values differ, the records only the later version holds, and the ones only the earlier version holds.
    /// Values compare as the exact text the sync wrote (ordered names, binary collation), so a change of case is a change.
    /// A repository lacking one of the versions, or whose records of one the catalog no longer carries, is a gap rather
    /// than a comparison against nothing, which would read as every record added or removed. Null when no repository in
    /// scope holds a named version at all.
    /// </summary>
    public static async Task<CacheComparison?> CompareAsync(CatalogDbContext db, CacheComparisonQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.FromVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Take);

        var fromVersion = query.FromVersion.Trim();
        var toVersion = string.IsNullOrWhiteSpace(query.ToVersion) ? null : query.ToVersion.Trim();
        var earlier = await ResolveAsync(db, query.RepoId, fromVersion, ct).ConfigureAwait(false);
        var later = await ResolveAsync(db, query.RepoId, toVersion, ct).ConfigureAwait(false);
        if (earlier.Count == 0 || (toVersion is not null && later.Count == 0))
        {
            return null;
        }

        var sides = earlier.Concat(later).Select(s => s.Id).Distinct().ToList();
        var carried = (await db.DeliverySnapshotItems.AsNoTracking()
            .Where(i => sides.Contains(i.SnapshotId))
            .Select(i => i.SnapshotId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();

        var (pairs, gaps) = Pair(earlier, later, fromVersion, toVersion, carried);
        if (pairs.Count == 0)
        {
            return new CacheComparison(fromVersion, toVersion, [], gaps, 0, []);
        }

        var fromIds = pairs.Select(p => p.From).ToList();
        var toIds = pairs.Select(p => p.To).ToList();
        var before = db.DeliverySnapshotItems.AsNoTracking().Where(i => fromIds.Contains(i.SnapshotId));
        var after = db.DeliverySnapshotItems.AsNoTracking().Where(i => toIds.Contains(i.SnapshotId));
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            var type = query.Type.Trim();
            before = before.Where(i => i.TypeName == type);
            after = after.Where(i => i.TypeName == type);
        }

        // Each repository contributes one snapshot to each side, so matching on the repository pairs a record with its
        // own counterpart and never with another repository's.
        var removed = before.Where(b => !after.Any(a => a.RepoId == b.RepoId && a.TypeName == b.TypeName && a.RecordId == b.RecordId));
        var added = after.Where(a => !before.Any(b => b.RepoId == a.RepoId && b.TypeName == a.TypeName && b.RecordId == a.RecordId));
        var joined = before.Join(
            after,
            b => new { b.RepoId, b.TypeName, b.RecordId },
            a => new { a.RepoId, a.TypeName, a.RecordId },
            (b, a) => new { Before = b, After = a });

        // SQL Server's default collation folds case, which would hide a corrected "Metre" to "metre"; the binary
        // collation the model keys OSDU ids with compares the text exactly. SQLite compares ordinally already.
        var changed = db.Database.IsSqlServer()
            ? joined.Where(x => EF.Functions.Collate(x.Before.FieldsJson, DeliveryModel.OsduIdCollation) != EF.Functions.Collate(x.After.FieldsJson, DeliveryModel.OsduIdCollation))
            : joined.Where(x => x.Before.FieldsJson != x.After.FieldsJson);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            removed = removed.Where(i => i.RecordId.Contains(term) || i.Terms.Contains(term));
            added = added.Where(i => i.RecordId.Contains(term) || i.Terms.Contains(term));
            changed = changed.Where(x => x.Before.RecordId.Contains(term) || x.Before.Terms.Contains(term) || x.After.Terms.Contains(term));
        }

        var changedByType = (await changed.GroupBy(x => x.Before.TypeName).Select(g => new { g.Key, Count = g.LongCount() })
            .ToListAsync(ct).ConfigureAwait(false)).ToDictionary(c => c.Key, c => c.Count, StringComparer.Ordinal);
        var addedByType = (await added.GroupBy(i => i.TypeName).Select(g => new { g.Key, Count = g.LongCount() })
            .ToListAsync(ct).ConfigureAwait(false)).ToDictionary(c => c.Key, c => c.Count, StringComparer.Ordinal);
        var removedByType = (await removed.GroupBy(i => i.TypeName).Select(g => new { g.Key, Count = g.LongCount() })
            .ToListAsync(ct).ConfigureAwait(false)).ToDictionary(c => c.Key, c => c.Count, StringComparer.Ordinal);
        var types = changedByType.Keys.Concat(addedByType.Keys).Concat(removedByType.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name => new CacheComparisonTypeCount(
                name, changedByType.GetValueOrDefault(name), addedByType.GetValueOrDefault(name), removedByType.GetValueOrDefault(name)))
            .ToList();

        // One page across the kinds of change in a fixed order (changed, added, removed), each by type and id, so a page
        // boundary falls in the same place however often it is asked for.
        var segments = new[]
        {
            (Change: CacheItemChange.Changed, Count: types.Sum(t => t.Changed)),
            (Change: CacheItemChange.Added, Count: types.Sum(t => t.Added)),
            (Change: CacheItemChange.Removed, Count: types.Sum(t => t.Removed)),
        }.Where(s => query.Change is null || s.Change == query.Change).ToList();
        var total = segments.Sum(s => s.Count);

        var items = new List<CacheComparisonItem>();
        long skip = query.Skip;
        foreach (var (kind, count) in segments)
        {
            var take = query.Take - items.Count;
            if (take <= 0)
            {
                break;
            }

            if (skip >= count)
            {
                skip -= count;
                continue;
            }

            var offset = (int)skip;
            skip = 0;
            if (kind == CacheItemChange.Changed)
            {
                var page = await changed
                    .OrderBy(x => x.Before.TypeName).ThenBy(x => x.Before.RecordId).ThenBy(x => x.Before.RepoId)
                    .Skip(offset).Take(take)
                    .Select(x => new { x.Before.RepoId, x.Before.TypeName, x.After.EntityType, x.Before.RecordId, BeforeJson = x.Before.FieldsJson, AfterJson = x.After.FieldsJson })
                    .ToListAsync(ct).ConfigureAwait(false);
                items.AddRange(page.Select(r => new CacheComparisonItem(
                    r.RepoId, r.TypeName, r.EntityType, r.RecordId, kind, r.BeforeJson, r.AfterJson, ChangedFields(r.BeforeJson, r.AfterJson))));
            }
            else
            {
                var side = kind == CacheItemChange.Added ? added : removed;
                var page = await side
                    .OrderBy(i => i.TypeName).ThenBy(i => i.RecordId).ThenBy(i => i.RepoId)
                    .Skip(offset).Take(take)
                    .Select(i => new { i.RepoId, i.TypeName, i.EntityType, i.RecordId, i.FieldsJson })
                    .ToListAsync(ct).ConfigureAwait(false);
                items.AddRange(page.Select(r => new CacheComparisonItem(
                    r.RepoId, r.TypeName, r.EntityType, r.RecordId, kind,
                    kind == CacheItemChange.Removed ? r.FieldsJson : null,
                    kind == CacheItemChange.Added ? r.FieldsJson : null,
                    [])));
            }
        }

        return new CacheComparison(fromVersion, toVersion, types, gaps, total, items);
    }

    /// <summary>Each repository's two sides, or the reason it cannot be compared.</summary>
    private static (List<(Guid From, Guid To)> Pairs, List<CacheComparisonGap> Gaps) Pair(
        IReadOnlyList<CacheSnapshotRef> earlier, IReadOnlyList<CacheSnapshotRef> later, string fromVersion, string? toVersion, HashSet<Guid> carried)
    {
        var from = ByRepo(earlier);
        var to = ByRepo(later);
        var pairs = new List<(Guid From, Guid To)>();
        var gaps = new List<CacheComparisonGap>();
        foreach (var repo in from.Keys.Union(to.Keys).Order())
        {
            if (!from.TryGetValue(repo, out var f))
            {
                gaps.Add(new CacheComparisonGap(repo, $"It holds no cache version {fromVersion}."));
                continue;
            }

            if (!to.TryGetValue(repo, out var t))
            {
                gaps.Add(new CacheComparisonGap(repo, toVersion is null ? "It has no current cache version." : $"It holds no cache version {toVersion}."));
                continue;
            }

            if (new[] { f, t }.FirstOrDefault(s => !carried.Contains(s.Id)) is { } aged)
            {
                gaps.Add(new CacheComparisonGap(
                    repo, $"The catalog no longer carries the records of cache version {aged.Version}; its snapshot files still hold them."));
                continue;
            }

            pairs.Add((f.Id, t.Id));
        }

        return (pairs, gaps);
    }

    /// <summary>One snapshot per repository: a label names one capture, so a repository holds it at most once.</summary>
    private static Dictionary<Guid, CacheSnapshotRef> ByRepo(IEnumerable<CacheSnapshotRef> snapshots)
        => snapshots
            .GroupBy(s => s.RepoId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.CapturedUtc).ThenByDescending(s => s.Version, StringComparer.Ordinal).First());

    /// <summary>
    /// The captured names whose value differs between the two sides, by exact JSON text. A side that is not a JSON object
    /// has no names to compare, so none are named; both sides are still returned whole for the reader to see.
    /// </summary>
    private static IReadOnlyList<string> ChangedFields(string before, string after)
    {
        var earlier = Values(before);
        var later = Values(after);
        if (earlier is null || later is null)
        {
            return [];
        }

        return earlier.Keys.Union(later.Keys, StringComparer.Ordinal)
            .Where(name => !earlier.TryGetValue(name, out var b) || !later.TryGetValue(name, out var a) || !string.Equals(b, a, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<string, string>? Values(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = property.Value.GetRawText();
            }

            return values;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
