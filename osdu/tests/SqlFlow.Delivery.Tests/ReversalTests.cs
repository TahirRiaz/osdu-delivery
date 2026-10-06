using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Reversals;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Reversals end to end (docs/reversal-plan.md): the sample well logs delivered through the ddms route to a stand-in OSDU
/// that keeps every version of every record, the module's database on SQL Server, and a run reversed record by record.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class ReversalTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly FakeOsduPlatform _platform = new();
    private readonly FakeOsduProtocols _protocols;

    public ReversalTests()
    {
        _protocols = new FakeOsduProtocols(_platform);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose()
    {
        _protocols.Dispose();
        _platform.Dispose();
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a run still holds open is the temporary folder's to clean up.
        }
    }

    [Fact]
    public async Task A_reversal_removes_what_a_run_created_and_restores_what_it_updated_with_its_bulk_data()
    {
        var tables = await EstateAsync();
        var late = WithheldFromTheFirstRun(tables);
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            var before = await HeldAsync(runtime, ledger, 0, 1, 2, 3);

            // The second run changes two records and creates the one the first run did not see.
            ChangeRows(tables, "HAL", 0, 1);
            Arrive(tables, late);
            var second = await DeliverAsync(runtime);
            Assert.All(new[] { 0, 1 }, i => Assert.NotEqual(before[i].Version, LatestVersion(TargetOf(runtime, i))));
            Assert.All(new[] { 0, 1 }, i => Assert.Equal(before[i].BulkUri, BulkUri(_platform.Records[TargetOf(runtime, i)])));
            var created = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(4));
            Assert.Equal(RecordStatus.Delivered, created!.Status);

            _clock.Advance(TimeSpan.FromMinutes(10));
            runtime.RunId = Guid.CreateVersion7();
            runtime.Actor = "manual:tester";
            var summary = await runtime.ReverseAsync(ReversalSource.Run(second));

            Assert.Equal(3, summary.Records);
            Assert.Equal(2, summary.Restored);
            Assert.Equal(1, summary.Removed);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(0, summary.Counts.Open);

            foreach (var i in new[] { 0, 1 })
            {
                // OSDU holds what it held before the second run, as a new version, the bulk link of that version included.
                var id = TargetOf(runtime, i);
                var latest = _platform.Records[id];
                Assert.True(JsonNode.DeepEquals(Content(VersionOf(id, before[i].Version)), Content(latest)));
                Assert.Equal(before[i].BulkUri, BulkUri(latest));

                var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(i));
                Assert.Equal(RecordStatus.Reverted, record!.Status);
                Assert.True(record.Blocked);
                Assert.Equal(latest["version"]!.GetValue<long>(), record.TargetVersion);
                Assert.Equal(before[i].MetadataHash, record.MetadataHash);
                Assert.Contains("reverted by manual:tester", record.LastError!, StringComparison.Ordinal);

                var attempt = (await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(i), 20)).First(a => a.Phase == AttemptPhases.Reverse);
                Assert.Equal(AttemptOutcome.Restored, attempt.Outcome);
                Assert.Equal(runtime.RunId, attempt.RunId);
                Assert.Equal(record.TargetVersion, attempt.TargetVersion);
                Assert.Contains("\"restoredVersion\":" + before[i].Version.ToString(CultureInfo.InvariantCulture), attempt.ResultJson!, StringComparison.Ordinal);
            }

            // What the second run created is removed again, reversibly.
            var removed = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(4));
            Assert.Equal(RecordStatus.Deleted, removed!.Status);
            Assert.True(removed.Blocked);
            Assert.Contains(created.TargetId!, _platform.Removed);
            Assert.Equal(
                AttemptOutcome.Deleted,
                (await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(4), 20)).First(a => a.Phase == AttemptPhases.Reverse).Outcome);

            // The records the second run did not touch are as they were.
            foreach (var i in new[] { 2, 3 })
            {
                var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(i));
                Assert.Equal(RecordStatus.Delivered, record!.Status);
                Assert.Equal(before[i].Version, record.TargetVersion);
                Assert.Equal(before[i].Version, LatestVersion(TargetOf(runtime, i)));
            }

            // The reversal is the ledger's: its state, its counts, and the run's activity naming every record it changed.
            var reversal = await ledger.FindReversalAsync(runtime.Flow.Id, ReversalSource.Run(second));
            Assert.Equal(ReversalStatuses.Completed, reversal!.Status);
            Assert.NotNull(reversal.CapturedUtc);
            var counts = await ledger.CountReversalAsync(reversal.ReversalId);
            Assert.Equal(2, counts.Outcome(ReversalOutcomes.Restored));
            Assert.Equal(1, counts.Outcome(ReversalOutcomes.Removed));
            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = "reverse" }));
            Assert.Equal("completed", activity.Outcome);
            Assert.Equal("manual:tester", activity.Actor);
            Assert.Contains("2 restored", activity.Summary!, StringComparison.Ordinal);
            Assert.Contains("1 removed", activity.Summary!, StringComparison.Ordinal);
            foreach (var i in new[] { 0, 1, 4 })
            {
                Assert.Contains(
                    await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, DeliveryKey = SampleEstate.Key(i).Value }),
                    a => a.Kind == "reverse");
            }

            // Asking again changes nothing: every record is settled.
            runtime.RunId = Guid.CreateVersion7();
            var again = await runtime.ReverseAsync(ReversalSource.Run(second));
            Assert.Equal(0, again.Taken);
            Assert.Equal(2, again.Counts.Outcome(ReversalOutcomes.Restored));
        }
    }

    [Fact]
    public async Task A_reversed_record_stays_blocked_until_its_source_changes_or_it_is_released()
    {
        var tables = await EstateAsync();
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            ChangeRows(tables, "HAL", 0, 1);
            var second = await DeliverAsync(runtime);
            runtime.RunId = Guid.CreateVersion7();
            await runtime.ReverseAsync(ReversalSource.Run(second));
            var reverted = await HeldAsync(runtime, ledger, 0, 1);

            // A run reading the whole scope again finds the reversed rows unchanged and passes them over.
            _clock.Advance(TimeSpan.FromMinutes(5));
            await DeliverAsync(runtime, force: true);
            foreach (var i in new[] { 0, 1 })
            {
                Assert.Equal(reverted[i].Version, LatestVersion(TargetOf(runtime, i)));
                Assert.Equal(RecordStatus.Reverted, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(i)))!.Status);
            }

            // Released, a reverted record is delivered again and planned by the next run, which sends what renders now.
            Assert.Equal(1, await ledger.ReleaseAsync(runtime.Flow.Id, [SampleEstate.Key(1)], Now));
            var released = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1));
            Assert.Equal(RecordStatus.Delivered, released!.Status);
            Assert.False(released.Blocked);
            Assert.NotNull(released.PlanRequestedUtc);

            // A row corrected in the source flows through on its own.
            ChangeRows(tables, "SLB", 0);
            await DeliverAsync(runtime, force: true);
            foreach (var i in new[] { 0, 1 })
            {
                var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(i));
                Assert.Equal(RecordStatus.Delivered, record!.Status);
                Assert.NotEqual(reverted[i].Version, record.TargetVersion);
                Assert.Equal(record.TargetVersion, LatestVersion(TargetOf(runtime, i)));
            }
        }
    }

    [Fact]
    public async Task A_record_a_later_run_or_another_system_changed_is_left_as_it_is()
    {
        var tables = await EstateAsync();
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            ChangeRows(tables, "HAL", 0, 1);
            var second = await DeliverAsync(runtime);

            // A third run changes record 0 again, and something outside the flow writes record 1.
            ChangeRows(tables, "SLB", 0);
            var third = await DeliverAsync(runtime);
            var thirdVersion = LatestVersion(TargetOf(runtime, 0));
            var outside = (JsonObject)_platform.Records[TargetOf(runtime, 1)].DeepClone();
            outside["data"]!["Name"] = "renamed elsewhere";
            var outsideVersion = _platform.Put(outside);

            runtime.RunId = Guid.CreateVersion7();
            var summary = await runtime.ReverseAsync(ReversalSource.Run(second));

            Assert.Equal(0, summary.Restored);
            Assert.Equal(2, summary.Skipped);
            Assert.Equal(1, summary.Counts.Outcome(ReversalOutcomes.Superseded));
            Assert.Equal(1, summary.Counts.Outcome(ReversalOutcomes.ChangedInOsdu));
            Assert.Equal(thirdVersion, LatestVersion(TargetOf(runtime, 0)));
            Assert.Equal(outsideVersion, LatestVersion(TargetOf(runtime, 1)));
            var skipped = (await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 20)).First(a => a.Phase == AttemptPhases.Reverse);
            Assert.Equal(AttemptOutcome.Skipped, skipped.Outcome);
            Assert.Contains("reverse the later run first", skipped.ResultJson!, StringComparison.Ordinal);
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!.Status);

            // Reversing the later run first puts record 0 back to what the second run left.
            runtime.RunId = Guid.CreateVersion7();
            var later = await runtime.ReverseAsync(ReversalSource.Run(third));
            Assert.Equal(1, later.Restored);
            var items = await ledger.ListReversalItemsAsync(later.ReversalId, ReversalOutcomes.Restored, null, 10);
            Assert.Equal(SampleEstate.Key(0), Assert.Single(items).DeliveryKey);
        }
    }

    [Fact]
    public async Task A_reversal_stopped_after_its_writes_landed_settles_them_without_writing_again()
    {
        var tables = await EstateAsync();
        var crashing = new CrashingProtocols(_protocols);
        var (runtime, ledger) = await RuntimeAsync(tables, crashing);
        using (runtime)
        {
            await DeliverAsync(runtime);
            ChangeRows(tables, "HAL", 0, 1);
            var second = await DeliverAsync(runtime);

            // The run is stopped right after OSDU took the restores, before the ledger heard of them.
            using var stop = new CancellationTokenSource();
            crashing.AfterRestore = () =>
            {
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
            };
            runtime.RunId = Guid.CreateVersion7();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ReverseAsync(ReversalSource.Run(second), stop.Token));
            var reversal = await ledger.FindReversalAsync(runtime.Flow.Id, ReversalSource.Run(second));
            Assert.Equal(ReversalStatuses.Cancelled, reversal!.Status);
            Assert.Equal(2, (await ledger.CountReversalAsync(reversal.ReversalId)).State(ReversalItemStates.Sending));
            foreach (var i in new[] { 0, 1 })
            {
                Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(i)))!.Status);
            }

            await AssertResumedWithoutWritingAgainAsync(runtime, ledger, crashing, reversal.ReversalId, second);
        }
    }

    [Fact]
    public async Task A_restore_whose_answer_was_lost_is_settled_by_the_next_run_without_writing_again()
    {
        var tables = await EstateAsync();
        var crashing = new CrashingProtocols(_protocols);
        var (runtime, ledger) = await RuntimeAsync(tables, crashing);
        using (runtime)
        {
            await DeliverAsync(runtime);
            ChangeRows(tables, "HAL", 0, 1);
            var second = await DeliverAsync(runtime);

            // OSDU took the restores, and the answer never came back: the run settles them failed and goes on.
            crashing.AfterRestore = () => throw new IOException("the connection dropped before OSDU answered");
            runtime.RunId = Guid.CreateVersion7();
            var first = await runtime.ReverseAsync(ReversalSource.Run(second));
            Assert.Equal(2, first.Failed);
            Assert.Equal(ReversalStatuses.Completed, (await ledger.GetReversalAsync(first.ReversalId))!.Status);
            var failed = await ledger.ListReversalItemsAsync(first.ReversalId, ReversalOutcomes.Failed, null, 10);
            Assert.All(failed, i => Assert.Contains("the connection dropped", i.Detail!, StringComparison.Ordinal));
            await AssertResumedWithoutWritingAgainAsync(runtime, ledger, crashing, first.ReversalId, second);
        }
    }

    /// <summary>Asks for the reversal again and finds the writes an earlier run sent settled as restored, with nothing written again.</summary>
    private async Task AssertResumedWithoutWritingAgainAsync(FlowRuntime runtime, OsduLedger ledger, CrashingProtocols crashing, long reversalId, Guid source)
    {
        var landed = new[] { 0, 1 }.ToDictionary(i => i, i => LatestVersion(TargetOf(runtime, i)));
        var written = _platform.History.Sum(h => h.Value.Count);
        crashing.AfterRestore = null;
        runtime.RunId = Guid.CreateVersion7();
        var resumed = await runtime.ReverseAsync(ReversalSource.Run(source));

        Assert.Equal(reversalId, resumed.ReversalId);
        Assert.Equal(2, resumed.Restored);
        Assert.Equal(written, _platform.History.Sum(h => h.Value.Count));
        foreach (var i in new[] { 0, 1 })
        {
            var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(i));
            Assert.Equal(RecordStatus.Reverted, record!.Status);
            Assert.Equal(landed[i], record.TargetVersion);
        }

        Assert.Equal(ReversalStatuses.Completed, (await ledger.GetReversalAsync(reversalId))!.Status);
        Assert.Equal(2, (await ledger.CountReversalAsync(reversalId)).Outcome(ReversalOutcomes.Restored));
    }

    [Fact]
    public async Task Without_the_ledger_history_before_a_run_the_version_list_says_what_to_put_back()
    {
        var tables = await EstateAsync();
        var late = WithheldFromTheFirstRun(tables);
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            var before = await HeldAsync(runtime, ledger, 0);
            ChangeRows(tables, "HAL", 0);
            Arrive(tables, late);
            var second = await DeliverAsync(runtime);

            // As an attempt delivered before the ledger recorded what it replaced, and as a ledger pruned past the run.
            await using (var db = _db.CreateDbContext())
            {
                var keys = new[] { SampleEstate.Key(0).Value, SampleEstate.Key(4).Value };
                var secondAttempts = await db.DeliveryAttempts.Where(a => a.RunId == second && keys.Contains(a.DeliveryKey)).ToListAsync();
                Assert.NotEmpty(secondAttempts);
                foreach (var attempt in secondAttempts)
                {
                    attempt.ResultJson = JsonNode.Parse(attempt.ResultJson!)!.AsObject().Also(o => o.Remove("replaced")).ToJsonString();
                }

                var first = secondAttempts.Min(a => a.AttemptId);
                db.DeliveryAttempts.RemoveRange(await db.DeliveryAttempts.Where(a => keys.Contains(a.DeliveryKey) && a.AttemptId < first).ToListAsync());
                await db.SaveChangesAsync();
            }

            runtime.RunId = Guid.CreateVersion7();
            var summary = await runtime.ReverseAsync(ReversalSource.Run(second));

            Assert.Equal(1, summary.Restored);
            Assert.Equal(1, summary.Removed);
            var id = TargetOf(runtime, 0);
            Assert.True(JsonNode.DeepEquals(Content(VersionOf(id, before[0].Version)), Content(_platform.Records[id])));
            Assert.Contains((await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(4)))!.TargetId ?? TargetOf(runtime, 4), _platform.Removed);
        }
    }

    [Fact]
    public async Task A_reversal_walked_a_few_records_at_a_time_reaches_every_one()
    {
        var tables = await EstateAsync();
        var ledger = new OsduLedger(_db.CreateDbContext, _clock) { WriteSlice = 2 };
        var (runtime, _) = await RuntimeAsync(tables, ledger: ledger);
        using (runtime)
        {
            await DeliverAsync(runtime);
            ChangeRows(tables, "HAL", 0, 1, 2, 3, 4);
            var second = await DeliverAsync(runtime);

            var protocol = await runtime.ProtocolAsync();
            var runner = new ReversalRunner(runtime.Flow, ledger, protocol, _clock, NullLogger.Instance, null, "test", Guid.CreateVersion7()) { PageSize = 2 };
            var summary = await runner.RunAsync(ReversalSource.Submission(await SubmissionOfAsync(ledger, second)), null, CancellationToken.None);

            Assert.Equal(5, summary.Records);
            Assert.Equal(5, summary.Restored);
            Assert.Equal(5, summary.Counts.Outcome(ReversalOutcomes.Restored));
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(RecordStatus.Reverted, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(i)))!.Status);
            }
        }
    }

    [Fact]
    public async Task A_preview_reads_what_a_reversal_would_reach_and_the_plan_decides_each_record_as_the_run_does()
    {
        var tables = await EstateAsync();
        var late = WithheldFromTheFirstRun(tables);
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            ChangeRows(tables, "HAL", 0);
            Arrive(tables, late);
            var second = await DeliverAsync(runtime);

            var read = await ledger.ReadReversalSourceAsync(runtime.Flow.Id, runtime.Flow.Label, ReversalSource.Run(second), 10);
            Assert.Equal(2, read.Records);
            Assert.NotEmpty(read.Submissions);
            var records = await ledger.GetRecordsAsync(runtime.Flow.Id, read.Sample.Select(s => s.DeliveryKey));
            var steps = read.Sample.ToDictionary(s => s.DeliveryKey, s => ReversalPlan.Decide(s, records[s.DeliveryKey], ReversalRoute.ForRecord(runtime.Flow, s.TargetId!)).Action);
            Assert.Equal(ReversalAction.Restore, steps[SampleEstate.Key(0)]);
            Assert.Equal(ReversalAction.Remove, steps[SampleEstate.Key(4)]);

            // A reversal of something the ledger does not hold is refused, naming it.
            var missing = Guid.NewGuid();
            var refused = await Assert.ThrowsAsync<DeliveryException>(() => ledger.ReadReversalSourceAsync(runtime.Flow.Id, runtime.Flow.Label, ReversalSource.Submission(missing), 10));
            Assert.Contains(missing.ToString("D"), refused.Message, StringComparison.Ordinal);
        }
    }

    private async Task<MemoryIngestionTables> EstateAsync() => await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);

    /// <summary>The sample flow, aimed at the stand-in OSDU under its platform root, failing fast.</summary>
    private FlowDefinition Flow()
    {
        var flow = Samples.LocalFlow(_root);
        return flow with
        {
            Target = flow.Target with
            {
                Endpoint = FakeOsduPlatform.Endpoint,
                Auth = new TargetAuth { Type = TargetAuthType.None },
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" },
            },
            Reliability = flow.Reliability with { Concurrency = 4, Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
        };
    }

    private async Task<(FlowRuntime Runtime, OsduLedger Ledger)> RuntimeAsync(MemoryIngestionTables tables, IProtocolFactory? protocols = null, OsduLedger? ledger = null)
    {
        ledger ??= _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock, protocols: protocols ?? _protocols, sources: tables);
        var runtime = await FlowRuntime.CreateAsync(engine, Flow(), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        return (runtime, ledger);
    }

    /// <summary>
    /// A deliver run of the flow under a run id of its own, reading the whole scope (each record's own gates decide what is
    /// sent, as an incremental run's would for the rows its window holds); returns the run.
    /// </summary>
    private async Task<Guid> DeliverAsync(FlowRuntime runtime, bool force = false)
    {
        var run = Guid.CreateVersion7();
        runtime.RunId = run;
        runtime.Actor = "schedule:test";
        runtime.Selection = SourceSelection.Full();

        // A runtime keeps the submission it planned, as the one run it serves in production does; each run here plans its own.
        runtime.SubmissionId = null;
        await runtime.RunAsync(force);
        _clock.Advance(TimeSpan.FromMinutes(1));
        return run;
    }

    private static async Task<Guid> SubmissionOfAsync(OsduLedger ledger, Guid run)
    {
        var submissions = await ledger.ListSubmissionsAsync(null, 50);
        return submissions.First(s => s.RunId == run).SubmissionId;
    }

    /// <summary>Takes the last sample record out of the ingestion tables, so the first run does not see it.</summary>
    private static MemoryRecord WithheldFromTheFirstRun(MemoryIngestionTables tables)
    {
        var late = tables.Records[^1];
        tables.Records.Remove(late);
        return late;
    }

    /// <summary>The withheld record lands in the ingestion tables now.</summary>
    private void Arrive(MemoryIngestionTables tables, MemoryRecord late)
    {
        late.UpdatedUtc = Now;
        tables.Records.Add(late);
    }

    /// <summary>
    /// Changes the creator (which the mapping reads) of the sample records at <paramref name="indexes"/> (in the order the estate built them), as
    /// the source would: stamped now, at a business version later than any the record carried before.
    /// </summary>
    private void ChangeRows(MemoryIngestionTables tables, string value, params int[] indexes)
    {
        _changes++;
        foreach (var index in indexes)
        {
            SampleEstate.Change(tables.Records[index], "creator", value, Now, SampleWellLogs.UpdatedUtc(index).AddHours(_changes));
        }
    }

    /// <summary>How many times the test changed rows, which moves each change's business version past the one before.</summary>
    private int _changes;

    private static string TargetOf(FlowRuntime runtime, int index)
        => "dev:work-product-component--WellLog:" + SampleEstate.Key(index).Value.ToString("N");

    private long LatestVersion(string id) => _platform.Records[id]["version"]!.GetValue<long>();

    private JsonObject VersionOf(string id, long version) => _platform.History[id].Single(v => v["version"]!.GetValue<long>() == version);

    private static string? BulkUri(JsonObject record) => record["data"]?["ExtensionProperties"]?["wdms"]?["bulkURI"]?.GetValue<string>();

    /// <summary>A stored version without the properties storage writes on each one.</summary>
    private static JsonObject Content(JsonObject stored)
    {
        var content = (JsonObject)stored.DeepClone();
        foreach (var property in new[] { "version", "createUser", "createTime", "modifyUser", "modifyTime" })
        {
            content.Remove(property);
        }

        return content;
    }

    /// <summary>What the stand-in holds of each named record, with the ledger's hash of it.</summary>
    private async Task<Dictionary<int, (long Version, string? BulkUri, string? MetadataHash)>> HeldAsync(FlowRuntime runtime, OsduLedger ledger, params int[] indexes)
    {
        var held = new Dictionary<int, (long, string?, string?)>();
        foreach (var index in indexes)
        {
            var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(index));
            var id = TargetOf(runtime, index);
            Assert.Equal(record!.TargetId, id);
            Assert.Equal(LatestVersion(id), record.TargetVersion);
            held[index] = (LatestVersion(id), BulkUri(_platform.Records[id]), record.MetadataHash);
        }

        return held;
    }

    /// <summary>The stand-in's protocols, whose restores can fail right after OSDU took them.</summary>
    private sealed class CrashingProtocols(IProtocolFactory inner) : IProtocolFactory
    {
        /// <summary>What happens once OSDU took a page of restores, before the run hears of it.</summary>
        public Action? AfterRestore { get; set; }

        public async Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, ILoggerFactory loggers, CancellationToken ct = default)
            => new Crashing(await inner.CreateAsync(flow, http, loggers, ct), this);

        private sealed class Crashing(IDeliveryProtocol inner, CrashingProtocols owner) : IDeliveryProtocol
        {
            public DeliveryProtocol Kind => inner.Kind;

            public int MaxBatch => inner.MaxBatch;

            public int MaxVerifyBatch => inner.MaxVerifyBatch;

            public bool VerifiesWithTargetState => inner.VerifiesWithTargetState;

            public Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default) => inner.DeliverAsync(work, ct);

            public Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct = default) => inner.DeliverBatchAsync(works, ct);

            public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default) => inner.VerifyAsync(targetId, expectedVersion, ct);

            public Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(IReadOnlyList<VerifyRequest> requests, CancellationToken ct = default) => inner.VerifyBatchAsync(requests, ct);

            public Task<IReadOnlyDictionary<string, string>?> InvalidLegalTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default) => inner.InvalidLegalTagsAsync(tags, ct);

            public Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
                => inner.DeleteAsync(targetId, scope, targetState, ct);

            public Task<IReadOnlyList<RemovalResult>> DeleteBatchAsync(IReadOnlyList<RecordRemoval> removals, RemovalScope scope, CancellationToken ct = default) => inner.DeleteBatchAsync(removals, scope, ct);

            public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default) => inner.ReadAsync(targetId, ct);

            public Task<JsonObject?> ReadAsync(string targetId, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct) => inner.ReadAsync(targetId, targetState, ct);

            public Task<IReadOnlyList<long>?> VersionsAsync(string targetId, CancellationToken ct = default) => inner.VersionsAsync(targetId, ct);

            public Task<JsonObject?> ReadVersionAsync(string targetId, long version, CancellationToken ct = default) => inner.ReadVersionAsync(targetId, version, ct);

            public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default) => inner.ProbeAsync(ct);

            public async Task<IReadOnlyList<RestoreResult>> RestoreBatchAsync(IReadOnlyList<VersionRestore> restores, CancellationToken ct = default)
            {
                var written = await inner.RestoreBatchAsync(restores, ct);
                owner.AfterRestore?.Invoke();
                return written;
            }
        }
    }
}

/// <summary>What a reversal decides and can do, without a database or a target.</summary>
public sealed class ReversalRulesTests
{
    private static readonly Guid Submission = Guid.Parse("0193a1b2-0000-7000-8000-000000000001");
    private static readonly Guid Run = Guid.Parse("0193a1b2-0000-7000-8000-000000000002");

    [Fact]
    public void A_reverse_run_names_one_source_and_nothing_else()
    {
        DeliveryRunPayload.Parse($$"""{"submissionId":"{{Submission}}"}""").Validate(DeliveryOperations.Reverse);
        DeliveryRunPayload.Parse($$"""{"runId":"{{Run}}","interface":"welllogs"}""").Validate(DeliveryOperations.Reverse);

        Refused("{}", "names what it reverses");
        Refused($$"""{"submissionId":"{{Submission}}","runId":"{{Run}}"}""", "not both");
        Refused($$"""{"runId":"{{Run}}","force":true}""", "passes no gate");
        Refused($$"""{"runId":"{{Run}}","recordKeys":["{{Submission}}"]}""", "every record its source delivered");
        Refused($$"""{"runId":"{{Run}}","redeliver":"all"}""", "sends nothing the flow renders");
        Refused($$"""{"runId":"{{Run}}","interfaces":["welllogs"]}""", "one interface's ledger");
        var other = Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse($$"""{"runId":"{{Run}}"}""").Validate(DeliveryOperations.Deliver));
        Assert.Contains("only a reverse run names the run it reverses", other.Message, StringComparison.Ordinal);

        var payload = DeliveryRunPayload.Parse($$"""{"runId":"{{Run}}"}""");
        Assert.Equal(Run, DeliveryRunPayload.Parse(payload.ToJson()).RunId);

        static void Refused(string json, string why)
        {
            var refused = Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse(json).Validate(DeliveryOperations.Reverse));
            Assert.Contains(why, refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Each_route_says_whether_it_restores_and_removes()
    {
        var storage = Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = DeliveryProtocol.Storage });
        var route = ReversalRoute.Of(storage, "osdu:wks:master-data--Wellbore:1.0.0");
        Assert.True(route.Restores);
        Assert.True(route.Removes);
        Assert.Contains("/api/storage/v2/records/{id}/{version}", route.Restore, StringComparison.Ordinal);

        // The sample well logs go to the Wellbore DDMS under the platform root: restored through storage.
        var logs = Samples.LocalFlow(Samples.NewTempDirectory());
        var wellLogs = ReversalRoute.Of(logs, "osdu:wks:work-product-component--WellLog:1.4.0");
        Assert.True(wellLogs.Restores);
        Assert.Contains("past the DDMS", wellLogs.Restore, StringComparison.Ordinal);
        Assert.True(wellLogs.Removes);

        // Routes whose records carry more than a version of the record holds, or no versions at all, do not restore.
        foreach (var protocol in new[] { DeliveryProtocol.File, DeliveryProtocol.Dataset, DeliveryProtocol.Manifest, DeliveryProtocol.Workflow, DeliveryProtocol.Dspdm, DeliveryProtocol.Etp })
        {
            var flow = Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = protocol });
            var other = ReversalRoute.Of(flow, "osdu:wks:work-product-component--Document:1.0.0");
            Assert.False(other.Restores);
            Assert.StartsWith("(", other.Restore, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(other.RestoreRefusal));
        }

        Assert.False(ReversalRoute.Of(Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = DeliveryProtocol.Dspdm }), null).Removes);
    }

    [Fact]
    public void The_plan_puts_a_record_back_only_while_it_is_the_one_the_source_left()
    {
        var route = new ReversalRoute("GET, then PUT", "POST delete", null, null);
        var key = new DeliveryKey(Guid.NewGuid());
        var item = new ReversalItemState
        {
            DeliveryKey = key,
            TargetId = "dev:master-data--Wellbore:1",
            FirstVersion = 20,
            RunVersion = 21,
            Prior = ReversalPriors.Version,
            PriorVersion = 10,
            State = ReversalItemStates.Pending,
        };
        var record = new RecordState
        {
            DeliveryKey = key,
            FlowId = Guid.NewGuid(),
            SourceKey = "1",
            MappingName = "m",
            TargetId = item.TargetId,
            ClaimedTargetId = item.TargetId,
            TargetVersion = 21,
            Status = RecordStatus.Delivered,
        };

        Assert.Equal(ReversalAction.Restore, ReversalPlan.Decide(item, record, route).Action);
        Assert.Equal(ReversalAction.Remove, ReversalPlan.Decide(item with { Prior = ReversalPriors.None, PriorVersion = null }, record, route).Action);
        Assert.Equal(ReversalAction.ResolvePrior, ReversalPlan.Decide(item with { Prior = ReversalPriors.Unknown, PriorVersion = null }, record, route).Action);
        Assert.Equal(ReversalAction.Restore, ReversalPlan.Decide(item, record with { Status = RecordStatus.Held }, route).Action);

        Skipped(ReversalOutcomes.NotInLedger, item, null, route);
        Skipped(ReversalOutcomes.NotClaimed, item, record with { ClaimedTargetId = null }, route);
        Skipped(ReversalOutcomes.Busy, item, record with { Status = RecordStatus.Pending }, route);
        Skipped(ReversalOutcomes.Busy, item, record with { LeaseOwner = "node/abc" }, route);
        Skipped(ReversalOutcomes.Superseded, item, record with { TargetVersion = 30 }, route);
        Skipped(ReversalOutcomes.Superseded, item, record with { Status = RecordStatus.Deleted, TargetVersion = null }, route);
        Skipped(ReversalOutcomes.Superseded, item, record with { Status = RecordStatus.Reverted, TargetVersion = 31 }, route);
        Skipped(ReversalOutcomes.Unchanged, item with { PriorVersion = 21 }, record, route);
        Skipped(ReversalOutcomes.NotReversible, item, record, route with { RestoreRefusal = "no versions here" });
        Skipped(ReversalOutcomes.NotReversible, item with { Prior = ReversalPriors.None, PriorVersion = null }, record, route with { RemoveRefusal = "no removal here" });
        Skipped(ReversalOutcomes.NotReversible, item with { RunVersion = null }, record with { TargetVersion = null }, route);

        static void Skipped(string outcome, ReversalItemState item, RecordState? record, ReversalRoute route)
        {
            var step = ReversalPlan.Decide(item, record, route);
            Assert.Equal(ReversalAction.Skip, step.Action);
            Assert.Equal(outcome, step.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(step.Detail));
        }
    }

    [Fact]
    public async Task A_storage_restore_writes_the_version_back_as_it_was_without_what_storage_writes_itself()
    {
        const string Id = "dev:master-data--Wellbore:1";
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Put, "/api/storage/v2/records", HttpStatusCode.Created, """{"recordCount":1,"recordIdVersions":["dev:master-data--Wellbore:1:30"]}""");
        var protocol = new OsduRecordProtocol(Client(handler), new ProtocolOptions { PreserveDataKeys = ["ExtensionProperties"] });
        var stored = FakeOsduPlatform.Record(Id, "osdu:wks:master-data--Wellbore:1.0.0", new JsonObject { ["Name"] = "before", ["ExtensionProperties"] = new JsonObject { ["kept"] = "as it was" } });
        stored["version"] = 10;
        stored["createUser"] = "someone";
        stored["modifyTime"] = "2026-09-01T00:00:00Z";

        var result = Assert.Single(await protocol.RestoreBatchAsync([new VersionRestore(new DeliveryKey(Guid.NewGuid()), Id, 10, stored, 21, null)]));

        Assert.True(result.Succeeded);
        Assert.Equal(30, result.NewVersion);
        var call = Assert.Single(handler.Calls);
        var written = JsonNode.Parse(call.Body!)!.AsArray().Single()!.AsObject();
        Assert.Null(written["version"]);
        Assert.Null(written["createUser"]);
        Assert.Null(written["modifyTime"]);
        Assert.Equal("before", written["data"]!["Name"]!.GetValue<string>());

        // The version's own keys are written as it held them, not carried from the latest version: nothing else is read.
        Assert.Equal("as it was", written["data"]!["ExtensionProperties"]!["kept"]!.GetValue<string>());

        // A version of another record is never written over this one.
        var foreign = (JsonObject)stored.DeepClone();
        foreign["id"] = "dev:master-data--Wellbore:2";
        var refused = Assert.Single(await protocol.RestoreBatchAsync([new VersionRestore(new DeliveryKey(Guid.NewGuid()), Id, 10, foreign, 21, null)]));
        Assert.False(refused.Succeeded);
        Assert.Contains("names the record", refused.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_route_that_cannot_restore_refuses_every_record_naming_itself()
    {
        IDeliveryProtocol protocol = new FakeProtocol();
        var stored = FakeOsduPlatform.Record("dev:x:1", "osdu:wks:x:1.0.0");
        var results = await protocol.RestoreBatchAsync([new VersionRestore(new DeliveryKey(Guid.NewGuid()), "dev:x:1", 1, stored, 2, null)]);
        Assert.False(Assert.Single(results).Succeeded);
        Assert.Contains("cannot write an earlier version", results[0].Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_delivered_attempt_records_the_version_it_replaced()
    {
        Assert.Equal((true, 12L), AttemptResult.Replaced(AttemptResult.WithReplaced("""{"steps":[]}""", 12)));
        Assert.Equal((true, (long?)null), AttemptResult.Replaced(AttemptResult.WithReplaced(null, null)));
        Assert.Equal((false, (long?)null), AttemptResult.Replaced("""{"steps":[]}"""));
    }

    private static OsduHttpClient Client(FakeHttpHandler handler)
    {
        var http = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SqlFlow.Core.Secrets.SecretResolver([new SqlFlow.Core.Secrets.EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        return new OsduHttpClient(http, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None }, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
    }
}

/// <summary>A fluent step for a test that changes an object in place.</summary>
internal static class TestObjects
{
    public static T Also<T>(this T value, Action<T> act)
    {
        act(value);
        return value;
    }
}
