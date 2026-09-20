using Microsoft.EntityFrameworkCore;
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
    string Status, DateTime? LastDeliveredUtc, DateTime UpdatedUtc, string? Interface = null,
    IReadOnlyList<DeliveryRecordMatchDto>? Matched = null);

/// <summary>
/// One value of a record that the term matched, and what that value is (an identity the mapping declares, a key value,
/// a word of the label, the OSDU id, the ingestion file). A hit says why it is a hit, so an operator searching a well
/// name sees the name they typed rather than having to work out which column answered.
/// </summary>
public sealed record DeliveryRecordMatchDto(string Value, string Kind);

/// <summary>
/// How a record the ledger found is described to a reader that holds no flow: the Records page's lookup and the combined
/// search read the same ledger lookup and name the record the same way, so this is the one place a hit is built.
/// </summary>
internal static class DeliveryRecordHits
{
    /// <summary>The matched values shown beside one hit, so a row says why it is a hit without becoming a list of its own.</summary>
    public const int MaxMatchesShown = 3;

    /// <summary>
    /// The records as hits, each with the pipeline and interface its ledger belongs to, in the order given. With a
    /// <paramref name="term"/>, each hit also carries the values of the record that start with it, read from the same
    /// identity index the lookup seeks, so the page can say which value answered.
    /// </summary>
    public static async Task<IReadOnlyList<DeliveryRecordHitDto>> DescribeAsync(
        CatalogDbContext catalog, OsduDbContext osdu, IReadOnlyList<RecordState> records, CancellationToken ct, string? term = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return [];
        }

        var pipelines = await DeliveryPipelines.ForLedgersAsync(catalog, osdu, records.Select(r => r.FlowId).Distinct().ToList(), ct).ConfigureAwait(false);
        var matches = await MatchesAsync(osdu, records, term, ct).ConfigureAwait(false);
        return records
            .Select(record => Describe(
                record,
                pipelines.GetValueOrDefault(record.FlowId),
                matches.GetValueOrDefault((record.FlowId, record.DeliveryKey.Value))))
            .ToList();
    }

    /// <summary>
    /// Which of each record's identity values the term matched, at most <see cref="MaxMatchesShown"/> per record. One
    /// seek of the same index the lookup used, over the records of the page alone.
    /// </summary>
    private static async Task<Dictionary<(Guid FlowId, Guid DeliveryKey), List<DeliveryRecordMatchDto>>> MatchesAsync(
        OsduDbContext osdu, IReadOnlyList<RecordState> records, string? term, CancellationToken ct)
    {
        var by = new Dictionary<(Guid, Guid), List<DeliveryRecordMatchDto>>();
        if (string.IsNullOrWhiteSpace(term) || Guid.TryParse(term.Trim(), out _))
        {
            // A delivery key matched the record itself, not one of its values; there is nothing to explain.
            return by;
        }

        var folded = RecordIdentities.Fold(term);
        var keys = records.Select(r => r.DeliveryKey.Value).Distinct().ToList();
        var rows = await osdu.DeliveryRecordIdentities.AsNoTracking()
            .Where(i => keys.Contains(i.DeliveryKey) && i.Token.StartsWith(folded))
            .OrderBy(i => i.Token)
            .Select(i => new { i.FlowId, i.DeliveryKey, i.Display, i.Kind })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var list = by.TryGetValue((row.FlowId, row.DeliveryKey), out var held) ? held : by[(row.FlowId, row.DeliveryKey)] = [];
            if (list.Count < MaxMatchesShown)
            {
                list.Add(new DeliveryRecordMatchDto(row.Display, row.Kind));
            }
        }

        return by;
    }

    /// <summary>One record as a hit; a record whose flow no synced repository holds any more is named by its ledger alone.</summary>
    public static DeliveryRecordHitDto Describe(RecordState record, LedgerPipeline? found, IReadOnlyList<DeliveryRecordMatchDto>? matched = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new DeliveryRecordHitDto(
            record.DeliveryKey.Value, record.FlowId, found?.Pipeline.Name, found?.Pipeline.Id, record.SourceKey,
            record.Label, record.TargetId, record.Status.ToString().ToLowerInvariant(), record.LastDeliveredUtc, record.UpdatedUtc,
            found is { Interface.Length: > 0 } ? found.Interface : null,
            matched is { Count: > 0 } ? matched : null);
    }

    /// <summary>How a hit names where it belongs: the pipeline, and the interface of a source.</summary>
    public static string? Named(LedgerPipeline? found)
        => found is null ? null : found.Interface.Length == 0 ? found.Pipeline.Name : $"{found.Pipeline.Name}/{found.Interface}";
}

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
            : await _ledger.LookupAsync(request.Phrase, take, null, ct).ConfigureAwait(false);
        var page = found.Skip(skip).Take(request.PageSize).ToList();

        // Fewer hits than asked for means no identity index ran into its bound, so that count is exact.
        var total = found.Count < take
            ? new BoundedCount(found.Count, Exact: true)
            : await _ledger.CountLookupAsync(request.Phrase, RecordListing.LookupCandidateLimit, null, ct).ConfigureAwait(false);

        var pipelines = await DeliveryPipelines.ForLedgersAsync(_catalog, _osdu, page.Select(r => r.FlowId).Distinct().ToList(), ct).ConfigureAwait(false);
        var items = page
            .Select(record =>
            {
                var pipeline = pipelines.GetValueOrDefault(record.FlowId);
                // A record is its flow and its key: the same row read by two flows is two hits, each with its own page.
                var path = DeliveryRecordRoutes.Path(record.FlowId, record.DeliveryKey.Value);
                return new SearchHitDto(
                    path,
                    record.Label ?? record.SourceKey,
                    DeliveryRecordHits.Named(pipeline),
                    "/delivery/records/" + path,
                    DeliveryRecordHits.Describe(record, pipeline));
            })
            .ToList();

        return new SearchContribution(items, total.Count, !total.Exact);
    }
}
