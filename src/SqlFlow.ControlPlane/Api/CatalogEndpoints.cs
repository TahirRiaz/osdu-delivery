using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

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

        return group;
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
        CatalogDbContext db, Guid? repoId, string? kind, bool? active, string? name, int? page, int? pageSize, CancellationToken ct)
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

        var ordered = query.OrderBy(x => x.Name).ThenBy(x => x.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new PipelineSummaryDto(
                x.Id, x.RepoId, x.Name, x.Kind, x.Batch, x.Wave, x.Active,
                x.SourceServer, x.TargetServer, x.RelativePath, x.FirstSeenUtc, x.LastSeenUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<PipelineSummaryDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PipelineDetailDto>, ProblemHttpResult>> GetPipelineAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var dto = await db.Pipelines.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new PipelineDetailDto(
                x.Id, x.RepoId, x.Name, x.Kind, x.Batch, x.Wave, x.Active,
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

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");
}
