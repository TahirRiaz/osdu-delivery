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
/// The dataset route as a unit of work (docs/atomic-delivery-plan.md, The routes): storage's read decides before each
/// registration whether it creates the dataset or writes a version over the one storage holds, the intent goes before the
/// registration and the landed version after it, a record storage cannot answer for fails alone unregistered, and the undo of
/// every failure point: a dataset the unit created removed through the Dataset service once storage confirms OSDU created it
/// after the unit began, a dataset the unit wrote a version of given back the version before, an intent that never landed
/// answered gone without a write, and the record's newer work never keeping what a registration wrote.
/// </summary>
public sealed class AtomicDatasetRouteTests
{
    private const string TraceId = "dev:work-product-component--SeismicTraceData:atomic-7";
    private const string TraceKind = "osdu:wks:work-product-component--SeismicTraceData:1.3.0";
    private const string ReportId = "dev:dataset--File.Generic:atomic-report";
    private const string DatasetKind = "osdu:wks:dataset--File.Generic:1.0.0";
    private const string Register = "/api/dataset/v1/registerDataset";
    private const string Instructions = "/api/dataset/v1/storageInstructions";
    private const string QueryRecords = "/api/storage/v2/query/records";
    private const string StorageRecords = "/api/storage/v2/records";

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Platform = new FakeOsduPlatform();
            Faults = new FileFamilyFaults(Platform);
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                Secrets, TimeProvider.System, Faults, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            Options = new ProtocolOptions
            {
                PayloadContentType = "application/octet-stream",
                UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-blob-type"] = "BlockBlob" },
            };
            Protocol = new OsduDatasetProtocol(Client, Options, Samples.Logger<OsduDatasetProtocol>());
        }

        public FakeOsduPlatform Platform { get; }

        public FileFamilyFaults Faults { get; }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public ProtocolOptions Options { get; }

        public OsduDatasetProtocol Protocol { get; }

        public FileFamilyLedger Ledger(DateTime? startedUtc = null) => new(() => Platform.Calls.Count, startedUtc);

        public string FilesOf(string targetId) => OsduDatasetProtocol.DatasetIdFor(Options, targetId);

        public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work) => (await Protocol.DeliverBatchAsync([work]))[0];

        public long VersionOf(string id) => Platform.Records[id]["version"]!.GetValue<long>();

        public void Dispose()
        {
            Runtime.Dispose();
            Faults.Dispose();
        }
    }

    private static JsonObject Trace(string id = TraceId) => FakeOsduPlatform.Record(id, TraceKind);

    private static JsonObject Report() => FakeOsduPlatform.Record(ReportId, DatasetKind);

    private static MemoryFiles Files(string text) => new(("line.segy", text));

    private static DeliveryWork Work(JsonObject document, IPayloadSource? files, bool metadata = true, bool payload = true, long? existing = null, IReadOnlyDictionary<string, string>? state = null)
        => new()
        {
            Key = DeliveryKey.Derive("atomic-dataset-route", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = payload,
            Payload = files,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

    /// <summary>An earlier delivery of the record that completed, so the next one updates it.</summary>
    private static async Task<DeliveryOutcome> SeedAsync(Rig rig, JsonObject document)
    {
        var seeded = await rig.DeliverAsync(Work(document, Files("first")));
        Assert.True(seeded.Succeeded, seeded.Failure?.Message);
        return seeded;
    }

    private static DeliveryWork Update(JsonObject document, DeliveryOutcome earlier, string text = "second")
        => Work(document, Files(text), existing: earlier.TargetVersion, state: earlier.Returned);

    private static string SourceOf(JsonObject record) => record["data"]!["DatasetProperties"]!["FileSourceInfo"]!["FileSource"]!.GetValue<string>();

    private static int IndexOf(FakeOsduPlatform platform, HttpMethod method, string path)
        => platform.Calls.FindIndex(c => c.Method == method && Uri.UnescapeDataString(c.Uri.AbsolutePath) == path);

    private static int Count(FakeOsduPlatform platform, HttpMethod method, string path, int from = 0)
        => platform.Calls.Skip(from).Count(c => c.Method == method && Uri.UnescapeDataString(c.Uri.AbsolutePath) == path);

    private static string SoftDelete(string id) => "/api/dataset/v1/metadataRecord/" + id + "/softDelete";

    private static string Text(long version) => version.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_record_of_another_kind_declares_the_dataset_of_its_files_created_before_its_registration_and_landed_after()
    {
        using var rig = new Rig();
        var files = rig.FilesOf(TraceId);
        var ledger = rig.Ledger();
        var outcome = await rig.DeliverAsync(ledger.Track(Work(Trace(), Files("L1"))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(["storage-files", OsduDatasetProtocol.RegisterIntentStep, OsduDatasetProtocol.RegisterStep, "records"], ledger.Reports.Select(r => r.Step));

        // Storage is asked first; the intent goes after its answer and before the registration.
        var read = IndexOf(rig.Platform, HttpMethod.Post, QueryRecords);
        var register = IndexOf(rig.Platform, HttpMethod.Put, Register);
        var intent = Assert.Single(ledger.Reports, r => r.Step == OsduDatasetProtocol.RegisterIntentStep);
        Assert.True(intent.CallsBefore > read && intent.CallsBefore <= register, "the intent was not reported between storage's read and the registration");
        Assert.Equal(files, intent.Report.Returned["datasetId"]);
        Assert.False(intent.Report.Returned.ContainsKey("priorVersion"));
        var declared = Assert.Single(intent.Artifacts);
        Assert.Equal(OsduDatasetProtocol.FilesDatasetSlot, declared.Slot);
        Assert.Equal(ArtifactRoles.Record, declared.Role);
        Assert.Equal(files, declared.TargetId);
        Assert.Null(declared.PriorVersion);
        Assert.Null(declared.Version);
        Assert.Equal(ArtifactStatus.Intent, declared.Status);

        var landed = Assert.Single(ledger.Reports, r => r.Step == OsduDatasetProtocol.RegisterStep);
        Assert.True(landed.CallsBefore > register);
        var artifact = Assert.Single(landed.Artifacts);
        Assert.Equal(ArtifactStatus.Pending, artifact.Status);
        Assert.Equal(rig.VersionOf(files), artifact.Version);
        Assert.Equal(Text(rig.VersionOf(files)), landed.Report.Returned["version"]);

        // The record that refers to the dataset is written last, in one write, and declares nothing.
        Assert.Empty(Assert.Single(ledger.Reports, r => r.Step == "records").Artifacts);
        var row = Assert.Single(ledger.Rows);
        Assert.Equal(ArtifactRoles.Record, row.Role);
        Assert.Equal(ArtifactStatus.Pending, row.Status);
        Assert.Equal(rig.VersionOf(files), row.Version);
    }

    [Fact]
    public async Task An_update_declares_the_version_storage_holds_and_reports_the_version_it_landed()
    {
        using var rig = new Rig();
        var files = rig.FilesOf(TraceId);
        var seeded = await SeedAsync(rig, Trace());
        var prior = rig.VersionOf(files);
        var ledger = rig.Ledger();
        var outcome = await rig.DeliverAsync(ledger.Track(Update(Trace(), seeded)));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        var intent = Assert.Single(ledger.Reports, r => r.Step == OsduDatasetProtocol.RegisterIntentStep);
        Assert.Equal(Text(prior), intent.Report.Returned["priorVersion"]);
        var declared = Assert.Single(intent.Artifacts);
        Assert.Equal(ArtifactRoles.Version, declared.Role);
        Assert.Equal(prior, declared.PriorVersion);
        Assert.Null(declared.Version);

        var row = Assert.Single(ledger.Rows);
        Assert.Equal(ArtifactRoles.Version, row.Role);
        Assert.Equal(prior, row.PriorVersion);
        Assert.Equal(rig.VersionOf(files), row.Version);
        Assert.True(row.Version > prior);

        // New files land on the same dataset, so a payload change makes nothing obsolete.
        Assert.Empty(outcome.Superseded);
        Assert.Equal(files, outcome.Returned[FileUploads.DatasetIdsValue]);
    }

    [Fact]
    public async Task A_dataset_record_is_declared_as_the_record_itself_and_given_back_its_version_when_its_registration_answer_is_lost()
    {
        using var rig = new Rig();
        var create = rig.Ledger();
        var seeded = await rig.DeliverAsync(create.Track(Work(Report(), Files("first"))));
        Assert.True(seeded.Succeeded, seeded.Failure?.Message);
        var created = Assert.Single(create.Rows);
        Assert.Equal(TargetArtifact.RecordSlot, created.Slot);
        Assert.Equal(ArtifactRoles.Record, created.Role);
        Assert.Equal(ReportId, created.TargetId);
        Assert.Equal(rig.VersionOf(ReportId), created.Version);
        var prior = rig.VersionOf(ReportId);
        var source = SourceOf(rig.Platform.Records[ReportId]);

        rig.Faults.LoseAnswer(HttpMethod.Put, Register);
        var update = rig.Ledger();
        var work = update.Track(Update(Report(), seeded));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var row = Assert.Single(update.Rows);
        Assert.Equal(TargetArtifact.RecordSlot, row.Slot);
        Assert.Equal(ArtifactRoles.Version, row.Role);
        Assert.Equal(prior, row.PriorVersion);
        Assert.Equal(ArtifactStatus.Intent, row.Status);
        Assert.NotEqual(source, SourceOf(rig.Platform.Records[ReportId]));

        var result = Assert.Single(await rig.Protocol.UndoAsync([update.Undo(work)]));
        Assert.Equal(ArtifactStatus.Restored, result.Outcome);
        Assert.Contains($"version {Text(prior)} written back", result.Note, StringComparison.Ordinal);
        Assert.Equal(source, SourceOf(rig.Platform.Records[ReportId]));
        Assert.DoesNotContain(ReportId, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_record_storage_cannot_answer_for_fails_alone_unregistered_and_the_others_land()
    {
        using var rig = new Rig();
        const string other = "dev:work-product-component--SeismicTraceData:atomic-8";
        var unanswered = rig.FilesOf(TraceId);
        var answered = rig.FilesOf(other);

        // Storage lists the first dataset without the version it holds.
        rig.Faults.Rewrite(FileFamilyFaults.On(HttpMethod.Post, QueryRecords), root =>
        {
            root["records"]!.AsArray().Add(new JsonObject { ["id"] = unanswered });
            var invalid = root["invalidRecords"]!.AsArray();
            foreach (var node in invalid.Where(n => n!.GetValue<string>() == unanswered).ToList())
            {
                invalid.Remove(node);
            }
        });

        var first = rig.Ledger();
        var second = rig.Ledger();
        var outcomes = await rig.Protocol.DeliverBatchAsync([first.Track(Work(Trace(), Files("a"))), second.Track(Work(Trace(other), Files("b")))]);

        Assert.False(outcomes[0].Succeeded);
        Assert.Contains($"storage could not say whether it holds {unanswered}", outcomes[0].Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was registered", outcomes[0].Failure!.Message, StringComparison.Ordinal);
        Assert.True(outcomes[1].Succeeded, outcomes[1].Failure?.Message);

        var registration = Assert.Single(rig.Platform.Calls, c => c.Method == HttpMethod.Put && c.Uri.AbsolutePath == Register);
        Assert.Contains(answered, registration.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(unanswered, registration.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(unanswered, rig.Platform.Records.Keys);
        Assert.Empty(first.Rows);
        Assert.DoesNotContain(first.Reports, r => r.Step == OsduDatasetProtocol.RegisterIntentStep);
        Assert.Single(second.Rows);
    }

    [Fact]
    public async Task A_dataset_storage_asks_to_be_read_again_is_not_declared_created_and_not_registered()
    {
        using var rig = new Rig();
        var files = rig.FilesOf(TraceId);
        var seeded = await SeedAsync(rig, Trace());
        var registrations = Count(rig.Platform, HttpMethod.Put, Register);

        // Storage holds the dataset, and answers the read by asking for a retry of it (MultiRecordInfo.retryRecords).
        rig.Faults.Rewrite(FileFamilyFaults.On(HttpMethod.Post, QueryRecords), root =>
        {
            var records = root["records"]!.AsArray();
            foreach (var node in records.Where(n => n!["id"]!.GetValue<string>() == files).ToList())
            {
                records.Remove(node);
            }

            root["retryRecords"] = new JsonArray(files);
        });

        var ledger = rig.Ledger();
        var outcome = await rig.DeliverAsync(ledger.Track(Update(Trace(), seeded)));
        Assert.False(outcome.Succeeded, "a dataset storage could not read was registered as if it held none");
        Assert.Equal(registrations, Count(rig.Platform, HttpMethod.Put, Register));
        Assert.Empty(ledger.Rows);
    }

    [Fact]
    public async Task A_record_held_before_anything_is_sent_reports_nothing_and_leaves_nothing_to_undo()
    {
        using var rig = new Rig();
        var ledger = rig.Ledger();
        var outcome = await rig.DeliverAsync(ledger.Track(Work(Trace(), new MemoryFiles(("a.segy", "a"), ("b.segy", "b")))));

        Assert.IsType<RecordHeldException>(outcome.Failure);
        Assert.Contains("holds 2 files", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Platform.Calls);
        Assert.Empty(ledger.Reports);
        Assert.Empty(ledger.Rows);
    }

    [Fact]
    public async Task A_storage_read_that_fails_as_a_whole_registers_nothing_and_declares_nothing()
    {
        using var rig = new Rig();
        rig.Faults.Refuse(HttpMethod.Post, QueryRecords, HttpStatusCode.InternalServerError);
        var first = rig.Ledger();
        var second = rig.Ledger();
        var outcomes = await rig.Protocol.DeliverBatchAsync(
            [first.Track(Work(Trace(), Files("a"))), second.Track(Work(Trace("dev:work-product-component--SeismicTraceData:atomic-9"), Files("b")))]);

        Assert.All(outcomes, o => Assert.False(o.Succeeded));
        Assert.All(outcomes, o => Assert.Contains("500", o.Failure!.Message, StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Platform.Calls, c => c.Uri.AbsolutePath == Register);
        Assert.DoesNotContain(rig.Platform.Calls, c => c.Uri.AbsolutePath == StorageRecords);
        Assert.Empty(first.Rows);
        Assert.Empty(second.Rows);
    }

    [Theory]
    [InlineData(false, FileFamilyLoss.Transport)]
    [InlineData(false, FileFamilyLoss.ServerError)]
    [InlineData(true, FileFamilyLoss.Transport)]
    [InlineData(true, FileFamilyLoss.Timeout)]
    public async Task A_registration_whose_answer_was_lost_is_removed_when_it_created_the_dataset_and_given_back_its_version_when_it_wrote_one(bool update, FileFamilyLoss loss)
    {
        using var rig = new Rig();
        var files = rig.FilesOf(TraceId);
        var seeded = update ? await SeedAsync(rig, Trace()) : null;
        long? prior = update ? rig.VersionOf(files) : null;
        var source = update ? SourceOf(rig.Platform.Records[files]) : null;

        rig.Faults.LoseAnswer(HttpMethod.Put, Register, loss);
        var ledger = rig.Ledger();
        var work = ledger.Track(seeded is null ? Work(Trace(), Files("L1")) : Update(Trace(), seeded));
        var before = rig.Platform.Calls.Count;
        var outcome = await rig.DeliverAsync(work);
        Assert.False(outcome.Succeeded);
        Assert.Contains(files, rig.Platform.Records.Keys);
        Assert.Equal(0, Count(rig.Platform, HttpMethod.Put, StorageRecords, before));

        // Only the intent is in the ledger: the registration landed and its answer did not come.
        var row = Assert.Single(ledger.Rows);
        Assert.Equal(ArtifactStatus.Intent, row.Status);
        Assert.Equal(update ? ArtifactRoles.Version : ArtifactRoles.Record, row.Role);
        Assert.Equal(prior, row.PriorVersion);
        Assert.Null(row.Version);

        var mark = rig.Platform.Calls.Count;
        var undo = ledger.Undo(work);
        var result = Assert.Single(await rig.Protocol.UndoAsync([undo]));
        if (update)
        {
            Assert.Equal(ArtifactStatus.Restored, result.Outcome);
            Assert.Contains($"version {Text(prior!.Value)} written back as version {Text(prior.Value + 2)}", result.Note, StringComparison.Ordinal);
            Assert.Equal(prior.Value + 2, rig.VersionOf(files));
            Assert.Equal(source, SourceOf(rig.Platform.Records[files]));
            Assert.DoesNotContain(files, rig.Platform.Removed);
        }
        else
        {
            Assert.Equal(ArtifactStatus.Removed, result.Outcome);
            Assert.Contains("dataset service", result.Note, StringComparison.Ordinal);
            Assert.Contains(files, rig.Platform.Removed);
            Assert.Equal(1, Count(rig.Platform, HttpMethod.Post, SoftDelete(files), mark));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_intent_of_a_registration_that_never_landed_is_gone_and_no_version_is_written(bool update)
    {
        using var rig = new Rig();
        var files = rig.FilesOf(TraceId);
        var seeded = update ? await SeedAsync(rig, Trace()) : null;
        var versions = update ? rig.Platform.History[files].Count : 0;

        rig.Faults.Refuse(HttpMethod.Put, Register, HttpStatusCode.InternalServerError);
        var ledger = rig.Ledger();
        var work = ledger.Track(seeded is null ? Work(Trace(), Files("L1")) : Update(Trace(), seeded));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        var mark = rig.Platform.Calls.Count;
        var result = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));
        Assert.Equal(ArtifactStatus.Gone, result.Outcome);
        Assert.Equal(0, Count(rig.Platform, HttpMethod.Put, StorageRecords, mark));
        Assert.Equal(0, Count(rig.Platform, HttpMethod.Post, SoftDelete(files), mark));
        if (update)
        {
            Assert.Contains($"OSDU's latest version is still {Text(rig.VersionOf(files))}", result.Note, StringComparison.Ordinal);
            Assert.Equal(versions, rig.Platform.History[files].Count);
        }
        else
        {
            Assert.Contains("OSDU no longer holds the record", result.Note, StringComparison.Ordinal);
            Assert.DoesNotContain(files, rig.Platform.Records.Keys);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_record_write_refused_after_its_dataset_landed_has_the_dataset_removed_or_given_back(bool update)
    {
        using var rig = new Rig();
        var files = rig.FilesOf(TraceId);
        var seeded = update ? await SeedAsync(rig, Trace()) : null;
        long? prior = update ? rig.VersionOf(files) : null;
        var source = update ? SourceOf(rig.Platform.Records[files]) : null;
        var traceVersion = update ? rig.VersionOf(TraceId) : (long?)null;

        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var ledger = rig.Ledger();
        var work = ledger.Track(seeded is null ? Work(Trace(), Files("L1")) : Update(Trace(), seeded));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var row = Assert.Single(ledger.Rows);
        Assert.Equal(ArtifactStatus.Pending, row.Status);
        Assert.Equal(rig.VersionOf(files), row.Version);

        var mark = rig.Platform.Calls.Count;
        var result = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));
        if (update)
        {
            Assert.Equal(ArtifactStatus.Restored, result.Outcome);
            Assert.Equal(source, SourceOf(rig.Platform.Records[files]));
            Assert.True(rig.VersionOf(files) > row.Version);
            Assert.Equal(traceVersion, rig.VersionOf(TraceId));
            Assert.Contains($"version {Text(prior!.Value)} written back", result.Note, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(ArtifactStatus.Removed, result.Outcome);
            Assert.Contains(files, rig.Platform.Removed);
            Assert.Equal(1, Count(rig.Platform, HttpMethod.Post, SoftDelete(files), mark));
            Assert.DoesNotContain(TraceId, rig.Platform.Records.Keys);
        }
    }

    [Fact]
    public async Task A_dataset_the_service_cannot_hand_out_leaves_its_registration_an_intent_which_the_undo_removes()
    {
        using var rig = new Rig();
        var files = rig.FilesOf(TraceId);
        rig.Platform.Unretrievable.Add(files);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Trace(), Files("L1")));
        var outcome = await rig.DeliverAsync(work);

        Assert.False(outcome.Succeeded);
        Assert.Contains("answers no retrieval instructions", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ledger.Reports, r => r.Step == OsduDatasetProtocol.RegisterStep);
        Assert.DoesNotContain(TraceId, rig.Platform.Records.Keys);
        var row = Assert.Single(ledger.Rows);
        Assert.Equal(ArtifactStatus.Intent, row.Status);
        Assert.Equal(files, row.TargetId);

        var result = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));
        Assert.Equal(ArtifactStatus.Removed, result.Outcome);
        Assert.Contains(files, rig.Platform.Removed);
    }

    [Theory]
    [InlineData(-60, ArtifactStatus.Restored)]
    [InlineData(-4, ArtifactStatus.Removed)]
    [InlineData(1, ArtifactStatus.Removed)]
    public async Task A_dataset_OSDU_created_before_the_unit_began_is_given_back_its_earlier_version_instead_of_removed(int createdMinutes, ArtifactStatus expected)
    {
        using var rig = new Rig();
        var files = rig.FilesOf(TraceId);
        var seeded = await SeedAsync(rig, Trace());
        var prior = rig.VersionOf(files);
        var source = SourceOf(rig.Platform.Records[files]);

        // Storage holds the dataset soft-deleted, so its read says it holds none, and the registration is declared as creating it.
        rig.Platform.Removed.Add(files);
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var ledger = rig.Ledger(DateTime.UtcNow);
        var work = ledger.Track(Update(Trace(), seeded));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var row = Assert.Single(ledger.Rows);
        Assert.Equal(ArtifactRoles.Record, row.Role);
        Assert.Equal(rig.VersionOf(files), row.Version);

        // What storage says of when OSDU created it decides: before the unit began (less five minutes for clocks), not the unit's.
        rig.Platform.Records[files]["createTime"] = ledger.Unit.StartedUtc.AddMinutes(createdMinutes).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var result = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));
        Assert.Equal(expected, result.Outcome);
        if (expected == ArtifactStatus.Restored)
        {
            Assert.Contains($"version {Text(prior)} written back", result.Note, StringComparison.Ordinal);
            Assert.DoesNotContain(files, rig.Platform.Removed);
            Assert.Equal(source, SourceOf(rig.Platform.Records[files]));
        }
        else
        {
            Assert.Contains(files, rig.Platform.Removed);
        }
    }

    [Fact]
    public async Task A_dataset_OSDU_created_before_the_unit_whose_registration_answer_was_lost_is_kept_and_nothing_is_written()
    {
        using var rig = new Rig();
        var files = rig.FilesOf(TraceId);
        var seeded = await SeedAsync(rig, Trace());
        rig.Platform.Removed.Add(files);
        rig.Faults.LoseAnswer(HttpMethod.Put, Register);
        var ledger = rig.Ledger(DateTime.UtcNow);
        var work = ledger.Track(Update(Trace(), seeded));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        rig.Platform.Records[files]["createTime"] = ledger.Unit.StartedUtc.AddDays(-30).ToString("O", CultureInfo.InvariantCulture);
        var versions = rig.Platform.History[files].Count;

        var mark = rig.Platform.Calls.Count;
        var result = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));
        Assert.Equal(ArtifactStatus.Kept, result.Outcome);
        Assert.Contains("before this delivery began", result.Note, StringComparison.Ordinal);
        Assert.Contains("which version it held before is not known", result.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(files, rig.Platform.Removed);
        Assert.Equal(versions, rig.Platform.History[files].Count);
        Assert.Equal(0, Count(rig.Platform, HttpMethod.Put, StorageRecords, mark));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_records_newer_work_does_not_keep_what_a_registration_wrote(bool update)
    {
        using var rig = new Rig();
        var files = rig.FilesOf(TraceId);
        var seeded = update ? await SeedAsync(rig, Trace()) : null;
        var source = update ? SourceOf(rig.Platform.Records[files]) : null;
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var ledger = rig.Ledger();
        var work = ledger.Track(seeded is null ? Work(Trace(), Files("L1")) : Update(Trace(), seeded));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        // A record of another kind names its dataset without a version, so newer work would take on the files of a delivery
        // that did not complete: the abandoned unit's registration is taken back even though the record is left to newer work.
        var result = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work, UndoReason.Abandoned, keepRecord: true)]));
        Assert.Equal(update ? ArtifactStatus.Restored : ArtifactStatus.Removed, result.Outcome);
        if (update)
        {
            Assert.Equal(source, SourceOf(rig.Platform.Records[files]));
        }
        else
        {
            Assert.Contains(files, rig.Platform.Removed);
        }
    }

    [Fact]
    public async Task A_resumed_try_registers_nothing_an_earlier_try_landed_and_declares_nothing_new()
    {
        using var rig = new Rig();
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.ServiceUnavailable);
        var ledger = rig.Ledger();
        Assert.False((await rig.DeliverAsync(ledger.Track(Work(Trace(), Files("L1"))))).Succeeded);
        var reported = ledger.Reports.Count;

        var mark = rig.Platform.Calls.Count;
        var resumed = await rig.DeliverAsync(ledger.Resume(Work(Trace(), Files("L1"))));
        Assert.True(resumed.Succeeded, resumed.Failure?.Message);
        Assert.Equal(["PUT " + StorageRecords], rig.Platform.Calls.Skip(mark).Select(c => c.Method + " " + c.Uri.AbsolutePath));
        Assert.Equal(["records"], ledger.Reports.Skip(reported).Select(r => r.Step));
        Assert.Empty(ledger.Reports.Skip(reported).SelectMany(r => r.Artifacts));
        Assert.Contains(resumed.Steps, s => s.Name == OsduDatasetProtocol.RegisterStep && s.Resumed);
        Assert.Contains(resumed.Steps, s => s.Name == "storage-files" && s.Resumed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_try_resumed_after_a_lost_registration_keeps_what_the_unit_first_declared(bool update)
    {
        using var rig = new Rig();
        var files = rig.FilesOf(TraceId);
        var seeded = update ? await SeedAsync(rig, Trace()) : null;
        long? prior = update ? rig.VersionOf(files) : null;
        var source = update ? SourceOf(rig.Platform.Records[files]) : null;

        rig.Faults.LoseAnswer(HttpMethod.Put, Register);
        var ledger = rig.Ledger();
        DeliveryWork Again() => seeded is null ? Work(Trace(), Files("L1")) : Update(Trace(), seeded);
        Assert.False((await rig.DeliverAsync(ledger.Track(Again()))).Succeeded);
        var landed = rig.VersionOf(files);

        // The next try reads storage again and sees the unit's own write: it declares a version over it, and registers again.
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var mark = rig.Platform.Calls.Count;
        var work = ledger.Resume(Again());
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        Assert.Equal(0, Count(rig.Platform, HttpMethod.Post, Instructions, mark));
        var again = ledger.Reports.Last(r => r.Step == OsduDatasetProtocol.RegisterIntentStep);
        var declared = Assert.Single(again.Artifacts);
        Assert.Equal(ArtifactRoles.Version, declared.Role);
        Assert.Equal(landed, declared.PriorVersion);

        // The ledger keeps what the unit first declared: the dataset it created, or the version it replaced first.
        var row = Assert.Single(ledger.Rows);
        Assert.Equal(update ? ArtifactRoles.Version : ArtifactRoles.Record, row.Role);
        Assert.Equal(prior, row.PriorVersion);
        Assert.Equal(rig.VersionOf(files), row.Version);

        mark = rig.Platform.Calls.Count;
        var result = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));
        if (update)
        {
            Assert.Equal(ArtifactStatus.Restored, result.Outcome);
            Assert.Contains($"version {Text(prior!.Value)} written back", result.Note, StringComparison.Ordinal);
            Assert.Equal(source, SourceOf(rig.Platform.Records[files]));
        }
        else
        {
            Assert.Equal(ArtifactStatus.Removed, result.Outcome);
            Assert.Contains(files, rig.Platform.Removed);
            Assert.Equal(0, Count(rig.Platform, HttpMethod.Put, StorageRecords, mark));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_second_undo_of_what_the_first_took_back_never_fails_and_changes_nothing_of_what_OSDU_serves(bool update)
    {
        using var rig = new Rig();
        var files = rig.FilesOf(TraceId);
        var seeded = update ? await SeedAsync(rig, Trace()) : null;
        var source = update ? SourceOf(rig.Platform.Records[files]) : null;
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var ledger = rig.Ledger();
        var work = ledger.Track(seeded is null ? Work(Trace(), Files("L1")) : Update(Trace(), seeded));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var undo = ledger.Undo(work);
        Assert.Equal(update ? ArtifactStatus.Restored : ArtifactStatus.Removed, Assert.Single(await rig.Protocol.UndoAsync([undo])).Outcome);

        // The ledger settles a restored artifact for good; a route handed it again (an undo whose settlement was lost) writes
        // the same version back once more, and a removed dataset is gone.
        var again = Assert.Single(await rig.Protocol.UndoAsync([undo]));
        if (update)
        {
            Assert.Equal(ArtifactStatus.Restored, again.Outcome);
            Assert.Equal(source, SourceOf(rig.Platform.Records[files]));
        }
        else
        {
            Assert.Equal(ArtifactStatus.Gone, again.Outcome);
            Assert.Contains(files, rig.Platform.Removed);
        }
    }

    [Theory]
    [InlineData("read")]
    [InlineData("remove")]
    public async Task An_undo_whose_osdu_call_fails_answers_failed_for_its_record_alone_and_the_other_record_still_settles(string failing)
    {
        using var rig = new Rig();
        const string other = "dev:work-product-component--SeismicTraceData:atomic-8";
        var failed = rig.FilesOf(TraceId);
        var settled = rig.FilesOf(other);
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest, times: 2);
        var first = rig.Ledger();
        var second = rig.Ledger();
        var firstWork = first.Track(Work(Trace(), Files("a")));
        var secondWork = second.Track(Work(Trace(other), Files("b")));
        Assert.False((await rig.DeliverAsync(firstWork)).Succeeded);
        Assert.False((await rig.DeliverAsync(secondWork)).Succeeded);

        if (failing == "read")
        {
            rig.Faults.Refuse(HttpMethod.Get, StorageRecords + "/" + failed, HttpStatusCode.ServiceUnavailable);
        }
        else
        {
            rig.Faults.Refuse(HttpMethod.Post, SoftDelete(failed), HttpStatusCode.InternalServerError);
        }

        var works = new[] { first.Undo(firstWork), second.Undo(secondWork) };
        var results = await rig.Protocol.UndoAsync(works);
        FileFamilyLedger.AssertAnsweredOnce(works, results);
        var refused = Assert.Single(results, r => r.Item.UnitId == first.Unit.Id);
        Assert.Equal(ArtifactStatus.Failed, refused.Outcome);
        Assert.Contains(failing == "read" ? "503" : "500", refused.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(results, r => r.Item.UnitId == second.Unit.Id).Outcome);
        Assert.DoesNotContain(failed, rig.Platform.Removed);
        Assert.Contains(settled, rig.Platform.Removed);
    }

    [Fact]
    public async Task What_the_dataset_route_never_makes_is_kept_and_every_item_is_answered_once()
    {
        using var rig = new Rig();
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Trace(), Files("L1")));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var owned = ledger.Undo(work);
        var foreign = owned with
        {
            Items =
            [
                .. owned.Items,
                new UndoItem(2_000_001, TargetArtifact.Created("file:0:abc", ArtifactRoles.Dataset, "dev:dataset--File.Generic:minted-1"), ledger.Unit.Id, ledger.Unit.StartedUtc),
                new UndoItem(2_000_002, TargetArtifact.Created("session", ArtifactRoles.Session, "session-1"), ledger.Unit.Id, ledger.Unit.StartedUtc),
            ],
        };

        var results = await rig.Protocol.UndoAsync([foreign]);
        FileFamilyLedger.AssertAnsweredOnce([foreign], results);
        Assert.Equal(ArtifactStatus.Removed, FileFamilyLedger.For(results, OsduDatasetProtocol.FilesDatasetSlot).Outcome);
        Assert.All(results.Where(r => r.Item.ArtifactId >= 2_000_001), r =>
        {
            Assert.Equal(ArtifactStatus.Kept, r.Outcome);
            Assert.Equal("the dataset route creates nothing of this kind", r.Note);
        });
        Assert.DoesNotContain("dev:dataset--File.Generic:minted-1", rig.Platform.Removed);
    }
}
