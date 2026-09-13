using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The types a cache holds as the catalog describes them, for the checks and the mapping builder that run without a node:
/// what the cache flow declares, with the names its fields are cached under, then any type the current version holds that
/// the flow no longer declares.
/// </summary>
public static class CatalogCacheReader
{
    /// <summary>The cached types of <paramref name="cache"/>, declared ones first in name order.</summary>
    public static async Task<IReadOnlyList<CachedTypeInfo>> TypesAsync(CatalogDbContext db, string cache, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(cache);
        var definitions = await db.DeliveryCacheDefinitions.AsNoTracking()
            .Where(c => c.CacheName == cache)
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

        var current = await db.DeliveryCacheVersions.AsNoTracking()
            .Where(v => v.CacheName == cache && v.Current)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (current is not null)
        {
            foreach (var held in CatalogCacheStore.Info(current).Types.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                if (!types.Any(t => string.Equals(t.Name, held.Name, StringComparison.Ordinal)))
                {
                    types.Add(new CachedTypeInfo(held.Name, held.EntityType, []));
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
}
