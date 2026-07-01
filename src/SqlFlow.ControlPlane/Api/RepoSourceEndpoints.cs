using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A tracked git repo the control plane auto-syncs into the catalog, with its schedule and last result.</summary>
public sealed record RepoSourceDto(
    Guid Id, string Name, string RemoteUrl, string Branch, bool Enabled, int SyncIntervalSeconds,
    DateTime? NextSyncUtc, DateTime? LastSyncUtc, string? LastSyncedSha, string? LastError,
    DateTime CreatedUtc, DateTime UpdatedUtc);

/// <summary>The body to register (or update) a tracked repo source.</summary>
public sealed record RegisterRepoSourceRequest(
    string Name, string RemoteUrl, string? Branch, int? SyncIntervalSeconds, bool? Enabled);

/// <summary>The registered-source acknowledgement.</summary>
public sealed record RepoSourceRegistered(Guid Id);

/// <summary>
/// The managed-sync surface: register a git repo the control plane keeps the catalog synced from, list the tracked
/// sources (with their last commit and any error), and force a sync now. Reads are mapped under the "read" scope;
/// the mutations under "operate". A credential to pull a private remote is never accepted here - the control plane
/// resolves it from its own environment.
/// </summary>
public static class RepoSourceEndpoints
{
    public static RouteGroupBuilder MapRepoSourceReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/repos/sources", ListSourcesAsync).WithTags("RepoSources").WithName("ListRepoSources");
        return group;
    }

    public static RouteGroupBuilder MapRepoSourceWriteEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPost("/repos/sources", RegisterSourceAsync).WithTags("RepoSources").WithName("RegisterRepoSource");
        group.MapPost("/repos/sources/{id:guid}/sync", SyncNowAsync).WithTags("RepoSources").WithName("SyncRepoSourceNow");
        return group;
    }

    private static async Task<Ok<PagedResult<RepoSourceDto>>> ListSourcesAsync(
        CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var ordered = db.RepoSources.AsNoTracking().OrderBy(s => s.Name);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered.Skip((p - 1) * size).Take(size).Select(Project).ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RepoSourceDto>(items, p, size, total));
    }

    private static async Task<Results<Created<RepoSourceRegistered>, ProblemHttpResult>> RegisterSourceAsync(
        RegisterRepoSourceRequest request, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.RemoteUrl))
        {
            return TypedResults.Problem(
                detail: "A repo source requires a non-blank name and remoteUrl.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var id = await RepoSourceStore.UpsertAsync(
            db, request.Name.Trim(), request.RemoteUrl.Trim(), request.Branch ?? "main",
            request.Enabled ?? true, request.SyncIntervalSeconds ?? 300, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);

        return TypedResults.Created($"/api/v1/repos/sources/{id}", new RepoSourceRegistered(id));
    }

    private static async Task<Results<Ok<RepoSourceDto>, ProblemHttpResult>> SyncNowAsync(
        Guid id, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outcome = await RepoSourceStore.TriggerNowAsync(db, id, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        if (outcome == RepoSourceMutation.NotFound)
        {
            return TypedResults.Problem(
                detail: $"No enabled repo source '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var dto = await db.RepoSources.AsNoTracking().Where(s => s.Id == id).Select(Project).FirstAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(dto);
    }

    // An expression (not a method) so EF Core can translate the projection into the SELECT column list.
    private static readonly Expression<Func<CatalogRepoSource, RepoSourceDto>> Project = s => new RepoSourceDto(
        s.Id, s.Name, s.RemoteUrl, s.Branch, s.Enabled, s.SyncIntervalSeconds,
        s.NextSyncUtc, s.LastSyncUtc, s.LastSyncedSha, s.LastError, s.CreatedUtc, s.UpdatedUtc);
}
