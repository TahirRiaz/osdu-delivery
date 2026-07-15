using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>
/// The fleet registry: a worker upserts its heartbeat here as it polls, so the control plane and GUI can see which
/// nodes are alive. Stateless like the other catalog stores; the heartbeat is intentionally cheap (one statement in
/// the common case) and best-effort, so it never slows or breaks a worker's draining.
/// </summary>
public static class NodeStore
{
    /// <summary>Records a node's heartbeat: refreshes its last-seen (and version), inserting the node the first time
    /// it is heard from, and returns the node's current <see cref="CatalogNode.RestartRequestedUtc"/> so the caller
    /// can honor an operator's restart request on the same cadence it heartbeats (null when none is pending). A
    /// freshly inserted node has no pending request, so its first heartbeat always returns null. Idempotent and safe
    /// to call on every poll.</summary>
    public static async Task<DateTime?> HeartbeatAsync(
        CatalogDbContext catalog, string name, string? version, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // The steady-state path is a single UPDATE; only a node's very first heartbeat falls through to an insert.
        var updated = await catalog.Nodes
            .Where(n => n.Name == name)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.LastSeenUtc, nowUtc)
                .SetProperty(n => n.Version, version), ct)
            .ConfigureAwait(false);
        if (updated > 0)
        {
            // A second cheap indexed read on the primary key returns any pending restart request the operator stamped.
            return await catalog.Nodes.AsNoTracking()
                .Where(n => n.Name == name)
                .Select(n => n.RestartRequestedUtc)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        catalog.Nodes.Add(new CatalogNode { Name = name, FirstSeenUtc = nowUtc, LastSeenUtc = nowUtc, Version = version });
        try
        {
            await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another heartbeat (a concurrent poll, or a racing node of the same name) inserted the row first; the
            // node is registered either way, so this is a no-op. Detach the failed add so the context stays clean.
            var entry = catalog.Entry(catalog.Nodes.Local.First(n => n.Name == name));
            entry.State = EntityState.Detached;
        }

        return null;
    }
}
