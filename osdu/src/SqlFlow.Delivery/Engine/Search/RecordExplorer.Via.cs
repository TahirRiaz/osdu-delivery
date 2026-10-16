using System.Globalization;
using System.Text.Json;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>
/// The conditions on search terms that reach a page's records through other records (<see cref="ExplorerVia"/>), read from
/// the platform before the page is asked: the records a value finds, or the record ids a key makes. Each condition is then
/// one the page asks of its own records, and a note says what was read for it.
/// </summary>
public sealed partial class RecordExplorer
{
    /// <summary>What each condition read through other records came to, by the condition and the kind asked, so the pages of one search read it once.</summary>
    private readonly Dictionary<string, (ExplorerFilter? Filter, string Note)> _throughOthers = new(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="search"/> with every condition read through other records replaced by the condition it comes to on
    /// the page's own records, and a note for each in <paramref name="notes"/>: what was read, and how many records it
    /// found. A condition whose value finds no record matches no record (or, excluding, every record).
    /// </summary>
    /// <exception cref="DeliveryException">The platform refused to find the records, or the partition an id is made in is not known.</exception>
    internal async Task<ExplorerSearch> ThroughOthersAsync(ExplorerSearch search, List<string> notes, CancellationToken ct)
    {
        if (search.Filters.All(f => f.Via is null))
        {
            return search;
        }

        var filters = new List<ExplorerFilter>(search.Filters.Count);
        foreach (var filter in search.Filters)
        {
            if (filter.Via is not { } via)
            {
                filters.Add(filter);
                continue;
            }

            var asked = JsonSerializer.Serialize(filter, ExplorerSearch.WireOptions);
            if (!_throughOthers.TryGetValue(asked, out var read))
            {
                read = via.Key is { } key ? await ByKeyAsync(filter, via, key, ct).ConfigureAwait(false) : await ByRecordsAsync(filter, via, ct).ConfigureAwait(false);
                _throughOthers[asked] = read;
            }

            notes.Add(read.Note);
            if (read.Filter is { } resolved)
            {
                filters.Add(resolved);
            }
        }

        return search with { Filters = filters };
    }

    /// <summary>A condition read through the records its values find: those records' ids, or the property read of them.</summary>
    private async Task<(ExplorerFilter? Filter, string Note)> ByRecordsAsync(ExplorerFilter filter, ExplorerVia via, CancellationToken ct)
    {
        var term = via.Term ?? filter.Path;
        var query = via.Matching(filter).Text;
        IReadOnlyList<string> returned = via.ReadsId ? ["id"] : ["id", via.Read!.Path];
        var answer = await _search.PageAsync(new OsduSearchQuery { Kind = via.Kind!, Query = query, ReturnedFields = returned }, 0, ExplorerVia.MaxFound, null, ct).ConfigureAwait(false);
        if (answer.Refusal is { } refusal)
        {
            throw new DeliveryException($"The search service refused to find the records {term} names ({via.Kind}, {query}): {refusal}");
        }

        var found = new List<string>();
        foreach (var hit in answer.Hits)
        {
            IEnumerable<string> values = via.ReadsId
                ? Text(hit, "id") is { Length: > 0 } id ? [TargetId.WithoutVersion(id) + ":"] : []
                : ValuesAt(hit, via.Read!.Path);
            foreach (var value in values)
            {
                if (found.Count < ExplorerVia.MaxFound && !found.Contains(value, StringComparer.Ordinal))
                {
                    found.Add(value);
                }
            }
        }

        var records = Counted(answer.Total, TypeOf(via.Kind!) + " record");
        var asked = $"{term} {Spoken(filter)}";
        var keeps = !filter.Excludes;
        if (found.Count == 0)
        {
            return keeps
                ? (Nothing, $"{asked}: no {TypeOf(via.Kind!)} record holds it ({query}), so no record is found by it.")
                : (null, $"{asked}: no {TypeOf(via.Kind!)} record holds it ({query}), so it leaves every record.");
        }

        var cut = answer.Total > ExplorerVia.MaxFound ? $"; the first {ExplorerVia.MaxFound} are compared, so narrow the value to reach the others" : string.Empty;
        var compared = new ExplorerFilter
        {
            Path = filter.Path,
            Index = filter.Index,
            Nested = filter.Nested,
            Condition = keeps ? ExplorerCondition.AnyOf : ExplorerCondition.NoneOf,
            Values = found,
        };
        return (compared, $"{asked}: {records} found by {query}, compared by {(via.ReadsId ? "id" : via.Read!.Path)} with {filter.Path}{cut}.");
    }

    /// <summary>
    /// A condition on a column of the dataset's key: the ids the key makes with every value of its other columns the
    /// platform holds, read by one grouping each, as the delivery makes a record's id.
    /// </summary>
    private async Task<(ExplorerFilter? Filter, string Note)> ByKeyAsync(ExplorerFilter filter, ExplorerVia via, ExplorerViaKey key, CancellationToken ct)
    {
        var partition = _partition ?? throw new DeliveryException("The partition the explorer reads is not known, so no record id can be made from a key.");
        var term = via.Term ?? key.Given;
        var kind = $"*:*:{key.EntityType}:*";
        var combinations = filter.Compared()
            .Select(v => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key.Given] = v.Trim() })
            .ToList();
        var read = new List<string>();
        var cut = false;
        foreach (var column in key.Columns.Where(c => !string.Equals(c, key.Given, StringComparison.OrdinalIgnoreCase)))
        {
            var other = key.Others.First(o => string.Equals(o.Column, column, StringComparison.OrdinalIgnoreCase));
            var field = OsduField.Of(other.Path, other.Index);
            var grouped = await _search.PageAsync(new OsduSearchQuery { Kind = kind, ReturnedFields = ["id"] }, 0, 1, field.AggregateBy, ct).ConfigureAwait(false);
            if (grouped.Refusal is { } refusal)
            {
                throw new DeliveryException($"The search service refused to group {kind} by {other.Path}, which the key's column {column} is read from: {refusal}");
            }

            var held = grouped.Buckets.Where(b => !string.IsNullOrEmpty(b.Key)).Select(b => b.Key!).ToList();
            read.Add($"{held.Count} {(held.Count == 1 ? "value" : "values")} of {column} ({other.Path})");
            combinations = combinations
                .SelectMany(c => held.Select(v => new Dictionary<string, string>(c, StringComparer.OrdinalIgnoreCase) { [column] = v }))
                .ToList();
            if (combinations.Count > ExplorerVia.MaxFound)
            {
                combinations = combinations.Take(ExplorerVia.MaxFound).ToList();
                cut = true;
            }
        }

        // Every combination's id, as each source system's delivery makes it (a key's own values make one id whatever the system).
        var ids = new List<string>();
        foreach (var combination in combinations)
        {
            IReadOnlyList<string?> values = key.Columns.Select(c => (string?)combination[c]).ToList();
            IEnumerable<string?> made = key.FromKey
                ? [TargetId.ComposeFromKey(partition, key.EntityType, values, out _)]
                : key.Systems.Select(system => (string?)TargetId.Compose(partition, key.EntityType, DeliveryKey.Derive(system, values)));
            foreach (var id in made)
            {
                if (id is null || ids.Contains(id, StringComparer.Ordinal))
                {
                    continue;
                }

                if (ids.Count == ExplorerVia.MaxFound)
                {
                    cut = true;
                    break;
                }

                ids.Add(id);
            }
        }

        var asked = $"{term} {Spoken(filter)}";
        if (ids.Count == 0)
        {
            return filter.Excludes
                ? (null, $"{asked}: no record id can be made from it, so it leaves every record.")
                : (Nothing, $"{asked}: no record id can be made from it, so no record is found by it.");
        }

        var from = read.Count == 0 ? string.Empty : $" with {string.Join(" and ", read)}";
        var compared = new ExplorerFilter
        {
            Path = "id",
            Index = OsduFieldIndex.Keyword,
            Condition = filter.Excludes ? ExplorerCondition.NoneOf : ExplorerCondition.AnyOf,
            Values = ids,
        };
        return (compared, $"{asked}: {Counted(ids.Count, "record id")} made from {string.Join(" and ", key.Columns)}{from}{(cut ? $"; the first {ExplorerVia.MaxFound} are compared" : string.Empty)}.");
    }

    /// <summary>The condition no record meets: one without an id, of which there is none.</summary>
    private static ExplorerFilter Nothing => new() { Path = "id", Index = OsduFieldIndex.Keyword, Condition = ExplorerCondition.Missing };

    /// <summary>A condition as a note says it after the term: <c>is Wellbore A-1</c>, <c>is one of A, B</c>.</summary>
    private static string Spoken(ExplorerFilter filter)
    {
        var values = filter.Compared();
        var shown = string.Join(", ", values.Take(3)) + (values.Count > 3 ? ", ..." : string.Empty);
        return filter.Condition switch
        {
            ExplorerCondition.IsNot => $"is not {shown}",
            ExplorerCondition.AnyOf => $"is one of {shown}",
            ExplorerCondition.NoneOf => $"is none of {shown}",
            ExplorerCondition.Contains => $"contains {shown}",
            ExplorerCondition.StartsWith => $"starts with {shown}",
            _ => $"is {shown}",
        };
    }

    /// <summary>The type a kind pattern names (<c>Wellbore</c> of <c>osdu:wks:master-data--Wellbore:*</c>), or the pattern.</summary>
    private static string TypeOf(string kind)
        => ExplorerKinds.EntityTypeOf(kind) is { } entityType && entityType.IndexOf("--", StringComparison.Ordinal) is var dashes and >= 0
            ? entityType[(dashes + 2)..]
            : kind;

    private static string Counted(long count, string noun)
        => string.Create(CultureInfo.InvariantCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");
}
