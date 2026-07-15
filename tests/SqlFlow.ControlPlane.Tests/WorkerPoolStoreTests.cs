using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The desired-compute surface (<see cref="WorkerPoolStore"/>): the pure replica-target resolution the autoscaler
/// mirrors, the per-pool desired-state upsert, and the per-node restart request. The DB-backed cases are gated on a
/// reachable catalog like the other integration tests and clean up their own pool/node rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WorkerPoolStoreTests
{
    [Fact]
    public void ResolveTarget_IsGreatestOfQueuedFloorAndActiveManual()
    {
        var now = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

        // No desired row: the target is pure queue depth.
        Assert.Equal(3, WorkerPoolStore.ResolveTarget(3, null, now));

        // Floor lifts an idle pool; queue depth still wins when it is higher.
        var floor = new CatalogWorkerPoolDesired { MinReplicas = 1 };
        Assert.Equal(1, WorkerPoolStore.ResolveTarget(0, floor, now));
        Assert.Equal(4, WorkerPoolStore.ResolveTarget(4, floor, now));

        // A manual override applies only while its window is open.
        var manualOpen = new CatalogWorkerPoolDesired { ManualReplicas = 5, ManualUntilUtc = now.AddMinutes(10) };
        Assert.Equal(5, WorkerPoolStore.ResolveTarget(0, manualOpen, now));

        var manualExpired = new CatalogWorkerPoolDesired { ManualReplicas = 5, ManualUntilUtc = now.AddMinutes(-1) };
        Assert.Equal(0, WorkerPoolStore.ResolveTarget(0, manualExpired, now));

        // All three terms combine as a max.
        var all = new CatalogWorkerPoolDesired { MinReplicas = 2, ManualReplicas = 5, ManualUntilUtc = now.AddMinutes(10) };
        Assert.Equal(5, WorkerPoolStore.ResolveTarget(3, all, now));
        Assert.Equal(7, WorkerPoolStore.ResolveTarget(7, all, now));
    }

    [SkippableFact]
    public async Task SaveScale_UpsertsFacetsAndResolvesTargetAgainstQueueDepth()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var pool = "wp-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;

            // Always-on floor of 2, no queued work: the resolved target is the floor.
            await WorkerPoolStore.SaveScaleAsync(db, pool, minReplicas: 2, manualReplicas: 0, manualUntilUtc: null,
                updatedBy: "tester", nowUtc: now);
            Assert.Equal(2, await WorkerPoolStore.ResolveReplicaTargetAsync(db, pool, now));

            // A bounded manual override lifts it further while its window is open, then lapses.
            await WorkerPoolStore.SaveScaleAsync(db, pool, minReplicas: 2, manualReplicas: 5,
                manualUntilUtc: now.AddMinutes(10), updatedBy: "tester", nowUtc: now);
            Assert.Equal(5, await WorkerPoolStore.ResolveReplicaTargetAsync(db, pool, now));
            Assert.Equal(2, await WorkerPoolStore.ResolveReplicaTargetAsync(db, pool, now.AddMinutes(11)));

            // The upsert is idempotent by pool key and the last write wins.
            await WorkerPoolStore.SaveScaleAsync(db, pool, minReplicas: 0, manualReplicas: 0, manualUntilUtc: null,
                updatedBy: "tester2", nowUtc: now.AddMinutes(1));
            var saved = await WorkerPoolStore.GetDesiredAsync(db, pool);
            Assert.NotNull(saved);
            Assert.Equal(0, saved!.MinReplicas);
            Assert.Equal("tester2", saved.UpdatedBy);
            Assert.Equal(0, await WorkerPoolStore.ResolveReplicaTargetAsync(db, pool, now));
        }
        finally
        {
            await DeletePools(cs, pool);
        }
    }

    [SkippableFact]
    public async Task RequestNodeRestart_StampsExistingNode_AndHeartbeatReturnsIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var node = "wp-node-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;

            // Unknown node: nothing stamped.
            Assert.False(await WorkerPoolStore.RequestNodeRestartAsync(db, node, now));

            // Register the node, then request its restart; the next heartbeat returns the pending request so the
            // worker can honor it.
            Assert.Null(await NodeStore.HeartbeatAsync(db, node, "1.0.0", now));
            Assert.True(await WorkerPoolStore.RequestNodeRestartAsync(db, node, now.AddSeconds(1)));

            var pending = await NodeStore.HeartbeatAsync(db, node, "1.0.0", now.AddSeconds(2));
            Assert.Equal(now.AddSeconds(1), pending);
        }
        finally
        {
            await DeleteNodes(cs, node);
        }
    }

    private static async Task DeletePools(string cs, params string[] pools)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.WorkerPools.Where(p => pools.Contains(p.Pool)).ExecuteDeleteAsync();
    }

    private static async Task DeleteNodes(string cs, params string[] names)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Nodes.Where(n => names.Contains(n.Name)).ExecuteDeleteAsync();
    }
}
