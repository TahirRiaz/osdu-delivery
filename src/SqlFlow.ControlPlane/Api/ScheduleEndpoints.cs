using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A schedule as the API returns it: its timing, lifecycle flags, source, and the next/last fire.</summary>
public sealed record ScheduleDto(
    Guid Id, Guid RepoId, Guid PipelineId, string FlowName, string? Cron, int? IntervalSeconds, string Timezone,
    bool Enabled, bool Paused, string Source, DateTime? NextFireUtc, DateTime? LastFireUtc, Guid? LastRunId,
    DateTime CreatedUtc, DateTime UpdatedUtc);

/// <summary>The body to create an ad-hoc API schedule: a flow reference and exactly one of cron / intervalSeconds.</summary>
public sealed record CreateScheduleRequest(
    Guid RepoId, string FlowName, string? Cron, int? IntervalSeconds, string? Timezone, bool? Enabled);

/// <summary>The created-schedule acknowledgement.</summary>
public sealed record ScheduleCreated(Guid Id, DateTime? NextFireUtc);

/// <summary>
/// The schedule surface over the catalog. The reads (list, detail) are mapped under the "read" scope; the mutations
/// (create an ad-hoc schedule, pause/resume an operational override, delete) under "operate". A schedule fires by
/// enqueuing onto the durable run queue, so this surface only manages WHEN; the run itself goes through the same
/// path as a manual trigger.
/// </summary>
public static class ScheduleEndpoints
{
    public static RouteGroupBuilder MapScheduleReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var schedules = group.MapGroup("/schedules").WithTags("Schedules");
        schedules.MapGet("/", ListSchedulesAsync).WithName("ListSchedules");
        schedules.MapGet("/{id:guid}", GetScheduleAsync).WithName("GetSchedule");
        return group;
    }

    public static RouteGroupBuilder MapScheduleWriteEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var schedules = group.MapGroup("/schedules").WithTags("Schedules");
        schedules.MapPost("/", CreateScheduleAsync).WithName("CreateSchedule");
        schedules.MapPost("/{id:guid}/pause", PauseScheduleAsync).WithName("PauseSchedule");
        schedules.MapPost("/{id:guid}/resume", ResumeScheduleAsync).WithName("ResumeSchedule");
        schedules.MapDelete("/{id:guid}", DeleteScheduleAsync).WithName("DeleteSchedule");
        return group;
    }

    private static async Task<Ok<PagedResult<ScheduleDto>>> ListSchedulesAsync(
        CatalogDbContext db, Guid? repoId, Guid? pipelineId, string? source, bool? enabled,
        int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = db.Schedules.AsNoTracking().AsQueryable();
        if (repoId is { } r)
        {
            query = query.Where(s => s.RepoId == r);
        }

        if (pipelineId is { } pid)
        {
            query = query.Where(s => s.PipelineId == pid);
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            var normalized = source.Trim().ToLowerInvariant();
            query = query.Where(s => s.Source == normalized);
        }

        if (enabled is { } e)
        {
            query = query.Where(s => s.Enabled == e);
        }

        var ordered = query.OrderBy(s => s.FlowName).ThenBy(s => s.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered.Skip((p - 1) * size).Take(size)
            .Select(Project).ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<ScheduleDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<ScheduleDto>, ProblemHttpResult>> GetScheduleAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var dto = await db.Schedules.AsNoTracking().Where(s => s.Id == id)
            .Select(Project).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return dto is null
            ? TypedResults.Problem(detail: $"No schedule '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found")
            : TypedResults.Ok(dto);
    }

    private static async Task<Results<Created<ScheduleCreated>, ProblemHttpResult>> CreateScheduleAsync(
        CreateScheduleRequest request, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.FlowName))
        {
            return TypedResults.Problem(
                detail: "A schedule requires a non-blank flowName.", statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "UTC" : request.Timezone.Trim();
        if (!ScheduleClock.TryValidate(request.Cron, request.IntervalSeconds, timezone, out var error))
        {
            return TypedResults.Problem(detail: error, statusCode: StatusCodes.Status400BadRequest, title: "Invalid schedule");
        }

        var flowName = request.FlowName.Trim();
        var pipelineId = CatalogIdentity.Pipeline(request.RepoId, flowName);
        var active = await db.Pipelines.AsNoTracking()
            .Where(pl => pl.Id == pipelineId && pl.RepoId == request.RepoId)
            .Select(pl => (bool?)pl.Active).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (active != true)
        {
            return TypedResults.Problem(
                detail: $"No active pipeline '{flowName}' in repo '{request.RepoId}'.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var next = ScheduleClock.NextFire(request.Cron, request.IntervalSeconds, timezone, now);
        var id = await ScheduleStore.CreateApiScheduleAsync(
            db, request.RepoId, flowName, request.Cron, request.IntervalSeconds, timezone,
            request.Enabled ?? true, next ?? now, now, ct).ConfigureAwait(false);

        return TypedResults.Created($"/api/v1/schedules/{id}", new ScheduleCreated(id, next));
    }

    private static Task<Results<Ok<ScheduleDto>, ProblemHttpResult>> PauseScheduleAsync(
        Guid id, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
        => SetPausedAsync(id, paused: true, db, clock, ct);

    private static Task<Results<Ok<ScheduleDto>, ProblemHttpResult>> ResumeScheduleAsync(
        Guid id, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
        => SetPausedAsync(id, paused: false, db, clock, ct);

    private static async Task<Results<Ok<ScheduleDto>, ProblemHttpResult>> SetPausedAsync(
        Guid id, bool paused, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        // On resume, recompute the next fire from now so a schedule paused across many missed occurrences does not
        // fire a burst when it is resumed.
        DateTime? nextOnResume = null;
        if (!paused)
        {
            var spec = await db.Schedules.AsNoTracking().Where(s => s.Id == id)
                .Select(s => new { s.Cron, s.IntervalSeconds, s.Timezone }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (spec is null)
            {
                return TypedResults.Problem(detail: $"No schedule '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
            }

            nextOnResume = ScheduleClock.NextFire(spec.Cron, spec.IntervalSeconds, spec.Timezone, now) ?? now;
        }

        var outcome = await ScheduleStore.SetPausedAsync(db, id, paused, nextOnResume, now, ct).ConfigureAwait(false);
        if (outcome == ScheduleMutation.NotFound)
        {
            return TypedResults.Problem(detail: $"No schedule '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var dto = await db.Schedules.AsNoTracking().Where(s => s.Id == id)
            .Select(Project).FirstAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(dto);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteScheduleAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var outcome = await ScheduleStore.DeleteAsync(db, id, ct).ConfigureAwait(false);
        return outcome == ScheduleMutation.Applied
            ? TypedResults.NoContent()
            : TypedResults.Problem(detail: $"No schedule '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
    }

    // An expression (not a method) so EF Core can translate the projection into the SELECT column list.
    private static readonly Expression<Func<CatalogSchedule, ScheduleDto>> Project = s => new ScheduleDto(
        s.Id, s.RepoId, s.PipelineId, s.FlowName, s.Cron, s.IntervalSeconds, s.Timezone,
        s.Enabled, s.Paused, s.Source, s.NextFireUtc, s.LastFireUtc, s.LastRunId, s.CreatedUtc, s.UpdatedUtc);
}
