using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Turns a schedule into queued work: the single place a schedule becomes runs, shared by the scheduler's automatic
/// fire and the manual "run now" endpoint. What a fire enqueues follows the schedule's <see cref="CatalogSchedule.Scope"/>:
/// a flow-scoped schedule enqueues its own flow as one run, while a node/batch-scoped schedule expands through the
/// lineage graph (<see cref="RunScopeExpander"/>) and enqueues the resolved set as ONE wave-gated run group, so the
/// members execute in dependency order (waves ascending, members within a wave concurrently) instead of racing. Both
/// paths go through the durable run queue, exactly as a manual trigger does, and stamp the schedule so the work traces
/// back to it. The scheduler's timing (the claim and next-fire math) is deliberately not here: the automatic caller
/// claims an occurrence before invoking this, and a manual invoke never touches the cadence at all.
/// </summary>
public static class ScheduleFire
{
    /// <summary>Why a fire did or did not enqueue, so each caller can report it in its own idiom (the scheduler logs,
    /// the endpoint answers a status code).</summary>
    public enum Outcome
    {
        /// <summary>A flow-scoped fire enqueued one run.</summary>
        Enqueued,

        /// <summary>A node/batch-scoped fire enqueued a wave-gated group of runs.</summary>
        EnqueuedGroup,

        /// <summary>The schedule's own flow is removed or deactivated.</summary>
        PipelineInactive,

        /// <summary>The flow declares <c>mode: manual</c> and this was an automatic fire.</summary>
        PipelineManual,

        /// <summary>The scope resolved to no runnable flow (an empty batch, or every member deactivated/manual).</summary>
        ScopeEmpty,
    }

    /// <summary>The result of a fire: the outcome, the run it enqueued (the group's first member for a scoped fire),
    /// the group id when the scope expanded to a set, and how many flows were enqueued.</summary>
    public readonly record struct FireResult(Outcome Outcome, Guid RunId, Guid? GroupId, int MemberCount)
    {
        /// <summary>Whether the fire actually queued work (either shape).</summary>
        public bool Queued => Outcome is Outcome.Enqueued or Outcome.EnqueuedGroup;
    }

    /// <summary>
    /// Enqueues the schedule's work and stamps it on the schedule. Returns <see cref="Outcome.PipelineInactive"/> when
    /// the schedule's flow is removed or deactivated (nothing is enqueued), <see cref="Outcome.PipelineManual"/> when
    /// <paramref name="honorManualMode"/> is set and the flow declares <c>mode: manual</c> (the automatic scheduler
    /// leaves it un-fired), and <see cref="Outcome.ScopeEmpty"/> when a node/batch scope resolved to nothing runnable.
    /// An explicit run-now passes <paramref name="honorManualMode"/> false, so a manual-mode flow is still run because
    /// the user invoked it deliberately.
    /// </summary>
    public static async Task<FireResult> EnqueueAsync(
        CatalogDbContext catalog, IRunDispatcher dispatcher, CatalogSchedule schedule,
        bool honorManualMode, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(schedule);

        // The flow must still exist and be active; a schedule for a removed/deactivated flow enqueues nothing. This
        // holds for every scope: the schedule's own flow is the anchor a node/batch expansion starts from.
        var pipeline = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.Id == schedule.PipelineId && p.RepoId == schedule.RepoId)
            .Select(p => new { p.Active, p.Kind, p.ExecutionMode })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is not { Active: true })
        {
            return new FireResult(Outcome.PipelineInactive, Guid.Empty, null, 0);
        }

        // A manual-mode flow (mode: manual in its document) opted out of every automatic dispatch, and a schedule is
        // exactly that, so the automatic scheduler skips it. A deliberate run-now is not automatic dispatch and runs
        // it regardless, matching the direct manual trigger which never consults the execution mode.
        if (honorManualMode &&
            string.Equals(pipeline.ExecutionMode, PipelineExecutionModes.Manual, StringComparison.OrdinalIgnoreCase))
        {
            return new FireResult(Outcome.PipelineManual, Guid.Empty, null, 0);
        }

        // An unknown scope is treated as flow rather than failing the fire: the scope is validated where it is written
        // (the sync warns, the API rejects), so a row that somehow holds a bad value still fires its own flow instead
        // of silently going dark.
        var scope = RunScopeExpander.TryParseScope(schedule.Scope) ?? RunScope.Flow;
        if (scope == RunScope.Flow)
        {
            var runId = await dispatcher.EnqueueAsync(
                catalog, new RunEnqueueRequest(schedule.RepoId, schedule.FlowName, pipeline.Kind), ct).ConfigureAwait(false);
            await ScheduleStore.SetLastRunAsync(catalog, schedule.Id, runId, nowUtc, ct).ConfigureAwait(false);
            return new FireResult(Outcome.Enqueued, runId, null, 1);
        }

        // Lineage decides what runs and in what order: the expansion reads the flow dependency edges and each
        // pipeline's topological wave, and returns the active members ordered by wave. Enqueuing them as one group
        // makes the queue's claim gate the order (a member is claimable only once every lower wave is terminal), so
        // waves run in sequence while the members of a wave run concurrently.
        var expansion = await RunScopeExpander
            .ExpandAsync(catalog, schedule.RepoId, schedule.FlowName, scope, batch: null, ct).ConfigureAwait(false);
        if (expansion.Members.Count == 0)
        {
            // The group enqueue throws on an empty member list, so an empty scope is answered here instead: a batch
            // whose flows are all deactivated or manual is a normal state, not a fault.
            return new FireResult(Outcome.ScopeEmpty, Guid.Empty, null, 0);
        }

        var mode = scope == RunScope.Node ? RunGroupModes.Node : RunGroupModes.Batch;
        var result = await dispatcher.EnqueueGroupAsync(
            catalog,
            new RunGroupEnqueueRequest(schedule.RepoId, mode, expansion.Anchor, expansion.Members),
            ct).ConfigureAwait(false);

        var firstRunId = result.RunIds.Count > 0 ? result.RunIds[0] : Guid.Empty;
        await ScheduleStore.SetLastGroupAsync(catalog, schedule.Id, result.GroupId, firstRunId, nowUtc, ct).ConfigureAwait(false);
        return new FireResult(Outcome.EnqueuedGroup, firstRunId, result.GroupId, expansion.Members.Count);
    }
}
