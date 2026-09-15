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
/// One version in a cache's history: the version captured before it, and what it changed against that version. The
/// counts are those of the type in scope when one was named.
/// </summary>
public sealed record CacheHistoryEntry(CacheVersionInfo Version, string? Before, CacheChangeCounts Changes);

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
    /// The cache's history, newest first: each version with the version captured before it and how many records it
    /// changed, added and removed against that version. With a <paramref name="type"/> the counts cover that type alone,
    /// which is what lets a reader find the versions that changed it. Three grouped queries answer every version at once.
    /// </summary>
    public static async Task<IReadOnlyList<CacheHistoryEntry>> HistoryAsync(OsduDbContext db, string scope, string? type, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var versions = await ListAsync(db, scope, ct).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            return [];
        }

        var items = db.DeliveryCacheItems.AsNoTracking().Where(i => i.Scope == scope);
        if (!string.IsNullOrWhiteSpace(type))
        {
            var name = type.Trim();
            items = items.Where(i => i.TypeName == name);
        }

        var arrived = (await items
            .GroupBy(i => i.FromSequence)
            .Select(g => new { Sequence = g.Key, Count = g.LongCount() })
            .ToListAsync(ct).ConfigureAwait(false)).ToDictionary(g => g.Sequence, g => g.Count);
        var departed = (await items
            .Where(i => i.ToSequence != null)
            .GroupBy(i => i.ToSequence!.Value)
            .Select(g => new { Sequence = g.Key, Count = g.LongCount() })
            .ToListAsync(ct).ConfigureAwait(false)).ToDictionary(g => g.Sequence, g => g.Count);

        // A record that changed at a version ended one row and began another there.
        var changed = (await items
            .Join(
                items.Where(d => d.ToSequence != null),
                a => new { a.TypeName, a.RecordId, Sequence = (int?)a.FromSequence },
                d => new { d.TypeName, d.RecordId, Sequence = d.ToSequence },
                (a, d) => a.FromSequence)
            .GroupBy(sequence => sequence)
            .Select(g => new { Sequence = g.Key, Count = g.LongCount() })
            .ToListAsync(ct).ConfigureAwait(false)).ToDictionary(g => g.Sequence, g => g.Count);

        var labels = versions.ToDictionary(v => v.Sequence, v => v.Version);
        return versions
            .Select(version =>
            {
                var both = changed.GetValueOrDefault(version.Sequence);
                return new CacheHistoryEntry(
                    version,
                    labels.GetValueOrDefault(version.Sequence - 1),
                    new CacheChangeCounts(both, arrived.GetValueOrDefault(version.Sequence) - both, departed.GetValueOrDefault(version.Sequence) - both));
            })
            .ToList();
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
        // the model keys OSDU ids with compares the text exactly. SQLite compares ordinally already.
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
