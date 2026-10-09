using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ledger keyed by partition (osdu/docs/reference/concepts/ledger.md, Ledgers, flows and partitions): the directory
/// that says which partition each ledger belongs to, the registration a run makes before it writes and the refusal of
/// another partition, the adoption of a ledger the upgrade could not place, and the reads across ledgers that name
/// their partition, each in the test database's emptied module schema.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class LedgerPartitionKeyTests : IDisposable
{
    private const string Dev = "dev";

    private const string Test = "test";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _dev = FlowId.Of("partition-keys-dev");
    private readonly Guid _test = FlowId.Of("partition-keys-test");

    private OsduLedger Ledger => _db.Ledger(_clock);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static LedgerEntry Entry(Guid flowId, string partition, string name) => new()
    {
        FlowId = flowId,
        Partition = partition,
        Kind = LedgerKinds.Delivery,
        FlowName = name,
        Interface = string.Empty,
        LedgerName = name,
    };

    private static RecordState Pending(Guid flowId, DeliveryKey key, string sourceKey, string targetId) => new()
    {
        DeliveryKey = key,
        FlowId = flowId,
        SourceKey = sourceKey,
        Label = sourceKey,
        MappingName = "Wellbore",
        TargetId = targetId,
        LastSubmissionId = Guid.NewGuid(),
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingMetadataHash = "mh",
        PendingMetadata = true,
    };

    [Fact]
    public async Task A_ledger_is_registered_once_and_every_later_registration_confirms_it()
    {
        var first = await Ledger.RegisterLedgerAsync(Entry(_dev, Dev, "wells"));
        var again = await Ledger.RegisterLedgerAsync(Entry(_dev, "DEV", "wells"));

        Assert.Equal((Dev, LedgerKinds.Delivery, "wells"), (first.Partition, first.Kind, first.LedgerName));
        Assert.Equal(first.RegisteredUtc, again.RegisteredUtc);
        Assert.Equal(Dev, (await Ledger.GetLedgerAsync(_dev))!.Partition);
        Assert.Equal([_dev], (await Ledger.ListLedgersAsync(Dev)).Select(l => l.FlowId));
        Assert.Empty(await Ledger.ListLedgersAsync(Test));
        Assert.Null(await Ledger.GetLedgerAsync(_test));
        Assert.Equal(1L, await _db.CreateDbContext().DeliveryLedgerPartitions.LongCountAsync());
    }

    [Fact]
    public async Task A_registration_naming_another_partition_is_refused_naming_both_and_changes_nothing()
    {
        await Ledger.RegisterLedgerAsync(Entry(_dev, Dev, "wells"));

        var refused = await Assert.ThrowsAsync<DeliveryException>(() => Ledger.RegisterLedgerAsync(Entry(_dev, Test, "wells")));
        Assert.Contains($"belongs to partition '{Dev}', and this run delivers to '{Test}'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing ran.", refused.Message, StringComparison.Ordinal);
        Assert.Equal(Dev, (await Ledger.GetLedgerAsync(_dev))!.Partition);

        var invalid = await Assert.ThrowsAsync<DeliveryException>(() => Ledger.RegisterLedgerAsync(Entry(_test, "${env:OSDU_PARTITION}", "wells")));
        Assert.Contains("no data-partition-id", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_to_a_ledger_no_run_registered_is_refused_and_a_read_of_it_answers_empty()
    {
        var key = DeliveryKey.Derive("wells", ["A"]);

        var refused = await Assert.ThrowsAsync<DeliveryException>(() => Ledger.UpsertPendingAsync(_dev, [Pending(_dev, key, "A", "dev:master-data--Wellbore:a")]));
        Assert.Contains("is not in the ledger's directory", refused.Message, StringComparison.Ordinal);

        Assert.Null(await Ledger.GetRecordAsync(_dev, key));
        Assert.Equal(0, (await Ledger.StatsAsync(_dev, Now)).Total);
        Assert.Empty(await Ledger.ListSubmissionsAsync(_dev, 10));
        Assert.Empty(await Ledger.ListActivitiesAsync(new ActivityQuery { FlowId = _dev }));
        Assert.Empty(await Ledger.LookupAsync(key.Value.ToString(), 10));
    }

    [Fact]
    public async Task One_row_in_two_partitions_is_two_records_each_read_in_its_own_partition()
    {
        await Ledger.RegisterAsync(Dev, _dev);
        await Ledger.RegisterAsync(Test, _test);
        var key = DeliveryKey.Derive("wells", ["WB A/1-19"]);
        await Ledger.UpsertPendingAsync(_dev, [Pending(_dev, key, "WB A/1-19", "dev:master-data--Wellbore:WB-A-1-19")]);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Ledger.UpsertPendingAsync(_test, [Pending(_test, key, "WB A/1-19", "test:master-data--Wellbore:WB-A-1-19")]);

        // Each record names its partition, read one flow at a time or across flows.
        Assert.Equal(Dev, (await Ledger.GetRecordAsync(_dev, key))!.Partition);
        Assert.Equal(Test, (await Ledger.GetRecordAsync(_test, key))!.Partition);

        // A partition is a filter on every read across ledgers; with none, each partition is read and the rows merged.
        Assert.Equal([_dev], (await Ledger.LookupAsync(key.Value.ToString(), 10, partition: Dev)).Select(r => r.FlowId));
        Assert.Equal([_test], (await Ledger.LookupAsync("WB A/1", 10, partition: Test)).Select(r => r.FlowId));
        Assert.Equal([_test, _dev], (await Ledger.ListRecentAsync(10)).Select(r => r.FlowId));
        Assert.Equal([Test, Dev], (await Ledger.ListRecentAsync(10)).Select(r => r.Partition));
        Assert.Equal([_dev], (await Ledger.ListRecentAsync(10, partition: Dev)).Select(r => r.FlowId));
        Assert.Equal(2, (await Ledger.CountRecentAsync(100)).Count);
        Assert.Equal(1, (await Ledger.CountRecentAsync(100, partition: Test)).Count);
        Assert.Equal(1, (await Ledger.CountLookupAsync("WB A/1", 100, partition: Dev)).Count);
        Assert.Empty(await Ledger.ListRecentAsync(10, partition: "prod"));

        // A flow and a partition named together are both filters: a ledger of another partition reads empty.
        Assert.Single(await Ledger.ListRecentAsync(10, flowId: _dev, partition: Dev));
        Assert.Empty(await Ledger.ListRecentAsync(10, flowId: _dev, partition: Test));
        Assert.Empty(await Ledger.LookupAsync(key.Value.ToString(), 10, flowId: _test, partition: Dev));

        // An OSDU id is held within its partition: each ledger's partition answers for its own ids only.
        Assert.Equal(["dev:master-data--Wellbore:WB-A-1-19"], await Ledger.HeldIdsAsync(_dev, ["dev:master-data--Wellbore:WB-A-1-19", "test:master-data--Wellbore:WB-A-1-19"]));
        Assert.Equal(["test:master-data--Wellbore:WB-A-1-19"], await Ledger.HeldIdsAsync(_test, ["dev:master-data--Wellbore:WB-A-1-19", "test:master-data--Wellbore:WB-A-1-19"]));
        Assert.Equal(_test, Assert.Single(await Ledger.ListHoldersAsync(_test, "test:master-data--Wellbore:WB-A-1-19", 10)).FlowId);
        Assert.Empty(await Ledger.ListHoldersAsync(_dev, "test:master-data--Wellbore:WB-A-1-19", 10));

        // The directory lists each partition's ledgers.
        Assert.Equal([_dev], (await Ledger.ListLedgersAsync(Dev)).Select(l => l.FlowId));
        Assert.Equal(2, (await Ledger.ListLedgersAsync(null)).Count);
    }

    [Fact]
    public async Task The_trail_leaves_out_the_runs_that_changed_nothing_when_asked_and_counts_them_apart()
    {
        await Ledger.RegisterAsync(Dev, _dev);
        await Ledger.RegisterAsync(Test, _test);
        async Task RunAsync(Guid flow, string name, string outcome, bool idle)
        {
            var started = await Ledger.StartActivityAsync(new ActivityRecord { FlowId = flow, FlowName = name, Kind = "deliver", Actor = "schedule:wells", StartedUtc = Now });
            Assert.False(started.Idle);
            await Ledger.CompleteActivityAsync(started.ActivityId, outcome, idle ? "nothing to deliver" : "3 delivered", null, Now, idle: idle);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        await RunAsync(_dev, "wells@dev", "completed", idle: false);
        await RunAsync(_dev, "wells@dev", "completed", idle: true);
        await RunAsync(_dev, "wells@dev", "completed", idle: true);
        // A run that failed is never idle, whatever it says of itself: the trail always shows it.
        await RunAsync(_dev, "wells@dev", "failed", idle: true);
        await RunAsync(_test, "wells@test", "completed", idle: true);

        var busy = await Ledger.ListActivitiesAsync(new ActivityQuery { Partition = Dev, Idle = false });
        Assert.Equal(["failed", "completed"], busy.Select(a => a.Outcome));
        Assert.All(busy, a => Assert.False(a.Idle));
        Assert.False((await Ledger.GetActivityAsync(busy[0].ActivityId))!.Idle);
        var quiet = await Ledger.ListActivitiesAsync(new ActivityQuery { Partition = Dev, Idle = true });
        Assert.Equal(2, quiet.Count);
        Assert.All(quiet, a => Assert.Equal(("completed", true, "nothing to deliver"), (a.Outcome, a.Idle, a.Summary)));
        Assert.Equal(2, await Ledger.CountActivitiesAsync(new ActivityQuery { Partition = Dev, Idle = true }));
        Assert.Equal(4, await Ledger.CountActivitiesAsync(new ActivityQuery { Partition = Dev }));

        // Across partitions, and beside the other filters, it narrows the same way.
        Assert.Equal(3, await Ledger.CountActivitiesAsync(new ActivityQuery { Idle = true }));
        Assert.Equal(3, (await Ledger.ListActivitiesAsync(new ActivityQuery { Idle = true })).Count);
        Assert.Single(await Ledger.ListActivitiesAsync(new ActivityQuery { Idle = false, Outcome = "completed" }));
        Assert.Single(await Ledger.ListActivitiesAsync(new ActivityQuery { FlowId = _test, Idle = true }));
        Assert.Empty(await Ledger.ListActivitiesAsync(new ActivityQuery { FlowId = _test, Idle = false }));
    }

    [Fact]
    public async Task The_audit_trail_and_the_submissions_are_read_per_partition_and_merged_across_them()
    {
        await Ledger.RegisterAsync(Dev, _dev);
        await Ledger.RegisterAsync(Test, _test);
        for (var i = 0; i < 3; i++)
        {
            foreach (var (flow, name) in new[] { (_dev, "wells@dev"), (_test, "wells@test") })
            {
                await Ledger.StartActivityAsync(new ActivityRecord { FlowId = flow, FlowName = name, Kind = "deliver", Actor = "user:tahir", StartedUtc = Now });
                await Ledger.RegisterSubmissionAsync(new SubmissionState
                {
                    SubmissionId = Guid.NewGuid(),
                    FlowId = flow,
                    FlowName = name,
                    MappingReference = "Wellbore@1.0.0",
                    RenderContext = "{}",
                    SourceConnection = "${env:OSDU_DATA_DB}",
                    SourceObject = "OsduData.arc.Wellbore",
                    RecordCount = 1,
                    ReceivedUtc = Now,
                });
                _clock.Advance(TimeSpan.FromMinutes(1));
            }
        }

        var dev = await Ledger.ListActivitiesAsync(new ActivityQuery { Partition = Dev, Max = 10 });
        Assert.Equal(3, dev.Count);
        Assert.All(dev, a => Assert.Equal((_dev, Dev), (a.FlowId, a.Partition)));
        Assert.Equal(3, await Ledger.CountActivitiesAsync(new ActivityQuery { Partition = Test }));

        // Across partitions the trail is every partition's, newest first, and a page reaches past one partition's rows.
        var every = await Ledger.ListActivitiesAsync(new ActivityQuery { Max = 4 });
        Assert.Equal([Test, Dev, Test, Dev], every.Select(a => a.Partition));
        Assert.True(every.Zip(every.Skip(1)).All(p => p.First.StartedUtc >= p.Second.StartedUtc));
        var second = await Ledger.ListActivitiesAsync(new ActivityQuery { Max = 4, Offset = 4 });
        Assert.Equal([Test, Dev], second.Select(a => a.Partition));
        Assert.Equal(6, await Ledger.CountActivitiesAsync(new ActivityQuery()));

        // A flow and a partition together are both filters.
        Assert.Equal(3, (await Ledger.ListActivitiesAsync(new ActivityQuery { FlowId = _test, Partition = Test })).Count);
        Assert.Empty(await Ledger.ListActivitiesAsync(new ActivityQuery { FlowId = _test, Partition = Dev }));

        Assert.All(await Ledger.ListSubmissionsAsync(null, 10, Test), s => Assert.Equal((_test, Test), (s.FlowId, s.Partition)));
        var submissions = await Ledger.ListSubmissionsAsync(null, 4);
        Assert.Equal([Test, Dev, Test, Dev], submissions.Select(s => s.Partition));
        Assert.Empty(await Ledger.ListSubmissionsAsync(_dev, 10, Test));
        Assert.Equal(3, (await Ledger.ListSubmissionsAsync(_dev, 10, Dev)).Count);
    }

    [Fact]
    public async Task A_ledger_the_upgrade_could_not_place_is_adopted_by_its_first_registration_with_every_row()
    {
        var keys = await SeedUnassignedAsync(_dev, "dev", 5);
        var sliced = new OsduLedger(_db.CreateDbContext, _clock) { WriteSlice = 2 };
        Assert.Null((await sliced.GetLedgerAsync(_dev))!.Partition);

        // Unassigned, its rows are still read in its own place.
        Assert.Equal(5, (await sliced.StatsAsync(_dev, Now)).Total);

        var adopted = await sliced.RegisterLedgerAsync(Entry(_dev, Dev, "wells"));
        Assert.Equal(Dev, adopted.Partition);

        await using var db = _db.CreateDbContext();
        var devId = await db.DeliveryLedgerPartitions.Where(p => p.Name == Dev).Select(p => p.PartitionId).SingleAsync();
        Assert.Equal(devId, await db.DeliveryLedgers.Where(l => l.FlowId == _dev).Select(l => l.PartitionId).SingleAsync());
        Assert.All(await db.DeliveryRecords.Where(r => r.FlowId == _dev).Select(r => r.PartitionId).ToListAsync(), p => Assert.Equal(devId, p));
        Assert.All(await db.DeliveryActivities.Where(a => a.FlowId == _dev).Select(a => a.PartitionId).ToListAsync(), p => Assert.Equal(devId, p));
        Assert.Equal(0, await db.DeliveryRecords.CountAsync(r => r.PartitionId == DeliveryModel.UnassignedPartition));

        // Adopted, it reads and writes as any ledger of its partition.
        Assert.Equal(Dev, (await Ledger.GetRecordAsync(_dev, keys[0]))!.Partition);
        Assert.Equal(5, (await Ledger.ListRecentAsync(10, partition: Dev)).Count);
        Assert.Equal(1, (await Ledger.UpsertPendingAsync(_dev, [Pending(_dev, DeliveryKey.Derive("wells", ["new"]), "new", "dev:master-data--Wellbore:new")])).Staged);
    }

    [Fact]
    public async Task A_ledger_the_upgrade_could_not_place_is_not_adopted_into_another_partition_than_its_records_went_to()
    {
        await SeedUnassignedAsync(_test, "test", 2);

        var refused = await Assert.ThrowsAsync<DeliveryException>(() => Ledger.RegisterLedgerAsync(Entry(_test, Dev, "wells")));
        Assert.Contains("2 record(s) delivered to 'test'", refused.Message, StringComparison.Ordinal);
        Assert.Null((await Ledger.GetLedgerAsync(_test))!.Partition);
        await using var db = _db.CreateDbContext();
        Assert.Equal(2, await db.DeliveryRecords.CountAsync(r => r.FlowId == _test && r.PartitionId == DeliveryModel.UnassignedPartition));

        // Its own partition adopts it.
        Assert.Equal(Test, (await Ledger.RegisterLedgerAsync(Entry(_test, Test, "wells"))).Partition);
    }

    [Fact]
    public async Task The_identity_backfill_walks_every_partition_in_key_order_and_resumes_from_its_cursor()
    {
        await Ledger.RegisterAsync(Dev, _dev);
        await Ledger.RegisterAsync(Test, _test);
        foreach (var (flow, partition, count) in new[] { (_dev, Dev, 3), (_test, Test, 2) })
        {
            await Ledger.UpsertPendingAsync(flow, Enumerable.Range(0, count)
                .Select(i => Pending(flow, DeliveryKey.Derive("wells", [$"{partition}-{i}"]), $"WELL-{partition}-{i}", $"{partition}:master-data--Wellbore:{i}"))
                .ToList());
        }

        await using (var db = _db.CreateDbContext())
        {
            await db.DeliveryRecordIdentities.ExecuteDeleteAsync();
        }

        Assert.Empty(await Ledger.LookupAsync("WELL-test-1", 10));
        var written = 0;
        RecordCursor? after = null;
        var partitions = new List<short>();
        for (var pass = 0; pass < 10; pass++)
        {
            var (last, count, _) = await Ledger.BackfillIdentitiesAsync(after, 2);
            if (last is null)
            {
                break;
            }

            partitions.Add(last.Value.PartitionId);
            written += count;
            after = last;
        }

        // Every record of both partitions was indexed once, the partitions walked in key order.
        Assert.Equal(5, written);
        Assert.Equal(partitions.Order(), partitions);
        Assert.Equal(_test, Assert.Single(await Ledger.LookupAsync("WELL-test-1", 10, partition: Test)).FlowId);
        Assert.Empty(await Ledger.LookupAsync("WELL-test-1", 10, partition: Dev));
    }

    /// <summary>
    /// A ledger as the upgrade leaves one it could not place: its directory row and its rows under the unassigned partition,
    /// with <paramref name="count"/> records delivered to <paramref name="partition"/> and one activity.
    /// </summary>
    private async Task<IReadOnlyList<DeliveryKey>> SeedUnassignedAsync(Guid flowId, string partition, int count)
    {
        await using var db = _db.CreateDbContext();
        db.DeliveryLedgers.Add(new DeliveryLedger
        {
            PartitionId = DeliveryModel.UnassignedPartition,
            FlowId = flowId,
            Kind = LedgerKinds.Delivery,
            FlowName = "wells",
            Interface = string.Empty,
            LedgerName = "wells",
            RegisteredUtc = Now,
        });
        var keys = new List<DeliveryKey>();
        for (var i = 0; i < count; i++)
        {
            var key = DeliveryKey.Derive("seeded", [$"{flowId:N}-{i}"]);
            keys.Add(key);
            var id = $"{partition}:master-data--Wellbore:{flowId:N}-{i}";
            db.DeliveryRecords.Add(new DeliveryRecord
            {
                PartitionId = DeliveryModel.UnassignedPartition,
                FlowId = flowId,
                DeliveryKey = key.Value,
                SourceKey = $"seeded-{i}",
                MappingName = "Wellbore",
                Status = "delivered",
                TargetId = id,
                ClaimedTargetId = id,
                CreatedUtc = Now,
                UpdatedUtc = Now,
            });
        }

        db.DeliveryActivities.Add(new DeliveryActivity
        {
            PartitionId = DeliveryModel.UnassignedPartition,
            FlowId = flowId,
            FlowName = "wells",
            Kind = "deliver",
            Actor = "user:tahir",
            StartedUtc = Now,
            Outcome = "completed",
        });
        await db.SaveChangesAsync();
        return keys;
    }

    public void Dispose() => _db.Dispose();
}
