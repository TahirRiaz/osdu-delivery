using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The platform's run path end to end over a flow of the module's kind: a triggered <c>plan</c> run of the sample
/// wellbore flow, executed by the in-process node, rendering the records the flow's ingestion tables hold. It needs no
/// OSDU target, which is what makes it a safe end-to-end proof that the module's kind really executes. Gated on a
/// reachable catalog database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeliveryPlanRunApiTests
{
    [SkippableFact]
    public async Task A_plan_run_of_the_sample_flow_executes_end_to_end()
    {
        var cs = CatalogTestDb.Require();
        var estate = await Estate.SeedAsync(cs);
        var tables = new MemoryIngestionTables();
        tables.Add(new MemoryRecord
        {
            Row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["RecId"] = 1L,
                ["facility_name"] = "OSDU-DEV-1-A",
                ["facility_description"] = "Sample wellbore A",
                ["facility_id"] = "srn:master-data/Wellbore:A",
                ["update_date"] = new DateTime(2026, 9, 1, 6, 30, 0, DateTimeKind.Utc),
            },
            UpdatedUtc = new DateTime(2026, 9, 1, 6, 30, 0, DateTimeKind.Utc),
            FileName = "wellbore_20260901.csv",
            RowNumber = 1,
        }).AddChild("aliases", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["facility_name"] = "OSDU-DEV-1-A",
            ["alias_name"] = "WB-A",
        });

        try
        {
            await SampleEstate.SaveTemplatesAsync(cs);
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
                .WithServices(services => services.AddSingleton<IIngestionSourceFactory>(tables));
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            Guid runId;
            using (var response = await client.SendAsync(Authorized(
                HttpMethod.Post, "/api/v1/runs", token,
                new RunTriggerRequest(estate.RepoId, SampleEstate.WellboreFlowName, Operation: DeliveryOperations.Plan))))
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                var accepted = await response.Content.ReadFromJsonAsync<RunTriggerAccepted>();
                Assert.NotNull(accepted);
                runId = accepted.RunId;
            }

            string? status = null;
            string? error = null;
            for (var attempt = 0; attempt < 160 && status is not ("succeeded" or "failed" or "cancelled"); attempt++)
            {
                await Task.Delay(250);
                using var detail = await client.SendAsync(Authorized(HttpMethod.Get, $"/api/v1/runs/{runId:D}", token));
                if (detail.StatusCode != HttpStatusCode.OK)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
                status = document.RootElement.GetProperty("status").GetString();
                error = document.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            }

            var log = string.Join(Environment.NewLine, factory.Logs.TakeLast(40));
            Assert.True(status == "succeeded", $"the plan run ended '{status}': {error}{Environment.NewLine}{log}");
            Assert.Equal(1, tables.Opens);

            // The run's own trace says what it planned.
            using var trace = await client.SendAsync(Authorized(HttpMethod.Get, $"/api/v1/runs/{runId:D}/trace?pageSize=200", token));
            Assert.Equal(HttpStatusCode.OK, trace.StatusCode);
            using var timeline = JsonDocument.Parse(await trace.Content.ReadAsStringAsync());
            var messages = timeline.RootElement.GetProperty("items").EnumerateArray()
                .Select(e => e.GetProperty("message").GetString() ?? string.Empty)
                .ToList();
            Assert.NotEmpty(messages);
            Assert.True(
                messages.Exists(m => m.Contains("record", StringComparison.OrdinalIgnoreCase)),
                "the plan's trace never mentioned a record: " + string.Join(" | ", messages));
        }
        finally
        {
            await estate.CleanupAsync(cs);
        }
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        return request;
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

    /// <summary>
    /// The sample wellbore flow in the catalog, in a repository of its own, with the mapping it pins as the repository sync
    /// reconciles it into the module's database. The documents are the estate's own, copied to a temp repository.
    /// </summary>
    private sealed record Estate(Guid RepoId, string Root)
    {
        public static async Task<Estate> SeedAsync(string cs)
        {
            await CatalogDatabase.MigrateAsync(cs);
            await SampleEstate.MigrateModuleAsync(cs);

            var suffix = Guid.NewGuid().ToString("N")[..10];
            var repoName = "cp-plan-run-" + suffix;
            var repoId = FlowIdentity.FromName("repo/" + repoName);
            var root = SampleEstate.CopyTo(Path.Combine(Path.GetTempPath(), "sqlflow_cp_plan_" + suffix));
            var now = DateTime.UtcNow;
            var flow = SampleEstate.WellboreFlowName;

            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = repoName, RemoteUrl = "https://example/" + repoName + ".git", RootPath = root, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = CatalogIdentity.Pipeline(repoId, flow),
                    RepoId = repoId,
                    Name = flow,
                    Kind = "delivery",
                    Batch = "recall",
                    RelativePath = SampleEstate.FlowPath(flow),
                    ContentHash = new string('0', 64),
                    Yaml = SampleEstate.FlowYaml(root, flow),
                    DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{flow}}","flowKind":"delivery"}"""),
                    Active = true,
                    Wave = 0,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
                await db.SaveChangesAsync();
            }

            await using var osdu = SampleEstate.Context(cs);
            osdu.DeliveryMappings.Add(new DeliveryMapping
            {
                Id = FlowIdentity.FromName($"delivery-mapping/{repoId:N}/{SampleEstate.WellboreMapping}"),
                RepoId = repoId,
                Reference = SampleEstate.WellboreMapping,
                Name = "Wellbore",
                Version = "1.0.0",
                Kind = SampleEstate.WellboreTemplateKind,
                TemplateVersion = SampleEstate.WellboreTemplateVersion,
                RelativePath = "mappings/Wellbore@1.0.0.yaml",
                ContentHash = new string('0', 64),
                Yaml = await File.ReadAllTextAsync(Path.Combine(root, "mappings", "Wellbore@1.0.0.yaml")),
                SummaryJson = "{}",
                Status = "valid",
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            await osdu.SaveChangesAsync();

            return new Estate(repoId, root);
        }

        public async Task CleanupAsync(string cs)
        {
            var flowId = FlowId.Of(SampleEstate.WellboreFlowName);
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliverySubmissions.Where(s => s.FlowId == flowId).ExecuteDeleteAsync();
                await osdu.DeliveryActivities.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
                await osdu.DeliveryMappings.Where(m => m.RepoId == RepoId).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.RunEvents.Where(e => e.RepoId == RepoId).ExecuteDeleteAsync();
                await db.Runs.Where(r => r.RepoId == RepoId).ExecuteDeleteAsync();
                await db.RunGroups.Where(g => g.RepoId == RepoId).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == RepoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == RepoId).ExecuteDeleteAsync();
            }

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A transient lock on a run-log file must not fail the test; the temp directory is disposable.
            }
        }
    }
}
