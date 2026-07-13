using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The read API over the shadow catalog: repositories and pipelines, paginated and projected to DTOs (the raw EF
/// entities never leave the host). Every query is read-only (<see cref="EntityFrameworkQueryableExtensions.AsNoTracking"/>)
/// and bounded by page size. Lineage, runs, and search are mapped by their own modules; this is the registry view.
/// </summary>
public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var repos = group.MapGroup("/repos").WithTags("Repositories");
        repos.MapGet("/", ListReposAsync).WithName("ListRepositories");
        repos.MapGet("/{id:guid}", GetRepoAsync).WithName("GetRepository");

        var pipelines = group.MapGroup("/pipelines").WithTags("Pipelines");
        pipelines.MapGet("/", ListPipelinesAsync).WithName("ListPipelines");
        pipelines.MapGet("/{id:guid}", GetPipelineAsync).WithName("GetPipeline");
        pipelines.MapGet("/{id:guid}/definition", GetPipelineDefinitionAsync).WithName("GetPipelineDefinition");
        pipelines.MapGet("/{id:guid}/columns", GetPipelineColumnsAsync).WithName("GetPipelineColumns");
        pipelines.MapGet("/{id:guid}/files", GetPipelineFilesAsync).WithName("GetPipelineFiles");

        return group;
    }

    /// <summary>
    /// The catalog mutations, mapped under the "operate" scope: a manual sync of a local-path repo. A repo tracked by
    /// a git source is kept current by that source's managed sync, so this serves the repos synced by the CLI from a
    /// filesystem path (which the background <see cref="Background.RepoSyncService"/> never polls), giving the GUI a
    /// "Sync now" for them without dropping to the CLI.
    /// </summary>
    public static RouteGroupBuilder MapCatalogWriteEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var repos = group.MapGroup("/repos").WithTags("Repositories");
        repos.MapPost("/{id:guid}/sync", SyncRepoAsync).WithName("SyncRepository");

        return group;
    }

    /// <summary>
    /// Re-sync a local-path repo from its recorded root path, running the same connected catalog sync the CLI's
    /// <c>db sync --connect</c> runs: the flow estate is mirrored and the derived lineage tier is read from the live
    /// database, so the lineage graph (including the object-level view) refreshes. The sync runs inline; a small repo
    /// completes in one request. Refused for a repo managed by a git source (its root path is a transient clone
    /// cache, so a re-sync from it would reflect a stale checkout: trigger the source's own sync instead) and for a
    /// root path the control-plane host cannot see (a local-path sync only works where the flows live on disk).
    /// </summary>
    private static async Task<Results<Ok<RepoSyncResultDto>, ProblemHttpResult>> SyncRepoAsync(
        Guid id, CatalogDbContext db, ISecretResolver secrets, TimeProvider clock, CancellationToken ct)
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
            // The same connected sync the CLI's `db sync --connect` runs: one sync path, with the derived tier (live
            // catalog + sys.sql_modules reads) enabled so the object-level lineage populates. A derived-tier failure
            // (an unreachable database, a timeout) is downgraded to a warning inside SyncAsync and never faults the
            // request; the flow registry and flow-level lineage still land.
            var result = await new CatalogSync().SyncAsync(
                db, repo.RootPath, repo.Name, repo.RemoteUrl, clock.GetUtcNow().UtcDateTime,
                includeDerived: true, secrets: secrets, ct: ct).ConfigureAwait(false);

            return TypedResults.Ok(new RepoSyncResultDto(
                result.PipelinesAdded, result.PipelinesUpdated, result.PipelinesUnchanged, result.PipelinesDeactivated,
                result.ObjectsUpserted, result.ObjectColumns, result.LineageEdges, result.Waves, result.FlowDependencies,
                result.LineageConnected, result.Warnings.Take(20).ToArray()));
        }
        catch (SqlFlowException ex)
        {
            // A domain-level failure of the sync itself (e.g. a flow document embedding a credential): report it as a
            // clean, secret-redacted 400 rather than a 500. Infrastructure faults (catalog writes) bubble to the
            // correlation-id error handler.
            return BadRequest("Sync failed", SecretHygiene.RedactedMessage(ex.Message));
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

    private static async Task<Results<Ok<PagedResult<PipelineSummaryDto>>, ProblemHttpResult>> ListPipelinesAsync(
        CatalogDbContext db, Guid? repoId, string? kind, bool? active, string? name, string? sort,
        int? page, int? pageSize, CancellationToken ct)
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

        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(x => x.Name.Contains(name));
        }

        IQueryable<CatalogPipeline> ordered;
        if (string.IsNullOrWhiteSpace(sort) || string.Equals(sort, "name", StringComparison.OrdinalIgnoreCase))
        {
            ordered = query.OrderBy(x => x.Name).ThenBy(x => x.Id);
        }
        else if (string.Equals(sort, "path", StringComparison.OrdinalIgnoreCase))
        {
            // Path order serves the GUI's folder-tree view: repos by display name, then each flow document by its
            // repo-relative path, so the repo and folder groups the client clusters over arrive contiguous even
            // across page boundaries.
            ordered =
                from x in query
                join repo in db.Repos.AsNoTracking() on x.RepoId equals repo.Id
                orderby repo.Name, repo.Id, x.RelativePath, x.Id
                select x;
        }
        else
        {
            return BadRequest("Unsupported sort", $"The sort '{sort}' is not supported; use 'name' or 'path'.");
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new PipelineSummaryDto(
                x.Id, x.RepoId, x.Name, x.Kind, x.Batch, x.Wave, x.Active, x.ExecutionMode, x.Lifecycle,
                x.SourceServer, x.TargetServer, x.RelativePath, x.FirstSeenUtc, x.LastSeenUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<PipelineSummaryDto>(items, p, size, total));
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

    /// <summary>
    /// The pipeline's pre-ingestion transform columns: <c>declared</c> rows projected from the flow YAML on sync
    /// (the source of truth) and <c>detected</c> rows from the latest run's generated transformation view. An
    /// optional <c>kind</c> filters to one provenance. Ordered declared-first, then by view position. Returned
    /// whole (not paged): a view's column count is bounded by the table it projects.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<PipelineColumnDto>>> GetPipelineColumnsAsync(
        Guid id, CatalogDbContext db, string? kind, CancellationToken ct)
    {
        var query = db.PipelineColumns.AsNoTracking().Where(c => c.PipelineId == id);
        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(c => c.Kind == kind);
        }

        IReadOnlyList<PipelineColumnDto> items = await query
            .OrderBy(c => c.Kind == PipelineColumnKinds.Declared ? 0 : 1).ThenBy(c => c.Ordinal)
            .Select(c => new PipelineColumnDto(
                c.Kind, c.Ordinal, c.ColumnName, c.SourceColumn, c.Expression, c.DataType,
                c.SortOrder, c.IsVirtual, c.ExcludeFromView, c.Converted))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(items);
    }

    /// <summary>
    /// The distinct files a pipeline has processed across its whole run history, newest-modified first: the
    /// "files this pipeline has seen" browser. Deduplicated by name+path (a file re-pulled by several runs appears
    /// once, carrying its newest processing's metadata), searchable by name or path, and each row flagged
    /// <c>lastRun</c> when it was processed by the pipeline's most recent file-bearing run so a caller can separate
    /// "what the last run found" from everything seen. Paged; an unknown pipeline is a 404 (distinct from a real
    /// pipeline that has processed no files, which is an empty page).
    /// </summary>
    private static async Task<Results<Ok<PagedResult<PipelineFileDto>>, ProblemHttpResult>> GetPipelineFilesAsync(
        Guid id, CatalogDbContext db, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await db.Pipelines.AsNoTracking().AnyAsync(p => p.Id == id, ct).ConfigureAwait(false))
        {
            return NotFound("pipeline", id);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);

        // The pipeline's most recent run that actually processed files: the files it touched carry the lastRun
        // flag. Ordering by StartUtc (nulls last) so the newest run wins; null when the pipeline has no files yet.
        var lastRunId = await (
                from f in db.RunFiles.AsNoTracking()
                join r in db.Runs.AsNoTracking() on f.RunId equals r.RunId
                where r.PipelineId == id
                orderby r.StartUtc descending
                select (Guid?)r.RunId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var files =
            from f in db.RunFiles.AsNoTracking()
            join r in db.Runs.AsNoTracking() on f.RunId equals r.RunId
            where r.PipelineId == id
            select new { f.Name, f.Path, f.Modified, f.Rows, f.SizeBytes, r.RunId, r.StartUtc };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            files = files.Where(x => x.Name.Contains(term) || (x.Path != null && x.Path.Contains(term)));
        }

        // Deduplicate to one row per file (name + path), keeping the newest processing's metadata and whether any
        // of its processings was the last run. File-constant attributes (rows/size/modified) are stable across
        // re-pulls, so the group aggregates are the file's own values.
        var distinct = files
            .GroupBy(x => new { x.Name, x.Path })
            .Select(g => new
            {
                g.Key.Name,
                g.Key.Path,
                Modified = g.Max(x => x.Modified),
                Rows = g.Max(x => x.Rows),
                SizeBytes = g.Max(x => x.SizeBytes),
                LastProcessedUtc = g.Max(x => x.StartUtc),
                InLastRun = g.Max(x => x.RunId == lastRunId ? 1 : 0),
            });

        var total = await distinct.LongCountAsync(ct).ConfigureAwait(false);
        var items = await distinct
            .OrderByDescending(x => x.Modified).ThenBy(x => x.Name)
            .Skip((p - 1) * size).Take(size)
            .Select(x => new PipelineFileDto(
                x.Name, x.Path, x.Modified, x.Rows, x.SizeBytes, x.InLastRun == 1, x.LastProcessedUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<PipelineFileDto>(items, p, size, total));
    }

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");

    private static ProblemHttpResult BadRequest(string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: title);
}
