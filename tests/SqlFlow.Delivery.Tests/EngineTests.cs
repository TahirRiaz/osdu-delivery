using System.Net;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Verify;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.SampleDrop;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>End-to-end over the sample mapping, the real WellLog 1.4.0 schema snapshot, a generated drop, a SQLite ledger and a fake protocol.</summary>
public class EndToEndTests : IDisposable
{
    private readonly SqliteCatalog _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private static readonly Guid Submission1 = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Submission2 = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Submission3 = new("33333333-3333-3333-3333-333333333333");

    private async Task<string> DropAsync(string name, IReadOnlyList<SampleRecord> records, Guid submission, long sourceVersion)
    {
        var dir = Path.Combine(_root, name);
        await SampleDropBuilder.WriteAsync(dir, "STAT_COMP", records, submission, sourceVersion);
        return dir;
    }

    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, CatalogLedger Ledger)> RuntimeAsync(string drop, Func<FlowDefinition, FlowDefinition>? adjust = null)
    {
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = adjust is null ? Samples.LocalFlow(drop) : adjust(Samples.LocalFlow(drop));
        var runtime = await FlowRuntime.CreateAsync(engine, flow, new Dictionary<string, string> { ["logSource"] = "STAT_COMP" }, drop);
        return (runtime, new FakeProtocol(), ledger);
    }

    private async Task<WorkerSummary> RunAsync(FlowRuntime runtime, FakeProtocol protocol, CatalogLedger ledger, Guid submission)
    {
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.DropLocation, force: false);
        if (intake.NothingToDo)
        {
            return WorkerSummary.Empty;
        }

        var worker = new DeliveryWorker(ledger, runtime.Context.Drops, runtime.Context.Stores, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };
        var summary = await worker.DrainAsync(submission);
        await runtime.Intake.CompleteAsync(submission, runtime.Flow.Id);
        return summary;
    }

    [Fact]
    public async Task Plan_without_a_ledger_creates_everything()
    {
        var drop = await DropAsync("d1", SampleDropBuilder.DefaultRecords("STAT_COMP"), Submission1, 1);
        var engine = Samples.Engine(ledger: null);
        using var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(drop), new Dictionary<string, string> { ["logSource"] = "STAT_COMP" }, drop);
        var plan = await runtime.PlanAsync();
        Assert.Equal(3, plan.Entries.Count);
        Assert.All(plan.Entries, e => Assert.Equal(PlannedAction.Create, e.Action));
        Assert.All(plan.Entries, e => Assert.Equal(1, e.ChunkCount));
        Assert.All(plan.Entries, e => Assert.StartsWith("opendes:work-product-component--WellLog:", e.TargetId!, StringComparison.Ordinal));
        var doc = plan.Entries[0].Render!.Document;
        Assert.Equal("opendes:reference-data--UnitOfMeasure:m:", doc["data"]!["VerticalMeasurement"]!["VerticalMeasurementUnitOfMeasureID"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tier0_plans_the_scope_again_when_the_cache_moved_under_an_unchanged_source()
    {
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var drop = await DropAsync("t0", records, Submission1, sourceVersion: 1);
        var (runtime, protocol, ledger) = await RuntimeAsync(drop);
        using (runtime)
        {
            await RunAsync(runtime, protocol, ledger, Submission1);
        }

        // The same drop again, same source version: nothing moved, so the whole run is skipped.
        var again = await DropAsync("t0b", records, Submission2, sourceVersion: 1);
        var (unchanged, _, unchangedLedger) = await RuntimeAsync(again);
        using (unchanged)
        {
            Assert.True((await unchanged.PlanAsync()).SkippedWholeRun);
        }

        // Now the render context moves, which is what a cache refresh does to it. The source is still where it
        // was, and the scope is planned anyway: this is the gate that used to leave OSDU on stale cached values.
        var scope = SqlFlow.Delivery.Engine.Planning.Planner.ScopeKey(new Dictionary<string, string> { ["logSource"] = "STAT_COMP" });
        var watermarks = await unchangedLedger.GetWatermarksAsync(runtime.Flow.Id, scope);
        Assert.NotEmpty(watermarks);
        await unchangedLedger.SetWatermarksAsync(watermarks.Select(w => w with { ContextHash = "a-different-cache-version" }));

        var (moved, _, _) = await RuntimeAsync(again);
        using (moved)
        {
            var plan = await moved.PlanAsync();
            Assert.False(plan.SkippedWholeRun);
            Assert.Equal(records.Count, plan.Entries.Count);
        }
    }

    [Fact]
    public async Task Run_delivers_then_skips_unchanged_then_updates_only_what_changed()
    {
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var drop1 = await DropAsync("d1", records, Submission1, sourceVersion: 1);
        var (runtime, protocol, ledger) = await RuntimeAsync(drop1);
        using (runtime)
        {
            var summary = await RunAsync(runtime, protocol, ledger, Submission1);
            Assert.Equal(3, summary.Delivered);
            Assert.Equal(3, protocol.Deliveries.Count);
            Assert.All(protocol.Deliveries, w => Assert.True(w.DeliverMetadata && w.DeliverPayload));
            var submission = await ledger.GetSubmissionAsync(Submission1);
            Assert.Equal(SubmissionStatus.Completed, submission!.Status);
            Assert.Equal(3, submission.Delivered);
            Assert.Equal(3, submission.Planned);

            var state = await ledger.GetRecordAsync(runtime.Flow.Id, records[0].Key);
            Assert.Equal(RecordStatus.Delivered, state!.Status);
            Assert.NotNull(state.TargetVersion);
            Assert.NotNull(state.MetadataHash);
            Assert.NotNull(state.PayloadHash);
            Assert.Equal(new DateTime(2026, 9, 1, 10, 15, 0, DateTimeKind.Utc), state.SourceModifiedUtc);
        }

        // Same source version, new submission id: tier 0 skips the whole run without reading a record.
        var drop2 = await DropAsync("d2", records, Submission2, sourceVersion: 1);
        var (runtime2, protocol2, ledger2) = await RuntimeAsync(drop2);
        using (runtime2)
        {
            var plan = await runtime2.PlanAsync();
            Assert.True(plan.SkippedWholeRun);
            var summary = await RunAsync(runtime2, protocol2, ledger2, Submission2);
            Assert.Equal(0, summary.Processed);
            Assert.Empty(protocol2.Deliveries);
            Assert.Equal(SubmissionStatus.Completed, (await ledger2.GetSubmissionAsync(Submission2))!.Status);
        }

        // Source advanced but rows unchanged: tier 1 skips every record without rendering.
        var drop3 = await DropAsync("d3", records, Submission3, sourceVersion: 2);
        var (runtime3, protocol3, ledger3) = await RuntimeAsync(drop3);
        using (runtime3)
        {
            var plan = await runtime3.PlanAsync();
            Assert.False(plan.SkippedWholeRun);
            Assert.All(plan.Entries, e => Assert.Equal(PlannedAction.Skip, e.Action));
            Assert.All(plan.Entries, e => Assert.Equal(SkipTier.Fingerprint, e.SkipTier));
            var summary = await RunAsync(runtime3, protocol3, ledger3, Submission3);
            Assert.Equal(0, summary.Processed);
            var submission = await ledger3.GetSubmissionAsync(Submission3);
            Assert.Equal(3, submission!.SkippedUnchanged);
            Assert.Equal(0, submission.Delivered);
        }

        // Metadata edit on the first record: update metadata only; the payload hash is unchanged.
        var changed = records.ToList();
        changed[0] = changed[0] with { LogRun = "1A", UpdateDate = "2026-09-05T09:00:00Z" };
        var drop4 = await DropAsync("d4", changed, Guid.NewGuid(), sourceVersion: 3);
        var (runtime4, protocol4, ledger4) = await RuntimeAsync(drop4);
        using (runtime4)
        {
            var plan = await runtime4.PlanAsync();
            var entry = plan.Entries.Single(e => e.SourceKey.EndsWith("L-1001", StringComparison.Ordinal));
            Assert.Equal(PlannedAction.Skip, entry.Action);
            // LogRun is not mapped into the document, so the render is identical: tier 2 skip despite a moved fingerprint.
            Assert.Equal(SkipTier.ContentHash, entry.SkipTier);
        }

        changed[0] = changed[0] with { Creator = "HAL", UpdateDate = "2026-09-05T10:00:00Z" };
        var drop5 = await DropAsync("d5", changed, Guid.NewGuid(), sourceVersion: 4);
        var (runtime5, protocol5, ledger5) = await RuntimeAsync(drop5);
        using (runtime5)
        {
            var plan = await runtime5.PlanAsync();
            var entry = plan.Entries.Single(e => e.SourceKey.EndsWith("L-1001", StringComparison.Ordinal));
            Assert.Equal(PlannedAction.UpdateMetadata, entry.Action);
            Assert.True(entry.DeliverMetadata);
            Assert.False(entry.DeliverPayload);
            var summary = await RunAsync(runtime5, protocol5, ledger5, plan.Drop.Manifest.SubmissionId);
            Assert.Equal(1, summary.Delivered);
            var work = Assert.Single(protocol5.Deliveries);
            Assert.True(work.DeliverMetadata);
            Assert.False(work.DeliverPayload);
            Assert.NotNull(work.ExistingVersion);
        }

        // Curve sample edit: payload only.
        var payloadChanged = changed.ToList();
        var gr = payloadChanged[0].Curves[0] with { Values = [.. payloadChanged[0].Curves[0].Values.Select(v => v + 1)] };
        payloadChanged[0] = payloadChanged[0] with { Curves = [gr, .. payloadChanged[0].Curves.Skip(1)], UpdateDate = "2026-09-06T00:00:00Z" };
        var drop6 = await DropAsync("d6", payloadChanged, Guid.NewGuid(), sourceVersion: 5);
        var (runtime6, protocol6, ledger6) = await RuntimeAsync(drop6);
        using (runtime6)
        {
            var plan = await runtime6.PlanAsync();
            var entry = plan.Entries.Single(e => e.SourceKey.EndsWith("L-1001", StringComparison.Ordinal));
            Assert.Equal(PlannedAction.UpdatePayload, entry.Action);
            var summary = await RunAsync(runtime6, protocol6, ledger6, plan.Drop.Manifest.SubmissionId);
            Assert.Equal(1, summary.Delivered);
            var work = Assert.Single(protocol6.Deliveries);
            Assert.False(work.DeliverMetadata);
            Assert.True(work.DeliverPayload);
        }
    }

    [Fact]
    public async Task Unresolvable_reference_holds_the_record_and_release_requeues_it()
    {
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP").ToList();
        records[2] = records[2] with { WellboreUwi = "NO 99/9-Z-1" };
        var drop = await DropAsync("held", records, Submission1, 1);
        var (runtime, protocol, ledger) = await RuntimeAsync(drop);
        using (runtime)
        {
            var summary = await RunAsync(runtime, protocol, ledger, Submission1);
            Assert.Equal(2, summary.Delivered);
            var submission = await ledger.GetSubmissionAsync(Submission1);
            Assert.Equal(1, submission!.Held);
            var held = await ledger.GetRecordAsync(runtime.Flow.Id, records[2].Key);
            Assert.Equal(RecordStatus.Held, held!.Status);
            Assert.Contains("no Wellbore matches", held.LastError, StringComparison.Ordinal);
            Assert.Equal("NO 99/9-Z-1 / STAT_COMP / run 1 (L-2001)", held.Label);
        }

        // The same source again: the held record is blocked, not re-attempted (design.md section 7.4).
        var again = await DropAsync("held2", records, Submission2, 2);
        var (runtime2, protocol2, ledger2) = await RuntimeAsync(again);
        using (runtime2)
        {
            var plan = await runtime2.PlanAsync();
            var blocked = plan.Entries.Single(e => e.Action == PlannedAction.Blocked);
            Assert.Equal(records[2].Key, blocked.Key);
            Assert.Contains("held since", blocked.Reason, StringComparison.Ordinal);
            var summary = await RunAsync(runtime2, protocol2, ledger2, Submission2);
            Assert.Equal(0, summary.Processed);
            Assert.Equal(1, (await ledger2.GetSubmissionAsync(Submission2))!.Blocked);
            Assert.Single(await ledger2.ListAttemptsAsync(records[2].Key, 10));

            // Released: the next plan renders it again (and holds it again, because the source is still wrong).
            Assert.Equal(1, await ledger2.ReleaseAsync(runtime2.Flow.Id, null, _clock.GetUtcNow().UtcDateTime));
            var replanned = await runtime2.PlanAsync(force: true);
            Assert.Equal(PlannedAction.Hold, replanned.Entries.Single(e => e.Key == records[2].Key).Action);
        }
    }

    [Fact]
    public async Task A_record_the_service_asked_to_wait_on_is_not_attempted_again_sooner()
    {
        // The transport does not sit through a long Retry-After; it hands the wait up with the failure, and the
        // worker must not schedule the record's next attempt earlier than the service asked. The ordinary record
        // backoff for a first failure is a minute; the service asked for two hours.
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var drop = await DropAsync("retry-after", records, Submission1, 1);
        var (runtime, protocol, ledger) = await RuntimeAsync(drop);
        using (runtime)
        {
            protocol.FailWith = work => work.TargetId.EndsWith(records[0].Key.Value.ToString("N"), StringComparison.Ordinal)
                ? new HttpStatusException(429, "HTTP 429 Too Many Requests", TimeSpan.FromHours(2))
                : null;

            var summary = await RunAsync(runtime, protocol, ledger, Submission1);
            Assert.Equal(1, summary.Retried);

            var waiting = await ledger.GetRecordAsync(runtime.Flow.Id, records[0].Key);
            Assert.Equal(RecordStatus.Pending, waiting!.Status);
            Assert.NotNull(waiting.NextAttemptUtc);
            Assert.True(waiting.NextAttemptUtc!.Value >= _clock.GetUtcNow().UtcDateTime + TimeSpan.FromHours(2));
        }
    }

    [Fact]
    public async Task Transient_failures_back_off_and_succeed_later_while_terminal_statuses_hold()
    {
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var drop = await DropAsync("retry", records, Submission1, 1);
        var (runtime, protocol, ledger) = await RuntimeAsync(drop);
        using (runtime)
        {
            var failures = 0;
            protocol.FailWith = work =>
            {
                if (work.TargetId.EndsWith(records[0].Key.Value.ToString("N"), StringComparison.Ordinal) && failures++ == 0)
                {
                    return new HttpStatusException(503, "HTTP 503 Service Unavailable");
                }

                if (work.TargetId.EndsWith(records[1].Key.Value.ToString("N"), StringComparison.Ordinal))
                {
                    return new HttpStatusException(409, "HTTP 409 Conflict: acl");
                }

                return null;
            };

            var summary = await RunAsync(runtime, protocol, ledger, Submission1);
            Assert.Equal(1, summary.Delivered);
            Assert.Equal(1, summary.Retried);
            Assert.Equal(1, summary.Held);

            var retrying = await ledger.GetRecordAsync(runtime.Flow.Id, records[0].Key);
            Assert.Equal(RecordStatus.Pending, retrying!.Status);
            Assert.NotNull(retrying.NextAttemptUtc);
            var held = await ledger.GetRecordAsync(runtime.Flow.Id, records[1].Key);
            Assert.Equal(RecordStatus.Held, held!.Status);
            Assert.Equal(SubmissionStatus.Running, (await ledger.GetSubmissionAsync(Submission1))!.Status);

            _clock.Advance(TimeSpan.FromMinutes(2));
            var worker = new DeliveryWorker(ledger, runtime.Context.Drops, runtime.Context.Stores, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };
            var later = await worker.DrainAsync(Submission1);
            Assert.Equal(1, later.Delivered);
            var attempts = await ledger.ListAttemptsAsync(records[0].Key, 10);
            Assert.Equal(2, attempts.Count);
            Assert.Equal(AttemptOutcome.Delivered, attempts[0].Outcome);
            Assert.Equal(AttemptOutcome.Failed, attempts[1].Outcome);
            await runtime.Intake.CompleteAsync(Submission1, runtime.Flow.Id);
            Assert.Equal(SubmissionStatus.Completed, (await ledger.GetSubmissionAsync(Submission1))!.Status);
        }
    }

    [Fact]
    public async Task Stopping_the_worker_releases_in_flight_records_without_charging_an_attempt()
    {
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var drop = await DropAsync("stop", records, Submission1, 1);
        var (runtime, protocol, ledger) = await RuntimeAsync(drop);
        using (runtime)
        {
            var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.DropLocation, force: false);
            Assert.Equal(3, intake.Submission.Planned);

            // The first delivery blocks until the worker is stopped; the stop arrives while it is in flight.
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource();
            protocol.Before = async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            };

            var worker = new DeliveryWorker(ledger, runtime.Context.Drops, runtime.Context.Stores, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "stopping-worker") { MaxWait = null };
            var drain = worker.DrainAsync(Submission1, stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);

            var interrupted = protocol.Deliveries.Single();
            var key = records.Single(r => interrupted.TargetId.EndsWith(r.Key.Value.ToString("N"), StringComparison.Ordinal)).Key;
            var state = await ledger.GetRecordAsync(runtime.Flow.Id, key);
            Assert.Equal(RecordStatus.Pending, state!.Status);
            Assert.Null(state.LeaseOwner);
            Assert.Equal(0, state.AttemptCount);
            Assert.Empty(await ledger.ListAttemptsAsync(key, 10));

            // A fresh worker picks everything up at once; nothing waited for a lease to expire.
            protocol.Before = null;
            var resumed = new DeliveryWorker(ledger, runtime.Context.Drops, runtime.Context.Stores, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "next-worker") { MaxWait = null };
            var summary = await resumed.DrainAsync(Submission1);
            Assert.Equal(3, summary.Delivered);
            Assert.Single(await ledger.ListAttemptsAsync(key, 10));
        }
    }

    [Fact]
    public async Task Verify_detects_drift_and_reconcile_queues_redelivery()
    {
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var drop = await DropAsync("verify", records, Submission1, 1);
        var (runtime, protocol, ledger) = await RuntimeAsync(drop);
        using (runtime)
        {
            await RunAsync(runtime, protocol, ledger, Submission1);
            protocol.VerifyWith = id => id.EndsWith(records[0].Key.Value.ToString("N"), StringComparison.Ordinal)
                ? new VerifyResult(VerifyOutcome.Drifted, 999, "someone edited it")
                : new VerifyResult(VerifyOutcome.Match, null, null);
            var verifier = new Verifier(ledger, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<Verifier>());
            var summary = await verifier.RunAsync(100, null, reconcile: true);
            Assert.Equal(3, summary.Checked);
            Assert.Equal(1, summary.Drifted);
            Assert.Equal(2, summary.Matched);
        }

        var drop2 = await DropAsync("verify2", records, Submission2, 2);
        var (runtime2, protocol2, ledger2) = await RuntimeAsync(drop2);
        using (runtime2)
        {
            var plan = await runtime2.PlanAsync();
            var drifted = plan.Entries.Single(e => e.SourceKey.EndsWith("L-1001", StringComparison.Ordinal));
            Assert.Equal(PlannedAction.UpdateBoth, drifted.Action);
            Assert.Equal(2, plan.Skips);
        }
    }

    [Fact]
    public async Task Known_state_is_published_as_parquet()
    {
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var drop = await DropAsync("known", records, Submission1, 1);
        var (runtime, protocol, ledger) = await RuntimeAsync(drop);
        using (runtime)
        {
            await RunAsync(runtime, protocol, ledger, Submission1);
            var target = Path.Combine(_root, "known-state");
            var count = await runtime.Publisher.PublishAsync(runtime.Flow, target);
            Assert.Equal(3, count);
            Assert.True(File.Exists(Path.Combine(target, "known-state.parquet")));
            Assert.Contains("\"delivered\": 3", File.ReadAllText(Path.Combine(target, "known-state.json")), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_incremental_drop_reprocesses_rows_modified_since_and_never_goes_back_to_an_older_one()
    {
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var drop1 = await DropAsync("inc1", records, Submission1, sourceVersion: 1);
        var (runtime, protocol, ledger) = await RuntimeAsync(drop1);
        using (runtime)
        {
            Assert.Equal(3, (await RunAsync(runtime, protocol, ledger, Submission1)).Delivered);
        }

        // The next drop carries only the row modified since: it goes through render and the hash check, and is sent.
        var edited = records[0] with { Creator = "HAL", UpdateDate = "2026-09-05T10:00:00Z" };
        var drop2 = await DropAsync("inc2", [edited], Submission2, sourceVersion: 2);
        var (runtime2, protocol2, ledger2) = await RuntimeAsync(drop2);
        string? editedHash;
        using (runtime2)
        {
            var entry = Assert.Single((await runtime2.PlanAsync()).Entries);
            Assert.Equal(PlannedAction.UpdateMetadata, entry.Action);
            Assert.Equal(1, (await RunAsync(runtime2, protocol2, ledger2, Submission2)).Delivered);
            var state = await ledger2.GetRecordAsync(runtime2.Flow.Id, records[0].Key);
            Assert.Equal(new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc), state!.SourceModifiedUtc);
            editedHash = state.MetadataHash;
            // The rows the incremental drop did not carry are left exactly as they were.
            Assert.Equal(Submission1, (await ledger2.GetRecordAsync(runtime2.Flow.Id, records[1].Key))!.LastSubmissionId);
        }

        // A replay of the earlier version of the row: older than what OSDU holds, so it is skipped and the skip recorded.
        var drop3 = await DropAsync("inc3", [records[0]], Submission3, sourceVersion: 3);
        var (runtime3, protocol3, ledger3) = await RuntimeAsync(drop3);
        using (runtime3)
        {
            var entry = Assert.Single((await runtime3.PlanAsync()).Entries);
            Assert.Equal(PlannedAction.Skip, entry.Action);
            Assert.Equal(SkipTier.Stale, entry.SkipTier);
            Assert.Contains("older than the version last modified 2026-09-05T10:00:00Z already delivered", entry.Reason, StringComparison.Ordinal);

            await RunAsync(runtime3, protocol3, ledger3, Submission3);
            Assert.Empty(protocol3.Deliveries);
            var submission = await ledger3.GetSubmissionAsync(Submission3);
            Assert.Equal(1, submission!.SkippedStale);
            Assert.Equal(SubmissionStatus.Completed, submission.Status);
            Assert.Equal(editedHash, (await ledger3.GetRecordAsync(runtime3.Flow.Id, records[0].Key))!.MetadataHash);
            var stale = (await ledger3.ListAttemptsAsync(records[0].Key, 10)).Single(a => a.Phase == AttemptPhases.Stale);
            Assert.Equal(AttemptOutcome.Skipped, stale.Outcome);
            Assert.Equal(Submission3, stale.SubmissionId);
        }

        // The same moment again, even with values that differ: the last-modified column says nothing changed.
        var drop4 = await DropAsync("inc4", [edited with { LogRun = "9" }], Guid.NewGuid(), sourceVersion: 4);
        var (runtime4, _, _) = await RuntimeAsync(drop4);
        using (runtime4)
        {
            var entry = Assert.Single((await runtime4.PlanAsync()).Entries);
            Assert.Equal(PlannedAction.Skip, entry.Action);
            Assert.Equal(SkipTier.Fingerprint, entry.SkipTier);
        }
    }

    [Fact]
    public async Task A_newer_version_queued_behind_an_in_flight_delivery_lands_after_it_and_the_final_check_sends_only_what_changed()
    {
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var drop1 = await DropAsync("flight1", records, Submission1, sourceVersion: 1);
        var (runtime, protocol, ledger) = await RuntimeAsync(drop1);
        using (runtime)
        {
            await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.DropLocation, force: false);
            var target = records[0].Key.Value.ToString("N");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            protocol.Before = async (work, ct) =>
            {
                if (work.TargetId.EndsWith(target, StringComparison.Ordinal) && entered.TrySetResult())
                {
                    await release.Task.WaitAsync(ct);
                }
            };

            var first = new DeliveryWorker(ledger, runtime.Context.Drops, runtime.Context.Stores, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "first-worker") { MaxWait = null };
            var drain = first.DrainAsync(Submission1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // While the first version is on its way, the source row changes and the next drop is taken in.
            var edited = records[0] with { Creator = "HAL", UpdateDate = "2026-09-05T10:00:00Z" };
            var drop2 = await DropAsync("flight2", [edited], Submission2, sourceVersion: 2);
            var (runtime2, _, ledger2) = await RuntimeAsync(drop2);
            using (runtime2)
            {
                var intake = await runtime2.Intake.IntakeAsync(runtime2.Flow, runtime2.Mapping, runtime2.Parameters, runtime2.DropLocation, force: false);
                Assert.Equal(1, intake.Counts.Planned);
                var queued = await ledger2.GetRecordAsync(runtime2.Flow.Id, records[0].Key);
                Assert.Equal(RecordStatus.Delivering, queued!.Status);
                Assert.Equal(Submission2, queued.LastSubmissionId);

                release.TrySetResult();
                await drain;

                // The first version landed; the newer one waits for the next pass, and nothing about it was lost.
                var settled = await ledger2.GetRecordAsync(runtime2.Flow.Id, records[0].Key);
                Assert.Equal(RecordStatus.Pending, settled!.Status);
                Assert.Equal(new DateTime(2026, 9, 1, 10, 15, 0, DateTimeKind.Utc), settled.SourceModifiedUtc);
                Assert.NotEqual(settled.MetadataHash, settled.PendingMetadataHash);
                Assert.True(settled.PendingPayload);

                var next = new DeliveryWorker(ledger2, runtime2.Context.Drops, runtime2.Context.Stores, protocol, runtime2.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "second-worker") { MaxWait = null };
                Assert.Equal(1, (await next.DrainAsync(Submission2)).Delivered);
                var sends = protocol.Deliveries.Where(w => w.TargetId.EndsWith(target, StringComparison.Ordinal)).ToList();
                Assert.Equal(2, sends.Count);
                // The payload the newer work carried is the one that just landed, so the final check sends the metadata alone.
                Assert.True(sends[1].DeliverMetadata);
                Assert.False(sends[1].DeliverPayload);

                var landed = await ledger2.GetRecordAsync(runtime2.Flow.Id, records[0].Key);
                Assert.Equal(RecordStatus.Delivered, landed!.Status);
                Assert.Equal(settled.PendingMetadataHash, landed.MetadataHash);
                Assert.Equal(new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc), landed.SourceModifiedUtc);

                // Each submission accounts for the delivery it made.
                Assert.Equal(3, (await runtime.Intake.CompleteAsync(Submission1, runtime.Flow.Id)).Delivered);
                Assert.Equal(1, (await runtime2.Intake.CompleteAsync(Submission2, runtime2.Flow.Id)).Delivered);
            }
        }
    }

    [Fact]
    public async Task Payload_chunk_files_are_the_watermark_when_the_flow_takes_their_modified_times()
    {
        static FlowDefinition ByFiles(FlowDefinition flow) => flow with { Change = flow.Change with { PayloadDetect = ChangeDetection.LastModified } };
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var t1 = new DateTime(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddHours(4);

        async Task<string> FilesDropAsync(string name, Guid submission, long version, Func<SampleRecord, DateTime> modified)
        {
            var dir = Path.Combine(_root, name);
            await SampleDropBuilder.WriteAsync(dir, "STAT_COMP", records, submission, version, payloadHash: false);
            foreach (var record in records)
            {
                foreach (var chunk in Directory.GetFiles(Path.Combine(dir, "curves", record.Key.ToString())))
                {
                    File.SetLastWriteTimeUtc(chunk, modified(record));
                }
            }

            return dir;
        }

        // A drop without a payload hash cannot be planned by content hash.
        var bare = await FilesDropAsync("files0", Guid.NewGuid(), 1, _ => t1);
        var (byHash, _, _) = await RuntimeAsync(bare);
        using (byHash)
        {
            var refused = await Assert.ThrowsAsync<FlowValidationException>(() => byHash.PlanAsync());
            Assert.Contains("declares no hashColumn", refused.Message, StringComparison.Ordinal);
        }

        var drop1 = await FilesDropAsync("files1", Submission1, 1, _ => t1);
        var (runtime, protocol, ledger) = await RuntimeAsync(drop1, ByFiles);
        using (runtime)
        {
            Assert.Equal(3, (await RunAsync(runtime, protocol, ledger, Submission1)).Delivered);
            Assert.Equal(t1, (await ledger.GetRecordAsync(runtime.Flow.Id, records[0].Key))!.PayloadModifiedUtc);
        }

        // The same files at the same times, read from another drop: decided without rendering, nothing to send.
        var drop2 = await FilesDropAsync("files2", Submission2, 2, _ => t1);
        var (runtime2, _, _) = await RuntimeAsync(drop2, ByFiles);
        using (runtime2)
        {
            Assert.All((await runtime2.PlanAsync()).Entries, e => Assert.Equal(SkipTier.Fingerprint, e.SkipTier));
        }

        // One record's chunk file was rewritten later: its payload is sent again, and only its payload.
        var drop3 = await FilesDropAsync("files3", Submission3, 3, r => r.Key == records[0].Key ? t2 : t1);
        var (runtime3, protocol3, ledger3) = await RuntimeAsync(drop3, ByFiles);
        using (runtime3)
        {
            var plan = await runtime3.PlanAsync();
            Assert.Equal(PlannedAction.UpdatePayload, plan.Entries.Single(e => e.Key == records[0].Key).Action);
            Assert.All(plan.Entries.Where(e => e.Key != records[0].Key), e => Assert.Equal(PlannedAction.Skip, e.Action));
            Assert.Equal(1, (await RunAsync(runtime3, protocol3, ledger3, Submission3)).Delivered);
            var work = Assert.Single(protocol3.Deliveries);
            Assert.False(work.DeliverMetadata);
            Assert.True(work.DeliverPayload);
            Assert.Equal(t2, (await ledger3.GetRecordAsync(runtime3.Flow.Id, records[0].Key))!.PayloadModifiedUtc);
        }

        // Chunk files older than the payload OSDU holds are stale: never sent, and recorded as such.
        var drop4 = await FilesDropAsync("files4", Guid.NewGuid(), 4, r => r.Key == records[0].Key ? t1.AddHours(1) : t1);
        var (runtime4, protocol4, ledger4) = await RuntimeAsync(drop4, ByFiles);
        using (runtime4)
        {
            var plan = await runtime4.PlanAsync();
            var stale = plan.Entries.Single(e => e.Key == records[0].Key);
            Assert.Equal(PlannedAction.Skip, stale.Action);
            Assert.Equal(SkipTier.Stale, stale.SkipTier);
            await RunAsync(runtime4, protocol4, ledger4, plan.Drop.Manifest.SubmissionId);
            Assert.Empty(protocol4.Deliveries);
            Assert.Equal(1, (await ledger4.GetSubmissionAsync(plan.Drop.Manifest.SubmissionId))!.SkippedStale);
        }
    }

    [Fact]
    public async Task Drop_prepared_for_another_mapping_or_flow_is_refused()
    {
        var drop = await DropAsync("mismatch", SampleDropBuilder.DefaultRecords("STAT_COMP"), Submission1, 1);
        var manifestPath = Path.Combine(drop, "manifest.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("WellLog@1.4.0", "WellLog@1.5.0", StringComparison.Ordinal));
        var engine = Samples.Engine(ledger: null);
        using var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(drop), new Dictionary<string, string> { ["logSource"] = "STAT_COMP" }, drop);
        var ex = await Assert.ThrowsAsync<FlowValidationException>(() => runtime.PlanAsync());
        Assert.Contains("prepared for mapping 'WellLog@1.5.0'", ex.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class ProtocolTests
{
    private static (OsduHttpClient Client, FakeHttpHandler Handler, HttpRuntime Runtime) Client(FakeHttpHandler? handler = null)
    {
        handler ??= new FakeHttpHandler();
        var runtime = new HttpRuntime(new FlowReliability { Retry = new FlowRetry { Attempts = 2, BaseDelayMs = 1, MaxDelayMs = 1 } }, new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(runtime, "http://localhost/petrodb", new TargetAuth { Type = TargetAuthType.None }, new Dictionary<string, string> { ["data-partition-id"] = "dev" });
        return (client, handler, runtime);
    }

    private static DeliveryWork Work(bool metadata, bool payload, int chunks, long? existing = null, IPayloadSource? source = null) => new()
    {
        Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("test", ["abc"]),
        TargetId = "dev:work-product-component--WellLog:abc",
        Document = TestSchema.Doc("""{"id":"dev:work-product-component--WellLog:abc","kind":"k","data":{"Name":"n"}}"""),
        DeliverMetadata = metadata,
        DeliverPayload = payload,
        Payload = source ?? new MemoryPayload(chunks),
        ExistingVersion = existing,
    };

    [Fact]
    public async Task WellLog_single_chunk_posts_record_then_streams_data()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/ddms/v3/welllogs", HttpStatusCode.OK, """{"recordCount":1,"recordIds":["dev:work-product-component--WellLog:abc"],"recordIdVersions":["dev:work-product-component--WellLog:abc:1699999"]}""")
            .On(HttpMethod.Post, "/data", HttpStatusCode.OK, "{}");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduWellLogProtocol(client, new ProtocolOptions(), Samples.Logger<OsduWellLogProtocol>());
            var outcome = await protocol.DeliverAsync(Work(true, true, 1));
            Assert.Equal(1699999, outcome.TargetVersion);
            Assert.Equal(1, outcome.ChunksSent);
            Assert.Equal(2, handler.Calls.Count);
            Assert.StartsWith("[{", handler.Calls[0].Body, StringComparison.Ordinal);
            Assert.Equal("dev", handler.Calls[0].Headers["data-partition-id"]);
            Assert.Equal("application/x-parquet", handler.Calls[1].ContentType);
            Assert.StartsWith("PAR1", handler.Calls[1].Body, StringComparison.Ordinal);
            Assert.EndsWith("/welllogs/dev:work-product-component--WellLog:abc/data", handler.Calls[1].Uri.AbsolutePath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Several_chunks_always_open_a_session_because_the_bulk_endpoint_replaces_the_whole_bulk()
    {
        // POST /welllogs/{id}/data carries "the entire bulk which will replace as latest version any previous
        // bulk", so posting three chunks to it would leave the record holding the third and report three delivered.
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, """{"id":"sess-9"}""")
            .On(HttpMethod.Post, "/sessions/sess-9/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Patch, "/sessions/sess-9", HttpStatusCode.OK, "{}");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduWellLogProtocol(client, new ProtocolOptions { SessionThresholdChunks = 1 }, Samples.Logger<OsduWellLogProtocol>());
            var outcome = await protocol.DeliverAsync(Work(false, true, 3));

            Assert.Equal(3, outcome.ChunksSent);
            Assert.Equal("sess-9", outcome.Returned["sessionId"]);
            Assert.DoesNotContain(handler.Calls, c => c.Uri.AbsolutePath.EndsWith("/welllogs/dev:work-product-component--WellLog:abc/data", StringComparison.Ordinal));
            Assert.Equal(3, handler.Calls.Count(c => c.Uri.AbsolutePath.EndsWith("/sessions/sess-9/data", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task A_threshold_of_zero_opens_a_session_even_for_one_chunk()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, """{"id":"sess-0"}""")
            .On(HttpMethod.Post, "/sessions/sess-0/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Patch, "/sessions/sess-0", HttpStatusCode.OK, "{}");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduWellLogProtocol(client, new ProtocolOptions { SessionThresholdChunks = 0 }, Samples.Logger<OsduWellLogProtocol>());
            var outcome = await protocol.DeliverAsync(Work(false, true, 1));

            Assert.Equal(1, outcome.ChunksSent);
            Assert.Equal("sess-0", outcome.Returned["sessionId"]);
        }
    }

    [Fact]
    public async Task A_commit_resent_after_a_lost_response_settles_on_the_session_state_rather_than_failing()
    {
        // The commit is a PATCH and the retry stack resends it, so a commit that worked and whose response was lost
        // meets a session that is no longer open. The session says which happened.
        var committed = new FakeHttpHandler()
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, """{"id":"sess-c"}""")
            .On(HttpMethod.Post, "/sessions/sess-c/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Patch, "/sessions/sess-c", HttpStatusCode.Conflict, """{"detail":"session is not open"}""")
            .On(HttpMethod.Get, "/sessions/sess-c", HttpStatusCode.OK, """{"id":"sess-c","state":"committed"}""");
        var (client, _, runtime) = Client(committed);
        using (runtime)
        {
            var protocol = new OsduWellLogProtocol(client, new ProtocolOptions(), Samples.Logger<OsduWellLogProtocol>());
            var outcome = await protocol.DeliverAsync(Work(false, true, 2));

            Assert.Equal(2, outcome.ChunksSent);
            Assert.Single(committed.Calls, c => c.Method == HttpMethod.Get);
        }

        // A session that is not committed is a real failure, and the payload is reported as not landed.
        var abandoned = new FakeHttpHandler()
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, """{"id":"sess-a"}""")
            .On(HttpMethod.Post, "/sessions/sess-a/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Patch, "/sessions/sess-a", HttpStatusCode.Conflict, """{"detail":"session expired"}""")
            .On(HttpMethod.Get, "/sessions/sess-a", HttpStatusCode.OK, """{"id":"sess-a","state":"abandoned"}""");
        var (client2, _, runtime2) = Client(abandoned);
        using (runtime2)
        {
            var protocol = new OsduWellLogProtocol(client2, new ProtocolOptions(), Samples.Logger<OsduWellLogProtocol>());
            var ex = await Assert.ThrowsAsync<DeliveryException>(() => protocol.DeliverAsync(Work(false, true, 2)));

            Assert.Contains("abandoned", ex.Message, StringComparison.Ordinal);
            Assert.Contains("did not land", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WellLog_multi_chunk_uses_a_session_and_abandons_on_failure()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, """{"id":"sess-1","mode":"overwrite"}""")
            .On(HttpMethod.Post, "/sessions/sess-1/data", hit => hit == 1 ? FakeHttpHandler.Json(HttpStatusCode.UnprocessableEntity, "bad chunk") : FakeHttpHandler.Json(HttpStatusCode.OK, "{}"))
            .On(HttpMethod.Patch, "/sessions/sess-1", HttpStatusCode.OK, "{}");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduWellLogProtocol(client, new ProtocolOptions(), Samples.Logger<OsduWellLogProtocol>());
            var ex = await Assert.ThrowsAsync<HttpStatusException>(() => protocol.DeliverAsync(Work(false, true, 3, existing: 5)));
            Assert.Equal(422, ex.StatusCode);
            var create = handler.Calls[0];
            Assert.Contains("\"fromVersion\":5", create.Body, StringComparison.Ordinal);
            Assert.Contains("\"mode\":\"overwrite\"", create.Body, StringComparison.Ordinal);
            var abandon = handler.Calls.Last();
            Assert.Equal(HttpMethod.Patch, abandon.Method);
            Assert.Contains("abandon", abandon.Body, StringComparison.Ordinal);
        }

        var ok = new FakeHttpHandler()
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, """{"id":"sess-2"}""")
            .On(HttpMethod.Post, "/sessions/sess-2/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Patch, "/sessions/sess-2", HttpStatusCode.OK, "{}");
        var (client2, _, runtime2) = Client(ok);
        using (runtime2)
        {
            var protocol = new OsduWellLogProtocol(client2, new ProtocolOptions(), Samples.Logger<OsduWellLogProtocol>());
            var outcome = await protocol.DeliverAsync(Work(false, true, 3));
            Assert.Equal(3, outcome.ChunksSent);
            var sent = ok.Calls.Where(c => c.Uri.AbsolutePath.EndsWith("/data", StringComparison.Ordinal)).Select(c => c.Body).ToList();
            Assert.Equal(3, sent.Count);
            Assert.All(sent, body => Assert.StartsWith("PAR1", body, StringComparison.Ordinal));
            Assert.Equal(3, sent.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains("commit", ok.Calls.Last().Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WellLog_holds_a_chunk_above_the_wellbore_ddms_bulk_ceilings_before_writing_metadata()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/ddms/v3/welllogs", HttpStatusCode.OK, """{"recordIdVersions":["dev:work-product-component--WellLog:abc:1"]}""")
            .On(HttpMethod.Post, "/data", HttpStatusCode.OK, "{}");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            // 40 rows by 4 columns is 160 values, so a ceiling of 100 values holds it.
            var values = new OsduWellLogProtocol(client, new ProtocolOptions { MaxChunkValues = 100 }, Samples.Logger<OsduWellLogProtocol>());
            var tooManyValues = await Assert.ThrowsAsync<RecordHeldException>(
                () => values.DeliverAsync(Work(true, true, 1, source: new MemoryPayload(1, columns: 4, rowsPerChunk: 40))));
            Assert.Contains("160 values (40 rows by 4 columns)", tooManyValues.Message, StringComparison.Ordinal);
            Assert.Contains("maxChunkValues", tooManyValues.Message, StringComparison.Ordinal);

            var columns = new OsduWellLogProtocol(client, new ProtocolOptions { MaxChunkColumns = 3 }, Samples.Logger<OsduWellLogProtocol>());
            var tooManyColumns = await Assert.ThrowsAsync<RecordHeldException>(
                () => columns.DeliverAsync(Work(true, true, 1, source: new MemoryPayload(1, columns: 4, rowsPerChunk: 2))));
            Assert.Contains("has 4 columns", tooManyColumns.Message, StringComparison.Ordinal);
            Assert.Contains("maxChunkColumns", tooManyColumns.Message, StringComparison.Ordinal);

            // The record is held before the metadata write, so a held payload never leaves a record without one.
            Assert.Empty(handler.Calls);
        }
    }

    [Fact]
    public async Task WellLog_checks_the_shape_only_for_parquet_payloads_and_only_when_a_ceiling_is_in_force()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/data", HttpStatusCode.OK, "{}");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            // A payload the target takes as JSON is not measurable from a parquet footer, so it is not measured.
            var json = new ProtocolOptions { PayloadContentType = "application/json", MaxChunkValues = 1, MaxChunkColumns = 1 };
            var outcome = await new OsduWellLogProtocol(client, json, Samples.Logger<OsduWellLogProtocol>()).DeliverAsync(Work(false, true, 1));
            Assert.Equal(1, outcome.ChunksSent);

            // Both ceilings off is the explicit opt out for a target that has raised them.
            var off = new ProtocolOptions { MaxChunkValues = 0, MaxChunkColumns = 0 };
            var second = await new OsduWellLogProtocol(client, off, Samples.Logger<OsduWellLogProtocol>())
                .DeliverAsync(Work(false, true, 1, source: new MemoryPayload(1, columns: 8, rowsPerChunk: 8)));
            Assert.Equal(1, second.ChunksSent);
        }
    }

    [Fact]
    public async Task WellLog_holds_a_chunk_that_is_declared_parquet_but_is_not()
    {
        var (client, handler, runtime) = Client();
        using (runtime)
        {
            var protocol = new OsduWellLogProtocol(client, new ProtocolOptions(), Samples.Logger<OsduWellLogProtocol>());
            var ex = await Assert.ThrowsAsync<RecordHeldException>(() => protocol.DeliverAsync(Work(true, true, 1, source: new NotParquetPayload())));
            Assert.Contains("declared as parquet but its footer could not be read", ex.Message, StringComparison.Ordinal);
            Assert.Empty(handler.Calls);
        }
    }

    [Fact]
    public void Bulk_limits_carry_the_documented_wellbore_ddms_numbers()
    {
        Assert.Equal(10_000_000, WellboreDdmsBulkLimits.MaxChunkValues);
        Assert.Equal(3_000, WellboreDdmsBulkLimits.MaxChunkColumns);
        Assert.Equal(500, WellboreDdmsBulkLimits.MaxChunkColumnsThroughM25);
        Assert.Equal(WellboreDdmsBulkLimits.MaxChunkValues, new ProtocolOptions().MaxChunkValues);
        Assert.Equal(WellboreDdmsBulkLimits.MaxChunkColumns, new ProtocolOptions().MaxChunkColumns);

        // A shape no file can hold saturates instead of overflowing into a value that would pass the check.
        Assert.Equal(long.MaxValue, WellboreDdmsBulkLimits.Values(long.MaxValue, 2));
        Assert.Equal(0, WellboreDdmsBulkLimits.Values(10, 0));
        Assert.Null(WellboreDdmsBulkLimits.Exceeded(0, "chunk_00000.parquet", 1000, 10, 10_000, 3_000));
        Assert.Null(WellboreDdmsBulkLimits.Exceeded(0, "chunk_00000.parquet", long.MaxValue, 4_000, 0, 0));
    }

    [Fact]
    public async Task Record_protocol_puts_arrays_preserves_keys_and_verifies_versions()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/records/dev:work-product-component--WellLog:abc", HttpStatusCode.OK, """{"id":"x","version":7,"data":{"Datasets":["ds1"],"Name":"old"}}""")
            .On(HttpMethod.Put, "/records", HttpStatusCode.Created, """{"recordIdVersions":["dev:work-product-component--WellLog:abc:8"]}""");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions { PreserveDataKeys = ["Datasets"] });
            var outcome = await protocol.DeliverAsync(Work(true, false, 0, existing: 7));
            Assert.Equal(8, outcome.TargetVersion);
            var put = handler.Calls.Single(c => c.Method == HttpMethod.Put);
            Assert.Contains("\"Datasets\":[\"ds1\"]", put.Body, StringComparison.Ordinal);
            Assert.Contains("\"Name\":\"n\"", put.Body, StringComparison.Ordinal);

            var verify = await protocol.VerifyAsync("dev:work-product-component--WellLog:abc", 8);
            Assert.Equal(VerifyOutcome.Drifted, verify.Outcome);
            Assert.Equal(7, verify.ObservedVersion);
            var match = await protocol.VerifyAsync("dev:work-product-component--WellLog:abc", 7);
            Assert.Equal(VerifyOutcome.Match, match.Outcome);
        }

        var missing = new FakeHttpHandler().On(HttpMethod.Get, "/records/gone", HttpStatusCode.NotFound, null);
        var (client2, _, runtime2) = Client(missing);
        using (runtime2)
        {
            var protocol = new OsduRecordProtocol(client2, new ProtocolOptions());
            Assert.Equal(VerifyOutcome.Missing, (await protocol.VerifyAsync("gone", 1)).Outcome);
        }
    }

    [Fact]
    public async Task A_record_write_asks_storage_to_skip_duplicates_only_when_the_flow_opts_in()
    {
        // Opted in, skipdupes is sent, and a record the service names under skippedRecordIds settles on the version
        // the ledger already held. By default it is not sent: the spec does not say what the service compares, and an
        // envelope-only change must never be skipped while the ledger records it as delivered.
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Put, "/records", HttpStatusCode.Created, """{"recordIdVersions":[],"skippedRecordIds":["dev:work-product-component--WellLog:abc"]}""");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions { SkipDuplicates = true });
            var outcome = await protocol.DeliverAsync(Work(true, false, 0, existing: 4));

            Assert.True(outcome.MetadataDelivered);
            Assert.Equal(4, outcome.TargetVersion);
            Assert.Equal("true", outcome.Returned["skipped"]);
            Assert.Contains("unchanged at the target", outcome.Detail, StringComparison.Ordinal);
            Assert.Contains("skipdupes=true", handler.Calls.Single().Uri.Query, StringComparison.Ordinal);
        }

        var plain = new FakeHttpHandler()
            .On(HttpMethod.Put, "/records", HttpStatusCode.Created, """{"recordIdVersions":["dev:work-product-component--WellLog:abc:9"]}""");
        var (client2, _, runtime2) = Client(plain);
        using (runtime2)
        {
            var protocol = new OsduRecordProtocol(client2, new ProtocolOptions());
            var outcome = await protocol.DeliverAsync(Work(true, false, 0, existing: 4));

            Assert.Equal(9, outcome.TargetVersion);
            Assert.DoesNotContain("skipdupes", plain.Calls.Single().Uri.Query, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Verifying_many_records_is_one_batched_read_that_separates_drift_from_absence_from_refusal()
    {
        var handler = new FakeHttpHandler().On(
            HttpMethod.Post,
            "/query/records",
            HttpStatusCode.OK,
            """{"records":[{"id":"dev:x:match","version":3},{"id":"dev:x:drifted","version":9}],"invalidRecords":["dev:x:refused"]}""");
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            IDeliveryProtocol protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            Assert.Equal(OsduRecordProtocol.MaxVerifyBatch, protocol.MaxVerifyBatch);

            var results = await protocol.VerifyBatchAsync(
            [
                new VerifyRequest("dev:x:match", 3),
                new VerifyRequest("dev:x:drifted", 3),
                new VerifyRequest("dev:x:refused", 3),
                new VerifyRequest("dev:x:absent", 3),
                new VerifyRequest("dev:x:adopted", null),
            ]);

            Assert.Equal(VerifyOutcome.Match, results[0].Outcome);
            Assert.Equal(3, results[0].ObservedVersion);
            Assert.Equal(VerifyOutcome.Drifted, results[1].Outcome);
            Assert.Equal(9, results[1].ObservedVersion);
            Assert.Contains("observed version 9", results[1].Detail, StringComparison.Ordinal);
            Assert.Equal(VerifyOutcome.Error, results[2].Outcome);
            Assert.Contains("invalid or unreadable", results[2].Detail, StringComparison.Ordinal);
            Assert.Equal(VerifyOutcome.Missing, results[3].Outcome);
            Assert.Equal(VerifyOutcome.Missing, results[4].Outcome);

            // Five records, one request: this is what keeps a drift pass over a large estate off one call per record.
            var call = Assert.Single(handler.Calls);
            var body = JsonNode.Parse(call.Body!)!.AsObject();
            Assert.Equal(5, body["records"]!.AsArray().Count);
            Assert.Equal("id", body["attributes"]![0]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_401_is_retried_once_under_a_freshly_resolved_token()
    {
        var handler = new FakeHttpHandler().On(
            HttpMethod.Put,
            "/records",
            hit => hit == 0
                ? FakeHttpHandler.Json(HttpStatusCode.Unauthorized, """{"code":401,"reason":"Unauthorized"}""")
                : FakeHttpHandler.Json(HttpStatusCode.Created, """{"recordIdVersions":["dev:work-product-component--WellLog:abc:2"]}"""));
        var (client, _, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var outcome = await protocol.DeliverAsync(Work(true, false, 0));

            Assert.Equal(2, outcome.TargetVersion);
            Assert.Equal(2, handler.Calls.Count);
        }

        // A 401 that survives the fresh token is a real authorisation failure and is not tried a third time.
        var refusing = new FakeHttpHandler().On(HttpMethod.Put, "/records", HttpStatusCode.Unauthorized, """{"code":401,"reason":"Unauthorized"}""");
        var (client2, _, runtime2) = Client(refusing);
        using (runtime2)
        {
            var protocol = new OsduRecordProtocol(client2, new ProtocolOptions());
            var ex = await Assert.ThrowsAsync<HttpStatusException>(() => protocol.DeliverAsync(Work(true, false, 0)));

            Assert.Equal(401, ex.StatusCode);
            Assert.Equal(2, refusing.Calls.Count);
        }
    }

    /// <summary>A chunk the manifest calls parquet that is not one.</summary>
    private sealed class NotParquetPayload : IPayloadSource
    {
        public Task<IReadOnlyList<Drops.PayloadChunk>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Drops.PayloadChunk>>([new Drops.PayloadChunk(0, "mem://chunk_0.parquet", 7)]);

        public Task<Stream> OpenAsync(Drops.PayloadChunk chunk, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("chunk-0")));
    }

    /// <summary>
    /// Real parquet chunks, because the protocol reads each chunk's footer to check it against the wellbore DDMS
    /// bulk ceilings before sending it. Each chunk carries one row per chunk index so the requests stay distinct.
    /// </summary>
    private sealed class MemoryPayload(int chunks, int columns = 2, int rowsPerChunk = 1) : IPayloadSource
    {
        public Task<IReadOnlyList<Drops.PayloadChunk>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Drops.PayloadChunk>>(
                Enumerable.Range(0, chunks).Select(i => new Drops.PayloadChunk(i, $"mem://chunk_{i}.parquet", Bytes(i).Length)).ToList());

        public Task<Stream> OpenAsync(Drops.PayloadChunk chunk, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(Bytes(chunk.Index), writable: false));

        private byte[] Bytes(int index)
        {
            var names = new List<(string Name, Type ClrType)> { ("MD", typeof(double)) };
            for (var c = 1; c < columns; c++)
            {
                names.Add(("CURVE_" + c.ToString(System.Globalization.CultureInfo.InvariantCulture), typeof(double)));
            }

            var rows = new List<IReadOnlyDictionary<string, object?>>(rowsPerChunk);
            for (var r = 0; r < rowsPerChunk; r++)
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (name, _) in names)
                {
                    row[name] = (double)((index * rowsPerChunk) + r);
                }

                rows.Add(row);
            }

            using var buffer = new MemoryStream();
            SqlFlow.Delivery.Storage.ParquetScopeReader.WriteAsync(buffer, names, rows).GetAwaiter().GetResult();
            return buffer.ToArray();
        }
    }
}
