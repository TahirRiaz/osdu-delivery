using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Whether this control plane has an OSDU partition to work against, reported with the start-up pass at
/// <c>/health/startup</c> (tagged <see cref="SqlFlow.ControlPlane.Background.BootstrapReadiness.Tag"/>): every delivery,
/// cache, retrieval and record read happens in a partition, so OSDU Delivery without one does nothing. Unhealthy until
/// the partitions the configuration names are written (<see cref="CentralConfigurationStep"/>), and after that while the
/// registry holds no partition, naming the setting that gives it one. It is not part of readiness: a control plane
/// waiting for its partitions (a client secret not yet in the vault, say) keeps serving the API, so a person can read
/// why and register one.
/// </summary>
public sealed class PartitionReadiness : IHealthCheck
{
    /// <summary>What an estate with no partition is told, wherever it is reported.</summary>
    public const string NoPartition =
        "OSDU Delivery has no partition to work against. Name at least one in the control plane's configuration: "
        + ConfiguredPartitions.SectionPath + ":<data-partition-id>, with the values its runs resolve under "
        + ConfiguredPartitions.SectionPath + ":<data-partition-id>:Properties (OSDU_URL, OSDU_TOKEN_URL, OSDU_SCOPE, OSDU_CLIENT_ID, OSDU_CLIENT_SECRET as a ${keyvault:...} reference).";

    private readonly IServiceProvider _services;
    private volatile bool _written;

    public PartitionReadiness(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>True once the configured partitions are written into the registry.</summary>
    public bool Written => _written;

    internal void MarkWritten() => _written = true;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_written)
        {
            return HealthCheckResult.Unhealthy(
                $"The partitions named under {ConfiguredPartitions.SectionPath} are not written into the partition registry yet.");
        }

        try
        {
            var registry = await _services.GetRequiredService<DeliveryPartitionRegistry>().ReadAsync(cancellationToken).ConfigureAwait(false);
            return registry.All.Count == 0
                ? HealthCheckResult.Unhealthy(NoPartition)
                : HealthCheckResult.Healthy($"{registry.All.Count} partition(s) registered; the default is '{registry.Default}'.");
        }
        catch (Exception ex) when (ex is DeliveryException or DbException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy($"The partition registry could not be read: {SecretHygiene.RedactedMessage(ex)}");
        }
    }
}
