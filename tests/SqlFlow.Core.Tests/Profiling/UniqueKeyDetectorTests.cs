using System.Globalization;
using SqlFlow.Core.Profiling;
using Xunit;

namespace SqlFlow.Tests.Profiling;

/// <summary>
/// The storage-agnostic unique-key detection algorithm, exercised against in-memory tables through a fake probe:
/// single-column keys, minimal composite keys (redundancy removed), the no-key outcome, and the sampling contract
/// that a candidate unique only on the sample is verified against the whole table before it is reported as unique.
/// </summary>
public sealed class UniqueKeyDetectorTests
{
    /// <summary>A probe over in-memory rows: the sample is the first N rows, mirroring the SQL probe's TOP(N).</summary>
    private sealed class FakeProbe : IUniquenessProbe
    {
        private readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> _full;
        private readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> _working;

        public FakeProbe(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, int sampleSize)
        {
            _full = rows;
            if (sampleSize > 0 && rows.Count > sampleSize)
            {
                _working = rows.Take(sampleSize).ToList();
                Sampled = true;
            }
            else
            {
                _working = rows;
                Sampled = false;
            }

            TotalRows = _full.Count;
            WorkingRows = _working.Count;
        }

        public long TotalRows { get; }

        public long WorkingRows { get; }

        public bool Sampled { get; }

        /// <summary>How many measurement passes (batched queries) the detector issued.</summary>
        public int MeasurePasses { get; private set; }

        public Task<IReadOnlyList<SetMeasure>> MeasureManyAsync(IReadOnlyList<IReadOnlyList<string>> sets, CancellationToken ct = default)
        {
            MeasurePasses++;
            return Task.FromResult<IReadOnlyList<SetMeasure>>(sets.Select(s => Measure(_working, s, exact: !Sampled)).ToList());
        }

        public Task<SetVerdict> VerifyAsync(IReadOnlyList<string> columns, CancellationToken ct = default)
        {
            var m = Measure(_full, columns, exact: true);
            return Task.FromResult(new SetVerdict
            {
                Columns = columns.ToList(),
                IsUnique = m.IsUnique,
                HasNulls = m.Nulls > 0,
                Rows = m.Scanned,
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static SetMeasure Measure(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<string> columns, bool exact)
        {
            long nulls = 0;
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (columns.Any(c => row[c] is null))
                {
                    nulls++;
                    continue;
                }

                // A length-prefixed join so ("a","bc") and ("ab","c") never alias to the same tuple key.
                distinct.Add(string.Concat(columns.Select(c =>
                {
                    var text = Convert.ToString(row[c], CultureInfo.InvariantCulture) ?? string.Empty;
                    return text.Length.ToString(CultureInfo.InvariantCulture) + ":" + text + "|";
                })));
            }

            return new SetMeasure
            {
                Columns = columns.ToList(),
                Scanned = rows.Count,
                Distinct = distinct.Count,
                Nulls = nulls,
                Exact = exact,
            };
        }
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Table(IReadOnlyList<string> columns, params object?[][] rows)
        => rows.Select(r =>
        {
            var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
            {
                d[columns[i]] = r[i];
            }

            return (IReadOnlyDictionary<string, object?>)d;
        }).ToList();

    private static Task<UniqueKeyReport> DetectAsync(FakeProbe probe, IReadOnlyList<string> columns, UniqueKeyOptions? options = null)
        => UniqueKeyDetector.DetectAsync(probe, columns, options ?? new UniqueKeyOptions());

    [Fact]
    public async Task SingleColumnKey_IsDetectedAndPreferred()
    {
        var columns = new[] { "Id", "Name" };
        var rows = Table(columns,
            [1, "a"], [2, "a"], [3, "b"], [4, "b"], [5, "c"]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns);

        var top = report.Candidates[0];
        Assert.True(top.IsUnique);
        Assert.True(top.Verified);
        Assert.Equal(new[] { "Id" }, top.Columns);
        // A single-column key exists, so no composite is offered.
        Assert.All(report.Candidates.Where(c => c.IsUnique), c => Assert.Single(c.Columns));
    }

    [Fact]
    public async Task CompositeKey_IsDetectedAndMinimal()
    {
        var columns = new[] { "A", "B", "Const" };
        // (A,B) is unique; neither A nor B alone is; Const is a useless constant.
        var rows = Table(columns,
            [1, "x", 9], [1, "y", 9], [2, "x", 9], [2, "y", 9]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns);

        var key = Assert.Single(report.Candidates, c => c.IsUnique);
        Assert.True(key.Verified);
        Assert.Equal(new[] { "A", "B" }, key.Columns.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("Const", key.Columns);
    }

    [Fact]
    public async Task DetectedKey_IsMinimal_NoProperSubsetIsUnique()
    {
        // (Y,Z) is the key; X is a decoy the ranking may seed first, so redundancy removal must strip whatever
        // greedy overshoots with. Whatever key is returned, removing any one column must break uniqueness.
        var columns = new[] { "X", "Y", "Z" };
        var rows = Table(columns,
            [1, 1, 1], [2, 1, 2], [3, 2, 1], [4, 2, 2], [5, 3, 1], [1, 3, 2]);
        await using var probe = new FakeProbe(rows, 0);
        var report = await DetectAsync(probe, columns);

        var key = report.Candidates.First(c => c.IsUnique).Columns;
        Assert.True(key.Count >= 2);
        foreach (var column in key)
        {
            var subset = key.Where(c => c != column).ToList();
            var measure = await probe.VerifyAsync(subset);
            Assert.False(measure.IsUnique, $"dropping {column} left a still-unique subset [{string.Join(",", subset)}]; key is not minimal");
        }
    }

    [Fact]
    public async Task NoUniqueKey_ReportsClosestCandidateAndNote()
    {
        var columns = new[] { "A", "B" };
        // Every combination repeats: no key exists at any width.
        var rows = Table(columns,
            [1, "x"], [1, "x"], [2, "y"], [2, "y"]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns);

        Assert.DoesNotContain(report.Candidates, c => c.IsUnique);
        Assert.NotNull(report.Note);
        Assert.Contains("No unique key", report.Note!, StringComparison.OrdinalIgnoreCase);
        Assert.All(report.Candidates, c => Assert.True(c.Duplicates > 0));
    }

    [Fact]
    public async Task NullableColumn_IsNeverAKey_EvenWhenDistinct()
    {
        var columns = new[] { "Id", "Opt" };
        // Opt has all-distinct non-null values but one null, so it cannot be a key; Id is the key.
        var rows = Table(columns,
            [1, "p"], [2, "q"], [3, null], [4, "s"]);
        var report = await DetectAsync(new FakeProbe(rows, 0), columns);

        Assert.Equal(new[] { "Id" }, report.Candidates.First(c => c.IsUnique).Columns);
        Assert.DoesNotContain(report.Candidates, c => c.IsUnique && c.Columns.Contains("Opt"));
    }

    [Fact]
    public async Task SampleUniqueButFullDuplicate_IsVerifiedAndRejected()
    {
        var columns = new[] { "Code" };
        // Unique across the first 3 sampled rows, but row 4 duplicates row 1: a real full-table check must reject it.
        var rows = Table(columns, ["a"], ["b"], ["c"], ["a"]);
        var report = await DetectAsync(new FakeProbe(rows, sampleSize: 3), columns);

        Assert.True(report.Sampled);
        var candidate = Assert.Single(report.Candidates);
        Assert.False(candidate.IsUnique);      // rejected by full-table verification
        Assert.True(candidate.Verified);        // and the verdict was measured against the whole table
    }

    [Fact]
    public async Task SampleUniqueAndFullUnique_IsReportedVerified()
    {
        var columns = new[] { "Id" };
        var rows = Table(columns, [1], [2], [3], [4], [5], [6]);
        var report = await DetectAsync(new FakeProbe(rows, sampleSize: 3), columns);

        var candidate = Assert.Single(report.Candidates);
        Assert.True(candidate.IsUnique);
        Assert.True(candidate.Verified);
        Assert.Contains("verified against the whole table", report.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_BatchesTrials_MeasurementPassesStaySmall()
    {
        // 10 columns, a composite key of two. A naive per-trial probe would scan once per single column and once per
        // trial combination (dozens of passes); the batched search collapses each level into one pass, so the total
        // number of passes is small and, crucially, does not scale with the number of combinations.
        var columns = new[] { "A", "B", "C", "D", "E", "F", "G", "H", "K1", "K2" };
        var rng = new Random(7);
        var rows = Enumerable.Range(0, 40).Select(i =>
        {
            var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in columns.Take(8))
            {
                d[c] = rng.Next(0, 3); // low-cardinality noise columns
            }

            d["K1"] = i % 8;   // together K1,K2 uniquely identify the 40 rows
            d["K2"] = i / 8;
            return (IReadOnlyDictionary<string, object?>)d;
        }).ToList();

        var probe = new FakeProbe(rows, 0);
        var report = await DetectAsync(probe, columns);

        Assert.Contains(report.Candidates, c => c.IsUnique);
        // One pass for all singles, then a bounded handful for the greedy levels and reduction: never the O(combinations)
        // of the naive approach.
        Assert.True(probe.MeasurePasses <= 12, $"expected a small number of batched passes, got {probe.MeasurePasses}");
    }

    [Fact]
    public async Task EmptyTable_YieldsNoCandidatesWithNote()
    {
        var columns = new[] { "Id" };
        var report = await DetectAsync(new FakeProbe(Table(columns), 0), columns);

        Assert.Empty(report.Candidates);
        Assert.Equal(0, report.TotalRows);
        Assert.Contains("empty", report.Note!, StringComparison.OrdinalIgnoreCase);
    }
}
