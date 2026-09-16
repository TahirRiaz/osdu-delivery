using System.Globalization;
using System.Net;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Every request the delivery protocols send, checked against the pinned OSDU contract of the service it goes to: a
/// delivery, a resumed delivery, a verification, a probe and every removal scope, for each protocol.
/// </summary>
public sealed class ProtocolContractTests
{
    private const string RecordId = "dev:work-product-component--WellLog:contract-1";

    private const string Document = """
        {
          "id": "dev:work-product-component--WellLog:contract-1",
          "kind": "osdu:wks:work-product-component--WellLog:1.4.0",
          "acl": { "viewers": ["data.default.viewers@dev.example.com"], "owners": ["data.default.owners@dev.example.com"] },
          "legal": { "legaltags": ["dev-public"], "otherRelevantDataCountries": ["NO"] },
          "data": { "Name": "GR run 1", "WellboreID": "dev:master-data--Wellbore:wb-1:", "Curves": [{ "CurveID": "MD" }, { "CurveID": "GR" }], "ReferenceCurveID": "MD" }
        }
        """;

    private static (OsduHttpClient Client, HttpRuntime Runtime) Client(FakeHttpHandler handler, string endpoint = "http://localhost/osdu")
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, endpoint, new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (client, runtime);
    }

    private static DeliveryWork Work(bool metadata, bool payload, IPayloadSource? source = null, long? existing = null) => new()
    {
        Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("contract", [RecordId]),
        TargetId = RecordId,
        Document = TestSchema.Doc(Document),
        DeliverMetadata = metadata,
        DeliverPayload = payload,
        Payload = source ?? new ParquetChunks(1),
        ExistingVersion = existing,
    };

    private static RecordRemoval Removal(string targetId)
        => new(SqlFlow.Delivery.Identity.DeliveryKey.Derive("contract", [targetId]), targetId, new Dictionary<string, string>(StringComparer.Ordinal));

    [Fact]
    public async Task Storage_writes_reads_and_removals_keep_to_the_storage_contract()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/records/" + RecordId, HttpStatusCode.OK, """{"id":"dev:work-product-component--WellLog:contract-1","version":7,"data":{"Datasets":["dev:dataset--File.Generic:ds-1:"]}}""")
            .On(HttpMethod.Put, "/records", HttpStatusCode.Created, """{"recordIdVersions":["dev:work-product-component--WellLog:contract-1:8"]}""")
            .On(HttpMethod.Post, "/query/records", HttpStatusCode.OK, """{"records":[{"id":"dev:work-product-component--WellLog:contract-1","version":8}]}""")
            .On(HttpMethod.Post, RecordId + ":delete", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Post, "/records/delete", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, "/versions", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, "/records/" + RecordId, HttpStatusCode.NoContent, null)
            .On(HttpMethod.Get, "/info", HttpStatusCode.OK, "{}");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions { PreserveDataKeys = ["Datasets"], SkipDuplicates = true });
            Assert.True((await protocol.DeliverAsync(Work(true, false, existing: 7))).Succeeded);
            await protocol.VerifyAsync(RecordId, 8);
            await ((IDeliveryProtocol)protocol).VerifyBatchAsync([new VerifyRequest(RecordId, 8)]);
            Assert.True((await protocol.ProbeAsync()).Reachable);
            Assert.True((await protocol.DeleteAsync(RecordId, RemovalScope.Record)).Deleted);
            Assert.True((await protocol.DeleteAsync(RecordId, RemovalScope.History)).Deleted);
            Assert.True((await protocol.DeleteAsync(RecordId, RemovalScope.Everything)).Deleted);
            var removed = await protocol.DeleteBatchAsync([Removal(RecordId), Removal(RecordId + "-2")], RemovalScope.Record);
            Assert.All(removed, r => Assert.True(r.Outcome?.Deleted == true, r.Failure?.Message));
        }

        Assert.Equal(
            [
                "core/storage DELETE /records/{id}",
                "core/storage DELETE /records/{id}/versions",
                "core/storage GET /info",
                "core/storage GET /records/{id}",
                "core/storage POST /query/records",
                "core/storage POST /records/delete",
                "core/storage POST /records/{id}:delete",
                "core/storage PUT /records",
            ],
            OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Storage));
    }

    [Fact]
    public async Task File_deliveries_keep_to_the_file_search_and_storage_contracts()
    {
        var handler = FileService()
            .On(HttpMethod.Put, "/records", HttpStatusCode.Created, """{"recordIdVersions":["dev:work-product-component--WellLog:contract-1:3"]}""")
            .On(HttpMethod.Delete, "/metadata", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, "/records/" + RecordId, HttpStatusCode.NoContent, null)
            .On(HttpMethod.Get, "/file/v2/info", HttpStatusCode.OK, "{}");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduFileProtocol(client, new ProtocolOptions { UploadUrlExpiry = "12H", PayloadContentType = "application/octet-stream" });
            var outcome = await protocol.DeliverAsync(Work(true, true, new TextChunks(2)));
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            Assert.True((await protocol.ProbeAsync()).Reachable);
            var state = new Dictionary<string, string>(StringComparer.Ordinal) { ["datasetIds"] = "dev:dataset--File.Generic:ds-0" };
            Assert.True((await protocol.DeleteAsync(RecordId, RemovalScope.Everything, state)).Deleted);
        }

        Assert.Equal(
            [
                "core/file DELETE /v2/files/{id}/metadata",
                "core/file GET /v2/files/uploadURL",
                "core/file GET /v2/info",
                "core/file POST /v2/files/metadata",
                "core/storage DELETE /records/{id}",
                "core/storage PUT /records",
            ],
            OsduContracts.AssertConform(handler.Calls, LandingZone, OsduContracts.File, OsduContracts.Search, OsduContracts.Storage));
    }

    [Fact]
    public async Task Manifest_deliveries_keep_to_the_file_search_workflow_and_storage_contracts()
    {
        var polls = 0;
        var handler = FileService()
            .On(HttpMethod.Post, "/workflow/Osdu_ingest/workflowRun", HttpStatusCode.OK, """{"workflowId":"Osdu_ingest","status":"submitted"}""")
            .OnMatch(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("/workflowRun/", StringComparison.Ordinal),
                _ => FakeHttpHandler.Json(HttpStatusCode.OK, ++polls == 1 ? """{"status":"running"}""" : """{"workflowId":"Osdu_ingest","status":"finished"}"""))
            .On(HttpMethod.Post, "/query/records", hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0
                ? """{"records":[],"invalidRecords":["dev:work-product-component--WellLog:contract-1"]}"""
                : """{"records":[{"id":"dev:work-product-component--WellLog:contract-1","version":5}]}"""));
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduManifestProtocol(client, new ProtocolOptions { WorkflowPollSeconds = 1 }, Samples.Logger<OsduManifestProtocol>());
            var outcomes = await protocol.DeliverBatchAsync([Work(true, true, new TextChunks(1))]);
            Assert.True(outcomes[0].Succeeded, outcomes[0].Failure?.Message);
        }

        Assert.Equal(
            [
                "core/file GET /v2/files/uploadURL",
                "core/file POST /v2/files/metadata",
                "core/search POST /query",
                "core/storage POST /query/records",
                "core/workflow GET /v1/workflow/{workflow_name}/workflowRun/{runId}",
                "core/workflow POST /v1/workflow/{workflow_name}/workflowRun",
            ],
            OsduContracts.AssertConform(handler.Calls, LandingZone, OsduContracts.File, OsduContracts.Search, OsduContracts.Workflow, OsduContracts.Storage));
    }

    [Fact]
    public async Task Wellbore_ddms_deliveries_keep_to_the_wellbore_ddms_and_storage_contracts()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/ddms/v3/welllogs", HttpStatusCode.OK, """{"recordCount":1,"recordIds":["dev:work-product-component--WellLog:contract-1"],"recordIdVersions":["dev:work-product-component--WellLog:contract-1:10"]}""")
            .On(HttpMethod.Post, "/welllogs/" + RecordId + "/data", HttpStatusCode.OK, "{}")
            .OnMatch(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/data", StringComparison.Ordinal),
                _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"numberOfRows":3,"columns":["MD","GR"]}"""))
            .On(HttpMethod.Post, "/sessions", HttpStatusCode.OK, """{"id":"sess-1"}""")
            .On(HttpMethod.Post, "/sessions/sess-1/data", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Patch, "/sessions/sess-1", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Get, "/welllogs/" + RecordId, HttpStatusCode.OK, """{"id":"dev:work-product-component--WellLog:contract-1","version":11}""")
            .On(HttpMethod.Delete, "/welllogs/" + RecordId, HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, "/versions", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Get, "/about", HttpStatusCode.OK, "{}");
        var (client, runtime) = Client(handler, "http://localhost");
        using (runtime)
        {
            var options = new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms" };
            var protocol = new OsduWellLogProtocol(client, options, Samples.Logger<OsduWellLogProtocol>());
            Assert.True((await protocol.DeliverAsync(Work(true, true, new ParquetChunks(1)))).Succeeded);
            Assert.True((await protocol.DeliverAsync(Work(false, true, new ParquetChunks(3)))).Succeeded);
            await protocol.VerifyAsync(RecordId, 11);
            Assert.True((await protocol.ProbeAsync()).Reachable);
            Assert.True((await protocol.DeleteAsync(RecordId, RemovalScope.Record)).Deleted);
            Assert.True((await protocol.DeleteAsync(RecordId, RemovalScope.History)).Deleted);
            Assert.True((await protocol.DeleteAsync(RecordId, RemovalScope.Everything)).Deleted);
        }

        Assert.Equal(
            [
                "core/storage DELETE /records/{id}/versions",
                "wellbore-ddms DELETE /ddms/v3/welllogs/{record_id}",
                "wellbore-ddms GET /about",
                "wellbore-ddms GET /ddms/v3/welllogs/{record_id}",
                "wellbore-ddms GET /ddms/v3/welllogs/{record_id}/data",
                "wellbore-ddms PATCH /ddms/v3/welllogs/{record_id}/sessions/{session_id}",
                "wellbore-ddms POST /ddms/v3/welllogs",
                "wellbore-ddms POST /ddms/v3/welllogs/{record_id}/data",
                "wellbore-ddms POST /ddms/v3/welllogs/{record_id}/sessions",
                "wellbore-ddms POST /ddms/v3/welllogs/{record_id}/sessions/{session_id}/data",
            ],
            OsduContracts.AssertConform(handler.Calls, null, OsduContracts.WellboreDdms, OsduContracts.Storage));
    }

    [Fact]
    public async Task Legal_tag_checks_keep_to_the_legal_contract()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/legaltags:validate", HttpStatusCode.OK, """{"invalidLegalTags":[]}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            Assert.Empty(await new LegalTagValidator(client).InvalidAsync(["dev-public", "dev-private"]));
        }

        Assert.Equal(["core/legal POST /legaltags:validate"], OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Legal));
    }

    /// <summary>A file service that hands out landing-zone URLs and registers every file, and a search index that lists them.</summary>
    private static FakeHttpHandler FileService() => new FakeHttpHandler()
        .On(HttpMethod.Get, "/files/uploadURL", hit => FakeHttpHandler.Json(HttpStatusCode.OK, UploadLocation(hit)))
        .OnMatch(r => LandingZone(r.Method, r.RequestUri!), _ => FakeHttpHandler.Json(HttpStatusCode.Created, null))
        .On(HttpMethod.Post, "/files/metadata", hit => FakeHttpHandler.Json(HttpStatusCode.Created, "{\"id\":\"dev:dataset--File.Generic:ds-" + hit.ToString(CultureInfo.InvariantCulture) + "\"}"))
        .On(HttpMethod.Post, "/search/v2/query", hit => FakeHttpHandler.Json(HttpStatusCode.OK, """{"results":[{"id":"dev:dataset--File.Generic:ds-0"},{"id":"dev:dataset--File.Generic:ds-1"}],"totalCount":2}"""));

    private static string UploadLocation(int hit)
    {
        var n = hit.ToString(CultureInfo.InvariantCulture);
        return "{\"FileID\":\"file-" + n + "\",\"Location\":{\"SignedURL\":\"http://localhost/landing/blob-" + n + "?sig=signature\",\"FileSource\":\"/landing/blob-" + n + "\"}}";
    }

    /// <summary>The signed landing-zone upload goes to blob storage, which no OSDU contract describes.</summary>
    private static bool LandingZone(FakeHttpHandler.Request request) => LandingZone(request.Method, request.Uri);

    private static bool LandingZone(HttpMethod method, Uri uri)
        => method == HttpMethod.Put && uri.AbsolutePath.StartsWith("/landing/", StringComparison.Ordinal);

    /// <summary>Text files, for the protocols that send files as they are.</summary>
    private sealed class TextChunks(int chunks) : IPayloadSource
    {
        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>(Enumerable.Range(0, chunks).Select(i => new PayloadFile(i, $"mem://files/report_{i}.txt", 7)).ToList());

        public Task<Stream> OpenAsync(PayloadFile chunk, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("chunk-" + chunk.Index)));
    }

    /// <summary>Parquet chunks of a two-curve log whose rows continue from chunk to chunk, as a correct prepare writes them.</summary>
    private sealed class ParquetChunks(int chunks) : IPayloadSource
    {
        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>(Enumerable.Range(0, chunks).Select(i => new PayloadFile(i, $"mem://chunk_{i}.parquet", Bytes(i).Length)).ToList());

        public Task<Stream> OpenAsync(PayloadFile chunk, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(Bytes(chunk.Index), writable: false));

        private static byte[] Bytes(int index)
        {
            var culture = CultureInfo.InvariantCulture;
            var rows = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["MD"] = (double)index, ["GR"] = 10.0 + index },
            };
            var metadata = new Dictionary<string, string>
            {
                [ParquetFiles.PandasMetadataKey] = $$"""{"index_columns": [{"kind": "range", "name": null, "start": {{index.ToString(culture)}}, "stop": {{(index + 1).ToString(culture)}}, "step": 1}]}""",
            };
            using var buffer = new MemoryStream();
            ParquetFiles.WriteAsync(buffer, [("MD", typeof(double)), ("GR", typeof(double))], rows, metadata).GetAwaiter().GetResult();
            return buffer.ToArray();
        }
    }
}
