using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// A delivery pipeline, the interface of it that keeps a ledger identity (empty for the single form), and the partition
/// whose ledger it is (empty for a ledger not yet placed in one).
/// </summary>
/// <summary>
/// The pipeline behind a ledger identity, the interface of it the ledger keeps, and the partition the ledger is kept under
/// (empty for a ledger not yet placed). <c>Bound</c> says whether that partition binds the flow in a request: true for a
/// flow that names or follows partitions, whose requests name the partition; false for one whose partition is its
/// data-partition-id header, which takes none.
/// </summary>
internal sealed record LedgerPipeline(CatalogPipeline Pipeline, string Interface, string Partition = "", bool Bound = false);

/// <summary>
/// Finds the pipeline behind a ledger identity through the read model of sources and interfaces
/// (<see cref="DeliveryInterfaceCatalog"/>): the interface keeping the identity names its flow and repository, and the
/// catalog holds the pipeline. When several keep it (a flow declared in two repositories, an adopted ledger whose old flow
/// is still declared, a flow removed from its repository), the interface its repository still declares answers first,
/// then the active pipeline. A ledger no synced interface describes yet (a partition registered after the last sync) is
/// found through the ledger's directory, by the flow and interface it names. The partition is always the directory's:
/// the ledger's own, which a flow whose partition is its header's has as much as one that names its partitions. Whether it
/// binds the flow is the interface catalog's to say, which describes a flow's interfaces per partition only for a flow that
/// names or follows them; a ledger found through the directory alone is of such a flow, since that is the one whose
/// partitions can outrun the sync.
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
        var directory = await DirectoryAsync(osdu, ids, ct).ConfigureAwait(false);
        var locations = await osdu.DeliveryInterfaces.AsNoTracking()
            .Where(i => ids.Contains(i.LedgerFlowId))
            .Select(i => new { i.LedgerFlowId, i.RepoId, i.FlowName, i.Interface, i.Partition, i.Active })
            .ToListAsync(ct).ConfigureAwait(false);
        var names = locations.Select(l => l.FlowName)
            .Concat(directory.Values.Where(d => d.Kind == LedgerKinds.Delivery).Select(d => d.FlowName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
        {
            return result;
        }

        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == FlowDefinition.FlowTypeName && names.Contains(p.Name))
            .ToListAsync(ct).ConfigureAwait(false);
        string PartitionOf(Guid flowId, string described)
            => directory.TryGetValue(flowId, out var ledger) ? ledger.Partition ?? string.Empty : described;

        foreach (var group in locations.GroupBy(l => l.LedgerFlowId))
        {
            var match = group
                .SelectMany(l => pipelines
                    .Where(p => p.RepoId == l.RepoId && string.Equals(p.Name, l.FlowName, StringComparison.OrdinalIgnoreCase))
                    .Select(p => (Match: new LedgerPipeline(p, l.Interface, PartitionOf(group.Key, l.Partition), Bound: l.Partition.Length > 0), Declared: l.Active)))
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

        // A ledger the interface catalog does not describe yet: the active pipeline its directory row names.
        foreach (var (flowId, ledger) in directory.Where(d => !result.ContainsKey(d.Key) && d.Value.Kind == LedgerKinds.Delivery))
        {
            var pipeline = pipelines
                .Where(p => string.Equals(p.Name, ledger.FlowName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Active)
                .ThenBy(p => p.RepoId)
                .FirstOrDefault();
            if (pipeline is not null)
            {
                result[flowId] = new LedgerPipeline(pipeline, ledger.Interface, ledger.Partition ?? string.Empty, Bound: true);
            }
        }

        return result;
    }

    /// <summary>The directory rows of <paramref name="flowIds"/>, each with its partition's name, or null for an unassigned ledger.</summary>
    private static async Task<Dictionary<Guid, LedgerEntry>> DirectoryAsync(OsduDbContext osdu, IReadOnlyList<Guid> flowIds, CancellationToken ct)
    {
        var rows = await osdu.DeliveryLedgers.AsNoTracking()
            .Where(l => flowIds.Contains(l.FlowId))
            .Select(l => new
            {
                l.FlowId,
                l.Kind,
                l.FlowName,
                l.Interface,
                l.LedgerName,
                l.RegisteredUtc,
                Partition = osdu.DeliveryLedgerPartitions.Where(p => p.PartitionId == l.PartitionId).Select(p => p.Name).FirstOrDefault(),
            })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(
            r => r.FlowId,
            r => new LedgerEntry
            {
                FlowId = r.FlowId,
                Partition = r.Partition,
                Kind = r.Kind,
                FlowName = r.FlowName,
                Interface = r.Interface,
                LedgerName = r.LedgerName,
                RegisteredUtc = DateTime.SpecifyKind(r.RegisteredUtc, DateTimeKind.Utc),
            });
    }
}
