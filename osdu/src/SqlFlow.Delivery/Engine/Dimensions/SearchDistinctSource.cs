using System.Runtime.CompilerServices;
using System.Text.Json;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// A dimension's values as the OSDU search service holds them (openapi search v2): the groups of its field over a slice
/// (<c>POST /query</c> with <c>aggregateBy</c>), the records of a slice read through the cursor with nothing but the field
/// returned (<c>POST /query_with_cursor</c>), and the counts that say how complete a read is (<c>POST /query</c> with
/// <c>trackTotalCount</c>). A slice is the dimension's own query with the slice's range added, so every request asks of the
/// records the dimension reads and nothing else.
/// </summary>
public sealed class SearchDistinctSource : IDistinctValueSource
{
    private readonly OsduSearch _search;
    private readonly string _kind;
    private readonly string? _query;

    /// <param name="search">The partition's search service.</param>
    /// <param name="kind">The kind the dimension reads, wildcards allowed per segment.</param>
    /// <param name="query">The dimension's own query, or null (or <c>*</c>) for every record of the kind.</param>
    /// <param name="field">The field read, as the index stores it.</param>
    public SearchDistinctSource(OsduSearch search, string kind, string? query, OsduField field)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(field);
        _search = search;
        _kind = kind;
        _query = string.IsNullOrWhiteSpace(query) || query.Trim() == "*" ? null : query.Trim();
        Field = field;
    }

    public OsduField Field { get; }

    public async Task<(long Total, IReadOnlyList<OsduSearchBucket> Buckets)> AggregateAsync(DistinctSlice slice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(slice);
        return await _search.AggregateAsync(Query(slice, extra: null), Field.AggregateBy, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<IReadOnlyList<DistinctUnit>> ScanAsync(DistinctSlice slice, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(slice);
        var query = Query(slice, extra: null) with { ReturnedFields = ["id", Field.Path] };

        // The reader hands each record out once, so a record the cursor returns twice is counted once, as the aggregation
        // counts it; and it hands out every record of the slice or fails the read, so no slice is counted short.
        await foreach (var page in _search.PagesAsync(query, OsduSearch.MaxPage, ct).ConfigureAwait(false))
        {
            var units = new List<DistinctUnit>(page.Hits.Count);
            foreach (var hit in page.Hits)
            {
                units.AddRange(Units(hit, Field));
            }

            yield return units;
        }
    }

    public async Task<long?> CountAsync(DistinctCheck check, CancellationToken ct)
    {
        string? extra;
        switch (check)
        {
            case DistinctCheck.Records:
                extra = null;
                break;
            case DistinctCheck.WithValue when Field.NestedPath is null:
                extra = $"_exists_:{Field.ExactPath}";
                break;
            case DistinctCheck.TooLong when Field.NestedPath is null && Field.Index == OsduFieldIndex.Text:
                // The analysed field holds a value of any length; the keyword sub-field none longer than 256 characters.
                extra = $"_exists_:{Field.Path} AND NOT _exists_:{Field.ExactPath}";
                break;
            default:
                // Inside a nested array the service prefixes the property it finds by pattern, which _exists_ is not; the
                // count is not asked rather than asked wrongly.
                return null;
        }

        return await _search.CountAsync(Query(DistinctSlice.Whole, extra) with { ReturnedFields = ["id"] }, ct).ConfigureAwait(false);
    }

    /// <summary>The search a slice asks: the dimension's query, the slice's range and any extra clause, each grouped.</summary>
    private OsduSearchQuery Query(DistinctSlice slice, string? extra)
    {
        var parts = new List<string>(3);
        if (_query is not null)
        {
            parts.Add(_query);
        }

        if (!slice.IsWhole)
        {
            parts.Add(OsduQuery.Range(Field, slice.From, slice.To).Text);
        }

        if (extra is not null)
        {
            parts.Add(extra);
        }

        var text = parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => string.Join(" AND ", parts.Select(p => $"({p})")),
        };
        return new OsduSearchQuery { Kind = _kind, Query = text, ReturnedFields = ["id"] };
    }

    /// <summary>
    /// What one hit holds at the field: one unit holding every value on the path, or for a property of a nested array one
    /// unit per object of the array, each holding the values under it. A path crosses arrays wherever it meets one, as the
    /// index does.
    /// </summary>
    public static IEnumerable<DistinctUnit> Units(JsonElement hit, OsduField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.NestedPath is not { } nested)
        {
            var values = new List<ScannedValue>();
            Collect(hit, field.Path.Split('.'), 0, values);
            if (values.Count > 0)
            {
                yield return new DistinctUnit(values);
            }

            yield break;
        }

        var objects = new List<JsonElement>();
        Reach(hit, nested.Split('.'), 0, objects);
        var inside = field.QueryPath.Split('.');
        foreach (var element in objects)
        {
            var values = new List<ScannedValue>();
            Collect(element, inside, 0, values);
            if (values.Count > 0)
            {
                yield return new DistinctUnit(values);
            }
        }
    }

    /// <summary>Every object the path reaches, an array on the way stepped into, an array at the end taken element by element.</summary>
    private static void Reach(JsonElement node, string[] path, int at, List<JsonElement> into)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                Reach(item, path, at, into);
            }

            return;
        }

        if (at == path.Length)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                into.Add(node);
            }

            return;
        }

        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(path[at], out var child))
        {
            Reach(child, path, at + 1, into);
        }
    }

    /// <summary>Every value the path reaches, an array on the way or at the end stepped into.</summary>
    private static void Collect(JsonElement node, string[] path, int at, List<ScannedValue> into)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                Collect(item, path, at, into);
            }

            return;
        }

        if (at == path.Length)
        {
            into.Add(node.ValueKind switch
            {
                JsonValueKind.String => ScannedValue.OfString(node.GetString()!),
                JsonValueKind.Number => ScannedValue.OfNumber(node.GetRawText()),
                JsonValueKind.True => ScannedValue.OfBoolean(true),
                JsonValueKind.False => ScannedValue.OfBoolean(false),
                JsonValueKind.Null => ScannedValue.Null,
                _ => ScannedValue.Composite,
            });
            return;
        }

        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(path[at], out var child))
        {
            Collect(child, path, at + 1, into);
        }
    }
}
