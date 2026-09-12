using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Removes drop-offs nobody needs any more, when the deployment has asked for that
/// (<c>ControlPlane:DropOff:RetentionDays</c>). It is off by default, and deliberately so: re-processing a submission
/// (a redelivery, a verify) reads its payload files again, so a swept drop-off would quietly turn a working record into
/// one that can no longer be sent. A deployment that knows its files are single-use turns it on.
/// </summary>
/// <remarks>
/// Only a drop-off that uploaded successfully is ever swept. An upload that failed or stopped halfway keeps whatever
/// landed, because somebody has to look at it, and a sweep that tidied it away would hide the very thing worth seeing.
/// A row whose files cannot be removed is logged and left alone for the next pass rather than marked deleted, so the
/// ledger never claims files are gone while they are still in storage. One pass removes a bounded number of drop-offs,
/// and a pass that throws is logged, secret-redacted, and retried on the next tick.
/// </remarks>
public sealed partial class DropOffRetentionService : BackgroundService
{
    /// <summary>How often the sweep looks; retention is measured in days, so an hourly look is ample.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>The most drop-offs one pass removes, so a long-disabled retention turning on never storms storage.</summary>
    private const int MaxPerTick = 100;

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _retention;
    private readonly ILogger<DropOffRetentionService> _logger;

    public DropOffRetentionService(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<ControlPlaneOptions> options,
        ILogger<DropOffRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _clock = clock;
        _retention = TimeSpan.FromDays(Math.Max(0, options.Value.DropOff.RetentionDays));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_retention <= TimeSpan.Zero)
        {
            // Nothing is swept unless the deployment asked for it, which is the default.
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
                // Host teardown has disposed the container out from under this tick; leave quietly.
                break;
            }
            catch (Exception ex)
            {
                LogTickError(SecretHygiene.RedactedMessage(ex));
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
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
        var cutoff = now - _retention;
        await using var scope = _services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<EngineContext>();
        var stale = await catalog.DeliveryDropOffs
            .Where(d => d.Status == DropOffStatus.Complete && d.CompletedUtc != null && d.CompletedUtc < cutoff)
            .OrderBy(d => d.CompletedUtc)
            .Take(MaxPerTick)
            .ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count == 0)
        {
            return;
        }

        var removed = 0;
        foreach (var row in stale)
        {
            try
            {
                await engine.Stores.DeleteAsync(row.Location, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Left as it is, and tried again next pass: a row marked deleted while its files remain would be a lie.
                LogRemovalFailed(row.DropOffId, SecretHygiene.RedactedMessage(ex));
                continue;
            }

            row.Status = DropOffStatus.Deleted;
            row.DeletedUtc = now;
            // Stated explicitly: the rows came back from a query, which this context does not track, so without this
            // the sweep would remove the files and leave every row claiming the drop-off was still complete.
            catalog.Entry(row).State = EntityState.Modified;
            removed++;
        }

        if (removed > 0)
        {
            await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
            LogRemoved(removed, (int)_retention.TotalDays);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} drop-off(s) completed more than {RetentionDays} day(s) ago.")]
    private partial void LogRemoved(int count, int retentionDays);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drop-off {DropOffId} could not be removed and stays until the next pass: {Error}")]
    private partial void LogRemovalFailed(Guid dropOffId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Drop-off retention tick error: {Error}")]
    private partial void LogTickError(string error);
}
