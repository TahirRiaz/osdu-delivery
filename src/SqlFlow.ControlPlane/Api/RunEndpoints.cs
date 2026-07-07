using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A run as it appears in lists: the lifecycle status, header dimensions, and headline row counts, without
/// the drill-down detail. Ordered newest-first by <see cref="WrittenUtc"/>. <see cref="Batch"/> and
/// <see cref="Wave"/> are joined in from the run's pipeline row at query time, the same way the batch report
/// combines the run log with the batch label and the lineage step: a flow whose YAML declares no batch, or a run
/// whose pipeline row left the catalog, reports under <see cref="CatalogPipeline.DefaultBatch"/>; a wave of -1
/// means lineage has not been computed for the repo (or the pipeline row is gone).</summary>
public sealed record RunSummaryDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Batch, int Wave,
    string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime WrittenUtc, DateTime? EnqueuedUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, int FileCount, Guid? GroupId);

/// <summary>One run with its full header for the detail view: the summary plus the lifecycle fields (status, when it
/// was enqueued, the node that claimed it), the schema version, the start/end window, the host, the error, and the
/// run's substitution parameters (the built-in backfill's audit trail: full load, window, file pattern), and the
/// engine-computed incremental scope the run actually applied (mode, filter, resolved watermark and its source).
/// <see cref="Batch"/> and <see cref="Wave"/> follow the same pipeline-join semantics as
/// <see cref="RunSummaryDto"/>.</summary>
public sealed record RunDetailDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Batch, int Wave,
    string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime? EnqueuedUtc, string? ClaimedByNode, DateTime? CancelRequestedUtc,
    int SchemaVersion, DateTime WrittenUtc, DateTime? StartUtc, DateTime? EndUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, int FileCount, string? Error, string? Host,
    bool FullLoad, DateTime? BackfillFrom, DateTime? BackfillTo, string? FilePattern,
    string? IncrementalMode, string? IncrementalFilter, string? IncrementalWatermark, string? IncrementalWatermarkSource,
    int? FailedStatementOrdinal, string? FailedStatementStep, string? FailedStatementSql, Guid? GroupId);

/// <summary>One file a run processed (file flows): a drill-down row under a run.</summary>
public sealed record RunFileDto(
    long Id, Guid RunId, Guid? RepoId, string Name, string? Path, long Rows, int Columns, long SizeBytes);

/// <summary>One data-quality assertion a run evaluated: a drill-down row under a run.</summary>
public sealed record RunAssertionDto(
    long Id, Guid RunId, Guid? RepoId, string Name, string Result, string AssertedValue, bool Evaluated, string? Error);

/// <summary>One generated SQL statement a run executed, in execution order: a drill-down row under a run.
/// <see cref="Error"/> is set only on the one statement that threw (the run's failure point), null otherwise.</summary>
public sealed record RunStatementDto(
    long Id, Guid RunId, Guid? RepoId, int Ordinal, string Step, string Sql, string? Error);

/// <summary>One surrogate-key generation outcome of a run (ingestion flows): a drill-down row under a run.</summary>
public sealed record RunSurrogateKeyDto(
    long Id, Guid RunId, Guid? RepoId, int SurrogateKeyId, string SurrogateTable, string SurrogateColumn,
    bool IsRemote, long KeysGenerated, long RowsStamped, bool Executed, string? Error);

/// <summary>One per-metric health-check summary of a run (hc flows): a drill-down row under a run.</summary>
public sealed record RunHealthCheckMetricDto(
    long Id, Guid RunId, Guid? RepoId, string Name, int SeriesPoints, int ImputedPoints, int ImmaturePoints,
    int Anomalies, int LevelShifts, bool ModelTrained, string? ModelTrainer, string? Error);

/// <summary>One flow a scope expansion would run, with the wave that orders it in the set.</summary>
public sealed record RunScopePreviewMemberDto(string FlowName, string FlowKind, int Wave);

/// <summary>What a Node or Batch run would enqueue, without enqueuing anything: the resolved anchor, the member
/// count, how many waves they span, and the ordered members. The dialog and the lineage graph show this so an
/// operator sees "will run N flows across M waves" before committing.</summary>
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
/// The read API over the shadow catalog's run history: the run headers and their drill-down detail (the files a run
/// processed, the assertions it evaluated, the SQL it executed, its surrogate-key outcomes, and its health-check
/// metrics), all projected from the on-disk <c>run.json</c> artifacts the sync mapped into the catalog. Every query
/// is read-only (<see cref="EntityFrameworkQueryableExtensions.AsNoTracking"/>) and DTO-projected so the raw EF
/// entities never leave the host; list endpoints are bounded by page size.
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
        runs.MapGet("/{runId:guid}", GetRunAsync).WithName("GetRun");
        runs.MapGet("/{runId:guid}/files", GetRunFilesAsync).WithName("GetRunFiles");
        runs.MapGet("/{runId:guid}/assertions", GetRunAssertionsAsync).WithName("GetRunAssertions");
        runs.MapGet("/{runId:guid}/statements", GetRunStatementsAsync).WithName("GetRunStatements");
        runs.MapGet("/{runId:guid}/surrogate-keys", GetRunSurrogateKeysAsync).WithName("GetRunSurrogateKeys");
        runs.MapGet("/{runId:guid}/health-metrics", GetRunHealthMetricsAsync).WithName("GetRunHealthMetrics");

        return group;
    }

    private static async Task<Ok<PagedResult<RunSummaryDto>>> ListRunsAsync(
        CatalogDbContext db, Guid? repoId, Guid? pipelineId, string? flowKind, string? status, bool? success,
        string? flowName, string? batch, Guid? groupId, bool? latest, int? page, int? pageSize, CancellationToken ct)
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

        // A group's member runs, for the group view: every run stamped with this GroupId, in wave order below.
        if (groupId is { } gid)
        {
            query = query.Where(x => x.GroupId == gid);
        }

        if (!string.IsNullOrWhiteSpace(flowKind))
        {
            query = query.Where(x => x.FlowKind == flowKind);
        }

        if (!string.IsNullOrWhiteSpace(flowName))
        {
            query = query.Where(x => x.FlowName.Contains(flowName));
        }

        // latest=true keeps only each pipeline's newest run: the batch status board ("what is red right now"),
        // one row per pipeline, versus the full history the flat inbox and the pipeline detail show. A row
        // survives when it IS the top-1 of ALL of the pipeline's runs ordered by WrittenUtc then RunId, both
        // descending: exactly the row the previous correlated NOT-EXISTS kept (RunId breaks WrittenUtc ties,
        // and being the primary key it makes the maximum unique), but shaped as a per-pipeline TOP(1) seek the
        // (PipelineId, WrittenUtc DESC, RunId DESC) index on Run answers directly. It still applies BEFORE the
        // lifecycle filters below so status=failed means "currently failed", not "ever failed".
        if (latest == true)
        {
            query = query.Where(x => x.RunId == db.Runs
                .Where(candidate => candidate.PipelineId == x.PipelineId)
                .OrderByDescending(candidate => candidate.WrittenUtc)
                .ThenByDescending(candidate => candidate.RunId)
                .Select(candidate => candidate.RunId)
                .FirstOrDefault());
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

        // The batch label and the lineage wave live on the pipeline row (git/YAML is their source of truth), so
        // they are joined in at query time rather than denormalized onto every run: one source, always current,
        // and a run whose pipeline left the estate still lists (left join) under the default batch. A missing
        // label coalesces to CatalogPipeline.DefaultBatch so every run belongs to a batch group.
        var joined =
            from run in query
            join pipeline in db.Pipelines.AsNoTracking() on run.PipelineId equals pipeline.Id into pipelines
            from pipeline in pipelines.DefaultIfEmpty()
            select new
            {
                Run = run,
                Batch = pipeline != null && pipeline.Batch != null ? pipeline.Batch : CatalogPipeline.DefaultBatch,
                Wave = pipeline != null ? pipeline.Wave : -1,
            };

        if (!string.IsNullOrWhiteSpace(batch))
        {
            var batchFilter = batch.Trim();
            joined = joined.Where(x => x.Batch.Contains(batchFilter));
        }

        // The history inbox reads newest-first; the status board (latest=true) reads in report order, batch then
        // lineage step then flow. A group view (groupId set) reads in execution order: by the member's wave then
        // flow name, so the set lists exactly as it runs.
        var ordered = latest == true
            ? joined.OrderBy(x => x.Batch).ThenBy(x => x.Wave).ThenBy(x => x.Run.FlowName).ThenBy(x => x.Run.RunId)
            : groupId is not null
                ? joined.OrderBy(x => x.Run.GroupWave).ThenBy(x => x.Run.FlowName).ThenBy(x => x.Run.RunId)
                : joined.OrderByDescending(x => x.Run.WrittenUtc).ThenBy(x => x.Run.RunId);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new RunSummaryDto(
                x.Run.RunId, x.Run.PipelineId, x.Run.RepoId, x.Run.FlowName, x.Run.FlowKind, x.Batch, x.Wave,
                x.Run.Status, x.Run.Success,
                x.Run.TargetPool, x.Run.CommitSha, x.Run.WrittenUtc, x.Run.EnqueuedUtc, x.Run.DurationSeconds,
                x.Run.RowsLoaded, x.Run.RowsInserted, x.Run.RowsUpdated, x.Run.RowsDeleted,
                db.RunFiles.Count(f => f.RunId == x.Run.RunId), x.Run.GroupId))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunSummaryDto>(items, p, size, total));
    }

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
                    run.RowsLoaded, run.RowsInserted, run.RowsUpdated, run.RowsDeleted,
                    db.RunFiles.Count(f => f.RunId == run.RunId), run.Error, run.Host,
                    run.FullLoad, run.BackfillFrom, run.BackfillTo, run.FilePattern,
                    run.IncrementalMode, run.IncrementalFilter, run.IncrementalWatermark, run.IncrementalWatermarkSource,
                    null, null, null, run.GroupId))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (dto is null)
        {
            return NotFound("run", runId);
        }

        // On a failed run, surface the exact statement that threw alongside the header, so the detail view can show
        // the offending SQL next to the error banner instead of making the operator hunt for it in the Statements
        // list. This is the one statement whose projection carries an Error (there is at most one per run); the
        // lookup is skipped for every non-failed run.
        if (dto.Status == RunStatuses.Failed)
        {
            var failed = await db.RunStatements.AsNoTracking()
                .Where(s => s.RunId == runId && s.Error != null)
                .OrderBy(s => s.Ordinal)
                .Select(s => new { s.Ordinal, s.Step, s.Sql })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (failed is not null)
            {
                dto = dto with
                {
                    FailedStatementOrdinal = failed.Ordinal,
                    FailedStatementStep = failed.Step,
                    FailedStatementSql = failed.Sql,
                };
            }
        }

        return TypedResults.Ok(dto);
    }

    private static async Task<Results<Ok<RunScopePreviewDto>, ProblemHttpResult>> PreviewScopeAsync(
        CatalogDbContext db, Guid repoId, string? flowName, string? scope, string? batch, CancellationToken ct)
    {
        var parsed = RunScopeExpander.TryParseScope(scope);
        if (parsed is null)
        {
            return TypedResults.Problem(
                detail: "scope must be one of 'flow', 'node', or 'batch'.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        RunScopeExpansion expansion;
        try
        {
            expansion = await RunScopeExpander.ExpandAsync(
                    db, repoId,
                    string.IsNullOrWhiteSpace(flowName) ? null : flowName.Trim(), parsed.Value,
                    string.IsNullOrWhiteSpace(batch) ? null : batch.Trim(), ct)
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
            parsed.Value.ToString().ToLowerInvariant(), expansion.Anchor, members.Count, waveCount, members));
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

    // Every drill-down endpoint first verifies the run exists and returns 404 when it does not, so an unknown run id
    // is reported (rather than silently returning an empty page that a caller could not distinguish from a real run
    // that processed nothing). This is consistent across all drill-down endpoints below.

    private static async Task<Results<Ok<PagedResult<RunFileDto>>, ProblemHttpResult>> GetRunFilesAsync(
        Guid runId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var ordered = db.RunFiles.AsNoTracking().Where(x => x.RunId == runId).OrderBy(x => x.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new RunFileDto(x.Id, x.RunId, x.RepoId, x.Name, x.Path, x.Rows, x.Columns, x.SizeBytes))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunFileDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PagedResult<RunAssertionDto>>, ProblemHttpResult>> GetRunAssertionsAsync(
        Guid runId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var ordered = db.RunAssertions.AsNoTracking().Where(x => x.RunId == runId).OrderBy(x => x.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new RunAssertionDto(
                x.Id, x.RunId, x.RepoId, x.Name, x.Result, x.AssertedValue, x.Evaluated, x.Error))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunAssertionDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PagedResult<RunStatementDto>>, ProblemHttpResult>> GetRunStatementsAsync(
        Guid runId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        // A run can emit many statements (every generated CREATE/MERGE/DDL), so this list is paged; ordered by the
        // 1-based execution position the sync recorded.
        var ordered = db.RunStatements.AsNoTracking().Where(x => x.RunId == runId)
            .OrderBy(x => x.Ordinal).ThenBy(x => x.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new RunStatementDto(x.Id, x.RunId, x.RepoId, x.Ordinal, x.Step, x.Sql, x.Error))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunStatementDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PagedResult<RunSurrogateKeyDto>>, ProblemHttpResult>> GetRunSurrogateKeysAsync(
        Guid runId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var ordered = db.RunSurrogateKeys.AsNoTracking().Where(x => x.RunId == runId).OrderBy(x => x.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new RunSurrogateKeyDto(
                x.Id, x.RunId, x.RepoId, x.SurrogateKeyId, x.SurrogateTable, x.SurrogateColumn,
                x.IsRemote, x.KeysGenerated, x.RowsStamped, x.Executed, x.Error))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunSurrogateKeyDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PagedResult<RunHealthCheckMetricDto>>, ProblemHttpResult>> GetRunHealthMetricsAsync(
        Guid runId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var ordered = db.RunHealthCheckMetrics.AsNoTracking().Where(x => x.RunId == runId).OrderBy(x => x.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new RunHealthCheckMetricDto(
                x.Id, x.RunId, x.RepoId, x.Name, x.SeriesPoints, x.ImputedPoints, x.ImmaturePoints,
                x.Anomalies, x.LevelShifts, x.ModelTrained, x.ModelTrainer, x.Error))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunHealthCheckMetricDto>(items, p, size, total));
    }

    private static Task<bool> RunExistsAsync(CatalogDbContext db, Guid runId, CancellationToken ct)
        => db.Runs.AsNoTracking().AnyAsync(x => x.RunId == runId, ct);

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");
}
