using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The record preview and the read of an OSDU record by id as the API serves them: a preview of a flow's first record or of
/// a named key, and of a record page's record, each queued for a node with the interface, the key and the scope's values it
/// is read with; the flow's declared parameters listed so a page can ask for them; and every request a node would refuse
/// (an undeclared parameter, a required one left out, a key or an id that cannot be one) answered as a 400 before anything
/// is queued. Nothing here reaches an OSDU: the tasks are queued and read back from the catalog, not run.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryPreviewApiTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_preview_and_a_read_by_id_are_queued_for_a_node_with_what_they_read_and_bad_asks_are_refused()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-preview-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-preview-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var logsLedger = FlowId.Of(flowName + "/welllogs");
        var key = new DeliveryKey(Guid.NewGuid());
        var now = DateTime.UtcNow;
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            parameters:
              logSource: { required: true, description: The Recall log source a run reads }
              project: { default: NORWAY_WELLDB }
            source:
              connection: ${env:OSDU_DATA_DB}
              work: ../.work/{logSource}
            render:
              parameters:
                dataPartition: dev
            target:
              endpoint: ${env:OSDU_URL}
              headers: { data-partition-id: dev }
              protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }
            interfaces:
              wellbores:
                record: { object: OsduData.arc.Wellbore, key: [facility_name] }
                mapping: Wellbore@1.0.0
              welllogs:
                record: { object: OsduData.arc.WellLog, key: [source_project, log_id] }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: payload_hash }
                mapping: WellLog@1.4.0
            """;

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-preview-" + suffix, RemoteUrl = "https://example/cp-preview.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = pipelineId,
                RepoId = repoId,
                Name = flowName,
                Kind = "delivery",
                RelativePath = "flows/" + flowName + ".yaml",
                ContentHash = new string('0', 64),
                Yaml = yaml,
                DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{flowName}}","flowKind":"delivery"}"""),
                Active = true,
                Wave = 0,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.UpsertPendingAsync(logsLedger,
        [
            new RecordState
            {
                DeliveryKey = key,
                FlowId = logsLedger,
                SourceKey = "recall:NORWAY_WELLDB/12359/1",
                Label = "NO 33/9-C-28 B",
                MappingName = "WellLog",
                Status = RecordStatus.Pending,
                PendingDocumentRef = "1:0:10",
                PendingMetadata = true,
            },
        ]);

        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // A page asks for the scope's values by what the flow declares: each parameter, required or with its default.
            using (var interfaces = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/interfaces"))
            {
                Assert.Equal(HttpStatusCode.OK, interfaces.StatusCode);
                var logs = JsonDocument.Parse(await interfaces.Content.ReadAsStringAsync()).RootElement.EnumerateArray().Last();
                Assert.Equal(["source_project", "log_id"], logs.GetProperty("keyColumns").EnumerateArray().Select(c => c.GetString()));
                var parameters = logs.GetProperty("parameters").EnumerateArray().ToList();
                var logSource = parameters.Single(p => p.GetProperty("name").GetString() == "logSource");
                Assert.True(logSource.GetProperty("required").GetBoolean());
                Assert.Equal(JsonValueKind.Null, logSource.GetProperty("default").ValueKind);
                Assert.Equal("The Recall log source a run reads", logSource.GetProperty("description").GetString());
                var project = parameters.Single(p => p.GetProperty("name").GetString() == "project");
                Assert.Equal("NORWAY_WELLDB", project.GetProperty("default").GetString());
            }

            // A preview of a named key, with the scope's values: queued for a node, carrying both and the interface.
            var previewPath = $"/api/v1/delivery/flows/{pipelineId:D}/preview?interface=welllogs";
            var queued = await QueuedAsync(client, token, previewPath, new { key = "  NORWAY_WELLDB/12359/1 ", values = new Dictionary<string, string> { ["logSource"] = "STAT_COMP" } });
            Assert.Equal("delivery-preview", queued.Operation);
            Assert.Equal(flowName, queued.SourceRef);
            Assert.Equal("NORWAY_WELLDB/12359/1", queued.Argument("key"));
            Assert.Equal("welllogs", queued.Argument("interface"));
            Assert.Equal("STAT_COMP", JsonDocument.Parse(queued.Argument("values")!).RootElement.GetProperty("logSource").GetString());

            // Without a key it previews the scope's first record, and carries no key.
            var first = await QueuedAsync(client, token, previewPath, new { values = new Dictionary<string, string> { ["logSource"] = "STAT_COMP" } });
            Assert.Null(first.Argument("key"));

            // What a node would refuse is refused here, before anything is queued.
            await RefusedAsync(client, token, previewPath, new { key = "x" }, "needs a value for logSource");
            await RefusedAsync(client, token, previewPath, new { values = new Dictionary<string, string> { ["logSource"] = " " } }, "needs a value for logSource");
            await RefusedAsync(client, token, previewPath, new { values = new Dictionary<string, string> { ["logSource"] = "S", ["region"] = "north" } }, "declares no parameter 'region'");
            await RefusedAsync(client, token, previewPath, new { key = "NORWAY\nWELLDB", values = new Dictionary<string, string> { ["logSource"] = "S" } }, "control character");
            await RefusedAsync(client, token, previewPath, new { key = new string('k', ComputeTaskPayload.MaxArgumentLength + 1), values = new Dictionary<string, string> { ["logSource"] = "S" } }, "characters long");
            await RefusedAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/preview", new { values = new Dictionary<string, string> { ["logSource"] = "S" } }, "name the one this request is about with ?interface=");

            // A record page's preview names its record by the delivery key, and reads the row the way the record was planned.
            var record = await QueuedAsync(client, token, $"/api/v1/delivery/records/{logsLedger:D}/{key.Value:D}/preview", null);
            Assert.Equal("delivery-preview", record.Operation);
            Assert.Equal(key.Value.ToString("D"), record.Argument("key"));
            Assert.Equal("welllogs", record.Argument("interface"));
            using (var missing = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/records/{logsLedger:D}/{Guid.NewGuid():D}/preview"))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }

            // A read by id takes a reference as a document holds it and reads the record, at its latest version.
            var readPath = $"/api/v1/delivery/flows/{pipelineId:D}/osdu/read?interface=welllogs";
            var read = await QueuedAsync(client, token, readPath, new { targetId = " dev:master-data--Wellbore:NO-33-9-C-28-B: " });
            Assert.Equal("delivery-read", read.Operation);
            Assert.Equal("dev:master-data--Wellbore:NO-33-9-C-28-B", read.Argument("targetId"));
            var versioned = await QueuedAsync(client, token, readPath, new { targetId = "dev:master-data--Wellbore:NO-33-9-C-28-B:1712345678901234" });
            Assert.Equal("dev:master-data--Wellbore:NO-33-9-C-28-B", versioned.Argument("targetId"));
            Assert.Null(versioned.Argument("version"));

            // A version to read at rides beside the id, and a record's own read takes one too; a version that is not
            // one is refused before anything is queued.
            var atVersion = await QueuedAsync(client, token, readPath, new { targetId = "dev:master-data--Wellbore:NO-33-9-C-28-B", version = 1712345678901234L });
            Assert.Equal("1712345678901234", atVersion.Argument("version"));
            await RefusedAsync(client, token, readPath, new { targetId = "dev:master-data--Wellbore:NO-33-9-C-28-B", version = 0 }, "is not a record version");
            var recordReadPath = $"/api/v1/delivery/records/{logsLedger:D}/{key.Value:D}/read";
            var recordRead = await QueuedAsync(client, token, recordReadPath, new { version = 3 });
            Assert.Equal("delivery-read", recordRead.Operation);
            Assert.Equal(key.Value.ToString("D"), recordRead.Argument("deliveryKey"));
            Assert.Equal("3", recordRead.Argument("version"));
            Assert.Null((await QueuedAsync(client, token, recordReadPath, null)).Argument("version"));
            await RefusedAsync(client, token, recordReadPath, new { version = -1 }, "is not a record version");

            await RefusedAsync(client, token, readPath, new { targetId = "" }, "Name the OSDU id to read");
            await RefusedAsync(client, token, readPath, new { targetId = "NO 33/9-C-28 B" }, "is not an OSDU record id");
            await RefusedAsync(client, token, readPath, new { targetId = "dev:Wellbore:x" }, "is not an OSDU record id");
            await RefusedAsync(client, token, readPath, new { targetId = "dev:master-data--Wellbore:" + new string('x', 1100) }, "an OSDU id is at most");
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryRecords.Where(r => r.FlowId == logsLedger).ExecuteDeleteAsync();
                await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.ComputeTasks.Where(t => t.SourceRef == flowName).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }

        async Task<ComputeTaskPayload> QueuedAsync(HttpClient client, string token, string path, object? body)
        {
            using var response = await SendAsync(client, token, HttpMethod.Post, path, body);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"POST {path} answered {(int)response.StatusCode}: {text}");
            var taskId = JsonDocument.Parse(text).RootElement.GetProperty("taskId").GetGuid();
            await using var db = CatalogDatabase.Create(cs);
            var task = await db.ComputeTasks.AsNoTracking().SingleAsync(t => t.TaskId == taskId);
            return JsonSerializer.Deserialize<ComputeTaskPayload>(task.ArgumentsJson, WebJson)!;
        }
    }

    private static async Task RefusedAsync(HttpClient client, string token, string path, object body, string why)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"POST {path} answered {(int)response.StatusCode}: {text}");
        Assert.Contains(why, text, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
