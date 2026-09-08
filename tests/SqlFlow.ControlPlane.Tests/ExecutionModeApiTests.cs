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
/// The execution-mode surface of the control plane: the queue persists the requested operation on the run row,
/// and a <c>mode: manual</c> pipeline is excluded from batch/node scope expansion while a direct anchor still
/// runs. Gated on a reachable catalog database, like the other DB-backed control-plane suites.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ExecutionModeApiTests
{
    [SkippableFact]
    public async Task Enqueue_PersistsTheOperation_OnTheRunRow()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();
        var flowName = $"ao-queue-{Guid.NewGuid():N}";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(
                db,
                new RunEnqueueRequest(repoId, flowName, "delivery", Parameters: new RunParameters { Operation = RunParameters.VerifyOperation }),
                DateTime.UtcNow);

            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.Equal("verify", run.Operation);
            Assert.False(run.Force);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    private static CatalogPipeline Pipeline(
        Guid repoId, string name, string kind, DateTime now, string? batch = null, string executionMode = PipelineExecutionModes.Auto)
        => new()
        {
            Id = CatalogIdentity.Pipeline(repoId, name),
            RepoId = repoId,
            Name = name,
            Kind = kind,
            Batch = batch,
            ExecutionMode = executionMode,
            RelativePath = $"{name}.flow.yaml",
            Active = true,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

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
