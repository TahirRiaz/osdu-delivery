using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Deleting a flow's ledger from the API (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting the ledger):
/// nothing is queued without the partition the ledger is kept in named as confirmation, a flow that never kept a ledger
/// has nothing to delete, and the run that deletes it is queued once, as a run of the pipeline carrying the
/// confirmation the node checks again.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryLedgerApiTests
{
    [Fact]
    public async Task Deleting_the_ledger_is_queued_once_as_a_run_of_the_pipeline_naming_its_partition()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-ledger-" + suffix;
        var unrunName = "api-ledger-unrun-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-ledger-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var unrunId = CatalogIdentity.Pipeline(repoId, unrunName);
        var flowId = FlowId.Of(flowName);
        var now = DateTime.UtcNow;

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-ledger-" + suffix, RemoteUrl = "https://example/cp-ledger.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, now));
            db.Pipelines.Add(Pipeline(unrunId, repoId, unrunName, now));
            await db.SaveChangesAsync();
        }

        // The flow's partition is its header's: the ledger's directory says where its ledger is kept once a run kept one.
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId, TestLedgers.Partition, flowName);

        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var path = $"/api/v1/delivery/flows/{pipelineId:D}/ledger/delete";

            // Nothing is queued without the partition named, or with another one.
            var missing = await ProblemAsync(client, token, path, "{}", HttpStatusCode.BadRequest);
            Assert.Equal("Confirmation required", missing.GetProperty("title").GetString());
            var other = await ProblemAsync(client, token, path, """{"confirm":"prod"}""", HttpStatusCode.BadRequest);
            Assert.Contains($"is kept in partition '{TestLedgers.Partition}'. Nothing was queued.", other.GetProperty("detail").GetString(), StringComparison.Ordinal);

            // A flow no run has kept a ledger of has nothing to delete.
            var unrun = await ProblemAsync(client, token, $"/api/v1/delivery/flows/{unrunId:D}/ledger/delete", """{"confirm":"dev"}""", HttpStatusCode.Conflict);
            Assert.Equal("Nothing to delete", unrun.GetProperty("title").GetString());

            // Named in any case, the run is queued as the pipeline's, carrying the partition as the ledger spells it.
            var accepted = await JsonAsync(client, token, path, """{"confirm":" DEV "}""", HttpStatusCode.Accepted);
            Assert.Equal(TestLedgers.Partition, accepted.GetProperty("partition").GetString());
            var runId = accepted.GetProperty("runId").GetGuid();
            await using (var db = CatalogDatabase.Create(cs))
            {
                var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
                Assert.Equal((DeliveryOperations.DeleteLedger, pipelineId), (run.Operation, run.PipelineId));
                Assert.Contains($"\"confirm\":\"{TestLedgers.Partition}\"", run.Payload!, StringComparison.Ordinal);
            }

            // While it is queued, a second request is refused rather than queuing a second run.
            var again = await ProblemAsync(client, token, path, """{"confirm":"dev"}""", HttpStatusCode.Conflict);
            Assert.Contains(runId.ToString("D"), again.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryLedgers.Where(l => l.FlowId == flowId).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
                await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
                await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }
    }

    /// <summary>A delivery pipeline in the single form, whose partition is its data-partition-id header.</summary>
    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name, DateTime now) => new()
    {
        Id = id,
        RepoId = repoId,
        Name = name,
        Kind = "delivery",
        RelativePath = "flows/" + name + ".yaml",
        ContentHash = new string('0', 64),
        Yaml = $$"""
            flowType: delivery
            name: {{name}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.arc.Wellbore, key: [facility_name] }
              work: ../.work/ledger
            render:
              mapping: Wellbore@1.0.0
            target:
              endpoint: ${env:OSDU_URL}
              protocol: storage
              headers: { data-partition-id: dev }
            """,
        DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"delivery"}"""),
        Active = true,
        Wave = 0,
        FirstSeenUtc = now,
        LastSeenUtc = now,
    };

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path, string body, HttpStatusCode expected)
    {
        using var response = await SendAsync(client, token, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"POST {path} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<JsonElement> ProblemAsync(HttpClient client, string token, string path, string body, HttpStatusCode expected)
        => JsonAsync(client, token, path, body, expected);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
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
