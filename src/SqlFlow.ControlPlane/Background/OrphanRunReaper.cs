using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Fails runs that a dead node left <c>running</c>. A worker claims the oldest queued run and flips it to
/// <c>running</c> under its own node name; if that process then dies without recording an outcome (a crashed or
/// evicted pod, which under Kubernetes never returns under the same name), the run would sit <c>running</c> forever.
/// That is not just stale history: the claim's pipeline gate makes a stuck <c>running</c> run block every future run
/// of the same flow, and the GUI shows it as healthily executing when nothing is executing it. This sweep clears
/// that class of orphan by liveness, so it works across pod restarts where a node's own startup recovery (which
/// matches only its own machine name) never can.
/// </summary>
/// <remarks>
/// It keys off the same fleet heartbeat the Nodes page reads, which is trustworthy because a node heartbeats on a
/// cadence independent of its draining (see <c>RunWorker.HeartbeatLoopAsync</c>): a busy, fully-saturated node still
/// beats, so it is never mistaken for a dead one. The stale window is set comfortably larger than that cadence, so a
/// brief heartbeat gap (a transient catalog outage) never fails a live node's work. Each run is failed with a
/// conditional update guarded on it still being <c>running</c>, so every control-plane replica can run this sweep
/// without double-failing a run or racing a node that completes its run in the same instant. A tick error (a
/// transient database outage) is logged, secret-redacted, and retried next tick; it never stops the loop.
/// </remarks>
public sealed partial class OrphanRunReaper : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _staleAfter;
    private readonly ILogger<OrphanRunReaper> _logger;

    public OrphanRunReaper(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<ControlPlaneOptions> options,
        ILogger<OrphanRunReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _clock = clock;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.Reaper.PollSeconds));
        _staleAfter = TimeSpan.FromSeconds(Math.Max(60, options.Value.Reaper.StaleAfterSeconds));
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
            catch (Exception ex)
            {
                LogTickError(SecretHygiene.RedactedMessage(ex.Message));
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
        var staleBefore = now - _staleAfter;
        await using var scope = _services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var reaped = await RunQueueStore.ReapOrphanedRunningAsync(catalog, staleBefore, now, ct).ConfigureAwait(false);
        if (reaped > 0)
        {
            LogReaped(reaped, (int)_staleAfter.TotalSeconds);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Orphan reaper failed {Count} run(s) whose claiming node had not heartbeated in {StaleAfterSeconds}s; their pipelines are unblocked.")]
    private partial void LogReaped(int count, int staleAfterSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Orphan reaper tick error: {Error}")]
    private partial void LogTickError(string error);
}
