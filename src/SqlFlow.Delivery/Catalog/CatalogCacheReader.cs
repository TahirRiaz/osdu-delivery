using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// A repository's cache as the catalog carries it, for the checks that run without a node: the cached types the
/// repository's retrieval flows declare, and its current reference snapshot rebuilt from the catalog's copy of the items.
/// Snapshot versions never change, so the most recently read ones are kept in memory.
/// </summary>
public sealed class CatalogCacheReader
{
    /// <summary>How many snapshot versions stay in memory.</summary>
    private const int RetainedSnapshots = 4;

    private readonly Lock _gate = new();
    private readonly LinkedList<(Guid SnapshotId, ReferenceSnapshot Snapshot)> _recent = new();

    /// <summary>The repository's current reference snapshot, or <see cref="ReferenceSnapshot.Empty"/> when the catalog carries none.</summary>
    public async Task<ReferenceSnapshot> CurrentAsync(CatalogDbContext db, Guid repoId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var resolved = await CacheVersions.ResolveAsync(db, repoId, version: null, ct).ConfigureAwait(false);
        if (resolved.Count == 0)
        {
            return ReferenceSnapshot.Empty;
        }

        var snapshot = resolved[0];

        lock (_gate)
        {
            var node = _recent.First;
            while (node is not null)
            {
                if (node.Value.SnapshotId == snapshot.Id)
                {
                    _recent.Remove(node);
                    _recent.AddFirst(node);
                    return node.Value.Snapshot;
                }

                node = node.Next;
            }
        }

        var rows = await db.DeliverySnapshotItems.AsNoTracking()
            .Where(i => i.SnapshotId == snapshot.Id)
            .Select(i => new { i.TypeName, i.EntityType, i.RecordId, i.FieldsJson })
            .ToListAsync(ct).ConfigureAwait(false);
        var types = rows
            .GroupBy(r => (r.TypeName, r.EntityType))
            .Select(g => new ReferenceType(g.Key.TypeName, g.Key.EntityType, g.Select(r => new ReferenceItem(r.RecordId, Fields(r.FieldsJson)))))
            .ToList();
        var captured = snapshot.CapturedUtc is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : DateTimeOffset.UnixEpoch;
        var loaded = new ReferenceSnapshot(snapshot.Version, captured, types);

        lock (_gate)
        {
            _recent.AddFirst((snapshot.Id, loaded));
            while (_recent.Count > RetainedSnapshots)
            {
                _recent.RemoveLast();
            }
        }

        return loaded;
    }

    /// <summary>
    /// The cached types of a repository: what its retrieval flows declare, with the names their fields are cached under,
    /// then any type the current snapshot holds that no flow declares any more.
    /// </summary>
    public static async Task<IReadOnlyList<CachedTypeInfo>> TypesAsync(CatalogDbContext db, Guid repoId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var definitions = await db.DeliveryCacheDefinitions.AsNoTracking()
            .Where(c => c.RepoId == repoId)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Name, c.EntityType, c.FieldsJson })
            .ToListAsync(ct).ConfigureAwait(false);
        var types = new List<CachedTypeInfo>();
        foreach (var definition in definitions)
        {
            if (types.Any(t => string.Equals(t.Name, definition.Name, StringComparison.Ordinal)))
            {
                continue;
            }

            types.Add(new CachedTypeInfo(definition.Name, definition.EntityType, DeclaredFields(definition.FieldsJson)));
        }

        var resolved = await CacheVersions.ResolveAsync(db, repoId, version: null, ct).ConfigureAwait(false);
        if (resolved.Count > 0)
        {
            var snapshot = resolved[0];
            var held = await db.DeliverySnapshotItems.AsNoTracking()
                .Where(i => i.SnapshotId == snapshot.Id)
                .Select(i => new { i.TypeName, i.EntityType })
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var type in held.OrderBy(t => t.TypeName, StringComparer.Ordinal))
            {
                if (!types.Any(t => string.Equals(t.Name, type.TypeName, StringComparison.Ordinal)))
                {
                    types.Add(new CachedTypeInfo(type.TypeName, type.EntityType, []));
                }
            }
        }

        return types;
    }

    /// <summary>The names a declaration's fields are cached under: the <c>as</c> it gives, or the path without its <c>data.</c> root.</summary>
    private static IReadOnlyList<string> DeclaredFields(string json)
    {
        try
        {
            return (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(field => field["as"] is JsonValue alias && alias.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : field["path"] is JsonValue path && path.TryGetValue<string>(out var p) && !string.IsNullOrWhiteSpace(p) ? ReferenceField.Normalize(p) : null)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"A cache definition's fields are not valid JSON ({ex.Message}); re-sync the repository.", ex);
        }
    }

    private static Dictionary<string, ReferenceValue> Fields(string json)
    {
        var fields = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase);
        JsonObject? node;
        try
        {
            node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"A cached record's values are not valid JSON ({ex.Message}); re-sync the repository.", ex);
        }

        foreach (var (name, value) in node ?? [])
        {
            if (value is not null)
            {
                fields[name] = ReferenceValue.From(value.DeepClone());
            }
        }

        return fields;
    }
}
