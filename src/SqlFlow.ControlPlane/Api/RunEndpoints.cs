using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A run as it appears in lists: the lifecycle status, header dimensions, and headline row counts, without
/// the drill-down detail. Ordered newest-first by <see cref="WrittenUtc"/>.</summary>
public sealed record RunSummaryDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime WrittenUtc, DateTime? EnqueuedUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted);

/// <summary>One run with its full header for the detail view: the summary plus the lifecycle fields (status, when it
/// was enqueued, the node that claimed it), the schema version, the start/end window, the host, and the error.</summary>
public sealed record RunDetailDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime? EnqueuedUtc, string? ClaimedByNode,
    int SchemaVersion, DateTime WrittenUtc, DateTime? StartUtc, DateTime? EndUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, string? Error, string? Host);

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
        string? flowName, int? page, int? pageSize, CancellationToken ct)
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

        if (!string.IsNullOrWhiteSpace(flowName))
        {
            query = query.Where(x => x.FlowName.Contains(flowName));
        }

        var ordered = query.OrderByDescending(x => x.WrittenUtc).ThenBy(x => x.RunId);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new RunSummaryDto(
                x.RunId, x.PipelineId, x.RepoId, x.FlowName, x.FlowKind, x.Status, x.Success,
                x.TargetPool, x.CommitSha, x.WrittenUtc, x.EnqueuedUtc, x.DurationSeconds,
                x.RowsLoaded, x.RowsInserted, x.RowsUpdated, x.RowsDeleted))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunSummaryDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<RunDetailDto>, ProblemHttpResult>> GetRunAsync(
        Guid runId, CatalogDbContext db, CancellationToken ct)
    {
        var dto = await db.Runs.AsNoTracking().Where(x => x.RunId == runId)
            .Select(x => new RunDetailDto(
                x.RunId, x.PipelineId, x.RepoId, x.FlowName, x.FlowKind, x.Status, x.Success,
                x.TargetPool, x.CommitSha, x.EnqueuedUtc, x.ClaimedByNode,
                x.SchemaVersion, x.WrittenUtc, x.StartUtc, x.EndUtc, x.DurationSeconds,
                x.RowsLoaded, x.RowsInserted, x.RowsUpdated, x.RowsDeleted, x.Error, x.Host))
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
