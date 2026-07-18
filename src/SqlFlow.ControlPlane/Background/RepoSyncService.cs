using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;
using SqlFlow.Node;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Keeps the shadow catalog synced from git: it scans the registered repo sources, and for each one due it pulls
/// the branch tip and runs the same catalog sync the CLI's <c>db sync</c> runs (pipelines, lineage, schedules, run
/// history), recording the pulled commit. Git stays the source of truth; this just keeps the queryable shadow
/// current without anyone running a sync by hand. A source's next sync is claimed atomically, so when several
/// control-plane nodes run this service the same source is never synced twice in one interval.
/// </summary>
/// <remarks>
/// Robustness: one source's failure (an unreachable remote, a bad branch) is recorded on that source and never
/// stops the others or the loop; a scan error is logged and retried next tick. Git credentials are resolved from
/// the control plane's own environment, never stored in the catalog. All diagnostics are secret-redacted.
/// </remarks>
public sealed partial class RepoSyncService : BackgroundService
{
    private const int MaxPerTick = 50;

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _pollInterval;
    private readonly GitMaterializer _materializer = new();
    private readonly ILogger<RepoSyncService> _logger;

    public RepoSyncService(
        IServiceProvider services, TimeProvider clock, IOptions<ControlPlaneOptions> options, ILogger<RepoSyncService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _clock = clock;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.ManagedSync.PollSeconds));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown has disposed the DI container and the loggers out from under this tick. No work
                // remains and logging would itself throw (the disposed Windows EventLog provider), so leave the loop
                // quietly instead of escalating shutdown noise into a "BackgroundService failed".
                break;
            }
            catch (Exception ex)
            {
                LogScanError(SecretHygiene.RedactedMessage(ex.Message));
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        List<CatalogRepoSource> due;
        await using (var scope = _services.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            due = (await RepoSourceStore.ListDueAsync(catalog, now, MaxPerTick, ct).ConfigureAwait(false)).ToList();
        }

        foreach (var source in due)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            await SyncOneAsync(source, ct).ConfigureAwait(false);
        }
    }

    private async Task SyncOneAsync(CatalogRepoSource source, CancellationToken ct)
    {
        if (source.NextSyncUtc is not { } observed)
        {
            return;
        }

        await using var scope = _services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var now = _clock.GetUtcNow().UtcDateTime;
        var next = now.AddSeconds(source.SyncIntervalSeconds);

        // Claim the sync by advancing the next-sync from the value we observed; only the winner pulls + syncs.
        if (!await RepoSourceStore.TryClaimSyncAsync(catalog, source.Id, observed, next, now, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            // Resolve the source's git credential from its stored ${...} reference (Key Vault / env); the secret
            // value is never stored in the catalog, only fetched here for the clone. A source with no reference
            // falls back to the host environment (public remotes, single-credential deployments).
            var resolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();
            var credentials = await GitMaterializer
                .ResolveCredentialsAsync(resolver, source.CredentialReference, source.CredentialUsername, ct)
                .ConfigureAwait(false);
            var (workingDir, sha) = _materializer.MaterializeBranch(source.RemoteUrl, source.Branch, credentials, ct);

            // The preview-first selection: only the flows the source includes are projected as pipelines (an
            // excluded flow never becomes a catalog pipeline, so the scheduler never picks it up).
            var excludedFlowPaths = RepoSourceStore.ParseExcludedPaths(source.ExcludedFlowPaths);

            // The exact same catalog sync the CLI's `db sync` runs - one sync path. Offline (no derived tier): a
            // managed sync mirrors the git estate; the connected/derived tier is a separate, opt-in concern.
            await new CatalogSync()
                .SyncAsync(catalog, workingDir, source.Name, source.RemoteUrl, _clock.GetUtcNow().UtcDateTime,
                    excludedFlowPaths: excludedFlowPaths, ct: ct)
                .ConfigureAwait(false);

            await RepoSourceStore.RecordSuccessAsync(catalog, source.Id, sha, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            LogSynced(source.Name, sha);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var redacted = SecretHygiene.RedactedMessage(ex.Message);
            LogSyncError(source.Name, redacted);
            await RecordFailureAsync(catalog, source.Id, redacted).ConfigureAwait(false);
        }
    }

    private async Task RecordFailureAsync(CatalogDbContext catalog, Guid id, string error)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RepoSourceStore.RecordFailureAsync(catalog, id, error, _clock.GetUtcNow().UtcDateTime, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogScanError(SecretHygiene.RedactedMessage(ex.Message));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Synced repo source '{Source}' from git at commit {CommitSha}.")]
    private partial void LogSynced(string source, string commitSha);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repo source '{Source}' sync failed: {Error}")]
    private partial void LogSyncError(string source, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Managed-sync scan error: {Error}")]
    private partial void LogScanError(string error);
}
