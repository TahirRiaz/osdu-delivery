using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The per-run parameters through the control plane: the queue persists them on the run row (the operation and
/// force as columns, the full set as JSON, so the history says exactly what was asked), the enqueue validates them,
/// and the trigger API accepts a valid request, rejects an invalid one at the boundary, and surfaces the parameters
/// on the run detail. Gated on a reachable catalog database, like the other DB-backed control-plane suites.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunParameterApiTests
{
    [SkippableFact]
    public async Task Enqueue_PersistsParameters_OnTheRunRow()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var repoId = Guid.NewGuid();
        var flowName = $"rp-queue-{Guid.NewGuid():N}";
        var submission = Guid.NewGuid();
        var key = Guid.NewGuid();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(
                db,
                new RunEnqueueRequest(repoId, flowName, "delivery", Parameters: new RunParameters
                {
                    Operation = RunParameters.VerifyOperation,
                    Force = true,
                    SubmissionId = submission,
                    RecordKeys = [key],
                    Values = new Dictionary<string, string> { ["logSource"] = "north" },
                }),
                DateTime.UtcNow);

            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.Equal("verify", run.Operation);
            Assert.True(run.Force);
            Assert.Equal(submission, run.SubmissionId);
            Assert.NotNull(run.ParametersJson);
            var restored = RunParameters.FromJson(run.ParametersJson);
            Assert.Equal([key], restored.RecordKeys);
            Assert.Equal("north", restored.Values["logSource"]);

            // The defaults are stored as no JSON at all: an unparameterized run reads back as exactly that.
            var plainId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "delivery"), DateTime.UtcNow);
            var plain = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == plainId);
            Assert.Equal("deliver", plain.Operation);
            Assert.False(plain.Force);
            Assert.Null(plain.ParametersJson);
            Assert.True(RunParameters.FromJson(plain.ParametersJson).IsDefault);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Enqueue_RejectsInvalidParameters_BeforeQueueing()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var repoId = Guid.NewGuid();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(() => RunQueueStore.EnqueueAsync(
                db,
                new RunEnqueueRequest(repoId, "bad", "delivery", Parameters: new RunParameters { Operation = "backfill" }),
                DateTime.UtcNow));

            // Nothing was queued for the repo.
            Assert.Equal(0, await db.Runs.AsNoTracking().CountAsync(r => r.RepoId == repoId));
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task TriggerApi_AcceptsParameters_AndTheRunDetailShowsThem()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var name = $"rp-api-{Guid.NewGuid():N}";
        Guid repoId;

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                repoId = SqlFlow.Core.Identity.FlowIdentity.FromName($"repo/{name}");
                var now = DateTime.UtcNow;
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = name, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = CatalogIdentity.Pipeline(repoId, "flow-a"),
                    RepoId = repoId,
                    Name = "flow-a",
                    Kind = "delivery",
                    RelativePath = "flow-a.flow.yaml",
                    Active = true,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
                await db.SaveChangesAsync();
            }

            using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // An unknown operation is refused at the boundary.
            using var invalid = await SendAsync(client, token, new
            {
                repoId,
                flowName = "flow-a",
                operation = "backfill",
            });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            // A known-state publication with a record scope is contradictory and refused too.
            using var contradictory = await SendAsync(client, token, new
            {
                repoId,
                flowName = "flow-a",
                operation = "known-state",
                recordKeys = new[] { Guid.NewGuid() },
            });
            Assert.Equal(HttpStatusCode.BadRequest, contradictory.StatusCode);

            // A valid request is accepted, and the run detail carries the parameters for the audit trail.
            var drop = "abfss://drops@lake/recall/2026-09-01";
            using var accepted = await SendAsync(client, token, new
            {
                repoId,
                flowName = "flow-a",
                pool = "rp-unserved",
                operation = "plan",
                force = true,
                drop,
                values = new Dictionary<string, string> { ["logSource"] = "north" },
            });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var run = await accepted.Content.ReadFromJsonAsync<RunTriggerAccepted>();
            Assert.NotNull(run);

            using var detailRequest = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/runs/{run.RunId}", UriKind.Relative));
            detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var detail = await client.SendAsync(detailRequest);
            var body = await detail.Content.ReadFromJsonAsync<RunDetailDto>();
            Assert.NotNull(body);
            Assert.Equal("plan", body.Operation);
            Assert.True(body.Force);
            Assert.NotNull(body.ParametersJson);
            var stored = RunParameters.FromJson(body.ParametersJson);
            Assert.Equal(drop, stored.Drop);
            Assert.Equal("north", stored.Values["logSource"]);
        }
        finally
        {
            await CleanupAsync(cs, SqlFlow.Core.Identity.FlowIdentity.FromName($"repo/{name}"));
        }
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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/runs", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task CleanupAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }
}
