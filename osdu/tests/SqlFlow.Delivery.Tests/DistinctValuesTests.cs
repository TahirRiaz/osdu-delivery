using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Reading every distinct value of a field from an aggregation that returns at most so many groups. The index here behaves
/// as the search service does (<c>AggregationParserUtil</c>, <c>CoreQueryBase</c>): it groups the values of the records a
/// query matches, every value of a matched record included, orders the groups by count and then by key, returns the first
/// <c>aggregationSize</c>, counts the objects of a nested array rather than records, keeps a null text as the text null and
/// a text longer than 256 characters not at all, and names no key for a plain number when told to.
/// </summary>
public class DistinctValuesTests
{
    private static readonly OsduField Name = OsduField.Text("data.FacilityName");
    private static readonly OsduField Tags = OsduField.Keyword("acl.viewers");
    private static readonly OsduField Mnemonic = OsduField.Text("data.Curves.Mnemonic", "data.Curves");
    private static readonly OsduField Depth = OsduField.Number("data.TotalDepth");
    private static readonly OsduField Spud = OsduField.Date("data.SpudDate");

    private static DistinctReadOptions Options(int size = 100, long max = 1_000_000, bool repeats = false) => new(size, max, repeats);

    private static Task<DistinctRead> Read(FakeIndex index, DistinctReadOptions options)
    {
        index.Cap = options.AggregationSize;
        return DistinctValues.ReadAsync(index, options, NullLogger.Instance, CancellationToken.None);
    }

    /// <summary>The values read, whatever order they were read in, against the values expected.</summary>
    private static void AssertValues(DistinctRead read, params (string Value, long Count)[] expected)
        => Assert.Equal(
            expected.OrderBy(e => e.Value, StringComparer.Ordinal).ToList(),
            read.Values.Select(kv => (kv.Key, kv.Value)).OrderBy(e => e.Key, StringComparer.Ordinal).ToList());

    [Fact]
    public async Task A_field_with_fewer_values_than_one_aggregation_returns_is_read_in_one_request_with_exact_counts()
    {
        var index = FakeIndex.Single(Name, ["A", "B", "B", "C", "C", "C"]);

        var read = await Read(index, Options());

        AssertValues(read, ("A", 1), ("B", 2), ("C", 3));
        Assert.Equal(1, read.Aggregations);
        Assert.Equal(0, read.Splits);
        Assert.Equal(6, read.Records);
        Assert.Equal(6, read.WithValue);
        Assert.Empty(read.Notes);
    }

    [Fact]
    public async Task Tens_of_thousands_of_values_are_all_read_by_paging_the_aggregation_by_ranges()
    {
        // 20,000 distinct names, some held by several records, against an aggregation of 100 groups.
        var names = Enumerable.Range(0, 20_000).SelectMany(i => Enumerable.Repeat($"WB-{i:D5}", 1 + (i % 3))).ToList();
        var index = FakeIndex.Single(Name, names);

        var read = await Read(index, Options(size: 100));

        Assert.Equal(20_000, read.Values.Count);
        Assert.All(read.Values, kv => Assert.Equal(1 + (int.Parse(kv.Key[3..], CultureInfo.InvariantCulture) % 3), kv.Value));
        Assert.True(read.Splits > 0);
        Assert.Equal(names.Count, read.Values.Values.Sum());
        Assert.Empty(read.Notes);
    }

    [Fact]
    public async Task Ranges_asked_several_at_a_time_read_what_one_at_a_time_reads_in_as_many_aggregations()
    {
        var names = Enumerable.Range(0, 20_000).SelectMany(i => Enumerable.Repeat($"WB-{i:D5}", 1 + (i % 3))).ToList();
        var inTurn = await Read(FakeIndex.Single(Name, names), Options(size: 100));

        var index = FakeIndex.Single(Name, names);
        index.Cap = 100;
        var slow = new AtOnce(index);
        var together = await DistinctValues.ReadAsync(slow, Options(size: 100) with { Concurrency = 8 }, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(
            inTurn.Values.OrderBy(v => v.Key, StringComparer.Ordinal).ToList(), together.Values.OrderBy(v => v.Key, StringComparer.Ordinal).ToList());
        Assert.Equal((inTurn.Aggregations, inTurn.Slices, inTurn.Splits), (together.Aggregations, together.Slices, together.Splits));
        Assert.Equal((inTurn.Records, inTurn.WithValue), (together.Records, together.WithValue));
        Assert.InRange(slow.Most, 2, 8);
        Assert.Equal(0, slow.Now);
    }

    [Fact]
    public async Task A_range_that_fails_ends_the_read_with_its_failure_and_leaves_no_range_asked()
    {
        var names = Enumerable.Range(0, 20_000).Select(i => $"WB-{i:D5}").ToList();
        var index = FakeIndex.Single(Name, names);
        index.Cap = 100;
        var slow = new AtOnce(index) { FailAt = 40 };

        var failed = await Assert.ThrowsAsync<DeliveryException>(
            () => DistinctValues.ReadAsync(slow, Options(size: 100) with { Concurrency = 8 }, NullLogger.Instance, CancellationToken.None));

        Assert.Equal("the service refused aggregation 40", failed.Message);
        Assert.Equal(0, slow.Now);
    }

    /// <summary>
    /// A source that answers each aggregation a moment after it is asked, as a service does, and counts how many are asked
    /// at once; the index behind it is asked one at a time, since it is no service.
    /// </summary>
    private sealed class AtOnce(FakeIndex inner) : IDistinctValueSource
    {
        private readonly Lock _gate = new();
        private int _now;
        private int _asked;

        public OsduField Field => inner.Field;

        /// <summary>The most aggregations in flight at one time.</summary>
        public int Most { get; private set; }

        /// <summary>The aggregations in flight now.</summary>
        public int Now => Volatile.Read(ref _now);

        /// <summary>The aggregation, counted from 1, that fails; none when zero.</summary>
        public int FailAt { get; init; }

        public async Task<(long Total, IReadOnlyList<OsduSearchBucket> Buckets)> AggregateAsync(DistinctSlice slice, CancellationToken ct)
        {
            int asked;
            lock (_gate)
            {
                asked = ++_asked;
                Most = Math.Max(Most, ++_now);
            }

            try
            {
                await Task.Delay(2, ct);
                if (asked == FailAt)
                {
                    throw new DeliveryException($"the service refused aggregation {asked}");
                }

                lock (_gate)
                {
                    return inner.AggregateAsync(slice, ct).GetAwaiter().GetResult();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _now);
            }
        }

        public IAsyncEnumerable<IReadOnlyList<DistinctUnit>> ScanAsync(DistinctSlice slice, CancellationToken ct) => inner.ScanAsync(slice, ct);

        public Task<long?> CountAsync(DistinctCheck check, CancellationToken ct) => inner.CountAsync(check, ct);
    }

    [Fact]
    public async Task Values_a_record_holds_several_of_are_each_counted_once_per_record()
    {
        var random = new Random(7);
        var records = Enumerable.Range(0, 3_000)
            .Select(_ => (IReadOnlyList<string>)Enumerable.Range(0, 3).Select(_ => $"group-{random.Next(5_000):D4}").ToList())
            .ToList();
        var index = FakeIndex.Arrays(Tags, records);

        var read = await Read(index, Options(size: 50, repeats: true));

        var expected = records.SelectMany(r => r.Distinct()).GroupBy(v => v).Select(g => (g.Key, (long)g.Count())).ToArray();
        AssertValues(read, expected);
    }

    [Fact]
    public async Task A_range_the_other_values_of_its_records_fill_is_read_by_scanning()
    {
        // Every record holds 150 values, so any range that matches a record brings back at least 150 groups: no split
        // can bring a range under the 100 an aggregation returns, and it has to be scanned. There are enough records that
        // the first range does not fit one page, so the split is tried and given up for the scan.
        var records = Enumerable.Range(0, 4_000)
            .Select(r => (IReadOnlyList<string>)Enumerable.Range(0, 150).Select(v => $"v{(r * 150) + v:D5}").ToList())
            .ToList();
        var index = FakeIndex.Arrays(Tags, records);

        var read = await Read(index, Options(size: 100, repeats: true));

        Assert.Equal(600_000, read.Values.Count);
        Assert.All(read.Values.Values, count => Assert.Equal(1, count));
        Assert.True(read.ScannedSlices > 0);
    }

    [Fact]
    public async Task Inside_a_nested_array_the_objects_are_counted_as_the_index_counts_them()
    {
        var index = FakeIndex.Nested(Mnemonic, [["GR", "DT", "GR"], ["GR"], ["RHOB"]]);

        var read = await Read(index, Options(repeats: true));

        AssertValues(read, ("DT", 1), ("GR", 3), ("RHOB", 1));

        // _exists_ cannot be asked inside a nested array, so what is not counted is said to be unknown.
        Assert.Null(read.WithValue);
        Assert.Null(read.TooLong);
    }

    [Fact]
    public async Task A_nested_field_whose_keys_the_service_would_misread_is_split_at_a_key_it_would_not()
    {
        // Unbalanced parentheses cannot be carried inside nested(...): the service balances the inner query by counting
        // every parenthesis. Half the keys have one, and the split has to pass them over.
        var values = Enumerable.Range(0, 4_000).Select(i => i % 2 == 0 ? $"M{i:D4} (" : $"M{i:D4}").ToList();
        var index = FakeIndex.Nested(Mnemonic, values.Select(v => (IReadOnlyList<string>)[v]).ToList());

        var read = await Read(index, Options(size: 50, repeats: true));

        Assert.Equal(4_000, read.Values.Count);
        Assert.True(read.Splits > 0);
        Assert.All(index.Bounds, bound => Assert.Null(OsduQueryProblem(Mnemonic, bound)));
    }

    [Fact]
    public async Task A_range_with_no_key_the_service_could_carry_as_a_bound_is_scanned()
    {
        // More records than one page, so the range is not simply scanned for being small: it is scanned because no key in
        // it could bound a smaller one.
        var values = Enumerable.Range(0, 3_000).Select(i => $"M{i:D4} (").ToList();
        var index = FakeIndex.Nested(Mnemonic, values.Select(v => (IReadOnlyList<string>)[v]).ToList());

        var read = await Read(index, Options(size: 50, repeats: true));

        Assert.Equal(3_000, read.Values.Count);
        Assert.Equal(0, read.Splits);
        Assert.Equal(1, read.ScannedSlices);
    }

    [Fact]
    public async Task A_number_whose_groups_come_back_without_keys_is_read_by_scanning_every_record()
    {
        var index = FakeIndex.Numbers(Depth, ["3500", "3500.0", "12.50", "-0", "1E+3", "1000"], namesKeys: false);

        var read = await Read(index, Options());

        Assert.True(read.ScannedWhole);
        AssertValues(read, ("0", 1), ("12.5", 1), ("1000", 2), ("3500", 2));
        Assert.Contains(read.Notes, n => n.Contains("no key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_number_whose_groups_carry_keys_is_read_from_them_in_numeric_order()
    {
        var values = Enumerable.Range(-2_500, 5_000).Select(i => (i / 4.0).ToString(CultureInfo.InvariantCulture)).ToList();
        var index = FakeIndex.Numbers(Depth, values, namesKeys: true);

        var read = await Read(index, Options(size: 20));

        Assert.False(read.ScannedWhole);
        Assert.Equal(5_000, read.Values.Count);
        Assert.True(read.Splits > 0);
        Assert.Contains("-625", read.Values.Keys);
        Assert.Contains("624.75", read.Values.Keys);
    }

    [Fact]
    public async Task A_null_text_is_counted_as_no_value_and_never_as_one()
    {
        var index = FakeIndex.Raw(Name, [[ScannedValue.OfString("A")], [ScannedValue.Null], [ScannedValue.OfString("null")], [ScannedValue.OfString("NULL")]]);

        var read = await Read(index, Options());

        AssertValues(read, ("A", 1), ("NULL", 1));
        Assert.Equal(2, read.Nulls);
        Assert.Empty(read.Notes);
    }

    [Fact]
    public async Task The_text_null_of_a_keyword_is_a_value_like_any_other()
    {
        var index = FakeIndex.Arrays(Tags, [["null"], ["data.default.viewers@opendes"]]);

        var read = await Read(index, Options(repeats: true));

        Assert.Equal(2, read.Values.Count);
        Assert.Equal(0, read.Nulls);
    }

    [Fact]
    public async Task A_text_longer_than_the_keyword_keeps_is_counted_apart_and_is_no_value()
    {
        var index = FakeIndex.Single(Name, ["short", new string('x', 257), new string('y', 256)]);

        var read = await Read(index, Options());

        Assert.Equal(2, read.Values.Count);
        Assert.Equal(1, read.TooLong);
        Assert.Contains(read.Notes, n => n.Contains("256 characters", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dimension_holding_more_values_than_it_may_keep_stops_and_says_so()
    {
        var index = FakeIndex.Single(Name, Enumerable.Range(0, 500).Select(i => $"N{i}").ToList());

        var refused = await Assert.ThrowsAsync<DimensionTooLargeException>(() => Read(index, Options(size: 50, max: 100)));

        Assert.Contains("maxValues", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_is_split_in_code_point_order_so_characters_beyond_the_basic_plane_land_in_the_right_range()
    {
        // U+1F600 sorts before U+FF5E by UTF-16 code units and after it by code points, which is how the index orders
        // UTF-8 terms. A split in the wrong order would put values in a range that does not hold them.
        var values = Enumerable.Range(0, 3_000)
            .SelectMany(i => new[] { $"a{i:D4}\U0001F600", $"a{i:D4}～", $"a{i:D4}z" })
            .ToList();
        var index = FakeIndex.Single(Name, values);

        var read = await Read(index, Options(size: 40));

        Assert.Equal(values.Count, read.Values.Count);
        Assert.True(read.Splits > 0);
        Assert.Empty(read.Notes);
    }

    [Fact]
    public async Task Dates_read_from_keys_and_from_a_scan_are_one_text()
    {
        var index = FakeIndex.Raw(Spud, [[ScannedValue.OfString("2020-01-02T00:00:00Z")], [ScannedValue.OfString("2020-01-02T00:00:00.000Z")], [ScannedValue.OfNumber("1577923200000")]]);

        var read = await Read(index, Options());

        AssertValues(read, ("2020-01-02T00:00:00.000Z", 3));
    }

    [Fact]
    public async Task A_count_the_service_refuses_is_noted_and_the_values_are_kept()
    {
        var index = FakeIndex.Single(Name, ["A", "B"]) with { RefuseCounts = true };

        var read = await Read(index, Options());

        Assert.Equal(2, read.Values.Count);
        Assert.Null(read.Records);
        Assert.Contains(read.Notes, n => n.StartsWith("Could not count", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Counts_that_disagree_with_the_records_holding_a_value_are_noted()
    {
        // A record arrives between the read of the values and the count of the records holding one.
        var index = FakeIndex.Single(Name, ["A", "B"]) with { ExtraRecordsAtCount = 1 };

        var read = await Read(index, Options());

        Assert.Contains(read.Notes, n => n.Contains("the index changed while it was read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_object_where_a_value_was_expected_is_counted_as_unreadable()
    {
        var index = FakeIndex.Raw(Depth, [[ScannedValue.Composite], [ScannedValue.OfNumber("7")], [ScannedValue.OfString("deep")]]) with { NamesKeys = false };

        var read = await Read(index, Options());

        AssertValues(read, ("7", 1));
        Assert.Equal(2, read.Unreadable);
    }

    [Fact]
    public async Task A_read_is_cancelled_between_requests()
    {
        var index = FakeIndex.Single(Name, Enumerable.Range(0, 5_000).Select(i => $"N{i:D5}").ToList());
        using var cancel = new CancellationTokenSource();
        index.Cap = 50;
        index.OnAggregate = count =>
        {
            if (count == 3)
            {
                cancel.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DistinctValues.ReadAsync(index, Options(size: 50), NullLogger.Instance, cancel.Token));
    }

    private static string? OsduQueryProblem(OsduField field, string bound)
    {
        try
        {
            _ = OsduQuery.Range(field, bound, null);
            return null;
        }
        catch (OsduQueryException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>An index of records, each holding units of values, answering as the search service does.</summary>
    internal sealed record FakeIndex(OsduField Field, IReadOnlyList<IReadOnlyList<IReadOnlyList<ScannedValue>>> Records, int PageSize = 1000)
        : IDistinctValueSource
    {
        /// <summary>False when the service names no key for the groups, as for a plain number.</summary>
        public bool NamesKeys { get; init; } = true;

        public bool RefuseCounts { get; init; }

        public int ExtraRecordsAtCount { get; init; }

        public Action<int>? OnAggregate { get; set; }

        public List<string> Bounds { get; } = [];

        private int _aggregations;

        private IComparer<string> Order => DimensionValueText.Order(Field.Index);

        public static FakeIndex Single(OsduField field, IReadOnlyList<string> values)
            => new(field, values.Select(v => (IReadOnlyList<IReadOnlyList<ScannedValue>>)[[ScannedValue.OfString(v)]]).ToList());

        public static FakeIndex Arrays(OsduField field, IReadOnlyList<IReadOnlyList<string>> records)
            => new(field, records.Select(r => (IReadOnlyList<IReadOnlyList<ScannedValue>>)[r.Select(ScannedValue.OfString).ToList()]).ToList());

        public static FakeIndex Nested(OsduField field, IReadOnlyList<IReadOnlyList<string>> records)
            => new(field, records.Select(r => (IReadOnlyList<IReadOnlyList<ScannedValue>>)r.Select(v => (IReadOnlyList<ScannedValue>)[ScannedValue.OfString(v)]).ToList()).ToList());

        public static FakeIndex Numbers(OsduField field, IReadOnlyList<string> values, bool namesKeys)
            => new(field, values.Select(v => (IReadOnlyList<IReadOnlyList<ScannedValue>>)[[ScannedValue.OfNumber(v)]]).ToList()) { NamesKeys = namesKeys };

        public static FakeIndex Raw(OsduField field, IReadOnlyList<IReadOnlyList<ScannedValue>> records)
            => new(field, records.Select(r => (IReadOnlyList<IReadOnlyList<ScannedValue>>)[r]).ToList());

        public Task<(long Total, IReadOnlyList<OsduSearchBucket> Buckets)> AggregateAsync(DistinctSlice slice, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            OnAggregate?.Invoke(++_aggregations);
            Remember(slice);
            var prepared = Prepared;
            var (low, high) = prepared.Ranks(slice, Order);
            var counts = new Dictionary<int, long>();
            var matched = 0;
            for (var r = 0; r < prepared.Units.Length; r++)
            {
                if (!prepared.Matches(r, slice, low, high))
                {
                    continue;
                }

                matched++;
                foreach (var unit in prepared.Units[r])
                {
                    foreach (var rank in unit)
                    {
                        counts[rank] = counts.GetValueOrDefault(rank) + 1;
                    }
                }
            }

            // By count, most first, and then by key in the index's order, which the ranks are.
            var buckets = counts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .Take(Cap)
                .Select(kv => new OsduSearchBucket(NamesKeys ? prepared.Sorted[kv.Key] : null, kv.Value))
                .ToList();
            return Task.FromResult<(long, IReadOnlyList<OsduSearchBucket>)>((matched, buckets));
        }

        /// <summary>The groups an aggregation returns: the most the options allow, set by the read through <see cref="Cap"/>.</summary>
        public int Cap { get; set; } = int.MaxValue;

        public async IAsyncEnumerable<IReadOnlyList<DistinctUnit>> ScanAsync(DistinctSlice slice, [EnumeratorCancellation] CancellationToken ct)
        {
            Remember(slice);
            var prepared = Prepared;
            var (low, high) = prepared.Ranks(slice, Order);
            var matched = Enumerable.Range(0, Records.Count).Where(r => prepared.Matches(r, slice, low, high)).Select(r => Records[r]).ToList();
            foreach (var page in matched.Chunk(PageSize))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return page.SelectMany(r => r).Select(u => new DistinctUnit(u)).ToList();
            }
        }

        public Task<long?> CountAsync(DistinctCheck check, CancellationToken ct)
        {
            if (RefuseCounts)
            {
                throw new DeliveryException("POST /api/search/v2/query answered 400: the query could not be parsed.");
            }

            if (Field.NestedPath is not null && check != DistinctCheck.Records)
            {
                return Task.FromResult<long?>(null);
            }

            long count = check switch
            {
                DistinctCheck.Records => Records.Count,
                DistinctCheck.WithValue => Records.Count(r => r.SelectMany(u => u).Any(v => Key(v) is not null)) + ExtraRecordsAtCount,
                _ => Records.Count(r => r.SelectMany(u => u).Any(v => v.Kind == ScannedKind.Text && v.Text.Length > OsduQuery.KeywordIgnoreAbove)
                    && !r.SelectMany(u => u).Any(v => Key(v) is not null)),
            };
            return Task.FromResult<long?>(count);
        }

        private void Remember(DistinctSlice slice)
        {
            foreach (var bound in new[] { slice.From, slice.To })
            {
                if (bound is not null && !Bounds.Contains(bound))
                {
                    Bounds.Add(bound);
                }
            }
        }

        private PreparedIndex? _prepared;

        /// <summary>Every record's terms, ranked once in the index's order, so each request is a pass over integers.</summary>
        private PreparedIndex Prepared => _prepared ??= PreparedIndex.Build(Records, Key, Order);

        /// <summary>The term the index keeps a value under, or null for a value it does not keep in the exact field.</summary>
        private string? Key(ScannedValue value)
        {
            var (reading, text) = DimensionValueText.FromScan(Field.Index, value);
            return reading switch
            {
                ValueReading.Null => OsduQuery.KeywordNullValue,
                ValueReading.Value when Field.Index != OsduFieldIndex.Text || text.Length <= OsduQuery.KeywordIgnoreAbove => text,
                _ => null,
            };
        }
    }

    /// <summary>
    /// The terms of an index, each ranked by its place in the index's order, and every record's terms as ranks: per unit for
    /// counting, and sorted per record for matching a range.
    /// </summary>
    internal sealed class PreparedIndex
    {
        private PreparedIndex(string[] sorted, int[][][] units, int[][] records)
        {
            Sorted = sorted;
            Units = units;
            RecordRanks = records;
        }

        public string[] Sorted { get; }

        public int[][][] Units { get; }

        public int[][] RecordRanks { get; }

        public static PreparedIndex Build(
            IReadOnlyList<IReadOnlyList<IReadOnlyList<ScannedValue>>> records, Func<ScannedValue, string?> key, IComparer<string> order)
        {
            var keyed = records.Select(r => r.Select(u => u.Select(key).OfType<string>().Distinct(StringComparer.Ordinal).ToArray()).ToArray()).ToArray();
            var sorted = keyed.SelectMany(r => r.SelectMany(u => u)).Distinct(StringComparer.Ordinal).OrderBy(k => k, order).ToArray();
            var rank = new Dictionary<string, int>(sorted.Length, StringComparer.Ordinal);
            for (var i = 0; i < sorted.Length; i++)
            {
                rank[sorted[i]] = i;
            }

            var units = keyed.Select(r => r.Select(u => u.Select(k => rank[k]).ToArray()).ToArray()).ToArray();
            var perRecord = units.Select(r => r.SelectMany(u => u).Distinct().Order().ToArray()).ToArray();
            return new PreparedIndex(sorted, units, perRecord);
        }

        /// <summary>The ranks a slice holds: from the first term at or after its start up to the first at or after its end.</summary>
        public (int Low, int High) Ranks(DistinctSlice slice, IComparer<string> order)
            => (slice.From is null ? 0 : LowerBound(slice.From, order), slice.To is null ? Sorted.Length : LowerBound(slice.To, order));

        /// <summary>Whether a slice matches record <paramref name="record"/>: all do when it adds no range, else those holding a term inside it.</summary>
        public bool Matches(int record, DistinctSlice slice, int low, int high)
        {
            if (slice.IsWhole)
            {
                return true;
            }

            var ranks = RecordRanks[record];
            var at = Array.BinarySearch(ranks, low);
            var first = at >= 0 ? at : ~at;
            return first < ranks.Length && ranks[first] < high;
        }

        private int LowerBound(string bound, IComparer<string> order)
        {
            var low = 0;
            var high = Sorted.Length;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (order.Compare(Sorted[middle], bound) < 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }
    }
}
