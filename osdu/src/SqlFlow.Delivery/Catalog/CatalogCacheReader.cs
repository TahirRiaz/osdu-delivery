using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The types a partition's cache holds as the catalog describes them, for the checks and the mapping builder that run
/// without a node: every type the partition's synced cache flows declare, with every name any of them caches its values
/// under, then any type the current version holds that no flow declares any more.
/// </summary>
public static class CatalogCacheReader
{
    /// <summary>The cached types of the partition's cache, declared ones first in name order.</summary>
    public static async Task<IReadOnlyList<CachedTypeInfo>> TypesAsync(OsduDbContext db, string scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var declaration = await OsduCacheStore.DeclarationAsync(db, scope, ct).ConfigureAwait(false);
        var types = declaration.TypeNames
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                var first = declaration.Of(name)[0];
                return new CachedTypeInfo(first.TypeName, first.EntityType, declaration.FieldsOf(name).Select(f => f.Name).ToList(), first.Key);
            })
            .ToList();

        var current = await db.DeliveryCacheVersions.AsNoTracking()
            .Where(v => v.Scope == scope && v.Current)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (current is not null)
        {
            foreach (var held in OsduCacheStore.Info(current).Types.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                if (!types.Any(t => string.Equals(t.Name, held.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    types.Add(new CachedTypeInfo(held.Name, held.EntityType, [], held.Key));
                }
            }
        }

        return types;
    }
}
