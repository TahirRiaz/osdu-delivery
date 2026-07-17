using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A schedule as the API returns it: its timing, scope, lifecycle flags, source, and the next/last fire.</summary>
public sealed record ScheduleDto(
    Guid Id, Guid RepoId, Guid PipelineId, string FlowName, string Scope, string? Cron, int? IntervalSeconds, string Timezone,
    bool Enabled, bool Catchup, bool Paused, string Source, DateTime? NextFireUtc, DateTime? LastFireUtc, Guid? LastRunId,
    Guid? LastGroupId, DateTime CreatedUtc, DateTime UpdatedUtc);

/// <summary>The body to create an ad-hoc API schedule: a flow reference, exactly one of cron / intervalSeconds, and
/// optionally the scope the fire expands to (<c>flow</c> by default, or <c>node</c> / <c>batch</c>).</summary>
public sealed record CreateScheduleRequest(
    Guid RepoId, string FlowName, string? Cron, int? IntervalSeconds, string? Timezone, bool? Enabled,
    bool? Catchup = null, string? Scope = null);

/// <summary>The created-schedule acknowledgement.</summary>
public sealed record ScheduleCreated(Guid Id, DateTime? NextFireUtc);

/// <summary>The manual run-now acknowledgement: the run the fire enqueued (the group's first member for a scoped
/// schedule), plus the run group and member count when the scope expanded to a wave-ordered set.</summary>
public sealed record ScheduleRunAccepted(Guid RunId, Guid? GroupId = null, int MemberCount = 1);

/// <summary>One flow a schedule runs, and the wave that orders it within the fire.</summary>
public sealed record SchedulePlanMemberDto(string FlowName, string FlowKind, int Wave);

/// <summary>
/// When a schedule next runs and exactly what it executes: the cadence (so "when does this source get updated" is
/// answerable) plus the lineage-resolved flows in wave order (so "what runs, and in which order" is too). Members are
/// ordered by wave; every member sharing a wave runs concurrently, and a wave starts only once the previous one has
/// finished.
/// </summary>
public sealed record SchedulePlanDto(
    Guid ScheduleId, Guid RepoId, string FlowName, string Scope, string? Cron, int? IntervalSeconds, string Timezone,
    bool Enabled, bool Paused, DateTime? NextFireUtc, DateTime? LastFireUtc, string Anchor, int MemberCount,
    int WaveCount, IReadOnlyList<SchedulePlanMemberDto> Members);

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
        schedules.MapGet("/{id:guid}/plan", GetSchedulePlanAsync).WithName("GetSchedulePlan");
        return group;
    }

    public static RouteGroupBuilder MapScheduleWriteEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var schedules = group.MapGroup("/schedules").WithTags("Schedules");
        schedules.MapPost("/", CreateScheduleAsync).WithName("CreateSchedule");
        schedules.MapPost("/{id:guid}/run", RunScheduleAsync).WithName("RunScheduleNow");
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

    /// <summary>
    /// The schedule's cadence plus the flows it actually runs, in wave order. The membership comes from the same
    /// <see cref="RunScopeExpander"/> the fire itself uses, so this answers "when does this source get updated, and
    /// what runs in which order" with exactly what the scheduler will enqueue rather than a second opinion. A
    /// flow-scoped schedule reports its single flow.
    /// </summary>
    private static async Task<Results<Ok<SchedulePlanDto>, ProblemHttpResult>> GetSchedulePlanAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var schedule = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (schedule is null)
        {
            return TypedResults.Problem(detail: $"No schedule '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        // A stored scope is already validated on write; fall back to flow so a legacy/hand-edited row still reports a
        // plan instead of failing the read.
        var scope = RunScopeExpander.TryParseScope(schedule.Scope) ?? RunScope.Flow;
        var expansion = await RunScopeExpander
            .ExpandAsync(db, schedule.RepoId, schedule.FlowName, scope, batch: null, ct).ConfigureAwait(false);

        var members = expansion.Members
            .Select(m => new SchedulePlanMemberDto(m.FlowName, m.FlowKind, m.Wave))
            .ToList();
        return TypedResults.Ok(new SchedulePlanDto(
            schedule.Id, schedule.RepoId, schedule.FlowName, RunScopes.From(scope), schedule.Cron,
            schedule.IntervalSeconds, schedule.Timezone, schedule.Enabled, schedule.Paused, schedule.NextFireUtc,
            schedule.LastFireUtc, expansion.Anchor, members.Count,
            members.Select(m => m.Wave).Distinct().Count(), members));
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

        // The scope decides what a fire expands to; an unknown value is rejected here rather than silently narrowing
        // to a single flow, which would look like the schedule was simply not running the rest of the set.
        if (RunScopeExpander.TryParseScope(request.Scope) is not { } parsedScope)
        {
            return TypedResults.Problem(
                detail: $"Unknown schedule scope '{request.Scope}'. Use 'flow', 'node', or 'batch'.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid schedule");
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
            db, request.RepoId, flowName, RunScopes.From(parsedScope), request.Cron, request.IntervalSeconds, timezone,
            request.Enabled ?? true, request.Catchup ?? false, next ?? now, now, ct).ConfigureAwait(false);

        return TypedResults.Created($"/api/v1/schedules/{id}", new ScheduleCreated(id, next));
    }

    /// <summary>
    /// Fires a schedule right now, on demand: it enqueues the schedule's work through the same durable path the
    /// automatic scheduler and a direct manual trigger take, and stamps the schedule so the fire is visible in its
    /// "last run". A flow-scoped schedule enqueues one run; a node/batch-scoped one expands through lineage and
    /// enqueues a wave-ordered run group, and the response points at the group. The cadence is untouched (the next
    /// scheduled fire does not move), so this is a safe way to test a schedule. Unlike the automatic scheduler it also
    /// runs a flow declaring <c>mode: manual</c>, because the invoke is deliberate. Answers 202 with the run (and
    /// group) reference, 404 for an unknown schedule, and 409 when the scope has nothing runnable.
    /// </summary>
    private static async Task<Results<Accepted<ScheduleRunAccepted>, ProblemHttpResult>> RunScheduleAsync(
        Guid id, CatalogDbContext db, IRunDispatcher dispatcher, TimeProvider clock, CancellationToken ct)
    {
        var schedule = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (schedule is null)
        {
            return TypedResults.Problem(detail: $"No schedule '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var fire = await ScheduleFire
            .EnqueueAsync(db, dispatcher, schedule, honorManualMode: false, now, ct).ConfigureAwait(false);
        if (!fire.Queued)
        {
            var detail = fire.Outcome == ScheduleFire.Outcome.ScopeEmpty
                ? $"The {schedule.Scope} scope anchored on '{schedule.FlowName}' resolved to no runnable flow."
                : $"Flow '{schedule.FlowName}' is inactive or removed, so the schedule has nothing to run.";
            return TypedResults.Problem(
                detail: detail, statusCode: StatusCodes.Status409Conflict, title: "Cannot run schedule");
        }

        // A group fire points at the group, whose detail reflects the whole set as it executes; a single-flow fire
        // points at its run, exactly as before.
        var location = fire.GroupId is { } groupId
            ? $"/api/v1/runs/groups/{groupId}"
            : $"/api/v1/runs/{fire.RunId}";
        return TypedResults.Accepted(location, new ScheduleRunAccepted(fire.RunId, fire.GroupId, fire.MemberCount));
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
        s.Id, s.RepoId, s.PipelineId, s.FlowName, s.Scope, s.Cron, s.IntervalSeconds, s.Timezone,
        s.Enabled, s.Catchup, s.Paused, s.Source, s.NextFireUtc, s.LastFireUtc, s.LastRunId, s.LastGroupId,
        s.CreatedUtc, s.UpdatedUtc);
}
