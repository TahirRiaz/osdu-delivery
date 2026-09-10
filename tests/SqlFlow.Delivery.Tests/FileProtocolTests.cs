using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>The file and manifest protocols against a fake file service, landing zone, storage service and workflow service.</summary>
public class FileProtocolTests
{
    private const string RecordId = "dev:work-product-component--WellLog:abc";
    private const string OtherId = "dev:work-product-component--WellLog:def";
    private const string Document = """{"id":"dev:work-product-component--WellLog:abc","kind":"dev:wks:work-product-component--WellLog:1.4.0","acl":{"viewers":["data.default.viewers@dev.example.com"],"owners":["data.default.owners@dev.example.com"]},"legal":{"legaltags":["dev-public"],"otherRelevantDataCountries":["NO"]},"data":{"Name":"n"}}""";

    private static (OsduHttpClient Client, HttpRuntime Runtime, TestClock Clock) Client(FakeHttpHandler handler)
    {
        var clock = new TestClock();
        var runtime = new HttpRuntime(new FlowReliability { Retry = new FlowRetry { Attempts = 2, BaseDelayMs = 1, MaxDelayMs = 1 } }, new SecretResolver([new EnvSecretProvider()]), clock, handler, allowLoopback: true);
        var client = new OsduHttpClient(runtime, "http://localhost/osdu", new TargetAuth { Type = TargetAuthType.None }, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (client, runtime, clock);
    }

    private static DeliveryWork Work(
        bool metadata,
        bool payload,
        int chunks,
        string id = RecordId,
        long? existing = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? completed = null,
        IReadOnlyDictionary<string, string>? targetState = null,
        List<string>? reported = null)
    {
        var document = TestSchema.Doc(Document);
        document["id"] = id;
        return new DeliveryWork
        {
            Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("test", [id]),
            TargetId = id,
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = payload,
            Payload = new MemoryPayload(chunks),
            ExistingVersion = existing,
            CompletedSteps = completed ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
            TargetState = targetState ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StepCompleted = reported is null
                ? null
                : (step, values, _) =>
                {
                    reported.Add(step + "=" + string.Join(";", values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key + ":" + v.Value)));
                    return Task.CompletedTask;
                },
        };
    }

    private static string UploadLocation(int hit)
    {
        var n = hit.ToString(CultureInfo.InvariantCulture);
        return "{\"FileID\":\"file-" + n + "\",\"Location\":{\"SignedURL\":\"http://localhost/landing/blob-" + n + "?sig=SECRET\",\"FileSource\":\"/landing/blob-" + n + "\"}}";
    }

    private static bool LandingUpload(HttpRequestMessage request)
        => request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.StartsWith("/landing/", StringComparison.Ordinal);

    private static bool RunStatus(HttpRequestMessage request, string runId)
        => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/workflowRun/" + runId, StringComparison.Ordinal);

    private static bool AnyRunStatus(HttpRequestMessage request)
        => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/workflowRun/", StringComparison.Ordinal);

    private static IEnumerable<string> Names(IEnumerable<string> reported) => reported.Select(r => r[..r.IndexOf('=', StringComparison.Ordinal)]);

    [Fact]
    public async Task File_protocol_uploads_registers_then_writes_the_record_with_its_datasets()
    {
        var reported = new List<string>();
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/files/uploadURL", hit => FakeHttpHandler.Json(HttpStatusCode.OK, UploadLocation(hit)))
            .OnMatch(LandingUpload, _ => FakeHttpHandler.Json(HttpStatusCode.Created, null))
            .On(HttpMethod.Post, "/files/metadata", hit => FakeHttpHandler.Json(HttpStatusCode.Created, "{\"id\":\"dev:dataset--File.Generic:ds-" + hit.ToString(CultureInfo.InvariantCulture) + "\"}"))
            .On(HttpMethod.Put, "/records", HttpStatusCode.Created, """{"recordIdVersions":["dev:work-product-component--WellLog:abc:12"]}""");
        var (client, runtime, _) = Client(handler);
        using (runtime)
        {
            var options = new ProtocolOptions
            {
                UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-blob-type"] = "BlockBlob" },
                UploadUrlExpiry = "12H",
                PayloadContentType = "application/octet-stream",
            };
            var protocol = new OsduFileProtocol(client, options);
            var outcome = await protocol.DeliverAsync(Work(true, true, 2, reported: reported));
            Assert.True(outcome.Succeeded);
            Assert.Equal(12, outcome.TargetVersion);
            Assert.Equal(2, outcome.ChunksSent);
            Assert.True(outcome.MetadataDelivered);
            Assert.True(outcome.PayloadDelivered);
            Assert.Equal("dev:dataset--File.Generic:ds-0,dev:dataset--File.Generic:ds-1", outcome.Returned["datasetIds"]);
            Assert.Equal("12", outcome.Returned["version"]);
            Assert.Equal("2", outcome.Returned["files"]);
            Assert.Equal(["upload-0", "upload-1", "register-0", "register-1", "records"], outcome.Steps.Select(s => s.Name));
            Assert.Equal(["upload-0", "upload-1", "register-0", "register-1", "records"], Names(reported));
            Assert.Contains("fileSource:/landing/blob-0", reported[0], StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET", string.Join("\n", reported), StringComparison.Ordinal);

            Assert.Equal(7, handler.Calls.Count);
            var location = handler.Calls[0];
            Assert.Equal("expiryTime=12H", location.Uri.Query.TrimStart('?'));
            Assert.Equal("dev", location.Headers["data-partition-id"]);
            var upload = handler.Calls[1];
            Assert.Equal(HttpMethod.Put, upload.Method);
            Assert.Equal("/landing/blob-0", upload.Uri.AbsolutePath);
            Assert.Equal("chunk-0", upload.Body);
            Assert.Equal("application/octet-stream", upload.ContentType);
            Assert.Equal("BlockBlob", upload.Headers["x-ms-blob-type"]);
            Assert.False(upload.Headers.ContainsKey("data-partition-id"));
            Assert.Equal("/landing/blob-1", handler.Calls[3].Uri.AbsolutePath);
            var register = JsonNode.Parse(handler.Calls[4].Body!)!.AsObject();
            Assert.Equal("osdu:wks:dataset--File.Generic:1.0.0", register["kind"]!.GetValue<string>());
            Assert.Equal("dev-public", register["legal"]!["legaltags"]![0]!.GetValue<string>());
            Assert.Equal("data.default.owners@dev.example.com", register["acl"]!["owners"]![0]!.GetValue<string>());
            var info = register["data"]!["DatasetProperties"]!["FileSourceInfo"]!.AsObject();
            Assert.Equal("/landing/blob-0", info["FileSource"]!.GetValue<string>());
            Assert.Equal("curve_0.parquet", info["Name"]!.GetValue<string>());
            Assert.Equal("7", info["FileSize"]!.GetValue<string>());
            Assert.Null(register["id"]);
            var record = JsonNode.Parse(handler.Calls[6].Body!)!.AsArray();
            Assert.Equal(HttpMethod.Put, handler.Calls[6].Method);
            Assert.Equal(["dev:dataset--File.Generic:ds-0:", "dev:dataset--File.Generic:ds-1:"], record[0]!["data"]!["Datasets"]!.AsArray().Select(n => n!.GetValue<string>()));
        }
    }

    [Fact]
    public void A_record_references_its_datasets_in_the_form_the_schemas_require_without_repeating_a_rendered_one()
    {
        // Work product component schemas take dataset references: the id, a colon and an optional version. Manifest
        // ingestion validates that pattern and drops a record that breaks it while still creating its datasets.
        var document = TestSchema.Doc("""{"data":{"Datasets":["dev:dataset--File.Generic:kept:","dev:dataset--File.Generic:pinned:7"]}}""");
        FileUploads.SetDatasets(document, "Datasets", ["dev:dataset--File.Generic:kept", "dev:dataset--File.Generic:new"]);
        var datasets = document["data"]!["Datasets"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["dev:dataset--File.Generic:kept:", "dev:dataset--File.Generic:pinned:7", "dev:dataset--File.Generic:new:"], datasets);
        Assert.All(datasets, d => Assert.Matches(@"^[\w\-\.]+:dataset\-\-[\w\-\.]+:[\w\-\.\:\%]+:[0-9]*$", d));
        Assert.Equal("dev:dataset--File.Generic:x:", FileUploads.DatasetReference("dev:dataset--File.Generic:x"));
        Assert.Equal("dev:dataset--File.Generic:x:", FileUploads.DatasetReference("dev:dataset--File.Generic:x:"));
    }

    [Fact]
    public async Task File_protocol_resumes_past_the_files_an_earlier_try_uploaded_and_registered()
    {
        var reported = new List<string>();
        var completed = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["upload-0"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["fileSource"] = "/landing/old-0", ["fileId"] = "file-old", ["name"] = "curve_0.parquet", ["size"] = "7" },
            ["register-0"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["datasetId"] = "dev:dataset--File.Generic:old-0", ["fileSource"] = "/landing/old-0" },
        };
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/files/uploadURL", hit => FakeHttpHandler.Json(HttpStatusCode.OK, UploadLocation(hit)))
            .OnMatch(LandingUpload, _ => FakeHttpHandler.Json(HttpStatusCode.Created, null))
            .On(HttpMethod.Post, "/files/metadata", hit => FakeHttpHandler.Json(HttpStatusCode.Created, "{\"id\":\"dev:dataset--File.Generic:ds-" + hit.ToString(CultureInfo.InvariantCulture) + "\"}"))
            .On(HttpMethod.Put, "/records", HttpStatusCode.Created, """{"recordIdVersions":["dev:work-product-component--WellLog:abc:13"]}""");
        var (client, runtime, _) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduFileProtocol(client, new ProtocolOptions());
            var outcome = await protocol.DeliverAsync(Work(true, true, 2, completed: completed, reported: reported));
            Assert.True(outcome.Succeeded);
            Assert.Equal(["upload-0", "upload-1", "register-0", "register-1", "records"], outcome.Steps.Select(s => s.Name));
            Assert.True(outcome.Steps[0].Resumed);
            Assert.False(outcome.Steps[1].Resumed);
            Assert.True(outcome.Steps[2].Resumed);
            Assert.False(outcome.Steps[3].Resumed);
            Assert.Equal(["upload-1", "register-1", "records"], Names(reported));
            Assert.Equal(4, handler.Calls.Count);
            Assert.Equal("dev:dataset--File.Generic:old-0,dev:dataset--File.Generic:ds-0", outcome.Returned["datasetIds"]);
            var record = JsonNode.Parse(handler.Calls[3].Body!)!.AsArray();
            Assert.Equal(["dev:dataset--File.Generic:old-0:", "dev:dataset--File.Generic:ds-0:"], record[0]!["data"]!["Datasets"]!.AsArray().Select(n => n!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task File_protocol_metadata_only_change_keeps_the_datasets_and_purge_removes_them()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Put, "/records", HttpStatusCode.Created, """{"recordIdVersions":["dev:work-product-component--WellLog:abc:14"]}""")
            .On(HttpMethod.Post, ":abc:delete", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, ":abc", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, "/files/ds-a/metadata", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, "/files/ds-b/metadata", HttpStatusCode.NotFound, null);
        var (client, runtime, _) = Client(handler);
        using (runtime)
        {
            var state = new Dictionary<string, string>(StringComparer.Ordinal) { ["datasetIds"] = "ds-a,ds-b" };
            var protocol = new OsduFileProtocol(client, new ProtocolOptions());
            var outcome = await protocol.DeliverAsync(Work(true, false, 0, existing: 13, targetState: state));
            Assert.True(outcome.Succeeded);
            Assert.Equal(14, outcome.TargetVersion);
            Assert.False(outcome.PayloadDelivered);
            Assert.Equal("ds-a,ds-b", outcome.Returned["datasetIds"]);
            Assert.Single(handler.Calls);
            var record = JsonNode.Parse(handler.Calls[0].Body!)!.AsArray();
            Assert.Equal(["ds-a:", "ds-b:"], record[0]!["data"]!["Datasets"]!.AsArray().Select(n => n!.GetValue<string>()));

            var logical = await protocol.DeleteAsync(RecordId, RemovalScope.Record, state);
            Assert.True(logical.Deleted);
            Assert.Equal(2, handler.Calls.Count);
            Assert.EndsWith(":delete", handler.Calls[1].Uri.AbsolutePath, StringComparison.Ordinal);

            var purged = await protocol.DeleteAsync(RecordId, RemovalScope.Everything, state);
            Assert.True(purged.Deleted);
            Assert.Contains("1 dataset record(s)", purged.Detail, StringComparison.Ordinal);
            Assert.Equal(5, handler.Calls.Count);
            Assert.Equal(HttpMethod.Delete, handler.Calls[2].Method);
            Assert.EndsWith("/files/ds-a/metadata", handler.Calls[3].Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.EndsWith("/files/ds-b/metadata", handler.Calls[4].Uri.AbsolutePath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Manifest_protocol_uploads_and_registers_the_files_hands_one_manifest_per_batch_to_the_workflow_and_reads_the_records_back()
    {
        var reported = new List<string>();
        var polls = 0;
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/files/uploadURL", hit => FakeHttpHandler.Json(HttpStatusCode.OK, UploadLocation(hit)))
            .OnMatch(LandingUpload, _ => FakeHttpHandler.Json(HttpStatusCode.Created, null))
            .On(HttpMethod.Post, "/files/metadata", hit => FakeHttpHandler.Json(HttpStatusCode.Created, "{\"id\":\"dev:dataset--File.Generic:ds-" + hit.ToString(CultureInfo.InvariantCulture) + "\"}"))
            .On(HttpMethod.Post, "/search/v2/query", HttpStatusCode.OK, """{"results":[{"id":"dev:dataset--File.Generic:ds-0"},{"id":"dev:dataset--File.Generic:ds-1"}],"totalCount":2}""")
            .On(HttpMethod.Post, "/workflow/Osdu_ingest/workflowRun", HttpStatusCode.OK, """{"workflowId":"wf-1","status":"SUBMITTED"}""")
            .OnMatch(AnyRunStatus, _ => FakeHttpHandler.Json(HttpStatusCode.OK, ++polls == 1 ? """{"status":"INPROGRESS"}""" : """{"workflowId":"wf-1","status":"SUCCESS","endTimeStamp":"1700000000000"}"""))
            .On(HttpMethod.Post, "/query/records", hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0
                ? """{"records":[],"invalidRecords":["dev:work-product-component--WellLog:abc","dev:work-product-component--WellLog:def"]}"""
                : """{"records":[{"id":"dev:work-product-component--WellLog:abc","version":3},{"id":"dev:work-product-component--WellLog:def","version":4}],"invalidRecords":[],"retryRecords":[]}"""));
        var (client, runtime, _) = Client(handler);
        using (runtime)
        {
            var options = new ProtocolOptions { WorkflowPollSeconds = 1, WorkflowPayload = new Dictionary<string, string>(StringComparer.Ordinal) { ["Source"] = "recall" } };
            var protocol = new OsduManifestProtocol(client, options, Samples.Logger<OsduManifestProtocol>());
            var works = new[] { Work(true, true, 1, reported: reported), Work(true, true, 1, id: OtherId, reported: reported) };
            var outcomes = await protocol.DeliverBatchAsync(works);
            Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Failure?.Message));
            Assert.Equal(3, outcomes[0].TargetVersion);
            Assert.Equal(4, outcomes[1].TargetVersion);
            Assert.Equal("wf-1", outcomes[0].Returned["workflowId"]);
            Assert.Equal("SUCCESS", outcomes[0].Returned["status"]);
            Assert.Equal("1700000000000", outcomes[0].Returned["endTimeStamp"]);
            Assert.Equal("dev:dataset--File.Generic:ds-0", outcomes[0].Returned["datasetIds"]);
            Assert.Equal("dev:dataset--File.Generic:ds-1", outcomes[1].Returned["datasetIds"]);
            Assert.Equal(["upload-0", "register-0", "indexed", "manifest", "workflow", "records"], outcomes[0].Steps.Select(s => s.Name));
            Assert.Null(outcomes[0].Steps[2].Error);
            var runId = outcomes[0].Returned["runId"];
            Assert.Equal(runId, outcomes[1].Returned["runId"]);
            Assert.Equal(2, reported.Count(r => r.StartsWith("manifest=", StringComparison.Ordinal)));
            Assert.Contains("runId:" + runId, reported.First(r => r.StartsWith("manifest=", StringComparison.Ordinal)), StringComparison.Ordinal);
            Assert.Equal(2, polls);

            var trigger = handler.Calls.Single(c => c.Uri.AbsolutePath.EndsWith("/workflowRun", StringComparison.Ordinal));
            var body = JsonNode.Parse(trigger.Body!)!.AsObject();
            Assert.Equal(runId, body["runId"]!.GetValue<string>());
            var context = body["executionContext"]!.AsObject();
            Assert.Equal("osdu-delivery", context["Payload"]!["AppKey"]!.GetValue<string>());
            Assert.Equal("dev", context["Payload"]!["data-partition-id"]!.GetValue<string>());
            Assert.Equal("recall", context["Payload"]!["Source"]!.GetValue<string>());
            var manifest = context["manifest"]!.AsObject();
            Assert.Equal("osdu:wks:Manifest:1.0.0", manifest["kind"]!.GetValue<string>());
            Assert.False(manifest.ContainsKey("MasterData"));
            var data = manifest["Data"]!.AsObject();
            Assert.Equal(2, data["WorkProductComponents"]!.AsArray().Count);

            // The files were registered through the file service before the manifest named them, so the manifest carries no
            // dataset entries of its own: each record references the datasets the service minted for its files.
            Assert.False(data.ContainsKey("Datasets"));
            Assert.Equal("dev:dataset--File.Generic:ds-0:", data["WorkProductComponents"]![0]!["data"]!["Datasets"]![0]!.GetValue<string>());
            Assert.Equal("dev:dataset--File.Generic:ds-1:", data["WorkProductComponents"]![1]!["data"]!["Datasets"]![0]!.GetValue<string>());
            var register = JsonNode.Parse(handler.Calls.First(c => c.Uri.AbsolutePath.EndsWith("/files/metadata", StringComparison.Ordinal)).Body!)!.AsObject();
            Assert.Null(register["id"]);
            Assert.Equal("/landing/blob-0", register["data"]!["DatasetProperties"]!["FileSourceInfo"]!["FileSource"]!.GetValue<string>());
            Assert.Equal(RecordId, data["WorkProductComponents"]![0]!["id"]!.GetValue<string>());

            var search = JsonNode.Parse(handler.Calls.Single(c => c.Uri.AbsolutePath.EndsWith("/search/v2/query", StringComparison.Ordinal)).Body!)!.AsObject();
            Assert.Equal("osdu:wks:dataset--File.Generic:1.0.0", search["kind"]!.GetValue<string>());
            Assert.Contains("\"dev:dataset--File.Generic:ds-1\"", search["query"]!.GetValue<string>(), StringComparison.Ordinal);

            // One read before the run (the versions storage held) and one after it.
            var reads = handler.Calls.Where(c => c.Uri.AbsolutePath.EndsWith("/query/records", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, reads.Count);
            Assert.Equal("id", JsonNode.Parse(reads[0].Body!)!["attributes"]![0]!.GetValue<string>());
            var query = JsonNode.Parse(reads[1].Body!)!.AsObject();
            Assert.Equal([RecordId, OtherId], query["records"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Equal("data.Datasets", query["attributes"]![0]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Manifest_protocol_resumes_an_earlier_run_and_triggers_a_new_one_when_it_failed()
    {
        var reported = new List<string>();
        var completed = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["manifest"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["runId"] = "run-old", ["workflowId"] = "wf-1" },
        };
        var handler = new FakeHttpHandler()
            .OnMatch(r => RunStatus(r, "run-old"), _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"workflowId":"wf-1","status":"FAILED"}"""))
            .On(HttpMethod.Post, "/workflow/Osdu_ingest/workflowRun", HttpStatusCode.Conflict, """{"message":"run already exists"}""")
            .OnMatch(AnyRunStatus, _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"workflowId":"wf-1","status":"SUCCESS"}"""))
            .On(HttpMethod.Post, "/query/records", hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0
                ? """{"records":[{"id":"dev:work-product-component--WellLog:abc","version":4}]}"""
                : """{"records":[{"id":"dev:work-product-component--WellLog:abc","version":5}]}"""));
        var (client, runtime, _) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduManifestProtocol(client, new ProtocolOptions { WorkflowPollSeconds = 1 }, Samples.Logger<OsduManifestProtocol>());
            var outcome = await protocol.DeliverAsync(Work(true, false, 0, completed: completed, reported: reported));
            Assert.True(outcome.Succeeded);
            Assert.Equal(5, outcome.TargetVersion);
            Assert.NotEqual("run-old", outcome.Returned["runId"]);
            Assert.Equal(["manifest", "workflow", "manifest", "workflow", "records"], outcome.Steps.Select(s => s.Name));
            Assert.True(outcome.Steps[0].Resumed);
            Assert.Contains("failed", outcome.Steps[1].Error, StringComparison.Ordinal);
            Assert.Equal(409, outcome.Steps[2].Status);
            var manifestReport = Assert.Single(reported);
            Assert.StartsWith("manifest=", manifestReport, StringComparison.Ordinal);
            Assert.DoesNotContain("run-old", manifestReport, StringComparison.Ordinal);
            Assert.Contains("priorVersion:4", manifestReport, StringComparison.Ordinal);
            Assert.Equal(5, handler.Calls.Count);
            Assert.EndsWith("/workflowRun/run-old", handler.Calls[0].Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.EndsWith("/query/records", handler.Calls[1].Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.EndsWith("/workflowRun", handler.Calls[2].Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.EndsWith("/query/records", handler.Calls[4].Uri.AbsolutePath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Manifest_protocol_fails_the_records_a_run_did_not_write_and_stops_polling_at_the_timeout()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/workflow/Osdu_ingest/workflowRun", HttpStatusCode.OK, """{"workflowId":"wf-2","runId":"run-2","status":"SUBMITTED"}""")
            .OnMatch(r => RunStatus(r, "run-2"), _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"workflowId":"wf-2","status":"PARTIAL_SUCCESS"}"""))
            .On(HttpMethod.Post, "/query/records", hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0
                ? """{"records":[]}"""
                : """{"records":[{"id":"dev:work-product-component--WellLog:abc","version":6}]}"""));
        var (client, runtime, _) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduManifestProtocol(client, new ProtocolOptions { WorkflowPollSeconds = 1 }, Samples.Logger<OsduManifestProtocol>());
            var outcomes = await protocol.DeliverBatchAsync([Work(true, false, 0), Work(true, false, 0, id: OtherId)]);
            Assert.True(outcomes[0].Succeeded);
            Assert.Equal(6, outcomes[0].TargetVersion);
            Assert.Equal("run-2", outcomes[0].Returned["runId"]);
            Assert.False(outcomes[1].Succeeded);
            Assert.Contains("not in storage", outcomes[1].Failure!.Message, StringComparison.Ordinal);
            Assert.Contains("run-2", outcomes[1].Failure!.Message, StringComparison.Ordinal);
            Assert.Equal(["manifest", "workflow", "records"], outcomes[1].Steps.Select(s => s.Name));
            Assert.NotNull(outcomes[1].Steps[2].Error);
        }

        // Storage answers a read of a record it does not hold by naming the id under invalidRecords (a live M26 service
        // does), so after a finished run that is a record the workflow did not write. It fails naming the run, and the
        // next try, finding that run already finished, sends the record in a new run instead of reading the same one back.
        var rejecting = new FakeHttpHandler()
            .On(HttpMethod.Post, "/workflow/Osdu_ingest/workflowRun", HttpStatusCode.OK, """{"workflowId":"wf-4","runId":"run-4","status":"SUCCESS"}""")
            .OnMatch(r => RunStatus(r, "run-4"), _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"workflowId":"wf-4","status":"SUCCESS"}"""))
            .On(HttpMethod.Post, "/query/records", HttpStatusCode.OK, """{"records":[],"invalidRecords":["dev:work-product-component--WellLog:abc"]}""");
        var (client4, runtime4, _) = Client(rejecting);
        using (runtime4)
        {
            var protocol = new OsduManifestProtocol(client4, new ProtocolOptions { WorkflowPollSeconds = 1 }, Samples.Logger<OsduManifestProtocol>());
            var first = await protocol.DeliverBatchAsync([Work(true, false, 0)]);
            Assert.False(first[0].Succeeded);
            Assert.Contains("not in storage", first[0].Failure!.Message, StringComparison.Ordinal);
            Assert.Contains("invalidRecords", first[0].Failure!.Message, StringComparison.Ordinal);
            Assert.Contains("run-4", first[0].Failure!.Message, StringComparison.Ordinal);
            Assert.Equal(4, rejecting.Calls.Count);

            var earlier = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["manifest"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["runId"] = "run-4" },
            };
            var second = await protocol.DeliverBatchAsync([Work(true, false, 0, completed: earlier)]);
            Assert.False(second[0].Succeeded);
            Assert.Equal(2, rejecting.Calls.Count(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith("/workflowRun", StringComparison.Ordinal)));
        }

        var reported = new List<string>();
        var slow = new FakeHttpHandler()
            .On(HttpMethod.Post, "/query/records", HttpStatusCode.OK, """{"records":[]}""")
            .On(HttpMethod.Post, "/workflow/Osdu_ingest/workflowRun", HttpStatusCode.OK, """{"runId":"run-3","status":"SUBMITTED"}""");
        var (client2, runtime2, clock) = Client(slow);
        slow.OnMatch(r => RunStatus(r, "run-3"), _ =>
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            return FakeHttpHandler.Json(HttpStatusCode.OK, """{"status":"INPROGRESS"}""");
        });
        using (runtime2)
        {
            var protocol = new OsduManifestProtocol(client2, new ProtocolOptions { WorkflowPollSeconds = 1, WorkflowTimeoutMinutes = 1 }, Samples.Logger<OsduManifestProtocol>(), time: clock);
            var ex = await Assert.ThrowsAsync<DeliveryException>(() => protocol.DeliverAsync(Work(true, false, 0, reported: reported)));
            Assert.Contains("run-3", ex.Message, StringComparison.Ordinal);
            Assert.Contains("resumes polling", ex.Message, StringComparison.Ordinal);
            Assert.Contains(reported, r => r.StartsWith("manifest=", StringComparison.Ordinal) && r.Contains("runId:run-3", StringComparison.Ordinal));
            Assert.Equal(3, slow.Calls.Count);
        }
    }

    [Theory]
    [InlineData("SUCCESS")]
    [InlineData("PARTIAL_SUCCESS")]
    [InlineData("FINISHED")]
    [InlineData("finished")]
    [InlineData("success")]
    public async Task Every_terminal_workflow_status_settles_the_run_instead_of_reading_as_unknown(string status)
    {
        // The workflow service reports run status in two shapes (openapi workflow v1: WorkflowRunResponse is upper
        // case, WorkflowRun is lower) and FINISHED is a real terminal status in both. A status the protocol does not
        // recognise fails the record, so a completed ingestion must never land there.
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/workflow/Osdu_ingest/workflowRun", HttpStatusCode.OK, """{"workflowId":"wf-5","runId":"run-5","status":"SUBMITTED"}""")
            .OnMatch(r => RunStatus(r, "run-5"), _ => FakeHttpHandler.Json(HttpStatusCode.OK, $$"""{"workflowId":"wf-5","status":"{{status}}"}"""))
            .On(HttpMethod.Post, "/query/records", hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0
                ? """{"records":[{"id":"dev:work-product-component--WellLog:abc","version":10}]}"""
                : """{"records":[{"id":"dev:work-product-component--WellLog:abc","version":11}]}"""));
        var (client, runtime, _) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduManifestProtocol(client, new ProtocolOptions { WorkflowPollSeconds = 1 }, Samples.Logger<OsduManifestProtocol>());
            var outcome = await protocol.DeliverAsync(Work(true, false, 0));

            Assert.True(outcome.Succeeded);
            Assert.Equal(11, outcome.TargetVersion);
            Assert.Equal(status.ToUpperInvariant(), outcome.Returned["status"]);
        }
    }

    [Fact]
    public async Task A_workflow_status_the_service_has_never_reported_is_named_rather_than_polled_forever()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/query/records", HttpStatusCode.OK, """{"records":[]}""")
            .On(HttpMethod.Post, "/workflow/Osdu_ingest/workflowRun", HttpStatusCode.OK, """{"runId":"run-6","status":"SUBMITTED"}""")
            .OnMatch(r => RunStatus(r, "run-6"), _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"status":"WEDGED"}"""));
        var (client, runtime, _) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduManifestProtocol(client, new ProtocolOptions { WorkflowPollSeconds = 1 }, Samples.Logger<OsduManifestProtocol>());
            var ex = await Assert.ThrowsAsync<DeliveryException>(() => protocol.DeliverAsync(Work(true, false, 0)));

            Assert.Contains("unknown status 'WEDGED'", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Manifest_protocol_does_not_take_a_version_that_was_there_before_the_run_as_written_and_waits_for_the_index()
    {
        // Seen live: a finished run that dropped a record which already existed leaves the old version in place, and a
        // read-back that only asked whether the record was there settled it as delivered.
        var reported = new List<string>();
        var unchanged = new FakeHttpHandler()
            .On(HttpMethod.Post, "/query/records", HttpStatusCode.OK, """{"records":[{"id":"dev:work-product-component--WellLog:abc","version":7}]}""")
            .On(HttpMethod.Post, "/workflow/Osdu_ingest/workflowRun", HttpStatusCode.OK, """{"workflowId":"wf-5","runId":"run-5","status":"SUBMITTED"}""")
            .OnMatch(r => RunStatus(r, "run-5"), _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"workflowId":"wf-5","status":"FINISHED"}"""));
        var (client, runtime, _) = Client(unchanged);
        using (runtime)
        {
            var protocol = new OsduManifestProtocol(client, new ProtocolOptions { WorkflowPollSeconds = 1 }, Samples.Logger<OsduManifestProtocol>());
            var outcomes = await protocol.DeliverBatchAsync([Work(true, false, 0, existing: 7, reported: reported)]);
            Assert.False(outcomes[0].Succeeded);
            Assert.Contains("still holds version 7", outcomes[0].Failure!.Message, StringComparison.Ordinal);
            Assert.Contains("next try triggers a new run", outcomes[0].Failure!.Message, StringComparison.Ordinal);
            Assert.Contains(reported, r => r.StartsWith("manifest=", StringComparison.Ordinal) && r.Contains("priorVersion:7", StringComparison.Ordinal));
        }

        // Ingestion checks references against the search index, so the manifest waits until the index lists the dataset
        // registered for the record: here it appears on the second ask.
        var asks = 0;
        var indexed = new FakeHttpHandler()
            .On(HttpMethod.Get, "/files/uploadURL", hit => FakeHttpHandler.Json(HttpStatusCode.OK, UploadLocation(hit)))
            .OnMatch(LandingUpload, _ => FakeHttpHandler.Json(HttpStatusCode.Created, null))
            .On(HttpMethod.Post, "/files/metadata", HttpStatusCode.Created, """{"id":"dev:dataset--File.Generic:ds-new"}""")
            .On(HttpMethod.Post, "/search/v2/query", _ => FakeHttpHandler.Json(HttpStatusCode.OK, ++asks == 1 ? """{"results":[],"totalCount":0}""" : """{"results":[{"id":"dev:dataset--File.Generic:ds-new"}],"totalCount":1}"""))
            .On(HttpMethod.Post, "/query/records", hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0 ? """{"records":[]}""" : """{"records":[{"id":"dev:work-product-component--WellLog:abc","version":1}]}"""))
            .On(HttpMethod.Post, "/workflow/Osdu_ingest/workflowRun", HttpStatusCode.OK, """{"workflowId":"wf-6","runId":"run-6","status":"SUBMITTED"}""")
            .OnMatch(r => RunStatus(r, "run-6"), _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"workflowId":"wf-6","status":"SUCCESS"}"""));
        var (client2, runtime2, _) = Client(indexed);
        using (runtime2)
        {
            var protocol = new OsduManifestProtocol(client2, new ProtocolOptions { WorkflowPollSeconds = 1 }, Samples.Logger<OsduManifestProtocol>());
            var outcome = await protocol.DeliverAsync(Work(true, true, 1));
            Assert.True(outcome.Succeeded);
            Assert.Equal(2, asks);
            var step = outcome.Steps.Single(s => s.Name == OsduManifestProtocol.IndexedStep);
            Assert.Null(step.Error);
            var first = indexed.Calls.FindIndex(c => c.Uri.AbsolutePath.EndsWith("/workflowRun", StringComparison.Ordinal));
            Assert.True(indexed.Calls.FindLastIndex(c => c.Uri.AbsolutePath.EndsWith("/search/v2/query", StringComparison.Ordinal)) < first);
        }

        // A wait that runs out is named on the step, and the manifest goes ahead: the read-back decides what the run wrote.
        var late = new FakeHttpHandler()
            .On(HttpMethod.Get, "/files/uploadURL", hit => FakeHttpHandler.Json(HttpStatusCode.OK, UploadLocation(hit)))
            .OnMatch(LandingUpload, _ => FakeHttpHandler.Json(HttpStatusCode.Created, null))
            .On(HttpMethod.Post, "/files/metadata", HttpStatusCode.Created, """{"id":"dev:dataset--File.Generic:ds-late"}""")
            .On(HttpMethod.Post, "/query/records", hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0 ? """{"records":[]}""" : """{"records":[{"id":"dev:work-product-component--WellLog:abc","version":1}]}"""))
            .On(HttpMethod.Post, "/workflow/Osdu_ingest/workflowRun", HttpStatusCode.OK, """{"workflowId":"wf-7","runId":"run-7","status":"SUBMITTED"}""")
            .OnMatch(r => RunStatus(r, "run-7"), _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"workflowId":"wf-7","status":"SUCCESS"}"""));
        var (client3, runtime3, clock3) = Client(late);
        late.On(HttpMethod.Post, "/search/v2/query", _ =>
        {
            clock3.Advance(TimeSpan.FromSeconds(6));
            return FakeHttpHandler.Json(HttpStatusCode.OK, """{"results":[],"totalCount":0}""");
        });
        using (runtime3)
        {
            var protocol = new OsduManifestProtocol(client3, new ProtocolOptions { WorkflowPollSeconds = 1, DatasetIndexWaitSeconds = 10 }, Samples.Logger<OsduManifestProtocol>(), time: clock3);
            var outcome = await protocol.DeliverAsync(Work(true, true, 1));
            Assert.True(outcome.Succeeded);
            var step = outcome.Steps.Single(s => s.Name == OsduManifestProtocol.IndexedStep);
            Assert.Contains("not listed by the search index", step.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Manifest_sections_derive_from_the_kinds()
    {
        Assert.Equal("WorkProductComponents", OsduManifestProtocol.SectionOf(TestSchema.Doc(Document)));
        Assert.Equal("MasterData", OsduManifestProtocol.SectionOf(TestSchema.Doc("""{"kind":"osdu:wks:master-data--Wellbore:1.0.0"}""")));
        Assert.Equal("ReferenceData", OsduManifestProtocol.SectionOf(TestSchema.Doc("""{"kind":"osdu:wks:reference-data--UnitOfMeasure:1.0.0"}""")));
        Assert.Equal("WorkProduct", OsduManifestProtocol.SectionOf(TestSchema.Doc("""{"kind":"osdu:wks:work-product--WorkProduct:1.0.0"}""")));
        Assert.Equal("Datasets", OsduManifestProtocol.SectionOf(TestSchema.Doc("""{"kind":"osdu:wks:dataset--File.Generic:1.0.0"}""")));
        var unknown = Assert.Throws<DeliveryException>(() => OsduManifestProtocol.SectionOf(TestSchema.Doc("""{"kind":"nope"}""")));
        Assert.Contains("manifestSection", unknown.Message, StringComparison.Ordinal);
    }

    private sealed class MemoryPayload(int chunks) : IPayloadSource
    {
        public Task<IReadOnlyList<Drops.PayloadChunk>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Drops.PayloadChunk>>(Enumerable.Range(0, chunks).Select(i => new Drops.PayloadChunk(i, $"mem://files/curve_{i}.parquet", 7)).ToList());

        public Task<Stream> OpenAsync(Drops.PayloadChunk chunk, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("chunk-" + chunk.Index)));
    }
}
