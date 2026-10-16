using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.ControlPlane.Configuration;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A probe of a flow's target that an operator asks for (the GUI's "Probe target", the API, the MCP tool, all through
/// <c>POST /flows/{pipelineId}/probe</c>) takes the scheduled probe's path: it is recorded in the flow's audit trail as a
/// <c>probe</c> activity under the caller's name, closed with what it found, and counted on the probe metric. A probe that
/// could not run is recorded as such, and a probe left open by a host that stopped is closed by the next scheduled pass
/// whoever asked for it, while one that may still be running is left to finish.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class TargetProbeAuditTests
{
    [Fact]
    public async Task A_probe_an_operator_asks_for_is_recorded_under_their_name_and_counted_as_a_scheduled_one_is()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "probe-asked-" + suffix;
        var brokenName = "probe-asked-broken-" + suffix;
        var operator1 = "probe-operator-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-probe-asked-" + suffix);
        var root = Path.Combine(Path.GetTempPath(), "cp-probe-asked-" + suffix);
        var unsetName = "PROBE_ASKED_UNSET_" + suffix.ToUpperInvariant();
        var ledgers = new[] { FlowId.Of(flowName), FlowId.Of(brokenName) };
        var now = DateTime.UtcNow;
        await SeedAsync(cs, repoId, root, now, (flowName, "https://osdu.example.com"), (brokenName, $"${{env:{unsetName}}}"));

        var osdu = new StandInOsdu();
        using var capture = new MetricsCapture();
        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
                .WithServices(services => services.AddSingleton(new TargetClients(TimeProvider.System, osdu, allowLoopback: false)));
            using var client = factory.CreateClient();
            var token = await TokenAsync(client, operator1, ["read", "operate"]);

            // The operator's probe answers with what the target said, as before.
            using (var answer = await PostAsync(client, token, $"/api/v1/delivery/flows/{CatalogIdentity.Pipeline(repoId, flowName):D}/probe"))
            {
                var body = await answer.Content.ReadAsStringAsync();
                Assert.True(answer.StatusCode == HttpStatusCode.OK, body);
                Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("reachable").GetBoolean());
            }

            // And it is in the flow's audit trail under the operator's name, closed with what it found, and counted.
            var asked = Assert.Single(await ActivitiesAsync(cs, [ledgers[0]]));
            Assert.Equal((ScheduledTargetProbeService.ActivityKind, "user:" + operator1, "completed"), (asked.Kind, asked.Actor, asked.Outcome));
            Assert.Equal("reachable: HTTP 200 at /api/storage/v2/info (the service answered)", asked.Summary);
            Assert.NotNull(asked.CompletedUtc);
            Assert.Contains("osdu.example.com", asked.ParametersJson, StringComparison.Ordinal);
            var counted = Assert.Single(capture.Of("osdu_delivery.probes", "flow", flowName));
            Assert.Equal(("reachable", "dev"), (counted.Tags["outcome"], counted.Tags["partition"]));

            // A probe that could not run (a reference nothing resolves) answers why, and is recorded and counted as such.
            using (var answer = await PostAsync(client, token, $"/api/v1/delivery/flows/{CatalogIdentity.Pipeline(repoId, brokenName):D}/probe"))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, answer.StatusCode);
            }

            var broken = Assert.Single(await ActivitiesAsync(cs, [ledgers[1]]));
            Assert.Equal(("user:" + operator1, "failed"), (broken.Actor, broken.Outcome));
            Assert.StartsWith("the probe could not run:", broken.Summary, StringComparison.Ordinal);
            Assert.Contains(unsetName, broken.Summary, StringComparison.Ordinal);
            Assert.Equal(["error"], capture.Of("osdu_delivery.probes", "flow", brokenName).Select(m => m.Tags["outcome"]));

            // Probes left open: one a host stopped under an hour ago, which can no longer report, and one that started a
            // moment ago and may still be running. The next probe of the interface closes the first before it starts, with no
            // schedule running, and leaves the second to finish.
            var ledger = new OsduLedger(() => SampleEstate.Context(cs));
            async Task<long> OpenAsync(int flow, string name, DateTime started)
                => (await ledger.StartActivityAsync(new ActivityRecord
                {
                    FlowId = ledgers[flow], FlowName = name, Kind = ScheduledTargetProbeService.ActivityKind, Actor = "user:" + operator1, StartedUtc = started,
                })).ActivityId;

            var stale = await OpenAsync(0, flowName, now.AddHours(-1));
            var fresh = await OpenAsync(0, flowName, DateTime.UtcNow.AddMinutes(-1));
            using (var answer = await PostAsync(client, token, $"/api/v1/delivery/flows/{CatalogIdentity.Pipeline(repoId, flowName):D}/probe"))
            {
                Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            }

            var afterProbe = await ActivitiesAsync(cs, [ledgers[0]]);
            var closed = afterProbe.Single(a => a.ActivityId == stale);
            Assert.Equal(("failed", "user:" + operator1), (closed.Outcome, closed.Actor));
            Assert.StartsWith("the probe did not finish", closed.Summary, StringComparison.Ordinal);
            Assert.Null(afterProbe.Single(a => a.ActivityId == fresh).CompletedUtc);

            // A scheduled pass closes what is left open in every ledger, a flow it does not cover included.
            var staleBroken = await OpenAsync(1, brokenName, now.AddHours(-1));
            var options = new TargetProbeOptions { Enabled = true, Pipelines = flowName };
            options.Validate();
            var schedule = new ScheduledTargetProbeService(
                factory.Services, TimeProvider.System, Options.Create(options), NullLogger<ScheduledTargetProbeService>.Instance);
            await schedule.ProbePassAsync(CancellationToken.None);

            var closedBroken = (await ActivitiesAsync(cs, [ledgers[1]])).Single(a => a.ActivityId == staleBroken);
            Assert.Equal("failed", closedBroken.Outcome);
            Assert.StartsWith("the probe did not finish", closedBroken.Summary, StringComparison.Ordinal);

            // The pass probed the flow it covers, under the schedule, and left the fresh probe alone.
            var afterPass = await ActivitiesAsync(cs, [ledgers[0]]);
            Assert.Contains(afterPass, a => a.Actor == ScheduledTargetProbeService.ScheduleActor && a.Outcome == "completed");
            Assert.Null(afterPass.Single(a => a.ActivityId == fresh).CompletedUtc);
        }
        finally
        {
            await using (var context = SampleEstate.Context(cs))
            {
                await context.DeliveryActivities.Where(a => ledgers.Contains(a.FlowId)).ExecuteDeleteAsync();
                await context.DeliveryLedgers.Where(l => ledgers.Contains(l.FlowId)).ExecuteDeleteAsync();
                await context.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// A repository of single-form storage flows, each reaching the endpoint given, written where the catalog says the
    /// repository is, since a probe reads the flow as a run does.
    /// </summary>
    private static async Task SeedAsync(string cs, Guid repoId, string root, DateTime now, params (string Name, string Endpoint)[] flows)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "flows")).FullName;
        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo { Id = repoId, Name = "cp-probe-asked-" + repoId.ToString("N")[..10], RemoteUrl = "https://example/cp-probe-asked.git", RootPath = root, FirstSeenUtc = now, LastSyncUtc = now });
        foreach (var (name, endpoint) in flows)
        {
            var yaml = $$"""
                flowType: delivery
                name: {{name}}
                source:
                  connection: ${env:OSDU_DATA_DB}
                  work: ../.work/{{name}}
                  record: { object: OsduData.silver.Wellbore, key: [wellbore_id] }
                render:
                  mapping: Wellbore@1.0.0
                  parameters:
                    dataPartition: dev
                target:
                  protocol: storage
                  endpoint: {{endpoint}}
                  headers: { data-partition-id: dev }
                """;
            await File.WriteAllTextAsync(Path.Combine(folder, name + ".yaml"), yaml);
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = CatalogIdentity.Pipeline(repoId, name),
                RepoId = repoId,
                Name = name,
                Kind = "delivery",
                RelativePath = "flows/" + name + ".yaml",
                ContentHash = new string('0', 64),
                Yaml = yaml,
                DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"delivery"}"""),
                Active = true,
                Wave = 0,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<List<DeliveryActivity>> ActivitiesAsync(string cs, IReadOnlyList<Guid> flowIds)
    {
        await using var osdu = SampleEstate.Context(cs);
        return await osdu.DeliveryActivities.AsNoTracking()
            .Where(a => flowIds.Contains(a.FlowId))
            .OrderBy(a => a.ActivityId)
            .ToListAsync();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client, string subject, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, subject, scopes.ToArray()));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    /// <summary>The platform the probes reach: its storage service answers its info path, and every URL asked is kept.</summary>
    private sealed class StandInOsdu : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            Asked.Enqueue(url);
            var (status, json) = url.AbsolutePath == "/api/storage/v2/info"
                ? (HttpStatusCode.OK, """{"groupId":"org.opengroup.osdu","version":"0.0.0"}""")
                : (HttpStatusCode.NotFound, """{"code":404,"reason":"Not Found","message":"No such path."}""");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
