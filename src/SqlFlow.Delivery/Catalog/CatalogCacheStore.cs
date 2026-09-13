using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The cache store over the catalog's <c>delivery.CacheVersion</c> and <c>delivery.CacheItem</c> tables. A version is written
/// in one transaction: its row first, which claims the cache's next sequence so a concurrent write of the same cache fails
/// instead of interleaving, then the records that changed, arrived or left against the newest version, then the current
/// flag. A record whose values did not change is not written again: its open range already covers the new version.
/// Versions never change once written, so the most recently loaded ones are kept in memory.
/// </summary>
public sealed class CatalogCacheStore : ICacheStore
{
    /// <summary>The width of a cache name in the catalog, the same as a flow name.</summary>
    public const int MaxCacheNameLength = 200;

    private const int InsertChunk = 2_000;

    private const int UpdateChunk = 1_000;

    /// <summary>How many loaded versions stay in memory. A run renders against one; the GUI reads a handful.</summary>
    private const int RetainedVersions = 8;

    private readonly Func<CatalogDbContext> _factory;
    private readonly Lock _gate = new();
    private readonly LinkedList<(string Cache, string Version, ReferenceSnapshot Snapshot)> _recent = new();

    public CatalogCacheStore(Func<CatalogDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<string?> CurrentVersionAsync(string cache, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cache);
        await using var db = _factory();
        return await db.DeliveryCacheVersions.AsNoTracking()
            .Where(v => v.CacheName == cache && v.Current)
            .Select(v => v.Version)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<ReferenceSnapshot?> LoadAsync(string cache, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cache);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (Recent(cache, version) is { } known)
        {
            return known;
        }

        await using var db = _factory();
        var row = await db.DeliveryCacheVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.CacheName == cache && v.Version == version, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var sequence = row.Sequence;
        var items = await db.DeliveryCacheItems.AsNoTracking()
            .Where(i => i.CacheName == cache && i.FromSequence <= sequence && (i.ToSequence == null || i.ToSequence > sequence))
            .Select(i => new { i.TypeName, i.RecordId, i.FieldsJson })
            .ToListAsync(ct).ConfigureAwait(false);
        var byType = items.GroupBy(i => i.TypeName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // The version row lists every type, a type that matched no record included, so the loaded version holds exactly
        // the types the capture held and hashes as it did.
        var types = ParseTypes(row.TypesJson, cache, version)
            .Select(type => new ReferenceType(
                type.Name,
                type.EntityType,
                (byType.GetValueOrDefault(type.Name) ?? [])
                    .OrderBy(i => i.RecordId, StringComparer.Ordinal)
                    .Select(i => new ReferenceItem(i.RecordId, Fields(i.FieldsJson, cache, version)))))
            .ToList();
        var snapshot = new ReferenceSnapshot(row.Version, new DateTimeOffset(DateTime.SpecifyKind(row.CapturedUtc, DateTimeKind.Utc)), types);
        if (!string.Equals(snapshot.ContentHash(), row.ContentHash, StringComparison.Ordinal))
        {
            throw new DeliveryException(
                $"Version {version} of cache '{cache}' does not match the content hash it was written with: its records were altered after the version was written, so nothing renders against it.");
        }

        Remember(cache, version, snapshot);
        return snapshot;
    }

    public async Task<IReadOnlyList<CacheVersionInfo>> ListVersionsAsync(string cache, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cache);
        await using var db = _factory();
        var rows = await db.DeliveryCacheVersions.AsNoTracking()
            .Where(v => v.CacheName == cache)
            .OrderByDescending(v => v.Sequence)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(Info).ToList();
    }

    public async Task<CacheVersionInfo> SaveAsync(string cache, ReferenceSnapshot snapshot, CacheCapture capture, bool makeCurrent, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cache);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(capture);
        if (cache.Length > MaxCacheNameLength)
        {
            throw new DeliveryException($"A cache name is at most {MaxCacheNameLength} characters; '{cache[..40]}...' is longer.");
        }

        var normalized = snapshot.Normalized();
        var types = normalized.Types.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        foreach (var type in types)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (type.Items.FirstOrDefault(item => !ids.Add(item.Id)) is { } twice)
            {
                throw new DeliveryException(
                    $"Cache '{cache}': type {type.Name} holds record {twice.Id} more than once, so the version could not say which values it holds. Nothing was written.");
            }
        }

        var hash = normalized.ContentHash();
        var typesJson = TypesJson(types);
        var itemCount = types.Sum(t => (long)t.Items.Count);
        var capturedUtc = normalized.CapturedUtc.UtcDateTime;

        await using var db = _factory();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                // A retried attempt rebuilds everything it stages from the snapshot, never from what a failed attempt tracked.
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                if (await db.DeliveryCacheVersions.AnyAsync(v => v.CacheName == cache && v.Version == normalized.Version, ct).ConfigureAwait(false))
                {
                    throw new DeliveryException($"Cache '{cache}' already holds version {normalized.Version}; a version is never rewritten. Nothing was written.");
                }

                var previous = await db.DeliveryCacheVersions
                    .Where(v => v.CacheName == cache && v.Current)
                    .Select(v => v.Version)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                var sequence = (await db.DeliveryCacheVersions
                    .Where(v => v.CacheName == cache)
                    .MaxAsync(v => (int?)v.Sequence, ct).ConfigureAwait(false) ?? 0) + 1;

                // The row goes in first: the unique sequence is what a concurrent write of the same cache collides on.
                var row = new DeliveryCacheVersion
                {
                    Id = FlowIdentity.FromName($"delivery-cache-version/{cache}/{normalized.Version}"),
                    CacheName = cache,
                    Version = normalized.Version,
                    Sequence = sequence,
                    CapturedUtc = capturedUtc,
                    ContentHash = hash,
                    PreviousVersion = previous,
                    Current = false,
                    RunId = capture.RunId,
                    CapturedBy = Clip(capture.CapturedBy, 200),
                    Origin = Clip(capture.Origin, 1000),
                    TypesJson = typesJson,
                    Items = itemCount,
                };
                db.DeliveryCacheVersions.Add(row);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                // What the newest version holds: every open range. The new version is compared against it, record by record.
                var open = await db.DeliveryCacheItems.AsNoTracking()
                    .Where(i => i.CacheName == cache && i.ToSequence == null)
                    .Select(i => new { i.ItemId, i.TypeName, i.EntityType, i.RecordId, i.FieldsJson })
                    .ToListAsync(ct).ConfigureAwait(false);
                var held = open.ToDictionary(o => (o.TypeName, o.RecordId));

                var closing = new List<long>();
                var arriving = new List<DeliveryCacheItem>();
                var seen = new HashSet<(string TypeName, string RecordId)>();
                foreach (var type in types)
                {
                    foreach (var item in type.Items)
                    {
                        var key = (type.Name, item.Id);
                        seen.Add(key);
                        var fields = FieldsJson(item);
                        if (held.TryGetValue(key, out var existing))
                        {
                            if (string.Equals(existing.FieldsJson, fields, StringComparison.Ordinal)
                                && string.Equals(existing.EntityType, type.EntityType, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            closing.Add(existing.ItemId);
                        }

                        arriving.Add(new DeliveryCacheItem
                        {
                            CacheName = cache,
                            TypeName = type.Name,
                            EntityType = type.EntityType,
                            RecordId = item.Id,
                            FieldsJson = fields,
                            Terms = Terms(item),
                            FromSequence = sequence,
                        });
                    }
                }

                closing.AddRange(open.Where(o => !seen.Contains((o.TypeName, o.RecordId))).Select(o => o.ItemId));
                foreach (var chunk in closing.Chunk(UpdateChunk))
                {
                    var ids = chunk.ToList();
                    await db.DeliveryCacheItems
                        .Where(i => ids.Contains(i.ItemId))
                        .ExecuteUpdateAsync(set => set.SetProperty(i => i.ToSequence, (int?)sequence), ct).ConfigureAwait(false);
                }

                foreach (var chunk in arriving.Chunk(InsertChunk))
                {
                    db.DeliveryCacheItems.AddRange(chunk);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    db.ChangeTracker.Clear();
                }

                if (makeCurrent)
                {
                    await db.DeliveryCacheVersions
                        .Where(v => v.CacheName == cache && v.Current)
                        .ExecuteUpdateAsync(set => set.SetProperty(v => v.Current, false), ct).ConfigureAwait(false);
                    await db.DeliveryCacheVersions
                        .Where(v => v.Id == row.Id)
                        .ExecuteUpdateAsync(set => set.SetProperty(v => v.Current, true), ct).ConfigureAwait(false);
                    row.Current = true;
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return Info(row);
            }).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            throw new DeliveryException(
                $"Version {normalized.Version} of cache '{cache}' could not be written, most likely because another refresh of the same cache wrote a version at the same time. Nothing of this version was kept; run the refresh again.",
                ex);
        }
    }

    /// <summary>A version row as the store and the catalog readers describe it.</summary>
    public static CacheVersionInfo Info(DeliveryCacheVersion row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new CacheVersionInfo(
            row.CacheName, row.Version, row.Sequence, DateTime.SpecifyKind(row.CapturedUtc, DateTimeKind.Utc), row.Current, row.PreviousVersion,
            row.RunId, row.CapturedBy, row.Origin, row.Items, ParseTypes(row.TypesJson, row.CacheName, row.Version));
    }

    /// <summary>The captured values of a record as the catalog stores them: names in ordinal order, each value as captured.</summary>
    public static string FieldsJson(ReferenceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var fields = new JsonObject();
        foreach (var (name, value) in item.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            fields[name] = value.Node.DeepClone();
        }

        return fields.ToJsonString();
    }

    /// <summary>Every scalar the record holds, newline separated, so a text search over the cache is one predicate.</summary>
    private static string Terms(ReferenceItem item)
        => string.Join('\n', item.Fields.Values.SelectMany(v => v.Terms).Distinct(StringComparer.OrdinalIgnoreCase));

    private static string TypesJson(IEnumerable<ReferenceType> types)
    {
        var array = new JsonArray();
        foreach (var type in types)
        {
            array.Add(new JsonObject { ["name"] = type.Name, ["entityType"] = type.EntityType, ["items"] = type.Items.Count });
        }

        return array.ToJsonString();
    }

    private static List<CacheVersionType> ParseTypes(string json, string cache, string version)
    {
        try
        {
            return (JsonNode.Parse(json) as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(type => new CacheVersionType(
                    type["name"]?.GetValue<string>() ?? throw new DeliveryException($"Version {version} of cache '{cache}' lists a type without a name."),
                    type["entityType"]?.GetValue<string>() ?? string.Empty,
                    type["items"]?.GetValue<long>() ?? 0))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new DeliveryException($"Version {version} of cache '{cache}' has a type list that is not valid JSON ({ex.Message}).", ex);
        }
    }

    private static Dictionary<string, ReferenceValue> Fields(string json, string cache, string version)
    {
        JsonObject? node;
        try
        {
            node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"A record of version {version} of cache '{cache}' holds values that are not valid JSON ({ex.Message}).", ex);
        }

        var fields = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in node ?? [])
        {
            if (value is not null)
            {
                fields[name] = ReferenceValue.From(value);
            }
        }

        return fields;
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];

    private ReferenceSnapshot? Recent(string cache, string version)
    {
        lock (_gate)
        {
            for (var node = _recent.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.Cache, cache, StringComparison.Ordinal) && string.Equals(node.Value.Version, version, StringComparison.Ordinal))
                {
                    _recent.Remove(node);
                    _recent.AddFirst(node);
                    return node.Value.Snapshot;
                }
            }
        }

        return null;
    }

    private void Remember(string cache, string version, ReferenceSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_recent.Any(e => string.Equals(e.Cache, cache, StringComparison.Ordinal) && string.Equals(e.Version, version, StringComparison.Ordinal)))
            {
                return;
            }

            _recent.AddFirst((cache, version, snapshot));
            while (_recent.Count > RetainedVersions)
            {
                _recent.RemoveLast();
            }
        }
    }
}
