using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>How much per-run trace is stored, how many event rows the retention policy would reclaim, and the
/// retention window in effect. <see cref="RetentionDays"/> is null when traces are kept forever.</summary>
public sealed record RunTraceStorageDto(long TotalEvents, long PrunableEvents, int PrunableRuns, int? RetentionDays);

/// <summary>A request to change the trace retention: how many days a superseded successful run keeps its trace,
/// or null to keep traces forever (age-based pruning off).</summary>
public sealed record RunTraceRetentionUpdateDto(int? RetentionDays);

/// <summary>The stored trace retention after an update; null means keep forever.</summary>
public sealed record RunTraceRetentionDto(int? RetentionDays);

/// <summary>The outcome of a manual trace prune or purge: how many event rows it deleted.</summary>
public sealed record RunEventPurgeResultDto(int EventsDeleted);

/// <summary>
/// Housekeeping the operator can see and drive from the GUI. Today that is the per-run trace (the run events): a
/// read reports how much is stored and how many rows are reclaimable under the retention policy; a write tunes
/// the retention (days, or forever); a prune applies the policy now; and a purge deletes every trace row now.
/// Each mutation returns its count so the GUI shows the real outcome and any database error surfaces instead of
/// being swallowed. The retention prune also runs automatically in <c>RunTraceReaper</c>. The delivery ledger
/// (the record history) is never touched here.
/// </summary>
public static class MaintenanceEndpoints
{
    // A generous ceiling on the retention window: enough for any real policy, low enough that TimeSpan.FromDays
    // never overflows and a fat-fingered value cannot silently mean "never prune".
    private const int MaxRetentionDays = 36500;

    public static RouteGroupBuilder MapMaintenanceEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var maintenance = group.MapGroup("/maintenance").WithTags("Maintenance");
        maintenance.MapGet("/trace-storage", GetTraceStorageAsync).WithName("GetRunTraceStorage");
        maintenance.MapPut("/trace-retention", SetTraceRetentionAsync).WithName("SetRunTraceRetention");
        maintenance.MapPost("/events/prune", PruneEventsAsync).WithName("PruneRunEvents");
        maintenance.MapPost("/events/purge", PurgeEventsAsync).WithName("PurgeRunEvents");
        return group;
    }

    private static async Task<Ok<RunTraceStorageDto>> GetTraceStorageAsync(
        CatalogDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var retentionDays = await MaintenanceStore.GetRunTraceRetentionDaysAsync(db, ct).ConfigureAwait(false);
        var summary = await RunTraceStore.SummarizeAsync(db, SupersededBefore(clock, retentionDays), ct).ConfigureAwait(false);
        return TypedResults.Ok(new RunTraceStorageDto(
            summary.TotalEvents,
            summary.PrunableEvents,
            summary.PrunableRuns,
            retentionDays));
    }

    private static async Task<Results<Ok<RunTraceRetentionDto>, ValidationProblem>> SetTraceRetentionAsync(
        RunTraceRetentionUpdateDto request,
        CatalogDbContext db,
        TimeProvider clock,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RetentionDays is { } days && (days < 0 || days > MaxRetentionDays))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(RunTraceRetentionUpdateDto.RetentionDays)] =
                    [$"RetentionDays must be null (keep forever) or between 0 and {MaxRetentionDays}."],
            });
        }

        var stored = await MaintenanceStore.SetRunTraceRetentionDaysAsync(
            db, request.RetentionDays, user.Identity?.Name, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        return TypedResults.Ok(new RunTraceRetentionDto(stored));
    }

    private static async Task<Ok<RunEventPurgeResultDto>> PruneEventsAsync(
        CatalogDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Applies the retention policy now, on the request's own catalog scope, and returns the count: the same
        // prune the reaper runs on its cadence, so the GUI can show what the policy reclaims without waiting.
        var retentionDays = await MaintenanceStore.GetRunTraceRetentionDaysAsync(db, ct).ConfigureAwait(false);
        var deleted = await RunTraceStore.PruneEventsAsync(db, SupersededBefore(clock, retentionDays), ct).ConfigureAwait(false);
        return TypedResults.Ok(new RunEventPurgeResultDto(deleted));
    }

    private static async Task<Ok<RunEventPurgeResultDto>> PurgeEventsAsync(
        CatalogDbContext db,
        CancellationToken ct)
    {
        // Deletes every run-event row here, on the request's own catalog scope. Run headers are untouched. A
        // database error propagates as a problem response, so the GUI sees a real failure, not a false "done".
        var deleted = await RunTraceStore.PurgeAllEventsAsync(db, ct).ConfigureAwait(false);
        return TypedResults.Ok(new RunEventPurgeResultDto(deleted));
    }

    private static DateTime? SupersededBefore(TimeProvider clock, int? retentionDays)
        => retentionDays is { } days ? clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(days) : null;
}
