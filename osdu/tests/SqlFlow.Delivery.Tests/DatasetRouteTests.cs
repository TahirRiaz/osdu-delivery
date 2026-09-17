using System.Globalization;
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
/// The dataset route against a fake Dataset service (docs/interfaces-design.md section 5.6; osdu/specs/core/INTEGRATION.md
/// sections 2.5 and 3.3): a dataset record registered with its files under its own id, a file collection put where each
/// provider signs it, another record referring to one dataset of its files, a change to a record alone written through
/// storage, and every removal scope, all within the pinned Dataset and Storage contracts.
/// </summary>
public sealed class DatasetRouteTests
{
    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform, ProtocolOptions? options = null)
        {
            Platform = platform;
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                Secrets, TimeProvider.System, platform, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "opendes" });
            Options = options ?? new ProtocolOptions
            {
                PayloadContentType = "application/octet-stream",
                UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-blob-type"] = "BlockBlob" },
            };
            Protocol = new OsduDatasetProtocol(Client, Options, Samples.Logger<OsduDatasetProtocol>());
        }

        public FakeOsduPlatform Platform { get; }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public ProtocolOptions Options { get; }

        public OsduDatasetProtocol Protocol { get; }

        public void Dispose() => Runtime.Dispose();
    }

    private static DeliveryWork Work(JsonObject document, IPayloadSource? files, bool metadata = true, bool payload = true, long? existing = null, IReadOnlyDictionary<string, string>? state = null)
        => new()
        {
            Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("dataset-route", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = payload,
            Payload = files,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

    private static IReadOnlySet<string> AssertConform(FakeOsduPlatform platform)
        => OsduContracts.AssertConform(platform.Calls, FakeOsduPlatform.ToSignedLocation, OsduContracts.Dataset, OsduContracts.Storage, OsduContracts.Legal);

    [Fact]
    public async Task A_dataset_record_is_registered_with_its_one_file_under_its_own_id()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        const string id = "opendes:dataset--File.Generic:report-1";
        var outcome = await rig.Protocol.DeliverAsync(Work(FakeOsduPlatform.Record(id, "osdu:wks:dataset--File.Generic:1.0.0"), new MemoryFiles(("report.pdf", "%PDF-1.7"))));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.True(outcome.MetadataDelivered);
        Assert.True(outcome.PayloadDelivered);
        Assert.Equal(platform.Records[id]["version"]!.GetValue<long>(), outcome.TargetVersion);
        Assert.Equal("1", outcome.Returned["files"]);

        var registered = platform.Records[id];
        var source = registered["data"]!["DatasetProperties"]!["FileSourceInfo"]!;
        Assert.Equal("/staging/blob/f-1", source["FileSource"]!.GetValue<string>());
        Assert.Equal("report.pdf", source["Name"]!.GetValue<string>());
        Assert.Equal("8", source["FileSize"]!.GetValue<string>());
        Assert.Equal("%PDF-1.7", Encoding.UTF8.GetString(platform.Staged["/staging/blob/f-1"]));

        // The storage location is asked for the record's own entity type, and its signed location is kept nowhere.
        var instructions = Assert.Single(platform.Calls, c => c.Uri.AbsolutePath.EndsWith("/storageInstructions", StringComparison.Ordinal));
        Assert.Equal("?kindSubType=dataset--File.Generic", instructions.Uri.Query);
        Assert.DoesNotContain(outcome.Steps.SelectMany(s => s.Returned.Values), v => v.Contains("sig=", StringComparison.Ordinal));
        Assert.Contains(outcome.Steps, s => s.Name == OsduDatasetProtocol.RegisterStep && s.Returned["retrievable"] == "true");

        Assert.Equal(
            [
                "core/dataset POST /retrievalInstructions",
                "core/dataset POST /storageInstructions",
                "core/dataset PUT /registerDataset",
            ],
            AssertConform(platform));
    }

    [Fact]
    public async Task A_single_file_dataset_holds_one_file_and_a_type_no_dms_serves_is_held()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var two = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(
            Work(FakeOsduPlatform.Record("opendes:dataset--File.Generic:two", "osdu:wks:dataset--File.Generic:1.0.0"), new MemoryFiles(("a.txt", "a"), ("b.txt", "b")))));
        Assert.Contains("holds 2 files, and a dataset--File.Generic dataset holds one file", two.Message, StringComparison.Ordinal);
        Assert.Empty(platform.Calls);

        // A type no DMS serves is refused by the storage instructions with 400, which holds the record.
        var refused = new FakeHttpHandler().On(HttpMethod.Post, "/storageInstructions", System.Net.HttpStatusCode.BadRequest, """{"code":400,"reason":"Bad Request","message":"No DMS handler for resource type 'dataset--PhysicalMedia' is registered"}""");
        using var runtime = new HttpRuntime(new FlowReliability { Retry = new FlowRetry { Attempts = 1 } }, Secrets, TimeProvider.System, refused, allowLoopback: true);
        var client = new OsduHttpClient(runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None }, new Dictionary<string, string> { ["data-partition-id"] = "opendes" });
        var protocol = new OsduDatasetProtocol(client, rig.Options, Samples.Logger<OsduDatasetProtocol>());
        var held = await Assert.ThrowsAsync<RecordHeldException>(() => protocol.DeliverAsync(
            Work(FakeOsduPlatform.Record("opendes:dataset--PhysicalMedia:tape-1", "osdu:wks:dataset--PhysicalMedia:1.0.0"), new MemoryFiles(("tape.bin", "x")))));
        Assert.Contains("the dataset service has no storage for dataset--PhysicalMedia", held.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(refused.Calls, c => c.Uri.AbsolutePath.EndsWith("/registerDataset", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FakeOsduPlatform.Staging.Azure, "/d-1", "/staging/dfs/d-1/a.segy")]
    [InlineData(FakeOsduPlatform.Staging.Minio, "/c1/", "/staging/minio/c1/a.segy")]
    [InlineData(FakeOsduPlatform.Staging.S3, "/c1/", "/staging/s3/c1/a.segy")]
    [InlineData(FakeOsduPlatform.Staging.Google, "/c1/", "/staging/gcs/c1/a.segy")]
    public async Task A_file_collection_goes_under_the_directory_each_provider_signs(FakeOsduPlatform.Staging provider, string path, string staged)
    {
        var platform = new FakeOsduPlatform { Provider = provider };
        using var rig = new Rig(platform);
        const string id = "opendes:dataset--FileCollection.SEGY:survey-1";
        var outcome = await rig.Protocol.DeliverAsync(Work(FakeOsduPlatform.Record(id, "osdu:wks:dataset--FileCollection.SEGY:1.0.0"), new MemoryFiles(("a.segy", "trace-a"), ("b.segy", "trace-bb"))));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal("2", outcome.Returned["files"]);
        var properties = platform.Records[id]["data"]!["DatasetProperties"]!;
        Assert.Equal(path, properties["FileCollectionPath"]!.GetValue<string>());
        Assert.Equal(["a.segy", "b.segy"], properties["FileSourceInfos"]!.AsArray().Select(f => f!["FileSource"]!.GetValue<string>()));
        Assert.Equal(["7", "8"], properties["FileSourceInfos"]!.AsArray().Select(f => f!["FileSize"]!.GetValue<string>()));
        Assert.Equal("15", platform.Records[id]["data"]!["TotalSize"]!.GetValue<string>());
        Assert.True(platform.Staged.ContainsKey(staged), $"nothing was staged at {staged}; staged: {string.Join(", ", platform.Staged.Keys)}");
        if (provider == FakeOsduPlatform.Staging.Azure)
        {
            Assert.Equal("trace-a", Encoding.UTF8.GetString(platform.Staged[staged]));
        }
        else
        {
            Assert.Contains("trace-a", Encoding.UTF8.GetString(platform.Staged[staged]), StringComparison.Ordinal);
        }

        if (provider == FakeOsduPlatform.Staging.Google)
        {
            var upload = Assert.Single(platform.Calls, c => c.Uri.AbsolutePath.StartsWith("/upload/storage/v1/b/staging-bucket/o", StringComparison.Ordinal) && c.Uri.Query.Contains("a.segy", StringComparison.Ordinal));
            Assert.Equal("Bearer folder-token", upload.Headers["Authorization"]);
            Assert.False(upload.Headers.ContainsKey("data-partition-id"));
        }

        AssertConform(platform);
    }

    [Fact]
    public async Task A_large_file_goes_to_a_data_lake_directory_in_parts_appended_in_order()
    {
        var platform = new FakeOsduPlatform { Provider = FakeOsduPlatform.Staging.Azure };
        using var rig = new Rig(platform);
        var datasets = new DatasetService(rig.Client, rig.Options);
        var storage = await datasets.StorageAsync("dataset--FileCollection.Generic", CancellationToken.None);
        var files = new MemoryFiles(("big.bin", "0123456789abcdefghij"));
        var chunks = await files.ListChunksAsync();
        var staged = await DatasetCollections.UploadAsync(rig.Client, rig.Options, storage, files, chunks, ["big.bin"], CancellationToken.None, appendBytes: 7);

        Assert.Equal("/d-1", staged.CollectionPath);
        Assert.Equal("0123456789abcdefghij", Encoding.UTF8.GetString(platform.Staged["/staging/dfs/d-1/big.bin"]));
        var appends = platform.Calls.Where(c => c.Uri.Query.Contains("action=append", StringComparison.Ordinal)).ToList();
        Assert.Equal(["position=0", "position=7", "position=14"], appends.Select(a => a.Uri.Query.Split('&').Single(q => q.StartsWith("position=", StringComparison.Ordinal))));
        Assert.All(platform.Calls.Where(FakeOsduPlatform.ToSignedLocation), c => Assert.Equal("2021-06-08", c.Headers["x-ms-version"]));
    }

    [Theory]
    [InlineData("osdu:wks:dataset--FileCollection.SEGY:1.0.0", "opendes:dataset--FileCollection.SEGY:ibm-1", "a collection location")]
    [InlineData("osdu:wks:dataset--File.Generic:1.0.0", "opendes:dataset--File.Generic:ibm-2", "a file location")]
    public async Task A_location_with_credentials_for_an_unnamed_endpoint_holds_the_record(string kind, string id, string what)
    {
        var platform = new FakeOsduPlatform { Provider = FakeOsduPlatform.Staging.Ibm };
        using var rig = new Rig(platform);
        var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(FakeOsduPlatform.Record(id, kind), new MemoryFiles(("a.segy", "x")))));
        Assert.Contains(what + " this route cannot upload to (provider IBM, with connectionString, credentials, unsignedUrl)", held.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretAccessKey=b", held.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(platform.Calls, c => c.Uri.AbsolutePath.EndsWith("/registerDataset", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Calls, FakeOsduPlatform.ToSignedLocation);
    }

    [Fact]
    public async Task A_record_of_another_kind_refers_to_one_dataset_holding_its_files_and_new_files_land_on_it()
    {
        var platform = new FakeOsduPlatform();
        var options = new ProtocolOptions
        {
            DatasetKind = "osdu:wks:dataset--FileCollection.Generic:1.1.0",
            PayloadContentType = "application/octet-stream",
        };
        using var rig = new Rig(platform, options);
        const string id = "opendes:work-product-component--SeismicTraceData:st-7";
        var document = FakeOsduPlatform.Record(id, "osdu:wks:work-product-component--SeismicTraceData:1.3.0");
        var first = await rig.Protocol.DeliverAsync(Work(document, new MemoryFiles(("line1.segy", "L1"), ("line2.segy", "L2"))));

        Assert.True(first.Succeeded, first.Failure?.Message);
        const string dataset = "opendes:dataset--FileCollection.Generic:st-7-files";
        Assert.Equal(dataset, first.Returned[FileUploads.DatasetIdsValue]);
        Assert.Equal(dataset + ":", platform.Records[id]["data"]!["Datasets"]![0]!.GetValue<string>());
        Assert.Equal("opendes-public", platform.Records[dataset]["legal"]!["legaltags"]![0]!.GetValue<string>());
        Assert.Equal(platform.Records[id]["version"]!.GetValue<long>(), first.TargetVersion);
        var recordVersion = first.TargetVersion;

        // New files land on the same dataset, and the record, which already names it, is not written again.
        var writes = platform.Calls.Count(c => c.Method == HttpMethod.Put && c.Uri.AbsolutePath == "/api/storage/v2/records");
        var second = await rig.Protocol.DeliverAsync(Work(document, new MemoryFiles(("line1.segy", "L1b")), metadata: false, existing: recordVersion, state: first.Returned));
        Assert.True(second.Succeeded, second.Failure?.Message);
        Assert.Equal(writes, platform.Calls.Count(c => c.Method == HttpMethod.Put && c.Uri.AbsolutePath == "/api/storage/v2/records"));
        Assert.Equal(recordVersion, second.TargetVersion);
        Assert.Single(platform.Records[dataset]["data"]!["DatasetProperties"]!["FileSourceInfos"]!.AsArray());

        // Removing everything purges the record and its dataset; the reversible removal leaves the dataset.
        Assert.True((await rig.Protocol.DeleteAsync(id, RemovalScope.Record)).Deleted);
        Assert.DoesNotContain(dataset, platform.Removed);
        var purged = await rig.Protocol.DeleteAsync(id, RemovalScope.Everything);
        Assert.Contains($"its dataset {dataset} purged too", purged.Detail, StringComparison.Ordinal);
        Assert.Contains(dataset, platform.Purged);
        AssertConform(platform);
    }

    [Fact]
    public async Task A_change_to_a_dataset_record_alone_keeps_the_properties_osdu_holds()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        const string id = "opendes:dataset--File.Generic:report-2";
        var delivered = await rig.Protocol.DeliverAsync(Work(FakeOsduPlatform.Record(id, "osdu:wks:dataset--File.Generic:1.0.0"), new MemoryFiles(("report.pdf", "%PDF"))));
        Assert.True(delivered.Succeeded, delivered.Failure?.Message);
        var source = platform.Records[id]["data"]!["DatasetProperties"]!["FileSourceInfo"]!["FileSource"]!.GetValue<string>();

        var renamed = FakeOsduPlatform.Record(id, "osdu:wks:dataset--File.Generic:1.0.0", new JsonObject { ["Name"] = "Annual report" });
        var registrations = platform.Calls.Count(c => c.Uri.AbsolutePath.EndsWith("/registerDataset", StringComparison.Ordinal));
        var outcome = await rig.Protocol.DeliverAsync(Work(renamed, null, payload: false, existing: delivered.TargetVersion));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.False(outcome.PayloadDelivered);
        Assert.Equal(registrations, platform.Calls.Count(c => c.Uri.AbsolutePath.EndsWith("/registerDataset", StringComparison.Ordinal)));
        Assert.Equal("Annual report", platform.Records[id]["data"]!["Name"]!.GetValue<string>());
        Assert.Equal(source, platform.Records[id]["data"]!["DatasetProperties"]!["FileSourceInfo"]!["FileSource"]!.GetValue<string>());
        Assert.Equal(platform.Records[id]["version"]!.GetValue<long>(), outcome.TargetVersion);

        // A record storage holds without the properties cannot be rewritten to point at its files.
        platform.Put(FakeOsduPlatform.Record(id, "osdu:wks:dataset--File.Generic:1.0.0"));
        var bare = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(renamed, null, payload: false, existing: 3)));
        Assert.Contains("holds no DatasetProperties in storage", bare.Message, StringComparison.Ordinal);

        // A record storage does not hold cannot be rewritten either.
        var missing = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(
            Work(FakeOsduPlatform.Record("opendes:dataset--File.Generic:never", "osdu:wks:dataset--File.Generic:1.0.0"), null, payload: false, existing: 1)));
        Assert.Contains("is not in storage", missing.Message, StringComparison.Ordinal);
        AssertConform(platform);
    }

    [Fact]
    public async Task A_dataset_the_service_cannot_hand_out_fails_its_record_and_the_others_land()
    {
        var platform = new FakeOsduPlatform();
        platform.Unretrievable.Add("opendes:dataset--File.Generic:lost");
        using var rig = new Rig(platform);
        var works = new[] { "kept", "lost" }
            .Select(n => Work(FakeOsduPlatform.Record($"opendes:dataset--File.Generic:{n}", "osdu:wks:dataset--File.Generic:1.0.0"), new MemoryFiles(($"{n}.txt", n))))
            .ToList();
        var outcomes = await rig.Protocol.DeliverBatchAsync(works);
        Assert.True(outcomes[0].Succeeded, outcomes[0].Failure?.Message);
        Assert.Contains("answers no retrieval instructions for it", outcomes[1].Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registrations_go_twenty_to_a_request()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform, new ProtocolOptions { BatchSize = 500, UploadHeaders = new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob" } });
        Assert.Equal(20, rig.Protocol.MaxBatch);
        var works = Enumerable.Range(0, 25)
            .Select(i => Work(FakeOsduPlatform.Record($"opendes:dataset--File.Generic:many-{i.ToString(CultureInfo.InvariantCulture)}", "osdu:wks:dataset--File.Generic:1.0.0"), new MemoryFiles(("f.txt", "x"))))
            .ToList();
        var outcomes = await rig.Protocol.DeliverBatchAsync(works);
        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Failure?.Message));
        Assert.Equal(2, platform.Calls.Count(c => c.Uri.AbsolutePath.EndsWith("/registerDataset", StringComparison.Ordinal)));
        AssertConform(platform);
    }

    [Fact]
    public async Task A_dataset_record_is_removed_through_the_dataset_service_its_undelete_restores()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        const string id = "opendes:dataset--File.Generic:removable";
        platform.Put(FakeOsduPlatform.Record(id, "osdu:wks:dataset--File.Generic:1.0.0"));

        var removed = await rig.Protocol.DeleteAsync(id, RemovalScope.Record);
        Assert.True(removed.Deleted);
        Assert.Contains("undelete restores it", removed.Detail, StringComparison.Ordinal);
        var again = await rig.Protocol.DeleteAsync(id, RemovalScope.Record);
        Assert.True(again.AlreadyGone);

        Assert.True((await rig.Protocol.DeleteAsync(id, RemovalScope.History)).Deleted);
        Assert.True((await rig.Protocol.DeleteAsync(id, RemovalScope.Everything)).Deleted);
        Assert.Contains(id, platform.Purged);

        Assert.Equal(
            [
                "core/dataset POST /metadataRecord/{id}/softDelete",
                "core/storage DELETE /records/{id}",
                "core/storage DELETE /records/{id}/versions",
            ],
            AssertConform(platform));
        Assert.True((await rig.Protocol.ProbeAsync()).Reachable);
    }
}
