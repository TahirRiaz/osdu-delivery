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
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Reversals from the API (docs/reversal-plan.md): what reversing a run or a submission would reach, decided as the run
/// decides each record; the reverse run queued as the caller, refused when the source moved since it was shown; and what the
/// ledger keeps of a reversal, its counts and its records by outcome.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryReversalApiTests
{
    [Fact]
    public async Task A_run_or_a_submission_is_previewed_reversed_and_read_back()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-reverse-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-reverse-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var flowId = FlowId.Of(flowName);
        var now = DateTime.UtcNow;
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.arc.Wellbore, key: [facility_name] }
              work: ../.work/reverse
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
                Id = repoId, Name = "cp-reverse-" + suffix, RemoteUrl = "https://example/cp-reverse.git",
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

        // Two runs: the first created three wellbores, the second updated one of them and created a fourth.
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId, TestLedgers.Partition, flowName);
        var firstRun = Guid.CreateVersion7();
        var secondRun = Guid.CreateVersion7();
        var first = await SubmissionAsync(ledger, flowId, flowName, firstRun, now.AddHours(-2));
        var second = await SubmissionAsync(ledger, flowId, flowName, secondRun, now.AddHours(-1));
        var keys = Enumerable.Range(1, 4).Select(i => DeliveryKey.Derive("reverse", [$"W-{suffix}-{i}"]).Value).ToList();
        await using (var osdu = SampleEstate.Context(cs))
        {
            var partition = await osdu.DeliveryLedgers.AsNoTracking().Where(l => l.FlowId == flowId).Select(l => l.PartitionId).SingleAsync();
            foreach (var (key, i) in keys.Select((k, i) => (k, i)))
            {
                var id = $"dev:master-data--Wellbore:{key:N}";
                var version = i == 0 ? 2L : 1L;
                osdu.DeliveryRecords.Add(new DeliveryRecord
                {
                    PartitionId = partition, FlowId = flowId, DeliveryKey = key, SourceKey = $"W-{suffix}-{i}", SourceKeyJson = $"[\"W-{suffix}-{i}\"]",
                    MappingName = "Wellbore", Status = "delivered", TargetId = id, ClaimedTargetId = id, TargetVersion = version, MetadataHash = "hash-" + i,
                    LastSubmissionId = i is 0 or 3 ? second : first, LastDeliveredUtc = now, CreatedUtc = now, UpdatedUtc = now,
                });
                if (i < 3)
                {
                    osdu.DeliveryAttempts.Add(Delivered(partition, flowId, key, first, firstRun, 1, null, now.AddHours(-2)));
                }

                if (i == 0)
                {
                    osdu.DeliveryAttempts.Add(Delivered(partition, flowId, key, second, secondRun, 2, 1, now.AddHours(-1)));
                }

                if (i == 3)
                {
                    osdu.DeliveryAttempts.Add(Delivered(partition, flowId, key, second, secondRun, 1, null, now.AddHours(-1)));
                }
            }

            await osdu.SaveChangesAsync();
        }

        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var flow = $"/api/v1/delivery/flows/{pipelineId:D}";

            // The second run's reversal reaches two records: one to restore to the version before it, one to remove again.
            var byRun = await JsonAsync(client, token, HttpMethod.Post, $"{flow}/reverse/preview", $$"""{"runId":"{{secondRun:D}}"}""");
            Assert.Equal(("run", 2L, 2, true), (byRun.GetProperty("source").GetString(), byRun.GetProperty("records").GetInt64(), byRun.GetProperty("sampled").GetInt32(), byRun.GetProperty("sampleIsAll").GetBoolean()));
            Assert.Equal((1, 1, 0), (byRun.GetProperty("restore").GetInt32(), byRun.GetProperty("remove").GetInt32(), byRun.GetProperty("resolvedFromOsdu").GetInt32()));
            Assert.True(byRun.GetProperty("route").GetProperty("restores").GetBoolean());
            Assert.Equal("storage", byRun.GetProperty("route").GetProperty("protocol").GetString());
            Assert.True(!byRun.TryGetProperty("existing", out var none) || none.ValueKind == JsonValueKind.Null);

            // By submission, the first one: three records it created, which the second run moved one of on since.
            var bySubmission = await JsonAsync(client, token, HttpMethod.Post, $"{flow}/reverse/preview", $$"""{"submissionId":"{{first:D}}"}""");
            Assert.Equal(3L, bySubmission.GetProperty("records").GetInt64());
            Assert.Equal(2, bySubmission.GetProperty("remove").GetInt32());
            Assert.Equal(1, bySubmission.GetProperty("passedOver").GetProperty("superseded").GetInt32());

            // One source, never both or neither, and a source of another ledger is not this flow's to reverse.
            await StatusAsync(client, token, HttpMethod.Post, $"{flow}/reverse/preview", HttpStatusCode.BadRequest, $$"""{"runId":"{{secondRun:D}}","submissionId":"{{first:D}}"}""");
            await StatusAsync(client, token, HttpMethod.Post, $"{flow}/reverse", HttpStatusCode.BadRequest, "{}");
            await StatusAsync(client, token, HttpMethod.Post, $"{flow}/reverse/preview", HttpStatusCode.NotFound, $$"""{"submissionId":"{{Guid.NewGuid():D}}"}""");

            // A request whose count no longer holds is refused; one that holds queues the reverse run as the caller.
            await StatusAsync(client, token, HttpMethod.Post, $"{flow}/reverse", HttpStatusCode.Conflict, $$"""{"runId":"{{secondRun:D}}","expected":5}""");
            var accepted = await JsonAsync(client, token, HttpMethod.Post, $"{flow}/reverse", $$"""{"runId":"{{secondRun:D}}","expected":2}""", HttpStatusCode.Accepted);
            Assert.Equal(2L, accepted.GetProperty("records").GetInt64());
            var runId = accepted.GetProperty("runId").GetGuid();
            await using (var db = CatalogDatabase.Create(cs))
            {
                var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
                Assert.Equal("reverse", run.Operation);
                Assert.Equal(pipelineId, run.PipelineId);
                Assert.Contains($"\"runId\":\"{secondRun:D}\"", run.Payload!, StringComparison.Ordinal);
            }

            // What the ledger keeps of a reversal, once one is opened and listed.
            var reversal = await ledger.OpenReversalAsync(flowId, flowName, ReversalSource.Run(secondRun), "user:tester", runId, now);
            await ledger.CaptureReversalAsync(reversal.ReversalId, null);
            var listed = await JsonAsync(client, token, HttpMethod.Get, $"{flow}/reversals");
            var only = Assert.Single(listed.EnumerateArray());
            Assert.Equal(reversal.ReversalId, only.GetProperty("reversalId").GetInt64());
            Assert.True(!only.TryGetProperty("records", out var uncounted) || uncounted.ValueKind == JsonValueKind.Null);
            var found = await JsonAsync(client, token, HttpMethod.Get, $"{flow}/reversals?runId={secondRun:D}");
            Assert.Equal(2L, Assert.Single(found.EnumerateArray()).GetProperty("records").GetInt64());
            var detail = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/reversals/{reversal.ReversalId}");
            Assert.Equal(pipelineId, detail.GetProperty("pipelineId").GetGuid());
            Assert.Equal(2L, detail.GetProperty("reversal").GetProperty("states").GetProperty("pending").GetInt64());
            Assert.Equal([second], detail.GetProperty("submissionIds").EnumerateArray().Select(s => s.GetGuid()).ToList());
            var pending = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/reversals/{reversal.ReversalId}/records?outcome=pending&limit=1");
            var page = pending.GetProperty("items");
            Assert.Equal(1, page.GetArrayLength());
            Assert.Equal(JsonValueKind.String, pending.GetProperty("next").ValueKind);
            Assert.StartsWith($"W-{suffix}-", page[0].GetProperty("sourceKey").GetString(), StringComparison.Ordinal);
            await StatusAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/reversals/{reversal.ReversalId}/records?outcome=nonsense", HttpStatusCode.BadRequest);
            await StatusAsync(client, token, HttpMethod.Get, "/api/v1/delivery/reversals/987654321", HttpStatusCode.NotFound);

            // The preview now names the reversal of the source, counted.
            var again = await JsonAsync(client, token, HttpMethod.Post, $"{flow}/reverse/preview", $$"""{"runId":"{{secondRun:D}}"}""");
            Assert.Equal(reversal.ReversalId, again.GetProperty("existing").GetProperty("reversalId").GetInt64());
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryReversalItems.Where(i => osdu.DeliveryReversals.Any(r => r.FlowId == flowId && r.ReversalId == i.ReversalId)).ExecuteDeleteAsync();
                await osdu.DeliveryReversals.Where(r => r.FlowId == flowId).ExecuteDeleteAsync();
                await osdu.DeliveryAttempts.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
                await osdu.DeliveryActivityRecords.Where(l => l.FlowId == flowId).ExecuteDeleteAsync();
                await osdu.DeliveryActivities.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
                await osdu.DeliveryRecords.Where(r => r.FlowId == flowId).ExecuteDeleteAsync();
                await osdu.DeliverySubmissions.Where(s => s.FlowId == flowId).ExecuteDeleteAsync();
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

    private static async Task<Guid> SubmissionAsync(OsduLedger ledger, Guid flowId, string flowName, Guid runId, DateTime received)
    {
        var (submission, _) = await ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = Guid.CreateVersion7(),
            FlowId = flowId,
            FlowName = flowName,
            MappingReference = "Wellbore@1.0.0",
            RenderContext = "{}",
            Status = SubmissionStatus.Completed,
            Kind = SubmissionKinds.Incremental,
            SourceConnection = "${env:OSDU_DATA_DB}",
            SourceObject = "OsduData.arc.Wellbore",
            RunId = runId,
            ReceivedUtc = received,
        });
        return submission.SubmissionId;
    }

    /// <summary>A delivered attempt of a record, saying which version it replaced (none for one it created).</summary>
    private static DeliveryAttempt Delivered(short partition, Guid flowId, Guid key, Guid submission, Guid run, long version, long? replaced, DateTime at) => new()
    {
        PartitionId = partition,
        FlowId = flowId,
        DeliveryKey = key,
        SubmissionId = submission,
        RunId = run,
        Worker = "test",
        StartedUtc = at,
        CompletedUtc = at,
        Outcome = "delivered",
        Phase = "metadata",
        TargetVersion = version,
        MetadataHash = "hash-" + version.ToString(CultureInfo.InvariantCulture),
        ResultJson = AttemptResult.WithReplaced(null, replaced),
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
