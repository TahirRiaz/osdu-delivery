using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Makes every enabled repository source due and wakes the sync loop, so the sync that describes each registry-driven
/// flow in the partitions the registry holds starts at once: the same request an operator makes with "sync now". What a
/// change of the partition registry asks for, whoever made it.
/// </summary>
internal static class RepositoryResync
{
    public static async Task NowAsync(CatalogDbContext catalog, RepoSyncSignal syncSignal, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(syncSignal);
        var sources = await catalog.RepoSources.AsNoTracking().Where(s => s.Enabled).Select(s => s.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var source in sources)
        {
            await RepoSourceStore.TriggerNowAsync(catalog, source, nowUtc, ct).ConfigureAwait(false);
        }

        syncSignal.Wake();
    }
}
