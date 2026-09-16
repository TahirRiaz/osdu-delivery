using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// One delivery record a search found: where it belongs (the flow's pipeline and, for a source, the interface), how it is
/// identified, and its custody state.
/// </summary>
public sealed record DeliveryRecordHitDto(
    Guid DeliveryKey, Guid FlowId, string? FlowName, Guid? PipelineId, string SourceKey, string? Label, string? TargetId,
    string Status, DateTime? LastDeliveredUtc, DateTime UpdatedUtc, string? Interface = null);

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
    private readonly OsduDbContext _osdu;

    public RecordSearchContributor(ILedger ledger, CatalogDbContext catalog, OsduDbContext osdu)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(osdu);
        _ledger = ledger;
        _catalog = catalog;
        _osdu = osdu;
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

        var pipelines = await DeliveryPipelines.ForLedgersAsync(_catalog, _osdu, page.Select(r => r.FlowId).ToList(), ct).ConfigureAwait(false);
        var items = page
            .Select(record =>
            {
                var known = pipelines.TryGetValue(record.FlowId, out var found);
                var name = known ? Named(found!) : null;
                // A record is its flow and its key: the same row read by two flows is two hits, each with its own page.
                var path = DeliveryRecordRoutes.Path(record.FlowId, record.DeliveryKey.Value);
                return new SearchHitDto(
                    path,
                    record.Label ?? record.SourceKey,
                    name,
                    "/delivery/records/" + path,
                    new DeliveryRecordHitDto(
                        record.DeliveryKey.Value, record.FlowId, known ? found!.Pipeline.Name : null, known ? found!.Pipeline.Id : null, record.SourceKey,
                        record.Label, record.TargetId, record.Status.ToString().ToLowerInvariant(), record.LastDeliveredUtc, record.UpdatedUtc,
                        known && found!.Interface.Length > 0 ? found.Interface : null));
            })
            .ToList();

        return new SearchContribution(items, total.Count, !total.Exact);
    }

    /// <summary>How a hit names where it belongs: the pipeline, and the interface of a source.</summary>
    private static string Named(LedgerPipeline found)
        => found.Interface.Length == 0 ? found.Pipeline.Name : $"{found.Pipeline.Name}/{found.Interface}";
}
