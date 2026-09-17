using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The composed routes against a fake platform (docs/interfaces-design.md section 5.7): a record's files registered
/// through the file service and the record with its bulk data written through the Wellbore DDMS (fileAndDdms), and a
/// manifest ingested before the bulk data goes to the DDMS (manifestAndDdms). Each part goes only when it changed or a
/// redelivery names it, every rewrite keeps the bulk link the DDMS holds, and every request keeps to the pinned contracts.
/// </summary>
public sealed class ComposedRouteTests
{
    private const string LogId = "opendes:work-product-component--WellLog:log-7";
    private const string LogKind = "osdu:wks:work-product-component--WellLog:1.2.0";
    private const string Ddms = FakeOsduPlatform.DdmsRoot + "/ddms/v3/welllogs";

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform)
        {
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                Secrets, TimeProvider.System, platform, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "opendes" });
        }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public void Dispose() => Runtime.Dispose();
    }

    /// <summary>A flow whose endpoint is the platform root, with the Wellbore DDMS under its ingress route.</summary>
    private static ProtocolOptions Options => new()
    {
        DdmsRoot = FakeOsduPlatform.DdmsRoot,
        WorkflowPollSeconds = 1,
        DatasetIndexWaitSeconds = 0,
    };

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
        JsonObject document, IReadOnlyList<WorkPayloadPart> parts, bool metadata = true, bool payload = true, long? existing = null,
        IReadOnlyDictionary<string, string>? state = null, IReadOnlySet<string>? forced = null)
        => new()
        {
            Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("composed-route", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = payload,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Parts = parts,
            ForcedParts = forced ?? new HashSet<string>(StringComparer.Ordinal),
        };

    /// <summary>The record's target state after its deliveries, as the ledger merges what each returned.</summary>
    private static Dictionary<string, string> Merge(params IReadOnlyDictionary<string, string>[] states)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            foreach (var (name, value) in state)
            {
                merged[name] = value;
            }
        }

        return merged;
    }

    private static List<string> Sent(FakeOsduPlatform platform, int from)
        => platform.Calls.Skip(from).Select(c => c.Method + " " + Uri.UnescapeDataString(c.Uri.AbsolutePath)).ToList();

    private static string? BulkLink(JsonObject record)
        => record["data"]?["ExtensionProperties"]?["wdms"]?["bulkURI"]?.GetValue<string>();

    private static string[] Datasets(JsonObject record)
        => record["data"]!["Datasets"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    /// <summary>
    /// What the ingestion workflow does with a manifest, sent inline or stored as a dataset: every record in it written
    /// through storage, which does not ask the DDMS about a bulk link.
    /// </summary>
    internal static void Ingest(FakeOsduPlatform platform, FakeOsduPlatform.Run run)
    {
        var manifest = run.Context["manifest"] switch
        {
            JsonObject inline => inline,
            JsonValue reference => StoredManifest(platform, reference.GetValue<string>()),
            _ => throw new InvalidOperationException("the execution context names no manifest"),
        };
        var data = manifest["Data"] as JsonObject;
        foreach (var section in new[] { manifest["ReferenceData"], manifest["MasterData"], data?["WorkProductComponents"], data?["Datasets"] })
        {
            foreach (var record in section as JsonArray ?? [])
            {
                platform.Put((JsonObject)record!.DeepClone());
            }
        }

        if (data?["WorkProduct"] is JsonObject workProduct)
        {
            platform.Put((JsonObject)workProduct.DeepClone());
        }
    }

    /// <summary>The manifest a dataset holds, read from where its file was staged.</summary>
    internal static JsonObject StoredManifest(FakeOsduPlatform platform, string datasetId)
    {
        var source = platform.Records[datasetId]["data"]!["DatasetProperties"]!["FileSourceInfo"]!["FileSource"]!.GetValue<string>();
        return JsonNode.Parse(platform.Staged[source])!.AsObject();
    }

    [Fact]
    public async Task Files_and_bulk_data_go_part_by_part_and_every_rewrite_keeps_the_bulk_link()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var protocol = new OsduFileAndDdmsProtocol(rig.Client, Options, Samples.Logger<OsduFileAndDdmsProtocol>());

        // A new record: its file uploaded and registered, the record written through its collection pointing at the
        // dataset, then its bulk data, and the version read back.
        var first = await protocol.DeliverAsync(Work(Log(), [LasFiles("f1"), Curves("b1")]));
        Assert.True(first.Succeeded, first.Failure?.Message);
        Assert.True(first.MetadataDelivered);
        Assert.True(first.PayloadDelivered);
        const string dataset = "opendes:dataset--File.Generic:minted-2";
        Assert.Equal(dataset, first.Returned[FileUploads.DatasetIdsValue]);
        Assert.Equal("1", first.Returned["files"]);
        Assert.Equal("f1", first.Returned[PayloadParts.StateKey("las")]);
        Assert.Equal("b1", first.Returned[PayloadParts.StateKey("curves")]);
        Assert.Equal(
            [
                "GET /api/file/v2/files/uploadURL",
                "PUT /staging/landing/l-1",
                "POST /api/file/v2/files/metadata",
                "POST " + Ddms,
                "POST " + Ddms + "/" + LogId + "/data",
                "GET " + Ddms + "/" + LogId,
            ],
            Sent(platform, 0));
        var stored = platform.Records[LogId];
        Assert.Equal([dataset + ":"], Datasets(stored));
        Assert.Equal(stored["version"]!.GetValue<long>(), first.TargetVersion);
        Assert.NotNull(BulkLink(stored));
        Assert.Equal("~VERSION INFORMATION", Encoding.UTF8.GetString(platform.Staged["/staging/landing/l-1"]));

        // The files go as files and the bulk data as parquet, from one flow.
        Assert.Equal(ProtocolOptions.DefaultFilesContentType, platform.Calls.Single(c => c.Uri.AbsolutePath == "/staging/landing/l-1").ContentType);
        Assert.Equal("application/x-parquet", platform.Calls.Single(c => c.Uri.AbsolutePath.EndsWith("/data", StringComparison.Ordinal)).ContentType);

        // New bulk data alone: no file moves and the record is not written, only the bulk data goes.
        var state = Merge(first.Returned);
        var mark = platform.Calls.Count;
        var second = await protocol.DeliverAsync(Work(Log(), [LasFiles("f1"), Curves("b2")], metadata: false, existing: first.TargetVersion, state: state));
        Assert.True(second.Succeeded, second.Failure?.Message);
        Assert.False(second.MetadataDelivered);
        Assert.True(second.PayloadDelivered);
        Assert.Equal("b2", second.Returned[PayloadParts.StateKey("curves")]);
        Assert.False(second.Returned.ContainsKey(PayloadParts.StateKey("las")));
        Assert.Equal(dataset, second.Returned[FileUploads.DatasetIdsValue]);
        Assert.Equal(["POST " + Ddms + "/" + LogId + "/data", "GET " + Ddms + "/" + LogId], Sent(platform, mark));
        Assert.Equal(platform.Records[LogId]["version"]!.GetValue<long>(), second.TargetVersion);

        // A redelivery naming the files: they are uploaded and registered again and the record is rewritten to point at
        // them, carrying the bulk link the DDMS holds, so the DDMS takes the write and the bulk data is not sent again.
        state = Merge(state, second.Returned);
        var link = BulkLink(platform.Records[LogId]);
        mark = platform.Calls.Count;
        var third = await protocol.DeliverAsync(Work(
            Log(), [LasFiles("f1", "~VERSION INFORMATION 2.0"), Curves("b2")], metadata: false, existing: second.TargetVersion, state: state,
            forced: new HashSet<string>(StringComparer.Ordinal) { PayloadParts.Files }));
        Assert.True(third.Succeeded, third.Failure?.Message);
        Assert.True(third.MetadataDelivered);
        const string redelivered = "opendes:dataset--File.Generic:minted-4";
        Assert.Equal(redelivered, third.Returned[FileUploads.DatasetIdsValue]);
        Assert.False(third.Returned.ContainsKey(PayloadParts.StateKey("curves")));
        Assert.Equal(
            [
                "GET /api/file/v2/files/uploadURL",
                "PUT /staging/landing/l-3",
                "POST /api/file/v2/files/metadata",
                "GET " + Ddms + "/" + LogId,
                "POST " + Ddms,
            ],
            Sent(platform, mark));
        Assert.Equal([redelivered + ":"], Datasets(platform.Records[LogId]));
        Assert.Equal(link, BulkLink(platform.Records[LogId]));
        Assert.Equal(0, platform.RefusedLinks);

        // A change to the record alone keeps the dataset list the files gave it and the bulk link.
        state = Merge(state, third.Returned);
        var renamed = await protocol.DeliverAsync(Work(Log("GR run, edited"), [], payload: false, existing: third.TargetVersion, state: state));
        Assert.True(renamed.Succeeded, renamed.Failure?.Message);
        Assert.False(renamed.PayloadDelivered);
        Assert.Equal("GR run, edited", platform.Records[LogId]["data"]!["Name"]!.GetValue<string>());
        Assert.Equal([redelivered + ":"], Datasets(platform.Records[LogId]));
        Assert.Equal(link, BulkLink(platform.Records[LogId]));
        Assert.Equal(0, platform.RefusedLinks);

        // Nothing changed: nothing is sent.
        mark = platform.Calls.Count;
        var same = await protocol.DeliverAsync(Work(Log(), [LasFiles("f1"), Curves("b2")], metadata: false, existing: renamed.TargetVersion, state: Merge(state, renamed.Returned)));
        Assert.Equal("every part is as OSDU holds it", same.Detail);
        Assert.Equal(renamed.TargetVersion, same.TargetVersion);
        Assert.Empty(Sent(platform, mark));

        // Removing everything purges the record with its bulk data through the DDMS, and the dataset its files are now.
        var removed = await protocol.DeleteAsync(LogId, RemovalScope.Everything, Merge(state, renamed.Returned));
        Assert.True(removed.Deleted);
        Assert.Contains("1 dataset record(s) and their files deleted", removed.Detail, StringComparison.Ordinal);
        Assert.Contains(redelivered, platform.Removed);
        Assert.Contains(platform.Calls, c => c.Method == HttpMethod.Delete && c.Uri.Query == "?purge=true");

        var probe = await protocol.ProbeAsync();
        Assert.True(probe.Reachable, probe.Detail);
        Assert.Contains("/api/file/v2/info", probe.Path, StringComparison.Ordinal);
        Assert.Contains(FakeOsduPlatform.DdmsRoot + "/about", probe.Path, StringComparison.Ordinal);

        Assert.Equal(
            [
                "core/file DELETE /v2/files/{id}/metadata",
                "core/file GET /v2/files/uploadURL",
                "core/file GET /v2/info",
                "core/file POST /v2/files/metadata",
                "wellbore-ddms DELETE /ddms/v3/welllogs/{record_id}",
                "wellbore-ddms GET /about",
                "wellbore-ddms GET /ddms/v3/welllogs/{record_id}",
                "wellbore-ddms POST /ddms/v3/welllogs",
                "wellbore-ddms POST /ddms/v3/welllogs/{record_id}/data",
            ],
            OsduContracts.AssertConform(platform.Calls, FakeOsduPlatform.ToSignedLocation, OsduContracts.File, OsduContracts.WellboreDdms));
    }

    [Fact]
    public async Task A_record_the_ddms_would_refuse_is_held_before_any_file_is_uploaded()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var protocol = new OsduFileAndDdmsProtocol(rig.Client, Options, Samples.Logger<OsduFileAndDdmsProtocol>());

        var described = FakeOsduPlatform.Record(LogId, LogKind, new JsonObject { ["Name"] = "GR run", ["Curves"] = new JsonArray(new JsonObject { ["CurveID"] = "MD" }) });
        var held = await Assert.ThrowsAsync<RecordHeldException>(() => protocol.DeliverAsync(Work(described, [LasFiles("f1"), Curves("b1")])));
        Assert.Contains("the bulk column(s) GR match no data.Curves[].CurveID", held.Message, StringComparison.Ordinal);

        // A record whose pending payload was planned for another route lists no parts, and waits for a redelivery.
        var unplanned = await Assert.ThrowsAsync<RecordHeldException>(() => protocol.DeliverAsync(Work(Log(), [])));
        Assert.Contains("lists no files part", unplanned.Message, StringComparison.Ordinal);

        // A files part the record carries no files for cannot be sent.
        var empty = await Assert.ThrowsAsync<RecordHeldException>(() => protocol.DeliverAsync(
            Work(Log(), [new WorkPayloadPart(PayloadParts.Files, "las", null, "none"), Curves("b1")])));
        Assert.Contains("payload 'las' names no files for the record", empty.Message, StringComparison.Ordinal);

        Assert.Empty(platform.Calls);
    }

    [Fact]
    public async Task The_files_of_a_composed_route_go_as_the_flow_names_them()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var protocol = new OsduFileAndDdmsProtocol(rig.Client, Options with { FilesContentType = "text/plain" }, Samples.Logger<OsduFileAndDdmsProtocol>());
        var outcome = await protocol.DeliverAsync(Work(Log(), [LasFiles("f1"), Curves("b1")]));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal("text/plain", platform.Calls.Single(c => c.Uri.AbsolutePath == "/staging/landing/l-1").ContentType);
        Assert.Equal("application/x-parquet", platform.Calls.Single(c => c.Uri.AbsolutePath.EndsWith("/data", StringComparison.Ordinal)).ContentType);
    }

    [Fact]
    public async Task A_manifest_writes_the_record_before_its_bulk_data_and_carries_the_bulk_link_on_every_rewrite()
    {
        var platform = new FakeOsduPlatform();
        platform.Register("Osdu_ingest", new FakeOsduPlatform.Script { Effect = Ingest });

        // The index lists a dataset once it is registered.
        platform.Search = (_, query) => platform.Records.Keys.Where(id => query?.Contains('"' + id + '"', StringComparison.Ordinal) == true).ToList();
        using var rig = new Rig(platform);
        var protocol = new OsduManifestAndDdmsProtocol(rig.Client, Options with { DatasetIndexWaitSeconds = 5 }, Samples.Logger<OsduManifestAndDdmsProtocol>());

        // A new record: its file registered, the manifest ingested, then its bulk data through the DDMS.
        var first = await protocol.DeliverAsync(Work(Log(), [LasFiles("f1"), Curves("b1")]));
        Assert.True(first.Succeeded, first.Failure?.Message);
        Assert.True(first.MetadataDelivered);
        Assert.True(first.PayloadDelivered);
        const string dataset = "opendes:dataset--File.Generic:minted-2";
        Assert.Equal(dataset, first.Returned[FileUploads.DatasetIdsValue]);
        Assert.Equal("f1", first.Returned[PayloadParts.StateKey("las")]);
        Assert.Equal("b1", first.Returned[PayloadParts.StateKey("curves")]);
        var run = Assert.Single(platform.Runs);
        Assert.Equal(run.RunId, first.Returned["runId"]);
        var manifested = run.Context["manifest"]!["Data"]!["WorkProductComponents"]![0]!.AsObject();
        Assert.Equal([dataset + ":"], Datasets(manifested));
        Assert.Null(BulkLink(manifested));
        var sent = Sent(platform, 0);
        Assert.True(
            sent.IndexOf("POST /api/workflow/v1/workflow/Osdu_ingest/workflowRun") < sent.IndexOf("POST " + Ddms + "/" + LogId + "/data"),
            "the bulk data went before the manifest wrote the record: " + string.Join(", ", sent));
        Assert.Contains("POST /api/search/v2/query", sent);
        Assert.Equal(ProtocolOptions.DefaultFilesContentType, platform.Calls.Single(c => c.Uri.AbsolutePath == "/staging/landing/l-1").ContentType);
        var link = BulkLink(platform.Records[LogId]);
        Assert.NotNull(link);
        Assert.Equal(platform.Records[LogId]["version"]!.GetValue<long>(), first.TargetVersion);

        // A change to the record: the manifest carries the bulk link storage holds, so ingestion does not drop it, and
        // the dataset list the files gave it. No bulk data goes.
        var state = Merge(first.Returned);
        var mark = platform.Calls.Count;
        var second = await protocol.DeliverAsync(Work(Log("GR run, edited"), [], payload: false, existing: first.TargetVersion, state: state));
        Assert.True(second.Succeeded, second.Failure?.Message);
        Assert.True(second.MetadataDelivered);
        Assert.False(second.PayloadDelivered);
        Assert.Equal(2, platform.Runs.Count);
        var rewritten = platform.Runs[1].Context["manifest"]!["Data"]!["WorkProductComponents"]![0]!.AsObject();
        Assert.Equal(link, BulkLink(rewritten));
        Assert.Equal([dataset + ":"], Datasets(rewritten));
        Assert.Equal(link, BulkLink(platform.Records[LogId]));
        Assert.Equal("GR run, edited", platform.Records[LogId]["data"]!["Name"]!.GetValue<string>());
        var read = platform.Calls.Skip(mark).First(c => c.Uri.AbsolutePath == "/api/storage/v2/query/records");
        Assert.Contains("data.ExtensionProperties", read.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Sent(platform, mark), c => c.EndsWith("/data", StringComparison.Ordinal));

        // New bulk data alone goes straight to the DDMS: the record is in storage, so no manifest runs.
        state = Merge(state, second.Returned);
        mark = platform.Calls.Count;
        var third = await protocol.DeliverAsync(Work(Log("GR run, edited"), [LasFiles("f1"), Curves("b2")], metadata: false, existing: second.TargetVersion, state: state));
        Assert.True(third.Succeeded, third.Failure?.Message);
        Assert.False(third.MetadataDelivered);
        Assert.True(third.PayloadDelivered);
        Assert.Equal(2, platform.Runs.Count);
        Assert.Equal(["POST " + Ddms + "/" + LogId + "/data", "GET " + Ddms + "/" + LogId], Sent(platform, mark));
        Assert.Equal("b2", third.Returned[PayloadParts.StateKey("curves")]);
        Assert.Equal(platform.Records[LogId]["version"]!.GetValue<long>(), third.TargetVersion);

        OsduContracts.AssertConform(
            platform.Calls, FakeOsduPlatform.ToSignedLocation,
            OsduContracts.File, OsduContracts.Workflow, OsduContracts.Storage, OsduContracts.Search, OsduContracts.WellboreDdms);
    }

    [Fact]
    public async Task A_failed_ingestion_sends_no_bulk_data_and_a_new_record_with_bulk_data_alone_goes_through_the_manifest()
    {
        var platform = new FakeOsduPlatform();
        platform.Register("Osdu_ingest", new FakeOsduPlatform.Script { Terminal = "failed" });
        using var rig = new Rig(platform);
        var protocol = new OsduManifestAndDdmsProtocol(rig.Client, Options, Samples.Logger<OsduManifestAndDdmsProtocol>());

        // No files part: the flow declares only bulk data, and a new record still needs the manifest to exist.
        var failed = await Assert.ThrowsAsync<DeliveryException>(() => protocol.DeliverAsync(Work(Log(), [Curves("b1")], metadata: false)));
        Assert.Contains("failed; the next try triggers a new run", failed.Message, StringComparison.Ordinal);
        Assert.Single(platform.Runs);
        Assert.DoesNotContain(platform.Calls, c => c.Uri.AbsolutePath.EndsWith("/data", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Calls, c => c.Uri.AbsolutePath.StartsWith("/api/file/", StringComparison.Ordinal));

        // The same record against a workflow that writes it: the manifest, then the bulk data.
        platform.Register("Osdu_ingest", new FakeOsduPlatform.Script { Effect = Ingest });
        var outcome = await protocol.DeliverAsync(Work(Log(), [Curves("b1")], metadata: false));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(2, platform.Runs.Count);
        Assert.True(platform.Bulk.ContainsKey(LogId));
        Assert.False(outcome.Returned.ContainsKey(FileUploads.DatasetIdsValue));

        // A record the DDMS would refuse is held before its manifest is sent.
        var described = FakeOsduPlatform.Record(LogId, LogKind, new JsonObject { ["Name"] = "GR run", ["Curves"] = new JsonArray(new JsonObject { ["CurveID"] = "MD" }) });
        var held = await Assert.ThrowsAsync<RecordHeldException>(() => protocol.DeliverAsync(Work(described, [Curves("b2")], existing: outcome.TargetVersion)));
        Assert.Contains("GR match no data.Curves[].CurveID", held.Message, StringComparison.Ordinal);
        Assert.Equal(2, platform.Runs.Count);

        // A work planned for another route has no bulk part to send.
        var unplanned = await Assert.ThrowsAsync<RecordHeldException>(() => protocol.DeliverAsync(Work(Log(), [LasFiles("f1")])));
        Assert.Contains("lists no bulk part", unplanned.Message, StringComparison.Ordinal);

        var probe = await protocol.ProbeAsync();
        Assert.True(probe.Reachable, probe.Detail);
        Assert.Contains(FakeOsduPlatform.DdmsRoot + "/about", probe.Path, StringComparison.Ordinal);
    }
}
