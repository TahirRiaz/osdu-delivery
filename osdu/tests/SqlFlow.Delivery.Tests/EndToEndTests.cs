using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Engine.Verify;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// End to end over the sample mapping, the real WellLog 1.4.0 schema snapshot, the sample estate in the in-memory
/// ingestion tables, the module's database on SQL Server and a fake protocol.
/// </summary>
public class EndToEndTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private async Task<MemoryIngestionTables> EstateAsync() => await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);

    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> RuntimeAsync(
        MemoryIngestionTables tables, Func<FlowDefinition, FlowDefinition>? adjust = null, FakeProtocol? protocol = null,
        FakeProtocolFactory? protocols = null, IRecordSearchFactory? searches = null)
    {
        var ledger = _db.Ledger(_clock);
        protocol ??= new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables, searches: searches) with { Protocols = protocols ?? new FakeProtocolFactory(protocol) };
        var flow = adjust is null ? Samples.LocalFlow(_root) : adjust(Samples.LocalFlow(_root));
        var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        return (runtime, protocol, ledger);
    }

    /// <summary>One run's work: plan into batches, drain them, and close the submission, as a deliver run does.</summary>
    private async Task<(WorkerSummary Work, Guid SubmissionId)> RunAsync(FlowRuntime runtime, FakeProtocol protocol, OsduLedger ledger, bool force = false)
    {
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force);
        if (intake.NothingToDo)
        {
            return (WorkerSummary.Empty, intake.Submission.SubmissionId);
        }

        var worker = new DeliveryWorker(
            ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
            CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };
        var summary = await worker.DrainAsync(intake.Submission.SubmissionId);
        await runtime.Intake.CompleteAsync(intake.Submission.SubmissionId, runtime.Flow.Id);
        return (summary, intake.Submission.SubmissionId);
    }

    [Fact]
    public async Task Plan_without_a_ledger_creates_everything()
    {
        var tables = await EstateAsync();
        var engine = Samples.Engine(ledger: null, _clock, sources: tables);
        using var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        runtime.Selection = SourceSelection.Full();

        var plan = await runtime.PlanAsync();

        Assert.Equal(3, plan.Entries.Count);
        Assert.All(plan.Entries, e => Assert.Equal(PlannedAction.Create, e.Action));
        Assert.All(plan.Entries, e => Assert.Equal(1, e.ChunkCount));
        Assert.All(plan.Entries, e => Assert.StartsWith("dev:work-product-component--WellLog:", e.TargetId!, StringComparison.Ordinal));
        // Every entry carries the row it was read from, which is what the ledger records as the record's origin.
        Assert.All(plan.Entries, e => Assert.Equal(SampleEstate.FileName, e.Origin.FileName));
        Assert.All(plan.Entries, e => Assert.NotNull(e.SourceKeyJson));
        var doc = plan.Entries[0].Render!.Document;
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", doc["data"]!["VerticalMeasurement"]!["VerticalMeasurementUnitOfMeasureID"]!.GetValue<string>());
    }

    [Fact]
    public async Task Every_record_a_run_settles_is_counted_in_the_delivery_metrics()
    {
        using var capture = new MetricsCapture();
        var name = $"metrics-{Guid.NewGuid():N}";
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, f => f with { Name = name });
        using (runtime)
        {
            var (summary, _) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(3, summary.Delivered);

            var counted = capture.Of("osdu_delivery.records", "flow", runtime.Flow.Label);
            Assert.Equal(3, counted.Count);
            Assert.All(counted, c => Assert.Equal("delivered", c.Tags["outcome"]));
            Assert.All(counted, c => Assert.Equal(DeliveryProtocols.Name(protocol.Kind), c.Tags["route"]));
            Assert.Equal(3, capture.Of("osdu_delivery.record.duration", "flow", runtime.Flow.Label).Count);
        }
    }

    [Fact]
    public async Task A_record_waits_for_the_record_it_refers_to_and_goes_out_when_that_one_lands()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            // The wellbore the sample well logs refer to is another flow's record of this ledger, queued and not yet
            // delivered: the well logs point at a record that is not in OSDU.
            var wellboreFlow = FlowId.Of("wells-wellbore-03-header-delivery");
            const string WellboreId = "dev:master-data--Wellbore:OSDU-DEV-1-A";
            var wellbore = new DeliveryKey(Guid.NewGuid());
            var submission = Guid.NewGuid();
            await ledger.RegisterSubmissionAsync(new SubmissionState
            {
                SubmissionId = submission,
                FlowId = wellboreFlow,
                FlowName = "wells-wellbore-03-header-delivery",
                MappingReference = "Wellbore@1.3.0",
                RenderContext = "{}",
                SourceConnection = "${env:OSDU_SAMPLE_DB}",
                SourceObject = "OsduSample.ing.Wellbore",
            });
            await ledger.UpsertPendingAsync(wellboreFlow, [new RecordState
            {
                DeliveryKey = wellbore,
                FlowId = wellboreFlow,
                SourceKey = "OSDU-DEV-1-A",
                MappingName = "Wellbore",
                TargetId = WellboreId,
                LastSubmissionId = submission,
                PendingDocumentRef = "0:0:10",
                PendingRenderContext = "{}",
                PendingMetadataHash = "mh",
                PendingMetadata = true,
            }]);

            // The two logs of that wellbore wait, and no try is charged for them; the third log refers to the other
            // wellbore, which no record of the ledger holds, so it goes out as it is.
            var (work, logSubmission) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(1, work.Delivered);
            Assert.Equal(2, work.Waiting);
            Assert.Single(protocol.Deliveries);
            var waiting = await ledger.ListAsync(runtime.Flow.Id, new RecordQuery { Status = RecordStatus.Waiting });
            Assert.Equal(2, waiting.Count);
            Assert.All(waiting, r => Assert.Equal(WellboreId, r.WaitingFor));
            Assert.All(waiting, r => Assert.Equal(0, r.AttemptCount));
            Assert.All(waiting, r => Assert.Contains("data.WellboreID", r.LastError!, StringComparison.Ordinal));
            Assert.Equal(2, (await ledger.GetSubmissionAsync(logSubmission))!.Waiting);
            Assert.Equal(2, (await ledger.StatsAsync(runtime.Flow.Id, Now)).Waiting);

            // The wellbore lands, which sends its waiters back to pending; the next drain of the same submission sends them.
            await ledger.CompleteAsync(wellboreFlow, new RecordCompletion
            {
                DeliveryKey = wellbore,
                Status = RecordStatus.Delivered,
                Promote = true,
                TargetVersion = 1,
                TargetId = WellboreId,
                Claimed = ClaimedWork.Of((await ledger.GetRecordAsync(wellboreFlow, wellbore))!),
                Attempt = new AttemptRecord
                {
                    DeliveryKey = wellbore,
                    Worker = "test",
                    StartedUtc = Now,
                    CompletedUtc = Now,
                    Outcome = AttemptOutcome.Delivered,
                    Phase = "metadata",
                },
            });

            var worker = new DeliveryWorker(
                ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
                CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };
            var sent = await worker.DrainAsync(logSubmission);

            Assert.Equal(2, sent.Delivered);
            Assert.Equal(0, sent.Waiting);
            Assert.Equal(3, protocol.Deliveries.Count);
            Assert.Empty(await ledger.ListAsync(runtime.Flow.Id, new RecordQuery { Status = RecordStatus.Waiting }));
        }
    }

    [Fact]
    public async Task With_the_storage_check_on_a_record_whose_reference_is_nowhere_is_held_before_anything_is_sent()
    {
        using var platform = new FakeOsduPlatform();
        using var http = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SqlFlow.Core.Secrets.SecretResolver([new SqlFlow.Core.Secrets.EnvSecretProvider()]), _clock, platform, allowLoopback: true);
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var check = new ReferenceCheck(
                ledger,
                new OsduHttpClient(
                    http, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" }),
                "/api/storage/v2/query/records");

            // Neither the ledger nor storage holds the wellbores the sample well logs refer to.
            var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
            var worker = new DeliveryWorker(
                ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
                CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null, References = check };
            var held = await worker.DrainAsync(intake.Submission.SubmissionId);

            Assert.Equal(3, held.Held);
            Assert.Equal(0, held.Delivered);
            Assert.Empty(protocol.Deliveries);
            var records = await ledger.ListAsync(runtime.Flow.Id, new RecordQuery { Status = RecordStatus.Held });
            Assert.Equal(3, records.Count);
            Assert.All(records, r => Assert.Contains("neither the ledger nor OSDU's storage service holds", r.LastError!, StringComparison.Ordinal));

            Assert.All(records, r => Assert.Contains("dev:master-data--Wellbore:OSDU-DEV-1-", r.LastError!, StringComparison.Ordinal));

            // Once storage holds every record they refer to (the wellbores, the units, the curve types), the released
            // records go out.
            foreach (var id in records.SelectMany(r => r.PendingReferences).Select(r => r.Id).Distinct(StringComparer.Ordinal))
            {
                platform.Records[id] = new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = id,
                    ["kind"] = "osdu:wks:master-data--Wellbore:1.3.0",
                    ["version"] = 1,
                };
            }

            Assert.Equal(3, await ledger.ReleaseAsync(runtime.Flow.Id, records.Select(r => r.DeliveryKey).ToList(), Now));
            var sent = await worker.DrainAsync(intake.Submission.SubmissionId);

            Assert.Equal(3, sent.Delivered);
            Assert.Equal(3, protocol.Deliveries.Count);
        }
    }

    [Fact]
    public async Task A_run_delivers_then_the_next_one_skips_a_scope_whose_rows_did_not_change()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var (summary, submissionId) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(3, summary.Delivered);
            Assert.Equal(3, protocol.Deliveries.Count);
            Assert.All(protocol.Deliveries, w => Assert.True(w.DeliverMetadata && w.DeliverPayload));

            var submission = await ledger.GetSubmissionAsync(submissionId);
            Assert.Equal(SubmissionStatus.Completed, submission!.Status);
            Assert.Equal(3, submission.Delivered);
            Assert.Equal(3, submission.Planned);
            Assert.Equal(runtime.Flow.Source.Record.Object, submission.SourceObject);
            Assert.Equal(runtime.Flow.Source.Connection, submission.SourceConnection);

            var state = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(RecordStatus.Delivered, state!.Status);
            Assert.NotNull(state.TargetVersion);
            Assert.NotNull(state.MetadataHash);
            Assert.NotNull(state.PayloadHash);
            Assert.Equal(SampleWellLogs.UpdatedUtc, state.SourceModifiedUtc);
            Assert.Equal(SampleEstate.FileName, state.SourceFileName);
            Assert.Equal(1, state.SourceRowNumber);

            // The whole-scope plan moved the scope's watermark to the window it covered.
            var watermark = await ledger.GetWatermarkAsync(runtime.Flow.Id, Planner.ScopeKey(runtime.Parameters));
            Assert.NotNull(watermark);
            Assert.Equal(submissionId, watermark!.SubmissionId);
        }

        // Nothing changed in the tables since: the next run reads its window, finds no row in it and skips the scope.
        var (next, nextProtocol, nextLedger) = await RuntimeAsync(tables);
        using (next)
        {
            next.Selection = SourceSelection.Incremental(
                (await nextLedger.GetWatermarkAsync(next.Flow.Id, Planner.ScopeKey(next.Parameters)))!.UpdatedThroughUtc);
            var plan = await next.PlanAsync();
            Assert.True(plan.SkippedWholeRun);
            Assert.Empty(plan.Entries);
            var (summary, _) = await RunAsync(next, nextProtocol, nextLedger);
            Assert.Equal(0, summary.Processed);
            Assert.Empty(nextProtocol.Deliveries);
        }
    }

    [Fact]
    public async Task A_row_that_changed_is_rendered_again_and_only_what_moved_is_sent()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(3, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();

            // A column the mapping does not read: the fingerprint moves, the rendered document does not, so nothing is sent.
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[0], "log_run", "1A", Now, SampleWellLogs.UpdatedUtc.AddHours(1));
            var plan = await runtime.PlanAsync(force: true);
            var entry = plan.Entries.Single(e => e.SourceKey.EndsWith("L-1001", StringComparison.Ordinal));
            Assert.Equal(PlannedAction.Skip, entry.Action);
            Assert.Equal(SkipTier.ContentHash, entry.SkipTier);

            // A column the mapping does read: the metadata is sent, and the payload is left alone.
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[0], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc.AddHours(2));
            var metadata = await runtime.PlanAsync(force: true);
            var changed = metadata.Entries.Single(e => e.SourceKey.EndsWith("L-1001", StringComparison.Ordinal));
            Assert.Equal(PlannedAction.UpdateMetadata, changed.Action);
            Assert.True(changed.DeliverMetadata);
            Assert.False(changed.DeliverPayload);

            var (summary, _) = await RunAsync(runtime, protocol, ledger, force: true);
            Assert.Equal(1, summary.Delivered);
            var work = Assert.Single(protocol.Deliveries);
            Assert.True(work.DeliverMetadata);
            Assert.False(work.DeliverPayload);
            Assert.NotNull(work.ExistingVersion);
        }
    }

    [Fact]
    public async Task Rewritten_curve_files_send_the_payload_and_nothing_else()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(3, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();

            // The curve values move: the payload hash on the row moves with them, the document does not.
            var log = SampleWellLogs.Logs()[0];
            var moved = log with { Curves = [log.Curves[0], log.Curves[1] with { First = 60.5 }, .. log.Curves.Skip(2)] };
            _clock.Advance(TimeSpan.FromMinutes(10));
            await SampleEstate.RewritePayloadAsync(_root, tables.Records[0], moved, Now);

            var plan = await runtime.PlanAsync(force: true);
            var entry = plan.Entries.Single(e => e.SourceKey.EndsWith("L-1001", StringComparison.Ordinal));
            Assert.Equal(PlannedAction.UpdatePayload, entry.Action);

            var (summary, _) = await RunAsync(runtime, protocol, ledger, force: true);
            Assert.Equal(1, summary.Delivered);
            var work = Assert.Single(protocol.Deliveries);
            Assert.False(work.DeliverMetadata);
            Assert.True(work.DeliverPayload);
        }
    }

    [Fact]
    public async Task A_batch_asks_every_name_once_in_one_round_and_a_wellbore_known_only_by_an_alias_in_the_next()
    {
        var tables = await EstateAsync();
        tables.Records[2].Row["wellbore_uwi"] = "OLD-NAME-B";
        var searches = new FixedRecordSearchFactory(
            ("data.FacilityName", "OSDU-DEV-1-A", FixedRecordSearchFactory.Partition + ":master-data--Wellbore:OSDU-DEV-1-A"),
            ("data.FacilityName", "OSDU-DEV-1-B", FixedRecordSearchFactory.Partition + ":master-data--Wellbore:OSDU-DEV-1-B"),
            ("data.NameAliases.AliasName", "OLD-NAME-B", FixedRecordSearchFactory.Partition + ":master-data--Wellbore:OSDU-DEV-1-B"));
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, searches: searches);
        using (runtime)
        {
            var (summary, _) = await RunAsync(runtime, protocol, ledger);

            Assert.Equal(3, summary.Delivered);
            var byAlias = Assert.Single(protocol.Deliveries, w => w.Key == SampleEstate.Key(2));
            Assert.Equal("dev:master-data--Wellbore:OSDU-DEV-1-B:", byAlias.Document["data"]!["WellboreID"]!.GetValue<string>());

            // Every distinct name is asked once, and the one no wellbore is named by is asked again as an alias, after.
            var asked = Assert.Single(searches.Created).Asked.ToList();
            var names = asked.Where(q => q.Field == "data.FacilityName").Select(q => q.Value).ToList();
            Assert.Equal(names.Distinct(StringComparer.Ordinal).Count(), names.Count);
            Assert.Contains("OLD-NAME-B", names);
            Assert.Equal(("data.NameAliases.AliasName", "OLD-NAME-B"), (asked[^1].Field, asked[^1].Value));
            Assert.Single(asked, q => q.Field == "data.NameAliases.AliasName");
        }
    }

    [Fact]
    public async Task Unresolvable_reference_holds_the_record_and_release_requeues_it()
    {
        var tables = await EstateAsync();
        tables.Records[2].Row["wellbore_uwi"] = "NO 99/9-Z-1";
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var (summary, submissionId) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(2, summary.Delivered);
            var submission = await ledger.GetSubmissionAsync(submissionId);
            Assert.Equal(1, submission!.Held);
            var held = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(2));
            Assert.Equal(RecordStatus.Held, held!.Status);
            Assert.Contains("no Wellbore on the platform (osdu:wks:master-data--Wellbore:*) matches", held.LastError, StringComparison.Ordinal);
            Assert.Contains("data.FacilityName 'NO 99/9-Z-1' found no record", held.LastError, StringComparison.Ordinal);
            Assert.Equal("NO 99/9-Z-1 / STAT_COMP / run 1 (L-2001)", held.Label);
            // The held record is traced to the row it was held at.
            Assert.Equal(SampleEstate.FileName, held.PendingSourceFileName);

            // The same rows again: the held record is blocked, not re-attempted (design.md section 7.4).
            var plan = await runtime.PlanAsync(force: true);
            var blocked = plan.Entries.Single(e => e.Action == PlannedAction.Blocked);
            Assert.Equal(SampleEstate.Key(2), blocked.Key);
            Assert.Contains("held since", blocked.Reason, StringComparison.Ordinal);

            // Released: the next plan renders it again (and holds it again, because the source is still wrong).
            Assert.Equal(1, await ledger.ReleaseAsync(runtime.Flow.Id, null, Now));
            var replanned = await runtime.PlanAsync(force: true);
            Assert.Equal(PlannedAction.Hold, replanned.Entries.Single(e => e.Key == SampleEstate.Key(2)).Action);
        }
    }

    [Fact]
    public async Task A_deleted_row_is_never_delivered_and_says_why()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(3, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);

            _clock.Advance(TimeSpan.FromMinutes(10));
            tables.Records[0].DeletedUtc = Now;
            tables.Records[0].UpdatedUtc = Now;

            var plan = await runtime.PlanAsync(force: true);
            var deleted = plan.Entries.Single(e => e.Key == SampleEstate.Key(0));
            Assert.Equal(PlannedAction.Hold, deleted.Action);
            Assert.Contains("marked the record row deleted", deleted.Reason, StringComparison.Ordinal);
            Assert.Contains("Remove the record from OSDU deliberately", deleted.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_record_scoped_run_reads_only_the_rows_it_names_by_key()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(3, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();

            // A redelivery of one record's payload, planned as a key-scoped read of exactly that row.
            await runtime.RedeliverAsync([SampleEstate.Key(1)], RedeliverScope.Payload);
            var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1));
            Assert.NotNull(record!.PlanRequestedUtc);

            runtime.Selection = SourceSelection.ForKeys([KeyTuple.FromJson(record.SourceKeyJson!)]);
            var (summary, submissionId) = await RunAsync(runtime, protocol, ledger, force: true);

            Assert.Equal(1, summary.Delivered);
            var sent = Assert.Single(protocol.Deliveries);
            Assert.Equal(SampleEstate.Key(1), sent.Key);
            Assert.False(sent.DeliverMetadata);
            Assert.True(sent.DeliverPayload);

            var submission = await ledger.GetSubmissionAsync(submissionId);
            Assert.Equal(SubmissionKinds.Keys, submission!.Kind);
            // A key-scoped plan says nothing about the rows it never read, so it never moves the scope's watermark.
            Assert.Null(submission.WindowFromUtc);
            Assert.Null((await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1)))!.PlanRequestedUtc);
        }
    }

    [Fact]
    public async Task A_key_the_tables_do_not_hold_is_reported_rather_than_silently_dropped()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            runtime.Selection = SourceSelection.ForKeys([KeyTuple.Of("NO_15_9", "L-9999")]);
            var plan = await runtime.PlanAsync(force: true);

            // Nothing to plan, and the key that matched no row is named rather than quietly left out of the result.
            Assert.Empty(plan.Entries);
            var missing = Assert.Single(plan.Header.Source.MissingKeys);
            Assert.Contains("L-9999", missing.Json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Transient_failures_back_off_and_succeed_later_while_terminal_statuses_hold()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var failures = 0;
            protocol.FailWith = work =>
            {
                if (work.Key == SampleEstate.Key(0) && failures++ == 0)
                {
                    return new OsduStatusException(503, "HTTP 503 Service Unavailable");
                }

                return work.Key == SampleEstate.Key(1) ? new OsduStatusException(409, "HTTP 409 Conflict: acl") : null;
            };

            var (summary, submissionId) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(1, summary.Delivered);
            Assert.Equal(1, summary.Retried);
            Assert.Equal(1, summary.Held);

            var retrying = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(RecordStatus.Pending, retrying!.Status);
            Assert.NotNull(retrying.NextAttemptUtc);
            var held = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1));
            Assert.Equal(RecordStatus.Held, held!.Status);
            Assert.Equal(SubmissionStatus.Running, (await ledger.GetSubmissionAsync(submissionId))!.Status);

            _clock.Advance(TimeSpan.FromMinutes(2));
            var worker = new DeliveryWorker(
                ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
                CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };
            var later = await worker.DrainAsync(submissionId);
            Assert.Equal(1, later.Delivered);
            var attempts = await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10);
            Assert.Equal(2, attempts.Count);
            Assert.Equal(AttemptOutcome.Delivered, attempts[0].Outcome);
            Assert.Equal(AttemptOutcome.Failed, attempts[1].Outcome);
            await runtime.Intake.CompleteAsync(submissionId, runtime.Flow.Id);
            Assert.Equal(SubmissionStatus.Completed, (await ledger.GetSubmissionAsync(submissionId))!.Status);
        }
    }

    [Fact]
    public async Task A_record_the_service_asked_to_wait_on_is_not_attempted_again_sooner()
    {
        // The transport does not sit through a long Retry-After; it hands the wait up with the failure, and the
        // worker must not schedule the record's next attempt earlier than the service asked.
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            protocol.FailWith = work => work.Key == SampleEstate.Key(0)
                ? new OsduStatusException(429, "HTTP 429 Too Many Requests", TimeSpan.FromHours(2))
                : null;

            var (summary, _) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(1, summary.Retried);

            var waiting = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(RecordStatus.Pending, waiting!.Status);
            Assert.NotNull(waiting.NextAttemptUtc);
            Assert.True(waiting.NextAttemptUtc!.Value >= Now + TimeSpan.FromHours(2));
        }
    }

    [Fact]
    public async Task Stopping_the_worker_releases_in_flight_records_without_charging_an_attempt()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
            Assert.Equal(3, intake.Submission.Planned);

            // The first delivery blocks until the worker is stopped; the stop arrives while it is in flight.
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource();
            protocol.Before = async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            };

            var worker = new DeliveryWorker(
                ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
                CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "stopping-worker") { MaxWait = null };
            var drain = worker.DrainAsync(intake.Submission.SubmissionId, stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);

            var interrupted = protocol.Deliveries.Single();
            var state = await ledger.GetRecordAsync(runtime.Flow.Id, interrupted.Key);
            Assert.Equal(RecordStatus.Pending, state!.Status);
            Assert.Null(state.LeaseOwner);
            Assert.Equal(0, state.AttemptCount);
            Assert.Empty(await ledger.ListAttemptsAsync(runtime.Flow.Id, interrupted.Key, 10));

            // A fresh worker picks everything up at once; nothing waited for a lease to expire.
            protocol.Before = null;
            var resumed = new DeliveryWorker(
                ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
                CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "next-worker") { MaxWait = null };
            var summary = await resumed.DrainAsync(intake.Submission.SubmissionId);
            Assert.Equal(3, summary.Delivered);
            Assert.Single(await ledger.ListAttemptsAsync(runtime.Flow.Id, interrupted.Key, 10));
        }
    }

    /// <summary>
    /// An operation that throws anything at all still ends its activity: the ledger is the only account of what an
    /// operator asked for, and an action left reading as running would say the opposite of what happened.
    /// </summary>
    [Fact]
    public async Task An_operation_that_fails_outright_leaves_its_activity_failed_not_running()
    {
        var tables = await EstateAsync();
        var protocol = new FakeProtocol();
        // The protocol cannot be built: the failure a missing ${env:...} credential raises, outside the engine's own types.
        var protocols = new FakeProtocolFactory(protocol) { FailWith = () => new SqlFlowException("Environment variable 'PETRODB_URL' is not set.") };
        var (runtime, _, ledger) = await RuntimeAsync(tables, protocol: protocol, protocols: protocols);
        using (runtime)
        {
            runtime.Actor = "gui:tahir";
            var thrown = await Assert.ThrowsAsync<SqlFlowException>(() => runtime.VerifyAsync(100, null, reconcile: false));
            Assert.Equal("Environment variable 'PETRODB_URL' is not set.", thrown.Message);

            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = "verify" }));
            Assert.Equal("failed", activity.Outcome);
            Assert.NotNull(activity.CompletedUtc);
            Assert.Equal("Environment variable 'PETRODB_URL' is not set.", activity.Summary);
        }
    }

    [Fact]
    public async Task Verify_detects_drift_and_reconcile_queues_redelivery()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await RunAsync(runtime, protocol, ledger);
            protocol.VerifyWith = id => id.EndsWith(SampleEstate.Key(0).Value.ToString("N"), StringComparison.Ordinal)
                ? new VerifyResult(VerifyOutcome.Drifted, 999, "someone edited it")
                : new VerifyResult(VerifyOutcome.Match, null, null);
            var verifier = new Verifier(ledger, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<Verifier>());
            var summary = await verifier.RunAsync(100, null, reconcile: true);
            Assert.Equal(3, summary.Checked);
            Assert.Equal(1, summary.Drifted);
            Assert.Equal(2, summary.Matched);

            // Reconcile queued the drifted record for redelivery: the next plan of the scope sends it whole again.
            var plan = await runtime.PlanAsync(force: true);
            var drifted = plan.Entries.Single(e => e.Key == SampleEstate.Key(0));
            Assert.Equal(PlannedAction.UpdateBoth, drifted.Action);
            Assert.Equal(2, plan.Skips);
        }
    }

    [Fact]
    public async Task A_stale_row_is_never_sent_and_the_skip_is_recorded_against_the_record()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(3, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();

            // The row is re-landed with an older business version than the one delivered: a replay, never sent.
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[0], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc.AddHours(-1));

            var plan = await runtime.PlanAsync(force: true);
            var entry = plan.Entries.Single(e => e.Key == SampleEstate.Key(0));
            Assert.Equal(PlannedAction.Skip, entry.Action);
            Assert.Equal(SkipTier.Stale, entry.SkipTier);
            Assert.Contains("older than the version last modified", entry.Reason, StringComparison.Ordinal);

            var (summary, submissionId) = await RunAsync(runtime, protocol, ledger, force: true);
            Assert.Equal(0, summary.Processed);
            Assert.Empty(protocol.Deliveries);
            var submission = await ledger.GetSubmissionAsync(submissionId);
            Assert.Equal(1, submission!.SkippedStale);
            var stale = (await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10)).Single(a => a.Phase == AttemptPhases.Stale);
            Assert.Equal(AttemptOutcome.Skipped, stale.Outcome);
            Assert.Equal(submissionId, stale.SubmissionId);
        }
    }

    /// <summary>
    /// A second mapping over the well log rows, into another OSDU kind: the wellbore each log was run in, keyed like the log
    /// itself, so its records carry the same delivery keys as the well log flow's.
    /// </summary>
    private const string LogWellboreMapping = """
        documentType: mapping
        name: LogWellbore
        version: 1.0.0
        template:
          kind: osdu:wks:master-data--Wellbore:1.3.0
          version: 58d6bdbd9d066a06
        description: The wellbore each well log was run in, one record per log row.
        dataset:
          system: wells
          key: [dataset.source_project, dataset.log_id]
          label: "{dataset.wellbore_uwi} ({dataset.log_id})"
        parameters:
          dataPartition:
            required: true
            description: The OSDU data partition record ids are minted in.
          aclOwner: { required: true }
          aclViewer: { required: true }
          legalTag: { required: true }
        mappings:
          - target: osdu.acl.owners
            static: ["{param.aclOwner}"]
          - target: osdu.acl.viewers
            static: ["{param.aclViewer}"]
          - target: osdu.legal.legaltags
            static: ["{param.legalTag}"]
          - target: osdu.legal.otherRelevantDataCountries
            static: [NO]
          - target: osdu.data.FacilityName
            source: dataset.wellbore_uwi
        """;

    /// <summary>The sample well log flow turned into a second flow over the same rows, rendering <see cref="LogWellboreMapping"/>.</summary>
    private FlowDefinition WellboresFlow(FlowDefinition flow)
    {
        var mappings = Path.Combine(_root, "wellbore-mappings");
        Directory.CreateDirectory(mappings);
        File.WriteAllText(Path.Combine(mappings, "LogWellbore@1.0.0.yaml"), LogWellboreMapping);
        return flow with
        {
            Name = "wells-welllog-wellbores",
            Render = flow.Render with { Mapping = "LogWellbore@1.0.0", MappingsDirectory = mappings },
            Target = flow.Target with { Protocol = DeliveryProtocol.Storage },
        };
    }

    [Fact]
    public async Task Two_flows_deliver_the_same_rows_into_different_kinds_and_keep_their_own_ledgers()
    {
        var tables = await EstateAsync();
        var (logs, logProtocol, ledger) = await RuntimeAsync(tables);
        var (wellbores, wellboreProtocol, _) = await RuntimeAsync(tables, WellboresFlow);
        using (logs)
        using (wellbores)
        {
            var (logRun, logSubmission) = await RunAsync(logs, logProtocol, ledger);
            var (wellboreRun, wellboreSubmission) = await RunAsync(wellbores, wellboreProtocol, ledger);
            Assert.Equal((3, 3), (logRun.Delivered, wellboreRun.Delivered));
            Assert.All(logProtocol.Deliveries, w => Assert.StartsWith("dev:work-product-component--WellLog:", w.TargetId, StringComparison.Ordinal));
            Assert.All(wellboreProtocol.Deliveries, w => Assert.StartsWith("dev:master-data--Wellbore:", w.TargetId, StringComparison.Ordinal));
            Assert.All(wellboreProtocol.Deliveries, w => Assert.False(w.DeliverPayload));

            // One row, one key, two records: each names its own mapping, OSDU id, submission and history, and both name the
            // same ingestion file and row as their origin.
            for (var i = 0; i < 3; i++)
            {
                var key = SampleEstate.Key(i);
                var log = await ledger.GetRecordAsync(logs.Flow.Id, key);
                var wellbore = await ledger.GetRecordAsync(wellbores.Flow.Id, key);
                Assert.Equal(("WellLog", RecordStatus.Delivered, (Guid?)logSubmission), (log!.MappingName, log.Status, log.LastSubmissionId));
                Assert.Equal(("LogWellbore", RecordStatus.Delivered, (Guid?)wellboreSubmission), (wellbore!.MappingName, wellbore.Status, wellbore.LastSubmissionId));
                Assert.NotEqual(log.TargetId, wellbore.TargetId);
                Assert.Equal((SampleEstate.FileName, (long?)(i + 1)), (log.SourceFileName, log.SourceRowNumber));
                Assert.Equal((SampleEstate.FileName, (long?)(i + 1)), (wellbore.SourceFileName, wellbore.SourceRowNumber));
                Assert.Equal(logSubmission, Assert.Single(await ledger.ListAttemptsAsync(logs.Flow.Id, key, 10)).SubmissionId);
                Assert.Equal(wellboreSubmission, Assert.Single(await ledger.ListAttemptsAsync(wellbores.Flow.Id, key, 10)).SubmissionId);
                Assert.Equal(2, (await ledger.LookupAsync(key.Value.ToString(), 10)).Count);
            }

            Assert.Equal(logSubmission, Assert.Single(await ledger.ListSubmissionsAsync(logs.Flow.Id, 10)).SubmissionId);
            Assert.Equal(wellboreSubmission, Assert.Single(await ledger.ListSubmissionsAsync(wellbores.Flow.Id, 10)).SubmissionId);
            Assert.Equal(3, (await ledger.StatsAsync(logs.Flow.Id, Now)).Delivered);
            Assert.Equal(3, (await ledger.StatsAsync(wellbores.Flow.Id, Now)).Delivered);
            Assert.Equal(logSubmission, (await ledger.GetWatermarkAsync(logs.Flow.Id, Planner.ScopeKey(logs.Parameters)))!.SubmissionId);
            Assert.Equal(wellboreSubmission, (await ledger.GetWatermarkAsync(wellbores.Flow.Id, Planner.ScopeKey(wellbores.Parameters)))!.SubmissionId);

            // A column only the well log reads changes: that flow sends its document again, the other renders its own,
            // finds it unchanged and sends nothing. Each decides from its own record.
            logProtocol.Deliveries.Clear();
            wellboreProtocol.Deliveries.Clear();
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[0], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc.AddHours(2));
            Assert.Equal(1, (await RunAsync(logs, logProtocol, ledger, force: true)).Work.Delivered);
            Assert.Equal(0, (await RunAsync(wellbores, wellboreProtocol, ledger, force: true)).Work.Processed);
            Assert.Equal(SampleEstate.Key(0), Assert.Single(logProtocol.Deliveries).Key);
            Assert.Empty(wellboreProtocol.Deliveries);
            Assert.Equal(2, (await ledger.ListAttemptsAsync(logs.Flow.Id, SampleEstate.Key(0), 10)).Count);
            Assert.Single(await ledger.ListAttemptsAsync(wellbores.Flow.Id, SampleEstate.Key(0), 10));

            // Removing the wellbores takes the wellbore records out of OSDU and leaves every well log where it is.
            wellbores.Actor = "gui:tahir";
            var removed = await wellbores.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalScope.Record);
            Assert.Equal(1, removed.Removed);
            Assert.StartsWith("dev:master-data--Wellbore:", Assert.Single(wellboreProtocol.Deletes).TargetId, StringComparison.Ordinal);
            Assert.Empty(logProtocol.Deletes);
            Assert.Equal(RecordStatus.Deleted, (await ledger.GetRecordAsync(wellbores.Flow.Id, SampleEstate.Key(0)))!.Status);
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(logs.Flow.Id, SampleEstate.Key(0)))!.Status);
            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = wellbores.Flow.Id, DeliveryKey = SampleEstate.Key(0).Value }));
            Assert.Equal("delete", activity.Kind);
            Assert.Empty(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = logs.Flow.Id, DeliveryKey = SampleEstate.Key(0).Value }));
        }
    }

    [Fact]
    public async Task A_second_flow_writing_the_same_osdu_records_is_held_and_never_reaches_them()
    {
        var tables = await EstateAsync();
        var (logs, logProtocol, ledger) = await RuntimeAsync(tables);
        var (copy, copyProtocol, _) = await RuntimeAsync(tables, flow => flow with { Name = "wells-welllog-copy" });
        using (logs)
        using (copy)
        {
            Assert.Equal(3, (await RunAsync(logs, logProtocol, ledger)).Work.Delivered);

            // Same mapping, same partition: the copy would write the very records the first flow owns. It sends nothing,
            // and every record it planned is held with the owner named, so the reason is on the record's own page.
            var (copied, copySubmission) = await RunAsync(copy, copyProtocol, ledger);
            Assert.Equal(0, copied.Processed);
            Assert.Empty(copyProtocol.Deliveries);
            var submission = await ledger.GetSubmissionAsync(copySubmission);
            Assert.Equal((0L, 3L), (submission!.Delivered, submission.Held));
            for (var i = 0; i < 3; i++)
            {
                var held = await ledger.GetRecordAsync(copy.Flow.Id, SampleEstate.Key(i));
                Assert.Equal(RecordStatus.Held, held!.Status);
                Assert.True(held.Blocked);
                Assert.Null(held.TargetId);
                Assert.Contains("already claimed by flow 'wells-welllog-03-header-delivery'", held.LastError, StringComparison.Ordinal);
                Assert.Equal(SampleEstate.FileName, held.PendingSourceFileName);
                var attempt = Assert.Single(await ledger.ListAttemptsAsync(copy.Flow.Id, SampleEstate.Key(i), 10));
                Assert.Equal((AttemptOutcome.Held, "render"), (attempt.Outcome, attempt.Phase));
                Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(logs.Flow.Id, SampleEstate.Key(i)))!.Status);
            }

            // Removing the copy's records never reaches the first flow's OSDU records: the copy wrote nothing.
            copy.Actor = "gui:tahir";
            var removal = await copy.RemoveAsync(RemovalSelection.Of([.. Enumerable.Range(0, 3).Select(SampleEstate.Key)]), RemovalScope.Everything);
            Assert.Equal((3, 0), (removal.Skipped, removal.Removed));
            Assert.Empty(copyProtocol.Deletes);

            // Released, the copy plans the records again and meets the same owner: held again, still nothing sent.
            await copy.ReleaseAsync(null);
            _clock.Advance(TimeSpan.FromMinutes(1));
            copy.Selection = SourceSelection.Full();
            var (again, _) = await RunAsync(copy, copyProtocol, ledger, force: true);
            Assert.Equal(0, again.Processed);
            Assert.Empty(copyProtocol.Deliveries);
            Assert.All(
                await ledger.ListAsync(copy.Flow.Id, new RecordQuery()),
                r => Assert.Equal((RecordStatus.Held, true), (r.Status, r.Blocked)));
        }
    }

    [Fact]
    public async Task Each_attempt_names_the_correlation_id_its_requests_carried()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await RunAsync(runtime, protocol, ledger);

            var sent = protocol.Correlations[protocol.Deliveries.FindIndex(w => w.Key == SampleEstate.Key(0))];
            Assert.True(Guid.TryParse(sent, out _));
            var attempt = Assert.Single(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10));
            using var result = System.Text.Json.JsonDocument.Parse(attempt.ResultJson!);
            Assert.Equal(sent, result.RootElement.GetProperty("correlationId").GetString());
            Assert.Null(attempt.Error);
            Assert.Null(OsduCorrelation.Current);
        }
    }

    [Fact]
    public async Task A_flow_whose_mapping_keys_records_differently_than_its_source_is_refused()
    {
        var tables = await EstateAsync();
        var (runtime, _, _) = await RuntimeAsync(
            tables,
            flow => flow with { Source = flow.Source with { Record = flow.Source.Record with { Key = ["log_id"] } } });
        using (runtime)
        {
            var ex = await Assert.ThrowsAsync<FlowValidationException>(() => runtime.PlanAsync());
            Assert.Contains("source.record.key is [log_id]", ex.Message, StringComparison.Ordinal);
            Assert.Contains("the same columns in the same order", ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>The captured gamma ray unit, with the code a mapping matches it by.</summary>
    private static ReferenceType GammaRayUnit(string code) => new(
        "UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            new ReferenceItem("dev:reference-data--UnitOfMeasure:gAPI", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Code"] = ReferenceValue.Of(code),
                ["ID"] = ReferenceValue.Of(code),
                ["Name"] = ReferenceValue.Of("API gamma ray unit"),
            }),
        ]);

    [Fact]
    public async Task Records_a_cache_change_holds_back_are_counted_as_awaiting_approval_not_as_unchanged()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(3, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);

            // The unit the two gamma ray logs matched by moves, and the change waits for a decision, so their sets are
            // gated. The third log does not read it.
            var impact = await new CacheImpactAnalyzer(ledger, _clock, NullLogger.Instance)
                .AnalyzeAsync(Samples.SampleCacheScope, GammaRayUnit("gAPI"), GammaRayUnit("gAPI-2"), CacheChangeMode.Approve, "20260908T212727Z", "20260909T000000Z");
            Assert.NotEqual(0, impact.Changes);
            Assert.NotEmpty(await ledger.GatedCacheSetsAsync());

            _clock.Advance(TimeSpan.FromMinutes(10));
            foreach (var record in tables.Records)
            {
                record.UpdatedUtc = Now;
            }

            var plan = await runtime.PlanAsync(force: true);
            Assert.Equal(2, plan.AwaitingApproval);
            Assert.All(plan.Entries.Where(e => e.SkipTier == SkipTier.Approval), e => Assert.Equal(PlannedAction.Skip, e.Action));

            // The held-back records are not unchanged: their document moved with the cache, and an approval is what
            // decides whether it is sent. Only the third log is unchanged.
            Assert.Equal(1, plan.Skips);

            var (summary, submissionId) = await RunAsync(runtime, protocol, ledger, force: true);
            Assert.Equal(0, summary.Processed);
            var submission = await ledger.GetSubmissionAsync(submissionId);
            Assert.Equal(2, submission!.AwaitingApproval);
            Assert.Equal(1, submission.SkippedUnchanged);
            Assert.Equal(0, submission.Delivered);
        }
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
            // A temporary directory a reader still holds open is left for the operating system to reclaim.
        }

        GC.SuppressFinalize(this);
    }
}
