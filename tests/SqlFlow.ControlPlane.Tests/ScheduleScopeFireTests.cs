using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// What a schedule actually enqueues, by scope, through the real fire path. A flow-scoped schedule stays a single run
/// (the long-standing behaviour); a batch/node-scoped one expands through lineage and enqueues ONE wave-gated group, so
/// the members carry the waves that order them. The assembly runs serially (see AssemblyInfo); each test removes its
/// own repo's rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScheduleScopeFireTests
{
    [SkippableFact]
    public async Task Fire_FlowScope_EnqueuesOneRun_AndNoGroup()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var batch = $"bt_{suffix}";
        var anchor = $"a_{suffix}";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await SeedPipelineAsync(db, repoId, anchor, wave: 0, batch: batch);
            await SeedPipelineAsync(db, repoId, $"b_{suffix}", wave: 1, batch: batch);
            var schedule = await SeedScheduleAsync(db, repoId, anchor, RunScopes.Flow);

            var fire = await ScheduleFire.EnqueueAsync(
                db, new RecordingDispatcher(), schedule, honorManualMode: true, DateTime.UtcNow, default);

            Assert.Equal(ScheduleFire.Outcome.Enqueued, fire.Outcome);
            Assert.Null(fire.GroupId);
            Assert.Equal(1, fire.MemberCount);

            // Only the schedule's own flow was queued, even though a sibling shares its batch.
            var queued = await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync();
            Assert.Equal(new[] { anchor }, queued.Select(r => r.FlowName).ToArray());
            Assert.All(queued, r => Assert.Null(r.GroupId));

            var after = await db.Schedules.AsNoTracking().FirstAsync(s => s.Id == schedule.Id);
            Assert.Null(after.LastGroupId);
            Assert.NotNull(after.LastRunId);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Fire_BatchScope_EnqueuesEveryFlowInTheBatch_AsOneWaveOrderedGroup()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var batch = $"bt_{suffix}";
        string copy = $"a_copy_{suffix}", pre = $"b_pre_{suffix}", ods = $"c_ods_{suffix}", other = $"z_other_{suffix}";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            // The shape of a converted source: a wave-1 copy, then its loads, then its merges.
            await SeedPipelineAsync(db, repoId, copy, wave: 0, batch: batch);
            await SeedPipelineAsync(db, repoId, pre, wave: 1, batch: batch);
            await SeedPipelineAsync(db, repoId, ods, wave: 2, batch: batch);
            // A flow in a different batch must never be dragged in.
            await SeedPipelineAsync(db, repoId, other, wave: 0, batch: $"other_{suffix}");

            var schedule = await SeedScheduleAsync(db, repoId, copy, RunScopes.Batch);
            var fire = await ScheduleFire.EnqueueAsync(
                db, new RecordingDispatcher(), schedule, honorManualMode: true, DateTime.UtcNow, default);

            Assert.Equal(ScheduleFire.Outcome.EnqueuedGroup, fire.Outcome);
            Assert.NotNull(fire.GroupId);
            Assert.Equal(3, fire.MemberCount);

            // Every flow in the batch is queued under ONE group, each carrying the wave that orders it. The queue's
            // claim gate reads GroupWave, so these waves are what make the set run in dependency order.
            var queued = await db.Runs.AsNoTracking()
                .Where(r => r.RepoId == repoId).OrderBy(r => r.GroupWave).ToListAsync();
            Assert.Equal(new[] { copy, pre, ods }, queued.Select(r => r.FlowName).ToArray());
            Assert.Equal(new[] { 0, 1, 2 }, queued.Select(r => r.GroupWave).ToArray());
            Assert.All(queued, r => Assert.Equal(fire.GroupId, r.GroupId));

            var after = await db.Schedules.AsNoTracking().FirstAsync(s => s.Id == schedule.Id);
            Assert.Equal(fire.GroupId, after.LastGroupId);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Fire_NodeScope_EnqueuesTheAnchorAndItsDescendants_ConcurrentWaveSharesAWave()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var batch = $"bt_{suffix}";
        string anchor = $"a_{suffix}", left = $"b_{suffix}", right = $"c_{suffix}", unrelated = $"z_{suffix}";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await SeedPipelineAsync(db, repoId, anchor, wave: 0, batch: batch);
            // Two dependents at the same wave: they must be enqueued at the same wave so they run concurrently.
            await SeedPipelineAsync(db, repoId, left, wave: 1, batch: batch);
            await SeedPipelineAsync(db, repoId, right, wave: 1, batch: batch);
            // Same batch, but not downstream of the anchor: a node scope must not include it.
            await SeedPipelineAsync(db, repoId, unrelated, wave: 1, batch: batch);
            await SeedDependencyAsync(db, repoId, anchor, left);
            await SeedDependencyAsync(db, repoId, anchor, right);

            var schedule = await SeedScheduleAsync(db, repoId, anchor, RunScopes.Node);
            var fire = await ScheduleFire.EnqueueAsync(
                db, new RecordingDispatcher(), schedule, honorManualMode: true, DateTime.UtcNow, default);

            Assert.Equal(ScheduleFire.Outcome.EnqueuedGroup, fire.Outcome);
            Assert.Equal(3, fire.MemberCount);

            var queued = await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync();
            Assert.Equal(new[] { anchor, left, right }, queued.Select(r => r.FlowName).OrderBy(x => x).ToArray());
            Assert.DoesNotContain(queued, r => r.FlowName == unrelated);

            // The two dependents share wave 1: nothing gates them against each other, so they run concurrently.
            Assert.Equal(new[] { 1, 1 }, queued.Where(r => r.FlowName != anchor).Select(r => r.GroupWave).ToArray());
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Fire_BatchScope_WithNothingRunnable_ReportsScopeEmpty_AndEnqueuesNothing()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var batch = $"bt_{suffix}";
        var anchor = $"a_{suffix}";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            // The anchor is active (so the fire gets past the pipeline check) but is manual, so a batch expansion
            // excludes it and resolves to nothing. This must be reported, not thrown: the group enqueue rejects an
            // empty member list.
            await SeedPipelineAsync(db, repoId, anchor, wave: 0, batch: batch, mode: PipelineExecutionModes.Manual);
            var schedule = await SeedScheduleAsync(db, repoId, anchor, RunScopes.Batch);

            var fire = await ScheduleFire.EnqueueAsync(
                db, new RecordingDispatcher(), schedule, honorManualMode: false, DateTime.UtcNow, default);

            Assert.Equal(ScheduleFire.Outcome.ScopeEmpty, fire.Outcome);
            Assert.False(fire.Queued);
            Assert.Empty(await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync());
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    /// <summary>A dispatcher that enqueues through the real store (so the rows, group and waves are genuine) without
    /// the control plane's in-process signalling, which needs a hosted worker.</summary>
    private sealed class RecordingDispatcher : IRunDispatcher
    {
        public Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default)
            => RunQueueStore.EnqueueAsync(catalog, request, DateTime.UtcNow, ct);

        public Task<RunGroupEnqueueResult> EnqueueGroupAsync(
            CatalogDbContext catalog, RunGroupEnqueueRequest request, CancellationToken ct = default)
            => RunQueueStore.EnqueueGroupAsync(catalog, request, DateTime.UtcNow, ct);

        public Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<GroupCancelResult> CancelGroupAsync(CatalogDbContext catalog, Guid groupId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Guid> EnqueueComputeTaskAsync(CatalogDbContext catalog, ComputeTaskEnqueueRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<CancelOutcome> CancelComputeTaskAsync(CatalogDbContext catalog, Guid taskId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static (Guid RepoId, string Suffix) NewRepo()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("scope_" + suffix), suffix);
    }

    private static async Task SeedPipelineAsync(
        CatalogDbContext db, Guid repoId, string name, int wave, string batch, string? mode = null)
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
            ExecutionMode = mode ?? PipelineExecutionModes.Auto,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedDependencyAsync(CatalogDbContext db, Guid repoId, string from, string to)
    {
        db.FlowDependencies.Add(new CatalogFlowDependency
        {
            RepoId = repoId,
            FromFlow = from,
            ToFlow = to,
            FromPipelineId = CatalogIdentity.Pipeline(repoId, from),
            ToPipelineId = CatalogIdentity.Pipeline(repoId, to),
            ViaObjects = string.Empty,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<CatalogSchedule> SeedScheduleAsync(CatalogDbContext db, Guid repoId, string flowName, string scope)
    {
        var now = DateTime.UtcNow;
        await ScheduleStore.StageYamlUpsertAsync(
            db, repoId, flowName, scope, "0 4 * * *", null, "UTC", enabled: true, catchup: false, now.AddHours(1), now);
        await db.SaveChangesAsync();
        var id = CatalogIdentity.YamlSchedule(repoId, flowName);
        return await db.Schedules.AsNoTracking().FirstAsync(s => s.Id == id);
    }

    private static async Task Cleanup(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
        await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
    }
}
