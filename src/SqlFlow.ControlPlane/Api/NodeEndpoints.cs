using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A compute node in the fleet: when it was first and last heard from, its build, and whether it is
/// currently online (last heartbeat within the liveness window, computed at read time).</summary>
public sealed record NodeDto(string Name, DateTime FirstSeenUtc, DateTime LastSeenUtc, string? Version, bool Online);

/// <summary>
/// The fleet read surface: <c>GET /api/v1/nodes</c> lists the workers that have heartbeated into the catalog, most
/// recently seen first, with a derived online flag so the GUI shows which nodes are alive right now. Read scope;
/// the registry is populated by the workers themselves (their heartbeat), not by this surface.
/// </summary>
public static class NodeEndpoints
{
    // A node heartbeats every poll (a few seconds); treat it as online if heard from within the last minute, which
    // tolerates a slow poll or a brief hiccup without flapping.
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(60);

    public static RouteGroupBuilder MapNodeEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/nodes", ListNodesAsync).WithTags("Nodes").WithName("ListNodes");
        return group;
    }

    private static async Task<Ok<PagedResult<NodeDto>>> ListNodesAsync(
        CatalogDbContext db, TimeProvider clock, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var staleBefore = clock.GetUtcNow().UtcDateTime - OnlineWindow;

        var ordered = db.Nodes.AsNoTracking().OrderByDescending(n => n.LastSeenUtc).ThenBy(n => n.Name);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(n => new NodeDto(n.Name, n.FirstSeenUtc, n.LastSeenUtc, n.Version, n.LastSeenUtc >= staleBefore))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<NodeDto>(items, p, size, total));
    }
}
