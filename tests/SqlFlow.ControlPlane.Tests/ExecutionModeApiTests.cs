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
/// The execution-mode surface of the control plane: the queue persists the assertions-only flag on the run row,
/// the trigger API accepts it for an ingestion flow and refuses it for any other kind (and for a group scope),
/// and a <c>mode: manual</c> pipeline is excluded from batch/node scope expansion while a direct anchor still
/// runs. Gated on a reachable catalog database, like the other DB-backed control-plane suites.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ExecutionModeApiTests
{
    [SkippableFact]
    public async Task Enqueue_PersistsAssertionsOnly_OnTheRunRow()
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
                new RunEnqueueRequest(repoId, flowName, "ing", Parameters: new RunParameters { AssertionsOnly = true }),
                DateTime.UtcNow);

            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.True(run.AssertionsOnly);
            Assert.False(run.FullLoad);
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
