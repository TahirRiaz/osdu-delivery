using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>One delivery record a search found: where it belongs, how it is identified, and its custody state.</summary>
public sealed record DeliveryRecordHitDto(
    Guid DeliveryKey, Guid FlowId, string? FlowName, Guid? PipelineId, string SourceKey, string? Label, string? TargetId,
    string Status, DateTime? LastDeliveredUtc, DateTime UpdatedUtc);

/// <summary>
/// The <c>records</c> category the module adds to the control plane's search: a delivery key lands on one record, and an
/// OSDU id, a source key, a label or an origin file name lists the records that start with it, across every flow. The
/// lookup is the ledger's own indexed one, so it answers in milliseconds at production volume and counts no further than
/// its bound, which is what <see cref="SearchContribution.TotalCapped"/> then says.
/// </summary>
public sealed class RecordSearchContributor : ISearchContributor
{
    /// <summary>The category's key: the member the combined search carries it under, and its route segment.</summary>
    public const string CategoryKey = "records";

    private readonly ILedger _ledger;
    private readonly CatalogDbContext _catalog;

    public RecordSearchContributor(ILedger ledger, CatalogDbContext catalog)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(catalog);
        _ledger = ledger;
        _catalog = catalog;
    }

    public string Key => CategoryKey;

    public string Label => "Delivery records";

    /// <summary>The search surface's own read policy is enough: a record hit carries no more than the records page does.</summary>
    public string? RequiredPolicy => null;

    public async Task<SearchContribution> SearchAsync(SearchContributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The ledger's lookup answers from the identity indexes and reads at most its candidate bound, so a page past
        // that bound is empty rather than a scan: the phrase has to be narrowed instead.
        var wanted = (long)request.Page * request.PageSize;
        var take = (int)Math.Min(wanted, RecordListing.LookupCandidateLimit);
        var skip = (request.Page - 1) * request.PageSize;
        var found = skip >= RecordListing.LookupCandidateLimit
            ? []
            : await _ledger.LookupAsync(request.Phrase, take, ct).ConfigureAwait(false);
        var page = found.Skip(skip).Take(request.PageSize).ToList();

        // Fewer hits than asked for means no identity index ran into its bound, so that count is exact.
        var total = found.Count < take
            ? new BoundedCount(found.Count, Exact: true)
            : await _ledger.CountLookupAsync(request.Phrase, RecordListing.LookupCandidateLimit, ct).ConfigureAwait(false);

        var pipelines = await PipelinesAsync(page, ct).ConfigureAwait(false);
        var items = page
            .Select(record =>
            {
                var known = pipelines.TryGetValue(record.FlowId, out var pipeline);
                return new SearchHitDto(
                    record.DeliveryKey.Value.ToString("D"),
                    record.Label ?? record.SourceKey,
                    known ? pipeline.Name : null,
                    "/delivery/records/" + record.DeliveryKey.Value.ToString("D"),
                    new DeliveryRecordHitDto(
                        record.DeliveryKey.Value, record.FlowId, known ? pipeline.Name : null, known ? pipeline.Id : null, record.SourceKey,
                        record.Label, record.TargetId, record.Status.ToString().ToLowerInvariant(), record.LastDeliveredUtc, record.UpdatedUtc));
            })
            .ToList();

        return new SearchContribution(items, total.Count, !total.Exact);
    }

    /// <summary>The delivery pipeline behind each hit's flow id, so a hit links to the flow it belongs to.</summary>
    private async Task<Dictionary<Guid, (Guid Id, string Name)>> PipelinesAsync(IReadOnlyList<RecordState> records, CancellationToken ct)
    {
        var byFlowId = new Dictionary<Guid, (Guid Id, string Name)>();
        if (records.Count == 0)
        {
            return byFlowId;
        }

        var pipelines = await _catalog.Pipelines.AsNoTracking()
            .Where(p => p.Kind == FlowDefinition.FlowTypeName)
            .Select(p => new { p.Id, p.Name, p.Active })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var group in pipelines.GroupBy(p => FlowId.Of(p.Name)))
        {
            var pipeline = group.OrderByDescending(p => p.Active).First();
            byFlowId[group.Key] = (pipeline.Id, pipeline.Name);
        }

        return byFlowId;
    }
}
