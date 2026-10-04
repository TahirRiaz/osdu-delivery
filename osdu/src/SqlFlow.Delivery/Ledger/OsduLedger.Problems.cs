using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The ledger's problems (docs/ledger.md, Problems): what keeps a flow's records blocked, grouped by the problem each
/// record's error names (<see cref="ProblemSignature"/>), counted from the records through the problem index, and the
/// signing of the records blocked before the ledger kept problems.
/// </summary>
public sealed partial class OsduLedger
{
    /// <summary>The most problems one listing names; the rest are counted, not named.</summary>
    public const int MaxProblems = 500;

    /// <summary>The most files one problem's listing names.</summary>
    public const int MaxProblemFiles = 200;

    public async Task<ProblemListing> ListProblemsAsync(Guid flowId, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return new ProblemListing([], 0, 0, 0);
        }

        var take = Math.Clamp(max, 1, MaxProblems);
        var (counts, examples, unsorted) = await ReadAsync(
            async db =>
            {
                var counts = await SqlServerLedgerBulk.ProblemsAsync(db, partition, flowId, null, take, ct).ConfigureAwait(false);
                var examples = await ExamplesAsync(db, partition, flowId, counts.Select(c => c.Problem).ToList(), ct).ConfigureAwait(false);
                var unsorted = await SqlServerLedgerBulk.UnsortedCountAsync(db, partition, flowId, ct).ConfigureAwait(false);
                return (counts, examples, unsorted);
            },
            ct).ConfigureAwait(false);

        var name = await PartitionNameAsync(partition, ct).ConfigureAwait(false);
        var problems = counts
            .Select(c => Group(c, examples.GetValueOrDefault(c.Problem), name))
            .OfType<ProblemGroup>()
            .ToList();
        var first = counts.Count > 0 ? counts[0] : null;
        return new ProblemListing(problems, first?.Problems ?? 0, first?.Total ?? 0, unsorted);
    }

    public async Task<ProblemGroup?> GetProblemAsync(Guid flowId, long problem, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var (counts, examples) = await ReadAsync(
            async db =>
            {
                var counts = await SqlServerLedgerBulk.ProblemsAsync(db, partition, flowId, problem, 1, ct).ConfigureAwait(false);
                var examples = await ExamplesAsync(db, partition, flowId, counts.Select(c => c.Problem).ToList(), ct).ConfigureAwait(false);
                return (counts, examples);
            },
            ct).ConfigureAwait(false);
        if (counts.Count == 0)
        {
            return null;
        }

        return Group(counts[0], examples.GetValueOrDefault(problem), await PartitionNameAsync(partition, ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<ProblemFile>> ListProblemFilesAsync(Guid flowId, long problem, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        return await ReadAsync(
            db => SqlServerLedgerBulk.ProblemFilesAsync(db, partition, flowId, problem, Math.Clamp(max, 1, MaxProblemFiles), ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sorts the next <paramref name="max"/> blocked records the ledger has not sorted into a problem, across every
    /// ledger: each record's problem is read from its stored error, exactly as a hold or a failure of it would be sorted
    /// now, and written only while the record is still the blocked record that error was read from. Returns how many
    /// records it read and how many it sorted; a pass reads pages until one is empty, or until a page sorts nothing, which
    /// only records changing under it can cause and the next pass settles.
    /// </summary>
    public async Task<(int Read, int Sorted)> SortProblemsAsync(int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        await using var db = Open();
        var page = await RetryDeadlockAsync(() => SqlServerLedgerBulk.UnsortedPageAsync(db, Math.Clamp(max, 1, 1000), ct), ct).ConfigureAwait(false);
        if (page.Count == 0)
        {
            return (0, 0);
        }

        var sorted = page.Select(r => (Record: r, Problem: ProblemSignature.Of(r.LastError))).ToList();
        var written = await RetryDeadlockAsync(() => SqlServerLedgerBulk.SortAsync(db, sorted, ct), ct).ConfigureAwait(false);
        return (page.Count, written);
    }

    /// <summary>The example record of each problem named: its most recently changed record, read whole.</summary>
    private static async Task<IReadOnlyDictionary<long, RecordState>> ExamplesAsync(
        Data.OsduDbContext db, short partition, Guid flowId, IReadOnlyCollection<long> problems, CancellationToken ct)
    {
        var keys = await SqlServerLedgerBulk.ProblemExamplesAsync(db, partition, flowId, problems, ct).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            return new Dictionary<long, RecordState>();
        }

        var rows = await db.DeliveryRecords.AsNoTracking()
            .Where(r => r.PartitionId == partition && r.FlowId == flowId && keys.Contains(r.DeliveryKey))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // A record whose problem moved on between the two reads is no example of the one it was found for.
        return rows
            .Where(r => r.ProblemHash is not null)
            .GroupBy(r => r.ProblemHash!.Value)
            .ToDictionary(g => g.Key, g => ToState(g.First()));
    }

    /// <summary>
    /// A problem as a listing names it: its counts, its example, and its pattern read from the example's error. A problem
    /// whose records all moved on between the count and the example's read has no example, and is left out.
    /// </summary>
    private static ProblemGroup? Group(SqlServerLedgerBulk.ProblemCount count, RecordState? example, string? partition)
        => example is null
            ? null
            : new ProblemGroup
            {
                Problem = count.Problem,
                Pattern = ProblemSignature.Pattern(example.LastError),
                Records = count.Records,
                Held = count.Held,
                Failed = count.Failed,
                OldestUtc = count.OldestUtc,
                NewestUtc = count.NewestUtc,
                Example = example with { Partition = partition },
            };
}
