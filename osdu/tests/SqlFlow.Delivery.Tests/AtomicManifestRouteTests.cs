using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The manifest route as a unit of work (docs/atomic-delivery-plan.md, The routes): the record a run is to write declared with
/// its run before the trigger, each run as an artifact of its own, and the record as written once the read-back finds it; a
/// manifest sent by reference declared before it is stored and reported removed once its run settles, or left for the sweep
/// when its removal fails; and the undo of every failure point: nothing taken back while any run the unit triggered may still
/// write (one going, one in a state not known as ended, one whose state cannot be asked, one a later try's run would have
/// hidden), then each run answered with how it ended, the record removed when the run created it or given back the version
/// storage held before, and after it the datasets its files were registered as and a manifest stored by reference, which wait
/// with a record that cannot be taken back yet; the record is left to newer work when it writes the record again.
/// </summary>
public sealed class AtomicManifestRouteTests
{
    private const string WellboreId = "dev:master-data--Wellbore:atomic-wb";
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";
    private const string LogId = "dev:work-product-component--WellLog:atomic-m";
    private const string LogKind = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string Ingest = "Osdu_ingest";
    private const string ByReference = ProtocolOptions.DefaultByReferenceWorkflowName;
    private const string Instructions = "/api/dataset/v1/storageInstructions";
    private const string Register = "/api/dataset/v1/registerDataset";
    private const string StorageRecords = "/api/storage/v2/records";
    private const string Minted = "dev:dataset--File.Generic:minted-";

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private sealed class Rig : IDisposable
    {
        public Rig(ManifestReference reference = ManifestReference.Never)
        {
            Platform = new FakeOsduPlatform();
            FileFamilyFaults.IndexLive(Platform);
            Faults = new FileFamilyFaults(Platform);
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                Secrets, TimeProvider.System, Faults, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            Protocol = new OsduManifestProtocol(Client, new ProtocolOptions
            {
                ManifestByReference = reference,
                BatchSize = 10,
                WorkflowPollSeconds = 1,
                DatasetIndexWaitSeconds = 0,
                UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-blob-type"] = "BlockBlob" },
            }, Samples.Logger<OsduManifestProtocol>());
        }

        public FakeOsduPlatform Platform { get; }

        public FileFamilyFaults Faults { get; }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public OsduManifestProtocol Protocol { get; }

        public FileFamilyLedger Ledger(DateTime? startedUtc = null) => new(() => Platform.Calls.Count, startedUtc);

        public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work) => (await Protocol.DeliverBatchAsync([work]))[0];

        public long VersionOf(string id) => Platform.Records[id]["version"]!.GetValue<long>();

        public void Dispose()
        {
            Runtime.Dispose();
            Faults.Dispose();
        }
    }

    private static JsonObject Document(string id, string name)
        => FakeOsduPlatform.Record(id, id == LogId ? LogKind : WellboreKind, new JsonObject { ["Name"] = name });

    private static DeliveryWork Work(JsonObject document, IPayloadSource? files = null, long? existing = null, IReadOnlyDictionary<string, string>? state = null)
        => new()
        {
            Key = DeliveryKey.Derive("atomic-manifest-route", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = true,
            DeliverPayload = files is not null,
            Payload = files,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

    private static MemoryFiles OneFile() => new(("run.las", "~A 1"));

    private static string Trigger(string workflow) => "/api/workflow/v1/workflow/" + workflow + "/workflowRun";

    private static bool AnyStatus(HttpRequestMessage request)
        => request.Method == HttpMethod.Get && FileFamilyFaults.PathOf(request).Contains("/workflowRun/", StringComparison.Ordinal);

    private static string Delete(string id) => StorageRecords + "/" + id + ":delete";

    private static string RunSlot(string runId) => "run:" + runId;

    private static UndoResult Record(IReadOnlyList<UndoResult> results) => FileFamilyLedger.For(results, TargetArtifact.RecordSlot);

    private static UndoResult RunOf(IReadOnlyList<UndoResult> results) => Assert.Single(results, r => r.Item.Artifact.Role == ArtifactRoles.Run);

    private static int IndexOf(FakeOsduPlatform platform, HttpMethod method, string path)
        => platform.Calls.FindIndex(c => c.Method == method && Uri.UnescapeDataString(c.Uri.AbsolutePath) == path);

    private static int Count(FakeOsduPlatform platform, HttpMethod method, string path, int from = 0)
        => platform.Calls.Skip(from).Count(c => c.Method == method && Uri.UnescapeDataString(c.Uri.AbsolutePath) == path);

    private static string NameOf(FakeOsduPlatform platform, string id) => platform.Records[id]["data"]!["Name"]!.GetValue<string>();

    private static string Text(long version) => version.ToString(CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task The_record_a_run_is_to_write_is_declared_with_its_run_before_the_trigger_and_as_written_once_read_back(bool update, bool files)
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        var id = files ? LogId : WellboreId;
        long? prior = update ? rig.Platform.Put(Document(id, "before")) : null;
        var ledger = rig.Ledger();
        var outcome = await rig.DeliverAsync(ledger.Track(Work(Document(id, "after"), files ? OneFile() : null, prior)));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        var triggered = Assert.Single(rig.Platform.Runs);
        string[] steps = files
            ? ["upload-0", "register-0", "register-0", "manifest-intent", OsduManifestProtocol.ManifestStep, OsduManifestProtocol.RecordsStep]
            : ["manifest-intent", OsduManifestProtocol.ManifestStep, OsduManifestProtocol.RecordsStep];
        Assert.Equal(steps, ledger.Reports.Select(r => r.Step));

        // The run is the unit's before the trigger goes: a trigger or a poll whose answer is lost is still waited out.
        var trigger = IndexOf(rig.Platform, HttpMethod.Post, Trigger(Ingest));
        var intent = Assert.Single(ledger.Reports, r => r.Step == "manifest-intent");
        Assert.True(intent.CallsBefore <= trigger, "the record was declared after its run was triggered");
        Assert.Equal(triggered.RunId, intent.Report.Returned["runId"]);
        Assert.Equal(Ingest, intent.Report.Returned[OsduManifestProtocol.WorkflowValue]);
        Assert.Equal(2, intent.Artifacts.Count);
        var declared = Assert.Single(intent.Artifacts, a => a.Slot == TargetArtifact.RecordSlot);
        Assert.Equal(update ? ArtifactRoles.Version : ArtifactRoles.Record, declared.Role);
        Assert.Equal(id, declared.TargetId);
        Assert.Equal(prior, declared.PriorVersion);
        Assert.Equal(Ingest + "|" + triggered.RunId, declared.Locator);
        Assert.Null(declared.Version);
        Assert.Equal(ArtifactStatus.Intent, declared.Status);

        // The run is an artifact of its own, under its own slot, so a run a later try triggers never hides it.
        var run = Assert.Single(intent.Artifacts, a => a.Slot == RunSlot(triggered.RunId));
        Assert.Equal(ArtifactRoles.Run, run.Role);
        Assert.Equal(triggered.RunId, run.TargetId);
        Assert.Equal(Ingest, run.Locator);
        Assert.Equal(ArtifactStatus.Pending, run.Status);

        var written = Assert.Single(ledger.Reports, r => r.Step == OsduManifestProtocol.RecordsStep);
        Assert.True(written.CallsBefore > trigger);
        var artifact = Assert.Single(written.Artifacts);
        Assert.Equal(ArtifactStatus.Pending, artifact.Status);
        Assert.Equal(declared.Role, artifact.Role);
        Assert.Equal(rig.VersionOf(id), artifact.Version);
        Assert.Equal(prior, artifact.PriorVersion);
        Assert.Equal(declared.Locator, artifact.Locator);

        var row = ledger.Slot(TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Pending, row.Status);
        Assert.Equal(rig.VersionOf(id), row.Version);
        Assert.Equal(prior, row.PriorVersion);
        Assert.Equal(ArtifactStatus.Pending, ledger.Slot(RunSlot(triggered.RunId)).Status);
        Assert.Equal(files ? 3 : 2, ledger.Rows.Count);
        if (files)
        {
            var dataset = Assert.Single(ledger.Rows, r => r.Role == ArtifactRoles.Dataset);
            Assert.Equal(Minted + "2", dataset.TargetId);
            var registration = IndexOf(rig.Platform, HttpMethod.Post, "/api/file/v2/files/metadata");
            Assert.True(registration < trigger);
        }

        // At the unit's commit the record and its run stand only for its progress, and go; a dataset it minted stays, live.
        ledger.Commit();
        Assert.Equal(files ? 1 : 0, ledger.Rows.Count);
        Assert.All(ledger.Rows, r => Assert.Equal(ArtifactStatus.Live, r.Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_record_a_failed_run_still_wrote_is_removed_or_given_back_the_version_storage_held_before(bool update)
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Terminal = "failed", Effect = ComposedRouteTests.Ingest });
        long? prior = update ? rig.Platform.Put(Document(WellboreId, "before")) : null;
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Document(WellboreId, "after"), existing: prior));
        var outcome = await rig.DeliverAsync(work);
        Assert.False(outcome.Succeeded);
        Assert.Contains("failed; the next try triggers a new run", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.Equal("after", NameOf(rig.Platform, WellboreId));
        var row = ledger.Slot(TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Intent, row.Status);
        Assert.Null(row.Version);

        var mark = rig.Platform.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);

        // The run has ended, so it is answered with how it ended; what it wrote is taken back.
        var run = RunOf(results);
        Assert.Equal(ArtifactStatus.Kept, run.Outcome);
        Assert.Contains("ended FAILED", run.Note, StringComparison.Ordinal);
        var result = Record(results);
        if (update)
        {
            Assert.Equal(ArtifactStatus.Restored, result.Outcome);
            Assert.Contains($"version {Text(prior!.Value)} written back", result.Note, StringComparison.Ordinal);
            Assert.Equal("before", NameOf(rig.Platform, WellboreId));
            Assert.DoesNotContain(WellboreId, rig.Platform.Removed);
        }
        else
        {
            Assert.Equal(ArtifactStatus.Removed, result.Outcome);
            Assert.Contains(WellboreId, rig.Platform.Removed);
            Assert.Equal(1, Count(rig.Platform, HttpMethod.Post, Delete(WellboreId), mark));

            // A second undo of the same record finds nothing to take back.
            var again = Record(await rig.Protocol.UndoAsync([ledger.Undo(work)]));
            Assert.Equal(ArtifactStatus.Gone, again.Outcome);
            Assert.Equal(1, Count(rig.Platform, HttpMethod.Post, Delete(WellboreId), mark));
        }
    }

    [Fact]
    public async Task A_record_the_run_wrote_whose_read_back_answer_was_lost_stays_an_intent_and_the_undo_removes_it()
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });

        // The first read is the version storage held before the run; the second is the read-back after it.
        rig.Faults.Lose(FileFamilyFaults.Nth(HttpMethod.Post, "/api/storage/v2/query/records", 2), FileFamilyLoss.Transport);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Document(WellboreId, "after")));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        Assert.Contains(WellboreId, rig.Platform.Records.Keys);
        Assert.DoesNotContain(ledger.Reports, r => r.Step == OsduManifestProtocol.RecordsStep);
        var row = ledger.Slot(TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Intent, row.Status);
        Assert.Null(row.Version);

        var results = await rig.Protocol.UndoAsync([ledger.Undo(work)]);
        Assert.Equal(ArtifactStatus.Removed, Record(results).Outcome);
        Assert.Equal(ArtifactStatus.Kept, RunOf(results).Outcome);
        Assert.Contains(WellboreId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_record_held_before_anything_is_sent_reports_nothing_and_leaves_nothing_to_undo()
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        var ledger = rig.Ledger();
        var empty = await rig.DeliverAsync(ledger.Track(Work(Document(LogId, "after"), new MemoryFiles(("empty.las", "")))));
        Assert.IsType<RecordHeldException>(empty.Failure);

        var unplaced = await rig.DeliverAsync(ledger.Track(Work(FakeOsduPlatform.Record("dev:thing--Unplaced:x", "osdu:wks:thing--Unplaced:1.0.0"))));
        Assert.Contains("names no manifest section", unplaced.Failure!.Message, StringComparison.Ordinal);

        Assert.Empty(rig.Platform.Calls);
        Assert.Empty(ledger.Reports);
        Assert.Empty(ledger.Rows);
    }

    [Fact]
    public async Task Nothing_is_taken_back_while_the_run_that_may_still_write_the_record_is_going()
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Pending = "running", Effect = ComposedRouteTests.Ingest });

        // The run is triggered and the poll's answer never comes, so the try fails with the run going.
        rig.Faults.Refuse(AnyStatus, HttpStatusCode.ServiceUnavailable);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Document(LogId, "after"), OneFile()));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var runId = Assert.Single(rig.Platform.Runs).RunId;
        var undo = ledger.Undo(work);
        Assert.Equal(3, undo.Items.Count);

        var waiting = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], waiting);
        Assert.All(waiting, r =>
        {
            Assert.Equal(ArtifactStatus.Failed, r.Outcome);
            Assert.Contains($"workflow run {runId} of {Ingest} is still RUNNING", r.Note, StringComparison.Ordinal);
        });
        Assert.Empty(rig.Platform.Removed);

        // Once the run has ended, the record it wrote and the datasets are taken back, and the run is answered with how it ended.
        var ended = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], ended);
        Assert.All(ended.Where(r => r.Item.Artifact.Role != ArtifactRoles.Run), r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        var run = RunOf(ended);
        Assert.Equal(ArtifactStatus.Kept, run.Outcome);
        Assert.Contains($"workflow run {runId} of {Ingest} ended FINISHED", run.Note, StringComparison.Ordinal);
        Assert.Contains(LogId, rig.Platform.Removed);
        Assert.Contains(Minted + "2", rig.Platform.Removed);
    }

    [Fact]
    public async Task A_run_in_a_state_not_known_as_ended_holds_the_undo_as_one_still_going_does()
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest, Terminal = "failed" });
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Document(WellboreId, "after")));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var runId = Assert.Single(rig.Platform.Runs).RunId;

        // Airflow scheduled a retry of the run: a state that is neither going nor ended as far as the route knows.
        rig.Faults.Answer(
            FileFamilyFaults.On(HttpMethod.Get, Trigger(Ingest) + "/" + runId),
            (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, "{\"runId\":\"" + runId + "\",\"status\":\"up_for_retry\"}")),
            times: 1);
        var undo = ledger.Undo(work);
        var waiting = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], waiting);
        Assert.All(waiting, r =>
        {
            Assert.Equal(ArtifactStatus.Failed, r.Outcome);
            Assert.Contains("UP_FOR_RETRY, which is not a state the route knows as ended", r.Note, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(WellboreId, rig.Platform.Removed);

        var ended = await rig.Protocol.UndoAsync([undo]);
        Assert.Equal(ArtifactStatus.Removed, Record(ended).Outcome);
    }

    [Fact]
    public async Task A_run_whose_status_cannot_be_asked_holds_every_item_failed_until_it_can()
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Terminal = "failed" });
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Document(WellboreId, "after")));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        rig.Faults.Refuse(AnyStatus, HttpStatusCode.ServiceUnavailable);
        var undo = ledger.Undo(work);
        var unknown = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], unknown);
        Assert.All(unknown, r =>
        {
            Assert.Equal(ArtifactStatus.Failed, r.Outcome);
            Assert.Contains("has ended could not be asked", r.Note, StringComparison.Ordinal);
        });

        // The run failed without writing the record: there is nothing to take back.
        var answered = await rig.Protocol.UndoAsync([undo]);
        Assert.Equal(ArtifactStatus.Gone, Record(answered).Outcome);
        Assert.Contains("ended FAILED", RunOf(answered).Note, StringComparison.Ordinal);
        Assert.Empty(rig.Platform.Removed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_trigger_that_never_landed_leaves_an_intent_the_undo_answers_gone_without_a_write(bool update)
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        long? prior = update ? rig.Platform.Put(Document(WellboreId, "before")) : null;
        rig.Faults.Refuse(HttpMethod.Post, Trigger(Ingest), HttpStatusCode.InternalServerError);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Document(WellboreId, "after"), existing: prior));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        Assert.Empty(rig.Platform.Runs);
        Assert.Equal(ArtifactStatus.Intent, ledger.Slot(TargetArtifact.RecordSlot).Status);

        var mark = rig.Platform.Calls.Count;
        var results = await rig.Protocol.UndoAsync([ledger.Undo(work)]);
        var run = RunOf(results);
        Assert.Equal(ArtifactStatus.Kept, run.Outcome);
        Assert.Contains("its trigger did not land", run.Note, StringComparison.Ordinal);
        var result = Record(results);
        Assert.Equal(ArtifactStatus.Gone, result.Outcome);
        Assert.Contains(update ? $"OSDU's latest version is still {Text(prior!.Value)}" : "OSDU no longer holds the record", result.Note, StringComparison.Ordinal);
        Assert.Equal(0, Count(rig.Platform, HttpMethod.Put, StorageRecords, mark));
        Assert.Equal(0, Count(rig.Platform, HttpMethod.Post, Delete(WellboreId), mark));
        if (update)
        {
            Assert.Equal("before", NameOf(rig.Platform, WellboreId));
        }
    }

    [Fact]
    public async Task A_manifest_by_reference_is_declared_before_it_is_stored_and_reported_removed_once_its_run_settles()
    {
        using var rig = new Rig(ManifestReference.Always);
        rig.Platform.Register(ByReference, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Document(WellboreId, "after")));
        var outcome = await rig.DeliverAsync(work);
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(["manifest-intent", OsduManifestProtocol.ManifestStep, "manifest-removed", OsduManifestProtocol.RecordsStep], ledger.Reports.Select(r => r.Step));

        var run = Assert.Single(rig.Platform.Runs);
        var manifestId = "dev:dataset--File.Generic:osdu-delivery-manifest-" + run.RunId;
        var slot = "manifest:" + run.RunId;
        var intent = Assert.Single(ledger.Reports, r => r.Step == "manifest-intent");
        Assert.True(intent.CallsBefore <= IndexOf(rig.Platform, HttpMethod.Post, Instructions), "the manifest was declared after it began to be stored");
        Assert.Equal(3, intent.Artifacts.Count);
        Assert.Equal(ByReference + "|" + run.RunId, Assert.Single(intent.Artifacts, a => a.Slot == TargetArtifact.RecordSlot).Locator);
        Assert.Equal(ByReference, Assert.Single(intent.Artifacts, a => a.Slot == RunSlot(run.RunId)).Locator);
        var manifest = Assert.Single(intent.Artifacts, a => a.Slot == slot);
        Assert.Equal(ArtifactRoles.Dataset, manifest.Role);
        Assert.Equal(manifestId, manifest.TargetId);
        // Named by its id alone: the undo removes it by that id, and searches by a landing path only for a registered file.
        Assert.Null(manifest.Locator);
        Assert.Equal(ArtifactStatus.Intent, manifest.Status);

        var removed = Assert.Single(Assert.Single(ledger.Reports, r => r.Step == "manifest-removed").Artifacts);
        Assert.Equal(slot, removed.Slot);
        Assert.Equal(ArtifactStatus.Removed, removed.Status);
        Assert.Equal(manifestId, removed.TargetId);
        Assert.Contains("removed once its run settled", removed.Note, StringComparison.Ordinal);
        Assert.Contains(manifestId, rig.Platform.Removed);

        // The manifest the route removed itself is no longer the undo's.
        Assert.Equal(ArtifactStatus.Removed, ledger.Slot(slot).Status);
        Assert.Equal(
            new[] { RunSlot(run.RunId), TargetArtifact.RecordSlot }.Order(StringComparer.Ordinal),
            ledger.Undo(work).Items.Select(i => i.Artifact.Slot).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_manifest_whose_removal_failed_stays_an_intent_and_is_removed_by_the_sweep_after_the_unit_commits()
    {
        using var rig = new Rig(ManifestReference.Always);
        rig.Platform.Register(ByReference, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        rig.Faults.Refuse(
            r => r.Method == HttpMethod.Post && FileFamilyFaults.PathOf(r).StartsWith("/api/dataset/v1/metadataRecord/", StringComparison.Ordinal),
            HttpStatusCode.InternalServerError);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Document(WellboreId, "after")));
        var outcome = await rig.DeliverAsync(work);

        // The record is written whatever became of its transport.
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var run = Assert.Single(rig.Platform.Runs);
        var manifestId = "dev:dataset--File.Generic:osdu-delivery-manifest-" + run.RunId;
        Assert.DoesNotContain(manifestId, rig.Platform.Removed);
        Assert.DoesNotContain(ledger.Reports, r => r.Step == "manifest-removed");

        ledger.Commit();
        Assert.Equal(ArtifactStatus.Due, ledger.Slot("manifest:" + run.RunId).Status);
        var sweep = ledger.Undo(work);
        var item = Assert.Single(sweep.Items);
        Assert.Equal(manifestId, item.Artifact.TargetId);
        var result = Assert.Single(await rig.Protocol.UndoAsync([sweep]));
        Assert.Equal(ArtifactStatus.Removed, result.Outcome);
        Assert.Contains(manifestId, rig.Platform.Removed);
        Assert.DoesNotContain(WellboreId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_manifest_whose_store_fails_triggers_nothing_and_leaves_intents_the_undo_answers_gone()
    {
        using var rig = new Rig(ManifestReference.Always);
        rig.Platform.Register(ByReference, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        rig.Faults.Refuse(HttpMethod.Put, Register, HttpStatusCode.InternalServerError);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Document(WellboreId, "after")));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        Assert.Empty(rig.Platform.Runs);
        Assert.DoesNotContain(rig.Platform.Calls, c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath == Trigger(ByReference));

        var undo = ledger.Undo(work);
        Assert.Equal(3, undo.Items.Count);
        var results = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        Assert.Contains("its trigger did not land", RunOf(results).Note, StringComparison.Ordinal);
        Assert.All(results.Where(r => r.Item.Artifact.Role != ArtifactRoles.Run), r => Assert.Equal(ArtifactStatus.Gone, r.Outcome));
        Assert.Empty(rig.Platform.Removed);
    }

    [Fact]
    public async Task Datasets_registered_for_a_record_whose_trigger_was_refused_are_removed_and_the_record_never_written_is_gone()
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        rig.Faults.Refuse(HttpMethod.Post, Trigger(Ingest), HttpStatusCode.BadRequest);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Document(LogId, "after"), OneFile()));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(results, r => r.Item.Artifact.Role == ArtifactRoles.Dataset).Outcome);
        Assert.Equal(ArtifactStatus.Gone, FileFamilyLedger.For(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Contains(Minted + "2", rig.Platform.Removed);
        Assert.DoesNotContain(LogId, rig.Platform.Records.Keys);
    }

    [Fact]
    public async Task Newer_work_keeps_the_record_and_the_datasets_of_the_abandoned_unit_are_still_removed()
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Terminal = "failed", Effect = ComposedRouteTests.Ingest });
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Document(LogId, "after"), OneFile()));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        var undo = ledger.Undo(work, UndoReason.Abandoned, keepRecord: true);
        var results = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        var record = FileFamilyLedger.For(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Superseded, record.Outcome);
        Assert.Contains("newer work writes it again", record.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(results, r => r.Item.Artifact.Role == ArtifactRoles.Dataset).Outcome);
        Assert.DoesNotContain(LogId, rig.Platform.Removed);
        Assert.Contains(Minted + "2", rig.Platform.Removed);
    }

    [Fact]
    public async Task A_record_OSDU_created_before_the_unit_whose_run_wrote_it_without_answering_is_kept_unwritten()
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Terminal = "failed", Effect = ComposedRouteTests.Ingest });

        // Storage holds the record soft-deleted, so the run is declared as creating it, and the failed run writes it again.
        rig.Platform.Put(Document(WellboreId, "before"));
        rig.Platform.Removed.Add(WellboreId);
        var ledger = rig.Ledger(DateTime.UtcNow);
        var work = ledger.Track(Work(Document(WellboreId, "after")));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        Assert.Equal(ArtifactRoles.Record, ledger.Slot(TargetArtifact.RecordSlot).Role);
        rig.Platform.Records[WellboreId]["createTime"] = ledger.Unit.StartedUtc.AddHours(-2).ToString("O", CultureInfo.InvariantCulture);

        var mark = rig.Platform.Calls.Count;
        var result = Record(await rig.Protocol.UndoAsync([ledger.Undo(work)]));
        Assert.Equal(ArtifactStatus.Kept, result.Outcome);
        Assert.Contains("not this delivery's to remove", result.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(WellboreId, rig.Platform.Removed);
        Assert.Equal(0, Count(rig.Platform, HttpMethod.Put, StorageRecords, mark));
    }

    [Fact]
    public async Task A_resumed_run_that_wrote_the_record_settles_it_without_a_trigger_and_reports_it_written()
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest);
        const string runId = "4f1c2b0a-9e8d-4c7b-a6f5-0e1d2c3b4a59";
        rig.Platform.Runs.Add(new FakeOsduPlatform.Run(Ingest, runId, new JsonObject()));
        var version = rig.Platform.Put(Document(WellboreId, "after"));
        var completed = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [OsduManifestProtocol.ManifestStep] = new Dictionary<string, string>(StringComparer.Ordinal) { ["runId"] = runId, [OsduManifestProtocol.WorkflowValue] = Ingest },
        };
        var ledger = rig.Ledger();
        var outcome = await rig.DeliverAsync(ledger.Track(Work(Document(WellboreId, "after")) with { CompletedSteps = completed }));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(version, outcome.TargetVersion);
        Assert.Empty(rig.Platform.Triggers(Ingest));
        Assert.Equal([OsduManifestProtocol.RecordsStep], ledger.Reports.Select(r => r.Step));
        var artifact = Assert.Single(Assert.Single(ledger.Reports).Artifacts);
        Assert.Equal(ArtifactRoles.Record, artifact.Role);
        Assert.Equal(version, artifact.Version);
        Assert.Equal(Ingest + "|" + runId, artifact.Locator);
    }

    [Fact]
    public async Task An_undo_waits_for_a_run_whose_trigger_answer_was_lost_even_after_a_later_try_triggered_another()
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        rig.Faults.LoseAnswer(HttpMethod.Post, Trigger(Ingest));
        var ledger = rig.Ledger();
        Assert.False((await rig.DeliverAsync(ledger.Track(Work(Document(WellboreId, "after"))))).Succeeded);
        var lost = Assert.Single(rig.Platform.Runs).RunId;
        Assert.Equal(Ingest + "|" + lost, ledger.Slot(TargetArtifact.RecordSlot).Locator);

        // That run has not ended. The next try triggers a run of its own, which fails.
        rig.Faults.Answer(
            FileFamilyFaults.On(HttpMethod.Get, Trigger(Ingest) + "/" + lost),
            (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.OK, "{\"runId\":\"" + lost + "\",\"status\":\"running\"}")));
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Terminal = "failed" });
        var work = ledger.Resume(Work(Document(WellboreId, "after")));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        Assert.Equal(2, rig.Platform.Runs.Count);

        // Each run is an artifact of its own, so the first is still named though the record's slot now names the second.
        Assert.Equal(Ingest + "|" + rig.Platform.Runs[1].RunId, ledger.Slot(TargetArtifact.RecordSlot).Locator);
        Assert.Equal(lost, ledger.Slot(RunSlot(lost)).TargetId);
        Assert.Equal(rig.Platform.Runs[1].RunId, ledger.Slot(RunSlot(rig.Platform.Runs[1].RunId)).TargetId);

        // The first run may still write the record after the undo took it back, so nothing is taken back until it ends.
        var undo = ledger.Undo(work, UndoReason.Failed);
        var results = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        Assert.All(results, r =>
        {
            Assert.Equal(ArtifactStatus.Failed, r.Outcome);
            Assert.Contains($"workflow run {lost} of {Ingest} is still RUNNING", r.Note, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(WellboreId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_payload_change_names_the_earlier_datasets_superseded_once_the_run_wrote_the_record()
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        const string old = "dev:dataset--File.Generic:old";
        var earlier = Document(LogId, "before");
        earlier["data"]!["Datasets"] = new JsonArray(old + ":");
        var version = rig.Platform.Put(earlier);
        var state = new Dictionary<string, string>(StringComparer.Ordinal) { [FileUploads.DatasetIdsValue] = old };

        var outcome = await rig.DeliverAsync(rig.Ledger().Track(Work(Document(LogId, "after"), OneFile(), version, state)));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal([old], outcome.Superseded);
        Assert.Equal(Minted + "2", outcome.Returned[FileUploads.DatasetIdsValue]);

        // A run that does not write the record supersedes nothing.
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Terminal = "failed" });
        var failed = await rig.DeliverAsync(rig.Ledger().Track(Work(Document(LogId, "again"), OneFile(), outcome.TargetVersion, outcome.Returned)));
        Assert.False(failed.Succeeded);
        Assert.Empty(failed.Superseded);
    }

    [Fact]
    public async Task A_record_storage_will_not_remove_yet_keeps_its_datasets_waiting_and_both_go_on_the_next_undo()
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Terminal = "failed", Effect = ComposedRouteTests.Ingest });
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Document(LogId, "after"), OneFile()));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        rig.Faults.Refuse(HttpMethod.Post, Delete(LogId), HttpStatusCode.InternalServerError);
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        var record = Record(results);
        Assert.Equal(ArtifactStatus.Failed, record.Outcome);
        Assert.Contains("500", record.Note, StringComparison.Ordinal);

        // The record still names its dataset, so the dataset waits with it rather than leaving it naming a removed one.
        var dataset = Assert.Single(results, r => r.Item.Artifact.Role == ArtifactRoles.Dataset);
        Assert.Equal(ArtifactStatus.Failed, dataset.Outcome);
        Assert.StartsWith("the record could not be taken back yet", dataset.Note, StringComparison.Ordinal);
        Assert.Empty(rig.Platform.Removed);

        ledger.Settle(results);
        var retry = ledger.Undo(work);
        Assert.Equal(2, retry.Items.Count);
        var settled = await rig.Protocol.UndoAsync([retry]);
        Assert.All(settled, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.Contains(LogId, rig.Platform.Removed);
        Assert.Contains(Minted + "2", rig.Platform.Removed);
    }

    [Fact]
    public async Task A_record_the_run_left_out_of_a_batch_keeps_only_its_intent_and_its_undo_answers_gone()
    {
        using var rig = new Rig();
        const string other = "dev:master-data--Wellbore:atomic-left-out";

        // The run writes the first record of its manifest and drops the second.
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script
        {
            Effect = (platform, run) => platform.Put((JsonObject)run.Context["manifest"]!["MasterData"]![0]!.DeepClone()),
        });
        var writtenLedger = rig.Ledger();
        var droppedLedger = rig.Ledger();
        var dropped = droppedLedger.Track(Work(Document(other, "dropped")));
        var outcomes = await rig.Protocol.DeliverBatchAsync([writtenLedger.Track(Work(Document(WellboreId, "kept"))), dropped]);

        Assert.True(outcomes[0].Succeeded, outcomes[0].Failure?.Message);
        Assert.False(outcomes[1].Succeeded);
        Assert.Contains("not in storage", outcomes[1].Failure!.Message, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Pending, writtenLedger.Slot(TargetArtifact.RecordSlot).Status);
        Assert.Equal(ArtifactStatus.Intent, droppedLedger.Slot(TargetArtifact.RecordSlot).Status);
        Assert.Equal(
            writtenLedger.Slot(TargetArtifact.RecordSlot).Locator,
            droppedLedger.Slot(TargetArtifact.RecordSlot).Locator);

        var results = await rig.Protocol.UndoAsync([droppedLedger.Undo(dropped)]);
        Assert.Equal(ArtifactStatus.Gone, Record(results).Outcome);
        Assert.Equal(ArtifactStatus.Kept, RunOf(results).Outcome);
        Assert.DoesNotContain(WellboreId, rig.Platform.Removed);
    }

    [Fact]
    public async Task What_the_manifest_route_never_makes_is_kept_and_every_item_is_answered_once()
    {
        using var rig = new Rig();
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Terminal = "failed" });
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Document(WellboreId, "after")));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var owned = ledger.Undo(work);
        var mixed = owned with
        {
            Items =
            [
                .. owned.Items,
                new UndoItem(3_000_001, TargetArtifact.Created("session", ArtifactRoles.Session, "session-1"), ledger.Unit.Id, ledger.Unit.StartedUtc),
                new UndoItem(3_000_002, TargetArtifact.Created("content:Kr", ArtifactRoles.Content, "dev:dataset--File.Generic:content-1"), ledger.Unit.Id, ledger.Unit.StartedUtc),
            ],
        };

        var results = await rig.Protocol.UndoAsync([mixed]);
        FileFamilyLedger.AssertAnsweredOnce([mixed], results);
        Assert.Equal(ArtifactStatus.Gone, FileFamilyLedger.For(results, TargetArtifact.RecordSlot).Outcome);
        Assert.All(results.Where(r => r.Item.ArtifactId >= 3_000_001), r =>
        {
            Assert.Equal(ArtifactStatus.Kept, r.Outcome);
            Assert.Equal("the manifest route makes nothing of this kind", r.Note);
        });
    }
}
