using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// What a pass over a dimension's records read of them: each record identified, by the SHA-256 of its unique key
/// (<see cref="DimensionRecordReader.RecordHash"/>); each key each such record holds; the records read; those holding no
/// single value at a key column, which cannot be told apart and are left out; and the pages read.
/// </summary>
internal sealed record RecordRead(
    IReadOnlyList<byte[]> Records, IReadOnlyList<(byte[] Record, string Original)> Held, long Read, long Unidentified, int Pages, int Ranges);

/// <summary>
/// Reads a dimension's records through the search cursor, each record's unique key (the flow's
/// <c>incremental.keyColumns</c>) and the keys it holds together, for its incremental loads: a full load reads every record,
/// cut into ranges of the dimension's keys read side by side as a collected attribute's pass is
/// (<see cref="DimensionCollector.Ranges"/>), and keeps the keys each holds; an incremental load reads the records that
/// changed in its window, each key each holds, and looks up what they held before by their unique keys. A record holding
/// keys of two ranges is read in both and keeps the keys of each range from each, so every pair is read once.
/// </summary>
internal sealed class DimensionRecordReader(OsduSearch search, ILogger log, int concurrency)
{
    /// <summary>The most keys a full load keeps by record between all a dimension's records: past it, the dimension is loaded in full only.</summary>
    public const long MaxHeld = 20_000_000;

    /// <summary>What separates the values of a unique key of several columns before it is hashed: a character no id holds.</summary>
    private const char Separator = '\u001F';

    /// <summary>The SHA-256 a record is kept by: of its key columns' values, in order, joined.</summary>
    public static byte[] RecordHash(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return SqlServerDimensionStore.HashOf(string.Join(Separator, values));
    }

    /// <summary>
    /// Every record of every scope, found by the scope's query and cut into ranges of its keys, with the keys it holds among
    /// the scope's: what a full load keeps by record.
    /// </summary>
    /// <exception cref="DeliveryException">The records hold more keys between them than a full load keeps by record.</exception>
    public Task<RecordRead> ReadAsync(DimensionSpec dimension, IReadOnlyList<DimensionScope> scopes, OsduField keyField, IReadOnlyList<OsduField> keyColumns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(keyField);
        var order = DimensionValueText.Order(keyField.Index);
        var work = scopes
            .SelectMany(scope => DimensionCollector.Ranges(keyField, scope.Keys.Values, order, Math.Max(1, concurrency))
                .Select(range => new Pass(Own(scope.Query), range, key => scope.Keys.Values.ContainsKey(key) && range.Contains(key, order))))
            .ToList();
        return ReadAsync(dimension, work, keyField, keyColumns, ct);
    }

    /// <summary>Every record <paramref name="query"/> finds, with every key it holds: the records an incremental load found changed.</summary>
    /// <exception cref="DeliveryException">The records hold more keys between them than a load keeps by record.</exception>
    public Task<RecordRead> ReadAllAsync(DimensionSpec dimension, string query, OsduField keyField, IReadOnlyList<OsduField> keyColumns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(keyField);
        return ReadAsync(dimension, [new Pass(Own(query), DistinctSlice.Whole, _ => true)], keyField, keyColumns, ct);
    }

    private async Task<RecordRead> ReadAsync(DimensionSpec dimension, IReadOnlyList<Pass> work, OsduField keyField, IReadOnlyList<OsduField> keyColumns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keyColumns);
        if (keyColumns.Count == 0)
        {
            throw new ArgumentException("A record is identified by at least one key column.", nameof(keyColumns));
        }

        var returned = keyColumns.Select(c => c.Path).Append(keyField.Path).Distinct(StringComparer.Ordinal).ToList();
        var records = new List<byte[]>();
        var held = new List<(byte[] Record, string Original)>();
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
        long read = 0;
        long unidentified = 0;
        var pages = 0;
        var gate = new Lock();
        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct },
            async (pass, token) =>
            {
                var request = new OsduSearchQuery
                {
                    Kind = dimension.Kind,
                    Query = pass.Range.IsWhole ? pass.Query : DimensionFilters.Within(pass.Query, OsduQuery.Range(keyField, pass.Range.From, pass.Range.To).Text),
                    ReturnedFields = returned,
                };

                // The reader hands each record out once and every record of the range, or fails the load.
                await foreach (var page in search.PagesAsync(request, OsduSearch.MaxPage, token).ConfigureAwait(false))
                {
                    var pageRecords = new List<byte[]>(page.Hits.Count);
                    var pageHeld = new List<(byte[] Record, string Original)>();
                    var pageUnidentified = 0;
                    foreach (var hit in page.Hits)
                    {
                        if (Identity(hit, keyColumns) is not { } identity)
                        {
                            pageUnidentified++;
                            continue;
                        }

                        var hash = RecordHash(identity);
                        pageRecords.Add(hash);
                        foreach (var key in DimensionCollector.TextsOf(hit, keyField).Where(pass.Keeps))
                        {
                            pageHeld.Add((hash, key));
                        }
                    }

                    // A page is kept in one go, under the lock the passes share; a key is held once however many records hold it.
                    lock (gate)
                    {
                        pages++;
                        read += page.Hits.Count;
                        unidentified += pageUnidentified;
                        records.AddRange(pageRecords);
                        foreach (var (hash, key) in pageHeld)
                        {
                            if (!canonical.TryGetValue(key, out var kept))
                            {
                                canonical[key] = kept = key;
                            }

                            held.Add((hash, kept));
                        }

                        if (held.Count > MaxHeld)
                        {
                            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                                $"The records of dimension {dimension.Name} hold more than {MaxHeld:N0} keys between them, more than a load keeps by record for incremental loads. Narrow its query, split it over dimensions with narrower queries, or take out the flow's incremental block to load it in full only."));
                        }
                    }
                }
            }).ConfigureAwait(false);

        log.LogInformation(
            "dimension {Dimension}: {Records} record(s) read by their unique key with {Held} key(s) between them in {Pages} page(s) over {Ranges} range(s){Unidentified}",
            dimension.Name, read, held.Count, pages, work.Count,
            unidentified > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {unidentified} hold no single value at a key column and are left out") : string.Empty);
        return new RecordRead(records, held, read, unidentified, pages, work.Count);
    }

    /// <summary>The values of a record's key columns, in order; null when one holds no single value, so the record cannot be told apart.</summary>
    private static IReadOnlyList<string>? Identity(JsonElement hit, IReadOnlyList<OsduField> keyColumns)
    {
        var values = new List<string>(keyColumns.Count);
        foreach (var column in keyColumns)
        {
            var held = DimensionCollector.TextsOf(hit, column);
            if (held.Count != 1)
            {
                return null;
            }

            values.Add(held.First());
        }

        return values;
    }

    /// <summary>A query as a pass asks it: none for every record.</summary>
    private static string? Own(string? query) => string.IsNullOrWhiteSpace(query) || query.Trim() == "*" ? null : query.Trim();

    /// <summary>One cursor of a pass: its query, the range of keys it reads, and which keys it keeps of a record.</summary>
    private sealed record Pass(string? Query, DistinctSlice Range, Func<string, bool> Keeps);
}
