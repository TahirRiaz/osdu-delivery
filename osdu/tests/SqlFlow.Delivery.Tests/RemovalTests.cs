using System.Net;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The three removals against the storage service, verified as endpoint and method rather than as "a delete":
/// they are different calls with different promises and the protocol must not blur them.
/// </summary>
public class RemovalProtocolTests
{
    private const string RecordId = "opendes:work-product-component--WellLog:abc";

    private static (OsduHttpClient Client, HttpRuntime Runtime) Client(FakeHttpHandler handler)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 2, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost/osdu", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (client, runtime);
    }

    [Fact]
    public async Task Each_scope_calls_its_own_storage_endpoint()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, ":abc:delete", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, ":abc/versions", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, ":abc", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());

            var reversible = await protocol.DeleteAsync(RecordId, RemovalScope.Record);
            Assert.True(reversible.Deleted);
            Assert.False(reversible.AlreadyGone);
            Assert.Contains("reversible", reversible.Detail, StringComparison.Ordinal);
            Assert.Equal(HttpMethod.Post, handler.Calls[0].Method);
            Assert.EndsWith(":delete", handler.Calls[0].Uri.AbsolutePath, StringComparison.Ordinal);

            var history = await protocol.DeleteAsync(RecordId, RemovalScope.History);
            Assert.True(history.Deleted);
            Assert.Contains("latest version is still live", history.Detail, StringComparison.Ordinal);
            Assert.Equal(HttpMethod.Delete, handler.Calls[1].Method);
            Assert.EndsWith("/versions", handler.Calls[1].Uri.AbsolutePath, StringComparison.Ordinal);

            var everything = await protocol.DeleteAsync(RecordId, RemovalScope.Everything);
            Assert.True(everything.Deleted);
            Assert.Contains("every version", everything.Detail, StringComparison.Ordinal);
            Assert.Equal(HttpMethod.Delete, handler.Calls[2].Method);
            Assert.EndsWith(":abc", handler.Calls[2].Uri.AbsolutePath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_record_OSDU_no_longer_holds_is_already_gone_not_a_failure()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, ":delete", HttpStatusCode.NotFound, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var outcome = await new OsduRecordProtocol(client, new ProtocolOptions()).DeleteAsync(RecordId, RemovalScope.Record);
            Assert.False(outcome.Deleted);
            Assert.True(outcome.AlreadyGone);
        }
    }

    [Fact]
    public async Task The_reversible_scope_removes_many_records_in_one_bulk_request()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/records/delete", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var results = await protocol.DeleteBatchAsync(Removals("a", "b", "c"), RemovalScope.Record);

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.True(r.Succeeded));
            Assert.Single(handler.Calls);
            var sent = JsonNode.Parse(handler.Calls[0].Body!)!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            Assert.Equal(["dev:x:a", "dev:x:b", "dev:x:c"], sent);
        }
    }

    [Fact]
    public async Task A_partial_bulk_removal_falls_back_to_one_request_per_record()
    {
        // 207 means the service removed some of them and will not say which in a shape the protocol can trust, so
        // every record in the chunk is asked for again on its own and reports its own outcome.
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/records/delete", HttpStatusCode.MultiStatus, """{"notDeletedRecordIds":["dev:x:b"]}""")
            .On(HttpMethod.Post, ":b:delete", HttpStatusCode.NotFound, null)
            .On(HttpMethod.Post, ":delete", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var results = await protocol.DeleteBatchAsync(Removals("a", "b", "c"), RemovalScope.Record);

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.True(r.Succeeded));
            Assert.True(results.Single(r => r.Removal.TargetId == "dev:x:b").Outcome!.AlreadyGone);
            Assert.False(results.Single(r => r.Removal.TargetId == "dev:x:a").Outcome!.AlreadyGone);
            Assert.Equal(4, handler.Calls.Count);
        }
    }

    [Fact]
    public async Task A_bulk_removal_the_service_refused_outright_does_not_repeat_itself_once_per_record()
    {
        // 401 is not a verdict on any record in the chunk. Asking again one id at a time would produce the same
        // failure three times over; every record carries the one failure that actually happened.
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/records/delete", HttpStatusCode.Unauthorized, """{"code":401,"reason":"Unauthorized"}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var results = await protocol.DeleteBatchAsync(Removals("a", "b", "c"), RemovalScope.Record);

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.False(r.Succeeded));
            Assert.All(results, r => Assert.Contains("401", r.Failure!.Message, StringComparison.Ordinal));

            // Twice, not once per record: the client retries a 401 once under a freshly resolved token, and stops.
            Assert.Equal(2, handler.Calls.Count);
            Assert.All(handler.Calls, c => Assert.EndsWith("/records/delete", c.Uri.AbsolutePath, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_bulk_removal_the_service_rejected_as_malformed_is_asked_again_record_by_record()
    {
        // 400 is about the ids in the list, so the chunk is split to find which of them the service objects to.
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/records/delete", HttpStatusCode.BadRequest, """{"code":400,"reason":"Invalid id format"}""")
            .On(HttpMethod.Post, ":b:delete", HttpStatusCode.BadRequest, """{"code":400,"reason":"Invalid id format"}""")
            .On(HttpMethod.Post, ":delete", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var results = await protocol.DeleteBatchAsync(Removals("a", "b", "c"), RemovalScope.Record);

            Assert.Equal(3, results.Count);
            Assert.True(results.Single(r => r.Removal.TargetId == "dev:x:a").Succeeded);
            Assert.False(results.Single(r => r.Removal.TargetId == "dev:x:b").Succeeded);
            Assert.True(results.Single(r => r.Removal.TargetId == "dev:x:c").Succeeded);
            Assert.Equal(4, handler.Calls.Count);
        }
    }

    [Fact]
    public async Task The_purges_have_no_bulk_endpoint_and_go_one_at_a_time()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Delete, "/versions", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var results = await protocol.DeleteBatchAsync(Removals("a", "b"), RemovalScope.History);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.True(r.Succeeded));
            Assert.Equal(2, handler.Calls.Count);
            Assert.All(handler.Calls, c => Assert.EndsWith("/versions", c.Uri.AbsolutePath, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_endpoints_a_flow_would_call_follow_its_protocol()
    {
        var storage = RemovalEndpoints.Of(Samples.Targeting(new FlowTarget { Endpoint = "http://x", Protocol = DeliveryProtocol.Storage }), null);
        Assert.Equal("/api/storage/v2/records/{id}:delete", storage.Record);
        Assert.Equal("/api/storage/v2/records/{id}/versions", storage.History);
        Assert.Equal("/api/storage/v2/records/{id}", storage.Everything);

        // The wellbore DDMS owns the record but not its versions, so only the history scope leaves the DDMS. Its
        // paths carry no /api/<service>/ prefix, so a storage path under a DDMS endpoint would not resolve: with
        // nowhere to send it the scope reports itself unconfigured rather than naming a URL that would 404.
        var ddmsFlow = Samples.Targeting(new FlowTarget { Endpoint = "http://x", Protocol = DeliveryProtocol.Ddms });
        var ddms = RemovalEndpoints.Of(ddmsFlow, "osdu:wks:work-product-component--WellLog:1.4.0");
        Assert.Equal("/ddms/v3/welllogs/{id}", ddms.Record);
        Assert.Equal(RemovalEndpoints.HistoryNotConfigured, ddms.History);
        Assert.Equal("/ddms/v3/welllogs/{id}?purge=true", ddms.Everything);

        // The collection follows the kind: a trajectory's records are in wellboretrajectories.
        Assert.Equal("/ddms/v3/wellboretrajectories/{id}", RemovalEndpoints.Of(ddmsFlow, "osdu:wks:work-product-component--WellboreTrajectory:1.3.0").Record);

        // A record-only collection's DELETE is logical only, so its purge is the storage service's, which a DDMS
        // endpoint does not reach either.
        var wellbores = RemovalEndpoints.Of(ddmsFlow, "osdu:wks:master-data--Wellbore:1.3.0");
        Assert.Equal("/ddms/v3/wellbores/{id}", wellbores.Record);
        Assert.Equal(RemovalEndpoints.PurgeNotConfigured, wellbores.Everything);

        // Until the kind is known, the collection is not.
        Assert.Equal(RemovalEndpoints.CollectionNotKnown, RemovalEndpoints.Of(ddmsFlow, null).Record);

        // A kind no DDMS the flow reaches serves says so instead of naming a path.
        Assert.StartsWith("(not routable: ", RemovalEndpoints.Of(ddmsFlow, "osdu:wks:work-product-component--SeismicTraceData:1.0.0").Record, StringComparison.Ordinal);

        // A ddms flow that says where storage lives is taken at its word.
        var configured = RemovalEndpoints.Of(
            Samples.Targeting(new FlowTarget
            {
                Endpoint = "http://x",
                Protocol = DeliveryProtocol.Ddms,
                ProtocolOptions = new ProtocolOptions
                {
                    PurgeVersionsPath = "https://osdu.example.com/api/storage/v2/records/{id}/versions",
                    PurgePath = "https://osdu.example.com/api/storage/v2/records/{id}",
                },
            }),
            "osdu:wks:master-data--Wellbore:1.3.0");
        Assert.Equal("https://osdu.example.com/api/storage/v2/records/{id}/versions", configured.History);
        Assert.Equal("https://osdu.example.com/api/storage/v2/records/{id}", configured.Everything);
    }

    [Fact]
    public async Task A_well_log_history_purge_refuses_rather_than_deleting_somewhere_nobody_chose()
    {
        var handler = new FakeHttpHandler();
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var ex = await Assert.ThrowsAsync<RecordHeldException>(() => protocol.DeleteAsync(RecordId, RemovalScope.History));

            Assert.Contains("purgeVersionsPath", ex.Message, StringComparison.Ordinal);
            Assert.Empty(handler.Calls);
        }
    }

    [Fact]
    public async Task A_well_log_history_purge_goes_to_the_storage_service_the_flow_names()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Delete, "/versions", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var options = new ProtocolOptions { PurgeVersionsPath = "http://localhost/storage/api/storage/v2/records/{id}/versions" };
            var protocol = new OsduDdmsProtocol(client, options, Samples.Logger<OsduDdmsProtocol>());
            var outcome = await protocol.DeleteAsync(RecordId, RemovalScope.History);

            Assert.True(outcome.Deleted);

            // The absolute path is honoured as written, not joined under the flow's DDMS endpoint.
            var call = Assert.Single(handler.Calls);
            Assert.Equal("http://localhost/storage/api/storage/v2/records/" + RecordId + "/versions", call.Uri.AbsoluteUri);
        }
    }

    private static List<RecordRemoval> Removals(params string[] keys)
        => keys.Select(k => new RecordRemoval(DeliveryKey.Derive("test", [k]), "dev:x:" + k, null)).ToList();
}

/// <summary>What each scope does to the ledger, which is not the same question as what it does to OSDU.</summary>
public class RemovalLedgerTests : IDisposable
{
    private readonly SqliteOsdu _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("test-flow");

    private OsduLedger Ledger => _db.Ledger(_clock);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task Removing_the_record_marks_it_deleted_and_blocked_and_forgets_the_hashes()
    {
        var key = await DeliveredAsync("a", Guid.NewGuid());
        await Ledger.MarkRemovedAsync(_flow, [key], RemovalScope.Record, "gui:tahir", Now);

        var record = await Ledger.GetRecordAsync(_flow, key);
        Assert.Equal(RecordStatus.Deleted, record!.Status);
        Assert.True(record.Blocked);
        Assert.Null(record.MetadataHash);
        Assert.Null(record.TargetVersion);

        var attempt = Assert.Single(await Ledger.ListAttemptsAsync(_flow, key, 10), a => a.Phase == "delete");
        Assert.Equal(AttemptOutcome.Deleted, attempt.Outcome);
        Assert.Null(attempt.Error);
        Assert.Contains("gui:tahir", attempt.ResultJson!, StringComparison.Ordinal);
        Assert.Contains("reversible", attempt.ResultJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_removal_attempt_reads_like_any_other_attempt_and_names_its_correlation_id()
    {
        // A removed record's page went blank in the GUI: this attempt's result was the record's target state, not the
        // steps-shaped result every other attempt carries.
        var key = await DeliveredAsync("a", Guid.NewGuid());
        await Ledger.MarkRemovedAsync(_flow, [key], RemovalScope.Record, "gui:tahir", Now, "corr-1");

        var attempt = Assert.Single(await Ledger.ListAttemptsAsync(_flow, key, 10), a => a.Phase == "delete");
        using var result = System.Text.Json.JsonDocument.Parse(attempt.ResultJson!);
        Assert.Equal("corr-1", result.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Array, result.RootElement.GetProperty("steps").ValueKind);
        Assert.Contains("reversible", result.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Purging_everything_is_recorded_as_the_permanent_removal_it_is()
    {
        var key = await DeliveredAsync("a", Guid.NewGuid());
        await Ledger.MarkRemovedAsync(_flow, [key], RemovalScope.Everything, "gui:tahir", Now);

        Assert.Equal(RecordStatus.Deleted, (await Ledger.GetRecordAsync(_flow, key))!.Status);
        var attempt = Assert.Single(await Ledger.ListAttemptsAsync(_flow, key, 10), a => a.Phase == "delete");
        Assert.Null(attempt.Error);
        Assert.Contains("every version", attempt.ResultJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Purging_the_history_leaves_the_record_delivered_because_OSDU_still_holds_it()
    {
        var key = await DeliveredAsync("a", Guid.NewGuid());
        await Ledger.MarkRemovedAsync(_flow, [key], RemovalScope.History, "gui:tahir", Now);

        var record = await Ledger.GetRecordAsync(_flow, key);
        Assert.Equal(RecordStatus.Delivered, record!.Status);
        Assert.False(record.Blocked);
        Assert.Equal(42, record.TargetVersion);
        Assert.Equal("mh", record.MetadataHash);

        var attempt = Assert.Single(await Ledger.ListAttemptsAsync(_flow, key, 10), a => a.Phase == "purge-history");
        Assert.Equal(AttemptOutcome.HistoryPurged, attempt.Outcome);
        Assert.Equal(42, attempt.TargetVersion);
    }

    [Fact]
    public async Task A_listing_can_be_narrowed_to_the_records_one_run_touched()
    {
        var submission = Guid.NewGuid();
        var run = Guid.NewGuid();
        var touched = await DeliveredAsync("a", submission, run);
        var untouched = await DeliveredAsync("b", submission, Guid.NewGuid());

        var keys = await Ledger.ListKeysAsync(_flow, new RecordQuery { RunId = run }, 100);
        Assert.Equal([touched], keys);
        Assert.Equal(new BoundedCount(1, Exact: true), await Ledger.CountAsync(_flow, new RecordQuery { RunId = run }, 100));

        // The same filter with a status the run's record is not in matches nothing, so the two compose.
        Assert.Empty(await Ledger.ListKeysAsync(_flow, new RecordQuery { RunId = run, Status = RecordStatus.Failed }, 100));
        Assert.Equal(new BoundedCount(2, Exact: true), await Ledger.CountAsync(_flow, new RecordQuery(), 100));
        Assert.DoesNotContain(untouched, keys);
    }

    [Fact]
    public async Task Selected_keys_come_back_in_key_order_so_a_removal_never_reshuffles_its_own_pages()
    {
        var submission = Guid.NewGuid();
        var keys = new List<DeliveryKey>();
        foreach (var name in new[] { "a", "b", "c", "d" })
        {
            keys.Add(await DeliveredAsync(name, submission));
        }

        var listed = await Ledger.ListKeysAsync(_flow, new RecordQuery(), 100);
        Assert.Equal(keys.OrderBy(k => k.Value).ToList(), listed);
        Assert.Equal(2, (await Ledger.ListKeysAsync(_flow, new RecordQuery(), 2)).Count);
    }

    [Fact]
    public async Task A_record_with_no_OSDU_id_at_all_is_skipped_because_there_is_nothing_to_ask_for()
    {
        await Ledger.UpsertPendingAsync(_flow, [new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("test", ["no-id"]),
            FlowId = _flow,
            SourceKey = "no-id",
            MappingName = "Thing",
            PendingRenderContext = "{}",
            PendingSourceFingerprint = "fp",
            PendingMetadataHash = "mh",
            PendingMetadata = true,
        }]);

        var record = await Ledger.GetRecordAsync(_flow, DeliveryKey.Derive("test", ["no-id"]));
        Assert.Null(record!.TargetId);
        Assert.Equal(new BoundedCount(1, Exact: true), await Ledger.CountAsync(_flow, new RecordQuery { EverDelivered = false }, 100));
    }

    /// <summary>A record staged, claimed and delivered, so a removal has something real to act on.</summary>
    private async Task<DeliveryKey> DeliveredAsync(string sourceKey, Guid submission, Guid? runId = null)
    {
        var key = DeliveryKey.Derive("test", [sourceKey]);
        await Ledger.UpsertPendingAsync(_flow, [new RecordState
        {
            DeliveryKey = key,
            FlowId = _flow,
            SourceKey = sourceKey,
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
                RunId = runId,
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

/// <summary>The runtime's removal: the ledger settled per record, and a selection that is a filter resolved here.</summary>
public class RemovalRuntimeTests : IDisposable
{
    private readonly SqliteOsdu _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private Guid _submission;

    [Fact]
    public async Task Removing_a_selection_settles_every_record_and_reports_what_each_one_did()
    {
        var (runtime, protocol, ledger) = await DeliveredEstateAsync();
        using (runtime)
        {
            var keys = await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery(), 100);
            Assert.Equal(3, keys.Count);
            protocol.Gone.Add((await ledger.GetRecordAsync(runtime.Flow.Id, keys[0]))!.TargetId!);
            runtime.Actor = "gui:tahir";

            var summary = await runtime.RemoveAsync(RemovalSelection.Of(keys), RemovalScope.Everything);

            Assert.Equal(3, summary.Selected);
            Assert.Equal(2, summary.Removed);
            Assert.Equal(1, summary.AlreadyGone);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(0, summary.Skipped);
            Assert.All(protocol.Deletes, d => Assert.Equal(RemovalScope.Everything, d.Scope));
            foreach (var key in keys)
            {
                Assert.Equal(RecordStatus.Deleted, (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.Status);
            }

            var activity = (await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = "delete" })).Single();
            Assert.Equal("completed", activity.Outcome);
            Assert.Equal("gui:tahir", activity.Actor);
            Assert.Contains("3 record(s) selected", activity.Summary!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_filter_selection_is_resolved_when_the_removal_runs_not_when_it_was_asked_for()
    {
        var (runtime, protocol, ledger) = await DeliveredEstateAsync();
        using (runtime)
        {
            var summary = await runtime.RemoveAsync(
                RemovalSelection.Of(new RecordQuery { Status = RecordStatus.Delivered }), RemovalScope.Record);

            Assert.Equal(3, summary.Selected);
            Assert.Equal(3, summary.Removed);
            Assert.Equal(3, protocol.Deletes.Count);
            Assert.Empty(await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery { Status = RecordStatus.Delivered }, 100));
        }
    }

    [Fact]
    public async Task A_record_that_was_planned_but_never_delivered_comes_back_already_gone()
    {
        // The plan gives a record its OSDU id, so an undelivered record has an id OSDU has never seen. The removal
        // is still made (the ledger cannot know what the target holds) and OSDU answers 404, which is not a failure.
        var (runtime, protocol, ledger) = await DeliveredEstateAsync(deliver: false);
        using (runtime)
        {
            var keys = await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery(), 100);
            foreach (var key in keys)
            {
                protocol.Gone.Add((await ledger.GetRecordAsync(runtime.Flow.Id, key))!.TargetId!);
            }

            var summary = await runtime.RemoveAsync(RemovalSelection.Of(keys), RemovalScope.Record);

            Assert.Equal(keys.Count, summary.Selected);
            Assert.Equal(keys.Count, summary.AlreadyGone);
            Assert.Equal(0, summary.Removed);
            Assert.Equal(0, summary.Failed);
            Assert.All(summary.Records, r => Assert.Equal("already-gone", r.Outcome));
            Assert.Equal(keys.Count, (await ledger.CountAsync(runtime.Flow.Id, new RecordQuery { EverDelivered = false }, 100)).Count);
        }
    }

    /// <summary>The sample estate delivered through the fake protocol, with the runtime wired to that same fake.</summary>
    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> DeliveredEstateAsync(bool deliver = true)
    {
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, new FixedProtocolFactory(protocol), sources: tables);
        var flow = Samples.LocalFlow(_root);
        var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        Assert.False(intake.NothingToDo);
        _submission = intake.Submission.SubmissionId;
        if (deliver)
        {
            var worker = new Engine.Worker.DeliveryWorker(
                ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
                CompositeDeliveryListener.Empty, Samples.Logger<Engine.Worker.DeliveryWorker>(), "test-worker") { MaxWait = null };
            await worker.DrainAsync(_submission);
            protocol.Deletes.Clear();
        }

        await runtime.Intake.CompleteAsync(_submission, runtime.Flow.Id);
        return (runtime, protocol, ledger);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Hands the runtime the same fake protocol the test holds, so a removal can be observed.</summary>
internal sealed class FixedProtocolFactory : IProtocolFactory
{
    private readonly IDeliveryProtocol _protocol;

    public FixedProtocolFactory(IDeliveryProtocol protocol) => _protocol = protocol;

    public Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, CancellationToken ct = default)
        => Task.FromResult(_protocol);
}

/// <summary>
/// The contract the API and the node share: the scope on the wire, and the listing filter a removal carries when
/// the operator asked for "every record this matches". A drift here would aim a removal at the wrong records or at
/// the wrong depth, so it is pinned rather than assumed.
/// </summary>
public class RemovalContractTests
{
    [Theory]
    [InlineData("record", RemovalScope.Record)]
    [InlineData("history", RemovalScope.History)]
    [InlineData("everything", RemovalScope.Everything)]
    public void Scopes_round_trip_through_their_wire_names(string wire, RemovalScope scope)
    {
        Assert.Equal(scope, Engine.Operations.RemovalScopes.Parse(wire));
        Assert.Equal(wire, Engine.Operations.RemovalScopes.Wire(scope));
        Assert.True(Engine.Operations.RemovalScopes.TryParse(wire, out var parsed));
        Assert.Equal(scope, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("purge")]
    [InlineData("Record")]
    public void An_unknown_scope_is_refused_rather_than_defaulted(string? wire)
    {
        // Defaulting would pick a removal depth the operator never asked for, which is the one mistake this
        // vocabulary exists to prevent.
        Assert.False(Engine.Operations.RemovalScopes.TryParse(wire, out _));
        Assert.Throws<SqlFlowException>(() => Engine.Operations.RemovalScopes.Parse(wire));
    }

    [Fact]
    public void A_listing_filter_survives_the_trip_to_the_node()
    {
        var submission = Guid.NewGuid();
        var deliveredBy = Guid.NewGuid();
        var run = Guid.NewGuid();
        var query = new RecordQuery
        {
            Status = RecordStatus.Delivered,
            Search = "STAT",
            Mode = SearchMode.Contains,
            SubmissionId = submission,
            DeliveredBySubmissionId = deliveredBy,
            RunId = run,
            Drifted = true,
        };

        var back = Engine.Operations.RemovalFilter.FromJson(Engine.Operations.RemovalFilter.ToJson(query));

        Assert.Equal(RecordStatus.Delivered, back.Status);
        Assert.Equal("STAT", back.Search);
        Assert.Equal(SearchMode.Contains, back.Mode);
        Assert.Equal(submission, back.SubmissionId);
        Assert.Equal(deliveredBy, back.DeliveredBySubmissionId);
        Assert.Equal(run, back.RunId);
        Assert.True(back.Drifted);
    }

    [Fact]
    public void An_empty_filter_stays_empty_and_matches_everything_the_flow_holds()
    {
        var back = Engine.Operations.RemovalFilter.FromJson(Engine.Operations.RemovalFilter.ToJson(new RecordQuery()));
        Assert.Null(back.Status);
        Assert.Null(back.Search);
        Assert.Null(back.SubmissionId);
        Assert.Null(back.DeliveredBySubmissionId);
        Assert.Null(back.RunId);
        Assert.False(back.Drifted);
        Assert.Equal(SearchMode.Prefix, back.Mode);
    }

    [Fact]
    public void A_selection_must_name_records_and_cannot_exceed_the_ceiling()
    {
        Assert.Throws<DeliveryException>(() => RemovalSelection.Of(Array.Empty<DeliveryKey>()));
        var tooMany = Enumerable.Range(0, RemovalLimits.MaxSelection + 1)
            .Select(i => DeliveryKey.Derive("test", [i.ToString(System.Globalization.CultureInfo.InvariantCulture)]))
            .ToList();
        Assert.Throws<DeliveryException>(() => RemovalSelection.Of(tooMany));
    }
}
