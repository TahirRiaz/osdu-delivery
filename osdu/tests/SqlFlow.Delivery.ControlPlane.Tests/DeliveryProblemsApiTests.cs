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
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A flow's problems as the API serves them (docs/ledger.md, Problems; docs/operations.md, The API): the blocked records
/// grouped by the problem their errors share, one problem with the files its records came from, the records listing
/// narrowed to a problem, the release of a problem's records with each record naming the release in its history, and one
/// record released and tried at once.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryProblemsApiTests
{
    /// <summary>The hold of a record whose wellbore the cache does not hold: one problem, whatever the wellbore.</summary>
    private static string MissingWellbore(string wellbore) => $"osdu.data.WellboreID: '{wellbore}' matches no cached Wellbore, and the entry is required";

    private const string EmptyTag = "osdu.tags.Tag4: dataset.tag4 is empty, and the entry is required";

    [Fact]
    public async Task A_flow_s_problems_are_listed_opened_narrowed_to_and_released_together()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-problems-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-problems-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var flowId = FlowId.Of(flowName);
        var now = DateTime.UtcNow;
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.arc.Wellbore, key: [facility_name] }
              work: ../.work/problems
            render:
              mapping: Wellbore@1.0.0
            target:
              endpoint: ${env:OSDU_URL}
              protocol: storage
              headers: { data-partition-id: dev }
            """;

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-problems-" + suffix, RemoteUrl = "https://example/cp-problems.git",
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
        await ledger.RegisterAsync(flowId, TestLedgers.Partition, flowName);
        var wellbores = Enumerable.Range(1, 3)
            .Select(i => Held(flowId, $"W-{suffix}-{i}", MissingWellbore($"WB-{i}"), i < 3 ? "wells.csv" : "late.csv"))
            .ToList();
        var tag = Held(flowId, $"T-{suffix}", EmptyTag, "wells.csv");
        await ledger.MarkHeldAsync(flowId, [.. wellbores, tag]);
        var missing = ProblemSignature.Format(ProblemSignature.Of(wellbores[0].LastError));

        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The problems, the most records first, each with its pattern, counts and example.
            var listing = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/problems");
            Assert.Equal((2L, 4L, 0L), (listing.GetProperty("totalProblems").GetInt64(), listing.GetProperty("totalRecords").GetInt64(), listing.GetProperty("unsorted").GetInt64()));
            var first = listing.GetProperty("problems")[0];
            Assert.Equal(missing, first.GetProperty("problem").GetString());
            Assert.Equal("osdu.data.WellboreID: '<value>' matches no cached Wellbore, and the entry is required", first.GetProperty("pattern").GetString());
            Assert.Equal((3L, 3L, 0L), (first.GetProperty("records").GetInt64(), first.GetProperty("held").GetInt64(), first.GetProperty("failed").GetInt64()));
            var example = first.GetProperty("example");
            Assert.Equal(missing, example.GetProperty("problem").GetString());
            Assert.Contains(example.GetProperty("deliveryKey").GetGuid(), wellbores.Select(w => w.DeliveryKey.Value));

            // One problem, with the files its records came from.
            var detail = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/problems/{missing}");
            Assert.Equal(
                [("wells.csv", 2L), ("late.csv", 1L)],
                detail.GetProperty("files").EnumerateArray().Select(f => (f.GetProperty("fileName").GetString(), f.GetProperty("records").GetInt64())).ToList());
            await StatusAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/problems/not-a-problem", HttpStatusCode.BadRequest);
            await StatusAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/problems/{new string('0', 16)}", HttpStatusCode.NotFound);

            // The records listing narrowed to the problem lists its records alone; a problem it cannot read is refused.
            var records = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/records?problem={missing}&pageSize=50");
            Assert.Equal(3, records.GetProperty("total").GetInt64());
            Assert.All(records.GetProperty("items").EnumerateArray(), r => Assert.Equal(missing, r.GetProperty("problem").GetString()));
            await StatusAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/records?problem=xyz", HttpStatusCode.BadRequest);

            // Released together: every record of the problem, the other problem's left blocked.
            var released = await JsonAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/problems/{missing}/release", "{}");
            Assert.Equal((missing, 3, JsonValueKind.Null), (released.GetProperty("problem").GetString(), released.GetProperty("released").GetInt32(), released.GetProperty("runId").ValueKind));
            var after = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/problems");
            Assert.Equal((1L, 1L), (after.GetProperty("totalProblems").GetInt64(), after.GetProperty("totalRecords").GetInt64()));
            await StatusAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/problems/{missing}/release", HttpStatusCode.NotFound, "{}");

            // Each record of it names the release in its own history: who asked, for which problem.
            foreach (var wellbore in wellbores)
            {
                var activities = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/records/{flowId:D}/{wellbore.DeliveryKey.Value:D}/activities");
                var release = Assert.Single(activities.EnumerateArray().ToList());
                Assert.Equal(("release", JsonValueKind.Null), (release.GetProperty("kind").GetString(), release.GetProperty("deliveryKey").ValueKind));
                Assert.Contains(missing, release.GetProperty("parametersJson").GetString(), StringComparison.Ordinal);
                Assert.StartsWith("released 3 record(s)", release.GetProperty("summary").GetString(), StringComparison.Ordinal);
            }

            // One record released and tried at once: the release here, and a deliver run scoped to it queued.
            var tried = await JsonAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/records/{flowId:D}/{tag.DeliveryKey.Value:D}/release", """{"run":true}""", HttpStatusCode.Accepted);
            Assert.Equal(1, tried.GetProperty("released").GetInt32());
            var runId = tried.GetProperty("runId").GetGuid();
            await using (var db = CatalogDatabase.Create(cs))
            {
                var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
                Assert.Equal(pipelineId, run.PipelineId);
                Assert.Contains(tag.DeliveryKey.Value.ToString("D"), run.Payload, StringComparison.Ordinal);
            }

            Assert.Equal(0L, (await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/problems")).GetProperty("totalRecords").GetInt64());
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryActivityRecords.Where(l => l.FlowId == flowId).ExecuteDeleteAsync();
                await osdu.DeliveryActivities.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
                await osdu.DeliveryAttempts.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
                await osdu.DeliveryRecordIdentities.Where(i => i.FlowId == flowId).ExecuteDeleteAsync();
                await osdu.DeliveryRecords.Where(r => r.FlowId == flowId).ExecuteDeleteAsync();
                await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
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

    private static RecordState Held(Guid flowId, string sourceKey, string error, string file) => new()
    {
        DeliveryKey = DeliveryKey.Derive("problems", [sourceKey]),
        FlowId = flowId,
        SourceKey = sourceKey,
        MappingName = "Wellbore",
        LastSubmissionId = Guid.NewGuid(),
        PendingSourceFileName = file,
        PendingSourceRowNumber = 1,
        LastError = error,
    };

    private static async Task<JsonElement> JsonAsync(
        HttpClient client, string token, HttpMethod method, string path, string? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await SendAsync(client, token, method, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{method} {path} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task StatusAsync(HttpClient client, string token, HttpMethod method, string path, HttpStatusCode expected, string? body = null)
    {
        using var response = await SendAsync(client, token, method, path, body);
        Assert.True(response.StatusCode == expected, $"{method} {path} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, string? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
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
