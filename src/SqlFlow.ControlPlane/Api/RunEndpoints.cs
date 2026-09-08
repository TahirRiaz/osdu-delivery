using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A run as it appears in lists: the lifecycle status, header dimensions, and headline counts, without
/// the trace. Ordered newest-first by <see cref="WrittenUtc"/>. <see cref="Batch"/> and <see cref="Wave"/> are
/// joined in from the run's pipeline row at query time: a flow whose YAML declares no batch, or a run whose
/// pipeline row left the catalog, reports under <see cref="CatalogPipeline.DefaultBatch"/>.
/// <see cref="LastAction"/>/<see cref="LastActionUtc"/> are the run's newest trace event: while the run executes
/// they answer "what is it doing right now", at rest "what did it do last". Null for a run that recorded no
/// events (still queued). They are resolved ONLY for a run group's member list (the runs list filtered by
/// <see cref="GroupId"/>, and the group stream): that is the one place any client renders them, and each costs a
/// per-row lookup into the event log, so the general run board (hundreds of rows, polled) does not pay for a
/// column it never shows and reports both as null there.
/// <see cref="Error"/> is why a failed run failed, carried on the summary so a set (a schedule's fire) can show
/// its failures where they happened instead of making an operator open each member to find out.</summary>
public sealed record RunSummaryDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Batch, int Wave,
    string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime WrittenUtc, DateTime? EnqueuedUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, Guid? GroupId,
    string? LastAction, DateTime? LastActionUtc, string? Error, string Operation, bool Force);

/// <summary>One run with its full header for the detail view: the summary plus the lifecycle fields (status, when it
/// was enqueued, the node that claimed it), the schema version, the start/end window, the host, the error, and the
/// run's parameters (the audit trail of what the trigger asked for). <see cref="Batch"/> and <see cref="Wave"/>
/// follow the same pipeline-join semantics as <see cref="RunSummaryDto"/>.</summary>
public sealed record RunDetailDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Batch, int Wave,
    string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime? EnqueuedUtc, string? ClaimedByNode, DateTime? CancelRequestedUtc,
    int SchemaVersion, DateTime WrittenUtc, DateTime? StartUtc, DateTime? EndUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, string? Error, string? Host, string? RequestedBy,
    string Operation, bool Force, Guid? SubmissionId, string? ParametersJson, Guid? ResultSubmissionId,
    int? RecordsPlanned, int? RecordsDelivered, int? RecordsHeld, int? RecordsFailed, int? RecordsSkipped, Guid? GroupId,
    Guid? FanOutRoot, int? FanOutSlot, int? FanOutCount, string? ResultJson);

/// <summary>
/// One entry of a run's trace: a canonical run event (progress, a decision, a stage summary, a warning, an error),
/// in timeline order. <see cref="Ordinal"/> is the 1-based position within the run's event stream.
/// </summary>
public sealed record RunTraceEntryDto(
    long Id, Guid RunId, Guid? RepoId, int Ordinal, DateTime TimestampUtc, string Level,
    string? Step, string Message, long? Rows, double? ElapsedMs);

/// <summary>The payload of the live trace stream's final <c>end</c> event: the run's terminal status. On
/// receiving it the client refetches the paged trace, which by then holds the authoritative re-projection
/// from the run artifact.</summary>
public sealed record RunTraceStreamEndDto(string Status);

/// <summary>One flow a scope expansion would run, with the wave that orders it in the set.</summary>
public sealed record RunScopePreviewMemberDto(string FlowName, string FlowKind, int Wave);

/// <summary>What a run would enqueue, without enqueuing anything: the resolved anchor, the member count, how many
/// waves they span, and the ordered members.</summary>
public sealed record RunScopePreviewDto(
    string Scope, string Anchor, int MemberCount, int WaveCount, IReadOnlyList<RunScopePreviewMemberDto> Members);

/// <summary>A run group's member counts by lifecycle state, for the group view's rollup.</summary>
public sealed record RunGroupCountsDto(
    int Total, int Queued, int Running, int Succeeded, int Failed, int Cancelled, int Skipped);

/// <summary>One run group's header: what it was (mode, anchor), when it was enqueued, the commit every member is
/// pinned to, and the live rollup of its members' states. The members themselves come from the runs list filtered
/// by this group id.</summary>
public sealed record RunGroupDto(
    Guid GroupId, Guid RepoId, string Mode, string Anchor, int MemberCount, string? CommitSha, DateTime EnqueuedUtc,
    RunGroupCountsDto Counts);

/// <summary>
/// The read API over the shadow catalog's run history: the run headers and their traces, projected from the
/// on-disk <c>run.json</c> artifacts the sync mapped into the catalog (and streamed live while a node executes).
/// Every query is read-only (<see cref="EntityFrameworkQueryableExtensions.AsNoTracking"/>) and DTO-projected so
/// the raw EF entities never leave the host; list endpoints are bounded by page size.
/// </summary>
public static class RunEndpoints
{
    public static RouteGroupBuilder MapRunEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var runs = group.MapGroup("/runs").WithTags("Runs");
        runs.MapGet("/", ListRunsAsync).WithName("ListRuns");
        runs.MapGet("/preview", PreviewScopeAsync).WithName("PreviewRunScope");
        runs.MapGet("/groups/{groupId:guid}", GetRunGroupAsync).WithName("GetRunGroup");
        runs.MapGet("/groups/{groupId:guid}/stream", StreamRunGroupAsync).WithName("StreamRunGroup");
        runs.MapGet("/{runId:guid}", GetRunAsync).WithName("GetRun");
        runs.MapGet("/{runId:guid}/trace", GetRunTraceAsync).WithName("GetRunTrace");
        runs.MapGet("/{runId:guid}/trace/text", GetRunTraceTextAsync).WithName("GetRunTraceText");
        runs.MapGet("/{runId:guid}/trace/stream", StreamRunTraceAsync).WithName("StreamRunTrace");

        // A pipeline's LATEST run's trace, without resolving a run id first: the entry point for "show me what its
        // last run did" when debugging a pipeline, by hand or from a tool.
        var pipelines = group.MapGroup("/pipelines").WithTags("Pipelines");
        pipelines.MapGet("/{pipelineId:guid}/trace", GetPipelineTraceAsync).WithName("GetPipelineLatestRunTrace");
        pipelines.MapGet("/{pipelineId:guid}/trace/text", GetPipelineTraceTextAsync).WithName("GetPipelineLatestRunTraceText");

        return group;
    }

    private static async Task<Ok<PagedResult<RunSummaryDto>>> ListRunsAsync(
        CatalogDbContext db, Guid? repoId, Guid? pipelineId, string? flowKind, string? status, bool? success,
        string? flowName, string? batch, Guid? scheduleId, Guid? groupId, bool? latest, DateTime? from, DateTime? to,
        int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);

        var query = db.Runs.AsNoTracking().AsQueryable();
        if (repoId is { } r)
        {
            query = query.Where(x => x.RepoId == r);
        }

        if (pipelineId is { } pid)
        {
            query = query.Where(x => x.PipelineId == pid);
        }

        // A time window over the run history. The window is applied on WrittenUtc: the moment the run's outcome
        // landed in the catalog and the same key the list already orders newest-first by. Both bounds are optional
        // and inclusive. WrittenUtc is stored as a UTC wall-clock value, but query-string binding turns an ISO
        // instant like "2026-03-10T12:00:00Z" into a server-local DateTime, so each bound is normalized back to UTC.
        if (from is { } fromUtc)
        {
            var lower = ToUtc(fromUtc);
            query = query.Where(x => x.WrittenUtc >= lower);
        }

        if (to is { } toUtc)
        {
            var upper = ToUtc(toUtc);
            query = query.Where(x => x.WrittenUtc <= upper);
        }

        // A group's member runs, for the group view: every run stamped with this GroupId, in wave order below.
        if (groupId is { } gid)
        {
            query = query.Where(x => x.GroupId == gid);
        }

        // The runs of one schedule's flows: the run board's schedule filter, resolved through MEMBERSHIP (the same
        // selector a fire uses) rather than a stamp on the run, so it covers a flow's manual and scheduled runs
        // alike. A run survives when its pipeline joined this schedule.
        if (scheduleId is { } sid)
        {
            query = query.Where(x => db.ScheduleMembers.Any(m => m.ScheduleId == sid && m.PipelineId == x.PipelineId));
        }

        if (!string.IsNullOrWhiteSpace(flowKind))
        {
            query = query.Where(x => x.FlowKind == flowKind);
        }

        if (!string.IsNullOrWhiteSpace(flowName))
        {
            query = query.Where(x => x.FlowName.Contains(flowName));
        }

        // latest=true keeps only each pipeline's newest run: the status board ("what is red right now"), one row
        // per pipeline, versus the full history the flat inbox and the pipeline detail show. The set is built PER
        // PIPELINE: the distinct pipeline list drives one TOP(1) seek each on the (PipelineId, WrittenUtc DESC,
        // RunId DESC) index, and the outer query keeps the runs whose id is in that set. It applies BEFORE the
        // lifecycle filters below so status=failed means "currently failed", not "ever failed".
        if (latest == true)
        {
            var latestRunIds = db.Runs.AsNoTracking()
                .Select(run => run.PipelineId)
                .Distinct()
                .Select(pipelineId => db.Runs
                    .Where(candidate => candidate.PipelineId == pipelineId)
                    .OrderByDescending(candidate => candidate.WrittenUtc)
                    .ThenByDescending(candidate => candidate.RunId)
                    .Select(candidate => candidate.RunId)
                    .FirstOrDefault());
            query = query.Where(x => latestRunIds.Contains(x.RunId));
        }

        // Lifecycle filter (queued / running / succeeded / failed / cancelled): the GUI's "active runs" and
        // "failures" views. Normalized to lower case so the query is case-insensitive on the stored value.
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToLowerInvariant();
            query = query.Where(x => x.Status == normalized);
        }

        if (success is { } s)
        {
            query = query.Where(x => x.Success == s);
        }

        var joined = JoinPipelines(db, query);
        if (!string.IsNullOrWhiteSpace(batch))
        {
            // Exact match: the board's batch filter is a dropdown of the estate's real batch labels, so a chosen
            // "trans" must not also drag in "trans_item". The label is compared after the pipeline join, since a
            // run whose pipeline left the catalog coalesces to the default batch there.
            var batchFilter = batch.Trim();
            joined = joined.Where(x => x.Batch == batchFilter);
        }

        // The history inbox reads newest-first; the status board (latest=true) reads in report order, batch then
        // wave then flow. A group view (groupId set) reads in execution order: by the member's wave then flow name.
        var ordered = latest == true
            ? joined.OrderBy(x => x.Batch).ThenBy(x => x.Wave).ThenBy(x => x.Run.FlowName).ThenBy(x => x.Run.RunId)
            : groupId is not null
                ? joined.OrderBy(x => x.Run.GroupWave).ThenBy(x => x.Run.FlowName).ThenBy(x => x.Run.RunId)
                : joined.OrderByDescending(x => x.Run.WrittenUtc).ThenBy(x => x.Run.RunId);
        // The last action is only ever rendered for a group's members, so it is resolved only when a group is
        // what was asked for: on the open run board that saves two per-row lookups into the event log per poll.
        var items = await ProjectSummaries(db, ordered.Skip((p - 1) * size).Take(size), groupId is not null)
            .ToListAsync(ct).ConfigureAwait(false);

        // The page is read first because it often proves the total: a first page that came back short IS the whole
        // result, so the count query (which re-runs the filters, the latest-run resolution and the pipeline join)
        // is skipped outright for every view narrow enough to fit one page.
        var total = p == 1 && items.Count < size
            ? items.Count
            : await ordered.LongCountAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunSummaryDto>(items, p, size, total));
    }

    /// <summary>The run row paired with its pipeline's batch label and wave: the intermediate shape the summary
    /// projection reads. A named class (not an anonymous type) so the join, the filters, and the final projection
    /// compose across the runs list and the group stream without duplicating the query.</summary>
    private sealed class SummarySource
    {
        public required CatalogRun Run { get; init; }

        public required string Batch { get; init; }

        public int Wave { get; init; }
    }

    /// <summary>The batch label and the wave live on the pipeline row (git/YAML is their source of truth), so
    /// they are joined in at query time rather than denormalized onto every run: one source, always current, and
    /// a run whose pipeline left the estate still lists (left join) under the default batch.</summary>
    private static IQueryable<SummarySource> JoinPipelines(CatalogDbContext db, IQueryable<CatalogRun> runs)
        => from run in runs
           join pipeline in db.Pipelines.AsNoTracking() on run.PipelineId equals pipeline.Id into pipelines
           from pipeline in pipelines.DefaultIfEmpty()
           select new SummarySource
           {
               Run = run,
               Batch = pipeline != null && pipeline.Batch != null ? pipeline.Batch : CatalogPipeline.DefaultBatch,
               Wave = pipeline != null ? pipeline.Wave : -1,
           };

    /// <summary>Projects joined run rows to <see cref="RunSummaryDto"/>: the single summary shape the runs list
    /// and the group stream both serve. <paramref name="includeLastAction"/> decides whether the newest trace
    /// event is resolved: it costs two indexed seeks per row, and only a group's member list shows it, so every
    /// other caller passes false and reports it as null.</summary>
    private static IQueryable<RunSummaryDto> ProjectSummaries(
        CatalogDbContext db, IQueryable<SummarySource> source, bool includeLastAction)
        => includeLastAction
            ? source.Select(x => new RunSummaryDto(
                x.Run.RunId, x.Run.PipelineId, x.Run.RepoId, x.Run.FlowName, x.Run.FlowKind, x.Batch, x.Wave,
                x.Run.Status, x.Run.Success,
                x.Run.TargetPool, x.Run.CommitSha, x.Run.WrittenUtc, x.Run.EnqueuedUtc, x.Run.DurationSeconds,
                x.Run.RowsLoaded, x.Run.RowsInserted, x.Run.RowsUpdated, x.Run.RowsDeleted, x.Run.GroupId,
                db.RunEvents.Where(e => e.RunId == x.Run.RunId)
                    .OrderByDescending(e => e.Id).Select(e => (string?)e.Message).FirstOrDefault(),
                db.RunEvents.Where(e => e.RunId == x.Run.RunId)
                    .OrderByDescending(e => e.Id).Select(e => (DateTime?)e.TimestampUtc).FirstOrDefault(),
                x.Run.Error, x.Run.Operation, x.Run.Force))
            : source.Select(x => new RunSummaryDto(
                x.Run.RunId, x.Run.PipelineId, x.Run.RepoId, x.Run.FlowName, x.Run.FlowKind, x.Batch, x.Wave,
                x.Run.Status, x.Run.Success,
                x.Run.TargetPool, x.Run.CommitSha, x.Run.WrittenUtc, x.Run.EnqueuedUtc, x.Run.DurationSeconds,
                x.Run.RowsLoaded, x.Run.RowsInserted, x.Run.RowsUpdated, x.Run.RowsDeleted, x.Run.GroupId,
                null, null,
                x.Run.Error, x.Run.Operation, x.Run.Force));

    /// <summary>A group's member summaries in execution order: the shape both the group view's member list and
    /// the group stream serve. This is the one list that shows the last action, so it resolves it.</summary>
    private static IQueryable<RunSummaryDto> GroupMembersQuery(CatalogDbContext db, Guid groupId)
        => ProjectSummaries(
            db,
            JoinPipelines(db, db.Runs.AsNoTracking().Where(r => r.GroupId == groupId))
                .OrderBy(x => x.Run.GroupWave).ThenBy(x => x.Run.FlowName).ThenBy(x => x.Run.RunId),
            includeLastAction: true);

    private static async Task<Results<Ok<RunDetailDto>, ProblemHttpResult>> GetRunAsync(
        Guid runId, CatalogDbContext db, CancellationToken ct)
    {
        var dto = await (
                from run in db.Runs.AsNoTracking()
                where run.RunId == runId
                join pipeline in db.Pipelines.AsNoTracking() on run.PipelineId equals pipeline.Id into pipelines
                from pipeline in pipelines.DefaultIfEmpty()
                select new RunDetailDto(
                    run.RunId, run.PipelineId, run.RepoId, run.FlowName, run.FlowKind,
                    pipeline != null && pipeline.Batch != null ? pipeline.Batch : CatalogPipeline.DefaultBatch,
                    pipeline != null ? pipeline.Wave : -1,
                    run.Status, run.Success,
                    run.TargetPool, run.CommitSha, run.EnqueuedUtc, run.ClaimedByNode, run.CancelRequestedUtc,
                    run.SchemaVersion, run.WrittenUtc, run.StartUtc, run.EndUtc, run.DurationSeconds,
                    run.RowsLoaded, run.RowsInserted, run.RowsUpdated, run.RowsDeleted, run.Error, run.Host, run.RequestedBy,
                    run.Operation, run.Force, run.SubmissionId, run.ParametersJson, run.ResultSubmissionId,
                    run.RecordsPlanned, run.RecordsDelivered, run.RecordsHeld, run.RecordsFailed, run.RecordsSkipped, run.GroupId,
                    run.FanOutRoot, run.FanOutSlot, run.FanOutCount, run.ResultJson))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return dto is null ? NotFound("run", runId) : TypedResults.Ok(dto);
    }

    private static async Task<Results<Ok<RunScopePreviewDto>, ProblemHttpResult>> PreviewScopeAsync(
        CatalogDbContext db, Guid repoId, string? flowName, string? scope, CancellationToken ct)
    {
        var parsed = RunScopeExpander.TryParseScope(scope);
        if (parsed is null)
        {
            return TypedResults.Problem(
                detail: "scope must be 'flow' (or omitted). To preview what a schedule runs, read its plan "
                        + "(GET /schedules/{id}/plan): a schedule's members are what a fire runs.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        RunScopeExpansion expansion;
        try
        {
            expansion = await RunScopeExpander.ExpandAsync(
                    db, repoId, string.IsNullOrWhiteSpace(flowName) ? null : flowName.Trim(), parsed.Value, ct)
                .ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(
                detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var members = expansion.Members
            .Select(m => new RunScopePreviewMemberDto(m.FlowName, m.FlowKind, m.Wave))
            .ToList();
        var waveCount = members.Select(m => m.Wave).Distinct().Count();
        return TypedResults.Ok(new RunScopePreviewDto(
            RunScopes.From(parsed.Value), expansion.Anchor, members.Count, waveCount, members));
    }

    private static async Task<Results<Ok<RunGroupDto>, ProblemHttpResult>> GetRunGroupAsync(
        Guid groupId, CatalogDbContext db, CancellationToken ct)
    {
        var group = await db.RunGroups.AsNoTracking()
            .Where(g => g.GroupId == groupId)
            .Select(g => new { g.GroupId, g.RepoId, g.Mode, g.Anchor, g.MemberCount, g.CommitSha, g.EnqueuedUtc })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (group is null)
        {
            return NotFound("run group", groupId);
        }

        // The live rollup: the members' current lifecycle states, counted in one grouped pass.
        var byStatus = await db.Runs.AsNoTracking()
            .Where(r => r.GroupId == groupId)
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);
        int CountOf(string status) => byStatus.FirstOrDefault(x => x.Status == status)?.Count ?? 0;
        var counts = new RunGroupCountsDto(
            byStatus.Sum(x => x.Count),
            CountOf(RunStatuses.Queued), CountOf(RunStatuses.Running), CountOf(RunStatuses.Succeeded),
            CountOf(RunStatuses.Failed), CountOf(RunStatuses.Cancelled), CountOf(RunStatuses.Skipped));

        return TypedResults.Ok(new RunGroupDto(
            group.GroupId, group.RepoId, group.Mode, group.Anchor, group.MemberCount, group.CommitSha,
            group.EnqueuedUtc, counts));
    }

    /// <summary>The run's trace in timeline order: the event rows the node streamed live and the completion
    /// re-projected from the artifact, one composable query for the paged, text and pipeline-latest views.</summary>
    private static IQueryable<RunTraceEntryDto> TraceQuery(CatalogDbContext db, Guid runId)
        => db.RunEvents.AsNoTracking()
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.TimestampUtc).ThenBy(x => x.Ordinal).ThenBy(x => x.Id)
            .Select(x => new RunTraceEntryDto(
                x.Id, x.RunId, x.RepoId, x.Ordinal, x.TimestampUtc, x.Level, x.Step, x.Message, x.Rows, x.ElapsedMs));

    private static async Task<Ok<PagedResult<RunTraceEntryDto>>> PageTraceAsync(
        CatalogDbContext db, Guid runId, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var trace = TraceQuery(db, runId);
        var total = await trace.LongCountAsync(ct).ConfigureAwait(false);
        var items = await trace.Skip((p - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunTraceEntryDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PagedResult<RunTraceEntryDto>>, ProblemHttpResult>> GetRunTraceAsync(
        Guid runId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        return await PageTraceAsync(db, runId, page, pageSize, ct).ConfigureAwait(false);
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> GetRunTraceTextAsync(
        Guid runId, CatalogDbContext db, CancellationToken ct)
    {
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return NotFound("run", runId);
        }

        return await TraceTextAsync(db, run, ct).ConfigureAwait(false);
    }

    /// <summary>The latest run's paged trace for a pipeline, so a caller can go from "this pipeline" straight to
    /// "what its last run did" without resolving a run id first.</summary>
    private static async Task<Results<Ok<PagedResult<RunTraceEntryDto>>, ProblemHttpResult>> GetPipelineTraceAsync(
        Guid pipelineId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        var run = await LatestRunAsync(db, pipelineId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return NoRunForPipeline(pipelineId);
        }

        return await PageTraceAsync(db, run.RunId, page, pageSize, ct).ConfigureAwait(false);
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> GetPipelineTraceTextAsync(
        Guid pipelineId, CatalogDbContext db, CancellationToken ct)
    {
        var run = await LatestRunAsync(db, pipelineId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return NoRunForPipeline(pipelineId);
        }

        return await TraceTextAsync(db, run, ct).ConfigureAwait(false);
    }

    /// <summary>The pipeline's newest run header, by the same ordering the runs list uses (newest WrittenUtc
    /// first); null when the pipeline has no recorded runs (or does not exist, which reads identically).</summary>
    private static Task<CatalogRun?> LatestRunAsync(CatalogDbContext db, Guid pipelineId, CancellationToken ct)
        => db.Runs.AsNoTracking()
            .Where(r => r.PipelineId == pipelineId)
            .OrderByDescending(r => r.WrittenUtc).ThenByDescending(r => r.RunId)
            .FirstOrDefaultAsync(ct);

    private static ProblemHttpResult NoRunForPipeline(Guid pipelineId)
        => TypedResults.Problem(
            detail: $"No run is recorded for pipeline '{pipelineId}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");

    private static async Task<ContentHttpResult> TraceTextAsync(CatalogDbContext db, CatalogRun run, CancellationToken ct)
    {
        var entries = await TraceQuery(db, run.RunId).ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Text(RenderTraceText(run, entries), "text/plain; charset=utf-8");
    }

    /// <summary>
    /// Renders a run's trace as one plain-text document: a header identifying the run (flow, kind, status,
    /// window, rows, error), then one line per entry in timeline order, with multi-line messages indented under
    /// their entry line. This single rendering serves every text consumer: the GUI's "Copy trace" button, a
    /// ticket paste, and a tool debugging a pipeline from its last run.
    /// </summary>
    private static string RenderTraceText(CatalogRun run, IReadOnlyList<RunTraceEntryDto> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("run ").Append(run.RunId)
          .Append(" flow '").Append(run.FlowName).Append("' (").Append(run.FlowKind).Append(')')
          .Append(" status ").Append(run.Status);
        if (run.StartUtc is { } start)
        {
            sb.Append(" started ").Append(start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('Z');
        }

        if (run.DurationSeconds is { } duration)
        {
            sb.Append(" duration ").Append(duration.ToString("0.###", CultureInfo.InvariantCulture)).Append('s');
        }

        if (run.RowsLoaded is { } rows)
        {
            sb.Append(" records ").Append(rows.ToString(CultureInfo.InvariantCulture));
        }

        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(run.Error))
        {
            sb.Append("error: ").AppendLine(run.Error.ReplaceLineEndings(" ").Trim());
        }

        foreach (var entry in entries)
        {
            var stamp = entry.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";
            sb.Append(stamp).Append(' ')
              .Append(entry.Level.ToUpperInvariant().PadRight(7)).Append(' ')
              .Append((entry.Step ?? "-").PadRight(24)).Append(' ');

            var lines = entry.Message.ReplaceLineEndings("\n").Split('\n');
            sb.AppendLine(lines[0]);
            foreach (var line in lines.Skip(1))
            {
                sb.Append("    | ").AppendLine(line);
            }
        }

        return sb.ToString();
    }

    // The server-side tail cadence of the live stream: short enough that an entry reaches the browser well under
    // a second after the node persisted it, long enough that one open run view costs a handful of cheap indexed
    // reads per second. The heartbeat keeps idle proxies from reaping the connection during a long quiet stage.
    private static readonly TimeSpan StreamTailInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StreamHeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The live run trace as Server-Sent Events: one <c>entry</c> event per new trace entry (the same shape as
    /// <see cref="RunTraceEntryDto"/>) as the executing node streams it into the catalog, then a single
    /// <c>end</c> event carrying the terminal status once the run completes, after which the paged endpoint
    /// holds the authoritative re-projected trace and the client should refetch it. The
    /// <paramref name="afterEventId"/> cursor resumes a dropped connection without replaying entries the client
    /// already holds. The stream tails the same catalog rows the paged endpoint reads, so it works identically
    /// for the in-process worker and a self-hosted node.
    /// </summary>
    private static async Task<IResult> StreamRunTraceAsync(
        Guid runId, CatalogDbContext db, HttpContext http,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        long? afterEventId, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Tell buffering reverse proxies (nginx) to pass frames through as they are written.
        response.Headers["X-Accel-Buffering"] = "no";

        var serializer = json.Value.SerializerOptions;
        var lastEventId = afterEventId ?? 0L;
        var lastHeartbeat = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // The live rows are an immutable, append-only log: the node writes each event once under a
                // monotonically increasing id and never rewrites them (completion only appends any missing tail). So
                // this tail forwards each delta exactly once, keyed on the client's id cursor. Read status first: a
                // terminal run ends the stream here, and the client then loads the authoritative paged trace.
                var status = await db.Runs.AsNoTracking()
                    .Where(r => r.RunId == runId).Select(r => r.Status)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                var terminal = status is null || (status != RunStatuses.Queued && status != RunStatuses.Running);

                if (!terminal)
                {
                    var newEvents = await db.RunEvents.AsNoTracking()
                        .Where(x => x.RunId == runId && x.Id > lastEventId).OrderBy(x => x.Id)
                        .Select(x => new RunTraceEntryDto(
                            x.Id, x.RunId, x.RepoId, x.Ordinal, x.TimestampUtc, x.Level, x.Step, x.Message, x.Rows, x.ElapsedMs))
                        .ToListAsync(ct).ConfigureAwait(false);
                    foreach (var entry in newEvents)
                    {
                        await WriteSseAsync(response, "entry", JsonSerializer.Serialize(entry, serializer), ct)
                            .ConfigureAwait(false);
                        lastEventId = entry.Id;
                        lastHeartbeat = DateTime.UtcNow;
                    }
                }
                else
                {
                    // Terminal (or the run row vanished, which only a manual catalog cleanup can cause): tell the
                    // client to swap to the authoritative paged trace and stop.
                    await WriteSseAsync(
                        response, "end",
                        JsonSerializer.Serialize(new RunTraceStreamEndDto(status ?? "unknown"), serializer), ct)
                        .ConfigureAwait(false);
                    break;
                }

                if (DateTime.UtcNow - lastHeartbeat >= StreamHeartbeatInterval)
                {
                    await response.WriteAsync(": hb\n\n", ct).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                    lastHeartbeat = DateTime.UtcNow;
                }

                await Task.Delay(StreamTailInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away (tab closed, navigation): the normal way a live stream ends mid-run.
        }

        return TypedResults.Empty;
    }

    private static async Task WriteSseAsync(HttpResponse response, string eventName, string data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The live run group as Server-Sent Events: one <c>member</c> event (a <see cref="RunSummaryDto"/>) for
    /// every member whose summary changed since the previous tick (status transitions, the newest trace event
    /// as the "last action", durations and counts), a full snapshot on connect, then a single <c>end</c> event
    /// carrying the final <see cref="RunGroupCountsDto"/> rollup once every member is terminal. Reconnecting
    /// simply replays the current snapshot: the diffing state is per connection.
    /// </summary>
    private static async Task<IResult> StreamRunGroupAsync(
        Guid groupId, CatalogDbContext db, HttpContext http,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, CancellationToken ct)
    {
        if (!await db.RunGroups.AsNoTracking().AnyAsync(g => g.GroupId == groupId, ct).ConfigureAwait(false))
        {
            return NotFound("run group", groupId);
        }

        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        var serializer = json.Value.SerializerOptions;
        // Per-member fingerprint of the last frame sent: the serialized summary itself, so change detection and
        // emission share one serialization and any changed field (status, last action, counts, timing) republishes.
        var sent = new Dictionary<Guid, string>();
        var lastHeartbeat = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var members = await GroupMembersQuery(db, groupId).ToListAsync(ct).ConfigureAwait(false);
                foreach (var member in members)
                {
                    var payload = JsonSerializer.Serialize(member, serializer);
                    if (!sent.TryGetValue(member.RunId, out var previous) || previous != payload)
                    {
                        sent[member.RunId] = payload;
                        await WriteSseAsync(response, "member", payload, ct).ConfigureAwait(false);
                        lastHeartbeat = DateTime.UtcNow;
                    }
                }

                // Terminal once every member has left the queue and the worker: the completion projections have
                // landed by then (each member's status flips in the same transaction as its re-projection), so
                // the last member frames sent above already carry the final summaries. An empty member list can
                // only mean a manual catalog cleanup mid-stream; it ends the stream the same way.
                if (members.Count == 0 || members.All(m => RunStatuses.IsTerminal(m.Status)))
                {
                    int CountOf(string status) => members.Count(m => m.Status == status);
                    var counts = new RunGroupCountsDto(
                        members.Count,
                        CountOf(RunStatuses.Queued), CountOf(RunStatuses.Running), CountOf(RunStatuses.Succeeded),
                        CountOf(RunStatuses.Failed), CountOf(RunStatuses.Cancelled), CountOf(RunStatuses.Skipped));
                    await WriteSseAsync(response, "end", JsonSerializer.Serialize(counts, serializer), ct)
                        .ConfigureAwait(false);
                    break;
                }

                if (DateTime.UtcNow - lastHeartbeat >= StreamHeartbeatInterval)
                {
                    await response.WriteAsync(": hb\n\n", ct).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                    lastHeartbeat = DateTime.UtcNow;
                }

                await Task.Delay(StreamTailInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away (tab closed, navigation): the normal way a live stream ends mid-group.
        }

        return TypedResults.Empty;
    }

    /// <summary>Normalizes a run-window bound to the UTC wall-clock value the WrittenUtc column stores: a local
    /// value (how query-string binding materializes an ISO instant with a 'Z' suffix) is converted, an already-UTC
    /// value is kept, and an unspecified-kind value is taken to be UTC (every timestamp in the API is UTC by
    /// contract).</summary>
    private static DateTime ToUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private static Task<bool> RunExistsAsync(CatalogDbContext db, Guid runId, CancellationToken ct)
        => db.Runs.AsNoTracking().AnyAsync(x => x.RunId == runId, ct);

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");
}
