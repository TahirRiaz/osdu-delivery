using System.Text.Json;
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
    public async Task ClaimNext_SamePipeline_NeverRunsTwiceConcurrently()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var otherFlow = flowName + "_other";
        var dir = NewTempDir();

        try
        {
            await using var db = CatalogDatabase.Create(cs);

            // Two queued runs of the SAME flow (a double-trigger) plus one run of a different flow.
            var first = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            var duplicate = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            var unrelated = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, otherFlow, "ing"), DateTime.UtcNow);

            // The oldest same-flow run is claimed; its duplicate is NOT claimable while it runs (each flow
            // stages through one canonical work table, so executions must serialize), but the pipeline gate is
            // per flow: the unrelated flow's run is handed out immediately.
            Assert.Equal(first, await RunQueueStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow));
            Assert.Equal(unrelated, await RunQueueStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow));
            Assert.Null(await RunQueueStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow));
            Assert.Equal(RunStatuses.Queued, (await Reload(db, duplicate)).Status);

            // Once the running execution reaches a terminal state, the duplicate becomes claimable.
            var runJson = Path.Combine(dir, "run.json");
            await File.WriteAllTextAsync(runJson, RunArtifact(first, flowName, success: true, rowsLoaded: 1));
            Assert.True(await RunQueueStore.CompleteFromArtifactAsync(db, first, repoId, runJson, DateTime.UtcNow));
            Assert.Equal(duplicate, await RunQueueStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow));
        }
        finally
        {
            await Cleanup(cs, repoId, dir);
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
    public async Task Cancel_RunningRun_RequestsCancellation_ForTheOwningNodeToObserveAndRecord()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var node = "cancel-node-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            Assert.Equal(runId, await RunQueueStore.ClaimNextAsync(db, node, [], DateTime.UtcNow));

            // A running run cannot be cancelled out from under its worker: it is a durable request instead, stamped
            // on the row for the owning node to observe. The run stays 'running' until the node records the outcome.
            var requestedAt = DateTime.UtcNow;
            Assert.Equal(CancelOutcome.CancelRequested, await RunQueueStore.CancelAsync(db, runId, requestedAt));
            var requested = await Reload(db, runId);
            Assert.Equal(RunStatuses.Running, requested.Status);
            Assert.Equal(requestedAt, requested.CancelRequestedUtc);

            // A second cancel is idempotent (still a pending request) and does not move the original request time.
            Assert.Equal(CancelOutcome.CancelRequested, await RunQueueStore.CancelAsync(db, runId, requestedAt.AddSeconds(5)));
            Assert.Equal(requestedAt, (await Reload(db, runId)).CancelRequestedUtc);

            // The owning node sees exactly this run in its cancel-requested set; a different node sees nothing.
            Assert.Equal([runId], await RunQueueStore.ListCancelRequestedAsync(db, node));
            Assert.Empty(await RunQueueStore.ListCancelRequestedAsync(db, "some-other-node"));

            // After the node aborts the in-flight statement it records the run cancelled; the request then clears
            // from the set (the run is no longer 'running'), and a further cancel finds nothing to cancel.
            Assert.Equal(1, await RunQueueStore.CancelRunningAsync(db, runId, DateTime.UtcNow));
            var cancelled = await Reload(db, runId);
            Assert.Equal(RunStatuses.Cancelled, cancelled.Status);
            Assert.False(cancelled.Success);
            Assert.NotNull(cancelled.EndUtc);
            Assert.Empty(await RunQueueStore.ListCancelRequestedAsync(db, node));
            Assert.Equal(CancelOutcome.NotCancellable, await RunQueueStore.CancelAsync(db, runId, DateTime.UtcNow));
        }
        finally
        {
            await Cleanup(cs, repoId, null);
        }
    }

    [SkippableFact]
    public async Task CancelRunning_DoesNotOverwriteAnAlreadyCompletedRun()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var dir = NewTempDir();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            await RunQueueStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow);

            // The run finishes successfully in the same instant a late cancel lands: CancelRunningAsync is guarded on
            // 'running', so it updates nothing and the recorded success stands.
            var runJson = Path.Combine(dir, "run.json");
            await File.WriteAllTextAsync(runJson, RunArtifact(runId, flowName, success: true, rowsLoaded: 3));
            Assert.True(await RunQueueStore.CompleteFromArtifactAsync(db, runId, repoId, runJson, DateTime.UtcNow));

            Assert.Equal(0, await RunQueueStore.CancelRunningAsync(db, runId, DateTime.UtcNow));
            Assert.Equal(RunStatuses.Succeeded, (await Reload(db, runId)).Status);
        }
        finally
        {
            await Cleanup(cs, repoId, dir);
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
    public async Task ReapOrphanedRunning_FailsRunsWhoseNodeHasStoppedHeartbeating()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var deadNode = "reap-dead-" + Guid.NewGuid().ToString("N")[..8];
        var goneNode = "reap-gone-" + Guid.NewGuid().ToString("N")[..8];
        var (repoId2, flowName2) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;

            // A run claimed by a node whose last heartbeat is well before the stale cutoff (a crashed pod), and a
            // second run claimed by a node with no registry row at all (it died without its heartbeat ever landing,
            // or a Kubernetes replacement pod took a new name). Both are orphans.
            var deadRun = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), now);
            Assert.Equal(deadRun, await RunQueueStore.ClaimNextAsync(db, deadNode, [], now));
            await NodeStore.HeartbeatAsync(db, deadNode, "1.0.0", now.AddMinutes(-10));

            var goneRun = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId2, flowName2, "ing"), now);
            Assert.Equal(goneRun, await RunQueueStore.ClaimNextAsync(db, goneNode, [], now));

            var staleBefore = now.AddMinutes(-3);
            var reaped = await RunQueueStore.ReapOrphanedRunningAsync(db, staleBefore, now);
            Assert.True(reaped >= 2);

            foreach (var runId in new[] { deadRun, goneRun })
            {
                var failed = await Reload(db, runId);
                Assert.Equal(RunStatuses.Failed, failed.Status);
                Assert.False(failed.Success);
                Assert.NotNull(failed.EndUtc);
                Assert.Contains("orphaned", failed.Error!, StringComparison.OrdinalIgnoreCase);
            }

            // The dead node's error names the node so an operator can see which host went away.
            Assert.Contains(deadNode, (await Reload(db, deadRun)).Error!, StringComparison.Ordinal);

            // A second sweep is a no-op: the runs are terminal, so they are no longer candidates.
            Assert.Equal(0, await RunQueueStore.ReapOrphanedRunningAsync(db, staleBefore, DateTime.UtcNow));
        }
        finally
        {
            await DeleteNodes(cs, deadNode, goneNode);
            await Cleanup(cs, repoId, null);
            await Cleanup(cs, repoId2, null);
        }
    }

    [SkippableFact]
    public async Task ReapOrphanedRunning_LeavesRunsOfALiveNodeAlone()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var liveNode = "reap-live-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;

            // A busy node executing a long run still heartbeats on its independent cadence, so its last-seen stays
            // fresh. The reaper must never fail its work, however long the run has been going.
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), now);
            Assert.Equal(runId, await RunQueueStore.ClaimNextAsync(db, liveNode, [], now));
            await NodeStore.HeartbeatAsync(db, liveNode, "1.0.0", now);

            var reaped = await RunQueueStore.ReapOrphanedRunningAsync(db, now.AddMinutes(-3), now);
            _ = reaped; // other tests' orphans may exist; only this run's fate is asserted

            var stillRunning = await Reload(db, runId);
            Assert.Equal(RunStatuses.Running, stillRunning.Status);
            Assert.Equal(liveNode, stillRunning.ClaimedByNode);
            Assert.Null(stillRunning.EndUtc);
        }
        finally
        {
            await DeleteNodes(cs, liveNode);
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

    [Fact]
    public void RunStatements_ProjectsTheErrorOntoTheFailingStatementOnly()
    {
        var runId = Guid.NewGuid();
        var repoId = Guid.NewGuid();
        using var doc = JsonDocument.Parse(FailedArtifactWithTrace(runId, "rq_flow"));

        var statements = CatalogProjection.RunStatements(doc.RootElement, runId, repoId);

        Assert.Equal(2, statements.Count);
        Assert.Equal(1, statements[0].Ordinal);
        Assert.Equal("staging.create", statements[0].Step);
        Assert.Null(statements[0].Error);
        Assert.Equal(2, statements[1].Ordinal);
        Assert.Equal("upsert.insert", statements[1].Step);
        Assert.Equal("Cannot insert duplicate key", statements[1].Error);
    }

    [SkippableFact]
    public async Task CompleteFromArtifact_KeepsACompleteLiveTraceUnderStableIds_AppendingNothing()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var dir = NewTempDir();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            await RunQueueStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow);

            // The node's live feed captured the run's whole trace as it executed: the same rows, ordinals, and
            // failure attribution the artifact carries. The trace stream has already delivered these under their
            // ids, so completion must keep them exactly - not delete and re-issue them under fresh ids.
            var liveStatements = new[]
            {
                new CatalogRunStatement
                {
                    RunId = runId, RepoId = repoId, Ordinal = 1, Step = "staging.create", Sql = "CREATE TABLE #s;",
                    TimestampUtc = new DateTime(2026, 6, 19, 9, 59, 58, DateTimeKind.Utc),
                },
                new CatalogRunStatement
                {
                    RunId = runId, RepoId = repoId, Ordinal = 2, Step = "upsert.insert", Sql = "INSERT INTO t;",
                    TimestampUtc = new DateTime(2026, 6, 19, 9, 59, 59, DateTimeKind.Utc),
                    Error = "Cannot insert duplicate key",
                },
            };
            var liveEvents = new[]
            {
                new CatalogRunEvent
                {
                    RunId = runId, RepoId = repoId, Ordinal = 1, Level = "info", Step = "incremental",
                    Message = "watermark resolved to 2026-06-18",
                    TimestampUtc = new DateTime(2026, 6, 19, 9, 59, 57, DateTimeKind.Utc),
                },
                new CatalogRunEvent
                {
                    RunId = runId, RepoId = repoId, Ordinal = 2, Level = "error", Step = "upsert.insert",
                    Message = "run failed: Cannot insert duplicate key",
                    TimestampUtc = new DateTime(2026, 6, 19, 10, 0, 0, DateTimeKind.Utc),
                },
            };
            db.RunStatements.AddRange(liveStatements);
            db.RunEvents.AddRange(liveEvents);
            await db.SaveChangesAsync();
            var liveStatementIds = liveStatements.Select(s => s.Id).ToArray();
            var liveEventIds = liveEvents.Select(e => e.Id).ToArray();

            var runJson = Path.Combine(dir, "run.json");
            await File.WriteAllTextAsync(runJson, FailedArtifactWithTrace(runId, flowName));
            Assert.True(await RunQueueStore.CompleteFromArtifactAsync(db, runId, repoId, runJson, DateTime.UtcNow));

            // No duplication and no re-issue: the very same live rows remain, under the very same ids.
            var statements = await db.RunStatements.AsNoTracking()
                .Where(s => s.RunId == runId).OrderBy(s => s.Ordinal).ToListAsync();
            Assert.Equal(liveStatementIds, statements.Select(s => s.Id).ToArray());
            Assert.Equal("Cannot insert duplicate key", statements[1].Error);

            var events = await db.RunEvents.AsNoTracking()
                .Where(e => e.RunId == runId).OrderBy(e => e.Ordinal).ToListAsync();
            Assert.Equal(liveEventIds, events.Select(e => e.Id).ToArray());

            var run = await Reload(db, runId);
            Assert.Equal(RunStatuses.Failed, run.Status);
        }
        finally
        {
            await Cleanup(cs, repoId, dir);
        }
    }

    [SkippableFact]
    public async Task CompleteFromArtifact_AppendsOnlyTheTailWhenALiveFeedBrokeMidRun()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var dir = NewTempDir();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            await RunQueueStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow);

            // The live feed broke after the first statement and the first event: only the prefix reached the
            // catalog. Completion must preserve that prefix under its ids and append only the missing tail.
            var liveStatement = new CatalogRunStatement
            {
                RunId = runId, RepoId = repoId, Ordinal = 1, Step = "staging.create", Sql = "CREATE TABLE #s;",
                TimestampUtc = new DateTime(2026, 6, 19, 9, 59, 58, DateTimeKind.Utc),
            };
            var liveEvent = new CatalogRunEvent
            {
                RunId = runId, RepoId = repoId, Ordinal = 1, Level = "info", Step = "incremental",
                Message = "watermark resolved to 2026-06-18",
                TimestampUtc = new DateTime(2026, 6, 19, 9, 59, 57, DateTimeKind.Utc),
            };
            db.RunStatements.Add(liveStatement);
            db.RunEvents.Add(liveEvent);
            await db.SaveChangesAsync();
            var liveStatementId = liveStatement.Id;
            var liveEventId = liveEvent.Id;

            var runJson = Path.Combine(dir, "run.json");
            await File.WriteAllTextAsync(runJson, FailedArtifactWithTrace(runId, flowName));
            Assert.True(await RunQueueStore.CompleteFromArtifactAsync(db, runId, repoId, runJson, DateTime.UtcNow));

            // The whole trace is present, exactly once: the prefix under its original id, the tail newly appended.
            var statements = await db.RunStatements.AsNoTracking()
                .Where(s => s.RunId == runId).OrderBy(s => s.Ordinal).ToListAsync();
            Assert.Equal(2, statements.Count);
            Assert.Equal(liveStatementId, statements[0].Id);
            Assert.NotEqual(liveStatementId, statements[1].Id);
            Assert.Equal("INSERT INTO t;", statements[1].Sql);
            Assert.Equal("Cannot insert duplicate key", statements[1].Error);

            var events = await db.RunEvents.AsNoTracking()
                .Where(e => e.RunId == runId).OrderBy(e => e.Ordinal).ToListAsync();
            Assert.Equal(2, events.Count);
            Assert.Equal(liveEventId, events[0].Id);
            Assert.NotEqual(liveEventId, events[1].Id);
            Assert.Equal("error", events[1].Level);
            Assert.Equal("run failed: Cannot insert duplicate key", events[1].Message);

            var run = await Reload(db, runId);
            Assert.Equal(RunStatuses.Failed, run.Status);
        }
        finally
        {
            await Cleanup(cs, repoId, dir);
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

    // A failed ingestion artifact whose SQL trace attributes the failure to the second statement (the upsert
    // insert), mirroring a real duplicate-key failure: the projection stamps its Error onto that entry only.
    // Carries a canonical events array too, so the completion's event reconciliation is exercised alongside.
    private static string FailedArtifactWithTrace(Guid runId, string flowName)
        => $$"""
            {
              "schemaVersion": 1,
              "flowKind": "ing",
              "flowName": "{{flowName}}",
              "runId": "{{runId}}",
              "success": false,
              "error": "Cannot insert duplicate key",
              "writtenUtc": "2026-06-19T10:00:00Z",
              "result": {
                "durationSeconds": 2.0,
                "sqlTrace": [
                  { "sequence": 1, "timestampUtc": "2026-06-19T09:59:58Z", "step": "staging.create", "sql": "CREATE TABLE #s;" },
                  { "sequence": 2, "timestampUtc": "2026-06-19T09:59:59Z", "step": "upsert.insert", "sql": "INSERT INTO t;", "error": "Cannot insert duplicate key" }
                ]
              },
              "events": [
                { "timestampUtc": "2026-06-19T09:59:57Z", "level": "info", "step": "incremental", "message": "watermark resolved to 2026-06-18" },
                { "timestampUtc": "2026-06-19T10:00:00Z", "level": "error", "step": "upsert.insert", "message": "run failed: Cannot insert duplicate key" }
              ]
            }
            """;

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqlflow_rq_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task DeleteNodes(string cs, params string[] names)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Nodes.Where(n => names.Contains(n.Name)).ExecuteDeleteAsync();
    }

    private static async Task Cleanup(string cs, Guid repoId, string? dir = null)
    {
        await using (var db = CatalogDatabase.Create(cs))
        {
            await db.RunStatements.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
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
