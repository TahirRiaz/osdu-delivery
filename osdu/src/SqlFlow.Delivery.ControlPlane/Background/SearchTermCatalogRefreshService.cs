using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Writes the search terms of every repository with delivery flows again, once, when the control plane starts
/// (osdu/docs/search-terms.md): from the interfaces, mappings and cache declarations the module database holds, as the
/// repository sync writes them. A module upgraded since a repository's last sync, or migrated before the terms existed,
/// so describes its terms as this version does without waiting for the next sync. What people made of the terms is kept
/// apart and never touched. A pass that fails (the module database not migrated yet, a lost connection) is tried again
/// until one completes.
/// </summary>
public sealed partial class SearchTermCatalogRefreshService : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<SearchTermCatalogRefreshService> _logger;

    public SearchTermCatalogRefreshService(IServiceProvider services, TimeProvider clock, ILogger<SearchTermCatalogRefreshService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown disposed the container out from under the pass; leave quietly.
                return;
            }
            catch (Exception ex)
            {
                LogPassError(SecretHygiene.RedactedMessage(ex), (int)RetryInterval.TotalSeconds);
            }

            try
            {
                await Task.Delay(RetryInterval, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One pass over every repository the module holds interfaces of, each in a write of its own.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var osdu = scope.ServiceProvider.GetRequiredService<OsduDbContext>();
        var documents = scope.ServiceProvider.GetRequiredService<DeliveryDocumentLoader>();
        var repositories = await osdu.DeliveryInterfaces.AsNoTracking()
            .Select(i => i.RepoId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var repoId in repositories)
        {
            ct.ThrowIfCancellationRequested();
            var warnings = new List<string>();
            var counts = await DeliverySearchTermCatalog.ReconcileAsync(osdu, repoId, documents, _clock.GetUtcNow().UtcDateTime, warnings, ct).ConfigureAwait(false);
            osdu.ChangeTracker.Clear();
            LogRefreshed(repoId, counts.Added, counts.Updated, counts.Removed, counts.Unchanged);
            foreach (var warning in warnings)
            {
                LogWarning(repoId, warning);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Search terms of repository {RepoId}: {Added} added, {Updated} updated, {Removed} removed, {Unchanged} unchanged.")]
    private partial void LogRefreshed(Guid repoId, int added, int updated, int removed, int unchanged);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repository {RepoId}: {Warning}")]
    private partial void LogWarning(Guid repoId, string warning);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the search terms again failed, and is tried again in {Seconds}s: {Error}")]
    private partial void LogPassError(string error, int seconds);
}
