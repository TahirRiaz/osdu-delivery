using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Identity;

namespace SqlFlow.Catalog;

/// <summary>The result of a sync-now / disable request, so the API can answer 200 / 404 precisely.</summary>
public enum RepoSourceMutation
{
    Applied,
    NotFound,
}

/// <summary>
/// Persistence for the managed-sync source registry: register/refresh a tracked git repo, find the sources due to
/// sync, claim one atomically (so several control-plane nodes never sync the same source at once), and record each
/// sync's outcome. Stateless like the other catalog stores; the git pull + the actual catalog sync are the control
/// plane's job, this only holds the schedule and results.
/// </summary>
public static class RepoSourceStore
{
    /// <summary>Registers or refreshes a tracked repo (keyed by name, so re-registering updates in place). A new
    /// source is due to sync immediately; an existing one keeps its schedule but takes the refreshed settings.</summary>
    public static Task<Guid> UpsertAsync(
        CatalogDbContext catalog, string name, string remoteUrl, string branch, bool enabled, int syncIntervalSeconds,
        DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteUrl);

        var id = FlowIdentity.FromName($"reposource/{name}");
        var interval = Math.Max(1, syncIntervalSeconds);
        var effectiveBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var existing = await catalog.RepoSources.AsTracking()
                .FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
            if (existing is null)
            {
                catalog.RepoSources.Add(new CatalogRepoSource
                {
                    Id = id,
                    Name = name,
                    RemoteUrl = remoteUrl,
                    Branch = effectiveBranch,
                    Enabled = enabled,
                    SyncIntervalSeconds = interval,
                    NextSyncUtc = nowUtc, // a new source syncs on the next tick
                    CreatedUtc = nowUtc,
                    UpdatedUtc = nowUtc,
                });
                return id;
            }

            existing.RemoteUrl = remoteUrl;
            existing.Branch = effectiveBranch;
            existing.Enabled = enabled;
            existing.SyncIntervalSeconds = interval;
            existing.UpdatedUtc = nowUtc;
            // Re-enabling a source (or one that never got a next-sync) becomes due now.
            if (enabled && existing.NextSyncUtc is null)
            {
                existing.NextSyncUtc = nowUtc;
            }

            return id;
        }, ct);
    }

    /// <summary>The enabled sources whose next sync has arrived, oldest-due first.</summary>
    public static async Task<IReadOnlyList<CatalogRepoSource>> ListDueAsync(
        CatalogDbContext catalog, DateTime nowUtc, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.RepoSources.AsNoTracking()
            .Where(s => s.Enabled && s.NextSyncUtc != null && s.NextSyncUtc <= nowUtc)
            .OrderBy(s => s.NextSyncUtc)
            .Take(Math.Clamp(max, 1, 1000))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Atomically claims a due source by advancing its next-sync from the observed value, so exactly one
    /// control-plane node syncs it this interval. Returns true if this caller won.</summary>
    public static async Task<bool> TryClaimSyncAsync(
        CatalogDbContext catalog, Guid id, DateTime observedNextSyncUtc, DateTime newNextSyncUtc, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.RepoSources
            .Where(s => s.Id == id && s.NextSyncUtc == observedNextSyncUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.NextSyncUtc, newNextSyncUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>Records a successful sync: the pulled commit and the time, clearing any prior error.</summary>
    public static Task RecordSuccessAsync(
        CatalogDbContext catalog, Guid id, string commitSha, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.RepoSources
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastSyncUtc, nowUtc)
                .SetProperty(x => x.LastSyncedSha, commitSha)
                .SetProperty(x => x.LastError, (string?)null)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct);
    }

    /// <summary>Records a failed sync: keeps the last successful commit and stores the (redacted) error.</summary>
    public static Task RecordFailureAsync(
        CatalogDbContext catalog, Guid id, string error, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.RepoSources
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastSyncUtc, nowUtc)
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct);
    }

    /// <summary>Makes a source due immediately (the sync-now action), so the next tick pulls it.</summary>
    public static async Task<RepoSourceMutation> TriggerNowAsync(
        CatalogDbContext catalog, Guid id, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.RepoSources
            .Where(s => s.Id == id && s.Enabled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.NextSyncUtc, nowUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0 ? RepoSourceMutation.Applied : RepoSourceMutation.NotFound;
    }
}
