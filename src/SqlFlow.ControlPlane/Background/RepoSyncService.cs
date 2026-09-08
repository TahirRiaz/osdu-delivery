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
    private readonly CatalogSync _sync;
    private readonly bool _enabled;
    private readonly GitMaterializer _materializer = new();
    private readonly ILogger<RepoSyncService> _logger;

    public RepoSyncService(
        IServiceProvider services, TimeProvider clock, IOptions<ControlPlaneOptions> options, CatalogSync sync, ILogger<RepoSyncService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _sync = sync;
        _clock = clock;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.ManagedSync.PollSeconds));
        // The control-plane app owns the managed sync; the compute workers run only the drain loop.
        // ManagedSync.Enabled=false opts an instance out entirely (a local dev control plane sharing the
        // production catalog must never steal claims).
        _enabled = options.Value.ManagedSync.Enabled;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            LogSyncDisabled();
            return;
        }

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
                LogScanError(SecretHygiene.RedactedMessage(ex));
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

        // Trace this attempt into the activity log the GUI's bottom panel tails, so an operator watches the sync
        // happen (clone, checkout, reconcile, result, warnings) instead of seeing only a terminal badge. The trace
        // shares this scope's catalog context and detaches its own rows, so it never disturbs the sync's writes;
        // its subject is the source id, keyed by the well-known repo-sync kind.
        var trace = await ActivityTrace.BeginAsync(
            catalog, ActivityKinds.RepoSync, source.Id.ToString(), _clock, ct).ConfigureAwait(false);

        try
        {
            await trace.InfoAsync("start", $"Sync started for '{source.Name}' (branch {source.Branch}).", ct).ConfigureAwait(false);

            // Resolve the source's git credential from its stored ${...} reference (Key Vault / env); the secret
            // value is never stored in the catalog, only fetched here for the clone. A source with no reference
            // falls back to the host environment (public remotes, single-credential deployments).
            await trace.InfoAsync("credentials", "Resolving git credentials.", ct).ConfigureAwait(false);
            var resolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();
            var credentials = await GitMaterializer
                .ResolveCredentialsAsync(resolver, source.CredentialReference, source.CredentialUsername, ct)
                .ConfigureAwait(false);

            await trace.InfoAsync("clone", $"Cloning {source.RemoteUrl} (branch {source.Branch}).", ct).ConfigureAwait(false);
            var (workingDir, sha) = _materializer.MaterializeBranch(source.RemoteUrl, source.Branch, credentials, ct);
            await trace.InfoAsync("clone", $"Checked out {sha}.", ct).ConfigureAwait(false);

            // The preview-first selection: only the flows the source includes are projected as pipelines (an
            // excluded flow never becomes a catalog pipeline, so the scheduler never picks it up).
            var excludedFlowPaths = RepoSourceStore.ParseExcludedPaths(source.ExcludedFlowPaths);

            // The exact same catalog sync the CLI runs (db sync): one sync path.
            await trace.InfoAsync("sync", "Reconciling the catalog from the estate (pipelines, schedules, runs).", ct).ConfigureAwait(false);
            var result = await _sync.SyncAsync(
                    catalog, workingDir, source.Name, source.RemoteUrl, _clock.GetUtcNow().UtcDateTime,
                    excludedFlowPaths, ct)
                .ConfigureAwait(false);

            await EmitResultAsync(trace, result, ct).ConfigureAwait(false);
            await RepoSourceStore.RecordSuccessAsync(catalog, source.Id, sha, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await trace.CompleteAsync(ActivityStatuses.Succeeded, $"Sync complete at {sha}.", ct).ConfigureAwait(false);
            LogSynced(source.Name, sha);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var redacted = SecretHygiene.RedactedMessage(ex);
            LogSyncError(source.Name, redacted);
            await RecordFailureAsync(catalog, source.Id, redacted).ConfigureAwait(false);
            await CompleteTraceFailureAsync(trace, redacted).ConfigureAwait(false);
        }
    }

    /// <summary>Expands the sync result into a concise activity line plus one warning line per warning, so the panel
    /// shows what the reconcile did and surfaces every warning the sync collected.</summary>
    private static async Task EmitResultAsync(ActivityTrace trace, CatalogSyncResult result, CancellationToken ct)
    {
        await trace.InfoAsync(
            "result",
            $"Pipelines: {result.PipelinesAdded} added, {result.PipelinesUpdated} updated, "
                + $"{result.PipelinesUnchanged} unchanged, {result.PipelinesDeactivated} deactivated, "
                + $"{result.PipelinesDeleted} removed.",
            ct).ConfigureAwait(false);

        if (result.RunsAdded > 0)
        {
            await trace.InfoAsync("result", $"Run history: {result.RunsAdded} run(s) recorded.", ct).ConfigureAwait(false);
        }

        foreach (var warning in result.Warnings)
        {
            await trace.WarnAsync("warning", warning, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Best-effort terminal "failed" trace line, on its own short deadline so a cancelled or unhealthy
    /// request context cannot leave the panel hanging without a terminal event (mirrors <see cref="RecordFailureAsync"/>).</summary>
    private async Task CompleteTraceFailureAsync(ActivityTrace trace, string error)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await trace.CompleteAsync(ActivityStatuses.Failed, $"Sync failed: {error}", cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogScanError(SecretHygiene.RedactedMessage(ex));
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
            LogScanError(SecretHygiene.RedactedMessage(ex));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Synced repo source '{Source}' from git at commit {CommitSha}.")]
    private partial void LogSynced(string source, string commitSha);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repo source '{Source}' sync failed: {Error}")]
    private partial void LogSyncError(string source, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Managed-sync scan error: {Error}")]
    private partial void LogScanError(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Managed sync is disabled on this instance (ControlPlane:ManagedSync:Enabled=false); it will not claim repo syncs.")]
    private partial void LogSyncDisabled();
}
