using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Runs;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A schedule as the API returns it: its timing, scope, lifecycle flags, source, and the next/last fire.
/// <paramref name="MaxConcurrency"/> is how many members one fire runs at once (null = unbounded).
/// <paramref name="LastCounts"/> is how the last fire actually ended: its members tallied by lifecycle state (the one
/// run's own state for a single-member fire), null when the schedule has never fired or its runs have aged out. It is
/// what makes "did the last execution succeed" answerable from the list without opening the run board.</summary>
public sealed record ScheduleDto(
    Guid Id, Guid RepoId, string Name, IReadOnlyList<Guid> MemberPipelineIds, string? Cron, int? IntervalSeconds, string Timezone,
    bool Enabled, bool Catchup, bool Paused, string Source, DateTime? NextFireUtc, DateTime? LastFireUtc, Guid? LastRunId,
    Guid? LastGroupId, bool LastGroupActive, DateTime CreatedUtc, DateTime UpdatedUtc, int? MaxConcurrency,
    RunGroupCountsDto? LastCounts);

/// <summary>
/// The YAML behind a schedule: where git declares its cadence and the text of that declaration. A schedule declared
/// by an inline <c>schedule:</c> block serves its declaring flow's document (the pipeline row's secret-redacted copy,
/// so this read can never leak a literal credential); one declared in a <c>schedules.yaml</c> serves that library
/// file. <paramref name="Yaml"/> is null for an API-created schedule, which has no file behind it, and for a
/// git schedule whose declaring flow has left the estate.
/// </summary>
public sealed record ScheduleDefinitionDto(
    Guid ScheduleId, string Name, string Source, string? Path, string? FlowName, Guid? PipelineId, string? Yaml);

/// <summary>The body to create an ad-hoc API schedule: the member flows it runs, exactly one of cron /
/// intervalSeconds, and optionally a name (defaulting to the first member's flow name). Membership is what a fire
/// runs, so a schedule with no members is rejected.
/// <para><paramref name="MaxConcurrency"/> bounds how many members one fire executes at once. Omitted takes the
/// product default (<see cref="ScheduleDefaults.MaxConcurrency"/>); <c>0</c> asks for unbounded.</para></summary>
public sealed record CreateScheduleRequest(
    Guid RepoId, IReadOnlyList<string> Members, string? Cron, int? IntervalSeconds, string? Timezone, bool? Enabled,
    bool? Catchup = null, string? Name = null, int? MaxConcurrency = null);

/// <summary>The created-schedule acknowledgement.</summary>
public sealed record ScheduleCreated(Guid Id, DateTime? NextFireUtc);

/// <summary>The manual run-now acknowledgement: the run the fire enqueued (the group's first member for a scoped
/// schedule), plus the run group and member count when the scope expanded to a wave-ordered set.</summary>
public sealed record ScheduleRunAccepted(Guid RunId, Guid? GroupId = null, int MemberCount = 1);

/// <summary>One flow a schedule runs: the wave that orders it within the fire and the batch it carries, so the run
/// board can group or filter the plan by batch and a fire can be narrowed to one batch's flows.</summary>
public sealed record SchedulePlanMemberDto(string FlowName, string FlowKind, int Wave, string Batch);

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
        schedules.MapGet("/{id:guid}/definition", GetScheduleDefinitionAsync).WithName("GetScheduleDefinition");
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
        var rows = await ordered.Skip((p - 1) * size).Take(size)
            .Select(Project(db)).ToListAsync(ct).ConfigureAwait(false);
        var items = await WithLastFireOutcomeAsync(db, rows, ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<ScheduleDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<ScheduleDto>, ProblemHttpResult>> GetScheduleAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var row = await db.Schedules.AsNoTracking().Where(s => s.Id == id)
            .Select(Project(db)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return TypedResults.Problem(detail: $"No schedule '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var dto = await WithLastFireOutcomeAsync(db, [row], ct).ConfigureAwait(false);
        return TypedResults.Ok(dto[0]);
    }

    /// <summary>
    /// The YAML that defines a schedule, so "why does this fire at 04:15" is answerable from the GUI without going
    /// to git. The sync records where git declares the cadence; an inline block resolves to its declaring flow's
    /// stored (secret-redacted) document, a library entry to the <c>schedules.yaml</c> text kept on the row. An
    /// API-created schedule has no file, and answers with a null document rather than a fabricated one.
    /// </summary>
    private static async Task<Results<Ok<ScheduleDefinitionDto>, ProblemHttpResult>> GetScheduleDefinitionAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var schedule = await db.Schedules.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new
            {
                s.Id, s.RepoId, s.Name, s.Source, s.DefinitionPath, s.DefinitionFlow, s.DefinitionYaml,
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (schedule is null)
        {
            return TypedResults.Problem(detail: $"No schedule '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        if (schedule.DefinitionFlow is not { Length: > 0 } flowName)
        {
            return TypedResults.Ok(new ScheduleDefinitionDto(
                schedule.Id, schedule.Name, schedule.Source, schedule.DefinitionPath, null, null, schedule.DefinitionYaml));
        }

        // The declaring flow's document is the definition: it is served from the pipeline row rather than copied onto
        // the schedule, so it stays in step with the flow and carries the same redaction every other YAML read does.
        var pipelineId = CatalogIdentity.Pipeline(schedule.RepoId, flowName);
        var pipeline = await db.Pipelines.AsNoTracking()
            .Where(pl => pl.Id == pipelineId)
            .Select(pl => new { pl.Id, pl.RelativePath, pl.Yaml })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new ScheduleDefinitionDto(
            schedule.Id, schedule.Name, schedule.Source, pipeline?.RelativePath ?? schedule.DefinitionPath,
            flowName, pipeline?.Id, pipeline?.Yaml));
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
            .Select(m => new SchedulePlanMemberDto(m.FlowName, m.FlowKind, m.Wave, m.Batch))
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
            request.Enabled ?? true, request.Catchup ?? false, ScheduleDefaults.Resolve(request.MaxConcurrency, out _),
            next ?? now, now, ct).ConfigureAwait(false);

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
    /// nightly, but only the small tables". It may be repeated (<c>?batch=small&amp;batch=medium</c>) to run several
    /// batches at once, and can only ever select a subset of the schedule's own members.
    /// </para>
    /// Answers 202 with the run (and group) reference, 404 for an unknown schedule, and 409 when nothing is runnable.
    /// </summary>
    private static async Task<Results<Accepted<ScheduleRunAccepted>, ProblemHttpResult>> RunScheduleAsync(
        Guid id, string[]? batch, DateTime? from, DateTime? to,
        CatalogDbContext db, IRunDispatcher dispatcher, TimeProvider clock, CancellationToken ct)
    {
        var schedule = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (schedule is null)
        {
            return TypedResults.Problem(detail: $"No schedule '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        // The optional from/to window turns the fire into a backfill: the schedule re-processes the source for that
        // date range (its integration roots re-land the slice, its silver flows re-pull from the source minimum). It
        // is validated here, at the trust boundary, exactly as a single-flow trigger's window is.
        RunParameters? backfillWindow = null;
        if (from is not null || to is not null)
        {
            backfillWindow = new RunParameters { BackfillFrom = from, BackfillTo = to };
            try
            {
                backfillWindow.Validate();
            }
            catch (SqlFlow.Core.SqlFlowException ex)
            {
                return TypedResults.Problem(
                    detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid run parameters");
            }
        }

        var filter = batch is null
            ? null
            : batch.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim())
                .Distinct(StringComparer.Ordinal).ToList();
        var now = clock.GetUtcNow().UtcDateTime;
        var fire = await ScheduleFire
            .EnqueueAsync(db, dispatcher, schedule, now, ct, filter, backfillWindow).ConfigureAwait(false);
        if (!fire.Queued)
        {
            var detail = filter is not { Count: > 0 }
                ? $"Schedule '{schedule.Name}' resolved to no runnable flow: nothing joins it, or every member is "
                  + "deactivated or mode: manual."
                : $"Schedule '{schedule.Name}' has no runnable member carrying "
                  + (filter.Count == 1 ? $"batch '{filter[0]}'." : $"any of the batches: {string.Join(", ", filter)}.");
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

        var row = await db.Schedules.AsNoTracking().Where(s => s.Id == id)
            .Select(Project(db)).FirstAsync(ct).ConfigureAwait(false);
        var dto = await WithLastFireOutcomeAsync(db, [row], ct).ConfigureAwait(false);
        return TypedResults.Ok(dto[0]);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteScheduleAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var outcome = await ScheduleStore.DeleteAsync(db, id, ct).ConfigureAwait(false);
        return outcome == ScheduleMutation.Applied
            ? TypedResults.NoContent()
            : TypedResults.Problem(detail: $"No schedule '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
    }

    /// <summary>One (run group, status) tally from the last-fire pass.</summary>
    private sealed record GroupStatusTally(Guid? GroupId, string Status, int Count);

    /// <summary>One single-run fire's current status, keyed by the run the schedule points at.</summary>
    private sealed record RunStatusRow(Guid Id, string Status);

    /// <summary>A schedule as the database returns it: everything but the last fire's outcome, which is tallied for
    /// the whole page in one pass rather than as a correlated subquery per row.</summary>
    private sealed record ScheduleRow(
        Guid Id, Guid RepoId, string Name, IReadOnlyList<Guid> MemberPipelineIds, string? Cron, int? IntervalSeconds,
        string Timezone, bool Enabled, bool Catchup, bool Paused, string Source, DateTime? NextFireUtc,
        DateTime? LastFireUtc, Guid? LastRunId, Guid? LastGroupId, DateTime CreatedUtc, DateTime UpdatedUtc,
        int? MaxConcurrency);

    // An expression (not a method body) so EF Core translates the projection into the SELECT column list. It takes the
    // context because the member count is a correlated subquery over the member table: a schedule's whole meaning is
    // what it runs, so a list that could not say how many flows that is would be answering the wrong question.
    private static Expression<Func<CatalogSchedule, ScheduleRow>> Project(CatalogDbContext db) => s => new ScheduleRow(
        s.Id, s.RepoId, s.Name,
        db.ScheduleMembers.Where(m => m.ScheduleId == s.Id).Select(m => m.PipelineId).ToList(),
        s.Cron, s.IntervalSeconds, s.Timezone,
        s.Enabled, s.Catchup, s.Paused, s.Source, s.NextFireUtc, s.LastFireUtc, s.LastRunId, s.LastGroupId,
        s.CreatedUtc, s.UpdatedUtc, s.MaxConcurrency);

    /// <summary>
    /// Fills in how each schedule's last fire ended: the member states of the run group it enqueued, or the single
    /// run's own state when the fire ran one flow. Two set-based queries for the whole page (one grouped tally over
    /// the groups, one status read over the single runs) rather than a handful of correlated subqueries per row, so
    /// the cost does not scale with page size. A schedule that never fired, or whose runs have aged out of the
    /// catalog, gets a null tally: "unknown", never a fabricated success.
    /// </summary>
    private static async Task<IReadOnlyList<ScheduleDto>> WithLastFireOutcomeAsync(
        CatalogDbContext db, IReadOnlyList<ScheduleRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var groupIds = rows.Where(r => r.LastGroupId is not null).Select(r => r.LastGroupId).Distinct().ToList();
        var runIds = rows.Where(r => r.LastGroupId is null && r.LastRunId is not null)
            .Select(r => r.LastRunId!.Value).Distinct().ToList();

        var groupTallies = new List<GroupStatusTally>();
        if (groupIds.Count > 0)
        {
            groupTallies = await db.Runs.AsNoTracking()
                .Where(r => groupIds.Contains(r.GroupId))
                .GroupBy(r => new { r.GroupId, r.Status })
                .Select(g => new GroupStatusTally(g.Key.GroupId, g.Key.Status, g.Count()))
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var runStatuses = new List<RunStatusRow>();
        if (runIds.Count > 0)
        {
            runStatuses = await db.Runs.AsNoTracking()
                .Where(r => runIds.Contains(r.RunId))
                .Select(r => new RunStatusRow(r.RunId, r.Status))
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var byGroup = groupTallies
            .Where(t => t.GroupId is not null)
            .GroupBy(t => t.GroupId!.Value)
            .ToDictionary(g => g.Key, g => Tally(g.Select(t => (t.Status, t.Count))));
        var byRun = runStatuses.ToDictionary(r => r.Id, r => Tally([(r.Status, 1)]));

        return rows.Select(r =>
        {
            RunGroupCountsDto? counts = null;
            if (r.LastGroupId is { } groupId)
            {
                byGroup.TryGetValue(groupId, out counts);
            }
            else if (r.LastRunId is { } runId)
            {
                byRun.TryGetValue(runId, out counts);
            }

            return new ScheduleDto(
                r.Id, r.RepoId, r.Name, r.MemberPipelineIds, r.Cron, r.IntervalSeconds, r.Timezone,
                r.Enabled, r.Catchup, r.Paused, r.Source, r.NextFireUtc, r.LastFireUtc, r.LastRunId, r.LastGroupId,
                // Whether the last scoped fire's group is still executing, so the list can surface a live re-entry
                // point to it. A single-member fire has no group, so this is always false there.
                r.LastGroupId is not null && counts is { } c && c.Queued + c.Running > 0,
                r.CreatedUtc, r.UpdatedUtc, r.MaxConcurrency, counts);
        }).ToList();
    }

    /// <summary>Rolls a fire's (status, count) pairs into the lifecycle tally the run board uses, so a schedule's last
    /// fire and a run group's header are read the same way.</summary>
    private static RunGroupCountsDto Tally(IEnumerable<(string Status, int Count)> statusCounts)
    {
        var byStatus = statusCounts.ToList();
        int CountOf(string status) => byStatus.Where(x => x.Status == status).Sum(x => x.Count);
        return new RunGroupCountsDto(
            byStatus.Sum(x => x.Count),
            CountOf(RunStatuses.Queued), CountOf(RunStatuses.Running), CountOf(RunStatuses.Succeeded),
            CountOf(RunStatuses.Failed), CountOf(RunStatuses.Cancelled), CountOf(RunStatuses.Skipped));
    }
}
