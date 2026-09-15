using System.Globalization;

namespace SqlFlow.Delivery.Source;

/// <summary>
/// How a plan's candidate records are cut into key slices for a fan-out: contiguous ranges of the record key, each holding at
/// least a work batch of records, at most <see cref="MaxSlices"/> of them, dealt to the coordinating run and its members as
/// contiguous shares. Slices partition the key space itself, so every record a read meets falls in exactly one of them.
/// </summary>
public static class KeySlices
{
    /// <summary>The most slices one plan is cut into.</summary>
    public const int MaxSlices = 1024;

    /// <summary>How many slices <paramref name="candidates"/> records are cut into when each holds at least <paramref name="batchRecords"/>.</summary>
    public static int Count(long candidates, int batchRecords)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(candidates);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchRecords, 1);
        if (candidates <= batchRecords)
        {
            return 1;
        }

        var slices = candidates / batchRecords;
        return (int)Math.Clamp(slices, 1, MaxSlices);
    }

    /// <summary>Deals slices <c>0..slices-1</c> into at most <paramref name="workers"/> contiguous, non-empty shares; share 0 is the coordinator's.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Shares(int slices, int workers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slices, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        var count = Math.Min(slices, workers);
        var shares = new List<IReadOnlyList<int>>(count);
        var next = 0;
        for (var share = 0; share < count; share++)
        {
            var size = (slices - next) / (count - share);
            shares.Add(Enumerable.Range(next, size).ToList());
            next += size;
        }

        return shares;
    }

    /// <summary>A slice list for a log line: every index when there are few, the first and last with the count otherwise.</summary>
    public static string Describe(IReadOnlyList<int> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);
        return slices.Count <= 8
            ? string.Join(",", slices.Select(s => s.ToString(CultureInfo.InvariantCulture)))
            : string.Create(CultureInfo.InvariantCulture, $"{slices[0]}..{slices[^1]} ({slices.Count})");
    }
}
