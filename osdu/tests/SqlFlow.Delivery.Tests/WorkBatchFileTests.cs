using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The work batch files the intake writes and the drains read back (docs/delivery/design.md section 16.2): a batch is
/// one JSON-lines file, written line by line as records render, and read either whole, in order, or one document at a
/// time by the reference the ledger holds. A batch of any size is never held in memory, which is what these two reads
/// prove: the sequential pass yields exactly what was written, and the range read answers one line by its offset.
/// </summary>
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

        // The range read is what a drain uses: one document, by the reference the record carries, without touching the rest.
        var one = await WorkBatchFile.ReadOneAsync(stores, root, submission, refs[17]);
        Assert.Equal(items[17].Key, one.Key);
        Assert.Equal(items[17].TargetId, one.TargetId);
        Assert.Equal(items[17].Document, one.Document);

        var last = await WorkBatchFile.ReadOneAsync(stores, root, submission, refs[49]);
        Assert.Equal(items[49].Document, last.Document);
    }
}
