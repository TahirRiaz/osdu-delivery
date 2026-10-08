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
/// The composed routes as units of work (docs/atomic-delivery-plan.md, The routes): fileAndDdms and manifestAndDdms declare
/// what their file or manifest part and their DDMS part create, and their undo hands each artifact to the part that made it:
/// the datasets of the files (and, for manifestAndDdms, the record a run wrote) to the file or manifest part, a session and
/// what else the DDMS made beside the record to the DDMS part, and the record of fileAndDdms to its DDMS. A bulk write the DDMS
/// refuses after the record was written leaves both parts to the undo, the record first; the datasets a payload change
/// replaced are named superseded, and the record is left to newer work when it writes it again.
/// </summary>
public sealed class AtomicComposedRouteTests
{
    private const string LogId = "dev:work-product-component--WellLog:atomic-log";
    private const string LogKind = "osdu:wks:work-product-component--WellLog:1.2.0";
    private const string Ddms = FakeOsduPlatform.DdmsRoot + "/ddms/v3/welllogs";
    private const string Ingest = "Osdu_ingest";
    private const string Minted = "dev:dataset--File.Generic:minted-";
    private const string StorageRecords = "/api/storage/v2/records";

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Platform = new FakeOsduPlatform();
            FileFamilyFaults.IndexLive(Platform);
            Platform.Register(Ingest, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
            Faults = new FileFamilyFaults(Platform);
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                Secrets, TimeProvider.System, Faults, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            var options = new ProtocolOptions { DdmsRoot = FakeOsduPlatform.DdmsRoot, WorkflowPollSeconds = 1, DatasetIndexWaitSeconds = 0 };
            FileAndDdms = new OsduFileAndDdmsProtocol(Client, options, Samples.Logger<OsduFileAndDdmsProtocol>());
            ManifestAndDdms = new OsduManifestAndDdmsProtocol(Client, options, Samples.Logger<OsduManifestAndDdmsProtocol>());
        }

        public FakeOsduPlatform Platform { get; }

        public FileFamilyFaults Faults { get; }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public OsduFileAndDdmsProtocol FileAndDdms { get; }

        public OsduManifestAndDdmsProtocol ManifestAndDdms { get; }

        public IDeliveryProtocol Route(string name) => name == "fileAndDdms" ? FileAndDdms : ManifestAndDdms;

        public FileFamilyLedger Ledger(DateTime? startedUtc = null) => new(() => Platform.Calls.Count, startedUtc);

        public long VersionOf(string id) => Platform.Records[id]["version"]!.GetValue<long>();

        public void RefuseBulk() => Faults.Refuse(HttpMethod.Post, Ddms + "/" + LogId + "/data", HttpStatusCode.InternalServerError);

        public void Dispose()
        {
            Runtime.Dispose();
            Faults.Dispose();
        }
    }

    private static JsonObject Log(string name = "GR run") => FakeOsduPlatform.Record(LogId, LogKind, new JsonObject
    {
        ["Name"] = name,
        ["Curves"] = new JsonArray(new JsonObject { ["CurveID"] = "MD" }, new JsonObject { ["CurveID"] = "GR" }),
    });

    private static WorkPayloadPart LasFiles(string hash, string text = "~VERSION INFORMATION")
        => new(PayloadParts.Files, "las", new MemoryFiles(("run.las", text)), hash);

    private static WorkPayloadPart Curves(string hash)
        => new(PayloadParts.Bulk, "curves", new DdmsRouteTests.ColumnChunks(1, "MD", "GR"), hash);

    private static DeliveryWork Work(
        JsonObject document, IReadOnlyList<WorkPayloadPart> parts, bool metadata = true, long? existing = null,
        IReadOnlyDictionary<string, string>? state = null, bool forceFiles = false)
        => new()
        {
            Key = DeliveryKey.Derive("atomic-composed-route", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = parts.Count > 0,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Parts = parts,
            ForcedParts = forceFiles ? new HashSet<string>(StringComparer.Ordinal) { PayloadParts.Files } : new HashSet<string>(StringComparer.Ordinal),
        };

    private static async Task<DeliveryOutcome> DeliverAsync(IDeliveryProtocol route, DeliveryWork work)
        => (await route.DeliverBatchAsync([work]))[0];

    private static int IndexOf(FakeOsduPlatform platform, HttpMethod method, string path, int from = 0)
    {
        var at = platform.Calls.Skip(from).ToList().FindIndex(c => c.Method == method && Uri.UnescapeDataString(c.Uri.AbsolutePath) == path);
        return at < 0 ? at : at + from;
    }

    private static string[] DatasetsOf(JsonObject record) => record["data"]!["Datasets"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    private static string? BulkLink(JsonObject record) => record["data"]?["ExtensionProperties"]?["wdms"]?["bulkURI"]?.GetValue<string>();

    private static string Delete(string id) => StorageRecords + "/" + id + ":delete";

    /// <summary>The call that removes the record itself on <paramref name="route"/>: the DDMS's logical delete, or storage's for a record a run wrote.</summary>
    private static (HttpMethod Method, string Path) RecordRemoval(string route)
        => route == "fileAndDdms" ? (HttpMethod.Delete, Ddms + "/" + LogId) : (HttpMethod.Post, Delete(LogId));

    [Fact]
    public async Task A_bulk_write_the_ddms_refuses_leaves_the_files_datasets_and_the_record_to_the_undo_of_fileAndDdms()
    {
        using var rig = new Rig();
        rig.RefuseBulk();
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Log(), [LasFiles("f1"), Curves("b1")]));
        var outcome = await DeliverAsync(rig.FileAndDdms, work);
        Assert.False(outcome.Succeeded);
        Assert.Contains("500", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.Equal(["upload-0", "register-0", "register-0", OsduDdmsProtocol.MetadataStep + "-intent", OsduDdmsProtocol.MetadataStep], ledger.Reports.Select(r => r.Step));

        // The file's registration is declared before its call, as on the file route.
        var registration = IndexOf(rig.Platform, HttpMethod.Post, "/api/file/v2/files/metadata");
        Assert.True(Assert.Single(ledger.Reports, r => r.Step == "register-0" && r.Artifacts.Any(a => a.Status == ArtifactStatus.Intent)).CallsBefore <= registration);

        // The record is the unit's from its write until its bulk data lands: its write is declared before it goes, since bulk
        // data follows, and reported at the version it landed once it answered.
        var written = IndexOf(rig.Platform, HttpMethod.Post, Ddms);
        var declared = Assert.Single(ledger.Reports, r => r.Step == OsduDdmsProtocol.MetadataStep + "-intent");
        Assert.True(declared.CallsBefore <= written, "the record's DDMS write was declared after it was sent");
        var intent = Assert.Single(declared.Artifacts);
        Assert.Equal(TargetArtifact.RecordSlot, intent.Slot);
        Assert.Equal(ArtifactRoles.Record, intent.Role);
        Assert.Equal(ArtifactStatus.Intent, intent.Status);
        Assert.Null(intent.Version);
        var metadata = Assert.Single(ledger.Reports, r => r.Step == OsduDdmsProtocol.MetadataStep);
        Assert.True(metadata.CallsBefore > written);
        var record = Assert.Single(metadata.Artifacts);
        Assert.Equal(TargetArtifact.RecordSlot, record.Slot);
        Assert.Equal(ArtifactRoles.Record, record.Role);
        Assert.Equal(LogId, record.TargetId);
        Assert.Equal(rig.VersionOf(LogId), record.Version);
        Assert.Null(record.PriorVersion);
        Assert.Equal([Minted + "2" + ":"], DatasetsOf(rig.Platform.Records[LogId]));

        var undo = ledger.Undo(work);
        Assert.Equal(2, undo.Items.Count);
        var mark = rig.Platform.Calls.Count;
        var results = await rig.FileAndDdms.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.Contains(LogId, rig.Platform.Removed);
        Assert.Contains(Minted + "2", rig.Platform.Removed);

        // The record goes first, through its DDMS's logical delete; then the dataset, through storage's soft delete.
        var recordCall = IndexOf(rig.Platform, HttpMethod.Delete, Ddms + "/" + LogId, mark);
        Assert.True(recordCall >= 0);
        Assert.True(IndexOf(rig.Platform, HttpMethod.Post, Delete(Minted + "2"), mark) > recordCall);
    }

    [Fact]
    public async Task A_resumed_try_of_fileAndDdms_sends_only_the_bulk_data_the_refused_try_did_not_land()
    {
        using var rig = new Rig();
        rig.RefuseBulk();
        var ledger = rig.Ledger();
        Assert.False((await DeliverAsync(rig.FileAndDdms, ledger.Track(Work(Log(), [LasFiles("f1"), Curves("b1")])))).Succeeded);
        var reported = ledger.Reports.Count;
        var rows = ledger.Rows;

        var mark = rig.Platform.Calls.Count;
        var resumed = await DeliverAsync(rig.FileAndDdms, ledger.Resume(Work(Log(), [LasFiles("f1"), Curves("b1")])));
        Assert.True(resumed.Succeeded, resumed.Failure?.Message);
        Assert.Equal(
            ["POST " + Ddms + "/" + LogId + "/data", "GET " + Ddms + "/" + LogId],
            rig.Platform.Calls.Skip(mark).Select(c => c.Method + " " + Uri.UnescapeDataString(c.Uri.AbsolutePath)));
        Assert.Empty(ledger.Reports.Skip(reported).SelectMany(r => r.Artifacts));
        Assert.Equal(rows, ledger.Rows);
        Assert.Contains(resumed.Steps, s => s.Name == OsduDdmsProtocol.MetadataStep && s.Resumed);
        Assert.Equal(Minted + "2", resumed.Returned[FileUploads.DatasetIdsValue]);
    }

    [Fact]
    public async Task A_record_whose_ddms_write_landed_without_an_answer_is_named_so_the_undo_takes_it_back_with_its_datasets()
    {
        using var rig = new Rig();
        rig.Faults.LoseAnswer(HttpMethod.Post, Ddms);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Log(), [LasFiles("f1"), Curves("b1")]));
        Assert.False((await DeliverAsync(rig.FileAndDdms, work)).Succeeded);

        // The DDMS wrote the record, pointing at the dataset of its file, and the bulk data never went.
        Assert.Equal([Minted + "2" + ":"], DatasetsOf(rig.Platform.Records[LogId]));
        Assert.False(rig.Platform.Bulk.ContainsKey(LogId));

        var undo = ledger.Undo(work);
        var results = await rig.FileAndDdms.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        Assert.Contains(Minted + "2", rig.Platform.Removed);
        Assert.Contains(LogId, rig.Platform.Removed);
    }

    [Theory]
    [InlineData("fileAndDdms")]
    [InlineData("manifestAndDdms")]
    public async Task The_undo_of_a_composed_route_takes_the_record_back_before_its_datasets(string route)
    {
        using var rig = new Rig();
        rig.RefuseBulk();
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Log(), [LasFiles("f1"), Curves("b1")]));
        Assert.False((await DeliverAsync(rig.Route(route), work)).Succeeded);

        // A record removed after its datasets would name removed datasets for as long as its own removal failed.
        var mark = rig.Platform.Calls.Count;
        var results = await rig.Route(route).UndoAsync([ledger.Undo(work)]);
        Assert.All(results.Where(r => r.Item.Artifact.Role != ArtifactRoles.Run), r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        var (method, path) = RecordRemoval(route);
        var record = IndexOf(rig.Platform, method, path, mark);
        var dataset = IndexOf(rig.Platform, HttpMethod.Post, Delete(Minted + "2"), mark);
        Assert.True(record >= 0 && dataset >= 0);
        Assert.True(record < dataset, $"the {route} undo removed the record's dataset before the record");
    }

    [Fact]
    public async Task An_update_whose_bulk_write_is_refused_gives_the_record_back_its_version_and_removes_only_the_new_dataset()
    {
        using var rig = new Rig();
        var first = await DeliverAsync(rig.FileAndDdms, Work(Log(), [LasFiles("f1"), Curves("b1")]));
        Assert.True(first.Succeeded, first.Failure?.Message);
        var link = BulkLink(rig.Platform.Records[LogId]);
        Assert.NotNull(link);

        rig.RefuseBulk();
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Log(), [LasFiles("f1", "~VERSION INFORMATION 2.0"), Curves("b2")], metadata: false, existing: first.TargetVersion, state: first.Returned, forceFiles: true));
        Assert.False((await DeliverAsync(rig.FileAndDdms, work)).Succeeded);
        Assert.Equal([Minted + "4" + ":"], DatasetsOf(rig.Platform.Records[LogId]));
        var record = ledger.Slot(TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactRoles.Version, record.Role);
        Assert.Equal(first.TargetVersion, record.PriorVersion);

        var undo = ledger.Undo(work);
        var results = await rig.FileAndDdms.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        var restored = FileFamilyLedger.For(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Restored, restored.Outcome);
        Assert.Contains($"version {first.TargetVersion!.Value.ToString(CultureInfo.InvariantCulture)} written back", restored.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(results, r => r.Item.Artifact.Role == ArtifactRoles.Dataset).Outcome);

        // OSDU serves the record as its earlier delivery left it: its dataset and its bulk link.
        Assert.Equal([Minted + "2" + ":"], DatasetsOf(rig.Platform.Records[LogId]));
        Assert.Equal(link, BulkLink(rig.Platform.Records[LogId]));
        Assert.Contains(Minted + "4", rig.Platform.Removed);
        Assert.DoesNotContain(Minted + "2", rig.Platform.Removed);
        Assert.DoesNotContain(LogId, rig.Platform.Removed);
    }

    [Theory]
    [InlineData("fileAndDdms")]
    [InlineData("manifestAndDdms")]
    public async Task The_undo_hands_each_artifact_to_the_part_that_made_it_and_answers_each_once(string route)
    {
        using var rig = new Rig();
        const string dataset = "dev:dataset--File.Generic:split-1";
        rig.Platform.Put(FakeOsduPlatform.Record(dataset, "osdu:wks:dataset--File.Generic:1.0.0"));
        rig.Platform.Put(Log());
        var unit = new DeliveryUnit(Guid.NewGuid(), DateTime.UtcNow);
        var work = new UndoWork
        {
            Key = DeliveryKey.Derive("atomic-composed-route", [LogId]),
            TargetId = LogId,
            Reason = UndoReason.Held,
            Items =
            [
                new UndoItem(4_000_001, TargetArtifact.Created("file:0:split", ArtifactRoles.Dataset, dataset, locator: "/staging/landing/split"), unit.Id, unit.StartedUtc),
                new UndoItem(4_000_002, TargetArtifact.RecordWritten(LogId, rig.VersionOf(LogId), null), unit.Id, unit.StartedUtc),
                new UndoItem(4_000_003, TargetArtifact.Created("session", ArtifactRoles.Session, "s-1", locator: LogId), unit.Id, unit.StartedUtc),
            ],
        };

        var mark = rig.Platform.Calls.Count;
        var results = await rig.Route(route).UndoAsync([work]);
        FileFamilyLedger.AssertAnsweredOnce([work], results);
        var byId = results.ToDictionary(r => r.Item.ArtifactId);
        Assert.Equal(ArtifactStatus.Removed, byId[4_000_001].Outcome);
        Assert.Equal(ArtifactStatus.Removed, byId[4_000_002].Outcome);

        // The session is the DDMS's: it is asked of the DDMS, which no longer knows it, never kept as something the route
        // does not make.
        Assert.Equal(ArtifactStatus.Gone, byId[4_000_003].Outcome);
        Assert.Contains("session s-1", byId[4_000_003].Note, StringComparison.Ordinal);
        Assert.True(IndexOf(rig.Platform, HttpMethod.Get, Ddms + "/" + LogId + "/sessions/s-1", mark) >= 0);
        Assert.True(IndexOf(rig.Platform, HttpMethod.Post, Delete(dataset), mark) >= 0);
        var (method, path) = RecordRemoval(route);
        Assert.True(IndexOf(rig.Platform, method, path, mark) >= 0);
        Assert.Contains(dataset, rig.Platform.Removed);
        Assert.Contains(LogId, rig.Platform.Removed);
    }

    [Theory]
    [InlineData("fileAndDdms")]
    [InlineData("manifestAndDdms")]
    public async Task A_session_whose_state_the_ddms_cannot_answer_is_failed_for_a_later_try_not_gone(string route)
    {
        using var rig = new Rig();
        var unit = new DeliveryUnit(Guid.NewGuid(), DateTime.UtcNow);
        var work = new UndoWork
        {
            Key = DeliveryKey.Derive("atomic-composed-route", [LogId]),
            TargetId = LogId,
            Reason = UndoReason.Failed,
            Items = [new UndoItem(4_100_001, TargetArtifact.Created("session", ArtifactRoles.Session, "s-2", locator: LogId), unit.Id, unit.StartedUtc)],
        };

        // The DDMS cannot be asked whether the session is still open: an open session would stay open if this read as gone.
        rig.Faults.Refuse(HttpMethod.Get, Ddms + "/" + LogId + "/sessions/s-2", HttpStatusCode.ServiceUnavailable);
        var result = Assert.Single(await rig.Route(route).UndoAsync([work]));
        Assert.Equal(ArtifactStatus.Failed, result.Outcome);
        Assert.Contains("503", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_ddms_cannot_undo_yet_holds_the_record_back_and_the_record_holds_back_its_datasets()
    {
        using var rig = new Rig();
        rig.RefuseBulk();
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Log(), [LasFiles("f1"), Curves("b1")]));
        Assert.False((await DeliverAsync(rig.FileAndDdms, work)).Succeeded);
        var owned = ledger.Undo(work);
        var undo = owned with
        {
            Items = [.. owned.Items, new UndoItem(4_300_001, TargetArtifact.Created("session", ArtifactRoles.Session, "s-3", locator: LogId), ledger.Unit.Id, ledger.Unit.StartedUtc)],
        };

        // A session whose state cannot be read may still commit bulk data under the record, so the record waits for it, and the
        // dataset the record names waits for the record.
        rig.Faults.Refuse(HttpMethod.Get, Ddms + "/" + LogId + "/sessions/s-3", HttpStatusCode.ServiceUnavailable);
        var waiting = await rig.FileAndDdms.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], waiting);
        Assert.Equal(ArtifactStatus.Failed, Assert.Single(waiting, r => r.Item.ArtifactId == 4_300_001).Outcome);
        var record = FileFamilyLedger.For(waiting, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Failed, record.Outcome);
        Assert.Contains("could not be undone yet", record.Note, StringComparison.Ordinal);
        Assert.Contains("the record is taken back with them, on the next undo", record.Note, StringComparison.Ordinal);
        var dataset = Assert.Single(waiting, r => r.Item.Artifact.Role == ArtifactRoles.Dataset);
        Assert.Equal(ArtifactStatus.Failed, dataset.Outcome);
        Assert.StartsWith("the record could not be taken back yet", dataset.Note, StringComparison.Ordinal);
        Assert.Empty(rig.Platform.Removed);

        // Once the DDMS answers that it no longer knows the session, the record and then its dataset go.
        var settled = await rig.FileAndDdms.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], settled);
        Assert.Equal(ArtifactStatus.Gone, Assert.Single(settled, r => r.Item.ArtifactId == 4_300_001).Outcome);
        Assert.Equal(ArtifactStatus.Removed, FileFamilyLedger.For(settled, TargetArtifact.RecordSlot).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(settled, r => r.Item.Artifact.Role == ArtifactRoles.Dataset).Outcome);
        Assert.Contains(LogId, rig.Platform.Removed);
        Assert.Contains(Minted + "2", rig.Platform.Removed);
    }

    [Fact]
    public async Task A_payload_change_of_fileAndDdms_names_the_earlier_datasets_superseded_and_new_bulk_data_alone_none()
    {
        using var rig = new Rig();
        var first = await DeliverAsync(rig.FileAndDdms, Work(Log(), [LasFiles("f1"), Curves("b1")]));
        Assert.True(first.Succeeded, first.Failure?.Message);
        Assert.Empty(first.Superseded);

        var files = await DeliverAsync(rig.FileAndDdms, Work(Log(), [LasFiles("f1", "~A 2"), Curves("b1")], metadata: false, existing: first.TargetVersion, state: first.Returned, forceFiles: true));
        Assert.True(files.Succeeded, files.Failure?.Message);
        Assert.Equal([Minted + "2"], files.Superseded);

        var state = new Dictionary<string, string>(first.Returned, StringComparer.Ordinal);
        foreach (var (name, value) in files.Returned)
        {
            state[name] = value;
        }

        var bulk = await DeliverAsync(rig.FileAndDdms, Work(Log(), [LasFiles("f1", "~A 2"), Curves("b3")], metadata: false, existing: files.TargetVersion, state: state));
        Assert.True(bulk.Succeeded, bulk.Failure?.Message);
        Assert.Empty(bulk.Superseded);

        // Files that went with a delivery that did not complete supersede nothing.
        rig.RefuseBulk();
        var failed = await DeliverAsync(rig.FileAndDdms, Work(Log(), [LasFiles("f1", "~A 3"), Curves("b4")], metadata: false, existing: bulk.TargetVersion, state: state, forceFiles: true));
        Assert.False(failed.Succeeded);
        Assert.Empty(failed.Superseded);
    }

    [Theory]
    [InlineData("fileAndDdms")]
    [InlineData("manifestAndDdms")]
    public async Task Newer_work_keeps_the_record_and_the_datasets_of_the_abandoned_unit_are_still_removed(string route)
    {
        using var rig = new Rig();
        rig.RefuseBulk();
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Log(), [LasFiles("f1"), Curves("b1")]));
        Assert.False((await DeliverAsync(rig.Route(route), work)).Succeeded);

        var undo = ledger.Undo(work, UndoReason.Abandoned, keepRecord: true);
        var results = await rig.Route(route).UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        Assert.Equal(ArtifactStatus.Superseded, FileFamilyLedger.For(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(results, r => r.Item.Artifact.Role == ArtifactRoles.Dataset).Outcome);
        Assert.DoesNotContain(LogId, rig.Platform.Removed);
        Assert.Contains(Minted + "2", rig.Platform.Removed);
    }

    [Fact]
    public async Task A_bulk_write_the_ddms_refuses_after_the_manifest_wrote_the_record_leaves_both_to_the_undo_of_manifestAndDdms()
    {
        using var rig = new Rig();
        rig.RefuseBulk();
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Log(), [LasFiles("f1"), Curves("b1")]));
        var outcome = await DeliverAsync(rig.ManifestAndDdms, work);
        Assert.False(outcome.Succeeded);
        Assert.Equal(
            ["upload-0", "register-0", "register-0", "manifest-intent", OsduManifestProtocol.ManifestStep, OsduManifestProtocol.RecordsStep],
            ledger.Reports.Select(r => r.Step));
        var record = ledger.Slot(TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactRoles.Record, record.Role);
        Assert.Equal(ArtifactStatus.Pending, record.Status);
        Assert.Equal(rig.VersionOf(LogId), record.Version);
        Assert.Equal(Ingest + "|" + Assert.Single(rig.Platform.Runs).RunId, record.Locator);

        // The run is the manifest part's, answered with how it ended; the record and the dataset are taken back.
        var undo = ledger.Undo(work);
        var results = await rig.ManifestAndDdms.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        var run = Assert.Single(results, r => r.Item.Artifact.Role == ArtifactRoles.Run);
        Assert.Equal(ArtifactStatus.Kept, run.Outcome);
        Assert.Contains("ended FINISHED", run.Note, StringComparison.Ordinal);
        Assert.All(results.Where(r => r.Item.Artifact.Role != ArtifactRoles.Run), r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.Contains(LogId, rig.Platform.Removed);
        Assert.Contains(Minted + "2", rig.Platform.Removed);
        Assert.False(rig.Platform.Bulk.ContainsKey(LogId));
    }

    [Fact]
    public async Task A_record_a_run_rewrote_over_one_OSDU_created_before_the_unit_is_given_back_its_earlier_version()
    {
        using var rig = new Rig();

        // Storage holds the record soft-deleted, so the run is declared as creating it; the run writes it again.
        var earlier = rig.Platform.Put(Log("before"));
        rig.Platform.Removed.Add(LogId);
        rig.RefuseBulk();
        var ledger = rig.Ledger(DateTime.UtcNow);
        var work = ledger.Track(Work(Log("after"), [Curves("b1")]));
        Assert.False((await DeliverAsync(rig.ManifestAndDdms, work)).Succeeded);
        var record = ledger.Slot(TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactRoles.Record, record.Role);
        Assert.Equal(rig.VersionOf(LogId), record.Version);
        rig.Platform.Records[LogId]["createTime"] = ledger.Unit.StartedUtc.AddDays(-3).ToString("O", CultureInfo.InvariantCulture);

        var result = FileFamilyLedger.For(await rig.ManifestAndDdms.UndoAsync([ledger.Undo(work)]), TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Restored, result.Outcome);
        Assert.Contains($"version {earlier.ToString(CultureInfo.InvariantCulture)} written back", result.Note, StringComparison.Ordinal);
        Assert.Equal("before", rig.Platform.Records[LogId]["data"]!["Name"]!.GetValue<string>());
        Assert.DoesNotContain(LogId, rig.Platform.Removed);
    }

    [Fact]
    public async Task While_a_run_is_going_the_ddms_part_is_undone_and_the_manifest_part_waits()
    {
        using var rig = new Rig();
        const string runId = "0b7e5d4c-3a2f-4e1d-9c8b-7a6f5e4d3c2b";
        rig.Platform.Register(Ingest, new FakeOsduPlatform.Script { Pending = "running" });
        rig.Platform.Runs.Add(new FakeOsduPlatform.Run(Ingest, runId, new JsonObject()));
        var unit = new DeliveryUnit(Guid.NewGuid(), DateTime.UtcNow);
        var work = new UndoWork
        {
            Key = DeliveryKey.Derive("atomic-composed-route", [LogId]),
            TargetId = LogId,
            Reason = UndoReason.Held,
            Items =
            [
                new UndoItem(4_200_001, TargetArtifact.Intent(TargetArtifact.RecordSlot, ArtifactRoles.Record, Ingest + "|" + runId, LogId), unit.Id, unit.StartedUtc),
                new UndoItem(4_200_002, TargetArtifact.Created("session", ArtifactRoles.Session, "s-9", locator: LogId), unit.Id, unit.StartedUtc),
            ],
        };

        var results = await rig.ManifestAndDdms.UndoAsync([work]);
        FileFamilyLedger.AssertAnsweredOnce([work], results);
        var record = Assert.Single(results, r => r.Item.ArtifactId == 4_200_001);
        Assert.Equal(ArtifactStatus.Failed, record.Outcome);
        Assert.Contains($"workflow run {runId} of {Ingest} is still RUNNING", record.Note, StringComparison.Ordinal);
        Assert.NotEqual(ArtifactStatus.Failed, Assert.Single(results, r => r.Item.ArtifactId == 4_200_002).Outcome);
    }

    [Fact]
    public async Task A_payload_change_of_manifestAndDdms_passes_the_manifests_superseded_datasets_through()
    {
        using var rig = new Rig();
        var first = await DeliverAsync(rig.ManifestAndDdms, Work(Log(), [LasFiles("f1"), Curves("b1")]));
        Assert.True(first.Succeeded, first.Failure?.Message);
        Assert.Empty(first.Superseded);

        var second = await DeliverAsync(rig.ManifestAndDdms, Work(Log(), [LasFiles("f1", "~A 2"), Curves("b1")], metadata: false, existing: first.TargetVersion, state: first.Returned, forceFiles: true));
        Assert.True(second.Succeeded, second.Failure?.Message);
        Assert.Equal([Minted + "2"], second.Superseded);
        Assert.Equal([Minted + "4" + ":"], DatasetsOf(rig.Platform.Records[LogId]));
    }
}
