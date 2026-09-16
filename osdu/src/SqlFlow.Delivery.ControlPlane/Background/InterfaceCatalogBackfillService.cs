using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Describes the interfaces of the delivery pipelines a repository sync has not described yet: the pipelines synced before
/// the read model of sources and interfaces existed (<see cref="DeliveryInterfaceCatalog"/>). Once, when the control plane
/// starts, it parses each such repository's catalog copies and writes their rows exactly as the repository sync writes
/// them, so a record's page finds its flow without waiting for the repository's next sync. A repository the read model
/// already describes is its sync's to keep current. A pass that fails (the module database not migrated yet, a lost
/// connection) is tried again until one completes.
/// </summary>
public sealed partial class InterfaceCatalogBackfillService : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<InterfaceCatalogBackfillService> _logger;

    public InterfaceCatalogBackfillService(IServiceProvider services, TimeProvider clock, ILogger<InterfaceCatalogBackfillService> logger)
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
                await BackfillAsync(stoppingToken).ConfigureAwait(false);
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

    /// <summary>One pass over every repository with delivery pipelines and no interface rows.</summary>
    public async Task BackfillAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var osdu = scope.ServiceProvider.GetRequiredService<OsduDbContext>();
        var documents = scope.ServiceProvider.GetRequiredService<DeliveryDocumentLoader>();
        var pipelines = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.Kind == FlowDefinition.FlowTypeName)
            .Select(p => new { p.RepoId, p.Name, p.RelativePath, p.Yaml, p.Active })
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var repository in pipelines.GroupBy(p => p.RepoId))
        {
            ct.ThrowIfCancellationRequested();
            if ((await DeliveryInterfaceCatalog.DescribedFlowsAsync(osdu, repository.Key, ct).ConfigureAwait(false)).Count > 0)
            {
                continue;
            }

            var sources = new List<RepositorySource>();
            var unreadable = 0;
            foreach (var pipeline in repository.OrderByDescending(p => p.Active).ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                try
                {
                    sources.Add(new RepositorySource(pipeline.RelativePath, documents.ParseSource(pipeline.Yaml, pipeline.RelativePath), pipeline.Active));
                }
                catch (FlowValidationException)
                {
                    // A catalog copy that does not parse describes no interface; its pipeline's page says why.
                    unreadable++;
                }
            }

            if (sources.Count == 0)
            {
                continue;
            }

            var warnings = new List<string>();
            var kinds = await DeliveryCatalogSync.MappingKindsAsync(osdu, repository.Key, ct).ConfigureAwait(false);
            var counts = await DeliveryInterfaceCatalog.ReconcileAsync(
                osdu, repository.Key, sources, kinds, _clock.GetUtcNow().UtcDateTime, warnings, ct).ConfigureAwait(false);
            LogBackfilled(repository.Key, counts.Added, sources.Count, unreadable);
            foreach (var warning in warnings)
            {
                LogWarning(repository.Key, warning);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Described {Interfaces} interface(s) of {Flows} delivery flow(s) of repository {RepoId} from their catalog copies ({Unreadable} did not parse).")]
    private partial void LogBackfilled(Guid repoId, int interfaces, int flows, int unreadable);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repository {RepoId}: {Warning}")]
    private partial void LogWarning(Guid repoId, string warning);

    [LoggerMessage(Level = LogLevel.Error, Message = "Describing the interfaces of delivery flows failed, and is tried again in {Seconds}s: {Error}")]
    private partial void LogPassError(string error, int seconds);
}
