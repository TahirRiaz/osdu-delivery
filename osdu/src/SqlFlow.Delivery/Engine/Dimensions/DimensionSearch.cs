using System.Globalization;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>The values picked in one dimension: by value id, by value, or both.</summary>
public sealed record DimensionPick(DimensionState Dimension, IReadOnlyCollection<long> ValueIds, IReadOnlyCollection<string> Values);

/// <summary>One dimension's part of a composed search: the values it covers, how many keys they hold, and its filter.</summary>
/// <param name="DimensionId">The dimension.</param>
/// <param name="Dimension">Its name.</param>
/// <param name="AggregateBy">The field the filter compares, as the search's aggregateBy names it.</param>
/// <param name="Values">The values picked that the dimension holds now, each with its id, records and keys.</param>
/// <param name="Keys">The keys the filter compares.</param>
/// <param name="Unfilterable">Keys of the values picked that no query can carry, which the filter leaves out.</param>
/// <param name="Filter">The part's filter: one query, or several joined with OR when the keys are more than one query holds.</param>
/// <param name="Query">The dimension's own query, the records its values were read from; null for every record of its kind.</param>
public sealed record DimensionSearchPart(
    int DimensionId, string Dimension, string AggregateBy, IReadOnlyList<DimensionMemberState> Values, int Keys, int Unfilterable, string Filter, string? Query);

/// <summary>
/// A search composed from values picked across dimensions: the kind to search, the query (each dimension's own query once,
/// then each dimension's filter, joined with AND), each part, how many clauses the query holds, and what the picks left out.
/// </summary>
public sealed record DimensionSearchSet(
    string Kind, string Query, IReadOnlyList<DimensionSearchPart> Parts, int Clauses, IReadOnlyList<string> Removed, IReadOnlyList<string> Missing,
    IReadOnlyList<string> Notes);

/// <summary>
/// Composes the OSDU search that finds the records holding the values picked across a kind's dimensions (docs/dimension-plan.md,
/// The search): within a dimension, a record holding any key of the values picked; across dimensions, a record matching every
/// dimension; within each dimension's own query, the records its values were read from. The API and the CLI compose through
/// here, so a search is the same text wherever it comes from.
/// </summary>
public static class DimensionSearch
{
    /// <summary>
    /// The most clauses a composed query holds: the service allows 1024 in a query (the search API document), and a few are
    /// kept for the dimensions' own queries and one a caller adds.
    /// </summary>
    public const int MaxClauses = 1000;

    /// <summary>The most dimensions one search combines.</summary>
    public const int MaxDimensions = 20;

    /// <summary>
    /// The search finding the records that hold one of the values picked in each dimension of <paramref name="picks"/>, in
    /// <paramref name="kind"/> when given (each dimension's kind has to cover it), else in the one kind every dimension reads;
    /// <paramref name="within"/> narrows it further.
    /// </summary>
    /// <exception cref="DeliveryException">
    /// Nothing is picked, the dimensions read different kinds and none is given, a dimension's kind does not cover the kind
    /// given, a dimension has no build, a pick holds no key a query can carry, or the query would hold more than
    /// <see cref="MaxClauses"/> clauses.
    /// </exception>
    public static async Task<DimensionSearchSet> ComposeAsync(
        ILedger ledger, IReadOnlyList<DimensionPick> picks, string? kind, string? within, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(picks);
        var picked = picks.Where(p => p.ValueIds.Count + p.Values.Count > 0).ToList();
        if (picked.Count == 0)
        {
            throw new DeliveryException("A search is composed from at least one value picked in a dimension.");
        }

        if (picked.Count > MaxDimensions)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"A search combines at most {MaxDimensions} dimensions, and {picked.Count} were picked in."));
        }

        if (picked.Select(p => p.Dimension.DimensionId).Distinct().Count() != picked.Count)
        {
            throw new DeliveryException("A dimension is picked in once; name all its values in one pick.");
        }

        var searched = KindOf(picked, kind);
        var parts = new List<DimensionSearchPart>(picked.Count);
        var removed = new List<string>();
        var missing = new List<string>();
        var notes = new List<string>();
        foreach (var pick in picked)
        {
            var set = await DimensionFilters.ForMembersAsync(ledger, pick.Dimension, pick.ValueIds, pick.Values, ct).ConfigureAwait(false);
            removed.AddRange(set.Removed.Select(v => $"{pick.Dimension.Name}: {v}"));
            missing.AddRange(set.Missing.Select(v => $"{pick.Dimension.Name}: {v}"));
            if (set.Filters.Count == 0)
            {
                throw new DeliveryException(
                    $"Nothing picked in dimension {pick.Dimension.Name} can be searched for: {(set.Members.Count == 0 ? "no value picked is one it holds now" : "no key of the values picked can be carried in a query")}.");
            }

            if (set.Unfilterable > 0)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{pick.Dimension.Name}: {set.Unfilterable} key(s) of the values picked cannot be carried in a query, so the search does not find the records holding them."));
            }

            var filter = set.Filters.Count == 1 ? set.Filters[0] : "(" + string.Join(" OR ", set.Filters.Select(f => $"({f})")) + ")";
            parts.Add(new DimensionSearchPart(
                pick.Dimension.DimensionId, pick.Dimension.Name, set.AggregateBy, set.Members, set.Originals, set.Unfilterable, filter,
                pick.Dimension.Query));
        }

        // Each dimension's own query once, since dimensions of one flow often read the same records; then every filter.
        var scopes = parts.Select(p => p.Query).Where(q => !string.IsNullOrWhiteSpace(q) && q!.Trim() != "*").Select(q => q!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var clauses = parts.Sum(p => p.Keys) + scopes.Count + (string.IsNullOrWhiteSpace(within) ? 0 : 1);
        if (clauses > MaxClauses)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"The values picked hold {parts.Sum(p => p.Keys)} keys, and one search holds at most {MaxClauses} clauses (the service allows 1024). Pick fewer values, or search each dimension's values on their own."));
        }

        var terms = scopes.Concat(parts.Select(p => p.Filter)).ToList();
        if (!string.IsNullOrWhiteSpace(within))
        {
            terms.Add(within.Trim());
        }

        // Every term is grouped when there are several, so an OR inside one never reaches across the AND between them.
        var query = terms.Count == 1 ? terms[0] : string.Join(" AND ", terms.Select(t => $"({t})"));
        return new DimensionSearchSet(searched, query, parts, clauses, removed, missing, notes);
    }

    /// <summary>
    /// The kind a composed search reads: the one given, which every dimension's kind has to cover, or the one kind every
    /// dimension reads.
    /// </summary>
    private static string KindOf(IReadOnlyList<DimensionPick> picks, string? kind)
    {
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var wanted = kind.Trim();
            if (!OsduKind.IsValid(wanted))
            {
                throw new DeliveryException($"'{wanted}' is not a kind: authority:source:entityType:version, wildcards allowed per segment.");
            }

            var outside = picks.Where(p => !Covers(p.Dimension.Kind, wanted)).Select(p => $"{p.Dimension.Name} ({p.Dimension.Kind})").ToList();
            return outside.Count == 0
                ? wanted
                : throw new DeliveryException(
                    $"The search reads {wanted}, and {string.Join(", ", outside)} read{(outside.Count == 1 ? "s" : string.Empty)} other records: a dimension's values filter only the kind its keys were read from.");
        }

        var kinds = picks.Select(p => p.Dimension.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return kinds.Count == 1
            ? kinds[0]
            : throw new DeliveryException(
                $"The dimensions picked in read different kinds ({string.Join(", ", kinds)}); a search reads one. Name the kind to search, which each dimension's kind has to cover.");
    }

    /// <summary>Whether a dimension reading <paramref name="pattern"/> covers <paramref name="kind"/>: each segment the same, or a wildcard.</summary>
    internal static bool Covers(string pattern, string kind)
    {
        var have = pattern.Split(':');
        var want = kind.Split(':');
        return have.Length == 4 && want.Length == 4
            && have.Zip(want).All(s => s.First == "*" || string.Equals(s.First, s.Second, StringComparison.OrdinalIgnoreCase));
    }
}
