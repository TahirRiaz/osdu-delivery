using System.Data.SqlTypes;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What the ledger keeps so a record removed from OSDU comes back (osdu/docs/reference/concepts/removal-and-reversal.md,
/// Removing records from OSDU, and Deleting removed records from the ledger): an operator's removal asks for the record to
/// be planned again, and a deletion from the ledger leaves the request on the line it keeps, listed with the records' own
/// requests in one order.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class RemovedRecordLedgerTests : IAsyncLifetime, IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("test-flow");

    private OsduLedger Ledger => _db.Ledger(_clock);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public Task InitializeAsync() => Ledger.RegisterAsync(_flow);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_operator_s_removal_asks_for_the_record_again_and_one_that_asks_for_nothing_leaves_what_it_carried()
    {
        var asked = await DeliveredAsync("a");
        var quiet = await DeliveredAsync("b");

        await Ledger.MarkRemovedAsync(_flow, [asked], RemovalScope.Record, "gui:tahir", Now);
        await Ledger.MarkRemovedAsync(_flow, [quiet], RemovalScope.Everything, "inventory", Now, deliverAgain: false);

        Assert.Equal(Now, (await Ledger.GetRecordAsync(_flow, asked))!.PlanRequestedUtc);
        Assert.Null((await Ledger.GetRecordAsync(_flow, quiet))!.PlanRequestedUtc);
        var attempt = Assert.Single(await Ledger.ListAttemptsAsync(_flow, asked, 10), a => a.Phase == "delete");
        Assert.Contains("next run reads its row again and delivers it", attempt.ResultJson!, StringComparison.Ordinal);

        // An inventory removing the id again later makes OSDU agree with the ledger, and leaves the operator's request as it was.
        var requested = Now;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Ledger.MarkRemovedAsync(_flow, [asked], RemovalScope.Record, "inventory", Now, deliverAgain: false);
        Assert.Equal(requested, (await Ledger.GetRecordAsync(_flow, asked))!.PlanRequestedUtc);
    }

    [Fact]
    public async Task A_record_deleted_from_the_ledger_is_listed_with_the_records_asked_for_in_one_order_page_after_page()
    {
        var first = await DeliveredAsync("first");
        var last = await DeliveredAsync("last");
        var purged = new List<DeliveryKey>();
        var sameMoment = new List<DeliveryKey>();
        for (var i = 0; i < 4; i++)
        {
            purged.Add(await DeliveredAsync("purged-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var alongside = await DeliveredAsync("alongside");

        // Asked for at three moments: one record before, four deleted from the ledger and one record at the same moment, one after.
        await Ledger.MarkRemovedAsync(_flow, [first], RemovalScope.Record, "gui:tahir", Now);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var middle = Now;
        await Ledger.MarkRemovedAsync(_flow, [.. purged, alongside], RemovalScope.Record, "gui:tahir", Now);
        Assert.Equal(4, (await Ledger.PurgeRecordsAsync(_flow, purged, "gui:tahir", null, Now)).Count);
        sameMoment.AddRange(purged);
        sameMoment.Add(alongside);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Ledger.MarkRemovedAsync(_flow, [last], RemovalScope.Record, "gui:tahir", Now);

        var page = await Ledger.ListPlanRequestedAsync(_flow, null, 100);

        // By the moment, then by the key as SQL Server orders a uniqueidentifier, whichever table the request is kept in.
        var expected = new List<DeliveryKey> { first };
        expected.AddRange(sameMoment.OrderBy(k => new SqlGuid(k.Value)));
        expected.Add(last);
        Assert.Equal(expected, page.Select(r => r.DeliveryKey));
        Assert.All(page, r => Assert.Equal(purged.Contains(r.DeliveryKey), r.Purged));
        Assert.All(page.Where(r => r.Purged), r => Assert.Equal(middle, r.RequestedUtc));
        Assert.All(page, r => Assert.NotNull(r.SourceKeyJson));

        // Walked one at a time, the cursor of either table leads to the next request of both.
        var walked = new List<PlanRequestedRecord>();
        PlanRequestedRecord? after = null;
        while ((await Ledger.ListPlanRequestedAsync(_flow, after, 1)) is [var next])
        {
            walked.Add(next);
            after = next;
        }

        Assert.Equal(page, walked);
    }

    [Fact]
    public async Task Answering_takes_only_a_deleted_record_s_request_still_standing_as_it_was_read()
    {
        var key = await DeliveredAsync("a");
        await Ledger.MarkRemovedAsync(_flow, [key], RemovalScope.Record, "gui:tahir", Now);
        Assert.Single(await Ledger.PurgeRecordsAsync(_flow, [key], "gui:tahir", null, Now));
        var request = Assert.Single(await Ledger.ListPlanRequestedAsync(_flow, null, 10));
        Assert.Equal(Now, (await Ledger.FindPurgedAsync(_flow, key))!.PlanRequestedUtc);

        // A request read at another moment, or a record's own request, answers nothing here.
        Assert.Equal(0, await Ledger.AnswerPurgedRequestsAsync(_flow, [request with { RequestedUtc = Now.AddSeconds(-1) }]));
        Assert.Equal(0, await Ledger.AnswerPurgedRequestsAsync(_flow, [request with { Purged = false }]));
        Assert.Single(await Ledger.ListPlanRequestedAsync(_flow, null, 10));

        Assert.Equal(1, await Ledger.AnswerPurgedRequestsAsync(_flow, [request]));
        Assert.Empty(await Ledger.ListPlanRequestedAsync(_flow, null, 10));
        Assert.Null((await Ledger.FindPurgedAsync(_flow, key))!.PlanRequestedUtc);
        Assert.Equal(0, await Ledger.AnswerPurgedRequestsAsync(_flow, [request]));
    }

    [Fact]
    public async Task A_record_deleted_again_moves_its_request_to_the_new_line_so_its_row_is_read_once()
    {
        var key = await DeliveredAsync("a");
        await Ledger.MarkRemovedAsync(_flow, [key], RemovalScope.Record, "gui:tahir", Now);
        Assert.Single(await Ledger.PurgeRecordsAsync(_flow, [key], "gui:tahir", null, Now));

        // Made a record again before a run read the line (a run scoped to it, say), then removed and deleted once more.
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(key, await DeliveredAsync("a"));
        await Ledger.MarkRemovedAsync(_flow, [key], RemovalScope.Record, "gui:tahir", Now);
        Assert.Single(await Ledger.PurgeRecordsAsync(_flow, [key], "gui:tahir", null, Now));

        var request = Assert.Single(await Ledger.ListPlanRequestedAsync(_flow, null, 10));
        Assert.Equal((key, Now, true), (request.DeliveryKey, request.RequestedUtc, request.Purged));
        await using var db = _db.CreateDbContext();
        var lines = await db.DeliveryPurgedRecords.Where(p => p.FlowId == _flow && p.DeliveryKey == key.Value).OrderBy(p => p.PurgedRecordId).ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.Null(lines[0].PlanRequestedUtc);
        Assert.Equal(Now, lines[1].PlanRequestedUtc);
        Assert.Equal(KeyJson("a"), lines[1].SourceKeyJson);
    }

    [Fact]
    public async Task A_record_a_reversal_keeps_out_or_one_with_no_key_tuple_asks_for_nothing_when_deleted_from_the_ledger()
    {
        var reversed = await DeliveredAsync("reversed");
        var keyless = await DeliveredAsync("keyless", keyJson: null);
        await Ledger.MarkRemovedAsync(_flow, [reversed, keyless], RemovalScope.Record, "gui:tahir", Now);
        await using (var db = _db.CreateDbContext())
        {
            // As a reversal of the run that created it leaves a record: deleted, and blocked so no run sends it back.
            var record = await db.DeliveryRecords.SingleAsync(r => r.FlowId == _flow && r.DeliveryKey == reversed.Value);
            record.Blocked = true;
            record.PlanRequestedUtc = null;
            await db.SaveChangesAsync();
        }

        Assert.Equal(2, (await Ledger.PurgeRecordsAsync(_flow, [reversed, keyless], "gui:tahir", null, Now)).Count);

        Assert.Null((await Ledger.FindPurgedAsync(_flow, reversed))!.PlanRequestedUtc);
        Assert.Null((await Ledger.FindPurgedAsync(_flow, keyless))!.PlanRequestedUtc);
        Assert.Empty(await Ledger.ListPlanRequestedAsync(_flow, null, 10));
    }

    [Fact]
    public async Task Deleting_the_ledger_leaves_no_request_to_read_a_row_again_and_answers_the_ones_deletions_left()
    {
        var deletedBefore = await DeliveredAsync("before");
        var rest = await DeliveredAsync("rest");
        await Ledger.MarkRemovedAsync(_flow, [deletedBefore], RemovalScope.Record, "gui:tahir", Now);
        Assert.Single(await Ledger.PurgeRecordsAsync(_flow, [deletedBefore], "gui:tahir", null, Now));
        Assert.Single(await Ledger.ListPlanRequestedAsync(_flow, null, 10));
        _clock.Advance(TimeSpan.FromMinutes(10));

        var deletion = await Ledger.DeleteLedgerAsync(_flow, "gui:tahir", null, Now);

        Assert.Equal(1, deletion.Records);
        Assert.Empty(await Ledger.ListPlanRequestedAsync(_flow, null, 10));
        await using var db = _db.CreateDbContext();
        Assert.False(await db.DeliveryPurgedRecords.AnyAsync(p => p.FlowId == _flow && p.PlanRequestedUtc != null));
        Assert.Equal(KeyJson("rest"), (await db.DeliveryPurgedRecords.SingleAsync(p => p.FlowId == _flow && p.DeliveryKey == rest.Value)).SourceKeyJson);
    }

    private static string KeyJson(string sourceKey) => JsonSerializer.Serialize(new[] { sourceKey });

    /// <summary>A record staged with its key tuple, claimed and delivered, so a removal has something real to act on.</summary>
    private async Task<DeliveryKey> DeliveredAsync(string sourceKey, string? keyJson = "")
    {
        var key = DeliveryKey.Derive("test", [sourceKey]);
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [new RecordState
        {
            DeliveryKey = key,
            FlowId = _flow,
            SourceKey = sourceKey,
            SourceKeyJson = keyJson == string.Empty ? KeyJson(sourceKey) : keyJson,
            MappingName = "Thing",
            TargetId = "dev:x:" + sourceKey,
            LastSubmissionId = submission,
            PendingDocumentRef = "0:0:10",
            PendingRenderContext = "{}",
            PendingSourceFingerprint = "fp",
            PendingMetadataHash = "mh",
            PendingMetadata = true,
        }]);
        var claimed = (await Ledger.ClaimAsync(_flow, submission, "w", 10, TimeSpan.FromMinutes(5), Now)).Records;
        var record = claimed.Single(r => r.DeliveryKey == key);
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = key,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 42,
            TargetId = record.TargetId,
            Attempt = new AttemptRecord
            {
                DeliveryKey = key,
                SubmissionId = submission,
                Worker = "w",
                StartedUtc = Now,
                CompletedUtc = Now,
                Outcome = AttemptOutcome.Delivered,
                Phase = "metadata",
                TargetVersion = 42,
            },
        });
        return key;
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The runs that bring removed records back: the next deliver run reads the rows of records removed from OSDU by key,
/// whether their history is still in the ledger or only the line kept of them, while its own incremental read meets no row;
/// a deleted record whose row is gone is not read again, and one outside the run's scope waits for the run of its own.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class RemovedRecordRunTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task A_record_removed_from_OSDU_is_delivered_again_by_the_next_run_with_or_without_its_ledger_history()
    {
        var (runtime, ledger, protocol, _) = await DeliveredAsync();
        using (runtime)
        {
            var kept = SampleEstate.Key(0);
            var deleted = SampleEstate.Key(1);
            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([kept]), RemovalChoice.Record)).Removed);
            var removal = await runtime.RemoveAsync(RemovalSelection.Of([deleted]), RemovalChoice.Record, purgeLedger: true);
            Assert.Equal((1, 1), (removal.Removed, removal.Purged));
            var line = (await ledger.FindPurgedAsync(runtime.Flow.Id, deleted))!;
            Assert.NotNull(line.PlanRequestedUtc);
            Assert.Null(await ledger.GetRecordAsync(runtime.Flow.Id, deleted));

            // No row changed, so the run's own read meets none: both are read by key and delivered again, under their ids.
            await RunAsync(runtime);

            Assert.Equal(2, protocol.Deliveries.Count);
            Assert.Equal(new[] { kept, deleted }.ToHashSet(), protocol.Deliveries.Select(d => d.Key).ToHashSet());
            Assert.Equal(line.TargetId, protocol.Deliveries.Single(d => d.Key == deleted).TargetId);
            foreach (var key in new[] { kept, deleted })
            {
                var record = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!;
                Assert.Equal((RecordStatus.Delivered, (DateTime?)null), (record.Status, record.PlanRequestedUtc));
            }

            Assert.Empty(await ledger.ListPlanRequestedAsync(runtime.Flow.Id, null, 10));
            Assert.Null((await ledger.FindPurgedAsync(runtime.Flow.Id, deleted))!.PlanRequestedUtc);

            // Back in OSDU and in the ledger: the run after sends nothing.
            protocol.Deliveries.Clear();
            await RunAsync(runtime);
            Assert.Empty(protocol.Deliveries);
        }
    }

    [Fact]
    public async Task A_deleted_record_whose_row_is_gone_is_not_read_again_and_one_outside_the_scope_waits_for_the_run_of_its_own()
    {
        var (runtime, ledger, protocol, engine) = await DeliveredAsync();
        using (runtime)
        {
            var gone = SampleEstate.Key(0);
            var waiting = SampleEstate.Key(1);
            Assert.Equal(2, (await runtime.RemoveAsync(RemovalSelection.Of([gone, waiting]), RemovalChoice.Record, purgeLedger: true)).Purged);
            await using (var db = _db.CreateDbContext())
            {
                // The line of one names a key the ingestion table holds no row for: its row was deleted from the table.
                await db.DeliveryPurgedRecords.Where(p => p.DeliveryKey == gone.Value)
                    .ExecuteUpdateAsync(set => set.SetProperty(p => p.SourceKeyJson, "[\"NO_15_9\",\"L-9999\"]"));
            }

            // A run of another scope reads both by key and plans neither: the gone row's request is answered, the other stays.
            var values = new Dictionary<string, string>(SampleEstate.Values, StringComparer.Ordinal) { ["logSource"] = "ELSEWHERE" };
            using (var elsewhere = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), values))
            {
                var passes = await DeliveryExecutor.DeliverRequestedAsync(
                    elsewhere, SourceSelection.Full(), DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
                Assert.Equal((2L, 0L), (passes.Read, passes.Planned));
            }

            Assert.Empty(protocol.Deliveries);
            var left = Assert.Single(await ledger.ListPlanRequestedAsync(runtime.Flow.Id, null, 10));
            Assert.Equal((waiting, true), (left.DeliveryKey, left.Purged));
            Assert.Null((await ledger.FindPurgedAsync(runtime.Flow.Id, gone))!.PlanRequestedUtc);

            // The run of its own scope delivers it, and the gone one is not read again.
            var own = await DeliveryExecutor.DeliverRequestedAsync(
                runtime, SourceSelection.Full(), DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
            Assert.Equal(1L, own.Read);
            Assert.Equal(waiting, Assert.Single(protocol.Deliveries).Key);
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(runtime.Flow.Id, waiting))!.Status);
            Assert.Null(await ledger.GetRecordAsync(runtime.Flow.Id, gone));
            Assert.Empty(await ledger.ListPlanRequestedAsync(runtime.Flow.Id, null, 10));
        }
    }

    [Fact]
    public async Task After_deleting_the_ledger_every_row_is_delivered_once_whatever_deletions_asked_for_before()
    {
        var (runtime, ledger, protocol, _) = await DeliveredAsync();
        using (runtime)
        {
            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Record, purgeLedger: true)).Purged);
            protocol.Deletes.Clear();
            _clock.Advance(TimeSpan.FromMinutes(10));

            var summary = await runtime.DeleteLedgerAsync((await ledger.GetLedgerAsync(runtime.Flow.Id))!.Partition!);

            Assert.Equal(SampleEstate.Logs().Count - 1, summary.Deleted.Records);
            Assert.Empty(await ledger.ListPlanRequestedAsync(runtime.Flow.Id, null, 10));

            await RunAsync(runtime);

            Assert.Equal(SampleEstate.Logs().Count, protocol.Deliveries.Count);
            Assert.Equal(SampleEstate.Logs().Count, protocol.Deliveries.Select(d => d.Key).Distinct().Count());
        }
    }

    /// <summary>The sample estate delivered by a deliver run through the fake protocol, with the engine it ran on.</summary>
    private async Task<(FlowRuntime Runtime, OsduLedger Ledger, FakeProtocol Protocol, EngineContext Engine)> DeliveredAsync()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.Actor = "gui:tahir";
        await RunAsync(runtime);
        Assert.Equal(SampleEstate.Logs().Count, protocol.Deliveries.Count);
        protocol.Deliveries.Clear();
        return (runtime, ledger, protocol, engine);
    }

    private Task RunAsync(FlowRuntime runtime)
    {
        _clock.Advance(TimeSpan.FromMinutes(1));
        runtime.RunId = Guid.CreateVersion7();
        return DeliveryExecutor.ExecuteInterfaceAsync(runtime, DeliveryOperations.Deliver, DeliveryRunPayload.None, null, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a run left open on a slow machine; the temp folder is the operating system's to clear.
        }
    }
}
