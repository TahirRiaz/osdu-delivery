using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>A delivery pipeline and the interface of it that keeps a ledger identity (empty for the single form).</summary>
internal sealed record LedgerPipeline(CatalogPipeline Pipeline, string Interface);

/// <summary>
/// Finds the pipeline behind a ledger identity through the read model of sources and interfaces
/// (<see cref="DeliveryInterfaceCatalog"/>): the interface keeping the identity names its flow and repository, and the
/// catalog holds the pipeline. When several keep it (a flow declared in two repositories, an adopted ledger whose old flow
/// is still declared, a flow removed from its repository), the interface its repository still declares answers first,
/// then the active pipeline.
/// </summary>
internal static class DeliveryPipelines
{
    public static async Task<LedgerPipeline?> ForLedgerAsync(CatalogDbContext db, OsduDbContext osdu, Guid flowId, CancellationToken ct)
    {
        var found = await ForLedgersAsync(db, osdu, [flowId], ct).ConfigureAwait(false);
        return found.GetValueOrDefault(flowId);
    }

    /// <summary>The pipeline behind each of <paramref name="flowIds"/> that has one.</summary>
    public static async Task<Dictionary<Guid, LedgerPipeline>> ForLedgersAsync(
        CatalogDbContext db, OsduDbContext osdu, IReadOnlyCollection<Guid> flowIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(flowIds);
        var result = new Dictionary<Guid, LedgerPipeline>();
        if (flowIds.Count == 0)
        {
            return result;
        }

        var ids = flowIds.Distinct().ToList();
        var locations = await osdu.DeliveryInterfaces.AsNoTracking()
            .Where(i => ids.Contains(i.LedgerFlowId))
            .Select(i => new { i.LedgerFlowId, i.RepoId, i.FlowName, i.Interface, i.Active })
            .ToListAsync(ct).ConfigureAwait(false);
        if (locations.Count == 0)
        {
            return result;
        }

        var names = locations.Select(l => l.FlowName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == FlowDefinition.FlowTypeName && names.Contains(p.Name))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var group in locations.GroupBy(l => l.LedgerFlowId))
        {
            var match = group
                .SelectMany(l => pipelines
                    .Where(p => p.RepoId == l.RepoId && string.Equals(p.Name, l.FlowName, StringComparison.OrdinalIgnoreCase))
                    .Select(p => (Match: new LedgerPipeline(p, l.Interface), Declared: l.Active)))
                .OrderByDescending(m => m.Declared)
                .ThenByDescending(m => m.Match.Pipeline.Active)
                .ThenBy(m => m.Match.Pipeline.Name, StringComparer.Ordinal)
                .Select(m => m.Match)
                .FirstOrDefault();
            if (match is not null)
            {
                result[group.Key] = match;
            }
        }

        return result;
    }
}
