using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Brings the local copy of the OSDU data definitions up when the control plane starts: loads (or reads) the release list
/// and downloads the newest release when it is not on disk yet, so the first person to open the Templates page's Browse
/// OSDU reads from disk. A failure is not fatal: the page asks again, and its Sync button reports the same problem.
/// </summary>
public sealed class DataDefinitionsWarmupService : BackgroundService
{
    private readonly OsduDataDefinitions _definitions;
    private readonly ILogger<DataDefinitionsWarmupService> _logger;

    public DataDefinitionsWarmupService(OsduDataDefinitions definitions, ILogger<DataDefinitionsWarmupService> logger)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(logger);
        _definitions = definitions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();
        _logger.LogInformation("Preparing the local copy of the OSDU data definitions at {Directory}.", _definitions.CacheDirectory);
        try
        {
            var index = await _definitions.IndexAsync(release: null, stoppingToken).ConfigureAwait(false);
            _logger.LogInformation(
                "The local copy of the OSDU data definitions is ready: release {Release}, {Count} record schemas, in {Seconds:F1} s.",
                index.Release.Name, index.Schemas.Count, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Stopped preparing the local copy of the OSDU data definitions: the control plane is shutting down.");
        }
        catch (DataDefinitionsException ex)
        {
            _logger.LogWarning(ex, "The local copy of the OSDU data definitions could not be prepared at startup; the Templates page tries again when it is opened.");
        }
    }
}
