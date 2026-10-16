using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.SearchTerms;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// The conditions on search terms a search asks (osdu/docs/reference/concepts/search-terms.md): each turned into the
/// condition on the record its term's route fills, in the partition the search reads, before the search is checked and
/// run.
/// </summary>
public static partial class DeliveryExplorerEndpoints
{
    /// <summary>Every condition the explorer asks, as a message lists them.</summary>
    internal const string ConditionNames = "is, isNot, anyOf, noneOf, contains, startsWith, range, exists or missing";

    /// <summary>
    /// The conditions of <paramref name="body"/> that name a search term, each resolved by place in the partition the search
    /// reads: the one asked, else the workbench's, else the registry's default, as the search's own connection picks it. None
    /// for a search naming no term; a problem answer for a term that cannot be searched as asked, or a partition no flow reaches.
    /// </summary>
    internal static async Task<(IReadOnlyDictionary<int, ExplorerFilter>? Terms, Results<ContentHttpResult, ProblemHttpResult>? Failure)> TermsOfAsync(
        DeliveryExplorerSearchRequest body, string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger,
        HttpRequest request, SearchTermDirectory directory, CancellationToken ct)
    {
        var filters = body.Filters ?? [];
        if (!filters.Any(f => f?.Term is not null))
        {
            return (null, null);
        }

        var found = await ConnectAsync(db, documents, partitions, ledger, WorkbenchPartition.Named(partition, request), ct).ConfigureAwait(false);
        if (found.Flow is null || found.Partition is not { } read)
        {
            return (null, TypedResults.Problem(detail: found.Reason ?? "No partition is named, so no search term can be read.", statusCode: StatusCodes.Status409Conflict, title: "No connection to the partition"));
        }

        var resolved = new Dictionary<int, ExplorerFilter>();
        for (var place = 0; place < filters.Count; place++)
        {
            if (filters[place] is not { Term: { } term } filter)
            {
                continue;
            }

            if (!TryParse<ExplorerCondition>(filter.Condition, ExplorerCondition.Is, out var condition))
            {
                return (null, DeliveryEndpoints.Invalid($"'{filter.Condition}' is not a condition: {ConditionNames}."));
            }

            try
            {
                // The route of the term for the kind searched: the version of the mapping that renders it, where versions differ.
                resolved[place] = await directory.ResolveAsync(term, new SearchTermCondition(condition, filter.Value, filter.Values, filter.To), read, body.Kind, ct).ConfigureAwait(false);
            }
            catch (DeliveryException ex)
            {
                return (null, DeliveryEndpoints.Invalid(ex.Message));
            }
        }

        return (resolved, null);
    }
}
