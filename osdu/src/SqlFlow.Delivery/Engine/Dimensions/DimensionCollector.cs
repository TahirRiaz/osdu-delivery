using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>What a build collected of one attribute from its dimension's own records.</summary>
/// <param name="Keys">
/// Each key's values, as its rows keep them: the value as shown, the text it was collected as (the first, ordinally, of
/// several shown alike), and how many of the key's records hold it; the value for what is not read, with the records holding
/// none, when the dimension names one.
/// </param>
/// <param name="Texts">Every text the records hold that a pick can find, with the value it is shown as and the records holding it.</param>
/// <param name="Passed">Whether the records were read in one pass of the search cursor, rather than with a read per value.</param>
/// <param name="Aggregations">Aggregations asked.</param>
/// <param name="ScanPages">Cursor pages read.</param>
/// <param name="Notes">What the read has to say, a line each.</param>
internal sealed record CollectedAttribute(
    IReadOnlyDictionary<string, List<DimensionAttributeState>> Keys, IReadOnlyList<DimensionCollectedText> Texts, bool Passed, int Aggregations, int ScanPages,
    IReadOnlyList<string> Notes);

/// <summary>
/// Collects an attribute from a dimension's own records (docs/dimension-plan.md, Collected attributes): every value the
/// records holding each key hold at the attribute's path, with how many hold it, and, when the dimension names a value for
/// what is not read, how many hold none of them. The attribute's distinct values are read first, as any field's are, so
/// however many there are the read pages; a value is shown as a label is (a reference by the code its id ends with,
/// trimmed), and a text no query can carry, or one shown as nothing, is no value and counts as none.
/// </summary>
/// <remarks>
/// The keys are then read the cheaper of two ways, both exact. With few values (one query can exclude them all) and fewer
/// reads than one pass would take pages, a distinct read of the dimension's own field per value, narrowed to the records
/// holding it, and one more narrowed to those holding none. Otherwise one pass of the search cursor over the dimension's
/// records, each record's key and value read together, which costs a page per thousand records however many values there
/// are. A key the dimension's own read did not find is passed over.
/// </remarks>
internal sealed class DimensionCollector(OsduSearch search, ILogger log, int aggregationSize, int concurrency)
{
    /// <summary>Collects <paramref name="attribute"/>, held at <paramref name="field"/>, for the keys of <paramref name="keyField"/> that <paramref name="keys"/> found.</summary>
    /// <exception cref="DeliveryException">The records hold more distinct values at the path than the dimension keeps (its maxValues).</exception>
    public async Task<CollectedAttribute> CollectAsync(
        DimensionSpec dimension, DimensionAttributeSpec attribute, string? query, OsduField keyField, bool keyRepeats, OsduField field, bool repeats,
        DistinctRead keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(keyField);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(keys);
        DistinctRead texts;
        try
        {
            texts = await DistinctValues.ReadAsync(
                new SearchDistinctSource(search, dimension.Kind, query, field),
                new DistinctReadOptions(aggregationSize, dimension.MaxValues, repeats, Checks: false),
                log, ct).ConfigureAwait(false);
        }
        catch (DimensionTooLargeException ex)
        {
            throw new DeliveryException($"Attribute {attribute.Name} of dimension {dimension.Name} collects {attribute.Collect}: {ex.Message}", ex);
        }

        // Each text as it is shown; a text no query can carry is no value a pick could find, so it is left out with a note,
        // and its records count as holding none, as a search for none of the values finds them.
        var shown = new Dictionary<string, string>(StringComparer.Ordinal);
        var unfilterable = new List<string>();
        foreach (var text in texts.Values.Keys.Order(StringComparer.Ordinal))
        {
            if (!DimensionFilters.Filterable(field, text))
            {
                unfilterable.Add(text);
                continue;
            }

            var value = DimensionLabeler.DisplayOf(text).Trim();
            if (value.Length > DimensionSpec.MaxAttributeValueLength)
            {
                value = value[..DimensionSpec.MaxAttributeValueLength];
            }

            if (value.Length > 0)
            {
                shown[text] = value;
            }
        }

        var notes = new List<string>();
        if (unfilterable.Count > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{attribute.Name}: {unfilterable.Count} value(s) the records hold cannot be carried in a query, so no pick finds them and their records hold none: {string.Join(", ", unfilterable.Take(5).Select(t => $"'{(t.Length > 60 ? t[..60] + "..." : t)}'"))}{(unfilterable.Count > 5 ? ", ..." : string.Empty)}."));
        }

        var tally = new KeyTally(keys);
        var values = shown.Values.Distinct(StringComparer.Ordinal).Count();
        var records = keys.Records ?? keys.Values.Values.Sum();
        var passPages = (records + OsduSearch.MaxPage - 1) / OsduSearch.MaxPage;
        var reads = values + (dimension.Unlabelled is null ? 0 : 1);
        var byValue = shown.Count <= DimensionFilters.MaxOriginalsPerQuery && reads < passPages;
        var (aggregations, pages) = byValue
            ? await ByValueAsync(dimension, query, keyField, keyRepeats, field, shown, tally, ct).ConfigureAwait(false)
            : await InOnePassAsync(dimension, query, keyField, field, shown, tally, ct).ConfigureAwait(false);
        log.LogInformation(
            "dimension {Dimension}: attribute {Attribute} collected {Values} value(s) from {Path} for {Keys} key(s), {How}",
            dimension.Name, attribute.Name, values, attribute.Collect, tally.Count,
            byValue
                ? string.Create(CultureInfo.InvariantCulture, $"read per value in {aggregations} aggregation(s)")
                : string.Create(CultureInfo.InvariantCulture, $"read in one pass of {pages} page(s)"));

        var collected = shown
            .Select(s => new DimensionCollectedText(attribute.Name, s.Key, s.Value, texts.Values[s.Key]))
            .OrderBy(t => t.Value, StringComparer.Ordinal).ThenBy(t => t.Text, StringComparer.Ordinal)
            .ToList();
        return new CollectedAttribute(
            tally.Rows(attribute.Name), collected, !byValue, texts.Aggregations + aggregations, texts.ScanPages + pages, notes);
    }

    /// <summary>
    /// The keys of each value read by a distinct read of the dimension's field over the records holding one of its texts, in
    /// parallel; and those of the records holding none, when the dimension names a value for them.
    /// </summary>
    private async Task<(int Aggregations, int Pages)> ByValueAsync(
        DimensionSpec dimension, string? query, OsduField keyField, bool keyRepeats, OsduField field, IReadOnlyDictionary<string, string> shown, KeyTally tally,
        CancellationToken ct)
    {
        var options = new DistinctReadOptions(aggregationSize, dimension.MaxValues, keyRepeats, Checks: false);
        var aggregations = 0;
        var pages = 0;
        var gate = new Lock();
        var groups = shown.GroupBy(s => s.Value, s => s.Key, StringComparer.Ordinal).ToList();
        await Parallel.ForEachAsync(
            groups,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct },
            async (group, token) =>
            {
                var texts = group.Order(StringComparer.Ordinal).ToList();
                var filter = string.Join(" OR ", DimensionFilters.Of(field, texts).Select(q => $"({q})"));
                var holding = await DistinctValues.ReadAsync(
                    new SearchDistinctSource(search, dimension.Kind, DimensionFilters.Within(query, filter), keyField), options, log, token).ConfigureAwait(false);
                lock (gate)
                {
                    aggregations += holding.Aggregations;
                    pages += holding.ScanPages;
                    foreach (var (key, count) in holding.Values)
                    {
                        tally.Add(key, group.Key, texts[0], count);
                    }
                }
            }).ConfigureAwait(false);

        if (dimension.Unlabelled is { } none)
        {
            if (shown.Count == 0)
            {
                // No value at all: every record of every key holds none.
                foreach (var (key, count) in tally.Found)
                {
                    tally.Add(key, none, null, count);
                }
            }
            else
            {
                var lacking = await DistinctValues.ReadAsync(
                    new SearchDistinctSource(search, dimension.Kind, DimensionFilters.Within(query, DimensionFilters.NoneOf(field, shown.Keys.ToList())), keyField),
                    options, log, ct).ConfigureAwait(false);
                aggregations += lacking.Aggregations;
                pages += lacking.ScanPages;
                foreach (var (key, count) in lacking.Values)
                {
                    tally.Add(key, none, null, count);
                }
            }
        }

        return (aggregations, pages);
    }

    /// <summary>
    /// Every record of the dimension read once through the search cursor, its key and its value together: each value it holds
    /// counted once under each key it holds, or, holding none, under the value for what is not read when the dimension names one.
    /// </summary>
    private async Task<(int Aggregations, int Pages)> InOnePassAsync(
        DimensionSpec dimension, string? query, OsduField keyField, OsduField field, IReadOnlyDictionary<string, string> shown, KeyTally tally, CancellationToken ct)
    {
        var request = new OsduSearchQuery
        {
            Kind = dimension.Kind,
            Query = string.IsNullOrWhiteSpace(query) || query.Trim() == "*" ? null : query.Trim(),
            ReturnedFields = new[] { "id", keyField.Path, field.Path }.Distinct(StringComparer.Ordinal).ToList(),
        };
        var pages = 0;

        // A record the cursor hands back twice is the same record: counted once, as an aggregation counts it.
        await foreach (var page in search.PagesAsync(request, OsduSearch.MaxPage, deduplicate: true, ct).ConfigureAwait(false))
        {
            pages++;
            foreach (var hit in page.Hits)
            {
                var held = TextsOf(hit, keyField).Where(tally.Holds).ToList();
                if (held.Count == 0)
                {
                    continue;
                }

                // A value counted once a record however many of its texts the record holds.
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var text in TextsOf(hit, field).Order(StringComparer.Ordinal))
                {
                    if (shown.TryGetValue(text, out var value))
                    {
                        values.TryAdd(value, text);
                    }
                }

                foreach (var key in held)
                {
                    if (values.Count == 0)
                    {
                        if (dimension.Unlabelled is { } none)
                        {
                            tally.Add(key, none, null, 1);
                        }

                        continue;
                    }

                    foreach (var (value, text) in values)
                    {
                        tally.Add(key, value, text, 1);
                    }
                }
            }
        }

        return (0, pages);
    }

    /// <summary>
    /// The texts a hit holds at <paramref name="field"/>, each once, in the text the read keeps a value under: a text longer
    /// than the keyword keeps is no value, as no aggregation or exact match reaches it.
    /// </summary>
    private static HashSet<string> TextsOf(JsonElement hit, OsduField field)
    {
        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in SearchDistinctSource.Units(hit, field))
        {
            foreach (var value in unit.Values)
            {
                var (reading, text) = DimensionValueText.FromScan(field.Index, value);
                if (reading == ValueReading.Value && (field.Index != OsduFieldIndex.Text || text.Length <= OsduQuery.KeywordIgnoreAbove))
                {
                    texts.Add(text);
                }
            }
        }

        return texts;
    }

    /// <summary>Each key's values as they are counted: the records holding each, and the first text it was collected as.</summary>
    private sealed class KeyTally(DistinctRead keys)
    {
        private readonly Dictionary<string, Dictionary<string, (string? From, long Records)>> _keys = new(StringComparer.Ordinal);

        /// <summary>The keys the dimension's own read found, with their counts.</summary>
        public IReadOnlyDictionary<string, long> Found => keys.Values;

        /// <summary>The keys counted under a value so far.</summary>
        public int Count => _keys.Count;

        /// <summary>Whether the dimension's own read found <paramref name="key"/>.</summary>
        public bool Holds(string key) => keys.Values.ContainsKey(key);

        public void Add(string key, string value, string? from, long records)
        {
            if (!Holds(key))
            {
                return;
            }

            var values = _keys.TryGetValue(key, out var found) ? found : _keys[key] = new Dictionary<string, (string?, long)>(StringComparer.Ordinal);
            if (!values.TryGetValue(value, out var held))
            {
                values[value] = (from, records);
                return;
            }

            // Several texts shown alike: the row names the first of them, ordinally.
            var first = held.From is null || (from is not null && string.CompareOrdinal(from, held.From) < 0) ? from : held.From;
            values[value] = (first, held.Records + records);
        }

        /// <summary>Each key's values as rows under <paramref name="name"/>: the most records first, then by value.</summary>
        public IReadOnlyDictionary<string, List<DimensionAttributeState>> Rows(string name)
            => _keys.ToDictionary(
                k => k.Key,
                k => k.Value
                    .OrderByDescending(v => v.Value.Records).ThenBy(v => v.Key, StringComparer.Ordinal)
                    .Select(v => new DimensionAttributeState(name, v.Key, v.Value.From, v.Value.Records))
                    .ToList(),
                StringComparer.Ordinal);
    }
}
