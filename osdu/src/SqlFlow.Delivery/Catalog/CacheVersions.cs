using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Catalog;

/// <summary>How one cached record differs between two versions of its cache.</summary>
public enum CacheItemChange
{
    /// <summary>Both versions hold the record, and at least one captured value differs.</summary>
    Changed,

    /// <summary>Only the later version holds the record.</summary>
    Added,

    /// <summary>Only the earlier version holds the record.</summary>
    Removed,
}

/// <summary>
/// A comparison of two versions of one cache. <see cref="ToVersion"/> null means the cache's current version. The type and
/// search filters narrow the counts and the items alike; the change filter narrows the items only, so the counts keep
/// describing every kind of change.
/// </summary>
public sealed record CacheComparisonQuery(string Scope, string FromVersion)
{
    public string? ToVersion { get; init; }

    public string? Type { get; init; }

    public CacheItemChange? Change { get; init; }

    public string? Search { get; init; }

    public int Skip { get; init; }

    /// <summary>How many differing records to return; 0 answers the counts alone.</summary>
    public int Take { get; init; } = 50;
}

/// <summary>How many cached records one version changed, added and removed.</summary>
public sealed record CacheChangeCounts(long Changed, long Added, long Removed)
{
    public long Total => Changed + Added + Removed;
}

/// <summary>
/// One version in a cache's history: the version captured before it, what it changed against that version, and which types
/// it added, changed or removed. The counts and the types are those of the type in scope when one was named. A version is
/// written when anything in the partition's cache moved, so <see cref="Types"/> is what says which types moved with it: one
/// that only rode along is not listed.
/// </summary>
public sealed record CacheHistoryEntry(CacheVersionInfo Version, string? Before, CacheChangeCounts Changes, IReadOnlyList<CacheHistoryType> Types);

/// <summary>
/// One type a version moved: <see cref="Change"/> is added (the version before did not hold it), changed (it held other
/// content) or removed (the version no longer holds it), with how many of its records changed, arrived and left. Each
/// such version is a version of the type: <see cref="Hash"/> is the type's content hash in it, which differs from the one
/// the version before held, and <see cref="Items"/> how many records of the type it holds.
/// </summary>
/// <param name="TypeName">The type, by the name the version holds it under (or held it under, for a type it removed).</param>
/// <param name="Change">added, changed or removed.</param>
/// <param name="Counts">How many of the type's records the version changed, added and removed.</param>
/// <param name="Hash">The type's content hash in the version; null when the version removed it, or was written before types were hashed.</param>
/// <param name="Items">How many records of the type the version holds; 0 when it removed the type.</param>
public sealed record CacheHistoryType(string TypeName, string Change, CacheChangeCounts Counts, string? Hash, long Items)
{
    /// <summary>What <see cref="Change"/> says of a type the version no longer holds.</summary>
    public const string Removed = "removed";
}

/// <summary>How many records of one cached type changed, arrived and left between the two versions.</summary>
public sealed record CacheComparisonTypeCount(string TypeName, long Changed, long Added, long Removed);

/// <summary>
/// One cached record that differs: the captured values as JSON on each side (null on the side that does not hold it), and,
/// for a changed record, the captured names whose value moved.
/// </summary>
public sealed record CacheComparisonItem(
    string TypeName, string EntityType, string RecordId, CacheItemChange Change, string? BeforeJson, string? AfterJson, IReadOnlyList<string> ChangedFields);

/// <summary>What changed in a cache between two versions: counts per type, and one page of the records that differ.</summary>
public sealed record CacheComparison(
    string Scope, string FromVersion, string ToVersion, IReadOnlyList<CacheComparisonTypeCount> Types, long Total, IReadOnlyList<CacheComparisonItem> Items)
{
    public long Changed => Types.Sum(t => t.Changed);

    public long Added => Types.Sum(t => t.Added);

    public long Removed => Types.Sum(t => t.Removed);
}

/// <summary>
/// Reads of a partition cache across its versions. A record's row is valid over a range of consecutive versions, so a version's
/// records are the rows whose range covers its sequence, and what one version changed is the rows that begin or end at
/// it: a record that changed ends one row and begins another at the same version.
/// </summary>
public static class CacheVersions
{
    /// <summary>The most versions one listing returns.</summary>
    public const int MaxVersions = 500;

    /// <summary>The versions of a cache, newest first.</summary>
    public static async Task<IReadOnlyList<CacheVersionInfo>> ListAsync(OsduDbContext db, string scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var rows = await db.DeliveryCacheVersions.AsNoTracking()
            .Where(v => v.Scope == scope)
            .OrderByDescending(v => v.Sequence)
            .Take(MaxVersions)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(OsduCacheStore.Info).ToList();
    }

    /// <summary>The named version of a cache, or its current version when none is named; null when there is no such version.</summary>
    public static async Task<DeliveryCacheVersion?> ResolveAsync(OsduDbContext db, string scope, string? version, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var query = db.DeliveryCacheVersions.AsNoTracking().Where(v => v.Scope == scope);
        if (string.IsNullOrWhiteSpace(version))
        {
            query = query.Where(v => v.Current);
        }
        else
        {
            var label = version.Trim();
            query = query.Where(v => v.Version == label);
        }

        return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The rows a version of a cache holds: every record whose range covers the version's sequence.</summary>
    public static IQueryable<DeliveryCacheItem> ItemsAt(OsduDbContext db, string scope, int sequence)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.DeliveryCacheItems.AsNoTracking()
            .Where(i => i.Scope == scope && i.FromSequence <= sequence && (i.ToSequence == null || i.ToSequence > sequence));
    }

    /// <summary>
    /// The cache's history, newest first: each version with the version captured before it, how many records it changed,
    /// added and removed against that version, and which types it added, changed or removed. With a <paramref name="type"/>
    /// the counts and the types cover that type alone, which is what lets a reader find the versions that changed it. Three
    /// grouped queries answer every version at once.
    /// </summary>
    /// <remarks>
    /// A type's change is what the version recorded by the type's content hash. A version written before types were hashed
    /// recorded none, and its types are read from what the records and the versions hold instead: a type the version before
    /// did not list arrived, and one whose records began or ended a range at the version changed.
    /// </remarks>
    public static async Task<IReadOnlyList<CacheHistoryEntry>> HistoryAsync(OsduDbContext db, string scope, string? type, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var versions = await ListAsync(db, scope, ct).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            return [];
        }

        var named = string.IsNullOrWhiteSpace(type) ? null : type.Trim();
        var items = db.DeliveryCacheItems.AsNoTracking().Where(i => i.Scope == scope);
        if (named is not null)
        {
            items = items.Where(i => i.TypeName == named);
        }

        var arrived = Counted(await items
            .GroupBy(i => new { i.TypeName, i.FromSequence })
            .Select(g => new { g.Key.TypeName, Sequence = g.Key.FromSequence, Count = g.LongCount() })
            .ToListAsync(ct).ConfigureAwait(false), g => (g.TypeName, g.Sequence), g => g.Count);
        var departed = Counted(await items
            .Where(i => i.ToSequence != null)
            .GroupBy(i => new { i.TypeName, Sequence = i.ToSequence!.Value })
            .Select(g => new { g.Key.TypeName, g.Key.Sequence, Count = g.LongCount() })
            .ToListAsync(ct).ConfigureAwait(false), g => (g.TypeName, g.Sequence), g => g.Count);

        // A record that changed at a version ended one row and began another there.
        var changed = Counted(await items
            .Join(
                items.Where(d => d.ToSequence != null),
                a => new { a.TypeName, a.RecordId, Sequence = (int?)a.FromSequence },
                d => new { d.TypeName, d.RecordId, Sequence = d.ToSequence },
                (a, d) => new { a.TypeName, a.FromSequence })
            .GroupBy(x => new { x.TypeName, x.FromSequence })
            .Select(g => new { g.Key.TypeName, Sequence = g.Key.FromSequence, Count = g.LongCount() })
            .ToListAsync(ct).ConfigureAwait(false), g => (g.TypeName, g.Sequence), g => g.Count);

        // The types of the version before each listed one: the listing's next entry, and for the oldest listed, its own
        // predecessor, read when the listing stopped short of the first version.
        var bySequence = versions.ToDictionary(v => v.Sequence);
        var oldest = versions[^1];
        if (oldest.Sequence > 1 && await ResolveBySequenceAsync(db, scope, oldest.Sequence - 1, ct).ConfigureAwait(false) is { } predecessor)
        {
            bySequence[predecessor.Sequence] = OsduCacheStore.Info(predecessor);
        }

        return versions
            .Select(version =>
            {
                var before = bySequence.GetValueOrDefault(version.Sequence - 1);
                return new CacheHistoryEntry(
                    version, before?.Version, Total(version.Sequence, arrived, departed, changed), TypesMoved(version, before, named, arrived, departed, changed));
            })
            .ToList();
    }

    /// <summary>A version row by its place in the partition's history, or null when there is none.</summary>
    private static Task<DeliveryCacheVersion?> ResolveBySequenceAsync(OsduDbContext db, string scope, int sequence, CancellationToken ct)
        => db.DeliveryCacheVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Scope == scope && v.Sequence == sequence, ct);

    /// <summary>Grouped counts keyed by type (ignoring case, as types are named) and sequence, summed across the spellings the ranges hold.</summary>
    private static Dictionary<(string Type, int Sequence), long> Counted<T>(IEnumerable<T> groups, Func<T, (string Type, int Sequence)> key, Func<T, long> count)
    {
        var counted = new Dictionary<(string Type, int Sequence), long>(TypeSequenceComparer.Instance);
        foreach (var group in groups)
        {
            var at = key(group);
            counted[at] = counted.GetValueOrDefault(at) + count(group);
        }

        return counted;
    }

    /// <summary>How many records of <paramref name="typeName"/> changed, arrived and left at <paramref name="sequence"/>.</summary>
    private static CacheChangeCounts CountsOf(
        string typeName, int sequence,
        Dictionary<(string Type, int Sequence), long> arrived, Dictionary<(string Type, int Sequence), long> departed, Dictionary<(string Type, int Sequence), long> changed)
    {
        var both = changed.GetValueOrDefault((typeName, sequence));
        return new CacheChangeCounts(both, arrived.GetValueOrDefault((typeName, sequence)) - both, departed.GetValueOrDefault((typeName, sequence)) - both);
    }

    /// <summary>
    /// The types <paramref name="version"/> added, changed or removed, in ordinal order: by the change the version recorded
    /// for a type when it recorded one, and otherwise by what the version before listed and what the records' ranges hold.
    /// </summary>
    private static List<CacheHistoryType> TypesMoved(
        CacheVersionInfo version, CacheVersionInfo? before, string? named,
        Dictionary<(string Type, int Sequence), long> arrived, Dictionary<(string Type, int Sequence), long> departed, Dictionary<(string Type, int Sequence), long> changed)
    {
        bool InScope(string name) => named is null || string.Equals(name, named, StringComparison.OrdinalIgnoreCase);
        var earlier = before?.Types.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moved = new List<CacheHistoryType>();
        foreach (var held in version.Types.Where(t => InScope(t.Name)))
        {
            var counts = CountsOf(held.Name, version.Sequence, arrived, departed, changed);
            var change = held.Change
                ?? (earlier is null
                    ? version.Sequence == 1 ? CacheTypeChange.Added : counts.Total > 0 ? CacheTypeChange.Changed : CacheTypeChange.Unchanged
                    : !earlier.Contains(held.Name) ? CacheTypeChange.Added : counts.Total > 0 ? CacheTypeChange.Changed : CacheTypeChange.Unchanged);
            if (change != CacheTypeChange.Unchanged)
            {
                moved.Add(new CacheHistoryType(held.Name, CacheTypeChanges.Text(change), counts, held.Hash, held.Items));
            }
        }

        foreach (var left in (before?.Types ?? []).Where(t => InScope(t.Name) && !version.Types.Any(held => held.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))))
        {
            moved.Add(new CacheHistoryType(left.Name, CacheHistoryType.Removed, CountsOf(left.Name, version.Sequence, arrived, departed, changed), null, 0));
        }

        return moved.OrderBy(t => t.TypeName, StringComparer.Ordinal).ToList();
    }

    /// <summary>How many records of every type in scope changed, arrived and left at <paramref name="sequence"/>.</summary>
    private static CacheChangeCounts Total(
        int sequence,
        Dictionary<(string Type, int Sequence), long> arrived, Dictionary<(string Type, int Sequence), long> departed, Dictionary<(string Type, int Sequence), long> changed)
    {
        static long At(Dictionary<(string Type, int Sequence), long> counted, int sequence)
            => counted.Where(c => c.Key.Sequence == sequence).Sum(c => c.Value);

        var both = At(changed, sequence);
        return new CacheChangeCounts(both, At(arrived, sequence) - both, At(departed, sequence) - both);
    }

    /// <summary>Compares a type and a sequence as the history keys them: the type without regard to case.</summary>
    private sealed class TypeSequenceComparer : IEqualityComparer<(string Type, int Sequence)>
    {
        public static TypeSequenceComparer Instance { get; } = new();

        public bool Equals((string Type, int Sequence) x, (string Type, int Sequence) y)
            => x.Sequence == y.Sequence && string.Equals(x.Type, y.Type, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Type, int Sequence) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Type), obj.Sequence);
    }

    /// <summary>
    /// Compares two versions of a cache: the records both hold whose captured values differ, the records only the later
    /// version holds, and the ones only the earlier version holds. Values compare as the exact text stored (ordered names,
    /// binary collation), so a change of case is a change. Null when the cache holds neither named version.
    /// </summary>
    public static async Task<CacheComparison?> CompareAsync(OsduDbContext db, CacheComparisonQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.FromVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Take);

        var scope = query.Scope.Trim();
        var earlier = await ResolveAsync(db, scope, query.FromVersion, ct).ConfigureAwait(false);
        var later = await ResolveAsync(db, scope, query.ToVersion, ct).ConfigureAwait(false);
        if (earlier is null || later is null)
        {
            return null;
        }

        var before = ItemsAt(db, scope, earlier.Sequence);
        var after = ItemsAt(db, scope, later.Sequence);
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            var type = query.Type.Trim();
            before = before.Where(i => i.TypeName == type);
            after = after.Where(i => i.TypeName == type);
        }

        var removed = before.Where(b => !after.Any(a => a.TypeName == b.TypeName && a.RecordId == b.RecordId));
        var added = after.Where(a => !before.Any(b => b.TypeName == a.TypeName && b.RecordId == a.RecordId));

        // A record held unchanged by both versions is one row, so differing rows are the candidates; the stored text then
        // decides, because a value that changed and changed back is two rows holding the same text.
        var joined = before.Join(
            after,
            b => new { b.TypeName, b.RecordId },
            a => new { a.TypeName, a.RecordId },
            (b, a) => new { Before = b, After = a })
            .Where(x => x.Before.ItemId != x.After.ItemId);

        // SQL Server's default collation folds case, which would hide a corrected "Metre" to "metre"; the binary collation
        // the model keys OSDU ids with compares the text exactly.
        var changed = joined.Where(x => EF.Functions.Collate(x.Before.FieldsJson, DeliveryModel.OsduIdCollation) != EF.Functions.Collate(x.After.FieldsJson, DeliveryModel.OsduIdCollation));

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

        var page = new List<CacheComparisonItem>();
        long skip = query.Skip;
        foreach (var (kind, count) in segments)
        {
            var take = query.Take - page.Count;
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
                var rows = await changed
                    .OrderBy(x => x.Before.TypeName).ThenBy(x => x.Before.RecordId)
                    .Skip(offset).Take(take)
                    .Select(x => new { x.Before.TypeName, x.After.EntityType, x.Before.RecordId, BeforeJson = x.Before.FieldsJson, AfterJson = x.After.FieldsJson })
                    .ToListAsync(ct).ConfigureAwait(false);
                page.AddRange(rows.Select(r => new CacheComparisonItem(
                    r.TypeName, r.EntityType, r.RecordId, kind, r.BeforeJson, r.AfterJson, ChangedFields(r.BeforeJson, r.AfterJson))));
            }
            else
            {
                var side = kind == CacheItemChange.Added ? added : removed;
                var rows = await side
                    .OrderBy(i => i.TypeName).ThenBy(i => i.RecordId)
                    .Skip(offset).Take(take)
                    .Select(i => new { i.TypeName, i.EntityType, i.RecordId, i.FieldsJson })
                    .ToListAsync(ct).ConfigureAwait(false);
                page.AddRange(rows.Select(r => new CacheComparisonItem(
                    r.TypeName, r.EntityType, r.RecordId, kind,
                    kind == CacheItemChange.Removed ? r.FieldsJson : null,
                    kind == CacheItemChange.Added ? r.FieldsJson : null,
                    [])));
            }
        }

        return new CacheComparison(scope, earlier.Version, later.Version, types, total, page);
    }

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
