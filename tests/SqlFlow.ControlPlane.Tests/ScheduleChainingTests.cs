using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Schedule CHAINING against the real catalog: a shadow schedule (<c>after: parent</c>) carries no cadence and is
/// driven by the parent's completed fire instead of the clock. These cover the readiness rule (parent fired, none of
/// its runs still queued or running, and this child has not already consumed that fire), the idempotence stamp that
/// makes one parent fire trigger a child exactly once however many nodes observe it, and the fact that a chained
/// schedule never appears in the clock-driven due scan. The assembly runs serially (see AssemblyInfo); each test
/// removes its own repo's rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScheduleChainingTests
{
    [SkippableFact]
    public async Task ChainedChild_IsReady_OnlyAfterParentsRunsAreTerminal()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();
        var parentFire = new DateTime(2026, 8, 2, 20, 10, 0, DateTimeKind.Utc);
        var groupId = Guid.NewGuid();

        try
        {
            await using (var seed = CatalogDatabase.Create(cs))
            {
                seed.Schedules.Add(Parent(repoId, "p", parentFire, groupId));
                seed.Schedules.Add(Child(repoId, "c", after: "p"));
                // The parent's fire is still in flight.
                seed.Runs.Add(Run(repoId, groupId, RunStatuses.Running));
                seed.Runs.Add(Run(repoId, groupId, RunStatuses.Succeeded));
                await seed.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var ready = await ScheduleStore.ListChainedReadyAsync(db, 10);
                Assert.DoesNotContain(ready, s => s.Name == "c");
            }

            // Finish the parent's last member: the whole fire is now terminal.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.RepoId == repoId && r.Status == RunStatuses.Running)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Succeeded));
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var ready = await ScheduleStore.ListChainedReadyAsync(db, 10);
                Assert.Contains(ready, s => s.Name == "c");
            }
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ChainedChild_FiresOncePerParentFire_AndOnlyOneNodeWins()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();
        var parentFire = new DateTime(2026, 8, 2, 20, 10, 0, DateTimeKind.Utc);
        var groupId = Guid.NewGuid();

        try
        {
            await using (var seed = CatalogDatabase.Create(cs))
            {
                seed.Schedules.Add(Parent(repoId, "p", parentFire, groupId));
                seed.Schedules.Add(Child(repoId, "c", after: "p"));
                seed.Runs.Add(Run(repoId, groupId, RunStatuses.Succeeded));
                await seed.SaveChangesAsync();
            }

            var childId = CatalogIdentity.YamlSchedule(repoId, "c");

            // Two control-plane nodes see the same completed parent; exactly one may consume it.
            await using (var a = CatalogDatabase.Create(cs))
            await using (var b = CatalogDatabase.Create(cs))
            {
                var results = await Task.WhenAll(
                    ScheduleStore.TryClaimChainedFireAsync(a, childId, null, parentFire, DateTime.UtcNow),
                    ScheduleStore.TryClaimChainedFireAsync(b, childId, null, parentFire, DateTime.UtcNow));
                Assert.Equal(1, results.Count(won => won));
            }

            // Having consumed that fire, the child is no longer ready: the parent must fire again first.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var stamped = await db.Schedules.AsNoTracking().SingleAsync(s => s.Id == childId);
                Assert.Equal(parentFire, stamped.LastParentFireUtc);

                var ready = await ScheduleStore.ListChainedReadyAsync(db, 10);
                Assert.DoesNotContain(ready, s => s.Name == "c");
            }
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ChainedChild_IsNeverInTheClockDueScan()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();

        try
        {
            await using (var seed = CatalogDatabase.Create(cs))
            {
                seed.Schedules.Add(Child(repoId, "c", after: "p"));
                await seed.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                // Far-future "now" would sweep up anything with a next fire; a chained schedule has none.
                var due = await ScheduleStore.ListDueAsync(db, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), 50);
                Assert.DoesNotContain(due, s => s.RepoId == repoId);
            }
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task YamlUpsert_MakingAScheduleChained_ClearsItsPendingClockFire()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var nextFire = now.AddHours(1);

        try
        {
            // First synced as a clock schedule...
            await using (var db = CatalogDatabase.Create(cs))
            {
                await ScheduleStore.UpsertYamlScheduleAsync(
                    db, repoId, "c", ["f"], "0 6 * * *", null, "UTC", true, false, 4, nextFire, now);
            }

            // ...then git changes it to chain behind a parent.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await ScheduleStore.UpsertYamlScheduleAsync(
                    db, repoId, "c", ["f"], null, null, "UTC", true, false, 4, nextFire, now,
                    definition: null, afterSchedule: "p");
            }

            await using (var verify = CatalogDatabase.Create(cs))
            {
                var row = await verify.Schedules.AsNoTracking()
                    .SingleAsync(s => s.Id == CatalogIdentity.YamlSchedule(repoId, "c"));
                Assert.Equal("p", row.AfterSchedule);
                Assert.Null(row.Cron);
                // The pending clock occurrence must be retired, or it would fire on the clock AND behind the parent.
                Assert.Null(row.NextFireUtc);
            }
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    private static CatalogSchedule Parent(Guid repoId, string name, DateTime lastFireUtc, Guid groupId) => new()
    {
        Id = CatalogIdentity.YamlSchedule(repoId, name),
        RepoId = repoId,
        Name = name,
        Cron = "10 20 * * *",
        Timezone = "UTC",
        Enabled = true,
        Source = "yaml",
        NextFireUtc = lastFireUtc.AddDays(1),
        LastFireUtc = lastFireUtc,
        LastGroupId = groupId,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static CatalogSchedule Child(Guid repoId, string name, string after) => new()
    {
        Id = CatalogIdentity.YamlSchedule(repoId, name),
        RepoId = repoId,
        Name = name,
        AfterSchedule = after,
        Timezone = "UTC",
        Enabled = true,
        Source = "yaml",
        NextFireUtc = null,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static CatalogRun Run(Guid repoId, Guid groupId, string status) => new()
    {
        RunId = Guid.NewGuid(),
        PipelineId = Guid.NewGuid(),
        RepoId = repoId,
        FlowName = "member",
        FlowKind = "ing",
        GroupId = groupId,
        Status = status,
        EnqueuedUtc = DateTime.UtcNow,
        WrittenUtc = DateTime.UtcNow,
        Success = status == RunStatuses.Succeeded,
    };

    private static async Task Cleanup(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
    }
}
