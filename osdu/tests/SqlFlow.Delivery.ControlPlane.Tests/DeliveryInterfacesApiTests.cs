using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A flow that declares interfaces as the API serves it (docs/interfaces-design.md sections 4 and 9): the control plane
/// describes a pipeline synced before the read model of interfaces existed once it starts, lists the pipeline's interfaces
/// with their routes and counts, adds a source's counts up, asks which interface a request is about, and leads a record to
/// its pipeline and interface, the interface travelling with every task it queues for a node.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeliveryInterfacesApiTests
{
    [SkippableFact]
    public async Task A_source_s_interfaces_are_listed_counted_and_named_on_every_request_about_one()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-source-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-interfaces-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var logsLedger = FlowId.Of(flowName + "/welllogs");
        var key = new DeliveryKey(Guid.NewGuid());
        var now = DateTime.UtcNow;
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            parameters:
              logSource: { required: true }
            source:
              connection: ${env:OSDU_SAMPLE_DB}
              work: ../.work/{logSource}
            render:
              parameters:
                dataPartition: opendes
            target:
              endpoint: ${env:OSDU_URL}
              headers: { data-partition-id: opendes }
              protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }
            interfaces:
              wellbores:
                record: { object: OsduSample.ing.Wellbore, key: [facility_name] }
                mapping: Wellbore@1.0.0
              welllogs:
                record: { object: OsduSample.ing.WellLog, key: [source_project, log_id] }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: payload_hash }
                mapping: WellLog@1.4.0
            """;

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-interfaces-" + suffix, RemoteUrl = "https://example/cp-interfaces.git",
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
                SourceKey = "NO_15_9/L-1001",
                Label = "NO_15_9/L-1001",
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

            // The pipeline was synced before interfaces were described; the control plane describes it once it starts.
            await WaitForInterfacesAsync(cs, repoId, 2);

            using (var interfaces = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/interfaces"))
            {
                Assert.Equal(HttpStatusCode.OK, interfaces.StatusCode);
                var items = JsonDocument.Parse(await interfaces.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();
                Assert.Equal(["wellbores", "welllogs"], items.Select(i => i.GetProperty("interface").GetString()));
                Assert.Equal("storage", items[0].GetProperty("route").GetString());
                Assert.Equal("ddms", items[1].GetProperty("route").GetString());
                Assert.Equal(logsLedger, items[1].GetProperty("flowId").GetGuid());
                Assert.Equal(flowName + "/welllogs", items[1].GetProperty("ledger").GetString());
                Assert.Empty(items[1].GetProperty("after").EnumerateArray());
                Assert.Equal(1, items[1].GetProperty("stats").GetProperty("pending").GetInt64());
                Assert.Equal(0, items[0].GetProperty("stats").GetProperty("total").GetInt64());
                Assert.Contains("declares bulk", items[1].GetProperty("routeReason").GetString(), StringComparison.Ordinal);

                // The repository's mappings are not synced yet, so nothing but after: orders the interfaces, and the listing says why.
                Assert.All(items, i => Assert.Equal(1, i.GetProperty("wave").GetInt32()));
                Assert.Empty(items[1].GetProperty("waitsFor").EnumerateArray());
                Assert.Contains("Mapping Wellbore@1.0.0 of interface 'wellbores' is not among the repository's valid mappings", items[0].GetProperty("orderProblem").GetString(), StringComparison.Ordinal);
            }

            // Once the mappings are synced and their templates saved, the well logs wait for the wellbores they refer to.
            await SampleEstate.SaveTemplatesAsync(cs);
            await using (var osdu = SampleEstate.Context(cs))
            {
                var loader = new DeliveryDocumentLoader();
                foreach (var reference in new[] { "Wellbore@1.0.0", "WellLog@1.4.0" })
                {
                    var yamlText = SampleEstate.MappingYaml(reference);
                    var mapping = loader.ParseMapping(yamlText, $"mappings/{reference}.yaml");
                    osdu.DeliveryMappings.Add(new DeliveryMapping
                    {
                        Id = FlowIdentity.FromName($"delivery-mapping/{repoId:N}/{reference}"),
                        RepoId = repoId,
                        Reference = reference,
                        Name = mapping.Name,
                        Version = mapping.Version,
                        Kind = mapping.Kind,
                        TemplateVersion = mapping.Template.Version,
                        RelativePath = $"mappings/{reference}.yaml",
                        ContentHash = new string('0', 64),
                        Yaml = yamlText,
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                    });
                }

                await osdu.SaveChangesAsync();
            }

            using (var ordered = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/interfaces"))
            {
                Assert.Equal(HttpStatusCode.OK, ordered.StatusCode);
                var items = JsonDocument.Parse(await ordered.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();
                Assert.Equal((1, 2), (items[0].GetProperty("wave").GetInt32(), items[1].GetProperty("wave").GetInt32()));
                var wait = Assert.Single(items[1].GetProperty("waitsFor").EnumerateArray().ToList());
                Assert.Equal(("wellbores", "schema"), (wait.GetProperty("interface").GetString(), wait.GetProperty("origin").GetString()));
                Assert.StartsWith("osdu.data.WellboreID refers to master-data--Wellbore, which wellbores delivers", wait.GetProperty("why").GetString(), StringComparison.Ordinal);
                Assert.All(items, i => Assert.Equal(JsonValueKind.Null, i.GetProperty("orderProblem").ValueKind));
            }

            // The source as a whole adds its interfaces up; one interface is asked for by name.
            var total = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/stats");
            Assert.Equal((2, 1L, Guid.Empty), (total.GetProperty("interfaces").GetInt32(), total.GetProperty("total").GetInt64(), total.GetProperty("flowId").GetGuid()));
            Assert.Equal(JsonValueKind.Null, total.GetProperty("interface").ValueKind);
            var logs = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/stats?interface=welllogs");
            Assert.Equal((logsLedger, "welllogs", 1L), (logs.GetProperty("flowId").GetGuid(), logs.GetProperty("interface").GetString(), logs.GetProperty("pending").GetInt64()));

            // A listing of a source's records is about one interface, and says so when it is not told which.
            using (var nameless = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/records"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, nameless.StatusCode);
                Assert.Contains("delivers 2 interfaces (wellbores, welllogs); name the one this request is about with ?interface=", await nameless.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var unknown = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/records?interface=cores"))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            }

            // The audit trail of a source is read the same way: one interface at a time, named the same way.
            using (var namelessActivity = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/activities?pipelineId={pipelineId:D}"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, namelessActivity.StatusCode);
                Assert.Contains("name the one this request is about with ?interface=", await namelessActivity.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var unknownActivity = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/activities?pipelineId={pipelineId:D}&interface=cores"))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknownActivity.StatusCode);
            }

            var activities = await JsonAsync(client, token, $"/api/v1/delivery/activities?pipelineId={pipelineId:D}&interface=welllogs");
            Assert.Equal(0, activities.GetProperty("items").GetArrayLength());

            var records = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/records?interface=welllogs");
            Assert.Equal(key.Value, Assert.Single(records.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());
            var target = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/target?interface=welllogs");
            Assert.Equal(("welllogs", "OsduWellLog"), (target.GetProperty("interface").GetString(), target.GetProperty("protocol").GetString()));

            // The well logs' mapping is synced, so the target names the collection of the kind it renders.
            Assert.Equal(
                "work-product-component--WellLog records go to the welllogs collection of the DDMS 'wellbore' (/api/os-wellbore-ddms).",
                target.GetProperty("ddms").GetString());
            Assert.Equal("/api/os-wellbore-ddms/ddms/v3/welllogs/{id}", target.GetProperty("recordPath").GetString());
            Assert.Equal("DELETE", target.GetProperty("recordMethod").GetString());
            Assert.Equal("/api/os-wellbore-ddms/ddms/v3/welllogs/{id}?purge=true", target.GetProperty("everythingPath").GetString());
            Assert.Equal("/api/storage/v2/records/{id}/versions", target.GetProperty("historyPath").GetString());
            var storageTarget = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/target?interface=wellbores");
            Assert.Equal(JsonValueKind.Null, storageTarget.GetProperty("ddms").ValueKind);
            Assert.Equal("POST", storageTarget.GetProperty("recordMethod").GetString());

            // A record leads to its pipeline and interface, and a task queued for it names the interface the node acts through.
            var record = await JsonAsync(client, token, $"/api/v1/delivery/records/{logsLedger:D}/{key.Value:D}");
            Assert.Equal((pipelineId, flowName, "welllogs"), (record.GetProperty("pipelineId").GetGuid(), record.GetProperty("flowName").GetString(), record.GetProperty("interface").GetString()));
            using (var read = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/records/{logsLedger:D}/{key.Value:D}/read"))
            {
                Assert.True(read.StatusCode == HttpStatusCode.Accepted, await read.Content.ReadAsStringAsync());
                var taskId = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement.GetProperty("taskId").GetGuid();
                await using var db = CatalogDatabase.Create(cs);
                var task = await db.ComputeTasks.AsNoTracking().SingleAsync(t => t.TaskId == taskId);
                Assert.Equal(flowName, task.SourceRef);
                Assert.Contains("\"interface\":\"welllogs\"", task.ArgumentsJson.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            }
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryRecords.Where(r => r.FlowId == logsLedger).ExecuteDeleteAsync();
                await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
                await osdu.DeliveryMappings.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.ComputeTasks.Where(t => t.SourceRef == flowName).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }
    }

    private static async Task WaitForInterfacesAsync(string cs, Guid repoId, int expected)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            await using var osdu = SampleEstate.Context(cs);
            if (await osdu.DeliveryInterfaces.CountAsync(i => i.RepoId == repoId) >= expected)
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"The control plane did not describe the {expected} interface(s) of repository {repoId:D} within 30 seconds of starting.");
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
