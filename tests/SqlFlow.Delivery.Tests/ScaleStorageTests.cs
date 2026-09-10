using SqlFlow.Core;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.SampleDrop;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>The storage pieces the engine streams through: work batch files, the disk spill, and the partitioned merge join.</summary>
public class WorkBatchFileTests
{
    [Fact]
    public async Task Work_batches_round_trip_sequentially_and_by_range()
    {
        var root = Samples.NewTempDirectory();
        var stores = Samples.Stores();
        var submission = Guid.NewGuid();
        var items = Enumerable.Range(0, 50)
            .Select(i => new WorkItem(Guid.NewGuid(), $"dev:x:{i}", $"{{\"id\":\"dev:x:{i}\",\"data\":{{\"n\":{i},\"name\":\"næme {i}\"}}}}"))
            .ToList();

        var refs = new List<DocumentRef>();
        await using (var writer = await WorkBatchWriter.OpenAsync(stores, root, submission, 7))
        {
            foreach (var item in items)
            {
                refs.Add(await writer.WriteAsync(item));
            }

            Assert.Equal(50, writer.Count);
            Assert.Equal(7, writer.Batch);
        }

        Assert.True(File.Exists(WorkBatchFile.PathFor(root, submission, 7)));
        Assert.Equal(7, refs[3].Batch);
        Assert.Equal(0, refs[0].Offset);
        Assert.Equal(refs[3], DocumentRef.Parse(refs[3].ToString()));
        Assert.True(DocumentRef.TryParse("1:2:3", out var parsed));
        Assert.Equal(new DocumentRef(1, 2, 3), parsed);
        Assert.False(DocumentRef.TryParse("nope", out _));

        var all = new List<(WorkItem Item, DocumentRef Reference)>();
        await foreach (var entry in WorkBatchFile.ReadAllAsync(stores, root, submission, 7))
        {
            all.Add(entry);
        }

        Assert.Equal(50, all.Count);
        Assert.Equal(refs, all.Select(e => e.Reference).ToList());
        Assert.Equal(items[17].Key, all[17].Item.Key);
        Assert.Equal(items[17].Document, all[17].Item.Document);

        var one = await WorkBatchFile.ReadOneAsync(stores, root, submission, refs[17]);
        Assert.Equal(items[17].Key, one.Key);
        Assert.Equal(items[17].TargetId, one.TargetId);
        Assert.Equal(items[17].Document, one.Document);

        var last = await WorkBatchFile.ReadOneAsync(stores, root, submission, refs[49]);
        Assert.Equal(items[49].Document, last.Document);
    }
}

public class ScopeSpillTests
{
    [Fact]
    public async Task Rows_are_bucketed_by_key_and_read_back_whole()
    {
        await using var spill = new ScopeSpill(buckets: 4);
        Assert.Equal(4, spill.Buckets);
        var keys = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToList();
        var when = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < keys.Count; i++)
        {
            var row = new SourceRow(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["curve_id"] = "GR" + i,
                ["ordinal"] = (long)i,
                ["depth"] = 1.5 * i,
                ["flag"] = i % 2 == 0,
                ["when"] = when.AddMinutes(i),
                ["ref"] = keys[i],
                ["none"] = null,
            });
            await spill.WriteAsync("curves", keys[i], row, CancellationToken.None);
        }

        await spill.CompleteAsync(CancellationToken.None);

        var seen = new Dictionary<Guid, SourceRow>();
        for (var bucket = 0; bucket < spill.Buckets; bucket++)
        {
            await foreach (var (key, row) in spill.ReadAsync("curves", bucket, CancellationToken.None))
            {
                Assert.Equal(bucket, spill.BucketOf(key));
                seen[key] = row;
            }
        }

        Assert.Equal(200, seen.Count);
        var sample = seen[keys[7]];
        Assert.Equal("GR7", sample.GetString("curve_id"));
        Assert.Equal(7L, sample.Get("ordinal"));
        Assert.Equal(10.5, sample.Get("depth"));
        Assert.Equal(false, sample.Get("flag"));
        Assert.Equal(when.AddMinutes(7), sample.Get("when"));
        Assert.Equal(keys[7], sample.Get("ref"));
        Assert.Null(sample.Get("none"));
        Assert.Equal(1, ScopeSpill.BucketsFor(1));
        Assert.Equal(3, ScopeSpill.BucketsFor(ScopeSpill.TargetBucketBytes * 2 + 1));
        Assert.Equal(ScopeSpill.MaxBuckets, ScopeSpill.BucketsFor(long.MaxValue / 2));
    }
}

public class PartitionedDropTests
{
    private static async Task<List<SourceRecord>> ReadAllAsync(DropReader reader, Drop drop, IReadOnlyList<int>? partitions)
    {
        var read = new List<SourceRecord>();
        await foreach (var record in reader.ReadRecordsAsync(drop, partitions))
        {
            read.Add(record);
        }

        return read;
    }

    [Fact]
    public async Task Partitioned_drops_merge_join_each_partition_in_key_order()
    {
        var dir = Samples.NewTempDirectory();
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var manifest = await SampleDropBuilder.WriteAsync(dir, "STAT_COMP", records, Guid.NewGuid(), 1, partitions: 2);
        Assert.True(manifest.Partitioned);
        Assert.Equal(2, manifest.PartitionCount);
        Assert.Equal(2, manifest.Scopes["curves"].Files.Count);

        var reader = new DropReader(Samples.Stores());
        var drop = await reader.OpenAsync(dir, "manifest.json");
        var all = await ReadAllAsync(reader, drop, null);
        Assert.Equal(3, all.Count);
        foreach (var record in records)
        {
            var read = all.Single(r => r.DeclaredDeliveryKey == record.Key.Value);
            Assert.Equal(record.Curves.Select(c => c.CurveId).Prepend(SampleDropBuilder.IndexCurveId), read.ScopeRows("curves").Select(c => c.GetString("curve_id")));
        }

        var first = await ReadAllAsync(reader, drop, [0]);
        var second = await ReadAllAsync(reader, drop, [1]);
        Assert.Equal(3, first.Count + second.Count);
        Assert.True(first.Count >= 1 && second.Count >= 1);
        Assert.Empty(first.Select(r => r.DeclaredDeliveryKey).Intersect(second.Select(r => r.DeclaredDeliveryKey)));

        await Assert.ThrowsAsync<FlowValidationException>(() => ReadAllAsync(reader, drop, [5]));
    }

    [Fact]
    public async Task Unsorted_partitioned_files_are_rejected()
    {
        var dir = Samples.NewTempDirectory();
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        await SampleDropBuilder.WriteAsync(dir, "STAT_COMP", records, Guid.NewGuid(), 1, partitions: 2);

        // Rewrite partition 0 of the root scope (two rows) in reverse key order.
        var path = Path.Combine(dir, "metadata", "part-00000.parquet");
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        IReadOnlyList<string> names;
        await using (var stream = File.OpenRead(path))
        {
            names = await ParquetScopeReader.ReadColumnsAsync(stream);
            stream.Position = 0;
            await foreach (var row in ParquetScopeReader.ReadRowsAsync(stream, null))
            {
                rows.Add(names.ToDictionary(n => n, n => row.Get(n), StringComparer.Ordinal));
            }
        }

        Assert.Equal(2, rows.Count);
        rows.Reverse();
        var columns = names.Select(n => (n, rows.Select(r => r[n]).FirstOrDefault(v => v is not null)?.GetType() ?? typeof(string))).ToList();
        await using (var stream = File.Create(path))
        {
            await ParquetScopeReader.WriteAsync(stream, columns, rows);
        }

        var reader = new DropReader(Samples.Stores());
        var drop = await reader.OpenAsync(dir, "manifest.json");
        var ex = await Assert.ThrowsAsync<FlowValidationException>(() => ReadAllAsync(reader, drop, null));
        Assert.Contains("sorted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unpartitioned_drops_join_through_the_spill_and_partitions_restrict_the_roots()
    {
        var dir = Samples.NewTempDirectory();
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        await SampleDropBuilder.WriteAsync(dir, "STAT_COMP", records, Guid.NewGuid(), 1);
        var reader = new DropReader(Samples.Stores());
        var drop = await reader.OpenAsync(dir, "manifest.json");
        Assert.False(drop.Manifest.Partitioned);

        var all = await ReadAllAsync(reader, drop, null);
        Assert.Equal(3, all.Count);
        Assert.All(all, r => Assert.NotEmpty(r.ScopeRows("curves")));
        var only = await ReadAllAsync(reader, drop, [0]);
        Assert.Equal(3, only.Count);
    }
}
