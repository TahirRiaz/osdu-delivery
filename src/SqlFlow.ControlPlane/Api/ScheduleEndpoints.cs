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
    Guid Id, Guid RepoId, string Name, IReadOnlyList<Guid> MemberPipelineIds, string? Cron, int? IntervalSeconds, string Timezone,
    bool Enabled, bool Catchup, bool Paused, string Source, DateTime? NextFireUtc, DateTime? LastFireUtc, Guid? LastRunId,
    Guid? LastGroupId, DateTime CreatedUtc, DateTime UpdatedUtc);

/// <summary>The body to create an ad-hoc API schedule: the member flows it runs, exactly one of cron /
/// intervalSeconds, and optionally a name (defaulting to the first member's flow name). Membership is what a fire
/// runs, so a schedule with no members is rejected.</summary>
public sealed record CreateScheduleRequest(
    Guid RepoId, IReadOnlyList<string> Members, string? Cron, int? IntervalSeconds, string? Timezone, bool? Enabled,
    bool? Catchup = null, string? Name = null);

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
    Guid ScheduleId, Guid RepoId, string Name, string? Cron, int? IntervalSeconds, string Timezone,
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
            // "Which schedules run this flow?", answered by MEMBERSHIP rather than ownership. A flow that declares no
            // schedule of its own but joined one with 'schedule: <name>' is still listed here, which is the whole
            // point: it is scheduled, and the GUI must be able to say so.
            query = query.Where(s => db.ScheduleMembers.Any(m => m.ScheduleId == s.Id && m.PipelineId == pid));
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

        var ordered = query.OrderBy(s => s.Name).ThenBy(s => s.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered.Skip((p - 1) * size).Take(size)
            .Select(Project(db)).ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<ScheduleDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<ScheduleDto>, ProblemHttpResult>> GetScheduleAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var dto = await db.Schedules.AsNoTracking().Where(s => s.Id == id)
            .Select(Project(db)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return dto is null
            ? TypedResults.Problem(detail: $"No schedule '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found")
            : TypedResults.Ok(dto);
    }

    /// <summary>
    /// The schedule's cadence plus the flows it actually runs, in wave order. The membership comes from the same
    /// <see cref="RunScopeExpander"/> the fire itself uses, so this answers "when does this source get updated, and
    /// what runs in which order" with exactly what the scheduler will enqueue rather than a second opinion.
    /// </summary>
    private static async Task<Results<Ok<SchedulePlanDto>, ProblemHttpResult>> GetSchedulePlanAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var schedule = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (schedule is null)
        {
            return TypedResults.Problem(detail: $"No schedule '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var expansion = await RunScopeExpander
            .ExpandScheduleAsync(db, schedule.RepoId, schedule.Id, schedule.Name, batchFilter: null, ct).ConfigureAwait(false);

        var members = expansion.Members
            .Select(m => new SchedulePlanMemberDto(m.FlowName, m.FlowKind, m.Wave))
            .ToList();
        return TypedResults.Ok(new SchedulePlanDto(
            schedule.Id, schedule.RepoId, schedule.Name, schedule.Cron,
            schedule.IntervalSeconds, schedule.Timezone, schedule.Enabled, schedule.Paused, schedule.NextFireUtc,
            schedule.LastFireUtc, expansion.Anchor, members.Count,
            members.Select(m => m.Wave).Distinct().Count(), members));
    }

    private static async Task<Results<Created<ScheduleCreated>, ProblemHttpResult>> CreateScheduleAsync(
        CreateScheduleRequest request, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (request is null || request.Members is not { Count: > 0 })
        {
            return TypedResults.Problem(
                detail: "A schedule requires at least one member flow: membership is what a fire runs.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "UTC" : request.Timezone.Trim();
        if (!ScheduleClock.TryValidate(request.Cron, request.IntervalSeconds, timezone, out var error))
        {
            return TypedResults.Problem(detail: error, statusCode: StatusCodes.Status400BadRequest, title: "Invalid schedule");
        }

        var members = request.Members
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (members.Count == 0)
        {
            return TypedResults.Problem(
                detail: "A schedule requires at least one non-blank member flow name.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // Every member must be a real, active flow: a schedule pointing at a name that does not exist would sit in
        // the catalog looking armed while firing nothing.
        var memberIds = members.ToDictionary(m => CatalogIdentity.Pipeline(request.RepoId, m), m => m);
        var activeIds = await db.Pipelines.AsNoTracking()
            .Where(pl => pl.RepoId == request.RepoId && pl.Active && memberIds.Keys.Contains(pl.Id))
            .Select(pl => pl.Id).ToListAsync(ct).ConfigureAwait(false);
        var missing = memberIds.Where(kv => !activeIds.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        if (missing.Count > 0)
        {
            return TypedResults.Problem(
                detail: $"No active pipeline in repo '{request.RepoId}' for: {string.Join(", ", missing)}.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        // The name is this schedule's identity in the repo, so a collision with a git-declared or existing API
        // schedule is rejected here rather than left to the unique index to surface as a 500.
        var name = string.IsNullOrWhiteSpace(request.Name) ? members[0] : request.Name.Trim();
        if (await db.Schedules.AsNoTracking()
                .AnyAsync(s => s.RepoId == request.RepoId && s.Name == name, ct).ConfigureAwait(false))
        {
            return TypedResults.Problem(
                detail: $"Repo '{request.RepoId}' already has a schedule named '{name}'.",
                statusCode: StatusCodes.Status409Conflict, title: "Duplicate schedule");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var next = ScheduleClock.NextFire(request.Cron, request.IntervalSeconds, timezone, now);
        var id = await ScheduleStore.CreateApiScheduleAsync(
            db, request.RepoId, name, members, request.Cron, request.IntervalSeconds, timezone,
            request.Enabled ?? true, request.Catchup ?? false, next ?? now, now, ct).ConfigureAwait(false);

        return TypedResults.Created($"/api/v1/schedules/{id}", new ScheduleCreated(id, next));
    }

    /// <summary>
    /// Fires a schedule right now, on demand: it enqueues the schedule's member set through the same durable path the
    /// automatic scheduler and a direct manual trigger take, and stamps the schedule so the fire is visible in its
    /// "last run". One member enqueues one run; several expand in wave order into a run group, and the response points
    /// at the group. The cadence is untouched (the next scheduled fire does not move), so this is a safe way to test a
    /// schedule.
    /// <para>
    /// The optional <c>batch</c> query parameter narrows the fire to members carrying that <c>batch:</c> tag: "run the
    /// nightly, but only the small tables". It can only select a subset of the schedule's own members.
    /// </para>
    /// Answers 202 with the run (and group) reference, 404 for an unknown schedule, and 409 when nothing is runnable.
    /// </summary>
    private static async Task<Results<Accepted<ScheduleRunAccepted>, ProblemHttpResult>> RunScheduleAsync(
        Guid id, string? batch, CatalogDbContext db, IRunDispatcher dispatcher, TimeProvider clock, CancellationToken ct)
    {
        var schedule = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (schedule is null)
        {
            return TypedResults.Problem(detail: $"No schedule '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var filter = string.IsNullOrWhiteSpace(batch) ? null : batch.Trim();
        var now = clock.GetUtcNow().UtcDateTime;
        var fire = await ScheduleFire
            .EnqueueAsync(db, dispatcher, schedule, now, ct, filter).ConfigureAwait(false);
        if (!fire.Queued)
        {
            var detail = filter is null
                ? $"Schedule '{schedule.Name}' resolved to no runnable flow: nothing joins it, or every member is "
                  + "deactivated or mode: manual."
                : $"Schedule '{schedule.Name}' has no runnable member carrying batch '{filter}'.";
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
            .Select(Project(db)).FirstAsync(ct).ConfigureAwait(false);
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

    // An expression (not a method body) so EF Core translates the projection into the SELECT column list. It takes the
    // context because the member count is a correlated subquery over the member table: a schedule's whole meaning is
    // what it runs, so a list that could not say how many flows that is would be answering the wrong question.
    private static Expression<Func<CatalogSchedule, ScheduleDto>> Project(CatalogDbContext db) => s => new ScheduleDto(
        s.Id, s.RepoId, s.Name,
        db.ScheduleMembers.Where(m => m.ScheduleId == s.Id).Select(m => m.PipelineId).ToList(),
        s.Cron, s.IntervalSeconds, s.Timezone,
        s.Enabled, s.Catchup, s.Paused, s.Source, s.NextFireUtc, s.LastFireUtc, s.LastRunId, s.LastGroupId,
        s.CreatedUtc, s.UpdatedUtc);
}
