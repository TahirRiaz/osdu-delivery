using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// One OSDU partition the catalog knows (docs/partitions-design.md section 7): what its cache serves (the current version,
/// when it was captured, how many types and records it holds, the cache flows that fill it, the changes waiting for a
/// decision) and the delivery flows that name it. What the workbench's partition switcher lists, so another partition is
/// one choice away and each says what it holds before it is picked.
/// </summary>
/// <param name="Name">The partition, as flows name it and as its cache is keyed.</param>
/// <param name="CurrentVersion">The version of its cache deliveries read, or null while it holds none.</param>
/// <param name="CapturedUtc">When that version was captured.</param>
/// <param name="Types">How many types that version holds.</param>
/// <param name="Items">How many records and lookup rows that version holds.</param>
/// <param name="CacheFlows">The cache flows that fill its cache, by name, in order.</param>
/// <param name="DeliveryFlows">The delivery flows that name it under <c>partitions</c>, by name, in order.</param>
/// <param name="PendingChanges">Cache changes found in it that wait for someone to approve or reject them.</param>
public sealed record DeliveryPartitionDto(
    string Name,
    string? CurrentVersion,
    DateTime? CapturedUtc,
    int Types,
    long Items,
    IReadOnlyList<string> CacheFlows,
    IReadOnlyList<string> DeliveryFlows,
    long PendingChanges);

/// <summary>The partitions the catalog knows, for the partition every OSDU page is read in.</summary>
public static class DeliveryPartitionEndpoints
{
    /// <summary>Maps the partition listing under the module's group.</summary>
    public static void Map(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/partitions", ListAsync).WithName("ListDeliveryPartitions");
    }

    /// <summary>
    /// Every partition a cache is kept for, or a synced delivery flow names, ordered by name. A cache flow whose header
    /// names a partition the sync could not resolve keeps the reference as its scope; that is not a partition anyone can
    /// pick, so it is left out here and shown on the cache page as it is.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DeliveryPartitionDto>>> ListAsync(OsduDbContext osdu, CancellationToken ct)
    {
        var cacheFlows = await osdu.DeliveryCacheDefinitions.AsNoTracking()
            .Select(d => new { d.Scope, d.FlowName })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var deliveryFlows = await osdu.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.Active && i.Partition != "")
            .Select(i => new { i.Partition, i.FlowName })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var current = (await osdu.DeliveryCacheVersions.AsNoTracking()
                .Where(v => v.Current)
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(OsduCacheStore.Info)
            .ToDictionary(v => v.Scope, StringComparer.Ordinal);
        var pending = await osdu.DeliveryUpdateTags.AsNoTracking()
            .Where(t => t.Status == "pending")
            .GroupBy(t => t.Scope)
            .Select(g => new { Scope = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(g => g.Scope, g => g.Count, StringComparer.Ordinal, ct).ConfigureAwait(false);

        var names = cacheFlows.Select(c => c.Scope)
            .Concat(deliveryFlows.Select(d => d.Partition))
            .Concat(current.Keys)
            .Where(CacheScope.IsPartitionId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return TypedResults.Ok<IReadOnlyList<DeliveryPartitionDto>>(names.Select(name =>
        {
            var version = current.GetValueOrDefault(name);
            return new DeliveryPartitionDto(
                name,
                version?.Version,
                version?.CapturedUtc,
                version?.Types.Count ?? 0,
                version?.Items ?? 0,
                cacheFlows.Where(c => c.Scope == name).Select(c => c.FlowName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                deliveryFlows.Where(d => d.Partition == name).Select(d => d.FlowName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                pending.GetValueOrDefault(name));
        }).ToList());
    }
}
