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
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted);

/// <summary>One run with its full header for the detail view: the summary plus the lifecycle fields (status, when it
/// was enqueued, the node that claimed it), the schema version, the start/end window, the host, the error, and the
/// run's substitution parameters (the built-in backfill's audit trail: full load, window, file pattern).
/// <see cref="Batch"/> and <see cref="Wave"/> follow the same pipeline-join semantics as
/// <see cref="RunSummaryDto"/>.</summary>
public sealed record RunDetailDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Batch, int Wave,
    string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime? EnqueuedUtc, string? ClaimedByNode, DateTime? CancelRequestedUtc,
    int SchemaVersion, DateTime WrittenUtc, DateTime? StartUtc, DateTime? EndUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, string? Error, string? Host,
    bool FullLoad, DateTime? BackfillFrom, DateTime? BackfillTo, string? FilePattern);

/// <summary>One file a run processed (file flows): a drill-down row under a run.</summary>
public sealed record RunFileDto(
    long Id, Guid RunId, Guid? RepoId, string Name, string? Path, long Rows, int Columns, long SizeBytes);

/// <summary>One data-quality assertion a run evaluated: a drill-down row under a run.</summary>
public sealed record RunAssertionDto(
    long Id, Guid RunId, Guid? RepoId, string Name, string Result, string AssertedValue, bool Evaluated, string? Error);

/// <summary>One generated SQL statement a run executed, in execution order: a drill-down row under a run.</summary>
public sealed record RunStatementDto(
    long Id, Guid RunId, Guid? RepoId, int Ordinal, string Step, string Sql);

/// <summary>One surrogate-key generation outcome of a run (ingestion flows): a drill-down row under a run.</summary>
public sealed record RunSurrogateKeyDto(
    long Id, Guid RunId, Guid? RepoId, int SurrogateKeyId, string SurrogateTable, string SurrogateColumn,
    bool IsRemote, long KeysGenerated, long RowsStamped, bool Executed, string? Error);

/// <summary>One per-metric health-check summary of a run (hc flows): a drill-down row under a run.</summary>
public sealed record RunHealthCheckMetricDto(
    long Id, Guid RunId, Guid? RepoId, string Name, int SeriesPoints, int ImputedPoints, int ImmaturePoints,
    int Anomalies, int LevelShifts, bool ModelTrained, string? ModelTrainer, string? Error);

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
        string? flowName, string? batch, bool? latest, int? page, int? pageSize, CancellationToken ct)
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
        // lineage step then flow, so each batch lists once, its steps ascending, exactly like the batch report.
        var ordered = latest == true
            ? joined.OrderBy(x => x.Batch).ThenBy(x => x.Wave).ThenBy(x => x.Run.FlowName).ThenBy(x => x.Run.RunId)
            : joined.OrderByDescending(x => x.Run.WrittenUtc).ThenBy(x => x.Run.RunId);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new RunSummaryDto(
                x.Run.RunId, x.Run.PipelineId, x.Run.RepoId, x.Run.FlowName, x.Run.FlowKind, x.Batch, x.Wave,
                x.Run.Status, x.Run.Success,
                x.Run.TargetPool, x.Run.CommitSha, x.Run.WrittenUtc, x.Run.EnqueuedUtc, x.Run.DurationSeconds,
                x.Run.RowsLoaded, x.Run.RowsInserted, x.Run.RowsUpdated, x.Run.RowsDeleted))
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
                    run.RowsLoaded, run.RowsInserted, run.RowsUpdated, run.RowsDeleted, run.Error, run.Host,
                    run.FullLoad, run.BackfillFrom, run.BackfillTo, run.FilePattern))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return dto is null ? NotFound("run", runId) : TypedResults.Ok(dto);
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
            .Select(x => new RunStatementDto(x.Id, x.RunId, x.RepoId, x.Ordinal, x.Step, x.Sql))
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
