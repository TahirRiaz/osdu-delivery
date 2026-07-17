using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>The result of a pause/resume or delete request, so the API can answer 200 / 404 precisely.</summary>
public enum ScheduleMutation
{
    Applied,
    NotFound,
}

/// <summary>
/// Persistence for the schedule table: the scheduler's due scan and atomic fire-claim, plus the upserts that keep
/// it in step with git (YAML schedules) and the API (ad-hoc schedules, pause/resume). Stateless like
/// <see cref="RunQueueStore"/>; the next-fire instants are computed by the control plane (which owns the cron
/// library and the time zone database) and passed in, so the catalog has no scheduling-math dependency.
/// </summary>
public static class ScheduleStore
{
    /// <summary>The active schedules whose next fire has arrived, oldest-due first. Read-only; the caller claims
    /// each one with <see cref="TryClaimFireAsync"/> before acting on it.</summary>
    public static async Task<IReadOnlyList<CatalogSchedule>> ListDueAsync(
        CatalogDbContext catalog, DateTime nowUtc, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.Schedules.AsNoTracking()
            .Where(s => s.Enabled && !s.Paused && s.NextFireUtc != null && s.NextFireUtc <= nowUtc)
            .OrderBy(s => s.NextFireUtc)
            .Take(Math.Clamp(max, 1, 1000))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically claims a schedule's fire by advancing <see cref="CatalogSchedule.NextFireUtc"/> from the value the
    /// caller observed to the next occurrence. Returns true only if this caller won the race: the compare-and-swap on
    /// the observed next-fire means that when several control-plane nodes scan the same due schedule, exactly one
    /// advances it (and so exactly one enqueues the run); the losers see zero rows affected and move on.
    /// </summary>
    public static async Task<bool> TryClaimFireAsync(
        CatalogDbContext catalog, Guid id, DateTime observedNextFireUtc, DateTime? newNextFireUtc, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.Schedules
            .Where(s => s.Id == id && s.NextFireUtc == observedNextFireUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.NextFireUtc, newNextFireUtc)
                .SetProperty(x => x.LastFireUtc, nowUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>Records the run a fire enqueued, so a scheduled run traces back to its schedule. Clears
    /// <see cref="CatalogSchedule.LastGroupId"/>: a flow-scoped fire is a single run, not a set.</summary>
    public static Task SetLastRunAsync(CatalogDbContext catalog, Guid id, Guid runId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Schedules
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastRunId, runId)
                .SetProperty(x => x.LastGroupId, (Guid?)null)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct);
    }

    /// <summary>Records the wave-gated group a scoped fire enqueued, so the schedule traces to the whole set (and to
    /// its first member, keeping <see cref="CatalogSchedule.LastRunId"/> meaningful for callers that show one run).</summary>
    public static Task SetLastGroupAsync(
        CatalogDbContext catalog, Guid id, Guid groupId, Guid firstRunId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Schedules
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastRunId, firstRunId)
                .SetProperty(x => x.LastGroupId, groupId)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct);
    }

    /// <summary>
    /// Upserts a YAML-declared schedule (deterministic id per flow). A new schedule is inserted with the computed
    /// next fire. An existing one has its definition refreshed from git, but the operational state set through the
    /// API is preserved: a <see cref="CatalogSchedule.Paused"/> flag survives the re-sync, and the next-fire cadence
    /// is only reset when the cron/interval/time-zone actually changed (an unchanged sync never disturbs the
    /// firing rhythm). Returns the schedule id.
    /// </summary>
    public static Task<Guid> UpsertYamlScheduleAsync(
        CatalogDbContext catalog, Guid repoId, string flowName, string scope, string? cron, int? intervalSeconds,
        string timezone, bool enabled, bool catchup, DateTime computedNextFireUtc, DateTime nowUtc, CancellationToken ct = default)
        => CatalogTransaction.InSerializableAsync(
            catalog,
            () => StageYamlUpsertAsync(catalog, repoId, flowName, scope, cron, intervalSeconds, timezone, enabled, catchup, computedNextFireUtc, nowUtc, ct),
            ct);

    /// <summary>The transaction-free core of the YAML upsert: it stages the insert/update on the context but does
    /// not open a transaction or save, so the catalog sync can call it for every flow inside its own one
    /// transaction (the public <see cref="UpsertYamlScheduleAsync"/> wraps this for standalone callers).</summary>
    public static async Task<Guid> StageYamlUpsertAsync(
        CatalogDbContext catalog, Guid repoId, string flowName, string scope, string? cron, int? intervalSeconds,
        string timezone, bool enabled, bool catchup, DateTime computedNextFireUtc, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var id = CatalogIdentity.YamlSchedule(repoId, flowName);
        {
            var existing = await catalog.Schedules.AsTracking()
                .FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
            if (existing is null)
            {
                catalog.Schedules.Add(new CatalogSchedule
                {
                    Id = id,
                    RepoId = repoId,
                    PipelineId = CatalogIdentity.Pipeline(repoId, flowName),
                    FlowName = flowName,
                    Scope = scope,
                    Cron = cron,
                    IntervalSeconds = intervalSeconds,
                    Timezone = timezone,
                    Enabled = enabled,
                    Catchup = catchup,
                    Source = "yaml",
                    NextFireUtc = computedNextFireUtc,
                    CreatedUtc = nowUtc,
                    UpdatedUtc = nowUtc,
                });
                return id;
            }

            // Only the TIMING definition resets the cadence. The scope changes what a fire runs, not when, so
            // re-scoping a schedule must not shift its next fire or disturb its rhythm.
            var definitionChanged = existing.Cron != cron
                || existing.IntervalSeconds != intervalSeconds
                || existing.Timezone != timezone;

            existing.RepoId = repoId;
            existing.PipelineId = CatalogIdentity.Pipeline(repoId, flowName);
            existing.FlowName = flowName;
            existing.Scope = scope;
            existing.Cron = cron;
            existing.IntervalSeconds = intervalSeconds;
            existing.Timezone = timezone;
            existing.Enabled = enabled;
            existing.Catchup = catchup;
            existing.Source = "yaml";
            existing.UpdatedUtc = nowUtc;
            // Only reset the cadence when the timing definition changed; an unchanged re-sync leaves the next fire
            // (and the operator's pause) exactly as they were.
            if (definitionChanged || existing.NextFireUtc is null)
            {
                existing.NextFireUtc = computedNextFireUtc;
            }

            return id;
        }
    }

    /// <summary>Stages the removal of YAML schedules for a repo whose flows left the estate (so a schedule deleted
    /// from git stops firing), without opening a transaction or saving: it loads the stale rows and marks them for
    /// deletion so the caller's sync transaction removes them. API-created schedules are never touched.</summary>
    public static async Task StageRemoveYamlSchedulesNotInAsync(
        CatalogDbContext catalog, Guid repoId, IReadOnlyCollection<Guid> keepIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var stale = await catalog.Schedules.AsTracking()
            .Where(s => s.RepoId == repoId && s.Source == "yaml" && !keepIds.Contains(s.Id))
            .ToListAsync(ct).ConfigureAwait(false);
        catalog.Schedules.RemoveRange(stale);
    }

    /// <summary>Stages the removal of ONE flow's YAML schedule, the single-flow counterpart to
    /// <see cref="StageRemoveYamlSchedulesNotInAsync"/>: the per-run write-back calls this when a flow's YAML no
    /// longer declares a usable schedule, so the mirror stops firing without touching any other flow's rows.
    /// API-created schedules are never touched. Staged on the context; the caller's transaction commits it.</summary>
    public static async Task StageRemoveYamlScheduleAsync(
        CatalogDbContext catalog, Guid repoId, string flowName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        var id = CatalogIdentity.YamlSchedule(repoId, flowName);
        var row = await catalog.Schedules.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.Source == "yaml", ct).ConfigureAwait(false);
        if (row is not null)
        {
            catalog.Schedules.Remove(row);
        }
    }

    /// <summary>Creates an ad-hoc API schedule with a fresh id; a flow can carry its git schedule plus API ones.</summary>
    public static Task<Guid> CreateApiScheduleAsync(
        CatalogDbContext catalog, Guid repoId, string flowName, string scope, string? cron, int? intervalSeconds,
        string timezone, bool enabled, bool catchup, DateTime computedNextFireUtc, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var id = Guid.CreateVersion7();
        return CatalogTransaction.InSerializableAsync(catalog, () =>
        {
            catalog.Schedules.Add(new CatalogSchedule
            {
                Id = id,
                RepoId = repoId,
                PipelineId = CatalogIdentity.Pipeline(repoId, flowName),
                FlowName = flowName,
                Scope = scope,
                Cron = cron,
                IntervalSeconds = intervalSeconds,
                Timezone = timezone,
                Enabled = enabled,
                Catchup = catchup,
                Source = "api",
                NextFireUtc = computedNextFireUtc,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
            });
            return Task.FromResult(id);
        }, ct);
    }

    /// <summary>Pauses or resumes a schedule (the API operational override). Resuming sets a fresh next fire so a
    /// schedule paused across many missed occurrences does not fire a burst on resume.</summary>
    public static async Task<ScheduleMutation> SetPausedAsync(
        CatalogDbContext catalog, Guid id, bool paused, DateTime? nextFireUtcOnResume, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.Schedules
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Paused, paused)
                .SetProperty(x => x.NextFireUtc, x => paused ? x.NextFireUtc : nextFireUtcOnResume)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0 ? ScheduleMutation.Applied : ScheduleMutation.NotFound;
    }

    /// <summary>Deletes a schedule by id (the API delete).</summary>
    public static async Task<ScheduleMutation> DeleteAsync(CatalogDbContext catalog, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.Schedules.Where(s => s.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return affected > 0 ? ScheduleMutation.Applied : ScheduleMutation.NotFound;
    }
}
