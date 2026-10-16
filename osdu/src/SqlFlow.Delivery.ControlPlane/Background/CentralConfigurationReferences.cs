using SqlFlow.ControlPlane.Background;
using SqlFlow.Delivery.Catalog;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// The central configuration as the control plane's reference source: the values set for no partition, the control plane's
/// with a repository's own over them, are what every run of every kind is queued carrying (a pre-ingestion or ingestion
/// flow's connections as much as a delivery's platform), so a node needs no environment of its own for what the estate's
/// flows read (osdu/docs/reference/concepts/control-plane.md, The central configuration on every run). The partitions'
/// own values are laid over them for the module's kinds by <see cref="ConfiguredRunDispatcher"/>.
/// </summary>
public sealed class CentralConfigurationReferences : IRunReferenceSource
{
    private readonly DeliveryConfigStore _config;

    public CentralConfigurationReferences(DeliveryConfigStore config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>
    /// The values set for no partition that a run of <paramref name="repoId"/> resolves with; for work of no repository, the
    /// control plane's alone. Empty when the host has no module database, which holds the configuration.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ReferencesAsync(Guid? repoId, CancellationToken ct)
        => (await _config.ConfigurationAsync(repoId, ct).ConfigureAwait(false)).Base;
}
