using System.Text.Json.Nodes;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A manifest sent by reference (osdu/specs/workflows/INTEGRATION.md section 3.3; docs/interfaces-design.md section 5.8):
/// stored as a dataset through the Dataset service and named in the by-reference workflow's context when the flow always
/// asks for it or a manifest is above the inline limit, split into manifests under the limit when the partition does not
/// register that workflow, resumed by the run and dataset the manifest step recorded, and removed once its run settles.
/// </summary>
public sealed class ManifestByReferenceTests
{
    private const string ByReference = ProtocolOptions.DefaultByReferenceWorkflowName;

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
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public OsduManifestProtocol Protocol(ManifestReference reference, int limitKb = ProtocolOptions.DefaultManifestInlineLimitKb)
            => new(Client, new ProtocolOptions
            {
                ManifestByReference = reference,
                ManifestInlineLimitKb = limitKb,
                BatchSize = 10,
                WorkflowPollSeconds = 1,
                DatasetIndexWaitSeconds = 0,
                UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-blob-type"] = "BlockBlob" },
            }, Samples.Logger<OsduManifestProtocol>());

        public void Dispose() => Runtime.Dispose();
    }

    private static JsonObject Wellbore(string key, int padding = 0) => FakeOsduPlatform.Record(
        "dev:master-data--Wellbore:" + key,
        "osdu:wks:master-data--Wellbore:1.3.0",
        new JsonObject { ["FacilityName"] = key, ["Remarks"] = new string('x', padding) });

    private static DeliveryWork Work(JsonObject document, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? completed = null) => new()
    {
        Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("by-reference", [document["id"]!.GetValue<string>()]),
        TargetId = document["id"]!.GetValue<string>(),
        Document = document,
        DeliverMetadata = true,
        DeliverPayload = false,
        CompletedSteps = completed ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
    };

    /// <summary>The execution context an inline manifest of <paramref name="records"/> is sent with, for measuring it.</summary>
    private static JsonObject InlineContext(params JsonObject[] records) => new()
    {
        ["Payload"] = new JsonObject { ["AppKey"] = "osdu-delivery", ["data-partition-id"] = "dev" },
        ["manifest"] = new JsonObject
        {
            ["kind"] = "osdu:wks:Manifest:1.0.0",
            ["MasterData"] = new JsonArray(records.Select(r => (JsonNode?)r.DeepClone()).ToArray()),
        },
    };

    private static int Lookups(FakeOsduPlatform platform)
        => platform.Calls.Count(c => c.Method == HttpMethod.Get && c.Uri.AbsolutePath.EndsWith("/workflow/" + ByReference, StringComparison.Ordinal));

    private static bool Stores(FakeHttpHandler.Request request)
        => request.Uri.AbsolutePath.EndsWith("/storageInstructions", StringComparison.Ordinal);

    [Fact]
    public async Task A_manifest_by_reference_is_stored_as_a_dataset_named_in_the_context_and_removed_once_its_run_settles()
    {
        var platform = new FakeOsduPlatform();
        platform.Register(ByReference, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        using var rig = new Rig(platform);
        var wellbore = Wellbore("wb-1");
        var outcome = await rig.Protocol(ManifestReference.Always).DeliverAsync(Work(wellbore));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var run = Assert.Single(platform.Runs);
        Assert.Equal(ByReference, run.Workflow);
        var manifestId = "dev:dataset--File.Generic:osdu-delivery-manifest-" + run.RunId;
        Assert.Equal(["Payload", "acl", "legal", "manifest"], run.Context.Select(p => p.Key));
        Assert.Equal(manifestId, run.Context["manifest"]!.GetValue<string>());
        Assert.Equal("dev", run.Context["Payload"]!["data-partition-id"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(wellbore["acl"], run.Context["acl"]));
        Assert.True(JsonNode.DeepEquals(wellbore["legal"], run.Context["legal"]));

        // The stored manifest holds the record, and the access and legal blocks the workflow copies onto what it writes.
        var stored = ComposedRouteTests.StoredManifest(platform, manifestId);
        Assert.Equal("osdu:wks:Manifest:1.0.0", stored["kind"]!.GetValue<string>());
        Assert.Equal(wellbore["id"]!.GetValue<string>(), stored["MasterData"]![0]!["id"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(wellbore["acl"], stored["acl"]));
        Assert.True(JsonNode.DeepEquals(wellbore["legal"], stored["legal"]));
        var dataset = platform.Records[manifestId];
        Assert.Equal("osdu:wks:dataset--File.Generic:1.0.0", dataset["kind"]!.GetValue<string>());
        Assert.Equal("manifest-" + run.RunId + ".json", dataset["data"]!["DatasetProperties"]!["FileSourceInfo"]!["Name"]!.GetValue<string>());
        Assert.Equal("application/json", platform.Calls.Single(c => c.Uri.AbsolutePath.StartsWith("/staging/blob/", StringComparison.Ordinal)).ContentType);

        // The manifest step names the workflow and the dataset, so a retry resumes this run; the dataset is removed.
        var step = outcome.Steps.Single(s => s.Name == OsduManifestProtocol.ManifestStep);
        Assert.Equal(ByReference, step.Returned[OsduManifestProtocol.WorkflowValue]);
        Assert.Equal(manifestId, step.Returned[OsduManifestProtocol.ManifestDatasetValue]);
        Assert.Contains(manifestId, platform.Removed);
        Assert.Equal(platform.Records[wellbore["id"]!.GetValue<string>()]["version"]!.GetValue<long>(), outcome.TargetVersion);

        Assert.Equal(
            [
                "core/dataset POST /metadataRecord/{id}/softDelete",
                "core/dataset POST /storageInstructions",
                "core/dataset PUT /registerDataset",
                "core/storage POST /query/records",
                "core/workflow GET /v1/workflow/{workflow_name}",
                "core/workflow GET /v1/workflow/{workflow_name}/workflowRun/{runId}",
                "core/workflow POST /v1/workflow/{workflow_name}/workflowRun",
            ],
            OsduContracts.AssertConform(
                platform.Calls, FakeOsduPlatform.ToSignedLocation, OsduContracts.ContextValueTyping, OsduContracts.Dataset, OsduContracts.Workflow, OsduContracts.Storage));
    }

    [Fact]
    public async Task Auto_sends_a_manifest_inline_under_the_limit_and_by_reference_above_it()
    {
        var platform = new FakeOsduPlatform();
        platform.Register("Osdu_ingest", new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        platform.Register(ByReference, new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        using var rig = new Rig(platform);

        // Under the limit the manifest goes inline, and nothing asks whether the by-reference workflow exists.
        var small = await rig.Protocol(ManifestReference.Auto).DeliverAsync(Work(Wellbore("wb-small")));
        Assert.True(small.Succeeded, small.Failure?.Message);
        Assert.Equal("Osdu_ingest", Assert.Single(platform.Runs).Workflow);
        Assert.Equal(0, Lookups(platform));
        Assert.DoesNotContain(platform.Calls, Stores);

        // Above it the manifest goes by reference, and the protocol asks once whether the partition registers the workflow.
        var protocol = rig.Protocol(ManifestReference.Auto, limitKb: 1);
        var large = await protocol.DeliverAsync(Work(Wellbore("wb-large", padding: 2048)));
        Assert.True(large.Succeeded, large.Failure?.Message);
        Assert.Equal(ByReference, platform.Runs[1].Workflow);
        Assert.IsAssignableFrom<JsonValue>(platform.Runs[1].Context["manifest"]);
        Assert.Equal(1, Lookups(platform));

        var again = await protocol.DeliverAsync(Work(Wellbore("wb-large-2", padding: 2048)));
        Assert.True(again.Succeeded, again.Failure?.Message);
        Assert.Equal(ByReference, platform.Runs[2].Workflow);
        Assert.Equal(1, Lookups(platform));
        Assert.Equal(2, platform.Removed.Count(id => id.Contains(":osdu-delivery-manifest-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Auto_without_the_by_reference_workflow_splits_a_batch_into_manifests_under_the_limit()
    {
        var platform = new FakeOsduPlatform();
        platform.Register("Osdu_ingest", new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        using var rig = new Rig(platform);
        var records = new[] { Wellbore("wb-0", 100), Wellbore("wb-1", 100), Wellbore("wb-big", 3000), Wellbore("wb-2", 100) };

        // Two small records fit under 3 KB together; the big one is above it alone, so it goes alone.
        Assert.True(OsduManifestProtocol.RequestKilobytes(InlineContext(records[0], records[1])) < 3);
        Assert.True(OsduManifestProtocol.RequestKilobytes(InlineContext(records[2])) > 3);

        var outcomes = await rig.Protocol(ManifestReference.Auto, limitKb: 3).DeliverBatchAsync(records.Select(r => Work(r)).ToList());
        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Failure?.Message));
        Assert.Equal(
            [["wb-0", "wb-1"], ["wb-big"], ["wb-2"]],
            platform.Runs.Select(r => r.Context["manifest"]!["MasterData"]!.AsArray().Select(m => m!["data"]!["FacilityName"]!.GetValue<string>()).ToArray()).ToArray());
        Assert.All(platform.Runs, r => Assert.Equal("Osdu_ingest", r.Workflow));
        Assert.Equal(1, Lookups(platform));
        Assert.DoesNotContain(platform.Calls, Stores);
        for (var i = 0; i < records.Length; i++)
        {
            Assert.Equal(platform.Records[records[i]["id"]!.GetValue<string>()]["version"]!.GetValue<long>(), outcomes[i].TargetVersion);
        }
    }

    [Fact]
    public async Task Always_on_a_partition_without_the_by_reference_workflow_fails_before_storing_anything()
    {
        var platform = new FakeOsduPlatform();
        platform.Register("Osdu_ingest");
        using var rig = new Rig(platform);
        var failed = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol(ManifestReference.Always).DeliverAsync(Work(Wellbore("wb-1"))));
        Assert.Contains("has no workflow named " + ByReference, failed.Message, StringComparison.Ordinal);
        Assert.Empty(platform.Runs);
        Assert.DoesNotContain(platform.Calls, Stores);
    }

    [Fact]
    public async Task A_retry_resumes_the_by_reference_run_an_earlier_try_started_and_removes_its_manifest()
    {
        var platform = new FakeOsduPlatform();
        platform.Register(ByReference);
        using var rig = new Rig(platform);
        var wellbore = Wellbore("wb-resumed");
        const string runId = "7d3c2f0e-5b1a-4c8e-9f2d-3a4b5c6d7e8f";
        var manifestId = "dev:dataset--File.Generic:osdu-delivery-manifest-" + runId;

        // What the earlier try left: the run triggered, its manifest stored, and the record the run wrote.
        platform.Runs.Add(new FakeOsduPlatform.Run(ByReference, runId, new JsonObject()));
        platform.Put(FakeOsduPlatform.Record(manifestId, "osdu:wks:dataset--File.Generic:1.0.0"));
        var written = platform.Put(wellbore);
        var completed = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [OsduManifestProtocol.ManifestStep] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["runId"] = runId,
                [OsduManifestProtocol.WorkflowValue] = ByReference,
                [OsduManifestProtocol.ManifestDatasetValue] = manifestId,
            },
        };

        var outcome = await rig.Protocol(ManifestReference.Always).DeliverAsync(Work(wellbore, completed));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(written, outcome.TargetVersion);
        Assert.Empty(platform.Triggers(ByReference));
        Assert.Single(platform.Calls, c => c.Uri.AbsolutePath.EndsWith($"/workflow/{ByReference}/workflowRun/{runId}", StringComparison.Ordinal));
        Assert.Contains(manifestId, platform.Removed);
        Assert.Contains(outcome.Steps, s => s.Name == OsduManifestProtocol.ManifestStep && s.Resumed);
        Assert.DoesNotContain(platform.Calls, Stores);
    }
}
