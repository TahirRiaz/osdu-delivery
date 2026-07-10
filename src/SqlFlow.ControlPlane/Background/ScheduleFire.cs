using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Turns a schedule into a queued run: the single place a schedule becomes a run, shared by the scheduler's automatic
/// fire and the manual "run now" endpoint. It verifies the flow is still active, enqueues onto the durable run queue
/// (the same path a manual flow trigger takes), and stamps the schedule's last run so the run traces back to it.
/// The scheduler's timing (the claim and next-fire math) is deliberately not here: the automatic caller claims an
/// occurrence before invoking this, and a manual invoke never touches the cadence at all.
/// </summary>
public static class ScheduleFire
{
    /// <summary>Why a fire did or did not enqueue, so each caller can report it in its own idiom (the scheduler logs,
    /// the endpoint answers a status code).</summary>
    public enum Outcome
    {
        Enqueued,
        PipelineInactive,
        PipelineManual,
    }

    /// <summary>
    /// Enqueues a run for the schedule's flow and stamps its last run. Returns <see cref="Outcome.PipelineInactive"/>
    /// when the flow is removed or deactivated (nothing is enqueued), and <see cref="Outcome.PipelineManual"/> when
    /// <paramref name="honorManualMode"/> is set and the flow declares <c>mode: manual</c> (the automatic scheduler
    /// leaves it un-fired). An explicit run-now passes <paramref name="honorManualMode"/> false, so a manual-mode
    /// flow is still run because the user invoked it deliberately.
    /// </summary>
    public static async Task<(Outcome Outcome, Guid RunId)> EnqueueAsync(
        CatalogDbContext catalog, IRunDispatcher dispatcher, CatalogSchedule schedule,
        bool honorManualMode, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(schedule);

        // The flow must still exist and be active; a schedule for a removed/deactivated flow enqueues nothing.
        var pipeline = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.Id == schedule.PipelineId && p.RepoId == schedule.RepoId)
            .Select(p => new { p.Active, p.Kind, p.ExecutionMode })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is not { Active: true })
        {
            return (Outcome.PipelineInactive, Guid.Empty);
        }

        // A manual-mode flow (mode: manual in its document) opted out of every automatic dispatch, and a schedule is
        // exactly that, so the automatic scheduler skips it. A deliberate run-now is not automatic dispatch and runs
        // it regardless, matching the direct manual trigger which never consults the execution mode.
        if (honorManualMode &&
            string.Equals(pipeline.ExecutionMode, PipelineExecutionModes.Manual, StringComparison.OrdinalIgnoreCase))
        {
            return (Outcome.PipelineManual, Guid.Empty);
        }

        // Scheduled and run-now runs are untargeted (any node) and unpinned, exactly like the schedule fire path.
        var runId = await dispatcher.EnqueueAsync(
            catalog, new RunEnqueueRequest(schedule.RepoId, schedule.FlowName, pipeline.Kind), ct).ConfigureAwait(false);
        await ScheduleStore.SetLastRunAsync(catalog, schedule.Id, runId, nowUtc, ct).ConfigureAwait(false);
        return (Outcome.Enqueued, runId);
    }
}
