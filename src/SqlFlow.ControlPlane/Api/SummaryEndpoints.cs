using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>Run counts by lifecycle state, plus how many ran in the last 24 hours.</summary>
public sealed record RunCountsDto(long Queued, long Running, long Succeeded, long Failed, long Cancelled, long Last24h);

/// <summary>The control-plane dashboard rollup a GUI landing page shows: estate size, the run queue's live state,
/// fleet liveness, scheduling, and managed-sync health, computed at read time.</summary>
public sealed record DashboardDto(
    long Repos, long Pipelines, long ActivePipelines, RunCountsDto Runs,
    long NodesOnline, long NodesTotal, long SchedulesEnabled, long SchedulesPaused,
    long RepoSources, long RepoSourcesWithErrors, DateTime AsOfUtc);

/// <summary>
/// The dashboard read surface: <c>GET /api/v1/summary</c> aggregates the catalog into the one-call overview a GUI
/// landing page needs, so the front end does not fan out a dozen list calls just to render headline numbers. Read
/// scope; every figure is a cheap COUNT computed at read time, so it is always current.
/// </summary>
public static class SummaryEndpoints
{
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(60);

    public static RouteGroupBuilder MapSummaryEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/summary", GetSummaryAsync).WithTags("Summary").WithName("GetSummary");
        return group;
    }

    private static async Task<Ok<DashboardDto>> GetSummaryAsync(CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var dayAgo = now.AddDays(-1);
        var onlineSince = now - OnlineWindow;

        var runs = new RunCountsDto(
            Queued: await db.Runs.CountAsync(r => r.Status == RunStatuses.Queued, ct).ConfigureAwait(false),
            Running: await db.Runs.CountAsync(r => r.Status == RunStatuses.Running, ct).ConfigureAwait(false),
            Succeeded: await db.Runs.CountAsync(r => r.Status == RunStatuses.Succeeded, ct).ConfigureAwait(false),
            Failed: await db.Runs.CountAsync(r => r.Status == RunStatuses.Failed, ct).ConfigureAwait(false),
            Cancelled: await db.Runs.CountAsync(r => r.Status == RunStatuses.Cancelled, ct).ConfigureAwait(false),
            Last24h: await db.Runs.CountAsync(r => r.WrittenUtc >= dayAgo, ct).ConfigureAwait(false));

        var dashboard = new DashboardDto(
            Repos: await db.Repos.CountAsync(ct).ConfigureAwait(false),
            Pipelines: await db.Pipelines.CountAsync(ct).ConfigureAwait(false),
            ActivePipelines: await db.Pipelines.CountAsync(p => p.Active, ct).ConfigureAwait(false),
            Runs: runs,
            NodesOnline: await db.Nodes.CountAsync(n => n.LastSeenUtc >= onlineSince, ct).ConfigureAwait(false),
            NodesTotal: await db.Nodes.CountAsync(ct).ConfigureAwait(false),
            SchedulesEnabled: await db.Schedules.CountAsync(s => s.Enabled && !s.Paused, ct).ConfigureAwait(false),
            SchedulesPaused: await db.Schedules.CountAsync(s => s.Paused, ct).ConfigureAwait(false),
            RepoSources: await db.RepoSources.CountAsync(ct).ConfigureAwait(false),
            RepoSourcesWithErrors: await db.RepoSources.CountAsync(s => s.LastError != null, ct).ConfigureAwait(false),
            AsOfUtc: now);

        return TypedResults.Ok(dashboard);
    }
}
