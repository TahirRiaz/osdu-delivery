using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The multi-flow run group over the real catalog database: enqueuing a set as one wave-ordered group, the claim's
/// wave gating (a higher-wave member is never claimed before its lower-wave siblings finish), the on-failure skip of
/// a failed flow's dependents (while independent branches keep running), cancelling a whole group, and the scope
/// expander that turns Flow / Node / Batch into the concrete member set. Gated on a reachable catalog database like
/// the other DB-backed tests; each test removes its own repo's rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunGroupQueueTests
{
    private const string Node = "test-group-node";

    [SkippableFact]
    public async Task EnqueueGroup_InsertsHeaderAndMembers_QueuedInWaveOrder()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var members = new List<RunScopeMember>
            {
                new($"a_{suffix}", "ing", 0, CatalogPipeline.DefaultBatch),
                new($"b_{suffix}", "ing", 1, CatalogPipeline.DefaultBatch),
            };
            var now = DateTime.UtcNow;
            var result = await RunQueueStore.EnqueueGroupAsync(
                db, new RunGroupEnqueueRequest(repoId, RunGroupModes.Node, $"a_{suffix}", members), now);

            Assert.Equal(2, result.RunIds.Count);
            var header = await db.RunGroups.AsNoTracking().FirstOrDefaultAsync(g => g.GroupId == result.GroupId);
            Assert.NotNull(header);
            Assert.Equal(RunGroupModes.Node, header.Mode);
            Assert.Equal(2, header.MemberCount);

            var runs = await db.Runs.AsNoTracking()
                .Where(r => r.GroupId == result.GroupId).OrderBy(r => r.GroupWave).ToListAsync();
            Assert.Equal(2, runs.Count);
            Assert.All(runs, r => Assert.Equal(RunStatuses.Queued, r.Status));
            Assert.Equal(0, runs[0].GroupWave);
            Assert.Equal(1, runs[1].GroupWave);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task EnqueueGroup_CarriesPerMemberParameters_AndDefaultsTheRest()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            // Three delivery flows in a chain: the anchor is forced, one descendant verifies instead of delivering,
            // and the other is absent from the map, so it runs as defined.
            string anchor = $"a_{suffix}", plainChild = $"b_{suffix}", verifyChild = $"c_{suffix}";
            var members = new List<RunScopeMember>
            {
                new(anchor, "delivery", 0, CatalogPipeline.DefaultBatch),
                new(plainChild, "delivery", 1, CatalogPipeline.DefaultBatch),
                new(verifyChild, "delivery", 2, CatalogPipeline.DefaultBatch),
            };
            var memberParameters = new Dictionary<string, RunParameters>(StringComparer.Ordinal)
            {
                [anchor] = new RunParameters { Force = true },
                [verifyChild] = new RunParameters { Operation = RunParameters.VerifyOperation },
                // plainChild is intentionally absent: a member left out of the map runs with default parameters.
            };
            var result = await RunQueueStore.EnqueueGroupAsync(
                db,
                new RunGroupEnqueueRequest(
                    repoId, RunGroupModes.Node, anchor, members, MemberParameters: memberParameters),
                DateTime.UtcNow);

            var runs = await db.Runs.AsNoTracking()
                .Where(r => r.GroupId == result.GroupId).ToListAsync();
            var anchorRun = Assert.Single(runs, r => r.FlowName == anchor);
            var plainRun = Assert.Single(runs, r => r.FlowName == plainChild);
            var verifyRun = Assert.Single(runs, r => r.FlowName == verifyChild);

            // The anchor delivers, forced past its change gates.
            Assert.Equal("deliver", anchorRun.Operation);
            Assert.True(anchorRun.Force);
            Assert.NotNull(anchorRun.ParametersJson);

            // The member absent from the map runs at defaults: a plain deliver with no stored parameters.
            Assert.Equal("deliver", plainRun.Operation);
            Assert.False(plainRun.Force);
            Assert.Null(plainRun.ParametersJson);

            // The verifying member carries its operation.
            Assert.Equal("verify", verifyRun.Operation);
            Assert.False(verifyRun.Force);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Claim_GatesByWave_HigherWaveWaitsForLowerWaveToSucceed()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var (repoId, suffix) = NewRepo();
        var dir = NewTempDir();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var members = new List<RunScopeMember>
            {
                new($"a_{suffix}", "ing", 0, CatalogPipeline.DefaultBatch),
                new($"b_{suffix}", "ing", 1, CatalogPipeline.DefaultBatch),
            };
            var result = await RunQueueStore.EnqueueGroupAsync(
                db, new RunGroupEnqueueRequest(repoId, RunGroupModes.Node, $"a_{suffix}", members), DateTime.UtcNow);
            var (runA, runB) = (result.RunIds[0], result.RunIds[1]);

            // Only the wave-0 member is claimable; the wave-1 member is gated behind it.
            Assert.Equal(runA, await ClaimId(db, Node, [], DateTime.UtcNow));
            Assert.Null(await ClaimId(db, Node, [], DateTime.UtcNow));
            Assert.Equal(RunStatuses.Queued, (await Reload(db, runB)).Status);

            // Once wave 0 succeeds, the wave-1 member becomes claimable.
            await CompleteSuccess(db, runA, repoId, $"a_{suffix}", dir);
            Assert.Equal(runB, await ClaimId(db, Node, [], DateTime.UtcNow));
        }
        finally
        {
            await Cleanup(cs, repoId, dir);
        }
    }

    [SkippableFact]
    public async Task Fail_SkipsEveryLaterWave_OfTheGroup()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            string A = $"a_{suffix}", B = $"b_{suffix}", C = $"c_{suffix}";
            // B and C sit in wave 1, behind A in wave 0.

            var members = new List<RunScopeMember>
            {
                new(A, "ing", 0, CatalogPipeline.DefaultBatch),
                new(B, "ing", 1, CatalogPipeline.DefaultBatch),
                new(C, "ing", 1, CatalogPipeline.DefaultBatch),
            };
            var result = await RunQueueStore.EnqueueGroupAsync(
                db, new RunGroupEnqueueRequest(repoId, RunGroupModes.Node, A, members), DateTime.UtcNow);
            var (runA, runB, runC) = (result.RunIds[0], result.RunIds[1], result.RunIds[2]);

            Assert.Equal(runA, await ClaimId(db, Node, [], DateTime.UtcNow));

            // A fails: every member queued behind it in a later wave is skipped, and nothing is left to claim.
            await RunQueueStore.FailAsync(db, runA, "boom", DateTime.UtcNow);
            Assert.Equal(RunStatuses.Failed, (await Reload(db, runA)).Status);
            Assert.Equal(RunStatuses.Skipped, (await Reload(db, runB)).Status);
            Assert.Equal(RunStatuses.Skipped, (await Reload(db, runC)).Status);
            Assert.Null(await ClaimId(db, Node, [], DateTime.UtcNow));
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task CancelGroup_CancelsQueuedMembers_AndReportsUnknownGroup()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var members = new List<RunScopeMember>
            {
                new($"a_{suffix}", "ing", 0, CatalogPipeline.DefaultBatch),
                new($"b_{suffix}", "ing", 1, CatalogPipeline.DefaultBatch),
            };
            var result = await RunQueueStore.EnqueueGroupAsync(
                db, new RunGroupEnqueueRequest(repoId, RunGroupModes.Batch, $"bt_{suffix}", members), DateTime.UtcNow);

            var cancelled = await RunQueueStore.CancelGroupAsync(db, result.GroupId, DateTime.UtcNow);
            Assert.True(cancelled.Found);
            Assert.Equal(2, cancelled.CancelledQueued);
            Assert.Equal(0, cancelled.RequestedRunning);
            var runs = await db.Runs.AsNoTracking().Where(r => r.GroupId == result.GroupId).ToListAsync();
            Assert.All(runs, r => Assert.Equal(RunStatuses.Cancelled, r.Status));

            var unknown = await RunQueueStore.CancelGroupAsync(db, Guid.NewGuid(), DateTime.UtcNow);
            Assert.False(unknown.Found);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ExpandSchedule_ReturnsItsMembersInWaveOrder_AndOnlyItsMembers()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            string A = $"a_{suffix}", B = $"b_{suffix}", C = $"c_{suffix}";
            await SeedPipelineAsync(db, repoId, A, wave: 0, batch: "small");
            await SeedPipelineAsync(db, repoId, B, wave: 1, batch: "large");
            // C carries the same batch tag as A but never joined the schedule. Under membership a label match is not
            // a join, which is exactly what the old batch scope got wrong.
            await SeedPipelineAsync(db, repoId, C, wave: 0, batch: "small");

            var scheduleId = await SeedScheduleAsync(db, repoId, $"nightly_{suffix}", [A, B]);

            var expansion = await RunScopeExpander.ExpandScheduleAsync(db, repoId, scheduleId, $"nightly_{suffix}");
            Assert.Equal($"nightly_{suffix}", expansion.Anchor);
            Assert.Equal(new[] { A, B }, expansion.Members.Select(m => m.FlowName).ToArray());
            Assert.Equal(new[] { 0, 1 }, expansion.Members.Select(m => m.Wave).ToArray());
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ExpandSchedule_BatchFilter_NarrowsToTaggedMembersOnly()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            string A = $"a_{suffix}", B = $"b_{suffix}", C = $"c_{suffix}";
            await SeedPipelineAsync(db, repoId, A, wave: 0, batch: "small");
            await SeedPipelineAsync(db, repoId, B, wave: 1, batch: "large");
            await SeedPipelineAsync(db, repoId, C, wave: 0, batch: "small");

            // A and B joined; C did not, though it is tagged 'small'.
            var scheduleId = await SeedScheduleAsync(db, repoId, $"nightly_{suffix}", [A, B]);

            // "Run the nightly, but only the small tables": a subset of the members, never a widening of them.
            var filtered = await RunScopeExpander.ExpandScheduleAsync(db, repoId, scheduleId, $"nightly_{suffix}", ["small"]);
            Assert.Equal(new[] { A }, filtered.Members.Select(m => m.FlowName).ToArray());

            // A tag no member carries runs nothing rather than falling back to the whole set.
            var none = await RunScopeExpander.ExpandScheduleAsync(db, repoId, scheduleId, $"nightly_{suffix}", ["medium"]);
            Assert.Empty(none.Members);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    /// <summary>Seeds a schedule with an explicit member set, the shape the estate scan produces.</summary>
    private static async Task<Guid> SeedScheduleAsync(
        CatalogDbContext db, Guid repoId, string name, IReadOnlyList<string> members)
    {
        var now = DateTime.UtcNow;
        var id = CatalogIdentity.YamlSchedule(repoId, name);
        db.Schedules.Add(new CatalogSchedule
        {
            Id = id, RepoId = repoId, Name = name, Cron = "0 4 * * *", Timezone = "UTC",
            Enabled = true, Source = "yaml", CreatedUtc = now, UpdatedUtc = now,
        });
        foreach (var member in members)
        {
            db.ScheduleMembers.Add(new CatalogScheduleMember
            {
                ScheduleId = id, PipelineId = CatalogIdentity.Pipeline(repoId, member), RepoId = repoId, FlowName = member,
            });
        }

        await db.SaveChangesAsync();
        return id;
    }

    private static (Guid RepoId, string Suffix) NewRepo()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("grp_" + suffix), suffix);
    }

    private static async Task SeedPipelineAsync(
        CatalogDbContext db, Guid repoId, string name, int wave, string batch)
    {
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = CatalogIdentity.Pipeline(repoId, name),
            RepoId = repoId,
            Name = name,
            Kind = "ing",
            Batch = batch,
            RelativePath = $"flows/{name}.flow.yaml",
            Active = true,
            Wave = wave,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task CompleteSuccess(
        CatalogDbContext db, Guid runId, Guid repoId, string flowName, string dir)
    {
        var runJson = Path.Combine(dir, $"run_{runId:N}.json");
        await File.WriteAllTextAsync(runJson, $$"""
            {
              "schemaVersion": 1,
              "flowKind": "ing",
              "flowName": "{{flowName}}",
              "runId": "{{runId}}",
              "success": true,
              "writtenUtc": "2026-06-19T10:00:00Z",
              "result": { "rowsLoaded": 1, "durationSeconds": 1.0 }
            }
            """);
        Assert.Equal(RunCompletionOutcome.Recorded, await RunQueueStore.CompleteFromArtifactAsync(db, runId, repoId, runJson, DateTime.UtcNow));
    }

    private static async Task<Guid?> ClaimId(CatalogDbContext db, string node, IReadOnlyList<string> pools, DateTime nowUtc)
        => (await RunQueueStore.ClaimNextAsync(db, node, pools, nowUtc))?.RunId;

    private static async Task<CatalogRun> Reload(CatalogDbContext db, Guid runId)
    {
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId);
        Assert.NotNull(run);
        return run;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqlflow_grp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task Cleanup(string cs, Guid repoId, string? dir = null)
    {
        await using (var db = CatalogDatabase.Create(cs))
        {
            await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
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
