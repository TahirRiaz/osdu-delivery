using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a fan-out cuts its candidates: ranges of the identity primary key from a count per range of values, and slices dealt
/// into contiguous shares.
/// </summary>
public class KeySlicesTests
{
    [Fact]
    public void An_even_histogram_is_cut_into_even_ranges_on_the_last_value_of_a_counted_range()
    {
        // Values 1 to 40, five to a counted range, ten candidates in each of four ranges.
        IReadOnlyList<(long, long)> buckets = [(0, 10), (1, 10), (2, 10), (3, 10)];
        Assert.Equal([10L], KeySlices.CutHistogram(1, 5, buckets, 2));
        Assert.Equal([5L, 10L, 15L], KeySlices.CutHistogram(1, 5, buckets, 4));
    }

    [Fact]
    public void A_skewed_histogram_is_cut_by_count_not_by_value()
    {
        // Most candidates sit in the first and the last counted range, far apart; the empty ranges between are not listed.
        IReadOnlyList<(long, long)> buckets = [(0, 100), (5, 1), (6, 1), (7, 1), (900, 97)];
        Assert.Equal([1_009L], KeySlices.CutHistogram(1_000, 10, buckets, 2));

        // Asked for far more ranges than the crowded buckets allow, each cut lands on the edge nearest its aim and no range is
        // left empty: the three single candidates between the crowds make a range of their own, and nothing is cut after
        // the last counted range.
        Assert.Equal([1_009L, 9_999L], KeySlices.CutHistogram(1_000, 10, buckets, 50));
    }

    [Fact]
    public void No_cut_strays_from_its_aim_by_more_than_half_a_bucket_so_errors_never_pile_up()
    {
        // Two dense runs of values and a far one, 198 values to a counted range: a greedy cut would push every early
        // overshoot into the last range.
        var buckets = new List<(long, long)>();
        buckets.AddRange(Enumerable.Range(0, 10).Select(b => ((long)b, 198L)));
        buckets.Add((10, 20));
        buckets.Add((20, 158));
        buckets.AddRange(Enumerable.Range(21, 9).Select(b => ((long)b, 198L)));
        buckets.Add((30, 60));
        buckets.Add((505, 188));
        buckets.AddRange(Enumerable.Range(506, 4).Select(b => ((long)b, 198L)));
        buckets.Add((510, 20));
        var bounds = KeySlices.CutHistogram(1, 198, buckets, 8);
        Assert.Equal(7, bounds.Count);

        var edges = new List<long> { long.MinValue };
        edges.AddRange(bounds);
        edges.Add(long.MaxValue);
        for (var slice = 0; slice < 8; slice++)
        {
            var held = buckets.Where(b => 1 + (b.Item1 * 198) > edges[slice] && 1 + (b.Item1 * 198) <= edges[slice + 1]).Sum(b => b.Item2);
            Assert.InRange(held, 625 - 198, 625 + 198);
        }
    }

    [Fact]
    public void A_cut_never_falls_after_the_last_counted_range_and_nothing_is_cut_without_candidates()
    {
        Assert.Empty(KeySlices.CutHistogram(1, 1, [], 8));
        Assert.Empty(KeySlices.CutHistogram(1, 1, [(0, 1_000)], 8));
        Assert.Equal([10L, 20L], KeySlices.CutHistogram(1, 10, [(0, 1), (1, 1), (2, 1)], 10));
    }

    [Fact]
    public void A_histogram_out_of_order_or_with_empty_ranges_is_refused()
    {
        Assert.Throws<ArgumentException>(() => KeySlices.CutHistogram(1, 1, [(2, 1), (1, 1)], 2));
        Assert.Throws<ArgumentException>(() => KeySlices.CutHistogram(1, 1, [(1, 1), (1, 1)], 2));
        Assert.Throws<ArgumentException>(() => KeySlices.CutHistogram(1, 1, [(1, 0)], 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeySlices.CutHistogram(1, 0, [(1, 1)], 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeySlices.CutHistogram(1, 1, [(1, 1)], 0));
    }

    [Fact]
    public void Slices_are_dealt_into_contiguous_shares_the_coordinator_taking_the_first()
    {
        var shares = KeySlices.Shares(10, 3);
        Assert.Equal(3, shares.Count);
        Assert.Equal([0, 1, 2], shares[0]);
        Assert.Equal([3, 4, 5], shares[1]);
        Assert.Equal([6, 7, 8, 9], shares[2]);
        Assert.Equal([1], KeySlices.Shares(2, 5)[1]);
        Assert.Equal(2, KeySlices.Shares(2, 5).Count);
    }
}
