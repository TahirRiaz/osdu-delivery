using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The schedule API end to end through the in-memory host: authorization (a read token cannot create a schedule),
/// validation (a malformed cron is a 400 before any database work), the not-found path, the full create -> list ->
/// get -> pause -> resume -> delete lifecycle, and an end-to-end proof that the scheduler actually fires a due
/// schedule by enqueuing a run for its pipeline. DB-backed tests seed and remove their own repo's rows.
/// </summary>
public sealed class ScheduleApiTests
{
    [Fact]
    public async Task CreateSchedule_WithReadOnlyToken_Returns403()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read"]);

        using var response = await PostAsync(client, token, "/api/v1/schedules",
            new CreateScheduleRequest(Guid.NewGuid(), "flow", "0 6 * * *", null, "UTC", true));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateSchedule_WithMalformedCron_Returns400_BeforeTouchingTheDatabase()
    {
        // Validation runs before the pipeline lookup, so a bad cron is a 400 against the placeholder connection.
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostAsync(client, token, "/api/v1/schedules",
            new CreateScheduleRequest(Guid.NewGuid(), "flow", "not a cron", null, "UTC", true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CreateSchedule_ForUnknownPipeline_Returns404()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostAsync(client, token, "/api/v1/schedules",
            new CreateScheduleRequest(FlowIdentity.FromName("nope_" + Guid.NewGuid().ToString("N")), "no_such", "0 6 * * *", null, "UTC", true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CreateSchedule_ForActivePipeline_RunsTheFullLifecycle()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await SeedActivePipeline(cs, repoId, flowName);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            // Create.
            Guid id;
            using (var create = await PostAsync(client, token, "/api/v1/schedules",
                new CreateScheduleRequest(repoId, flowName, "0 6 * * *", null, "Europe/Oslo", true)))
            {
                Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                var created = await create.Content.ReadFromJsonAsync<ScheduleCreated>();
                Assert.NotNull(created);
                Assert.NotEqual(Guid.Empty, created.Id);
                Assert.NotNull(created.NextFireUtc);
                id = created.Id;
            }

            // Get + list reflect it.
            var got = await GetJsonAsync<ScheduleDto>(client, token, $"/api/v1/schedules/{id}");
            Assert.Equal("api", got.Source);
            Assert.False(got.Paused);
            Assert.Equal("Europe/Oslo", got.Timezone);

            var list = await GetJsonAsync<PagedResult<ScheduleDto>>(client, token, $"/api/v1/schedules?repoId={repoId}");
            Assert.Contains(list.Items, s => s.Id == id);

            // Pause then resume toggles the operational flag and re-arms the next fire.
            using (var pause = await PostAsync(client, token, $"/api/v1/schedules/{id}/pause", null))
            {
                Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
                var paused = await pause.Content.ReadFromJsonAsync<ScheduleDto>();
                Assert.True(paused!.Paused);
            }

            using (var resume = await PostAsync(client, token, $"/api/v1/schedules/{id}/resume", null))
            {
                Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
                var resumed = await resume.Content.ReadFromJsonAsync<ScheduleDto>();
                Assert.False(resumed!.Paused);
                Assert.NotNull(resumed.NextFireUtc);
            }

            // Delete, then it is gone.
            using (var del = await SendAsync(client, token, HttpMethod.Delete, $"/api/v1/schedules/{id}"))
            {
                Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
            }

            using var afterDelete = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/schedules/{id}");
            Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Scheduler_FiresADueSchedule_EnqueuingARunForThePipeline()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var scheduleId = Guid.NewGuid();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await SeedActivePipeline(cs, repoId, flowName);

            // A schedule already past due, with a daily cron so it fires exactly once during the test (the fire
            // advances the next occurrence to tomorrow). Seeded directly so we control the due time.
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Schedules.Add(new CatalogSchedule
                {
                    Id = scheduleId,
                    RepoId = repoId,
                    PipelineId = pipelineId,
                    FlowName = flowName,
                    Cron = "0 6 * * *",
                    Timezone = "UTC",
                    Enabled = true,
                    Source = "api",
                    NextFireUtc = DateTime.UtcNow.AddMinutes(-1),
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            // The scheduler (1s tick in tests) should fire the schedule: its LastRunId gets set and its next fire
            // advances into the future.
            ScheduleDto? schedule = null;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                schedule = await GetJsonAsync<ScheduleDto>(client, token, $"/api/v1/schedules/{scheduleId}");
                if (schedule.LastRunId is not null)
                {
                    break;
                }

                await Task.Delay(250);
            }

            Assert.NotNull(schedule);
            Assert.NotNull(schedule.LastRunId);
            Assert.NotNull(schedule.NextFireUtc);
            Assert.True(schedule.NextFireUtc > DateTime.UtcNow, "the next fire should have advanced into the future");

            // A run was enqueued for the schedule's pipeline.
            var runs = await GetJsonAsync<PagedResult<RunSummaryDto>>(client, token, $"/api/v1/runs?pipelineId={pipelineId}");
            Assert.True(runs.Total >= 1, "the fire should have enqueued at least one run for the pipeline");
            Assert.Contains(runs.Items, r => r.RunId == schedule.LastRunId);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    private static async Task SeedActivePipeline(string cs, Guid repoId, string flowName)
    {
        var now = DateTime.UtcNow;
        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId,
            Name = "sch_repo_" + repoId.ToString("N")[..8],
            RootPath = Path.Combine(Path.GetTempPath(), "sch_" + repoId.ToString("N")[..8]),
            FirstSeenUtc = now,
            LastSyncUtc = now,
        });
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = CatalogIdentity.Pipeline(repoId, flowName),
            RepoId = repoId,
            Name = flowName,
            Kind = "file",
            RelativePath = "flows/" + flowName + ".flow.yaml",
            ContentHash = new string('0', 64),
            Yaml = "name: " + flowName + "\n",
            DefinitionJson = "{}",
            Active = true,
            Wave = 0,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });
        await db.SaveChangesAsync();
    }

    private static (Guid RepoId, string FlowName) NewIds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("sch_" + suffix), "sch_flow_" + suffix);
    }

    private static async Task Cleanup(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string url, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string url)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, url);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
