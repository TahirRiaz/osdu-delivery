using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The durable run queue (<see cref="RunQueueStore"/>) against the real catalog database: the queued -> running ->
/// terminal lifecycle, the atomic claim (two workers never get the same run), cancellation of a queued run, fail,
/// and crash recovery of orphaned running runs. The assembly runs serially (see AssemblyInfo), so the only queued
/// run during a test is the one it enqueued, which is what the "claim returns my run" assertions rely on. Each test
/// removes its own repo's rows. Gated on a reachable catalog database, like the other DB-backed tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunQueueStoreTests
{
    private const string Node = "test-node";

    [SkippableFact]
    public async Task Enqueue_Claim_Complete_MovesThroughTheLifecycle()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var dir = NewTempDir();

        try
        {
            await using var db = CatalogDatabase.Create(cs);

            var enqueuedAt = DateTime.UtcNow;
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), enqueuedAt);

            var queued = await Reload(db, runId);
            Assert.Equal(RunStatuses.Queued, queued.Status);
            Assert.Equal(enqueuedAt, queued.EnqueuedUtc);
            Assert.Null(queued.ClaimedByNode);

            var claimed = await RunQueueStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow);
            Assert.Equal(runId, claimed);

            var running = await Reload(db, runId);
            Assert.Equal(RunStatuses.Running, running.Status);
            Assert.Equal(Node, running.ClaimedByNode);
            Assert.NotNull(running.StartUtc);

            // The queue is now empty, so a second claim returns null (the run is running, not queued).
            Assert.Null(await RunQueueStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow));

            var runJson = Path.Combine(dir, "run.json");
            await File.WriteAllTextAsync(runJson, RunArtifact(runId, flowName, success: true, rowsLoaded: 5));
            var recorded = await RunQueueStore.CompleteFromArtifactAsync(db, runId, repoId, runJson, DateTime.UtcNow);
            Assert.True(recorded);

            var done = await Reload(db, runId);
            Assert.Equal(RunStatuses.Succeeded, done.Status);
            Assert.True(done.Success);
            Assert.Equal(5, done.RowsLoaded);
            // The completion preserved the queue-only fields set at enqueue/claim.
            Assert.Equal(enqueuedAt, done.EnqueuedUtc);
            Assert.Equal(Node, done.ClaimedByNode);
        }
        finally
        {
            await Cleanup(cs, repoId, dir);
        }
    }

    [SkippableFact]
    public async Task ClaimNext_ConcurrentClaims_NeverGiveTheSameRunToTwoWorkers()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            Guid runId;
            await using (var seed = CatalogDatabase.Create(cs))
            {
                runId = await RunQueueStore.EnqueueAsync(seed, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            }

            // Two independent contexts (separate connections) claim at the same time. READPAST plus the
            // single-statement claim guarantee exactly one of them gets the run; the other gets null.
            await using var a = CatalogDatabase.Create(cs);
            await using var b = CatalogDatabase.Create(cs);
            var results = await Task.WhenAll(
                RunQueueStore.ClaimNextAsync(a, "node-a", [], DateTime.UtcNow),
                RunQueueStore.ClaimNextAsync(b, "node-b", [], DateTime.UtcNow));

            Assert.Equal(1, results.Count(id => id == runId));
            Assert.Equal(1, results.Count(id => id is null));
        }
        finally
        {
            await Cleanup(cs, repoId, null);
        }
    }

    [SkippableFact]
    public async Task ClaimNext_RoutesByPool_OnlyAnEligibleNodeClaimsATargetedRun()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var pool = "pool_" + Guid.NewGuid().ToString("N")[..6];

        try
        {
            Guid targetedRunId;
            await using (var seed = CatalogDatabase.Create(cs))
            {
                targetedRunId = await RunQueueStore.EnqueueAsync(seed, new RunEnqueueRequest(repoId, flowName, "ing", pool), DateTime.UtcNow);
            }

            // A worker that does not serve the pool cannot claim the run (it is routed elsewhere).
            await using (var other = CatalogDatabase.Create(cs))
            {
                Assert.NotEqual(targetedRunId, await RunQueueStore.ClaimNextAsync(other, "other-node", ["a-different-pool"], DateTime.UtcNow));
            }

            // An untargeted worker cannot claim it either.
            await using (var untargeted = CatalogDatabase.Create(cs))
            {
                Assert.NotEqual(targetedRunId, await RunQueueStore.ClaimNextAsync(untargeted, "plain-node", [], DateTime.UtcNow));
            }

            // A worker serving the pool claims it.
            await using (var pooled = CatalogDatabase.Create(cs))
            {
                Assert.Equal(targetedRunId, await RunQueueStore.ClaimNextAsync(pooled, "pool-node", [pool], DateTime.UtcNow));
            }
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Cancel_QueuedRun_CancelsIt_AndItIsNeverClaimed()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);

            Assert.Equal(CancelOutcome.Cancelled, await RunQueueStore.CancelAsync(db, runId, DateTime.UtcNow));
            Assert.Equal(RunStatuses.Cancelled, (await Reload(db, runId)).Status);

            // A cancelled run is not queued, so the claim never returns it.
            Assert.Null(await RunQueueStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow));

            // A second cancel of the same run is not cancellable; an unknown run is not found.
            Assert.Equal(CancelOutcome.NotCancellable, await RunQueueStore.CancelAsync(db, runId, DateTime.UtcNow));
            Assert.Equal(CancelOutcome.NotFound, await RunQueueStore.CancelAsync(db, Guid.NewGuid(), DateTime.UtcNow));
        }
        finally
        {
            await Cleanup(cs, repoId, null);
        }
    }

    [SkippableFact]
    public async Task RecoverStuckRunning_RequeuesThisNodesOrphans()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var node = "recover-node-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            Assert.Equal(runId, await RunQueueStore.ClaimNextAsync(db, node, [], DateTime.UtcNow));

            var recovered = await RunQueueStore.RecoverStuckRunningAsync(db, node);
            Assert.True(recovered >= 1);

            var requeued = await Reload(db, runId);
            Assert.Equal(RunStatuses.Queued, requeued.Status);
            Assert.Null(requeued.ClaimedByNode);
        }
        finally
        {
            await Cleanup(cs, repoId, null);
        }
    }

    [SkippableFact]
    public async Task Fail_DrivesARunningRunToFailed_AndIsTerminalRespecting()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            await RunQueueStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow);

            await RunQueueStore.FailAsync(db, runId, "boom", DateTime.UtcNow);
            var failed = await Reload(db, runId);
            Assert.Equal(RunStatuses.Failed, failed.Status);
            Assert.False(failed.Success);
            Assert.Equal("boom", failed.Error);

            // A second fail must not overwrite the recorded terminal state.
            await RunQueueStore.FailAsync(db, runId, "second", DateTime.UtcNow);
            Assert.Equal("boom", (await Reload(db, runId)).Error);
        }
        finally
        {
            await Cleanup(cs, repoId, null);
        }
    }

    private static (Guid RepoId, string FlowName) NewIds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("rq_" + suffix), "rq_flow_" + suffix);
    }

    private static async Task<CatalogRun> Reload(CatalogDbContext db, Guid runId)
    {
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId);
        Assert.NotNull(run);
        return run;
    }

    private static string RunArtifact(Guid runId, string flowName, bool success, long rowsLoaded)
        => $$"""
            {
              "schemaVersion": 1,
              "flowKind": "ing",
              "flowName": "{{flowName}}",
              "runId": "{{runId}}",
              "success": {{(success ? "true" : "false")}},
              "writtenUtc": "2026-06-19T10:00:00Z",
              "result": { "rowsLoaded": {{rowsLoaded}}, "durationSeconds": 1.0 }
            }
            """;

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqlflow_rq_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task Cleanup(string cs, Guid repoId, string? dir = null)
    {
        await using (var db = CatalogDatabase.Create(cs))
        {
            await db.RunStatements.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunFiles.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunAssertions.Where(a => a.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunSurrogateKeys.Where(k => k.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunHealthCheckMetrics.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        }

        if (dir is not null && Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // A transient lock on the temp file must not fail the test.
            }
        }
    }
}
