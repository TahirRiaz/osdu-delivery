using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Refreshes of one partition merge one after another (docs/partitions-design.md section 6): a merge waits for another
/// merge of the same partition to commit and then merges onto the version it wrote, so refreshes run at once never fail each
/// other and never lose one another's types. Each test works in a partition of its own.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class CachePartitionLockTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly string _scope = "lock" + Guid.NewGuid().ToString("N")[..10];

    public void Dispose()
    {
        using (var db = _db.CreateDbContext())
        {
            db.DeliveryCacheItems.Where(i => i.Scope == _scope).ExecuteDelete();
            db.DeliveryCacheMembers.Where(m => m.Scope == _scope).ExecuteDelete();
            db.DeliveryCacheVersions.Where(v => v.Scope == _scope).ExecuteDelete();
        }

        _db.Dispose();
    }

    /// <summary>A lookup table of one row, captured by the flow of the same name.</summary>
    private static ReferenceType Table(string name)
        => new(name, "lookup--" + name, [new ReferenceItem("k", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase) { ["k"] = ReferenceValue.Of("k"), ["v"] = ReferenceValue.Of(name) })], "k");

    [Fact]
    public async Task Refreshes_of_one_partition_run_at_once_merge_one_after_another_and_keep_every_type()
    {
        var names = Enumerable.Range(1, 6).Select(i => $"Table{i}").ToList();
        var start = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        // Every merge starts at once, each from a store of its own, as the refreshes of six nodes would.
        var writes = await Task.WhenAll(names.Select((name, index) => Task.Run(() => _db.Caches().MergeAsync(
            _scope, "flow-" + name, [Table(name)], new CacheCapture(null, "tests", name), start.AddSeconds(index)))));

        Assert.All(writes, write => Assert.True(write.Written));
        await using var db = _db.CreateDbContext();
        var versions = await db.DeliveryCacheVersions.AsNoTracking().Where(v => v.Scope == _scope).OrderBy(v => v.Sequence).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 6), versions.Select(v => v.Sequence));
        Assert.Single(versions, v => v.Current);
        Assert.True(versions[^1].Current);

        // Each version was merged onto the one written before it, so the chain is unbroken and the last holds every type.
        Assert.Null(versions[0].PreviousVersion);
        Assert.All(versions.Skip(1).Zip(versions), pair => Assert.Equal(pair.Second.Version, pair.First.PreviousVersion));
        var current = (await _db.Caches().LoadAsync(_scope, versions[^1].Version))!;
        Assert.Equal(names, current.Types.Select(t => t.Name).Order(StringComparer.Ordinal));
    }
}
