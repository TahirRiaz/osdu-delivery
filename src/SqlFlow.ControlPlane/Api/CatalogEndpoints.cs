using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The read API over the shadow catalog's registry: repositories and the pipelines (flow documents) synced from
/// them. Every query is read-only and DTO-projected so the raw EF entities never leave the host; list endpoints
/// are bounded by page size.
/// </summary>
public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var repos = group.MapGroup("/repos").WithTags("Repos");
        repos.MapGet("/", ListReposAsync).WithName("ListRepositories");
        repos.MapGet("/{id:guid}", GetRepoAsync).WithName("GetRepository");

        var pipelines = group.MapGroup("/pipelines").WithTags("Pipelines");
        pipelines.MapGet("/", ListPipelinesAsync).WithName("ListPipelines");
        pipelines.MapGet("/batches", ListPipelineBatchesAsync).WithName("ListPipelineBatches");
        pipelines.MapGet("/projects", ListPipelineProjectsAsync).WithName("ListPipelineProjects");
        pipelines.MapGet("/{id:guid}", GetPipelineAsync).WithName("GetPipeline");
        pipelines.MapGet("/{id:guid}/definition", GetPipelineDefinitionAsync).WithName("GetPipelineDefinition");

        return group;
    }

    /// <summary>The operate-scoped writes over the registry: a manual local-path re-sync and the repo delete.</summary>
    public static RouteGroupBuilder MapCatalogWriteEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var repos = group.MapGroup("/repos").WithTags("Repos");
        repos.MapPost("/{id:guid}/sync", SyncRepoAsync).WithName("SyncRepository");
        repos.MapDelete("/{id:guid}", DeleteRepoAsync).WithName("DeleteRepository");

        return group;
    }

    /// <summary>
    /// Deletes a repo and everything attributed to it: its pipelines, run history (and the runs' traces), run
    /// groups and schedules, plus any managed git source registered under the same name (dropped so the
    /// background sync cannot recreate the repo). Irreversible, so it is an explicit operator action rather than
    /// a side effect of a sync. Returns the counts removed, or 404 when no repo has the given id.
    /// </summary>
    private static async Task<Results<Ok<RepoDeletionResult>, ProblemHttpResult>> DeleteRepoAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var result = await RepoStore.DeleteAsync(db, id, ct).ConfigureAwait(false);
        return result is null ? NotFound("repository", id) : TypedResults.Ok(result);
    }

    /// <summary>
    /// Re-sync a local-path repo from its recorded root path, running the same catalog sync the CLI's
    /// <c>db sync</c> runs. The sync runs inline; a small repo completes in one request. Refused for a repo managed
    /// by a git source (its root path is a transient clone cache, so a re-sync from it would reflect a stale
    /// checkout: trigger the source's own sync instead) and for a root path the control-plane host cannot see (a
    /// local-path sync only works where the flows live on disk).
    /// </summary>
    private static async Task<Results<Ok<RepoSyncResultDto>, ProblemHttpResult>> SyncRepoAsync(
        Guid id, CatalogDbContext db, CatalogSync sync, TimeProvider clock, CancellationToken ct)
    {
        var repo = await db.Repos.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new { r.Name, r.RemoteUrl, r.RootPath })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (repo is null)
        {
            return NotFound("repository", id);
        }

        // A repo tracked by a git source is kept current by the managed sync (RepoSyncService), whose root path is a
        // transient clone cache; a local-path sync from it would reflect a stale checkout. Direct the caller to the
        // source's own sync so each repo has a single, unambiguous sync path.
        var hasSource = await db.RepoSources.AsNoTracking()
            .AnyAsync(s => s.Name == repo.Name, ct).ConfigureAwait(false);
        if (hasSource)
        {
            return BadRequest(
                "Managed by a git source",
                $"Repo '{repo.Name}' is synced from a registered git source; trigger its sync from the repo source instead of a local-path sync.");
        }

        if (string.IsNullOrWhiteSpace(repo.RootPath))
        {
            return BadRequest("No local path", $"Repo '{repo.Name}' has no recorded root path to sync from.");
        }

        if (!Directory.Exists(repo.RootPath))
        {
            return BadRequest(
                "Path not reachable",
                $"The control-plane host cannot see the repo's root path '{repo.RootPath}'. A local-path sync only works when the control plane runs on the machine that holds the flow files.");
        }

        try
        {
            var result = await sync.SyncAsync(
                db, repo.RootPath, repo.Name, repo.RemoteUrl, clock.GetUtcNow().UtcDateTime, ct: ct).ConfigureAwait(false);

            return TypedResults.Ok(new RepoSyncResultDto(
                result.PipelinesAdded, result.PipelinesUpdated, result.PipelinesUnchanged, result.PipelinesDeactivated, result.PipelinesDeleted,
                result.RunsAdded, result.RunsSkipped, result.RunsFailed,
                result.DocumentsAdded, result.DocumentsUpdated, result.DocumentsUnchanged, result.DocumentsRemoved, result.DocumentsInvalid,
                result.Warnings.Take(20).ToArray()));
        }
        catch (SqlFlowException ex)
        {
            // A domain-level failure of the sync itself (e.g. a flow document embedding a credential): report it as a
            // clean, secret-redacted 400 rather than a 500. Infrastructure faults (catalog writes) bubble to the
            // correlation-id error handler.
            return BadRequest("Sync failed", SecretHygiene.RedactedMessage(ex));
        }
    }

    private static async Task<Ok<PagedResult<RepoDto>>> ListReposAsync(
        CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = db.Repos.AsNoTracking().OrderBy(r => r.Name).ThenBy(r => r.Id);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query
            .Skip((p - 1) * size).Take(size)
            .Select(r => new RepoDto(r.Id, r.Name, r.RemoteUrl, r.RootPath, r.FirstSeenUtc, r.LastSyncUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RepoDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<RepoDto>, ProblemHttpResult>> GetRepoAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var dto = await db.Repos.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new RepoDto(r.Id, r.Name, r.RemoteUrl, r.RootPath, r.FirstSeenUtc, r.LastSyncUtc))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return dto is null ? NotFound("repository", id) : TypedResults.Ok(dto);
    }

    private static async Task<Ok<PagedResult<PipelineSummaryDto>>> ListPipelinesAsync(
        CatalogDbContext db, Guid? repoId, string? kind, bool? active, string? name, string? project,
        string? batch, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);

        var query = db.Pipelines.AsNoTracking().AsQueryable();
        if (repoId is { } r)
        {
            query = query.Where(x => x.RepoId == r);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(x => x.Kind == kind);
        }

        if (active is { } a)
        {
            query = query.Where(x => x.Active == a);
        }

        // The default batch is a presentation label for flows that declare none, so filtering by it must match
        // both the literal label and the null rows it stands in for (mirrors the coalescing in the batches list).
        if (!string.IsNullOrWhiteSpace(batch))
        {
            query = batch == CatalogPipeline.DefaultBatch
                ? query.Where(x => x.Batch == null || x.Batch == batch)
                : query.Where(x => x.Batch == batch);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(x => x.Name.Contains(name));
        }

        // The project is a flow's root folder within its repo (the first path segment, or "(root)" for a flow at
        // the repo root): narrow the list to one source's folder. Root flows have no slash; a named project matches
        // its folder prefix so nested folders under it stay included.
        if (!string.IsNullOrWhiteSpace(project))
        {
            // Contains("/") stays a string overload: this predicate is translated to SQL by EF Core, and the
            // char overload CA1847 suggests is not guaranteed to translate to the same CHARINDEX.
#pragma warning disable CA1847
            query = project == ProjectPath.Root
                ? query.Where(x => !x.RelativePath.Contains("/"))
                : query.Where(x => x.RelativePath.StartsWith(project + "/"));
#pragma warning restore CA1847
        }

        var ordered = query.OrderBy(x => x.Name).ThenBy(x => x.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new PipelineSummaryDto(
                x.Id, x.RepoId, x.Name, x.Kind, x.Batch, x.Wave, x.Active, x.ExecutionMode, x.Lifecycle,
                x.SourceServer, x.TargetServer, x.RelativePath, x.FirstSeenUtc, x.LastSeenUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<PipelineSummaryDto>(items, p, size, total));
    }

    /// <summary>
    /// The distinct batches (source-system groupings) of the catalog's flows, optionally scoped to one repo, each
    /// with its flow counts, in one response (no paging): the distinct batches are bounded by the estate's source
    /// count, not its flow count. A flow that declares no batch is coalesced into
    /// <see cref="CatalogPipeline.DefaultBatch"/>, matching how the pipelines list's batch filter resolves it.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<PipelineBatchDto>>> ListPipelineBatchesAsync(
        CatalogDbContext db, Guid? repoId, bool? active, CancellationToken ct)
    {
        var query = db.Pipelines.AsNoTracking().AsQueryable();
        if (repoId is { } r)
        {
            query = query.Where(x => x.RepoId == r);
        }

        if (active is { } a)
        {
            query = query.Where(x => x.Active == a);
        }

        var groups = await query
            .GroupBy(x => new { x.RepoId, Batch = x.Batch ?? CatalogPipeline.DefaultBatch })
            .Select(g => new PipelineBatchDto(
                g.Key.RepoId, g.Key.Batch, g.Count(), g.Count(x => x.Active)))
            .ToListAsync(ct).ConfigureAwait(false);

        var ordered = groups
            .OrderBy(b => b.RepoId)
            .ThenBy(b => b.Batch, StringComparer.Ordinal)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<PipelineBatchDto>>(ordered);
    }

    /// <summary>
    /// The distinct projects (repo-root folders) the pipelines list can be filtered by, optionally scoped to one
    /// repo, sorted for a stable dropdown. Derived from each flow's repo-relative path (see <see cref="ProjectPath"/>):
    /// the segment before the first slash, or the root project for a flow at the repo root. Distinct paths are pulled
    /// once and reduced in memory (the segment split is not worth pushing into SQL for the catalog's cardinality).
    /// </summary>
    private static async Task<Ok<IReadOnlyList<string>>> ListPipelineProjectsAsync(
        CatalogDbContext db, Guid? repoId, CancellationToken ct)
    {
        var query = db.Pipelines.AsNoTracking().AsQueryable();
        if (repoId is { } r)
        {
            query = query.Where(x => x.RepoId == r);
        }

        var paths = await query.Select(x => x.RelativePath).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var projects = paths
            .Select(ProjectPath.Of)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<string>>(projects);
    }

    private static async Task<Results<Ok<PipelineDetailDto>, ProblemHttpResult>> GetPipelineAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var dto = await db.Pipelines.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new PipelineDetailDto(
                x.Id, x.RepoId, x.Name, x.Kind, x.Batch, x.Wave, x.Active, x.ExecutionMode, x.Lifecycle,
                x.SourceServer, x.TargetServer, x.RelativePath, x.ContentHash,
                x.Yaml, x.DefinitionJson, x.FirstSeenUtc, x.LastSeenUtc))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return dto is null ? NotFound("pipeline", id) : TypedResults.Ok(dto);
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> GetPipelineDefinitionAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var json = await db.Pipelines.AsNoTracking().Where(x => x.Id == id)
            .Select(x => x.DefinitionJson)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return json is null
            ? NotFound("pipeline", id)
            : TypedResults.Text(json, "application/json");
    }

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");

    private static ProblemHttpResult BadRequest(string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: title);
}
